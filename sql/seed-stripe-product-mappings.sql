-- Seed example data for Stripe -> license term mapping.
--
-- Populates:
--   1. "ProductDefinitions"     - the internal product catalog (Code/DisplayName)
--   2. "StripeProductMappings"  - Stripe product ID -> license terms, used by
--                                 checkout.session.completed / async_payment_succeeded
--                                 to issue licenses automatically.
--
-- Safe to re-run: every INSERT uses ON CONFLICT so re-importing this file will not
-- create duplicates or error out. Existing rows for the same Code / StripeProductId
-- are left untouched (see the "Re-importing" note at the bottom if you want upserts
-- instead).
--
-- Replace the example "prod_..." Stripe product IDs below with your real ones from
-- the Stripe Dashboard (Products -> [product] -> API ID) before relying on this data.

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Product catalog entries (skipped if a product with the same Code exists)
-- ---------------------------------------------------------------------------

INSERT INTO "ProductDefinitions" ("Id", "Code", "DisplayName", "Description", "IsActive", "CreatedAt", "UpdatedAt")
VALUES
    (gen_random_uuid(), 'cursdel2', 'Cursor Deluxe 2',
     'Perpetual desktop license, sold both as an enterprise seat pack and an education edition.',
     true, now(), now()),
    (gen_random_uuid(), 'cursdel2-trial', 'Cursor Deluxe 2 Trial',
     'Time-limited evaluation edition of Cursor Deluxe 2.',
     true, now(), now()),
    (gen_random_uuid(), 'backup-pro', 'Backup Pro',
     'Perpetual license with a fixed updates-included window.',
     true, now(), now())
ON CONFLICT ("Code") DO NOTHING;

-- ---------------------------------------------------------------------------
-- 2. Stripe product ID -> license terms
--
--    Edition/LicenseType/Seats must be either ALL set (one-time purchase
--    mapping) or ALL left NULL (subscription-only mapping, used only to
--    resolve the product; edition/type/seats then come from the matching
--    StripePriceMapping row instead). Edition must be one of: community,
--    project, education, consumer, business, smb, enterprise, corporate.
--    LicenseType must be one of: perpetual, subscription, evaluation.
--    ExpiresAt = NULL means perpetual/never-expiring.
-- ---------------------------------------------------------------------------

INSERT INTO "StripeProductMappings"
    ("Id", "StripeProductId", "ProductDefinitionId", "Edition", "LicenseType", "Seats", "UpdatesUntil", "ExpiresAt", "CreatedAt", "UpdatedAt")
VALUES
    -- Example 1: one-time purchase, enterprise edition, 500 seats, perpetual (never expires)
    (gen_random_uuid(), 'prod_V5Q67Ol2Jm4MaL',
     (SELECT "Id" FROM "ProductDefinitions" WHERE "Code" = 'cursdel2'),
     'enterprise', 'perpetual', 500, NULL, NULL, now(), now()),

    -- Example 2: same product, sold under a different Stripe product as the education edition
    (gen_random_uuid(), 'prod_V5Q67Ol33ff3er',
     (SELECT "Id" FROM "ProductDefinitions" WHERE "Code" = 'cursdel2'),
     'education', 'perpetual', 20, NULL, NULL, now(), now()),

    -- Example 3: one-time purchase, evaluation edition, 5 seats, expires on a fixed date
    (gen_random_uuid(), 'prod_EVALTRIAL0001',
     (SELECT "Id" FROM "ProductDefinitions" WHERE "Code" = 'cursdel2-trial'),
     'business', 'evaluation', 5, '2027-01-01', '2027-01-01T00:00:00+00', now(), now()),

    -- Example 4: one-time purchase, perpetual license, but free updates only until a cutoff date
    (gen_random_uuid(), 'prod_BACKUPPRO0001',
     (SELECT "Id" FROM "ProductDefinitions" WHERE "Code" = 'backup-pro'),
     'smb', 'perpetual', 10, '2027-08-22', NULL, now(), now()),

    -- Example 5: subscription-only mapping - Edition/LicenseType/Seats intentionally NULL.
    -- Used only so the webhook can resolve which product a *subscription* checkout is
    -- for; actual edition/license type/seats for subscriptions come from the matching
    -- row in "StripePriceMappings" (keyed on Stripe price ID), not from this table.
    (gen_random_uuid(), 'prod_SUBSCRIPTION01',
     (SELECT "Id" FROM "ProductDefinitions" WHERE "Code" = 'cursdel2'),
     NULL, NULL, NULL, NULL, NULL, now(), now())
ON CONFLICT ("StripeProductId") DO NOTHING;

COMMIT;

-- ---------------------------------------------------------------------------
-- Re-importing / updating existing rows
--
-- The ON CONFLICT DO NOTHING clauses above make this file safe to re-run, but
-- they also mean editing a value in this file and re-running it will NOT
-- update an already-imported row. To upsert instead, replace
-- "ON CONFLICT (...) DO NOTHING" with, for example:
--
--   ON CONFLICT ("StripeProductId") DO UPDATE SET
--       "ProductDefinitionId" = EXCLUDED."ProductDefinitionId",
--       "Edition"             = EXCLUDED."Edition",
--       "LicenseType"         = EXCLUDED."LicenseType",
--       "Seats"               = EXCLUDED."Seats",
--       "UpdatesUntil"        = EXCLUDED."UpdatesUntil",
--       "ExpiresAt"           = EXCLUDED."ExpiresAt",
--       "UpdatedAt"           = now();
--
-- Or manage these rows going forward through the admin UI instead:
-- Settings -> Stripe product mappings (requires the billing.manage permission).
