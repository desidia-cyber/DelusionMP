using System;
using System.Collections.Generic;
using System.IO;
using DelusionMP.Core;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>Local voice/text-chat settings (the launcher writes them). Never sent on the wire.</summary>
    public static class VoiceConfig
    {
        public static bool ChatEnabled = true;
        public static string ChatKey = "T";
        public static bool VoiceChat = true;
        public static int VoiceMode;          // 0 = push to talk, 1 = always on (gated)
        public static string VoiceKey = "V";
        public static int VoiceRangeM = 80;   // 0 = no proximity
        public static int VoiceVolumePct = 100;
        public static int VoiceMicPct = 100;
        public static int VoiceAwayPct = 40;
    }

    /// <summary>
    /// Keys read the way the game reads them: through the Input System's keyboard device
    /// (the game is built on the Input System; the legacy Input class may be disabled).
    /// Names are Input System key names (T, V, F1, LeftCtrl, Tab ...); a few legacy KeyCode
    /// spellings are accepted too.
    /// </summary>
    internal static class Keys
    {
        private static readonly Dictionary<string, int> Cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static bool _legacyBroken;

        private static bool TryKey(string name, out UnityEngine.InputSystem.Key key)
        {
            key = UnityEngine.InputSystem.Key.None;
            if (string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            name = name.Trim();
            if (Cache.TryGetValue(name, out var v)) { key = (UnityEngine.InputSystem.Key)v; return v != 0; }
            string n = name;
            if (n.Equals("LeftControl", StringComparison.OrdinalIgnoreCase)) n = "LeftCtrl";
            else if (n.Equals("RightControl", StringComparison.OrdinalIgnoreCase)) n = "RightCtrl";
            else if (n.Equals("Return", StringComparison.OrdinalIgnoreCase)) n = "Enter";
            else if (n.Equals("BackQuote", StringComparison.OrdinalIgnoreCase)) n = "Backquote";
            else if (n.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase) && n.Length == 6) n = "Digit" + n.Substring(5);
            if (!Enum.TryParse(n, true, out key)) key = UnityEngine.InputSystem.Key.None;
            Cache[name] = (int)key;
            return key != UnityEngine.InputSystem.Key.None;
        }

        public static bool Held(string name)
        {
            if (!TryKey(name, out var k)) return false;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null) return kb[k].isPressed;
            }
            catch { }
            return LegacyHeld(name);
        }

        public static bool Pressed(string name)
        {
            if (!TryKey(name, out var k)) return false;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null) return kb[k].wasPressedThisFrame;
            }
            catch { }
            return false;
        }

        private static bool LegacyHeld(string name)
        {
            if (_legacyBroken) return false;
            try { return Enum.TryParse(name.Trim(), true, out KeyCode kc) && Input.GetKey(kc); }
            catch { _legacyBroken = true; return false; }
        }
    }

    /// <summary>
    /// Proximity voice chat and text chat, ported from DollsMP's design to Unity.
    ///
    /// Voice: the game's own microphone (Unity Microphone) at 16 kHz mono, 20 ms frames,
    /// G.711 mu-law (8 bits per sample, 128 kbit/s) over the session as unreliable packets
    /// with a sequence number. The receiver keeps a small jitter buffer, conceals short
    /// losses with a fading repeat, and feeds a looping clip that is always written ahead of
    /// the playhead - with silence when nothing arrives, so old audio is never replayed.
    ///
    /// Text: reliable packets carrying the sender's name. While the line is open the game's
    /// input maps are switched off so typing does not move the player.
    /// </summary>
    public static class VoiceSync
    {
        private const int Rate = 16000;
        private const int Frame = 320;              // 20 ms at 16 kHz
        private const byte FmtPcm16 = 1, FmtMuLaw = 2;
        private const int JitterMax = 16;           // 320 ms
        private const int ConcealMax = 5;           // 100 ms
        private const int Lead = 1600;              // keep ~100 ms written ahead of the playhead
        private const int Guard = 6400;             // and keep the 400 ms after it silent
        private static readonly float[] Silence = new float[Frame];

        private static readonly object Gate = new object();
        private static readonly Dictionary<ushort, short[]> Incoming = new Dictionary<ushort, short[]>();
        private static readonly Queue<float> Capture = new Queue<float>();

        private static ushort _seq;
        private static ushort _next;
        private static bool _started;
        private static short[] _lastFrame;
        private static int _concealLeft;
        private static float _concealGain = 1f;
        private static float _lastHeard = -10f;

        private static string _device;
        private static AudioClip _mic;
        private static float[] _micBuf;
        private static int _micPos;
        private static bool _micWarned;

        private static AudioClip _outClip;
        private static AudioSource _source;
        private static int _writePos;
        private static bool _playing;
        private static float _outGain = 1f;

        private static float _gateUntil;
        private static float _pttTail;
        private static bool _wasSending;

        // test hooks (rig: no microphone, muted)
        private static byte[] _testIn;
        private static int _testPos;
        private static float _testNext;
        private static bool _testChecked;
        private static float _testTalkUntil;
        private static FileStream _dump;

        // stats
        private static int _sent, _recv, _played, _concealed, _dropped;

        private static readonly List<string> Lines = new List<string>();
        private static bool _chatOpen;
        private static string _chatText = "";
        private static float _chatHideAt;
        private static int _chatFocusFrames;

        public static bool ChatOpen => _chatOpen;
        public static string ChatText { get => _chatText; set => _chatText = value ?? ""; }
        public static IReadOnlyList<string> ChatLines => Lines;
        public static float ChatHideAt => _chatHideAt;
        public static bool Sending => _wasSending;
        public static bool Hearing => Time.realtimeSinceStartup - _lastHeard < 0.6f;
        internal static int ChatFocusFrames { get => _chatFocusFrames; set => _chatFocusFrames = value; }

        public static string Stats =>
            $"voice={(VoiceConfig.VoiceChat ? "on" : "off")} mode={(VoiceConfig.VoiceMode == 1 ? "open" : "ptt:" + VoiceConfig.VoiceKey)} " +
            $"mic={(_testIn != null ? "test-file" : _device ?? "none")} sending={_wasSending} hearing={Hearing} " +
            $"sent={_sent} recv={_recv} played={_played} concealed={_concealed} dropped={_dropped} buffered={Incoming.Count} " +
            $"gain={_outGain:F2} spatial={(_source != null ? _source.spatialBlend : -1):F0} " +
            $"head={(_source != null ? _source.timeSamples : -1)} playing={(_source != null && _source.isPlaying)} listenerPause={AudioListener.pause} " +
            $"chat={(VoiceConfig.ChatEnabled ? "on:" + VoiceConfig.ChatKey : "off")} open={_chatOpen} lines={Lines.Count}";

        // ---------------------------------------------------------------- lifecycle

        public static void Init()
        {
            EnsurePlayer();
            Game.Log.Info($"voice ready (16 kHz mono mu-law, {Frame} samples/frame, mic: {Devices()})");
        }

        /// <summary>
        /// The playback object. Created on demand: the game tears down objects made during
        /// boot when it loads its scenes, so this is re-made whenever it has gone.
        /// </summary>
        private static bool EnsurePlayer()
        {
            if (_source != null && _outClip != null) return true;
            _playing = false;
            try
            {
                var holder = new GameObject("DelusionMP_Voice");
                holder.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(holder);
                _source = holder.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.loop = true;
                _source.spatialBlend = 0f;
                _source.dopplerLevel = 0f;
                _source.rolloffMode = AudioRolloffMode.Linear;
                _source.bypassEffects = true;
                _source.bypassReverbZones = true;
                _source.ignoreListenerPause = true;    // menus pause game audio; the partner keeps talking
                _source.priority = 0;
                // A streaming clip needs a PCMReaderCallback delegate, which IL2CPP interop
                // cannot bridge for float[]; a looping one-second clip written ahead of the
                // playhead does the same job.
                _outClip = AudioClip.Create("DelusionMP_Voice_In", Rate, 1, Rate, false);
                _outClip.hideFlags = HideFlags.HideAndDontSave;
                _source.clip = _outClip;
                Game.Log.Info("voice playback object created");
                return true;
            }
            catch (Exception e)
            {
                Game.Log.Warn($"voice playback could not start: {e.Message}");
                return false;
            }
        }

        public static void Shutdown()
        {
            StopMic();
            StopPlayback();
            try { if (_outClip != null) UnityEngine.Object.Destroy(_outClip); } catch { }
            try { _dump?.Dispose(); } catch { }
            _dump = null;
            BlockGameInput(false);
        }

        private static string Devices()
        {
            try
            {
                var d = Microphone.devices;
                return d == null || d.Length == 0 ? "none" : string.Join(", ", d);
            }
            catch { return "none"; }
        }

        /// <summary>Test hook: talk (as if the push-to-talk key were held) for this many seconds.</summary>
        public static void TestTalk(float seconds) => _testTalkUntil = Time.realtimeSinceStartup + seconds;

        // ---------------------------------------------------------------- per-frame

        public static void Tick()
        {
            var session = Session.Instance;
            bool live = session != null && session.Connected && VoiceConfig.VoiceChat;
            if (!live)
            {
                StopMic();
                StopPlayback();
                _wasSending = false;
                return;
            }

            float now = Time.realtimeSinceStartup;
            UpdateSpatial();
            PumpPlayback();

            if (!CaptureMic(now)) { _wasSending = false; return; }
            float gain = VoiceConfig.VoiceMicPct / 100f;
            while (Capture.Count >= Frame)
            {
                var pcm = new short[Frame];
                float sum = 0f;
                for (int i = 0; i < Frame; i++)
                {
                    float v = Capture.Dequeue() * gain;
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    pcm[i] = (short)(v * 32767f);
                    sum += v * v;
                }
                float rms = (float)Math.Sqrt(sum / Frame);
                // Always-on mode: a noise gate with a 400 ms hangover, like the DollsMP sidecar.
                if (VoiceConfig.VoiceMode == 1 && rms > 0.008f) _gateUntil = now + 0.4f;

                bool send = WantsToSend(now);
                if (send) SendFrame(pcm);
                _wasSending = send;
            }
        }

        private static bool WantsToSend(float now)
        {
            if (now < _testTalkUntil) return true;
            if (_chatOpen) return VoiceConfig.VoiceMode == 1 && now < _gateUntil;   // typing T/V must not key the mic
            if (Keys.Held(VoiceConfig.VoiceKey)) _pttTail = now + 0.2f;             // keep the end of the last word
            if (VoiceConfig.VoiceMode == 1) return now < _gateUntil;
            return now < _pttTail;
        }

        private static void UpdateSpatial()
        {
            if (_source == null) return;
            try
            {
                float gain = VoiceConfig.VoiceVolumePct / 100f;
                _source.volume = 1f;
                _source.spatialBlend = 0f;
                if (VoiceConfig.VoiceRangeM > 0)
                {
                    if (PlayerSync.TryPeerVoicePose(out var pos, out bool fresh) && fresh)
                    {
                        _source.transform.position = pos;
                        _source.spatialBlend = 1f;
                        _source.minDistance = Mathf.Min(8f, VoiceConfig.VoiceRangeM * 0.5f);
                        _source.maxDistance = VoiceConfig.VoiceRangeM;
                    }
                    else
                    {
                        // The partner is in a menu, loading or not spawned: heard flat, quieter.
                        gain *= VoiceConfig.VoiceAwayPct / 100f;
                    }
                }
                _outGain = gain;
            }
            catch { }
        }

        private static void SendFrame(short[] pcm)
        {
            var packet = new byte[4 + Frame];
            packet[0] = (byte)_seq; packet[1] = (byte)(_seq >> 8); _seq++;
            packet[2] = FmtMuLaw; packet[3] = 0;
            for (int i = 0; i < Frame; i++) packet[4 + i] = MuLawEncode(pcm[i]);
            Session.Instance.SendUnreliable(PacketKind.Voice, packet);
            _sent++;
        }

        // ---------------------------------------------------------------- microphone

        private static bool CaptureMic(float now)
        {
            if (!_testChecked)
            {
                _testChecked = true;
                try
                {
                    var p = Path.Combine(BepInEx.Paths.BepInExRootPath, "ddmp_voice_test_in.raw");
                    if (File.Exists(p)) { _testIn = File.ReadAllBytes(p); Game.Log.Info($"voice: test input {p} ({_testIn.Length / 2} samples) replaces the microphone"); }
                }
                catch { }
            }
            if (_testIn != null && _testIn.Length >= 2)
            {
                // Real-time pacing, like a microphone: one frame of samples per 20 ms.
                if (_testNext == 0f) _testNext = now;
                while (_testNext <= now)
                {
                    for (int i = 0; i < Frame; i++)
                    {
                        if (_testPos + 1 >= _testIn.Length) _testPos = 0;
                        short s = (short)(_testIn[_testPos] | (_testIn[_testPos + 1] << 8));
                        _testPos += 2;
                        Capture.Enqueue(s / 32768f);
                    }
                    _testNext += 0.02f;
                }
                return true;
            }
            try
            {
                if (_mic == null)
                {
                    var devices = Microphone.devices;
                    if (devices == null || devices.Length == 0)
                    {
                        if (!_micWarned) { Game.Log.Warn("no microphone found: voice is receive-only"); _micWarned = true; }
                        return false;
                    }
                    _device = devices[0];
                    _mic = Microphone.Start(_device, true, 1, Rate);
                    _micBuf = new float[_mic != null ? _mic.samples : Rate];
                    _micPos = Microphone.GetPosition(_device);
                    Game.Log.Info($"microphone open: {_device} ({_mic?.frequency} Hz)");
                }
                int len = _micBuf.Length;
                int pos = Microphone.GetPosition(_device);
                int delta = pos - _micPos;
                if (delta < 0) delta += len;
                if (delta <= 0) return true;
                _mic.GetData(_micBuf, 0);
                for (int i = 0; i < delta; i++)
                {
                    int idx = _micPos + i;
                    if (idx >= len) idx -= len;
                    Capture.Enqueue(_micBuf[idx]);
                }
                _micPos = pos;
                // Never let a stalled consumer build up seconds of delay.
                while (Capture.Count > Rate / 4) Capture.Dequeue();
                return true;
            }
            catch (Exception e)
            {
                if (!_micWarned) { Game.Log.Warn($"microphone capture failed: {e.Message}"); _micWarned = true; }
                StopMic();
                return false;
            }
        }

        private static void StopMic()
        {
            try { if (_device != null && Microphone.IsRecording(_device)) Microphone.End(_device); } catch { }
            _mic = null;
            _device = null;
            _micBuf = null;
            Capture.Clear();
            _testNext = 0f;
        }

        // ---------------------------------------------------------------- receive / jitter

        public static void Receive(byte[] packet)
        {
            if (packet == null || packet.Length < 4 || !VoiceConfig.VoiceChat) return;
            ushort seq = (ushort)(packet[0] | (packet[1] << 8));
            short[] pcm;
            if (packet[2] == FmtMuLaw && packet.Length >= 4 + Frame)
            {
                pcm = new short[Frame];
                for (int i = 0; i < Frame; i++) pcm[i] = MuLawDecode(packet[4 + i]);
            }
            else if (packet[2] == FmtPcm16 && packet.Length >= 4 + Frame * 2)
            {
                pcm = new short[Frame];
                Buffer.BlockCopy(packet, 4, pcm, 0, Frame * 2);
            }
            else return;

            lock (Gate)
            {
                _recv++;
                // After a pause (or the partner's restart) the stream starts wherever it is.
                if (!_started && Incoming.Count == 0) _next = seq;
                short diff = (short)(seq - _next);
                if (diff < 0) { _dropped++; return; }                       // late or duplicate
                if (diff > JitterMax) { Incoming.Clear(); _next = seq; }    // far ahead: resync
                Incoming[seq] = pcm;
                while (Incoming.Count > JitterMax)
                {
                    // Too much queued: drop the oldest so latency stays bounded.
                    Incoming.Remove(_next); _next++; _dropped++;
                }
                if (!_started && Incoming.Count >= 3) _started = true;     // 60 ms cushion
            }
            _lastHeard = Time.realtimeSinceStartup;
        }

        private static void StopPlayback()
        {
            if (!_playing) return;
            _playing = false;
            try { _source?.Stop(); } catch { }
            lock (Gate) { Incoming.Clear(); _started = false; _lastFrame = null; }
        }

        /// <summary>
        /// Keep ~100 ms written ahead of the playhead: real frames when they are there, a fading
        /// repeat for a short loss, silence otherwise. The clip loops, so whatever is not
        /// overwritten would be heard again a second later - hence the silence.
        /// </summary>
        private static void PumpPlayback()
        {
            if (!EnsurePlayer()) return;
            if (!_playing)
            {
                try
                {
                    _outClip.SetData(new float[Rate], 0);
                    _writePos = 0;
                    _source.timeSamples = 0;
                    _source.Play();
                    _playing = true;
                }
                catch (Exception e) { Game.Log.Warn("voice playback start failed: " + e.Message); return; }
            }
            int head;
            try { head = _source.timeSamples; } catch { return; }
            if (head < 0 || head >= Rate) head = 0;
            int lead = (_writePos - head + Rate) % Rate;
            if (lead > Rate / 2) { _writePos = head; lead = 0; }   // the playhead overtook us (a hitch)
            int guard = 0;
            while (lead < Lead && guard++ < 16)
            {
                WriteClip(NextFrame());
                lead += Frame;
            }
        }

        private static float[] NextFrame()
        {
            short[] frame = null;
            float g = 1f;
            bool real = false;
            lock (Gate)
            {
                if (_started && Incoming.TryGetValue(_next, out frame))
                {
                    Incoming.Remove(_next);
                    _next++;
                    _lastFrame = frame;
                    _concealLeft = 0;
                    _concealGain = 1f;
                    real = true;
                }
                else if (_started && Incoming.Count > 0 && _lastFrame != null && _concealLeft < ConcealMax)
                {
                    // A later frame is here but this one is not: it was lost. Repeat the last
                    // one, fading, and move on.
                    frame = _lastFrame;
                    _concealLeft++;
                    _concealGain *= 0.6f;
                    g = _concealGain;
                    _next++;
                    _concealed++;
                }
                else if (_started && Incoming.Count > 0)
                {
                    // A gap longer than concealment: jump to the oldest held frame.
                    ushort oldest = _next; bool have = false;
                    foreach (var k in Incoming.Keys) if (!have || (short)(k - oldest) < 0) { oldest = k; have = true; }
                    if (have) _next = oldest;
                    frame = null;
                }
                else
                {
                    // Nothing queued: we are ahead of the stream (or it ended). Silence, and
                    // do not skip - the frame may still be on its way. After a pause the next
                    // spurt starts with a fresh cushion.
                    frame = null;
                    if (_started && Time.realtimeSinceStartup - _lastHeard > 0.3f) { _started = false; _lastFrame = null; }
                }
            }
            var data = new float[Frame];
            if (frame == null) return data;
            float gain = g * _outGain;
            for (int i = 0; i < Frame; i++)
            {
                float v = frame[i] / 32768f * gain;
                data[i] = v > 1f ? 1f : (v < -1f ? -1f : v);
            }
            _played++;
            if (real) DumpFrame(frame);
            return data;
        }

        private static void DumpFrame(short[] frame)
        {
            try
            {
                if (_dump == null)
                {
                    var flag = Path.Combine(BepInEx.Paths.BepInExRootPath, "ddmp_voice_dump.on");
                    if (!File.Exists(flag)) return;
                    _dump = new FileStream(Path.Combine(BepInEx.Paths.BepInExRootPath, "ddmp_voice_dump.raw"), FileMode.Create, FileAccess.Write);
                }
                var b = new byte[Frame * 2];
                Buffer.BlockCopy(frame, 0, b, 0, b.Length);
                _dump.Write(b, 0, b.Length);
                _dump.Flush();
            }
            catch { }
        }

        private static void WriteClip(float[] data)
        {
            try
            {
                if (_writePos + data.Length <= Rate)
                {
                    _outClip.SetData(data, _writePos);
                }
                else
                {
                    int a = Rate - _writePos;
                    var first = new float[a];
                    Array.Copy(data, 0, first, 0, a);
                    _outClip.SetData(first, _writePos);
                    var second = new float[data.Length - a];
                    Array.Copy(data, a, second, 0, second.Length);
                    _outClip.SetData(second, 0);
                }
                _writePos = (_writePos + data.Length) % Rate;
                // Clear a stretch beyond what is written: if the game stalls longer than the
                // lead, the playhead runs into silence instead of last second's audio.
                int g = (_writePos + Guard) % Rate;
                if (g + Frame <= Rate) _outClip.SetData(Silence, g);
            }
            catch (Exception e)
            {
                Game.Log.Warn($"voice playback write failed: {e.Message}");
            }
        }

        // G.711 mu-law
        private static byte MuLawEncode(short sample)
        {
            const int Bias = 0x84, Clip = 32635;
            int s = sample;
            int sign = (s >> 8) & 0x80;
            if (sign != 0) s = -s;
            if (s > Clip) s = Clip;
            s += Bias;
            int exponent = 7;
            for (int mask = 0x4000; (s & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
            int mantissa = (s >> (exponent + 3)) & 0x0F;
            return (byte)~(sign | (exponent << 4) | mantissa);
        }

        private static short MuLawDecode(byte b)
        {
            int u = ~b & 0xFF;
            int sign = u & 0x80, exponent = (u >> 4) & 0x07, mantissa = u & 0x0F;
            int s = (((mantissa << 3) + 0x84) << exponent) - 0x84;
            return (short)(sign != 0 ? -s : s);
        }

        // ---------------------------------------------------------------- text chat

        // Typing: characters come from the Input System's text event (any keyboard layout).
        // IMGUI's TextField received nothing in this game (it runs the new Input System only).
        private static bool _textHooked;
        private static int _openedFrame;
        private static bool _weLockedPlayer;

        // Typed characters are read from the game window's own WM_CHAR messages (a hook on
        // the game's thread): exact UTF-16 for any keyboard layout. The Input System's text
        // event lost everything above 0xFF through the interop bridge ("привет" -> "?@825B").
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, HookProc fn, IntPtr mod, uint threadId);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        private static HookProc _hookProc;   // kept alive: native code holds a pointer to it
        private static IntPtr _hook;

        private static void HookTextInput()
        {
            if (_textHooked) return;
            _textHooked = true;
            try
            {
                _hookProc = OnGetMessage;
                // WH_GETMESSAGE on this (the window's) thread only.
                _hook = SetWindowsHookExW(3, _hookProc, IntPtr.Zero, GetCurrentThreadId());
                if (_hook == IntPtr.Zero) Game.Log.Warn("chat: message hook failed (" + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")");
                else Game.Log.Info("chat: text input hooked");
            }
            catch (Exception e) { Game.Log.Warn("chat: text input hook failed: " + e.Message); }
        }

        private static IntPtr OnGetMessage(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // PM_REMOVE: the message is being taken off the queue (seen once).
                if (code >= 0 && wParam == (IntPtr)1 && lParam != IntPtr.Zero && _chatOpen)
                {
                    // MSG (x64): hwnd @0, message @8, wParam @16.
                    uint msg = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 8);
                    if (msg == 0x0102)   // WM_CHAR
                    {
                        long w = System.Runtime.InteropServices.Marshal.ReadInt64(lParam, 16);
                        OnTextChar((char)(w & 0xFFFF));
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        /// <summary>One typed character (from WM_CHAR).</summary>
        internal static void OnTextChar(char c)
        {
            if (!_chatOpen) return;
            if (_logChars-- > 0) Game.Log.Info($"chat: char U+{(int)c:X4}");
            // The key that opened the line arrives as text in the same or the next frame.
            if (Time.frameCount - _openedFrame <= 1 && VoiceConfig.ChatKey.Length == 1
                && char.ToUpperInvariant(c) == char.ToUpperInvariant(VoiceConfig.ChatKey[0])) return;
            if (c == (char)13 || c == (char)10) { SendChat(_chatText); return; }
            if (c == (char)27) { CloseChat(); return; }
            if (c == (char)8) { _backspaceByText = Time.frameCount; if (_chatText.Length > 0) _chatText = _chatText.Substring(0, _chatText.Length - 1); return; }
            if (char.IsControl(c)) return;
            if (_chatText.Length < 200) _chatText += c;
        }

        internal static int _backspaceByText = -10;
        internal static int _logChars = 0;

        public static void OpenChat()
        {
            if (_chatOpen) return;
            HookTextInput();
            // Only from normal play: the game moves the player only while RUNNING, so the
            // chat holds the player in PLAYER_DISABLED (the input maps were not enough: the
            // movement code reads its own copy of the controls).
            var m = Game.GameRefs.Manager;
            try { if (m == null || m.CurrentGameState != global::DelusionManager.GameState.RUNNING) return; } catch { return; }
            _chatOpen = true;
            _chatText = "";
            _openedFrame = Time.frameCount;
            _chatFocusFrames = 3;
            _chatHideAt = Time.realtimeSinceStartup + 3600f;   // history stays visible while typing
            try { m.CurrentGameState = global::DelusionManager.GameState.PLAYER_DISABLED; _weLockedPlayer = true; } catch (Exception e) { Game.Log.Warn("chat: player lock: " + e.Message); }
            BlockGameInput(true);
        }

        public static void CloseChat()
        {
            if (!_chatOpen) return;
            _chatOpen = false;
            _chatText = "";
            _chatHideAt = Time.realtimeSinceStartup + 8f;
            // Re-enable a frame later: the Enter/Esc that closed the line must not also reach
            // the game (Esc would open the pause menu).
            _unblockAt = Time.frameCount + 2;
        }

        private static void ReleasePlayer()
        {
            if (!_weLockedPlayer) return;
            _weLockedPlayer = false;
            try
            {
                var m = Game.GameRefs.Manager;
                // Something else (a cutscene, the partner's dialogue) took over meanwhile: leave it.
                if (m != null && m.CurrentGameState == global::DelusionManager.GameState.PLAYER_DISABLED)
                    m.CurrentGameState = global::DelusionManager.GameState.RUNNING;
            }
            catch { }
        }

        private static int _unblockAt = -1;

        internal static void TickChat()
        {
            if (_unblockAt > 0 && Time.frameCount >= _unblockAt) { _unblockAt = -1; if (!_chatOpen) { BlockGameInput(false); ReleasePlayer(); } }
        }

        public static void SendChat(string text)
        {
            CloseChat();
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.Length > 200) text = text.Substring(0, 200);
            AddLine("You: " + text);
            try
            {
                var s = Session.Instance;
                if (s == null || !s.Connected) { AddLine("(not connected - not sent)"); return; }
                var w = new Writer();
                w.Str(Bootstrap.PlayerName ?? "Partner").Str(text);
                s.SendReliable(PacketKind.Chat, w.ToArray());
                Game.Log.Info("chat sent: " + text);
            }
            catch (Exception e) { Game.Log.Warn($"chat send failed: {e.Message}"); }
        }

        public static void ReceiveChat(byte[] payload)
        {
            try
            {
                var r = new Reader(payload);
                string name = r.Str();
                string text = r.Remaining > 0 ? r.Str() : null;
                if (text == null) { text = name; name = "Partner"; }
                if (string.IsNullOrWhiteSpace(text)) return;
                if (string.IsNullOrWhiteSpace(name)) name = "Partner";
                AddLine(name + ": " + text);
                Game.Log.Info($"chat received: {name}: {text}");
            }
            catch { }
        }

        private static void AddLine(string line)
        {
            lock (Lines)
            {
                Lines.Add(line);
                while (Lines.Count > 8) Lines.RemoveAt(0);
            }
            if (!_chatOpen) _chatHideAt = Time.realtimeSinceStartup + 12f;
        }

        // ---------------------------------------------------------------- game input while typing

        private static readonly List<UnityEngine.InputSystem.InputActionMap> Disabled = new List<UnityEngine.InputSystem.InputActionMap>();
        private static bool _blocked;

        private static void BlockGameInput(bool on)
        {
            try
            {
                if (on)
                {
                    if (_blocked) return;
                    _blocked = true;
                    Disabled.Clear();
                    var di = global::DelusionInput.Instance;
                    if (di == null) return;
                    DisableAll(di.controls);
                    try { DisableAll(di.delusionControls?.asset); } catch { }
                    try { DisableAll(di.playerInput?.actions); } catch { }
                }
                else
                {
                    if (!_blocked) return;
                    _blocked = false;
                    foreach (var m in Disabled) { try { m?.Enable(); } catch { } }
                    Disabled.Clear();
                }
            }
            catch (Exception e) { Game.Log.Debug("chat input block: " + e.Message); }
        }

        private static void DisableAll(UnityEngine.InputSystem.InputActionAsset asset)
        {
            if (asset == null) return;
            var maps = asset.actionMaps;
            for (int i = 0; i < maps.Count; i++)
            {
                var m = maps[i];
                if (m == null || !m.enabled || Disabled.Contains(m)) continue;
                m.Disable();
                Disabled.Add(m);
            }
        }
    }

    /// <summary>On-screen chat (IMGUI) and the talking indicator.</summary>
    public sealed class VoiceGuiBehaviour : MonoBehaviour
    {
        private GUIStyle _line, _box;
        private static float _bsNext;

        private void Update()
        {
            try
            {
                VoiceSync.TickChat();
                if (!VoiceConfig.ChatEnabled) { if (VoiceSync.ChatOpen) VoiceSync.CloseChat(); return; }
                if (!VoiceSync.ChatOpen)
                {
                    if (GameRefs_InWorld() && Keys.Pressed(VoiceConfig.ChatKey)) VoiceSync.OpenChat();
                    return;
                }
                if (!GameRefs_InWorld()) { VoiceSync.CloseChat(); return; }
                if (Keys.Pressed("Escape")) { VoiceSync.CloseChat(); return; }
                if (Keys.Pressed("Enter") || Keys.Pressed("NumpadEnter")) { VoiceSync.SendChat(VoiceSync.ChatText); return; }
                // Backspace with key repeat (when the text event did not already delete).
                float now = Time.realtimeSinceStartup;
                bool fire = false;
                if (Keys.Pressed("Backspace")) { fire = true; _bsNext = now + 0.45f; }
                else if (Keys.Held("Backspace") && now >= _bsNext) { fire = true; _bsNext = now + 0.05f; }
                if (fire && Time.frameCount - VoiceSync._backspaceByText > 1 && VoiceSync.ChatText.Length > 0)
                    VoiceSync.ChatText = VoiceSync.ChatText.Substring(0, VoiceSync.ChatText.Length - 1);
            }
            catch { }
        }

        private static bool GameRefs_InWorld()
        {
            try { return Game.GameRefs.InWorld; } catch { return false; }
        }

        private void OnGUI()
        {
            try
            {
                if (_line == null)
                {
                    _line = new GUIStyle(); _line.fontSize = 16; _line.wordWrap = true;
                    _line.normal.textColor = Color.white;
                    _box = GUI.skin.box;
                }
                if (VoiceConfig.ChatEnabled)
                {
                    bool show = VoiceSync.ChatOpen || Time.realtimeSinceStartup < VoiceSync.ChatHideAt;
                    if (show)
                    {
                        string[] lines;
                        lock (VoiceSync.ChatLines)
                        {
                            lines = new string[VoiceSync.ChatLines.Count];
                            for (int i = 0; i < lines.Length; i++) lines[i] = VoiceSync.ChatLines[i];
                        }
                        if (lines.Length > 0)
                        {
                            float h = lines.Length * 22f + 10f;
                            float top = Screen.height - 140f - h;
                            GUI.Box(new Rect(10f, top, 540f, h), GUIContent.none, _box);
                            for (int i = 0; i < lines.Length; i++)
                                GUI.Label(new Rect(18f, top + 5f + i * 22f, 528f, 22f), lines[i], _line);
                        }
                    }
                    if (VoiceSync.ChatOpen)
                    {
                        float y = Screen.height - 128f;
                        GUI.Box(new Rect(10f, y - 4f, 540f, 34f), GUIContent.none, _box);
                        GUI.Label(new Rect(18f, y, 50f, 26f), "Say:", _line);
                        bool caret = ((int)(Time.realtimeSinceStartup * 2f)) % 2 == 0;
                        GUI.Label(new Rect(66f, y, 476f, 26f), (VoiceSync.ChatText ?? "") + (caret ? "_" : " "), _line);
                    }
                }
                if (VoiceConfig.VoiceChat && (VoiceSync.Sending || VoiceSync.Hearing))
                {
                    string t = VoiceSync.Sending && VoiceSync.Hearing ? "VOICE  ◄►" : VoiceSync.Sending ? "VOICE  ►" : "VOICE  ◄";
                    GUI.Box(new Rect(Screen.width - 150f, Screen.height - 46f, 138f, 30f), GUIContent.none, _box);
                    GUI.Label(new Rect(Screen.width - 140f, Screen.height - 43f, 128f, 26f), t, _line);
                }
            }
            catch { }
        }
    }
}
