using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// What a ship build is still short of, and where in the estate that shortfall could come from.
    ///
    /// <para>The queue's own numbers say how far a build has got; they never say why it stopped. A
    /// build sitting at 29% is short of something, and the next question is always the same one:
    /// is that component missing from the faction entirely, sitting at another station, or already
    /// on a hull flying here. Those three answers live in three different places — the node's own
    /// inventory, every other owned node's, and the itineraries of the faction's in-flight convoys
    /// — and this is the one place that reads all three against one BOM.</para>
    ///
    /// <para><b>The estate half is gathered once per node, not once per entry.</b> A yard holds an
    /// entry per role and their BOMs overlap heavily, so <see cref="EstateView.For"/> walks the
    /// owned nodes and the pending fleets a single time for the union of every queued BOM's keys.
    /// <see cref="Rows"/> is then a dictionary read per component.</para>
    /// </summary>
    public static class BuildSupply
    {
        /// <summary>One BOM component of one build entry, with the four places its shortfall could
        /// be answered from.</summary>
        public struct ComponentRow
        {
            /// <summary>Inventory key, e.g. "Comp:SteelPlate".</summary>
            public string Key;
            /// <summary>Units the BOM calls for.</summary>
            public long Need;
            /// <summary>Units the entry has already absorbed.</summary>
            public long Have;
            /// <summary>Routable stock of this key at the build's own node. Non-zero here means the
            /// yard is not absorbing — a paused shipyard, or an entry waiting for a slot.</summary>
            public long AtNode;
            /// <summary>Routable stock of this key across the faction's OTHER nodes.</summary>
            public long Elsewhere;
            /// <summary>Units already aboard an in-flight convoy bound for this node, plus what a
            /// convoy's later pickup stop is going to collect for it.</summary>
            public long EnRoute;
            /// <summary>The faction's last Operations pass found no internal source for this key at
            /// this node — <see cref="FactionState.UnservedDemands"/>. The permanently-stuck case.</summary>
            public bool Unserved;

            /// <summary>Units still outstanding. Never negative: a BOM that has been over-collected
            /// is complete, not owed a refund by this row.</summary>
            public long Short { get { return Need > Have ? Need - Have : 0L; } }
        }

        /// <summary>
        /// The estate-wide half of the answer, gathered once for one node's whole build queue.
        /// Keyed by inventory key and holding only the keys that queue actually asks for, so the
        /// cost is one pass over the owned nodes and one over the faction's pending fleets however
        /// many entries read it.
        /// </summary>
        public sealed class EstateView
        {
            private readonly Dictionary<string, long> _elsewhere = new Dictionary<string, long>(StringComparer.Ordinal);
            private readonly Dictionary<string, long> _enRoute = new Dictionary<string, long>(StringComparer.Ordinal);
            private readonly HashSet<string> _unserved = new HashSet<string>(StringComparer.Ordinal);

            private EstateView() { }

            /// <summary>Nothing known about anything — what a node with no owner state resolves to,
            /// so a caller never has to null-check the view itself.</summary>
            public static EstateView Empty { get { return new EstateView(); } }

            public long Elsewhere(string key)
            {
                long v;
                return _elsewhere.TryGetValue(key, out v) ? v : 0L;
            }

            public long EnRoute(string key)
            {
                long v;
                return _enRoute.TryGetValue(key, out v) ? v : 0L;
            }

            public bool Unserved(string key)
            {
                return _unserved.Contains(key);
            }

            /// <summary>
            /// Gathers the estate half for <paramref name="node"/>'s build queue and repair bills.
            /// Returns <see cref="Empty"/> rather than null for a node with nothing queued.
            /// </summary>
            public static EstateView For(WorldModel world, FactionState owner, SpatialNode node)
            {
                var view = new EstateView();
                if (world == null || owner == null || node == null) return view;
                if (node.BuildQueue.Count == 0 && node.DamagedHulls.Count == 0) return view;

                var wanted = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < node.BuildQueue.Count; i++)
                {
                    BuildQueueEntry entry = node.BuildQueue[i];
                    if (entry == null) continue;
                    foreach (KeyValuePair<string, long> pair in entry.Bom) wanted.Add(pair.Key);
                }
                for (int i = 0; i < node.DamagedHulls.Count; i++)
                {
                    DamagedHull hull = node.DamagedHulls[i];
                    if (hull == null) continue;
                    foreach (KeyValuePair<string, long> pair in hull.Bill) wanted.Add(pair.Key);
                }
                if (wanted.Count == 0) return view;

                foreach (SpatialNode other in world.Graph.OwnedBy(owner.Tag))
                {
                    if (other == null || string.Equals(other.Id, node.Id, StringComparison.Ordinal)) continue;
                    foreach (KeyValuePair<string, long> pair in other.Inventory)
                    {
                        if (pair.Value <= 0 || !wanted.Contains(pair.Key)) continue;
                        long running;
                        view._elsewhere.TryGetValue(pair.Key, out running);
                        view._elsewhere[pair.Key] = running + pair.Value;
                    }
                }

                for (int i = 0; i < owner.PendingFleets.Count; i++)
                {
                    PendingFleetAction fleet = owner.PendingFleets[i];
                    if (fleet.Resolved || fleet.Intercepted) continue;
                    if (!CarriesSupply(fleet.Kind)) continue;
                    if (!string.Equals(DeliveryNodeOf(fleet), node.Id, StringComparison.Ordinal)) continue;

                    if (fleet.Cargo != null)
                    {
                        foreach (KeyValuePair<string, long> pair in fleet.Cargo)
                        {
                            if (pair.Value <= 0 || !wanted.Contains(pair.Key)) continue;
                            long running;
                            view._enRoute.TryGetValue(pair.Key, out running);
                            view._enRoute[pair.Key] = running + pair.Value;
                        }
                    }

                    // A consolidated convoy's later pickups are not aboard yet, but they were
                    // matched to this destination and nothing else will collect them.
                    if (fleet.RemainingStops == null) continue;
                    for (int s = 0; s < fleet.RemainingStops.Count; s++)
                    {
                        FleetStop stop = fleet.RemainingStops[s];
                        if (stop.Kind != FleetStopKind.Pickup) continue;
                        if (stop.PickupQuantity <= 0 || stop.PickupItemKey == null) continue;
                        if (!wanted.Contains(stop.PickupItemKey)) continue;
                        long running;
                        view._enRoute.TryGetValue(stop.PickupItemKey, out running);
                        view._enRoute[stop.PickupItemKey] = running + stop.PickupQuantity;
                    }
                }

                for (int i = 0; i < owner.UnservedDemands.Count; i++)
                {
                    UnservedDemand row = owner.UnservedDemands[i];
                    if (!string.Equals(row.NodeId, node.Id, StringComparison.Ordinal)) continue;
                    if (row.ItemKey == null || !wanted.Contains(row.ItemKey)) continue;
                    view._unserved.Add(row.ItemKey);
                }

                return view;
            }
        }

        /// <summary>
        /// Every BOM component of <paramref name="entry"/>, outstanding rows first and the largest
        /// shortfall at the top of those; complete rows keep their own descending order below.
        /// Empty for a free build, which has no BOM at all.
        /// </summary>
        public static List<ComponentRow> Rows(SpatialNode node, BuildQueueEntry entry, EstateView view)
        {
            if (entry == null) return new List<ComponentRow>();
            return Rows(node, entry.Bom, entry.Collected, view);
        }

        /// <summary>Every line of a damaged hull's repair bill, in the order and with the supply
        /// columns a build's BOM rows carry.</summary>
        public static List<ComponentRow> Rows(SpatialNode node, DamagedHull hull, EstateView view)
        {
            if (hull == null) return new List<ComponentRow>();
            return Rows(node, hull.Bill, hull.Collected, view);
        }

        private static List<ComponentRow> Rows(SpatialNode node, Dictionary<string, long> bill,
            Dictionary<string, long> collected, EstateView view)
        {
            var rows = new List<ComponentRow>();
            if (view == null) view = EstateView.Empty;

            foreach (KeyValuePair<string, long> pair in bill)
            {
                long have;
                collected.TryGetValue(pair.Key, out have);
                long atNode = 0;
                if (node != null) node.Inventory.TryGetValue(pair.Key, out atNode);

                rows.Add(new ComponentRow
                {
                    Key = pair.Key,
                    Need = pair.Value,
                    Have = have,
                    AtNode = atNode,
                    Elsewhere = view.Elsewhere(pair.Key),
                    EnRoute = view.EnRoute(pair.Key),
                    Unserved = view.Unserved(pair.Key),
                });
            }

            rows.Sort(CompareRows);
            return rows;
        }

        /// <summary>Outstanding before complete, biggest shortfall first, then by key so the order
        /// is stable across snapshots — a row that moved because a dictionary enumerated differently
        /// is a row the player has to find again.</summary>
        private static int CompareRows(ComponentRow a, ComponentRow b)
        {
            long shortA = a.Short, shortB = b.Short;
            if (shortA != shortB) return shortB.CompareTo(shortA);
            if (shortA == 0)
            {
                int byNeed = b.Need.CompareTo(a.Need);
                if (byNeed != 0) return byNeed;
            }
            return string.CompareOrdinal(a.Key, b.Key);
        }

        /// <summary>The fleet kinds that put goods into a node's own stock on arrival. A raider's
        /// hold is loot bound for its origin and a patrol carries nothing, so neither is supply
        /// anyone is waiting on here.</summary>
        private static bool CarriesSupply(ActionKind kind)
        {
            return kind == ActionKind.Haul || kind == ActionKind.Trade || kind == ActionKind.Gift;
        }

        /// <summary>
        /// Where a convoy's cargo is actually going: the first <see cref="FleetStopKind.Service"/>
        /// stop on its itinerary, which is the delivery whether or not pickups precede it and
        /// whether or not a <see cref="FleetStopKind.Return"/> leg follows it. A convoy with no
        /// remaining stops is flying its delivery leg right now, so its current target is it.
        /// </summary>
        private static string DeliveryNodeOf(PendingFleetAction fleet)
        {
            if (fleet.RemainingStops != null && fleet.RemainingStops.Count > 0)
            {
                for (int i = 0; i < fleet.RemainingStops.Count; i++)
                    if (fleet.RemainingStops[i].Kind == FleetStopKind.Service)
                        return fleet.RemainingStops[i].NodeId;
                // Pickups still ahead and no Service among them: the delivery was already made and
                // what is left is the way home.
                return null;
            }
            return fleet.CurrentStopKind == FleetStopKind.Service ? fleet.TargetNodeId : null;
        }
    }
}
