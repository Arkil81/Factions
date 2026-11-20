using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Maps SE faction types (Miner, Trader, Builder, Military, Pirate) to the invKey-format
    /// item lists defined in <c>FactionTypes.sbc</c>.
    ///
    /// <para>Used by <see cref="ProductionManager"/> to post standing catalog demands so each
    /// faction continuously produces the items its type is supposed to offer. A missing item in
    /// the store means a missing production queue, not a missing store entry.</para>
    /// </summary>
    public interface IFactionTypeCatalogue
    {
        /// <summary>Maps an SE faction tag to its type name ("Miner", "Trader", etc.).</summary>
        bool TryGetFactionType(string factionTag, out string factionType);

        /// <summary>
        /// InvKey-format item list for the given faction type's <c>OffersList</c> — the items
        /// this type is supposed to produce and sell. Returns an empty list for unknown types.
        /// </summary>
        IReadOnlyList<string> OfferInvKeys(string factionType);

        /// <summary>
        /// InvKey-format item list for the given faction type's <c>OrdersList</c> — the items
        /// this type wants to buy from players. Returns an empty list for unknown types.
        /// </summary>
        IReadOnlyList<string> OrderInvKeys(string factionType);
    }
}
