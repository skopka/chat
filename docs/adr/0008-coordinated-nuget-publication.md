# ADR 0008: coordinated NuGet publication

Date: 2026-09-01
Status: accepted for package version 0.7.0.

## Context

CI created seven local packages but there was no guarded publication path, no symbol packages, and no consumer test that restored the complete set without project references. Publishing interdependent packages ad hoc can leave a permanently partial version because NuGet artifacts are immutable.

## Decision

- Align with the maintained Skopka package release model: portable PDBs, `.snupkg`, repository/source metadata, LICENSE and README content, and a package-only consumer smoke test.
- Use one `VersionPrefix` for all packages. A `v<SemVer>` tag must be reachable from `main`; its stable base must match `VersionPrefix`.
- Validate formatting, all tests, required PostgreSQL paths, fuzz smoke, dependency audit, exact package set, and package consumer before publication.
- Before pushing anything, require that the version is absent for every `Skopka.Chat.*` ID on NuGet.org.
- Publish in dependency order from a protected `release` environment using only `NUGET_API_KEY`. Wait for all IDs to become visible, then create a GitHub Release with package and symbol artifacts.
- Do not use `--skip-duplicate` for coordinated releases. A partial publication consumes the version and requires a new patch release.

## Consequences

A tag is now a high-impact publication action rather than a naming convenience. Normal pushes and pull requests only build and upload temporary workflow artifacts. Repository administrators must configure the `release` environment and scoped NuGet key before the first tag. The workflow publishes no container because Skopka.Chat deliberately provides libraries and a sample, not a production host.
