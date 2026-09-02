# Owner-hosted bots

The initial bot integration in `0.15.0` provides a .NET text-bot runtime, durable
inbox and private HTTP gateway. The `0.14.0` packages do not contain this layer.
It does not provide a managed bot platform or consent portal.

## Trust boundary and required host integration

Each bot uses its own user/device/private keys in its owner's infrastructure.
First-party bots use the same runtime with separate accounts/storage. The chat
server retains metadata/ciphertext only. **The gateway operator can read messages
addressed to the bot.** A separate first-party container does not hide plaintext
from Skopka. No managed hosting of third-party bots is included.

Before the first message, the product UI must show the trusted profile and request
explicit confirmation, for example:

> Бот «Поддержка». Оператор: Skopka. Сообщения в этом диалоге получает и
> обрабатывает Skopka. Это не даёт боту доступа к другим вашим диалогам.

Keep the bot/operator badge and Block action visible; encode display strings.
Operator/hosting changes require a new disclosure revision and renewed consent.
Prefer a **new bot identity** for operator changes; never transfer old private keys
or history. Blocking cannot erase data or external effects already received.

The mandatory `IChatBotConsentProvider` obtains **live grants from the trusted
application host**, not a bot-editable allow-list. The host must authenticate the
human user, verify conversation membership and the current profile revision,
persist explicit consent, and return a bounded-expiry grant scoped to that
user/conversation/bot/revision. Block/re-consent requires a new nonempty GrantId;
continuous renewal may retain it. Block, expiry or profile change must stop grants.
Lookup failure must throw, not fall back to stale permission.

**The host must also enforce this policy before accepting bot traffic on the chat
server**, including new conversations and sends in either direction. The generic
engine still treats bots as users: no bot ACL schema or consent portal is added
here. Gateway checks alone cannot constrain a malicious operator running a
modified client. Do not enable bot accounts until that host integration is ready.

Runtime checks occur on receive, update retrieval and send. Stored updates keep
their original GrantId; new consent cannot resurrect old queued updates. Denied
or unsupported deliveries retain suppression tombstones. Authorization can race
with an already in-flight operation; previously accepted ciphertext may reach the
addressed endpoint. Protocol-v1 has no forward secrecy; key transfer/compromise
can expose historical ciphertext. See [ADR 0018](adr/0018-self-hosted-bots.md).

## Composition and storage

| Package | Responsibility |
| --- | --- |
| `Skopka.Chat.Bots` | Client-only runtime, profiles, live consent and inbox contracts |
| `Skopka.Chat.Bots.Sqlite` | Durable updates, processing acknowledgements, suppression and send reservations |
| `Skopka.Chat.Bots.AspNetCore` | Private HTTP API and Data Protection-backed file identity |

The runtime reuses `ChatCryptoService` and `ChatMultiDeviceSender`; compose with
the existing HTTP client, `SqliteChatOutboxStore`, inbox and protected identity.
Never register it in the main chat server. Use one bot device/polling process per
deployment. Each bot/device/disclosure owns one database namespace; changing it
rejects reuse. Deliberately provision a new inbox/outbox namespace when changing
the deployment identity/disclosure.

The inbox contains plaintext text/metadata, not unsupported attachment keys. Ack
clears active text, but **not securely** from pages/WAL/backups/memory. Tombstones
and request reservations must outlive supported retries; no retention compactor
is provided. Use protected/encrypted filesystems, quotas, backup and capacity
monitoring; never delete the database to clear an error.

`ProtectedFileBotIdentityStore` uses scope/file-specific Data Protection purposes,
versioned leased metadata and create-only keys. Replacement via SaveAsync is
unsupported. Startup only loads identity; a new installation needs explicit
CreateAsync or sample `--initialize`. Lost/corrupt/unavailable/revoked identity
never regenerates automatically. Retain InstallationId across login sessions.

Protect/persist the Data Protection key ring **independently**, e.g. a mounted
certificate with its private key/password outside the data volume/image. Losing
that material loses identity. Use local filesystems with atomic rename/exclusive
locks; never delete live locks. Host ACLs, encryption, certificate rotation,
backup, power-loss durability and crash recovery require deployment verification.
Temporary identity files contain protected bytes only.

## Private API and delivery

Map with `app.MapSkopkaChatBotApi("YourBotPolicy")`. The policy authenticates the
caller and issues exactly one `skopka_chat_bot` claim matching the configured bot
account. Every route additionally verifies it. No URL parameter switches tenants.
Authorization holds credentials; query strings are rejected. Chat, consent and
gateway credentials are three separate secrets.

| Route | Request / response |
| --- | --- |
| `GET /bot/v1/getMe` | Bot/operator/hosting/revision disclosure |
| `POST /bot/v1/getUpdates` | `{"limit":20}` → `{"updates":[...]}` without consumption |
| `POST /bot/v1/acknowledgeUpdate` | `{"updateId":1}` → idempotent HTTP 204 |
| `POST /bot/v1/sendMessage` | `conversationId`, stable `requestId`, `text`, optional nullable `replyToContentId` |

Update fields: `updateId`, `conversationId`, `senderUserId`, `contentId`, `text`,
`replyToContentId`, `isForwarded`. Forwarding does not prove original authorship.
This is **text/replies only**: oversized text, reactions, edits, attachments and
own-account echoes are durably suppressed and chat-acknowledged, never commands.
Do not use this API for a workflow requiring those event types.

Limits: 16 KiB UTF-8 text, 20 updates, 128 KiB request, 2 MiB response, JSON depth16.
Case-sensitive JSON rejects duplicate/unknown members, missing/null required
fields, coercion, comments and trailing commas/data. Non-JSON → 415; oversized →
413; invalid → 400; wrong scope → 403; operation failure → generic 503. No remote
error details are returned. Responses use no-store. This is not Telegram API.

Receive: authenticate/decrypt/decode → durable inbox compare/insert → chat ack.
Bot processing ack is separate: persist business effects/idempotency first.
Several readers may see the same update. Delivery is at-least-once, not automatic
exactly-once business execution.

Persist and reuse the **same send request UUID/content** on timeout or
`succeeded:false`. Inspect `succeeded`, `acceptedCount`, `requiredCount`: HTTP200
alone does not mean every device accepted. Changed content/conversation/grant
under an old UUID fails. The outbox retries identical ciphertext/message IDs
after partial acceptance or restart; never mint a new UUID for every retry.

Use private/loopback networking, TLS across machines, rate/proxy limits and
payload-free health monitoring. No body/token logging, permissive CORS,
unprotected cookie authentication or public gateway ports. Webhooks, arbitrary
URLs, key export, file downloads, bot-initiated chats and managed external bots
are deferred.

See the [gateway/container sample](../samples/Skopka.Chat.BotGateway/README.md) and
[Python echo bot](../samples/Skopka.Chat.BotGateway/echo_bot.py).

```powershell
dotnet test --project tests/Skopka.Chat.Bots.Tests --configuration Release --no-restore
dotnet run --project tests/Skopka.Chat.FuzzTests --configuration Release -- --replay tests/Skopka.Chat.FuzzTests/corpus
```
