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
    /// One-shot world events that the save-state mirror cannot carry, because the objects
    /// involved are not saveable, are per player, or need the transition (not just the end
    /// state) to look right on the partner's screen:
    ///
    ///   * CharWorldState (story phase) - per player in the game, monotonic max here;
    ///   * rest / sleep - the partner's clock jumps with DayPassed/OnRest firing, but only
    ///     the sleeper restores stats;
    ///   * TriggerOnPlayerEnter - objects on/off, animators, SendMessage, run once per world;
    ///   * story travel (Underlands, Cradle, NPC teleports) - both players go;
    ///   * MovingFactory phase, CompletableObject slab, Trap disarm, smashable crate visuals;
    ///   * shop stock - pushed on change, deferred while the shop is open, host-arbitrated
    ///     purchase of unique items;
    ///   * EnemySpawner respawns - host owned.
    ///
    /// All travel on PacketKind.WorldAction with a sub-message byte. Either peer sends; the
    /// receiver applies with the game's own methods under LoopGuard.
    /// </summary>
    public static class WorldEventsSync
    {
        private const byte MsgWorldState = 0, MsgRest = 1, MsgTriggerOn = 2, MsgTriggerOff = 3,
            MsgTravel = 4, MsgFactory = 5, MsgCompletable = 6, MsgTrap = 7, MsgSmash = 8,
            MsgBuyRequest = 9, MsgBuyReply = 10, MsgSpawnerRespawn = 11;

        private const byte TravelUnderlands = 0, TravelCradle = 1, TravelReturnCradle = 2, TravelNpc = 3;
        private const byte FactoryMove = 0, FactoryRotate = 1, FactoryPause = 2;

        private static int _sent, _applied, _ignored;
        private static string _last = "";

        public static string Stats =>
            $"sent={_sent} applied={_applied} ignored={_ignored} fired={_fired.Count} travelQueue={_travelQueue.Count} " +
            $"deferredShops={_deferredShops.Count} pendingBuy={(_pendingBuy != null)} last='{_last}'";

        // ------------------------------------------------------------------ plumbing

        private static bool Connected
        {
            get { var s = Session.Instance; return s != null && s.Connected; }
        }

        private static bool IsHost => Session.Instance?.Role == SessionRole.Host;
        private static bool IsClient => Session.Instance?.Role == SessionRole.Client;

        /// <summary>Outbound gate: connected and not inside a remote apply.</summary>
        private static bool CanSend => Connected && !LoopGuard.ApplyingRemote;

        private static void Send(Writer w)
        {
            Session.Instance.SendReliable(PacketKind.WorldAction, w.ToArray());
            _sent++;
        }

        private static string Key(Component c)
        {
            if (c == null) return null;
            try { return EnemySync.KeyFor(ObjectRegistry.Instance, c); }
            catch { return null; }
        }

        /// <summary>Key -> component, per type, rebuilt lazily (at most once a second on a miss).</summary>
        private static class Index<T> where T : Component
        {
            public static readonly Dictionary<string, T> Map = new Dictionary<string, T>(StringComparer.Ordinal);
            public static float BuiltAt = -10f;
        }

        private static T Find<T>(string key) where T : Component
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (Index<T>.Map.TryGetValue(key, out var c) && (UnityEngine.Object)c != null) return c;
            float now = Time.realtimeSinceStartup;
            if (now - Index<T>.BuiltAt < 1f) return null;
            Index<T>.BuiltAt = now;
            Index<T>.Map.Clear();
            var all = Resources.FindObjectsOfTypeAll<T>();
            if (all != null)
                foreach (var x in all)
                {
                    if ((UnityEngine.Object)x == null) continue;
                    try
                    {
                        if (!x.gameObject.scene.IsValid()) continue;   // prefab assets
                        var k = Key(x);
                        if (k != null && !Index<T>.Map.ContainsKey(k)) Index<T>.Map[k] = x;
                    }
                    catch { }
                }
            return Index<T>.Map.TryGetValue(key, out c) ? c : null;
        }

        private static bool InMenu()
        {
            var m = GameRefs.Manager;
            if (m == null) return true;
            try { return m.CurrentGameState != global::DelusionManager.GameState.RUNNING; }
            catch { return true; }
        }

        // ------------------------------------------------------------------ session

        /// <summary>A new session (or a restarted client): forget per-session state.</summary>
        public static void OnSessionStart()
        {
            _fired.Clear();
            _localFired.Clear();
            _travelQueue.Clear();
            _deferredShops.Clear();
            _pendingBuy = null;
            _factoryDrivenAt.Clear();
            _factorySuspended.Clear();
            _pendingWorldState = -1;
            _worldStateAnnounced = false;
        }

        /// <summary>
        /// Join / resync: host sends its story phase and the non-saveable machinery state;
        /// a client sends its story phase (the receiver keeps the max of both).
        /// </summary>
        public static void SendJoinState()
        {
            if (!Connected) return;
            try
            {
                var st = global::DelusionStats.Instance;
                // Join: the host's story phase is the truth; a client does not announce its own.
                if (IsHost && st != null && GameRefs.InWorld)
                {
                    _worldStateAnnounced = true;
                    var w = new Writer();
                    w.U8(MsgWorldState).U8((byte)(int)st.GetWorldState());
                    Send(w);
                }
            }
            catch (Exception e) { Log.Debug("join world state: " + e.Message); }

            if (!IsHost) return;
            foreach (var kv in _factoryLast)
            {
                var w = new Writer();
                w.U8(MsgFactory).Str(kv.Key).U8(kv.Value.op).U8(kv.Value.state);
                Send(w);
            }
            foreach (var k in _completed) { var w = new Writer(); w.U8(MsgCompletable).Str(k).Bool(true); Send(w); }
            foreach (var k in _disarmed) { var w = new Writer(); w.U8(MsgTrap).Str(k); Send(w); }
        }

        /// <summary>Per frame (from the plugin tick).</summary>
        public static void Tick()
        {
            if (!Connected) return;
            try
            {
                TickWorldState();
                TickFactories();
                TickTravel();
                TickShops();
            }
            catch (Exception e) { Log.Debug("world events tick: " + e.Message); }
        }

        // ------------------------------------------------------------------ dispatch

        public static void Apply(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            var r = new Reader(payload);
            byte kind = 255;
            try
            {
                kind = r.U8();
                switch (kind)
                {
                    case MsgWorldState: ApplyWorldState(r.U8()); break;
                    case MsgRest: ApplyRest(r.I32(), r.I32()); break;
                    case MsgTriggerOn: ApplyTrigger(r.Str(), true); break;
                    case MsgTriggerOff: ApplyTrigger(r.Str(), false); break;
                    case MsgTravel: QueueTravel(r.U8(), r.Str()); break;
                    case MsgFactory: ApplyFactory(r.Str(), r.U8(), r.U8()); break;
                    case MsgCompletable: ApplyCompletable(r.Str(), r.Bool()); break;
                    case MsgTrap: ApplyTrap(r.Str()); break;
                    case MsgSmash: ApplySmash(r.Str()); break;
                    case MsgBuyRequest: OnBuyRequest(r.I32(), r.Str(), r.Str(), r.I32()); break;
                    case MsgBuyReply: OnBuyReply(r.I32(), r.Bool()); break;
                    case MsgSpawnerRespawn: ApplySpawnerRespawn(r.Str()); break;
                    default: _ignored++; break;
                }
            }
            catch (Exception e) { Log.Warn($"world action {kind} failed: {e.GetType().Name}: {e.Message}"); }
        }

        // ================================================================== 1. CharWorldState

        private static int _pendingWorldState = -1;
        private static bool _worldStateAnnounced;

        internal static void OnWorldStateChanged()
        {
            if (!CanSend || !GameRefs.InWorld) return;
            var st = global::DelusionStats.Instance;
            if (st == null) return;
            int v;
            try { v = (int)st.GetWorldState(); } catch { return; }
            var w = new Writer();
            w.U8(MsgWorldState).U8((byte)v);
            Send(w);
            Log.Info($"world state -> peer: {(global::CharWorldState)v}");
        }

        private static void ApplyWorldState(byte v)
        {
            // Monotonic: the story phase only moves forward. Applied when the world is up.
            if (v > _pendingWorldState) _pendingWorldState = v;
            TickWorldState();
        }

        private static void TickWorldState()
        {
            // Connected from the menu: announce our phase once the save is up.
            if (IsHost && !_worldStateAnnounced && GameRefs.InWorld && global::DelusionStats.Instance != null)
            {
                _worldStateAnnounced = true;
                OnWorldStateChanged();
            }
            if (_pendingWorldState < 0 || !GameRefs.InWorld) return;
            var st = global::DelusionStats.Instance;
            if (st == null) return;
            int want = _pendingWorldState;
            _pendingWorldState = -1;
            int cur;
            try { cur = (int)st.GetWorldState(); } catch { return; }
            if (want <= cur) { _ignored++; return; }
            LoopGuard.Apply(() =>
            {
                try
                {
                    st.ChangeCharWorldState((global::CharWorldState)want);
                    _applied++;
                    _last = "worldstate " + (global::CharWorldState)want;
                    Log.Info($"world state <- peer: {(global::CharWorldState)cur} -> {(global::CharWorldState)want}");
                }
                catch (Exception e) { Log.Warn("apply world state: " + e.Message); }
            });
        }

        // ================================================================== 2. Rest

        private static float _lastLocalRestAt = -100f;
        [ThreadStatic] private static bool _restInFade;

        /// <summary>True for a short while after this player rested: ClockSync must not
        /// rewind the clock with a host state sent before the host saw the rest.</summary>
        public static bool RecentLocalRest(float seconds) => Time.realtimeSinceStartup - _lastLocalRestAt < seconds;

        internal static void OnScreenFadeEndedPre(global::DelusionDayNight dn)
        {
            _restInFade = false;
            try { _restInFade = dn != null && dn.waitingForScreenFade; } catch { }
        }

        internal static void OnScreenFadeEndedPost(global::DelusionDayNight dn)
        {
            bool wasRest = _restInFade;
            _restInFade = false;
            if (!wasRest || LoopGuard.ApplyingRemote) return;
            _lastLocalRestAt = Time.realtimeSinceStartup;
            if (!CanSend) return;
            int hour = 0, day = 0;
            try { hour = dn.hourAfterScreenFade; } catch { }
            try { day = GameRefs.Stats?.daysPlayed ?? 0; } catch { }
            SendRest(hour, day);
        }

        private static void SendRest(int hour, int day)
        {
            var w = new Writer();
            w.U8(MsgRest).I32(hour).I32(day);
            Send(w);
            Log.Info($"rest -> peer: hour {hour}, day {day}");
        }

        private static void ApplyRest(int hour, int day)
        {
            var dn = GameRefs.DayNight;
            if (dn == null) { _ignored++; return; }
            bool resting = false;
            try { resting = dn.waitingForScreenFade; } catch { }
            if (resting) { _ignored++; return; }   // both slept at once: our own rest decides

            LoopGuard.Apply(() =>
            {
                try
                {
                    // SetHour(hour, true) runs DayHasPassed itself when midnight is crossed,
                    // so DayPassed/OnNewDay fire on this side too. No stat restore here.
                    dn.SetHour(hour, true);
                    var stats = GameRefs.Stats;
                    if (stats != null)
                    {
                        int missing = day - stats.daysPlayed;
                        for (int i = 0; i < missing && i < 3; i++) dn.DayHasPassed();
                    }
                    try { dn.OnRest?.Invoke(); } catch (Exception e) { Log.Debug("OnRest invoke: " + e.Message); }
                    _applied++;
                    _last = $"rest h{hour} d{day}";
                    Log.Info($"rest <- peer: hour {hour}, day {day}");
                }
                catch (Exception e) { Log.Warn("apply rest: " + e.Message); }
            });
        }

        /// <summary>Debug: an instant rest to <paramref name="hour"/> (no fade, no stat
        /// restore), replicated like a real one.</summary>
        public static string DebugRest(int hour)
        {
            var dn = GameRefs.DayNight;
            if (dn == null) return "ERR no daynight";
            dn.SetHour(hour, true);
            try { dn.OnRest?.Invoke(); } catch { }
            _lastLocalRestAt = Time.realtimeSinceStartup;
            int day = GameRefs.Stats?.daysPlayed ?? 0;
            if (Connected) SendRest(hour, day);
            return $"REST hour={hour} day={day} star={dn._starTimeSeconds:F0}";
        }

        // ================================================================== 3. TriggerOnPlayerEnter

        private static readonly HashSet<string> _fired = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _localFired = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Prefix on Activate: false skips it (the trigger already fired in this world).</summary>
        internal static bool OnTriggerActivate(global::TriggerOnPlayerEnter t)
        {
            if (LoopGuard.ApplyingRemote || !Connected || t == null) return true;
            var key = Key(t);
            if (key == null) return true;
            if (_fired.Contains(key)) return false;   // the partner already ran it for this world
            _fired.Add(key);
            _localFired.Add(key);
            var w = new Writer();
            w.U8(MsgTriggerOn).Str(key);
            Send(w);
            return true;
        }

        /// <summary>Prefix on Deactivate (player left the trigger).</summary>
        internal static bool OnTriggerDeactivate(global::TriggerOnPlayerEnter t)
        {
            if (LoopGuard.ApplyingRemote || !Connected || t == null) return true;
            var key = Key(t);
            if (key == null) return true;
            // Activated by the partner, never by us: leaving is not ours to undo.
            if (_fired.Contains(key) && !_localFired.Contains(key)) return false;
            _fired.Remove(key);
            _localFired.Remove(key);
            var w = new Writer();
            w.U8(MsgTriggerOff).Str(key);
            Send(w);
            return true;
        }

        private static void ApplyTrigger(string key, bool on)
        {
            if (on && _fired.Contains(key)) { _ignored++; return; }
            var t = Find<global::TriggerOnPlayerEnter>(key);
            if (on) _fired.Add(key); else { _fired.Remove(key); _localFired.Remove(key); }
            if (t == null) { _ignored++; return; }
            LoopGuard.Apply(() =>
            {
                try
                {
                    if (on)
                    {
                        // The respawn point is per player: the partner walking in must not
                        // move ours.
                        var drp = t.deathRespawnPoint;
                        t.deathRespawnPoint = null;
                        try { t.Activate(); }
                        finally { t.deathRespawnPoint = drp; }
                    }
                    else if (!t.staysActivated)
                    {
                        t.Deactivate();
                    }
                    _applied++;
                    _last = (on ? "trigger on " : "trigger off ") + key;
                }
                catch (Exception e) { Log.Warn($"apply trigger {key}: {e.Message}"); }
            });
        }

        // ================================================================== 4. Story travel

        private struct Travel { public byte Kind; public string NpcKey; public float At; }
        private static readonly List<Travel> _travelQueue = new List<Travel>();

        internal static void OnTravel(byte kind, global::NPCBase npc)
        {
            if (!CanSend || !GameRefs.InWorld) return;
            string npcKey = npc != null ? Key(npc) ?? "" : "";
            var w = new Writer();
            w.U8(MsgTravel).U8(kind).Str(npcKey);
            Send(w);
            Log.Info($"story travel -> peer: {kind} {npcKey}");
        }

        private static void QueueTravel(byte kind, string npcKey)
        {
            _travelQueue.Add(new Travel { Kind = kind, NpcKey = npcKey, At = Time.realtimeSinceStartup });
            TickTravel();
        }

        private static void TickTravel()
        {
            if (_travelQueue.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            // Drop very old requests (a partner who left the world long ago).
            _travelQueue.RemoveAll(t => now - t.At > 180f);
            if (_travelQueue.Count == 0) return;
            if (!GameRefs.InWorld || InMenu() || DialogueSync.Spectating) return;

            var tr = _travelQueue[0];
            var pa = GameRefs.PlayerActions;
            if (pa == null) return;
            global::NPCBase npc = null;
            if (tr.Kind == TravelNpc)
            {
                npc = Find<global::NPCBase>(tr.NpcKey);
                if (npc == null)
                {
                    if (now - tr.At > 30f) { _travelQueue.RemoveAt(0); _ignored++; Log.Warn("story travel: NPC not loaded " + tr.NpcKey); }
                    return;   // the region may still be streaming in
                }
            }
            _travelQueue.RemoveAt(0);
            LoopGuard.Apply(() =>
            {
                try
                {
                    switch (tr.Kind)
                    {
                        case TravelUnderlands: pa.UnderlandsTravel(); break;
                        case TravelCradle: pa.CradleTravel(); break;
                        case TravelReturnCradle: pa.ReturnFromCradle(); break;
                        case TravelNpc: npc.Teleport(); break;
                    }
                    _applied++;
                    _last = "travel " + tr.Kind;
                    Log.Info($"story travel <- peer: {tr.Kind} {tr.NpcKey}");
                }
                catch (Exception e) { Log.Warn("story travel: " + e.Message); }
            });
        }

        public static string DebugTravel(string what)
        {
            var pa = GameRefs.PlayerActions;
            if (pa == null) return "ERR no player actions";
            switch (what)
            {
                case "underlands": pa.UnderlandsTravel(); break;
                case "cradle": pa.CradleTravel(); break;
                case "return": pa.ReturnFromCradle(); break;
                default: return "ERR travel underlands|cradle|return";
            }
            return "TRAVEL " + what;
        }

        // ================================================================== 6. Machinery

        // ---- MovingFactory: the host drives the phase, the client follows.
        private static readonly Dictionary<string, (byte op, byte state)> _factoryLast =
            new Dictionary<string, (byte, byte)>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _factoryDrivenAt =
            new Dictionary<string, float>(StringComparer.Ordinal);
        // client: factories whose own next step was skipped because the host drives them
        private static readonly Dictionary<string, global::MovingFactory> _factorySuspended =
            new Dictionary<string, global::MovingFactory>(StringComparer.Ordinal);
        private const float FactoryHostTimeout = 60f;   // longest step is moveTime (40 s)

        /// <summary>Prefix on MoveStart/RotateStart/StartPause. Host: record and broadcast.
        /// Client: its own loop is suspended while the host drives this factory.</summary>
        internal static bool OnFactoryStep(global::MovingFactory f, byte op, int state)
        {
            if (LoopGuard.ApplyingRemote || !Connected || f == null) return true;
            var key = Key(f);
            if (key == null) return true;
            if (IsClient)
            {
                if (_factoryDrivenAt.TryGetValue(key, out var t) && Time.realtimeSinceStartup - t < FactoryHostTimeout)
                {
                    _factorySuspended[key] = f;
                    return false;
                }
                return true;
            }
            _factoryLast[key] = (op, (byte)state);
            var w = new Writer();
            w.U8(MsgFactory).Str(key).U8(op).U8((byte)state);
            Send(w);
            return true;
        }

        /// <summary>Client: the host stopped driving a factory (it left the region) - restart
        /// the local loop that was suspended.</summary>
        private static void TickFactories()
        {
            if (_factorySuspended.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<string> resume = null;
            foreach (var kv in _factorySuspended)
                if (!_factoryDrivenAt.TryGetValue(kv.Key, out var t) || now - t >= FactoryHostTimeout)
                    (resume ??= new List<string>()).Add(kv.Key);
            if (resume == null) return;
            foreach (var k in resume)
            {
                var f = _factorySuspended[k];
                _factorySuspended.Remove(k);
                _factoryDrivenAt.Remove(k);
                try { if (f != null && f.gameObject.activeInHierarchy) { f.StopAllCoroutines(); f.StartMovement(); } }
                catch (Exception e) { Log.Debug("factory resume: " + e.Message); }
            }
        }

        private static void ApplyFactory(string key, byte op, byte state)
        {
            if (!IsClient) { _ignored++; return; }
            _factoryDrivenAt[key] = Time.realtimeSinceStartup;
            _factorySuspended.Remove(key);
            var f = Find<global::MovingFactory>(key);
            if (f == null) { _ignored++; return; }
            LoopGuard.Apply(() =>
            {
                try
                {
                    f.StopAllCoroutines();
                    var s = (global::MovingFactory.MoveState)state;
                    switch (op)
                    {
                        case FactoryMove: f.MoveStart(s); break;
                        case FactoryRotate: f.RotateStart(s); break;
                        case FactoryPause: f.StartPause(); break;
                    }
                    _applied++;
                    _last = $"factory {op}/{s}";
                }
                catch (Exception e) { Log.Warn("apply factory: " + e.Message); }
            });
        }

        // ---- CompletableObject: the slab opens for both.
        private static readonly HashSet<string> _completed = new HashSet<string>(StringComparer.Ordinal);

        internal static void OnAllComponentsComplete(global::CompletableObject c)
        {
            var key = Key(c);
            if (key == null) return;
            _completed.Add(key);
            if (!CanSend) return;
            var w = new Writer();
            w.U8(MsgCompletable).Str(key).Bool(false);
            Send(w);
        }

        private static void ApplyCompletable(string key, bool instant)
        {
            _completed.Add(key);
            var c = Find<global::CompletableObject>(key);
            if (c == null) { _ignored++; return; }
            bool done = false;
            try { done = c.isComplete; } catch { }
            if (done) { _ignored++; return; }
            LoopGuard.Apply(() =>
            {
                try
                {
                    if (instant) c.AllComponentsCompleteInstant(); else c.AllComponentsComplete();
                    _applied++;
                    _last = "completable " + key;
                }
                catch (Exception e) { Log.Warn("apply completable: " + e.Message); }
            });
        }

        // ---- Trap: a disarmed trap is disarmed for both.
        private static readonly HashSet<string> _disarmed = new HashSet<string>(StringComparer.Ordinal);

        internal static void OnTrapDisabled(global::Trap t)
        {
            var key = Key(t);
            if (key == null) return;
            _disarmed.Add(key);
            if (!CanSend) return;
            var w = new Writer();
            w.U8(MsgTrap).Str(key);
            Send(w);
        }

        private static void ApplyTrap(string key)
        {
            _disarmed.Add(key);
            var t = Find<global::Trap>(key);
            if (t == null) { _ignored++; return; }
            if (!t.isReady) { _ignored++; return; }
            LoopGuard.Apply(() =>
            {
                try { t.DisableTrap(); _applied++; _last = "trap " + key; }
                catch (Exception e) { Log.Warn("apply trap: " + e.Message); }
            });
        }

        // ---- SmashableSpawner: show the smash on the partner, who gets their own loot roll.
        [ThreadStatic] private static bool _smashWasActive;

        internal static void OnSmashPre(global::SmashableSpawner s)
        {
            _smashWasActive = false;
            try { _smashWasActive = s != null && s.saveData != null && s.saveData.IsActive(); } catch { }
        }

        internal static void OnSmashPost(global::SmashableSpawner s)
        {
            if (!CanSend || s == null || _smashWasActive) return;
            bool now = false;
            try { now = s.saveData != null && s.saveData.IsActive(); } catch { }
            if (!now) return;   // might check failed
            var key = Key(s);
            if (key == null) return;
            var w = new Writer();
            w.U8(MsgSmash).Str(key);
            Send(w);
        }

        private static void ApplySmash(string key)
        {
            var s = Find<global::SmashableSpawner>(key);
            if (s == null) { _ignored++; return; }
            LoopGuard.Apply(() =>
            {
                try
                {
                    var simple = s.simpleObject;
                    // Already nullified by the mirror (or smashed here): nothing to show.
                    if (simple == null || !simple.activeSelf || !s.gameObject.activeInHierarchy) { _ignored++; return; }
                    // The visual half of Smash(): swap to the physics pieces and let them
                    // clear themselves. The might check and save flag stay with the smasher.
                    simple.SetActive(false);
                    var rbs = s.rigidBodyObjs;
                    if (rbs != null)
                        for (int i = 0; i < rbs.Length; i++) if (rbs[i] != null) rbs[i].SetActive(true);
                    s.StartCoroutine(s.DelayedDeactivation());
                    // The partner gets loot too: this copy rolls its own drop (a pickup, so
                    // it is this player's to take).
                    try { s.EnableRandomLoot(); } catch (Exception e) { Log.Debug("smash loot: " + e.Message); }
                    _applied++;
                    _last = "smash " + key;
                }
                catch (Exception e) { Log.Warn("apply smash: " + e.Message); }
            });
        }

        // ================================================================== 7. Shops

        private static global::ShopSpeaker _dirtyShop;
        private static readonly Dictionary<IntPtr, (global::SaveableObject so, Il2CppSystem.Object state, global::ShopSpeaker shop)> _deferredShops =
            new Dictionary<IntPtr, (global::SaveableObject, Il2CppSystem.Object, global::ShopSpeaker)>();

        /// <summary>The ShopSpeaker whose stock the inventory screen is showing.</summary>
        private static global::ShopSpeaker OpenShop(global::DelusionInventory inv)
        {
            if (inv == null) return null;
            Il2CppSystem.Collections.Generic.List<global::ItemInstance> list = null;
            try { list = inv.actualShopList; } catch { }
            if (list == null) return null;
            IntPtr p = list.Pointer;
            var all = Resources.FindObjectsOfTypeAll<global::ShopSpeaker>();
            if (all == null) return null;
            foreach (var s in all)
            {
                if (s == null) continue;
                try { if (s.shopInventory != null && s.shopInventory.Pointer == p) return s; } catch { }
                try { if (s.savedShop != null && s.savedShop.itemList != null && s.savedShop.itemList.Pointer == p) return s; } catch { }
            }
            // The open list may be a copy: find the shop that stocks the very item being traded.
            global::ItemInstance trade = null;
            try { trade = inv.itemToTrade; } catch { }
            if (trade != null)
                foreach (var s in all)
                {
                    if (s == null || !s.gameObject.scene.IsValid()) continue;
                    try
                    {
                        var l = s.shopInventory;
                        if (l != null) for (int i = 0; i < l.Count; i++) if (l[i] != null && l[i].Pointer == trade.Pointer) return s;
                    }
                    catch { }
                }
            return null;
        }

        /// <summary>True while the local shop screen shows this shop's stock.</summary>
        private static bool IsShopOpen(global::ShopSpeaker shop)
        {
            if (shop == null) return false;
            var m = GameRefs.Manager;
            var inv = GameRefs.Inventory;
            if (m == null || inv == null) return false;
            try
            {
                if (m.CurrentGameState == global::DelusionManager.GameState.RUNNING) return false;
                var mode = inv.activeSelectionMode;
                if (mode != global::DelusionInventory.SelectionMode.Buy && mode != global::DelusionInventory.SelectionMode.Sell) return false;
                return OpenShop(inv) == shop;
            }
            catch { return false; }
        }

        private static string ShopKey(global::ShopSpeaker shop)
        {
            var so = shop != null ? shop.GetComponent<global::SaveableObject>() : null;
            if (so == null) return null;
            string id = null;
            try { id = so.GetUniqueIdentifier(); } catch { }
            return string.IsNullOrEmpty(id) ? null : ObjectRegistry.Instance.CurrentScene + "|" + id;
        }

        /// <summary>Stock changed locally (buy, sell, stackable depletion): push it next frame.</summary>
        internal static void OnShopStockChanged(global::DelusionInventory inv)
        {
            if (!CanSend) return;
            var shop = OpenShop(inv);
            if (shop != null) _dirtyShop = shop;
        }

        /// <summary>Send the shop's full saveable state through the mirror channel now,
        /// instead of waiting for the next poll.</summary>
        private static void PushShop(global::ShopSpeaker shop)
        {
            var so = shop.GetComponent<global::SaveableObject>();
            var key = ShopKey(shop);
            if (so == null || key == null) return;
            object state;
            try { state = so.CaptureState(); } catch { return; }
            if (state == null) return;
            byte[] body;
            try { body = StateCodec.Encode(state); }
            catch (Exception e) { Log.Debug("shop encode: " + e.Message); return; }
            var w = new Writer();
            w.Str(key).U16((ushort)Math.Min(body.Length, ushort.MaxValue)).Bytes(body);
            Session.Instance.SendReliableLarge(IsHost ? PacketKind.ObjectState : PacketKind.ObjectRequest, w.ToArray());
            _sent++;
            Log.Info("shop -> peer: " + key);
        }

        /// <summary>Prefix on SaveableObject.RestoreState: a peer's shop state arriving while
        /// that shop is open here would leave the buy list stale (or index past its end).
        /// Hold it and apply when the screen closes.</summary>
        internal static bool OnRestoreState(global::SaveableObject so, Il2CppSystem.Object state)
        {
            if (!LoopGuard.ApplyingRemote || !Connected || so == null) return true;
            global::ShopSpeaker shop = null;
            try { shop = so.GetComponent<global::ShopSpeaker>(); } catch { }
            if (shop == null || !IsShopOpen(shop)) return true;
            _deferredShops[so.Pointer] = (so, state, shop);
            Log.Info("shop state deferred while open: " + so.name);
            return false;
        }

        private static void TickShops()
        {
            if (_dirtyShop != null)
            {
                var s = _dirtyShop;
                _dirtyShop = null;
                try { PushShop(s); } catch (Exception e) { Log.Debug("shop push: " + e.Message); }
            }

            if (_deferredShops.Count > 0)
            {
                List<IntPtr> done = null;
                foreach (var kv in _deferredShops)
                {
                    var (so, state, shop) = kv.Value;
                    if (so == null || shop == null) { (done ??= new List<IntPtr>()).Add(kv.Key); continue; }
                    if (IsShopOpen(shop)) continue;
                    (done ??= new List<IntPtr>()).Add(kv.Key);
                    LoopGuard.Apply(() =>
                    {
                        try { so.RestoreState(state); _applied++; _last = "shop (deferred) " + so.name; }
                        catch (Exception e) { Log.Warn("deferred shop restore: " + e.Message); }
                    });
                }
                if (done != null) foreach (var k in done) _deferredShops.Remove(k);
            }

            if (_pendingBuy != null && Time.realtimeSinceStartup - _pendingBuy.At > 2f)
            {
                // Host silent (or busy streaming): fall back to the optimistic buy.
                Log.Warn("buy request: no host answer - buying optimistically");
                CompleteBuy(true);
            }
        }

        // ---- host-arbitrated purchase of unique (non-stackable) items

        private sealed class PendingBuy
        {
            public int Id;
            public global::DelusionInventory Inv;
            public IntPtr ItemPtr;
            public bool Multi;
            public float At;
        }

        private static PendingBuy _pendingBuy;

        /// <summary>Off by default: inventories are per player, so two players buying the
        /// same one-of-a-kind item at the same moment each simply own a copy (stock still ends
        /// the same on both). On: the host decides who gets it.</summary>
        public static bool ExclusiveUniqueBuys = false;
        private static int _nextBuyId = 1;
        [ThreadStatic] private static bool _bypassBuy;

        /// <summary>Prefix on FinishBuyItem. Client: a unique item is first claimed from the
        /// host; the purchase completes when the host agrees.</summary>
        internal static bool OnFinishBuyPre(global::DelusionInventory inv, bool sellingMultiple)
        {
            if (!ExclusiveUniqueBuys || _bypassBuy || LoopGuard.ApplyingRemote || !Connected || !IsClient || inv == null) return true;
            global::ItemInstance item = null;
            try { item = inv.itemToTrade; } catch { }
            if (item == null || item.item == null || item.item.isStackable) return true;
            var shop = OpenShop(inv);
            var key = shop != null ? ShopKey(shop) : null;
            if (key == null) return true;
            if (_pendingBuy != null) return false;   // a claim is already in flight
            _pendingBuy = new PendingBuy
            {
                Id = _nextBuyId++, Inv = inv, ItemPtr = item.Pointer, Multi = sellingMultiple,
                At = Time.realtimeSinceStartup,
            };
            var w = new Writer();
            w.U8(MsgBuyRequest).I32(_pendingBuy.Id).Str(key).Str(item.item.itemID ?? "").I32((int)item.rarity);
            Send(w);
            Log.Info($"buy -> host: claim {item.item.itemID} from {key}");
            return false;
        }

        private static void OnBuyReply(int id, bool ok)
        {
            if (_pendingBuy == null || _pendingBuy.Id != id) { _ignored++; return; }
            CompleteBuy(ok);
        }

        private static void CompleteBuy(bool ok)
        {
            var pb = _pendingBuy;
            _pendingBuy = null;
            if (pb == null || pb.Inv == null) return;
            global::ItemInstance cur = null;
            try { cur = pb.Inv.itemToTrade; } catch { }
            if (ok)
            {
                if (cur == null || cur.Pointer != pb.ItemPtr) { Log.Warn("buy: selection changed before the host answered - not bought"); return; }
                _bypassBuy = true;
                try { pb.Inv.FinishBuyItem(pb.Multi); _applied++; _last = "buy ok"; }
                catch (Exception e) { Log.Warn("buy: " + e.Message); }
                finally { _bypassBuy = false; }
                return;
            }
            // Denied: the partner bought it first. Take it off our list and refresh.
            Log.Info("buy denied by host: the partner already bought that item");
            LoopGuard.Apply(() =>
            {
                try
                {
                    var list = pb.Inv.actualShopList;
                    if (list != null)
                        for (int i = list.Count - 1; i >= 0; i--)
                            if (list[i] != null && list[i].Pointer == pb.ItemPtr) { list.RemoveAt(i); break; }
                    if (pb.Inv.activeSelectionMode == global::DelusionInventory.SelectionMode.Buy) pb.Inv.BuyDisplayEssentials();
                }
                catch (Exception e) { Log.Debug("buy denied cleanup: " + e.Message); }
            });
            _last = "buy denied";
        }

        private static void OnBuyRequest(int id, string shopKey, string itemId, int rarity)
        {
            if (!IsHost) return;
            bool ok = true;
            global::ShopSpeaker shop = null;
            var all = Resources.FindObjectsOfTypeAll<global::ShopSpeaker>();
            if (all != null)
                foreach (var s in all)
                {
                    if (s == null || !s.gameObject.scene.IsValid()) continue;
                    if (ShopKey(s) == shopKey) { shop = s; break; }
                }
            // A shop the host has not loaded cannot be arbitrated: allow (mirror fallback).
            var list = shop?.shopInventory;
            if (list != null)
            {
                int found = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    if (it == null || it.item == null) continue;
                    if (it.item.itemID == itemId && (int)it.rarity == rarity) { found = i; break; }
                }
                ok = found >= 0;
                if (ok)
                {
                    LoopGuard.Apply(() =>
                    {
                        try
                        {
                            list.RemoveAt(found);
                            var inv = GameRefs.Inventory;
                            if (IsShopOpen(shop) && inv != null && inv.activeSelectionMode == global::DelusionInventory.SelectionMode.Buy)
                                inv.BuyDisplayEssentials();
                        }
                        catch (Exception e) { Log.Debug("buy claim remove: " + e.Message); }
                    });
                }
            }
            var w = new Writer();
            w.U8(MsgBuyReply).I32(id).Bool(ok);
            Send(w);
            Log.Info($"buy claim from client: {itemId} r{rarity} at {shopKey} -> {(ok ? "granted" : "denied")}{(shop == null ? " (shop not loaded)" : "")}");
        }

        // ================================================================== 8. Spawner respawns

        internal static void OnRespawnEnemy(global::EnemySpawner sp)
        {
            if (sp == null) return;
            try
            {
                var c = sp.enemyController;
                if (c != null) EnemySync.ClearDead(c);
            }
            catch { }
            if (!CanSend || !IsHost) return;
            var key = Key(sp);
            if (key == null) return;
            var w = new Writer();
            w.U8(MsgSpawnerRespawn).Str(key);
            Send(w);
        }

        /// <summary>Client: the host owns respawns, so a local death does not respawn.</summary>
        internal static bool AllowPlayerDiedRespawn() => !(Connected && IsClient);

        private static void ApplySpawnerRespawn(string key)
        {
            var sp = Find<global::EnemySpawner>(key);
            if (sp == null) { _ignored++; return; }
            LoopGuard.Apply(() =>
            {
                try
                {
                    var c = sp.enemyController;
                    bool dead = c == null || EnemySync.IsDead(c) || !c.gameObject.activeInHierarchy;
                    try { if (!dead && c.myStats != null && c.myStats.currentHealth <= 0) dead = true; } catch { }
                    if (c != null) EnemySync.ClearDead(c);
                    if (!dead) { _ignored++; return; }   // already respawned here (own day/rest)
                    sp.RespawnEnemy(true, null);
                    _applied++;
                    _last = "respawn " + key;
                }
                catch (Exception e) { Log.Warn("apply respawn: " + e.Message); }
            });
        }

        // ================================================================== debug

        private static T Nearest<T>(string filter = null) where T : Component
        {
            var me = GameRefs.Player;
            var all = Resources.FindObjectsOfTypeAll<T>();
            if (me == null || all == null) return null;
            T best = null; float bd = float.MaxValue;
            foreach (var x in all)
            {
                if ((UnityEngine.Object)x == null || !x.gameObject.activeInHierarchy) continue;
                if (filter != null && x.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                float d = Vector3.Distance(x.transform.position, me.transform.position);
                if (d < bd) { bd = d; best = x; }
            }
            return best;
        }

        /// <summary>Rig console commands (see Cmd).</summary>
        public static IEnumerable<string> DebugCmd(string cmd, string[] parts)
        {
            var res = new List<string>();
            string arg = parts.Length > 1 ? parts[1] : null;
            switch (cmd)
            {
                case "worldevents":
                    res.Add("WORLDEVENTS " + Stats);
                    break;
                case "worldstate":
                {
                    var st = global::DelusionStats.Instance;
                    if (st == null) { res.Add("ERR no stats"); break; }
                    if (arg != null) st.ChangeCharWorldState((global::CharWorldState)int.Parse(arg));
                    res.Add($"WORLDSTATE {st.GetWorldState()} ({(int)st.GetWorldState()})");
                    break;
                }
                case "rest":
                    res.Add(DebugRest(arg != null ? int.Parse(arg) : 9));
                    break;
                case "clock":
                {
                    var dn = GameRefs.DayNight;
                    if (dn == null) { res.Add("ERR no daynight"); break; }
                    res.Add($"CLOCK day={GameRefs.Stats?.daysPlayed} star={dn._starTimeSeconds:F1} tod={dn.typeOfDay}");
                    break;
                }
                case "travel":
                    res.Add(DebugTravel(arg ?? ""));
                    break;
                case "trignear":
                {
                    var t = Nearest<global::TriggerOnPlayerEnter>(arg);
                    if (t == null) { res.Add("ERR no trigger"); break; }
                    t.Activate();
                    res.Add($"TRIGGER {Key(t)} fired={_fired.Contains(Key(t) ?? "")}");
                    break;
                }
                case "trapnear":
                {
                    var t = Nearest<global::Trap>(arg);
                    if (t == null) { res.Add("ERR no trap"); break; }
                    t.DisableTrap();
                    res.Add($"TRAP {Key(t)} ready={t.isReady}");
                    break;
                }
                case "smashnear":
                {
                    var s = Nearest<global::SmashableSpawner>(arg);
                    if (s == null) { res.Add("ERR no smashable"); break; }
                    s.Smash();
                    res.Add($"SMASH {Key(s)} active={s.saveData?.IsActive()}");
                    break;
                }
                case "respawnnear":
                {
                    var s = Nearest<global::EnemySpawner>(arg);
                    if (s == null) { res.Add("ERR no spawner"); break; }
                    s.RespawnEnemy(true, null);
                    res.Add($"RESPAWN {Key(s)}");
                    break;
                }
                case "liftup":
                case "liftdown":
                {
                    var lifts = Resources.FindObjectsOfTypeAll<global::DelusionLift>();
                    var me = GameRefs.Player;
                    global::DelusionLift best = null; string bestId = null; float bd = float.MaxValue;
                    if (lifts != null && me != null)
                        foreach (var l in lifts)
                        {
                            if (l == null || !l.gameObject.activeInHierarchy) continue;
                            string id = null;
                            try { id = l.GetComponent<global::SaveableObject>()?.GetUniqueIdentifier(); } catch { }
                            if (string.IsNullOrEmpty(id)) continue;
                            if (arg != null && id.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            float d = Vector3.Distance(l.transform.position, me.transform.position);
                            if (d < bd) { bd = d; best = l; bestId = id; }
                        }
                    if (best == null) { res.Add("ERR no lift"); break; }
                    bool ok = cmd == "liftup" ? best.TryRecallTop() : best.TryRecallBot();
                    res.Add($"LIFT {bestId} {cmd} ok={ok} pos={best.liftPosition} locked={best.recallLocked} d={bd:F0}");
                    break;
                }
                default:
                    res.Add("ERR unknown");
                    break;
            }
            return res;
        }
    }

    // ====================================================================== hooks

    [HarmonyPatch(typeof(global::DelusionStats), nameof(global::DelusionStats.ChangeCharWorldState))]
    internal static class Patch_Stats_ChangeCharWorldState
    {
        [HarmonyPostfix] static void Postfix() => WorldEventsSync.OnWorldStateChanged();
    }

    [HarmonyPatch(typeof(global::DelusionDayNight), "ScreenFadeEnded")]
    internal static class Patch_DayNight_ScreenFadeEnded
    {
        [HarmonyPrefix] static void Prefix(global::DelusionDayNight __instance) => WorldEventsSync.OnScreenFadeEndedPre(__instance);
        [HarmonyPostfix] static void Postfix(global::DelusionDayNight __instance) => WorldEventsSync.OnScreenFadeEndedPost(__instance);
    }

    [HarmonyPatch(typeof(global::TriggerOnPlayerEnter), "Activate")]
    internal static class Patch_TriggerOnPlayerEnter_Activate
    {
        [HarmonyPrefix] static bool Prefix(global::TriggerOnPlayerEnter __instance) => WorldEventsSync.OnTriggerActivate(__instance);
    }

    [HarmonyPatch(typeof(global::TriggerOnPlayerEnter), "Deactivate")]
    internal static class Patch_TriggerOnPlayerEnter_Deactivate
    {
        [HarmonyPrefix] static bool Prefix(global::TriggerOnPlayerEnter __instance) => WorldEventsSync.OnTriggerDeactivate(__instance);
    }

    [HarmonyPatch(typeof(global::DelusionPlayerActions), nameof(global::DelusionPlayerActions.UnderlandsTravel))]
    internal static class Patch_Actions_UnderlandsTravel
    {
        [HarmonyPostfix] static void Postfix() => WorldEventsSync.OnTravel(0, null);
    }

    [HarmonyPatch(typeof(global::DelusionPlayerActions), nameof(global::DelusionPlayerActions.CradleTravel))]
    internal static class Patch_Actions_CradleTravel
    {
        [HarmonyPostfix] static void Postfix() => WorldEventsSync.OnTravel(1, null);
    }

    [HarmonyPatch(typeof(global::DelusionPlayerActions), nameof(global::DelusionPlayerActions.ReturnFromCradle))]
    internal static class Patch_Actions_ReturnFromCradle
    {
        [HarmonyPostfix] static void Postfix() => WorldEventsSync.OnTravel(2, null);
    }

    [HarmonyPatch(typeof(global::NPCBase), nameof(global::NPCBase.Teleport))]
    internal static class Patch_NPC_Teleport
    {
        [HarmonyPostfix] static void Postfix(global::NPCBase __instance) => WorldEventsSync.OnTravel(3, __instance);
    }

    [HarmonyPatch(typeof(global::MovingFactory), "MoveStart")]
    internal static class Patch_MovingFactory_MoveStart
    {
        [HarmonyPrefix]
        static bool Prefix(global::MovingFactory __instance, global::MovingFactory.MoveState nextMoveState)
            => WorldEventsSync.OnFactoryStep(__instance, 0, (int)nextMoveState);
    }

    [HarmonyPatch(typeof(global::MovingFactory), "RotateStart")]
    internal static class Patch_MovingFactory_RotateStart
    {
        [HarmonyPrefix]
        static bool Prefix(global::MovingFactory __instance, global::MovingFactory.MoveState nextMoveState)
            => WorldEventsSync.OnFactoryStep(__instance, 1, (int)nextMoveState);
    }

    [HarmonyPatch(typeof(global::MovingFactory), "StartPause")]
    internal static class Patch_MovingFactory_StartPause
    {
        [HarmonyPrefix]
        static bool Prefix(global::MovingFactory __instance)
            => WorldEventsSync.OnFactoryStep(__instance, 2, (int)__instance.currentMoveState);
    }

    [HarmonyPatch(typeof(global::CompletableObject), "AllComponentsComplete")]
    internal static class Patch_CompletableObject_AllComplete
    {
        [HarmonyPostfix]
        static void Postfix(global::CompletableObject __instance)
        {
            if (!LoopGuard.ApplyingRemote) WorldEventsSync.OnAllComponentsComplete(__instance);
        }
    }

    [HarmonyPatch(typeof(global::Trap), "DisableTrap")]
    internal static class Patch_Trap_DisableTrap
    {
        [HarmonyPostfix]
        static void Postfix(global::Trap __instance)
        {
            if (!LoopGuard.ApplyingRemote) WorldEventsSync.OnTrapDisabled(__instance);
        }
    }

    [HarmonyPatch(typeof(global::SmashableSpawner), nameof(global::SmashableSpawner.Smash))]
    internal static class Patch_Smashable_Smash
    {
        [HarmonyPrefix] static void Prefix(global::SmashableSpawner __instance) => WorldEventsSync.OnSmashPre(__instance);
        [HarmonyPostfix] static void Postfix(global::SmashableSpawner __instance) => WorldEventsSync.OnSmashPost(__instance);
    }

    // ---- shops

    [HarmonyPatch(typeof(global::DelusionInventory), nameof(global::DelusionInventory.RemoveItemFromShop))]
    internal static class Patch_Inventory_RemoveItemFromShop
    {
        [HarmonyPostfix] static void Postfix(global::DelusionInventory __instance) => WorldEventsSync.OnShopStockChanged(__instance);
    }

    [HarmonyPatch(typeof(global::DelusionInventory), nameof(global::DelusionInventory.DepleteShopStackable))]
    internal static class Patch_Inventory_DepleteShopStackable
    {
        [HarmonyPostfix] static void Postfix(global::DelusionInventory __instance) => WorldEventsSync.OnShopStockChanged(__instance);
    }

    [HarmonyPatch(typeof(global::DelusionInventory), nameof(global::DelusionInventory.StoreItemInShop))]
    internal static class Patch_Inventory_StoreItemInShop
    {
        [HarmonyPostfix] static void Postfix(global::DelusionInventory __instance) => WorldEventsSync.OnShopStockChanged(__instance);
    }

    [HarmonyPatch(typeof(global::DelusionInventory), nameof(global::DelusionInventory.FinishBuyItem))]
    internal static class Patch_Inventory_FinishBuyItem
    {
        // FinishBuyItem removes a unique item from the stock inline (not through
        // RemoveItemFromShop), so the push is hooked here as well.
        [HarmonyPrefix]
        static bool Prefix(global::DelusionInventory __instance, bool sellingMultiple, out bool __state)
        {
            __state = WorldEventsSync.OnFinishBuyPre(__instance, sellingMultiple);
            return __state;
        }

        [HarmonyPostfix]
        static void Postfix(global::DelusionInventory __instance, bool __state)
        {
            if (__state) WorldEventsSync.OnShopStockChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(global::DelusionInventory), nameof(global::DelusionInventory.FinishSellItem))]
    internal static class Patch_Inventory_FinishSellItem
    {
        [HarmonyPostfix] static void Postfix(global::DelusionInventory __instance) => WorldEventsSync.OnShopStockChanged(__instance);
    }

    [HarmonyPatch(typeof(global::SaveableObject), nameof(global::SaveableObject.RestoreState))]
    internal static class Patch_SaveableObject_RestoreState_ShopDefer
    {
        [HarmonyPrefix]
        static bool Prefix(global::SaveableObject __instance, Il2CppSystem.Object state)
            => WorldEventsSync.OnRestoreState(__instance, state);
    }

    // ---- spawners

    [HarmonyPatch(typeof(global::EnemySpawner), nameof(global::EnemySpawner.RespawnEnemy))]
    internal static class Patch_EnemySpawner_RespawnEnemy
    {
        [HarmonyPostfix] static void Postfix(global::EnemySpawner __instance) => WorldEventsSync.OnRespawnEnemy(__instance);
    }

    [HarmonyPatch(typeof(global::EnemySpawner), nameof(global::EnemySpawner.PlayerDied))]
    internal static class Patch_EnemySpawner_PlayerDied
    {
        [HarmonyPrefix] static bool Prefix() => WorldEventsSync.AllowPlayerDiedRespawn();
    }
}
