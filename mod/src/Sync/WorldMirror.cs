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

        public static void Tick()
        {
            var session = Session.Instance;
            if (session == null || !session.Connected) return;
            bool host = session.Role == SessionRole.Host;
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + PollInterval;

            var all = Resources.FindObjectsOfTypeAll<global::SaveableObject>();
            if (all == null || all.Length == 0)
            {
                Log.Info("mirror: no saveables visible yet");
                return;
            }

            int sentThisTick = 0;
            int seen = 0, unnamed = 0, encodeFail = 0;
            foreach (var so in all)
            {
                if (so == null) continue;
                if (!TryGetKey(so, out var key)) { unnamed++; continue; }
                seen++;

                object state;
                try { state = so.CaptureState(); }
                catch { continue; }
                // Untouched objects capture null (the save only stores changes). They are
                // still tracked, so the moment one changes it is a change - not a first
                // sight - and gets sent. Null itself is never sent.
                if (state == null)
                {
                    unnamed++;
                    if (!_hashes.ContainsKey(key)) _hashes[key] = NullHash;
                    else if (_hashes[key] != NullHash) _hashes[key] = NullHash;
                    continue;
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
                    continue;
                }

                ulong h = Hash(body);
                bool known = _hashes.TryGetValue(key, out var old);
                if (known && old == h) continue;
                _hashes[key] = h;

                if (!host)
                {
                    // Client: first sight is a baseline; a peer-written object settling is
                    // not a player action; only genuine local changes are forwarded.
                    if (!known) { _newSightings++; continue; }
                    if (IsQuiet(key) || !ShouldForward(key)) continue;
                }

                var w = new Writer();
                w.Str(key).U16((ushort)Math.Min(body.Length, ushort.MaxValue));
                w.Bytes(body);
                var payload = w.ToArray();
                if (payload.Length > Proto.MaxPayload) _oversize++;
                // Large states (chests, shops) travel as reliable fragments.
                session.SendReliableLarge(host ? PacketKind.ObjectState : PacketKind.ObjectRequest, payload);
                if (host) _sent++; else _requests++;
                sentThisTick++;
            }

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

        private enum TypedKind : byte { DoorUltimate = 0, Door = 1, Lift = 2, LiftMove = 3 }

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
            return ShouldForward(key);
        }

        private static void SendTyped(Session session, Writer w) =>
            session.SendReliable(session.Role == SessionRole.Host ? PacketKind.WorldEvent : PacketKind.WorldEventRequest,
                                 w.ToArray());

        private static void ScanTyped(Session session)
        {
            var scene = ObjectRegistry.Instance.CurrentScene;

            var doors = Resources.FindObjectsOfTypeAll<global::DoorsUltimate>();
            if (doors != null)
                foreach (var d in doors)
                {
                    if (d == null) continue;
                    var so = d.GetComponent<global::SaveableObject>();
                    string id = null;
                    try { id = so != null ? so.GetUniqueIdentifier() : null; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;
                    int st;
                    try { st = (int)d.GetSt8(); } catch { continue; }
                    string key = scene + "|" + id;
                    if (!TypedChanged(session, key, st)) continue;
                    var w = new Writer();
                    w.U8((byte)TypedKind.DoorUltimate).Str(key).I32(st);
                    SendTyped(session, w);
                }

            var plain = Resources.FindObjectsOfTypeAll<global::DoorsDelusion>();
            if (plain != null)
                foreach (var d in plain)
                {
                    if (d == null) continue;
                    var so = d.GetComponent<global::SaveableObject>();
                    string id = null;
                    try { id = so != null ? so.GetUniqueIdentifier() : null; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;
                    int open = Priv.GetField<bool>(d, "isOpen") ? 1 : 0;
                    string key = scene + "|" + id;
                    if (!TypedChanged(session, key, open)) continue;
                    var w = new Writer();
                    w.U8((byte)TypedKind.Door).Str(key).I32(open);
                    SendTyped(session, w);
                }

            var lifts = Resources.FindObjectsOfTypeAll<global::DelusionLift>();
            if (lifts != null)
                foreach (var l in lifts)
                {
                    if (l == null) continue;
                    var so = l.GetComponent<global::SaveableObject>();
                    string id = null;
                    try { id = so != null ? so.GetUniqueIdentifier() : null; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;
                    int packed;
                    try { packed = ((int)l.liftPosition) | (Priv.GetField<bool>(l, "isMoving") ? 2 : 0); }
                    catch { continue; }
                    string key = scene + "|" + id;
                    if (!TypedChanged(session, key, packed)) continue;
                    var w = new Writer();
                    w.U8((byte)TypedKind.Lift).Str(key).I32(packed);
                    SendTyped(session, w);
                }
        }

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
                            if (d == null) break;
                            if (value != 0 && d.IsClosed())
                                d.OpenDoor(PlayerSync.PuppetTransform ?? GameRefs.Player?.transform);
                            else if (value == 0 && !d.IsClosed())
                                d.StartClose();
                            break;
                        }
                        case TypedKind.Lift:
                        {
                            // Settled state only: movement itself arrives as LiftMove. If
                            // the lift rests somewhere else here, snap it.
                            var l = go.GetComponent<global::DelusionLift>();
                            if (l == null || (value & 2) != 0) break;
                            var pos = (global::DelusionLift.LiftPosition)(value & 1);
                            if (!Priv.GetField<bool>(l, "isMoving") && l.liftPosition != pos)
                                l.PositionInstant(pos);
                            break;
                        }
                        case TypedKind.LiftMove:
                        {
                            var l = go.GetComponent<global::DelusionLift>();
                            if (l == null || Priv.GetField<bool>(l, "isMoving")) break;
                            l.StartLiftMovement((global::DelusionLift.LiftPosition)value);
                            break;
                        }
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
            StateCodec.Trace = _crumbed++ < 400;
            if (StateCodec.Trace) StateCodec.Crumb("key " + key);
        }

        /// <summary>A lift started moving on this side because a player used it: replay
        /// the same movement on the peer so it animates (and carries riders) there too.</summary>
        internal static void OnLiftMove(global::DelusionLift l, global::DelusionLift.LiftPosition newPos)
        {
            if (LoopGuard.ApplyingRemote) return;
            var session = Session.Instance;
            if (session == null || !session.Connected) return;
            var so = l.GetComponent<global::SaveableObject>();
            string id = null;
            try { id = so != null ? so.GetUniqueIdentifier() : null; } catch { }
            if (string.IsNullOrEmpty(id)) return;
            var w = new Writer();
            w.U8((byte)TypedKind.LiftMove).Str(ObjectRegistry.Instance.CurrentScene + "|" + id).I32((int)newPos);
            SendTyped(session, w);
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
                if (cur == global::DoorState.locked || cur == global::DoorState.midPick) { d.UnlockDoor(true, false); return; }
                // Swing away from whoever opened it: the partner's avatar if we have one.
                var user = PlayerSync.PuppetTransform ?? GameRefs.Player?.transform;
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
                Priv.SetField(d, "st8", target);
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
                    _rejected++;
                    _lastReject = $"unknown '{key}' registry={reg.Count} scene='{reg.CurrentScene}' plain={reg.TryResolve(key, out _)}";
                    if (_rejected <= 3) Log.Error(_lastReject);
                    return;
                }
            }

            var so = go.GetComponent<global::SaveableObject>();
            if (so == null)
            {
                _rejected++;
                _lastReject = $"no SaveableObject for '{key}'";
                if (_rejected <= 3) Log.Error(_lastReject);
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

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionLift), "StartLiftMovement")]
    internal static class Patch_DelusionLift_StartLiftMovement
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix(global::DelusionLift __instance, global::DelusionLift.LiftPosition newPos)
            => WorldMirror.OnLiftMove(__instance, newPos);
    }
}
