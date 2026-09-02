using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Server.AspNetCore;

internal static class DeviceBindingEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, string prefix)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<SkopkaChatDeviceBindingOptions>>().Value;
        var group = endpoints.MapGroup(prefix).RequireAuthorization(DeviceBindingPolicies.Account)
            .WithMetadata(new RequestSizeLimitAttribute(DeviceBindingHttpRoutes.MaximumBodyBytes));
        if (!string.IsNullOrWhiteSpace(options.AccountAuthorizationPolicy)) { group.RequireAuthorization(options.AccountAuthorizationPolicy); }
        // A host may apply its account policy separately; the legacy chat policy can require a device claim
        // and must not accidentally create a bootstrap dependency cycle.
        group.MapPost(DeviceBindingHttpRoutes.Challenges, IssueAsync).RequireRateLimiting(options.ChallengeRateLimitPolicy);
        group.MapPost(DeviceBindingHttpRoutes.Completions, CompleteAsync).RequireRateLimiting(options.ProofRateLimitPolicy);
    }

    private static async Task<IResult> IssueAsync(HttpContext http, DeviceBindingRequestResolver resolver,
        DeviceBindingService service, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        try
        {
            var auth = await resolver.AccountAsync(http, cancellationToken).ConfigureAwait(false);
            if (auth is null) { return Results.Forbid(); }
            var request = await ReadAsync(http, SkopkaChatHttpJsonContext.Default.DeviceBindingIssueRequest, cancellationToken).ConfigureAwait(false);
            if (request.Device is null) { throw new ArgumentException("Invalid bootstrap request."); }
            var device = new PublicDevice(auth.UserId, new DeviceId(request.Device.DeviceId), new KeyId(request.Device.KeyId),
                request.Device.EncryptionPublicKey ?? [], request.Device.SigningPublicKey ?? [], timeProvider.GetUtcNow());
            var challenge = await service.IssueAsync(auth, (DeviceBindingOperation)request.Operation, device, cancellationToken).ConfigureAwait(false);
            return Results.Json(new DeviceBindingChallengeResponse(DeviceBindingEncoding.Encode(challenge)), SkopkaChatHttpJsonContext.Default.DeviceBindingChallengeResponse);
        }
        catch (OversizedBodyException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (Exception exception) when (exception is ArgumentException or JsonException) { return InvalidRequest(); }
        catch (DeviceBindingException exception) { return Rejected(exception); }
    }

    private static async Task<IResult> CompleteAsync(HttpContext http, DeviceBindingRequestResolver resolver,
        DeviceBindingService service, CancellationToken cancellationToken)
    {
        try
        {
            var auth = await resolver.AccountAsync(http, cancellationToken).ConfigureAwait(false);
            if (auth is null) { return Results.Forbid(); }
            var request = await ReadAsync(http, SkopkaChatHttpJsonContext.Default.DeviceBindingCompleteRequest, cancellationToken).ConfigureAwait(false);
            var result = await service.CompleteAsync(auth, request.ToDomain(), cancellationToken).ConfigureAwait(false);
            return Results.Json(DeviceBindingResultResponse.FromDomain(result), SkopkaChatHttpJsonContext.Default.DeviceBindingResultResponse);
        }
        catch (OversizedBodyException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (Exception exception) when (exception is ArgumentException or JsonException) { return InvalidRequest(); }
        catch (DeviceBindingException exception) { return Rejected(exception); }
    }

    private static async ValueTask<T> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType()) { throw new ArgumentException("Invalid bootstrap content type."); }
        if (context.Request.ContentLength > DeviceBindingHttpRoutes.MaximumBodyBytes) { throw new OversizedBodyException(); }
        var bytes = new byte[DeviceBindingHttpRoutes.MaximumBodyBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            length += read;
        }
        if (length > DeviceBindingHttpRoutes.MaximumBodyBytes) { throw new OversizedBodyException(); }
        return JsonSerializer.Deserialize(bytes.AsSpan(0, length), typeInfo) ?? throw new ArgumentException("Invalid bootstrap request.");
    }
    private static IResult InvalidRequest() => Results.Problem(statusCode: 400, title: "Invalid device binding request.");
    private static IResult Rejected(DeviceBindingException exception) => Results.Problem(
        statusCode: exception.Failure == DeviceBindingFailure.Revoked ? 410 : 403, title: "Device binding was rejected.");
    private sealed class OversizedBodyException : Exception;
}
