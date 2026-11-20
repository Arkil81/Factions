#requires -Version 5.1
<#
.SYNOPSIS
  One-time elevated setup for running Factions.Api as a durable Windows Service.

.DESCRIPTION
  Run this from an ELEVATED PowerShell session on the Windows host. It creates or updates
  the Factions.Api service to run the deployed API from tmp\api-live, starts it, and verifies
  that http://127.0.0.1:7891/api/status responds.

  This is needed because API processes spawned through SSH are tied to the SSH logon session
  and can die when that session disconnects.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = 'C:\Users\se-dev\Documents\GitHub\Factions',
    [string]$ServiceName = 'Factions.Api',
    [string]$DisplayName = 'Factions Api',
    [string]$DataDir = 'C:\Torch\torch-server\Instance\Factions',
    [string]$Url = 'http://0.0.0.0:7891'
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell session (Run as Administrator).'
    }
}

function Wait-ApiStatus {
    param([int]$TimeoutSeconds = 30)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            return Invoke-RestMethod -Uri 'http://127.0.0.1:7891/api/status' -TimeoutSec 5
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 1
        }
    }
    throw "Factions.Api did not respond on http://127.0.0.1:7891/api/status within ${TimeoutSeconds}s. Last error: $lastError"
}

Assert-Administrator

$liveDir = Join-Path $RepoRoot 'tmp\api-live'
$exe = Join-Path $liveDir 'Factions.Api.exe'
if (-not (Test-Path $exe)) {
    throw "Missing deployed API executable: $exe`nRun build.ps1 -Deploy first so tmp\api-live is populated."
}

New-Item -ItemType Directory -Force -Path (Join-Path $liveDir 'logs') | Out-Null

# Stop old ad-hoc API processes if any are still around.
Get-Process -Name 'Factions.Api' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping old Factions.Api process pid $($_.Id)..." -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
foreach ($p in @(Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue)) {
    $cmd = $null
    try { $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)" -ErrorAction Stop).CommandLine } catch { }
    if ($cmd -and ($cmd -match 'Factions\.Api' -or $cmd -match [regex]::Escape($liveDir))) {
        Write-Host "Stopping old dotnet API process pid $($p.Id)..." -ForegroundColor Yellow
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
}

$binPath = '"' + $exe + '" --data-dir "' + $DataDir + '"'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $service) {
    Write-Host "Creating service $ServiceName..." -ForegroundColor Cyan
    New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName $binPath -StartupType Automatic | Out-Null
} else {
    Write-Host "Updating existing service $ServiceName..." -ForegroundColor Cyan
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
}

# ASP.NET Core Windows-service hosting uses AppContext.BaseDirectory as content root. Set URL explicitly too.
[Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', $Url, 'Machine')

Write-Host "Starting service $ServiceName..." -ForegroundColor Cyan
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

$status = Wait-ApiStatus -TimeoutSeconds 30
Write-Host "Factions.Api service is running." -ForegroundColor Green
Write-Host "Status response:" -ForegroundColor Green
$status | ConvertTo-Json -Compress

Write-Host "`nVerification commands:" -ForegroundColor Cyan
Write-Host "  sc query $ServiceName"
Write-Host "  netstat -ano | findstr :7891"
Write-Host "  Invoke-RestMethod http://127.0.0.1:7891/api/status"
