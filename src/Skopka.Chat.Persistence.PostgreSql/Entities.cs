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
    public Guid ConversationId { get; set; }
    public Guid FirstUserId { get; set; }
    public Guid SecondUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static ConversationEntity FromDomain(PersonalConversation conversation) => new()
    {
        ConversationId = conversation.ConversationId.Value,
        FirstUserId = conversation.FirstUserId.Value,
        SecondUserId = conversation.SecondUserId.Value,
        CreatedAt = conversation.CreatedAt
    };

    public PersonalConversation ToDomain() => new(
        new ConversationId(ConversationId),
        new UserId(FirstUserId),
        new UserId(SecondUserId),
        CreatedAt);
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
