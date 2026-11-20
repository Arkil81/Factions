namespace Factions.Core
{
    /// <summary>
    /// What a node looks like to a player, derived at send time.
    ///
    /// <para>The client used to be handed a <c>NodeKind</c> ordinal, and that byte was its only
    /// signal about what a node holds — deposits never crossed the wire. Retiring the enum therefore
    /// meant deciding what a player sees instead, and the answer is the same one the rest of FAC-102
    /// gives: what is actually under the node, and what has been built on it.</para>
    ///
    /// <para>Derived on demand and never stored. There is no display state to keep in sync.</para>
    /// </summary>
    public static class NodeDisplay
    {
        /// <summary>The bare subtype of the ore dominating this node's deposit ("Iron", "Ice",
        /// "Platinum"), or empty when there is no ore under it. Empty is the honest answer for both
        /// "sampled and barren" and "never sampled" — neither is known to hold anything.</summary>
        public static string DominantOre(SpatialNode n)
        {
            if (!NodeContent.IsMineable(n)) return "";
            return DepositModel.DominantOre(n.Deposit) ?? "";
        }

        /// <summary>Whether this node's ground has been sampled at all — the distinction an empty
        /// <see cref="DominantOre"/> would otherwise collapse.
        /// <para>A deposit exists once the seam has answered, barren or not; a node the seam declined
        /// for, or that no pass has reached yet, has none. "Sampled and empty" and "never looked at"
        /// drive opposite decisions, and <see cref="IOreGeography"/> is explicit that they are
        /// different answers.</para></summary>
        public static bool Surveyed(SpatialNode n)
        {
            return n != null && n.Deposit != null;
        }

        /// <summary>This node's installed capabilities as one bit per <see cref="NodeCapabilityKind"/>
        /// ordinal, for the map — which sends every node the viewer knows and needs only enough to
        /// badge a marker. The assets and node-detail payloads carry the named list instead.
        /// <para>Only levels above zero set a bit: a capability recorded at level 0 is not
        /// installed.</para></summary>
        public static ushort CapabilityMask(SpatialNode n)
        {
            if (n == null || n.Capabilities == null) return 0;

            int mask = 0;
            foreach (var entry in n.Capabilities)
            {
                if (entry.Value <= 0) continue;
                int bit = (int)entry.Key;
                // Sixteen bits on the wire: a kind past ordinal 15 is absent from the badge until
                // the field is widened again, which is a protocol change on both sides.
                if (bit < 0 || bit > 15) continue;
                mask |= 1 << bit;
            }
            return (ushort)mask;
        }
    }
}
