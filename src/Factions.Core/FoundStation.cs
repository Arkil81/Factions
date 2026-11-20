using System;

namespace Factions.Core
{
    /// <summary>Why founding a station on a claimed node cannot happen right now.
    /// <see cref="Ok"/> is the success case.</summary>
    public enum FoundStationResult
    {
        Ok = 0,
        NodeNotFound,
        NotOwned,
        /// <summary>The node already carries a station.</summary>
        AlreadyFounded,
        InsufficientTreasury,
    }

    /// <summary>
    /// Whether a faction may found a station on a node it holds, and what that costs
    /// (docs/economy-territory.md -> "Player-faction claiming" -> Founding).
    ///
    /// <para><b>The price is credits and nothing else.</b> A player faction pays
    /// <see cref="FactionTuning.FoundCost"/> out of its treasury — the same figure the NPC brain's own
    /// Found action is scored against, so founding is never cheaper for one side than the other. The
    /// NPC path additionally holds back <see cref="FactionTuning.FoundItemCostProcessedGood"/>, which
    /// is a stock check its own production loop can satisfy on its own; a player has no such loop
    /// working on their behalf, and gating a purchase on an abstract item stock they cannot see or
    /// buy is a wall rather than a price.</para>
    ///
    /// <para>Pure policy, single-sourced and offline-testable, so the chat verb, the client's station
    /// window and the server all refuse for the same reasons and quote the same number.</para>
    /// </summary>
    public static class FoundStationValidator
    {
        public static FoundStationResult Validate(IWorldView world, FactionTuning tuning, string factionTag,
            string nodeId, out long treasuryCost, out long treasuryHave)
        {
            treasuryCost = 0;
            treasuryHave = 0;
            if (world == null || tuning == null) return FoundStationResult.NodeNotFound;

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return FoundStationResult.NodeNotFound;
            if (string.IsNullOrEmpty(factionTag)
                || !string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return FoundStationResult.NotOwned;
            if (node.StableStationId != 0)
                return FoundStationResult.AlreadyFounded;

            treasuryCost = tuning.FoundCost;
            treasuryHave = world.GetTreasury(factionTag);
            if (treasuryHave < treasuryCost) return FoundStationResult.InsufficientTreasury;

            return FoundStationResult.Ok;
        }
    }
}
