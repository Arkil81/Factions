namespace Factions.Core
{
    /// <summary>
    /// The one place litres of gas become SE store units and back.
    ///
    /// <para>SE prices and delivers gas in <b>kilolitres</b>: <c>MyStoreBlock.BuyGasInternal</c>
    /// opens with <c>float num = (float)amount * 1000f</c>, so a store row's <c>Amount</c> of 1 is
    /// 1000 litres delivered into the buyer's tank. This economy counts gas in litres, because that
    /// is the unit SE's own <c>IceToGasRatio</c> is quoted in. Every crossing of that boundary goes
    /// through here rather than through a literal 1000 at a call site.</para>
    ///
    /// <para>A gas <b>price</b> does not cross it at all: it is quoted per kilolitre, the unit SE
    /// quotes <c>MinimumHydrogenPrice</c> in, because a credit per litre cannot be expressed in the
    /// integer the price catalogue carries. <see cref="ItemCredits"/> is where a litre count meets
    /// that price.</para>
    /// </summary>
    public static class GasStoreUnits
    {
        /// <summary>Litres in one SE gas store unit.</summary>
        public const long LitresPerStoreUnit = 1000;

        /// <summary>Store units a node's litre stock can fill, rounded <b>down</b>.
        ///
        /// <para>A holding under one kilolitre yields 0 and must not be listed: SE would take the
        /// row's price for an <c>Amount</c> the buyer cannot draw a unit from.</para></summary>
        public static long ToStoreUnits(long litres)
        {
            return litres <= 0 ? 0 : litres / LitresPerStoreUnit;
        }

        /// <summary>Litres a store transaction of <paramref name="storeUnits"/> actually moved —
        /// what the sim must debit, since node inventory is in litres.</summary>
        public static long ToLitres(long storeUnits)
        {
            return storeUnits <= 0 ? 0 : storeUnits * LitresPerStoreUnit;
        }
    }
}
