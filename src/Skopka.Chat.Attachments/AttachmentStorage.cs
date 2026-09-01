using Skopka.Chat.Protocol;

namespace Skopka.Chat.Attachments;

/// <summary>Immutable encrypted-blob persistence boundary.</summary>
public interface IAttachmentStore
{
    /// <summary>Gets opaque metadata without opening the blob.</summary>
    ValueTask<StoredAttachment?> GetMetadataAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically creates a blob after validating its exact length and SHA-256. Implementations must never overwrite.
    /// </summary>
    ValueTask<AttachmentStoreResult> TryPutAsync(
        StoredAttachment attachment,
        Stream ciphertext,
        CancellationToken cancellationToken = default);

    /// <summary>Copies the exact encrypted blob to a caller-owned destination.</summary>
    ValueTask CopyToAsync(
        AttachmentId attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an encrypted blob and its metadata if present.</summary>
    ValueTask<bool> DeleteAsync(
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes expired blobs and returns the number removed.</summary>
    ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>Host authorization boundary for attachment operations.</summary>
public interface IAttachmentAccessAuthorizer
{
    /// <summary>Returns whether an authenticated user may add a blob to the conversation.</summary>
    ValueTask<bool> CanUploadAsync(
        UserId authenticatedUserId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether an authenticated user may read the stored blob.</summary>
    ValueTask<bool> CanDownloadAsync(
        UserId authenticatedUserId,
        StoredAttachment attachment,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether an authenticated user may delete the stored blob.</summary>
    ValueTask<bool> CanDeleteAsync(
        UserId authenticatedUserId,
        StoredAttachment attachment,
        CancellationToken cancellationToken = default);
}

/// <summary>Transport-neutral authorization and storage orchestration for encrypted attachments.</summary>
public sealed class AttachmentStorageService
{
    private readonly IAttachmentStore _store;
    private readonly IAttachmentAccessAuthorizer _authorizer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an attachment service over host-selected storage and authorization.</summary>
    public AttachmentStorageService(
        IAttachmentStore store,
        IAttachmentAccessAuthorizer authorizer,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Authorizes and atomically stores authenticated ciphertext.</summary>
    public async ValueTask<AttachmentStoreResult> UploadAsync(
        UserId authenticatedUserId,
        AttachmentUploadRequest request,
        Stream ciphertext,
        CancellationToken cancellationToken = default)
    {
        AttachmentValidation.RequireId(authenticatedUserId, nameof(authenticatedUserId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!ciphertext.CanRead)
        {
            throw new ArgumentException("Ciphertext stream must be readable.", nameof(ciphertext));
        }

        if (!await _authorizer.CanUploadAsync(authenticatedUserId, request.ConversationId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new AttachmentServiceException("Attachment operation is not permitted.");
        }

        var now = _timeProvider.GetUtcNow();
        if (request.ExpiresAt.HasValue && request.ExpiresAt <= now)
        {
            throw new AttachmentServiceException("Attachment retention is invalid.");
        }

        var metadata = new StoredAttachment(
            request.AttachmentId,
            request.ConversationId,
            authenticatedUserId,
            request.CiphertextLength,
            request.CiphertextSha256.Span,
            now,
            request.ExpiresAt);
        return await _store.TryPutAsync(metadata, ciphertext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Authorizes and copies ciphertext to a caller-owned stream.</summary>
    public async ValueTask<StoredAttachment> DownloadAsync(
        UserId authenticatedUserId,
        AttachmentId attachmentId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        AttachmentValidation.RequireId(authenticatedUserId, nameof(authenticatedUserId));
        AttachmentValidation.RequireId(attachmentId, nameof(attachmentId));
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        }

        var metadata = await GetDownloadMetadataAsync(authenticatedUserId, attachmentId, cancellationToken)
            .ConfigureAwait(false);

        await _store.CopyToAsync(attachmentId, destination, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    /// <summary>Gets metadata only after applying the same availability and authorization checks as download.</summary>
    public async ValueTask<StoredAttachment> GetDownloadMetadataAsync(
        UserId authenticatedUserId,
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        AttachmentValidation.RequireId(authenticatedUserId, nameof(authenticatedUserId));
        AttachmentValidation.RequireId(attachmentId, nameof(attachmentId));
        var metadata = await _store.GetMetadataAsync(attachmentId, cancellationToken).ConfigureAwait(false)
            ?? throw new AttachmentServiceException("Attachment is unavailable.");
        var now = _timeProvider.GetUtcNow();
        if ((metadata.ExpiresAt.HasValue && metadata.ExpiresAt <= now) ||
            !await _authorizer.CanDownloadAsync(authenticatedUserId, metadata, cancellationToken).ConfigureAwait(false))
        {
            throw new AttachmentServiceException("Attachment is unavailable.");
        }

        return metadata;
    }

    /// <summary>Authorizes and deletes a stored ciphertext blob.</summary>
    public async ValueTask<bool> DeleteAsync(
        UserId authenticatedUserId,
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default)
    {
        AttachmentValidation.RequireId(authenticatedUserId, nameof(authenticatedUserId));
        AttachmentValidation.RequireId(attachmentId, nameof(attachmentId));
        var metadata = await _store.GetMetadataAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return false;
        }

        if (!await _authorizer.CanDeleteAsync(authenticatedUserId, metadata, cancellationToken).ConfigureAwait(false))
        {
            throw new AttachmentServiceException("Attachment operation is not permitted.");
        }

        return await _store.DeleteAsync(attachmentId, cancellationToken).ConfigureAwait(false);
    }
}
