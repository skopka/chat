using System.Security.Cryptography;
using Microsoft.Maui.Storage;
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Maui;

/// <summary>Opt-in create-only recovery key adapter over OS SecureStorage and a cooperative cross-process lease.</summary>
public sealed class SecureStorageBackupKeyStore(DeviceIdentityScope scope, ISecureStorage secureStorage, IIdentityStorageLock storageLock) : IChatBackupKeyStore
{
    private readonly ISecureStorage _storage = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
    private readonly IIdentityStorageLock _lock = storageLock ?? throw new ArgumentNullException(nameof(storageLock));
    private bool _closed;
    /// <inheritdoc />
    public DeviceIdentityScope Scope { get; } = scope ?? throw new ArgumentNullException(nameof(scope));
    private string Name => "skopka.chat.backup.v1." + Scope.StoragePartition;
    /// <inheritdoc />
    public async ValueTask<ChatBackupCredential?> LoadAsync(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        try
        {
            var encoded = await _storage.GetAsync(Name).WaitAsync(cancellationToken).ConfigureAwait(false);
            Check(cancellationToken);
            if (encoded is null) { return null; }
            if (encoded.Length > 1500) { throw new ChatBackupFormatException(); }
            var bytes = Convert.FromBase64String(encoded);
            try { return ChatBackupCredential.DecodeProtectedStorage(bytes, new(Scope.ServiceId, Scope.UserId)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ChatBackupException) { throw; }
        catch (Exception) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
    }
    /// <inheritdoc />
    public async ValueTask<bool> TryCreateAsync(ChatBackupCredential credential, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken); ArgumentNullException.ThrowIfNull(credential);
        if (credential.Archive.Scope != new ChatBackupScope(Scope.ServiceId, Scope.UserId)) { throw new ChatBackupException(ChatBackupFailure.Scope); }
        await using var lease = await _lock.AcquireAsync(Scope.StoragePartition, cancellationToken).ConfigureAwait(false);
        using var existing = await LoadAsync(cancellationToken).ConfigureAwait(false); if (existing is not null) { return false; }
        var bytes = credential.EncodeForProtectedStorage();
        try
        {
            // Never release the creation lease while the uncancellable OS write can still commit.
            await _storage.SetAsync(Name, Convert.ToBase64String(bytes)).ConfigureAwait(false);
            Check(cancellationToken); return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ChatBackupException) { throw; }
        catch (Exception) { throw new ChatBackupException(ChatBackupFailure.LocalStorage); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
    private void Check(CancellationToken token) { if (_closed) { throw new ChatBackupException(ChatBackupFailure.Locked); } token.ThrowIfCancellationRequested(); }
}
