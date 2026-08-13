# Stage 06 of 16 — Enforce customer email and signed contact metadata

Read `docs/README.md` in full. This is stage 06. Inspect current issuance, customer persistence, signed schema/canonical JSON, seed paths, search, migrations, and tests. Preserve unrelated work. Implement the invariant in shared services and the database, not just in the form.

Add required `Customer.Email` and `Customer.NormalizedEmail`. Use a single explicit normalization/validation policy and a unique or non-unique index appropriate to the application's customer model; document the choice. Search email case-insensitively using `NormalizedEmail`. Keep `Customer.Email` as current contact data while each license's `metadata.contactEmail` is the immutable issuance snapshot.

Change `LicenseRecord.MetadataJson` from text to PostgreSQL `jsonb`. Every issuance path—UI, internal service, seeds, future API callers, and integration adapters—must fail without a valid email and must write exactly one authoritative lower-camel-case `contactEmail` equal to the normalized customer email. Clients may propose approved flat scalar metadata but cannot omit, null, nest, or override this field. The domain service must write it after validating the customer.

Add a PostgreSQL integrity boundary: a check constraint and/or robust save interceptor that rejects missing, non-string, invalid, or conflicting `contactEmail`. Add useful indexes, including a JSONB expression index only if it materially supports integrity/search. Ensure canonical signing still accepts the metadata shape and makes clear that this email is plaintext to anyone holding the signed file. Never put billing data, secrets, or provider objects into signed metadata.

Update `/licenses/create`, detail/list/search views, seed/demo data, fixtures, and API-facing DTOs. Explain the signed-plaintext consequence in the issuance UI. Do not silently rewrite historical snapshots when a customer's current email changes; any later reissue/update must be explicit, versioned, and audited.

Make the stage-01 metadata/search tests green, including forged payloads, every issuance path, database-level rejection, exact authoritative value, seed validity, case-insensitive search, and immutable snapshot behavior. Add a migration, run targeted/full tests, and verify a clean migration and idempotent seed in temporary PostgreSQL. Finish with commands/results and the chosen normalization/integrity approach.
