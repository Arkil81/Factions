using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Which calibration a fleet's predicted cost is scaled by. Combat has its own because
    /// a fight costs far more than the same hulls drifting (FAC-425: 7-9x the bench law).</summary>
    public enum TrafficClass
    {
        Civilian = 0,
        Combat = 1,
    }

    /// <summary>
    /// A fleet's predicted frame cost before it exists: the bench law
    /// (<see cref="PrefabCeiling.PredictedAwakeMs"/>) summed over the grids it will put in the world.
    /// Unscaled; <see cref="TrafficBudget"/> multiplies it by the live calibration for its class.
    ///
    /// <para>A squadron spawns each surviving hull as its own grid. Every other fleet flies its
    /// <see cref="PendingFleetAction.ConvoySize"/> hulls as one grid of
    /// <see cref="PendingFleetAction.ShipPrefabId"/>, so it costs one hull whatever the convoy size.</para>
    /// </summary>
    public static class FleetLoadCost
    {
        public static TrafficClass ClassOf(ActionKind kind)
        {
            return FactionsLoadActuator.IsCombat(kind) ? TrafficClass.Combat : TrafficClass.Civilian;
        }

        /// <param name="blocksOf">Blocks in a prefab over every grid it spawns; 0 when unknown.</param>
        /// <param name="unknownHullMs">Charged for a hull whose prefab has no block count (an empty
        /// <see cref="PendingFleetAction.ShipPrefabId"/> spawns from the role catalogue).</param>
        /// <param name="unknownHulls">How many hulls were charged <paramref name="unknownHullMs"/>.</param>
        public static double BaseMs(PendingFleetAction fleet, Func<string, int> blocksOf, PrefabCeiling law,
                                    double unknownHullMs, out int unknownHulls)
        {
            unknownHulls = 0;
            if (law == null) return 0.0;
            IReadOnlyList<string> squadron = fleet.HullsToReturn;
            if (squadron.Count > 0)
            {
                double sum = 0.0;
                for (int i = 0; i < squadron.Count; i++)
                    sum += HullMs(squadron[i], blocksOf, law, unknownHullMs, ref unknownHulls);
                return sum;
            }
            return HullMs(fleet.ShipPrefabId, blocksOf, law, unknownHullMs, ref unknownHulls);
        }

        /// <summary>Blocks the fleet will put in the world, over every grid of every hull it spawns;
        /// a hull with no catalogue count contributes nothing.</summary>
        public static int Blocks(PendingFleetAction fleet, Func<string, int> blocksOf)
        {
            if (blocksOf == null) return 0;
            IReadOnlyList<string> squadron = fleet.HullsToReturn;
            if (squadron.Count == 0) return string.IsNullOrEmpty(fleet.ShipPrefabId) ? 0 : Math.Max(0, blocksOf(fleet.ShipPrefabId));
            int sum = 0;
            for (int i = 0; i < squadron.Count; i++)
                if (!string.IsNullOrEmpty(squadron[i])) sum += Math.Max(0, blocksOf(squadron[i]));
            return sum;
        }

        private static double HullMs(string prefabId, Func<string, int> blocksOf, PrefabCeiling law,
                                     double unknownHullMs, ref int unknownHulls)
        {
            int blocks = string.IsNullOrEmpty(prefabId) || blocksOf == null ? 0 : blocksOf(prefabId);
            if (blocks > 0) return law.PredictedAwakeMs(blocks);
            unknownHulls++;
            return unknownHullMs;
        }
    }
}
