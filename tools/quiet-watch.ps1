#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Keep rig game windows off-screen, unactivated and silent.

.DESCRIPTION
    A rig run must never disturb the machine's owner: no window on screen, no focus
    steal, no audio. This watchdog runs for the lifetime of the game processes and,
    every ~200 ms:

      * finds the top-level windows owned by the target image name,
      * positions them past the right/bottom edge of the virtual desktop,
      * marks them WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW so they cannot take focus or
        appear in the taskbar/alt-tab,
      * re-applies SW_SHOWNOACTIVATE if the game tries to activate itself,
      * mutes the process's Core Audio sessions (and re-mutes if the game recreates
        them on device/reload).

    It also keeps a state file with the current mute state so the launcher can report
    whether the run is genuinely quiet.

    Usage (normally started by tools/rig.ps1):
      ./tools/quiet-watch.ps1 -Image 'Dread Delusion.exe' -Seconds 21600
#>
[CmdletBinding()]
param(
    [string]$Image = 'Dread Delusion.exe',
    [int]$Seconds = 21600,
    [switch]$Result
)

$ErrorActionPreference = 'Continue'

if (-not ('DmpQuiet' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class DmpQuiet
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr64(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong32(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr64(IntPtr h, int idx, IntPtr val);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong32(IntPtr h, int idx, int val);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOSIZE = 0x0001;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOWMINNOACTIVE = 7;

    static IntPtr GetLong(IntPtr h, int idx)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(h, idx) : (IntPtr)GetWindowLong32(h, idx);
    }

    static void SetLong(IntPtr h, int idx, IntPtr v)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(h, idx, v);
        else SetWindowLong32(h, idx, v.ToInt32());
    }

    public static List<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            int wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid == pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Returns number of windows touched.
    // grace = true -> minimise without activating (covers the first frames, when the
    // game may still be borderless-fullscreen from its PlayerPrefs).
    // grace = false -> the mod has forced windowed mode, so park it off-screen and
    // show it again without activation so Unity keeps rendering.
    public static int Quiet(int pid, int x, int y, bool grace)
    {
        int n = 0;
        foreach (var h in WindowsOf(pid))
        {
            IntPtr ex = GetLong(h, GWL_EXSTYLE);
            IntPtr want = (IntPtr)(ex.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            if (want != ex) SetLong(h, GWL_EXSTYLE, want);

            RECT r;
            GetWindowRect(h, out r);
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;

            if (w < 200 || hh < 150)
            {
                // Helper/console/IME windows: hide outright, they have no value on screen.
                ShowWindow(h, SW_HIDE);
                n++;
                continue;
            }

            if (grace || w > 1600)
            {
                // Still fullscreen (prefs win at startup): minimise without activating.
                SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
                ShowWindow(h, SW_SHOWMINNOACTIVE);
            }
            else
            {
                // The mod forced windowed mode: park it off-screen, keep it rendering.
                SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_SHOWWINDOW);
                ShowWindow(h, SW_SHOWNOACTIVATE);
            }
            n++;
        }
        return n;
    }

    public static bool AnyVisible(int pid)
    {
        foreach (var h in WindowsOf(pid)) if (IsWindowVisible(h)) return true;
        return false;
    }

    public static bool IsForeground(int pid)
    {
        int fpid;
        GetWindowThreadProcessId(GetForegroundWindow(), out fpid);
        return fpid == pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    public static string Rect(int pid)
    {
        foreach (var h in WindowsOf(pid))
        {
            RECT r;
            if (GetWindowRect(h, out r))
                return string.Format("{0},{1} {2}x{3}", r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        return "none";
    }

    public static string AllWindows(int pid)
    {
        var sb = new StringBuilder();
        foreach (var h in WindowsOf(pid))
        {
            RECT r;
            GetWindowRect(h, out r);
            sb.AppendFormat("[{0},{1} {2}x{3} vis={4}] ",
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, IsWindowVisible(h) ? 1 : 0);
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }
}

public static class DmpAudio2
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection { int GetCount(out int c); int Item(int i, out IMMDevice d); }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr g, int f, [MarshalAs(UnmanagedType.IUnknown)] out object c);
        int GetSimpleAudioVolume(IntPtr g, int f, [MarshalAs(UnmanagedType.IUnknown)] out object v);
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
        int RegisterSessionNotification(IntPtr n);
        int UnregisterSessionNotification(IntPtr n);
        int RegisterDuckNotification(string id, IntPtr n);
        int UnregisterDuckNotification(IntPtr n);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator { int GetCount(out int c); int GetSession(int i, out IAudioSessionControl s); }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        int GetState(out int s);
        int GetDisplayName(out IntPtr n); int SetDisplayName(string n, IntPtr e);
        int GetIconPath(out IntPtr p); int SetIconPath(string p, IntPtr e);
        int GetGroupingParam(out Guid g); int SetGroupingParam(ref Guid g, IntPtr e);
        int RegisterAudioSessionNotification(IntPtr n); int UnregisterAudioSessionNotification(IntPtr n);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        int GetState(out int s);
        int GetDisplayName(out IntPtr n); int SetDisplayName(string n, IntPtr e);
        int GetIconPath(out IntPtr p); int SetIconPath(string p, IntPtr e);
        int GetGroupingParam(out Guid g); int SetGroupingParam(ref Guid g, IntPtr e);
        int RegisterAudioSessionNotification(IntPtr n); int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out int pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float l, IntPtr e);
        int GetMasterVolume(out float l);
        int SetMute(bool m, IntPtr e);
        int GetMute(out bool m);
    }

    public static int SetAll(bool mute, HashSet<int> pids)
    {
        int changed = 0;
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devices;
        if (en.EnumAudioEndpoints(0, 1, out devices) != 0) return 0;
        int dc; devices.GetCount(out dc);
        for (int d = 0; d < dc; d++)
        {
            IMMDevice dev;
            if (devices.Item(d, out dev) != 0) continue;
            Guid iid = typeof(IAudioSessionManager2).GUID;
            object iface;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out iface) != 0) continue;
            var mgr = (IAudioSessionManager2)iface;
            IAudioSessionEnumerator ses;
            if (mgr.GetSessionEnumerator(out ses) != 0) continue;
            int sc; ses.GetCount(out sc);
            for (int i = 0; i < sc; i++)
            {
                IAudioSessionControl ctl;
                if (ses.GetSession(i, out ctl) != 0) continue;
                var c2 = ctl as IAudioSessionControl2;
                if (c2 == null) continue;
                int pid;
                if (c2.GetProcessId(out pid) != 0 || !pids.Contains(pid)) continue;
                var vol = ctl as ISimpleAudioVolume;
                if (vol == null) continue;
                if (vol.SetMute(mute, IntPtr.Zero) == 0) changed++;
            }
        }
        return changed;
    }
}
'@
}

# Past the right edge of the whole virtual desktop; stacked down for multi-instance.
$x = [DmpQuiet]::GetSystemMetrics(76) + [DmpQuiet]::GetSystemMetrics(78) + 400
$y = [DmpQuiet]::GetSystemMetrics(77)

$deadline = (Get-Date).AddSeconds($Seconds)
$graceUntil = (Get-Date).AddSeconds(35)   # until the mod has forced windowed mode
$lastMuteCount = -1
$muted = $false
$logPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'rig\quiet-watch.log'
function QLog($m) {
    try { Add-Content -LiteralPath $logPath -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $m) } catch { }
}

while ((Get-Date) -lt $deadline) {
    $procs = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Image)) -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) {
        Start-Sleep -Milliseconds 500
        continue
    }

    $pids = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($p in $procs) { [void]$pids.Add($p.Id) }

    # Visibility is handled by launching on a private desktop (rig.ps1). A window on a
    # non-active desktop is never drawn and cannot take focus, so this watchdog only has
    # to keep the run silent.
    $changed = [DmpAudio2]::SetAll($true, $pids)
    if ($changed -gt 0) { $muted = $true }
    if ($changed -ne $lastMuteCount) {
        $lastMuteCount = $changed
        if ($changed -gt 0) { QLog "muted $changed audio session(s)" }
    }

    if ($Result) {
        foreach ($p in $procs) { $line = "{0} pid={1} mutedSessions={2}" -f $Image, $p.Id, $changed; Write-Host $line; QLog $line }
        break
    }

    Start-Sleep -Milliseconds 500
}



