using Skopka.Chat.Protocol;

namespace Skopka.Chat.Transport.Http;

/// <summary>Separate account-authenticated bootstrap routes and bounds.</summary>
public static class DeviceBindingHttpRoutes
{
    /// <summary>Issue a short-lived ownership challenge.</summary>
    public const string Challenges = "/device-binding/challenges";
    /// <summary>Complete a stored challenge with a typed signature.</summary>
    public const string Completions = "/device-binding/completions";
    /// <summary>Maximum request or response body for bootstrap JSON.</summary>
    public const int MaximumBodyBytes = 4096;
}

/// <summary>Public keys for explicit enrollment or comparison to immutable directory keys on rebind.</summary>
public sealed record DeviceBindingIssueRequest(int Operation, RegisterDeviceRequest Device);

/// <summary>Canonical bounded binding-v1 bytes; never arbitrary bytes offered to a signing API.</summary>
public sealed record DeviceBindingChallengeResponse(byte[] Payload)
{
    /// <summary>Strictly reconstructs a typed challenge before any signing operation.</summary>
    public DeviceBindingChallenge ToDomain() => DeviceBindingEncoding.Decode(Payload ?? []);
    /// <inheritdoc />
    public override string ToString() => "DeviceBindingChallengeResponse([REDACTED])";
}

/// <summary>Challenge identifier and exactly one Ed25519 signature.</summary>
public sealed record DeviceBindingCompleteRequest(Guid ChallengeId, byte[] Signature)
{
    /// <summary>Validates sizes before cryptographic work.</summary>
    public DeviceBindingProof ToDomain() => new(ChallengeId, Signature ?? []);
    /// <inheritdoc />
    public override string ToString() => "DeviceBindingCompleteRequest([REDACTED])";
}

/// <summary>Authoritative permanent device plus bounded non-secret session context.</summary>
public sealed record DeviceBindingResultResponse(string ServiceId, Guid UserId, string SessionReference,
    DateTimeOffset SessionExpiresAt, PublicDeviceResponse Device, DateTimeOffset BoundAt)
{
    /// <summary>Creates a response without bearer credentials.</summary>
    public static DeviceBindingResultResponse FromDomain(DeviceSessionBinding binding) => new(
        binding.Context.ServiceId, binding.Context.UserId.Value, binding.Context.SessionReference, binding.Context.ExpiresAt,
        PublicDeviceResponse.FromDomain(binding.Device), binding.BoundAt);
    /// <summary>Validates the response's account/device and time bounds.</summary>
    public DeviceSessionBinding ToDomain()
    {
        var context = new DeviceAuthorizationContext(ServiceId, new UserId(UserId), SessionReference, SessionExpiresAt);
        var device = Device?.ToDomain() ?? throw new ArgumentException("Invalid binding response.");
        if (device.UserId != context.UserId || device.IsRevoked || BoundAt == default || BoundAt >= context.ExpiresAt || device.RegisteredAt > BoundAt)
        {
            throw new ArgumentException("Invalid binding response.");
        }
        return new DeviceSessionBinding(context, device, BoundAt);
    }
    /// <inheritdoc />
    public override string ToString() => "DeviceBindingResultResponse([REDACTED])";
}
