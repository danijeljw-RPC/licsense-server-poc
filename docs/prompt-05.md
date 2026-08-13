# Stage 05 of 16 — Generate and reveal activation codes once

Read `docs/README.md` in full. This is stage 05 after database-generated license IDs. Inspect the real issuance services/UI, data model, configuration, logging, audit, tests, and working tree. Preserve unrelated changes and keep issuance transactional. Implement and verify the complete slice.

Remove activation-code plaintext from all client/operator issuance inputs. Create a cryptographically secure generator using `RandomNumberGenerator.GetInt32` and the exact alphabet `ABCDEFGHJKMNPQRSTUVWXYZ23456789`. Generate 32 characters and format them `8-4-4-4-12`. Do not use GUIDs, `Random`, timestamps, counters, modulo-reduced bytes, or ambiguous characters.

Store only a versioned lookup/password hash. Prefer HMAC-SHA-256 with a server-side pepper supplied outside source control, and add a hash-version field so rotation/legacy SHA-256 verification is possible. Use fixed-time verification. Define safe development configuration and clear startup failure for missing production secret material; never write the pepper or plaintext code to logs, exceptions, audit, URLs, analytics, database rows, or later GETs.

Have the issuance service return a result containing plaintext exactly once after the complete transaction commits. Build an issuance-result screen that displays license ID and activation code in a read-only field, offers an accessible Copy button with success/failure feedback, warns that recovery is impossible, and loses plaintext state on navigation. Prevent double submission and add a UI idempotency token. Do not place the code in query strings, persistent browser storage, or server logs.

Design the service boundary now for later API `Idempotency-Key` support. If retry-result storage is implemented in this stage, encrypt it, bind it to the request/principal, expire it after a short configured window, and ensure it cannot become a general recovery mechanism. Otherwise leave a clean explicit interface for stage 11 without issuing duplicates.

Make stage-01 activation-code tests green: alphabet/shape, sufficient repeated generation, one-time response, hashing/version compatibility, fixed-time validation path, no plaintext persistence/log/audit/subsequent GET, double-submit idempotency, and result-page behavior. Run targeted tests, the full suite, and a browser smoke test for Copy/navigation. Finish with commands/results and security-relevant design choices.
