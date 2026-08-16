#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
solution="$repository_root/SoftwareLicensing.slnx"
generator_project="$repository_root/src/LicenseGenerator/LicenseGenerator.csproj"
validator_project="$repository_root/src/LicenseValidator/LicenseValidator.csproj"
licenses_directory="$repository_root/licenses"

invoke_tool() {
    local project="$1"
    shift
    dotnet run --no-build --configuration Release --project "$project" -- "$@"
}

mkdir -p "$licenses_directory"

dotnet build "$solution" --configuration Release

demo_names=(primary-2026 secondary-2026)
demo_inputs=("$repository_root/input/demo-primary-license-data.json" "$repository_root/input/demo-secondary-license-data.json")
demo_outputs=("$licenses_directory/demo-primary.license" "$licenses_directory/demo-secondary.license")
demo_private_keys=("$repository_root/keys/primary-2026.private.pem" "$repository_root/keys/secondary-2026.private.pem")
demo_products=(gcexp winupd)
demo_release_dates=(2028-12-31 2030-12-31)

for i in 0 1; do
    name="${demo_names[$i]}"
    input="${demo_inputs[$i]}"
    output="${demo_outputs[$i]}"
    private_key="${demo_private_keys[$i]}"
    product="${demo_products[$i]}"
    release_date="${demo_release_dates[$i]}"

    echo
    echo "Signing demo with $name..."

    invoke_tool "$generator_project" \
        sign \
        --input "$input" \
        --output "$output" \
        --private-key "$private_key" \
        --key-id "$name"

    echo "Validating $product without supplying a public key..."

    invoke_tool "$validator_project" \
        --license "$output" \
        --product "$product" \
        --release-date "$release_date"
done

echo
echo 'Both demo licences were generated and validated successfully.'
echo "Primary:   ${demo_outputs[0]}"
echo "Secondary: ${demo_outputs[1]}"
