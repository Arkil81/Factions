using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Weapons;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Gives missile-class munitions the firer's velocity, which SE adds and then throws away.
    ///
    /// <para><b>The mechanism.</b> <c>MyGunBase.GetMissileInitialSpeed</c> does:</para>
    /// <code>
    /// initialVelocity += Vector3.ClampToSphere(vectorToTarget, 1f) * initialSpeed;
    /// if (initialVelocity.Length() &gt; desiredSpeed)
    ///     Vector3.ClampToSphere(ref initialVelocity, desiredSpeed);   // the platform's motion, gone
    /// </code>
    /// <para>Every Warfare 2 shell declares <c>MissileInitialSpeed == DesiredSpeed</c> with zero
    /// acceleration (artillery and the assault cannon 500/500/0, the railguns 2000 and 1000), so the
    /// clamp bites on every single shot and the launcher contributes exactly nothing. On a world
    /// whose large-grid limit is itself 500 m/s that makes a fleeing grid unhittable by the whole
    /// calibre family: the round leaves at the same speed the target is running at.
    /// <c>ProjectileAmmoDefinition</c> ammo — gatling, autocannon — is unaffected and untouched here;
    /// <c>MyProjectile.SetInitialVelocities</c> adds the firer's velocity with no clamp at all.</para>
    ///
    /// <para><b>All three clamps are needed.</b> Un-clamping the launch is not enough on its own:
    /// <c>MyMissile.OnAddedToScene</c> also writes <c>RigidBody.MaxLinearVelocity = DesiredSpeed</c>,
    /// and Havok pulls the round straight back down to it — <see cref="MissileVelocityCeilingPatch"/>
    /// raises that ceiling to the speed the round actually launched at. And the round's first
    /// <c>UpdateBeforeSimulation</c> runs <c>MyMissile.GetMissileSpeedNextFrame</c>, which writes any
    /// velocity above <c>DesiredSpeed</c> back down to it along the round's heading —
    /// <see cref="MissileSpeedHoldPatch"/> leaves an inherited velocity alone.</para>
    ///
    /// <para><b>Turret lead prediction comes along for free.</b>
    /// <c>MyLargeTurretTargetingSystem.GetSpatialDeviation</c> calls the same static to simulate its
    /// round's flight, so the aim solution and the round it fires stay the same object — patching
    /// one place fixes both.</para>
    ///
    /// <para><b>The client must be patched too.</b> A missile replicates as an object builder
    /// (<c>MyMultiplayer.RaiseEvent(... OnShootMissile, builder)</c>) and every client then simulates
    /// its own copy against its own definitions, so a server-only change is a server-only trajectory.
    /// <c>FactionsUI</c> carries the same three patches. FAC-222, FAC-389.</para>
    /// </summary>
    [HarmonyPatch]
    [PatchGroup("MissileFlight")]
    internal static class MissileVelocityInheritancePatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>True once the prefix has actually replaced a launch computation, so "the patch
        /// is bound" and "the patch is doing something" stay separate claims.</summary>
        internal static bool HasApplied;

        /// <summary>Whether this patch is in place. Read by the aimed-fire solution, which leads a
        /// missile round with the firer's velocity only while all three patches are.</summary>
        internal static bool Bound { get { return HarmonyPatcher.IsLive(typeof(MissileVelocityInheritancePatch)); } }

        private static bool _outcomeLogged;

        /// <summary>Resolved before Harmony binds anything. A miss disables this class only and must
        /// never throw: a throw fails the whole MissileFlight group (<c>HarmonyPatcher</c>).</summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile velocity inheritance: launch patch bound.");
                else
                    Log.Error("Missile velocity inheritance: MyGunBase.GetMissileInitialSpeed"
                            + "(Vector3, Vector3, float, float) did not resolve, so missile-class rounds keep "
                            + "leaving the barrel at their own DesiredSpeed with the firer's motion clamped "
                            + "away. Gatling/autocannon ammo is unaffected either way. Nothing else changes.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyGunBase), "GetMissileInitialSpeed",
                new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });
        }

        /// <summary>
        /// Replaces the vanilla body. The muzzle contribution is capped exactly as vanilla caps it —
        /// so a stationary firer is bit-for-bit unchanged — and the platform's own velocity is then
        /// added on top instead of being clamped out of existence.
        ///
        /// <para><paramref name="vectorToTarget"/> is not always normalised: <c>MyGunBase.AddMissile</c>
        /// passes a unit direction while the turret targeting system passes a raw
        /// target-minus-origin difference. <c>ClampToSphere(v, 1f)</c> is kept rather than
        /// <c>Normalize()</c> because it is what vanilla does with both, including the sub-metre case
        /// where it leaves the vector short.</para>
        /// </summary>
        [HarmonyPrefix]
        private static bool Prefix(Vector3 initialVelocity, Vector3 vectorToTarget,
                                   float initialSpeed, float desiredSpeed, ref Vector3 __result)
        {
            float muzzle = initialSpeed < desiredSpeed ? initialSpeed : desiredSpeed;
            __result = initialVelocity + Vector3.ClampToSphere(vectorToTarget, 1f) * muzzle;
            HasApplied = true;
            return false;
        }
    }

    /// <summary>
    /// Raises the Havok ceiling a launched missile carries, so the velocity
    /// <see cref="MissileVelocityInheritancePatch"/> gives it survives the first physics step.
    ///
    /// <para><c>MyMissile.OnAddedToScene</c> writes
    /// <c>Physics.RigidBody.MaxLinearVelocity = m_missileAmmoDefinition.DesiredSpeed</c> right after
    /// <c>Start()</c> has set the launch velocity. Left alone that is the whole cap this ticket is
    /// about: the round is clamped to its own definition speed in world space no matter how fast the
    /// launcher was moving.</para>
    ///
    /// <para>The new ceiling is the round's actual launch speed, never lower than the definition's —
    /// so a shot fired from a stationary grid keeps exactly the ceiling vanilla gave it, and only a
    /// round that genuinely inherited motion gets room for it.</para>
    /// </summary>
    [HarmonyPatch]
    [PatchGroup("MissileFlight")]
    internal static class MissileVelocityCeilingPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>True once a launch has actually needed a raised ceiling — that is, a round was
        /// fired from a moving platform. Distinct from the patch being bound.</summary>
        internal static bool HasRaised;

        /// <summary>Whether this patch is in place. Read by the aimed-fire solution, which leads a
        /// missile round with the firer's velocity only while all three patches are.</summary>
        internal static bool Bound { get { return HarmonyPatcher.IsLive(typeof(MissileVelocityCeilingPatch)); } }

        private static bool _outcomeLogged;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile velocity inheritance: Havok ceiling patch bound.");
                else
                    Log.Error("Missile velocity inheritance: MyMissile.OnAddedToScene(object) did not "
                            + "resolve, so every missile keeps a rigid-body ceiling of its own DesiredSpeed. "
                            + "Any velocity the launch patch adds is clamped back off on the first physics "
                            + "step, which leaves that patch inert rather than wrong.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyMissile), "OnAddedToScene", new[] { typeof(object) });
        }

        /// <summary>Runs after vanilla has set the ceiling, so this is the last word on it.
        /// <c>LinearVelocity</c> is the missile's own field, set from the replicated object builder,
        /// and is what <c>UpdateBeforeSimulation</c> writes onto the body every frame — reading the
        /// body back instead would race the first solve.</summary>
        [HarmonyPostfix]
        private static void Postfix(MyMissile __instance)
        {
            if (__instance == null || __instance.Physics == null) return;
            var body = __instance.Physics.RigidBody;
            if (body == null) return;

            float launchSpeed = __instance.LinearVelocity.Length();
            if (launchSpeed <= body.MaxLinearVelocity) return;

            body.MaxLinearVelocity = launchSpeed;
            HasRaised = true;
        }
    }

    /// <summary>
    /// Stops a missile's per-frame speed rule from undoing the velocity it inherited at launch.
    ///
    /// <para><c>MyMissile.GetMissileSpeedNextFrame</c> accelerates a round below its
    /// <c>DesiredSpeed</c>, and rewrites a round above it to exactly <c>DesiredSpeed</c>: the component
    /// across the heading is kept and the component along it becomes
    /// <c>Sqrt(desired² - lateral²)</c>. <c>MyMissile.UpdateBeforeSimulation</c> calls it until it
    /// reports <c>reachedMaxSpeed</c>, which is false on every new missile, so a round launched at
    /// 600 m/s by a 500 m/s grid is set to 200 m/s on its first frame and the grid runs into it. A
    /// round fired sideways with more lateral speed than <c>DesiredSpeed</c> takes the square root of
    /// a negative number.</para>
    ///
    /// <para>A round already faster than <c>DesiredSpeed</c> is returned unchanged with
    /// <c>reachedMaxSpeed</c> set, so it coasts at its launch velocity. Only a launch above
    /// <c>DesiredSpeed</c> reaches that case — vanilla never feeds this method a velocity it did not
    /// already bring under the ceiling — so a round from a stationary or slow grid runs the vanilla
    /// body and accelerates exactly as before. An accelerating round (the vanilla rocket) fired from
    /// a fast grid gives up the rest of its spin-up, and leaves at its muzzle speed relative to the
    /// firer.</para>
    ///
    /// <para><c>MyLargeTurretTargetingSystem.GetSpatialDeviation</c> steps its lead prediction through
    /// the same static, so aim and round stay consistent.</para>
    ///
    /// <para>Inside a missile's own update with a <see cref="MissileFirerFrame"/> open, the step runs
    /// vanilla's rule on the velocity relative to the firer's launch frame instead, and the round
    /// accelerates the way it would from a grid at rest. The hold above is what everything else gets:
    /// turret lead prediction, and a round whose firer was at rest.</para>
    /// </summary>
    [HarmonyPatch]
    [PatchGroup("MissileFlight")]
    internal static class MissileSpeedHoldPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>Whether this patch is in place. Read by the aimed-fire solution, which leads a
        /// missile round with the firer's velocity only while all three patches are.</summary>
        internal static bool Bound { get { return HarmonyPatcher.IsLive(typeof(MissileSpeedHoldPatch)); } }

        private static bool _outcomeLogged;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile velocity inheritance: per-frame speed patch bound.");
                else
                    Log.Error("Missile velocity inheritance: MyMissile.GetMissileSpeedNextFrame"
                            + "(Vector3, Vector3, float, float, bool, out bool) did not resolve, so every "
                            + "missile is cut back to its own DesiredSpeed on its first frame and the firer's "
                            + "velocity is lost after launch.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyMissile), "GetMissileSpeedNextFrame",
                new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(float), typeof(bool),
                        typeof(bool).MakeByRefType() });
        }

        [HarmonyPrefix]
        private static bool Prefix(Vector3 currentSpeed, Vector3 forwardVector, float desiredSpeed,
                                   float missileAcceleration, bool skipAcceleration,
                                   ref bool reachedMaxSpeed, ref Vector3 __result)
        {
            if (MissileFirerFrame.TryStep(currentSpeed, forwardVector, desiredSpeed, missileAcceleration,
                                          skipAcceleration, out reachedMaxSpeed, out __result))
                return false;

            if (currentSpeed.LengthSquared() <= desiredSpeed * desiredSpeed) return true;

            reachedMaxSpeed = true;
            __result = currentSpeed;
            return false;
        }
    }
}
