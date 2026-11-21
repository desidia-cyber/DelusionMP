using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>Local voice/text-chat settings. Never sent on the wire.</summary>
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
    /// Proximity voice chat, ported from DollsMP's dmp_talk / sidecar voice design to Unity.
    ///
    /// The game owns the microphone and the speaker (Unity AudioModule); the mod's own UDP
    /// session carries 20 ms frames of 16 kHz mono PCM as unreliable Voice packets with a
    /// sequence number, a jitter buffer and packet-loss concealment. Audio work never touches
    /// the save state and is isolated by Perf.Run like every other subsystem.
    /// </summary>
    public static class VoiceSync
    {
        private const int Rate = 16000;
        private const int Frame = 320;              // 20 ms at 16 kHz
        private const int FrameBytes = Frame * 2;   // 16-bit mono
        private const int PacketBytes = 4 + FrameBytes;
        private const int JitterMax = 12;           // 240 ms
        private const int ConcealMax = 6;           // 120 ms

        private static readonly object Gate = new object();
        private static readonly Dictionary<ushort, byte[]> Incoming = new Dictionary<ushort, byte[]>();
        private static readonly Queue<float> Capture = new Queue<float>();

        private static ushort _seq;
        private static ushort _next;
        private static bool _started;
        private static float _firstAt;
        private static byte[] _lastFrame;
        private static int _concealLeft;
        private static float _concealGain = 1f;
        private static float _lastHeard;

        private static string _device;
        private static AudioClip _mic;
        private static float[] _micBuf;
        private static int _micPos;
        private static bool _micWarned;

        private static AudioClip _outClip;
        private static AudioSource _source;
        private static int _writePos;

        private static float _nextFrameAt;
        private static float _gateUntil;
        private static float _pttTail;
        private static bool _wasSending;

        private static readonly List<string> Lines = new List<string>();
        private static bool _chatOpen;
        private static string _chatText = "";
        private static float _chatHideAt;

        public static bool ChatOpen => _chatOpen;
        public static string ChatText { get => _chatText; set => _chatText = value; }
        public static IReadOnlyList<string> ChatLines => Lines;
        public static float ChatHideAt => _chatHideAt;
        public static bool Sending => _wasSending;
        public static bool Hearing => Time.realtimeSinceStartup - _lastHeard < 0.6f;

        // ---------------------------------------------------------------- lifecycle

        public static void Init()
        {
            try
            {
                var holder = new GameObject("DelusionMP_Voice");
                UnityEngine.Object.DontDestroyOnLoad(holder);
                _source = holder.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.loop = true;
                _source.spatialBlend = 0f;
                _source.dopplerLevel = 0f;
                _source.bypassEffects = true;
                _source.priority = 0;
                // A streaming clip needs a native PCMReaderCallback delegate, which IL2CPP interop
                // cannot bridge for float[]. Use a looping one-second clip and SetData instead.
                _outClip = AudioClip.Create("DelusionMP_Voice_In", Rate, 1, Rate, false);
                _writePos = 0;
                _source.clip = _outClip;
                _source.Play();
                Game.Log.Info($"voice ready (16 kHz mono, {Frame} samples/frame, mic: {Devices()})");
            }
            catch (Exception e)
            {
                Game.Log.Warn($"voice playback could not start: {e.Message}");
            }
        }

        public static void Shutdown()
        {
            try { if (_device != null && Microphone.IsRecording(_device)) Microphone.End(_device); } catch { }
            try { if (_source != null) _source.Stop(); } catch { }
            try { if (_outClip != null) UnityEngine.Object.Destroy(_outClip); } catch { }
            lock (Gate) { Incoming.Clear(); Capture.Clear(); _lastFrame = null; _started = false; }
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

        // ---------------------------------------------------------------- per-frame

        public static void Tick()
        {
            var session = Session.Instance;
            if (session == null || !session.Connected || !VoiceConfig.VoiceChat)
            {
                StopMic();
                _wasSending = false;
                return;
            }

            UpdateSpatial();
            PumpPlayback();
            float now = Time.realtimeSinceStartup;
            if (now < _nextFrameAt) return;
            _nextFrameAt = now + 0.02f;

            if (!CaptureMic(now)) return;
            while (Capture.Count >= Frame)
            {
                var pcm = new short[Frame];
                float sum = 0f;
                float gain = VoiceConfig.VoiceMicPct / 100f;
                for (int i = 0; i < Frame; i++)
                {
                    float v = Capture.Dequeue() * gain;
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    pcm[i] = (short)(v * 32767f);
                    sum += v * v;
                }
                float rms = (float)Math.Sqrt(sum / Frame);
                // Always-on mode uses a noise gate with a 400 ms hangover, like the sidecar.
                if (VoiceConfig.VoiceMode == 1 && rms > 0.008f) _gateUntil = now + 0.4f;

                bool send = WantsToSend(now);
                if (send) SendFrame(pcm);
                _wasSending = send;
            }
        }

        private static bool WantsToSend(float now)
        {
            if (!VoiceConfig.VoiceChat) return false;
            if (KeyHeld(VoiceConfig.VoiceKey)) _pttTail = now + 0.2f;
            if (VoiceConfig.VoiceMode == 1) return now < _gateUntil;
            return now < _pttTail;
        }

        private static bool KeyHeld(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                KeyCode k;
                if (!Enum.TryParse(name.Trim(), true, out k)) return false;
                return Input.GetKey(k);
            }
            catch { return false; }
        }

        private static void UpdateSpatial()
        {
            if (_source == null) return;
            try
            {
                float vol = VoiceConfig.VoiceVolumePct / 100f;
                _source.spatialBlend = 0f;
                _source.volume = vol;
                if (VoiceConfig.VoiceRangeM > 0)
                {
                    Vector3 pos;
                    bool fresh;
                    if (PlayerSync.TryPeerVoicePose(out pos, out fresh) && fresh)
                    {
                        _source.transform.position = pos;
                        _source.spatialBlend = 1f;
                        _source.minDistance = 8f;
                        _source.maxDistance = VoiceConfig.VoiceRangeM;
                    }
                    else
                    {
                        _source.volume = vol * (VoiceConfig.VoiceAwayPct / 100f);
                    }
                }
            }
            catch { }
        }

        private static void SendFrame(short[] pcm)
        {
            var packet = new byte[PacketBytes];
            byte[] seq = BitConverter.GetBytes(_seq++);
            packet[0] = seq[0]; packet[1] = seq[1];
            packet[2] = 1; packet[3] = 0;
            Buffer.BlockCopy(pcm, 0, packet, 4, FrameBytes);
            Session.Instance.SendUnreliable(PacketKind.Voice, packet);
        }

        // ---------------------------------------------------------------- microphone

        private static bool CaptureMic(float now)
        {
            try
            {
                if (VoiceConfig.VoiceMode == 1)
                {
                    // gate state is computed from the captured frame below
                }
                if (_mic == null)
                {
                    var devices = Microphone.devices;
                    if (devices == null || devices.Length == 0)
                    {
                        if (!_micWarned) { Game.Log.Warn("no microphone found: voice will be receive-only"); _micWarned = true; }
                        return false;
                    }
                    _device = devices[0];
                    _mic = Microphone.Start(_device, true, 1, Rate);
                    _micBuf = new float[Rate];
                    _micPos = Microphone.GetPosition(_device);
                    Game.Log.Info($"microphone open: {_device}");
                }
                int pos = Microphone.GetPosition(_device);
                int delta = pos - _micPos;
                if (delta < 0) delta += Rate;
                if (delta <= 0 || delta > Rate) return false;
                _mic.GetData(_micBuf, 0);
                for (int i = 0; i < delta; i++)
                {
                    int idx = _micPos + i;
                    if (idx >= Rate) idx -= Rate;
                    Capture.Enqueue(_micBuf[idx]);
                }
                _micPos = pos;
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
        }

        // ---------------------------------------------------------------- receive / jitter

        public static void Receive(byte[] packet)
        {
            if (packet == null || packet.Length < PacketBytes || !VoiceConfig.VoiceChat) return;
            ushort seq = BitConverter.ToUInt16(packet, 0);
            var pcm = new byte[FrameBytes];
            Buffer.BlockCopy(packet, 4, pcm, 0, FrameBytes);
            lock (Gate)
            {
                short diff = (short)(seq - _next);
                if (diff < 0) return; // late duplicate
                if (diff > JitterMax) { Incoming.Clear(); _next = seq; }
                if (!Incoming.ContainsKey(seq) && Incoming.Count >= JitterMax)
                {
                    // drop the oldest held frame
                    ushort oldest = seq;
                    bool have = false;
                    foreach (var k in Incoming.Keys) { if (!have || IsOlder(k, oldest)) { oldest = k; have = true; } }
                    if (have) Incoming.Remove(oldest);
                }
                Incoming[seq] = pcm;
                if (!_started && (Incoming.Count >= 3 || Time.realtimeSinceStartup - _firstAt > 0.08f))
                    _started = true;
                if (!_started) _firstAt = Time.realtimeSinceStartup;
            }
            _lastHeard = Time.realtimeSinceStartup;
        }

        private static bool IsOlder(ushort a, ushort b) => (short)(a - b) < 0;

        /// <summary>Drain the jitter buffer into the looping clip, ahead of the playhead.</summary>
        private static void PumpPlayback()
        {
            if (_outClip == null || !_started) return;
            for (int budget = 0; budget < 3; budget++)
            {
                byte[] frame = null;
                lock (Gate)
                {
                    if (Incoming.TryGetValue(_next, out frame))
                    {
                        Incoming.Remove(_next);
                        _next++;
                        _lastFrame = frame;
                        _concealLeft = 0;
                        _concealGain = 1f;
                    }
                    else if (_lastFrame != null && _concealLeft < ConcealMax)
                    {
                        frame = _lastFrame;
                        _concealLeft++;
                        _concealGain *= 0.65f;
                    }
                    else
                    {
                        if (Incoming.Count == 0 && Time.realtimeSinceStartup - _lastHeard > 2f) _started = false;
                        return;
                    }
                }
                var data = new float[Frame];
                for (int i = 0; i < Frame; i++)
                    data[i] = (BitConverter.ToInt16(frame, i * 2) / 32768f) * _concealGain;
                WriteClip(data);
            }
        }

        private static void WriteClip(float[] data)
        {
            if (_outClip == null) return;
            try
            {
                int head;
                try { head = _source != null ? _source.timeSamples : 0; } catch { head = 0; }
                if (head < 0 || head >= Rate) head = 0;
                int behind = (_writePos - head + Rate) % Rate;
                if (behind > Rate / 2) _writePos = (head + Rate / 3) % Rate;

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
            }
            catch (Exception e)
            {
                Game.Log.Warn($"voice playback write failed: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- text chat

        public static void ToggleChat()
        {
            _chatOpen = !_chatOpen;
            if (_chatOpen) _chatText = "";
        }

        public static void CancelChat() { _chatOpen = false; _chatText = ""; }

        public static void SendChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { _chatOpen = false; return; }
            text = text.Trim();
            if (text.Length > 200) text = text.Substring(0, 200);
            try
            {
                var w = new Writer();
                w.Str(text);
                Session.Instance.SendUnreliable(PacketKind.Chat, w.ToArray());
                AddLine("You: " + text);
            }
            catch (Exception e) { Game.Log.Warn($"chat send failed: {e.Message}"); }
            _chatText = "";
            _chatOpen = false;
        }

        public static void ReceiveChat(byte[] payload)
        {
            try
            {
                var r = new Reader(payload);
                var text = r.Str();
                if (!string.IsNullOrWhiteSpace(text)) AddLine(text);
            }
            catch { }
        }

        private static void AddLine(string line)
        {
            lock (Lines)
            {
                Lines.Add(line);
                while (Lines.Count > 6) Lines.RemoveAt(0);
            }
            _chatHideAt = Time.realtimeSinceStartup + 12f;
        }
    }

    /// <summary>Minimal on-screen chat line / voice indicator (IMGUI).</summary>
    public sealed class VoiceGuiBehaviour : MonoBehaviour
    {
        private bool _lastChatKey;

        private void Update()
        {
            try
            {
                if (VoiceConfig.ChatEnabled)
                {
                    bool down = KeyDown(VoiceConfig.ChatKey, ref _lastChatKey);
                    if (down)
                    {
                        if (VoiceSync.ChatOpen) VoiceSync.CancelChat();
                        else VoiceSync.ToggleChat();
                    }
                    if (VoiceSync.ChatOpen && Input.GetKeyDown(KeyCode.Escape)) VoiceSync.CancelChat();
                    if (VoiceSync.ChatOpen && Input.GetKeyDown(KeyCode.Return)) VoiceSync.SendChat(VoiceSync.ChatText);
                }
            }
            catch { }
        }

        private static bool KeyDown(string name, ref bool previous)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            KeyCode k;
            if (!Enum.TryParse(name.Trim(), true, out k)) return false;
            bool now = Input.GetKey(k);
            bool hit = now && !previous;
            previous = now;
            return hit;
        }

        private void OnGUI()
        {
            if (VoiceConfig.ChatEnabled)
            {
                float y = 12f;
                if (Time.realtimeSinceStartup < VoiceSync.ChatHideAt)
                {
                    string[] lines;
                    lock (VoiceSync.ChatLines)
                    {
                        lines = new string[VoiceSync.ChatLines.Count];
                        for (int i = 0; i < lines.Length; i++) lines[i] = VoiceSync.ChatLines[i];
                    }
                    foreach (var line in lines)
                    {
                        GUI.Label(new Rect(14f, y, 520f, 26f), line);
                        y += 22f;
                    }
                }
                if (VoiceSync.ChatOpen)
                {
                    GUI.Label(new Rect(14f, Screen.height - 96f, 120f, 24f), "Say:");
                    VoiceSync.ChatText = GUI.TextField(new Rect(80f, Screen.height - 96f, 420f, 26f), VoiceSync.ChatText ?? "");
                }
            }
            if (VoiceConfig.VoiceChat && VoiceSync.Sending)
                GUI.Label(new Rect(Screen.width - 130f, Screen.height - 42f, 120f, 26f), "VOICE");
        }
    }
}
