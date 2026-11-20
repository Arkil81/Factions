using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Whether a station may post a buy order for an item, and for how much.
    ///
    /// <para>A station buys what its faction needs. Need is a faction property — which of its
    /// stations a player delivers to is logistics — so both scopes of <c>Item:</c> demand qualify:
    /// a node-scoped entry (a recipe input, a ship BOM shortfall, per-node surplus intent) and a
    /// faction-scalar one (an install BOM, a founding cost, catalogue demand). A demand pinned to a
    /// <b>different</b> node never qualifies: that need is owed there, not here.</para>
    ///
    /// <para><b>No demand means no listing.</b> The rule this replaced fell back to
    /// <see cref="FactionTuning.ReferenceStockFor"/> — a flat per-<see cref="ItemKind"/> constant —
    /// whenever no node-scoped demand existed. That could not distinguish two items of the same
    /// kind, and it fires whenever stock is under the constant, which for a station holding a
    /// handful of the world's inventory keys is nearly always. The result was every item on the
    /// faction type's <c>OrdersList</c> listed at nearly every station, including items nothing in
    /// the world had ever demanded.</para>
    /// </summary>
    public static class StationBuyDemand
    {
        /// <summary>Kind prefix for the concrete-item demands a buy order answers.</summary>
        public const string ItemPrefix = "Item:";

        /// <summary>
        /// Outstanding quantity the faction wants of <paramref name="invKey"/>, as seen from
        /// <paramref name="nodeId"/>. False when nothing live demands it, which is the caller's
        /// signal not to list it at all.
        ///
        /// <para>A node-scoped entry wins over a faction-scalar one when both exist: it is the more
        /// precise, currently-relevant signal, and it is what the old node-only lookup returned.
        /// Expiry is checked here rather than relied on from <see cref="DemandRegister.Prune"/>,
        /// which runs on the manager tick while stores refresh far more often — now that this read
        /// decides whether an item lists at all, a lapsed need must stop listing it immediately.</para>
        /// </summary>
        public static bool TryGetBuyQuantity(
            DemandRegister demand, string factionTag, string nodeId, string invKey,
            DateTime utcNow, out long quantity)
        {
            quantity = 0;
            if (demand == null || string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(invKey))
                return false;

            long factionWide = 0;
            foreach (DemandEntry d in demand.GetFor(factionTag, ItemPrefix + invKey))
            {
                if (d.ExpiresUtc <= utcNow || d.Quantity <= 0) continue;

                if (string.IsNullOrEmpty(d.NodeId))
                {
                    // Faction-scalar: qualifies, but keep looking for a node-scoped entry.
                    if (d.Quantity > factionWide) factionWide = d.Quantity;
                    continue;
                }

                if (!string.Equals(d.NodeId, nodeId, StringComparison.Ordinal)) continue;

                quantity = d.Quantity;   // the precise signal; nothing beats it
                return true;
            }

            quantity = factionWide;
            return factionWide > 0;
        }

        /// <summary>
        /// Every inventory key the faction has a live <c>Item:</c> demand for that
        /// <paramref name="nodeId"/> can answer — node-scoped there, or faction-scalar: the scope
        /// <see cref="TryGetBuyQuantity"/> accepts. Distinct, in ordinal order, so a store list
        /// reads the same between refreshes.
        ///
        /// <para>These join the faction type's <c>OrdersList</c> as buy candidates. The list is
        /// flavour and demand is need; with the list as the whole candidate set, a need it did not
        /// name could never be bought — ore on four of five types (FAC-157), a Farm's missing seed
        /// (FAC-486).</para>
        /// </summary>
        public static List<string> DemandedKeys(
            DemandRegister demand, string factionTag, string nodeId, DateTime utcNow)
        {
            var keys = new List<string>();
            if (demand == null || string.IsNullOrEmpty(factionTag)) return keys;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DemandEntry d in demand.SnapshotItemDemands(factionTag))
            {
                if (d.ExpiresUtc <= utcNow || d.Quantity <= 0) continue;
                if (!string.IsNullOrEmpty(d.NodeId)
                 && !string.Equals(d.NodeId, nodeId, StringComparison.Ordinal)) continue;

                string invKey = d.Kind.Substring(ItemPrefix.Length);
                if (invKey.Length > 0 && seen.Add(invKey)) keys.Add(invKey);
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
    }
}
