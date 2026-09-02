using Microsoft.Maui.Controls;
using Skopka.Chat.Client;
using Skopka.Chat.UI;

namespace Skopka.Chat.UI.Maui;

/// <summary>
/// Native, themeable and replaceable conversation surface. The host retains all transport,
/// encryption, navigation, file-opening and attachment-policy decisions.
/// </summary>
public partial class SkopkaChatView : ContentView, IDisposable
{
    /// <summary>Bindable headless conversation state.</summary>
    public static readonly BindableProperty ViewModelProperty = BindableProperty.Create(
        nameof(ViewModel), typeof(ChatViewModel), typeof(SkopkaChatView), propertyChanged: OnViewModelChanged);

    /// <summary>Bindable localized string set.</summary>
    public static readonly BindableProperty StringsProperty = BindableProperty.Create(
        nameof(Strings), typeof(MauiChatStrings), typeof(SkopkaChatView), MauiChatStrings.Default,
        propertyChanged: OnStringsChanged);

    /// <summary>Bindable loading marker controlled by the host.</summary>
    public static readonly BindableProperty IsLoadingProperty = BindableProperty.Create(
        nameof(IsLoading), typeof(bool), typeof(SkopkaChatView), false, propertyChanged: OnLoadingChanged);

    /// <summary>Bindable quick-reaction choices.</summary>
    public static readonly BindableProperty ReactionChoicesProperty = BindableProperty.Create(
        nameof(ReactionChoices), typeof(IReadOnlyList<string>), typeof(SkopkaChatView),
        defaultValueCreator: _ => new[] { "👍", "❤️", "😂" }, propertyChanged: OnReactionChoicesChanged);

    /// <summary>Bindable host message template.</summary>
    public static readonly BindableProperty MessageTemplateProperty = BindableProperty.Create(
        nameof(MessageTemplate), typeof(DataTemplate), typeof(SkopkaChatView), propertyChanged: OnTemplateChanged);

    /// <summary>Bindable host attachment template.</summary>
    public static readonly BindableProperty AttachmentTemplateProperty = BindableProperty.Create(
        nameof(AttachmentTemplate), typeof(DataTemplate), typeof(SkopkaChatView), propertyChanged: OnTemplateChanged);

    /// <summary>Bindable host composer template.</summary>
    public static readonly BindableProperty ComposerTemplateProperty = BindableProperty.Create(
        nameof(ComposerTemplate), typeof(DataTemplate), typeof(SkopkaChatView), propertyChanged: OnTemplateChanged);

    /// <summary>Bindable host empty-state template.</summary>
    public static readonly BindableProperty EmptyTemplateProperty = BindableProperty.Create(
        nameof(EmptyTemplate), typeof(DataTemplate), typeof(SkopkaChatView), propertyChanged: OnTemplateChanged);

    /// <summary>Bindable host forwarding callback.</summary>
    public static readonly BindableProperty ForwardRequestedProperty = BindableProperty.Create(
        nameof(ForwardRequested), typeof(Func<IProjectedChatItem, CancellationToken, ValueTask>), typeof(SkopkaChatView),
        propertyChanged: OnCallbackChanged);

    /// <summary>Bindable host attachment download callback.</summary>
    public static readonly BindableProperty AttachmentDownloadRequestedProperty = BindableProperty.Create(
        nameof(AttachmentDownloadRequested), typeof(Func<ProjectedChatAttachment, CancellationToken, ValueTask>), typeof(SkopkaChatView),
        propertyChanged: OnCallbackChanged);

    /// <summary>Bindable host attachment picker/preparation callback.</summary>
    public static readonly BindableProperty AttachmentSendRequestedProperty = BindableProperty.Create(
        nameof(AttachmentSendRequested), typeof(Func<CancellationToken, ValueTask<ChatAttachmentContent?>>), typeof(SkopkaChatView),
        propertyChanged: OnCallbackChanged);

    /// <summary>Bindable callback used to prepend an older bounded page.</summary>
    public static readonly BindableProperty LoadOlderRequestedProperty = BindableProperty.Create(
        nameof(LoadOlderRequested), typeof(Func<CancellationToken, ValueTask>), typeof(SkopkaChatView));

    private readonly MauiChatPresentation _presentation;
    private readonly MauiChatTemplateSelector _selector;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _nearBottom = true;
    private bool _loadingOlder;
    private bool _disposed;

    /// <summary>Creates a native conversation control bound through the current UI dispatcher.</summary>
    public SkopkaChatView()
    {
        InitializeComponent();
        _presentation = new MauiChatPresentation(Dispatcher);
        _presentation.DiffApplied += OnDiffApplied;
        BindingContext = _presentation;
        _selector = new MauiChatTemplateSelector(
            (DataTemplate)Resources["DefaultMessageTemplate"],
            (DataTemplate)Resources["DefaultAttachmentTemplate"]);
        Timeline.ItemTemplate = _selector;
        ApplyAllProperties();
    }

    /// <summary>Headless state for exactly one conversation.</summary>
    public ChatViewModel? ViewModel { get => (ChatViewModel?)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    /// <summary>All user-visible strings.</summary>
    public MauiChatStrings Strings { get => (MauiChatStrings)GetValue(StringsProperty); set => SetValue(StringsProperty, value); }
    /// <summary>Whether a host loading operation is active.</summary>
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    /// <summary>At most twelve quick reaction strings.</summary>
    public IReadOnlyList<string> ReactionChoices { get => (IReadOnlyList<string>)GetValue(ReactionChoicesProperty); set => SetValue(ReactionChoicesProperty, value); }
    /// <summary>Optional text-message template receiving <see cref="MauiChatTimelineItem"/>.</summary>
    public DataTemplate? MessageTemplate { get => (DataTemplate?)GetValue(MessageTemplateProperty); set => SetValue(MessageTemplateProperty, value); }
    /// <summary>Optional attachment template receiving <see cref="MauiChatTimelineItem"/>.</summary>
    public DataTemplate? AttachmentTemplate { get => (DataTemplate?)GetValue(AttachmentTemplateProperty); set => SetValue(AttachmentTemplateProperty, value); }
    /// <summary>Optional composer template receiving <see cref="MauiChatPresentation"/>.</summary>
    public DataTemplate? ComposerTemplate { get => (DataTemplate?)GetValue(ComposerTemplateProperty); set => SetValue(ComposerTemplateProperty, value); }
    /// <summary>Optional empty template receiving <see cref="MauiChatPresentation"/>.</summary>
    public DataTemplate? EmptyTemplate { get => (DataTemplate?)GetValue(EmptyTemplateProperty); set => SetValue(EmptyTemplateProperty, value); }
    /// <summary>Host-owned forwarding callback.</summary>
    public Func<IProjectedChatItem, CancellationToken, ValueTask>? ForwardRequested { get => (Func<IProjectedChatItem, CancellationToken, ValueTask>?)GetValue(ForwardRequestedProperty); set => SetValue(ForwardRequestedProperty, value); }
    /// <summary>Host-owned attachment download callback; the control never opens a URI itself.</summary>
    public Func<ProjectedChatAttachment, CancellationToken, ValueTask>? AttachmentDownloadRequested { get => (Func<ProjectedChatAttachment, CancellationToken, ValueTask>?)GetValue(AttachmentDownloadRequestedProperty); set => SetValue(AttachmentDownloadRequestedProperty, value); }
    /// <summary>Host-owned pick, prepare, encrypt and upload callback.</summary>
    public Func<CancellationToken, ValueTask<ChatAttachmentContent?>>? AttachmentSendRequested { get => (Func<CancellationToken, ValueTask<ChatAttachmentContent?>>?)GetValue(AttachmentSendRequestedProperty); set => SetValue(AttachmentSendRequestedProperty, value); }
    /// <summary>Host callback invoked near the top to prepend older items.</summary>
    public Func<CancellationToken, ValueTask>? LoadOlderRequested { get => (Func<CancellationToken, ValueTask>?)GetValue(LoadOlderRequestedProperty); set => SetValue(LoadOlderRequestedProperty, value); }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _presentation.DiffApplied -= OnDiffApplied;
        _presentation.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void OnViewModelChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SkopkaChatView)bindable)._presentation.SetViewModel((ChatViewModel?)newValue);

    private static void OnStringsChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SkopkaChatView)bindable)._presentation.SetStrings((MauiChatStrings?)newValue ?? MauiChatStrings.Default);

    private static void OnLoadingChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SkopkaChatView)bindable)._presentation.SetLoading((bool)newValue);

    private static void OnReactionChoicesChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SkopkaChatView)bindable)._presentation.SetReactionChoices((IReadOnlyList<string>?)newValue ?? Array.Empty<string>());

    private static void OnTemplateChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SkopkaChatView)bindable).ApplyTemplates();

    private static void OnCallbackChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((SkopkaChatView)bindable).ApplyCallbacks();

    private void ApplyAllProperties()
    {
        _presentation.SetViewModel(ViewModel);
        _presentation.SetStrings(Strings);
        _presentation.SetLoading(IsLoading);
        _presentation.SetReactionChoices(ReactionChoices);
        ApplyCallbacks();
        ApplyTemplates();
    }

    private void ApplyCallbacks()
    {
        _presentation.ForwardRequested = ForwardRequested;
        _presentation.AttachmentDownloadRequested = AttachmentDownloadRequested;
        _presentation.AttachmentSendRequested = AttachmentSendRequested;
    }

    private void ApplyTemplates()
    {
        if (_selector is null)
        {
            return;
        }

        _selector.MessageTemplate = MessageTemplate;
        _selector.AttachmentTemplate = AttachmentTemplate;
        DefaultComposer.IsVisible = ComposerTemplate is null;
        CustomComposerHost.IsVisible = ComposerTemplate is not null;
        CustomComposerHost.Content = CreateTemplateContent(ComposerTemplate, _presentation);
        DefaultEmptyLabel.IsVisible = EmptyTemplate is null;
        CustomEmptyHost.IsVisible = EmptyTemplate is not null;
        CustomEmptyHost.Content = CreateTemplateContent(EmptyTemplate, _presentation);
    }

    private static View? CreateTemplateContent(DataTemplate? template, object bindingContext)
    {
        if (template?.CreateContent() is not View view)
        {
            return null;
        }

        view.BindingContext = bindingContext;
        return view;
    }

    private async void OnTimelineScrolled(object? sender, ItemsViewScrolledEventArgs args)
    {
        _nearBottom = args.LastVisibleItemIndex >= _presentation.Items.Count - 2;
        if (_disposed || _loadingOlder || args.FirstVisibleItemIndex > 2 || LoadOlderRequested is null)
        {
            return;
        }

        _loadingOlder = true;
        try
        {
            await LoadOlderRequested(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _presentation.ReportExternalError();
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    private void OnDiffApplied(object? sender, MauiChatDiffResult result)
    {
        if (result.Prepended && result.PreviousFirstContentId is { } first)
        {
            var anchor = _presentation.Items.FirstOrDefault(item => item.ContentId == first);
            if (anchor is not null)
            {
                Timeline.ScrollTo(anchor, position: ScrollToPosition.Start, animate: false);
            }
        }
        else if (result.Appended && _nearBottom && _presentation.Items.Count > 0)
        {
            Timeline.ScrollTo(_presentation.Items[^1], position: ScrollToPosition.End, animate: true);
        }
    }
}

internal sealed class MauiChatTemplateSelector(DataTemplate defaultMessage, DataTemplate defaultAttachment) : DataTemplateSelector
{
    internal DataTemplate? MessageTemplate { get; set; }
    internal DataTemplate? AttachmentTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        item is MauiChatTimelineItem { IsAttachment: true }
            ? AttachmentTemplate ?? defaultAttachment
            : MessageTemplate ?? defaultMessage;
}
