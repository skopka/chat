using Skopka.Chat.Attachments;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using System.Text;

namespace Skopka.Chat.Media;

/// <summary>Uploads one already encrypted attachment through a host-selected transport.</summary>
public interface IEncryptedAttachmentUploader
{
    /// <summary>Uploads the exact ciphertext represented by the participant-only manifest.</summary>
    ValueTask<AttachmentStoreResult> UploadAsync(
        ConversationId conversationId,
        ChatAttachmentContent manifest,
        Stream ciphertext,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Outgoing media plus E2EE manifest fields not owned by a media transformer.</summary>
public sealed class ChatMediaAttachmentRequest
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Creates an outgoing attachment preparation request.</summary>
    public ChatMediaAttachmentRequest(
        MediaPreparationRequest media,
        string? caption = null,
        ChatContentId? replyToContentId = null,
        DateTimeOffset? expiresAt = null,
        int chunkPlaintextBytes = ChatAttachmentCryptoService.DefaultChunkPlaintextBytes)
    {
        Media = media ?? throw new ArgumentNullException(nameof(media));
        if (chunkPlaintextBytes < ChatAttachmentCryptoService.MinChunkPlaintextBytes ||
            chunkPlaintextBytes > ChatAttachmentCryptoService.MaxChunkPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkPlaintextBytes), "Chunk size is outside the supported range.");
        }

        if (caption is not null)
        {
            int captionLength;
            try
            {
                captionLength = StrictUtf8.GetByteCount(caption);
            }
            catch (EncoderFallbackException)
            {
                throw new ArgumentException("Caption must contain valid Unicode.", nameof(caption));
            }

            if (captionLength > ChatContentLimits.MaxAttachmentCaptionUtf8Bytes)
            {
                throw new ArgumentOutOfRangeException(nameof(caption), "Caption exceeds its limit.");
            }
        }

        if (replyToContentId is { Value: var replyId } && replyId == Guid.Empty)
        {
            throw new ArgumentException("Reply content ID must not be empty.", nameof(replyToContentId));
        }

        Caption = caption;
        ReplyToContentId = replyToContentId;
        ExpiresAt = expiresAt;
        ChunkPlaintextBytes = chunkPlaintextBytes;
    }

    /// <summary>Source and requested transformation mode.</summary>
    public MediaPreparationRequest Media { get; }

    /// <summary>Optional participant-only caption.</summary>
    public string? Caption { get; }

    /// <summary>Optional participant-only reply target.</summary>
    public ChatContentId? ReplyToContentId { get; }

    /// <summary>Optional server-visible retention deadline.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Attachment plaintext chunk size.</summary>
    public int ChunkPlaintextBytes { get; }
}

/// <summary>High-level media upload stages reported without paths, names or content.</summary>
public enum ChatMediaTransferStage
{
    /// <summary>Media is being prepared according to the requested mode.</summary>
    Preparing = 1,

    /// <summary>Prepared plaintext is being encrypted locally.</summary>
    Encrypting = 2,

    /// <summary>Ciphertext is being uploaded.</summary>
    Uploading = 3,

    /// <summary>The encrypted manifest is ready to send through <c>IChatContentSender</c>.</summary>
    Completed = 4,
}

/// <summary>Redacted progress notification for preparation, encryption and upload.</summary>
public readonly record struct ChatMediaTransferProgress(ChatMediaTransferStage Stage);

/// <summary>Prepares media, encrypts it and uploads ciphertext before the manifest is sent.</summary>
public sealed class ChatMediaAttachmentService
{
    private readonly IMediaPreparationService _preparation;
    private readonly IEncryptedAttachmentUploader _uploader;

    /// <summary>Creates the client-side media attachment orchestrator.</summary>
    public ChatMediaAttachmentService(
        IMediaPreparationService preparation,
        IEncryptedAttachmentUploader uploader)
    {
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
    }

    /// <summary>
    /// Prepares, encrypts and uploads one attachment. The caller supplies an empty readable/writable/seekable
    /// ciphertext buffer and must discard it on failure. The returned manifest is then sent as chat content.
    /// </summary>
    public async ValueTask<ChatAttachmentContent> PrepareEncryptAndUploadAsync(
        ConversationId conversationId,
        ChatMediaAttachmentRequest request,
        Stream ciphertextBuffer,
        IProgress<ChatMediaTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Conversation ID must not be empty.", nameof(conversationId));
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ciphertextBuffer);
        if (!ciphertextBuffer.CanRead || !ciphertextBuffer.CanWrite || !ciphertextBuffer.CanSeek ||
            ciphertextBuffer.Position != 0 || ciphertextBuffer.Length != 0)
        {
            throw new ArgumentException(
                "Ciphertext buffer must be empty, positioned at zero, readable, writable and seekable.",
                nameof(ciphertextBuffer));
        }

        progress?.Report(new ChatMediaTransferProgress(ChatMediaTransferStage.Preparing));
        await using var prepared = await _preparation.PrepareAsync(
            request.Media,
            progress: null,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new ChatMediaTransferProgress(ChatMediaTransferStage.Encrypting));
        var manifest = await ChatAttachmentCryptoService.EncryptAsync(
            prepared.Content,
            prepared.Length,
            ciphertextBuffer,
            AttachmentId.New(),
            ChatContentId.New(),
            prepared.FileName,
            prepared.MediaType,
            request.Caption,
            request.ReplyToContentId,
            request.ChunkPlaintextBytes,
            cancellationToken).ConfigureAwait(false);
        ciphertextBuffer.Position = 0;

        progress?.Report(new ChatMediaTransferProgress(ChatMediaTransferStage.Uploading));
        var result = await _uploader.UploadAsync(
            conversationId,
            manifest,
            ciphertextBuffer,
            request.ExpiresAt,
            cancellationToken).ConfigureAwait(false);
        if (result is not AttachmentStoreResult.Stored and not AttachmentStoreResult.Duplicate)
        {
            throw new MediaPreparationException("Encrypted media upload failed.");
        }

        progress?.Report(new ChatMediaTransferProgress(ChatMediaTransferStage.Completed));
        return manifest;
    }
}
