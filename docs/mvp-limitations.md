# MVP limitations and roadmap

## Security ceiling of protocol v1

Protocol v1 is a constrained per-message hybrid encryption design. It is not Signal Protocol, MLS or a ratchet. It has not received an independent cryptographic audit. The most important consequence is that compromise of a recipient's long-lived X25519 private key permits decryption of retained historical envelopes addressed to that key. Rotating or revoking a key protects only future server-mediated traffic.

The sender uses a new ephemeral X25519 key for each recipient envelope, but this alone does not provide the forward-secrecy or post-compromise guarantees of a ratcheting session. Device-directory substitution is mitigated only by an out-of-band security-code comparison; v1 has no key-transparency log. The server observes social graph, timing and ciphertext length.

## Functional limits

- Personal chat with encrypted text, replies, non-provenance text forwarding, reactions, text/attachment-caption edits and independently encrypted attachment manifests.
- One envelope per recipient device; the host owns device enumeration and fan-out.
- No message deletion, edit-history UI, groups, attachment replacement/forwarding, resumable/multipart uploads, range playback, thumbnails or automatic media preview. Photos/videos may be prepared locally before the current HTTP path streams a complete ciphertext object.
- No push-notification provider integration.
- No key backup, recovery, transfer or account reset protocol.
- The optional Minimal API and typed HTTP client support request/response polling only; no WebSocket or SignalR push transport is included.
- Delivery is at-least-once. Concurrent pollers may observe the same envelope before acknowledgement. `ChatSyncCoordinator` provides durable store/apply-before-ack with exact `MessageId` deduplication, but idempotent replay is not an exactly-once external-side-effect guarantee.
- No token issuer, token format, authentication scheme or identity-provider integration is selected by the packages.
- Optional UI.Core and Blazor conversation components are included, but there is no product shell, contacts/conversation navigation, MAUI/Avalonia adapter or SkopiClub integration.
- No federation, traffic padding, sealed sender or metadata hiding.

## Required host responsibilities

- Implement `IDeviceKeyStore` with platform secure storage. Never use the in-memory store in production.
- Configure a trusted ASP.NET Core authentication handler before mapping the optional API. Validate token signature, issuer, audience and lifetime as appropriate; never turn untrusted headers directly into claims.
- Implement `IAccessTokenProvider` without logging or persistently copying tokens. Keep Authorization headers redacted and use the typed HTTP client as transient/scoped, not singleton.
- Protect device registration and revocation, rate-limit all endpoints and avoid sensitive logging.
- Deliver public-key changes to users and require security-code comparison for high-risk conversations.
- Run migration/TTL cleanup jobs and configure retention, database encryption, backup and operational monitoring.
- Choose and harden one `IAttachmentStore`: bounded PostgreSQL `bytea` for small files or S3-compatible object storage for larger media. Configure quotas, proxy limits, bucket/database policy, encryption at rest, orphan/expiry cleanup and backup/restore.
- Implement `IAttachmentAccessAuthorizer` against authoritative conversation membership. Treat decrypted names/MIME as untrusted, discard partial output on failure, prevent path traversal and scan content before preview/open.
- If media preparation is enabled, provision a private plaintext working directory, pin/sandbox the host FFmpeg binary, bound concurrent processes/time/disk and clean stale operation directories after abnormal termination. `File` mode is the exact-byte escape hatch; JPEG conversion does not preserve PNG transparency/animation.
- Keep the PostgreSQL reliability and HTTP integration gates mandatory in release CI; extend them with sustained-load, deployment-specific failover and restore exercises.
- Treat decrypted local messages as sensitive. Use `IChatEventStore`/`ChatSyncCoordinator` for durable typed receive, or implement equivalent transactional storage before acknowledgement; `IReceivedMessageStore` remains the lower-level `ChatReceiver` boundary.
- Protect SQLite client history with platform/filesystem/database controls. It contains canonical plaintext and delivered attachment keys; the adapter does not provide encryption, backup, retention, secure deletion or cross-device synchronization.
- Treat UI drafts, templates, browser/Blazor Server circuits and rendered notification text as plaintext. Keep templates encoded, bound retention and do not log `IChatContentSender` input or remote response bodies.

## Roadmap

1. Independent review of protocol, implementation, dependencies and host-integration guidance.
2. Append-only key transparency and key-change UX for the authenticated device directory.
3. Maintained ratcheting protocol for personal chat, introduced as a new protocol version with explicit migration.
4. Multi-device fan-out, device-list consistency and safe device removal.
5. Resumable/multipart attachments, range playback, thumbnails and a separately reviewed safe-forwarding/revocation policy.
6. Groups, preferably through a maintained MLS implementation when a supported .NET integration is available.
7. Optional protected key backup/recovery with a separately reviewed threat model.
