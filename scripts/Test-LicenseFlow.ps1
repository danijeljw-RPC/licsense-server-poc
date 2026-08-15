[CmdletBinding()]
param(
    [switch] $KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$generatorProject = Join-Path $repositoryRoot 'src\LicenseGenerator\LicenseGenerator.csproj'
$validatorProject = Join-Path $repositoryRoot 'src\LicenseValidator\LicenseValidator.csproj'
$primaryPrivateKey = Join-Path $repositoryRoot 'keys\primary-2026.private.pem'
$secondaryPrivateKey = Join-Path $repositoryRoot 'keys\secondary-2026.private.pem'
$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ("software-license-test-" + [Guid]::NewGuid())

function Invoke-LicenseTool {
    param(
        [Parameter(Mandatory)]
        [string] $Project,

        [Parameter(Mandatory)]
        [string[]] $ToolArguments,

        [int] $ExpectedExitCode = 0
    )

    $output = @(
        & dotnet run `
            --no-build `
            --configuration Release `
            --project $Project `
            -- @ToolArguments 2>&1
    )
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine

    if ($exitCode -ne $ExpectedExitCode) {
        throw "Expected exit code $ExpectedExitCode, received $exitCode.`n$text"
    }

    return $text.Trim()
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [AllowEmptyString()]
        [string] $Actual,

        [AllowEmptyString()]
        [string] $Expected
    )

    if ($Actual -cne $Expected) {
        throw "$Name failed. Expected '$Expected', received '$Actual'."
    }

    Write-Host "PASS  $Name"
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Actual,

        [Parameter(Mandatory)]
        [string] $ExpectedText
    )

    if (-not $Actual.Contains($ExpectedText, [StringComparison]::Ordinal)) {
        throw "$Name failed. Output did not contain '$ExpectedText'.`n$Actual"
    }

    Write-Host "PASS  $Name"
}

try {
    foreach ($keyPath in @($primaryPrivateKey, $secondaryPrivateKey)) {
        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw "Required test key does not exist: $keyPath"
        }
    }

    New-Item -ItemType Directory -Path $workDirectory | Out-Null

    $inputPath = Join-Path $workDirectory 'license-data.json'
    $primaryLicensePath = Join-Path $workDirectory 'primary.license'
    $secondaryLicensePath = Join-Path $workDirectory 'secondary.license'
    $invalidInputPath = Join-Path $workDirectory 'invalid-expiry-license-data.json'
    $tamperedLicensePath = Join-Path $workDirectory 'tampered.license'
    $unknownKeyLicensePath = Join-Path $workDirectory 'unknown-key.license'

    @'
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
'@ | Set-Content -LiteralPath $inputPath -Encoding utf8NoBOM

    Write-Host 'Building projects...'
    & dotnet build (Join-Path $repositoryRoot 'SoftwareLicensing.slnx') --configuration Release --no-restore

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $invalidInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $invalidInput.entitlements[0].expiresAt = '2030-12-31 23:59:59'
    $invalidInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $invalidInputPath -Encoding utf8NoBOM

    $invalidExpiryResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $invalidInputPath,
        '--output', (Join-Path $workDirectory 'invalid-expiry.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects expiry without a timezone' $invalidExpiryResult "Field 'expiresAt'"

    $invalidSeatsInputPath = Join-Path $workDirectory 'invalid-seats-license-data.json'
    $invalidSeatsInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $invalidSeatsInput.entitlements[0].seats = 0
    $invalidSeatsInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $invalidSeatsInputPath -Encoding utf8NoBOM
    $invalidSeatsResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $invalidSeatsInputPath,
        '--output', (Join-Path $workDirectory 'invalid-seats.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects non-positive seats' $invalidSeatsResult "field 'seats'"

    $invalidUpdatesInputPath = Join-Path $workDirectory 'invalid-updates-license-data.json'
    $invalidUpdatesInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $invalidUpdatesInput.entitlements[0].updatesUntil = '2027-02-30'
    $invalidUpdatesInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $invalidUpdatesInputPath -Encoding utf8NoBOM
    $invalidUpdatesResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $invalidUpdatesInputPath,
        '--output', (Join-Path $workDirectory 'invalid-updates.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects impossible update dates' $invalidUpdatesResult 'yyyy-MM-dd'

    $duplicateProductInputPath = Join-Path $workDirectory 'duplicate-product-license-data.json'
    $duplicateProductInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $duplicateProductInput.entitlements[1].product = 'GCEXP'
    $duplicateProductInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $duplicateProductInputPath -Encoding utf8NoBOM
    $duplicateProductResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $duplicateProductInputPath,
        '--output', (Join-Path $workDirectory 'duplicate-product.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects duplicate products' $duplicateProductResult 'more than one entitlement'

    $topLevelExtraInputPath = Join-Path $workDirectory 'top-level-extra-license-data.json'
    $topLevelExtraInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $topLevelExtraInput | Add-Member -NotePropertyName 'purchaseOrder' -NotePropertyValue 'PO-WRONG-LOCATION'
    $topLevelExtraInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $topLevelExtraInputPath -Encoding utf8NoBOM
    $topLevelExtraResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $topLevelExtraInputPath,
        '--output', (Join-Path $workDirectory 'top-level-extra.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects custom top-level fields' $topLevelExtraResult 'Custom fields are allowed only inside licence metadata'

    $entitlementExtraInputPath = Join-Path $workDirectory 'entitlement-extra-license-data.json'
    $entitlementExtraInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $entitlementExtraInput.entitlements[0] | Add-Member -NotePropertyName 'purchaseOrder' -NotePropertyValue 'PO-WRONG-LOCATION'
    $entitlementExtraInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $entitlementExtraInputPath -Encoding utf8NoBOM
    $entitlementExtraResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $entitlementExtraInputPath,
        '--output', (Join-Path $workDirectory 'entitlement-extra.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects custom entitlement fields' $entitlementExtraResult 'Custom fields are allowed only inside licence metadata'

    $invalidMetadataNameInputPath = Join-Path $workDirectory 'invalid-metadata-name-license-data.json'
    $invalidMetadataNameInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $invalidMetadataNameInput.metadata | Add-Member -NotePropertyName 'Purchase_Order' -NotePropertyValue 'PO-INVALID'
    $invalidMetadataNameInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $invalidMetadataNameInputPath -Encoding utf8NoBOM
    $invalidMetadataNameResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $invalidMetadataNameInputPath,
        '--output', (Join-Path $workDirectory 'invalid-metadata-name.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer enforces lower-camel-case metadata names' $invalidMetadataNameResult 'must use lowerCamelCase'

    $nestedMetadataInputPath = Join-Path $workDirectory 'nested-metadata-license-data.json'
    $nestedMetadataInput = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json
    $nestedMetadataInput.metadata | Add-Member -NotePropertyName 'nestedValue' -NotePropertyValue ([pscustomobject]@{ value = 'not-supported' })
    $nestedMetadataInput | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $nestedMetadataInputPath -Encoding utf8NoBOM
    $nestedMetadataResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $nestedMetadataInputPath,
        '--output', (Join-Path $workDirectory 'nested-metadata.license'),
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects nested metadata values' $nestedMetadataResult 'Nested objects, arrays, and null are not supported'

    $mismatchedKeyResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $inputPath,
        '--output', (Join-Path $workDirectory 'mismatched-key.license'),
        '--private-key', $secondaryPrivateKey,
        '--key-id', 'primary-2026'
    )
    Assert-Contains 'signer rejects private key and key ID mismatch' $mismatchedKeyResult 'does not match the public key for key ID'

    # A key that exists only as a dropped-in PEM pair - never added to TrustedPublicKeys.cs - must be
    # usable by the offline signer, since that is the whole point of the key-ring workflow.
    $droppedKeyDirectory = Join-Path $workDirectory 'dropped-keys'
    $keygenResult = Invoke-LicenseTool -Project $generatorProject -ToolArguments @(
        'keygen',
        '--id', 'dropped-2027',
        '--output', $droppedKeyDirectory
    )
    Assert-Contains 'keygen --id reports the convention private key path' $keygenResult 'dropped-2027.private.pem'

    $droppedPrivateKey = Join-Path $droppedKeyDirectory 'dropped-2027.private.pem'
    $droppedPublicKey = Join-Path $droppedKeyDirectory 'dropped-2027.public.pem'
    if (-not (Test-Path -LiteralPath $droppedPrivateKey)) { throw 'keygen --id did not create <keyId>.private.pem' }
    if (-not (Test-Path -LiteralPath $droppedPublicKey)) { throw 'keygen --id did not create <keyId>.public.pem' }
    Write-Host 'PASS  keygen --id writes both halves under the convention names'

    if (-not $IsWindows) {
        $droppedKeyMode = (& stat @('-f', '%OLp', $droppedPrivateKey) 2>$null)
        if (-not $droppedKeyMode) { $droppedKeyMode = (& stat @('-c', '%a', $droppedPrivateKey)) }
        Assert-Equal 'keygen restricts the private key to mode 600' ($droppedKeyMode | Out-String).Trim() '600'
    }

    $keygenOverwriteResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'keygen',
        '--id', 'dropped-2027',
        '--output', $droppedKeyDirectory
    )
    Assert-Contains 'keygen --id still refuses to overwrite' $keygenOverwriteResult 'already exists'

    $keygenBadIdResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'keygen',
        '--id', 'Bad_Id',
        '--output', $droppedKeyDirectory
    )
    Assert-Contains 'keygen rejects key IDs the server could not discover' $keygenBadIdResult 'Invalid --id'

    # No --key-id: it is derived from the '<keyId>.private.pem' filename.
    $droppedLicensePath = Join-Path $workDirectory 'dropped.license'
    Invoke-LicenseTool -Project $generatorProject -ToolArguments @(
        'sign',
        '--input', $inputPath,
        '--output', $droppedLicensePath,
        '--private-key', $droppedPrivateKey
    ) | Out-Null
    $droppedKeyId = (Get-Content -LiteralPath $droppedLicensePath -Raw | ConvertFrom-Json).keyId
    Assert-Equal 'sign derives --key-id from the private key filename' $droppedKeyId 'dropped-2027'

    # The signature is real: LicenseValidator rejects it only because the key is untrusted there,
    # which is the expected embedded-trust behavior, not a signing failure.
    $droppedValidationResult = Invoke-LicenseTool -Project $validatorProject -ExpectedExitCode 1 -ToolArguments @(
        '--license', $droppedLicensePath
    )
    Assert-Contains 'a dropped-in key signs without TrustedPublicKeys edits' $droppedValidationResult 'Unknown signing key: dropped-2027'

    $underivableKeyIdResult = Invoke-LicenseTool -Project $generatorProject -ExpectedExitCode 1 -ToolArguments @(
        'sign',
        '--input', $inputPath,
        '--output', (Join-Path $workDirectory 'underivable.license'),
        '--private-key', $inputPath
    )
    Assert-Contains 'sign explains an underivable key ID' $underivableKeyIdResult 'could not be derived'

    Invoke-LicenseTool -Project $generatorProject -ToolArguments @(
        'sign',
        '--input', $inputPath,
        '--output', $primaryLicensePath,
        '--private-key', $primaryPrivateKey,
        '--key-id', 'primary-2026'
    ) | Out-Null

    $summary = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath
    )
    Assert-Contains 'primary signature validates' $summary 'VALID LICENCE'

    $fieldCases = @(
        @{ Name = 'customer (case-insensitive)'; Field = 'CuStOmEr'; Expected = 'Example Pty Ltd' },
        @{ Name = 'licence ID'; Field = 'licenseId'; Expected = 'LIC-TEST-0001' },
        @{ Name = 'issued at'; Field = 'issuedat'; Expected = '2026-08-12T06:30:00Z' }
    )

    foreach ($case in $fieldCases) {
        $actual = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
            '--license', $primaryLicensePath,
            '--field', $case.Field
        )
        Assert-Equal $case.Name $actual $case.Expected
    }

    $metadataCases = @(
        @{ Name = 'purchase order metadata'; Field = 'metadata.purchaseOrder'; Expected = 'PO-88429' },
        @{ Name = 'case-insensitive metadata path'; Field = 'MeTaDaTa.CoNtAcTeMaIl'; Expected = 'licensing@example.com' },
        @{ Name = 'boolean metadata'; Field = 'metadata.priorityCustomer'; Expected = 'true' },
        @{ Name = 'numeric metadata'; Field = 'metadata.accountNumber'; Expected = '4812' }
    )

    foreach ($case in $metadataCases) {
        $actual = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
            '--license', $primaryLicensePath,
            '--field', $case.Field
        )
        Assert-Equal $case.Name $actual $case.Expected
    }

    $entitlementCases = @(
        @{ Product = 'gcexp'; Edition = 'professional' },
        @{ Product = 'winupd'; Edition = 'enterprise' }
    )

    foreach ($case in $entitlementCases) {
        $edition = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
            '--license', $primaryLicensePath,
            '--product', $case.Product.ToUpperInvariant(),
            '--field', 'EdItIoN'
        )
        Assert-Equal "$($case.Product) edition" $edition $case.Edition

        $product = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
            '--license', $primaryLicensePath,
            '--product', $case.Product,
            '--field', 'product'
        )
        Assert-Equal "$($case.Product) entitlement" $product $case.Product
    }

    $gcexpSeats = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'gcexp',
        '--field', 'SEATS'
    )
    Assert-Equal 'per-product seats' $gcexpSeats '5'

    $winupdType = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'winupd',
        '--field', 'licenseType'
    )
    Assert-Equal 'per-product licence type' $winupdType 'subscription'

    $expiry = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'gcexp',
        '--field', 'expiresat'
    )
    Assert-Equal 'active product expiry' $expiry '2099-12-31T23:59:59Z'

    $updatesUntil = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'gcexp',
        '--field', 'updatesUntil'
    )
    Assert-Equal 'per-product updates date' $updatesUntil '2027-08-12'

    $coveredRelease = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'gcexp',
        '--release-date', '2027-08-12'
    )
    Assert-Contains 'updates-through date is inclusive' $coveredRelease 'VALID LICENCE'

    $uncoveredRelease = Invoke-LicenseTool -Project $validatorProject -ExpectedExitCode 1 -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'gcexp',
        '--release-date', '2027-08-13'
    )
    Assert-Contains 'newer release is not covered' $uncoveredRelease 'release 2027-08-13 is not covered'

    $unlimitedUpdates = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'winupd',
        '--release-date', '2099-01-01'
    )
    Assert-Contains 'missing updatesUntil has no update cutoff' $unlimitedUpdates 'VALID LICENCE'

    $perpetualProduct = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'winupd'
    )
    Assert-Contains 'product without expiry remains valid' $perpetualProduct 'VALID LICENCE'

    $expiredProduct = Invoke-LicenseTool -Project $validatorProject -ExpectedExitCode 1 -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'processwatch'
    )
    Assert-Contains 'expired product is rejected' $expiredProduct "Entitlement for 'processwatch' expired at"

    $expiredField = Invoke-LicenseTool -Project $validatorProject -ExpectedExitCode 1 -ToolArguments @(
        '--license', $primaryLicensePath,
        '--product', 'processwatch',
        '--field', 'edition'
    )
    Assert-Contains 'field reads cannot bypass expiry' $expiredField "Entitlement for 'processwatch' expired at"

    Assert-Contains 'summary marks an expired product' $summary '[EXPIRED 2000-01-01T00:00:00.0000000+00:00;'

    $entitlementsJson = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $primaryLicensePath,
        '--field', 'entitlements'
    )
    $entitlements = @($entitlementsJson | ConvertFrom-Json)
    Assert-Equal 'entitlement count' $entitlements.Count.ToString() '3'

    Invoke-LicenseTool -Project $generatorProject -ToolArguments @(
        'sign',
        '--input', $inputPath,
        '--output', $secondaryLicensePath,
        '--private-key', $secondaryPrivateKey,
        '--key-id', 'secondary-2026'
    ) | Out-Null

    $secondarySummary = Invoke-LicenseTool -Project $validatorProject -ToolArguments @(
        '--license', $secondaryLicensePath
    )
    Assert-Contains 'secondary signature validates' $secondarySummary 'Key ID:       secondary-2026'

    $tampered = Get-Content -LiteralPath $primaryLicensePath -Raw | ConvertFrom-Json
    $tampered.license.entitlements[0].seats = 999
    $tampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tamperedLicensePath -Encoding utf8NoBOM
    $tamperedResult = Invoke-LicenseTool -Project $validatorProject -ExpectedExitCode 1 -ToolArguments @(
        '--license', $tamperedLicensePath
    )
    Assert-Contains 'tampering is rejected' $tamperedResult 'Cryptographic signature is invalid.'

    $unknownKey = Get-Content -LiteralPath $primaryLicensePath -Raw | ConvertFrom-Json
    $unknownKey.keyId = 'attacker-key'
    $unknownKey | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $unknownKeyLicensePath -Encoding utf8NoBOM
    $unknownKeyResult = Invoke-LicenseTool -Project $validatorProject -ExpectedExitCode 1 -ToolArguments @(
        '--license', $unknownKeyLicensePath
    )
    Assert-Contains 'unknown keys are rejected' $unknownKeyResult 'Unknown signing key: attacker-key'

    $publicKeyOptionResult = Invoke-LicenseTool -Project $validatorProject -ExpectedExitCode 2 -ToolArguments @(
        '--license', $primaryLicensePath,
        '--public-key', (Join-Path $repositoryRoot 'keys\primary-2026.public.pem')
    )
    Assert-Contains 'caller-supplied public keys are rejected' $publicKeyOptionResult '--public-key is no longer accepted'

    Write-Host
    Write-Host 'All licence flow tests passed.' -ForegroundColor Green
}
finally {
    if ($KeepArtifacts) {
        Write-Host "Test artifacts kept at: $workDirectory"
    }
    elseif (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
