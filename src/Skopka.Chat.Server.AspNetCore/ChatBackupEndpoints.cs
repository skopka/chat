using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Server.AspNetCore;

/// <summary>Explicit account-authenticated backup endpoints; not mapped by the legacy chat registration.</summary>
public static class ChatBackupEndpointExtensions
{
    /// <summary>Maps opt-in binary backup routes. Register ChatBackupService, trusted IChatAuthorizationContextProvider and both named host policies.</summary>
    /// <remarks>Account policy must not require old device binding. Cookie hosts must supply CSRF protection; rates and concurrent requests must be bounded per account.</remarks>
    public static RouteGroupBuilder MapSkopkaChatBackups(this IEndpointRouteBuilder endpoints, string serviceId,
        string accountAuthorizationPolicy, string rateLimitPolicy, string prefix = SkopkaChatHttpRoutes.DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints); DeviceBindingEncoding.ValidateReference(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountAuthorizationPolicy); ArgumentException.ThrowIfNullOrWhiteSpace(rateLimitPolicy);
        var group = endpoints.MapGroup(prefix + ChatBackupHttpRoutes.Root).RequireAuthorization(accountAuthorizationPolicy).RequireRateLimiting(rateLimitPolicy);
        group.MapMethods("", ["GET", "PUT"], (Delegate)Handle);
        group.MapGet("/{archive}/head", (Delegate)Handle);
        group.MapMethods("/{archive}/versions/{version}", ["GET", "PUT", "POST"], (Delegate)Handle);
        group.MapMethods("/{archive}/versions/{version}/parts/{index}", ["GET", "PUT"], (Delegate)Handle);
        return group;

        async Task<IResult> Handle(HttpContext http)
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                if (!http.User.Identities.Any(identity => identity.IsAuthenticated)) { return Results.Unauthorized(); }
                var token = http.RequestAborted;
                var context = await http.RequestServices.GetRequiredService<IChatAuthorizationContextProvider>().GetContextAsync(http, token).ConfigureAwait(false);
                var time = http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                if (context is null || context.ServiceId != serviceId || context.ExpiresAt <= time.GetUtcNow()) { return Results.Forbid(); }
                var scope = new ChatBackupScope(serviceId, context.UserId);
                var service = http.RequestServices.GetRequiredService<ChatBackupService>();
                if (http.Request.Query.Count != 0) { throw new ChatBackupFormatException(); }
                var get = HttpMethods.IsGet(http.Request.Method);
                var archiveId = RouteId(http, "archive"); var versionId = RouteId(http, "version");
                if (archiveId is null)
                {
                    if (get) { var archive = await service.GetArchiveAsync(scope, token).ConfigureAwait(false); return Binary(archive is null ? null : ChatBackupEncoding.EncodeArchive(archive)); }
                    var value = ChatBackupEncoding.DecodeArchive(await Read(http, ChatBackupLimits.MaxControlBytes).ConfigureAwait(false));
                    return Binary([(byte)(await service.TryCreateArchiveAsync(scope, value, token).ConfigureAwait(false) ? 1 : 0)]);
                }
                if (versionId is null) { var head = await service.GetHeadAsync(scope, archiveId.Value, token).ConfigureAwait(false); return Binary(head is null ? null : ChatBackupEncoding.EncodeVersion(head)); }
                if (http.Request.RouteValues.TryGetValue("index", out var indexValue))
                {
                    if (!int.TryParse(indexValue?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index is < 0 or >= ChatBackupLimits.MaxParts) { throw new ChatBackupFormatException(); }
                    if (get) { return Binary(ChatBackupEncoding.EncodePart(await service.GetPartAsync(scope, archiveId.Value, versionId.Value, index, token).ConfigureAwait(false))); }
                    var part = ChatBackupEncoding.DecodePart(await Read(http, ChatBackupLimits.MaxPartBytes).ConfigureAwait(false));
                    if (part.UploadId != versionId || part.Index != index) { throw new ChatBackupFormatException(); }
                    await service.PutPartAsync(scope, archiveId.Value, part, token).ConfigureAwait(false); return Results.NoContent();
                }
                if (get) { var version = await service.GetVersionAsync(scope, archiveId.Value, versionId.Value, token).ConfigureAwait(false); return Binary(version is null ? null : ChatBackupEncoding.EncodeVersion(version)); }
                if (HttpMethods.IsPut(http.Request.Method))
                {
                    if ((await Read(http, 0).ConfigureAwait(false)).Length != 0) { throw new ChatBackupFormatException(); }
                    await service.BeginUploadAsync(scope, archiveId.Value, versionId.Value, token).ConfigureAwait(false); return Results.NoContent();
                }
                var seal = ChatBackupEncoding.DecodeVersion(await Read(http, ChatBackupLimits.MaxControlBytes).ConfigureAwait(false));
                if (seal.Archive.ArchiveId != archiveId || seal.VersionId != versionId) { throw new ChatBackupFormatException(); }
                return Binary([(byte)await service.CommitAsync(scope, seal, token).ConfigureAwait(false)]);
            }
            catch (BodyTooLargeException) { return Results.StatusCode(413); }
            catch (Exception error) when (error is ChatBackupFormatException or ArgumentException) { return Results.StatusCode(400); }
            catch (ChatBackupException error)
            {
                http.Response.Headers[ChatBackupHttpRoutes.FailureHeader] = ((int)error.Failure).ToString(CultureInfo.InvariantCulture);
                return Results.StatusCode(error.Failure switch
                {
                    ChatBackupFailure.Scope => 403,
                    ChatBackupFailure.NotFound => 404,
                    ChatBackupFailure.Conflict or ChatBackupFailure.Incomplete => 409,
                    ChatBackupFailure.Quota => 413,
                    _ => 503
                });
            }
            catch (IOException) { return Results.StatusCode(400); }
        }
    }
    private static Guid? RouteId(HttpContext http, string name)
    {
        if (!http.Request.RouteValues.TryGetValue(name, out var value)) { return null; }
        return Guid.TryParseExact(value?.ToString(), "D", out var id) && id != Guid.Empty ? id : throw new ChatBackupFormatException();
    }
    private static IResult Binary(byte[]? bytes) => bytes is null ? Results.NoContent() : Results.Bytes(bytes, ChatBackupHttpRoutes.ContentType);
    private static async ValueTask<byte[]> Read(HttpContext http, int maximum)
    {
        if (http.Request.ContentType != ChatBackupHttpRoutes.ContentType || http.Request.Headers.ContentEncoding.Count != 0) { throw new ChatBackupFormatException(); }
        if (http.Request.ContentLength > maximum) { throw new BodyTooLargeException(); }
        var buffer = new byte[maximum + 1]; var length = 0;
        while (length < buffer.Length)
        { var count = await http.Request.Body.ReadAsync(buffer.AsMemory(length), http.RequestAborted).ConfigureAwait(false); if (count == 0) { break; } length += count; }
        if (length > maximum) { throw new BodyTooLargeException(); }
        return buffer[..length];
    }
    private sealed class BodyTooLargeException : Exception;
}
