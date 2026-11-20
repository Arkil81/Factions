using System;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// Counts missiles a station's safe zone destroys, so rocket-class weapons can besiege a zone
    /// the same way projectile-class ones do.
    ///
    /// <para><b>Why a patch is needed for this class and not for the other.</b> The two ammo classes
    /// are absorbed by completely different code. A <c>MyProjectile</c> is a raycast struct: it flies
    /// through the zone, hits the grid, and has its damage zeroed — the hit still happens, so
    /// <see cref="PlayerSiegeManager"/>'s public hit interceptor sees it. A missile is a real entity:
    /// <c>MySafeZone.InsertEntityInternal</c> marks it for destruction the moment it crosses the
    /// boundary, so it never reaches a grid and never raises a hit at all. Without this half, every
    /// rocket, artillery shell and railgun slug in the game would silently fail to besiege anything,
    /// while a gatling worked fine — the worst kind of gap, because it looks like the feature works.</para>
    ///
    /// <para><b>Hand-applied and wrapped, deliberately.</b> <c>InsertEntityInternal</c> is private.
    /// Same shape as <c>WeaponFireTelemetry.TryPatch</c>: if the bind fails, sieges by missile stop
    /// counting and everything else carries on.</para>
    /// </summary>
    internal static class SafeZoneMissilePatches
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Set by <see cref="PlayerSiegeManager"/>. Null means nothing is listening, which
        /// is the state before a session loads and after it unloads.
        /// <para>Arguments: the safe zone's entity id, the attacker's identity id, the launching
        /// block's entity id (0 when unknown), and the damage the round would have done.</para></summary>
        internal static Action<long, long, long, double> Sink;

        /// <summary>True once the prefix has actually observed a missile, so "the patch bound" and
        /// "the patch runs" are separable when reading a log.</summary>
        internal static bool Observed;

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MySafeZone), "InsertEntityInternal");
                if (target == null)
                {
                    Log.Info("SafeZoneMissilePatches: MySafeZone.InsertEntityInternal not found; "
                           + "missiles will not count toward a siege.");
                    return;
                }
                var prefix = new HarmonyMethod(typeof(SafeZoneMissilePatches)
                    .GetMethod("OnInsertEntity", BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(target, prefix: prefix);
                Log.Info("SafeZoneMissilePatches: bound; missiles absorbed by a safe zone count toward a siege.");
            }
            catch (Exception ex)
            {
                Log.Info("SafeZoneMissilePatches: could not patch the absorption hook: " + ex.Message);
            }
        }

        /// <summary>Observes a missile entering a safe zone, then lets SE destroy it exactly as
        /// before. Never suppresses the original: this counts the round, it does not save it.</summary>
        private static void OnInsertEntity(MySafeZone __instance, MyEntity entity)
        {
            try
            {
                if (Sink == null || __instance == null || entity == null) return;
                if (!__instance.Enabled) return;      // a down zone absorbs nothing to count

                MyMissile missile = entity as MyMissile ?? (entity.Parent as MyMissile);
                if (missile == null) return;

                var ammo = missile.AmmoDefinition as MyMissileAmmoDefinition;
                // A countermeasure flare (SE 1.211) is a missile with a health pool and no damage; it
                // decoys seekers and is no attack on the zone.
                if (ammo == null || ammo.CanDistract) return;

                // MissileHealthPool is where a Warfare 2 round keeps its grid damage;
                // MissileExplosionDamage is the older channel and is the fallback rather than the
                // first choice, which is the same split the weapon scan had to learn.
                double damage = ammo.MissileHealthPool > 0
                    ? ammo.MissileHealthPool
                    : ammo.MissileExplosionDamage;
                if (damage <= 0) return;

                // No owner and no launcher, no attacker: a meteor is not one. An NPC turret's missile
                // does carry an owner, so whether a player is behind it is the sink's question.
                if (missile.Owner == 0 && missile.LauncherId == 0) return;

                Observed = true;
                Sink(__instance.EntityId, missile.Owner, missile.LauncherId, damage);
            }
            catch
            {
                // A throwing prefix would take the safe zone's own entity-insertion path down with
                // it, which would be a far worse outcome than an uncounted rocket.
            }
        }
    }
}
