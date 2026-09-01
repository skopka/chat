namespace Skopka.Chat.Media.FFmpeg;

/// <summary>Optional local FFmpeg implementation for compressed JPEG photos and H.264/AAC MP4 videos.</summary>
public sealed class FfmpegMediaPreparationService : IMediaPreparationService
{
    private static readonly HashSet<string> SupportedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/heic", "image/heif", "image/avif", "image/bmp", "image/tiff"
    };
    private readonly FfmpegMediaPreparationOptions _options;
    private readonly IFfmpegProcessRunner _runner;
    private readonly string _workingDirectory;

    /// <summary>Creates an FFmpeg adapter over a host-protected plaintext working directory.</summary>
    public FfmpegMediaPreparationService(FfmpegMediaPreparationOptions options)
        : this(options, new FfmpegProcessRunner())
    {
    }

    internal FfmpegMediaPreparationService(
        FfmpegMediaPreparationOptions options,
        IFfmpegProcessRunner runner)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _workingDirectory = _options.ValidateAndGetWorkingDirectory();
    }

    /// <inheritdoc />
    public async ValueTask<PreparedMedia> PrepareAsync(
        MediaPreparationRequest request,
        IProgress<MediaPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SendMode == MediaSendMode.File)
        {
            progress?.Report(new MediaPreparationProgress(MediaPreparationStage.Completed));
            return BorrowOriginal(request, Classify(request.MediaType));
        }

        var kind = ClassifySupported(request.MediaType);
        if (kind is null || request.SourceLength == 0)
        {
            if (request.SendMode == MediaSendMode.Media)
            {
                throw new MediaPreparationException("Media format is not supported for transformation.");
            }

            progress?.Report(new MediaPreparationProgress(MediaPreparationStage.Completed));
            return BorrowOriginal(request, Classify(request.MediaType));
        }

        var operationDirectory = Path.Combine(_workingDirectory, $"skopka-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationDirectory);
        var inputPath = Path.Combine(operationDirectory, "input.bin");
        var outputPath = Path.Combine(
            operationDirectory,
            kind == PreparedMediaKind.Image ? "output.jpg" : "output.mp4");
        var handedOff = false;
        try
        {
            progress?.Report(new MediaPreparationProgress(MediaPreparationStage.ReadingSource));
            await CopyExactAsync(request.Source, request.SourceLength, inputPath, cancellationToken).ConfigureAwait(false);
            progress?.Report(new MediaPreparationProgress(MediaPreparationStage.Transforming));
            var invocation = new FfmpegInvocation(
                _options.ExecutablePath,
                operationDirectory,
                BuildArguments(kind.Value, inputPath, outputPath),
                _options.ProcessingTimeout);
            int exitCode;
            try
            {
                exitCode = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (MediaPreparationException) when (request.SendMode == MediaSendMode.Auto)
            {
                return HandOffOriginal();
            }

            if (exitCode != 0 || !File.Exists(outputPath))
            {
                if (request.SendMode == MediaSendMode.Auto)
                {
                    return HandOffOriginal();
                }

                throw new MediaPreparationException("Media processing failed.");
            }

            var outputLength = new FileInfo(outputPath).Length;
            if (outputLength <= 0 || outputLength > _options.MaxOutputBytes)
            {
                if (request.SendMode == MediaSendMode.Auto)
                {
                    return HandOffOriginal();
                }

                throw new MediaPreparationException("Media processing produced an invalid output.");
            }

            progress?.Report(new MediaPreparationProgress(MediaPreparationStage.SelectingOutput));
            var useOriginal = request.SendMode == MediaSendMode.Auto && outputLength >= request.SourceLength;
            var selectedPath = useOriginal ? inputPath : outputPath;
            var unusedPath = useOriginal ? outputPath : inputPath;
            File.Delete(unusedPath);
            var stream = new CleanupFileStream(selectedPath, operationDirectory);
            handedOff = true;
            progress?.Report(new MediaPreparationProgress(MediaPreparationStage.Completed));
            return useOriginal
                ? new PreparedMedia(
                    stream,
                    request.SourceLength,
                    request.FileName,
                    request.MediaType,
                    kind.Value,
                    wasTransformed: false)
                : new PreparedMedia(
                    stream,
                    outputLength,
                    BuildOutputFileName(request.FileName, kind.Value),
                    kind == PreparedMediaKind.Image ? "image/jpeg" : "video/mp4",
                    kind.Value,
                    wasTransformed: true);

            PreparedMedia HandOffOriginal()
            {
                progress?.Report(new MediaPreparationProgress(MediaPreparationStage.SelectingOutput));
                File.Delete(outputPath);
                var stream = new CleanupFileStream(inputPath, operationDirectory);
                handedOff = true;
                progress?.Report(new MediaPreparationProgress(MediaPreparationStage.Completed));
                return new PreparedMedia(
                    stream,
                    request.SourceLength,
                    request.FileName,
                    request.MediaType,
                    kind.Value,
                    wasTransformed: false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MediaPreparationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MediaPreparationException("Media preparation failed.");
        }
        finally
        {
            if (!handedOff)
            {
                TryDeleteOperationDirectory(operationDirectory);
            }
        }
    }

    private List<string> BuildArguments(
        PreparedMediaKind kind,
        string inputPath,
        string outputPath)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", inputPath,
            "-map_metadata", "-1", "-map_chapters", "-1"
        };
        if (kind == PreparedMediaKind.Image)
        {
            arguments.AddRange([
                "-frames:v", "1",
                "-vf", $"scale=w=min({_options.ImageMaxDimension}\\,iw):h=min({_options.ImageMaxDimension}\\,ih):force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuvj420p",
                "-c:v", "mjpeg",
                "-q:v", _options.JpegQualityScale.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ]);
        }
        else
        {
            arguments.AddRange([
                "-map", "0:v:0",
                "-map", "0:a:0?",
                "-sn", "-dn",
                "-vf", $"scale=w=min({_options.VideoMaxDimension}\\,iw):h=min({_options.VideoMaxDimension}\\,ih):force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-c:v", "libx264",
                "-preset", _options.VideoPreset,
                "-crf", _options.VideoCrf.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                "-b:a", $"{_options.AudioBitrateKbps}k",
                "-ac", "2",
                "-movflags", "+faststart"
            ]);
        }

        arguments.Add(outputPath);
        return arguments;
    }

    private static PreparedMedia BorrowOriginal(MediaPreparationRequest request, PreparedMediaKind kind) =>
        new(
            request.Source,
            request.SourceLength,
            request.FileName,
            request.MediaType,
            kind,
            wasTransformed: false,
            leaveOpen: true);

    private static PreparedMediaKind? ClassifySupported(string mediaType)
    {
        if (SupportedImageTypes.Contains(mediaType))
        {
            return PreparedMediaKind.Image;
        }

        return mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            ? PreparedMediaKind.Video
            : null;
    }

    private static PreparedMediaKind Classify(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? PreparedMediaKind.Image
            : mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? PreparedMediaKind.Video
                : PreparedMediaKind.File;

    private static string BuildOutputFileName(string originalFileName, PreparedMediaKind kind)
    {
        var extension = kind == PreparedMediaKind.Image ? ".jpg" : ".mp4";
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = kind == PreparedMediaKind.Image ? "photo" : "video";
        }

        var candidate = baseName + extension;
        return System.Text.Encoding.UTF8.GetByteCount(candidate) <= Skopka.Chat.Client.ChatContentLimits.MaxFileNameUtf8Bytes
            ? candidate
            : (kind == PreparedMediaKind.Image ? "photo.jpg" : "video.mp4");
    }

    private static async ValueTask CopyExactAsync(
        Stream source,
        long sourceLength,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        var remaining = sourceLength;
        while (remaining > 0)
        {
            var requested = checked((int)Math.Min(remaining, buffer.Length));
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new MediaPreparationException("Media source ended before its declared length.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new MediaPreparationException("Media source exceeds its declared length.");
        }
    }

    private static void TryDeleteOperationDirectory(string operationDirectory)
    {
        try
        {
            if (Directory.Exists(operationDirectory))
            {
                Directory.Delete(operationDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class CleanupFileStream : Stream
    {
        private readonly FileStream _inner;
        private readonly string _operationDirectory;
        private int _disposed;

        internal CleanupFileStream(string path, string operationDirectory)
        {
            _operationDirectory = operationDirectory;
            _inner = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _inner.Dispose();
                DeleteDirectory();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                    DeleteDirectory();
                }
            }
            finally
            {
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void DeleteDirectory()
        {
            try
            {
                Directory.Delete(_operationDirectory, recursive: false);
            }
            catch (IOException)
            {
                throw new MediaPreparationException("Media work file cleanup failed.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new MediaPreparationException("Media work file cleanup failed.");
            }
        }
    }
}
