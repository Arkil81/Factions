using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Stock standing orders (<see cref="StandingOrderKind.Stock"/>): "keep N of this item at this
    /// node". An order does two things and nothing else.
    ///
    /// <para><b>It asks.</b> Each production pass posts the node's gap as demand, in the row shape
    /// <see cref="FleetMunitions.PostReserveForNode"/> uses: a <c>Need:</c> row for the gap and a gross
    /// <c>Item:</c> row for the target. From there the item arrives by whatever already serves a
    /// demand row: a haul from another owned node, the faction's own production, or a purchase from a
    /// partner that has it.</para>
    ///
    /// <para><b>It holds.</b> <see cref="Keep"/> is subtracted from the node's routable surplus, so the
    /// target is neither hauled away to another node's demand nor sold. The faction's own use is not
    /// held back: player withdrawals (<see cref="NodeTransfer"/>), production inputs and fleet
    /// munition draws (<c>FleetMunitions.Arm</c>).
    /// The order refills what they take.</para>
    ///
    /// <para><b>Its rank claims.</b> An order's <see cref="StandingOrder.Priority"/> puts its item, and
    /// the inputs of the recipe that makes it, on the claim scale ship builds and capability installs
    /// share (<see cref="StockClaimFloor"/>): a build or install ranked below the order leaves them,
    /// and a tie goes to the build or install. Rank 0 claims nothing they would give up.</para>
    ///
    /// <para>Several orders for the same node and item add up. A paused order, or one at a node the
    /// faction does not own, neither asks nor holds.</para>
    ///
    /// <para>Provenance: FAC-431; all stations FAC-499; rank FAC-501.</para>
    /// </summary>
    public static class StockOrders
    {
        /// <summary>Summed target per (node, item) over <paramref name="state"/>'s unpaused Stock
        /// orders at nodes it owns. Null when there are none.
        ///
        /// <para>An <see cref="StandingOrder.AllStations"/> order applies at every founded station
        /// the faction owns, read from ownership now, so a station founded or taken later is covered
        /// with no edit. The most specific order wins: at a (node, item) any per-node order names,
        /// the per-node orders alone set the target and the all-stations orders are ignored there, so
        /// a player can raise or lower one station against the faction default.</para></summary>
        public static Dictionary<(string NodeId, string ItemKey), long> Targets(FactionState state, SpatialGraph graph)
        {
            Dictionary<(string NodeId, string ItemKey), int> ranks;
            return Collect(state, graph, false, out ranks);
        }

        /// <summary><see cref="Targets"/>, and in <paramref name="ranks"/> the highest
        /// <see cref="StandingOrder.Priority"/> among the orders that set each target -- the per-node
        /// orders where one names the (node, item), the all-stations orders elsewhere.</summary>
        public static Dictionary<(string NodeId, string ItemKey), long> TargetsWithRanks(
            FactionState state, SpatialGraph graph, out Dictionary<(string NodeId, string ItemKey), int> ranks)
        {
            return Collect(state, graph, true, out ranks);
        }

        private static Dictionary<(string NodeId, string ItemKey), long> Collect(
            FactionState state, SpatialGraph graph, bool wantRanks,
            out Dictionary<(string NodeId, string ItemKey), int> ranks)
        {
            ranks = null;
            if (state == null || graph == null || state.StandingOrders.Count == 0) return null;
            Dictionary<(string NodeId, string ItemKey), long> targets = null;
            Dictionary<string, long> everyStation = null;
            Dictionary<string, int> everyStationRank = null;
            foreach (StandingOrder o in state.StandingOrders)
            {
                if (o.Kind != StandingOrderKind.Stock || o.Paused || o.TargetQuantity <= 0) continue;
                if (string.IsNullOrEmpty(o.ItemKey)) continue;
                long prior;
                if (o.AllStations)
                {
                    if (everyStation == null) everyStation = new Dictionary<string, long>(StringComparer.Ordinal);
                    everyStation.TryGetValue(o.ItemKey, out prior);
                    everyStation[o.ItemKey] = prior + o.TargetQuantity;
                    if (wantRanks)
                    {
                        if (everyStationRank == null) everyStationRank = new Dictionary<string, int>(StringComparer.Ordinal);
                        int rank;
                        everyStationRank[o.ItemKey] = everyStationRank.TryGetValue(o.ItemKey, out rank)
                            ? Math.Max(rank, o.Priority) : o.Priority;
                    }
                    continue;
                }
                if (string.IsNullOrEmpty(o.NodeId)) continue;
                SpatialNode node;
                if (!graph.Nodes.TryGetValue(o.NodeId, out node)) continue;
                if (!string.Equals(node.OwnerFactionTag, state.Tag, StringComparison.Ordinal)) continue;

                if (targets == null) targets = new Dictionary<(string NodeId, string ItemKey), long>();
                var key = (o.NodeId, o.ItemKey);
                targets.TryGetValue(key, out prior);
                targets[key] = prior + o.TargetQuantity;
                if (wantRanks)
                {
                    if (ranks == null) ranks = new Dictionary<(string NodeId, string ItemKey), int>();
                    int rank;
                    ranks[key] = ranks.TryGetValue(key, out rank) ? Math.Max(rank, o.Priority) : o.Priority;
                }
            }

            if (everyStation != null)
            {
                foreach (SpatialNode node in CoveredStations(state, graph))
                {
                    foreach (KeyValuePair<string, long> item in everyStation)
                    {
                        if (targets == null) targets = new Dictionary<(string NodeId, string ItemKey), long>();
                        var key = (node.Id, item.Key);
                        if (targets.ContainsKey(key)) continue;
                        targets[key] = item.Value;
                        if (wantRanks)
                        {
                            if (ranks == null) ranks = new Dictionary<(string NodeId, string ItemKey), int>();
                            ranks[key] = everyStationRank[item.Key];
                        }
                    }
                }
            }
            return targets;
        }

        /// <summary>The founded stations <paramref name="state"/> owns, which an
        /// <see cref="StandingOrder.AllStations"/> order covers.</summary>
        public static List<SpatialNode> CoveredStations(FactionState state, SpatialGraph graph)
        {
            var stations = new List<SpatialNode>();
            if (state == null || graph == null) return stations;
            foreach (SpatialNode node in graph.Nodes.Values)
                if (Economy.HasPhysicalStation(node)
                    && string.Equals(node.OwnerFactionTag, state.Tag, StringComparison.Ordinal))
                    stations.Add(node);
            return stations;
        }

        /// <summary>The founded stations at which an <see cref="StandingOrder.AllStations"/> order
        /// sets the target — every covered station a per-node order for the item does not override —
        /// and that target in <paramref name="target"/>: the sum of the item's unpaused all-stations
        /// orders, counting <paramref name="order"/> even while paused, so a paused order still reads
        /// what it would ask. Empty for any other order.</summary>
        public static List<SpatialNode> AppliedStations(FactionState state, SpatialGraph graph, StandingOrder order,
                                                        out long target)
        {
            target = 0;
            var applied = new List<SpatialNode>();
            if (state == null || graph == null || order == null || !order.AllStations
                || string.IsNullOrEmpty(order.ItemKey)) return applied;

            var overridden = new HashSet<string>(StringComparer.Ordinal);
            foreach (StandingOrder o in state.StandingOrders)
            {
                if (o.Kind != StandingOrderKind.Stock || o.TargetQuantity <= 0) continue;
                if (!string.Equals(o.ItemKey, order.ItemKey, StringComparison.Ordinal)) continue;
                if (o.AllStations)
                {
                    if (!o.Paused || ReferenceEquals(o, order)) target += o.TargetQuantity;
                }
                else if (!o.Paused && !string.IsNullOrEmpty(o.NodeId)) overridden.Add(o.NodeId);
            }

            foreach (SpatialNode station in CoveredStations(state, graph))
                if (!overridden.Contains(station.Id)) applied.Add(station);
            return applied;
        }

        /// <summary>Recomputes <paramref name="state"/>'s targets and records them on
        /// <paramref name="world"/> as the keeps <see cref="Keep"/> reads. Returns them.</summary>
        public static Dictionary<(string NodeId, string ItemKey), long> Publish(FactionState state, WorldModel world)
        {
            if (state == null || world == null) return null;
            Dictionary<(string NodeId, string ItemKey), long> targets = Targets(state, world.Graph);
            world.SetStockOrderKeeps(state.Tag, targets);
            return targets;
        }

        /// <summary>Units of <paramref name="itemKey"/> the owner's Stock orders keep back at
        /// <paramref name="node"/>.</summary>
        public static long Keep(SpatialNode node, string itemKey, WorldModel world)
        {
            if (node == null || world == null) return 0;
            return world.StockOrderKeep(node.OwnerFactionTag, node.Id, itemKey);
        }

        /// <summary>Posts the gap for every entry in <paramref name="targets"/> and returns how many
        /// (node, item) pairs asked for anything. A node holding its target asks for nothing.
        /// <paramref name="itemRowsByNodeKind"/> is the faction's <c>Item:</c> rows keyed
        /// <c>nodeId|kind</c>: a row already asking for at least the target is left as its poster
        /// wrote it.</summary>
        public static int PostDemands(string factionTag, Dictionary<(string NodeId, string ItemKey), long> targets,
                                      IProductionExecutionContext ctx, DateTime expiresUtc,
                                      IDictionary<string, DemandEntry> itemRowsByNodeKind)
        {
            if (targets == null || ctx == null) return 0;
            int posted = 0;
            foreach (KeyValuePair<(string NodeId, string ItemKey), long> t in targets)
            {
                string nodeId = t.Key.NodeId, itemKey = t.Key.ItemKey;
                long target = t.Value;
                long gap = target - ctx.GetNodeInventory(nodeId, itemKey);
                if (gap <= 0) continue;

                ctx.PostDemand(DemandEntry.ForNeed(factionTag, itemKey, nodeId, expiresUtc, gap, DemandClass.StoreStock));
                string itemKind = DemandRegister.ItemKind(itemKey);
                DemandEntry prior;
                bool covered = itemRowsByNodeKind != null
                               && itemRowsByNodeKind.TryGetValue(nodeId + "|" + itemKind, out prior)
                               && prior.Quantity >= target;
                if (!covered)
                    ctx.PostDemand(new DemandEntry
                    {
                        FactionTag = factionTag,
                        Kind       = itemKind,
                        NodeId     = nodeId,
                        ExpiresUtc = expiresUtc,
                        Quantity   = target,
                        Class      = DemandClass.StoreStock,
                    });
                posted++;
            }
            return posted;
        }
    }
}
