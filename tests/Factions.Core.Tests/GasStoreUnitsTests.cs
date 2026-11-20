using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The litre/kilolitre boundary between this economy and SE's store.
    /// <c>MyStoreBlock.BuyGasInternal</c> opens with <c>float num = (float)amount * 1000f</c>, so a
    /// store row's <c>Amount</c> of 1 delivers 1000 litres. Node inventory counts litres, because
    /// that is the unit SE's own <c>IceToGasRatio</c> is quoted in.
    /// </summary>
    public class GasStoreUnitsTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(-5, 0)]
        [InlineData(1, 0)]          // a litre is not a listing
        [InlineData(999, 0)]        // still under one kilolitre
        [InlineData(1000, 1)]
        [InlineData(1999, 1)]       // rounds DOWN: never offer a unit the buyer cannot draw
        [InlineData(1_350_000, 1350)]
        public void StoreUnitsRoundDownFromLitres(long litres, long expectedUnits)
        {
            Assert.Equal(expectedUnits, GasStoreUnits.ToStoreUnits(litres));
        }

        [Fact]
        public void ARowUnderOneKilolitreIsNotListable()
        {
            // The rule the store branch relies on to drop a row rather than list it at zero: SE would
            // otherwise take the row's price for an Amount nothing can be drawn from.
            Assert.Equal(0, GasStoreUnits.ToStoreUnits(GasStoreUnits.LitresPerStoreUnit - 1));
            Assert.Equal(1, GasStoreUnits.ToStoreUnits(GasStoreUnits.LitresPerStoreUnit));
        }

        [Fact]
        public void LitresAndStoreUnitsRoundTripOnWholeKilolitres()
        {
            Assert.Equal(5000, GasStoreUnits.ToStoreUnits(GasStoreUnits.ToLitres(5000)));
            Assert.Equal(2_000_000, GasStoreUnits.ToLitres(GasStoreUnits.ToStoreUnits(2_000_000)));
        }

        [Fact]
        public void GasKeysAreRecognisedByPrefix_NotBySubtype()
        {
            // A bottle and the gas share a subtype name and are not the same thing.
            Assert.True(InventoryKeyMapper.IsGasKey("Gas:Hydrogen"));
            Assert.True(InventoryKeyMapper.IsGasKey("Gas:Oxygen"));
            Assert.False(InventoryKeyMapper.IsGasKey("HydrogenBottle"));
            Assert.False(InventoryKeyMapper.IsGasKey("Ore:Ice"));
            Assert.False(InventoryKeyMapper.IsGasKey(null));
        }
    }
}
