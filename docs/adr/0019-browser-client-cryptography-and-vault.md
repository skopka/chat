# ADR 0019: browser endpoint cryptography, encrypted vault and cookie BFF

- Date: 2026-09-02
- Status: implemented in 0.15.0; independent security review required
- Wire compatibility: protocol-v1, content-v1/v2/v3 and binding-v1 unchanged

## Decision

Multi-target `Skopka.Chat.Client` as `net10.0` and `net10.0-browser`, compiling the
same protocol, identity, projection and fan-out source files. Only the native
target references NSec. `IChatCryptographyProvider` is a trusted endpoint primitive
boundary; canonical encoding, associated data, signature inputs, context checks
and receive ordering remain common C#. Native constructors keep their previous
signatures and default to NSec. Browser callers explicitly supply a provider;
there is no mutable global provider or silent native fallback.

Client.Storage, Client.Http, Media, UI.Core and UI.Blazor share the same two
targets. NuGet's central transitive pinning otherwise promotes the native NSec
dependency into these packages even when the final application chooses Client's
browser asset. The package-consumer gate inspects the restored dependency graph
and executes the trimmed sample from NuGet references, not just project references.

`Skopka.Chat.Client.Browser` targets `net10.0-browser`, using locally vendored
`libsodium-wrappers-sumo`/`libsodium-sumo` 0.8.4 for X25519, Ed25519 and
XChaCha20-Poly1305. HKDF-SHA256 uses the .NET implementation, not a handwritten
primitive. The native provider continues using NSec for all four operations.
Synchronous primitive calls use Blazor WASM in-process interop after explicit
asynchronous module initialization. Blazor Server and prerender are rejected.

The browser-specific TFM is a supported [.NET target](https://learn.microsoft.com/en-us/dotnet/standard/frameworks).
The [upstream libsodium.js project](https://github.com/jedisct1/libsodium.js/releases/tag/0.8.4)
is the cryptographic dependency, not code invented in this repository. Its ISC
licenses, npm SHA-512 lockfile and vendored SHA-256 manifest are included. The only
vendoring rewrite changes a bare ESM import to a local relative import. No runtime
CDN or npm access occurs. WASM is embedded in the upstream ESM asset.

## Private-key compatibility

Native creation still exports `NSecPrivateKey`, and legacy records still import
with that format. The new portable endpoint-only container is:

`ASCII("Skopka.Chat.PrivateKey") || 00 || 01 || algorithm-byte || raw-32-bytes`.

Algorithm 1 is X25519 private bytes; 2 is an Ed25519 seed. Length, purpose and
version are exact. This is **not** a network DTO, encrypted export or recovery
protocol. Browser creation uses this container. Native providers accept either
legacy NSec or portable v1; browser providers deliberately reject legacy blobs.
`NSecChatCryptography.ExportPortablePrivateKey` is an explicit conversion preserving
the public key, not an automatic rewrite of a store. Cross-device migration/key
transfer UX remains outside scope. Never send these bytes through the BFF.

## Local encryption and scope

The origin owns IndexedDB `Skopka.Chat.Browser.v1`, schema 1. Explicit installation
creation atomically stores a random non-secret installation UUID once. Vaults use
the existing SHA-256 `(serviceId, userId, installationId)` partition. No sid,
cookie, access token or refresh token enters that namespace.

Unlock uses a **separate local vault phrase**, never the account password. Vault
v1 fixes Argon2id13 to 3 iterations, 64 MiB and a random 16-byte per-vault salt.
Its 32-byte result is imported into WebCrypto as a non-extractable AES-256-GCM key;
the raw result is cleared. An authenticated check distinguishes a successful
unlock from wrong-phrase/corrupt-vault failure without claiming to distinguish
those two causes. The phrase and AES key are not persisted. Future parameter or
schema changes require explicit migration. Phrase changes/recovery/export are
not implemented.

Identity metadata, private keys, canonical verified events, exact fan-out plans
and pre-network jobs are encrypted before IndexedDB writes with fresh 96-bit
nonces and 128-bit GCM tags. Local AAD binds a distinct domain/schema, scope,
record kind, record key, partition and revision. This local AAD is unrelated to
wire signatures; JSON is still never signed by the chat protocol.

Opaque namespace/record IDs, record kind, conversation index, sequence, salt,
nonce, ciphertext length and revision are visible locally. Values remain
encrypted. Random revision compare-and-swap inside an IndexedDB transaction
prevents lost updates; no plaintext hash is stored alongside ciphertext.

Web Locks serialize identity creation and delivery across cooperating tabs;
acquisition is bounded to ten seconds. Identity retains reserve → create-only
keys → finalize semantics. Transactions request strict durability and await
completion before releasing leases, including cancellation during writes.
The browser still controls actual disk durability. Unsupported/blocked storage,
quota, corrupt ciphertext, missing keys and revocation fail closed. Detectable
loss never recreates keys. Complete origin erasure cannot be detected from an
empty database alone and cannot recover the previous installation.

Records are limited to 16 MiB; pages enumerate at most 200 opaque keys and decrypt
one record at a time. History paging uses stable insertion cursors, like the
existing local journal, and returned items are ordered for display. Queue batches
are bounded to 100 jobs. Whole-history replay is not the browser-session default.
Event timestamps are stored in UTC so equivalent offsets compare as duplicates.

## Delivery and lifecycle

`BrowserChatSession` composes `ChatMultiDeviceSender` and `ChatSyncCoordinator`,
not another implementation of them. QueueAsync persists canonical content and its
stable content ID before directory/network work. Once a plan exists, retries use
exact stored envelopes/message IDs and accepted flags. A completed job is removed
only after durable local echo. A reload at any earlier stage retains either the
job or the exact plan. Partial success is not atomic recipient acceptance.

Receive remains authenticate/decrypt → atomic store/compare → idempotent apply →
ACK. Another tab may already have applied/acknowledged data, so hosts refresh the
visible history from the shared journal. Logout cancels/awaits the session, then
locks/disposes its vault handle without deleting records. No exactly-once display,
background execution, push or offline application-shell cache is promised.

## HTTP and UI boundaries

`IChatHttpRequestAuthorizer` adds a non-bearer path without changing the old
`IAccessTokenProvider` constructor. `BrowserBffAuthorization` requires same-origin
requests, browser same-origin credentials, no-store, redirect rejection and a
host CSRF provider on every unsafe attempt. `IBrowserChatAccountContextProvider`
supplies expected binding context from an independent trusted account endpoint.
The host keeps OAuth tokens on its backend and proxies only existing protocol
data. CSRF implementation, cookies, step-up enrollment, authentication, key-change
UX and production BFF policy remain host responsibilities.

UI.Blazor replaces the transitive ASP.NET Core framework reference with the
Components.Web package. The sample is standalone WASM, no server circuit or
prerender. Its separate loopback-only demo host has synthetic accounts and normal
cookie/antiforgery protection, references Server but never Client, and holds no
device private keys or message plaintext.

## Security ceiling

Local encryption protects locked stored values, not arbitrary same-origin JS,
XSS, malicious browser extensions, compromised browser/OS, rollback/deletion,
screenshots, unlocked process memory or substituted application code. A
non-extractable CryptoKey can still be **used** by malicious same-origin code.
Managed and JavaScript strings cannot reliably be zeroed. A weak phrase enables
offline guessing against a copied vault. CSP narrows injection exposure but does
not establish code authenticity. Protocol v1 still lacks ratchets, recipient
forward secrecy, key transparency and independent audit.

See [browser integration](../browser.md) for the tested matrix, exact commands,
recovery behavior and remaining host work.
