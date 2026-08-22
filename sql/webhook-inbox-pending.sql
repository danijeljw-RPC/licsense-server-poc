-- List WebhookInbox rows that are still waiting to be processed.
-- "Waiting" here covers every non-terminal status: pending, leased, retry,
-- and categorized. Terminal statuses (completed, ignored, quarantined,
-- dead_letter) are excluded — see webhook-inbox-completed.sql for the
-- successful ones, or query "Status" = 'quarantined' directly for stuck ones.
--
-- How to run (production, via docker compose)
-- ---------------------------------------------
-- Run from the repo root, where compose.prod.yaml lives:
--
--   set -a; source .env.prod; set +a
--   docker compose --env-file .env.prod -f compose.prod.yaml exec -T postgres \
--     psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
--     -f - < sql/webhook-inbox-pending.sql

SELECT "Id", "ProviderEventId", "EventType", "Status", "AttemptCount",
       "NextAttemptAt", "LastErrorCode", "ReceivedAt"
FROM "WebhookInbox"
WHERE "Status" IN ('pending', 'leased', 'retry', 'categorized')
ORDER BY "ReceivedAt";
