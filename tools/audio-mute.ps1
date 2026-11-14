#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Mute (or unmute) every Core Audio session belonging to an image name.

.DESCRIPTION
    Dependency-free replacement for pycaw: talks to the Windows Core Audio COM
    API directly. The rig uses this so a background game run never makes noise.

      ./tools/audio-mute.ps1 -Image 'Dread Delusion.exe'            # mute
      ./tools/audio-mute.ps1 -Image 'Dread Delusion.exe' -Unmute    # restore
      ./tools/audio-mute.ps1 -Image 'Dread Delusion.exe' -Timeout 90

    Exit 0 when at least one session was found.
#>
[CmdletBinding()]
param(
    [string]$Image = 'Dread Delusion.exe',
    [switch]$Unmute,
    [int]$Timeout = 45,
    [int]$IntervalMs = 1000
)

$ErrorActionPreference = 'Stop'

if (-not ('DmpAudio' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DmpAudio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

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
    internal interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object sessionControl);
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags,
                                 [MarshalAs(UnmanagedType.IUnknown)] out object volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification(string sessionId, IntPtr notification);
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, IntPtr eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, IntPtr eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, IntPtr eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, IntPtr eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out int processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, IntPtr eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, IntPtr eventContext);
        int GetMute(out bool mute);
    }

    // Returns how many sessions were changed. imageName matched case-insensitively.
    public static int SetMute(string imageName, bool mute)
    {
        int changed = 0;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devices;
        // eRender = 0, DEVICE_STATE_ACTIVE = 1
        if (enumerator.EnumAudioEndpoints(0, 1, out devices) != 0) return 0;
        int deviceCount;
        devices.GetCount(out deviceCount);
        for (int d = 0; d < deviceCount; d++)
        {
            IMMDevice device;
            if (devices.Item(d, out device) != 0) continue;
            Guid iid = typeof(IAudioSessionManager2).GUID;
            object iface;
            // CLSCTX_ALL = 23
            if (device.Activate(ref iid, 23, IntPtr.Zero, out iface) != 0) continue;
            var manager = (IAudioSessionManager2)iface;
            IAudioSessionEnumerator sessions;
            if (manager.GetSessionEnumerator(out sessions) != 0) continue;
            int count;
            sessions.GetCount(out count);
            for (int s = 0; s < count; s++)
            {
                IAudioSessionControl control;
                if (sessions.GetSession(s, out control) != 0) continue;
                var control2 = control as IAudioSessionControl2;
                if (control2 == null) continue;
                int pid;
                if (control2.GetProcessId(out pid) != 0 || pid == 0) continue;
                string name;
                try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName + ".exe"; }
                catch { continue; }
                if (!string.Equals(name, imageName, StringComparison.OrdinalIgnoreCase)) continue;
                var volume = control as ISimpleAudioVolume;
                if (volume == null) continue;
                if (volume.SetMute(mute, IntPtr.Zero) == 0) changed++;
            }
        }
        return changed;
    }
}
'@
}

$deadline = (Get-Date).AddSeconds($Timeout)
$muteFlag = -not $Unmute
$total = 0
while ($true) {
    $n = [DmpAudio]::SetMute($Image, $muteFlag)
    $total += $n
    if ($n -gt 0) {
        Write-Host ("[ok] {0} {1} session(s) for {2}" -f $(if ($muteFlag) {'muted'} else {'unmuted'}), $n, $Image)
        exit 0
    }
    if ((Get-Date) -ge $deadline) {
        Write-Host ("[warn] no audio session for {0} within {1}s" -f $Image, $Timeout) -ForegroundColor Yellow
        exit 1
    }
    Start-Sleep -Milliseconds $IntervalMs
}
