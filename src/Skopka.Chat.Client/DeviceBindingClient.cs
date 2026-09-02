using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>An authenticated bootstrap response reports that the permanent device is revoked.</summary>
public sealed class DeviceBindingRevokedException : Exception
{
    /// <summary>Creates a generic revocation failure without response content.</summary>
    public DeviceBindingRevokedException() : base("The device has been revoked.") { }
}

/// <summary>Account-authenticated bootstrap transport usable before device-bound chat services exist.</summary>
public interface IDeviceBindingTransport
{
    /// <summary>Requests enrollment or rebind; authentication context is resolved by the server host.</summary>
    ValueTask<DeviceBindingChallenge> IssueAsync(DeviceBindingOperation operation, PublicDevice device, CancellationToken cancellationToken = default);
    /// <summary>Completes a stored challenge; exact retries must preserve the proof bytes.</summary>
    ValueTask<DeviceSessionBinding> CompleteAsync(DeviceBindingProof proof, CancellationToken cancellationToken = default);
}

/// <summary>Purpose-specific signing using the current device's protected keys.</summary>
public sealed class DeviceBindingProofService
{
    private readonly IDeviceKeyStore _keys;
    private readonly TimeProvider _time;
    private readonly IChatCryptographyProvider _crypto;

    /// <summary>Uses the default native provider.</summary>
    public DeviceBindingProofService(IDeviceKeyStore keyStore, TimeProvider timeProvider)
        : this(keyStore, timeProvider, ChatCryptographyDefaults.Create()) { }

    /// <summary>Uses the selected endpoint provider; expected context validation remains shared.</summary>
    public DeviceBindingProofService(IDeviceKeyStore keyStore, TimeProvider timeProvider, IChatCryptographyProvider cryptography)
    {
        _keys = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _crypto = cryptography ?? throw new ArgumentNullException(nameof(cryptography));
    }

    /// <summary>Checks the independently expected context, operation and both keys before signing binding-v1 only.</summary>
    public async ValueTask<DeviceBindingProof> CreateProofAsync(DeviceBindingChallenge challenge,
        DeviceAuthorizationContext expectedContext, PublicDevice expectedDevice, DeviceBindingOperation expectedOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(expectedContext);
        ArgumentNullException.ThrowIfNull(expectedDevice);
        // Snapshot the immutable protocol bytes before asynchronous key-store access.
        var snapshot = DeviceBindingEncoding.Decode(DeviceBindingEncoding.Encode(challenge));
        var now = _time.GetUtcNow();
        if (!snapshot.Context.Matches(expectedContext) || snapshot.Operation != expectedOperation || expectedDevice.IsRevoked ||
            !DeviceBindingEncoding.SameKeys(snapshot.Device, expectedDevice) || snapshot.ExpiresAt <= now ||
            snapshot.IssuedAt > now.AddSeconds(30))
        {
            throw new ChatCryptographicException("Device challenge does not match the expected context.");
        }
        var local = await new DeviceIdentityService(_keys, _crypto).LoadPublicAsync(expectedContext.UserId, expectedDevice.DeviceId,
            expectedDevice.RegisteredAt, cancellationToken).ConfigureAwait(false);
        if (local is null || !DeviceBindingEncoding.SameKeys(local, expectedDevice))
        {
            throw new ChatCryptographicException("Device ownership keys are missing or inconsistent.");
        }
        var material = await _keys.LoadAsync(expectedDevice.DeviceId, cancellationToken).ConfigureAwait(false);
        if (material is null || material.UserId != expectedDevice.UserId || material.DeviceId != expectedDevice.DeviceId || material.KeyId != expectedDevice.KeyId)
        {
            throw new ChatCryptographicException("Device ownership keys are missing or inconsistent.");
        }
        var privateBytes = material.ExportSigningPrivateKey();
        try
        {
            if (!_crypto.GetPublicKey(ChatKeyAlgorithm.Ed25519, privateBytes).AsSpan().SequenceEqual(snapshot.Device.SigningPublicKey.Span))
            {
                throw new ChatCryptographicException("Device ownership keys are inconsistent.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new DeviceBindingProof(snapshot.ChallengeId, _crypto.Sign(privateBytes, DeviceBindingEncoding.Encode(snapshot)));
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or FormatException)
        {
            throw new ChatCryptographicException("Device ownership proof could not be created.");
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }
}

/// <summary>Login/bootstrap coordinator; does not start sender/history services before binding succeeds.</summary>
public sealed class DeviceBindingCoordinator(PersistentDeviceIdentityService identities, DeviceBindingProofService proofs,
    IDeviceBindingTransport transport)
{
    private readonly PersistentDeviceIdentityService _identities = identities ?? throw new ArgumentNullException(nameof(identities));
    private readonly DeviceBindingProofService _proofs = proofs ?? throw new ArgumentNullException(nameof(proofs));
    private readonly IDeviceBindingTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    /// <summary>Loads existing identity, binds this session and persists authoritative registration metadata.</summary>
    /// <remarks>Call CreateAsync or AdoptAsync explicitly first; this operation never creates missing keys.</remarks>
    public async ValueTask<DeviceSessionBinding> BindAsync(DeviceIdentityScope scope, DeviceAuthorizationContext context,
        DeviceBindingOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);
        if (scope.ServiceId != context.ServiceId || scope.UserId != context.UserId)
        {
            throw new ArgumentException("Account context does not match the permanent identity scope.");
        }
        var loaded = await _identities.LoadAsync(scope, cancellationToken).ConfigureAwait(false);
        if (loaded.State != PersistentDeviceIdentityState.Ready || loaded.Metadata?.PublicDevice is not { } device)
        {
            throw new DeviceIdentityStorageException(loaded.State);
        }
        DeviceSessionBinding binding;
        try
        {
            var challenge = await _transport.IssueAsync(operation, device, cancellationToken).ConfigureAwait(false);
            var proof = await _proofs.CreateProofAsync(challenge, context, device, operation, cancellationToken).ConfigureAwait(false);
            binding = await _transport.CompleteAsync(proof, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceBindingRevokedException)
        {
            await _identities.RememberRevokedAsync(scope, cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (!binding.Context.Matches(context) || !DeviceBindingEncoding.SameKeys(binding.Device, device) || binding.Device.IsRevoked)
        {
            throw new ChatCryptographicException("Device binding response does not match the persistent identity.");
        }
        await _identities.MarkRegisteredAsync(scope, binding, cancellationToken).ConfigureAwait(false);
        return binding;
    }
}
