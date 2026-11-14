#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Capture a window that lives on the rig's private desktop.

.DESCRIPTION
    The rig launches games on a non-active desktop ("DelusionMPRig"), so their windows are
    invisible to the owner and cannot be enumerated from the interactive desktop. This opens
    that desktop read-only, finds the target process's window, and PrintWindow()s it into a
    PNG. Nothing is shown on the owner's screen.

    Usage:
      ./tools/shot-desktop.ps1 -ProcessId 1234 -OutPath rig\shot.png
      ./tools/shot-desktop.ps1 -ProcessId 1234 -Desktop DelusionMPRig -List
#>
[CmdletBinding()]
param(
    [int]$ProcessId,
    [string]$Desktop = 'DelusionMPRig',
    [string]$OutPath = 'rig\shot.png',
    [switch]$List
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('DmpShot' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DmpShot
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenDesktopW(string name, int flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDesktopWindows(IntPtr desktop, EnumProc cb, IntPtr lParam);

    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    public struct RECT { public int L, T, R, B; }
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const uint DESKTOP_ENUMERATE = 0x0040;

    public static List<IntPtr> WindowsOf(int pid, string desktopName)
    {
        var list = new List<IntPtr>();
        IntPtr d = OpenDesktopW(desktopName, 0, false, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
        if (d == IntPtr.Zero) return list;
        EnumDesktopWindows(d, (h, l) =>
        {
            int p;
            GetWindowThreadProcessId(h, out p);
            if (p == pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Describe(int pid, string desktopName)
    {
        var sb = new StringBuilder();
        foreach (var h in WindowsOf(pid, desktopName))
        {
            RECT r; GetWindowRect(h, out r);
            var t = new StringBuilder(200); GetWindowText(h, t, 200);
            var c = new StringBuilder(200); GetClassName(h, c, 200);
            sb.AppendFormat("hwnd={0} rect={1},{2} {3}x{4} class={5} title={6}\n",
                h, r.L, r.T, r.R - r.L, r.B - r.T, c, t);
        }
        return sb.Length == 0 ? "no windows on " + desktopName : sb.ToString();
    }
}
'@
}

if (-not $ProcessId) {
    $p = Get-Process 'Dread Delusion' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw "no Dread Delusion process; pass -ProcessId" }
    $ProcessId = $p.Id
}

$wins = [DmpShot]::WindowsOf($ProcessId, $Desktop)
if ($List -or $wins.Count -eq 0) {
    [DmpShot]::Describe($ProcessId, $Desktop)
    if ($wins.Count -eq 0) { exit 1 }
    exit 0
}

# Pick the largest window (the game view).
$best = $null; $area = -1
foreach ($h in $wins) {
    $r = New-Object DmpShot+RECT
    [void][DmpShot]::GetWindowRect($h, [ref]$r)
    $a = ($r.R - $r.L) * ($r.B - $r.T)
    if ($a -gt $area) { $area = $a; $best = $h }
}

$rect = New-Object DmpShot+RECT
[void][DmpShot]::GetWindowRect($best, [ref]$rect)
$w = [Math]::Max(1, $rect.R - $rect.L)
$hh = [Math]::Max(1, $rect.B - $rect.T)

$bmp = New-Object System.Drawing.Bitmap($w, $hh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [DmpShot]::PrintWindow($best, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()

$dir = Split-Path -Parent $OutPath
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save((Join-Path (Get-Location) $OutPath), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

"$OutPath ${w}x${hh} printwindow=$ok"
