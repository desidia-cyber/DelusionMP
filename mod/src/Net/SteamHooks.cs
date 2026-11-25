using System;
using System.Diagnostics;
using System.IO;
using DelusionMP.Core;
using DelusionMP.Game;
using HarmonyLib;
using UnityEngine;

namespace DelusionMP.Net
{
    /// <summary>
    /// Integrates DelusionMP's Steam transport with Dread Delusion's built-in Facepunch.Steamworks:
    ///   1. Rewrites SteamClient.Init(1574240 -> 480) when Spacewar mode is configured, and
    ///      suppresses SteamClient.RestartAppIfNecessary so Steam doesn't bounce back to 1574240.
    ///   2. Taps Dispatch.ProcessCallback / Dispatch.ProcessResult on the main thread so
    ///      SteamTransport never races with Facepunch over ManualDispatch on the live HSteamPipe.
    ///   3. Supports a one-click in-place game restart when switching between 1574240 and 480
    ///      after Steam has already initialized in the current process.
    /// </summary>
    public static class SteamHooks
    {
        internal static long LastDispatchTick;

        /// <summary>Returns the AppID Facepunch.Steamworks initialized with, or 0 if not active.</summary>
        public static uint ActiveFacepunchAppId
        {
            get
            {
                try
                {
                    if (Steamworks.SteamClient.IsValid)
                        return Steamworks.SteamClient.AppId.Value;
                }
                catch { }
                return 0;
            }
        }

        public static bool IsFacepunchActive
        {
            get
            {
                try { return Steamworks.SteamClient.IsValid; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Ensures Facepunch's callback loop is pumping while a Steam co-op session is active.
        /// Called on Unity's main thread from Plugin.OnUpdate().
        /// </summary>
        public static void EnsureCallbacksPumped()
        {
            if (!SteamTransport.NeedsMainThreadPump) return;
            try
            {
                if (!Steamworks.SteamClient.IsValid) return;
                long now = Environment.TickCount64;
                if (now - LastDispatchTick > 100)
                {
                    LastDispatchTick = now;
                    Steamworks.SteamClient.RunCallbacks();
                }
            }
            catch { }
        }

        /// <summary>
        /// Saves the desired Steam identity (game / spacewar) and relaunches Dread Delusion.exe
        /// directly with clean SteamAppId / SteamGameId environment variables so Steam initializes
        /// under the requested AppID without any external launcher .exe.
        /// </summary>
        public static bool RestartWithAppId(uint appId, out string error)
        {
            error = "";
            try
            {
                bool toSpacewar = appId == SteamTransport.AppIdSpacewar;
                CoopConfig.SteamIdentity.Value = toSpacewar ? "spacewar" : "game";
                CoopConfig.Save();

                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    error = "Could not locate Dread Delusion.exe to restart.";
                    return false;
                }

                // Preserve any existing command-line args (skipping argv[0])
                var rawArgs = Environment.GetCommandLineArgs();
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                    UseShellExecute = false,
                };
                for (int i = 1; i < rawArgs.Length; i++)
                    psi.ArgumentList.Add(rawArgs[i]);

                // Override Steam launcher environment variables so steam_api64.dll binds to appId
                psi.Environment["SteamAppId"] = appId.ToString();
                psi.Environment["SteamGameId"] = appId.ToString();
                psi.Environment.Remove("SteamOverlayGameId");

                Log.Info($"steam: restarting {exe} with SteamAppId={appId}");
                Process.Start(psi);
                Application.Quit();
                return true;
            }
            catch (Exception e)
            {
                error = "Restart failed: " + e.Message;
                Log.Error(error);
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(Steamworks.SteamClient), nameof(Steamworks.SteamClient.Init))]
    internal static class Patch_SteamClient_Init
    {
        private static void Prefix(ref uint appid)
        {
            try
            {
                bool wantSpacewar = CoopConfig.SteamIdentity != null
                    && CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase);
                if (wantSpacewar && appid != SteamTransport.AppIdSpacewar)
                {
                    Log.Info($"steam: overriding SteamClient.Init appid {appid} -> {SteamTransport.AppIdSpacewar} (Spacewar)");
                    appid = SteamTransport.AppIdSpacewar;
                    Environment.SetEnvironmentVariable("SteamAppId", SteamTransport.AppIdSpacewar.ToString());
                    Environment.SetEnvironmentVariable("SteamGameId", SteamTransport.AppIdSpacewar.ToString());
                }
            }
            catch (Exception e) { Log.Warn("SteamClient.Init prefix: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(Steamworks.SteamClient), nameof(Steamworks.SteamClient.RestartAppIfNecessary))]
    internal static class Patch_SteamClient_RestartAppIfNecessary
    {
        private static bool Prefix(uint appid, ref bool __result)
        {
            try
            {
                bool wantSpacewar = CoopConfig.SteamIdentity != null
                    && CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase);
                if (wantSpacewar)
                {
                    __result = false;
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(Steamworks.Dispatch), "ProcessCallback")]
    internal static class Patch_SteamDispatch_ProcessCallback
    {
        private static void Prefix(Steamworks.Dispatch.CallbackMsg_t msg, bool isServer)
        {
            SteamHooks.LastDispatchTick = Environment.TickCount64;
            if (!isServer)
                SteamTransport.OnFacepunchCallback((int)msg.Type, msg.Data, msg.DataSize);
        }
    }

    [HarmonyPatch(typeof(Steamworks.Dispatch), "ProcessResult")]
    internal static class Patch_SteamDispatch_ProcessResult
    {
        private static void Prefix(Steamworks.Dispatch.CallbackMsg_t msg)
        {
            SteamHooks.LastDispatchTick = Environment.TickCount64;
            SteamTransport.OnFacepunchResult(msg.Data, msg.DataSize);
        }
    }
}
