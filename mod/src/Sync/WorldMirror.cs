using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Full world-state mirror.
    ///
    /// The shortcut this mod is built on: Dread Delusion already has a generic keyed state
    /// system. Every world object that persists (door, lift, lever, chest, pickup, shop,
    /// spawner, quest object, trigger, airship part) is a SaveableObject whose
    /// CaptureState()/RestoreState() the game itself wrote. Instead of a hand-written wire
    /// payload per component type, the host hashes each saveable's captured state and sends
    /// the encoded object graph whenever it changes; the client rebuilds the same type and
    /// lets the game's own RestoreState apply it.
    ///
    /// That is what makes "full world sync" tractable: coverage is defined by the game's
    /// save surface rather than by a list we have to maintain, and animation-heavy objects
    /// still animate locally because only their state crosses the wire.
    ///
    /// Keys are (scene, SaveableObject.uniqueIdentifier) - the identity the game already
    /// ships for 4 754 of 4 804 components.
    /// </summary>
    public static class WorldMirror
    {
        private const float PollInterval = 0.5f;
        private static float _nextPoll;

        private static readonly Dictionary<string, ulong> _hashes =
            new Dictionary<string, ulong>(8192, StringComparer.Ordinal);

        private static readonly Dictionary<string, GameObject> _byKey =
            new Dictionary<string, GameObject>(8192, StringComparer.Ordinal);

        private static int _sent;
        private static int _oversize;
        private static int _applied;
        private static int _rejected;
        private static string _lastReject = "";
        private static string _lastApply = "";

        public static int KnownObjects => _hashes.Count;
        public static int AppliedCount => _applied;
        public static int SentCount => _sent;
        public static int RejectedCount => _rejected;
        public static string LastReject => _lastReject;
        public static string LastApply => _lastApply;

        public static void Init()
        {
            _hashes.Clear();
            _byKey.Clear();
            _sent = _oversize = _applied = 0;
        }

        // ------------------------------------------------------------------ two-way
        //
        // Both players act on the world. The host is the authority and broadcasts every
        // change; the client forwards changes *it* caused (a door it opened, a chest it
        // looted) as requests, which the host applies and so makes canonical.
        //
        // Three rules keep that from turning into a tug-of-war:
        //   * first sight is a baseline on the client - its own pre-session/streamed-in
        //     state is never pushed to the host, only later local changes are;
        //   * an object just written by the peer is "quiet" for a moment, so the local
        //     settle (door midOpen -> open) is not echoed back;
        //   * an object that keeps changing on the client by itself (timers, simulated
        //     props) is marked volatile and left to the host.

        private static readonly Dictionary<string, float> _quietUntil =
            new Dictionary<string, float>(256, StringComparer.Ordinal);
        private static readonly Dictionary<string, (float since, int n)> _churn =
            new Dictionary<string, (float, int)>(256, StringComparer.Ordinal);
        private static readonly HashSet<string> _volatile = new HashSet<string>(StringComparer.Ordinal);
        private static int _requests;
        private static int _newSightings;
        private static float _nextResyncAsk;

        public static int RequestCount => _requests;
        public static int VolatileCount => _volatile.Count;

        private static bool IsQuiet(string key) =>
            _quietUntil.TryGetValue(key, out var t) && Time.realtimeSinceStartup < t;

        private static void MarkQuiet(string key, float seconds = 2.5f) =>
            _quietUntil[key] = Time.realtimeSinceStartup + seconds;

        /// <summary>Client: true when a local change should go to the host.</summary>
        private static bool ShouldForward(string key)
        {
            if (_volatile.Contains(key)) return false;
            float now = Time.realtimeSinceStartup;
            if (!_churn.TryGetValue(key, out var c) || now - c.since > 10f) c = (now, 0);
            c.n++;
            _churn[key] = c;
            if (c.n > 4)
            {
                _volatile.Add(key);
                Log.Info($"mirror: '{key}' changes by itself on the client - host-owned from now on");
                return false;
            }
            return true;
        }

        /// <summary>Client: the session (re)started - re-baseline everything.</summary>
        private static bool _firstPass = true;

        public static void OnClientConnected()
        {
            _firstPass = true;
            _hashes.Clear();
            _typed.Clear();
            _quietUntil.Clear();
            _churn.Clear();
            _volatile.Clear();
        }

        /// <summary>Host: the client asked for a fresh copy of the world (it streamed in a
        /// region whose objects it had not seen when they were last sent).</summary>
        public static void OnMirrorResyncRequest()
        {
            _hashes.Clear();
            _typed.Clear();
        }

        /// <summary>Force the next tick to re-send every object (late join / resync).</summary>
        public static void ForceSnapshot()
        {
            _hashes.Clear();
            _typed.Clear();
            Log.Info("world mirror: forcing full snapshot on next tick");
        }

        // ------------------------------------------------------------------ host

        public static void TickHost() => Tick();

        // The sweep is time-sliced: a slice of the saveables is captured each frame within
        // a small budget, instead of all of them (~700+) in one frame twice a second, which
        // produced 100+ ms hitches. A full sweep still starts at most every PollInterval.
        private const double SliceBudgetMs = 1.5;
        private const int SliceMaxObjects = 120;
        private const float ShopPollInterval = 10f;   // shops are pushed on change; their capture logs
        private static global::SaveableObject[] _sweep;
        private static double _slowestMs;
        private static string _slowestName = "";
        public static string Slowest { get { var r = $"slowest object {_slowestMs:F2} ms: {_slowestName}"; _slowestMs = 0; return r; } }
        private static int _cursor;
        private static int _sentThisTick, _seen, _unnamed, _encodeFail;
        private static readonly Dictionary<int, float> _shopNext = new Dictionary<int, float>();

        public static void Tick()
        {
            var session = Session.Instance;
            if (session == null || !session.Connected) return;
            bool host = session.Role == SessionRole.Host;

            if (_sweep == null || _cursor >= _sweep.Length)
            {
                if (_sweep != null) Perf.Run("mirror.finish", () => FinishSweep(session, host));
                if (Time.realtimeSinceStartup < _nextPoll) return;
                _nextPoll = Time.realtimeSinceStartup + PollInterval;
                global::SaveableObject[] all = null;
                Perf.Run("mirror.find", () => { all = ObjectRegistry.AllSaveables(); });
                if (all == null || all.Length == 0)
                {
                    Log.Info("mirror: no saveables visible yet");
                    _sweep = null;
                    return;
                }
                _sweep = all;
                _cursor = 0;
                _sentThisTick = _seen = _unnamed = _encodeFail = 0;
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double budgetTicks = SliceBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0;
            int done = 0;
            while (_cursor < _sweep.Length && done < SliceMaxObjects)
            {
                var so = _sweep[_cursor++];
                done++;
                if (so == null) continue;
                long o0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { ProcessOne(so, host, session); } catch (Exception e) { Log.Debug("mirror object: " + e.Message); }
                double oms = (System.Diagnostics.Stopwatch.GetTimestamp() - o0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (oms > _slowestMs) { _slowestMs = oms; try { _slowestName = so.gameObject.name + "/" + so.GetUniqueIdentifier(); } catch { } }
                if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budgetTicks) break;
            }
            if (_cursor >= _sweep.Length) { Perf.Run("mirror.finish", () => FinishSweep(session, host)); _sweep = null; }
        }

        private static void ProcessOne(global::SaveableObject so, bool host, Session session)
        {
            int sentThisTick = 0, seen = 0, unnamed = 0, encodeFail = 0;
            try
            {
                // Shops capture with a Debug.Log each time and are pushed on change anyway.
                if (so.GetComponent<global::ShopSpeaker>() != null)
                {
                    int id = so.GetInstanceID();
                    if (_shopNext.TryGetValue(id, out var due) && Time.realtimeSinceStartup < due) return;
                    _shopNext[id] = Time.realtimeSinceStartup + ShopPollInterval;
                }
                ProcessBody(so, host, session, ref sentThisTick, ref seen, ref unnamed, ref encodeFail);
            }
            finally
            {
                _sentThisTick += sentThisTick; _seen += seen; _unnamed += unnamed; _encodeFail += encodeFail;
            }
        }

        private static void ProcessBody(global::SaveableObject so, bool host, Session session,
            ref int sentThisTick, ref int seen, ref int unnamed, ref int encodeFail)
        {
                if (so == null) return;
                if (!TryGetKey(so, out var key)) { unnamed++; return; }
                // Duplicate identifiers (261 in the shipped data): only the object the registry
                // resolves the key to is mirrored. The others sent their own states under the
                // same key, so the peer's object flipped between them twice a second.
                if (ObjectRegistry.Instance.TryResolve(key, out var owner) && owner != null && owner != so.gameObject)
                {
                    _byKey[key] = owner;
                    return;
                }
                if (IsPerPlayer(so)) return;   // each player's own loot
                if (IsTypedOnly(so)) return;   // synced exclusively via typed fast paths (avoids RestoreState double-animations)
                seen++;

                object state;
                try { state = so.CaptureState(); }
                catch { return; }
                // Untouched objects capture null (the save only stores changes). They are
                // still tracked, so the moment one changes it is a change - not a first
                // sight - and gets sent. Null itself is never sent.
                if (state == null)
                {
                    unnamed++;
                    if (!_hashes.ContainsKey(key)) _hashes[key] = NullHash;
                    else if (_hashes[key] != NullHash) _hashes[key] = NullHash;
                    return;
                }

                byte[] body;
                // Breadcrumb: a native fault inside IL2CPP reflection cannot be caught, so
                // record which object is being encoded the first time it is seen.
                if (!_hashes.ContainsKey(key)) Crumb(key);
                try { body = StateCodec.Encode(state); }
                catch (Exception e)
                {
                    encodeFail++;
                    if (encodeFail <= 3) Log.Warn("mirror encode: " + e.GetType().Name + ": " + e.Message);
                    return;
                }

                ulong h = Hash(body);
                bool known = _hashes.TryGetValue(key, out var old);
                if (known && old == h) return;
                _hashes[key] = h;
                if (_localOnlyUntil.TryGetValue(key, out var lo))
                {
                    if (Time.realtimeSinceStartup < lo) return;   // local streaming change: baseline only
                    _localOnlyUntil.Remove(key);
                }

                if (!host)
                {
                    // Client: first sight is a baseline; a peer-written object settling is
                    // not a player action; only genuine local changes are forwarded.
                    if (!known) { _newSightings++; return; }
                    if (IsQuiet(key) || !ShouldForward(key)) return;
                }

                var w = new Writer();
                w.Str(key).U16((ushort)Math.Min(body.Length, ushort.MaxValue));
                w.Bytes(body);
                var payload = w.ToArray();
                if (payload.Length > Proto.MaxPayload) _oversize++;
                // Large states (chests, shops) travel as reliable fragments.
                session.SendReliableLarge(host ? PacketKind.ObjectState : PacketKind.ObjectRequest, payload);
                if (host) _sent++; else _requests++;
                _sentPerKey[key] = _sentPerKey.TryGetValue(key, out var spk) ? spk + 1 : 1;
                sentThisTick++;
        }

        private static void FinishSweep(Session session, bool host)
        {
            int sentThisTick = _sentThisTick, seen = _seen, unnamed = _unnamed, encodeFail = _encodeFail;
            // Client: objects appeared that the host may have sent before we had them
            // loaded (region streaming). Ask for a fresh pass, rate-limited.
            if (!host && _firstPass) { _firstPass = false; _newSightings = 0; }
            if (!host && _newSightings > 0 && Time.realtimeSinceStartup >= _nextResyncAsk)
            {
                _nextResyncAsk = Time.realtimeSinceStartup + 3f;
                Log.Info($"mirror: {_newSightings} newly loaded objects - asking host for their state");
                _newSightings = 0;
                session.SendReliable(PacketKind.ResyncRequest, new byte[] { 1 });
            }

            if (sentThisTick > 0 || encodeFail > 0)
                Log.Info($"mirror: {sentThisTick} sent / {seen} seen / {unnamed} unnamed / " +
                         $"{encodeFail} encode-fail ({_sent} total, {_hashes.Count} known, {_oversize} oversized)");

            ScanTyped(session);
        }

        // ------------------------------------------------------------------ typed fast paths
        //
        // Some components capture null before their own Awake/Start has built the state
        // object, so the generic mirror cannot drive them on the peer. Doors and lifts are
        // the ones players notice, and both expose a small explicit state, so replicate
        // those directly as well. The generic mirror still covers everything else.

        // LiftMove (3) was the old motion replay; it is retired (ignored on receipt).
        private enum TypedKind : byte { DoorUltimate = 0, Door = 1, Lift = 2, LiftMove = 3, LiftActivate = 4, DoorVert = 5, DoorBridge = 6 }

        // Lift typed value: liftPosition (0..3) | LiftMovingBit. liftPosition already uses 2/3
        // for MovingDown/MovingUp, so the moving flag needs its own bit.
        private const int LiftMovingBit = 4;

        private static readonly Dictionary<string, int> _typed = new Dictionary<string, int>(256, StringComparer.Ordinal);

        /// <summary>
        /// Records a typed state and decides whether to send it. Host: every change.
        /// Client: only changes after first sight that are not the settle of a peer write.
        /// </summary>
        private static bool TypedChanged(Session session, string key, int value)
        {
            bool known = _typed.TryGetValue(key, out var old);
            if (known && old == value) return false;
            _typed[key] = value;
            if (session.Role == SessionRole.Host) return true;
            if (!known || IsQuiet(key)) return false;
            // Typed doors only ever change through a player (a peer write is quiet), so the
            // volatile churn rule must not lock out a door that is used often.
            return true;
        }

        private static void SendTyped(Session session, Writer w) =>
            session.SendReliable(session.Role == SessionRole.Host ? PacketKind.WorldEvent : PacketKind.WorldEventRequest,
                                 w.ToArray());

        // Doors and lifts only change with region streaming; their lists and keys are cached
        // (refreshed every 3 s) instead of four full object searches per sweep.
        private static (global::DoorsUltimate c, string key)[] _doorsU = new (global::DoorsUltimate, string)[0];
        private static (global::DoorsDelusion c, string key)[] _doorsP = new (global::DoorsDelusion, string)[0];
        private static (global::DoorsDelusionVert c, string key)[] _doorsV = new (global::DoorsDelusionVert, string)[0];
        private static (global::DoorsDelusionBridge c, string key)[] _doorsB = new (global::DoorsDelusionBridge, string)[0];
        private static (global::DelusionLift c, string key)[] _liftsC = new (global::DelusionLift, string)[0];
        private static float _typedListsAt = -10f;
        private static string _typedListsScene;

        private static (T, string)[] KeyedList<T>(string scene) where T : Component
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            var list = new List<(T, string)>(all != null ? all.Length : 0);
            if (all != null)
                foreach (var c in all)
                {
                    if (c == null || !c.gameObject.scene.IsValid()) continue;
                    var so = c.GetComponent<global::SaveableObject>();
                    string id = null;
                    try { id = so != null ? so.GetUniqueIdentifier() : null; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add((c, scene + "|" + id));
                }
            return list.ToArray();
        }

        private static void RefreshTypedLists(string scene)
        {
            if (Time.realtimeSinceStartup - _typedListsAt < 3f && scene == _typedListsScene) return;
            _typedListsAt = Time.realtimeSinceStartup;
            _typedListsScene = scene;
            _doorsU = KeyedList<global::DoorsUltimate>(scene);
            _doorsP = KeyedList<global::DoorsDelusion>(scene);
            _doorsV = KeyedList<global::DoorsDelusionVert>(scene);
            _doorsB = KeyedList<global::DoorsDelusionBridge>(scene);
            _liftsC = KeyedList<global::DelusionLift>(scene);
        }

        private static void ScanTyped(Session session)
        {
            var scene = ObjectRegistry.Instance.CurrentScene;
            RefreshTypedLists(scene);

            foreach (var (d, key) in _doorsU)
                {
                    if (d == null) continue;
                    int st;
                    try { st = (int)d.GetSt8(); } catch { continue; }
                    if (IsQuiet(key) && (st == (int)global::DoorState.midOpen || st == (int)global::DoorState.midClose)) continue;
                    if (!TypedChanged(session, key, st)) continue;
                    var w = new Writer();
                    w.U8((byte)TypedKind.DoorUltimate).Str(key).I32(st);
                    SendTyped(session, w);
                }

            foreach (var (d, key) in _doorsP)
                {
                    if (d == null) continue;
                    int open;
                    try
                    {
                        if (IsQuiet(key) && d.isAnimating) continue;
                        open = d.isOpen ? 1 : 0;
                    }
                    catch { continue; }
                    if (!TypedChanged(session, key, open)) continue;
                    var w = new Writer();
                    w.U8((byte)TypedKind.Door).Str(key).I32(open);
                    SendTyped(session, w);
                }

            foreach (var (d, key) in _doorsV)
                {
                    if (d == null) continue;
                    int open;
                    try
                    {
                        if (IsQuiet(key) && d.IsAnimating()) continue;
                        open = d.isOpen ? 1 : 0;
                    }
                    catch { continue; }
                    if (!TypedChanged(session, key, open)) continue;
                    var w = new Writer();
                    w.U8((byte)TypedKind.DoorVert).Str(key).I32(open);
                    SendTyped(session, w);
                }

            foreach (var (d, key) in _doorsB)
                {
                    if (d == null) continue;
                    int open;
                    try
                    {
                        if (IsQuiet(key) && d.isAnimating) continue;
                        open = d.isOpen ? 1 : 0;
                    }
                    catch { continue; }
                    if (!TypedChanged(session, key, open)) continue;
                    var w = new Writer();
                    w.U8((byte)TypedKind.DoorBridge).Str(key).I32(open);
                    SendTyped(session, w);
                }

            foreach (var (l, key) in _liftsC)
                {
                    if (l == null) continue;
                    int packed;
                    try { packed = LiftValue(l); }
                    catch { continue; }
                    // Lifts travel as transitions (LiftActivate, both roles) plus the host's
                    // settled position. The scan only provides that settled value for late
                    // joins / resyncs; a client never forwards scanned lift state.
                    if (session.Role != SessionRole.Host) { _typed[key] = packed; continue; }
                    bool known = _typed.TryGetValue(key, out var old);
                    _typed[key] = packed;
                    if (known && old == packed) continue;
                    if ((packed & LiftMovingBit) != 0 || packed >= 2) continue;   // moving: wait for the settle
                    var w = new Writer();
                    w.U8((byte)TypedKind.Lift).Str(key).I32(packed);
                    SendTyped(session, w);
                }
        }

        private static int LiftValue(global::DelusionLift l) =>
            ((int)l.liftPosition) | (l.isMoving ? LiftMovingBit : 0);

        /// <summary>Client: apply a typed door/lift transition.</summary>
        public static void ApplyTyped(byte[] payload)
        {
            var r = new Reader(payload);
            var kind = (TypedKind)r.U8();
            string key = r.Str();
            int value = r.I32();

            var reg = ObjectRegistry.Instance;
            if (!reg.TryResolve(key, out var go) || go == null)
            {
                for (int n = 1; n <= 4 && go == null; n++) reg.TryResolve(key + "#" + n, out go);
            }
            if (go == null) { _rejected++; _lastReject = "typed unknown '" + key + "'"; return; }

            // Peer-written: do not echo the local settle back, and remember the value so the
            // next scan does not see it as a local change.
            MarkQuiet(key, 4f);
            _typed[key] = value;

            LoopGuard.Apply(() =>
            {
                try
                {
                    switch (kind)
                    {
                        case TypedKind.DoorUltimate:
                        {
                            var d = go.GetComponent<global::DoorsUltimate>();
                            if (d == null) break;
                            // Use the game's own transition rather than writing the private
                            // state field: DoorState is driven by Update/saveData, and the
                            // public API keeps the animator, navmesh link and save record in
                            // step on this side.
                            ApplyDoor(d, (global::DoorState)value);
                            break;
                        }
                        case TypedKind.Door:
                        {
                            var d = go.GetComponent<global::DoorsDelusion>();
                            if (d == null || d.isAnimating) break;
                            if (value != 0 && d.IsClosed())
                                d.OpenDoor(PlayerSync.PuppetTransform ?? GameRefs.Player?.transform);
                            else if (value == 0 && !d.IsClosed())
                                d.StartClose();
                            break;
                        }
                        case TypedKind.DoorVert:
                        {
                            var d = go.GetComponent<global::DoorsDelusionVert>();
                            if (d == null) break;
                            bool wantOpen = value != 0;
                            if (d.isOpen != wantOpen && !d.IsAnimating())
                                d.SetDoor(wantOpen, false);
                            break;
                        }
                        case TypedKind.DoorBridge:
                        {
                            var d = go.GetComponent<global::DoorsDelusionBridge>();
                            if (d == null || d.isAnimating) break;
                            if (value != 0 && !d.isOpen)
                            {
                                d.UnlockDoor();
                                d.StartOpen();
                            }
                            break;
                        }
                        case TypedKind.Lift:
                        {
                            var l = go.GetComponent<global::DelusionLift>();
                            if (l != null) ApplyLiftSettle(l, value);
                            break;
                        }
                        case TypedKind.LiftActivate:
                        {
                            var l = go.GetComponent<global::DelusionLift>();
                            if (l != null) ApplyLiftActivate(l, key, value);
                            break;
                        }
                        case TypedKind.LiftMove:
                            break;   // retired motion replay
                    }
                    // Keep the generic SaveableObject hash in step so ProcessBody does not echo
                    // an ObjectState/RestoreState back on top of the typed transition.
                    var so = go.GetComponent<global::SaveableObject>();
                    if (so != null)
                    {
                        try
                        {
                            var st = so.CaptureState();
                            _hashes[key] = st == null ? NullHash : Hash(StateCodec.Encode(st));
                        }
                        catch { }
                    }
                    _applied++;
                    _lastApply = "typed " + key;
                }
                catch (Exception e)
                {
                    _rejected++;
                    _lastReject = $"typed '{key}': {e.Message}";
                }
            });
        }

        private static int _crumbed;
        private static void Crumb(string key)
        {
            // Trace the first few hundred first-sight encodes in detail; after that the codec
            // has seen every shape in play.
            // File breadcrumbs cost tens of ms per encode; only when crash-hunting (-ddmp-trace).
            StateCodec.Trace = Bootstrap.TraceCodec && _crumbed++ < 400;
            if (StateCodec.Trace) StateCodec.Crumb("key " + key);
        }

        // ------------------------------------------------------------------ lifts
        //
        // A lift ride is replicated as its transition: the peer runs the same private
        // ActivateLift(newPos) - door close animation, doorClipping wall, Moving* position,
        // audio - and its own carriage motion. The host's EndLiftMovement is the settled
        // truth (PositionInstant on a peer that ended up elsewhere).

        [ThreadStatic] private static int _leverCtx;   // 0 none, 1 top lever, 2 bottom lever
        internal static void EnterLever(int lever) => _leverCtx = lever;
        internal static void ExitLever() => _leverCtx = 0;

        /// <summary>Lifts whose current ride was started by the peer: their journey-end
        /// facts/tasks arrive through FactSync/QuestSync, not from this side.</summary>
        private static readonly HashSet<int> _remoteRide = new HashSet<int>();
        internal static bool IsRemoteRide(global::DelusionLift l) => l != null && _remoteRide.Contains(l.GetInstanceID());

        private static string LiftKey(global::DelusionLift l)
        {
            var so = l.GetComponent<global::SaveableObject>();
            string id = null;
            try { id = so != null ? so.GetUniqueIdentifier() : null; } catch { }
            return string.IsNullOrEmpty(id) ? null : ObjectRegistry.Instance.CurrentScene + "|" + id;
        }

        /// <summary>ActivateLift succeeded locally because a player used the lift.</summary>
        internal static void OnLiftActivated(global::DelusionLift l, global::DelusionLift.LiftPosition newPos)
        {
            if (LoopGuard.ApplyingRemote || l == null) return;
            var session = Session.Instance;
            if (session == null || !session.Connected) return;
            var key = LiftKey(l);
            if (key == null) return;
            var w = new Writer();
            w.U8((byte)TypedKind.LiftActivate).Str(key).I32((int)newPos | (_leverCtx << 4));
            SendTyped(session, w);
        }

        /// <summary>Host: a ride ended here - broadcast the settled position.</summary>
        internal static void OnLiftEnded(global::DelusionLift l)
        {
            if (l == null) return;
            _remoteRide.Remove(l.GetInstanceID());
            var session = Session.Instance;
            if (session == null || !session.Connected || session.Role != SessionRole.Host) return;
            var key = LiftKey(l);
            if (key == null) return;
            int packed;
            try { packed = LiftValue(l); } catch { return; }
            if ((packed & LiftMovingBit) != 0 || packed >= 2) return;
            _typed[key] = packed;
            var w = new Writer();
            w.U8((byte)TypedKind.Lift).Str(key).I32(packed);
            SendTyped(session, w);
        }

        private static void ApplyLiftActivate(global::DelusionLift l, string key, int value)
        {
            var pos = (global::DelusionLift.LiftPosition)(value & 0xF);
            int lever = (value >> 4) & 0xF;
            if (pos != global::DelusionLift.LiftPosition.Top && pos != global::DelusionLift.LiftPosition.Bot) return;
            // Runs inside LoopGuard: MissingKeyItem and TriggerFactsAndTasks are bypassed by
            // their prefixes, and the ActivateLift postfix does not echo.
            bool ok = l.ActivateLift(pos);
            if (ok)
            {
                _remoteRide.Add(l.GetInstanceID());
                try
                {
                    if (lever == 1 && l.leverTop != null) l.leverTop.PullLever();
                    else if (lever == 2 && l.leverBot != null) l.leverBot.PullLever();
                }
                catch (Exception e) { Log.Debug("lift lever visual: " + e.Message); }
                return;
            }
            // Refused (already moving / already there / disabled). The host tells the client
            // where its lift really is, so the client's own ride gets corrected.
            var session = Session.Instance;
            if (session != null && session.Role == SessionRole.Host)
            {
                int packed;
                try { packed = LiftValue(l); } catch { return; }
                if ((packed & LiftMovingBit) == 0 && packed < 2)
                {
                    var w = new Writer();
                    w.U8((byte)TypedKind.Lift).Str(key).I32(packed);
                    SendTyped(session, w);
                }
            }
        }

        /// <summary>The host's settled lift position. A peer still riding toward the same
        /// end is left to arrive by itself; anything else is snapped.</summary>
        private static void ApplyLiftSettle(global::DelusionLift l, int value)
        {
            if ((value & LiftMovingBit) != 0) return;
            var pos = (global::DelusionLift.LiftPosition)(value & 3);
            if (pos != global::DelusionLift.LiftPosition.Top && pos != global::DelusionLift.LiftPosition.Bot) return;
            bool moving = l.isMoving;
            var cur = l.liftPosition;
            if (!moving && cur == pos) return;
            if (cur == global::DelusionLift.LiftPosition.MovingUp && pos == global::DelusionLift.LiftPosition.Top) return;
            if (cur == global::DelusionLift.LiftPosition.MovingDown && pos == global::DelusionLift.LiftPosition.Bot) return;
            bool wasMid = moving || cur == global::DelusionLift.LiftPosition.MovingUp || cur == global::DelusionLift.LiftPosition.MovingDown;
            l.PositionInstant(pos);
            _remoteRide.Remove(l.GetInstanceID());
            if (wasMid)
            {
                // PositionInstant skips EndLiftMovement's cleanup.
                try { if (l.doorClipping != null) l.doorClipping.SetActive(false); } catch { }
                try { if (l.liftAudio != null) l.liftAudio.Stop(); } catch { }
            }
        }

        /// <summary>
        /// Drive a door to the peer's state through its own methods, so animation, sound,
        /// nav link and save record all follow. Transitional states map to their end state.
        /// </summary>
        private static void ApplyDoor(global::DoorsUltimate d, global::DoorState target)
        {
            var cur = d.GetSt8();
            bool wantOpen = target == global::DoorState.open || target == global::DoorState.midOpen;
            bool isOpen = cur == global::DoorState.open || cur == global::DoorState.midOpen;
            if (target == global::DoorState.smashed)
            {
                if (cur != global::DoorState.smashed) d.SmashDoor(false);
                return;
            }
            if (wantOpen)
            {
                if (isOpen) return;
                // Swing away from whoever opened it: the partner's avatar if we have one.
                var user = PlayerSync.PuppetTransform ?? GameRefs.Player?.transform;
                if (cur == global::DoorState.locked || cur == global::DoorState.midPick)
                {
                    d.UnlockDoor(true, false);
                    // Some doors only unlock here; the partner's door ended up open.
                    var after = d.GetSt8();
                    if (after == global::DoorState.unlocked || after == global::DoorState.locked) d.OpenDoor(user, true);
                    return;
                }
                d.OpenDoor(user, true);
                return;
            }
            if (target == global::DoorState.unlocked || target == global::DoorState.midClose)
            {
                if (isOpen) d.CloseDoor();
                else if (cur == global::DoorState.locked) d.UnlockDoor(false, false);
                return;
            }
            if (target == global::DoorState.locked && cur != global::DoorState.locked)
            {
                if (isOpen) d.CloseDoor();
                d.st8 = target;   // re-lock (Traverse cannot see IL2CPP fields)
            }
        }

        private static bool TryGetKey(global::SaveableObject so, out string key)
        {
            key = null;
            string id = null;
            try { id = so.GetUniqueIdentifier(); }
            catch { }
            if (string.IsNullOrEmpty(id)) return false;
            // Address by (scene, identifier) only: the registry resolves the plain key to the
            // first object with that identifier on both peers, so duplicate identifiers (261
            // of them in the shipped data) still name the same object on both sides.
            key = ObjectRegistry.Instance.CurrentScene + "|" + id;
            _byKey[key] = so.gameObject;
            return true;
        }

        // ------------------------------------------------------------------ client

        private static int _storedUnloaded;
        private static float _lastForcedRebuild;
        public static int StoredUnloadedCount => _storedUnloaded;

        /// <summary>Writes a peer's state for an object that is not loaded here into
        /// DelusionSave.loadedState[uniqueId] (SaveableObject.OnEnable restores from exactly
        /// that entry, and saving persists it).</summary>
        private static bool StoreForUnloaded(string key, byte[] body)
        {
            int bar = key.IndexOf('|');
            if (bar < 0 || key.IndexOf("|#path/", StringComparison.Ordinal) >= 0) return false;
            string uid = key.Substring(bar + 1);
            int hash = uid.IndexOf('#');
            if (hash >= 0) return false;   // duplicate-id variants cannot be addressed by id
            if (uid.Length == 0) return false;
            try
            {
                var save = GameRefs.Save;
                var table = save != null ? save.GetLoadedState() : null;
                if (table == null) return false;
                var state = StateCodec.Build(body);
                if (state == null) return false;
                table[uid] = state;
                _storedUnloaded++;
                _pendingUnloaded[uid] = body;
                _pendingDirty = true;
                _hashes[key] = Hash(body);      // our view of it now matches the peer's
                _lastApply = "stored-for-unloaded " + key;
                if (_storedUnloaded <= 5 || _storedUnloaded % 50 == 0)
                    Log.Info($"mirror: {key} is not loaded here - kept in the save's loaded state ({_storedUnloaded} so far)");
                return true;
            }
            catch (Exception e)
            {
                Log.Warn($"mirror: could not keep unloaded '{key}': {e.Message}");
                return false;
            }
        }

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            string key = ""; int len = 0;
            try
            {
                key = r.Str();
                len = r.U16();
            }
            catch (Exception e)
            {
                _rejected++;
                _lastReject = "parse " + e.GetType().Name + ": " + e.Message;
                return;
            }
            byte[] body;
            try { body = r.Bytes(len); }
            catch (Exception e)
            {
                _rejected++;
                _lastReject = $"truncated body key='{key}' len={len} remaining={r.Remaining}";
                return;
            }

            if (IsPerPlayerBody(body)) return;   // an older peer's chest/pickup: each player's own

            var reg = ObjectRegistry.Instance;
            if (!reg.TryResolve(key, out var go) || go == null)
            {
                // Duplicate identifiers register extra objects under "key#n"; try the first
                // suffixed variant before giving up.
                bool found = false;
                for (int n = 1; n <= 4 && !found; n++)
                    found = reg.TryResolve(key + "#" + n, out go) && go != null;
                if (!found)
                {
                    // Keys made while a peer was still in the boot scene carry that scene's
                    // name; and a region that has just streamed in may not be in the registry
                    // yet. Retry with this scene's name and a fresh registry before deciding
                    // the object is not loaded here.
                    int bar0 = key.IndexOf('|');
                    string local = bar0 >= 0 ? reg.CurrentScene + key.Substring(bar0) : key;
                    found = reg.TryResolve(local, out go) && go != null;
                    if (!found && Time.realtimeSinceStartup - _lastForcedRebuild > 0.25f)
                    {
                        _lastForcedRebuild = Time.realtimeSinceStartup;
                        ObjectRegistry.InvalidateSaveables();
                        reg.Rebuild(reg.CurrentScene);
                        found = reg.TryResolve(local, out go) && go != null;
                    }
                    if (found) key = local;
                }
                if (!found)
                {
                    // The object lives in a region this machine has not loaded (the players
                    // are apart). Keep the change where the game itself keeps the state of
                    // unloaded objects: DelusionSave's loaded-state table, which a saveable
                    // restores from when its region streams in, and which the next save writes.
                    if (StoreForUnloaded(key, body)) return;
                    _rejected++;
                    _lastReject = $"unknown '{key}' registry={reg.Count} scene='{reg.CurrentScene}' plain={reg.TryResolve(key, out _)}";
                    if (_rejected <= 3) Log.Error(_lastReject);
                    return;
                }
            }

            ApplyResolved(key, go, body);
        }

        /// <summary>Applies a peer's state to a resolved local object.</summary>
        private static void ApplyResolved(string key, GameObject go, byte[] body)
        {
            var so = go.GetComponent<global::SaveableObject>();
            if (so == null)
            {
                _rejected++;
                _lastReject = $"no SaveableObject for '{key}'";
                if (_rejected <= 3) Log.Error(_lastReject);
                return;
            }

            // Objects handled exclusively by typed fast paths (lifts, vertical grates) must not
            // also run RestoreState, which replays animations or snaps mid-motion.
            if (IsTypedOnly(so))
            {
                _hashes[key] = Hash(body);
                MarkQuiet(key);
                return;
            }

            // Idempotency guard: if this object already holds the exact same serialized state
            // (e.g. from a typed transition, a synced fact trigger, or a host echo of our own
            // action), skip RestoreState so it does not replay close/open/pull animations twice.
            ulong incomingHash = Hash(body);
            try
            {
                var curState = so.CaptureState();
                if (curState != null && Hash(StateCodec.Encode(curState)) == incomingHash)
                {
                    _hashes[key] = incomingHash;
                    MarkQuiet(key);
                    return;
                }
            }
            catch { }

            // Avoid interrupting or replaying an active door animation via RestoreState:
            // typed door sync already drives open/close transitions smoothly.
            if (IsDoorAnimating(go))
            {
                _hashes[key] = incomingHash;
                MarkQuiet(key);
                return;
            }

            // Rebuild the sender's state object here and let the game's RestoreState apply
            // it. The local capture is not used: an untouched object captures null, which is
            // precisely the common case (the peer changed something this side never did).
            Il2CppSystem.Object state;
            try { state = StateCodec.Build(body); }
            catch (Exception e)
            {
                _rejected++;
                _lastReject = $"build '{key}': {e.GetType().Name}: {e.Message}";
                if (_rejected <= 5) Log.Error(_lastReject);
                return;
            }
            if (state == null)
            {
                // The sender's object went back to its default (e.g. a respawned pickup).
                // Day/rest respawns run on both peers from the synced clock.
                _lastReject = $"null state '{key}'";
                return;
            }

            LoopGuard.Apply(() =>
            {
                try
                {
                    if (StateCodec.BuildTrace) StateCodec.Crumb("restore " + key);
                    so.RestoreState(state);
                    if (StateCodec.BuildTrace) StateCodec.Crumb("restored");
                    _applied++;
                    _lastApply = key;
                    MarkQuiet(key);
                    // Re-baseline to what this side now holds so it is not sent back.
                    try { _hashes[key] = Hash(StateCodec.Encode(so.CaptureState())); } catch { }
                }
                catch (Exception e)
                {
                    _rejected++;
                    _lastReject = $"restore '{key}': {e.GetType().Name}: {e.Message}";
                    if (_rejected <= 3) Log.Error(_lastReject);
                }
            });
                }

        // ---- per-player loot: chests and loose pickups are each player's own. Mirrored,
        // the first player emptied them for both (and a quest item found by one was gone for
        // the other). Each player loots every chest and pickup once; the state lives in each
        // player's own save. Breakable crates stay shared (the partner's copy rolls its own loot).
        private static readonly Dictionary<int, bool> _perPlayer = new Dictionary<int, bool>(512);
        private static readonly Dictionary<int, bool> _typedOnly = new Dictionary<int, bool>(512);

        internal static bool IsTypedOnly(global::SaveableObject so)
        {
            if (so == null) return false;
            int id = so.GetInstanceID();
            if (_typedOnly.TryGetValue(id, out var v)) return v;
            try { v = so.GetComponent<global::DelusionLift>() != null || so.GetComponent<global::DoorsDelusionVert>() != null; }
            catch { v = false; }
            _typedOnly[id] = v;
            return v;
        }

        private static bool IsDoorAnimating(GameObject go)
        {
            if (go == null) return false;
            try
            {
                var dd = go.GetComponent<global::DoorsDelusion>();
                if (dd != null && dd.isAnimating) return true;
                var du = go.GetComponent<global::DoorsUltimate>();
                if (du != null)
                {
                    var st = du.GetSt8();
                    if (st == global::DoorState.midOpen || st == global::DoorState.midClose) return true;
                }
                var db = go.GetComponent<global::DoorsDelusionBridge>();
                if (db != null && db.isAnimating) return true;
            }
            catch { }
            return false;
        }

        internal static bool IsPerPlayer(global::SaveableObject so)
        {
            if (so == null) return false;
            int id = so.GetInstanceID();
            if (_perPlayer.TryGetValue(id, out var v)) return v;
            try { v = so.GetComponent<global::ItemChest>() != null || so.GetComponent<global::ItemPickup>() != null; }
            catch { v = false; }
            _perPlayer[id] = v;
            return v;
        }

        /// <summary>For objects that are not loaded: an encoded state is keyed by component type.</summary>
        internal static bool IsPerPlayerBody(byte[] body)
        {
            if (body == null) return false;
            try
            {
                var d = StateCodec.Describe(body, 64);
                return d.StartsWith("{\"ItemChest\"", StringComparison.Ordinal) || d.StartsWith("{\"ItemPickup\"", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static readonly Dictionary<string, int> _sentPerKey = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Rig: the objects sent most often (state that keeps changing).</summary>
        public static IEnumerable<string> TopSent(int n)
        {
            var l = new List<KeyValuePair<string, int>>(_sentPerKey);
            l.Sort((x, y) => y.Value.CompareTo(x.Value));
            for (int i = 0; i < l.Count && i < n; i++)
            {
                string name = _byKey.TryGetValue(l[i].Key, out var go) && go != null ? go.name : "?";
                yield return $"{l[i].Value}x {name} {l[i].Key}";
            }
        }

        private static readonly Dictionary<string, float> _localOnlyUntil = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>
        /// The object's state is about to change for a reason local to this player (an
        /// encounter zone switching its spawners with the local player's position): record
        /// the new state as the baseline instead of sending it. Sent, it turned the partner's
        /// enemies off and back on - respawned at full health in the middle of a fight.
        /// </summary>
        internal static void LocalOnly(global::SaveableObject so, float seconds = 5f)
        {
            if (so == null || !TryGetKey(so, out var key)) return;
            _localOnlyUntil[key] = Time.realtimeSinceStartup + seconds;
        }

        /// <summary>Join: this side now holds what the host holds for that object.</summary>
        internal static void Rebaseline(string key, global::SaveableObject so)
        {
            try { var st = so.CaptureState(); _hashes[key] = st == null ? NullHash : Hash(StateCodec.Encode(st)); } catch { }
            MarkQuiet(key);
        }

        /// <summary>Join: treat every object as first sight again (a baseline, never sent).</summary>
        internal static void ForceRebaselineAll()
        {
            if (Session.Instance?.Role == SessionRole.Client) { _hashes.Clear(); _firstPass = true; }
        }

        /// <summary>Join: the host's world is the truth; our own pending changes are dropped.</summary>
        internal static void DiscardPending()
        {
            if (_pendingUnloaded.Count == 0) return;
            Log.Info($"join: {_pendingUnloaded.Count} pending change(s) discarded (the host's world wins)");
            _pendingUnloaded.Clear();
            _pendingDirty = true;
        }

        // ---- changes for objects not loaded here, applied when they appear
        private static readonly Dictionary<string, byte[]> _pendingUnloaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static float _nextPendingCheck;
        private static int _pendingApplied;
        public static string PendingInfo => $"pendingUnloaded={_pendingUnloaded.Count} appliedOnArrival={_pendingApplied} storedInSave={_storedUnloaded}";

        // ---- persistence: the pending list survives quitting. It is stored next to the
        // game's own saves, one file per save, keyed by object identifier (scene-agnostic),
        // and reloaded whenever that save is loaded - so a change the partner made in a
        // region this player never visited during the session is still applied the first time
        // the region loads, in any later session, co-op or solo.
        private static bool _pendingDirty;
        private static float _nextPendingSave;
        private static string _pendingFileLoaded;

        private static string PendingFile()
        {
            var save = GameRefs.Save;
            if (save == null) return null;
            string name = "save";
            int slot = 0;
            try { name = string.IsNullOrEmpty(save.saveGameName) ? "save" : save.saveGameName; slot = (int)save.saveSlot; } catch { }
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return System.IO.Path.Combine(Application.persistentDataPath, $"DelusionMP_pending_{name}_{slot}.bin");
        }

        private static string Uid(string key)
        {
            int bar = key.IndexOf('|');
            return bar >= 0 ? key.Substring(bar + 1) : key;
        }

        private static void LoadPendingFile(string path)
        {
            _pendingFileLoaded = path;
            if (!System.IO.File.Exists(path)) return;
            try
            {
                using (var br = new System.IO.BinaryReader(System.IO.File.OpenRead(path)))
                {
                    if (br.ReadInt32() != 0x444D5031) return;   // "DMP1"
                    int n = br.ReadInt32();
                    int added = 0;
                    for (int i = 0; i < n; i++)
                    {
                        string uid = br.ReadString();
                        int len = br.ReadInt32();
                        var body = br.ReadBytes(len);
                        if (!_pendingUnloaded.ContainsKey(uid)) { _pendingUnloaded[uid] = body; added++; }
                    }
                    if (added > 0) Log.Info($"mirror: {added} change(s) from earlier sessions waiting for their region ({path})");
                }
            }
            catch (Exception e) { Log.Warn("mirror: pending file unreadable: " + e.Message); }
        }

        private static void SavePendingFile()
        {
            var path = _pendingFileLoaded ?? PendingFile();
            if (path == null) return;
            try
            {
                if (_pendingUnloaded.Count == 0)
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    return;
                }
                var tmp = path + ".tmp";
                var dir = System.IO.Path.GetDirectoryName(tmp);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                using (var bw = new System.IO.BinaryWriter(System.IO.File.Create(tmp)))
                {
                    bw.Write(0x444D5031);
                    bw.Write(_pendingUnloaded.Count);
                    foreach (var kv in _pendingUnloaded)
                    {
                        bw.Write(kv.Key);
                        bw.Write(kv.Value.Length);
                        bw.Write(kv.Value);
                    }
                }
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(tmp, path);
            }
            catch (Exception e) { Log.Warn("mirror: could not write pending file: " + e.Message); }
        }

        /// <summary>Every frame, connected or not (cheap; real work once a second).</summary>
        public static void TickPending()
        {
            if (!GameRefs.InWorld) { return; }
            float now = Time.realtimeSinceStartup;
            if (now >= _nextPendingSave)
            {
                _nextPendingSave = now + 2f;
                var path = PendingFile();
                if (path != null && path != _pendingFileLoaded)
                {
                    // A (different) save was loaded: forget the previous save's entries.
                    if (_pendingFileLoaded != null) _pendingUnloaded.Clear();
                    LoadPendingFile(path);
                }
                if (_pendingDirty) { _pendingDirty = false; SavePendingFile(); }
            }
            ApplyPending();
        }

        /// <summary>Once a second: objects whose region has streamed in get the change the
        /// peer made while they were not loaded here (the game's own loaded-state table can be
        /// refilled from disk when a region loads, so it is not relied on alone).</summary>
        private static void ApplyPending()
        {
            if (_pendingUnloaded.Count == 0 || Time.realtimeSinceStartup < _nextPendingCheck) return;
            _nextPendingCheck = Time.realtimeSinceStartup + 1f;
            var reg = ObjectRegistry.Instance;
            List<string> done = null;
            string scene = reg.CurrentScene;
            foreach (var kv in _pendingUnloaded)
            {
                // Keyed by identifier alone: the scene name is the one in play right now.
                string key = scene + "|" + kv.Key;
                if (!reg.TryResolve(key, out var go) || go == null) continue;
                ApplyResolved(key, go, kv.Value);
                (done ??= new List<string>()).Add(kv.Key);
            }
            if (done == null) return;
            foreach (var k in done) _pendingUnloaded.Remove(k);
            _pendingApplied += done.Count;
            _pendingDirty = true;
            Log.Info($"mirror: applied {done.Count} change(s) the partner made while those objects were not loaded here");
        }

        // ------------------------------------------------------------------ util

        /// <summary>
        /// Hash of the *current* encoded state of every named saveable, computed the same way
        /// on both peers. Equal hashes mean the two games agree on the whole world surface -
        /// the strongest check the rig can make without looking at pixels.
        /// </summary>
        public static (ulong hash, int count) StateHash()
        {
            var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
            if (all == null) return (0, 0);

            var rows = new List<KeyValuePair<string, ulong>>(256);
            foreach (var so in all)
            {
                if (so == null) continue;
                string id = null;
                try { id = so.GetUniqueIdentifier(); } catch { }
                if (string.IsNullOrEmpty(id)) continue;

                byte[] body;
                try { body = StateCodec.Encode(so.CaptureState()); }
                catch { continue; }
                // The sky clock (StarOrbit) advances every frame on both peers and is
                // re-synced by ClockSync; sampling it at two instants is not divergence.
                if (body.Length < 64 && StateCodec.Describe(body, 64).Contains("\"StarOrbit\"")) continue;
                rows.Add(new KeyValuePair<string, ulong>(id, Hash(body)));
            }

            rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            ulong h = 1469598103934665603UL;
            foreach (var r in rows)
            {
                foreach (char c in r.Key) { h ^= c; h *= 1099511628211UL; }
                h ^= r.Value;
                h *= 1099511628211UL;
            }
            return (h, rows.Count);
        }

        private static readonly ulong NullHash = Hash(new byte[] { 0 });

        private static ulong Hash(byte[] data)
        {
            ulong h = 1469598103934665603UL;
            foreach (var b in data)
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionLift), "ActivateLift")]
    internal static class Patch_DelusionLift_ActivateLift
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix(global::DelusionLift __instance, global::DelusionLift.LiftPosition newPos, bool __result)
        {
            if (__result) WorldMirror.OnLiftActivated(__instance, newPos);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionLift), "EndLiftMovement")]
    internal static class Patch_DelusionLift_EndLiftMovement
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix(global::DelusionLift __instance) => WorldMirror.OnLiftEnded(__instance);
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionLift), "MissingKeyItem")]
    internal static class Patch_DelusionLift_MissingKeyItem
    {
        // A replayed ride: the partner had the key; no "need item" note here.
        [HarmonyLib.HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (!LoopGuard.ApplyingRemote) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionLift), "TriggerFactsAndTasks")]
    internal static class Patch_DelusionLift_TriggerFactsAndTasks
    {
        // Facts and tasks of a replayed ride arrive through FactSync/QuestSync.
        [HarmonyLib.HarmonyPrefix]
        static bool Prefix(global::DelusionLift __instance) =>
            !LoopGuard.ApplyingRemote && !WorldMirror.IsRemoteRide(__instance);
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionLift), nameof(global::DelusionLift.TryRecallTop))]
    internal static class Patch_DelusionLift_TryRecallTop
    {
        [HarmonyLib.HarmonyPrefix] static void Prefix() => WorldMirror.EnterLever(1);
        [HarmonyLib.HarmonyFinalizer] static void Finalizer() => WorldMirror.ExitLever();
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionLift), nameof(global::DelusionLift.TryRecallBot))]
    internal static class Patch_DelusionLift_TryRecallBot
    {
        [HarmonyLib.HarmonyPrefix] static void Prefix() => WorldMirror.EnterLever(2);
        [HarmonyLib.HarmonyFinalizer] static void Finalizer() => WorldMirror.ExitLever();
    }
}
