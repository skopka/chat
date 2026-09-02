using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Maui.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Maui;

/// <summary>Raised for unavailable or corrupt platform key storage without exposing key material.</summary>
public sealed class DeviceKeyStorageException : Exception
{
    /// <summary>Creates a bounded key-storage failure.</summary>
    public DeviceKeyStorageException(string message) : base(message)
    {
    }
}

/// <summary>Versioned private device-key persistence over an injected MAUI secure storage instance.</summary>
/// <remarks>
/// One instance is scoped to one authenticated user. Absence returns null; corruption fails closed and never
/// creates a replacement identity. MAUI secure storage is intended for small secrets, not message history.
/// </remarks>
public sealed class SecureStorageDeviceKeyStore : IDeviceKeyStore
{
    private const uint Magic = 0x534B434B;
    private const byte FormatVersion = 1;
    private const int MaximumPrivateKeyBytes = 256;
    private const int MaximumEncodedCharacters = 2_048;
    private readonly ISecureStorage _secureStorage;
    private readonly UserId _userId;
    private readonly string _keyPrefix;

    /// <summary>Creates a user-isolated adapter without using a global secure-storage singleton.</summary>
    public SecureStorageDeviceKeyStore(ISecureStorage secureStorage, UserId userId)
    {
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        if (userId.Value == Guid.Empty)
        {
            throw new ArgumentException("User ID must not be empty.", nameof(userId));
        }

        _userId = userId;
        _keyPrefix = $"skopka.chat.keys.v1.{userId.Value:N}.";
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(
        DeviceKeyMaterial material,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        cancellationToken.ThrowIfCancellationRequested();
        if (material.UserId != _userId || material.DeviceId.Value == Guid.Empty || material.KeyId.Value == Guid.Empty)
        {
            throw new ArgumentException("Device key material does not belong to this secure-storage session.", nameof(material));
        }

        var encryptionKey = material.ExportEncryptionPrivateKey();
        var signingKey = material.ExportSigningPrivateKey();
        byte[]? payload = null;
        try
        {
            if (encryptionKey.Length is < 1 or > MaximumPrivateKeyBytes ||
                signingKey.Length is < 1 or > MaximumPrivateKeyBytes)
            {
                throw new ArgumentException("Device key material has an unsupported format.", nameof(material));
            }

            payload = Encode(material, encryptionKey, signingKey);
            var encoded = Convert.ToBase64String(payload);
            if (encoded.Length > MaximumEncodedCharacters)
            {
                throw new ArgumentException("Device key material exceeds the secure-storage limit.", nameof(material));
            }

            try
            {
                await _secureStorage.SetAsync(StorageKey(material.DeviceId), encoded)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw new DeviceKeyStorageException("Protected device key storage is unavailable.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(signingKey);
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<DeviceKeyMaterial?> LoadAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        if (deviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("Device ID must not be empty.", nameof(deviceId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? encoded;
        try
        {
            encoded = await _secureStorage.GetAsync(StorageKey(deviceId))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new DeviceKeyStorageException("Protected device key storage is unavailable.");
        }

        if (encoded is null)
        {
            return null;
        }

        if (encoded.Length is 0 or > MaximumEncodedCharacters)
        {
            throw Corrupt();
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw Corrupt();
        }

        try
        {
            return Decode(payload, deviceId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("Device ID must not be empty.", nameof(deviceId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _secureStorage.Remove(StorageKey(deviceId));
            return ValueTask.CompletedTask;
        }
        catch (Exception)
        {
            throw new DeviceKeyStorageException("Protected device key storage is unavailable.");
        }
    }

    private static byte[] Encode(
        DeviceKeyMaterial material,
        ReadOnlySpan<byte> encryptionKey,
        ReadOnlySpan<byte> signingKey)
    {
        var payload = new byte[57 + encryptionKey.Length + signingKey.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, Magic);
        payload[4] = FormatVersion;
        WriteGuid(payload.AsSpan(5, 16), material.UserId.Value);
        WriteGuid(payload.AsSpan(21, 16), material.DeviceId.Value);
        WriteGuid(payload.AsSpan(37, 16), material.KeyId.Value);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(53, 2), checked((ushort)encryptionKey.Length));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(55, 2), checked((ushort)signingKey.Length));
        encryptionKey.CopyTo(payload.AsSpan(57));
        signingKey.CopyTo(payload.AsSpan(57 + encryptionKey.Length));
        return payload;
    }

    private DeviceKeyMaterial Decode(ReadOnlySpan<byte> payload, DeviceId requestedDeviceId)
    {
        if (payload.Length < 59 || BinaryPrimitives.ReadUInt32BigEndian(payload) != Magic ||
            payload[4] != FormatVersion)
        {
            throw Corrupt();
        }

        var storedUserId = new UserId(new Guid(payload.Slice(5, 16), bigEndian: true));
        var storedDeviceId = new DeviceId(new Guid(payload.Slice(21, 16), bigEndian: true));
        var storedKeyId = new KeyId(new Guid(payload.Slice(37, 16), bigEndian: true));
        var encryptionLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(53, 2));
        var signingLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(55, 2));
        if (storedUserId != _userId || storedDeviceId != requestedDeviceId || storedKeyId.Value == Guid.Empty ||
            encryptionLength is < 1 or > MaximumPrivateKeyBytes ||
            signingLength is < 1 or > MaximumPrivateKeyBytes ||
            payload.Length != 57 + encryptionLength + signingLength)
        {
            throw Corrupt();
        }

        var encryptionKey = payload.Slice(57, encryptionLength).ToArray();
        var signingKey = payload.Slice(57 + encryptionLength, signingLength).ToArray();
        try
        {
            return new DeviceKeyMaterial(
                storedUserId,
                storedDeviceId,
                storedKeyId,
                encryptionKey,
                signingKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    private string StorageKey(DeviceId deviceId) => $"{_keyPrefix}{deviceId.Value:N}";

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (value == Guid.Empty || !value.TryWriteBytes(destination, bigEndian: true, out var written) || written != 16)
        {
            throw new ArgumentException("Device key material contains an invalid identifier.");
        }
    }

    private static DeviceKeyStorageException Corrupt() =>
        new("Protected device key storage is corrupt or incompatible.");
}
