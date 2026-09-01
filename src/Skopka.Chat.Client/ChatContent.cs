using System.Text;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Versions the typed application content carried inside protocol-v1 ciphertext.</summary>
public static class ChatContentVersions
{
    /// <summary>Text, reply, forward and reaction content.</summary>
    public const byte V1 = 1;

    /// <summary>The content version emitted by this package.</summary>
    public const byte Current = V1;
}

/// <summary>Bounds fields before typed content is encrypted or projected.</summary>
public static class ChatContentLimits
{
    /// <summary>
    /// Maximum UTF-8 text size, reserving space for the largest content-v1 text header.
    /// </summary>
    public const int MaxTextUtf8Bytes = ProtocolLimits.MaxPlaintextBytes - 54;

    /// <summary>Maximum UTF-8 size of one reaction rendering token.</summary>
    public const int MaxReactionUtf8Bytes = 64;
}

/// <summary>Identifies one logical encrypted content event across per-device envelopes.</summary>
public readonly record struct ChatContentId(Guid Value)
{
    /// <summary>Creates a new opaque content identifier.</summary>
    public static ChatContentId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Known encrypted content variants.</summary>
public enum ChatContentKind : byte
{
    /// <summary>A user-visible text message.</summary>
    Text = 1,

    /// <summary>An add or remove reaction event.</summary>
    Reaction = 2,
}

/// <summary>Action applied by a reaction event.</summary>
public enum ChatReactionOperation : byte
{
    /// <summary>Adds the reaction for the authenticated sender user.</summary>
    Add = 1,

    /// <summary>Removes the reaction for the authenticated sender user.</summary>
    Remove = 2,
}

/// <summary>Base type for versioned content encrypted inside an envelope.</summary>
public abstract class ChatContent
{
    private protected ChatContent(ChatContentId contentId, ChatContentKind kind)
    {
        ChatContentValidation.RequireId(contentId, nameof(contentId));
        ContentId = contentId;
        Kind = kind;
    }

    /// <summary>Stable logical identifier reused when this event is encrypted for several devices.</summary>
    public ChatContentId ContentId { get; }

    /// <summary>Discriminator for the content variant.</summary>
    public ChatContentKind Kind { get; }

    /// <inheritdoc />
    public override string ToString() => $"ChatContent(ContentId={ContentId}, Kind={Kind}, Payload=[REDACTED])";
}

/// <summary>Encrypted text with optional reply metadata and a non-provenance forward marker.</summary>
public sealed class ChatTextContent : ChatContent
{
    /// <summary>Creates text content.</summary>
    public ChatTextContent(
        ChatContentId contentId,
        string text,
        ChatContentId? replyToContentId = null,
        bool isForwarded = false)
        : base(contentId, ChatContentKind.Text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ChatContentValidation.RequireUtf8Length(text, ChatContentLimits.MaxTextUtf8Bytes, nameof(text));
        if (replyToContentId is { } replyId)
        {
            ChatContentValidation.RequireId(replyId, nameof(replyToContentId));
            if (replyId == contentId)
            {
                throw new ArgumentException("Content cannot reply to itself.", nameof(replyToContentId));
            }
        }

        Text = text;
        ReplyToContentId = replyToContentId;
        IsForwarded = isForwarded;
    }

    /// <summary>Decrypted UTF-16 text for the host application.</summary>
    public string Text { get; }

    /// <summary>Referenced logical content, even when it is not available locally.</summary>
    public ChatContentId? ReplyToContentId { get; }

    /// <summary>
    /// Whether the authenticated sender marked this as copied content. This does not prove the original author.
    /// </summary>
    public bool IsForwarded { get; }

    /// <summary>
    /// Copies only text into a new forwarded event, intentionally dropping reply and source attribution.
    /// </summary>
    public ChatTextContent Forward(ChatContentId newContentId) => new(newContentId, Text, isForwarded: true);

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatTextContent(ContentId={ContentId}, ReplyTo={ReplyToContentId}, Forwarded={IsForwarded}, Text=[REDACTED])";
}

/// <summary>An encrypted add/remove reaction directed at logical content.</summary>
public sealed class ChatReactionContent : ChatContent
{
    /// <summary>Creates a reaction event.</summary>
    public ChatReactionContent(
        ChatContentId contentId,
        ChatContentId targetContentId,
        string reaction,
        ChatReactionOperation operation)
        : base(contentId, ChatContentKind.Reaction)
    {
        ChatContentValidation.RequireId(targetContentId, nameof(targetContentId));
        if (targetContentId == contentId)
        {
            throw new ArgumentException("A reaction cannot target itself.", nameof(targetContentId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reaction);
        ChatContentValidation.RequireUtf8Length(reaction, ChatContentLimits.MaxReactionUtf8Bytes, nameof(reaction));
        if (reaction.Any(char.IsControl))
        {
            throw new ArgumentException("A reaction must not contain control characters.", nameof(reaction));
        }

        if (operation is not ChatReactionOperation.Add and not ChatReactionOperation.Remove)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), "Unknown reaction operation.");
        }

        TargetContentId = targetContentId;
        Reaction = reaction;
        Operation = operation;
    }

    /// <summary>Logical content receiving the reaction.</summary>
    public ChatContentId TargetContentId { get; }

    /// <summary>Bounded rendering token, usually one emoji or emoji sequence.</summary>
    public string Reaction { get; }

    /// <summary>Whether this event adds or removes the reaction.</summary>
    public ChatReactionOperation Operation { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatReactionContent(ContentId={ContentId}, Target={TargetContentId}, Operation={Operation}, Reaction=[REDACTED])";
}

internal static class ChatContentValidation
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void RequireId(ChatContentId contentId, string parameterName)
    {
        if (contentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Content ID must not be empty.", parameterName);
        }
    }

    internal static int RequireUtf8Length(string value, int maximumBytes, string parameterName)
    {
        int length;
        try
        {
            length = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Text must contain valid Unicode.", parameterName);
        }

        if (length > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, "UTF-8 content exceeds its limit.");
        }

        return length;
    }
}
