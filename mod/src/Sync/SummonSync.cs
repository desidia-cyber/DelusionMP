using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// The summon spells (skeleton, archer, shield, caster, spider).
    ///
    /// DelusionPlayerSpell.SpellSummon instantiates a prefab ("summon_Skeleton(Clone)", an
    /// EnemySpawner holding an EnemyController) at the scene root and keeps it in
    /// summonSpawners. A summon lives ~15 s (SummonSelfDamage drains it) and fights for the
    /// local player. It has no save identity: its key is just the prefab name, the same on
    /// both machines, so before this the enemy stream mixed up the two players' summons and
    /// the partner never saw them.
    ///
    /// Summons stay the caster's alone: they never enter the enemy stream. The caster streams
    /// their pose and health (5 Hz) and the partner shows a ghost - the same prefab with the
    /// AI, spawner, self-drain, HUD and colliders stripped, so nothing on the partner's
    /// machine can fight it or count it. A summon's hits on an enemy the partner simulates
    /// are forwarded like the caster's own (EnemySync), so the threat brings that enemy to
    /// the caster's machine, where the summon really fights it.
    /// </summary>
    public static class SummonSync
    {
        private const float SendInterval = 0.2f;
        private const float GhostStale = 1.0f;
        private const string GhostPrefix = "DelusionMP_Summon_";
        private static float _nextSend;
        private static int _sent, _recv, _ghostsMade, _animSent, _animApplied;

        private sealed class Ghost
        {
            public GameObject Root;
            public Transform Body;           // the EnemyController's object (moves inside the root)
            public global::EnemyStats Stats;
            public global::EnemyAnimator Anim;
            public Vector3 Pos;
            public Vector3 LastPos;
            public float Yaw;
            public float Speed;
            public byte CombatMode;
            public float At;
        }

        private static readonly Dictionary<int, Ghost> _ghosts = new Dictionary<int, Ghost>();

        public static string Stats => $"sent={_sent} recv={_recv} ghosts={_ghosts.Count} made={_ghostsMade} local={LocalCount()} animSent={_animSent} animApplied={_animApplied}";

        // ------------------------------------------------------------ identity

        /// <summary>True for a summon (ours or a ghost of the partner's): never an enemy to sync.</summary>
        public static bool IsSummon(global::EnemyController c)
        {
            if (c == null) return false;
            try
            {
                var n = c.transform.root.gameObject.name;
                return n.StartsWith("summon_", StringComparison.Ordinal) || n.StartsWith(GhostPrefix, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>True for one of this machine's own live summons.</summary>
        public static bool IsLocalSummon(global::EnemyController c)
        {
            if (c == null) return false;
            try { return c.transform.root.gameObject.name.StartsWith("summon_", StringComparison.Ordinal); }
            catch { return false; }
        }

        private static int LocalCount()
        {
            int n = 0;
            try
            {
                var list = global::DelusionPlayerSpell.Instance?.summonSpawners;
                if (list != null) for (int i = 0; i < list.Count; i++) if (Alive(list[i], out _)) n++;
            }
            catch { }
            return n;
        }

        private static bool Alive(global::EnemySpawner es, out global::EnemyController ec)
        {
            ec = null;
            if (es == null) return false;
            ec = es.enemyController;
            if (ec == null || !ec.gameObject.activeInHierarchy) return false;
            try { return ec.myStats == null || ec.myStats.currentHealth > 0; } catch { return false; }
        }

        // ------------------------------------------------------------ tick

        public static void Tick()
        {
            var s = Session.Instance;
            if (s == null || !s.Connected) { if (_ghosts.Count > 0) ClearGhosts(); return; }
            float now = Time.realtimeSinceStartup;

            // Ghosts glide toward the caster's stream; a summon that stopped arriving is gone.
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            List<int> drop = null;
            foreach (var kv in _ghosts)
            {
                var g = kv.Value;
                if (g.Root == null || now - g.At > GhostStale) { (drop ??= new List<int>()).Add(kv.Key); continue; }
                var t = g.Body != null ? g.Body : g.Root.transform;
                var prevPos = t.position;
                var nextPos = Vector3.Lerp(prevPos, g.Pos, 0.35f);
                t.position = nextPos;
                var ea = t.eulerAngles;
                t.rotation = Quaternion.Euler(ea.x, Mathf.LerpAngle(ea.y, g.Yaw, 0.35f), ea.z);
                float horiz = new Vector2(nextPos.x - prevPos.x, nextPos.z - prevPos.z).magnitude / dt;
                g.Speed = Mathf.Lerp(g.Speed, horiz, 0.25f);
                g.LastPos = nextPos;
                if (g.Anim != null)
                {
                    try
                    {
                        var a = g.Anim.animator;
                        if (a != null)
                        {
                            float pct = g.Speed < 0.15f ? 0f : (g.Speed > 3.5f ? 1f : Mathf.Clamp(g.Speed / 4.5f, 0.25f, 0.7f));
                            a.SetFloat("speedPercent", pct, 0.1f, dt);
                        }
                    }
                    catch { }
                }
            }
            if (drop != null) foreach (var id in drop) DestroyGhost(id);

            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            if (!GameRefs.InWorld) return;   // menus, credits: no spell manager to ask

            global::Il2CppSystem.Collections.Generic.List<global::EnemySpawner> list = null;
            try { list = global::DelusionPlayerSpell.Instance?.summonSpawners; } catch { }
            var entries = new List<byte[]>();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var es = list[i];
                    if (!Alive(es, out var ec)) continue;
                    var e = new Writer();
                    e.I32(es.GetInstanceID());
                    e.Str(PrefabName(es.gameObject.name));
                    e.Position(ec.transform.position);
                    e.Yaw(ec.transform.eulerAngles.y);
                    int hp = 0; try { hp = ec.myStats != null ? ec.myStats.currentHealth : 0; } catch { }
                    e.I32(hp);
                    byte cm = 0; try { cm = (byte)(int)ec.combatMode; } catch { }
                    e.U8(cm);
                    entries.Add(e.ToArray());
                }
            // An empty list is sent too, so the partner's ghosts go at once when ours die.
            if (entries.Count == 0 && _sentEmpty) return;
            _sentEmpty = entries.Count == 0;
            var w = new Writer();
            w.U8((byte)Math.Min(entries.Count, 32));
            for (int i = 0; i < entries.Count && i < 32; i++) w.Bytes(entries[i]);
            s.SendUnreliable(PacketKind.SummonState, w.ToArray());
            _sent++;
        }
        private static bool _sentEmpty;

        private static string PrefabName(string n)
        {
            int k = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return (k >= 0 ? n.Substring(0, k) : n).Trim();
        }

        // ------------------------------------------------------------ receive

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            int n = r.U8();
            float now = Time.realtimeSinceStartup;
            _recv++;
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int id = r.I32();
                string prefab = r.Str();
                Vector3 pos = r.Position();
                float yaw = r.Yaw();
                int hp = r.I32();
                byte cm = r.Remaining > 0 ? r.U8() : (byte)0;
                seen.Add(id);
                if (!_ghosts.TryGetValue(id, out var g) || g.Root == null)
                {
                    g = MakeGhost(prefab, pos, yaw);
                    if (g == null) continue;
                    _ghosts[id] = g;
                }
                g.Pos = pos; g.Yaw = yaw; g.At = now;
                if (g.CombatMode != cm)
                {
                    g.CombatMode = cm;
                    if (g.Anim != null)
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                if (cm == (byte)(int)global::EnemyController.CombatModes.InCombat) g.Anim.WeaponOut();
                                else g.Anim.WeaponAway();
                            }
                            catch { }
                        });
                }
                if (g.Stats != null)
                    LoopGuard.Apply(() => { try { Priv.SetProp(g.Stats, "currentHealth", Math.Max(1, hp)); } catch { } });
            }
            List<int> gone = null;
            foreach (var id in _ghosts.Keys) if (!seen.Contains(id)) (gone ??= new List<int>()).Add(id);
            if (gone != null) foreach (var id in gone) DestroyGhost(id);
        }

        private static Ghost MakeGhost(string prefabName, Vector3 pos, float yaw)
        {
            GameObject prefab = null;
            try
            {
                var refs = global::DelusionPlayerSpell.Instance?.summonReferences;
                if (refs != null)
                    foreach (var rf in refs)
                        if (rf != null && rf.summonPrefab != null && rf.summonPrefab.name == prefabName) { prefab = rf.summonPrefab; break; }
            }
            catch { }
            if (prefab == null) { Log.Warn("summon ghost: no prefab " + prefabName); return null; }

            GameObject root = null;
            bool wasActive = prefab.activeSelf;
            try
            {
                // Built inactive so no game script wakes up before it is stripped.
                prefab.SetActive(false);
                root = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, yaw, 0f));
            }
            catch (Exception e) { Log.Warn("summon ghost: " + e.Message); }
            finally { try { prefab.SetActive(wasActive); } catch { } }
            if (root == null) return null;
            root.name = GhostPrefix + prefabName;

            global::EnemyController ec = null;
            global::EnemyStats stats = null;
            try
            {
                foreach (var c in root.GetComponentsInChildren<global::SummonSelfDamage>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in root.GetComponentsInChildren<global::SummonMonitor>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in root.GetComponentsInChildren<global::EnemySpawner>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in root.GetComponentsInChildren<global::SaveableObject>(true)) UnityEngine.Object.DestroyImmediate(c);
                // SkeletonFacts writes the summoner's kill/death tallies; QuestObject switches
                // objects by story facts. Neither belongs to a ghost.
                foreach (var c in root.GetComponentsInChildren<global::SkeletonFacts>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in root.GetComponentsInChildren<global::QuestObject>(true)) UnityEngine.Object.DestroyImmediate(c);
                // Its talk point and buff aura stay off (the spawner/controller set them up).
                foreach (var c in root.GetComponentsInChildren<global::NPCBase>(true)) c.gameObject.SetActive(false);
                foreach (var c in root.GetComponentsInChildren<global::SpellEffect>(true)) c.gameObject.SetActive(false);
                foreach (var c in root.GetComponentsInChildren<Rigidbody>(true)) { c.isKinematic = true; c.useGravity = false; }
                foreach (var c in root.GetComponentsInChildren<global::EnemyDetector>(true)) c.enabled = false;
                foreach (var c in root.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true)) c.enabled = false;
                // Nothing here may touch it: no hits, no detection, no pushing the player.
                foreach (var c in root.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                ec = root.GetComponentInChildren<global::EnemyController>(true);
                if (ec != null)
                {
                    ec.enabled = false;
                    stats = ec.myStats;
                }
                if (stats == null) stats = root.GetComponentInChildren<global::EnemyStats>(true);
                root.SetActive(true);
                // The spawner normally switches its enemy on; the ghost has no spawner.
                if (ec != null && !ec.gameObject.activeSelf) ec.gameObject.SetActive(true);
                if (ec != null) { ec.StopAllCoroutines(); ec.enabled = false; }
                foreach (var c in root.GetComponentsInChildren<global::EnemyDetector>(true)) { c.StopAllCoroutines(); c.enabled = false; }
                // The controller normally wakes its animator (equipment, layer weights, the
                // idle/walk blend). Without it the model holds the rig's default pose.
                var anim = root.GetComponentInChildren<global::EnemyAnimator>(true);
                if (anim != null)
                {
                    anim.enabled = true;
                    try { anim.Initialize(); } catch (Exception e) { Log.Debug("ghost anim init: " + e.Message); }
                    try { anim.Wakeup(); } catch (Exception e) { Log.Debug("ghost anim wakeup: " + e.Message); }
                }
            }
            catch (Exception e) { Log.Warn("summon ghost strip: " + e.Message); }

            _ghostsMade++;
            Log.Info($"summon ghost: partner's {prefabName} shown at {pos.x:F1},{pos.y:F1},{pos.z:F1}");
            return new Ghost
            {
                Root = root,
                Body = ec != null ? ec.transform : root.transform,
                Stats = stats,
                Anim = root.GetComponentInChildren<global::EnemyAnimator>(true),
                Pos = pos,
                LastPos = pos,
                Yaw = yaw,
                At = Time.realtimeSinceStartup,
            };
        }

        // ------------------------------------------------------------ summon combat animations

        /// <summary>Called by EnemySync's EnemyAnimator hooks when a local summon plays a combat/action animation.</summary>
        internal static void OnLocalSummonAnim(global::EnemyController c, byte kind, byte arg)
        {
            if (c == null || LoopGuard.ApplyingRemote) return;
            var s = Session.Instance;
            if (s == null || !s.Connected) return;
            int spawnerId = 0;
            try
            {
                var sp = c.mySpawner ?? c.GetComponentInParent<global::EnemySpawner>();
                if (sp != null) spawnerId = sp.GetInstanceID();
            }
            catch { }
            if (spawnerId == 0) return;
            var w = new Writer();
            w.I32(spawnerId).U8(kind).U8(arg);
            s.SendReliable(PacketKind.SummonAnim, w.ToArray());
            _animSent++;
        }

        public static void ApplyAnim(byte[] payload)
        {
            if (payload == null || payload.Length < 6) return;
            var r = new Reader(payload);
            int id = r.I32();
            var animKind = (EnemySync.EnemyAnimKind)r.U8();
            byte arg = r.U8();
            if (!_ghosts.TryGetValue(id, out var g) || g.Anim == null) return;
            EnemySync.ReplayAnim(g.Anim, animKind, arg);
            _animApplied++;
        }

        private static void DestroyGhost(int id)
        {
            if (!_ghosts.TryGetValue(id, out var g)) return;
            _ghosts.Remove(id);
            try { if (g.Root != null) UnityEngine.Object.Destroy(g.Root); } catch { }
        }

        public static void ClearGhosts()
        {
            foreach (var id in new List<int>(_ghosts.Keys)) DestroyGhost(id);
        }

        public static Vector3? FirstGhostPosition()
        {
            foreach (var g in _ghosts.Values)
                if (g.Root != null) return (g.Body != null ? g.Body : g.Root.transform).position;
            return null;
        }

        /// <summary>Rig: the ghosts this machine shows.</summary>
        public static IEnumerable<string> Describe()
        {
            foreach (var kv in _ghosts)
            {
                var g = kv.Value;
                if (g.Root == null) { yield return $"{kv.Key} destroyed"; continue; }
                var p = (g.Body != null ? g.Body : g.Root.transform).position;
                int hp = -1; try { hp = g.Stats != null ? g.Stats.currentHealth : -1; } catch { }
                bool vis = false; string bounds = "-";
                try
                {
                    foreach (var rr in g.Root.GetComponentsInChildren<Renderer>(false))
                        if (rr.enabled) { vis = true; var b = rr.bounds; bounds = $"{b.center.x:F1},{b.center.y:F1},{b.center.z:F1}~{b.size.y:F1}"; break; }
                }
                catch { }
                bool animOn = false; try { var a = g.Root.GetComponentInChildren<global::EnemyAnimator>(true); animOn = a != null && a.enabled; } catch { }
                yield return $"{kv.Key} {g.Root.name} pos={p.x:F1},{p.y:F1},{p.z:F1} target={g.Pos.x:F1},{g.Pos.y:F1},{g.Pos.z:F1} hp={hp} visible={vis} bounds={bounds} anim={animOn} age={Time.realtimeSinceStartup - g.At:F2}s";
            }
        }
    }
}
