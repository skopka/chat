# ADR 0005: PostgreSQL concurrency and delivery ordering

Status: accepted for package version 0.5.0.

## Context

The protocol promises idempotence by message ID, but sequential tests did not prove behavior when independent requests insert, poll or acknowledge concurrently. Pending rows were ordered only by `accepted_at`; PostgreSQL may return tied rows in an implementation-dependent order, which makes bounded batches unstable. TTL deletion also lacked a real-database regression test.

## Decision

- Keep `message_id` as the envelope primary key and compare SHA-256 hashes of complete canonical envelope bytes after a unique-key race.
- Test identical and conflicting inserts concurrently through independent `ChatDbContext` instances. Exactly one insert wins; identical losers are duplicates and conflicting losers are rejected.
- Keep polling non-destructive and explicitly at-least-once. Concurrent pollers may receive the same pending envelope until acknowledgement.
- Keep acknowledgement as one conditional `UPDATE ... WHERE acknowledged_at IS NULL`; the affected-row count makes only the first concurrent acknowledgement successful.
- Order pending rows by `accepted_at`, then `message_id`, and add migration `202609010002_DeterministicPendingDeliveryOrder` for the matching index.
- Keep TTL cleanup as an immediate conditional delete and verify that repeated cleanup is idempotent and preserves unexpired rows.

## Consequences

Bounded batches are stable and the tested races have explicit outcomes without locks in application memory. This does not provide exactly-once delivery or exactly-once client display: a recipient must commit plaintext and its message ID transactionally before acknowledging. The tests cover controlled contention on one PostgreSQL node, not sustained load, failover, scheduler behavior or every transaction-isolation anomaly.
