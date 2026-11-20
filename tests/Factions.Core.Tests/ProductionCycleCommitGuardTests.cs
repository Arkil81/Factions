using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionCycleCommitGuardTests
    {
        [Fact]
        public void Capture_UnchangedRelevantState_RemainsValid()
        {
            WorldModel world = Fixture();

            FactionState state = State();
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);

            Assert.True(guard.IsStillValid(state, world));
            Assert.Equal(world.UtcNow, guard.CapturedUtc);
        }

        [Fact]
        public void Capture_RejectsOwnershipCapabilityInventoryReservationAndBuildChanges()
        {
            AssertInvalidAfter(delegate(WorldModel world) { world.Graph.Nodes["N1"].OwnerFactionTag = "Other"; });
            AssertInvalidAfter(delegate(WorldModel world) { world.Graph.Nodes["N1"].Capabilities[NodeCapabilityKind.Manufactory] = 2; });
            AssertInvalidAfter(delegate(WorldModel world) { world.Graph.Nodes["N1"].StableStationId = 44; });
            AssertInvalidAfter(delegate(WorldModel world) { world.Graph.Nodes["N1"].ResourceValue = 0.9; });
            AssertInvalidAfter(delegate(WorldModel world) { world.Graph.Nodes["N1"].HoldStrength = 0.5; });
            AssertInvalidAfter(delegate(WorldModel world) { world.Graph.Nodes["N1"].Position = new Vec3(3, 0, 0); });
            AssertInvalidAfter(delegate(WorldModel world) { world.AddNodeInventory("N1", "Comp:SteelPlate", 1); });
            AssertInvalidAfter(delegate(WorldModel world) { world.Graph.Nodes["N1"].ReservedInventory["Comp:SteelPlate"] = 1; });
            AssertInvalidAfter(delegate(WorldModel world)
            {
                world.Graph.Nodes["N1"].BuildQueue[0].Collected["Comp:SteelPlate"] = 1;
            });
        }

        [Fact]
        public void Capture_RejectsDemandInvestmentAndOwnedNodeSetChanges_ButIgnoresOtherFactionSignals()
        {
            AssertInvalidAfter(delegate(WorldModel world)
            {
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = "N1",
                    Quantity = 4, ExpiresUtc = world.UtcNow.AddMinutes(2)
                });
            });
            AssertInvalidAfter(delegate(WorldModel world)
            {
                world.Investments.Post(new InvestmentRequest
                {
                    FactionTag = "F", Cap = NodeCapabilityKind.RefineryII,
                    Urgency = 0.9, ExpiresUtc = world.UtcNow.AddMinutes(2)
                });
            });
            AssertInvalidAfter(delegate(WorldModel world)
            {
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                    Quantity = 11, ExpiresUtc = world.UtcNow.AddMinutes(3)
                });
            });
            AssertInvalidAfter(delegate(WorldModel world)
            {
                world.Investments.Post(new InvestmentRequest
                {
                    FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                    Urgency = 0.7, ExpiresUtc = world.UtcNow.AddMinutes(3)
                });
            });
            AssertInvalidAfter(delegate(WorldModel world)
            {
                SpatialNode added = world.Graph.Add(new Vec3(2, 0, 0), 0.3, "N2");
                added.OwnerFactionTag = "F";
            });
            AssertInvalidAfter(delegate(WorldModel world)
            {
                SpatialNode rival = world.Graph.Add(new Vec3(8, 0, 0), 0.3, "R1");
                rival.OwnerFactionTag = "Other";
                rival.HoldStrength = 0.8;
            });

            WorldModel unchangedForFaction = Fixture();
            FactionState state = State();
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, unchangedForFaction);
            unchangedForFaction.Demand.Post(new DemandEntry
            {
                FactionTag = "Other", Kind = "Item:Comp:Thrust", NodeId = "Elsewhere",
                Quantity = 4, ExpiresUtc = unchangedForFaction.UtcNow.AddMinutes(2)
            });
            unchangedForFaction.Investments.Post(new InvestmentRequest
            {
                FactionTag = "Other", Cap = NodeCapabilityKind.RefineryII,
                Urgency = 0.9, ExpiresUtc = unchangedForFaction.UtcNow.AddMinutes(2)
            });

            Assert.True(guard.IsStillValid(state, unchangedForFaction));
        }

        [Fact]
        public void Capture_IgnoresInfluenceSourceChange_FarBeyondCutoffRadius()
        {
            // InfluenceField.Sample/DominantAt (what Production's InfluenceControl ultimately calls)
            // skip any source farther than 4x its own radius — it contributes exactly zero. A rival
            // appearing or changing far outside that range of every owned node position can never
            // affect anything Production reads, so it must not invalidate an in-flight staged cycle.
            WorldModel world = Fixture(); // N1 at (1,0,0)
            FactionState state = State();
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);

            SpatialNode farRival = world.Graph.Add(new Vec3(500, 0, 0), 0.3, "FarRival");
            farRival.OwnerFactionTag = "Other";
            farRival.Radius = 10; // cutoff 40 around FarRival; distance to N1 is 499 either way
            farRival.HoldStrength = 0.9;

            Assert.True(guard.IsStillValid(state, world));

            // A later change on that same far node must also stay ignored.
            farRival.HoldStrength = 0.1;
            Assert.True(guard.IsStillValid(state, world));
        }

        [Fact]
        public void Capture_RejectsInfluenceSourceChange_WithinCutoffRadius()
        {
            WorldModel world = Fixture(); // N1 at (1,0,0)
            FactionState state = State();
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);

            // The cutoff is 4x the RIVAL's own radius, and since FAC-250 that is derived from what the
            // rival is rather than a flat 40 km. A founded station reaches
            // InfluenceRadiusFoundedKm (24) x 4 = 96 km, so at distance 49 it genuinely bears on N1.
            SpatialNode nearRival = world.Graph.Add(new Vec3(50, 0, 0), 0.3, "NearRival"); // distance 49 < 96
            nearRival.OwnerFactionTag = "Other";
            nearRival.StableStationId = 99;
            nearRival.HoldStrength = 0.9;

            Assert.False(guard.IsStillValid(state, world));
        }

        [Fact]
        public void Capture_RejectsDormancyChange()
        {
            WorldModel world = Fixture();
            FactionState state = State();
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);

            state.DormantUntilUtc = world.UtcNow.AddHours(1);

            Assert.False(guard.IsStillValid(state, world));
        }

        private static void AssertInvalidAfter(Action<WorldModel> mutate)
        {
            WorldModel world = Fixture();
            FactionState state = State();
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);
            mutate(world);
            Assert.False(guard.IsStillValid(state, world));
        }

        private static FactionState State()
        {
            return new FactionState { Tag = "F" };
        }

        private static WorldModel Fixture()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(1, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:SteelPlate"] = 20;
            node.BuildQueue.Add(new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "TradeShip",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(1)
            });
            node.BuildQueue[0].Bom["Comp:SteelPlate"] = 10;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 10, ExpiresUtc = world.UtcNow.AddMinutes(2)
            });
            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 0.6, ExpiresUtc = world.UtcNow.AddMinutes(2)
            });
            return world;
        }
    }
}
