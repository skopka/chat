using System.Globalization;
using System.Buffers.Binary;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Skopka.Chat.Attachments;
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

        var boundMode = endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>()
            .IsService(typeof(DeviceBindingRequestResolver));
        if (boundMode)
        {
            group.RequireAuthorization(DeviceBindingPolicies.Device);
            DeviceBindingEndpoints.Map(endpoints, prefix);
        }
        else
        {
            group.MapPost(SkopkaChatHttpRoutes.Devices, RegisterDeviceAsync);
        }
        group.MapGet($"{SkopkaChatHttpRoutes.Devices}/{{deviceId:guid}}", GetDeviceAsync);
        group.MapPost($"{SkopkaChatHttpRoutes.Devices}/{{deviceId:guid}}/revocation", RevokeDeviceAsync);
        group.MapPost(SkopkaChatHttpRoutes.Conversations, CreateConversationAsync);
        group.MapPost(SkopkaChatHttpRoutes.PersonalConversation, GetOrCreateConversationAsync);
        group.MapGet(SkopkaChatHttpRoutes.Conversations, ListConversationsAsync);
        var serviceProbe = endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>();
        if (serviceProbe.IsService(typeof(IGroupConversationRepository)))
        {
            group.MapPost(SkopkaChatHttpRoutes.GroupConversations, CreateGroupConversationAsync);
            group.MapGet(SkopkaChatHttpRoutes.GroupConversations, ListGroupConversationsAsync);
            group.MapGet($"{SkopkaChatHttpRoutes.GroupConversations}/{{conversationId:guid}}", GetGroupConversationAsync);
            group.MapPut($"{SkopkaChatHttpRoutes.GroupConversations}/{{conversationId:guid}}", RenameGroupConversationAsync);
            group.MapPost($"{SkopkaChatHttpRoutes.GroupConversations}/{{conversationId:guid}}/members", AddGroupMemberAsync);
            group.MapDelete($"{SkopkaChatHttpRoutes.GroupConversations}/{{conversationId:guid}}/members/{{userId:guid}}", RemoveGroupMemberAsync);
            group.MapPut($"{SkopkaChatHttpRoutes.GroupConversations}/{{conversationId:guid}}/members/{{userId:guid}}/role", ChangeGroupMemberRoleAsync);
        }
        group.MapGet($"{SkopkaChatHttpRoutes.Conversations}/{{conversationId:guid}}/devices", ListConversationDevicesAsync);
        group.MapPost(SkopkaChatHttpRoutes.Envelopes, SubmitEnvelopeAsync);
        group.MapGet(SkopkaChatHttpRoutes.Deliveries, GetDeliveriesAsync);
        group.MapPost($"{SkopkaChatHttpRoutes.Deliveries}/{{messageId:guid}}/acknowledgements", AcknowledgeAsync);
        if (serviceProbe.IsService(typeof(AttachmentStorageService)))
        {
            group.MapPut($"{SkopkaChatHttpRoutes.Attachments}/{{attachmentId:guid}}", UploadAttachmentAsync)
                .WithMetadata(new RequestSizeLimitAttribute(AttachmentStorageLimits.MaxCiphertextBytes));
            group.MapGet($"{SkopkaChatHttpRoutes.Attachments}/{{attachmentId:guid}}", DownloadAttachmentAsync);
            group.MapDelete($"{SkopkaChatHttpRoutes.Attachments}/{{attachmentId:guid}}", DeleteAttachmentAsync);
        }

        return group;
    }

    private static async Task<IResult> UploadAttachmentAsync(
        Guid attachmentId,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        AttachmentStorageService attachments,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            context.Request.ContentLength is not { } contentLength ||
            !TryGetSingleHeader(context, SkopkaChatAttachmentHeaders.ConversationId, out var conversationValue) ||
            !Guid.TryParseExact(conversationValue, "D", out var conversationId) ||
            !TryGetSingleHeader(context, SkopkaChatAttachmentHeaders.CiphertextSha256, out var hashValue) ||
            hashValue.Length != AttachmentStorageLimits.Sha256Bytes * 2)
        {
            return InvalidRequestProblem();
        }

        byte[] ciphertextSha256;
        try
        {
            ciphertextSha256 = Convert.FromHexString(hashValue);
        }
        catch (FormatException)
        {
            return InvalidRequestProblem();
        }

        DateTimeOffset? expiresAt = null;
        if (!TryGetOptionalSingleHeader(context, SkopkaChatAttachmentHeaders.ExpiresAt, out var expiryValue))
        {
            return InvalidRequestProblem();
        }

        if (expiryValue is not null)
        {
            if (!DateTimeOffset.TryParseExact(
                    expiryValue,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedExpiry))
            {
                return InvalidRequestProblem();
            }

            expiresAt = parsedExpiry;
        }

        try
        {
            var request = new AttachmentUploadRequest(
                new AttachmentId(attachmentId),
                new ConversationId(conversationId),
                contentLength,
                ciphertextSha256,
                expiresAt);
            var result = await attachments.UploadAsync(
                ownership.Value.Identity.UserId,
                request,
                context.Request.Body,
                cancellationToken).ConfigureAwait(false);
            return result switch
            {
                AttachmentStoreResult.Stored => Results.StatusCode(StatusCodes.Status201Created),
                AttachmentStoreResult.Duplicate => Results.Ok(),
                AttachmentStoreResult.Conflict => ConflictProblem(),
                _ => throw new InvalidOperationException("Unknown attachment storage outcome."),
            };
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
        catch (InvalidDataException)
        {
            return InvalidRequestProblem();
        }
        catch (AttachmentServiceException)
        {
            return Results.Forbid();
        }
    }

    private static async Task<IResult> DownloadAttachmentAsync(
        Guid attachmentId,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        AttachmentStorageService attachments,
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
            var id = new AttachmentId(attachmentId);
            var metadata = await attachments.GetDownloadMetadataAsync(
                ownership.Value.Identity.UserId,
                id,
                cancellationToken).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = metadata.CiphertextLength;
            context.Response.Headers[SkopkaChatAttachmentHeaders.ConversationId] = metadata.ConversationId.ToString();
            context.Response.Headers[SkopkaChatAttachmentHeaders.CiphertextSha256] =
                Convert.ToHexString(metadata.CiphertextSha256.Span);
            if (metadata.ExpiresAt is { } expiresAt)
            {
                context.Response.Headers[SkopkaChatAttachmentHeaders.ExpiresAt] =
                    expiresAt.ToString("O", CultureInfo.InvariantCulture);
            }

            await attachments.DownloadAsync(
                ownership.Value.Identity.UserId,
                id,
                context.Response.Body,
                cancellationToken).ConfigureAwait(false);
            return Results.Empty;
        }
        catch (ArgumentException)
        {
            return Results.NotFound();
        }
        catch (AttachmentServiceException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> DeleteAttachmentAsync(
        Guid attachmentId,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        AttachmentStorageService attachments,
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
            return await attachments.DeleteAsync(
                ownership.Value.Identity.UserId,
                new AttachmentId(attachmentId),
                cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (ArgumentException)
        {
            return Results.NotFound();
        }
        catch (AttachmentServiceException)
        {
            return Results.Forbid();
        }
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
        if (await ResolveIdentityAsync(context, principalMapper, cancellationToken).ConfigureAwait(false) is not { } identity ||
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
        if (await ResolveIdentityAsync(context, principalMapper, cancellationToken).ConfigureAwait(false) is not { } identity)
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

    private static async Task<IResult> GetOrCreateConversationAsync(
        GetOrCreateConversationRequest request,
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
            var conversation = await engine.GetOrCreateConversationAsync(
                ownership.Value.Identity.UserId,
                new UserId(request.PeerUserId),
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToResponse(conversation));
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

    private static async Task<IResult> ListConversationsAsync(
        string? cursor,
        int? take,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        if (!TryDecodeConversationCursor(cursor, out var decodedCursor))
        {
            return InvalidRequestProblem();
        }

        try
        {
            var page = await engine.ListConversationsAsync(
                ownership.Value.Identity.UserId,
                decodedCursor,
                take ?? 50,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new ConversationDirectoryResponse(
                page.Items.Select(ToResponse).ToArray(),
                page.NextCursor is { } next ? EncodeConversationCursor(next) : null));
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
    }

    private static async Task<IResult> CreateGroupConversationAsync(
        CreateGroupConversationRequest request,
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
            var conversation = await engine.CreateGroupConversationAsync(
                ownership.Value.Identity.UserId,
                new ConversationId(request.ConversationId),
                request.Title,
                (request.MemberUserIds ?? []).Select(static userId => new UserId(userId)).ToArray(),
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return Results.Created(SkopkaChatHttpRoutes.GroupConversation(conversation.ConversationId.Value), ToResponse(conversation));
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

    private static async Task<IResult> GetGroupConversationAsync(
        Guid conversationId,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
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
            var conversation = await engine.GetGroupConversationAsync(
                ownership.Value.Identity.UserId,
                new ConversationId(conversationId),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToResponse(conversation));
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
        catch (ChatServerException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ListGroupConversationsAsync(
        string? cursor,
        int? take,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        if (!TryDecodeConversationCursor(cursor, out var decodedCursor))
        {
            return InvalidRequestProblem();
        }

        try
        {
            var page = await engine.ListGroupConversationsAsync(
                ownership.Value.Identity.UserId,
                decodedCursor,
                take ?? 50,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new GroupConversationDirectoryResponse(
                page.Items.Select(ToResponse).ToArray(),
                page.NextCursor is { } next ? EncodeConversationCursor(next) : null));
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
    }

    private static async Task<IResult> RenameGroupConversationAsync(
        Guid conversationId,
        RenameGroupConversationRequest request,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        CancellationToken cancellationToken) =>
        await MutateGroupAsync(context, principalMapper, devices, async ownership =>
            await engine.RenameGroupConversationAsync(
                ownership.UserId,
                new ConversationId(conversationId),
                request.Title,
                request.ExpectedRevision,
                cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> AddGroupMemberAsync(
        Guid conversationId,
        AddGroupMemberRequest request,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        await MutateGroupAsync(context, principalMapper, devices, async ownership =>
            await engine.AddGroupMemberAsync(
                ownership.UserId,
                new ConversationId(conversationId),
                new UserId(request.UserId),
                request.ExpectedRevision,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> RemoveGroupMemberAsync(
        Guid conversationId,
        Guid userId,
        long revision,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        CancellationToken cancellationToken) =>
        await MutateGroupAsync(context, principalMapper, devices, async ownership =>
            await engine.RemoveGroupMemberAsync(
                ownership.UserId,
                new ConversationId(conversationId),
                new UserId(userId),
                revision,
                cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ChangeGroupMemberRoleAsync(
        Guid conversationId,
        Guid userId,
        ChangeGroupMemberRoleRequest request,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        CancellationToken cancellationToken) =>
        await MutateGroupAsync(context, principalMapper, devices, async ownership =>
            await engine.ChangeGroupMemberRoleAsync(
                ownership.UserId,
                new ConversationId(conversationId),
                new UserId(userId),
                (GroupConversationRole)request.Role,
                request.ExpectedRevision,
                cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> MutateGroupAsync(
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        Func<ChatRequestIdentity, ValueTask<GroupConversation>> mutation,
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
            return Results.Ok(ToResponse(await mutation(ownership.Value.Identity).ConfigureAwait(false)));
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

    private static async Task<IResult> ListConversationDevicesAsync(
        Guid conversationId,
        string? cursor,
        int? take,
        HttpContext context,
        IChatPrincipalMapper principalMapper,
        IDeviceRepository devices,
        ChatServerEngine engine,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireActiveOwnedDeviceAsync(
            context, principalMapper, devices, cancellationToken).ConfigureAwait(false);
        if (ownership is null)
        {
            return Results.Forbid();
        }

        if (!TryDecodeDeviceCursor(cursor, out var decodedCursor))
        {
            return InvalidRequestProblem();
        }

        try
        {
            var page = await engine.ListConversationDevicesAsync(
                ownership.Value.Identity.UserId,
                new ConversationId(conversationId),
                decodedCursor,
                take ?? 50,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(new DeviceDirectoryResponse(
                page.Items.Select(PublicDeviceResponse.FromDomain).ToArray(),
                page.NextCursor is { } next ? EncodeDeviceCursor(next) : null));
        }
        catch (ArgumentException)
        {
            return InvalidRequestProblem();
        }
        catch (ChatServerException)
        {
            return Results.Forbid();
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
        if (await ResolveIdentityAsync(context, principalMapper, cancellationToken).ConfigureAwait(false) is not { } identity)
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

    private static async ValueTask<ChatRequestIdentity?> ResolveIdentityAsync(HttpContext context,
        IChatPrincipalMapper mapper, CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetService<IChatRequestIdentityResolver>();
        return resolver is not null
            ? await resolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false)
            : mapper.TryMap(context.User, out var identity) ? identity : null;
    }

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

    private static GroupConversationResponse ToResponse(GroupConversation conversation) => new(
        conversation.ConversationId.Value,
        conversation.Title,
        conversation.CreatedByUserId.Value,
        conversation.Revision,
        conversation.CreatedAt,
        conversation.Members.Select(static member => new GroupConversationMemberResponse(
            member.UserId.Value,
            (byte)member.Role,
            member.JoinedAt)).ToArray());

    private static string EncodeConversationCursor(ConversationDirectoryCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[24];
        BinaryPrimitives.WriteInt64BigEndian(bytes, cursor.CreatedAt.UtcTicks);
        if (!cursor.ConversationId.Value.TryWriteBytes(bytes[8..], bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("Could not encode a conversation cursor.");
        }

        return ToBase64Url(bytes);
    }

    private static bool TryDecodeConversationCursor(
        string? value,
        out ConversationDirectoryCursor? cursor)
    {
        cursor = null;
        if (value is null)
        {
            return true;
        }

        if (!TryFromBase64Url(value, 24, out var bytes))
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        try
        {
            var conversationId = new Guid(bytes.AsSpan(8), bigEndian: true);
            if (ticks <= 0 || conversationId == Guid.Empty)
            {
                return false;
            }

            cursor = new ConversationDirectoryCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                new ConversationId(conversationId));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string EncodeDeviceCursor(DeviceDirectoryCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[32];
        if (!cursor.UserId.Value.TryWriteBytes(bytes, bigEndian: true, out var firstWritten) || firstWritten != 16 ||
            !cursor.DeviceId.Value.TryWriteBytes(bytes[16..], bigEndian: true, out var secondWritten) || secondWritten != 16)
        {
            throw new InvalidOperationException("Could not encode a device cursor.");
        }

        return ToBase64Url(bytes);
    }

    private static bool TryDecodeDeviceCursor(string? value, out DeviceDirectoryCursor? cursor)
    {
        cursor = null;
        if (value is null)
        {
            return true;
        }

        if (!TryFromBase64Url(value, 32, out var bytes))
        {
            return false;
        }

        var userId = new Guid(bytes.AsSpan(0, 16), bigEndian: true);
        var deviceId = new Guid(bytes.AsSpan(16, 16), bigEndian: true);
        if (userId == Guid.Empty || deviceId == Guid.Empty)
        {
            return false;
        }

        cursor = new DeviceDirectoryCursor(new UserId(userId), new DeviceId(deviceId));
        return true;
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, int expectedBytes, out byte[] bytes)
    {
        bytes = [];
        if (value.Length is 0 or > SkopkaChatHttpLimits.MaxCursorCharacters ||
            value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return bytes.Length == expectedBytes && string.Equals(ToBase64Url(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static IResult InvalidRequestProblem() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid chat request.");

    private static IResult ConflictProblem() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Chat operation rejected.");

    private static bool TryGetSingleHeader(HttpContext context, string name, out string value)
    {
        value = string.Empty;
        if (!context.Request.Headers.TryGetValue(name, out var values) || values.Count != 1)
        {
            return false;
        }

        value = values[0] ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetOptionalSingleHeader(HttpContext context, string name, out string? value)
    {
        value = null;
        if (!context.Request.Headers.TryGetValue(name, out var values))
        {
            return true;
        }

        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return false;
        }

        value = values[0];
        return true;
    }

    private readonly record struct OwnedDevice(ChatRequestIdentity Identity, PublicDevice Device);
}
