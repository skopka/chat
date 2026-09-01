# ADR 0015: Disposable PostgreSQL Testcontainers gates

- Status: accepted
- Date: 2026-09-01

## Context

The required persistence, attachment and authenticated HTTP/E2EE gates need a disposable PostgreSQL database. Local execution previously required a manually created database and connection string, while CI and release workflows duplicated a PostgreSQL service definition. This made the safe full gate less convenient locally and left container lifecycle outside the test assemblies that mutate the database.

The infrastructure-free solution run must remain usable without Docker. A requested release gate, however, must fail if its database cannot be provisioned rather than silently skip.

## Decision

- Add a test-only `Skopka.Chat.Testing` assembly with one xUnit v3 assembly fixture per PostgreSQL-backed test project.
- When `SKOPKA_CHAT_POSTGRES` is set, use that explicitly disposable external database and do not start a container.
- When `SKOPKA_CHAT_POSTGRES_TESTCONTAINERS=true` and no external connection string is set, start PostgreSQL `18.6-alpine3.24` pinned by image digest on a random host port. Disable Npgsql pooling for deterministic teardown and dispose the container with the test assembly.
- Keep database tests opt-in for the ordinary infrastructure-free solution run. Without an external connection string or the Testcontainers flag, the database scenarios skip.
- Require `SKOPKA_CHAT_POSTGRES_REQUIRED=true` in CI and release-like runs. A missing configuration or failure to start a requested container is an error.
- Run the three database projects sequentially in CI and release workflows. Each project owns an isolated container, migration history and test rows.
- Keep Testcontainers and xUnit fixture dependencies in test-only projects. The sixteen published package dependency graphs and protocol/content bytes do not change.

## Consequences

Developers with Docker can execute the complete PostgreSQL gate without manually managing credentials, ports, database creation or cleanup. CI no longer duplicates a service-container definition, and the same fixture lifecycle is exercised locally and remotely. External disposable databases remain supported for managed PostgreSQL compatibility checks.

The gate now depends on a Docker-compatible engine, Docker Hub availability when the pinned image is not cached, and the Testcontainers resource reaper. Three isolated containers add startup overhead, but avoid cross-project schema/data interference. These tests still do not cover managed-database configuration, backup/restore, failover, long-duration load or production credentials.
