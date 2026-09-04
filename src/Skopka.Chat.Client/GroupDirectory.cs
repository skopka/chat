using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Authenticated server-visible role in a small group.</summary>
public enum ChatGroupRole : byte
{
    /// <summary>Ordinary participant.</summary>
    Member = 1,

    /// <summary>May rename the group, manage ordinary members and use <c>@all</c>.</summary>
    Administrator = 2,

    /// <summary>Permanent initial owner.</summary>
    Owner = 3,
}

/// <summary>One current participant returned by the authenticated group directory.</summary>
public sealed record ChatGroupMemberInfo(UserId UserId, ChatGroupRole Role, DateTimeOffset JoinedAt)
{
    /// <summary>Whether this participant may use a structured <c>@all</c> mention.</summary>
    public bool CanMentionEveryone => Role is ChatGroupRole.Owner or ChatGroupRole.Administrator;
}

/// <summary>Current server-visible group metadata; message text and mentions remain encrypted.</summary>
public sealed class ChatGroupConversationInfo
{
    private readonly ChatGroupMemberInfo[] _members;

    /// <summary>Creates trusted directory metadata after transport validation.</summary>
    public ChatGroupConversationInfo(
        ConversationId conversationId,
        string title,
        UserId createdByUserId,
        long revision,
        DateTimeOffset createdAt,
        IReadOnlyCollection<ChatGroupMemberInfo> members)
    {
        if (conversationId.Value == Guid.Empty || createdByUserId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(title) || revision < 1 || createdAt == default)
        {
            throw new ArgumentException("Group directory metadata is invalid.");
        }

        ArgumentNullException.ThrowIfNull(members);
        _members = members.OrderBy(static member => member.UserId.Value).ToArray();
        if (_members.Length is < 1 or > 64 ||
            _members.Select(static member => member.UserId).Distinct().Count() != _members.Length ||
            _members.Any(static member => member.UserId.Value == Guid.Empty || member.JoinedAt == default ||
                member.Role is < ChatGroupRole.Member or > ChatGroupRole.Owner) ||
            _members.Count(static member => member.Role == ChatGroupRole.Owner) != 1 ||
            _members.Single(static member => member.Role == ChatGroupRole.Owner).UserId != createdByUserId)
        {
            throw new ArgumentException("Group member directory metadata is invalid.", nameof(members));
        }

        ConversationId = conversationId;
        Title = title;
        CreatedByUserId = createdByUserId;
        Revision = revision;
        CreatedAt = createdAt;
        Members = Array.AsReadOnly(_members);
    }

    /// <summary>Group conversation ID.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Server-visible title.</summary>
    public string Title { get; }

    /// <summary>Permanent owner identity.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Monotonic metadata revision.</summary>
    public long Revision { get; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Current active members.</summary>
    public IReadOnlyList<ChatGroupMemberInfo> Members { get; }

    /// <summary>Gets one current participant.</summary>
    public ChatGroupMemberInfo? FindMember(UserId userId) =>
        _members.FirstOrDefault(member => member.UserId == userId);

    /// <summary>Returns whether the sender may produce an effective <c>@all</c> mention.</summary>
    public bool CanMentionEveryone(UserId senderUserId) => FindMember(senderUserId)?.CanMentionEveryone == true;

    /// <summary>
    /// Evaluates structured mention semantics against authenticated sender and current membership metadata.
    /// Unauthorized <c>@all</c> values are ignored; direct user targets require current membership.
    /// </summary>
    public bool IsEffectivelyMentioned(ChatTextContent content, UserId senderUserId, UserId recipientUserId)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (FindMember(senderUserId) is null || FindMember(recipientUserId) is null)
        {
            return false;
        }

        return content.Mentions.Any(mention => mention.Kind switch
        {
            ChatMentionKind.User => mention.UserId == recipientUserId,
            ChatMentionKind.Everyone => CanMentionEveryone(senderUserId),
            _ => false,
        });
    }
}

/// <summary>Bounded group directory page.</summary>
public sealed record ChatGroupConversationPage(
    IReadOnlyList<ChatGroupConversationInfo> Items,
    string? NextCursor);

/// <summary>Transport-independent authenticated small-group directory.</summary>
public interface IChatGroupConversationDirectory
{
    /// <summary>Creates a group owned by the authenticated user.</summary>
    ValueTask<ChatGroupConversationInfo> CreateGroupConversationAsync(
        ConversationId conversationId,
        string title,
        IReadOnlyCollection<UserId> memberUserIds,
        CancellationToken cancellationToken = default);

    /// <summary>Gets current metadata for one group the caller belongs to.</summary>
    ValueTask<ChatGroupConversationInfo> GetGroupConversationAsync(
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists groups containing the authenticated user.</summary>
    ValueTask<ChatGroupConversationPage> ListGroupConversationsAsync(
        string? cursor = null,
        int maximumCount = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a group at the supplied metadata revision.</summary>
    ValueTask<ChatGroupConversationInfo> RenameGroupConversationAsync(
        ConversationId conversationId,
        string title,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Adds one ordinary member at the supplied metadata revision.</summary>
    ValueTask<ChatGroupConversationInfo> AddGroupMemberAsync(
        ConversationId conversationId,
        UserId userId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a member or leaves the group at the supplied metadata revision.</summary>
    ValueTask<ChatGroupConversationInfo> RemoveGroupMemberAsync(
        ConversationId conversationId,
        UserId userId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Assigns Member or Administrator; server authorization requires the owner.</summary>
    ValueTask<ChatGroupConversationInfo> ChangeGroupMemberRoleAsync(
        ConversationId conversationId,
        UserId userId,
        ChatGroupRole role,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
