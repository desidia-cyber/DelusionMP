using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Sync;

namespace DelusionMP.Net
{
    public enum SessionRole { None, Host, Client }

    /// <summary>
    /// Transport abstraction. Two implementations are planned:
    ///   * SteamTransport - SteamNetworkingMessages through the game's own steam_api64
    ///     (the game already links Facepunch.Steamworks, so no extra app id is needed);
    ///   * LoopbackTransport - two processes on one PC, for the test harness.
    /// </summary>
    public interface ITransport
    {
        bool IsOpen { get; }
        void Open();
        void Close();
        void Send(byte[] datagram);
        /// <summary>Returns the next datagram, or null when the queue is empty.</summary>
        byte[] Poll();
    }

    /// <summary>
    /// Session: handshake, reliability, dispatch.
    ///
    /// Reliability is a simple sequence + ack_bits scheme, mirroring DollsMP
    /// (crates/dollsmp-core/src/protocol.rs). Reliable packets are retransmitted until
    /// acked; unreliable packets (transforms) are dropped rather than delaying the next
    /// frame.
    /// </summary>
    public sealed class Session
    {
        private static Session _instance;
        public static Session Instance => _instance ??= new Session();

        public SessionRole Role { get; private set; } = SessionRole.None;
        public ITransport Transport { get; set; }

        private ushort _nextSeq;
        private ushort _lastSeenSeq;
        private uint _ackBits;

        private readonly Dictionary<ushort, (byte[] data, float lastSent, int tries)> _pending =
            new Dictionary<ushort, (byte[], float, int)>();
        private readonly EventJournal _journal = new EventJournal();

        private bool _handshakeComplete;
        private float _lastSnapshotAt;
        private string _peerTableHash = "";

        public bool Connected => _handshakeComplete && Transport != null && Transport.IsOpen;

        // ---------------- lifecycle ----------------

        public void Host(ITransport t)
        {
            Transport = t;
            Transport.Open();
            Role = SessionRole.Host;
            Log.Info("hosting");
        }

        public void Join(ITransport t)
        {
            Transport = t;
            Transport.Open();
            Role = SessionRole.Client;
            SendHello();
            Log.Info("joining");
        }

        public void Close()
        {
            try { SendReliable(PacketKind.Bye, Array.Empty<byte>()); } catch { }
            Transport?.Close();
            Role = SessionRole.None;
            _handshakeComplete = false;
            _pending.Clear();
            PlayerSync.DestroyPuppet();
        }

        // ---------------- handshake ----------------

        private void SendHello()
        {
            var w = new Writer();
            w.U16(Proto.Version);
            w.Str(PluginInfo.Version);
            w.Str(BuildInfo.GameAssemblyHash);
            w.Str(ObjectRegistry.Instance.TableHash());
            SendRaw(PacketKind.Hello, w.ToArray(), reliable: true);
        }

        private void OnHello(byte[] payload)
        {
            var r = new Reader(payload);
            ushort ver = r.U16();
            string modVer = r.Str();
            string build = r.Str();
            string table = r.Str();

            if (ver != Proto.Version)
            {
                Fail($"protocol mismatch: host {ver}, us {Proto.Version}");
                return;
            }
            if (modVer != PluginInfo.Version)
            {
                Fail($"mod version mismatch: host {modVer}, us {PluginInfo.Version}");
                return;
            }
            if (build != BuildInfo.GameAssemblyHash)
            {
                Fail("game build mismatch - both players must run the same game version");
                return;
            }
            if (Role == SessionRole.Host)
            {
                _peerTableHash = table;
                var w = new Writer();
                w.U16(Proto.Version);
                w.Str(PluginInfo.Version);
                w.Str(build);
                w.Str(ObjectRegistry.Instance.TableHash());
                w.U8(1); // accepted
                SendRaw(PacketKind.Welcome, w.ToArray(), reliable: true);
                _handshakeComplete = true;
                SendWorldSnapshot();
            }
        }

        private void OnWelcome(byte[] payload)
        {
            var r = new Reader(payload);
            r.U16(); r.Str(); r.Str(); r.Str();
            byte accepted = r.U8();
            if (accepted == 0) { Fail("host refused the connection"); return; }
            _handshakeComplete = true;
            Log.Info("connected to host");
        }

        private void Fail(string reason)
        {
            Log.Error("connection refused: " + reason);
            Close();
        }

        // ---------------- sending ----------------

        public void SendReliable(PacketKind kind, byte[] payload) =>
            SendRaw(kind, payload, reliable: true);

        public void SendUnreliable(PacketKind kind, byte[] payload) =>
            SendRaw(kind, payload, reliable: false);

        private void SendRaw(PacketKind kind, byte[] payload, bool reliable)
        {
            if (Transport == null || !Transport.IsOpen) return;
            if (payload.Length > Proto.MaxPayload)
            {
                Log.Warn($"dropping oversized {kind} ({payload.Length} B)");
                return;
            }

            ushort seq = _nextSeq++;
            byte flags = reliable ? Proto.FlagReliable : (byte)0;
            var h = new Header
            {
                Version = Proto.Version,
                Kind = kind,
                Flags = flags,
                Seq = seq,
                Ack = _lastSeenSeq,
                AckBits = _ackBits,
                PayloadLen = (ushort)payload.Length,
            };
            var dg = new byte[Proto.HeaderLen + payload.Length];
            var hb = h.Encode();
            Buffer.BlockCopy(hb, 0, dg, 0, Proto.HeaderLen);
            Buffer.BlockCopy(payload, 0, dg, Proto.HeaderLen, payload.Length);
            Transport.Send(dg);

            if (reliable) _pending[seq] = (dg, UnityEngine.Time.realtimeSinceStartup, 1);
        }

        // ---------------- receiving ----------------

        public void Pump()
        {
            if (Transport == null || !Transport.IsOpen) return;
            for (int i = 0; i < 64; i++)
            {
                var dg = Transport.Poll();
                if (dg == null) break;
                Handle(dg);
            }
            RetransmitTick();
        }

        private void Handle(byte[] datagram)
        {
            if (!Header.TryDecode(datagram, out var h))
            {
                Log.Debug("malformed datagram");
                return;
            }
            if (h.Version != Proto.Version) return;

            // ack bookkeeping
            if (h.Seq > _lastSeenSeq)
            {
                uint shift = (uint)(h.Seq - _lastSeenSeq);
                _ackBits = shift >= 32 ? 1u : (_ackBits << (int)shift) | 1u;
                _lastSeenSeq = h.Seq;
            }
            else
            {
                int back = _lastSeenSeq - h.Seq;
                if (back < 32) _ackBits |= 1u << back;
            }
            _pending.Remove(h.Ack); // coarse: ack of the most recent makes older moot

            var payload = new byte[h.PayloadLen];
            Buffer.BlockCopy(datagram, Proto.HeaderLen, payload, 0, h.PayloadLen);

            if (h.Kind == PacketKind.Fragment)
            {
                OnFragment(payload);
                return;
            }

            Dispatch(h.Kind, payload);
        }

        private void Dispatch(PacketKind kind, byte[] payload)
        {
            switch (kind)
            {
                case PacketKind.Hello: OnHello(payload); break;
                case PacketKind.Welcome: OnWelcome(payload); break;
                case PacketKind.Ping: SendRaw(PacketKind.Pong, payload, false); ReplyPing(payload); break;
                case PacketKind.Pong: ReplyPing(payload); break;

                case PacketKind.FactDelta: FactSync.ApplyDelta(payload); break;
                case PacketKind.QuestState: QuestSync.Apply(payload); break;
                case PacketKind.ObjectState: WorldMirror.Apply(payload); break;
                case PacketKind.WorldEvent: WorldMirror.ApplyTyped(payload); break;
                case PacketKind.EnemyState: EnemySync.Apply(payload); break;
                case PacketKind.PlayerState: PlayerSync.Apply(payload); break;
                case PacketKind.ClockState: ClockSync.Apply(payload); break;
                case PacketKind.Snapshot: ApplySnapshot(payload); break;
                case PacketKind.ResyncRequest: OnResyncRequest(payload); break;
                case PacketKind.DesyncReport: OnDesyncReport(payload); break;
                case PacketKind.Bye: Log.Info("peer left"); Close(); break;
                default: Log.Debug($"unhandled {kind}"); break;
            }
        }

        // ---------------- fragmentation ----------------
        //
        // A full fact map (54 bools + 19 ints at a save's start, more later) does not fit a
        // single datagram. Reliable-ordered fragments keep the late-join / drift-repair path
        // working; each fragment carries (id, index, total, inner kind).

        private sealed class Reassembly
        {
            public PacketKind Kind;
            public int Total;
            public byte[][] Parts;
            public int Have;
            public float StartedAt;
        }

        private readonly Dictionary<ushort, Reassembly> _reassembly =
            new Dictionary<ushort, Reassembly>();
        private ushort _nextFragId = 1;

        private void SendFragmented(PacketKind kind, byte[] payload)
        {
            const int chunk = Proto.MaxPayload - 8;
            int total = (payload.Length + chunk - 1) / chunk;
            if (total <= 1)
            {
                SendReliable(kind, payload);
                return;
            }
            if (total > 255) { Log.Error($"fragment: payload {payload.Length}B needs {total} parts"); return; }

            ushort id = _nextFragId++;
            for (int i = 0; i < total; i++)
            {
                int off = i * chunk;
                int len = Math.Min(chunk, payload.Length - off);
                var w = new Writer();
                w.U16(id).U8((byte)i).U8((byte)total).U8((byte)kind);
                w.Bytes(new ArraySegment<byte>(payload, off, len).ToArray());
                SendReliable(PacketKind.Fragment, w.ToArray());
            }
        }

        private void OnFragment(byte[] payload)
        {
            var r = new Reader(payload);
            ushort id = r.U16();
            int index = r.U8();
            int total = r.U8();
            var inner = (PacketKind)r.U8();
            var part = r.Bytes(r.Remaining);

            if (total == 0 || index >= total) return;
            if (!_reassembly.TryGetValue(id, out var re))
            {
                re = new Reassembly
                {
                    Kind = inner,
                    Total = total,
                    Parts = new byte[total][],
                    StartedAt = UnityEngine.Time.realtimeSinceStartup,
                };
                _reassembly[id] = re;
            }
            if (re.Parts[index] == null) { re.Parts[index] = part; re.Have++; }

            // Drop abandoned partials.
            if (UnityEngine.Time.realtimeSinceStartup - re.StartedAt > 20f) { _reassembly.Remove(id); return; }
            if (re.Have < re.Total) return;

            int size = 0;
            foreach (var p in re.Parts) size += p?.Length ?? 0;
            var full = new byte[size];
            int at = 0;
            foreach (var p in re.Parts)
            {
                if (p == null) { _reassembly.Remove(id); return; }
                Buffer.BlockCopy(p, 0, full, at, p.Length);
                at += p.Length;
            }
            _reassembly.Remove(id);
            Dispatch(re.Kind, full);
        }

        private void ReplyPing(byte[] payload)
        {
            // ping payload carries the sender's fact fingerprint; mismatch triggers a
            // targeted resync instead of letting drift surface as a stuck quest.
            if (payload.Length < 8) return;
            try
            {
                var r = new Reader(payload);
                uint sentAt = (uint)r.I32();
                string fp = r.Str();
                if (Role == SessionRole.Host && fp != FactSync.Fingerprint())
                {
                    // Rate-limit: a peer that legitimately carries local-only facts would
                    // otherwise make every ping trigger a snapshot.
                    float now2 = UnityEngine.Time.realtimeSinceStartup;
                    if (now2 - _lastSnapshotAt < 5f) return;
                    _lastSnapshotAt = now2;
                    Log.Warn("fact fingerprint mismatch - sending snapshot");
                    SendWorldSnapshot();
                }
            }
            catch { }
        }

        // ---------------- snapshots ----------------

        private void SendWorldSnapshot()
        {
            var (bools, ints) = FactSync.Capture();
            var w = new Writer();
            w.U8(0); // snapshot section: facts
            w.U16((ushort)Math.Min(bools.Count, ushort.MaxValue));
            foreach (var kv in bools) w.Str(kv.Key).Bool(kv.Value);
            w.U16((ushort)Math.Min(ints.Count, ushort.MaxValue));
            foreach (var kv in ints) w.Str(kv.Key).I32(kv.Value);
            SendFragmented(PacketKind.Snapshot, w.ToArray());

            SendFragmented(PacketKind.QuestState, QuestSync.CaptureJournal());
            Log.Info($"world snapshot sent: {bools.Count} bools, {ints.Count} ints ({w.Length} B)");
        }

        private void ApplySnapshot(byte[] payload)
        {
            if (payload.Length == 0) return;
            var r = new Reader(payload);
            byte section = r.U8();
            if (section == 0)
            {
                // re-frame for FactSync.ApplySnapshot (it expects the fact section only)
                var body = new byte[payload.Length - 1];
                Buffer.BlockCopy(payload, 1, body, 0, body.Length);
                FactSync.ApplySnapshot(body);
            }
        }

        private void OnResyncRequest(byte[] payload)
        {
            if (Role != SessionRole.Host) return;
            SendWorldSnapshot();
        }

        /// <summary>Host: push the full fact map + journal + world mirror to the peer.</summary>
        public void Resync()
        {
            if (Role != SessionRole.Host) return;
            _lastSnapshotAt = UnityEngine.Time.realtimeSinceStartup;
            SendWorldSnapshot();
            WorldMirror.ForceSnapshot();
        }

        private void OnDesyncReport(byte[] payload)
        {
            if (Role != SessionRole.Host) return;
            Log.Warn("client reported desync - resending snapshot");
            SendWorldSnapshot();
        }

        // ---------------- reliability ----------------

        private void RetransmitTick()
        {
            if (_pending.Count == 0) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            List<ushort> drop = null;
            foreach (var kv in _pending)
            {
                var e = kv.Value;
                if (now - e.lastSent < 0.1f) continue;
                if (e.tries > 20)
                {
                    (drop ??= new List<ushort>()).Add(kv.Key);
                    continue;
                }
                Transport.Send(e.data);
                _pending[kv.Key] = (e.data, now, e.tries + 1);
            }
            if (drop != null) foreach (var k in drop) _pending.Remove(k);
        }

        public void PingTick()
        {
            var w = new Writer();
            w.I32((int)UnityEngine.Time.realtimeSinceStartup);
            w.Str(FactSync.Fingerprint());
            SendUnreliable(PacketKind.Ping, w.ToArray());
        }
    }

    public static class PluginInfo
    {
        public const string Version = "0.1.0";
    }

    /// <summary>
    /// Game build identity. The handshake refuses to connect when these differ: a build
    /// mismatch produces a desync that is indistinguishable from a game bug.
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>SHA-256 of GameAssembly.dll, computed at load.</summary>
        public static string GameAssemblyHash = "";

        public static void Compute()
        {
            try
            {
                var path = System.IO.Path.Combine(
                    UnityEngine.Application.dataPath, "..", "GameAssembly.dll");
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = System.IO.File.OpenRead(path);
                GameAssemblyHash = Convert.ToHexString(sha.ComputeHash(fs));
                Log.Info("GameAssembly SHA-256 " + GameAssemblyHash);
            }
            catch (Exception e)
            {
                Log.Warn("could not hash GameAssembly.dll: " + e.Message);
                GameAssemblyHash = "unknown";
            }
        }
    }
}



