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

        /// <summary>Force the next tick to re-send every object (late join / resync).</summary>
        public static void ForceSnapshot()
        {
            _hashes.Clear();
            _typed.Clear();
            Log.Info("world mirror: forcing full snapshot on next tick");
        }

        // ------------------------------------------------------------------ host

        public static void TickHost()
        {
            var session = Session.Instance;
            if (session?.Role != SessionRole.Host) return;
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
                // Many saveables legitimately return null until their scene/save is
                // initialised. Sending "null" is pure noise.
                if (state == null) { unnamed++; continue; }

                byte[] body;
                try { body = StateCodec.Encode(state); }
                catch (Exception e)
                {
                    encodeFail++;
                    if (encodeFail <= 3) Log.Warn("mirror encode: " + e.GetType().Name + ": " + e.Message);
                    continue;
                }

                ulong h = Hash(body);
                if (_hashes.TryGetValue(key, out var old) && old == h) continue;
                _hashes[key] = h;

                var w = new Writer();
                w.Str(key).U16((ushort)Math.Min(body.Length, ushort.MaxValue));
                w.Bytes(body);
                var payload = w.ToArray();
                if (payload.Length > Proto.MaxPayload)
                {
                    _oversize++;
                    if (_oversize <= 8)
                        Log.Warn($"mirror: {key} too large ({payload.Length} B), not sent");
                    continue;
                }
                session.SendReliable(PacketKind.ObjectState, payload);
                _sent++;
                sentThisTick++;
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

        private enum TypedKind : byte { DoorUltimate = 0, Door = 1, Lift = 2 }

        private static readonly Dictionary<string, int> _typed = new Dictionary<string, int>(256, StringComparer.Ordinal);

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
                    if (_typed.TryGetValue(key, out var old) && old == st) continue;
                    _typed[key] = st;
                    var w = new Writer();
                    w.U8((byte)TypedKind.DoorUltimate).Str(key).I32(st);
                    session.SendReliable(PacketKind.WorldEvent, w.ToArray());
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
                    if (_typed.TryGetValue(key, out var old) && old == open) continue;
                    _typed[key] = open;
                    var w = new Writer();
                    w.U8((byte)TypedKind.Door).Str(key).I32(open);
                    session.SendReliable(PacketKind.WorldEvent, w.ToArray());
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
                    if (_typed.TryGetValue(key, out var old) && old == packed) continue;
                    _typed[key] = packed;
                    var w = new Writer();
                    w.U8((byte)TypedKind.Lift).Str(key).I32(packed);
                    session.SendReliable(PacketKind.WorldEvent, w.ToArray());
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
                            var target = (global::DoorState)value;
                            if (target == global::DoorState.open || target == global::DoorState.midOpen)
                                d.UnlockDoor(true, false);
                            else
                                Priv.SetField(d, "st8", target);
                            break;
                        }
                        case TypedKind.Door:
                        {
                            var d = go.GetComponent<global::DoorsDelusion>();
                            if (d != null) Priv.SetField(d, "isOpen", value != 0);
                            break;
                        }
                        case TypedKind.Lift:
                        {
                            var l = go.GetComponent<global::DelusionLift>();
                            if (l != null)
                            {
                                l.liftPosition = (global::DelusionLift.LiftPosition)(value & 1);
                                Priv.SetField(l, "isMoving", (value & 2) != 0);
                            }
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

            object state;
            try { state = so.CaptureState(); }
            catch (Exception e)
            {
                _rejected++;
                _lastReject = $"capture '{key}': {e.Message}";
                return;
            }
            if (state == null)
            {
                _rejected++;
                _lastReject = $"capture null '{key}'";
                return;
            }

            // Patch the peer's own state object with the host's field values, then let the
            // game's RestoreState apply it. No game type is constructed on this side.
            try
            {
                StateCodec.ApplyFields(state, body);
            }
            catch (Exception e)
            {
                _rejected++;
                _lastReject = $"apply '{key}': {e.GetType().Name}: {e.Message}";
                return;
            }

            LoopGuard.Apply(() =>
            {
                try
                {
                    so.RestoreState((Il2CppSystem.Object)state);
                    _applied++;
                    _lastApply = key + " " + state.GetType().Name;
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
}
