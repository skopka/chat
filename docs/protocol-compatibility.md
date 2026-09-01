# Protocol and package compatibility

## Versioning

NuGet packages use SemVer. The initial package version was `0.1.0`; public APIs and the wire format may evolve before `1.0.0`, but already published protocol versions are never silently reinterpreted.

`ProtocolVersions.V1` is encoded into every envelope. A v1 implementation rejects unknown versions before storage or cryptographic work. A future wire change must use a new protocol version, a separate canonical domain separator and new golden vectors.

## Canonical binary format

Protocol v1 uses fixed field order, signed lengths, network byte order for integers and RFC 4122 big-endian UUID bytes. Strings are limited to ASCII domain separators; arbitrary JSON is not signed. The signed bytes cover:

1. protocol version;
2. message, conversation, sender-device and recipient-device IDs;
3. sender signing-key and recipient encryption-key IDs;
4. sent/expiry timestamps;
5. ephemeral X25519 public key and XChaCha20-Poly1305 nonce;
6. ciphertext and authentication tag.

AEAD associated data covers the canonical header and ephemeral public key. Tests pin a complete deterministic golden envelope vector.

## Compatibility table

| Package range | Emitted protocol | Accepted protocol | Notes |
| --- | --- | --- | --- |
| `0.1.x` | v1 | v1 | Personal chat MVP; no ratchet. |
| `0.2.x` | v1 | v1 | Adds the optional authenticated ASP.NET Core transport; canonical envelope bytes are unchanged. |
| `0.3.x` | v1 | v1 | Adds shared HTTP contracts and the authenticated HTTP client; canonical envelope bytes are unchanged. |
| `0.4.x` | v1 | v1 | Adds the required HTTP-to-PostgreSQL CI gate; canonical envelope bytes are unchanged. |
| `0.5.x` | v1 | v1 | Adds PostgreSQL concurrency, deterministic delivery and TTL reliability gates; canonical envelope bytes are unchanged. |
| `0.6.x` | v1 | v1 | Adds a strict bounded HTTP JSON profile and hostile-input regression corpus; routes, DTOs and canonical envelope bytes are unchanged. |
| `0.7.x` | v1 | v1 | Adds coverage-guided JSON fuzzing, real-Kestrel edge gates and coordinated package publication; the HTTP and canonical envelope formats are unchanged. |
| `0.8.x` *(not published)* | v1 | v1 | Development line for explicit encrypted-content v1 inside ciphertext for replies, safe forwards and reactions. Raw `0.1.x`–`0.7.x` plaintext APIs remain explicit and supported. |
| `0.9.x` *(not published)* | v1 | v1 | Development line for optional UI.Core presentation state and adaptable Blazor components. |
| `0.10.x` *(not published)* | v1 | v1 | Development line for attachment content v2, chunked file AEAD, independent PostgreSQL/S3 storage and authenticated ciphertext HTTP routes. |
| `0.11.x` | v1 | v1 | First coordinated public set after `0.7.0`; includes the accumulated encrypted-content v1, UI, attachment content v2/storage and client-side media preparation/exact-file mode. Protocol-v1 canonical bytes remain unchanged. |

Patch releases must not change canonical v1 output. Minor releases may add optional APIs or support a new protocol version, but must retain v1 decoding/validation if they claim compatibility. Removal of a protocol version or breaking public API requires a major package version.

## Encrypted content compatibility

Encrypted content has its own version discriminator because it is parsed only after protocol-v1 authentication and decryption. Content v1 is emitted only by the explicit typed-content APIs introduced in package `0.8.0`. Older clients can still authenticate and decrypt those envelopes as opaque bytes but cannot interpret their typed contents. New clients do not heuristically parse legacy raw text; callers choose the raw or typed receive API deliberately.

One `ChatContentId` identifies the same logical event across recipient-device fan-out, while every recipient-specific envelope keeps a unique `MessageId`. A future incompatible content change must add a content version and retain an explicit decoder for every version it claims to accept. It must not change protocol-v1 envelope bytes unless a distinct outer protocol version is also introduced.

Package `0.11.x` emits content v1 for `ChatTextContent` and `ChatReactionContent`; it emits content v2 only for `ChatAttachmentContent`. Content v2 is not a reinterpretation of v1. Its manifest carries the separately stored ciphertext hash, framing parameters and participant-only file metadata/key. Published clients through `0.7.0` can authenticate the outer envelope as bytes but cannot project or decrypt typed content or attachments. Exact attachment fields and the chunk-v1 domain are pinned in [ADR 0011](adr/0011-encrypted-attachments-and-storage.md).

Package `0.11.x` prepares optional photo/video plaintext before the attachment encryption step. `Auto`, `Media` and exact `File` modes are local API semantics and add no new content fields. A recipient with attachment-content-v2 support can receive/decrypt the result without referencing the media package. See [ADR 0012](adr/0012-client-media-preparation.md).
