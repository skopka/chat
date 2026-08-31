# ADR 0006: strict and bounded HTTP JSON boundary

Date: 2026-09-01
Status: accepted for package version 0.6.0.

## Context

The authenticated endpoints and typed client already applied protocol validation and byte limits, but the default web JSON profile remained permissive about case-insensitive property matching, duplicate members and unknown members. Ambiguous documents can be interpreted differently by proxies, clients, logs or a future implementation. Preserving a remote `JsonException` as an inner exception could also expose an attacker-controlled property name or JSON path through application telemetry.

.NET 10 exposes duplicate-member rejection through [`JsonSerializerOptions.AllowDuplicateProperties`](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.allowduplicateproperties?view=net-10.0), unknown-member rejection through [`JsonUnmappedMemberHandling.Disallow`](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/missing-members), and the corresponding source-generation settings through [`JsonSourceGenerationOptionsAttribute`](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonsourcegenerationoptionsattribute?view=net-10.0).

## Decision

- Define one source-generated JSON profile in `Skopka.Chat.Transport.Http` and apply it to the typed client plus ASP.NET Core `HttpJsonOptions`.
- Require exact camel-case member names. Reject duplicate and unmapped properties, comments, trailing commas, trailing root values, string-to-number coercion, missing required constructor values and null for non-nullable members.
- Limit JSON nesting to 16. Keep existing request and response byte limits; protocol limits remain authoritative for decoded key, nonce, ciphertext, tag and signature lengths.
- Require `application/json` or a structured `+json` media type for successful client responses. ASP.NET Core rejects request bodies with unsupported media types.
- Replace JSON parsing and remote DTO-to-domain failures with the same generic `ChatHttpTransportException`, without retaining the remote exception as `InnerException`.
- Gate the boundary with deterministic hostile-input corpora mirrored across TestServer and the typed client. Cover malformed, truncated, duplicate, unknown, case-mismatched, null, missing, invalid Base64, excessive-depth, trailing-data and wrong-media-type inputs, plus invalid identifiers and every cryptographic envelope byte field.

## Consequences

Ambiguous or structurally hostile JSON fails closed before state changes or cryptographic work, and attacker-controlled JSON details do not enter public exception text. The HTTP routes, DTO member names and protocol-v1 canonical bytes are unchanged.

`AddSkopkaChatAspNetCore` configures the host's shared Minimal API `HttpJsonOptions`, so unrelated JSON endpoints in the same application also receive the strict profile. Hosts must either keep those endpoints compatible or isolate transports in separate applications. This corpus is deterministic regression coverage, not a substitute for coverage-guided fuzzing, an independent security review, reverse-proxy limits or deployment-specific request logging tests.
