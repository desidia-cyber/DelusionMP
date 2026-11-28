using System;
using System.Collections.Generic;
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
    /// end-of-scene handling. Skips, ends, and CutsceneTrigger consumption are mirrored
    /// (including pending consumption for unloaded regions and join replay).
    ///
    /// Dialogue art cards (StartCinematic/ContinueCinematic) are not timelines; they reach
    /// the partner through DialogueSync.
    /// </summary>
    public static class CutsceneSync
    {
        private const byte MsgPlay = 0, MsgSkip = 1, MsgCredits = 2, MsgEnd = 3, MsgTriggerConsumed = 4;
        private static int _sent, _played, _skipped, _missing, _triggersConsumed;
        public static string Stats => $"sent={_sent} played={_played} skipped={_skipped} missing={_missing} consumed={_triggersConsumed}";

        private static readonly HashSet<string> _consumedTriggers = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _pendingTriggers = new HashSet<string>(StringComparer.Ordinal);
        private static float _nextPendingCheck;

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

        internal static void OnWorldCinematicEnded()
        {
            if (!CanSend()) return;
            var w = new Writer();
            w.U8(MsgEnd);
            Session.Instance.SendReliable(PacketKind.Cutscene, w.ToArray());
        }

        internal static void OnTriggerConsumed(global::CutsceneTrigger ct)
        {
            if (ct == null) return;
            string key = null;
            try { key = EnemySync.KeyFor(ObjectRegistry.Instance, ct); } catch { }
            if (string.IsNullOrEmpty(key)) return;
            _consumedTriggers.Add(key);
            if (!CanSend()) return;
            var w = new Writer();
            w.U8(MsgTriggerConsumed).Str(key);
            Session.Instance.SendReliable(PacketKind.Cutscene, w.ToArray());
        }

        public static void OnSessionStart()
        {
            _consumedTriggers.Clear();
            _pendingTriggers.Clear();
        }

        public static void SendJoinState()
        {
            var s = Session.Instance;
            if (s == null || !s.Connected || s.Role != SessionRole.Host) return;
            foreach (var key in _consumedTriggers)
            {
                var w = new Writer();
                w.U8(MsgTriggerConsumed).Str(key);
                s.SendReliable(PacketKind.Cutscene, w.ToArray());
            }
        }

        public static void Tick()
        {
            if (_pendingTriggers.Count == 0 || !GameRefs.InWorld) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextPendingCheck) return;
            _nextPendingCheck = now + 1.5f;
            List<string> done = null;
            foreach (var key in _pendingTriggers)
                if (TryMarkTriggerConsumed(key)) (done ??= new List<string>()).Add(key);
            if (done != null) foreach (var k in done) _pendingTriggers.Remove(k);
        }

        private static bool TryMarkTriggerConsumed(string key)
        {
            try
            {
                var reg = ObjectRegistry.Instance;
                var all = Resources.FindObjectsOfTypeAll<global::CutsceneTrigger>();
                if (all == null) return false;
                foreach (var ct in all)
                {
                    if (ct == null || !ct.gameObject.scene.IsValid()) continue;
                    if (EnemySync.KeyFor(reg, ct) == key)
                    {
                        ct.hasBeenPlayed = true;
                        _triggersConsumed++;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        internal static bool IsConsumed(global::CutsceneTrigger ct)
        {
            if (ct == null) return false;
            try
            {
                var key = EnemySync.KeyFor(ObjectRegistry.Instance, ct);
                return !string.IsNullOrEmpty(key) && _consumedTriggers.Contains(key);
            }
            catch { return false; }
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
                    // The ending's driver saves right before the credits (the finished game:
                    // ending, karma, story facts), and loading the credits re-reads each
                    // machine's save. Without the same save here the partner's file stayed
                    // before the ending, and its facts fell back to it.
                    try { GameRefs.Save?.Save(); Log.Info("credits: saved before following the partner"); }
                    catch (Exception e) { Log.Warn("credits save: " + e.Message); }
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
            if (kind == MsgEnd)
            {
                LoopGuard.Apply(() =>
                {
                    try
                    {
                        var m = Manager;
                        if (m != null && m.currentlyPlaying != null && m.currentlyPlaying.state == PlayState.Playing)
                            global::DelusionCinematics.Instance?.WorldCinematicEnded();
                    }
                    catch (Exception e) { Log.Debug("cutscene end: " + e.Message); }
                });
                return;
            }
            if (kind == MsgTriggerConsumed)
            {
                string tkey = r.Str();
                if (!string.IsNullOrEmpty(tkey))
                {
                    _consumedTriggers.Add(tkey);
                    if (!TryMarkTriggerConsumed(tkey)) _pendingTriggers.Add(tkey);
                }
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

    [HarmonyPatch(typeof(global::DelusionCinematics), nameof(global::DelusionCinematics.WorldCinematicEnded))]
    internal static class Patch_Cinematics_Ended
    {
        [HarmonyPostfix] static void Postfix() => CutsceneSync.OnWorldCinematicEnded();
    }

    [HarmonyPatch(typeof(global::CutsceneTrigger), nameof(global::CutsceneTrigger.TriggerCutscene))]
    internal static class Patch_CutsceneTrigger
    {
        // Both players can walk into the same trigger; the partner's replay is already
        // running or was already played, so the local duplicate must not restart it.
        [HarmonyPrefix]
        static bool Prefix(global::CutsceneTrigger __instance)
        {
            try
            {
                if (CutsceneSync.IsPlaying(__instance.sceneToPlay)) return false;
                if (__instance.triggerOncePerSession && CutsceneSync.IsConsumed(__instance))
                {
                    __instance.hasBeenPlayed = true;
                    return false;
                }
                return true;
            }
            catch { return true; }
        }

        [HarmonyPostfix]
        static void Postfix(global::CutsceneTrigger __instance) => CutsceneSync.OnTriggerConsumed(__instance);
    }
}
