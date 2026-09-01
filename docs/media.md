# Photo and video preparation

Skopka.Chat `0.11.x` can prepare photos and videos on the participant device before attachment encryption. The prepared bytes are then encrypted and uploaded through the existing attachment content-v2 path. The server and storage provider never run FFmpeg and never receive media plaintext.

## Send modes

| Mode | Behavior |
| --- | --- |
| `Auto` | Transform supported photo/video input. Keep the exact original when the transform is unavailable or not smaller. This is the default UI choice. |
| `Media` | Require transformation and use a valid transformed result even when it is larger. |
| `File` | Never invoke a transformer. Preserve exact source bytes, file name and media type. |

`Skopka.Chat.Media` contains the contracts, passthrough implementation, progress stages and `ChatMediaAttachmentService`. `Skopka.Chat.Media.FFmpeg` is optional and contains no FFmpeg binary; the host supplies and updates a trusted executable.

## FFmpeg profile

The default profile converts supported still images to JPEG and videos to H.264/AAC MP4:

- photo maximum dimension: 1920 pixels, without upscaling smaller input;
- video maximum dimension: 1280 pixels, H.264 CRF 24, yuv420p;
- optional audio: AAC 128 kbit/s stereo;
- source metadata and chapters are not mapped to the output;
- MP4 uses fast-start layout;
- automatic mode keeps the original if the candidate is not smaller.

All values are configurable through `FfmpegMediaPreparationOptions`. The actual codecs must exist in the host's FFmpeg build. PNG transparency/animation is not preserved by the JPEG profile; use File mode or another `IMediaPreparationService` when it matters.

## Host setup

Create a private working directory before constructing the service. It contains plaintext while FFmpeg is running and must be restricted to the application identity, protected by the host's local-storage policy and cleaned for stale `skopka-media-*` directories after abnormal termination.

```csharp
var preparation = new FfmpegMediaPreparationService(
    new FfmpegMediaPreparationOptions
    {
        ExecutablePath = trustedFfmpegPath,
        WorkingDirectory = protectedMediaWorkDirectory
    });

var mediaAttachments = new ChatMediaAttachmentService(preparation, chatHttpClient);
```

The caller owns the selected source and an empty seekable ciphertext buffer. A disk-backed delete-on-close buffer is recommended for large video; ciphertext does not need a plaintext-protected directory, but the normal local data policy still applies.

```csharp
await using var source = File.OpenRead(selectedPath);
var mode = sendAsFile ? MediaSendMode.File : MediaSendMode.Auto;
var media = new MediaPreparationRequest(
    source,
    source.Length,
    Path.GetFileName(selectedPath),
    selectedMediaType,
    mode);

await using var ciphertext = CreateEmptySeekableCiphertextBuffer();
ChatAttachmentContent manifest = await mediaAttachments.PrepareEncryptAndUploadAsync(
    conversationId,
    new ChatMediaAttachmentRequest(media, caption, replyToContentId),
    ciphertext,
    progress);

await chatViewModel.SendAttachmentAsync(manifest);
```

Do not build a destination path directly from the selected or decrypted file name. The constructor rejects paths and control characters, but names remain participant-controlled display data.

## Blazor callback

The standard composer renders its photo/video picker only when `SkopkaChat.AttachmentSender` is provided. The callback must open and consume `IBrowserFile` before returning. Map `SendAsFile` to `File`; otherwise use `Auto`.

```csharp
async ValueTask<bool> SendBrowserAttachmentAsync(
    ChatBrowserAttachmentSelection selection,
    CancellationToken cancellationToken)
{
    await using var source = selection.File.OpenReadStream(maxAllowedSize, cancellationToken);
    var request = new MediaPreparationRequest(
        source,
        selection.File.Size,
        selection.File.Name,
        string.IsNullOrWhiteSpace(selection.File.ContentType)
            ? "application/octet-stream"
            : selection.File.ContentType,
        selection.SendAsFile ? MediaSendMode.File : MediaSendMode.Auto);

    await using var ciphertext = CreateEmptySeekableCiphertextBuffer();
    var manifest = await mediaAttachments.PrepareEncryptAndUploadAsync(
        conversationId,
        new ChatMediaAttachmentRequest(request),
        ciphertext,
        cancellationToken: cancellationToken);
    return await chatViewModel.SendAttachmentAsync(manifest, cancellationToken);
}
```

In Blazor Server this callback moves plaintext through server-side circuit memory and temporary storage. A browser-only or native client should use a platform-local implementation when that is a confidentiality requirement.

## Security and operations

- Never accept an FFmpeg executable path from a chat participant or request field.
- Pin and update the host binary; sandbox it where the platform permits.
- Do not log process arguments, stdout/stderr, local paths, file names or media content.
- Bound source size, output size, process time, concurrent jobs and disk usage.
- Dispose `PreparedMedia` and remove stale work directories after crashes.
- Scan authenticated plaintext locally before previewing/opening it.
- Treat reported MIME as a hint; FFmpeg validates the actual source it can decode.

The durable design rationale and deferred preview work are recorded in [ADR 0012](adr/0012-client-media-preparation.md).

## Real FFmpeg conformance test

Unit tests validate the exact process arguments with a fake runner. A host can additionally verify its installed FFmpeg/ffprobe pair against synthetic photo and video inputs:

```powershell
$env:SKOPKA_CHAT_FFMPEG = (Get-Command ffmpeg).Source
$env:SKOPKA_CHAT_FFMPEG_REQUIRED = 'true'
dotnet test --project tests/Skopka.Chat.Media.Tests --configuration Release --no-restore
```

This opt-in test checks actual JPEG, H.264/AAC MP4, dimensions, pixel formats, metadata removal, fast-start atom ordering and plaintext work-file cleanup. No participant media is used.
