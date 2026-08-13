# Stage 14 of 16 — Add billing domain and verified Stripe ingestion

Read `docs/README.md` in full. This is stage 14 and starts Stripe work. Inspect current domain services, products/customers, outbox patterns, API routing, configuration, tests, and working tree. Preserve unrelated changes. Stripe APIs and SDKs change, so use current official Stripe documentation and the supported official .NET SDK; cite official sources in the completion report.

Create provider-neutral billing models such as `BillingContract` and `LicenseOrder`, plus dedicated mappings for Stripe customer, product/price, subscription, checkout/invoice, and internal customer/product/license IDs. Do not place Stripe objects, payment data, or provider identifiers in signed license metadata. Stripe must be an adapter; internal licensing rules remain authoritative.

Add anonymous `POST /api/v1/integrations/stripe/webhook`. Read the untouched raw request body and verify the `Stripe-Signature` header with the endpoint secret before parsing or mutating anything. Store each verified Stripe event ID in a unique durable `WebhookInbox` row before processing. Duplicate deliveries return success without repeating work. Invalid signatures and malformed payloads must not create inbox, audit, email, customer, order, or license rows.

Acknowledge valid events quickly and process them asynchronously through a lease/lock-safe background worker. Treat delivery order as non-deterministic and leave a controlled way to fetch current Stripe object state when event payloads are insufficient. Use bounded retries/dead-letter state, correlation/provider event IDs, and audit only non-secret business identifiers. Keep API and webhook secrets outside source control and redact request headers/body/payment details.

In this stage, persist/map/categorize candidate events and establish processing interfaces; do not yet implement final purchase/renewal/grace/refund policy. Add migration and tests using official webhook-signature generation/test fixtures for valid, invalid, duplicate, delayed, reordered, concurrent, malformed, and worker-crash cases. No live Stripe calls in automated tests.

Run targeted/full tests, clean migration/seed checks, and a local signed-webhook smoke test. Finish with schema/event states, configuration names, official-source links, commands/results, and policy work deferred to stage 15.
