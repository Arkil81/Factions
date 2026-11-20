using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Factions.Core
{
    /// <summary>
    /// Immutable faction-catalogue answers needed by a staged Production cycle. PriceOracle cannot
    /// be captured here: its answers depend on same-cycle inventory changes and must be evaluated
    /// against the private transaction overlay instead of the live world.
    /// </summary>
    public sealed class ProductionCatalogueSnapshot
    {
        private static readonly IReadOnlyList<string> s_emptyOffers =
            new ReadOnlyCollection<string>(new List<string>());

        private readonly bool _hasFactionType;
        private readonly string _factionType;
        private readonly IReadOnlyList<string> _offerInvKeys;

        private ProductionCatalogueSnapshot(bool hasFactionType, string factionType,
                                            IReadOnlyList<string> offerInvKeys)
        {
            _hasFactionType = hasFactionType;
            _factionType = factionType;
            _offerInvKeys = offerInvKeys;
        }

        public IReadOnlyList<string> OfferInvKeys { get { return _offerInvKeys; } }

        public static ProductionCatalogueSnapshot Capture(IFactionTypeCatalogue catalogue, string factionTag)
        {
            if (catalogue == null)
                return new ProductionCatalogueSnapshot(false, null, null);

            string factionType;
            if (!catalogue.TryGetFactionType(factionTag, out factionType))
                return new ProductionCatalogueSnapshot(false, null, null);

            return new ProductionCatalogueSnapshot(true, factionType,
                CopyOffers(catalogue.OfferInvKeys(factionType)));
        }

        public bool TryGetFactionType(out string factionType)
        {
            factionType = _factionType;
            return _hasFactionType;
        }

        private static IReadOnlyList<string> CopyOffers(IReadOnlyList<string> source)
        {
            if (source == null) return null;
            if (source.Count == 0) return s_emptyOffers;
            var copy = new List<string>(source.Count);
            for (int i = 0; i < source.Count; i++) copy.Add(source[i]);
            return new ReadOnlyCollection<string>(copy);
        }
    }
}
