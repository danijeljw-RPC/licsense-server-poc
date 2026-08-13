[CmdletBinding()]
param([switch] $KeepArtifacts)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serverAssembly = Join-Path $repositoryRoot 'src\LicenseServer\bin\Release\net10.0\LicenseServer.dll'
$validatorProject = Join-Path $repositoryRoot 'src\LicenseValidator\LicenseValidator.csproj'
$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ("software-activation-test-" + [Guid]::NewGuid())
$port = Get-Random -Minimum 51000 -Maximum 59000
$serverUrl = "http://127.0.0.1:$port"
$serverProcess = $null
$databaseContainer = "license-activation-test-" + [Guid]::NewGuid().ToString('N')
$databasePort = Get-Random -Minimum 49152 -Maximum 50999
$databasePassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$adminPassword = 'LocalAdmin!7Kp9-Vx3-Rm8-Qz2'
$changedAdminPassword = 'ChangedLocalAdmin!8Lp4-Wy6-Sn9-Rt3'
$adminSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Assert-Equal {
    param([string] $Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        throw "$Name failed. Expected '$Expected', received '$Actual'."
    }
    Write-Host "PASS  $Name"
}

function New-ActivationRequest {
    param([string] $DeviceId, [string] $DeviceName, [string] $Mode)
    return @{
        requestId = [Guid]::NewGuid().ToString('D')
        activationCode = 'POC-DEMO-ACTIVATION-CODE'
        activationToken = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        mode = $Mode
        device = @{
            scheme = 'os-machine-id-sha256-v1'
            deviceId = $DeviceId
            deviceName = $DeviceName
        }
    }
}

function Invoke-Api {
    param([string] $Path, $Body, [string] $Method = 'Post', [hashtable] $Headers = @{})
    $parameters = @{
        Uri = "$serverUrl$Path"
        Method = $Method
        Headers = $Headers
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 20
    }
    return Invoke-WebRequest @parameters
}

try {
    New-Item -ItemType Directory -Path $workDirectory | Out-Null
    $stdout = Join-Path $workDirectory 'server.stdout.log'
    $stderr = Join-Path $workDirectory 'server.stderr.log'

    docker run --detach --name $databaseContainer `
        --publish "127.0.0.1:${databasePort}:5432" `
        --env POSTGRES_DB=activation_test `
        --env POSTGRES_USER=license_test `
        --env "POSTGRES_PASSWORD=$databasePassword" `
        --health-cmd 'pg_isready -U license_test -d activation_test' `
        --health-interval 1s --health-timeout 3s --health-retries 30 `
        postgres:18-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start isolated PostgreSQL.' }
    foreach ($attempt in 1..40) {
        if ((docker inspect --format '{{.State.Health.Status}}' $databaseContainer 2>$null) -eq 'healthy') { break }
        Start-Sleep -Milliseconds 500
    }
    if ((docker inspect --format '{{.State.Health.Status}}' $databaseContainer) -ne 'healthy') { throw 'Isolated PostgreSQL did not become healthy.' }

    dotnet build (Join-Path $repositoryRoot 'SoftwareLicensing.slnx') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $env:ConnectionStrings__DefaultConnection = "Host=127.0.0.1;Port=$databasePort;Database=activation_test;Username=license_test;Password=$databasePassword"
    $env:SEED_DEFAULT_ADMIN = 'true'
    $env:DEFAULT_ADMIN_EMAIL = 'admin@localhost.com'
    $env:DEFAULT_ADMIN_PASSWORD = $adminPassword
    $env:SEED_DEMO_LICENSE = 'true'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Licensing__PrivateKeyPath = Join-Path $repositoryRoot 'keys\license-primary-2026-private.pem'
    $env:Licensing__PublicKeyPath = Join-Path $repositoryRoot 'keys\license-primary-2026-public.pem'
    $startProcessArguments = @{
        FilePath = 'dotnet'
        ArgumentList = @($serverAssembly, '--urls', $serverUrl)
        WorkingDirectory = $repositoryRoot
        RedirectStandardOutput = $stdout
        RedirectStandardError = $stderr
        PassThru = $true
    }
    if ($IsWindows) {
        $startProcessArguments.WindowStyle = 'Hidden'
    }
    $serverProcess = Start-Process @startProcessArguments

    $ready = $false
    foreach ($attempt in 1..40) {
        Start-Sleep -Milliseconds 250
        try {
            if ((Invoke-WebRequest -Uri "$serverUrl/health" -TimeoutSec 1).StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch { }
    }
    if (-not $ready) {
        throw "Server did not start.`n$(Get-Content -LiteralPath $stderr -Raw)"
    }

    $demoLicenseId = (docker exec $databaseContainer psql --username license_test --dbname activation_test --tuples-only --no-align --command 'SELECT l."LicenseId" FROM "Licenses" l JOIN "Customers" c ON c."Id" = l."CustomerId" WHERE c."ExternalId" = ''POC-CUSTOMER'';').Trim()
    if ($demoLicenseId -notmatch '^LIC-[0-9]{4}-[0-9]{4}[A-F0-9]{6}$') {
        throw "The generated demo license ID could not be read from PostgreSQL: '$demoLicenseId'."
    }

    $loginPage = Invoke-WebRequest -Uri "$serverUrl/Account/Login" -WebSession $adminSession
    $loginToken = [regex]::Match($loginPage.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    Invoke-WebRequest -Uri "$serverUrl/Account/Login" -Method Post -WebSession $adminSession -Body @{
        '__RequestVerificationToken' = [Net.WebUtility]::HtmlDecode($loginToken)
        '_handler' = 'login'
        'Input.Email' = 'admin@localhost.com'
        'Input.Password' = $adminPassword
        'Input.RememberMe' = 'false'
    } | Out-Null
    $changePage = Invoke-WebRequest -Uri "$serverUrl/Account/Manage/ChangePassword?forced=true" -WebSession $adminSession
    $changeToken = [regex]::Match($changePage.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    Invoke-WebRequest -Uri "$serverUrl/Account/Manage/ChangePassword?forced=true" -Method Post -WebSession $adminSession -Body @{
        '__RequestVerificationToken' = [Net.WebUtility]::HtmlDecode($changeToken)
        '_handler' = 'change-password'
        'Input.OldPassword' = $adminPassword
        'Input.NewPassword' = $changedAdminPassword
        'Input.ConfirmPassword' = $changedAdminPassword
    } | Out-Null

    $deviceJson = & dotnet run --no-build --configuration Release --project $validatorProject -- --device-id
    if ($LASTEXITCODE -ne 0) { throw 'Could not obtain the local device ID.' }
    $device = $deviceJson | ConvertFrom-Json

    $firstRequest = New-ActivationRequest $device.DeviceId $device.DeviceName 'online'
    $first = Invoke-Api "/api/v1/licenses/$demoLicenseId/activate" $firstRequest
    Assert-Equal 'online activation succeeds' $first.StatusCode 200
    $firstResponse = $first.Content | ConvertFrom-Json

    $onlineLicense = Join-Path $workDirectory 'online.license'
    $firstResponse.signedLicense | Set-Content -LiteralPath $onlineLicense -Encoding utf8NoBOM
    & dotnet run --no-build --configuration Release --project $validatorProject -- --license $onlineLicense --product gcexp
    Assert-Equal 'device-bound online licence validates locally' $LASTEXITCODE 0

    $otherDeviceId = 'A' * 64
    $blockedRequest = New-ActivationRequest $otherDeviceId 'TRANSFER-TARGET' 'online'
    $blocked = Invoke-Api "/api/v1/licenses/$demoLicenseId/activate" $blockedRequest
    Assert-Equal 'transfer is blocked before deactivation' $blocked.StatusCode 409

    $credential = @{
        activationToken = $firstRequest.activationToken
        deviceId = $device.DeviceId
    }
    $validated = Invoke-Api "/api/v1/activations/$($firstResponse.activationId)/validate" $credential
    Assert-Equal 'server confirms activation' $validated.StatusCode 200

    $refreshed = Invoke-Api "/api/v1/activations/$($firstResponse.activationId)/refresh" $credential
    Assert-Equal 'online lease refresh succeeds' $refreshed.StatusCode 200

    $deactivated = Invoke-Api "/api/v1/activations/$($firstResponse.activationId)/deactivate" $credential
    Assert-Equal 'authenticated deactivation succeeds' $deactivated.StatusCode 200

    $transferred = Invoke-Api "/api/v1/licenses/$demoLicenseId/activate" $blockedRequest
    Assert-Equal 'activation transfers after deactivation' $transferred.StatusCode 200
    $transferredResponse = $transferred.Content | ConvertFrom-Json

    $transferredLicense = Join-Path $workDirectory 'transferred.license'
    $transferredResponse.signedLicense | Set-Content -LiteralPath $transferredLicense -Encoding utf8NoBOM
    $mismatchOutput = & dotnet run --no-build --configuration Release --project $validatorProject -- --license $transferredLicense --product gcexp 2>&1
    Assert-Equal 'copied licence fails on the wrong device' $LASTEXITCODE 1
    if (-not (($mismatchOutput -join "`n").Contains('different device'))) {
        throw 'Wrong-device validation did not report a device mismatch.'
    }

    $transferCredential = @{
        activationToken = $blockedRequest.activationToken
        deviceId = $otherDeviceId
    }
    Invoke-Api "/api/v1/activations/$($transferredResponse.activationId)/deactivate" $transferCredential | Out-Null

    $offlineRequest = New-ActivationRequest $device.DeviceId $device.DeviceName 'offline'
    $offline = Invoke-Api "/api/v1/licenses/$demoLicenseId/activate" $offlineRequest
    Assert-Equal 'offline request receives signed response' $offline.StatusCode 200
    $offlineResponse = $offline.Content | ConvertFrom-Json
    Assert-Equal 'offline licence has no lease cutoff' $null $offlineResponse.leaseExpiresAt

    $offlineLicense = Join-Path $workDirectory 'offline.license'
    $offlineResponse.signedLicense | Set-Content -LiteralPath $offlineLicense -Encoding utf8NoBOM
    & dotnet run --no-build --configuration Release --project $validatorProject -- --license $offlineLicense --product gcexp
    Assert-Equal 'offline device-bound licence validates without server' $LASTEXITCODE 0

    $csrf = Invoke-RestMethod -Uri "$serverUrl/api/v1/admin/antiforgery" -WebSession $adminSession
    $csrfToken = $csrf.requestToken
    $revoked = Invoke-WebRequest -Uri "$serverUrl/api/v1/admin/licenses/$demoLicenseId/revoke" -Method Post -WebSession $adminSession -ContentType 'application/json' -Headers @{ 'X-CSRF-TOKEN' = $csrfToken } -Body (@{ reason = 'integration test' } | ConvertTo-Json) -SkipHttpErrorCheck
    if ($revoked.StatusCode -ne 200) { Write-Host "Revocation response: $($revoked.Content)" }
    Assert-Equal 'admin revocation succeeds' $revoked.StatusCode 200

    $offlineCredential = @{
        activationToken = $offlineRequest.activationToken
        deviceId = $device.DeviceId
    }
    $serverRejects = Invoke-Api "/api/v1/activations/$($offlineResponse.activationId)/validate" $offlineCredential
    Assert-Equal 'server rejects revoked activation' $serverRejects.StatusCode 403

    & dotnet run --no-build --configuration Release --project $validatorProject -- --license $offlineLicense --product gcexp | Out-Null
    Assert-Equal 'offline file remains valid until it reconnects' $LASTEXITCODE 0

    Write-Host
    Write-Host 'All activation, transfer, revocation, and offline tests passed.' -ForegroundColor Green
}
finally {
    Remove-Item Env:\ConnectionStrings__DefaultConnection,Env:\SEED_DEFAULT_ADMIN,Env:\DEFAULT_ADMIN_EMAIL,Env:\DEFAULT_ADMIN_PASSWORD,Env:\SEED_DEMO_LICENSE,Env:\ASPNETCORE_ENVIRONMENT,Env:\Licensing__PrivateKeyPath,Env:\Licensing__PublicKeyPath -ErrorAction SilentlyContinue
    if ($null -ne $serverProcess) {
        try {
            if (-not $serverProcess.HasExited) {
                Stop-Process -Id $serverProcess.Id -Force
            }
            if (-not $serverProcess.WaitForExit(5000)) {
                Write-Warning "Test server process $($serverProcess.Id) did not exit within five seconds."
            }
        }
        finally {
            $serverProcess.Dispose()
        }
    }

    if ($KeepArtifacts) {
        Write-Host "Test artifacts kept at: $workDirectory"
    }
    elseif (Test-Path -LiteralPath $workDirectory) {
        foreach ($attempt in 1..10) {
            try {
                Remove-Item -LiteralPath $workDirectory -Recurse -Force
                break
            }
            catch {
                if ($attempt -eq 10) {
                    throw
                }
                Start-Sleep -Milliseconds 100
            }
        }
    }

    docker rm --force $databaseContainer 2>$null | Out-Null
}
