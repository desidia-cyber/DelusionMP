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

        public override void Load()
        {
            Logger = Log;
            Game.Log.Bind(Log);
            Game.Log.Info($"DelusionMP {DelusionMP.Net.PluginInfo.Version} loading");

            BuildInfo.Compute();
            ObjectRegistry.Instance.Invalidate();
            FactSync.Init();
            QuestSync.Init();
            WorldMirror.Init();
            EnemySync.Init();
            PlayerSync.Init();

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Game.Log.Info($"harmony patches applied: {_harmony.GetPatchedMethods().Count()}");

            Bootstrap.Parse();
            Bootstrap.Start();

            DelusionMPBootstrap.Current = this;
            AddComponent<TickBehaviour>();
            Cmd.Init();
        }

        public override bool Unload()
        {
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
            try
            {
                session.Pump();
                Bootstrap.Tick();
                Cmd.Tick();

                // Both sides need the identity table: the client resolves incoming object
                // states through it. Rebuilding is cheap relative to a frame and tracks
                // region streaming, which adds and removes objects continuously.
                float now = Time.realtimeSinceStartup;
                if (now >= _nextSlowTick && (GameRefs.InWorld || session.Connected))
                {
                    _nextSlowTick = now + 1f;
                    ObjectRegistry.Instance.Rebuild(CurrentSceneName());
                }

                // The avatar is streamed whenever a player object exists - also while the
                // partner sits in a menu, a loot window or a dialogue (those leave RUNNING).
                if (session.Connected && GameRefs.Player != null)
                {
                    PlayerSync.TickSend();
                    PlayerSync.TickRender();
                }

                DialogueSync.Tick();
                WorldEventsSync.Tick();
                if (GameRefs.Player != null) AirshipSync.Tick();

                if (GameRefs.InWorld)
                {
                    QuestSync.Tick();

                    if (session.Role == SessionRole.Host)
                    {
                        FactSync.Flush();
                        EnemySync.TickHost();
                        ClockSync.TickHost();
                    }
                    else
                    {
                        EnemySync.TickClient();
                    }
                }

                // World objects are replicated whenever a session is live. They do not need
                // a spawned player: region scenes carry doors, lifts and pickups before the
                // avatar exists, and a late joiner needs those too.
                if (session.Connected)
                {
                    FactSync.Flush();
                    WorldMirror.Tick();
                }

                if (now >= _nextPing)
                {
                    _nextPing = now + 1f;
                    if (session.Connected) session.PingTick();
                }
            }
            catch (Exception e)
            {
                Game.Log.Error($"tick failed: {e}");
            }
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


