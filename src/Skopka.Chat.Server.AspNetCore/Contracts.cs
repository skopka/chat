using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Server.AspNetCore;

/// <summary>Public key material supplied while registering the caller's authenticated device.</summary>
public sealed record RegisterDeviceRequest(
    Guid DeviceId,
    Guid KeyId,
    byte[] EncryptionPublicKey,
    byte[] SigningPublicKey);

/// <summary>Creates a personal conversation containing the authenticated user and one peer.</summary>
public sealed record CreateConversationRequest(Guid ConversationId, Guid PeerUserId);

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
    internal static PublicDeviceResponse FromDomain(PublicDevice device) => new(
        device.UserId.Value,
        device.DeviceId.Value,
        device.KeyId.Value,
        device.EncryptionPublicKey.ToArray(),
        device.SigningPublicKey.ToArray(),
        device.RegisteredAt,
        device.RevokedAt);
}

/// <summary>Server-created personal conversation metadata.</summary>
public sealed record PersonalConversationResponse(
    Guid ConversationId,
    Guid FirstUserId,
    Guid SecondUserId,
    DateTimeOffset CreatedAt)
{
    internal static PersonalConversationResponse FromDomain(PersonalConversation conversation) => new(
        conversation.ConversationId.Value,
        conversation.FirstUserId.Value,
        conversation.SecondUserId.Value,
        conversation.CreatedAt);
}

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
    internal EncryptedEnvelope ToDomain() => new(
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

    internal static EncryptedEnvelopeDto FromDomain(EncryptedEnvelope envelope) => new(
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

/// <summary>One pending encrypted delivery and its server acceptance time.</summary>
public sealed record PendingDeliveryResponse(EncryptedEnvelopeDto Envelope, DateTimeOffset AcceptedAt)
{
    internal static PendingDeliveryResponse FromDomain(StoredEnvelope stored) =>
        new(EncryptedEnvelopeDto.FromDomain(stored.Envelope), stored.AcceptedAt);
}

/// <summary>Idempotent envelope submission outcome.</summary>
public sealed record SubmitEnvelopeResponse(Guid MessageId, bool Duplicate);
