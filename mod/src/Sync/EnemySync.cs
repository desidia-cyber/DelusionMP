using System;
using System.Collections.Generic;
using DelusionMP.Core;
using DelusionMP.Game;
using DelusionMP.Net;
using UnityEngine;

namespace DelusionMP.Sync
{
    /// <summary>
    /// Enemies and bosses.
    ///
    /// There is no `Boss` type in this game (verified by type search): bosses are ordinary
    /// EnemyController objects with a high CombatantLevel, wrapped in scripted scenes. So
    /// enemy replication covers bosses for free, and only the surrounding cutscene triggers
    /// need separate handling (see CutsceneSync).
    ///
    /// Ownership is per spawner. EnemySpawner (918 in the shipped assets) already implements
    /// ISaveable and owns kill state, so the host drives EnemyController AI and the client
    /// turns its local copy into a puppet.
    /// </summary>
    public static class EnemySync
    {
        private const float StateHz = 10f;
        private const float StateInterval = 1f / StateHz;
        private static float _nextState;

        private sealed class Puppet
        {
            public global::EnemyController Controller;
            public Vector3 TargetPos;
            public float TargetYaw;
            public float LastUpdate;
        }

        private static readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>(256);

        public static void Init() => _puppets.Clear();

        /// <summary>Host: broadcast engaged enemies at 10 Hz.</summary>
        public static void TickHost()
        {
            if (Session.Instance?.Role != SessionRole.Host) return;
            if (Time.realtimeSinceStartup < _nextState) return;
            _nextState = Time.realtimeSinceStartup + StateInterval;

            var reg = ObjectRegistry.Instance;
            if (reg.NeedsRebuild) reg.Rebuild(CurrentScene());

            var enemies = Resources.FindObjectsOfTypeAll<global::EnemyController>();
            if (enemies == null) return;

            var buffered = new List<(string key, global::EnemyController c)>(64);
            foreach (var e in enemies)
            {
                if (e == null || !e.gameObject.activeInHierarchy) continue;
                string key = KeyFor(reg, e);
                if (key == null) continue;
                buffered.Add((key, e));
                if (buffered.Count >= 24) { FlushBatch(buffered); buffered.Clear(); }
            }
            if (buffered.Count > 0) FlushBatch(buffered);
        }

        private static void FlushBatch(List<(string key, global::EnemyController c)> batch)
        {
            var w = new Writer();
            w.U8((byte)batch.Count);
            foreach (var (key, c) in batch)
            {
                w.Str(key);
                w.Position(c.transform.position);
                w.Yaw(c.transform.eulerAngles.y);
                int hp = 0;
                try { hp = c.myStats != null ? c.myStats.currentHealth : 0; } catch { }
                w.I32(hp);
                w.U8((byte)(int)c.combatMode);
                w.U8((byte)(int)c.hostileType);
                w.Bool(c.isRunning);
                w.Bool(c.hasBeenProvoked);
            }
            if (batch.Count > 0)
                Session.Instance.SendUnreliable(PacketKind.EnemyState, w.ToArray());
        }

        public static void Apply(byte[] payload)
        {
            var r = new Reader(payload);
            int n = r.U8();
            var reg = ObjectRegistry.Instance;

            for (int i = 0; i < n; i++)
            {
                string key = r.Str();
                Vector3 pos = r.Position();
                float yaw = r.Yaw();
                int hp = r.I32();
                var mode = (global::EnemyController.CombatModes)r.U8();
                var hostile = (global::EnemyController.HostileType)r.U8();
                bool running = r.Bool();
                bool provoked = r.Bool();

                if (!reg.TryResolve(key, out var go) || go == null) continue;
                var c = go.GetComponent<global::EnemyController>();
                if (c == null) continue;

                var p = PuppetFor(c);
                p.TargetPos = pos;
                p.TargetYaw = yaw;
                p.LastUpdate = Time.realtimeSinceStartup;

                LoopGuard.Apply(() =>
                {
                    try
                    {
                        c.hostileType = hostile;
                        c.isRunning = running;
                        c.hasBeenProvoked = provoked;
                        if (c.myStats != null) Priv.SetProp(c.myStats, "currentHealth", hp);
                    }
                    catch (Exception e) { Log.Debug($"puppet apply failed: {e.Message}"); }
                });
            }
        }

        private static Puppet PuppetFor(global::EnemyController c)
        {
            int id = c.GetInstanceID();
            if (_puppets.TryGetValue(id, out var p)) return p;
            p = new Puppet { Controller = c };
            // A puppeted enemy must not run its own AI: its NavMeshAgent would fight the
            // replicated transforms and produce the classic rubber-banding enemy.
            try
            {
                var agent = Priv.GetField<UnityEngine.AI.NavMeshAgent>(c, "agent");
                if (agent != null) agent.enabled = false;
            }
            catch { }
            _puppets[id] = p;
            return p;
        }

        /// <summary>Client: interpolate puppets toward their last replicated transform.</summary>
        public static void TickClient()
        {
            if (Session.Instance?.Role == SessionRole.Host) return;
            float now = Time.realtimeSinceStartup;
            foreach (var p in _puppets.Values)
            {
                if (p.Controller == null) continue;
                // Freeze if the host stopped talking about this enemy (out of range,
                // dead, or peer travelling) rather than extrapolating into a wall.
                if (now - p.LastUpdate > 0.5f) continue;

                var t = p.Controller.transform;
                t.position = Vector3.Lerp(t.position, p.TargetPos, 0.35f);
                var e = t.eulerAngles;
                t.rotation = Quaternion.Euler(e.x, Mathf.LerpAngle(e.y, p.TargetYaw, 0.35f), e.z);
            }
        }

        private static string CurrentScene()
        {
            try { return GameRefs.Scene?.currentSceneName ?? ""; }
            catch { return ""; }
        }

        private static string KeyFor(ObjectRegistry reg, Component c)
        {
            var saveable = c.GetComponent<global::SaveableObject>();
            if (saveable != null)
            {
                try
                {
                    var id = saveable.GetUniqueIdentifier();
                    if (!string.IsNullOrEmpty(id)) return reg.CurrentScene + "|" + id;
                }
                catch { }
            }
            return reg.CurrentScene + "|#path/" + ObjectRegistry.HierarchyPath(c.transform);
        }
    }
}
