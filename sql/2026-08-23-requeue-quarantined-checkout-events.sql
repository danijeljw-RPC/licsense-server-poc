-- Requeue a quarantined checkout.session.completed webhook so the billing
-- worker reprocesses it now that the "guest checkout has no Stripe customer"
-- bug is fixed (see BillingPolicies.cs / StripeWebhook.cs).
--
-- Context
-- -------
-- Event evt_1U7GFG4BF2uOrrJbIpi5NF1a was a one-time (mode=payment) checkout
-- via a Payment Link with customer_creation=if_required. Stripe never created
-- a Customer object, so the webhook payload's top-level "customer" field was
-- null. StripeBillingPolicyProcessor.PurchaseAsync used to hard-require a
-- non-null CustomerId, so this row landed in WebhookInbox with
-- Status = 'quarantined' and LastErrorCode = 'incomplete_purchase_state' —
-- no Customer/LicenseOrder/License was ever created for this purchase.
--
-- The code now falls back to the checkout's verified email when Stripe
-- didn't attach a Customer object, so simply flipping this row back to
-- 'pending' is enough: BillingInboxWorker's poll loop will pick it up,
-- StripeBillingEventProcessor will reprocess the original payload (still
-- sitting in ProtectedPayload, untouched), and PurchaseAsync will create the
-- Customer/LicenseOrder/License this time.
--
-- This does NOT touch the stored payload — it only resets the queue bookkeeping
-- columns so the row becomes eligible for another attempt.
--
-- How to run (production, via docker compose)
-- ---------------------------------------------
-- Run from the repo root, where compose.prod.yaml lives:
--
--   docker compose -f compose.prod.yaml exec -T postgres \
--     psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
--     -f - < sql/2026-08-23-requeue-quarantined-checkout-events.sql
--
-- ($POSTGRES_USER / $POSTGRES_DB must match whatever is set in .env.prod —
-- defaults are license_app / license_server, see compose.prod.yaml.)
--
-- Or, to eyeball the row first / paste interactively:
--
--   docker compose -f compose.prod.yaml exec postgres \
--     psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"
--   -- then paste the SELECT and UPDATE below by hand.
--
-- Only run this AFTER the fix in BillingPolicies.cs (guest-checkout email
-- fallback) has been deployed — requeuing before that just quarantines the
-- row again with the same error.

BEGIN;

-- 1. Sanity check: confirm this is the row we expect before mutating it.
--    Expect exactly one row, Status = 'quarantined', LastErrorCode = 'incomplete_purchase_state'.
SELECT "Id", "ProviderEventId", "EventType", "Status", "LastErrorCode",
       "AttemptCount", "NextAttemptAt", "ReceivedAt"
FROM "WebhookInbox"
WHERE "ProviderEventId" = 'evt_1U7GFG4BF2uOrrJbIpi5NF1a';

-- 2. Reset it so BillingInboxWorker's next poll picks it back up.
--    AttemptCount is deliberately left as-is (it's a lifetime counter, not
--    reset by other successful paths either); NextAttemptAt = now() makes it
--    immediately eligible; clearing LeaseId/LeaseExpiresAt guards against a
--    stale lease in case it was left mid-processing.
UPDATE "WebhookInbox"
SET "Status" = 'pending',
    "LastErrorCode" = NULL,
    "NextAttemptAt" = now(),
    "LeaseId" = NULL,
    "LeaseExpiresAt" = NULL
WHERE "ProviderEventId" = 'evt_1U7GFG4BF2uOrrJbIpi5NF1a'
  AND "Status" = 'quarantined';

-- 3. Confirm the update landed (expect Status = 'pending', LastErrorCode = NULL).
SELECT "Id", "ProviderEventId", "Status", "LastErrorCode", "NextAttemptAt"
FROM "WebhookInbox"
WHERE "ProviderEventId" = 'evt_1U7GFG4BF2uOrrJbIpi5NF1a';

COMMIT;

-- After this runs, tail the app logs for a moment to confirm the worker
-- actually processed it (row should flip to Status = 'completed'):
--
--   docker compose -f compose.prod.yaml logs -f app
--
-- If it quarantines again, the fix wasn't actually deployed to this
-- container yet, or there's a second, different root cause — don't re-run
-- this script blindly, go back to WebhookInbox."LastErrorCode".
