<#
.SYNOPSIS
    Asserts that every SourceDirectory in FactionsUI.xml actually exists in a checkout.

.DESCRIPTION
    FAC-095 trap 3. Pulsar compiles the directories listed in FactionsUI.xml's <SourceDirectories>.
    The deploy checkout (C:\Torch\FactionsUI-dev) is a SPARSE clone, and the directories it puts on
    disk come from .git/info/sparse-checkout. Those are two separate lists and nothing keeps them in
    step.

    Add a SourceDirectory without adding it to the sparse set and the files are simply absent, while
    git reports the checkout clean and HEAD correct. Pulsar then fails to compile with an error
    naming a type nobody deleted, so it presents as "the client plugin is broken" rather than as a
    missing file. That happened on 2026-08-25 with src/Factions.Shared and was caught by luck.

    Read-only. Exits non-zero when a listed directory is missing or holds no .cs files.

.PARAMETER Checkout
    Root of the checkout to verify. Defaults to the deploy checkout.

.EXAMPLE
    .\scripts\Check-ClientSources.ps1
    .\scripts\Check-ClientSources.ps1 -Checkout C:\Users\se-dev\source\repos\Factions
#>
[CmdletBinding()]
param(
    [string] $Checkout = 'C:\Torch\FactionsUI-dev'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Checkout)) {
    Write-Host "Checkout not found: $Checkout" -ForegroundColor Yellow
    Write-Host "Nothing to check. Pass -Checkout if it lives elsewhere." -ForegroundColor Yellow
    exit 0
}

$manifest = Join-Path $Checkout 'FactionsUI.xml'
if (-not (Test-Path $manifest)) {
    Write-Host "FactionsUI.xml not found in $Checkout." -ForegroundColor Red
    Write-Host "Either this is not a FactionsUI checkout, or the manifest itself is outside the" -ForegroundColor Red
    Write-Host "sparse set -- which is the same failure this script exists to catch." -ForegroundColor Red
    exit 1
}

[xml] $xml = Get-Content $manifest -Raw
$dirs = @($xml.PluginData.SourceDirectories.Directory)
if ($dirs.Count -eq 0) {
    Write-Host "FactionsUI.xml lists no SourceDirectories. Pulsar would compile the whole repo." -ForegroundColor Red
    exit 1
}

$missing = @()
foreach ($d in $dirs) {
    $rel = ($d -replace '/', '\')
    $full = Join-Path $Checkout $rel
    if (-not (Test-Path $full)) {
        $missing += "$d  (directory absent)"
        continue
    }
    $cs = @(Get-ChildItem $full -Filter *.cs -Recurse -ErrorAction SilentlyContinue)
    if ($cs.Count -eq 0) {
        $missing += "$d  (present but holds no .cs files)"
        continue
    }
    Write-Host ("  OK  {0,-28} {1} .cs file(s)" -f $d, $cs.Count) -ForegroundColor Green
}

if ($missing.Count -eq 0) {
    Write-Host "All $($dirs.Count) source directories present in $Checkout." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "=== CLIENT SOURCES MISSING FROM THE CHECKOUT ===================" -ForegroundColor Red
foreach ($m in $missing) { Write-Host "  $m" -ForegroundColor Red }
Write-Host ""
Write-Host "FactionsUI.xml lists these; $Checkout does not have them." -ForegroundColor Red
Write-Host "Pulsar will fail to compile with an error naming a type nobody deleted." -ForegroundColor Red
Write-Host ""
$sparse = Join-Path $Checkout '.git\info\sparse-checkout'
if (Test-Path $sparse) {
    Write-Host "This checkout is sparse. Current set ($sparse):" -ForegroundColor Yellow
    Get-Content $sparse | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "Add the missing directories, then re-apply:" -ForegroundColor Yellow
    Write-Host "    Add-Content '$sparse' '/src/<Dir>/'" -ForegroundColor Yellow
    Write-Host "    git -C '$Checkout' read-tree -mu HEAD" -ForegroundColor Yellow
} else {
    Write-Host "This checkout is not sparse, so the directories are genuinely absent from HEAD." -ForegroundColor Yellow
}
Write-Host "================================================================" -ForegroundColor Red
exit 1
