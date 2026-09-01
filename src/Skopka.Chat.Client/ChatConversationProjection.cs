using System.Collections.ObjectModel;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Outcome of applying authenticated content to a conversation projection.</summary>
public enum ChatProjectionApplyResult
{
    /// <summary>The event changed projected state.</summary>
    Applied = 1,

    /// <summary>The same authenticated logical event was already present.</summary>
    Duplicate = 2,

    /// <summary>The content ID was reused with conflicting authenticated data and is excluded.</summary>
    Conflict = 3,
}

/// <summary>One active reaction token and the authenticated users who selected it.</summary>
public sealed class ProjectedChatReaction
{
    private readonly ReadOnlyCollection<UserId> _senderUserIds;

    internal ProjectedChatReaction(string reaction, IEnumerable<UserId> senderUserIds)
    {
        Reaction = reaction;
        _senderUserIds = Array.AsReadOnly(senderUserIds.ToArray());
    }

    /// <summary>Decrypted rendering token.</summary>
    public string Reaction { get; }

    /// <summary>Authenticated users with an active matching reaction.</summary>
    public IReadOnlyList<UserId> SenderUserIds => _senderUserIds;

    /// <summary>Number of authenticated users with this active reaction.</summary>
    public int Count => _senderUserIds.Count;

    /// <inheritdoc />
    public override string ToString() => $"ProjectedChatReaction(Count={Count}, Reaction=[REDACTED])";
}

/// <summary>One projected text item with reply, forward and active-reaction state.</summary>
public sealed class ProjectedChatMessage
{
    private readonly ReadOnlyCollection<ProjectedChatReaction> _reactions;

    internal ProjectedChatMessage(ReceivedChatContent delivery, ChatTextContent content, IEnumerable<ProjectedChatReaction> reactions)
    {
        ContentId = content.ContentId;
        DeliveryMessageId = delivery.DeliveryMessageId;
        SenderUserId = delivery.SenderUserId;
        SenderDeviceId = delivery.SenderDeviceId;
        SentAt = delivery.SentAt;
        Text = content.Text;
        ReplyToContentId = content.ReplyToContentId;
        IsForwarded = content.IsForwarded;
        _reactions = Array.AsReadOnly(reactions.ToArray());
    }

    /// <summary>Logical content identifier used by replies and reactions.</summary>
    public ChatContentId ContentId { get; }

    /// <summary>Recipient-specific envelope id that supplied this local projection.</summary>
    public MessageId DeliveryMessageId { get; }

    /// <summary>Authenticated sending user.</summary>
    public UserId SenderUserId { get; }

    /// <summary>Authenticated signing device.</summary>
    public DeviceId SenderDeviceId { get; }

    /// <summary>Sender-supplied timestamp authenticated by the envelope.</summary>
    public DateTimeOffset SentAt { get; }

    /// <summary>Decrypted message text.</summary>
    public string Text { get; }

    /// <summary>Referenced logical content, including when it is absent from this projection.</summary>
    public ChatContentId? ReplyToContentId { get; }

    /// <summary>Sender assertion that this text was forwarded; not proof of its original author.</summary>
    public bool IsForwarded { get; }

    /// <summary>Active reactions grouped by rendering token.</summary>
    public IReadOnlyList<ProjectedChatReaction> Reactions => _reactions;

    /// <inheritdoc />
    public override string ToString() =>
        $"ProjectedChatMessage(ContentId={ContentId}, Reactions={Reactions.Count}, Text=[REDACTED])";
}

/// <summary>
/// Thread-safe in-memory reducer for authenticated text and reaction events in one conversation.
/// Hosts remain responsible for protected durable local history.
/// </summary>
public sealed class ChatConversationProjection
{
    private readonly object _gate = new();
    private readonly Dictionary<ChatContentId, ReceivedChatContent> _events = [];
    private readonly HashSet<ChatContentId> _conflictedContentIds = [];
    private readonly Dictionary<ChatContentId, ReceivedChatContent> _texts = [];
    private readonly Dictionary<ReactionKey, ReceivedChatContent> _reactionStates = [];

    /// <summary>Creates an empty projection for exactly one conversation.</summary>
    public ChatConversationProjection(ConversationId conversationId)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        ConversationId = conversationId;
    }

    /// <summary>Conversation accepted by this projection.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>
    /// Applies verified content. Reactions may arrive before their target and become visible when it arrives.
    /// </summary>
    public ChatProjectionApplyResult Apply(ReceivedChatContent delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.ConversationId != ConversationId)
        {
            throw new ArgumentException("Content belongs to another conversation.", nameof(delivery));
        }

        lock (_gate)
        {
            var contentId = delivery.Content.ContentId;
            if (_conflictedContentIds.Contains(contentId))
            {
                return ChatProjectionApplyResult.Conflict;
            }

            if (_events.TryGetValue(contentId, out var existing))
            {
                if (IsSameEvent(existing, delivery))
                {
                    return ChatProjectionApplyResult.Duplicate;
                }

                _events.Remove(contentId);
                _conflictedContentIds.Add(contentId);
                Rebuild();
                return ChatProjectionApplyResult.Conflict;
            }

            _events.Add(contentId, delivery);
            ApplyCore(delivery);
            return ChatProjectionApplyResult.Applied;
        }
    }

    /// <summary>Returns an immutable, deterministically ordered view of current text and reactions.</summary>
    public IReadOnlyList<ProjectedChatMessage> Snapshot()
    {
        lock (_gate)
        {
            var messages = _texts.Values
                .OrderBy(static item => item.SentAt)
                .ThenBy(static item => item.Content.ContentId.Value)
                .Select(CreateProjectedMessage)
                .ToArray();
            return Array.AsReadOnly(messages);
        }
    }

    /// <summary>Returns logical IDs excluded after conflicting authenticated reuse.</summary>
    public IReadOnlyList<ChatContentId> ConflictedContentIds()
    {
        lock (_gate)
        {
            var identifiers = _conflictedContentIds.OrderBy(static item => item.Value).ToArray();
            return Array.AsReadOnly(identifiers);
        }
    }

    private ProjectedChatMessage CreateProjectedMessage(ReceivedChatContent delivery)
    {
        var text = (ChatTextContent)delivery.Content;
        var reactions = _reactionStates.Values
            .Where(item => ((ChatReactionContent)item.Content).TargetContentId == text.ContentId)
            .Where(item => ((ChatReactionContent)item.Content).Operation == ChatReactionOperation.Add)
            .GroupBy(item => ((ChatReactionContent)item.Content).Reaction, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new ProjectedChatReaction(
                group.Key,
                group.Select(static item => item.SenderUserId).OrderBy(static item => item.Value)))
            .ToArray();
        return new ProjectedChatMessage(delivery, text, reactions);
    }

    private void ApplyCore(ReceivedChatContent delivery)
    {
        switch (delivery.Content)
        {
            case ChatTextContent:
                _texts.Add(delivery.Content.ContentId, delivery);
                break;
            case ChatReactionContent reaction:
                var key = new ReactionKey(reaction.TargetContentId, delivery.SenderUserId, reaction.Reaction);
                if (!_reactionStates.TryGetValue(key, out var existing) || CompareEventOrder(existing, delivery) < 0)
                {
                    _reactionStates[key] = delivery;
                }

                break;
            default:
                throw new InvalidOperationException("Unsupported chat content type.");
        }
    }

    private void Rebuild()
    {
        _texts.Clear();
        _reactionStates.Clear();
        foreach (var delivery in _events.Values)
        {
            ApplyCore(delivery);
        }
    }

    private static int CompareEventOrder(ReceivedChatContent left, ReceivedChatContent right)
    {
        var timestamp = left.SentAt.CompareTo(right.SentAt);
        return timestamp != 0
            ? timestamp
            : left.Content.ContentId.Value.CompareTo(right.Content.ContentId.Value);
    }

    private static bool IsSameEvent(ReceivedChatContent left, ReceivedChatContent right)
    {
        if (left.ConversationId != right.ConversationId ||
            left.SenderUserId != right.SenderUserId ||
            left.SenderDeviceId != right.SenderDeviceId ||
            left.SentAt != right.SentAt ||
            left.Content.Kind != right.Content.Kind)
        {
            return false;
        }

        return (left.Content, right.Content) switch
        {
            (ChatTextContent first, ChatTextContent second) =>
                first.Text == second.Text &&
                first.ReplyToContentId == second.ReplyToContentId &&
                first.IsForwarded == second.IsForwarded,
            (ChatReactionContent first, ChatReactionContent second) =>
                first.TargetContentId == second.TargetContentId &&
                first.Reaction == second.Reaction &&
                first.Operation == second.Operation,
            _ => false,
        };
    }

    private readonly record struct ReactionKey(
        ChatContentId TargetContentId,
        UserId SenderUserId,
        string Reaction);
}
