# ADR 0009: encrypted typed content events

Date: 2026-09-01
Status: accepted for package version 0.8.0 and encrypted content version 1.

## Context

Protocol v1 authenticates and encrypts bounded opaque bytes for one recipient device. Packages through `0.7.x` exposed those bytes and a UTF-8 text convenience API, but they had no interoperable representation for replies, forwarding or reactions. Reusing envelope `MessageId` as an application reference would also fail multi-device fan-out: every recipient-specific envelope must have a distinct idempotency ID because its recipient, key agreement and ciphertext differ.

The server must remain unable to distinguish text, replies, forwards and reactions. Existing protocol-v1 canonical bytes, HTTP DTOs, routes and PostgreSQL schema are already published and cannot be reinterpreted.

## Decision

`Skopka.Chat.Client` owns a separate application-content format carried entirely inside existing ciphertext. `ChatContentId` is a random logical event identifier reused when the same event is encrypted into several recipient envelopes; `MessageId` remains unique to each envelope.

Content version 1 is deterministic and bounded. It starts with ASCII `skopka.chat.content`, ASCII version `1`, a one-byte kind and an RFC 4122 big-endian content UUID. Text (`T`) then carries one ASCII flag digit, an optional reply content UUID and the remaining strict UTF-8 bytes. Reaction (`R`) carries a target content UUID, `+` or `-`, and a bounded strict UTF-8 rendering token. Unknown versions, kinds, flags, operations, empty identifiers, malformed Unicode, self-reference and oversize values are rejected without copying input into exception text.

`ChatTextContent.Forward` creates a new content ID, copies only the text, clears the reply reference and sets `IsForwarded`. It deliberately carries no source conversation, original author, original content ID or original signature. The marker means only that the current authenticated sender asserted a forward; it is not cryptographic proof of provenance.

Reactions are append-only encrypted add/remove events. `ChatConversationProjection` folds the latest event for `(target content ID, authenticated sender user ID, reaction token)` by authenticated sender timestamp and then content ID. Events can arrive before their target. Fan-out copies of the same authenticated logical event are duplicates even if envelope `MessageId` differs. Conflicting reuse of a content ID excludes that ID and is surfaced through `ChatProjectionApplyResult.Conflict`; content is never silently replaced.

The raw `EncryptTextAsync`, `EncryptAsync`, `DecryptAsync` and `ReceiveAsync` APIs retain their `0.1.x`–`0.7.x` behavior. Typed decoding is explicit through `EncryptContentAsync`, `DecryptContentAsync` and `ReceiveContentAsync`; legacy bytes are never guessed or silently reinterpreted.

## Security and compatibility consequences

- The server still stores only envelope routing metadata, ciphertext and delivery state. Reply targets, forward markers, reaction targets and reaction tokens remain encrypted, although ciphertext length is visible.
- The envelope signature authenticates the exact ciphertext and sender device. After decryption, the content parser enforces a separate version and bounds; this does not change protocol-v1 canonical signing or AEAD bytes.
- Reaction ordering uses sender-controlled authenticated time. A malicious sender can manipulate only that user's projected reaction state; it cannot forge another user. Host UI may surface implausible timestamps.
- Projection state and decrypted content are plaintext. Durable storage, retention, notification redaction and memory/process protection remain host responsibilities.
- The in-memory projection is a reducer, not an authoritative durable history store. Delivery remains at-least-once and `IReceivedMessageStore` remains the transactional local commit boundary.
- The existing coverage-guided harness instruments both the strict HTTP JSON boundary and this content decoder. Golden bytes, hostile truncation/version/Unicode cases and an opaque server round trip are release tests.
- Adding or reinterpreting content-v1 fields requires a new content version. Changing the outer envelope canonical representation still requires a new protocol version under ADR 0001.
