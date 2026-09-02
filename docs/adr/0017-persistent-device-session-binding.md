# ADR 0017: persistent device identity and authenticated session binding

- Status: accepted; independent security review still required
- Date: 2026-09-02
- Coordinated package version: 0.14.0

## Trust boundaries

A device is an account/installation identity, not an OAuth session. The host supplies a stable service identifier, an authenticated chat UserId, an opaque non-secret session reference and an absolute session expiry. The host must authenticate every request normally, reject revoked/expired sessions as required by its policy, and never reuse a session reference for a different login. Access/refresh tokens never enter binding contracts, signed payloads or persistence. The service identifier is exact and configuration-owned, not a request Host header.

The client receives its expected context from the authenticated host account provider. It must not use a challenge response as the source of its expected account, service or session. Session references need not be GUIDs or a claim named sid. An integration example maps already validated sub/sid claims; the core knows neither JWT nor an identity provider.

Existing claims-based HTTP authorization remains the default. Explicit binding registration installs separate account-authenticated bootstrap and device-bound chat policies and an asynchronous request-identity resolver. The legacy IChatPrincipalMapper stays supported by the endpoint resolver's non-binding branch. Bound mode never falls back to a device claim or header. Legacy registration is disabled in bound mode: enrollment is the only registration path there.

## Local identity, concurrency and crashes

Identity scope is the exact service identifier, UserId and a random installation identifier supplied/persisted by the host with appropriate backup exclusions. It is not a hardware fingerprint. Session IDs and tokens never enter this scope or history/outbox paths.

Protected versioned metadata is reserved before private keys are written. An exclusive scope lease spans read/reserve/create/finalize. A pending record with valid matching keys is finalized after restart; a pending record without keys requires explicit recovery rather than replacement. An absent record, corrupt record, inaccessible storage, missing keys and a locally remembered server revocation are distinct outcomes. Neither load nor login creates a replacement. Explicit create uses a create-only key-store capability, never SaveAsync overwrite. Explicit adoption of legacy keys proves they load for the intended user and keeps the existing DeviceId, KeyId and public keys.

MAUI metadata uses injected ISecureStorage and an injected cross-process initialization lock. The provided file-lock adapter uses a host-protected installation directory, bounded acquisition and cancellation; all cooperating app processes must share it. SecureStorage has no native compare-and-swap, so bypassing the lease or deleting lock files while clients run is unsupported. Uncertain writes leave a recoverable reservation. OS storage completion is awaited before releasing a write lease even when cancellation arrives.

Logout disposes session services but does not erase identity. Explicit local forgetting deletes local key/metadata state and does not claim remote revocation. Server revocation is separate. Preserving DeviceId alone cannot recover keys or history; SQLite history/outbox use the persistent account/device partition and remain host-protected plaintext/metadata.

## Binding protocol v1

Protocol owns immutable bounded models and canonical encoding, not crypto. The signed bytes use the distinct ASCII domain `Skopka.Chat.DeviceBinding.v1` followed by a zero byte, then big-endian fields: version, operation (enroll/rebind), length-prefixed exact UTF-8 service and session reference, user/device/key/challenge UUIDs in network order, both 32-byte public keys, 32-byte random nonce, and signed 64-bit UTC millisecond timestamps (device registration, issue, challenge expiry, session expiry). No arbitrary JSON is signed. Separate golden vectors pin this format. Envelope-v1 and content-v1/v2/v3 remain unchanged.

Challenges use a CSPRNG nonce, unique ID and at most five minutes of validity, capped by session expiry. Enrollment timestamps and authoritative public records come from the server. Rebind uses exclusively the existing directory keys and rejects a revoked device; supplied keys cannot replace them. Before signing, Client compares operation, exact expected context, device/key IDs and both public keys, verifies the time bounds and loads the matching private keys through IDeviceKeyStore. Only a typed proof method is public; no general-purpose signing oracle is exposed.

Server defines an IDeviceProofVerifier contract. A small optional Skopka.Chat.Server.NSec adapter uses the already reviewed NSec version to verify Ed25519, adding no private-key or decryption API. This avoids Server -> Client and avoids imposing native crypto on every HTTP host. Native deployment compatibility and dependency auditing now also apply to hosts selecting this adapter.

## Persistence and atomicity

Challenges are pending, consumed or expired. Invalid proofs do not consume them; short expiry, byte limits and mandatory host rate-limit policies bound abuse. A repository transaction atomically registers an enrollment device (immutable keys), consumes the challenge and writes the session binding. PostgreSQL serializes a session with a transaction advisory lock, locks the device against revocation and locks the challenge before state comparison. Unique keys and conditional writes prevent duplicate effects. Revocation and binding resolution always consult current directory state; a stored binding is not an independent credential.

A session may bind to only one device/key. Multiple fresh sessions can bind to the same device. Binding expiry never exceeds the trusted session expiry and is not extended by a retry. Context comparison is exact, including the original session deadline. The host still validates access-token lifetime on every request. A database binding cannot extend an expired token or provide live OAuth-session revocation.

The completion request contains only challenge ID and signature. The server loads the exact stored payload and verifies against the stored challenge/device. An exact retry of an already successful request in the same still-valid context returns the original result, without a new effect, even after the short challenge deadline; it still requires a live binding and non-revoked device. A different signature/context, expired authorization, changed binding or subsequent revocation is rejected. Pending challenges cannot complete after expiry. Consumed challenges are retained only through their bounded session/result lifetime; bounded cleanup deletes expired rows in small batches. Deleting an idempotency record ends the retry window rather than re-executing the operation.

The repository contracts require these atomic properties; in-memory implementations are only for tests/samples. EF migrations are append-only. No existing device, conversation, envelope, history or outbox identifier is rewritten.

## Migration and limitations

An old DeviceId that happened to equal a previous sid can be explicitly adopted as permanent if its private keys remain available. New sessions prove ownership of it; old devices are never merged automatically. Lost keys produce recovery-required, not ownership inferred from an account login. New enrollment after key loss is a separate explicit user decision.

`ImportLegacyAsync` explicitly copies matching retained keys into the new scoped namespace with a persisted intent and create-only insertion, leaving the source untouched. Retrying an interrupted import requires the same retained keys, never generation. Legacy user-only MAUI storage has process-local serialization only; new identity uses the full scope and a cross-process lock. See the [integration guide](../device-identity.md) for stable session deadlines, compiled sub/sid host mapping and retention choices.

Binding proves signing-key possession once. It is not OAuth/JWT authentication, DPoP, mTLS, live session revocation, step-up authentication, key backup, a ratchet, forward secrecy or key transparency. A stolen authenticated session can enroll a new attacker-owned device unless the host requires additional authentication. A stolen bearer credential for an already bound session remains a bearer credential. These limits must appear in integration/security documentation and tests, not only here.

## Verification

Required gates include canonical vectors and mutation tests, independent initialization writers and crash points, key substitution/context/replay/revocation rejection, atomic PostgreSQL races and restart, hostile HTTP JSON and authentication boundaries, and a full E2EE logout/re-login delivery with unchanged identity/history/outbox. Complete format/build/test/fuzz/audit/pack and the platform CI matrix precede release; skipped local infrastructure/platform gates are reported explicitly. Publication requires separate explicit authorization and the protected release workflow.
