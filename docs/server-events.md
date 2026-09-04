# Reliable server events through PostgreSQL Outbox and Kafka

This integration is optional. Synchronous authenticated HTTP commands, polling,
acknowledgement, and PostgreSQL history remain the authoritative chat path. Kafka is
only a prompt for downstream work; it is never the only copy of an envelope and a
consumer must use the ordinary authorized API/storage boundary when it needs current
message or conversation state.

## First vertical slice

The first version emits `skopka.chat.encrypted-envelope-accepted` schema `1` after a
recipient-specific protocol-v1 envelope is accepted. `PostgreSqlChatStore` inserts the
envelope and `chat_server_event_outbox` row in the same EF Core/PostgreSQL transaction.
An exact HTTP/engine retry by `MessageId` compares canonical envelope bytes and does not
create a second event.

The JSON value contains only:

- `eventId`, `messageId`, and `conversationId`;
- sender and recipient public device IDs;
- outer protocol version;
- server-visible sent, expiry, and acceptance timestamps.

It contains no plaintext, typed content, attachment manifest/key, private/public key
bytes, ciphertext, nonce, authentication tag, or signature. The Kafka record key is the
recipient device ID, preserving order within that device's partition. The stable
`eventId` is the consumer idempotency key; `messageId` remains the envelope identity.

## Host registration

Apply the append-only `202609040001_EncryptedEnvelopeEventOutbox` migration, then register
the scoped PostgreSQL outbox and optional Kafka adapter:

```csharp
using Skopka.Chat.Persistence.PostgreSql;
using Skopka.Chat.Server;
using Skopka.Chat.Server.Kafka;

builder.Services.AddDbContext<ChatDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<PostgreSqlChatStore>();
builder.Services.AddScoped<IEnvelopeRepository>(services =>
    services.GetRequiredService<PostgreSqlChatStore>());
builder.Services.AddScoped<IChatServerEventOutbox, PostgreSqlChatEventOutbox>();
builder.Services.AddSkopkaChatKafkaServerEvents(options =>
{
    options.BootstrapServers = "kafka:9092";
    options.EncryptedEnvelopeAcceptedTopic =
        KafkaChatServerEventTopics.EncryptedEnvelopeAcceptedV1;
});
```

The adapter uses `acks=all`, producer idempotence, bounded delivery time, and
`allow.auto.create.topics=false`. Its hosted worker claims a bounded batch with
`FOR UPDATE SKIP LOCKED`, publishes each record, and marks it complete only after the
broker acknowledgement. A finite lease permits another instance to retry after a crash.
Failures use capped exponential backoff with stable per-event jitter. Cancellation stops
new work immediately; an interrupted or publish-before-mark event becomes eligible after
lease expiry and may therefore be delivered again.

## Required topic

Topic auto-creation is deliberately disabled. For the supplied single-node local KRaft
broker, create the topic explicitly before starting the host:

```bash
kafka-topics.sh \
  --bootstrap-server kafka:9092 \
  --create \
  --if-not-exists \
  --topic skopka.chat.encrypted-envelope-accepted.v1 \
  --partitions 12 \
  --replication-factor 1 \
  --config cleanup.policy=delete \
  --config retention.ms=604800000 \
  --config min.insync.replicas=1
```

Replication factor `1` and `min.insync.replicas=1` are local single-node settings, not a
production recommendation. Production operators must select replication, ISR, retention,
quotas, authentication/authorization, TLS, and disaster recovery for their own cluster.
Topic retention does not define chat-history retention.

## Consumer contract

Each independent use case needs a distinct bounded `group.id`. Recommended baseline:

```text
enable.auto.commit=false
enable.auto.offset.store=false
auto.offset.reset=earliest
isolation.level=read_committed
allow.auto.create.topics=false
```

For every record, a consumer must validate the expected topic/type/version and bounded
JSON, then atomically persist `eventId` in its own durable inbox together with any local
side effect. Only after that commit should it store/commit the Kafka offset. A duplicate
`eventId` is acknowledged without reapplying the effect. Kafka producer idempotence does
not remove duplicates caused by a crash after broker acknowledgement and before the
PostgreSQL outbox completion update.

Consumers must not treat a notification as proof that an envelope is still pending or
that the caller is still authorized. Expiry, acknowledgement, revocation, and membership
can change independently; read current state through the normal authorized server path.

## Operations and tests

The dispatcher emits the `Skopka.Chat.Server.Events` activity source and meter:

- `skopka.chat.server.events.claimed`;
- `skopka.chat.server.events.published`;
- `skopka.chat.server.events.retried`;
- `skopka.chat.server.events.publish_lag` in seconds.

Alerts should cover oldest due outbox age, pending count, repeated attempts, publish lag,
and worker/process availability. Logs intentionally omit payloads and broker exception
details; deployment diagnostics must remain access-controlled and redacted. Completed
rows are deleted in bounded batches after the configured retention period.

The infrastructure-free tests pin the v1 JSON shape, prove no Kafka dependency in
`Skopka.Chat.Server`, cover failure/backoff, and verify controlled cancellation. The
required disposable-PostgreSQL gate applies the migration and simulates commit followed
by publisher process loss: the same `eventId` is reclaimed after lease expiry while the
ordinary envelope remains readable. Cluster authentication, failover, multi-broker ISR,
and sustained-load behavior remain deployment gates.
