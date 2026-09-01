# Skopka.Chat agent guide

This file is the authoritative repository instruction set for coding agents. Tool-specific files must import or link to this file instead of copying its rules. If a nested `AGENTS.md` is added later, it may refine these instructions only for its subtree.

## Start every task here

1. Read the user request, this file, `README.md`, and the documents relevant to the area being changed.
2. Inspect `git status --short` before editing. Preserve unrelated user changes and never discard work you did not create.
3. Locate the owning package and its tests before changing a public contract.
4. Make the smallest coherent change, then verify it with the narrowest useful test followed by the appropriate repository gate.
5. Report tests that were skipped and why. A skipped PostgreSQL test is not a successful release gate.

Do not commit, push, publish packages, alter shared infrastructure, or perform destructive Git operations unless the user explicitly requests that action. Never place real secrets, production connection strings, access tokens, private keys, or user message data in source, fixtures, logs, exception text, or documentation; clearly synthetic plaintext belongs only in tests and samples that require it.

## Product and security invariants

Skopka.Chat is a transport-independent personal-chat engine for .NET 10. Protocol v1 is a constrained E2EE MVP, not Signal Protocol, MLS, or an audited production protocol.

These invariants are non-negotiable:

- The server may store public device data, conversation metadata, ciphertext, authentication data, and delivery state. It must not receive plaintext or private keys and must not expose a decryption API.
- Private key material crosses only `IDeviceKeyStore`. In-memory key and message stores are test/sample implementations, never production recommendations.
- Protocol v1 canonical bytes are the signing and AEAD source of truth. JSON is never signed and is not AEAD associated data.
- Never silently reinterpret an existing protocol version. A canonical wire change requires a new protocol version, a distinct domain separator, compatibility documentation, and new golden vectors.
- Public device keys are authenticated directory data, not automatically trusted identity. Preserve security-code/fingerprint guidance and key-change warnings.
- Delivery is at-least-once until acknowledgement. Keep message-ID idempotency and transactional client-side deduplication; do not claim exactly-once display.
- Access-token validation, TLS termination, rate limits, CSRF/CORS policy, proxy limits, protected key storage, backups, and operational monitoring remain host responsibilities unless a task explicitly adds a reviewed implementation.
- Treat cryptographic changes, authentication changes, parser relaxations, logging changes, and persistence concurrency changes as security-sensitive.

Read `docs/threat-model.md`, `docs/security-self-review.md`, `docs/mvp-limitations.md`, and `docs/adr/0001-e2ee-cryptography.md` before changing a trust boundary or cryptographic behavior.

## Repository map and dependency direction

| Path | Responsibility | Allowed direction |
| --- | --- | --- |
| `src/Skopka.Chat.Protocol` | IDs, bounds, protocol DTOs, validation, canonical v1 encoding | No framework, persistence, server, or client dependency |
| `src/Skopka.Chat.Client` | Device identity, key storage abstractions, NSec cryptography, typed encrypted content/projection, fingerprints, receive deduplication, `IChatTransport` | Protocol only, plus the reviewed crypto dependency |
| `src/Skopka.Chat.Transport.Http` | Shared routes, HTTP DTOs, limits, mappings, strict source-generated JSON metadata | Protocol only |
| `src/Skopka.Chat.Client.Http` | Authenticated typed HTTP client, bounded responses, retries | Client + Transport.Http; never Server |
| `src/Skopka.Chat.Server` | Transport-neutral device/conversation/envelope engine and repository contracts | Protocol only; never Client or ASP.NET Core |
| `src/Skopka.Chat.Server.AspNetCore` | Authenticated Minimal API adapter and principal mapping | Protocol + Server + Transport.Http; never Client |
| `src/Skopka.Chat.Persistence.PostgreSql` | EF Core/Npgsql implementation, migrations, cleanup | Protocol + Server |
| `tests/*` | Unit, boundary, in-memory, PostgreSQL, and full HTTP/E2EE tests | May compose only the packages needed by the scenario |
| `samples/Skopka.Chat.Sample` | Demonstration code, not a production host | Keep security limitations explicit |

Do not solve dependency cycles by moving client cryptography into Protocol or by making Server reference Client. Automated assembly-boundary tests are intentional release gates.

## Implementation conventions

- The SDK is pinned by `global.json`; the repository currently targets `net10.0` and C# 14.
- Nullable reference types, XML documentation generation, recommended analyzers, and warnings-as-errors are enabled globally.
- Keep package versions centralized in `Directory.Build.props` and dependency versions in `Directory.Packages.props`.
- Prefer immutable records/value objects and explicit validation at public boundaries.
- Use `DateTimeOffset` and an injected `TimeProvider` for observable time. Avoid hidden `UtcNow` calls in testable logic.
- Propagate `CancellationToken` through asynchronous I/O. Do not retry caller cancellation.
- Public exception text must be bounded and generic. Do not attach remote JSON/parser exceptions when they can contain attacker-controlled property names or paths.
- Preserve source-generated `System.Text.Json` metadata. The HTTP profile is case-sensitive and rejects duplicate/unmapped members, comments, trailing commas/data, coercion, null/missing required values, and nesting beyond 16.
- `AddSkopkaChatAspNetCore` applies that strict profile to shared Minimal API `HttpJsonOptions`; document compatibility implications if this integration changes.
- Keep request/response byte bounds before expensive parsing or cryptographic work. Protocol byte-array length validation remains authoritative after Base64 decoding.
- Preserve deterministic ordering. PostgreSQL pending delivery is ordered by `accepted_at`, then `message_id`.
- Typed content is parsed only after envelope authentication. Preserve its separate version, strict UTF-8 and bounds; do not treat a forward marker as verified original attribution or collapse recipient-specific `MessageId` into logical `ChatContentId`.
- Treat EF migrations as append-only history. Add a new migration for schema/index changes; do not rewrite a migration that may already have been applied.
- Match the existing xUnit naming style: descriptive method names with underscores and deterministic data. Security regressions should assert both rejection and absence of state/log/exception reflection.
- Avoid speculative abstractions, unrelated cleanup, or new production claims outside the requested scope.

## Verification commands

Restore only when dependencies or generated restore state require it:

```powershell
dotnet restore Skopka.Chat.sln --configfile NuGet.Config
```

For normal changes:

```powershell
dotnet format Skopka.Chat.sln --verify-no-changes --no-restore
dotnet build Skopka.Chat.sln --configuration Release --no-restore
dotnet test --solution Skopka.Chat.sln --configuration Release --no-build --no-restore
```

Replay the bounded JSON fuzz corpus on every HTTP contract change:

```powershell
dotnet run --project tests/Skopka.Chat.FuzzTests --configuration Release --no-build -- --replay tests/Skopka.Chat.FuzzTests/corpus
```

The same corpus includes the encrypted-content decoder and must also be replayed on typed content/parser changes.

On Linux with AFL++ installed, run the coverage-guided smoke harness. Its output directory must not already exist:

```bash
bash eng/run-json-fuzz-smoke.sh 30 artifacts/fuzz-local
```

Run a narrow project first while iterating, for example:

```powershell
dotnet test --project tests/Skopka.Chat.Client.Http.Tests --no-restore
dotnet test --project tests/Skopka.Chat.Server.AspNetCore.Tests --no-restore
```

PostgreSQL tests require an explicitly disposable database. They mutate schema and test rows. Never point them at a shared or production database.

```powershell
$env:SKOPKA_CHAT_POSTGRES = 'Host=localhost;Port=5432;Database=skopka_chat_tests;Username=postgres;Password=...;Pooling=false'
$env:SKOPKA_CHAT_POSTGRES_REQUIRED = 'true'
dotnet test --project tests/Skopka.Chat.Persistence.PostgreSql.Tests --configuration Release --no-build --no-restore
dotnet test --project tests/Skopka.Chat.Http.IntegrationTests --configuration Release --no-build --no-restore
```

`SKOPKA_CHAT_POSTGRES_REQUIRED=true` is mandatory for a release-like database gate so a missing connection string fails instead of skipping. CI in `.github/workflows/ci.yml` is the canonical full gate.

## Change-specific expectations

- Protocol or cryptography: run Protocol, Client, and in-memory integration tests; update compatibility/threat documentation and golden vectors when applicable.
- Typed client content/projection: run Client and in-memory integration tests, replay the fuzz corpus (and AFL++ when available), preserve content-version golden bytes, and update compatibility/threat documentation.
- Server rules: run Server and in-memory integration tests; prove rejection before persistence.
- HTTP DTO/parser/client/server changes: run both HTTP unit projects, fuzz corpus replay (and AFL++ when available), and `Skopka.Chat.Http.IntegrationTests`; cover malformed and hostile inputs on both sides.
- PostgreSQL query/model/migration changes: run the complete PostgreSQL project against a disposable database and the PostgreSQL-backed HTTP integration.
- Authentication/authorization changes: include missing, malformed, duplicate, and cross-user/device negative cases; never use untrusted headers as a production authentication example.
- Dependency changes: update only `Directory.Packages.props`, review transitive/native impact, restore, and run the complete gate.
- Documentation-only changes: run `git diff --check` and validate every local link/path and every command against the repository.

## Documentation and decisions

- `README.md` is the package overview and quick start.
- `docs/README.md` is the documentation index.
- `docs/development.md` is the human contributor workflow.
- `docs/protocol-compatibility.md` records package/protocol compatibility.
- `docs/threat-model.md`, `docs/security-self-review.md`, and `docs/mvp-limitations.md` must remain candid; do not weaken limitations to market unfinished work.
- `docs/adr/` records durable architecture/security decisions. Add a numbered ADR when changing a trust boundary, wire/storage semantics, dependency direction, or release gate.
- `docs/adr/0009-encrypted-content-events.md` defines typed content IDs, replies, forward/reaction semantics and projection conflicts separately from the outer protocol version.

Update documentation in the same change when public APIs, package boundaries, protocol behavior, security assumptions, deployment responsibilities, migrations, or verification commands change.

## Release and Git hygiene

Before a requested release or version commit:

1. Update `VersionPrefix` in `Directory.Build.props`.
2. Update the README release summary and `docs/protocol-compatibility.md`.
3. Run formatting, Release build, the infrastructure-free solution tests, required PostgreSQL gates, and pack validation.
4. Create a focused commit only if requested.
5. Recreate packages after that commit so NuGet `<repository commit>` metadata points at the release commit, then inspect at least one `.nuspec`.
6. Confirm exactly seven versioned `.nupkg` and seven matching `.snupkg` files were produced in `artifacts/packages`, run the package consumer, and ensure the working tree is clean.

Publication is performed only by `.github/workflows/release.yml` for an explicit `v<SemVer>` tag reachable from `main`. The workflow validates the complete coordinated set before entering the protected `release` environment and using `NUGET_API_KEY`. Never use `--skip-duplicate` for a coordinated release or manually republish a partial version; advance to a new patch version. Do not create or push a release tag unless the user explicitly requests publication.

Use conventional, focused commit subjects such as `feat: ...`, `fix: ...`, `test: ...`, or `docs: ...`. Do not amend, force-push, reset, or rewrite history without explicit authorization. Do not push or publish merely because a local commit/package was requested.

## Definition of done

A change is complete when the requested behavior is implemented, relevant negative cases exist, package boundaries and security invariants still hold, appropriate tests pass, skipped infrastructure is disclosed, documentation matches reality, generated artifacts contain current metadata when requested, and `git status` contains no unexplained changes.
