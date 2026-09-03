using System.Security.Cryptography;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Storage;

/// <summary>Protected backup credential, separate from every device identity and local vault phrase.</summary>
public sealed class ChatBackupCredential : IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;
    /// <summary>Creates a bounded credential; clear the caller's temporary secret buffer.</summary>
    public ChatBackupCredential(ChatBackupArchive archive, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (key.Length != 32) { throw new ChatBackupFormatException(); }
        Archive = archive; _key = key.ToArray();
    }
    /// <summary>Non-secret archive identity.</summary>
    public ChatBackupArchive Archive { get; }
    /// <summary>Opens a controlled disposable key copy for one operation.</summary>
    public ChatBackupRecoveryKey OpenKey() { ObjectDisposedException.ThrowIf(_disposed, this); return ChatBackupRecoveryKey.FromBytes(_key); }
    /// <summary>Explicit protected-store encoding. Contains the raw recovery secret: never send or log it.</summary>
    public byte[] EncodeForProtectedStorage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var archive = ChatBackupEncoding.EncodeArchive(Archive); var result = new byte[archive.Length + 32];
        archive.CopyTo(result, 0); _key.CopyTo(result, archive.Length); return result;
    }
    /// <summary>Loads a bounded protected record and checks independently expected service/account.</summary>
    public static ChatBackupCredential DecodeProtectedStorage(ReadOnlySpan<byte> bytes, ChatBackupScope scope)
    {
        if (bytes.Length is < 33 or > ChatBackupLimits.MaxControlBytes + 32) { throw new ChatBackupFormatException(); }
        var archive = ChatBackupEncoding.DecodeArchive(bytes[..^32]);
        if (archive.Scope != scope) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        return new(archive, bytes[^32..]);
    }
    /// <summary>Clears controlled secret memory.</summary>
    public void Dispose() { if (!_disposed) { _disposed = true; CryptographicOperations.ZeroMemory(_key); } }
    /// <inheritdoc />
    public override string ToString() => "ChatBackupCredential([REDACTED])";
}

/// <summary>OS/vault-protected create-only recovery-secret boundary. Never store this secret in plain SQLite.</summary>
public interface IChatBackupKeyStore : IAsyncDisposable
{
    /// <summary>Permanent service/account/installation namespace, never sid/token scoped.</summary>
    DeviceIdentityScope Scope { get; }
    /// <summary>Loads a controlled secret copy or returns null. Corruption is not absence.</summary>
    ValueTask<ChatBackupCredential?> LoadAsync(CancellationToken cancellationToken = default);
    /// <summary>Atomically creates an absent credential; never replaces a retained recovery key.</summary>
    ValueTask<bool> TryCreateAsync(ChatBackupCredential credential, CancellationToken cancellationToken = default);
}

/// <summary>Bounded opaque local record key page; cursors are provider-owned.</summary>
public sealed record ChatBackupLocalPage(IReadOnlyList<string> Keys, string? NextCursor);

/// <summary>Durable backup workspace, including invisible restore staging and a single atomic active pointer.</summary>
/// <remarks>Restored rows contain plaintext in native adapters. Browser adapters encrypt them with the existing vault. No device keys or outbox belong here.</remarks>
public interface IChatBackupWorkspace : IAsyncDisposable
{
    /// <summary>Namespace identical to the protected key-store scope.</summary>
    DeviceIdentityScope Scope { get; }
    /// <summary>Exclusive cooperative cross-process/tab lease; release only after outstanding writes complete.</summary>
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads one bounded record; returned buffers must be cleared by the caller.</summary>
    ValueTask<byte[]?> ReadAsync(string group, string key, CancellationToken cancellationToken = default);
    /// <summary>Durably writes one record. Immutable duplicates compare exactly; different data fails. State replacement is explicit.</summary>
    ValueTask<bool> WriteAsync(string group, string key, ReadOnlyMemory<byte> data, bool replace = false, CancellationToken cancellationToken = default);
    /// <summary>Reads at most one bounded page in stable insertion order.</summary>
    ValueTask<ChatBackupLocalPage> ReadPageAsync(string group, string? cursor = null, int maximumCount = 50, CancellationToken cancellationToken = default);
    /// <summary>Removes one unreferenced local staging record. Never delete the active restore group.</summary>
    ValueTask DeleteAsync(string group, string key, CancellationToken cancellationToken = default);
}

/// <summary>Common local record validation used by durable adapters and tests.</summary>
public static class ChatBackupLocalValidation
{
    /// <summary>Rejects path-like/unbounded identifiers before provider I/O.</summary>
    public static void Validate(string group, string key, int byteCount = 0)
    {
        if (group is null || group.Length is < 1 or > 100 || key is null || key.Length is < 1 or > 100 ||
            group.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
            byteCount is < 0 or > ChatBackupLimits.MaxPartBytes) { throw new ChatBackupFormatException(); }
    }
}

/// <summary>Bounded preparation/restore policy; host server quotas remain independently authoritative.</summary>
public sealed class ChatBackupClientOptions
{
    /// <summary>Maximum event parts prepared in one update.</summary>
    public int MaximumParts { get; init; } = ChatBackupLimits.MaxParts;
    /// <summary>Maximum encrypted bytes in one update and one full restore.</summary>
    public long MaximumBytes { get; init; } = 1L << 30;
    /// <summary>Maximum ancestor chain length; prevents cycles and unbounded work.</summary>
    public int MaximumVersions { get; init; } = ChatBackupLimits.MaxVersions;
    /// <summary>Maximum merge retries before returning a retryable conflict.</summary>
    public int MaximumCommitAttempts { get; init; } = 8;
    internal void Validate()
    {
        if (MaximumParts is < 1 or > ChatBackupLimits.MaxParts || MaximumBytes is < ChatBackupLimits.MaxPartBytes or > 64L << 30 ||
            MaximumVersions is < 1 or > ChatBackupLimits.MaxVersions || MaximumCommitAttempts is < 1 or > 32) { throw new ArgumentException("Backup limits are invalid."); }
    }
}

/// <summary>UI-independent backup operation state. Contains no recovery key or event content.</summary>
public enum ChatBackupPhase
{
    /// <summary>Feature is not configured locally.</summary>
    Disabled,
    /// <summary>User must confirm a saved copy of the recovery key.</summary>
    AwaitingConfirmation,
    /// <summary>Explicitly unlocked and available.</summary>
    Ready,
    /// <summary>Preparing durable encrypted parts before network I/O.</summary>
    Preparing,
    /// <summary>Uploading/resuming exact encrypted parts.</summary>
    Uploading,
    /// <summary>Authenticating and staging history without exposing a partial restore.</summary>
    Restoring,
    /// <summary>Last requested operation completed durably.</summary>
    Completed,
    /// <summary>Generic failure; retry may resume durable work.</summary>
    Failed,
    /// <summary>Logout/account switch closed this session.</summary>
    Locked,
}

/// <summary>Reusable safe UI snapshot. LastBackupAt is authenticated client time, not trusted clock proof.</summary>
public sealed record ChatBackupStatus(ChatBackupPhase Phase, long ProcessedParts = 0, long ProcessedBytes = 0,
    DateTimeOffset? LastBackupAt = null, ChatBackupFailure? Failure = null);
