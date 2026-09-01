# ADR 0013: Encrypted message edits as immutable content events

- Status: Accepted
- Date: 2026-09-01
- Package version: 0.12.0
- Encrypted content version: 3

## Context

Protocol v1 and the server store immutable recipient-specific ciphertext envelopes. Replacing an existing envelope would weaken message-ID idempotency, require the server to understand an encrypted logical target and make concurrent/offline edits ambiguous. Content v1 text/reaction bytes and content v2 attachment manifests are already immutable compatibility formats.

An edit must remain end-to-end encrypted, work when delivered before its target, support another authenticated device belonging to the same author and never allow one participant to rewrite another participant's projected text.

## Decision

### Content v3 format

`ChatEditContent` is a new immutable event. It encodes with the existing `skopka.chat.content` prefix, ASCII version `3`, kind `E` and these canonical fields:

1. 16-byte RFC 4122 big-endian edit-event `ContentId`;
2. 16-byte big-endian target `ChatContentId`;
3. one field byte: `T` for text or `C` for attachment caption;
4. one value byte: `1` when strict UTF-8 replacement bytes follow, or `0` only to clear an attachment caption;
5. the remaining bytes as the replacement value, with no length prefix or trailing fields.

Text replacement is required, non-whitespace and bounded so the complete payload fits `ProtocolLimits.MaxPlaintextBytes`. A present caption is non-empty and bounded by `MaxAttachmentCaptionUtf8Bytes`; a null caption is the single canonical clear operation. Empty IDs, self-targets, unknown fields/value flags, malformed UTF-8 and oversize input are rejected through the generic `ChatContentFormatException`. A golden vector and committed fuzz seed pin the format.

Content v1 and v2 encoders/decoders are unchanged. Protocol-v1 envelope signing and AEAD associated data are unchanged because content v3 remains authenticated plaintext inside the existing ciphertext field.

### Projection and author rule

`ChatConversationProjection` retains edit events even when their target has not arrived. For each `(target content ID, authenticated sender user ID, field)` it selects the greatest authenticated envelope `SentAt`, breaking ties by edit `ContentId`. The selected edit is applied only when:

- the target is a projected text or attachment item in the same conversation;
- the edit sender user equals the original target sender user;
- `Text` targets text or `AttachmentCaption` targets an attachment.

The signing device may differ, allowing the same authenticated user to edit from another registered device. Other-user and wrong-field edits remain non-visible events. The server cannot enforce this rule because target, field and replacement are encrypted; recipients enforce it after signature verification and decryption.

An applied edit changes only projected text/caption and exposes `IsEdited` plus the selected edit's `EditedAt`. It does not change the original item `ContentId`, envelope `DeliveryMessageId`, author, `SentAt`, reply/forward metadata, attachment manifest or reactions. Fan-out copies are duplicates. Conflicting reuse of an edit event ID excludes that event and rebuilds the projection without silently retaining its plaintext.

### UI behavior

`ChatViewModel.BeginEdit` accepts only projected content owned by `CurrentUserId`. The composer sends a new `ChatEditContent` through the existing host-owned `IChatContentSender`, validates the authenticated local echo and restores the unsent draft/reply state that preceded edit mode. Expected failures retain the edit draft and only a generic error marker. Text messages and attachment captions are editable; deletion, attachment replacement and history/version display are separate features.

The default Blazor components show Edit only on own items, render an encoded “edited” marker and reuse the replaceable composer/strings/templates. Custom UI remains free to bind the same headless state.

## Security and compatibility consequences

- The server and attachment provider still receive no target ID, field or replacement plaintext, though ciphertext length/timing expose that another envelope was sent.
- Sender time is authenticated but sender-controlled. A malicious author can order only that author's competing edits; deterministic projection does not make timestamps trusted wall-clock evidence.
- A participant may send unauthorized or wrong-target edit ciphertext that recipients ignore, so host rate limits and quotas still matter.
- Local projection/history now retains original event plaintext plus edit plaintext. Protected persistence, retention, telemetry redaction and notification handling remain host responsibilities.
- Clients through `0.11.x` reject content v3 in typed APIs. Deployments that require edit convergence must upgrade all projecting clients; raw decrypt APIs continue to return opaque authenticated bytes.
- Message deletion remains deferred and must use a separately reviewed immutable event rather than overloading an empty text edit.

## Alternatives considered

- Replace the original server row: rejected because it breaks immutable delivery/idempotency and would expose edit relationships or trust the server with conflict resolution.
- Reuse content v1 flags: rejected because published content-v1 bytes cannot be reinterpreted.
- Authorize by original device only: rejected because authenticated multi-device users must be able to edit from another current device; the stable authorization identity is the sender user.
- Use arrival order: rejected because polling, retries and recipient-device fan-out reorder events.
- Treat empty text as deletion: rejected because deletion and its retention semantics require a separate decision.
