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

    /// <summary>Lists active devices owned by either participant in stable order.</summary>
    /// <remarks>
    /// The server engine authorizes conversation membership before calling this method. The default keeps
    /// existing repository implementations binary-compatible while making the new directory capability explicit.
    /// </remarks>
    ValueTask<DeviceDirectoryPage> ListActiveForParticipantsAsync(
        UserId firstUserId,
        UserId secondUserId,
        DeviceDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The device directory repository capability is not configured.");

    /// <summary>Lists active devices for an authorized bounded participant set.</summary>
    ValueTask<DeviceDirectoryPage> ListActiveForUsersAsync(
        IReadOnlyCollection<UserId> userIds,
        DeviceDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 2)
        {
            var pair = userIds.ToArray();
            return ListActiveForParticipantsAsync(pair[0], pair[1], cursor, maximumCount, cancellationToken);
        }

        throw new NotSupportedException("The group device directory repository capability is not configured.");
    }
}

/// <summary>Personal-conversation persistence boundary.</summary>
public interface IConversationRepository
{
    /// <summary>Adds a conversation if its ID does not exist.</summary>
    ValueTask<bool> TryAddAsync(PersonalConversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Gets a conversation by ID.</summary>
    ValueTask<PersonalConversation?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>Gets the unique conversation for an unordered participant pair.</summary>
    ValueTask<PersonalConversation?> GetByParticipantsAsync(
        UserId firstUserId,
        UserId secondUserId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The conversation directory repository capability is not configured.");

    /// <summary>Lists only conversations containing the authenticated user in stable order.</summary>
    ValueTask<ConversationDirectoryPage> ListForUserAsync(
        UserId userId,
        ConversationDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The conversation directory repository capability is not configured.");
}

/// <summary>Encrypted-envelope and acknowledgement persistence boundary.</summary>
public interface IEnvelopeRepository
{
    /// <summary>Atomically inserts by message ID and compares canonical bytes on retry.</summary>
    ValueTask<EnvelopeStoreResult> TryAddAsync(
        EncryptedEnvelope envelope,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Gets an undelivered, unexpired batch ordered by acceptance time and message ID.</summary>
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
