using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server.AspNetCore;

/// <summary>Host boundary: produce context only from a normally authenticated principal/session.</summary>
public interface IChatAuthorizationContextProvider
{
    /// <summary>Resolves a stable, non-secret session context asynchronously; never trusts caller-supplied device IDs.</summary>
    ValueTask<DeviceAuthorizationContext?> GetContextAsync(HttpContext context, CancellationToken cancellationToken = default);
}

/// <summary>Async request identity boundary, with a compatible claims-based fallback when binding is not enabled.</summary>
public interface IChatRequestIdentityResolver
{
    /// <summary>Returns the currently authorized permanent device, or null.</summary>
    ValueTask<ChatRequestIdentity?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default);
}

/// <summary>Opt-in binding settings. Hosts must register both named rate-limiter policies.</summary>
public sealed class SkopkaChatDeviceBindingOptions
{
    /// <summary>Configuration-owned exact service/authority ID, never derived from a request header.</summary>
    public string ServiceId { get; set; } = "";
    /// <summary>Optional additional host account/step-up policy; must not require an existing device binding.</summary>
    public string? AccountAuthorizationPolicy { get; set; }
    /// <summary>Host rate-limit policy for challenge issuance, partitioned by authenticated account/session.</summary>
    public string ChallengeRateLimitPolicy { get; set; } = "skopka-chat-challenges";
    /// <summary>Host rate-limit policy for proof verification.</summary>
    public string ProofRateLimitPolicy { get; set; } = "skopka-chat-proofs";
}

/// <summary>Independent authorization policy names used by the opt-in mechanism.</summary>
public static class DeviceBindingPolicies
{
    /// <summary>Authenticated account/session; no device binding required.</summary>
    public const string Account = "Skopka.Chat.Binding.Account";
    /// <summary>Authenticated account/session with a live device binding.</summary>
    public const string Device = "Skopka.Chat.Binding.Device";
}

internal sealed record DeviceBindingRequirement(bool RequireDevice) : IAuthorizationRequirement;

internal sealed class DeviceBindingRequestResolver(IChatAuthorizationContextProvider provider, IDeviceBindingRepository bindings,
    IOptions<SkopkaChatDeviceBindingOptions> options, TimeProvider timeProvider) : IChatRequestIdentityResolver
{
    private static readonly object AccountKey = new();
    private static readonly object DeviceKey = new();
    public async ValueTask<DeviceAuthorizationContext?> AccountAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Items.TryGetValue(AccountKey, out var cached)) { return cached as DeviceAuthorizationContext; }
        DeviceAuthorizationContext? account = null;
        if (context.User.Identities.Any(identity => identity.IsAuthenticated))
        {
            account = await provider.GetContextAsync(context, cancellationToken).ConfigureAwait(false);
            if (account?.ServiceId != options.Value.ServiceId || account.ExpiresAt <= timeProvider.GetUtcNow()) { account = null; }
        }
        context.Items[AccountKey] = account;
        return account;
    }
    public async ValueTask<ChatRequestIdentity?> ResolveAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (context.Items.TryGetValue(DeviceKey, out var cached)) { return cached is ChatRequestIdentity identity ? identity : null; }
        var account = await AccountAsync(context, cancellationToken).ConfigureAwait(false);
        var binding = account is null ? null : await bindings.ResolveAsync(account, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        ChatRequestIdentity? result = binding is null ? null : new ChatRequestIdentity(binding.Device.UserId, binding.Device.DeviceId);
        context.Items[DeviceKey] = result;
        return result;
    }
}

internal sealed class DeviceBindingAuthorizationHandler(DeviceBindingRequestResolver resolver) : AuthorizationHandler<DeviceBindingRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, DeviceBindingRequirement requirement)
    {
        if (context.Resource is not HttpContext http) { return; }
        var allowed = requirement.RequireDevice
            ? await resolver.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false) is not null
            : await resolver.AccountAsync(http, http.RequestAborted).ConfigureAwait(false) is not null;
        if (allowed) { context.Succeed(requirement); }
    }
}
