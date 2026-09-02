# ADR 0016: MAUI client orchestration and native conversation UI

- Status: Accepted
- Date: 2026-09-02
- Package version: 0.13.0

## Context

The transport-independent engine already provides E2EE envelopes, typed content, durable verified receive storage, media preparation and a headless UI. A reusable MAUI client also needs protected key adapters, account/lifecycle coordination, bounded history, restart-safe multi-device sending, safe file callbacks and a virtualized native conversation surface. Putting these concerns in Client, UI.Core or Server would introduce framework dependencies across existing trust boundaries.

## Decision

Add `Skopka.Chat.Client.Maui` over Client, Client.Storage and Media. It provides versioned SecureStorage key/trust adapters, serialized foreground lifecycle/session coordination and app-private bounded plaintext-file helpers. Missing/corrupt key records are explicit; no adapter silently regenerates device identity. It provides no identity provider, token persistence, push service, background-execution guarantee or encrypted database.

Add transport-neutral conversation/device directory APIs and a durable fan-out plan. One logical typed event retains one `ChatContentId`, while each peer device and active sibling device receives a distinct immutable protocol-v1 envelope/`MessageId`. The exact plan is committed before network I/O and retried unchanged until every recipient is accepted. Device directory authorization and active/revoked filtering are server responsibilities; key verification remains explicit endpoint policy.

Extend Client.Storage with bounded newest/previous history pages and a durable outbox contract. SQLite keeps event and outbox schemas independent and append-only. MAUI startup may disable full projection replay and instead restore pages through `ChatHistoryPager`; receive still follows authenticate/decrypt → atomic store → idempotent apply → acknowledge.

Add `Skopka.Chat.UI.Maui` over UI.Core. Its `CollectionView` uses compiled XAML bindings and stable identity-preserving diffs. Styling, strings and message/attachment/composer/empty templates are replaceable. File, forward and paging operations are host callbacks; the control neither opens remote content automatically nor references transport/server/persistence.

The coordinated release grows from sixteen to eighteen packages. Linux remains the core/DB/fuzz pack gate, Windows builds/tests/packs MAUI and verifies Android package consumption and package target assets, and macOS compiles iOS/Mac Catalyst plus a trimming smoke. Release publication combines the two artifacts and rejects anything other than eighteen matching packages and symbol packages.

The iOS gate compiles and natively links the unsigned ARM64 device app, not a simulator app: the reviewed NSec/libsodium dependency set contains `ios-arm64` but no `iossimulator-*` asset. All four platform gates remain required. Code signing and physical-device execution are host-owned; simulator support is not claimed without an additional native-dependency review.

## Consequences

- Protocol-v1 and encrypted-content v1/v2/v3 canonical bytes are unchanged.
- The server learns the existing conversation participants, public devices, recipient-specific ciphertext and delivery state, but not local plaintext, private keys, trust decisions or media keys.
- Endpoint storage now has additional high-value state: plaintext SQLite history, encrypted outbox records, identity keys/trust records and temporary plaintext media. The host owns OS protection, backup exclusions, retention and recovery UX.
- Foreground polling and lifecycle wake improve usability but do not replace push notifications or guarantee execution while suspended.
- MAUI dependencies remain optional and do not enter the core/server dependency graph or the infrastructure-free Linux package set.
