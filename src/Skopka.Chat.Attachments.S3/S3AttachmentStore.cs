using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Attachments.S3;

/// <summary>S3-compatible immutable object storage for encrypted attachments.</summary>
public sealed class S3AttachmentStore : IAttachmentStore
{
    private const string AttachmentIdMetadata = "skopka-attachment-id";
    private const string ConversationIdMetadata = "skopka-conversation-id";
    private const string UploaderUserIdMetadata = "skopka-uploader-user-id";
    private const string CiphertextLengthMetadata = "skopka-ciphertext-length";
    private const string CiphertextSha256Metadata = "skopka-ciphertext-sha256";
    private const string CreatedAtMetadata = "skopka-created-at";
    private const string ExpiresAtMetadata = "skopka-expires-at";
    private readonly IAmazonS3 _client;
    private readonly string _bucketName;
    private readonly string _keyPrefix;

    /// <summary>Creates a store over a caller-configured AWS SDK client.</summary>
    public S3AttachmentStore(IAmazonS3 client, S3AttachmentStoreOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);
        _bucketName = options.BucketName;
        _keyPrefix = options.KeyPrefix;
    }

    /// <inheritdoc />
    public async ValueTask<StoredAttachment?> GetMetadataAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        RequireId(attachmentId);
        try
        {
            var response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucketName, Key = GetKey(attachmentId) },
                cancellationToken).ConfigureAwait(false);
            return ParseMetadata(response.Metadata, response.ContentLength, attachmentId);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<AttachmentStoreResult> TryPutAsync(
        StoredAttachment attachment,
        Stream ciphertext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!ciphertext.CanRead)
        {
            throw new ArgumentException("Ciphertext stream must be readable.", nameof(ciphertext));
        }

        FileStream? spool = null;
        Stream upload = ciphertext;
        long originalPosition = 0;
        try
        {
            if (ciphertext.CanSeek)
            {
                originalPosition = ciphertext.Position;
            }
            else
            {
                var path = Path.Combine(Path.GetTempPath(), $"skopka-chat-{Guid.NewGuid():N}.ciphertext");
                spool = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                upload = spool;
            }

            await ValidateAndPrepareAsync(attachment, ciphertext, spool, originalPosition, cancellationToken)
                .ConfigureAwait(false);

            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = GetKey(attachment.AttachmentId),
                InputStream = upload,
                AutoCloseStream = false,
                IfNoneMatch = "*",
                ChecksumSHA256 = Convert.ToBase64String(attachment.CiphertextSha256.Span)
            };
            AddMetadata(request, attachment);
            try
            {
                await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                return AttachmentStoreResult.Stored;
            }
            catch (AmazonS3Exception exception) when (
                exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                var existing = await GetMetadataAsync(attachment.AttachmentId, cancellationToken).ConfigureAwait(false);
                return existing is not null && IsSameImmutableBlob(existing, attachment)
                    ? AttachmentStoreResult.Duplicate
                    : AttachmentStoreResult.Conflict;
            }
        }
        finally
        {
            if (ciphertext.CanSeek)
            {
                ciphertext.Position = originalPosition;
            }

            if (spool is not null)
            {
                await spool.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask CopyToAsync(
        AttachmentId attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        RequireId(attachmentId);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        using var response = await _client.GetObjectAsync(
            new GetObjectRequest { BucketName = _bucketName, Key = GetKey(attachmentId) },
            cancellationToken).ConfigureAwait(false);
        var metadata = ParseMetadata(response.Metadata, response.ContentLength, attachmentId);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var copied = await CopyAndHashAsync(response.ResponseStream, destination, hash, cancellationToken)
            .ConfigureAwait(false);
        if (copied != metadata.CiphertextLength ||
            !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), metadata.CiphertextSha256.Span))
        {
            throw new InvalidDataException("Stored encrypted attachment failed integrity validation.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        RequireId(attachmentId);
        if (await GetMetadataAsync(attachmentId, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        await _client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = _bucketName, Key = GetKey(attachmentId) },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        string? continuationToken = null;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = _keyPrefix,
                ContinuationToken = continuationToken
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseKey(item.Key, out var attachmentId))
                {
                    continue;
                }

                var metadata = await GetMetadataAsync(attachmentId, cancellationToken).ConfigureAwait(false);
                if (metadata?.ExpiresAt is { } expiresAt && expiresAt <= now &&
                    await DeleteAsync(attachmentId, cancellationToken).ConfigureAwait(false))
                {
                    deleted++;
                }
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        return deleted;
    }

    private static async ValueTask ValidateAndPrepareAsync(
        StoredAttachment attachment,
        Stream source,
        FileStream? spool,
        long originalPosition,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long remaining = attachment.CiphertextLength;
        while (remaining > 0)
        {
            var requested = checked((int)Math.Min(remaining, buffer.Length));
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Encrypted attachment ended before its declared length.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            if (spool is not null)
            {
                await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            remaining -= read;
        }

        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0 ||
            !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), attachment.CiphertextSha256.Span))
        {
            throw new InvalidDataException("Encrypted attachment does not match its declared metadata.");
        }

        if (spool is not null)
        {
            spool.Position = 0;
        }
        else
        {
            source.Position = originalPosition;
        }
    }

    private static async ValueTask<long> CopyAndHashAsync(
        Stream source,
        Stream destination,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (copied > AttachmentStorageLimits.MaxCiphertextBytes)
            {
                throw new InvalidDataException("Stored encrypted attachment exceeds its limit.");
            }
        }

        return copied;
    }

    private static void AddMetadata(PutObjectRequest request, StoredAttachment attachment)
    {
        request.Metadata.Add(AttachmentIdMetadata, attachment.AttachmentId.ToString());
        request.Metadata.Add(ConversationIdMetadata, attachment.ConversationId.ToString());
        request.Metadata.Add(UploaderUserIdMetadata, attachment.UploaderUserId.ToString());
        request.Metadata.Add(CiphertextLengthMetadata, attachment.CiphertextLength.ToString(CultureInfo.InvariantCulture));
        request.Metadata.Add(CiphertextSha256Metadata, Convert.ToHexString(attachment.CiphertextSha256.Span));
        request.Metadata.Add(CreatedAtMetadata, attachment.CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (attachment.ExpiresAt is { } expiresAt)
        {
            request.Metadata.Add(ExpiresAtMetadata, expiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }
    }

    private static StoredAttachment ParseMetadata(
        MetadataCollection metadata,
        long contentLength,
        AttachmentId expectedAttachmentId)
    {
        try
        {
            var attachmentId = new AttachmentId(Guid.Parse(GetMetadata(metadata, AttachmentIdMetadata)));
            var conversationId = new ConversationId(Guid.Parse(GetMetadata(metadata, ConversationIdMetadata)));
            var uploaderUserId = new UserId(Guid.Parse(GetMetadata(metadata, UploaderUserIdMetadata)));
            var declaredLength = long.Parse(GetMetadata(metadata, CiphertextLengthMetadata), CultureInfo.InvariantCulture);
            var ciphertextSha256 = Convert.FromHexString(GetMetadata(metadata, CiphertextSha256Metadata));
            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(
                long.Parse(GetMetadata(metadata, CreatedAtMetadata), CultureInfo.InvariantCulture));
            var expiresValue = GetOptionalMetadata(metadata, ExpiresAtMetadata);
            DateTimeOffset? expiresAt = expiresValue is null
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(expiresValue, CultureInfo.InvariantCulture));
            if (attachmentId != expectedAttachmentId || declaredLength != contentLength)
            {
                throw new InvalidDataException();
            }

            return new StoredAttachment(
                attachmentId,
                conversationId,
                uploaderUserId,
                declaredLength,
                ciphertextSha256,
                createdAt,
                expiresAt);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException("Stored encrypted attachment metadata is invalid.");
        }
    }

    private static string GetMetadata(MetadataCollection metadata, string name) =>
        GetOptionalMetadata(metadata, name) ?? throw new KeyNotFoundException();

    private static string? GetOptionalMetadata(MetadataCollection metadata, string name)
    {
        var value = metadata[name];
        return string.IsNullOrEmpty(value) ? metadata[$"x-amz-meta-{name}"] : value;
    }

    private static bool IsSameImmutableBlob(StoredAttachment left, StoredAttachment right) =>
        left.AttachmentId == right.AttachmentId &&
        left.ConversationId == right.ConversationId &&
        left.UploaderUserId == right.UploaderUserId &&
        left.CiphertextLength == right.CiphertextLength &&
        left.ExpiresAt == right.ExpiresAt &&
        CryptographicOperations.FixedTimeEquals(left.CiphertextSha256.Span, right.CiphertextSha256.Span);

    private string GetKey(AttachmentId attachmentId) => _keyPrefix + attachmentId.Value.ToString("N");

    private bool TryParseKey(string key, out AttachmentId attachmentId)
    {
        attachmentId = default;
        return key.StartsWith(_keyPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(key[_keyPrefix.Length..], "N", out var value) &&
            (attachmentId = new AttachmentId(value)).Value != Guid.Empty;
    }

    private static void RequireId(AttachmentId attachmentId)
    {
        if (attachmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Attachment ID must not be empty.", nameof(attachmentId));
        }
    }
}
