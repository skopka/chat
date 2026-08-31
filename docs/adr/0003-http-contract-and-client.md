# ADR 0003: shared HTTP contract and authenticated client

Date: 2026-09-01
Status: accepted for package version 0.3.0.

## Context

The ASP.NET Core package initially owned its JSON DTOs. A reusable HTTP client must not reference a server-framework assembly, duplicate a drifting wire contract, cache bearer tokens accidentally or deserialize unbounded server responses. `IChatTransport` also accepts recipient device IDs even though the authenticated HTTP API derives the recipient from claims.

Current .NET guidance supports short-lived typed clients created by `IHttpClientFactory` and handler pooling for connection reuse and DNS changes. Sources: [IHttpClientFactory guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory), [HttpClient lifetime guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines), and [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation).

## Decision

- Introduce `Skopka.Chat.Transport.Http`, referencing Protocol only. It owns versioned route constants, bounded HTTP limits, public JSON DTOs, protocol mappings and source-generated `System.Text.Json` metadata.
- `Skopka.Chat.Server.AspNetCore` and `Skopka.Chat.Client.Http` reference the shared contract but never reference each other.
- `SkopkaChatHttpClient` implements `IChatTransport` and also exposes device registration/revocation and personal-conversation creation.
- Every client instance is configured for one expected user ID and device ID. Registration, envelope sender, polling recipient and acknowledgement recipient must match that configuration before network I/O.
- `IAccessTokenProvider` is called before every attempt. The token is placed only on that `HttpRequestMessage`; it is never written to default headers, parsed, persisted or logged by the package. `ChatAccessToken.ToString()` is redacted and an optional trusted expiry is checked with bounded skew.
- The registered handler disables automatic redirects, requires HTTPS by default and uses a two-minute pooled connection lifetime. A direct `HttpClient` supplied by a consumer must also disable redirects.
- All operations are idempotent by protocol identifier or semantics. The client may retry a fresh request for network/timeout failures and HTTP 408, 429, 500, 502, 503 or 504. Retries are bounded to at most three, obtain a fresh token and honor only a clamped `Retry-After` value. Caller cancellation is never retried.
- Successful JSON bodies are bounded before deserialization. Error bodies are not read into exception messages. Directory IDs, conversation participants, submit IDs, recipient IDs, counts, timestamps and protocol structures are validated after deserialization.
- The HTTP acknowledgement timestamp remains server-authoritative. The transport validates the interface argument locally but does not send it.

## Consequences

Package consumers can share one HTTP contract without coupling client code to ASP.NET Core. The client cannot prove that the access token really contains its configured IDs; validation and refresh remain responsibilities of the host token provider and identity system. Managed token strings cannot be reliably zeroed, so providers should keep lifetimes short and avoid additional copies.

The package intentionally adds no offline queue, WebSocket/SignalR connection, token cache, refresh protocol, cookies, telemetry sink or general-purpose resilience policy. Hosts must preserve Authorization-header redaction in their HTTP logging and must not inject the typed client into a singleton.
