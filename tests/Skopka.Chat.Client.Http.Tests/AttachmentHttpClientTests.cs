using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Skopka.Chat.Attachments;
using Skopka.Chat.Media;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Http.Tests;

public sealed class AttachmentHttpClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Typed_http_client_is_a_media_attachment_uploader()
    {
        var client = CreateClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        Assert.IsAssignableFrom<IEncryptedAttachmentUploader>(client);
    }

    [Fact]
    public async Task Upload_streams_ciphertext_with_strict_metadata_and_leaves_source_open()
    {
        var conversationId = ConversationId.New();
        var (manifest, encrypted) = await EncryptAsync("media payload"u8.ToArray());
        using var source = new MemoryStream(encrypted, writable: false);
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(
                $"/skopka-chat/v1/attachments/{manifest.AttachmentId.Value:D}",
                request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(conversationId.ToString(), Assert.Single(request.Headers.GetValues(
                SkopkaChatAttachmentHeaders.ConversationId)));
            Assert.Equal(
                Convert.ToHexString(manifest.CiphertextSha256.Span),
                Assert.Single(request.Headers.GetValues(SkopkaChatAttachmentHeaders.CiphertextSha256)));
            Assert.Equal(manifest.CiphertextLength, request.Content?.Headers.ContentLength);
            Assert.Equal(encrypted, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var client = CreateClient(handler);

        Assert.Equal(
            AttachmentStoreResult.Stored,
            await client.UploadAttachmentAsync(conversationId, manifest, source));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Download_validates_headers_and_streams_authenticated_plaintext()
    {
        var conversationId = ConversationId.New();
        var plaintext = Enumerable.Range(0, 9000).Select(static value => (byte)(value % 251)).ToArray();
        var (manifest, encrypted) = await EncryptAsync(plaintext);
        var handler = new DelegateHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var content = new ByteArrayContent(encrypted);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.Add(SkopkaChatAttachmentHeaders.ConversationId, conversationId.ToString());
            response.Headers.Add(
                SkopkaChatAttachmentHeaders.CiphertextSha256,
                Convert.ToHexString(manifest.CiphertextSha256.Span));
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);
        await using var destination = new MemoryStream();

        await client.DownloadAndDecryptAttachmentAsync(conversationId, manifest, destination);

        Assert.Equal(plaintext, destination.ToArray());
    }

    [Fact]
    public async Task Download_rejects_mismatched_storage_metadata_before_writing_plaintext()
    {
        var conversationId = ConversationId.New();
        var (manifest, encrypted) = await EncryptAsync("secret"u8.ToArray());
        var handler = new DelegateHandler((_, _) =>
        {
            var content = new ByteArrayContent(encrypted);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.Add(SkopkaChatAttachmentHeaders.ConversationId, ConversationId.New().ToString());
            response.Headers.Add(
                SkopkaChatAttachmentHeaders.CiphertextSha256,
                Convert.ToHexString(manifest.CiphertextSha256.Span));
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ChatHttpTransportException>(async () =>
            await client.DownloadAndDecryptAttachmentAsync(conversationId, manifest, destination));
        Assert.Empty(destination.ToArray());
    }

    private static async Task<(ChatAttachmentContent Manifest, byte[] Ciphertext)> EncryptAsync(byte[] plaintext)
    {
        await using var source = new MemoryStream(plaintext, writable: false);
        await using var encrypted = new MemoryStream();
        var manifest = await ChatAttachmentCryptoService.EncryptAsync(
            source,
            plaintext.Length,
            encrypted,
            AttachmentId.New(),
            ChatContentId.New(),
            "media.bin",
            "application/octet-stream",
            chunkPlaintextBytes: ChatAttachmentCryptoService.MinChunkPlaintextBytes);
        return (manifest, encrypted.ToArray());
    }

    private static SkopkaChatHttpClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://chat.example.test/") };
        return new SkopkaChatHttpClient(
            httpClient,
            new TokenProvider(),
            Options.Create(new SkopkaChatHttpClientOptions
            {
                AuthenticatedUserId = Guid.NewGuid(),
                AuthenticatedDeviceId = Guid.NewGuid(),
                MaxTransientRetries = 0
            }),
            new FixedTimeProvider(Now));
    }

    private sealed class TokenProvider : IAccessTokenProvider
    {
        public ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ChatAccessToken("synthetic-token", Now.AddHours(1)));
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await callback(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
