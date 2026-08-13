[CmdletBinding()]
param(
    [string] $LicenseId = 'LIC-POC-0001',
    [string] $ActivationCode = 'POC-DEMO-ACTIVATION-CODE',
    [string] $OutputPath = 'artifacts/offline-activation-request.json'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$validatorProject = Join-Path $repositoryRoot 'src\LicenseValidator\LicenseValidator.csproj'
$absoluteOutput = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
}
$deviceJson = & dotnet run --no-restore --project $validatorProject -- --device-id

if ($LASTEXITCODE -ne 0) {
    throw 'Could not determine the local device identifier.'
}

$device = $deviceJson | ConvertFrom-Json
$request = [ordered]@{
    requestId = [Guid]::NewGuid().ToString('D')
    activationCode = $ActivationCode
    activationToken = [Convert]::ToBase64String(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    mode = 'offline'
    device = [ordered]@{
        scheme = $device.Scheme
        deviceId = $device.DeviceId
        deviceName = $device.DeviceName
    }
}

$parent = Split-Path -Parent $absoluteOutput
if ($parent) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$request | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $absoluteOutput -Encoding utf8NoBOM
Write-Host "Offline activation request written to: $absoluteOutput"
Write-Warning 'This request contains activation credentials. Transfer and store it securely.'
