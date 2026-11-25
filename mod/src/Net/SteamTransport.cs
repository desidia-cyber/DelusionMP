using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DelusionMP.Game;

namespace DelusionMP.Net
{
    /// <summary>
    /// In-process Steam P2P transport using ISteamNetworkingSockets (v008+, exported by Dread
    /// Delusion's stock steam_api64.dll) with zero external .exe files required.
    ///
    /// Supports both discovery modes:
    ///   - Co-op password: SHA-256 x 20,000 lobby key on a public Steam Matchmaking lobby.
    ///   - Steam friends: auto-detect a Steam friend playing Dread Delusion/Spacewar,
    ///     or connect by friend name / SteamID64.
    ///
    /// When Dread Delusion's Facepunch.Steamworks has already initialized Steam, callbacks are
    /// tapped safely on the main thread via SteamHooks (zero ManualDispatch races). When Steam
    /// is not initialized by the game (e.g. non-Steam launch), SteamTransport initializes
    /// steam_api64.dll itself and pumps ManualDispatch directly.
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        public const uint AppIdGame = 1574240;
        public const uint AppIdSpacewar = 480;

        private const int FriendImmediate = 0x04;
        private const int SendUnreliableNoNagle = 1; // k_nSteamNetworkingSend_UnreliableNoNagle
        private const int MsgMax = 64;
        private const byte FrameData = 0x00;
        private const byte FrameCtrl = 0x01;
        private const int LobbyPublic = 2;
        private const int LobbyMax = 8;
        private const int EnterSuccess = 1;
        private const int IdentitySteamId = 16;
        private const int VirtualPort = 0;

        private const int ConnStateNone = 0;
        private const int ConnStateConnecting = 1;
        private const int ConnStateFindingRoute = 2;
        private const int ConnStateConnected = 3;
        private const int ConnStateClosedByPeer = 4;
        private const int ConnStateProblemDetectedLocally = 5;

        private const int CfgSendBufferSize = 9;
        private const int CfgSendRateMin = 10;
        private const int SendBufferBytes = 64 * 1024;
        private const int SendRateMinBps = 256 * 1024;

        private static readonly byte[] KeepAliveFrame = new byte[] { FrameCtrl, (byte)'K', (byte)'A' };
        private static readonly byte[] GoodbyeFrame = new byte[] { FrameCtrl, (byte)'B', (byte)'Y', (byte)'E' };

        // Shared Facepunch main-thread callback tap -> drained by active SteamApi.Pump()
        private static readonly ConcurrentQueue<ConnStatusEvent> SharedConnEvents = new ConcurrentQueue<ConnStatusEvent>();
        private static readonly ConcurrentQueue<(int result, ulong lobby)> SharedLobbyCreated = new ConcurrentQueue<(int, ulong)>();
        private static readonly ConcurrentQueue<uint> SharedLobbyMatches = new ConcurrentQueue<uint>();
        private static readonly ConcurrentQueue<(ulong lobby, int response)> SharedLobbyEntered = new ConcurrentQueue<(ulong, int)>();
        private static volatile int _activeConsumers;
        private static SteamApi.CallResultFn _sharedCallResult;
        private static SteamApi.GetPipeFn _sharedGetPipe;

        public static bool NeedsMainThreadPump => _activeConsumers > 0;

        /// <summary>Returns the currently active Steam AppID in this process, or 0 if Steam isn't initialized.</summary>
        public static uint ActiveSteamAppId
        {
            get
            {
                uint fp = SteamHooks.ActiveFacepunchAppId;
                if (fp != 0) return fp;
                return SteamApi.TryQueryActiveAppId();
            }
        }

        /// <summary>True when Steam is already initialized in this process under a different AppID than requested.</summary>
        public static bool IsAppIdMismatched(bool wantSpacewar, out uint activeAppId)
        {
            activeAppId = ActiveSteamAppId;
            if (activeAppId == 0) return false;
            uint want = wantSpacewar ? AppIdSpacewar : AppIdGame;
            return activeAppId != want;
        }

        // Configuration
        private readonly bool _isHost;
        private readonly bool _useSpacewar;
        private readonly string _password;
        private readonly string _wantPartner;

        // Runtime state
        private readonly ConcurrentQueue<byte[]> _inbox = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> _outbox = new ConcurrentQueue<byte[]>();
        private Thread _thread;
        private volatile bool _open;
        private volatile bool _stop;

        public bool IsOpen => _open;
        public string Status { get; private set; } = "Starting Steam...";
        public string LastError { get; private set; } = "";
        public string LobbyCode { get; private set; } = "";
        public string PartnerName { get; private set; } = "";
        public string LocalSteamName { get; private set; } = "";
        public ulong LocalSteamId { get; private set; }

        public SteamTransport(bool isHost, bool useSpacewar, string password, string wantPartner)
        {
            _isHost = isHost;
            _useSpacewar = useSpacewar;
            _password = password?.Trim() ?? "";
            _wantPartner = wantPartner?.Trim() ?? "";
        }

        public static string ComputeLobbyKey(string password)
        {
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes("dollsmp-coop-v1|" + (password?.Trim() ?? "")));
            for (int i = 0; i < 20000; i++)
                h = sha.ComputeHash(h);
            return Convert.ToHexString(h).ToLowerInvariant().Substring(0, 32);
        }

        public static string ShortCode(string password)
        {
            if (string.IsNullOrWhiteSpace(password)) return "";
            return ComputeLobbyKey(password).Substring(0, 8).ToUpperInvariant();
        }

        internal static void OnFacepunchCallback(int callbackId, IntPtr data, int size)
        {
            if (_activeConsumers <= 0 || data == IntPtr.Zero) return;
            if (callbackId == 1221 && size >= 0xC0) // SteamNetConnectionStatusChangedCallback_t
            {
                try
                {
                    var ev = ParseConnStatus(data);
                    SharedConnEvents.Enqueue(ev);
                }
                catch { }
            }
        }

        internal static void OnFacepunchResult(IntPtr data, int size)
        {
            if (_activeConsumers <= 0 || data == IntPtr.Zero || size < 16) return;
            var callResult = _sharedCallResult;
            var getPipe = _sharedGetPipe;
            if (callResult == null || getPipe == null) return;
            try
            {
                ulong call = (ulong)Marshal.ReadInt64(data, 0);
                int cb = Marshal.ReadInt32(data, 8);
                if (cb != 513 && cb != 510 && cb != 504) return;
                int pipe = getPipe();
                if (pipe == 0) return;
                IntPtr resBuf = Marshal.AllocHGlobal(256);
                try
                {
                    if (callResult(pipe, call, resBuf, 256, cb, out bool failed) && !failed)
                    {
                        if (cb == 513) SharedLobbyCreated.Enqueue((Marshal.ReadInt32(resBuf, 0), (ulong)Marshal.ReadInt64(resBuf, 8)));
                        else if (cb == 510) SharedLobbyMatches.Enqueue((uint)Marshal.ReadInt32(resBuf, 0));
                        else if (cb == 504) SharedLobbyEntered.Enqueue(((ulong)Marshal.ReadInt64(resBuf, 0), Marshal.ReadInt32(resBuf, 16)));
                    }
                    else if (failed)
                    {
                        if (cb == 513) SharedLobbyCreated.Enqueue((-1, 0));
                        else if (cb == 510) SharedLobbyMatches.Enqueue(0);
                        else if (cb == 504) SharedLobbyEntered.Enqueue((0, -1));
                    }
                }
                finally { Marshal.FreeHGlobal(resBuf); }
            }
            catch { }
        }

        private static void ClearSharedQueues()
        {
            while (SharedConnEvents.TryDequeue(out _)) { }
            while (SharedLobbyCreated.TryDequeue(out _)) { }
            while (SharedLobbyMatches.TryDequeue(out _)) { }
            while (SharedLobbyEntered.TryDequeue(out _)) { }
        }

        private static ConnStatusEvent ParseConnStatus(IntPtr param)
        {
            // Verified against Dread Delusion Facepunch.Steamworks (SDK 1.48, Win64 pack=8):
            // 0x00: uint m_hConn
            // 0x08: m_info.m_identityRemote (int type @ 0x08, int size @ 0x0C, ulong steamId @ 0x10)
            // 0x98: uint m_info.m_hListenSocket
            // 0xB8: int  m_info.m_eState
            // 0xBC: int  m_info.m_eEndReason
            // 0x2C0: int m_eOldState
            uint conn = (uint)Marshal.ReadInt32(param, 0x00);
            int idType = Marshal.ReadInt32(param, 0x08);
            ulong peerId = idType == IdentitySteamId ? (ulong)Marshal.ReadInt64(param, 0x10) : 0;
            uint listenSocket = (uint)Marshal.ReadInt32(param, 0x98);
            int state = Marshal.ReadInt32(param, 0xB8);
            int endReason = Marshal.ReadInt32(param, 0xBC);
            return new ConnStatusEvent
            {
                Conn = conn,
                PeerId = peerId,
                ListenSocket = listenSocket,
                State = state,
                EndReason = endReason,
            };
        }

        public void Open()
        {
            if (_open) return;
            _stop = false;
            LastError = "";
            Status = "Connecting to Steam...";
            LobbyCode = string.IsNullOrEmpty(_password) ? "" : ShortCode(_password);
            _open = true;
            _thread = new Thread(RunLoop) { IsBackground = true, Name = "DelusionMP-Steam" };
            _thread.Start();
        }

        public void Close()
        {
            _stop = true;
            _open = false;
            try { _thread?.Join(900); } catch { }
            _thread = null;
            Status = "Disconnected";
        }

        public void Send(byte[] datagram)
        {
            if (!_open || datagram == null || datagram.Length == 0) return;
            if (_outbox.Count > 512) _outbox.TryDequeue(out _);
            _outbox.Enqueue(datagram);
        }

        public byte[] Poll()
        {
            return _inbox.TryDequeue(out var d) ? d : null;
        }

        /// <summary>Quick one-shot query of Steam friends currently playing Dread Delusion or Spacewar.</summary>
        public static List<(ulong id, string name)> QueryFriendsInGame(bool useSpacewar, out string error)
        {
            error = "";
            var list = new List<(ulong, string)>();
            SteamApi api = null;
            try
            {
                api = SteamApi.AttachOrInit(useSpacewar);
                if (api == null) { error = "Steam is not running or steam_api64.dll was not found."; return list; }
                var ev = new SteamEvents();
                api.Pump(ev);
                list = api.GetFriendsInGame();
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            finally
            {
                api?.Dispose();
            }
            return list;
        }

        private void RunLoop()
        {
            SteamApi api = null;
            Interlocked.Increment(ref _activeConsumers);
            try
            {
                ClearSharedQueues();
                api = SteamApi.AttachOrInit(_useSpacewar);
                LocalSteamId = api.GetSteamId();
                LocalSteamName = api.GetPersonaName();
                Log.Info($"steam: up as \"{LocalSteamName}\" ({LocalSteamId}) app={api.ActiveAppId} host={_isHost} pw={!string.IsNullOrEmpty(_password)} facepunch={api.UsingFacepunchDispatch}");

                var ev = new SteamEvents();
                string key = string.IsNullOrEmpty(_password) ? null : ComputeLobbyKey(_password);

                if (_isHost)
                    HostLoop(api, ev, key);
                else
                    JoinLoop(api, ev, key);
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Status = "Steam error: " + e.Message;
                Log.Error("steam transport failed: " + e.Message);
                _open = false;
            }
            finally
            {
                try { api?.Dispose(); } catch { }
                Interlocked.Decrement(ref _activeConsumers);
            }
        }

        private void HostLoop(SteamApi api, SteamEvents ev, string key)
        {
            uint listenSocket = api.CreateListenSocketP2P(VirtualPort);
            if (listenSocket == 0)
                throw new Exception("Steam could not open a P2P listen socket.");

            ulong lobby = 0;
            uint activeConn = 0;
            ulong activePeer = 0;
            var pendingConns = new Dictionary<uint, ulong>();

            try
            {
                if (key != null)
                {
                    Status = $"Opening Steam lobby (code {LobbyCode})...";
                    lobby = api.CreatePasswordLobby(ev, key, () => _stop);
                    Status = $"Hosting on Steam (code {LobbyCode}) — waiting for partner";
                    Log.Info($"steam: lobby {lobby} open, code {LobbyCode}, listen={listenSocket}");
                }
                else
                {
                    Status = "Hosting on Steam (Friends) — waiting for partner";
                    Log.Info($"steam: hosting for Steam friends, listen={listenSocket}");
                }

                var peerLastHeard = Stopwatch.StartNew();
                var beat = Stopwatch.StartNew();
                var msgs = new IntPtr[MsgMax];

                bool Allowed(ulong id)
                {
                    if (id == 0) return false;
                    if (lobby != 0) return api.LobbyContains(lobby, id);
                    return api.HasFriend(id);
                }

                while (!_stop)
                {
                    api.Pump(ev);

                    while (ev.ConnEvents.Count > 0)
                    {
                        var ce = ev.ConnEvents.Dequeue();
                        if (ce.State == ConnStateConnecting && ce.ListenSocket == listenSocket)
                        {
                            if (Allowed(ce.PeerId))
                            {
                                if (activeConn != 0 && activePeer != ce.PeerId)
                                {
                                    api.CloseConnection(activeConn);
                                    activeConn = 0;
                                    activePeer = 0;
                                }
                                int r = api.AcceptConnection(ce.Conn);
                                pendingConns[ce.Conn] = ce.PeerId;
                                Log.Info($"steam: accepted P2P connection {ce.Conn} from {ce.PeerId} ({api.GetFriendName(ce.PeerId)}) res={r}");
                            }
                            else
                            {
                                Log.Warn($"steam: refused P2P connection {ce.Conn} from {ce.PeerId} (not in lobby / not friend)");
                                api.CloseConnection(ce.Conn);
                            }
                        }
                        else if (ce.State == ConnStateConnected)
                        {
                            ulong peer = ce.PeerId != 0 ? ce.PeerId : (pendingConns.TryGetValue(ce.Conn, out var p) ? p : 0);
                            if (peer != 0 && (pendingConns.ContainsKey(ce.Conn) || ce.ListenSocket == listenSocket))
                            {
                                pendingConns.Remove(ce.Conn);
                                if (activeConn != 0 && activeConn != ce.Conn)
                                    api.CloseConnection(activeConn);
                                activeConn = ce.Conn;
                                activePeer = peer;
                                peerLastHeard.Restart();
                                PartnerName = api.GetFriendName(peer);
                                if (string.IsNullOrWhiteSpace(PartnerName) || PartnerName == "[unknown]")
                                    PartnerName = peer.ToString();
                                Status = $"Hosting on Steam — {PartnerName} connected";
                                Log.Info($"steam: partner {PartnerName} ({peer}) connected (conn={activeConn})");
                            }
                        }
                        else if (ce.State == ConnStateClosedByPeer || ce.State == ConnStateProblemDetectedLocally)
                        {
                            pendingConns.Remove(ce.Conn);
                            api.CloseConnection(ce.Conn);
                            if (ce.Conn == activeConn)
                            {
                                Log.Info($"steam: partner {activePeer} disconnected (state={ce.State} reason={ce.EndReason})");
                                activeConn = 0;
                                activePeer = 0;
                                PartnerName = "";
                                Status = lobby != 0
                                    ? $"Hosting on Steam (code {LobbyCode}) — waiting for partner"
                                    : "Hosting on Steam (Friends) — waiting for partner";
                            }
                        }
                    }

                    if (beat.ElapsedMilliseconds >= 1000)
                    {
                        beat.Restart();
                        if (activeConn != 0)
                        {
                            api.SendRaw(activeConn, KeepAliveFrame);
                            if (peerLastHeard.ElapsedMilliseconds > 20000)
                            {
                                Log.Warn($"steam: partner {activePeer} silent for 20s, dropped");
                                api.CloseConnection(activeConn);
                                activeConn = 0;
                                activePeer = 0;
                                PartnerName = "";
                                Status = lobby != 0
                                    ? $"Hosting on Steam (code {LobbyCode}) — waiting for partner"
                                    : "Hosting on Steam (Friends) — waiting for partner";
                            }
                        }
                    }

                    int got = 0;
                    if (activeConn != 0)
                    {
                        int n = api.Receive(activeConn, msgs);
                        if (n > 0) got += n;
                        for (int i = 0; i < n; i++)
                        {
                            var raw = api.ReadAndRelease(msgs[i]);
                            if (raw.Length == 0) continue;
                            peerLastHeard.Restart();

                            if (raw[0] == FrameCtrl)
                            {
                                if (BytesEqual(raw, GoodbyeFrame))
                                {
                                    Log.Info($"steam: partner {activePeer} said goodbye");
                                    api.CloseConnection(activeConn);
                                    activeConn = 0;
                                    activePeer = 0;
                                    PartnerName = "";
                                    Status = lobby != 0
                                        ? $"Hosting on Steam (code {LobbyCode}) — waiting for partner"
                                        : "Hosting on Steam (Friends) — waiting for partner";
                                    break;
                                }
                                continue;
                            }

                            if (raw[0] == FrameData && raw.Length > 1)
                            {
                                var payload = new byte[raw.Length - 1];
                                Buffer.BlockCopy(raw, 1, payload, 0, payload.Length);
                                _inbox.Enqueue(payload);
                            }
                        }
                    }

                    while (_outbox.TryDequeue(out var pkt))
                    {
                        if (activeConn != 0)
                            api.SendData(activeConn, pkt);
                    }

                    if (got <= 0) Thread.Sleep(1);
                }
            }
            finally
            {
                if (activeConn != 0)
                {
                    api.SendRaw(activeConn, GoodbyeFrame);
                    Thread.Sleep(50);
                    api.CloseConnection(activeConn);
                }
                foreach (var c in pendingConns.Keys)
                    api.CloseConnection(c);
                if (listenSocket != 0) api.CloseListenSocket(listenSocket);
                if (lobby != 0) api.LeaveLobby(lobby);
            }
        }

        private void JoinLoop(SteamApi api, SteamEvents ev, string key)
        {
            ulong hostId = 0;
            ulong lobby = 0;

            if (key != null)
            {
                Status = $"Searching Steam lobby (code {LobbyCode})...";
                var found = api.FindLobbyByPassword(ev, key, LocalSteamId, once: false, () => _stop);
                hostId = found.host;
                lobby = found.lobby;
            }
            else
            {
                Status = "Looking for Steam friend in Dread Delusion...";
                var sw = Stopwatch.StartNew();
                while (!_stop)
                {
                    api.Pump(ev);
                    var resolved = api.ResolveFriendHost(_wantPartner, out string err);
                    if (!string.IsNullOrEmpty(err)) throw new Exception(err);
                    if (resolved != 0) { hostId = resolved; break; }
                    if (sw.Elapsed.TotalSeconds > 240)
                        throw new Exception("No Steam friend is playing Dread Delusion (the host must start first).");
                    Thread.Sleep(1500);
                }
            }

            if (_stop || hostId == 0) return;

            PartnerName = api.GetFriendName(hostId);
            if (string.IsNullOrWhiteSpace(PartnerName) || PartnerName == "[unknown]")
                PartnerName = hostId.ToString();

            uint conn = 0;
            bool connected = false;

            try
            {
                Status = $"Connecting via Steam to {PartnerName}...";
                conn = api.ConnectP2P(hostId, VirtualPort);
                Log.Info($"steam: connecting P2P to {PartnerName} ({hostId}) conn={conn}");

                var beat = Stopwatch.StartNew();
                var lastHost = Stopwatch.StartNew();
                var nextReconnect = Stopwatch.StartNew();
                var msgs = new IntPtr[MsgMax];

                while (!_stop)
                {
                    api.Pump(ev);

                    while (ev.ConnEvents.Count > 0)
                    {
                        var ce = ev.ConnEvents.Dequeue();
                        if (ce.Conn != conn) continue;
                        if (ce.State == ConnStateConnected)
                        {
                            connected = true;
                            lastHost.Restart();
                            api.SendRaw(conn, KeepAliveFrame);
                            Status = $"Connected via Steam to {PartnerName}";
                            Log.Info($"steam: connected to host {PartnerName} ({hostId})");
                        }
                        else if (ce.State == ConnStateClosedByPeer || ce.State == ConnStateProblemDetectedLocally)
                        {
                            Log.Warn($"steam: connection to host closed (state={ce.State} reason={ce.EndReason})");
                            api.CloseConnection(conn);
                            conn = 0;
                            connected = false;
                            nextReconnect.Restart();
                            Status = $"Steam: {PartnerName} disconnected — reconnecting...";
                        }
                    }

                    if (conn == 0 && nextReconnect.ElapsedMilliseconds >= 3000)
                    {
                        nextReconnect.Restart();
                        if (key != null)
                        {
                            try
                            {
                                var found = api.FindLobbyByPassword(ev, key, LocalSteamId, once: true, () => _stop);
                                if (found.lobby != 0 && found.lobby != lobby)
                                {
                                    if (lobby != 0) api.LeaveLobby(lobby);
                                    hostId = found.host;
                                    lobby = found.lobby;
                                    Log.Info($"steam: re-found host {hostId} in lobby {lobby}");
                                }
                            }
                            catch { }
                        }
                        conn = api.ConnectP2P(hostId, VirtualPort);
                        Log.Info($"steam: reconnecting P2P to {hostId} conn={conn}");
                    }

                    if (beat.ElapsedMilliseconds >= 1000)
                    {
                        beat.Restart();
                        if (conn != 0 && connected)
                        {
                            api.SendRaw(conn, KeepAliveFrame);
                            if (lastHost.ElapsedMilliseconds > 20000)
                            {
                                Log.Warn("steam: host silent for 20s, reconnecting");
                                api.CloseConnection(conn);
                                conn = 0;
                                connected = false;
                                nextReconnect.Restart();
                                Status = $"Steam: {PartnerName} stopped answering — reconnecting...";
                            }
                        }
                    }

                    while (_outbox.TryDequeue(out var pkt))
                    {
                        if (conn != 0 && connected)
                            api.SendData(conn, pkt);
                    }

                    int got = 0;
                    if (conn != 0)
                    {
                        int n = api.Receive(conn, msgs);
                        if (n > 0) got += n;
                        for (int i = 0; i < n; i++)
                        {
                            var raw = api.ReadAndRelease(msgs[i]);
                            if (raw.Length == 0) continue;
                            lastHost.Restart();

                            if (raw[0] == FrameCtrl)
                            {
                                if (BytesEqual(raw, GoodbyeFrame))
                                {
                                    api.CloseConnection(conn);
                                    conn = 0;
                                    connected = false;
                                    nextReconnect.Restart();
                                    Status = $"Steam: {PartnerName} ended the session";
                                    break;
                                }
                                continue;
                            }

                            if (raw[0] == FrameData && raw.Length > 1)
                            {
                                var payload = new byte[raw.Length - 1];
                                Buffer.BlockCopy(raw, 1, payload, 0, payload.Length);
                                _inbox.Enqueue(payload);
                            }
                        }
                    }

                    if (got <= 0) Thread.Sleep(1);
                }
            }
            finally
            {
                if (conn != 0)
                {
                    if (connected)
                    {
                        api.SendRaw(conn, GoodbyeFrame);
                        Thread.Sleep(50);
                    }
                    api.CloseConnection(conn);
                }
                if (lobby != 0) api.LeaveLobby(lobby);
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // =====================================================================
        // Native steam_api64.dll bindings (ISteamNetworkingSockets v008+)
        // =====================================================================

        private struct ConnStatusEvent
        {
            public uint Conn;
            public ulong PeerId;
            public uint ListenSocket;
            public int State;
            public int EndReason;
        }

        private sealed class SteamEvents
        {
            public (int result, ulong lobby)? Created;
            public uint? Matches;
            public (ulong lobby, int response)? Entered;
            public readonly Queue<ConnStatusEvent> ConnEvents = new Queue<ConnStatusEvent>();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SteamIdentity
        {
            public int Kind;
            public int Size;
            public ulong SteamId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 120)]
            public byte[] Pad;

            public static SteamIdentity FromSteamId(ulong id) => new SteamIdentity
            {
                Kind = IdentitySteamId,
                Size = 8,
                SteamId = id,
                Pad = new byte[120],
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FriendGameInfo
        {
            public ulong GameId;
            public uint Ip;
            public ushort Port;
            public ushort QueryPort;
            public ulong Lobby;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CallbackMsg
        {
            public int User;
            public int Id;
            public IntPtr Param;
            public int Size;
        }

        private sealed class SteamApi : IDisposable
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr GetModuleHandleW(string name);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibraryW(string path);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string procName);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetIfaceFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFlatFn(IntPtr errBuf);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool InitFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetPipeFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RunFrameFn(int pipe);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool NextCbFn(int pipe, ref CallbackMsg msg);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeCbFn(int pipe);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate bool CallResultFn(int pipe, ulong call, IntPtr buf, int size, int expectedCb, out bool failed);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetAppIdFn(IntPtr utils);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GetSteamIdFn(IntPtr user);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PersonaFn(IntPtr friends);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FriendCountFn(IntPtr friends, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FriendByIdxFn(IntPtr friends, int idx, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FriendNameFn(IntPtr friends, ulong id);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool FriendGameFn(IntPtr friends, ulong id, ref FriendGameInfo info);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool HasFriendFn(IntPtr friends, ulong id, int flags);

            // ISteamNetworkingSockets
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CreateListenP2PFn(IntPtr socks, int vport, int nOpts, IntPtr pOpts);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ConnectP2PFn(IntPtr socks, ref SteamIdentity id, int vport, int nOpts, IntPtr pOpts);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AcceptConnFn(IntPtr socks, uint hConn);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool CloseConnFn(IntPtr socks, uint hPeer, int reason, [MarshalAs(UnmanagedType.LPStr)] string debug, bool linger);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool CloseListenFn(IntPtr socks, uint hSocket);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SendToConnFn(IntPtr socks, uint hConn, IntPtr data, uint len, int flags, out long msgNum);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RecvOnConnFn(IntPtr socks, uint hConn, IntPtr[] outMsgs, int max);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseMsgFn(IntPtr msg);

            // ISteamMatchmaking
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong CreateLobbyFn(IntPtr mm, int type, int max);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetLobbyDataFn(IntPtr mm, ulong lobby, [MarshalAs(UnmanagedType.LPStr)] string k, [MarshalAs(UnmanagedType.LPStr)] string v);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong RequestLobbiesFn(IntPtr mm);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FilterStrFn(IntPtr mm, [MarshalAs(UnmanagedType.LPStr)] string k, [MarshalAs(UnmanagedType.LPStr)] string v, int cmp);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FilterIntFn(IntPtr mm, int val);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong LobbyByIdxFn(IntPtr mm, int idx);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong LobbyOwnerFn(IntPtr mm, ulong lobby);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong JoinLobbyFn(IntPtr mm, ulong lobby);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LobbyMemCountFn(IntPtr mm, ulong lobby);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong LobbyMemFn(IntPtr mm, ulong lobby, int idx);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LeaveLobbyFn(IntPtr mm, ulong lobby);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InitRelayFn(IntPtr utils);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetCfgIntFn(IntPtr utils, int cfg, int val);

            private bool _weInitialized;
            public bool UsingFacepunchDispatch { get; private set; }
            public uint ActiveAppId { get; private set; }
            private int _pipe;
            private IntPtr _user, _friends, _socks, _mm;

            private VoidFn _shutdown;
            private RunFrameFn _runFrame;
            private NextCbFn _nextCb;
            private FreeCbFn _freeCb;
            private CallResultFn _callResult;
            private GetSteamIdFn _getSteamId;
            private PersonaFn _persona;
            private FriendCountFn _friendCount;
            private FriendByIdxFn _friendByIdx;
            private FriendNameFn _friendName;
            private FriendGameFn _friendGame;
            private HasFriendFn _hasFriend;
            private CreateListenP2PFn _createListenP2P;
            private ConnectP2PFn _connectP2P;
            private AcceptConnFn _acceptConn;
            private CloseConnFn _closeConn;
            private CloseListenFn _closeListen;
            private SendToConnFn _sendToConn;
            private RecvOnConnFn _recvOnConn;
            private ReleaseMsgFn _release;
            private CreateLobbyFn _createLobby;
            private SetLobbyDataFn _setLobbyData;
            private RequestLobbiesFn _requestLobbies;
            private FilterStrFn _filterStr;
            private FilterIntFn _filterDist;
            private FilterIntFn _filterCount;
            private LobbyByIdxFn _lobbyByIdx;
            private LobbyOwnerFn _lobbyOwner;
            private JoinLobbyFn _joinLobby;
            private LobbyMemCountFn _lobbyMemCount;
            private LobbyMemFn _lobbyMem;
            private LeaveLobbyFn _leaveLobby;

            private static T Sym<T>(IntPtr mod, string name) where T : Delegate
            {
                var p = GetProcAddress(mod, name);
                if (p == IntPtr.Zero) throw new Exception($"steam_api64.dll is missing {name}");
                return Marshal.GetDelegateForFunctionPointer<T>(p);
            }

            private static GetIfaceFn Iface(IntPtr mod, params string[] names)
            {
                foreach (var n in names)
                {
                    var p = GetProcAddress(mod, n);
                    if (p != IntPtr.Zero) return Marshal.GetDelegateForFunctionPointer<GetIfaceFn>(p);
                }
                throw new Exception($"steam_api64.dll lacks {string.Join(", ", names)}");
            }

            private static IntPtr FindOrLoadSteamDll()
            {
                var h = GetModuleHandleW("steam_api64.dll");
                if (h != IntPtr.Zero) return h;

                string dataPath = "";
                try { dataPath = UnityEngine.Application.dataPath; } catch { }
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(dataPath))
                {
                    candidates.Add(Path.Combine(dataPath, "Plugins", "x86_64", "steam_api64.dll"));
                    candidates.Add(Path.Combine(dataPath, "Plugins", "steam_api64.dll"));
                    candidates.Add(Path.Combine(dataPath, "..", "steam_api64.dll"));
                }
                try
                {
                    candidates.Add(Path.Combine(BepInEx.Paths.PluginPath, "steam_api64.dll"));
                    candidates.Add(Path.Combine(BepInEx.Paths.BepInExRootPath, "steam_api64.dll"));
                }
                catch { }

                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        var loaded = LoadLibraryW(c);
                        if (loaded != IntPtr.Zero) return loaded;
                    }
                }
                throw new Exception("steam_api64.dll not found in game folder.");
            }

            public static uint TryQueryActiveAppId()
            {
                try
                {
                    var mod = GetModuleHandleW("steam_api64.dll");
                    if (mod == IntPtr.Zero) return 0;
                    var pPipe = GetProcAddress(mod, "SteamAPI_GetHSteamPipe");
                    if (pPipe == IntPtr.Zero) return 0;
                    int pipe = Marshal.GetDelegateForFunctionPointer<GetPipeFn>(pPipe)();
                    if (pipe == 0) return 0;
                    foreach (var n in new[] { "SteamAPI_SteamUtils_v010", "SteamAPI_SteamUtils_v009" })
                    {
                        var pU = GetProcAddress(mod, n);
                        var pApp = GetProcAddress(mod, "SteamAPI_ISteamUtils_GetAppID");
                        if (pU != IntPtr.Zero && pApp != IntPtr.Zero)
                        {
                            var u = Marshal.GetDelegateForFunctionPointer<GetIfaceFn>(pU)();
                            if (u != IntPtr.Zero)
                                return Marshal.GetDelegateForFunctionPointer<GetAppIdFn>(pApp)(u);
                        }
                    }
                }
                catch { }
                return 0;
            }

            public static SteamApi AttachOrInit(bool useSpacewar)
            {
                var mod = FindOrLoadSteamDll();
                var api = new SteamApi();

                var userG = Iface(mod, "SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v020");
                var friendsG = Iface(mod, "SteamAPI_SteamFriends_v018", "SteamAPI_SteamFriends_v017", "SteamAPI_SteamFriends_v016");
                var socksG = Iface(mod,
                    "SteamAPI_SteamNetworkingSockets_SteamAPI_v012",
                    "SteamAPI_SteamNetworkingSockets_SteamAPI_v011",
                    "SteamAPI_SteamNetworkingSockets_SteamAPI_v009",
                    "SteamAPI_SteamNetworkingSockets_v008");
                var mmG = Iface(mod, "SteamAPI_SteamMatchmaking_v009");
                var getPipe = Sym<GetPipeFn>(mod, "SteamAPI_GetHSteamPipe");
                var manualInit = Sym<VoidFn>(mod, "SteamAPI_ManualDispatch_Init");

                api._shutdown = Sym<VoidFn>(mod, "SteamAPI_Shutdown");
                api._runFrame = Sym<RunFrameFn>(mod, "SteamAPI_ManualDispatch_RunFrame");
                api._nextCb = Sym<NextCbFn>(mod, "SteamAPI_ManualDispatch_GetNextCallback");
                api._freeCb = Sym<FreeCbFn>(mod, "SteamAPI_ManualDispatch_FreeLastCallback");
                api._callResult = Sym<CallResultFn>(mod, "SteamAPI_ManualDispatch_GetAPICallResult");
                api._getSteamId = Sym<GetSteamIdFn>(mod, "SteamAPI_ISteamUser_GetSteamID");
                api._persona = Sym<PersonaFn>(mod, "SteamAPI_ISteamFriends_GetPersonaName");
                api._friendCount = Sym<FriendCountFn>(mod, "SteamAPI_ISteamFriends_GetFriendCount");
                api._friendByIdx = Sym<FriendByIdxFn>(mod, "SteamAPI_ISteamFriends_GetFriendByIndex");
                api._friendName = Sym<FriendNameFn>(mod, "SteamAPI_ISteamFriends_GetFriendPersonaName");
                api._friendGame = Sym<FriendGameFn>(mod, "SteamAPI_ISteamFriends_GetFriendGamePlayed");
                api._hasFriend = Sym<HasFriendFn>(mod, "SteamAPI_ISteamFriends_HasFriend");

                api._createListenP2P = Sym<CreateListenP2PFn>(mod, "SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P");
                api._connectP2P = Sym<ConnectP2PFn>(mod, "SteamAPI_ISteamNetworkingSockets_ConnectP2P");
                api._acceptConn = Sym<AcceptConnFn>(mod, "SteamAPI_ISteamNetworkingSockets_AcceptConnection");
                api._closeConn = Sym<CloseConnFn>(mod, "SteamAPI_ISteamNetworkingSockets_CloseConnection");
                api._closeListen = Sym<CloseListenFn>(mod, "SteamAPI_ISteamNetworkingSockets_CloseListenSocket");
                api._sendToConn = Sym<SendToConnFn>(mod, "SteamAPI_ISteamNetworkingSockets_SendMessageToConnection");
                api._recvOnConn = Sym<RecvOnConnFn>(mod, "SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection");
                api._release = Sym<ReleaseMsgFn>(mod, "SteamAPI_SteamNetworkingMessage_t_Release");

                api._createLobby = Sym<CreateLobbyFn>(mod, "SteamAPI_ISteamMatchmaking_CreateLobby");
                api._setLobbyData = Sym<SetLobbyDataFn>(mod, "SteamAPI_ISteamMatchmaking_SetLobbyData");
                api._requestLobbies = Sym<RequestLobbiesFn>(mod, "SteamAPI_ISteamMatchmaking_RequestLobbyList");
                api._filterStr = Sym<FilterStrFn>(mod, "SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter");
                api._filterDist = Sym<FilterIntFn>(mod, "SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter");
                api._filterCount = Sym<FilterIntFn>(mod, "SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter");
                api._lobbyByIdx = Sym<LobbyByIdxFn>(mod, "SteamAPI_ISteamMatchmaking_GetLobbyByIndex");
                api._lobbyOwner = Sym<LobbyOwnerFn>(mod, "SteamAPI_ISteamMatchmaking_GetLobbyOwner");
                api._joinLobby = Sym<JoinLobbyFn>(mod, "SteamAPI_ISteamMatchmaking_JoinLobby");
                api._lobbyMemCount = Sym<LobbyMemCountFn>(mod, "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
                api._lobbyMem = Sym<LobbyMemFn>(mod, "SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex");
                api._leaveLobby = Sym<LeaveLobbyFn>(mod, "SteamAPI_ISteamMatchmaking_LeaveLobby");

                _sharedCallResult = api._callResult;
                _sharedGetPipe = getPipe;

                uint wantAppId = useSpacewar ? AppIdSpacewar : AppIdGame;

                int existingPipe = 0;
                try { existingPipe = getPipe(); } catch { }

                if (existingPipe != 0)
                {
                    uint active = SteamHooks.ActiveFacepunchAppId;
                    if (active == 0) active = TryQueryActiveAppId();
                    if (active != 0 && active != wantAppId)
                    {
                        string curName = active == AppIdSpacewar ? "Spacewar (480)" : $"Dread Delusion ({active})";
                        string wantName = useSpacewar ? "Spacewar (480)" : "Dread Delusion (1574240)";
                        throw new Exception($"Game is running as {curName}. Click 'Restart as {wantName}' to switch.");
                    }
                    api.ActiveAppId = active != 0 ? active : wantAppId;
                    api.UsingFacepunchDispatch = SteamHooks.IsFacepunchActive;
                }
                else
                {
                    Environment.SetEnvironmentVariable("SteamAppId", wantAppId.ToString());
                    Environment.SetEnvironmentVariable("SteamGameId", wantAppId.ToString());

                    var flatPtr = GetProcAddress(mod, "SteamAPI_InitFlat");
                    if (flatPtr != IntPtr.Zero)
                    {
                        var initFlat = Marshal.GetDelegateForFunctionPointer<InitFlatFn>(flatPtr);
                        IntPtr errBuf = Marshal.AllocHGlobal(1024);
                        try
                        {
                            Marshal.WriteByte(errBuf, 0);
                            int r = initFlat(errBuf);
                            if (r != 0)
                            {
                                string detail = Marshal.PtrToStringUTF8(errBuf) ?? "";
                                throw new Exception(r == 2
                                    ? "Steam client is not running or not logged in."
                                    : $"Steam init failed ({r}{(detail.Length > 0 ? ": " + detail : "")}).");
                            }
                        }
                        finally { Marshal.FreeHGlobal(errBuf); }
                    }
                    else
                    {
                        var init = Sym<InitFn>(mod, "SteamAPI_Init");
                        if (!init()) throw new Exception("SteamAPI_Init failed. Is Steam running and logged in?");
                    }
                    api._weInitialized = true;
                    api.ActiveAppId = wantAppId;
                    api.UsingFacepunchDispatch = false;
                    manualInit();
                }

                api._pipe = getPipe();
                api._user = userG();
                api._friends = friendsG();
                api._socks = socksG();
                api._mm = mmG();

                if (api._user == IntPtr.Zero || api._friends == IntPtr.Zero || api._socks == IntPtr.Zero || api._mm == IntPtr.Zero)
                    throw new Exception("Steam started, but networking/matchmaking interfaces are unavailable.");

                // Warm relay network & tune send queue
                try
                {
                    var utilsG = Iface(mod,
                        "SteamAPI_SteamNetworkingUtils_SteamAPI_v004",
                        "SteamAPI_SteamNetworkingUtils_SteamAPI_v003",
                        "SteamAPI_SteamNetworkingUtils_v003");
                    var utils = utilsG();
                    if (utils != IntPtr.Zero)
                    {
                        var pInit = GetProcAddress(mod, "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess");
                        if (pInit != IntPtr.Zero)
                            Marshal.GetDelegateForFunctionPointer<InitRelayFn>(pInit)(utils);

                        var pCfg = GetProcAddress(mod, "SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32");
                        if (pCfg != IntPtr.Zero)
                        {
                            var setCfg = Marshal.GetDelegateForFunctionPointer<SetCfgIntFn>(pCfg);
                            setCfg(utils, CfgSendBufferSize, SendBufferBytes);
                            setCfg(utils, CfgSendRateMin, SendRateMinBps);
                        }
                    }
                }
                catch { }

                return api;
            }

            public ulong GetSteamId() => _getSteamId(_user);
            public string GetPersonaName() => Marshal.PtrToStringUTF8(_persona(_friends)) ?? "";
            public string GetFriendName(ulong id) => Marshal.PtrToStringUTF8(_friendName(_friends, id)) ?? "";
            public bool HasFriend(ulong id) => _hasFriend(_friends, id, FriendImmediate);
            public void LeaveLobby(ulong lobby) => _leaveLobby(_mm, lobby);

            public uint CreateListenSocketP2P(int vport) => _createListenP2P(_socks, vport, 0, IntPtr.Zero);
            public uint ConnectP2P(ulong peerId, int vport)
            {
                var ident = SteamIdentity.FromSteamId(peerId);
                return _connectP2P(_socks, ref ident, vport, 0, IntPtr.Zero);
            }
            public int AcceptConnection(uint hConn) => _acceptConn(_socks, hConn);
            public void CloseConnection(uint hConn) { try { _closeConn(_socks, hConn, 0, null, false); } catch { } }
            public void CloseListenSocket(uint hSock) { try { _closeListen(_socks, hSock); } catch { } }

            public bool LobbyContains(ulong lobby, ulong id)
            {
                int n = _lobbyMemCount(_mm, lobby);
                for (int i = 0; i < n; i++)
                    if (_lobbyMem(_mm, lobby, i) == id) return true;
                return false;
            }

            public List<(ulong id, string name)> GetFriendsInGame()
            {
                var outList = new List<(ulong, string)>();
                int n = _friendCount(_friends, FriendImmediate);
                for (int i = 0; i < n; i++)
                {
                    ulong id = _friendByIdx(_friends, i, FriendImmediate);
                    var info = new FriendGameInfo();
                    if (_friendGame(_friends, id, ref info))
                    {
                        uint app = (uint)(info.GameId & 0xFFFFFF);
                        if (app == AppIdGame || app == AppIdSpacewar)
                            outList.Add((id, GetFriendName(id)));
                    }
                }
                return outList;
            }

            public ulong ResolveFriendHost(string want, out string err)
            {
                err = "";
                want = want?.Trim() ?? "";
                if (want.Length >= 15 && ulong.TryParse(want, out ulong sid))
                    return sid;

                bool byName = !(string.IsNullOrEmpty(want) || want.Equals("steam", StringComparison.OrdinalIgnoreCase) || want == "127.0.0.1");
                if (byName)
                {
                    int n = _friendCount(_friends, FriendImmediate);
                    for (int i = 0; i < n; i++)
                    {
                        ulong id = _friendByIdx(_friends, i, FriendImmediate);
                        if (GetFriendName(id).Equals(want, StringComparison.OrdinalIgnoreCase))
                            return id;
                    }
                    err = $"No Steam friend named \"{want}\" was found.";
                    return 0;
                }

                var playing = GetFriendsInGame();
                if (playing.Count == 0) return 0;
                if (playing.Count == 1) return playing[0].id;
                err = "Multiple friends are playing Dread Delusion. Enter a friend name or use a co-op password.";
                return 0;
            }

            public void SendData(uint hConn, byte[] payload)
            {
                var framed = new byte[payload.Length + 1];
                framed[0] = FrameData;
                Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);
                SendRaw(hConn, framed);
            }

            public void SendRaw(uint hConn, byte[] data)
            {
                var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    _sendToConn(_socks, hConn, handle.AddrOfPinnedObject(), (uint)data.Length, SendUnreliableNoNagle, out _);
                }
                finally { handle.Free(); }
            }

            public int Receive(uint hConn, IntPtr[] buffer) => _recvOnConn(_socks, hConn, buffer, buffer.Length);

            public byte[] ReadAndRelease(IntPtr msgPtr)
            {
                try
                {
                    // SteamNetworkingMessage_t / NetMsg (SDK 1.48+):
                    // 0x00: void* m_pData
                    // 0x08: int m_cbSize
                    IntPtr dataPtr = Marshal.ReadIntPtr(msgPtr, 0);
                    int size = Marshal.ReadInt32(msgPtr, 8);
                    if (dataPtr == IntPtr.Zero || size <= 0 || size > 65536)
                        return Array.Empty<byte>();
                    var copy = new byte[size];
                    Marshal.Copy(dataPtr, copy, 0, size);
                    return copy;
                }
                finally
                {
                    _release(msgPtr);
                }
            }

            public void Pump(SteamEvents ev)
            {
                if (UsingFacepunchDispatch)
                {
                    // Facepunch pumps ManualDispatch on the main thread; drain tapped events safely.
                    while (SharedConnEvents.TryDequeue(out var ce)) ev.ConnEvents.Enqueue(ce);
                    while (SharedLobbyCreated.TryDequeue(out var lc)) ev.Created = lc;
                    while (SharedLobbyMatches.TryDequeue(out var lm)) ev.Matches = lm;
                    while (SharedLobbyEntered.TryDequeue(out var le)) ev.Entered = le;
                    return;
                }

                _runFrame(_pipe);
                var m = new CallbackMsg();
                IntPtr resBuf = Marshal.AllocHGlobal(256);
                try
                {
                    while (_nextCb(_pipe, ref m))
                    {
                        try
                        {
                            if (m.Id == 703 && m.Param != IntPtr.Zero && m.Size >= 16)
                            {
                                ulong call = (ulong)Marshal.ReadInt64(m.Param, 0);
                                int cb = Marshal.ReadInt32(m.Param, 8);
                                if (_callResult(_pipe, call, resBuf, 256, cb, out bool failed) && !failed)
                                {
                                    if (cb == 513) ev.Created = (Marshal.ReadInt32(resBuf, 0), (ulong)Marshal.ReadInt64(resBuf, 8));
                                    else if (cb == 510) ev.Matches = (uint)Marshal.ReadInt32(resBuf, 0);
                                    else if (cb == 504) ev.Entered = ((ulong)Marshal.ReadInt64(resBuf, 0), Marshal.ReadInt32(resBuf, 16));
                                }
                                else if (failed)
                                {
                                    if (cb == 513) ev.Created = (-1, 0);
                                    else if (cb == 510) ev.Matches = 0;
                                    else if (cb == 504) ev.Entered = (0, -1);
                                }
                            }
                            else if (m.Id == 1221 && m.Param != IntPtr.Zero && m.Size >= 0xC0)
                            {
                                ev.ConnEvents.Enqueue(ParseConnStatus(m.Param));
                            }
                        }
                        finally
                        {
                            _freeCb(_pipe);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(resBuf);
                }
            }

            private bool WaitFor(SteamEvents ev, int timeoutMs, Func<bool> done, Func<bool> stopped)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs && !stopped())
                {
                    Pump(ev);
                    if (done()) return true;
                    Thread.Sleep(20);
                }
                return false;
            }

            public ulong CreatePasswordLobby(SteamEvents ev, string key, Func<bool> stopped)
            {
                ev.Created = null;
                _createLobby(_mm, LobbyPublic, LobbyMax);
                if (!WaitFor(ev, 30000, () => ev.Created.HasValue, stopped))
                    throw new Exception("Steam did not respond when creating the lobby.");
                var (res, lobby) = ev.Created.Value;
                if (res != 1 || lobby == 0)
                    throw new Exception($"Steam could not create the lobby (result {res}).");
                if (!_setLobbyData(_mm, lobby, "dmp", "1") || !_setLobbyData(_mm, lobby, "k", key))
                    throw new Exception("Steam refused the lobby metadata.");
                return lobby;
            }

            public (ulong host, ulong lobby) FindLobbyByPassword(SteamEvents ev, string key, ulong me, bool once, Func<bool> stopped)
            {
                var sw = Stopwatch.StartNew();
                while (!stopped())
                {
                    ev.Matches = null;
                    _filterStr(_mm, "k", key, 0);
                    _filterDist(_mm, 3);
                    _filterCount(_mm, 20);
                    _requestLobbies(_mm);

                    if (WaitFor(ev, 20000, () => ev.Matches.HasValue, stopped))
                    {
                        uint count = ev.Matches.Value;
                        for (int i = 0; i < count; i++)
                        {
                            ulong lobby = _lobbyByIdx(_mm, i);
                            if (lobby == 0) continue;
                            ev.Entered = null;
                            _joinLobby(_mm, lobby);
                            if (!WaitFor(ev, 20000, () => ev.Entered.HasValue, stopped) || ev.Entered.Value.response != EnterSuccess)
                                continue;
                            ulong owner = _lobbyOwner(_mm, lobby);
                            if (owner != 0 && owner != me)
                                return (owner, lobby);
                            _leaveLobby(_mm, lobby);
                        }
                    }

                    if (once) return (0, 0);
                    if (sw.Elapsed.TotalSeconds > 240)
                        throw new Exception("No host found with this co-op password (make sure the host started first).");
                    Thread.Sleep(2000);
                }
                return (0, 0);
            }

            public void Dispose()
            {
                if (_weInitialized)
                {
                    try { _shutdown?.Invoke(); } catch { }
                    _weInitialized = false;
                }
            }
        }
    }
}
