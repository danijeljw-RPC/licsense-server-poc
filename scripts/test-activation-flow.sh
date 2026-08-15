#!/usr/bin/env bash
set -uo pipefail

usage() {
    echo "Usage: $0 [--keep-artifacts]" >&2
    exit 2
}

keep_artifacts=0
while [ $# -gt 0 ]; do
    case "$1" in
        --keep-artifacts) keep_artifacts=1; shift ;;
        *) usage ;;
    esac
done

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
server_assembly="$repository_root/src/LicenseServer/bin/Release/net10.0/LicenseServer.dll"
validator_project="$repository_root/src/LicenseValidator/LicenseValidator.csproj"
work_directory="$(mktemp -d "${TMPDIR:-/tmp}/software-activation-test-XXXXXXXX")"
port=$((RANDOM % (59000 - 51000 + 1) + 51000))
server_url="http://127.0.0.1:$port"
server_pid=""
database_container="license-activation-test-$(uuidgen | tr -d '-' | tr '[:upper:]' '[:lower:]')"
database_port=$((RANDOM % (50999 - 49152 + 1) + 49152))
database_password="$(openssl rand -base64 24)"
admin_password='LocalAdmin!7Kp9-Vx3-Rm8-Qz2'
changed_admin_password='ChangedLocalAdmin!8Lp4-Wy6-Sn9-Rt3'
cookie_jar="$work_directory/admin-session.cookies"

fail() {
    echo "Error: $1" >&2
    exit 1
}

assert_equal() {
    local name="$1" actual="$2" expected="$3"
    if [ "$actual" != "$expected" ]; then
        fail "$name failed. Expected '$expected', received '$actual'."
    fi
    echo "PASS  $name"
}

html_decode() {
    sed -e 's/&amp;/\&/g' -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&quot;/"/g' -e "s/&#39;/'/g"
}

extract_token() {
    sed -nE 's/.*name="__RequestVerificationToken"[^>]*value="([^"]*)".*/\1/p' "$1" | head -n1 | html_decode
}

new_activation_request() {
    local device_id="$1" device_name="$2" mode="$3"
    local request_id activation_token
    request_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
    activation_token="$(openssl rand -base64 32)"
    jq -n \
        --arg requestId "$request_id" \
        --arg activationCode 'POC-DEMO-ACTIVATION-CODE' \
        --arg activationToken "$activation_token" \
        --arg mode "$mode" \
        --arg deviceId "$device_id" \
        --arg deviceName "$device_name" \
        '{
            requestId: $requestId,
            activationCode: $activationCode,
            activationToken: $activationToken,
            mode: $mode,
            device: {
                scheme: "os-machine-id-sha256-v1",
                deviceId: $deviceId,
                deviceName: $deviceName
            }
        }'
}

credential_for() {
    local request_json="$1" device_id="$2"
    jq -n --arg activationToken "$(printf '%s' "$request_json" | jq -r '.activationToken')" --arg deviceId "$device_id" \
        '{activationToken: $activationToken, deviceId: $deviceId}'
}

call_api() {
    local method="$1" path="$2" body="$3" out_file="$4"
    if [ -n "$body" ]; then
        curl -s -o "$out_file" -w '%{http_code}' -X "$method" -H 'Content-Type: application/json' --data "$body" "${server_url}${path}"
    else
        curl -s -o "$out_file" -w '%{http_code}' -X "$method" "${server_url}${path}"
    fi
}

cleanup() {
    unset ConnectionStrings__DefaultConnection SEED_DEFAULT_ADMIN DEFAULT_ADMIN_EMAIL \
        DEFAULT_ADMIN_PASSWORD SEED_DEMO_LICENSE ASPNETCORE_ENVIRONMENT \
        Licensing__KeyDirectory Licensing__DefaultSigningKey

    if [ -n "$server_pid" ] && kill -0 "$server_pid" 2>/dev/null; then
        kill -9 "$server_pid" 2>/dev/null || true
        local waited=0
        while kill -0 "$server_pid" 2>/dev/null && [ "$waited" -lt 50 ]; do
            sleep 0.1
            waited=$((waited + 1))
        done
        if kill -0 "$server_pid" 2>/dev/null; then
            echo "Warning: Test server process $server_pid did not exit within five seconds." >&2
        fi
    fi

    if [ "$keep_artifacts" -eq 1 ]; then
        echo "Test artifacts kept at: $work_directory"
    elif [ -e "$work_directory" ]; then
        local attempt=1
        while [ "$attempt" -le 10 ]; do
            if rm -rf "$work_directory" 2>/dev/null; then
                break
            fi
            if [ "$attempt" -eq 10 ]; then
                echo "Error: failed to remove work directory $work_directory" >&2
            fi
            sleep 0.1
            attempt=$((attempt + 1))
        done
    fi

    docker rm --force "$database_container" >/dev/null 2>&1 || true
}
trap cleanup EXIT

stdout_log="$work_directory/server.stdout.log"
stderr_log="$work_directory/server.stderr.log"

if ! docker run --detach --name "$database_container" \
    --publish "127.0.0.1:${database_port}:5432" \
    --env POSTGRES_DB=activation_test \
    --env POSTGRES_USER=license_test \
    --env "POSTGRES_PASSWORD=$database_password" \
    --health-cmd 'pg_isready -U license_test -d activation_test' \
    --health-interval 1s --health-timeout 3s --health-retries 30 \
    postgres:18-alpine >/dev/null; then
    fail 'Could not start isolated PostgreSQL.'
fi

healthy=0
for _ in {1..40}; do
    status="$(docker inspect --format '{{.State.Health.Status}}' "$database_container" 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then
        healthy=1
        break
    fi
    sleep 0.5
done
[ "$healthy" -eq 1 ] || fail 'Isolated PostgreSQL did not become healthy.'

if ! dotnet build "$repository_root/SoftwareLicensing.slnx" --configuration Release --no-restore; then
    fail 'Build failed.'
fi

export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=$database_port;Database=activation_test;Username=license_test;Password=$database_password"
export SEED_DEFAULT_ADMIN='true'
export DEFAULT_ADMIN_EMAIL='admin@localhost.com'
export DEFAULT_ADMIN_PASSWORD="$admin_password"
export SEED_DEMO_LICENSE='true'
export ASPNETCORE_ENVIRONMENT='Development'
export Licensing__KeyDirectory="$repository_root/keys"
export Licensing__DefaultSigningKey='primary-2026'

pushd "$repository_root" >/dev/null || fail "Could not change directory to $repository_root."
dotnet "$server_assembly" --urls "$server_url" >"$stdout_log" 2>"$stderr_log" &
server_pid=$!
popd >/dev/null || true

ready=0
for _ in {1..40}; do
    sleep 0.25
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 1 "$server_url/health" 2>/dev/null || true)"
    if [ "$code" = "200" ]; then
        ready=1
        break
    fi
done
if [ "$ready" -ne 1 ]; then
    fail "Server did not start.
$(cat "$stderr_log" 2>/dev/null || true)"
fi

sql="SELECT l.\"LicenseId\" FROM \"Licenses\" l JOIN \"Customers\" c ON c.\"Id\" = l.\"CustomerId\" WHERE c.\"ExternalId\" = 'POC-CUSTOMER';"
demo_license_id="$(docker exec "$database_container" psql --username license_test --dbname activation_test --tuples-only --no-align --command "$sql" | tr -d '[:space:]')"
if ! [[ "$demo_license_id" =~ ^LIC-[0-9]{4}-[0-9]{4}[A-F0-9]{6}$ ]]; then
    fail "The generated demo license ID could not be read from PostgreSQL: '$demo_license_id'."
fi

curl -s -c "$cookie_jar" "$server_url/Account/Login" -o "$work_directory/login-page.html"
login_token="$(extract_token "$work_directory/login-page.html")"
curl -s -o /dev/null -c "$cookie_jar" -b "$cookie_jar" \
    --data-urlencode "__RequestVerificationToken=$login_token" \
    --data-urlencode "_handler=login" \
    --data-urlencode "Input.Email=admin@localhost.com" \
    --data-urlencode "Input.Password=$admin_password" \
    --data-urlencode "Input.RememberMe=false" \
    "$server_url/Account/Login"

curl -s -c "$cookie_jar" -b "$cookie_jar" "$server_url/Account/Manage/ChangePassword?forced=true" -o "$work_directory/change-password-page.html"
change_token="$(extract_token "$work_directory/change-password-page.html")"
curl -s -o /dev/null -c "$cookie_jar" -b "$cookie_jar" \
    --data-urlencode "__RequestVerificationToken=$change_token" \
    --data-urlencode "_handler=change-password" \
    --data-urlencode "Input.OldPassword=$admin_password" \
    --data-urlencode "Input.NewPassword=$changed_admin_password" \
    --data-urlencode "Input.ConfirmPassword=$changed_admin_password" \
    "$server_url/Account/Manage/ChangePassword?forced=true"

if ! device_json="$(dotnet run --no-build --configuration Release --project "$validator_project" -- --device-id)"; then
    fail 'Could not obtain the local device ID.'
fi
device_id="$(printf '%s' "$device_json" | jq -r '.DeviceId')"
device_name="$(printf '%s' "$device_json" | jq -r '.DeviceName')"

first_request="$(new_activation_request "$device_id" "$device_name" online)"
first_status="$(call_api POST "/api/v1/licenses/$demo_license_id/activate" "$first_request" "$work_directory/first-response.json")"
assert_equal 'online activation succeeds' "$first_status" '200'
first_activation_id="$(jq -r '.activationId' "$work_directory/first-response.json")"

online_license="$work_directory/online.license"
jq -r '.signedLicense' "$work_directory/first-response.json" >"$online_license"
dotnet run --no-build --configuration Release --project "$validator_project" -- --license "$online_license" --product gcexp
assert_equal 'device-bound online licence validates locally' "$?" '0'

other_device_id="$(printf 'A%.0s' {1..64})"
blocked_request="$(new_activation_request "$other_device_id" 'TRANSFER-TARGET' online)"
blocked_status="$(call_api POST "/api/v1/licenses/$demo_license_id/activate" "$blocked_request" "$work_directory/blocked-response.json")"
assert_equal 'transfer is blocked before deactivation' "$blocked_status" '409'

credential="$(credential_for "$first_request" "$device_id")"
validated_status="$(call_api POST "/api/v1/activations/$first_activation_id/validate" "$credential" "$work_directory/validated-response.json")"
assert_equal 'server confirms activation' "$validated_status" '200'

refreshed_status="$(call_api POST "/api/v1/activations/$first_activation_id/refresh" "$credential" "$work_directory/refreshed-response.json")"
assert_equal 'online lease refresh succeeds' "$refreshed_status" '200'

deactivated_status="$(call_api POST "/api/v1/activations/$first_activation_id/deactivate" "$credential" "$work_directory/deactivated-response.json")"
assert_equal 'authenticated deactivation succeeds' "$deactivated_status" '200'

transferred_status="$(call_api POST "/api/v1/licenses/$demo_license_id/activate" "$blocked_request" "$work_directory/transferred-response.json")"
assert_equal 'activation transfers after deactivation' "$transferred_status" '200'
transferred_activation_id="$(jq -r '.activationId' "$work_directory/transferred-response.json")"

transferred_license="$work_directory/transferred.license"
jq -r '.signedLicense' "$work_directory/transferred-response.json" >"$transferred_license"
mismatch_output="$(dotnet run --no-build --configuration Release --project "$validator_project" -- --license "$transferred_license" --product gcexp 2>&1)"
mismatch_exit=$?
assert_equal 'copied licence fails on the wrong device' "$mismatch_exit" '1'
case "$mismatch_output" in
    *'different device'*) ;;
    *) fail 'Wrong-device validation did not report a device mismatch.' ;;
esac

transfer_credential="$(credential_for "$blocked_request" "$other_device_id")"
call_api POST "/api/v1/activations/$transferred_activation_id/deactivate" "$transfer_credential" "$work_directory/transfer-deactivate-response.json" >/dev/null

offline_request="$(new_activation_request "$device_id" "$device_name" offline)"
offline_status="$(call_api POST "/api/v1/licenses/$demo_license_id/activate" "$offline_request" "$work_directory/offline-response.json")"
assert_equal 'offline request receives signed response' "$offline_status" '200'
offline_activation_id="$(jq -r '.activationId' "$work_directory/offline-response.json")"
lease_expires_at="$(jq -r '.leaseExpiresAt' "$work_directory/offline-response.json")"
assert_equal 'offline licence has no lease cutoff' 'null' "$lease_expires_at"

offline_license="$work_directory/offline.license"
jq -r '.signedLicense' "$work_directory/offline-response.json" >"$offline_license"
dotnet run --no-build --configuration Release --project "$validator_project" -- --license "$offline_license" --product gcexp
assert_equal 'offline device-bound licence validates without server' "$?" '0'

csrf_token="$(curl -s -b "$cookie_jar" -c "$cookie_jar" "$server_url/api/v1/admin/antiforgery" | jq -r '.requestToken')"
curl -s -b "$cookie_jar" -c "$cookie_jar" "$server_url/api/v1/admin/licenses/$demo_license_id" -o "$work_directory/license-detail.json"
revoke_body="$(jq -c --arg reason 'integration test' '{confirmed: true, reason: $reason, version: .version}' "$work_directory/license-detail.json")"
revoked_status="$(curl -s -o "$work_directory/revoked-response.json" -w '%{http_code}' -X POST -b "$cookie_jar" -c "$cookie_jar" \
    -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $csrf_token" --data "$revoke_body" \
    "$server_url/api/v1/admin/licenses/$demo_license_id/revoke")"
if [ "$revoked_status" != "200" ]; then
    echo "Revocation response: $(cat "$work_directory/revoked-response.json")"
fi
assert_equal 'admin revocation succeeds' "$revoked_status" '200'

offline_credential="$(credential_for "$offline_request" "$device_id")"
server_rejects_status="$(call_api POST "/api/v1/activations/$offline_activation_id/validate" "$offline_credential" "$work_directory/server-rejects-response.json")"
assert_equal 'server rejects revoked activation' "$server_rejects_status" '403'

dotnet run --no-build --configuration Release --project "$validator_project" -- --license "$offline_license" --product gcexp >/dev/null
assert_equal 'offline file remains valid until it reconnects' "$?" '0'

echo
echo 'All activation, transfer, revocation, and offline tests passed.'
