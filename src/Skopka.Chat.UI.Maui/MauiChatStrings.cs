namespace Skopka.Chat.UI.Maui;

/// <summary>Replaceable user-visible strings for the native MAUI conversation surface.</summary>
public sealed record MauiChatStrings
{
    /// <summary>Default English strings.</summary>
    public static MauiChatStrings Default { get; } = new();

    /// <summary>Timeline accessibility label.</summary>
    public string Timeline { get; init; } = "Conversation messages";
    /// <summary>Empty-state label.</summary>
    public string EmptyConversation { get; init; } = "No messages yet";
    /// <summary>Loading-state label.</summary>
    public string Loading { get; init; } = "Loading messages…";
    /// <summary>Own-sender label.</summary>
    public string You { get; init; } = "You";
    /// <summary>Remote-sender label.</summary>
    public string Contact { get; init; } = "Contact";
    /// <summary>Forward marker.</summary>
    public string Forwarded { get; init; } = "Forwarded";
    /// <summary>Reply prefix.</summary>
    public string ReplyTo { get; init; } = "Reply to";
    /// <summary>Unavailable reply-target label.</summary>
    public string ReplyUnavailable { get; init; } = "Message is unavailable";
    /// <summary>Reply action label.</summary>
    public string Reply { get; init; } = "Reply";
    /// <summary>Forward action label.</summary>
    public string Forward { get; init; } = "Forward";
    /// <summary>Edit action label.</summary>
    public string Edit { get; init; } = "Edit";
    /// <summary>Edited-content marker.</summary>
    public string Edited { get; init; } = "edited";
    /// <summary>Generic attachment label.</summary>
    public string Attachment { get; init; } = "Attachment";
    /// <summary>Attachment download action label.</summary>
    public string Download { get; init; } = "Download";
    /// <summary>Attachment send action label.</summary>
    public string Attach { get; init; } = "Attach";
    /// <summary>Composer placeholder.</summary>
    public string ComposerPlaceholder { get; init; } = "Write a message";
    /// <summary>Send action label.</summary>
    public string Send { get; init; } = "Send";
    /// <summary>In-progress send-state label.</summary>
    public string Sending { get; init; } = "Sending…";
    /// <summary>Cancel action label.</summary>
    public string Cancel { get; init; } = "Cancel";
    /// <summary>Generic expected-failure label.</summary>
    public string CommandFailed { get; init; } = "The operation failed. Try again.";
}
