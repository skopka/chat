using System.Buffers.Binary;
using Microsoft.Maui.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Maui;

/// <summary>Bounded versioned persistent metadata over injected SecureStorage and a cross-process lease provider.</summary>
public sealed class SecureStorageDeviceIdentityStore(ISecureStorage secureStorage, IIdentityStorageLock storageLock) : IDeviceIdentityMetadataStore
{
    private readonly ISecureStorage _storage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
    private readonly IIdentityStorageLock _lock = storageLock ?? throw new ArgumentNullException(nameof(storageLock));
    /// <inheritdoc />
    public async ValueTask<IDeviceIdentityLease> AcquireAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var lease = await _lock.AcquireAsync(scope.StoragePartition, cancellationToken).ConfigureAwait(false);
        return new Lease(_storage, scope, lease);
    }

    private sealed class Lease(ISecureStorage storage, DeviceIdentityScope scope, IAsyncDisposable storageLease) : IDeviceIdentityLease
    {
        private const int PayloadLength = 154;
        private const uint Magic = 0x534B4944;
        private readonly string _key = "skopka.chat.identity.v1." + scope.StoragePartition;
        private bool _disposed;
        public async ValueTask<DeviceIdentityMetadata?> ReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? encoded;
            try { encoded = await storage.GetAsync(_key).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { throw new DeviceIdentityStorageException(PersistentDeviceIdentityState.Unavailable); }
            if (encoded is null) { return null; }
            if (encoded.Length != ((PayloadLength + 2) / 3) * 4) { throw Corrupt(); }
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length != PayloadLength || BinaryPrimitives.ReadUInt32BigEndian(bytes) != Magic || bytes[4] != 1 ||
                    (bytes[5] & ~7) != 0 || !bytes.AsSpan(6, 32).SequenceEqual(Convert.FromHexString(scope.StoragePartition))) { throw Corrupt(); }
                var device = new DeviceId(new Guid(bytes.AsSpan(38, 16), bigEndian: true));
                var key = new KeyId(new Guid(bytes.AsSpan(54, 16), bigEndian: true));
                var created = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(70)));
                var registered = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(78)));
                // Four reserved bytes must remain zero; they are not a hidden format extension.
                if (device.Value == Guid.Empty || key.Value == Guid.Empty || created == default ||
                    BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(86)) != 0) { throw Corrupt(); }
                PublicDevice? publicDevice = null;
                if ((bytes[5] & 1) != 0)
                {
                    publicDevice = new PublicDevice(scope.UserId, device, key, bytes.AsSpan(90, 32), bytes.AsSpan(122, 32), registered);
                    ProtocolValidator.Validate(publicDevice);
                }
                else if (bytes.AsSpan(78).IndexOfAnyExcept((byte)0) >= 0 || (bytes[5] & 2) != 0) { throw Corrupt(); }
                return new DeviceIdentityMetadata(1, scope, device, key, created, publicDevice, (bytes[5] & 2) != 0, (bytes[5] & 4) != 0);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
            {
                throw Corrupt();
            }
        }
        public async ValueTask WriteAsync(DeviceIdentityMetadata metadata, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(metadata);
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata.Version != 1 || metadata.Scope.StoragePartition != scope.StoragePartition || metadata.DeviceId.Value == Guid.Empty ||
                metadata.KeyId.Value == Guid.Empty || metadata.CreatedAt == default || (metadata.Registered && metadata.PublicDevice is null)) { throw Corrupt(); }
            var bytes = new byte[PayloadLength];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, Magic);
            bytes[4] = 1;
            bytes[5] = (byte)((metadata.PublicDevice is null ? 0 : 1) | (metadata.Registered ? 2 : 0) | (metadata.Revoked ? 4 : 0));
            Convert.FromHexString(scope.StoragePartition).CopyTo(bytes, 6);
            metadata.DeviceId.Value.TryWriteBytes(bytes.AsSpan(38, 16), bigEndian: true, out _);
            metadata.KeyId.Value.TryWriteBytes(bytes.AsSpan(54, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(70), metadata.CreatedAt.ToUnixTimeMilliseconds());
            if (metadata.PublicDevice is { } device)
            {
                ProtocolValidator.Validate(device);
                if (device.UserId != scope.UserId || device.DeviceId != metadata.DeviceId || device.KeyId != metadata.KeyId || device.IsRevoked) { throw Corrupt(); }
                BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(78), device.RegisteredAt.ToUnixTimeMilliseconds());
                device.EncryptionPublicKey.Span.CopyTo(bytes.AsSpan(90, 32));
                device.SigningPublicKey.Span.CopyTo(bytes.AsSpan(122, 32));
            }
            try
            {
                // SecureStorage writes have no cancellation. Do not release the lease while one can still commit.
                await storage.SetAsync(_key, Convert.ToBase64String(bytes)).ConfigureAwait(false);
            }
            catch (Exception) { throw new DeviceIdentityStorageException(PersistentDeviceIdentityState.Unavailable); }
            cancellationToken.ThrowIfCancellationRequested();
        }
        public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try { storage.Remove(_key); }
            catch (Exception) { throw new DeviceIdentityStorageException(PersistentDeviceIdentityState.Unavailable); }
            return ValueTask.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) { return; }
            _disposed = true;
            await storageLease.DisposeAsync().ConfigureAwait(false);
        }
        private static DeviceIdentityStorageException Corrupt() => new(PersistentDeviceIdentityState.Corrupt);
    }
}
