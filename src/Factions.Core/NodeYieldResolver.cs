using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The single place that answers "what can this node yield, and in what proportion".
    ///
    /// <para>Returns the same <see cref="NodeYieldEntry"/> shape every consumer already reads, so the
    /// income mint, Mine targeting and the asteroid-field planner keep their own logic and only change
    /// where their entries come from. That is deliberate: five consumers agreeing by construction beats
    /// five consumers each re-deriving the answer.</para>
    ///
    /// <para><b>The rule.</b> A node's <see cref="SpatialNode.Deposit"/> replaces the <b>Ore and
    /// RareOre channels only</b>. Every other channel passes through from the node-kind table
    /// untouched — a Core node's ProcessedGood, a FuelDepot's Hydrogen and Oxygen. The ground says
    /// what ore is in it; it says nothing about what a settlement manufactures. No shipped table
    /// states any of those channels today.</para>
    ///
    /// <para><see cref="NodeYieldEntry.MineOnly"/> entries also pass through unchanged. <c>Ore:Stone</c>
    /// is the only one, and it is deliberately not geography: stone is the base voxel material
    /// everywhere there is rock at all, gated by effort rather than by location (see FAC-050). A
    /// deposit derived from the ore map will never contain it, so taking it from the table is what
    /// keeps it mineable — and since FAC-233 retired the passive <c>Ingot:Stone</c> mint, it is the
    /// only source of gravel there is.</para>
    ///
    /// <para>A node with no deposit — never sampled, or a host with no ore geography wired — gets the
    /// table verbatim, so nothing changes anywhere the seam is absent.</para>
    ///
    /// <para>Shares are renormalised <b>within each ItemKind</b> rather than across the whole deposit.
    /// That preserves the table's existing contract (weights within an ItemKind sum to 1.0) and keeps
    /// this change about <i>which</i> ores a node yields, not <i>how much</i> it yields in total —
    /// magnitude stays governed by <see cref="FactionTuning.FreeMintScale"/> exactly as before.
    /// See docs/ore-geography.md.</para>
    /// </summary>
    public static class NodeYieldResolver
    {
        private static readonly NodeYieldEntry[] Empty = new NodeYieldEntry[0];

        /// <summary>Effective yield entries for <paramref name="node"/>. Never null.
        /// <para>Cached on the node against the deposit and tuning instances it was built from, so the
        /// per-frame observation loops do not allocate an array per node per pass — the cost profile
        /// that made asteroid-field observation 69% of plugin CPU once already.</para></summary>
        public static NodeYieldEntry[] EntriesFor(SpatialNode node, FactionTuning tuning, IItemRegistry registry)
        {
            if (node == null || tuning == null) return Empty;

            NodeYieldEntry[] table = tuning.NodeYieldTable;
            if (node.Deposit == null || node.Deposit.IsBarren) return table ?? Empty;

            if (ReferenceEquals(node.EffectiveYieldDepositKey, node.Deposit)
                && ReferenceEquals(node.EffectiveYieldTuningKey, tuning)
                && node.EffectiveYieldCache != null)
                return node.EffectiveYieldCache;

            NodeYieldEntry[] built = Build(node, table, registry);
            node.EffectiveYieldCache = built;
            node.EffectiveYieldDepositKey = node.Deposit;
            node.EffectiveYieldTuningKey = tuning;
            return built;
        }

        private static NodeYieldEntry[] Build(SpatialNode node, NodeYieldEntry[] table, IItemRegistry registry)
        {
            var entries = new List<NodeYieldEntry>();

            // Everything the deposit does not speak for, kept verbatim.
            if (table != null)
            {
                foreach (NodeYieldEntry e in table)
                {
                    if (e == null) continue;
                    if (e.MineOnly) { entries.Add(e); continue; }

                    ItemKind k;
                    if (!Enum.TryParse(e.ItemKind, out k)) continue;
                    if (k == ItemKind.Ore || k == ItemKind.RareOre) continue; // the deposit owns these
                    entries.Add(e);
                }
            }

            // The deposit's own ores, renormalised within each ItemKind.
            var sumByKind = new Dictionary<ItemKind, double>();
            var kindOf = new Dictionary<string, ItemKind>(StringComparer.Ordinal);

            for (int i = 0; i < node.Deposit.Entries.Count; i++)
            {
                DepositEntry d = node.Deposit.Entries[i];
                if (string.IsNullOrEmpty(d.SubTypeId) || d.Weight <= 0) continue;

                ItemKind k;
                if (registry == null || !registry.TryGetItemKind(d.SubTypeId, out k)) k = ItemKind.Ore;
                if (k != ItemKind.Ore && k != ItemKind.RareOre) k = ItemKind.Ore;

                kindOf[d.SubTypeId] = k;
                double sum;
                sumByKind[k] = sumByKind.TryGetValue(k, out sum) ? sum + d.Weight : d.Weight;
            }

            for (int i = 0; i < node.Deposit.Entries.Count; i++)
            {
                DepositEntry d = node.Deposit.Entries[i];
                ItemKind k;
                if (string.IsNullOrEmpty(d.SubTypeId) || !kindOf.TryGetValue(d.SubTypeId, out k)) continue;

                double kindSum;
                if (!sumByKind.TryGetValue(k, out kindSum) || kindSum <= 0) continue;

                entries.Add(new NodeYieldEntry
                {
                    // Prefixed, matching the node-kind table's own key form: a deposit's subtypes are
                    // bare ("Iron") while NodeYieldEntry.SubTypeId is the inventory key ("Ore:Iron"),
                    // and every consumer matches on that key.
                    SubTypeId = WorldModel.ApplyInvKeyPrefix(k, d.SubTypeId),
                    ItemKind  = k.ToString(),
                    Weight    = d.Weight / kindSum,
                    MineOnly  = false,
                });
            }

            return entries.ToArray();
        }
    }
}
