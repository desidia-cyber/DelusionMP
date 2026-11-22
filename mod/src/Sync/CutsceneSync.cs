using System;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Playables;

namespace DelusionMP.Sync
{
    /// <summary>
    /// World cutscenes play for both players. Every timeline cutscene in the game goes
    /// through CinematicsManager.PlayScene(director) and then
    /// DelusionCinematics.WorldCinematicStarted(fullspeed, canSkip, blocksRegionLoading);
    /// the originator streams that start, and the partner plays the same director through
    /// DelusionCinematics.WorldCutscene, which runs the game's own camera, state and
    /// end-of-scene handling. Skips are mirrored. A peer that already plays that director
    /// (both walked into the same trigger) ignores the duplicate.
    ///
    /// Dialogue art cards (StartCinematic/ContinueCinematic) are not timelines; they reach
    /// the partner through DialogueSync.
    /// </summary>
    public static class CutsceneSync
    {
        private const byte MsgPlay = 0, MsgSkip = 1, MsgCredits = 2;
        private static int _sent, _played, _skipped, _missing;
        public static string Stats => $"sent={_sent} played={_played} skipped={_skipped} missing={_missing}";

        private static readonly string[] Named = { "fortressScene", "cutterLiftoff", "angelAwaken", "velaKillPlayer" };

        internal static global::CinematicsManager Manager
        {
            get
            {
                try
                {
                    var m = global::DelusionCinematics.Instance?.worldCinemaManager;
                    if (m != null) return m;
                    // Assigned lazily by the game (CheckCinemaManager); find it directly.
                    foreach (var c in Resources.FindObjectsOfTypeAll<global::CinematicsManager>())
                        if (c != null && c.gameObject.scene.IsValid()) return c;
                }
                catch { }
                return null;
            }
        }

        private static PlayableDirector Named_(global::CinematicsManager m, string field)
        {
            switch (field)
            {
                case "fortressScene": return m.fortressScene;
                case "cutterLiftoff": return m.cutterLiftoff;
                case "angelAwaken": return m.angelAwaken;
                case "velaKillPlayer": return m.velaKillPlayer;
            }
            return null;
        }

        private static string KeyOf(PlayableDirector d)
        {
            var m = Manager;
            if (m != null)
                foreach (var f in Named)
                {
                    var nd = Named_(m, f);
                    if (nd != null && nd.Pointer == d.Pointer) return "field:" + f;
                }
            return EnemySync.KeyFor(ObjectRegistry.Instance, d);
        }

        private static PlayableDirector Resolve(string key)
        {
            if (key.StartsWith("field:", StringComparison.Ordinal))
            {
                var m = Manager;
                return m != null ? Named_(m, key.Substring(6)) : null;
            }
            var all = Resources.FindObjectsOfTypeAll<PlayableDirector>();
            if (all == null) return null;
            var reg = ObjectRegistry.Instance;
            foreach (var d in all)
            {
                if (d == null) continue;
                try { if (EnemySync.KeyFor(reg, d) == key) return d; } catch { }
            }
            return null;
        }

        private static bool CanSend()
        {
            var s = Session.Instance;
            return s != null && s.Connected && !LoopGuard.ApplyingRemote;
        }

        internal static void OnWorldCinematicStarted(bool fullspeed, bool canSkip, bool blocks)
        {
            if (!CanSend()) return;
            var m = Manager;
            PlayableDirector d = null;
            try { d = m != null ? m.currentlyPlaying : null; } catch { }
            if (d == null) return;
            string key = KeyOf(d);
            var w = new Writer();
            w.U8(MsgPlay).Str(key).Bool(canSkip).Bool(blocks);
            Session.Instance.SendReliable(PacketKind.Cutscene, w.ToArray());
            _sent++;
            Log.Info($"cutscene -> peer: {key}");
        }

        internal static void OnSkip()
        {
            if (!CanSend()) return;
            var w = new Writer();
            w.U8(MsgSkip);
            Session.Instance.SendReliable(PacketKind.Cutscene, w.ToArray());
        }

        /// <summary>True when this director is already running locally.</summary>
        internal static bool IsPlaying(PlayableDirector d)
        {
            var m = Manager;
            if (m == null || d == null) return false;
            try
            {
                var cur = m.currentlyPlaying;
                return cur != null && cur.Pointer == d.Pointer && cur.state == PlayState.Playing;
            }
            catch { return false; }
        }

        /// <summary>The ending's last line rolls the credits (a scene change). The partner
        /// stayed in the world without them.</summary>
        internal static void OnSceneSwitch(string sceneName)
        {
            Log.Info($"scene load: FadeAndLoadScene('{sceneName}')");
            if (string.IsNullOrEmpty(sceneName) || sceneName.IndexOf("Credits", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (!CanSend()) return;
            var w = new Writer();
            w.U8(MsgCredits).Str(sceneName);
            Session.Instance.SendReliable(PacketKind.Cutscene, w.ToArray());
            Log.Info("credits -> peer: " + sceneName);
        }

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            byte kind = r.U8();
            if (kind == MsgCredits)
            {
                string scene = r.Str();
                LoopGuard.Apply(() =>
                {
                    try { global::DelusionSceneControl.Instance?.FadeAndLoadScene(scene); Log.Info("credits <- peer: " + scene); }
                    catch (Exception e) { Log.Warn("credits: " + e.Message); }
                });
                return;
            }
            if (kind == MsgSkip)
            {
                LoopGuard.Apply(() =>
                {
                    try { global::DelusionCinematics.Instance?.TrySkipCinematic(); _skipped++; }
                    catch (Exception e) { Log.Debug("cutscene skip: " + e.Message); }
                });
                return;
            }
            string key = r.Str();
            bool canSkip = r.Bool(), blocks = r.Bool();
            var d = Resolve(key);
            if (d == null)
            {
                _missing++;
                Log.Warn($"cutscene <- peer: {key} is not loaded here; skipped");
                return;
            }
            if (IsPlaying(d)) return;
            LoopGuard.Apply(() =>
            {
                try
                {
                    global::DelusionCinematics.Instance.WorldCutscene(d, canSkip, blocks);
                    _played++;
                    Log.Info($"cutscene <- peer: playing {key}");
                }
                catch (Exception e) { Log.Warn($"cutscene {key} failed: {e.Message}"); }
            });
        }
    }

    [HarmonyPatch(typeof(global::DelusionCinematics), "WorldCinematicStarted")]
    internal static class Patch_Cinematics_Started
    {
        [HarmonyPostfix]
        static void Postfix(bool isFullspeed, bool canSkip, bool blocksRegionLoading)
            => CutsceneSync.OnWorldCinematicStarted(isFullspeed, canSkip, blocksRegionLoading);
    }

    // The ending's last line switches to the credits scene; the partner follows.
    [HarmonyPatch(typeof(global::DelusionSceneControl), nameof(global::DelusionSceneControl.FadeAndLoadScene))]
    internal static class Patch_SceneControl_FadeAndLoadScene
    {
        [HarmonyPrefix] static void Prefix(string sceneName) => CutsceneSync.OnSceneSwitch(sceneName);
    }

    [HarmonyPatch(typeof(global::DelusionCinematics), nameof(global::DelusionCinematics.TrySkipCinematic))]
    internal static class Patch_Cinematics_Skip
    {
        [HarmonyPrefix] static void Prefix() => CutsceneSync.OnSkip();
    }

    [HarmonyPatch(typeof(global::CutsceneTrigger), nameof(global::CutsceneTrigger.TriggerCutscene))]
    internal static class Patch_CutsceneTrigger
    {
        // Both players can walk into the same trigger; the partner's replay is already
        // running, so the local duplicate must not restart it.
        [HarmonyPrefix]
        static bool Prefix(global::CutsceneTrigger __instance)
        {
            try { return !CutsceneSync.IsPlaying(__instance.sceneToPlay); }
            catch { return true; }
        }
    }
}
