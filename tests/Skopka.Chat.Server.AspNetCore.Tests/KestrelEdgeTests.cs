using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Server.AspNetCore.Tests;

public sealed class KestrelEdgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Declared_oversized_body_is_rejected_by_Kestrel_before_state_change()
    {
        var store = new InMemoryServerStore();
        await using var application = await CreateApplicationAsync(store, store, store);
        using var client = CreateClient(application);
        var identity = new TestIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var request = AuthorizedRequest(HttpMethod.Post, "/skopka-chat/v1/devices", identity);
        request.Content = new ByteArrayContent(new byte[SkopkaChatHttpLimits.MaxRequestBodyBytes + 1]);
        request.Content.Headers.ContentType = new("application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(await ((IDeviceRepository)store).GetAsync(new DeviceId(identity.DeviceId)));
    }

    [Fact]
    public async Task Chunked_oversized_body_is_rejected_while_streaming_without_state_change()
    {
        var store = new InMemoryServerStore();
        await using var application = await CreateApplicationAsync(store, store, store);
        using var client = CreateClient(application);
        var identity = new TestIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var request = AuthorizedRequest(HttpMethod.Post, "/skopka-chat/v1/devices", identity);
        request.Headers.TransferEncodingChunked = true;
        request.Content = new OversizedChunkedJsonContent();

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(await ((IDeviceRepository)store).GetAsync(new DeviceId(identity.DeviceId)));
    }

    [Fact]
    public async Task Client_disconnect_cancels_in_flight_repository_operation()
    {
        var store = new CancellationObservingStore();
        await using var application = await CreateApplicationAsync(store, store, store);
        using var client = CreateClient(application);
        var identity = new TestIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var request = AuthorizedRequest(HttpMethod.Post, "/skopka-chat/v1/devices", identity);
        request.Content = JsonContent.Create(
            DeviceRegistration(identity.DeviceId),
            SkopkaChatHttpJsonContext.Default.RegisterDeviceRequest);
        using var cancellation = new CancellationTokenSource();

        var send = client.SendAsync(request, cancellation.Token);
        await store.OperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await send);
        await store.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<WebApplication> CreateApplicationAsync(
        IDeviceRepository devices,
        IConversationRepository conversations,
        IEnvelopeRepository envelopes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = HeaderAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                HeaderAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(devices);
        builder.Services.AddSingleton(conversations);
        builder.Services.AddSingleton(envelopes);
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

    private static HttpClient CreateClient(WebApplication application)
    {
        var server = application.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>() ??
            throw new InvalidOperationException("Kestrel did not publish a server address.");
        var address = Assert.Single(addresses.Addresses);
        return new HttpClient
        {
            BaseAddress = new Uri(address, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        TestIdentity identity)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(HeaderAuthenticationHandler.UserHeader, identity.UserId.ToString("D"));
        request.Headers.Add(HeaderAuthenticationHandler.DeviceHeader, identity.DeviceId.ToString("D"));
        return request;
    }

    private static RegisterDeviceRequest DeviceRegistration(Guid deviceId) => new(
        deviceId,
        Guid.NewGuid(),
        Enumerable.Repeat((byte)0x11, ProtocolLimits.X25519PublicKeyBytes).ToArray(),
        Enumerable.Repeat((byte)0x22, ProtocolLimits.Ed25519PublicKeyBytes).ToArray());

    private sealed record TestIdentity(Guid UserId, Guid DeviceId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "KestrelTestHeaders";
        internal const string UserHeader = "X-Test-User";
        internal const string DeviceHeader = "X-Test-Device";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userId) ||
                !Request.Headers.TryGetValue(DeviceHeader, out var deviceId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("skopka_chat_device_id", deviceId.ToString())
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class OversizedChunkedJsonContent : HttpContent
    {
        private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("{\"deviceId\":\"");
        private static readonly byte[] Block = Enumerable.Repeat((byte)'A', 8 * 1024).ToArray();

        public OversizedChunkedJsonContent()
        {
            Headers.ContentType = new("application/json");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(Prefix, cancellationToken);
            var remaining = SkopkaChatHttpLimits.MaxRequestBodyBytes + Block.Length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, Block.Length);
                await stream.WriteAsync(Block.AsMemory(0, count), cancellationToken);
                remaining -= count;
            }
        }
    }

    private sealed class CancellationObservingStore :
        IDeviceRepository,
        IConversationRepository,
        IEnvelopeRepository
    {
        public TaskCompletionSource OperationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<bool> TryAddAsync(
            PublicDevice device,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<bool>(new InvalidOperationException("The cancellation test must not add a device."));

        public async ValueTask<PublicDevice?> GetAsync(
            DeviceId deviceId,
            CancellationToken cancellationToken = default)
        {
            OperationEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public ValueTask<bool> RevokeAsync(
            DeviceId deviceId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<bool>(new InvalidOperationException("The cancellation test must not revoke a device."));

        public ValueTask<bool> TryAddAsync(
            PersonalConversation conversation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<bool>(new InvalidOperationException("The cancellation test must not add a conversation."));

        public ValueTask<PersonalConversation?> GetAsync(
            ConversationId conversationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<PersonalConversation?>(
                new InvalidOperationException("The cancellation test must not load a conversation."));

        public ValueTask<EnvelopeStoreResult> TryAddAsync(
            EncryptedEnvelope envelope,
            DateTimeOffset acceptedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<EnvelopeStoreResult>(
                new InvalidOperationException("The cancellation test must not add an envelope."));

        public ValueTask<IReadOnlyList<StoredEnvelope>> GetPendingAsync(
            DeviceId recipientDeviceId,
            int maximumCount,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<StoredEnvelope>>(
                new InvalidOperationException("The cancellation test must not poll deliveries."));

        public ValueTask<bool> AcknowledgeAsync(
            DeviceId recipientDeviceId,
            MessageId messageId,
            DateTimeOffset acknowledgedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<bool>(new InvalidOperationException("The cancellation test must not acknowledge."));

        public ValueTask<int> DeleteExpiredAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("The cancellation test must not delete."));
    }
}
