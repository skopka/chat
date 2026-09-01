using System.Collections.Concurrent;
using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Decrypted local message. Its string form does not include plaintext.</summary>
public sealed class ReceivedMessage
{
    private readonly byte[] _plaintext;

    /// <summary>Creates a local message after successful authentication.</summary>
    public ReceivedMessage(MessageId messageId, ConversationId conversationId, DeviceId senderDeviceId, ReadOnlySpan<byte> plaintext)
    {
        MessageId = messageId;
        ConversationId = conversationId;
        SenderDeviceId = senderDeviceId;
        _plaintext = plaintext.ToArray();
    }

    /// <summary>Delivery-envelope idempotency identifier.</summary>
    public MessageId MessageId { get; }

    /// <summary>Conversation identifier.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>Authenticated sender device.</summary>
    public DeviceId SenderDeviceId { get; }

    /// <summary>Returns a defensive plaintext copy to the host application.</summary>
    public byte[] ExportPlaintext() => _plaintext.ToArray();

    /// <inheritdoc />
    public override string ToString() => $"ReceivedMessage(MessageId={MessageId}, Plaintext=[REDACTED])";
}

/// <summary>Atomic local deduplication boundary for decrypted messages.</summary>
public interface IReceivedMessageStore
{
    /// <summary>Checks whether a logical message was already committed.</summary>
    ValueTask<bool> ContainsAsync(MessageId messageId, CancellationToken cancellationToken = default);

    /// <summary>Adds a message only when its ID is new.</summary>
    ValueTask<bool> TryAddAsync(ReceivedMessage message, CancellationToken cancellationToken = default);
}

/// <summary>In-memory local message store for tests and samples.</summary>
public sealed class InMemoryReceivedMessageStore : IReceivedMessageStore
{
    private readonly ConcurrentDictionary<MessageId, ReceivedMessage> _messages = new();

    /// <summary>Number of distinct committed message IDs.</summary>
    public int Count => _messages.Count;

    /// <inheritdoc />
    public ValueTask<bool> ContainsAsync(MessageId messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_messages.ContainsKey(messageId));
    }

    /// <inheritdoc />
    public ValueTask<bool> TryAddAsync(ReceivedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_messages.TryAdd(message.MessageId, message));
    }
}

/// <summary>Result of processing one encrypted delivery.</summary>
public sealed record ReceiveResult(bool Added, ReceivedMessage? Message)
{
    /// <summary>Duplicate delivery result.</summary>
    public static ReceiveResult Duplicate { get; } = new(false, null);
}

/// <summary>Authenticated typed content and the delivery metadata that carried it.</summary>
public sealed class ReceivedChatContent
{
    /// <summary>Creates a verified content delivery, typically when restoring a protected local store.</summary>
    public ReceivedChatContent(
        MessageId deliveryMessageId,
        ConversationId conversationId,
        UserId senderUserId,
        DeviceId senderDeviceId,
        DateTimeOffset sentAt,
        ChatContent content)
    {
        if (deliveryMessageId.Value == Guid.Empty)
        {
            throw new ArgumentException("Delivery message ID must not be empty.", nameof(deliveryMessageId));
        }

        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        if (senderUserId.Value == Guid.Empty)
        {
            throw new ArgumentException("Sender user ID must not be empty.", nameof(senderUserId));
        }

        if (senderDeviceId.Value == Guid.Empty)
        {
            throw new ArgumentException("Sender device ID must not be empty.", nameof(senderDeviceId));
        }

        if (sentAt == default)
        {
            throw new ArgumentException("Sent timestamp must not be empty.", nameof(sentAt));
        }

        ArgumentNullException.ThrowIfNull(content);
        DeliveryMessageId = deliveryMessageId;
        ConversationId = conversationId;
        SenderUserId = senderUserId;
        SenderDeviceId = senderDeviceId;
        SentAt = sentAt;
        Content = content;
    }

    /// <summary>Idempotency identifier of this recipient-specific envelope.</summary>
    public MessageId DeliveryMessageId { get; }

    /// <summary>Conversation authenticated by the envelope.</summary>
    public ConversationId ConversationId { get; }

    /// <summary>User owning the verified sender device directory entry.</summary>
    public UserId SenderUserId { get; }

    /// <summary>Device whose signature authenticated the envelope.</summary>
    public DeviceId SenderDeviceId { get; }

    /// <summary>Sender-supplied timestamp authenticated by the envelope.</summary>
    public DateTimeOffset SentAt { get; }

    /// <summary>Strictly decoded application content.</summary>
    public ChatContent Content { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ReceivedChatContent(DeliveryMessageId={DeliveryMessageId}, ContentId={Content.ContentId}, Payload=[REDACTED])";
}

/// <summary>Result of processing one typed encrypted delivery.</summary>
public sealed record ChatContentReceiveResult(bool Added, ReceivedChatContent? Delivery)
{
    /// <summary>Duplicate delivery result.</summary>
    public static ChatContentReceiveResult Duplicate { get; } = new(false, null);
}

/// <summary>Authenticates, decrypts and atomically deduplicates local deliveries.</summary>
public sealed class ChatReceiver
{
    private readonly ChatCryptoService _crypto;
    private readonly IReceivedMessageStore _messages;

    /// <summary>Creates a delivery processor.</summary>
    public ChatReceiver(ChatCryptoService crypto, IReceivedMessageStore messages)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
    }

    /// <summary>Processes a delivery once by logical message ID.</summary>
    public async ValueTask<ReceiveResult> ReceiveAsync(
        EncryptedEnvelope envelope,
        PublicDevice sender,
        CancellationToken cancellationToken = default)
    {
        if (await _messages.ContainsAsync(envelope.MessageId, cancellationToken).ConfigureAwait(false))
        {
            return ReceiveResult.Duplicate;
        }

        var plaintext = await _crypto.DecryptAsync(envelope, sender, cancellationToken).ConfigureAwait(false);
        try
        {
            var message = new ReceivedMessage(envelope.MessageId, envelope.ConversationId, envelope.SenderDeviceId, plaintext);
            return await _messages.TryAddAsync(message, cancellationToken).ConfigureAwait(false)
                ? new ReceiveResult(true, message)
                : ReceiveResult.Duplicate;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Authenticates, decodes and atomically deduplicates one typed content delivery.</summary>
    public async ValueTask<ChatContentReceiveResult> ReceiveContentAsync(
        EncryptedEnvelope envelope,
        PublicDevice sender,
        CancellationToken cancellationToken = default)
    {
        if (await _messages.ContainsAsync(envelope.MessageId, cancellationToken).ConfigureAwait(false))
        {
            return ChatContentReceiveResult.Duplicate;
        }

        var plaintext = await _crypto.DecryptAsync(envelope, sender, cancellationToken).ConfigureAwait(false);
        try
        {
            var content = ChatContentEncoding.Decode(plaintext);
            var localMessage = new ReceivedMessage(
                envelope.MessageId,
                envelope.ConversationId,
                envelope.SenderDeviceId,
                plaintext);
            if (!await _messages.TryAddAsync(localMessage, cancellationToken).ConfigureAwait(false))
            {
                return ChatContentReceiveResult.Duplicate;
            }

            var delivery = new ReceivedChatContent(
                envelope.MessageId,
                envelope.ConversationId,
                sender.UserId,
                envelope.SenderDeviceId,
                envelope.SentAt,
                content);
            return new ChatContentReceiveResult(true, delivery);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
