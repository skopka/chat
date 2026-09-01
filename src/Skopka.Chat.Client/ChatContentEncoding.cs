using System.Text;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Raised when authenticated plaintext is not a supported, canonical chat-content payload.</summary>
public sealed class ChatContentFormatException : FormatException
{
    /// <summary>Creates a content-free format failure.</summary>
    public ChatContentFormatException() : base("Encrypted chat content is invalid or unsupported.")
    {
    }
}

/// <summary>Deterministically encodes and strictly parses encrypted application content version 1.</summary>
public static class ChatContentEncoding
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("skopka.chat.content");
    private const byte TextKind = (byte)'T';
    private const byte ReactionKind = (byte)'R';
    private const byte AddReaction = (byte)'+';
    private const byte RemoveReaction = (byte)'-';

    /// <summary>Encodes validated content into a bounded deterministic plaintext payload.</summary>
    public static byte[] Encode(ChatContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var output = new MemoryStream(128);
        output.Write(Domain);
        output.WriteByte((byte)('0' + ChatContentVersions.Current));

        switch (content)
        {
            case ChatTextContent text:
                output.WriteByte(TextKind);
                WriteGuid(output, text.ContentId.Value);
                WriteText(output, text);
                break;
            case ChatReactionContent reaction:
                output.WriteByte(ReactionKind);
                WriteGuid(output, reaction.ContentId.Value);
                WriteReaction(output, reaction);
                break;
            default:
                throw new ArgumentException("Unsupported chat content type.", nameof(content));
        }

        var encoded = output.ToArray();
        if (encoded.Length > ProtocolLimits.MaxPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content), "Encoded content exceeds the protocol limit.");
        }

        return encoded;
    }

    /// <summary>Strictly parses one complete content payload and rejects unknown or malformed fields.</summary>
    public static ChatContent Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > ProtocolLimits.MaxPlaintextBytes)
        {
            throw new ChatContentFormatException();
        }

        try
        {
            return DecodeCore(encoded);
        }
        catch (ChatContentFormatException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw new ChatContentFormatException();
        }
    }

    private static ChatContent DecodeCore(ReadOnlySpan<byte> encoded)
    {
        var remaining = encoded;
        if (!Read(ref remaining, Domain.Length).SequenceEqual(Domain) ||
            ReadByte(ref remaining) != (byte)('0' + ChatContentVersions.V1))
        {
            throw new ChatContentFormatException();
        }

        var kind = ReadByte(ref remaining);
        var contentId = ReadContentId(ref remaining);
        return kind switch
        {
            TextKind => ReadText(contentId, remaining),
            ReactionKind => ReadReaction(contentId, remaining),
            _ => throw new ChatContentFormatException(),
        };
    }

    private static ChatTextContent ReadText(ChatContentId contentId, ReadOnlySpan<byte> remaining)
    {
        var flags = ReadByte(ref remaining);
        if (flags is < (byte)'0' or > (byte)'3')
        {
            throw new ChatContentFormatException();
        }

        var flagValue = flags - (byte)'0';
        ChatContentId? replyTo = (flagValue & 1) != 0 ? ReadContentId(ref remaining) : null;
        if (remaining.Length > ChatContentLimits.MaxTextUtf8Bytes)
        {
            throw new ChatContentFormatException();
        }

        var text = ChatContentValidation.StrictUtf8.GetString(remaining);
        return new ChatTextContent(contentId, text, replyTo, isForwarded: (flagValue & 2) != 0);
    }

    private static ChatReactionContent ReadReaction(ChatContentId contentId, ReadOnlySpan<byte> remaining)
    {
        var targetContentId = ReadContentId(ref remaining);
        var operation = ReadByte(ref remaining) switch
        {
            AddReaction => ChatReactionOperation.Add,
            RemoveReaction => ChatReactionOperation.Remove,
            _ => throw new ChatContentFormatException(),
        };

        if (remaining.Length > ChatContentLimits.MaxReactionUtf8Bytes)
        {
            throw new ChatContentFormatException();
        }

        var reaction = ChatContentValidation.StrictUtf8.GetString(remaining);
        return new ChatReactionContent(contentId, targetContentId, reaction, operation);
    }

    private static void WriteText(Stream output, ChatTextContent content)
    {
        var flags = (content.ReplyToContentId.HasValue ? 1 : 0) | (content.IsForwarded ? 2 : 0);
        output.WriteByte((byte)('0' + flags));
        if (content.ReplyToContentId is { } replyTo)
        {
            WriteGuid(output, replyTo.Value);
        }

        WriteUtf8(output, content.Text);
    }

    private static void WriteReaction(Stream output, ChatReactionContent content)
    {
        WriteGuid(output, content.TargetContentId.Value);
        output.WriteByte(content.Operation == ChatReactionOperation.Add ? AddReaction : RemoveReaction);
        WriteUtf8(output, content.Reaction);
    }

    private static void WriteUtf8(Stream output, string value)
    {
        var byteCount = ChatContentValidation.StrictUtf8.GetByteCount(value);
        Span<byte> buffer = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        var written = ChatContentValidation.StrictUtf8.GetBytes(value, buffer);
        output.Write(buffer[..written]);
    }

    private static void WriteGuid(Stream output, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != bytes.Length)
        {
            throw new InvalidOperationException("Could not encode a content UUID.");
        }

        output.Write(bytes);
    }

    private static ChatContentId ReadContentId(ref ReadOnlySpan<byte> remaining) =>
        new(new Guid(Read(ref remaining, 16), bigEndian: true));

    private static byte ReadByte(ref ReadOnlySpan<byte> remaining) => Read(ref remaining, 1)[0];

    private static ReadOnlySpan<byte> Read(ref ReadOnlySpan<byte> remaining, int length)
    {
        if (remaining.Length < length)
        {
            throw new ChatContentFormatException();
        }

        var value = remaining[..length];
        remaining = remaining[length..];
        return value;
    }
}
