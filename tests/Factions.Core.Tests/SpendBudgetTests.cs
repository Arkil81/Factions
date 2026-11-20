using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Direct coverage for <see cref="SpendBudget"/>, in particular the <see cref="SpendBudget.RemainingBudget"/>
    /// method added for the 2026-08-02 Trade-settlement fix (partial-fill needs the numeric remaining
    /// capacity, not just the boolean <see cref="SpendBudget.CanSpend"/> check). The class was previously
    /// only exercised indirectly through FleetManager/TradeManager's own dispatch-budget usage.
    /// </summary>
    public class SpendBudgetTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void RemainingBudget_WindowSecondsZero_Uncapped()
        {
            var b = new SpendBudget();
            Assert.Equal(long.MaxValue, b.RemainingBudget("F", T0, 1000, 0.3, 500, 0));
        }

        [Fact]
        public void RemainingBudget_BothFractionAndFloorZero_Uncapped()
        {
            var b = new SpendBudget();
            Assert.Equal(long.MaxValue, b.RemainingBudget("F", T0, 1000, 0, 0, 30));
        }

        [Fact]
        public void RemainingBudget_FreshWindow_EqualsFullBudget()
        {
            var b = new SpendBudget();
            // budget = recentIncome*fraction + floor = 1000*0.3 + 500 = 800
            Assert.Equal(800L, b.RemainingBudget("F", T0, 1000, 0.3, 500, 30));
        }

        [Fact]
        public void RemainingBudget_DecrementsAfterRecord()
        {
            var b = new SpendBudget();
            b.Record("F", T0, 300, 30);
            Assert.Equal(500L, b.RemainingBudget("F", T0, 1000, 0.3, 500, 30)); // 800 - 300
        }

        [Fact]
        public void RemainingBudget_ClampedToZero_NeverNegative()
        {
            var b = new SpendBudget();
            b.Record("F", T0, 5000, 30); // far more than the 800 budget
            Assert.Equal(0L, b.RemainingBudget("F", T0, 1000, 0.3, 500, 30));
        }

        [Fact]
        public void RemainingBudget_RollsOverToFreshWindow_AfterWindowSecondsElapse()
        {
            var b = new SpendBudget();
            b.Record("F", T0, 800, 30); // fully consumed
            DateTime later = T0.AddSeconds(31);
            Assert.Equal(800L, b.RemainingBudget("F", later, 1000, 0.3, 500, 30));
        }

        [Fact]
        public void RemainingBudget_KeyedPerTag_IndependentOfOtherTags()
        {
            var b = new SpendBudget();
            b.Record("F1", T0, 800, 30);
            Assert.Equal(0L, b.RemainingBudget("F1", T0, 1000, 0.3, 500, 30));
            Assert.Equal(800L, b.RemainingBudget("F2", T0, 1000, 0.3, 500, 30));
        }

        // ---- wealth term: treasurySurplus * wealthFraction on top of income + floor -------------

        [Fact]
        public void WealthTerm_AddsSurplusFractionToBudget()
        {
            var b = new SpendBudget();
            // 1000*0.3 + 500 + 1_000_000*0.001 = 800 + 1000 = 1800
            Assert.Equal(1800L, b.RemainingBudget("F", T0, 1000, 0.3, 500, 30, 1_000_000, 0.001));
            Assert.True(b.CanSpend("F", T0, 1000, 1800, 0.3, 500, 30, 1_000_000, 0.001));
            Assert.False(b.CanSpend("F", T0, 1000, 1801, 0.3, 500, 30, 1_000_000, 0.001));
        }

        [Fact]
        public void WealthTerm_ZeroFraction_ReproducesIncomeOnlyBudget()
        {
            var b = new SpendBudget();
            Assert.Equal(800L, b.RemainingBudget("F", T0, 1000, 0.3, 500, 30, 1_000_000, 0));
        }

        [Fact]
        public void WealthTerm_ZeroSurplus_ReproducesIncomeOnlyBudget()
        {
            var b = new SpendBudget();
            Assert.Equal(800L, b.RemainingBudget("F", T0, 1000, 0.3, 500, 30, 0, 0.5));
        }

        [Fact]
        public void WealthTerm_DoesNotChangeUncappedContract()
        {
            var b = new SpendBudget();
            // fraction+floor both zero still means uncapped, wealth term or not
            Assert.Equal(long.MaxValue, b.RemainingBudget("F", T0, 1000, 0, 0, 30, 1_000_000, 0.5));
        }

        // ---- Economy.SpendableTreasurySurplus: the reserve rule feeding the wealth term ---------

        [Fact]
        public void SpendableSurplus_TreasuryBelowReserve_IsZero()
        {
            var t = new FactionTuning { ReserveTreasury = 50_000_000, OperationalReserveRunwayTicks = 60 };
            Assert.Equal(0L, Economy.SpendableTreasurySurplus(30_000_000, 0, t));
            Assert.Equal(0L, Economy.SpendableTreasurySurplus(50_000_000, 0, t));
        }

        [Fact]
        public void SpendableSurplus_AboveReserve_ReturnsExcess()
        {
            var t = new FactionTuning { ReserveTreasury = 50_000_000, OperationalReserveRunwayTicks = 60 };
            Assert.Equal(10_000_000L, Economy.SpendableTreasurySurplus(60_000_000, 0, t));
        }

        [Fact]
        public void SpendableSurplus_NegativeNetFlow_RaisesReserveToRunway()
        {
            var t = new FactionTuning { ReserveTreasury = 50_000_000, OperationalReserveRunwayTicks = 60 };
            // burn 2M/pass -> runway reserve 120M > 50M floor -> surplus shrinks accordingly
            Assert.Equal(30_000_000L, Economy.SpendableTreasurySurplus(150_000_000, -2_000_000, t));
            // small burn: runway reserve 600k < 50M floor -> floor still wins
            Assert.Equal(100_000_000L, Economy.SpendableTreasurySurplus(150_000_000, -10_000, t));
        }

        [Fact]
        public void SpendableSurplus_PositiveNetFlow_UsesReserveFloorOnly()
        {
            var t = new FactionTuning { ReserveTreasury = 50_000_000, OperationalReserveRunwayTicks = 60 };
            Assert.Equal(100_000_000L, Economy.SpendableTreasurySurplus(150_000_000, 5_000_000, t));
        }
    }
}
