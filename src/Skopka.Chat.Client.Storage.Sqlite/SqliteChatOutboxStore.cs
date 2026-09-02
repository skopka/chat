using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage.Sqlite;

/// <summary>SQLite encrypted fan-out outbox that preserves exact envelopes across restart.</summary>
/// <remarks>
/// The schema stores ciphertext and a canonical-content hash, not plaintext content or private keys. A host that
/// stores local echoes elsewhere must still protect the event database as documented by <see cref="IChatEventStore"/>.
/// </remarks>
public sealed class SqliteChatOutboxStore : IChatOutboxStore, IDisposable
{
    private const int SchemaVersion = 1;
    private const int BusyTimeoutMilliseconds = 5_000;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;

    /// <summary>Creates a durable outbox over a host-owned file-backed SQLite database.</summary>
    public SqliteChatOutboxStore(string connectionString)
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

        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.Mode == SqliteOpenMode.Memory ||
            builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The durable SQLite outbox requires a file-backed data source.", nameof(connectionString));
        }

        _connectionString = builder.ToString();
    }

    /// <summary>Creates or validates the independent versioned outbox schema.</summary>
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
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS chat_outbox_meta (
                        name TEXT PRIMARY KEY,
                        version INTEGER NOT NULL
                    );
                    INSERT INTO chat_outbox_meta(name, version)
                    VALUES ('schema', 1)
                    ON CONFLICT(name) DO NOTHING;

                    CREATE TABLE IF NOT EXISTS chat_outbox_plans (
                        conversation_id BLOB NOT NULL CHECK(length(conversation_id) = 16),
                        content_id BLOB NOT NULL CHECK(length(content_id) = 16),
                        sender_user_id BLOB NOT NULL CHECK(length(sender_user_id) = 16),
                        sender_device_id BLOB NOT NULL CHECK(length(sender_device_id) = 16),
                        local_echo_message_id BLOB NOT NULL UNIQUE CHECK(length(local_echo_message_id) = 16),
                        sent_at_utc_ticks INTEGER NOT NULL,
                        content_hash BLOB NOT NULL CHECK(length(content_hash) = 32),
                        completed_at_utc_ticks INTEGER NULL,
                        PRIMARY KEY(conversation_id, content_id)
                    );

                    CREATE TABLE IF NOT EXISTS chat_outbox_envelopes (
                        conversation_id BLOB NOT NULL CHECK(length(conversation_id) = 16),
                        content_id BLOB NOT NULL CHECK(length(content_id) = 16),
                        ordinal INTEGER NOT NULL CHECK(ordinal >= 0 AND ordinal < 100),
                        protocol_version INTEGER NOT NULL,
                        message_id BLOB NOT NULL UNIQUE CHECK(length(message_id) = 16),
                        recipient_device_id BLOB NOT NULL CHECK(length(recipient_device_id) = 16),
                        sender_signing_key_id BLOB NOT NULL CHECK(length(sender_signing_key_id) = 16),
                        recipient_encryption_key_id BLOB NOT NULL CHECK(length(recipient_encryption_key_id) = 16),
                        expires_at_utc_ticks INTEGER NULL,
                        ephemeral_public_key BLOB NOT NULL CHECK(length(ephemeral_public_key) = 32),
                        nonce BLOB NOT NULL CHECK(length(nonce) = 24),
                        ciphertext BLOB NOT NULL CHECK(length(ciphertext) <= 65536),
                        authentication_tag BLOB NOT NULL CHECK(length(authentication_tag) = 16),
                        signature BLOB NOT NULL CHECK(length(signature) = 64),
                        accepted_at_utc_ticks INTEGER NULL,
                        PRIMARY KEY(conversation_id, content_id, ordinal),
                        FOREIGN KEY(conversation_id, content_id)
                            REFERENCES chat_outbox_plans(conversation_id, content_id)
                            ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ix_chat_outbox_pending
                        ON chat_outbox_plans(completed_at_utc_ticks, sent_at_utc_ticks, content_id);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var versionCommand = connection.CreateCommand();
                versionCommand.CommandText = "SELECT version FROM chat_outbox_meta WHERE name = 'schema';";
                var version = Convert.ToInt32(
                    await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (version != SchemaVersion)
                {
                    throw new ChatEventStorageException("The local chat outbox database schema is unsupported.");
                }

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
    public async ValueTask<ChatFanOutPlan?> LoadAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIds(conversationId, contentId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, conversationId, contentId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ChatFanOutPlanStoreResult> StoreAsync(
        ChatFanOutPlan plan,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CompletedAt.HasValue || plan.Envelopes.Any(item => item.IsAccepted))
        {
            throw new ArgumentException("A new outbox plan must be pending.", nameof(plan));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var planCommand = connection.CreateCommand();
            planCommand.Transaction = transaction;
            planCommand.CommandText = """
                INSERT INTO chat_outbox_plans (
                    conversation_id, content_id, sender_user_id, sender_device_id,
                    local_echo_message_id, sent_at_utc_ticks, content_hash, completed_at_utc_ticks)
                VALUES ($conversation, $content, $user, $device, $echo, $sentAt, $hash, NULL)
                ON CONFLICT(conversation_id, content_id) DO NOTHING;
                """;
            AddBlob(planCommand, "$conversation", ToBytes(plan.ConversationId.Value));
            AddBlob(planCommand, "$content", ToBytes(plan.ContentId.Value));
            AddBlob(planCommand, "$user", ToBytes(plan.SenderUserId.Value));
            AddBlob(planCommand, "$device", ToBytes(plan.SenderDeviceId.Value));
            AddBlob(planCommand, "$echo", ToBytes(plan.LocalEchoMessageId.Value));
            planCommand.Parameters.AddWithValue("$sentAt", plan.SentAt.UtcTicks);
            AddBlob(planCommand, "$hash", plan.ContentHash.ToArray());
            var inserted = await planCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            if (!inserted)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var existing = await LoadAsync(connection, plan.ConversationId, plan.ContentId, cancellationToken)
                    .ConfigureAwait(false);
                return existing is not null && AreEquivalent(existing, plan)
                    ? ChatFanOutPlanStoreResult.Duplicate
                    : ChatFanOutPlanStoreResult.Conflict;
            }

            for (var index = 0; index < plan.Envelopes.Count; index++)
            {
                await InsertEnvelopeAsync(
                    connection,
                    transaction,
                    plan,
                    index,
                    plan.Envelopes[index].Envelope,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ChatFanOutPlanStoreResult.Stored;
        }
        catch (SqliteException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw StorageFailure(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask MarkAcceptedAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        MessageId messageId,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIds(conversationId, contentId);
        if (messageId.Value == Guid.Empty || acceptedAt == default)
        {
            throw new ArgumentException("The outbox acceptance state is invalid.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE chat_outbox_envelopes
            SET accepted_at_utc_ticks = COALESCE(accepted_at_utc_ticks, $acceptedAt)
            WHERE conversation_id = $conversation AND content_id = $content AND message_id = $message;
            """;
        AddBlob(command, "$conversation", ToBytes(conversationId.Value));
        AddBlob(command, "$content", ToBytes(contentId.Value));
        AddBlob(command, "$message", ToBytes(messageId.Value));
        command.Parameters.AddWithValue("$acceptedAt", acceptedAt.UtcTicks);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ChatEventStorageException("The local chat outbox acceptance target was not found.");
            }
        }
        catch (SqliteException exception)
        {
            throw StorageFailure(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask MarkCompletedAsync(
        ConversationId conversationId,
        ChatContentId contentId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateIds(conversationId, contentId);
        if (completedAt == default)
        {
            throw new ArgumentException("The outbox completion timestamp is required.", nameof(completedAt));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var pendingCommand = connection.CreateCommand();
            pendingCommand.Transaction = transaction;
            pendingCommand.CommandText = """
                SELECT COUNT(*)
                FROM chat_outbox_envelopes
                WHERE conversation_id = $conversation AND content_id = $content
                  AND accepted_at_utc_ticks IS NULL;
                """;
            AddBlob(pendingCommand, "$conversation", ToBytes(conversationId.Value));
            AddBlob(pendingCommand, "$content", ToBytes(contentId.Value));
            var pending = Convert.ToInt32(
                await pendingCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (pending != 0)
            {
                throw new ChatEventStorageException("The local chat outbox plan is incomplete.");
            }

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE chat_outbox_plans
                SET completed_at_utc_ticks = COALESCE(completed_at_utc_ticks, $completedAt)
                WHERE conversation_id = $conversation AND content_id = $content;
                """;
            AddBlob(updateCommand, "$conversation", ToBytes(conversationId.Value));
            AddBlob(updateCommand, "$content", ToBytes(contentId.Value));
            updateCommand.Parameters.AddWithValue("$completedAt", completedAt.UtcTicks);
            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ChatEventStorageException("The local chat outbox plan was not found.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw StorageFailure(exception);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatFanOutPlan> ReadPendingAsync(
        int maximumCount = 50,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumCount is < 1 or > ChatFanOutLimits.MaxRecipientDevices)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<(ConversationId ConversationId, ChatContentId ContentId)>(maximumCount);
        await using (var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT conversation_id, content_id
                FROM chat_outbox_plans
                WHERE completed_at_utc_ticks IS NULL
                ORDER BY sent_at_utc_ticks, content_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", maximumCount);
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    keys.Add((
                        new ConversationId(ReadGuid(reader, 0)),
                        new ChatContentId(ReadGuid(reader, 1))));
                }
            }
            catch (SqliteException exception)
            {
                throw StorageFailure(exception);
            }
        }

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = await LoadAsync(key.ConversationId, key.ContentId, cancellationToken).ConfigureAwait(false);
            if (plan is not null && !plan.CompletedAt.HasValue)
            {
                yield return plan;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteCompletedBeforeAsync(
        DateTimeOffset cutoff,
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cutoff == default)
        {
            throw new ArgumentException("The outbox retention cutoff is required.", nameof(cutoff));
        }

        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM chat_outbox_plans
            WHERE rowid IN (
                SELECT rowid
                FROM chat_outbox_plans
                WHERE completed_at_utc_ticks IS NOT NULL AND completed_at_utc_ticks < $cutoff
                ORDER BY completed_at_utc_ticks, content_id
                LIMIT $limit
            );
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.UtcTicks);
        command.Parameters.AddWithValue("$limit", maximumCount);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw StorageFailure(exception);
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

    private static async Task InsertEnvelopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatFanOutPlan plan,
        int ordinal,
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chat_outbox_envelopes (
                conversation_id, content_id, ordinal, protocol_version, message_id,
                recipient_device_id, sender_signing_key_id, recipient_encryption_key_id,
                expires_at_utc_ticks, ephemeral_public_key, nonce, ciphertext,
                authentication_tag, signature, accepted_at_utc_ticks)
            VALUES (
                $conversation, $content, $ordinal, $version, $message,
                $recipient, $senderKey, $recipientKey, $expiresAt, $ephemeral, $nonce,
                $ciphertext, $tag, $signature, NULL);
            """;
        AddBlob(command, "$conversation", ToBytes(plan.ConversationId.Value));
        AddBlob(command, "$content", ToBytes(plan.ContentId.Value));
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$version", envelope.ProtocolVersion);
        AddBlob(command, "$message", ToBytes(envelope.MessageId.Value));
        AddBlob(command, "$recipient", ToBytes(envelope.RecipientDeviceId.Value));
        AddBlob(command, "$senderKey", ToBytes(envelope.SenderSigningKeyId.Value));
        AddBlob(command, "$recipientKey", ToBytes(envelope.RecipientEncryptionKeyId.Value));
        command.Parameters.AddWithValue(
            "$expiresAt",
            envelope.ExpiresAt.HasValue ? envelope.ExpiresAt.Value.UtcTicks : DBNull.Value);
        AddBlob(command, "$ephemeral", envelope.EphemeralPublicKey.ToArray());
        AddBlob(command, "$nonce", envelope.Nonce.ToArray());
        AddBlob(command, "$ciphertext", envelope.Ciphertext.ToArray());
        AddBlob(command, "$tag", envelope.AuthenticationTag.ToArray());
        AddBlob(command, "$signature", envelope.Signature.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ChatFanOutPlan?> LoadAsync(
        SqliteConnection connection,
        ConversationId conversationId,
        ChatContentId contentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var planCommand = connection.CreateCommand();
            planCommand.CommandText = """
                SELECT sender_user_id, sender_device_id, local_echo_message_id,
                       sent_at_utc_ticks, content_hash, completed_at_utc_ticks
                FROM chat_outbox_plans
                WHERE conversation_id = $conversation AND content_id = $content;
                """;
            AddBlob(planCommand, "$conversation", ToBytes(conversationId.Value));
            AddBlob(planCommand, "$content", ToBytes(contentId.Value));

            UserId senderUserId;
            DeviceId senderDeviceId;
            MessageId localEchoMessageId;
            DateTimeOffset sentAt;
            byte[] contentHash;
            DateTimeOffset? completedAt;
            await using (var reader = await planCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                senderUserId = new UserId(ReadGuid(reader, 0));
                senderDeviceId = new DeviceId(ReadGuid(reader, 1));
                localEchoMessageId = new MessageId(ReadGuid(reader, 2));
                sentAt = ReadTimestamp(reader, 3);
                contentHash = reader.GetFieldValue<byte[]>(4);
                completedAt = reader.IsDBNull(5) ? null : ReadTimestamp(reader, 5);
            }

            var envelopes = new List<ChatEnvelopePlanItem>();
            await using var envelopeCommand = connection.CreateCommand();
            envelopeCommand.CommandText = """
                SELECT protocol_version, message_id, recipient_device_id,
                       sender_signing_key_id, recipient_encryption_key_id,
                       expires_at_utc_ticks, ephemeral_public_key, nonce, ciphertext,
                       authentication_tag, signature, accepted_at_utc_ticks
                FROM chat_outbox_envelopes
                WHERE conversation_id = $conversation AND content_id = $content
                ORDER BY ordinal;
                """;
            AddBlob(envelopeCommand, "$conversation", ToBytes(conversationId.Value));
            AddBlob(envelopeCommand, "$content", ToBytes(contentId.Value));
            await using (var reader = await envelopeCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var envelope = new EncryptedEnvelope(
                        reader.GetInt32(0),
                        new MessageId(ReadGuid(reader, 1)),
                        conversationId,
                        senderDeviceId,
                        new DeviceId(ReadGuid(reader, 2)),
                        new KeyId(ReadGuid(reader, 3)),
                        new KeyId(ReadGuid(reader, 4)),
                        sentAt,
                        reader.IsDBNull(5) ? null : ReadTimestamp(reader, 5),
                        reader.GetFieldValue<byte[]>(6),
                        reader.GetFieldValue<byte[]>(7),
                        reader.GetFieldValue<byte[]>(8),
                        reader.GetFieldValue<byte[]>(9),
                        reader.GetFieldValue<byte[]>(10));
                    ProtocolValidator.Validate(envelope);
                    envelopes.Add(new ChatEnvelopePlanItem(envelope, !reader.IsDBNull(11)));
                }
            }

            var plan = new ChatFanOutPlan(
                conversationId,
                contentId,
                senderUserId,
                senderDeviceId,
                localEchoMessageId,
                sentAt,
                contentHash,
                envelopes,
                completedAt);
            CryptographicOperations.ZeroMemory(contentHash);
            return plan;
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
            throw new ChatEventStorageException("The local chat outbox database is corrupt.", exception);
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

    private static bool AreEquivalent(ChatFanOutPlan left, ChatFanOutPlan right)
    {
        if (left.ConversationId != right.ConversationId || left.ContentId != right.ContentId ||
            left.SenderUserId != right.SenderUserId || left.SenderDeviceId != right.SenderDeviceId ||
            left.LocalEchoMessageId != right.LocalEchoMessageId || left.SentAt != right.SentAt ||
            left.Envelopes.Count != right.Envelopes.Count ||
            !CryptographicOperations.FixedTimeEquals(left.ContentHash.Span, right.ContentHash.Span))
        {
            return false;
        }

        for (var index = 0; index < left.Envelopes.Count; index++)
        {
            var leftBytes = CanonicalEnvelopeEncoding.EncodeEnvelope(left.Envelopes[index].Envelope);
            var rightBytes = CanonicalEnvelopeEncoding.EncodeEnvelope(right.Envelopes[index].Envelope);
            if (!CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateIds(ConversationId conversationId, ChatContentId contentId)
    {
        if (conversationId.Value == Guid.Empty || contentId.Value == Guid.Empty)
        {
            throw new ArgumentException("The outbox identifiers must not be empty.");
        }
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return new DateTimeOffset(reader.GetInt64(ordinal), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ChatEventStorageException("The local chat outbox database is corrupt.", exception);
        }
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetBytes(ordinal, 0, null, 0, 0) != 16)
        {
            throw new ChatEventStorageException("The local chat outbox database is corrupt.");
        }

        var bytes = new byte[16];
        if (reader.GetBytes(ordinal, 0, bytes, 0, bytes.Length) != bytes.Length)
        {
            throw new ChatEventStorageException("The local chat outbox database is corrupt.");
        }

        return new Guid(bytes, bigEndian: true);
    }

    private static byte[] ToBytes(Guid value)
    {
        var bytes = new byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("Could not encode a local chat identifier.");
        }

        return bytes;
    }

    private static void AddBlob(SqliteCommand command, string name, byte[] value) =>
        command.Parameters.Add(name, SqliteType.Blob).Value = value;

    private static ChatEventStorageException StorageFailure(SqliteException exception) =>
        new("The local chat outbox database operation failed.", exception);
}
