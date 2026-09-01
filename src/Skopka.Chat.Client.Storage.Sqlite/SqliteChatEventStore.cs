using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage.Sqlite;

/// <summary>
/// SQLite journal of authenticated decrypted events with atomic delivery-ID idempotency.
/// </summary>
/// <remarks>
/// The content BLOB is canonical plaintext, including attachment keys and metadata. Use an access-controlled,
/// platform-protected database location; SQLite does not provide encryption at rest by itself.
/// </remarks>
public sealed class SqliteChatEventStore : IChatEventStore, IDisposable
{
    private const int SchemaVersion = 1;
    private const int BusyTimeoutMilliseconds = 5_000;
    private const int ReadPageSize = 256;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;

    /// <summary>Creates a local event journal over a host-owned SQLite connection string.</summary>
    public SqliteChatEventStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("The SQLite connection string is invalid.", nameof(connectionString));
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new ArgumentException("The SQLite data source is required.", nameof(connectionString));
        }

        if (builder.Mode == SqliteOpenMode.Memory ||
            builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The durable SQLite event store requires a file-backed data source.", nameof(connectionString));
        }

        _connectionString = builder.ToString();
    }

    /// <summary>Creates or validates the versioned local schema.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(
                    await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (version is not 0 and not SchemaVersion)
                {
                    throw new ChatEventStorageException("The local chat event database schema is unsupported.");
                }

                await using var schemaCommand = connection.CreateCommand();
                schemaCommand.CommandText = """
                    CREATE TABLE IF NOT EXISTS chat_events (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        delivery_message_id BLOB NOT NULL UNIQUE CHECK(length(delivery_message_id) = 16),
                        conversation_id BLOB NOT NULL CHECK(length(conversation_id) = 16),
                        sender_user_id BLOB NOT NULL CHECK(length(sender_user_id) = 16),
                        sender_device_id BLOB NOT NULL CHECK(length(sender_device_id) = 16),
                        sent_at_utc_ticks INTEGER NOT NULL,
                        content_id BLOB NOT NULL CHECK(length(content_id) = 16),
                        content BLOB NOT NULL CHECK(length(content) BETWEEN 1 AND 1048576)
                    );
                    CREATE INDEX IF NOT EXISTS ix_chat_events_conversation_sequence
                        ON chat_events(conversation_id, sequence);
                    PRAGMA user_version = 1;
                    """;
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            catch (SqliteException exception)
            {
                throw StorageFailure(exception);
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ChatEventStoreResult> StoreAsync(
        ReceivedChatContent delivery,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(delivery);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var encoded = ChatContentEncoding.Encode(delivery.Content);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO chat_events (
                    delivery_message_id,
                    conversation_id,
                    sender_user_id,
                    sender_device_id,
                    sent_at_utc_ticks,
                    content_id,
                    content)
                VALUES ($delivery, $conversation, $user, $device, $sentAt, $contentId, $content)
                ON CONFLICT(delivery_message_id) DO NOTHING;
                """;
            AddBlob(command, "$delivery", ToBytes(delivery.DeliveryMessageId.Value));
            AddBlob(command, "$conversation", ToBytes(delivery.ConversationId.Value));
            AddBlob(command, "$user", ToBytes(delivery.SenderUserId.Value));
            AddBlob(command, "$device", ToBytes(delivery.SenderDeviceId.Value));
            command.Parameters.AddWithValue("$sentAt", delivery.SentAt.UtcTicks);
            AddBlob(command, "$contentId", ToBytes(delivery.Content.ContentId.Value));
            AddBlob(command, "$content", encoded);

            try
            {
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    return ChatEventStoreResult.Stored;
                }

                return await CompareExistingAsync(connection, delivery, encoded, cancellationToken).ConfigureAwait(false)
                    ? ChatEventStoreResult.Duplicate
                    : ChatEventStoreResult.Conflict;
            }
            catch (SqliteException exception)
            {
                throw StorageFailure(exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReceivedChatContent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await foreach (var delivery in ReadAsync(null, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delivery;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReceivedChatContent> ReadConversationAsync(
        ConversationId conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        await foreach (var delivery in ReadAsync(conversationId, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delivery;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initializationGate.Dispose();
    }

    private async IAsyncEnumerable<ReceivedChatContent> ReadAsync(
        ConversationId? conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var afterSequence = 0L;
        while (true)
        {
            var page = await ReadPageAsync(conversationId, afterSequence, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                yield break;
            }

            foreach (var row in page)
            {
                afterSequence = row.Sequence;
                yield return row.Delivery;
            }
        }
    }

    private async Task<IReadOnlyList<StoredRow>> ReadPageAsync(
        ConversationId? conversationId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = conversationId.HasValue
            ? """
                SELECT sequence, delivery_message_id, conversation_id, sender_user_id, sender_device_id,
                       sent_at_utc_ticks, content_id, content, length(content)
                FROM chat_events
                WHERE conversation_id = $conversation AND sequence > $after
                ORDER BY sequence
                LIMIT $limit;
                """
            : """
                SELECT sequence, delivery_message_id, conversation_id, sender_user_id, sender_device_id,
                       sent_at_utc_ticks, content_id, content, length(content)
                FROM chat_events
                WHERE sequence > $after
                ORDER BY sequence
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$limit", ReadPageSize);
        if (conversationId is { } id)
        {
            AddBlob(command, "$conversation", ToBytes(id.Value));
        }

        try
        {
            var result = new List<StoredRow>(ReadPageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt64(8) is < 1 or > ProtocolLimits.MaxPlaintextBytes)
                {
                    throw new ChatEventStorageException("The local chat event database is corrupt.");
                }

                var encoded = reader.GetFieldValue<byte[]>(7);
                try
                {
                    var content = ChatContentEncoding.Decode(encoded);
                    var storedContentId = ReadGuid(reader, 6);
                    if (content.ContentId.Value != storedContentId)
                    {
                        throw new ChatEventStorageException("The local chat event database is corrupt.");
                    }

                    result.Add(new StoredRow(
                        reader.GetInt64(0),
                        new ReceivedChatContent(
                            new MessageId(ReadGuid(reader, 1)),
                            new ConversationId(ReadGuid(reader, 2)),
                            new UserId(ReadGuid(reader, 3)),
                            new DeviceId(ReadGuid(reader, 4)),
                            new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                            content)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }

            return result;
        }
        catch (ChatEventStorageException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw StorageFailure(exception);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException)
        {
            throw new ChatEventStorageException("The local chat event database is corrupt.", exception);
        }
    }

    private static async Task<bool> CompareExistingAsync(
        SqliteConnection connection,
        ReceivedChatContent delivery,
        byte[] encoded,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, sender_user_id, sender_device_id, sent_at_utc_ticks,
                   content, length(content)
            FROM chat_events
            WHERE delivery_message_id = $delivery;
            """;
        AddBlob(command, "$delivery", ToBytes(delivery.DeliveryMessageId.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ChatEventStorageException("The local chat event database changed during an atomic write.");
        }

        if (reader.GetInt64(5) is < 1 or > ProtocolLimits.MaxPlaintextBytes)
        {
            throw new ChatEventStorageException("The local chat event database is corrupt.");
        }

        var existingContent = reader.GetFieldValue<byte[]>(4);
        try
        {
            return ReadGuid(reader, 0) == delivery.ConversationId.Value &&
                ReadGuid(reader, 1) == delivery.SenderUserId.Value &&
                ReadGuid(reader, 2) == delivery.SenderDeviceId.Value &&
                reader.GetInt64(3) == delivery.SentAt.UtcTicks &&
                CryptographicOperations.FixedTimeEquals(existingContent, encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(existingContent);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw StorageFailure(exception);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddBlob(SqliteCommand command, string name, byte[] value) =>
        command.Parameters.Add(name, SqliteType.Blob).Value = value;

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetBytes(ordinal, 0, null, 0, 0) != 16)
        {
            throw new ChatEventStorageException("The local chat event database is corrupt.");
        }

        var bytes = new byte[16];
        if (reader.GetBytes(ordinal, 0, bytes, 0, bytes.Length) != bytes.Length)
        {
            throw new ChatEventStorageException("The local chat event database is corrupt.");
        }

        return new Guid(bytes, bigEndian: true);
    }

    private static byte[] ToBytes(Guid value)
    {
        var bytes = new byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != bytes.Length)
        {
            throw new InvalidOperationException("Could not encode a local chat identifier.");
        }

        return bytes;
    }

    private static ChatEventStorageException StorageFailure(SqliteException exception) =>
        new("The local chat event database operation failed.", exception);

    private sealed record StoredRow(long Sequence, ReceivedChatContent Delivery);
}
