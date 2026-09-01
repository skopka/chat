# ADR 0010: headless presentation state and optional Blazor components

Date: 2026-09-01
Status: accepted for package version 0.9.0.

## Context

The engine exposes authenticated typed content and a deterministic projection, but every consumer otherwise has to rebuild composer, reply, reaction and forwarding state. Shipping one fixed visual application would make reuse difficult and would couple client encryption to a UI framework. UI code also handles decrypted managed strings and therefore belongs entirely on the client side of the trust boundary.

## Decision

Add `Skopka.Chat.UI.Core` as a framework-independent package that references Client only. `ChatViewModel` wraps one `ChatConversationProjection`, bounded draft/reply state and commands. All sending crosses `IChatContentSender`, which is implemented by the host because device enumeration, encryption, transport, retries and protected persistence remain deployment-specific. Expected failures return a generic result without remote text; successful results must include a matching authenticated local echo before the view model applies it.

Add `Skopka.Chat.UI.Blazor` as a Razor class library that references UI.Core and the ASP.NET Core shared framework. Its default conversation, message and composer components render text through Razor encoding, expose accessible labels, support CSS custom properties and localized strings, and allow complete message/composer/empty templates. Forwarding raises a host callback for target selection. The UI packages never reference Server, persistence or an HTTP implementation.

The coordinated package version advances to `0.9.0` and now contains nine package IDs. Protocol v1, encrypted content v1, routes, persistence and cryptography are unchanged.

## Consequences

- Hosts can use a working Blazor conversation surface, replace individual templates, or bind another framework directly to UI.Core.
- `IChatContentSender` is deliberately not a transport adapter. It must preserve logical content IDs across recipient-device fan-out and return only a locally authenticated echo.
- The default UI retains only generic expected-failure state, but host implementations remain responsible for safe exception and telemetry handling.
- Decrypted strings exist in component and view-model memory and cannot be reliably zeroed. Durable local history, retention, browser/server circuit exposure and notification redaction remain host responsibilities.
- CSS isolation and the supplied variables provide a baseline, not a stable pixel-identical design contract. Semantic component parameters and UI.Core public APIs follow package SemVer.
