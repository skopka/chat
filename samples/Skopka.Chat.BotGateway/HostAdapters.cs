using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Skopka.Chat.Bots;
using Skopka.Chat.Bots.AspNetCore;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.BotGateway;

internal static class SecretFiles
{
    internal static async ValueTask<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length is < 1 or > 8192) { throw new ChatBotException(); }
        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.ReadByte() != -1) { throw new ChatBotException(); }
            return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\r', '\n');
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed class FileTokenProvider(string path) : IAccessTokenProvider
{
    public async ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        new(await SecretFiles.ReadAsync(path, cancellationToken).ConfigureAwait(false));
}

internal sealed record GatewayCredentials(string TokenFile, UserId BotUserId)
{
    public override string ToString() => "GatewayCredentials([REDACTED])";
}

internal sealed class GatewayAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, GatewayCredentials credentials) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } header || header.Length > 8199 ||
            !header.StartsWith("Bearer ", StringComparison.Ordinal) || header.AsSpan(7).Contains(','))
        {
            return AuthenticateResult.Fail("Invalid bot credentials.");
        }
        try
        {
            var expected = await SecretFiles.ReadAsync(credentials.TokenFile, Context.RequestAborted).ConfigureAwait(false);
            // Configure a randomly generated token of at least 32 bytes, encoded as unpadded base64url.
            if (expected.Length < 43 || expected.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            {
                return AuthenticateResult.Fail("Invalid bot credentials.");
            }
            var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]));
            var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
            if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash)) { return AuthenticateResult.Fail("Invalid bot credentials."); }
            var identity = new ClaimsIdentity([new Claim(BotEndpointExtensions.BotUserClaim, credentials.BotUserId.ToString())], Scheme.Name);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { return AuthenticateResult.Fail("Invalid bot credentials."); }
    }
}

// This is an authenticated HOST endpoint, not an endpoint implemented by this gateway.
// Only the host can assert end-user consent and server-side admission policy.
internal sealed class HostConsentProvider : IChatBotConsentProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _tokenFile;
    internal HostConsentProvider(Uri baseAddress, string tokenFile)
    {
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseAddress.UserInfo) ||
            !string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment) || !baseAddress.AbsolutePath.EndsWith('/'))
        {
            throw new ArgumentException("The host consent endpoint must be a fixed HTTPS base URI.", nameof(baseAddress));
        }
        _http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
        _tokenFile = tokenFile;
    }
    public async ValueTask<ChatBotConsent?> GetConsentAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, "consents/" + conversationId.Value.ToString("D"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await SecretFiles.ReadAsync(_tokenFile, cancellationToken).ConfigureAwait(false));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > 4096) { throw new ChatBotException(); }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[4097];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) { break; }
            length += count;
        }
        if (length > 4096) { throw new ChatBotException(); }
        var grant = JsonSerializer.Deserialize(bytes.AsSpan(0, length), ConsentJson.Default.ConsentResponse) ?? throw new ChatBotException();
        return new(grant.GrantId, new(grant.ConversationId), new(grant.UserId), new(grant.BotUserId), grant.ProfileRevision, grant.ExpiresAt);
    }
    public void Dispose() => _http.Dispose();
}

internal sealed record ConsentResponse(Guid GrantId, Guid ConversationId, Guid UserId, Guid BotUserId, Guid ProfileRevision, DateTimeOffset ExpiresAt);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, MaxDepth = 16)]
[JsonSerializable(typeof(ConsentResponse))]
internal sealed partial class ConsentJson : JsonSerializerContext;

internal sealed class BotPollingWorker(ChatBotRuntime runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.SynchronizeAsync(cancellationToken: stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Generic health signal only. Host monitoring should alert without capturing exceptions or payloads.
                Console.Error.WriteLine("Bot synchronization unavailable; delivery remains pending.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }
}
