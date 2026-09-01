using Skopka.Chat.Client;
using Skopka.Chat.UI;
using Microsoft.AspNetCore.Components.Forms;

namespace Skopka.Chat.UI.Blazor;

/// <summary>Context supplied to a custom message template.</summary>
public sealed record ChatMessageTemplateContext(ChatViewModel ViewModel, ProjectedChatMessage Message);

/// <summary>Context supplied to a custom attachment template.</summary>
public sealed record ChatAttachmentTemplateContext(ChatViewModel ViewModel, ProjectedChatAttachment Attachment);

/// <summary>Context supplied to a custom composer template.</summary>
public sealed record ChatComposerTemplateContext(ChatViewModel ViewModel);

/// <summary>Browser-selected media and the user's explicit exact-file choice.</summary>
public sealed record ChatBrowserAttachmentSelection
{
    /// <summary>Creates a validated browser attachment selection.</summary>
    public ChatBrowserAttachmentSelection(IBrowserFile file, bool sendAsFile)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        SendAsFile = sendAsFile;
    }

    /// <summary>Browser-owned file handle that must be consumed before the callback returns.</summary>
    public IBrowserFile File { get; }

    /// <summary>Whether the user explicitly requested byte-exact file transfer.</summary>
    public bool SendAsFile { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"ChatBrowserAttachmentSelection(Size={File.Size}, SendAsFile={SendAsFile}, Metadata=[REDACTED])";
}

/// <summary>Host-owned browser attachment pipeline. False represents an expected generic failure.</summary>
public delegate ValueTask<bool> ChatBrowserAttachmentSender(
    ChatBrowserAttachmentSelection selection,
    CancellationToken cancellationToken);

/// <summary>Default component command that returned an expected generic failure.</summary>
public enum SkopkaChatCommand
{
    /// <summary>Sending the current draft.</summary>
    SendDraft = 1,

    /// <summary>Toggling a reaction.</summary>
    ToggleReaction = 2,

    /// <summary>Preparing, encrypting, uploading and sending a selected attachment.</summary>
    SendAttachment = 3,

    /// <summary>Sending a text or attachment-caption edit.</summary>
    EditContent = 4,
}

/// <summary>Reusable defaults for the standard components.</summary>
public static class SkopkaChatDefaults
{
    private static readonly IReadOnlyList<string> DefaultReactions =
        Array.AsReadOnly(["👍", "❤️", "😂", "😮", "😢", "🙏"]);

    /// <summary>Default quick-reaction tokens in deterministic display order.</summary>
    public static IReadOnlyList<string> Reactions => DefaultReactions;
}
