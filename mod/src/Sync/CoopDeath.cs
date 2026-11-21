using System;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Co-op death rules (the dollsMP three-case rule, adapted to Dread Delusion):
    ///
    ///   * One player dies while the partner is alive: no world reset. After a short respawn
    ///     delay the player gets up next to the partner (not at a distant respawn point) and
    ///     carries a death stack: -10% max health, mana and stamina per stack (5 at most) until
    ///     they rest in a bed. Enemies stay dead.
    ///   * Both players are down at the same time (party wipe): the game's own death applies
    ///     to both - each respawns at their own respawn point, enemies on "respawn on player
    ///     death" spawners come back, and the stacks are cleared (the reset is the price).
    ///   * Dread Delusion has no boss-fight state (bosses are ordinary enemies), so there is no
    ///     "held" case.
    ///
    /// Stacks are saved per player in a player-owned fact, so they survive a reload.
    /// </summary>
    public static class CoopDeath
    {
        public static bool Enabled = true;
        public static int MaxStacks = 5;
        public static float PctPerStack = 10f;
        public static float RespawnDelay = 8f;
        public static bool RespawnAtPartner = true;

        public const string StackFact = "ddmpDeathStacks";
        private const byte MsgDown = 0, MsgUp = 1;

        private static bool _localDown, _peerDown;
        private static float _deathAt;
        private static bool _wipeEpisode;
        private static float _respawnDue = -1f;
        private static global::DelusionPlayerActions _respawnFor;
        private static bool _bypassRespawn;
        internal static bool BypassSpawners;
        private static int _stacks = -1;
        private static int _wipes, _singles;

        public static string Stats =>
            $"localDown={_localDown} peerDown={_peerDown} wipeEpisode={_wipeEpisode} stacks={Stacks} wipes={_wipes} singleDeaths={_singles} respawnIn={(_respawnDue > 0 ? _respawnDue - Time.realtimeSinceStartup : 0f):F1}";

        private static bool Connected => Session.Instance != null && Session.Instance.Connected;

        // ------------------------------------------------------------------ stacks

        public static int Stacks
        {
            get
            {
                if (_stacks < 0)
                {
                    _stacks = 0;
                    try { var f = GameRefs.Facts; if (f != null && f.GetFact(StackFact, out int v)) _stacks = Mathf.Clamp(v, 0, MaxStacks); } catch { }
                }
                return _stacks;
            }
        }

        public static void SetStacks(int n, string why)
        {
            n = Mathf.Clamp(n, 0, MaxStacks);
            if (n == Stacks) return;
            _stacks = n;
            try { GameRefs.Facts?.SetFact(StackFact, n); } catch { }
            Log.Info($"coop death: {n} stack(s) ({why})");
            Note(n == 0
                ? "Your wounds have healed."
                : $"Wounded ({n}): max health, mana and stamina -{n * PctPerStack:F0}% until you rest.");
            ClampCurrent();
        }

        /// <summary>Multiplier for the local player's max health/mana/stamina.</summary>
        public static float Factor => Enabled ? Mathf.Clamp(1f - Stacks * PctPerStack / 100f, 0.1f, 1f) : 1f;

        private static IntPtr _hp, _mp, _sp;
        private static float _nextCache;

        internal static void RefreshStatPointers()
        {
            if (Time.realtimeSinceStartup < _nextCache) return;
            _nextCache = Time.realtimeSinceStartup + 2f;
            try
            {
                var cs = GameRefs.Stats;
                _hp = cs?.maxHealth?.Pointer ?? IntPtr.Zero;
                _mp = cs?.maxMana?.Pointer ?? IntPtr.Zero;
                _sp = cs?.maxStamina?.Pointer ?? IntPtr.Zero;
            }
            catch { }
        }

        internal static bool IsLocalMaxStat(IntPtr p) => p != IntPtr.Zero && (p == _hp || p == _mp || p == _sp);

        private static void ClampCurrent()
        {
            try
            {
                var cs = GameRefs.Stats;
                if (cs == null) return;
                float mh = cs.maxHealth.GetValue(), mm = cs.maxMana.GetValue(), ms = cs.maxStamina.GetValue();
                if (cs.currentHealth > mh) cs.currentHealth = mh;
                if (cs.currentMana > mm) cs.currentMana = mm;
                if (cs.currentStamina > ms) cs.currentStamina = ms;
            }
            catch { }
        }

        private static void Note(string text)
        {
            try { global::DelusionNote.Instance?.StartNewNote(null, text, false); } catch { }
        }

        // ------------------------------------------------------------------ death flow

        /// <summary>StartPlayerDeath succeeded on this machine.</summary>
        internal static void OnLocalDeath()
        {
            _localDown = true;
            _deathAt = Time.realtimeSinceStartup;
            if (!Connected) return;
            var w = new Writer();
            w.U8(MsgDown);
            Session.Instance.SendReliable(PacketKind.CoopDeath, w.ToArray());
            if (_peerDown) BeginWipe("both down");
            else { _singles++; Log.Info("coop death: down while the partner is up"); }
        }

        /// <summary>The game wants to respawn the local player (end of the death fade).</summary>
        internal static bool AllowRespawn(global::DelusionPlayerActions pa)
        {
            if (!Connected || _bypassRespawn || _wipeEpisode || !Enabled) return true;
            float due = _deathAt + RespawnDelay;
            if (Time.realtimeSinceStartup >= due) return true;
            _respawnDue = due;
            _respawnFor = pa;
            return false;   // held; Tick calls it when the delay is over (or at once on a wipe)
        }

        /// <summary>The local player is back up.</summary>
        internal static void OnRespawned()
        {
            bool wasWipe = _wipeEpisode;
            _localDown = false;
            _respawnDue = -1f;
            if (!Connected) return;
            var w = new Writer();
            w.U8(MsgUp);
            Session.Instance.SendReliable(PacketKind.CoopDeath, w.ToArray());

            if (wasWipe)
            {
                SetStacks(0, "party wipe - the reset is the price");
            }
            else if (Enabled)
            {
                SetStacks(Stacks + 1, "died while the partner was up");
                if (RespawnAtPartner) MoveToPartner();
            }
            if (!_peerDown) _wipeEpisode = false;
        }

        private static void MoveToPartner()
        {
            if (!PlayerSync.TryPeerPose(out var pos, out float yaw)) return;
            try
            {
                var tmp = new GameObject("DelusionMP_RespawnAtPartner");
                var back = Quaternion.Euler(0, yaw, 0) * Vector3.back;
                tmp.transform.SetPositionAndRotation(pos + back * 1.5f + Vector3.up * 0.2f, Quaternion.Euler(0, yaw, 0));
                global::DelusionManager.Instance?.RepositionPlayer(tmp.transform, true);
                UnityEngine.Object.Destroy(tmp, 5f);
                Log.Info("coop death: respawned next to the partner");
            }
            catch (Exception e) { Log.Warn("respawn at partner: " + e.Message); }
        }

        private static void BeginWipe(string why)
        {
            if (_wipeEpisode) return;
            _wipeEpisode = true;
            _wipes++;
            Log.Info($"coop death: PARTY WIPE ({why}) - enemies return, both respawn at their own points");
            EnemySync.ForgetAllHealth();
            // A held respawn goes now.
            if (_respawnDue > 0f) _respawnDue = Time.realtimeSinceStartup;
            // The host owns enemy respawns; it runs the game's own "player died" respawns.
            if (Session.Instance?.Role == SessionRole.Host) RespawnEnemies();
        }

        private static void RespawnEnemies()
        {
            int n = 0;
            BypassSpawners = true;
            try
            {
                foreach (var sp in Resources.FindObjectsOfTypeAll<global::EnemySpawner>())
                {
                    if (sp == null || !sp.gameObject.scene.IsValid() || !sp.respawnsOnPlayerDeath) continue;
                    try { sp.PlayerDied(); n++; } catch { }
                }
            }
            finally { BypassSpawners = false; }
            Log.Info($"coop death: {n} spawner(s) respawned their enemies");
        }

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            byte kind = r.U8();
            if (kind == MsgDown)
            {
                _peerDown = true;
                if (_localDown) BeginWipe("partner went down too");
            }
            else if (kind == MsgUp)
            {
                _peerDown = false;
                if (!_localDown) _wipeEpisode = false;
            }
        }

        /// <summary>Per frame.</summary>
        public static void Tick()
        {
            RefreshStatPointers();
            if (_respawnDue > 0f && Time.realtimeSinceStartup >= _respawnDue)
            {
                _respawnDue = -1f;
                var pa = _respawnFor ?? GameRefs.PlayerActions;
                _respawnFor = null;
                _bypassRespawn = true;
                try { pa?.PlayerDeathRespawn(); }
                catch (Exception e) { Log.Warn("held respawn: " + e.Message); }
                finally { _bypassRespawn = false; }
            }
            if (!Connected) { _peerDown = false; }
        }

        /// <summary>A real rest in a bed (local).</summary>
        internal static void OnRested()
        {
            if (Stacks > 0) SetStacks(0, "rested");
        }
    }

    // ---------------------------------------------------------------------- patches

    [HarmonyPatch(typeof(global::DelusionPlayerActions), nameof(global::DelusionPlayerActions.StartPlayerDeath))]
    internal static class Patch_CoopDeath_Start
    {
        [HarmonyPostfix] static void Postfix(bool __result) { if (__result) CoopDeath.OnLocalDeath(); }
    }

    [HarmonyPatch(typeof(global::DelusionPlayerActions), nameof(global::DelusionPlayerActions.PlayerDeathRespawn))]
    internal static class Patch_CoopDeath_Respawn
    {
        // Harmony runs postfixes even when a prefix skipped the original; only a respawn that
        // really happened counts.
        [HarmonyPrefix]
        static bool Prefix(global::DelusionPlayerActions __instance, out bool __state)
        {
            __state = CoopDeath.AllowRespawn(__instance);
            return __state;
        }
        [HarmonyPostfix] static void Postfix(bool __state) { if (__state) CoopDeath.OnRespawned(); }
    }

    [HarmonyPatch(typeof(global::EnemySpawner), nameof(global::EnemySpawner.PlayerDied))]
    internal static class Patch_CoopDeath_SpawnerPlayerDied
    {
        // In co-op only a party wipe brings enemies back; one player's death does not.
        [HarmonyPrefix]
        static bool Prefix()
        {
            if (Session.Instance == null || !Session.Instance.Connected) return true;
            return CoopDeath.BypassSpawners;
        }
    }

    [HarmonyPatch(typeof(global::Stat), nameof(global::Stat.GetValue))]
    internal static class Patch_CoopDeath_StatGetValue
    {
        [HarmonyPostfix]
        static void Postfix(global::Stat __instance, ref int __result)
        {
            if (__result <= 0 || __instance == null) return;
            if (!CoopDeath.IsLocalMaxStat(__instance.Pointer)) return;
            float f = CoopDeath.Factor;
            if (f < 1f) __result = Mathf.Max(1, Mathf.RoundToInt(__result * f));
        }
    }

    [HarmonyPatch(typeof(global::DelusionDayNight), "ScreenFadeEnded")]
    internal static class Patch_CoopDeath_Rest
    {
        // ScreenFadeEnded also ends other fades (the death fade among them); only a sleep in a
        // bed heals the wounds. The flag is read before the method clears it.
        [HarmonyPrefix]
        static void Prefix(global::DelusionDayNight __instance, out bool __state)
        {
            __state = false;
            try { __state = __instance.restingInBed && !LoopGuard.ApplyingRemote; } catch { }
        }
        [HarmonyPostfix] static void Postfix(bool __state) { if (__state) CoopDeath.OnRested(); }
    }
}
