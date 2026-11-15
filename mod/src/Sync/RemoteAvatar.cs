using System;
using System.Collections.Generic;
using DelusionMP.Game;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>What the partner is doing, streamed with every pose.</summary>
    public struct AvatarState
    {
        public byte WeaponType;     // WeaponType: Sword 0, Dagger 1, Greatsword 2, Bow 3; 255 = none
        public byte Rarity;         // ItemRarity: Poor 0 .. Emberian 6
        public byte WeaponState;    // DelusionPlayerWeapons.WeaponState
        public byte Attack;         // DelusionPlayerWeapons.AttackType
        public byte SpellState;     // DelusionPlayerSpell.SpellState
        public byte SpellType;      // PlayerAnimations.ActiveSpellType
        public bool Grounded;
        public bool Sprinting;
        public bool Crouched;
        public int Health;
    }

    /// <summary>
    /// The partner's third-person body: the game's own female character model
    /// (fbx_UnionWomen, Armature_F rig, a_UnionWomen controller) cloned from the always
    /// loaded DelusionMain scene with every script and collider stripped, dressed as an
    /// adventurer, holding the partner's equipped weapon (the rig's own skinned weapon parts,
    /// or a world copy of the weapon rig from the airship trophy wall attached to the hand),
    /// and animated with its own clips from the partner's movement, attacks and spells.
    ///
    /// The game has no jump clip for any humanoid, so an airborne partner holds a mid-stride
    /// pose until landing.
    /// </summary>
    public static class RemoteAvatar
    {
        public static string[] Outfit =
        {
            "Thief_F_body", "Thief_F_head", "Thief_F_hood", "Thief_F_gaiter",
        };

        private static GameObject _model;
        private static Animator _anim;
        private static Transform _handR;
        private static readonly Dictionary<string, GameObject> _parts = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static GameObject _heldClone;
        private static string _heldKey = "";
        public static Vector3 HeldEuler = new Vector3(0f, 0f, 90f);
        public static Vector3 HeldOffset = Vector3.zero;

        private static AvatarState _prev;
        private static float _airborneFor;
        private static bool _dead;
        private static string _oneShot = "";
        private static float _oneShotUntil;
        private static float _oneShotStart;

        public static bool Ready => _model != null && _anim != null;
        public static string Info
        {
            get
            {
                if (_model == null) return "avatar=none";
                string a = "";
                try
                {
                    if (_anim != null)
                    {
                        var info = _anim.GetCurrentAnimatorStateInfo(0);
                        a = $" ctrl={_anim.runtimeAnimatorController?.name} av={_anim.avatar?.name} valid={_anim.avatar?.isValid} init={_anim.isInitialized} " +
                            $"en={_anim.enabled} act={_anim.isActiveAndEnabled} speed={_anim.speed} layers={_anim.layerCount} state={info.fullPathHash} t={info.normalizedTime:F2} " +
                            $"loco={_anim.HasState(0, Animator.StringToHash("Locomotion"))}";
                    }
                }
                catch (Exception e) { a = " animErr=" + e.Message; }
                return $"avatar={_model.name} anim={_anim != null} parts={_parts.Count} held={_heldKey} oneshot={_oneShot}{a}";
            }
        }

        // ------------------------------------------------------------------ build

        private static GameObject FindSource(out RuntimeAnimatorController controller)
        {
            controller = null;
            GameObject best = null;
            var all = Resources.FindObjectsOfTypeAll<Animator>();
            if (all == null) return null;
            foreach (var a in all)
            {
                if (a == null) continue;
                var go = a.gameObject;
                if (!go.scene.IsValid()) continue;
                var rc = a.runtimeAnimatorController;
                if (rc != null && rc.name == "a_UnionWomen" && controller == null) controller = rc;
                if (go.name != "fbx_UnionWomen") continue;
                if (best == null || (rc != null && rc.name == "a_UnionWomen")) best = go;
            }
            if (controller == null)
            {
                // Any controller made for the female rig (same Armature_F bones).
                var seen = new HashSet<string>(StringComparer.Ordinal);
                RuntimeAnimatorController alt = null;
                foreach (var rc in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
                {
                    if (rc == null) continue;
                    var n = rc.name ?? "";
                    if (n.IndexOf("Wom", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fem", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        seen.Add(n);
                        if (n == "a_UnionWomen") { controller = rc; break; }
                        if (alt == null || n.StartsWith("a_", StringComparison.Ordinal)) alt = rc;
                    }
                }
                if (controller == null) controller = alt;
                Log.Info($"avatar: female controllers loaded: {string.Join(",", seen)} -> {(controller != null ? controller.name : "none")}");
            }
            return best;
        }

        /// <summary>Builds the model under <paramref name="root"/>; false if the game's model
        /// is not available (the caller keeps the mannequin).</summary>
        public static bool Build(Transform root, float feet)
        {
            try
            {
                var src = FindSource(out var controller);
                if (src == null) { Log.Warn("avatar: fbx_UnionWomen not found"); return false; }

                // Instantiate under an inactive holder so no Awake/OnEnable runs before the
                // scripts are stripped.
                var holder = new GameObject("DelusionMP_AvatarHolder");
                holder.SetActive(false);
                var clone = UnityEngine.Object.Instantiate(src, holder.transform);
                clone.name = "DelusionMP_PartnerBody";
                clone.SetActive(true);   // still inactive in hierarchy (holder is off)

                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null) UnityEngine.Object.DestroyImmediate(mb);
                foreach (var c in clone.GetComponentsInChildren<Collider>(true))
                    if (c != null) UnityEngine.Object.DestroyImmediate(c);
                foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
                    if (rb != null) UnityEngine.Object.DestroyImmediate(rb);

                var t = clone.transform;
                var worldScale = src.transform.lossyScale;
                t.SetParent(root, false);
                t.localPosition = new Vector3(0f, feet, 0f);
                t.localRotation = Quaternion.identity;
                t.localScale = worldScale;
                UnityEngine.Object.Destroy(holder);

                foreach (var tr in clone.GetComponentsInChildren<Transform>(true))
                {
                    tr.gameObject.layer = 0;
                    if (tr.name == "Hand.R") _handR = tr;
                }

                _parts.Clear();
                foreach (var smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null) continue;
                    smr.updateWhenOffscreen = true;
                    var go = smr.gameObject;
                    _parts[go.name] = go;
                    go.SetActive(false);
                }
                foreach (var n in Outfit) if (_parts.TryGetValue(n, out var p)) p.SetActive(true);

                _anim = clone.GetComponent<Animator>();
                if (_anim != null)
                {
                    if (controller != null && (_anim.runtimeAnimatorController == null || _anim.runtimeAnimatorController.name != "a_UnionWomen"))
                        _anim.runtimeAnimatorController = controller;
                    _anim.applyRootMotion = false;
                    _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    _anim.enabled = true;
                }
                _model = clone;
                _prev = default;
                _prev.WeaponType = 255;
                Log.Info($"avatar: built from {ObjectPath(src.transform)} parts={_parts.Count} hand={(_handR != null)} scale={worldScale.x:F2}");
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("avatar build failed: " + e.Message);
                if (_model != null) UnityEngine.Object.Destroy(_model);
                _model = null; _anim = null;
                return false;
            }
        }

        private static float _nextCtrlSearch;

        /// <summary>The full combat controller (a_UnionWomen) is only in memory while a region
        /// with female guards is loaded. Adopt it as soon as it appears; our Animator then
        /// keeps it alive for the rest of the session.</summary>
        private static void TryUpgradeController()
        {
            var cur = _anim.runtimeAnimatorController;
            if (cur != null && cur.name == "a_UnionWomen") return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextCtrlSearch) return;
            _nextCtrlSearch = now + 5f;
            try
            {
                foreach (var rc in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
                {
                    if (rc == null || rc.name != "a_UnionWomen") continue;
                    _anim.runtimeAnimatorController = rc;
                    _anim.Rebind();
                    _oneShot = "";
                    Log.Info("avatar: combat animations (a_UnionWomen) now available");
                    return;
                }
            }
            catch (Exception e) { Log.Debug("avatar controller search: " + e.Message); }
        }

        /// <summary>Rig: list the controller's clips, or force a state ("" = list).</summary>
        public static string DebugPlay(string state)
        {
            if (_anim == null) return "no animator";
            if (state.Length == 0)
            {
                var names = new List<string>();
                var rc = _anim.runtimeAnimatorController;
                if (rc != null) foreach (var c in rc.animationClips) if (c != null) names.Add(c.name);
                return $"clips={string.Join(",", names)}";
            }
            Override = null;
            _anim.speed = 1f;
            if (PlayClipDirect(state)) return $"clip {state}";
            _anim.Play(state, 0, 0f);
            _oneShot = state; _oneShotUntil = Time.realtimeSinceStartup + 600f;
            return $"playing {state} has={_anim.HasState(0, Animator.StringToHash(state))}";
        }

        public static void Destroyed() { StopClip(); _model = null; _anim = null; _handR = null; _parts.Clear(); _heldClone = null; _heldKey = ""; }

        public static void SetOutfit(string[] names)
        {
            Outfit = names;
            if (_model == null) return;
            foreach (var kv in _parts) if (!IsEquipmentPart(kv.Key)) kv.Value.SetActive(false);
            foreach (var n in names) if (_parts.TryGetValue(n, out var p)) p.SetActive(true);
        }

        public static IEnumerable<string> PartNames => _parts.Keys;

        private static bool IsEquipmentPart(string n) =>
            n.StartsWith("sword", StringComparison.Ordinal) || n.StartsWith("Holstered_", StringComparison.Ordinal)
            || n.StartsWith("Bow_", StringComparison.Ordinal) || n.StartsWith("spellBook", StringComparison.Ordinal)
            || n.StartsWith("SpellEffect_", StringComparison.Ordinal);

        private static string ObjectPath(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        // ------------------------------------------------------------------ equipment

        private static readonly string[] Tier = { "Rusty", "Steel", "Fine" };
        private static readonly string[] HolsterTier = { "Rust", "Steel", "Fine" };

        private static bool Drawn(in AvatarState s) =>
            s.WeaponState != 0 && s.WeaponState != 2 && s.WeaponState != 4;

        private static bool Casting(in AvatarState s) => s.SpellState == 1 || s.SpellState == 3;

        private static void Show(string part, bool on)
        {
            if (_parts.TryGetValue(part, out var go) && go.activeSelf != on) go.SetActive(on);
        }

        private static void UpdateEquipment(in AvatarState s)
        {
            bool drawn = Drawn(s) && !Casting(s);
            int tier = Mathf.Clamp(s.Rarity, 0, 2);
            bool simpleTier = s.Rarity <= 2;

            for (int i = 0; i < 3; i++)
            {
                Show($"sword{Tier[i]}_F", drawn && s.WeaponType == 0 && simpleTier && i == tier);
                Show($"Bow_{Tier[i]}_F", drawn && s.WeaponType == 3 && simpleTier && i == tier);
                Show($"Holstered_{HolsterTier[i]}Swrd_F", !drawn && s.WeaponType == 0 && i == tier);
            }

            // Daggers, greatswords and the special rarities: a world copy of the weapon rig.
            string key = "";
            if (drawn && s.WeaponType != 255 && !(simpleTier && (s.WeaponType == 0 || s.WeaponType == 3)))
                key = s.WeaponType + ":" + s.Rarity;
            if (key != _heldKey) SetHeld(key, s.WeaponType, s.Rarity);

            // Spellbook and the casting glow.
            bool cast = Casting(s);
            bool red = s.SpellType == 0 || s.SpellType == 4;
            Show("spellBookRed_F", cast && red);
            Show("spellBookBlue_F", cast && !red);
            Show("SpellEffect_Red_F", cast && s.SpellType == 0);
            Show("SpellEffect_Blue_F", cast && s.SpellType == 1);
            Show("SpellEffect_Green_F", cast && s.SpellType > 1);
        }

        private static readonly Dictionary<int, string> RigFor = new Dictionary<int, string>
        {
            { 0, "wpn_ShortSwords" }, { 1, "fbx_wpn_Daggers" }, { 2, "fbx_wpn_GSwords" }, { 3, "fbx_wpn_Bows" },
        };

        // Child mesh names per rarity (Poor, Common, Fine, Shroomweave, Clockwork, Void, Emberian).
        private static readonly Dictionary<int, string[][]> RarityParts = new Dictionary<int, string[][]>
        {
            { 0, new[] { new[]{"Rusty_sword"}, new[]{"Steel_Sword"}, new[]{"Fine_Sword"}, new[]{"Shroomweave_Blade","Shroomweave_Hilt"},
                         new[]{"cwk_MovingBlade_low","cwk_MovingGuard","cwk_StaticBlade_low","cwk_Sword_Grip"},
                         new[]{"VoidSteel_Sword","VoidSteel_Sword_Flair","VoidSteel_Sword_Metal"}, new[]{"Emberian_Sword"} } },
            { 1, new[] { new[]{"Rusty_Dagger"}, new[]{"Steel_Dagger"}, new[]{"Fine_Dagger"}, new[]{"Shroomweave_Dagger"},
                         new[]{"Clockwork_Dagger_Grip"}, new[]{"VoidSteel_Dagger_Handle"}, new[]{"Emberian_Dagger"} } },
            { 2, new[] { new[]{"Rusty_Greatsword"}, new[]{"TEMP_SteelGsword"}, new[]{"Fine_Greatsword"}, new[]{"Shroomweave_Greatsword"},
                         new[]{"Clockwork_Greatsword_Blade"}, new[]{"VoidSteel_Greatsword_Handle"}, new[]{"Emberian_Sword"} } },
            { 3, new[] { new[]{"Rusty_Bow","Rusty_Arrow_Notched"}, new[]{"Steel_Bow"}, new[]{"Fine_Bow"}, new[]{"Shroomweave_Bow"},
                         new[]{"Clockwork_Bowblade"}, new[]{"Voidsteel_Bow"}, new[]{"Emberian_Bow"} } },
        };

        private static Transform FindTrophyRig(string rigName)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>();
            if (all == null) return null;
            foreach (var t in all)
                if (t != null && t.name == rigName && t.parent != null && t.parent.name == "Weapons" && t.gameObject.scene.IsValid())
                    return t;
            return null;
        }

        private static void SetHeld(string key, int type, int rarity)
        {
            _heldKey = key;
            if (_heldClone != null) { UnityEngine.Object.Destroy(_heldClone); _heldClone = null; }
            if (key.Length == 0 || _handR == null) return;
            try
            {
                if (!RigFor.TryGetValue(type, out var rigName)) return;
                var src = FindTrophyRig(rigName);
                if (src == null) { Log.Debug("avatar: no trophy rig " + rigName); return; }

                var holder = new GameObject("DelusionMP_HeldHolder");
                holder.SetActive(false);
                var clone = UnityEngine.Object.Instantiate(src.gameObject, holder.transform);
                foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) UnityEngine.Object.DestroyImmediate(mb);
                foreach (var c in clone.GetComponentsInChildren<Collider>(true)) if (c != null) UnityEngine.Object.DestroyImmediate(c);
                foreach (var a in clone.GetComponentsInChildren<Animator>(true)) if (a != null) UnityEngine.Object.DestroyImmediate(a);
                foreach (var a in clone.GetComponentsInChildren<Animation>(true)) if (a != null) UnityEngine.Object.DestroyImmediate(a);
                foreach (var p in clone.GetComponentsInChildren<Renderer>(true)) if (p != null && p.GetIl2CppType().Name.Contains("Particle")) p.enabled = false;

                var want = new HashSet<string>(StringComparer.Ordinal);
                if (RarityParts.TryGetValue(type, out var table)) foreach (var n in table[Mathf.Clamp(rarity, 0, 6)]) want.Add(n);
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    var rn = r.GetIl2CppType().Name;
                    if (rn != "SkinnedMeshRenderer" && rn != "MeshRenderer") r.enabled = false;
                    else r.gameObject.SetActive(want.Contains(r.gameObject.name));
                }

                var worldScale = src.lossyScale;
                var ct = clone.transform;
                ct.SetParent(_handR, false);
                ct.localPosition = HeldOffset;
                ct.localRotation = Quaternion.Euler(HeldEuler);
                var hs = _handR.lossyScale;
                ct.localScale = new Vector3(worldScale.x / Mathf.Max(1e-4f, hs.x), worldScale.y / Mathf.Max(1e-4f, hs.y), worldScale.z / Mathf.Max(1e-4f, hs.z));
                foreach (var tr in clone.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = 0;
                clone.SetActive(true);
                UnityEngine.Object.Destroy(holder);
                _heldClone = clone;
            }
            catch (Exception e) { Log.Debug("avatar held item: " + e.Message); }
        }

        public static void RetuneHeld()
        {
            if (_heldClone == null) return;
            _heldClone.transform.localPosition = HeldOffset;
            _heldClone.transform.localRotation = Quaternion.Euler(HeldEuler);
        }

        // ------------------------------------------------------------------ animation

        private static void Play(string state, float fade = 0.1f)
        {
            try { _anim.CrossFadeInFixedTime(state, fade, 0); } catch { }
        }

        // One-shot swings are played as clips straight onto the Animator through a small
        // PlayableGraph: the controller's own transitions (built to be driven by the enemy
        // AI) would pull an attack state straight back to locomotion. When the clip ends the
        // graph is destroyed and the controller takes over again.
        private static readonly Dictionary<string, string> ShotClip = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Attack1a", "Attack1a_UnionFem" }, { "Attack1b_Goblin", "Attack1b__UnionFem" },
            { "Attack2a", "Attack2a_UnionFem" }, { "Attack2b", "Attack2b_UnionFem" },
            { "AttackSa", "AttackSa_UnionFem" }, { "AttackSb", "AttackSb_UnionFem" },
            { "RangedFire", "bowFire_UnionFem" }, { "SpellFire1", "spellFire1_UnionFem" },
            { "SpellFire2", "spellFire2_UnionFem" }, { "Taunt", "Taunt_UnionFem" },
        };
        private static UnityEngine.Playables.PlayableGraph _clipGraph;
        private static bool _clipActive;
        private static float _clipEnd;

        private static AnimationClip FindClip(string name)
        {
            var rc = _anim.runtimeAnimatorController;
            if (rc == null) return null;
            foreach (var c in rc.animationClips) if (c != null && c.name == name) return c;
            return null;
        }

        private static bool PlayClipDirect(string state)
        {
            if (!ShotClip.TryGetValue(state, out var clipName)) return false;
            var clip = FindClip(clipName);
            if (clip == null) { Log.Info($"avatar: clip {clipName} not in controller"); return false; }
            StopClip();
            try
            {
                // With a controller attached its output wins; detach it for the swing.
                _savedCtrl = _anim.runtimeAnimatorController;
                _anim.runtimeAnimatorController = null;
                _anim.speed = 1f;
                UnityEngine.Playables.AnimationPlayableUtilities.PlayClip(_anim, clip, out _clipGraph);
                _clipActive = true;
                _clipEnd = Time.realtimeSinceStartup + clip.length;
                Log.Info($"avatar: playing {clipName} ({clip.length:F2}s) graphValid={_clipGraph.IsValid()} playing={_clipGraph.IsPlaying()} outputs={_clipGraph.GetOutputCount()}");
                return true;
            }
            catch (Exception e)
            {
                Log.Debug("avatar clip: " + e.Message);
                if (_savedCtrl != null && _anim.runtimeAnimatorController == null) _anim.runtimeAnimatorController = _savedCtrl;
                return false;
            }
        }

        private static RuntimeAnimatorController _savedCtrl;

        private static void StopClip()
        {
            if (!_clipActive) return;
            _clipActive = false;
            try { if (_clipGraph.IsValid()) _clipGraph.Destroy(); } catch { }
            try
            {
                if (_anim != null && _savedCtrl != null)
                {
                    _anim.runtimeAnimatorController = _savedCtrl;
                    _anim.CrossFadeInFixedTime("Locomotion", 0f, 0);
                }
            }
            catch { }
        }

        private static string AttackState(byte attack, byte weaponType)
        {
            switch (attack)
            {
                case 1: return weaponType == 1 ? "Attack1b_Goblin" : "Attack1a";
                case 2: return "Attack2a";
                case 3: return "AttackSa";
                case 4: return "Attack2b";
                case 5: return "Attack1b_Goblin";
                case 6: return "RangedFire";
            }
            return null;
        }

        /// <summary>Per frame on the receiver. <paramref name="speed"/> is the partner's
        /// horizontal speed (m/s) as rendered.</summary>
        /// <summary>Rig testing: force a pose state instead of the streamed one.</summary>
        public static AvatarState? Override;

        public static void Animate(in AvatarState streamed, float speed, float dt)
        {
            if (!Ready) return;
            if (_clipActive)
            {
                if (Time.realtimeSinceStartup < _clipEnd) { try { UpdateEquipment(Override ?? streamed); } catch { } _prev = Override ?? streamed; return; }
                StopClip();
            }
            TryUpgradeController();
            var s = streamed;
            if (Override.HasValue)
            {
                var o = Override.Value;
                o.Grounded = streamed.Grounded; o.Health = streamed.Health; o.Sprinting = streamed.Sprinting;
                s = o;
            }
            try
            {
                UpdateEquipment(s);
                var prev = _prev;
                _prev = s;
                float now = Time.realtimeSinceStartup;

                // Death and revival.
                if (s.Health <= 0 && prev.Health > 0) { _dead = true; _anim.speed = 1f; Play("Death", 0.1f); return; }
                if (_dead)
                {
                    if (s.Health > 0) { _dead = false; Play("Locomotion", 0.2f); }
                    else return;
                }

                // Hit reaction (additive layer).
                if (s.Health < prev.Health && prev.Health > 0)
                {
                    try { _anim.SetLayerWeight(1, 1f); _anim.Play("TakeDamage", 1, 0f); } catch { }
                }

                bool drawn = Drawn(s) || Casting(s);
                try
                {
                    _anim.SetBool("weaponOut", drawn);
                    _anim.SetLayerWeight(2, drawn ? 0f : 1f);
                    _anim.SetLayerWeight(3, 0f);
                }
                catch { }

                float pct = speed < 0.25f ? 0f : (s.Sprinting || speed > 5.5f ? 1f : Mathf.Clamp(speed / 6f, 0.3f, 0.6f));
                try { _anim.SetFloat("speedPercent", pct, 0.12f, dt); } catch { }

                // One-shots: attacks, bow and spells, on the edge where they start.
                string shot = null;
                bool attackEdge = (s.WeaponState == 9 && (prev.WeaponState != 9 || s.Attack != prev.Attack))
                                  || (s.Attack == 6 && prev.Attack != 6);
                if (attackEdge) shot = AttackState(s.Attack, s.WeaponType);
                else if (s.WeaponType == 3 && (s.WeaponState == 6 || s.WeaponState >= 11) && prev.WeaponState != s.WeaponState) shot = "RangedStart";
                else if (s.SpellState == 1 && prev.SpellState != 1) shot = "SpellStart";
                else if (s.SpellState == 3 && prev.SpellState != 3) shot = "SpellFire1";

                if (_clipActive && Time.realtimeSinceStartup >= _clipEnd) StopClip();

                if (shot != null)
                {
                    _anim.speed = 1f;
                    if (PlayClipDirect(shot)) { _oneShot = ""; shot = null; }
                    else Play(shot, 0.06f);
                    if (shot == null) goto afterShot;
                    _oneShot = shot;
                    _oneShotStart = now;
                    _oneShotUntil = now + (shot.StartsWith("Spell") || shot.StartsWith("Ranged") ? 30f : 1.2f);
                }
                else if (_oneShot.Length > 0)
                {
                    // Hold a charge/draw pose while the partner holds it; otherwise return to
                    // locomotion once the one-shot clip has really played out.
                    bool holding = (_oneShot == "SpellStart" && s.SpellState == 1)
                                   || (_oneShot == "RangedStart" && Drawn(s) && s.WeaponType == 3 && s.Attack != 6);
                    if (!holding && now - _oneShotStart > 0.2f && !_anim.IsInTransition(0))
                    {
                        var info = _anim.GetCurrentAnimatorStateInfo(0);
                        bool inShot = info.IsName(_oneShot);
                        if (!inShot || info.normalizedTime >= 0.95f || now > _oneShotUntil)
                        {
                            Play("Locomotion", 0.15f);
                            _oneShot = "";
                        }
                    }
                }

                afterShot:
                // Airborne: there is no jump clip; hold a mid-stride pose.
                _airborneFor = s.Grounded ? 0f : _airborneFor + dt;
                if (_oneShot.Length == 0)
                    _anim.speed = _airborneFor > 0.25f ? 0f : 1f;
            }
            catch (Exception e) { Log.Debug("avatar animate: " + e.Message); }
        }
    }
}
