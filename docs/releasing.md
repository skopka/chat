# Releasing Skopka.Chat packages

Skopka.Chat follows the coordinated NuGet publication model used by the maintained `Skopka.*` package families. One version represents all sixteen packages and is immutable once any member reaches NuGet.org.

## One-time repository configuration

In the GitHub repository settings:

1. Create an environment named `release`. Configure required reviewers if the organization uses manual release approval.
2. Add an environment secret named `NUGET_API_KEY` with permission to publish only the `Skopka.Chat.*` package IDs on NuGet.org.
3. Keep workflow token permissions at their checked-in minimum. The NuGet job needs no GitHub write permission; only the final GitHub Release job receives `contents: write`.
4. Protect `main` and require the CI workflow before merging. A release tag must point to a commit reachable from `origin/main`.

Do not store the NuGet key in repository secrets, local files, shell history, workflow artifacts, or package metadata when an environment-scoped secret is available.

## Release preparation

1. Set `VersionPrefix` in `Directory.Build.props` to the intended stable base version.
2. Update the README release summary, protocol compatibility table, security/limitation documents, and ADRs.
3. Run formatting, Release build, the complete infrastructure-free suite, all required disposable-PostgreSQL gates, fuzz corpus replay/AFL++ smoke, dependency audit, pack, and package-consumer validation.
4. Commit the release state and recreate packages after the commit. Verify the `.nuspec` repository SHA, sixteen `.nupkg` files, and sixteen `.snupkg` files.
5. Push the commit to `main` and wait for CI to succeed.

The checked-in CI/release gates set `SKOPKA_CHAT_POSTGRES_TESTCONTAINERS=true` and start a pinned disposable PostgreSQL per test assembly. `SKOPKA_CHAT_POSTGRES` remains an external disposable-database override for environments that cannot expose Docker to the test process.

Package IDs are published in dependency order:

1. `Skopka.Chat.Protocol`
2. `Skopka.Chat.Attachments`
3. `Skopka.Chat.Attachments.PostgreSql`
4. `Skopka.Chat.Attachments.S3`
5. `Skopka.Chat.Client`
6. `Skopka.Chat.Client.Storage`
7. `Skopka.Chat.Client.Storage.Sqlite`
8. `Skopka.Chat.Media`
9. `Skopka.Chat.Media.FFmpeg`
10. `Skopka.Chat.UI.Core`
11. `Skopka.Chat.UI.Blazor`
12. `Skopka.Chat.Server`
13. `Skopka.Chat.Transport.Http`
14. `Skopka.Chat.Client.Http`
15. `Skopka.Chat.Persistence.PostgreSql`
16. `Skopka.Chat.Server.AspNetCore`

## Triggering publication

Publication is intentionally tag-only. After explicit approval to publish, create a signed or annotated `v<SemVer>` tag on the validated `main` commit and push that tag. Examples of accepted shapes are `v0.11.0` and `v0.11.0-rc.1`; build metadata is rejected.

The release workflow then:

- proves the tag commit belongs to `main`;
- validates SemVer and matches its stable base to `VersionPrefix`;
- repeats formatting, build, tests, PostgreSQL gates, fuzz smoke, dependency audit, packing, and package-consumer execution;
- verifies the exact sixteen-package and sixteen-symbol-package set;
- refuses to start if any package ID already owns that version on NuGet.org;
- uploads immutable artifacts, enters the protected `release` environment, and pushes packages in dependency order;
- waits until every package is visible on NuGet.org;
- creates a GitHub Release containing all `.nupkg` and `.snupkg` files.

The workflow never publishes a server container because this repository intentionally contains no production host image.

## Failure and recovery

NuGet packages cannot be overwritten. If publication begins and only part of the coordinated set succeeds, do not rerun with `--skip-duplicate` and do not reuse the version. Diagnose the failure, increment the patch version, repeat the complete validation, and publish a new tag. Deprecate an incorrect package on NuGet.org when appropriate; unlisting is not a replacement for a corrected release.

Creating packages locally, uploading CI artifacts, or creating a Git commit does not authorize tag creation or publication. A release requires an explicit user decision.
