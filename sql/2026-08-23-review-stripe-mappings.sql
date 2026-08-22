-- Review what's currently configured in the Stripe -> license term mapping tables.
--
-- Covers all three mapping tables used by the checkout/subscription webhooks:
--   1. "StripeProductMappings"      - Stripe product ID -> license terms (one-time
--                                      purchases) or product resolution only
--                                      (subscription-only rows, Edition/LicenseType/
--                                      Seats NULL).
--   2. "StripePriceMappings"        - Stripe price ID -> license terms, used for
--                                      subscription checkouts.
--   3. "StripeSubscriptionMappings" - Stripe subscription ID -> issued license,
--                                      used to track/renew active subscriptions.
--
-- Read-only; safe to run at any time.
--
-- How to run (production, via docker compose)
-- ---------------------------------------------
-- Run from the repo root, where compose.prod.yaml lives:
--
--   set -a; source .env.prod; set +a
--   docker compose --env-file .env.prod -f compose.prod.yaml exec -T postgres \
--     psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
--     -f - < sql/2026-08-23-review-stripe-mappings.sql

-- ---------------------------------------------------------------------------
-- 1. Product mappings, joined to the internal product catalog
-- ---------------------------------------------------------------------------
SELECT
    spm."StripeProductId",
    pd."Code"            AS "ProductCode",
    pd."DisplayName"     AS "ProductDisplayName",
    spm."Edition",
    spm."LicenseType",
    spm."Seats",
    spm."UpdatesUntil",
    spm."ExpiresAt",
    spm."CreatedAt",
    spm."UpdatedAt"
FROM "StripeProductMappings" spm
JOIN "ProductDefinitions" pd ON pd."Id" = spm."ProductDefinitionId"
ORDER BY pd."Code", spm."StripeProductId";

-- ---------------------------------------------------------------------------
-- 2. Price mappings (subscriptions), joined to the internal product catalog
-- ---------------------------------------------------------------------------
SELECT
    spr."StripePriceId",
    pd."Code"            AS "ProductCode",
    pd."DisplayName"     AS "ProductDisplayName",
    spr."Edition",
    spr."LicenseType",
    spr."Seats",
    spr."CreatedAt"
FROM "StripePriceMappings" spr
JOIN "ProductDefinitions" pd ON pd."Id" = spr."ProductDefinitionId"
ORDER BY pd."Code", spr."StripePriceId";

-- ---------------------------------------------------------------------------
-- 3. Subscription mappings (active/issued subscriptions tracked from webhooks)
-- ---------------------------------------------------------------------------
SELECT *
FROM "StripeSubscriptionMappings"
ORDER BY "CreatedAt" DESC;
