using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots.AspNetCore;

/// <summary>Private single-bot HTTP API. The application supplies authentication, TLS, quotas and consent.</summary>
public static class BotEndpointExtensions
{
    /// <summary>Exactly one authenticated claim must match the configured bot account.</summary>
    public const string BotUserClaim = "skopka_chat_bot";
    /// <summary>Largest request body, including escaped UTF-8 text and JSON metadata.</summary>
    public const int MaximumRequestBytes = 128 * 1024;

    /// <summary>
    /// Maps a single-bot gateway with an explicit host authorization policy. The policy must validate
    /// credentials and issue exactly one <see cref="BotUserClaim"/>. Never mount in the chat server.
    /// </summary>
    public static RouteGroupBuilder MapSkopkaChatBotApi(this IEndpointRouteBuilder endpoints,
        string authorizationPolicy, string prefix = "/bot/v1")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        var group = endpoints.MapGroup(prefix).RequireAuthorization(authorizationPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.QueryString.HasValue || !IsBotPrincipal(http.User, http.RequestServices))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            try { return await next(context).ConfigureAwait(false); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (BadHttpRequestException exception) { return Results.StatusCode(exception.StatusCode); }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }
            catch (Exception)
            {
                // Remote/library/storage exceptions may carry endpoint data. Do not reflect/log them here.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
        group.MapGet("/getMe", (ChatBotRuntime runtime) =>
        {
            var p = runtime.Profile;
            return Json(new ProfileResponse(p.BotUserId.Value, p.Name, p.OperatorId, p.OperatorName,
                p.Hosting.ToString(), p.Revision), BotHttpJson.Default.ProfileResponse);
        });
        group.MapPost("/getUpdates", async (HttpContext http, ChatBotRuntime runtime) =>
        {
            var request = await ReadAsync(http, BotHttpJson.Default.UpdatesRequest).ConfigureAwait(false);
            var updates = await runtime.GetUpdatesAsync(request.Limit, http.RequestAborted).ConfigureAwait(false);
            return Json(new UpdatesResponse(updates.Select(u => new UpdateResponse(u.UpdateId, u.ConversationId.Value,
                u.SenderUserId.Value, u.ContentId.Value, u.Text, u.ReplyToContentId?.Value, u.IsForwarded)).ToArray()),
                BotHttpJson.Default.UpdatesResponse);
        });
        group.MapPost("/acknowledgeUpdate", async (HttpContext http, ChatBotRuntime runtime) =>
        {
            var request = await ReadAsync(http, BotHttpJson.Default.AcknowledgeRequest).ConfigureAwait(false);
            await runtime.AcknowledgeUpdateAsync(request.UpdateId, http.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });
        group.MapPost("/sendMessage", async (HttpContext http, ChatBotRuntime runtime) =>
        {
            var request = await ReadAsync(http, BotHttpJson.Default.SendRequest).ConfigureAwait(false);
            var result = await runtime.SendMessageAsync(new ConversationId(request.ConversationId), request.RequestId,
                request.Text, request.ReplyToContentId is { } reply ? new ChatContentId(reply) : null,
                http.RequestAborted).ConfigureAwait(false);
            return Json(new SendResponse(request.RequestId, result.Succeeded, result.AcceptedCount, result.RequiredCount), BotHttpJson.Default.SendResponse);
        });
        return group;
    }

    private static bool IsBotPrincipal(ClaimsPrincipal principal, IServiceProvider services)
    {
        var ids = principal.FindAll(BotUserClaim).ToArray();
        return principal.Identity?.IsAuthenticated == true && ids.Length == 1 && Guid.TryParseExact(ids[0].Value, "D", out var user) &&
            services.GetService(typeof(ChatBotRuntime)) is ChatBotRuntime runtime && user == runtime.Profile.BotUserId.Value;
    }

    private static IResult Json<T>(T value, JsonTypeInfo<T> type)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (bytes.Length > 2 * 1024 * 1024) { throw new ChatBotException(); }
        return Results.Bytes(bytes, "application/json; charset=utf-8");
    }

    private static async ValueTask<T> ReadAsync<T>(HttpContext http, JsonTypeInfo<T> type)
    {
        if (!http.Request.HasJsonContentType()) { throw new BadHttpRequestException("Invalid media type.", 415); }
        if (http.Request.ContentLength > MaximumRequestBytes) { throw new BadHttpRequestException("Request too large.", 413); }
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted).ConfigureAwait(false)) != 0)
            {
                if (body.Length + count > MaximumRequestBytes) { throw new BadHttpRequestException("Request too large.", 413); }
                body.Write(buffer, 0, count);
            }
            return JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, checked((int)body.Length)), type) ??
                throw new BadHttpRequestException("Invalid request.", 400);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(body.GetBuffer());
        }
    }
}
