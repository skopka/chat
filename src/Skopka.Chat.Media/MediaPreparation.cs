using System.Text;
using Skopka.Chat.Attachments;
using Skopka.Chat.Client;

namespace Skopka.Chat.Media;

/// <summary>Controls whether an outgoing local file may be transformed before encryption.</summary>
public enum MediaSendMode
{
    /// <summary>Compress supported media, but keep the original when transformation is unavailable or not smaller.</summary>
    Auto = 1,

    /// <summary>Require supported media transformation and use its output even when it is not smaller.</summary>
    Media = 2,

    /// <summary>Preserve the exact source bytes, name and media type.</summary>
    File = 3,
}

/// <summary>Classification of a prepared outgoing item.</summary>
public enum PreparedMediaKind
{
    /// <summary>An opaque file.</summary>
    File = 1,

    /// <summary>A still image.</summary>
    Image = 2,

    /// <summary>A video, optionally with audio.</summary>
    Video = 3,
}

/// <summary>Observable stages of media preparation without exposing local paths or content.</summary>
public enum MediaPreparationStage
{
    /// <summary>The exact bounded source is being copied to private working storage.</summary>
    ReadingSource = 1,

    /// <summary>A host-selected media implementation is transforming the source.</summary>
    Transforming = 2,

    /// <summary>The transformed and original candidates are being compared.</summary>
    SelectingOutput = 3,

    /// <summary>The prepared stream is ready for encryption.</summary>
    Completed = 4,
}

/// <summary>Redacted progress notification for one preparation operation.</summary>
public readonly record struct MediaPreparationProgress(MediaPreparationStage Stage);

/// <summary>One caller-owned source and its requested send semantics.</summary>
public sealed class MediaPreparationRequest
{
    /// <summary>Creates a validated media preparation request.</summary>
    public MediaPreparationRequest(
        Stream source,
        long sourceLength,
        string fileName,
        string mediaType,
        MediaSendMode sendMode = MediaSendMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Media source must be readable.", nameof(source));
        }

        if (sourceLength < 0 || sourceLength > AttachmentStorageLimits.MaxCiphertextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLength), "Media source length is outside the supported range.");
        }

        MediaValidation.RequireFileName(fileName, nameof(fileName));
        MediaValidation.RequireMediaType(mediaType, nameof(mediaType));
        if (sendMode is not MediaSendMode.Auto and not MediaSendMode.Media and not MediaSendMode.File)
        {
            throw new ArgumentOutOfRangeException(nameof(sendMode), "Unknown media send mode.");
        }

        Source = source;
        SourceLength = sourceLength;
        FileName = fileName;
        MediaType = mediaType;
        SendMode = sendMode;
    }

    /// <summary>Caller-owned stream positioned at the first source byte.</summary>
    public Stream Source { get; }

    /// <summary>Exact number of remaining source bytes.</summary>
    public long SourceLength { get; }

    /// <summary>Path-free display file name.</summary>
    public string FileName { get; }

    /// <summary>Caller-declared input media type.</summary>
    public string MediaType { get; }

    /// <summary>Requested transformation behavior.</summary>
    public MediaSendMode SendMode { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"MediaPreparationRequest(SourceLength={SourceLength}, SendMode={SendMode}, Metadata=[REDACTED])";
}

/// <summary>An owned or borrowed stream ready for attachment encryption.</summary>
public sealed class PreparedMedia : IAsyncDisposable
{
    private readonly bool _leaveOpen;
    private int _disposed;

    /// <summary>Creates a validated prepared stream. Custom implementations may return a borrowed stream.</summary>
    public PreparedMedia(
        Stream content,
        long length,
        string fileName,
        string mediaType,
        PreparedMediaKind kind,
        bool wasTransformed,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Prepared media stream must be readable.", nameof(content));
        }

        if (length < 0 || length > AttachmentStorageLimits.MaxCiphertextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Prepared media length is outside the supported range.");
        }

        MediaValidation.RequireFileName(fileName, nameof(fileName));
        MediaValidation.RequireMediaType(mediaType, nameof(mediaType));
        if (kind is not PreparedMediaKind.File and not PreparedMediaKind.Image and not PreparedMediaKind.Video)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Unknown prepared media kind.");
        }

        Content = content;
        Length = length;
        FileName = fileName;
        MediaType = mediaType;
        Kind = kind;
        WasTransformed = wasTransformed;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Stream positioned at the first prepared byte.</summary>
    public Stream Content { get; }

    /// <summary>Exact prepared plaintext length.</summary>
    public long Length { get; }

    /// <summary>Manifest file name after any transformation.</summary>
    public string FileName { get; }

    /// <summary>Manifest media type after any transformation.</summary>
    public string MediaType { get; }

    /// <summary>Prepared item classification.</summary>
    public PreparedMediaKind Kind { get; }

    /// <summary>Whether the returned bytes differ from the source due to media transformation.</summary>
    public bool WasTransformed { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && !_leaveOpen)
        {
            await Content.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"PreparedMedia(Length={Length}, Kind={Kind}, WasTransformed={WasTransformed}, Metadata=[REDACTED])";
}

/// <summary>Client-side boundary for media transformation before attachment encryption.</summary>
public interface IMediaPreparationService
{
    /// <summary>Prepares one source and returns a stream positioned at its first byte.</summary>
    ValueTask<PreparedMedia> PrepareAsync(
        MediaPreparationRequest request,
        IProgress<MediaPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Exact-byte implementation useful for hosts that do not enable media transformation.</summary>
public sealed class PassthroughMediaPreparationService : IMediaPreparationService
{
    /// <inheritdoc />
    public ValueTask<PreparedMedia> PrepareAsync(
        MediaPreparationRequest request,
        IProgress<MediaPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new MediaPreparationProgress(MediaPreparationStage.Completed));
        return ValueTask.FromResult(new PreparedMedia(
            request.Source,
            request.SourceLength,
            request.FileName,
            request.MediaType,
            Classify(request.MediaType),
            wasTransformed: false,
            leaveOpen: true));
    }

    private static PreparedMediaKind Classify(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? PreparedMediaKind.Image
            : mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? PreparedMediaKind.Video
                : PreparedMediaKind.File;
}

/// <summary>Generic bounded media preparation failure without paths or process output.</summary>
public sealed class MediaPreparationException : InvalidOperationException
{
    /// <summary>Creates a redacted preparation error.</summary>
    public MediaPreparationException(string message) : base(message)
    {
    }
}

internal static class MediaValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void RequireFileName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        int length;
        try
        {
            length = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("File name must contain valid Unicode.", parameterName);
        }

        if (length > ChatContentLimits.MaxFileNameUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, "File name exceeds its limit.");
        }

        if (value is "." or ".." || value.Any(static character => char.IsControl(character) || character is '/' or '\\'))
        {
            throw new ArgumentException("File name must not contain paths or control characters.", parameterName);
        }
    }

    internal static void RequireMediaType(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > ChatContentLimits.MaxMediaTypeAsciiBytes ||
            value.Any(static character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new ArgumentException("Media type must be bounded printable ASCII.", parameterName);
        }
    }
}
