# Private bot gateway sample

This is a composition example, not a ready-to-enable production bot platform.
First implement trusted profiles, the human consent UI, persistent grants and
server-side admission described in [the bot guide](../../docs/bots.md). There is
no permissive fallback or bot-controlled grant endpoint here.

The chat host must support binding-v1. Initialize explicitly, enroll once, then
rebind the same identity on subsequent sessions. Tokens are read from mounted
files on each request; the sample does not refresh Auth tokens itself.

Configure through environment names such as `Bot__Name`, or a protected external
ASP.NET configuration provider:

| `Bot` setting | Meaning |
| --- | --- |
| `ServiceId`, `UserId`, `InstallationId` | Stable service, bot account UUID and retained random installation UUID |
| `Name`, `OperatorId`, `OperatorName` | Trusted disclosure |
| `Hosting`, `Revision` | `OwnerHosted` / `FirstParty` and host-issued revision UUID |
| `DataDirectory` | Protected writable persistent directory (`/data` in container) |
| `CertificateFile`, `CertificatePasswordFile` | Read-only mounted PFX/password protecting the key ring |
| `ChatBaseAddress`, `ChatTokenFile` | HTTPS chat host and bot account credential file |
| `SessionReference`, `SessionExpiresAt` | Authenticated session reference/ISO-8601 deadline; never inferred from a challenge |
| `BindingOperation` | Explicit first `Enrollment`, then `Rebind` |
| `ConsentBaseAddress`, `ConsentTokenFile` | Trusted HTTPS consent API with trailing slash and read-only bot-scoped credential |
| `GatewayTokenFile` | Independent random base64url token, at least 32 random bytes, for the bot application |

Keep secret files outside the repository/build context and mount read-only.
Never share identity, certificate, key ring or databases between tenants.
Provision encrypted storage/permissions before starting the unprivileged image.
Loss of protection material requires recovery, not automatic reinitialization.

With identity/profile/storage configured, initialize once:

```powershell
dotnet run --project samples/Skopka.Chat.BotGateway --configuration Release -- --initialize
```

This prints only the public DeviceId and exits without listening/registering.
Configure the authenticated session and explicit Enrollment, then start:

```powershell
dotnet run --project samples/Skopka.Chat.BotGateway --configuration Release -- --urls http://127.0.0.1:8080
```

Use Rebind and fresh trusted session metadata for later logins. Do not automate
`--initialize` as recovery. Normal startup never creates a missing identity.

## Consent host endpoint

The sample calls `GET {ConsentBaseAddress}consents/{conversationUuid}` with its
separate read-only credential. The host authenticates the bot and verifies human
consent/membership/disclosure/blocking. Return 404 to deny, or bounded JSON with
UUID fields `grantId`, `conversationId`, `userId`, `botUserId`, `profileRevision`
and an ISO-8601 `expiresAt`. All scope fields must describe current trusted state.
Never return a grant for every requested UUID. Other statuses, redirects,
oversized/malformed JSON and timeouts fail closed. The host must enforce the same
policy on chat-server admission; this endpoint alone is insufficient.

## Container and Python

Build from the repository root:

```powershell
docker build -f samples/Skopka.Chat.BotGateway/Dockerfile -t skopka-chat-bot-gateway:local .
docker build --target checks -f samples/Skopka.Chat.BotGateway/Dockerfile -t skopka-chat-bot-checks:local .
```

Pin reviewed image digests via `SDK_IMAGE` / `RUNTIME_IMAGE` for deployment;
floating sample tags are not a supply-chain policy. Use read-only root, bounded
`/tmp` tmpfs, CPU/memory/disk quotas, protected writable `/data`, read-only secret
mounts and loopback-only host port 8080 (or no host port/private bot network).
Use TLS across machines. Never mount gateway routes on the public chat server.
No image is published by this change.

Default logging is disabled; polling failure emits a generic stderr signal.
Add host counters/health alerts without bodies, keys, tokens or remote exception
details. Verify certificate rotation, token renewal, local filesystem permissions
and power-loss recovery on the actual deployment platform.

Set `BOT_GATEWAY_TOKEN_FILE` and optionally `BOT_GATEWAY_URL` (default loopback):

```powershell
python samples/Skopka.Chat.BotGateway/echo_bot.py
```

The standard-library example echoes text as a reply, uses a stable request UUID
per bot/revision/update and acknowledges only complete recipient fan-out. It does
not log bodies/tokens. Non-idempotent business effects need your own durable
transaction/idempotency store before acknowledgement.
