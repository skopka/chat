using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage.Sqlite;

/// <summary>Separate durable backup/staging database. Restored history is plaintext: the host must protect the directory and database.</summary>
/// <remarks>All operations require an acquired cooperative cross-process lease. This never stores recovery or device private keys.</remarks>
public sealed class SqliteBackupWorkspace : IChatBackupWorkspace
{
    private readonly string _connectionString;
    private readonly string _lockPath;
    private readonly long _maximumBytes;
    private bool _closed;
    private bool _leased;
    /// <summary>Creates a scoped handle over a dedicated file-backed database in an existing protected directory.</summary>
    public SqliteBackupWorkspace(DeviceIdentityScope scope, string connectionString, long maximumBytes = 4L << 30)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.Mode == SqliteOpenMode.Memory || builder.DataSource.Contains(':', StringComparison.Ordinal) && !Path.IsPathFullyQualified(builder.DataSource))
            { throw new ArgumentException("A file-backed data source is required."); }
            builder.DataSource = Path.GetFullPath(builder.DataSource); builder.DefaultTimeout = 10;
            _connectionString = builder.ToString(); _lockPath = builder.DataSource + ".backup.lock";
        }
        catch (ArgumentException) { throw new ArgumentException("A dedicated file-backed backup database is required.", nameof(connectionString)); }
        if (maximumBytes < ChatBackupLimits.MaxPartBytes || maximumBytes > 128L << 30) { throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
        _maximumBytes = maximumBytes;
    }
    /// <inheritdoc />
    public DeviceIdentityScope Scope { get; }
    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) { throw new ChatBackupException(ChatBackupFailure.Locked); }
        var started = System.Diagnostics.Stopwatch.StartNew(); FileStream stream;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
        }
        try
        {
            _leased = true;
            await WithAsync(async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS backup_schema(version INTEGER NOT NULL CHECK(version=1));
                    INSERT INTO backup_schema(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM backup_schema);
                    CREATE TABLE IF NOT EXISTS backup_records(
                      sequence INTEGER PRIMARY KEY AUTOINCREMENT, scope TEXT NOT NULL, group_name TEXT NOT NULL,
                      record_key TEXT NOT NULL, data BLOB NOT NULL CHECK(length(data)<=66000), UNIQUE(scope,group_name,record_key));
                    CREATE INDEX IF NOT EXISTS ix_backup_records_page ON backup_records(scope,group_name,sequence);
                    SELECT version FROM backup_schema;
                    """;
                var version = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (Convert.ToInt32(version, CultureInfo.InvariantCulture) != 1) { throw new ChatBackupFormatException(); }
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return new Lease(this, stream);
        }
        catch { _leased = false; await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }
    /// <inheritdoc />
    public ValueTask<byte[]?> ReadAsync(string group, string key, CancellationToken cancellationToken = default)
    {
        ChatBackupLocalValidation.Validate(group, key);
        return WithAsync(async (connection, token) =>
        {
            await using var command = Command(connection, group, key);
            command.CommandText = "SELECT data FROM backup_records WHERE scope=$scope AND group_name=$group AND record_key=$key;";
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as byte[];
        }, cancellationToken);
    }
    /// <inheritdoc />
    public async ValueTask<bool> WriteAsync(string group, string key, ReadOnlyMemory<byte> data, bool replace = false, CancellationToken cancellationToken = default)
    {
        ChatBackupLocalValidation.Validate(group, key, data.Length);
        var old = await ReadAsync(group, key, cancellationToken).ConfigureAwait(false);
        try
        {
            if (old is not null && !replace)
            { if (!old.AsSpan().SequenceEqual(data.Span)) { throw new ChatBackupException(ChatBackupFailure.Conflict); } return false; }
            return await WithAsync(async (connection, token) =>
            {
                await using var command = Command(connection, group, key);
                command.CommandText = "SELECT COALESCE(SUM(length(data)),0) FROM backup_records;";
                var total = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (total - (old?.Length ?? 0) + data.Length > _maximumBytes) { throw new ChatBackupException(ChatBackupFailure.Quota); }
                command.CommandText = "INSERT INTO backup_records(scope,group_name,record_key,data) VALUES($scope,$group,$key,$data) ON CONFLICT(scope,group_name,record_key) DO UPDATE SET data=excluded.data;";
                var copy = data.ToArray();
                try { command.Parameters.AddWithValue("$data", copy); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(copy); }
                return old is null;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { if (old is not null) { CryptographicOperations.ZeroMemory(old); } }
    }
    /// <inheritdoc />
    public ValueTask<ChatBackupLocalPage> ReadPageAsync(string group, string? cursor = null, int maximumCount = 50, CancellationToken cancellationToken = default)
    {
        ChatBackupLocalValidation.Validate(group, "page"); long after = 0;
        if (maximumCount is < 1 or > ChatBackupLimits.MaxPageSize || (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))) { throw new ChatBackupFormatException(); }
        return WithAsync(async (connection, token) =>
        {
            await using var command = Command(connection, group, "page");
            command.CommandText = "SELECT sequence,record_key FROM backup_records WHERE scope=$scope AND group_name=$group AND sequence>$after ORDER BY sequence LIMIT $count;";
            command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$count", maximumCount);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var keys = new List<string>(); long last = after;
            while (await reader.ReadAsync(token).ConfigureAwait(false)) { last = reader.GetInt64(0); keys.Add(reader.GetString(1)); }
            return new ChatBackupLocalPage(keys, keys.Count == maximumCount ? last.ToString(CultureInfo.InvariantCulture) : null);
        }, cancellationToken);
    }
    /// <inheritdoc />
    public async ValueTask DeleteAsync(string group, string key, CancellationToken cancellationToken = default)
    {
        ChatBackupLocalValidation.Validate(group, key);
        await WithAsync(async (connection, token) =>
        {
            await using var command = Command(connection, group, key);
            command.CommandText = "DELETE FROM backup_records WHERE scope=$scope AND group_name=$group AND record_key=$key;";
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
    private SqliteCommand Command(SqliteConnection connection, string group, string key)
    {
        var command = connection.CreateCommand(); command.Parameters.AddWithValue("$scope", Scope.StoragePartition);
        command.Parameters.AddWithValue("$group", group); command.Parameters.AddWithValue("$key", key); return command;
    }
    private async ValueTask<T> WithAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> action, CancellationToken token)
    {
        if (_closed) { throw new ChatBackupException(ChatBackupFailure.Locked); }
        if (!_leased) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
        try { await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token).ConfigureAwait(false); return await action(connection, token).ConfigureAwait(false); }
        catch (SqliteException) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
    }
    private sealed class Lease(SqliteBackupWorkspace owner, FileStream stream) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { owner._leased = false; await stream.DisposeAsync().ConfigureAwait(false); }
    }
}
