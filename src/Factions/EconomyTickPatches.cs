using HarmonyLib;
using Sandbox.Game.SessionComponents;

namespace Factions
{
    /// <summary>
    /// Prevents SE's economy tick from overwriting Factions-managed station store lists with vanilla
    /// prices and items.
    ///
    /// <para><c>MySessionComponentEconomy.UpdateStations()</c> fires every
    /// <c>EconomyTickInSeconds</c> (default 600 s) and calls
    /// <c>MyFactionTypeBaseStrategy.UpdateStationsStoreItems()</c> for each NPC faction, which
    /// overwrites the store with vanilla pricing and appends extra items we don't control.  Our GPS
    /// refresh (~5 s) normally restores brain prices within 5 s, leaving a brief vanilla-price
    /// exposure window.  This postfix eliminates that window by clearing and re-populating managed
    /// stations immediately after SE finishes, on the same game-thread call.</para>
    /// </summary>
    [HarmonyPatch(typeof(MySessionComponentEconomy), "UpdateStations")]  // not in public API surface; string required
    static class EconomyTickPatches
    {
        [HarmonyPostfix]
        static void UpdateStations_Postfix()
        {
            if (SimulationGate.Deferred) return;
            // A sliced pass calls it at its end (EconomyTickSlicer.Finish).
            if (EconomyTickSlicer.Active) return;
            FactionsPlugin.Instance?.OnEconomyTickCompleted();
        }
    }
}
