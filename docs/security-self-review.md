# Security-boundary self-review

## Browser integration review (0.15.0)

Shared canonical/signature/AEAD and binding code now accepts an explicit primitive
provider. Native NSec constructors and legacy key records remain supported; portable
v1 keys require explicit native conversion. Browser assets contain no native NSec
or server framework dependency. Real published Chromium/Firefox tests exercise
native cross-decryption/signatures, encrypted IndexedDB, concurrent identity,
reload/outbox retries, quota-before-ACK, corruption/loss/revocation and the cookie
CSRF sample. This is regression coverage, not an independent audit or proof of
browser/OS power-loss durability. Strong local phrase policy, same-origin code
integrity, XSS prevention, host session coordination, retention/recovery and the
production BFF remain prerequisites. See [browser integration](browser.md).

## Bot integration review (0.15.0)

Bots run as separate client endpoints; no server/private-key dependency was added.
Review/tests cover authenticated decrypt before durable inbox/ack, logical and
delivery-ID conflicts, independent writers, restart, grant changes, suppression,
exact send retries, private HTTP bot scope/strict bounds and create-only protected
identity. A crash-reservation regression pins null public-key metadata separately
from empty invalid key arrays. Source-generated metadata mode preserves that JSON
distinction. This is an internal regression gate, not an independent audit.

Production prerequisites remain: host-authenticated operator registry, human
consent UI/persistence, server-side bot admission, protected Data Protection key
ring/certificate, private gateway ingress, quotas/monitoring and deployment
power-loss/recovery checks. Neither client consent nor a separate process hides
plaintext from the bot operator. See [bots](bots.md).

Date: 2026-09-02
Scope: package boundaries and protocol-v1 vertical slice. This is not an independent audit.

## Confirmed boundaries

- Binding-v1 adds a separate purpose/domain and golden vector; both encryption/signing keys and exact host context are covered. Client exposes only typed proof creation. Server remains Protocol-only; optional `Server.NSec` performs public-key verification without Client/private-key APIs.
- Scoped metadata reserves identity before create-only key persistence. Tests cover independent initializers, interrupted finalization/import, missing/corrupt/unavailable keys and sticky revocation. Platform SecureStorage, backup/uninstall and cross-process OS behavior still require physical-device validation.
- The opt-in account/bootstrap and device/chat policies use asynchronous trusted-context/binding resolution and never fall back to caller device headers/claims. Legacy `IChatPrincipalMapper` mode is unchanged without opt-in.
- New binding tests exercise disposable PostgreSQL atomic rollback, consume races, revocation, clock advancement under locks and actual owned-container restart; HTTP re-login preserves DeviceId, history/outbox and decrypts queued E2EE content. Captured logs and generic errors are checked for synthetic token/plaintext/private-key markers. This is regression coverage, not proof that arbitrary host logging middleware is safe.
- Session refs/deadlines and binding metadata are visible to the server. Binding is not DPoP/mTLS, live Auth revocation, key recovery or a ratchet; step-up enrollment and bearer-token risks remain. See [device identity](device-identity.md).
- `Skopka.Chat.Protocol` references no ASP.NET Core, EF Core, NSec or Client assembly.
- `Skopka.Chat.Attachments` references Protocol only. PostgreSQL and S3 dependencies are isolated in separate adapters, so selecting one backend does not pull the other into a host.
- `Skopka.Chat.Media` is client-side and references Client without HTTP, Server, EF or UI; `Skopka.Chat.Media.FFmpeg` references Media and the BCL only. The FFmpeg binary is host-supplied rather than restored transitively.
- `Skopka.Chat.Transport.Http` references Protocol only; shared JSON DTOs do not pull in Client, Server or ASP.NET Core.
- `Skopka.Chat.Client.Http` references no Server assembly. It binds calls to configured user/device IDs, requires HTTPS by default, disables redirects in its registered handler, bounds responses and redacts token objects/errors.
- `Skopka.Chat.Client.Storage` references Client only; its coordinator verifies/decrypts before atomic storage, applies before acknowledgement, replays on restart and rejects conflicting reuse of a delivery `MessageId`. The SQLite provider is isolated in `Skopka.Chat.Client.Storage.Sqlite` and references neither Server nor PostgreSQL persistence.
- `Skopka.Chat.Client.Maui` references Client, Client.Storage and Media only. Its SecureStorage, lifecycle/session and bounded plaintext-file adapters reference neither transport nor Server. `Skopka.Chat.UI.Maui` references UI.Core only and uses compiled XAML bindings; neither MAUI package enters the core/server graph.
- `Skopka.Chat.Server` references Protocol only, has no decryption/private-key API and is protected by an automated assembly-reference test.
- `Skopka.Chat.Persistence.PostgreSql` models public devices, conversation metadata, ciphertext, tag, signature and delivery state; an automated model test rejects plaintext/private-key property names.
- Private keys cross only `IDeviceKeyStore`. `DeviceKeyMaterial` redacts `ToString`; exported temporary arrays are cleared after NSec import/use where controlled by the library.
- Canonical signing and AEAD data do not use JSON. UUID and integer byte order is explicit and pinned by a golden vector.
- Ciphertext, header and signature mutation tests fail authentication. Wrong-recipient and size-limit tests are present.
- Message ID insertion is atomic and compares a SHA-256 hash of canonical bytes; identical retry is accepted as duplicate and conflicting reuse is rejected.
- Recipient revocation blocks both new submissions and delivery polling. Acknowledgement is bound to recipient device ID.
- Personal conversations are unique by canonical user pair in memory and PostgreSQL. Authenticated directory queries expose only a participant's conversations and active authorized devices; hostile cursors and cross-user/device queries are rejected before data is returned.
- The optional ASP.NET Core route group requires authorization, rejects missing/duplicate/malformed identity claims, verifies user/device ownership, derives polling and acknowledgement recipients from claims, and never accepts plaintext or private-key DTO fields.
- The shared HTTP JSON profile rejects duplicate, unknown and case-mismatched properties, comments, trailing commas/data, coercion, missing non-null values and excessive nesting. Mirrored hostile-input corpora exercise registration, public-device and encrypted-envelope fields on both server and client; rejected input is not persisted or reflected in responses/exceptions.
- A bounded SharpFuzz/AFL++ harness routes mutations through every shared HTTP DTO, replays committed seeds/regressions, and treats only JSON/protocol validation failures as expected. Real Kestrel tests prove both declared and chunked oversized bodies return 413 without state changes and client disconnect reaches repository cancellation.
- The same bounded fuzz harness instruments the versioned client content decoder. Text/reaction content-v1, attachment content-v2 and edit content-v3 have pinned bytes; unknown version/kind/flags/fields, truncated identifiers, malformed UTF-8, inconsistent framing, self-reference, empty reaction/edit text and oversize input are rejected without reflecting plaintext in exceptions.
- Replies, forward markers, reactions and edits remain inside ciphertext. Stable `ChatContentId` is separate from per-envelope `MessageId`; forwarding drops source attribution, reaction state is bound to the authenticated sender user, and edits apply only to matching text/caption owned by that authenticated user. Out-of-order events are deterministic, edits from another device of the same user are accepted, and conflicting logical ID reuse is excluded rather than silently replacing content.
- Attachment file key, nonce prefix, name, media type, caption and plaintext length remain inside the E2EE manifest. Multi-chunk, empty, truncated and tampered-file tests exercise chunk-v1 AEAD; every nonce binds a monotonically increasing index and the AAD binds ID/order/length/finality.
- The common attachment service derives uploader identity from the authenticated caller and delegates conversation membership to a host policy. PostgreSQL stores only opaque metadata and validated ciphertext in an isolated context; S3 validates/spools before conditional create and never overwrites an existing ID.
- Optional PUT/GET/DELETE attachment routes require an active owned device plus the host policy, reject duplicate/malformed metadata before storage and expose only octet-stream ciphertext. The typed HTTP client checks conversation, exact length/hash and media type before streaming authenticated plaintext.
- `Skopka.Chat.UI.Core` references Client only; Blazor and MAUI adapters reference UI.Core but not Server or Persistence. Default rendering treats message/attachment/edit text as text, exposes Edit only for own projected items, expected send/callback failures retain no remote error text, forwarding requires host target selection, and attachment retrieval is a host callback rather than an automatically opened URL/path.
- Media tests prove exact File-mode bypass, Auto smaller-output selection/fallback, bounded JPEG/H.264/AAC argument profiles, path/name redaction, package boundaries and prepare → encrypt → upload → decrypt behavior. An opt-in synthetic conformance test additionally verifies a selected real FFmpeg/ffprobe build, output codecs/dimensions/pixel formats, metadata removal, MP4 fast-start and work-file cleanup. The Blazor picker is opt-in and defaults its “send as file” checkbox to false.
- A full TestServer integration test performs Alice encryption → bearer-authenticated HTTP submit → encrypted server storage → Bob polling/decryption/acknowledgement without bypassing either HTTP package.
- A second full TestServer integration test performs the same authenticated E2EE round trip through a migrated PostgreSQL database. CI requires the direct persistence, attachment and HTTP-backed paths through pinned disposable Testcontainers; failure to start PostgreSQL is a release failure rather than a skip.
- PostgreSQL tests race identical and conflicting inserts through independent contexts, prove one canonical row per message ID, verify at-least-once concurrent polling, first-ack atomicity, deterministic batch ordering and idempotent TTL cleanup.
- Client-storage tests race exact SQLite inserts through independent connections, distinguish exact duplicate from conflict, prove authentication/store/apply/ack ordering, withhold acknowledgement on every earlier failure, retry failed acknowledgement and rebuild out-of-order edits after restart.
- Multi-device/storage tests prove one logical content ID with distinct recipient message IDs, inclusion of peer and sibling devices, cancellation behavior, exact ciphertext reuse after partial failure/restart, stable equal-timestamp history paging and completed-plan cleanup. MAUI tests cover dependency boundaries, account isolation/switch disposal, missing/corrupt/cancelled SecureStorage operations and stable UI wrapper identity.
- The sample and integration test prove Alice → server → Bob while checking that the plaintext marker is absent from stored ciphertext.

## Findings deliberately not hidden

1. **No ratchet / recipient forward secrecy.** A stolen recipient long-term key decrypts retained v1 envelopes. Severity: critical for production claims. Resolution: new reviewed ratcheting protocol version.
2. **No key transparency.** A compromised directory plus missing out-of-band verification enables future key substitution. Severity: high. Resolution: authenticated append-only directory and mandatory key-change UX.
3. **Token validation remains a host boundary.** `Skopka.Chat.Server.AspNetCore` provides and tests authorization after authentication, but deliberately selects no scheme or identity provider. A permissive handler, incorrectly validated JWT or untrusted-header mapping can forge both claims. Severity: high integration risk. Resolution: deployment-specific authentication tests, secure proxy configuration and application-security review.
4. **No replay window beyond message ID.** The server and local store deduplicate IDs, but v1 has no ratchet counter or cross-server replay ledger. Severity: medium.
5. **Metadata is not protected.** User/device graph, timestamps, frequency and approximate length remain visible. Severity depends on deployment.
6. **In-memory key/message stores are intentionally unsafe for production.** Their names and documentation make this explicit, but package consumers can still misuse them. Severity: high if ignored.
7. **PostgreSQL coverage remains bounded.** CI verifies migrations, concurrent idempotency/conflict races, first-ack, deterministic polling, TTL semantics and one authenticated HTTP/E2EE round trip against a disposable single-node PostgreSQL 18 service. It does not validate sustained load, every isolation anomaly, failover, cleanup scheduling, backup/restore or production tuning. Severity: medium operational risk.
8. **Dependency/native boundary.** NSec/libsodium and the optional SQLite native assets become part of the client trust base. Releases must monitor advisories and preserve exact reviewed dependency versions.
9. **Managed access-token lifetime.** `ChatAccessToken` redacts its string representation, but immutable managed strings cannot be reliably zeroed. Severity: medium if providers copy or retain tokens unnecessarily. Resolution: short-lived tokens, protected provider storage and logging/telemetry review.
10. **Local typed-content lifetime.** Text, edits, reactions and projection snapshots are immutable managed objects and cannot be reliably zeroed. The optional SQLite journal improves durability and acknowledgement ordering but stores canonical plaintext, including attachment keys; it provides no encryption, retention, secure deletion or cross-device synchronization. Severity: high if a host treats E2EE as local-at-rest protection. Resolution: platform-protected/encrypted local storage, bounded retention, protected backups, notification/telemetry review and process-hardening appropriate to the application.
11. **UI plaintext and template boundary.** Drafts and projected messages exist in browser or Blazor Server circuit memory. Host templates can bypass Razor encoding with raw markup or third-party DOM APIs. Severity: high when untrusted message text is rendered unsafely or circuits/logs are retained. Resolution: keep normal encoded text rendering, audit custom templates, bound circuit/history lifetime and exclude plaintext from telemetry.
12. **MAUI endpoint/platform boundary.** SecureStorage is subject to platform keychain/keystore, backup/restore, uninstall and entitlement behavior; SQLite history is plaintext; temporary prepared/decrypted media exists in app-private files; foreground lifecycle callbacks do not guarantee background delivery. Severity: high if the host assumes these adapters provide key backup, encrypted history or push. Resolution: platform-specific backup exclusions/recovery UX, protected paths, bounded cleanup, explicit key-change verification and a reviewed push/wake design.
12. **Attachment lifetime and local file boundary.** Upload and envelope fan-out are not transactional, deletion cannot revoke already delivered keys/copies, and decryption can leave an authenticated prefix in a destination before a later failure. Decrypted names, MIME and bytes are sender-controlled. Severity: high if hosts open paths/content directly or retain partial/orphan files. Resolution: temporary protected destinations, discard-on-error, path normalization, content scanning, quotas and lifecycle cleanup.

13. **Media decoder/process and temporary plaintext.** FFmpeg or another host processor receives complete plaintext and can contain exploitable codec bugs. Normal operation and crashes can leave plaintext work files. Severity: high on untrusted media or shared temporary storage. Resolution: pinned/updated binary, least privilege/network sandboxing, private encrypted working directory, strict process/disk/concurrency limits, startup cleanup, generic errors and exact File-mode bypass. Default CI uses a fake runner; the opt-in synthetic conformance gate validates compatibility but is not a codec security audit.
14. **S3/PostgreSQL operational boundary.** Unit and disposable-PostgreSQL tests cover validation, idempotency and migration behavior; there is no live S3-compatible integration, multipart/failover/load test, object-versioning review or backup/restore exercise. Severity: medium operational risk. Resolution: deployment-specific provider tests, bucket/IAM/lifecycle review and production-like restore/load validation.

## Release gate before production

- Independent cryptographic and application-security audit.
- Ratcheting or MLS decision revisited against then-current maintained .NET integrations.
- Production identity-provider, proxy, CSRF/CORS, rate-limit and authorization-policy tests.
- Key transparency and device-change UX.
- Real protected key-store implementations for every target platform.
- Protected client-history location, retention/backup/corruption tests and idempotent host-applier review for every target platform.
- PostgreSQL concurrency, migration, backup/restore and cleanup tests under production-like load.
- S3-compatible provider conformance, conditional-write, lifecycle, IAM, backup/restore and large-object tests for the selected deployment.
- Maintain and grow the deterministic JSON/content corpora, run longer scheduled fuzzing, and add a dedicated target for every future parser.
- Logging review proving plaintext, tokens and key material cannot enter structured logs or exception telemetry.
