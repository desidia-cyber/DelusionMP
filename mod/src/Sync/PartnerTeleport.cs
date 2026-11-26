using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// A key (F5 by default, [Player] TeleportToPartnerKey) puts the local player next to the
    /// partner: just behind them, on the ground they stand on. Far jumps hold the player still
    /// for a moment so the destination region can stream in (no falling through an empty world),
    /// and the fall tracker is reset so the drop is not fall damage.
    /// </summary>
    public static class PartnerTeleport
    {
        public static string Key = "F5";
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

            var dest = FindSpot(player, peer, yaw);
            float dist = Vector3.Distance(player.transform.position, dest);
            Place(player, dest);
            _readyAt = now + Cooldown;
            // Always hold briefly so the controller settles onto the floor; far jumps hold
            // until the region has streamed in and then look for the floor again.
            _holding = true;
            _far = dist > FarDistance;
            _holdPos = dest;
            _holdUntil = now + (_far ? HoldSeconds : SettleSeconds);
            SetControl(player, false);
            Log.Info($"teleported to the partner ({dist:F0} m)");
        }

        private const float SettleSeconds = 0.25f, MaxFloorStep = 1.5f;
        private static bool _far;

        /// <summary>
        /// A spot on solid ground next to the partner: behind, beside, then on the partner's own
        /// spot. Each is probed straight down from above; only floor within 1.5 m of the
        /// partner's feet counts (not a roof above them, not a level below). The player is put
        /// 0.5 m above the floor, never inside it.
        /// </summary>
        private static Vector3 FindSpot(GameObject player, Vector3 peer, float yaw)
        {
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var offsets = new[] { rot * Vector3.back * 1.3f, rot * Vector3.left * 1.3f, rot * Vector3.right * 1.3f, Vector3.zero };
            foreach (var off in offsets)
            {
                try
                {
                    var from = peer + off + Vector3.up * 2.5f;
                    var hits = Physics.RaycastAll(from, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore);
                    if (hits == null) continue;
                    float best = float.MaxValue; Vector3 bestPoint = default;
                    foreach (var h in hits)
                    {
                        if (h.collider == null || h.collider.transform.IsChildOf(player.transform)) continue;
                        if (Mathf.Abs(h.point.y - peer.y) > MaxFloorStep) continue;
                        float d = Mathf.Abs(h.point.y - peer.y);
                        if (d < best) { best = d; bestPoint = h.point; }
                    }
                    if (best < float.MaxValue) return bestPoint + Vector3.up * 0.5f;
                }
                catch { }
            }
            return peer + Vector3.up * 0.6f;   // the partner stands there
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
            if (_far && PlayerSync.TryPeerVoicePose(out var peer, out bool fresh) && fresh)
            {
                // The region is in now: find the floor again (and follow a partner who moved).
                PlayerSync.TryPeerPose(out _, out float yaw);
                player.transform.position = FindSpot(player, peer, yaw);
            }
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
