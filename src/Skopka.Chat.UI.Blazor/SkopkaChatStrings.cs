namespace Skopka.Chat.UI.Blazor;

/// <summary>Replaceable user-visible strings for the default Blazor components.</summary>
public sealed record SkopkaChatStrings
{
    /// <summary>Default English strings.</summary>
    public static SkopkaChatStrings Default { get; } = new();

    /// <summary>Empty timeline label.</summary>
    public string EmptyConversation { get; init; } = "No messages yet";

    /// <summary>Accessible timeline label.</summary>
    public string Timeline { get; init; } = "Conversation messages";

    /// <summary>Current-user sender label.</summary>
    public string You { get; init; } = "You";

    /// <summary>Fallback peer sender label.</summary>
    public string Contact { get; init; } = "Contact";

    /// <summary>Forward marker.</summary>
    public string Forwarded { get; init; } = "Forwarded";

    /// <summary>Reply metadata label.</summary>
    public string ReplyTo { get; init; } = "Reply to";

    /// <summary>Missing reply target label.</summary>
    public string ReplyUnavailable { get; init; } = "Message is unavailable";

    /// <summary>Reaction group label.</summary>
    public string Reactions { get; init; } = "Reactions";

    /// <summary>Reply action label.</summary>
    public string Reply { get; init; } = "Reply";

    /// <summary>Forward action label.</summary>
    public string Forward { get; init; } = "Forward";

    /// <summary>Edit action label.</summary>
    public string Edit { get; init; } = "Edit";

    /// <summary>Marker shown after an authenticated edit is applied.</summary>
    public string Edited { get; init; } = "edited";

    /// <summary>Composer mode label for editing a text message.</summary>
    public string EditingMessage { get; init; } = "Editing message";

    /// <summary>Composer mode label for editing an attachment caption.</summary>
    public string EditingCaption { get; init; } = "Editing caption";

    /// <summary>Attachment fallback label.</summary>
    public string Attachment { get; init; } = "Attachment";

    /// <summary>Download/decrypt action label.</summary>
    public string Download { get; init; } = "Download";

    /// <summary>Select-photo-or-video action label.</summary>
    public string AttachMedia { get; init; } = "Photo or video";

    /// <summary>Exact-file mode label.</summary>
    public string SendAsFile { get; init; } = "Send as file";

    /// <summary>Media pipeline busy label.</summary>
    public string PreparingMedia { get; init; } = "Preparing media…";

    /// <summary>Browser-selected file exceeded the host limit.</summary>
    public string MediaTooLarge { get; init; } = "The selected file is too large.";

    /// <summary>Composer label.</summary>
    public string Composer { get; init; } = "Message";

    /// <summary>Composer placeholder.</summary>
    public string ComposerPlaceholder { get; init; } = "Write a message";

    /// <summary>Send action label.</summary>
    public string Send { get; init; } = "Send";

    /// <summary>Save-edit action label.</summary>
    public string Save { get; init; } = "Save";

    /// <summary>Cancel action label.</summary>
    public string Cancel { get; init; } = "Cancel";

    /// <summary>Generic expected command failure without remote details.</summary>
    public string CommandFailed { get; init; } = "The message could not be sent. Try again.";

    /// <summary>Draft-size validation label.</summary>
    public string DraftTooLong { get; init; } = "The message is too long.";
}
