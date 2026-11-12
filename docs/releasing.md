# Releasing Skopka.Chat packages

Skopka.Chat follows the coordinated NuGet publication model used by the maintained `Skopka.*` package families. One version represents all twenty-seven packages and is immutable once any member reaches NuGet.org.

## One-time repository configuration

In the GitHub repository settings:

1. Create an environment named `release`. Configure required reviewers if the organization uses manual release approval.
2. Configure a NuGet.org Trusted Publishing policy for the `Skopka` account with repository owner `skopka`, repository `chat`, workflow `release.yml` and environment `release`. The publish job exchanges its GitHub OIDC token (`id-token: write`) through `NuGet/login` for a short-lived API key; no long-lived `NUGET_API_KEY` secret is used, and an old one should be deleted. The policy must cover every coordinated package ID, including first-time IDs such as `Skopka.Chat.ServerTrusted*`.
3. Keep workflow token permissions at their checked-in minimum. The NuGet job needs no GitHub write permission; only the final GitHub Release job receives `contents: write`.
4. Protect `main` and require the CI workflow before merging. A release tag must point to a commit reachable from `origin/main`.

Never store a NuGet API key in repository or environment secrets, local files, shell history, workflow artifacts, or package metadata; the short-lived Trusted Publishing key exists only inside the publish job.

## Release preparation

1. Set `VersionPrefix` in `Directory.Build.props` to the intended stable base version.
2. Update the README release summary, protocol compatibility table, security/limitation documents, and ADRs.
3. Run formatting, Release build, the complete infrastructure-free suite, all required disposable-PostgreSQL gates, fuzz corpus replay/AFL++ smoke, dependency audit, the Windows/macOS MAUI matrix, pack, and core, browser and MAUI package-consumer validations.
4. Commit the release state and recreate packages after the commit. Verify the `.nuspec` repository SHA, twenty-seven `.nupkg` files, and twenty-seven `.snupkg` files.
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
10. `Skopka.Chat.Client.Maui`
11. `Skopka.Chat.UI.Core`
12. `Skopka.Chat.UI.Blazor`
13. `Skopka.Chat.UI.Maui`
14. `Skopka.Chat.Server`
15. `Skopka.Chat.Transport.Http`
16. `Skopka.Chat.Client.Http`
17. `Skopka.Chat.Persistence.PostgreSql`
18. `Skopka.Chat.Server.Kafka`
19. `Skopka.Chat.Server.AspNetCore`
20. `Skopka.Chat.Server.NSec`
21. `Skopka.Chat.Bots`
22. `Skopka.Chat.Bots.Sqlite`
23. `Skopka.Chat.Bots.AspNetCore`
24. `Skopka.Chat.Client.Browser`
25. `Skopka.Chat.ServerTrusted`
26. `Skopka.Chat.ServerTrusted.PostgreSql`
27. `Skopka.Chat.ServerTrusted.Blazor`

## Triggering publication

Publication is intentionally tag-only. After explicit approval to publish, create a signed or annotated `v<SemVer>` tag on the validated `main` commit and push that tag. Examples of accepted shapes are `v0.13.0` and `v0.13.0-rc.1`; build metadata is rejected.

The release workflow then:

- proves the tag commit belongs to `main`;
- validates SemVer and matches its stable base to `VersionPrefix`;
- repeats formatting, core build/tests, PostgreSQL gates, fuzz smoke and dependency audit on Linux;
- builds/tests/packs the MAUI packages on Windows, verifies Android/Windows sample builds, all four package assets and the Android package consumer; the commit must also have passed the CI iOS/Mac Catalyst and trimming gate;
- combines artifacts and verifies the exact twenty-seven-package and twenty-seven-symbol-package set;
- refuses to start if any package ID already owns that version on NuGet.org;
- uploads immutable artifacts, enters the protected `release` environment, and pushes packages in dependency order;
- waits up to 75 minutes until every package is visible on NuGet.org (validation and indexing are asynchronous and first-time package IDs can take longer than ten minutes);
- creates a GitHub Release containing all `.nupkg` and `.snupkg` files.

The workflow never publishes a server container because this repository intentionally contains no production host image.

## Failure and recovery

NuGet packages cannot be overwritten. If publication begins and only part of the coordinated set succeeds, do not rerun with `--skip-duplicate` and do not reuse the version. Diagnose the failure, increment the patch version, repeat the complete validation, and publish a new tag. Deprecate an incorrect package on NuGet.org when appropriate; unlisting is not a replacement for a corrected release.

If the push succeeded but some packages are still not indexed when the wait ends, the job fails with a message that NuGet.org accepted every package. That is not a partial publication: do not push again, do not re-run the failed jobs (the "completely unpublished version" check would now fail) and do not bump the version. Check the packages' validation status on NuGet.org, wait until all IDs appear, then create the GitHub Release for the tag manually and attach the `.nupkg` and `.snupkg` files from the run's `release-packages` artifact. A partial publication is only the case where the push itself failed after NuGet.org accepted some packages; the job reports that explicitly.

Creating packages locally, uploading CI artifacts, or creating a Git commit does not authorize library tag creation or NuGet publication. A package release requires an explicit user decision. Products documentation publication follows the separate standing instruction below.

## Products documentation

Follow the Products documentation synchronization requirements in [AGENTS.md](../AGENTS.md#products-documentation-synchronization) and the canonical [Products workflow](https://git.skopka.ru/skopka/products/src/branch/main/docs/documentation-workflow.md). Prepare consumer-facing documentation before release, then verify the published tag and all packages before updating the Products pin and deploying the site. Report any remaining publication or deployment stage.

## Publish the Products update immediately

The owner has authorized this documentation publication step. Unless the user
explicitly asks for drafts/local changes or no publication, finish a verified
public documentation update in the same task:

1. Confirm the documented Git tag/full SHA and the complete coordinated NuGet
   package set are available. Update the affected Products source pin, RU/EN
   pages, examples and navigation; build and review them according to the
   [canonical Products workflow](https://git.skopka.ru/skopka/products/src/branch/main/docs/documentation-workflow.md).
2. In the `products` repository, inspect the working tree and fetch remote `main`
   and tags. Preserve unrelated edits and drafts. Commit and push only reviewed,
   release-ready documentation changes; do not stage everything, reset or stash
   someone else's work. Ensure the tagged commit contains no unfinished work.
3. Select an unused stable `vX.Y.Z` greater than both the published version in
   `https://products.skopka.ru/release.json` and existing stable Products tags
   (compare semantic versions, not strings). Site versions are independent of
   package versions. Create an annotated tag on the exact reviewed Products
   commit and push it to `products`, not to this library or `build-projects`.
4. Wait for the Products release workflow to succeed and for the public
   `/release.json` to match both the site tag and full Products commit SHA.
   The tag triggers build and deployment directly; no infra version bump or
   scheduled infrastructure reconciliation is needed.
5. Verify the affected public RU/EN overview, guide chapters and API pages,
   including README/NuGet links. Report the package version/tag/SHA, Products
   tag/SHA, workflow result and live URLs. A queued or failed deployment is not
   complete: diagnose it and report any concrete blocker rather than promising
   an unspecified later publication.

Never move, delete or reuse an existing tag for changed content. A transient
failure may be retried using the same unchanged tag; corrected content requires
another higher site version. This instruction does not authorize new library or
NuGet releases. Do not create an empty Products release for instructions-only
changes or an update with no published documentation changes.
