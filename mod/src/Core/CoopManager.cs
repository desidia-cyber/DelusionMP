using System;
using System.Collections.Generic;
using DelusionMP.Game;
using DelusionMP.Net;

namespace DelusionMP.Core
{
    /// <summary>
    /// High-level runtime session controller driven by the native in-game Co-op menu.
    /// Starts and stops LAN or Steam sessions on demand without restarting the game,
    /// manages co-op save slot isolation, and exposes live status for the UI.
    /// </summary>
    public static class CoopManager
    {
        public static bool CoopSaveActive { get; private set; }
        public static string LastError { get; private set; } = "";
        public static SteamTransport ActiveSteam { get; private set; }

        public static bool IsActive => Session.Instance != null && Session.Instance.Role != SessionRole.None;

        public static string StatusSummary
        {
            get
            {
                var s = Session.Instance;
                if (s == null || s.Role == SessionRole.None)
                    return string.IsNullOrEmpty(LastError) ? "Offline (Singleplayer)" : "Error: " + LastError;

                if (ActiveSteam != null)
                {
                    if (!string.IsNullOrEmpty(ActiveSteam.LastError))
                        return "Steam error: " + ActiveSteam.LastError;
                    if (s.Connected)
                    {
                        string who = string.IsNullOrEmpty(ActiveSteam.PartnerName) ? "partner" : ActiveSteam.PartnerName;
                        return s.Role == SessionRole.Host
                            ? $"Hosting (Steam) — Connected to {who}"
                            : $"Joined (Steam) — Connected to {who}";
                    }
                    return ActiveSteam.Status;
                }

                // LAN
                int port = Bootstrap.HasCliRole ? Bootstrap.Port : CoopConfig.LanPort.Value;
                string host = Bootstrap.HasCliRole ? Bootstrap.HostAddress : CoopConfig.LanServer.Value;
                if (s.Connected)
                    return s.Role == SessionRole.Host
                        ? $"Hosting (LAN :{port}) — Partner connected"
                        : $"Joined (LAN {host}:{port}) — Connected";

                return s.Role == SessionRole.Host
                    ? $"Hosting (LAN :{port}) — Waiting for partner..."
                    : $"Joining (LAN {host}:{port}) — Connecting...";
            }
        }

        public static bool StartHost()
        {
            StopSession();
            LastError = "";
            CoopConfig.ApplyAll(false);

            // Auto-backup co-op saves before starting a session
            try { SaveBackups.MakeBackup("session-start", "Host session started", out _); } catch { }

            try
            {
                var session = Session.Instance;
                bool useSteam = CoopConfig.Network.Value.Equals("steam", StringComparison.OrdinalIgnoreCase);
                if (useSteam)
                {
                    if (!SteamTransport.IsSteamRunning())
                    {
                        LastError = SteamTransport.SteamClosedMessage;
                        Log.Warn("coop host blocked: " + LastError);
                        UI.CoopNotifications.Show("Co-op Error: " + LastError, UI.ToastKind.Error, 7.5f);
                        return false;
                    }
                    bool sw = CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase);
                    if (SteamTransport.IsAppIdMismatched(sw, out uint activeId))
                    {
                        string curName = activeId == SteamTransport.AppIdSpacewar ? "Spacewar (480)" : $"Dread Delusion ({activeId})";
                        string wantName = sw ? "Spacewar (480)" : "Dread Delusion (1574240)";
                        LastError = $"Game is running as {curName}. Click 'Restart as {wantName}' to switch.";
                        Log.Warn("coop host blocked: " + LastError);
                        UI.CoopNotifications.Show(LastError, UI.ToastKind.Warning, 7f);
                        return false;
                    }
                    string pw = CoopConfig.SteamUsePassword.Value ? CoopConfig.SteamPassword.Value : "";
                    var t = new SteamTransport(isHost: true, useSpacewar: sw, password: pw, wantPartner: "");
                    ActiveSteam = t;
                    session.Host(t);
                }
                else
                {
                    int port = Math.Clamp(CoopConfig.LanPort.Value, 1024, 65534);
                    string bind = string.IsNullOrWhiteSpace(CoopConfig.LanBind.Value) ? "0.0.0.0" : CoopConfig.LanBind.Value.Trim();
                    var t = new UdpTransport().Configure(port, bind, port + 1, learnPeer: true);
                    session.Host(t);
                }

                Bootstrap.Offline = false;
                Bootstrap.Role = SessionRole.Host;
                SetCoopSaveMode(CoopConfig.SeparateCoopSaves.Value);
                Log.Info($"coop: hosting ({(useSteam ? "steam" : "lan")})");
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                ActiveSteam = null;
                Log.Error("coop host failed: " + e.Message);
                return false;
            }
        }

        public static bool StartJoin()
        {
            StopSession();
            LastError = "";
            CoopConfig.ApplyAll(false);

            try { SaveBackups.MakeBackup("session-start", "Join session started", out _); } catch { }

            try
            {
                var session = Session.Instance;
                bool useSteam = CoopConfig.Network.Value.Equals("steam", StringComparison.OrdinalIgnoreCase);
                if (useSteam)
                {
                    if (!SteamTransport.IsSteamRunning())
                    {
                        LastError = SteamTransport.SteamClosedMessage;
                        Log.Warn("coop join blocked: " + LastError);
                        UI.CoopNotifications.Show("Co-op Error: " + LastError, UI.ToastKind.Error, 7.5f);
                        return false;
                    }
                    bool sw = CoopConfig.SteamIdentity.Value.Equals("spacewar", StringComparison.OrdinalIgnoreCase);
                    if (SteamTransport.IsAppIdMismatched(sw, out uint activeId))
                    {
                        string curName = activeId == SteamTransport.AppIdSpacewar ? "Spacewar (480)" : $"Dread Delusion ({activeId})";
                        string wantName = sw ? "Spacewar (480)" : "Dread Delusion (1574240)";
                        LastError = $"Game is running as {curName}. Click 'Restart as {wantName}' to switch.";
                        Log.Warn("coop join blocked: " + LastError);
                        UI.CoopNotifications.Show(LastError, UI.ToastKind.Warning, 7f);
                        return false;
                    }
                    string pw = CoopConfig.SteamUsePassword.Value ? CoopConfig.SteamPassword.Value : "";
                    string partner = CoopConfig.SteamUsePassword.Value ? "" : CoopConfig.SteamPartner.Value;
                    var t = new SteamTransport(isHost: false, useSpacewar: sw, password: pw, wantPartner: partner);
                    ActiveSteam = t;
                    session.Join(t);
                }
                else
                {
                    int port = Math.Clamp(CoopConfig.LanPort.Value, 1024, 65534);
                    string host = string.IsNullOrWhiteSpace(CoopConfig.LanServer.Value) ? "127.0.0.1" : CoopConfig.LanServer.Value.Trim();
                    // Bind ephemeral local port (0) so client never collides with a local host port
                    var t = new UdpTransport().Configure(0, host, port, learnPeer: false);
                    session.Join(t);
                }

                Bootstrap.Offline = false;
                Bootstrap.Role = SessionRole.Client;
                SetCoopSaveMode(CoopConfig.SeparateCoopSaves.Value);
                Log.Info($"coop: joining ({(useSteam ? "steam" : "lan")})");
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                ActiveSteam = null;
                Log.Error("coop join failed: " + e.Message);
                return false;
            }
        }

        public static void StopSession()
        {
            try { Session.Instance?.Close(); } catch { }
            ActiveSteam = null;
            LastError = "";
            if (!Bootstrap.HasCliRole)
            {
                Bootstrap.Offline = true;
                SetCoopSaveMode(false);
            }
            Log.Info("coop: session stopped");
        }

        public static void SetCoopSaveMode(bool on)
        {
            if (Bootstrap.Slot > 0) return; // CLI -ddmp-slot always wins
            if (CoopSaveActive == on) return;
            // Never switch persistentDataPath while a save is loaded in-world: doing so would
            // redirect the next autosave into the other folder and overwrite the wrong slot.
            // SyncSaveModeOnTitle() applies the desired mode once the player returns to the Title Screen.
            if (GameRefs.InWorld)
            {
                Log.Info($"coop save isolation switch to {(on ? "Dread Delusion MP1" : "Singleplayer")} deferred until Title Screen");
                return;
            }
            if (on)
            {
                try { Patch_PersistentDataPath.EnsureCoopDirectoryExists(); } catch { }
            }
            CoopSaveActive = on;
            Log.Info($"coop save isolation: {(on ? "Dread Delusion MP1" : "Singleplayer")}");
            try { global::DelusionMenu.Instance?.RefreshSaveButtons(); } catch { }
        }

        /// <summary>Applies any deferred save-folder switch once the player is back on the Title Screen.</summary>
        public static void SyncSaveModeOnTitle()
        {
            if (Bootstrap.Slot > 0 || Bootstrap.HasCliRole || GameRefs.InWorld) return;
            bool want = IsActive && CoopConfig.SeparateCoopSaves.Value;
            SetCoopSaveMode(want);
        }
    }
}
