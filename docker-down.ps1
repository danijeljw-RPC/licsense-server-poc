[CmdletBinding()]
param(
    [switch] $RemoveVolumes
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
$originalLocation = Get-Location
$originalDockerHost = [Environment]::GetEnvironmentVariable('DOCKER_HOST', 'Process')
$originalDockerContext = [Environment]::GetEnvironmentVariable('DOCKER_CONTEXT', 'Process')
$temporaryVariables = @()

function Test-DockerEngine {
    & docker info --format '{{.ServerVersion}}' *> $null
    return $LASTEXITCODE -eq 0
}

try {
    Set-Location -LiteralPath $repositoryRoot

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker CLI was not found.'
    }
    if (-not (Test-DockerEngine)) {
        Remove-Item Env:\DOCKER_CONTEXT -ErrorAction SilentlyContinue
        $env:DOCKER_HOST = 'npipe:////./pipe/docker_engine'
        if (-not (Test-DockerEngine)) {
            throw 'Docker Desktop is not reachable. Start Docker Desktop before removing the stack.'
        }
    }

    # `compose down` still interpolates the full file. Supply inert values only when
    # neither .env nor the current process provides them.
    if (-not (Test-Path -LiteralPath '.env')) {
        foreach ($pair in @{
            POSTGRES_PASSWORD = 'down-only-not-used'
            DEFAULT_ADMIN_PASSWORD = 'DownOnly!9Xv4-Qr7-Ws2'
            LICENSE_SIGNING_KEY_PATH = './keys/license-primary-2026-private.pem'
        }.GetEnumerator()) {
            if (-not (Test-Path "Env:\$($pair.Key)")) {
                Set-Item "Env:\$($pair.Key)" $pair.Value
                $temporaryVariables += $pair.Key
            }
        }
    }

    $arguments = @('compose', 'down', '--remove-orphans')
    if ($RemoveVolumes) {
        Write-Warning 'Removing the PostgreSQL and Data Protection volumes. This deletes local users, licenses, audit data, and persistent cookies.'
        $arguments += '--volumes'
    }

    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Compose shutdown failed.' }

    if ($RemoveVolumes) {
        Write-Host 'LicenseServer containers, network, and development volumes were removed.' -ForegroundColor Green
    } else {
        Write-Host 'LicenseServer containers and network were removed; persistent volumes were preserved.' -ForegroundColor Green
    }
}
finally {
    foreach ($name in $temporaryVariables) { Remove-Item "Env:\$name" -ErrorAction SilentlyContinue }
    Set-Location $originalLocation
    if ($null -eq $originalDockerHost) { Remove-Item Env:\DOCKER_HOST -ErrorAction SilentlyContinue } else { $env:DOCKER_HOST = $originalDockerHost }
    if ($null -eq $originalDockerContext) { Remove-Item Env:\DOCKER_CONTEXT -ErrorAction SilentlyContinue } else { $env:DOCKER_CONTEXT = $originalDockerContext }
}
