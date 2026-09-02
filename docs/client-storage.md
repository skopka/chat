# Durable client history and synchronization

`Skopka.Chat.Client.Storage` closes both receive/acknowledgement and partial multi-device-send gaps for typed content. It defines a durable verified-event journal, bounded history paging, an idempotent projection applier, an exact-envelope outbox and `ChatSyncCoordinator`. `Skopka.Chat.Client.Storage.Sqlite` is the optional local SQLite implementation.

## Safe receive order

For each polled envelope the coordinator performs exactly this order:

1. resolve the current public sender device;
2. verify the signature, authenticate/decrypt the envelope and strictly decode typed content;
3. atomically store the verified event by recipient-specific `MessageId`;
4. apply the committed event through an idempotent `IChatEventApplier`;
5. acknowledge the envelope through `IChatTransport`.

An authentication, storage, conflict or applier failure stops the batch before acknowledgement. If acknowledgement fails, at-least-once delivery returns the envelope on a later call. The store reports it as an exact duplicate, the idempotent applier sees it again, and acknowledgement is retried. A reused `MessageId` with different authenticated metadata/content is a conflict and is not acknowledged.

`InitializeAsync` replays committed events before the first poll. This covers a process crash after durable storage but before projection or acknowledgement. Replay order is stable local insertion order; `ChatConversationProjection` still deterministically folds out-of-order originals, reactions and edits.

For mobile clients with large histories, construct `ChatSyncCoordinator` with `restoreAllHistory: false` and restore only the visible window through `ChatHistoryPager`. `LoadInitialAsync` reads the newest bounded page; `LoadPreviousAsync` walks an opaque store cursor and prepends older events without gaps, including rows that share a timestamp. Calls are serialized, and a cursor is advanced only after the whole page has been applied.

Outgoing events are not necessarily delivered back to their sending device. After a successful send, pass the host-authenticated local echo returned by the sender to `CommitLocalEchoAsync`; it performs the same durable store/apply steps without polling or acknowledgement. The echo's authenticated sender device must match the coordinator's local device.

## SQLite setup

With persistent identity (`0.14.x`), construct history/outbox paths from stable service/account/installation scope plus DeviceId, never from sid/access-token state. Preserve existing account/device paths during migration. Run enrollment/rebind before network sync and reopen the same databases after re-login; saved outbox envelopes are retried byte-for-byte. Preserving DeviceId without both original private keys and local databases does not restore history. See [device identity integration](device-identity.md).
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

## Durable multi-device outbox

`ChatMultiDeviceSender` creates the complete fan-out plan once, then asks `IChatFanOutPlanStore` to commit it before sending any envelope. Use `SqliteChatOutboxStore` as that plan store in a durable application. Each recipient row contains the exact protocol-v1 envelope and an accepted flag; a retry never re-encrypts, changes a nonce or replaces a `MessageId`. `ChatOutboxDispatcher` resumes pending logical sends on startup/foreground and removes completed plans according to the store policy.

```csharp
var outbox = new SqliteChatOutboxStore(
    "Data Source=protected/chat-outbox.db;Pooling=False");
await outbox.InitializeAsync(cancellationToken);

var fanOut = new ChatMultiDeviceSender(
    currentUserId,
    currentDeviceId,
    crypto,
    recipientDeviceDirectory,
    transport,
    outbox);

ChatFanOutSendResult sent = await fanOut.SendAsync(
    conversationId,
    content,
    cancellationToken);
var dispatcher = new ChatOutboxDispatcher(outbox, transport);
await dispatcher.DispatchAsync(cancellationToken: cancellationToken);
```

A new logical send snapshots current authorized active peer/sibling devices. A pending plan does not add newly registered devices or remove a now-revoked destination: mutating recipients would produce a different atomic send. Revocation stops future plans; server authorization still decides whether an old pending envelope can be accepted.

`SqliteChatEventStore.InitializeAsync` lazily creates schema version 1. Every operation uses a separate connection, a bounded busy timeout and parameterized commands. Inserts use a unique 16-byte delivery ID and distinguish exact retry from conflicting reuse. Newest/previous reads are bounded and use a stable insertion-order cursor. `SqliteChatOutboxStore` owns a separate versioned schema so outgoing retry state cannot be confused with authenticated received history. Because these contracts are durable, `:memory:`/`Mode=Memory` data sources are rejected; use the in-memory implementations only in tests. Neither adapter needs PostgreSQL or a server package.

Do not manually acknowledge the same polling stream before this coordinator completes. `ChatReceiver` and `IReceivedMessageStore` remain available for raw/legacy or host-managed receive paths, but they do not replace the durable typed-event pipeline above.

## Local plaintext boundary

The event SQLite `content` BLOB is the canonical decrypted typed-content encoding. It can contain message text, reactions, edited values, file names, captions and attachment decryption keys. Outbox rows contain recipient/device metadata and exact ciphertext/signatures; although not message plaintext, they remain sensitive traffic and identity data. SQLite provides no encryption at rest in this package. The host must:

- place the database in an access-controlled, platform-protected or encrypted location;
- exclude it from plaintext logs, crash attachments, unprotected cloud sync and casual backups;
- define retention, secure deletion and backup/recovery policy;
- treat database compromise as compromise of local chat history and delivered attachment keys;
- keep every `IChatEventApplier` idempotent and free of non-deduplicated external side effects.

The package does not provide cross-device history sync, key backup, search indexing, plaintext-history encryption, push delivery or exactly-once external side effects. See [ADR 0014](adr/0014-durable-client-events-and-sync.md), [ADR 0016](adr/0016-maui-client-orchestration.md), the [threat model](threat-model.md) and [MVP limitations](mvp-limitations.md).
