using System.Text;
using Skopka.Chat.Attachments;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client;

/// <summary>Versions the typed application content carried inside protocol-v1 ciphertext.</summary>
public static class ChatContentVersions
{
    /// <summary>Text, reply, forward and reaction content.</summary>
    public const byte V1 = 1;

    /// <summary>Encrypted attachment manifests.</summary>
    public const byte V2 = 2;

    /// <summary>Text and attachment-caption edit events.</summary>
    public const byte V3 = 3;

    /// <summary>The latest content version understood by this package.</summary>
    public const byte Current = V3;
}

/// <summary>Bounds fields before typed content is encrypted or projected.</summary>
public static class ChatContentLimits
{
    /// <summary>
    /// Maximum UTF-8 text size, reserving space for the largest content-v1 text header.
    /// </summary>
    public const int MaxTextUtf8Bytes = ProtocolLimits.MaxPlaintextBytes - 54;

    /// <summary>
    /// Maximum UTF-8 replacement-text size, reserving space for the content-v3 edit header.
    /// </summary>
    public const int MaxEditTextUtf8Bytes = ProtocolLimits.MaxPlaintextBytes - 55;

    /// <summary>Maximum UTF-8 size of one reaction rendering token.</summary>
    public const int MaxReactionUtf8Bytes = 64;

    /// <summary>Maximum UTF-8 size of a display file name.</summary>
    public const int MaxFileNameUtf8Bytes = 512;

    /// <summary>Maximum ASCII size of an Internet media type.</summary>
    public const int MaxMediaTypeAsciiBytes = 127;

    /// <summary>Maximum UTF-8 size of an attachment caption.</summary>
    public const int MaxAttachmentCaptionUtf8Bytes = 4 * 1024;
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

    /// <summary>An encrypted manifest for a separately stored ciphertext blob.</summary>
    Attachment = 3,

    /// <summary>An encrypted replacement for text or an attachment caption.</summary>
    Edit = 4,
}

/// <summary>Action applied by a reaction event.</summary>
public enum ChatReactionOperation : byte
{
    /// <summary>Adds the reaction for the authenticated sender user.</summary>
    Add = 1,

    /// <summary>Removes the reaction for the authenticated sender user.</summary>
    Remove = 2,
}

/// <summary>User-visible field replaced by an edit event.</summary>
public enum ChatEditField : byte
{
    /// <summary>Replaces the body of a text message.</summary>
    Text = 1,

    /// <summary>Replaces or clears an attachment caption.</summary>
    AttachmentCaption = 2,
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

/// <summary>An encrypted immutable edit directed at existing logical content.</summary>
public sealed class ChatEditContent : ChatContent
{
    /// <summary>Creates a validated content-v3 edit event.</summary>
    public ChatEditContent(
        ChatContentId contentId,
        ChatContentId targetContentId,
        ChatEditField field,
        string? newValue)
        : base(contentId, ChatContentKind.Edit)
    {
        ChatContentValidation.RequireId(targetContentId, nameof(targetContentId));
        if (targetContentId == contentId)
        {
            throw new ArgumentException("An edit cannot target itself.", nameof(targetContentId));
        }

        switch (field)
        {
            case ChatEditField.Text:
                ArgumentException.ThrowIfNullOrWhiteSpace(newValue);
                ChatContentValidation.RequireUtf8Length(
                    newValue,
                    ChatContentLimits.MaxEditTextUtf8Bytes,
                    nameof(newValue));
                break;
            case ChatEditField.AttachmentCaption:
                if (newValue is not null)
                {
                    if (newValue.Length == 0)
                    {
                        throw new ArgumentException("Use null to clear an attachment caption.", nameof(newValue));
                    }

                    ChatContentValidation.RequireUtf8Length(
                        newValue,
                        ChatContentLimits.MaxAttachmentCaptionUtf8Bytes,
                        nameof(newValue));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), "Unknown edit field.");
        }

        TargetContentId = targetContentId;
        Field = field;
        NewValue = newValue;
    }

    /// <summary>Logical text or attachment content being edited.</summary>
    public ChatContentId TargetContentId { get; }

    /// <summary>Field replaced by this event.</summary>
    public ChatEditField Field { get; }

    /// <summary>Replacement plaintext; null clears an attachment caption.</summary>
    public string? NewValue { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatEditContent(ContentId={ContentId}, Target={TargetContentId}, Field={Field}, NewValue=[REDACTED])";
}

/// <summary>
/// Encrypted attachment manifest. Every property on this type is plaintext only on participant devices.
/// </summary>
public sealed class ChatAttachmentContent : ChatContent
{
    private const int FileKeyBytes = 32;
    private const int NoncePrefixBytes = 16;
    private readonly byte[] _ciphertextSha256;
    private readonly byte[] _fileKey;
    private readonly byte[] _noncePrefix;

    /// <summary>Creates a validated content-v2 attachment manifest.</summary>
    public ChatAttachmentContent(
        ChatContentId contentId,
        AttachmentId attachmentId,
        string fileName,
        string mediaType,
        long plaintextLength,
        long ciphertextLength,
        int chunkPlaintextBytes,
        ReadOnlySpan<byte> ciphertextSha256,
        ReadOnlySpan<byte> fileKey,
        ReadOnlySpan<byte> noncePrefix,
        string? caption = null,
        ChatContentId? replyToContentId = null)
        : base(contentId, ChatContentKind.Attachment)
    {
        if (attachmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Attachment ID must not be empty.", nameof(attachmentId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ChatContentValidation.RequireUtf8Length(fileName, ChatContentLimits.MaxFileNameUtf8Bytes, nameof(fileName));
        if (fileName is "." or ".." ||
            fileName.Any(static character => char.IsControl(character) || character is '/' or '\\'))
        {
            throw new ArgumentException("File name must not contain paths or control characters.", nameof(fileName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (mediaType.Length > ChatContentLimits.MaxMediaTypeAsciiBytes ||
            mediaType.Any(static character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new ArgumentException("Media type must be bounded printable ASCII.", nameof(mediaType));
        }

        if (plaintextLength < 0 || plaintextLength > AttachmentStorageLimits.MaxCiphertextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintextLength), "Plaintext length is outside the supported range.");
        }

        if (ciphertextLength <= 0 || ciphertextLength > AttachmentStorageLimits.MaxCiphertextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ciphertextLength), "Ciphertext length is outside the supported range.");
        }

        if (chunkPlaintextBytes < ChatAttachmentCryptoService.MinChunkPlaintextBytes ||
            chunkPlaintextBytes > ChatAttachmentCryptoService.MaxChunkPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkPlaintextBytes), "Chunk size is outside the supported range.");
        }

        if (!ChatAttachmentCryptoService.TryGetCiphertextLength(
                plaintextLength,
                chunkPlaintextBytes,
                out var expectedCiphertextLength) ||
            ciphertextLength != expectedCiphertextLength)
        {
            throw new ArgumentException("Ciphertext length does not match the canonical chunk framing.", nameof(ciphertextLength));
        }

        if (ciphertextSha256.Length != AttachmentStorageLimits.Sha256Bytes)
        {
            throw new ArgumentException("Ciphertext hash has an invalid size.", nameof(ciphertextSha256));
        }

        if (fileKey.Length != FileKeyBytes)
        {
            throw new ArgumentException("Attachment key has an invalid size.", nameof(fileKey));
        }

        if (noncePrefix.Length != NoncePrefixBytes)
        {
            throw new ArgumentException("Attachment nonce prefix has an invalid size.", nameof(noncePrefix));
        }

        if (caption is not null)
        {
            ChatContentValidation.RequireUtf8Length(caption, ChatContentLimits.MaxAttachmentCaptionUtf8Bytes, nameof(caption));
        }

        if (replyToContentId is { } replyId)
        {
            ChatContentValidation.RequireId(replyId, nameof(replyToContentId));
            if (replyId == contentId)
            {
                throw new ArgumentException("Content cannot reply to itself.", nameof(replyToContentId));
            }
        }

        AttachmentId = attachmentId;
        FileName = fileName;
        MediaType = mediaType;
        PlaintextLength = plaintextLength;
        CiphertextLength = ciphertextLength;
        ChunkPlaintextBytes = chunkPlaintextBytes;
        _ciphertextSha256 = ciphertextSha256.ToArray();
        _fileKey = fileKey.ToArray();
        _noncePrefix = noncePrefix.ToArray();
        Caption = caption;
        ReplyToContentId = replyToContentId;
    }

    /// <summary>Opaque identifier used to retrieve ciphertext.</summary>
    public AttachmentId AttachmentId { get; }

    /// <summary>Decrypted display name without a path.</summary>
    public string FileName { get; }

    /// <summary>Decrypted sender-declared media type; hosts must still treat bytes as untrusted.</summary>
    public string MediaType { get; }

    /// <summary>Expected decrypted length.</summary>
    public long PlaintextLength { get; }

    /// <summary>Exact separately stored ciphertext length.</summary>
    public long CiphertextLength { get; }

    /// <summary>Plaintext bytes authenticated per encrypted chunk.</summary>
    public int ChunkPlaintextBytes { get; }

    /// <summary>SHA-256 over the exact separately stored ciphertext.</summary>
    public ReadOnlyMemory<byte> CiphertextSha256 => _ciphertextSha256;

    /// <summary>Symmetric attachment key. Hosts must keep it in protected local state.</summary>
    public ReadOnlyMemory<byte> FileKey => _fileKey;

    /// <summary>Random nonce prefix combined with a monotonically increasing chunk index.</summary>
    public ReadOnlyMemory<byte> NoncePrefix => _noncePrefix;

    /// <summary>Optional decrypted caption.</summary>
    public string? Caption { get; }

    /// <summary>Optional referenced logical content.</summary>
    public ChatContentId? ReplyToContentId { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatAttachmentContent(ContentId={ContentId}, AttachmentId={AttachmentId}, PlaintextLength={PlaintextLength}, Manifest=[REDACTED])";
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
