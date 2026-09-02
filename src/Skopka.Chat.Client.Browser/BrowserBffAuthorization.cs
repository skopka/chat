using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Browser;

/// <summary>Host account endpoint boundary, independent of SkopiClub.Auth/OAuth.</summary>
public interface IBrowserChatAccountContextProvider
{
    /// <summary>Obtains independently trusted current service/user/session/deadline data, never from the binding challenge alone.</summary>
    ValueTask<DeviceAuthorizationContext> GetContextAsync(CancellationToken cancellationToken = default);
}

/// <summary>Host-owned anti-CSRF request preparation, called for every unsafe method and retry.</summary>
public interface IBrowserChatCsrfProvider
{
    /// <summary>Adds the host's reviewed CSRF proof; must not log it or create OAuth bearer tokens in the browser.</summary>
    ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}

/// <summary>Same-origin cookie authorization with redirect rejection and mandatory host CSRF preparation.</summary>
public sealed class BrowserBffAuthorization : IChatHttpRequestAuthorizer
{
    private readonly Uri _origin;
    private readonly IBrowserChatCsrfProvider _csrf;
    /// <summary>Creates an adapter for the actual page origin. HTTP is allowed only for explicit loopback development.</summary>
    public BrowserBffAuthorization(Uri pageOrigin, IBrowserChatCsrfProvider csrf, bool allowLoopbackHttp = false)
    {
        ArgumentNullException.ThrowIfNull(pageOrigin);
        if (!pageOrigin.IsAbsoluteUri || pageOrigin.UserInfo.Length != 0 ||
            pageOrigin.Scheme != "https" && !(allowLoopbackHttp && pageOrigin.IsLoopback && pageOrigin.Scheme == "http"))
        { throw new ArgumentException("A secure page origin is required.", nameof(pageOrigin)); }
        _origin = pageOrigin;
        _csrf = csrf ?? throw new ArgumentNullException(nameof(csrf));
    }
    /// <inheritdoc />
    public async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options)
        { await _csrf.ApplyAsync(request, cancellationToken).ConfigureAwait(false); }
        // A host callback must not inadvertently change the destination or add bearer credentials.
        ValidateRequest(request);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.SameOrigin);
        request.SetBrowserRequestOption("redirect", "error");
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
    }

    private void ValidateRequest(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != _origin.Scheme || uri.Host != _origin.Host || uri.Port != _origin.Port || uri.UserInfo.Length != 0 ||
            request.Headers.Authorization is not null)
        { throw new ChatHttpTransportException("BFF request origin or authorization is invalid."); }
    }
}
