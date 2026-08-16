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
generator_project="$repository_root/src/LicenseGenerator/LicenseGenerator.csproj"
validator_project="$repository_root/src/LicenseValidator/LicenseValidator.csproj"
primary_private_key="$repository_root/keys/primary-2026.private.pem"
secondary_private_key="$repository_root/keys/secondary-2026.private.pem"
work_directory="$(mktemp -d "${TMPDIR:-/tmp}/software-license-test-XXXXXXXX")"

fail() {
    echo "Error: $1" >&2
    exit 1
}

trim() {
    local s="$1"
    s="${s#"${s%%[![:space:]]*}"}"
    s="${s%"${s##*[![:space:]]}"}"
    printf '%s' "$s"
}

invoke_license_tool() {
    local project="$1" expected_exit="$2"
    shift 2
    local output exit_code
    output="$(dotnet run --no-build --configuration Release --project "$project" -- "$@" 2>&1)"
    exit_code=$?
    if [ "$exit_code" -ne "$expected_exit" ]; then
        fail "Expected exit code $expected_exit, received $exit_code.
$output"
    fi
    trim "$output"
}

assert_equal() {
    local name="$1" actual="$2" expected="$3"
    if [ "$actual" != "$expected" ]; then
        fail "$name failed. Expected '$expected', received '$actual'."
    fi
    echo "PASS  $name"
}

assert_contains() {
    local name="$1" actual="$2" expected_text="$3"
    if ! printf '%s' "$actual" | grep -qF -- "$expected_text"; then
        fail "$name failed. Output did not contain '$expected_text'.
$actual"
    fi
    echo "PASS  $name"
}

cleanup() {
    if [ "$keep_artifacts" -eq 1 ]; then
        echo "Test artifacts kept at: $work_directory"
    else
        rm -rf "$work_directory"
    fi
}
trap cleanup EXIT

for key_path in "$primary_private_key" "$secondary_private_key"; do
    [ -f "$key_path" ] || fail "Required test key does not exist: $key_path"
done

input_path="$work_directory/license-data.json"
primary_license_path="$work_directory/primary.license"
secondary_license_path="$work_directory/secondary.license"
tampered_license_path="$work_directory/tampered.license"
unknown_key_license_path="$work_directory/unknown-key.license"

cat <<'JSON' >"$input_path"
{
  "licenseId": "LIC-TEST-0001",
  "customer": "Example Pty Ltd",
  "issuedAt": "2026-08-12T06:30:00Z",
  "metadata": {
    "purchaseOrder": "PO-88429",
    "contactEmail": "licensing@example.com",
    "contactPerson": "Jane Smith",
    "addressLine1": "100 Example Street",
    "priorityCustomer": true,
    "accountNumber": 4812
  },
  "entitlements": [
    {
      "product": "gcexp",
      "edition": "professional",
      "licenseType": "perpetual",
      "seats": 5,
      "expiresAt": "2099-12-31T23:59:59Z",
      "updatesUntil": "2027-08-12"
    },
    {
      "product": "winupd",
      "edition": "enterprise",
      "licenseType": "subscription",
      "seats": 25
    },
    {
      "product": "processwatch",
      "edition": "standard",
      "licenseType": "trial",
      "seats": 1,
      "expiresAt": "2000-01-01T00:00:00Z"
    }
  ]
}
JSON

echo 'Building projects...'
dotnet build "$repository_root/SoftwareLicensing.slnx" --configuration Release --no-restore
build_exit=$?
if [ "$build_exit" -ne 0 ]; then
    fail "dotnet build failed with exit code $build_exit."
fi

invalid_input_path="$work_directory/invalid-expiry-license-data.json"
jq '.entitlements[0].expiresAt = "2030-12-31 23:59:59"' "$input_path" >"$invalid_input_path"
invalid_expiry_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$invalid_input_path" --output "$work_directory/invalid-expiry.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects expiry without a timezone' "$invalid_expiry_result" "Field 'expiresAt'"

invalid_seats_input_path="$work_directory/invalid-seats-license-data.json"
jq '.entitlements[0].seats = 0' "$input_path" >"$invalid_seats_input_path"
invalid_seats_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$invalid_seats_input_path" --output "$work_directory/invalid-seats.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects non-positive seats' "$invalid_seats_result" "field 'seats'"

invalid_updates_input_path="$work_directory/invalid-updates-license-data.json"
jq '.entitlements[0].updatesUntil = "2027-02-30"' "$input_path" >"$invalid_updates_input_path"
invalid_updates_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$invalid_updates_input_path" --output "$work_directory/invalid-updates.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects impossible update dates' "$invalid_updates_result" 'yyyy-MM-dd'

duplicate_product_input_path="$work_directory/duplicate-product-license-data.json"
jq '.entitlements[1].product = "GCEXP"' "$input_path" >"$duplicate_product_input_path"
duplicate_product_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$duplicate_product_input_path" --output "$work_directory/duplicate-product.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects duplicate products' "$duplicate_product_result" 'more than one entitlement'

top_level_extra_input_path="$work_directory/top-level-extra-license-data.json"
jq '. + {purchaseOrder: "PO-WRONG-LOCATION"}' "$input_path" >"$top_level_extra_input_path"
top_level_extra_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$top_level_extra_input_path" --output "$work_directory/top-level-extra.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects custom top-level fields' "$top_level_extra_result" 'Custom fields are allowed only inside licence metadata'

entitlement_extra_input_path="$work_directory/entitlement-extra-license-data.json"
jq '.entitlements[0] += {purchaseOrder: "PO-WRONG-LOCATION"}' "$input_path" >"$entitlement_extra_input_path"
entitlement_extra_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$entitlement_extra_input_path" --output "$work_directory/entitlement-extra.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects custom entitlement fields' "$entitlement_extra_result" 'Custom fields are allowed only inside licence metadata'

invalid_metadata_name_input_path="$work_directory/invalid-metadata-name-license-data.json"
jq '.metadata += {"Purchase_Order": "PO-INVALID"}' "$input_path" >"$invalid_metadata_name_input_path"
invalid_metadata_name_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$invalid_metadata_name_input_path" --output "$work_directory/invalid-metadata-name.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer enforces lower-camel-case metadata names' "$invalid_metadata_name_result" 'must use lowerCamelCase'

nested_metadata_input_path="$work_directory/nested-metadata-license-data.json"
jq '.metadata += {nestedValue: {value: "not-supported"}}' "$input_path" >"$nested_metadata_input_path"
nested_metadata_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$nested_metadata_input_path" --output "$work_directory/nested-metadata.license" \
    --private-key "$primary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects nested metadata values' "$nested_metadata_result" 'Nested objects, arrays, and null are not supported'

mismatched_key_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$input_path" --output "$work_directory/mismatched-key.license" \
    --private-key "$secondary_private_key" --key-id primary-2026)"
assert_contains 'signer rejects private key and key ID mismatch' "$mismatched_key_result" 'does not match the public key for key ID'

# A key that exists only as a dropped-in PEM pair - never added to TrustedPublicKeys.cs - must be
# usable by the offline signer, since that is the whole point of the key-ring workflow.
dropped_key_directory="$work_directory/dropped-keys"
keygen_result="$(invoke_license_tool "$generator_project" 0 \
    keygen --id dropped-2027 --output "$dropped_key_directory")"
assert_contains 'keygen --id reports the convention private key path' "$keygen_result" "dropped-2027.private.pem"
[ -f "$dropped_key_directory/dropped-2027.private.pem" ] || fail 'keygen --id did not create <keyId>.private.pem'
[ -f "$dropped_key_directory/dropped-2027.public.pem" ] || fail 'keygen --id did not create <keyId>.public.pem'
echo 'PASS  keygen --id writes both halves under the convention names'

# GNU stat first, then BSD/macOS. Order matters: BSD 'stat -f' means --file-system on GNU and
# succeeds while printing something that is not a mode at all, so probing it first would silently
# skip the real check.
file_mode() {
    stat -c '%a' "$1" 2>/dev/null || stat -f '%OLp' "$1" 2>/dev/null
}
dropped_key_mode="$(file_mode "$dropped_key_directory/dropped-2027.private.pem")"
case "$dropped_key_mode" in
    ''|*[!0-7]*) fail "Could not read the file mode of the generated private key (got '$dropped_key_mode')." ;;
esac
assert_equal 'keygen restricts the private key to mode 600' "$dropped_key_mode" '600'

keygen_overwrite_result="$(invoke_license_tool "$generator_project" 1 \
    keygen --id dropped-2027 --output "$dropped_key_directory")"
assert_contains 'keygen --id still refuses to overwrite' "$keygen_overwrite_result" 'already exists'

keygen_bad_id_result="$(invoke_license_tool "$generator_project" 1 \
    keygen --id 'Bad_Id' --output "$dropped_key_directory")"
assert_contains 'keygen rejects key IDs the server could not discover' "$keygen_bad_id_result" "Invalid --id"

# No --key-id: it is derived from the '<keyId>.private.pem' filename.
dropped_license_path="$work_directory/dropped.license"
invoke_license_tool "$generator_project" 0 \
    sign --input "$input_path" --output "$dropped_license_path" \
    --private-key "$dropped_key_directory/dropped-2027.private.pem" >/dev/null
dropped_key_id="$(jq -r '.keyId' "$dropped_license_path")"
assert_equal 'sign derives --key-id from the private key filename' "$dropped_key_id" 'dropped-2027'

# The signature is real: LicenseValidator rejects it only because the key is untrusted there, which
# is the expected embedded-trust behavior, not a signing failure.
dropped_validation_result="$(invoke_license_tool "$validator_project" 1 --license "$dropped_license_path")"
assert_contains 'a dropped-in key signs without TrustedPublicKeys edits' "$dropped_validation_result" 'Unknown signing key: dropped-2027'

undeducible_key_id_result="$(invoke_license_tool "$generator_project" 1 \
    sign --input "$input_path" --output "$work_directory/undeducible.license" \
    --private-key "$input_path")"
assert_contains 'sign explains an underivable key ID' "$undeducible_key_id_result" 'could not be derived'

invoke_license_tool "$generator_project" 0 \
    sign --input "$input_path" --output "$primary_license_path" \
    --private-key "$primary_private_key" --key-id primary-2026 >/dev/null

summary="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path")"
assert_contains 'primary signature validates' "$summary" 'VALID LICENCE'

field_names=('CuStOmEr' 'licenseId' 'issuedat')
field_labels=('customer (case-insensitive)' 'licence ID' 'issued at')
field_expected=('Example Pty Ltd' 'LIC-TEST-0001' '2026-08-12T06:30:00Z')
for i in 0 1 2; do
    actual="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --field "${field_names[$i]}")"
    assert_equal "${field_labels[$i]}" "$actual" "${field_expected[$i]}"
done

metadata_fields=('metadata.purchaseOrder' 'MeTaDaTa.CoNtAcTeMaIl' 'metadata.priorityCustomer' 'metadata.accountNumber')
metadata_labels=('purchase order metadata' 'case-insensitive metadata path' 'boolean metadata' 'numeric metadata')
metadata_expected=('PO-88429' 'licensing@example.com' 'true' '4812')
for i in 0 1 2 3; do
    actual="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --field "${metadata_fields[$i]}")"
    assert_equal "${metadata_labels[$i]}" "$actual" "${metadata_expected[$i]}"
done

entitlement_products=('gcexp' 'winupd')
entitlement_editions=('professional' 'enterprise')
for i in 0 1; do
    product="${entitlement_products[$i]}"
    edition_expected="${entitlement_editions[$i]}"
    product_upper="$(printf '%s' "$product" | tr '[:lower:]' '[:upper:]')"

    edition="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product "$product_upper" --field EdItIoN)"
    assert_equal "$product edition" "$edition" "$edition_expected"

    product_value="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product "$product" --field product)"
    assert_equal "$product entitlement" "$product_value" "$product"
done

gcexp_seats="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product gcexp --field SEATS)"
assert_equal 'per-product seats' "$gcexp_seats" '5'

winupd_type="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product winupd --field licenseType)"
assert_equal 'per-product licence type' "$winupd_type" 'subscription'

expiry="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product gcexp --field expiresat)"
assert_equal 'active product expiry' "$expiry" '2099-12-31T23:59:59Z'

updates_until="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product gcexp --field updatesUntil)"
assert_equal 'per-product updates date' "$updates_until" '2027-08-12'

covered_release="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product gcexp --release-date 2027-08-12)"
assert_contains 'updates-through date is inclusive' "$covered_release" 'VALID LICENCE'

uncovered_release="$(invoke_license_tool "$validator_project" 1 --license "$primary_license_path" --product gcexp --release-date 2027-08-13)"
assert_contains 'newer release is not covered' "$uncovered_release" 'release 2027-08-13 is not covered'

unlimited_updates="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product winupd --release-date 2099-01-01)"
assert_contains 'missing updatesUntil has no update cutoff' "$unlimited_updates" 'VALID LICENCE'

perpetual_product="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --product winupd)"
assert_contains 'product without expiry remains valid' "$perpetual_product" 'VALID LICENCE'

expired_product="$(invoke_license_tool "$validator_project" 1 --license "$primary_license_path" --product processwatch)"
assert_contains 'expired product is rejected' "$expired_product" "Entitlement for 'processwatch' expired at"

expired_field="$(invoke_license_tool "$validator_project" 1 --license "$primary_license_path" --product processwatch --field edition)"
assert_contains 'field reads cannot bypass expiry' "$expired_field" "Entitlement for 'processwatch' expired at"

assert_contains 'summary marks an expired product' "$summary" '[EXPIRED 2000-01-01T00:00:00.0000000+00:00;'

entitlements_json="$(invoke_license_tool "$validator_project" 0 --license "$primary_license_path" --field entitlements)"
entitlements_count="$(printf '%s' "$entitlements_json" | jq 'length')"
assert_equal 'entitlement count' "$entitlements_count" '3'

invoke_license_tool "$generator_project" 0 \
    sign --input "$input_path" --output "$secondary_license_path" \
    --private-key "$secondary_private_key" --key-id secondary-2026 >/dev/null

secondary_summary="$(invoke_license_tool "$validator_project" 0 --license "$secondary_license_path")"
assert_contains 'secondary signature validates' "$secondary_summary" 'Key ID:       secondary-2026'

jq '.license.entitlements[0].seats = 999' "$primary_license_path" >"$tampered_license_path"
tampered_result="$(invoke_license_tool "$validator_project" 1 --license "$tampered_license_path")"
assert_contains 'tampering is rejected' "$tampered_result" 'Cryptographic signature is invalid.'

jq '.keyId = "attacker-key"' "$primary_license_path" >"$unknown_key_license_path"
unknown_key_result="$(invoke_license_tool "$validator_project" 1 --license "$unknown_key_license_path")"
assert_contains 'unknown keys are rejected' "$unknown_key_result" 'Unknown signing key: attacker-key'

public_key_option_result="$(invoke_license_tool "$validator_project" 2 --license "$primary_license_path" --public-key "$repository_root/keys/primary-2026.public.pem")"
assert_contains 'caller-supplied public keys are rejected' "$public_key_option_result" '--public-key is no longer accepted'

echo
echo 'All licence flow tests passed.'
