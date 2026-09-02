using System.Security.Cryptography;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Browser;

/// <summary>Foreground browser session over shared sender/sync logic, durable pre-network jobs and a cross-tab delivery lease.</summary>
/// <remarks>Create only after successful enrollment/rebind. Logout cancels/awaits operations, but does not erase the supplied vault.</remarks>
public sealed class BrowserChatSession : IAsyncDisposable
{
    private readonly BrowserVault _vault;
    private readonly PublicDevice _device;
    private readonly ChatMultiDeviceSender _sender;
    private readonly ChatSyncCoordinator _sync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    /// <summary>Composes existing shared engine services with browser stores. Authentication and key trust remain host-owned.</summary>
    public BrowserChatSession(BrowserVault vault, PublicDevice device, IChatCryptographyProvider cryptography,
        IChatTransport transport, IRecipientDeviceDirectory directory, IChatEventApplier applier, TimeProvider? timeProvider = null,
        Func<Exception, bool>? isExpectedFailure = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        ProtocolValidator.Validate(device);
        if (device.UserId != vault.Scope.UserId || device.IsRevoked) { throw new ArgumentException("Device does not match the unlocked vault.", nameof(device)); }
        _device = device;
        var crypto = new ChatCryptoService(new BrowserDeviceIdentityStore(vault), cryptography);
        Events = new BrowserChatEventStore(vault);
        Outbox = new BrowserChatOutboxStore(vault);
        _sender = new ChatMultiDeviceSender(device.UserId, device.DeviceId, crypto, directory, transport, Outbox, timeProvider, isExpectedFailure);
        _sync = new ChatSyncCoordinator(transport, crypto, Events, applier, device.DeviceId, timeProvider, restoreAllHistory: false);
    }

    /// <summary>Encrypted verified history; use bounded paging for UI restoration.</summary>
    public BrowserChatEventStore Events { get; }
    /// <summary>Exact persisted recipient-specific ciphertext plans.</summary>
    public BrowserChatOutboxStore Outbox { get; }

    /// <summary>Durably queues canonical content before any directory lookup, encryption or network request.</summary>
    public async ValueTask QueueAsync(ConversationId conversationId, ChatContent content, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await QueueCoreAsync(conversationId, content, linked.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async ValueTask QueueCoreAsync(ConversationId conversationId, ChatContent content, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var record = new BrowserJobRecord(1, conversationId.Value, content.ContentId.Value, _device.UserId.Value, _device.DeviceId.Value, ChatContentEncoding.Encode(content));
        var bytes = BrowserStoreEncoding.Encode(record, BrowserStoreJson.Default.BrowserJobRecord);
        try
        {
            var key = JobKey(conversationId, content.ContentId);
            if (!await _vault.WriteAsync("jobs", key, "", bytes, null, linked.Token).ConfigureAwait(false))
            {
                var row = await _vault.ReadAsync("jobs", key, linked.Token).ConfigureAwait(false);
                try
                {
                    if (row.Data is null || !CryptographicOperations.FixedTimeEquals(row.Data, bytes)) { throw new BrowserStorageException("conflict"); }
                }
                finally { if (row.Data is not null) { CryptographicOperations.ZeroMemory(row.Data); } }
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(record.Body); }
    }

    /// <summary>Retries a bounded batch of queued jobs; completed jobs are removed only after durable local echo.</summary>
    /// <returns>Number of logical jobs completed. Network failures leave their original IDs, content and plans queued.</returns>
    public async ValueTask<int> DispatchAsync(int maximumCount = 20, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumCount is < 1 or > 100) { throw new ArgumentOutOfRangeException(nameof(maximumCount)); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await using var lease = await _vault.AcquireAsync("delivery", linked.Token).ConfigureAwait(false);
            var rows = await _vault.PageAsync("jobs", null, 0, 0, maximumCount, linked.Token).ConfigureAwait(false);
            var completed = 0;
            foreach (var row in rows)
            {
                var stored = await _vault.ReadAsync("jobs", row.Key, linked.Token).ConfigureAwait(false);
                var record = BrowserStoreEncoding.Decode(stored.Data, BrowserStoreJson.Default.BrowserJobRecord);
                try
                {
                    BrowserStoreEncoding.Version(record.Version);
                    var conversationId = new ConversationId(record.Conversation);
                    var content = ChatContentEncoding.Decode(record.Body);
                    if (record.User != _device.UserId.Value || record.Device != _device.DeviceId.Value || record.Content != content.ContentId.Value ||
                        JobKey(conversationId, content.ContentId) != row.Key) { throw new BrowserStorageException("corrupt"); }
                    var result = await _sender.SendAsync(conversationId, content, linked.Token).ConfigureAwait(false);
                    if (!result.Succeeded) { break; }
                    await _sync.CommitLocalEchoAsync(result.LocalEcho ?? throw new BrowserStorageException("corrupt"), linked.Token).ConfigureAwait(false);
                    await _vault.RemoveAsync("jobs", row.Key, stored.Revision, linked.Token).ConfigureAwait(false);
                    completed++;
                }
                finally { CryptographicOperations.ZeroMemory(record.Body); }
            }
            return completed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Authenticates/decrypts, atomically stores, idempotently applies and only then acknowledges.</summary>
    public async ValueTask<ChatSyncBatchResult> SynchronizeAsync(int maximumCount = 50, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await using var lease = await _vault.AcquireAsync("delivery", linked.Token).ConfigureAwait(false);
            return await _sync.SynchronizeAsync(maximumCount, linked.Token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Cancels and awaits foreground delivery. Host disposes the vault afterwards; local records survive logout.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _sync.Dispose(); }
        finally { _gate.Release(); _lifetime.Dispose(); }
        // Semaphore is not disposed while cancelled waiters may still be unwinding.
    }
    private static string JobKey(ConversationId conversationId, ChatContentId contentId) =>
        BrowserStoreEncoding.Id(conversationId.Value) + "-" + BrowserStoreEncoding.Id(contentId.Value);
}
