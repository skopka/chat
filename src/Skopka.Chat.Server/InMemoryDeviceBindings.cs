using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

public sealed partial class InMemoryServerStore : IDeviceBindingRepository
{
    private readonly object _bindingGate = new();
    private readonly Dictionary<Guid, ChallengeEntry> _challenges = new();
    private readonly Dictionary<(string Service, UserId User, string Session), DeviceSessionBinding> _bindings = new();

    /// <inheritdoc />
    public ValueTask<bool> TryAddChallengeAsync(DeviceBindingChallenge challenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindingGate)
        {
            return ValueTask.FromResult(_challenges.TryAdd(challenge.ChallengeId,
                new ChallengeEntry(DeviceBindingEncoding.Encode(challenge))));
        }
    }

    /// <inheritdoc />
    public ValueTask<DeviceBindingChallenge?> GetChallengeAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindingGate)
        {
            return ValueTask.FromResult(_challenges.TryGetValue(challengeId, out var entry)
                ? DeviceBindingEncoding.Decode(entry.Payload) : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<DeviceSessionBinding?> CompleteAsync(DeviceBindingChallenge verifiedChallenge, DeviceBindingProof proof,
        TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedChallenge);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(timeProvider);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindingGate)
        {
            return ValueTask.FromResult(CompleteBinding(verifiedChallenge, proof, DeviceBindingEncoding.NormalizeTime(timeProvider.GetUtcNow())));
        }
    }

    private DeviceSessionBinding? CompleteBinding(DeviceBindingChallenge challenge, DeviceBindingProof proof, DateTimeOffset now)
    {
        if (challenge.ChallengeId != proof.ChallengeId || challenge.Context.ExpiresAt <= now ||
            !_challenges.TryGetValue(proof.ChallengeId, out var entry) ||
            !entry.Payload.AsSpan().SequenceEqual(DeviceBindingEncoding.Encode(challenge))) { return null; }
        var device = _devices.GetValueOrDefault(challenge.Device.DeviceId);
        if (device is not null && (device.IsRevoked || !DeviceBindingEncoding.SameKeys(device, challenge.Device))) { return null; }
        var key = BindingKey(challenge.Context);
        var binding = _bindings.GetValueOrDefault(key);
        if (binding is not null && (!binding.Context.Matches(challenge.Context) ||
            !DeviceBindingEncoding.SameKeys(binding.Device, challenge.Device))) { return null; }
        if (entry.Signature is not null)
        {
            return binding is not null && device is not null && entry.Signature.AsSpan().SequenceEqual(proof.Signature.Span)
                ? entry.Result : null;
        }

        if (challenge.ExpiresAt <= now || challenge.IssuedAt > now ||
            (device is null && challenge.Operation != DeviceBindingOperation.Enrollment)) { return null; }
        // Enrollment must not adopt a concurrent registration under a different challenge.
        if (challenge.Operation == DeviceBindingOperation.Enrollment && device is not null) { return null; }
        device ??= challenge.Device;
        var result = binding ?? new DeviceSessionBinding(challenge.Context, device, now);
        _devices.TryAdd(device.DeviceId, device);
        _bindings[key] = result;
        entry.Signature = proof.Signature.ToArray();
        entry.Result = result;
        return result;
    }

    /// <inheritdoc />
    public ValueTask<DeviceSessionBinding?> ResolveAsync(DeviceAuthorizationContext context, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindingGate)
        {
            var binding = _bindings.GetValueOrDefault(BindingKey(context));
            var device = binding is null ? null : _devices.GetValueOrDefault(binding.Device.DeviceId);
            return ValueTask.FromResult(binding is not null && context.ExpiresAt > now && binding.Context.Matches(context) &&
                device is not null && !device.IsRevoked && DeviceBindingEncoding.SameKeys(device, binding.Device) ? binding : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CleanupAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1000) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bindingGate)
        {
            var challenges = _challenges.Where(pair =>
            {
                var challenge = DeviceBindingEncoding.Decode(pair.Value.Payload);
                return (pair.Value.Signature is null ? challenge.ExpiresAt : challenge.Context.ExpiresAt) <= now;
            }).Take(maximumCount).Select(pair => pair.Key).ToArray();
            foreach (var key in challenges) { _challenges.Remove(key); }
            var bindings = _bindings.Where(pair => pair.Value.Context.ExpiresAt <= now)
                .Take(maximumCount - challenges.Length).Select(pair => pair.Key).ToArray();
            foreach (var key in bindings) { _bindings.Remove(key); }
            return ValueTask.FromResult(challenges.Length + bindings.Length);
        }
    }

    private static (string Service, UserId User, string Session) BindingKey(DeviceAuthorizationContext context) =>
        (context.ServiceId, context.UserId, context.SessionReference);
    private sealed class ChallengeEntry(byte[] payload)
    {
        public byte[] Payload { get; } = payload;
        public byte[]? Signature { get; set; }
        public DeviceSessionBinding? Result { get; set; }
    }
}
