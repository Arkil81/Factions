using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Weapons;
using VRageMath;

namespace FactionsUI.Patches;

/// <summary>
/// The client half of the server's missile velocity inheritance (FAC-222, FAC-389).
///
/// <para>A missile is not a streamed entity. The server broadcasts an object builder and every
/// client simulates its own copy against its own definitions, so all three of the server's patches
/// have to exist here or this client's rounds fly a different trajectory from the ones the server is
/// scoring hits with: the round detonates in one place on the server and somewhere behind it on
/// screen. The server-side reasoning, and what vanilla actually does, is in
/// <c>Factions/MissileVelocityPatches.cs</c>.</para>
///
/// <para><c>MyGunBase.AddMissile</c> returns early off the server, so on this end the launch patch
/// matters for <c>MyLargeTurretTargetingSystem</c>'s own aim prediction rather than for spawning
/// anything. It is applied anyway: a client predicting a lead the server does not use is the same
/// disagreement in a different place.</para>
/// </summary>
[HarmonyPatch]
[Factions.PatchGroup("MissileFlight")]
internal static class MissileInitialVelocityPatch
{
    /// <summary>Resolved before Harmony binds anything. A miss disables this class only and must
    /// never throw: a throw fails the whole MissileFlight group (<c>HarmonyPatcher</c>).</summary>
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile velocity inheritance: launch patch bound"
            : "missile velocity inheritance: MyGunBase.GetMissileInitialSpeed did not resolve -- this "
              + "client will predict missile leads the server does not use.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyGunBase), "GetMissileInitialSpeed",
            new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });
    }

    /// <summary>Caps the muzzle contribution exactly as vanilla caps it — so a stationary firer is
    /// unchanged — and adds the platform's own velocity on top instead of clamping it away.
    /// <c>ClampToSphere(v, 1f)</c> rather than <c>Normalize()</c> because the turret targeting
    /// system passes a raw target-minus-origin difference, and this is what vanilla does with
    /// both.</summary>
    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Local
    private static bool Prefix(Vector3 initialVelocity, Vector3 vectorToTarget,
                               float initialSpeed, float desiredSpeed, ref Vector3 __result)
    {
        float muzzle = initialSpeed < desiredSpeed ? initialSpeed : desiredSpeed;
        __result = initialVelocity + Vector3.ClampToSphere(vectorToTarget, 1f) * muzzle;
        return false;
    }
}

/// <summary>
/// Raises the Havok ceiling on a replicated missile so the velocity it arrived with survives the
/// first physics step on this client.
///
/// <para><c>MyMissile.OnAddedToScene</c> writes
/// <c>RigidBody.MaxLinearVelocity = m_missileAmmoDefinition.DesiredSpeed</c>. Without this the
/// object builder's inherited velocity is clamped straight back off here while the server's copy
/// keeps it, which is the visible half of the desync.</para>
/// </summary>
[HarmonyPatch]
[Factions.PatchGroup("MissileFlight")]
internal static class MissileVelocityCeilingPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile velocity inheritance: Havok ceiling patch bound"
            : "missile velocity inheritance: MyMissile.OnAddedToScene did not resolve -- rounds fired "
              + "from a moving grid will render slower here than the server flies them.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyMissile), "OnAddedToScene", new[] { typeof(object) });
    }

    [HarmonyPostfix]
    // ReSharper disable once UnusedMember.Local
    private static void Postfix(MyMissile __instance)
    {
        if (__instance?.Physics?.RigidBody == null) return;

        float launchSpeed = __instance.LinearVelocity.Length();
        if (launchSpeed > __instance.Physics.RigidBody.MaxLinearVelocity)
            __instance.Physics.RigidBody.MaxLinearVelocity = launchSpeed;
    }
}

/// <summary>
/// Keeps this client's copy of a missile at its inherited velocity past the first frame, matching
/// the server.
///
/// <para><c>MyMissile.GetMissileSpeedNextFrame</c> rewrites a round above its <c>DesiredSpeed</c> back
/// down to it on the round's first <c>UpdateBeforeSimulation</c>. A round faster than
/// <c>DesiredSpeed</c> is returned unchanged with <c>reachedMaxSpeed</c> set; slower rounds run the
/// vanilla body. Inside a missile's own update with a <see cref="MissileFirerFrame"/> open, vanilla's
/// rule runs on the velocity relative to the firer's launch frame instead. The server-side reasoning
/// is in <c>Factions/MissileVelocityPatches.cs</c>.</para>
/// </summary>
[HarmonyPatch]
[Factions.PatchGroup("MissileFlight")]
internal static class MissileSpeedHoldPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "missile velocity inheritance: per-frame speed patch bound"
            : "missile velocity inheritance: MyMissile.GetMissileSpeedNextFrame did not resolve -- rounds "
              + "fired from a moving grid will slow to their own speed here while the server flies them faster.");
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
    // ReSharper disable once UnusedMember.Local
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
