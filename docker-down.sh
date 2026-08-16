#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"
DOCKER_COMMAND=docker

docker_cli() {
    "$DOCKER_COMMAND" "$@"
}

if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    DOCKER_COMMAND=docker
elif command -v docker.exe >/dev/null 2>&1 && docker.exe compose version >/dev/null 2>&1; then
    DOCKER_COMMAND=docker.exe
    printf '%s\n' 'Native WSL Docker integration is unavailable; using Docker Desktop through docker.exe.'
else
    printf '%s\n' 'Docker Compose v2 was not found.' >&2
    exit 1
fi

if ! docker_cli info >/dev/null 2>&1; then
    printf '%s\n' 'Docker is not reachable. Start Docker Desktop or the native Docker daemon before shutting down the stack.' >&2
    exit 1
fi

# Compose interpolates required variables even during shutdown. These inert values
# are used only if .env and the calling environment do not supply real values.
if [ ! -f .env ]; then
    : "${POSTGRES_PASSWORD:=down-only-not-used}"
    : "${DEFAULT_ADMIN_PASSWORD:=DownOnly-Not-Used-9xQ}"
    : "${LICENSE_SIGNING_KEY_DIR:=./keys}"
    export POSTGRES_PASSWORD DEFAULT_ADMIN_PASSWORD LICENSE_SIGNING_KEY_DIR
fi

if [ "${REMOVE_VOLUMES:-false}" = true ]; then
    printf '%s\n' 'WARNING: deleting PostgreSQL and Data Protection volumes.' >&2
    docker_cli compose down --remove-orphans --volumes
    printf '%s\n' 'LicenseServer containers, network, and development volumes were removed.'
else
    docker_cli compose down --remove-orphans
    printf '%s\n' 'LicenseServer containers and network were removed; persistent volumes were preserved.'
fi
