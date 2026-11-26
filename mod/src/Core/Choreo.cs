using System;
using System.Globalization;
using DelusionMP.Game;
using DelusionMP.Sync;
using UnityEngine;

namespace DelusionMP.Core
{
    /// <summary>
    /// Smooth, human-like movement, camera tracking, weapon/spell actions, and airship
    /// flight helpers driven exclusively from the rig command channel (ddmp_cmd.txt).
    /// Completely inert during normal gameplay.
    /// </summary>
    internal static class Choreo
    {
        private enum TrackMode { None, Peer, Enemy, Pos }

        private static TrackMode _trackMode = TrackMode.None;
        private static string _trackEnemyFilter;
        private static Vector3 _trackPos;
        private static float _trackRate = 130f; // max deg/sec
        private static float _curYaw;
        private static float _curPitch;
        private static bool _rotInit;

        private static bool _walking;
        private static Vector2 _walkTarget;
        private static float _walkSpeed = 4.5f;
        private static bool _walkSprint;

        private static float _strafeUntil;
        private static Vector2 _strafeLocal; // x=right, y=fwd
        private static float _strafeSpeed = 4.2f;
        private static bool _strafeSprint;

        private static float _vy;

        private static int _swingLeft;
        private static float _swingInterval = 0.75f;
        private static float _nextSwing;

        private static float _shipFlyUntil;
        private static Vector3 _shipVel;
        private static float _shipYawRate;

        // In-engine 24fps frame capture (pixel-perfect 1280x720 including HUD/IMGUI, Drive G only)
        private static bool _recording;
        private static string _recDir;
        private static int _recFrame;
        private static int _recMaxFrames;
        private static float _recInterval;
        private static float _recNextAt;

        private static float F(string s, float def) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        public static void StopAll()
        {
            _trackMode = TrackMode.None;
            _walking = false;
            _strafeUntil = 0f;
            _swingLeft = 0;
            _shipFlyUntil = 0f;
            _rotInit = false;
            try
            {
                var pa = GameRefs.PlayerActions;
                if (pa != null) pa.isSprinting = false;
            }
            catch { }
        }

        public static bool Handle(string cmd, string[] parts, Action<string> emit)
        {
            switch (cmd)
            {
                case "choreo":
                    if (parts.Length > 1 && parts[1] == "stop") { StopAll(); emit("CHOREO stopped"); }
                    else emit($"CHOREO track={_trackMode} walk={_walking} strafe={Time.realtimeSinceStartup < _strafeUntil} swings={_swingLeft}");
                    return true;

                case "track":
                {
                    if (parts.Length < 2 || parts[1] == "off")
                    {
                        _trackMode = TrackMode.None;
                        _rotInit = false;
                        emit("TRACK off");
                        return true;
                    }
                    SyncCurrentAngles();
                    var mode = parts[1].ToLowerInvariant();
                    if (mode == "peer")
                    {
                        _trackMode = TrackMode.Peer;
                        _trackRate = parts.Length > 2 ? F(parts[2], 130f) : 130f;
                        emit($"TRACK peer rate={_trackRate:F0}");
                    }
                    else if (mode == "enemy")
                    {
                        _trackMode = TrackMode.Enemy;
                        _trackEnemyFilter = parts.Length > 2 && parts[2] != "-" ? parts[2] : null;
                        _trackRate = parts.Length > 3 ? F(parts[3], 145f) : 145f;
                        emit($"TRACK enemy filter={_trackEnemyFilter ?? "*"} rate={_trackRate:F0}");
                    }
                    else if (mode == "pos" && parts.Length >= 5)
                    {
                        _trackMode = TrackMode.Pos;
                        _trackPos = new Vector3(F(parts[2], 0), F(parts[3], 0), F(parts[4], 0));
                        _trackRate = parts.Length > 5 ? F(parts[5], 110f) : 110f;
                        emit($"TRACK pos={_trackPos} rate={_trackRate:F0}");
                    }
                    else emit("ERR track peer|enemy|pos|off");
                    return true;
                }

                case "walk":
                {
                    if (parts.Length < 3) { emit("ERR walk <x> <z> [speed] [sprint]"); return true; }
                    _walkTarget = new Vector2(F(parts[1], 0), F(parts[2], 0));
                    _walkSpeed = parts.Length > 3 ? F(parts[3], 4.5f) : 4.5f;
                    _walkSprint = parts.Length > 4 && parts[4] == "sprint";
                    _walking = true;
                    _strafeUntil = 0f;
                    SyncCurrentAngles();
                    emit($"WALK target=({_walkTarget.x:F1},{_walkTarget.y:F1}) speed={_walkSpeed:F1} sprint={_walkSprint}");
                    return true;
                }

                case "strafe":
                {
                    if (parts.Length < 3) { emit("ERR strafe <left|right|fwd|back|fl|fr|bl|br> <dur> [speed] [sprint]"); return true; }
                    var d = parts[1].ToLowerInvariant();
                    Vector2 v = d switch
                    {
                        "left" => new Vector2(-1, 0),
                        "right" => new Vector2(1, 0),
                        "fwd" => new Vector2(0, 1),
                        "back" => new Vector2(0, -1),
                        "fl" => new Vector2(-0.7f, 0.7f),
                        "fr" => new Vector2(0.7f, 0.7f),
                        "bl" => new Vector2(-0.7f, -0.7f),
                        "br" => new Vector2(0.7f, -0.7f),
                        _ => Vector2.zero
                    };
                    float dur = F(parts[2], 1.5f);
                    _strafeLocal = v;
                    _strafeSpeed = parts.Length > 3 ? F(parts[3], 4.2f) : 4.2f;
                    _strafeSprint = parts.Length > 4 && parts[4] == "sprint";
                    _strafeUntil = Time.realtimeSinceStartup + dur;
                    _walking = false;
                    emit($"STRAFE {d} dur={dur:F1} speed={_strafeSpeed:F1}");
                    return true;
                }

                case "jump":
                {
                    _vy = parts.Length > 1 ? F(parts[1], 7.2f) : 7.2f;
                    emit($"JUMP vy={_vy:F1}");
                    return true;
                }

                case "draw":
                {
                    try
                    {
                        var pw = global::DelusionPlayerWeapons.Instance;
                        pw?.UnholsterWeapon();
                        emit($"DRAW wpn={(pw?.currentWeapon != null ? pw.currentWeaponType.ToString() : "none")}");
                    }
                    catch (Exception e) { emit("ERR draw: " + e.Message); }
                    return true;
                }

                case "equip":
                {
                    if (parts.Length < 2) { emit("ERR equip <itemId> [rarity0..6]"); return true; }
                    try
                    {
                        var inv = GameRefs.Inventory;
                        var pw = global::DelusionPlayerWeapons.Instance;
                        if (inv == null || pw == null) { emit("ERR no inv/wpn"); return true; }
                        var rar = parts.Length > 2 && int.TryParse(parts[2], out var r) ? (global::ItemRarity)r : global::ItemRarity.Common;
                        var it = inv.CreateItem(parts[1], rar, 1);
                        if (it != null)
                        {
                            inv.StoreItem(it, (global::ItemPopupMode)0, 1, false);
                            pw.UpdateCurrentWeapon(it);
                            pw.UnholsterWeapon();
                        }
                        emit($"EQUIP {parts[1]} rar={rar} wpn={(pw.currentWeapon != null ? pw.currentWeaponType.ToString() : "none")}");
                    }
                    catch (Exception e) { emit("ERR equip: " + e.Message); }
                    return true;
                }

                case "swing":
                {
                    _swingLeft = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
                    _swingInterval = parts.Length > 2 ? F(parts[2], 0.75f) : 0.75f;
                    _nextSwing = Time.realtimeSinceStartup;
                    emit($"SWING count={_swingLeft} interval={_swingInterval:F2}");
                    return true;
                }

                case "power":
                {
                    try
                    {
                        var pw = global::DelusionPlayerWeapons.Instance;
                        pw?.UnholsterWeapon();
                        pw?.PowerAttackRoot();
                        emit("POWER");
                    }
                    catch (Exception e) { emit("ERR power: " + e.Message); }
                    return true;
                }

                case "cast":
                {
                    string spName = parts.Length > 1 ? parts[1].ToLowerInvariant() : "missile";
                    try
                    {
                        var sp = global::DelusionPlayerSpell.Instance;
                        if (sp == null) { emit("ERR no DelusionPlayerSpell"); return true; }
                        switch (spName)
                        {
                            case "missile": sp.SpellMissile(); break;
                            case "burn": sp.SpellBurn(); break;
                            case "flechette": sp.SpellFlechette(); break;
                            case "poison": sp.SpellPoisonFlechette(); break;
                            case "speed": sp.SpellSpeed(); break;
                            case "regen": sp.SpellRegen(); break;
                            case "ultima": sp.SpellUltima(); break;
                            default: emit("ERR unknown spell: " + spName); return true;
                        }
                        emit("CAST " + spName);
                    }
                    catch (Exception e) { emit("ERR cast: " + e.Message); }
                    return true;
                }

                case "shipfly":
                {
                    if (parts.Length < 6) { emit("ERR shipfly <vx> <vy> <vz> <yawRate> <dur>"); return true; }
                    _shipVel = new Vector3(F(parts[1], 0), F(parts[2], 0), F(parts[3], 0));
                    _shipYawRate = F(parts[4], 0);
                    float dur = F(parts[5], 3f);
                    _shipFlyUntil = Time.realtimeSinceStartup + dur;
                    emit($"SHIPFLY vel={_shipVel} yawRate={_shipYawRate:F1} dur={dur:F1}");
                    return true;
                }

                case "rec":
                {
                    if (parts.Length < 4) { emit("ERR rec <dir> <fps> <dur>"); return true; }
                    _recDir = parts[1];
                    float fps = Mathf.Clamp(F(parts[2], 24f), 5f, 30f);
                    float dur = Mathf.Clamp(F(parts[3], 5f), 0.5f, 30f);
                    try { System.IO.Directory.CreateDirectory(_recDir); } catch { }
                    _recFrame = 0;
                    _recMaxFrames = Mathf.CeilToInt(fps * dur);
                    _recInterval = 1f / fps;
                    _recNextAt = Time.realtimeSinceStartup;
                    _recording = true;
                    emit($"REC start dir={_recDir} fps={fps:F0} frames={_recMaxFrames}");
                    return true;
                }

                case "recstop":
                    _recording = false;
                    emit($"REC stop frames={_recFrame}");
                    return true;

                default:
                    return false;
            }
        }

        private static void SyncCurrentAngles()
        {
            var p = GameRefs.Player;
            if (p == null) return;
            _curYaw = p.transform.eulerAngles.y;
            var cam = GameRefs.PlayerCamera;
            if (cam != null)
            {
                float x = cam.transform.eulerAngles.x;
                _curPitch = x > 180f ? x - 360f : x;
            }
            else _curPitch = 0f;
            _rotInit = true;
        }

        public static void Tick()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (dt <= 0f) return;
            float now = Time.realtimeSinceStartup;

            TickShipFly(now, dt);
            TickMovement(now, dt);
            TickSwings(now);
            TickRec(now);
        }

        /// <summary>Runs in LateUpdate after PlayerLook.Update so smooth camera slew is never overwritten.</summary>
        public static void LateTick()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (dt <= 0f) return;
            TickTracking(dt);
        }

        private static void TickRec(float now)
        {
            if (!_recording) return;
            if (now < _recNextAt) return;
            _recNextAt += _recInterval;
            if (_recNextAt < now) _recNextAt = now + _recInterval;
            _recFrame++;
            string path = System.IO.Path.Combine(_recDir, $"f_{_recFrame:D4}.png");
            try { ScreenCapture.CaptureScreenshot(path); } catch { }
            if (_recFrame >= _recMaxFrames) _recording = false;
        }

        private static void TickShipFly(float now, float dt)
        {
            if (now >= _shipFlyUntil) return;
            var ship = AirshipSync.Ship;
            if (ship == null) { _shipFlyUntil = 0f; return; }
            var delta = _shipVel * dt;
            var rot = Quaternion.Euler(0f, _shipYawRate * dt, 0f);
            try
            {
                var body = ship.body;
                if (body != null)
                {
                    body.position = body.position + delta;
                    body.rotation = body.rotation * rot;
                }
                ship.transform.position += delta;
                ship.transform.rotation = ship.transform.rotation * rot;
            }
            catch { }
        }

        private static void TickMovement(float now, float dt)
        {
            var p = GameRefs.Player;
            if (p == null) return;
            var cc = p.GetComponent<CharacterController>();
            if (cc == null || !cc.enabled) return;

            Vector3 horiz = Vector3.zero;
            bool sprint = false;
            bool moving = false;

            if (_walking)
            {
                var pos = p.transform.position;
                Vector2 to = new Vector2(_walkTarget.x - pos.x, _walkTarget.y - pos.z);
                float dist = to.magnitude;
                if (dist < 0.45f)
                {
                    _walking = false;
                }
                else
                {
                    Vector2 dir = to / dist;
                    float spd = dist < 1.2f ? _walkSpeed * (dist / 1.2f) : _walkSpeed;
                    horiz = new Vector3(dir.x, 0f, dir.y) * spd;
                    sprint = _walkSprint;
                    moving = true;

                    // If not tracking a separate target, gently turn toward walk direction
                    if (_trackMode == TrackMode.None)
                    {
                        if (!_rotInit) SyncCurrentAngles();
                        float wantYaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
                        SlewTo(wantYaw, 0f, 140f, dt);
                    }
                }
            }
            else if (now < _strafeUntil)
            {
                float yawRad = p.transform.eulerAngles.y * Mathf.Deg2Rad;
                Vector3 fwd = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
                Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
                horiz = (right * _strafeLocal.x + fwd * _strafeLocal.y) * _strafeSpeed;
                sprint = _strafeSprint;
                moving = true;
            }

            if (moving || Math.Abs(_vy) > 0.05f)
            {
                if (cc.isGrounded && _vy <= 0f) _vy = -2f;
                else _vy = Mathf.Max(_vy - 20f * dt, -22f);

                cc.Move((horiz + new Vector3(0f, _vy, 0f)) * dt);
                try
                {
                    var pa = GameRefs.PlayerActions;
                    if (pa != null) pa.isSprinting = sprint;
                }
                catch { }
            }
        }

        private static void TickTracking(float dt)
        {
            if (_trackMode == TrackMode.None) return;
            var p = GameRefs.Player;
            if (p == null) return;
            if (!_rotInit) SyncCurrentAngles();

            Vector3? target = null;
            if (_trackMode == TrackMode.Peer)
            {
                if (PlayerSync.TryPuppetHead(out var h)) target = h - Vector3.up * 0.15f;
            }
            else if (_trackMode == TrackMode.Pos)
            {
                target = _trackPos;
            }
            else if (_trackMode == TrackMode.Enemy)
            {
                foreach (var (key, c, dist) in EnemySync.Nearest(25))
                {
                    if (c == null || EnemySync.IsDead(c) || SummonSync.IsSummon(c)) continue;
                    if (dist > 30f) continue;
                    if (_trackEnemyFilter != null && key.IndexOf(_trackEnemyFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    target = c.transform.position + Vector3.up * 1.15f;
                    break;
                }
                // Fallback to any live enemy within 20m if filtered enemy died
                if (target == null && _trackEnemyFilter != null)
                {
                    foreach (var (_, c, dist) in EnemySync.Nearest(10))
                    {
                        if (c == null || EnemySync.IsDead(c) || SummonSync.IsSummon(c) || dist > 20f) continue;
                        target = c.transform.position + Vector3.up * 1.15f;
                        break;
                    }
                }
            }

            if (target == null) return;
            var cam = GameRefs.PlayerCamera;
            var eye = cam != null ? cam.transform.position : p.transform.position + Vector3.up * 1.6f;
            var d = target.Value - eye;
            if (d.sqrMagnitude < 0.04f) return;

            float wantYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float wantPitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            wantPitch = Mathf.Clamp(wantPitch, -45f, 45f);
            SlewTo(wantYaw, wantPitch, _trackRate, dt);
        }

        private static void SlewTo(float wantYaw, float wantPitch, float maxDegPerSec, float dt)
        {
            float alpha = 1f - Mathf.Exp(-7.5f * dt);
            float dy = Mathf.DeltaAngle(_curYaw, wantYaw);
            float dp = wantPitch - _curPitch;
            float maxStep = maxDegPerSec * dt;
            _curYaw = Mathf.Repeat(_curYaw + Mathf.Clamp(dy * alpha, -maxStep, maxStep), 360f);
            _curPitch = Mathf.Clamp(_curPitch + Mathf.Clamp(dp * alpha, -maxStep, maxStep), -50f, 50f);

            var p = GameRefs.Player;
            if (p == null) return;
            p.transform.rotation = Quaternion.Euler(0f, _curYaw, 0f);
            try
            {
                var look = GameRefs.Manager?.playerLook;
                if (look != null) look.xRotation = _curPitch;
                var cam = GameRefs.PlayerCamera;
                if (cam != null) cam.transform.localRotation = Quaternion.Euler(_curPitch, 0f, 0f);
            }
            catch { }
        }

        private static void TickSwings(float now)
        {
            if (_swingLeft <= 0 || now < _nextSwing) return;
            _swingLeft--;
            _nextSwing = now + _swingInterval;
            try
            {
                var pw = global::DelusionPlayerWeapons.Instance;
                if (pw != null)
                {
                    pw.UnholsterWeapon();
                    pw.AttackRoot();
                }
            }
            catch { }
        }
    }
}
