using HarmonyLib;
using Sandbox.Game.SessionComponents;
using SpaceEngineers.Game.World;

namespace Factions
{
    /// <summary>
    /// Withholds the economy datapad SE drops into a respawn ship's cockpit, so an arrival carries one
    /// datapad — the one <see cref="RespawnPlacement"/> issues — and not two naming different stations.
    ///
    /// Vanilla fills it inside <c>MySpaceRespawnComponent.PutPlayerInRespawnGrid</c> through
    /// <c>MySessionComponentEconomy.TryFillStoryDatapad</c>, and adds the item only when that returns
    /// true. The fill is suppressed only while the seating runs, so the loot, container and store
    /// datapads that go through the same method are untouched.
    ///
    /// Two classes, because <see cref="HarmonyPatcher"/> applies and fails per class: either one
    /// failing leaves the other inert and vanilla's datapad in place, never the placement patches
    /// broken.
    /// </summary>
    [HarmonyPatch(typeof(MySpaceRespawnComponent), "PutPlayerInRespawnGrid")]
    internal static class RespawnSeatingPatch
    {
        [HarmonyPrefix]
        private static void Before()
        {
            if (!RespawnPlacement.Active) return;
            RespawnPlacement placement = RespawnPlacement.Instance;
            if (placement != null) placement.BeginSeating();
        }

        // Not gated on SimulationGate: a gate that flips during the seating must still close the
        // window, or every loot datapad after it would be withheld. Closing an unopened one is a no-op.
        [HarmonyFinalizer]
        private static void After()
        {
            RespawnPlacement placement = RespawnPlacement.Instance;
            if (placement != null) placement.EndSeating();
        }
    }

    [HarmonyPatch(typeof(MySessionComponentEconomy), "TryFillStoryDatapad")]
    internal static class RespawnVanillaDatapadPatch
    {
        [HarmonyPrefix]
        private static bool Before(ref bool __result)
        {
            if (!RespawnPlacement.Active) return true;
            RespawnPlacement placement = RespawnPlacement.Instance;
            if (placement == null || !placement.WithholdVanillaDatapad()) return true;
            __result = false;
            return false;
        }
    }
}
