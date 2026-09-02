# Skopka.Chat MAUI sample

This sample composes the reusable MAUI packages without embedding credentials or pretending that test headers are authentication.
Replace `ConfigureAuthenticationProvider` with the host application's OIDC/session provider. It must return trusted user/device IDs
that the access token binds to server claims, a peer selected by the host contact directory, and an HTTPS server base address.

The sample then loads or creates device keys in `SecureStorage`, registers public keys, gets the unique personal conversation,
restores the newest SQLite history page, resumes the encrypted outbox, opens `SkopkaChatView`, performs multi-device E2EE fan-out,
polls on foreground/resume, and uses callbacks for bounded attachment pick/encrypt/upload and authenticated download. It never opens
downloaded data automatically. SQLite history remains plaintext and must be protected by the platform and host retention policy.
