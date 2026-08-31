namespace Skopka.Chat.Protocol;

/// <summary>Protocol format versions understood by this package.</summary>
public static class ProtocolVersions
{
    /// <summary>The constrained E2EE MVP wire format.</summary>
    public const int V1 = 1;

    /// <summary>The version emitted by this package.</summary>
    public const int Current = V1;
}

/// <summary>Hard protocol limits applied before storage or cryptographic work.</summary>
public static class ProtocolLimits
{
    /// <summary>Maximum UTF-8 plaintext size before encryption.</summary>
    public const int MaxPlaintextBytes = 64 * 1024;

    /// <summary>Maximum ciphertext size excluding the authentication tag.</summary>
    public const int MaxCiphertextBytes = MaxPlaintextBytes;

    /// <summary>X25519 public key size.</summary>
    public const int X25519PublicKeyBytes = 32;

    /// <summary>Ed25519 public key size.</summary>
    public const int Ed25519PublicKeyBytes = 32;

    /// <summary>XChaCha20-Poly1305 nonce size.</summary>
    public const int NonceBytes = 24;

    /// <summary>Poly1305 tag size.</summary>
    public const int AuthenticationTagBytes = 16;

    /// <summary>Ed25519 signature size.</summary>
    public const int SignatureBytes = 64;

    /// <summary>Maximum delivery batch accepted by the server engine.</summary>
    public const int MaxDeliveryBatch = 100;

    /// <summary>Maximum retention period accepted for a message.</summary>
    public static readonly TimeSpan MaxRetention = TimeSpan.FromDays(30);
}
