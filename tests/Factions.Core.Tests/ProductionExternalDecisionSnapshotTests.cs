using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionCatalogueSnapshotTests
    {
        [Fact]
        public void Capture_FreezesFactionTypeAndOfferList()
        {
            var catalogue = new MutableCatalogue();
            catalogue.Types["FAC"] = "Miner";
            catalogue.Offers["Miner"] = new List<string> { "Comp:SteelPlate" };

            ProductionCatalogueSnapshot snapshot = ProductionCatalogueSnapshot.Capture(catalogue, "FAC");

            catalogue.Types["FAC"] = "Trader";
            catalogue.Offers["Miner"].Add("Comp:InteriorPlate");

            string factionType;
            Assert.True(snapshot.TryGetFactionType(out factionType));
            Assert.Equal("Miner", factionType);
            Assert.Equal(new[] { "Comp:SteelPlate" }, snapshot.OfferInvKeys);
        }

        private sealed class MutableCatalogue : IFactionTypeCatalogue
        {
            public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
            public readonly Dictionary<string, List<string>> Offers = new Dictionary<string, List<string>>();

            public bool TryGetFactionType(string factionTag, out string factionType)
            {
                return Types.TryGetValue(factionTag, out factionType);
            }

            public IReadOnlyList<string> OfferInvKeys(string factionType)
            {
                List<string> offers;
                return Offers.TryGetValue(factionType, out offers) ? offers : null;
            }

            public IReadOnlyList<string> OrderInvKeys(string factionType) { return null; }
        }
    }
}
