using System;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using DelusionMP.Sync;
using HarmonyLib;
using UnityEngine;

namespace DelusionMP
{
    /// <summary>
    /// DelusionMP - seamless two-player co-op for Dread Delusion.
    ///
    /// BepInEx 6 IL2CPP plugin. Everything hangs off one Unity update tick; the game has no
    /// networking of its own, so all replication is done here.
    /// </summary>
    [BepInPlugin(Guid, "DelusionMP", DelusionMP.Net.PluginInfo.Version)]
    public sealed class Plugin : BepInEx.Unity.IL2CPP.BasePlugin
    {
        public const string Guid = "delusionmp.coop";

        internal static ManualLogSource Logger;

        private Harmony _harmony;
        private float _nextPing;
        private float _nextSlowTick;
        private bool _lastInWorld;

        public override void Load()
        {
            Logger = Log;
            Game.Log.Bind(Log);
            Game.Log.Info($"DelusionMP {DelusionMP.Net.PluginInfo.Version} loading");

            CoopConfig.Bind(Config);

            BuildInfo.Compute();
            ObjectRegistry.Instance.Invalidate();
            FactSync.Init();
            QuestSync.Init();
            WorldMirror.Init();
            EnemySync.Init();
            PlayerSync.Init();
            VoiceSync.Init();

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Game.Log.Info($"harmony patches applied: {_harmony.GetPatchedMethods().Count()}");

            Bootstrap.Parse();
            Bootstrap.Start();

            DelusionMPBootstrap.Current = this;
            AddComponent<TickBehaviour>();
            AddComponent<VoiceGuiBehaviour>();
            Cmd.Init();
        }

        public override bool Unload()
        {
            VoiceSync.Shutdown();
            Session.Instance.Close();
            PlayerSync.DestroyPuppet();
            _harmony?.UnpatchSelf();
            return true;
        }

        private static string CurrentSceneName()
        {
            try
            {
                var n = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            try { return GameRefs.Scene?.currentSceneName ?? ""; }
            catch { return ""; }
        }

        internal void OnUpdate()
        {
            var session = Session.Instance;
            if (session == null) return;
            // Each subsystem is timed and isolated (Perf.Run): a fault in one no longer skips
            // every system after it for that frame.
            Perf.Run("net.pump", () => session.Pump());
            Perf.Run("steam.cb", Net.SteamHooks.EnsureCallbacksPumped);
            Perf.Run("bootstrap", Bootstrap.Tick);
            Perf.Run("ui.menu", UI.NativeCoopMenu.Tick);
            Perf.Run("cmd", Cmd.Tick);

            // Both sides need the identity table: the client resolves incoming object
            // states through it, and region streaming adds and removes objects continuously.
            float now = Time.realtimeSinceStartup;
            if (now >= _nextSlowTick && (GameRefs.InWorld || session.Connected))
            {
                _nextSlowTick = now + 1f;
                Perf.Run("registry", () => ObjectRegistry.Instance.Rebuild(CurrentSceneName()));
            }

            // The avatar is streamed whenever a player object exists - also while the
            // partner sits in a menu, a loot window or a dialogue (those leave RUNNING).
            bool havePlayer = GameRefs.Player != null;
            bool inWorld = GameRefs.InWorld;
            if (inWorld != _lastInWorld)
            {
                _lastInWorld = inWorld;
                string gs = "-";
                try { gs = GameRefs.Manager != null ? GameRefs.Manager.CurrentGameState.ToString() : "-"; } catch { }
                DelusionMP.Game.Log.Info($"inWorld={inWorld} (state={gs} active={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} player={havePlayer})");
                if (!inWorld) CoopManager.SyncSaveModeOnTitle();
            }
            if (session.Connected && havePlayer)
            {
                Perf.Run("player.send", PlayerSync.TickSend);
                Perf.Run("player.render", PlayerSync.TickRender);
            }
            Perf.Run("voice", VoiceSync.Tick);
            Perf.Run("teleport", PartnerTeleport.Tick);

            Perf.Run("mirror.pending", WorldMirror.TickPending);
            Perf.Run("join", JoinSync.Tick);
            Perf.Run("dialogue", DialogueSync.Tick);
            Perf.Run("avatar.preload", () => RemoteAvatar.PreloadTick(inWorld));
            Perf.Run("coopdeath", CoopDeath.Tick);
            Perf.Run("factory", FactorySync.Tick);
            Perf.Run("worldevents", WorldEventsSync.Tick);
            if (havePlayer) Perf.Run("airship", AirshipSync.Tick);

            if (inWorld)
            {
                Perf.Run("quests", QuestSync.Tick);
                if (session.Role == SessionRole.Host)
                {
                    Perf.Run("clock", ClockSync.TickHost);
                }
                Perf.Run("enemies", EnemySync.Tick);
            }
            Perf.Run("summons", SummonSync.Tick);

            // World objects are replicated whenever a session is live. They do not need
            // a spawned player: region scenes carry doors, lifts and pickups before the
            // avatar exists, and a late joiner needs those too.
            if (session.Connected)
            {
                Perf.Run("facts", FactSync.Flush);
                Perf.Run("mirror", WorldMirror.Tick);
            }

            if (now >= _nextPing)
            {
                _nextPing = now + 1f;
                if (session.Connected) Perf.Run("ping", session.PingTick);
            }
            Perf.EndFrame();
        }
    }

    /// <summary>The single per-frame driver. Kept in its own component so the plugin's
    /// Load/Unload stay clean and the tick is visible in the scene hierarchy.</summary>
    public sealed class TickBehaviour : MonoBehaviour
    {
        private void Update() => DelusionMPBootstrap.Current?.OnUpdate();

        // Camera overrides (shared-dialogue point of view) run after the game's own
        // PlayerLook has written the camera for this frame.
        private void LateUpdate()
        {
            try { DelusionMP.Sync.DialogueSync.LateTick(); }
            catch (System.Exception e) { DelusionMP.Game.Log.Debug("late tick: " + e.Message); }
        }
    }

    internal static class DelusionMPBootstrap
    {
        internal static Plugin Current;
    }

    /// <summary>
    /// Console/debug surface. F8 status, F9 dump. Deliberately tiny - the real QA harness
    /// drives the mod over its own loopback transport instead of the UI.
    /// </summary>
    public sealed class DebugBehaviour : MonoBehaviour
    {
        private string _status = "";

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                var (b, i) = FactSync.Capture();
                _status = $"DelusionMP  role={Session.Instance.Role}  " +
                          $"connected={Session.Instance.Connected}  " +
                          $"facts={b.Count}b/{i.Count}i  objects={ObjectRegistry.Instance.Count}  " +
                          $"fp={FactSync.Fingerprint()}";
                Plugin.Logger?.LogInfo(_status);
            }
            if (Input.GetKeyDown(KeyCode.F9))
            {
                DumpAll();
            }
        }

        private static void DumpAll()
        {
            var (bools, ints) = FactSync.Capture();
            Plugin.Logger?.LogInfo("=== bool facts ===");
            foreach (var kv in bools) Plugin.Logger?.LogInfo($"  {kv.Key} = {kv.Value}");
            Plugin.Logger?.LogInfo("=== int facts ===");
            foreach (var kv in ints) Plugin.Logger?.LogInfo($"  {kv.Key} = {kv.Value}");
        }
    }
}


