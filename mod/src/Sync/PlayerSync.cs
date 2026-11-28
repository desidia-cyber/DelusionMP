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
        private static Vector3 _renderLocal;
        private static global::PlayerAnimations _pan;
        private static readonly Dictionary<int, string> _moverKeys = new Dictionary<int, string>();
        private static Transform _groundMover;

        /// <summary>The moving object the player stands on (lift carriage / moving factory),
        /// found by a short ray down; null on static ground.</summary>
        public static string UnderfootInfo()
        {
            var p = GameRefs.Player;
            if (p == null) return "no player";
            var sb = new System.Text.StringBuilder();
            var hits = Physics.RaycastAll(p.transform.position + Vector3.up * 0.5f, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore);
            if (hits != null)
                foreach (var h in hits)
                {
                    if (h.collider == null) continue;
                    var lift = h.collider.GetComponentInParent<global::DelusionLift>();
                    sb.Append($" [{ObjectRegistry.HierarchyPath(h.collider.transform)} lift={(lift != null ? lift.name : "-")} carriage={(lift != null && lift.liftCarriage != null ? lift.liftCarriage.name : "-")} self={h.collider.transform.IsChildOf(p.transform)}]");
                }
            var m = MoverUnderfoot(p.transform);
            return $"parent={(p.transform.parent != null ? p.transform.parent.name : "-")} mover={(m != null ? m.name : "-")} hits:{sb}";
        }

        internal static Transform MoverUnderfoot(Transform player)
        {
            try
            {
                var hits = Physics.RaycastAll(player.position + Vector3.up * 0.5f, Vector3.down, 2.5f, ~0, QueryTriggerInteraction.Ignore);
                if (hits == null) return null;
                foreach (var h in hits)
                {
                    var c = h.collider;
                    if (c == null || c.transform.IsChildOf(player)) continue;
                    var lift = c.GetComponentInParent<global::DelusionLift>();
                    if (lift != null)
                    {
                        // The carriage, or whichever moving piece of the lift we stand on.
                        if (lift.liftCarriage != null && c.transform.IsChildOf(lift.liftCarriage.transform)) return lift.liftCarriage.transform;
                        if (lift.liftCarriage != null) return lift.liftCarriage.transform;
                        return c.transform;
                    }
                    var fac = c.GetComponentInParent<global::MovingFactory>();
                    if (fac != null) return fac.moveObjectTransform != null ? fac.moveObjectTransform : fac.transform;
                    var skiff = c.GetComponentInParent<global::SkiffManager>();
                    if (skiff != null) return skiff.parentObject != null ? skiff.parentObject.transform : skiff.transform;
                }
            }
            catch { }
            return null;
        }
        private static float _nextLook;
        public static string PartnerName { get; private set; } = "Partner";

        public static void ApplyLook(byte[] payload)
        {
            var r = new Reader(payload);
            string model = r.Str(), outfit = r.Str();
            if (r.Remaining > 0)
            {
                string pname = r.Str();
                if (!string.IsNullOrWhiteSpace(pname)) PartnerName = pname;
            }
            if (RemoteAvatar.ApplyPartnerLook(model, outfit) && _puppet != null) DestroyPuppet();
        }
        private static string _frameKey;

        // ---------------- moving-parent resolution ----------------

        private static readonly Dictionary<string, Transform> _keyCache = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _keyMiss = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>Finds a transform from a registry key: a saveable id, or
        /// "scene|#path/root:i/child:j/..." walked from the loaded scenes' roots.</summary>
        internal static Transform ResolveKey(string key)
        {
            if (_keyCache.TryGetValue(key, out var cached) && cached != null) return cached;
            if (_keyMiss.TryGetValue(key, out var missAt) && Time.realtimeSinceStartup - missAt < 2f) return null;
            Transform found = null;
            try
            {
                if (ObjectRegistry.Instance.TryResolve(key, out var go) && go != null) found = go.transform;
                else
                {
                    int at = key.IndexOf("|#path/", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        var segs = key.Substring(at + 7).Split('/');
                        for (int si = 0; si < UnityEngine.SceneManagement.SceneManager.sceneCount && found == null; si++)
                        {
                            var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(si);
                            if (!sc.isLoaded) continue;
                            foreach (var root in sc.GetRootGameObjects())
                            {
                                var cur = WalkFrom(root.transform, segs);
                                if (cur != null) { found = cur; break; }
                            }
                        }
                    }
                }
            }
            catch { }
            _keyCache[key] = found;
            if (found == null) _keyMiss[key] = Time.realtimeSinceStartup; else _keyMiss.Remove(key);
            return found;
        }

        private static Transform WalkFrom(Transform root, string[] segs)
        {
            if (!SegMatches(root, segs[0], true)) return null;
            var cur = root;
            for (int i = 1; i < segs.Length; i++)
            {
                int c = segs[i].LastIndexOf(':');
                if (c < 0 || !int.TryParse(segs[i].Substring(c + 1), out int idx)) return null;
                if (idx >= cur.childCount) return null;
                var ch = cur.GetChild(idx);
                if (ch.name != segs[i].Substring(0, c)) return null;
                cur = ch;
            }
            return cur;
        }

        private static bool SegMatches(Transform t, string seg, bool root)
        {
            int c = seg.LastIndexOf(':');
            if (c < 0) return false;
            if (t.name != seg.Substring(0, c)) return false;
            return int.TryParse(seg.Substring(c + 1), out int idx) && t.GetSiblingIndex() == idx;
        }
        private static Vector3 _lastRenderPos;
        private static float _renderSpeed;
        private static float _nextBodyRetry;
        private static bool _wasShipLocal;
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
            public bool ShipLocal;      // pose is relative to the airship
            public string ParentKey;    // or relative to another moving parent (lift carriage, platform)
            public Vector3 LocalPos;
            public float LocalYaw;
            public AvatarState Av;
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

            // On the airship (pilot or passenger) the pose is sent in ship space: both
            // machines move the ship slightly differently in time, and a world pose would
            // trail a fast ship by metres.
            var ship = AirshipSync.Ship;
            if (ship != null && t.IsChildOf(ship.transform))
            {
                var st = ship.transform;
                var lp = st.InverseTransformPoint(t.position);
                w.U8(1).F32(lp.x).F32(lp.y).F32(lp.z).F32(Mathf.DeltaAngle(st.eulerAngles.y, t.eulerAngles.y));
            }
            else if ((_groundMover = MoverUnderfoot(t)) != null || t.parent != null)
            {
                // Riding something that moves (lift carriage, moving factory, or anything the
                // game parents the player to): send the pose in that object's space so the
                // partner's body stays glued to their own copy of it, whatever its timing.
                // Prefer the moving object itself (it exists on both machines); the game's
                // runtime helper "PARENT_PLAYER" does not exist on the partner's side.
                var pt = _groundMover != null ? _groundMover : t.parent;
                while (pt != null && pt.name == "PARENT_PLAYER") pt = pt.parent;
                if (pt == null) pt = t.parent;
                string key = null;
                int pid = pt.GetInstanceID();
                if (!_moverKeys.TryGetValue(pid, out key))
                {
                    try { key = EnemySync.KeyFor(ObjectRegistry.Instance, pt); } catch { }
                    if (_moverKeys.Count > 256) _moverKeys.Clear();
                    _moverKeys[pid] = key;
                }
                if (!string.IsNullOrEmpty(key) && key.Length < 400)
                {
                    var lp = pt.InverseTransformPoint(t.position);
                    w.U8(2).Str(key).F32(lp.x).F32(lp.y).F32(lp.z).F32(Mathf.DeltaAngle(pt.eulerAngles.y, t.eulerAngles.y));
                }
                else w.U8(0);
            }
            else w.U8(0);

            // What the body is doing: equipped weapon, swing, spell.
            byte wt = 255, rar = 0, ws = 0, atk = 0, ss = 0, spt = 0;
            try
            {
                var pw = global::DelusionPlayerWeapons.Instance;
                if (pw != null)
                {
                    if (pw.currentWeapon != null) { wt = (byte)(int)pw.currentWeaponType; rar = (byte)(int)pw.currentWeaponRarity; }
                    ws = (byte)(int)pw._currentWeaponState;
                    atk = (byte)(int)pw.currentAttack;
                }
            }
            catch { }
            try { var sp = global::DelusionPlayerSpell.Instance; if (sp != null) ss = (byte)(int)sp._currentSpellState; } catch { }
            try
            {
                if (_pan == null) _pan = player.GetComponentInChildren<global::PlayerAnimations>(true);
                if (_pan != null) spt = (byte)(int)_pan.activeSpellType;
            }
            catch { _pan = null; }
            w.U8(wt).U8(rar).U8(ws).U8(atk).U8(ss).U8(spt);
            w.U8(actions != null ? (byte)(int)actions.playerState : (byte)0);

            Session.Instance.SendUnreliable(PacketKind.PlayerState, w.ToArray());

            // Our chosen look + display name, every few seconds (tiny; survives a lost packet and reconnects).
            if (Time.realtimeSinceStartup >= _nextLook)
            {
                _nextLook = Time.realtimeSinceStartup + 3f;
                var lw = new Writer();
                lw.Str(RemoteAvatar.LocalModel).Str(RemoteAvatar.LocalOutfit).Str(Bootstrap.PlayerName ?? "Partner");
                Session.Instance.SendUnreliable(PacketKind.PlayerLoadout, lw.ToArray());
            }
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
            int hp = r.I32(); r.I32(); r.I32();
            byte frame = r.Remaining > 0 ? r.U8() : (byte)0;
            if (frame == 1)
            {
                s.ShipLocal = true;
                s.LocalPos = new Vector3(r.F32(), r.F32(), r.F32());
                s.LocalYaw = r.F32();
            }
            else if (frame == 2)
            {
                s.ParentKey = r.Str();
                s.LocalPos = new Vector3(r.F32(), r.F32(), r.F32());
                s.LocalYaw = r.F32();
            }
            s.Av.WeaponType = 255;
            if (r.Remaining >= 6)
            {
                s.Av.WeaponType = r.U8(); s.Av.Rarity = r.U8(); s.Av.WeaponState = r.U8();
                s.Av.Attack = r.U8(); s.Av.SpellState = r.U8(); s.Av.SpellType = r.U8();
            }
            if (r.Remaining >= 1) s.Av.PlayerState = r.U8();
            s.Av.Grounded = s.Grounded; s.Av.Sprinting = s.Sprinting; s.Av.Crouched = s.Crouched;
            s.Av.Dodging = s.Dodging; s.Av.Health = hp;
            _buffer.Enqueue(s);
            while (_buffer.Count > 24) _buffer.Dequeue();
            _latest = s;
            _hasLatest = true;

            if (_puppet == null) CreatePuppet();
        }

        public static void TickRender()
        {
            if (!_hasLatest || _puppet == null) return;

            // The body model lives in the world scene; a puppet made at the menu fell back
            // to the mannequin. Rebuild with the real body once the world is there.
            if ((!RemoteAvatar.Ready || RemoteAvatar.IsStandIn) && GameRefs.InWorld && Time.realtimeSinceStartup >= _nextBodyRetry)
            {
                _nextBodyRetry = Time.realtimeSinceStartup + (RemoteAvatar.Ready ? 30f : 10f);
                if (RemoteAvatar.Ready && !RemoteAvatar.ChosenModelAvailable()) goto bodyDone;
                DestroyPuppet();
                CreatePuppet();
                if (_puppet == null) return;
            }
            bodyDone:

            // Hide (rather than extrapolate) if the peer went quiet for a while - loading,
            // quitting. Short gaps just hold the last pose. Extrapolating here is what makes puppets slide into walls.
            if (Time.realtimeSinceStartup - _latest.ReceivedAt > 3f)
            {
                _puppet.SetActive(false);
                return;
            }
            if (!_puppet.activeSelf) _puppet.SetActive(true);

            var t = _puppet.transform;
            Transform frameT = null;
            if (_latest.ShipLocal) { var sh = AirshipSync.Ship; if (sh != null) frameT = sh.transform; }
            else if (_latest.ParentKey != null) frameT = ResolveKey(_latest.ParentKey);
            if (frameT != null && _frameKey != (_latest.ShipLocal ? "ship" : _latest.ParentKey)) _wasShipLocal = false;
            _frameKey = frameT != null ? (_latest.ShipLocal ? "ship" : _latest.ParentKey) : null;
            if (frameT != null)
            {
                // Smooth in the moving object's space, then place with the local copy of it.
                var st = frameT;
                if (!_wasShipLocal) _renderLocal = _latest.LocalPos;
                _renderLocal = Vector3.Lerp(_renderLocal, _latest.LocalPos, 0.4f);
                t.position = st.TransformPoint(_renderLocal);
                var e0 = t.eulerAngles;
                t.rotation = Quaternion.Euler(e0.x, Mathf.LerpAngle(e0.y, st.eulerAngles.y + _latest.LocalYaw, 0.5f), e0.z);
                _wasShipLocal = true;
            }
            else
            {
                _wasShipLocal = false;
                t.position = Vector3.Lerp(t.position, _latest.Pos, 0.4f);
                var e = t.eulerAngles;
                t.rotation = Quaternion.Euler(e.x, Mathf.LerpAngle(e.y, _latest.Yaw, 0.4f), e.z);
            }

            if (RemoteAvatar.Ready)
            {
                float dt = Mathf.Max(Time.deltaTime, 1e-3f);
                var flat = t.position - _lastRenderPos; flat.y = 0f;
                _renderSpeed = Mathf.Lerp(_renderSpeed, flat.magnitude / dt, 0.2f);
                _lastRenderPos = t.position;
                RemoteAvatar.Animate(_latest.Av, _renderSpeed, dt);
            }

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
                float bodyH = height * 0.72f;
                float headY = feet + bodyH + 0.22f;
                bool body = RemoteAvatar.Build(_puppet.transform, feet);
                if (!body)
                {
                var color = new Color(0.95f, 0.55f, 0.1f);   // ember orange - reads in fog and dark
                var mat = MakeMaterial(color);
                var dark = MakeMaterial(new Color(0.08f, 0.08f, 0.1f));

                Part(PrimitiveType.Capsule, "Body", new Vector3(0, feet + bodyH * 0.5f, 0),
                     new Vector3(0.6f, bodyH * 0.5f, 0.45f), mat);
                Part(PrimitiveType.Sphere, "Head", new Vector3(0, headY, 0), Vector3.one * 0.4f, mat);
                Part(PrimitiveType.Cube, "Visor", new Vector3(0, headY + 0.03f, 0.17f),
                     new Vector3(0.3f, 0.09f, 0.1f), dark);
                Part(PrimitiveType.Cube, "Pack", new Vector3(0, feet + bodyH * 0.65f, -0.25f),
                     new Vector3(0.4f, 0.45f, 0.15f), dark);
                }

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
                    tag.transform.localPosition = new Vector3(0, headY + (body ? 1.15f : 0.55f), 0);
                    tag.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
                    tag.transform.localScale = Vector3.one * (body ? 0.09f : 0.18f);
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

        private static bool _puppetVisible = true;

        /// <summary>Hides the partner's avatar while our camera sits inside it.</summary>
        public static void SetPuppetVisible(bool visible)
        {
            _puppetVisible = visible;
            if (_puppet == null) return;
            var rs = _puppet.GetComponentsInChildren<Renderer>(true);
            if (rs != null) foreach (var r in rs) if (r != null) r.enabled = visible;
        }

        public static bool PuppetVisible => _puppetVisible;

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

        /// <summary>Peer position for spatial voice, with a freshness flag (partner may be away).</summary>
        public static bool TryPeerVoicePose(out Vector3 pos, out bool fresh)
        {
            pos = default; fresh = false;
            if (!_hasLatest) return false;
            var t = PuppetTransform;
            pos = t != null ? t.position : _latest.Pos;
            fresh = Time.realtimeSinceStartup - _latest.ReceivedAt < 2f;
            return true;
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
                    string fk = _latest.ShipLocal ? "ship" : (_latest.ParentKey ?? "-");
                    var ft = _latest.ParentKey != null ? ResolveKey(_latest.ParentKey) : null;
                    bool res = ft != null;
                    if (ft != null) { var rl = ft.InverseTransformPoint(p); fk += $" rel=({rl.x:F2},{rl.y:F2},{rl.z:F2}) frameY={ft.position.y:F1}"; }
                    return $"{(int)p.x},{(int)p.y},{(int)p.z} active={_puppet.activeSelf} frame={fk} resolved={res}";
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
            RemoteAvatar.Destroyed();
        }

        // ---------------- reliable one-shot actions ----------------

        private static int _actionsSent, _actionsApplied;
        public static string ActionStats => $"sent={_actionsSent} applied={_actionsApplied}";

        internal static void SendPlayerAction(byte kind, byte arg = 0)
        {
            var s = Session.Instance;
            if (s == null || !s.Connected || LoopGuard.ApplyingRemote || !GameRefs.InWorld) return;
            var w = new Writer();
            w.U8(kind).U8(arg);
            s.SendReliable(PacketKind.PlayerAction, w.ToArray());
            _actionsSent++;
        }

        public static void ApplyAction(byte[] payload)
        {
            if (payload == null || payload.Length < 2) return;
            var r = new Reader(payload);
            byte kind = r.U8();
            byte arg = r.U8();
            _actionsApplied++;
            RemoteAvatar.TriggerReliableAction(kind, arg);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionPlayerWeapons), "SetWeaponActionState")]
    internal static class Patch_PlayerWeapons_SetState
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix(global::DelusionPlayerWeapons __instance, global::DelusionPlayerWeapons.WeaponState newState)
        {
            if (newState != global::DelusionPlayerWeapons.WeaponState.Attacking) return;
            try
            {
                byte atk = (byte)(int)__instance.currentAttack;
                byte wt = (byte)(int)__instance.currentWeaponType;
                PlayerSync.SendPlayerAction(0, (byte)((atk & 0x0F) | ((wt & 0x0F) << 4)));
            }
            catch { }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionPlayerWeapons), nameof(global::DelusionPlayerWeapons.BowRelease))]
    internal static class Patch_PlayerWeapons_BowRelease
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix() => PlayerSync.SendPlayerAction(1, 0);
    }

    [HarmonyLib.HarmonyPatch(typeof(global::DelusionPlayerActions), "ParriedEnemy")]
    internal static class Patch_PlayerActions_Parried
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix() => PlayerSync.SendPlayerAction(4, 0);
    }

    [HarmonyLib.HarmonyPatch(typeof(global::PlayerAnimations), nameof(global::PlayerAnimations.TriggerPotionAnimation))]
    internal static class Patch_PlayerAnimations_Potion
    {
        [HarmonyLib.HarmonyPostfix]
        static void Postfix() => PlayerSync.SendPlayerAction(3, 0);
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
            w.I32(day).U8((byte)tod).F32(dn._starTimeSeconds);
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
                    var stats = GameRefs.Stats;
                    if (dn == null || stats == null)
                    {
                        if (dn != null) { dn._starTimeSeconds = star; dn.typeOfDay = tod; }
                        if (stats != null) stats.daysPlayed = day;
                        return;
                    }

                    // A game day is 1440 clock seconds (12 "hours" of 120 s; SetHour = h*120).
                    const float DayLen = 1440f;
                    float localStar = dn._starTimeSeconds;
                    int localDay = stats.daysPlayed;
                    double delta = (double)(day - localDay) * DayLen + (star - localStar);

                    if (delta > 0 && delta <= 3 * DayLen)
                    {
                        // Host is ahead: advance through the game's own clock so DayHasPassed
                        // (DayPassed, OnNewDay) and OnTypeOfDayChanged fire here too - spawners,
                        // pickups, shops and time-of-day triggers depend on them. Chunks stay
                        // below one day: PassTime handles at most one midnight per call.
                        double remaining = delta;
                        for (int guard = 0; remaining > 0.01 && guard < 8; guard++)
                        {
                            float chunk = (float)Math.Min(remaining, DayLen - 1f);
                            float before = dn._starTimeSeconds;
                            int dayBefore = stats.daysPlayed;
                            dn.PassTime(chunk);
                            // PassTime does nothing while the game clock is paused.
                            if (stats.daysPlayed == dayBefore && Math.Abs(dn._starTimeSeconds - before) < 0.001f)
                                break;
                            remaining -= chunk;
                        }
                        dn._starTimeSeconds = star;
                        if (stats.daysPlayed != day) stats.daysPlayed = day;
                        if (remaining > 0.01) dn.typeOfDay = tod;
                    }
                    else if (delta < 0 && delta > -60)
                    {
                        // Small drift (this side ran a little fast): snap back.
                        dn._starTimeSeconds = star;
                        stats.daysPlayed = day;
                    }
                    else if (delta < 0 && WorldEventsSync.RecentLocalRest(15f))
                    {
                        // We just rested; this state predates the host seeing our rest.
                        // Never rewind a rest - the host catches up and re-sends.
                    }
                    else if (delta != 0)
                    {
                        // Large jump either way (save loaded, multi-day skip): host authority.
                        dn._starTimeSeconds = star;
                        dn.typeOfDay = tod;
                        stats.daysPlayed = day;
                    }
                }
                catch (Exception e) { Log.Debug($"clock apply failed: {e.Message}"); }
            });
        }
    }
}
