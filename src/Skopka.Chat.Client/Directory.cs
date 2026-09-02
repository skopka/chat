using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Public conversation metadata without message plaintext or previews.</summary>
public sealed record ChatConversationInfo(
    ConversationId ConversationId,
    UserId FirstUserId,
    UserId SecondUserId,
    DateTimeOffset CreatedAt)
{
    /// <summary>Returns whether the user is one of the two participants.</summary>
    public bool Contains(UserId userId) => userId == FirstUserId || userId == SecondUserId;
}

/// <summary>Bounded conversation directory page with an opaque continuation cursor.</summary>
public sealed record ChatConversationPage(
    IReadOnlyList<ChatConversationInfo> Items,
    string? NextCursor);

/// <summary>Bounded active-device directory page with an opaque continuation cursor.</summary>
public sealed record ChatDevicePage(
    IReadOnlyList<PublicDevice> Items,
    string? NextCursor);

/// <summary>Transport-independent authenticated personal-conversation directory.</summary>
public interface IChatConversationDirectory
{
    /// <summary>Gets or creates the unique personal conversation with one peer.</summary>
    ValueTask<ChatConversationInfo> GetOrCreatePersonalConversationAsync(
        UserId peerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the authenticated user's conversations without plaintext previews.</summary>
    ValueTask<ChatConversationPage> ListConversationsAsync(
        string? cursor = null,
        int maximumCount = 50,
        CancellationToken cancellationToken = default);
}

/// <summary>Transport-independent authenticated directory of active conversation devices.</summary>
public interface IRecipientDeviceDirectory
{
    /// <summary>Lists active devices for both participants of one authorized conversation.</summary>
    ValueTask<ChatDevicePage> ListConversationDevicesAsync(
        ConversationId conversationId,
        string? cursor = null,
        int maximumCount = 50,
        CancellationToken cancellationToken = default);
}
