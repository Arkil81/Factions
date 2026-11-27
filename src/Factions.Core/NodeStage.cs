using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// How developed a node looks from outside, the map icon's ladder (docs/fog-of-war.md →
    /// Economic stage). A Class-tier observation: gross size and activity are what a sensor measured
    /// to detect the node at all. Ordinals are the wire byte.
    /// </summary>
    public enum NodeStage : byte
    {
        /// <summary>Not observed: below Class, or never graded.</summary>
        None = 0,
        /// <summary>No infrastructure.</summary>
        Field = 1,
        /// <summary>Infrastructure but no station.</summary>
        Outpost = 2,
        /// <summary>A station.</summary>
        Settlement = 3,
        /// <summary>A station scoring at least <see cref="FactionTuning.NodeHubScore"/>.</summary>
        Hub = 4,
        /// <summary>Its owner's highest-scoring Hub. One per faction.</summary>
        Capital = 5,
    }

    /// <summary>
    /// The stage of every node, with each faction's capital resolved once per build. Built from the
    /// live graph by the host once per knowledge sweep, so the HUD, the map and the sweeps read the
    /// same answer for a whole pass.
    /// </summary>
    public sealed class NodeStages
    {
        private readonly Dictionary<string, string> _capitalByOwner =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly double _hubScore;

        private NodeStages(double hubScore) { _hubScore = hubScore; }

        /// <summary>
        /// The economic score: capability levels summed, plus a tenth of the headcount. Ship traffic
        /// is deliberately absent. It was in the design, and the live graph had it at its cap on
        /// nearly every station (p50 8.6 of 10, FAC-529), so it would have said nothing.
        /// </summary>
        public static double Score(SpatialNode node)
        {
            if (node == null) return 0;
            double score = 0;
            foreach (KeyValuePair<NodeCapabilityKind, int> cap in node.Capabilities)
                if (cap.Value > 0) score += cap.Value;
            return score + WorkerUpkeep.Headcount(node) / 10.0;
        }

        public static NodeStages Build(SpatialGraph graph, FactionTuning tuning)
        {
            var stages = new NodeStages(tuning != null ? tuning.NodeHubScore : 10.0);
            if (graph == null) return stages;
            var best = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null || node.StableStationId == 0) continue;
                string owner = node.OwnerFactionTag;
                if (string.IsNullOrEmpty(owner) || NeutralFaction.IsClaimable(owner)) continue;
                double score = Score(node);
                if (score < stages._hubScore) continue;
                double held;
                string heldId;
                // Ties go to the lower id, so the capital does not depend on dictionary order.
                if (best.TryGetValue(owner, out held)
                    && (score < held || (score == held
                        && stages._capitalByOwner.TryGetValue(owner, out heldId)
                        && string.CompareOrdinal(heldId, node.Id) < 0)))
                    continue;
                best[owner] = score;
                stages._capitalByOwner[owner] = node.Id;
            }
            return stages;
        }

        public NodeStage Of(SpatialNode node)
        {
            if (node == null) return NodeStage.None;
            if (node.StableStationId == 0)
                return HasInfrastructure(node) ? NodeStage.Outpost : NodeStage.Field;
            if (Score(node) < _hubScore) return NodeStage.Settlement;
            string capital;
            return node.OwnerFactionTag != null
                   && _capitalByOwner.TryGetValue(node.OwnerFactionTag, out capital)
                   && capital == node.Id
                ? NodeStage.Capital
                : NodeStage.Hub;
        }

        public int CapitalCount { get { return _capitalByOwner.Count; } }

        private static bool HasInfrastructure(SpatialNode node)
        {
            foreach (KeyValuePair<NodeCapabilityKind, int> cap in node.Capabilities)
                if (cap.Value > 0) return true;
            return false;
        }
    }
}
