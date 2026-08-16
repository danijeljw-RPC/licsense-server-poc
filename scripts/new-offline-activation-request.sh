#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: $0 --license-id <id> [--activation-code <code>] [--output <path>]" >&2
    exit 2
}

license_id=""
activation_code="POC-DEMO-ACTIVATION-CODE"
output_path="artifacts/offline-activation-request.json"

while [ $# -gt 0 ]; do
    case "$1" in
        --license-id)
            license_id="$2"
            shift 2
            ;;
        --activation-code)
            activation_code="$2"
            shift 2
            ;;
        --output)
            output_path="$2"
            shift 2
            ;;
        *)
            usage
            ;;
    esac
done

if [ -z "$license_id" ]; then
    echo "Error: --license-id is required." >&2
    usage
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
validator_project="$repository_root/src/LicenseValidator/LicenseValidator.csproj"

case "$output_path" in
    /*) absolute_output="$output_path" ;;
    *) absolute_output="$repository_root/$output_path" ;;
esac

if ! device_json="$(dotnet run --no-restore --project "$validator_project" -- --device-id)"; then
    echo "Error: Could not determine the local device identifier." >&2
    exit 1
fi

request_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
activation_token="$(openssl rand -base64 32)"

request="$(printf '%s' "$device_json" | jq \
    --arg requestId "$request_id" \
    --arg activationCode "$activation_code" \
    --arg activationToken "$activation_token" \
    '{
        requestId: $requestId,
        activationCode: $activationCode,
        activationToken: $activationToken,
        mode: "offline",
        device: {
            scheme: .Scheme,
            deviceId: .DeviceId,
            deviceName: .DeviceName
        }
    }')"

mkdir -p "$(dirname "$absolute_output")"

printf '%s\n' "$request" >"$absolute_output"
echo "Offline activation request written to: $absolute_output"
echo "Warning: This request contains activation credentials. Transfer and store it securely." >&2
