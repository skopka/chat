using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Skopka.Chat.ServerTrusted;

/// <summary>What changed in a conversation.</summary>
public enum ServerTrustedChatSignalKind
{
    /// <summary>Messages were created, edited, deleted, hidden or erased; re-read changes since the last revision.</summary>
    MessagesChanged = 1,

    /// <summary>A participant's read marker advanced.</summary>
    ReadMarkerChanged = 2,

    /// <summary>Status or participants changed.</summary>
    ConversationChanged = 3,
}

/// <summary>
/// A content-free change notification. Subscribers must re-read through <see cref="ServerTrustedChatService"/>, so
/// every displayed item passes the same authorization as an ordinary read.
/// </summary>
public readonly record struct ServerTrustedChatSignal(
    TrustedConversationId ConversationId,
    ServerTrustedChatSignalKind Kind,
    long ChangeRevision,
    Guid? UserId = null,
    long? ReadThroughSequence = null);

/// <summary>Receives signals after commits (the engine) or from another process (a bridge).</summary>
public interface IServerTrustedChatSignalSink
{
    /// <summary>Publishes without blocking; slow subscribers drop the oldest signals.</summary>
    void Publish(ServerTrustedChatSignal signal);
}

/// <summary>Source of live signals for components in this process.</summary>
public interface IServerTrustedChatLiveSignals
{
    /// <summary>Streams signals for one conversation, or for all conversations when <paramref name="conversationId"/> is null.</summary>
    IAsyncEnumerable<ServerTrustedChatSignal> SubscribeAsync(TrustedConversationId? conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process hub with a bounded drop-oldest channel per subscriber. Losing a signal is safe because subscribers
/// catch up from their last revision on the next signal or on their periodic refresh.
/// </summary>
public sealed class InProcessServerTrustedChatSignalHub : IServerTrustedChatSignalSink, IServerTrustedChatLiveSignals
{
    private const int SubscriberCapacity = 64;
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>Current subscriber count, for diagnostics and tests.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Publish(ServerTrustedChatSignal signal)
    {
        Subscription[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _subscriptions];
        }

        foreach (var subscription in snapshot)
        {
            if (subscription.ConversationId is null || subscription.ConversationId == signal.ConversationId)
            {
                subscription.Channel.Writer.TryWrite(signal);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ServerTrustedChatSignal> SubscribeAsync(
        TrustedConversationId? conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscription = new Subscription(
            conversationId,
            Channel.CreateBounded<ServerTrustedChatSignal>(new BoundedChannelOptions(SubscriberCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }));
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        try
        {
            while (await subscription.Channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (subscription.Channel.Reader.TryRead(out var signal))
                {
                    yield return signal;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscriptions.Remove(subscription);
            }
        }
    }

    private sealed record Subscription(TrustedConversationId? ConversationId, Channel<ServerTrustedChatSignal> Channel);
}
