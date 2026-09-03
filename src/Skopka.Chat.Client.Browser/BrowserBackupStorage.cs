using System.Globalization;
using System.Security.Cryptography;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Browser;

/// <summary>Backup workspace encrypted by the existing IndexedDB vault; disposing closes this handle, not the shared vault.</summary>
public sealed class BrowserBackupWorkspace(BrowserVault vault) : IChatBackupWorkspace
{
    private readonly BrowserVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private bool _closed;
    /// <inheritdoc />
    public DeviceIdentityScope Scope => _vault.Scope;
    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    { Check(); return _vault.AcquireAsync("backup", cancellationToken); }
    /// <inheritdoc />
    public async ValueTask<byte[]?> ReadAsync(string group, string key, CancellationToken cancellationToken = default)
    { Check(); return (await _vault.ReadAsync("backup", Slot(group, key), cancellationToken).ConfigureAwait(false)).Data; }
    /// <inheritdoc />
    public async ValueTask<bool> WriteAsync(string group, string key, ReadOnlyMemory<byte> data, bool replace = false, CancellationToken cancellationToken = default)
    {
        Check(); ChatBackupLocalValidation.Validate(group, key, data.Length); var slot = Slot(group, key);
        var old = await _vault.ReadAsync("backup", slot, cancellationToken).ConfigureAwait(false);
        var copy = data.ToArray();
        try
        {
            if (old.Data is not null && !replace)
            {
                if (!old.Data.AsSpan().SequenceEqual(copy)) { throw new ChatBackupException(ChatBackupFailure.Conflict); }
                return false;
            }
            if (!await _vault.WriteAsync("backup", slot, group, copy, old.Revision, cancellationToken).ConfigureAwait(false))
            { throw new ChatBackupException(ChatBackupFailure.Conflict); }
            return old.Data is null;
        }
        finally { CryptographicOperations.ZeroMemory(copy); if (old.Data is not null) { CryptographicOperations.ZeroMemory(old.Data); } }
    }
    /// <inheritdoc />
    public async ValueTask<ChatBackupLocalPage> ReadPageAsync(string group, string? cursor = null, int maximumCount = 50, CancellationToken cancellationToken = default)
    {
        Check(); ChatBackupLocalValidation.Validate(group, "page");
        long after = 0;
        if (maximumCount is < 1 or > ChatBackupLimits.MaxPageSize || (cursor is not null &&
            (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))) { throw new ChatBackupFormatException(); }
        var rows = await _vault.PageAsync("backup", group, 0, after, maximumCount, cancellationToken).ConfigureAwait(false);
        var prefix = group + "-";
        if (rows.Any(row => !row.Key.StartsWith(prefix, StringComparison.Ordinal))) { throw new ChatBackupFormatException(); }
        return new(rows.Select(row => row.Key[prefix.Length..]).ToArray(), rows.Length == maximumCount ? rows[^1].Sequence.ToString(CultureInfo.InvariantCulture) : null);
    }
    /// <inheritdoc />
    public async ValueTask DeleteAsync(string group, string key, CancellationToken cancellationToken = default)
    {
        Check(); var slot = Slot(group, key); var old = await _vault.ReadAsync("backup", slot, cancellationToken).ConfigureAwait(false);
        try { if (old.Data is not null) { await _vault.RemoveAsync("backup", slot, old.Revision, cancellationToken).ConfigureAwait(false); } }
        finally { if (old.Data is not null) { CryptographicOperations.ZeroMemory(old.Data); } }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
    private void Check() { if (_closed) { throw new ChatBackupException(ChatBackupFailure.Locked); } }
    private static string Slot(string group, string key)
    { ChatBackupLocalValidation.Validate(group, key); if (group.Length + key.Length > 127) { throw new ChatBackupFormatException(); } return group + "-" + key; }
}

/// <summary>Create-only recovery credential encrypted with the local vault, separate from device keys and the vault phrase.</summary>
public sealed class BrowserBackupKeyStore(BrowserVault vault) : IChatBackupKeyStore
{
    private readonly BrowserVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private bool _closed;
    /// <inheritdoc />
    public DeviceIdentityScope Scope => _vault.Scope;
    /// <inheritdoc />
    public async ValueTask<ChatBackupCredential?> LoadAsync(CancellationToken cancellationToken = default)
    {
        Check(); var result = await _vault.ReadAsync("backupkeys", "recovery", cancellationToken).ConfigureAwait(false);
        try { return result.Data is null ? null : ChatBackupCredential.DecodeProtectedStorage(result.Data, new(Scope.ServiceId, Scope.UserId)); }
        finally { if (result.Data is not null) { CryptographicOperations.ZeroMemory(result.Data); } }
    }
    /// <inheritdoc />
    public async ValueTask<bool> TryCreateAsync(ChatBackupCredential credential, CancellationToken cancellationToken = default)
    {
        Check(); ArgumentNullException.ThrowIfNull(credential);
        if (credential.Archive.Scope != new ChatBackupScope(Scope.ServiceId, Scope.UserId)) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        var bytes = credential.EncodeForProtectedStorage();
        try { return await _vault.WriteAsync("backupkeys", "recovery", "", bytes, null, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
    private void Check() { if (_closed) { throw new ChatBackupException(ChatBackupFailure.Locked); } }
}
