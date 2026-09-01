using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
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
    private static readonly KeyAgreementAlgorithm Agreement = KeyAgreementAlgorithm.X25519;
    private static readonly SignatureAlgorithm Signature = SignatureAlgorithm.Ed25519;
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;
    private static readonly KeyDerivationAlgorithm Kdf = KeyDerivationAlgorithm.HkdfSha256;
    private readonly IDeviceKeyStore _keyStore;

    /// <summary>Creates a crypto service over the device private-key store.</summary>
    public ChatCryptoService(IDeviceKeyStore keyStore) =>
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));

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
        try
        {
            using var signingKey = Key.Import(Signature, signingPrivate, KeyBlobFormat.NSecPrivateKey);
            using var ephemeralKey = Key.Create(Agreement);
            var recipientPublicKey = PublicKey.Import(Agreement, recipient.EncryptionPublicKey.Span, KeyBlobFormat.RawPublicKey);
            using var sharedSecret = Agreement.Agree(ephemeralKey, recipientPublicKey) ??
                throw new ChatCryptographicException("Key agreement failed.");

            var nonce = RandomNumberGenerator.GetBytes(ProtocolLimits.NonceBytes);
            var ephemeralPublic = ephemeralKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
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
            using var contentKey = Kdf.DeriveKey(sharedSecret, nonce, associatedData, Aead);
            var encrypted = Aead.Encrypt(contentKey, nonce, associatedData, plaintext.Span);
            var ciphertextLength = encrypted.Length - Aead.TagSize;
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
            var signature = Signature.Sign(signingKey, CanonicalEnvelopeEncoding.EncodeForSignature(toSign));
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

        PublicKey senderSigningKey;
        try
        {
            senderSigningKey = PublicKey.Import(Signature, sender.SigningPublicKey.Span, KeyBlobFormat.RawPublicKey);
        }
        catch (FormatException)
        {
            throw new ChatCryptographicException("Sender public key is invalid.");
        }

        if (!Signature.Verify(senderSigningKey, CanonicalEnvelopeEncoding.EncodeForSignature(envelope), envelope.Signature.Span))
        {
            throw new ChatCryptographicException("Envelope signature verification failed.");
        }

        var material = await LoadRequiredAsync(envelope.RecipientDeviceId, cancellationToken).ConfigureAwait(false);
        if (material.KeyId != envelope.RecipientEncryptionKeyId)
        {
            throw new ChatCryptographicException("Recipient key does not match the envelope.");
        }

        var encryptionPrivate = material.ExportEncryptionPrivateKey();
        try
        {
            using var recipientKey = Key.Import(Agreement, encryptionPrivate, KeyBlobFormat.NSecPrivateKey);
            var ephemeralPublic = PublicKey.Import(Agreement, envelope.EphemeralPublicKey.Span, KeyBlobFormat.RawPublicKey);
            using var sharedSecret = Agreement.Agree(recipientKey, ephemeralPublic) ??
                throw new ChatCryptographicException("Key agreement failed.");
            var associatedData = CanonicalEnvelopeEncoding.EncodeAssociatedData(envelope);
            using var contentKey = Kdf.DeriveKey(sharedSecret, envelope.Nonce.Span, associatedData, Aead);
            var encrypted = new byte[envelope.Ciphertext.Length + envelope.AuthenticationTag.Length];
            envelope.Ciphertext.Span.CopyTo(encrypted);
            envelope.AuthenticationTag.Span.CopyTo(encrypted.AsSpan(envelope.Ciphertext.Length));
            return Aead.Decrypt(contentKey, envelope.Nonce.Span, associatedData, encrypted) ??
                throw new ChatCryptographicException("Envelope authentication failed.");
        }
        catch (FormatException)
        {
            throw new ChatCryptographicException("Cryptographic key encoding is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionPrivate);
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
