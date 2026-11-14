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

            // Freeze rather than extrapolate if the peer went quiet (region change,
            // death, loading). Extrapolating here is what makes puppets slide into walls.
            if (Time.realtimeSinceStartup - _latest.ReceivedAt > 0.5f)
            {
                _puppet.SetActive(false);
                return;
            }
            if (!_puppet.activeSelf) _puppet.SetActive(true);

            var t = _puppet.transform;
            t.position = Vector3.Lerp(t.position, _latest.Pos, 0.4f);
            var e = t.eulerAngles;
            t.rotation = Quaternion.Euler(e.x, Mathf.LerpAngle(e.y, _latest.Yaw, 0.4f), e.z);

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

        private static void CreatePuppet()
        {
            var player = GameRefs.Player;
            if (player == null) return;
            try
            {
                _puppet = UnityEngine.Object.Instantiate(player);
                _puppet.name = "DelusionMP_RemotePuppet";

                // Remove everything that simulates, controls or damages.
                Strip<global::PlayerMove>(_puppet);
                Strip<global::PlayerLook>(_puppet);
                Strip<global::DelusionPlayerActions>(_puppet);
                Strip<UnityEngine.CharacterController>(_puppet);
                Strip<global::PlayerColliderTrigger>(_puppet);
                Strip<AudioListener>(_puppet);

                // The local player carries a first-person camera rig; the puppet must not.
                foreach (var cam in _puppet.GetComponentsInChildren<Camera>(true))
                    UnityEngine.Object.Destroy(cam.gameObject);

                _puppetAnim = _puppet.GetComponentInChildren<Animator>(true);
                Log.Info("remote puppet created");
            }
            catch (Exception e)
            {
                Log.Error($"puppet creation failed: {e}");
                _puppet = null;
            }
        }

        private static void Strip<T>(GameObject root) where T : Component
        {
            foreach (var c in root.GetComponentsInChildren<T>(true))
                UnityEngine.Object.Destroy(c);
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
