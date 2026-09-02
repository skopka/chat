using System.Security.Cryptography;
using NSec.Cryptography;

namespace Skopka.Chat.Client;

/// <summary>Native NSec implementation. Existing NSecPrivateKey records remain the default and load unchanged.</summary>
public sealed class NSecChatCryptography : IChatCryptographyProvider
{
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    /// <inheritdoc />
    public byte[] CreatePrivateKey(ChatKeyAlgorithm algorithm)
    {
        using var key = Key.Create(GetAlgorithm(algorithm), new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextArchiving });
        return key.Export(KeyBlobFormat.NSecPrivateKey);
    }

    /// <inheritdoc />
    public byte[] GetPublicKey(ChatKeyAlgorithm algorithm, ReadOnlySpan<byte> privateKey)
    {
        using var key = Import(algorithm, privateKey);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    /// <summary>Explicitly converts retained NSec material to portable v1 without changing its key or source store.</summary>
    public static byte[] ExportPortablePrivateKey(ChatKeyAlgorithm algorithm, ReadOnlySpan<byte> privateKey)
    {
        using var key = Import(algorithm, privateKey, true);
        var raw = key.Export(KeyBlobFormat.RawPrivateKey);
        try { return PortableChatPrivateKey.Encode(algorithm, raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    /// <inheritdoc />
    public byte[] DeriveEnvelopeKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        using var key = Import(ChatKeyAlgorithm.X25519, privateKey);
        var peer = PublicKey.Import(KeyAgreementAlgorithm.X25519, publicKey, KeyBlobFormat.RawPublicKey);
        using var shared = KeyAgreementAlgorithm.X25519.Agree(key, peer) ?? throw new ChatCryptographicException("Key agreement failed.");
        return KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, salt, info, 32);
    }

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        using var imported = Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey);
        return Aead.Encrypt(imported, nonce, associatedData, plaintext);
    }

    /// <inheritdoc />
    public byte[]? Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        using var imported = Key.Import(Aead, key, KeyBlobFormat.RawSymmetricKey);
        return Aead.Decrypt(imported, nonce, associatedData, ciphertext);
    }

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        using var key = Import(ChatKeyAlgorithm.Ed25519, privateKey);
        return SignatureAlgorithm.Ed25519.Sign(key, message);
    }

    /// <inheritdoc />
    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        SignatureAlgorithm.Ed25519.Verify(PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey), message, signature);

    private static Algorithm GetAlgorithm(ChatKeyAlgorithm algorithm) => algorithm switch
    {
        ChatKeyAlgorithm.X25519 => KeyAgreementAlgorithm.X25519,
        ChatKeyAlgorithm.Ed25519 => SignatureAlgorithm.Ed25519,
        _ => throw new ChatCryptographicException("Private key purpose is invalid.")
    };

    private static Key Import(ChatKeyAlgorithm algorithm, ReadOnlySpan<byte> privateKey, bool exportable = false)
    {
        var parameters = new KeyCreationParameters { ExportPolicy = exportable ? KeyExportPolicies.AllowPlaintextArchiving : KeyExportPolicies.None };
        if (!PortableChatPrivateKey.IsPortable(privateKey))
        {
            return Key.Import(GetAlgorithm(algorithm), privateKey, KeyBlobFormat.NSecPrivateKey, parameters);
        }
        var raw = PortableChatPrivateKey.Decode(algorithm, privateKey);
        try { return Key.Import(GetAlgorithm(algorithm), raw, KeyBlobFormat.RawPrivateKey, parameters); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
}
