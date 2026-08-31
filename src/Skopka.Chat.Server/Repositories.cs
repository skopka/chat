using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Public device-directory persistence boundary.</summary>
public interface IDeviceRepository
{
    /// <summary>Adds a device if its ID does not exist.</summary>
    ValueTask<bool> TryAddAsync(PublicDevice device, CancellationToken cancellationToken = default);

    /// <summary>Gets current public data for a device.</summary>
    ValueTask<PublicDevice?> GetAsync(DeviceId deviceId, CancellationToken cancellationToken = default);

    /// <summary>Marks a device revoked without deleting its public audit data.</summary>
    ValueTask<bool> RevokeAsync(DeviceId deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);
}

/// <summary>Personal-conversation persistence boundary.</summary>
public interface IConversationRepository
{
    /// <summary>Adds a conversation if its ID does not exist.</summary>
    ValueTask<bool> TryAddAsync(PersonalConversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Gets a conversation by ID.</summary>
    ValueTask<PersonalConversation?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken = default);
}

/// <summary>Encrypted-envelope and acknowledgement persistence boundary.</summary>
public interface IEnvelopeRepository
{
    /// <summary>Atomically inserts by message ID and compares canonical bytes on retry.</summary>
    ValueTask<EnvelopeStoreResult> TryAddAsync(
        EncryptedEnvelope envelope,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Gets an undelivered, unexpired batch for one active device.</summary>
    ValueTask<IReadOnlyList<StoredEnvelope>> GetPendingAsync(
        DeviceId recipientDeviceId,
        int maximumCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically records the first acknowledgement for the addressed device.</summary>
    ValueTask<bool> AcknowledgeAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes data whose explicit retention deadline has elapsed.</summary>
    ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
