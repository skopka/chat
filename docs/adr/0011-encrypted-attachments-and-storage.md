# ADR 0011: Encrypted attachments and independent blob storage

- Status: Accepted
- Date: 2026-09-01

## Context

Protocol v1 envelopes are bounded to 64 KiB and the server must never receive message plaintext or private keys. Media can be much larger, needs streaming, and should not force deployments that keep message envelopes in PostgreSQL to keep large files there. A file reference also carries sensitive name, media type, caption and decryption material that must not become server metadata.

Changing the existing encrypted content-v1 bytes would silently reinterpret already emitted content. Returning direct object-store URLs would move authorization, redirect handling and provider metadata into every client and could allow untrusted UI code to load ciphertext or attacker-selected resources automatically.

## Decision

### Content v2 manifest

Text and reactions continue to encode as content v1. Attachments alone encode as content v2 with domain prefix `skopka.chat.content`, ASCII version `2` and kind `A`. The remaining canonical fields are:

1. 16-byte big-endian content UUID;
2. 16-byte big-endian attachment UUID;
3. one flags byte (`0x01` reply, `0x02` caption; all other bits rejected);
4. optional 16-byte reply content UUID;
5. signed 64-bit big-endian plaintext and ciphertext lengths;
6. signed 32-bit big-endian plaintext chunk size;
7. 32-byte ciphertext SHA-256, 32-byte file key and 16-byte nonce prefix;
8. two-byte big-endian length plus strict UTF-8 file name;
9. two-byte big-endian length plus printable-ASCII media type;
10. optional two-byte big-endian length plus strict UTF-8 caption;
11. no trailing bytes.

The constructor rejects empty IDs, paths/control characters in file names, non-printable media types, invalid sizes, self-replies and a ciphertext length that does not exactly match canonical framing. A golden vector and fuzz seed pin this format.

### Chunk encryption v1

The client creates a random 32-byte key and 16-byte nonce prefix. Each chunk uses NSec XChaCha20-Poly1305. Its 24-byte nonce is the prefix followed by the signed non-negative 64-bit big-endian chunk index. The associated data is the ASCII domain `skopka.chat.attachment.chunk.v1`, attachment UUID, chunk index, total plaintext length, current plaintext length and a final-chunk byte.

Each stored frame is a four-byte big-endian plaintext length followed by ciphertext and the 16-byte authentication tag. The final partial chunk is explicit; an empty file is one authenticated zero-length chunk. Decryption requires every expected frame, rejects trailing bytes and compares SHA-256 over the exact stored stream. A caller must discard a destination after any failure because already authenticated earlier chunks may have been written.

The file key and nonce prefix exist only in participant-side `ChatAttachmentContent` instances and their recipient-specific encrypted envelopes. They never enter storage metadata.

### Independent storage boundary

`Skopka.Chat.Attachments` depends only on Protocol and defines immutable create-if-absent storage. `AttachmentId` reuse returns `Duplicate` only for the same conversation, authenticated uploader, ciphertext length/hash and expiry; otherwise it returns `Conflict`. Stores must validate exact length and SHA-256 before making a new blob visible and must never overwrite.

`AttachmentStorageService` derives uploader identity from the authenticated caller and delegates upload/download/delete decisions to host-owned `IAttachmentAccessAuthorizer`. The policy is responsible for authoritative conversation membership. The server-visible record contains only IDs, ciphertext length/hash and retention timestamps.

Two optional adapters are coordinated packages:

- PostgreSQL uses an isolated `AttachmentDbContext`, append-only migration and one bounded `bytea` row. The default maximum is 16 MiB.
- S3-compatible storage validates the source before upload, spooling non-seekable ciphertext to a delete-on-close temporary file, and uses conditional `If-None-Match: *`. The common single-object limit is 5 GiB.

Message envelopes remain owned by `Skopka.Chat.Persistence.PostgreSql`; no dependency is introduced from Server to Client or from the envelope store to a blob provider.

### HTTP and UI

The optional authenticated API exposes PUT/GET/DELETE at `/attachments/{attachmentId}` only when attachment storage is registered. PUT requires `application/octet-stream`, `Content-Length`, conversation ID and ciphertext SHA-256. GET returns only ciphertext plus the same opaque metadata. Active device ownership is checked before the host attachment policy. Client upload is single-attempt because arbitrary non-seekable caller streams cannot be replayed safely; callers can retry with the same ID and a fresh/repositioned stream.

The default UI renders a file card and delegates retrieval/decryption to the host. It does not automatically embed a remote media URL. Custom templates remain possible but enter the host security boundary.

## Consequences

- S3 is the recommended backend for large media; PostgreSQL is a simple small-file option with transactional database operations but larger backups/WAL and memory allocation per row.
- Upload and envelope send are not one transaction. Orphans and missing blobs are expected failure modes and require retention cleanup/retry UX.
- Server-side malware inspection cannot see plaintext without terminating E2EE. Participants/hosts must scan after authenticated decryption and before opening.
- Storage still reveals conversation/uploader linkage, timing, retention and exact ciphertext length. No padding or sealed-sender property is added.
- Protocol v1 security limits remain: compromise of a recipient long-term key can reveal a retained manifest and therefore the corresponding retained attachment key.
- Resumable/multipart uploads, range playback, thumbnails, automatic media preview, attachment forwarding and remote deletion of already delivered manifests are deferred.

## Alternatives considered

- Put media in protocol envelopes: rejected by the 64 KiB bound and memory/amplification risk.
- Store plaintext and encrypt only transport: rejected because it violates the E2EE server boundary.
- Store all files in the message PostgreSQL context: rejected because it couples independent scaling, retention and backup profiles.
- S3-only storage: rejected because small/self-contained deployments benefit from a bounded PostgreSQL option.
- Change content v1: rejected because published canonical bytes are immutable.
- Give clients pre-signed provider URLs by default: deferred because provider-specific signing, redirect/origin policy and authorization lifecycle require a separate reviewed design.
