using Skopka.Chat.Client;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.UI;

/// <summary>Adapts the common multi-device sender to the existing headless UI boundary.</summary>
public sealed class MultiDeviceChatContentSender : IChatContentSender
{
    private readonly ChatMultiDeviceSender _sender;
    private readonly IChatLocalEchoCommitter? _localEchoCommitter;

    /// <summary>Creates an adapter with optional durable local-echo commit before UI success.</summary>
    public MultiDeviceChatContentSender(
        ChatMultiDeviceSender sender,
        IChatLocalEchoCommitter? localEchoCommitter = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _localEchoCommitter = localEchoCommitter;
    }

    /// <inheritdoc />
    public async ValueTask<ChatContentSendResult> SendAsync(
        ConversationId conversationId,
        ChatContent content,
        CancellationToken cancellationToken = default)
    {
        var result = await _sender.SendAsync(conversationId, content, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.LocalEcho is null)
        {
            return ChatContentSendResult.Failed;
        }

        if (_localEchoCommitter is not null)
        {
            await _localEchoCommitter.CommitLocalEchoAsync(result.LocalEcho, cancellationToken).ConfigureAwait(false);
        }

        return ChatContentSendResult.Success(result.LocalEcho);
    }
}
