# Server-trusted chat (no end-to-end encryption)

> **The server reads every message.** The `Skopka.Chat.ServerTrusted*` packages are a separate mode for hosts
> that are required to see conversations (moderation of minors' chats, complaints, retention rules). They provide
> **no confidentiality against the operator, a database administrator, a backup reader or a compromised server.**
> Use the E2EE packages when that is the requirement. Decision record: [ADR 0023](adr/0023-server-trusted-chat-mode.md).

The E2EE packages, protocol v1, content v1–v4, routes and their invariants are untouched. No API converts an E2EE
conversation into a server-trusted one or back, and the two modes use separate identifier types and storage.

## Packages

| Package | Contents | References |
| --- | --- | --- |
| `Skopka.Chat.ServerTrusted` | Engine `ServerTrustedChatService`, models, limits, host hooks, store contract, in-process live signals, metadata-only events, `InMemoryServerTrustedChatStore` (tests/samples only) | `Skopka.Chat.Attachments` |
| `Skopka.Chat.ServerTrusted.PostgreSql` | `ServerTrustedChatDbContext` with a configurable schema, migration, `PostgreSqlServerTrustedChatStore`, outbox, event dispatcher, `LISTEN/NOTIFY` bridge, maintenance worker, DI registration | ServerTrusted, `Skopka.Chat.Server` (outbox contracts and dispatcher only), EF Core/Npgsql |
| `Skopka.Chat.ServerTrusted.Blazor` | Components for Blazor Server (`InteractiveServer` or legacy `ServerPrerendered`), headless view model, unread badge, strings, CSS variables | ServerTrusted, `Microsoft.AspNetCore.Components.Web` |

None of them references `Skopka.Chat.Client`, the E2EE UI or `Skopka.Chat.Persistence.PostgreSql`, and no E2EE
assembly references them; assembly-boundary tests enforce both directions.

## Model

- **Conversation**: direct (exactly two writers at creation) or small group (`MaxParticipants`, default 32, ceiling
  256). Each has a unique host key such as `iqzone:mentor:{learnerId}:{teacherId}`;
  `GetOrCreateConversationAsync` is idempotent under concurrency, so a reassignment reopens the same history.
  Status is `Open` or `Closed`; a closed conversation rejects send, edit, delete and upload for everyone.
- **Participant**: a user, never a device, so any number of browsers works without keys or enrollment. Stored
  `Role` (`Writer` or `Observer`), `CanRead`, a host label (snapshotted onto authored messages), history visibility
  for newcomers and a monotonic read marker.
- **Message**: plain text (strictly validated, CRLF normalized, `MaxTextLength` default 4000, ceiling 16000),
  optional reply, up to four structured host references `(type, id)`, attachments, per-conversation gap-free
  `Sequence` and a per-conversation `ChangeRevision` advanced by every create/edit/delete/hide/erase.
- **Idempotency**: `(conversation, author, ClientMessageId)` is unique; an exact retry returns the original message
  and writes no second event, a changed retry is a `Conflict`.

## Authorization

Every actor operation checks stored state first (participation, `CanRead`, role, status, ownership) and then asks
the host `IServerTrustedChatAccessPolicy` with a `ServerTrustedAccessContext`. The policy can only deny further.
There is no allow-all default: without a registered policy the engine cannot be resolved. Rejections are a generic
`NotFoundOrForbidden`, so a non-participant cannot learn whether a conversation exists.

Writes re-check stored state inside the database transaction under the conversation row lock, so a policy decision
cannot race a closure or a revocation into an accepted message.

The unread total for a menu counter is one bounded query over **stored** readable participations; it does not call
the policy per conversation. A host that revokes reading must therefore also clear `CanRead`:

```csharp
// Assignment ended: closed for writing; the learner keeps the history, the teacher loses it.
await chat.CloseAsync(conversation.Id);
await chat.SetParticipantAsync(conversation.Id,
    new TrustedParticipantSpec(teacherId, TrustedParticipantRole.Writer, "teacher", canRead: false));

// Same teacher assigned again: same conversation, same history.
await chat.ReopenAsync(conversation.Id);
await chat.SetParticipantAsync(conversation.Id,
    new TrustedParticipantSpec(teacherId, TrustedParticipantRole.Writer, "teacher"));
```

Host-management methods (`GetOrCreateConversationAsync`, `SetParticipantAsync`, `RemoveParticipantAsync`,
`CloseAsync`, `ReopenAsync`, `ForgetUserAsync`, `DeleteConversationAsync`, `PurgeClosedBeforeAsync`,
`GetAccessLogAsync`, `IsReadThroughAsync`) take no actor and must be called only from trusted host code.

## Registration

```csharp
using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.PostgreSql;

builder.Services.AddSingleton<IServerTrustedChatAccessPolicy, MentorChatPolicy>();     // host-owned, required
builder.Services.AddSingleton<IServerTrustedChatRateLimiter, MentorChatRateLimiter>(); // or UnlimitedServerTrustedChatRateLimiter
builder.Services.AddSkopkaChatServerTrustedPostgreSql(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("Platform")!;
    options.Schema = "skopka_chat_trusted";
    options.Chat.MaxTextLength = 5000;
    options.Chat.MaxAttachmentsPerMessage = 3;
    options.Chat.MaxAttachmentBytes = 5 * 1024 * 1024;
    options.Chat.ReportWindowRadius = 10;
});

// Optional: attachments through the existing blob abstractions (stored as plaintext in this mode).
builder.Services.AddSingleton<IAttachmentStore>(/* PostgreSqlAttachmentStore or S3AttachmentStore */);
builder.Services.AddSingleton<IServerTrustedAttachmentValidator, ImageSniffingValidator>();

// Optional: consume outbox events (email "unread for 15 minutes", push, counters).
builder.Services.AddSkopkaChatServerTrustedEventDispatch<PlatformChatEventPublisher>();
```

`ServerTrustedChatService` is registered as scoped; the store uses `IDbContextFactory<>` and works outside request
scopes. The registration adds two hosted services unless disabled: the `LISTEN/NOTIFY` bridge
(`EnableNotifyBridge`) and the maintenance worker (`EnableMaintenance`: expires unsent uploads, retries queued blob
deletions, deletes completed outbox rows after `Dispatch.PublishedRetention`).

## Schema and migrations

All tables, the outbox and the migrations-history table `__skopka_chat_trusted_migrations` live in the configured
schema (default `skopka_chat_trusted`, validated `^[a-z_][a-z0-9_]{0,47}$`). Nothing migrates at startup; apply the
append-only migration from the host's migration step:

```csharp
await using var context = new ServerTrustedChatDbContext(
    ServerTrustedChatDbContext.CreateOptions(connectionString, "skopka_chat_trusted"));
await context.Database.MigrateAsync();
```

`Skopka.Chat.Attachments.PostgreSql` keeps its own `chat_attachments` table outside this schema. With S3, enable
bucket encryption, block public access and use a dedicated bucket or prefix.

## Reading, writing and live updates

```csharp
var page = await chat.GetHistoryAsync(userId, conversationId, beforeSequence: null, limit: 50);
var sent = await chat.SendAsync(userId, conversationId, new TrustedSendRequest
{
    ClientMessageId = clientMessageId,                // reuse on retry of the same logical send
    Text = "…",
    References = [new HostReference("lesson", lessonId.ToString("D"))],
});
await chat.MarkReadAsync(userId, conversationId, page.Value!.Messages[^1].Sequence);
```

Live delivery is a **signal, not content**. `IServerTrustedChatLiveSignals.SubscribeAsync` yields
`ServerTrustedChatSignal(conversationId, kind, changeRevision)`; the subscriber calls `GetChangesAsync(userId,
conversationId, sinceRevision)` and therefore passes the same authorization as any read. A revoked participant
stops receiving content on the next read. The in-process hub uses bounded drop-oldest channels; a lost signal is
repaired by the next one or by the periodic catch-up the components run.

Across replicas the PostgreSQL store calls `pg_notify` inside each write transaction (delivered only on commit) and
the bridge republishes the content-free payload into every process's hub. The bridge needs one dedicated direct
or session-pooled connection per process; PgBouncer transaction pooling cannot carry `LISTEN`. Notifications sent
while the bridge reconnects are lost and repaired by catch-up.

## Attachments

Upload first, then reference. `UploadAttachmentAsync` buffers at most `MaxAttachmentBytes`, calls the host
`IServerTrustedAttachmentValidator` (sniff content, scan, decide the real content type), inserts a pending row,
writes the immutable blob through `IAttachmentStore` and marks it ready. `SendAsync` binds ready uploads of the same
sender and conversation inside the message transaction; an upload binds at most once. Unsent uploads expire after
`PendingAttachmentTtl` (default 1 hour). Blob deletions are queued in the database first and retried by
maintenance, so a storage outage cannot leave untracked plaintext.

The blob is **plaintext** here even though the shared contract names it "ciphertext"; file name and MIME type are
kept in the server-trusted tables, never in `StoredAttachment`. Downloads go through
`OpenAttachmentAsync(userId, attachmentId)`; the host maps the endpoint and must serve it with
`X-Content-Type-Options: nosniff`, `Content-Disposition: attachment` (or `inline` only for sniffed raster images),
`Cache-Control: private, no-store` and `Content-Security-Policy: sandbox`; never serve HTML or SVG inline.

```csharp
app.MapGet("/chat/attachments/{id:guid}", async (Guid id, HttpContext http, ServerTrustedChatService chat) =>
{
    var userId = /* host-resolved authenticated user */;
    var download = await chat.OpenAttachmentAsync(userId, new TrustedAttachmentId(id), http.RequestAborted);
    if (!download.IsSuccess) return Results.NotFound();
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.CacheControl = "private, no-store";
    http.Response.Headers.ContentSecurityPolicy = "sandbox";
    http.Response.ContentType = download.Value!.Info.ContentType;
    await download.Value.CopyToAsync(http.Response.Body, http.RequestAborted);
    return Results.Empty;
}).RequireAuthorization();
```

## Events

Written to the schema's `event_outbox` in the same transaction as the change and published by the unchanged
`ChatServerEventDispatcher` to the host `IServerTrustedChatEventPublisher` (at least once; deduplicate by
`EventId`). Partition key: conversation ID. Version 1 payloads contain metadata only:

| Type | Fields |
| --- | --- |
| `skopka.chat.server-trusted.message-created` | `eventId`, `messageId`, `conversationId`, `sequence`, `authorUserId`, `authorLabel`, `attachmentCount`, `referenceTypes`, `createdAt` |
| `skopka.chat.server-trusted.read-marker-advanced` | `eventId`, `conversationId`, `userId`, `readThroughSequence`, `at` |
| `skopka.chat.server-trusted.message-reported` | `eventId`, `reportId`, `messageId`, `conversationId`, `reporterUserId`, `reasonCode`, `createdAt` |

No text, file names, reference IDs or report comments. An event is not proof of current state: for "unread for 15
minutes" schedule a check and ask `IsReadThroughAsync(conversationId, userId, sequence)` at that time. Kafka topic
mapping for these types is not provided yet.

## Moderation

- **Report**: any reader, observers included, can report a visible message once (`ReportAsync`, host reason code,
  optional comment of at most 1000 characters that never enters events).
- **Report window**: `ReadReportWindowAsync(moderatorId, reportId)` for a **non-participant** allowed by the
  `ReadReportWindow` policy for that report. It returns the reported message flagged `IsReportTarget` and
  `ReportWindowRadius` messages before and after it (default 10, ceiling 50). Moderators see retained text of
  author-deleted messages, prior versions of edited messages and hidden messages; erased content is gone.
- **Access log**: each window read writes an append-only entry (actor, time, conversation, report, sequence range)
  in the same transaction; if it cannot be written nothing is returned. Opening an attachment from a window and
  hide/unhide are logged too. `GetAccessLogAsync` lets the host review it.
- **Hide/unhide**: `SetHiddenAsync` behind the `Hide` policy; participants see a "hidden by moderator" tombstone.
- There is no standing staff read API. Standing access, if ever needed, is an explicit observer participation
  visible to everyone in the participant list.

By default (`RetainDeletedAndEditedContentForModeration = true`) deleted text and prior versions are kept until the
conversation is purged or the author is erased, visible only through report windows. Set it to `false` to wipe them
at once.

## Erasure and retention

`ForgetUserAsync(userId, mode, surrogateUserId)` deletes the user's participations and read markers in both modes,
replaces their ID in reports and the access log with the surrogate, and then either erases their messages' text,
prior versions and attachments (`EraseContent`, tombstones keep sequence continuity) or only reassigns the author
(`ReassignAuthor`; the text may still contain personal data). `DeleteConversationAsync` hard-deletes a conversation
with its history, participants, reports and attachments; the access log (no message content) is kept for
accountability until the host calls `PurgeAccessLogBeforeAsync(cutoff)`. Erasure also wipes the host references of
erased messages, and tombstoned messages never expose references. `PurgeClosedBeforeAsync(cutoff)` implements a host retention
rule for closed conversations. Outbox rows are not rewritten (exact-bytes semantics, leases): already-written events
keep the original user ID, completed rows are deleted after the bounded retention, and published copies are the
host's to erase.

## Blazor Server

Unlike the E2EE components, `Skopka.Chat.ServerTrusted.Blazor` is designed for server rendering, including
prerender. Island parameters are simple values; services come from DI and every engine call runs in its own service
scope.

```cshtml
<component type="typeof(Skopka.Chat.ServerTrusted.Blazor.ServerTrustedChat)"
           render-mode="ServerPrerendered"
           param-ConversationId="@Model.ConversationId"
           param-UserId="@Model.UserId" />
<persist-component-state />
```

`UserId` is trustworthy only because server-rendered component descriptors are protected by ASP.NET Core Data
Protection and validated when the circuit starts; share Data Protection keys across replicas and never take the
user from a query string or a WebAssembly component. Every engine call is still authorized. During prerender the
component renders the first page read-only and persists it; it subscribes to signals, runs catch-up and marks
messages read only when interactive and only while the document is visible.

Replace `MessageBodyTemplate` to render Markdown with the host's sanitizing renderer; default rendering is
HTML-encoded text. Strings come from `ServerTrustedChatStrings` (English `Default`, Russian `Russian`) and colors
from the shared `--skopka-chat-*` CSS custom properties. The component shows who else can read the conversation
(observers), because a child must know that a parent can see it. `ServerTrustedChatUnreadBadge` renders the
stored-state unread total for a header.

Every engine call goes through `IServerTrustedChatOperationRunner`; the default `ScopedServerTrustedChatOperationRunner`
opens one service scope per operation, and a host may register its own. Templates receive
`ServerTrustedMessageContext`, `ServerTrustedAttachmentContext` and `ServerTrustedComposerContext`;
`ParticipantName` and `ReportReasonLabel` map host labels and ASCII reason codes to display text. The headless
`ServerTrustedChatViewModel` can be bound to other UI code.

Attachments and host references in the composer are opt-in: set `AllowAttachments` (with `AttachmentAccept` and an
optional `MaxAttachmentBytes` lower than the engine limit) to show a file input for writers of open conversations, and
pass `DraftReferenceType`/`DraftReferenceId` (simple values, validated) to attach, for example, the lesson the chat
was opened from. `ReferenceLabel`, `ReferenceUrl` and `ReferenceTemplate` control how references render; links are
emitted only for relative or `http(s)` URLs. Removing a pending upload only drops it from the draft; its blob is
deleted by maintenance after `PendingAttachmentTtl`.

Operational notes:

- Messages are marked read only after the JavaScript module
  `_content/Skopka.Chat.ServerTrusted.Blazor/serverTrustedChat.js` reports the document visible; if static web assets
  are not served, nothing is marked read.
- Attachment links are rendered only for relative or `http(s)` URLs returned by `AttachmentUrl`.
- Pass `TimeFormatter`; the default format uses the server's time zone.
- A reply whose target lies outside the loaded window shows a generic "unavailable" preview.
- Each unread badge subscribes to all signals in its process and re-queries the total at most once per second on
  any conversation's activity; on busy hosts prefer a host counter fed by outbox events.
- The timeline is not capped while a page stays open.
- Static web assets must be served (publish output, or the Development environment for `dotnet run`).

## Runnable sample

`samples/Skopka.Chat.ServerTrusted.Sample` is a Razor Pages host with legacy islands
(`<component render-mode="ServerPrerendered">`, `AddServerSideBlazor`, `MapBlazorHub`, `<persist-component-state />`),
a host wrapper component that sets delegates (islands accept only simple values), a host access policy, a content-sniffing
image validator, the download endpoint with safe headers and a moderation page. It uses the in-memory store by default or
PostgreSQL when `ConnectionStrings:ServerTrusted` is set. Its synthetic cookie sign-in exists only to switch demo users.

```powershell
dotnet run --project samples/Skopka.Chat.ServerTrusted.Sample
```

Run it with the `Development` profile (the included `launchSettings.json`): outside Development, unpublished builds do
not serve referenced static web assets, so `_framework/blazor.server.js`, the component CSS and the visibility module
return 404 and nothing is ever marked read. Published hosts serve them from the publish output.

Verified manually against the sample on 2026-11-11: prerendered HTML contains the first page and a Data-Protection
payload in `<persist-component-state />`; the circuit connects and loads the visibility module; a message sent by one
user appears live for the other; while the reader's document is hidden the message stays unread and the badge shows it,
and it is marked read as soon as the document becomes visible; a host reference from the island parameter is sent and
rendered; an image is uploaded through `InputFile`, rendered as a link and downloaded by an observer with `nosniff`,
`no-store` and `sandbox` headers while a non-participant gets 404; a report from the UI opens a bounded, flagged window
for the manager with one access-log entry, and a participant cannot open it.

## Threat model summary

| Asset | Who can read it |
| --- | --- |
| Text, prior versions, report comments, attachments, file names | Server, operators, DBAs, backups/replicas, blob storage, server memory (Blazor circuits) |
| Participants, roles, read state, access log | Same, plus authorized participants for their own conversation |
| Events | Outbox readers and the host consumers; metadata only |

The library guarantees per-operation authorization, deny without a policy, generic errors, content-free live
signals, metadata-only events and logs, bounded inputs, atomic and fail-closed moderation logging and the erasure
semantics above. The host must protect databases, backups and blob storage; TLS and authentication; role checks in
the policy; download headers and content validation; Markdown/HTML rendering; log and telemetry redaction;
retention and legal basis (minors' data, 152-FZ/GDPR); review of the access log, which a DBA could still tamper
with; and notification content (emails and pushes must not include message text unless the host decides so).
See [threat model](threat-model.md) and [limitations](mvp-limitations.md).

## Verification

```powershell
dotnet test --project tests/Skopka.Chat.ServerTrusted.Tests --configuration Release
dotnet test --project tests/Skopka.Chat.ServerTrusted.Blazor.Tests --configuration Release
$env:SKOPKA_CHAT_POSTGRES_TESTCONTAINERS = 'true'
$env:SKOPKA_CHAT_POSTGRES_REQUIRED = 'true'
dotnet test --project tests/Skopka.Chat.ServerTrusted.PostgreSql.Tests --configuration Release
```

The PostgreSQL gate covers migrations into a custom schema, concurrent sends (gap-free sequences, unique revisions,
one event each), concurrent exact retries, closure racing sends, concurrent read markers, edit/delete races,
concurrent get-or-create and reports, atomic fail-closed report-window logging, erasure in both modes, unread
capping and visibility, attachment binding races with queued blob deletion, outbox lease redelivery and the
NOTIFY bridge with the hosted dispatcher.
