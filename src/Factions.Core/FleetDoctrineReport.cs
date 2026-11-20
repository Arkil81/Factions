using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One doctrine line with the live counts the shipyard manager evaluates it against.
    /// Every number here comes from the manager's own helper, never from a second count.</summary>
    public sealed class DoctrineLineReport
    {
        public DoctrineLine Line;
        /// <summary><see cref="ShipyardManager.CountDoctrineHave"/>: parked in scope, flying, and in
        /// repair (<see cref="RepairWaiting"/> plus <see cref="Repairing"/>).</summary>
        public long Have;
        /// <summary>The flying half of <see cref="Have"/>.</summary>
        public long Flying;
        /// <summary><see cref="ShipyardManager.CountDoctrineBuildsInProgress"/>.</summary>
        public int Building;
        /// <summary>Live <c>Ship:{role}</c> demands in scope this line is the highest-ranked answer
        /// for. Work waiting on hulls the doctrine has decided not to build -- the ceiling made
        /// visible, and zero in the ordinary case.</summary>
        public int Pressure;
        /// <summary>No yard in scope this hull can fly out of.
        ///
        /// <para><b>Manager-owned, and read here rather than recomputed.</b> It is
        /// <see cref="DoctrineLine.LastBlockedLogSignature"/> being non-null -- the state
        /// <see cref="ShipyardManager.ServiceDoctrine"/> sets on the pass that found no placeable
        /// yard and clears on the pass that finds one. Two consequences a caller must know: it is
        /// <b>false until the first Shipyard pass after a restart</b>, because the field is not
        /// persisted; and it means "blocked on the last pass", not "blocked right now". Recomputing
        /// it here would need the route check per yard per line on every snapshot, and would be a
        /// second answer to a question the evaluator already answers.</para></summary>
        public bool RouteBlocked;
        /// <summary>The line's hull is over the server's performance ceiling (FAC-385), so the
        /// manager places nothing for it.</summary>
        public bool OverCeiling;
        /// <summary>The line's design is on the server's ban list (FAC-451), so the manager places
        /// nothing for it. Set independently of <see cref="OverCeiling"/>; the window names the ban
        /// first.</summary>
        public bool Banned;
        /// <summary>Hulls of the line's prefab in repair at a node in scope
        /// (<see cref="SpatialNode.DamagedHulls"/>): still short of parts, and with their parts in.
        /// Both are part of <see cref="Have"/>, the way <see cref="Flying"/> is.</summary>
        public int RepairWaiting;
        public int Repairing;
        /// <summary>The roles the line's hull is suitable for
        /// (<see cref="ShipyardManager.RolesFitting"/>) -- which <c>Ship:{role}</c> pressure it can
        /// answer. Derived from the hull; a line carries no role of its own.</summary>
        public List<string> Fits = new List<string>();
    }

    /// <summary>Work waiting on hulls that no unpaused line covers: a <c>Ship:{role}</c> demand
    /// group at a node where no doctrine line whose hull fits that role has a scope reaching it.</summary>
    public sealed class DoctrinePressureRow
    {
        public string Role;
        public string NodeId;
        public int Demands;
    }

    /// <summary>One hull the faction owns, across every node it holds.</summary>
    public sealed class FleetCensusRow
    {
        public string PrefabId;
        /// <summary>Parked, less the store-listed slice
        /// (<see cref="ShipyardManager.UnlistedStockAt"/>) -- the rule a line's Have counts by.</summary>
        public long Parked;
        public long Flying;
        /// <summary>In repair across the faction's nodes: still short of parts, and with their
        /// parts in (<see cref="HullDamage.CountRepairs"/>).</summary>
        public int RepairWaiting;
        public int Repairing;
    }

    /// <summary>
    /// Everything the Fleet window shows, assembled from the model: the doctrine with its live
    /// counts, the demand pressure no line answers, and a census of the hulls the faction actually
    /// owns.
    ///
    /// <para>In Core rather than in the host so it is unit-testable and so the sim can print the
    /// same picture the client draws. The host maps these plain objects onto wire structs and adds
    /// nothing but display names.</para>
    ///
    /// <para><b>Nothing here is a second computation.</b> Have, flying and building come from the
    /// <see cref="ShipyardManager"/> helpers the evaluator itself uses, and pressure is attributed
    /// with <see cref="ShipyardManager.HighestRankedLineFor"/> -- the same function that names the
    /// line in the <c>DOCTRINE_PRESSURE</c> log line. A window that counted for itself could tell a
    /// player a line was satisfied while the manager kept building.</para>
    /// </summary>
    public sealed class FleetDoctrineReport
    {
        /// <summary>The doctrine in evaluation order (<see cref="FactionState.DoctrineByRank"/>).</summary>
        public readonly List<DoctrineLineReport> Lines = new List<DoctrineLineReport>();

        /// <summary>Pressure no unpaused line covers, ordered by role then node so two snapshots of
        /// an unchanged world read identically.</summary>
        public readonly List<DoctrinePressureRow> Unlined = new List<DoctrinePressureRow>();

        /// <summary>Every hull the faction holds, ordered by prefab id.</summary>
        public readonly List<FleetCensusRow> Census = new List<FleetCensusRow>();

        /// <summary>Assembles the report for one faction. An empty doctrine is a real answer, not a
        /// missing one: the pressure rows and the census still describe a faction that has not
        /// written a doctrine yet, which is exactly the state the window is opened in.</summary>
        public static FleetDoctrineReport Build(WorldModel world, FactionState st)
        {
            var report = new FleetDoctrineReport();
            if (world == null || st == null) return report;

            var byLineId = new Dictionary<string, DoctrineLineReport>(StringComparer.Ordinal);
            foreach (DoctrineLine line in st.DoctrineByRank())
            {
                var row = new DoctrineLineReport
                {
                    Line = line,
                    Flying = ShipyardManager.CountDoctrineHullsInFlight(st, line, world.PrefabCatalogue),
                    Building = ShipyardManager.CountDoctrineBuildsInProgress(world, st.Tag, line),
                    RouteBlocked = line.LastBlockedLogSignature != null,
                    OverCeiling = world.IsPrefabOverCeiling(PrefabVersions.LatestFor(world.PrefabCatalogue, line.PrefabId, st.Tag)),
                    Banned = world.IsPrefabBanned(PrefabVersions.LatestFor(world.PrefabCatalogue, line.PrefabId, st.Tag)),
                    Fits = ShipyardManager.RolesFitting(world.PrefabCatalogue, line.PrefabId),
                };
                row.Have = ShipyardManager.CountDoctrineHave(world, st, line);
                if (!string.IsNullOrEmpty(line.PrefabId))
                    foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
                        if (ShipyardManager.DoctrineScopeCovers(line, node.Id))
                            HullDamage.CountRepairs(node, line.PrefabId, ref row.RepairWaiting, ref row.Repairing,
                                                    world.PrefabCatalogue);
                report.Lines.Add(row);
                if (!string.IsNullOrEmpty(line.Id)) byLineId[line.Id] = row;
            }

            AttributePressure(world, st, report, byLineId);
            BuildCensus(world, st, report);
            return report;
        }

        /// <summary>Groups this faction's live <c>Ship:*</c> demands by (role, node) and hands each
        /// group to the line that would have built for it, or to the unlined list when the doctrine
        /// has none.
        ///
        /// <para>The grouping matches <c>ShipyardManager</c>'s own: one group per (node, role), and
        /// the whole group counts, because a node carries a destination-keyed demand per blocked
        /// material vector and the number of routes waiting is the size of the pressure. Shelf
        /// demands (<see cref="DemandEntry.DestinedForSale"/>) are excluded for the reason the
        /// evaluator excludes them -- the store's hulls are a different purpose the doctrine says
        /// nothing about -- and so is the station-refit pseudo-role, which names no fleet.</para></summary>
        private static void AttributePressure(WorldModel world, FactionState st, FleetDoctrineReport report,
            Dictionary<string, DoctrineLineReport> byLineId)
        {
            if (world.Demand == null) return;

            // (role, node) -> demands in that group. Ordered, so the unlined list is stable.
            var groupKeys = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var roles = new Dictionary<string, string>(StringComparer.Ordinal);
            var nodes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (DemandEntry d in world.Demand.SnapshotShipDemands(st.Tag))
            {
                if (d.NodeId == null || d.DestinedForSale) continue;
                string role = d.Kind.Substring(5);   // "Ship:Trade" -> "Trade"
                if (string.Equals(role, ShipyardManager.StationRefitRole, StringComparison.Ordinal)) continue;

                // Only a node this faction still owns: a demand outliving the loss of the node it
                // sits at is not pressure on a doctrine that can no longer build there.
                SpatialNode node;
                if (!world.Graph.Nodes.TryGetValue(d.NodeId, out node) || node == null) continue;
                if (!string.Equals(node.OwnerFactionTag, st.Tag, StringComparison.OrdinalIgnoreCase)) continue;

                string key = d.NodeId + "|" + role;
                int existing;
                if (counts.TryGetValue(key, out existing))
                {
                    counts[key] = existing + 1;
                    continue;
                }
                counts[key] = 1;
                roles[key] = role;
                nodes[key] = d.NodeId;
                groupKeys.Add(key);
            }

            groupKeys.Sort(StringComparer.Ordinal);
            foreach (string key in groupKeys)
            {
                string role = roles[key];
                string nodeId = nodes[key];
                DoctrineLine line = ShipyardManager.HighestRankedLineFor(st, role, nodeId, world.PrefabCatalogue);

                DoctrineLineReport row;
                if (line != null && line.Id != null && byLineId.TryGetValue(line.Id, out row))
                {
                    row.Pressure += counts[key];
                    continue;
                }

                // No line for this role in this scope, or the only ones are paused -- a paused line
                // stops answering pressure, so its work reads as unlined rather than as its own.
                report.Unlined.Add(new DoctrinePressureRow
                {
                    Role = role,
                    NodeId = nodeId,
                    Demands = counts[key],
                });
            }

            report.Unlined.Sort(CompareUnlined);
        }

        private static int CompareUnlined(DoctrinePressureRow a, DoctrinePressureRow b)
        {
            int byRole = string.CompareOrdinal(a.Role, b.Role);
            return byRole != 0 ? byRole : string.CompareOrdinal(a.NodeId, b.NodeId);
        }

        /// <summary>Every hull the faction holds: parked across its nodes, in repair at them, and
        /// flying on its pending fleets.
        ///
        /// <para>One pass over the fleets accumulating per prefab, rather than
        /// <see cref="ShipyardManager.CountDoctrineHullsInFlight"/> once per prefab, which would be
        /// a pass per hull the faction owns. The two shapes it credits are deliberately the same --
        /// a plain convoy by <see cref="PendingFleetAction.ConvoySize"/>, a mixed squadron member by
        /// member, a resolved or intercepted fleet not at all -- and a test asserts the census and
        /// that helper agree, which is what keeps them from drifting.</para></summary>
        private static void BuildCensus(WorldModel world, FactionState st, FleetDoctrineReport report)
        {
            var parked = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var flying = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var waiting = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var repairing = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
            {
                for (int h = 0; h < node.DamagedHulls.Count; h++)
                {
                    DamagedHull hull = node.DamagedHulls[h];
                    if (hull == null || string.IsNullOrEmpty(hull.PrefabId)) continue;
                    Add(hull.PartsComplete ? repairing : waiting, hull.PrefabId, 1);
                }
                foreach (KeyValuePair<string, int> kv in node.ShipStock)
                {
                    long held = ShipyardManager.UnlistedStockAt(node, kv.Key);
                    if (held <= 0) continue;
                    Add(parked, kv.Key, held);
                }
            }

            for (int i = 0; i < st.PendingFleets.Count; i++)
            {
                PendingFleetAction fleet = st.PendingFleets[i];
                if (fleet.Resolved || fleet.Intercepted) continue;

                if (fleet.Squadron != null && fleet.Squadron.Count > 0)
                {
                    for (int m = 0; m < fleet.Squadron.Count; m++)
                    {
                        string member = fleet.Squadron[m];
                        if (!string.IsNullOrEmpty(member)) Add(flying, member, 1);
                    }
                    continue;
                }

                if (!string.IsNullOrEmpty(fleet.ShipPrefabId))
                    Add(flying, fleet.ShipPrefabId, Math.Max(1, fleet.ConvoySize));
            }

            var ids = new List<string>();
            foreach (KeyValuePair<string, long> kv in parked) ids.Add(kv.Key);
            var seen = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, long> more in new[] { flying, waiting, repairing })
                foreach (KeyValuePair<string, long> kv in more)
                    if (seen.Add(kv.Key)) ids.Add(kv.Key);
            ids.Sort(StringComparer.Ordinal);

            foreach (string id in ids)
            {
                long held, airborne, stillShort, worked;
                parked.TryGetValue(id, out held);
                flying.TryGetValue(id, out airborne);
                waiting.TryGetValue(id, out stillShort);
                repairing.TryGetValue(id, out worked);
                if (held <= 0 && airborne <= 0 && stillShort <= 0 && worked <= 0) continue;
                report.Census.Add(new FleetCensusRow
                {
                    PrefabId = id, Parked = held, Flying = airborne,
                    RepairWaiting = (int)stillShort, Repairing = (int)worked,
                });
            }
        }

        private static void Add(Dictionary<string, long> into, string key, long amount)
        {
            long existing;
            into.TryGetValue(key, out existing);
            into[key] = existing + amount;
        }
    }
}
