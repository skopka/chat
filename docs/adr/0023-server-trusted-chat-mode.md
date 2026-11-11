# ADR 0023: Server-trusted chat mode without end-to-end encryption

- Status: accepted 2026-11-11 for package version 0.20.0 (all proposals Q1–Q9 approved as written)
- Date: 2026-11-10
- First consumer: IqZone stage 11.5 (learner ↔ assigned teacher chat, Blazor Server islands)
- E2EE packages, protocol v1, content v1–v4, routes and their invariants: **unchanged**

## Context

Every published Skopka.Chat package is built around one invariant: the server never
receives plaintext or private keys. Messages are per-device envelopes, history lives
only on clients, `Skopka.Chat.UI.Core` depends on `Skopka.Chat.Client` cryptography, and
[docs/ui.md](../ui.md) forbids Blazor Server and prerendering of decrypted components.

Some hosts are legally or operationally required to read conversations. IqZone (minors,
assigned teachers, observing parents, managers handling complaints) decided on
2026-11-10 that its server stores and reads chat messages. It needs things the E2EE
design deliberately cannot provide:

- revoke history from one participant while another keeps it;
- show a manager the reported message with surrounding context, with every view logged;
- anonymise or erase an author on account deletion;
- know on the server that a message has been unread for 15 minutes;
- run under Blazor Server with prerender, on several replicas, with any number of
  browsers per user and no device enrollment, keys or vault.

Bending the E2EE packages to do this would weaken their guarantees for every other
consumer. The mode must therefore be a **separate package family** whose trust model is
explicit in its name, its README section and its threat model.

## Decision

### 1. Separate package family

Package names (agreed, Q6):

| Package | Responsibility | References |
| --- | --- | --- |
| `Skopka.Chat.ServerTrusted` | IDs, models, limits/options, host hooks (access policy, rate limit, moderation, attachment validation), the engine service, store contract, live-signal contract plus in-process hub, metadata-only event payloads v1, an in-memory store for tests/samples | `Skopka.Chat.Attachments` (blob contracts only) |
| `Skopka.Chat.ServerTrusted.PostgreSql` | Own `DbContext`, append-only migrations, configurable schema, store, own outbox table implementing `IChatServerEventOutbox`, hosted dispatcher, `LISTEN/NOTIFY` live bridge, retention/orphan cleanup worker | `ServerTrusted`, `Skopka.Chat.Server` (outbox contracts and dispatcher only), EF Core/Npgsql, `Microsoft.Extensions.Hosting.Abstractions` |
| `Skopka.Chat.ServerTrusted.Blazor` | Headless presentation state plus Razor components that work in `InteractiveServer` and legacy `ServerPrerendered` hosting | `ServerTrusted`, `Microsoft.AspNetCore.Components.Web` |

Deferred, optional and not required by IqZone: `Skopka.Chat.ServerTrusted.AspNetCore`
(HTTP/SignalR API and attachment endpoints) and a Kafka topic mapping for the new
event types.

Dependency rules enforced by new assembly-boundary tests (both directions):

- No ServerTrusted assembly references `Skopka.Chat.Client*`, `Skopka.Chat.UI.*`,
  `Skopka.Chat.Bots*`, `Skopka.Chat.Media*`, `Skopka.Chat.Persistence.PostgreSql`,
  `Skopka.Chat.Server.AspNetCore`, `Skopka.Chat.Transport.Http` or NSec/libsodium.
- `Skopka.Chat.ServerTrusted` and `.Blazor` do not reference `Skopka.Chat.Server`,
  EF Core, Npgsql or the ASP.NET Core shared framework.
  `Skopka.Chat.Server` is referenced only by `.PostgreSql`.
- No existing E2EE assembly references any ServerTrusted assembly.

Server-trusted identifiers are distinct value types (`TrustedConversationId`,
`TrustedMessageId`, `TrustedAttachmentId`; users are `Guid`). They are mapped to
Protocol `ConversationId`/`UserId` only at the `IAttachmentStore` boundary. No API
converts an E2EE conversation into a server-trusted one or the other way round; a
host that runs both modes keeps them in separate stores and must label the mode in
its UI. Server-trusted UI never shows lock icons, security codes or E2EE wording.

### 2. Conversations and participants

- Kinds: direct (exactly two writers at creation) and small group (bounded, default
  max 32 participants including observers, hard ceiling 256).
- Each conversation has a **unique host key** (bounded ASCII, ≤ 200 chars), e.g.
  `iqzone:mentor:{learnerId}:{teacherId}`. `GetOrCreateAsync(hostKey, …)` is
  idempotent under concurrency (unique index, retry-read on conflict). IqZone creates
  a new `TeacherAssignment` row on reassignment, so the pair key — not the assignment
  ID — reopens the same conversation and history.
- Status is `Open` or `Closed`; `Close`/`Reopen` are explicit, revisioned operations
  that keep the history. A closed conversation rejects send, edit, delete-own and
  attachment upload for everyone.
- Participants are users, never devices. Each participation stores:
  `UserId`, `Role` (`Writer` | `Observer`), `CanRead` flag, host label (bounded string
  such as `learner`, `teacher`, `parent`, snapshotted onto messages as author label),
  `JoinedAt`, optional `HistoryVisibleFrom` (host decides whether a newcomer sees older
  messages), `ReadThroughSequence`.
- The host owns membership: add, remove, change role, set `CanRead`. The library never
  derives membership from anything else.

### 3. Authorization: stored state plus a host policy on every operation

Two layers, both must allow:

1. **Stored state** (fast, authoritative for counters): participation exists,
   `CanRead`, role permits the operation, conversation status, message ownership.
2. **Host policy** `IServerTrustedChatAccessPolicy.AuthorizeAsync(actor, operation,
   context)` — asynchronous, called on every operation, may only deny further.
   Operations: `ReadConversation`, `ReadHistory`, `Send`, `EditOwn`, `DeleteOwn`,
   `MarkRead`, `UploadAttachment`, `DownloadAttachment`, `AttachHostReference`,
   `Report`, `ReadReportWindow`, `Hide`/`Unhide`.

There is no default "allow all" registration: the host must register a policy
explicitly, otherwise service resolution fails. Rejections are generic
(`NotFoundOrForbidden`), so the API does not reveal whether a conversation exists to a
non-participant.

The cross-conversation unread total is one bounded query over **stored** state only
(participations with `CanRead = true`); it does not invoke the policy per conversation.
Consequently a host that revokes reading dynamically must also clear `CanRead`, or the
menu counter can still show a number (never content) for that conversation. This is
open question Q1.

IqZone mapping: assignment ends → `Close` + teacher `CanRead = false` (learner keeps
reading); same teacher reassigned → `Reopen` + teacher `CanRead = true`; child-account
parent → `Observer`; personal-account parent → not a participant.

### 4. Messages

- Plain text, stored as submitted after validation: strict UTF-16 → UTF-8, no
  unpaired surrogates, no control characters except `\n` and `\t`, CRLF normalized to
  LF, length bounded by `MaxTextLength` (default 4000, hard ceiling 16000). Rendering is
  the host's (IqZone: its Markdown renderer with raw HTML disabled).
- **Per-conversation monotonic `Sequence`** (`bigint`, gap-free per conversation,
  allocated under the conversation row lock in the send transaction). It is the only
  ordering key for paging, "read through", unread counts and the report window.
  Timestamps are display data only.
- **Per-conversation `ChangeRevision`** (`bigint`, incremented in the same transaction
  by every mutation: create, edit, delete, hide, unhide, erase) stored on the conversation
  and on each changed message. Edits, deletes and hides do not advance `Sequence`, so
  live catch-up reads "messages changed since revision R", not only "after sequence S".
- Idempotency: `(ConversationId, AuthorUserId, ClientMessageId)` is unique. An exact
  retry returns the original message and creates no second outbox event; a retry with
  different content is a conflict.
- Host references: 0–4 structured `(Type, Id)` pairs per message (`Type` matches
  `[a-z][a-z0-9._-]{0,63}`, `Id` ≤ 128 chars), opaque to the library, each authorized by
  the `AttachHostReference` policy (e.g. IqZone `lesson` / `task` with Guid strings).
- Reply: optional `ReplyToMessageId`, must belong to the same conversation and be
  visible to the author; the preview shows a tombstone if the target is deleted,
  hidden or erased.
- Edit own: allowed while the conversation is open and the message is neither deleted
  nor hidden; sets `EditedAt`. Previous versions are retained only for moderation
  (Q2).
- Delete own (soft): participants see a tombstone; the text is either wiped at once or
  retained for moderation for a host-configured period (Q2). Attachments of a deleted
  message become unreadable to participants immediately.
- Author label and author ID are stored per message, so a later role change does not
  rewrite history.

### 5. Attachments

- Upload-then-reference. Upload validates limits (`MaxAttachmentBytes`, default 10 MiB,
  bounded by `AttachmentStorageLimits`), calls the host hook
  `IServerTrustedAttachmentValidator` (content sniffing — IqZone accepts PNG/JPEG/WebP
  by content), writes the blob through the existing `IAttachmentStore` and inserts a
  **pending** attachment row in the ServerTrusted schema bound to uploader and
  conversation. Send binds ≤ `MaxAttachmentsPerMessage` (default 3, ceiling 10) pending
  rows owned by the sender in the same conversation within the message transaction.
- The blob in `IAttachmentStore` is **plaintext** in this mode. The existing contract is
  byte-agnostic (exact length + SHA-256, immutable), but its member names say
  "ciphertext"; documentation will state the difference instead of renaming published
  API. File name, MIME type and dimensions live in the ServerTrusted tables, never in
  `StoredAttachment`, preserving the existing storage-metadata rule.
- Atomicity: the blob store is a separate `DbContext`/bucket, so there is no
  transaction with the message. Unbound pending rows older than `PendingAttachmentTtl`
  (default 1 h) are marked `Deleting`, their blobs deleted idempotently, then the rows
  removed by the cleanup worker. Blob `ExpiresAt` is not used because it is immutable.
- `Skopka.Chat.Attachments.PostgreSql` keeps its table `chat_attachments` in its own
  (default) schema; it is not inside the ServerTrusted schema (Q5). S3 hosts must
  enable bucket encryption, block public access and use a dedicated prefix/bucket.
- Download: `OpenAttachmentAsync(actor, attachmentId)` authorizes through the message
  (stored state + `DownloadAttachment` policy) and returns metadata plus a stream. In
  the first release the **host maps the HTTP endpoint** (IqZone already serves
  `/questions/images/{id}` this way); the guide will prescribe `nosniff`,
  `Content-Disposition`, `Cache-Control: private, no-store`, a `sandbox` CSP and no
  inline SVG/HTML. The Blazor component builds URLs through a host delegate.

### 6. History, read markers and unread counts

- `GetPageAsync(actor, conversation, beforeSequence | afterSequence, limit ≤ 100)`;
  results respect `HistoryVisibleFrom`, tombstones and moderation hiding.
- `MarkReadAsync(actor, conversation, throughSequence)` is monotonic
  (`GREATEST`), clamped to the last sequence, and emits an event only when it advances.
- Unread for one participation: messages with `Sequence > ReadThroughSequence`, not
  authored by the participant, not deleted/hidden. The per-conversation count is capped
  (default 999) to keep queries bounded; the total for the menu is the sum over the
  user's readable participations in one query, supported by
  `(conversation_id, sequence)` and participation indexes.
- `IsReadThroughAsync(user, conversation, sequence)` lets the host's 15-minute check
  ask the authoritative store instead of trusting an old event.

### 7. Live updates

- The contract is a **signal, not content**: `IServerTrustedChatLiveSignals` yields
  `(ConversationId, ChangeRevision)` for message created/edited/deleted/hidden/erased
  (read-marker signals carry the user ID and read-through sequence instead, which
  are not message content). A subscriber re-reads through the engine everything changed
  since its last seen revision, so every delivered item passes the same authorization
  as a page read, a lost signal is repaired by the next one or by catch-up, and
  revocation takes effect on the next read.
- In-process hub in the core package (`IAsyncEnumerable` with bounded channels per
  subscriber; drop-oldest coalescing is safe because signals carry only the latest
  sequence).
- Cross-replica: `.PostgreSql` ships a `LISTEN/NOTIFY` bridge on one dedicated
  connection per process. The payload is the conversation ID and change
  revision only, the channel name derives from the configured schema. Because notifications are lost
  while that connection reconnects, components also run a bounded catch-up re-read on
  reconnect and on a periodic interval (default 30 s). PgBouncer in transaction-pooling
  mode cannot carry `LISTEN`; the bridge needs a direct or session-pooled connection.
  Polling-only mode remains available when the bridge is disabled.

### 8. Events through the PostgreSQL outbox

- `.PostgreSql` owns its outbox table in the ServerTrusted schema with the same
  semantics as ADR 0022 (exact payload bytes, unique source tuple, leases with
  `FOR UPDATE SKIP LOCKED`) and implements the existing `IChatServerEventOutbox`, so the
  existing `ChatServerEventDispatcher` is reused unchanged. Events are written in the
  same `SaveChanges` transaction as the domain change; `chat_server_event_outbox` of the
  E2EE schema is not used.
- Its hosted worker constructs a dispatcher over this outbox and a host-registered
  publisher dedicated to this mode, so it does not collide with an E2EE outbox
  registered in the same container. IqZone implements the publisher by enqueueing into
  its own outbox/mail pipeline, deduplicating by `eventId`.
- Event types, version 1, partition key = conversation ID, metadata only:
  - `skopka.chat.server-trusted.message-created`: event/message/conversation IDs,
    sequence, author user ID, author label, attachment count, host-reference types,
    created timestamp;
  - `skopka.chat.server-trusted.read-marker-advanced`: conversation, user, read-through
    sequence, timestamp;
  - `skopka.chat.server-trusted.message-reported`: report, message and conversation
    IDs, reporter ID, reason code, timestamp.
  No text, file name, MIME, reply preview, host-reference ID or report comment.
- Delivery is at least once; consumers deduplicate by `eventId` and re-read current
  state (an event is not proof that a message is still unread or visible).

### 9. Moderation

- **Report**: any user who may currently `ReadConversation` (observers included) may
  report a visible message once (`(message, reporter)` unique; exact retry is
  idempotent). It carries a host-defined reason code and an optional bounded comment
  (≤ 1000 chars, stored, never in events).
- **Report window**: `ReadReportWindowAsync(actor, reportId)` for a user who is **not**
  a participant, allowed only by the `ReadReportWindow` policy for that specific
  report. It returns the reported message and up to N messages before and after it by
  sequence (`ReportWindowRadius`, host option, default 10, ceiling 50), with the target
  flagged `IsReportTarget`. Participants never use this path.
- **Access log**: every window read inserts an append-only row (actor, timestamp,
  conversation, report, returned sequence range) **in the same transaction** that reads
  the window. If the row cannot be written, nothing is returned. The log holds no
  message text. Hide/unhide is logged the same way.
- **Hide/unhide** by staff through the `Hide` policy hook; participants see a
  "hidden by moderator" tombstone; content is retained for moderation (Q2).
- There is no general staff read API. Standing access, if a host ever needs it, is an
  explicit `Observer` participation visible in the participant list.

What the window shows: current text; edited messages flagged with the prior versions
if retained (Q2); author-deleted messages flagged, with text only while retained;
hidden messages with text; erased content as an "erased" placeholder.

### 10. Erasure and retention

All operations are host-invoked, bounded and idempotent.

`ForgetUserAsync(userId, mode, surrogateUserId)` — the surrogate is required in both
modes and must not be an existing participant. In **both** modes it deletes the user's
read markers and participations, so no phantom participant remains in counters or
participant lists. Then:

- `EraseContent`: wipes text, prior versions and attachments of all the user's
  messages; a tombstone with the surrogate author keeps sequence continuity;
- `ReassignAuthor`: replaces only the author ID of the user's messages with the
  surrogate; content is kept. Text may still contain personal data — that is the host's
  judgement.

In both modes, reporter IDs in reports and actor IDs in the access log are replaced
with the surrogate rather than deleted, so the accountability trail survives (Q3).

Outbox rows are **not** rewritten: ADR 0022 publishes exact stored bytes and a row may
be under a lease. Events therefore keep the original user ID; completed rows are
deleted after the bounded outbox retention, and already-published events and the
host's copies are outside the library's reach. Hosts must apply their own erasure to
what their consumers stored.

Other operations:

- `DeleteConversationAsync(conversationId)`: hard delete including attachments; the access log is kept
  (it holds no content) until the host's own `PurgeAccessLogBeforeAsync(cutoff)` retention rule, so the record of
  who read a reported conversation survives the conversation (decided during implementation, consistent with Q3).
  Erasure also wipes host references of erased messages; tombstones never expose references
  (IqZone: deleting a learner removes their chats).
- `PurgeClosedBeforeAsync(cutoff)`: host retention rule for closed conversations.

### 11. Limits and abuse

Options with validated ceilings: text length, attachments per message, attachment
size, participants per conversation, page size, report-window radius, comment length.
`IServerTrustedChatRateLimiter.TryAcquireAsync(actor, operation)` is a host hook
(default: allow, explicitly documented); a rejection is a distinct generic result.

### 12. PostgreSQL

- `ServerTrustedChatDbContext` with a **configurable schema** (default
  `skopka_chat_trusted`, validated `^[a-z_][a-z0-9_]{0,47}$` so the derived NOTIFY channel name stays within 63 bytes), applied to the model,
  the migrations and the migrations-history table `__skopka_chat_trusted_migrations`
  inside that schema. The model-cache key includes the schema. Migrations are
  append-only, applied by the host's migration step; nothing migrates at startup.
- The store uses `IDbContextFactory<>` so it works outside a request scope (Blazor
  circuits, background workers).
- Concurrency covered by disposable-PostgreSQL Testcontainers tests: concurrent sends
  get unique gap-free sequences; every mutation advances `ChangeRevision` exactly once; exact retries create one message and one event;
  close vs. send; concurrent and out-of-order read markers keep the maximum; edit vs.
  delete; hide vs. edit; get-or-create by host key; report idempotency; window read and
  access-log atomicity; erasure during send; outbox lease expiry and redelivery.

### 13. UI

- New `Skopka.Chat.ServerTrusted.Blazor`; `Skopka.Chat.UI.Core` is **not** refactored.
  Decoupling UI.Core from Client would break published APIs and the "E2EE packages do
  not change" constraint. The alternative (a shared crypto-free presentation layer
  extracted from UI.Core) stays possible later and is listed as Q4.
- The root component takes only simple values (`Guid ConversationId`, `Guid UserId`)
  and resolves services from DI, creating a service scope per operation, matching
  IqZone's island convention. `UserId` is trustworthy only because it is rendered on the
  server into Data-Protection-protected component descriptors, which the server
  validates when the circuit starts (confirmed against `ServerComponentDeserializer` in
  ASP.NET Core; the host must share Data Protection keys across replicas, as IqZone
  already does). It must never come from a query string or
  a WebAssembly component, and every engine call is still authorized.
- Prerender renders the first page read-only. No mark-read, subscription or JavaScript
  runs during prerender; `PersistentComponentState` avoids the second query. Messages
  are marked read only while interactive and the document is visible (small JS
  module).
- Replaceable `MessageTemplate`, `MessageBodyTemplate` (host Markdown),
  `AttachmentTemplate`, `ComposerTemplate`, `EmptyTemplate`; localized strings in a
  `ServerTrustedChatStrings` record with English defaults and a Russian preset;
  CSS custom properties reuse the `--skopka-chat-*` names for consistent theming.
- An observers notice ("this conversation is visible to …") is built in, because IqZone
  must tell a child that a parent can read the chat. An unread badge component listens
  to the same live signals.
- Text is HTML-encoded by default; `MarkupString` in host templates is the host's
  responsibility.

## Threat model: the server reads messages

**Assets.** Message text, prior versions, report comments, attachments (plaintext
blobs, file names), participant lists and roles, read state, access log, events.

**What the server and its operators can see.** Everything above. Database, replica,
backup, blob-storage, log and memory-dump access equals full read access to all
conversations. Blazor Server circuits hold plaintext in server memory. This mode
provides **no confidentiality against the operator**, and its documentation must never
imply otherwise.

**What the library guarantees.**

- Authorization on every operation by stored state and host policy; deny when no
  policy is registered; generic errors that do not reveal existence.
- Live delivery never bypasses authorization: signals carry no content.
- Metadata-only events; no text, file names or comments in events, logs, exceptions or
  telemetry tags.
- Report-window reads only by explicit per-report host permission, bounded radius,
  logged atomically, fail-closed.
- Bounded inputs and strict text validation before persistence; idempotent retries.
- Erasure operations as specified, with their stated limits.

**What the host must protect.** Database and backup encryption and access control;
blob-storage encryption and private buckets; TLS; authentication and session
revocation; role checks inside the policy; CSRF on any HTTP endpoints; safe attachment
download headers and content validation/malware scanning; rendering of Markdown/HTML;
log and telemetry redaction; retention periods and legal basis (minors' data,
152-FZ/GDPR); review of the access log; protecting the access log itself from DBA
tampering, which the library cannot prevent; notification content (emails and pushes
must not include message text unless the host decides so).

**Explicit non-goals.** Protection against a malicious operator, DBA or compromised
server; tamper-evident logs; recall of content already displayed, emailed or exported;
end-to-end encryption.

## Consequences on acceptance

Not done in this change; each is part of the implementation work:

- `README.md` gains a clearly separated "Server-trusted mode (no E2EE)" section;
  `docs/threat-model.md` gains the section above; `docs/mvp-limitations.md` and
  `docs/security-self-review.md` gain their parts; a new guide `docs/server-trusted.md`.
- `AGENTS.md`: the product invariants are scoped to the E2EE packages and a
  ServerTrusted invariant block is added; the repository map lists the new packages.
  [docs/ui.md](../ui.md)'s Blazor Server/prerender prohibition is scoped to E2EE
  components.
- The coordinated set grows from 24 to 27 packages: `.github/workflows/ci.yml` and
  `.github/workflows/release.yml` package-count checks, `AGENTS.md`,
  [docs/releasing.md](../releasing.md) (count and publication order),
  [docs/development.md](../development.md) and
  [docs/protocol-compatibility.md](../protocol-compatibility.md).
- New test projects: `Skopka.Chat.ServerTrusted.Tests` (engine, policy, limits,
  boundary tests), `Skopka.Chat.ServerTrusted.PostgreSql.Tests` (required Testcontainers
  gate), `Skopka.Chat.ServerTrusted.Blazor.Tests` (HtmlRenderer and a test renderer: prerender, templates, strings,
  encoding); the core package consumer covers the new packages.
- Products documentation (Russian and English overview, guides, API index and a
  working Blazor Server example) is prepared before release, per
  [releasing.md](../releasing.md#products-documentation).

## Questions for the customer (resolved 2026-11-11: every proposal accepted)

- **Q1 — counter vs. dynamic policy.** Is it acceptable that the menu total counts
  stored `CanRead` participations only, so a host revoking access must also clear
  `CanRead`? (Alternative: the host supplies the conversation set for the total.)
- **Q2 — moderation retention.** Keep prior versions of edited messages and the text
  of author-deleted messages for moderation, and for how long (proposal: until the
  conversation is purged, visible only in the report window)? Or wipe at once?
- **Q3 — erasure of reporters and staff.** Surrogate IDs in reports and the access log
  (proposal) or deletion of those rows?
- **Q4 — UI layering.** New `ServerTrusted.Blazor` with its own headless state
  (proposal), or a refactor extracting a crypto-free layer from `UI.Core` (breaking
  change for E2EE consumers)?
- **Q5 — attachment schema.** Accept that PostgreSQL blobs stay in
  `Skopka.Chat.Attachments.PostgreSql`'s own table, or add an optional schema setting to
  that package (an additive change to an existing package)?
- **Q6 — package names.** `Skopka.Chat.ServerTrusted.*` (proposal: the name states the
  trust model, nobody mistakes it for E2EE), `Skopka.Chat.Hosted.*` (shorter but reads
  like "hosted service") or `Skopka.Chat.Managed.*`?
- **Q7 — dependency on `Skopka.Chat.Server`.** `.PostgreSql` references the E2EE server
  package only to reuse `IChatServerEventOutbox` and `ChatServerEventDispatcher`
  (proposal: accept; no E2EE package changes). Alternative: move the outbox contracts to a
  new shared package with type forwarding — a change to an existing package.
- **Q8 — cross-replica live updates.** `LISTEN/NOTIFY` bridge plus periodic catch-up
  (proposal; needs a direct or session-pooled connection), or polling only (simpler,
  seconds of delay, more queries)?
- **Q9 — release version.** At proposal time `0.19.0` was believed unpublished; it has since been confirmed on NuGet. Ship ServerTrusted in a
  separate `0.20.0` after `0.19.0` (proposal: keeps the outbox release independent), or
  add it to `0.19.0` so IqZone waits for one release only?
