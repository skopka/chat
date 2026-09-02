using System.Text;
using Microsoft.Maui.Storage;

namespace Skopka.Chat.Client.Maui;

/// <summary>Display metadata and an owned app-private plaintext file with deterministic cleanup.</summary>
public sealed class MauiPrivatePlaintextFile : IAsyncDisposable
{
    private readonly string _path;
    private bool _disposed;

    internal MauiPrivatePlaintextFile(string path, string fileName, string mediaType, long length)
    {
        _path = path;
        FileName = fileName;
        MediaType = mediaType;
        Length = length;
    }

    /// <summary>Untrusted display name; it is never used as a local path.</summary>
    public string FileName { get; }

    /// <summary>Untrusted media-type claim; it is never automatically opened.</summary>
    public string MediaType { get; }

    /// <summary>Exact bounded plaintext length.</summary>
    public long Length { get; }

    /// <summary>Opens the generated app-private path for a host callback.</summary>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        TryDelete(_path);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"MauiPrivatePlaintextFile(FileName=[REDACTED], MediaType=[REDACTED], Length={Length})";

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Copies selected or decrypted plaintext into generated app-private files and delegates all use to the host.
/// </summary>
public sealed class MauiProtectedFileService
{
    private const int BufferBytes = 64 * 1024;
    private const int MaximumDisplayNameUtf8Bytes = 512;
    private const int MaximumMediaTypeCharacters = 127;
    private readonly IFilePicker _filePicker;
    private readonly string _privateDirectory;

    /// <summary>Creates the service under MAUI's app-data directory, never a public/shared path.</summary>
    public MauiProtectedFileService(
        IFilePicker filePicker,
        IFileSystem fileSystem,
        string directoryName = "skopka-chat-plaintext")
    {
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (string.IsNullOrWhiteSpace(directoryName) || directoryName != Path.GetFileName(directoryName))
        {
            throw new ArgumentException("The private directory name is invalid.", nameof(directoryName));
        }

        _privateDirectory = Path.GetFullPath(Path.Combine(fileSystem.AppDataDirectory, directoryName));
        var appData = Path.GetFullPath(fileSystem.AppDataDirectory);
        if (!_privateDirectory.StartsWith(appData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The plaintext directory must remain inside app data.", nameof(directoryName));
        }

        Directory.CreateDirectory(_privateDirectory);
    }

    /// <summary>
    /// Selects one host-configured file/photo, copies it with pre-read and streaming bounds, and invokes a callback.
    /// The generated plaintext file is removed after success, failure or cancellation.
    /// </summary>
    public async ValueTask<bool> PickAndUseAsync(
        PickOptions pickOptions,
        long maximumBytes,
        Func<MauiPrivatePlaintextFile, CancellationToken, ValueTask> useAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pickOptions);
        ArgumentNullException.ThrowIfNull(useAsync);
        ValidateMaximum(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();

        FileResult? selected;
        try
        {
            selected = await _filePicker.PickAsync(pickOptions)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IOException("The platform file picker failed.");
        }

        if (selected is null)
        {
            return false;
        }

        var path = CreateGeneratedPath();
        try
        {
            await using var source = await selected.OpenReadAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!source.CanRead || (source.CanSeek && source.Length > maximumBytes))
            {
                throw new IOException("The selected file exceeds the configured limit.");
            }

            var length = await CopyBoundedAsync(source, path, maximumBytes, cancellationToken).ConfigureAwait(false);
            await using var owned = new MauiPrivatePlaintextFile(
                path,
                NormalizeFileName(selected.FileName),
                NormalizeMediaType(selected.ContentType),
                length);
            await useAsync(owned, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MauiPrivatePlaintextFile.TryDelete(path);
            throw;
        }
        catch
        {
            MauiPrivatePlaintextFile.TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// Writes authenticated decrypted bytes through a bounded stream, invokes the host callback, then cleans up.
    /// The library never previews, opens or shares the file itself.
    /// </summary>
    public async ValueTask UseDecryptedAsync(
        string fileName,
        string mediaType,
        long maximumBytes,
        Func<Stream, CancellationToken, ValueTask> decryptToAsync,
        Func<MauiPrivatePlaintextFile, CancellationToken, ValueTask> useAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decryptToAsync);
        ArgumentNullException.ThrowIfNull(useAsync);
        ValidateMaximum(maximumBytes);
        var path = CreateGeneratedPath();
        try
        {
            await using (var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var bounded = new BoundedWriteStream(destination, maximumBytes))
            {
                await decryptToAsync(bounded, cancellationToken).ConfigureAwait(false);
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var length = new FileInfo(path).Length;
            if (length > maximumBytes)
            {
                throw new IOException("The decrypted file exceeds the configured limit.");
            }

            await using var owned = new MauiPrivatePlaintextFile(
                path,
                NormalizeFileName(fileName),
                NormalizeMediaType(mediaType),
                length);
            await useAsync(owned, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            MauiPrivatePlaintextFile.TryDelete(path);
            throw;
        }
    }

    /// <summary>Removes stale generated plaintext files after an abnormal previous termination.</summary>
    public int CleanupStaleFiles(DateTimeOffset olderThan, int maximumCount = 100)
    {
        if (olderThan == default || maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentException("The plaintext cleanup bounds are invalid.");
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_privateDirectory, "skopka-*.tmp")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Take(maximumCount))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < olderThan.UtcDateTime)
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    private static async ValueTask<long> CopyBoundedAsync(
        Stream source,
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferBytes];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new IOException("The selected file exceeds the configured limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    private string CreateGeneratedPath() =>
        Path.Combine(_privateDirectory, $"skopka-{Guid.NewGuid():N}.tmp");

    private static string NormalizeFileName(string? value)
    {
        var name = Path.GetFileName(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(name) > MaximumDisplayNameUtf8Bytes)
        {
            return "attachment";
        }

        return name;
    }

    private static string NormalizeMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMediaTypeCharacters ||
            value.Any(character => character is < (char)0x20 or > (char)0x7E))
        {
            return "application/octet-stream";
        }

        return value;
    }

    private static void ValidateMaximum(long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
    }

    private sealed class BoundedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            _written += buffer.Length;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || _written > maximumBytes - count)
            {
                throw new IOException("The plaintext file exceeds the configured limit.");
            }
        }
    }
}
