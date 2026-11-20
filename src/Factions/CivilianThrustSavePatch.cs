using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using VRage.Game;

namespace Factions
{
    /// <summary>Serialize the authored override while an experimental civilian controller owns
    /// the block. SE otherwise saves the current burn into both worlds and copied blueprints.
    /// Changes only the builder, not the live thrust; dictionary reads are snapshot-thread safe.</summary>
    [HarmonyPatch(typeof(MyThrust), nameof(MyThrust.GetObjectBuilderCubeBlock))]
    internal static class CivilianThrustSavePatch
    {
        private static void Postfix(MyThrust __instance, MyObjectBuilder_CubeBlock __result)
        {
            float fraction;
            MyObjectBuilder_Thrust builder = __result as MyObjectBuilder_Thrust;
            if (builder != null && FleetFlightController.OriginalCivilianOverrides.TryGetValue(__instance.EntityId, out fraction))
                builder.ThrustOverride = fraction * __instance.ThrustForceLength;
        }
    }
}
