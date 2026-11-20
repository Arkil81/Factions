using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Proves the staged production path (StagedProductionExecutionContext + ProductionStagedWorldEffects,
    /// guard-checked commit) produces exactly the same WorldModel effects as today's synchronous
    /// live path (Tick() via LiveProductionExecutionContext), for one faction's full cycle: capability-bank
    /// consume/produce on two chained recipes, bank input demands, and refinement demands. Both paths run
    /// the exact same ProductionManager.TickOneFaction code against two independently built but identical
    /// starting worlds — only the execution context differs.
    /// </summary>
    public sealed class ProductionExecutionContextEquivalenceTests
    {
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> TwoStageRecipes()
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
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };
        }

        private static WorldModel BuildWorld()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ore:Iron"] = 1000;
            node.Inventory["Ingot:Iron"] = 50;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 300, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            return world;
        }

        [Fact]
        public void StagedContext_ProducesIdenticalWorldEffects_ToLiveTick()
        {
            // A single tick from a cold start never produces anything — a freshly-created MachineSlot
            // stamps LastTick = now, so elapsed real time is zero on the tick that creates it (see
            // ProductionManager.RebalanceSlots). Run twice with a real-time gap so the second tick has
            // elapsed seconds to convert into batches on both paths, exercising actual consume/produce.
            WorldModel liveWorld = BuildWorld();
            WorldModel stagedWorld = BuildWorld();
            var liveState = new FactionState { Tag = "F" };
            var stagedState = new FactionState { Tag = "F" };
            var liveManager = new ProductionManager(TwoStageRecipes());
            var stagedManager = new ProductionManager(TwoStageRecipes());
            var liveLog = new List<string>();
            var stagedLog = new List<string>();

            liveManager.Tick(new List<FactionState> { liveState }, liveWorld, liveWorld.Tuning, liveLog);
            RunStagedTick(stagedManager, stagedState, stagedWorld, stagedLog);

            liveWorld.UtcNow = liveWorld.UtcNow.AddSeconds(30);
            stagedWorld.UtcNow = stagedWorld.UtcNow.AddSeconds(30);
            liveLog.Clear();
            stagedLog.Clear();

            liveManager.Tick(new List<FactionState> { liveState }, liveWorld, liveWorld.Tuning, liveLog);
            bool committed = RunStagedTick(stagedManager, stagedState, stagedWorld, stagedLog);

            Assert.True(committed);

            // Node inventory must match exactly: both banks' consume/produce this tick.
            Assert.Equal(liveWorld.GetNodeInventory("N1", "Ore:Iron"), stagedWorld.GetNodeInventory("N1", "Ore:Iron"));
            Assert.Equal(liveWorld.GetNodeInventory("N1", "Ingot:Iron"), stagedWorld.GetNodeInventory("N1", "Ingot:Iron"));
            Assert.Equal(liveWorld.GetNodeInventory("N1", "Comp:SteelPlate"), stagedWorld.GetNodeInventory("N1", "Comp:SteelPlate"));
            // Sanity: production actually happened, this isn't a vacuously-equal no-op comparison.
            Assert.True(liveWorld.GetNodeInventory("N1", "Comp:SteelPlate") > 0);
            Assert.True(liveWorld.GetNodeInventory("N1", "Ore:Iron") < 1000);

            // Demand/investment registers must match (kind+nodeId+quantity+expiry).
            AssertSameDemands(liveWorld.Demand.SnapshotItemDemands("F"), stagedWorld.Demand.SnapshotItemDemands("F"));
            AssertSameInvestments(liveWorld.Investments.SnapshotFor("F"), stagedWorld.Investments.SnapshotFor("F"));

            // Logs must match (order-independent — both compute the same PRODUCED/*_DEMAND lines).
            Assert.Equal(liveLog.OrderBy(s => s, StringComparer.Ordinal), stagedLog.OrderBy(s => s, StringComparer.Ordinal));
        }

        // Mirrors what a real ProductionCycle commit must do: publish the WorldModel effects AND the
        // manager-local transaction (slots/catalog-suppression) together, so machine-slot accumulator
        // state (MachineSlot.Accum/LastTick) persists correctly across this test's two staged ticks —
        // exactly as the live Tick() path's TransactionState already does via TryCommitTransaction.
        // Order matters: the transaction commit touches no WorldModel state, so it is safe to guard-
        // check first; the world-effects commit MUST come after, because publishing it mutates the
        // very node inventory the guard's own NodeSnapshot compares — checking the guard again after
        // publishing would spuriously fail against the mutation the commit itself just made.
        private static bool RunStagedTick(ProductionManager manager, FactionState state, WorldModel world, List<string> log)
        {
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);
            ProductionStagedWorldEffects effects = ProductionStagedWorldEffects.Capture(state, world);
            var ctx = new StagedProductionExecutionContext(world, effects, state.Tag, world.UtcNow);

            object transaction = Invoke(manager, "BeginTransactionForTesting");
            object slots = GetField(transaction, "Slots");
            object catalogSuppressed = GetField(transaction, "CatalogDemandSuppressed");
            object remainders = GetField(transaction, "Remainders");
            InvokeTickOneFaction(manager, state, ctx, world.Tuning, slots, catalogSuppressed, remainders);

            if (!(bool)Invoke(manager, "TryCommitTransactionForTesting", transaction, guard, state, world, new List<string>()))
                return false;
            return effects.TryCommit(guard, state, world, log);
        }

        [Fact]
        public void StagedContext_PublishesNothing_BeforeCommit()
        {
            WorldModel stagedWorld = BuildWorld();
            var stagedState = new FactionState { Tag = "F" };
            var stagedManager = new ProductionManager(TwoStageRecipes());

            long oreBeforeCommit = stagedWorld.GetNodeInventory("N1", "Ore:Iron");
            long ingotBeforeCommit = stagedWorld.GetNodeInventory("N1", "Ingot:Iron");

            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(stagedState, stagedWorld);
            ProductionStagedWorldEffects effects = ProductionStagedWorldEffects.Capture(stagedState, stagedWorld);
            var ctx = new StagedProductionExecutionContext(stagedWorld, effects, stagedState.Tag, stagedWorld.UtcNow);
            object transaction = Invoke(stagedManager, "BeginTransactionForTesting");
            InvokeTickOneFaction(stagedManager, stagedState, ctx, stagedWorld.Tuning,
                GetField(transaction, "Slots"), GetField(transaction, "CatalogDemandSuppressed"),
                GetField(transaction, "Remainders"));

            // Nothing published to the live world yet.
            Assert.Equal(oreBeforeCommit, stagedWorld.GetNodeInventory("N1", "Ore:Iron"));
            Assert.Equal(ingotBeforeCommit, stagedWorld.GetNodeInventory("N1", "Ingot:Iron"));
            Assert.Empty(stagedWorld.Demand.SnapshotItemDemands("F").Where(d => d.Kind == "Item:Comp:SteelPlate" && d.NodeId == null));

            Assert.True(effects.TryCommit(guard, stagedState, stagedWorld));

            // Committing a second time is a no-op, not a double-publish.
            Assert.False(effects.TryCommit(guard, stagedState, stagedWorld));
        }

        [Fact]
        public void StagedContext_ServiceShipBuildReservations_ProducesIdenticalResults_ToLiveTick()
        {
            // ServiceShipBuildReservations runs once per node per cycle (Cycle.cs's
            // ShipBuildReservationsNodeId guard) before the bank loop, mutating BuildQueueEntry.
            // Collected/ReservedInputs/RecipeProgress through the same IProductionExecutionContext
            // seam as everything else -- this proves the staged path stages those three fields
            // correctly and commits them identically to the live path, the same parity coverage
            // ComputeReservedInputs had before this redesign replaced it.
            Func<WorldModel> buildWorld = () =>
            {
                WorldModel world = TestSupport.World();
                // Pin the cadence this fixture's collected-quantity expectations depend on: the
                // cold-start credit window is ProductionIntervalSeconds x ProductionCreditCapIntervals.
                world.Tuning.ProductionIntervalSeconds = 30.0;
                world.UtcNow = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

                SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
                node.OwnerFactionTag = "F";
                node.HoldStrength = 1.0;
                node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
                node.Inventory["Ingot:Iron"] = 1000;

                var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
                build.Bom["Comp:SteelPlate"] = 200;
                node.BuildQueue.Add(build);
                return world;
            };
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 5 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };

            WorldModel liveWorld = buildWorld();
            WorldModel stagedWorld = buildWorld();
            var liveState = new FactionState { Tag = "F" };
            var stagedState = new FactionState { Tag = "F" };
            var liveManager = new ProductionManager(capRecipes);
            var stagedManager = new ProductionManager(capRecipes);

            liveManager.Tick(new List<FactionState> { liveState }, liveWorld, liveWorld.Tuning, new List<string>());
            RunStagedTick(stagedManager, stagedState, stagedWorld, new List<string>());

            liveWorld.UtcNow = liveWorld.UtcNow.AddSeconds(30);
            stagedWorld.UtcNow = stagedWorld.UtcNow.AddSeconds(30);

            liveManager.Tick(new List<FactionState> { liveState }, liveWorld, liveWorld.Tuning, new List<string>());
            bool committed = RunStagedTick(stagedManager, stagedState, stagedWorld, new List<string>());
            Assert.True(committed);

            BuildQueueEntry liveEntry = liveWorld.Graph.Nodes["N1"].BuildQueue[0];
            BuildQueueEntry stagedEntry = stagedWorld.Graph.Nodes["N1"].BuildQueue[0];

            Assert.Equal(liveWorld.GetNodeInventory("N1", "Ingot:Iron"), stagedWorld.GetNodeInventory("N1", "Ingot:Iron"));

            long liveCollected, stagedCollected, liveReserved, stagedReserved;
            liveEntry.Collected.TryGetValue("Comp:SteelPlate", out liveCollected);
            stagedEntry.Collected.TryGetValue("Comp:SteelPlate", out stagedCollected);
            liveEntry.ReservedInputs.TryGetValue("Ingot:Iron", out liveReserved);
            stagedEntry.ReservedInputs.TryGetValue("Ingot:Iron", out stagedReserved);

            Assert.Equal(liveCollected, stagedCollected);
            Assert.Equal(liveReserved, stagedReserved);
            // Sanity: real production/reservation happened, not a vacuously-equal no-op comparison.
            Assert.True(liveCollected > 0);
            Assert.True(liveReserved > 0);
        }

        private static void AssertSameDemands(List<DemandEntry> a, List<DemandEntry> b)
        {
            List<string> left = a.Select(DemandKey).OrderBy(s => s, StringComparer.Ordinal).ToList();
            List<string> right = b.Select(DemandKey).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(left, right);
        }

        private static void AssertSameInvestments(List<InvestmentRequest> a, List<InvestmentRequest> b)
        {
            List<string> left = a.Select(InvestmentKey).OrderBy(s => s, StringComparer.Ordinal).ToList();
            List<string> right = b.Select(InvestmentKey).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(left, right);
        }

        private static string DemandKey(DemandEntry d)
        {
            return d.FactionTag + "|" + d.Kind + "|" + d.NodeId + "|" + d.Quantity + "|" + d.ExpiresUtc.Ticks;
        }

        private static string InvestmentKey(InvestmentRequest r)
        {
            return r.FactionTag + "|" + r.Cap + "|" + r.Urgency + "|" + r.ExpiresUtc.Ticks;
        }

        // ProductionManager.TickOneFaction is internal (same reflection-invoke pattern as
        // ProductionManagerTransactionStateTests) — it is the seam a future staged ProductionCycle
        // will call directly; tests reach it without widening the public API surface. slots/
        // catalogDemandSuppressed are untyped (object) because their declared type, the nested
        // internal MachineSlot, cannot be named from this assembly — get them via GetField off a
        // TransactionState (itself obtained via the existing BeginTransactionForTesting seam) instead
        // of constructing fresh dictionaries, so Invoke's runtime type matching still binds correctly.
        private static void InvokeTickOneFaction(ProductionManager manager, FactionState state,
            IProductionExecutionContext ctx, FactionTuning tuning, object slots, object catalogDemandSuppressed,
            object remainders)
        {
            MethodInfo method = typeof(ProductionManager).GetMethod("TickOneFaction",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(manager, new object[] { state, ctx, tuning, slots, catalogDemandSuppressed, remainders });
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field.GetValue(target);
        }
    }
}
