using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Http.Tests;

public sealed class HttpClientTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private const string HostileMarker = "secret-hostile-response-marker";

    [Fact]
    public async Task Transient_response_is_retried_with_a_fresh_request_and_token()
    {
        var userId = Guid.NewGuid();
        var ownDeviceId = Guid.NewGuid();
        var requestedDeviceId = Guid.NewGuid();
        var attempts = 0;
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("fresh-token", Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            attempts++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fresh-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(
                $"/base/skopka-chat/v1/devices/{requestedDeviceId:D}",
                request.RequestUri?.AbsolutePath);
            if (attempts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var dto = PublicDevice(requestedDeviceId);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(dto, SkopkaChatHttpJsonContext.Default.PublicDeviceResponse)
            });
        }))
        {
            BaseAddress = new Uri("https://chat.example.test/base")
        };
        var client = CreateClient(httpClient, tokenProvider, userId, ownDeviceId, options =>
        {
            options.MaxTransientRetries = 1;
            options.RetryDelay = TimeSpan.Zero;
            options.MaxRetryDelay = TimeSpan.Zero;
        });

        var result = await client.GetDeviceAsync(new DeviceId(requestedDeviceId));

        Assert.NotNull(result);
        Assert.Equal(new DeviceId(requestedDeviceId), result.DeviceId);
        Assert.Equal(2, attempts);
        Assert.Equal(2, tokenProvider.CallCount);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Expiring_token_is_rejected_before_network_io()
    {
        var attempts = 0;
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("old-token", Now.AddSeconds(10)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<ChatAccessTokenException>(async () =>
            await client.GetDeviceAsync(DeviceId.New()));

        Assert.Equal(0, attempts);
        Assert.Equal(1, tokenProvider.CallCount);
    }

    [Fact]
    public async Task Recipient_parameter_must_match_the_authenticated_device()
    {
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("unused-token", Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Network must not be reached.")))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.ReceiveAsync(DeviceId.New(), 10));

        Assert.Equal(0, tokenProvider.CallCount);
    }

    [Fact]
    public async Task Http_error_does_not_echo_token_or_response_body()
    {
        const string token = "secret-access-token";
        const string body = "secret-server-body";
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken(token, Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(body)
            })))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<ChatHttpTransportException>(async () =>
            await client.GetDeviceAsync(DeviceId.New()));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(body, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, tokenProvider.CallCount);
    }

    [Fact]
    public async Task Oversized_success_response_is_rejected_before_deserialization()
    {
        var content = new ByteArrayContent(new byte[SkopkaChatHttpLimits.MaxControlResponseBytes + 1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("token", Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<ChatHttpTransportException>(async () =>
            await client.GetDeviceAsync(DeviceId.New()));
    }

    [Fact]
    public async Task Structurally_invalid_success_response_is_a_bounded_transport_failure()
    {
        var requestedDeviceId = Guid.NewGuid();
        var invalid = PublicDevice(requestedDeviceId) with { EncryptionPublicKey = [0x01] };
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("token", Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(invalid, SkopkaChatHttpJsonContext.Default.PublicDeviceResponse)
            })))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<ChatHttpTransportException>(async () =>
            await client.GetDeviceAsync(new DeviceId(requestedDeviceId)));

        Assert.Contains("response was invalid", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HostileResponseCases))]
    public async Task Hostile_success_response_is_rejected_without_reflecting_remote_data(string caseName)
    {
        const string token = "secret-hostile-response-token";
        var requestedDeviceId = Guid.NewGuid();
        var body = HostilePublicDeviceResponse(caseName, requestedDeviceId);
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken(token, Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            })))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<ChatHttpTransportException>(async () =>
            await client.GetDeviceAsync(new DeviceId(requestedDeviceId)));

        Assert.Equal("The chat HTTP response was invalid.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(HostileMarker, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_response_with_non_json_content_type_is_rejected()
    {
        var requestedDeviceId = Guid.NewGuid();
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("token", Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ValidPublicDeviceJson(requestedDeviceId),
                    Encoding.UTF8,
                    "text/plain")
            })))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<ChatHttpTransportException>(async () =>
            await client.GetDeviceAsync(new DeviceId(requestedDeviceId)));
    }

    [Theory]
    [MemberData(nameof(HostileDeliveryCases))]
    public async Task Hostile_delivery_fields_are_rejected_without_reflecting_remote_data(string caseName)
    {
        const string token = "secret-hostile-delivery-token";
        var authenticatedDeviceId = Guid.NewGuid();
        var body = HostileDeliveryResponse(caseName, authenticatedDeviceId);
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken(token, Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            })))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), authenticatedDeviceId);

        var exception = await Assert.ThrowsAsync<ChatHttpTransportException>(async () =>
            await client.ReceiveAsync(new DeviceId(authenticatedDeviceId), 10));

        Assert.Equal("The chat HTTP response was invalid.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(HostileMarker, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_json_context_uses_the_strict_bounded_profile()
    {
        var options = SkopkaChatHttpJsonContext.Default.Options;

        Assert.False(options.AllowDuplicateProperties);
        Assert.False(options.AllowTrailingCommas);
        Assert.Equal(SkopkaChatHttpJson.MaximumDepth, options.MaxDepth);
        Assert.False(options.PropertyNameCaseInsensitive);
        Assert.True(options.RespectNullableAnnotations);
        Assert.True(options.RespectRequiredConstructorParameters);
        Assert.Equal(JsonUnmappedMemberHandling.Disallow, options.UnmappedMemberHandling);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("token", Now.AddHours(1)));
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
        {
            attempts++;
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        }))
        {
            BaseAddress = new Uri("https://chat.example.test/")
        };
        var client = CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid(), options =>
        {
            options.MaxTransientRetries = 3;
            options.RetryDelay = TimeSpan.Zero;
            options.MaxRetryDelay = TimeSpan.Zero;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetDeviceAsync(DeviceId.New(), cancellation.Token));

        Assert.Equal(1, attempts);
        Assert.Equal(1, tokenProvider.CallCount);
    }

    [Fact]
    public void Tokens_and_package_boundaries_do_not_expose_secrets_or_server_dependencies()
    {
        var token = new ChatAccessToken("never-print-this", Now.AddHours(1));
        var clientReferences = typeof(SkopkaChatHttpClient).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        var contractReferences = typeof(SkopkaChatHttpRoutes).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.Equal("[REDACTED ACCESS TOKEN]", token.ToString());
        Assert.DoesNotContain("Skopka.Chat.Server", clientReferences);
        Assert.DoesNotContain("Skopka.Chat.Server.AspNetCore", clientReferences);
        Assert.DoesNotContain("Skopka.Chat.Client", contractReferences);
        Assert.DoesNotContain("Skopka.Chat.Server", contractReferences);
    }

    [Fact]
    public void Plain_http_base_address_is_rejected_by_default()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://chat.example.test/") };
        var tokenProvider = new CountingTokenProvider(new ChatAccessToken("token", Now.AddHours(1)));

        Assert.Throws<ArgumentException>(() =>
            CreateClient(httpClient, tokenProvider, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Invalid_bearer_characters_are_rejected_without_echoing_the_value()
    {
        const string invalid = "do-not echo:this";

        var exception = Assert.Throws<ArgumentException>(() => new ChatAccessToken(invalid));

        Assert.DoesNotContain(invalid, exception.ToString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> HostileResponseCases()
    {
        yield return ["empty"];
        yield return ["truncated"];
        yield return ["duplicate-property"];
        yield return ["unknown-property"];
        yield return ["case-mismatched-property"];
        yield return ["missing-required-property"];
        yield return ["null-non-nullable-property"];
        yield return ["invalid-base64"];
        yield return ["trailing-root-value"];
        yield return ["comment"];
        yield return ["trailing-comma"];
        yield return ["excessive-depth"];
    }

    public static IEnumerable<object[]> HostileDeliveryCases()
    {
        yield return ["invalid-guid"];
        yield return ["empty-message-id"];
        yield return ["empty-signing-key-id"];
        yield return ["short-ephemeral-key"];
        yield return ["short-nonce"];
        yield return ["oversized-ciphertext"];
        yield return ["short-authentication-tag"];
        yield return ["short-signature"];
        yield return ["invalid-base64"];
        yield return ["string-number"];
        yield return ["recipient-mismatch"];
        yield return ["missing-acceptance-time"];
    }

    private static SkopkaChatHttpClient CreateClient(
        HttpClient httpClient,
        IAccessTokenProvider tokenProvider,
        Guid userId,
        Guid deviceId,
        Action<SkopkaChatHttpClientOptions>? configure = null)
    {
        var options = new SkopkaChatHttpClientOptions
        {
            AuthenticatedUserId = userId,
            AuthenticatedDeviceId = deviceId,
            MaxTransientRetries = 0
        };
        configure?.Invoke(options);
        return new SkopkaChatHttpClient(
            httpClient,
            tokenProvider,
            Options.Create(options),
            new FixedTimeProvider(Now));
    }

    private static PublicDeviceResponse PublicDevice(Guid deviceId) => new(
        Guid.NewGuid(),
        deviceId,
        Guid.NewGuid(),
        Enumerable.Repeat((byte)0x11, ProtocolLimits.X25519PublicKeyBytes).ToArray(),
        Enumerable.Repeat((byte)0x22, ProtocolLimits.Ed25519PublicKeyBytes).ToArray(),
        Now,
        null);

    private static string HostilePublicDeviceResponse(string caseName, Guid requestedDeviceId)
    {
        var valid = ValidPublicDeviceJson(requestedDeviceId);
        var keyIdProperty = "\"keyId\":\"30000000-0000-0000-0000-000000000003\",";
        var encryptionKey = Convert.ToBase64String(
            Enumerable.Repeat((byte)0x11, ProtocolLimits.X25519PublicKeyBytes).ToArray());
        var encryptionProperty = $"\"encryptionPublicKey\":\"{encryptionKey}\"";
        var nestedValue = new string('[', SkopkaChatHttpJson.MaximumDepth + 1) +
            "\"AA==\"" +
            new string(']', SkopkaChatHttpJson.MaximumDepth + 1);

        return caseName switch
        {
            "empty" => string.Empty,
            "truncated" => valid[..^1],
            "duplicate-property" => valid.Replace(
                $"\"deviceId\":\"{requestedDeviceId:D}\"",
                $"\"deviceId\":\"{requestedDeviceId:D}\",\"deviceId\":\"{Guid.NewGuid():D}\"",
                StringComparison.Ordinal),
            "unknown-property" => valid[..^1] + $",\"{HostileMarker}\":\"{HostileMarker}\"}}",
            "case-mismatched-property" => valid.Replace("\"deviceId\"", "\"DeviceId\"", StringComparison.Ordinal),
            "missing-required-property" => valid.Replace(keyIdProperty, string.Empty, StringComparison.Ordinal),
            "null-non-nullable-property" => valid.Replace($"\"{encryptionKey}\"", "null", StringComparison.Ordinal),
            "invalid-base64" => valid.Replace(encryptionKey, "***not-base64***", StringComparison.Ordinal),
            "trailing-root-value" => valid + "{}",
            "comment" => "/* ambiguous */" + valid,
            "trailing-comma" => valid[..^1] + ",}",
            "excessive-depth" => valid.Replace(
                encryptionProperty,
                $"\"encryptionPublicKey\":{nestedValue}",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown hostile response case.")
        };
    }

    private static string ValidPublicDeviceJson(Guid deviceId)
    {
        var encryptionKey = Convert.ToBase64String(
            Enumerable.Repeat((byte)0x11, ProtocolLimits.X25519PublicKeyBytes).ToArray());
        var signingKey = Convert.ToBase64String(
            Enumerable.Repeat((byte)0x22, ProtocolLimits.Ed25519PublicKeyBytes).ToArray());
        return $$"""{"userId":"40000000-0000-0000-0000-000000000004","deviceId":"{{deviceId:D}}","keyId":"30000000-0000-0000-0000-000000000003","encryptionPublicKey":"{{encryptionKey}}","signingPublicKey":"{{signingKey}}","registeredAt":"2026-09-01T09:00:00+00:00","revokedAt":null}""";
    }

    private static string HostileDeliveryResponse(string caseName, Guid authenticatedDeviceId)
    {
        var valid = DeliveryEnvelope(authenticatedDeviceId);
        var envelope = caseName switch
        {
            "empty-message-id" => valid with { MessageId = Guid.Empty },
            "empty-signing-key-id" => valid with { SenderSigningKeyId = Guid.Empty },
            "short-ephemeral-key" => valid with { EphemeralPublicKey = [0x01] },
            "short-nonce" => valid with { Nonce = [0x01] },
            "oversized-ciphertext" => valid with
            {
                Ciphertext = new byte[ProtocolLimits.MaxCiphertextBytes + 1]
            },
            "short-authentication-tag" => valid with { AuthenticationTag = [0x01] },
            "short-signature" => valid with { Signature = [0x01] },
            "recipient-mismatch" => valid with { RecipientDeviceId = Guid.NewGuid() },
            _ => valid
        };
        var acceptedAt = caseName == "missing-acceptance-time" ? default : Now;
        var serialized = JsonSerializer.Serialize(
            new[] { new PendingDeliveryResponse(envelope, acceptedAt) },
            SkopkaChatHttpJsonContext.Default.PendingDeliveryResponseArray);

        return caseName switch
        {
            "invalid-guid" => serialized.Replace(valid.MessageId.ToString("D"), HostileMarker, StringComparison.Ordinal),
            "invalid-base64" => serialized.Replace(
                Convert.ToBase64String(valid.Ciphertext),
                "***not-base64***",
                StringComparison.Ordinal),
            "string-number" => serialized.Replace(
                $"\"protocolVersion\":{ProtocolVersions.Current}",
                $"\"protocolVersion\":\"{ProtocolVersions.Current}\"",
                StringComparison.Ordinal),
            _ => serialized
        };
    }

    private static EncryptedEnvelopeDto DeliveryEnvelope(Guid recipientDeviceId) => new(
        ProtocolVersions.Current,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        recipientDeviceId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Now,
        Now.AddDays(1),
        Enumerable.Repeat((byte)0x33, ProtocolLimits.X25519PublicKeyBytes).ToArray(),
        Enumerable.Repeat((byte)0x44, ProtocolLimits.NonceBytes).ToArray(),
        [0x45, 0x32, 0x45, 0x45],
        Enumerable.Repeat((byte)0x55, ProtocolLimits.AuthenticationTagBytes).ToArray(),
        Enumerable.Repeat((byte)0x66, ProtocolLimits.SignatureBytes).ToArray());

    private sealed class CountingTokenProvider(ChatAccessToken token) : IAccessTokenProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<ChatAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(token);
        }
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
