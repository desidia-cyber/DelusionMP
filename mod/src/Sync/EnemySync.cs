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
                    LoopGuard.Apply(() => st.currentHealth = w.hp);
                    Log.Info($"enemy {e.gameObject.name}: back in play at {was} hp, kept the fight's {w.hp}");
                }
            }
            catch { }
            return true;
        }

        internal static void ForgetReturn(string key) => _returning.Remove(key);

        private sealed class Puppet
        {
            public bool InCombat;   // the simulating peer's enemy is fighting
            public global::EnemyController Controller;
            public Vector3 TargetPos;
            public Vector3 LastPos;
            public float TargetYaw;
            public float Speed;
            public global::EnemyController.CombatModes CombatMode;
            public float LastUpdate;
        }

        private static readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>(256);

        public static void Init()
        {
            _puppets.Clear(); _dead.Clear(); _index.Clear();
            _owner.Clear(); _hostOwnedAt.Clear(); _clientStateAt.Clear(); _puppetMode.Clear();
            _charmPin.Clear(); _charmedHere.Clear();
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
                    try { if (e.gameObject.scene.IsValid() && !SummonSync.IsSummon(e)) _index[CachedKey(reg, e)] = e; } catch { }
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
        private const byte CombatThreat = 2;   // client -> host: the client hit an enemy it simulates
        private const byte CombatAway = 3;     // client -> host: the client entered/left a menu
        private const byte CombatCharm = 4;    // both: this machine's player charmed an enemy (timer, buff)
        private const byte CombatUncharm = 5;  // both: that charm ended
        private const byte CombatAnim = 6;     // simulating peer -> puppet: attack/spell/bow/stagger/block anim

        internal enum EnemyAnimKind : byte
        {
            Attack1 = 0, Attack2 = 1, AttackStrong = 2, Taunt = 3,
            ReadyBow = 4, FireBow = 5, ReadySpell = 6, FireSpell = 7,
            CancelRanged = 8, DrinkPotion = 9, Damage = 10, Stagger = 11,
            GuardBreak = 12, Parry = 13, Block = 14, WeaponOut = 15, WeaponAway = 16,
        }

        internal static void OnAnimEvent(global::EnemyAnimator anim, EnemyAnimKind kind, byte arg = 0)
        {
            if (anim == null || LoopGuard.ApplyingRemote) return;
            var s = Session.Instance;
            if (s == null || !s.Connected) return;
            global::EnemyController c = null;
            try { c = anim.controller ?? anim.GetComponent<global::EnemyController>() ?? anim.GetComponentInParent<global::EnemyController>(); } catch { }
            if (c == null || SummonSync.IsSummon(c) || IsPuppet(c)) return;
            var key = CachedKey(ObjectRegistry.Instance, c);
            if (string.IsNullOrEmpty(key)) return;
            var w = new Writer();
            w.U8(CombatAnim).Str(key).U8((byte)kind).U8(arg);
            s.SendUnreliable(PacketKind.CombatEvent, w.ToArray());
        }

        internal static void DrivePuppetAnimator(global::EnemyAnimator anim)
        {
            if (anim == null) return;
            global::EnemyController c = null;
            try { c = anim.controller; } catch { }
            if (c == null || !IsPuppet(c)) return;
            if (!_puppets.TryGetValue(c.GetInstanceID(), out var pp)) return;
            try
            {
                var a = anim.animator;
                if (a == null) return;
                float pct = pp.Speed < 0.15f ? 0f : (c.isRunning || pp.Speed > 3.5f ? 1f : Mathf.Clamp(pp.Speed / 4.5f, 0.25f, 0.7f));
                a.SetFloat("speedPercent", pct, 0.1f, Time.deltaTime);
            }
            catch { }
        }

        // Host: the client is in a menu/panel/dialogue. Client: what it last reported.
        private static bool _peerAway, _sentAway;
        private static float _nextAwaySend;

        // True while a peer's hit is being applied here (it is the peer's threat, not ours).
        private static bool _applyingPeerHit;
        // When this machine's player last hit each enemy: an enemy taken over after our hits
        // must fight us (our hits landed on a puppet whose AI never noticed them).
        private static readonly Dictionary<int, float> _localHitAt = new Dictionary<int, float>(64);

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
            if (c == null || _applyingPeerHit || SummonSync.IsSummon(c)) return;
            _localHitAt[c.GetInstanceID()] = Time.realtimeSinceStartup;
            // Threat: the host decides who the enemy fights, from damage and closeness.
            if (IsHost) AddThreat(c.GetInstanceID(), client: false, damage);
            // The simulating peer owns health: hits on a local puppet go to it. The local
            // hit still lands for instant feedback; the owner's stream corrects it.
            if (!IsPuppet(c))
            {
                if (IsClient)
                {
                    // Our own simulated enemy: the host still needs the threat to keep it here.
                    var tw = new Writer();
                    tw.U8(CombatThreat).Str(KeyFor(ObjectRegistry.Instance, c)).I32(damage);
                    s.SendReliable(PacketKind.CombatEvent, tw.ToArray());
                }
                return;
            }
            var key = KeyFor(ObjectRegistry.Instance, c);
            var w = new Writer();
            // Charm power is not forwarded: the charm roll already ran here, on the local hit
            // (EnemyStats.ApplyCharm rolls a die, shows the note and fills this player's
            // charm slot). Its result follows as CombatCharm instead of a second roll there.
            w.U8(CombatDamage).Str(key).I32(damage).F32(piercing).I32(poison).I32(burn).I32(0).F32(slow).I32(stagger);
            s.SendReliable(PacketKind.CombatEvent, w.ToArray());
            _hitsForwarded++;
        }

        internal static void OnDeath(global::EnemyController c, bool killedByPlayer)
        {
            if (c == null || SummonSync.IsSummon(c)) return;
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
            if (kind == CombatAway)
            {
                bool away = r.Bool();
                if (away != _peerAway) Log.Info($"aggro: partner {(away ? "is in a menu" : "is back")}");
                _peerAway = away;
                return;
            }
            string key = r.Str();
            var c = Resolve(key);
            if (c == null) { Log.Warn("combat: unknown enemy " + key); return; }
            var go = c.gameObject;

            if (kind == CombatDamage)
            {
                int dmg = r.I32(); float pierce = r.F32(); int poison = r.I32(); int burn = r.I32();
                int charm = r.I32(); float slow = r.F32(); int stagger = r.I32();
                if (_dead.Contains(c.GetInstanceID())) return;
                if (IsHost) AddThreat(c.GetInstanceID(), client: true, dmg);
                var trig = go.GetComponentInChildren<global::EnemyDamageTrigger>(true);
                if (trig == null) return;
                // Outside LoopGuard: the resulting death must replicate normally.
                _applyingPeerHit = true;
                try
                {
                    trig.ApplyDamage(dmg, pierce, poison, burn, charm, slow, false, true, null, stagger);
                    _hitsApplied++;
                }
                catch (Exception e) { Log.Warn($"client hit on {key} failed: {e.Message}"); }
                finally { _applyingPeerHit = false; }
            }
            else if (kind == CombatCharm || kind == CombatUncharm)
            {
                int timer = kind == CombatCharm ? r.I32() : 0;
                int buff = kind == CombatCharm ? r.I32() : 0;
                ApplyCharmState(c, kind == CombatCharm, timer, buff);
            }
            else if (kind == CombatThreat)
            {
                int dmg = r.I32();
                if (IsHost) AddThreat(c.GetInstanceID(), client: true, dmg);
            }
            else if (kind == CombatAnim)
            {
                var animKind = (EnemyAnimKind)r.U8();
                byte arg = r.U8();
                if (!IsPuppet(c) || _dead.Contains(c.GetInstanceID())) return;
                var anim = c.myAnim;
                if (anim == null) return;
                LoopGuard.Apply(() =>
                {
                    try
                    {
                        switch (animKind)
                        {
                            case EnemyAnimKind.Attack1: anim.OnAttack(); break;
                            case EnemyAnimKind.Attack2: anim.OnAttackTwo(); break;
                            case EnemyAnimKind.AttackStrong: anim.OnAttackStrong(); break;
                            case EnemyAnimKind.Taunt: anim.OnAttackTaunt(); break;
                            case EnemyAnimKind.ReadyBow: anim.OnReadyBow(); break;
                            case EnemyAnimKind.FireBow: anim.OnFireBow(); break;
                            case EnemyAnimKind.ReadySpell: anim.OnReadySpell((global::EnemyCombat.SpellType)arg); break;
                            case EnemyAnimKind.FireSpell: anim.OnFireSpell(); break;
                            case EnemyAnimKind.CancelRanged: anim.OnCancelRanged(); break;
                            case EnemyAnimKind.DrinkPotion: anim.OnDrinkPotion(); break;
                            case EnemyAnimKind.Damage: anim.OnDamage(); break;
                            case EnemyAnimKind.Stagger: anim.Stagger(); break;
                            case EnemyAnimKind.GuardBreak: anim.GuardBreak(); break;
                            case EnemyAnimKind.Parry: anim.ActivateParry(); break;
                            case EnemyAnimKind.Block: anim.SetBlocking(arg != 0); break;
                            case EnemyAnimKind.WeaponOut: anim.WeaponOut(); break;
                            case EnemyAnimKind.WeaponAway: anim.WeaponAway(); break;
                        }
                    }
                    catch (Exception e) { Log.Debug($"puppet anim {animKind}: {e.Message}"); }
                });
            }
            else if (kind == CombatDeath)
            {
                bool byPlayer = r.Bool();
                if (_dead.Contains(c.GetInstanceID())) return;   // already died locally
                LoopGuard.Apply(() =>
                {
                    try
                    {
                        if (c.myStats != null) c.myStats.currentHealth = 0;
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

        // ------------------------------------------------------------------ charm
        //
        // EnemyStats.ApplyCharm (a hit with charm power) rolls on the hitter's machine; on
        // success GetCharmed sets isCharmed (a countdown) and maybe isBuffed, puts the enemy in
        // the local player's single charm slot (DelusionPlayerSpell.CharmEnemy, which frees the
        // previous one) and shows the note and the summon HUD. Only the state moves to the
        // partner: their copy gets the same countdown, nothing in their charm slot, no note.
        // The host pins the enemy to the charmer's machine while it lasts (DecideOwner).

        // host: who charmed each enemy (OwnerHost/OwnerClient)
        private static readonly Dictionary<int, byte> _charmPin = new Dictionary<int, byte>();
        // both: enemies this machine's player charmed (their end is sent from here)
        private static readonly HashSet<int> _charmedHere = new HashSet<int>();
        private static int _charmsSent, _charmsApplied;
        public static string CharmStats => $"charmsSent={_charmsSent} charmsApplied={_charmsApplied} pinned={_charmPin.Count} charmedHere={_charmedHere.Count}";

        private static global::EnemyController ControllerOf(global::EnemyStats st)
        {
            var c = st.GetComponent<global::EnemyController>();
            return c != null ? c : st.GetComponentInParent<global::EnemyController>();
        }

        internal static void OnLocalCharm(global::EnemyStats st)
        {
            if (st == null || LoopGuard.ApplyingRemote) return;
            var c = ControllerOf(st);
            if (c == null || SummonSync.IsSummon(c)) return;
            int id = c.GetInstanceID();
            _charmedHere.Add(id);
            if (IsHost) _charmPin[id] = OwnerHost;
            var s = Session.Instance;
            if (s == null || !s.Connected) return;
            int timer = 0, buff = 0;
            try { timer = st.isCharmed; buff = st.isBuffed; } catch { }
            var w = new Writer();
            w.U8(CombatCharm).Str(KeyFor(ObjectRegistry.Instance, c)).I32(timer).I32(buff);
            s.SendReliable(PacketKind.CombatEvent, w.ToArray());
            _charmsSent++;
            Log.Info($"charm: {c.gameObject.name} charmed here ({timer})");
        }

        internal static void OnLocalUncharm(global::EnemyStats st)
        {
            if (st == null || LoopGuard.ApplyingRemote) return;
            var c = ControllerOf(st);
            if (c == null) return;
            int id = c.GetInstanceID();
            if (!_charmedHere.Remove(id)) return;   // not ours: the charmer sends the end
            if (IsHost) _charmPin.Remove(id);
            var s = Session.Instance;
            if (s == null || !s.Connected) return;
            var w = new Writer();
            w.U8(CombatUncharm).Str(KeyFor(ObjectRegistry.Instance, c));
            s.SendReliable(PacketKind.CombatEvent, w.ToArray());
            Log.Info($"charm: {c.gameObject.name} charm ended here");
        }

        private static void ApplyCharmState(global::EnemyController c, bool charmed, int timer, int buff)
        {
            int id = c.GetInstanceID();
            var st = c.myStats;
            if (st == null) return;
            if (charmed)
            {
                // The partner's charm replaces ours on this enemy, if we had one.
                _charmedHere.Remove(id);
                if (IsHost) _charmPin[id] = OwnerClient;
                LoopGuard.Apply(() =>
                {
                    try
                    {
                        st.isCharmed = Math.Max(timer, 1);
                        if (buff > 0) st.isBuffed = buff;
                    }
                    catch (Exception e) { Log.Debug("charm apply: " + e.Message); }
                });
                _charmsApplied++;
                Log.Info($"charm: {c.gameObject.name} charmed by the partner ({timer})");
            }
            else
            {
                if (IsHost) _charmPin.Remove(id);
                if (_charmedHere.Contains(id)) return;   // we charmed it since: ours stands
                LoopGuard.Apply(() =>
                {
                    try { if (st.isCharmed > 0) { st.isCharmed = 0; st.RecoverCharmed(); } }
                    catch (Exception e) { Log.Debug("uncharm apply: " + e.Message); }
                });
                Log.Info($"charm: {c.gameObject.name} charm ended on the partner's side");
            }
        }

        /// <summary>True when an attacker fights on this machine's player's side: one of our
        /// summons, or an enemy we simulate that is charmed. Its hits count as ours.</summary>
        internal static bool IsLocalAlly(global::EnemyController a)
        {
            if (a == null) return false;
            if (SummonSync.IsLocalSummon(a)) return true;
            if (SummonSync.IsSummon(a) || IsPuppet(a)) return false;
            try { return a.IsCharmed(); } catch { return false; }
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

        // ---- aggro: who the enemy fights.
        //
        // The machine that simulates an enemy runs the game's own AI, which fights that
        // machine's player. So "who owns it" is "who it is angry at". The host scores both
        // players per enemy: damage dealt (x3, decaying, half-life 8 s) plus a closeness bonus
        // inside 15 m (up to 15). Control moves when the other player's score clearly wins, at most
        // every 2 s, so a ranged attacker pulls the enemy off the partner and a player who
        // walks up to it takes it over - without flip-flopping mid-swing.
        private sealed class Threat { public float Host, Client, At, LastSwitch = -10f; }
        private static readonly Dictionary<int, Threat> _threat = new Dictionary<int, Threat>(128);
        private const float ThreatHalfLife = 8f, CloseRange = 15f, CloseWeight = 1f, SwitchHold = 2f, ThreatPerDamage = 3f;

        private static Threat ThreatOf(int id)
        {
            if (!_threat.TryGetValue(id, out var t)) { t = new Threat { At = Time.realtimeSinceStartup }; _threat[id] = t; }
            float now = Time.realtimeSinceStartup, dt = now - t.At;
            if (dt > 0f)
            {
                float k = Mathf.Pow(0.5f, dt / ThreatHalfLife);
                t.Host *= k; t.Client *= k; t.At = now;
            }
            return t;
        }

        internal static void AddThreat(int id, bool client, float amount)
        {
            if (amount <= 0f) return;
            var t = ThreatOf(id);
            if (client) t.Client += amount * ThreatPerDamage; else t.Host += amount * ThreatPerDamage;
        }

        public static string ThreatInfo(global::EnemyController c)
        {
            if (c == null || !_threat.TryGetValue(c.GetInstanceID(), out var t)) return "";
            return $" threat(host={t.Host:F0} client={t.Client:F0})";
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
                var t = ThreatOf(id);
                float sh = t.Host + Mathf.Max(0f, CloseRange - dh) * CloseWeight;
                float sc = t.Client + Mathf.Max(0f, CloseRange - dc) * CloseWeight;
                float now = Time.realtimeSinceStartup;
                bool canSwitch = now - t.LastSwitch >= SwitchHold;
                byte want = cur;
                if (sh <= 0.5f && sc <= 0.5f)
                {
                    // Nobody near or fighting it: the nearer player simulates (streaming/LOD).
                    if (cur == OwnerHost && dc + Hysteresis < dh) want = OwnerClient;
                    else if (cur == OwnerClient && dh + Hysteresis < dc) want = OwnerHost;
                }
                else if (cur == OwnerHost && sc > sh * 1.25f + 5f) want = OwnerClient;
                else if (cur == OwnerClient && sh > sc * 1.25f + 5f) want = OwnerHost;
                // A player in a menu (time slowed or stopped on their machine) hands their
                // fights to a partner who is playing, at once; it comes back by score later.
                bool hostAway = AwayState.LocalAway, clientAway = _peerAway;
                bool forced = false;
                if (hostAway && !clientAway && dc < 60f) { want = OwnerClient; forced = cur != OwnerClient; }
                else if (clientAway && !hostAway) { want = OwnerHost; forced = cur != OwnerHost; }
                // A charmed enemy follows and fights for the player who charmed it, so it is
                // simulated on that player's machine for as long as the charm lasts.
                if (_charmPin.TryGetValue(id, out var pin))
                {
                    int left = 0; try { left = e.myStats != null ? e.myStats.isCharmed : 0; } catch { }
                    if (left > 0) { forced = cur != pin; want = pin; }
                    else _charmPin.Remove(id);
                }
                if (want != cur && (canSwitch || forced))
                {
                    t.LastSwitch = now;
                    if (sh > 0.5f || sc > 0.5f)
                        Log.Info($"aggro: {e.gameObject.name} -> {(want == OwnerClient ? "client" : "host")} (score host={sh:F0} client={sc:F0})");
                    cur = want;
                }
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

            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            foreach (var pp in _puppets.Values)
            {
                if (pp.Controller == null) continue;
                if (now - pp.LastUpdate > 0.5f) { pp.Speed = Mathf.Lerp(pp.Speed, 0f, 0.25f); continue; }   // owner went quiet: freeze, don't extrapolate
                var t = pp.Controller.transform;
                var prevPos = t.position;
                var nextPos = Vector3.Lerp(prevPos, pp.TargetPos, 0.35f);
                t.position = nextPos;
                var ea = t.eulerAngles;
                t.rotation = Quaternion.Euler(ea.x, Mathf.LerpAngle(ea.y, pp.TargetYaw, 0.35f), ea.z);
                float horiz = new Vector2(nextPos.x - prevPos.x, nextPos.z - prevPos.z).magnitude / dt;
                pp.Speed = Mathf.Lerp(pp.Speed, horiz, 0.25f);
                pp.LastPos = nextPos;
            }

            if (now < _nextState) return;
            _nextState = now + StateInterval;

            if (IsClient)
            {
                bool away = AwayState.LocalAway;
                if (away != _sentAway || now >= _nextAwaySend)   // repeated: survives reconnects
                {
                    _sentAway = away;
                    _nextAwaySend = now + 5f;
                    var aw = new Writer();
                    aw.U8(CombatAway).Bool(away);
                    s.SendReliable(PacketKind.CombatEvent, aw.ToArray());
                }
            }

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
                bool wasFighting = _puppets.TryGetValue(id, out var was) && was.InCombat;
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
                    // Taken over mid-fight: turn on this machine's player at once instead of
                    // idling until it happens to see them.
                    bool provoked = false;
                    try { provoked = c.hasBeenProvoked; } catch { }
                    bool hitByUs = _localHitAt.TryGetValue(id, out var hitAt) && Time.realtimeSinceStartup - hitAt < 15f;
                    if ((wasFighting || provoked || hitByUs) && c.hostileType != global::EnemyController.HostileType.CannotFight)
                        LoopGuard.Apply(() =>
                        {
                            try { if (c.player != null) c.targetEnemyObj = c.player; } catch { }
                            c.Provoke();
                            // Provoked alone it keeps patrolling; combat mode makes it come at us.
                            c.CombatMode(global::EnemyController.CombatModes.InCombat);
                        });
                }
                catch (Exception e) { Log.Debug("enemy resume: " + e.Message); }
                if (keepHp != int.MinValue)
                {
                    try
                    {
                        if (c.myStats != null && c.myStats.currentHealth != keepHp)
                            LoopGuard.Apply(() => c.myStats.currentHealth = keepHp);
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
                p.InCombat = mode == global::EnemyController.CombatModes.InCombat && hostile == global::EnemyController.HostileType.Aggressive;

                LoopGuard.Apply(() =>
                {
                    try
                    {
                        c.hostileType = hostile;
                        c.isRunning = running;
                        c.hasBeenProvoked = provoked;
                        if (p.CombatMode != mode)
                        {
                            p.CombatMode = mode;
                            c.combatMode = mode;
                            if (c.myAnim != null)
                            {
                                if (mode == global::EnemyController.CombatModes.InCombat) c.myAnim.WeaponOut();
                                else c.myAnim.WeaponAway();
                            }
                        }
                        if (c.myStats != null) c.myStats.currentHealth = hp > 0 ? hp : 1;
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
                        // Prefab assets are skipped, and so are summons: they are the caster's
                        // alone (SummonSync), and their keys are the same on both machines.
                        if (e != null && e.gameObject.scene.IsValid() && !SummonSync.IsSummon(e)) list.Add(e);
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
            // Spawned enemies carry no SaveableObject of their own and their scene sibling index
            // depends on runtime spawn order; keying by their static EnemySpawner guarantees a
            // stable 1-to-1 identity across both machines even when multiple identical enemies
            // are nearby.
            var ec = c as global::EnemyController;
            if (ec != null)
            {
                try
                {
                    var sp = ec.mySpawner;
                    if (sp != null)
                    {
                        var spSave = sp.GetComponent<global::SaveableObject>();
                        if (spSave != null)
                        {
                            var sid = spSave.GetUniqueIdentifier();
                            if (!string.IsNullOrEmpty(sid)) return reg.CurrentScene + "|spawner:" + sid;
                        }
                        return reg.CurrentScene + "|#spawner/" + ObjectRegistry.HierarchyPath(sp.transform);
                    }
                }
                catch { }
            }
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
                           int burnTimer, int charmPower, float slowMultiplier, bool isPlayer,
                           global::EnemyController agressorController, int staggerLevel)
        {
            if (isPlayer || EnemySync.IsLocalAlly(agressorController))
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
                           int charmPower, float slowMultiplier, bool isPlayer,
                           global::EnemyController agressorController, int staggerLevel)
        {
            if (isPlayer || EnemySync.IsLocalAlly(agressorController))
                EnemySync.OnPlayerHit(__instance, damage, 0f, poisonTimer, burnTimer, charmPower, slowMultiplier, staggerLevel);
        }
    }

    // The charm roll succeeded on this machine (see EnemySync, charm).
    [HarmonyPatch(typeof(global::EnemyStats), "GetCharmed")]
    internal static class Patch_EnemyStats_GetCharmed
    {
        [HarmonyPostfix]
        static void Postfix(global::EnemyStats __instance) => EnemySync.OnLocalCharm(__instance);
    }

    [HarmonyPatch(typeof(global::EnemyStats), nameof(global::EnemyStats.RecoverCharmed))]
    internal static class Patch_EnemyStats_RecoverCharmed
    {
        [HarmonyPostfix]
        static void Postfix(global::EnemyStats __instance) => EnemySync.OnLocalUncharm(__instance);
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

    // Drive puppet locomotion (speedPercent) after EnemyAnimator.Update reads zero agent velocity.
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), "Update")]
    internal static class Patch_EnemyAnimator_Update
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix(global::EnemyAnimator __instance) => EnemySync.DrivePuppetAnimator(__instance);
    }

    // Replicate combat/action animations from the simulating peer to the puppet.
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnAttack))]
    internal static class Patch_EnemyAnim_Attack1 { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.Attack1); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnAttackTwo))]
    internal static class Patch_EnemyAnim_Attack2 { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.Attack2); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnAttackStrong))]
    internal static class Patch_EnemyAnim_AttackStrong { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.AttackStrong); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnAttackTaunt))]
    internal static class Patch_EnemyAnim_Taunt { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.Taunt); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnReadyBow))]
    internal static class Patch_EnemyAnim_ReadyBow { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.ReadyBow); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnFireBow))]
    internal static class Patch_EnemyAnim_FireBow { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.FireBow); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnReadySpell))]
    internal static class Patch_EnemyAnim_ReadySpell { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance, global::EnemyCombat.SpellType spellType) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.ReadySpell, (byte)(int)spellType); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnFireSpell))]
    internal static class Patch_EnemyAnim_FireSpell { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.FireSpell); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnCancelRanged))]
    internal static class Patch_EnemyAnim_CancelRanged { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.CancelRanged); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnDrinkPotion))]
    internal static class Patch_EnemyAnim_DrinkPotion { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.DrinkPotion); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.OnDamage))]
    internal static class Patch_EnemyAnim_Damage { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.Damage); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), "Stagger")]
    internal static class Patch_EnemyAnim_Stagger { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.Stagger); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.GuardBreak))]
    internal static class Patch_EnemyAnim_GuardBreak { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.GuardBreak); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), "ActivateParry")]
    internal static class Patch_EnemyAnim_Parry { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.Parry); }
    [HarmonyLib.HarmonyPatch(typeof(global::EnemyAnimator), nameof(global::EnemyAnimator.SetBlocking))]
    internal static class Patch_EnemyAnim_Block { [HarmonyLib.HarmonyPostfix] static void Postfix(global::EnemyAnimator __instance, bool value) => EnemySync.OnAnimEvent(__instance, EnemySync.EnemyAnimKind.Block, value ? (byte)1 : (byte)0); }
}
