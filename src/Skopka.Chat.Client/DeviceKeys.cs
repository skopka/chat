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
    /// <summary>Atomically creates keys only when absent. Persistent identity creation requires this capability.</summary>
    /// <remarks>Existing custom stores remain load-compatible; implement this method before creating new identities.</remarks>
    ValueTask<bool> TryCreateAsync(DeviceKeyMaterial material, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The key store does not support atomic identity creation.");

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
    public ValueTask<bool> TryCreateAsync(DeviceKeyMaterial material, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_keys.TryAdd(material.DeviceId, Clone(material)));
    }

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
    public ValueTask<PublicDevice> CreateAsync(
        UserId userId,
        DeviceId deviceId,
        DateTimeOffset registeredAt,
        CancellationToken cancellationToken = default) =>
        CreateAsync(userId, deviceId, KeyId.New(), registeredAt, cancellationToken);

    internal async ValueTask<PublicDevice> CreateAsync(UserId userId, DeviceId deviceId, KeyId keyId,
        DateTimeOffset registeredAt, CancellationToken cancellationToken)
    {
        if (userId.Value == Guid.Empty || deviceId.Value == Guid.Empty || keyId.Value == Guid.Empty || registeredAt == default)
        {
            throw new ArgumentException("User and device identifiers must not be empty.");
        }

        var parameters = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextArchiving };
        using var encryptionKey = Key.Create(Agreement, parameters);
        using var signingKey = Key.Create(Signature, parameters);
        var encryptionPrivate = encryptionKey.Export(KeyBlobFormat.NSecPrivateKey);
        var signingPrivate = signingKey.Export(KeyBlobFormat.NSecPrivateKey);

        try
        {
            var material = new DeviceKeyMaterial(userId, deviceId, keyId, encryptionPrivate, signingPrivate);
            if (!await _keyStore.TryCreateAsync(material, cancellationToken).ConfigureAwait(false))
            {
                throw new ChatCryptographicException("Device identity already exists; keys were not replaced.");
            }
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

    /// <summary>Loads an existing identity and derives its public record without replacing missing keys.</summary>
    public async ValueTask<PublicDevice?> LoadPublicAsync(
        UserId userId,
        DeviceId deviceId,
        DateTimeOffset registeredAt,
        CancellationToken cancellationToken = default)
    {
        if (userId.Value == Guid.Empty || deviceId.Value == Guid.Empty || registeredAt == default)
        {
            throw new ArgumentException("User, device and registration time are required.");
        }

        var material = await _keyStore.LoadAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (material is null)
        {
            return null;
        }

        return DerivePublic(material, userId, deviceId, registeredAt);
    }

    internal static PublicDevice DerivePublic(DeviceKeyMaterial material, UserId userId, DeviceId deviceId, DateTimeOffset registeredAt)
    {
        if (material.UserId != userId || material.DeviceId != deviceId || material.KeyId.Value == Guid.Empty)
        {
            throw new ChatCryptographicException("Stored device identity does not match the authenticated session.");
        }

        var encryptionPrivate = material.ExportEncryptionPrivateKey();
        var signingPrivate = material.ExportSigningPrivateKey();
        try
        {
            using var encryptionKey = Key.Import(Agreement, encryptionPrivate, KeyBlobFormat.NSecPrivateKey);
            using var signingKey = Key.Import(Signature, signingPrivate, KeyBlobFormat.NSecPrivateKey);
            var device = new PublicDevice(
                userId,
                deviceId,
                material.KeyId,
                encryptionKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
                signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
                registeredAt);
            ProtocolValidator.Validate(device);
            return device;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            throw new ChatCryptographicException("Stored device identity is corrupt or incompatible.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionPrivate);
            CryptographicOperations.ZeroMemory(signingPrivate);
        }
    }
}
