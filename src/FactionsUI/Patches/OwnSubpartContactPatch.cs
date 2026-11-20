using System.Reflection;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace FactionsUI.Patches;

/// <summary>
/// A grid's own moving parts must not push it: no predicted collision impulse from a subpart of a block on the same
/// grid, or on a grid in the same physical group.
///
/// <para><b>What this fixes.</b> A piloted grid is predicted on its pilot's client, and the client alone runs
/// <c>MyGridPhysics.PredictContactImpulse</c> on every contact manifold: it adds
/// <c>0.005 x relative speed x partner mass / grid mass</c> to the grid's velocity along the contact normal. A
/// subpart's partner mass is its grid's whole mass (<c>PredictContactMass</c>), so the push is 0.005 x relative
/// speed per contact. On a client a door's panels are <i>static</i> Havok bodies moved with the hull
/// (<c>MyDoor.CreateConstraint</c>), so their velocity reads zero and the relative speed is the ship's own speed.
/// Measured on "Fruit Bat" over Tellus (2026-10-14, <c>dampprobe trace</c>): ~10 predicted contacts a frame from a
/// CorridorRoundDoor's and a SlidingHatchDoor's panels at relSpeed ~170 m/s, and the client's velocity gaining
/// 0.29-0.30 x forward speed in m/s² that the server never had. The server corrected it back every few frames,
/// which the pilot saw as the velocity marker jumping.</para>
///
/// <para>SE's own eligibility test already disables the prediction for a grid in the same physical group
/// (<c>GetEligibilityForPredictedImpulses</c>); a subpart is not a grid, so it fell through. This applies the same
/// rule to subparts. Contacts with anything else, including other grids' subparts, are predicted as before.</para>
/// </summary>
[HarmonyPatch]
internal static class OwnSubpartContactPatch
{
    /// <summary>Predicted impulses skipped since load. Read by <see cref="NetSyncTrace"/>.</summary>
    internal static long Skipped;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        bool resolved = Target() != null;
        Plugin.Log(resolved
            ? "own-subpart contact guard bound"
            : "own-subpart contact guard: MyGridPhysics.PredictContactImpulse did not resolve -- a piloted grid's own "
              + "door panels can push it on this client and the server will keep correcting it.");
        return resolved;
    }

    [HarmonyTargetMethod]
    private static MethodBase Target()
    {
        return AccessTools.Method(typeof(MyGridPhysics), "PredictContactImpulse");
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    // ReSharper disable once UnusedMember.Local
    private static bool Prefix(MyGridPhysics __instance, IMyEntity otherEntity)
    {
        if (otherEntity is not MyEntitySubpart) return true;
        MyEntity root = (MyEntity)otherEntity;
        while (root is MyEntitySubpart && root.Parent != null) root = root.Parent;
        if (root is not MyCubeBlock block || block.CubeGrid == null) return true;
        if (__instance?.Entity is not MyCubeGrid grid) return true;
        if (block.CubeGrid != grid && !MyCubeGridGroups.Static.Physical.HasSameGroup(grid, block.CubeGrid)) return true;
        Interlocked.Increment(ref Skipped);
        return false;
    }
}
