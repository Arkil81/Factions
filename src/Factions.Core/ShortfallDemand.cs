using System;

namespace Factions.Core
{
    /// <summary>Asks for a consumable a departure from a node went without, so the haul path moves
    /// it to where fleets actually leave from. Shared by fuel and ammunition, which are both drawn at
    /// departure and both fly short rather than wait.
    ///
    /// <para>Consumption alone cannot raise this: a node whose stock is empty hands nothing out, and
    /// what it hands out is all a consumption-fed signal sees. So each short key gets a
    /// <c>Need:</c> row for what was missing and an <c>Item:</c> row for the full quantity. The
    /// <c>Item:</c> row is gross, because <c>MatchVectors</c> nets the node's stock itself. Both are
    /// node-scoped at <see cref="DemandClass.Fuel"/>: supply a departing hull needs, ranked by the
    /// node's parked hulls. Each keeps the larger of its own quantity and expiry and any row already
    /// there, so a catalogue, store or reserve row under the same key is never lowered by a smaller
    /// shortfall, and an <c>Item:</c> row already asking for the full quantity is left untouched.</para></summary>
    public static class ShortfallDemand
    {
        /// <summary>Posts one key's shortfall rows. Returns false, having posted nothing, when there
        /// is no register, no node or nothing missing.</summary>
        public static bool Post(DemandRegister demand, string factionTag, string nodeId, string key,
                                long fullQuantity, long missing, DateTime expiresUtc)
        {
            if (demand == null || string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(key)) return false;
            if (missing <= 0) return false;

            DemandEntry need = DemandEntry.ForNeed(factionTag, key, nodeId, expiresUtc, missing, DemandClass.Fuel);
            demand.Post(AtLeastExisting(demand, need));

            // An Item: row already asking for as much is left as its poster wrote it; restamping
            // a catalogue row Fuel would lift its whole quantity's urgency for one departure.
            string itemKind = DemandRegister.ItemKind(key);
            DemandEntry priorItem;
            bool covered = demand.TryGet(factionTag, itemKind, nodeId, null, out priorItem)
                           && priorItem.Quantity >= fullQuantity;
            if (!covered)
                demand.Post(AtLeastExisting(demand, new DemandEntry
                {
                    FactionTag = factionTag,
                    Kind       = itemKind,
                    NodeId     = nodeId,
                    ExpiresUtc = expiresUtc,
                    Quantity   = fullQuantity,
                    Class      = DemandClass.Fuel,
                }));
            return true;
        }

        private static DemandEntry AtLeastExisting(DemandRegister demand, DemandEntry incoming)
        {
            DemandEntry prior;
            if (demand.TryGet(incoming.FactionTag, incoming.Kind, incoming.NodeId, null, out prior))
            {
                if (prior.Quantity > incoming.Quantity) incoming.Quantity = prior.Quantity;
                if (prior.ExpiresUtc > incoming.ExpiresUtc) incoming.ExpiresUtc = prior.ExpiresUtc;
            }
            return incoming;
        }
    }
}
