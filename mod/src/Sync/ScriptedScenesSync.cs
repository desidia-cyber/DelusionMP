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
    /// Synchronization for scripted story scenes, non-saveable world machinery, and
    /// animated world triggers that previously ran locally per machine:
    ///   * SkiffManager (host-authoritative moving skiff state machine &amp; depart)
    ///   * VelaFortressLift (Blinding Light fortress raise/lower &amp; sky objects)
    ///   * VelaUnderlandsScene (Underlands encounter trigger &amp; post-cutscene cleanup)
    ///   * CutterTransportScript (cutter departure sequences)
    ///   * CryptWyrm (wyrm landing / flying animation &amp; dialogue toggle)
    ///   * CrystalSkull_Master / CrystalSkull_Trig / CrystalSkull_Door (timed puzzle &amp; door)
    ///   * DelusionLever (standalone lever pull/reset animations)
    ///   * TriggerWithScript (non-saveable scripted triggers &amp; animator toggles)
    ///   * ProximityTrigger (activateObject sync)
    ///   * TriggerSendMessage (scripted SendMessage sync)
    ///   * PlayerCloseAnim (ambient proximity animations)
    ///   * EndlessQuake (scripted earthquake)
    ///   * LoreTrigger (individual lore pedestal animations)
    /// </summary>
    public static class ScriptedScenesSync
    {
        private const byte MsgSkiffState = 0;
        private const byte MsgSkiffDepart = 1;
        private const byte MsgFortressLift = 2;
        private const byte MsgVelaUnderlands = 3;
        private const byte MsgCutter = 4;
        private const byte MsgCryptWyrm = 5;
        private const byte MsgCrystalMaster = 6;
        private const byte MsgCrystalTrig = 7;
        private const byte MsgCrystalDoor = 8;
        private const byte MsgLever = 9;
        private const byte MsgTriggerScript = 10;
        private const byte MsgProximityTrig = 11;
        private const byte MsgSendMessage = 12;
        private const byte MsgPlayerCloseAnim = 13;
        private const byte MsgEndlessQuake = 14;
        private const byte MsgLoreTrigger = 15;

        private static int _sent, _applied, _pendingCount;
        private static string _last = "";

        public static string Stats =>
            $"sent={_sent} applied={_applied} pending={_pending.Count} velaDone={_velaDone.Count} doors={_skullDoors.Count} last='{_last}'";

        private static bool Connected => Session.Instance != null && Session.Instance.Connected;
        private static bool IsHost => Session.Instance?.Role == SessionRole.Host;
        private static bool IsClient => Session.Instance?.Role == SessionRole.Client;
        private static bool CanSend => Connected && !LoopGuard.ApplyingRemote && GameRefs.InWorld;

        private static void Send(Writer w)
        {
            Session.Instance.SendReliable(PacketKind.ScriptedScene, w.ToArray());
            _sent++;
        }

        private static string Key(Component c)
        {
            if (c == null) return null;
            try { return EnemySync.KeyFor(ObjectRegistry.Instance, c); }
            catch { return null; }
        }

        private static class Index<T> where T : Component
        {
            public static readonly Dictionary<string, T> Map = new Dictionary<string, T>(StringComparer.Ordinal);
            public static float BuiltAt = -10f;
        }

        private static T Find<T>(string key) where T : Component
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (Index<T>.Map.TryGetValue(key, out var c) && (UnityEngine.Object)c != null) return c;
            float now = Time.realtimeSinceStartup;
            if (now - Index<T>.BuiltAt < 1f) return null;
            Index<T>.BuiltAt = now;
            Index<T>.Map.Clear();
            var all = Resources.FindObjectsOfTypeAll<T>();
            if (all != null)
                foreach (var x in all)
                {
                    if ((UnityEngine.Object)x == null) continue;
                    try
                    {
                        if (!x.gameObject.scene.IsValid()) continue;
                        var k = Key(x);
                        if (k != null && !Index<T>.Map.ContainsKey(k)) Index<T>.Map[k] = x;
                    }
                    catch { }
                }
            return Index<T>.Map.TryGetValue(key, out c) ? c : null;
        }

        // Stateful events remembered for late-join and pending unloaded regions.
        private static readonly Dictionary<string, (byte state, bool startedAtA)> _skiffLast = new Dictionary<string, (byte, bool)>(StringComparer.Ordinal);
        private static readonly Dictionary<string, byte> _fortressLast = new Dictionary<string, byte>(StringComparer.Ordinal);
        private static readonly Dictionary<string, (byte op, float at)> _fortressRecent = new Dictionary<string, (byte, float)>(StringComparer.Ordinal);
        private static readonly Dictionary<string, (bool pull, float at)> _leverRecent = new Dictionary<string, (bool, float)>(StringComparer.Ordinal);
        private static readonly Dictionary<string, byte> _velaDone = new Dictionary<string, byte>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> _wyrmState = new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly HashSet<string> _skullDoors = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, (bool on, bool objectsOnly)> _scriptTriggers = new Dictionary<string, (bool, bool)>(StringComparer.Ordinal);
        private static readonly HashSet<string> _proxTriggers = new HashSet<string>(StringComparer.Ordinal);

        // Skiff host-driven tracking on client.
        private static readonly Dictionary<string, float> _skiffDrivenAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private const float SkiffHostTimeout = 25f;

        // Pending packets for objects whose region wasn't loaded yet.
        private static readonly List<byte[]> _pending = new List<byte[]>();
        private static float _nextPendingCheck;

        public static void OnSessionStart(bool isHost = false)
        {
            if (!isHost)
            {
                _skiffLast.Clear();
                _fortressLast.Clear();
                _fortressRecent.Clear();
                _leverRecent.Clear();
                _velaDone.Clear();
                _wyrmState.Clear();
                _skullDoors.Clear();
                _scriptTriggers.Clear();
                _proxTriggers.Clear();
            }
            _skiffDrivenAt.Clear();
            _pending.Clear();
            _pendingCount = 0;
        }

        public static void SendJoinState()
        {
            if (!Connected || !IsHost) return;
            foreach (var kv in _skiffLast)
                Send(new Writer().U8(MsgSkiffState).Str(kv.Key).U8(kv.Value.state).Bool(kv.Value.startedAtA));
            foreach (var kv in _fortressLast)
                Send(new Writer().U8(MsgFortressLift).Str(kv.Key).U8(kv.Value));
            foreach (var kv in _velaDone)
                Send(new Writer().U8(MsgVelaUnderlands).Str(kv.Key).U8(kv.Value));
            foreach (var kv in _wyrmState)
                Send(new Writer().U8(MsgCryptWyrm).Str(kv.Key).Bool(kv.Value));
            foreach (var k in _skullDoors)
                Send(new Writer().U8(MsgCrystalDoor).Str(k));
            foreach (var kv in _scriptTriggers)
                Send(new Writer().U8(MsgTriggerScript).Str(kv.Key).Bool(kv.Value.on).Bool(kv.Value.objectsOnly));
            foreach (var k in _proxTriggers)
                Send(new Writer().U8(MsgProximityTrig).Str(k));
        }

        public static void Tick()
        {
            if (!Connected || !GameRefs.InWorld || _pending.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextPendingCheck) return;
            _nextPendingCheck = now + 1.5f;
            for (int i = 0; i < _pending.Count; )
            {
                if (TryApply(_pending[i], queueIfMissing: false))
                    _pending.RemoveAt(i);
                else
                    i++;
            }
            _pendingCount = _pending.Count;
        }

        public static void Apply(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            TryApply(payload, queueIfMissing: true);
        }

        private static bool TryApply(byte[] payload, bool queueIfMissing)
        {
            var r = new Reader(payload);
            byte kind = 255;
            try
            {
                kind = r.U8();
                switch (kind)
                {
                    case MsgSkiffState:
                    {
                        string key = r.Str();
                        var st = (global::SkiffState)r.U8();
                        bool startedAtA = r.Bool();
                        if (!IsClient) return true;
                        _skiffDrivenAt[key] = Time.realtimeSinceStartup;
                        var sk = Find<global::SkiffManager>(key);
                        if (sk == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                sk.startedAtA = startedAtA;
                                if (sk.st8 != st) sk.SetSt8(st);
                                _applied++;
                                _last = $"skiff {st}";
                            }
                            catch (Exception e) { Log.Debug("apply skiff: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgSkiffDepart:
                    {
                        string key = r.Str();
                        var sk = Find<global::SkiffManager>(key);
                        if (IsHost)
                        {
                            if (sk == null)
                            {
                                // Host doesn't have this skiff loaded; let the client depart locally.
                                Send(new Writer().U8(MsgSkiffDepart).Str(key));
                                return true;
                            }
                            try { sk.Depart(); _applied++; _last = "skiff depart"; }
                            catch (Exception e) { Log.Debug("apply skiff depart: " + e.Message); }
                            return true;
                        }
                        if (IsClient && sk != null)
                        {
                            LoopGuard.Apply(() =>
                            {
                                try { sk.Depart(); _applied++; _last = "skiff depart local"; }
                                catch (Exception e) { Log.Debug("apply skiff depart local: " + e.Message); }
                            });
                        }
                        return true;
                    }
                    case MsgFortressLift:
                    {
                        string key = r.Str();
                        byte op = r.U8();
                        _fortressLast[key] = op;
                        var fl = Find<global::VelaFortressLift>(key);
                        if (fl == null) { QueueIf(queueIfMissing, payload); return false; }
                        float now = Time.realtimeSinceStartup;
                        if (_fortressRecent.TryGetValue(key, out var rec) && rec.op == op && now - rec.at < 5f)
                            return true;
                        _fortressRecent[key] = (op, now);
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                switch (op)
                                {
                                    case 0: fl.ActivateObject(); break;
                                    case 1: fl.FortressMovingDown(); break;
                                    case 2: fl.FortressRaisingUp(); break;
                                    case 3: fl.CinematicAnimComplete(); break;
                                }
                                _applied++;
                                _last = $"fortress {op}";
                            }
                            catch (Exception e) { Log.Debug("apply fortress: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgVelaUnderlands:
                    {
                        string key = r.Str();
                        byte op = r.U8();
                        bool alreadyDone = _velaDone.TryGetValue(key, out var prevOp) && prevOp >= op;
                        _velaDone[key] = prevOp > op ? prevOp : op;
                        var vu = Find<global::VelaUnderlandsScene>(key);
                        if (vu == null) { QueueIf(queueIfMissing, payload); return false; }
                        if (alreadyDone && vu.hasBeenTriggered) return true;
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                vu.hasBeenTriggered = true;
                                if (op == 1)
                                {
                                    if (vu.disableBeforeForcedRespawn != null) vu.disableBeforeForcedRespawn.SetActive(false);
                                    vu.AfterCutscene();
                                }
                                _applied++;
                                _last = $"velaUnderlands {op}";
                            }
                            catch (Exception e) { Log.Debug("apply velaUnderlands: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgCutter:
                    {
                        string key = r.Str();
                        byte op = r.U8();
                        var ct = Find<global::CutterTransportScript>(key);
                        if (ct == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                switch (op)
                                {
                                    case 0: ct.UnderlandsJourneyBegin(); break;
                                    case 1: ct.CradleJourneyBegin(); break;
                                    case 2: ct.ReturnToBlindingLight(); break;
                                }
                                _applied++;
                                _last = $"cutter {op}";
                            }
                            catch (Exception e) { Log.Debug("apply cutter: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgCryptWyrm:
                    {
                        string key = r.Str();
                        bool on = r.Bool();
                        _wyrmState[key] = on;
                        var cw = Find<global::CryptWyrm>(key);
                        if (cw == null) { QueueIf(queueIfMissing, payload); return false; }
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                if (on) cw.Activate(); else cw.Deactivate();
                                _applied++;
                                _last = $"wyrm {on}";
                            }
                            catch (Exception e) { Log.Debug("apply wyrm: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgCrystalMaster:
                    {
                        string key = r.Str();
                        byte op = r.U8();
                        var cm = Find<global::CrystalSkull_Master>(key);
                        if (cm == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                switch (op)
                                {
                                    case 0: if (!cm.timerActive) cm.StartTimer(); break;
                                    case 1: if (cm.timerActive) cm.FailTimer(); break;
                                    case 2: if (!cm.challengeComplete) cm.ChallengeComplete(false); break;
                                }
                                _applied++;
                                _last = $"crystalMaster {op}";
                            }
                            catch (Exception e) { Log.Debug("apply crystalMaster: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgCrystalTrig:
                    {
                        string key = r.Str();
                        var ct = Find<global::CrystalSkull_Trig>(key);
                        if (ct == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try { ct.SkullUsed(); _applied++; _last = "crystalTrig"; }
                            catch (Exception e) { Log.Debug("apply crystalTrig: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgCrystalDoor:
                    {
                        string key = r.Str();
                        _skullDoors.Add(key);
                        var cd = Find<global::CrystalSkull_Door>(key);
                        if (cd == null) { QueueIf(queueIfMissing, payload); return false; }
                        if (cd.isOpen) return true;
                        LoopGuard.Apply(() =>
                        {
                            try { cd.OpenDoor(); _applied++; _last = "crystalDoor"; }
                            catch (Exception e) { Log.Debug("apply crystalDoor: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgLever:
                    {
                        string key = r.Str();
                        bool pull = r.Bool();
                        var lv = Find<global::DelusionLever>(key);
                        if (lv == null) return true;
                        float now = Time.realtimeSinceStartup;
                        if (_leverRecent.TryGetValue(key, out var rec) && rec.pull == pull && now - rec.at < 3f)
                            return true;
                        _leverRecent[key] = (pull, now);
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                if (pull) lv.PullLever(); else lv.ResetLever();
                                _applied++;
                                _last = $"lever {(pull ? "pull" : "reset")}";
                            }
                            catch (Exception e) { Log.Debug("apply lever: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgTriggerScript:
                    {
                        string key = r.Str();
                        bool on = r.Bool();
                        bool objectsOnly = r.Bool();
                        _scriptTriggers[key] = (on, objectsOnly);
                        var ts = Find<global::TriggerWithScript>(key);
                        if (ts == null) { QueueIf(queueIfMissing, payload); return false; }
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                if (on) ts.Activate(objectsOnly, false);
                                else ts.Deactivate();
                                _applied++;
                                _last = $"triggerScript {on}";
                            }
                            catch (Exception e) { Log.Debug("apply triggerScript: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgProximityTrig:
                    {
                        string key = r.Str();
                        _proxTriggers.Add(key);
                        var pt = Find<global::ProximityTrigger>(key);
                        if (pt == null) { QueueIf(queueIfMissing, payload); return false; }
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                if (pt.activateObject != null && !pt.activateObject.activeSelf)
                                    pt.activateObject.SetActive(true);
                                _applied++;
                                _last = "proxTrig";
                            }
                            catch (Exception e) { Log.Debug("apply proxTrig: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgSendMessage:
                    {
                        string key = r.Str();
                        var sm = Find<global::TriggerSendMessage>(key);
                        if (sm == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try { sm.CheckParamsAndSend(); _applied++; _last = "sendMsg"; }
                            catch (Exception e) { Log.Debug("apply sendMsg: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgPlayerCloseAnim:
                    {
                        string key = r.Str();
                        bool enter = r.Bool();
                        var pca = Find<global::PlayerCloseAnim>(key);
                        if (pca == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try
                            {
                                if (pca.anim != null)
                                {
                                    pca.anim.enabled = true;
                                    pca.anim.SetBool("isActive", enter);
                                }
                                _applied++;
                                _last = $"closeAnim {enter}";
                            }
                            catch (Exception e) { Log.Debug("apply closeAnim: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgEndlessQuake:
                    {
                        string key = r.Str();
                        var eq = Find<global::EndlessQuake>(key);
                        if (eq == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try { eq.TriggerEarthquake(); _applied++; _last = "endlessQuake"; }
                            catch (Exception e) { Log.Debug("apply endlessQuake: " + e.Message); }
                        });
                        return true;
                    }
                    case MsgLoreTrigger:
                    {
                        string key = r.Str();
                        var lt = Find<global::LoreTrigger>(key);
                        if (lt == null) return true;
                        LoopGuard.Apply(() =>
                        {
                            try { lt.Activation(false); _applied++; _last = "loreTrig"; }
                            catch (Exception e) { Log.Debug("apply loreTrig: " + e.Message); }
                        });
                        return true;
                    }
                }
            }
            catch (Exception e) { Log.Warn($"scripted scene {kind} failed: {e.Message}"); }
            return true;
        }

        private static void QueueIf(bool queue, byte[] payload)
        {
            if (!queue || _pending.Count >= 128) return;
            _pending.Add(payload);
            _pendingCount = _pending.Count;
        }

        // ---------------- outbound hooks ----------------

        internal static bool OnSkiffSetSt8(global::SkiffManager sk, global::SkiffState newSt8)
        {
            if (LoopGuard.ApplyingRemote || !Connected || sk == null) return true;
            var key = Key(sk);
            if (key == null) return true;
            if (IsClient)
            {
                if (_skiffDrivenAt.TryGetValue(key, out var t) && Time.realtimeSinceStartup - t < SkiffHostTimeout)
                    return false;
                return true;
            }
            _skiffLast[key] = ((byte)(int)newSt8, sk.startedAtA);
            Send(new Writer().U8(MsgSkiffState).Str(key).U8((byte)(int)newSt8).Bool(sk.startedAtA));
            return true;
        }

        internal static bool OnSkiffDepart(global::SkiffManager sk)
        {
            if (LoopGuard.ApplyingRemote || !Connected || sk == null) return true;
            var key = Key(sk);
            if (key == null) return true;
            if (IsClient)
            {
                Send(new Writer().U8(MsgSkiffDepart).Str(key));
                return false;
            }
            return true;
        }

        internal static void OnFortressLift(global::VelaFortressLift fl, byte op)
        {
            if (fl == null) return;
            var key = Key(fl);
            if (key == null) return;
            _fortressLast[key] = op;
            if (!CanSend) return;
            float now = Time.realtimeSinceStartup;
            if (_fortressRecent.TryGetValue(key, out var rec) && rec.op == op && now - rec.at < 5f)
                return;
            _fortressRecent[key] = (op, now);
            Send(new Writer().U8(MsgFortressLift).Str(key).U8(op));
        }

        internal static bool OnVelaUnderlandsPre(global::VelaUnderlandsScene vu)
        {
            if (LoopGuard.ApplyingRemote || !Connected || vu == null) return true;
            var key = Key(vu);
            if (key != null && vu.onlyTriggerOnce && _velaDone.ContainsKey(key))
            {
                vu.hasBeenTriggered = true;
                return false;
            }
            return true;
        }

        internal static void OnVelaUnderlandsPost(global::VelaUnderlandsScene vu, byte op)
        {
            if (vu == null) return;
            var key = Key(vu);
            if (key == null) return;
            _velaDone[key] = op;
            if (!CanSend) return;
            Send(new Writer().U8(MsgVelaUnderlands).Str(key).U8(op));
        }

        internal static void OnCutter(global::CutterTransportScript ct, byte op)
        {
            if (!CanSend || ct == null) return;
            var key = Key(ct);
            if (key == null) return;
            Send(new Writer().U8(MsgCutter).Str(key).U8(op));
        }

        internal static void OnCryptWyrm(global::CryptWyrm cw, bool on)
        {
            if (cw == null) return;
            var key = Key(cw);
            if (key == null) return;
            _wyrmState[key] = on;
            if (!CanSend) return;
            Send(new Writer().U8(MsgCryptWyrm).Str(key).Bool(on));
        }

        internal static void OnCrystalMaster(global::CrystalSkull_Master cm, byte op)
        {
            if (!CanSend || cm == null) return;
            var key = Key(cm);
            if (key == null) return;
            Send(new Writer().U8(MsgCrystalMaster).Str(key).U8(op));
        }

        internal static void OnCrystalTrig(global::CrystalSkull_Trig ct)
        {
            if (!CanSend || ct == null) return;
            var key = Key(ct);
            if (key == null) return;
            Send(new Writer().U8(MsgCrystalTrig).Str(key));
        }

        internal static void OnCrystalDoor(global::CrystalSkull_Door cd)
        {
            if (cd == null) return;
            var key = Key(cd);
            if (key == null) return;
            _skullDoors.Add(key);
            if (!CanSend) return;
            Send(new Writer().U8(MsgCrystalDoor).Str(key));
        }

        internal static void OnLever(global::DelusionLever lv, bool pull)
        {
            if (!CanSend || lv == null) return;
            var key = Key(lv);
            if (key == null) return;
            float now = Time.realtimeSinceStartup;
            if (_leverRecent.TryGetValue(key, out var rec) && rec.pull == pull && now - rec.at < 3f)
                return;
            _leverRecent[key] = (pull, now);
            Send(new Writer().U8(MsgLever).Str(key).Bool(pull));
        }

        internal static void OnTriggerScript(global::TriggerWithScript ts, bool on, bool objectsOnly, bool fromSave)
        {
            if (fromSave || ts == null) return;
            var key = Key(ts);
            if (key == null) return;
            _scriptTriggers[key] = (on, objectsOnly);
            if (!CanSend) return;
            Send(new Writer().U8(MsgTriggerScript).Str(key).Bool(on).Bool(objectsOnly));
        }

        internal static void OnProximityTriggerEnter(global::ProximityTrigger pt, Collider other)
        {
            if (!CanSend || pt == null || other == null) return;
            var player = GameRefs.Player;
            if (player == null || !other.transform.IsChildOf(player.transform)) return;
            var key = Key(pt);
            if (key == null || _proxTriggers.Contains(key)) return;
            _proxTriggers.Add(key);
            Send(new Writer().U8(MsgProximityTrig).Str(key));
        }

        internal static void OnSendMessage(global::TriggerSendMessage sm)
        {
            if (!CanSend || sm == null) return;
            var key = Key(sm);
            if (key == null) return;
            Send(new Writer().U8(MsgSendMessage).Str(key));
        }

        internal static void OnPlayerCloseAnim(global::PlayerCloseAnim pca, Collider other, bool enter)
        {
            if (!CanSend || pca == null || other == null) return;
            var player = GameRefs.Player;
            if (player == null || !other.transform.IsChildOf(player.transform)) return;
            var key = Key(pca);
            if (key == null) return;
            Send(new Writer().U8(MsgPlayerCloseAnim).Str(key).Bool(enter));
        }

        internal static void OnEndlessQuake(global::EndlessQuake eq)
        {
            if (!CanSend || eq == null) return;
            var key = Key(eq);
            if (key == null) return;
            Send(new Writer().U8(MsgEndlessQuake).Str(key));
        }

        internal static void OnLoreTrigger(global::LoreTrigger lt, bool fromSave)
        {
            if (fromSave || !CanSend || lt == null) return;
            var key = Key(lt);
            if (key == null) return;
            Send(new Writer().U8(MsgLoreTrigger).Str(key));
        }
    }

    // ====================================================================== Harmony patches

    [HarmonyPatch(typeof(global::SkiffManager), "SetSt8")]
    internal static class Patch_SkiffManager_SetSt8
    {
        [HarmonyPrefix] static bool Prefix(global::SkiffManager __instance, global::SkiffState newSt8) => ScriptedScenesSync.OnSkiffSetSt8(__instance, newSt8);
    }

    [HarmonyPatch(typeof(global::SkiffManager), nameof(global::SkiffManager.Depart))]
    internal static class Patch_SkiffManager_Depart
    {
        [HarmonyPrefix] static bool Prefix(global::SkiffManager __instance) => ScriptedScenesSync.OnSkiffDepart(__instance);
    }

    [HarmonyPatch(typeof(global::VelaFortressLift), nameof(global::VelaFortressLift.ActivateObject))]
    internal static class Patch_VelaFortress_Activate
    {
        [HarmonyPostfix] static void Postfix(global::VelaFortressLift __instance) => ScriptedScenesSync.OnFortressLift(__instance, 0);
    }

    [HarmonyPatch(typeof(global::VelaFortressLift), "FortressMovingDown")]
    internal static class Patch_VelaFortress_Down
    {
        [HarmonyPostfix] static void Postfix(global::VelaFortressLift __instance) => ScriptedScenesSync.OnFortressLift(__instance, 1);
    }

    [HarmonyPatch(typeof(global::VelaFortressLift), "FortressRaisingUp")]
    internal static class Patch_VelaFortress_Up
    {
        [HarmonyPostfix] static void Postfix(global::VelaFortressLift __instance) => ScriptedScenesSync.OnFortressLift(__instance, 2);
    }

    [HarmonyPatch(typeof(global::VelaFortressLift), nameof(global::VelaFortressLift.CinematicAnimComplete))]
    internal static class Patch_VelaFortress_CineComplete
    {
        [HarmonyPostfix] static void Postfix(global::VelaFortressLift __instance) => ScriptedScenesSync.OnFortressLift(__instance, 3);
    }

    [HarmonyPatch(typeof(global::VelaUnderlandsScene), "Activate")]
    internal static class Patch_VelaUnderlands_Activate
    {
        [HarmonyPrefix] static bool Prefix(global::VelaUnderlandsScene __instance) => ScriptedScenesSync.OnVelaUnderlandsPre(__instance);
        [HarmonyPostfix] static void Postfix(global::VelaUnderlandsScene __instance) => ScriptedScenesSync.OnVelaUnderlandsPost(__instance, 0);
    }

    [HarmonyPatch(typeof(global::VelaUnderlandsScene), "AfterCutscene")]
    internal static class Patch_VelaUnderlands_AfterCutscene
    {
        [HarmonyPostfix] static void Postfix(global::VelaUnderlandsScene __instance) => ScriptedScenesSync.OnVelaUnderlandsPost(__instance, 1);
    }

    [HarmonyPatch(typeof(global::CutterTransportScript), "UnderlandsJourneyBegin")]
    internal static class Patch_Cutter_Underlands
    {
        [HarmonyPostfix] static void Postfix(global::CutterTransportScript __instance) => ScriptedScenesSync.OnCutter(__instance, 0);
    }

    [HarmonyPatch(typeof(global::CutterTransportScript), "CradleJourneyBegin")]
    internal static class Patch_Cutter_Cradle
    {
        [HarmonyPostfix] static void Postfix(global::CutterTransportScript __instance) => ScriptedScenesSync.OnCutter(__instance, 1);
    }

    [HarmonyPatch(typeof(global::CutterTransportScript), nameof(global::CutterTransportScript.ReturnToBlindingLight))]
    internal static class Patch_Cutter_ReturnBL
    {
        [HarmonyPostfix] static void Postfix(global::CutterTransportScript __instance) => ScriptedScenesSync.OnCutter(__instance, 2);
    }

    [HarmonyPatch(typeof(global::CryptWyrm), nameof(global::CryptWyrm.Activate))]
    internal static class Patch_CryptWyrm_Activate
    {
        [HarmonyPostfix] static void Postfix(global::CryptWyrm __instance) => ScriptedScenesSync.OnCryptWyrm(__instance, true);
    }

    [HarmonyPatch(typeof(global::CryptWyrm), nameof(global::CryptWyrm.Deactivate))]
    internal static class Patch_CryptWyrm_Deactivate
    {
        [HarmonyPostfix] static void Postfix(global::CryptWyrm __instance) => ScriptedScenesSync.OnCryptWyrm(__instance, false);
    }

    [HarmonyPatch(typeof(global::CrystalSkull_Master), "StartTimer")]
    internal static class Patch_CrystalMaster_Start
    {
        [HarmonyPostfix] static void Postfix(global::CrystalSkull_Master __instance) => ScriptedScenesSync.OnCrystalMaster(__instance, 0);
    }

    [HarmonyPatch(typeof(global::CrystalSkull_Master), "FailTimer")]
    internal static class Patch_CrystalMaster_Fail
    {
        [HarmonyPostfix] static void Postfix(global::CrystalSkull_Master __instance) => ScriptedScenesSync.OnCrystalMaster(__instance, 1);
    }

    [HarmonyPatch(typeof(global::CrystalSkull_Master), "ChallengeComplete")]
    internal static class Patch_CrystalMaster_Complete
    {
        [HarmonyPostfix] static void Postfix(global::CrystalSkull_Master __instance, bool fromSave)
        {
            if (!fromSave) ScriptedScenesSync.OnCrystalMaster(__instance, 2);
        }
    }

    [HarmonyPatch(typeof(global::CrystalSkull_Trig), nameof(global::CrystalSkull_Trig.SkullUsed))]
    internal static class Patch_CrystalTrig_Used
    {
        [HarmonyPostfix] static void Postfix(global::CrystalSkull_Trig __instance) => ScriptedScenesSync.OnCrystalTrig(__instance);
    }

    [HarmonyPatch(typeof(global::CrystalSkull_Door), "OpenDoor")]
    internal static class Patch_CrystalDoor_Open
    {
        [HarmonyPostfix] static void Postfix(global::CrystalSkull_Door __instance) => ScriptedScenesSync.OnCrystalDoor(__instance);
    }

    [HarmonyPatch(typeof(global::DelusionLever), nameof(global::DelusionLever.PullLever))]
    internal static class Patch_DelusionLever_Pull
    {
        [HarmonyPostfix] static void Postfix(global::DelusionLever __instance) => ScriptedScenesSync.OnLever(__instance, true);
    }

    [HarmonyPatch(typeof(global::DelusionLever), nameof(global::DelusionLever.ResetLever))]
    internal static class Patch_DelusionLever_Reset
    {
        [HarmonyPostfix] static void Postfix(global::DelusionLever __instance) => ScriptedScenesSync.OnLever(__instance, false);
    }

    [HarmonyPatch(typeof(global::TriggerWithScript), nameof(global::TriggerWithScript.Activate))]
    internal static class Patch_TriggerWithScript_Activate
    {
        [HarmonyPostfix] static void Postfix(global::TriggerWithScript __instance, bool objectsOnly, bool fromSave)
            => ScriptedScenesSync.OnTriggerScript(__instance, true, objectsOnly, fromSave);
    }

    [HarmonyPatch(typeof(global::TriggerWithScript), nameof(global::TriggerWithScript.Deactivate))]
    internal static class Patch_TriggerWithScript_Deactivate
    {
        [HarmonyPostfix] static void Postfix(global::TriggerWithScript __instance)
            => ScriptedScenesSync.OnTriggerScript(__instance, false, false, false);
    }

    [HarmonyPatch(typeof(global::ProximityTrigger), "OnTriggerEnter")]
    internal static class Patch_ProximityTrigger_Enter
    {
        [HarmonyPostfix] static void Postfix(global::ProximityTrigger __instance, Collider other)
            => ScriptedScenesSync.OnProximityTriggerEnter(__instance, other);
    }

    [HarmonyPatch(typeof(global::TriggerSendMessage), "CheckParamsAndSend")]
    internal static class Patch_TriggerSendMessage_Send
    {
        [HarmonyPrefix] static void Prefix() => DialogueSync.SuppressNpcAction++;
        [HarmonyFinalizer] static void Finalizer() { if (DialogueSync.SuppressNpcAction > 0) DialogueSync.SuppressNpcAction--; }
        [HarmonyPostfix] static void Postfix(global::TriggerSendMessage __instance)
            => ScriptedScenesSync.OnSendMessage(__instance);
    }

    [HarmonyPatch(typeof(global::PlayerCloseAnim), "OnTriggerEnter")]
    internal static class Patch_PlayerCloseAnim_Enter
    {
        [HarmonyPostfix] static void Postfix(global::PlayerCloseAnim __instance, Collider other)
            => ScriptedScenesSync.OnPlayerCloseAnim(__instance, other, true);
    }

    [HarmonyPatch(typeof(global::PlayerCloseAnim), "OnTriggerExit")]
    internal static class Patch_PlayerCloseAnim_Exit
    {
        [HarmonyPostfix] static void Postfix(global::PlayerCloseAnim __instance, Collider other)
            => ScriptedScenesSync.OnPlayerCloseAnim(__instance, other, false);
    }

    [HarmonyPatch(typeof(global::EndlessQuake), "TriggerEarthquake")]
    internal static class Patch_EndlessQuake_Trigger
    {
        [HarmonyPostfix] static void Postfix(global::EndlessQuake __instance)
            => ScriptedScenesSync.OnEndlessQuake(__instance);
    }

    [HarmonyPatch(typeof(global::LoreTrigger), nameof(global::LoreTrigger.Activation))]
    internal static class Patch_LoreTrigger_Activation
    {
        [HarmonyPostfix] static void Postfix(global::LoreTrigger __instance, bool fromSave)
            => ScriptedScenesSync.OnLoreTrigger(__instance, fromSave);
    }
}
