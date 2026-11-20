using System;

namespace Factions.Core
{
    /// <summary>
    /// What a player standing at a node their own faction holds may move in and out of its abstract
    /// <see cref="SpatialNode.Inventory"/>, and the two mutations that do it.
    ///
    /// <para>The arithmetic lives here rather than in the host because it is a rule, not a
    /// presentation detail. The listing a client is offered and the amount a transfer actually moves
    /// have to be the same number computed the same way, or a player is shown stock the server then
    /// refuses — and the host half cannot be unit-tested at all.</para>
    ///
    /// <para>No money moves through any of this and no ledger is touched. A node's stock is already
    /// the owning faction's; carrying it to a physical inventory is a change of representation, not a
    /// sale.</para>
    /// </summary>
    public static class NodeTransfer
    {
        /// <summary>How much of <paramref name="invKey"/> may be taken out of the node right now.
        ///
        /// <para><see cref="SpatialNode.BuildQueueReserved"/> is subtracted because its own contract
        /// requires it: the outstanding BOM shortfall of an active build is stock the shipyard will
        /// absorb on its next tick, and a consumer that spends it wins a race it is not allowed to
        /// enter.</para>
        ///
        /// <para><see cref="SpatialNode.ReservedInventory"/> is <b>not</b> subtracted, and that is
        /// the one subtlety here. Install-reserved stock has already been moved out of
        /// <see cref="SpatialNode.Inventory"/> — the two dictionaries are disjoint, which is why
        /// <c>InstallReserveFloor</c> nets one against the other and <c>DevelopReadiness</c> sums
        /// them. Subtracting it again would withhold a second, imaginary copy of it and quietly hide
        /// stock nothing has claimed. It is reported through <see cref="Reserved"/> instead, so the
        /// player still sees where it went.</para></summary>
        public static long Withdrawable(SpatialNode node, string invKey, DateTime nowUtc)
        {
            if (node == null || string.IsNullOrEmpty(invKey)) return 0;
            long have;
            if (!node.Inventory.TryGetValue(invKey, out have) || have <= 0) return 0;
            long routable = have - node.BuildQueueReserved(invKey, nowUtc);
            return routable > 0 ? routable : 0;
        }

        /// <summary>Everything held back from <paramref name="invKey"/>: the active build queue's
        /// outstanding claim on routable stock, plus what a capability install has already staged out
        /// of it. One number because the player is asking one question — why can I not take all of
        /// it — and the two answers are the same answer.</summary>
        public static long Reserved(SpatialNode node, string invKey, DateTime nowUtc)
        {
            if (node == null || string.IsNullOrEmpty(invKey)) return 0;
            long have;
            node.Inventory.TryGetValue(invKey, out have);

            // Only the part of the build claim that routable stock could actually satisfy. A build
            // wanting 500 of a key the node holds 100 of is short by 400 the node does not have, and
            // reporting the shortfall as "reserved" would read as stock standing in the station.
            long queued = node.BuildQueueReserved(invKey, nowUtc);
            if (queued > have) queued = have;
            if (queued < 0) queued = 0;

            long staged;
            node.ReservedInventory.TryGetValue(invKey, out staged);
            if (staged < 0) staged = 0;
            return queued + staged;
        }

        /// <summary>Whether a key may be credited INTO a node by hand.
        ///
        /// <para>Ore, ingots, components and seeds — the families the economy actually consumes. A
        /// seed is what lets a farm grow a crop its body does not (docs/food-and-population.md).
        /// <see cref="InventoryKeyMapper.FromTypeIdAndSubtype"/> falls through to a bare subtype for
        /// everything else, so an ammo box or a hand tool would enter node stock as a channel no
        /// recipe, upkeep line or BOM ever draws from: a row that reads as wealth and buys nothing.
        /// Gas fails this too, and would fail on the way back out regardless — a gas key has no
        /// <c>MyDefinitionId</c> to materialise from.</para>
        ///
        /// <para>Withdrawal is deliberately not filtered this way. Anything already in a node's stock
        /// is the faction's, and taking it out cannot create a channel that did not exist.</para></summary>
        public static bool IsDepositable(string invKey)
        {
            if (string.IsNullOrEmpty(invKey)) return false;
            return invKey.StartsWith("Ore:", StringComparison.Ordinal)
                || invKey.StartsWith("Ingot:", StringComparison.Ordinal)
                || invKey.StartsWith("Comp:", StringComparison.Ordinal)
                || InventoryKeyMapper.IsSeedKey(invKey);
        }

        /// <summary>Removes up to <paramref name="amount"/> of <paramref name="invKey"/> from the
        /// node, never more than <see cref="Withdrawable"/> allows, and returns what was actually
        /// taken.
        ///
        /// <para>Called by the host with what physically landed in the target inventory, not with
        /// what the player asked for: a hold that fills up mid-transfer must leave the remainder
        /// standing in the node rather than losing it. The clamp is re-applied here anyway so the
        /// invariant holds for any caller.</para></summary>
        public static long TakeFromNode(SpatialNode node, string invKey, long amount, DateTime nowUtc)
        {
            if (node == null || string.IsNullOrEmpty(invKey) || amount <= 0) return 0;
            long allowed = Withdrawable(node, invKey, nowUtc);
            long taken = amount < allowed ? amount : allowed;
            if (taken <= 0) return 0;

            long have;
            node.Inventory.TryGetValue(invKey, out have);
            long remaining = have - taken;
            // The key is dropped rather than left at zero: every reader of this dictionary treats an
            // absent key and a zero the same way, and an empty row on a node's stock listing is one
            // more line between a player and the stock they are looking for.
            if (remaining > 0) node.Inventory[invKey] = remaining;
            else node.Inventory.Remove(invKey);
            return taken;
        }

        /// <summary>Credits <paramref name="amount"/> of <paramref name="invKey"/> to the node's
        /// routable stock. Called by the host with what was actually removed from the physical
        /// inventory, so the two sides of a deposit balance even when the removal came up short.</summary>
        public static void CreditNode(SpatialNode node, string invKey, long amount)
        {
            if (node == null || string.IsNullOrEmpty(invKey) || amount <= 0) return;
            long have;
            node.Inventory.TryGetValue(invKey, out have);
            node.Inventory[invKey] = have + amount;
        }
    }
}
