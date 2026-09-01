using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Skopka.Chat.Attachments;
using Skopka.Chat.Media;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Http;

/// <summary>Authenticated HTTP API client and <see cref="IChatTransport"/> implementation.</summary>
public sealed class SkopkaChatHttpClient : IChatTransport, IEncryptedAttachmentUploader
{
    private readonly HttpClient _httpClient;
    private readonly IAccessTokenProvider _accessTokens;
    private readonly SkopkaChatHttpClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Uri _baseAddress;
    private readonly UserId _authenticatedUserId;
    private readonly DeviceId _authenticatedDeviceId;

    /// <summary>
    /// Creates a client over a host-managed <see cref="HttpClient"/>. Automatic redirects must be disabled;
    /// <c>AddSkopkaChatHttpClient</c> configures that secure default.
    /// </summary>
    public SkopkaChatHttpClient(
        HttpClient httpClient,
        IAccessTokenProvider accessTokens,
        IOptions<SkopkaChatHttpClientOptions> options,
        TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessTokens = accessTokens ?? throw new ArgumentNullException(nameof(accessTokens));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _options.Validate();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _baseAddress = NormalizeBaseAddress(_httpClient.BaseAddress, _options.RequireHttps);
        _httpClient.BaseAddress = _baseAddress;
        _httpClient.Timeout = _options.RequestTimeout;
        _authenticatedUserId = new UserId(_options.AuthenticatedUserId);
        _authenticatedDeviceId = new DeviceId(_options.AuthenticatedDeviceId);
    }

    /// <summary>Registers the public keys of this client's authenticated device.</summary>
    public async ValueTask<PublicDevice> RegisterDeviceAsync(
        PublicDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ProtocolValidator.Validate(device);
        if (device.UserId != _authenticatedUserId || device.DeviceId != _authenticatedDeviceId || device.IsRevoked)
        {
            throw new ArgumentException("The public device does not match the authenticated HTTP client.", nameof(device));
        }

        var payload = RegisterDeviceRequest.FromDomain(device);
        using var response = await SendWithRetryAsync(
            () => CreateJsonRequest(
                HttpMethod.Post,
                SkopkaChatHttpRoutes.Devices,
                payload,
                SkopkaChatHttpJsonContext.Default.RegisterDeviceRequest),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var result = ToPublicDevice(await ReadJsonAsync(
            response,
            SkopkaChatHttpJsonContext.Default.PublicDeviceResponse,
            SkopkaChatHttpLimits.MaxControlResponseBytes,
            cancellationToken).ConfigureAwait(false));
        if (result.UserId != device.UserId || result.DeviceId != device.DeviceId || result.KeyId != device.KeyId ||
            !result.EncryptionPublicKey.Span.SequenceEqual(device.EncryptionPublicKey.Span) ||
            !result.SigningPublicKey.Span.SequenceEqual(device.SigningPublicKey.Span))
        {
            throw InvalidResponse();
        }

        return result;
    }

    /// <summary>Creates an idempotent personal conversation for the authenticated user and one peer.</summary>
    public async ValueTask<PersonalConversationResponse> CreateConversationAsync(
        UserId peerUserId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        RequireId(peerUserId.Value, nameof(peerUserId));
        RequireId(conversationId.Value, nameof(conversationId));
        if (peerUserId == _authenticatedUserId)
        {
            throw new ArgumentException("A personal conversation requires a different peer.", nameof(peerUserId));
        }

        var payload = new CreateConversationRequest(conversationId.Value, peerUserId.Value);
        using var response = await SendWithRetryAsync(
            () => CreateJsonRequest(
                HttpMethod.Post,
                SkopkaChatHttpRoutes.Conversations,
                payload,
                SkopkaChatHttpJsonContext.Default.CreateConversationRequest),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var result = await ReadJsonAsync(
            response,
            SkopkaChatHttpJsonContext.Default.PersonalConversationResponse,
            SkopkaChatHttpLimits.MaxControlResponseBytes,
            cancellationToken).ConfigureAwait(false);
        if (result.ConversationId != conversationId.Value || result.CreatedAt == default ||
            !HasExactParticipants(result, _authenticatedUserId.Value, peerUserId.Value))
        {
            throw InvalidResponse();
        }

        return result;
    }

    /// <summary>Revokes one device owned by the authenticated user.</summary>
    public async ValueTask RevokeDeviceAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        RequireId(deviceId.Value, nameof(deviceId));
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, BuildUri(SkopkaChatHttpRoutes.DeviceRevocation(deviceId.Value))),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    /// <inheritdoc />
    public async ValueTask<PublicDevice?> GetDeviceAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        RequireId(deviceId.Value, nameof(deviceId));
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUri(SkopkaChatHttpRoutes.Device(deviceId.Value))),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response);
        var result = ToPublicDevice(await ReadJsonAsync(
            response,
            SkopkaChatHttpJsonContext.Default.PublicDeviceResponse,
            SkopkaChatHttpLimits.MaxControlResponseBytes,
            cancellationToken).ConfigureAwait(false));
        return result.DeviceId == deviceId ? result : throw InvalidResponse();
    }

    /// <inheritdoc />
    public async ValueTask<TransportSendStatus> SendAsync(
        EncryptedEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ProtocolValidator.Validate(envelope);
        if (envelope.SenderDeviceId != _authenticatedDeviceId)
        {
            throw new ArgumentException("The envelope sender does not match the authenticated HTTP client.", nameof(envelope));
        }

        var payload = EncryptedEnvelopeDto.FromDomain(envelope);
        using var response = await SendWithRetryAsync(
            () => CreateJsonRequest(
                HttpMethod.Post,
                SkopkaChatHttpRoutes.Envelopes,
                payload,
                SkopkaChatHttpJsonContext.Default.EncryptedEnvelopeDto),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var result = await ReadJsonAsync(
            response,
            SkopkaChatHttpJsonContext.Default.SubmitEnvelopeResponse,
            SkopkaChatHttpLimits.MaxControlResponseBytes,
            cancellationToken).ConfigureAwait(false);
        if (result.MessageId != envelope.MessageId.Value)
        {
            throw InvalidResponse();
        }

        return result.Duplicate ? TransportSendStatus.Duplicate : TransportSendStatus.Accepted;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TransportDelivery>> ReceiveAsync(
        DeviceId recipientDeviceId,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        RequireAuthenticatedDevice(recipientDeviceId);
        if (maximumCount is < 1 or > ProtocolLimits.MaxDeliveryBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri($"{SkopkaChatHttpRoutes.Deliveries}?take={maximumCount}")),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var pending = await ReadJsonAsync(
            response,
            SkopkaChatHttpJsonContext.Default.PendingDeliveryResponseArray,
            SkopkaChatHttpLimits.MaxDeliveryResponseBytes,
            cancellationToken).ConfigureAwait(false);
        if (pending.Length > maximumCount)
        {
            throw InvalidResponse();
        }

        var result = new TransportDelivery[pending.Length];
        for (var index = 0; index < pending.Length; index++)
        {
            var item = pending[index] ?? throw InvalidResponse();
            var envelope = item.Envelope is null ? throw InvalidResponse() : ToEnvelope(item.Envelope);
            if (item.AcceptedAt == default || envelope.RecipientDeviceId != _authenticatedDeviceId)
            {
                throw InvalidResponse();
            }

            result[index] = new TransportDelivery(envelope, item.AcceptedAt);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask AcknowledgeAsync(
        DeviceId recipientDeviceId,
        MessageId messageId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken = default)
    {
        RequireAuthenticatedDevice(recipientDeviceId);
        RequireId(messageId.Value, nameof(messageId));
        if (acknowledgedAt == default)
        {
            throw new ArgumentException("The acknowledgement timestamp is required.", nameof(acknowledgedAt));
        }

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                BuildUri(SkopkaChatHttpRoutes.Acknowledgement(messageId.Value))),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    /// <summary>Uploads an already encrypted attachment blob without sending plaintext metadata.</summary>
    public async ValueTask<AttachmentStoreResult> UploadAttachmentAsync(
        ConversationId conversationId,
        ChatAttachmentContent manifest,
        Stream ciphertext,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        RequireId(conversationId.Value, nameof(conversationId));
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!ciphertext.CanRead)
        {
            throw new ArgumentException("Ciphertext stream must be readable.", nameof(ciphertext));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            BuildUri(SkopkaChatHttpRoutes.Attachment(manifest.AttachmentId.Value)));
        request.Content = new StreamContent(new NonDisposingReadStream(ciphertext));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = manifest.CiphertextLength;
        request.Headers.Add(SkopkaChatAttachmentHeaders.ConversationId, conversationId.ToString());
        request.Headers.Add(
            SkopkaChatAttachmentHeaders.CiphertextSha256,
            Convert.ToHexString(manifest.CiphertextSha256.Span));
        if (expiresAt is { } expiry)
        {
            request.Headers.Add(
                SkopkaChatAttachmentHeaders.ExpiresAt,
                expiry.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return AttachmentStoreResult.Conflict;
        }

        EnsureSuccess(response);
        return response.StatusCode switch
        {
            HttpStatusCode.Created => AttachmentStoreResult.Stored,
            HttpStatusCode.OK => AttachmentStoreResult.Duplicate,
            _ => throw InvalidResponse(),
        };
    }

    ValueTask<AttachmentStoreResult> IEncryptedAttachmentUploader.UploadAsync(
        ConversationId conversationId,
        ChatAttachmentContent manifest,
        Stream ciphertext,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken) =>
        UploadAttachmentAsync(conversationId, manifest, ciphertext, expiresAt, cancellationToken);

    /// <summary>
    /// Downloads ciphertext and streams authenticated plaintext to a caller-owned destination.
    /// The caller must discard the destination if this method fails.
    /// </summary>
    public async ValueTask DownloadAndDecryptAttachmentAsync(
        ConversationId conversationId,
        ChatAttachmentContent manifest,
        Stream plaintextDestination,
        CancellationToken cancellationToken = default)
    {
        RequireId(conversationId.Value, nameof(conversationId));
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plaintextDestination);
        if (!plaintextDestination.CanWrite)
        {
            throw new ArgumentException("Plaintext destination must be writable.", nameof(plaintextDestination));
        }

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri(SkopkaChatHttpRoutes.Attachment(manifest.AttachmentId.Value))),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength != manifest.CiphertextLength ||
            !string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/octet-stream",
                StringComparison.OrdinalIgnoreCase) ||
            !TryGetSingleResponseHeader(response, SkopkaChatAttachmentHeaders.ConversationId, out var conversationValue) ||
            !Guid.TryParseExact(conversationValue, "D", out var returnedConversationId) ||
            returnedConversationId != conversationId.Value ||
            !TryGetSingleResponseHeader(response, SkopkaChatAttachmentHeaders.CiphertextSha256, out var hashValue) ||
            !TryParseHash(hashValue, out var returnedHash) ||
            !CryptographicOperations.FixedTimeEquals(returnedHash, manifest.CiphertextSha256.Span))
        {
            throw InvalidResponse();
        }

        await using var ciphertext = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await ChatAttachmentCryptoService.DecryptAsync(
            manifest,
            ciphertext,
            plaintextDestination,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes an encrypted attachment blob when allowed by the host policy.</summary>
    public async ValueTask<bool> DeleteAttachmentAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        RequireId(attachmentId.Value, nameof(attachmentId));
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(
                HttpMethod.Delete,
                BuildUri(SkopkaChatHttpRoutes.Attachment(attachmentId.Value))),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        EnsureSuccess(response);
        return response.StatusCode == HttpStatusCode.NoContent ? true : throw InvalidResponse();
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        try
        {
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSameOrigin(response);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (HttpRequestException exception)
        {
            throw new ChatHttpTransportException("The chat HTTP request failed.", innerException: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatHttpTransportException("The chat HTTP request timed out.", innerException: exception);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (attempt >= _options.MaxTransientRetries)
            {
                throw new ChatHttpTransportException("The chat HTTP request failed.", innerException: exception);
            }
            catch (HttpRequestException) when (attempt < _options.MaxTransientRetries)
            {
                await DelayBeforeRetryAsync(GetRetryDelay(attempt, null), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested && attempt >= _options.MaxTransientRetries)
            {
                throw new ChatHttpTransportException("The chat HTTP request timed out.", innerException: exception);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested && attempt < _options.MaxTransientRetries)
            {
                await DelayBeforeRetryAsync(GetRetryDelay(attempt, null), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                EnsureSameOrigin(response);
                if (attempt < _options.MaxTransientRetries && IsTransient(response.StatusCode))
                {
                    var retryDelay = GetRetryDelay(attempt, response);
                    response.Dispose();
                    await DelayBeforeRetryAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private async ValueTask<ChatAccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = await _accessTokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            throw new ChatAccessTokenException("The access-token provider returned no token.");
        }

        if (token.ExpiresAt is { } expiresAt &&
            expiresAt <= _timeProvider.GetUtcNow() + _options.TokenExpirySkew)
        {
            throw new ChatAccessTokenException("The access token is expired or too close to expiry.");
        }

        return token;
    }

    private HttpRequestMessage CreateJsonRequest<T>(
        HttpMethod method,
        string route,
        T payload,
        JsonTypeInfo<T> typeInfo) =>
        new(method, BuildUri(route))
        {
            Content = JsonContent.Create(payload, typeInfo)
        };

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null ||
            (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidResponse();
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maximumBytes)
        {
            throw InvalidResponse();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw InvalidResponse();
            }

            buffer.Write(block, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), typeInfo) ??
                throw InvalidResponse();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private async Task DelayBeforeRetryAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan GetRetryDelay(
        int attempt,
        HttpResponseMessage? response)
    {
        var delay = GetRetryAfter(response) ?? TimeSpan.FromTicks(
            Math.Min(
                _options.RetryDelay.Ticks * (1L << attempt),
                _options.MaxRetryDelay.Ticks));
        if (delay > _options.MaxRetryDelay)
        {
            delay = _options.MaxRetryDelay;
        }

        return delay;
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - _timeProvider.GetUtcNow();
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private Uri BuildUri(string route)
    {
        var prefix = _options.RoutePrefix.Trim('/');
        var relative = route.TrimStart('/');
        return new Uri(_baseAddress, $"{prefix}/{relative}");
    }

    private void RequireAuthenticatedDevice(DeviceId deviceId)
    {
        RequireId(deviceId.Value, nameof(deviceId));
        if (deviceId != _authenticatedDeviceId)
        {
            throw new ArgumentException("The addressed device does not match the authenticated HTTP client.", nameof(deviceId));
        }
    }

    private static Uri NormalizeBaseAddress(Uri? address, bool requireHttps)
    {
        if (address is null || !address.IsAbsoluteUri ||
            (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            (requireHttps && address.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The chat HTTP base address is invalid or insecure.", nameof(address));
        }

        var builder = new UriBuilder(address);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private void EnsureSameOrigin(HttpResponseMessage response)
    {
        var finalAddress = response.RequestMessage?.RequestUri;
        if (finalAddress is null ||
            !finalAddress.Scheme.Equals(_baseAddress.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !finalAddress.Host.Equals(_baseAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            finalAddress.Port != _baseAddress.Port)
        {
            throw new ChatHttpTransportException("The chat HTTP response changed origin.");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new ChatHttpTransportException("The chat HTTP operation was rejected.", response.StatusCode);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static bool HasExactParticipants(PersonalConversationResponse conversation, Guid first, Guid second) =>
        first != second &&
        ((conversation.FirstUserId == first && conversation.SecondUserId == second) ||
         (conversation.FirstUserId == second && conversation.SecondUserId == first));

    private static PublicDevice ToPublicDevice(PublicDeviceResponse response)
    {
        try
        {
            return response.ToDomain();
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    private static EncryptedEnvelope ToEnvelope(EncryptedEnvelopeDto response)
    {
        try
        {
            return response.ToDomain();
        }
        catch (ArgumentException)
        {
            throw InvalidResponse();
        }
    }

    private static void RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The chat identifier must not be empty.", parameterName);
        }
    }

    private static ChatHttpTransportException InvalidResponse() =>
        new("The chat HTTP response was invalid.");

    private static bool TryGetSingleResponseHeader(
        HttpResponseMessage response,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return false;
        }

        var items = values.Take(2).ToArray();
        if (items.Length != 1 || items[0].Length == 0)
        {
            return false;
        }

        value = items[0];
        return true;
    }

    private static bool TryParseHash(string value, out byte[] hash)
    {
        hash = [];
        if (value.Length != AttachmentStorageLimits.Sha256Bytes * 2)
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(value);
            return hash.Length == AttachmentStorageLimits.Sha256Bytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class NonDisposingReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
