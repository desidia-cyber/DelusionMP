using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// A key (F8 by default, [Player] TeleportToPartnerKey) puts the local player next to the
    /// partner: just behind them, on the ground they stand on. Far jumps hold the player still
    /// for a moment so the destination region can stream in (no falling through an empty world),
    /// and the fall tracker is reset so the drop is not fall damage.
    /// </summary>
    public static class PartnerTeleport
    {
        public static string Key = "F8";
        private const float Cooldown = 5f, FarDistance = 150f, HoldSeconds = 2f;

        private static float _readyAt, _holdUntil;
        private static Vector3 _holdPos;
        private static bool _holding;

        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (_holding) { Hold(now); return; }

            var s = Session.Instance;
            if (s == null || !s.Connected || !GameRefs.InWorld) return;
            if (VoiceSync.ChatOpen || !Keys.Pressed(Key)) return;
            Request(now);
        }

        /// <summary>Rig: what the key does.</summary>
        public static void DebugRequest() => Request(Time.realtimeSinceStartup);

        private static void Request(float now)
        {
            var player = GameRefs.Player;
            if (player == null) return;
            if (now < _readyAt) { Note($"Teleport ready in {Mathf.CeilToInt(_readyAt - now)} s"); return; }
            if (AwayState.LocalAway) return;                 // menus, dialogue: the key belongs to them
            if (CoopDeath.LocalDown) { Note("You can't teleport while down"); return; }
            if (!PlayerSync.TryPeerVoicePose(out var peer, out bool fresh) || !fresh)
            {
                Note("Partner position unknown - is your partner in the world?");
                return;
            }
            PlayerSync.TryPeerPose(out _, out float yaw);

            // Behind the partner, on the ground they stand on; their own spot if that misses.
            var back = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
            var dest = peer + Vector3.up * 0.3f;
            try
            {
                var probe = peer + back * 1.3f + Vector3.up * 1.5f;
                if (Physics.Raycast(probe, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)
                    && !hit.collider.transform.IsChildOf(player.transform))
                    dest = hit.point + Vector3.up * 0.15f;
            }
            catch { }

            float dist = Vector3.Distance(player.transform.position, dest);
            Place(player, dest);
            _readyAt = now + Cooldown;
            if (dist > FarDistance)
            {
                // Let the region stream in before gravity applies.
                _holding = true;
                _holdPos = dest;
                _holdUntil = now + HoldSeconds;
                SetControl(player, false);
            }
            Log.Info($"teleported to the partner ({dist:F0} m)");
        }

        private static void Hold(float now)
        {
            var player = GameRefs.Player;
            if (player == null) { _holding = false; return; }
            if (now < _holdUntil)
            {
                player.transform.position = _holdPos;
                return;
            }
            _holding = false;
            SetControl(player, true);
            ResetFall(player);
        }

        private static void Place(GameObject player, Vector3 pos)
        {
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;   // a live controller snaps the transform back
            player.transform.position = pos;
            if (cc != null) cc.enabled = true;
            ResetFall(player);
        }

        private static void SetControl(GameObject player, bool on)
        {
            try
            {
                var pm = player.GetComponent<global::PlayerMove>();
                var cc = player.GetComponent<CharacterController>();
                if (pm != null) pm.enabled = on;
                if (cc != null) cc.enabled = on;
            }
            catch { }
        }

        private static void ResetFall(GameObject player)
        {
            try
            {
                var pm = player.GetComponent<global::PlayerMove>();
                if (pm != null) pm.startFallPosition = player.transform.position;
            }
            catch { }
        }

        private static void Note(string text)
        {
            try { global::DelusionNote.Instance?.StartNewNote(null, text, false); } catch { }
        }
    }
}
