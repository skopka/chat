using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skopka.Chat.Attachments;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Server.AspNetCore.Tests;

public sealed class AttachmentEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Authenticated_attachment_endpoint_round_trips_ciphertext_only()
    {
        var identity = new Identity(UserId.New(), DeviceId.New());
        var conversationId = ConversationId.New();
        var attachmentId = AttachmentId.New();
        var ciphertext = Enumerable.Range(0, 512).Select(static value => (byte)(value % 251)).ToArray();
        await using var application = await CreateApplicationAsync(identity, conversationId);
        using var client = application.GetTestClient();
        using var upload = AuthorizedRequest(
            HttpMethod.Put,
            $"/skopka-chat/v1/attachments/{attachmentId.Value:D}",
            identity);
        upload.Content = new ByteArrayContent(ciphertext);
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Headers.Add(SkopkaChatAttachmentHeaders.ConversationId, conversationId.ToString());
        upload.Headers.Add(SkopkaChatAttachmentHeaders.CiphertextSha256, Convert.ToHexString(SHA256.HashData(ciphertext)));

        using var uploadResponse = await client.SendAsync(upload);

        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        using var download = AuthorizedRequest(
            HttpMethod.Get,
            $"/skopka-chat/v1/attachments/{attachmentId.Value:D}",
            identity);
        using var downloadResponse = await client.SendAsync(download);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("application/octet-stream", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ciphertext, await downloadResponse.Content.ReadAsByteArrayAsync());

        var store = Assert.IsType<InMemoryAttachmentStore>(
            application.Services.GetRequiredService<IAttachmentStore>());
        Assert.Equal(ciphertext, store.Ciphertext);
        Assert.DoesNotContain(
            store.MetadataPropertyNames,
            static property => property.Contains("FileName", StringComparison.OrdinalIgnoreCase) ||
                property.Contains("MediaType", StringComparison.OrdinalIgnoreCase) ||
                property.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Typed_client_uploads_then_authenticates_and_decrypts_through_the_real_endpoint()
    {
        var identity = new Identity(UserId.New(), DeviceId.New());
        var conversationId = ConversationId.New();
        var plaintext = Enumerable.Range(0, 9000).Select(static value => (byte)(value % 251)).ToArray();
        await using var application = await CreateApplicationAsync(identity, conversationId);
        using var authenticationHandler = new TestHeaderHandler(identity)
        {
            InnerHandler = application.GetTestServer().CreateHandler()
        };
        using var http = new HttpClient(authenticationHandler) { BaseAddress = new Uri("http://localhost/") };
        var api = new SkopkaChatHttpClient(
            http,
            new TokenProvider(),
            Options.Create(new SkopkaChatHttpClientOptions
            {
                AuthenticatedUserId = identity.UserId.Value,
                AuthenticatedDeviceId = identity.DeviceId.Value,
                RequireHttps = false,
                MaxTransientRetries = 0
            }),
            new FixedTimeProvider(Now));
        await using var plaintextSource = new MemoryStream(plaintext, writable: false);
        await using var encrypted = new MemoryStream();
        var manifest = await ChatAttachmentCryptoService.EncryptAsync(
            plaintextSource,
            plaintext.Length,
            encrypted,
            AttachmentId.New(),
            ChatContentId.New(),
            "media.bin",
            "application/octet-stream",
            chunkPlaintextBytes: ChatAttachmentCryptoService.MinChunkPlaintextBytes);
        encrypted.Position = 0;

        Assert.Equal(
            AttachmentStoreResult.Stored,
            await api.UploadAttachmentAsync(conversationId, manifest, encrypted));
        await using var decrypted = new MemoryStream();
        await api.DownloadAndDecryptAttachmentAsync(conversationId, manifest, decrypted);

        Assert.Equal(plaintext, decrypted.ToArray());
    }

    [Fact]
    public async Task Attachment_endpoint_rejects_duplicate_or_malformed_metadata_before_storage()
    {
        var identity = new Identity(UserId.New(), DeviceId.New());
        var conversationId = ConversationId.New();
        await using var application = await CreateApplicationAsync(identity, conversationId);
        using var client = application.GetTestClient();
        using var request = AuthorizedRequest(
            HttpMethod.Put,
            $"/skopka-chat/v1/attachments/{AttachmentId.New().Value:D}",
            identity);
        request.Content = new ByteArrayContent([1, 2, 3]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation(
            SkopkaChatAttachmentHeaders.ConversationId,
            [conversationId.ToString(), ConversationId.New().ToString()]);
        request.Headers.Add(SkopkaChatAttachmentHeaders.CiphertextSha256, new string('0', 64));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var store = Assert.IsType<InMemoryAttachmentStore>(
            application.Services.GetRequiredService<IAttachmentStore>());
        Assert.Null(store.Ciphertext);
    }

    private static async Task<WebApplication> CreateApplicationAsync(
        Identity identity,
        ConversationId allowedConversationId)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
        builder.Services.AddAuthorization();
        var chatStore = new InMemoryServerStore();
        await ((IDeviceRepository)chatStore).TryAddAsync(new PublicDevice(
            identity.UserId,
            identity.DeviceId,
            KeyId.New(),
            Enumerable.Repeat((byte)1, ProtocolLimits.X25519PublicKeyBytes).ToArray(),
            Enumerable.Repeat((byte)2, ProtocolLimits.Ed25519PublicKeyBytes).ToArray(),
            Now));
        var attachmentStore = new InMemoryAttachmentStore();
        builder.Services.AddSingleton<IDeviceRepository>(chatStore);
        builder.Services.AddSingleton<IConversationRepository>(chatStore);
        builder.Services.AddSingleton<IEnvelopeRepository>(chatStore);
        builder.Services.AddSingleton<ChatServerEngine>();
        builder.Services.AddSingleton<IAttachmentStore>(attachmentStore);
        builder.Services.AddSingleton<IAttachmentAccessAuthorizer>(
            new AllowingAuthorizer(identity.UserId, allowedConversationId));
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        builder.Services.AddSkopkaChatAspNetCore();
        builder.Services.AddSkopkaChatAttachmentStorage();

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapSkopkaChatApi();
        await application.StartAsync();
        return application;
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        Identity identity)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthenticationHandler.UserHeader, identity.UserId.ToString());
        request.Headers.Add(TestAuthenticationHandler.DeviceHeader, identity.DeviceId.ToString());
        return request;
    }

    private sealed record Identity(UserId UserId, DeviceId DeviceId);

    private sealed class TokenProvider : IAccessTokenProvider
    {
        public ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ChatAccessToken("synthetic-token", Now.AddHours(1)));
    }

    private sealed class TestHeaderHandler(Identity identity) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Add(TestAuthenticationHandler.UserHeader, identity.UserId.ToString());
            request.Headers.Add(TestAuthenticationHandler.DeviceHeader, identity.DeviceId.ToString());
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class InMemoryAttachmentStore : IAttachmentStore
    {
        public StoredAttachment? Metadata { get; private set; }
        public byte[]? Ciphertext { get; private set; }
        public IReadOnlyList<string> MetadataPropertyNames { get; } =
            typeof(StoredAttachment).GetProperties().Select(static property => property.Name).ToArray();

        public ValueTask<StoredAttachment?> GetMetadataAsync(
            AttachmentId attachmentId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Metadata?.AttachmentId == attachmentId ? Metadata : null);

        public async ValueTask<AttachmentStoreResult> TryPutAsync(
            StoredAttachment attachment,
            Stream ciphertext,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await ciphertext.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            if (bytes.LongLength != attachment.CiphertextLength ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), attachment.CiphertextSha256.Span))
            {
                throw new InvalidDataException();
            }

            Metadata = attachment;
            Ciphertext = bytes;
            return AttachmentStoreResult.Stored;
        }

        public async ValueTask CopyToAsync(
            AttachmentId attachmentId,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            if (Metadata?.AttachmentId != attachmentId || Ciphertext is null)
            {
                throw new InvalidOperationException();
            }

            await destination.WriteAsync(Ciphertext, cancellationToken);
        }

        public ValueTask<bool> DeleteAsync(
            AttachmentId attachmentId,
            CancellationToken cancellationToken = default)
        {
            if (Metadata?.AttachmentId != attachmentId)
            {
                return ValueTask.FromResult(false);
            }

            Metadata = null;
            Ciphertext = null;
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> DeleteExpiredAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }

    private sealed class AllowingAuthorizer(UserId userId, ConversationId conversationId)
        : IAttachmentAccessAuthorizer
    {
        public ValueTask<bool> CanUploadAsync(
            UserId authenticatedUserId,
            ConversationId requestedConversationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authenticatedUserId == userId && requestedConversationId == conversationId);

        public ValueTask<bool> CanDownloadAsync(
            UserId authenticatedUserId,
            StoredAttachment attachment,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authenticatedUserId == userId && attachment.ConversationId == conversationId);

        public ValueTask<bool> CanDeleteAsync(
            UserId authenticatedUserId,
            StoredAttachment attachment,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authenticatedUserId == userId && attachment.ConversationId == conversationId);
    }

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
        internal const string SchemeName = "AttachmentTestHeaders";
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
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
