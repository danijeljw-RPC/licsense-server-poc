[CmdletBinding()]
param([switch] $KeepContainer)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$containerName = "license-server-tests-" + [Guid]::NewGuid().ToString('N')
$port = Get-Random -Minimum 49152 -Maximum 60000
$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))

try {
    docker run --detach --name $containerName `
        --publish "127.0.0.1:${port}:5432" `
        --env POSTGRES_DB=license_tests `
        --env POSTGRES_USER=license_test `
        --env "POSTGRES_PASSWORD=$password" `
        --health-cmd 'pg_isready -U license_test -d license_tests' `
        --health-interval 1s --health-timeout 3s --health-retries 30 `
        postgres:18-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to start the isolated PostgreSQL test container.' }

    $healthy = $false
    foreach ($attempt in 1..40) {
        $status = docker inspect --format '{{.State.Health.Status}}' $containerName 2>$null
        if ($status -eq 'healthy') { $healthy = $true; break }
        if ($status -eq 'unhealthy') { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) { throw "PostgreSQL did not become healthy.`n$(docker logs $containerName 2>&1)" }

    $env:TEST_POSTGRES_CONNECTION = "Host=127.0.0.1;Port=$port;Database=license_tests;Username=license_test;Password=$password"
    dotnet test (Join-Path $repositoryRoot 'tests\LicenseServer.Tests\LicenseServer.Tests.csproj') --configuration Release --no-restore -p:AnalysisMode=Recommended
    if ($LASTEXITCODE -ne 0) { throw 'Database and authentication tests failed.' }
}
finally {
    Remove-Item Env:\TEST_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
    if ($KeepContainer) { Write-Host "Test container kept: $containerName" }
    else { docker rm --force $containerName 2>$null | Out-Null }
}
