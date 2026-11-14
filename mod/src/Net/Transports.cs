using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DelusionMP.Core;
using DelusionMP.Game;

namespace DelusionMP.Net
{
    /// <summary>
    /// Plain UDP transport, used by the two-instance test harness and as the LAN fallback.
    ///
    /// The production path is a Steam transport (SteamNetworkingMessages through the game's
    /// own steam_api64.dll, which already ships in StreamingAssets), but that needs a
    /// running Steam client and a friend, so it cannot be used for automated tests. Keeping
    /// the transport behind ITransport lets the QA harness swap it without touching sync
    /// logic.
    /// </summary>
    public sealed class UdpTransport : ITransport
    {
        private UdpClient _sock;
        private IPEndPoint _remote;
        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private volatile bool _open;

        public bool IsOpen => _open;

        /// <summary>Endpoint setup. Call before <see cref="Open"/>.</summary>
        public UdpTransport Configure(int localPort, string remoteHost, int remotePort)
        {
            _localPort = localPort;
            _remoteHost = remoteHost;
            _remotePort = remotePort;
            return this;
        }

        private int _localPort;
        private string _remoteHost = "127.0.0.1";
        private int _remotePort;

        public void Open()
        {
            _sock = new UdpClient(_localPort);
            _sock.Client.Blocking = false;
            _sock.Client.ReceiveBufferSize = 1 << 20;
            _remote = new IPEndPoint(IPAddress.Parse(_remoteHost), _remotePort);
            _open = true;

            _sock.BeginReceive(OnReceive, null);
            Log.Info($"udp open :{_localPort} -> {_remoteHost}:{_remotePort}");
        }

        private void OnReceive(IAsyncResult ar)
        {
            if (!_open) return;
            try
            {
                IPEndPoint from = null;
                var data = _sock.EndReceive(ar, ref from);
                if (data != null && data.Length > 0) _inbox.Enqueue(data);
                _sock.BeginReceive(OnReceive, null);
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { if (_open) TryAgain(); }
        }

        private void TryAgain()
        {
            try { if (_open) _sock.BeginReceive(OnReceive, null); } catch { }
        }

        public void Close()
        {
            _open = false;
            try { _sock?.Close(); } catch { }
            _sock = null;
        }

        public void Send(byte[] datagram)
        {
            if (!_open || _sock == null) return;
            try { _sock.Send(datagram, datagram.Length, _remote); }
            catch (SocketException e) { Log.Debug("udp send: " + e.SocketErrorCode); }
        }

        public byte[] Poll()
        {
            return _inbox.TryDequeue(out var d) ? d : null;
        }
    }

    /// <summary>
    /// In-process transport for unit tests: two instances wired to each other, optionally
    /// with a loss/reorder injector. Mirrors the DollsMP fault-injecting transport so the
    /// protocol can be exercised without a game or a network.
    /// </summary>
    public sealed class LoopbackTransport : ITransport
    {
        public LoopbackTransport Peer;
        public float LossRate;
        public bool DropAll;

        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly Random _rng = new Random(12345);
        private volatile bool _open;

        public bool IsOpen => _open;
        public void Open() => _open = true;
        public void Close() { _open = false; Peer = null; }

        public void Send(byte[] datagram)
        {
            if (!_open || Peer == null || DropAll) return;
            // Only inject loss on datagrams that would tolerate it; the caller marks
            // reliable ones with a flag it can retransmit, so plain random loss is fine.
            if (LossRate > 0 && _rng.NextDouble() < LossRate) return;
            Peer._inbox.Enqueue(datagram);
        }

        public byte[] Poll() => _inbox.TryDequeue(out var d) ? d : null;

        public static (LoopbackTransport a, LoopbackTransport b) Pair()
        {
            var a = new LoopbackTransport();
            var b = new LoopbackTransport();
            a.Peer = b;
            b.Peer = a;
            a.Open();
            b.Open();
            return (a, b);
        }
    }
}
