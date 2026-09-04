# Browser E2EE integration

## Browser-bound trusted vault (0.17.0)

`BrowserVault.OpenTrustedAsync` creates or reopens a vault with a non-extractable
AES-GCM `CryptoKey` persisted by IndexedDB. The key does not cross JS interop and
does not go to the server. A 0.16 phrase vault deliberately returns
`phrase-required`; open it once with `OpenAsync`, call
`RememberForDeviceAsync`, dispose it, then future logins can use
`OpenTrustedAsync`. Existing encrypted rows and the device identity are not
rewritten.

If the application owner has explicitly declared all 0.16 browser data
disposable, `BrowserVault.DiscardLegacyAsync` irreversibly removes only that
account/service/installation scope. It rejects an already trusted v2 vault. The
host must separately account for the obsolete public server device/session.

This mode matches normal messenger ergonomics but changes the local threat
boundary: possession of an unlocked browser profile or same-origin code execution
can use the key. It only protects copied IndexedDB ciphertext. Account context,
logout cancellation and device revocation remain host responsibilities.

The 0.16.0 opt-in [encrypted history backup](backups.md) adds `BrowserBackupKeyStore`
and `BrowserBackupWorkspace` inside the existing encrypted vault. Use the existing
browser primitive provider and cookie/BFF authorizer; no crypto or recovery code
passes through server prerender/BFF. Attach the coordinator to `BrowserChatSession`
so logout cancels/awaits backup before closing the vault. New devices retain their
own installation and device keys. The compile-checked [factory](../samples/Skopka.Chat.Browser.Sample/BackupExample.cs)
does not automatically enable backup. Imported history requires the visible trust warning.

Introduced in package line **0.15.0**. Existing 0.14.0 servers remain compatible.
This browser text stage uses the existing server wire protocol;
no server decryption, private-key endpoint or protocol upgrade is required.

Package 0.18.0 keeps the same browser vault and outer protocol while adding small
groups and content-v4 structured mentions. Browser hosts resolve aliases locally,
queue one durable logical event and let the shared sender encrypt one envelope per
current participant device. Group metadata is server-visible; mention targets are not.

## Packages and responsibilities

- Client now supplies both native and `net10.0-browser` assets from shared code.
  Native constructors and NSecPrivateKey storage remain compatible.
- Client.Storage, Client.Http, Media, UI.Core and UI.Blazor also ship both target
  variants. This prevents native NSec transitive pins from being promoted into
  browser NuGet dependencies; project-reference tests alone would not catch it.
- New Client.Browser supplies libsodium.js primitives, encrypted IndexedDB
  identity/history/outbox, durable jobs, foreground session coordination and
  same-origin cookie/CSRF adapters.
- Client.Http retains bearer authentication and adds `IChatHttpRequestAuthorizer`.
- UI.Blazor uses Components.Web, not a transitive ASP.NET Core shared framework.
  It remains replaceable, localized and HTML-encoded by default.

The coordinated 0.15.0 set is 23 packages: 20 core/bot packages, one
browser package and two MAUI packages.
See [ADR 0019](adr/0019-browser-client-cryptography-and-vault.md).

## Integrating into SkopiClub's existing web cabinet

1. Host a standalone Blazor WebAssembly client (target `net10.0-browser`) and its
   same-origin static resources. Do not run decrypted components under Blazor
   Server, interactive-server rendering or prerender. A server-rendered account
   shell must not receive decrypted chat state.
2. Implement a production same-origin BFF with secure HttpOnly cookies. OAuth
   access/refresh tokens remain backend-only. Restrict proxy destinations/routes,
   disable redirects, enforce request/response limits and forward only existing
   public-device/binding/ciphertext contracts. No generic signing/decryption API.
3. Implement `IBrowserChatCsrfProvider` using the cabinet's antiforgery system, and
   `IBrowserChatAccountContextProvider` using its authenticated account endpoint.
   The latter returns exact configured service ID, stable user ID, session
   reference and deadline. Never derive the expected context only from a challenge.
4. Ask explicitly to initialize a new browser installation/vault/device. On
   subsequent logins load the existing installation, unlock the vault and load
   identity. Refresh/login must not call CreateAsync as an implicit fallback.
5. Bind via DeviceBindingCoordinator. Select Enrollment for unregistered local
   metadata and Rebind for an existing device. Start BrowserChatSession only after
   successful binding. Preserve service/account/installation scope across sessions.
6. Use QueueAsync before attempting delivery, DispatchAsync to retry queued work,
   SynchronizeAsync for store/apply-before-ACK, and ChatHistoryPager for visible
   history. Refresh the visible page when another tab may have received messages.
7. On logout/account switch, cancel and await all host tasks, dispose the session,
   then dispose the vault and clear UI state. Do not delete identity or history.
   Stop active work in every tab according to the cabinet's session policy.
8. Add production account/device trust UX, device revocation/step-up policy,
   retention/quota/recovery screens and deployment security tests. The sample's
   synthetic account selector is not an authentication implementation to deploy.

Core composition (application supplies trusted context, adapters and local phrase):

```csharp
await using var crypto = await BrowserChatCryptography.CreateAsync(js);
Guid installation = (await BrowserVault.GetInstallationIdAsync(js))
    ?? throw new InvalidOperationException("Explicit installation setup required.");
var scope = new DeviceIdentityScope(context.ServiceId, context.UserId, installation);
await using var vault = await BrowserVault.OpenAsync(js, scope, localPhraseUtf8);
var keys = new BrowserDeviceIdentityStore(vault);
var identities = new PersistentDeviceIdentityService(keys, keys, TimeProvider.System, crypto);
var loaded = await identities.LoadAsync(scope); // never silently creates keys
// Handle Absent/RecoveryRequired/Corrupt/Unavailable/Revoked before continuing.
var device = loaded.Metadata!.PublicDevice!;
using var http = new HttpClient { BaseAddress = pageOrigin };
var api = new SkopkaChatHttpClient(http, new BrowserBffAuthorization(pageOrigin, csrf),
    Options.Create(new SkopkaChatHttpClientOptions {
        AuthenticatedUserId = context.UserId.Value,
        AuthenticatedDeviceId = device.DeviceId.Value
    }), TimeProvider.System);
var proofs = new DeviceBindingProofService(keys, TimeProvider.System, crypto);
await new DeviceBindingCoordinator(identities, proofs, api).BindAsync(scope, context,
    loaded.Metadata.Registered ? DeviceBindingOperation.Rebind : DeviceBindingOperation.Enrollment);
await using var session = new BrowserChatSession(vault, device, crypto, api, api, applier);
await session.QueueAsync(conversationId, new ChatTextContent(ChatContentId.New(), text));
await session.DispatchAsync();
await session.SynchronizeAsync();
```

Use separate HttpClient instances for the account adapter and chat API. The typed
client configures timeout before its first request. Default headers must contain
no bearer token. Do not use the native DI registration's SocketsHttpHandler in WASM.

## Local protection, lifecycle and recovery

Vault schema 1 uses a separate local phrase (12–1024 UTF-8 bytes), Argon2id13
(3 iterations, 64 MiB, random per-vault salt), and non-extractable AES-256-GCM.
Choose a strong, long phrase unrelated to the account password; the byte-length
check is not an entropy guarantee. The phrase and AES key are not stored. Locking
requires re-entering it after reload/login; neither the server nor an account
password reset can unlock the vault. Phrase rotation/export/recovery is deferred.

Private keys, metadata, verified content (including any received attachment keys),
pre-network jobs and exact ciphertext plans are encrypted. IndexedDB still reveals
opaque IDs, record kinds, conversation indexes, sequence, lengths, nonce and salt.
No plaintext history, private key or OAuth token is put in localStorage.

Web Locks coordinate cooperating tabs; IndexedDB transactions enforce create-only
and revision compare-and-swap. Once a transaction starts, its completion is awaited
before releasing a lease. Browser/OS power-loss durability and eviction remain
platform concerns. Hosts may request [persistent storage](https://developer.mozilla.org/en-US/docs/Web/API/StorageManager/persist),
but a granted request is not a backup or protection against explicit clearing.

| Situation | Required behavior |
| --- | --- |
| Existing vault, correct phrase, retained keys | Same DeviceId/KeyId/public keys/history/outbox |
| Wrong phrase or damaged vault check | `unlock-failed`; no replacement and no recovery claim |
| Metadata exists but key record is missing | `RecoveryRequired`; creation returns that state |
| Damaged authenticated record/unknown schema | Corrupt/unavailable; generic error, no overwrite |
| Storage denied/blocked/full | Unavailable/`quota`; no ACK before durable commit |
| Interrupted reservation without keys | RecoveryRequired; no silent regeneration |
| Keys committed but final metadata write interrupted | Finalize the same reserved device from the retained keys |
| Server reports revocation during binding | Sticky Revoked metadata; no new keys |
| All origin data cleared/private browsing ended | Previous installation cannot be inferred or recovered |

Keep a host-owned device directory/recovery screen so a user can explicitly revoke
a lost device and choose a new enrollment. Preserving only its ID cannot recover
keys. Avoid retaining plaintext in analytics, crash dumps, clipboard or previews.
XSS/same-origin JS can read an unlocked vault and use non-extractable keys; E2EE
does not protect against substituted client code. Managed/JS strings cannot be
reliably erased. See [threat model](threat-model.md).

## HTTPS, CSP and hosting

Production requires HTTPS, same-origin secure HttpOnly cookies, reviewed CSRF and
origin policy, no permissive CORS, bounded proxying, redacted logs and pinned static
assets. Do not co-host untrusted scripts on the origin. A minimal tested CSP is:

```text
default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self';
connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'
```

`wasm-unsafe-eval` permits WebAssembly compilation, not general JavaScript eval.
No global unsafe-inline/unsafe-eval or runtime CDN is used. Custom templates/themes
must use CSS files rather than inline Style under this policy. Configure `.wasm`
as `application/wasm`, `.mjs` as JavaScript and ICU `.dat` as octet-stream. The demo
host handles these explicitly. Test your own hosting path, base URL, CSP and
compression rules with the published output, not just the development server.

## Build, run and verify

Prerequisites: pinned .NET SDK, Node 24.19.0, pnpm 11.19.0; Docker only for existing
PostgreSQL regression gates. From the repository root:

```powershell
dotnet restore Skopka.Chat.sln --configfile NuGet.Config
dotnet publish samples/Skopka.Chat.Browser.Sample -c Release -o artifacts/browser-publish --no-restore
dotnet run --project samples/Skopka.Chat.Browser.Host -c Release -- --webroot "$PWD/artifacts/browser-publish/wwwroot"
```

Open `http://127.0.0.1:5200/`. The host only accepts that exact loopback authority;
it intentionally refuses remote deployment. In two separate browser profiles,
log in as Alice and Bob, explicitly create a separate vault and device, enroll,
open the test conversation and send text. Use Receive/retry to poll, reload to
unlock/rebind and read retained history, then logout/login to verify unchanged ID.
The sample server uses disposable in-memory data: restarting it loses registration
and server queues. That does not authorize replacing retained client keys.

Stop the demo and any old test listeners on ports 5190/5200 before the full gate:

```powershell
pnpm --dir eng/browser install --frozen-lockfile --ignore-scripts
node eng/browser/node_modules/playwright/cli.js install chromium firefox
node eng/browser/run-gate.mjs
```

On Linux, use Playwright `install --with-deps chromium firefox`. The gate publishes
trimmed WASM, serves it under CSP, generates **synthetic-only** native fixtures,
runs both actual browser engines, verifies their outputs through native NSec and
exercises the complete cookie/CSRF sample. Never deploy `artifacts/browser-tests`:
its synthetic private-key vectors exist only for interoperability tests.

For local package-consumer validation after packing a fresh coordinated local
version (not a publication):

```powershell
dotnet pack Skopka.Chat.sln -c Release --no-build --no-restore -p:PackageVersion=0.16.0-browser-local -o artifacts/browser-packages
$env:CHAT_BROWSER_PACKAGE_VERSION = '0.16.0-browser-local'
$env:CHAT_BROWSER_PACKAGE_FEED = 'artifacts/browser-packages'
node eng/browser/run-gate.mjs
Remove-Item Env:CHAT_BROWSER_PACKAGE_VERSION
Remove-Item Env:CHAT_BROWSER_PACKAGE_FEED
```

The consumer uses package source mapping plus a separate cache, rejects native
NSec/libsodium/server framework dependencies and runs the published UI from NuGet
references. CI/release require this gate before artifacts can be published.
`eng/browser/vendor.mjs` regenerates local crypto assets from the pinned lockfile;
review upstream changes, licenses and SHA256SUMS before any dependency update.

## Verified matrix and limits

| Runtime | Verification in this change |
| --- | --- |
| Chromium 151 / Playwright build 1234, Windows x64 | Published WASM, native interop, vault, multi-tab, crash/recovery, outbox and cookie UI |
| Firefox 153 / Playwright build 1538, Windows x64 | Same real-runtime checks |
| Native .NET 10 / NSec, Windows x64 | Cross-runtime decrypt/signatures; existing client/binding/HTTP/storage/UI regression |
| MAUI orchestration net10.0 test target | Identity/session regression; no new device-runtime certification |
| Safari/WebKit, iOS/Android browsers, private browsing, older engines | Not certified by this change |

Tests cover exact canonical/binding bytes, altered envelopes, relogin identity,
service/account isolation, concurrent initializers, interrupted creation, stored
events from independent tab writers, serialized competing delivery workers,
history, exact duplicate/conflict, partial multi-device retry after reload,
real HTTP acceptance followed by page destruction and byte-identical retry,
network-offline job persistence, cross-origin/CSRF rejection, quota
failure without ACK, key corruption/loss, unavailable storage and revocation.
These bounded tests are not a browser/OS power-loss proof or independent audit.

First stage is text, foreground polling and durable retries. Media preparation,
push, background guarantees, service-worker offline shell, a finished contact
catalog, key backup/transfer/phrase rotation and the production SkopiClub BFF are
not implemented. After lock/reload, loading the app shell and authenticating may
still require connectivity even though unsent jobs/history remain encrypted locally.
