using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP
{
    /// <summary>
    /// Session bootstrap: reads the rig/player command line, opens the transport and
    /// starts hosting or joining. Kept out of Plugin so the plugin lifecycle stays small
    /// and so this can be unit-reasoned about on its own.
    ///
    /// Command line (Unity passes unknown args straight through):
    ///   -ddmp-role=host|client   default: host
    ///   -ddmp-port=27077         UDP port the host binds; the client uses port+1
    ///   -ddmp-host=127.0.0.1     client's host address
    ///   -ddmp-name=Playername    display name
    ///   -ddmp-mute               hold AudioListener.volume at 0 (rig runs)
    ///   -ddmp-offline            load the mod but never connect (census/debug runs)
    /// </summary>
    public static class Bootstrap
    {
        public static SessionRole Role = SessionRole.Host;
        public static string PlayerName = "Player";
        public static int Port = 27077;
        public static string HostAddress = "127.0.0.1";
        public static bool Mute;
        public static bool Offline;
        /// <summary>True when -ddmp-role was explicitly supplied on the command line (rig/CLI).</summary>
        public static bool HasCliRole;
        /// <summary>-ddmp-trace: write state-codec breadcrumbs (native crash hunting only).</summary>
        public static bool TraceCodec;
        public static int Slot;

        private static float _nextJoinAttempt;
        private static bool _windowFixed;

        public static void Parse()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in args)
                {
                    if (!a.StartsWith("-ddmp-", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = a.IndexOf('=');
                    if (eq < 0) map[a.Substring(6)] = "1";
                    else map[a.Substring(6, eq - 6)] = a.Substring(eq + 1);
                }

                HasCliRole = map.TryGetValue("role", out var role);
                if (HasCliRole)
                    Role = role.Equals("client", StringComparison.OrdinalIgnoreCase)
                        ? SessionRole.Client : SessionRole.Host;
                if (map.TryGetValue("port", out var port) && int.TryParse(port, out var p) && p > 0) Port = p;
                if (map.TryGetValue("slot", out var slot) && int.TryParse(slot, out var sl)) Slot = sl;
                if (map.TryGetValue("host", out var host) && !string.IsNullOrWhiteSpace(host)) HostAddress = host;
                if (map.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name)) PlayerName = name;
                // When launched normally (without -ddmp-role), stay idle until the player
                // starts Host or Join from the native in-game Co-op menu.
                Offline = map.ContainsKey("offline") || !HasCliRole;
                TraceCodec = map.ContainsKey("trace");
                Mute = map.ContainsKey("mute");
            }
            catch (Exception e)
            {
                Log.Warn("command line parse failed: " + e.Message);
            }
            Log.Info($"bootstrap cliRole={HasCliRole} role={Role} port={Port} host={HostAddress} name={PlayerName} " +
                     $"slot={Slot} mute={Mute} offline={Offline}");
        }

        public static void Start()
        {
            try
            {
                // The rig keeps both copies alive while unfocused. Without this the game
                // pauses when the window is not active, and a paused host stops replicating.
                Application.runInBackground = true;
            }
            catch (Exception e) { Log.Debug("runInBackground: " + e.Message); }

            if (Mute)
            {
                try { AudioListener.volume = 0f; }
                catch (Exception e) { Log.Debug("mute: " + e.Message); }
            }

            // A rig run must not cover the screen. The game's PlayerPrefs ask for borderless
            // fullscreen, which overrides the Unity command line, so the external watchdog
            // (tools/quiet-watch.ps1) minimises the window without activating it on the
            // first frame. Do not call Screen.SetResolution here: this runs during the
            // chainloader, before the player's first frame, and doing so can kill the
            // process.

            if (Offline)
            {
                Log.Info("offline mode - no transport");
                return;
            }

            try
            {
                var session = Session.Instance;
                if (Role == SessionRole.Host)
                {
                    var t = new UdpTransport().Configure(Port, HostAddress, Port + 1, learnPeer: true);
                    session.Host(t);
                }
                else
                {
                    var t = new UdpTransport().Configure(Port + 1, HostAddress, Port);
                    session.Join(t);
                }
            }
            catch (Exception e)
            {
                Log.Error("transport start failed: " + e);
            }
        }

        /// <summary>Called every tick: clients retry the handshake until the host answers.</summary>
        public static void Tick()
        {
            if (Mute)
            {
                try { AudioListener.volume = 0f; } catch { }
            }

            // Force a small windowed surface from the game's own update loop. Doing this
            // here (rather than during Load) is safe: Unity has finished initialising, and
            // the watchdog then parks the small window off-screen. The game's PlayerPrefs
            // ask for borderless fullscreen and override the command line.
            if (Mute && !_windowFixed && Time.realtimeSinceStartup > 3f)
            {
                _windowFixed = true;
                try { Screen.SetResolution(320, 180, FullScreenMode.Windowed); }
                catch (Exception e) { Log.Debug("windowed force: " + e.Message); }
            }

            if (Offline) return;
            var session = Session.Instance;
            if (session.Role != SessionRole.Client || session.Connected) return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextJoinAttempt) return;
            _nextJoinAttempt = now + 1f;

            session.SendHello();
        }
    }
}
