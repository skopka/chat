using System.Security.Cryptography;
using Microsoft.JSInterop;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Browser;

/// <summary>Local vault failure. No platform text, passphrase or record content is included.</summary>
public sealed class BrowserStorageException : DeviceIdentityStorageException
{
    internal BrowserStorageException(string code) : base(code is "corrupt" ? PersistentDeviceIdentityState.Corrupt :
        code is "recovery" or "unlock-failed" or "phrase-required" ? PersistentDeviceIdentityState.RecoveryRequired : PersistentDeviceIdentityState.Unavailable)
        => Code = code;
    /// <summary>Bounded local error code; unlock failure may mean a wrong phrase or damaged ciphertext.</summary>
    public string Code { get; }
}

/// <summary>An unlocked account/installation encrypted IndexedDB vault. Disposing locks memory, never deletes identity.</summary>
public sealed class BrowserVault : IAsyncDisposable
{
    private readonly IJSObjectReference _module;
    private readonly string _handle;
    private bool _disposed;
    private BrowserVault(IJSObjectReference module, string handle, DeviceIdentityScope scope)
    { _module = module; _handle = handle; Scope = scope; }

    /// <summary>Permanent account/installation namespace, independent of login sessions.</summary>
    public DeviceIdentityScope Scope { get; }

    /// <summary>Loads an origin installation ID; creation occurs only when explicitly requested.</summary>
    public static async ValueTask<Guid?> GetInstallationIdAsync(IJSRuntime runtime, bool create = false, CancellationToken cancellationToken = default)
    {
        await using var module = await ImportAsync(runtime, cancellationToken).ConfigureAwait(false);
        var result = await InvokeAsync(module, "installation", create).ConfigureAwait(false);
        if (result.Status == "absent") { return null; }
        RequireSuccess(result);
        return Guid.TryParseExact(result.Value, "D", out var id) && id != Guid.Empty ? id : throw new BrowserStorageException("corrupt");
    }

    /// <summary>Unlocks or explicitly creates a vault using a separate local phrase (12–1024 UTF-8 bytes).</summary>
    /// <remarks>Never pass an account password. Clear the caller's byte buffer; forgotten phrases cannot be recovered by the service.</remarks>
    public static async ValueTask<BrowserVault> OpenAsync(IJSRuntime runtime, DeviceIdentityScope scope,
        ReadOnlyMemory<byte> localPassphrase, bool create = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (localPassphrase.Length is < 12 or > 1024) { throw new ArgumentException("The local vault phrase must contain 12–1024 UTF-8 bytes.", nameof(localPassphrase)); }
        var module = await ImportAsync(runtime, cancellationToken).ConfigureAwait(false);
        var copy = localPassphrase.ToArray();
        try
        {
            var result = await InvokeAsync(module, "unlock", scope.StoragePartition, scope.InstallationId.ToString("D"), copy, create).ConfigureAwait(false);
            RequireSuccess(result);
            return new BrowserVault(module, result.Value ?? throw new BrowserStorageException("corrupt"), scope);
        }
        catch { await module.DisposeAsync().ConfigureAwait(false); throw; }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <summary>Opens a vault with its browser-bound non-exportable key, or creates one without a phrase.</summary>
    /// <remarks>
    /// The key remains scoped to the browser profile and origin. This protects exported IndexedDB ciphertext,
    /// but not an unlocked browser profile or compromised same-origin application code.
    /// Legacy phrase vaults return <c>phrase-required</c> until <see cref="RememberForDeviceAsync"/> is called.
    /// </remarks>
    public static async ValueTask<BrowserVault> OpenTrustedAsync(IJSRuntime runtime, DeviceIdentityScope scope,
        bool create = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var module = await ImportAsync(runtime, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await InvokeAsync(module, "trusted", scope.StoragePartition,
                scope.InstallationId.ToString("D"), create).ConfigureAwait(false);
            RequireSuccess(result);
            return new BrowserVault(module, result.Value ?? throw new BrowserStorageException("corrupt"), scope);
        }
        catch { await module.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Persists this unlocked vault's key as a non-exportable key in the current browser profile.</summary>
    /// <remarks>This is a one-time opt-in migration for a legacy phrase-protected vault; no record is re-encrypted.</remarks>
    public async ValueTask RememberForDeviceAsync(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        RequireSuccess(await CallAsync("remember", Scope.InstallationId.ToString("D")).ConfigureAwait(false));
    }

    /// <summary>Permanently deletes every record in one legacy phrase vault so it can be replaced.</summary>
    /// <remarks>
    /// This operation is intentionally explicit and irreversible. It rejects an already browser-bound vault and
    /// does not unregister the old public device from a server. The host must obtain informed authorization.
    /// </remarks>
    public static async ValueTask DiscardLegacyAsync(IJSRuntime runtime, DeviceIdentityScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var module = await ImportAsync(runtime, cancellationToken).ConfigureAwait(false);
        RequireSuccess(await InvokeAsync(module, "discardLegacy", scope.StoragePartition,
            scope.InstallationId.ToString("D")).ConfigureAwait(false));
    }

    /// <summary>Cooperative cross-tab lease. Tab termination releases it; acquisition is bounded to ten seconds.</summary>
    public async ValueTask<IAsyncDisposable> AcquireAsync(string operation, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        var result = await CallAsync("lock", operation).ConfigureAwait(false);
        RequireSuccess(result);
        var lease = new Lease(_module, result.Value ?? throw new BrowserStorageException("corrupt"));
        if (cancellationToken.IsCancellationRequested)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return lease;
    }

    internal async ValueTask<VaultResult> ReadAsync(string kind, string key, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        var result = await CallAsync("read", kind, key).ConfigureAwait(false);
        if (result.Status != "absent") { RequireSuccess(result); }
        return result;
    }

    internal async ValueTask<bool> WriteAsync(string kind, string key, string partition, byte[] data, string? revision, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        var result = await CallAsync("write", kind, key, partition, data, revision).ConfigureAwait(false);
        if (result.Status == "conflict") { return false; }
        RequireSuccess(result);
        return true;
    }

    internal async ValueTask RemoveAsync(string kind, string key, string? revision, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        RequireSuccess(await CallAsync("remove", kind, key, revision).ConfigureAwait(false));
    }

    internal async ValueTask<VaultRow[]> PageAsync(string kind, string? partition, long before, long after, int count, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        if (count is < 1 or > 200 || before < 0 || after < 0) { throw new ArgumentOutOfRangeException(nameof(count)); }
        var result = await CallAsync("page", kind, partition, before, after, count).ConfigureAwait(false);
        RequireSuccess(result);
        return result.Rows ?? throw new BrowserStorageException("corrupt");
    }

    /// <summary>Releases this page's vault key and leases without deleting any local data.</summary>
    /// <remarks>The host must cancel and await active session operations before disposing the vault.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { await _module.InvokeVoidAsync("close", _handle).ConfigureAwait(false); }
        catch (JSException) { /* Do not reflect platform exceptions during logout. */ }
        await _module.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override string ToString() => "BrowserVault([REDACTED])";

    private void Check(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
    }
    private ValueTask<VaultResult> CallAsync(string operation, params object?[] arguments) =>
        InvokeAsync(_module, operation, [_handle, .. arguments]);

    private static async ValueTask<IJSObjectReference> ImportAsync(IJSRuntime runtime, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsBrowser() || runtime is not IJSInProcessRuntime) { throw new PlatformNotSupportedException("Browser vault requires WebAssembly."); }
        try { return await runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./_content/Skopka.Chat.Client.Browser/vault.mjs").ConfigureAwait(false); }
        catch (JSException) { throw new BrowserStorageException("unavailable"); }
    }
    private static async ValueTask<VaultResult> InvokeAsync(IJSObjectReference module, string operation, params object?[] arguments)
    {
        // Once a write starts, await its transaction result even if cancellation arrives. A caller never abandons a live creation lease.
        try { return await module.InvokeAsync<VaultResult>(operation, arguments).ConfigureAwait(false); }
        catch (JSException) { throw new BrowserStorageException("unavailable"); }
    }
    internal static void RequireSuccess(VaultResult result)
    {
        if (result.Status != "ok")
        {
            throw new BrowserStorageException(result.Status switch
            {
                "absent" or "exists" or "locked" or "unlock-failed" or "phrase-required" or "corrupt" or "recovery" or "conflict" or "quota" => result.Status,
                _ => "unavailable"
            });
        }
    }
    private sealed class Lease(IJSObjectReference module, string token) : IAsyncDisposable
    {
        private bool _released;
        public async ValueTask DisposeAsync()
        {
            if (_released) { return; }
            _released = true;
            try { await module.InvokeVoidAsync("release", token).ConfigureAwait(false); }
            catch (JSException) { throw new BrowserStorageException("unavailable"); }
        }
    }
}

internal sealed class VaultResult
{
    public string Status { get; set; } = "unavailable";
    public string? Value { get; set; }
    public string? Revision { get; set; }
    public string? Partition { get; set; }
    public string? Key { get; set; }
    public long Sequence { get; set; }
    public byte[]? Data { get; set; }
    public VaultRow[]? Rows { get; set; }
}
internal sealed class VaultRow
{
    public string Key { get; set; } = "";
    public long Sequence { get; set; }
}
