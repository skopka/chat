# ADR 0020: opt-in encrypted history backup, not device-key recovery

- Date: 2026-09-03
- Status: implemented in 0.16.0 as opt-in; independent security review still required
- Compatibility: envelope/content/binding bytes and existing local schemas unchanged

## Decision

Keep the feature in the existing Protocol, Client, Client.Storage, HTTP, Server,
PostgreSQL, Browser and MAUI packages. No application/domain-specific dependency.
An account password, local vault phrase, random backup recovery key and device
identity are four distinct assets. Restore never reads or writes device keys and
does not transfer pending jobs/outbox. Enabling is explicit and requires confirming
the saved recovery key before the first upload. Existing archives/keys are never
silently replaced. Logout closes the session and its recovery-key access.

Backup v1 uses a random 256-bit recovery key, grouped hexadecimal representation
with a domain-separated checksum, HKDF-SHA256 purpose/context separation and the
existing XChaCha20-Poly1305 primitive provider (NSec or vendored libsodium.js).
Every encryption has a fresh 192-bit nonce. Account, exact service identifier,
archive, key generation, format, upload, index and chain metadata are authenticated.
There is no password-based recovery, device seed derivation or custom primitive.

Versions are immutable append-only contributions. Each contains a bounded ordered
chain of encrypted event parts and an authenticated seal referring to the previous
complete version and its hash. The server atomically commits only complete parts
and compare-and-swaps the head. A losing writer authenticates the new head and
rebases its unchanged contribution; it cannot replace another device's history.
Parts are persisted locally before network I/O; exact retries reuse their bytes.
Pending-upload expiry never deletes committed ancestors. Retention limits reject
new work instead of pruning referenced versions. Compaction/key rotation are future
formats/operations, not unsafe in-place rewrites.

Restore verifies the complete bounded chain, decrypts one part at a time and stages
stable event variants in protected local storage. A durable cursor resumes at the
next part; durable part-to-event references are revalidated locally before completion
so missing/corrupt staging cannot be masked by that cursor. One final pointer commit exposes
the imported snapshot. Cancellation/crash/full storage cannot expose a prefix as a
successful restore. Repeated restore and overlapping device histories deduplicate
logical events without inventing new content IDs. Existing verified history remains
separate and is preserved. No transport ACK, local echo, outbox send or external
event-applier callback is invoked by import.

## Historical authenticity

The existing journal stores authenticated metadata and canonical decrypted content,
not original signed envelopes/public-key verification evidence. A backup key holder
can forge such records. Restored events are therefore **recovery-key authenticated
history**, never independently sender-verified evidence. Presentation uses an
explicit restored-history entry point and provenance indicator. Conflicting restored
data cannot overwrite locally verified events. Actual new deliveries still follow
authenticate → durable store → apply → ACK. Export includes existing content-v1/v2/v3
events, including attachment manifests/references, but no binary media. The engine
has no message-deletion event yet; unsupported future content is rejected, not
silently flattened or discarded.

## Threats and limits

Server/storage operators see account/archive/version IDs, counts, lengths and timing,
but no recovery key, device private key, message text or attachment manifest. A
malicious server can withhold/delete data or replay a valid old head; retained local
head pinning can detect rollback for that client, but a brand-new device has no
independent freshness anchor. An authorized account writer lacking the recovery key
can also append an invalid seal because the opaque server does not verify AEAD tags;
clients reject it and immutable ancestors survive, but availability is lost until
reviewed head repair. Public archive-key possession proofs are not implemented.
No availability, transparency or ratchet claim.

Lost recovery keys cannot be recovered by the server. Revocation cannot erase
already copied history or secrets. XSS/unlocked endpoint compromise defeats E2EE;
managed strings cannot reliably be erased. Browser staging is vault-encrypted;
native restored SQLite history needs the same host-owned protection as live history.
The host owns Auth, service configuration, CSRF/CORS, quotas, rate limits, operational
storage/backups and physical-device SecureStorage validation. QR/device-to-device
secret transfer, binary backup, account recovery, key rotation and compaction are
not implemented by this first format.

See the [format, API, setup and tested failure cases](../backups.md).
