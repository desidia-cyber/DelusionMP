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

            // How this player looks to the partner (sent to them on connect).
            var model = Config.Bind("Avatar", "Model", "fbx_UnionWomen",
                "Character model your partner sees you as. Female models: fbx_UnionWomen (dressable, " +
                "see Outfit), fbx_DarkStarWomen, fbx_PwyllWomen. See docs/screenshots/avatar_model_options.png.");
            var outfit = Config.Bind("Avatar", "Outfit", "thief",
                "Outfit for fbx_UnionWomen: a preset (thief, civilianA, civilianB, civilianC, soldier, " +
                "inquisitor, cultist) or a comma-separated list of part names.");
            Sync.RemoteAvatar.SetLocalLook(model.Value, outfit.Value);

            // Co-op death rules (one player down: respawn by the partner with a debuff until a
            // rest; both down: the game's own death and enemies return).
            Sync.WorldEventsSync.ExclusiveUniqueBuys = Config.Bind("Rules", "ExclusiveUniqueShopItems", false,
                "1 = only one player can buy a one-of-a-kind shop item (host decides); 0 = each player may buy their own copy.").Value;
            Sync.CoopDeath.Enabled = Config.Bind("Rules", "DeathDebuffs", true,
                "Dying while the partner is alive adds a stack that lowers max health/mana/stamina until you rest in a bed.").Value;
            Sync.CoopDeath.MaxStacks = Config.Bind("Rules", "MaxStacks", 5, "Most death stacks a player can carry.").Value;
            Sync.CoopDeath.PctPerStack = Config.Bind("Rules", "PercentPerStack", 10f, "Max health/mana/stamina lost per stack, in %.").Value;
            Sync.CoopDeath.RespawnDelay = Config.Bind("Rules", "RespawnDelaySeconds", 8f,
                "Seconds a player stays down when the partner is alive (the window in which a second death is a party wipe).").Value;
            Sync.CoopDeath.RespawnAtPartner = Config.Bind("Rules", "RespawnAtPartner", true,
                "Get up next to the partner instead of at the nearest respawn point (when the partner is alive).").Value;

            // Local chat and proximity voice. The launcher writes these into this config file.
            VoiceConfig.ChatEnabled = Config.Bind("Voice", "ChatEnabled", true,
                "1 = the chat key opens a text line; partner lines are still shown when off.").Value;
            VoiceConfig.ChatKey = Config.Bind("Voice", "ChatKey", "T", "Key that opens the chat line.").Value;
            VoiceConfig.VoiceChat = Config.Bind("Voice", "VoiceChat", true,
                "1 = proximity voice chat: you hear your partner and can talk; 0 = off.").Value;
            VoiceConfig.VoiceMode = Config.Bind("Voice", "VoiceMode", 0,
                "0 = hold the key to talk, 1 = always on (silence is gated out).").Value;
            VoiceConfig.VoiceKey = Config.Bind("Voice", "VoiceKey", "V", "Push-to-talk key.").Value;
            VoiceConfig.VoiceRangeM = Config.Bind("Voice", "VoiceRangeM", 80,
                "Partner's voice fades to nothing at this many metres; 0 = no proximity.").Value;
            VoiceConfig.VoiceVolumePct = Config.Bind("Voice", "VoiceVolumePct", 100,
                "How loud your partner's voice is, in %.").Value;
            VoiceConfig.VoiceMicPct = Config.Bind("Voice", "VoiceMicPct", 100,
                "How loud your microphone is sent, in %.").Value;
            VoiceConfig.VoiceAwayPct = Config.Bind("Voice", "VoiceAwayPct", 40,
                "Partner's volume while they are away, in %; ignored when VoiceRangeM is 0.").Value;

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
            Perf.Run("bootstrap", Bootstrap.Tick);
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
            if (session.Connected && havePlayer)
            {
                Perf.Run("player.send", PlayerSync.TickSend);
                Perf.Run("player.render", PlayerSync.TickRender);
                Perf.Run("voice", VoiceSync.Tick);
            }

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


