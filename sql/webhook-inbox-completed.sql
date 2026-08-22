-- List WebhookInbox rows that have finished processing successfully.
--
-- How to run (production, via docker compose)
-- ---------------------------------------------
-- Run from the repo root, where compose.prod.yaml lives:
--
--   set -a; source .env.prod; set +a
--   docker compose --env-file .env.prod -f compose.prod.yaml exec -T postgres \
--     psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
--     -f - < sql/webhook-inbox-completed.sql

SELECT "Id", "ProviderEventId", "EventType", "Status", "AttemptCount",
       "NextAttemptAt", "LastErrorCode", "ProcessedAt"
FROM "WebhookInbox"
WHERE "Status" = 'completed'
ORDER BY "ProcessedAt" DESC;
