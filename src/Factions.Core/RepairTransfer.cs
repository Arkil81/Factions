using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>Moves damaged hulls from a node that cannot repair them to one that can. A hull lands
    /// in <see cref="SpatialNode.DamagedHulls"/> wherever its fleet comes home (<see cref="HullDamage.Land"/>),
    /// and only a node with a Shipyard ever services that queue, so a hull that came home to a depot
    /// would wait there for good. Once per shipyard pass every such queue on the faction's own nodes is
    /// flown to the nearest owned, unpaused yard the hull can reach, as a cargo-less
    /// <see cref="ActionKind.Haul"/> carrying the hulls at their integrity: the Haul terminal lands
    /// them through <see cref="HullDamage.Land"/> at the yard, where they queue for repair like any
    /// other arrival. A queue at a node that has since lost its yard, and a queue persisted in a brain,
    /// are moved the same way.
    ///
    /// <para>The hulls fly: the transfer takes transit time, is counted as in flight by the doctrine
    /// and the fleet census while it does (both read <see cref="PendingFleetAction.ShipPrefabId"/> x
    /// <see cref="PendingFleetAction.ConvoySize"/>), can be materialised and fought like any Haul, and
    /// a transfer whose yard is lost on the way lands wherever the Haul terminal resolves and, if
    /// that node has no yard either, is sent on by the next pass. With no reachable yard in the estate the hulls stay
    /// queued where they are (<c>yard=none</c> on the line that queued them) and move once one exists.</para>
    ///
    /// <para>Parts a hull had already absorbed at a yard that has since gone are put back into that
    /// node's inventory: they are physically there, and the destination yard bills the hull afresh.</para>
    ///
    /// <para>Provenance: FAC-432, fix 8.</para></summary>
    public static class RepairTransfer
    {
        /// <summary>The <see cref="PendingFleetAction.RelocationRole"/> a transfer carries: it names the
        /// flight on <c>SHIP_RELOCATE_ARRIVED</c> and keeps it out of every per-role relocation count.</summary>
        public const string Role = "Repair";

        /// <summary>Transfer fleets one faction dispatches per pass. A node's hulls are grouped by
        /// destination, prefab and integrity, one fleet per group; whatever the cap leaves waits for
        /// the next pass.</summary>
        public const int MaxTransfersPerPass = 8;

        /// <summary>Whether <paramref name="node"/> can repair a hull: it has a Shipyard.</summary>
        public static bool HasYard(SpatialNode node)
        {
            int yard;
            return node != null && node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out yard) && yard >= 1;
        }

        /// <summary>Dispatches <paramref name="st"/>'s repair transfers for this pass and logs one
        /// <c>REPAIR_REROUTED</c> line per fleet. Answers how many fleets it sent.</summary>
        public static int Dispatch(FactionState st, WorldModel world, FactionTuning tuning, IList<string> log)
        {
            if (st == null || world == null || tuning == null) return 0;

            List<SpatialNode> stranded = null;
            List<SpatialNode> yards = null;
            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
            {
                if (HasYard(node))
                {
                    if (!node.ShipyardPaused) (yards ?? (yards = new List<SpatialNode>())).Add(node);
                    continue;
                }
                if (node.DamagedHulls.Count > 0) (stranded ?? (stranded = new List<SpatialNode>())).Add(node);
            }
            if (stranded == null || yards == null) return 0;
            stranded.Sort(ById);
            yards.Sort(ById);

            int sent = 0;
            for (int s = 0; s < stranded.Count && sent < MaxTransfersPerPass; s++)
                sent += DispatchFrom(st, world, tuning, stranded[s], yards, MaxTransfersPerPass - sent, log);
            return sent;
        }

        private sealed class Group
        {
            public SpatialNode Yard;
            public string PrefabId;
            public double Integrity;
            public readonly List<DamagedHull> Hulls = new List<DamagedHull>();
        }

        private static int DispatchFrom(FactionState st, WorldModel world, FactionTuning tuning, SpatialNode node,
                                        List<SpatialNode> yards, int budget, IList<string> log)
        {
            // One reachability answer per prefab: every hull of a prefab leaves from the same node.
            var nearest = new Dictionary<string, SpatialNode>(StringComparer.Ordinal);
            var groups = new List<Group>();
            for (int i = 0; i < node.DamagedHulls.Count; i++)
            {
                DamagedHull hull = node.DamagedHulls[i];
                if (hull == null || string.IsNullOrEmpty(hull.PrefabId)) continue;

                SpatialNode yard;
                if (!nearest.TryGetValue(hull.PrefabId, out yard))
                {
                    yard = NearestYard(world, tuning, node, hull.PrefabId, yards);
                    nearest[hull.PrefabId] = yard;
                }
                if (yard == null) continue;

                Group group = null;
                for (int g = 0; g < groups.Count; g++)
                {
                    Group candidate = groups[g];
                    if (candidate.Yard == yard && candidate.Integrity == hull.Integrity
                        && string.Equals(candidate.PrefabId, hull.PrefabId, StringComparison.Ordinal))
                    {
                        group = candidate;
                        break;
                    }
                }
                if (group == null)
                {
                    if (groups.Count >= budget) continue;
                    group = new Group { Yard = yard, PrefabId = hull.PrefabId, Integrity = hull.Integrity };
                    groups.Add(group);
                }
                group.Hulls.Add(hull);
            }

            for (int g = 0; g < groups.Count; g++)
                Send(st, world, tuning, node, groups[g], log);
            return groups.Count;
        }

        /// <summary>The owned, unpaused yard nearest <paramref name="from"/> that
        /// <paramref name="prefabId"/> can reach under the same dry-TWR gate every landing uses
        /// (<see cref="ShipPool.IsRouteFeasible"/>); ties go to the lower node id. Null when none.</summary>
        public static SpatialNode NearestYard(WorldModel world, FactionTuning tuning, SpatialNode from,
                                               string prefabId, List<SpatialNode> yards)
        {
            SpatialNode best = null;
            double bestKm = double.MaxValue;
            for (int i = 0; i < yards.Count; i++)
            {
                SpatialNode yard = yards[i];
                double km = from.Position.DistanceTo(yard.Position);
                if (km >= bestKm) continue;   // yards are in id order, so a tie keeps the lower id
                ShipPool.RouteEnvironment route = ShipPool.RouteEnvironment.From(
                    world.Planets, from.Position, yard.Position, tuning.MinDryTwr);
                if (!ShipPool.IsRouteFeasible(world.PrefabCatalogue, prefabId, route)) continue;
                best = yard;
                bestKm = km;
            }
            return best;
        }

        private static void Send(FactionState st, WorldModel world, FactionTuning tuning, SpatialNode node,
                                 Group group, IList<string> log)
        {
            long partsReturned = 0;
            for (int i = 0; i < group.Hulls.Count; i++)
            {
                DamagedHull hull = group.Hulls[i];
                foreach (KeyValuePair<string, long> kv in hull.Collected)
                {
                    if (kv.Value <= 0) continue;
                    world.AddNodeInventory(node.Id, kv.Key, kv.Value);
                    partsReturned += kv.Value;
                }
                node.DamagedHulls.Remove(hull);
            }

            SpatialNode yard = group.Yard;
            double distKm = node.Position.DistanceTo(yard.Position);
            PendingFleetAction fleet = HullRelocation.Fly(st, world, tuning, node, yard, group.PrefabId,
                group.Hulls.Count, group.Integrity, Role,
                node.Id + ":repair:" + group.PrefabId + ":"
                + group.Integrity.ToString("R", CultureInfo.InvariantCulture), log);

            if (log != null)
                log.Add(st.Tag + " REPAIR_REROUTED fleet=" + fleet.LogId + " prefab=" + group.PrefabId
                        + " count=" + group.Hulls.Count
                        + " integrity=" + group.Integrity.ToString("0.00", CultureInfo.InvariantCulture)
                        + " from=" + node.Id + " to=" + yard.Id
                        + " km=" + distKm.ToString("0", CultureInfo.InvariantCulture)
                        + " eta=" + fleet.EstimatedArrivalUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                        + (partsReturned > 0 ? " parts_returned=" + partsReturned : ""));
        }

        private static int ById(SpatialNode a, SpatialNode b)
        {
            return string.CompareOrdinal(a.Id, b.Id);
        }
    }
}
