# Stage 04 of 16 — Generate immutable license IDs atomically

Read `docs/README.md` in full. This is stage 04. Inspect the completed lifecycle work, current issuance flow, EF model, PostgreSQL fixtures, configuration, and working tree. Preserve unrelated changes. Implement the feature end to end rather than describing it.

Remove `LicenseId` from every operator/client issuance input contract and make it immutable after insertion. Add `LicenseIdCounter` with `BusinessDate` as the primary key and `LastValue` constrained to `0..16777215`. Implement a dedicated allocator that resolves the business date using `Licensing:IdTimeZone` (default `Australia/Adelaide`) while all real timestamps remain UTC. Allocate inside the same transaction as license creation using PostgreSQL `INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING`.

Format IDs exactly as `LIC-{yyyy}-{MMdd}{value:X6}` with uppercase hexadecimal. Retain a unique index on `Licenses.LicenseId`. Fail clearly after `0xFFFFFF` issues in a business day; never wrap, accept a caller override, or fall back to random data. Make retry/concurrency behavior explicit and safe, including transaction isolation and how an issuance rollback affects the counter.

Update the create page to show only a read-only `Generated on issue` placeholder before submission and the allocated ID after success. Update seeds, test builders, and internal call sites without adding a public override escape hatch. If a test-only seam is required, inject the clock/business-date resolver, not the allocated ID.

Make stage-01 ID tests green, including 1,000 concurrent creations, regex matching, uniqueness, monotonic daily values, day/time-zone rollover, overflow, immutability, forged form/API input, and rollback behavior. Use real PostgreSQL for atomicity tests.

Add a migration and run targeted tests, the full suite, and clean database migration/seed verification. Finish with the allocation transaction design, configuration added, test commands/results, and any later-stage failures still expected.
