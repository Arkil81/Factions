#requires -Version 5.1
<#
.SYNOPSIS
  Live probe for the two fixes shipped this session (2026-08-05, commits b248432 + 952f552):
    1. PriceOracle.AllocateConcreteReference no longer collapses to a six-figure undivided kind
       budget when a subtype has zero stock (was: pool-split across whatever's in inventory).
    2. OperationsManager.Tick routes surplus to every shortfall node per item per tick, not just
       the single largest one (was: winner-take-all).
  Exit 0 = both signals observed healthy (or explicitly inconclusive with a stated reason).
  Exit 1 = a regression signal was observed.

.DESCRIPTION
  Non-destructive by design: the only live-state change is a temporary LogItemXfer=true flip
  (ITEM_XFER is gated off by default, see docs/live-server-access.md's suppression table;
  PG_SURPLUS_DEMAND is not gated, so no equivalent flip is needed for the PriceOracle check).
  The original value is snapshotted and restored via 'factions reload' in a finally block even
  if the probe fails partway through.

.EXAMPLE
  ./scripts/Probe-SessionEffects.ps1
.EXAMPLE
  ./scripts/Probe-SessionEffects.ps1 -MonitorSeconds 60 -MaxSaneSurplusTarget 40000
#>
[CmdletBinding()]
param(
    [string]$TorchRoot            = 'C:\Torch\torch-server',
    [string]$InstanceName         = 'Instance',
    [int]   $MonitorSeconds       = 30,
    [int]   $MaxSaneSurplusTarget = 50000   # pre-fix live evidence showed 148,660-157,114; post-fix max observed was ~27,722
)
$ErrorActionPreference = 'Stop'

$instanceDir = Join-Path $TorchRoot (Join-Path $InstanceName 'Factions')
$bridgeDir   = Join-Path $instanceDir 'bridge'
$tuningPath  = Join-Path $instanceDir 'tuning.json'
$logsDir     = Join-Path $instanceDir 'logs'

function Get-TodayFactionsLog {
    Get-ChildItem (Join-Path $logsDir 'Factions-*.log') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Send-BridgeCommand([string]$cmd) {
    if (-not (Test-Path $bridgeDir)) { New-Item -ItemType Directory -Path $bridgeDir -Force | Out-Null }
    [System.IO.File]::WriteAllText((Join-Path $bridgeDir 'inbox.txt'), ($cmd + "`n"))
}

if (-not (Get-Process -Name 'Torch.Server' -ErrorAction SilentlyContinue)) {
    Write-Host "FAIL: Torch.Server is not running. This probe observes a live server; it does not start one." -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $tuningPath)) {
    Write-Host "FAIL: tuning.json not found at $tuningPath" -ForegroundColor Red
    exit 1
}

$positiveSignals = New-Object System.Collections.Generic.List[string]
$negativeSignals = New-Object System.Collections.Generic.List[string]
$notes           = New-Object System.Collections.Generic.List[string]

$originalTuningRaw = Get-Content $tuningPath -Raw
$originalObj       = $originalTuningRaw | ConvertFrom-Json
$originalLogItemXfer = $originalObj.LogItemXfer

$restored = $false
try {
    # --- Set up: flip LogItemXfer on so the OperationsManager fair-share signal is visible ---
    $obj = Get-Content $tuningPath -Raw | ConvertFrom-Json
    $obj.LogItemXfer = $true
    ($obj | ConvertTo-Json -Depth 10) | Set-Content -Path $tuningPath -Encoding UTF8
    Send-BridgeCommand 'factions reload'
    Start-Sleep -Seconds 3

    $probeStartTod = [timespan]::FromSeconds([math]::Floor(((Get-Date).TimeOfDay).TotalSeconds))
    Write-Host "Probe window open (LogItemXfer=true); monitoring for ${MonitorSeconds}s..." -ForegroundColor Cyan
    Start-Sleep -Seconds $MonitorSeconds

    $log = Get-TodayFactionsLog
    if (-not $log) {
        Write-Host "FAIL: no Factions-*.log found under $logsDir" -ForegroundColor Red
        exit 1
    }

    $windowLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Get-Content $log.FullName -Tail 20000 -ErrorAction SilentlyContinue)) {
        if ($line -match '^(\d{2}):(\d{2}):(\d{2})') {
            $tod = New-TimeSpan -Hours ([int]$matches[1]) -Minutes ([int]$matches[2]) -Seconds ([int]$matches[3])
            if ($tod -ge $probeStartTod) { [void]$windowLines.Add($line) }
        }
    }
    $notes.Add("window: $($windowLines.Count) log line(s) since probe start")

    # --- Signal 1: OperationsManager fair-share (multiple destinations per item per tick) ---
    # Line shape: "HH:mm:ss  mgr <TAG> ITEM_XFER <Item> +<qty> from=<node> to=<node>"
    $xferGroups = @{}
    foreach ($line in $windowLines) {
        if ($line -match '^(\d{2}:\d{2}:\d{2})\s+mgr (\S+) ITEM_XFER (\S+) \+\d+ from=(\S+) to=(\S+)') {
            $key = "$($matches[1])|$($matches[2])|$($matches[3])"
            if (-not $xferGroups.ContainsKey($key)) { $xferGroups[$key] = New-Object System.Collections.Generic.HashSet[string] }
            [void]$xferGroups[$key].Add($matches[5])
        }
    }
    if ($xferGroups.Count -eq 0) {
        $notes.Add("ITEM_XFER: no transfers observed in this window (inconclusive, not a failure -- no faction had both a shortfall and surplus to route this tick; rerun with a longer -MonitorSeconds or during heavier load)")
    } else {
        $maxDestCount = ($xferGroups.Values | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
        $fanoutGroups = ($xferGroups.Values | Where-Object { $_.Count -gt 1 }).Count
        if ($maxDestCount -gt 1) {
            $positiveSignals.Add("OperationsManager fair-share: observed $fanoutGroups (faction,item,tick) group(s) with multiple distinct destinations in one pass (max $maxDestCount) -- winner-take-all would cap this at 1")
        } else {
            $negativeSignals.Add("OperationsManager fair-share: $($xferGroups.Count) transfer group(s) observed, none with more than 1 destination -- expected multi-destination fan-out under the fix; either back to winner-take-all behavior or this window just didn't have concurrent demand")
        }
    }

    # --- Signal 2: PriceOracle value-basing (no six-figure undivided-budget collapse) ---
    # Line shape: "HH:mm:ss  mgr <TAG> PG_SURPLUS_DEMAND node=<id> item=<key> stock=<n> target=<n> qty=<n>"
    $targets = New-Object System.Collections.Generic.List[long]
    foreach ($line in $windowLines) {
        if ($line -match 'PG_SURPLUS_DEMAND .*target=(\d+)') { [void]$targets.Add([long]$matches[1]) }
    }
    if ($targets.Count -eq 0) {
        $notes.Add("PG_SURPLUS_DEMAND: no new demand postings in this window (inconclusive, not a failure -- this line only logs on a NEW demand entry, not a refresh; existing demands may all still be within TTL)")
    } else {
        $maxTarget = ($targets | Measure-Object -Maximum).Maximum
        if ($maxTarget -gt $MaxSaneSurplusTarget) {
            $negativeSignals.Add("PriceOracle: max PG_SURPLUS_DEMAND target=$maxTarget exceeds sane bound $MaxSaneSurplusTarget (pre-fix collapse produced 148,660-157,114 for a single zero-stock subtype) -- possible regression")
        } else {
            $positiveSignals.Add("PriceOracle: $($targets.Count) PG_SURPLUS_DEMAND posting(s) observed, max target=$maxTarget, bounded well under the pre-fix six-figure collapse value")
        }
    }
}
finally {
    # --- Restore: always put LogItemXfer back, whatever happened above ---
    try {
        $obj = Get-Content $tuningPath -Raw | ConvertFrom-Json
        $obj.LogItemXfer = $originalLogItemXfer
        ($obj | ConvertTo-Json -Depth 10) | Set-Content -Path $tuningPath -Encoding UTF8
        Send-BridgeCommand 'factions reload'
        Start-Sleep -Seconds 3
        $restored = $true
    } catch {
        Write-Host "WARN: failed to restore LogItemXfer to $originalLogItemXfer -- fix tuning.json by hand: $_" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "=== Probe summary ===" -ForegroundColor Cyan
Write-Host "restored_state: $restored"
foreach ($n in $notes)           { Write-Host "note: $n" -ForegroundColor Gray }
foreach ($p in $positiveSignals) { Write-Host "PASS: $p" -ForegroundColor Green }
foreach ($n in $negativeSignals) { Write-Host "FAIL: $n" -ForegroundColor Red }

if ($negativeSignals.Count -gt 0) { exit 1 }
exit 0
