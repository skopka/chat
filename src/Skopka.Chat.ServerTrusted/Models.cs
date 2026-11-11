namespace Skopka.Chat.ServerTrusted;

/// <summary>Identifies one server-trusted conversation. It is never interchangeable with an E2EE conversation ID.</summary>
public readonly record struct TrustedConversationId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    public static TrustedConversationId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies one server-trusted message.</summary>
public readonly record struct TrustedMessageId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    public static TrustedMessageId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies one server-trusted attachment (the same value addresses its plaintext blob).</summary>
public readonly record struct TrustedAttachmentId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    public static TrustedAttachmentId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies one report (complaint) about a message.</summary>
public readonly record struct TrustedReportId(Guid Value)
{
    /// <summary>Creates a new identifier.</summary>
    public static TrustedReportId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Conversation shape.</summary>
public enum TrustedConversationKind
{
    /// <summary>Exactly two writers at creation; observers may be added.</summary>
    Direct = 1,

    /// <summary>Small bounded group.</summary>
    Group = 2,
}

/// <summary>Whether a conversation accepts new content.</summary>
public enum TrustedConversationStatus
{
    /// <summary>Participants with the writer role may send, edit and delete.</summary>
    Open = 1,

    /// <summary>No sending, editing, deleting or uploading; history is kept.</summary>
    Closed = 2,
}

/// <summary>Stored capability of a participant.</summary>
public enum TrustedParticipantRole
{
    /// <summary>May read (when <see cref="TrustedParticipant.CanRead"/>) and write.</summary>
    Writer = 1,

    /// <summary>May only read, for example a parent or a staff member with standing access.</summary>
    Observer = 2,
}

/// <summary>Host-defined structured reference attached to a message, for example a lesson or task.</summary>
public sealed record HostReference
{
    /// <summary>Creates a validated reference.</summary>
    public HostReference(string type, string id)
    {
        if (!ServerTrustedChatValidation.IsReferenceType(type) || !ServerTrustedChatValidation.IsReferenceId(id))
        {
            throw new ArgumentException("The host reference is invalid.");
        }

        Type = type;
        Id = id;
    }

    /// <summary>Lower-case host type such as <c>lesson</c>.</summary>
    public string Type { get; }

    /// <summary>Host identifier; opaque to the library.</summary>
    public string Id { get; }
}

/// <summary>Stored conversation metadata.</summary>
public sealed record TrustedConversation(
    TrustedConversationId Id,
    string HostKey,
    TrustedConversationKind Kind,
    TrustedConversationStatus Status,
    DateTimeOffset CreatedAt,
    long LastSequence,
    long ChangeRevision);

/// <summary>Stored participation of one user in one conversation.</summary>
public sealed record TrustedParticipant(
    Guid UserId,
    TrustedParticipantRole Role,
    bool CanRead,
    string Label,
    DateTimeOffset JoinedAt,
    long HistoryVisibleFromSequence,
    long ReadThroughSequence);

/// <summary>Host request to add or update one participation.</summary>
public sealed record TrustedParticipantSpec
{
    /// <summary>Creates a validated specification.</summary>
    /// <param name="userId">Authenticated host user.</param>
    /// <param name="role">Stored capability.</param>
    /// <param name="label">Bounded host label such as <c>teacher</c>; snapshotted onto authored messages.</param>
    /// <param name="canRead">Stored read capability; the unread total counts only readable participations.</param>
    /// <param name="seesHistory">When false, a new participant sees only messages sent after joining.</param>
    public TrustedParticipantSpec(Guid userId, TrustedParticipantRole role, string label, bool canRead = true, bool seesHistory = true)
    {
        if (userId == Guid.Empty || !Enum.IsDefined(role) || !ServerTrustedChatValidation.IsLabel(label))
        {
            throw new ArgumentException("The participant specification is invalid.");
        }

        UserId = userId;
        Role = role;
        Label = label;
        CanRead = canRead;
        SeesHistory = seesHistory;
    }

    /// <summary>User.</summary>
    public Guid UserId { get; }

    /// <summary>Role.</summary>
    public TrustedParticipantRole Role { get; }

    /// <summary>Label.</summary>
    public string Label { get; }

    /// <summary>Read capability.</summary>
    public bool CanRead { get; }

    /// <summary>Whether history before joining is visible (applies on first join only).</summary>
    public bool SeesHistory { get; }
}

/// <summary>Plaintext attachment metadata stored in the server-trusted tables, never in blob metadata.</summary>
public sealed record TrustedAttachmentInfo(
    TrustedAttachmentId Id,
    string FileName,
    string ContentType,
    long Length);

/// <summary>Why message content is not shown to participants.</summary>
public enum TrustedMessageTombstone
{
    /// <summary>The content is visible.</summary>
    None = 0,

    /// <summary>Deleted by its author.</summary>
    DeletedByAuthor = 1,

    /// <summary>Hidden by a moderator.</summary>
    HiddenByModerator = 2,

    /// <summary>Erased on a host erasure request.</summary>
    Erased = 3,
}

/// <summary>A message as shown to an authorized participant.</summary>
public sealed record TrustedMessage(
    TrustedMessageId Id,
    TrustedConversationId ConversationId,
    long Sequence,
    long ChangeRevision,
    Guid AuthorUserId,
    string AuthorLabel,
    string? Text,
    TrustedMessageId? ReplyToMessageId,
    IReadOnlyList<HostReference> References,
    IReadOnlyList<TrustedAttachmentInfo> Attachments,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    TrustedMessageTombstone Tombstone)
{
    /// <summary>Whether content is replaced by a tombstone.</summary>
    public bool IsTombstone => Tombstone != TrustedMessageTombstone.None;
}

/// <summary>A message inside a moderation window, including content hidden from participants while retained.</summary>
public sealed record ModerationMessage(
    TrustedMessage Message,
    string? RetainedText,
    IReadOnlyList<TrustedAttachmentInfo> RetainedAttachments,
    IReadOnlyList<string> PriorVersions,
    bool IsDeletedByAuthor,
    bool IsHidden,
    bool IsErased,
    bool IsReportTarget);

/// <summary>A stored report about a message.</summary>
public sealed record TrustedReport(
    TrustedReportId Id,
    TrustedConversationId ConversationId,
    TrustedMessageId MessageId,
    Guid ReporterUserId,
    string ReasonCode,
    string? Comment,
    DateTimeOffset CreatedAt);

/// <summary>The reported message and its surrounding context for a non-participant moderator.</summary>
public sealed record ModerationWindow(
    TrustedReport Report,
    IReadOnlyList<ModerationMessage> Messages);

/// <summary>One page of history.</summary>
public sealed record TrustedMessagePage(
    IReadOnlyList<TrustedMessage> Messages,
    bool HasMoreBefore,
    long ChangeRevision);

/// <summary>Conversation entry in a user's list, with a capped unread count.</summary>
public sealed record TrustedConversationSummary(
    TrustedConversation Conversation,
    TrustedParticipant Participant,
    int UnreadCount);

/// <summary>How a user's authored content is handled on erasure.</summary>
public enum TrustedForgetMode
{
    /// <summary>Wipe text, prior versions and attachments; tombstones keep sequence continuity.</summary>
    EraseContent = 1,

    /// <summary>Keep content but replace the author with the surrogate.</summary>
    ReassignAuthor = 2,
}
