using Skopka.Chat.Protocol;

namespace Skopka.Chat.Server;

/// <summary>One-to-one conversation between users; each user may own several devices.</summary>
public sealed record PersonalConversation(
    ConversationId ConversationId,
    UserId FirstUserId,
    UserId SecondUserId,
    DateTimeOffset CreatedAt)
{
    /// <summary>Returns whether the user is one of the two participants.</summary>
    public bool Contains(UserId userId) => userId == FirstUserId || userId == SecondUserId;

    /// <summary>Creates metadata with a deterministic unordered participant pair.</summary>
    public static PersonalConversation CreateCanonical(
        ConversationId conversationId,
        UserId firstUserId,
        UserId secondUserId,
        DateTimeOffset createdAt) =>
        firstUserId.Value.CompareTo(secondUserId.Value) <= 0
            ? new PersonalConversation(conversationId, firstUserId, secondUserId, createdAt)
            : new PersonalConversation(conversationId, secondUserId, firstUserId, createdAt);
}

/// <summary>Bounds authenticated server directory pages.</summary>
public static class ChatDirectoryLimits
{
    /// <summary>Largest conversation or device directory page.</summary>
    public const int MaxPageSize = 100;
}

/// <summary>Stable repository cursor for conversations ordered by creation time and ID.</summary>
public readonly record struct ConversationDirectoryCursor(
    DateTimeOffset CreatedAt,
    ConversationId ConversationId);

/// <summary>One bounded page of personal-conversation metadata.</summary>
public sealed record ConversationDirectoryPage(
    IReadOnlyList<PersonalConversation> Items,
    ConversationDirectoryCursor? NextCursor);

/// <summary>Stable repository cursor for active devices ordered by owner and device ID.</summary>
public readonly record struct DeviceDirectoryCursor(UserId UserId, DeviceId DeviceId);

/// <summary>One bounded page of active participant devices.</summary>
public sealed record DeviceDirectoryPage(
    IReadOnlyList<PublicDevice> Items,
    DeviceDirectoryCursor? NextCursor);

/// <summary>Server persistence record containing encrypted data and delivery metadata only.</summary>
public sealed record StoredEnvelope(
    EncryptedEnvelope Envelope,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? AcknowledgedAt = null);

/// <summary>Atomic insert outcome used to distinguish retries from message-ID conflicts.</summary>
public enum EnvelopeStoreResult
{
    /// <summary>A new logical message was persisted.</summary>
    Inserted,
    /// <summary>The identical canonical envelope was already persisted.</summary>
    Duplicate,
    /// <summary>The message ID already belongs to different canonical bytes.</summary>
    Conflict
}

/// <summary>Server registration result.</summary>
public enum DeviceRegistrationResult
{
    /// <summary>A new device record was created.</summary>
    Registered,
    /// <summary>An identical record already exists.</summary>
    Duplicate
}

/// <summary>Server send result.</summary>
public enum SubmitEnvelopeResult
{
    /// <summary>A new encrypted envelope was persisted.</summary>
    Accepted,
    /// <summary>An identical retry was recognized.</summary>
    Duplicate
}

/// <summary>Thrown for a rejected server operation without embedding sensitive data.</summary>
public sealed class ChatServerException : InvalidOperationException
{
    /// <summary>Creates a safe server rejection.</summary>
    public ChatServerException(string message) : base(message)
    {
    }
}
