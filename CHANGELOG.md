# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Design specification for a multi-key **key-ring signing and rotation**
  architecture (`docs/superpowers/specs/2026-08-14-key-ring-signing-design.md`).
  It replaces the single configured signing key with hot-reloadable, directory-
  scanned keys, an authoritative Postgres-backed default, distinct
  rotate/retire/revoke operations, and a supported path for importing licenses
  produced offline by `LicenseGenerator`. **Design only — no implementation
  yet**; `LicenseValidator`'s embedded trust model is explicitly out of scope
  and unchanged.
- Bash equivalents of the PowerShell scripts in `scripts/`
  (`new-demo-licenses.sh`, `new-offline-activation-request.sh`,
  `test-database-and-auth.sh`, `test-activation-flow.sh`,
  `test-license-flow.sh`) for running the demo-licensing, offline-activation,
  and integration-test flows from macOS/Linux shells.

### Changed
- Repository-local `.claude/settings.json` now ships a minimal, schema-valid
  permission set (`dotnet build`/`test`/`restore`, read-only `git` commands)
  in place of an earlier overly broad, non-schema-valid draft.

## [0.1.0] - 2026-08-14

Initial tracked release. Brings together the signed-license toolchain and the
PostgreSQL-backed `LicenseServer` administration/licensing service built out
over the project's first development cycle.

### Added

**Signing and validation toolchain**
- `Licensing.Core`: shared license contract, canonical JSON, and schema
  validation used by every signer and verifier so their interpretation of a
  license cannot drift independently.
- `LicenseGenerator`: ECDSA P-256 key generation and offline license signing
  CLI, with private-key/key-ID sanity checks against the trusted key map.
- `LicenseValidator`: signature, schema, product, and expiry validation CLI
  with embedded public keys and device-ID display, for fully offline
  end-product verification.
- Device-binding model (`DeviceIdentity`) hashing an OS installation ID with a
  product namespace, plus documented transfer/invalidation state machine
  (`available → active → deactivated → active`, with `revoked` as terminal).

**LicenseServer core lifecycle**
- Server-generated, immutable `LIC-{yyyy}-{MMdd}{value:X6}` license IDs,
  allocated atomically through a PostgreSQL counter upsert with per-day
  rollover protection.
- Full lifecycle coverage: issue, online/offline activate, refresh, deactivate
  and transfer, cancel, and revoke — enforced with serializable/read-committed
  transactions and a partial unique index limiting one live activation per
  license.
- Secure, cryptographically random activation codes and bearer tokens, shown
  once and stored only as SHA-256/HMAC hashes at rest.
- Authoritative, signature-covered `metadata.contactEmail` snapshot on every
  issued license, enforced as a database invariant independent of the
  customer's current email.
- Administratively managed product and edition catalogs, replacing free-text
  product entry, with archival that preserves historical references.

**Administration, identity, and access**
- Permission-based RBAC with seven built-in roles (System Administrator,
  License Manager, License Issuer, Support Agent, Product Administrator,
  Auditor, Billing Automation) enforced at the action level on both UI and API.
- ASP.NET Core Identity with MFA (TOTP + one-time recovery codes) and WebAuthn
  passkeys; production requires an MFA-authenticated principal for high-risk
  permissions.
- Operator and service-account administration, including invitation, forced
  password setup, role changes, and safe disable/demotion that always leaves
  one enabled System Administrator.
- Scoped bearer API credentials (`lic_live_<public-id>_<secret>`) with
  versioned HMAC digests at rest, mandatory expiry for human-owned keys, and
  atomic rotation/revocation.

**API surface**
- Versioned `/api/v1/admin` REST API mirroring UI authorization policies, with
  bounded DTOs, ETag/`If-Match` concurrency on terms updates,
  `Idempotency-Key` support on issuance, `X-Correlation-ID` on every response,
  and a generated OpenAPI 3.1 document at `/openapi/v1.json`.
- Public device APIs for activation, validation, refresh, and deactivation.

**Notifications and customer access**
- Durable transactional email outbox (MailerSend-backed) covering purchase,
  renewal, payment failure, invoice, operator invitation, Identity, and
  magic-link templates, with `FOR UPDATE SKIP LOCKED` batch claiming, bounded
  retries, and signature-verified inbound webhooks.
- Passwordless customer portal: email-challenge magic links, a scoped,
  short-lived customer session distinct from operator Identity, and read-only,
  redacted license/device projections.

**Billing**
- Verified, idempotent Stripe webhook ingestion (raw-body signature check
  before any parsing or side effects) with a `WebhookInbox` and
  `FOR UPDATE SKIP LOCKED` billing worker.
- Idempotent licensing policy engine: monotonic renewals, configurable payment
  grace, paid-through cancellation, refund/dispute review actions, and
  provider-ID mapping tables kept separate from provider-neutral billing
  models.
- Operator billing tooling: redacted event listing and safe reprocessing at
  `/api/v1/admin/billing/events`.

**Operations and delivery**
- Append-only audit trail for every sensitive mutation, with actor, action,
  target, and correlation context.
- Hardened Docker Compose deployment: non-root app user, read-only root
  filesystem, dropped capabilities, `no-new-privileges`, and explicit volumes
  for PostgreSQL and Data Protection keys.
- CI workflow for build/test on the .NET solution.
- PowerShell test suites covering license/activation flows, database and auth
  invariants, and container smoke testing; offline issuance/import scripts.
- Operator runbook (`docs/operator-runbook.md`) and full acceptance
  traceability matrix (`docs/roadmap-traceability.md`).

### Fixed
- Gated Stripe purchase fulfillment on `payment_status` and handled the
  `async_payment_succeeded` follow-up event for delayed payment methods.
- Ignored `invoice.payment_failed` once an invoice is already recorded paid,
  preventing reordered webhooks from pushing a paid contract back into grace.
- Fetched canonical subscription state for `subscription.created`/`updated`
  events instead of trusting a potentially stale webhook payload.
- Bound `Billing:WorkerEnabled` config to the background billing worker so
  disabling it actually stops processing.
- Sanitized legacy `Entitlements.Product` values into unique, constraint-safe
  product codes during catalog backfill migration, instead of failing on real
  historical data.
- Revoked a user's API credentials whenever their roles are reduced, not only
  when the account is disabled.
- Let failed Stripe current-state reconciliation retry with backoff instead of
  being marked terminal and silently dropped.
- Scoped the customer portal's license listing to every `Customer` record
  sharing the session's normalized email, since each issuance creates its own
  `Customer` row.
- Aligned the container SDK image with the `global.json` pin.

### Security
- Activation codes and bearer tokens are never stored in plaintext — only
  SHA-256 or versioned HMAC-SHA-256 digests.
- Stripe and MailerSend webhooks are verified against the raw request body
  with fixed-time signature comparison before any parsing or database writes.
- Rotated the 2026 primary/secondary license signing keys and their embedded
  public-key trust map.
- Removed the legacy `local-poc-admin-key` header-based admin bypass.

[Unreleased]: https://github.com/danijeljw-RPC/licsense-server-poc/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/danijeljw-RPC/licsense-server-poc/releases/tag/v0.1.0
