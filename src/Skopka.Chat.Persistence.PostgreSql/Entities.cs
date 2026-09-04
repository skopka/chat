using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Persistence.PostgreSql;

internal sealed class DeviceEntity
{
    public Guid DeviceId { get; set; }
    public Guid UserId { get; set; }
    public Guid KeyId { get; set; }
    public byte[] EncryptionPublicKey { get; set; } = [];
    public byte[] SigningPublicKey { get; set; } = [];
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public static DeviceEntity FromDomain(PublicDevice device) => new()
    {
        DeviceId = device.DeviceId.Value,
        UserId = device.UserId.Value,
        KeyId = device.KeyId.Value,
        EncryptionPublicKey = device.EncryptionPublicKey.ToArray(),
        SigningPublicKey = device.SigningPublicKey.ToArray(),
        RegisteredAt = device.RegisteredAt,
        RevokedAt = device.RevokedAt
    };

    public PublicDevice ToDomain() => new(
        new UserId(UserId),
        new DeviceId(DeviceId),
        new KeyId(KeyId),
        EncryptionPublicKey,
        SigningPublicKey,
        RegisteredAt,
        RevokedAt);
}

internal sealed class ConversationEntity
{
    public const short PersonalKind = 1;
    public const short GroupKind = 2;

    public Guid ConversationId { get; set; }
    public short ConversationKind { get; set; } = PersonalKind;
    public Guid? FirstUserId { get; set; }
    public Guid? SecondUserId { get; set; }
    public string? Title { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public long? Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static ConversationEntity FromDomain(PersonalConversation conversation) => new()
    {
        ConversationId = conversation.ConversationId.Value,
        ConversationKind = PersonalKind,
        FirstUserId = conversation.FirstUserId.Value,
        SecondUserId = conversation.SecondUserId.Value,
        CreatedAt = conversation.CreatedAt
    };

    public PersonalConversation ToDomain() => new(
        new ConversationId(ConversationId),
        new UserId(FirstUserId!.Value),
        new UserId(SecondUserId!.Value),
        CreatedAt);

    public static ConversationEntity FromDomain(GroupConversation conversation) => new()
    {
        ConversationId = conversation.ConversationId.Value,
        ConversationKind = GroupKind,
        Title = conversation.Title,
        CreatedByUserId = conversation.CreatedByUserId.Value,
        Revision = conversation.Revision,
        CreatedAt = conversation.CreatedAt
    };

    public GroupConversation ToGroupDomain(IReadOnlyCollection<GroupConversationMember> members) => new(
        new ConversationId(ConversationId),
        Title!,
        new UserId(CreatedByUserId!.Value),
        Revision!.Value,
        CreatedAt,
        members);
}

internal sealed class GroupConversationMemberEntity
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public short Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    public static GroupConversationMemberEntity FromDomain(
        ConversationId conversationId,
        GroupConversationMember member) => new()
        {
            ConversationId = conversationId.Value,
            UserId = member.UserId.Value,
            Role = (short)member.Role,
            JoinedAt = member.JoinedAt
        };

    public GroupConversationMember ToDomain() => new(
        new UserId(UserId),
        (GroupConversationRole)Role,
        JoinedAt);
}

internal sealed class EnvelopeEntity
{
    public Guid MessageId { get; set; }
    public int ProtocolVersion { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderDeviceId { get; set; }
    public Guid RecipientDeviceId { get; set; }
    public Guid SenderSigningKeyId { get; set; }
    public Guid RecipientEncryptionKeyId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public byte[] EphemeralPublicKey { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] AuthenticationTag { get; set; } = [];
    public byte[] Signature { get; set; } = [];
    public byte[] CanonicalHash { get; set; } = [];
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }

    public static EnvelopeEntity FromDomain(EncryptedEnvelope envelope, DateTimeOffset acceptedAt, byte[] canonicalHash) => new()
    {
        MessageId = envelope.MessageId.Value,
        ProtocolVersion = envelope.ProtocolVersion,
        ConversationId = envelope.ConversationId.Value,
        SenderDeviceId = envelope.SenderDeviceId.Value,
        RecipientDeviceId = envelope.RecipientDeviceId.Value,
        SenderSigningKeyId = envelope.SenderSigningKeyId.Value,
        RecipientEncryptionKeyId = envelope.RecipientEncryptionKeyId.Value,
        SentAt = envelope.SentAt,
        ExpiresAt = envelope.ExpiresAt,
        EphemeralPublicKey = envelope.EphemeralPublicKey.ToArray(),
        Nonce = envelope.Nonce.ToArray(),
        Ciphertext = envelope.Ciphertext.ToArray(),
        AuthenticationTag = envelope.AuthenticationTag.ToArray(),
        Signature = envelope.Signature.ToArray(),
        CanonicalHash = canonicalHash,
        AcceptedAt = acceptedAt
    };

    public StoredEnvelope ToDomain() => new(
        new EncryptedEnvelope(
            ProtocolVersion,
            new MessageId(MessageId),
            new ConversationId(ConversationId),
            new DeviceId(SenderDeviceId),
            new DeviceId(RecipientDeviceId),
            new KeyId(SenderSigningKeyId),
            new KeyId(RecipientEncryptionKeyId),
            SentAt,
            ExpiresAt,
            EphemeralPublicKey,
            Nonce,
            Ciphertext,
            AuthenticationTag,
            Signature),
        AcceptedAt,
        AcknowledgedAt);
}

internal sealed class ChatServerOutboxEntity
{
    public Guid EventId { get; set; }
    public Guid SourceMessageId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int EventVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string PartitionKey { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? LastFailedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public static ChatServerOutboxEntity FromDomain(Guid sourceMessageId, ChatServerOutboxMessage message) => new()
    {
        EventId = message.EventId,
        SourceMessageId = sourceMessageId,
        EventType = message.EventType,
        EventVersion = message.EventVersion,
        OccurredAt = message.OccurredAt,
        PartitionKey = message.PartitionKey,
        Payload = message.Payload.ToArray(),
        NextAttemptAt = message.OccurredAt
    };

    public ChatServerOutboxMessage ToDomain() => new(
        EventId,
        EventType,
        EventVersion,
        OccurredAt,
        PartitionKey,
        Payload);
}
