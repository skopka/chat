using Skopka.Chat.Attachments;

namespace Skopka.Chat.Media.FFmpeg;

/// <summary>Configures bounded FFmpeg image/video preparation.</summary>
public sealed class FfmpegMediaPreparationOptions
{
    private static readonly HashSet<string> AllowedPresets = new(StringComparer.Ordinal)
    {
        "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"
    };

    /// <summary>
    /// Absolute host-protected directory for temporary plaintext. It must exist before the service is created.
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Host-installed FFmpeg executable path or trusted executable name.</summary>
    public string ExecutablePath { get; set; } = "ffmpeg";

    /// <summary>Largest width or height of a transformed photo.</summary>
    public int ImageMaxDimension { get; set; } = 1920;

    /// <summary>FFmpeg MJPEG quality scale, where smaller values mean higher quality.</summary>
    public int JpegQualityScale { get; set; } = 3;

    /// <summary>Largest width or height of a transformed video.</summary>
    public int VideoMaxDimension { get; set; } = 1280;

    /// <summary>libx264 constant-rate-factor quality value.</summary>
    public int VideoCrf { get; set; } = 24;

    /// <summary>Trusted libx264 encoding preset.</summary>
    public string VideoPreset { get; set; } = "medium";

    /// <summary>AAC target audio bitrate in kilobits per second.</summary>
    public int AudioBitrateKbps { get; set; } = 128;

    /// <summary>Maximum time allowed for one FFmpeg process.</summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Maximum transformed output size.</summary>
    public long MaxOutputBytes { get; set; } = AttachmentStorageLimits.MaxCiphertextBytes;

    internal string ValidateAndGetWorkingDirectory()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutablePath);
        if (ExecutablePath.Any(char.IsControl))
        {
            throw new ArgumentException("FFmpeg executable path is invalid.", nameof(ExecutablePath));
        }

        if (!Path.IsPathFullyQualified(WorkingDirectory))
        {
            throw new ArgumentException("FFmpeg working directory must be an existing absolute directory.", nameof(WorkingDirectory));
        }

        var fullWorkingDirectory = Path.GetFullPath(WorkingDirectory);
        if (!Directory.Exists(fullWorkingDirectory))
        {
            throw new ArgumentException("FFmpeg working directory must be an existing absolute directory.", nameof(WorkingDirectory));
        }

        if (ImageMaxDimension is < 64 or > 8192 || VideoMaxDimension is < 64 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(ImageMaxDimension), "Media dimensions are outside the supported range.");
        }

        if (JpegQualityScale is < 2 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(JpegQualityScale), "JPEG quality scale is outside the supported range.");
        }

        if (VideoCrf is < 0 or > 51)
        {
            throw new ArgumentOutOfRangeException(nameof(VideoCrf), "Video CRF is outside the supported range.");
        }

        if (!AllowedPresets.Contains(VideoPreset))
        {
            throw new ArgumentException("Video preset is not supported.", nameof(VideoPreset));
        }

        if (AudioBitrateKbps is < 32 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(AudioBitrateKbps), "Audio bitrate is outside the supported range.");
        }

        if (ProcessingTimeout < TimeSpan.FromSeconds(1) || ProcessingTimeout > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(ProcessingTimeout), "Processing timeout is outside the supported range.");
        }

        if (MaxOutputBytes <= 0 || MaxOutputBytes > AttachmentStorageLimits.MaxCiphertextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputBytes), "Output size is outside the supported range.");
        }

        return Path.TrimEndingDirectorySeparator(fullWorkingDirectory);
    }
}
