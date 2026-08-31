# Protocol and package compatibility

## Versioning

NuGet packages use SemVer. The initial package version was `0.1.0`; public APIs and the wire format may evolve before `1.0.0`, but already published protocol versions are never silently reinterpreted.

`ProtocolVersions.V1` is encoded into every envelope. A v1 implementation rejects unknown versions before storage or cryptographic work. A future wire change must use a new protocol version, a separate canonical domain separator and new golden vectors.

## Canonical binary format

Protocol v1 uses fixed field order, signed lengths, network byte order for integers and RFC 4122 big-endian UUID bytes. Strings are limited to ASCII domain separators; arbitrary JSON is not signed. The signed bytes cover:

1. protocol version;
2. message, conversation, sender-device and recipient-device IDs;
3. sender signing-key and recipient encryption-key IDs;
4. sent/expiry timestamps;
5. ephemeral X25519 public key and XChaCha20-Poly1305 nonce;
6. ciphertext and authentication tag.

AEAD associated data covers the canonical header and ephemeral public key. Tests pin a complete deterministic golden envelope vector.

## Compatibility table

| Package range | Emitted protocol | Accepted protocol | Notes |
| --- | --- | --- | --- |
| `0.1.x` | v1 | v1 | Personal chat MVP; no ratchet. |
| `0.2.x` | v1 | v1 | Adds the optional authenticated ASP.NET Core transport; canonical envelope bytes are unchanged. |

Patch releases must not change canonical v1 output. Minor releases may add optional APIs or support a new protocol version, but must retain v1 decoding/validation if they claim compatibility. Removal of a protocol version or breaking public API requires a major package version.
