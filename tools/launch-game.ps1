#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Detached rig launcher: starts one game instance on its own private desktop.

.DESCRIPTION
    This script is intentionally started through explorer.exe (via a generated .vbs) so it
    is not a child of the tool/harness process tree. The harness kills its descendants when
    a call ends, which makes a game launched directly die within seconds. Once here, the
    process tree is owned by explorer and survives.

    It then:
      * creates a private desktop ("DelusionMPRig<N>") and launches the game there, so its
        window can never be drawn on the owner's desktop or take focus, even fullscreen;
      * starts tools/quiet-watch.ps1, also detached, to keep the game's audio muted;
      * writes <instance>\pid.txt so the rig can report/stop it.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's folder.
#>
param(
    [Parameter(Mandatory)][int]$Slot,
    [ValidateSet('host', 'client')][string]$Role = 'host',
    [int]$Port = 27077,
    [Parameter(Mandatory)][string]$GameDir,
    [string]$Root,
    # Manual checks only (typing into the chat box needs a focused window): open the game on the
    # owner's desktop in a small window instead of a private desktop.
    [switch]$Visible
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$DesktopName = "DelusionMPRig$Slot"
$ExeName = 'Dread Delusion.exe'

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class DmpLaunch
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SI { public int cb; public string reserved, desktop, title;
                public int x, y, w, h, cx, cy, fill, flags;
                public short show, r2; public IntPtr r3, input, output, error; }
    [StructLayout(LayoutKind.Sequential)]
    struct PI { public IntPtr hp, ht; public int pid, tid; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode,
        int flags, uint access, IntPtr sa);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string dir, ref SI si, out PI pi);

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint DESKTOP_ALL_ACCESS = 0x000F01FF;

    public static int Start(string exe, string args, string dir, string desktop)
    {
        IntPtr hd = IntPtr.Zero;
        if (!string.IsNullOrEmpty(desktop))
        {
            hd = CreateDesktopW(desktop, IntPtr.Zero, IntPtr.Zero, 0, DESKTOP_ALL_ACCESS, IntPtr.Zero);
            if (hd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        SI si = new SI();
        si.cb = Marshal.SizeOf(typeof(SI));
        si.desktop = string.IsNullOrEmpty(desktop) ? null : desktop;
        si.flags = 0x1;   // STARTF_USESHOWWINDOW
        si.show = 1;      // SW_SHOWNORMAL, on the invisible desktop

        PI pi;
        var cmd = new StringBuilder("\"" + exe + "\" " + args);
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.ht); CloseHandle(pi.hp);
        if (hd != IntPtr.Zero) CloseHandle(hd);
        return pi.pid;
    }

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@

$exe = Join-Path $GameDir $ExeName
if (-not (Test-Path -LiteralPath $exe)) { throw "missing executable: $exe" }

$instDir = Split-Path -Parent $GameDir
$res = if ($Visible) { '-screen-width 640 -screen-height 360' } else { '-screen-width 320 -screen-height 180' }
$argsLine = "-screen-fullscreen 0 -windowed $res " +
            "-ddmp-slot=$Slot -ddmp-role=$Role -ddmp-port=$Port -ddmp-name=P$Slot -ddmp-mute"

$procId = [DmpLaunch]::Start($exe, $argsLine, $GameDir, $(if ($Visible) { $null } else { $DesktopName }))
Set-Content -LiteralPath (Join-Path $instDir 'pid.txt') -Value $procId -Encoding ASCII
Set-Content -LiteralPath (Join-Path $instDir 'cmdline.txt') -Value $argsLine -Encoding ASCII

# Mute watchdog: detached too, so it outlives this helper.
Start-Process -FilePath 'powershell' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', (Join-Path $Root 'tools\quiet-watch.ps1'), '-Image', $ExeName, '-Seconds', '21600'
) -WindowStyle Hidden | Out-Null

if ($Visible) {
    # Side by side: instance 1 on the left, instance 2 on the right.
    for ($t = 0; $t -lt 120; $t++) {
        Start-Sleep -Milliseconds 500
        $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
            [void][DmpLaunch]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 20 + ($Slot - 1) * 680, 60, 656, 399, 0x0044)
            break
        }
    }
}
