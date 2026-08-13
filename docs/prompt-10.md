# Stage 10 of 16 — Add scoped bearer API credentials

Read `docs/README.md` in full. This is stage 10 after users/RBAC. Inspect authentication configuration, user/service-account model, permissions, database model, logging middleware, audit, tests, and working tree. Preserve unrelated changes. Implement API credentials as production-minded secrets, not plain database tokens.

Add storage and authentication for bearer values shaped `lic_live_<public-id>_<secret>`. Use a recognizable non-secret public ID for indexed lookup and at least 256 random bits for the secret. Store only a versioned HMAC/hash plus prefix/public ID, last four, owner, scopes, created/last-used/expiry/revoked timestamps. Use fixed-time comparison. Show the full key once and never return it again.

Build `/settings/api-keys` with create, one-time reveal/copy, name, owner/service account, constrained scopes, mandatory expiry for human-owned keys, created/last-used/expiry, rotate, and revoke. Enforce `apiKeys.manageSelf` versus `apiKeys.manageAll` on both reads and writes. Expired/revoked keys cannot be restored. Rotation creates a new secret and invalidates the old one atomically. Disabling a user must revoke owned keys using the hook from stage 09.

Register an authentication scheme that coexists cleanly with Identity cookies. Bearer requests authenticate only from `Authorization`; they do not require antiforgery, while cookie-authenticated mutations still do. Require HTTPS outside development, redact authorization headers, update last-used safely without excessive write contention, support immediate revocation, and rate-limit by principal and IP. Never log raw keys, hashes, peppers, or full headers.

Map key scopes to the same permission policies introduced in stage 08; do not create a parallel authorization system. Add migration and tests for one-time reveal, entropy/format, hash-only persistence, correct `401` versus `403`, scope enforcement, expiry, rotation, immediate revocation, owner disablement, antiforgery distinction, concurrent use, and log/audit redaction.

Run targeted security tests, the full suite, and clean migration/seed verification. Finish with scheme behavior, secrets/configuration requirements, commands/results, and later API work intentionally deferred.
