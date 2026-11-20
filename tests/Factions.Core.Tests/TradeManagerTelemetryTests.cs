using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary><see cref="TradeTickTelemetry"/> and the <c>TRADE_TICK_PERF</c> line it feeds. The
    /// counters follow the pass they measure — partners examined, unserved rows seen, vectors formed,
    /// and what each vector's dispatch produced — so every assertion here is against a scenario whose
    /// outcome is hand-computable from the fixture. See docs/trade-haul-shaped.md.</summary>
    public class TradeManagerTelemetryTests
    {
        /// <summary>Seller "A" can serve one of buyer "B"'s two rows: it is glutted in Iron and short
        /// of Nickel itself, so exactly one vector forms and one is refused for want of consent.
        /// "COOL" is whitelisted but owns nothing, so it is a partner examined that yields no
        /// rows.</summary>
        private static void BuildScenario(out WorldModel world, out FactionTuning tuning,
            out List<FactionState> states, out TradeTest.NodeScarcityOracle oracle)
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", 1_000_000);
            world.SetTreasury("B", 1_000_000);

            SpatialNode a1 = TradeTest.Node(world, "A1", "A");
            a1.Inventory["Iron"] = 5000;
            a1.Inventory["Nickel"] = 5000;
            a1.ShipStock["Trade"] = 1;

            TradeTest.Node(world, "B1", "B", x: 100);

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 0.1)
                .Set("B1", ItemKind.Ore, 2.0);

            FactionState a = TradeTest.State("A");
            a.CentralBankRecentIncome = 1_000_000;
            TradeTest.Whitelist(a, "B", 2.0);
            TradeTest.Whitelist(a, "COOL", 1.0);

            FactionState b = TradeTest.State("B");
            b.CentralBankRecentIncome = 1_000_000;
            TradeTest.Unserved(b, "B1", "Iron", 100, urgency: 5.0);
            TradeTest.Unserved(b, "B1", "Explosives", 100, urgency: 1.0);   // nobody holds any

            states = new List<FactionState> { a, b, TradeTest.State("COOL") };
        }

        [Fact]
        public void LastTelemetry_ResetsEverySweep_NoCrossCallAccumulation()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);

            manager.Tick(states, world, tuning, new List<string>());
            int firstFactions = manager.LastTelemetry.Factions;
            Assert.Equal(states.Count, firstFactions);

            manager.Tick(states, world, tuning, new List<string>());
            Assert.Equal(firstFactions, manager.LastTelemetry.Factions);
        }

        [Fact]
        public void Tick_ScriptedScenario_CountersMatchHandComputedValues()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);

            manager.Tick(states, world, tuning, new List<string>());
            TradeTickTelemetry t = manager.LastTelemetry;

            Assert.Equal(3, t.Factions);            // roster walked once, counted on the buy pass
            Assert.Equal(1, t.EligibleFactions);    // only A has a whitelist
            Assert.Equal(2, t.Partners);            // A examines B and COOL, once each, on its sell pass
            Assert.Equal(2, t.UnservedSeen);        // B publishes two rows
            Assert.Equal(1, t.Vectors);             // only the Iron row finds a consenting source
            Assert.Equal(1, t.RefusedConsent);      // nobody holds Explosives
            Assert.Equal(1, t.Dispatched);
            Assert.Equal(0, t.Procured);
            Assert.Equal(1, t.Tier1);
            Assert.Equal(0, t.Tier2);
            Assert.Equal(0, t.Tier3);
            Assert.Equal(0, t.NoShip);
            Assert.Equal(0, t.AssessRefused);
            Assert.Equal(0, t.Claimed);
            Assert.Equal(1, t.SweepsCompleted);
        }

        /// <summary>A row already claimed is counted as claimed, not as a consent refusal — the two
        /// are opposite diagnoses ("someone is already on it" vs "nobody will sell it") and reading
        /// one for the other sends the next investigation at the wrong mechanism.</summary>
        [Fact]
        public void ClaimedRow_CountsAsClaimed_NotAsRefusedConsent()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            states[1].ExternalHaulClaims.Add(("B1", "Iron"));

            TradeManager manager = TradeTest.Manager(oracle);
            manager.Tick(states, world, tuning, new List<string>());

            Assert.Equal(1, manager.LastTelemetry.Claimed);
            Assert.Equal(1, manager.LastTelemetry.RefusedConsent);   // the Explosives row, unchanged
            Assert.Equal(0, manager.LastTelemetry.Vectors);
            Assert.Equal(0, manager.LastTelemetry.Dispatched);
        }

        [Fact]
        public void NoHullAnywhere_CountsNoShipAndTier3_NotADispatch()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            world.Graph.Nodes["A1"].ShipStock.Clear();

            TradeManager manager = TradeTest.Manager(oracle);
            manager.Tick(states, world, tuning, new List<string>());

            Assert.Equal(1, manager.LastTelemetry.Vectors);
            Assert.Equal(1, manager.LastTelemetry.NoShip);
            Assert.Equal(1, manager.LastTelemetry.Tier3);
            Assert.Equal(0, manager.LastTelemetry.Dispatched);
        }

        [Fact]
        public void LoadGatedConvoy_CountsAssessRefused_NotNoShip()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            world.Graph.Nodes["A1"].Inventory["Iron"] = 5;
            states[1].UnservedDemands.Clear();
            TradeTest.Unserved(states[1], "B1", "Iron", 10_000);

            TradeManager manager = TradeTest.Manager(oracle, new TradeTest.Volumes().Set("Iron", 1.0));
            manager.Tick(states, world, tuning, new List<string>());

            Assert.Equal(1, manager.LastTelemetry.AssessRefused);
            Assert.Equal(0, manager.LastTelemetry.NoShip);
            Assert.Equal(0, manager.LastTelemetry.Dispatched);
        }

        [Fact]
        public void PhaseTicks_AreNonNegative()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);

            manager.Tick(states, world, tuning, new List<string>());

            Assert.True(manager.LastTelemetry.EligibilityFilterTicks >= 0);
            Assert.True(manager.LastTelemetry.DispatchTicks >= 0);
        }

        /// <summary>The FAC-199 lesson: a threshold that selects against the cheap path cannot measure
        /// it. This pass is far below <c>PerfLogThresholdMs</c> and still has to report, because a
        /// dispatch happened.</summary>
        [Fact]
        public void PerfLine_IsEmittedWheneverAnythingDispatched_EvenBelowTheCostThreshold()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            var log = new List<string>();

            TradeTest.Manager(oracle).Tick(states, world, tuning, log);

            // The sell pass is the one that dispatched; a buy pass over the same faction may also
            // report if it happened to cost more than the threshold, which is timing, not behaviour.
            string line = Assert.Single(log.Where(l => l.StartsWith("TRADE_TICK_PERF ", StringComparison.Ordinal)
                                                       && l.Contains(" dispatched=1 ")));
            Assert.StartsWith("TRADE_TICK_PERF A ", line);
            foreach (string field in new[]
            {
                " partners=", " unservedSeen=", " vectors=", " dispatched=", " procured=", " noShip=",
                " refusedConsent=", " claimed=", " assessRefused=", " tier1=", " tier2=", " tier3=",
                " totalMs=", " dispatchMs=",
            })
                Assert.Contains(field, line);
            Assert.Contains(" dispatched=1 ", line);
            Assert.Contains(" vectors=1 ", line);
            Assert.Contains(" refusedConsent=1 ", line);
        }

        /// <summary>The other side of the same gate: a faction filtered out before it does any work
        /// reports nothing at all, so the channel is not one line per faction per sweep forever. "B"
        /// and "COOL" have no whitelist and leave at the eligibility filter, which is a decision, not
        /// a duration — unlike the cost threshold, which is timing.</summary>
        [Fact]
        public void PerfLine_NamesOnlyFactionsThatActuallyRanAPass()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            var log = new List<string>();

            TradeTest.Manager(oracle).Tick(states, world, tuning, log);

            List<string> perf = log.Where(l => l.StartsWith("TRADE_TICK_PERF ", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(perf);
            Assert.All(perf, l => Assert.StartsWith("TRADE_TICK_PERF A ", l));
        }

        [Fact]
        public void BudgetBlockedFaction_IncrementsBudgetBlockedNotDispatched()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            world.SetTreasury("A", 0);   // below TradeCost

            TradeManager manager = TradeTest.Manager(oracle);
            manager.Tick(states, world, tuning, new List<string>());

            Assert.Equal(0, manager.LastTelemetry.Dispatched);
            Assert.True(manager.LastTelemetry.BudgetBlocked >= 1);
            Assert.Empty(states[0].PendingFleets);
        }
    }
}
