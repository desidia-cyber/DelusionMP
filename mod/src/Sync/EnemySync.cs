using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Enemies and bosses.
    ///
    /// There is no `Boss` type in this game (verified by type search): bosses are ordinary
    /// EnemyController objects with a high CombatantLevel, wrapped in scripted scenes. So
    /// enemy replication covers bosses for free, and only the surrounding cutscene triggers
    /// need separate handling (see CutsceneSync).
    ///
    /// Ownership is per spawner. EnemySpawner (918 in the shipped assets) already implements
    /// ISaveable and owns kill state, so the host drives EnemyController AI and the client
    /// turns its local copy into a puppet.
    /// </summary>
    public static class EnemySync
    {
        private const float StateHz = 10f;
        private const float StateInterval = 1f / StateHz;
        private static float _nextState;

        private sealed class Puppet
        {
            public global::EnemyController Controller;
            public Vector3 TargetPos;
            public float TargetYaw;
            public float LastUpdate;
        }

        private static readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>(256);

        public static void Init() { _puppets.Clear(); _dead.Clear(); _index.Clear(); }

        // Enemy keys are usually hierarchy paths (spawned enemies carry no saveable id), so
        // they are not in the saveable registry. Keep a per-peer index, rebuilt lazily.
        private static readonly Dictionary<string, global::EnemyController> _index =
            new Dictionary<string, global::EnemyController>(512, StringComparer.Ordinal);
        private static float _indexBuiltAt = -10f;

        private static global::EnemyController Resolve(string key)
        {
            if (_index.TryGetValue(key, out var c) && c != null) return c;
            if (Time.realtimeSinceStartup - _indexBuiltAt < 1f) return null;
            _indexBuiltAt = Time.realtimeSinceStartup;
            _index.Clear();
            var reg = ObjectRegistry.Instance;
            var all = Resources.FindObjectsOfTypeAll<global::EnemyController>();
            if (all != null)
                foreach (var e in all)
                {
                    if (e == null) continue;
                    try { _index[KeyFor(reg, e)] = e; } catch { }
                }
            return _index.TryGetValue(key, out c) ? c : null;
        }

        // ------------------------------------------------------------------ combat
        //
        // Damage: the host owns enemy health. A client hit is applied locally for instant
        // feedback (the host's 10 Hz health stream corrects it) and forwarded to the host,
        // which applies it through the game's own EnemyDamageTrigger.ApplyDamage - so
        // armour, stagger, poison and the kill all run where the authority is.
        // Death: the host's OnDeath is replicated so the client's copy dies with the game's
        // own death path (animation, ragdoll, loot, spawner bookkeeping) exactly once.

        private const byte CombatDamage = 0;
        private const byte CombatDeath = 1;

        private static readonly HashSet<int> _dead = new HashSet<int>();
        private static int _hitsForwarded, _hitsApplied, _deathsSent, _deathsApplied;
        public static string Stats =>
            $"hitsForwarded={_hitsForwarded} hitsApplied={_hitsApplied} deathsSent={_deathsSent} deathsApplied={_deathsApplied} puppets={_puppets.Count}";

        internal static void OnPlayerHit(global::EnemyDamageTrigger trig, int damage, float piercing, int poison,
                                         int burn, int charm, float slow, int stagger)
        {
            var s = Session.Instance;
            if (s == null || s.Role != SessionRole.Client || !s.Connected || LoopGuard.ApplyingRemote) return;
            var c = trig != null ? trig.myController : null;
            if (c == null) return;
            var key = KeyFor(ObjectRegistry.Instance, c);
            var w = new Writer();
            w.U8(CombatDamage).Str(key).I32(damage).F32(piercing).I32(poison).I32(burn).I32(charm).F32(slow).I32(stagger);
            s.SendReliable(PacketKind.CombatEvent, w.ToArray());
            _hitsForwarded++;
        }

        internal static void OnDeath(global::EnemyController c, bool killedByPlayer)
        {
            if (c == null) return;
            _dead.Add(c.GetInstanceID());
            var s = Session.Instance;
            if (s == null || s.Role != SessionRole.Host || !s.Connected || LoopGuard.ApplyingRemote) return;
            var w = new Writer();
            w.U8(CombatDeath).Str(KeyFor(ObjectRegistry.Instance, c)).Bool(killedByPlayer);
            s.SendReliable(PacketKind.CombatEvent, w.ToArray());
            _deathsSent++;
        }

        public static void ApplyCombat(byte[] payload)
        {
            var r = new Reader(payload);
            byte kind = r.U8();
            string key = r.Str();
            var c = Resolve(key);
            if (c == null) { Log.Warn("combat: unknown enemy " + key); return; }
            var go = c.gameObject;

            if (kind == CombatDamage)
            {
                if (Session.Instance?.Role != SessionRole.Host) return;
                int dmg = r.I32(); float pierce = r.F32(); int poison = r.I32(); int burn = r.I32();
                int charm = r.I32(); float slow = r.F32(); int stagger = r.I32();
                if (_dead.Contains(c.GetInstanceID())) return;
                var trig = go.GetComponentInChildren<global::EnemyDamageTrigger>(true);
                if (trig == null) return;
                // Outside LoopGuard: the host's resulting death must replicate normally.
                try
                {
                    trig.ApplyDamage(dmg, pierce, poison, burn, charm, slow, false, true, null, stagger);
                    _hitsApplied++;
                }
                catch (Exception e) { Log.Warn($"client hit on {key} failed: {e.Message}"); }
            }
            else if (kind == CombatDeath)
            {
                bool byPlayer = r.Bool();
                if (_dead.Contains(c.GetInstanceID())) return;   // already died locally
                LoopGuard.Apply(() =>
                {
                    try
                    {
                        if (c.myStats != null) Priv.SetProp(c.myStats, "currentHealth", 0);
                        c.OnDeath(byPlayer);
                        _deathsApplied++;
                    }
                    catch (Exception e) { Log.Warn($"replicated death {key} failed: {e.Message}"); }
                });
                _dead.Add(c.GetInstanceID());
                _puppets.Remove(c.GetInstanceID());
            }
        }

        /// <summary>Rig/debug: nearest live enemies to the local player.</summary>
        public static List<(string key, global::EnemyController c, float dist)> Nearest(int n)
        {
            var list = new List<(string, global::EnemyController, float)>();
            var p = GameRefs.Player;
            var all = Resources.FindObjectsOfTypeAll<global::EnemyController>();
            if (p == null || all == null) return list;
            var reg = ObjectRegistry.Instance;
            foreach (var e in all)
            {
                if (e == null || !e.gameObject.activeInHierarchy) continue;
                list.Add((KeyFor(reg, e), e, Vector3.Distance(e.transform.position, p.transform.position)));
            }
            list.Sort((a, b) => a.Item3.CompareTo(b.Item3));
            if (list.Count > n) list.RemoveRange(n, list.Count - n);
            return list;
        }

        public static string PuppetInfo(global::EnemyController c)
        {
            if (c == null || !_puppets.TryGetValue(c.GetInstanceID(), out var p)) return "";
            var d = Vector3.Distance(c.transform.position, p.TargetPos);
            return $" target={p.TargetPos.x:F1},{p.TargetPos.y:F1},{p.TargetPos.z:F1} off={d:F2} age={Time.realtimeSinceStartup - p.LastUpdate:F2}s";
        }

        public static bool IsDead(global::EnemyController c) => c != null && _dead.Contains(c.GetInstanceID());

        /// <summary>Host: broadcast engaged enemies at 10 Hz.</summary>
        public static void TickHost()
        {
            if (Session.Instance?.Role != SessionRole.Host) return;
            if (Time.realtimeSinceStartup < _nextState) return;
            _nextState = Time.realtimeSinceStartup + StateInterval;

            var reg = ObjectRegistry.Instance;
            if (reg.NeedsRebuild) reg.Rebuild(CurrentScene());

            var enemies = Resources.FindObjectsOfTypeAll<global::EnemyController>();
            if (enemies == null) return;

            var buffered = new List<(string key, global::EnemyController c)>(64);
            foreach (var e in enemies)
            {
                if (e == null || !e.gameObject.activeInHierarchy) continue;
                if (_dead.Contains(e.GetInstanceID())) continue;
                string key = KeyFor(reg, e);
                if (key == null) continue;
                buffered.Add((key, e));
            }
            if (buffered.Count > 0) FlushBatch(buffered);
        }

        /// <summary>
        /// Packs enemy states into datagrams by size. Keys are hierarchy paths (~120 B), so
        /// a fixed count per packet overflowed the 1200 B datagram and every batch was
        /// dropped by the transport.
        /// </summary>
        private static void FlushBatch(List<(string key, global::EnemyController c)> batch)
        {
            var entries = new List<byte[]>(batch.Count);
            foreach (var (key, c) in batch)
            {
                var e = new Writer();
                e.Str(key);
                e.Position(c.transform.position);
                e.Yaw(c.transform.eulerAngles.y);
                int hp = 0;
                try { hp = c.myStats != null ? c.myStats.currentHealth : 0; } catch { }
                e.I32(hp);
                e.U8((byte)(int)c.combatMode);
                e.U8((byte)(int)c.hostileType);
                e.Bool(c.isRunning);
                e.Bool(c.hasBeenProvoked);
                entries.Add(e.ToArray());
            }

            int i = 0;
            while (i < entries.Count)
            {
                int size = 1, n = 0;
                while (i + n < entries.Count && n < 255 && size + entries[i + n].Length <= Proto.MaxPayload - 16)
                    size += entries[i + n++].Length;
                if (n == 0) { i++; continue; }   // a single oversized entry: skip it
                var w = new Writer();
                w.U8((byte)n);
                for (int k = 0; k < n; k++) w.Bytes(entries[i + k]);
                Session.Instance.SendUnreliable(PacketKind.EnemyState, w.ToArray());
                i += n;
            }
        }

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            int n = r.U8();
            var reg = ObjectRegistry.Instance;

            for (int i = 0; i < n; i++)
            {
                string key = r.Str();
                Vector3 pos = r.Position();
                float yaw = r.Yaw();
                int hp = r.I32();
                var mode = (global::EnemyController.CombatModes)r.U8();
                var hostile = (global::EnemyController.HostileType)r.U8();
                bool running = r.Bool();
                bool provoked = r.Bool();

                var c = Resolve(key);
                if (c == null || _dead.Contains(c.GetInstanceID())) continue;

                var p = PuppetFor(c);
                p.TargetPos = pos;
                p.TargetYaw = yaw;
                p.LastUpdate = Time.realtimeSinceStartup;

                LoopGuard.Apply(() =>
                {
                    try
                    {
                        c.hostileType = hostile;
                        c.isRunning = running;
                        c.hasBeenProvoked = provoked;
                        if (c.myStats != null) Priv.SetProp(c.myStats, "currentHealth", hp);
                    }
                    catch (Exception e) { Log.Debug($"puppet apply failed: {e.Message}"); }
                });
            }
        }

        private static Puppet PuppetFor(global::EnemyController c)
        {
            int id = c.GetInstanceID();
            if (_puppets.TryGetValue(id, out var p)) return p;
            p = new Puppet { Controller = c };
            // A puppeted enemy must not run its own AI: its NavMeshAgent would fight the
            // replicated transforms and produce the classic rubber-banding enemy.
            try
            {
                var agent = Priv.GetField<UnityEngine.AI.NavMeshAgent>(c, "agent");
                if (agent != null) agent.enabled = false;
            }
            catch { }
            _puppets[id] = p;
            return p;
        }

        /// <summary>Client: interpolate puppets toward their last replicated transform.</summary>
        public static void TickClient()
        {
            if (Session.Instance?.Role == SessionRole.Host) return;
            float now = Time.realtimeSinceStartup;
            foreach (var p in _puppets.Values)
            {
                if (p.Controller == null) continue;
                // Freeze if the host stopped talking about this enemy (out of range,
                // dead, or peer travelling) rather than extrapolating into a wall.
                if (now - p.LastUpdate > 0.5f) continue;

                var t = p.Controller.transform;
                t.position = Vector3.Lerp(t.position, p.TargetPos, 0.35f);
                var e = t.eulerAngles;
                t.rotation = Quaternion.Euler(e.x, Mathf.LerpAngle(e.y, p.TargetYaw, 0.35f), e.z);
            }
        }

        private static string CurrentScene()
        {
            try { return GameRefs.Scene?.currentSceneName ?? ""; }
            catch { return ""; }
        }

        internal static string KeyFor(ObjectRegistry reg, Component c)
        {
            var saveable = c.GetComponent<global::SaveableObject>();
            if (saveable != null)
            {
                try
                {
                    var id = saveable.GetUniqueIdentifier();
                    if (!string.IsNullOrEmpty(id)) return reg.CurrentScene + "|" + id;
                }
                catch { }
            }
            return reg.CurrentScene + "|#path/" + ObjectRegistry.HierarchyPath(c.transform);
        }
    }

    [HarmonyPatch(typeof(global::EnemyDamageTrigger), nameof(global::EnemyDamageTrigger.ApplyDamage),
        new[] { typeof(int), typeof(float), typeof(int), typeof(int), typeof(int), typeof(float), typeof(bool),
                typeof(bool), typeof(global::EnemyController), typeof(int) })]
    internal static class Patch_EnemyDamage_Pierce
    {
        [HarmonyPrefix]
        static void Prefix(global::EnemyDamageTrigger __instance, int damage, float piercing, int poisonTimer,
                           int burnTimer, int charmPower, float slowMultiplier, bool isPlayer, int staggerLevel)
        {
            if (isPlayer)
                EnemySync.OnPlayerHit(__instance, damage, piercing, poisonTimer, burnTimer, charmPower, slowMultiplier, staggerLevel);
        }
    }

    [HarmonyPatch(typeof(global::EnemyDamageTrigger), nameof(global::EnemyDamageTrigger.ApplyDamage),
        new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(float), typeof(bool),
                typeof(bool), typeof(global::EnemyController), typeof(int) })]
    internal static class Patch_EnemyDamage_Plain
    {
        [HarmonyPrefix]
        static void Prefix(global::EnemyDamageTrigger __instance, int damage, int poisonTimer, int burnTimer,
                           int charmPower, float slowMultiplier, bool isPlayer, int staggerLevel)
        {
            if (isPlayer)
                EnemySync.OnPlayerHit(__instance, damage, 0f, poisonTimer, burnTimer, charmPower, slowMultiplier, staggerLevel);
        }
    }

    [HarmonyPatch(typeof(global::EnemyController), nameof(global::EnemyController.OnDeath))]
    internal static class Patch_EnemyController_OnDeath
    {
        [HarmonyPostfix]
        static void Postfix(global::EnemyController __instance, bool killedByPlayer)
            => EnemySync.OnDeath(__instance, killedByPlayer);
    }
}
