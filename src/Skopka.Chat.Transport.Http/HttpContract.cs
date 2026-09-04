using Skopka.Chat.Protocol;

namespace Skopka.Chat.Transport.Http;

/// <summary>Stable relative routes shared by the HTTP client and ASP.NET Core adapter.</summary>
public static class SkopkaChatHttpRoutes
{
    /// <summary>Default versioned route-group prefix.</summary>
    public const string DefaultPrefix = "/skopka-chat/v1";

    /// <summary>Device registration collection.</summary>
    public const string Devices = "/devices";

    /// <summary>Personal-conversation collection.</summary>
    public const string Conversations = "/conversations";

    /// <summary>Idempotent personal-conversation lookup/creation endpoint.</summary>
    public const string PersonalConversation = "/conversations/personal";

    /// <summary>Small-group conversation collection.</summary>
    public const string GroupConversations = "/conversations/groups";

    /// <summary>Encrypted-envelope submission endpoint.</summary>
    public const string Envelopes = "/envelopes";

    /// <summary>Pending delivery collection for the authenticated device.</summary>
    public const string Deliveries = "/deliveries";

    /// <summary>Encrypted attachment collection.</summary>
    public const string Attachments = "/attachments";

    /// <summary>Returns the public-directory route for one device.</summary>
    public static string Device(Guid deviceId) => $"{Devices}/{deviceId:D}";

    /// <summary>Returns the revocation route for one device.</summary>
    public static string DeviceRevocation(Guid deviceId) => $"{Device(deviceId)}/revocation";

    /// <summary>Returns the active participant-device directory route for a conversation.</summary>
    public static string ConversationDevices(Guid conversationId) =>
        $"{Conversations}/{conversationId:D}/devices";

    /// <summary>Returns the metadata route for one small group.</summary>
    public static string GroupConversation(Guid conversationId) =>
        $"{GroupConversations}/{conversationId:D}";

    /// <summary>Returns the member collection route for one small group.</summary>
    public static string GroupMembers(Guid conversationId) =>
        $"{GroupConversation(conversationId)}/members";

    /// <summary>Returns the route for one small-group member.</summary>
    public static string GroupMember(Guid conversationId, Guid userId) =>
        $"{GroupMembers(conversationId)}/{userId:D}";

    /// <summary>Returns the role route for one small-group member.</summary>
    public static string GroupMemberRole(Guid conversationId, Guid userId) =>
        $"{GroupMember(conversationId, userId)}/role";

    /// <summary>Returns the acknowledgement route for one encrypted message.</summary>
    public static string Acknowledgement(Guid messageId) =>
        $"{Deliveries}/{messageId:D}/acknowledgements";

    /// <summary>Returns the ciphertext route for one attachment.</summary>
    public static string Attachment(Guid attachmentId) => $"{Attachments}/{attachmentId:D}";
}

/// <summary>Strict transport headers for opaque attachment metadata.</summary>
public static class SkopkaChatAttachmentHeaders
{
    /// <summary>Conversation ID used by the server authorization policy.</summary>
    public const string ConversationId = "X-Skopka-Conversation-Id";

    /// <summary>Uppercase hexadecimal SHA-256 over the ciphertext body.</summary>
    public const string CiphertextSha256 = "X-Skopka-Ciphertext-Sha256";

    /// <summary>Optional UTC expiry in round-trip ISO-8601 form.</summary>
    public const string ExpiresAt = "X-Skopka-Expires-At";
}

/// <summary>Transport-level bounds applied in addition to protocol validation.</summary>
public static class SkopkaChatHttpLimits
{
    /// <summary>Maximum JSON request body advertised by the ASP.NET Core endpoints.</summary>
    public const long MaxRequestBodyBytes = ProtocolLimits.MaxCiphertextBytes + (32 * 1024L);

    /// <summary>Maximum buffered JSON response for registration, directory and mutation calls.</summary>
    public const int MaxControlResponseBytes = 128 * 1024;

    /// <summary>Maximum buffered JSON response for a full delivery batch.</summary>
    public const int MaxDeliveryResponseBytes = 10 * 1024 * 1024;

    /// <summary>Largest accepted opaque pagination cursor.</summary>
    public const int MaxCursorCharacters = 64;

    /// <summary>Largest conversation or participant-device directory page.</summary>
    public const int MaxDirectoryPageSize = 100;
}

/// <summary>Public key material supplied while registering the caller's authenticated device.</summary>
public sealed record RegisterDeviceRequest(
    Guid DeviceId,
    Guid KeyId,
    byte[] EncryptionPublicKey,
    byte[] SigningPublicKey)
{
    /// <summary>Creates a request without sending the user ID or client lifecycle timestamps.</summary>
    public static RegisterDeviceRequest FromDomain(PublicDevice device)
    {
        ProtocolValidator.Validate(device);
        return new RegisterDeviceRequest(
            device.DeviceId.Value,
            device.KeyId.Value,
            device.EncryptionPublicKey.ToArray(),
            device.SigningPublicKey.ToArray());
    }
}

/// <summary>Creates a personal conversation containing the authenticated user and one peer.</summary>
public sealed record CreateConversationRequest(Guid ConversationId, Guid PeerUserId);

/// <summary>Gets or creates a personal conversation using only the authenticated user and peer ID.</summary>
public sealed record GetOrCreateConversationRequest(Guid PeerUserId);

/// <summary>Creates a small group owned by the authenticated user.</summary>
public sealed record CreateGroupConversationRequest(Guid ConversationId, string Title, Guid[] MemberUserIds);

/// <summary>Renames a group at an expected metadata revision.</summary>
public sealed record RenameGroupConversationRequest(string Title, long ExpectedRevision);

/// <summary>Adds one ordinary group member at an expected metadata revision.</summary>
public sealed record AddGroupMemberRequest(Guid UserId, long ExpectedRevision);

/// <summary>Changes one group member role at an expected metadata revision.</summary>
public sealed record ChangeGroupMemberRoleRequest(byte Role, long ExpectedRevision);

/// <summary>Server-visible public device data returned by the authenticated directory.</summary>
public sealed record PublicDeviceResponse(
    Guid UserId,
    Guid DeviceId,
    Guid KeyId,
    byte[] EncryptionPublicKey,
    byte[] SigningPublicKey,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>Creates a wire response from validated public device data.</summary>
    public static PublicDeviceResponse FromDomain(PublicDevice device)
    {
        ProtocolValidator.Validate(device);
        return new PublicDeviceResponse(
            device.UserId.Value,
            device.DeviceId.Value,
            device.KeyId.Value,
            device.EncryptionPublicKey.ToArray(),
            device.SigningPublicKey.ToArray(),
            device.RegisteredAt,
            device.RevokedAt);
    }

    /// <summary>Creates and validates the transport-neutral protocol value.</summary>
    public PublicDevice ToDomain()
    {
        var device = new PublicDevice(
            new UserId(UserId),
            new DeviceId(DeviceId),
            new KeyId(KeyId),
            EncryptionPublicKey ?? [],
            SigningPublicKey ?? [],
            RegisteredAt,
            RevokedAt);
        ProtocolValidator.Validate(device);
        return device;
    }
}

/// <summary>Server-created personal conversation metadata.</summary>
public sealed record PersonalConversationResponse(
    Guid ConversationId,
    Guid FirstUserId,
    Guid SecondUserId,
    DateTimeOffset CreatedAt);

/// <summary>One active server-visible small-group participant.</summary>
public sealed record GroupConversationMemberResponse(Guid UserId, byte Role, DateTimeOffset JoinedAt);

/// <summary>Current small-group metadata; message text and mentions remain encrypted.</summary>
public sealed record GroupConversationResponse(
    Guid ConversationId,
    string Title,
    Guid CreatedByUserId,
    long Revision,
    DateTimeOffset CreatedAt,
    GroupConversationMemberResponse[] Members);

/// <summary>Bounded page of current small-group metadata.</summary>
public sealed record GroupConversationDirectoryResponse(
    GroupConversationResponse[] Items,
    string? NextCursor);

/// <summary>Bounded page of conversation metadata without message plaintext or previews.</summary>
public sealed record ConversationDirectoryResponse(
    PersonalConversationResponse[] Items,
    string? NextCursor);

/// <summary>Bounded page of active devices for authorized conversation participants.</summary>
public sealed record DeviceDirectoryResponse(
    PublicDeviceResponse[] Items,
    string? NextCursor);

/// <summary>Encrypted protocol envelope accepted and returned by the transport.</summary>
public sealed record EncryptedEnvelopeDto(
    int ProtocolVersion,
    Guid MessageId,
    Guid ConversationId,
    Guid SenderDeviceId,
    Guid RecipientDeviceId,
    Guid SenderSigningKeyId,
    Guid RecipientEncryptionKeyId,
    DateTimeOffset SentAt,
    DateTimeOffset? ExpiresAt,
    byte[] EphemeralPublicKey,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] AuthenticationTag,
    byte[] Signature)
{
    /// <summary>Creates and structurally validates the transport-neutral envelope.</summary>
    public EncryptedEnvelope ToDomain()
    {
        var envelope = new EncryptedEnvelope(
            ProtocolVersion,
            new MessageId(MessageId),
            new ConversationId(ConversationId),
            new DeviceId(SenderDeviceId),
            new DeviceId(RecipientDeviceId),
            new KeyId(SenderSigningKeyId),
            new KeyId(RecipientEncryptionKeyId),
            SentAt,
            ExpiresAt,
            EphemeralPublicKey ?? [],
            Nonce ?? [],
            Ciphertext ?? [],
            AuthenticationTag ?? [],
            Signature ?? []);
        ProtocolValidator.Validate(envelope);
        return envelope;
    }

    /// <summary>Creates a wire envelope from validated protocol data.</summary>
    public static EncryptedEnvelopeDto FromDomain(EncryptedEnvelope envelope)
    {
        ProtocolValidator.Validate(envelope);
        return new EncryptedEnvelopeDto(
            envelope.ProtocolVersion,
            envelope.MessageId.Value,
            envelope.ConversationId.Value,
            envelope.SenderDeviceId.Value,
            envelope.RecipientDeviceId.Value,
            envelope.SenderSigningKeyId.Value,
            envelope.RecipientEncryptionKeyId.Value,
            envelope.SentAt,
            envelope.ExpiresAt,
            envelope.EphemeralPublicKey.ToArray(),
            envelope.Nonce.ToArray(),
            envelope.Ciphertext.ToArray(),
            envelope.AuthenticationTag.ToArray(),
            envelope.Signature.ToArray());
    }
}

/// <summary>One pending encrypted delivery and its server acceptance time.</summary>
public sealed record PendingDeliveryResponse(EncryptedEnvelopeDto Envelope, DateTimeOffset AcceptedAt);

/// <summary>Idempotent envelope submission outcome.</summary>
public sealed record SubmitEnvelopeResponse(Guid MessageId, bool Duplicate);
