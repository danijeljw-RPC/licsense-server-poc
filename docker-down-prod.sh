#!/usr/bin/env sh
# Tears down the production stack started by docker-up-prod.sh.
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"

COMPOSE_FILE=compose.prod.yaml
ENV_FILE=.env.prod

compose() {
    docker compose --file "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"
}

if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
    printf '%s\n' 'Docker Compose v2 was not found.' >&2
    exit 1
fi

if ! docker info >/dev/null 2>&1; then
    printf '%s\n' 'Docker is not reachable. Start the engine before shutting down the stack.' >&2
    exit 1
fi

# Compose interpolates required variables even during shutdown. These inert values are used
# only if .env.prod does not exist and the calling environment does not supply real values.
if [ ! -f "$ENV_FILE" ]; then
    : "${POSTGRES_PASSWORD:=down-only-not-used}"
    : "${DEFAULT_ADMIN_EMAIL:=down-only-not-used@localhost}"
    : "${DEFAULT_ADMIN_PASSWORD:=DownOnly-Not-Used-9xQ}"
    : "${LICENSE_DEFAULT_SIGNING_KEY:=down-only-not-used}"
    : "${ACTIVATION_CODE_PEPPER:=down-only-not-used}"
    : "${API_CREDENTIAL_PEPPER:=down-only-not-used}"
    : "${DEPLOYMENT_KEY_PEPPER:=down-only-not-used}"
    : "${MAILERSEND_API_TOKEN:=down-only-not-used}"
    : "${MAILERSEND_FROM_EMAIL:=down-only-not-used@localhost}"
    : "${MAILERSEND_WEBHOOK_SECRET:=down-only-not-used}"
    : "${STRIPE_API_KEY:=down-only-not-used}"
    : "${STRIPE_WEBHOOK_SECRET:=down-only-not-used}"
    : "${CUSTOMER_PORTAL_PUBLIC_BASE_URL:=https://down-only-not-used.localhost}"
    : "${LICENSE_SIGNING_KEY_DIR:=./keys}"
    : "${IMAGE_TAG:=down-only-not-used}"
    : "${CADDY_DOMAIN:=down-only-not-used.localhost}"
    : "${CADDY_EMAIL:=down-only-not-used@localhost}"
    export POSTGRES_PASSWORD DEFAULT_ADMIN_EMAIL DEFAULT_ADMIN_PASSWORD LICENSE_DEFAULT_SIGNING_KEY \
        ACTIVATION_CODE_PEPPER API_CREDENTIAL_PEPPER DEPLOYMENT_KEY_PEPPER MAILERSEND_API_TOKEN \
        MAILERSEND_FROM_EMAIL MAILERSEND_WEBHOOK_SECRET STRIPE_API_KEY STRIPE_WEBHOOK_SECRET \
        CUSTOMER_PORTAL_PUBLIC_BASE_URL LICENSE_SIGNING_KEY_DIR IMAGE_TAG CADDY_DOMAIN CADDY_EMAIL
fi

if [ "${REMOVE_VOLUMES:-false}" = true ]; then
    printf '%s\n' 'WARNING: deleting PostgreSQL, Data Protection, and Caddy (TLS cert/ACME state) volumes.' >&2
    compose down --remove-orphans --volumes
    printf '%s\n' 'LicenseServer containers, network, and production volumes were removed.'
else
    compose down --remove-orphans
    printf '%s\n' 'LicenseServer containers and networks were removed; persistent volumes were preserved.'
fi
