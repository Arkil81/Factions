using HarmonyLib;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Skips vanilla's per-station contract pass entirely for unspawned Factions-managed stations.
    ///
    /// <para>The existing <c>FactionContractStrategy.CanBeGenerated</c> spawn gate only covers the
    /// four wrapped strategy types (Acquisition/Hauling/Repair/PvEBounty); Bounty, Search, Salvage,
    /// and GridHauling stay raw vanilla, whose <c>CanBeGenerated</c> has no spawn check. Their
    /// nonzero chances make <c>CreateContractsForStation</c>'s retry loop call their vanilla
    /// <c>GenerateContract</c> up to CONTRACT_CREATION_TRIES_MAX times per station — profiled live
    /// (2026-08-09, EconomyTickProfiler) at 18.0s of a 19.2s economy tick across 1,970 stations,
    /// i.e. the periodic whole-server freeze.</para>
    ///
    /// <para>Gating those types' <c>CanBeGenerated</c> instead would NOT work: with every weight
    /// zero the roulette's <c>list[i] &gt;= NextFloat()*0</c> check degenerates to <c>0 &gt;= 0</c>,
    /// true for every type on every retry — vanilla would then call every strategy's
    /// <c>GenerateContract</c> unconditionally, which is worse. (The unwrapped types' nonzero
    /// weights are the only reason the wrapped types' zero weights currently suppress calls at
    /// all.) Skipping the whole method for unreachable stations is the only clean cut, and it is
    /// exactly what the CanBeGenerated fix intended: no physical grid means no terminal where any
    /// contract could ever be seen. <c>RefreshBoardOnSpawn</c> re-populates a board the moment its
    /// station materializes (StationEntityId is nonzero by then, so this prefix passes it).</para>
    /// </summary>
    [HarmonyPatch(typeof(MySessionComponentContractSystem), "CreateContractsForStation")]
    internal static class ContractGenerationGatePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(MyFaction faction, IMyFactionStation station)
        {
            if (SimulationGate.Deferred) return true;
            if (faction == null || station == null) return true;
            // A live grid, not merely a remembered id: a station whose grid was despawned keeps
            // StationEntityId on some paths, and refilling its board does nothing a player can see.
            if (EconomyTickSlicer.IsLive(station)) return true;
            return !FactionContractStrategy.IsRegistered(faction.Tag);
        }
    }
}
