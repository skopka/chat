namespace Skopka.Chat.Client.Http;

/// <summary>Host-owned request authentication/CSRF boundary for non-bearer transports such as a same-origin cookie BFF.</summary>
/// <remarks>Called on every attempt. Must not log requests, weaken TLS, redirect, or forward secrets to another origin.</remarks>
public interface IChatHttpRequestAuthorizer
{
    /// <summary>Authorizes this request using the host's session and cancellation policy.</summary>
    ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}
