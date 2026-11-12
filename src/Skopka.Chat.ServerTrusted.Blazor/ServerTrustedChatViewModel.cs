namespace Skopka.Chat.ServerTrusted.Blazor;

/// <summary>Generic, expected failure retained by the view-model. It never carries remote or exception text.</summary>
public enum ServerTrustedChatFailure
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>The conversation does not exist or the user may no longer read it; the timeline was cleared.</summary>
    Unavailable = 1,

    /// <summary>The conversation is closed for writing.</summary>
    Closed = 2,

    /// <summary>The host rate limiter rejected the operation.</summary>
    RateLimited = 3,

    /// <summary>The operation failed; it may be retried.</summary>
    Failed = 4,

    /// <summary>An attachment was rejected (size, count, type or host validation).</summary>
    AttachmentRejected = 5,
}

/// <summary>
/// Thread-safe presentation state of one server-trusted conversation for one user. Every engine call runs through an
/// <see cref="IServerTrustedChatOperationRunner"/> (by default a fresh service scope), so every displayed item passes
/// the engine's stored-state and host-policy authorization. Live signals and periodic refreshes call
/// <see cref="CatchUpAsync"/>, which re-reads changes since the revision cursor.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The gate never allocates a wait handle; it holds no unmanaged resource.")]
public sealed class ServerTrustedChatViewModel
{
    private const int ChangesPageSize = ServerTrustedChatLimits.MaxPageSize;
    private const int MaxCatchUpPages = 10;
    private const int DefaultMaxTextLength = 4000;
    private const int DefaultMaxAttachmentsPerMessage = 3;
    private const long DefaultMaxAttachmentBytes = 10L * 1024 * 1024;

    private readonly IServerTrustedChatOperationRunner _runner;
    private readonly int _pageSize;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<TrustedMessageId, TrustedMessage> _byId = [];
    private readonly HashSet<TrustedMessageId> _reported = [];

    private TrustedMessage[] _timeline = [];
    private TrustedConversationView? _conversation;
    private TrustedParticipant[] _observers = [];
    private long _revision;
    private bool _hasMoreBefore;
    private bool _isLoaded;
    private int _maxTextLength = DefaultMaxTextLength;
    private int _maxAttachmentsPerMessage = DefaultMaxAttachmentsPerMessage;
    private long _maxAttachmentBytes = DefaultMaxAttachmentBytes;
    private int _uploadsInFlight;
    private readonly List<TrustedAttachmentInfo> _pendingAttachments = [];
    private readonly List<HostReference> _references = [];
    private TrustedAttachmentInfo[] _pendingAttachmentsSnapshot = [];
    private HostReference[] _referencesSnapshot = [];
    private HostReference? _hostReference;
    private bool _hostReferenceOwned;
    private long _readThrough;
    private ServerTrustedChatFailure _failure;
    private bool _isBusy;

    private string _draft = string.Empty;
    private TrustedMessageId? _replyTo;
    private TrustedMessageId? _editing;
    private Guid? _pendingClientMessageId;
    private string _stashedDraft = string.Empty;
    private TrustedMessageId? _stashedReplyTo;
    private Guid? _stashedPendingClientMessageId;

    /// <summary>Creates presentation state for one conversation and one server-authenticated user.</summary>
    /// <param name="conversationId">Conversation to show.</param>
    /// <param name="userId">Authenticated actor; every engine call is still authorized.</param>
    /// <param name="runner">Runs engine operations, normally inside a fresh service scope.</param>
    /// <param name="pageSize">History page size (1–100, default 50).</param>
    public ServerTrustedChatViewModel(TrustedConversationId conversationId, Guid userId, IServerTrustedChatOperationRunner runner, int pageSize = 50)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The user identifier is invalid.", nameof(userId));
        }

        if (pageSize is < 1 or > ServerTrustedChatLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        ConversationId = conversationId;
        UserId = userId;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _pageSize = pageSize;
    }

    /// <summary>Raised after any state change, possibly on a background thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Conversation shown.</summary>
    public TrustedConversationId ConversationId { get; }

    /// <summary>Current user.</summary>
    public Guid UserId { get; }

    /// <summary>Loaded messages ordered by sequence; edits and tombstones replace items by identifier.</summary>
    public IReadOnlyList<TrustedMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _timeline;
            }
        }
    }

    /// <summary>Change-revision cursor used by <see cref="CatchUpAsync"/>.</summary>
    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    /// <summary>Whether older history can be loaded.</summary>
    public bool HasMoreBefore
    {
        get
        {
            lock (_gate)
            {
                return _hasMoreBefore;
            }
        }
    }

    /// <summary>Whether the conversation and its newest page are loaded and readable.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _isLoaded;
            }
        }
    }

    /// <summary>Conversation metadata, participants and own participation, or null when not loaded or unavailable.</summary>
    public TrustedConversationView? Conversation
    {
        get
        {
            lock (_gate)
            {
                return _conversation;
            }
        }
    }

    /// <summary>Own stored participation, or null when not loaded or unavailable.</summary>
    public TrustedParticipant? OwnParticipant
    {
        get
        {
            lock (_gate)
            {
                return _conversation?.Participant;
            }
        }
    }

    /// <summary>Other participants who can read the conversation as observers (for example a parent).</summary>
    public IReadOnlyList<TrustedParticipant> Observers
    {
        get
        {
            lock (_gate)
            {
                return _observers;
            }
        }
    }

    /// <summary>Whether the conversation is closed.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _conversation?.Conversation.Status == TrustedConversationStatus.Closed;
            }
        }
    }

    /// <summary>Whether the current user only observes.</summary>
    public bool IsObserver
    {
        get
        {
            lock (_gate)
            {
                return _conversation?.Participant.Role == TrustedParticipantRole.Observer;
            }
        }
    }

    /// <summary>Whether the current user may write: loaded, open conversation and writer role.</summary>
    public bool CanWrite
    {
        get
        {
            lock (_gate)
            {
                return CanWriteLocked;
            }
        }
    }

    /// <summary>Engine text bound (UTF-16 code units) learned from the engine options.</summary>
    public int MaxTextLength
    {
        get
        {
            lock (_gate)
            {
                return _maxTextLength;
            }
        }
    }

    /// <summary>Current composer text.</summary>
    public string Draft
    {
        get
        {
            lock (_gate)
            {
                return _draft;
            }
        }
    }

    /// <summary>Message being replied to.</summary>
    public TrustedMessageId? ReplyTarget
    {
        get
        {
            lock (_gate)
            {
                return _replyTo;
            }
        }
    }

    /// <summary>Own message being edited.</summary>
    public TrustedMessageId? EditTarget
    {
        get
        {
            lock (_gate)
            {
                return _editing;
            }
        }
    }

    /// <summary>Whether the composer edits an existing message.</summary>
    public bool IsEditing => EditTarget is not null;

    /// <summary>Whether the composer replies to a message.</summary>
    public bool IsReplying => ReplyTarget is not null;

    /// <summary>Whether a send or edit is in progress.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _isBusy;
            }
        }
    }

    /// <summary>Whether <see cref="SendDraftAsync"/> would submit the draft.</summary>
    public bool CanSend
    {
        get
        {
            lock (_gate)
            {
                return CanSendLocked;
            }
        }
    }

    /// <summary>Uploaded attachments that the next sent message will carry.</summary>
    public IReadOnlyList<TrustedAttachmentInfo> PendingAttachments
    {
        get
        {
            lock (_gate)
            {
                return _pendingAttachmentsSnapshot;
            }
        }
    }

    /// <summary>Host references (for example a lesson or task) that the next sent message will carry.</summary>
    public IReadOnlyList<HostReference> DraftReferences
    {
        get
        {
            lock (_gate)
            {
                return _referencesSnapshot;
            }
        }
    }

    /// <summary>Whether an upload is in progress.</summary>
    public bool IsUploading
    {
        get
        {
            lock (_gate)
            {
                return _uploadsInFlight > 0;
            }
        }
    }

    /// <summary>Whether another attachment may be uploaded for the draft now.</summary>
    public bool CanUpload
    {
        get
        {
            lock (_gate)
            {
                return CanUploadLocked;
            }
        }
    }

    /// <summary>Engine attachment count bound per message, learned from the engine options.</summary>
    public int MaxAttachmentsPerMessage
    {
        get
        {
            lock (_gate)
            {
                return _maxAttachmentsPerMessage;
            }
        }
    }

    /// <summary>Engine attachment size bound in bytes, learned from the engine options.</summary>
    public long MaxAttachmentBytes
    {
        get
        {
            lock (_gate)
            {
                return _maxAttachmentBytes;
            }
        }
    }

    /// <summary>Generic failure of the last operation.</summary>
    public ServerTrustedChatFailure Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    private bool CanSendLocked =>
        CanWriteLocked && !_isBusy && (_editing is not null
            ? ServerTrustedChatValidation.NormalizeText(_draft, _maxTextLength) is not null
            : _uploadsInFlight == 0 && DraftTextLocked(out _));

    private bool CanUploadLocked =>
        CanWriteLocked && _editing is null &&
        _pendingAttachments.Count + _uploadsInFlight < _maxAttachmentsPerMessage;

    private bool CanWriteLocked =>
        _isLoaded && _conversation is { } view &&
        view.Conversation.Status == TrustedConversationStatus.Open &&
        view.Participant.Role == TrustedParticipantRole.Writer;

    /// <summary>Returns a loaded message by identifier.</summary>
    public TrustedMessage? FindMessage(TrustedMessageId messageId)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(messageId);
        }
    }

    /// <summary>Whether the message was authored by the current user.</summary>
    public bool IsOwn(TrustedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.AuthorUserId == UserId;
    }

    /// <summary>Whether the current user reported the message in this session.</summary>
    public bool IsReported(TrustedMessageId messageId)
    {
        lock (_gate)
        {
            return _reported.Contains(messageId);
        }
    }

    /// <summary>Loads the conversation and its newest page, replacing the timeline.</summary>
    public async ValueTask<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>Loads the page before the oldest loaded message. The revision cursor is not changed.</summary>
    public async ValueTask<bool> LoadOlderAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long? before;
            lock (_gate)
            {
                if (!_isLoaded || !_hasMoreBefore)
                {
                    return false;
                }

                before = _timeline.Length == 0 ? null : _timeline[0].Sequence;
            }

            ServerTrustedChatResult<TrustedMessagePage> result;
            try
            {
                result = await _runner.RunAsync(
                    (service, token) => service.GetHistoryAsync(UserId, ConversationId, before, _pageSize, token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
            {
                SetFailure(ServerTrustedChatFailure.Failed);
                return false;
            }

            if (!result.IsSuccess || result.Value is null)
            {
                if (result.Status == ServerTrustedChatStatus.NotFoundOrForbidden)
                {
                    SetUnavailable();
                }
                else
                {
                    SetFailure(ServerTrustedChatFailure.Failed);
                }

                return false;
            }

            lock (_gate)
            {
                foreach (var message in result.Value.Messages)
                {
                    UpsertLocked(message, fromChanges: false);
                }

                _hasMoreBefore = result.Value.HasMoreBefore;
                RebuildLocked();
            }

            RaiseChanged();
            return true;
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// Re-reads conversation metadata and every message changed since the revision cursor. When the state is not loaded
    /// (for example after access was revoked) it attempts a full load instead, so restored access takes effect.
    /// Transient errors keep the current state and return false.
    /// </summary>
    public async ValueTask<bool> CatchUpAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long since;
            lock (_gate)
            {
                if (!_isLoaded)
                {
                    since = -1;
                }
                else
                {
                    since = _revision;
                }
            }

            if (since < 0)
            {
                return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            CatchUpOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync((service, token) => ReadChangesAsync(service, since, token), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
            {
                return false;
            }

            if (!outcome.Authorized)
            {
                SetUnavailable();
                return false;
            }

            if (outcome.Exhausted)
            {
                return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (!_isLoaded)
                {
                    return false;
                }

                SetConversationLocked(outcome.View!);
                foreach (var message in outcome.Messages)
                {
                    UpsertLocked(message, fromChanges: true);
                }

                _revision = Math.Max(_revision, outcome.Cursor);
                RebuildLocked();
            }

            RaiseChanged();
            return true;
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>Replaces the draft, truncated to <see cref="MaxTextLength"/> without splitting a surrogate pair.</summary>
    public void SetDraft(string? text)
    {
        lock (_gate)
        {
            var value = Truncate(text ?? string.Empty, _maxTextLength);
            if (string.Equals(value, _draft, StringComparison.Ordinal))
            {
                return;
            }

            _draft = value;
            if (_editing is null)
            {
                _pendingClientMessageId = null;
            }
        }

        RaiseChanged();
    }

    /// <summary>Starts replying to a visible loaded message.</summary>
    public bool BeginReply(TrustedMessageId messageId)
    {
        lock (_gate)
        {
            if (!CanWriteLocked || _editing is not null || !_byId.TryGetValue(messageId, out var message) || message.IsTombstone)
            {
                return false;
            }

            if (_replyTo != messageId)
            {
                _replyTo = messageId;
                _pendingClientMessageId = null;
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Starts editing an own visible message; the current draft is restored afterwards.</summary>
    public bool BeginEdit(TrustedMessageId messageId)
    {
        lock (_gate)
        {
            if (!CanWriteLocked || !_byId.TryGetValue(messageId, out var message) || message.IsTombstone ||
                message.AuthorUserId != UserId || message.Text is null)
            {
                return false;
            }

            if (_editing is null)
            {
                _stashedDraft = _draft;
                _stashedReplyTo = _replyTo;
                _stashedPendingClientMessageId = _pendingClientMessageId;
                _replyTo = null;
            }

            _editing = messageId;
            _draft = Truncate(message.Text, _maxTextLength);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Cancels editing (restoring the previous draft) or, when not editing, the reply target.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_editing is not null)
            {
                RestoreStashLocked();
            }
            else if (_replyTo is not null)
            {
                _replyTo = null;
                _pendingClientMessageId = null;
            }

            _failure = _failure == ServerTrustedChatFailure.Unavailable ? _failure : ServerTrustedChatFailure.None;
        }

        RaiseChanged();
    }

    /// <summary>Clears a non-terminal failure.</summary>
    public void ClearFailure()
    {
        lock (_gate)
        {
            if (_failure is ServerTrustedChatFailure.None or ServerTrustedChatFailure.Unavailable)
            {
                return;
            }

            _failure = ServerTrustedChatFailure.None;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Sends the draft (or saves the edit when editing). One client message ID is generated per logical send and reused
    /// when the same draft is retried after a failure, so a lost response never creates a second message.
    /// </summary>
    public async ValueTask<bool> SendDraftAsync(CancellationToken cancellationToken = default)
    {
        if (IsEditing)
        {
            return await SaveEditAsync(cancellationToken).ConfigureAwait(false);
        }

        string draft;
        string? text;
        Guid clientMessageId;
        TrustedMessageId? replyTo;
        TrustedAttachmentInfo[] attachments;
        HostReference[] references;
        lock (_gate)
        {
            if (!CanSendLocked || _editing is not null || !DraftTextLocked(out text))
            {
                return false;
            }

            _pendingClientMessageId ??= Guid.CreateVersion7();
            clientMessageId = _pendingClientMessageId.Value;
            draft = _draft;
            replyTo = _replyTo;
            attachments = _pendingAttachmentsSnapshot;
            references = _referencesSnapshot;
            _isBusy = true;
            _failure = ServerTrustedChatFailure.None;
        }

        RaiseChanged();
        var request = new TrustedSendRequest
        {
            ClientMessageId = clientMessageId,
            Text = text,
            ReplyToMessageId = replyTo,
            AttachmentIds = attachments.Select(static item => item.Id).ToArray(),
            References = references,
        };
        ServerTrustedChatResult<TrustedMessage> result;
        try
        {
            result = await _runner.RunAsync(
                (service, token) => service.SendAsync(UserId, ConversationId, request, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            EndBusy(ServerTrustedChatFailure.Failed);
            return false;
        }
        catch
        {
            EndBusy(ServerTrustedChatFailure.None);
            throw;
        }

        if (result.IsSuccess && result.Value is not null)
        {
            lock (_gate)
            {
                _isBusy = false;
                UpsertLocked(result.Value, fromChanges: false);
                RebuildLocked();
                if (string.Equals(_draft, draft, StringComparison.Ordinal))
                {
                    _draft = string.Empty;
                }

                _pendingAttachments.RemoveAll(item => attachments.Contains(item));
                _references.RemoveAll(item => references.Contains(item));
                ReleaseHostReferenceIfGoneLocked();
                RebuildDraftLocked();

                if (_replyTo == replyTo)
                {
                    _replyTo = null;
                }

                if (_pendingClientMessageId == clientMessageId)
                {
                    _pendingClientMessageId = null;
                }
            }

            RaiseChanged();
            return true;
        }

        if (result.Status == ServerTrustedChatStatus.Conflict)
        {
            lock (_gate)
            {
                if (_pendingClientMessageId == clientMessageId)
                {
                    _pendingClientMessageId = null;
                }
            }
        }

        lock (_gate)
        {
            _isBusy = false;
        }

        await HandleWriteFailureAsync(result.Status, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Uploads one attachment for the next message through the engine (host validation, bounded size). Uploads are
    /// rejected while editing or when <see cref="MaxAttachmentsPerMessage"/> would be exceeded.
    /// </summary>
    public async ValueTask<bool> UploadAttachmentAsync(string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            if (!CanUploadLocked)
            {
                if (CanWriteLocked && _editing is null)
                {
                    _failure = ServerTrustedChatFailure.AttachmentRejected;
                }

                return false;
            }

            _uploadsInFlight++;
            _failure = ServerTrustedChatFailure.None;
        }

        RaiseChanged();
        ServerTrustedChatResult<TrustedAttachmentInfo> result;
        try
        {
            result = await _runner.RunAsync(
                (service, token) => service.UploadAttachmentAsync(UserId, ConversationId, fileName ?? string.Empty, contentType ?? string.Empty, content, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            EndUpload(null, ServerTrustedChatFailure.Failed);
            return false;
        }
        catch
        {
            EndUpload(null, ServerTrustedChatFailure.None);
            throw;
        }

        if (result.IsSuccess && result.Value is not null)
        {
            EndUpload(result.Value, ServerTrustedChatFailure.None);
            return true;
        }

        EndUpload(null, ServerTrustedChatFailure.None);
        await HandleWriteFailureAsync(result.Status, cancellationToken, ServerTrustedChatFailure.AttachmentRejected).ConfigureAwait(false);
        return false;
    }

    /// <summary>Records an attachment rejected before upload, for example a file larger than the bound.</summary>
    public void RejectAttachment() => SetFailure(ServerTrustedChatFailure.AttachmentRejected);

    /// <summary>
    /// Removes a pending attachment from the draft. The uploaded blob stays unbound and is deleted by the engine
    /// pending-attachment cleanup.
    /// </summary>
    public bool RemovePendingAttachment(TrustedAttachmentId attachmentId)
    {
        lock (_gate)
        {
            if (_pendingAttachments.RemoveAll(item => item.Id == attachmentId) == 0)
            {
                return false;
            }

            _pendingClientMessageId = null;
            RebuildDraftLocked();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Adds a distinct host reference to the draft (at most <see cref="ServerTrustedChatLimits.MaxHostReferences"/>).</summary>
    public bool AddReference(HostReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate)
        {
            if (_references.Count >= ServerTrustedChatLimits.MaxHostReferences || _references.Contains(reference))
            {
                return false;
            }

            _references.Add(reference);
            DraftReferencesChangedLocked();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Removes a host reference from the draft, including the one placed by <see cref="SetHostReference"/>.</summary>
    public bool RemoveReference(HostReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        lock (_gate)
        {
            if (!_references.Remove(reference))
            {
                return false;
            }

            DraftReferencesChangedLocked();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Removes every host reference from the draft.</summary>
    public void ClearReferences()
    {
        lock (_gate)
        {
            if (_references.Count == 0)
            {
                return;
            }

            _references.Clear();
            DraftReferencesChangedLocked();
        }

        RaiseChanged();
    }

    /// <summary>
    /// The draft reference placed by the host through <see cref="SetHostReference"/>, or null. It becomes null when the
    /// user removes it, when <see cref="ClearReferences"/> runs or when a sent message carried it.
    /// </summary>
    public HostReference? HostReference
    {
        get
        {
            lock (_gate)
            {
                return _hostReference;
            }
        }
    }

    /// <summary>
    /// Replaces the host-placed draft reference (one slot): the reference previously placed through this method leaves
    /// the draft and <paramref name="reference"/> takes its place; null only removes it. References added otherwise
    /// (<see cref="AddReference"/>) are never removed, and a reference already in the draft is adopted without a
    /// duplicate. Draft text, pending attachments, the reply target and an edit in progress are kept; while editing, the
    /// reference belongs to the next new message, never to the edit. Repeating the current reference changes nothing.
    /// </summary>
    /// <returns>
    /// False, with the draft unchanged, when the reference is invalid or the draft already holds
    /// <see cref="ServerTrustedChatLimits.MaxHostReferences"/> other references; the engine authorizes it again on send.
    /// </returns>
    public bool SetHostReference(HostReference? reference)
    {
        if (reference is not null &&
            (!ServerTrustedChatValidation.IsReferenceType(reference.Type) || !ServerTrustedChatValidation.IsReferenceId(reference.Id)))
        {
            return false;
        }

        lock (_gate)
        {
            if (Equals(reference, _hostReference))
            {
                return true;
            }

            var ownedPrevious = _hostReference is not null && _hostReferenceOwned;
            if (reference is not null && !_references.Contains(reference))
            {
                var free = ServerTrustedChatLimits.MaxHostReferences - _references.Count + (ownedPrevious ? 1 : 0);
                if (free <= 0)
                {
                    return false;
                }
            }

            var listChanged = ownedPrevious && _references.Remove(_hostReference!);
            _hostReference = reference;
            _hostReferenceOwned = false;
            if (reference is not null && !_references.Contains(reference))
            {
                _references.Add(reference);
                _hostReferenceOwned = true;
                listChanged = true;
            }

            if (listChanged)
            {
                DraftReferencesChangedLocked();
            }
        }

        RaiseChanged();
        return true;
    }

    // The host slot is either empty or present in the draft: a removed or sent host reference frees it.
    private void ReleaseHostReferenceIfGoneLocked()
    {
        if (_hostReference is not null && !_references.Contains(_hostReference))
        {
            _hostReference = null;
            _hostReferenceOwned = false;
        }
    }

    // A changed reference set is a different logical send: neither the live nor the stashed client message ID may be
    // reused, or a retry would conflict with the fingerprint of the earlier attempt.
    private void DraftReferencesChangedLocked()
    {
        ReleaseHostReferenceIfGoneLocked();
        if (_editing is null)
        {
            _pendingClientMessageId = null;
        }
        else
        {
            _stashedPendingClientMessageId = null;
        }

        RebuildDraftLocked();
    }

    /// <summary>Saves the edited draft to the edit target and restores the previous draft.</summary>
    public async ValueTask<bool> SaveEditAsync(CancellationToken cancellationToken = default)
    {
        string text;
        TrustedMessageId target;
        lock (_gate)
        {
            if (_editing is not { } editing || !CanWriteLocked || _isBusy ||
                ServerTrustedChatValidation.NormalizeText(_draft, _maxTextLength) is null)
            {
                return false;
            }

            target = editing;
            text = _draft;
            _isBusy = true;
            _failure = ServerTrustedChatFailure.None;
        }

        RaiseChanged();
        ServerTrustedChatResult<TrustedMessage> result;
        try
        {
            result = await _runner.RunAsync(
                (service, token) => service.EditAsync(UserId, ConversationId, target, text, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            EndBusy(ServerTrustedChatFailure.Failed);
            return false;
        }
        catch
        {
            EndBusy(ServerTrustedChatFailure.None);
            throw;
        }

        if (result.IsSuccess && result.Value is not null)
        {
            lock (_gate)
            {
                _isBusy = false;
                UpsertLocked(result.Value, fromChanges: false);
                RebuildLocked();
                if (_editing == target)
                {
                    RestoreStashLocked();
                }
            }

            RaiseChanged();
            return true;
        }

        lock (_gate)
        {
            _isBusy = false;
        }

        await HandleWriteFailureAsync(result.Status, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Soft-deletes an own message; participants then see a tombstone.</summary>
    public async ValueTask<bool> DeleteAsync(TrustedMessageId messageId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!CanWriteLocked || !_byId.TryGetValue(messageId, out var message) || message.IsTombstone || message.AuthorUserId != UserId)
            {
                return false;
            }
        }

        ServerTrustedChatResult<TrustedMessage> result;
        try
        {
            result = await _runner.RunAsync(
                (service, token) => service.DeleteAsync(UserId, ConversationId, messageId, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            SetFailure(ServerTrustedChatFailure.Failed);
            return false;
        }

        if (result.IsSuccess && result.Value is not null)
        {
            lock (_gate)
            {
                UpsertLocked(result.Value, fromChanges: false);
                RebuildLocked();
                if (_editing == messageId)
                {
                    RestoreStashLocked();
                }

                if (_replyTo == messageId)
                {
                    _replyTo = null;
                    _pendingClientMessageId = null;
                }
            }

            RaiseChanged();
            return true;
        }

        await HandleWriteFailureAsync(result.Status, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Reports a visible message with a host-defined reason code and an optional bounded comment.</summary>
    public async ValueTask<bool> ReportAsync(TrustedMessageId messageId, string reasonCode, string? comment = null, CancellationToken cancellationToken = default)
    {
        if (!ServerTrustedChatValidation.IsLabel(reasonCode))
        {
            return false;
        }

        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? null : comment;
        lock (_gate)
        {
            if (!_isLoaded || !_byId.TryGetValue(messageId, out var message) || message.IsTombstone)
            {
                return false;
            }
        }

        ServerTrustedChatResult<TrustedReport> result;
        try
        {
            result = await _runner.RunAsync(
                (service, token) => service.ReportAsync(UserId, messageId, reasonCode, normalizedComment, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            SetFailure(ServerTrustedChatFailure.Failed);
            return false;
        }

        if (result.IsSuccess)
        {
            lock (_gate)
            {
                _reported.Add(messageId);
                _failure = ServerTrustedChatFailure.None;
            }

            RaiseChanged();
            return true;
        }

        await HandleWriteFailureAsync(result.Status, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Advances the read marker through the newest loaded message when it is beyond the known read position. Call it
    /// only while the conversation is actually visible to the user.
    /// </summary>
    public async ValueTask<bool> MarkReadThroughLatestAsync(CancellationToken cancellationToken = default)
    {
        long latest;
        lock (_gate)
        {
            if (!_isLoaded || _timeline.Length == 0)
            {
                return false;
            }

            latest = _timeline[^1].Sequence;
            if (latest <= _readThrough)
            {
                return false;
            }
        }

        ServerTrustedChatResult<long> result;
        try
        {
            result = await _runner.RunAsync(
                (service, token) => service.MarkReadAsync(UserId, ConversationId, latest, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            return false;
        }

        if (result.IsSuccess)
        {
            lock (_gate)
            {
                _readThrough = Math.Max(_readThrough, result.Value);
            }

            return true;
        }

        if (result.Status == ServerTrustedChatStatus.NotFoundOrForbidden)
        {
            await VerifyAccessAsync(ServerTrustedChatFailure.None, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    internal ServerTrustedChatSnapshot? CreateSnapshot()
    {
        lock (_gate)
        {
            return _isLoaded && _conversation is not null
                ? new ServerTrustedChatSnapshot(_conversation, _timeline, _hasMoreBefore, _revision, _maxTextLength, _maxAttachmentsPerMessage, _maxAttachmentBytes)
                : null;
        }
    }

    internal bool TryRestore(ServerTrustedChatSnapshot? snapshot)
    {
        if (snapshot?.Conversation?.Conversation is null || snapshot.Conversation.Participant is null ||
            snapshot.Conversation.Participants is null || snapshot.Messages is null ||
            snapshot.Conversation.Conversation.Id != ConversationId || snapshot.Conversation.Participant.UserId != UserId ||
            snapshot.MaxTextLength is < 1 or > ServerTrustedChatLimits.MaxTextLengthCeiling || snapshot.Revision < 0 ||
            snapshot.MaxAttachmentsPerMessage is < 0 or > ServerTrustedChatLimits.MaxAttachmentsPerMessageCeiling ||
            snapshot.MaxAttachmentBytes is < 1 or > ServerTrustedChatLimits.MaxAttachmentBytesCeiling ||
            snapshot.Messages.Any(item => item is null || item.ConversationId != ConversationId))
        {
            return false;
        }

        lock (_gate)
        {
            _byId.Clear();
            foreach (var message in snapshot.Messages)
            {
                UpsertLocked(message, fromChanges: false);
            }

            _hasMoreBefore = snapshot.HasMoreBefore;
            _revision = snapshot.Revision;
            _maxTextLength = snapshot.MaxTextLength;
            _maxAttachmentsPerMessage = snapshot.MaxAttachmentsPerMessage;
            _maxAttachmentBytes = snapshot.MaxAttachmentBytes;
            _isLoaded = true;
            SetConversationLocked(snapshot.Conversation);
            _failure = ServerTrustedChatFailure.None;
            RebuildLocked();
        }

        RaiseChanged();
        return true;
    }

    private async ValueTask<bool> LoadCoreAsync(CancellationToken cancellationToken)
    {
        LoadOutcome loaded;
        try
        {
            loaded = await _runner.RunAsync(async (service, token) =>
            {
                var view = await service.GetConversationAsync(UserId, ConversationId, token).ConfigureAwait(false);
                var page = view.IsSuccess
                    ? await service.GetHistoryAsync(UserId, ConversationId, null, _pageSize, token).ConfigureAwait(false)
                    : default;
                return new LoadOutcome(view, page, service.Options.MaxTextLength, service.Options.MaxAttachmentsPerMessage, service.Options.MaxAttachmentBytes);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            SetFailure(ServerTrustedChatFailure.Failed);
            return false;
        }

        if (!loaded.View.IsSuccess || loaded.View.Value is null || !loaded.Page.IsSuccess || loaded.Page.Value is null)
        {
            SetUnavailable();
            return false;
        }

        lock (_gate)
        {
            _byId.Clear();
            foreach (var message in loaded.Page.Value.Messages)
            {
                UpsertLocked(message, fromChanges: false);
            }

            _hasMoreBefore = loaded.Page.Value.HasMoreBefore;
            _revision = loaded.Page.Value.ChangeRevision;
            _maxTextLength = loaded.MaxTextLength;
            _maxAttachmentsPerMessage = loaded.MaxAttachmentsPerMessage;
            _maxAttachmentBytes = loaded.MaxAttachmentBytes;
            _draft = Truncate(_draft, _maxTextLength);
            _isLoaded = true;
            SetConversationLocked(loaded.View.Value, replaceReadPosition: true);
            _failure = ServerTrustedChatFailure.None;
            RebuildLocked();
        }

        RaiseChanged();
        return true;
    }

    private async ValueTask<CatchUpOutcome> ReadChangesAsync(ServerTrustedChatService service, long since, CancellationToken cancellationToken)
    {
        var view = await service.GetConversationAsync(UserId, ConversationId, cancellationToken).ConfigureAwait(false);
        if (!view.IsSuccess || view.Value is null)
        {
            return new CatchUpOutcome(false, null, [], since, false);
        }

        var messages = new List<TrustedMessage>();
        var cursor = since;
        for (var index = 0; index < MaxCatchUpPages; index++)
        {
            var page = await service.GetChangesAsync(UserId, ConversationId, cursor, ChangesPageSize, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess || page.Value is null)
            {
                return new CatchUpOutcome(page.Status != ServerTrustedChatStatus.NotFoundOrForbidden, view.Value, [], since, true);
            }

            messages.AddRange(page.Value.Messages);
            cursor = Math.Max(cursor, page.Value.ChangeRevision);
            if (!page.Value.HasMoreBefore)
            {
                return new CatchUpOutcome(true, view.Value, messages, cursor, false);
            }
        }

        return new CatchUpOutcome(true, view.Value, [], since, true);
    }

    private async ValueTask HandleWriteFailureAsync(
        ServerTrustedChatStatus status,
        CancellationToken cancellationToken,
        ServerTrustedChatFailure rejectedFailure = ServerTrustedChatFailure.Failed)
    {
        switch (status)
        {
            case ServerTrustedChatStatus.NotFoundOrForbidden:
                await VerifyAccessAsync(rejectedFailure, cancellationToken).ConfigureAwait(false);
                break;
            case ServerTrustedChatStatus.Invalid:
                SetFailure(rejectedFailure);
                break;
            case ServerTrustedChatStatus.Closed:
                await VerifyAccessAsync(ServerTrustedChatFailure.Closed, cancellationToken).ConfigureAwait(false);
                break;
            case ServerTrustedChatStatus.RateLimited:
                SetFailure(ServerTrustedChatFailure.RateLimited);
                break;
            default:
                SetFailure(ServerTrustedChatFailure.Failed);
                break;
        }
    }

    // A write rejection may only concern the message (deleted meanwhile, not own); confirm with a read before clearing.
    private async ValueTask VerifyAccessAsync(ServerTrustedChatFailure failureWhenReadable, CancellationToken cancellationToken)
    {
        ServerTrustedChatResult<TrustedConversationView> view;
        try
        {
            view = await _runner.RunAsync(
                (service, token) => service.GetConversationAsync(UserId, ConversationId, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception, cancellationToken))
        {
            SetFailure(ServerTrustedChatFailure.Failed);
            return;
        }

        if (!view.IsSuccess || view.Value is null)
        {
            SetUnavailable();
            return;
        }

        lock (_gate)
        {
            if (_isLoaded)
            {
                SetConversationLocked(view.Value);
            }

            if (failureWhenReadable != ServerTrustedChatFailure.None)
            {
                _failure = failureWhenReadable;
            }
        }

        RaiseChanged();
    }

    private void SetUnavailable()
    {
        lock (_gate)
        {
            _byId.Clear();
            _timeline = [];
            _conversation = null;
            _observers = [];
            _hasMoreBefore = false;
            _isLoaded = false;
            _replyTo = null;
            _editing = null;
            _stashedReplyTo = null;
            _pendingClientMessageId = null;
            _pendingAttachments.Clear();
            RebuildDraftLocked();
            _failure = ServerTrustedChatFailure.Unavailable;
        }

        RaiseChanged();
    }

    private void SetFailure(ServerTrustedChatFailure failure)
    {
        lock (_gate)
        {
            if (_failure == ServerTrustedChatFailure.Unavailable && !_isLoaded)
            {
                return;
            }

            _failure = failure;
        }

        RaiseChanged();
    }

    private void EndBusy(ServerTrustedChatFailure failure)
    {
        lock (_gate)
        {
            _isBusy = false;
            if (failure != ServerTrustedChatFailure.None)
            {
                _failure = failure;
            }
        }

        RaiseChanged();
    }

    private void SetConversationLocked(TrustedConversationView view, bool replaceReadPosition = false)
    {
        _conversation = view;
        _observers = view.Participants
            .Where(item => item.Role == TrustedParticipantRole.Observer && item.CanRead && item.UserId != UserId)
            .ToArray();
        _readThrough = replaceReadPosition
            ? view.Participant.ReadThroughSequence
            : Math.Max(_readThrough, view.Participant.ReadThroughSequence);
        if (!CanWriteLocked)
        {
            if (_editing is not null)
            {
                RestoreStashLocked();
            }

            _replyTo = null;
            _pendingClientMessageId = null;
        }
    }

    private void UpsertLocked(TrustedMessage message, bool fromChanges)
    {
        if (message.ConversationId != ConversationId)
        {
            return;
        }

        if (_byId.TryGetValue(message.Id, out var existing))
        {
            if (message.ChangeRevision >= existing.ChangeRevision)
            {
                _byId[message.Id] = message;
            }

            return;
        }

        // A change to a message older than the loaded window would leave a gap that LoadOlderAsync skips.
        if (fromChanges && _hasMoreBefore && _timeline.Length > 0 && message.Sequence < _timeline[0].Sequence)
        {
            return;
        }

        _byId[message.Id] = message;
    }

    private void RebuildLocked() =>
        _timeline = _byId.Values.OrderBy(static item => item.Sequence).ThenBy(static item => item.Id.Value).ToArray();

    private void RestoreStashLocked()
    {
        _editing = null;
        _draft = Truncate(_stashedDraft, _maxTextLength);
        _replyTo = _stashedReplyTo is { } reply && _byId.TryGetValue(reply, out var target) && !target.IsTombstone ? reply : null;
        _pendingClientMessageId = _replyTo == _stashedReplyTo ? _stashedPendingClientMessageId : null;
        _stashedDraft = string.Empty;
        _stashedReplyTo = null;
        _stashedPendingClientMessageId = null;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // Text is optional when attachments are present; a non-blank draft must still be valid text.
    private bool DraftTextLocked(out string? text)
    {
        if (string.IsNullOrWhiteSpace(_draft))
        {
            text = null;
            return _pendingAttachments.Count > 0;
        }

        text = _draft;
        return ServerTrustedChatValidation.NormalizeText(_draft, _maxTextLength) is not null;
    }

    private void RebuildDraftLocked()
    {
        _pendingAttachmentsSnapshot = [.. _pendingAttachments];
        _referencesSnapshot = [.. _references];
    }

    private void EndUpload(TrustedAttachmentInfo? uploaded, ServerTrustedChatFailure failure)
    {
        lock (_gate)
        {
            _uploadsInFlight = Math.Max(0, _uploadsInFlight - 1);
            if (uploaded is not null && _isLoaded && _pendingAttachments.Count < _maxAttachmentsPerMessage &&
                !_pendingAttachments.Any(item => item.Id == uploaded.Id))
            {
                _pendingAttachments.Add(uploaded);
                _pendingClientMessageId = null;
                RebuildDraftLocked();
            }

            if (failure != ServerTrustedChatFailure.None)
            {
                _failure = failure;
            }
        }

        RaiseChanged();
    }

    private static bool IsOperationFailure(Exception exception, CancellationToken cancellationToken) =>
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    private readonly record struct LoadOutcome(
        ServerTrustedChatResult<TrustedConversationView> View,
        ServerTrustedChatResult<TrustedMessagePage> Page,
        int MaxTextLength,
        int MaxAttachmentsPerMessage,
        long MaxAttachmentBytes);

    private sealed record CatchUpOutcome(
        bool Authorized,
        TrustedConversationView? View,
        IReadOnlyList<TrustedMessage> Messages,
        long Cursor,
        bool Exhausted);
}

/// <summary>Minimal prerender state: what the first interactive render needs to avoid a second query.</summary>
internal sealed record ServerTrustedChatSnapshot(
    TrustedConversationView Conversation,
    IReadOnlyList<TrustedMessage> Messages,
    bool HasMoreBefore,
    long Revision,
    int MaxTextLength,
    int MaxAttachmentsPerMessage,
    long MaxAttachmentBytes);
