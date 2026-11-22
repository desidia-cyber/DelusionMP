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
        private bool _seenAny;
        private uint _ackBits;

        // Reliable-ordered channel: exactly-once, in-order delivery on top of the
        // ack/retransmit layer. Without it a retransmitted packet is applied once per copy
        // (forwarded damage would multiply) and an old copy can overwrite a newer state.
        private ushort _relOut;
        private ushort _relIn;
        private readonly Dictionary<ushort, (PacketKind kind, byte[] payload)> _relBuffer =
            new Dictionary<ushort, (PacketKind, byte[])>();

        // Session epoch: a random id the client picks per process. The host resets the
        // reliable channel only when it changes (a new or restarted client).
        // A new value every time this client starts a fresh channel (process start or after a
        // lost connection), so the host always treats a rejoin as a fresh join and both sides
        // restart their reliable sequence numbers together.
        private uint _localNonce = (uint)new Random().Next() | 1u;
        private static readonly Random _rng = new Random();
        private float _nextRejoinHint;
        private uint _peerNonce;

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
            ResetReliable();
            PlayerSync.DestroyPuppet();
        }

        // ---------------- handshake ----------------

        /// <summary>Client: announce ourselves. Unreliable - Bootstrap repeats it each
        /// second until a Welcome arrives, so queuing reliable copies would replay stale
        /// handshakes after the connection is up.</summary>
        public void SendHello()
        {
            var w = new Writer();
            w.U16(Proto.Version);
            w.Str(PluginInfo.Version);
            w.Str(BuildInfo.GameAssemblyHash);
            w.Str(ObjectRegistry.Instance.TableHash());
            w.I32((int)_localNonce);
            SendRaw(PacketKind.Hello, w.ToArray(), reliable: false);
        }

        private void ResetReliable()
        {
            _pending.Clear();
            _relBuffer.Clear();
            _reassembly.Clear();
            _relOut = 0;
            _relIn = 0;
        }

        private void OnHello(byte[] payload)
        {
            var r = new Reader(payload);
            ushort ver = r.U16();
            string modVer = r.Str();
            string build = r.Str();
            string table = r.Str();
            uint nonce = r.Remaining >= 4 ? (uint)r.I32() : 0u;

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
                bool fresh = !_handshakeComplete || nonce != _peerNonce;
                var w = new Writer();
                w.U16(Proto.Version);
                w.Str(PluginInfo.Version);
                w.Str(build);
                w.Str(ObjectRegistry.Instance.TableHash());
                w.U8(1); // accepted
                w.I32((int)nonce);
                SendRaw(PacketKind.Welcome, w.ToArray(), reliable: false);
                if (!fresh) return;   // a repeated Hello from the same client process

                // New (or restarted) client: start a clean reliable channel and give it the
                // whole world.
                _peerNonce = nonce;
                ResetReliable();
                _handshakeComplete = true;
                _lastHeardAt = UnityEngine.Time.realtimeSinceStartup;
                Log.Info($"client joined (epoch {nonce:X8})");
                WorldEventsSync.OnSessionStart();
                SendWorldSnapshot();
                WorldMirror.ForceSnapshot();
            }
        }

        private void OnWelcome(byte[] payload)
        {
            var r = new Reader(payload);
            r.U16(); r.Str(); r.Str(); r.Str();
            byte accepted = r.U8();
            uint echo = r.Remaining >= 4 ? (uint)r.I32() : 0u;
            if (accepted == 0) { Fail("host refused the connection"); return; }
            if (_handshakeComplete) return;               // repeated Welcome
            if (echo != 0 && echo != _localNonce) return; // meant for a previous process
            ResetReliable();
            _handshakeComplete = true;
            _lastHeardAt = UnityEngine.Time.realtimeSinceStartup;
            WorldMirror.OnClientConnected();
            WorldEventsSync.OnSessionStart();
            WorldEventsSync.SendJoinState();
            Log.Info("connected to host");
        }

        private float _lastFailNote = -100f;

        private void Fail(string reason)
        {
            Log.Error("connection refused: " + reason);
            // Otherwise the player only sees that nothing happens.
            if (UnityEngine.Time.realtimeSinceStartup - _lastFailNote > 15f)
            {
                _lastFailNote = UnityEngine.Time.realtimeSinceStartup;
                try { global::DelusionNote.Instance?.StartNewNote(null, "Co-op: " + reason + ". Both players need the same DelusionMP package.", false); } catch { }
            }
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
            if (reliable)
            {
                // Nothing reliable flows before the handshake: there is no peer channel
                // yet, and queued packets would replay into the next session.
                if (!_handshakeComplete) return;
                var framed = new byte[payload.Length + 2];
                framed[0] = (byte)_relOut; framed[1] = (byte)(_relOut >> 8);
                _relOut++;
                Buffer.BlockCopy(payload, 0, framed, 2, payload.Length);
                payload = framed;
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


        // Peer loss: nothing heard (pings run at 1 Hz) for this long means the other game is
        // gone. The session drops back to "not connected": a client then re-announces itself
        // every second (Bootstrap) and rejoins a restarted host with a full host-truth sync;
        // a host accepts the returning client as a fresh join.
        private const float PeerTimeout = 20f;
        private float _lastHeardAt;

        /// <summary>Forget the channel. A client also takes a new epoch so its next Hello is a
        /// fresh join on the host.</summary>
        private void DropChannel()
        {
            _handshakeComplete = false;
            ResetReliable();
            if (Role == SessionRole.Client) _localNonce = (uint)_rng.Next() | 1u;
        }

        private void CheckPeerLost()
        {
            if (!_handshakeComplete) return;
            if (UnityEngine.Time.realtimeSinceStartup - _lastHeardAt < PeerTimeout) return;
            Log.Warn($"peer lost (nothing heard for {PeerTimeout:F0} s) - waiting for it to come back");
            DropChannel();
        }

        public void Pump()
        {
            if (Transport == null || !Transport.IsOpen) return;
            CheckPeerLost();
            if (_deferredFacts != null && Role == SessionRole.Client && GameRefs.InWorld)
            {
                var f = _deferredFacts; _deferredFacts = null;
                FactSync.ApplySnapshot(f);
            }
            if (_snapshotDeferred && _handshakeComplete && Role == SessionRole.Host && GameRefs.InWorld)
            {
                SendWorldSnapshot();
                WorldMirror.ForceSnapshot();
            }
            // Drain everything that arrived. A fixed per-frame cap (it was 64) falls behind at
            // low frame rates (background windows, slow machines) and the backlog turns into
            // seconds of latency for every stream.
            for (int i = 0; i < 8192; i++)
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
            _lastHeardAt = UnityEngine.Time.realtimeSinceStartup;

            // One side dropped the channel but the other still sends on it: sequence numbers
            // no longer line up and everything would be discarded silently. The host tells
            // such a client to rejoin; the client drops its channel and says Hello again.
            if (h.Kind == PacketKind.Rejoin)
            {
                if (Role == SessionRole.Client && _handshakeComplete)
                {
                    Log.Warn("host asked us to rejoin (it had dropped the session)");
                    DropChannel();
                }
                return;
            }
            if (Role == SessionRole.Host && !_handshakeComplete && h.Kind != PacketKind.Hello
                && UnityEngine.Time.realtimeSinceStartup >= _nextRejoinHint)
            {
                _nextRejoinHint = UnityEngine.Time.realtimeSinceStartup + 1f;
                SendRaw(PacketKind.Rejoin, Array.Empty<byte>(), reliable: false);
            }

            // ack bookkeeping (16-bit sequence, wrap-aware)
            short d = (short)(h.Seq - _lastSeenSeq);
            if (!_seenAny || d > 0)
            {
                int shift = _seenAny ? d : 32;
                _ackBits = shift >= 32 ? 1u : (_ackBits << shift) | 1u;
                _lastSeenSeq = h.Seq;
                _seenAny = true;
            }
            else
            {
                int back = -d;
                if (back < 32) _ackBits |= 1u << back;
            }
            // The peer acks its newest sequence plus a 32-packet history bitfield.
            if (h.AckBits != 0)
                for (int i = 0; i < 32; i++)
                    if ((h.AckBits & (1u << i)) != 0) _pending.Remove((ushort)(h.Ack - i));

            var payload = new byte[h.PayloadLen];
            Buffer.BlockCopy(datagram, Proto.HeaderLen, payload, 0, h.PayloadLen);

            if (!h.HasFlag(Proto.FlagReliable))
            {
                Deliver(h.Kind, payload);
                return;
            }

            if (!_handshakeComplete || payload.Length < 2) return;
            ushort rs = (ushort)(payload[0] | (payload[1] << 8));
            var body = new byte[payload.Length - 2];
            Buffer.BlockCopy(payload, 2, body, 0, body.Length);

            short ahead = (short)(rs - _relIn);
            if (ahead < 0) return;                       // duplicate of something delivered
            if (ahead > 0)
            {
                if (ahead < 4096 && !_relBuffer.ContainsKey(rs)) _relBuffer[rs] = (h.Kind, body);
                return;                                  // wait for the gap to fill
            }
            _relIn++;
            Deliver(h.Kind, body);
            while (_relBuffer.TryGetValue(_relIn, out var next))
            {
                _relBuffer.Remove(_relIn);
                _relIn++;
                Deliver(next.kind, next.payload);
            }
        }

        private void Deliver(PacketKind kind, byte[] payload)
        {
            if (kind == PacketKind.Fragment) { OnFragment(payload); return; }
            Dispatch(kind, payload);
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
                case PacketKind.Dialogue: DialogueSync.Apply(payload); break;
                case PacketKind.Voice: VoiceSync.Receive(payload); break;
                case PacketKind.Chat: VoiceSync.ReceiveChat(payload); break;
                case PacketKind.Rules: RulesSync.Apply(payload); break;
                case PacketKind.PlayerLoadout: PlayerSync.ApplyLook(payload); break;
                case PacketKind.CoopDeath: CoopDeath.Apply(payload); break;
                case PacketKind.FactoryState: FactorySync.Apply(payload); break;
                case PacketKind.JoinTruth: if (Role == SessionRole.Client) JoinSync.Apply(payload); break;
                case PacketKind.Airship: AirshipSync.Apply(payload); break;
                case PacketKind.AirshipState: AirshipSync.ApplyState(payload); break;
                case PacketKind.Cutscene: CutsceneSync.Apply(payload); break;
                case PacketKind.WorldAction: WorldEventsSync.Apply(payload); break;
                case PacketKind.ObjectState: WorldMirror.Apply(payload); break;
                case PacketKind.WorldEvent: WorldMirror.ApplyTyped(payload); break;
                // Client-originated: the host applies (making it canonical). Only a host
                // accepts these - a client never takes world authority from its peer.
                case PacketKind.ObjectRequest: if (Role == SessionRole.Host) WorldMirror.Apply(payload); break;
                case PacketKind.WorldEventRequest: if (Role == SessionRole.Host) WorldMirror.ApplyTyped(payload); break;
                case PacketKind.FactRequest: if (Role == SessionRole.Host) FactSync.ApplyRequest(payload); break;
                case PacketKind.CombatEvent: EnemySync.ApplyCombat(payload); break;
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

        public void SendReliableLarge(PacketKind kind, byte[] payload) => SendFragmented(kind, payload);

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

        private bool _snapshotDeferred;
        private byte[] _deferredFacts;

        private void SendWorldSnapshot()
        {
            // The host's world is the truth only once it is loaded: a host still at the menu
            // would otherwise send the menu's empty story and the client would adopt it.
            if (!GameRefs.InWorld)
            {
                if (!_snapshotDeferred) Log.Info("world snapshot deferred until the host is in the world");
                _snapshotDeferred = true;
                return;
            }
            _snapshotDeferred = false;
            var (bools, ints) = FactSync.CaptureShared();
            var w = new Writer();
            w.U8(0); // snapshot section: facts
            w.U16((ushort)Math.Min(bools.Count, ushort.MaxValue));
            foreach (var kv in bools) w.Str(kv.Key).Bool(kv.Value);
            w.U16((ushort)Math.Min(ints.Count, ushort.MaxValue));
            foreach (var kv in ints) w.Str(kv.Key).I32(kv.Value);
            SendFragmented(PacketKind.Snapshot, w.ToArray());

            SendFragmented(PacketKind.QuestState, QuestSync.CaptureJournal());
            WorldEventsSync.SendJoinState();
            JoinSync.SendHostTruth(this);
            RulesSync.Send(this);
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
                // Loading a save replaces the fact map: apply the host's story once we are in
                // the world, not to the menu's map.
                if (!GameRefs.InWorld) { _deferredFacts = body; return; }
                FactSync.ApplySnapshot(body);
            }
        }

        private void OnResyncRequest(byte[] payload)
        {
            if (Role != SessionRole.Host) return;
            if (payload.Length > 0 && payload[0] == 1)
            {
                WorldMirror.OnMirrorResyncRequest();   // objects only (region streamed in)
                return;
            }
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
                // Back off to 1 s, but never give up while the session lives: the ordered
                // channel stalls behind a lost packet, so dropping one would freeze sync.
                if (now - e.lastSent < Math.Min(1f, 0.1f * e.tries)) continue;
                (drop ??= new List<ushort>()).Add(kv.Key);
            }
            if (drop == null) return;
            foreach (var k in drop)
            {
                var e = _pending[k];
                Transport.Send(e.data);
                _pending[k] = (e.data, now, e.tries + 1);
            }
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
        public const string Version = "0.2.0";
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



