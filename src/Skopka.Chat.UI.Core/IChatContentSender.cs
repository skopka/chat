using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.UI;

/// <summary>
/// Host-owned boundary that encrypts, fans out and sends one logical content event.
/// </summary>
/// <remarks>
/// Implementations must not log content. A successful result contains the authenticated local echo
/// accepted by the host and must describe the same content and authenticated current user.
/// </remarks>
public interface IChatContentSender
{
    /// <summary>
    /// Sends content to one conversation. Expected transport failures should return
    /// <see cref="ChatContentSendResult.Failed"/> rather than expose remote text.
    /// </summary>
    ValueTask<ChatContentSendResult> SendAsync(
        ConversationId conversationId,
        ChatContent content,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounded result returned by the host-owned content sender.</summary>
public sealed class ChatContentSendResult
{
    private ChatContentSendResult(bool succeeded, ReceivedChatContent? delivery)
    {
        Succeeded = succeeded;
        Delivery = delivery;
    }

    /// <summary>Generic expected failure without remote or plaintext error details.</summary>
    public static ChatContentSendResult Failed { get; } = new(false, null);

    /// <summary>Whether the host accepted the logical event.</summary>
    public bool Succeeded { get; }

    /// <summary>Authenticated local echo when the operation succeeded.</summary>
    public ReceivedChatContent? Delivery { get; }

    /// <summary>Creates a successful result with an authenticated local echo.</summary>
    public static ChatContentSendResult Success(ReceivedChatContent delivery) =>
        new(true, delivery ?? throw new ArgumentNullException(nameof(delivery)));

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatContentSendResult(Succeeded={Succeeded}, Delivery={(Delivery is null ? "none" : "[REDACTED]")})";
}
