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
  produced offline by `LicenseGenerator`. Implemented since as a reduced core
  slice; the license-import feature remains design-only.
  `LicenseValidator`'s embedded trust model is explicitly out of scope and
  unchanged.
- `Licensing.Core.LicenseEnvelope` — the single envelope-construction and
  signing implementation, now called by both `LicenseServer`'s online signing
  path and the offline `LicenseGenerator` CLI, so canonicalization and
  signature rules cannot drift between them.
- `Licensing.Core.SigningKeyFiles` — the `<keyId>.private.pem` /
  `<keyId>.public.pem` naming convention and key-ID rules in one place, shared
  by the server's key directory scanner and the CLI.
- `LicenseGenerator keygen --id <keyId> --output <dir>`, writing exactly the
  filenames the server's key directory scanner discovers. Generated private
  keys are mode `600` on POSIX platforms, requested at file-creation time;
  Windows has no POSIX mode and the command says so.
- `LicenseGenerator sign --public-key <path>`, overriding the public key the
  private-key/key-ID pair check runs against.
- `LicenseGenerator sign` now hard-fails before signing if the resolved key ID
  is present in `TrustedPublicKeys.ByKeyId` and the on-disk public key doesn't
  match the compiled entry. This restores the guarantee moving the pair check
  off `TrustedPublicKeys` gave up: a locally regenerated pair reusing an
  existing key ID (e.g. `keygen --id primary-2026 --force`) is rejected before
  anything is signed, instead of quietly producing licences every released
  validator rejects. A key ID absent from `TrustedPublicKeys` — the normal
  key-ring case — still signs exactly as before, with no warning; the map is
  consulted only as a negative check, never as an allowlist for which key IDs
  may sign.
- Recorded decisions for the four open license-import design questions —
  catalog handling for unknown products, the email source for metadata-free
  imports, activation credentials on pre-activated imports, and verbatim
  artifact storage. Decisions only; the import endpoint is still unimplemented.
- `POST /api/v1/admin/signing-keys/rescan` (and the Blazor "Rescan key
  directory" button, which now goes through the same `RescanAsync` method)
  write an `AuditRecord` (`signingKey.rescan`), matching `set-default` and
  `revoke`. Written unconditionally, on every invocation, not only when the
  rescan changes the published key-ring snapshot.
- CI now runs on pull requests and pushes to `dev`, not only `main`.
- A `test-bash-license-flow` CI job runs `scripts/test-license-flow.sh` on
  `ubuntu-latest`, covering the bash port of the license-flow scripts that
  CI previously never exercised.
- Bash equivalents of the PowerShell scripts in `scripts/`
  (`new-demo-licenses.sh`, `new-offline-activation-request.sh`,
  `test-database-and-auth.sh`, `test-activation-flow.sh`,
  `test-license-flow.sh`) for running the demo-licensing, offline-activation,
  and integration-test flows from macOS/Linux shells.

### Changed

- `LicenseGenerator sign` checks the selected private key against the
  `<keyId>.public.pem` PEM pair on disk instead of the hardcoded
  `TrustedPublicKeys` map. A signing key created the key-ring way — dropping
  two PEM files into the key directory — can now be used by the offline
  generator immediately, with no `TrustedPublicKeys.cs` edit or CLI rebuild.
  The public half is located by key ID rather than by rewriting the private
  key's filename, so the check still catches a private-key/key-ID mismatch.
  One consequence: signing a private key stored outside the
  `<keyId>.private.pem` convention, with no public half beside it, now requires
  the new `--public-key`. A second consequence — the check no longer proving
  the key ID is one shipped products trust — is addressed below.
- `LicenseGenerator sign --key-id` is optional, derived from a
  `<keyId>.private.pem` filename and still overridable.
- The key-ring contracts (`ILicenseKeyRing`, `ILicenseSigner`,
  `ILicenseVerifier`, `SigningKeyInfo`, `SigningKeyStatus`,
  `LicenseSigningResult`) moved from `LicenseServer` into `Licensing.Core` as
  pure contracts, making them reachable from `LicenseGenerator`.
  `Licensing.Core` still has no ASP.NET Core or EF Core dependency.
- The key-ring design spec now records the absence of a `FileSystemWatcher` as
  a settled rejected alternative with its reasoning, rather than leaving the
  shipped periodic-reload behavior contradicting the written design.
- Repository-local `.claude/settings.json` now ships a minimal, schema-valid
  permission set (`dotnet build`/`test`/`restore`, read-only `git` commands)
  in place of an earlier overly broad, non-schema-valid draft.

### Fixed

- README's production-hardening guidance referenced `LicenseEnvelopeSigner`,
  a class deleted when the signing key ring landed. It now names the current
  signing component (`SigningKeyRingService` behind `ILicenseSigner`).
- CI's "Database and authentication" test leg filtered on `Suite=Baseline`, an
  allowlist matching only 4 of 152 tests. Every other suite — including the
  entire signing-key-ring test suite and several test classes with no `Suite`
  trait at all — silently never ran in CI. Switched to excluding
  `Suite=Phase0Roadmap` (the intentional-red executable specification)
  instead, so everything meant to pass runs: 106 of 152 tests, all green.
- `EcdsaKeyPairs.TryValidatePair`, `TryValidatePublicKey`, and
  `PublicKeysMatch` never checked that imported key material was actually on
  the NIST P-256 curve. A self-consistent key pair generated on another curve
  (e.g. P-384) passed `TryValidatePair` cleanly, while `LicenseEnvelope.Sign`
  still hardcoded the envelope's `algorithm` field to `ECDSA-P256-SHA256`
  regardless of the curve actually used, producing a mislabeled artifact. All
  three methods now reject any key not on P-256; the two `Try*` methods
  return `false` with an explanatory error, and `PublicKeysMatch` throws
  `CryptographicException`, consistent with its existing exception-based
  contract for malformed input. Purely additive: every key in `keys/` and
  every key `LicenseGenerator keygen` produces is already P-256. (#32)

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
