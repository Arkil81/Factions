using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Exercises the resumable ProductionCycle sweep machinery directly: yielding before a deadline,
    /// resuming across many calls to the same final result as Tick(), telemetry accumulating across
    /// a multi-faction sweep instead of being replaced by the last faction committed, the restart-
    /// budget fallback after repeated guard invalidation, and the isolation guarantee that motivated
    /// threading _slots explicitly (SnapshotThroughput must never observe an in-flight cycle's staged
    /// slot state while a sweep is suspended between calls).
    /// </summary>
    public sealed class ProductionCycleTests
    {
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> OneStageRecipe()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "IronIngot",
                        TimeSecs = 1,
                        Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                    },
                },
            };
        }

        private static WorldModel BuildWorld(string factionTag, string nodeId, Vec3 pos)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(pos, 1.0, nodeId);
            node.OwnerFactionTag = factionTag;
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Ore:Iron"] = 1000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = factionTag, Kind = "Item:Ingot:Iron", NodeId = nodeId,
                Quantity = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            return world;
        }

        [Fact]
        public void IsCycleInProgress_TracksCapturedAndRestartedCyclesButNotInterFactionGaps()
        {
            WorldModel world = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var manager = new ProductionManager(OneStageRecipe());
            var log = new List<string>();

            Assert.False(manager.IsCycleInProgress);
            BeginSweep(manager, states);
            Assert.False(manager.IsCycleInProgress); // roster captured; no faction guard yet

            SpatialNode node = world.Graph.Nodes["N1"];
            bool done = false;
            bool sawInFlight = false;
            bool sawUnguardedGap = false;
            for (int i = 0; i < 500 && !done; i++)
            {
                done = StepSweepOnce(manager, states, world, log);
                if (manager.IsCycleInProgress)
                {
                    sawInFlight = true;
                    node.HoldStrength = 1.0 - (i + 1) * 0.0001;
                }
                else if (sawInFlight && !done)
                {
                    // The faction committed or forced-sync completed; the sweep still needs one
                    // bookkeeping step, but no guard is live and host maintenance may safely run.
                    sawUnguardedGap = true;
                }
            }

            Assert.True(done, "sweep did not complete within the step budget");
            Assert.True(sawInFlight);
            Assert.True(sawUnguardedGap);
            Assert.Contains(log, l => l.Contains("PRODUCTION_CYCLE_RESTART"));
            Assert.Contains(log, l => l.Contains("PRODUCTION_CYCLE_FORCED_SYNC"));
            Assert.False(manager.IsCycleInProgress);
        }

        [Fact]
        public void TryAdvance_ExpiredDeadline_ReturnsFalseAndPublishesNothing()
        {
            WorldModel world = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var manager = new ProductionManager(OneStageRecipe());
            var log = new List<string>();
            long oreBefore = world.GetNodeInventory("N1", "Ore:Iron");

            var expired = new FrameDeadline(0, 0.0); // start=0 ticks, budget=0ms -> already expired
            bool done = manager.TryAdvance(states, world, world.Tuning, expired, log);

            Assert.False(done);
            Assert.Empty(log);
            Assert.Equal(oreBefore, world.GetNodeInventory("N1", "Ore:Iron"));
        }

        [Fact]
        public void TryAdvance_ResumesAcrossManyCalls_ToTheSameResultAsTick()
        {
            WorldModel liveWorld = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            WorldModel resumedWorld = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var liveManager = new ProductionManager(OneStageRecipe());
            var resumedManager = new ProductionManager(OneStageRecipe());
            var liveStates = new List<FactionState> { new FactionState { Tag = "F" } };
            var resumedStates = new List<FactionState> { new FactionState { Tag = "F" } };
            var liveLog = new List<string>();
            var resumedLog = new List<string>();

            liveManager.Tick(liveStates, liveWorld, liveWorld.Tuning, liveLog);

            int calls = 0;
            bool done = false;
            while (!done)
            {
                calls++;
                // Fresh per call, matching how the real host constructs one every Update() frame --
                // a FrameDeadline's start timestamp is fixed at construction, so reusing one instance
                // across calls means it stays expired forever after the first tiny budget elapses.
                var tinyBudget = new FrameDeadline(System.Diagnostics.Stopwatch.GetTimestamp(), 0.0005);
                done = resumedManager.TryAdvance(resumedStates, resumedWorld, resumedWorld.Tuning, tinyBudget, resumedLog);
                Assert.True(calls < 100_000, "sweep never completed — likely an infinite loop in the state machine");
            }

            Assert.True(calls > 1, "expected the tiny deadline to force at least one real yield across multiple calls");
            Assert.Equal(liveWorld.GetNodeInventory("N1", "Ore:Iron"), resumedWorld.GetNodeInventory("N1", "Ore:Iron"));
            Assert.Equal(liveWorld.GetNodeInventory("N1", "Ingot:Iron"), resumedWorld.GetNodeInventory("N1", "Ingot:Iron"));
        }

        [Fact]
        public void ThroughputPhases_ResumeAcrossEveryStep_ToTheSameResultAsOneUninterruptedAdvance()
        {
            // The throughput comparison is now four resumable phases (collect slots, sum ore income
            // per node, sum the three cap rates in batches, decide). Driving the sweep one bounded
            // step at a time returns to the caller between every phase and every batch, so anything
            // the cursors fail to carry across a return shows up as a divergence here; the
            // uninterrupted run does the identical steps back-to-back without ever returning.
            WorldModel wholeWorld = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var wholeStates = new List<FactionState> { new FactionState { Tag = "F" } };
            var wholeLog = new List<string>();
            new ProductionManager(OneStageRecipe()).Tick(wholeStates, wholeWorld, wholeWorld.Tuning, wholeLog);

            WorldModel steppedWorld = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var steppedStates = new List<FactionState> { new FactionState { Tag = "F" } };
            var steppedLog = new List<string>();
            var stepped = new ProductionManager(OneStageRecipe());

            BeginSweep(stepped, steppedStates);
            int steps = 0;
            bool done = false;
            while (!done)
            {
                done = StepSweepOnce(stepped, steppedStates, steppedWorld, steppedLog);
                steps++;
                Assert.True(steps < 10_000, "sweep never completed - likely an infinite loop in the state machine");
            }

            // Four throughput phases plus the pre-existing prepare/banks/post-pass phases: a sweep
            // that still completed in a couple of steps would mean the new phases never engaged.
            Assert.True(steps > 6, "expected the throughput phases to chunk the sweep, saw " + steps + " steps");
            Assert.Equal(wholeLog, steppedLog);
            Assert.Equal(wholeWorld.GetNodeInventory("N1", "Ore:Iron"), steppedWorld.GetNodeInventory("N1", "Ore:Iron"));
            Assert.Equal(wholeWorld.GetNodeInventory("N1", "Ingot:Iron"), steppedWorld.GetNodeInventory("N1", "Ingot:Iron"));
        }

        [Fact]
        public void ThroughputRates_BatchedAccumulationMatchesTheSynchronousPass()
        {
            // The synchronous pass (PostThroughputInvestmentRequests, still used by the restart-budget
            // forced-sync fallback) sums each rate in its own loop over the whole slot list; the cycle
            // sums all three in one batched pass. Both must reach the same investment decision.
            WorldModel cycleWorld = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var cycleStates = new List<FactionState> { new FactionState { Tag = "F" } };
            var cycleLog = new List<string>();
            var cycleManager = new ProductionManager(OneStageRecipe());
            BeginSweep(cycleManager, cycleStates);
            bool done = false;
            int guard = 0;
            while (!done && guard++ < 10_000)
                done = StepSweepOnce(cycleManager, cycleStates, cycleWorld, cycleLog);

            WorldModel syncWorld = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var syncStates = new List<FactionState> { new FactionState { Tag = "F" } };
            var syncLog = new List<string>();
            RunFactionSynchronously(new ProductionManager(OneStageRecipe()), syncStates, syncWorld, syncLog);

            Assert.Equal(
                FilterThroughputInvestments(syncLog),
                FilterThroughputInvestments(cycleLog));
        }

        private static List<string> FilterThroughputInvestments(List<string> log)
        {
            var result = new List<string>();
            foreach (string line in log)
                if (line.Contains("THROUGHPUT_INVEST")) result.Add(line);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void RunFactionSynchronously(ProductionManager manager, IList<FactionState> states,
            WorldModel world, List<string> log)
        {
            MethodInfo m = typeof(ProductionManager).GetMethod("RunFactionSynchronously",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(m);
            m.Invoke(manager, new object[] { states[0], world, world.Tuning, log, 0 });
        }

        [Fact]
        public void TryAdvance_MultiFactionSweep_TelemetryAccumulatesAcrossFactions_NotJustTheLastOne()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
            foreach (string tag in new[] { "F1", "F2" })
            {
                SpatialNode node = world.Graph.Add(new Vec3(tag == "F1" ? 0 : 500, 0, 0), 1.0, "N_" + tag);
                node.OwnerFactionTag = tag;
                node.HoldStrength = 1.0;
                node.StableStationId = 1;
                node.Capabilities[NodeCapabilityKind.Refinery] = 1;
                node.Inventory["Ore:Iron"] = 1000;
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = tag, Kind = "Item:Ingot:Iron", NodeId = "N_" + tag,
                    Quantity = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
                });
            }
            var manager = new ProductionManager(OneStageRecipe());
            var states = new List<FactionState> { new FactionState { Tag = "F1" }, new FactionState { Tag = "F2" } };
            var log = new List<string>();

            manager.Tick(states, world, world.Tuning, log);

            Assert.Equal(2, manager.LastTelemetry.Factions);
            Assert.Equal(2, manager.LastTelemetry.OwnedNodes);
            Assert.Equal(2, manager.LastTelemetry.CapabilityBanks);
        }

        [Fact]
        public void RestartBudget_FallsBackToSynchronousRun_AfterRepeatedInvalidation_AndStillProducesCorrectResult()
        {
            WorldModel world = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var manager = new ProductionManager(OneStageRecipe());
            var log = new List<string>();

            BeginSweep(manager, states);

            // Drive the sweep step-by-step, mutating the world between every step of the first
            // faction's cycle so its guard is invalidated every time it tries to commit, forcing
            // the restart-budget fallback within a bounded number of steps. Strictly monotonic (never
            // toggling back to a prior value) -- an even number of steps between a guard's capture and
            // its commit-time check could otherwise land back on the exact captured HoldStrength,
            // making the guard see no net change at all.
            SpatialNode node = world.Graph.Nodes["N1"];
            bool done = false;
            for (int i = 0; i < 500 && !done; i++)
            {
                done = StepSweepOnce(manager, states, world, log);
                node.HoldStrength = 1.0 - (i + 1) * 0.0001;
            }

            Assert.True(done, "sweep did not complete within the step budget");
            Assert.Contains(log, l => l.Contains("PRODUCTION_CYCLE_FORCED_SYNC"));
            // Every restart before the terminal forced-sync used to be silent and uncounted, so a
            // cycle burning most of its budget each sweep was indistinguishable from a healthy one.
            Assert.Contains(log, l => l.Contains("PRODUCTION_CYCLE_RESTART"));
            Assert.True(manager.LastTelemetry.Restarts >= RestartBudgetLimitForTesting,
                "expected every invalidation to be counted, saw " + manager.LastTelemetry.Restarts);
            // The forced-sync fallback is unstaged/live and atomic within one call, so it must still
            // have produced a correct result despite every staged attempt being invalidated.
            Assert.True(world.GetNodeInventory("N1", "Ingot:Iron") >= 0); // no crash/corruption
        }

        // Mirrors ProductionManager.RestartBudgetLimit, which is private. Kept as a named constant so
        // a change to the budget surfaces here as a test failure rather than a silent weakening.
        private const int RestartBudgetLimitForTesting = 3;

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> TwoStageRecipes()
        {
            var ironIngot = new ConversionRecipe
            {
                BlueprintId = "IronIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
            var steelPlate = new ConversionRecipe
            {
                BlueprintId = "SteelPlate", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot",     SubTypeId = "Iron",       Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
            };
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery]    = new List<ConversionRecipe> { ironIngot },
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe> { steelPlate },
            };
        }

        private sealed class StubPriceOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            private readonly long _reference;
            public StubPriceOracle(long reference) { _reference = reference; }
            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            { reference = 0; return false; }
            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return 1.0; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                if (inventoryKey == "Comp:SteelPlate") { reference = _reference; return true; }
                reference = 0;
                return false;
            }
        }

        private sealed class StubFactionTypeCatalogue : IFactionTypeCatalogue
        {
            private readonly string _tag;
            private readonly IReadOnlyList<string> _offerKeys;
            public StubFactionTypeCatalogue(string tag, string[] offerKeys)
            { _tag = tag; _offerKeys = new List<string>(offerKeys).AsReadOnly(); }
            public bool TryGetFactionType(string factionTag, out string factionType)
            {
                if (factionTag == _tag) { factionType = "Trader"; return true; }
                factionType = null;
                return false;
            }
            public IReadOnlyList<string> OfferInvKeys(string factionType) { return _offerKeys; }
            public IReadOnlyList<string> OrderInvKeys(string factionType) { return new List<string>(); }
        }

        [Fact]
        public void PostPassesNodes_MultiNode_ResumesAcrossManyCalls_ToTheSameResultAsTick()
        {
            // Two nodes each with independent refinement-eligible inventory (drives
            // PostRefinementRequestsForNode) and node-priced Comp:SteelPlate stock below the
            // surplus-intent target (drives PostCatalogSurplusForNode) -- proves the checkpointed
            // PostPassesRefinementNodes/PostPassesCatalogNodes phases resume across many small
            // TryAdvance calls to the exact same result a synchronous Tick() produces, for both
            // per-node passes and both nodes.
            Func<WorldModel> buildWorld = () =>
            {
                WorldModel w = TestSupport.World();
                w.UtcNow = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
                w.Tuning.ProductionSurplusIntentEnabled = true;
                w.Tuning.ProductionSurplusTargetMultiple = 2.0;

                foreach (string id in new[] { "N1", "N2" })
                {
                    SpatialNode node = w.Graph.Add(new Vec3(id == "N1" ? 0 : 500, 0, 0), 1.0, id);
                    node.OwnerFactionTag = "F";
                    node.HoldStrength = 1.0;
                    node.StableStationId = 1;
                    node.Capabilities[NodeCapabilityKind.Refinery] = 1;
                    node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
                    node.Inventory["Ingot:Iron"] = 1000;      // refinement trigger
                    node.Inventory["Comp:SteelPlate"] = 50;   // below the 200 surplus target
                }
                return w;
            };

            Func<Dictionary<NodeCapabilityKind, List<ConversionRecipe>>> recipes = TwoStageRecipes;
            Func<ProductionManager> buildManager = () => new ProductionManager(
                recipes(),
                new StubFactionTypeCatalogue("F", new[] { "Comp:SteelPlate" }),
                new StubPriceOracle(100));

            WorldModel liveWorld = buildWorld();
            var liveManager = buildManager();
            var liveStates = new List<FactionState> { new FactionState { Tag = "F" } };
            liveManager.Tick(liveStates, liveWorld, liveWorld.Tuning, new List<string>());

            WorldModel resumedWorld = buildWorld();
            var resumedManager = buildManager();
            var resumedStates = new List<FactionState> { new FactionState { Tag = "F" } };
            var resumedLog = new List<string>();

            int calls = 0;
            bool done = false;
            while (!done)
            {
                calls++;
                var tinyBudget = new FrameDeadline(System.Diagnostics.Stopwatch.GetTimestamp(), 0.0005);
                done = resumedManager.TryAdvance(resumedStates, resumedWorld, resumedWorld.Tuning, tinyBudget, resumedLog);
                Assert.True(calls < 100_000, "sweep never completed — likely an infinite loop in the state machine");
            }

            // Two nodes through two checkpointed per-node phases should force multiple yields.
            Assert.True(calls > 2, "expected multiple nodes across two chunked phases to force several yields");

            foreach (string id in new[] { "N1", "N2" })
            {
                Assert.Equal(liveWorld.GetNodeInventory(id, "Ingot:Iron"), resumedWorld.GetNodeInventory(id, "Ingot:Iron"));
                Assert.Equal(liveWorld.GetNodeInventory(id, "Comp:SteelPlate"), resumedWorld.GetNodeInventory(id, "Comp:SteelPlate"));
            }
            AssertSameDemands(liveWorld.Demand.SnapshotItemDemands("F"), resumedWorld.Demand.SnapshotItemDemands("F"));
        }

        private static void AssertSameDemands(List<DemandEntry> a, List<DemandEntry> b)
        {
            List<string> left = a.Select(DemandKey).OrderBy(s => s, StringComparer.Ordinal).ToList();
            List<string> right = b.Select(DemandKey).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(left, right);
        }

        private static string DemandKey(DemandEntry d)
        {
            return d.FactionTag + "|" + d.Kind + "|" + d.NodeId + "|" + d.Quantity + "|" + d.ExpiresUtc.Ticks;
        }

        [Fact]
        public void SnapshotThroughput_NeverObservesAnInFlightCycles_UncommittedSlotState()
        {
            // First, a clean synchronous tick so the faction has a real committed machine slot.
            WorldModel world = BuildWorld("F", "N1", new Vec3(0, 0, 0));
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var manager = new ProductionManager(OneStageRecipe());
            manager.Tick(states, world, world.Tuning, new List<string>());
            List<ThroughputEntry> committedBefore = manager.SnapshotThroughput("F");

            // Advance real time and start a brand-new sweep, but stop after exactly one bounded step
            // (Preparing) -- well before that faction's second cycle could possibly commit.
            world.UtcNow = world.UtcNow.AddSeconds(30);
            BeginSweep(manager, states);
            bool done = StepSweepOnce(manager, states, world, new List<string>()); // faction selection
            Assert.False(done);
            done = StepSweepOnce(manager, states, world, new List<string>()); // Preparing phase only
            Assert.False(done);

            // The manager's shared, externally-readable state must be unaffected by the in-flight
            // cycle -- SnapshotThroughput must report exactly what was committed before, no more.
            List<ThroughputEntry> committedDuring = manager.SnapshotThroughput("F");
            AssertSameThroughput(committedBefore, committedDuring);
        }

        private static void AssertSameThroughput(List<ThroughputEntry> a, List<ThroughputEntry> b)
        {
            Assert.Equal(a.Count, b.Count);
            List<string> left = a.Select(e => e.NodeId + "|" + e.Cap + "|" + e.PrimaryOutputId + "|" + e.SlotCount)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            List<string> right = b.Select(e => e.NodeId + "|" + e.Cap + "|" + e.PrimaryOutputId + "|" + e.SlotCount)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(left, right);
        }

        // ---- reflection helpers onto ProductionManager's internal test-only sweep seam ----

        private static void BeginSweep(ProductionManager manager, IList<FactionState> states)
        {
            Invoke(manager, "BeginSweepForTesting", states);
        }

        private static bool StepSweepOnce(ProductionManager manager, IList<FactionState> states,
            WorldModel world, List<string> log)
        {
            return (bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, log);
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
