using System.Collections.Concurrent;
using System.Security.Cryptography;
using NSec.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Opaque persisted private-key material for one device. Its string form is always redacted.</summary>
public sealed class DeviceKeyMaterial
{
    private readonly byte[] _encryptionPrivateKey;
    private readonly byte[] _signingPrivateKey;

    /// <summary>Creates material for a custom secure key-store implementation.</summary>
    public DeviceKeyMaterial(
        UserId userId,
        DeviceId deviceId,
        KeyId keyId,
        ReadOnlySpan<byte> encryptionPrivateKey,
        ReadOnlySpan<byte> signingPrivateKey)
    {
        UserId = userId;
        DeviceId = deviceId;
        KeyId = keyId;
        _encryptionPrivateKey = encryptionPrivateKey.ToArray();
        _signingPrivateKey = signingPrivateKey.ToArray();
    }

    /// <summary>Owning user identifier.</summary>
    public UserId UserId { get; }

    /// <summary>Owning device identifier.</summary>
    public DeviceId DeviceId { get; }

    /// <summary>Key version.</summary>
    public KeyId KeyId { get; }

    /// <summary>Explicitly exports a defensive copy for a secure-store implementation.</summary>
    public byte[] ExportEncryptionPrivateKey() => _encryptionPrivateKey.ToArray();

    /// <summary>Explicitly exports a defensive copy for a secure-store implementation.</summary>
    public byte[] ExportSigningPrivateKey() => _signingPrivateKey.ToArray();

    /// <inheritdoc />
    public override string ToString() => $"DeviceKeyMaterial(DeviceId={DeviceId}, KeyId={KeyId}, PrivateKeys=[REDACTED])";
}

/// <summary>Host-provided protected storage for private device keys.</summary>
public interface IDeviceKeyStore
{
    /// <summary>Saves or atomically replaces one device key record.</summary>
    ValueTask SaveAsync(DeviceKeyMaterial material, CancellationToken cancellationToken = default);

    /// <summary>Loads one device key record, or null when absent.</summary>
    ValueTask<DeviceKeyMaterial?> LoadAsync(DeviceId deviceId, CancellationToken cancellationToken = default);

    /// <summary>Deletes one device key record.</summary>
    ValueTask DeleteAsync(DeviceId deviceId, CancellationToken cancellationToken = default);
}

/// <summary>Non-persistent key store for tests and samples only.</summary>
public sealed class InMemoryDeviceKeyStore : IDeviceKeyStore
{
    private readonly ConcurrentDictionary<DeviceId, DeviceKeyMaterial> _keys = new();

    /// <inheritdoc />
    public ValueTask SaveAsync(DeviceKeyMaterial material, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        cancellationToken.ThrowIfCancellationRequested();
        _keys[material.DeviceId] = Clone(material);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<DeviceKeyMaterial?> LoadAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_keys.TryGetValue(deviceId, out var material) ? Clone(material) : null);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.TryRemove(deviceId, out _);
        return ValueTask.CompletedTask;
    }

    private static DeviceKeyMaterial Clone(DeviceKeyMaterial material)
    {
        var encryption = material.ExportEncryptionPrivateKey();
        var signing = material.ExportSigningPrivateKey();
        try
        {
            return new DeviceKeyMaterial(material.UserId, material.DeviceId, material.KeyId, encryption, signing);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryption);
            CryptographicOperations.ZeroMemory(signing);
        }
    }
}

/// <summary>Creates independent device identities using NSec-managed keys.</summary>
public sealed class DeviceIdentityService
{
    private static readonly KeyAgreementAlgorithm Agreement = KeyAgreementAlgorithm.X25519;
    private static readonly SignatureAlgorithm Signature = SignatureAlgorithm.Ed25519;
    private readonly IDeviceKeyStore _keyStore;

    /// <summary>Creates an identity service over a host-selected secure store.</summary>
    public DeviceIdentityService(IDeviceKeyStore keyStore) =>
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));

    /// <summary>Generates, persists and returns public data for a new device.</summary>
    public async ValueTask<PublicDevice> CreateAsync(
        UserId userId,
        DeviceId deviceId,
        DateTimeOffset registeredAt,
        CancellationToken cancellationToken = default)
    {
        if (userId.Value == Guid.Empty || deviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("User and device identifiers must not be empty.");
        }

        var parameters = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextArchiving };
        using var encryptionKey = Key.Create(Agreement, parameters);
        using var signingKey = Key.Create(Signature, parameters);
        var keyId = KeyId.New();
        var encryptionPrivate = encryptionKey.Export(KeyBlobFormat.NSecPrivateKey);
        var signingPrivate = signingKey.Export(KeyBlobFormat.NSecPrivateKey);

        try
        {
            var material = new DeviceKeyMaterial(userId, deviceId, keyId, encryptionPrivate, signingPrivate);
            await _keyStore.SaveAsync(material, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionPrivate);
            CryptographicOperations.ZeroMemory(signingPrivate);
        }

        var device = new PublicDevice(
            userId,
            deviceId,
            keyId,
            encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            registeredAt);
        ProtocolValidator.Validate(device);
        return device;
    }
}
