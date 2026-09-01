# ADR 0004: PostgreSQL-backed HTTP release gate

Status: accepted for package version 0.4.0.

Container provisioning for this gate is revised by [ADR 0015](0015-postgresql-testcontainers-gates.md).

## Context

The direct PostgreSQL repository test and the authenticated HTTP/E2EE vertical slice previously covered different storage implementations. A green infrastructure-free test run therefore did not prove that the public HTTP adapters, scoped `ChatServerEngine`, EF Core mappings and migration worked together. The PostgreSQL test also skipped when no connection string was supplied, which is convenient locally but unsafe as a release gate.

## Decision

- Add a second TestServer scenario that performs Alice encryption, bearer-authenticated HTTP registration/submission, PostgreSQL persistence, Bob polling/decryption and acknowledgement.
- Resolve `PostgreSqlChatStore` and `ChatDbContext` per request scope, matching the documented host registration.
- Keep DB tests opt-in for local development through `SKOPKA_CHAT_POSTGRES`.
- Add `SKOPKA_CHAT_POSTGRES_REQUIRED=true`; in this mode a missing connection string fails instead of skipping.
- Run direct persistence and HTTP-backed integration projects sequentially in CI against a disposable PostgreSQL 18 service.
- Pin GitHub Actions to full commit SHA, grant only read access to repository contents, bound the job duration and upload the resulting NuGet packages.

## Consequences

The release workflow now proves the full supported server path without changing protocol v1 or adding a production deployment. CI takes longer and depends on Docker Hub plus a PostgreSQL service. The gate does not cover load, failover, backup/restore, long-running cleanup, managed-database configuration or a real identity provider; those remain explicit production prerequisites.
