using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Skopka.Chat.Client.Browser;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Browser.Sample;

internal sealed class DemoHostApi(Uri origin) : IBrowserChatCsrfProvider, IBrowserChatAccountContextProvider, IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) };
    public BrowserBffAuthorization Authorization => new(origin, this, allowLoopbackHttp: true);
    public async ValueTask<DeviceAuthorizationContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "demo/account");
        using var response = await _http.SendAsync(request, cancellationToken);
        var bytes = await ReadAsync(response, cancellationToken);
        var value = JsonSerializer.Deserialize(bytes, DemoJson.Default.DemoAccount) ?? throw Failure();
        return new(value.ServiceId, new UserId(value.UserId), value.SessionReference, value.ExpiresAt);
    }
    public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        using var csrfRequest = Request(HttpMethod.Get, "demo/csrf");
        using var response = await _http.SendAsync(csrfRequest, cancellationToken);
        var bytes = await ReadAsync(response, cancellationToken);
        var value = JsonSerializer.Deserialize(bytes, DemoJson.Default.DemoCsrf) ?? throw Failure();
        if (value.Token.Length is < 1 or > 2048) { throw Failure(); }
        request.Headers.Add("X-Chat-CSRF", value.Token);
    }
    public async Task LoginAsync(string account)
    {
        if (account is not ("alice" or "bob")) { throw Failure(); }
        await PostAsync("demo/login/" + account);
    }
    public Task LogoutAsync() => PostAsync("demo/logout");
    private async Task PostAsync(string path)
    {
        using var request = Request(HttpMethod.Post, path);
        await Authorization.AuthorizeAsync(request);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) { throw Failure(); }
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(origin, path));
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.SameOrigin);
        request.SetBrowserRequestOption("redirect", "error");
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        return request;
    }
    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4096) { throw Failure(); }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4097];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) { return output.ToArray(); }
            if (output.Length + count > 4096) { throw Failure(); }
            output.Write(buffer, 0, count);
        }
    }
    private static InvalidOperationException Failure() => new("Local demonstration session request failed.");
    public void Dispose() => _http.Dispose();
}
internal sealed record DemoAccount(string ServiceId, Guid UserId, string SessionReference, DateTimeOffset ExpiresAt);
internal sealed record DemoCsrf(string Token);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false, MaxDepth = 4)]
[JsonSerializable(typeof(DemoAccount))]
[JsonSerializable(typeof(DemoCsrf))]
internal sealed partial class DemoJson : JsonSerializerContext;
