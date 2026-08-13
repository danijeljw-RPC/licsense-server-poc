# Stage 13 of 16 — Build passwordless customer access

Read `docs/README.md` in full. This is stage 13 after transactional email. Inspect customer/license models, current authentication, MailerSend abstraction/outbox, rate limiting, UI, tests, and working tree. Preserve unrelated changes. Implement a separate restricted customer session; never treat an activation code as a portal password.

Build a passwordless email flow where a customer supplies email, optionally with a license ID, and always receives the same generic response regardless of match. On a valid normalized match, enqueue a single-use magic link or email OTP. Store only a hash, expire it in 10–15 minutes, rate-limit by normalized identifier/IP without creating an enumeration side channel, and consume it atomically. Create a short-lived, customer-scoped session separated from operator Identity/permissions.

The initial customer portal is read-only: show only that customer's license status, product, edition, seats, expiry, activation status, a redacted device suffix, and invoice/renewal links when available. Never reveal activation-code plaintext, hashes, full device IDs/tokens, other customers, audit internals, signed metadata beyond approved fields, or operator controls. Use the lifecycle state precedence and render perpetual expiry as `Never`.

Require a fresh email challenge for future sensitive operations. Do not implement self-service deactivation, contact-email changes, or renewal mutations in this stage; surface them as unavailable rather than insecure placeholders. Make session/cookie settings, CSRF defenses, logout, fixation prevention, token replay resistance, and customer-record authorization explicit.

Add tests for generic responses and timing-insensitive behavior where practical, normalization, token hash-only persistence, expiry, single use/concurrent consume, rate limits, wrong-customer access, direct object reference attempts, session boundaries, redaction, and email-outbox idempotency. Include browser accessibility/usability coverage for request, consume, portal, and logout flows.

Run targeted tests, full tests, and a browser smoke test using the fake email transport. Finish with the threat boundaries, exact commands/results, and deferred sensitive features.
