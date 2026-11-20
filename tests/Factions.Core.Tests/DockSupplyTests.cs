using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The arithmetic behind the Services Terminal's Refuel and Rearm. The host half walks
    /// live tanks, guns and containers and has no offline harness, so this pins what can be pinned:
    /// the plan never exceeds stock or budget, gas is priced per kilolitre over the key's whole
    /// total, sinks are served in order, and the limit names what actually stopped it.</summary>
    public class DockSupplyTests
    {
        private const string H2 = "Gas:Hydrogen";
        private const string Nato = "NATO_25x184mm";
        private const string Missile = "Missile200mm";

        private static DockSupplySink Sink(string key, long capacity, int slot = 0)
        {
            return new DockSupplySink { Slot = slot, InvKey = key, Capacity = capacity };
        }

        private static Dictionary<string, DockSupplyOffer> Offers(params object[] triples)
        {
            var d = new Dictionary<string, DockSupplyOffer>();
            for (int i = 0; i + 2 < triples.Length; i += 3)
                d[(string)triples[i]] = new DockSupplyOffer
                {
                    Available = System.Convert.ToInt64(triples[i + 1]),
                    UnitPrice = System.Convert.ToInt64(triples[i + 2]),
                };
            return d;
        }

        [Fact]
        public void Plan_FillsEverySink_WhenStockAndBudgetSuffice()
        {
            var sinks = new List<DockSupplySink> { Sink(H2, 15000), Sink(H2, 5000) };
            DockSupplyPlan plan = DockSupply.Plan(sinks, Offers(H2, 1000000, 5000), 1000000);

            Assert.Equal(new long[] { 15000, 5000 }, plan.Units);
            Assert.Equal(20000L, plan.UnitsByKey[H2]);
            // 20 kL at 5,000 per kL.
            Assert.Equal(100000L, plan.Credits);
            Assert.Equal(DockSupplyLimit.None, plan.Limit);
        }

        [Fact]
        public void Plan_GasIsPricedOverTheKeysTotal_NotPerTank()
        {
            // Three 333 L tanks at 3 credits per kL. Priced tank by tank each is 0.999 -> 0, so the
            // fill would be free; over the 999 L total it is 2.997 -> 2.
            var sinks = new List<DockSupplySink> { Sink(H2, 333), Sink(H2, 333), Sink(H2, 333) };
            DockSupplyPlan plan = DockSupply.Plan(sinks, Offers(H2, 1000000, 3), 1000000);

            Assert.Equal(999L, plan.UnitsByKey[H2]);
            Assert.Equal(2L, plan.Credits);
        }

        [Fact]
        public void Plan_StopsAtStationStock_AndSaysSo()
        {
            var sinks = new List<DockSupplySink> { Sink(Nato, 10), Sink(Nato, 10) };
            DockSupplyPlan plan = DockSupply.Plan(sinks, Offers(Nato, 14, 30), 1000000);

            Assert.Equal(new long[] { 10, 4 }, plan.Units);
            Assert.Equal(420L, plan.Credits);
            Assert.Equal(DockSupplyLimit.Stock, plan.Limit);
        }

        [Fact]
        public void Plan_NeverExceedsBudget_AndSaysFunds()
        {
            var sinks = new List<DockSupplySink> { Sink(Nato, 10), Sink(Missile, 10) };
            DockSupplyPlan plan = DockSupply.Plan(sinks, Offers(Nato, 100, 30, Missile, 100, 200), 1000);

            // 10 NATO = 300, leaving 700 for missiles at 200 -> 3.
            Assert.Equal(new long[] { 10, 3 }, plan.Units);
            Assert.Equal(900L, plan.Credits);
            Assert.True(plan.Credits <= 1000);
            Assert.Equal(DockSupplyLimit.Funds, plan.Limit);
        }

        [Fact]
        public void Plan_GasBudgetReachesAPartKilolitre()
        {
            // 4,999 credits at 5,000/kL: ItemCredits.Affordable says 0 L, but 999 L cost 4,995.
            var sinks = new List<DockSupplySink> { Sink(H2, 20000) };
            DockSupplyPlan plan = DockSupply.Plan(sinks, Offers(H2, 1000000, 5000), 4999);

            Assert.Equal(999L, plan.UnitsByKey[H2]);
            Assert.Equal(4995L, plan.Credits);
            Assert.Equal(DockSupplyLimit.Funds, plan.Limit);
        }

        [Fact]
        public void Plan_ServesSinksInOrder_SoGunsLoadBeforeCargo()
        {
            var sinks = new List<DockSupplySink>
            {
                Sink(Nato, 6, slot: 0),     // a gun
                Sink(Nato, 6, slot: 1),     // a gun
                Sink(Nato, 50, slot: 2),    // a cargo reserve
            };
            DockSupplyPlan plan = DockSupply.Plan(sinks, Offers(Nato, 15, 10), 1000000);

            Assert.Equal(new long[] { 6, 6, 3 }, plan.Units);
        }

        [Fact]
        public void Plan_KeyTheStationDoesNotSell_IsSkippedAndReportedAsStock()
        {
            var sinks = new List<DockSupplySink> { Sink(Missile, 4), Sink(Nato, 4) };
            DockSupplyPlan plan = DockSupply.Plan(sinks, Offers(Nato, 100, 10), 1000000);

            Assert.Equal(new long[] { 0, 4 }, plan.Units);
            Assert.False(plan.UnitsByKey.ContainsKey(Missile));
            Assert.Equal(DockSupplyLimit.Stock, plan.Limit);
        }

        [Fact]
        public void Plan_FullShipOrNoSinks_IsNoSpace()
        {
            Assert.Equal(DockSupplyLimit.NoSpace,
                DockSupply.Plan(new List<DockSupplySink>(), Offers(H2, 1000, 5), 1000).Limit);
            DockSupplyPlan full = DockSupply.Plan(new List<DockSupplySink> { Sink(H2, 0) }, Offers(H2, 1000, 5), 1000);
            Assert.Equal(DockSupplyLimit.NoSpace, full.Limit);
            Assert.Equal(0L, full.Credits);
        }

        [Fact]
        public void Plan_ZeroBudget_BuysNothing()
        {
            DockSupplyPlan plan = DockSupply.Plan(new List<DockSupplySink> { Sink(Nato, 5) }, Offers(Nato, 100, 10), 0);
            Assert.Equal(0L, plan.TotalUnits);
            Assert.Equal(0L, plan.Credits);
            Assert.Equal(DockSupplyLimit.Funds, plan.Limit);
        }

        [Fact]
        public void Plan_ATopUpThatRoundsToNothing_StillCostsOneCredit()
        {
            // 100 L at 3 per kL is 0.3 credits.
            DockSupplyPlan plan = DockSupply.Plan(new List<DockSupplySink> { Sink(H2, 100) }, Offers(H2, 1000000, 3), 1000);
            Assert.Equal(100L, plan.UnitsByKey[H2]);
            Assert.Equal(1L, plan.Credits);

            DockSupplyPlan broke = DockSupply.Plan(new List<DockSupplySink> { Sink(H2, 100) }, Offers(H2, 1000000, 3), 0);
            Assert.Equal(0L, broke.TotalUnits);
            Assert.Equal(0L, broke.Credits);
        }

        [Fact]
        public void MaxUnitsFor_IsTheInverseOfItemCredits()
        {
            for (long credits = 0; credits < 20000; credits += 997)
            {
                long litres = DockSupply.MaxUnitsFor(H2, credits, 5000);
                Assert.True(ItemCredits.For(H2, litres, 5000) <= credits);
                Assert.True(ItemCredits.For(H2, litres + 1, 5000) > credits);
            }
            Assert.Equal(33L, DockSupply.MaxUnitsFor(Nato, 1000, 30));
            Assert.True(DockSupply.MaxUnitsFor(H2, long.MaxValue, 5000) > 0);
        }

        [Fact]
        public void OfferUnits_ConvertsKilolitresToLitres_ForGasOnly()
        {
            Assert.Equal(2500000L, DockSupply.OfferUnits(H2, 2500));
            Assert.Equal(40L, DockSupply.OfferUnits(Nato, 40));
            Assert.Equal(0L, DockSupply.OfferUnits(H2, -1));
        }

        [Fact]
        public void ApplyMultiplier_DiscountsButNeverRaisesOrZeroes()
        {
            Assert.Equal(90L, DockSupply.ApplyMultiplier(100, 0.9));
            Assert.Equal(100L, DockSupply.ApplyMultiplier(100, 1.0));
            Assert.Equal(100L, DockSupply.ApplyMultiplier(100, 1.3));
            Assert.Equal(100L, DockSupply.ApplyMultiplier(100, double.NaN));
            Assert.Equal(1L, DockSupply.ApplyMultiplier(1, 0.1));
        }

        [Fact]
        public void PlanCargoReserve_FillsContainersInOrder_ByVolume()
        {
            // Containers of 20 L and 100 L free; 150 NATO boxes at 0.2 L each want 30 L.
            var sinks = DockSupply.PlanCargoReserve(
                new List<double> { 20.0, 100.0 },
                new List<DockReserveRequest>
                {
                    new DockReserveRequest { InvKey = Nato, Count = 150, UnitLitres = 0.2 },
                });

            Assert.Equal(2, sinks.Count);
            Assert.Equal(0, sinks[0].Slot);
            Assert.Equal(100L, sinks[0].Capacity);     // exactly 20 L -- the tolerance matters
            Assert.Equal(1, sinks[1].Slot);
            Assert.Equal(50L, sinks[1].Capacity);
        }

        [Fact]
        public void PlanCargoReserve_SecondRequestSeesTheFirstsVolume()
        {
            var sinks = DockSupply.PlanCargoReserve(
                new List<double> { 100.0 },
                new List<DockReserveRequest>
                {
                    new DockReserveRequest { InvKey = Missile, Count = 2, UnitLitres = 30.0 },
                    new DockReserveRequest { InvKey = Nato, Count = 1000, UnitLitres = 0.2 },
                });

            Assert.Equal(2, sinks.Count);
            Assert.Equal(2L, sinks[0].Capacity);
            Assert.Equal(200L, sinks[1].Capacity);     // the 40 L the missiles left
        }

        [Fact]
        public void PlanCargoReserve_IgnoresEmptyAndUnsizedRequests()
        {
            var sinks = DockSupply.PlanCargoReserve(
                new List<double> { 100.0 },
                new List<DockReserveRequest>
                {
                    new DockReserveRequest { InvKey = Nato, Count = 0, UnitLitres = 0.2 },
                    new DockReserveRequest { InvKey = Missile, Count = 3, UnitLitres = 0 },
                    new DockReserveRequest { InvKey = "", Count = 3, UnitLitres = 1 },
                });
            Assert.Empty(sinks);
        }
    }
}
