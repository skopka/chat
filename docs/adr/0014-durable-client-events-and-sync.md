# ADR 0014: Durable verified client events and acknowledgement ordering

- Status: accepted
- Date: 2026-09-01

## Context

Server delivery is at-least-once until acknowledgement. `ChatReceiver.ReceiveContentAsync` authenticates and decodes typed content while `IReceivedMessageStore` atomically deduplicates the decrypted envelope bytes, but a host still had to coordinate durable typed history, projection and server acknowledgement. A crash after deduplication but before persisting the typed event could make a retry look like an unprojectable duplicate. Acknowledging before durable local storage could permanently remove the server's last copy.

The in-memory `ChatConversationProjection` also cannot restore reactions or edits that arrived before their targets after a restart. A reusable client engine needs a transport-independent pipeline and a production-shaped local adapter without making Client depend on HTTP, Server or server persistence.

## Decision

- Add `Skopka.Chat.Client.Storage`, depending only on `Skopka.Chat.Client`. It owns `IChatEventStore`, `IChatEventApplier`, `ChatSyncCoordinator`, an in-memory test/sample journal and a projection registry.
- Add optional `Skopka.Chat.Client.Storage.Sqlite`, depending on Client.Storage and `Microsoft.Data.Sqlite`. It stores canonical decrypted content bytes plus authenticated delivery metadata. It never stores device private keys or access tokens.
- Process each delivery as resolve sender → verify/decrypt/decode → atomic event insert/compare → idempotent apply → acknowledgement. No acknowledgement is attempted after an earlier stage fails.
- Key local idempotency by recipient-specific envelope `MessageId`. An exact repeat returns `Duplicate`; different metadata or canonical content under the same ID returns `Conflict` and is not acknowledged.
- Replay every committed event into the idempotent applier before the first poll. A duplicate returned after an acknowledgement failure is applied again, so appliers must tolerate repetition.
- Expose `CommitLocalEchoAsync` for a host-authenticated successful outgoing echo from the coordinator's own device. It uses the same store/apply ordering without transport acknowledgement, because servers need not deliver a sender's own envelope back to that device.
- Serialize one coordinator instance with a semaphore. SQLite independently enforces unique-ID atomicity across connections/processes and pages replay by stable insertion sequence.
- Keep content/protocol wire bytes unchanged. This is local storage and orchestration behavior only.

SQLite schema version 1 stores IDs as fixed 16-byte big-endian BLOBs, the authenticated sender timestamp as UTC ticks, a content ID for integrity checking and the complete canonical typed-content BLOB. The schema uses `PRAGMA user_version`; future changes must migrate forward rather than silently reinterpret existing rows.

## Security and failure semantics

The event journal contains plaintext. Attachment events additionally contain file names, MIME claims, captions and symmetric attachment keys. This package deliberately does not claim database encryption: protected location, OS/database encryption, credentials, retention, backup and secure deletion remain host responsibilities.

Authentication and strict content decoding happen before storage. Store or applier failure leaves the server delivery unacknowledged. Acknowledgement failure leaves the committed event replayable and retryable. A crash after storage but before apply/ack is recovered by startup replay. These properties provide durable at-least-once application, not exactly-once UI or external side effects.

The current sender directory remains a trust boundary. If the sender entry is missing, altered or cryptographically inconsistent, synchronization stops without storing or acknowledging the delivery. A conflicting local delivery ID is surfaced with generic text rather than silently accepting different plaintext.

## Consequences

- Hosts can recover text, attachment manifests, reactions and edits after restart and acknowledge only after durable local commit.
- HTTP polling, an in-process transport or a future push-triggered poll can share the same coordinator without a Client.Storage dependency on a transport adapter.
- Applications that already own a protected database can implement `IChatEventStore` without installing SQLite.
- The SQLite adapter adds a native SQLite dependency and needs deployment-specific advisory, filesystem, backup and corruption testing.
- Search, retention cleanup, cross-device history sync, key backup, push transport and transactionally coupled external side effects remain deferred.
