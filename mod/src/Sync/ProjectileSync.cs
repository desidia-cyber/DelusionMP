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
    /// Cosmetic replication of player-fired projectiles (arrows, clockwork bullets, magic
    /// missiles, throwables, and spell explosions).
    ///
    /// Damage is still authoritative on the shooter/target owner (EnemySync); this channel
    /// ensures the partner sees every arrow, fireball, flechette, bomb, and blast fly from
    /// the partner's hands/bow with the matching visual model, trail, and particles, while
    /// stripping all damage colliders so a cosmetic copy can never double-hit.
    /// </summary>
    public static class ProjectileSync
    {
        private const byte KindArrow = 0, KindBullet = 1, KindMissile = 2, KindThrowable = 3, KindExplosion = 4;
        private const byte KindEnemyArrow = 5, KindEnemyMissile = 6;

        private sealed class Flight
        {
            public GameObject Go;
            public Vector3 Vel;
            public float Gravity;
            public float ExpireAt;
        }

        private static readonly List<Flight> _flights = new List<Flight>(32);
        private static int _sent, _spawned;
        public static string Stats => $"sent={_sent} spawned={_spawned} active={_flights.Count}";

        private static bool CanSend()
        {
            var s = Session.Instance;
            return s != null && s.Connected && !LoopGuard.ApplyingRemote && GameRefs.InWorld;
        }

        private static bool IsPlayerPool(Component pool, out global::PlayerAnimations pan)
        {
            pan = null;
            if (pool == null) return false;
            try
            {
                var p = GameRefs.Player;
                if (p == null) return false;
                if (!pool.transform.IsChildOf(p.transform)) return false;
                pan = p.GetComponentInChildren<global::PlayerAnimations>(true);
                return pan != null;
            }
            catch { return false; }
        }

        private static void AimOrigin(out Vector3 pos, out Vector3 dir)
        {
            var cam = GameRefs.PlayerCamera ?? Camera.main;
            if (cam != null)
            {
                pos = cam.transform.position + cam.transform.forward * 0.6f;
                dir = cam.transform.forward;
                return;
            }
            var p = GameRefs.Player;
            pos = p != null ? p.transform.position + Vector3.up * 1.5f : Vector3.zero;
            dir = p != null ? p.transform.forward : Vector3.forward;
        }

        internal static void OnArrowFired(global::ArrowPool pool, global::ItemRarity rarity)
        {
            if (!CanSend() || !IsPlayerPool(pool, out var pan)) return;
            byte kind = (pan.bulletPool != null && pool.Pointer == pan.bulletPool.Pointer) ? KindBullet : KindArrow;
            AimOrigin(out var pos, out var dir);
            float speed = kind == KindBullet ? 55f : 36f;
            Send(kind, (byte)(int)rarity, pos, dir, speed);
        }

        internal static void OnMissileFired(global::MissilePool pool)
        {
            if (!CanSend() || !IsPlayerPool(pool, out var pan)) return;
            byte sub = 0;
            if (pan.lightningPool != null && pool.Pointer == pan.lightningPool.Pointer) sub = 1;
            else if (pan.flechettePool != null && pool.Pointer == pan.flechettePool.Pointer) sub = 2;
            else if (pan.poisonFlechettePool != null && pool.Pointer == pan.poisonFlechettePool.Pointer) sub = 3;
            else if (pan.ultimaPool != null && pool.Pointer == pan.ultimaPool.Pointer) sub = 4;
            else if (pan.charmPool != null && pool.Pointer == pan.charmPool.Pointer) sub = 5;
            AimOrigin(out var pos, out var dir);
            float speed = 22f;
            try
            {
                if (pool.missilePool != null && pool.missilePool.Length > 0 && pool.missilePool[0] != null)
                    speed = Mathf.Max(pool.missilePool[0].missileSpeed, 12f);
            }
            catch { }
            Send(KindMissile, sub, pos, dir, speed);
            PlayerSync.SendPlayerAction(2, sub);
        }

        internal static void OnThrowableFired(global::ThrowablePool pool, int which)
        {
            if (!CanSend() || !IsPlayerPool(pool, out _)) return;
            AimOrigin(out var pos, out var dir);
            Send(KindThrowable, (byte)Mathf.Clamp(which, 0, 15), pos, dir, 18f);
            PlayerSync.SendPlayerAction(5, (byte)Mathf.Clamp(which, 0, 15));
        }

        internal static void OnExplosionTriggered(global::ExplosionPool pool)
        {
            if (!CanSend() || !IsPlayerPool(pool, out _)) return;
            AimOrigin(out var pos, out var dir);
            try
            {
                if (pool.defaultPosition != null)
                    pos = pool.defaultPosition.position;
                else if (pool.explosionPool != null && pool.explosionPool.Length > 0)
                {
                    // Pick the most recently activated explosion in the pool if present.
                    for (int i = 0; i < pool.explosionPool.Length; i++)
                    {
                        var ex = pool.explosionPool[i];
                        if (ex != null && ex.gameObject.activeInHierarchy)
                        {
                            pos = ex.transform.position;
                            break;
                        }
                    }
                }
            }
            catch { }
            Send(KindExplosion, 0, pos, dir, 0f);
        }

        internal static void OnEnemyArrowFired(global::CombatantRangedPool pool, GameObject target, global::ItemRarity rarity)
        {
            if (!CanSend() || pool == null) return;
            var c = pool.myController;
            if (c != null && !SummonSync.IsLocalSummon(c) && EnemySync.IsPuppet(c)) return;
            try
            {
                var arrow = pool.arrow;
                Vector3 pos = arrow != null ? arrow.transform.position : (pool.defaultPosition != null ? pool.defaultPosition.position : pool.transform.position + Vector3.up * 1.4f);
                Vector3 vel = arrow != null && arrow.thisRigidBody != null ? arrow.thisRigidBody.velocity : Vector3.zero;
                float speed = vel.magnitude;
                Vector3 dir;
                if (speed > 1f)
                {
                    dir = vel / speed;
                }
                else
                {
                    dir = target != null ? (target.transform.position + Vector3.up * 1.1f - pos).normalized : pool.transform.forward;
                    speed = 30f;
                }
                Send(KindEnemyArrow, (byte)(int)rarity, pos, dir, speed);
            }
            catch (Exception e) { Log.Debug("enemy arrow sync: " + e.Message); }
        }

        internal static void OnEnemyMissileFired(global::CombatantRangedPool pool, GameObject target)
        {
            if (!CanSend() || pool == null) return;
            var c = pool.myController;
            if (c != null && !SummonSync.IsLocalSummon(c) && EnemySync.IsPuppet(c)) return;
            try
            {
                var m = pool.missile;
                Vector3 pos = m != null ? m.transform.position : (pool.defaultPosition != null ? pool.defaultPosition.position : pool.transform.position + Vector3.up * 1.4f);
                Vector3 vel = m != null && m.thisRigidBody != null ? m.thisRigidBody.velocity : Vector3.zero;
                float speed = vel.magnitude;
                Vector3 dir;
                if (speed > 1f)
                {
                    dir = vel / speed;
                }
                else
                {
                    dir = target != null ? (target.transform.position + Vector3.up * 1.1f - pos).normalized : pool.transform.forward;
                    speed = m != null ? Mathf.Max(m.missileSpeed, 14f) : 20f;
                }
                byte sub = 0;
                try
                {
                    var ec = c != null ? c.GetComponent<global::EnemyCombat>() : null;
                    if (ec != null) sub = (byte)(int)ec.currentSpellType;
                }
                catch { }
                Send(KindEnemyMissile, sub, pos, dir, speed);
            }
            catch (Exception e) { Log.Debug("enemy missile sync: " + e.Message); }
        }

        private static void Send(byte kind, byte sub, Vector3 pos, Vector3 dir, float speed)
        {
            var w = new Writer();
            w.U8(kind).U8(sub);
            w.F32(pos.x).F32(pos.y).F32(pos.z);
            w.F32(dir.x).F32(dir.y).F32(dir.z);
            w.F32(speed);
            Session.Instance.SendReliable(PacketKind.Projectile, w.ToArray());
            _sent++;
        }

        public static void Apply(byte[] payload)
        {
            if (payload == null || payload.Length < 30 || !GameRefs.InWorld) return;
            var r = new Reader(payload);
            byte kind = r.U8();
            byte sub = r.U8();
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            var dir = new Vector3(r.F32(), r.F32(), r.F32());
            float speed = r.F32();
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            else dir.Normalize();

            SpawnCosmetic(kind, sub, pos, dir, speed);
        }

        private static GameObject TemplateFor(byte kind, byte sub)
        {
            var p = GameRefs.Player;
            var pan = p != null ? p.GetComponentInChildren<global::PlayerAnimations>(true) : null;
            if (pan == null) return null;
            try
            {
                switch (kind)
                {
                    case KindArrow:
                    case KindEnemyArrow:
                        return pan.arrowPool?.arrowPool != null && pan.arrowPool.arrowPool.Length > 0 ? pan.arrowPool.arrowPool[0]?.gameObject : null;
                    case KindBullet:
                        return pan.bulletPool?.arrowPool != null && pan.bulletPool.arrowPool.Length > 0 ? pan.bulletPool.arrowPool[0]?.gameObject : null;
                    case KindMissile:
                    case KindEnemyMissile:
                    {
                        global::MissilePool mp = sub switch
                        {
                            1 => pan.lightningPool,
                            2 => pan.flechettePool,
                            3 => pan.poisonFlechettePool,
                            4 => pan.ultimaPool,
                            5 => pan.charmPool,
                            _ => pan.fireballPool,
                        };
                        return mp?.missilePool != null && mp.missilePool.Length > 0 ? mp.missilePool[0]?.gameObject : null;
                    }
                    case KindThrowable:
                        return pan.thowablePool?.ThrownItems != null && sub < pan.thowablePool.ThrownItems.Length
                            ? pan.thowablePool.ThrownItems[sub] : null;
                    case KindExplosion:
                        return pan.slowExplosionPool?.explosionPool != null && pan.slowExplosionPool.explosionPool.Length > 0
                            ? pan.slowExplosionPool.explosionPool[0]?.gameObject : null;
                }
            }
            catch { }
            return null;
        }

        private static void SpawnCosmetic(byte kind, byte sub, Vector3 pos, Vector3 dir, float speed)
        {
            try
            {
                GameObject go = null;
                var src = TemplateFor(kind, sub);
                if (src != null)
                {
                    var holder = new GameObject("DelusionMP_ProjHolder");
                    holder.SetActive(false);
                    go = UnityEngine.Object.Instantiate(src, holder.transform);
                    go.name = $"DelusionMP_Proj_{kind}_{sub}";
                    foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                        if (mb != null) UnityEngine.Object.DestroyImmediate(mb);
                    foreach (var c in go.GetComponentsInChildren<Collider>(true))
                        if (c != null) UnityEngine.Object.DestroyImmediate(c);
                    foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                        if (rb != null) UnityEngine.Object.DestroyImmediate(rb);
                    go.transform.SetParent(null, false);
                    go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up));
                    go.SetActive(true);
                    UnityEngine.Object.Destroy(holder);
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = $"DelusionMP_ProjFallback_{kind}";
                    var col = go.GetComponent<Collider>();
                    if (col != null) UnityEngine.Object.Destroy(col);
                    go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up));
                    go.transform.localScale = Vector3.one * (kind == KindExplosion ? 1.2f : 0.14f);
                }

                float gravity = (kind == KindArrow || kind == KindEnemyArrow || kind == KindThrowable) ? 9.81f : 0f;
                float life = kind == KindExplosion ? 1.5f : 3.5f;
                _flights.Add(new Flight
                {
                    Go = go,
                    Vel = dir * speed,
                    Gravity = gravity,
                    ExpireAt = Time.realtimeSinceStartup + life,
                });
                _spawned++;
            }
            catch (Exception e) { Log.Debug("projectile spawn: " + e.Message); }
        }

        public static void Tick()
        {
            if (_flights.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            float dt = Time.deltaTime;
            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];
                if (f.Go == null || now >= f.ExpireAt)
                {
                    if (f.Go != null) UnityEngine.Object.Destroy(f.Go);
                    _flights.RemoveAt(i);
                    continue;
                }
                if (f.Vel.sqrMagnitude > 1e-4f)
                {
                    var cur = f.Go.transform.position;
                    f.Vel.y -= f.Gravity * dt;
                    var step = f.Vel * dt;
                    float dist = step.magnitude;
                    if (dist > 0.01f && Physics.Raycast(cur, step / dist, dist, ~0, QueryTriggerInteraction.Ignore))
                    {
                        UnityEngine.Object.Destroy(f.Go);
                        _flights.RemoveAt(i);
                        continue;
                    }
                    f.Go.transform.position = cur + step;
                    if (f.Vel.sqrMagnitude > 0.01f)
                        f.Go.transform.rotation = Quaternion.LookRotation(f.Vel.normalized, Vector3.up);
                }
            }
        }
    }

    [HarmonyPatch(typeof(global::ArrowPool), nameof(global::ArrowPool.SelectNextArrow))]
    internal static class Patch_ArrowPool_SelectNextArrow
    {
        [HarmonyPostfix]
        static void Postfix(global::ArrowPool __instance, global::ItemRarity rarity) => ProjectileSync.OnArrowFired(__instance, rarity);
    }

    [HarmonyPatch(typeof(global::MissilePool), nameof(global::MissilePool.SelectNextMissile))]
    internal static class Patch_MissilePool_SelectNextMissile
    {
        [HarmonyPostfix]
        static void Postfix(global::MissilePool __instance) => ProjectileSync.OnMissileFired(__instance);
    }

    [HarmonyPatch(typeof(global::ThrowablePool), nameof(global::ThrowablePool.Throw))]
    internal static class Patch_ThrowablePool_Throw
    {
        [HarmonyPostfix]
        static void Postfix(global::ThrowablePool __instance, int which) => ProjectileSync.OnThrowableFired(__instance, which);
    }

    [HarmonyPatch(typeof(global::ExplosionPool), nameof(global::ExplosionPool.SelectNextExplosion))]
    internal static class Patch_ExplosionPool_SelectNextExplosion
    {
        [HarmonyPostfix]
        static void Postfix(global::ExplosionPool __instance) => ProjectileSync.OnExplosionTriggered(__instance);
    }

    [HarmonyPatch(typeof(global::CombatantRangedPool), nameof(global::CombatantRangedPool.ShootArrow))]
    internal static class Patch_CombatantRangedPool_ShootArrow
    {
        [HarmonyPostfix]
        static void Postfix(global::CombatantRangedPool __instance, GameObject newTarget, global::ItemRarity rarity)
            => ProjectileSync.OnEnemyArrowFired(__instance, newTarget, rarity);
    }

    [HarmonyPatch(typeof(global::CombatantRangedPool), nameof(global::CombatantRangedPool.ShootMissile))]
    internal static class Patch_CombatantRangedPool_ShootMissile
    {
        [HarmonyPostfix]
        static void Postfix(global::CombatantRangedPool __instance, GameObject newTarget)
            => ProjectileSync.OnEnemyMissileFired(__instance, newTarget);
    }
}
