# ADR 0018: owner-hosted bot endpoints

- Status: accepted for the initial text-only integration layer
- Date: 2026-09-02
- Package line: 0.15.0

## Decision

A bot is a separate chat user/device, running the existing client cryptography in
its operator's infrastructure. A private HTTP gateway exposes that endpoint to
Python, JavaScript or other bot applications. First-party bots use exactly the
same implementation, with separate accounts, credentials and storage. There is
no managed hosting of third-party bots in this iteration.

The chat server does not reference the bot packages, receive plaintext/private
keys, or acquire a decryption endpoint. The gateway and the bot application DO
receive plaintext. Separating our own gateway into another process does not hide
messages from us as its operator. A bot profile identifies the bot user, operator,
hosting mode and disclosure revision. Changing any of these requires a new
revision and renewed consent; it never migrates private keys or history.

## Consent is a host boundary

The product host owns authenticated bot registration, trusted profile discovery,
the user's confirmation screen, persistent consent, revocation and server-side
admission rules. The SDK does not introduce an identity provider or let a bot
grant itself access. `IChatBotConsentProvider` is mandatory and has no allow-all
default. It returns a live, conversation/user/bot/revision-bound, expiring grant
with a unique grant ID. Never derive grants from messages, bot API requests,
untrusted headers or the bot's own configuration. Block/re-consent uses a new
grant ID. A host must enforce the same policy before accepting new bot traffic
on its chat server; client/gateway checks alone cannot constrain a malicious bot
operator who replaces their own executable.

The runtime checks consent before making updates available and before sending.
Persisted updates retain their original grant ID; a changed grant cannot reveal
an old queued update. Denied/unsupported events have durable suppression
tombstones, so replay cannot revive them. Grant lookup failures fail closed and
leave delivery pending. Revocation can race with an operation already authorized;
this is not distributed instantaneous revocation. Already accepted/delivered
ciphertext may reach an endpoint, and blocking cannot erase copies or external
side effects. With protocol v1, key transfer/compromise can expose historical
ciphertext: a change of operator should use a new bot identity, not key transfer.

## Packages and storage

- `Skopka.Chat.Bots`: client-only runtime, profiles, live consent contract and
  durable inbox contract. Reuses `ChatCryptoService` and `ChatMultiDeviceSender`.
- `Skopka.Chat.Bots.Sqlite`: endpoint-local inbox, suppression/acknowledgement
  tombstones and request-id reservations. Depends on Bots and the existing
  reviewed SQLite provider, never Server or PostgreSQL server persistence.
- `Skopka.Chat.Bots.AspNetCore`: private, policy-authorized HTTP gateway and
  Data Protection-backed create-only file identity adapter. The host must protect
  the Data Protection key ring independently of the encrypted identity files.

One identity/operator revision owns one storage namespace. SQLite is plaintext;
host ACLs/encryption/backup/quotas/retention remain mandatory. Do not share a
database, installation ID, certificate, key ring or device between bot tenants.
Use local filesystems supporting atomic rename and cooperative exclusive locks;
network filesystem locking is not certified.

## Delivery and API

Receive ordering is authenticate/decrypt/decode, durable inbox compare/insert,
then chat acknowledgement. Only bounded text/reply updates are exposed initially;
unsupported content is durably suppressed, not executed as a command. Logical
content deduplication is separate from recipient-specific delivery ID conflict
detection. Bot processing has its own explicit acknowledgement; polling alone
does not consume updates. The bot must durably deduplicate update IDs around its
business side effects. This is at-least-once, not exactly-once execution.

`sendMessage` requires a stable request UUID. A durable reservation binds it to
conversation, content and grant before network I/O. The existing durable fan-out
outbox then retries identical recipient envelopes. A conflicting reuse or an old
grant is rejected; partial acceptance is returned as incomplete, not success.
There is no create-conversation, grant-consent, arbitrary proxy URL, attachment
download, key export, plaintext server endpoint or webhook API.

HTTP uses a separate strict source-generated JSON profile, bounded requests and
responses, an explicitly named host authorization policy and generic failures.
Credentials belong in Authorization, never URLs. Hosts must use private network
binding, TLS across machines, rate limits and no body/token logging. Container
examples are source-built deployment skeletons: authentication, consent and
platform storage still require deliberate integration, not permissive defaults.

Envelope-v1, content-v1/v2/v3 and binding-v1 canonical bytes are unchanged. These
packages do not add Signal Protocol, forward secrecy, groups, webhooks, managed
third-party hosting, a bot marketplace or an end-user account/consent portal.
