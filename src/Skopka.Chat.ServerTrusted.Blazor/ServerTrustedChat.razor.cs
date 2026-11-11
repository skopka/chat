using System.Globalization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Skopka.Chat.ServerTrusted.Blazor;

/// <summary>
/// Server-trusted conversation for Blazor Server (<c>InteractiveServer</c> or legacy <c>ServerPrerendered</c>). The
/// server reads these messages; this component never shows end-to-end encryption wording. Prerender renders the first
/// page read-only and persists it; interactive rendering subscribes to content-free live signals, re-reads through the
/// engine and marks messages read only while the document is visible.
/// </summary>
public sealed partial class ServerTrustedChat : ComponentBase, IAsyncDisposable
{
    [Inject]
    private IServiceProvider Services { get; set; } = null!;

    [Inject]
    private IServiceScopeFactory ScopeFactory { get; set; } = null!;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;


    private const string ModulePath = "./_content/Skopka.Chat.ServerTrusted.Blazor/serverTrustedChat.js";
    private const int SnippetLength = 140;

    private ServerTrustedChatViewModel? _model;
    private CancellationTokenSource? _modelLifetime;
    private Channel<bool>? _refreshRequests;
    private PersistentComponentState? _state;
    private PersistingComponentStateSubscription? _persisting;
    private IJSObjectReference? _module;
    private IJSObjectReference? _visibilityHandle;
    private DotNetObjectReference<ServerTrustedChat>? _selfReference;
    private volatile bool _visible;
    private bool _liveStarted;
    private ServerTrustedChatViewModel? _liveModel;
    private bool _disposed;
    private TrustedMessageId? _confirmDeleteId;
    private TrustedMessageId? _reportingId;
    private string? _reportReason;
    private string? _reportComment;
    private int _fileInputGeneration;

    /// <summary>Conversation to show; every read and write is authorized by the engine.</summary>
    [Parameter, EditorRequired]
    public Guid ConversationId { get; set; }

    /// <summary>
    /// Authenticated user. It is trustworthy only because server-rendered component descriptors are protected by ASP.NET
    /// Core Data Protection (share keys across replicas). Never take it from a query string, a form field or a
    /// WebAssembly component; every engine call still authorizes it.
    /// </summary>
    [Parameter, EditorRequired]
    public Guid UserId { get; set; }

    /// <summary>Replaceable localized strings; defaults to <see cref="ServerTrustedChatStrings.Default"/>.</summary>
    [Parameter]
    public ServerTrustedChatStrings? Strings { get; set; }

    /// <summary>Optional class for theme scoping.</summary>
    [Parameter]
    public string? CssClass { get; set; }

    /// <summary>Optional inline style, including <c>--skopka-chat-*</c> custom-property overrides.</summary>
    [Parameter]
    public string? Style { get; set; }

    /// <summary>Periodic catch-up interval that repairs lost live signals (default 30 seconds, minimum 1 second).</summary>
    [Parameter]
    public TimeSpan CatchUpInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>History page size (1–100, default 50).</summary>
    [Parameter]
    public int PageSize { get; set; } = 50;

    /// <summary>Optional complete replacement for each message.</summary>
    [Parameter]
    public RenderFragment<ServerTrustedMessageContext>? MessageTemplate { get; set; }

    /// <summary>
    /// Optional replacement for a visible message body, for example host Markdown rendering with raw HTML disabled. The
    /// default renders encoded text with line breaks preserved.
    /// </summary>
    [Parameter]
    public RenderFragment<TrustedMessage>? MessageBodyTemplate { get; set; }

    /// <summary>Optional replacement for each attachment entry.</summary>
    [Parameter]
    public RenderFragment<ServerTrustedAttachmentContext>? AttachmentTemplate { get; set; }

    /// <summary>Optional complete replacement for the composer (shown only to writers of open conversations).</summary>
    [Parameter]
    public RenderFragment<ServerTrustedComposerContext>? ComposerTemplate { get; set; }

    /// <summary>Optional replacement for the empty timeline.</summary>
    [Parameter]
    public RenderFragment? EmptyTemplate { get; set; }

    /// <summary>
    /// Builds the host-owned download URL (an endpoint that calls <c>OpenAttachmentAsync</c>). Only relative and
    /// http(s) URLs are rendered. When null, attachments render as name and size without a link.
    /// </summary>
    [Parameter]
    public Func<TrustedAttachmentInfo, string>? AttachmentUrl { get; set; }

    /// <summary>Author display name; defaults to <see cref="ServerTrustedChatStrings.You"/> or the stored author label.</summary>
    [Parameter]
    public Func<TrustedMessage, string>? AuthorName { get; set; }

    /// <summary>Participant display name for the observers notice; defaults to the stored participant label.</summary>
    [Parameter]
    public Func<TrustedParticipant, string>? ParticipantName { get; set; }

    /// <summary>Timestamp formatter; supply one that uses the user's time zone. Defaults to server local time.</summary>
    [Parameter]
    public Func<DateTimeOffset, string>? TimeFormatter { get; set; }

    /// <summary>Host report reason codes (each a bounded ASCII label). When null or empty, no report action is shown.</summary>
    [Parameter]
    public IReadOnlyList<string>? ReportReasons { get; set; }

    /// <summary>Localized display text for a report reason code; defaults to the code itself.</summary>
    [Parameter]
    public Func<string, string>? ReportReasonLabel { get; set; }

    /// <summary>Whether writers may attach files in the default composer (default false).</summary>
    [Parameter]
    public bool AllowAttachments { get; set; }

    /// <summary>Optional <c>accept</c> filter for the file input, for example <c>image/png,image/jpeg,image/webp</c>.</summary>
    [Parameter]
    public string? AttachmentAccept { get; set; }

    /// <summary>
    /// Optional client-side attachment size bound in bytes; the effective bound is never above the engine's
    /// <see cref="ServerTrustedChatOptions.MaxAttachmentBytes"/>.
    /// </summary>
    [Parameter]
    public long? MaxAttachmentBytes { get; set; }

    /// <summary>
    /// Host reference type added to the draft on first load, for example <c>lesson</c>. Ignored unless both it and
    /// <see cref="DraftReferenceId"/> are valid; the engine still authorizes it on send.
    /// </summary>
    [Parameter]
    public string? DraftReferenceType { get; set; }

    /// <summary>Host reference identifier added to the draft together with <see cref="DraftReferenceType"/>.</summary>
    [Parameter]
    public string? DraftReferenceId { get; set; }

    /// <summary>Display text of a host reference; defaults to its type.</summary>
    [Parameter]
    public Func<HostReference, string>? ReferenceLabel { get; set; }

    /// <summary>Host URL of a host reference; only relative and http(s) URLs are rendered as links.</summary>
    [Parameter]
    public Func<HostReference, string?>? ReferenceUrl { get; set; }

    /// <summary>Optional replacement for each rendered host reference (messages and draft).</summary>
    [Parameter]
    public RenderFragment<HostReference>? ReferenceTemplate { get; set; }

    /// <summary>Additional attributes applied to the root section.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    private ServerTrustedChatStrings S => Strings ?? ServerTrustedChatStrings.Default;

    private bool IsInteractive => RendererInfo.IsInteractive;

    private string RootClass => string.IsNullOrWhiteSpace(CssClass)
        ? "skopka-chat skopka-chat--server-trusted"
        : $"skopka-chat skopka-chat--server-trusted {CssClass}";

    private string PersistenceKey => $"Skopka.Chat.ServerTrusted.Blazor/{ConversationId:N}/{UserId:N}";

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (ConversationId == Guid.Empty || UserId == Guid.Empty)
        {
            throw new ArgumentException("The conversation and user identifiers are required.");
        }

        if (PageSize is < 1 or > ServerTrustedChatLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }

        if (CatchUpInterval < TimeSpan.FromSeconds(1) || CatchUpInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(CatchUpInterval));
        }

        if (MaxAttachmentBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttachmentBytes));
        }

        if (_model is not null && _model.ConversationId.Value == ConversationId && _model.UserId == UserId)
        {
            return;
        }

        StopModel();
        var runner = Services.GetService<IServerTrustedChatOperationRunner>() ?? new ScopedServerTrustedChatOperationRunner(ScopeFactory);
        var model = new ServerTrustedChatViewModel(new TrustedConversationId(ConversationId), UserId, runner, PageSize);
        var lifetime = new CancellationTokenSource();
        _model = model;
        _modelLifetime = lifetime;
        _confirmDeleteId = null;
        _reportingId = null;
        model.Changed += OnModelChanged;

        _state ??= Services.GetService<PersistentComponentState>();
        if (IsInteractive)
        {
            if (_state is null ||
                !_state.TryTakeFromJson<ServerTrustedChatSnapshot>(PersistenceKey, out var snapshot) ||
                !model.TryRestore(snapshot))
            {
                await model.LoadAsync(lifetime.Token);
            }

            // A parameter change after the first interactive render restarts the live loops for the new model.
            if (_liveStarted && ReferenceEquals(_model, model))
            {
                StartLive(model, lifetime.Token);
            }
        }
        else
        {
            await model.LoadAsync(lifetime.Token);
            if (_state is not null && _persisting is null)
            {
                _persisting = _state.RegisterOnPersisting(PersistAsync);
            }
        }

        if (ServerTrustedChatValidation.IsReferenceType(DraftReferenceType) && ServerTrustedChatValidation.IsReferenceId(DraftReferenceId))
        {
            model.AddReference(new HostReference(DraftReferenceType!, DraftReferenceId!));
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !IsInteractive || _disposed || _model is null || _modelLifetime is null)
        {
            return;
        }

        _liveStarted = true;
        StartLive(_model, _modelLifetime.Token);
        await StartVisibilityAsync(_modelLifetime.Token);
        RequestRefresh();
    }

    /// <summary>Called by the browser module when the document visibility changes.</summary>
    [JSInvokable]
    public Task OnVisibilityChanged(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            RequestRefresh();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopModel();
        _persisting?.Dispose();
        _persisting = null;
        try
        {
            if (_visibilityHandle is not null)
            {
                await _visibilityHandle.InvokeVoidAsync("dispose");
                await _visibilityHandle.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _visibilityHandle = null;
        _module = null;
        _selfReference?.Dispose();
        _selfReference = null;
        GC.SuppressFinalize(this);
    }

    private void StopModel()
    {
        if (_model is not null)
        {
            _model.Changed -= OnModelChanged;
        }

        _modelLifetime?.Cancel();
        _modelLifetime = null;
        _refreshRequests?.Writer.TryComplete();
        _refreshRequests = null;
        _liveModel = null;
        _model = null;
    }

    private void StartLive(ServerTrustedChatViewModel model, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(_liveModel, model) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _liveModel = model;
        var requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        _refreshRequests = requests;
        _ = RunRefreshesAsync(model, requests.Reader, cancellationToken);
        _ = RunTimerAsync(requests.Writer, cancellationToken);
        if (Services.GetService<IServerTrustedChatLiveSignals>() is { } signals)
        {
            _ = RunSignalsAsync(signals, model, requests.Writer, cancellationToken);
        }
    }

    private void RequestRefresh() => _refreshRequests?.Writer.TryWrite(true);

    private async Task RunRefreshesAsync(ServerTrustedChatViewModel model, ChannelReader<bool> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reader.TryRead(out _);
                if (await model.CatchUpAsync(cancellationToken).ConfigureAwait(false) && _visible)
                {
                    await model.MarkReadThroughLatestAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task RunTimerAsync(ChannelWriter<bool> writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CatchUpInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSignalsAsync(
        IServerTrustedChatLiveSignals signals,
        ServerTrustedChatViewModel model,
        ChannelWriter<bool> writer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var signal in signals.SubscribeAsync(model.ConversationId, cancellationToken).ConfigureAwait(false))
                {
                    // Read markers are not displayed; message signals already covered by the cursor need no re-read.
                    if (signal.Kind == ServerTrustedChatSignalKind.ReadMarkerChanged ||
                        (signal.Kind == ServerTrustedChatSignalKind.MessagesChanged && model.IsLoaded && signal.ChangeRevision <= model.Revision))
                    {
                        continue;
                    }

                    writer.TryWrite(true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failing signal source must not end the circuit; the periodic catch-up continues.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            try
            {
                await Task.Delay(CatchUpInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task StartVisibilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
            _selfReference ??= DotNetObjectReference.Create(this);
            _visibilityHandle = await _module.InvokeAsync<IJSObjectReference>("observeVisibility", cancellationToken, _selfReference);
            _visible = await _module.InvokeAsync<bool>("isVisible", cancellationToken);
        }
        catch (JSDisconnectedException)
        {
            _visible = false;
        }
        catch (JSException)
        {
            _visible = false;
        }
        catch (OperationCanceledException)
        {
            _visible = false;
        }
    }

    private Task PersistAsync()
    {
        if (_state is not null && _model?.CreateSnapshot() is { } snapshot)
        {
            _state.PersistAsJson(PersistenceKey, snapshot);
        }

        return Task.CompletedTask;
    }

    private void OnModelChanged(object? sender, EventArgs args)
    {
        if (!_disposed && IsInteractive)
        {
            _ = RenderSafelyAsync();
        }
    }

    private async Task RenderSafelyAsync()
    {
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private CancellationToken Token => _modelLifetime?.Token ?? new CancellationToken(canceled: true);

    private async Task LoadOlderAsync()
    {
        if (_model is { } model)
        {
            await model.LoadOlderAsync(Token);
        }
    }

    private void OnDraftInput(ChangeEventArgs args) => _model?.SetDraft(args.Value?.ToString());

    private async Task SendAsync()
    {
        if (_model is { } model)
        {
            await model.SendDraftAsync(Token);
        }
    }

    private void Cancel() => _model?.Cancel();

    private bool AttachmentsEnabled(ServerTrustedChatViewModel model) =>
        AllowAttachments && model.CanWrite && !model.IsEditing && model.MaxAttachmentsPerMessage > 0;

    private long EffectiveMaxAttachmentBytes(ServerTrustedChatViewModel model) =>
        MaxAttachmentBytes is { } limit ? Math.Min(limit, model.MaxAttachmentBytes) : model.MaxAttachmentBytes;

    private async Task OnFileSelectedAsync(InputFileChangeEventArgs args)
    {
        _fileInputGeneration++;
        await UploadFileAsync(args.File);
    }

    private async Task<bool> UploadFileAsync(IBrowserFile file)
    {
        if (_model is not { } model || !AllowAttachments)
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(file);
        var maximum = EffectiveMaxAttachmentBytes(model);
        if (file.Size <= 0 || file.Size > maximum)
        {
            model.RejectAttachment();
            return false;
        }

        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var stream = file.OpenReadStream(maximum, Token);
        await using (stream)
        {
            return await model.UploadAttachmentAsync(file.Name, contentType, stream, Token);
        }
    }

    private void RemoveAttachment(TrustedAttachmentId attachmentId) => _model?.RemovePendingAttachment(attachmentId);

    private void RemoveReference(HostReference reference) => _model?.RemoveReference(reference);

    private string ResolveReferenceLabel(HostReference reference) => ReferenceLabel?.Invoke(reference) ?? reference.Type;

    private RenderFragment RenderReference(HostReference reference)
    {
        if (ReferenceTemplate is not null)
        {
            return ReferenceTemplate(reference);
        }

        var label = ResolveReferenceLabel(reference);
        var url = ReferenceUrl?.Invoke(reference);
        return builder =>
        {
            if (IsSafeLink(url))
            {
                builder.OpenElement(0, "a");
                builder.AddAttribute(1, "class", "skopka-chat-reference");
                builder.AddAttribute(2, "href", url);
                builder.AddAttribute(3, "rel", "noopener noreferrer");
                builder.AddContent(4, label);
                builder.CloseElement();
            }
            else
            {
                builder.OpenElement(5, "span");
                builder.AddAttribute(6, "class", "skopka-chat-reference");
                builder.AddContent(7, label);
                builder.CloseElement();
            }
        };
    }

    private void BeginReply(TrustedMessageId messageId)
    {
        CloseMessagePanels();
        _model?.BeginReply(messageId);
    }

    private void BeginEdit(TrustedMessageId messageId)
    {
        CloseMessagePanels();
        _model?.BeginEdit(messageId);
    }

    private void ConfirmDelete(TrustedMessageId messageId)
    {
        _reportingId = null;
        _confirmDeleteId = messageId;
    }

    private void CancelDelete() => _confirmDeleteId = null;

    private async Task DeleteAsync(TrustedMessageId messageId)
    {
        _confirmDeleteId = null;
        if (_model is { } model)
        {
            await model.DeleteAsync(messageId, Token);
        }
    }

    private void BeginReport(TrustedMessageId messageId)
    {
        _confirmDeleteId = null;
        _reportingId = messageId;
        _reportReason = ReportReasons is { Count: > 0 } reasons ? reasons[0] : null;
        _reportComment = null;
    }

    private void CancelReport() => _reportingId = null;

    private void OnReportReasonChanged(ChangeEventArgs args) => _reportReason = args.Value?.ToString();

    private void OnReportCommentInput(ChangeEventArgs args) => _reportComment = args.Value?.ToString();

    private async Task SubmitReportAsync(TrustedMessageId messageId)
    {
        if (_model is not { } model || ReportReasons is not { Count: > 0 } reasons || _reportReason is not { } reason || !reasons.Contains(reason))
        {
            return;
        }

        if (await model.ReportAsync(messageId, reason, _reportComment, Token))
        {
            _reportingId = null;
            _reportComment = null;
        }
    }

    private void CloseMessagePanels()
    {
        _confirmDeleteId = null;
        _reportingId = null;
    }

    private static bool CanEdit(ServerTrustedChatViewModel model, TrustedMessage message) =>
        model.CanWrite && model.IsOwn(message) && !message.IsTombstone && message.Text is not null;

    private static bool CanDelete(ServerTrustedChatViewModel model, TrustedMessage message) =>
        model.CanWrite && model.IsOwn(message) && !message.IsTombstone;

    private static bool CanReply(ServerTrustedChatViewModel model, TrustedMessage message) =>
        model.CanWrite && !model.IsOwn(message) && !message.IsTombstone && !model.IsEditing;

    private bool CanReport(ServerTrustedChatViewModel model, TrustedMessage message) =>
        ReportReasons is { Count: > 0 } && model.IsLoaded && !model.IsOwn(message) && !message.IsTombstone && !model.IsReported(message.Id);

    private ServerTrustedMessageContext CreateMessageContext(ServerTrustedChatViewModel model, TrustedMessage message)
    {
        var interactive = IsInteractive;
        return new ServerTrustedMessageContext(
            message,
            model.IsOwn(message),
            message.ReplyToMessageId is { } replyId ? model.FindMessage(replyId) : null,
            ResolveAuthorName(message),
            model,
            interactive && CanEdit(model, message),
            interactive && CanDelete(model, message),
            interactive && CanReply(model, message),
            interactive && CanReport(model, message),
            () =>
            {
                model.BeginReply(message.Id);
                return Task.CompletedTask;
            },
            () =>
            {
                model.BeginEdit(message.Id);
                return Task.CompletedTask;
            },
            async () => await model.DeleteAsync(message.Id, Token),
            async (reason, comment) => await model.ReportAsync(message.Id, reason, comment, Token));
    }

    private ServerTrustedComposerContext CreateComposerContext(ServerTrustedChatViewModel model) =>
        new(
            model.Draft,
            model.SetDraft,
            async () => await model.SendDraftAsync(Token),
            model.Cancel,
            model.IsEditing,
            model.IsReplying,
            IsInteractive && model.CanSend,
            model.MaxTextLength,
            model.ReplyTarget is { } replyId ? model.FindMessage(replyId) : null,
            model,
            AllowAttachments,
            IsInteractive && AttachmentsEnabled(model) && model.CanUpload,
            model.IsUploading,
            EffectiveMaxAttachmentBytes(model),
            AttachmentAccept,
            model.PendingAttachments,
            UploadFileAsync,
            attachmentId => model.RemovePendingAttachment(attachmentId),
            model.DraftReferences,
            reference => model.RemoveReference(reference));

    private static string MessageClass(TrustedMessage message, bool own) =>
        (own, message.IsTombstone) switch
        {
            (true, true) => "skopka-chat-message skopka-chat-message--own skopka-chat-message--tombstone",
            (true, false) => "skopka-chat-message skopka-chat-message--own",
            (false, true) => "skopka-chat-message skopka-chat-message--tombstone",
            _ => "skopka-chat-message",
        };

    private string ResolveAuthorName(TrustedMessage message) =>
        AuthorName?.Invoke(message) ?? (message.AuthorUserId == UserId ? S.You : message.AuthorLabel);

    private string ResolveParticipantName(TrustedParticipant participant) =>
        ParticipantName?.Invoke(participant) ?? participant.Label;

    private string ResolveReasonLabel(string reason) => ReportReasonLabel?.Invoke(reason) ?? reason;

    private string FormatTime(DateTimeOffset value) =>
        TimeFormatter?.Invoke(value) ?? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private string TombstoneText(TrustedMessageTombstone tombstone) => tombstone switch
    {
        TrustedMessageTombstone.DeletedByAuthor => S.DeletedByAuthor,
        TrustedMessageTombstone.HiddenByModerator => S.HiddenByModerator,
        _ => S.Erased,
    };

    private string ReplySnippet(ServerTrustedChatViewModel model, TrustedMessageId replyId) =>
        model.FindMessage(replyId) is { } target ? Snippet(target) : S.ReplyUnavailable;

    private string Snippet(TrustedMessage message)
    {
        if (message.IsTombstone)
        {
            return TombstoneText(message.Tombstone);
        }

        if (string.IsNullOrEmpty(message.Text))
        {
            return S.Attachment;
        }

        var text = message.Text.Replace('\n', ' ').Replace('\t', ' ');
        if (text.Length <= SnippetLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[SnippetLength - 1]) ? SnippetLength - 1 : SnippetLength;
        return string.Concat(text.AsSpan(0, length), "…");
    }

    private string? FailureText(ServerTrustedChatFailure failure) => failure switch
    {
        ServerTrustedChatFailure.RateLimited => S.RateLimited,
        ServerTrustedChatFailure.Failed => S.GenericFailure,
        ServerTrustedChatFailure.AttachmentRejected => S.AttachmentRejected,
        _ => null,
    };

    private string AttachmentName(TrustedAttachmentInfo attachment) =>
        string.IsNullOrWhiteSpace(attachment.FileName) ? S.Attachment : attachment.FileName;

    private string? ResolveAttachmentUrl(TrustedAttachmentInfo attachment)
    {
        var url = AttachmentUrl?.Invoke(attachment);
        return IsSafeLink(url) ? url : null;
    }

    // Only same-origin relative references and absolute http(s) URLs; never javascript:, data: or protocol-relative.
    internal static bool IsSafeLink(string? url)
    {
        // Browsers trim surrounding whitespace and treat a backslash like a slash, so "\\host", "/\host" and " //host"
        // would all become protocol-relative links to another origin.
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl) || !string.Equals(url.Trim(), url, StringComparison.Ordinal) ||
            url.StartsWith('\\') || url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("/\\", StringComparison.Ordinal))
        {
            return false;
        }

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out _);
        }

        var colon = url.IndexOf(':', StringComparison.Ordinal);
        var pathStart = url.IndexOfAny(['/', '?', '#']);
        return colon < 0 || (pathStart >= 0 && pathStart < colon);
    }

    private string FormatSize(long length)
    {
        var culture = CultureInfo.CurrentCulture;
        return length switch
        {
            < 1024 => string.Create(culture, $"{length} {S.Bytes}"),
            < 1024 * 1024 => string.Create(culture, $"{length / 1024d:0.#} {S.Kilobytes}"),
            _ => string.Create(culture, $"{length / (1024d * 1024d):0.#} {S.Megabytes}"),
        };
    }
}
