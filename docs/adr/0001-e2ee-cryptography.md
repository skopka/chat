# ADR 0001: cryptography for the constrained E2EE MVP

Date: 2026-08-31  
Status: accepted for protocol version 1; security review required before production.

## Context

The engine targets .NET 10 and must not implement cryptographic primitives. The preferred outcome would be a maintained, consumable Signal Protocol or MLS implementation with a supported .NET API.

Current upstream options were reviewed on 2026-08-31:

| Option | Finding | Decision |
| --- | --- | --- |
| Signal `libsignal` | Official APIs are Java, Swift and TypeScript over Rust; no supported public .NET API. It implements Double Ratchet but adopting an unofficial native binding creates a substantial maintenance and assurance boundary. | Not selected. |
| OpenMLS | Maintained RFC 9420 implementation, but it is a Rust crate, primarily aimed at group messaging, with no supported .NET package/API. | Deferred for a future groups design. |
| Bouncy Castle | Broad, lower-level primitive surface; correct protocol composition and safe key handling would remain application responsibilities. | Not selected for this narrow MVP. |
| Sodium.Core | Maintained libsodium binding, but exposes a less strongly typed surface than NSec for this design. | Acceptable fallback, not selected. |
| `NSec.Cryptography` 26.4.0 | Actively maintained, MIT licensed, based on libsodium, compatible with .NET 10, and provides typed X25519, Ed25519, HKDF-SHA-256 and XChaCha20-Poly1305 APIs. | Selected. |

Sources: [official libsignal repository](https://github.com/signalapp/libsignal), [official OpenMLS repository](https://github.com/openmls/openmls), [NSec project](https://github.com/ektrah/nsec), [NSec NuGet package](https://www.nuget.org/packages/NSec.Cryptography/26.4.0), and [libsodium guidance](https://doc.libsodium.org/quickstart).

## Decision

Protocol version 1 uses a deliberately limited, one-envelope hybrid construction built only from NSec high-level primitives:

1. Each device has long-lived X25519 encryption and Ed25519 signing key pairs.
2. The sender generates a fresh ephemeral X25519 key pair for every recipient envelope.
3. X25519 agreement with the recipient's published encryption key produces input keying material.
4. HKDF-SHA-256 derives one XChaCha20-Poly1305 content key. Its context contains a domain separator and the canonical header, including both device identities and key identifiers.
5. XChaCha20-Poly1305 encrypts the plaintext with a 24-byte random nonce and authenticates the canonical header plus ephemeral public key as associated data.
6. Ed25519 signs a separate domain separator plus the complete canonical envelope excluding the signature itself. The recipient verifies the signature before decryption.
7. Private and derived keys are disposed/zeroed through NSec-managed key objects where supported; raw exported private-key byte arrays are cleared after import.

Canonical serialization is fixed-width and length-prefixed binary data in network byte order. Arbitrary JSON is never signed or used as AEAD associated data.

## Security properties and non-properties

The construction gives per-recipient confidentiality, integrity and sender-device authenticity when public keys are authentic and endpoints are uncompromised. Fresh ephemeral sender keys reduce accidental key/nonce reuse and limit exposure from later compromise of a sender's long-term encryption key.

It does **not** provide Signal's Double Ratchet, deniability, asynchronous pre-key sessions, replay state, forward secrecy against compromise of the recipient's long-term key, or post-compromise security. It has not undergone an independent cryptographic audit and must not be described as Signal-compatible or production-grade.

The server is not allowed to reference `Skopka.Chat.Client`, import private keys or expose any decryption API. Client fingerprint comparison is the v1 mitigation for malicious key substitution; it is not a replacement for key transparency.

## Consequences

- Native libsodium assets accompany the client package through NSec.
- Every recipient device requires a separate envelope.
- Protocol v1 ciphertext remains decryptable by anyone who later obtains the recipient's long-lived private encryption key and retained envelope.
- A future ratcheting design requires a new protocol version and migration path, not an in-place reinterpretation of v1 envelopes.
