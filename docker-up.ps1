[CmdletBinding()]
param(
    [switch] $NoBuild,
    [ValidateRange(30, 900)]
    [int] $WaitTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
$originalLocation = Get-Location
$originalDockerHost = [Environment]::GetEnvironmentVariable('DOCKER_HOST', 'Process')
$originalDockerContext = [Environment]::GetEnvironmentVariable('DOCKER_CONTEXT', 'Process')

function Invoke-DockerCapture {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $output = & docker @Arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = @($output)
        Text = ($output -join [Environment]::NewLine)
    }
}

function Test-DockerEngine {
    $result = Invoke-DockerCapture @('info', '--format', '{{.ServerVersion}}')
    if ($result.ExitCode -eq 0) {
        Write-Host "Docker engine $($result.Text.Trim()) is available."
        return $true
    }

    Write-Warning $result.Text
    return $false
}

function Use-DirectDockerDesktopPipe {
    Write-Host 'Retrying through the Docker Desktop engine pipe to bypass locked context metadata...'
    Remove-Item Env:\DOCKER_CONTEXT -ErrorAction SilentlyContinue
    $env:DOCKER_HOST = 'npipe:////./pipe/docker_engine'
    return Test-DockerEngine
}

try {
    Set-Location -LiteralPath $repositoryRoot

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker CLI was not found. Install and start Docker Desktop, then reopen this terminal.'
    }
    if (-not (Test-Path -LiteralPath '.env')) {
        Copy-Item -LiteralPath '.env.example' -Destination '.env'
        throw "Created .env from .env.example. Replace its example passwords, then run this script again."
    }
    if (-not (Test-Path -LiteralPath 'keys\primary-2026.private.pem')) {
        throw 'The development signing key keys\primary-2026.private.pem is missing.'
    }

    $envText = Get-Content -LiteralPath '.env' -Raw
    if ($envText -match 'POSTGRES_PASSWORD=local-db-password-change-me' -and [string]::IsNullOrWhiteSpace($env:POSTGRES_PASSWORD)) {
        Write-Warning 'POSTGRES_PASSWORD still uses the .env.example value. Replace it before using anything except disposable local development.'
    }

    if (-not (Test-DockerEngine)) {
        if (-not (Use-DirectDockerDesktopPipe)) {
            throw 'Docker Desktop is not reachable. Start Docker Desktop, wait until its engine reports Running, and retry.'
        }
    }

    $config = Invoke-DockerCapture @('compose', 'config', '--quiet')
    if ($config.ExitCode -ne 0) {
        throw "Compose configuration is invalid:`n$($config.Text)"
    }

    # Build directly through the engine. This intentionally bypasses Compose Bake,
    # whose buildx driver metadata can be transiently locked by Docker Desktop.
    if (-not $NoBuild) {
        $build = Invoke-DockerCapture @('build', '--tag', 'license-server-app', '.')
        Write-Host $build.Text
        if ($build.ExitCode -ne 0 -and $build.Text -match 'no valid drivers found|contexts[\\/]meta|being used by another process') {
            if (Use-DirectDockerDesktopPipe) {
                Write-Host 'Retrying the image build once with the direct engine connection...'
                $build = Invoke-DockerCapture @('build', '--tag', 'license-server-app', '.')
                Write-Host $build.Text
            }
        }
        if ($build.ExitCode -ne 0) { throw "Docker image build failed.`n$($build.Text)" }
    }

    $upArguments = @(
        'compose', 'up', '--detach', '--no-build', '--wait', '--wait-timeout',
        $WaitTimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    )

    $up = Invoke-DockerCapture $upArguments
    Write-Host $up.Text

    if ($up.ExitCode -ne 0 -and $up.Text -match 'no valid drivers found|contexts[\\/]meta|being used by another process') {
        if (Use-DirectDockerDesktopPipe) {
            Write-Host 'Retrying Compose once with the direct engine connection...'
            $up = Invoke-DockerCapture $upArguments
            Write-Host $up.Text
        }
    }
    if ($up.ExitCode -ne 0) {
        & docker compose ps
        $logs = Invoke-DockerCapture @('compose', 'logs', '--tail', '80', 'app', 'postgres')
        Write-Host $logs.Text
        if ($logs.Text -match 'password authentication failed') {
            Write-Warning 'The existing PostgreSQL volume was initialized with a different POSTGRES_PASSWORD. Restore the original value in .env, or intentionally reset local data with .\docker-down.ps1 -RemoveVolumes.'
        }
        throw "Compose failed. If the message still mentions a locked context file, quit and restart Docker Desktop, then rerun this script.`n$($up.Text)"
    }

    & docker compose ps
    if ($LASTEXITCODE -ne 0) { throw 'Compose started, but its final status could not be read.' }

    $appPort = if (-not [string]::IsNullOrWhiteSpace($env:APP_PORT)) {
        $env:APP_PORT
    } elseif ($envText -match '(?m)^\s*APP_PORT\s*=\s*([^#\r\n]+)') {
        $Matches[1].Trim().Trim('"').Trim("'")
    } else { '8080' }
    Write-Host "`nLicenseServer is healthy: http://localhost:$appPort" -ForegroundColor Green
}
finally {
    Set-Location $originalLocation
    if ($null -eq $originalDockerHost) { Remove-Item Env:\DOCKER_HOST -ErrorAction SilentlyContinue } else { $env:DOCKER_HOST = $originalDockerHost }
    if ($null -eq $originalDockerContext) { Remove-Item Env:\DOCKER_CONTEXT -ErrorAction SilentlyContinue } else { $env:DOCKER_CONTEXT = $originalDockerContext }
}
