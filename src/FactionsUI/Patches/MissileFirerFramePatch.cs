using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Weapons;
using VRageMath;

namespace FactionsUI.Patches;

/// <summary>
/// The client half of the server's missile firer frame (FAC-391).
///
/// <para>A missile fired from a moving grid accelerates and measures its range relative to the
/// firer's launch velocity, not the world. The launch velocity is recovered from the replicated
/// builder's velocity minus heading × <c>min(MissileInitialSpeed, DesiredSpeed)</c>, the same
/// arithmetic the server does, so both ends fly the same trajectory. The server-side reasoning is in
/// <c>Factions/MissileFirerFramePatches.cs</c>.</para>
/// </summary>
internal static class MissileFirerFrame
{
    private sealed class Frame
    {
        public Vector3 Platform;
    }

    private static readonly ConditionalWeakTable<MyMissile, Frame> Frames = new();

    [ThreadStatic] private static Frame _current;

    internal static void Begin(MyMissile missile)
    {
        if (missile?.AmmoDefinition is not MyMissileAmmoDefinition def) return;
        // Seeking and momentum-preserving rounds are steered by SE 1.211 itself; the server leaves them unframed
        // too (Factions MissileFirerFrame.SteersItself), so the drawn round matches.
        if (def.CanSeek || def.MissilePreserveLaunchMomentum) return;

        float muzzle = Math.Min(def.MissileInitialSpeed, def.DesiredSpeed);
        Vector3 forward = (Vector3)missile.WorldMatrix.Forward;
        Vector3 platform = missile.LinearVelocity - forward * muzzle;
        if (platform.LengthSquared() < 0.01f) return;

        Frames.Remove(missile);
        Frames.Add(missile, new Frame { Platform = platform });

        var body = missile.Physics?.RigidBody;
        if (body != null)
        {
            float ceiling = platform.Length() + def.DesiredSpeed;
            if (ceiling > body.MaxLinearVelocity) body.MaxLinearVelocity = ceiling;
        }
    }

    internal static void Enter(MyMissile missile)
    {
        if (missile == null || !Frames.TryGetValue(missile, out Frame frame))
        {
            _current = null;
            return;
        }
        _current = frame;
        missile.Origin += (Vector3D)(frame.Platform * (1f / 60f));
    }

    internal static void Exit() => _current = null;

    /// <summary>Points a framed missile along its velocity relative to the frame, after vanilla has
    /// pointed it along its world displacement (FAC-392).</summary>
    internal static void Orient(MyMissile missile)
    {
        if (missile == null || !Frames.TryGetValue(missile, out Frame frame)) return;

        Vector3 rel = missile.LinearVelocity - frame.Platform;
        if (rel.LengthSquared() < 1f) return;

        MatrixD worldMatrix = MatrixD.CreateFromDir(Vector3D.Normalize((Vector3D)rel));
        worldMatrix.Translation = missile.PositionComp.GetPosition();
        missile.PositionComp.SetWorldMatrix(ref worldMatrix);
    }

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

[HarmonyPatch]
[Factions.PatchGroup("MissileFlight")]
internal static class MissileFirerFrameStartPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile firer frame: start patch bound"
            : "missile firer frame: MyMissile.OnAddedToScene did not resolve -- rounds fired from a moving "
              + "grid will be drawn expiring short of where the server flies them.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyMissile), "OnAddedToScene", new[] { typeof(object) });
    }

    [HarmonyPostfix]
    // ReSharper disable once UnusedMember.Local
    private static void Postfix(MyMissile __instance) => MissileFirerFrame.Begin(__instance);
}

[HarmonyPatch]
[Factions.PatchGroup("MissileFlight")]
internal static class MissileFirerFrameUpdatePatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile firer frame: update patch bound"
            : "missile firer frame: MyMissile.UpdateBeforeSimulation did not resolve -- rounds fired from a "
              + "moving grid will be drawn expiring short of where the server flies them.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyMissile), "UpdateBeforeSimulation", Type.EmptyTypes);
    }

    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Local
    private static void Prefix(MyMissile __instance) => MissileFirerFrame.Enter(__instance);

    [HarmonyFinalizer]
    // ReSharper disable once UnusedMember.Local
    private static Exception Finalizer(Exception __exception)
    {
        MissileFirerFrame.Exit();
        return __exception;
    }
}

[HarmonyPatch]
[Factions.PatchGroup("MissileFlight")]
internal static class MissileFirerFrameOrientPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile firer frame: orientation patch bound"
            : "missile firer frame: MyMissile.UpdateAfterSimulation10 did not resolve -- a missile fired "
              + "against its firer's travel will turn to face its world motion.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyMissile), "UpdateAfterSimulation10", Type.EmptyTypes);
    }

    [HarmonyPostfix]
    // ReSharper disable once UnusedMember.Local
    private static void Postfix(MyMissile __instance) => MissileFirerFrame.Orient(__instance);
}
