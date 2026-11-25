#requires -Version 5.1
<#
.SYNOPSIS
  Build + package the Factions Torch plugin into a Plugins-ready zip (DLL + manifest).
  Also builds the Factions.Api server and Factions.Dashboard web UI.
.EXAMPLE
  ./build.ps1                     # build Release + produce dist/Factions.zip
.EXAMPLE
  ./build.ps1 -Deploy             # copy the zip and restart Torch through its own bridge
.EXAMPLE
  ./build.ps1 -ApiOnly            # build only Factions.Api (no Torch plugin)
.EXAMPLE
  ./build.ps1 -Deploy -TorchRoot 'C:\Torch\awl-standalone-test'
  # Deploy the plugin zip AND the instance data files to a second Torch host. The Factions.Api
  # dashboard is republished and restarted on EVERY deploy -- it is one service for this box and it
  # shares Factions.Core, so a stale one disagrees with the plugin about the files both read. Only
  # the TORCH restart is per-root: restart that host yourself to pick the plugin build up.
.EXAMPLE
  ./build.ps1 -Deploy -TorchRoot 'C:\Torch\awl-standalone-test' -LiveTorchRoot 'C:\Torch\awl-standalone-test'
  # Same, and treat that root as the live one, so its Torch is restarted too.
.EXAMPLE
  ./build.ps1 -Deploy -TorchRoot 'C:\Torch\awl-standalone-test' -LiveTorchRoot 'C:\Torch\awl-standalone-test' -SkipTorchRestart
  # Same, but leave Torch alone. For a deploy whose payload is not in the plugin DLL -- a dashboard
  # or Factions.Api change -- where the restart costs a running measurement or a player's session.
  # The plugin zip is still written, so the server keeps the OLD DLL until something restarts it.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Deploy,
    [switch]$ApiOnly,
    # The Torch host to deploy into. Everything a deploy writes -- the plugin zip and the instance
    # data files -- is derived from this one path, so a second host cannot end up with the zip in
    # one root and its defaults in another.
    [string]$TorchRoot = 'C:\Torch\torch-server',
    [string]$PluginsDir = (Join-Path $TorchRoot 'Plugins'),
    # Which root owns the running Torch. Only the Torch start/stop is gated on this -- the
    # Factions.Api service is box-wide and is published on every deploy. A parameter rather than a
    # constant so it cannot silently rot: it named a host that had been down for three days, and
    # every deploy in that window skipped the dashboard without anyone noticing (FAC-123).
    [string]$LiveTorchRoot = 'C:\Torch\torch-server',
    # Deploy the files and republish the dashboard, but do not restart Torch. For a change whose
    # payload is not in the plugin DLL, where the bounce is pure cost: it empties the demand
    # register for 2-4 minutes, resets every per-process debug toggle, and interrupts anyone in
    # game. It does NOT make the deploy cheaper to verify -- the zip is still rewritten while the
    # running server keeps the old DLL, which is exactly FAC-095's trap, so this reports itself in
    # the same terms the secondary-root path does.
    [switch]$SkipTorchRestart,
    # The Torch root the dashboard should read. Defaults to the root being deployed, which is what
    # anyone deploying a single host wants; name it explicitly when deploying a root that is NOT the
    # one the dashboard should follow, so a deploy cannot silently repoint it at another world.
    [string]$DashboardRoot = $TorchRoot
)
$ErrorActionPreference = 'Stop'
$root     = $PSScriptRoot
$proj     = Join-Path $root 'src\Factions\Factions.csproj'
$apiProj  = Join-Path $root 'src\Factions.Api\Factions.Api.csproj'
$dashDir  = Join-Path $root 'src\Factions.Dashboard'
$manifest = Join-Path $root 'manifest.xml'
$dist     = Join-Path $root 'dist'
$outDir   = Join-Path $root "src\Factions\bin\$Configuration"
# Only the Torch process is per-root. The Factions.Api service is one per box and is published on
# every deploy -- it shares Factions.Core with the plugin, so a deploy that moves the persistence
# schema without republishing it leaves two builds disagreeing about files they both read.
$liveTorchRoot = $LiveTorchRoot
$isLiveRoot = ($TorchRoot.TrimEnd('\') -ieq $liveTorchRoot.TrimEnd('\'))
$torchExe  = Join-Path $TorchRoot 'Torch.Server.exe'
$apiLiveDir = Join-Path $root 'tmp\api-live'
$apiUrl = 'http://0.0.0.0:7891'

function Get-TorchProcess {
    # Non-elevated SSH sessions can see Torch.Server but may not be allowed to
    # read its Path/StartTime properties, so matching on $_.Path makes a live
    # Torch process look absent. Match by process name first; this host should
    # only run one Torch.Server instance for the live test server.
    $proc = Get-Process -Name 'Torch.Server' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { return $proc }
    return $null
}
function Get-LatestTorchLogFile {
    Get-ChildItem (Join-Path $script:TorchRoot 'Logs\Torch-*.log') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Get-RecentTorchLogLines {
    param([timespan]$SinceTod)

    $f = Get-LatestTorchLogFile
    if (-not $f) { return @() }

    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Get-Content $f.FullName -Tail 250 -ErrorAction SilentlyContinue)) {
        if ($line -match '^(\d{2}):(\d{2}):(\d{2})') {
            $tod = New-TimeSpan -Hours ([int]$matches[1]) -Minutes ([int]$matches[2]) -Seconds ([int]$matches[3])
            if ($tod -ge $SinceTod) { [void]$out.Add($line) }
        }
    }
    return $out.ToArray()
}

function Send-TorchBridgeCommand {
    param([string[]]$Lines)

    $bridgeDir = Join-Path $script:TorchRoot 'Instance\Factions\bridge'
    New-Item -ItemType Directory -Force -Path $bridgeDir | Out-Null
    $inbox = Join-Path $bridgeDir 'inbox.txt'
    [System.IO.File]::WriteAllText($inbox, (($Lines -join "`n") + "`n"))
    return $inbox
}

function Get-TorchLogBaseline {
    # Anchored to the action, not to the waiter: captured BEFORE the restart is requested so a
    # boot that completes during the first poll interval is still seen. Line offset rather than a
    # timestamp comparison, which breaks across hour and midnight boundaries.
    $f = Get-LatestTorchLogFile
    if (-not $f) { return [pscustomobject]@{ Path = $null; Lines = 0 } }
    $count = 0
    # Every line, blank ones included: Select-Object -Skip counts them, Measure-Object -Line does not.
    try { $count = @(Get-Content $f.FullName -ErrorAction Stop).Count } catch { }
    return [pscustomobject]@{ Path = $f.FullName; Lines = $count }
}

function Test-TorchBootedSince {
    param($Baseline)

    $f = Get-LatestTorchLogFile
    if (-not $f) { return $false }
    # A rolled log (new date) means the boot marker is in a different file, at offset 0.
    $skip = if ($f.FullName -eq $Baseline.Path) { $Baseline.Lines } else { 0 }
    $lines = @()
    try { $lines = @(Get-Content $f.FullName -ErrorAction Stop | Select-Object -Skip $skip) } catch { return $false }
    foreach ($line in $lines) {
        if ($line -match 'Torch: Starting server watchdog\.') { return $true }
    }
    return $false
}

<#
  Prints the "files copied, Torch not restarted" notice, for every path that deploys without
  bouncing the server. The wording is deliberately blunt and the PID is the point: such a deploy
  rewrites Factions.zip -- so its mtime, length and hash all change -- while the running server
  keeps the OLD DLL loaded. Every artifact check an operator naturally reaches for therefore
  passes on a deploy that has not landed, and only a changed process id distinguishes the two.
  FAC-095.

  One function rather than one block per caller, because the callers must not drift: a secondary
  root and -SkipTorchRestart leave the server in the same state and owe the reader the same
  sentence.
#>
function Write-TorchNotRestartedNotice {
    param(
        [Parameter(Mandatory)][string]$Headline,
        [Parameter(Mandatory)][string]$RootForBridge,
        [bool]$ApiPublished
    )

    $torchPid = (Get-Process Torch.Server -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    $inbox = Join-Path $RootForBridge 'Instance\Factions\bridge\inbox.txt'

    Write-Host ""
    Write-Host "=== NOT DEPLOYED TO THE RUNNING SERVER =========================" -ForegroundColor Yellow
    Write-Host $Headline -ForegroundColor Yellow
    if ($ApiPublished) {
        Write-Host "  Skipped: the Torch restart only. The Factions.Api dashboard WAS republished" -ForegroundColor Yellow
        Write-Host "  and restarted above -- it is one service for this box, not a per-root one." -ForegroundColor Yellow
    } else {
        Write-Host "  Skipped: the Torch restart. The dashboard publish FAILED -- see above." -ForegroundColor Yellow
    }
    Write-Host "  The running server still has the OLD Factions.dll until it restarts." -ForegroundColor Yellow
    Write-Host ""
    if ($torchPid) {
        Write-Host "  Torch.Server is running as pid $torchPid." -ForegroundColor Yellow
        Write-Host "  Restart it, then confirm the pid CHANGED. A rewritten zip is not evidence:" -ForegroundColor Yellow
        Write-Host "  its timestamp changes whether or not the server ever loads it." -ForegroundColor Yellow
    } else {
        Write-Host "  No Torch.Server process is running, so there is nothing to restart" -ForegroundColor Yellow
        Write-Host "  and nothing has picked this build up. Start it to load this artifact." -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "  Restart via the bridge (one atomic write, both verbs in one file):" -ForegroundColor Yellow
    Write-Host "    [System.IO.File]::WriteAllText('$inbox', "save``nrestart``n")" -ForegroundColor Yellow
    Write-Host "  Then wait for a FRESH 'Torch: Starting server watchdog.' past a baseline" -ForegroundColor Yellow
    Write-Host "  captured BEFORE the restart -- the log keeps every previous one." -ForegroundColor Yellow
    Write-Host "  Debug traces are per-process and will be OFF after it; re-enable them." -ForegroundColor Yellow
    Write-Host "================================================================" -ForegroundColor Yellow
}

function Stop-TorchGracefully {
    <#
      Restarts Torch through its own bridge and reports what happened. Returns one of:
        'NotRunning' - nothing was running, so the caller must start Torch itself
        'Restarted'  - Torch came back on its own; the caller must NOT start another one
        'Unknown'    - no boot marker within the deadline; caller must not kill or start anything

      Deliberately never kills Torch. The previous version waited for the process to disappear and
      force-killed it when it did not, which is wrong in both of Torch's restart shapes: an
      in-process restart keeps the same PID forever, and a process-replacing restart brings the new
      one up faster than a poll for "is any Torch.Server running" can see the gap. Either way the
      wait expired against a perfectly healthy server and then killed it. Success is now a fresh
      boot marker in the log, or a PID that actually changed -- never the absence of a process.
    #>
    $proc = Get-TorchProcess
    if (-not $proc) {
        Write-Host "Torch is not running; no shutdown needed." -ForegroundColor Yellow
        return 'NotRunning'
    }

    $originalPid = $proc.Id
    $baseline = Get-TorchLogBaseline
    $stopTod = [timespan]::FromSeconds([math]::Floor(((Get-Date).TimeOfDay).TotalSeconds))
    Write-Host "Requesting Torch save + restart via bridge (pid $originalPid)..." -ForegroundColor Cyan
    Send-TorchBridgeCommand -Lines @('save', 'restart') | Out-Null

    $deadline = (Get-Date).AddSeconds(180)
    $seen = New-Object System.Collections.Generic.HashSet[string]
    while ($true) {
        foreach ($line in (Get-RecentTorchLogLines -SinceTod $stopTod)) {
            if (($line -match 'Torch: Saving server\.' -or $line -match 'Torch: Restarting server\.' -or $line -match 'Torch: Server started\.' -or $line -match 'Torch: Server stopped\.') -and $seen.Add($line)) {
                Write-Host $line -ForegroundColor DarkGray
            }
        }

        # Check before sleeping: Torch often boots inside the first poll interval.
        if (Test-TorchBootedSince -Baseline $baseline) {
            $now = Get-TorchProcess
            $nowPid = if ($now) { $now.Id } else { '(none)' }
            Write-Host "Torch restarted and reached its watchdog (pid $nowPid)." -ForegroundColor Green
            return 'Restarted'
        }

        $current = Get-TorchProcess
        if ($current -and $current.Id -ne $originalPid) {
            Write-Host "Torch process replaced (pid $originalPid -> $($current.Id))." -ForegroundColor Green
            return 'Restarted'
        }

        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Seconds 2
    }

    Write-Host "Torch did not report a watchdog within the deadline, and its PID did not change." -ForegroundColor Yellow
    Write-Host "NOT killing it and NOT starting another instance -- the server may simply be slow to boot." -ForegroundColor Yellow
    Write-Host "Check $TorchRoot\Logs and start Torch by hand if it really is down." -ForegroundColor Yellow
    return 'Unknown'
}

function Start-TorchVisible {
    $args = '-console -autostart -instancename "Instance"'
    Write-Host "Starting Torch in a visible console window..." -ForegroundColor Cyan
    $p = Start-Process -FilePath $script:torchExe -ArgumentList $args -WorkingDirectory $script:TorchRoot -WindowStyle Normal -PassThru
    Write-Host "Torch started (pid $($p.Id))." -ForegroundColor Green
    return $p
}

# The Factions data directory this instance reads, resolved the way DataDirectory.Resolve does:
# tuning.json's DataDirectory key, empty = the instance directory, relative = under it. Shared world
# inputs belong there, because every instance running one world must read the same copy (FAC-241).
# A tuning.json that cannot be read returns $null rather than guessing the instance directory: a guess
# would put the file exactly where a redirected server does not look.
function Resolve-FactionsDataDir {
    param([string]$InstanceDir)
    $tuningPath = Join-Path $InstanceDir 'tuning.json'
    if (-not (Test-Path $tuningPath)) { return $InstanceDir }
    try {
        $tuning = Get-Content $tuningPath -Raw | ConvertFrom-Json
    } catch {
        Write-Warning "Could not read DataDirectory from ${tuningPath}: $_"
        return $null
    }
    $configured = [string]$tuning.DataDirectory
    if ([string]::IsNullOrWhiteSpace($configured)) { return $InstanceDir }
    $configured = $configured.Trim()
    if ([System.IO.Path]::IsPathRooted($configured)) { return $configured }
    return (Join-Path $InstanceDir $configured)
}

function Get-ApiProcesses {
    $procs = @()
    $procs += @(Get-Process -Name 'Factions.Api' -ErrorAction SilentlyContinue)
    foreach ($p in @(Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue)) {
        $cmd = $null
        try { $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)" -ErrorAction Stop).CommandLine } catch { }
        if ($cmd -and ($cmd -match 'Factions\.Api' -or $cmd -match [regex]::Escape($script:apiLiveDir))) {
            $procs += $p
        }
    }
    return @($procs | Sort-Object Id -Unique)
}

function Wait-FactionsApiServiceStatus {
    param(
        [string]$Status,
        [int]$TimeoutSeconds = 60
    )

    $serviceName = 'Factions.Api'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if (-not $svc) { return $false }
        if ($svc.Status.ToString() -eq $Status) { return $true }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Wait-FactionsApiStatus {
    param([int]$TimeoutSeconds = 30)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    do {
        try {
            Invoke-RestMethod -Uri 'http://127.0.0.1:7891/api/status' -TimeoutSec 5 | Out-Null
            return $true
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 1
        }
    } while ((Get-Date) -lt $deadline)
    Write-Warning "Factions.Api did not answer /api/status within ${TimeoutSeconds}s. Last error: $lastError"
    return $false
}

function Stop-FactionsApi {
    $serviceName = 'Factions.Api'
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Host "Stopping Factions.Api service..." -ForegroundColor Cyan
        try { Stop-Service -Name $serviceName -Force -ErrorAction Stop }
        catch {
            Write-Warning "Stop-Service failed: $($_.Exception.Message); trying sc stop."
            sc.exe stop $serviceName | Out-Host
        }
        if (-not (Wait-FactionsApiServiceStatus -Status 'Stopped' -TimeoutSeconds 90)) {
            throw "Factions.Api service did not reach Stopped within timeout."
        }
        Write-Host "Factions.Api service stopped." -ForegroundColor Green
    } elseif ($svc) {
        Write-Host "Factions.Api service is already stopped." -ForegroundColor Yellow
    } else {
        Write-Host "Factions.Api service is not installed; checking for ad-hoc API processes." -ForegroundColor Yellow
    }

    $procs = Get-ApiProcesses
    foreach ($p in $procs) {
        Write-Host "Stopping stray Factions.Api process pid $($p.Id)..." -ForegroundColor Cyan
        try {
            Stop-Process -Id $p.Id -Force -ErrorAction Stop
        } catch {
            Write-Warning "Stop-Process could not stop API pid $($p.Id): $($_.Exception.Message); trying taskkill."
            $taskkillOut = cmd /c "taskkill /PID $($p.Id) /F 2>&1"
            if ($LASTEXITCODE -ne 0) { Write-Warning "taskkill could not stop API pid $($p.Id): $taskkillOut" }
        }
    }

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $left = Get-ApiProcesses
        if (-not $left -or $left.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    }
    $left = Get-ApiProcesses
    if ($left -and $left.Count -gt 0) {
        throw "Factions.Api process(es) are still running after stop timeout."
    }
}

function Publish-FactionsApiLive {
    param([string]$BuiltApiOutDir)

    Write-Host "Publishing Factions.Api live output..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $script:apiLiveDir | Out-Null

    $target = Join-Path $script:apiLiveDir 'Factions.Api.dll'
    $before = (Get-Item $target -ErrorAction SilentlyContinue).LastWriteTimeUtc

    # The service reaching Stopped does not mean its file handles are closed. A copy issued
    # immediately after can move NOTHING, report no error, and leave -ErrorVariable empty -- observed
    # 2026-08-26, and the reason the live DLL sat three days old while every deploy "succeeded".
    Start-Sleep -Seconds 3

    $copyErrors = @()
    Copy-Item (Join-Path $BuiltApiOutDir '*') $script:apiLiveDir -Recurse -Force `
        -ErrorAction SilentlyContinue -ErrorVariable copyErrors
    foreach ($e in $copyErrors) { Write-Warning "API publish copy: $e" }

    # Verify the artifact actually moved rather than trusting the copy. A silent no-op here is
    # indistinguishable from a successful publish everywhere downstream.
    if (-not (Test-Path $target)) { throw "API publish produced no $target." }
    $after = (Get-Item $target).LastWriteTimeUtc
    if ($before -and $after -le $before) {
        throw ("API publish did not replace $target (still $($after.ToString('o'))). " +
               "The service is probably still holding it.")
    }
    Write-Host "Factions.Api.dll published, stamped $($after.ToString('o'))." -ForegroundColor Green
}

function Start-FactionsApi {
    Write-Host "Starting Factions.Api service on $script:apiUrl..." -ForegroundColor Cyan
    $exe = Join-Path $script:apiLiveDir 'Factions.Api.exe'
    if (-not (Test-Path $exe)) { throw "missing API live output: $exe" }
    $serviceName = 'Factions.Api'
    # --instance-dir, not --data-dir: this names the Torch root's own Instance\Factions, and the API
    # resolves the real data directory out of that instance's tuning.json the way the plugin does.
    # The two are the same folder on a single-root host and are NOT on a shared-data one.
    $instanceDir = Join-Path $script:DashboardRoot 'Instance\Factions'
    $binPath = '"' + $exe + '" --instance-dir "' + $instanceDir + '"'
    Write-Host "Factions.Api instance dir: $instanceDir" -ForegroundColor Cyan

    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "Creating Windows service $serviceName..." -ForegroundColor Cyan
        New-Service -Name $serviceName -DisplayName 'Factions Api' -BinaryPathName $binPath -StartupType Automatic | Out-Null
    } else {
        # Not piped to Out-Null. sc.exe config needs elevation, and a non-elevated run fails with
        # "Access is denied" while everything after it succeeds -- so the service keeps its OLD
        # command line and the operator has no way to tell.
        $cfg = & sc.exe config $serviceName binPath= $binPath start= auto 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning ("sc.exe config failed ($LASTEXITCODE): $cfg")
            Write-Warning ("The service keeps its previous command line -- the new binaries WILL " +
                           "load, but the instance dir is unchanged. Re-run elevated to repoint it.")
        }
    }

    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    if ($svc.Status -ne 'Running') {
        Start-Service -Name $serviceName
    } else {
        Stop-Service -Name $serviceName -Force
        if (-not (Wait-FactionsApiServiceStatus -Status 'Stopped' -TimeoutSeconds 90)) {
            throw "Factions.Api service did not reach Stopped during restart."
        }
        Start-Service -Name $serviceName
    }
    if (-not (Wait-FactionsApiServiceStatus -Status 'Running' -TimeoutSeconds 90)) {
        throw "Factions.Api service did not reach Running within timeout."
    }
    if (-not (Wait-FactionsApiStatus -TimeoutSeconds 45)) {
        throw "Factions.Api service is Running but /api/status did not respond."
    }
    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    Write-Host "Factions.Api service is running and responding." -ForegroundColor Green
    return $svc
}

# ---- Build outputs that can tolerate a running API process ---------------------
# Build the API into a temp output dir so a running Factions.Api.exe does not lock
# the normal bin\Release\net9.0 output folder during package/deploy operations.
$apiOutDir = Join-Path $root ('tmp\api-out\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $apiOutDir | Out-Null

# ---- Dashboard (always build; outputs to Factions.Api/wwwroot/) -----------------
if (Test-Path $dashDir) {
    try {
        Write-Host "Building Factions.Dashboard..." -ForegroundColor Cyan
        Push-Location $dashDir
        $ea = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        npm run build
        $npmExit = $LASTEXITCODE
        $ErrorActionPreference = $ea
        if ($npmExit -ne 0) { throw "npm build failed ($npmExit)" }
        Pop-Location
    } catch {
        Write-Warning "Skipping dashboard build: $($_.Exception.Message)"
        try { Pop-Location | Out-Null } catch { }
    }
}

# ---- Factions.Api ---------------------------------------------------------------
Write-Host "Building Factions.Api ($Configuration) into temp output..." -ForegroundColor Cyan
dotnet build $apiProj -c $Configuration --nologo -o $apiOutDir
if ($LASTEXITCODE -ne 0) { throw "Factions.Api build failed ($LASTEXITCODE)" }

if ($ApiOnly) {
    Write-Host "ApiOnly flag set; skipping Torch plugin build." -ForegroundColor Yellow
    if ($Deploy) {
        # -ApiOnly -Deploy used to build the API and then exit without publishing it, which is the
        # same gap this whole path had: a successful-looking run that shipped nothing.
        Stop-FactionsApi
        Publish-FactionsApiLive -BuiltApiOutDir $apiOutDir
        Start-FactionsApi | Out-Null
    }
    exit 0
}

# ---- Torch plugin ---------------------------------------------------------------
Write-Host "Building Factions ($Configuration)..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$dll = Join-Path $outDir 'Factions.dll'
if (-not (Test-Path $dll)) { throw "missing build output: $dll" }

# A Torch plugin zip = the plugin DLL(s) + manifest.xml at the zip root.
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip     = Join-Path $dist ('Factions-' + [Guid]::NewGuid().ToString('N') + '.zip')
$staging = Join-Path $dist ('_staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item $dll $staging
# Bundle the SE-free brain alongside the plugin (Newtonsoft is provided by Torch, so not bundled).
$coreDll = Join-Path $outDir 'Factions.Core.dll'
if (-not (Test-Path $coreDll)) { throw "missing build output: $coreDll" }
Copy-Item $coreDll $staging
# Harmony is not provided by Torch; bundle our copy.
$harmonyDll = Join-Path $outDir '0Harmony.dll'
if (-not (Test-Path $harmonyDll)) { throw "missing build output: $harmonyDll (restore NuGet packages)" }
Copy-Item $harmonyDll $staging
Copy-Item $manifest $staging
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
try { Remove-Item $staging -Recurse -Force } catch { Write-Warning "Could not remove staging dir ${staging}: $($_.Exception.Message)" }
Write-Host "Packaged $zip" -ForegroundColor Green

if ($Deploy) {
    if (-not (Test-Path $TorchRoot)) { throw "Torch root not found: $TorchRoot" }
    # A missing Plugins dir under an existing root is a fresh host, not a typo -- create it. A
    # missing root is a typo, and threw above.
    New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null
    # FAC-095 trap 3. FactionsUI.xml's SourceDirectories and the deploy checkout's sparse-checkout
    # are two separate lists with nothing keeping them in step, and a directory listed in one and
    # absent from the other presents to Pulsar as "the client plugin is broken" rather than as a
    # missing file. Warn, never fail: this script does not own that checkout, and a server deploy
    # must not be blocked by the state of the client one.
    function Assert-ClientSourcesPresent {
        $checker = Join-Path $root 'scripts\Check-ClientSources.ps1'
        if (-not (Test-Path $checker)) { return }
        if (-not (Test-Path 'C:\Torch\FactionsUI-dev')) { return }
        try {
            & $checker | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host ""
                Write-Host "WARNING: the client checkout is missing source directories that" -ForegroundColor Red
                Write-Host "FactionsUI.xml lists. Pulsar will fail to compile it. Details:" -ForegroundColor Red
                Write-Host "  .\scripts\Check-ClientSources.ps1" -ForegroundColor Red
            }
        } catch {
            Write-Host "Client source check could not run: $_" -ForegroundColor Yellow
        }
    }

    # FAC-082. What refuses a client link is the ProtocolVersion in the two artifacts, never the
    # branch either checkout sits on: three live refusals happened with both on `testing`. Read it
    # from the source this run built and from the client checkout Pulsar compiles. Warn, never fail,
    # for the same reason as above -- this script does not own that checkout.
    function Get-WireProtocolVersion([string]$checkout) {
        $file = Join-Path $checkout 'src\Factions.Wire\FactionsWire.cs'
        if (-not (Test-Path $file)) { return $null }
        $hit = Select-String -Path $file -Pattern 'public const int ProtocolVersion\s*=\s*(\d+)' | Select-Object -First 1
        if (-not $hit) { return $null }
        return [int]$hit.Matches[0].Groups[1].Value
    }

    function Assert-ClientProtocolMatches {
        $clientCheckout = 'C:\Torch\FactionsUI-dev'
        if (-not (Test-Path $clientCheckout)) { return }
        try {
            $deployed = Get-WireProtocolVersion $root
            $client   = Get-WireProtocolVersion $clientCheckout
            if ($null -eq $deployed -or $null -eq $client) {
                Write-Host "Wire protocol could not be read (deploying: '$deployed', FactionsUI-dev: '$client')." -ForegroundColor Yellow
                return
            }
            if ($deployed -eq $client) {
                Write-Host "Wire protocol: deploying $deployed, FactionsUI-dev $client -- clients built from it will link." -ForegroundColor Green
                return
            }
            # Never fatal: under Stop, PS 5.1 turns git's stderr into a terminating error, and the
            # one thing this block must not do is swallow its own warning.
            $commit = '<the deployed commit>'
            try {
                $head = (& git -C $root rev-parse --short HEAD 2>$null)
                if ($LASTEXITCODE -eq 0 -and $head) { $commit = $head }
            } catch { }
            Write-Host ""
            Write-Host "================================================================" -ForegroundColor Red
            Write-Host "WARNING: WIRE PROTOCOL MISMATCH -- deploying $deployed, C:\Torch\FactionsUI-dev holds $client." -ForegroundColor Red
            Write-Host "Every client built from FactionsUI-dev will be refused at the uplink:" -ForegroundColor Red
            Write-Host "  UPLINK_REFUSED ... reason=protocol $client != $deployed" -ForegroundColor Red
            Write-Host "Move it to the commit deployed here (its own claim target, C:\Torch\FactionsUI-dev):" -ForegroundColor Red
            Write-Host "  git -C C:\Torch\FactionsUI-dev fetch origin" -ForegroundColor Red
            Write-Host "  git -C C:\Torch\FactionsUI-dev checkout --detach $commit" -ForegroundColor Red
            Write-Host "then players restart Space Engineers so Pulsar recompiles it." -ForegroundColor Red
            Write-Host "================================================================" -ForegroundColor Red
            Write-Host ""
        } catch {
            Write-Host "Wire protocol check could not run: $_" -ForegroundColor Yellow
        }
    }

    Copy-Item $zip (Join-Path $PluginsDir 'Factions.zip') -Force
    Write-Host "Deployed to $PluginsDir\Factions.zip." -ForegroundColor Green

    # Copy instance data files (defaults and tracked station-name additions shipped with the plugin;
    # tuning.json and the generated catalogue/usage files remain operator/runtime managed).
    # tuning_defaults.json is not optional: LoadTuning skips ApplyVersionedContractDefaults entirely
    # when it is absent, so a host without it authors a tuning.json from the C# field initialisers --
    # an older TuneVersion, and a zero value for every key whose real default ships only in the file.
    $instanceDir = Join-Path $TorchRoot 'Instance\Factions'
    New-Item -ItemType Directory -Force -Path $instanceDir | Out-Null
    Copy-Item (Join-Path $root 'instance\tuning_defaults.json') (Join-Path $instanceDir 'tuning_defaults.json') -Force
    Write-Host "Deployed instance defaults to $instanceDir." -ForegroundColor Green

    # station_name_additions.json is shared world data, so it goes to the data directory, and the
    # instance copy goes: left behind, it is a second copy that diverges on the next edit. Overwriting
    # the shared copy is right for this file alone -- it ships with the plugin and must match the DLL;
    # operator-edited inputs in that folder are never touched here.
    $namesSource = Join-Path $root 'instance\station_name_additions.json'
    $dataDir = Resolve-FactionsDataDir -InstanceDir $instanceDir
    if ($null -eq $dataDir) {
        Write-Host "station_name_additions.json NOT deployed: the data directory could not be resolved (see the warning above)." -ForegroundColor Red
    } else {
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        Copy-Item $namesSource (Join-Path $dataDir 'station_name_additions.json') -Force
        $instanceNames = Join-Path $instanceDir 'station_name_additions.json'
        if (($dataDir.TrimEnd('\') -ne $instanceDir.TrimEnd('\')) -and (Test-Path $instanceNames)) {
            Remove-Item $instanceNames -Force
            Write-Host "Removed the instance copy $instanceNames; the data directory holds it now." -ForegroundColor Green
        }
        Write-Host "Deployed station-name additions to $dataDir." -ForegroundColor Green
    }

    # Both roots pair with the one client checkout, so this runs on every deploy rather than only
    # the secondary path.
    Assert-ClientSourcesPresent

    # ---- The dashboard service, on EVERY deploy ---------------------------------
    # Deliberately outside the per-root branch below. There is one Factions.Api service on this box,
    # it shares Factions.Core with the plugin, and therefore shares a persistence schema: a deploy
    # that moves that schema without republishing it leaves two builds reading the same brain files
    # with different types. That is not hypothetical -- it dropped eleven of thirteen factions from
    # the dashboard, silently, because this step sat behind a live-root check naming a host that had
    # been down for three days (FAC-123, FAC-124).
    # Loud, but not fatal. The plugin zip is already on disk by this point, and the dashboard being
    # unhappy is not a reason to withhold the Torch restart instructions printed below.
    $apiPublished = $true
    try {
        Stop-FactionsApi
        Publish-FactionsApiLive -BuiltApiOutDir $apiOutDir
        Start-FactionsApi | Out-Null
    } catch {
        $apiPublished = $false
        Write-Host ""
        Write-Host "=== FACTIONS.API DASHBOARD NOT PUBLISHED =======================" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  The PLUGIN deploy above is unaffected -- the zip is on disk." -ForegroundColor Red
        Write-Host "  But the dashboard is now running OLDER code than the plugin, and the two" -ForegroundColor Red
        Write-Host "  share Factions.Core: they will disagree about any persisted shape that" -ForegroundColor Red
        Write-Host "  changed. Check /api/status -- 'ok' is false while any brain fails to load." -ForegroundColor Red
        Write-Host "================================================================" -ForegroundColor Red
        Write-Host ""
    }

    # Late, so the verdict sits next to the restart lines rather than scrolled above the API publish.
    Assert-ClientProtocolMatches

    # ---- Is the live-root parameter still telling the truth? ---------------------
    # The failure this guards against is not a wrong value, it is a value nobody re-reads. Compare
    # the two roots' newest Torch log: if the root being deployed has the fresher one, the root this
    # run is treating as live is almost certainly not the live one any more.
    if (-not $isLiveRoot) {
        $liveLog   = Get-ChildItem (Join-Path $liveTorchRoot 'Logs\Torch-*.log') -ErrorAction SilentlyContinue |
                     Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $targetLog = Get-ChildItem (Join-Path $TorchRoot 'Logs\Torch-*.log') -ErrorAction SilentlyContinue |
                     Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($targetLog -and (-not $liveLog -or $targetLog.LastWriteTimeUtc -gt $liveLog.LastWriteTimeUtc)) {
            Write-Host ""
            Write-Warning ("-LiveTorchRoot is '$liveTorchRoot', but '$TorchRoot' has the fresher Torch log " +
                           "(" + $targetLog.LastWriteTimeUtc.ToString('o') + " vs " +
                           $(if ($liveLog) { $liveLog.LastWriteTimeUtc.ToString('o') } else { 'none' }) + ").")
            Write-Warning ("Only the Torch restart is gated on it, so nothing was skipped that matters -- " +
                           "but pass -LiveTorchRoot '$TorchRoot' if that is now the live host.")
        }
    }

    if (-not $isLiveRoot) {
        # Only the live root owns a running Torch. Restarting or starting one for a secondary host
        # would fight the live server for its world and its ports.
        Write-TorchNotRestartedNotice `
            -Headline "Secondary Torch root ($TorchRoot): files copied, TORCH not restarted." `
            -RootForBridge $TorchRoot -ApiPublished $apiPublished
        exit 0
    }

    if ($SkipTorchRestart) {
        # Asked for explicitly, so this is not the FAC-123 accident the -LiveTorchRoot default was:
        # the caller named the live root AND said to leave it alone. It still owes the same notice,
        # because the state it leaves behind is identical to a secondary-root deploy's.
        Write-TorchNotRestartedNotice `
            -Headline "-SkipTorchRestart was passed: files copied, TORCH not restarted." `
            -RootForBridge $TorchRoot -ApiPublished $apiPublished
        exit 0
    }

    $restart = Stop-TorchGracefully
    if ($restart -eq 'NotRunning') {
        # Only start Torch when there was genuinely nothing running. Starting one after a
        # successful bridge restart would leave two instances fighting over the same world.
        Start-TorchVisible | Out-Null
    } elseif ($restart -eq 'Unknown') {
        Write-Host "Skipping Torch start: its state is unknown, and a second instance is worse than none." -ForegroundColor Yellow
    }
}
