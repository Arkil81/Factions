using System;

namespace Factions.Core
{
    /// <summary>
    /// The single answer to "does this faction keep a player store at all?", shared by every side of
    /// the store: <see cref="StoreStockManager"/> (which builds the hulls), and the host's
    /// <c>StationStoreManager</c> (which shelves and prices them). Gating only one side leaves the
    /// other half working — a faction that stops building still sells what it already holds, and a
    /// faction that stops selling still burns its yard's throughput stocking a shelf nobody sees.
    /// </summary>
    public static class FactionStorePolicy
    {
        /// <summary>True when <paramref name="factionTag"/> may offer, order and list ships to
        /// players.
        ///
        /// <para>False only when a catalogue exists <b>and</b> resolves the tag to a type named in
        /// <see cref="FactionTuning.StoreExcludedFactionTypes"/>. A bare lookup miss is never
        /// enough: a world with no faction-type catalogue has every tag unresolved, and there the
        /// inventory-threshold fallback in <see cref="StationListings"/> is the intended behaviour
        /// rather than an accident. Reading a miss as "no type, so no store" would silently close
        /// every store in such a world.</para></summary>
        public static bool HasPlayerStore(IFactionTypeCatalogue catalogue, FactionTuning tuning, string factionTag)
        {
            if (catalogue == null || tuning == null || string.IsNullOrEmpty(factionTag)) return true;

            string[] excluded = tuning.StoreExcludedFactionTypes;
            if (excluded == null || excluded.Length == 0) return true;

            string factionType;
            if (!catalogue.TryGetFactionType(factionTag, out factionType)) return true;
            if (string.IsNullOrEmpty(factionType)) return true;

            for (int i = 0; i < excluded.Length; i++)
                if (string.Equals(excluded[i], factionType, StringComparison.OrdinalIgnoreCase))
                    return false;

            return true;
        }

    }
}
