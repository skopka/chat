using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
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
public sealed class InMemoryChatEventStore : IChatEventStore
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
