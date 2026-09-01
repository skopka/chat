using System.Security.Cryptography;
using Skopka.Chat.Attachments;
using Skopka.Chat.Client;
using Skopka.Chat.Media.FFmpeg;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Media.Tests;

public sealed class MediaPreparationTests
{
    [Fact]
    public void Media_packages_preserve_client_side_dependency_boundaries()
    {
        var mediaReferences = typeof(ChatMediaAttachmentService).Assembly.GetReferencedAssemblies()
            .Select(static item => item.Name)
            .ToArray();
        Assert.Contains("Skopka.Chat.Client", mediaReferences);
        Assert.DoesNotContain("Skopka.Chat.Client.Http", mediaReferences);
        Assert.DoesNotContain("Skopka.Chat.Server", mediaReferences);
        Assert.DoesNotContain(mediaReferences, static item => item?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true);

        var ffmpegReferences = typeof(FfmpegMediaPreparationService).Assembly.GetReferencedAssemblies()
            .Select(static item => item.Name)
            .ToArray();
        Assert.Contains("Skopka.Chat.Media", ffmpegReferences);
        Assert.DoesNotContain("Skopka.Chat.Client.Http", ffmpegReferences);
        Assert.DoesNotContain("Skopka.Chat.Server", ffmpegReferences);
    }

    [Fact]
    public void Ffmpeg_working_directory_must_be_existing_and_absolute()
    {
        var runner = new FakeRunner((_, _) => ValueTask.FromResult(0));
        var options = new FfmpegMediaPreparationOptions { WorkingDirectory = "." };

        var exception = Assert.Throws<ArgumentException>(() =>
            _ = new FfmpegMediaPreparationService(options, runner));

        Assert.Equal("WorkingDirectory", exception.ParamName);
    }

    [Fact]
    public async Task File_mode_preserves_exact_bytes_and_never_invokes_ffmpeg()
    {
        await using var fixture = new WorkingDirectoryFixture();
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("Runner must not be called."));
        var service = fixture.CreateService(runner);
        var bytes = "exact source bytes"u8.ToArray();
        await using var source = new MemoryStream(bytes, writable: false);
        var request = new MediaPreparationRequest(
            source,
            bytes.Length,
            "photo.jpg",
            "image/jpeg",
            MediaSendMode.File);

        await using var prepared = await service.PrepareAsync(request);

        Assert.False(prepared.WasTransformed);
        Assert.Equal("photo.jpg", prepared.FileName);
        Assert.Equal("image/jpeg", prepared.MediaType);
        Assert.Equal(bytes, await ReadAllAsync(prepared.Content));
        Assert.Equal(0, runner.CallCount);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Auto_image_strips_metadata_bounds_dimensions_and_selects_smaller_jpeg()
    {
        await using var fixture = new WorkingDirectoryFixture();
        var transformed = Enumerable.Repeat((byte)7, 80).ToArray();
        var runner = new FakeRunner(async (invocation, cancellationToken) =>
        {
            Assert.Contains("-map_metadata", invocation.Arguments);
            Assert.Contains("-1", invocation.Arguments);
            Assert.Contains("scale=w=min(1920\\,iw):h=min(1920\\,ih):force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuvj420p", invocation.Arguments);
            Assert.Contains("mjpeg", invocation.Arguments);
            Assert.DoesNotContain(invocation.Arguments, static argument => argument.Contains("private-photo", StringComparison.Ordinal));
            await File.WriteAllBytesAsync(invocation.Arguments[^1], transformed, cancellationToken);
            return 0;
        });
        var service = fixture.CreateService(runner);
        var original = Enumerable.Range(0, 400).Select(static value => (byte)(value % 251)).ToArray();
        await using var source = new MemoryStream(original, writable: false);

        await using var prepared = await service.PrepareAsync(new MediaPreparationRequest(
            source,
            original.Length,
            "private-photo.heic",
            "image/heic"));

        Assert.True(prepared.WasTransformed);
        Assert.Equal(PreparedMediaKind.Image, prepared.Kind);
        Assert.Equal("private-photo.jpg", prepared.FileName);
        Assert.Equal("image/jpeg", prepared.MediaType);
        Assert.Equal(transformed, await ReadAllAsync(prepared.Content));
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task Auto_keeps_original_when_transformed_candidate_is_not_smaller()
    {
        await using var fixture = new WorkingDirectoryFixture();
        var original = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        var runner = new FakeRunner(async (invocation, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(invocation.Arguments[^1], new byte[original.Length + 1], cancellationToken);
            return 0;
        });
        var service = fixture.CreateService(runner);
        await using var source = new MemoryStream(original, writable: false);

        await using var prepared = await service.PrepareAsync(new MediaPreparationRequest(
            source,
            original.Length,
            "photo.png",
            "image/png"));

        Assert.False(prepared.WasTransformed);
        Assert.Equal("photo.png", prepared.FileName);
        Assert.Equal("image/png", prepared.MediaType);
        Assert.Equal(original, await ReadAllAsync(prepared.Content));
    }

    [Fact]
    public async Task Auto_keeps_original_when_ffmpeg_cannot_transform_the_media()
    {
        await using var fixture = new WorkingDirectoryFixture();
        var original = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();
        var service = fixture.CreateService(new FakeRunner((_, _) => ValueTask.FromResult(1)));
        await using var source = new MemoryStream(original, writable: false);

        await using var prepared = await service.PrepareAsync(new MediaPreparationRequest(
            source,
            original.Length,
            "photo.png",
            "image/png"));

        Assert.False(prepared.WasTransformed);
        Assert.Equal("photo.png", prepared.FileName);
        Assert.Equal("image/png", prepared.MediaType);
        Assert.Equal(original, await ReadAllAsync(prepared.Content));
    }

    [Fact]
    public async Task Forced_video_uses_h264_aac_mp4_profile_and_generic_failures()
    {
        await using var fixture = new WorkingDirectoryFixture();
        var runner = new FakeRunner(async (invocation, cancellationToken) =>
        {
            Assert.Contains("libx264", invocation.Arguments);
            Assert.Contains("aac", invocation.Arguments);
            Assert.Contains("+faststart", invocation.Arguments);
            Assert.Contains("scale=w=min(1280\\,iw):h=min(1280\\,ih):force_original_aspect_ratio=decrease:force_divisible_by=2", invocation.Arguments);
            await File.WriteAllBytesAsync(invocation.Arguments[^1], new byte[64], cancellationToken);
            return 0;
        });
        var service = fixture.CreateService(runner);
        await using var source = new MemoryStream(new byte[32], writable: false);

        await using var prepared = await service.PrepareAsync(new MediaPreparationRequest(
            source,
            source.Length,
            "private-video.mov",
            "video/quicktime",
            MediaSendMode.Media));

        Assert.True(prepared.WasTransformed);
        Assert.Equal("private-video.mp4", prepared.FileName);
        Assert.Equal("video/mp4", prepared.MediaType);

        var failing = fixture.CreateService(new FakeRunner((_, _) => ValueTask.FromResult(1)));
        await using var failingSource = new MemoryStream(new byte[32], writable: false);
        var exception = await Assert.ThrowsAsync<MediaPreparationException>(async () =>
            await failing.PrepareAsync(new MediaPreparationRequest(
                failingSource,
                failingSource.Length,
                "secret-name.mov",
                "video/quicktime",
                MediaSendMode.Media)));
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Path, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Orchestrator_preserves_file_bytes_then_encrypts_and_uploads_before_returning_manifest()
    {
        var plaintext = Enumerable.Range(0, 9000).Select(static value => (byte)(value % 251)).ToArray();
        await using var source = new MemoryStream(plaintext, writable: false);
        await using var ciphertext = new MemoryStream();
        var uploader = new RecordingUploader();
        var service = new ChatMediaAttachmentService(new PassthroughMediaPreparationService(), uploader);
        var stages = new RecordingProgress<ChatMediaTransferProgress>();
        var conversationId = ConversationId.New();
        var request = new ChatMediaAttachmentRequest(new MediaPreparationRequest(
            source,
            plaintext.Length,
            "original.bin",
            "application/octet-stream",
            MediaSendMode.File));

        var manifest = await service.PrepareEncryptAndUploadAsync(
            conversationId,
            request,
            ciphertext,
            stages);

        Assert.Equal(conversationId, uploader.ConversationId);
        Assert.Equal(manifest.AttachmentId, uploader.Manifest?.AttachmentId);
        Assert.Equal(manifest.CiphertextSha256.ToArray(), SHA256.HashData(uploader.Ciphertext));
        await using var encryptedSource = new MemoryStream(uploader.Ciphertext, writable: false);
        await using var decrypted = new MemoryStream();
        await ChatAttachmentCryptoService.DecryptAsync(manifest, encryptedSource, decrypted);
        Assert.Equal(plaintext, decrypted.ToArray());
        Assert.Equal(
            [
                ChatMediaTransferStage.Preparing,
                ChatMediaTransferStage.Encrypting,
                ChatMediaTransferStage.Uploading,
                ChatMediaTransferStage.Completed
            ],
            stages.Items.Select(static item => item.Stage));
    }

    private static async Task<byte[]> ReadAllAsync(Stream source)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed class WorkingDirectoryFixture : IAsyncDisposable
    {
        internal WorkingDirectoryFixture()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"skopka-media-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal FfmpegMediaPreparationService CreateService(IFfmpegProcessRunner runner) =>
            new(new FfmpegMediaPreparationOptions { WorkingDirectory = Path }, runner);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRunner(
        Func<FfmpegInvocation, CancellationToken, ValueTask<int>> callback) : IFfmpegProcessRunner
    {
        internal int CallCount { get; private set; }

        public async ValueTask<int> RunAsync(FfmpegInvocation invocation, CancellationToken cancellationToken)
        {
            CallCount++;
            return await callback(invocation, cancellationToken);
        }
    }

    private sealed class RecordingUploader : IEncryptedAttachmentUploader
    {
        internal ConversationId? ConversationId { get; private set; }
        internal ChatAttachmentContent? Manifest { get; private set; }
        internal byte[] Ciphertext { get; private set; } = [];

        public async ValueTask<AttachmentStoreResult> UploadAsync(
            ConversationId conversationId,
            ChatAttachmentContent manifest,
            Stream ciphertext,
            DateTimeOffset? expiresAt = null,
            CancellationToken cancellationToken = default)
        {
            ConversationId = conversationId;
            Manifest = manifest;
            using var destination = new MemoryStream();
            await ciphertext.CopyToAsync(destination, cancellationToken);
            Ciphertext = destination.ToArray();
            return AttachmentStoreResult.Stored;
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        internal List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }
}
