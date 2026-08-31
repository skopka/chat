# Documentation index

## Build and architecture

- [`development.md`](development.md) — local setup, dependency graph, test matrix, PostgreSQL gates, packaging, and contribution workflow.
- [`protocol-compatibility.md`](protocol-compatibility.md) — protocol-v1 canonical format and package compatibility table.
- [`../AGENTS.md`](../AGENTS.md) — authoritative instructions for coding agents.
- [`../CLAUDE.md`](../CLAUDE.md) — Claude Code adapter that imports the authoritative agent guide.

## Security and product scope

- [`threat-model.md`](threat-model.md) — assets, trust boundaries, attackers, mitigations, and residual risks.
- [`security-self-review.md`](security-self-review.md) — verified package/security boundaries and production blockers.
- [`mvp-limitations.md`](mvp-limitations.md) — protocol ceiling, host responsibilities, functional limits, and roadmap.

## Architecture decisions

- [`adr/0001-e2ee-cryptography.md`](adr/0001-e2ee-cryptography.md) — protocol-v1 cryptographic construction and its limits.
- [`adr/0002-aspnet-core-transport-authorization.md`](adr/0002-aspnet-core-transport-authorization.md) — authenticated HTTP server boundary.
- [`adr/0003-http-contract-and-client.md`](adr/0003-http-contract-and-client.md) — shared HTTP contract and authenticated typed client.
- [`adr/0004-postgresql-ci-gate.md`](adr/0004-postgresql-ci-gate.md) — required PostgreSQL-backed HTTP release gate.
- [`adr/0005-postgresql-concurrency-and-delivery-order.md`](adr/0005-postgresql-concurrency-and-delivery-order.md) — storage races, acknowledgement, ordering, and cleanup.
- [`adr/0006-strict-json-boundary.md`](adr/0006-strict-json-boundary.md) — strict, bounded, non-reflecting HTTP JSON parsing.

ADRs describe accepted decisions, not production certification. Start with the threat model and MVP limitations before integrating the packages into a real host.
