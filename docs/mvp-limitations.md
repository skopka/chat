# MVP limitations and roadmap

## Browser endpoint (0.15.0)

Client.Browser supports the text/foreground stage with encrypted IndexedDB and
same-origin cookie BFF adapters; see [browser integration](browser.md). A separate
local unlock phrase is required. It provides no key/phrase recovery, rotation,
backup/transfer, media preparation, service-worker offline shell, push or guaranteed
background execution. Browser storage can be cleared/evicted; complete data loss
cannot restore the previous installation. XSS and malicious same-origin code can
use an unlocked vault despite non-extractable CryptoKeys. Safari/mobile/private-mode
deployment behavior is not certified by the Chromium/Firefox desktop gate.

## Owner-hosted bot layer (0.15.0)

The new bot runtime/private gateway handles bounded text/replies only, not edits,
reactions, attachments, groups, webhooks or managed third-party hosting. Unsupported
events are durably suppressed and acknowledged. The product host must supply
trusted profiles, human consent UX/persistence and server admission; these product
features are not implemented by the generic server. Bot operators can read messages
addressed to them. Blocking does not erase copies or in-flight effects. Local inbox
plaintext, protected identity/key-ring custody, quotas, backup and retention are
deployment responsibilities. See [integration requirements](bots.md).

## Persistent identity is not account recovery

`0.14.x` adds opt-in ownership proof for enrollment/rebind and protected scoped metadata, not an identity provider. Logout/re-login can retain the same device only when the host preserves its keys, installation scope and history/outbox. Missing/corrupt/unavailable keys never trigger silent replacement. Explicit legacy import keeps old IDs; it does not merge devices or recover lost private keys.

Binding is not OAuth validation, immediate Auth revocation, DPoP/mTLS, step-up enrollment, forward secrecy or a ratchet. A stolen authorized session can enroll another device unless the host prevents it; a stolen bound-session bearer token remains a bearer credential. Cooperative file locks and injected SecureStorage tests do not certify real platform backup, uninstall/restore or multi-process semantics. See [integration and migration](device-identity.md).

## Security ceiling of protocol v1

Protocol v1 is a constrained per-message hybrid encryption design. It is not Signal Protocol, MLS or a ratchet. It has not received an independent cryptographic audit. The most important consequence is that compromise of a recipient's long-lived X25519 private key permits decryption of retained historical envelopes addressed to that key. Rotating or revoking a key protects only future server-mediated traffic.

The sender uses a new ephemeral X25519 key for each recipient envelope, but this alone does not provide the forward-secrecy or post-compromise guarantees of a ratcheting session. Device-directory substitution is mitigated only by an out-of-band security-code comparison; v1 has no key-transparency log. The server observes social graph, timing and ciphertext length.

## Functional limits

- MAUI iOS targets physical ARM64 devices. The reviewed NSec/libsodium package set has no iOS simulator native binary; CI validates an unsigned device build, not simulator execution or on-device Keychain behavior. See the [MAUI native runtime boundary](maui.md#apple-native-runtime-boundary).
- Personal chat with encrypted text, replies, non-provenance text forwarding, reactions, text/attachment-caption edits and independently encrypted attachment manifests.
- One envelope per active recipient/sibling device. The standard directory and durable sender perform fan-out, but the host still owns trust UX, account lifecycle and policy for starting a new logical send.
- No message deletion, edit-history UI, groups, attachment replacement/forwarding, resumable/multipart uploads, range playback, thumbnails or automatic media preview. Photos/videos may be prepared locally before the current HTTP path streams a complete ciphertext object.
- No push-notification provider integration.
- No key backup, recovery, transfer or account reset protocol.
- The optional Minimal API and typed HTTP client support request/response polling only; no WebSocket or SignalR push transport is included.
- Delivery is at-least-once. Concurrent pollers may observe the same envelope before acknowledgement. `ChatSyncCoordinator` provides durable store/apply-before-ack with exact `MessageId` deduplication, but idempotent replay is not an exactly-once external-side-effect guarantee.
- No token issuer, token format, authentication scheme or identity-provider integration is selected by the packages.
- Optional UI.Core, Blazor and MAUI conversation components are included, but there is no product shell, contact discovery/navigation, Avalonia adapter or SkopiClub integration.
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
- Protect SQLite client history/outbox with platform/filesystem/database controls. History contains canonical plaintext and delivered attachment keys; the outbox contains exact ciphertext and routing identities. The adapters do not provide database encryption, backup, retention, secure deletion or cross-device synchronization.
- Review MAUI SecureStorage backup/restore and uninstall behavior per platform. Missing or corrupt identity records require an explicit recovery flow; never hide a key change by silently generating a replacement.
- Treat UI drafts, templates, browser/Blazor Server circuits and rendered notification text as plaintext. Keep templates encoded, bound retention and do not log `IChatContentSender` input or remote response bodies.

## Roadmap

1. Independent review of protocol, implementation, dependencies and host-integration guidance.
2. Append-only key transparency and key-change UX for the authenticated device directory.
3. Maintained ratcheting protocol for personal chat, introduced as a new protocol version with explicit migration.
4. Multi-device consistency hardening: transparency-backed device changes, safe device removal, history/bootstrap policy and large-scale outbox operations.
5. Resumable/multipart attachments, range playback, thumbnails and a separately reviewed safe-forwarding/revocation policy.
6. Groups, preferably through a maintained MLS implementation when a supported .NET integration is available.
7. Optional protected key backup/recovery with a separately reviewed threat model.
