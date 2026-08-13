# Stage 07 of 16 — Add product and edition catalogs

Read `docs/README.md` in full. This is stage 07, after safe issuance and customer metadata. Inspect actual entities, issuance services, pages, routes, tests, migrations, and working tree. Preserve unrelated changes. Implement the catalog slice end to end.

Add `ProductDefinition` with UUID ID, required immutable lower-case stable `Code`, display name, optional description, `IsActive`, and UTC created/updated timestamps. Make `Entitlement` reference the product row while retaining the immutable product-code snapshot used in signed artifacts. Archive referenced products instead of deleting them; code cannot change after first use. Seed known existing products idempotently without breaking historical licenses.

Build permission-ready `/products` list/search/add/edit/activate/archive UI. Show license reference counts. The issue form must list only active products, post a product ID, and let the server resolve the code. Never trust a posted code/display name. Archived products remain readable on historical records but cannot be used for new issuance.

Create shared controlled values for editions exactly: `community`, `project`, `education`, `consumer`, `business`, `smb`, `enterprise`, `corporate`. License types remain exactly `perpetual`, `subscription`, `evaluation`. Reuse these values for issuance and detail-page edit options, server validation, API contracts, database constraints, and tests; invalid forged requests must receive field-level errors rather than falling back to free text. Complete the shared terms-amendment service/UI from stages 02–03 by adding controlled edition changes with version checks and audit old/new values.

Enforce exactly one entitlement in every administration-issued license at the service and database level where practical. Keep the core signed schema's array and do not build the future multi-product support CLI. Ensure product lookup, entitlement creation, ID/code snapshot, activation, signing, and audit occur consistently in the issuance transaction.

Add the migration and make stage-01 catalog/one-entitlement tests green, including inactive products, forged IDs/codes, code immutability, archive behavior, reference counts, invalid editions/types, and seed idempotency. Run targeted tests, the full suite, and clean migration/seed verification. Finish with changed routes/schema and exact commands/results.
