using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots.Sqlite;

/// <summary>
/// File-backed plaintext inbox for one bot/device/disclosure. Protect the file, WAL, backups and disk
/// with host-owned permissions/encryption/quotas. Tombstones must survive all supported retry windows.
/// </summary>
public sealed class SqliteChatBotInbox : IChatBotInbox
{
    private readonly string _connectionString;
    private readonly string _scope;

    /// <summary>Creates a store; existing files cannot be silently reused for another identity/disclosure.</summary>
    public SqliteChatBotInbox(string connectionString, ChatBotProfile profile, DeviceId deviceId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (deviceId.Value == Guid.Empty) { throw new ArgumentException("The bot device is invalid.", nameof(deviceId)); }
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.Mode == SqliteOpenMode.Memory ||
                builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A file-backed bot database is required.");
            }
            builder.DefaultTimeout = 5;
            _connectionString = builder.ToString();
        }
        catch (ArgumentException) { throw new ArgumentException("The bot database configuration is invalid.", nameof(connectionString)); }
        _scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{profile.BotUserId}\0{deviceId}\0{profile.Revision:D}\0{profile.OperatorId}\0{profile.OperatorName}\0{profile.Name}\0{profile.Hosting}")));
    }

    /// <inheritdoc />
    public async ValueTask<ChatBotStoreResult> StoreAsync(ReceivedChatContent delivery, Guid? grantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (grantId == Guid.Empty) { throw new ArgumentException("The grant is invalid.", nameof(grantId)); }
        var contentBytes = ChatContentEncoding.Encode(delivery.Content);
        try
        {
            var contentHash = SHA256.HashData(contentBytes);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData(Encoding.ASCII.GetBytes($"{delivery.DeliveryMessageId}/{delivery.ConversationId}/{delivery.SenderUserId}/{delivery.SenderDeviceId}/{delivery.SentAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}"));
            digest.AppendData(contentBytes);
            var deliveryHash = digest.GetHashAndReset();
            return await WithDatabaseAsync(async (connection, transaction) =>
            {
                using var previous = Command(connection, transaction, "SELECT hash FROM deliveries WHERE id = $id;", ("$id", delivery.DeliveryMessageId.ToString()));
                if (await previous.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] storedDelivery)
                {
                    return Equal(storedDelivery, deliveryHash) ? ChatBotStoreResult.Duplicate : ChatBotStoreResult.Conflict;
                }
                using var logical = Command(connection, transaction,
                    "SELECT hash FROM updates WHERE conversation = $conversation AND sender = $sender AND content_id = $content;",
                    ("$conversation", delivery.ConversationId.ToString()), ("$sender", delivery.SenderUserId.ToString()), ("$content", delivery.Content.ContentId.ToString()));
                var storedContent = await logical.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
                if (storedContent is not null && !Equal(storedContent, contentHash)) { return ChatBotStoreResult.Conflict; }
                if (storedContent is null)
                {
                    var text = delivery.Content as ChatTextContent;
                    var active = grantId is not null && text is not null && !string.IsNullOrWhiteSpace(text.Text) &&
                        Encoding.UTF8.GetByteCount(text.Text) <= ChatBotLimits.MaxTextUtf8Bytes;
                    using var insert = Command(connection, transaction, """
                        INSERT INTO updates(conversation, sender, content_id, hash, grant_id, text, reply_id, forwarded, completed)
                        VALUES ($conversation, $sender, $content, $hash, $grant, $text, $reply, $forwarded, $completed);
                        """, ("$conversation", delivery.ConversationId.ToString()), ("$sender", delivery.SenderUserId.ToString()),
                        ("$content", delivery.Content.ContentId.ToString()), ("$hash", contentHash),
                        ("$grant", active ? grantId!.Value.ToString("D") : null), ("$text", active ? text!.Text : null),
                        ("$reply", active ? text!.ReplyToContentId?.ToString() : null), ("$forwarded", active && text!.IsForwarded ? 1 : 0),
                        ("$completed", active ? 0 : 1));
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                using var record = Command(connection, transaction, "INSERT INTO deliveries(id, hash) VALUES ($id, $hash);",
                    ("$id", delivery.DeliveryMessageId.ToString()), ("$hash", deliveryHash));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return storedContent is null ? ChatBotStoreResult.Stored : ChatBotStoreResult.Duplicate;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(contentBytes); }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ChatBotUpdate>> ReadAsync(long after, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (after < 0 || maximumCount is < 1 or > ChatBotLimits.MaxUpdates) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        return WithDatabaseAsync<IReadOnlyList<ChatBotUpdate>>(async (connection, transaction) =>
        {
            using var command = Command(connection, transaction, """
                SELECT sequence, grant_id, conversation, sender, content_id, text, reply_id, forwarded
                FROM updates WHERE completed = 0 AND sequence > $after ORDER BY sequence LIMIT $count;
                """, ("$after", after), ("$count", maximumCount));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<ChatBotUpdate>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var text = reader.GetString(5);
                if (Encoding.UTF8.GetByteCount(text) > ChatBotLimits.MaxTextUtf8Bytes) { throw new ChatBotException(); }
                result.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), new(Guid.Parse(reader.GetString(2))),
                    new(Guid.Parse(reader.GetString(3))), new(Guid.Parse(reader.GetString(4))), text,
                    reader.IsDBNull(6) ? null : new ChatContentId(Guid.Parse(reader.GetString(6))), reader.GetBoolean(7)));
            }
            return result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask AcknowledgeAsync(long updateId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(updateId);
        _ = await WithDatabaseAsync(async (connection, transaction) =>
        {
            using var command = Command(connection, transaction,
                "UPDATE updates SET completed = 1, text = NULL, reply_id = NULL WHERE sequence = $id;", ("$id", updateId));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ChatBotStoreResult> ReserveSendAsync(ConversationId conversationId, Guid grantId,
        ChatTextContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (conversationId.Value == Guid.Empty || grantId == Guid.Empty) { throw new ArgumentException("The send scope is invalid."); }
        var bytes = ChatContentEncoding.Encode(content);
        byte[] hash;
        try { hash = SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return await WithDatabaseAsync(async (connection, transaction) =>
        {
            using var insert = Command(connection, transaction, """
                INSERT INTO sends(request_id, conversation, grant_id, hash) VALUES ($id, $conversation, $grant, $hash)
                ON CONFLICT(request_id) DO NOTHING;
                """, ("$id", content.ContentId.ToString()), ("$conversation", conversationId.ToString()), ("$grant", grantId.ToString("D")), ("$hash", hash));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) { return ChatBotStoreResult.Stored; }
            using var select = Command(connection, transaction,
                "SELECT hash FROM sends WHERE request_id = $id AND conversation = $conversation AND grant_id = $grant;",
                ("$id", content.ContentId.ToString()), ("$conversation", conversationId.ToString()), ("$grant", grantId.ToString("D")));
            return await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] previous && Equal(previous, hash)
                ? ChatBotStoreResult.Duplicate : ChatBotStoreResult.Conflict;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> WithDatabaseAsync<T>(Func<SqliteConnection, SqliteTransaction, ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Immediate transaction serializes independent writers, including schema/scope initialization.
            using var transaction = connection.BeginTransaction(deferred: false);
            using var version = Command(connection, transaction, "PRAGMA user_version;");
            var schema = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (schema is not 0 and not 1) { throw new ChatBotException(); }
            using var initialize = Command(connection, transaction, """
                CREATE TABLE IF NOT EXISTS bot_scope(id INTEGER PRIMARY KEY CHECK(id = 1), scope TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS deliveries(id TEXT PRIMARY KEY, hash BLOB NOT NULL CHECK(length(hash) = 32));
                CREATE TABLE IF NOT EXISTS updates(
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, conversation TEXT NOT NULL, sender TEXT NOT NULL,
                    content_id TEXT NOT NULL, hash BLOB NOT NULL CHECK(length(hash) = 32), grant_id TEXT,
                    text TEXT CHECK(text IS NULL OR length(CAST(text AS BLOB)) <= 16384), reply_id TEXT,
                    forwarded INTEGER NOT NULL, completed INTEGER NOT NULL,
                    UNIQUE(conversation, sender, content_id));
                CREATE INDEX IF NOT EXISTS ix_updates_pending ON updates(completed, sequence);
                CREATE TABLE IF NOT EXISTS sends(request_id TEXT PRIMARY KEY, conversation TEXT NOT NULL,
                    grant_id TEXT NOT NULL, hash BLOB NOT NULL CHECK(length(hash) = 32));
                INSERT INTO bot_scope(id, scope) VALUES (1, $scope) ON CONFLICT(id) DO NOTHING;
                PRAGMA user_version = 1;
                """, ("$scope", _scope));
            await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            using var scope = Command(connection, transaction, "SELECT scope FROM bot_scope WHERE id = 1;");
            if (await scope.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string != _scope) { throw new ChatBotException(); }
            var result = await operation(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is SqliteException or FormatException or InvalidCastException or OverflowException)
        {
            throw new ChatBotException();
        }
    }

    private static bool Equal(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) { command.Parameters.AddWithValue(name, value ?? DBNull.Value); }
        return command;
    }
}
