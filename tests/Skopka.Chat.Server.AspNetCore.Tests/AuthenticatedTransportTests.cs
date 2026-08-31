using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;

namespace Skopka.Chat.Server.AspNetCore.Tests;

public sealed class AuthenticatedTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Route_group_rejects_unauthenticated_requests()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();

        using var response = await client.GetAsync("/skopka-chat/v1/deliveries");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ambiguous_device_claim_is_forbidden()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        using var request = AuthorizedRequest(HttpMethod.Post, "/skopka-chat/v1/devices", userId, deviceId);
        request.Headers.Add(TestAuthenticationHandler.DuplicateDeviceHeader, Guid.NewGuid().ToString("D"));
        request.Content = JsonContent.Create(DeviceRegistration(deviceId));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Registration_cannot_bind_a_different_device_than_the_claim()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var userId = Guid.NewGuid();
        var claimedDeviceId = Guid.NewGuid();
        var submittedDeviceId = Guid.NewGuid();
        using var request = AuthorizedRequest(HttpMethod.Post, "/skopka-chat/v1/devices", userId, claimedDeviceId);
        request.Content = JsonContent.Create(DeviceRegistration(submittedDeviceId));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var repository = (IDeviceRepository)application.Services.GetRequiredService<InMemoryServerStore>();
        Assert.Null(await repository.GetAsync(new DeviceId(submittedDeviceId)));
    }

    [Fact]
    public async Task Foreign_user_cannot_revoke_another_users_device()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var alice = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var mallory = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await RegisterAsync(client, alice);
        await RegisterAsync(client, mallory);
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            $"/skopka-chat/v1/devices/{alice.DeviceId:D}/revocation",
            mallory.UserId,
            mallory.DeviceId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var repository = (IDeviceRepository)application.Services.GetRequiredService<InMemoryServerStore>();
        var stored = await repository.GetAsync(new DeviceId(alice.DeviceId));
        Assert.NotNull(stored);
        Assert.False(stored.IsRevoked);
    }

    [Fact]
    public async Task Foreign_user_cannot_pair_its_claim_with_the_senders_real_device()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var alice = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bob = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await RegisterAsync(client, alice);
        await RegisterAsync(client, bob);
        var conversationId = await CreateConversationAsync(client, alice, bob.UserId);
        var envelope = Envelope(conversationId, alice, bob);
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            "/skopka-chat/v1/envelopes",
            bob.UserId,
            alice.DeviceId);
        request.Content = JsonContent.Create(envelope);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(application.Services.GetRequiredService<InMemoryServerStore>().SnapshotEnvelopes());
    }

    [Fact]
    public async Task Authenticated_round_trip_derives_recipient_from_claims_and_is_idempotent()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var alice = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bob = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await RegisterAsync(client, alice);
        await RegisterAsync(client, bob);
        var conversationId = await CreateConversationAsync(client, alice, bob.UserId);
        var envelope = Envelope(conversationId, alice, bob);

        using var firstSubmit = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/skopka-chat/v1/envelopes",
            alice,
            envelope);
        using var duplicateSubmit = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/skopka-chat/v1/envelopes",
            alice,
            envelope);

        Assert.Equal(HttpStatusCode.Accepted, firstSubmit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicateSubmit.StatusCode);
        var duplicate = await duplicateSubmit.Content.ReadFromJsonAsync<SubmitEnvelopeResponse>();
        Assert.NotNull(duplicate);
        Assert.True(duplicate.Duplicate);

        using var poll = AuthorizedRequest(
            HttpMethod.Get,
            "/skopka-chat/v1/deliveries?take=10",
            bob.UserId,
            bob.DeviceId);
        using var pollResponse = await client.SendAsync(poll);
        var deliveries = await pollResponse.Content.ReadFromJsonAsync<PendingDeliveryResponse[]>();
        Assert.Equal(HttpStatusCode.OK, pollResponse.StatusCode);
        var delivery = Assert.Single(Assert.IsType<PendingDeliveryResponse[]>(deliveries));
        Assert.Equal(envelope.MessageId, delivery.Envelope.MessageId);
        Assert.Equal(envelope.Ciphertext, delivery.Envelope.Ciphertext);

        using var acknowledgement = AuthorizedRequest(
            HttpMethod.Post,
            $"/skopka-chat/v1/deliveries/{envelope.MessageId:D}/acknowledgements",
            bob.UserId,
            bob.DeviceId);
        using var acknowledgementResponse = await client.SendAsync(acknowledgement);
        Assert.Equal(HttpStatusCode.NoContent, acknowledgementResponse.StatusCode);

        using var emptyPoll = AuthorizedRequest(
            HttpMethod.Get,
            "/skopka-chat/v1/deliveries",
            bob.UserId,
            bob.DeviceId);
        using var emptyPollResponse = await client.SendAsync(emptyPoll);
        var remaining = await emptyPollResponse.Content.ReadFromJsonAsync<PendingDeliveryResponse[]>();
        Assert.Empty(Assert.IsType<PendingDeliveryResponse[]>(remaining));
    }

    [Fact]
    public async Task Oversized_ciphertext_is_rejected_without_persistence()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var alice = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bob = new TestIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await RegisterAsync(client, alice);
        await RegisterAsync(client, bob);
        var conversationId = await CreateConversationAsync(client, alice, bob.UserId);
        var envelope = Envelope(conversationId, alice, bob) with
        {
            Ciphertext = new byte[ProtocolLimits.MaxCiphertextBytes + 1]
        };

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/skopka-chat/v1/envelopes",
            alice,
            envelope);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(application.Services.GetRequiredService<InMemoryServerStore>().SnapshotEnvelopes());
    }

    [Fact]
    public void Transport_assembly_has_no_client_dependency()
    {
        var references = typeof(SkopkaChatEndpointRouteBuilderExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("Skopka.Chat.Client", references);
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization();

        var store = new InMemoryServerStore();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IDeviceRepository>(store);
        builder.Services.AddSingleton<IConversationRepository>(store);
        builder.Services.AddSingleton<IEnvelopeRepository>(store);
        builder.Services.AddSingleton<ChatServerEngine>();
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        builder.Services.AddSkopkaChatAspNetCore();

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapSkopkaChatApi();
        await application.StartAsync();
        return application;
    }

    private static async Task RegisterAsync(HttpClient client, TestIdentity identity)
    {
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/skopka-chat/v1/devices",
            identity,
            DeviceRegistration(identity.DeviceId, identity.KeyId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Guid> CreateConversationAsync(
        HttpClient client,
        TestIdentity caller,
        Guid peerUserId)
    {
        var conversationId = Guid.NewGuid();
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/skopka-chat/v1/conversations",
            caller,
            new CreateConversationRequest(conversationId, peerUserId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return conversationId;
    }

    private static async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        TestIdentity identity,
        T body)
    {
        using var request = AuthorizedRequest(method, path, identity.UserId, identity.DeviceId);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        Guid userId,
        Guid deviceId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, userId.ToString("D"));
        request.Headers.Add(TestAuthenticationHandler.DeviceHeader, deviceId.ToString("D"));
        return request;
    }

    private static RegisterDeviceRequest DeviceRegistration(Guid deviceId, Guid? keyId = null) => new(
        deviceId,
        keyId ?? Guid.NewGuid(),
        Enumerable.Repeat((byte)0x11, ProtocolLimits.X25519PublicKeyBytes).ToArray(),
        Enumerable.Repeat((byte)0x22, ProtocolLimits.Ed25519PublicKeyBytes).ToArray());

    private static EncryptedEnvelopeDto Envelope(
        Guid conversationId,
        TestIdentity sender,
        TestIdentity recipient) => new(
        ProtocolVersions.Current,
        Guid.NewGuid(),
        conversationId,
        sender.DeviceId,
        recipient.DeviceId,
        sender.KeyId,
        recipient.KeyId,
        Now,
        Now.AddDays(1),
        Enumerable.Repeat((byte)0x33, ProtocolLimits.X25519PublicKeyBytes).ToArray(),
        Enumerable.Repeat((byte)0x44, ProtocolLimits.NonceBytes).ToArray(),
        [0x45, 0x32, 0x45, 0x45],
        Enumerable.Repeat((byte)0x55, ProtocolLimits.AuthenticationTagBytes).ToArray(),
        Enumerable.Repeat((byte)0x66, ProtocolLimits.SignatureBytes).ToArray());

    private sealed record TestIdentity(Guid UserId, Guid DeviceId, Guid KeyId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "TestHeaders";
        internal const string UserHeader = "X-Test-User";
        internal const string DeviceHeader = "X-Test-Device";
        internal const string DuplicateDeviceHeader = "X-Test-Duplicate-Device";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userId) ||
                !Request.Headers.TryGetValue(DeviceHeader, out var deviceId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new("skopka_chat_device_id", deviceId.ToString())
            };
            if (Request.Headers.TryGetValue(DuplicateDeviceHeader, out var duplicateDeviceId))
            {
                claims.Add(new Claim("skopka_chat_device_id", duplicateDeviceId.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
