using Microsoft.Extensions.Options;
using Microsoft.Maui.Storage;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Client.Maui;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Media;
using Skopka.Chat.Protocol;
using Skopka.Chat.UI;

namespace Skopka.Chat.Maui.Sample;

public sealed class SampleChatSessionFactory(ISampleAuthenticationProvider authentication)
{
    public async ValueTask<SampleChatContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        var authenticated = await authentication.AuthenticateAsync(cancellationToken);
        if (!authenticated.ServerBaseAddress.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The sample requires HTTPS outside an explicitly isolated test host.");
        }

        var keyStore = new SecureStorageDeviceKeyStore(SecureStorage.Default, authenticated.UserId);
        var identityService = new DeviceIdentityService(keyStore);
        var httpClient = new HttpClient { BaseAddress = authenticated.ServerBaseAddress };
        var transport = new SkopkaChatHttpClient(
            httpClient,
            new FixedAccessTokenProvider(authenticated.AccessToken),
            Options.Create(new SkopkaChatHttpClientOptions
            {
                AuthenticatedUserId = authenticated.UserId.Value,
                AuthenticatedDeviceId = authenticated.DeviceId.Value,
            }),
            TimeProvider.System);
        var registered = await transport.GetDeviceAsync(authenticated.DeviceId, cancellationToken);
        var local = await identityService.LoadPublicAsync(
            authenticated.UserId,
            authenticated.DeviceId,
            registered?.RegisteredAt ?? TimeProvider.System.GetUtcNow(),
            cancellationToken);
        if (local is null)
        {
            if (registered is not null)
            {
                throw new InvalidOperationException("Server device exists but its local private identity is unavailable.");
            }

            local = await identityService.CreateAsync(
                authenticated.UserId,
                authenticated.DeviceId,
                TimeProvider.System.GetUtcNow(),
                cancellationToken);
            registered = await transport.RegisterDeviceAsync(local, cancellationToken);
        }
        else if (registered is null)
        {
            registered = await transport.RegisterDeviceAsync(local, cancellationToken);
        }

        EnsureSameIdentity(local, registered);
        var conversation = await transport.GetOrCreatePersonalConversationAsync(
            authenticated.PeerUserId,
            cancellationToken);
        var sessionStem = $"{authenticated.UserId.Value:N}-{authenticated.DeviceId.Value:N}";
        var eventStore = new SqliteChatEventStore(
            $"Data Source={Path.Combine(FileSystem.Current.AppDataDirectory, $"chat-{sessionStem}.db")};Pooling=False");
        var outbox = new SqliteChatOutboxStore(
            $"Data Source={Path.Combine(FileSystem.Current.AppDataDirectory, $"outbox-{sessionStem}.db")};Pooling=False");
        var applier = new SampleConversationApplier(conversation.ConversationId);
        var sync = new ChatSyncCoordinator(
            transport,
            new ChatCryptoService(keyStore),
            eventStore,
            applier,
            authenticated.DeviceId,
            restoreAllHistory: false);
        var multiDevice = new ChatMultiDeviceSender(
            authenticated.UserId,
            authenticated.DeviceId,
            new ChatCryptoService(keyStore),
            transport,
            transport,
            outbox);
        var viewModel = new ChatViewModel(
            conversation.ConversationId,
            authenticated.UserId,
            new MultiDeviceChatContentSender(multiDevice, sync));
        applier.Attach(viewModel);
        var history = new ChatHistoryPager(eventStore, applier, conversation.ConversationId, pageSize: 50);
        await history.LoadInitialAsync(cancellationToken);
        var outboxDispatcher = new ChatOutboxDispatcher(outbox, transport);
        var lifecycle = new MauiChatLifecycleCoordinator(sync, outboxDispatcher);
        await lifecycle.StartAsync(cancellationToken);
        var session = new MauiChatSession(
            new MauiChatSessionIdentity(authenticated.UserId, authenticated.DeviceId),
            lifecycle,
            [history, eventStore, outbox, httpClient]);
        return new SampleChatContext(
            session,
            viewModel,
            history,
            transport,
            conversation.ConversationId,
            new MauiProtectedFileService(FilePicker.Default, FileSystem.Current));
    }

    private static void EnsureSameIdentity(PublicDevice local, PublicDevice registered)
    {
        if (local.UserId != registered.UserId || local.DeviceId != registered.DeviceId || local.KeyId != registered.KeyId ||
            !local.EncryptionPublicKey.Span.SequenceEqual(registered.EncryptionPublicKey.Span) ||
            !local.SigningPublicKey.Span.SequenceEqual(registered.SigningPublicKey.Span))
        {
            throw new InvalidOperationException("The registered device identity changed and requires host recovery.");
        }
    }
}

public sealed class SampleChatContext : IAsyncDisposable
{
    private const long MaximumSampleAttachmentBytes = 25 * 1024 * 1024;
    private readonly MauiChatSession _session;
    private readonly SkopkaChatHttpClient _transport;
    private readonly ConversationId _conversationId;
    private readonly MauiProtectedFileService _files;

    internal SampleChatContext(
        MauiChatSession session,
        ChatViewModel viewModel,
        ChatHistoryPager history,
        SkopkaChatHttpClient transport,
        ConversationId conversationId,
        MauiProtectedFileService files)
    {
        _session = session;
        ViewModel = viewModel;
        History = new SampleHistoryCallbacks(history);
        _transport = transport;
        _conversationId = conversationId;
        _files = files;
    }

    public ChatViewModel ViewModel { get; }
    public MauiChatLifecycleCoordinator Lifecycle => _session.Lifecycle;
    public SampleHistoryCallbacks History { get; }

    public async ValueTask<ChatAttachmentContent?> PickEncryptAndUploadAsync(CancellationToken cancellationToken)
    {
        ChatAttachmentContent? manifest = null;
        await _files.PickAndUseAsync(
            new PickOptions { PickerTitle = "Choose an attachment" },
            MaximumSampleAttachmentBytes,
            async (file, token) =>
            {
                await using var source = await file.OpenReadAsync(token);
                await using var ciphertext = new MemoryStream();
                var service = new ChatMediaAttachmentService(new PassthroughMediaPreparationService(), _transport);
                manifest = await service.PrepareEncryptAndUploadAsync(
                    _conversationId,
                    new ChatMediaAttachmentRequest(new MediaPreparationRequest(
                        source,
                        file.Length,
                        file.FileName,
                        file.MediaType,
                        MediaSendMode.File)),
                    ciphertext,
                    cancellationToken: token);
            },
            cancellationToken);
        return manifest;
    }

    public ValueTask DownloadAuthenticatedAsync(
        ProjectedChatAttachment attachment,
        Func<MauiPrivatePlaintextFile, CancellationToken, ValueTask> useAsync,
        CancellationToken cancellationToken) =>
        _files.UseDecryptedAsync(
            attachment.FileName,
            attachment.MediaType,
            attachment.PlaintextLength,
            (destination, token) => _transport.DownloadAndDecryptAttachmentAsync(
                _conversationId,
                attachment.Manifest,
                destination,
                token),
            useAsync,
            cancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

public sealed class SampleHistoryCallbacks(ChatHistoryPager pager)
{
    public async ValueTask LoadPreviousAsyncAsCallback(CancellationToken cancellationToken) =>
        _ = await pager.LoadPreviousAsync(cancellationToken);
}

internal sealed class SampleConversationApplier(ConversationId conversationId) : IChatEventApplier
{
    private ChatViewModel? _viewModel;

    internal void Attach(ChatViewModel viewModel) => _viewModel = viewModel;

    public ValueTask ApplyAsync(ReceivedChatContent delivery, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delivery.ConversationId == conversationId)
        {
            (_viewModel ?? throw new InvalidOperationException("The sample projection is not attached.")).Apply(delivery);
        }

        return ValueTask.CompletedTask;
    }
}
