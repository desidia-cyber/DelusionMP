#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Two-instance Dread Delusion rig for DelusionMP - background, unfocused, silent.

.DESCRIPTION
    Creates N isolated game instances and launches them so the owner can keep using the
    machine. A rig run must be invisible and inaudible:

      * each instance runs on its own private Windows desktop
        ("DelusionMPRig<N>"), so its window can never be drawn on the owner's desktop and
        cannot take focus - even if the game asks for fullscreen;
      * the launch is routed through explorer.exe (a generated .vbs), because anything
        started directly from the harness is killed when the tool call ends;
      * tools/quiet-watch.ps1 mutes every game audio session (and re-mutes if the game
        recreates it), so a run is silent;
      * each instance gets its own save directory via the mod's persistentDataPath patch
        (-ddmp-slot=N), so two careers never share a save index.

.PARAMETER Action
    setup    copy the game into rig/instance1..N and install BepInEx
    clone    clone instance1 -> others (after interop generation)
    launch   start all instances (instance1 = host, others = clients)
    stop     stop instances and the mute watchdog
    status   print instance state and the last BepInEx log lines
#>
[CmdletBinding()]
param(
    [ValidateSet('setup', 'clone', 'launch', 'stop', 'status')]
    [string]$Action = 'status',

    [int]$Count = 2,
    [string]$GameSrc = 'G:\SteamLibrary\steamapps\common\Dread Delusion\windows_content',
    [int]$Port = 27077,
    # launch only: small windows on this desktop instead of private desktops (manual checks)
    [switch]$Visible
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$RigDir = Join-Path $Root 'rig'
$ExeName = 'Dread Delusion.exe'

function Write-Head($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t) { Write-Host "  [ok]   $t" -ForegroundColor Green }
function Write-Info($t) { Write-Host "  [info] $t" -ForegroundColor Gray }
function Write-Warn2($t) { Write-Host "  [warn] $t" -ForegroundColor Yellow }

function Get-InstDir([int]$i) { Join-Path $RigDir "instance$i" }
function Get-GameDir([int]$i) { Join-Path (Get-InstDir $i) 'game' }
function Get-InstanceExe([int]$i) { Join-Path (Get-GameDir $i) $ExeName }

function Get-InstancePid([int]$i) {
    $f = Join-Path (Get-InstDir $i) 'pid.txt'
    if (-not (Test-Path $f)) { return 0 }
    $v = (Get-Content -LiteralPath $f -Raw).Trim()
    if ($v -match '^\d+$') { return [int]$v }
    return 0
}

# ---------------------------------------------------------------- quiet launch
function Set-BepInExQuiet([string]$game) {
    $cfg = Join-Path $game 'BepInEx\config\BepInEx.cfg'
    if (-not (Test-Path $cfg)) { return }
    $text = Get-Content -LiteralPath $cfg -Raw
    if ($text -notmatch '(?m)^\[Logging\.Console\]') { return }
    # The console window steals foreground the moment it is created, which is exactly what
    # a background rig run must never do. Logs still go to LogOutput.log.
    $text = [regex]::Replace($text, '(?m)^(\[Logging\.Console\][\s\S]*?Enabled\s*=\s*)true', '${1}false')
    Set-Content -LiteralPath $cfg -Value $text -Encoding UTF8
}

function Write-LaunchVbs([int]$i, [string]$role) {
    $vbs = Join-Path (Get-InstDir $i) 'launch.vbs'
    $game = Get-GameDir $i
    $line = 'CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -ExecutionPolicy Bypass ' +
            '-File ""' + (Join-Path $Root 'tools\launch-game.ps1') + '""' +
            ' -Slot ' + $i + ' -Role ' + $role + ' -Port ' + $Port +
            ' -GameDir ""' + $game + '"" -Root ""' + $Root + '""' +
            $(if ($Visible) { ' -Visible' } else { '' }) + '", 0, False'
    Set-Content -LiteralPath $vbs -Value $line -Encoding ASCII
    return $vbs
}

# ---------------------------------------------------------------- setup
function Invoke-Setup {
    Write-Head 'Create instances'
    if (-not (Test-Path -LiteralPath (Join-Path $GameSrc $ExeName))) { throw "game not found: $GameSrc" }
    for ($i = 1; $i -le $Count; $i++) {
        $game = Get-GameDir $i
        if (Test-Path -LiteralPath (Join-Path $game $ExeName)) {
            Write-Info "instance$i already exists"
        } else {
            New-Item -ItemType Directory -Force -Path $game | Out-Null
            Write-Info "instance${i}: copying game (1.7 GB)..."
            robocopy $GameSrc $game /E /NFL /NDL /NJH /NJS /MT:16 | Out-Null
            Write-Ok "instance${i}: copied"
        }

        # A Steam build launched directly needs its app id or the Steam stub exits
        # before Unity starts. 1574240 == Dread Delusion.
        Set-Content -LiteralPath (Join-Path $game 'steam_appid.txt') -Value '1574240' -Encoding ASCII

        if (-not (Test-Path -LiteralPath (Join-Path $game 'BepInEx'))) {
            Write-Info "instance${i}: installing BepInEx..."
            python -X utf8 (Join-Path $Root 'tools\fetch_bepinex.py') --target $game | Select-Object -Last 2 | ForEach-Object { Write-Ok $_ }
            python -X utf8 (Join-Path $Root 'tools\fetch_bepinex.py') --target $game --unity-version 2022.3.60 | Select-Object -Last 2 | ForEach-Object { Write-Ok $_ }
        }
        $plugins = Join-Path $game 'BepInEx\plugins'
        New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    }
}

# ---------------------------------------------------------------- clone
function Invoke-Clone {
    Write-Head 'Clone instance1 -> others (after interop generation)'
    $src = Get-GameDir 1
    if (-not (Test-Path -LiteralPath (Join-Path $src 'BepInEx\interop'))) {
        Write-Warn2 'no BepInEx/interop in instance1 yet - launch it once first'
    }
    for ($i = 2; $i -le $Count; $i++) {
        $game = Get-GameDir $i
        if (Test-Path -LiteralPath (Join-Path $game $ExeName)) {
            Write-Info "instance$i exists, refreshing BepInEx from instance1"
            robocopy (Join-Path $src 'BepInEx') (Join-Path $game 'BepInEx') /E /NFL /NDL /NJH /NJS /MT:8 | Out-Null
        } else {
            New-Item -ItemType Directory -Force -Path $game | Out-Null
            Write-Info "instance${i}: copying instance1..."
            robocopy $src $game /E /NFL /NDL /NJH /NJS /MT:16 | Out-Null
        }
        Set-Content -LiteralPath (Join-Path $game 'steam_appid.txt') -Value '1574240' -Encoding ASCII
        Write-Ok "instance${i}: ready"
    }
}

# ---------------------------------------------------------------- launch
function Invoke-Launch {
    Write-Head 'Launch instances'
    for ($i = 1; $i -le $Count; $i++) {
        $game = Get-GameDir $i
        $exe = Get-InstanceExe $i
        if (-not (Test-Path -LiteralPath $exe)) { Write-Warn2 "instance$i has no game - run -Action setup"; continue }

        $procId = Get-InstancePid $i
        if ($procId -gt 0 -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
            Write-Info "instance$i already running (PID $procId)"; continue
        }

        $role = if ($i -eq 1) { 'host' } else { 'client' }
        Set-BepInExQuiet $game
        Remove-Item (Join-Path $game 'BepInEx\ddmp_cmd.txt') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $game 'BepInEx\ddmp_out.txt') -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path (Get-InstDir $i) 'pid.txt') -Force -ErrorAction SilentlyContinue

        $vbs = Write-LaunchVbs $i $role
        # explorer.exe owns the process tree from here, so the game survives this call.
        Start-Process explorer.exe -ArgumentList "`"$vbs`"" | Out-Null

        $wait = 0
        while ($wait -lt 40) {
            Start-Sleep -Milliseconds 500
            if (Test-Path (Join-Path (Get-InstDir $i) 'pid.txt')) { break }
            $wait++
        }
        $procId = Get-InstancePid $i
        if ($procId -gt 0) { Write-Ok "instance$i ($role): PID $procId (desktop DelusionMPRig$i)" }
        else { Write-Warn2 "instance${i}: launcher produced no pid" }
    }
}

# ---------------------------------------------------------------- stop
function Invoke-Stop {
    Write-Head 'Stop instances'
    for ($i = 1; $i -le $Count; $i++) {
        $procId = Get-InstancePid $i
        if ($procId -gt 0) {
            if (Get-Process -Id $procId -ErrorAction SilentlyContinue) {
                Write-Info "killing instance$i PID $procId"
                Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
            }
            Remove-Item (Join-Path (Get-InstDir $i) 'pid.txt') -Force -ErrorAction SilentlyContinue
        }
    }
    $watch = Join-Path $RigDir 'quiet-watch.pid'
    if (Test-Path $watch) {
        $id = (Get-Content $watch -Raw).Trim()
        if ($id) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
        Remove-Item $watch -Force -ErrorAction SilentlyContinue
    }
    # Belt and braces: anything still matching the image name.
    Get-Process 'Dread Delusion' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Info "killing stray PID $($_.Id)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- status
function Invoke-Status {
    Write-Head 'Rig status'
    for ($i = 1; $i -le $Count; $i++) {
        $game = Get-GameDir $i
        $exe = Get-InstanceExe $i
        if (-not (Test-Path -LiteralPath $exe)) { Write-Warn2 "instance${i}: missing"; continue }
        $procId = Get-InstancePid $i
        $alive = $procId -gt 0 -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)
        $interop = if (Test-Path (Join-Path $game 'BepInEx\interop\Assembly-CSharp.dll')) { 'interop=yes' } else { 'interop=no' }
        $log = Join-Path $game 'BepInEx\LogOutput.log'
        $logAge = if (Test-Path $log) { (Get-Item $log).LastWriteTime.ToString('HH:mm:ss') } else { '-' }
        Write-Info ("instance{0}: pid={1} alive={2} {3} log={4}" -f $i, $procId, [bool]$alive, $interop, $logAge)
        if (-not $alive -and (Test-Path $log)) {
            Write-Host '    --- last log lines ---' -ForegroundColor DarkGray
            Get-Content -LiteralPath $log -Tail 10 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        }
    }
}

switch ($Action) {
    'setup'  { Invoke-Setup }
    'clone'  { Invoke-Clone }
    'launch' { Invoke-Launch }
    'stop'   { Invoke-Stop }
    'status' { Invoke-Status }
}


