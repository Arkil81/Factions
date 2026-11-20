using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Resumable-sweep coverage for TradeManager. Chunking is at the (faction, direction)
    /// boundary with no staged-effects/commit-guard apparatus -- see TradeManager.Cycle.cs's header
    /// for why: each unit runs one whole faction's pass in one direction synchronously and
    /// atomically, so there is no staged state for another manager's concurrent mutation to
    /// invalidate, and deliberately no GuardInvalidation test.
    ///
    /// <para>The sweep walks the roster TWICE, buy direction first. That ordering is the subject of
    /// its own test here, because it is what makes the design's per-row rule ("the owner buys for
    /// itself first, then a seller may fly to it") independent of roster order.</para></summary>
    public sealed class TradeCycleTests
    {
        /// <summary>Four-faction roster: "A" sells into "B"'s unserved row (Tier 1, local hull);
        /// "IDLE1"/"IDLE2" have no whitelist at all, so they are visited but never dispatch. Gives a
        /// deterministic step count: one StepSweepOnce per (faction, direction), plus one per
        /// direction to notice the roster is exhausted.</summary>
        private static void BuildScenario(out WorldModel world, out FactionTuning tuning,
            out List<FactionState> states, out TradeTest.NodeScarcityOracle oracle)
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", 1_000_000);
            world.SetTreasury("B", 1_000_000);

            SpatialNode origin = TradeTest.Node(world, "A1", "A");
            origin.Inventory["Iron"] = 500;
            origin.ShipStock["Trade"] = 1;

            TradeTest.Node(world, "B1", "B", x: 60);

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 0.1)
                .Set("B1", ItemKind.Ore, 2.0);

            FactionState a = TradeTest.State("A");
            a.CentralBankRecentIncome = 1_000_000;
            TradeTest.Whitelist(a, "B");
            FactionState b = TradeTest.State("B");
            b.CentralBankRecentIncome = 1_000_000;
            TradeTest.Whitelist(b, "A");
            TradeTest.Unserved(b, "B1", "Iron", 100);

            states = new List<FactionState> { a, b, TradeTest.State("IDLE1"), TradeTest.State("IDLE2") };
        }

        [Fact]
        public void TryAdvance_ExpiredDeadline_YieldsWithoutPublishingAnything()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);
            var log = new List<string>();

            bool complete = manager.TryAdvance(states, world, tuning, new FrameDeadline(0, 0.0), log);

            Assert.False(complete);
            Assert.Empty(states[0].PendingFleets);
            Assert.Empty(log);
            Assert.True(manager.LastTelemetry.Yields >= 1);
        }

        [Fact]
        public void ResumedSweep_MatchesSynchronousTick_AtCompletion()
        {
            WorldModel expectedWorld; FactionTuning tuning; List<FactionState> expectedStates;
            TradeTest.NodeScarcityOracle expectedOracle;
            BuildScenario(out expectedWorld, out tuning, out expectedStates, out expectedOracle);
            var expectedLog = new List<string>();
            TradeTest.Manager(expectedOracle).Tick(expectedStates, expectedWorld, tuning, expectedLog);

            WorldModel actualWorld; FactionTuning actualTuning; List<FactionState> states;
            TradeTest.NodeScarcityOracle actualOracle;
            BuildScenario(out actualWorld, out actualTuning, out states, out actualOracle);
            var actualLog = new List<string>();
            TradeManager manager = TradeTest.Manager(actualOracle);
            BeginSweep(manager, states);

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualTuning, actualLog);
                steps++;
                Assert.True(steps < 1000, "Trade sweep did not complete");
            }

            // Exact and deterministic: one step per (faction, direction), plus one per direction to
            // notice the roster is exhausted.
            Assert.Equal(2 * states.Count + 2, steps);

            Assert.Equal(expectedStates[0].PendingFleets.Count, states[0].PendingFleets.Count);
            Assert.Single(states[0].PendingFleets);
            Assert.Equal("B", states[0].PendingFleets[0].TargetTag);
            Assert.Equal(expectedWorld.Graph.Nodes["A1"].Inventory["Iron"],
                         actualWorld.Graph.Nodes["A1"].Inventory["Iron"]);
            AssertSemanticLogsEqual(expectedLog, actualLog);
            Assert.Equal(1, manager.LastTelemetry.SweepsCompleted);
            Assert.Equal(0, manager.LastTelemetry.Restarts);
        }

        [Fact]
        public void ResumedSweep_PublishesEachFactionImmediatelyOnItsOwnStep_NotDeferredToSweepEnd()
        {
            // Deliberately the OPPOSITE invariant from OperationsCycleTests' equivalent test: there is
            // no staging here, so a faction's dispatch is visible in the live world as soon as its own
            // step runs. The sell direction is the second roster walk, so "A" dispatches on the step
            // after the buy walk finishes -- which is also what makes the claim it takes visible to
            // every later step.
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            BeginSweep(manager, states);

            bool complete = false;
            int steps = 0;
            while (!complete && states[0].PendingFleets.Count == 0)
            {
                complete = Step(manager, states, world, tuning, log);
                steps++;
                Assert.True(steps < 1000, "Trade sweep did not complete");
            }

            Assert.False(complete);                       // published mid-sweep, not at the end
            Assert.Single(states[0].PendingFleets);
            Assert.Equal(states.Count + 2, steps);        // the whole buy walk, its flip, then A sells

            while (!complete) complete = Step(manager, states, world, tuning, log);
        }

        /// <summary>The buy direction is a whole roster walk ahead of the sell direction, so a buyer
        /// that owns a hull claims its own row even when its seller sits earlier in the roster. With
        /// one interleaved walk the seller would win on list order alone — which is the failure this
        /// asserts against, not merely "someone served the row".</summary>
        [Fact]
        public void BuyWalkPrecedesSellWalk_AcrossTheWholeRoster()
        {
            WorldModel world; FactionTuning tuning; List<FactionState> states; TradeTest.NodeScarcityOracle oracle;
            BuildScenario(out world, out tuning, out states, out oracle);
            world.Graph.Nodes["B1"].ShipStock["Trade"] = 1;   // the buyer can fly for itself

            TradeTest.Manager(oracle).Tick(states, world, tuning, new List<string>());

            Assert.Empty(states[0].PendingFleets);                 // A, first in the roster, did not sell
            PendingFleetAction fleet = Assert.Single(states[1].PendingFleets);
            Assert.True(fleet.IsProcurement);
        }

        [Fact]
        public void MaxTradeFleets_AtCap_SkipsDispatch_ThenResumesOnceSlotFrees_AcrossBothTickAndSteppedSweep()
        {
            WorldModel tickWorld; FactionTuning tuning; List<FactionState> tickStates; TradeTest.NodeScarcityOracle tickOracle;
            BuildScenario(out tickWorld, out tuning, out tickStates, out tickOracle);
            tuning.MaxTradeFleets = 1;
            tickStates[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "A", TargetTag = "C", Resolved = false, Intercepted = false,
            });
            TradeTest.Manager(tickOracle).Tick(tickStates, tickWorld, tuning, new List<string>());
            Assert.Single(tickStates[0].PendingFleets);   // the cap blocked a new dispatch

            PendingFleetAction resolvedFleet = tickStates[0].PendingFleets[0];
            resolvedFleet.Resolved = true;                // frees the slot
            tickStates[0].PendingFleets[0] = resolvedFleet;
            tickWorld.UtcNow = tickWorld.UtcNow.AddSeconds(1);
            TradeTest.Manager(tickOracle).Tick(tickStates, tickWorld, tuning, new List<string>());
            Assert.Equal(2, tickStates[0].PendingFleets.Count);

            WorldModel stepWorld; FactionTuning stepTuning; List<FactionState> stepStates; TradeTest.NodeScarcityOracle stepOracle;
            BuildScenario(out stepWorld, out stepTuning, out stepStates, out stepOracle);
            stepTuning.MaxTradeFleets = 1;
            stepStates[0].PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "A", TargetTag = "C", Resolved = false, Intercepted = false,
            });
            TradeManager manager = TradeTest.Manager(stepOracle);
            RunSweepToCompletion(manager, stepStates, stepWorld, stepTuning);
            Assert.Single(stepStates[0].PendingFleets);

            PendingFleetAction stepResolvedFleet = stepStates[0].PendingFleets[0];
            stepResolvedFleet.Resolved = true;
            stepStates[0].PendingFleets[0] = stepResolvedFleet;
            stepWorld.UtcNow = stepWorld.UtcNow.AddSeconds(1);
            RunSweepToCompletion(manager, stepStates, stepWorld, stepTuning);
            Assert.Equal(2, stepStates[0].PendingFleets.Count);
        }

        private static void RunSweepToCompletion(TradeManager manager, IList<FactionState> states, WorldModel world, FactionTuning tuning)
        {
            BeginSweep(manager, states);
            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, world, tuning, new List<string>());
                steps++;
                Assert.True(steps < 1000, "Trade sweep did not complete");
            }
        }

        private static void AssertSemanticLogsEqual(List<string> expectedLog, List<string> actualLog)
        {
            Assert.Equal(Without(expectedLog), Without(actualLog));

            List<string> Without(List<string> log)
            {
                var result = new List<string>();
                foreach (string line in log)
                    if (!line.StartsWith("TRADE_TICK_PERF ", StringComparison.Ordinal)) result.Add(line);
                return result;
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
