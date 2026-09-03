using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Persistence.PostgreSql;

/// <summary>Independent migration context for opaque account backups, unrelated to message delivery TTL.</summary>
/// <remarks>Use the separate __SkopkaChatBackupMigrations history table, as configured by PostgreSqlBackupStorage.</remarks>
public sealed class ChatBackupDbContext(DbContextOptions<ChatBackupDbContext> options) : DbContext(options);

/// <summary>PostgreSQL transactional opaque backup store. Serializes each account across processes with transaction-scoped advisory locks.</summary>
public sealed class PostgreSqlBackupStorage : IChatBackupStorage
{
    private readonly DbContextOptions<ChatBackupDbContext> _options;
    /// <summary>Uses a host-owned connection string. Never enable sensitive SQL logging; backups require independently configured retention and backups.</summary>
    public PostgreSqlBackupStorage(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _options = new DbContextOptionsBuilder<ChatBackupDbContext>().UseNpgsql(connectionString,
            options => options.MigrationsHistoryTable("__SkopkaChatBackupMigrations")).Options;
    }
    /// <summary>Applies the independent append-only backup migration. Run explicitly during host deployment, not on each request.</summary>
    public async ValueTask MigrateAsync(CancellationToken cancellationToken = default)
    {
        try { await using var context = new ChatBackupDbContext(_options); await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbException) { throw new ChatBackupException(ChatBackupFailure.Unavailable); }
    }
    /// <inheritdoc />
    public async ValueTask<IChatBackupTransaction> BeginAsync(ChatBackupScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); var context = new ChatBackupDbContext(_options);
        try
        {
            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // A collision only serializes unrelated accounts. SQL predicates still use exact service/account, never this lock hash.
            var bytes = Encoding.UTF8.GetBytes("Skopka.Chat.Backup.Lock.v1\0" + scope.ServiceId + "\0" + scope.UserId.Value.ToString("N"));
            var lockId = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(bytes));
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [lockId], cancellationToken).ConfigureAwait(false);
            return new Transaction(context, transaction, scope);
        }
        catch { await context.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private sealed class Transaction(ChatBackupDbContext context, IDbContextTransaction transaction, ChatBackupScope scope) : IChatBackupTransaction
    {
        private bool _completed;
        public async ValueTask<byte[]?> ReadAsync(string group, string key, CancellationToken cancellationToken)
        {
            await using var command = Command(group, key);
            command.CommandText = "SELECT data FROM chat_backup_records WHERE service_id=@service AND user_id=@user AND group_name=@group AND record_key=@key";
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        }
        public async ValueTask WriteAsync(string group, string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.Length > ChatBackupLimits.MaxPartBytes) { throw new ChatBackupFormatException(); }
            await using var command = Command(group, key);
            command.CommandText = """
                INSERT INTO chat_backup_records(service_id,user_id,group_name,record_key,data) VALUES(@service,@user,@group,@key,@data)
                ON CONFLICT(service_id,user_id,group_name,record_key) DO UPDATE SET data=excluded.data
                """;
            Parameter(command, "data", data.ToArray()); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask<IReadOnlyList<string>> ListAsync(string group, string? after, int count, CancellationToken cancellationToken)
        {
            if (count is < 1 or > ChatBackupLimits.MaxPageSize) { throw new ChatBackupFormatException(); }
            await using var command = Command(group, after ?? "");
            command.CommandText = "SELECT record_key FROM chat_backup_records WHERE service_id=@service AND user_id=@user AND group_name=@group AND record_key>@key ORDER BY record_key LIMIT @count";
            Parameter(command, "count", count); var result = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { result.Add(reader.GetString(0)); }
            return result;
        }
        public async ValueTask DeleteAsync(string group, string key, CancellationToken cancellationToken)
        {
            await using var command = Command(group, key);
            command.CommandText = "DELETE FROM chat_backup_records WHERE service_id=@service AND user_id=@user AND group_name=@group AND record_key=@key";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        public async ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Wait for the actual commit outcome before releasing the account lease; callers recover ambiguous network failures by exact version ID.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false); _completed = true;
        }
        public async ValueTask DisposeAsync()
        { try { await transaction.DisposeAsync().ConfigureAwait(false); } finally { await context.DisposeAsync().ConfigureAwait(false); } }
        private DbCommand Command(string group, string key)
        {
            if (_completed || group.Length is < 1 or > 40 || key.Length > 40 || group.Any(c => !char.IsAsciiLetterOrDigit(c)) || key.Any(c => !char.IsAsciiLetterOrDigit(c))) { throw new ChatBackupFormatException(); }
            var command = context.Database.GetDbConnection().CreateCommand(); command.Transaction = transaction.GetDbTransaction(); command.CommandTimeout = 30;
            Parameter(command, "service", scope.ServiceId); Parameter(command, "user", scope.UserId.Value); Parameter(command, "group", group); Parameter(command, "key", key); return command;
        }
        private static void Parameter(DbCommand command, string name, object value)
        { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
    }
}

// This context deliberately manages its opaque table through SQL migrations, without mapping it into the envelope EF model.
[DbContext(typeof(ChatBackupDbContext))]
[Migration("202609030001_EncryptedHistoryBackups")]
internal sealed class EncryptedHistoryBackups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE chat_backup_records (
          service_id varchar(256) COLLATE "C" NOT NULL,
          user_id uuid NOT NULL,
          group_name varchar(40) COLLATE "C" NOT NULL,
          record_key varchar(40) COLLATE "C" NOT NULL,
          data bytea NOT NULL CHECK(octet_length(data)<=66000),
          PRIMARY KEY(service_id,user_id,group_name,record_key)
        );
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE chat_backup_records;");
}

[DbContext(typeof(ChatBackupDbContext))]
internal sealed class ChatBackupModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) { modelBuilder.HasAnnotation("ProductVersion", "10.0.0"); }
}
