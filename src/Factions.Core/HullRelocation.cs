using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>Flies a faction's own hulls from one node to another as a cargo-less
    /// <see cref="ActionKind.Haul"/>, and evacuates a node its faction has just lost.
    ///
    /// <para>A relocation fleet is an ordinary Haul: it takes transit time, is counted as in flight by
    /// the doctrine and the fleet census while it does (both read <see cref="PendingFleetAction.ShipPrefabId"/>
    /// x <see cref="PendingFleetAction.ConvoySize"/>, scoped by <see cref="PendingFleetAction.ReturnNodeId"/>),
    /// can be materialised and intercepted like any Haul, and lands through the Haul terminal: whole
    /// hulls into <see cref="SpatialNode.ShipStock"/>, damaged ones into
    /// <see cref="SpatialNode.DamagedHulls"/> through <see cref="HullDamage.Land"/>. A destination the
    /// faction no longer owns on arrival lands the hulls at the nearest owned node they can reach
    /// (<see cref="ShipPool.ResolveLanding"/>); a damaged hull that lands at a node without a yard is
    /// sent on by <see cref="RepairTransfer"/>.</para></summary>
    public static class HullRelocation
    {
        /// <summary>The <see cref="PendingFleetAction.RelocationRole"/> of a flight off a node its
        /// faction has just lost (<see cref="Evacuate"/>). Names the flight on
        /// <c>SHIP_RELOCATE_ARRIVED</c> and keeps it out of every per-role relocation count.
        /// Damaged hulls flown to a yard by the same evacuation carry it too: what distinguishes them
        /// is the integrity the fleet carries, not the role.</summary>
        public const string EvacuateRole = "Evacuate";

        /// <summary><see cref="ShipEvacuation.Reason"/> for damaged hulls sent to a yard.</summary>
        public const string ToYard = "yard";
        /// <summary><see cref="ShipEvacuation.Reason"/> for damaged hulls that could reach no yard and
        /// were sent to the nearest owned node instead.</summary>
        public const string NoReachableYard = "no_reachable_yard";
        /// <summary><see cref="ShipEvacuation.Reason"/> when nothing the faction owns is reachable.</summary>
        public const string NoOwnedNode = "no_owned_node";
        /// <summary><see cref="ShipEvacuation.Reason"/> when the caller could not name the faction's
        /// state, so there is no fleet list to put a flight on and the hulls stay where they are.</summary>
        public const string NoFactionState = "no_faction_state";

        /// <summary>Dispatches <paramref name="count"/> hulls of <paramref name="prefabId"/> at
        /// <paramref name="integrity"/> from <paramref name="from"/> to <paramref name="to"/>, adds the
        /// fleet to <paramref name="st"/>'s pending fleets and returns it. The caller has already taken
        /// the hulls out of whatever pool held them. <paramref name="idSalt"/> separates fleets minted
        /// in the same second toward the same node.</summary>
        public static PendingFleetAction Fly(FactionState st, WorldModel world, FactionTuning tuning,
                                             SpatialNode from, SpatialNode to, string prefabId, int count,
                                             double integrity, string role, string idSalt, IList<string> log)
        {
            double distKm = from.Position.DistanceTo(to.Position);
            DateTime departed = world.UtcNow;
            DateTime arrival = tuning.FleetSpeedKmPerMin > 0
                ? departed.AddMinutes(tuning.TransitTimeMins(distKm))
                : departed;

            var fleet = new PendingFleetAction
            {
                Kind                = ActionKind.Haul,
                RelocationRole      = role,
                ActorTag            = st.Tag,
                OriginNodeId        = from.Id,
                TargetNodeId        = to.Id,
                // Where the hulls come home to: the doctrine scopes its in-flight count by this.
                ReturnNodeId        = to.Id,
                OriginPosKm         = from.Position,
                TargetPosKm         = to.Position,
                DepartedUtc         = departed,
                EstimatedArrivalUtc = arrival,
                ConvoySize          = count,
                ShipPrefabId        = prefabId,
                FleetId             = PendingFleetAction.MintId(st.PendingFleets, st.Tag, departed, to.Id, idSalt),
                LastResolvedSegment = -1,
            };
            fleet.SetIntegrity(0, integrity);
            fleet = PendingFleetResolver.PlanFleetLeg(fleet, world, tuning, false, log);
            st.PendingFleets.Add(fleet);
            return fleet;
        }

        /// <summary>Flies the prior owner's hulls off <paramref name="node"/>, which has just been
        /// handed to <see cref="NeutralFaction"/>, and appends one <see cref="ShipEvacuation"/> per
        /// fleet dispatched or group left behind to <paramref name="records"/>.
        ///
        /// <para>Whole hulls (<see cref="SpatialNode.ShipStock"/>) fly, one fleet per prefab, to the
        /// node <see cref="ShipPool.ResolveLanding"/> picks: the nearest owned node the prefab can
        /// reach. Their store listing is dropped with them, because
        /// <see cref="SpatialNode.ListedForSale"/> is a reservation against this node's stock.</para>
        ///
        /// <para>Damaged hulls (<see cref="SpatialNode.DamagedHulls"/>) fly to the nearest owned,
        /// unpaused yard the prefab can reach (<see cref="RepairTransfer.NearestYard"/>), else to the
        /// nearest owned node it can reach, where <see cref="RepairTransfer"/> moves them on once a
        /// yard exists. One fleet per destination, prefab and integrity. Parts a hull had already
        /// absorbed stay in the node's inventory with the rest of the derelict's loot; the
        /// destination bills the hull afresh.</para>
        ///
        /// <para>A hull that can reach nothing the faction owns stays on the node, recorded with
        /// <see cref="NoOwnedNode"/>. With no <paramref name="owner"/> state there is no fleet list,
        /// so every hull stays, recorded with <see cref="NoFactionState"/>.</para>
        ///
        /// <para>Provenance: FAC-278 (whole hulls, then instant), FAC-432 fix 5.</para></summary>
        public static void Evacuate(WorldModel world, FactionState owner, string ownerTag, SpatialNode node,
                                    IList<ShipEvacuation> records)
        {
            if (world == null || node == null || ownerTag == null) return;
            FactionTuning tuning = world.Tuning;
            EvacuateStock(world, tuning, owner, ownerTag, node, records);
            EvacuateDamaged(world, tuning, owner, ownerTag, node, records);
        }

        private static void EvacuateStock(WorldModel world, FactionTuning tuning, FactionState owner,
                                          string ownerTag, SpatialNode node, IList<ShipEvacuation> records)
        {
            if (node.ShipStock.Count == 0) return;
            var prefabIds = new List<string>(node.ShipStock.Keys);   // snapshot -- the move mutates ShipStock
            prefabIds.Sort(StringComparer.Ordinal);
            foreach (string prefabId in prefabIds)
            {
                int count;
                if (!node.ShipStock.TryGetValue(prefabId, out count) || count <= 0) continue;

                var record = new ShipEvacuation
                {
                    FactionTag = ownerTag, PrefabId = prefabId, Count = count, FromNodeId = node.Id,
                    Integrity = 1.0,
                };
                ShipPool.Landing landing = ShipPool.ResolveLanding(world, ownerTag, prefabId,
                                                                   node.Id, null, node.Position);
                if (landing.NodeId == null || owner == null)
                {
                    record.Reason = landing.NodeId == null ? NoOwnedNode : NoFactionState;
                    records.Add(record);
                    continue;
                }

                node.ShipStock.Remove(prefabId);
                node.ListedForSale.Remove(prefabId);
                PendingFleetAction fleet = Fly(owner, world, tuning, node, landing.Node, prefabId, count, 1.0,
                                               EvacuateRole, node.Id + ":evac:" + prefabId, null);
                record.ToNodeId = landing.NodeId;
                record.Reason = landing.Reason;
                record.FleetLogId = fleet.LogId;
                record.EtaUtc = fleet.EstimatedArrivalUtc;
                records.Add(record);
            }
        }

        private sealed class Group
        {
            public SpatialNode Dest;
            public string Reason;
            public string PrefabId;
            public double Integrity;
            public readonly List<DamagedHull> Hulls = new List<DamagedHull>();
        }

        private static void EvacuateDamaged(WorldModel world, FactionTuning tuning, FactionState owner,
                                            string ownerTag, SpatialNode node, IList<ShipEvacuation> records)
        {
            if (node.DamagedHulls.Count == 0) return;

            var yards = new List<SpatialNode>();
            foreach (SpatialNode n in world.Graph.OwnedBy(ownerTag))
                if (RepairTransfer.HasYard(n) && !n.ShipyardPaused) yards.Add(n);
            yards.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            // One destination answer per prefab: every hull of a prefab leaves from the same node.
            var destByPrefab = new Dictionary<string, KeyValuePair<SpatialNode, string>>(StringComparer.Ordinal);
            var groups = new List<Group>();
            for (int i = 0; i < node.DamagedHulls.Count; i++)
            {
                DamagedHull hull = node.DamagedHulls[i];
                if (hull == null || string.IsNullOrEmpty(hull.PrefabId)) continue;

                KeyValuePair<SpatialNode, string> dest;
                if (!destByPrefab.TryGetValue(hull.PrefabId, out dest))
                {
                    dest = PickDamagedDestination(world, tuning, ownerTag, node, hull.PrefabId, yards);
                    destByPrefab[hull.PrefabId] = dest;
                }
                if (owner == null) dest = new KeyValuePair<SpatialNode, string>(null, NoFactionState);

                Group group = null;
                for (int g = 0; g < groups.Count; g++)
                {
                    Group c = groups[g];
                    if (c.Dest == dest.Key && c.Integrity == hull.Integrity
                        && string.Equals(c.PrefabId, hull.PrefabId, StringComparison.Ordinal))
                    {
                        group = c;
                        break;
                    }
                }
                if (group == null)
                {
                    group = new Group { Dest = dest.Key, Reason = dest.Value, PrefabId = hull.PrefabId,
                                        Integrity = hull.Integrity };
                    groups.Add(group);
                }
                group.Hulls.Add(hull);
            }

            for (int g = 0; g < groups.Count; g++)
            {
                Group group = groups[g];
                var record = new ShipEvacuation
                {
                    FactionTag = ownerTag, PrefabId = group.PrefabId, Count = group.Hulls.Count,
                    FromNodeId = node.Id, Reason = group.Reason, Integrity = group.Integrity, Damaged = true,
                };
                if (group.Dest == null)
                {
                    records.Add(record);
                    continue;
                }

                for (int i = 0; i < group.Hulls.Count; i++)
                {
                    DamagedHull hull = group.Hulls[i];
                    foreach (KeyValuePair<string, long> kv in hull.Collected)
                    {
                        if (kv.Value <= 0) continue;
                        world.AddNodeInventory(node.Id, kv.Key, kv.Value);
                        record.PartsLeft += kv.Value;
                    }
                    node.DamagedHulls.Remove(hull);
                }
                PendingFleetAction fleet = Fly(owner, world, tuning, node, group.Dest, group.PrefabId,
                    group.Hulls.Count, group.Integrity, EvacuateRole,
                    node.Id + ":evac-damaged:" + group.PrefabId + ":"
                    + group.Integrity.ToString("R", CultureInfo.InvariantCulture), null);
                record.ToNodeId = group.Dest.Id;
                record.FleetLogId = fleet.LogId;
                record.EtaUtc = fleet.EstimatedArrivalUtc;
                records.Add(record);
            }
        }

        /// <summary>Where a damaged <paramref name="prefabId"/> leaving <paramref name="from"/> goes:
        /// the nearest reachable yard (<see cref="ToYard"/>), else the nearest reachable owned node
        /// (<see cref="NoReachableYard"/>), else nowhere (<see cref="NoOwnedNode"/>).</summary>
        private static KeyValuePair<SpatialNode, string> PickDamagedDestination(WorldModel world, FactionTuning tuning,
            string ownerTag, SpatialNode from, string prefabId, List<SpatialNode> yards)
        {
            SpatialNode yard = RepairTransfer.NearestYard(world, tuning, from, prefabId, yards);
            if (yard != null) return new KeyValuePair<SpatialNode, string>(yard, ToYard);
            ShipPool.Landing landing = ShipPool.ResolveLanding(world, ownerTag, prefabId, from.Id, null, from.Position);
            if (landing.NodeId != null) return new KeyValuePair<SpatialNode, string>(landing.Node, NoReachableYard);
            return new KeyValuePair<SpatialNode, string>(null, NoOwnedNode);
        }
    }
}
