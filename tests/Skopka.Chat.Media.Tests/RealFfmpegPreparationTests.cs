using System.Diagnostics;
using Skopka.Chat.Media.FFmpeg;

namespace Skopka.Chat.Media.Tests;

public sealed class RealFfmpegPreparationTests
{
    private const string SyntheticMetadata = "skopka-chat-synthetic-metadata";

    [Fact]
    public async Task Installed_ffmpeg_transforms_synthetic_photo_and_video_and_cleans_plaintext_work_files()
    {
        var ffmpegPath = GetFfmpegPathOrSkip();
        var ffprobePath = GetFfprobePath(ffmpegPath);
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"skopka-chat-ffmpeg-tests-{Guid.NewGuid():N}");
        var workingDirectory = Path.Combine(testRoot, "work");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var imageInput = Path.Combine(testRoot, "synthetic.png");
            var imageOutput = Path.Combine(testRoot, "prepared.jpg");
            await RunProcessAsync(ffmpegPath, testRoot,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=2560x1440:rate=1",
                "-frames:v", "1", "-metadata", $"comment={SyntheticMetadata}", imageInput
            ]);

            var service = new FfmpegMediaPreparationService(new FfmpegMediaPreparationOptions
            {
                ExecutablePath = ffmpegPath,
                WorkingDirectory = workingDirectory,
                ProcessingTimeout = TimeSpan.FromMinutes(2)
            });
            await using (var imageSource = File.OpenRead(imageInput))
            await using (var image = await service.PrepareAsync(new MediaPreparationRequest(
                imageSource,
                imageSource.Length,
                "synthetic.png",
                "image/png",
                MediaSendMode.Media)))
            await using (var imageDestination = File.Create(imageOutput))
            {
                Assert.True(image.WasTransformed);
                Assert.Equal("image/jpeg", image.MediaType);
                Assert.Equal("synthetic.jpg", image.FileName);
                await image.Content.CopyToAsync(imageDestination);
            }

            var imageProbe = await ProbeVideoStreamAsync(ffprobePath, testRoot, imageOutput);
            Assert.Equal("mjpeg", GetProbeValue(imageProbe, "codec_name"));
            Assert.Equal("1920", GetProbeValue(imageProbe, "width"));
            Assert.Equal("1080", GetProbeValue(imageProbe, "height"));
            Assert.Equal("yuvj420p", GetProbeValue(imageProbe, "pix_fmt"));
            Assert.DoesNotContain(
                SyntheticMetadata,
                await ProbeAllAsync(ffprobePath, testRoot, imageOutput),
                StringComparison.Ordinal);

            var videoInput = Path.Combine(testRoot, "synthetic.mov");
            var videoOutput = Path.Combine(testRoot, "prepared.mp4");
            await RunProcessAsync(ffmpegPath, testRoot,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24",
                "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                "-t", "1", "-shortest",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-metadata", $"comment={SyntheticMetadata}", videoInput
            ]);

            await using (var videoSource = File.OpenRead(videoInput))
            await using (var video = await service.PrepareAsync(new MediaPreparationRequest(
                videoSource,
                videoSource.Length,
                "synthetic.mov",
                "video/quicktime",
                MediaSendMode.Media)))
            await using (var videoDestination = File.Create(videoOutput))
            {
                Assert.True(video.WasTransformed);
                Assert.Equal("video/mp4", video.MediaType);
                Assert.Equal("synthetic.mp4", video.FileName);
                await video.Content.CopyToAsync(videoDestination);
            }

            var videoProbe = await ProbeVideoStreamAsync(ffprobePath, testRoot, videoOutput);
            Assert.Equal("h264", GetProbeValue(videoProbe, "codec_name"));
            Assert.Equal("1280", GetProbeValue(videoProbe, "width"));
            Assert.Equal("720", GetProbeValue(videoProbe, "height"));
            Assert.Equal("yuv420p", GetProbeValue(videoProbe, "pix_fmt"));
            var audioProbe = await RunProcessAsync(ffprobePath, testRoot,
            [
                "-v", "error", "-select_streams", "a:0",
                "-show_entries", "stream=codec_name", "-of", "default=noprint_wrappers=1",
                videoOutput
            ]);
            Assert.Equal("aac", GetProbeValue(audioProbe, "codec_name"));
            Assert.DoesNotContain(
                SyntheticMetadata,
                await ProbeAllAsync(ffprobePath, testRoot, videoOutput),
                StringComparison.Ordinal);
            AssertMoovPrecedesMediaData(videoOutput);
            Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory));
        }
        finally
        {
            DeleteGeneratedTestRoot(testRoot);
        }
    }

    private static string GetFfmpegPathOrSkip()
    {
        var path = Environment.GetEnvironmentVariable("SKOPKA_CHAT_FFMPEG");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var fullPath = Path.GetFullPath(path);
            if (!Path.IsPathFullyQualified(path) || !File.Exists(fullPath))
            {
                Assert.Fail("SKOPKA_CHAT_FFMPEG must name an existing absolute executable path.");
            }

            return fullPath;
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("SKOPKA_CHAT_FFMPEG_REQUIRED"), out var required) &&
            required)
        {
            Assert.Fail("SKOPKA_CHAT_FFMPEG is required but was not provided.");
        }

        Assert.Skip("Set SKOPKA_CHAT_FFMPEG to an installed ffmpeg executable to run this conformance test.");
        return null!;
    }

    private static string GetFfprobePath(string ffmpegPath)
    {
        var extension = Path.GetExtension(ffmpegPath);
        var ffprobePath = Path.Combine(
            Path.GetDirectoryName(ffmpegPath)!,
            $"ffprobe{extension}");
        if (!File.Exists(ffprobePath))
        {
            Assert.Fail("ffprobe must be installed beside ffmpeg for the conformance test.");
        }

        return ffprobePath;
    }

    private static ValueTask<string> ProbeVideoStreamAsync(
        string ffprobePath,
        string workingDirectory,
        string mediaPath) =>
        RunProcessAsync(ffprobePath, workingDirectory,
        [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,width,height,pix_fmt",
            "-of", "default=noprint_wrappers=1", mediaPath
        ]);

    private static ValueTask<string> ProbeAllAsync(
        string ffprobePath,
        string workingDirectory,
        string mediaPath) =>
        RunProcessAsync(ffprobePath, workingDirectory,
        ["-v", "error", "-show_streams", "-show_format", mediaPath]);

    private static string GetProbeValue(string probeOutput, string name)
    {
        var prefix = name + "=";
        var line = probeOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(item => item.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..];
    }

    private static void AssertMoovPrecedesMediaData(string mediaPath)
    {
        var bytes = File.ReadAllBytes(mediaPath);
        var moov = bytes.AsSpan().IndexOf("moov"u8);
        var mediaData = bytes.AsSpan().IndexOf("mdat"u8);
        Assert.True(moov >= 0 && mediaData >= 0 && moov < mediaData);
    }

    private static async ValueTask<string> RunProcessAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "The synthetic media process could not be started.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        var capturedOutput = await output;
        _ = await errors;
        Assert.True(process.ExitCode == 0, $"The synthetic media process exited with code {process.ExitCode}.");
        return capturedOutput;
    }

    private static void DeleteGeneratedTestRoot(string testRoot)
    {
        var fullRoot = Path.GetFullPath(testRoot);
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
            Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullRoot).StartsWith("skopka-chat-ffmpeg-tests-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete an unexpected media test directory.");
        }

        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
