#requires -Version 5.1
<#
.SYNOPSIS
  Automated Torch test-loop for the Factions plugin: boot Torch headless against a test instance,
  wait for the plugin to load (and optionally run a console command), then shut it down.
  Exit 0 = plugin loaded OK, 1 = failure/timeout.

.DESCRIPTION
  The "Torch started automatically as part of the test loop" piece. It does NOT create a world or
  download the dedicated server (one-time GUI setup). Torch reuses a daily log file, so this script
  only trusts log lines whose embedded HH:mm:ss timestamp is at/after this launch; otherwise stale
  markers from earlier runs match immediately.

.NOTES
  Validated on Torch 1.3.1.340: headless boot (-nogui -console -noupdate -autostart) works.
  Console-command delivery (stdin) and graceful 'stop' are best-effort; kills after a grace period.
  ASCII-only on purpose (Windows PowerShell 5.1 mis-decodes non-ASCII in .ps1).

.EXAMPLE
  ./Run-ServerTest.ps1 -RunCommand 'factions init'
#>
[CmdletBinding()]
param(
    [string]$TorchRoot              = 'C:\Torch\torch-server',
    [string]$InstanceName           = 'Instance',
    [int]   $BootTimeoutSec         = 360,
    [string]$ReadyMarker            = 'Factions: session loaded',
    [string]$FailMarker             = '',
    [string]$RunCommand             = '',
    [string]$BridgeCommand          = '',
    [int]   $ReadyToCommandDelaySec = 4,
    [int]   $PostCommandWaitSec     = 10,
    [int]   $StopGraceSec           = 25,
    [switch]$Deploy,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $TorchRoot 'Torch.Server.exe'
if (-not (Test-Path $exe)) { throw "Torch.Server.exe not found at $exe" }

$script:launchTod = [timespan]::Zero

# Only the log lines whose embedded HH:mm:ss is at/after this launch (robust vs. the daily log file).
# Use Torch-*.log specifically: Torch plugin messages land there; Keen-*.log is the SE game log and
# gets touched later by autosaves, which causes it to sort first and mask the plugin markers.
function Get-RunLog {
    $f = Get-ChildItem (Join-Path $TorchRoot 'Logs\Torch-*.log') -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { return @() }
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Get-Content $f.FullName -ErrorAction SilentlyContinue)) {
        if ($line -match '^(\d{2}):(\d{2}):(\d{2})') {
            $tod = New-TimeSpan -Hours ([int]$matches[1]) -Minutes ([int]$matches[2]) -Seconds ([int]$matches[3])
            if ($tod -ge $script:launchTod) { [void]$out.Add($line) }
        }
    }
    return $out.ToArray()
}

function Stop-TorchProc($p) {
    if ($null -eq $p -or $p.HasExited) { return }
    Write-Host "Stopping Torch (graceful, ${StopGraceSec}s)..." -ForegroundColor Cyan
    try { $p.StandardInput.WriteLine('stop'); $p.StandardInput.Flush() } catch { }
    if (-not $p.WaitForExit($StopGraceSec * 1000)) { Write-Host "  graceful stop did not land; killing." -ForegroundColor Yellow; try { $p.Kill() } catch { } }
    else { Write-Host "  stopped gracefully." -ForegroundColor Green }
}

if ($Deploy) {
    Write-Host "Deploying Factions plugin..." -ForegroundColor Cyan
    # -TorchRoot, not just -PluginsDir: the instance data files follow the root, and passing only
    # the plugins dir would deploy the zip here and its tuning defaults into the live server.
    & (Join-Path $PSScriptRoot 'build.ps1') -Deploy -TorchRoot $TorchRoot
}

if (Get-Process -Name 'Torch.Server' -ErrorAction SilentlyContinue) {
    throw "A Torch.Server process is already running. Stop it before running the test loop."
}

# floor to the whole second so a same-second log line is not lost on the boundary
$script:launchTod = [timespan]::FromSeconds([math]::Floor(((Get-Date).TimeOfDay).TotalSeconds))

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName              = $exe
$psi.Arguments             = "-nogui -console -noupdate -autostart -instancename `"$InstanceName`""
$psi.WorkingDirectory      = $TorchRoot
$psi.UseShellExecute       = $false
$psi.CreateNoWindow        = $true
$psi.RedirectStandardInput = $true
$proc = [System.Diagnostics.Process]::Start($psi)
Write-Host "Torch starting headless (pid $($proc.Id)); waiting up to ${BootTimeoutSec}s for '$ReadyMarker'..." -ForegroundColor Cyan

$deadline = (Get-Date).AddSeconds($BootTimeoutSec)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) { Write-Host "FAIL: Torch exited early (code $($proc.ExitCode))." -ForegroundColor Red; exit 1 }
    $log = Get-RunLog
    if ($log -match $ReadyMarker) { $ready = $true; break }
    if ($FailMarker -and ($log -match $FailMarker)) { Write-Host "FAIL: failure marker matched." -ForegroundColor Red; Stop-TorchProc $proc; exit 1 }
    Start-Sleep -Seconds 2
}
if (-not $ready) { Write-Host "FAIL: timed out waiting for '$ReadyMarker'." -ForegroundColor Red; Stop-TorchProc $proc; exit 1 }

Write-Host "READY: Factions plugin loaded (this run)." -ForegroundColor Green
$reg = (Get-RunLog) | Select-String -Pattern 'faction\(s\) in the registry' | Select-Object -Last 1
if ($reg) { Write-Host ("  " + $reg.ToString().Trim()) }

# Drive a server command through the plugin's file bridge (the reliable replacement for stdin):
# write the inbox, wait for the plugin to consume + run it, then surface the ack + plugin log lines.
if ($BridgeCommand) {
    $bridgeDir = Join-Path $TorchRoot (Join-Path $InstanceName 'Factions\bridge')
    if (-not (Test-Path $bridgeDir)) { New-Item -ItemType Directory -Path $bridgeDir -Force | Out-Null }
    $inbox  = Join-Path $bridgeDir 'inbox.txt'
    $outbox = Join-Path $bridgeDir 'outbox.txt'

    $outboxBefore = 0
    if (Test-Path $outbox) { $outboxBefore = @(Get-Content $outbox -ErrorAction SilentlyContinue).Count }

    Write-Host "Bridge: queueing '$BridgeCommand' -> $inbox" -ForegroundColor Cyan
    [System.IO.File]::WriteAllText($inbox, ($BridgeCommand -replace "`r`n", "`n"))

    $waited = 0
    while ($waited -lt 20 -and (Test-Path $inbox)) { Start-Sleep -Seconds 2; $waited += 2 }
    Start-Sleep -Seconds 4   # let the dispatched command finish and flush its log/ack

    if (Test-Path $inbox) { Write-Host "  WARN: inbox not consumed after ${waited}s." -ForegroundColor Yellow }
    else { Write-Host "  inbox consumed by plugin (after ${waited}s)." -ForegroundColor Green }

    if (Test-Path $outbox) {
        $ack = Get-Content $outbox -ErrorAction SilentlyContinue | Select-Object -Skip $outboxBefore
        if ($ack) { Write-Host "Bridge outbox:" -ForegroundColor Cyan; $ack | ForEach-Object { Write-Host ("  " + $_) } }
    }
    $hits = (Get-RunLog) | Select-String -Pattern 'bridge ran|scan examined|init registered|Registered .* NPC' | Select-Object -Last 8
    if ($hits) { Write-Host "Bridge log:" -ForegroundColor Cyan; $hits | ForEach-Object { Write-Host ("  " + $_.ToString().Trim()) } }
}

if ($RunCommand) {
    Start-Sleep -Seconds $ReadyToCommandDelaySec
    Write-Host "Sending console command: '$RunCommand'" -ForegroundColor Cyan
    try { $proc.StandardInput.WriteLine($RunCommand); $proc.StandardInput.Flush() } catch { Write-Host "  (stdin write failed: $_)" -ForegroundColor Yellow }
    Start-Sleep -Seconds $PostCommandWaitSec
    $after = (Get-RunLog) | Select-String -Pattern 'init registered|Registered .* NPC faction|No world loaded|not initialised' | Select-Object -Last 5
    if ($after) { $after | ForEach-Object { Write-Host ("  " + $_.ToString().Trim()) } }
    else { Write-Host "  (no init result line in log; stdin command may not have been processed)" -ForegroundColor Yellow }
}

if ($KeepRunning) { Write-Host "Leaving Torch running (pid $($proc.Id))." -ForegroundColor Yellow; exit 0 }

Stop-TorchProc $proc
Write-Host "Test loop complete." -ForegroundColor Green
exit 0
