using System.Security.Cryptography;
using Skopka.Chat.Attachments;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.ServerTrusted;

/// <summary>A message to send.</summary>
public sealed record TrustedSendRequest
{
    /// <summary>Caller-generated idempotency key; an exact retry returns the original message.</summary>
    public required Guid ClientMessageId { get; init; }

    /// <summary>Plain text, optional when attachments are present. Rendering belongs to the host.</summary>
    public string? Text { get; init; }

    /// <summary>Optional reply target in the same conversation.</summary>
    public TrustedMessageId? ReplyToMessageId { get; init; }

    /// <summary>Structured host references (at most four).</summary>
    public IReadOnlyList<HostReference> References { get; init; } = [];

    /// <summary>Previously uploaded attachments of the same sender and conversation.</summary>
    public IReadOnlyList<TrustedAttachmentId> AttachmentIds { get; init; } = [];
}

/// <summary>Conversation metadata visible to an authorized reader.</summary>
public sealed record TrustedConversationView(
    TrustedConversation Conversation,
    TrustedParticipant Participant,
    IReadOnlyList<TrustedParticipant> Participants,
    int UnreadCount);

/// <summary>An authorized attachment download; the blob is copied only through this object.</summary>
public sealed class ServerTrustedAttachmentDownload
{
    private readonly Func<Stream, CancellationToken, ValueTask> _copy;

    internal ServerTrustedAttachmentDownload(TrustedAttachmentInfo info, Func<Stream, CancellationToken, ValueTask> copy)
    {
        Info = info;
        _copy = copy;
    }

    /// <summary>Stored metadata. Serve with <c>nosniff</c>, a download disposition and a sandbox CSP.</summary>
    public TrustedAttachmentInfo Info { get; }

    /// <summary>Copies the plaintext bytes to a caller-owned destination.</summary>
    public ValueTask CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return _copy(destination, cancellationToken);
    }
}

/// <summary>
/// Server-trusted chat engine. The server stores and reads plaintext. Every actor operation checks stored state and
/// then asks the host <see cref="IServerTrustedChatAccessPolicy"/>; host-management operations (creating conversations,
/// membership, closing, erasure, retention) take no actor and must be called only from trusted host code.
/// </summary>
public sealed class ServerTrustedChatService
{
    private readonly IServerTrustedChatStore _store;
    private readonly IServerTrustedChatAccessPolicy _policy;
    private readonly IServerTrustedChatRateLimiter _rateLimiter;
    private readonly ServerTrustedChatOptions _options;
    private readonly TimeProvider _time;
    private readonly IServerTrustedChatSignalSink? _signals;
    private readonly IAttachmentStore? _blobs;
    private readonly IServerTrustedAttachmentValidator? _validator;

    /// <summary>Creates the engine over host-selected storage and hooks.</summary>
    public ServerTrustedChatService(
        IServerTrustedChatStore store,
        IServerTrustedChatAccessPolicy policy,
        IServerTrustedChatRateLimiter rateLimiter,
        ServerTrustedChatOptions options,
        TimeProvider timeProvider,
        IServerTrustedChatSignalSink? signals = null,
        IAttachmentStore? attachmentStore = null,
        IServerTrustedAttachmentValidator? attachmentValidator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _signals = signals;
        _blobs = attachmentStore;
        _validator = attachmentValidator;
        if ((_blobs is null) != (_validator is null))
        {
            throw new ArgumentException("Attachments require both a blob store and a host validator.");
        }
    }

    /// <summary>Effective options.</summary>
    public ServerTrustedChatOptions Options => _options;

    // ---------------------------------------------------------------- host management

    /// <summary>Creates a conversation with its participants, or returns the existing one for the host key unchanged.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedConversation>> GetOrCreateConversationAsync(
        string hostKey,
        TrustedConversationKind kind,
        IReadOnlyList<TrustedParticipantSpec> participants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (!ServerTrustedChatValidation.IsHostKey(hostKey) || !Enum.IsDefined(kind) ||
            participants.Count is 0 || participants.Count > _options.MaxParticipants || participants.Any(static item => item is null) ||
            participants.Select(static item => item.UserId).Distinct().Count() != participants.Count ||
            (kind == TrustedConversationKind.Direct && participants.Count(static item => item.Role == TrustedParticipantRole.Writer) != 2))
        {
            return ServerTrustedChatResult<TrustedConversation>.Fail(ServerTrustedChatStatus.Invalid);
        }

        var (conversation, _) = await _store.GetOrCreateConversationAsync(
            new TrustedConversationCreate(TrustedConversationId.New(), hostKey, kind, participants, _time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        return ServerTrustedChatResult<TrustedConversation>.Ok(conversation);
    }

    /// <summary>Finds a conversation by host key (host code only).</summary>
    public ValueTask<TrustedConversation?> FindConversationAsync(string hostKey, CancellationToken cancellationToken = default) =>
        ServerTrustedChatValidation.IsHostKey(hostKey)
            ? _store.FindConversationAsync(hostKey, cancellationToken)
            : ValueTask.FromResult<TrustedConversation?>(null);

    /// <summary>Adds or updates a participation (host code only).</summary>
    public async ValueTask<ServerTrustedChatStatus> SetParticipantAsync(TrustedConversationId conversationId, TrustedParticipantSpec participant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        var outcome = await _store.UpsertParticipantAsync(conversationId, participant, _time.GetUtcNow(), _options.MaxParticipants, cancellationToken).ConfigureAwait(false);
        return await AfterConversationChangeAsync(conversationId, outcome, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a participation and its read marker (host code only).</summary>
    public async ValueTask<ServerTrustedChatStatus> RemoveParticipantAsync(TrustedConversationId conversationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var outcome = await _store.RemoveParticipantAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);
        return await AfterConversationChangeAsync(conversationId, outcome, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the conversation for writing; history is kept (host code only).</summary>
    public async ValueTask<ServerTrustedChatStatus> CloseAsync(TrustedConversationId conversationId, CancellationToken cancellationToken = default) =>
        await AfterConversationChangeAsync(
            conversationId,
            await _store.SetStatusAsync(conversationId, TrustedConversationStatus.Closed, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    /// <summary>Reopens a closed conversation with the same history (host code only).</summary>
    public async ValueTask<ServerTrustedChatStatus> ReopenAsync(TrustedConversationId conversationId, CancellationToken cancellationToken = default) =>
        await AfterConversationChangeAsync(
            conversationId,
            await _store.SetStatusAsync(conversationId, TrustedConversationStatus.Open, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    /// <summary>Whether a participant has read through a sequence; null without participation (host code only).</summary>
    public ValueTask<bool?> IsReadThroughAsync(TrustedConversationId conversationId, Guid userId, long sequence, CancellationToken cancellationToken = default) =>
        _store.IsReadThroughAsync(conversationId, userId, sequence, cancellationToken);

    /// <summary>Reads the staff access log of a conversation, newest first (host code only).</summary>
    public ValueTask<IReadOnlyList<TrustedAccessLogEntry>> GetAccessLogAsync(TrustedConversationId conversationId, int limit = 100, CancellationToken cancellationToken = default) =>
        _store.GetAccessLogAsync(conversationId, Math.Clamp(limit, 1, 1000), cancellationToken);

    /// <summary>
    /// Erases or reassigns a user's content, deletes their read markers and participations and replaces their ID in
    /// reports and the access log with the surrogate (host code only). Already-published events keep the original ID.
    /// </summary>
    public async ValueTask<ServerTrustedChatStatus> ForgetUserAsync(Guid userId, TrustedForgetMode mode, Guid surrogateUserId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || surrogateUserId == Guid.Empty || userId == surrogateUserId || !Enum.IsDefined(mode))
        {
            return ServerTrustedChatStatus.Invalid;
        }

        var deletions = await _store.ForgetUserAsync(userId, mode, surrogateUserId, cancellationToken).ConfigureAwait(false);
        await DeleteBlobsAsync(deletions.AttachmentIds, cancellationToken).ConfigureAwait(false);
        return ServerTrustedChatStatus.Success;
    }

    /// <summary>
    /// Hard-deletes a conversation with its history, participants, reports and attachments (host code only). The access
    /// log, which holds no message content, is kept for accountability until <see cref="PurgeAccessLogBeforeAsync"/>.
    /// </summary>
    public async ValueTask<ServerTrustedChatStatus> DeleteConversationAsync(TrustedConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var deletions = await _store.DeleteConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (deletions is null)
        {
            return ServerTrustedChatStatus.NotFoundOrForbidden;
        }

        await DeleteBlobsAsync(deletions.AttachmentIds, cancellationToken).ConfigureAwait(false);
        _signals?.Publish(new ServerTrustedChatSignal(conversationId, ServerTrustedChatSignalKind.ConversationChanged, long.MaxValue));
        return ServerTrustedChatStatus.Success;
    }

    /// <summary>Host retention rule: hard-deletes a bounded batch of conversations closed before the cutoff.</summary>
    public async ValueTask<int> PurgeClosedBeforeAsync(DateTimeOffset cutoff, int maximumCount = 100, CancellationToken cancellationToken = default)
    {
        if (cutoff == default || maximumCount is < 1 or > 1000)
        {
            throw new ArgumentException("The purge request is invalid.");
        }

        var (count, blobs) = await _store.PurgeClosedBeforeAsync(cutoff, maximumCount, cancellationToken).ConfigureAwait(false);
        await DeleteBlobsAsync(blobs.AttachmentIds, cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>Host retention rule for the staff access log: deletes a bounded batch of entries older than the cutoff.</summary>
    public ValueTask<int> PurgeAccessLogBeforeAsync(DateTimeOffset cutoff, int maximumCount = 1000, CancellationToken cancellationToken = default)
    {
        if (cutoff == default || maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentException("The access-log purge request is invalid.");
        }

        return _store.PurgeAccessLogBeforeAsync(cutoff, maximumCount, cancellationToken);
    }

    /// <summary>Expires unsent uploads and retries queued blob deletions; returns the number of blobs deleted.</summary>
    public async ValueTask<int> RunMaintenanceAsync(int maximumCount = 200, CancellationToken cancellationToken = default)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 1000);
        await _store.ExpirePendingAttachmentsAsync(_time.GetUtcNow() - _options.PendingAttachmentTtl, maximumCount, cancellationToken).ConfigureAwait(false);
        var queued = await _store.GetQueuedBlobDeletionsAsync(maximumCount, cancellationToken).ConfigureAwait(false);
        await DeleteBlobsAsync(queued.AttachmentIds, cancellationToken).ConfigureAwait(false);
        return queued.AttachmentIds.Count;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Conversation metadata, participants and own unread count for an authorized reader.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedConversationView>> GetConversationAsync(Guid actorUserId, TrustedConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var access = await AuthorizeReaderAsync(actorUserId, conversationId, ServerTrustedChatOperation.ReadConversation, null, cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return ServerTrustedChatResult<TrustedConversationView>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var participants = await _store.ListParticipantsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var unread = await _store.GetUnreadCountAsync(conversationId, actorUserId, _options.UnreadCountCap, cancellationToken).ConfigureAwait(false);
        return ServerTrustedChatResult<TrustedConversationView>.Ok(new TrustedConversationView(access.Value.Conversation, access.Value.Participant, participants, unread));
    }

    /// <summary>The actor's readable conversations that the host policy allows, with capped unread counts.</summary>
    public async ValueTask<IReadOnlyList<TrustedConversationSummary>> ListConversationsAsync(Guid actorUserId, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return [];
        }

        var items = await _store.ListConversationsAsync(actorUserId, Math.Clamp(limit, 1, 200), _options.UnreadCountCap, cancellationToken).ConfigureAwait(false);
        var allowed = new List<TrustedConversationSummary>(items.Count);
        foreach (var item in items)
        {
            if (await _policy.AuthorizeAsync(new ServerTrustedAccessContext
            {
                ActorUserId = actorUserId,
                Operation = ServerTrustedChatOperation.ReadConversation,
                Conversation = item.Conversation,
                Participant = item.Participant,
            }, cancellationToken).ConfigureAwait(false))
            {
                allowed.Add(item);
            }
        }

        return allowed;
    }

    /// <summary>
    /// Sum of capped unread counts over the actor's stored readable participations (one query, no per-conversation
    /// policy call). A host that revokes reading must also clear <see cref="TrustedParticipant.CanRead"/>.
    /// </summary>
    public ValueTask<int> GetUnreadTotalAsync(Guid actorUserId, CancellationToken cancellationToken = default) =>
        actorUserId == Guid.Empty
            ? ValueTask.FromResult(0)
            : _store.GetUnreadTotalAsync(actorUserId, _options.UnreadCountCap, cancellationToken);

    /// <summary>Reads the newest page, or the page before a sequence, ascending by sequence.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedMessagePage>> GetHistoryAsync(
        Guid actorUserId,
        TrustedConversationId conversationId,
        long? beforeSequence = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > ServerTrustedChatLimits.MaxPageSize || beforeSequence is < 1)
        {
            return ServerTrustedChatResult<TrustedMessagePage>.Fail(ServerTrustedChatStatus.Invalid);
        }

        var access = await AuthorizeReaderAsync(actorUserId, conversationId, ServerTrustedChatOperation.ReadHistory, null, cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return ServerTrustedChatResult<TrustedMessagePage>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var messages = await _store.GetMessagesAsync(conversationId, new TrustedMessageQuery
        {
            BeforeSequence = beforeSequence ?? long.MaxValue,
            MinSequence = access.Value.Participant.HistoryVisibleFromSequence,
            Limit = limit + 1,
        }, cancellationToken).ConfigureAwait(false);
        var hasMore = messages.Count > limit;
        var page = hasMore ? messages.Skip(messages.Count - limit).ToArray() : messages;
        return ServerTrustedChatResult<TrustedMessagePage>.Ok(new TrustedMessagePage(page, hasMore, access.Value.Conversation.ChangeRevision));
    }

    /// <summary>
    /// Reads messages changed after a revision (creations, edits, deletions, hiding, erasure), ascending by revision.
    /// Continue from <see cref="TrustedMessagePage.ChangeRevision"/> while <see cref="TrustedMessagePage.HasMoreBefore"/>
    /// (here: more changes pending) is true.
    /// </summary>
    public async ValueTask<ServerTrustedChatResult<TrustedMessagePage>> GetChangesAsync(
        Guid actorUserId,
        TrustedConversationId conversationId,
        long sinceRevision,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > ServerTrustedChatLimits.MaxPageSize || sinceRevision < 0)
        {
            return ServerTrustedChatResult<TrustedMessagePage>.Fail(ServerTrustedChatStatus.Invalid);
        }

        var access = await AuthorizeReaderAsync(actorUserId, conversationId, ServerTrustedChatOperation.ReadHistory, null, cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return ServerTrustedChatResult<TrustedMessagePage>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var messages = await _store.GetMessagesAsync(conversationId, new TrustedMessageQuery
        {
            ChangedSinceRevision = sinceRevision,
            MinSequence = access.Value.Participant.HistoryVisibleFromSequence,
            Limit = limit,
        }, cancellationToken).ConfigureAwait(false);
        var more = messages.Count == limit;
        var lastRevision = messages.Count == 0 ? sinceRevision : messages[^1].ChangeRevision;
        var cursor = more ? lastRevision : Math.Max(lastRevision, Math.Max(sinceRevision, access.Value.Conversation.ChangeRevision));
        return ServerTrustedChatResult<TrustedMessagePage>.Ok(new TrustedMessagePage(messages, more, cursor));
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Sends a message. An exact retry with the same client message ID returns the original.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedMessage>> SendAsync(Guid actorUserId, TrustedConversationId conversationId, TrustedSendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Text is null ? null : ServerTrustedChatValidation.NormalizeText(request.Text, _options.MaxTextLength);
        var references = request.References ?? [];
        var attachments = request.AttachmentIds ?? [];
        if (actorUserId == Guid.Empty || request.ClientMessageId == Guid.Empty ||
            (request.Text is not null && text is null) || (text is null && attachments.Count == 0) ||
            references.Count > ServerTrustedChatLimits.MaxHostReferences || references.Any(static item => item is null) ||
            references.Distinct().Count() != references.Count ||
            attachments.Count > _options.MaxAttachmentsPerMessage || attachments.Distinct().Count() != attachments.Count)
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.Invalid);
        }

        if (!await _rateLimiter.TryAcquireAsync(actorUserId, ServerTrustedChatOperation.Send, cancellationToken).ConfigureAwait(false))
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.RateLimited);
        }

        var access = await AuthorizeWriterAsync(actorUserId, conversationId, ServerTrustedChatOperation.Send, null, cancellationToken).ConfigureAwait(false);
        if (!access.IsSuccess)
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(access.Status);
        }

        foreach (var reference in references)
        {
            if (!await _policy.AuthorizeAsync(new ServerTrustedAccessContext
            {
                ActorUserId = actorUserId,
                Operation = ServerTrustedChatOperation.AttachHostReference,
                Conversation = access.Value.Conversation,
                Participant = access.Value.Participant,
                Reference = reference,
            }, cancellationToken).ConfigureAwait(false))
            {
                return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
            }
        }

        var result = await _store.SendAsync(new TrustedSendRecord(
            TrustedMessageId.New(),
            conversationId,
            actorUserId,
            request.ClientMessageId,
            text,
            request.ReplyToMessageId,
            references,
            attachments,
            _time.GetUtcNow(),
            Guid.CreateVersion7()), cancellationToken).ConfigureAwait(false);
        return MapMutation(conversationId, result.Outcome, result.Message);
    }

    /// <summary>Edits an own visible message in an open conversation.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedMessage>> EditAsync(Guid actorUserId, TrustedConversationId conversationId, TrustedMessageId messageId, string text, CancellationToken cancellationToken = default)
    {
        var normalized = ServerTrustedChatValidation.NormalizeText(text, _options.MaxTextLength);
        if (normalized is null || actorUserId == Guid.Empty)
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.Invalid);
        }

        if (!await _rateLimiter.TryAcquireAsync(actorUserId, ServerTrustedChatOperation.EditOwn, cancellationToken).ConfigureAwait(false))
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.RateLimited);
        }

        var access = await AuthorizeWriterAsync(actorUserId, conversationId, ServerTrustedChatOperation.EditOwn, messageId, cancellationToken).ConfigureAwait(false);
        if (!access.IsSuccess)
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(access.Status);
        }

        var result = await _store.EditAsync(conversationId, messageId, actorUserId, normalized, _time.GetUtcNow(),
            _options.RetainDeletedAndEditedContentForModeration, cancellationToken).ConfigureAwait(false);
        return MapMutation(conversationId, result.Outcome, result.Message);
    }

    /// <summary>Soft-deletes an own message in an open conversation; participants see a tombstone.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedMessage>> DeleteAsync(Guid actorUserId, TrustedConversationId conversationId, TrustedMessageId messageId, CancellationToken cancellationToken = default)
    {
        if (!await _rateLimiter.TryAcquireAsync(actorUserId, ServerTrustedChatOperation.DeleteOwn, cancellationToken).ConfigureAwait(false))
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.RateLimited);
        }

        var access = await AuthorizeWriterAsync(actorUserId, conversationId, ServerTrustedChatOperation.DeleteOwn, messageId, cancellationToken).ConfigureAwait(false);
        if (!access.IsSuccess)
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(access.Status);
        }

        var result = await _store.DeleteOwnAsync(conversationId, messageId, actorUserId, _time.GetUtcNow(),
            _options.RetainDeletedAndEditedContentForModeration, cancellationToken).ConfigureAwait(false);
        return MapMutation(conversationId, result.Outcome, result.Message);
    }

    /// <summary>Monotonically advances the actor's read marker; returns the stored value.</summary>
    public async ValueTask<ServerTrustedChatResult<long>> MarkReadAsync(Guid actorUserId, TrustedConversationId conversationId, long throughSequence, CancellationToken cancellationToken = default)
    {
        if (throughSequence < 0)
        {
            return ServerTrustedChatResult<long>.Fail(ServerTrustedChatStatus.Invalid);
        }

        var access = await AuthorizeReaderAsync(actorUserId, conversationId, ServerTrustedChatOperation.MarkRead, null, cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return ServerTrustedChatResult<long>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var result = await _store.MarkReadAsync(conversationId, actorUserId, throughSequence, _time.GetUtcNow(), Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case TrustedStoreOutcome.Applied:
                _signals?.Publish(new ServerTrustedChatSignal(conversationId, ServerTrustedChatSignalKind.ReadMarkerChanged,
                    access.Value.Conversation.ChangeRevision, actorUserId, result.ReadThroughSequence));
                return ServerTrustedChatResult<long>.Ok(result.ReadThroughSequence);
            case TrustedStoreOutcome.Unchanged:
            case TrustedStoreOutcome.Duplicate:
                return ServerTrustedChatResult<long>.Ok(result.ReadThroughSequence);
            default:
                return ServerTrustedChatResult<long>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }
    }

    // ---------------------------------------------------------------- attachments

    /// <summary>
    /// Uploads a plaintext attachment for a later message. The content is buffered (bounded by
    /// <see cref="ServerTrustedChatOptions.MaxAttachmentBytes"/>), validated by the host and stored immutably.
    /// </summary>
    public async ValueTask<ServerTrustedChatResult<TrustedAttachmentInfo>> UploadAttachmentAsync(
        Guid actorUserId,
        TrustedConversationId conversationId,
        string fileName,
        string declaredContentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_blobs is null || _validator is null)
        {
            return ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        if (!ServerTrustedChatValidation.IsFileName(fileName) || !ServerTrustedChatValidation.IsContentType(declaredContentType))
        {
            return ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(ServerTrustedChatStatus.Invalid);
        }

        if (!await _rateLimiter.TryAcquireAsync(actorUserId, ServerTrustedChatOperation.UploadAttachment, cancellationToken).ConfigureAwait(false))
        {
            return ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(ServerTrustedChatStatus.RateLimited);
        }

        var access = await AuthorizeWriterAsync(actorUserId, conversationId, ServerTrustedChatOperation.UploadAttachment, null, cancellationToken).ConfigureAwait(false);
        if (!access.IsSuccess)
        {
            return ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(access.Status);
        }

        var bytes = await ReadBoundedAsync(content, _options.MaxAttachmentBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(ServerTrustedChatStatus.Invalid);
        }

        var verdict = await _validator.ValidateAsync(new ServerTrustedAttachmentCandidate(actorUserId, conversationId, fileName, declaredContentType, bytes), cancellationToken).ConfigureAwait(false);
        if (verdict is null || !verdict.Accepted || verdict.ContentType is null)
        {
            return ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(ServerTrustedChatStatus.Invalid);
        }

        var now = _time.GetUtcNow();
        var info = new TrustedAttachmentInfo(TrustedAttachmentId.New(), fileName, verdict.ContentType, bytes.Length);
        await _store.BeginAttachmentAsync(new TrustedAttachmentRecord(info, conversationId, actorUserId, now, null, false), cancellationToken).ConfigureAwait(false);
        var stored = new StoredAttachment(
            new AttachmentId(info.Id.Value),
            new ConversationId(conversationId.Value),
            new UserId(actorUserId),
            bytes.Length,
            SHA256.HashData(bytes),
            now);
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            var put = await _blobs.TryPutAsync(stored, stream, cancellationToken).ConfigureAwait(false);
            if (put == AttachmentStoreResult.Conflict)
            {
                return ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(ServerTrustedChatStatus.Conflict);
            }
        }

        return await _store.CompleteAttachmentAsync(info.Id, cancellationToken).ConfigureAwait(false)
            ? ServerTrustedChatResult<TrustedAttachmentInfo>.Ok(info)
            : ServerTrustedChatResult<TrustedAttachmentInfo>.Fail(ServerTrustedChatStatus.Conflict);
    }

    /// <summary>Authorizes a participant download of an attachment that belongs to a visible message.</summary>
    public async ValueTask<ServerTrustedChatResult<ServerTrustedAttachmentDownload>> OpenAttachmentAsync(Guid actorUserId, TrustedAttachmentId attachmentId, CancellationToken cancellationToken = default)
    {
        var located = await LocateAttachmentAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (located is null || located.Value.Message.IsTombstone)
        {
            return ServerTrustedChatResult<ServerTrustedAttachmentDownload>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var (record, message) = located.Value;
        var access = await AuthorizeReaderAsync(actorUserId, message.ConversationId, ServerTrustedChatOperation.DownloadAttachment, message.Id, cancellationToken).ConfigureAwait(false);
        if (access is null || message.Sequence < access.Value.Participant.HistoryVisibleFromSequence)
        {
            return ServerTrustedChatResult<ServerTrustedAttachmentDownload>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        return ServerTrustedChatResult<ServerTrustedAttachmentDownload>.Ok(CreateDownload(record.Info));
    }

    // ---------------------------------------------------------------- moderation

    /// <summary>Reports a visible message; any reader, including an observer, may report a message once.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedReport>> ReportAsync(Guid actorUserId, TrustedMessageId messageId, string reasonCode, string? comment = null, CancellationToken cancellationToken = default)
    {
        var normalizedComment = comment is null ? null : ServerTrustedChatValidation.NormalizeText(comment, ServerTrustedChatLimits.MaxReportCommentLength);
        if (!ServerTrustedChatValidation.IsLabel(reasonCode) || (comment is not null && normalizedComment is null))
        {
            return ServerTrustedChatResult<TrustedReport>.Fail(ServerTrustedChatStatus.Invalid);
        }

        if (!await _rateLimiter.TryAcquireAsync(actorUserId, ServerTrustedChatOperation.Report, cancellationToken).ConfigureAwait(false))
        {
            return ServerTrustedChatResult<TrustedReport>.Fail(ServerTrustedChatStatus.RateLimited);
        }

        var message = await _store.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.IsTombstone)
        {
            return ServerTrustedChatResult<TrustedReport>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var access = await AuthorizeReaderAsync(actorUserId, message.ConversationId, ServerTrustedChatOperation.ReadConversation, messageId, cancellationToken).ConfigureAwait(false);
        if (access is null || message.Sequence < access.Value.Participant.HistoryVisibleFromSequence ||
            !await _policy.AuthorizeAsync(new ServerTrustedAccessContext
            {
                ActorUserId = actorUserId,
                Operation = ServerTrustedChatOperation.Report,
                Conversation = access.Value.Conversation,
                Participant = access.Value.Participant,
                MessageId = messageId,
            }, cancellationToken).ConfigureAwait(false))
        {
            return ServerTrustedChatResult<TrustedReport>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var report = new TrustedReport(TrustedReportId.New(), message.ConversationId, messageId, actorUserId, reasonCode, normalizedComment, _time.GetUtcNow());
        var (outcome, stored) = await _store.CreateReportAsync(new TrustedReportCreate(report, Guid.CreateVersion7()), cancellationToken).ConfigureAwait(false);
        return outcome is TrustedStoreOutcome.Applied or TrustedStoreOutcome.Duplicate && stored is not null
            ? ServerTrustedChatResult<TrustedReport>.Ok(stored)
            : ServerTrustedChatResult<TrustedReport>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
    }

    /// <summary>
    /// Returns the reported message and up to <see cref="ServerTrustedChatOptions.ReportWindowRadius"/> messages on each
    /// side to a non-participant the host allows for this report. The access-log entry is written atomically; if it
    /// cannot be written nothing is returned.
    /// </summary>
    public async ValueTask<ServerTrustedChatResult<ModerationWindow>> ReadReportWindowAsync(Guid actorUserId, TrustedReportId reportId, CancellationToken cancellationToken = default)
    {
        var authorized = await AuthorizeModeratorAsync(actorUserId, reportId, cancellationToken).ConfigureAwait(false);
        if (authorized is null)
        {
            return ServerTrustedChatResult<ModerationWindow>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var (report, from, to) = authorized.Value;
        var messages = await _store.ReadReportWindowAsync(report, from, to, new TrustedAccessLogEntry(
            Guid.CreateVersion7(), actorUserId, TrustedAccessAction.ReadReportWindow, report.ConversationId, report.Id, report.MessageId, from, to, _time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        return ServerTrustedChatResult<ModerationWindow>.Ok(new ModerationWindow(report, messages));
    }

    /// <summary>Opens an attachment of a message inside an authorized moderation window; the access is logged first.</summary>
    public async ValueTask<ServerTrustedChatResult<ServerTrustedAttachmentDownload>> OpenReportAttachmentAsync(Guid actorUserId, TrustedReportId reportId, TrustedAttachmentId attachmentId, CancellationToken cancellationToken = default)
    {
        var authorized = await AuthorizeModeratorAsync(actorUserId, reportId, cancellationToken).ConfigureAwait(false);
        var located = authorized is null ? null : await LocateAttachmentAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (authorized is null || located is null)
        {
            return ServerTrustedChatResult<ServerTrustedAttachmentDownload>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var (report, from, to) = authorized.Value;
        var message = located.Value.Message;
        if (message.ConversationId != report.ConversationId || message.Sequence < from || message.Sequence > to ||
            message.Tombstone == TrustedMessageTombstone.Erased)
        {
            return ServerTrustedChatResult<ServerTrustedAttachmentDownload>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        await _store.AppendAccessLogAsync(new TrustedAccessLogEntry(
            Guid.CreateVersion7(), actorUserId, TrustedAccessAction.OpenReportAttachment, report.ConversationId, report.Id, message.Id,
            message.Sequence, message.Sequence, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return ServerTrustedChatResult<ServerTrustedAttachmentDownload>.Ok(CreateDownload(located.Value.Record.Info));
    }

    /// <summary>Hides or unhides a message as a moderator allowed by the host <see cref="ServerTrustedChatOperation.Hide"/> policy.</summary>
    public async ValueTask<ServerTrustedChatResult<TrustedMessage>> SetHiddenAsync(Guid actorUserId, TrustedMessageId messageId, bool hidden, CancellationToken cancellationToken = default)
    {
        var message = await _store.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        var conversation = message is null ? null : await _store.GetConversationAsync(message.ConversationId, cancellationToken).ConfigureAwait(false);
        if (actorUserId == Guid.Empty || message is null || conversation is null)
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var participant = await _store.GetParticipantAsync(conversation.Id, actorUserId, cancellationToken).ConfigureAwait(false);
        if (!await _policy.AuthorizeAsync(new ServerTrustedAccessContext
        {
            ActorUserId = actorUserId,
            Operation = ServerTrustedChatOperation.Hide,
            Conversation = conversation,
            Participant = participant,
            MessageId = messageId,
        }, cancellationToken).ConfigureAwait(false))
        {
            return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        var result = await _store.SetHiddenAsync(messageId, hidden, new TrustedAccessLogEntry(
            Guid.CreateVersion7(), actorUserId, hidden ? TrustedAccessAction.Hide : TrustedAccessAction.Unhide, conversation.Id, null, messageId,
            message.Sequence, message.Sequence, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return MapMutation(conversation.Id, result.Outcome, result.Message);
    }

    // ---------------------------------------------------------------- helpers

    private async ValueTask<(TrustedReport Report, long From, long To)?> AuthorizeModeratorAsync(Guid actorUserId, TrustedReportId reportId, CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty)
        {
            return null;
        }

        var report = await _store.GetReportAsync(reportId, cancellationToken).ConfigureAwait(false);
        var conversation = report is null ? null : await _store.GetConversationAsync(report.ConversationId, cancellationToken).ConfigureAwait(false);
        var target = report is null ? null : await _store.GetMessageAsync(report.MessageId, cancellationToken).ConfigureAwait(false);
        if (report is null || conversation is null || target is null ||
            await _store.GetParticipantAsync(conversation.Id, actorUserId, cancellationToken).ConfigureAwait(false) is not null ||
            !await _policy.AuthorizeAsync(new ServerTrustedAccessContext
            {
                ActorUserId = actorUserId,
                Operation = ServerTrustedChatOperation.ReadReportWindow,
                Conversation = conversation,
                MessageId = report.MessageId,
                Report = report,
            }, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var radius = _options.ReportWindowRadius;
        return (report, Math.Max(1, target.Sequence - radius), target.Sequence + radius);
    }

    private async ValueTask<(TrustedAttachmentRecord Record, TrustedMessage Message)?> LocateAttachmentAsync(TrustedAttachmentId attachmentId, CancellationToken cancellationToken)
    {
        if (_blobs is null)
        {
            return null;
        }

        var record = await _store.GetAttachmentAsync(attachmentId, cancellationToken).ConfigureAwait(false);
        if (record?.MessageId is not { } messageId)
        {
            return null;
        }

        var message = await _store.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        return message is null ? null : (record, message);
    }

    private ServerTrustedAttachmentDownload CreateDownload(TrustedAttachmentInfo info)
    {
        var blobs = _blobs!;
        return new ServerTrustedAttachmentDownload(info, (destination, token) => blobs.CopyToAsync(new AttachmentId(info.Id.Value), destination, token));
    }

    private async ValueTask<(TrustedConversation Conversation, TrustedParticipant Participant)?> AuthorizeReaderAsync(
        Guid actorUserId,
        TrustedConversationId conversationId,
        ServerTrustedChatOperation operation,
        TrustedMessageId? messageId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty)
        {
            return null;
        }

        var conversation = await _store.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var participant = conversation is null ? null : await _store.GetParticipantAsync(conversationId, actorUserId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || participant is null || !participant.CanRead ||
            !await _policy.AuthorizeAsync(new ServerTrustedAccessContext
            {
                ActorUserId = actorUserId,
                Operation = operation,
                Conversation = conversation,
                Participant = participant,
                MessageId = messageId,
            }, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (conversation, participant);
    }

    private async ValueTask<ServerTrustedChatResult<(TrustedConversation Conversation, TrustedParticipant Participant)>> AuthorizeWriterAsync(
        Guid actorUserId,
        TrustedConversationId conversationId,
        ServerTrustedChatOperation operation,
        TrustedMessageId? messageId,
        CancellationToken cancellationToken)
    {
        var access = await AuthorizeReaderAsync(actorUserId, conversationId, operation, messageId, cancellationToken).ConfigureAwait(false);
        if (access is null || access.Value.Participant.Role != TrustedParticipantRole.Writer)
        {
            return ServerTrustedChatResult<(TrustedConversation, TrustedParticipant)>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }

        return access.Value.Conversation.Status == TrustedConversationStatus.Open
            ? ServerTrustedChatResult<(TrustedConversation, TrustedParticipant)>.Ok(access.Value)
            : ServerTrustedChatResult<(TrustedConversation, TrustedParticipant)>.Fail(ServerTrustedChatStatus.Closed);
    }

    private ServerTrustedChatResult<TrustedMessage> MapMutation(TrustedConversationId conversationId, TrustedStoreOutcome outcome, TrustedMessage? message)
    {
        switch (outcome)
        {
            case TrustedStoreOutcome.Applied when message is not null:
                _signals?.Publish(new ServerTrustedChatSignal(conversationId, ServerTrustedChatSignalKind.MessagesChanged, message.ChangeRevision));
                return ServerTrustedChatResult<TrustedMessage>.Ok(message);
            case TrustedStoreOutcome.Duplicate when message is not null:
            case TrustedStoreOutcome.Unchanged when message is not null:
                return ServerTrustedChatResult<TrustedMessage>.Ok(message);
            case TrustedStoreOutcome.Closed:
                return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.Closed);
            case TrustedStoreOutcome.Conflict:
                return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.Conflict);
            default:
                return ServerTrustedChatResult<TrustedMessage>.Fail(ServerTrustedChatStatus.NotFoundOrForbidden);
        }
    }

    private async ValueTask<ServerTrustedChatStatus> AfterConversationChangeAsync(TrustedConversationId conversationId, TrustedStoreOutcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case TrustedStoreOutcome.Applied:
                var conversation = await _store.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
                _signals?.Publish(new ServerTrustedChatSignal(conversationId, ServerTrustedChatSignalKind.ConversationChanged, conversation?.ChangeRevision ?? 0));
                return ServerTrustedChatStatus.Success;
            case TrustedStoreOutcome.Unchanged:
            case TrustedStoreOutcome.Duplicate:
                return ServerTrustedChatStatus.Success;
            case TrustedStoreOutcome.LimitReached:
                return ServerTrustedChatStatus.LimitReached;
            case TrustedStoreOutcome.Conflict:
                return ServerTrustedChatStatus.Conflict;
            default:
                return ServerTrustedChatStatus.NotFoundOrForbidden;
        }
    }

    private async ValueTask DeleteBlobsAsync(IReadOnlyList<TrustedAttachmentId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        if (_blobs is not null)
        {
            foreach (var id in ids)
            {
                await _blobs.DeleteAsync(new AttachmentId(id.Value), cancellationToken).ConfigureAwait(false);
            }
        }

        await _store.CompleteBlobDeletionsAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]?> ReadBoundedAsync(Stream content, long maximum, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximum)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }
}
