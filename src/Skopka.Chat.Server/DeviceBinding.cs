using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Public-key-only proof verification boundary; never imports device private keys.</summary>
public interface IDeviceProofVerifier
{
    /// <summary>Verifies the binding-specific canonical payload against its authoritative signing key.</summary>
    bool Verify(DeviceBindingChallenge challenge, DeviceBindingProof proof);
}

/// <summary>Transactional persistence for ownership challenges and authenticated session bindings.</summary>
public interface IDeviceBindingRepository
{
    /// <summary>Inserts a unique immutable challenge. False means identifier conflict.</summary>
    ValueTask<bool> TryAddChallengeAsync(DeviceBindingChallenge challenge, CancellationToken cancellationToken = default);
    /// <summary>Loads a bounded challenge, including consumed challenges retained for exact retry.</summary>
    ValueTask<DeviceBindingChallenge?> GetChallengeAsync(Guid challengeId, CancellationToken cancellationToken = default);
    /// <summary>
    /// After cryptographic verification, atomically rechecks stored payload/context/expiry/revocation,
    /// enrolls immutable keys when requested, consumes the challenge and binds the session.
    /// Exact completed retries return the original result only while authorization/device/binding remain valid.
    /// </summary>
    ValueTask<DeviceSessionBinding?> CompleteAsync(DeviceBindingChallenge verifiedChallenge, DeviceBindingProof proof,
        TimeProvider timeProvider, CancellationToken cancellationToken = default);
    /// <summary>Resolves a live exact context and currently active device; never trusts a stored binding alone.</summary>
    ValueTask<DeviceSessionBinding?> ResolveAsync(DeviceAuthorizationContext context, DateTimeOffset now,
        CancellationToken cancellationToken = default);
    /// <summary>Deletes at most maximumCount expired challenge/binding rows; never unbounded.</summary>
    ValueTask<int> CleanupAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default);
}

/// <summary>Bounded failure category; no remote payload, token or key is included.</summary>
public enum DeviceBindingFailure
{
    /// <summary>The proof, context or current state cannot authorize this operation.</summary>
    Rejected,
    /// <summary>The authenticated user's device was revoked and cannot be rebound.</summary>
    Revoked
}

/// <summary>Generic binding failure safe for application error handling.</summary>
public sealed class DeviceBindingException(DeviceBindingFailure failure) : Exception("Device binding was rejected.")
{
    /// <summary>Bounded machine-readable category.</summary>
    public DeviceBindingFailure Failure { get; } = failure;
}

/// <summary>Transport-independent challenge/response orchestration over an authenticated host context.</summary>
public sealed class DeviceBindingService(
    IDeviceRepository devices, IDeviceBindingRepository bindings, IDeviceProofVerifier verifier, TimeProvider timeProvider)
{
    private readonly IDeviceRepository _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    private readonly IDeviceBindingRepository _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    private readonly IDeviceProofVerifier _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Issues a two-minute challenge, capped by the authenticated session deadline.</summary>
    public async ValueTask<DeviceBindingChallenge> IssueAsync(DeviceAuthorizationContext context,
        DeviceBindingOperation operation, PublicDevice requestedDevice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestedDevice);
        cancellationToken.ThrowIfCancellationRequested();
        ProtocolValidator.Validate(requestedDevice);
        var now = DeviceBindingEncoding.NormalizeTime(_time.GetUtcNow());
        if (context.ExpiresAt <= now || requestedDevice.UserId != context.UserId || requestedDevice.IsRevoked)
        {
            throw Rejected();
        }

        var current = await _devices.GetAsync(requestedDevice.DeviceId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.UserId == context.UserId && current.IsRevoked)
        {
            throw new DeviceBindingException(DeviceBindingFailure.Revoked);
        }

        PublicDevice device;
        if (operation == DeviceBindingOperation.Enrollment && current is null)
        {
            device = new PublicDevice(context.UserId, requestedDevice.DeviceId, requestedDevice.KeyId,
                requestedDevice.EncryptionPublicKey.Span, requestedDevice.SigningPublicKey.Span, now);
        }
        else if (operation == DeviceBindingOperation.Rebind && current is not null &&
            DeviceBindingEncoding.SameKeys(current, requestedDevice))
        {
            device = current;
        }
        else
        {
            throw Rejected();
        }

        var expires = now.AddMinutes(2);
        if (expires > context.ExpiresAt) { expires = context.ExpiresAt; }
        var challenge = new DeviceBindingChallenge(1, operation, context, device, Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32), now, expires);
        if (!await _bindings.TryAddChallengeAsync(challenge, cancellationToken).ConfigureAwait(false))
        {
            throw Rejected();
        }

        return challenge;
    }

    /// <summary>Verifies only the stored challenge and authoritative device keys, then commits atomically.</summary>
    public async ValueTask<DeviceSessionBinding> CompleteAsync(DeviceAuthorizationContext context,
        DeviceBindingProof proof, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proof);
        var now = DeviceBindingEncoding.NormalizeTime(_time.GetUtcNow());
        var challenge = await _bindings.GetChallengeAsync(proof.ChallengeId, cancellationToken).ConfigureAwait(false);
        if (context.ExpiresAt <= now || challenge is null || !challenge.Context.Matches(context))
        {
            throw Rejected();
        }

        var device = await _devices.GetAsync(challenge.Device.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is not null && device.UserId == context.UserId && device.IsRevoked)
        {
            throw new DeviceBindingException(DeviceBindingFailure.Revoked);
        }

        if ((challenge.Operation == DeviceBindingOperation.Rebind && device is null) ||
            (device is not null && !DeviceBindingEncoding.SameKeys(device, challenge.Device)) ||
            !_verifier.Verify(challenge, proof))
        {
            throw Rejected();
        }

        return await _bindings.CompleteAsync(challenge, proof, _time, cancellationToken).ConfigureAwait(false) ?? throw Rejected();
    }

    private static DeviceBindingException Rejected() => new(DeviceBindingFailure.Rejected);
}
