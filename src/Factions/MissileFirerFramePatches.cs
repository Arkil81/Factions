using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Weapons;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Flies a missile fired from a moving grid in that grid's frame: its acceleration and its range
    /// are measured relative to the velocity the firer had at launch, not relative to the world.
    ///
    /// <para><b>Why.</b> With the firer's velocity inherited, vanilla's two per-frame rules both read
    /// world speed and world distance. <c>GetMissileSpeedNextFrame</c> accelerates a round until its
    /// world speed reaches <c>DesiredSpeed</c>, so from a 500 m/s grid a rocket (muzzle 100, desired 200)
    /// never accelerates at all and leaves 100 m/s ahead instead of 200. And
    /// <c>UpdateBeforeSimulation</c> detonates a round once it is <c>MaxTrajectory</c> from
    /// <c>m_origin</c>, a fixed world point, so a round flying at 600 m/s spends its 800 m in 1.3 s and
    /// self-destructs about 130 m in front of the grid that fired it.</para>
    ///
    /// <para><b>How.</b> When the missile enters the scene, the firer's launch velocity is recovered as
    /// the launch velocity minus the muzzle contribution <see cref="MissileVelocityInheritancePatch"/>
    /// added (heading × <c>min(MissileInitialSpeed, DesiredSpeed)</c>). The builder and the definition
    /// both ends share are enough for that, so client and server get the same number. Each frame,
    /// the origin advances by that velocity, so the range check measures distance travelled relative
    /// to the firer's frame. <see cref="MissileSpeedHoldPatch"/> runs vanilla's speed rule on the velocity
    /// relative to that frame. The result is that a round behaves the way it does from a stationary
    /// grid: its speed and range relative to the launch frame match vanilla's, and its lifetime is
    /// vanilla's.</para>
    ///
    /// <para>A round from a grid at rest gets no frame and runs pure vanilla. The frame is the firer's
    /// velocity at launch, held constant: a grid that accelerates after firing leaves its own round's
    /// frame behind, the way a real launch platform would.</para>
    ///
    /// <para><c>FactionsUI</c> carries the same logic, so the drawn round flies the server's
    /// trajectory. FAC-391.</para>
    /// </summary>
    internal static class MissileFirerFrame
    {
        private sealed class Frame
        {
            public Vector3 Platform;
        }

        private static readonly ConditionalWeakTable<MyMissile, Frame> Frames =
            new ConditionalWeakTable<MyMissile, Frame>();

        [ThreadStatic] private static Frame _current;

        /// <summary>Missiles given a firer frame, for <c>factions missilespeed</c>.</summary>
        internal static int Framed;

        /// <summary>Recovers the firer's launch velocity and raises the Havok ceiling to the fastest
        /// the round can go in world space. Runs after vanilla <c>OnAddedToScene</c>.</summary>
        internal static void Begin(MyMissile missile)
        {
            // Without the launch patch the launch velocity is vanilla's clamped one, and subtracting
            // the muzzle term from it does not give the firer's velocity back.
            if (missile == null || !MissileVelocityInheritancePatch.HasApplied) return;
            var def = missile.AmmoDefinition as MyMissileAmmoDefinition;
            if (def == null || SteersItself(def)) return;

            float muzzle = Math.Min(def.MissileInitialSpeed, def.DesiredSpeed);
            Vector3 forward = (Vector3)missile.WorldMatrix.Forward;
            Vector3 platform = missile.LinearVelocity - forward * muzzle;
            if (platform.LengthSquared() < 0.01f) return;

            Frames.Remove(missile);
            Frames.Add(missile, new Frame { Platform = platform });
            Framed++;

            var body = missile.Physics != null ? missile.Physics.RigidBody : null;
            if (body != null)
            {
                float ceiling = platform.Length() + def.DesiredSpeed;
                if (ceiling > body.MaxLinearVelocity) body.MaxLinearVelocity = ceiling;
            }
        }

        /// <summary>
        /// SE 1.211 (Warfare 3) steers these rounds itself: a seeking round's <c>MySeekerMissileComponent</c> turns
        /// its nose and velocity, flares and EMP scramble it, and vanilla's <c>UpdateAfterSimulation10</c> no longer
        /// reorients it while it guides or when its ammo keeps launch momentum (every Torpedo*). A firer frame would
        /// overwrite that nose each update, so such a round flies pure vanilla. <c>FactionsUI</c> applies the same
        /// test, so both ends agree on which rounds are framed.
        /// </summary>
        internal static bool SteersItself(MyMissileAmmoDefinition def)
        {
            return def.CanSeek || def.MissilePreserveLaunchMomentum;
        }

        /// <summary>Opens the missile's frame for this update and carries its origin along it.</summary>
        internal static void Enter(MyMissile missile)
        {
            Frame frame;
            if (missile == null || !Frames.TryGetValue(missile, out frame))
            {
                _current = null;
                return;
            }
            _current = frame;
            missile.Origin += (Vector3D)(frame.Platform * (1f / 60f));
        }

        internal static void Exit()
        {
            _current = null;
        }

        /// <summary>
        /// Points a framed missile along its velocity relative to the frame — the direction its thrust
        /// pushes it. Runs after vanilla <c>UpdateAfterSimulation10</c>, which points it along its world
        /// displacement: for a round fired against the firer's travel that is backwards, and the round
        /// swings round mid-flight. FAC-392.
        /// </summary>
        internal static void Orient(MyMissile missile)
        {
            Frame frame;
            if (missile == null || !Frames.TryGetValue(missile, out frame)) return;

            Vector3 rel = missile.LinearVelocity - frame.Platform;
            if (rel.LengthSquared() < 1f) return;

            MatrixD worldMatrix = MatrixD.CreateFromDir(Vector3D.Normalize((Vector3D)rel));
            worldMatrix.Translation = missile.PositionComp.GetPosition();
            missile.PositionComp.SetWorldMatrix(ref worldMatrix);
        }

        /// <summary>
        /// Vanilla's <c>GetMissileSpeedNextFrame</c>, applied to the velocity relative to the open
        /// frame. False when no frame is open, and the caller falls back.
        /// </summary>
        internal static bool TryStep(Vector3 currentSpeed, Vector3 forwardVector, float desiredSpeed,
                                     float missileAcceleration, bool skipAcceleration,
                                     out bool reachedMaxSpeed, out Vector3 result)
        {
            Frame frame = _current;
            if (frame == null)
            {
                reachedMaxSpeed = false;
                result = currentSpeed;
                return false;
            }

            Vector3 rel = currentSpeed - frame.Platform;
            Vector3 dir = rel.LengthSquared() > 1e-6f ? Vector3.Normalize(rel) : forwardVector;

            reachedMaxSpeed = true;
            if (rel.LengthSquared() < desiredSpeed * desiredSpeed)
            {
                if (skipAcceleration)
                {
                    rel = Vector3.ClampToSphere(dir, desiredSpeed);
                }
                else
                {
                    rel += dir * (missileAcceleration * (1f / 60f));
                    reachedMaxSpeed = false;
                }
            }
            if (rel.LengthSquared() > desiredSpeed * desiredSpeed)
            {
                Vector3 lateral = Vector3.ProjectOnPlane(ref rel, ref dir);
                float along = desiredSpeed * desiredSpeed - lateral.LengthSquared();
                rel = lateral + dir * (float)Math.Sqrt(along > 0f ? along : 0f);
                reachedMaxSpeed = true;
            }

            result = rel + frame.Platform;
            return true;
        }
    }

    /// <summary>Gives a launched missile its firer frame.</summary>
    [HarmonyPatch]
    [PatchGroup("MissileFlight")]
    internal static class MissileFirerFrameStartPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static bool _outcomeLogged;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile firer frame: start patch bound.");
                else
                    Log.Error("Missile firer frame: MyMissile.OnAddedToScene(object) did not resolve, so a "
                            + "missile fired from a moving grid measures its range and acceleration against "
                            + "the world and self-destructs short of its rated range.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyMissile), "OnAddedToScene", new[] { typeof(object) });
        }

        [HarmonyPostfix]
        private static void Postfix(MyMissile __instance)
        {
            MissileFirerFrame.Begin(__instance);
        }
    }

    /// <summary>Opens the missile's frame around its per-frame update.</summary>
    [HarmonyPatch]
    [PatchGroup("MissileFlight")]
    internal static class MissileFirerFrameUpdatePatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static bool _outcomeLogged;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile firer frame: update patch bound.");
                else
                    Log.Error("Missile firer frame: MyMissile.UpdateBeforeSimulation() did not resolve, so a "
                            + "missile fired from a moving grid measures its range and acceleration against "
                            + "the world and self-destructs short of its rated range.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyMissile), "UpdateBeforeSimulation", Type.EmptyTypes);
        }

        [HarmonyPrefix]
        private static void Prefix(MyMissile __instance)
        {
            MissileFirerFrame.Enter(__instance);
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            MissileFirerFrame.Exit();
            return __exception;
        }
    }

    /// <summary>Re-points a framed missile after vanilla's 10th-frame orientation update.</summary>
    [HarmonyPatch]
    [PatchGroup("MissileFlight")]
    internal static class MissileFirerFrameOrientPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static bool _outcomeLogged;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool resolved = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Missile firer frame: orientation patch bound.");
                else
                    Log.Error("Missile firer frame: MyMissile.UpdateAfterSimulation10() did not resolve, so a "
                            + "missile fired against its firer's travel turns to face its world motion.");
            }
            return resolved;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            return AccessTools.Method(typeof(MyMissile), "UpdateAfterSimulation10", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        private static void Postfix(MyMissile __instance)
        {
            MissileFirerFrame.Orient(__instance);
        }
    }
}
