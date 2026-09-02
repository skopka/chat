using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Transport-neutral server engine with no reference to client cryptography.</summary>
public sealed class ChatServerEngine
{
    private readonly IDeviceRepository _devices;
    private readonly IConversationRepository _conversations;
    private readonly IEnvelopeRepository _envelopes;

    /// <summary>Creates an engine over independent server stores.</summary>
    public ChatServerEngine(
        IDeviceRepository devices,
        IConversationRepository conversations,
        IEnvelopeRepository envelopes)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
    }

    /// <summary>Registers immutable public keys for a device.</summary>
    public async ValueTask<DeviceRegistrationResult> RegisterDeviceAsync(
        PublicDevice device,
        CancellationToken cancellationToken = default)
    {
        ProtocolValidator.Validate(device);
        if (await _devices.TryAddAsync(device, cancellationToken).ConfigureAwait(false))
        {
            return DeviceRegistrationResult.Registered;
        }

        var existing = await _devices.GetAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        if (existing is null || !SamePublicDevice(existing, device))
        {
            throw new ChatServerException("The device ID is already bound to different public data.");
        }

        return DeviceRegistrationResult.Duplicate;
    }

    /// <summary>Revokes a device so it cannot send or receive new server deliveries.</summary>
    public ValueTask<bool> RevokeDeviceAsync(
        DeviceId deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default) =>
        _devices.RevokeAsync(deviceId, revokedAt, cancellationToken);

    /// <summary>Creates a personal conversation after validating both participants.</summary>
    public async ValueTask<PersonalConversation> CreateConversationAsync(
        UserId firstUserId,
        UserId secondUserId,
        ConversationId conversationId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        if (firstUserId.Value == Guid.Empty || secondUserId.Value == Guid.Empty ||
            conversationId.Value == Guid.Empty || firstUserId == secondUserId || createdAt == default)
        {
            throw new ArgumentException("Personal conversation data is invalid.");
        }

        var existingPair = await _conversations.GetByParticipantsAsync(
            firstUserId,
            secondUserId,
            cancellationToken).ConfigureAwait(false);
        if (existingPair is not null)
        {
            if (existingPair.ConversationId != conversationId)
            {
                throw new ChatServerException("The participant pair already belongs to another conversation.");
            }

            return existingPair;
        }

        var conversation = PersonalConversation.CreateCanonical(
            conversationId,
            firstUserId,
            secondUserId,
            createdAt);
        if (!await _conversations.TryAddAsync(conversation, cancellationToken).ConfigureAwait(false))
        {
            var existing = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (existing != conversation)
            {
                throw new ChatServerException("The conversation ID is already bound to different participants.");
            }

            return existing;
        }

        return conversation;
    }

    /// <summary>Gets or atomically creates the unique personal conversation for a peer.</summary>
    public async ValueTask<PersonalConversation> GetOrCreateConversationAsync(
        UserId authenticatedUserId,
        UserId peerUserId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ValidateConversationParticipants(authenticatedUserId, peerUserId, createdAt);
        var existing = await _conversations.GetByParticipantsAsync(
            authenticatedUserId,
            peerUserId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var candidate = PersonalConversation.CreateCanonical(
            ConversationId.New(),
            authenticatedUserId,
            peerUserId,
            createdAt);
        if (await _conversations.TryAddAsync(candidate, cancellationToken).ConfigureAwait(false))
        {
            return candidate;
        }

        return await _conversations.GetByParticipantsAsync(
            authenticatedUserId,
            peerUserId,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChatServerException("The personal conversation could not be created.");
    }

    /// <summary>Lists a bounded page of the authenticated user's conversation metadata.</summary>
    public ValueTask<ConversationDirectoryPage> ListConversationsAsync(
        UserId authenticatedUserId,
        ConversationDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ValidateDirectoryRequest(authenticatedUserId.Value, maximumCount);
        return _conversations.ListForUserAsync(authenticatedUserId, cursor, maximumCount, cancellationToken);
    }

    /// <summary>Lists active devices for both participants after authorizing conversation membership.</summary>
    public async ValueTask<DeviceDirectoryPage> ListConversationDevicesAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        DeviceDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ValidateDirectoryRequest(authenticatedUserId.Value, maximumCount);
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false) ??
            throw new ChatServerException("Conversation was not found.");
        if (!conversation.Contains(authenticatedUserId))
        {
            throw new ChatServerException("The caller is not a conversation participant.");
        }

        return await _devices.ListActiveForParticipantsAsync(
            conversation.FirstUserId,
            conversation.SecondUserId,
            cursor,
            maximumCount,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates routing and lifecycle state, then stores ciphertext idempotently.</summary>
    public async ValueTask<SubmitEnvelopeResult> SubmitAsync(
        EncryptedEnvelope envelope,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default)
    {
        ProtocolValidator.Validate(envelope);
        var sender = await RequireActiveDeviceAsync(envelope.SenderDeviceId, cancellationToken).ConfigureAwait(false);
        var recipient = await RequireActiveDeviceAsync(envelope.RecipientDeviceId, cancellationToken).ConfigureAwait(false);
        if (sender.KeyId != envelope.SenderSigningKeyId || recipient.KeyId != envelope.RecipientEncryptionKeyId)
        {
            throw new ChatServerException("Envelope key identifiers are stale or invalid.");
        }

        var conversation = await _conversations.GetAsync(envelope.ConversationId, cancellationToken).ConfigureAwait(false) ??
            throw new ChatServerException("Conversation was not found.");
        if (!conversation.Contains(sender.UserId) || !conversation.Contains(recipient.UserId))
        {
            throw new ChatServerException("Envelope devices are not valid conversation participants.");
        }

        return await _envelopes.TryAddAsync(envelope, acceptedAt, cancellationToken).ConfigureAwait(false) switch
        {
            EnvelopeStoreResult.Inserted => SubmitEnvelopeResult.Accepted,
            EnvelopeStoreResult.Duplicate => SubmitEnvelopeResult.Duplicate,
            _ => throw new ChatServerException("Message ID reuse with different envelope data was rejected.")
        };
    }

    /// <summary>Returns pending ciphertext only for an active addressed device.</summary>
    public async ValueTask<IReadOnlyList<StoredEnvelope>> ReceiveAsync(
        DeviceId recipientDeviceId,
        int maximumCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > ProtocolLimits.MaxDeliveryBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await RequireActiveDeviceAsync(recipientDeviceId, cancellationToken).ConfigureAwait(false);
        return await _envelopes.GetPendingAsync(recipientDeviceId, maximumCount, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records an acknowledgement from the addressed device.</summary>
    public async ValueTask<bool> AcknowledgeAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default)
    {
        await RequireActiveDeviceAsync(recipientDeviceId, cancellationToken).ConfigureAwait(false);
        return await _envelopes.AcknowledgeAsync(recipientDeviceId, messageId, acknowledgedAt, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PublicDevice> RequireActiveDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.GetAsync(deviceId, cancellationToken).ConfigureAwait(false) ??
            throw new ChatServerException("Device was not found.");
        if (device.IsRevoked)
        {
            throw new ChatServerException("Device is revoked.");
        }

        return device;
    }

    private static bool SamePublicDevice(PublicDevice first, PublicDevice second) =>
        first.UserId == second.UserId &&
        first.DeviceId == second.DeviceId &&
        first.KeyId == second.KeyId &&
        first.RegisteredAt == second.RegisteredAt &&
        first.RevokedAt == second.RevokedAt &&
        first.EncryptionPublicKey.Span.SequenceEqual(second.EncryptionPublicKey.Span) &&
        first.SigningPublicKey.Span.SequenceEqual(second.SigningPublicKey.Span);

    private static void ValidateConversationParticipants(
        UserId firstUserId,
        UserId secondUserId,
        DateTimeOffset createdAt)
    {
        if (firstUserId.Value == Guid.Empty || secondUserId.Value == Guid.Empty ||
            firstUserId == secondUserId || createdAt == default)
        {
            throw new ArgumentException("Personal conversation data is invalid.");
        }
    }

    private static void ValidateDirectoryRequest(Guid userId, int maximumCount)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID must not be empty.", nameof(userId));
        }

        if (maximumCount is < 1 or > ChatDirectoryLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
    }
}
