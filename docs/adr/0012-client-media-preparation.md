# ADR 0012: Client-side media preparation

- Status: Accepted
- Date: 2026-09-01

## Context

Users expect photos and videos to be compressed before sending, while an explicit “send as file” action must preserve the exact source. Performing this work on the server would reveal plaintext and violate the attachment E2EE boundary. Baking one native codec stack into Client or UI would also make the reusable engine unsuitable for mobile, browser and desktop hosts with different platform encoders.

Attachment content v2 already carries a file name, media type, exact encrypted length/hash and file key. Compression alone does not require a new canonical content version: participants authenticate and decrypt the prepared bytes as an ordinary attachment.

## Decision

Add `Skopka.Chat.Media` as a client-side package depending on Client. It defines:

- `MediaSendMode.Auto`, which transforms supported photo/video sources but retains the original when the result is not smaller or transformation is unavailable;
- `MediaSendMode.Media`, which requires transformation and uses a valid transformed result even when larger;
- `MediaSendMode.File`, which bypasses transformation and preserves exact source bytes, name and media type;
- `IMediaPreparationService` and owned `PreparedMedia` streams;
- `ChatMediaAttachmentService`, which performs prepare → chunk encrypt → ciphertext upload and returns a content-v2 manifest for the host to send through `IChatContentSender`;
- redacted stage-only progress notifications.

`Skopka.Chat.Client.Http` implements the media package's ciphertext uploader boundary. It still uploads only a v2 manifest's ciphertext and server-visible opaque metadata.

Add `Skopka.Chat.Media.FFmpeg` as an optional implementation. The host supplies an installed FFmpeg executable and an existing access-controlled working directory. The adapter:

- invokes the executable directly with `ProcessStartInfo.ArgumentList`, never through a command shell;
- uses generated internal names rather than the sender's file name in process arguments;
- drains and discards bounded-lifetime stdout/stderr rather than logging it;
- kills the process tree on cancellation or timeout;
- strips mapped metadata and chapters;
- converts supported still images to bounded MJPEG/JPEG;
- converts videos to bounded H.264/yuv420p plus optional AAC in fast-start MP4;
- rejects truncated/trailing input and invalid/oversized output;
- removes per-operation plaintext work files when the prepared result is disposed.

The default Blazor composer exposes photo/video selection only when a host callback is supplied. Automatic media mode is the default; an unchecked-by-default “send as file” option selects exact-file mode. `IBrowserFile` remains a host/UI boundary and must be consumed inside the callback with an explicit size bound.

## Consequences

- The server, PostgreSQL and S3 provider continue to receive ciphertext only. Protocol v1, content v1 and attachment content v2 bytes are unchanged.
- FFmpeg is not bundled or downloaded. Hosts own binary provenance, codec availability, updates, process sandboxing and license compliance. Other implementations may use Android/iOS codecs or browser APIs without changing Media or Client.
- FFmpeg requires temporary plaintext files. The working directory must be private to the application identity and reside on suitably protected storage. A crash or forced process termination can leave files for host startup cleanup.
- `Auto` is best-effort: unsupported formats, empty media and non-smaller results remain exact original files. `Media` fails generically when a transform cannot be produced.
- PNG transparency and animation are not preserved by the JPEG photo profile. Hosts that require those properties should select File mode or provide another preparation implementation.
- Thumbnail/poster manifests, inline preview, resolution/duration metadata, resumable upload and range playback remain deferred. Those features require a new separately versioned content shape.

## Alternatives considered

- Server-side transcoding: rejected because the server would require plaintext.
- Add image/video fields to content v2: rejected because canonical published bytes are immutable and compression itself needs no wire change.
- Bundle FFmpeg/native binaries in every package: rejected because platform, codec, update, size and licensing requirements belong to the host.
- Always transform media: rejected because explicit exact-file transfer and lossless/unsupported inputs are required.
