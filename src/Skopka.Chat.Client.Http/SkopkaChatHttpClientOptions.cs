using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Http;

/// <summary>Configures one authenticated user/device HTTP client.</summary>
public sealed class SkopkaChatHttpClientOptions
{
    /// <summary>User ID expected in the access-token principal and server responses.</summary>
    public Guid AuthenticatedUserId { get; set; }

    /// <summary>Device ID expected in the access-token principal and addressed operations.</summary>
    public Guid AuthenticatedDeviceId { get; set; }

    /// <summary>Versioned path below <see cref="System.Net.Http.HttpClient.BaseAddress"/>.</summary>
    public string RoutePrefix { get; set; } = SkopkaChatHttpRoutes.DefaultPrefix;

    /// <summary>Reject non-HTTPS base addresses. Disable only for a trusted local test host.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Maximum duration of one HTTP attempt.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Number of retries after the initial idempotent HTTP attempt.</summary>
    public int MaxTransientRetries { get; set; } = 1;

    /// <summary>Initial delay used when a transient response has no bounded Retry-After value.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Upper bound for server-requested or exponential retry delays.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Reject tokens that expire within this interval.</summary>
    public TimeSpan TokenExpirySkew { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (AuthenticatedUserId == Guid.Empty || AuthenticatedDeviceId == Guid.Empty)
        {
            throw new ArgumentException("Authenticated chat user and device IDs are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(RoutePrefix);
        var normalizedPrefix = RoutePrefix.Trim('/');
        if (normalizedPrefix.Length == 0 || RoutePrefix.Contains('\\') ||
            RoutePrefix.Contains('?') || RoutePrefix.Contains('#') ||
            normalizedPrefix.Split('/').Any(segment => segment is "." or ".." or "") ||
            normalizedPrefix.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/' or '.')))
        {
            throw new ArgumentException("The chat route prefix is invalid.", nameof(RoutePrefix));
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaxTransientRetries is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTransientRetries));
        }

        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromSeconds(5) ||
            MaxRetryDelay < RetryDelay || MaxRetryDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        }

        if (TokenExpirySkew < TimeSpan.Zero || TokenExpirySkew > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(TokenExpirySkew));
        }
    }
}
