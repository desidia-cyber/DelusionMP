#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Bring up a real two-player DelusionMP session from the saved game and report state.

.DESCRIPTION
    Drives the invisible rig end-to-end through the mod's control channel:
      launch (private desktops, muted) -> skip intro -> Start Game -> slot 1 ->
      Accept journal note -> host resync -> compare fact maps and world-state hashes.

    The title screen is advanced through the game's own UI buttons (the same onClick the
    player's click runs), so no synthetic input and no visible window is involved.

.EXAMPLE
    ./tools/session.ps1
    ./tools/session.ps1 -NoRelaunch
#>
[CmdletBinding()]
param(
    [switch]$NoRelaunch,
    [int]$Count = 2,
    [int]$WaitSeconds = 32
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

function Send-Cmd([int]$i, [string]$cmd, [int]$ms = 1800) {
    $game = Join-Path $Root "rig\instance$i\game"
    $p = Join-Path $game 'BepInEx\ddmp_cmd.txt'
    $o = Join-Path $game 'BepInEx\ddmp_out.txt'
    $before = (Get-Content $o -ErrorAction SilentlyContinue | Measure-Object).Count
    Set-Content $p $cmd -Encoding UTF8
    Start-Sleep -Milliseconds $ms
    Get-Content $o -ErrorAction SilentlyContinue | Select-Object -Skip $before
}

function Invoke-InstanceMenu([int]$i) {
    [void](Send-Cmd $i 'endintro')
    Start-Sleep -Seconds 2
    [void](Send-Cmd $i 'click Start_Btn')
    Start-Sleep -Seconds 3
    [void](Send-Cmd $i 'click QuickStart_1')
    Start-Sleep -Seconds 10
    [void](Send-Cmd $i 'click Accept')
    Start-Sleep -Seconds 5
}

if (-not $NoRelaunch) {
    & (Join-Path $PSScriptRoot 'rig.ps1') -Action stop | Out-Null
    # The plugin DLL stays locked until the killed process has fully exited.
    for ($t = 0; $t -lt 30 -and (Get-Process 'Dread Delusion' -ErrorAction SilentlyContinue); $t++) {
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 1
    & (Join-Path $PSScriptRoot 'deploy.ps1') -NoBuild | Out-Null
    & (Join-Path $PSScriptRoot 'rig.ps1') -Action launch -Count $Count | Out-Null
    Start-Sleep -Seconds $WaitSeconds
    # An instance occasionally dies during start-up when both boot at once; the launcher
    # skips live instances, so a second pass only restarts the missing one.
    & (Join-Path $PSScriptRoot 'rig.ps1') -Action launch -Count $Count | Out-Null
    Start-Sleep -Seconds 5
}

Write-Host "`n=== load save on both instances ===" -ForegroundColor Cyan
for ($i = 1; $i -le $Count; $i++) {
    Write-Host "  instance$i"
    Invoke-InstanceMenu $i
}

Write-Host "`n=== status ===" -ForegroundColor Cyan
for ($i = 1; $i -le $Count; $i++) { Send-Cmd $i 'status' }

Write-Host "`n=== resync from host ===" -ForegroundColor Cyan
[void](Send-Cmd 1 'resync')
Start-Sleep -Seconds 6

Write-Host "`n=== fact fingerprints ===" -ForegroundColor Cyan
for ($i = 1; $i -le $Count; $i++) { Send-Cmd $i 'facts' | Select-Object -First 1 }

Write-Host "`n=== world state hashes ===" -ForegroundColor Cyan
for ($i = 1; $i -le $Count; $i++) { Send-Cmd $i 'statehash' }

Write-Host "`n=== door sample (host) ===" -ForegroundColor Cyan
Send-Cmd 1 'doors' | Select-Object -First 4
