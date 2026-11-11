namespace Skopka.Chat.ServerTrusted;

/// <summary>Hard ceilings that no host option may exceed.</summary>
public static class ServerTrustedChatLimits
{
    /// <summary>Largest configurable text length in UTF-16 code units.</summary>
    public const int MaxTextLengthCeiling = 16_000;

    /// <summary>Largest configurable attachment count per message.</summary>
    public const int MaxAttachmentsPerMessageCeiling = 10;

    /// <summary>Largest configurable attachment size; uploads are buffered in memory to hash them.</summary>
    public const long MaxAttachmentBytesCeiling = 50L * 1024 * 1024;

    /// <summary>Largest configurable participant count.</summary>
    public const int MaxParticipantsCeiling = 256;

    /// <summary>Largest page of history.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Largest configurable report-window radius.</summary>
    public const int MaxReportWindowRadiusCeiling = 50;

    /// <summary>Maximum host references per message.</summary>
    public const int MaxHostReferences = 4;

    /// <summary>Maximum host conversation key length.</summary>
    public const int MaxHostKeyLength = 200;

    /// <summary>Maximum participant label length.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>Maximum report reason code length.</summary>
    public const int MaxReasonCodeLength = 64;

    /// <summary>Maximum report comment length.</summary>
    public const int MaxReportCommentLength = 1000;

    /// <summary>Maximum attachment file name length.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>Maximum attachment MIME type length.</summary>
    public const int MaxContentTypeLength = 127;
}

/// <summary>Host-selected bounds for the server-trusted engine.</summary>
public sealed class ServerTrustedChatOptions
{
    /// <summary>Maximum message text length in UTF-16 code units (default 4000).</summary>
    public int MaxTextLength { get; set; } = 4000;

    /// <summary>Maximum attachments per message (default 3).</summary>
    public int MaxAttachmentsPerMessage { get; set; } = 3;

    /// <summary>Maximum bytes per attachment (default 10 MiB).</summary>
    public long MaxAttachmentBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Maximum participants per conversation including observers (default 32).</summary>
    public int MaxParticipants { get; set; } = 32;

    /// <summary>Messages returned before and after a reported message (default 10).</summary>
    public int ReportWindowRadius { get; set; } = 10;

    /// <summary>Per-conversation unread count cap (default 999).</summary>
    public int UnreadCountCap { get; set; } = 999;

    /// <summary>Lifetime of an uploaded but unsent attachment before cleanup (default 1 hour).</summary>
    public TimeSpan PendingAttachmentTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// When true (default), author-deleted text and prior edit versions are retained for moderation until the
    /// conversation is purged or the author is erased; participants never see them. When false they are wiped at once.
    /// </summary>
    public bool RetainDeletedAndEditedContentForModeration { get; set; } = true;

    /// <summary>Rejects settings outside the hard ceilings.</summary>
    public void Validate()
    {
        if (MaxTextLength is < 1 or > ServerTrustedChatLimits.MaxTextLengthCeiling ||
            MaxAttachmentsPerMessage is < 0 or > ServerTrustedChatLimits.MaxAttachmentsPerMessageCeiling ||
            MaxAttachmentBytes is < 1 or > ServerTrustedChatLimits.MaxAttachmentBytesCeiling ||
            MaxParticipants is < 2 or > ServerTrustedChatLimits.MaxParticipantsCeiling ||
            ReportWindowRadius is < 0 or > ServerTrustedChatLimits.MaxReportWindowRadiusCeiling ||
            UnreadCountCap is < 1 or > 100_000 ||
            PendingAttachmentTtl < TimeSpan.FromMinutes(1) || PendingAttachmentTtl > TimeSpan.FromDays(7))
        {
            throw new ArgumentException("The server-trusted chat options are invalid.");
        }
    }
}

/// <summary>Strict input validation shared by the engine and stores.</summary>
public static class ServerTrustedChatValidation
{
    /// <summary>Normalizes and validates message text; returns null when it is invalid.</summary>
    public static string? NormalizeText(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length == 0 || normalized.Length > maxLength || string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= normalized.Length || !char.IsLowSurrogate(normalized[index + 1]))
                {
                    return null;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(current) || (char.IsControl(current) && current is not '\n' and not '\t'))
            {
                return null;
            }
        }

        return normalized;
    }

    /// <summary>Whether a host conversation key is acceptable (printable ASCII, no spaces).</summary>
    public static bool IsHostKey(string? value) =>
        IsAscii(value, ServerTrustedChatLimits.MaxHostKeyLength, static c => c is > ' ' and < '\u007f');

    /// <summary>Whether a participant label or reason code is acceptable.</summary>
    public static bool IsLabel(string? value) =>
        IsAscii(value, ServerTrustedChatLimits.MaxLabelLength, static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Whether a host reference type matches <c>[a-z][a-z0-9._-]{0,63}</c>.</summary>
    public static bool IsReferenceType(string? value) =>
        value is { Length: > 0 } && char.IsAsciiLetterLower(value[0]) &&
        IsAscii(value, 64, static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');

    /// <summary>Whether a host reference identifier is acceptable (printable ASCII, at most 128 characters).</summary>
    public static bool IsReferenceId(string? value) =>
        IsAscii(value, 128, static c => c is > ' ' and < '\u007f');

    /// <summary>Whether a MIME type is a bounded <c>type/subtype</c> token.</summary>
    public static bool IsContentType(string? value)
    {
        if (!IsAscii(value, ServerTrustedChatLimits.MaxContentTypeLength, static c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '+' or '-'))
        {
            return false;
        }

        var slash = value!.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < value.Length - 1 && slash == value.LastIndexOf('/');
    }

    /// <summary>Whether an attachment file name is acceptable (no path separators or control characters).</summary>
    public static bool IsFileName(string? value) =>
        value is not null && value.Length <= ServerTrustedChatLimits.MaxFileNameLength &&
        NormalizeText(value, ServerTrustedChatLimits.MaxFileNameLength) == value &&
        value.IndexOfAny(['/', '\\', '\n', '\t', ':']) < 0 && value.Trim() == value && value is not "." and not "..";

    private static bool IsAscii(string? value, int maxLength, Func<char, bool> allowed)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!allowed(c))
            {
                return false;
            }
        }

        return true;
    }

}
