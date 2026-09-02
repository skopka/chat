using System.Security.Cryptography;
using System.Text;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Raised when authentication, key agreement or decryption fails without exposing sensitive input.</summary>
public sealed class ChatCryptographicException : CryptographicException
{
    /// <summary>Creates a content-free cryptographic failure.</summary>
    public ChatCryptographicException(string message) : base(message)
    {
    }
}

/// <summary>Encrypts and authenticates protocol-v1 recipient envelopes.</summary>
public sealed class ChatCryptoService
{
    private readonly IChatCryptographyProvider _crypto;
    private readonly IDeviceKeyStore _keyStore;

    /// <summary>Creates a crypto service over the device private-key store.</summary>
    public ChatCryptoService(IDeviceKeyStore keyStore) : this(keyStore, ChatCryptographyDefaults.Create()) { }

    /// <summary>Creates a service with an explicitly selected endpoint primitive provider.</summary>
    public ChatCryptoService(IDeviceKeyStore keyStore, IChatCryptographyProvider cryptography)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _crypto = cryptography ?? throw new ArgumentNullException(nameof(cryptography));
    }

    /// <summary>UTF-8 encodes and encrypts one text message for exactly one recipient device.</summary>
    public ValueTask<EncryptedEnvelope> EncryptTextAsync(
        string plaintext,
        ConversationId conversationId,
        MessageId messageId,
        DeviceId senderDeviceId,
        PublicDevice recipient,
        DateTimeOffset sentAt,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (Encoding.UTF8.GetByteCount(plaintext) > ProtocolLimits.MaxPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Plaintext exceeds the protocol limit.");
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return EncryptAsync(bytes, conversationId, messageId, senderDeviceId, recipient, sentAt, expiresAt, cancellationToken);
    }

    /// <summary>Encodes and encrypts typed text, reply, forward or reaction content for one recipient device.</summary>
    public async ValueTask<EncryptedEnvelope> EncryptContentAsync(
        ChatContent content,
        ConversationId conversationId,
        MessageId messageId,
        DeviceId senderDeviceId,
        PublicDevice recipient,
        DateTimeOffset sentAt,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var encoded = ChatContentEncoding.Encode(content);
        try
        {
            return await EncryptAsync(
                encoded,
                conversationId,
                messageId,
                senderDeviceId,
                recipient,
                sentAt,
                expiresAt,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    /// <summary>Encrypts one bounded binary payload for exactly one recipient device.</summary>
    public async ValueTask<EncryptedEnvelope> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        ConversationId conversationId,
        MessageId messageId,
        DeviceId senderDeviceId,
        PublicDevice recipient,
        DateTimeOffset sentAt,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ProtocolValidator.Validate(recipient);
        if (recipient.IsRevoked)
        {
            throw new InvalidOperationException("Cannot encrypt to a revoked device.");
        }

        if (plaintext.Length > ProtocolLimits.MaxPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Plaintext exceeds the protocol limit.");
        }

        var material = await LoadRequiredAsync(senderDeviceId, cancellationToken).ConfigureAwait(false);
        var signingPrivate = material.ExportSigningPrivateKey();
        byte[]? ephemeralPrivate = null;
        byte[]? contentKey = null;
        try
        {
            ephemeralPrivate = _crypto.CreatePrivateKey(ChatKeyAlgorithm.X25519);
            var nonce = RandomNumberGenerator.GetBytes(ProtocolLimits.NonceBytes);
            var ephemeralPublic = _crypto.GetPublicKey(ChatKeyAlgorithm.X25519, ephemeralPrivate);
            var unsigned = CreateEnvelope(
                messageId,
                conversationId,
                material.DeviceId,
                recipient,
                material.KeyId,
                sentAt,
                expiresAt,
                ephemeralPublic,
                nonce,
                [],
                new byte[ProtocolLimits.AuthenticationTagBytes],
                new byte[ProtocolLimits.SignatureBytes]);
            var associatedData = CanonicalEnvelopeEncoding.EncodeAssociatedData(unsigned);
            contentKey = _crypto.DeriveEnvelopeKey(ephemeralPrivate, recipient.EncryptionPublicKey.Span, nonce, associatedData);
            var encrypted = _crypto.Encrypt(contentKey, nonce, associatedData, plaintext.Span);
            var ciphertextLength = encrypted.Length - ProtocolLimits.AuthenticationTagBytes;
            var ciphertext = encrypted.AsSpan(0, ciphertextLength).ToArray();
            var tag = encrypted.AsSpan(ciphertextLength).ToArray();
            var toSign = CreateEnvelope(
                messageId,
                conversationId,
                material.DeviceId,
                recipient,
                material.KeyId,
                sentAt,
                expiresAt,
                ephemeralPublic,
                nonce,
                ciphertext,
                tag,
                new byte[ProtocolLimits.SignatureBytes]);
            var signature = _crypto.Sign(signingPrivate, CanonicalEnvelopeEncoding.EncodeForSignature(toSign));
            var result = CreateEnvelope(
                messageId,
                conversationId,
                material.DeviceId,
                recipient,
                material.KeyId,
                sentAt,
                expiresAt,
                ephemeralPublic,
                nonce,
                ciphertext,
                tag,
                signature);
            ProtocolValidator.Validate(result);
            return result;
        }
        catch (FormatException)
        {
            throw new ChatCryptographicException("Stored device key material is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingPrivate);
            if (ephemeralPrivate is not null) { CryptographicOperations.ZeroMemory(ephemeralPrivate); }
            if (contentKey is not null) { CryptographicOperations.ZeroMemory(contentKey); }
        }
    }

    /// <summary>Verifies the sender signature, authenticates the header and decrypts for the addressed device.</summary>
    public async ValueTask<byte[]> DecryptAsync(
        EncryptedEnvelope envelope,
        PublicDevice sender,
        CancellationToken cancellationToken = default)
    {
        ProtocolValidator.Validate(envelope);
        ProtocolValidator.Validate(sender);
        if (sender.DeviceId != envelope.SenderDeviceId || sender.KeyId != envelope.SenderSigningKeyId)
        {
            throw new ChatCryptographicException("Sender identity does not match the envelope.");
        }

        try
        {
            if (!_crypto.Verify(sender.SigningPublicKey.Span, CanonicalEnvelopeEncoding.EncodeForSignature(envelope), envelope.Signature.Span))
            {
                throw new ChatCryptographicException("Envelope signature verification failed.");
            }
        }
        catch (FormatException)
        {
            throw new ChatCryptographicException("Sender public key is invalid.");
        }

        var material = await LoadRequiredAsync(envelope.RecipientDeviceId, cancellationToken).ConfigureAwait(false);
        if (material.KeyId != envelope.RecipientEncryptionKeyId)
        {
            throw new ChatCryptographicException("Recipient key does not match the envelope.");
        }

        var encryptionPrivate = material.ExportEncryptionPrivateKey();
        byte[]? contentKey = null;
        try
        {
            var associatedData = CanonicalEnvelopeEncoding.EncodeAssociatedData(envelope);
            contentKey = _crypto.DeriveEnvelopeKey(encryptionPrivate, envelope.EphemeralPublicKey.Span, envelope.Nonce.Span, associatedData);
            var encrypted = new byte[envelope.Ciphertext.Length + envelope.AuthenticationTag.Length];
            envelope.Ciphertext.Span.CopyTo(encrypted);
            envelope.AuthenticationTag.Span.CopyTo(encrypted.AsSpan(envelope.Ciphertext.Length));
            return _crypto.Decrypt(contentKey, envelope.Nonce.Span, associatedData, encrypted) ??
                throw new ChatCryptographicException("Envelope authentication failed.");
        }
        catch (FormatException)
        {
            throw new ChatCryptographicException("Cryptographic key encoding is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionPrivate);
            if (contentKey is not null) { CryptographicOperations.ZeroMemory(contentKey); }
        }
    }

    /// <summary>Authenticates, decrypts and strictly decodes typed application content.</summary>
    public async ValueTask<ChatContent> DecryptContentAsync(
        EncryptedEnvelope envelope,
        PublicDevice sender,
        CancellationToken cancellationToken = default)
    {
        var plaintext = await DecryptAsync(envelope, sender, cancellationToken).ConfigureAwait(false);
        try
        {
            return ChatContentEncoding.Decode(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<DeviceKeyMaterial> LoadRequiredAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        var material = await _keyStore.LoadAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return material ?? throw new InvalidOperationException("Device key material was not found.");
    }

    private static EncryptedEnvelope CreateEnvelope(
        MessageId messageId,
        ConversationId conversationId,
        DeviceId senderDeviceId,
        PublicDevice recipient,
        KeyId senderKeyId,
        DateTimeOffset sentAt,
        DateTimeOffset? expiresAt,
        ReadOnlySpan<byte> ephemeralPublicKey,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> signature) =>
        new(
            ProtocolVersions.Current,
            messageId,
            conversationId,
            senderDeviceId,
            recipient.DeviceId,
            senderKeyId,
            recipient.KeyId,
            sentAt,
            expiresAt ?? sentAt.AddDays(7),
            ephemeralPublicKey,
            nonce,
            ciphertext,
            tag,
            signature);
}
