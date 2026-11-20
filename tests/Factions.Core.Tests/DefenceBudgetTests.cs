using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Every faction arms against the strongest attacker it has met (FAC-275 slice 3): the fight
    /// ledger's DEFENDING records, inside a memory window, times a ratio, become the same
    /// <c>MinDps</c> power floor a terror campaign posts — so a civilian that repelled a raid
    /// builds a hull that can win the next duel instead of a fifth drone. The larger of the two
    /// budgets sets the floor and the posted line names which.
    /// </summary>
    public class DefenceBudgetTests
    {
        private static readonly DateTime T0 = YardT0;

        /// <summary>A fight as the DEFENDER's copy: what the attacker brought against us.</summary>
        private static FightRecord Defended(long attackerDps, DateTime at, ActionKind kind = ActionKind.Raid)
        {
            return new FightRecord
            {
                Utc = at, Kind = kind, Attacking = false, OpponentTag = "SPRT", NodeId = "YARD",
                AttackerDps = attackerDps, DefenderDps = 9000, AttackerRatio = 0.0, AttackerHullsLost = 3,
            };
        }

        /// <summary>The attacker's own copy of the same fight.</summary>
        private static FightRecord Attacked(long attackerDps, DateTime at)
        {
            FightRecord f = Defended(attackerDps, at);
            f.Attacking = true;
            return f;
        }

        // ---- the budget --------------------------------------------------------------------------

        [Fact]
        public void TheStrongestAttackerInsideTheWindow_SetsTheBudget_TimesTheRatio()
        {
            FactionTuning t = TestSupport.Tuning();
            var s = new FactionState { Tag = "IGSL" };
            FightLedger.Append(s, Defended(900, T0.AddHours(-6)), t.FightHistoryCap);
            FightLedger.Append(s, Defended(2500, T0.AddHours(-3)), t.FightHistoryCap);
            FightLedger.Append(s, Defended(1200, T0.AddHours(-1)), t.FightHistoryCap);

            Assert.Equal(1.0, t.DefenceBudgetRatio);                  // shipped default: match what came
            Assert.Equal(2500, DefenceBudget.WantedDps(s, t, T0));    // the strongest, not the latest

            t.DefenceBudgetRatio = 1.5;
            Assert.Equal(3750, DefenceBudget.WantedDps(s, t, T0));

            t.DefenceBudgetRatio = 0.0;                                // the disable switch
            Assert.Equal(0, DefenceBudget.WantedDps(s, t, T0));
        }

        [Fact]
        public void AnAttackOlderThanTheWindow_IsForgotten()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(TimeSpan.FromHours(24), t.DefenceMemoryWindow);
            var s = new FactionState { Tag = "IGSL" };
            DateTime lapsed = T0 - t.DefenceMemoryWindow - TimeSpan.FromMinutes(1);
            DateTime remembered = T0 - t.DefenceMemoryWindow + TimeSpan.FromMinutes(1);
            Assert.True(T0 - lapsed > t.DefenceMemoryWindow, "the old fight must fall outside the window");

            FightLedger.Append(s, Defended(2500, lapsed), t.FightHistoryCap);
            Assert.Equal(0, DefenceBudget.WantedDps(s, t, T0));

            // A weaker attack still inside the window is what the budget reads: the number falls to
            // the recent one rather than staying at the strongest ever met.
            FightLedger.Append(s, Defended(900, remembered), t.FightHistoryCap);
            Assert.Equal(900, DefenceBudget.WantedDps(s, t, T0));

            t.DefenceMemoryWindow = TimeSpan.Zero;   // the other disable switch
            Assert.Equal(0, DefenceBudget.WantedDps(s, t, T0));
        }

        [Fact]
        public void OnlyWhatWasBroughtAgainstUsCounts_NotWhatWeBrought()
        {
            FactionTuning t = TestSupport.Tuning();
            var s = new FactionState { Tag = "IGSL" };
            FightLedger.Append(s, Attacked(9000, T0.AddHours(-2)), t.FightHistoryCap);
            Assert.Equal(0, DefenceBudget.WantedDps(s, t, T0));
            Assert.Empty(FightLedger.DefendedSince(s, T0.AddHours(-24)));

            FightLedger.Append(s, Defended(2500, T0.AddHours(-1)), t.FightHistoryCap);
            Assert.Equal(2500, DefenceBudget.WantedDps(s, t, T0));   // still only the defending copy
        }

        [Fact]
        public void AConquerFleetsGunsCountLikeARaiders()
        {
            FactionTuning t = TestSupport.Tuning();
            var s = new FactionState { Tag = "IGSL" };
            FightLedger.Append(s, Defended(4000, T0.AddHours(-2), ActionKind.Conquer), t.FightHistoryCap);
            Assert.Equal(4000, DefenceBudget.WantedDps(s, t, T0));
        }

        // ---- the yard reads the larger of the two budgets ----------------------------------------

        private static ShipyardManager Manager()
        {
            return new ShipyardManager
            {
                PrefabListPicker = (role, tag) => role == "Raid"
                    ? new[] { "Drone", "Interceptor", "Cruiser" }
                    : new[] { "Hauler" }
            };
        }

        private static ArmedCatalogue Catalogue()
        {
            return new ArmedCatalogue().Add("Drone", 500, "Small").Add("Interceptor", 1000, "Small")
                .Add("Cruiser", 6000, "Large").Add("Hauler", 0, "Large", 5000);
        }

        private static CostRecipes Recipes()
        {
            var r = new CostRecipes();
            r.CostByPrefab["Drone"] = 10; r.CostByPrefab["Interceptor"] = 30; r.CostByPrefab["Cruiser"] = 500;
            return r;
        }

        [Fact]
        public void TheLargerBudgetSetsTheFloor_AndTheLineNamesIt_Terror()
        {
            // Terror 8000 against a defence of 2000: one 500-DPS drone in stock, four slots open,
            // so the floor is (8000 - 500) / 4.
            SpatialNode yard; WorldModel world = Yard(Catalogue(), Recipes(), out yard);
            yard.ShipStock["Drone"] = 1;
            var state = new FactionState { Tag = "F", TerrorStandingDps = 8000 };
            FightLedger.Append(state, Defended(2000, T0.AddHours(-1)), world.Tuning.FightHistoryCap);

            var log = new List<string>();
            Manager().Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid")
                                      && l.Contains("minDps=1875") && l.Contains("budget=terror"));
        }

        [Fact]
        public void TheLargerBudgetSetsTheFloor_AndTheLineNamesIt_Defence()
        {
            // The same floor from the other side: terror 1000, an 8000-DPS attacker met an hour ago.
            SpatialNode yard; WorldModel world = Yard(Catalogue(), Recipes(), out yard);
            yard.ShipStock["Drone"] = 1;
            var state = new FactionState { Tag = "F", TerrorStandingDps = 1000 };
            FightLedger.Append(state, Defended(8000, T0.AddHours(-1)), world.Tuning.FightHistoryCap);

            var log = new List<string>();
            Manager().Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid")
                                      && l.Contains("minDps=1875") && l.Contains("budget=defence"));
        }

        [Fact]
        public void ACivilianThatWasRaided_PostsAPowerFloorOfItsOwn()
        {
            // No campaign, no standing requirement -- the whole budget is what SPRT brought.
            SpatialNode yard; WorldModel world = Yard(Catalogue(), Recipes(), out yard);
            yard.ShipStock["Drone"] = 1;
            var state = new FactionState { Tag = "F" };
            Assert.Equal(0, TerrorCampaigns.WantedDps(state));
            FightLedger.Append(state, Defended(2500, T0.AddHours(-2)), world.Tuning.FightHistoryCap);

            var log = new List<string>();
            Manager().Tick(new List<FactionState> { state }, world, world.Tuning, log);

            // (2500 - 500) shared across the squadron's four open slots: four hulls at this floor
            // plus the drone already held come to the 2500 the raider brought.
            List<DemandEntry> demands = world.Demand.SnapshotShipDemands("F");
            DemandEntry raid = demands.Find(d => d.Kind == "Ship:Raid" && d.MinDps > 0);
            Assert.NotNull(raid);
            Assert.Equal(500, (int)raid.MinDps);
            Assert.True(raid.MinDps * 4 + 500 >= DefenceBudget.WantedDps(state, world.Tuning, world.UtcNow),
                "the posted floor must close the budget's gap across the open slots");
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid")
                                      && l.Contains("minDps=500") && l.Contains("budget=defence"));
        }

        [Fact]
        public void AFactionNobodyHasFought_PostsNoPowerFloor()
        {
            SpatialNode yard; WorldModel world = Yard(Catalogue(), Recipes(), out yard);
            yard.ShipStock["Drone"] = 1;
            var state = new FactionState { Tag = "F" };
            Assert.Empty(state.Fights);

            var log = new List<string>();
            Manager().Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("role=Raid") && l.Contains("minDps="));
            Assert.DoesNotContain(log, l => l.Contains("budget="));
            Assert.Empty(world.Demand.SnapshotShipDemands("F").FindAll(d => d.MinDps > 0));
        }
    }
}
