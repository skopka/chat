namespace Skopka.Chat.ServerTrusted.PostgreSql;

internal sealed class ConversationRow
{
    public Guid Id { get; set; }
    public string HostKey { get; set; } = string.Empty;
    public short Kind { get; set; }
    public short Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public long LastSequence { get; set; }
    public long ChangeRevision { get; set; }

    public TrustedConversation ToDomain() =>
        new(new TrustedConversationId(Id), HostKey, (TrustedConversationKind)Kind, (TrustedConversationStatus)Status, CreatedAt, LastSequence, ChangeRevision);
}

internal sealed class ParticipantRow
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public short Role { get; set; }
    public bool CanRead { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
    public long HistoryVisibleFrom { get; set; }
    public long ReadThrough { get; set; }

    public TrustedParticipant ToDomain() =>
        new(UserId, (TrustedParticipantRole)Role, CanRead, Label, JoinedAt, HistoryVisibleFrom, ReadThrough);
}

internal sealed class MessageRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long Sequence { get; set; }
    public long ChangeRevision { get; set; }
    public Guid AuthorUserId { get; set; }
    public string AuthorLabel { get; set; } = string.Empty;
    public Guid ClientMessageId { get; set; }
    public byte[] Fingerprint { get; set; } = [];
    public string? Text { get; set; }
    public string? RetainedText { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public string ReferencesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
    public bool IsErased { get; set; }

    public TrustedMessageTombstone Tombstone =>
        IsErased ? TrustedMessageTombstone.Erased
        : HiddenAt is not null ? TrustedMessageTombstone.HiddenByModerator
        : DeletedAt is not null ? TrustedMessageTombstone.DeletedByAuthor
        : TrustedMessageTombstone.None;
}

internal sealed class MessageVersionRow
{
    public Guid MessageId { get; set; }
    public int Version { get; set; }
    public string Text { get; set; } = string.Empty;
}

internal sealed class AttachmentRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UploaderUserId { get; set; }
    public Guid? MessageId { get; set; }
    public int Position { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsReady { get; set; }

    public TrustedAttachmentInfo ToInfo() => new(new TrustedAttachmentId(Id), FileName, ContentType, Length);
}

internal sealed class ReportRow
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    public Guid ReporterUserId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public TrustedReport ToDomain() =>
        new(new TrustedReportId(Id), new TrustedConversationId(ConversationId), new TrustedMessageId(MessageId), ReporterUserId, ReasonCode, Comment, CreatedAt);
}

internal sealed class AccessLogRow
{
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public short Action { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? ReportId { get; set; }
    public Guid? MessageId { get; set; }
    public long? FromSequence { get; set; }
    public long? ToSequence { get; set; }
    public DateTimeOffset At { get; set; }

    public static AccessLogRow From(TrustedAccessLogEntry entry) => new()
    {
        Id = entry.Id,
        ActorUserId = entry.ActorUserId,
        Action = (short)entry.Action,
        ConversationId = entry.ConversationId.Value,
        ReportId = entry.ReportId?.Value,
        MessageId = entry.MessageId?.Value,
        FromSequence = entry.FromSequence,
        ToSequence = entry.ToSequence,
        At = entry.At,
    };

    public TrustedAccessLogEntry ToDomain() => new(
        Id,
        ActorUserId,
        (TrustedAccessAction)Action,
        new TrustedConversationId(ConversationId),
        ReportId is { } report ? new TrustedReportId(report) : null,
        MessageId is { } message ? new TrustedMessageId(message) : null,
        FromSequence,
        ToSequence,
        At);
}

internal sealed class BlobDeletionRow
{
    public Guid AttachmentId { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
}

internal sealed class OutboxRow
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int EventVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string PartitionKey { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? LastFailedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public static OutboxRow From(ServerTrustedChatEvent item) => new()
    {
        EventId = item.EventId,
        EventType = item.EventType,
        EventVersion = item.EventVersion,
        OccurredAt = item.OccurredAt,
        PartitionKey = item.PartitionKey,
        Payload = item.Payload.ToArray(),
        NextAttemptAt = item.OccurredAt,
    };
}
