using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Story flags. This is the spine of the whole mod: Dread Delusion stores nearly all
    /// narrative and world progression as boolean/integer facts, and doors, spawners,
    /// triggers, quest stages and cutscenes all key off them.
    ///
    /// FactDB (TypeDefIndex 2868):
    ///   SetFact(string,bool)              @ RVA 0x3A4440
    ///   SetFact(string,int)               @ RVA 0x3A46C0
    ///   ChangeOrCreateIntFact(string,int) @ RVA 0x3A3580
    ///   GetBoolFacts() / GetIntFacts()    @ RVA 0x365F00 / 0x352390
    ///
    /// Only the host forwards. There is no client-to-host fact path by design: two
    /// writers on one story timeline is unrecoverable.
    /// </summary>
    public static class FactSync
    {
        private static FactShadow _shadow = new FactShadow();
        private static readonly List<(string key, byte kind, bool b, int i)> _pending =
            new List<(string, byte, bool, int)>(64);

        private const byte KindBool = 0;
        private const byte KindInt = 1;

        /// <summary>Last full snapshot from the host, used to rebuild after drift.</summary>
        private static Dictionary<string, bool> _lastBools = new Dictionary<string, bool>();
        private static Dictionary<string, int> _lastInts = new Dictionary<string, int>();

        /// <summary>
        /// Keys the host has sent us. The client can accumulate local-only facts before a
        /// session (menu triggers, scene scripts); comparing those would report permanent
        /// drift, so the divergence check only looks at host-owned keys.
        /// </summary>
        private static HashSet<string> _syncedKeys;

        public static void Init()
        {
            _shadow = new FactShadow();
        }

        // ---------------- host: observe ----------------

        internal static void OnFactSetBool(string key, bool value)
        {
            if (LoopGuard.ApplyingRemote) return;
            if (Session.Instance?.Role != SessionRole.Host) return;
            if (!_shadow.ObserveBool(key, value)) return;
            Queue(key, KindBool, value, 0);
        }

        internal static void OnFactSetInt(string key, int value)
        {
            if (LoopGuard.ApplyingRemote) return;
            if (Session.Instance?.Role != SessionRole.Host) return;
            if (!_shadow.ObserveInt(key, value)) return;
            Queue(key, KindInt, false, value);
        }

        private static void Queue(string key, byte kind, bool b, int i)
        {
            _pending.Add((key, kind, b, i));
            if (_pending.Count >= 20) Flush();
        }

        /// <summary>Packs queued fact changes into one FactDelta datagram.</summary>
        public static void Flush()
        {
            if (_pending.Count == 0) return;
            if (Session.Instance?.Role != SessionRole.Host) { _pending.Clear(); return; }

            var w = new Writer();
            w.U8((byte)_pending.Count);
            foreach (var e in _pending)
            {
                w.Str(e.key).U8(e.kind);
                if (e.kind == KindBool) w.Bool(e.b); else w.I32(e.i);
            }
            _pending.Clear();
            Session.Instance?.SendReliable(PacketKind.FactDelta, w.ToArray());
        }

        // ---------------- client: apply ----------------

        public static void ApplyDelta(byte[] payload)
        {
            var r = new Reader(payload);
            int n = r.U8();
            var facts = GameRefs.Facts;
            if (facts == null) { Log.Warn("FactDelta received with no FactDB - dropping"); return; }

            LoopGuard.Apply(() =>
            {
                for (int k = 0; k < n; k++)
                {
                    string key = r.Str();
                    byte kind = r.U8();
                    if (kind == KindBool)
                    {
                        bool v = r.Bool();
                        facts.SetFact(key, v);
                        _lastBools[key] = v;
                    }
                    else
                    {
                        int v = r.I32();
                        facts.SetFact(key, v);
                        _lastInts[key] = v;
                    }
                }
            });
        }

        // ---------------- snapshots ----------------

        /// <summary>Full fact map, for the handshake and for resync.</summary>
        public static (Dictionary<string, bool>, Dictionary<string, int>) Capture()
        {
            var bools = new Dictionary<string, bool>();
            var ints = new Dictionary<string, int>();
            var facts = GameRefs.Facts;
            if (facts == null) return (bools, ints);
            try
            {
                // The game's dictionaries are Il2CppSystem.Collections.Generic.Dictionary,
                // not System ones; copy across the interop boundary once here.
                var b = facts.GetBoolFacts();
                if (b != null) foreach (var kv in b) bools[kv.Key] = kv.Value;
                var i = facts.GetIntFacts();
                if (i != null) foreach (var kv in i) ints[kv.Key] = kv.Value;
                return (bools, ints);
            }
            catch (Exception e)
            {
                Log.Error($"Capture facts failed: {e}");
                return (bools, ints);
            }
        }

        public static byte[] EncodeSnapshot(Dictionary<string, bool> bools, Dictionary<string, int> ints)
        {
            var w = new Writer();
            w.U16((ushort)Math.Min(bools.Count, ushort.MaxValue));
            foreach (var kv in bools) { w.Str(kv.Key).Bool(kv.Value); if (w.Length > Proto.MaxPayload - 64) break; }
            w.U16((ushort)Math.Min(ints.Count, ushort.MaxValue));
            foreach (var kv in ints) { w.Str(kv.Key).I32(kv.Value); if (w.Length > Proto.MaxPayload - 64) break; }
            return w.ToArray();
        }

        public static void ApplySnapshot(byte[] payload)
        {
            var (bools, ints) = DecodeSnapshot(payload);
            var facts = GameRefs.Facts;
            if (facts == null) return;

            LoopGuard.Apply(() =>
            {
                foreach (var kv in bools) facts.SetFact(kv.Key, kv.Value);
                foreach (var kv in ints) facts.SetFact(kv.Key, kv.Value);
            });
            _shadow.Seed(bools, ints);
            _lastBools = bools;
            _lastInts = ints;
            _syncedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in bools.Keys) _syncedKeys.Add(k);
            foreach (var k in ints.Keys) _syncedKeys.Add(k);
            Log.Info($"Applied fact snapshot: {bools.Count} bools, {ints.Count} ints");
        }

        public static (Dictionary<string, bool>, Dictionary<string, int>) DecodeSnapshot(byte[] payload)
        {
            var r = new Reader(payload);
            var bools = new Dictionary<string, bool>();
            var ints = new Dictionary<string, int>();
            int nb = r.U16();
            for (int k = 0; k < nb; k++) bools[r.Str()] = r.Bool();
            int ni = r.U16();
            for (int k = 0; k < ni; k++) ints[r.Str()] = r.I32();
            return (bools, ints);
        }

        /// <summary>
        /// Cheap divergence detector: FNV-1a over the sorted fact map. Sent in Ping so
        /// drift is noticed within a second instead of when a quest refuses to advance.
        /// </summary>
        public static string Fingerprint()
        {
            var (bools, ints) = Capture();
            bool client = Session.Instance?.Role == SessionRole.Client;
            var filter = client ? _syncedKeys : null;

            ulong h = 1469598103934665603UL;
            var keys = new List<string>(bools.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys)
            {
                if (filter != null && !filter.Contains(k)) continue;
                foreach (char c in k) { h ^= c; h *= 1099511628211UL; }
                h ^= bools[k] ? 1UL : 2UL;
                h *= 1099511628211UL;
            }
            var ikeys = new List<string>(ints.Keys);
            ikeys.Sort(StringComparer.Ordinal);
            foreach (var k in ikeys)
            {
                if (filter != null && !filter.Contains(k)) continue;
                foreach (char c in k) { h ^= c; h *= 1099511628211UL; }
                h ^= (uint)ints[k];
                h *= 1099511628211UL;
            }
            return h.ToString("X16");
        }
    }

    // ---------------- Harmony hooks ----------------

    [HarmonyPatch(typeof(global::FactDB), nameof(global::FactDB.SetFact), new[] { typeof(string), typeof(bool) })]
    internal static class Patch_FactDB_SetFact_Bool
    {
        /// <summary>
        /// Story facts have exactly one writer: the host. A connected client suppresses its
        /// own local writes (scene triggers, one-shot world events, dialogue actions) so the
        /// two games cannot diverge; the host's values arrive through FactDelta. Writes that
        /// the mod itself performs run inside LoopGuard and are allowed through.
        /// </summary>
        [HarmonyPrefix]
        static bool Prefix() => !FactWritePolicy.SuppressClientWrites();

        [HarmonyPostfix]
        static void Postfix(string key, bool value) => FactSync.OnFactSetBool(key, value);
    }

    internal static class FactWritePolicy
    {
        internal static bool SuppressClientWrites()
        {
            var s = Session.Instance;
            if (s == null || s.Role != SessionRole.Client || !s.Connected) return false;
            return !LoopGuard.ApplyingRemote;
        }
    }

    [HarmonyPatch(typeof(global::FactDB), nameof(global::FactDB.SetFact), new[] { typeof(string), typeof(int) })]
    internal static class Patch_FactDB_SetFact_Int
    {
        [HarmonyPrefix]
        static bool Prefix() => !FactWritePolicy.SuppressClientWrites();

        [HarmonyPostfix]
        static void Postfix(string key, int value) => FactSync.OnFactSetInt(key, value);
    }

    [HarmonyPatch(typeof(global::FactDB), nameof(global::FactDB.ChangeOrCreateIntFact))]
    internal static class Patch_FactDB_ChangeOrCreateInt
    {
        [HarmonyPrefix]
        static bool Prefix() => !FactWritePolicy.SuppressClientWrites();

        [HarmonyPostfix]
        static void Postfix(string factKey, int addedValue, bool __result)
        {
            if (!__result) return;
            var facts = GameRefs.Facts;
            if (facts == null) return;
            try
            {
                if (facts.GetFact(factKey, out int now)) FactSync.OnFactSetInt(factKey, now);
            }
            catch (Exception e) { Log.Debug($"ChangeOrCreateIntFact readback failed: {e.Message}"); }
        }
    }
}

