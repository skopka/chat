# Threat model

Status: accepted for protocol version 1 (MVP), 2026-08-31.

## Scope and assets

Skopka.Chat v1 protects the contents and authenticity of one-to-one encrypted events sent between individually identified devices. Typed client content may represent text, replies, non-provenance forwards and reaction changes. Each device owns an X25519 encryption key and an Ed25519 signing key. Private keys remain on that device behind the `IDeviceKeyStore` abstraction. The server stores only public device data, routing metadata, ciphertext and delivery state.

The assets are message plaintext, device private keys, message integrity, sender authenticity, and the user's ability to notice that a device key changed by comparing a fingerprint/security code out of band.

## Trust boundaries and adversaries

- The network and server are untrusted for message confidentiality.
- A malicious or compromised server may read, drop, delay, duplicate, reorder or replace records and public-key directory responses.
- The cryptographic library and the client's local secure-storage implementation are trusted dependencies.
- Endpoints are trusted only while the device and its private keys remain uncompromised.
- Denial of service, traffic analysis, a malicious application process and compromised build/dependency infrastructure are outside the confidentiality guarantee.

## What E2EE protects

- XChaCha20-Poly1305 authenticates ciphertext and its canonical associated header. An altered recipient, conversation, message identifier, key identifier, timestamp, ephemeral key, nonce, ciphertext or tag fails authentication and/or signature verification.
- Ed25519 binds the complete canonical envelope to the sending device identity.
- A fresh ephemeral X25519 key is generated for every envelope; the server never receives that private key or the derived content key.
- A device other than the addressed recipient cannot decrypt an envelope with its own private key.

These properties do not make this implementation Signal-compatible or production-grade. Version 1 has no Double Ratchet, pre-key protocol, key transparency, forward secrecy against later compromise of a recipient's long-term key, or post-compromise security.

## Metadata visible to the server

The server sees user, device, conversation, message and key identifiers; public encryption and signing keys; device registration/revocation times; sender and recipient device identifiers; message creation, acceptance, expiry, delivery and acknowledgement times; protocol version; ciphertext length; ephemeral public key, nonce, authentication tag and signature; delivery frequency and IP/transport metadata supplied by an eventual host application.

The server can therefore infer who communicates with whom, when, how often, from which devices, and approximate plaintext length. Padding is not implemented in v1.

## Server compromise

A server compromise exposes all metadata and stored ciphertext. It enables deletion, withholding, replay, reordering and denial of service. It can register or substitute public keys if the host application's authorization is also compromised. Users who do not compare an authenticated security code may then encrypt future messages to an attacker-controlled device.

The compromise alone does not decrypt already stored ciphertext because private device keys are absent. The server is not a trust anchor for message authenticity at the recipient: recipients verify the sender signature. A compromised server can still present stale public keys or hide revocations; v1 has no append-only key-transparency log.

## Device theft or compromise

An attacker who can use a device or extract its private keys can impersonate that device and decrypt envelopes addressed to its current long-term encryption key, including previously recorded envelopes. Local plaintext and application notifications may also be exposed by the host application. Revocation prevents the server from accepting new envelopes for that device but cannot erase data already delivered or stop an attacker who bypasses the server.

The library deliberately supplies no filesystem key store. A host must implement `IDeviceKeyStore` with platform secure storage, access control, backup policy and deletion semantics appropriate to its platform. The in-memory implementation is for samples and tests only.

## Version 1 limitations and deferred work

- One-to-one text, reply, forward-marker and reaction events only; no edits, deletes or rich content.
- No Double Ratchet, forward secrecy guarantee, post-compromise security, deniability or Signal interoperability.
- No groups, attachments, push notifications, key backup/recovery or server federation.
- Multi-device users are representable, but the sender creates one envelope per recipient device; device fan-out policy belongs to the host application.
- No key transparency, certificate authority, remote attestation or mandatory out-of-band fingerprint verification.
- No traffic-shape protection, padding or sealed sender.
- No UI, SignalR/WebSocket push binding, access-token format, identity-provider integration or production infrastructure. An optional authenticated Minimal API polling transport is available.

Before production use, obtain an independent protocol and implementation audit, add a key-transparency design, validate deployment-specific host authentication and authorization, and replace this MVP protocol with a maintained ratcheting protocol where the target platforms permit it.

## Optional ASP.NET Core transport boundary

`Skopka.Chat.Server.AspNetCore` treats the host authentication handler as a new trust boundary. It requires an authenticated principal and maps exactly one user and one device claim before endpoint logic. It then binds registration, submission, delivery polling, acknowledgement and revocation to that identity. Supplying a forged principal, a permissive development handler, an incorrectly validated JWT, or claims copied from untrusted headers defeats this boundary.

The package does not log or persist access tokens and does not select a token format. TLS, issuer/audience/signature/lifetime validation, CORS, CSRF protection for cookie authentication, rate limits, proxy request limits and authorization-policy configuration belong to the host. HTTP authorization does not change the E2EE limitations above and does not hide routing metadata from the server.

## Typed content and local projection boundary

Typed content is decoded only after protocol-v1 signature verification and AEAD authentication. Its content ID, reply target, forward marker, reaction target and reaction token are inside ciphertext and are not available to the server. The strict content-v1 parser applies fixed discriminators, strict UTF-8 and byte bounds and returns a generic format exception for malformed authenticated bytes.

`IsForwarded` authenticates only the current sender's assertion that text was copied. It carries no original author or signature and must not be rendered as verified provenance. Reaction state is scoped to the authenticated sender user from the public device directory; a sender-controlled timestamp can reorder only that user's own reaction. A reused content ID with conflicting authenticated data is excluded by the in-memory projection instead of silently replacing plaintext.

The projection, its snapshots and any host persistence contain plaintext. The library does not encrypt local history, synchronize it between the user's devices, enforce retention or redact host UI/notifications. Applications must feed the projection only content returned by `ReceiveContentAsync` or equivalently verified protected local records.

## HTTP client boundary

`Skopka.Chat.Client.Http` trusts its host-provided `IAccessTokenProvider`, configured user/device IDs, base address and `HttpMessageHandler`. Its DI registration requires HTTPS, disables automatic redirects and adds a bearer token only to the current request. A custom handler that follows redirects, logs Authorization values, weakens TLS or rewrites destinations can still disclose tokens or metadata.

All supported operations are idempotent and transient retries are bounded. Each retry creates a new request and asks the provider for a current token; caller cancellation is not retried. Successful JSON responses are byte-bounded and must use a JSON media type. A shared case-sensitive profile rejects duplicate or unmapped fields, comments, trailing commas/data, string-to-number coercion, missing required values and nesting beyond 16 before protocol validation. Remote parser exceptions are replaced with a generic transport exception so property names or paths cannot be reflected into exception telemetry. A coverage-guided fuzz target exercises every DTO under the same bounds, while real Kestrel tests verify declared/chunked request rejection and disconnect cancellation. These gates do not replace deployment proxy limits or sustained fuzzing. Error bodies are not surfaced, but status codes and network timing remain observable to the host. The package neither parses token claims nor refreshes tokens, so a mismatch between configured identity and issued claims fails at the server and must be diagnosed by the host without logging the token.

## PostgreSQL and CI boundary

PostgreSQL persists public device keys, conversation/routing metadata, ciphertext, authentication data and delivery state. Database encryption, credentials, network isolation, retention, backups and operator access remain deployment responsibilities; E2EE does not hide metadata from database operators. Delivery is at-least-once: concurrent polling may expose the same ciphertext envelope to multiple workers until the first acknowledgement succeeds, so recipient-side transactional deduplication is part of the trust boundary.

The automated PostgreSQL service is a disposable correctness gate, not evidence of production availability or hardening. It verifies migrations, bounded insert/acknowledgement races, deterministic selection, TTL deletion and the complete authenticated HTTP round trip without granting the server any decryption capability.
