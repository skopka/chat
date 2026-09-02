using System.Security.Cryptography;

namespace Skopka.Chat.Client;

/// <summary>The two private-key purposes in protocol v1; not interchangeable.</summary>
public enum ChatKeyAlgorithm
{
    /// <summary>X25519 agreement private key.</summary>
    X25519 = 1,
    /// <summary>Ed25519 signing seed.</summary>
    Ed25519 = 2
}

/// <summary>Trusted endpoint primitive provider. Canonical protocol composition remains in Client.</summary>
/// <remarks>Providers must reject invalid keys, use a CSPRNG, and return only generic errors. Returned secrets belong to the caller and must be cleared.</remarks>
public interface IChatCryptographyProvider
{
    /// <summary>Creates an opaque encoded private key for the requested purpose.</summary>
    byte[] CreatePrivateKey(ChatKeyAlgorithm algorithm);
    /// <summary>Derives the raw 32-byte public key from persisted private material.</summary>
    byte[] GetPublicKey(ChatKeyAlgorithm algorithm, ReadOnlySpan<byte> privateKey);
    /// <summary>X25519 followed by HKDF-SHA256; returns a 32-byte key using exact salt and info.</summary>
    byte[] DeriveEnvelopeKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info);
    /// <summary>XChaCha20-Poly1305 encryption with the 16-byte tag appended.</summary>
    byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext);
    /// <summary>XChaCha20-Poly1305 authenticated decryption; null means authentication failure.</summary>
    byte[]? Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext);
    /// <summary>Signs exact bytes with Ed25519.</summary>
    byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message);
    /// <summary>Verifies exact bytes with an Ed25519 public key.</summary>
    bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);
}

/// <summary>Explicit portable private-key container v1. Not an encrypted backup or a wire format.</summary>
public static class PortableChatPrivateKey
{
    private static ReadOnlySpan<byte> Domain => "Skopka.Chat.PrivateKey\0\x01"u8;

    /// <summary>Wraps exactly 32 raw X25519 bytes or a 32-byte Ed25519 seed.</summary>
    public static byte[] Encode(ChatKeyAlgorithm algorithm, ReadOnlySpan<byte> rawKey)
    {
        if (algorithm is not (ChatKeyAlgorithm.X25519 or ChatKeyAlgorithm.Ed25519) || rawKey.Length != 32)
        {
            throw new ChatCryptographicException("Private key format is invalid.");
        }
        var result = new byte[Domain.Length + 1 + 32];
        Domain.CopyTo(result);
        result[Domain.Length] = (byte)algorithm;
        rawKey.CopyTo(result.AsSpan(Domain.Length + 1));
        return result;
    }

    /// <summary>Recognizes the exact container domain; unknown versions are not legacy keys.</summary>
    public static bool IsPortable(ReadOnlySpan<byte> encoded) => encoded.StartsWith("Skopka.Chat.PrivateKey\0"u8);

    /// <summary>Validates purpose/version/length and returns a raw secret copy; clear it after use.</summary>
    public static byte[] Decode(ChatKeyAlgorithm algorithm, ReadOnlySpan<byte> encoded)
    {
        if (algorithm is not (ChatKeyAlgorithm.X25519 or ChatKeyAlgorithm.Ed25519) ||
            encoded.Length != Domain.Length + 33 || !encoded.StartsWith(Domain) || encoded[Domain.Length] != (byte)algorithm)
        {
            throw new ChatCryptographicException("Private key format is invalid.");
        }
        return encoded[(Domain.Length + 1)..].ToArray();
    }
}

internal static class ChatCryptographyDefaults
{
    public static IChatCryptographyProvider Create()
    {
#if BROWSER
        throw new PlatformNotSupportedException("A browser cryptography provider must be supplied explicitly.");
#else
        return new NSecChatCryptography();
#endif
    }
}
