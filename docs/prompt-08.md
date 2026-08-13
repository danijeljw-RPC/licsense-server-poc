# Stage 08 of 16 — Introduce permission-based RBAC

Read `docs/README.md` in full. This is stage 08 and begins the administration security phase. Inspect current Identity setup, `Administrator` policy usage, pages, handlers, endpoints, database initialization, tests, and working tree. Preserve unrelated changes and do not rely on hidden navigation as authorization.

Implement named permission claims/policies for all roadmap permissions: `licenses.read`, `licenses.issue`, `licenses.update`, `licenses.cancel`, `licenses.revoke`, `activations.manage`, `customers.read`, `customers.manage`, `products.read`, `products.manage`, `users.read`, `users.manage`, `apiKeys.manageSelf`, `apiKeys.manageAll`, `audit.read`, and `billing.manage`.

Seed the built-in roles and their permission claims idempotently: System Administrator, License Manager, License Issuer, Support Agent, Product Administrator, Auditor, and Billing Automation, following the roadmap's intended access. Migrate/map the existing `Administrator` role to System Administrator without locking out the current administrator. Centralize permission constants and policy registration so spelling cannot drift.

Apply action-level policies to every existing page, form handler, minimal API endpoint, and shared service boundary. Read access must not imply issue/update/cancel/revoke. Navigation may reflect permissions but is only presentation. Return a consistent challenge/forbid result without revealing protected data. Establish an MFA requirement mechanism for high-risk permissions (`users.manage`, `apiKeys.manageAll`, `licenses.revoke`) in production, while keeping automated tests and explicit development behavior usable and documented.

Add a complete role/permission matrix test that proves each built-in role can perform allowed actions and receives `403` for disallowed direct HTTP calls; unauthenticated callers receive `401`/challenge as appropriate. Test migration of the legacy administrator, claim-seed idempotency, page/endpoint parity, and MFA gating. Do not add API keys or user-management UI in this stage.

Run targeted authorization tests, full tests, and repeated database initialization. Finish with the role matrix actually implemented, compatibility handling, commands/results, and any later-stage red tests.
