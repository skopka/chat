using System.Security.Cryptography;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Browser;

/// <summary>Create-only encrypted IndexedDB keys and leased identity metadata for one unlocked account vault.</summary>
public sealed class BrowserDeviceIdentityStore(BrowserVault vault) : IDeviceKeyStore, IDeviceIdentityMetadataStore
{
    private readonly BrowserVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));

    /// <inheritdoc />
    public async ValueTask<bool> TryCreateAsync(DeviceKeyMaterial material, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (material.UserId != _vault.Scope.UserId) { throw new BrowserStorageException("corrupt"); }
        var record = new BrowserKeyRecord(1, material.UserId.Value, material.DeviceId.Value, material.KeyId.Value,
            material.ExportEncryptionPrivateKey(), material.ExportSigningPrivateKey());
        try
        {
            var encoded = BrowserStoreEncoding.Encode(record, BrowserStoreJson.Default.BrowserKeyRecord);
            try { return await _vault.WriteAsync("keys", BrowserStoreEncoding.Id(material.DeviceId.Value), "", encoded, null, cancellationToken).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        finally { CryptographicOperations.ZeroMemory(record.Encryption); CryptographicOperations.ZeroMemory(record.Signing); }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(DeviceKeyMaterial material, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Browser device keys are create-only.");

    /// <inheritdoc />
    public async ValueTask<DeviceKeyMaterial?> LoadAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var stored = await _vault.ReadAsync("keys", BrowserStoreEncoding.Id(deviceId.Value), cancellationToken).ConfigureAwait(false);
        if (stored.Status == "absent") { return null; }
        var record = BrowserStoreEncoding.Decode(stored.Data, BrowserStoreJson.Default.BrowserKeyRecord);
        try
        {
            BrowserStoreEncoding.Version(record.Version);
            if (record.User != _vault.Scope.UserId.Value || record.Device != deviceId.Value || record.Key == Guid.Empty ||
                record.Encryption.Length is < 1 or > 1024 || record.Signing.Length is < 1 or > 1024)
            { throw new BrowserStorageException("corrupt"); }
            return new DeviceKeyMaterial(_vault.Scope.UserId, deviceId, new KeyId(record.Key), record.Encryption, record.Signing);
        }
        finally { CryptographicOperations.ZeroMemory(record.Encryption); CryptographicOperations.ZeroMemory(record.Signing); }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var key = BrowserStoreEncoding.Id(deviceId.Value);
        var row = await _vault.ReadAsync("keys", key, cancellationToken).ConfigureAwait(false);
        if (row.Data is not null) { CryptographicOperations.ZeroMemory(row.Data); }
        await _vault.RemoveAsync("keys", key, row.Revision, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IDeviceIdentityLease> AcquireAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.StoragePartition != _vault.Scope.StoragePartition) { throw new BrowserStorageException("corrupt"); }
        return new IdentityLease(_vault, await _vault.AcquireAsync("identity", cancellationToken).ConfigureAwait(false));
    }

    private sealed class IdentityLease(BrowserVault vault, IAsyncDisposable lease) : IDeviceIdentityLease
    {
        public async ValueTask<DeviceIdentityMetadata?> ReadAsync(CancellationToken cancellationToken = default)
        {
            var row = await vault.ReadAsync("identity", "metadata", cancellationToken).ConfigureAwait(false);
            if (row.Status == "absent") { return null; }
            var record = BrowserStoreEncoding.Decode(row.Data, BrowserStoreJson.Default.BrowserIdentityRecord);
            BrowserStoreEncoding.Version(record.Version);
            if (record.Partition != vault.Scope.StoragePartition || record.Device == Guid.Empty || record.Key == Guid.Empty || record.CreatedAt == default)
            { throw new BrowserStorageException("corrupt"); }
            try
            {
                return new DeviceIdentityMetadata(1, vault.Scope, new DeviceId(record.Device), new KeyId(record.Key), record.CreatedAt,
                    record.PublicDevice?.ToDomain(), record.Registered, record.Revoked);
            }
            catch (Exception error) when (error is ArgumentException or FormatException) { throw new BrowserStorageException("corrupt"); }
        }

        public async ValueTask WriteAsync(DeviceIdentityMetadata metadata, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            if (metadata.Scope.StoragePartition != vault.Scope.StoragePartition || metadata.Version != 1) { throw new BrowserStorageException("corrupt"); }
            var previous = await vault.ReadAsync("identity", "metadata", cancellationToken).ConfigureAwait(false);
            if (previous.Data is not null) { CryptographicOperations.ZeroMemory(previous.Data); }
            var record = new BrowserIdentityRecord(1, metadata.Scope.StoragePartition, metadata.DeviceId.Value, metadata.KeyId.Value,
                metadata.CreatedAt, metadata.PublicDevice is null ? null : PublicDeviceResponse.FromDomain(metadata.PublicDevice), metadata.Registered, metadata.Revoked);
            var encoded = BrowserStoreEncoding.Encode(record, BrowserStoreJson.Default.BrowserIdentityRecord);
            try
            {
                if (!await vault.WriteAsync("identity", "metadata", "", encoded, previous.Revision, cancellationToken).ConfigureAwait(false))
                { throw new BrowserStorageException("conflict"); }
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        public async ValueTask DeleteAsync(CancellationToken cancellationToken = default)
        {
            var row = await vault.ReadAsync("identity", "metadata", cancellationToken).ConfigureAwait(false);
            if (row.Data is not null) { CryptographicOperations.ZeroMemory(row.Data); }
            await vault.RemoveAsync("identity", "metadata", row.Revision, cancellationToken).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}
