using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Browser;

/// <summary>Encrypted, atomic verified-event journal. History is paged by stable local insertion sequence.</summary>
public sealed class BrowserChatEventStore(BrowserVault vault) : IChatEventStore, IPagedChatEventStore
{
    private readonly BrowserVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));

    /// <inheritdoc />
    public async ValueTask<ChatEventStoreResult> StoreAsync(ReceivedChatContent delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var record = BrowserEventRecord.FromDomain(delivery);
        var bytes = BrowserStoreEncoding.Encode(record, BrowserStoreJson.Default.BrowserEventRecord);
        try
        {
            var key = BrowserStoreEncoding.Id(delivery.DeliveryMessageId.Value);
            if (await _vault.WriteAsync("events", key, BrowserStoreEncoding.Id(delivery.ConversationId.Value), bytes, null, cancellationToken).ConfigureAwait(false))
            { return ChatEventStoreResult.Stored; }
            var existing = await _vault.ReadAsync("events", key, cancellationToken).ConfigureAwait(false);
            try
            {
                return existing.Data is not null && CryptographicOperations.FixedTimeEquals(existing.Data, bytes)
                    ? ChatEventStoreResult.Duplicate : ChatEventStoreResult.Conflict;
            }
            finally { if (existing.Data is not null) { CryptographicOperations.ZeroMemory(existing.Data); } }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(record.Content); }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ReceivedChatContent> ReadAllAsync(CancellationToken cancellationToken = default) => ReadAsync(null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ReceivedChatContent> ReadConversationAsync(ConversationId conversationId, CancellationToken cancellationToken = default) =>
        ReadAsync(BrowserStoreEncoding.Id(conversationId.Value), cancellationToken);

    /// <summary>Reads a bounded previous insertion page; each returned page is sorted by sender time and delivery ID for display.</summary>
    public async ValueTask<ChatEventPage> ReadPreviousPageAsync(ConversationId conversationId, string? beforeCursor = null,
        int maximumCount = 50, CancellationToken cancellationToken = default)
    {
        long before = 9_007_199_254_740_991;
        if (beforeCursor is not null && (beforeCursor.Length > 16 || !long.TryParse(beforeCursor, NumberStyles.None, CultureInfo.InvariantCulture, out before) || before <= 0 || before > 9_007_199_254_740_991))
        { throw new ArgumentException("History cursor is invalid.", nameof(beforeCursor)); }
        var rows = await _vault.PageAsync("events", BrowserStoreEncoding.Id(conversationId.Value), before, 0, maximumCount, cancellationToken).ConfigureAwait(false);
        var result = new List<ReceivedChatContent>(rows.Length);
        foreach (var row in rows)
        {
            var item = await ReadEventAsync(row.Key, cancellationToken).ConfigureAwait(false);
            if (item.ConversationId != conversationId) { throw new BrowserStorageException("corrupt"); }
            result.Add(item);
        }
        return new(result.OrderBy(item => item.SentAt).ThenBy(item => item.DeliveryMessageId.Value).ToArray(),
            rows.Length == maximumCount ? rows[^1].Sequence.ToString(CultureInfo.InvariantCulture) : null);
    }

    private async IAsyncEnumerable<ReceivedChatContent> ReadAsync(string? partition, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long after = 0;
        while (true)
        {
            var rows = await _vault.PageAsync("events", partition, 0, after, 50, cancellationToken).ConfigureAwait(false);
            if (rows.Length == 0) { yield break; }
            foreach (var row in rows)
            {
                var item = await ReadEventAsync(row.Key, cancellationToken).ConfigureAwait(false);
                if (partition is not null && BrowserStoreEncoding.Id(item.ConversationId.Value) != partition) { throw new BrowserStorageException("corrupt"); }
                yield return item;
                after = row.Sequence;
            }
        }
    }
    private async ValueTask<ReceivedChatContent> ReadEventAsync(string key, CancellationToken cancellationToken)
    {
        var stored = await _vault.ReadAsync("events", key, cancellationToken).ConfigureAwait(false);
        var record = BrowserStoreEncoding.Decode(stored.Data, BrowserStoreJson.Default.BrowserEventRecord);
        try
        {
            if (BrowserStoreEncoding.Id(record.Message) != key || BrowserStoreEncoding.Id(record.Conversation) != stored.Partition) { throw new BrowserStorageException("corrupt"); }
            return record.ToDomain();
        }
        catch (Exception error) when (error is ArgumentException or FormatException) { throw new BrowserStorageException("corrupt"); }
        finally { CryptographicOperations.ZeroMemory(record.Content); }
    }
}
