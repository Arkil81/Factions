using System;

namespace Factions.Core
{
    /// <summary>Why a station withholds part of its stock from sale. The wire mirrors it as
    /// <c>Factions.Wire.WireStockKeptFor</c>, ordinal for ordinal.</summary>
    public enum StockKeptFor : byte
    {
        /// <summary>Nothing withheld.</summary>
        None = 0,
        /// <summary>The target's <c>owed</c> term: ship and install bills owed here.</summary>
        Owed = 1,
        /// <summary>The <c>flow</c> term: what the station consumed recently, fuel drawn by
        /// departing hulls included.</summary>
        Use = 2,
        /// <summary>No longer produced: a store does not hold stock back for its own sales
        /// (<see cref="IOfferKeepBack"/>). Kept so the wire ordinals do not move.</summary>
        Sales = 3,
        /// <summary>The floor every gas holder keeps.</summary>
        GasFloor = 4,
        /// <summary>The per-kind floor, for an item something here wants.</summary>
        Floor = 5,
        /// <summary>Above the target, but more than one store row may carry.</summary>
        ListingCap = 6,
        /// <summary>The store carries no row for this item at all.</summary>
        NotTraded = 7,
        /// <summary>The station has no store.</summary>
        NoStore = 8,
    }

    /// <summary>
    /// Names the reason for the gap between what a station holds and what its store offers.
    ///
    /// <para>Reads the offer the store actually built (<see cref="StationListings"/>) rather than
    /// re-deriving it, so the station window can never claim stock is for sale that a refuel at the
    /// same terminal then refuses. Only the label is derived: the offer is short of the holding
    /// because the holding is under what the store keeps back
    /// (<see cref="PriceOracle.TargetBreakdown.KeepBack"/>; its term says which need set it), or
    /// because it is over that and the listing cap bound, or because no row exists at all.</para>
    /// </summary>
    public static class StockAvailability
    {
        /// <param name="held">Routable stock, inventory units.</param>
        /// <param name="forSale">The store's offer for the item, inventory units; 0 with no row.</param>
        /// <param name="hasStore">Whether the station runs a player store.</param>
        /// <param name="listed">Whether the store carries an offer row for the item.</param>
        /// <param name="target">The station's target breakdown for the item; its keep-back is read.</param>
        /// <param name="unitsPerPricedUnit">Inventory units per listed unit — 1000 for gas, whose
        /// rows are whole kilolitres. A shortfall under one listed unit is rounding, not a hold.</param>
        public static StockKeptFor Classify(long held, long forSale, bool hasStore, bool listed,
            PriceOracle.TargetBreakdown target, long unitsPerPricedUnit)
        {
            if (held <= 0) return StockKeptFor.None;
            if (!hasStore) return StockKeptFor.NoStore;
            if (held - forSale < Math.Max(1L, unitsPerPricedUnit)) return StockKeptFor.None;

            // Under its keep-back the store holds back the shortfall's share, and nothing at all
            // below StationListings.OfferThreshold: the need that set the keep-back is the reason.
            if (target.KeepBack > 0 && held < target.KeepBack) return FromTerm(target.KeepBackTerm);
            return listed ? StockKeptFor.ListingCap : StockKeptFor.NotTraded;
        }

        private static StockKeptFor FromTerm(string term)
        {
            switch (term)
            {
                case "owed": return StockKeptFor.Owed;
                case "flow": return StockKeptFor.Use;
                case "gas": return StockKeptFor.GasFloor;
                case "wanted":
                case "bankglut": return StockKeptFor.Floor;
                default: return StockKeptFor.None;
            }
        }
    }
}
