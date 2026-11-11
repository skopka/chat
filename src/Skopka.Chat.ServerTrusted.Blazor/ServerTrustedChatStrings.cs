namespace Skopka.Chat.ServerTrusted.Blazor;

/// <summary>
/// Replaceable user-visible strings for the server-trusted components. Server-trusted UI must never use end-to-end
/// encryption wording, lock icons or security codes: the server reads these conversations.
/// </summary>
public sealed record ServerTrustedChatStrings
{
    /// <summary>Default English strings.</summary>
    public static ServerTrustedChatStrings Default { get; } = new();

    /// <summary>Russian preset.</summary>
    public static ServerTrustedChatStrings Russian { get; } = new()
    {
        Timeline = "Сообщения переписки",
        EmptyConversation = "Сообщений пока нет",
        Composer = "Сообщение",
        ComposerPlaceholder = "Напишите сообщение",
        Send = "Отправить",
        Reply = "Ответить",
        ReplyTo = "Ответ на",
        ReplyUnavailable = "Более раннее сообщение",
        Edit = "Изменить",
        EditingMessage = "Редактирование сообщения",
        Delete = "Удалить",
        ConfirmDelete = "Удалить сообщение?",
        Save = "Сохранить",
        Cancel = "Отмена",
        Edited = "изменено",
        Report = "Пожаловаться",
        ReportReasonsTitle = "Причина жалобы",
        ReportComment = "Комментарий (необязательно)",
        SubmitReport = "Отправить жалобу",
        ReportSent = "Жалоба отправлена",
        LoadOlder = "Показать более ранние",
        DeletedByAuthor = "Сообщение удалено автором",
        HiddenByModerator = "Сообщение скрыто модератором",
        Erased = "Сообщение стёрто",
        ConversationClosed = "Переписка закрыта",
        ReadOnlyNotice = "Вы можете только читать эту переписку",
        ObserversNotice = "Эту переписку также видят:",
        Unavailable = "Переписка недоступна",
        GenericFailure = "Не удалось выполнить действие. Попробуйте ещё раз.",
        RateLimited = "Слишком много действий. Подождите немного.",
        Attachment = "Вложение",
        Attachments = "Вложения",
        You = "Вы",
        MessageActions = "Действия с сообщением",
        UnreadMessages = "Непрочитанные сообщения",
        UnreadOverflow = "99+",
        Bytes = "Б",
        Kilobytes = "КБ",
        Megabytes = "МБ",
        AttachFile = "Прикрепить файл",
        RemoveAttachment = "Убрать вложение",
        Uploading = "Загрузка…",
        AttachmentRejected = "Файл не принят. Проверьте тип и размер.",
        References = "Ссылки",
        RemoveReference = "Убрать ссылку",
    };

    /// <summary>Accessible timeline label.</summary>
    public string Timeline { get; init; } = "Conversation messages";

    /// <summary>Empty conversation label.</summary>
    public string EmptyConversation { get; init; } = "No messages yet";

    /// <summary>Accessible composer label.</summary>
    public string Composer { get; init; } = "Message";

    /// <summary>Composer placeholder.</summary>
    public string ComposerPlaceholder { get; init; } = "Write a message";

    /// <summary>Send action.</summary>
    public string Send { get; init; } = "Send";

    /// <summary>Reply action.</summary>
    public string Reply { get; init; } = "Reply";

    /// <summary>Reply preview label.</summary>
    public string ReplyTo { get; init; } = "Reply to";

    /// <summary>Reply preview when the target is not loaded.</summary>
    public string ReplyUnavailable { get; init; } = "Earlier message";

    /// <summary>Edit action.</summary>
    public string Edit { get; init; } = "Edit";

    /// <summary>Composer label while editing.</summary>
    public string EditingMessage { get; init; } = "Editing message";

    /// <summary>Delete action.</summary>
    public string Delete { get; init; } = "Delete";

    /// <summary>Delete confirmation prompt.</summary>
    public string ConfirmDelete { get; init; } = "Delete this message?";

    /// <summary>Save-edit action.</summary>
    public string Save { get; init; } = "Save";

    /// <summary>Cancel action.</summary>
    public string Cancel { get; init; } = "Cancel";

    /// <summary>Edited marker.</summary>
    public string Edited { get; init; } = "edited";

    /// <summary>Report action.</summary>
    public string Report { get; init; } = "Report";

    /// <summary>Report reason picker label.</summary>
    public string ReportReasonsTitle { get; init; } = "Reason for the report";

    /// <summary>Optional report comment label.</summary>
    public string ReportComment { get; init; } = "Comment (optional)";

    /// <summary>Submit-report action.</summary>
    public string SubmitReport { get; init; } = "Send report";

    /// <summary>Confirmation after a report was stored.</summary>
    public string ReportSent { get; init; } = "Report sent";

    /// <summary>Load-older action.</summary>
    public string LoadOlder { get; init; } = "Load older messages";

    /// <summary>Tombstone of an author-deleted message.</summary>
    public string DeletedByAuthor { get; init; } = "Message deleted by its author";

    /// <summary>Tombstone of a moderator-hidden message.</summary>
    public string HiddenByModerator { get; init; } = "Message hidden by a moderator";

    /// <summary>Tombstone of erased content.</summary>
    public string Erased { get; init; } = "Message erased";

    /// <summary>Shown instead of the composer when the conversation is closed.</summary>
    public string ConversationClosed { get; init; } = "This conversation is closed";

    /// <summary>Shown instead of the composer to an observer.</summary>
    public string ReadOnlyNotice { get; init; } = "You can only read this conversation";

    /// <summary>Prefix of the notice listing observers who can read the conversation.</summary>
    public string ObserversNotice { get; init; } = "This conversation is also visible to:";

    /// <summary>Shown when the conversation does not exist or may no longer be read.</summary>
    public string Unavailable { get; init; } = "This conversation is unavailable";

    /// <summary>Generic operation failure without remote details.</summary>
    public string GenericFailure { get; init; } = "The action could not be completed. Try again.";

    /// <summary>Rate-limit failure.</summary>
    public string RateLimited { get; init; } = "Too many actions. Please wait a moment.";

    /// <summary>Attachment fallback label.</summary>
    public string Attachment { get; init; } = "Attachment";

    /// <summary>Accessible label of a message's attachment list.</summary>
    public string Attachments { get; init; } = "Attachments";

    /// <summary>Own author label.</summary>
    public string You { get; init; } = "You";

    /// <summary>Accessible label of a message's action group.</summary>
    public string MessageActions { get; init; } = "Message actions";

    /// <summary>Accessible unread badge label prefix.</summary>
    public string UnreadMessages { get; init; } = "Unread messages";

    /// <summary>Badge text when more than 99 messages are unread.</summary>
    public string UnreadOverflow { get; init; } = "99+";

    /// <summary>Byte unit.</summary>
    public string Bytes { get; init; } = "B";

    /// <summary>Kilobyte unit.</summary>
    public string Kilobytes { get; init; } = "KB";

    /// <summary>Megabyte unit.</summary>
    public string Megabytes { get; init; } = "MB";

    /// <summary>Attach-file control label.</summary>
    public string AttachFile { get; init; } = "Attach file";

    /// <summary>Accessible label prefix of the remove-pending-attachment button.</summary>
    public string RemoveAttachment { get; init; } = "Remove attachment";

    /// <summary>Upload progress label.</summary>
    public string Uploading { get; init; } = "Uploading…";

    /// <summary>Generic attachment rejection (size, count, type or host validation).</summary>
    public string AttachmentRejected { get; init; } = "The file was not accepted. Check its type and size.";

    /// <summary>Accessible label of a message's or draft's host references.</summary>
    public string References { get; init; } = "Links";

    /// <summary>Accessible label prefix of the remove-draft-reference button.</summary>
    public string RemoveReference { get; init; } = "Remove link";
}
