using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionStagedWorldEffectsOverlayTests
    {
        [Fact]
        public void ReservationTransfer_MovesOnlyStagedInventoryUntilGuardedCommit()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.Inventory["Ingot:Iron"] = 10;
            var state = new FactionState { Tag = "F" };
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);
            ProductionStagedWorldEffects staged = ProductionStagedWorldEffects.Capture(state, world);

            Assert.Equal(7, staged.MoveInventoryToReserved("N1", "Ingot:Iron", 7));
            Assert.Equal(3, staged.GetNodeInventory("N1", "Ingot:Iron"));
            Assert.Equal(7, staged.GetReservedInventory("N1", "Ingot:Iron"));
            Assert.Equal(10, node.Inventory["Ingot:Iron"]);
            Assert.False(node.ReservedInventory.ContainsKey("Ingot:Iron"));

            Assert.True(staged.TryCommit(guard, state, world));
            Assert.Equal(3, node.Inventory["Ingot:Iron"]);
            Assert.Equal(7, node.ReservedInventory["Ingot:Iron"]);
        }

        [Fact]
        public void DemandHistoryCredit_StaysStagedUntilGuardedCommit()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            var state = new FactionState { Tag = "F" };
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);
            ProductionStagedWorldEffects staged = ProductionStagedWorldEffects.Capture(state, world);

            staged.AddPendingDemandHistoryCredit("N1", "Ingot:Iron", 42.0);

            // Not visible on WorldModel before commit -- draining now must find nothing.
            Assert.Null(world.DrainPendingDemandHistory("N1"));

            Assert.True(staged.TryCommit(guard, state, world));

            var credited = world.DrainPendingDemandHistory("N1");
            Assert.NotNull(credited);
            Assert.Equal(42.0, credited["Ingot:Iron"], 6);
        }

        [Fact]
        public void PriceOracle_UsesCurrentStagedInventoryWithoutCachingBeginAnswer()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.Inventory["Comp:SteelPlate"] = 10;
            var state = new FactionState { Tag = "F" };
            ProductionStagedWorldEffects staged = ProductionStagedWorldEffects.Capture(state, world);
            var live = new CountingOracle();
            var oracle = new ProductionStagedPriceOracle(live, staged, 0.25, 4.0);

            Assert.Equal(1.0, oracle.ScarcityMultiplier("N1", "Comp:SteelPlate"), 8);
            staged.AddNodeInventory("N1", "Comp:SteelPlate", -5);

            // Half the reference on the linear short side: 1 + (4.0 - 1) x 0.5.
            Assert.Equal(2.5, oracle.ScarcityMultiplier("N1", "Comp:SteelPlate"), 8);
            Assert.Equal(2, live.ReferenceCalls);
            Assert.Equal(10, node.Inventory["Comp:SteelPlate"]);
        }

        [Fact]
        public void SnapshotItemDemands_ReflectsStagedPostsFromEarlierInTheSameCycle()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
            world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1").OwnerFactionTag = "F";
            var state = new FactionState { Tag = "F" };
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore:Iron", NodeId = "N1",
                Quantity = 5, ExpiresUtc = world.UtcNow.AddMinutes(1),
            });
            ProductionStagedWorldEffects effects = ProductionStagedWorldEffects.Capture(state, world);

            // Not yet visible from the live world — matches DemandRegister's own same-call visibility
            // requirement: a Post must be visible to a Snapshot later in the same logical pass.
            Assert.DoesNotContain(effects.SnapshotItemDemands(), d => d.Kind == "Item:Ingot:Iron");

            effects.PostDemand(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 10, ExpiresUtc = world.UtcNow.AddMinutes(1),
            });

            List<DemandEntry> snapshot = effects.SnapshotItemDemands();
            Assert.Contains(snapshot, d => d.Kind == "Item:Ore:Iron");   // captured live at begin time
            Assert.Contains(snapshot, d => d.Kind == "Item:Ingot:Iron"); // staged moments earlier, same cycle
            Assert.Empty(world.Demand.SnapshotItemDemands("F").Where(d => d.Kind == "Item:Ingot:Iron")); // still not published
        }

        [Fact]
        public void SnapshotInvestments_ReflectsStagedPostsFromEarlierInTheSameCycle()
        {
            WorldModel world = TestSupport.World();
            world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1").OwnerFactionTag = "F";
            var state = new FactionState { Tag = "F" };
            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 0.5,
                ExpiresUtc = world.UtcNow.AddMinutes(1),
            });
            ProductionStagedWorldEffects effects = ProductionStagedWorldEffects.Capture(state, world);

            effects.PostInvestment(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Manufactory, Urgency = 0.6,
                ExpiresUtc = world.UtcNow.AddMinutes(1),
            });

            List<InvestmentRequest> snapshot = effects.SnapshotInvestments();
            Assert.Contains(snapshot, r => r.Cap == NodeCapabilityKind.Refinery);
            Assert.Contains(snapshot, r => r.Cap == NodeCapabilityKind.Manufactory);
            Assert.Empty(world.Investments.SnapshotFor("F").Where(r => r.Cap == NodeCapabilityKind.Manufactory));
        }

        [Fact]
        public void GetFactionInventory_SumsAcrossNodes_ClassifiedTheSameWayAsWorldModel()
        {
            WorldModel world = TestSupport.World();
            SpatialNode n1 = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            n1.OwnerFactionTag = "F";
            n1.Inventory["Ore:Iron"] = 100;
            n1.Inventory["Ingot:Iron"] = 7; // must not bleed into the Ore aggregate — same subtype, different kind
            SpatialNode n2 = world.Graph.Add(new Vec3(1, 0, 0), 0.5, "N2");
            n2.OwnerFactionTag = "F";
            n2.Inventory["Ore:Nickel"] = 40;
            var state = new FactionState { Tag = "F" };
            ProductionStagedWorldEffects effects = ProductionStagedWorldEffects.Capture(state, world);

            Assert.Equal(world.GetInventory("F", ItemKind.Ore), effects.GetFactionInventory(ItemKind.Ore));
            Assert.Equal(140, effects.GetFactionInventory(ItemKind.Ore));
            Assert.Equal(world.GetInventory("F", ItemKind.Ingot), effects.GetFactionInventory(ItemKind.Ingot));
            Assert.Equal(107, effects.GetFactionInventoryByKey("Ore:Iron") + effects.GetFactionInventoryByKey("Ingot:Iron"));

            effects.AddNodeInventory("N1", "Ore:Iron", -30);

            Assert.Equal(110, effects.GetFactionInventory(ItemKind.Ore));
            Assert.Equal(140, world.GetInventory("F", ItemKind.Ore)); // unstaged live world unaffected
        }

        private sealed class CountingOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            public int ReferenceCalls;
            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference) { reference = 0; return false; }
            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return 1.0; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                ReferenceCalls++;
                reference = 10;
                return true;
            }
        }
    }
}
