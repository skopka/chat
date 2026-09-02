using System.Security.Cryptography;
using System.Text;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Permanent identity namespace. InstallationId is a host-persisted random identifier, never hardware data.</summary>
public sealed class DeviceIdentityScope
{
    /// <summary>Creates a service/account/installation scope, independent of any login session.</summary>
    public DeviceIdentityScope(string serviceId, UserId userId, Guid installationId)
    {
        DeviceBindingEncoding.ValidateReference(serviceId);
        if (userId.Value == Guid.Empty || installationId == Guid.Empty) { throw new ArgumentException("Invalid identity scope."); }
        ServiceId = serviceId;
        UserId = userId;
        InstallationId = installationId;
        // GUID suffixes are fixed width; the service is the sole variable-width prefix.
        StoragePartition = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"skopka.chat.identity.v1\0{serviceId}\0{userId.Value:N}{installationId:N}")));
    }
    /// <summary>Exact configured service identifier.</summary>
    public string ServiceId { get; }
    /// <summary>Stable account.</summary>
    public UserId UserId { get; }
    /// <summary>Random, persistent installation identifier supplied by the host.</summary>
    public Guid InstallationId { get; }
    /// <summary>Stable opaque namespace for protected metadata and local history; contains no session ID.</summary>
    public string StoragePartition { get; }
    /// <inheritdoc />
    public override string ToString() => "DeviceIdentityScope([REDACTED])";
}

/// <summary>Local identity state; non-ready states never trigger implicit key replacement.</summary>
public enum PersistentDeviceIdentityState
{
    /// <summary>No identity metadata exists.</summary>
    Absent,
    /// <summary>Metadata and both private keys agree.</summary>
    Ready,
    /// <summary>Metadata exists but keys are missing; explicit recovery/forgetting is required.</summary>
    RecoveryRequired,
    /// <summary>Metadata or keys are corrupt/inconsistent.</summary>
    Corrupt,
    /// <summary>Protected storage or its initialization lock is unavailable.</summary>
    Unavailable,
    /// <summary>The server has reported revocation of this identity.</summary>
    Revoked
}

/// <summary>Generic, typed protected-storage failure; raw platform exceptions must not be attached.</summary>
public class DeviceIdentityStorageException : Exception
{
    /// <summary>Creates a bounded storage failure.</summary>
    public DeviceIdentityStorageException(PersistentDeviceIdentityState state) : this(state, "Protected device identity storage failed.") { }
    /// <summary>For compatibility with existing adapters; callers must supply only a generic message.</summary>
    protected DeviceIdentityStorageException(PersistentDeviceIdentityState state, string message) : base(message) => State = state;
    /// <summary>Corrupt or unavailable, never a signal to replace keys.</summary>
    public PersistentDeviceIdentityState State { get; }
}

/// <summary>Versioned protected metadata. A null public record is a durable pre-key creation reservation.</summary>
public sealed record DeviceIdentityMetadata(int Version, DeviceIdentityScope Scope, DeviceId DeviceId, KeyId KeyId,
    DateTimeOffset CreatedAt, PublicDevice? PublicDevice, bool Registered, bool Revoked)
{
    /// <inheritdoc />
    public override string ToString() => "DeviceIdentityMetadata([REDACTED])";
}

/// <summary>Result of loading identity without creating/replacing key material.</summary>
public sealed record PersistentDeviceIdentityResult(PersistentDeviceIdentityState State, DeviceIdentityMetadata? Metadata);

/// <summary>Exclusive, crash-released scope lease. All state writers must cooperate with the same lock.</summary>
public interface IDeviceIdentityLease : IAsyncDisposable
{
    /// <summary>Reads at most one bounded protected metadata record.</summary>
    ValueTask<DeviceIdentityMetadata?> ReadAsync(CancellationToken cancellationToken = default);
    /// <summary>Atomically replaces the scoped metadata record; uncertain completion must remain recoverable.</summary>
    ValueTask WriteAsync(DeviceIdentityMetadata metadata, CancellationToken cancellationToken = default);
    /// <summary>Deletes local metadata only; does not revoke the remote device.</summary>
    ValueTask DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Protected metadata store with an exclusive initialization lease spanning key and metadata writes.</summary>
public interface IDeviceIdentityMetadataStore
{
    /// <summary>Acquires a bounded, cancellation-aware cross-writer scope lease.</summary>
    ValueTask<IDeviceIdentityLease> AcquireAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Persistent device lifecycle. Logout does not call ForgetLocalAsync.</summary>
public sealed class PersistentDeviceIdentityService
{
    private readonly IDeviceKeyStore _keys;
    private readonly IDeviceIdentityMetadataStore _metadata;
    private readonly TimeProvider _time;
    private readonly DeviceIdentityService _identity;

    /// <summary>Uses the default native provider.</summary>
    public PersistentDeviceIdentityService(IDeviceKeyStore keys, IDeviceIdentityMetadataStore metadata, TimeProvider timeProvider)
        : this(keys, metadata, timeProvider, ChatCryptographyDefaults.Create()) { }

    /// <summary>Uses the selected endpoint cryptography with the same create-only lifecycle.</summary>
    public PersistentDeviceIdentityService(IDeviceKeyStore keys, IDeviceIdentityMetadataStore metadata, TimeProvider timeProvider,
        IChatCryptographyProvider cryptography)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _identity = new DeviceIdentityService(keys, cryptography);
    }

    /// <summary>Loads and validates both keys; finalizes a reservation only if its exact keys survived the crash.</summary>
    public async ValueTask<PersistentDeviceIdentityResult> LoadAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            await using var lease = await _metadata.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
            return await LoadAsync(scope, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceIdentityStorageException exception)
        {
            return new(exception.State, null);
        }
        catch (ChatCryptographicException)
        {
            return new(PersistentDeviceIdentityState.Corrupt, null);
        }
    }

    /// <summary>Explicitly creates an absent identity, or returns the existing state without replacement.</summary>
    public async ValueTask<PersistentDeviceIdentityResult> CreateAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            await using var lease = await _metadata.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
            var existing = await LoadAsync(scope, lease, cancellationToken).ConfigureAwait(false);
            if (existing.State != PersistentDeviceIdentityState.Absent) { return existing; }
            var reserved = new DeviceIdentityMetadata(1, scope, DeviceId.New(), KeyId.New(),
                DeviceBindingEncoding.NormalizeTime(_time.GetUtcNow()), null, false, false);
            await lease.WriteAsync(reserved, cancellationToken).ConfigureAwait(false);
            var device = await _identity.CreateAsync(scope.UserId, reserved.DeviceId, reserved.KeyId,
                reserved.CreatedAt, cancellationToken).ConfigureAwait(false);
            var ready = reserved with { PublicDevice = device };
            await lease.WriteAsync(ready, cancellationToken).ConfigureAwait(false);
            return new(PersistentDeviceIdentityState.Ready, ready);
        }
        catch (DeviceIdentityStorageException exception) { return new(exception.State, null); }
        catch (ChatCryptographicException) { return new(PersistentDeviceIdentityState.Corrupt, null); }
    }

    /// <summary>Explicitly adopts existing keys (including an old sid-shaped DeviceId); never changes those keys.</summary>
    public async ValueTask<PersistentDeviceIdentityResult> AdoptAsync(DeviceIdentityScope scope, PublicDevice legacyDevice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(legacyDevice);
        ProtocolValidator.Validate(legacyDevice);
        if (legacyDevice.UserId != scope.UserId || legacyDevice.IsRevoked) { throw new ArgumentException("Invalid legacy identity."); }
        await using var lease = await _metadata.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
        var existing = await LoadAsync(scope, lease, cancellationToken).ConfigureAwait(false);
        if (existing.State != PersistentDeviceIdentityState.Absent) { return existing; }
        var loaded = await _identity.LoadPublicAsync(scope.UserId, legacyDevice.DeviceId,
            legacyDevice.RegisteredAt, cancellationToken).ConfigureAwait(false);
        if (loaded is null) { return new(PersistentDeviceIdentityState.RecoveryRequired, null); }
        if (!DeviceBindingEncoding.SameKeys(loaded, legacyDevice)) { return new(PersistentDeviceIdentityState.Corrupt, null); }
        var adopted = new DeviceIdentityMetadata(1, scope, loaded.DeviceId, loaded.KeyId, loaded.RegisteredAt, loaded, true, false);
        await lease.WriteAsync(adopted, cancellationToken).ConfigureAwait(false);
        return new(PersistentDeviceIdentityState.Ready, adopted);
    }

    /// <summary>Explicitly copies retained legacy keys into the scoped store without replacing keys or deleting the source.</summary>
    public async ValueTask<PersistentDeviceIdentityResult> ImportLegacyAsync(DeviceIdentityScope scope, PublicDevice legacyDevice,
        IDeviceKeyStore legacyKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(legacyDevice);
        ArgumentNullException.ThrowIfNull(legacyKeys);
        ProtocolValidator.Validate(legacyDevice);
        if (legacyDevice.UserId != scope.UserId || legacyDevice.IsRevoked) { throw new ArgumentException("Invalid legacy identity."); }
        try
        {
            await using var lease = await _metadata.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
            var existing = await LoadAsync(scope, lease, cancellationToken).ConfigureAwait(false);
            if (existing.State != PersistentDeviceIdentityState.Absent &&
                !(existing.State == PersistentDeviceIdentityState.RecoveryRequired && existing.Metadata?.PublicDevice is { } reserved &&
                  DeviceBindingEncoding.SameKeys(reserved, legacyDevice))) { return existing; }
            var material = await legacyKeys.LoadAsync(legacyDevice.DeviceId, cancellationToken).ConfigureAwait(false);
            if (material is null) { return new(PersistentDeviceIdentityState.RecoveryRequired, existing.Metadata); }
            var loaded = _identity.DerivePublic(material, scope.UserId, legacyDevice.DeviceId, legacyDevice.RegisteredAt);
            if (!DeviceBindingEncoding.SameKeys(loaded, legacyDevice)) { return new(PersistentDeviceIdentityState.Corrupt, existing.Metadata); }
            // Persist intent first: an interrupted import can only resume with these exact retained keys.
            var intent = new DeviceIdentityMetadata(1, scope, loaded.DeviceId, loaded.KeyId, loaded.RegisteredAt, loaded, true, false);
            await lease.WriteAsync(intent, cancellationToken).ConfigureAwait(false);
            _ = await _keys.TryCreateAsync(material, cancellationToken).ConfigureAwait(false);
            return await LoadAsync(scope, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceIdentityStorageException exception) { return new(exception.State, null); }
        catch (ChatCryptographicException) { return new(PersistentDeviceIdentityState.Corrupt, null); }
    }

    /// <summary>Records server registration metadata after a verified successful binding.</summary>
    public async ValueTask MarkRegisteredAsync(DeviceIdentityScope scope, DeviceSessionBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        await using var lease = await _metadata.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
        var current = await LoadAsync(scope, lease, cancellationToken).ConfigureAwait(false);
        if (current.State != PersistentDeviceIdentityState.Ready || current.Metadata?.PublicDevice is not { } device ||
            scope.ServiceId != binding.Context.ServiceId || scope.UserId != binding.Context.UserId ||
            !DeviceBindingEncoding.SameKeys(device, binding.Device) || binding.Device.IsRevoked)
        {
            throw new ChatCryptographicException("Device binding does not match the persistent identity.");
        }
        await lease.WriteAsync(current.Metadata with { PublicDevice = binding.Device, Registered = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Remembers an explicit authenticated server revocation response; cannot undo revocation.</summary>
    public async ValueTask RememberRevokedAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
    {
        await using var lease = await _metadata.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
        var current = await lease.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null) { await lease.WriteAsync(current with { Revoked = true }, cancellationToken).ConfigureAwait(false); }
    }

    /// <summary>Explicit local deletion only. No remote revocation or secure history erasure is implied.</summary>
    public async ValueTask ForgetLocalAsync(DeviceIdentityScope scope, CancellationToken cancellationToken = default)
    {
        await using var lease = await _metadata.AcquireAsync(scope, cancellationToken).ConfigureAwait(false);
        var current = await lease.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null) { await _keys.DeleteAsync(current.DeviceId, cancellationToken).ConfigureAwait(false); }
        await lease.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PersistentDeviceIdentityResult> LoadAsync(DeviceIdentityScope scope, IDeviceIdentityLease lease, CancellationToken cancellationToken)
    {
        var record = await lease.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (record is null) { return new(PersistentDeviceIdentityState.Absent, null); }
        if (record.Version != 1 || record.Scope.StoragePartition != scope.StoragePartition || record.DeviceId.Value == Guid.Empty ||
            record.KeyId.Value == Guid.Empty || record.CreatedAt == default || (record.Registered && record.PublicDevice is null))
        {
            return new(PersistentDeviceIdentityState.Corrupt, null);
        }
        if (record.Revoked) { return new(PersistentDeviceIdentityState.Revoked, record); }
        var device = await _identity.LoadPublicAsync(scope.UserId, record.DeviceId,
            record.PublicDevice?.RegisteredAt ?? record.CreatedAt, cancellationToken).ConfigureAwait(false);
        if (device is null) { return new(PersistentDeviceIdentityState.RecoveryRequired, record); }
        if (device.KeyId != record.KeyId || (record.PublicDevice is not null && !DeviceBindingEncoding.SameKeys(device, record.PublicDevice)))
        {
            return new(PersistentDeviceIdentityState.Corrupt, record);
        }
        if (record.PublicDevice is null)
        {
            record = record with { PublicDevice = device };
            await lease.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }
        return new(PersistentDeviceIdentityState.Ready, record);
    }
}
