using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The other half of the litre/kilolitre boundary: a gas price is quoted per kilolitre, the unit
    /// SE quotes <c>MinimumHydrogenPrice</c> in and the unit a store row sells, while gas stock is
    /// counted in litres. Multiplying one by the other at a call site is a thousand times the
    /// credits, and both numbers are individually plausible.
    /// </summary>
    public class ItemCreditsTests
    {
        [Fact]
        public void AGasHoldingIsWorthItsKilolitres()
        {
            // 20,000 litres at 300 credits the kilolitre is 20 kL -> 6,000 credits, not 6,000,000.
            Assert.Equal(6000, ItemCredits.For("Gas:Hydrogen", 20_000, 300));
            Assert.Equal(6000, ItemCredits.For("Gas:Oxygen", 20_000, 300));
        }

        [Fact]
        public void EverythingElseIsWorthItsUnits()
        {
            Assert.Equal(1_000_000, ItemCredits.For("Ore:Ice", 20_000, 50));
            Assert.Equal(2860, ItemCredits.For("Ingot:Iron", 20, 143));
            // A bottle is not gas: it is priced and counted per bottle.
            Assert.Equal(300, ItemCredits.For("HydrogenBottle", 1, 300));
        }

        [Fact]
        public void APartKilolitreIsValuedProportionally_ThoughItCannotBeListed()
        {
            // Valuation is not listing: a node holding 999 litres owns 299 credits of hydrogen even
            // though PricedUnits refuses to put a row on the shelf for it.
            Assert.Equal(299, ItemCredits.For("Gas:Hydrogen", 999, 300));
            Assert.Equal(0, ItemCredits.PricedUnits("Gas:Hydrogen", 999));
            Assert.Equal(300, ItemCredits.For("Gas:Hydrogen", 1000, 300));
        }

        [Fact]
        public void TheKindReadingAgreesWithTheKeyReading()
        {
            Assert.Equal(ItemCredits.For("Gas:Hydrogen", 20_000, 300),
                         ItemCredits.For(ItemKind.Hydrogen, 20_000, 300));
            Assert.Equal(ItemCredits.For("Gas:Oxygen", 20_000, 300),
                         ItemCredits.For(ItemKind.Oxygen, 20_000, 300));
            Assert.Equal(ItemCredits.For("Ore:Iron", 20_000, 300),
                         ItemCredits.For(ItemKind.Ore, 20_000, 300));
        }

        [Fact]
        public void AffordableIsTheInverse()
        {
            Assert.Equal(20_000, ItemCredits.Affordable("Gas:Hydrogen", 6000, 300));
            Assert.Equal(20, ItemCredits.Affordable("Ingot:Iron", 6000, 300));
            Assert.Equal(0, ItemCredits.Affordable("Gas:Hydrogen", 299, 300));
        }

        [Fact]
        public void AnUncappedBudgetDoesNotOverflowIntoARefusal()
        {
            // Trade settlement passes long.MaxValue for "no bound". Scaling that by a thousand
            // before dividing wraps negative, and a negative affordable refuses every row.
            Assert.True(ItemCredits.Affordable("Gas:Hydrogen", long.MaxValue, 300) > 0);
            Assert.True(ItemCredits.Affordable("Ore:Iron", long.MaxValue, 300) > 0);
        }

        [Fact]
        public void PricedUnitsRestatesLitresAsKilolitres()
        {
            Assert.Equal(20, ItemCredits.PricedUnits("Gas:Hydrogen", 20_000));
            Assert.Equal(0, ItemCredits.PricedUnits("Gas:Hydrogen", 999));
            Assert.Equal(20_000, ItemCredits.PricedUnits("Ore:Ice", 20_000));
        }

        [Fact]
        public void APriceOfZeroIsWorthNothing_SoACallerCanRejectTheRow()
        {
            Assert.Equal(0, ItemCredits.For("Gas:Hydrogen", 20_000, 0));
            Assert.Equal(0, ItemCredits.For("Gas:Hydrogen", 20_000, -1));
            Assert.Equal(0, ItemCredits.Affordable("Gas:Hydrogen", 6000, 0));
        }
    }
}
