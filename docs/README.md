# Documentation index

## Build and architecture

- [`development.md`](development.md) — local setup, dependency graph, test matrix, PostgreSQL gates, packaging, and contribution workflow.
- [`ui.md`](ui.md) — headless presentation model, Blazor components, theming, templates, host send boundary, and UI security limits.
- [`attachments.md`](attachments.md) — attachment encryption, HTTP transfer, PostgreSQL/S3 storage, host authorization and UI boundaries.
- [`media.md`](media.md) — client-side photo/video preparation, exact-file mode, FFmpeg integration and UI callback.
- [`client-storage.md`](client-storage.md) — durable verified-event history, SQLite setup, restart replay and safe acknowledgement ordering.
- [`maui.md`](maui.md) — .NET MAUI client/session composition, SecureStorage, native controls and platform gates.
- [`releasing.md`](releasing.md) — protected tag-based publication of the coordinated NuGet package set.
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
- [`adr/0007-json-fuzzing-and-kestrel-edge-gate.md`](adr/0007-json-fuzzing-and-kestrel-edge-gate.md) — coverage-guided JSON fuzzing and real-server edge behavior.
- [`adr/0008-coordinated-nuget-publication.md`](adr/0008-coordinated-nuget-publication.md) — immutable coordinated NuGet and GitHub releases.
- [`adr/0009-encrypted-content-events.md`](adr/0009-encrypted-content-events.md) — encrypted replies, forwards, reactions and deterministic local projection.
- [`adr/0010-headless-ui-and-blazor-components.md`](adr/0010-headless-ui-and-blazor-components.md) — framework-independent UI state and optional adaptable Blazor components.
- [`adr/0011-encrypted-attachments-and-storage.md`](adr/0011-encrypted-attachments-and-storage.md) — content-v2 attachment manifests, chunk encryption and independent storage.
- [`adr/0012-client-media-preparation.md`](adr/0012-client-media-preparation.md) — automatic client-side media compression and exact-file semantics.
- [`adr/0013-encrypted-message-edits.md`](adr/0013-encrypted-message-edits.md) — immutable encrypted edit events, author checks and deterministic projection.
- [`adr/0014-durable-client-events-and-sync.md`](adr/0014-durable-client-events-and-sync.md) — durable client journal, SQLite plaintext boundary and store/apply/ack ordering.
- [`adr/0015-postgresql-testcontainers-gates.md`](adr/0015-postgresql-testcontainers-gates.md) — pinned disposable PostgreSQL fixtures for local, CI and release gates.
- [`adr/0016-maui-client-orchestration.md`](adr/0016-maui-client-orchestration.md) — MAUI endpoint boundaries, durable multi-device outbox, paging and native UI.

ADRs describe accepted decisions, not production certification. Start with the threat model and MVP limitations before integrating the packages into a real host.
