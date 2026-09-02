using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skopka.Chat.Bots.AspNetCore;

namespace Skopka.Chat.Bots.Tests;

public sealed class BotHttpTests
{
    [Fact]
    public async Task Every_route_requires_authentication_and_exact_single_bot_scope()
    {
        using var f = await BotFixture.CreateAsync();
        await using var app = await ApplicationAsync(f);
        using var client = app.GetTestClient();
        using var anonymous = await client.GetAsync("/bot/v1/getMe");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Bot", Guid.NewGuid().ToString("D"));
        using var wrong = await client.GetAsync("/bot/v1/getMe");
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Bot");
        client.DefaultRequestHeaders.Add("X-Test-Bot", [f.Bot.UserId.ToString(), f.Bot.UserId.ToString()]);
        using var duplicate = await client.GetAsync("/bot/v1/getMe");
        Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Bot");
        client.DefaultRequestHeaders.Add("X-Test-Bot", f.Bot.UserId.ToString());
        using var valid = await client.GetAsync("/bot/v1/getMe");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Contains("Synthetic operator", await valid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(valid.Headers.CacheControl!.NoStore);
        using var query = await client.GetAsync("/bot/v1/getMe?token=synthetic");
        Assert.Equal(HttpStatusCode.Forbidden, query.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"limit\":1,\"limit\":2}")]
    [InlineData("{\"Limit\":1}")]
    [InlineData("{\"limit\":\"1\"}")]
    [InlineData("{\"limit\":null}")]
    [InlineData("{\"limit\":1,\"synthetic-secret-marker\":0}")]
    [InlineData("{\"limit\":1,}")]
    [InlineData("{\"limit\":1}{}")]
    [InlineData("{/*comment*/\"limit\":1}")]
    [InlineData("{\"limit\":0}")]
    [InlineData("{\"limit\":21}")]
    public async Task Strict_json_and_bounds_fail_without_reflection(string payload)
    {
        using var f = await BotFixture.CreateAsync();
        await using var app = await ApplicationAsync(f);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Bot", f.Bot.UserId.ToString());
        using var request = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/bot/v1/getUpdates", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gateway_roundtrips_updates_and_processing_ack_without_exposing_keys()
    {
        using var f = await BotFixture.CreateAsync();
        await f.AddAsync("synthetic gateway text");
        using (var runtime = f.Runtime()) { await runtime.SynchronizeAsync(); }
        await using var app = await ApplicationAsync(f);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Bot", f.Bot.UserId.ToString());
        using var poll = new StringContent("{\"limit\":20}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/bot/v1/getUpdates", poll);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("synthetic gateway text", json, StringComparison.Ordinal);
        Assert.DoesNotContain("grantId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
        var update = Assert.Single(await f.Inbox.ReadAsync(0, 20));
        using var ack = new StringContent($"{{\"updateId\":{update.UpdateId}}}", Encoding.UTF8, "application/json");
        using var acknowledged = await client.PostAsync("/bot/v1/acknowledgeUpdate", ack);
        Assert.Equal(HttpStatusCode.NoContent, acknowledged.StatusCode);
        Assert.Empty(await f.Inbox.ReadAsync(0, 20));
    }

    [Fact]
    public async Task Media_type_and_declared_or_chunked_body_limits_are_enforced()
    {
        using var f = await BotFixture.CreateAsync();
        await using var app = await ApplicationAsync(f);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Bot", f.Bot.UserId.ToString());
        using var text = new StringContent("{\"limit\":1}");
        using var wrongType = await client.PostAsync("/bot/v1/getUpdates", text);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        var body = new byte[BotEndpointExtensions.MaximumRequestBytes + 1];
        using var oversized = new ByteArrayContent(body);
        oversized.Headers.ContentType = new("application/json");
        using var declared = await client.PostAsync("/bot/v1/getUpdates", oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        using var chunks = new ChunkedContent(body);
        using var chunked = await client.PostAsync("/bot/v1/getUpdates", chunks);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
    }

    [Fact]
    public async Task Send_message_uses_ciphertext_transport_and_denies_revoked_consent()
    {
        using var f = await BotFixture.CreateAsync();
        await using var app = await ApplicationAsync(f);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Bot", f.Bot.UserId.ToString());
        using var message = new StringContent($"{{\"conversationId\":\"{f.Conversation}\",\"requestId\":\"{Guid.NewGuid():D}\",\"text\":\"synthetic HTTP answer\"}}", Encoding.UTF8, "application/json");
        using var sent = await client.PostAsync("/bot/v1/sendMessage", message);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Single(f.Sent);
        Assert.Contains("\"succeeded\":true", await sent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        f.Grant = null;
        using var denied = await client.PostAsync("/bot/v1/sendMessage", message);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.Single(f.Sent);
        Assert.Equal(string.Empty, await denied.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> ApplicationAsync(BotFixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.Runtime());
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("TestBot", policy => policy.RequireAuthenticatedUser()));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSkopkaChatBotApi("TestBot");
        await app.StartAsync();
        return app;
    }

    // Synthetic test authentication only. Never copy header assertions into a real host.
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var values = Request.Headers["X-Test-Bot"];
            if (values.Count == 0) { return Task.FromResult(AuthenticateResult.NoResult()); }
            var claims = values.Select(value => new Claim(BotEndpointExtensions.BotUserClaim, value!));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _bytes;
        public ChunkedContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
