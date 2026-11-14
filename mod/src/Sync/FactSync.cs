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
    /// The host owns the canonical fact map and broadcasts every change. The client's own
    /// story actions (a dialogue choice, a trigger it walked into, a quest step it
    /// completed) are applied locally for immediate feedback AND sent to the host as a
    /// FactRequest; the host applies them through the real SetFact, which makes them
    /// canonical and re-broadcasts. Facts are monotonic story flags in practice, so
    /// last-writer-wins is safe and either player can advance the story.
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

        // ---------------- player-owned facts ----------------
        //
        // The fact map also mirrors each player's inventory: DelusionInventory.ChangeItemFact
        // writes the item count under the item id (potionHealth = 12, sword = 2, ore = 6),
        // and coin/pick counters live there too. Inventories are per player, and the game
        // re-derives these facts from the real inventory (ValidateItemFact), so syncing them
        // would overwrite a partner's counts and then fight the game. Those keys are
        // player-owned: never broadcast, forwarded, snapshotted or applied.

        private static readonly HashSet<string> _playerOwned = new HashSet<string>(StringComparer.Ordinal)
        {
            "playerCoin", "playerPicks", "picksNotFull",
        };
        private static bool _seededFromItems;
        [ThreadStatic] private static int _ownerDepth;

        internal static void EnterPlayerScope() => _ownerDepth++;
        internal static void ExitPlayerScope() { if (_ownerDepth > 0) _ownerDepth--; }

        public static int PlayerOwnedCount { get { Seed(); return _playerOwned.Count; } }

        private static void Seed()
        {
            if (_seededFromItems) return;
            try
            {
                var db = global::DelusionItemDatabase.Instance;
                var names = db != null ? db.itemNameDict : null;
                if (names == null || names.Count == 0) return;
                foreach (var kv in names) _playerOwned.Add(kv.Key);
                _seededFromItems = true;
                Log.Info($"facts: {_playerOwned.Count} player-owned keys (inventory mirrors) excluded from sync");
            }
            catch (Exception e) { Log.Debug("player-owned seed: " + e.Message); }
        }

        public static bool IsPlayerOwned(string key)
        {
            Seed();
            return _playerOwned.Contains(key);
        }

        /// <summary>Called by every SetFact observer: learns keys written by inventory code
        /// and reports whether this key must stay local.</summary>
        private static bool LocalOnly(string key)
        {
            if (_ownerDepth > 0) { _playerOwned.Add(key); return true; }
            return IsPlayerOwned(key);
        }

        // ---------------- host: observe ----------------

        // ---------------- client: forward own writes ----------------

        private static readonly List<(string key, byte kind, bool b, int i)> _requests =
            new List<(string, byte, bool, int)>(32);
        private static int _forwarded;
        public static int ForwardedCount => _forwarded;

        private static bool ClientShouldForward()
        {
            var s = Session.Instance;
            if (s == null || s.Role != SessionRole.Client || !s.Connected) return false;
            if (LoopGuard.ApplyingRemote) return false;
            // Menu/loading scripts reset facts to defaults; those are not player actions.
            return GameRefs.InWorld;
        }

        private static void Request(string key, byte kind, bool b, int i)
        {
            _requests.Add((key, kind, b, i));
            _forwarded++;
            Log.Info($"fact -> host: {key} = {(kind == KindBool ? b.ToString() : i.ToString())}");
            if (_requests.Count >= 20) FlushRequests();
        }

        public static void FlushRequests()
        {
            if (_requests.Count == 0) return;
            var s = Session.Instance;
            if (s == null || s.Role != SessionRole.Client || !s.Connected) { _requests.Clear(); return; }
            var w = new Writer();
            w.U8((byte)_requests.Count);
            foreach (var e in _requests)
            {
                w.Str(e.key).U8(e.kind);
                if (e.kind == KindBool) w.Bool(e.b); else w.I32(e.i);
            }
            _requests.Clear();
            s.SendReliable(PacketKind.FactRequest, w.ToArray());
        }

        /// <summary>Host: apply a client's story write through the real FactDB, so the
        /// game reacts exactly as if the host had done it (and the change re-broadcasts).</summary>
        public static void ApplyRequest(byte[] payload)
        {
            var r = new Reader(payload);
            int n = r.U8();
            var facts = GameRefs.Facts;
            if (facts == null) return;
            for (int k = 0; k < n; k++)
            {
                string key = r.Str();
                byte kind = r.U8();
                bool bv = false; int iv = 0;
                if (kind == KindBool) bv = r.Bool(); else iv = r.I32();
                if (IsPlayerOwned(key)) continue;
                try
                {
                    if (kind == KindBool) facts.SetFact(key, bv);
                    else facts.SetFact(key, iv);
                }
                catch (Exception e) { Log.Warn($"fact request {key} failed: {e.Message}"); }
            }
            Log.Info($"applied {n} fact(s) from client");
        }

        internal static void OnFactSetBool(string key, bool value)
        {
            if (LocalOnly(key)) return;
            if (ClientShouldForward()) { Request(key, KindBool, value, 0); return; }
            if (LoopGuard.ApplyingRemote) return;
            if (Session.Instance?.Role != SessionRole.Host) return;
            if (!_shadow.ObserveBool(key, value)) return;
            Queue(key, KindBool, value, 0);
        }

        internal static void OnFactSetInt(string key, int value)
        {
            if (LocalOnly(key)) return;
            if (ClientShouldForward()) { Request(key, KindInt, false, value); return; }
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
            FlushRequests();
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
                        if (IsPlayerOwned(key)) continue;
                        facts.SetFact(key, v);
                        _lastBools[key] = v;
                    }
                    else
                    {
                        int v = r.I32();
                        if (IsPlayerOwned(key)) continue;
                        facts.SetFact(key, v);
                        _lastInts[key] = v;
                    }
                }
            });
        }

        // ---------------- snapshots ----------------

        /// <summary>Full fact map, for the handshake and for resync.</summary>
        /// <summary>The shared (story/world) part of the fact map: what is snapshotted,
        /// fingerprinted and compared between peers.</summary>
        public static (Dictionary<string, bool>, Dictionary<string, int>) CaptureShared()
        {
            var (b, i) = Capture();
            foreach (var k in new List<string>(b.Keys)) if (IsPlayerOwned(k)) b.Remove(k);
            foreach (var k in new List<string>(i.Keys)) if (IsPlayerOwned(k)) i.Remove(k);
            return (b, i);
        }

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

            foreach (var k in new List<string>(bools.Keys)) if (IsPlayerOwned(k)) bools.Remove(k);
            foreach (var k in new List<string>(ints.Keys)) if (IsPlayerOwned(k)) ints.Remove(k);
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
            var (bools, ints) = CaptureShared();
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
        // Every write - host or client - is observed after it lands. The host broadcasts;
        // a connected client forwards it to the host as a request.
        [HarmonyPostfix]
        static void Postfix(string key, bool value) => FactSync.OnFactSetBool(key, value);
    }

    [HarmonyPatch(typeof(global::FactDB), nameof(global::FactDB.SetFact), new[] { typeof(string), typeof(int) })]
    internal static class Patch_FactDB_SetFact_Int
    {
        [HarmonyPostfix]
        static void Postfix(string key, int value) => FactSync.OnFactSetInt(key, value);
    }

    [HarmonyPatch(typeof(global::FactDB), nameof(global::FactDB.ChangeOrCreateIntFact))]
    internal static class Patch_FactDB_ChangeOrCreateInt
    {
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

    // Inventory code paths: any fact they write is a per-player inventory mirror.
    [HarmonyPatch(typeof(global::DelusionInventory), "ChangeItemFact")]
    internal static class Patch_Inventory_ChangeItemFact
    {
        [HarmonyPrefix] static void Prefix() => FactSync.EnterPlayerScope();
        [HarmonyFinalizer] static void Finalizer() => FactSync.ExitPlayerScope();
    }

    [HarmonyPatch(typeof(global::DelusionInventory), nameof(global::DelusionInventory.ValidateItemFact))]
    internal static class Patch_Inventory_ValidateItemFact
    {
        [HarmonyPrefix] static void Prefix() => FactSync.EnterPlayerScope();
        [HarmonyFinalizer] static void Finalizer() => FactSync.ExitPlayerScope();
    }

    [HarmonyPatch(typeof(global::DelusionStats), nameof(global::DelusionStats.ChangeCoin))]
    internal static class Patch_Stats_ChangeCoin
    {
        [HarmonyPrefix] static void Prefix() => FactSync.EnterPlayerScope();
        [HarmonyFinalizer] static void Finalizer() => FactSync.ExitPlayerScope();
    }
}
