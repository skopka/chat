using System.Text;
using System.Buffers.Binary;
using Skopka.Chat.Attachments;
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

/// <summary>Deterministically encodes and strictly parses versioned encrypted application content.</summary>
public static class ChatContentEncoding
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("skopka.chat.content");
    private const byte TextKind = (byte)'T';
    private const byte ReactionKind = (byte)'R';
    private const byte AttachmentKind = (byte)'A';
    private const byte EditKind = (byte)'E';
    private const byte AddReaction = (byte)'+';
    private const byte RemoveReaction = (byte)'-';
    private const byte EditText = (byte)'T';
    private const byte EditAttachmentCaption = (byte)'C';
    private const byte ValueAbsent = (byte)'0';
    private const byte ValuePresent = (byte)'1';

    /// <summary>Encodes validated content into a bounded deterministic plaintext payload.</summary>
    public static byte[] Encode(ChatContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var output = new MemoryStream(128);
        output.Write(Domain);

        switch (content)
        {
            case ChatTextContent text:
                output.WriteByte((byte)('0' + ChatContentVersions.V1));
                output.WriteByte(TextKind);
                WriteGuid(output, text.ContentId.Value);
                WriteText(output, text);
                break;
            case ChatReactionContent reaction:
                output.WriteByte((byte)('0' + ChatContentVersions.V1));
                output.WriteByte(ReactionKind);
                WriteGuid(output, reaction.ContentId.Value);
                WriteReaction(output, reaction);
                break;
            case ChatAttachmentContent attachment:
                output.WriteByte((byte)('0' + ChatContentVersions.V2));
                output.WriteByte(AttachmentKind);
                WriteGuid(output, attachment.ContentId.Value);
                WriteAttachment(output, attachment);
                break;
            case ChatEditContent edit:
                output.WriteByte((byte)('0' + ChatContentVersions.V3));
                output.WriteByte(EditKind);
                WriteGuid(output, edit.ContentId.Value);
                WriteEdit(output, edit);
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
        if (!Read(ref remaining, Domain.Length).SequenceEqual(Domain))
        {
            throw new ChatContentFormatException();
        }

        var version = ReadByte(ref remaining);
        var kind = ReadByte(ref remaining);
        var contentId = ReadContentId(ref remaining);
        return (version, kind) switch
        {
            ((byte)('0' + ChatContentVersions.V1), TextKind) => ReadText(contentId, remaining),
            ((byte)('0' + ChatContentVersions.V1), ReactionKind) => ReadReaction(contentId, remaining),
            ((byte)('0' + ChatContentVersions.V2), AttachmentKind) => ReadAttachment(contentId, remaining),
            ((byte)('0' + ChatContentVersions.V3), EditKind) => ReadEdit(contentId, remaining),
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

    private static ChatAttachmentContent ReadAttachment(ChatContentId contentId, ReadOnlySpan<byte> remaining)
    {
        var attachmentId = new AttachmentId(new Guid(Read(ref remaining, 16), bigEndian: true));
        var flags = ReadByte(ref remaining);
        if ((flags & ~3) != 0)
        {
            throw new ChatContentFormatException();
        }

        ChatContentId? replyTo = (flags & 1) != 0 ? ReadContentId(ref remaining) : null;
        var plaintextLength = ReadInt64(ref remaining);
        var ciphertextLength = ReadInt64(ref remaining);
        var chunkPlaintextBytes = ReadInt32(ref remaining);
        var ciphertextSha256 = Read(ref remaining, AttachmentStorageLimits.Sha256Bytes);
        var fileKey = Read(ref remaining, 32);
        var noncePrefix = Read(ref remaining, 16);
        var fileName = ReadLengthPrefixedUtf8(ref remaining, ChatContentLimits.MaxFileNameUtf8Bytes);
        var mediaType = ReadLengthPrefixedAscii(ref remaining, ChatContentLimits.MaxMediaTypeAsciiBytes);
        string? caption = null;
        if ((flags & 2) != 0)
        {
            caption = ReadLengthPrefixedUtf8(ref remaining, ChatContentLimits.MaxAttachmentCaptionUtf8Bytes);
        }

        if (!remaining.IsEmpty)
        {
            throw new ChatContentFormatException();
        }

        return new ChatAttachmentContent(
            contentId,
            attachmentId,
            fileName,
            mediaType,
            plaintextLength,
            ciphertextLength,
            chunkPlaintextBytes,
            ciphertextSha256,
            fileKey,
            noncePrefix,
            caption,
            replyTo);
    }

    private static ChatEditContent ReadEdit(ChatContentId contentId, ReadOnlySpan<byte> remaining)
    {
        var targetContentId = ReadContentId(ref remaining);
        var field = ReadByte(ref remaining) switch
        {
            EditText => ChatEditField.Text,
            EditAttachmentCaption => ChatEditField.AttachmentCaption,
            _ => throw new ChatContentFormatException(),
        };
        var hasValue = ReadByte(ref remaining) switch
        {
            ValueAbsent => false,
            ValuePresent => true,
            _ => throw new ChatContentFormatException(),
        };
        if (field == ChatEditField.Text && !hasValue)
        {
            throw new ChatContentFormatException();
        }

        if (!hasValue)
        {
            if (!remaining.IsEmpty)
            {
                throw new ChatContentFormatException();
            }

            return new ChatEditContent(contentId, targetContentId, field, null);
        }

        var maximumBytes = field == ChatEditField.Text
            ? ChatContentLimits.MaxEditTextUtf8Bytes
            : ChatContentLimits.MaxAttachmentCaptionUtf8Bytes;
        if (remaining.Length > maximumBytes)
        {
            throw new ChatContentFormatException();
        }

        var newValue = ChatContentValidation.StrictUtf8.GetString(remaining);
        return new ChatEditContent(contentId, targetContentId, field, newValue);
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

    private static void WriteAttachment(Stream output, ChatAttachmentContent content)
    {
        WriteGuid(output, content.AttachmentId.Value);
        var flags = (content.ReplyToContentId.HasValue ? 1 : 0) | (content.Caption is not null ? 2 : 0);
        output.WriteByte((byte)flags);
        if (content.ReplyToContentId is { } replyTo)
        {
            WriteGuid(output, replyTo.Value);
        }

        WriteInt64(output, content.PlaintextLength);
        WriteInt64(output, content.CiphertextLength);
        WriteInt32(output, content.ChunkPlaintextBytes);
        output.Write(content.CiphertextSha256.Span);
        output.Write(content.FileKey.Span);
        output.Write(content.NoncePrefix.Span);
        WriteLengthPrefixedUtf8(output, content.FileName);
        WriteLengthPrefixedAscii(output, content.MediaType);
        if (content.Caption is { } caption)
        {
            WriteLengthPrefixedUtf8(output, caption);
        }
    }

    private static void WriteEdit(Stream output, ChatEditContent content)
    {
        WriteGuid(output, content.TargetContentId.Value);
        output.WriteByte(content.Field switch
        {
            ChatEditField.Text => EditText,
            ChatEditField.AttachmentCaption => EditAttachmentCaption,
            _ => throw new InvalidOperationException("Unsupported edit field."),
        });
        output.WriteByte(content.NewValue is null ? ValueAbsent : ValuePresent);
        if (content.NewValue is { } newValue)
        {
            WriteUtf8(output, newValue);
        }
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

    private static void WriteInt64(Stream output, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteLengthPrefixedUtf8(Stream output, string value)
    {
        var bytes = ChatContentValidation.StrictUtf8.GetBytes(value);
        WriteUInt16(output, checked((ushort)bytes.Length));
        output.Write(bytes);
    }

    private static void WriteLengthPrefixedAscii(Stream output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt16(output, checked((ushort)bytes.Length));
        output.Write(bytes);
    }

    private static void WriteUInt16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static ChatContentId ReadContentId(ref ReadOnlySpan<byte> remaining) =>
        new(new Guid(Read(ref remaining, 16), bigEndian: true));

    private static long ReadInt64(ref ReadOnlySpan<byte> remaining) =>
        BinaryPrimitives.ReadInt64BigEndian(Read(ref remaining, sizeof(long)));

    private static int ReadInt32(ref ReadOnlySpan<byte> remaining) =>
        BinaryPrimitives.ReadInt32BigEndian(Read(ref remaining, sizeof(int)));

    private static string ReadLengthPrefixedUtf8(ref ReadOnlySpan<byte> remaining, int maximumBytes)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(Read(ref remaining, sizeof(ushort)));
        if (length > maximumBytes)
        {
            throw new ChatContentFormatException();
        }

        return ChatContentValidation.StrictUtf8.GetString(Read(ref remaining, length));
    }

    private static string ReadLengthPrefixedAscii(ref ReadOnlySpan<byte> remaining, int maximumBytes)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(Read(ref remaining, sizeof(ushort)));
        if (length > maximumBytes)
        {
            throw new ChatContentFormatException();
        }

        var value = Read(ref remaining, length);
        if (value.ContainsAnyExceptInRange((byte)0x21, (byte)0x7e))
        {
            throw new ChatContentFormatException();
        }

        return Encoding.ASCII.GetString(value);
    }

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
