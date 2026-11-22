using System;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using HarmonyLib;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// The airship as a shared vehicle.
    ///
    /// Authority: whoever pilots it owns it from AirshipController.Use() until StopUsing
    /// fully completes (docking and stabilizing included); at rest the host owns it. The
    /// authority runs the game's own flight model and streams the pose (25 Hz in flight,
    /// 2 Hz at rest, including the anchor). The other machine never runs Use/ToggleFlying/
    /// StopUsing (they would swap cameras, grab the player, disable the deck colliders and
    /// save): its ship is kinematic and follows the stream, deck colliders stay walkable.
    ///
    /// One pilot at a time: the host arbitrates the lock. A client asks first; the wheel is
    /// used locally only after the grant.
    ///
    /// Passengers: a player standing on the deck of a remotely flown ship is parented to it
    /// (the game's own moving-platform mechanism, TryReparentPlayer) so it carries them;
    /// region loading/saving follows the pilot's policy while aboard, and falls are measured
    /// from the deck. Implicit warps (teleport/death recall) are left to the authority, so
    /// one player's fast travel never yanks the ship from under the other.
    /// </summary>
    public static class AirshipSync
    {
        private const byte MsgPilotRequest = 0, MsgPilotGrant = 1, MsgPilotDeny = 2, MsgPilotEnd = 3, MsgWarpRequest = 4;
        private const byte LockNone = 0, LockHost = 1, LockClient = 2;

        private static byte _lock;              // host's view of the pilot lock
        private static bool _localPilot;
        private static bool _granted;           // client: may use the wheel once
        private static bool _bypass;
        private static global::AirshipWheel _pendingWheel;
        private static float _lastRemoteState = -10f;
        private static bool _remotePiloted;     // the peer is flying it right now
        private static float _nextSend;
        private static bool _implicitWarp;
        private static int _sent, _recv;

        // passenger
        private static bool _aboard;
        private static float _lastDeckHit;
        private static float _nextBoardCheck;
        private static bool _regionPolicyApplied;

        // follower interpolation
        private struct Sample { public Vector3 Pos; public Quaternion Rot; public Quaternion Roll; public float T; }
        private static Sample _a, _b;
        private static bool _haveSample;

        public static string Stats =>
            $"lock={_lock} localPilot={_localPilot} remotePiloted={_remotePiloted} aboard={_aboard} sent={_sent} recv={_recv} authority={IsAuthority}";

        private static global::AirshipController _ship;
        public static global::AirshipController Ship
        {
            get
            {
                if (_ship != null) return _ship;
                try
                {
                    var all = Resources.FindObjectsOfTypeAll<global::AirshipController>();
                    if (all != null) foreach (var s in all) if (s != null && s.gameObject.scene.IsValid()) { _ship = s; break; }
                }
                catch { }
                return _ship;
            }
        }

        private static bool Connected => Session.Instance != null && Session.Instance.Connected;
        private static bool IsHost => Session.Instance?.Role == SessionRole.Host;

        /// <summary>This machine simulates the ship.</summary>
        public static bool IsAuthority => !Connected || _localPilot || (IsHost && !_remotePiloted);

        public static bool RemotePiloted => _remotePiloted;

        // ------------------------------------------------------------------ pilot lock

        internal static bool OnWheelUse(global::AirshipWheel wheel)
        {
            if (!Connected || _bypass) return true;
            if (_remotePiloted) { Note("Your partner is at the helm."); return false; }
            if (IsHost)
            {
                if (_lock == LockClient) { Note("Your partner is at the helm."); return false; }
                return true;   // Use() postfix takes the lock
            }
            if (_granted) { _granted = false; return true; }
            _pendingWheel = wheel;
            var w = new Writer();
            w.U8(MsgPilotRequest);
            Session.Instance.SendReliable(PacketKind.Airship, w.ToArray());
            return false;
        }

        internal static void OnUse()
        {
            if (!Connected) return;
            _localPilot = true;
            if (IsHost) _lock = LockHost;
            Log.Info("airship: piloting");
        }

        internal static void OnStopUsing(global::AirshipController ship)
        {
            if (!Connected || !_localPilot || ship == null) return;
            // StopUsing returns early into Stabilizing and is called again later.
            if (ship.state != global::AirshipController.AirshipState.Docked) return;
            // Keep authority until the anchor has dropped (DropAnchorAfterLoad runs later),
            // so the partner receives the final anchor instead of retracting it.
            _landingDeadline = Time.realtimeSinceStartup + 6f;
            Log.Info("airship: landed");
        }

        private static float _landingDeadline = -1f;

        private static void FinishLanding(global::AirshipController ship)
        {
            _landingDeadline = -1f;
            SendState(ship, true);
            _localPilot = false;
            if (IsHost) _lock = LockNone;
            var w = new Writer();
            w.U8(MsgPilotEnd);
            Session.Instance.SendReliable(PacketKind.Airship, w.ToArray());
        }

        private static void Note(string text)
        {
            Log.Info("airship: " + text);
            try { global::DelusionNote.Instance?.StartNewNote(null, text, false); } catch { }
        }

        // ------------------------------------------------------------------ events

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            byte kind = r.U8();
            switch (kind)
            {
                case MsgPilotRequest:
                {
                    if (!IsHost) break;
                    var w = new Writer();
                    bool ok = !_localPilot && _lock != LockHost;
                    if (ok) { _lock = LockClient; _remotePiloted = true; _lastRemoteState = Time.realtimeSinceStartup; }
                    w.U8(ok ? MsgPilotGrant : MsgPilotDeny);
                    Session.Instance.SendReliable(PacketKind.Airship, w.ToArray());
                    break;
                }
                case MsgPilotGrant:
                {
                    var wheel = _pendingWheel;
                    _pendingWheel = null;
                    if (wheel == null) break;
                    _granted = true;
                    _bypass = true;
                    try { wheel.PlayerUse(); }
                    catch (Exception e) { Log.Warn("airship wheel: " + e.Message); }
                    finally { _bypass = false; _granted = false; }
                    if (!_localPilot)
                    {
                        // The game refused (region still loading, no deed): give the lock back.
                        var w = new Writer();
                        w.U8(MsgPilotEnd);
                        Session.Instance.SendReliable(PacketKind.Airship, w.ToArray());
                    }
                    break;
                }
                case MsgPilotDeny:
                    _pendingWheel = null;
                    Note("Your partner is at the helm.");
                    break;
                case MsgPilotEnd:
                    _remotePiloted = false;
                    if (IsHost && _lock == LockClient) _lock = LockNone;
                    break;
                case MsgWarpRequest:
                {
                    string dock = r.Str();
                    if (!IsHost || _remotePiloted || _localPilot) break;
                    var ship = Ship;
                    var d = global::AirshipDock.GetDock(dock);
                    if (ship != null && d != null)
                        try { ship.GoToDock(d); } catch (Exception e) { Log.Warn("airship recall: " + e.Message); }
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ state stream

        private static void SendState(global::AirshipController ship, bool force = false)
        {
            var t = ship.transform;
            var w = new Writer();
            w.Bool(_localPilot);
            w.U8((byte)(int)ship.state);
            WriteVec(w, t.position);
            WriteQuat(w, t.rotation);
            WriteQuat(w, ship.modelRoot != null ? ship.modelRoot.localRotation : Quaternion.identity);
            var anchor = ship.anchor;
            bool anchored = false;
            try { anchored = anchor != null && anchor.isAnchored; } catch { }
            w.Bool(anchored);
            if (anchored)
            {
                WriteVec(w, anchor.transform.position);
                var line = anchor.anchorLine;
                int n = line != null ? Mathf.Min(line.positionCount, 4) : 0;
                w.U8((byte)n);
                for (int i = 0; i < n; i++) WriteVec(w, line.GetPosition(i));
                var box = anchor.useTrigger;
                WriteVec(w, box != null ? box.center : Vector3.zero);
                WriteVec(w, box != null ? box.size : Vector3.zero);
            }
            Session.Instance.SendUnreliable(PacketKind.AirshipState, w.ToArray());
            _sent++;
        }

        public static void ApplyState(byte[] payload)
        {
            var ship = Ship;
            var r = new Reader(payload);
            bool piloted = r.Bool();
            r.U8();
            var pos = ReadVec(r);
            var rot = ReadQuat(r);
            var roll = ReadQuat(r);
            bool anchored = r.Bool();
            _recv++;
            if (ship == null) return;
            if (_localPilot) return;                       // we fly it; ignore stale echoes
            if (IsHost && !piloted && !_remotePiloted) return;   // host owns it at rest
            if (piloted) { _remotePiloted = true; _lastRemoteState = Time.realtimeSinceStartup; }

            _a = _haveSample ? _b : new Sample { Pos = pos, Rot = rot, Roll = roll, T = Time.realtimeSinceStartup };
            _b = new Sample { Pos = pos, Rot = rot, Roll = roll, T = Time.realtimeSinceStartup };
            _haveSample = true;
            if (!piloted)
            {
                // At rest: snap (landing, warps, late join).
                ship.transform.SetPositionAndRotation(pos, rot);
                if (ship.modelRoot != null) ship.modelRoot.localRotation = roll;
            }

            if (anchored)
            {
                var ap = ReadVec(r);
                int n = r.U8();
                var pts = new Vector3[n];
                for (int i = 0; i < n; i++) pts[i] = ReadVec(r);
                var center = ReadVec(r);
                var size = ReadVec(r);
                ApplyAnchor(ship, true, ap, pts, center, size);
            }
            else ApplyAnchor(ship, false, Vector3.zero, null, Vector3.zero, Vector3.zero);
        }

        private static void ApplyAnchor(global::AirshipController ship, bool anchored, Vector3 pos, Vector3[] pts, Vector3 center, Vector3 size)
        {
            var anchor = ship.anchor;
            if (anchor == null) return;
            bool cur = false;
            try { cur = anchor.isAnchored; } catch { }
            if (!anchored)
            {
                if (cur) LoopGuard.Apply(() => { try { anchor.RetrieveAnchor(); } catch { } });
                return;
            }
            // The receiver's terrain may not be loaded: copy the result, don't re-raycast.
            if (cur && (anchor.transform.position - pos).sqrMagnitude < 0.01f) return;
            try
            {
                anchor.gameObject.SetActive(true);
                anchor.transform.position = pos;
                var line = anchor.anchorLine;
                if (line != null && pts != null)
                {
                    line.positionCount = pts.Length;
                    for (int i = 0; i < pts.Length; i++) line.SetPosition(i, pts[i]);
                    line.enabled = true;
                }
                if (anchor.useTrigger != null) { anchor.useTrigger.center = center; anchor.useTrigger.size = size; anchor.useTrigger.enabled = true; }
                anchor.isAnchored = true;
            }
            catch (Exception e) { Log.Debug("airship anchor: " + e.Message); }
        }

        /// <summary>Follower: Update replacement - interpolate the pilot's stream.</summary>
        internal static void FollowerUpdate(global::AirshipController ship)
        {
            if (!_haveSample || ship == null) return;
            float dt = Mathf.Max(0.001f, _b.T - _a.T);
            // Render one sample interval behind the newest sample; extrapolate a little.
            float k = Mathf.Clamp((Time.realtimeSinceStartup - _b.T) / dt, 0f, 1.5f);
            var pos = Vector3.LerpUnclamped(_a.Pos, _b.Pos, k);
            var rot = Quaternion.SlerpUnclamped(_a.Rot, _b.Rot, Mathf.Min(k, 1f));
            ship.transform.SetPositionAndRotation(pos, rot);
            if (ship.modelRoot != null) ship.modelRoot.localRotation = Quaternion.Slerp(ship.modelRoot.localRotation, _b.Roll, 0.3f);
        }

        internal static void FollowerFixedUpdate(global::AirshipController ship)
        {
            try { var body = ship.body; if (body != null && !body.isKinematic) body.isKinematic = true; } catch { }
        }

        // ------------------------------------------------------------------ warps

        internal static void BeginImplicitWarp() => _implicitWarp = true;
        internal static void EndImplicitWarp() => _implicitWarp = false;

        /// <summary>WarpToDock gate. The authority warps (and the stream carries it); a
        /// client's explicit recall becomes a request to the host; implicit recalls caused by
        /// one player's teleport/death never move a ship the partner is using.</summary>
        internal static bool AllowWarp(global::AirshipController ship)
        {
            if (!Connected || LoopGuard.ApplyingRemote) return true;
            if (_localPilot) return true;
            if (_remotePiloted) return false;
            if (IsHost)
            {
                if (!_implicitWarp) return true;
                // Don't recall the ship from under a partner standing on or near it.
                if (PlayerSync.TryPeerPose(out var peer, out _) && ship != null
                    && Vector3.Distance(peer, ship.transform.position) < 40f) return false;
                return true;
            }
            if (!_implicitWarp && ship != null)
            {
                string dock = "";
                try { dock = ship.data != null ? ship.data.dockId : ""; } catch { }
                if (!string.IsNullOrEmpty(dock))
                {
                    var w = new Writer();
                    w.U8(MsgWarpRequest).Str(dock);
                    Session.Instance.SendReliable(PacketKind.Airship, w.ToArray());
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ per frame

        public static void Tick()
        {
            if (!Connected)
            {
                if (_aboard) Disembark();
                _remotePiloted = false;
                return;
            }
            var ship = Ship;
            if (ship == null) return;
            float now = Time.realtimeSinceStartup;

            // The pilot vanished (disconnect, crash): stop following.
            if (_remotePiloted && now - _lastRemoteState > 3f)
            {
                _remotePiloted = false;
                if (IsHost && _lock == LockClient) _lock = LockNone;
            }

            if (_landingDeadline > 0f)
            {
                bool anchored = false;
                try { anchored = ship.anchor != null && ship.anchor.isAnchored; } catch { }
                if (anchored || now > _landingDeadline) FinishLanding(ship);
            }

            if (_localPilot || (IsHost && !_remotePiloted))
            {
                if (now >= _nextSend)
                {
                    _nextSend = now + (_localPilot ? 0.04f : 0.5f);
                    try { if (ship.gameObject.activeInHierarchy) SendState(ship); } catch { }
                }
            }

            PassengerTick(ship, now);
        }

        private static void PassengerTick(global::AirshipController ship, float now)
        {
            var player = GameRefs.Player;
            if (player == null || _localPilot) return;
            if (now >= _nextBoardCheck)
            {
                _nextBoardCheck = now + 0.1f;
                bool onDeck = false;
                try
                {
                    var origin = player.transform.position + Vector3.up * 0.5f;
                    var hits = Physics.RaycastAll(origin, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore);
                    if (hits != null)
                        foreach (var h in hits)
                            if (h.collider != null && h.collider.transform.IsChildOf(ship.transform)
                                && !h.collider.transform.IsChildOf(player.transform)) { onDeck = true; break; }
                }
                catch { }
                if (onDeck) _lastDeckHit = now;

                if (!_aboard && onDeck && _remotePiloted) Board(ship);
                else if (_aboard && (!_remotePiloted || now - _lastDeckHit > 0.6f)) Disembark();
                else if (_aboard && onDeck)
                {
                    // A respawn next to the pilot (or any teleport) clears the player's parent:
                    // still on the deck but no longer carried, the next ship move dropped them.
                    bool carried = false;
                    try { carried = player.transform.IsChildOf(ship.transform); } catch { }
                    if (!carried)
                    {
                        bool ok = false;
                        try { ok = GameRefs.PlayerActions?.TryReparentPlayer(global::DelusionPlayerActions.ParentPriority.High, ship.gameObject) ?? false; } catch { }
                        if (ok) Log.Info("airship: re-attached to the deck (parent was cleared)");
                    }
                }
            }

            // Falls are measured in world space; on a moving deck measure from the deck.
            if (_aboard)
            {
                try
                {
                    var pm = player.GetComponent<global::PlayerMove>();
                    if (pm != null) pm.startFallPosition = player.transform.position;
                }
                catch { }
            }
        }

        private static void Board(global::AirshipController ship)
        {
            var pa = GameRefs.PlayerActions;
            if (pa == null) return;
            bool ok = false;
            try { ok = pa.TryReparentPlayer(global::DelusionPlayerActions.ParentPriority.High, ship.gameObject); } catch { }
            if (!ok) return;   // retried next check (fails while regions stream)
            _aboard = true;
            SetRegionPolicy(true);
            Log.Info("airship: aboard as passenger");
        }

        private static void Disembark()
        {
            _aboard = false;
            try
            {
                var pa = GameRefs.PlayerActions;
                var player = GameRefs.Player;
                var ship = Ship;
                if (pa != null && player != null && ship != null && player.transform.IsChildOf(ship.transform))
                    pa.NullifyPlayerParent();
            }
            catch { }
            SetRegionPolicy(false);
            Log.Info("airship: left the deck");
        }

        private static void SetRegionPolicy(bool aboard)
        {
            if (aboard == _regionPolicyApplied) return;
            _regionPolicyApplied = aboard;
            try
            {
                var rm = GameRefs.Regions;
                if (rm != null) { if (aboard) rm.AllowLoadingRegions(false, true); else rm.AllowLoadingRegions(true); }
                var dr = global::DelusionRegions.Instance;
                if (dr != null) dr.disableSaving = aboard;
            }
            catch (Exception e) { Log.Debug("airship region policy: " + e.Message); }
        }

        /// <summary>True while the local player rides the partner's ship (for ship-local pose).</summary>
        public static bool LocalAboard => _aboard || _localPilot;

        // ------------------------------------------------------------------ codec

        private static void WriteVec(Writer w, Vector3 v) { w.F32(v.x); w.F32(v.y); w.F32(v.z); }
        private static Vector3 ReadVec(Reader r) => new Vector3(r.F32(), r.F32(), r.F32());
        private static void WriteQuat(Writer w, Quaternion q) { w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); }
        private static Quaternion ReadQuat(Reader r) => new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
    }

    // ---------------------------------------------------------------------- patches

    [HarmonyPatch(typeof(global::AirshipWheel), nameof(global::AirshipWheel.PlayerUse))]
    internal static class Patch_AirshipWheel_Use
    {
        [HarmonyPrefix] static bool Prefix(global::AirshipWheel __instance) => AirshipSync.OnWheelUse(__instance);
    }

    [HarmonyPatch(typeof(global::AirshipController), nameof(global::AirshipController.Use))]
    internal static class Patch_Airship_Use
    {
        [HarmonyPrefix] static bool Prefix() => !AirshipSync.RemotePiloted;
        [HarmonyPostfix] static void Postfix() => AirshipSync.OnUse();
    }

    [HarmonyPatch(typeof(global::AirshipController), nameof(global::AirshipController.StopUsing))]
    internal static class Patch_Airship_StopUsing
    {
        [HarmonyPostfix] static void Postfix(global::AirshipController __instance) => AirshipSync.OnStopUsing(__instance);
    }

    [HarmonyPatch(typeof(global::AirshipController), "Update")]
    internal static class Patch_Airship_Update
    {
        [HarmonyPrefix]
        static bool Prefix(global::AirshipController __instance)
        {
            if (AirshipSync.IsAuthority) return true;
            AirshipSync.FollowerUpdate(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(global::AirshipController), "FixedUpdate")]
    internal static class Patch_Airship_FixedUpdate
    {
        [HarmonyPrefix]
        static bool Prefix(global::AirshipController __instance)
        {
            if (AirshipSync.IsAuthority) return true;
            AirshipSync.FollowerFixedUpdate(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(global::AirshipController), "WarpToDock")]
    internal static class Patch_Airship_WarpToDock
    {
        [HarmonyPrefix] static bool Prefix(global::AirshipController __instance) => AirshipSync.AllowWarp(__instance);
    }

    [HarmonyPatch(typeof(global::AirshipController), "OnTeleport")]
    internal static class Patch_Airship_OnTeleport
    {
        [HarmonyPrefix] static void Prefix() => AirshipSync.BeginImplicitWarp();
        [HarmonyFinalizer] static void Finalizer() => AirshipSync.EndImplicitWarp();
    }

    [HarmonyPatch(typeof(global::AirshipController), "WarpToClosestDock")]
    internal static class Patch_Airship_WarpToClosestDock
    {
        [HarmonyPrefix] static void Prefix() => AirshipSync.BeginImplicitWarp();
        [HarmonyFinalizer] static void Finalizer() => AirshipSync.EndImplicitWarp();
    }
}
