using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage;

/// <summary>Outcome of atomically storing one authenticated content delivery.</summary>
public enum ChatEventStoreResult
{
    /// <summary>A new delivery was committed.</summary>
    Stored = 1,

    /// <summary>The exact delivery was already committed.</summary>
    Duplicate = 2,

    /// <summary>The delivery message ID was already committed with different authenticated data.</summary>
    Conflict = 3,
}

/// <summary>Raised when protected local event storage is unavailable or corrupt.</summary>
public sealed class ChatEventStorageException : Exception
{
    /// <summary>Creates a content-free storage failure.</summary>
    public ChatEventStorageException(string message) : base(message)
    {
    }

    /// <summary>Creates a content-free storage failure with its local provider cause.</summary>
    public ChatEventStorageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Durable local journal for authenticated, decrypted chat-content events.</summary>
/// <remarks>
/// Implementations store plaintext application content. The host is responsible for database encryption,
/// access control, backup, retention and secure deletion.
/// </remarks>
public interface IChatEventStore
{
    /// <summary>Atomically inserts a verified event or compares it with the existing delivery ID.</summary>
    ValueTask<ChatEventStoreResult> StoreAsync(
        ReceivedChatContent delivery,
        CancellationToken cancellationToken = default);

    /// <summary>Reads all committed events in stable local insertion order.</summary>
    IAsyncEnumerable<ReceivedChatContent> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one conversation's committed events in stable local insertion order.</summary>
    IAsyncEnumerable<ReceivedChatContent> ReadConversationAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Hard bounds for on-demand local history pages.</summary>
public static class ChatEventPagingLimits
{
    /// <summary>Largest page returned by a local history provider.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Largest provider-owned opaque cursor.</summary>
    public const int MaxCursorCharacters = 64;
}

/// <summary>One chronological page and an opaque cursor for the preceding page.</summary>
public sealed record ChatEventPage(
    IReadOnlyList<ReceivedChatContent> Items,
    string? PreviousCursor);

/// <summary>Optional bounded paging contract kept separate from the legacy event journal interface.</summary>
public interface IPagedChatEventStore
{
    /// <summary>
    /// Reads the newest page before an opaque cursor. Items are chronological so a UI can prepend them.
    /// </summary>
    ValueTask<ChatEventPage> ReadPreviousPageAsync(
        ConversationId conversationId,
        string? beforeCursor = null,
        int maximumCount = 50,
        CancellationToken cancellationToken = default);
}

/// <summary>Receives a durably committed event after storage and before server acknowledgement.</summary>
/// <remarks>Implementations must be idempotent because acknowledgement retries reapply duplicate deliveries.</remarks>
public interface IChatEventApplier
{
    /// <summary>Applies one authenticated event to host state.</summary>
    ValueTask ApplyAsync(ReceivedChatContent delivery, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe registry of in-memory conversation projections.</summary>
public sealed class ChatConversationProjectionRegistry : IChatEventApplier
{
    private readonly ConcurrentDictionary<ConversationId, ChatConversationProjection> _projections = new();

    /// <summary>Gets or creates the projection for a conversation.</summary>
    public ChatConversationProjection GetOrCreate(ConversationId conversationId)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        return _projections.GetOrAdd(conversationId, static id => new ChatConversationProjection(id));
    }

    /// <summary>Returns the currently materialized conversation identifiers in deterministic order.</summary>
    public IReadOnlyList<ConversationId> ConversationIds()
    {
        var ids = _projections.Keys.OrderBy(static item => item.Value).ToArray();
        return new ReadOnlyCollection<ConversationId>(ids);
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(ReceivedChatContent delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        cancellationToken.ThrowIfCancellationRequested();
        GetOrCreate(delivery.ConversationId).Apply(delivery);
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory event journal for tests and samples; it is not durable or protected storage.</summary>
public sealed class InMemoryChatEventStore : IChatEventStore, IPagedChatEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<MessageId, ReceivedChatContent> _deliveries = [];
    private readonly List<MessageId> _order = [];

    /// <summary>Number of distinct delivery IDs held in memory.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _deliveries.Count;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<ChatEventStoreResult> StoreAsync(
        ReceivedChatContent delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_deliveries.TryGetValue(delivery.DeliveryMessageId, out var existing))
            {
                return ValueTask.FromResult(ChatEventComparison.AreEquivalent(existing, delivery)
                    ? ChatEventStoreResult.Duplicate
                    : ChatEventStoreResult.Conflict);
            }

            _deliveries.Add(delivery.DeliveryMessageId, delivery);
            _order.Add(delivery.DeliveryMessageId);
            return ValueTask.FromResult(ChatEventStoreResult.Stored);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReceivedChatContent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var delivery in Snapshot(null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delivery;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReceivedChatContent> ReadConversationAsync(
        ConversationId conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        foreach (var delivery in Snapshot(conversationId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delivery;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public ValueTask<ChatEventPage> ReadPreviousPageAsync(
        ConversationId conversationId,
        string? beforeCursor = null,
        int maximumCount = 50,
        CancellationToken cancellationToken = default)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (maximumCount is < 1 or > ChatEventPagingLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var beforeSequence = DecodeCursor(beforeCursor);
        lock (_gate)
        {
            var matching = _order
                .Select((id, index) => new { Sequence = (long)index + 1, Delivery = _deliveries[id] })
                .Where(item =>
                    item.Delivery.ConversationId == conversationId &&
                    (!beforeSequence.HasValue || item.Sequence < beforeSequence.Value))
                .ToArray();
            var selected = matching.TakeLast(maximumCount).ToArray();
            var hasOlder = selected.Length > 0 && matching.Length > selected.Length;
            return ValueTask.FromResult(new ChatEventPage(
                selected.Select(item => item.Delivery).ToArray(),
                hasOlder ? EncodeCursor(selected[0].Sequence) : null));
        }
    }

    private ReceivedChatContent[] Snapshot(ConversationId? conversationId)
    {
        lock (_gate)
        {
            return _order
                .Select(id => _deliveries[id])
                .Where(item => !conversationId.HasValue || item.ConversationId == conversationId.Value)
                .ToArray();
        }
    }

    private static string EncodeCursor(long sequence)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, sequence);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static long? DecodeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        if (cursor.Length is 0 or > ChatEventPagingLimits.MaxCursorCharacters ||
            cursor.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException("The history cursor is invalid.", nameof(cursor));
        }

        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != 8)
            {
                throw new ArgumentException("The history cursor is invalid.", nameof(cursor));
            }

            var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes);
            if (sequence <= 0 || !string.Equals(EncodeCursor(sequence), cursor, StringComparison.Ordinal))
            {
                throw new ArgumentException("The history cursor is invalid.", nameof(cursor));
            }

            return sequence;
        }
        catch (FormatException)
        {
            throw new ArgumentException("The history cursor is invalid.", nameof(cursor));
        }
    }
}

internal static class ChatEventComparison
{
    internal static bool AreEquivalent(ReceivedChatContent left, ReceivedChatContent right)
    {
        if (left.DeliveryMessageId != right.DeliveryMessageId ||
            left.ConversationId != right.ConversationId ||
            left.SenderUserId != right.SenderUserId ||
            left.SenderDeviceId != right.SenderDeviceId ||
            left.SentAt != right.SentAt)
        {
            return false;
        }

        var leftContent = ChatContentEncoding.Encode(left.Content);
        var rightContent = ChatContentEncoding.Encode(right.Content);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftContent, rightContent);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftContent);
            CryptographicOperations.ZeroMemory(rightContent);
        }
    }
}
