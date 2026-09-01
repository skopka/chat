# ADR 0007: coverage-guided JSON fuzzing and real Kestrel edge gate

Date: 2026-09-01
Status: accepted for package version 0.7.0.

## Context

The strict HTTP boundary had a deterministic hostile-input regression suite, but mutation coverage remained manually selected and server tests used `TestServer`. `TestServer` does not prove how Kestrel enforces endpoint body limits or propagates a real client disconnect. Untrusted JSON parsing is a suitable coverage-guided fuzz target; [SharpFuzz](https://github.com/Metalnem/sharpfuzz) supports AFL-based .NET instrumentation and treats unhandled exceptions as crashes.

## Decision

- Add a non-packable `Skopka.Chat.FuzzTests` executable that accepts at most the HTTP request limit plus one byte and routes inputs across all seven source-generated DTO contracts.
- Treat only `JsonException` and `ProtocolValidationException` as expected invalid-input outcomes. Successful values must serialize and deserialize again; public device and envelope values also pass domain validation.
- Commit small valid seeds and minimized regression inputs. A cross-platform replay mode runs them deterministically.
- Pin SharpFuzz runtime and command-line versions. On Linux, build the harness into an isolated output directory, replay the corpus, instrument only those copies, then run a time-bounded AFL++ smoke session. Instrumented assemblies are never packed.
- Add loopback Kestrel tests for declared-length and chunked oversized bodies. Both must return 413 before persistence. A separate disconnect test must observe cancellation in the repository boundary.

## Consequences

CI now explores mutations beyond hand-written cases and verifies behavior at the actual ASP.NET Core server edge. The short smoke duration is a regression signal, not evidence of exhaustive coverage. Longer scheduled fuzzing, corpus minimization, resource monitoring under deployment limits, and fuzzing of any future binary decoder remain future work. Reverse-proxy limits are still a host responsibility.
