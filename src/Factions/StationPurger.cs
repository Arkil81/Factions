using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.World;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// SE adapter for <c>factions world purge</c>: strip every <see cref="MyFactionStation"/> record
    /// from the world while leaving the factions that own them untouched. Read-only until
    /// <see cref="Apply"/> runs.
    ///
    /// This is the inverse of <see cref="FactionSanitizer"/>. Sanitize removes factions and keeps
    /// their stations; purge removes stations and keeps their factions. Both exist because SE's
    /// three world generators run behind one flag and there is no way to ask for only some of what
    /// they produce.
    ///
    /// <para><c>orphans</c> mode narrows the sweep to station records no Factions node points at —
    /// the debris a re-rolled estate leaves behind, since <c>factions world estate</c> mints a fresh
    /// record per founded node without removing the previous generation. Without it a purge is
    /// all-or-nothing per faction and would have to un-found every live station to remove the dead
    /// ones.</para>
    ///
    /// A station reached by a Factions node (<see cref="SpatialNode.StableStationId"/>) is purged
    /// too, but its node survives: the node is the richer record — it carries kind, deposit,
    /// capabilities, build queue and topology edges — so the station fields are cleared and the node
    /// reverts to un-founded rather than being deleted or left pointing at a station that is gone.
    ///
    /// MUST run on the game thread (it reads and mutates the faction collection and closes entities).
    /// </summary>
    internal static class StationPurger
    {
        internal sealed class StationPlan
        {
            public string Tag;
            public long   FactionId;
            public long   StationId;
            public string Name;
            public string PrefabName;
            public Vector3D Position;
            /// <summary>Live grid, or 0 when the station has never materialized.</summary>
            public long   GridEntityId;
            public long   SafeZoneEntityId;
            /// <summary>Factions node that owns this station, or null when nothing references it.</summary>
            public string NodeId;
        }

        internal sealed class Plan
        {
            public readonly List<StationPlan> Remove = new List<StationPlan>();
            /// <summary>Station count per faction tag, for the report.</summary>
            public readonly Dictionary<string, int> ByFaction =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Stations with a live grid. These are the ones with anything to tear down;
            /// the rest are abstract records and cost nothing to remove.</summary>
            public int Materialized;
            /// <summary>Stations a Factions node points at. Those nodes survive, un-founded.</summary>
            public int NodeBacked;
            /// <summary>Node-backed stations deliberately left out of the plan by <c>orphans</c>
            /// mode. Reported rather than merely omitted: "left 56 alone" is the assurance the mode
            /// exists to give, and a silent omission looks identical to a world that had none.</summary>
            public int SkippedNodeBacked;
            /// <summary>True when this plan was built in <c>orphans</c> mode.</summary>
            public bool OrphansOnly;
        }

        /// <summary>
        /// Build the purge plan without touching anything. <paramref name="graph"/> may be null —
        /// on a world that has never run <c>factions init</c> there is no graph and every station is
        /// simply node-less. <paramref name="tagFilter"/> limits the sweep to one faction tag when
        /// non-null.
        /// </summary>
        internal static Plan Build(SpatialGraph graph, string tagFilter, bool orphansOnly = false)
        {
            var plan = new Plan();
            plan.OrphansOnly = orphansOnly;
            if (MySession.Static == null || MySession.Static.Factions == null) return plan;

            // stationId -> nodeId, so the node lookup below is a dictionary hit rather than a scan
            // per station. A node with StableStationId 0 has no station and is not a candidate.
            var nodeByStation = new Dictionary<long, string>();
            if (graph != null)
                foreach (SpatialNode node in graph.Nodes.Values)
                    if (node != null && node.StableStationId != 0 && !nodeByStation.ContainsKey(node.StableStationId))
                        nodeByStation[node.StableStationId] = node.Id;

            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction faction = kv.Value;
                if (faction == null) continue;
                if (tagFilter != null && !string.Equals(faction.Tag, tagFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                int count = 0;
                foreach (var station in faction.Stations)   // var: MyFaction.Stations' element type
                {
                    var concrete = station as MyFactionStation;
                    if (concrete == null) continue;

                    string nodeId;
                    nodeByStation.TryGetValue(concrete.Id, out nodeId);

                    // orphans mode: a station some node still points at is live estate, not debris.
                    // Filtered here rather than at Apply so the dry run stays honest -- the plan a
                    // caller reads is exactly the plan that would be committed.
                    if (orphansOnly && nodeId != null)
                    {
                        plan.SkippedNodeBacked++;
                        continue;
                    }

                    plan.Remove.Add(new StationPlan
                    {
                        Tag              = faction.Tag,
                        FactionId        = faction.FactionId,
                        StationId        = concrete.Id,
                        Name             = concrete.Name,
                        PrefabName       = concrete.PrefabName,
                        Position         = concrete.Position,
                        GridEntityId     = concrete.StationEntityId,
                        SafeZoneEntityId = concrete.SafeZoneEntityId,
                        NodeId           = nodeId,
                    });

                    if (concrete.StationEntityId != 0) plan.Materialized++;
                    if (nodeId != null) plan.NodeBacked++;
                    count++;
                }

                if (count > 0) plan.ByFaction[faction.Tag] = count;
            }

            return plan;
        }

        /// <summary>
        /// Commit the plan. Each station record is torn down through
        /// <see cref="StationMaterializer.PurgeStationRecord"/> — the same sequence the UNKN grace
        /// sweep already uses — and any node that referenced one is reverted to un-founded rather
        /// than deleted.
        /// </summary>
        internal static void Apply(Plan plan, SpatialGraph graph, StationMaterializer materializer,
            out int stationsRemoved, out int gridsClosed, out int zonesClosed)
        {
            stationsRemoved = 0;
            gridsClosed = 0;
            zonesClosed = 0;
            if (plan == null || MySession.Static == null || MySession.Static.Factions == null) return;

            foreach (StationPlan sp in plan.Remove)
            {
                var faction = MySession.Static.Factions.TryGetFactionById(sp.FactionId) as MyFaction;
                if (faction == null) continue;

                bool gridClosed = false, zoneClosed = false;
                bool removed = materializer != null
                    && materializer.PurgeStationRecord(faction, sp.StationId, sp.NodeId, out gridClosed, out zoneClosed);

                if (removed) stationsRemoved++;
                if (gridClosed) gridsClosed++;
                if (zoneClosed) zonesClosed++;

                ClearNodeStationFields(graph, sp.NodeId);
            }
        }

        /// <summary>Revert a node to un-founded: it keeps its id, position, kind, deposit,
        /// capabilities, inventory and topology edges, and loses only the station that no longer
        /// exists. Leaving <see cref="SpatialNode.StableStationId"/> set would point at a removed
        /// record, which is the dangling state <c>Found</c> would then refuse to replace.</summary>
        private static void ClearNodeStationFields(SpatialGraph graph, string nodeId)
        {
            if (graph == null || nodeId == null) return;
            SpatialNode node;
            if (!graph.Nodes.TryGetValue(nodeId, out node) || node == null) return;

            node.StableStationId  = 0;
            StationMaterializer.MarkClaimsDirty();
            node.StationPrefabId  = null;
            node.StationBookValue = null;
            node.StationWeapons   = null;
            node.DisplayName      = null;   // the name belonged to the station, not the ground
        }

        /// <summary>Human-readable plan or outcome. <paramref name="applied"/> false is a dry run.</summary>
        internal static string Describe(Plan plan, bool applied, int stationsRemoved, int gridsClosed, int zonesClosed)
        {
            var sb = new StringBuilder();
            sb.Append(applied ? "world purge applied" : "world purge (dry run)");
            if (plan.OrphansOnly) sb.Append(" [orphans only]");
            sb.Append(": ");

            if (plan.Remove.Count == 0)
            {
                sb.Append(plan.OrphansOnly
                    ? "no orphan station records found -- nothing to purge."
                    : "no station records found -- nothing to purge.");
                if (plan.SkippedNodeBacked > 0)
                    sb.Append("\n  ").Append(plan.SkippedNodeBacked)
                      .Append(" node-backed station(s) left untouched.");
                return sb.ToString();
            }

            sb.Append(plan.Remove.Count).Append(" station record(s) across ")
              .Append(plan.ByFaction.Count).Append(" faction(s); ")
              .Append(plan.Materialized).Append(" materialized, ")
              .Append(plan.NodeBacked).Append(" backed by a Factions node.");

            if (applied)
                sb.Append("\n  removed ").Append(stationsRemoved).Append('/').Append(plan.Remove.Count)
                  .Append(" station(s), closed ").Append(gridsClosed).Append(" grid(s) and ")
                  .Append(zonesClosed).Append(" safe zone(s).");
            else
                sb.Append("\n  'factions world purge ").Append(plan.OrphansOnly ? "orphans " : "")
                  .Append("apply' to commit. Factions themselves are NOT touched.");

            if (plan.SkippedNodeBacked > 0)
                sb.Append("\n  ").Append(plan.SkippedNodeBacked)
                  .Append(" node-backed station(s) left untouched -- live estate is not in this plan.");

            if (plan.NodeBacked > 0)
                sb.Append("\n  ").Append(plan.NodeBacked)
                  .Append(" node(s) revert to un-founded (station fields cleared, node kept).");

            foreach (KeyValuePair<string, int> kv in plan.ByFaction)
                sb.Append("\n  - ").Append(kv.Key).Append("  stations=").Append(kv.Value);

            return sb.ToString();
        }
    }
}
