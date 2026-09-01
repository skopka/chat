using System.Text;
using System.Security.Cryptography;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.UI;

/// <summary>
/// Thread-safe presentation state for one conversation without a dependency on a UI framework.
/// </summary>
public sealed class ChatViewModel
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly ChatConversationProjection _projection;
    private readonly IChatContentSender _sender;
    private IReadOnlyList<ProjectedChatMessage> _messages = Array.Empty<ProjectedChatMessage>();
    private IReadOnlyList<IProjectedChatItem> _timeline = Array.Empty<IProjectedChatItem>();
    private string _draftText = string.Empty;
    private ChatContentId? _replyToContentId;
    private ChatContentId? _editTargetContentId;
    private string? _draftBeforeEdit;
    private ChatContentId? _replyBeforeEdit;
    private long _draftRevision;
    private bool _isSendingDraft;
    private bool _hasCommandError;

    /// <summary>Creates presentation state for exactly one conversation and authenticated user.</summary>
    public ChatViewModel(
        ConversationId conversationId,
        UserId currentUserId,
        IChatContentSender sender)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (currentUserId.Value == Guid.Empty)
        {
            throw new ArgumentException("Current user ID must not be empty.", nameof(currentUserId));
        }

        ConversationId = conversationId;
        CurrentUserId = currentUserId;
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _projection = new ChatConversationProjection(conversationId);
    }

    /// <summary>Raised after observable presentation state changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Conversation represented by this instance.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Authenticated user used to identify outgoing messages and active reactions.</summary>
    public UserId CurrentUserId { get; }

    /// <summary>Immutable current projection ordered by authenticated sender time and content ID.</summary>
    public IReadOnlyList<ProjectedChatMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages;
            }
        }
    }

    /// <summary>Immutable text-and-attachment timeline in deterministic order.</summary>
    public IReadOnlyList<IProjectedChatItem> Timeline
    {
        get
        {
            lock (_gate)
            {
                return _timeline;
            }
        }
    }

    /// <summary>Current plaintext composer draft. Hosts must treat it as sensitive local data.</summary>
    public string DraftText
    {
        get
        {
            lock (_gate)
            {
                return _draftText;
            }
        }
    }

    /// <summary>Message currently selected as the reply target, if it is still projected.</summary>
    public ProjectedChatMessage? ReplyTarget
    {
        get
        {
            lock (_gate)
            {
                return FindMessage(_replyToContentId);
            }
        }
    }

    /// <summary>Text or attachment currently selected as the reply target.</summary>
    public IProjectedChatItem? ReplyTargetItem
    {
        get
        {
            lock (_gate)
            {
                return FindItem(_replyToContentId);
            }
        }
    }

    /// <summary>Text message currently being edited, if any.</summary>
    public ProjectedChatMessage? EditTarget
    {
        get
        {
            lock (_gate)
            {
                return FindMessage(_editTargetContentId);
            }
        }
    }

    /// <summary>Text or attachment whose user-visible text is currently being edited.</summary>
    public IProjectedChatItem? EditTargetItem
    {
        get
        {
            lock (_gate)
            {
                return FindItem(_editTargetContentId);
            }
        }
    }

    /// <summary>Whether the composer is editing existing content.</summary>
    public bool IsEditing
    {
        get
        {
            lock (_gate)
            {
                return _editTargetContentId.HasValue;
            }
        }
    }

    /// <summary>Whether the composer currently has a send operation in flight.</summary>
    public bool IsSendingDraft
    {
        get
        {
            lock (_gate)
            {
                return _isSendingDraft;
            }
        }
    }

    /// <summary>
    /// Whether the last command failed. The exception text is deliberately not retained.
    /// </summary>
    public bool HasCommandError
    {
        get
        {
            lock (_gate)
            {
                return _hasCommandError;
            }
        }
    }

    /// <summary>Whether the current bounded draft can be sent.</summary>
    public bool CanSendDraft
    {
        get
        {
            lock (_gate)
            {
                return CanSendDraftCore();
            }
        }
    }

    /// <summary>Applies already authenticated incoming or restored content.</summary>
    public ChatProjectionApplyResult Apply(ReceivedChatContent delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ChatProjectionApplyResult result;
        lock (_gate)
        {
            result = _projection.Apply(delivery);
            if (result is not ChatProjectionApplyResult.Duplicate)
            {
                RefreshItems();
            }
        }

        if (result is not ChatProjectionApplyResult.Duplicate)
        {
            OnStateChanged();
        }

        return result;
    }

    /// <summary>Replaces the bounded plaintext draft without sending it.</summary>
    public void SetDraftText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool changed;
        lock (_gate)
        {
            ValidateDraft(value, FindItem(_editTargetContentId));
            changed = !string.Equals(_draftText, value, StringComparison.Ordinal);
            if (changed)
            {
                _draftText = value;
                _draftRevision++;
                _hasCommandError = false;
            }
        }

        if (changed)
        {
            OnStateChanged();
        }
    }

    /// <summary>Selects an existing projected message as the reply target.</summary>
    public void BeginReply(ChatContentId contentId)
    {
        if (contentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Content ID must not be empty.", nameof(contentId));
        }

        bool changed;
        lock (_gate)
        {
            if (FindItem(contentId) is null)
            {
                throw new ArgumentException("Reply target is not present in this conversation.", nameof(contentId));
            }

            changed = _replyToContentId != contentId || _editTargetContentId.HasValue;
            if (changed)
            {
                RestoreComposerAfterEditCore();
                _replyToContentId = contentId;
                _draftRevision++;
                _hasCommandError = false;
            }
        }

        if (changed)
        {
            OnStateChanged();
        }
    }

    /// <summary>
    /// Loads an own projected text body or attachment caption into the composer for editing.
    /// The previous unsent draft and reply target are restored after save or cancel.
    /// </summary>
    public void BeginEdit(ChatContentId contentId)
    {
        if (contentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Content ID must not be empty.", nameof(contentId));
        }

        bool changed;
        lock (_gate)
        {
            if (_isSendingDraft)
            {
                throw new InvalidOperationException("The composer cannot change mode while a send is in progress.");
            }

            var target = FindItem(contentId)
                ?? throw new ArgumentException("Edit target is not present in this conversation.", nameof(contentId));
            if (target.SenderUserId != CurrentUserId)
            {
                throw new ArgumentException("Only own content can be edited.", nameof(contentId));
            }

            var editableValue = target switch
            {
                ProjectedChatMessage message => message.Text,
                ProjectedChatAttachment attachment => attachment.Caption ?? string.Empty,
                _ => throw new ArgumentException("Content type cannot be edited.", nameof(contentId)),
            };
            changed = _editTargetContentId != contentId;
            if (changed)
            {
                if (!_editTargetContentId.HasValue)
                {
                    _draftBeforeEdit = _draftText;
                    _replyBeforeEdit = _replyToContentId;
                }

                _editTargetContentId = contentId;
                _draftText = editableValue;
                _replyToContentId = null;
                _draftRevision++;
                _hasCommandError = false;
            }
        }

        if (changed)
        {
            OnStateChanged();
        }
    }

    /// <summary>Leaves edit mode and restores the draft/reply state that preceded it.</summary>
    public void CancelEdit()
    {
        bool changed;
        lock (_gate)
        {
            changed = _editTargetContentId.HasValue;
            if (changed)
            {
                RestoreComposerAfterEditCore();
                _draftRevision++;
                _hasCommandError = false;
            }
        }

        if (changed)
        {
            OnStateChanged();
        }
    }

    /// <summary>Clears the current reply target.</summary>
    public void CancelReply()
    {
        bool changed;
        lock (_gate)
        {
            changed = _replyToContentId is not null;
            if (changed)
            {
                _replyToContentId = null;
                _draftRevision++;
            }
        }

        if (changed)
        {
            OnStateChanged();
        }
    }

    /// <summary>Clears only the generic command-failure marker.</summary>
    public void ClearCommandError()
    {
        bool changed;
        lock (_gate)
        {
            changed = _hasCommandError;
            _hasCommandError = false;
        }

        if (changed)
        {
            OnStateChanged();
        }
    }

    /// <summary>
    /// Sends the current draft or an edit event. Returns false without calling the host when no valid change exists.
    /// </summary>
    public async ValueTask<bool> TrySendDraftAsync(CancellationToken cancellationToken = default)
    {
        ChatContent content;
        long revision;
        lock (_gate)
        {
            if (_isSendingDraft)
            {
                throw new InvalidOperationException("A draft send is already in progress.");
            }

            if (!CanSendDraftCore())
            {
                return false;
            }

            content = CreateComposerContent();
            revision = _draftRevision;
            _isSendingDraft = true;
            _hasCommandError = false;
        }

        OnStateChanged();
        ReceivedChatContent? delivery;
        var commandReturned = false;
        try
        {
            delivery = await SendAndValidateAsync(ConversationId, content, cancellationToken).ConfigureAwait(false);
            commandReturned = true;
        }
        finally
        {
            if (!commandReturned)
            {
                SetDraftSendFailed(hasError: false);
            }
        }

        if (delivery is null)
        {
            SetDraftSendFailed(hasError: true);
            return false;
        }

        Apply(delivery);
        lock (_gate)
        {
            if (_draftRevision == revision)
            {
                if (content is ChatEditContent)
                {
                    RestoreComposerAfterEditCore();
                }
                else
                {
                    _draftText = string.Empty;
                    _replyToContentId = null;
                }

                _draftRevision++;
            }

            _isSendingDraft = false;
        }

        OnStateChanged();
        return true;
    }

    /// <summary>Toggles one reaction for the authenticated current user.</summary>
    public async ValueTask<bool> ToggleReactionAsync(
        ChatContentId targetContentId,
        string reaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);
        ChatReactionOperation operation;
        lock (_gate)
        {
            var message = FindItem(targetContentId)
                ?? throw new ArgumentException("Reaction target is not present in this conversation.", nameof(targetContentId));
            var active = message.Reactions.Any(item =>
                string.Equals(item.Reaction, reaction, StringComparison.Ordinal) &&
                item.SenderUserIds.Contains(CurrentUserId));
            operation = active ? ChatReactionOperation.Remove : ChatReactionOperation.Add;
            _hasCommandError = false;
        }

        var content = new ChatReactionContent(ChatContentId.New(), targetContentId, reaction, operation);
        var delivery = await SendAndValidateAsync(ConversationId, content, cancellationToken).ConfigureAwait(false);
        if (delivery is null)
        {
            SetCommandError();
            return false;
        }

        Apply(delivery);
        return true;
    }

    /// <summary>Sends an already encrypted-and-uploaded attachment manifest and applies its local echo.</summary>
    public async ValueTask<bool> SendAttachmentAsync(
        ChatAttachmentContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            _hasCommandError = false;
        }

        var delivery = await SendAndValidateAsync(ConversationId, content, cancellationToken).ConfigureAwait(false);
        if (delivery is null)
        {
            SetCommandError();
            return false;
        }

        Apply(delivery);
        return true;
    }

    /// <summary>
    /// Copies one projected message into another conversation without source attribution.
    /// </summary>
    public async ValueTask<bool> ForwardAsync(
        ChatContentId sourceContentId,
        ConversationId targetConversationId,
        CancellationToken cancellationToken = default)
    {
        if (targetConversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Target conversation ID must not be empty.", nameof(targetConversationId));
        }

        ChatTextContent content;
        lock (_gate)
        {
            var source = FindMessage(sourceContentId)
                ?? throw new ArgumentException("Forward source is not present in this conversation.", nameof(sourceContentId));
            content = new ChatTextContent(ChatContentId.New(), source.Text, isForwarded: true);
            _hasCommandError = false;
        }

        var delivery = await SendAndValidateAsync(targetConversationId, content, cancellationToken).ConfigureAwait(false);
        if (delivery is null)
        {
            SetCommandError();
            return false;
        }

        if (targetConversationId == ConversationId)
        {
            Apply(delivery);
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatViewModel(ConversationId={ConversationId}, Messages={Messages.Count}, Draft=[REDACTED])";

    private async ValueTask<ReceivedChatContent?> SendAndValidateAsync(
        ConversationId conversationId,
        ChatContent content,
        CancellationToken cancellationToken)
    {
        var result = await _sender.SendAsync(conversationId, content, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The content sender returned an invalid result.");
        if (!result.Succeeded)
        {
            if (result.Delivery is not null)
            {
                throw new InvalidOperationException("The content sender returned an invalid result.");
            }

            return null;
        }

        var delivery = result.Delivery;
        if (delivery is null ||
            delivery.ConversationId != conversationId ||
            delivery.SenderUserId != CurrentUserId ||
            !IsSameContent(delivery.Content, content))
        {
            throw new InvalidOperationException("The content sender returned an invalid local echo.");
        }

        return delivery;
    }

    private static bool IsSameContent(ChatContent left, ChatContent right)
    {
        if (left.ContentId != right.ContentId || left.Kind != right.Kind)
        {
            return false;
        }

        return (left, right) switch
        {
            (ChatTextContent first, ChatTextContent second) =>
                first.Text == second.Text &&
                first.ReplyToContentId == second.ReplyToContentId &&
                first.IsForwarded == second.IsForwarded,
            (ChatReactionContent first, ChatReactionContent second) =>
                first.TargetContentId == second.TargetContentId &&
                first.Reaction == second.Reaction &&
                first.Operation == second.Operation,
            (ChatEditContent first, ChatEditContent second) =>
                first.TargetContentId == second.TargetContentId &&
                first.Field == second.Field &&
                first.NewValue == second.NewValue,
            (ChatAttachmentContent first, ChatAttachmentContent second) =>
                first.AttachmentId == second.AttachmentId &&
                first.FileName == second.FileName &&
                first.MediaType == second.MediaType &&
                first.PlaintextLength == second.PlaintextLength &&
                first.CiphertextLength == second.CiphertextLength &&
                first.ChunkPlaintextBytes == second.ChunkPlaintextBytes &&
                first.Caption == second.Caption &&
                first.ReplyToContentId == second.ReplyToContentId &&
                CryptographicOperations.FixedTimeEquals(first.CiphertextSha256.Span, second.CiphertextSha256.Span) &&
                CryptographicOperations.FixedTimeEquals(first.FileKey.Span, second.FileKey.Span) &&
                CryptographicOperations.FixedTimeEquals(first.NoncePrefix.Span, second.NoncePrefix.Span),
            _ => false,
        };
    }

    private void SetDraftSendFailed(bool hasError)
    {
        lock (_gate)
        {
            _isSendingDraft = false;
            _hasCommandError = hasError;
        }

        OnStateChanged();
    }

    private void SetCommandError()
    {
        lock (_gate)
        {
            _hasCommandError = true;
        }

        OnStateChanged();
    }

    private void RefreshItems()
    {
        _messages = _projection.Snapshot();
        _timeline = _projection.SnapshotTimeline();
        if (_replyToContentId is not null && FindItem(_replyToContentId) is null)
        {
            _replyToContentId = null;
            _draftRevision++;
        }

        if (_editTargetContentId is not null && FindItem(_editTargetContentId) is null)
        {
            RestoreComposerAfterEditCore();
            _draftRevision++;
        }
    }

    private ProjectedChatMessage? FindMessage(ChatContentId? contentId) =>
        contentId is null
            ? null
            : _messages.FirstOrDefault(item => item.ContentId == contentId.Value);

    private IProjectedChatItem? FindItem(ChatContentId? contentId) =>
        contentId is null
            ? null
            : _timeline.FirstOrDefault(item => item.ContentId == contentId.Value);

    private bool CanSendDraftCore()
    {
        if (_isSendingDraft)
        {
            return false;
        }

        return FindItem(_editTargetContentId) switch
        {
            ProjectedChatMessage message =>
                !string.IsNullOrWhiteSpace(_draftText) &&
                !string.Equals(_draftText, message.Text, StringComparison.Ordinal),
            ProjectedChatAttachment attachment =>
                !string.Equals(NormalizeCaption(_draftText), attachment.Caption, StringComparison.Ordinal),
            _ => !string.IsNullOrWhiteSpace(_draftText),
        };
    }

    private ChatContent CreateComposerContent() => FindItem(_editTargetContentId) switch
    {
        ProjectedChatMessage message => new ChatEditContent(
            ChatContentId.New(),
            message.ContentId,
            ChatEditField.Text,
            _draftText),
        ProjectedChatAttachment attachment => new ChatEditContent(
            ChatContentId.New(),
            attachment.ContentId,
            ChatEditField.AttachmentCaption,
            NormalizeCaption(_draftText)),
        _ => new ChatTextContent(ChatContentId.New(), _draftText, _replyToContentId),
    };

    private void RestoreComposerAfterEditCore()
    {
        if (!_editTargetContentId.HasValue)
        {
            return;
        }

        _draftText = _draftBeforeEdit ?? string.Empty;
        _replyToContentId = _replyBeforeEdit is { } reply && FindItem(reply) is not null ? reply : null;
        _editTargetContentId = null;
        _draftBeforeEdit = null;
        _replyBeforeEdit = null;
    }

    private static string? NormalizeCaption(string value) => value.Length == 0 ? null : value;

    private static void ValidateDraft(string value, IProjectedChatItem? editTarget)
    {
        int utf8Length;
        try
        {
            utf8Length = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Draft must contain valid Unicode.", nameof(value));
        }

        var maximumBytes = editTarget switch
        {
            ProjectedChatMessage => ChatContentLimits.MaxEditTextUtf8Bytes,
            ProjectedChatAttachment => ChatContentLimits.MaxAttachmentCaptionUtf8Bytes,
            _ => ChatContentLimits.MaxTextUtf8Bytes,
        };
        if (utf8Length > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Draft exceeds the encrypted text limit.");
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
