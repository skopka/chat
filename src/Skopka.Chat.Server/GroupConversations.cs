using System.Text;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>Bounds the first small-group implementation.</summary>
public static class GroupConversationLimits
{
    /// <summary>Largest active participant set, including the owner.</summary>
    public const int MaxMembers = 64;

    /// <summary>Largest UTF-8 encoded server-visible group title.</summary>
    public const int MaxTitleUtf8Bytes = 256;
}

/// <summary>Server-visible authorization role of an active group participant.</summary>
public enum GroupConversationRole : byte
{
    /// <summary>Can read and send future group traffic.</summary>
    Member = 1,

    /// <summary>Can rename the group and manage ordinary members.</summary>
    Administrator = 2,

    /// <summary>Permanent initial owner; can also assign administrators.</summary>
    Owner = 3,
}

/// <summary>One active participant in a group conversation.</summary>
public sealed record GroupConversationMember(UserId UserId, GroupConversationRole Role, DateTimeOffset JoinedAt)
{
    /// <summary>Returns whether this member may use the structured <c>@all</c> mention.</summary>
    public bool CanMentionEveryone => Role is GroupConversationRole.Owner or GroupConversationRole.Administrator;
}

/// <summary>Server-visible small-group metadata; message content and mentions remain encrypted.</summary>
public sealed class GroupConversation : IEquatable<GroupConversation>
{
    private readonly GroupConversationMember[] _members;

    /// <summary>Creates validated group metadata.</summary>
    public GroupConversation(
        ConversationId conversationId,
        string title,
        UserId createdByUserId,
        long revision,
        DateTimeOffset createdAt,
        IReadOnlyCollection<GroupConversationMember> members)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (createdByUserId.Value == Guid.Empty)
        {
            throw new ArgumentException("Creator user ID must not be empty.", nameof(createdByUserId));
        }

        Title = NormalizeTitle(title);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        if (createdAt == default)
        {
            throw new ArgumentException("Creation time must be set.", nameof(createdAt));
        }

        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1 or > GroupConversationLimits.MaxMembers)
        {
            throw new ArgumentOutOfRangeException(nameof(members));
        }

        _members = members.OrderBy(static member => member.UserId.Value).ToArray();
        if (_members.Select(static member => member.UserId).Distinct().Count() != _members.Length ||
            _members.Any(static member => member.UserId.Value == Guid.Empty || member.JoinedAt == default ||
                member.Role is < GroupConversationRole.Member or > GroupConversationRole.Owner))
        {
            throw new ArgumentException("Group members are invalid or duplicated.", nameof(members));
        }

        var owners = _members.Where(static member => member.Role == GroupConversationRole.Owner).ToArray();
        if (owners.Length != 1 || owners[0].UserId != createdByUserId)
        {
            throw new ArgumentException("A group must retain exactly one initial owner.", nameof(members));
        }

        ConversationId = conversationId;
        CreatedByUserId = createdByUserId;
        Revision = revision;
        CreatedAt = createdAt;
        Members = Array.AsReadOnly(_members);
    }

    /// <summary>Opaque group conversation ID.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Server-visible display title.</summary>
    public string Title { get; }

    /// <summary>Permanent owner identity.</summary>
    public UserId CreatedByUserId { get; }

    /// <summary>Monotonic metadata revision used for optimistic writes and recipient refresh.</summary>
    public long Revision { get; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Current active members in stable user-ID order.</summary>
    public IReadOnlyList<GroupConversationMember> Members { get; }

    /// <summary>Gets one active member, if present.</summary>
    public GroupConversationMember? FindMember(UserId userId) =>
        _members.FirstOrDefault(member => member.UserId == userId);

    /// <summary>Returns whether the user currently participates.</summary>
    public bool Contains(UserId userId) => FindMember(userId) is not null;

    /// <summary>Creates a title update and increments the metadata revision.</summary>
    public GroupConversation WithTitle(string title) =>
        new(ConversationId, title, CreatedByUserId, checked(Revision + 1), CreatedAt, _members);

    /// <summary>Adds a current member and increments the metadata revision.</summary>
    public GroupConversation AddMember(UserId userId, DateTimeOffset joinedAt)
    {
        if (userId.Value == Guid.Empty || joinedAt == default)
        {
            throw new ArgumentException("Group member data is invalid.");
        }

        if (Contains(userId))
        {
            throw new ChatServerException("The user is already a group member.");
        }

        if (_members.Length >= GroupConversationLimits.MaxMembers)
        {
            throw new ChatServerException("The group member limit was reached.");
        }

        return new GroupConversation(
            ConversationId,
            Title,
            CreatedByUserId,
            checked(Revision + 1),
            CreatedAt,
            [.. _members, new GroupConversationMember(userId, GroupConversationRole.Member, joinedAt)]);
    }

    /// <summary>Removes a non-owner current member and increments the metadata revision.</summary>
    public GroupConversation RemoveMember(UserId userId)
    {
        var member = FindMember(userId) ?? throw new ChatServerException("The user is not a group member.");
        if (member.Role == GroupConversationRole.Owner)
        {
            throw new ChatServerException("The permanent group owner cannot be removed.");
        }

        return new GroupConversation(
            ConversationId,
            Title,
            CreatedByUserId,
            checked(Revision + 1),
            CreatedAt,
            _members.Where(item => item.UserId != userId).ToArray());
    }

    /// <summary>Assigns Member or Administrator to a non-owner and increments the metadata revision.</summary>
    public GroupConversation ChangeRole(UserId userId, GroupConversationRole role)
    {
        if (role is not GroupConversationRole.Member and not GroupConversationRole.Administrator)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        var member = FindMember(userId) ?? throw new ChatServerException("The user is not a group member.");
        if (member.Role == GroupConversationRole.Owner)
        {
            throw new ChatServerException("The permanent group owner role cannot be changed.");
        }

        return new GroupConversation(
            ConversationId,
            Title,
            CreatedByUserId,
            checked(Revision + 1),
            CreatedAt,
            _members.Select(item => item.UserId == userId ? item with { Role = role } : item).ToArray());
    }

    /// <inheritdoc />
    public bool Equals(GroupConversation? other) =>
        other is not null &&
        ConversationId == other.ConversationId &&
        string.Equals(Title, other.Title, StringComparison.Ordinal) &&
        CreatedByUserId == other.CreatedByUserId &&
        Revision == other.Revision &&
        CreatedAt == other.CreatedAt &&
        _members.SequenceEqual(other._members);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GroupConversation other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ConversationId, Title, CreatedByUserId, Revision, CreatedAt);

    /// <inheritdoc />
    public override string ToString() =>
        $"GroupConversation(ConversationId={ConversationId}, Revision={Revision}, Members={Members.Count}, Title=[REDACTED])";

    internal static string NormalizeTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var normalized = title.Trim();
        if (normalized.Any(char.IsControl) || Encoding.UTF8.GetByteCount(normalized) > GroupConversationLimits.MaxTitleUtf8Bytes)
        {
            throw new ArgumentException("Group title is invalid or too long.", nameof(title));
        }

        return normalized;
    }
}

/// <summary>One bounded page of groups visible to the authenticated user.</summary>
public sealed record GroupConversationDirectoryPage(
    IReadOnlyList<GroupConversation> Items,
    ConversationDirectoryCursor? NextCursor);

/// <summary>Atomic metadata update outcome.</summary>
public enum GroupConversationStoreResult
{
    /// <summary>The requested aggregate was stored.</summary>
    Updated,

    /// <summary>The expected revision is stale or the group no longer exists.</summary>
    Conflict,
}

/// <summary>Group metadata persistence boundary.</summary>
public interface IGroupConversationRepository
{
    /// <summary>Creates a group if its ID is unused.</summary>
    ValueTask<bool> TryAddAsync(GroupConversation conversation, CancellationToken cancellationToken = default);

    /// <summary>Gets current group metadata by ID.</summary>
    ValueTask<GroupConversation?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>Lists groups containing the authenticated user in stable order.</summary>
    ValueTask<GroupConversationDirectoryPage> ListForUserAsync(
        UserId userId,
        ConversationDirectoryCursor? cursor,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the aggregate only at the expected revision.</summary>
    ValueTask<GroupConversationStoreResult> TryReplaceAsync(
        GroupConversation conversation,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}
