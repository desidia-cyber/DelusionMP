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

        // Last health seen for each enemy (local or streamed). An enemy that comes back into
        // play here - an encounter zone re-entered, a region scene reloaded - is rebuilt by
        // the game at full health while the partner may have been fighting it.
        private const float HealthMemory = 90f;
        private static readonly Dictionary<string, (int hp, float at)> _lastHp = new Dictionary<string, (int, float)>(256, StringComparer.Ordinal);
        private static readonly HashSet<int> _activeLastTick = new HashSet<int>();
        private static readonly HashSet<int> _activeNow = new HashSet<int>();

        private static void NoteHealth(string key, int hp) => _lastHp[key] = (hp, Time.realtimeSinceStartup);

        /// <summary>A real respawn (rest, day, party wipe): forget the remembered health.</summary>
        internal static void ForgetHealth(global::EnemyController c)
        {
            if (c == null) return;
            var k = KeyFor(ObjectRegistry.Instance, c);
            _lastHp.Remove(k);
            _returning.Remove(k);
        }
        internal static void ForgetAllHealth() { _lastHp.Clear(); _returning.Clear(); }

        // The game refills health a little after the enemy is re-enabled (spawner setup), so a
        // returning enemy is watched for a few seconds.
        private const float ReturnWatch = 3f;
        private static readonly Dictionary<string, (int hp, float until)> _returning = new Dictionary<string, (int, float)>(StringComparer.Ordinal);

        private static void RestoreHealthOnReturn(global::EnemyController e, string key)
        {
            if (!_lastHp.TryGetValue(key, out var m) || Time.realtimeSinceStartup - m.at > HealthMemory || m.hp <= 0) return;
            _returning[key] = (m.hp, Time.realtimeSinceStartup + ReturnWatch);
        }

        /// <summary>Returns true while the enemy's health is being held at the fight's value.</summary>
        private static bool WatchReturn(global::EnemyController e, string key)
        {
            if (!_returning.TryGetValue(key, out var w)) return false;
            if (Time.realtimeSinceStartup > w.until) { _returning.Remove(key); return false; }
            try
            {
                var st = e.myStats;
                if (st != null && st.currentHealth > w.hp)
                {
                    int was = st.currentHealth;
                    LoopGuard.Apply(() => Priv.SetProp(st, "currentHealth", w.hp));
                    Log.Info($"enemy {e.gameObject.name}: back in play at {was} hp, kept the fight's {w.hp}");
                }
            }
            catch { }
            return true;
        }

        internal static void ForgetReturn(string key) => _returning.Remove(key);

        private sealed class Puppet
        {
            public global::EnemyController Controller;
            public Vector3 TargetPos;
            public float TargetYaw;
            public float LastUpdate;
        }

        private static readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>(256);

        public static void Init()
        {
            _puppets.Clear(); _dead.Clear(); _index.Clear();
            _owner.Clear(); _hostOwnedAt.Clear(); _clientStateAt.Clear(); _puppetMode.Clear();
        }

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
                    try { if (e.gameObject.scene.IsValid()) _index[CachedKey(reg, e)] = e; } catch { }
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
            $"hitsForwarded={_hitsForwarded} hitsApplied={_hitsApplied} deathsSent={_deathsSent} deathsApplied={_deathsApplied} puppets={_puppets.Count} puppetMode={_puppetMode.Count} hostAssignedToClient={_ownedByClient}";

        internal static void OnPlayerHit(global::EnemyDamageTrigger trig, int damage, float piercing, int poison,
                                         int burn, int charm, float slow, int stagger)
        {
            var s = Session.Instance;
            if (s == null || !s.Connected || LoopGuard.ApplyingRemote) return;
            var c = trig != null ? trig.myController : null;
            if (c == null) return;
            // The simulating peer owns health: hits on a local puppet go to it. The local
            // hit still lands for instant feedback; the owner's stream corrects it.
            if (!IsPuppet(c)) return;
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
            if (s == null || !s.Connected || LoopGuard.ApplyingRemote) return;
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
                int dmg = r.I32(); float pierce = r.F32(); int poison = r.I32(); int burn = r.I32();
                int charm = r.I32(); float slow = r.F32(); int stagger = r.I32();
                if (_dead.Contains(c.GetInstanceID())) return;
                var trig = go.GetComponentInChildren<global::EnemyDamageTrigger>(true);
                if (trig == null) return;
                // Outside LoopGuard: the resulting death must replicate normally.
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
                _puppetMode.Remove(c.GetInstanceID());
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
            if (c == null || !_puppets.TryGetValue(c.GetInstanceID(), out var p)) return $" puppet={IsPuppet(c)}";
            var d = Vector3.Distance(c.transform.position, p.TargetPos);
            return $" puppet={IsPuppet(c)} target={p.TargetPos.x:F1},{p.TargetPos.y:F1},{p.TargetPos.z:F1} off={d:F2} age={Time.realtimeSinceStartup - p.LastUpdate:F2}s";
        }

        public static bool IsDead(global::EnemyController c) => c != null && _dead.Contains(c.GetInstanceID());

        /// <summary>The enemy's spawner respawned it: it is alive and replicates again.</summary>
        internal static void ClearDead(global::EnemyController c)
        {
            if (c != null) _dead.Remove(c.GetInstanceID());
            ForgetHealth(c);
        }

        // ------------------------------------------------------------------ ownership
        //
        // Each enemy is simulated by the player nearest to it. The host decides ownership
        // (it knows both positions) with hysteresis and tags every state entry with the
        // owner. The owner runs the game's own AI (which targets its local player); the
        // other peer turns its copy into a puppet. Enemies the host has not loaded (the
        // client is in a region the host is not) receive no host state, so the client
        // simulates them and streams them back.

        private const byte OwnerHost = 0, OwnerClient = 1;
        private const float Hysteresis = 4f;
        private const float HostSilence = 1.5f;

        // host: current owner per enemy instance (absent = host)
        private static readonly Dictionary<int, byte> _owner = new Dictionary<int, byte>(256);
        // client: last time the host said "host owns this"; enemies with no recent host
        // ownership are simulated locally
        private static readonly Dictionary<int, float> _hostOwnedAt = new Dictionary<int, float>(256);
        // host: last time client state arrived for an enemy
        private static readonly Dictionary<int, float> _clientStateAt = new Dictionary<int, float>(256);
        private static int _ownedByClient;

        private static bool IsClient => Session.Instance?.Role == SessionRole.Client;
        private static bool IsHost => Session.Instance?.Role == SessionRole.Host;

        /// <summary>True when the peer simulates this enemy and the local copy is a puppet.</summary>
        public static bool IsPuppet(global::EnemyController c)
        {
            if (c == null) return false;
            int id = c.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            if (IsHost)
                return _owner.TryGetValue(id, out var o) && o == OwnerClient
                       && _clientStateAt.TryGetValue(id, out var t) && now - t < HostSilence;
            if (IsClient)
                return _hostOwnedAt.TryGetValue(id, out var h) && now - h < HostSilence;
            return false;
        }

        private static byte DecideOwner(global::EnemyController e, bool havePeer, Vector3 peer, Vector3 self)
        {
            int id = e.GetInstanceID();
            byte cur = _owner.TryGetValue(id, out var o) ? o : OwnerHost;
            if (!havePeer) cur = OwnerHost;
            else
            {
                var p = e.transform.position;
                float dh = Vector3.Distance(p, self), dc = Vector3.Distance(p, peer);
                if (cur == OwnerHost && dc + Hysteresis < dh) cur = OwnerClient;
                else if (cur == OwnerClient && dh + Hysteresis < dc) cur = OwnerHost;
            }
            _owner[id] = cur;
            return cur;
        }

        /// <summary>Both roles: puppets interpolate every frame; at 10 Hz stream the
        /// enemies this peer simulates and switch local copies between AI and puppet.</summary>
        public static void Tick()
        {
            var s = Session.Instance;
            if (s == null || !s.Connected) return;
            float now = Time.realtimeSinceStartup;

            foreach (var pp in _puppets.Values)
            {
                if (pp.Controller == null) continue;
                if (now - pp.LastUpdate > 0.5f) continue;   // owner went quiet: freeze, don't extrapolate
                var t = pp.Controller.transform;
                t.position = Vector3.Lerp(t.position, pp.TargetPos, 0.35f);
                var ea = t.eulerAngles;
                t.rotation = Quaternion.Euler(ea.x, Mathf.LerpAngle(ea.y, pp.TargetYaw, 0.35f), ea.z);
            }

            if (now < _nextState) return;
            _nextState = now + StateInterval;

            var reg = ObjectRegistry.Instance;
            if (reg.NeedsRebuild) reg.Rebuild(CurrentScene());
            var enemies = CachedEnemies();
            if (enemies == null) return;

            var player = GameRefs.Player;
            Vector3 self = player != null ? player.transform.position : Vector3.zero;
            bool havePeer = PlayerSync.TryPeerPose(out var peer, out _) && player != null;

            var outgoing = new List<(string key, global::EnemyController c, byte owner)>(64);
            int clientOwned = 0;
            _activeNow.Clear();
            foreach (var e in enemies)
            {
                if (e == null || !e.gameObject.activeInHierarchy) continue;
                int eid = e.GetInstanceID();
                if (_dead.Contains(eid)) continue;
                string key = CachedKey(reg, e);
                if (key == null) continue;
                _activeNow.Add(eid);
                if (!_activeLastTick.Contains(eid)) RestoreHealthOnReturn(e, key);
                bool held = WatchReturn(e, key);
                try { if (e.myStats != null && !held) NoteHealth(key, e.myStats.currentHealth); } catch { }

                if (IsHost)
                {
                    byte owner = DecideOwner(e, havePeer, peer, self);
                    if (owner == OwnerClient) clientOwned++;
                    // Client-owned entries tell the client to simulate; their transform is
                    // ignored there. The host's copy becomes a puppet once the client's
                    // stream for it arrives.
                    outgoing.Add((key, e, owner));
                    SetSimulated(e, !IsPuppet(e));
                }
                else
                {
                    bool puppet = IsPuppet(e);
                    SetSimulated(e, !puppet);
                    if (!puppet) outgoing.Add((key, e, OwnerClient));
                }
            }
            _ownedByClient = clientOwned;
            _activeLastTick.Clear();
            _activeLastTick.UnionWith(_activeNow);
            if (outgoing.Count > 0) FlushBatch(outgoing);
        }

        // Kept for older call sites.
        public static void TickHost() => Tick();
        public static void TickClient() => Tick();

        private static readonly HashSet<int> _puppetMode = new HashSet<int>();

        /// <summary>Switches a local enemy between running its own AI and being a puppet.</summary>
        private static void SetSimulated(global::EnemyController c, bool simulate)
        {
            int id = c.GetInstanceID();
            bool isPuppet = _puppetMode.Contains(id);
            if (simulate == !isPuppet) return;
            UnityEngine.AI.NavMeshAgent agent = null;
            try { agent = c.agent; } catch { }
            if (simulate)
            {
                _puppetMode.Remove(id);
                _puppets.Remove(id);
                // Re-enabling the controller runs the game's enemy setup, which refills
                // health: a hand-over mid-fight healed the enemy. Keep what the previous
                // owner streamed.
                int keepHp = int.MinValue;
                try { if (c.myStats != null) keepHp = c.myStats.currentHealth; } catch { }
                try
                {
                    if (agent != null)
                    {
                        agent.enabled = true;
                        agent.Warp(c.transform.position);
                    }
                    // OnEnable restarts the AI coroutines.
                    c.enabled = true;
                    var det = c.myDetector;
                    if (det != null) { det.enabled = true; det.ResetDetectionLists(); }
                }
                catch (Exception e) { Log.Debug("enemy resume: " + e.Message); }
                if (keepHp != int.MinValue)
                {
                    try
                    {
                        if (c.myStats != null && c.myStats.currentHealth != keepHp)
                            LoopGuard.Apply(() => Priv.SetProp(c.myStats, "currentHealth", keepHp));
                    }
                    catch (Exception e) { Log.Debug("enemy resume hp: " + e.Message); }
                }
            }
            else
            {
                _puppetMode.Add(id);
                // A puppet must not think: no navigation (it would fight the replicated
                // transform), and no AI. AIUpdate/SlowUpdate are coroutines that survive a
                // disabled component, so they are stopped explicitly; otherwise the puppet
                // keeps attacking the local player.
                try
                {
                    if (agent != null) agent.enabled = false;
                    c.StopAllCoroutines();
                    c.enabled = false;
                    var det = c.myDetector;
                    if (det != null) { det.StopAllCoroutines(); det.enabled = false; }
                }
                catch (Exception e) { Log.Debug("enemy puppet: " + e.Message); }
            }
        }

        /// <summary>
        /// Packs enemy states into datagrams by size. Keys are hierarchy paths (~120 B), so
        /// a fixed count per packet overflowed the 1200 B datagram.
        /// </summary>
        private static void FlushBatch(List<(string key, global::EnemyController c, byte owner)> batch)
        {
            var entries = new List<byte[]>(batch.Count);
            foreach (var (key, c, owner) in batch)
            {
                var e = new Writer();
                e.Str(key);
                e.U8(owner);
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
            float now = Time.realtimeSinceStartup;
            bool host = IsHost;

            for (int i = 0; i < n; i++)
            {
                string key = r.Str();
                byte owner = r.U8();
                Vector3 pos = r.Position();
                float yaw = r.Yaw();
                int hp = r.I32();
                var mode = (global::EnemyController.CombatModes)r.U8();
                var hostile = (global::EnemyController.HostileType)r.U8();
                bool running = r.Bool();
                bool provoked = r.Bool();

                var c = Resolve(key);
                if (c == null || _dead.Contains(c.GetInstanceID())) continue;
                int id = c.GetInstanceID();
                if (host || owner != OwnerClient) NoteHealth(key, hp);   // the simulating peer's value

                if (host)
                {
                    // The client streams only what it simulates. Remember it so the host
                    // keeps its copy as a puppet (also for enemies the host has not loaded).
                    _clientStateAt[id] = now;
                    if (!_owner.ContainsKey(id) || !c.gameObject.activeInHierarchy) _owner[id] = OwnerClient;
                    if (_owner[id] != OwnerClient) continue;   // host took it back: stale state
                    SetSimulated(c, false);
                }
                else
                {
                    if (owner == OwnerClient) { _hostOwnedAt.Remove(id); continue; }   // ours to simulate
                    _hostOwnedAt[id] = now;
                    SetSimulated(c, false);
                }

                var p = PuppetFor(c);
                p.TargetPos = pos;
                p.TargetYaw = yaw;
                p.LastUpdate = now;

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
            p = new Puppet { Controller = c, TargetPos = c.transform.position, TargetYaw = c.transform.eulerAngles.y };
            _puppets[id] = p;
            return p;
        }

        // ---- caches: the enemy list changes only with region streaming/spawning, and an
        // enemy's key never changes while it lives. Both were rebuilt 10x per second.
        private static global::EnemyController[] _enemyCache;
        private static float _enemyCacheAt = -10f;
        private static readonly Dictionary<int, string> _keyCache = new Dictionary<int, string>(512);
        private static float _keyCacheClearedAt;

        private static global::EnemyController[] CachedEnemies()
        {
            float now = Time.realtimeSinceStartup;
            if (_enemyCache == null || now - _enemyCacheAt > 1f)
            {
                _enemyCacheAt = now;
                var all = Resources.FindObjectsOfTypeAll<global::EnemyController>();
                var list = new List<global::EnemyController>(all != null ? all.Length : 0);
                if (all != null)
                    foreach (var e in all)
                        if (e != null && e.gameObject.scene.IsValid()) list.Add(e);   // skip prefab assets
                _enemyCache = list.ToArray();
            }
            return _enemyCache;
        }

        private static string CachedKey(ObjectRegistry reg, global::EnemyController e)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _keyCacheClearedAt > 30f) { _keyCache.Clear(); _keyCacheClearedAt = now; }
            int id = e.GetInstanceID();
            if (_keyCache.TryGetValue(id, out var k)) return k;
            k = KeyFor(reg, e);
            _keyCache[id] = k;
            return k;
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

namespace DelusionMP.Sync
{
    // An encounter zone switches its spawners when the *local* player enters or leaves.
    // That is streaming, not a world change: keep it off the mirror (see WorldMirror.LocalOnly).
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyCombatEncounter), "ToggleEnemies")]
    internal static class Patch_EnemyCombatEncounter_ToggleEnemies
    {
        [HarmonyLib.HarmonyPrefix]
        static void Prefix(global::EnemyCombatEncounter __instance)
        {
            try
            {
                var arr = __instance.enemiesToSpawn;
                if (arr == null) return;
                foreach (var sp in arr)
                {
                    if (sp == null) continue;
                    WorldMirror.LocalOnly(sp.GetComponent<global::SaveableObject>());
                }
            }
            catch { }
        }
    }

    // Trigger messages still reach a disabled detector; a puppet must not detect anyone.
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyDetector), "ProcessCollider")]
    internal static class Patch_EnemyDetector_ProcessCollider
    {
        [HarmonyLib.HarmonyPrefix]
        static bool Prefix(global::EnemyDetector __instance, ref bool __result)
        {
            global::EnemyController c = null;
            try { c = __instance.GetComponentInParent<global::EnemyController>(); } catch { }
            if (c == null || !EnemySync.IsPuppet(c)) return true;
            __result = false;
            return false;
        }
    }
}
