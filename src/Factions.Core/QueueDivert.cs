using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Where a Pickup arrival that waited out a station queue (docs/traffic-control.md, T3) collects
    /// instead. A Pickup's stop is bound to the stock at its node, so it cannot be delivered elsewhere
    /// the way a Service or Return stop is; it can be collected from another node of the same owner
    /// that holds the item. With no such node the errand is given up
    /// (<see cref="PendingFleetResolver.QueueGiveUpCause"/>).
    /// <para>Provenance: FAC-514.</para>
    /// </summary>
    public static class QueueDivert
    {
        /// <summary>The node <paramref name="fleet"/>'s current Pickup is collected from instead of
        /// its queued one: a node of the pickup node's owner, not in <paramref name="excluded"/>, with
        /// a surplus of the pickup item (<see cref="OperationsManager.SurplusOf(SpatialNode, string, WorldModel, FactionTuning)"/>).
        /// The nearest that covers the whole pickup quantity; failing
        /// that, the one with the most spare. Null when the stop is not a Pickup or no node has any.</summary>
        public static SpatialNode PickupSource(PendingFleetAction fleet, Vec3 fromKm, WorldModel world,
                                               FactionTuning tuning, ICollection<string> excluded)
        {
            if (world == null || fleet.CurrentStopKind != FleetStopKind.Pickup) return null;
            if (string.IsNullOrEmpty(fleet.PickupItemKey) || fleet.TargetNodeId == null) return null;
            SpatialNode queued;
            if (!world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out queued)) return null;
            string owner = queued.OwnerFactionTag;
            if (string.IsNullOrEmpty(owner)) return null;

            SpatialNode nearestCovering = null, mostSpare = null;
            double nearestKm = double.MaxValue;
            long mostSpareUnits = 0;
            foreach (SpatialNode node in world.Graph.Nodes.Values)
            {
                if (!string.Equals(node.OwnerFactionTag, owner, StringComparison.Ordinal)) continue;
                if (string.Equals(node.Id, queued.Id, StringComparison.Ordinal)) continue;
                if (excluded != null && excluded.Contains(node.Id)) continue;
                long spare = OperationsManager.SurplusOf(node, fleet.PickupItemKey, world, tuning);
                if (spare <= 0) continue;
                if (spare >= fleet.PickupQuantity)
                {
                    double d = fromKm.DistanceTo(node.Position);
                    if (d < nearestKm) { nearestKm = d; nearestCovering = node; }
                }
                else if (spare > mostSpareUnits) { mostSpareUnits = spare; mostSpare = node; }
            }
            return nearestCovering ?? mostSpare;
        }
    }
}
