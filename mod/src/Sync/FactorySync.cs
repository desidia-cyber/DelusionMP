using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// The moving factory (and any MovingFactory) as a host-driven transform stream.
    ///
    /// Replaying its discrete steps (MoveStart/RotateStart) breaks whenever the two machines
    /// load the region at different moments: a late copy resets itself to point A and then
    /// waits for the next step, up to 40 s. Instead the host streams the moving part's pose
    /// (5 Hz) and the client's copy follows it with its own simulation paused. When the host
    /// does not have the region loaded (no stream for 3 s) the client runs the factory itself.
    /// </summary>
    public static class FactorySync
    {
        private const float SendInterval = 0.2f;
        private const float StaleAfter = 3f;
        private static float _nextSend;

        private sealed class Follow
        {
            public Vector3 Pos; public Quaternion Rot; public float At;
        }

        private static readonly Dictionary<IntPtr, Follow> _follow = new Dictionary<IntPtr, Follow>();
        private static int _sent, _recv;
        public static string Stats => $"sent={_sent} recv={_recv} following={_follow.Count}";

        private static bool IsHost => Session.Instance?.Role == SessionRole.Host;

        public static string DebugFor(global::MovingFactory f)
        {
            if (f == null) return "";
            if (!_follow.TryGetValue(f.Pointer, out var fl)) return $" following=False ptr={f.Pointer} keys={_follow.Count}";
            return $" following={Following(f)} age={Time.realtimeSinceStartup - fl.At:F1} target={fl.Pos.x:F1},{fl.Pos.y:F1},{fl.Pos.z:F1}";
        }

        internal static bool Following(global::MovingFactory f)
        {
            if (f == null || IsHost) return false;
            return _follow.TryGetValue(f.Pointer, out var fl) && Time.realtimeSinceStartup - fl.At < StaleAfter;
        }

        public static void Tick()
        {
            var s = Session.Instance;
            if (s == null || !s.Connected) return;
            float now = Time.realtimeSinceStartup;

            if (IsHost)
            {
                if (now < _nextSend) return;
                _nextSend = now + SendInterval;
                foreach (var f in Resources.FindObjectsOfTypeAll<global::MovingFactory>())
                {
                    if (f == null || !f.gameObject.activeInHierarchy) continue;
                    var t = f.moveObjectTransform != null ? f.moveObjectTransform : f.transform;
                    var w = new Writer();
                    w.Str(EnemySync.KeyFor(ObjectRegistry.Instance, f));
                    w.F32(t.position.x).F32(t.position.y).F32(t.position.z);
                    var q = t.rotation;
                    w.F32(q.x).F32(q.y).F32(q.z).F32(q.w);
                    var bytes = w.ToArray();
                    if (_sent == 0) Log.Info($"factory stream: sending {bytes.Length} B for {EnemySync.KeyFor(ObjectRegistry.Instance, f)}");
                    s.SendUnreliable(PacketKind.FactoryState, bytes);
                    _sent++;
                }
                return;
            }

            // Client: move each followed factory toward the host's pose every frame.
            foreach (var f in Resources.FindObjectsOfTypeAll<global::MovingFactory>())
            {
                if (f == null || !f.gameObject.activeInHierarchy) continue;
                if (!_follow.TryGetValue(f.Pointer, out var fl) || now - fl.At > StaleAfter) continue;
                var t = f.moveObjectTransform != null ? f.moveObjectTransform : f.transform;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, fl.Pos, 0.25f), Quaternion.Slerp(t.rotation, fl.Rot, 0.25f));
            }
        }

        private static readonly Dictionary<string, global::MovingFactory> _byKey = new Dictionary<string, global::MovingFactory>(StringComparer.Ordinal);
        private static float _indexAt = -10f;
        private static int _missLogged;

        public static void Apply(byte[] payload)
        {
            if (_recv == 0) Log.Info($"factory stream: first packet {payload.Length} B");
            var r = new Reader(payload);
            string key = r.Str();
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            _recv++;
            if (!_byKey.TryGetValue(key, out var f) || f == null)
            {
                if (Time.realtimeSinceStartup - _indexAt < 1f) return;
                _indexAt = Time.realtimeSinceStartup;
                _byKey.Clear();
                foreach (var x in Resources.FindObjectsOfTypeAll<global::MovingFactory>())
                    if (x != null && x.gameObject.scene.IsValid()) _byKey[EnemySync.KeyFor(ObjectRegistry.Instance, x)] = x;
                if (!_byKey.TryGetValue(key, out f) || f == null)
                {
                    if (_missLogged++ < 3) Log.Info($"factory stream: no local match for {key}; local keys: {string.Join(" | ", _byKey.Keys)}");
                    return;
                }
            }
            if (!_follow.TryGetValue(f.Pointer, out var fl)) _follow[f.Pointer] = fl = new Follow();
            fl.Pos = pos; fl.Rot = rot; fl.At = Time.realtimeSinceStartup;
        }
    }

    [HarmonyPatch(typeof(global::MovingFactory), "FixedUpdate")]
    internal static class Patch_MovingFactory_FixedUpdate
    {
        // The client's own simulation pauses while it follows the host's stream.
        [HarmonyPrefix] static bool Prefix(global::MovingFactory __instance) => !FactorySync.Following(__instance);
    }
}
