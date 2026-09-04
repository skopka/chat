# ADR 0021: Small groups and encrypted structured mentions

- Status: accepted for package version 0.18.0
- Date: 2026-09-03
- Outer protocol: v1 unchanged
- Encrypted content: v4 for text carrying mentions

## Context

The published engine supported one personal participant pair. Its per-recipient
envelope construction can safely serve small groups without inventing a shared
group cipher: one logical event is encrypted independently to every active device.
The server must authorize the current participant set but must not learn message
text or whether it contains `@user` or `@all`.

A literal parser alone is ambiguous because display aliases are product-owned and
may change. Mention intent therefore needs stable user IDs inside authenticated
ciphertext. Published content-v1 text bytes cannot be extended in place.

## Decision

### Server-visible group metadata

- A group has an opaque conversation ID, UTF-8 title, permanent initial owner,
  monotonic revision and 1–64 current members.
- Roles are Member, Administrator and Owner. Administrators may rename and manage
  ordinary members. Only the owner may assign administrators. Ownership transfer
  and owner removal are deferred.
- Title, member IDs, roles, join times and revision are visible to the server.
  Message bodies, reply targets and mentions remain encrypted.
- Metadata changes use expected-revision writes. PostgreSQL replaces the member
  snapshot in the same transaction as the revision update.
- A sender creates one protocol-v1 envelope for every active peer or sibling
  device returned by the authenticated directory. The current implementation is
  bounded to 100 recipient envelopes per logical send.
- Adding a member does not grant old history automatically. Removing a member
  stops new envelope submission to that user, but cannot erase content or keys
  already delivered. Existing encrypted-history backup remains account-scoped.

This is a small-group MVP, not MLS. It adds no group epoch key, forward secrecy,
post-compromise security, membership transcript or cryptographic server exclusion.
A malicious server can still manipulate directory membership/public keys; key-change
display and out-of-band device verification remain required.

### Encrypted mention content v4

Unmentioned `ChatTextContent` continues to emit byte-identical content v1. A text
event with at least one mention emits:

1. ASCII `skopka.chat.content`;
2. ASCII version `4`, kind `T`;
3. 16-byte RFC 4122 big-endian content ID;
4. the existing ASCII text flags `0`–`3` and optional reply content ID;
5. unsigned big-endian 16-bit mention count, from 1 through 64;
6. canonical distinct targets, sorted by kind and user ID: `U` plus a 16-byte
   user ID, or `*` for Everyone;
7. the remaining strict UTF-8 visible text.

The format intentionally stores targets rather than text ranges. The host owns
display aliases/autocomplete and inserts visible `@name` text. Forwarding copies
only visible text and drops mention targets, preventing an accidental second ping.
Unknown kinds, empty IDs, duplicates, malformed UTF-8, noncanonical ordering and
oversize payloads fail with the generic content-format error. Golden and fuzz seeds
pin the exact format.

Direct mentions are effective only for current members. An Everyone mention is
effective only when the authenticated sender is currently Owner or Administrator;
recipients evaluate that policy using authenticated group-directory metadata after
envelope verification. The ciphertext-only server cannot inspect or enforce it.
Concurrent role changes may require a directory refresh before all clients converge.

## Consequences

- Protocol-v1 envelope signing, AEAD data and personal-conversation routes remain
  unchanged. Clients through 0.17.x reject content v4 in typed APIs but can still
  authenticate/decrypt it as opaque bytes through raw APIs.
- Group HTTP routes and an append-only PostgreSQL migration are opt-in through an
  `IGroupConversationRepository`. Hosts that do not register it retain personal chat.
- Local journals/backups already store canonical typed bytes and can carry content
  v4 without a schema reinterpretation; old readers still cannot project it.
- Push notifications, unread counters, alias discovery, ownership transfer,
  member-change system messages, old-history sharing and large-group/MLS semantics
  remain product or future-protocol work.
