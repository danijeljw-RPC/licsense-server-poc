# Stage 11 of 16 — Complete the versioned administration API

Read `docs/README.md` in full. This is stage 11 after shared services, RBAC, users, and API keys. Inspect all existing endpoints/contracts, domain services, authentication schemes, rate limits, audit, tests, and working tree. Preserve unrelated changes. Implement the API using the same domain services and authorization policies as the UI.

Complete `/api/v1/admin` for: paged/filterable license list; one-product issuance with one-time activation code; license detail; terms patch; cancel; revoke; activation-code rotation; operator-assisted deactivation; product list/create/update/archive; customer search/update; user list/create/update; and filtered audit. Apply the exact roadmap permissions per operation. Device-facing anonymous endpoints remain separate and receive stricter guessing-resistant rate limits.

Use explicit request/response DTOs rather than EF entities. Validate controlled fields server-side and return `application/problem+json` consistently with field-level validation details. Support bounded pagination, stable sorting, correlation IDs, and `ETag`/`If-Match` or explicit version concurrency for updates. Administrative issuance must reject caller-supplied license IDs/codes, return the generated code only in `201 Created`, and enforce one entitlement plus mandatory contact email.

Implement durable `Idempotency-Key` semantics for create and integration-style mutations. Bind keys to principal, route, and a canonical request fingerprint. Same key/same request returns the original status/body during a short defined retry window without duplicating license/audit/outbox work; same key/different request is a conflict. Protect any retained one-time activation code with encryption and automatic expiry, not reversible indefinite storage.

Publish OpenAPI with cookie and bearer security schemes, request/response/problem examples, permissions, idempotency, concurrency, pagination, offline limitations, and one-time-secret behavior. Use the official ASP.NET Core/.NET 10 OpenAPI APIs available in this repository; do not adopt an obsolete package casually.

Add contract/integration tests for every route, permission, `401`/`403`, antiforgery distinction, invalid inputs, concurrency conflicts, idempotent retries, secret non-disclosure, pagination/sorting, rate limits, and UI/API domain parity. Run targeted tests, full tests, and inspect the generated OpenAPI document. Finish with the route inventory and exact commands/results.
