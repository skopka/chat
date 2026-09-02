using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Bots;

/// <summary>Who operates the endpoint that can read messages addressed to a bot.</summary>
public enum ChatBotHosting
{
    /// <summary>The external bot owner operates the endpoint.</summary>
    OwnerHosted = 1,
    /// <summary>The chat service operator operates its own bot endpoint.</summary>
    FirstParty = 2,
}

/// <summary>Host-authenticated public disclosure. This is not proof of an operator's identity.</summary>
public sealed record ChatBotProfile
{
    /// <summary>Creates an immutable disclosure; changes require a new revision.</summary>
    public ChatBotProfile(UserId botUserId, string name, string operatorId, string operatorName,
        ChatBotHosting hosting, Guid revision)
    {
        if (botUserId.Value == Guid.Empty || revision == Guid.Empty || !Enum.IsDefined(hosting))
        {
            throw new ArgumentException("The bot profile is invalid.");
        }
        ValidateLabel(name);
        ValidateLabel(operatorId);
        ValidateLabel(operatorName);
        BotUserId = botUserId;
        Name = name;
        OperatorId = operatorId;
        OperatorName = operatorName;
        Hosting = hosting;
        Revision = revision;
    }

    /// <summary>Separate bot account, never a human account impersonated by the gateway.</summary>
    public UserId BotUserId { get; }
    /// <summary>Display name; render with encoding.</summary>
    public string Name { get; }
    /// <summary>Host-owned stable operator reference.</summary>
    public string OperatorId { get; }
    /// <summary>Operator disclosure; render with encoding.</summary>
    public string OperatorName { get; }
    /// <summary>Declared hosting boundary, authenticated by the product host.</summary>
    public ChatBotHosting Hosting { get; }
    /// <summary>Changes whenever the disclosure or operator/hosting changes.</summary>
    public Guid Revision { get; }

    private static void ValidateLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
        {
            throw new ArgumentException("The bot disclosure is invalid.");
        }
    }
}

/// <summary>Live host-authorized consent. A fresh grant ID is required after block/re-consent.</summary>
public sealed record ChatBotConsent(Guid GrantId, ConversationId ConversationId, UserId UserId,
    UserId BotUserId, Guid ProfileRevision, DateTimeOffset ExpiresAt)
{
    /// <summary>Validates every scope field against trusted runtime state, not API input.</summary>
    public bool Allows(ChatBotProfile profile, ConversationId conversationId, DateTimeOffset now) =>
        GrantId != Guid.Empty && UserId.Value != Guid.Empty && UserId != profile.BotUserId &&
        ConversationId == conversationId && conversationId.Value != Guid.Empty &&
        BotUserId == profile.BotUserId && ProfileRevision == profile.Revision && ExpiresAt > now;
}

/// <summary>
/// Mandatory trusted host integration. Resolve current authenticated user consent, membership,
/// profile revision and blocking state; return null to deny. Never trust bot-provided consent.
/// </summary>
public interface IChatBotConsentProvider
{
    /// <summary>Obtains live authorization; failures must fail closed, not use cached stale permission.</summary>
    ValueTask<ChatBotConsent?> GetConsentAsync(ConversationId conversationId, CancellationToken cancellationToken = default);
}

/// <summary>Bounded text-only bot API limits, independent of the encrypted-content wire limits.</summary>
public static class ChatBotLimits
{
    /// <summary>Largest exposed or sent UTF-8 text. Larger incoming events are durably suppressed.</summary>
    public const int MaxTextUtf8Bytes = 16 * 1024;
    /// <summary>Maximum updates per polling request.</summary>
    public const int MaxUpdates = 20;
}

/// <summary>One durable, unacknowledged plaintext update. Do not log or serialize implicitly.</summary>
public sealed record ChatBotUpdate(long UpdateId, Guid GrantId, ConversationId ConversationId,
    UserId SenderUserId, ChatContentId ContentId, string Text, ChatContentId? ReplyToContentId, bool IsForwarded)
{
    /// <inheritdoc />
    public override string ToString() => "ChatBotUpdate(Payload=[REDACTED])";
}

/// <summary>Storage operation outcome with immutable conflict detection.</summary>
public enum ChatBotStoreResult
{
    /// <summary>A new record was committed.</summary>
    Stored = 1,
    /// <summary>An equivalent record was already committed.</summary>
    Duplicate = 2,
    /// <summary>The ID already identifies different data.</summary>
    Conflict = 3,
}

/// <summary>
/// Durable endpoint-local state for one bot identity and disclosure revision. Implementations must
/// atomically deduplicate delivery IDs AND (conversation, sender user, content ID), retain suppression
/// and acknowledgement tombstones, and reject conflicts before transport acknowledgement.
/// </summary>
public interface IChatBotInbox
{
    /// <summary>Stores authenticated content with an optional grant; null durably suppresses the event.</summary>
    ValueTask<ChatBotStoreResult> StoreAsync(ReceivedChatContent delivery, Guid? grantId, CancellationToken cancellationToken = default);
    /// <summary>Reads unacknowledged updates after a sequence, without consuming them.</summary>
    ValueTask<IReadOnlyList<ChatBotUpdate>> ReadAsync(long after, int maximumCount, CancellationToken cancellationToken = default);
    /// <summary>Durably acknowledges or suppresses one update without deleting its idempotency record.</summary>
    ValueTask AcknowledgeAsync(long updateId, CancellationToken cancellationToken = default);
    /// <summary>Reserves a stable send request ID against its exact conversation, grant and content.</summary>
    ValueTask<ChatBotStoreResult> ReserveSendAsync(ConversationId conversationId, Guid grantId, ChatTextContent content,
        CancellationToken cancellationToken = default);
}

/// <summary>Generic, payload-free bot boundary failure.</summary>
public sealed class ChatBotException : Exception
{
    /// <summary>Creates a failure without remote text, storage paths or exception details.</summary>
    public ChatBotException() : base("The bot operation could not be completed safely.") { }
}
