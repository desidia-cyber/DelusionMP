using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Remote player puppet.
    ///
    /// The local player is authoritative over its own movement; the peer renders an
    /// interpolated copy. The puppet is the host's player hierarchy with every component
    /// that would simulate, damage or control it disabled - only the visual rig and its
    /// Animator stay alive.
    /// </summary>
    public static class PlayerSync
    {
        private const float SendHz = 20f;
        private const float SendInterval = 1f / SendHz;
        private static float _nextSend;

        private static GameObject _puppet;
        private static Animator _puppetAnim;

        private struct Snapshot
        {
            public Vector3 Pos;
            public float Yaw;
            public float Pitch;
            public byte MoveState;
            public bool Grounded;
            public bool Crouched;
            public bool Sprinting;
            public bool Dodging;
            public float ReceivedAt;
        }

        private static readonly Queue<Snapshot> _buffer = new Queue<Snapshot>(32);
        private static Snapshot _latest;
        private static bool _hasLatest;

        public static void Init()
        {
            _buffer.Clear();
            _hasLatest = false;
            DestroyPuppet();
        }

        // ---------------- send ----------------

        public static void TickSend()
        {
            if (Session.Instance == null) return;
            if (Time.realtimeSinceStartup < _nextSend) return;
            _nextSend = Time.realtimeSinceStartup + SendInterval;

            var player = GameRefs.Player;
            if (player == null) return;

            var actions = GameRefs.PlayerActions;
            var t = player.transform;

            var w = new Writer();
            w.Position(t.position);
            w.Yaw(t.eulerAngles.y);
            w.Yaw(CameraPitch());
            w.U8(actions != null ? (byte)(int)actions.moveState : (byte)0);
            w.Bool(actions != null && actions.isGrounded);
            w.Bool(actions != null && actions.isCrouched);
            w.Bool(actions != null && actions.isSprinting);
            w.Bool(actions != null && actions.isDodging);

            var stats = SafeStats();
            w.I32((int)(stats?.currentHealth ?? 0f));
            w.I32((int)(stats?.currentMana ?? 0f));
            w.I32((int)(stats?.currentStamina ?? 0f));

            Session.Instance.SendUnreliable(PacketKind.PlayerState, w.ToArray());
        }

        private static float CameraPitch()
        {
            try
            {
                var cam = GameRefs.PlayerCamera;
                if (cam != null) return cam.transform.eulerAngles.x;
            }
            catch { }
            return 0f;
        }
        private static global::CharacterStats SafeStats()
        {
            return GameRefs.Stats;
        }
        // ---------------- receive ----------------

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            var s = new Snapshot
            {
                Pos = r.Position(),
                Yaw = r.Yaw(),
                Pitch = r.Yaw(),
                MoveState = r.U8(),
                Grounded = r.Bool(),
                Crouched = r.Bool(),
                Sprinting = r.Bool(),
                Dodging = r.Bool(),
                ReceivedAt = Time.realtimeSinceStartup,
            };
            // vitals follow but the puppet does not need them yet
            _buffer.Enqueue(s);
            while (_buffer.Count > 24) _buffer.Dequeue();
            _latest = s;
            _hasLatest = true;

            if (_puppet == null) CreatePuppet();
        }

        public static void TickRender()
        {
            if (!_hasLatest || _puppet == null) return;

            // Hide (rather than extrapolate) if the peer went quiet for a while - loading,
            // quitting. Short gaps just hold the last pose. Extrapolating here is what makes puppets slide into walls.
            if (Time.realtimeSinceStartup - _latest.ReceivedAt > 3f)
            {
                _puppet.SetActive(false);
                return;
            }
            if (!_puppet.activeSelf) _puppet.SetActive(true);

            var t = _puppet.transform;
            t.position = Vector3.Lerp(t.position, _latest.Pos, 0.4f);
            var e = t.eulerAngles;
            t.rotation = Quaternion.Euler(e.x, Mathf.LerpAngle(e.y, _latest.Yaw, 0.4f), e.z);

            if (_tag != null)
            {
                try
                {
                    var cam = GameRefs.PlayerCamera ?? Camera.main;
                    _tag.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);   // slow spin
                }
                catch { }
            }

            if (_puppetAnim != null)
            {
                try
                {
                    _puppetAnim.SetFloat("Speed", _latest.Sprinting ? 1f : (_latest.MoveState > 0 ? 0.5f : 0f));
                    _puppetAnim.SetBool("Grounded", _latest.Grounded);
                    _puppetAnim.SetBool("Crouch", _latest.Crouched);
                }
                catch { /* parameter names differ by avatar; harmless */ }
            }
        }

        // ---------------- puppet construction ----------------

        /// <summary>
        /// Builds a stand-alone visual avatar for the remote player.
        ///
        /// Dread Delusion is first-person: the local player object has no body mesh (only a
        /// camera rig and hands), and its components are MonoSingletons whose Awake would
        /// re-point DelusionPlayerActions.Instance &amp; co. at a clone. So the puppet is not
        /// a copy of the player - it is a small mannequin (body, head, visor showing facing,
        /// floating name tag) with no colliders and no game scripts.
        /// </summary>
        private static void CreatePuppet()
        {
            try
            {
                float height = 1.8f, centerY = 0f;
                try
                {
                    var cc = GameRefs.Player?.GetComponent<UnityEngine.CharacterController>();
                    if (cc != null) { height = Mathf.Clamp(cc.height, 1.2f, 2.6f); centerY = cc.center.y; }
                }
                catch { }
                float feet = centerY - height * 0.5f;

                _puppet = new GameObject("DelusionMP_RemotePuppet");
                UnityEngine.Object.DontDestroyOnLoad(_puppet);
                var color = new Color(0.95f, 0.55f, 0.1f);   // ember orange - reads in fog and dark
                var mat = MakeMaterial(color);
                var dark = MakeMaterial(new Color(0.08f, 0.08f, 0.1f));

                float bodyH = height * 0.72f;
                Part(PrimitiveType.Capsule, "Body", new Vector3(0, feet + bodyH * 0.5f, 0),
                     new Vector3(0.6f, bodyH * 0.5f, 0.45f), mat);
                float headY = feet + bodyH + 0.22f;
                Part(PrimitiveType.Sphere, "Head", new Vector3(0, headY, 0), Vector3.one * 0.4f, mat);
                Part(PrimitiveType.Cube, "Visor", new Vector3(0, headY + 0.03f, 0.17f),
                     new Vector3(0.3f, 0.09f, 0.1f), dark);
                Part(PrimitiveType.Cube, "Pack", new Vector3(0, feet + bodyH * 0.65f, -0.25f),
                     new Vector3(0.4f, 0.45f, 0.15f), dark);

                try
                {
                    // Floating marker so the partner is findable at a distance. (TextMesh is
                    // stripped from this build and adding TextMeshPro at runtime faults
                    // natively, so no text label.)
                    var tag = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    tag.name = "Marker";
                    var col = tag.GetComponent<Collider>();
                    if (col != null) UnityEngine.Object.Destroy(col);
                    tag.transform.SetParent(_puppet.transform, false);
                    tag.transform.localPosition = new Vector3(0, headY + 0.55f, 0);
                    tag.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
                    tag.transform.localScale = Vector3.one * 0.18f;
                    var mk = MakeMaterial(new Color(0.3f, 0.95f, 1f));
                    if (mk != null) tag.GetComponent<Renderer>().sharedMaterial = mk;
                    _tag = tag.transform;
                }
                catch (Exception e) { Log.Warn("name tag: " + e); }

                Log.Info($"remote puppet created (h={height:F2} feet={feet:F2})");
            }
            catch (Exception e)
            {
                Log.Error($"puppet creation failed: {e}");
                _puppet = null;
            }
        }

        public static Transform PuppetTransform => _puppet != null ? _puppet.transform : null;

        private static Transform _tag;
        private static string _peerName = "";
        public static void SetPeerName(string n) { _peerName = n ?? ""; }

        private static void Part(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);   // purely visual, never blocks
            go.transform.SetParent(_puppet.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
        }

        /// <summary>The game is URP; the built-in default material renders magenta there.</summary>
        private static Material MakeMaterial(Color c)
        {
            Shader sh = null;
            foreach (var n in new[] { "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Lit",
                                      "Unlit/Color", "Sprites/Default" })
            {
                try { sh = Shader.Find(n); } catch { }
                if (sh != null) break;
            }
            if (sh == null) return null;
            var m = new Material(sh);
            try { m.SetColor("_BaseColor", c); } catch { }
            try { m.SetColor("_Color", c); } catch { }
            return m;
        }

        /// <summary>Last received peer pose (world position, body yaw).</summary>
        public static bool TryPeerPose(out Vector3 pos, out float yaw)
        {
            pos = _latest.Pos; yaw = _latest.Yaw;
            return _hasLatest;
        }

        /// <summary>Puppet head position, for aiming a camera at the partner.</summary>
        public static bool TryPuppetHead(out Vector3 head)
        {
            head = default;
            if (_puppet == null) return false;
            var h = _puppet.transform.Find("Head");
            head = h != null ? h.position : _puppet.transform.position;
            return true;
        }

        public static string PuppetInfo
        {
            get
            {
                if (_puppet == null) return "none";
                try
                {
                    var p = _puppet.transform.position;
                    return $"{(int)p.x},{(int)p.y},{(int)p.z} active={_puppet.activeSelf}";
                }
                catch { return "invalid"; }
            }
        }

        public static void DestroyPuppet()
        {
            if (_puppet != null) UnityEngine.Object.Destroy(_puppet);
            _puppet = null;
            _puppetAnim = null;
            _tag = null;
        }
    }

    /// <summary>
    /// Day/night clock and rest. World state, host-owned.
    ///
    /// This is not cosmetic: EnemySpawner.PlayerRest/DayPassed and ItemPickup.PlayerRest
    /// respawn world content on day changes, so a diverged clock silently desyncs spawners.
    /// </summary>
    public static class ClockSync
    {
        private static int _lastDay = -1;
        private static int _lastTypeOfDay = -1;

        public static void TickHost()
        {
            if (Session.Instance?.Role != SessionRole.Host) return;
            var dn = GameRefs.DayNight;
            if (dn == null) return;

            int day = (int)(GameRefs.Stats?.daysPlayed ?? 0);
            int tod = (int)dn.typeOfDay;

            if (day == _lastDay && tod == _lastTypeOfDay) return;
            _lastDay = day;
            _lastTypeOfDay = tod;

            var w = new Writer();
            w.I32(day).U8((byte)tod).F32(Priv.GetField<float>(dn, "_starTimeSeconds"));
            Session.Instance.SendReliable(PacketKind.ClockState, w.ToArray());
        }

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            int day = r.I32();
            var tod = (global::TypeOfDay)r.U8();
            float star = r.F32();

            LoopGuard.Apply(() =>
            {
                try
                {
                    var dn = GameRefs.DayNight;
                    if (dn != null)
                    {
                        Priv.SetField(dn, "_starTimeSeconds", star);
                        dn.typeOfDay = tod;
                    }
                    var stats = GameRefs.Stats;
                    if (stats != null) stats.daysPlayed = day;
                }
                catch (Exception e) { Log.Debug($"clock apply failed: {e.Message}"); }
            });
        }
    }
}
