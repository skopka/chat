namespace Skopka.Chat.ServerTrusted;

/// <summary>Operation for which the host access policy is asked.</summary>
public enum ServerTrustedChatOperation
{
    /// <summary>See conversation metadata, participants, unread state and live signals.</summary>
    ReadConversation = 1,

    /// <summary>Read message history pages.</summary>
    ReadHistory = 2,

    /// <summary>Send a new message.</summary>
    Send = 3,

    /// <summary>Edit an own message.</summary>
    EditOwn = 4,

    /// <summary>Delete an own message.</summary>
    DeleteOwn = 5,

    /// <summary>Advance the own read marker.</summary>
    MarkRead = 6,

    /// <summary>Upload an attachment for a later message.</summary>
    UploadAttachment = 7,

    /// <summary>Download an attachment of a visible message.</summary>
    DownloadAttachment = 8,

    /// <summary>Attach a host reference such as a lesson or task.</summary>
    AttachHostReference = 9,

    /// <summary>Report a visible message.</summary>
    Report = 10,

    /// <summary>Read the moderation window of a specific report as a non-participant.</summary>
    ReadReportWindow = 11,

    /// <summary>Hide or unhide a message as a moderator.</summary>
    Hide = 12,
}

/// <summary>Everything the library knows about an operation when it asks the host policy.</summary>
public sealed record ServerTrustedAccessContext
{
    /// <summary>Authenticated actor resolved by the host.</summary>
    public required Guid ActorUserId { get; init; }

    /// <summary>Requested operation.</summary>
    public required ServerTrustedChatOperation Operation { get; init; }

    /// <summary>Target conversation.</summary>
    public required TrustedConversation Conversation { get; init; }

    /// <summary>Actor's stored participation, or null for a non-participant (moderator).</summary>
    public TrustedParticipant? Participant { get; init; }

    /// <summary>Target message, when the operation concerns one.</summary>
    public TrustedMessageId? MessageId { get; init; }

    /// <summary>Target report, for <see cref="ServerTrustedChatOperation.ReadReportWindow"/>.</summary>
    public TrustedReport? Report { get; init; }

    /// <summary>Reference to authorize, for <see cref="ServerTrustedChatOperation.AttachHostReference"/>.</summary>
    public HostReference? Reference { get; init; }
}

/// <summary>
/// Host authorization asked on every operation after the stored-state checks. It can only deny further; there is
/// deliberately no allow-all default registration.
/// </summary>
public interface IServerTrustedChatAccessPolicy
{
    /// <summary>Returns whether the actor may perform the operation. Implementations must not log message text.</summary>
    ValueTask<bool> AuthorizeAsync(ServerTrustedAccessContext context, CancellationToken cancellationToken = default);
}

/// <summary>Host rate-limit hook evaluated before any write.</summary>
public interface IServerTrustedChatRateLimiter
{
    /// <summary>Returns false to reject the operation with <see cref="ServerTrustedChatStatus.RateLimited"/>.</summary>
    ValueTask<bool> TryAcquireAsync(Guid actorUserId, ServerTrustedChatOperation operation, CancellationToken cancellationToken = default);
}

/// <summary>Explicit no-op rate limiter; registering it is a documented host decision.</summary>
public sealed class UnlimitedServerTrustedChatRateLimiter : IServerTrustedChatRateLimiter
{
    /// <inheritdoc />
    public ValueTask<bool> TryAcquireAsync(Guid actorUserId, ServerTrustedChatOperation operation, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);
}

/// <summary>Candidate upload presented to the host validator.</summary>
public sealed record ServerTrustedAttachmentCandidate(
    Guid UploaderUserId,
    TrustedConversationId ConversationId,
    string FileName,
    string DeclaredContentType,
    ReadOnlyMemory<byte> Content);

/// <summary>Host decision about an upload.</summary>
public sealed record ServerTrustedAttachmentVerdict
{
    private ServerTrustedAttachmentVerdict(bool accepted, string? contentType)
    {
        Accepted = accepted;
        ContentType = contentType;
    }

    /// <summary>Rejected upload.</summary>
    public static ServerTrustedAttachmentVerdict Rejected { get; } = new(false, null);

    /// <summary>Whether the upload is accepted.</summary>
    public bool Accepted { get; }

    /// <summary>Content type determined by the host from the bytes (not the declared one).</summary>
    public string? ContentType { get; }

    /// <summary>Accepts the upload with the host-determined content type.</summary>
    public static ServerTrustedAttachmentVerdict Accept(string contentType) =>
        ServerTrustedChatValidation.IsContentType(contentType)
            ? new ServerTrustedAttachmentVerdict(true, contentType)
            : throw new ArgumentException("The content type is invalid.", nameof(contentType));
}

/// <summary>Host content validation (sniffing, malware scanning) before an attachment is stored.</summary>
public interface IServerTrustedAttachmentValidator
{
    /// <summary>Validates the complete plaintext content. Implementations must not log content or file names.</summary>
    ValueTask<ServerTrustedAttachmentVerdict> ValidateAsync(ServerTrustedAttachmentCandidate candidate, CancellationToken cancellationToken = default);
}

/// <summary>Generic outcome of an engine operation; carries no attacker-controlled detail.</summary>
public enum ServerTrustedChatStatus
{
    /// <summary>The operation succeeded.</summary>
    Success = 1,

    /// <summary>The target does not exist or the actor may not access it; deliberately indistinguishable.</summary>
    NotFoundOrForbidden = 2,

    /// <summary>The input violates a bound or format.</summary>
    Invalid = 3,

    /// <summary>The conversation is closed for writing.</summary>
    Closed = 4,

    /// <summary>An identifier was reused with different content, or the state changed concurrently.</summary>
    Conflict = 5,

    /// <summary>The host rate limiter rejected the operation.</summary>
    RateLimited = 6,

    /// <summary>The conversation reached its participant limit.</summary>
    LimitReached = 7,
}

/// <summary>Outcome with an optional value.</summary>
public readonly record struct ServerTrustedChatResult<T>(ServerTrustedChatStatus Status, T? Value)
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess => Status == ServerTrustedChatStatus.Success;

    internal static ServerTrustedChatResult<T> Ok(T value) => new(ServerTrustedChatStatus.Success, value);

    internal static ServerTrustedChatResult<T> Fail(ServerTrustedChatStatus status) => new(status, default);
}
