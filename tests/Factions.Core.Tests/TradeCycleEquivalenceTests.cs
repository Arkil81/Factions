using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Equivalence coverage beyond TradeCycleTests' single scenario: drives the resumable
    /// sweep one bounded unit at a time against a real fixture per dispatch tier, and asserts the
    /// result matches a synchronous Tick() call exactly. Each tier is the shared core's own
    /// (<c>OperationsManager.DispatchVector</c>), reached with a cross-faction vector.</summary>
    public sealed class TradeCycleEquivalenceTests
    {
        /// <summary>Tier 1: the seller's hull is parked at the node the cargo loads from.</summary>
        private static void BuildTier1LocalScenario(out WorldModel world, out FactionTuning tuning,
            out List<FactionState> states, out TradeTest.NodeScarcityOracle oracle)
        {
            BuildBase(out world, out tuning, out states, out oracle);
            world.Graph.Nodes["A1"].ShipStock["Trade"] = 1;
        }

        /// <summary>Tier 2: the loading node has surplus but no hull; one sits idle at Alt1 instead,
        /// forcing the pickup-chained dispatch.</summary>
        private static void BuildTier2PickupScenario(out WorldModel world, out FactionTuning tuning,
            out List<FactionState> states, out TradeTest.NodeScarcityOracle oracle)
        {
            BuildBase(out world, out tuning, out states, out oracle);
            SpatialNode alt = TradeTest.Node(world, "Alt1", "A", x: 10);
            alt.ShipStock["Trade"] = 1;
        }

        /// <summary>Tier 3: no Trade hull anywhere in the seller's estate, and both ends share a
        /// planet -- so the shipyard-build fallback runs, including the local-Shipyard investment
        /// post.</summary>
        private static void BuildTier3ShipyardFallbackScenario(out WorldModel world, out FactionTuning tuning,
            out List<FactionState> states, out TradeTest.NodeScarcityOracle oracle)
        {
            BuildBase(out world, out tuning, out states, out oracle);
            SpatialNode origin = world.Graph.Nodes["A1"];
            origin.Zone = NodeZone.Surface;
            origin.PlanetId = "Terra";
            // A Shipyard is only asked for where one could be installed: a founded station in the
            // region, and the prerequisite capability somewhere in the network (FAC-238).
            origin.StableStationId = 7001;
            origin.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            SpatialNode bNode = world.Graph.Nodes["B1"];
            bNode.Zone = NodeZone.Surface;
            bNode.PlanetId = "Terra";
        }

        private static void BuildBase(out WorldModel world, out FactionTuning tuning,
            out List<FactionState> states, out TradeTest.NodeScarcityOracle oracle)
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", 1_000_000);
            world.SetTreasury("B", 1_000_000);

            SpatialNode origin = TradeTest.Node(world, "A1", "A");
            origin.Inventory["Iron"] = 500;

            TradeTest.Node(world, "B1", "B", x: 60);

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 0.1)
                .Set("B1", ItemKind.Ore, 2.0);

            FactionState a = TradeTest.State("A");
            a.CentralBankRecentIncome = 1_000_000;
            TradeTest.Whitelist(a, "B");
            FactionState b = TradeTest.State("B");
            b.CentralBankRecentIncome = 1_000_000;
            TradeTest.Unserved(b, "B1", "Iron", 100);

            states = new List<FactionState> { a, b };
        }

        [Fact]
        public void ResumedSweep_Tier1LocalDispatch_PendingFleetMatchesExactly()
        {
            WorldModel expectedWorld; FactionTuning tuning; List<FactionState> expectedStates;
            TradeTest.NodeScarcityOracle expectedOracle;
            BuildTier1LocalScenario(out expectedWorld, out tuning, out expectedStates, out expectedOracle);
            TradeTest.Manager(expectedOracle).Tick(expectedStates, expectedWorld, tuning, new List<string>());

            WorldModel actualWorld; FactionTuning actualTuning; List<FactionState> actualStates;
            TradeTest.NodeScarcityOracle actualOracle;
            BuildTier1LocalScenario(out actualWorld, out actualTuning, out actualStates, out actualOracle);
            RunToCompletion(TradeTest.Manager(actualOracle), actualStates, actualWorld, actualTuning);

            PendingFleetAction expected = Assert.Single(expectedStates[0].PendingFleets);
            PendingFleetAction actual = Assert.Single(actualStates[0].PendingFleets);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.OriginNodeId, actual.OriginNodeId);
            Assert.Equal(expected.TargetNodeId, actual.TargetNodeId);
            Assert.Equal(expected.TargetTag, actual.TargetTag);
            Assert.Equal(expected.Cargo["Iron"], actual.Cargo["Iron"]);
            Assert.Equal(expectedWorld.Graph.Nodes["A1"].Inventory["Iron"],
                         actualWorld.Graph.Nodes["A1"].Inventory["Iron"]);
        }

        [Fact]
        public void ResumedSweep_Tier2PickupDispatch_MatchesSynchronousTick()
        {
            WorldModel expectedWorld; FactionTuning tuning; List<FactionState> expectedStates;
            TradeTest.NodeScarcityOracle expectedOracle;
            BuildTier2PickupScenario(out expectedWorld, out tuning, out expectedStates, out expectedOracle);
            TradeTest.Manager(expectedOracle).Tick(expectedStates, expectedWorld, tuning, new List<string>());

            WorldModel actualWorld; FactionTuning actualTuning; List<FactionState> actualStates;
            TradeTest.NodeScarcityOracle actualOracle;
            BuildTier2PickupScenario(out actualWorld, out actualTuning, out actualStates, out actualOracle);
            RunToCompletion(TradeTest.Manager(actualOracle), actualStates, actualWorld, actualTuning);

            PendingFleetAction expected = Assert.Single(expectedStates[0].PendingFleets);
            PendingFleetAction actual = Assert.Single(actualStates[0].PendingFleets);
            Assert.Equal(FleetStopKind.Pickup, actual.CurrentStopKind);
            Assert.Equal(expected.OriginNodeId, actual.OriginNodeId);   // "Alt1" -- where the hull was
            Assert.Equal(expected.TargetNodeId, actual.TargetNodeId);   // "A1" -- the pickup leg's end
            Assert.Equal("Iron", actual.PickupItemKey);
            Assert.Equal(expected.PickupQuantity, actual.PickupQuantity);
            Assert.Equal(expected.RemainingStops.Count, actual.RemainingStops.Count);
            // Sell via pickup: hand over at B1, then home to the node the cargo came from.
            Assert.Equal(FleetStopKind.Service, actual.RemainingStops[0].Kind);
            Assert.Equal("B1", actual.RemainingStops[0].NodeId);
            Assert.Equal(FleetStopKind.Return, actual.RemainingStops[1].Kind);
            Assert.Equal("A1", actual.RemainingStops[1].NodeId);
        }

        [Fact]
        public void ResumedSweep_Tier3ShipyardFallback_MatchesSynchronousTick_IncludingInvestmentPost()
        {
            WorldModel expectedWorld; FactionTuning tuning; List<FactionState> expectedStates;
            TradeTest.NodeScarcityOracle expectedOracle;
            BuildTier3ShipyardFallbackScenario(out expectedWorld, out tuning, out expectedStates, out expectedOracle);
            TradeTest.Manager(expectedOracle).Tick(expectedStates, expectedWorld, tuning, new List<string>());

            WorldModel actualWorld; FactionTuning actualTuning; List<FactionState> actualStates;
            TradeTest.NodeScarcityOracle actualOracle;
            BuildTier3ShipyardFallbackScenario(out actualWorld, out actualTuning, out actualStates, out actualOracle);
            RunToCompletion(TradeTest.Manager(actualOracle), actualStates, actualWorld, actualTuning);

            Assert.Empty(actualStates[0].PendingFleets); // no hull yet -- the fallback only requests one
            Assert.Single(actualWorld.Investments.SnapshotFor("A"));
            InvestmentRequest req = actualWorld.Investments.SnapshotFor("A")[0];
            Assert.Equal(NodeCapabilityKind.Shipyard, req.Cap);
            Assert.Equal("Terra", req.TargetPlanetId);
            DemandEntry shipDemand = Assert.Single(actualWorld.Demand.SnapshotShipDemands("A"));
            Assert.Equal("A1", shipDemand.NodeId);
            Assert.Equal("B1", shipDemand.DestNodeId);

            Assert.Equal(expectedStates[0].PendingFleets.Count, actualStates[0].PendingFleets.Count);
            Assert.Equal(expectedWorld.Investments.SnapshotFor("A").Count, actualWorld.Investments.SnapshotFor("A").Count);
            Assert.Equal(expectedWorld.Demand.SnapshotShipDemands("A").Count,
                         actualWorld.Demand.SnapshotShipDemands("A").Count);
        }

        /// <summary>The bootstrap teleport is Haul's, never Trade's: a cross-faction vector with no
        /// hull anywhere posts a ship demand and stops. Nothing moves stock across a border for
        /// free.</summary>
        [Fact]
        public void CrossFactionVector_NeverTakesTheBootstrapTeleport()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildBase(out world, out tuning, out states, out oracle);   // no ShipStock anywhere

            var log = new List<string>();
            TradeTest.Manager(oracle).Tick(states, world, tuning, log);

            Assert.Empty(states[0].PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.Equal(500L, world.Graph.Nodes["A1"].Inventory["Iron"]);
            Assert.False(world.Graph.Nodes["B1"].Inventory.ContainsKey("Iron"));
        }

        private static void RunToCompletion(TradeManager manager, IList<FactionState> states, WorldModel world, FactionTuning tuning)
        {
            BeginSweep(manager, states);
            var log = new List<string>();
            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, world, tuning, log);
                steps++;
                Assert.True(steps < 1000, "Trade sweep did not complete");
            }
        }

        private static void BeginSweep(TradeManager manager, IList<FactionState> states)
        {
            Invoke(manager, "BeginSweepForTesting", states);
        }

        private static bool Step(TradeManager manager, IList<FactionState> states,
            WorldModel world, FactionTuning tuning, List<string> log)
        {
            return (bool)Invoke(manager, "StepSweepOnceForTesting", states, world, tuning, log);
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }
    }
}
