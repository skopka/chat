using System.Diagnostics;

namespace Skopka.Chat.Client.Maui;

/// <summary>Exclusive bounded lease shared by all processes accessing a protected identity namespace.</summary>
public interface IIdentityStorageLock
{
    /// <summary>Acquires a lock for a 64-character hexadecimal partition identifier.</summary>
    ValueTask<IAsyncDisposable> AcquireAsync(string partition, CancellationToken cancellationToken = default);
}

/// <summary>Cross-process lock files in a host-protected installation directory; files contain no identity data.</summary>
public sealed class FileIdentityStorageLock : IIdentityStorageLock
{
    private readonly string _directory;
    private readonly TimeSpan _timeout;
    /// <summary>Creates a lock adapter. All app processes must use the same protected directory; do not delete live lock files.</summary>
    public FileIdentityStorageLock(string protectedDirectory, TimeSpan? acquisitionTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedDirectory);
        _directory = Path.GetFullPath(protectedDirectory);
        _timeout = acquisitionTimeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30)) { throw new ArgumentOutOfRangeException(nameof(acquisitionTimeout)); }
    }
    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(string partition, CancellationToken cancellationToken = default)
    {
        if (partition is null || partition.Length != 64 || partition.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Invalid identity lock partition.");
        }
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(_directory);
                return new FileLease(new FileStream(Path.Combine(_directory, partition + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous));
            }
            catch (IOException) when (elapsed.Elapsed < _timeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new DeviceIdentityStorageException(PersistentDeviceIdentityState.Unavailable);
            }
        }
    }
    private sealed class FileLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

// Compatibility for the legacy user-only key-store constructor. New persistent identity stores require
// an explicit cross-process provider. One gate avoids an unbounded attacker-controlled lock dictionary.
internal sealed class ProcessIdentityStorageLock : IIdentityStorageLock
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async ValueTask<IAsyncDisposable> AcquireAsync(string partition, CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
        {
            throw new DeviceIdentityStorageException(PersistentDeviceIdentityState.Unavailable);
        }
        return new Lease();
    }
    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { Gate.Release(); return ValueTask.CompletedTask; }
    }
}
