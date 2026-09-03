using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Http;

public sealed partial class SkopkaChatHttpClient : IChatBackupTransport
{
    /// <inheritdoc />
    public async ValueTask<ChatBackupArchive?> GetArchiveAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await BackupRequest(HttpMethod.Get, ChatBackupHttpRoutes.Root, null, ChatBackupLimits.MaxControlBytes, cancellationToken).ConfigureAwait(false);
        var result = bytes is null ? null : ChatBackupEncoding.DecodeArchive(bytes);
        if (result is not null && result.Scope.UserId != _authenticatedUserId) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        return result;
    }
    /// <inheritdoc />
    public async ValueTask<bool> TryCreateArchiveAsync(ChatBackupArchive archive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive); if (archive.Scope.UserId != _authenticatedUserId) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        var bytes = await BackupRequest(HttpMethod.Put, ChatBackupHttpRoutes.Root, ChatBackupEncoding.EncodeArchive(archive), 1, cancellationToken).ConfigureAwait(false);
        if (bytes is not { Length: 1 } || bytes[0] > 1) { throw new ChatBackupFormatException(); }
        return bytes[0] == 1;
    }
    /// <inheritdoc />
    public ValueTask<ChatBackupVersion?> GetHeadAsync(Guid archiveId, CancellationToken cancellationToken = default) => BackupVersion(archiveId, null, cancellationToken);
    /// <inheritdoc />
    public ValueTask<ChatBackupVersion?> GetVersionAsync(Guid archiveId, Guid versionId, CancellationToken cancellationToken = default) => BackupVersion(archiveId, versionId, cancellationToken);
    /// <inheritdoc />
    public async ValueTask BeginUploadAsync(Guid archiveId, Guid uploadId, CancellationToken cancellationToken = default)
    { await BackupRequest(HttpMethod.Put, ChatBackupHttpRoutes.Version(archiveId, uploadId), [], 0, cancellationToken).ConfigureAwait(false); }
    /// <inheritdoc />
    public async ValueTask PutPartAsync(Guid archiveId, ChatBackupPart part, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        await BackupRequest(HttpMethod.Put, ChatBackupHttpRoutes.Part(archiveId, part.UploadId, part.Index), ChatBackupEncoding.EncodePart(part), 0, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async ValueTask<ChatBackupCommitResult> CommitAsync(ChatBackupVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version); if (version.Archive.Scope.UserId != _authenticatedUserId) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        var bytes = await BackupRequest(HttpMethod.Post, ChatBackupHttpRoutes.Version(version.Archive.ArchiveId, version.VersionId), ChatBackupEncoding.EncodeVersion(version), 1, cancellationToken).ConfigureAwait(false);
        if (bytes is not { Length: 1 } || bytes[0] is < 1 or > 3) { throw new ChatBackupFormatException(); }
        return (ChatBackupCommitResult)bytes[0];
    }
    /// <inheritdoc />
    public async ValueTask<ChatBackupPart?> GetPartAsync(Guid archiveId, Guid uploadId, int index, CancellationToken cancellationToken = default)
    {
        var bytes = await BackupRequest(HttpMethod.Get, ChatBackupHttpRoutes.Part(archiveId, uploadId, index), null, ChatBackupLimits.MaxPartBytes, cancellationToken).ConfigureAwait(false);
        var part = ChatBackupEncoding.DecodePart(bytes ?? throw new ChatBackupException(ChatBackupFailure.Incomplete));
        if (part.UploadId != uploadId || part.Index != index) { throw new ChatBackupFormatException(); }
        return part;
    }
    private async ValueTask<ChatBackupVersion?> BackupVersion(Guid archive, Guid? version, CancellationToken ct)
    {
        var bytes = await BackupRequest(HttpMethod.Get, version is null ? ChatBackupHttpRoutes.Head(archive) : ChatBackupHttpRoutes.Version(archive, version.Value), null, ChatBackupLimits.MaxControlBytes, ct).ConfigureAwait(false);
        var result = bytes is null ? null : ChatBackupEncoding.DecodeVersion(bytes);
        if (result is not null && (result.Archive.Scope.UserId != _authenticatedUserId || result.Archive.ArchiveId != archive || version is not null && result.VersionId != version)) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        return result;
    }
    private async ValueTask<byte[]?> BackupRequest(HttpMethod method, string route, byte[]? body, int maximum, CancellationToken ct)
    {
        try
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(method, BuildUri(route));
                if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue(ChatBackupHttpRoutes.ContentType); }
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ChatBackupHttpRoutes.ContentType)); return request;
            }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ChatBackupFailure.Unavailable;
                if (response.Headers.TryGetValues(ChatBackupHttpRoutes.FailureHeader, out var values))
                {
                    var fields = values.Take(2).ToArray();
                    if (fields.Length == 1 && fields[0].Length <= 2 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var code) && Enum.IsDefined((ChatBackupFailure)code)) { failure = (ChatBackupFailure)code; }
                }
                throw new ChatBackupException(failure);
            }
            if (response.StatusCode == HttpStatusCode.NoContent) { return null; }
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.ToString() != ChatBackupHttpRoutes.ContentType ||
                response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > maximum) { throw new ChatBackupFormatException(); }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); var buffer = new byte[maximum + 1]; var length = 0;
            while (length < buffer.Length) { var count = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false); if (count == 0) { break; } length += count; }
            if (length > maximum) { throw new ChatBackupFormatException(); }
            return buffer[..length];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ChatBackupException) { throw; }
        catch (ChatBackupFormatException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { throw new ChatBackupException(ChatBackupFailure.Unavailable); }
    }
}
