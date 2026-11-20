using System;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    public class StockAvailabilityTests
    {
        /// <summary>A breakdown whose keep-back is <paramref name="keepBack"/>, set by
        /// <paramref name="term"/>. Classify reads only the keep-back.</summary>
        private static PriceOracle.TargetBreakdown Target(long keepBack, string term)
        {
            return new PriceOracle.TargetBreakdown { Target = keepBack, KeepBack = keepBack, KeepBackTerm = term };
        }

        /// <summary>The case that prompted the column: a station holding plenty of hydrogen that its
        /// refuel refuses, because departing hulls draw it and the flow term sets the target above
        /// the holding.</summary>
        [Fact]
        public void Hydrogen_UnderAFlowTarget_IsKeptForUse()
        {
            StockKeptFor why = StockAvailability.Classify(
                held: 12_000_000, forSale: 0, hasStore: true, listed: false,
                target: Target(80_000_000, "flow"), unitsPerPricedUnit: 1000);

            Assert.Equal(StockKeptFor.Use, why);
        }

        [Theory]
        [InlineData("owed", StockKeptFor.Owed)]
        [InlineData("flow", StockKeptFor.Use)]
        [InlineData("gas", StockKeptFor.GasFloor)]
        [InlineData("wanted", StockKeptFor.Floor)]
        [InlineData("bankglut", StockKeptFor.Floor)]
        public void UnderKeepBack_NamesTheNeedThatSetIt(string term, StockKeptFor expected)
        {
            // Partly offered: the need-weighted keep-back, not the threshold.
            Assert.Equal(expected, StockAvailability.Classify(
                held: 600, forSale: 200, hasStore: true, listed: true,
                target: Target(900, term), unitsPerPricedUnit: 1));
        }

        /// <summary>The N217 case after the fix: a target raised only by sales keeps nothing back, so
        /// a row short of its holding is the cap or the rounding, never "steady sales".</summary>
        [Fact]
        public void ATargetSetBySalesAlone_IsNeverTheReason()
        {
            var salesOnly = new PriceOracle.TargetBreakdown
            {
                Target = 80_000_000, Winner = "sold", KeepBack = 0, KeepBackTerm = "none",
            };
            Assert.Equal(StockKeptFor.ListingCap, StockAvailability.Classify(
                12_000_000, 10_000_000, true, true, salesOnly, 1000));
            Assert.Equal(StockKeptFor.None, StockAvailability.Classify(
                12_000_000, 12_000_000, true, true, salesOnly, 1000));
        }

        [Fact]
        public void AllOffered_KeepsNothing()
        {
            Assert.Equal(StockKeptFor.None, StockAvailability.Classify(
                500, 500, true, true, Target(100, "flow"), 1));
        }

        /// <summary>Gas rows are whole kilolitres: 1,500 L holds back 500 L however the target
        /// reads, and that is rounding, not the station keeping anything.</summary>
        [Fact]
        public void GasRemainderUnderOneKilolitre_IsRounding()
        {
            Assert.Equal(StockKeptFor.None, StockAvailability.Classify(
                1500, 1000, true, true, Target(0, "none"), 1000));
        }

        [Fact]
        public void OverTarget_AndShortOfTheHolding_IsTheListingCap()
        {
            Assert.Equal(StockKeptFor.ListingCap, StockAvailability.Classify(
                50_000, 10_000, true, true, Target(2_000, "flow"), 1));
        }

        [Fact]
        public void OverTarget_WithNoRow_IsNotTraded()
        {
            Assert.Equal(StockKeptFor.NotTraded, StockAvailability.Classify(
                300, 0, true, false, Target(0, "none"), 1));
        }

        [Fact]
        public void NoStore_OutranksEveryOtherReason()
        {
            Assert.Equal(StockKeptFor.NoStore, StockAvailability.Classify(
                300, 0, false, false, Target(900, "owed"), 1));
        }

        [Fact]
        public void NothingHeld_KeepsNothing()
        {
            Assert.Equal(StockKeptFor.None, StockAvailability.Classify(
                0, 0, true, false, Target(900, "owed"), 1));
        }

        /// <summary>The host casts the Core value straight onto the wire byte, so the two enums must
        /// agree name for name and ordinal for ordinal.</summary>
        [Fact]
        public void WireMirror_MatchesCoreOrdinals()
        {
            Assert.Equal(Enum.GetNames(typeof(StockKeptFor)), Enum.GetNames(typeof(WireStockKeptFor)));
            foreach (StockKeptFor v in Enum.GetValues(typeof(StockKeptFor)))
                Assert.Equal(v.ToString(), ((WireStockKeptFor)(byte)v).ToString());
        }
    }
}
