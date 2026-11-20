using HarmonyLib;
using Sandbox.Game.Contracts;
using Sandbox.Game.Screens.Helpers;

namespace Factions
{
    /// <summary>
    /// Appends the Reinforce-via-Repair note to the tracked GPS waypoint for contracts hooked by
    /// <see cref="FactionContractStrategy"/>. <see cref="MyContractRepair.PrepareGPS"/> is private and
    /// non-virtual and rebuilds a fixed-text <c>MyGps</c> from scratch on every activation, so there is
    /// no per-instance field to set at generation time -- this postfix is the only way to surface the
    /// effect in the contract's own in-game text. Every station in this deployment is Factions-managed,
    /// so patching this private vanilla method carries no risk to an unmanaged NPC population (same
    /// category of patch as <see cref="EconomyTickPatches"/>, which already patches a private vanilla
    /// method universally).
    /// </summary>
    [HarmonyPatch(typeof(MyContractRepair), "PrepareGPS")]
    static class ContractRepairGpsPatch
    {
        private const string ReinforcementNote = "Completing this repair also reinforces the station.";

        [HarmonyPostfix]
        static void Postfix(MyContractRepair __instance, ref MyGps __result)
        {
            if (SimulationGate.Deferred) return;
            if (__result == null) return;
            if (!FactionContractStrategy.IsHookedRepairContract(__instance.Id)) return;
            __result.Description = __result.Description + "\n" + ReinforcementNote;
        }
    }
}
