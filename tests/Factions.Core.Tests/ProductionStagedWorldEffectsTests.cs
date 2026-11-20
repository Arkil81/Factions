using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionStagedWorldEffectsTests
    {
        [Fact]
        public void Commit_PublishesOverlayOnlyAfterGuardValidation()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.Inventory["Ore:Iron"] = 10;
            node.ReservedInventory["Ore:Iron"] = 2;
            var state = new FactionState { Tag = "F" };
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);
            ProductionStagedWorldEffects staged = ProductionStagedWorldEffects.Capture(state, world);

            staged.AddNodeInventory("N1", "Ore:Iron", -4);
            staged.AddReservedInventory("N1", "Ore:Iron", 3);
            staged.PostDemand(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1", Quantity = 8,
                ExpiresUtc = world.UtcNow.AddMinutes(1)
            });
            staged.PostInvestment(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 0.6,
                ExpiresUtc = world.UtcNow.AddMinutes(2)
            });
            staged.AddLog("F PRODUCED Ingot:Iron +2");
            var log = new System.Collections.Generic.List<string>();

            Assert.Equal(6, staged.GetNodeInventory("N1", "Ore:Iron"));
            Assert.Equal(5, staged.GetReservedInventory("N1", "Ore:Iron"));
            Assert.Equal(10, node.Inventory["Ore:Iron"]);
            Assert.Equal(2, node.ReservedInventory["Ore:Iron"]);
            Assert.Empty(world.Demand.SnapshotItemDemands("F"));
            Assert.Empty(world.Investments.SnapshotFor("F"));
            Assert.Empty(log);

            Assert.True(staged.TryCommit(guard, state, world, log));

            Assert.Equal(6, node.Inventory["Ore:Iron"]);
            Assert.Equal(5, node.ReservedInventory["Ore:Iron"]);
            Assert.Single(world.Demand.SnapshotItemDemands("F"));
            Assert.Single(world.Investments.SnapshotFor("F"));
            Assert.Equal(new[] { "F PRODUCED Ingot:Iron +2" }, log);
        }

        [Fact]
        public void Commit_RejectsChangedReadSetWithoutPublishingEffects()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.Inventory["Ore:Iron"] = 10;
            var state = new FactionState { Tag = "F" };
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);
            ProductionStagedWorldEffects staged = ProductionStagedWorldEffects.Capture(state, world);
            staged.AddNodeInventory("N1", "Ore:Iron", -4);

            world.AddNodeInventory("N1", "Ore:Iron", 1);

            Assert.False(staged.TryCommit(guard, state, world));
            Assert.Equal(11, node.Inventory["Ore:Iron"]);
        }

        [Fact]
        public void PostInvestment_DifferentTargetPlanetId_BothSurviveTheSameCycle()
        {
            // Same (FactionTag, Cap) but different TargetPlanetId -- must not collapse into one
            // entry here just because the real InvestmentRegister keeps them separate (the landmine
            // this staging mirror's own key had to be fixed alongside the register's).
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
            var state = new FactionState { Tag = "F" };
            ProductionStagedWorldEffects staged = ProductionStagedWorldEffects.Capture(state, world);

            staged.PostInvestment(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                ExpiresUtc = world.UtcNow.AddMinutes(2), TargetPlanetId = "Terra",
            });
            staged.PostInvestment(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.5,
                ExpiresUtc = world.UtcNow.AddMinutes(2), TargetPlanetId = "Mars",
            });

            var snapshot = staged.SnapshotInvestments();
            Assert.Equal(2, snapshot.Count);
            Assert.Contains(snapshot, r => r.TargetPlanetId == "Terra");
            Assert.Contains(snapshot, r => r.TargetPlanetId == "Mars");
        }
    

        [Fact]
        public void PostInvestment_DifferentTargetFeature_AllSurviveTheSameCycle_AndEachIsNew()
        {
            // Five nests asked for a refinery in one production cycle on v0.174.0's first pass; the
            // overlay's key ignored TargetFeature, so four of them read "standing" on the pass that
            // posted them and the cycle's own snapshot held only the last. Same landmine as the
            // planet key above, one field later.
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var state = new FactionState { Tag = "F" };
            ProductionStagedWorldEffects staged = ProductionStagedWorldEffects.Capture(state, world);

            foreach (string nest in new[] { "Hollow Nest", "Far Nest Umbra", "Far Nest Vigil" })
                Assert.True(staged.PostInvestment(NodeRegion.Feature(nest).Scope(new InvestmentRequest
                {
                    FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 0.2,
                    ExpiresUtc = world.UtcNow.AddMinutes(2),
                })), nest + " should be a fresh post");
            Assert.False(staged.PostInvestment(NodeRegion.Feature("Hollow Nest").Scope(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 0.3,
                ExpiresUtc = world.UtcNow.AddMinutes(2),
            })));

            var snapshot = staged.SnapshotInvestments();
            Assert.Equal(3, snapshot.Count);
            Assert.Contains(snapshot, r => r.TargetFeature == "Far Nest Vigil");
        }
    

        [Fact]
        public void OperationsOverlay_PostInvestment_DifferentTargetFeature_AllSurvive_AndEachIsNew()
        {
            // The Operations overlay had the same key as the Production one: (Tag, Cap, PlanetId,
            // Site) with no TargetFeature, so two nests' dispatch-failure Shipyard asks in one
            // Operations cycle collapsed to one in the cycle's snapshot.
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            var state = new FactionState { Tag = "F" };
            OperationsStagedWorldEffects staged = OperationsStagedWorldEffects.Capture(state, world);

            foreach (string nest in new[] { "Hollow Nest", "Far Nest Umbra" })
                Assert.True(staged.PostInvestment(NodeRegion.Feature(nest).Scope(new InvestmentRequest
                {
                    FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                    ExpiresUtc = world.UtcNow.AddMinutes(2),
                })), nest + " should be a fresh post");

            // The overlay exposes no snapshot; its new-versus-refresh answer is the observable, and
            // a repeat for one nest must read as the refresh it is.
            Assert.False(staged.PostInvestment(NodeRegion.Feature("Hollow Nest").Scope(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                ExpiresUtc = world.UtcNow.AddMinutes(2),
            })));
        }
    }
}
