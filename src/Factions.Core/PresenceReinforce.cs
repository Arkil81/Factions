using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One node whose presence-as-Reinforce accumulator crossed its target this pass, with
    /// the graph-hop distance from the nearest node a faction member was actually standing at.</summary>
    public struct PresenceReinforceFire
    {
        public string NodeId;
        public int Hops;
    }

    /// <summary>Magnate-tier presence-as-Reinforce accumulation (see docs/economy-territory.md ->
    /// "Player-faction claiming" -> Growing). Pure policy, single-sourced and offline-testable; the
    /// host adapter (PresenceReinforceManager) supplies elapsed time from actual player presence and
    /// calls WorldModel.Reinforce when this fires.</summary>
    public static class PresenceReinforce
    {
        // Safety valve on the hop walk: a densified graph can carry hundreds of thousands of edges,
        // and this runs on the game thread. Bounds the BFS regardless of how large maxHops is set.
        private const int MaxVisitedNodes = 20000;

        // Nodes whose hop-decayed share of a tick is below this never get an accumulator entry —
        // keeps a generously-set maxHops from populating ReinforcePresenceSeconds with entries that
        // would take days of presence to fire.
        private const double MinCreditWeight = 0.01;

        /// <summary>Adds <paramref name="deltaSeconds"/> of continuous owning-faction presence at
        /// <paramref name="nodeId"/> to <paramref name="state"/>'s accumulator. Returns true exactly
        /// when the accumulator crosses <paramref name="targetSeconds"/> — the caller should then
        /// perform one Reinforce action for this node. The accumulator resets to 0 on firing rather
        /// than carrying over any overflow, matching a real Reinforce action's own coarse cadence.</summary>
        public static bool Accumulate(FactionState state, string nodeId, double deltaSeconds, double targetSeconds)
        {
            if (state == null || string.IsNullOrEmpty(nodeId) || deltaSeconds <= 0 || targetSeconds <= 0)
                return false;

            double accrued;
            state.ReinforcePresenceSeconds.TryGetValue(nodeId, out accrued);
            accrued += deltaSeconds;

            if (accrued >= targetSeconds)
            {
                state.ReinforcePresenceSeconds.Remove(nodeId); // back to 0; omitted from persistence like any other empty entry
                return true;
            }

            state.ReinforcePresenceSeconds[nodeId] = accrued;
            return false;
        }

        /// <summary>Credits <paramref name="deltaSeconds"/> of presence not only at the nodes members
        /// are standing at (<paramref name="presenceNodeIds"/>) but at every node <paramref name="state"/>
        /// owns within <paramref name="maxHops"/> graph-hops of one, scaling the accrual rate by
        /// <paramref name="hopFalloff"/> per hop (hop 0 = full rate). Traversal crosses foreign and
        /// unowned nodes — it measures graph distance, not a same-faction chain — but only nodes this
        /// faction owns accrue. Every node fired this pass is appended to <paramref name="fired"/>.
        /// <paramref name="maxHops"/> of 0 credits the presence nodes alone.</summary>
        public static void AccumulateWithSpread(FactionState state, SpatialGraph graph,
            ICollection<string> presenceNodeIds, double deltaSeconds, double targetSeconds,
            int maxHops, double hopFalloff, List<PresenceReinforceFire> fired)
        {
            if (state == null || graph == null || presenceNodeIds == null || presenceNodeIds.Count == 0) return;
            if (deltaSeconds <= 0 || targetSeconds <= 0) return;

            if (maxHops < 0) maxHops = 0;
            if (hopFalloff < 0) hopFalloff = 0;
            if (hopFalloff > 1) hopFalloff = 1;

            // Multi-source BFS: seeding every presence node at hop 0 yields each node's distance to
            // the *nearest* member, so two members standing a hop apart credit a shared neighbour
            // once at its closest weight rather than stacking.
            var hops = new Dictionary<string, int>(StringComparer.Ordinal);
            var frontier = new List<string>();
            foreach (string id in presenceNodeIds)
            {
                if (string.IsNullOrEmpty(id) || hops.ContainsKey(id)) continue;
                if (!graph.Nodes.ContainsKey(id)) continue;
                hops[id] = 0;
                frontier.Add(id);
            }

            for (int hop = 1; hop <= maxHops && frontier.Count > 0 && hops.Count < MaxVisitedNodes; hop++)
            {
                var next = new List<string>();
                for (int i = 0; i < frontier.Count; i++)
                {
                    SpatialNode node;
                    if (!graph.Nodes.TryGetValue(frontier[i], out node)) continue;
                    for (int j = 0; j < node.Neighbors.Count; j++)
                    {
                        string nbId = node.Neighbors[j];
                        if (hops.ContainsKey(nbId) || !graph.Nodes.ContainsKey(nbId)) continue;
                        hops[nbId] = hop;
                        next.Add(nbId);
                        if (hops.Count >= MaxVisitedNodes) break;
                    }
                    if (hops.Count >= MaxVisitedNodes) break;
                }
                frontier = next;
            }

            foreach (KeyValuePair<string, int> kv in hops)
            {
                SpatialNode node;
                if (!graph.Nodes.TryGetValue(kv.Key, out node)) continue;
                if (!string.Equals(node.OwnerFactionTag, state.Tag, StringComparison.Ordinal)) continue;

                double weight = kv.Value == 0 ? 1.0 : Math.Pow(hopFalloff, kv.Value);
                if (weight < MinCreditWeight) continue;

                if (Accumulate(state, kv.Key, deltaSeconds * weight, targetSeconds) && fired != null)
                    fired.Add(new PresenceReinforceFire { NodeId = kv.Key, Hops = kv.Value });
            }
        }
    }
}
