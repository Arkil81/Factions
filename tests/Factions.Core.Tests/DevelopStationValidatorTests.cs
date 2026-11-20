using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CapabilityPrerequisitesTests
    {
        [Theory]
        [InlineData(NodeCapabilityKind.Manufactory, NodeCapabilityKind.Refinery)]
        [InlineData(NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.Manufactory)]
        [InlineData(NodeCapabilityKind.Shipyard, NodeCapabilityKind.Manufactory)]
        [InlineData(NodeCapabilityKind.RefineryII, NodeCapabilityKind.Refinery)]
        public void For_ReturnsExpectedUpstream(NodeCapabilityKind cap, NodeCapabilityKind expectedPrereq)
        {
            Assert.Equal(expectedPrereq, CapabilityPrerequisites.For(cap));
        }

        [Theory]
        [InlineData(NodeCapabilityKind.Refinery)]
        [InlineData(NodeCapabilityKind.FuelDepot)]
        public void For_ReturnsNull_WhenNoPrerequisite(NodeCapabilityKind cap)
        {
            Assert.Null(CapabilityPrerequisites.For(cap));
        }
    }

    public class DevelopStationValidatorTests
    {
        private static SpatialNode AddOwnedFoundedNode(WorldModel world, string id, string tag)
        {
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            node.StableStationId = 1; // any non-zero value marks the node as founded
            return node;
        }

        [Fact]
        public void Validate_ReturnsOk_WhenOwnedFoundedAffordableNoPrereqNeeded()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Ore:Iron"] = 300; // Refinery's flat install cost
            world.SetTreasury("F", world.Tuning.DevelopCost);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.Ok, result);
            Assert.Equal(world.Tuning.DevelopCost, cost);
        }

        [Fact]
        public void Validate_ReturnsNodeNotFound_WhenNodeIdUnknown()
        {
            WorldModel world = TestSupport.World();

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N404",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.NodeNotFound, result);
        }

        [Fact]
        public void Validate_ReturnsNotOwned_WhenNodeBelongsToAnotherFaction()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "RIVAL");

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.NotOwned, result);
        }

        [Fact]
        public void Validate_ReturnsNotFounded_WhenStableStationIdIsZero()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            // StableStationId left at 0 -- never founded.

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.NotFounded, result);
        }

        [Fact]
        public void Validate_ReturnsOk_WhenCapabilityBelowMaxLevel_AllowingUpgrade()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1; // below MaxRefineryUpgrades (15)
            node.Inventory["Ore:Iron"] = 300; // Refinery's flat install/upgrade cost
            world.SetTreasury("F", world.Tuning.DevelopCost);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.Ok, result);
        }

        [Fact]
        public void Validate_ReturnsAlreadyInstalled_WhenCapabilityAtMaxLevel()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = world.Tuning.MaxRefineryUpgrades;

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.AlreadyInstalled, result);
        }

        [Fact]
        public void Validate_ReturnsAlreadyInstalled_ForSingleLevelCapability_WhenAlreadyInstalledOnce()
        {
            // SensorArray has no slot-based upgrades (MaxLevelFor == 1), so level 1 is already the max
            // -- unlike Refinery/Manufactory/Shipyard/FuelDepot, a second `develop` must still reject.
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.SensorArray] = 1;

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.SensorArray, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.AlreadyInstalled, result);
        }

        [Theory]
        [InlineData(NodeCapabilityKind.Manufactory, NodeCapabilityKind.Refinery)]
        [InlineData(NodeCapabilityKind.Shipyard, NodeCapabilityKind.Manufactory)]
        [InlineData(NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.Manufactory)]
        [InlineData(NodeCapabilityKind.RefineryII, NodeCapabilityKind.Refinery)]
        public void Validate_ReturnsPrerequisiteMissing_WhenUpstreamCapabilityNotInstalledAnywhere(
            NodeCapabilityKind cap, NodeCapabilityKind expectedMissing)
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "F");
            world.SetTreasury("F", world.Tuning.DevelopCost);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                cap, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.PrerequisiteMissing, result);
            Assert.Equal(expectedMissing, missingPrereq);
        }

        [Fact]
        public void Validate_ReturnsOk_WhenPrerequisiteSatisfiedByADifferentOwnedNode()
        {
            WorldModel world = TestSupport.World();
            SpatialNode refineryNode = AddOwnedFoundedNode(world, "N1", "F");
            refineryNode.Capabilities[NodeCapabilityKind.Refinery] = 1;
            SpatialNode targetNode = AddOwnedFoundedNode(world, "N2", "F");
            targetNode.Inventory["Ingot:Iron"] = 200; // Manufactory's flat install cost (Ingot)
            world.SetTreasury("F", world.Tuning.DevelopCost);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N2",
                NodeCapabilityKind.Manufactory, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.Ok, result);
        }

        [Fact]
        public void Validate_ReturnsInsufficientTreasury_WhenBelowDevelopCost()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Ore:Iron"] = 300;
            world.SetTreasury("F", world.Tuning.DevelopCost - 1);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.InsufficientTreasury, result);
        }

        [Fact]
        public void Validate_ReturnsInsufficientItemStock_ForFlatItemKindPath()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Ore:Iron"] = 299; // Refinery needs 300 Ore -- one short
            world.SetTreasury("F", world.Tuning.DevelopCost);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.InsufficientItemStock, result);
            Assert.Equal(ItemKind.Ore.ToString(), missingItemKey);
            Assert.Equal(299, missingItemHave);
            Assert.Equal(300, missingItemNeed);
        }

        [Fact]
        public void Validate_ReturnsInsufficientItemStock_ForBomBackedPath()
        {
            WorldModel world = TestSupport.World();
            world.Tuning.InstallBoms[NodeCapabilityKind.Refinery] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Comp:SteelPlate"] = 50; // short of the registered BOM
            world.SetTreasury("F", world.Tuning.DevelopCost);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.InsufficientItemStock, result);
            Assert.Equal("Comp:SteelPlate", missingItemKey);
            Assert.Equal(50, missingItemHave);
            Assert.Equal(100, missingItemNeed);
        }

        [Fact]
        public void Validate_ReturnsOk_ForBomBackedPath_WhenStockSufficient()
        {
            WorldModel world = TestSupport.World();
            world.Tuning.InstallBoms[NodeCapabilityKind.Refinery] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Comp:SteelPlate"] = 100;
            world.SetTreasury("F", world.Tuning.DevelopCost);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                NodeCapabilityKind.Refinery, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.Ok, result);
        }

        [Theory]
        [InlineData(NodeCapabilityKind.PrototechForge)]
        [InlineData(NodeCapabilityKind.PrototechRefinery)]
        public void Validate_RefusesThePrototechTier_EvenWithEveryOtherGateSatisfied(NodeCapabilityKind cap)
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            world.Tuning.InstallBoms.Remove(cap);
            world.SetTreasury("F", world.Tuning.DevelopCost * 100);

            NodeCapabilityKind? missingPrereq; long cost; string missingItemKey; long missingItemHave; long missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(world, world.Tuning, "F", "N1",
                cap, out missingPrereq, out cost, out missingItemKey, out missingItemHave, out missingItemNeed);

            Assert.Equal(DevelopStationResult.NotPlayerDevelopable, result);
            Assert.False(CapabilityTokens.IsPlayerDevelopable(cap));
            // The refusal is the capability's, not the world's: the same call on the same node for a
            // vanilla capability is judged on its own gates instead.
            Assert.NotEqual(DevelopStationResult.NotPlayerDevelopable, DevelopStationValidator.Validate(
                world, world.Tuning, "F", "N1", NodeCapabilityKind.SensorArray, out missingPrereq, out cost,
                out missingItemKey, out missingItemHave, out missingItemNeed));
        }
    }
}
