using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One demand as a snapshot reader sees it: the entry, plus the two numbers the
    /// ranking already had to compute to place it.
    ///
    /// <para><b>Why the score and the stock ride along.</b> Both are a node lookup and two
    /// dictionary reads away, and the ranking pays that cost for every row already. A payload
    /// builder that recomputed them would be a second reading of the same question, free to
    /// disagree with the order the rows arrived in — the thing <see cref="DemandUrgency"/> being one
    /// function exists to prevent.</para></summary>
    public struct DemandSnapshotRow
    {
        /// <summary>The demand itself.</summary>
        public DemandEntry Demand;

        /// <summary>Its <see cref="DemandUrgency.Score"/>, or 0 for a kind that names no item.</summary>
        public double Urgency;

        /// <summary>What the demanding node holds of the key, or 0 when the row names no item, the
        /// node is gone, or the node holds none. Read beside <see cref="Scored"/>, which is what
        /// separates "holds none" from "not applicable".</summary>
        public long HaveAtNode;

        /// <summary>True when this row names an item at a live node, so
        /// <see cref="Urgency"/>/<see cref="HaveAtNode"/> are measurements rather than defaults.
        /// False for <c>Ship:</c>/<c>Position:</c> rows and for a node that no longer exists.</summary>
        public bool Scored;
    }

    /// <summary>
    /// Orders a demand snapshot for a reader — most urgent first, on the same
    /// <see cref="DemandUrgency"/> scale the dispatcher itself ranks by.
    ///
    /// <para><b>Why this is not a sort of the register.</b> <see cref="DemandRegister"/> keeps its
    /// entries in one list whose indexes are the values of an id→index map
    /// (<c>_indexes</c>), and <c>Post</c> keeps a refreshed entry at its original index. Reordering
    /// that list in place would invalidate every stored index. The register's order is an
    /// implementation detail of its own lookup, not a priority, so the ordering belongs at the
    /// snapshot boundary instead.</para>
    ///
    /// <para><b>What it buys.</b> The snapshot a dashboard receives is capped per kind (a faction's
    /// demand set runs to thousands of rows), and before this the surviving rows were simply the
    /// oldest-posted ones — so a burning shortfall could be absent from the panel entirely while a
    /// long-satisfied catalogue want was shown. Sorting first means the cap keeps the rows worth
    /// looking at.</para>
    ///
    /// <para>Stable, and rows that carry no urgency (<c>Ship:</c> and <c>Position:</c> demands have
    /// no <see cref="DemandEntry.Quantity"/>, so they score 0) keep the order they arrived in. They
    /// are bucketed by kind downstream, so they are never interleaved with item rows in the output
    /// regardless.</para>
    /// </summary>
    public static class DemandSnapshotOrder
    {
        /// <summary>Ranks <paramref name="demands"/> most urgent first and returns them with the
        /// score and node stock each row was placed by. The input list is not modified — the
        /// returned rows are the snapshot.</summary>
        public static List<DemandSnapshotRow> RankForSnapshot(List<DemandEntry> demands,
            IWorldView world, FactionTuning tuning)
        {
            var rows = new List<DemandSnapshotRow>(demands != null ? demands.Count : 0);
            if (demands == null || demands.Count == 0) return rows;

            // Scored once per row, never inside the comparator: the comparator runs O(n log n)
            // times and each score costs a node lookup plus two dictionary reads. MatchVectors
            // already paid for that exact mistake once, with DistanceTo.
            var scored = new List<(DemandSnapshotRow Row, int Index)>(demands.Count);
            for (int i = 0; i < demands.Count; i++)
                scored.Add((Measure(demands[i], world, tuning), i));

            if (scored.Count > 1)
                scored.Sort((a, b) =>
                {
                    int cmp = b.Row.Urgency.CompareTo(a.Row.Urgency);
                    return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
                });

            for (int i = 0; i < scored.Count; i++) rows.Add(scored[i].Row);
            return rows;
        }

        /// <summary>Score and node stock for one row. A kind that names no item (<c>Ship:</c>,
        /// <c>Position:</c>), a node that no longer exists, or a null world all read as unscored.
        /// One node lookup serves the stock, the history and the score.</summary>
        private static DemandSnapshotRow Measure(DemandEntry d, IWorldView world, FactionTuning tuning)
        {
            var row = new DemandSnapshotRow { Demand = d };
            if (world == null || d.Quantity <= 0 || d.NodeId == null) return row;
            string key = KeyOf(d);
            if (key == null) return row;

            SpatialNode node;
            if (!world.Graph.Nodes.TryGetValue(d.NodeId, out node)) return row;

            long have;
            node.Inventory.TryGetValue(key, out have);
            double history;
            node.DemandHistory.TryGetValue(key, out history);

            row.Scored = true;
            row.HaveAtNode = have;
            row.Urgency = DemandUrgency.Score(d, have, history, world.UtcNow, tuning, FuelDraw.ParkedHulls(node));
            return row;
        }

        /// <summary>The inventory key a demand is about, or null for a kind that names no item.</summary>
        private static string KeyOf(DemandEntry d)
        {
            if (d.Kind == null) return null;
            if (d.Kind.StartsWith(DemandRegister.ItemPrefix, StringComparison.Ordinal))
                return d.Kind.Substring(DemandRegister.ItemPrefix.Length);
            if (d.Kind.StartsWith(DemandRegister.NeedPrefix, StringComparison.Ordinal))
                return d.Kind.Substring(DemandRegister.NeedPrefix.Length);
            return null;
        }
    }
}
