namespace Skopka.Chat.Transport.Http;

/// <summary>Opt-in backup-v1 binary routes relative to the configured chat API prefix.</summary>
public static class ChatBackupHttpRoutes
{
    /// <summary>Account archive discovery/create endpoint.</summary>
    public const string Root = "/backups";
    /// <summary>Strict binary media type; JSON and content encodings are not accepted.</summary>
    public const string ContentType = "application/octet-stream";
    /// <summary>Bounded numeric failure category; never includes provider diagnostics.</summary>
    public const string FailureHeader = "X-Skopka-Backup-Failure";
    /// <summary>Completed head endpoint.</summary>
    public static string Head(Guid archive) => $"{Root}/{archive:D}/head";
    /// <summary>Immutable contribution endpoint (begin via PUT; completed seal via GET/POST).</summary>
    public static string Version(Guid archive, Guid version) => $"{Root}/{archive:D}/versions/{version:D}";
    /// <summary>Immutable encrypted part endpoint.</summary>
    public static string Part(Guid archive, Guid version, int index) => $"{Version(archive, version)}/parts/{index}";
}
