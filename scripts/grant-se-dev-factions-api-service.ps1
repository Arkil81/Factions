#requires -Version 5.1
<#
.SYNOPSIS
  Grant se-dev permission to manage only the Factions.Api Windows service.

.DESCRIPTION
  Run this from an ELEVATED PowerShell session on the Windows host after the
  Factions.Api service has been created. It updates only the service security
  descriptor for Factions.Api so the deploy account (default <this computer>\se-dev) can query, start, stop,
  pause/continue, and interrogate that service during deploy.

  It does NOT grant global Service Control Manager admin rights and does not
  affect other services.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'Factions.Api',
    [string]$Account = "$env:COMPUTERNAME\se-dev"
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell session (Run as Administrator).'
    }
}

function Get-AccountSidString {
    param([string]$Name)
    $nt = New-Object System.Security.Principal.NTAccount($Name)
    return $nt.Translate([System.Security.Principal.SecurityIdentifier]).Value
}

function Get-ServiceSddl {
    param([string]$Name)
    $out = sc.exe sdshow $Name 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe sdshow $Name failed: $out" }
    return (($out | Out-String).Trim())
}

function Set-ServiceSddl {
    param([string]$Name, [string]$Sddl)
    $out = sc.exe sdset $Name $Sddl 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe sdset $Name failed: $out" }
    return $out
}

Assert-Administrator

# Confirm the service exists before editing its ACL.
$svc = Get-Service -Name $ServiceName -ErrorAction Stop
$sid = Get-AccountSidString -Name $Account
$sddl = Get-ServiceSddl -Name $ServiceName

# Rights used here:
#   RP = SERVICE_START
#   LO = SERVICE_INTERROGATE
#   DT = SERVICE_PAUSE_CONTINUE
#   RC = READ_CONTROL
#   LC = SERVICE_QUERY_STATUS
#   SW = SERVICE_ENUMERATE_DEPENDENTS
#   CR = SERVICE_USER_DEFINED_CONTROL
#   WP = SERVICE_STOP
# This is enough for deploy scripts to query, stop, start, and restart Factions.Api.
$ace = "(A;;RPLODTLCRCSWWP;;;$sid)"

if ($sddl -like "*$sid*") {
    Write-Host "Service '$ServiceName' already has an ACE for $Account ($sid)." -ForegroundColor Yellow
} else {
    if ($sddl -notmatch '^D:') {
        throw "Unexpected service SDDL format: $sddl"
    }
    $newSddl = $sddl -replace '^D:', ('D:' + $ace)
    Write-Host "Granting $Account service-control rights on '$ServiceName' only..." -ForegroundColor Cyan
    Set-ServiceSddl -Name $ServiceName -Sddl $newSddl | Out-Host
}

Write-Host "`nVerification as current elevated user:" -ForegroundColor Cyan
sc.exe sdshow $ServiceName

Write-Host "`nTo verify from the se-dev SSH session, run:" -ForegroundColor Green
Write-Host "  sc query $ServiceName"
Write-Host "  sc stop $ServiceName"
Write-Host "  sc start $ServiceName"
Write-Host "  Invoke-RestMethod http://127.0.0.1:7891/api/status"
