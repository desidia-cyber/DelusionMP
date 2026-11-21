using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Host-authoritative join. When a client connects (or the host forces a resync), the
    /// host's world wins wherever the two saves disagree:
    ///
    ///   * story facts: exact copy (FactSync snapshot; facts only the client has are removed);
    ///   * quest journal: exact copy (QuestSync journal snapshot; extra/advanced client entries
    ///     are brought back to the host's);
    ///   * story phase (CharWorldState): the host's value, even if lower;
    ///   * world objects: the host sends its whole world table - every live object's state plus
    ///     its save's loaded-state entries for regions it has not loaded. The client replaces
    ///     its own table with it, restores live objects that differ, and where the host has an
    ///     object at its default but the client changed it, reloads the loaded regions so those
    ///     objects come back untouched. The client's own pending changes are discarded.
    ///
    /// Player-owned data (inventory, coins, stats, position, death stacks) is never touched.
    /// The world part runs only when the tables actually differ.
    /// </summary>
    public static class JoinSync
    {
        private const byte MsgWorldTable = 0, MsgWorldPhase = 1;
        private const int ChunkBytes = 200_000;   // well under the 255-fragment limit after deflate

        private static int _adopted, _restored, _removed, _reset;
        public static string Stats => $"adoptions={_adopted} restored={_restored} removedFromTable={_removed} regionResets={_reset}";

        // ------------------------------------------------------------------ host

        /// <summary>Host: everything this world holds, uid -> encoded state (null body = default).</summary>
        private static Dictionary<string, byte[]> CaptureWorldTable(out int liveDefaults)
        {
            liveDefaults = 0;
            var table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            // 1. The save's table for objects not loaded right now.
            try
            {
                var loaded = GameRefs.Save?.GetLoadedState();
                if (loaded != null)
                    foreach (var kv in loaded)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                        try { table[kv.Key] = StateCodec.Encode(kv.Value); } catch { }
                    }
            }
            catch (Exception e) { Log.Warn("join: loaded-state capture failed: " + e.Message); }
            // 2. Live objects override (they are the current truth).
            foreach (var so in ObjectRegistry.AllSaveables())
            {
                if (so == null || !so.gameObject.scene.IsValid()) continue;
                string id = null;
                try { id = so.GetUniqueIdentifier(); } catch { }
                if (string.IsNullOrEmpty(id)) continue;
                object st = null;
                try { st = so.CaptureState(); } catch { continue; }
                if (st == null) { table[id] = null; liveDefaults++; continue; }
                try { table[id] = StateCodec.Encode(st); } catch { }
            }
            return table;
        }

        /// <summary>Host: part of the join snapshot.</summary>
        public static void SendHostTruth(Session session)
        {
            if (session == null || session.Role != SessionRole.Host || !GameRefs.InWorld) return;
            int phase = -1;
            try { phase = (int)global::DelusionStats.Instance.GetWorldState(); } catch { }
            var pw = new Writer();
            pw.U8(MsgWorldPhase).I32(phase);
            session.SendReliable(PacketKind.JoinTruth, pw.ToArray());

            var table = CaptureWorldTable(out int defaults);
            // Serialize, then cut into deflated chunks.
            var chunks = new List<byte[]>();
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            int count = 0;
            void Flush()
            {
                bw.Flush();
                chunks.Add(Deflate(ms.ToArray(), count));
                ms = new MemoryStream(); bw = new BinaryWriter(ms); count = 0;
            }
            foreach (var kv in table)
            {
                bw.Write(kv.Key);
                if (kv.Value == null) bw.Write(-1);
                else { bw.Write(kv.Value.Length); bw.Write(kv.Value); }
                count++;
                if (ms.Length > ChunkBytes) Flush();
            }
            Flush();
            ushort batch = (ushort)UnityEngine.Random.Range(1, 65535);
            for (int i = 0; i < chunks.Count; i++)
            {
                var w = new Writer();
                w.U8(MsgWorldTable).U16(batch).U16((ushort)i).U16((ushort)chunks.Count);
                w.Bytes(chunks[i]);
                session.SendReliableLarge(PacketKind.JoinTruth, w.ToArray());
            }
            Log.Info($"join: host world table sent - {table.Count} objects ({defaults} live at default), {chunks.Count} chunk(s)");
        }

        private static byte[] Deflate(byte[] raw, int count)
        {
            var o = new MemoryStream();
            var hdr = new BinaryWriter(o);
            hdr.Write(count);
            hdr.Flush();
            using (var z = new DeflateStream(o, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
            return o.ToArray();
        }

        // ------------------------------------------------------------------ client

        private static ushort _batch;
        private static byte[][] _parts;
        private static int _have;
        private static int _pendingPhase = -1;
        private static Dictionary<string, byte[]> _pendingTable;

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            byte kind = r.U8();
            if (kind == MsgWorldPhase) { _pendingPhase = r.I32(); return; }
            if (kind != MsgWorldTable) return;
            ushort batch = r.U16(); int index = r.U16(); int total = r.U16();
            var body = r.Bytes(r.Remaining);
            if (batch != _batch || _parts == null || _parts.Length != total) { _batch = batch; _parts = new byte[total][]; _have = 0; }
            if (index >= total || _parts[index] != null) return;
            _parts[index] = body; _have++;
            if (_have < total) return;

            var table = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var part in _parts)
            {
                var ms = new MemoryStream(part);
                var hdr = new BinaryReader(ms);
                int count = hdr.ReadInt32();
                using (var z = new DeflateStream(ms, CompressionMode.Decompress))
                using (var br = new BinaryReader(z))
                    for (int i = 0; i < count; i++)
                    {
                        string uid = br.ReadString();
                        int len = br.ReadInt32();
                        table[uid] = len < 0 ? null : br.ReadBytes(len);
                    }
            }
            _parts = null;
            _pendingTable = table;
            Log.Info($"join: host world table received ({table.Count} objects)");
        }

        /// <summary>Client, per frame: adopt once the world is up.</summary>
        public static void Tick()
        {
            if (Session.Instance?.Role != SessionRole.Client || !GameRefs.InWorld) return;
            if (_pendingPhase >= 0) ApplyPhase();
            if (_pendingTable != null) { var t = _pendingTable; _pendingTable = null; AdoptWorld(t); }
        }

        private static void ApplyPhase()
        {
            int want = _pendingPhase;
            _pendingPhase = -1;
            try
            {
                var st = global::DelusionStats.Instance;
                if (st == null) return;
                int cur = (int)st.GetWorldState();
                if (cur == want) return;
                // The game only moves the phase forward through ChangeCharWorldState; set it
                // directly and raise the same change event so phase-driven triggers re-check.
                var cs = st.characterStats;
                if (cs == null) return;
                cs.charWorldState = (global::CharWorldState)want;
                LoopGuard.Apply(() => { try { st.OnCharWorldStateChanged?.Invoke(); } catch { } });
                Log.Info($"join: story phase {cur} -> {want} (host)");
            }
            catch (Exception e) { Log.Warn("join phase: " + e.Message); }
        }

        private static void AdoptWorld(Dictionary<string, byte[]> host)
        {
            var mine = CaptureWorldTable(out _);
            var loaded = GameRefs.Save?.GetLoadedState();
            var reg = ObjectRegistry.Instance;
            string scene = reg.CurrentScene;

            // Which of my live objects are which uid.
            var live = new Dictionary<string, global::SaveableObject>(StringComparer.Ordinal);
            foreach (var so in ObjectRegistry.AllSaveables())
            {
                if (so == null || !so.gameObject.scene.IsValid()) continue;
                string id = null;
                try { id = so.GetUniqueIdentifier(); } catch { }
                if (!string.IsNullOrEmpty(id) && !live.ContainsKey(id)) live[id] = so;
            }

            int restored = 0, removed = 0, resetLive = 0, unchanged = 0;
            bool needRegionReset = false;

            // 1. Host has a state: make mine equal (table + live object).
            foreach (var kv in host)
            {
                if (kv.Value == null) continue;
                mine.TryGetValue(kv.Key, out var my);
                if (my != null && Same(my, kv.Value)) { unchanged++; continue; }
                Il2CppSystem.Object state;
                try { state = StateCodec.Build(kv.Value); } catch { continue; }
                if (state == null) continue;
                try { if (loaded != null) loaded[kv.Key] = state; } catch { }
                if (live.TryGetValue(kv.Key, out var so))
                {
                    var key = scene + "|" + kv.Key;
                    LoopGuard.Apply(() => { try { so.RestoreState(state); } catch { } });
                    WorldMirror.Rebaseline(key, so);
                }
                restored++;
            }

            // 2. The host has the object loaded and at its default, but mine is changed.
            //    Objects missing from the host's table entirely (a region the host has left;
            //    the game does not keep every unloaded object there) are left alone - absence
            //    is not evidence of a default.
            foreach (var kv in mine)
            {
                if (kv.Value == null) continue;
                if (!host.TryGetValue(kv.Key, out var hv)) continue;   // unknown to the host
                if (hv != null) continue;                              // handled above
                try { if (loaded != null && loaded.ContainsKey(kv.Key)) { loaded.Remove(kv.Key); removed++; } } catch { }
                if (live.ContainsKey(kv.Key)) { resetLive++; needRegionReset = true; }
            }

            WorldMirror.DiscardPending();

            int diffs = restored + removed + resetLive;
            if (diffs == 0)
            {
                Log.Info($"join: worlds already match ({unchanged} objects)");
                return;
            }
            _adopted++; _restored += restored; _removed += removed;
            Log.Info($"join: adopted the host's world - {restored} object(s) set to the host's state, " +
                     $"{removed} dropped from the save table, {resetLive} live object(s) changed here but untouched on the host");

            if (needRegionReset)
            {
                // Objects come back from their scene data + the (now host) loaded-state table.
                try
                {
                    var rm = GameRefs.Regions;
                    if (rm != null)
                    {
                        rm.ResetRegions();
                        // ResetRegions empties the region list; only OnRegionChanged (normally
                        // fired when the player crosses a region border) rebuilds it. Without
                        // this the player keeps no region scene until the next border.
                        rm.OnRegionChanged();
                        _reset++;
                        Log.Info($"join: regions reloaded so untouched-on-host objects return to default ({rm.regions?.Count ?? 0} region(s))");
                    }
                }
                catch (Exception e) { Log.Warn("join: region reload failed: " + e.Message); }
            }
            WorldMirror.ForceRebaselineAll();
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
