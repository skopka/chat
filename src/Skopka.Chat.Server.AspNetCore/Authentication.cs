using System.Security.Claims;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server.AspNetCore;

/// <summary>Configures the claims and authorization policy used by the HTTP transport.</summary>
public sealed class SkopkaChatHttpOptions
{
    /// <summary>Claim that contains the authenticated chat user ID as a D-format GUID.</summary>
    public string UserIdClaimType { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>Claim that contains the authenticated chat device ID as a D-format GUID.</summary>
    public string DeviceIdClaimType { get; set; } = "skopka_chat_device_id";

    /// <summary>
    /// Optional named authorization policy applied to the complete route group.
    /// The default requires any authenticated principal.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }
}

/// <summary>Authenticated chat identity derived only from trusted host claims.</summary>
public readonly record struct ChatRequestIdentity(UserId UserId, DeviceId DeviceId);

/// <summary>Maps a host-authenticated principal to the user and device identity enforced by the transport.</summary>
public interface IChatPrincipalMapper
{
    /// <summary>Returns false when the principal is unauthenticated, ambiguous or malformed.</summary>
    bool TryMap(ClaimsPrincipal principal, out ChatRequestIdentity identity);
}

/// <summary>Strict default mapper requiring exactly one user claim and exactly one device claim.</summary>
public sealed class ClaimsChatPrincipalMapper : IChatPrincipalMapper
{
    private readonly SkopkaChatHttpOptions _options;

    /// <summary>Creates the mapper from transport options.</summary>
    public ClaimsChatPrincipalMapper(IOptions<SkopkaChatHttpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public bool TryMap(ClaimsPrincipal principal, out ChatRequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(principal);
        identity = default;
        if (string.IsNullOrWhiteSpace(_options.UserIdClaimType) ||
            string.IsNullOrWhiteSpace(_options.DeviceIdClaimType))
        {
            return false;
        }

        var authenticatedClaims = principal.Identities
            .Where(candidate => candidate.IsAuthenticated)
            .SelectMany(candidate => candidate.Claims)
            .ToArray();

        if (!TryGetSingleGuid(authenticatedClaims, _options.UserIdClaimType, out var userId) ||
            !TryGetSingleGuid(authenticatedClaims, _options.DeviceIdClaimType, out var deviceId))
        {
            return false;
        }

        identity = new ChatRequestIdentity(new UserId(userId), new DeviceId(deviceId));
        return true;
    }

    private static bool TryGetSingleGuid(
        IEnumerable<Claim> claims,
        string claimType,
        out Guid value)
    {
        value = default;
        var matches = claims.Where(claim => claim.Type == claimType).Take(2).ToArray();
        return matches.Length == 1 &&
            Guid.TryParseExact(matches[0].Value, "D", out value) &&
            value != Guid.Empty;
    }
}
