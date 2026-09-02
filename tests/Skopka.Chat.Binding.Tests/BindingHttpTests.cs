using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Server.NSec;
using Skopka.Chat.Testing;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Binding.Tests;

public sealed class BindingHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E2ee_message_survives_logout_new_session_rebind_and_history_outbox_restart(bool postgres)
    {
        var database = postgres ? await PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync() : null;
        await using var host = await Host.CreateAsync(database);
        var aliceKeys = new InMemoryDeviceKeyStore();
        var bobKeys = new InMemoryDeviceKeyStore();
        var alice = await new DeviceIdentityService(aliceKeys).CreateAsync(UserId.New(), DeviceId.New(), BindingProtocolTests.Now);
        var bob = await new DeviceIdentityService(bobKeys).CreateAsync(UserId.New(), DeviceId.New(), BindingProtocolTests.Now);
        var aliceContext = host.AddSession("synthetic-alice", alice.UserId, "alice-login");
        var bobFirst = host.AddSession("synthetic-bob-first", bob.UserId, "bob-login-one");
        using var aliceHttp = host.Http();
        using var bobHttp = host.Http();
        var aliceApi = host.Client(aliceHttp, alice, "synthetic-alice");
        var bobApi = host.Client(bobHttp, bob, "synthetic-bob-first");
        await BindAsync(aliceApi, aliceKeys, alice, aliceContext, DeviceBindingOperation.Enrollment, host.Clock);
        await BindAsync(bobApi, bobKeys, bob, bobFirst, DeviceBindingOperation.Enrollment, host.Clock);
        var conversation = await aliceApi.GetOrCreatePersonalConversationAsync(bob.UserId);
        var content = new ChatTextContent(ChatContentId.New(), "synthetic-secret-roundtrip");
        var envelope = await new ChatCryptoService(aliceKeys).EncryptContentAsync(content, conversation.ConversationId,
            MessageId.New(), alice.DeviceId, bob, BindingProtocolTests.Now);
        await aliceApi.SendAsync(envelope);
        var partition = new DeviceIdentityScope("chat.example.test", bob.UserId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), "skopka-binding-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var historyPath = Path.Combine(root, partition.StoragePartition + ".db");
        var outboxPath = Path.Combine(root, partition.StoragePartition + ".outbox.db");
        var pendingContent = new ChatTextContent(ChatContentId.New(), "synthetic-pending-outbox");
        var pendingEnvelope = await new ChatCryptoService(bobKeys).EncryptContentAsync(pendingContent, conversation.ConversationId,
            MessageId.New(), bob.DeviceId, alice, BindingProtocolTests.Now);
        var pendingPlan = new ChatFanOutPlan(conversation.ConversationId, pendingContent.ContentId, bob.UserId, bob.DeviceId,
            MessageId.New(), BindingProtocolTests.Now, System.Security.Cryptography.SHA256.HashData(ChatContentEncoding.Encode(pendingContent)),
            [new ChatEnvelopePlanItem(pendingEnvelope, false)]);
        try
        {
            using (var history = new SqliteChatEventStore("Data Source=" + historyPath + ";Pooling=False"))
            using (var outbox = new SqliteChatOutboxStore("Data Source=" + outboxPath + ";Pooling=False"))
            {
                await history.StoreAsync(new ReceivedChatContent(MessageId.New(), conversation.ConversationId, bob.UserId, bob.DeviceId,
                    BindingProtocolTests.Now.AddMinutes(-1), new ChatTextContent(ChatContentId.New(), "synthetic-old-history")));
                await outbox.StoreAsync(pendingPlan);
            }
            host.Tokens.TryRemove("synthetic-bob-first", out _); // real authentication no longer accepts the old session
            var oldError = await Assert.ThrowsAsync<ChatHttpTransportException>(async () => await bobApi.ReceiveAsync(bob.DeviceId, 10));
            Assert.Equal(HttpStatusCode.Unauthorized, oldError.StatusCode);
            var bobSecond = host.AddSession("synthetic-bob-second", bob.UserId, "bob-login-two");
            using var secondHttp = host.Http();
            var secondApi = host.Client(secondHttp, bob, "synthetic-bob-second");
            var unbound = await Assert.ThrowsAsync<ChatHttpTransportException>(async () => await secondApi.ReceiveAsync(bob.DeviceId, 10));
            Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);
            var reloaded = (await new DeviceIdentityService(bobKeys).LoadPublicAsync(bob.UserId, bob.DeviceId, bob.RegisteredAt))!;
            await BindAsync(secondApi, bobKeys, reloaded, bobSecond, DeviceBindingOperation.Rebind, host.Clock);
            using var restoredHistory = new SqliteChatEventStore("Data Source=" + historyPath + ";Pooling=False");
            using var restoredOutbox = new SqliteChatOutboxStore("Data Source=" + outboxPath + ";Pooling=False");
            var loadedPlan = (await restoredOutbox.LoadAsync(conversation.ConversationId, pendingContent.ContentId))!;
            Assert.Equal(CanonicalEnvelopeEncoding.EncodeEnvelope(pendingEnvelope), CanonicalEnvelopeEncoding.EncodeEnvelope(loadedPlan.Envelopes[0].Envelope));
            var projection = new ChatConversationProjectionRegistry();
            using var sync = new ChatSyncCoordinator(secondApi, new ChatCryptoService(bobKeys), restoredHistory, projection, bob.DeviceId, host.Clock);
            Assert.Equal(1, await sync.InitializeAsync());
            var batch = await sync.SynchronizeAsync();
            Assert.Equal(1, batch.Acknowledged);
            var messages = projection.GetOrCreate(conversation.ConversationId).SnapshotTimeline().OfType<ProjectedChatMessage>().ToArray();
            Assert.Contains(messages, item => item.Text == "synthetic-old-history");
            Assert.Contains(messages, item => item.Text == content.Text);
            using var dispatcher = new ChatOutboxDispatcher(restoredOutbox, secondApi, host.Clock);
            Assert.Equal(1, (await dispatcher.DispatchAsync()).EnvelopesAccepted);
            Assert.Empty(await secondApi.ReceiveAsync(bob.DeviceId, 10));
            var logs = string.Join('\n', host.Logs.Entries);
            Assert.DoesNotContain(content.Text, logs, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-bob-second", logs, StringComparison.Ordinal);
            var retained = (await bobKeys.LoadAsync(bob.DeviceId))!;
            Assert.DoesNotContain(Convert.ToBase64String(retained.ExportSigningPrivateKey()), logs, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(retained.ExportEncryptionPrivateKey()), logs, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Lost_completion_response_retries_exact_proof_and_cannot_bypass_revocation()
    {
        await using var host = await Host.CreateAsync();
        var keys = new InMemoryDeviceKeyStore();
        var device = await new DeviceIdentityService(keys).CreateAsync(UserId.New(), DeviceId.New(), BindingProtocolTests.Now);
        var account = host.AddSession("synthetic-retry", device.UserId, "retry-session");
        using var handler = new LoseCompletionResponse(host.App.GetTestServer().CreateHandler());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var api = host.Client(http, device, "synthetic-retry");
        var challenge = await api.IssueAsync(DeviceBindingOperation.Enrollment, device);
        var proof = await new DeviceBindingProofService(keys, host.Clock).CreateProofAsync(challenge, account, device, challenge.Operation);
        var bound = await api.CompleteAsync(proof);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.True(DeviceBindingEncoding.SameKeys(device, bound.Device));
        await api.RevokeDeviceAsync(device.DeviceId);
        await Assert.ThrowsAsync<DeviceBindingRevokedException>(async () => await api.CompleteAsync(proof));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"operation\":1,\"operation\":2,\"device\":null}")]
    [InlineData("{\"operation\":\"1\",\"device\":null}")]
    [InlineData("{\"operation\":1,\"device\":null,\"private-token-marker\":true}")]
    [InlineData("{\"operation\":1,\"device\":null} true")]
    [InlineData("{\"Operation\":1,\"device\":null}")]
    [InlineData("{\"operation\":1,\"device\":null,}")]
    [InlineData("/* comment */{\"operation\":1,\"device\":null}")]
    public async Task Bootstrap_hostile_json_fails_without_reflection(string json)
    {
        await using var host = await Host.CreateAsync();
        host.AddSession("synthetic-json", UserId.New(), "json-session");
        using var http = host.Http();
        using var request = Request(DeviceBindingHttpRoutes.Challenges, "synthetic-json", json);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("private-token-marker", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-token-marker", string.Join('\n', host.Logs.Entries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_bounds_authentication_headers_and_cancellation_are_enforced()
    {
        await using var host = await Host.CreateAsync();
        host.AddSession("synthetic-account", UserId.New(), "account-session");
        using var http = host.Http();
        using (var request = Request(DeviceBindingHttpRoutes.Challenges, "synthetic-account", new string(' ', 4097)))
        using (var response = await http.SendAsync(request)) { Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); }
        using (var request = Request(DeviceBindingHttpRoutes.Challenges, "forged-header-token", "{}"))
        {
            request.Headers.Add("X-User-Id", UserId.New().ToString());
            request.Headers.Add("X-Device-Id", DeviceId.New().ToString());
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/skopka-chat/v1/deliveries"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-account");
            request.Headers.Add("X-Device-Id", DeviceId.New().ToString());
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var device = await new DeviceIdentityService(new InMemoryDeviceKeyStore()).CreateAsync(UserId.New(), DeviceId.New(), BindingProtocolTests.Now);
        using var cancelHttp = host.Http();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.Client(cancelHttp, device, "synthetic-account")
            .IssueAsync(DeviceBindingOperation.Enrollment, device, cancelled.Token));
    }

    [Fact]
    public async Task Foreign_session_proof_and_expired_authentication_cannot_bind_or_access_ordinary_routes()
    {
        await using var host = await Host.CreateAsync();
        var keys = new InMemoryDeviceKeyStore();
        var device = await new DeviceIdentityService(keys).CreateAsync(UserId.New(), DeviceId.New(), BindingProtocolTests.Now);
        var context = host.AddSession("synthetic-first", device.UserId, "first");
        host.AddSession("synthetic-second", device.UserId, "second");
        using var firstHttp = host.Http();
        using var secondHttp = host.Http();
        var first = host.Client(firstHttp, device, "synthetic-first");
        var second = host.Client(secondHttp, device, "synthetic-second");
        var challenge = await first.IssueAsync(DeviceBindingOperation.Enrollment, device);
        var proof = await new DeviceBindingProofService(keys, host.Clock).CreateProofAsync(challenge, context, device, challenge.Operation);
        var rejected = await Assert.ThrowsAsync<ChatHttpTransportException>(async () => await second.CompleteAsync(proof));
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        foreach (var route in new[] { "/envelopes", "/deliveries/" + Guid.NewGuid().ToString("D") + "/acknowledgements" })
        {
            using var request = Request(route, "synthetic-second", "{}");
            using var response = await secondHttp.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        await first.CompleteAsync(proof);
        host.Clock.Now = context.ExpiresAt;
        var expired = await Assert.ThrowsAsync<ChatHttpTransportException>(async () => await first.ReceiveAsync(device.DeviceId, 1));
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        await Assert.ThrowsAsync<ChatHttpTransportException>(async () => await first.CompleteAsync(proof));
    }

    [Theory]
    [InlineData("{\"payload\":\"AA==\",\"private-marker\":true}")]
    [InlineData("{\"payload\":null}")]
    [InlineData("oversized")]
    public async Task Hostile_bootstrap_response_is_bounded_generic_and_has_no_parser_inner_exception(string body)
    {
        var device = await new DeviceIdentityService(new InMemoryDeviceKeyStore()).CreateAsync(UserId.New(), DeviceId.New(), BindingProtocolTests.Now);
        using var http = new HttpClient(new ResponseHandler(body == "oversized" ? new string(' ', 4097) : body)) { BaseAddress = new Uri("https://localhost/") };
        var api = new SkopkaChatHttpClient(http, new TokensProvider("synthetic-private-token"), Options.Create(new SkopkaChatHttpClientOptions
        {
            AuthenticatedUserId = device.UserId.Value,
            AuthenticatedDeviceId = device.DeviceId.Value
        }), new BindingProtocolTests.Clock());
        var error = await Assert.ThrowsAsync<ChatHttpTransportException>(async () => await api.IssueAsync(DeviceBindingOperation.Enrollment, device));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-marker", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-token", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static HttpRequestMessage Request(string route, string token, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/skopka-chat/v1" + route) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static async Task<DeviceSessionBinding> BindAsync(SkopkaChatHttpClient api, IDeviceKeyStore keys, PublicDevice device,
        DeviceAuthorizationContext context, DeviceBindingOperation operation, TimeProvider clock)
    {
        var challenge = await api.IssueAsync(operation, device);
        var proof = await new DeviceBindingProofService(keys, clock).CreateProofAsync(challenge, context, device, operation);
        return await api.CompleteAsync(proof);
    }

    internal sealed class Host : IAsyncDisposable
    {
        public WebApplication App { get; private set; } = null!;
        public ConcurrentDictionary<string, DeviceAuthorizationContext> Tokens { get; } = new();
        public BindingProtocolTests.Clock Clock { get; } = new();
        public CapturedLogs Logs { get; } = new();
        public static async Task<Host> CreateAsync(string? database = null)
        {
            var host = new Host();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(host.Logs).SetMinimumLevel(LogLevel.Trace);
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton(host.Tokens);
            builder.Services.AddSingleton<TimeProvider>(host.Clock);
            builder.Services.AddAuthentication("TestBearer").AddScheme<AuthenticationSchemeOptions, TestBearer>("TestBearer", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(options =>
            {
                options.AddFixedWindowLimiter("skopka-chat-challenges", limit => { limit.PermitLimit = 1000; limit.Window = TimeSpan.FromMinutes(1); });
                options.AddFixedWindowLimiter("skopka-chat-proofs", limit => { limit.PermitLimit = 1000; limit.Window = TimeSpan.FromMinutes(1); });
            });
            builder.Services.AddScoped<IChatAuthorizationContextProvider, ContextProvider>();
            builder.Services.AddSingleton<IDeviceProofVerifier, NSecDeviceProofVerifier>();
            if (database is null)
            {
                var store = new InMemoryServerStore();
                builder.Services.AddSingleton<IDeviceRepository>(store);
                builder.Services.AddSingleton<IConversationRepository>(store);
                builder.Services.AddSingleton<IEnvelopeRepository>(store);
                builder.Services.AddSingleton<IDeviceBindingRepository>(store);
            }
            else
            {
                builder.Services.AddDbContext<ChatDbContext>(options => options.UseNpgsql(database));
                builder.Services.AddScoped<PostgreSqlChatStore>();
                builder.Services.AddScoped<IDeviceRepository>(services => services.GetRequiredService<PostgreSqlChatStore>());
                builder.Services.AddScoped<IConversationRepository>(services => services.GetRequiredService<PostgreSqlChatStore>());
                builder.Services.AddScoped<IEnvelopeRepository>(services => services.GetRequiredService<PostgreSqlChatStore>());
                builder.Services.AddScoped<IDeviceBindingRepository, PostgreSqlDeviceBindingStore>();
            }
            builder.Services.AddScoped<ChatServerEngine>();
            builder.Services.AddSkopkaChatDeviceBinding(options => options.ServiceId = "chat.example.test");
            host.App = builder.Build();
            host.App.UseAuthentication();
            host.App.UseAuthorization();
            host.App.UseRateLimiter();
            host.App.MapSkopkaChatApi();
            if (database is not null)
            {
                await using var scope = host.App.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
            }
            await host.App.StartAsync();
            return host;
        }
        public DeviceAuthorizationContext AddSession(string token, UserId user, string session)
        {
            var context = new DeviceAuthorizationContext("chat.example.test", user, session, BindingProtocolTests.Now.AddHours(1));
            Tokens[token] = context;
            return context;
        }
        public HttpClient Http() => new(App.GetTestServer().CreateHandler()) { BaseAddress = new Uri("https://localhost/") };
        public SkopkaChatHttpClient Client(HttpClient http, PublicDevice device, string token) => new(http, new TokensProvider(token),
            Options.Create(new SkopkaChatHttpClientOptions
            {
                AuthenticatedUserId = device.UserId.Value,
                AuthenticatedDeviceId = device.DeviceId.Value,
                RetryDelay = TimeSpan.Zero
            }), Clock);
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private sealed class TokensProvider(string token) : IAccessTokenProvider
    {
        public ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ChatAccessToken(token));
        }
    }

    internal sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
        public void Dispose() { }
        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }
    private sealed class TestBearer(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        ConcurrentDictionary<string, DeviceAuthorizationContext> tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
                header.Scheme != "Bearer" || header.Parameter is null || !tokens.TryGetValue(header.Parameter, out var context))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", context.UserId.Value.ToString("D")),
                new Claim("sid", context.SessionReference), new Claim("service", context.ServiceId),
                new Claim("session_deadline", context.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
                // Even a valid but irrelevant device claim must not authorize an unbound session.
                new Claim("skopka_chat_device_id", Guid.NewGuid().ToString("D")) }, "TestBearer"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "TestBearer")));
        }
    }
    private sealed class ContextProvider : IChatAuthorizationContextProvider
    {
        public ValueTask<DeviceAuthorizationContext?> GetContextAsync(Microsoft.AspNetCore.Http.HttpContext http, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claims = http.User.Identities.Where(identity => identity.IsAuthenticated).SelectMany(identity => identity.Claims).ToArray();
            string One(string name) => claims.Single(claim => claim.Type == name).Value;
            return ValueTask.FromResult<DeviceAuthorizationContext?>(new DeviceAuthorizationContext(One("service"), new UserId(Guid.Parse(One("sub"))),
                One("sid"), DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(One("session_deadline"), CultureInfo.InvariantCulture))));
        }
    }
    private sealed class LoseCompletionResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var completion = request.RequestUri!.AbsolutePath.EndsWith("/completions", StringComparison.Ordinal);
            if (completion) { Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken)); }
            var response = await base.SendAsync(request, cancellationToken);
            if (completion && Bodies.Count == 1 && response.IsSuccessStatusCode)
            {
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            }
            return response;
        }
    }
}
