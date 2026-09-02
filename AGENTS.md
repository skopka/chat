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
- Permanent device identity is service/account/installation-scoped, never sid/token-scoped. Creation uses atomic `TryCreateAsync`; loss/corruption/unavailability/revocation never triggers automatic key replacement. Scoped metadata initialization requires cooperative exclusive leases.
- Binding-v1 has its own canonical domain/version. Host authentication precedes bootstrap; bound-mode requests require asynchronous live binding resolution. Enrollment/consume/binding are atomic; exact retries cannot bypass revocation or extend expiry. Read ADR 0017 before changing this boundary.
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
| `src/Skopka.Chat.Attachments` | Opaque attachment IDs, immutable ciphertext storage and host authorization contracts | Protocol only |
| `src/Skopka.Chat.Attachments.PostgreSql` | Isolated bounded `bytea` attachment storage and migration | Attachments + EF Core/Npgsql |
| `src/Skopka.Chat.Attachments.S3` | S3-compatible immutable encrypted-object storage | Attachments + reviewed AWS S3 SDK |
| `src/Skopka.Chat.Client` | Shared identity, canonical envelope/file orchestration, primitive-provider boundary, typed content and fan-out; native + browser targets | Protocol + Attachments; reviewed NSec on native target only |
| `src/Skopka.Chat.Client.Browser` | Browser-only libsodium.js provider, encrypted IndexedDB, Web Locks, durable jobs/session and cookie BFF adapters | Client + Client.Storage + Client.Http + WebAssembly; never Server or native storage |
| `src/Skopka.Chat.Client.Storage` | Durable verified-event journal contracts, projection replay and store/apply/ack coordinator | Client only; never transport, server or a database provider |
| `src/Skopka.Chat.Client.Storage.Sqlite` | Local SQLite implementation of the verified-event journal | Client.Storage + reviewed SQLite provider; never server persistence |
| `src/Skopka.Chat.Bots` | Owner-hosted text bot runtime, disclosure, live consent and inbox contracts | Client only; never Server or persistence |
| `src/Skopka.Chat.Bots.Sqlite` | Local bot inbox, suppression/ack tombstones and send request idempotency | Bots + reviewed SQLite provider |
| `src/Skopka.Chat.Bots.AspNetCore` | Private bot HTTP gateway and Data Protection-backed file identity adapter | Bots + ASP.NET Core; never Server |
| `src/Skopka.Chat.Media` | Client-side media preparation contracts and prepare/encrypt/upload orchestration | Client only; never transport, server, persistence, or a media executable |
| `src/Skopka.Chat.Media.FFmpeg` | Optional FFmpeg photo/video transformer over a host-protected plaintext work directory | Media only; the host supplies and maintains the executable |
| `src/Skopka.Chat.UI.Core` | Framework-independent conversation presentation state and host-owned send boundary | Client only; never transport, server, persistence, or a UI framework |
| `src/Skopka.Chat.UI.Blazor` | Themeable, replaceable Blazor components and localized UI strings | UI.Core plus Components.Web; no ASP.NET Core framework reference, server or persistence |
| `src/Skopka.Chat.Transport.Http` | Shared routes, HTTP DTOs, limits, mappings, strict source-generated JSON metadata | Protocol only |
| `src/Skopka.Chat.Client.Http` | Authenticated typed HTTP client, bounded responses, retries and encrypted attachment upload adapter | Client + Media + Transport.Http; never Server |
| `src/Skopka.Chat.Server` | Transport-neutral device/conversation/envelope engine and repository contracts | Protocol only; never Client or ASP.NET Core |
| `src/Skopka.Chat.Server.NSec` | Optional public-key-only binding-v1 Ed25519 verifier | Server + reviewed NSec; never Client or private-key/decryption APIs |
| `src/Skopka.Chat.Server.AspNetCore` | Authenticated Minimal API adapter, principal mapping and optional ciphertext attachment routes | Protocol + Server + Attachments + Transport.Http; never Client |
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
- Typed content is parsed only after envelope authentication. Preserve its separate version, strict UTF-8 and bounds; do not treat a forward marker as verified original attribution or collapse recipient-specific `MessageId` into logical `ChatContentId`. Edit events apply only to the original authenticated sender user's text/caption, may arrive before their target, and never rewrite server ciphertext.
- Attachment content v2 and chunk framing v1 are canonical security formats. Keep file key/name/MIME/caption out of server/storage metadata, never reuse a nonce/index pair, validate exact length/hash before immutable storage, and require callers to discard partial plaintext destinations after failure.
- Media preparation is client-side plaintext processing before attachment encryption. `File` mode must be byte-exact and never invoke FFmpeg; `Auto` must safely retain the original when transformation is unavailable or not smaller. Use generated paths only, generic failures, direct process arguments without a shell, and a host-protected working directory with bounded time/disk/concurrency and startup cleanup.
- UI packages handle decrypted managed strings. Keep messages HTML-encoded by default, retain only generic expected-failure state, and keep encryption, device fan-out, protected history and transport errors behind the host-owned `IChatContentSender` boundary.
- Durable typed receive must remain authenticate/decrypt → atomic event store/compare → idempotent apply → acknowledge. SQLite client history is plaintext (including attachment keys); keep its schema versioned, reads bounded/paged, errors generic and file/database protection explicitly host-owned.
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

Certify a host-installed FFmpeg/ffprobe pair with synthetic media when the media adapter changes or before deploying that binary:

```powershell
$env:SKOPKA_CHAT_FFMPEG = (Get-Command ffmpeg).Source
$env:SKOPKA_CHAT_FFMPEG_REQUIRED = 'true'
dotnet test --project tests/Skopka.Chat.Media.Tests --configuration Release --no-restore
```

PostgreSQL tests require an explicitly disposable database. They mutate schema and test rows. Never point them at a shared or production database. Prefer the pinned Testcontainers fixture when Docker is available:

```powershell
$env:SKOPKA_CHAT_POSTGRES_TESTCONTAINERS = 'true'
$env:SKOPKA_CHAT_POSTGRES_REQUIRED = 'true'
dotnet test --project tests/Skopka.Chat.Persistence.PostgreSql.Tests --configuration Release --no-build --no-restore
dotnet test --project tests/Skopka.Chat.Binding.Tests --configuration Release --no-build --no-restore
dotnet test --project tests/Skopka.Chat.Attachments.Tests --configuration Release --no-build --no-restore
dotnet test --project tests/Skopka.Chat.Http.IntegrationTests --configuration Release --no-build --no-restore
```

Set `SKOPKA_CHAT_POSTGRES` instead to use an explicitly disposable external database; it takes precedence over Testcontainers. `SKOPKA_CHAT_POSTGRES_REQUIRED=true` is mandatory for a release-like database gate so a missing external database and disabled Testcontainers fail instead of skipping. CI in `.github/workflows/ci.yml` is the canonical full gate.

## Change-specific expectations

- Protocol or cryptography: run Protocol, Client, and in-memory integration tests; update compatibility/threat documentation and golden vectors when applicable.
- Typed client content/projection: run Client and in-memory integration tests, replay the fuzz corpus (and AFL++ when available), preserve content-version golden bytes, and update compatibility/threat documentation.
- UI state/components: run both UI test projects, verify host templates and localized strings remain replaceable, and prove the UI assemblies do not reference Server or Persistence.
- Client event storage/synchronization: run `Skopka.Chat.Client.Storage.Tests`; prove independent-writer atomicity, exact duplicate/conflict behavior, restart replay, authentication-before-storage and no acknowledgement before durable apply.
- Server rules: run Server and in-memory integration tests; prove rejection before persistence.
- HTTP DTO/parser/client/server changes: run both HTTP unit projects, fuzz corpus replay (and AFL++ when available), and `Skopka.Chat.Http.IntegrationTests`; cover malformed and hostile inputs on both sides.
- PostgreSQL query/model/migration changes: run the complete PostgreSQL project against a disposable database and the PostgreSQL-backed HTTP integration.
- Attachment crypto/storage/HTTP changes: run Client, Attachments, both HTTP projects, content fuzz replay/AFL++, and the attachment PostgreSQL gate; test truncation, trailing data, tampering, ID conflict, authorization and partial-destination behavior.
- Media preparation changes: run Media tests plus Client and Client.Http tests; prove exact `File` bypass, `Auto` fallback, generated path isolation, bounded output, generic failures and prepare-before-encrypt ordering. A fake runner does not certify a deployment's FFmpeg binary; run the opt-in synthetic conformance gate against the selected host build.
- Authentication/authorization changes: include missing, malformed, duplicate, and cross-user/device negative cases; never use untrusted headers as a production authentication example.
- Bot changes: run `Skopka.Chat.Bots.Tests`, strict JSON/content fuzz replay, Client.Storage and HTTP integration tests; preserve deny-by-default host consent, operator revision, two-stage acknowledgement, exact outbox retries and create-only protected identity. Read ADR 0018; never implement a plaintext gateway in Server.
- Browser changes: read ADR 0019 and `docs/browser.md`; run `node eng/browser/run-gate.mjs` in real Chromium and Firefox, native/binding/HTTP/storage/UI regressions and the browser NuGet consumer. Keep crypto vendored and pinned; never silently change NSecPrivateKey or portable-key/vault versions. No plaintext IndexedDB/localStorage, server prerender or fake bearer tokens. Explicit installation/vault/device creation and cross-tab reserve/create/finalize remain mandatory.
- Identity/binding changes: run Binding and Client.Maui tests, binding corpus replay, required owned-container PostgreSQL restart/atomicity and HTTP re-login/history/outbox gates. Update `docs/device-identity.md` and ADR 0017. Custom stores must preserve create-only and crash-recovery semantics.
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
- `docs/adr/0011-encrypted-attachments-and-storage.md` defines content-v2 manifests, chunk framing, storage visibility and PostgreSQL/S3/HTTP boundaries.
- `docs/adr/0012-client-media-preparation.md` defines client-only photo/video transformation, send modes, plaintext work files and unchanged content-v2 compatibility.
- `docs/adr/0013-encrypted-message-edits.md` defines content-v3 edit bytes, author checks, deterministic folding and UI edit semantics.
- `docs/adr/0014-durable-client-events-and-sync.md` defines verified local history, SQLite plaintext storage and store/apply/ack ordering.
- `docs/adr/0016-maui-client-orchestration.md` defines MAUI endpoint boundaries, exact multi-device outbox retries, bounded paging and native UI responsibilities.

Update documentation in the same change when public APIs, package boundaries, protocol behavior, security assumptions, deployment responsibilities, migrations, or verification commands change.

## Release and Git hygiene

Before a requested release or version commit:

1. Update `VersionPrefix` in `Directory.Build.props`.
2. Update the README release summary and `docs/protocol-compatibility.md`.
3. Run formatting, Release build, the infrastructure-free solution tests, required PostgreSQL gates, and pack validation.
4. Create a focused commit only if requested.
5. Recreate packages after that commit so NuGet `<repository commit>` metadata points at the release commit, then inspect at least one `.nuspec`.
6. Confirm exactly twenty-three versioned `.nupkg` and twenty-three matching `.snupkg` files were produced in `artifacts/packages`, run core, browser and MAUI package consumers, and ensure the working tree is clean.

Publication is performed only by `.github/workflows/release.yml` for an explicit `v<SemVer>` tag reachable from `main`. The workflow validates the complete coordinated set before entering the protected `release` environment and using `NUGET_API_KEY`. Never use `--skip-duplicate` for a coordinated release or manually republish a partial version; advance to a new patch version. Do not create or push a release tag unless the user explicitly requests publication.

Use conventional, focused commit subjects such as `feat: ...`, `fix: ...`, `test: ...`, or `docs: ...`. Do not amend, force-push, reset, or rewrite history without explicit authorization. Do not push or publish merely because a local commit/package was requested.

## Definition of done

A change is complete when the requested behavior is implemented, relevant negative cases exist, package boundaries and security invariants still hold, appropriate tests pass, skipped infrastructure is disclosed, documentation matches reality, generated artifacts contain current metadata when requested, and `git status` contains no unexplained changes.
