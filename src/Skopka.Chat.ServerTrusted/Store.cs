namespace Skopka.Chat.ServerTrusted;

/// <summary>Outcome of an atomic store write; mapped by the engine to <see cref="ServerTrustedChatStatus"/>.</summary>
public enum TrustedStoreOutcome
{
    /// <summary>The write was applied.</summary>
    Applied = 1,

    /// <summary>An exact retry of an already applied write; nothing changed.</summary>
    Duplicate = 2,

    /// <summary>Nothing to change (for example a read marker that did not advance).</summary>
    Unchanged = 3,

    /// <summary>The target is missing or the stored state does not permit the write.</summary>
    NotFoundOrForbidden = 4,

    /// <summary>The conversation is closed.</summary>
    Closed = 5,

    /// <summary>An identifier was reused with different content, or a referenced item is unusable.</summary>
    Conflict = 6,

    /// <summary>The participant limit was reached.</summary>
    LimitReached = 7,
}

/// <summary>Metadata-only integration event written atomically with the domain change.</summary>
public sealed class ServerTrustedChatEvent
{
    private readonly byte[] _payload;

    /// <summary>Creates a bounded event.</summary>
    public ServerTrustedChatEvent(Guid eventId, string eventType, int eventVersion, DateTimeOffset occurredAt, string partitionKey, ReadOnlySpan<byte> payload)
    {
        if (eventId == Guid.Empty || string.IsNullOrWhiteSpace(eventType) || eventType.Length > 128 || eventVersion <= 0 ||
            occurredAt == default || string.IsNullOrWhiteSpace(partitionKey) || partitionKey.Length > 128 ||
            payload.IsEmpty || payload.Length > 16 * 1024)
        {
            throw new ArgumentException("The server-trusted event is invalid.");
        }

        EventId = eventId;
        EventType = eventType;
        EventVersion = eventVersion;
        OccurredAt = occurredAt;
        PartitionKey = partitionKey;
        _payload = payload.ToArray();
    }

    /// <summary>Consumer idempotency key.</summary>
    public Guid EventId { get; }

    /// <summary>Stable event type.</summary>
    public string EventType { get; }

    /// <summary>Payload schema version.</summary>
    public int EventVersion { get; }

    /// <summary>Commit time.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Conversation ID used as ordering key.</summary>
    public string PartitionKey { get; }

    /// <summary>Exact UTF-8 JSON payload; contains no message text.</summary>
    public ReadOnlyMemory<byte> Payload => _payload.ToArray();
}

/// <summary>Atomic conversation creation request.</summary>
public sealed record TrustedConversationCreate(
    TrustedConversationId Id,
    string HostKey,
    TrustedConversationKind Kind,
    IReadOnlyList<TrustedParticipantSpec> Participants,
    DateTimeOffset CreatedAt);

/// <summary>Atomic send request; the store re-checks stored state under the conversation lock.</summary>
public sealed record TrustedSendRecord(
    TrustedMessageId MessageId,
    TrustedConversationId ConversationId,
    Guid AuthorUserId,
    Guid ClientMessageId,
    string? Text,
    TrustedMessageId? ReplyToMessageId,
    IReadOnlyList<HostReference> References,
    IReadOnlyList<TrustedAttachmentId> AttachmentIds,
    DateTimeOffset CreatedAt,
    Guid EventId);

/// <summary>Result of an atomic send.</summary>
public sealed record TrustedSendResult(TrustedStoreOutcome Outcome, TrustedMessage? Message);

/// <summary>Result of a message mutation (edit, delete, hide).</summary>
public sealed record TrustedMutationResult(TrustedStoreOutcome Outcome, TrustedMessage? Message);

/// <summary>Result of a read-marker write.</summary>
public sealed record TrustedReadMarkerResult(TrustedStoreOutcome Outcome, long ReadThroughSequence);

/// <summary>History query; exactly one ordering mode applies.</summary>
public sealed record TrustedMessageQuery
{
    /// <summary>Return messages with a smaller sequence (newest first internally, returned ascending).</summary>
    public long? BeforeSequence { get; init; }

    /// <summary>Return messages changed after this conversation revision, ascending by revision.</summary>
    public long? ChangedSinceRevision { get; init; }

    /// <summary>Lowest visible sequence for the reader.</summary>
    public long MinSequence { get; init; }

    /// <summary>Page size.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>Uploaded attachment awaiting a message.</summary>
public sealed record TrustedAttachmentRecord(
    TrustedAttachmentInfo Info,
    TrustedConversationId ConversationId,
    Guid UploaderUserId,
    DateTimeOffset CreatedAt,
    TrustedMessageId? MessageId,
    bool IsReady);

/// <summary>Atomic report request.</summary>
public sealed record TrustedReportCreate(TrustedReport Report, Guid EventId);

/// <summary>Kind of a logged staff access.</summary>
public enum TrustedAccessAction
{
    /// <summary>A moderation window was returned.</summary>
    ReadReportWindow = 1,

    /// <summary>An attachment inside a moderation window was opened.</summary>
    OpenReportAttachment = 2,

    /// <summary>A message was hidden.</summary>
    Hide = 3,

    /// <summary>A message was unhidden.</summary>
    Unhide = 4,
}

/// <summary>Append-only access-log entry; contains no message text.</summary>
public sealed record TrustedAccessLogEntry(
    Guid Id,
    Guid ActorUserId,
    TrustedAccessAction Action,
    TrustedConversationId ConversationId,
    TrustedReportId? ReportId,
    TrustedMessageId? MessageId,
    long? FromSequence,
    long? ToSequence,
    DateTimeOffset At);

/// <summary>Attachments whose blobs must be deleted after a database change.</summary>
public sealed record TrustedBlobDeletions(IReadOnlyList<TrustedAttachmentId> AttachmentIds);

/// <summary>
/// Persistence boundary. Every write is atomic and re-validates stored state (status, role, ownership, membership)
/// inside its own transaction, so host-policy checks performed earlier cannot race a closure or revocation.
/// </summary>
public interface IServerTrustedChatStore
{
    /// <summary>Creates the conversation and its participants or returns the existing one with the same host key.</summary>
    ValueTask<(TrustedConversation Conversation, bool Created)> GetOrCreateConversationAsync(TrustedConversationCreate request, CancellationToken cancellationToken = default);

    /// <summary>Gets a conversation by ID.</summary>
    ValueTask<TrustedConversation?> GetConversationAsync(TrustedConversationId id, CancellationToken cancellationToken = default);

    /// <summary>Gets a conversation by host key.</summary>
    ValueTask<TrustedConversation?> FindConversationAsync(string hostKey, CancellationToken cancellationToken = default);

    /// <summary>Opens or closes a conversation.</summary>
    ValueTask<TrustedStoreOutcome> SetStatusAsync(TrustedConversationId id, TrustedConversationStatus status, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a participation; the limit is checked under the conversation lock.</summary>
    ValueTask<TrustedStoreOutcome> UpsertParticipantAsync(TrustedConversationId id, TrustedParticipantSpec participant, DateTimeOffset at, int maxParticipants, CancellationToken cancellationToken = default);

    /// <summary>Removes a participation and its read marker.</summary>
    ValueTask<TrustedStoreOutcome> RemoveParticipantAsync(TrustedConversationId id, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Gets one participation.</summary>
    ValueTask<TrustedParticipant?> GetParticipantAsync(TrustedConversationId id, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Lists all participations of a conversation in join order.</summary>
    ValueTask<IReadOnlyList<TrustedParticipant>> ListParticipantsAsync(TrustedConversationId id, CancellationToken cancellationToken = default);

    /// <summary>Lists the user's readable participations, most recently active first, with capped unread counts.</summary>
    ValueTask<IReadOnlyList<TrustedConversationSummary>> ListConversationsAsync(Guid userId, int limit, int unreadCap, CancellationToken cancellationToken = default);

    /// <summary>Sums capped unread counts over the user's readable participations in one query.</summary>
    ValueTask<int> GetUnreadTotalAsync(Guid userId, int unreadCap, CancellationToken cancellationToken = default);

    /// <summary>Counts unread messages for one participation, capped.</summary>
    ValueTask<int> GetUnreadCountAsync(TrustedConversationId id, Guid userId, int unreadCap, CancellationToken cancellationToken = default);

    /// <summary>Atomically sends: allocates sequence and revision, binds attachments and writes the outbox event.</summary>
    ValueTask<TrustedSendResult> SendAsync(TrustedSendRecord request, CancellationToken cancellationToken = default);

    /// <summary>Atomically edits an own, visible message in an open conversation.</summary>
    ValueTask<TrustedMutationResult> EditAsync(TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, string text, DateTimeOffset at, bool retainPriorVersion, CancellationToken cancellationToken = default);

    /// <summary>Atomically soft-deletes an own message in an open conversation.</summary>
    ValueTask<TrustedMutationResult> DeleteOwnAsync(TrustedConversationId id, TrustedMessageId messageId, Guid authorUserId, DateTimeOffset at, bool retainContent, CancellationToken cancellationToken = default);

    /// <summary>Atomically hides or unhides a message and appends the access-log entry.</summary>
    ValueTask<TrustedMutationResult> SetHiddenAsync(TrustedMessageId messageId, bool hidden, TrustedAccessLogEntry logEntry, CancellationToken cancellationToken = default);

    /// <summary>Gets a message in its participant view.</summary>
    ValueTask<TrustedMessage?> GetMessageAsync(TrustedMessageId messageId, CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page in its participant view, ascending by sequence or revision.</summary>
    ValueTask<IReadOnlyList<TrustedMessage>> GetMessagesAsync(TrustedConversationId id, TrustedMessageQuery query, CancellationToken cancellationToken = default);

    /// <summary>Monotonically advances a readable participation's marker, clamped to the last sequence; writes an event when it advances.</summary>
    ValueTask<TrustedReadMarkerResult> MarkReadAsync(TrustedConversationId id, Guid userId, long throughSequence, DateTimeOffset at, Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>Inserts an attachment row in the uploading state before the blob is written.</summary>
    ValueTask BeginAttachmentAsync(TrustedAttachmentRecord attachment, CancellationToken cancellationToken = default);

    /// <summary>Marks an uploaded attachment ready to be bound by its uploader.</summary>
    ValueTask<bool> CompleteAttachmentAsync(TrustedAttachmentId id, CancellationToken cancellationToken = default);

    /// <summary>Gets an attachment row.</summary>
    ValueTask<TrustedAttachmentRecord?> GetAttachmentAsync(TrustedAttachmentId id, CancellationToken cancellationToken = default);

    /// <summary>Atomically creates a report or returns the existing one for the same message and reporter.</summary>
    ValueTask<(TrustedStoreOutcome Outcome, TrustedReport? Report)> CreateReportAsync(TrustedReportCreate request, CancellationToken cancellationToken = default);

    /// <summary>Gets a report.</summary>
    ValueTask<TrustedReport?> GetReportAsync(TrustedReportId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the moderation window and appends the access-log entry in the same transaction; nothing is returned when
    /// the entry cannot be written.
    /// </summary>
    ValueTask<IReadOnlyList<ModerationMessage>> ReadReportWindowAsync(TrustedReport report, long fromSequence, long toSequence, TrustedAccessLogEntry logEntry, CancellationToken cancellationToken = default);

    /// <summary>Appends an access-log entry on its own (for example an opened moderation attachment).</summary>
    ValueTask AppendAccessLogAsync(TrustedAccessLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Reads the access log of a conversation, newest first.</summary>
    ValueTask<IReadOnlyList<TrustedAccessLogEntry>> GetAccessLogAsync(TrustedConversationId id, int limit, CancellationToken cancellationToken = default);

    /// <summary>Whether the participation's marker covers the sequence; null when there is no participation.</summary>
    ValueTask<bool?> IsReadThroughAsync(TrustedConversationId id, Guid userId, long sequence, CancellationToken cancellationToken = default);

    /// <summary>Erases or reassigns a user's content, deletes their markers and participations and queues blob deletions.</summary>
    ValueTask<TrustedBlobDeletions> ForgetUserAsync(Guid userId, TrustedForgetMode mode, Guid surrogateUserId, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a conversation (history, participants, reports, attachments) and queues its blob deletions; the access log is kept.</summary>
    ValueTask<TrustedBlobDeletions?> DeleteConversationAsync(TrustedConversationId id, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a bounded batch of conversations that were closed before the cutoff and are still closed.</summary>
    ValueTask<(int Conversations, TrustedBlobDeletions Blobs)> PurgeClosedBeforeAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Queues blobs of unbound attachments created before the cutoff and deletes their rows.</summary>
    ValueTask<TrustedBlobDeletions> ExpirePendingAttachmentsAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Deletes a bounded batch of access-log entries older than the cutoff (host retention rule).</summary>
    ValueTask<int> PurgeAccessLogBeforeAsync(DateTimeOffset cutoff, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Lists queued blob deletions (including ones left by a crash).</summary>
    ValueTask<TrustedBlobDeletions> GetQueuedBlobDeletionsAsync(int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Removes queued blob deletions after the blobs were deleted.</summary>
    ValueTask CompleteBlobDeletionsAsync(IReadOnlyList<TrustedAttachmentId> ids, CancellationToken cancellationToken = default);
}
