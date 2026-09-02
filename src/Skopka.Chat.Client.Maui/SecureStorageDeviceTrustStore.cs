using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Maui.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Maui;

/// <summary>Raised for unavailable or corrupt platform trust storage without exposing public-key bytes.</summary>
public sealed class DeviceTrustStorageException : Exception
{
    /// <summary>Creates a bounded trust-storage failure.</summary>
    public DeviceTrustStorageException(string message) : base(message)
    {
    }
}

/// <summary>Persists small user-approved device trust records in injected MAUI secure storage.</summary>
public sealed class SecureStorageDeviceTrustStore : IChatDeviceTrustStore
{
    private const uint Magic = 0x534B5452;
    private const byte FormatVersion = 1;
    private const int PayloadBytes = 126;
    private readonly ISecureStorage _secureStorage;
    private readonly UserId _currentUserId;
    private readonly string _prefix;

    /// <summary>Creates a trust store isolated to one authenticated local user.</summary>
    public SecureStorageDeviceTrustStore(ISecureStorage secureStorage, UserId currentUserId)
    {
        _secureStorage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        if (currentUserId.Value == Guid.Empty)
        {
            throw new ArgumentException("User ID must not be empty.", nameof(currentUserId));
        }

        _currentUserId = currentUserId;
        _prefix = $"skopka.chat.trust.v1.{currentUserId.Value:N}.";
    }

    /// <inheritdoc />
    public async ValueTask<ChatDeviceTrustRecord?> LoadAsync(
        UserId userId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        string? encoded;
        try
        {
            encoded = await _secureStorage.GetAsync(StorageKey(userId, deviceId))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Unavailable();
        }

        if (encoded is null)
        {
            return null;
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
            if (payload.Length != PayloadBytes || BinaryPrimitives.ReadUInt32BigEndian(payload) != Magic ||
                payload[4] != FormatVersion)
            {
                throw Corrupt();
            }

            var storedUserId = new UserId(new Guid(payload.AsSpan(5, 16), bigEndian: true));
            var storedDeviceId = new DeviceId(new Guid(payload.AsSpan(21, 16), bigEndian: true));
            var keyId = new KeyId(new Guid(payload.AsSpan(37, 16), bigEndian: true));
            var state = (ChatDeviceTrustState)payload[53];
            DateTimeOffset recordedAt;
            try
            {
                recordedAt = new DateTimeOffset(
                    BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(54, 8)),
                    TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw Corrupt();
            }

            if (storedUserId != userId || storedDeviceId != deviceId || keyId.Value == Guid.Empty ||
                state is < ChatDeviceTrustState.Unknown or > ChatDeviceTrustState.Revoked || recordedAt == default)
            {
                throw Corrupt();
            }

            try
            {
                return new ChatDeviceTrustRecord(
                    storedUserId,
                    storedDeviceId,
                    keyId,
                    payload.AsSpan(62, ProtocolLimits.X25519PublicKeyBytes),
                    payload.AsSpan(94, ProtocolLimits.Ed25519PublicKeyBytes),
                    state,
                    recordedAt);
            }
            catch (ArgumentException)
            {
                throw Corrupt();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(
        ChatDeviceTrustRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIds(record.UserId, record.DeviceId);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = new byte[PayloadBytes];
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload, Magic);
            payload[4] = FormatVersion;
            WriteGuid(payload.AsSpan(5, 16), record.UserId.Value);
            WriteGuid(payload.AsSpan(21, 16), record.DeviceId.Value);
            WriteGuid(payload.AsSpan(37, 16), record.KeyId.Value);
            payload[53] = (byte)record.State;
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(54, 8), record.RecordedAt.UtcTicks);
            record.EncryptionPublicKey.Span.CopyTo(payload.AsSpan(62, ProtocolLimits.X25519PublicKeyBytes));
            record.SigningPublicKey.Span.CopyTo(payload.AsSpan(94, ProtocolLimits.Ed25519PublicKeyBytes));
            var encoded = Convert.ToBase64String(payload);
            try
            {
                await _secureStorage.SetAsync(StorageKey(record.UserId, record.DeviceId), encoded)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw Unavailable();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(
        UserId userId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, deviceId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _secureStorage.Remove(StorageKey(userId, deviceId));
            return ValueTask.CompletedTask;
        }
        catch (Exception)
        {
            throw Unavailable();
        }
    }

    private void ValidateIds(UserId userId, DeviceId deviceId)
    {
        if (userId.Value == Guid.Empty || deviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("Trust record identifiers must not be empty.");
        }

        if (_currentUserId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("The trust store session is invalid.");
        }
    }

    private string StorageKey(UserId userId, DeviceId deviceId) =>
        $"{_prefix}{userId.Value:N}.{deviceId.Value:N}";

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (value == Guid.Empty || !value.TryWriteBytes(destination, bigEndian: true, out var written) || written != 16)
        {
            throw new ArgumentException("Trust record contains an invalid identifier.");
        }
    }

    private static DeviceTrustStorageException Unavailable() =>
        new("Protected device trust storage is unavailable.");

    private static DeviceTrustStorageException Corrupt() =>
        new("Protected device trust storage is corrupt or incompatible.");
}
