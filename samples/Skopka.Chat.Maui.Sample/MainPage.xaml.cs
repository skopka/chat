using Skopka.Chat.Client;

namespace Skopka.Chat.Maui.Sample;

public partial class MainPage : ContentPage
{
    private readonly SampleChatSessionFactory _sessions;
    private SampleChatContext? _context;
    private bool _opening;

    public MainPage(SampleChatSessionFactory sessions)
    {
        InitializeComponent();
        _sessions = sessions;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_context is not null)
        {
            _context.Lifecycle.OnResume();
            return;
        }

        if (_opening)
        {
            return;
        }

        _opening = true;
        try
        {
            StatusLabel.Text = "Opening encrypted conversation…";
            _context = await _sessions.OpenAsync();
            ChatView.ViewModel = _context.ViewModel;
            ChatView.LoadOlderRequested = _context.History.LoadPreviousAsyncAsCallback;
            ChatView.AttachmentSendRequested = _context.PickEncryptAndUploadAsync;
            ChatView.AttachmentDownloadRequested = DownloadAttachmentAsync;
            ChatView.ForwardRequested = ForwardAsync;
            StatusLabel.Text = "Foreground polling is active. Push may call RequestSynchronization as a wake signal.";
        }
        catch (Exception)
        {
            StatusLabel.Text = "Session setup failed. Configure trusted authentication and an HTTPS endpoint.";
        }
        finally
        {
            _opening = false;
        }
    }

    protected override void OnDisappearing()
    {
        _context?.Lifecycle.OnSleep();
        base.OnDisappearing();
    }

    private async ValueTask DownloadAttachmentAsync(ProjectedChatAttachment attachment, CancellationToken cancellationToken)
    {
        if (_context is null)
        {
            return;
        }

        await _context.DownloadAuthenticatedAsync(
            attachment,
            async (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                await DisplayAlertAsync("Authenticated attachment", $"{file.FileName} ({file.Length} bytes)", "OK");
            },
            cancellationToken);
    }

    private async ValueTask ForwardAsync(IProjectedChatItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DisplayAlertAsync("Forward", "Choose a target conversation in host navigation.", "OK");
    }
}
