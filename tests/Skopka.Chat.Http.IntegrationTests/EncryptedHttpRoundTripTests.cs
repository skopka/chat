using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Server.AspNetCore;
using Skopka.Chat.Testing;

namespace Skopka.Chat.Http.IntegrationTests;

public sealed class EncryptedHttpRoundTripTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Alice_to_http_server_to_Bob_preserves_e2ee_and_authenticated_device_binding()
    {
        var aliceKeyStore = new InMemoryDeviceKeyStore();
        var bobKeyStore = new InMemoryDeviceKeyStore();
        var alice = await new DeviceIdentityService(aliceKeyStore)
            .CreateAsync(UserId.New(), DeviceId.New(), Now);
        var bob = await new DeviceIdentityService(bobKeyStore)
            .CreateAsync(UserId.New(), DeviceId.New(), Now);
        var tokens = new TestTokenRegistry();
        tokens.Add("alice-token", alice.UserId, alice.DeviceId);
        tokens.Add("bob-token", bob.UserId, bob.DeviceId);
        await using var application = await CreateApplicationAsync(tokens);
        using var aliceHttp = application.GetTestClient();
        using var bobHttp = application.GetTestClient();
        var aliceApi = CreateClient(aliceHttp, alice, "alice-token");
        var bobApi = CreateClient(bobHttp, bob, "bob-token");

        var registeredAlice = await aliceApi.RegisterDeviceAsync(alice);
        var registeredBob = await bobApi.RegisterDeviceAsync(bob);
        var conversationId = ConversationId.New();
        await aliceApi.CreateConversationAsync(bob.UserId, conversationId);
        var bobFromDirectory = Assert.IsType<PublicDevice>(
            await aliceApi.GetDeviceAsync(bob.DeviceId));
        const string plaintext = "HTTP server must never see this marker: 94A6CE21.";
        var envelope = await new ChatCryptoService(aliceKeyStore).EncryptTextAsync(
            plaintext,
            conversationId,
            MessageId.New(),
            alice.DeviceId,
            bobFromDirectory,
            Now,
            Now.AddDays(1));

        Assert.Equal(TransportSendStatus.Accepted, await aliceApi.SendAsync(envelope));
        var stored = Assert.Single(application.Services
            .GetRequiredService<InMemoryServerStore>()
            .SnapshotEnvelopes());
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        Assert.True(stored.Envelope.Ciphertext.Span.IndexOf(plaintextBytes) < 0);

        var delivery = Assert.Single(await bobApi.ReceiveAsync(bob.DeviceId, 10));
        var aliceFromDirectory = Assert.IsType<PublicDevice>(
            await bobApi.GetDeviceAsync(alice.DeviceId));
        var receiver = new ChatReceiver(
            new ChatCryptoService(bobKeyStore),
            new InMemoryReceivedMessageStore());
        var received = await receiver.ReceiveAsync(delivery.Envelope, aliceFromDirectory);

        Assert.True(received.Added);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(received.Message!.ExportPlaintext()));
        Assert.Equal(registeredAlice.DeviceId, aliceFromDirectory.DeviceId);
        Assert.Equal(registeredBob.DeviceId, bobFromDirectory.DeviceId);
        await bobApi.AcknowledgeAsync(bob.DeviceId, envelope.MessageId, Now.AddSeconds(1));
        Assert.Empty(await bobApi.ReceiveAsync(bob.DeviceId, 10));
    }

    [Fact]
    public async Task Alice_to_http_to_PostgreSql_to_Bob_preserves_e2ee()
    {
        var connectionString = await GetPostgreSqlConnectionStringOrSkipAsync();
        var aliceKeyStore = new InMemoryDeviceKeyStore();
        var bobKeyStore = new InMemoryDeviceKeyStore();
        var alice = await new DeviceIdentityService(aliceKeyStore)
            .CreateAsync(UserId.New(), DeviceId.New(), Now);
        var bob = await new DeviceIdentityService(bobKeyStore)
            .CreateAsync(UserId.New(), DeviceId.New(), Now);
        var tokens = new TestTokenRegistry();
        tokens.Add("postgres-alice-token", alice.UserId, alice.DeviceId);
        tokens.Add("postgres-bob-token", bob.UserId, bob.DeviceId);
        var conversationId = ConversationId.New();
        var messageId = MessageId.New();
        await using var application = await CreateApplicationAsync(tokens, connectionString);
        using var aliceHttp = application.GetTestClient();
        using var bobHttp = application.GetTestClient();
        var aliceApi = CreateClient(aliceHttp, alice, "postgres-alice-token");
        var bobApi = CreateClient(bobHttp, bob, "postgres-bob-token");

        try
        {
            await aliceApi.RegisterDeviceAsync(alice);
            await bobApi.RegisterDeviceAsync(bob);
            await aliceApi.CreateConversationAsync(bob.UserId, conversationId);
            var bobFromDirectory = Assert.IsType<PublicDevice>(
                await aliceApi.GetDeviceAsync(bob.DeviceId));
            const string plaintext = "PostgreSQL must never see this marker: 7E3CC90B.";
            var envelope = await new ChatCryptoService(aliceKeyStore).EncryptTextAsync(
                plaintext,
                conversationId,
                messageId,
                alice.DeviceId,
                bobFromDirectory,
                Now,
                Now.AddDays(1));

            Assert.Equal(TransportSendStatus.Accepted, await aliceApi.SendAsync(envelope));
            await using (var scope = application.Services.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IEnvelopeRepository>();
                var persisted = Assert.Single(await repository.GetPendingAsync(bob.DeviceId, 10, Now));
                Assert.Equal(envelope.Ciphertext.ToArray(), persisted.Envelope.Ciphertext.ToArray());
                Assert.True(persisted.Envelope.Ciphertext.Span.IndexOf(Encoding.UTF8.GetBytes(plaintext)) < 0);
            }

            var delivery = Assert.Single(await bobApi.ReceiveAsync(bob.DeviceId, 10));
            var aliceFromDirectory = Assert.IsType<PublicDevice>(
                await bobApi.GetDeviceAsync(alice.DeviceId));
            var receiver = new ChatReceiver(
                new ChatCryptoService(bobKeyStore),
                new InMemoryReceivedMessageStore());
            var received = await receiver.ReceiveAsync(delivery.Envelope, aliceFromDirectory);

            Assert.True(received.Added);
            Assert.Equal(plaintext, Encoding.UTF8.GetString(received.Message!.ExportPlaintext()));
            await bobApi.AcknowledgeAsync(bob.DeviceId, messageId, Now.AddSeconds(1));
            Assert.Empty(await bobApi.ReceiveAsync(bob.DeviceId, 10));
        }
        finally
        {
            await CleanupPostgreSqlAsync(application, messageId, conversationId, alice.DeviceId, bob.DeviceId);
        }
    }

    private static async Task<WebApplication> CreateApplicationAsync(
        TestTokenRegistry tokens,
        string? postgreSqlConnectionString = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton(tokens);
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestBearerAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestBearerAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TestBearerAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestBearerAuthenticationHandler>(
                TestBearerAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization();

        if (postgreSqlConnectionString is null)
        {
            var store = new InMemoryServerStore();
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<IDeviceRepository>(store);
            builder.Services.AddSingleton<IConversationRepository>(store);
            builder.Services.AddSingleton<IEnvelopeRepository>(store);
        }
        else
        {
            builder.Services.AddDbContext<ChatDbContext>(options => options.UseNpgsql(postgreSqlConnectionString));
            builder.Services.AddScoped<PostgreSqlChatStore>();
            builder.Services.AddScoped<IDeviceRepository>(services =>
                services.GetRequiredService<PostgreSqlChatStore>());
            builder.Services.AddScoped<IConversationRepository>(services =>
                services.GetRequiredService<PostgreSqlChatStore>());
            builder.Services.AddScoped<IEnvelopeRepository>(services =>
                services.GetRequiredService<PostgreSqlChatStore>());
        }

        builder.Services.AddScoped<ChatServerEngine>();
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        builder.Services.AddSkopkaChatAspNetCore();

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapSkopkaChatApi();
        if (postgreSqlConnectionString is not null)
        {
            await using var scope = application.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
        }

        await application.StartAsync();
        return application;
    }

    private static ValueTask<string> GetPostgreSqlConnectionStringOrSkipAsync() =>
        PostgreSqlTestDatabase.GetConnectionStringOrSkipAsync();

    private static async Task CleanupPostgreSqlAsync(
        WebApplication application,
        MessageId messageId,
        ConversationId conversationId,
        DeviceId aliceDeviceId,
        DeviceId bobDeviceId)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM envelopes WHERE message_id = {messageId.Value}");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM conversations WHERE conversation_id = {conversationId.Value}");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM devices WHERE device_id = {aliceDeviceId.Value} OR device_id = {bobDeviceId.Value}");
    }

    private static SkopkaChatHttpClient CreateClient(
        HttpClient httpClient,
        PublicDevice identity,
        string token)
    {
        var options = Options.Create(new SkopkaChatHttpClientOptions
        {
            AuthenticatedUserId = identity.UserId.Value,
            AuthenticatedDeviceId = identity.DeviceId.Value,
            RequireHttps = false,
            MaxTransientRetries = 0
        });
        return new SkopkaChatHttpClient(
            httpClient,
            new StaticTokenProvider(new ChatAccessToken(token, Now.AddHours(1))),
            options,
            new FixedTimeProvider(Now));
    }

    private sealed class StaticTokenProvider(ChatAccessToken token) : IAccessTokenProvider
    {
        public ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(token);
        }
    }

    private sealed class TestTokenRegistry
    {
        private readonly Dictionary<string, ChatIdentity> _tokens = new(StringComparer.Ordinal);

        public void Add(string token, UserId userId, DeviceId deviceId) =>
            _tokens.Add(token, new ChatIdentity(userId, deviceId));

        public bool TryGet(string token, out ChatIdentity identity) =>
            _tokens.TryGetValue(token, out identity);
    }

    private readonly record struct ChatIdentity(UserId UserId, DeviceId DeviceId);

    private sealed class TestBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestTokenRegistry tokens)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "TestBearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
                !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                header.Parameter is null || !tokens.TryGet(header.Parameter, out var identity))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, identity.UserId.Value.ToString("D")),
                new Claim("skopka_chat_device_id", identity.DeviceId.Value.ToString("D"))
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
