using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// How much of a faction-wide need one node owes — a need no single node is on the hook for,
    /// such as a capability install's BOM, payable from wherever the faction holds the material.
    ///
    /// <para>Splitting it evenly across capability-bearing nodes is wrong on its own, because the
    /// need spreads while the stock concentrates: only some of a faction's industrial nodes actually
    /// produce a given component, so an even split halves the protection at the one node holding all
    /// of it. The share is therefore whichever is larger of this node's proportion of the faction's
    /// stock and an even split — a node holding all of it owes all of it, and a node holding none
    /// still keeps a reason to accumulate rather than reading its first units as surplus.</para>
    ///
    /// <para>Two readers share this and they must agree, which is why it is one function.
    /// <see cref="PriceOracle"/> uses it to decide how much of an item a node is committed to
    /// holding before the rest counts as surplus; <c>ProductionManager.BuildChainRequirements</c>
    /// uses it to place an install BOM's root on the nodes that could produce it. A node told it
    /// owes a share for pricing and a different share for production would be incoherent.</para>
    /// </summary>
    public static class FactionNeedShare
    {
        /// <summary>One node's share of <paramref name="shared"/>, given what it holds
        /// (<paramref name="hereStock"/>) against the faction's whole stock of the same key and the
        /// number of capability-bearing nodes among which the need is split.</summary>
        public static long For(long shared, long hereStock, long factionStock, int holders)
        {
            if (shared <= 0) return 0;

            long evenShare = holders > 1 ? shared / holders : shared;
            if (factionStock <= 0 || hereStock <= 0) return evenShare;

            long byStock = (long)((double)shared * hereStock / factionStock);
            return byStock > evenShare ? byStock : evenShare;
        }

        /// <summary>How many of <paramref name="ownedNodes"/> carry any installed capability.
        /// Never returns 0 — a faction with a live need and no industry still has to gather it
        /// somewhere, and dividing by zero would erase the need entirely.</summary>
        public static int HolderCount(IEnumerable<SpatialNode> ownedNodes)
        {
            int count = 0;
            if (ownedNodes != null)
                foreach (SpatialNode n in ownedNodes)
                    if (HasAnyCapability(n)) count++;
            return count < 1 ? 1 : count;
        }

        /// <summary>True when <paramref name="node"/> has at least one capability installed at
        /// level 1 or above — the test behind <see cref="HolderCount"/>, exposed so a caller can ask
        /// it of a single node without recounting the network.</summary>
        public static bool HasAnyCapability(SpatialNode node)
        {
            if (node == null || node.Capabilities == null) return false;
            foreach (KeyValuePair<NodeCapabilityKind, int> cap in node.Capabilities)
                if (cap.Value >= 1) return true;
            return false;
        }
    }
}
