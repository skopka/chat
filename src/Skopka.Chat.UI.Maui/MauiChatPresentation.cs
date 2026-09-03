using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.UI.Maui;

/// <summary>One reaction choice bound to an AOT-safe command without relative-source reflection.</summary>
public sealed record MauiReactionChoice(string Text, ICommand Command);

/// <summary>Stable mutable presentation wrapper keyed by logical content ID.</summary>
public sealed class MauiChatTimelineItem : INotifyPropertyChanged
{
    private IProjectedChatItem _item;
    private readonly UserId _currentUserId;
    private readonly Func<ChatContentId, ValueTask> _reply;
    private readonly Func<IProjectedChatItem, ValueTask> _forward;
    private readonly Func<ChatContentId, ValueTask> _edit;
    private readonly Func<ProjectedChatAttachment, ValueTask> _download;
    private readonly Func<ChatContentId, string, ValueTask> _reaction;
    private readonly Action _commandFailed;
    private IReadOnlyList<MauiReactionChoice> _reactionChoices = [];
    private string? _replyPreview;

    internal MauiChatTimelineItem(
        IProjectedChatItem item,
        UserId currentUserId,
        Func<ChatContentId, ValueTask> reply,
        Func<IProjectedChatItem, ValueTask> forward,
        Func<ChatContentId, ValueTask> edit,
        Func<ProjectedChatAttachment, ValueTask> download,
        Func<ChatContentId, string, ValueTask> reaction,
        Action commandFailed)
    {
        _item = item;
        _currentUserId = currentUserId;
        _reply = reply;
        _forward = forward;
        _edit = edit;
        _download = download;
        _reaction = reaction;
        _commandFailed = commandFailed;
        ReplyCommand = new SafeAsyncCommand(() => _reply(ContentId), _commandFailed);
        ForwardCommand = new SafeAsyncCommand(() => _forward(_item), _commandFailed);
        EditCommand = new SafeAsyncCommand(() => _edit(ContentId), _commandFailed);
        DownloadCommand = new SafeAsyncCommand(() =>
            _item is ProjectedChatAttachment attachment ? _download(attachment) : ValueTask.CompletedTask,
            _commandFailed);
    }

    /// <summary>Stable logical ID used by the MAUI diff.</summary>
    public ChatContentId ContentId => _item.ContentId;

    /// <summary>Whether the current user authored the item.</summary>
    public bool IsOwn => _item.SenderUserId == _currentUserId;
    /// <summary>Whether the item contains text.</summary>
    public bool IsText => _item is ProjectedChatMessage;
    /// <summary>Whether the item contains an attachment manifest.</summary>
    public bool IsAttachment => _item is ProjectedChatAttachment;
    /// <summary>Whether edit controls may be shown.</summary>
    public bool CanEdit => IsOwn && (_item is ProjectedChatMessage or ProjectedChatAttachment);
    /// <summary>Localized sender label.</summary>
    public string SenderLabel { get; private set; } = string.Empty;
    /// <summary>Plaintext message body for native text rendering.</summary>
    public string Text => (_item as ProjectedChatMessage)?.Text ?? string.Empty;
    /// <summary>Authenticated attachment filename.</summary>
    public string FileName => (_item as ProjectedChatAttachment)?.FileName ?? string.Empty;
    /// <summary>Authenticated attachment media type.</summary>
    public string MediaType => (_item as ProjectedChatAttachment)?.MediaType ?? string.Empty;
    /// <summary>Authenticated attachment caption.</summary>
    public string Caption => (_item as ProjectedChatAttachment)?.Caption ?? string.Empty;
    /// <summary>Whether a non-empty caption is present.</summary>
    public bool HasCaption => !string.IsNullOrEmpty(Caption);
    /// <summary>Whether forward presentation was requested by the sender.</summary>
    public bool IsForwarded => (_item as ProjectedChatMessage)?.IsForwarded == true;
    /// <summary>Whether a valid edit has been applied.</summary>
    public bool IsEdited => _item.IsEdited;
    /// <summary>Culture-aware local send-time label.</summary>
    public string SentAtLabel => _item.SentAt.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
    /// <summary>Bounded aggregate reaction label.</summary>
    public string ReactionSummary => string.Join(
        "  ",
        _item.Reactions.Select(reaction => $"{reaction.Reaction} {reaction.SenderUserIds.Count}"));
    /// <summary>Whether reactions are present.</summary>
    public bool HasReactions => _item.Reactions.Count > 0;
    /// <summary>Reply preview resolved within the current bounded snapshot.</summary>
    public string? ReplyPreview => _replyPreview;
    /// <summary>Whether a reply preview is available.</summary>
    public bool HasReplyPreview => _replyPreview is not null;
    /// <summary>Localized forward marker.</summary>
    public string ForwardedLabel { get; private set; } = string.Empty;
    /// <summary>Localized reply action.</summary>
    public string ReplyLabel { get; private set; } = string.Empty;
    /// <summary>Localized forward action.</summary>
    public string ForwardLabel { get; private set; } = string.Empty;
    /// <summary>Localized edit action.</summary>
    public string EditLabel { get; private set; } = string.Empty;
    /// <summary>Localized edited marker.</summary>
    public string EditedLabel { get; private set; } = string.Empty;
    /// <summary>Localized attachment action.</summary>
    public string DownloadLabel { get; private set; } = string.Empty;
    /// <summary>Native accessibility description containing no transport details.</summary>
    public string SemanticDescription { get; private set; } = string.Empty;
    /// <summary>Host-configurable reaction actions.</summary>
    public IReadOnlyList<MauiReactionChoice> ReactionChoices => _reactionChoices;
    /// <summary>Starts a reply.</summary>
    public ICommand ReplyCommand { get; }
    /// <summary>Requests forwarding through the host callback.</summary>
    public ICommand ForwardCommand { get; }
    /// <summary>Starts an allowed edit.</summary>
    public ICommand EditCommand { get; }
    /// <summary>Requests attachment download through the host callback.</summary>
    public ICommand DownloadCommand { get; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    internal IProjectedChatItem Item => _item;

    internal void Update(
        IProjectedChatItem item,
        string senderLabel,
        string? replyPreview,
        IReadOnlyList<string> reactions,
        MauiChatStrings strings)
    {
        _item = item;
        SenderLabel = senderLabel;
        _replyPreview = replyPreview;
        ForwardedLabel = strings.Forwarded;
        ReplyLabel = strings.Reply;
        ForwardLabel = strings.Forward;
        EditLabel = strings.Edit;
        EditedLabel = strings.Edited;
        DownloadLabel = strings.Download;
        SemanticDescription = $"{senderLabel}, {SentAtLabel}";
        _reactionChoices = reactions
            .Select(reaction => new MauiReactionChoice(
                reaction,
                new SafeAsyncCommand(() => _reaction(ContentId, reaction), _commandFailed)))
            .ToArray();
        OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Stable-diff result used by the control to preserve scroll behavior.</summary>
public sealed record MauiChatDiffResult(
    bool Appended,
    bool Prepended,
    ChatContentId? PreviousFirstContentId);

/// <summary>Dispatcher-bound MAUI presentation adapter over one headless <see cref="ChatViewModel"/>.</summary>
public sealed class MauiChatPresentation : INotifyPropertyChanged, IDisposable
{
    private readonly IDispatcher _dispatcher;
    private readonly ObservableCollection<MauiChatTimelineItem> _items = [];
    private readonly ReadOnlyObservableCollection<MauiChatTimelineItem> _readOnlyItems;
    private ChatViewModel? _viewModel;
    private MauiChatStrings _strings = MauiChatStrings.Default;
    private IReadOnlyList<string> _reactionChoices = ["👍", "❤️", "😂"];
    private bool _isLoading;
    private bool _externalError;
    private bool _disposed;

    /// <summary>Creates an adapter that applies every state update through the supplied UI dispatcher.</summary>
    public MauiChatPresentation(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _readOnlyItems = new ReadOnlyObservableCollection<MauiChatTimelineItem>(_items);
        SendCommand = new SafeAsyncCommand(SendDraftAsync, SetExternalError);
        CancelEditOrReplyCommand = new Command(CancelEditOrReply);
        AttachmentCommand = new SafeAsyncCommand(SendAttachmentAsync, SetExternalError);
    }

    /// <summary>Stable dispatcher-bound timeline wrappers.</summary>
    public ReadOnlyObservableCollection<MauiChatTimelineItem> Items => _readOnlyItems;
    /// <summary>Current localized strings.</summary>
    public MauiChatStrings Strings => _strings;
    /// <summary>Whether imported history requires an explicit trust warning.</summary>
    public bool ContainsBackupHistory => _viewModel?.ContainsBackupHistory == true;
    /// <summary>Whether the host reports a loading operation.</summary>
    public bool IsLoading => _isLoading;
    /// <summary>Whether the empty state should be visible.</summary>
    public bool IsEmpty => !_isLoading && _items.Count == 0;
    /// <summary>Whether timeline items are present.</summary>
    public bool HasItems => _items.Count > 0;
    /// <summary>Whether a draft is being sent.</summary>
    public bool IsSending => _viewModel?.IsSendingDraft == true;
    /// <summary>Whether only a generic command failure should be displayed.</summary>
    public bool HasError => _externalError || _viewModel?.HasCommandError == true;
    /// <summary>Whether edit or reply context is active.</summary>
    public bool IsEditingOrReplying => _viewModel?.IsEditing == true || _viewModel?.ReplyTargetItem is not null;
    /// <summary>Gets or replaces the bounded composer draft.</summary>
    public string DraftText
    {
        get => _viewModel?.DraftText ?? string.Empty;
        set
        {
            if (_viewModel is not null && !string.Equals(_viewModel.DraftText, value, StringComparison.Ordinal))
            {
                _viewModel.SetDraftText(value);
            }
        }
    }

    /// <summary>Sends the current draft.</summary>
    public ICommand SendCommand { get; }
    /// <summary>Cancels the current edit or reply.</summary>
    public ICommand CancelEditOrReplyCommand { get; }
    /// <summary>Requests and sends an attachment through the host callback.</summary>
    public ICommand AttachmentCommand { get; }

    /// <summary>Host-owned forwarding callback.</summary>
    public Func<IProjectedChatItem, CancellationToken, ValueTask>? ForwardRequested { get; set; }
    /// <summary>Host-owned authenticated attachment download callback.</summary>
    public Func<ProjectedChatAttachment, CancellationToken, ValueTask>? AttachmentDownloadRequested { get; set; }
    /// <summary>Host-owned pick, prepare, encrypt and upload callback.</summary>
    public Func<CancellationToken, ValueTask<ChatAttachmentContent?>>? AttachmentSendRequested { get; set; }

    /// <summary>Raised after a dispatcher-bound stable diff.</summary>
    public event EventHandler<MauiChatDiffResult>? DiffApplied;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Rebinds the adapter to exactly one headless conversation view model.</summary>
    public void SetViewModel(ChatViewModel? viewModel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.StateChanged -= OnViewModelStateChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.StateChanged += OnViewModelStateChanged;
        }

        _items.Clear();
        _externalError = false;
        QueueRefresh();
    }

    /// <summary>Replaces all user-visible strings.</summary>
    public void SetStrings(MauiChatStrings strings)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        QueueRefresh();
    }

    /// <summary>Replaces the bounded quick-reaction list.</summary>
    public void SetReactionChoices(IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count > 12 || choices.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Reaction choices are invalid.", nameof(choices));
        }

        _reactionChoices = choices.ToArray();
        QueueRefresh();
    }

    /// <summary>Sets host-owned loading state.</summary>
    public void SetLoading(bool value)
    {
        _isLoading = value;
        QueueRefresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_viewModel is not null)
        {
            _viewModel.StateChanged -= OnViewModelStateChanged;
        }
    }

    private void OnViewModelStateChanged(object? sender, EventArgs args) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Dispatch(ApplySnapshot);
    }

    private void ApplySnapshot()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _viewModel?.Timeline ?? [];
        var previousFirst = _items.Count > 0 ? _items[0].ContentId : (ChatContentId?)null;
        var previousLast = _items.Count > 0 ? _items[^1].ContentId : (ChatContentId?)null;
        for (var targetIndex = 0; targetIndex < snapshot.Count; targetIndex++)
        {
            var target = snapshot[targetIndex];
            var existingIndex = IndexOf(target.ContentId);
            MauiChatTimelineItem wrapper;
            if (existingIndex < 0)
            {
                wrapper = CreateWrapper(target);
                _items.Insert(targetIndex, wrapper);
            }
            else
            {
                wrapper = _items[existingIndex];
                if (existingIndex != targetIndex)
                {
                    _items.Move(existingIndex, targetIndex);
                }
            }

            UpdateWrapper(wrapper, target, snapshot);
        }

        while (_items.Count > snapshot.Count)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        _externalError = false;
        OnPropertyChanged(string.Empty);
        var newFirst = _items.Count > 0 ? _items[0].ContentId : (ChatContentId?)null;
        var newLast = _items.Count > 0 ? _items[^1].ContentId : (ChatContentId?)null;
        DiffApplied?.Invoke(this, new MauiChatDiffResult(
            previousLast.HasValue && newLast.HasValue && previousLast != newLast,
            previousFirst.HasValue && newFirst.HasValue && previousFirst != newFirst,
            previousFirst));
    }

    private MauiChatTimelineItem CreateWrapper(IProjectedChatItem item) => new(
        item,
        _viewModel?.CurrentUserId ?? default,
        ReplyAsync,
        ForwardAsync,
        EditAsync,
        DownloadAsync,
        ToggleReactionAsync,
        SetExternalError);

    private void UpdateWrapper(
        MauiChatTimelineItem wrapper,
        IProjectedChatItem item,
        IReadOnlyList<IProjectedChatItem> snapshot)
    {
        var replyTo = item switch
        {
            ProjectedChatMessage message => message.ReplyToContentId,
            ProjectedChatAttachment attachment => attachment.ReplyToContentId,
            _ => null,
        };
        var target = replyTo.HasValue
            ? snapshot.FirstOrDefault(candidate => candidate.ContentId == replyTo.Value)
            : null;
        var preview = target switch
        {
            ProjectedChatMessage message => message.Text,
            ProjectedChatAttachment attachment => attachment.FileName,
            _ when replyTo.HasValue => _strings.ReplyUnavailable,
            _ => null,
        };
        wrapper.Update(
            item,
            item.SenderUserId == _viewModel?.CurrentUserId ? _strings.You : _strings.Contact,
            preview,
            _reactionChoices,
            _strings);
    }

    private int IndexOf(ChatContentId contentId)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            if (_items[index].ContentId == contentId)
            {
                return index;
            }
        }

        return -1;
    }

    private ValueTask ReplyAsync(ChatContentId contentId)
    {
        _viewModel?.BeginReply(contentId);
        return ValueTask.CompletedTask;
    }

    private async ValueTask ForwardAsync(IProjectedChatItem item)
    {
        if (ForwardRequested is not null)
        {
            await ForwardRequested(item, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private ValueTask EditAsync(ChatContentId contentId)
    {
        _viewModel?.BeginEdit(contentId);
        return ValueTask.CompletedTask;
    }

    private async ValueTask DownloadAsync(ProjectedChatAttachment attachment)
    {
        if (AttachmentDownloadRequested is not null)
        {
            await AttachmentDownloadRequested(attachment, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask ToggleReactionAsync(ChatContentId contentId, string reaction)
    {
        if (_viewModel is not null)
        {
            await _viewModel.ToggleReactionAsync(contentId, reaction).ConfigureAwait(false);
        }
    }

    private async ValueTask SendDraftAsync()
    {
        if (_viewModel is not null)
        {
            await _viewModel.TrySendDraftAsync().ConfigureAwait(false);
        }
    }

    private void CancelEditOrReply()
    {
        if (_viewModel?.IsEditing == true)
        {
            _viewModel.CancelEdit();
        }
        else
        {
            _viewModel?.CancelReply();
        }
    }

    private async ValueTask SendAttachmentAsync()
    {
        if (_viewModel is null || AttachmentSendRequested is null)
        {
            return;
        }

        var content = await AttachmentSendRequested(CancellationToken.None).ConfigureAwait(false);
        if (content is not null)
        {
            await _viewModel.SendAttachmentAsync(content).ConfigureAwait(false);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetExternalError()
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.Dispatch(() =>
        {
            _externalError = true;
            OnPropertyChanged(nameof(HasError));
        });
    }

    internal void ReportExternalError() => SetExternalError();
}

internal sealed class SafeAsyncCommand(Func<ValueTask> execute, Action commandFailed) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            commandFailed();
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
