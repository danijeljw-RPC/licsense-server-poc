#!/usr/bin/env bash
set -uo pipefail

usage() {
    echo "Usage: $0 [--keep-container] [--test-filter <filter>]" >&2
    exit 2
}

keep_container=0
test_filter=""

while [ $# -gt 0 ]; do
    case "$1" in
        --keep-container)
            keep_container=1
            shift
            ;;
        --test-filter)
            test_filter="$2"
            shift 2
            ;;
        *)
            usage
            ;;
    esac
done

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container_name="license-server-tests-$(uuidgen | tr -d '-' | tr '[:upper:]' '[:lower:]')"
port=$((RANDOM % (60000 - 49152 + 1) + 49152))
password="$(openssl rand -base64 24)"

cleanup() {
    unset TEST_POSTGRES_CONNECTION
    if [ "$keep_container" -eq 1 ]; then
        echo "Test container kept: $container_name"
    else
        docker rm --force "$container_name" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

if ! docker run --detach --name "$container_name" \
    --publish "127.0.0.1:${port}:5432" \
    --env POSTGRES_DB=license_tests \
    --env POSTGRES_USER=license_test \
    --env "POSTGRES_PASSWORD=$password" \
    --health-cmd 'pg_isready -U license_test -d license_tests' \
    --health-interval 1s --health-timeout 3s --health-retries 30 \
    postgres:18-alpine >/dev/null; then
    echo "Error: Unable to start the isolated PostgreSQL test container." >&2
    exit 1
fi

healthy=0
for _ in $(seq 1 40); do
    status="$(docker inspect --format '{{.State.Health.Status}}' "$container_name" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then
        healthy=1
        break
    fi
    if [ "$status" = "unhealthy" ]; then
        break
    fi
    sleep 0.5
done
if [ "$healthy" -ne 1 ]; then
    echo "Error: PostgreSQL did not become healthy." >&2
    docker logs "$container_name" 2>&1 >&2 || true
    exit 1
fi

export TEST_POSTGRES_CONNECTION="Host=127.0.0.1;Port=$port;Database=license_tests;Username=license_test;Password=$password"

test_arguments=(
    test
    "$repository_root/tests/LicenseServer.Tests/LicenseServer.Tests.csproj"
    --configuration Release
    --no-restore
    -p:AnalysisMode=Recommended
)
if [ -n "$test_filter" ]; then
    test_arguments+=(--filter "$test_filter")
fi

if ! dotnet "${test_arguments[@]}"; then
    echo "Error: Selected tests failed." >&2
    exit 1
fi
