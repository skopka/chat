# ADR 0002: authenticated ASP.NET Core transport boundary

Date: 2026-08-31
Status: accepted for the first HTTP transport package.

## Context

The core engine deliberately has no HTTP principal, token or authentication-scheme concept. A transport adapter must prevent a caller from registering, sending, polling, acknowledging or revoking as another device while preserving the existing rule that the server never accepts plaintext or private keys.

ASP.NET Core 10 Minimal APIs support applying authorization to a complete route group. Claims are identity assertions produced by a trusted authentication handler; they are not authorization decisions on their own. Sources: [Minimal API security](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/security?view=aspnetcore-10.0), [route groups](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/route-handlers?view=aspnetcore-10.0), and [claims mapping](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/claims?view=aspnetcore-10.0).

## Decision

Create `Skopka.Chat.Server.AspNetCore` as an optional package over `Skopka.Chat.Protocol` and `Skopka.Chat.Server`.

- Every endpoint is placed in one route group with `RequireAuthorization`; a host may supply a stricter named policy.
- The package does not select an authentication scheme, issue tokens, store tokens or add JWT validation. The host must configure a supported ASP.NET Core authentication handler and validate issuer, audience, lifetime, signature and transport security as appropriate.
- A default `IChatPrincipalMapper` requires exactly one GUID user claim and exactly one GUID device claim on an authenticated identity. Claim types are configurable. A host with non-GUID external subjects can replace the mapper after securely resolving them to internal `UserId` and `DeviceId` values.
- Registration derives `UserId` and registration time from the authenticated request/server clock. A client cannot assert either value in the body.
- Conversation creation derives one participant from the authenticated user.
- Submission requires the authenticated device to equal `SenderDeviceId`.
- Polling and acknowledgement derive the recipient device from the authenticated principal; the client cannot poll a supplied device ID.
- Revocation first loads the target public device and requires the same authenticated user owner.
- DTOs contain public keys, identifiers, ciphertext and delivery metadata only. No endpoint accepts plaintext, private keys or access tokens in a request body.
- Known validation and state failures return bounded generic problem responses without echoing request bodies, ciphertext, keys or token values.
- Protocol size validation remains authoritative. The host must also configure a request-body limit at the reverse proxy/server boundary because JSON parsing occurs before endpoint logic.

## Consequences

The package is compatible with JWT bearer, cookies, mTLS-derived claims or a custom authentication scheme without depending on a specific identity provider. This flexibility means a host can still be insecure if it installs a permissive authentication handler or maps untrusted headers directly to claims; this is explicitly outside the package guarantee.

The API prevents cross-device actions after authentication but does not add key transparency, rate limiting, anti-CSRF configuration for cookie schemes, TLS termination, CORS policy or token revocation. Those remain mandatory host/deployment responsibilities.

Starting with package version 0.3.0, the unchanged routes and JSON DTOs live in `Skopka.Chat.Transport.Http` so the HTTP client and server adapter share a contract without referencing each other. See ADR 0003.
