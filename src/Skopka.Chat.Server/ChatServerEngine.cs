using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Transport-neutral server engine with no reference to client cryptography.</summary>
public sealed class ChatServerEngine
{
    private readonly IDeviceRepository _devices;
    private readonly IConversationRepository _conversations;
    private readonly IGroupConversationRepository? _groups;
    private readonly IEnvelopeRepository _envelopes;

    /// <summary>Creates an engine over independent server stores.</summary>
    public ChatServerEngine(
        IDeviceRepository devices,
        IConversationRepository conversations,
        IEnvelopeRepository envelopes)
        : this(devices, conversations, envelopes, groups: null)
    {
    }

    /// <summary>Creates an engine with optional small-group metadata support.</summary>
    public ChatServerEngine(
        IDeviceRepository devices,
        IConversationRepository conversations,
        IEnvelopeRepository envelopes,
        IGroupConversationRepository? groups)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _groups = groups;
    }

    /// <summary>Creates a small group owned by the authenticated creator.</summary>
    public async ValueTask<GroupConversation> CreateGroupConversationAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        string title,
        IReadOnlyCollection<UserId> memberUserIds,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var groups = RequireGroups();
        if (authenticatedUserId.Value == Guid.Empty || conversationId.Value == Guid.Empty || createdAt == default)
        {
            throw new ArgumentException("Group conversation data is invalid.");
        }

        ArgumentNullException.ThrowIfNull(memberUserIds);
        var participantIds = memberUserIds
            .Append(authenticatedUserId)
            .Distinct()
            .OrderBy(static userId => userId.Value)
            .ToArray();
        if (participantIds.Length is < 2 or > GroupConversationLimits.MaxMembers ||
            participantIds.Any(static userId => userId.Value == Guid.Empty))
        {
            throw new ArgumentException("Group participants are invalid.", nameof(memberUserIds));
        }

        var normalizedTitle = GroupConversation.NormalizeTitle(title);
        var conversation = new GroupConversation(
            conversationId,
            normalizedTitle,
            authenticatedUserId,
            revision: 1,
            createdAt,
            participantIds.Select(userId => new GroupConversationMember(
                userId,
                userId == authenticatedUserId ? GroupConversationRole.Owner : GroupConversationRole.Member,
                createdAt)).ToArray());
        if (await groups.TryAddAsync(conversation, cancellationToken).ConfigureAwait(false))
        {
            return conversation;
        }

        var existing = await groups.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null &&
            existing.CreatedByUserId == authenticatedUserId &&
            string.Equals(existing.Title, normalizedTitle, StringComparison.Ordinal) &&
            existing.Members.Select(static member => member.UserId).SequenceEqual(participantIds))
        {
            return existing;
        }

        throw new ChatServerException("The group conversation ID is already in use.");
    }

    /// <summary>Gets current group metadata after checking active membership.</summary>
    public async ValueTask<GroupConversation> GetGroupConversationAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateConversationLookup(authenticatedUserId, conversationId);
        var conversation = await RequireGroups().GetAsync(conversationId, cancellationToken).ConfigureAwait(false) ??
            throw new ChatServerException("Group conversation was not found.");
        RequireGroupMember(conversation, authenticatedUserId);
        return conversation;
    }

    /// <summary>Lists groups containing the authenticated user.</summary>
    public ValueTask<GroupConversationDirectoryPage> ListGroupConversationsAsync(
        UserId authenticatedUserId,
        ConversationDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ValidateDirectoryRequest(authenticatedUserId.Value, maximumCount);
        return RequireGroups().ListForUserAsync(authenticatedUserId, cursor, maximumCount, cancellationToken);
    }

    /// <summary>Renames a group using optimistic revision control.</summary>
    public async ValueTask<GroupConversation> RenameGroupConversationAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        string title,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var conversation = await GetGroupConversationAsync(authenticatedUserId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        RequireAdministrator(conversation, authenticatedUserId);
        return await ReplaceGroupAsync(conversation.WithTitle(title), expectedRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Adds one ordinary member using optimistic revision control.</summary>
    public async ValueTask<GroupConversation> AddGroupMemberAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        UserId newMemberUserId,
        long expectedRevision,
        DateTimeOffset joinedAt,
        CancellationToken cancellationToken = default)
    {
        var conversation = await GetGroupConversationAsync(authenticatedUserId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        RequireAdministrator(conversation, authenticatedUserId);
        return await ReplaceGroupAsync(
            conversation.AddMember(newMemberUserId, joinedAt),
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a member, allowing self-leave except for the permanent owner.</summary>
    public async ValueTask<GroupConversation> RemoveGroupMemberAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        UserId memberUserId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var conversation = await GetGroupConversationAsync(authenticatedUserId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        var actor = RequireGroupMember(conversation, authenticatedUserId);
        var target = RequireGroupMember(conversation, memberUserId);
        if (authenticatedUserId != memberUserId)
        {
            RequireAdministrator(conversation, authenticatedUserId);
            if (actor.Role == GroupConversationRole.Administrator && target.Role != GroupConversationRole.Member)
            {
                throw new ChatServerException("An administrator cannot remove another administrator or the owner.");
            }
        }

        return await ReplaceGroupAsync(
            conversation.RemoveMember(memberUserId),
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Assigns or removes administrator privileges; only the permanent owner may do so.</summary>
    public async ValueTask<GroupConversation> ChangeGroupMemberRoleAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        UserId memberUserId,
        GroupConversationRole role,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var conversation = await GetGroupConversationAsync(authenticatedUserId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (RequireGroupMember(conversation, authenticatedUserId).Role != GroupConversationRole.Owner)
        {
            throw new ChatServerException("Only the group owner can change member roles.");
        }

        return await ReplaceGroupAsync(
            conversation.ChangeRole(memberUserId, role),
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
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

        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is not null)
        {
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

        if (_groups is null)
        {
            throw new ChatServerException("Conversation was not found.");
        }

        var group = await _groups.GetAsync(conversationId, cancellationToken).ConfigureAwait(false) ??
            throw new ChatServerException("Conversation was not found.");
        RequireGroupMember(group, authenticatedUserId);
        return await _devices.ListActiveForUsersAsync(
            group.Members.Select(static member => member.UserId).ToArray(),
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

        var conversation = await _conversations.GetAsync(envelope.ConversationId, cancellationToken).ConfigureAwait(false);
        var participantsAreValid = conversation is not null &&
            conversation.Contains(sender.UserId) && conversation.Contains(recipient.UserId);
        if (conversation is null && _groups is not null)
        {
            var group = await _groups.GetAsync(envelope.ConversationId, cancellationToken).ConfigureAwait(false);
            participantsAreValid = group is not null && group.Contains(sender.UserId) && group.Contains(recipient.UserId);
        }

        if (!participantsAreValid)
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

    private IGroupConversationRepository RequireGroups() =>
        _groups ?? throw new NotSupportedException("Group conversations are not configured.");

    private async ValueTask<GroupConversation> ReplaceGroupAsync(
        GroupConversation updated,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);

        var result = await RequireGroups().TryReplaceAsync(updated, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        return result == GroupConversationStoreResult.Updated
            ? updated
            : throw new ChatServerException("Group metadata changed; refresh and retry.");
    }

    private static GroupConversationMember RequireGroupMember(GroupConversation conversation, UserId userId) =>
        conversation.FindMember(userId) ?? throw new ChatServerException("The caller is not an active group member.");

    private static void RequireAdministrator(GroupConversation conversation, UserId userId)
    {
        if (RequireGroupMember(conversation, userId).Role is not GroupConversationRole.Owner and
            not GroupConversationRole.Administrator)
        {
            throw new ChatServerException("Group administrator privileges are required.");
        }
    }

    private static void ValidateConversationLookup(UserId userId, ConversationId conversationId)
    {
        if (userId.Value == Guid.Empty || conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation lookup data is invalid.");
        }
    }

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
