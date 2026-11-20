using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Which node reclaims a piece of wreckage. The host decides *whether* a grid is debris — that
    /// answer belongs to SE's own trash criteria and cannot be asked outside a live session — and
    /// this decides *where* the parts land.
    ///
    /// <para>Kept in Core because it is the only part of the feature that is pure: a position, the
    /// graph, and a reach. The credit arithmetic it feeds is already here
    /// (<see cref="ServiceSettlement.CreditSalvage"/>).</para>
    /// </summary>
    public static class DebrisAbsorption
    {
        /// <summary>A node can reclaim wreckage only if it has a station standing on it and an owner
        /// to credit. An unclaimed node has no inventory anyone reads, and an owned node with no
        /// station is not a place a salvage crew works out of.</summary>
        public static bool CanAbsorb(SpatialNode node)
        {
            return node != null
                && node.StableStationId != 0
                && !string.IsNullOrEmpty(node.OwnerFactionTag);
        }

        /// <summary>The closest node able to reclaim a wreck at <paramref name="posKm"/> and within
        /// <paramref name="radiusKm"/> of it, or null when nothing is in reach. A tie goes to the
        /// node whose id sorts first, so a sweep over the same world twice makes the same choice.</summary>
        public static SpatialNode NearestAbsorbingNode(SpatialGraph graph, Vec3 posKm, double radiusKm)
        {
            if (graph == null || radiusKm <= 0) return null;

            double reachSquared = radiusKm * radiusKm;
            SpatialNode best = null;
            double bestSquared = 0;

            foreach (KeyValuePair<string, SpatialNode> entry in graph.Nodes)
            {
                SpatialNode node = entry.Value;
                if (!CanAbsorb(node)) continue;

                double distanceSquared = node.Position.DistanceSquaredTo(posKm);
                if (distanceSquared > reachSquared) continue;

                if (best == null
                    || distanceSquared < bestSquared
                    || (distanceSquared == bestSquared && string.CompareOrdinal(node.Id, best.Id) < 0))
                {
                    best = node;
                    bestSquared = distanceSquared;
                }
            }

            return best;
        }
    }
}
