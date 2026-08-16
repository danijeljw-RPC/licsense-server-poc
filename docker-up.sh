#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"

WAIT_TIMEOUT_SECONDS=${WAIT_TIMEOUT_SECONDS:-240}
NO_BUILD=${NO_BUILD:-false}
DOCKER_COMMAND=docker

docker_cli() {
    "$DOCKER_COMMAND" "$@"
}

fail_docker_connection() {
    printf '%s\n' 'Docker is installed, but its Linux engine is not reachable.' >&2
    if grep -qi microsoft /proc/version 2>/dev/null; then
        printf '%s\n' \
            'This shell is running in WSL. Start Docker Desktop in Windows, then:' \
            '  1. Open Docker Desktop Settings > Resources > WSL integration.' \
            '  2. Enable integration for this WSL distribution and apply/restart.' \
            '  3. Run `wsl --shutdown` from Windows PowerShell, reopen WSL, and retry.' >&2
    else
        printf '%s\n' \
            'Start the native daemon with `sudo systemctl start docker` (systemd)' \
            'or `sudo service docker start`, then retry.' >&2
    fi
    printf '%s\n' 'If the error says permission denied, add your user to the docker group and start a new login session.' >&2
    exit 1
}

if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    DOCKER_COMMAND=docker
elif command -v docker.exe >/dev/null 2>&1 && docker.exe compose version >/dev/null 2>&1; then
    DOCKER_COMMAND=docker.exe
    printf '%s\n' 'Native WSL Docker integration is unavailable; using Docker Desktop through docker.exe.'
else
    printf '%s\n' 'Docker Compose v2 was not found. Enable Docker Desktop WSL integration or install the Compose v2 plugin.' >&2
    exit 1
fi

if ! docker_cli info >/dev/null 2>&1; then
    fail_docker_connection
fi

if [ ! -f .env ]; then
    cp .env.example .env
    printf '%s\n' 'Created .env from .env.example. Replace its example passwords, then run this script again.' >&2
    exit 1
fi

if [ ! -f keys/primary-2026.private.pem ]; then
    printf '%s\n' 'The development signing key keys/primary-2026.private.pem is missing.' >&2
    exit 1
fi

if grep -q '^POSTGRES_PASSWORD=local-db-password-change-me$' .env && [ -z "${POSTGRES_PASSWORD:-}" ]; then
    printf '%s\n' 'WARNING: POSTGRES_PASSWORD still uses the disposable .env.example value.' >&2
fi

docker_cli compose config --quiet

# Build through the engine directly, bypassing Compose Bake/buildx driver metadata.
if [ "$NO_BUILD" != true ]; then
    docker_cli build --tag license-server-app .
fi

if ! docker_cli compose up --detach --no-build --wait --wait-timeout "$WAIT_TIMEOUT_SECONDS"; then
    docker_cli compose ps || true
    LOGS=$(docker_cli compose logs --tail 80 app postgres 2>&1 || true)
    printf '%s\n' "$LOGS" >&2
    if printf '%s' "$LOGS" | grep -q 'password authentication failed'; then
        printf '%s\n' 'The existing PostgreSQL volume was initialized with a different POSTGRES_PASSWORD.' \
            'Restore the original value in .env, or intentionally reset local data with `REMOVE_VOLUMES=true sh ./docker-down.sh`.' >&2
    fi
    exit 1
fi

docker_cli compose ps
if [ -z "${APP_PORT:-}" ]; then
    APP_PORT=$(sed -n 's/^[[:space:]]*APP_PORT[[:space:]]*=[[:space:]]*\([^#[:space:]]*\).*/\1/p' .env | tail -n 1)
fi
APP_PORT=${APP_PORT:-8080}
printf '\nLicenseServer is healthy: http://localhost:%s\n' "$APP_PORT"
