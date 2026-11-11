using Microsoft.AspNetCore.Components.Forms;

namespace Skopka.Chat.ServerTrusted.Blazor;

/// <summary>Context supplied to a custom message template.</summary>
/// <param name="Message">Message as returned by the engine; tombstones carry no text.</param>
/// <param name="IsOwn">Whether the current user authored the message.</param>
/// <param name="ReplyTarget">Loaded reply target, or null when there is none or it is not loaded.</param>
/// <param name="AuthorName">Resolved author display name.</param>
/// <param name="ViewModel">Underlying presentation state.</param>
/// <param name="CanEdit">Whether <paramref name="Edit"/> is meaningful now.</param>
/// <param name="CanDelete">Whether <paramref name="Delete"/> is meaningful now.</param>
/// <param name="CanReply">Whether <paramref name="Reply"/> is meaningful now.</param>
/// <param name="CanReport">Whether <paramref name="Report"/> is meaningful now.</param>
/// <param name="Reply">Starts replying to the message.</param>
/// <param name="Edit">Starts editing the message.</param>
/// <param name="Delete">Soft-deletes the message without confirmation.</param>
/// <param name="Report">Reports the message with a reason code and an optional comment.</param>
public sealed record ServerTrustedMessageContext(
    TrustedMessage Message,
    bool IsOwn,
    TrustedMessage? ReplyTarget,
    string AuthorName,
    ServerTrustedChatViewModel ViewModel,
    bool CanEdit,
    bool CanDelete,
    bool CanReply,
    bool CanReport,
    Func<Task> Reply,
    Func<Task> Edit,
    Func<Task> Delete,
    Func<string, string?, Task> Report);

/// <summary>Context supplied to a custom attachment template.</summary>
/// <param name="Message">Message the attachment belongs to.</param>
/// <param name="Attachment">Stored attachment metadata.</param>
/// <param name="Url">Host-owned download URL, or null when the host supplied no URL builder.</param>
public sealed record ServerTrustedAttachmentContext(
    TrustedMessage Message,
    TrustedAttachmentInfo Attachment,
    string? Url);

/// <summary>Context supplied to a custom composer template.</summary>
/// <param name="Draft">Current draft text.</param>
/// <param name="SetDraft">Replaces the draft.</param>
/// <param name="Send">Sends the draft with pending attachments and references, or saves it when editing.</param>
/// <param name="Cancel">Cancels editing or the reply target.</param>
/// <param name="IsEditing">Whether an own message is being edited.</param>
/// <param name="IsReplying">Whether the draft replies to a message.</param>
/// <param name="CanSend">Whether <paramref name="Send"/> would submit.</param>
/// <param name="MaxLength">Engine text bound in UTF-16 code units.</param>
/// <param name="ReplyTarget">Loaded reply target, if any.</param>
/// <param name="ViewModel">Underlying presentation state.</param>
/// <param name="AttachmentsAllowed">Whether the host enabled attachments for this component.</param>
/// <param name="CanUpload">Whether <paramref name="UploadFile"/> would accept another file now.</param>
/// <param name="IsUploading">Whether an upload is in progress.</param>
/// <param name="MaxAttachmentBytes">Effective attachment size bound in bytes.</param>
/// <param name="AttachmentAccept">Host <c>accept</c> filter for file inputs, if any.</param>
/// <param name="PendingAttachments">Uploaded attachments the next message will carry.</param>
/// <param name="UploadFile">Uploads a browser-selected file after checking the size bound.</param>
/// <param name="RemoveAttachment">Removes a pending attachment from the draft.</param>
/// <param name="DraftReferences">Host references the next message will carry.</param>
/// <param name="RemoveReference">Removes a host reference from the draft.</param>
public sealed record ServerTrustedComposerContext(
    string Draft,
    Action<string?> SetDraft,
    Func<Task> Send,
    Action Cancel,
    bool IsEditing,
    bool IsReplying,
    bool CanSend,
    int MaxLength,
    TrustedMessage? ReplyTarget,
    ServerTrustedChatViewModel ViewModel,
    bool AttachmentsAllowed,
    bool CanUpload,
    bool IsUploading,
    long MaxAttachmentBytes,
    string? AttachmentAccept,
    IReadOnlyList<TrustedAttachmentInfo> PendingAttachments,
    Func<IBrowserFile, Task<bool>> UploadFile,
    Action<TrustedAttachmentId> RemoveAttachment,
    IReadOnlyList<HostReference> DraftReferences,
    Action<HostReference> RemoveReference);
