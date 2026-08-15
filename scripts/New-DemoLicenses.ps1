[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'SoftwareLicensing.slnx'
$generatorProject = Join-Path $repositoryRoot 'src\LicenseGenerator\LicenseGenerator.csproj'
$validatorProject = Join-Path $repositoryRoot 'src\LicenseValidator\LicenseValidator.csproj'
$licensesDirectory = Join-Path $repositoryRoot 'licenses'

function Invoke-Tool {
    param(
        [Parameter(Mandatory)]
        [string] $Project,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & dotnet run `
        --no-build `
        --configuration Release `
        --project $Project `
        -- @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $licensesDirectory -Force | Out-Null

& dotnet build $solution --configuration Release

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$demos = @(
    @{
        Name = 'primary-2026'
        Input = Join-Path $repositoryRoot 'input\demo-primary-license-data.json'
        Output = Join-Path $licensesDirectory 'demo-primary.license'
        PrivateKey = Join-Path $repositoryRoot 'keys\primary-2026.private.pem'
        Product = 'gcexp'
        ReleaseDate = '2028-12-31'
    },
    @{
        Name = 'secondary-2026'
        Input = Join-Path $repositoryRoot 'input\demo-secondary-license-data.json'
        Output = Join-Path $licensesDirectory 'demo-secondary.license'
        PrivateKey = Join-Path $repositoryRoot 'keys\secondary-2026.private.pem'
        Product = 'winupd'
        ReleaseDate = '2030-12-31'
    }
)

foreach ($demo in $demos) {
    Write-Host
    Write-Host "Signing demo with $($demo.Name)..." -ForegroundColor Cyan

    Invoke-Tool -Project $generatorProject -Arguments @(
        'sign',
        '--input', $demo.Input,
        '--output', $demo.Output,
        '--private-key', $demo.PrivateKey,
        '--key-id', $demo.Name
    )

    Write-Host "Validating $($demo.Product) without supplying a public key..." -ForegroundColor Cyan

    Invoke-Tool -Project $validatorProject -Arguments @(
        '--license', $demo.Output,
        '--product', $demo.Product,
        '--release-date', $demo.ReleaseDate
    )
}

Write-Host
Write-Host 'Both demo licences were generated and validated successfully.' -ForegroundColor Green
Write-Host "Primary:   $(Join-Path $licensesDirectory 'demo-primary.license')"
Write-Host "Secondary: $(Join-Path $licensesDirectory 'demo-secondary.license')"
