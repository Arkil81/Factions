using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class OperationsCycleTests
    {
        [Fact]
        public void TryAdvance_ExpiredDeadline_YieldsWithoutPublishingAnything()
        {
            WorldModel world = BuildWorld();
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var manager = new OperationsManager();
            var log = new List<string>();

            bool complete = manager.TryAdvance(states, world, world.Tuning,
                new FrameDeadline(0, 0.0), log);

            Assert.False(complete);
            Assert.Equal(100, world.GetNodeInventory("Source", "Ore"));
            Assert.Equal(0, world.GetNodeInventory("Dest", "Ore"));
            Assert.Empty(states[0].PendingFleets);
            Assert.Empty(log);
            Assert.True(manager.LastTelemetry.Yields >= 1);
        }

        [Fact]
        public void ResumedSweep_PublishesOneFactionAtomically_AndMatchesSynchronousTick()
        {
            WorldModel expectedWorld = BuildWorld();
            var expectedState = new FactionState { Tag = "F" };
            var expectedLog = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { expectedState }, expectedWorld,
                expectedWorld.Tuning, expectedLog);

            WorldModel actualWorld = BuildWorld();
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager();
            BeginSweep(manager, states);

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
                if (!complete)
                {
                    Assert.Equal(100, actualWorld.GetNodeInventory("Source", "Ore"));
                    Assert.Equal(0, actualWorld.GetNodeInventory("Dest", "Ore"));
                    Assert.Empty(actualState.PendingFleets);
                    Assert.Empty(actualLog);
                }
            }

            Assert.True(steps > 3);
            Assert.Equal(expectedWorld.GetNodeInventory("Source", "Ore"), actualWorld.GetNodeInventory("Source", "Ore"));
            Assert.Equal(expectedWorld.GetNodeInventory("Dest", "Ore"), actualWorld.GetNodeInventory("Dest", "Ore"));
            Assert.Equal(expectedState.PendingFleets.Count, actualState.PendingFleets.Count);
            AssertSemanticLogsEqual(expectedLog, actualLog);
            Assert.Equal(1, manager.LastTelemetry.SweepsCompleted);
        }

        [Fact]
        public void GuardInvalidation_DiscardsAllStagedEffects_ThenRestartsFromFreshInput()
        {
            WorldModel expectedWorld = BuildWorld();
            expectedWorld.AddNodeInventory("Source", "Ore", 25);
            var expectedState = new FactionState { Tag = "F" };
            var expectedLog = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { expectedState }, expectedWorld,
                expectedWorld.Tuning, expectedLog);

            WorldModel actualWorld = BuildWorld();
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager();
            BeginSweep(manager, states);

            Assert.False(Step(manager, states, actualWorld, actualLog)); // select/capture faction
            Assert.True(IsFactionCycleInProgress(manager));
            Assert.False(Step(manager, states, actualWorld, actualLog)); // advance staged work
            actualWorld.AddNodeInventory("Source", "Ore", 25);          // invalidate captured read set

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 2000, "invalidated Operations cycle did not restart and complete");
            }

            Assert.True(manager.LastTelemetry.Restarts >= 1);
            Assert.Equal(expectedWorld.GetNodeInventory("Source", "Ore"), actualWorld.GetNodeInventory("Source", "Ore"));
            Assert.Equal(expectedWorld.GetNodeInventory("Dest", "Ore"), actualWorld.GetNodeInventory("Dest", "Ore"));
            AssertSemanticLogsEqual(expectedLog, actualLog);
        }

        /// <summary>Compares every semantic log line exactly, but excludes OPS_TICK_PERF — that line
        /// is gated by a real 1ms wall-clock threshold (see OperationsManager.PerfLogThresholdMs), and
        /// expectedLog/actualLog come from two independently-timed stopwatch runs (a synchronous Tick()
        /// vs. a multi-step staged cycle in the same test process). Under xUnit's parallel test
        /// execution, CPU contention can push either run's real elapsed time across the threshold
        /// independently of the other, making an exact match on that one diagnostic-only line
        /// nondeterministic — unrelated to the atomicity/equivalence this test actually verifies.</summary>
        private static void AssertSemanticLogsEqual(List<string> expectedLog, List<string> actualLog)
        {
            Assert.Equal(Without(expectedLog), Without(actualLog));

            List<string> Without(List<string> log)
            {
                var result = new List<string>();
                foreach (string line in log)
                    if (!line.StartsWith("OPS_TICK_PERF ", StringComparison.Ordinal)) result.Add(line);
                return result;
            }
        }

        private static WorldModel BuildWorld()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            SpatialNode source = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Source");
            source.OwnerFactionTag = "F";
            source.Inventory["Ore"] = 100;
            SpatialNode dest = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "Dest");
            dest.OwnerFactionTag = "F";
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore", NodeId = "Dest", Quantity = 40,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            return world;
        }

        private static void BeginSweep(OperationsManager manager, IList<FactionState> states)
        {
            Invoke(manager, "BeginSweepForTesting", states);
        }

        private static bool Step(OperationsManager manager, IList<FactionState> states,
            WorldModel world, List<string> log)
        {
            return (bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, log);
        }

        private static bool IsFactionCycleInProgress(OperationsManager manager)
        {
            return (bool)Invoke(manager, "IsFactionCycleInProgressForTesting");
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
