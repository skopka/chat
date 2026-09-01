# Security-boundary self-review

Date: 2026-09-01
Scope: package boundaries and protocol-v1 vertical slice. This is not an independent audit.

## Confirmed boundaries

- `Skopka.Chat.Protocol` references no ASP.NET Core, EF Core, NSec or Client assembly.
- `Skopka.Chat.Transport.Http` references Protocol only; shared JSON DTOs do not pull in Client, Server or ASP.NET Core.
- `Skopka.Chat.Client.Http` references no Server assembly. It binds calls to configured user/device IDs, requires HTTPS by default, disables redirects in its registered handler, bounds responses and redacts token objects/errors.
- `Skopka.Chat.Server` references Protocol only, has no decryption/private-key API and is protected by an automated assembly-reference test.
- `Skopka.Chat.Persistence.PostgreSql` models public devices, conversation metadata, ciphertext, tag, signature and delivery state; an automated model test rejects plaintext/private-key property names.
- Private keys cross only `IDeviceKeyStore`. `DeviceKeyMaterial` redacts `ToString`; exported temporary arrays are cleared after NSec import/use where controlled by the library.
- Canonical signing and AEAD data do not use JSON. UUID and integer byte order is explicit and pinned by a golden vector.
- Ciphertext, header and signature mutation tests fail authentication. Wrong-recipient and size-limit tests are present.
- Message ID insertion is atomic and compares a SHA-256 hash of canonical bytes; identical retry is accepted as duplicate and conflicting reuse is rejected.
- Recipient revocation blocks both new submissions and delivery polling. Acknowledgement is bound to recipient device ID.
- The optional ASP.NET Core route group requires authorization, rejects missing/duplicate/malformed identity claims, verifies user/device ownership, derives polling and acknowledgement recipients from claims, and never accepts plaintext or private-key DTO fields.
- The shared HTTP JSON profile rejects duplicate, unknown and case-mismatched properties, comments, trailing commas/data, coercion, missing non-null values and excessive nesting. Mirrored hostile-input corpora exercise registration, public-device and encrypted-envelope fields on both server and client; rejected input is not persisted or reflected in responses/exceptions.
- A bounded SharpFuzz/AFL++ harness routes mutations through every shared HTTP DTO, replays committed seeds/regressions, and treats only JSON/protocol validation failures as expected. Real Kestrel tests prove both declared and chunked oversized bodies return 413 without state changes and client disconnect reaches repository cancellation.
- The same bounded fuzz harness instruments the client content decoder. Content-v1 has pinned bytes and rejects unknown versions/kinds/flags, truncated identifiers, malformed UTF-8, self-reference, empty reaction tokens and oversize input without reflecting plaintext in exceptions.
- Replies, forward markers and reactions remain inside ciphertext. Stable `ChatContentId` is separate from per-envelope `MessageId`; forwarding drops source attribution, reaction state is bound to the authenticated sender user, out-of-order events are deterministic, and conflicting logical ID reuse is excluded rather than silently replacing content.
- A full TestServer integration test performs Alice encryption → bearer-authenticated HTTP submit → encrypted server storage → Bob polling/decryption/acknowledgement without bypassing either HTTP package.
- A second full TestServer integration test performs the same authenticated E2EE round trip through a migrated PostgreSQL database. CI requires both the direct persistence test and this HTTP-backed path; a missing connection string is a failure there rather than a skip.
- PostgreSQL tests race identical and conflicting inserts through independent contexts, prove one canonical row per message ID, verify at-least-once concurrent polling, first-ack atomicity, deterministic batch ordering and idempotent TTL cleanup.
- The sample and integration test prove Alice → server → Bob while checking that the plaintext marker is absent from stored ciphertext.

## Findings deliberately not hidden

1. **No ratchet / recipient forward secrecy.** A stolen recipient long-term key decrypts retained v1 envelopes. Severity: critical for production claims. Resolution: new reviewed ratcheting protocol version.
2. **No key transparency.** A compromised directory plus missing out-of-band verification enables future key substitution. Severity: high. Resolution: authenticated append-only directory and mandatory key-change UX.
3. **Token validation remains a host boundary.** `Skopka.Chat.Server.AspNetCore` provides and tests authorization after authentication, but deliberately selects no scheme or identity provider. A permissive handler, incorrectly validated JWT or untrusted-header mapping can forge both claims. Severity: high integration risk. Resolution: deployment-specific authentication tests, secure proxy configuration and application-security review.
4. **No replay window beyond message ID.** The server and local store deduplicate IDs, but v1 has no ratchet counter or cross-server replay ledger. Severity: medium.
5. **Metadata is not protected.** User/device graph, timestamps, frequency and approximate length remain visible. Severity depends on deployment.
6. **In-memory key/message stores are intentionally unsafe for production.** Their names and documentation make this explicit, but package consumers can still misuse them. Severity: high if ignored.
7. **PostgreSQL coverage remains bounded.** CI verifies migrations, concurrent idempotency/conflict races, first-ack, deterministic polling, TTL semantics and one authenticated HTTP/E2EE round trip against a disposable single-node PostgreSQL 18 service. It does not validate sustained load, every isolation anomaly, failover, cleanup scheduling, backup/restore or production tuning. Severity: medium operational risk.
8. **Dependency/native boundary.** NSec/libsodium native assets become part of the client trust base. Releases must monitor advisories and preserve exact reviewed dependency versions.
9. **Managed access-token lifetime.** `ChatAccessToken` redacts its string representation, but immutable managed strings cannot be reliably zeroed. Severity: medium if providers copy or retain tokens unnecessarily. Resolution: short-lived tokens, protected provider storage and logging/telemetry review.
10. **Local typed-content lifetime.** Text, reactions and projection snapshots are immutable managed objects and cannot be reliably zeroed. The provided reducer is in-memory and has no retention or protected persistence. Severity: high if a host treats E2EE as local-at-rest protection. Resolution: platform-protected local storage, bounded retention, notification/telemetry review and process-hardening appropriate to the application.

## Release gate before production

- Independent cryptographic and application-security audit.
- Ratcheting or MLS decision revisited against then-current maintained .NET integrations.
- Production identity-provider, proxy, CSRF/CORS, rate-limit and authorization-policy tests.
- Key transparency and device-change UX.
- Real protected key-store implementations for every target platform.
- PostgreSQL concurrency, migration, backup/restore and cleanup tests under production-like load.
- Maintain and grow the deterministic JSON/content corpora, run longer scheduled fuzzing, and add a dedicated target for every future parser.
- Logging review proving plaintext, tokens and key material cannot enter structured logs or exception telemetry.
