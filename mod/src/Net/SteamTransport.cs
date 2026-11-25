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
    /// In-process Steam P2P transport using the game's own steam_api64.dll via P/Invoke.
    /// Replaces the external launcher's Steam UDP tunnel completely (zero .exe files needed).
    ///
    /// Supports both discovery modes:
    ///   - Co-op password: SHA-256 x 20,000 lobby key on a public Steam Matchmaking lobby.
    ///   - Steam friends: auto-detect a Steam friend playing Dread Delusion/Spacewar,
    ///     or connect by friend name / SteamID64.
    ///
    /// If Dread Delusion already initialized Steam (via Facepunch.Steamworks), we reuse the
    /// live HSteamPipe without double-initializing or shutting down the game's Steam session.
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        public const uint AppIdGame = 1574240;
        public const uint AppIdSpacewar = 480;

        private const int FriendImmediate = 0x04;
        private const int SendUnreliableNoNagleRestart = 1 | 32;
        private const int MsgMax = 64;
        private const int ChData = 0;
        private const int ChCtrl = 1;
        private const int LobbyPublic = 2;
        private const int LobbyMax = 8;
        private const int EnterSuccess = 1;
        private const int IdentitySteamId = 16;
        private const int CfgSendBufferSize = 9;
        private const int CfgSendRateMin = 10;
        private const int SendBufferBytes = 64 * 1024;
        private const int SendRateMinBps = 256 * 1024;

        private static readonly byte[] HelloBytes = Encoding.ASCII.GetBytes("DMPSTEAM-HELLO");
        private static readonly byte[] KeepAliveBytes = Encoding.ASCII.GetBytes("KA");
        private static readonly byte[] GoodbyeBytes = Encoding.ASCII.GetBytes("BYE");

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
            try
            {
                api = SteamApi.AttachOrInit(_useSpacewar);
                LocalSteamId = api.GetSteamId();
                LocalSteamName = api.GetPersonaName();
                Log.Info($"steam: up as \"{LocalSteamName}\" ({LocalSteamId}) host={_isHost} pw={!string.IsNullOrEmpty(_password)}");

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
            }
        }

        private void HostLoop(SteamApi api, SteamEvents ev, string key)
        {
            ulong lobby = 0;
            if (key != null)
            {
                Status = $"Opening Steam lobby (code {LobbyCode})...";
                lobby = api.CreatePasswordLobby(ev, key, () => _stop);
                Status = $"Hosting on Steam (code {LobbyCode}) — waiting for partner";
                Log.Info($"steam: lobby {lobby} open, code {LobbyCode}");
            }
            else
            {
                Status = "Hosting on Steam (Friends) — waiting for partner";
                Log.Info("steam: hosting for Steam friends");
            }

            ulong activePeer = 0;
            var peerLastHeard = Stopwatch.StartNew();
            var waiting = new Dictionary<ulong, Stopwatch>();
            var beat = Stopwatch.StartNew();
            var helloTimer = Stopwatch.StartNew();
            var msgs = new IntPtr[MsgMax];

            bool Allowed(ulong id)
            {
                if (lobby != 0) return api.LobbyContains(lobby, id);
                return api.HasFriend(id);
            }

            while (!_stop)
            {
                api.Pump(ev);

                while (ev.Requests.Count > 0)
                {
                    ulong id = ev.Requests.Dequeue();
                    if (!waiting.ContainsKey(id)) waiting[id] = Stopwatch.StartNew();
                }

                if (waiting.Count > 0)
                {
                    var done = new List<ulong>();
                    foreach (var kv in waiting)
                    {
                        if (Allowed(kv.Key))
                        {
                            bool ok = api.AcceptSession(kv.Key);
                            Log.Info($"steam: accepted session request from {kv.Key} ({api.GetFriendName(kv.Key)}) ok={ok}");
                            done.Add(kv.Key);
                        }
                        else if (kv.Value.ElapsedMilliseconds > 60000)
                        {
                            Log.Warn($"steam: refused session request from {kv.Key} (not allowed)");
                            done.Add(kv.Key);
                        }
                    }
                    foreach (var d in done) waiting.Remove(d);
                }

                while (ev.Failed.Count > 0)
                {
                    ulong id = ev.Failed.Dequeue();
                    if (id == activePeer)
                    {
                        Log.Warn($"steam: session failed with {id}");
                        activePeer = 0;
                        PartnerName = "";
                        Status = lobby != 0
                            ? $"Hosting on Steam (code {LobbyCode}) — partner lost, waiting"
                            : "Hosting on Steam (Friends) — partner lost, waiting";
                    }
                }

                if (beat.ElapsedMilliseconds >= 1000)
                {
                    beat.Restart();
                    if (activePeer != 0)
                    {
                        api.Send(activePeer, KeepAliveBytes, ChCtrl);
                        if (peerLastHeard.ElapsedMilliseconds > 20000)
                        {
                            Log.Warn($"steam: partner {activePeer} silent for 20s, dropped");
                            activePeer = 0;
                            PartnerName = "";
                            Status = lobby != 0
                                ? $"Hosting on Steam (code {LobbyCode}) — waiting for partner"
                                : "Hosting on Steam (Friends) — waiting for partner";
                        }
                    }
                }

                if (lobby == 0 && activePeer == 0 && helloTimer.ElapsedMilliseconds >= 3000)
                {
                    helloTimer.Restart();
                    foreach (var (fid, _) in api.GetFriendsInGame())
                        api.Send(fid, HelloBytes, ChData);
                }

                int got = 0;
                foreach (int ch in new[] { ChData, ChCtrl })
                {
                    int n = api.Receive(ch, msgs);
                    if (n > 0) got += n;
                    for (int i = 0; i < n; i++)
                    {
                        var (peerId, data) = api.ReadAndRelease(msgs[i]);
                        if (peerId == 0) continue;

                        if (ch == ChCtrl && BytesEqual(data, GoodbyeBytes))
                        {
                            if (peerId == activePeer)
                            {
                                Log.Info($"steam: partner {peerId} said goodbye");
                                activePeer = 0;
                                PartnerName = "";
                                Status = lobby != 0
                                    ? $"Hosting on Steam (code {LobbyCode}) — waiting for partner"
                                    : "Hosting on Steam (Friends) — waiting for partner";
                            }
                            continue;
                        }

                        if (activePeer == 0 && Allowed(peerId))
                        {
                            activePeer = peerId;
                            PartnerName = api.GetFriendName(peerId);
                            if (string.IsNullOrWhiteSpace(PartnerName) || PartnerName == "[unknown]")
                                PartnerName = peerId.ToString();
                            Status = $"Hosting on Steam — {PartnerName} connected";
                            Log.Info($"steam: partner {PartnerName} ({peerId}) connected");
                        }

                        if (peerId == activePeer)
                        {
                            peerLastHeard.Restart();
                            if (ch == ChData && data.Length > 0 && !BytesEqual(data, HelloBytes))
                                _inbox.Enqueue(data);
                        }
                    }
                }

                while (_outbox.TryDequeue(out var pkt))
                {
                    if (activePeer != 0)
                        api.Send(activePeer, pkt, ChData);
                }

                if (got <= 0) Thread.Sleep(1);
            }

            if (activePeer != 0) api.Send(activePeer, GoodbyeBytes, ChCtrl);
            if (lobby != 0) api.LeaveLobby(lobby);
            Thread.Sleep(80);
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
            Status = $"Connected via Steam to {PartnerName}";
            Log.Info($"steam: joining {PartnerName} ({hostId})");

            var beat = Stopwatch.StartNew();
            var lastHost = Stopwatch.StartNew();
            var nextSearch = Stopwatch.StartNew();
            bool lost = false;
            var msgs = new IntPtr[MsgMax];

            api.Send(hostId, KeepAliveBytes, ChCtrl);

            while (!_stop)
            {
                api.Pump(ev);

                while (ev.Requests.Count > 0)
                {
                    ulong id = ev.Requests.Dequeue();
                    if (id == hostId) api.AcceptSession(id);
                }
                while (ev.Failed.Count > 0)
                {
                    if (ev.Failed.Dequeue() == hostId)
                        Log.Warn("steam: session to host failed");
                }

                if (beat.ElapsedMilliseconds >= 1000)
                {
                    beat.Restart();
                    api.Send(hostId, KeepAliveBytes, ChCtrl);

                    if (lost && key != null && nextSearch.ElapsedMilliseconds >= 5000)
                    {
                        nextSearch.Restart();
                        try
                        {
                            var found = api.FindLobbyByPassword(ev, key, LocalSteamId, once: true, () => _stop);
                            if (found.lobby != 0 && found.lobby != lobby)
                            {
                                if (lobby != 0) api.LeaveLobby(lobby);
                                hostId = found.host;
                                lobby = found.lobby;
                                api.Send(hostId, KeepAliveBytes, ChCtrl);
                                Log.Info($"steam: re-found host {hostId} in lobby {lobby}");
                            }
                        }
                        catch { }
                    }

                    if (!lost && lastHost.ElapsedMilliseconds > 20000)
                    {
                        lost = true;
                        Status = $"Steam: {PartnerName} stopped answering — reconnecting...";
                        Log.Warn("steam: host silent for 20s");
                    }
                }

                while (_outbox.TryDequeue(out var pkt))
                    api.Send(hostId, pkt, ChData);

                int got = 0;
                foreach (int ch in new[] { ChData, ChCtrl })
                {
                    int n = api.Receive(ch, msgs);
                    if (n > 0) got += n;
                    for (int i = 0; i < n; i++)
                    {
                        var (peerId, data) = api.ReadAndRelease(msgs[i]);
                        if (peerId != hostId) continue;

                        if (ch == ChCtrl && BytesEqual(data, GoodbyeBytes))
                        {
                            lost = true;
                            Status = $"Steam: {PartnerName} ended the session";
                            continue;
                        }

                        lastHost.Restart();
                        if (lost)
                        {
                            lost = false;
                            Status = $"Connected via Steam to {PartnerName}";
                        }

                        if (ch == ChData && data.Length > 0 && !BytesEqual(data, HelloBytes))
                            _inbox.Enqueue(data);
                    }
                }

                if (got <= 0) Thread.Sleep(1);
            }

            api.Send(hostId, GoodbyeBytes, ChCtrl);
            if (lobby != 0) api.LeaveLobby(lobby);
            Thread.Sleep(80);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // =====================================================================
        // Native steam_api64.dll bindings
        // =====================================================================

        private sealed class SteamEvents
        {
            public (int result, ulong lobby)? Created;
            public uint? Matches;
            public (ulong lobby, int response)? Entered;
            public readonly Queue<ulong> Requests = new Queue<ulong>();
            public readonly Queue<ulong> Failed = new Queue<ulong>();
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

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetIfaceFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFlatFn(IntPtr errBuf);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool InitFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPipeFn();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RunFrameFn(int pipe);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool NextCbFn(int pipe, ref CallbackMsg msg);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeCbFn(int pipe);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool CallResultFn(int pipe, ulong call, IntPtr buf, int size, int expectedCb, out bool failed);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GetSteamIdFn(IntPtr user);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PersonaFn(IntPtr friends);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FriendCountFn(IntPtr friends, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FriendByIdxFn(IntPtr friends, int idx, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FriendNameFn(IntPtr friends, ulong id);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool FriendGameFn(IntPtr friends, ulong id, ref FriendGameInfo info);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool HasFriendFn(IntPtr friends, ulong id, int flags);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SendMsgFn(IntPtr msgs, ref SteamIdentity id, IntPtr data, uint len, int flags, int ch);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RecvMsgFn(IntPtr msgs, int ch, IntPtr[] outMsgs, int max);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool AcceptFn(IntPtr msgs, ref SteamIdentity id);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseMsgFn(IntPtr msg);
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
            private int _pipe;
            private IntPtr _user, _friends, _msgs, _mm;

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
            private SendMsgFn _send;
            private RecvMsgFn _receive;
            private AcceptFn _accept;
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
                // 1. Reuse already-loaded steam_api64.dll (Dread Delusion loads it via Facepunch.Steamworks)
                var h = GetModuleHandleW("steam_api64.dll");
                if (h != IntPtr.Zero) return h;

                // 2. Try game's Plugins folder and BepInEx plugins folder
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

            public static SteamApi AttachOrInit(bool useSpacewar)
            {
                var mod = FindOrLoadSteamDll();
                var api = new SteamApi();

                var userG = Iface(mod, "SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v020");
                var friendsG = Iface(mod, "SteamAPI_SteamFriends_v018", "SteamAPI_SteamFriends_v017", "SteamAPI_SteamFriends_v016");
                var msgsG = Iface(mod, "SteamAPI_SteamNetworkingMessages_SteamAPI_v002");
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
                api._send = Sym<SendMsgFn>(mod, "SteamAPI_ISteamNetworkingMessages_SendMessageToUser");
                api._receive = Sym<RecvMsgFn>(mod, "SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel");
                api._accept = Sym<AcceptFn>(mod, "SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser");
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

                // Check if Steam is already initialized by the game
                int existingPipe = 0;
                try { existingPipe = getPipe(); } catch { }

                if (existingPipe == 0)
                {
                    uint appId = useSpacewar ? AppIdSpacewar : AppIdGame;
                    Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
                    Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());

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
                }

                manualInit();
                api._pipe = getPipe();
                api._user = userG();
                api._friends = friendsG();
                api._msgs = msgsG();
                api._mm = mmG();

                if (api._user == IntPtr.Zero || api._friends == IntPtr.Zero || api._msgs == IntPtr.Zero || api._mm == IntPtr.Zero)
                    throw new Exception("Steam started, but networking/matchmaking interfaces are unavailable.");

                // Warm relay network & tune send queue
                try
                {
                    var utilsG = Iface(mod, "SteamAPI_SteamNetworkingUtils_SteamAPI_v004", "SteamAPI_SteamNetworkingUtils_SteamAPI_v003");
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
            public bool AcceptSession(ulong id) { var ident = SteamIdentity.FromSteamId(id); return _accept(_msgs, ref ident); }
            public void LeaveLobby(ulong lobby) => _leaveLobby(_mm, lobby);

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

            public void Send(ulong peerId, byte[] data, int channel)
            {
                var ident = SteamIdentity.FromSteamId(peerId);
                var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    _send(_msgs, ref ident, handle.AddrOfPinnedObject(), (uint)data.Length, SendUnreliableNoNagleRestart, channel);
                }
                finally { handle.Free(); }
            }

            public int Receive(int channel, IntPtr[] buffer) => _receive(_msgs, channel, buffer, buffer.Length);

            public (ulong peerId, byte[] data) ReadAndRelease(IntPtr msgPtr)
            {
                try
                {
                    // SteamNetworkingMessage_t (SDK 1.51+):
                    // 0x00: void* m_pData
                    // 0x08: int m_cbSize
                    // 0x0C: HSteamNetConnection m_conn
                    // 0x10: SteamNetworkingIdentity m_identityPeer (int kind @ 0x10, int size @ 0x14, ulong steamId @ 0x18)
                    IntPtr dataPtr = Marshal.ReadIntPtr(msgPtr, 0);
                    int size = Marshal.ReadInt32(msgPtr, 8);
                    int kind = Marshal.ReadInt32(msgPtr, 16);
                    ulong peerId = kind == IdentitySteamId ? (ulong)Marshal.ReadInt64(msgPtr, 24) : 0;

                    byte[] copy = Array.Empty<byte>();
                    if (dataPtr != IntPtr.Zero && size > 0 && size <= 65536)
                    {
                        copy = new byte[size];
                        Marshal.Copy(dataPtr, copy, 0, size);
                    }
                    return (peerId, copy);
                }
                finally
                {
                    _release(msgPtr);
                }
            }

            public void Pump(SteamEvents ev)
            {
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
                            else if (m.Id == 1251 && m.Param != IntPtr.Zero && m.Size >= 16)
                            {
                                if (Marshal.ReadInt32(m.Param, 0) == IdentitySteamId)
                                    ev.Requests.Enqueue((ulong)Marshal.ReadInt64(m.Param, 8));
                            }
                            else if (m.Id == 1252 && m.Param != IntPtr.Zero && m.Size >= 16)
                            {
                                if (Marshal.ReadInt32(m.Param, 0) == IdentitySteamId)
                                    ev.Failed.Enqueue((ulong)Marshal.ReadInt64(m.Param, 8));
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
