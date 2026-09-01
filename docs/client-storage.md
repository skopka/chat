# Durable client history and synchronization

`Skopka.Chat.Client.Storage` closes the receive/acknowledgement gap for typed content. It defines a durable verified-event journal, an idempotent projection applier and `ChatSyncCoordinator`. `Skopka.Chat.Client.Storage.Sqlite` is the optional local SQLite implementation.

## Safe receive order

For each polled envelope the coordinator performs exactly this order:

1. resolve the current public sender device;
2. verify the signature, authenticate/decrypt the envelope and strictly decode typed content;
3. atomically store the verified event by recipient-specific `MessageId`;
4. apply the committed event through an idempotent `IChatEventApplier`;
5. acknowledge the envelope through `IChatTransport`.

An authentication, storage, conflict or applier failure stops the batch before acknowledgement. If acknowledgement fails, at-least-once delivery returns the envelope on a later call. The store reports it as an exact duplicate, the idempotent applier sees it again, and acknowledgement is retried. A reused `MessageId` with different authenticated metadata/content is a conflict and is not acknowledged.

`InitializeAsync` replays committed events before the first poll. This covers a process crash after durable storage but before projection or acknowledgement. Replay order is stable local insertion order; `ChatConversationProjection` still deterministically folds out-of-order originals, reactions and edits.

Outgoing events are not necessarily delivered back to their sending device. After a successful send, pass the host-authenticated local echo returned by the sender to `CommitLocalEchoAsync`; it performs the same durable store/apply steps without polling or acknowledgement. The echo's authenticated sender device must match the coordinator's local device.

## SQLite setup

```csharp
using Skopka.Chat.Client.Storage;
using Skopka.Chat.Client.Storage.Sqlite;

var eventStore = new SqliteChatEventStore(
    "Data Source=protected/chat-history.db;Pooling=False");
var projections = new ChatConversationProjectionRegistry();

using var sync = new ChatSyncCoordinator(
    transport,                 // IChatTransport, for example SkopkaChatHttpClient
    new ChatCryptoService(keyStore),
    eventStore,
    projections,
    myPublicDevice.DeviceId);

await sync.InitializeAsync(cancellationToken);
ChatSyncBatchResult batch = await sync.SynchronizeAsync(100, cancellationToken);

// After IChatContentSender successfully sends and returns its authenticated local echo:
await sync.CommitLocalEchoAsync(sendResult.Delivery!, cancellationToken);

var timeline = projections.GetOrCreate(conversationId).SnapshotTimeline();
```

`SqliteChatEventStore.InitializeAsync` lazily creates schema version 1. Every operation uses a separate connection, a bounded busy timeout and parameterized commands. Inserts use a unique 16-byte delivery ID and distinguish exact retry from conflicting reuse. Reads are paged and preserve insertion order. Because this contract is durable, `:memory:`/`Mode=Memory` data sources are rejected; use `InMemoryChatEventStore` only in tests and samples. The adapter needs no PostgreSQL or server package.

Do not manually acknowledge the same polling stream before this coordinator completes. `ChatReceiver` and `IReceivedMessageStore` remain available for raw/legacy or host-managed receive paths, but they do not replace the durable typed-event pipeline above.

## Local plaintext boundary

The SQLite `content` BLOB is the canonical decrypted typed-content encoding. It can contain message text, reactions, edited values, file names, captions and attachment decryption keys. SQLite provides no encryption at rest in this package. The host must:

- place the database in an access-controlled, platform-protected or encrypted location;
- exclude it from plaintext logs, crash attachments, unprotected cloud sync and casual backups;
- define retention, secure deletion and backup/recovery policy;
- treat database compromise as compromise of local chat history and delivered attachment keys;
- keep every `IChatEventApplier` idempotent and free of non-deduplicated external side effects.

The package does not provide cross-device history sync, key backup, search indexing, retention cleanup or exactly-once external side effects. See [ADR 0014](adr/0014-durable-client-events-and-sync.md), the [threat model](threat-model.md) and [MVP limitations](mvp-limitations.md).
