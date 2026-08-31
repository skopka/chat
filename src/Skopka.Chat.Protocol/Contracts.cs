namespace Skopka.Chat.Protocol;

/// <summary>Public, server-visible keys and lifecycle state for one device.</summary>
public sealed class PublicDevice
{
    private readonly byte[] _encryptionPublicKey;
    private readonly byte[] _signingPublicKey;

    /// <summary>Creates an immutable public device record.</summary>
    public PublicDevice(
        UserId userId,
        DeviceId deviceId,
        KeyId keyId,
        ReadOnlySpan<byte> encryptionPublicKey,
        ReadOnlySpan<byte> signingPublicKey,
        DateTimeOffset registeredAt,
        DateTimeOffset? revokedAt = null)
    {
        UserId = userId;
        DeviceId = deviceId;
        KeyId = keyId;
        _encryptionPublicKey = encryptionPublicKey.ToArray();
        _signingPublicKey = signingPublicKey.ToArray();
        RegisteredAt = registeredAt;
        RevokedAt = revokedAt;
    }

    /// <summary>Owner of the device.</summary>
    public UserId UserId { get; }

    /// <summary>Device identity.</summary>
    public DeviceId DeviceId { get; }

    /// <summary>Version of both published keys.</summary>
    public KeyId KeyId { get; }

    /// <summary>Raw X25519 public key.</summary>
    public ReadOnlyMemory<byte> EncryptionPublicKey => _encryptionPublicKey;

    /// <summary>Raw Ed25519 public key.</summary>
    public ReadOnlyMemory<byte> SigningPublicKey => _signingPublicKey;

    /// <summary>UTC registration time.</summary>
    public DateTimeOffset RegisteredAt { get; }

    /// <summary>UTC revocation time, or null while active.</summary>
    public DateTimeOffset? RevokedAt { get; }

    /// <summary>Whether this exact public-key record has been revoked.</summary>
    public bool IsRevoked => RevokedAt.HasValue;

    /// <summary>Returns a copy marked as revoked without changing key data.</summary>
    public PublicDevice Revoke(DateTimeOffset revokedAt) =>
        new(UserId, DeviceId, KeyId, _encryptionPublicKey, _signingPublicKey, RegisteredAt, revokedAt);
}

/// <summary>An immutable, server-readable envelope whose payload remains encrypted.</summary>
public sealed class EncryptedEnvelope
{
    private readonly byte[] _ephemeralPublicKey;
    private readonly byte[] _nonce;
    private readonly byte[] _ciphertext;
    private readonly byte[] _authenticationTag;
    private readonly byte[] _signature;

    /// <summary>Creates an encrypted envelope from explicit protocol fields.</summary>
    public EncryptedEnvelope(
        int protocolVersion,
        MessageId messageId,
        ConversationId conversationId,
        DeviceId senderDeviceId,
        DeviceId recipientDeviceId,
        KeyId senderSigningKeyId,
        KeyId recipientEncryptionKeyId,
        DateTimeOffset sentAt,
        DateTimeOffset? expiresAt,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> authenticationTag,
        ReadOnlySpan<byte> signature)
    {
        ProtocolVersion = protocolVersion;
        MessageId = messageId;
        ConversationId = conversationId;
        SenderDeviceId = senderDeviceId;
        RecipientDeviceId = recipientDeviceId;
        SenderSigningKeyId = senderSigningKeyId;
        RecipientEncryptionKeyId = recipientEncryptionKeyId;
        SentAt = sentAt;
        ExpiresAt = expiresAt;
        _ephemeralPublicKey = ephemeralPublicKey.ToArray();
        _nonce = nonce.ToArray();
        _ciphertext = ciphertext.ToArray();
        _authenticationTag = authenticationTag.ToArray();
        _signature = signature.ToArray();
    }

    /// <summary>Canonical wire-format version.</summary>
    public int ProtocolVersion { get; }

    /// <summary>Idempotency and replay identifier.</summary>
    public MessageId MessageId { get; }

    /// <summary>Conversation routing identifier.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Claimed signing device.</summary>
    public DeviceId SenderDeviceId { get; }

    /// <summary>Only device able to derive the content key.</summary>
    public DeviceId RecipientDeviceId { get; }

    /// <summary>Sender key version used for the signature.</summary>
    public KeyId SenderSigningKeyId { get; }

    /// <summary>Recipient key version used for X25519 agreement.</summary>
    public KeyId RecipientEncryptionKeyId { get; }

    /// <summary>Sender-supplied UTC creation time.</summary>
    public DateTimeOffset SentAt { get; }

    /// <summary>Optional server retention deadline.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Per-message raw X25519 ephemeral public key.</summary>
    public ReadOnlyMemory<byte> EphemeralPublicKey => _ephemeralPublicKey;

    /// <summary>Random XChaCha20-Poly1305 nonce.</summary>
    public ReadOnlyMemory<byte> Nonce => _nonce;

    /// <summary>Encrypted payload excluding its tag.</summary>
    public ReadOnlyMemory<byte> Ciphertext => _ciphertext;

    /// <summary>Poly1305 authentication tag.</summary>
    public ReadOnlyMemory<byte> AuthenticationTag => _authenticationTag;

    /// <summary>Ed25519 signature over the canonical envelope.</summary>
    public ReadOnlyMemory<byte> Signature => _signature;
}

/// <summary>Thrown when public protocol input is structurally invalid.</summary>
public sealed class ProtocolValidationException : ArgumentException
{
    /// <summary>Creates a validation exception that never includes message content or key material.</summary>
    public ProtocolValidationException(string message) : base(message)
    {
    }
}

/// <summary>Validates public protocol values before expensive or persistent operations.</summary>
public static class ProtocolValidator
{
    /// <summary>Validates one public device record.</summary>
    public static void Validate(PublicDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        RequireId(device.UserId.Value, "User ID");
        RequireId(device.DeviceId.Value, "Device ID");
        RequireId(device.KeyId.Value, "Key ID");
        RequireLength(device.EncryptionPublicKey, ProtocolLimits.X25519PublicKeyBytes, "Encryption public key");
        RequireLength(device.SigningPublicKey, ProtocolLimits.Ed25519PublicKeyBytes, "Signing public key");
        if (device.RegisteredAt == default || device.RevokedAt < device.RegisteredAt)
        {
            throw new ProtocolValidationException("Device lifecycle timestamps are invalid.");
        }
    }

    /// <summary>Validates one encrypted envelope without decrypting it.</summary>
    public static void Validate(EncryptedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ProtocolVersion != ProtocolVersions.Current)
        {
            throw new ProtocolValidationException("Unsupported protocol version.");
        }

        RequireId(envelope.MessageId.Value, "Message ID");
        RequireId(envelope.ConversationId.Value, "Conversation ID");
        RequireId(envelope.SenderDeviceId.Value, "Sender device ID");
        RequireId(envelope.RecipientDeviceId.Value, "Recipient device ID");
        RequireId(envelope.SenderSigningKeyId.Value, "Sender key ID");
        RequireId(envelope.RecipientEncryptionKeyId.Value, "Recipient key ID");

        if (envelope.SenderDeviceId == envelope.RecipientDeviceId)
        {
            throw new ProtocolValidationException("Sender and recipient devices must differ.");
        }

        if (envelope.SentAt == default || envelope.ExpiresAt <= envelope.SentAt ||
            envelope.ExpiresAt - envelope.SentAt > ProtocolLimits.MaxRetention)
        {
            throw new ProtocolValidationException("Envelope timestamps are invalid.");
        }

        RequireLength(envelope.EphemeralPublicKey, ProtocolLimits.X25519PublicKeyBytes, "Ephemeral public key");
        RequireLength(envelope.Nonce, ProtocolLimits.NonceBytes, "Nonce");
        RequireLength(envelope.AuthenticationTag, ProtocolLimits.AuthenticationTagBytes, "Authentication tag");
        RequireLength(envelope.Signature, ProtocolLimits.SignatureBytes, "Signature");
        if (envelope.Ciphertext.Length > ProtocolLimits.MaxCiphertextBytes)
        {
            throw new ProtocolValidationException("Ciphertext exceeds the protocol limit.");
        }
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ProtocolValidationException($"{name} must not be empty.");
        }
    }

    private static void RequireLength(ReadOnlyMemory<byte> value, int expected, string name)
    {
        if (value.Length != expected)
        {
            throw new ProtocolValidationException($"{name} has an invalid length.");
        }
    }
}
