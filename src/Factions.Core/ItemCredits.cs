namespace Factions.Core
{
    /// <summary>
    /// Units of an item multiplied by its price, in the unit that item is priced in.
    ///
    /// <para>Every item but gas is priced per inventory unit, so the product is the plain one. Gas
    /// is priced per <b>kilolitre</b> — the unit SE quotes <c>MinimumHydrogenPrice</c> in and the
    /// unit a store row sells — while gas inventory counts litres, because that is the unit SE's own
    /// <c>IceToGasRatio</c> is quoted in. A litre count multiplied by a kilolitre price is a thousand
    /// times the credits it is worth, so every site where a quantity meets a price goes through
    /// here rather than multiplying at the call site.</para>
    /// </summary>
    public static class ItemCredits
    {
        /// <summary>Inventory units in one priced unit of <paramref name="invKey"/>: a thousand for
        /// gas, one for everything else.</summary>
        public static long UnitsPerPricedUnit(string invKey)
        {
            return InventoryKeyMapper.IsGasKey(invKey) ? GasStoreUnits.LitresPerStoreUnit : 1;
        }

        /// <summary>The kind-shaped reading, for the valuation paths that hold an
        /// <see cref="ItemKind"/> rather than an inventory key.</summary>
        public static long UnitsPerPricedUnit(ItemKind kind)
        {
            return kind == ItemKind.Hydrogen || kind == ItemKind.Oxygen
                ? GasStoreUnits.LitresPerStoreUnit : 1;
        }

        /// <summary>Inventory units expressed in the unit the item is priced and listed in: litres
        /// of gas become kilolitres, everything else is unchanged. Rounded down, so a holding under
        /// one kilolitre yields 0 — which a store row must not carry, since SE would take the row's
        /// price for an amount the buyer cannot draw a unit from.</summary>
        public static long PricedUnits(string invKey, long inventoryUnits)
        {
            if (inventoryUnits <= 0) return 0;
            long per = UnitsPerPricedUnit(invKey);
            return per == 1 ? inventoryUnits : inventoryUnits / per;
        }

        /// <summary>Credits <paramref name="units"/> of <paramref name="invKey"/> are worth at
        /// <paramref name="unitPrice"/> per priced unit. Proportional, so 999 litres of gas is worth
        /// 999 thousandths of a kilolitre's price — valuing a holding is not listing it, and only
        /// <see cref="PricedUnits"/> refuses a part unit.</summary>
        public static long For(string invKey, long units, long unitPrice)
        {
            if (units == 0 || unitPrice <= 0) return 0;
            long per = UnitsPerPricedUnit(invKey);
            return per == 1 ? units * unitPrice : units * unitPrice / per;
        }

        /// <summary>Kind-shaped <see cref="For(string,long,long)"/>.</summary>
        public static long For(ItemKind kind, long units, long unitPrice)
        {
            if (units == 0 || unitPrice <= 0) return 0;
            long per = UnitsPerPricedUnit(kind);
            return per == 1 ? units * unitPrice : units * unitPrice / per;
        }

        /// <summary>The fractional reading, for the fuel paths that carry a burn in fractional
        /// litres and rank on it before anything is spent.</summary>
        public static double For(string invKey, double units, long unitPrice)
        {
            if (units == 0 || unitPrice <= 0) return 0;
            long per = UnitsPerPricedUnit(invKey);
            return per == 1 ? units * unitPrice : units * unitPrice / per;
        }

        /// <summary>Inventory units <paramref name="credits"/> buys at <paramref name="unitPrice"/>
        /// per priced unit — the inverse of <see cref="For(string,long,long)"/>, for the budget
        /// bounds that ask how much a purse reaches to.</summary>
        public static long Affordable(string invKey, long credits, long unitPrice)
        {
            if (credits <= 0 || unitPrice <= 0) return 0;
            long per = UnitsPerPricedUnit(invKey);
            // Divide before multiplying: an uncapped budget is long.MaxValue, and scaling that by a
            // thousand first overflows to a negative "affordable".
            long pricedUnits = credits / unitPrice;
            if (per == 1) return pricedUnits;
            return pricedUnits > long.MaxValue / per ? long.MaxValue : pricedUnits * per;
        }
    }
}
