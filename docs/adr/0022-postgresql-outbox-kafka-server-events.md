# ADR 0022: PostgreSQL Outbox and optional Kafka server events

Date: 2026-09-04
Status: accepted for the first encrypted-envelope acceptance event.

## Context

Hosts need reliable asynchronous reactions to accepted chat envelopes without weakening
the synchronous command API or making Kafka authoritative. Direct publish after the
database commit loses notifications if the process fails between commit and publish;
publish before commit can expose an event for a transaction that later rolls back. Kafka
also must not pull broker dependencies into the transport-neutral server engine or expose
E2EE plaintext/key material.

## Decision

`Skopka.Chat.Server` owns broker-independent event, outbox, publisher, lease, dispatcher,
retry, and telemetry contracts. It references no Kafka library. The first wire event is
`skopka.chat.encrypted-envelope-accepted` version `1`, encoded as bounded UTF-8 JSON with
only server-visible routing identifiers and timestamps.

`Skopka.Chat.Persistence.PostgreSql` adds an append-only migration and writes the envelope
plus exact event payload in one `SaveChanges` transaction. The outbox has a unique source
tuple per message/type/version. Concurrent or exact `MessageId` retries therefore leave
one envelope and one event. Workers claim due rows with finite leases and
`FOR UPDATE SKIP LOCKED`; completion and reschedule updates require the current lease.

`Skopka.Chat.Server.Kafka` is a separate optional adapter. It provides an idempotent
`acks=all` producer and a controlled `BackgroundService`; topic auto-creation is disabled.
The topic is `skopka.chat.encrypted-envelope-accepted.v1`, keyed by recipient device ID.
The publisher uses the exact outbox bytes and includes event ID/type/version headers.

Delivery is at least once. A crash after Kafka acknowledgement and before the PostgreSQL
completion update republishes the same `eventId`. Every consumer must durably deduplicate
that ID and commit its Kafka offset only after its inbox/effect transaction. Kafka is a
notification log with bounded retention, not message history or an authorization source;
ordinary PostgreSQL/HTTP reads remain authoritative.

## Security and operational consequences

- No canonical envelope/content/binding bytes or synchronous HTTP DTOs change.
- Events contain no ciphertext or cryptographic key material, and logging omits event
  payload and broker exception text. Existing server-visible metadata is still exposed to
  Kafka operators and authorized consumers.
- Producer idempotence reduces broker retry duplicates but cannot provide end-to-end
  exactly-once side effects. Consumer inbox idempotency remains mandatory.
- PostgreSQL availability is required for commands and outbox progress; Kafka outages do
  not roll back already accepted commands. Backoff and leases prevent tight retry loops.
- Operators must pre-create/version topics, monitor backlog/lag/retries, bound completed
  row retention, and test Kafka security, ISR/failover, and capacity for the deployment.
- The coordinated package set gains the optional `Skopka.Chat.Server.Kafka`; the domain
  server still references only Protocol.

## Verification

Golden metadata-only JSON and assembly-boundary tests run without infrastructure.
Dispatcher tests cover failure, stable event ID, bounded backoff, retry, and cancellation.
The disposable PostgreSQL gate covers the migration and process loss between domain
commit and outbox completion, including lease-expiry redelivery and unchanged ordinary
history. Live multi-broker Kafka reliability remains a deployment-specific gate.
