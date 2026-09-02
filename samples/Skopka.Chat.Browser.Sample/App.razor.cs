using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Browser;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;
using Skopka.Chat.UI;

namespace Skopka.Chat.Browser.Sample;

public partial class App
{
    private BrowserChatCryptography? _crypto;
    private DemoHostApi? _host;
    private DeviceAuthorizationContext? _account;
    private BrowserVault? _vault;
    private PersistentDeviceIdentityService? _identities;
    private PersistentDeviceIdentityState _identityState;
    private PublicDevice? _device;
    private SkopkaChatHttpClient? _api;
    private HttpClient? _apiHttp;
    private BrowserChatSession? _session;
    private ChatViewModel? _chat;
    private ChatHistoryPager? _pager;
    private string _status = "Starting browser cryptography…";
    private string _selectedAccount = "alice";
    private string _phrase = "";
    private string _draft = "";
    private bool _busy;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) { return; }
        await GuardAsync(async () =>
        {
            _crypto = await BrowserChatCryptography.CreateAsync(JS);
            _host = new DemoHostApi(new Uri(Navigation.BaseUri));
            try { _account = await _host.GetContextAsync(); }
            catch { /* Anonymous startup does not create a device or vault. */ }
            _status = _account is null ? "Browser ready. Log in to a demo account." : "Session restored. Unlock the existing vault.";
        });
        StateHasChanged();
    }
    private Task LoginAsync() => GuardAsync(async () =>
    {
        await _host!.LoginAsync(_selectedAccount);
        _account = await _host.GetContextAsync();
        _status = "Logged in. Unlock your existing vault or explicitly create one on first use.";
    });
    private Task UnlockAsync(bool create) => GuardAsync(async () =>
    {
        _account = await _host!.GetContextAsync();
        var installation = await BrowserVault.GetInstallationIdAsync(JS, create);
        if (installation is null) { _status = "No local installation. Cleared data cannot be recovered; explicit new enrollment is required."; return; }
        var phrase = Encoding.UTF8.GetBytes(_phrase);
        _phrase = "";
        try { _vault = await BrowserVault.OpenAsync(JS, new DeviceIdentityScope(_account.ServiceId, _account.UserId, installation.Value), phrase, create); }
        finally { CryptographicOperations.ZeroMemory(phrase); }
        var keys = new BrowserDeviceIdentityStore(_vault);
        _identities = new PersistentDeviceIdentityService(keys, keys, TimeProvider.System, _crypto!);
        var loaded = await _identities.LoadAsync(_vault.Scope);
        _identityState = loaded.State;
        _device = loaded.State == PersistentDeviceIdentityState.Ready ? loaded.Metadata!.PublicDevice : null;
        _status = "Vault unlocked. Identity: " + loaded.State;
    });
    private Task CreateDeviceAsync() => GuardAsync(async () =>
    {
        var result = await _identities!.CreateAsync(_vault!.Scope);
        _identityState = result.State;
        _device = result.State == PersistentDeviceIdentityState.Ready ? result.Metadata!.PublicDevice : null;
        _status = "Identity: " + result.State + ". Bind the current session before chatting.";
    });
    private Task BindAsync() => GuardAsync(async () =>
    {
        if (_session is not null) { await _session.DisposeAsync(); _session = null; }
        _account = await _host!.GetContextAsync(); // Independent context, not taken from the challenge.
        var metadata = (await _identities!.LoadAsync(_vault!.Scope)).Metadata!;
        _apiHttp?.Dispose();
        _apiHttp = new HttpClient { BaseAddress = new Uri(Navigation.BaseUri) };
        _api = new SkopkaChatHttpClient(_apiHttp, _host.Authorization, Options.Create(new SkopkaChatHttpClientOptions
        {
            AuthenticatedUserId = _account.UserId.Value,
            AuthenticatedDeviceId = _device!.DeviceId.Value,
            RequireHttps = !new Uri(Navigation.BaseUri).IsLoopback
        }), TimeProvider.System);
        var proof = new DeviceBindingProofService(new BrowserDeviceIdentityStore(_vault), TimeProvider.System, _crypto!);
        var binding = await new DeviceBindingCoordinator(_identities, proof, _api).BindAsync(_vault.Scope, _account,
            metadata.Registered ? DeviceBindingOperation.Rebind : DeviceBindingOperation.Enrollment);
        _device = binding.Device;
        _session = new BrowserChatSession(_vault, _device, _crypto!, _api, _api, new Applier(this));
        _status = "Session bound. Device keys are unchanged.";
    });
    private Task SelectConversationAsync() => GuardAsync(async () =>
    {
        var peer = _account!.UserId.Value == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
            ? Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") : Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var conversation = await _api!.GetOrCreatePersonalConversationAsync(new UserId(peer));
        _chat = new ChatViewModel(conversation.ConversationId, _account.UserId, new DisabledDefaultSender());
        _pager?.Dispose();
        _pager = new ChatHistoryPager(_session!.Events, new Applier(this), conversation.ConversationId);
        await _pager.LoadInitialAsync();
        _status = "Conversation opened. The other account must enroll a device before receiving messages.";
    });
    private Task SendAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(_draft)) { return; }
        var content = new ChatTextContent(ChatContentId.New(), _draft);
        await _session!.QueueAsync(_chat!.ConversationId, content);
        _draft = ""; // Only after durable insertion; retry keeps the stored content ID.
        _status = "Message saved locally. Waiting for delivery.";
        var completed = await _session.DispatchAsync();
        _status = completed > 0 ? "Queued messages delivered." : "Saved locally; retry when the network and recipient are available.";
    });
    private Task SynchronizeAsync() => GuardAsync(async () =>
    {
        var sent = await _session!.DispatchAsync();
        var received = await _session.SynchronizeAsync();
        if (_chat is not null)
        {
            var page = await _session.Events.ReadPreviousPageAsync(_chat.ConversationId);
            foreach (var item in page.Items) { _chat.Apply(item); }
        }
        _status = $"Synchronized: {sent} outgoing jobs, {received.Acknowledged} acknowledged deliveries.";
    });
    private Task LoadOlderAsync() => GuardAsync(async () => { await _pager!.LoadPreviousAsync(); });
    private Task LogoutAsync() => GuardAsync(async () =>
    {
        await CloseSessionAsync();
        await _host!.LogoutAsync();
        _account = null;
        _status = "Logged out and locked. Local identity/history/outbox were retained.";
    });
    private async Task CloseSessionAsync()
    {
        if (_session is not null) { await _session.DisposeAsync(); _session = null; }
        if (_vault is not null) { await _vault.DisposeAsync(); _vault = null; }
        _apiHttp?.Dispose(); _apiHttp = null; _api = null;
        _pager?.Dispose(); _pager = null;
        _chat = null; _device = null; _identities = null; _draft = ""; _phrase = "";
    }
    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy) { return; }
        _busy = true;
        try { await action(); }
        catch (BrowserStorageException error) { _status = "Local vault unavailable or recovery required: " + error.Code + ". No replacement keys were created."; }
        catch (DeviceBindingRevokedException) { _device = null; _identityState = PersistentDeviceIdentityState.Revoked; _status = "Device revoked. Recovery requires an explicit host decision."; }
        catch { _status = "Operation failed. Any durably queued message is retained; unlock/rebind or retry when available. No sensitive error details are displayed."; }
        finally { _busy = false; }
    }
    public async ValueTask DisposeAsync()
    {
        await CloseSessionAsync();
        if (_crypto is not null) { await _crypto.DisposeAsync(); }
        _host?.Dispose();
        GC.SuppressFinalize(this);
    }
    private sealed class Applier(App app) : IChatEventApplier
    {
        public ValueTask ApplyAsync(ReceivedChatContent delivery, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (app._chat?.ConversationId == delivery.ConversationId) { app._chat.Apply(delivery); }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class DisabledDefaultSender : IChatContentSender
    {
        public ValueTask<ChatContentSendResult> SendAsync(ConversationId conversationId, ChatContent content, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ChatContentSendResult.Failed);
    }
}
