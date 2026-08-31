using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Skopka.Chat.Protocol;
using Skopka.Chat.Server;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Server.AspNetCore;

/// <summary>Maps the authenticated Minimal API surface over <see cref="ChatServerEngine"/>.</summary>
public static class SkopkaChatEndpointRouteBuilderExtensions
{
    /// <summary>Maps the versioned chat API. Every endpoint requires authorization.</summary>
    public static RouteGroupBuilder MapSkopkaChatApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = SkopkaChatHttpRoutes.DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<SkopkaChatHttpOptions>>()
            .Value;
        var group = endpoints.MapGroup(prefix)
            .WithTags("Skopka.Chat")
            .WithMetadata(new RequestSizeLimitAttribute(SkopkaChatHttpLimits.MaxRequestBodyBytes));
        if (string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            group.RequireAuthorization();
        }
        else
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapPost(SkopkaChatHttpRoutes.Devices, RegisterDeviceAsync);
        group.MapGet($"{SkopkaChatHttpRoutes.Devices}/{{deviceId:guid}}", GetDeviceAsync);
        group.MapPost($"{SkopkaChatHttpRoutes.Devices}/{{deviceId:guid}}/revocation", RevokeDeviceAsync);
        group.MapPost(SkopkaChatHttpRoutes.Conversations, CreateConversationAsync);
        group.MapPost(SkopkaChatHttpRoutes.Envelopes, SubmitEnvelopeAsync);
        group.MapGet(SkopkaChatHttpRoutes.Deliveries, GetDeliveriesAsync);
        group.MapPost($"{SkopkaChatHttpRoutes.Deliveries}/{{messageId:guid}}/acknowledgements", AcknowledgeAsync);
        return group;
    }

    private static async Task<IResult> RegisterDeviceAsync(
        RegisterDeviceRequest request,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!principalMapper.TryMap(context.User, out var identity) ||
            identity.DeviceId.Value != request.DeviceId)
        {
            return Results.Forbid();
        }

        var encryptionPublicKey = request.EncryptionPublicKey ?? [];
        var signingPublicKey = request.SigningPublicKey ?? [];
        var existing = await devices.GetAsync(identity.DeviceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return SameRegistration(existing, identity.UserId, request.KeyId, encryptionPublicKey, signingPublicKey)
                ? Results.Ok(PublicDeviceResponse.FromDomain(existing))
                : ConflictProblem();
        }

        var device = new PublicDevice(
            identity.UserId,
            identity.DeviceId,
            new KeyId(request.KeyId),
            encryptionPublicKey,
            signingPublicKey,
            timeProvider.GetUtcNow());

        try
        {
            await engine.RegisterDeviceAsync(device, cancellationToken).ConfigureAwait(false);
            return Results.Json(PublicDeviceResponse.FromDomain(device), statusCode: StatusCodes.Status201Created);
        }
        catch (ChatServerException)
        {
            existing = await devices.GetAsync(identity.DeviceId, cancellationToken).ConfigureAwait(false);
            return existing is not null &&
                SameRegistration(existing, identity.UserId, request.KeyId, encryptionPublicKey, signingPublicKey)
                ? Results.Ok(PublicDeviceResponse.FromDomain(existing))
                : ConflictProblem();
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
    }

    private static async Task<IResult> GetDeviceAsync(
        Guid deviceId,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        var device = await devices.GetAsync(new DeviceId(deviceId), cancellationToken).ConfigureAwait(false);
        return device is null
            ? Results.NotFound()
            : Results.Ok(PublicDeviceResponse.FromDomain(device));
    }

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!principalMapper.TryMap(context.User, out var identity))
        {
            return Results.Forbid();
        }

        var target = await devices.GetAsync(new DeviceId(deviceId), cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return Results.NotFound();
        }

        if (target.UserId != identity.UserId)
        {
            return Results.Forbid();
        }

        var caller = await devices.GetAsync(identity.DeviceId, cancellationToken).ConfigureAwait(false);
        if (caller is null || caller.UserId != identity.UserId)
        {
            return Results.Forbid();
        }

        if (target.IsRevoked)
        {
            return Results.NoContent();
        }

        if (caller.IsRevoked)
        {
            return Results.Forbid();
        }

        try
        {
            await engine.RevokeDeviceAsync(target.DeviceId, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
    }

    private static async Task<IResult> CreateConversationAsync(
        CreateConversationRequest request,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        IConversationRepository conversations,
        ChatServerEngine engine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        var identity = ownership.Value.Identity;
        var conversationId = new ConversationId(request.ConversationId);
        var peerUserId = new UserId(request.PeerUserId);
        var existing = await conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return HasExactParticipants(existing, identity.UserId, peerUserId)
                ? Results.Ok(ToResponse(existing))
                : ConflictProblem();
        }

        try
        {
            var conversation = await engine.CreateConversationAsync(
                identity.UserId,
                peerUserId,
                conversationId,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return Results.Json(
                ToResponse(conversation),
                statusCode: StatusCodes.Status201Created);
        }
        catch (ChatServerException)
        {
            existing = await conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
            return existing is not null && HasExactParticipants(existing, identity.UserId, peerUserId)
                ? Results.Ok(ToResponse(existing))
                : ConflictProblem();
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
    }

    private static async Task<IResult> SubmitEnvelopeAsync(
        EncryptedEnvelopeDto request,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null || ownership.Value.Identity.DeviceId.Value != request.SenderDeviceId)
        {
            return Results.Forbid();
        }

        try
        {
            var envelope = request.ToDomain();
            var result = await engine.SubmitAsync(
                envelope,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            var response = new SubmitEnvelopeResponse(
                envelope.MessageId.Value,
                result == SubmitEnvelopeResult.Duplicate);
            return Results.Json(
                response,
                statusCode: result == SubmitEnvelopeResult.Accepted
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status200OK);
        }
        catch (ProtocolValidationException)
        {
            return InvalidRequestProblem();
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
        catch (ChatServerException)
        {
            return ConflictProblem();
        }
    }

    private static async Task<IResult> GetDeliveriesAsync(
        int? take,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        var maximumCount = take ?? 50;
        try
        {
            var pending = await engine.ReceiveAsync(
                ownership.Value.Identity.DeviceId,
                maximumCount,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(pending.Select(stored => new PendingDeliveryResponse(
                EncryptedEnvelopeDto.FromDomain(stored.Envelope),
                stored.AcceptedAt)).ToArray());
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
        catch (ChatServerException)
        {
            return ConflictProblem();
        }
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid messageId,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        try
        {
            await engine.AcknowledgeAsync(
                ownership.Value.Identity.DeviceId,
                new MessageId(messageId),
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
        catch (ChatServerException)
        {
            return ConflictProblem();
        }
    }

    private static async ValueTask<OwnedDevice?> RequireActiveOwnedDeviceAsync(
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        CancellationToken cancellationToken)
    {
        if (!principalMapper.TryMap(context.User, out var identity))
        {
            return null;
        }

        var device = await devices.GetAsync(identity.DeviceId, cancellationToken).ConfigureAwait(false);
        return device is not null && device.UserId == identity.UserId && !device.IsRevoked
            ? new OwnedDevice(identity, device)
            : null;
    }

    private static bool SameRegistration(
        PublicDevice existing,
        UserId userId,
        Guid keyId,
        ReadOnlySpan<byte> encryptionPublicKey,
        ReadOnlySpan<byte> signingPublicKey) =>
        existing.UserId == userId &&
        existing.KeyId.Value == keyId &&
        existing.EncryptionPublicKey.Span.SequenceEqual(encryptionPublicKey) &&
        existing.SigningPublicKey.Span.SequenceEqual(signingPublicKey);

    private static bool HasExactParticipants(
        PersonalConversation conversation,
        UserId first,
        UserId second) =>
        first != second && conversation.Contains(first) && conversation.Contains(second);

    private static PersonalConversationResponse ToResponse(PersonalConversation conversation) => new(
        conversation.ConversationId.Value,
        conversation.FirstUserId.Value,
        conversation.SecondUserId.Value,
        conversation.CreatedAt);

    private static IResult InvalidRequestProblem() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid chat request.");

    private static IResult ConflictProblem() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Chat operation rejected.");

    private readonly record struct OwnedDevice(ChatRequestIdentity Identity, PublicDevice Device);
}
