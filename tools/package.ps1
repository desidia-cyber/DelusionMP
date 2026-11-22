#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build a shareable DelusionMP co-op package (both players unzip the same folder).

.DESCRIPTION
    dist\DelusionMP-Coop\
        DelusionMP-Launcher.exe      launcher window
        DelusionMP-Coop.exe          launcher without a window (uses DelusionMP-Coop.ini)
        DelusionMP-Share-Logs.cmd    packs logs for a bug report
        README.txt
        mod\DelusionMP.dll           the mod (the same Debug build the rig tests)
        tools\bepinex-payload\       BepInEx 6 IL2CPP + pre-generated interop for this game build

    The BepInEx payload is gitignored (140 MB). It is taken from tools\bepinex-payload, or from
    the launcher worktree (..\delusionMP-launcher\tools\bepinex-payload) when absent here.
    A zip is written next to the folder.

.EXAMPLE
    ./tools/package.ps1
    ./tools/package.ps1 -NoBuild
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [string]$Configuration = 'Debug',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $Root 'dist\DelusionMP-Coop' }

function Step($t) { Write-Host "=== $t ===" -ForegroundColor Cyan }

$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not $NoBuild) {
    Step 'build mod'
    dotnet build (Join-Path $Root 'mod\DelusionMP.csproj') -c $Configuration -v:q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'mod build failed' }
    Step 'build launchers'
    if (-not (Test-Path $cargo)) { $cargo = 'cargo' }
    Push-Location $Root
    try {
        & $cargo build --release -p delusionmp-launcher -p delusionmp-launcher-gui
        if ($LASTEXITCODE -ne 0) { throw 'launcher build failed' }
    } finally { Pop-Location }
}

$dll = Join-Path $Root "mod\bin\$Configuration\DelusionMP.dll"
$exeCli = Join-Path $Root 'target\release\DelusionMP-Coop.exe'
$exeGui = Join-Path $Root 'target\release\DelusionMP-Launcher.exe'
foreach ($f in @($dll, $exeCli, $exeGui)) { if (-not (Test-Path $f)) { throw "missing $f (build first)" } }

$payload = Join-Path $Root 'tools\bepinex-payload'
if (-not (Test-Path (Join-Path $payload 'BepInEx\core'))) {
    $payload = Join-Path (Split-Path -Parent $Root) 'delusionMP-launcher\tools\bepinex-payload'
}
if (-not (Test-Path (Join-Path $payload 'BepInEx\core'))) {
    throw 'no BepInEx payload: put BepInEx 6 IL2CPP (with interop generated for this game) in tools\bepinex-payload'
}

Step "assemble $Out"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $Out 'mod') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Out 'tools') | Out-Null
Copy-Item $exeCli, $exeGui $Out
Copy-Item $dll (Join-Path $Out 'mod')
Copy-Item (Join-Path $Root 'tools\coop-package\*') $Out
# robocopy follows the junction and copies real files; exit codes below 8 are success.
robocopy $payload (Join-Path $Out 'tools\bepinex-payload') /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "payload copy failed ($LASTEXITCODE)" }

$zip = "$Out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $Out -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "  [ok] $Out" -ForegroundColor Green
Write-Host "  [ok] $zip ($mb MB)" -ForegroundColor Green
