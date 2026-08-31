# Security-boundary self-review

Date: 2026-08-31  
Scope: package boundaries and protocol-v1 vertical slice. This is not an independent audit.

## Confirmed boundaries

- `Skopka.Chat.Protocol` references no ASP.NET Core, EF Core, NSec or Client assembly.
- `Skopka.Chat.Server` references Protocol only, has no decryption/private-key API and is protected by an automated assembly-reference test.
- `Skopka.Chat.Persistence.PostgreSql` models public devices, conversation metadata, ciphertext, tag, signature and delivery state; an automated model test rejects plaintext/private-key property names.
- Private keys cross only `IDeviceKeyStore`. `DeviceKeyMaterial` redacts `ToString`; exported temporary arrays are cleared after NSec import/use where controlled by the library.
- Canonical signing and AEAD data do not use JSON. UUID and integer byte order is explicit and pinned by a golden vector.
- Ciphertext, header and signature mutation tests fail authentication. Wrong-recipient and size-limit tests are present.
- Message ID insertion is atomic and compares a SHA-256 hash of canonical bytes; identical retry is accepted as duplicate and conflicting reuse is rejected.
- Recipient revocation blocks both new submissions and delivery polling. Acknowledgement is bound to recipient device ID.
- The optional ASP.NET Core route group requires authorization, rejects missing/duplicate/malformed identity claims, verifies user/device ownership, derives polling and acknowledgement recipients from claims, and never accepts plaintext or private-key DTO fields.
- The sample and integration test prove Alice → server → Bob while checking that the plaintext marker is absent from stored ciphertext.

## Findings deliberately not hidden

1. **No ratchet / recipient forward secrecy.** A stolen recipient long-term key decrypts retained v1 envelopes. Severity: critical for production claims. Resolution: new reviewed ratcheting protocol version.
2. **No key transparency.** A compromised directory plus missing out-of-band verification enables future key substitution. Severity: high. Resolution: authenticated append-only directory and mandatory key-change UX.
3. **Token validation remains a host boundary.** `Skopka.Chat.Server.AspNetCore` provides and tests authorization after authentication, but deliberately selects no scheme or identity provider. A permissive handler, incorrectly validated JWT or untrusted-header mapping can forge both claims. Severity: high integration risk. Resolution: deployment-specific authentication tests, secure proxy configuration and application-security review.
4. **No replay window beyond message ID.** The server and local store deduplicate IDs, but v1 has no ratchet counter or cross-server replay ledger. Severity: medium.
5. **Metadata is not protected.** User/device graph, timestamps, frequency and approximate length remain visible. Severity depends on deployment.
6. **In-memory key/message stores are intentionally unsafe for production.** Their names and documentation make this explicit, but package consumers can still misuse them. Severity: high if ignored.
7. **PostgreSQL integration is opt-in locally.** The test requires `SKOPKA_CHAT_POSTGRES`; CI/release automation should provide a disposable database and fail if it is skipped.
8. **Dependency/native boundary.** NSec/libsodium native assets become part of the client trust base. Releases must monitor advisories and preserve exact reviewed dependency versions.

## Release gate before production

- Independent cryptographic and application-security audit.
- Ratcheting or MLS decision revisited against then-current maintained .NET integrations.
- Production identity-provider, proxy, CSRF/CORS, rate-limit and authorization-policy tests.
- Key transparency and device-change UX.
- Real protected key-store implementations for every target platform.
- PostgreSQL concurrency, migration, backup/restore and cleanup tests under production-like load.
- Fuzzing of canonical parsing when a wire decoder/transport package is introduced.
- Logging review proving plaintext, tokens and key material cannot enter structured logs or exception telemetry.
