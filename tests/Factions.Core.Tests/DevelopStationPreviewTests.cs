using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The develop menu behind the station window's Develop button. What matters here is that a row
    /// says the same thing the install itself would: the preview and the gate share
    /// <see cref="DevelopStationValidator"/>, and these tests are what keep a future shortcut from
    /// re-deriving either one.
    /// </summary>
    public class DevelopStationPreviewTests
    {
        private static SpatialNode AddOwnedFoundedNode(WorldModel world, string id, string tag)
        {
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            return node;
        }

        private static DevelopOption Row(List<DevelopOption> options, NodeCapabilityKind cap)
        {
            foreach (DevelopOption option in options)
                if (option.Capability == cap) return option;
            throw new Xunit.Sdk.XunitException("no develop row for " + cap);
        }

        [Fact]
        public void For_ReturnsARowForEveryCapability_IncludingTheOnesTheBrainNeverProposes()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "F");

            List<DevelopOption> options = DevelopStationPreview.For(world, world.Tuning, "F", "N1");

            Assert.Equal(CapabilityTokens.PlayerDevelopable.Length, options.Count);
            foreach (NodeCapabilityKind cap in CapabilityTokens.PlayerDevelopable)
                Row(options, cap);   // throws if absent
            // The menu is the enum, not FactionTuning.AllCapabilities -- which omits SensorArray, a
            // capability the develop verb behind this menu accepts.
            Assert.DoesNotContain(NodeCapabilityKind.SensorArray, FactionTuning.AllCapabilities);
            Assert.Equal(NodeCapabilityKind.SensorArray,
                Row(options, NodeCapabilityKind.SensorArray).Capability);
        }

        [Fact]
        public void For_OmitsThePrototechTier_ItIsTheFactorumsNotAPlayersToInstall()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            // Everything the pair's own gate would otherwise stop on: the prerequisite cascade
            // satisfied, the treasury full. The rows are still absent.
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            world.SetTreasury("F", world.Tuning.DevelopCost * 100);

            List<DevelopOption> options = DevelopStationPreview.For(world, world.Tuning, "F", "N1");

            foreach (DevelopOption option in options)
            {
                Assert.NotEqual(NodeCapabilityKind.PrototechForge, option.Capability);
                Assert.NotEqual(NodeCapabilityKind.PrototechRefinery, option.Capability);
            }
            // Still the operator's to grant, and still the brain's to invest in -- only the player
            // menu drops them.
            Assert.Contains(NodeCapabilityKind.PrototechForge, CapabilityTokens.Grantable);
            Assert.Contains(NodeCapabilityKind.PrototechRefinery, CapabilityTokens.Grantable);
            Assert.Contains(NodeCapabilityKind.PrototechForge, FactionTuning.AllCapabilities);
            Assert.Contains(NodeCapabilityKind.PrototechRefinery, FactionTuning.AllCapabilities);
        }

        [Fact]
        public void For_ReportsReadyWithTheCost_WhenNothingIsInTheWay()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Ore:Iron"] = 300;
            world.SetTreasury("F", world.Tuning.DevelopCost);

            DevelopOption refinery = Row(
                DevelopStationPreview.For(world, world.Tuning, "F", "N1"), NodeCapabilityKind.Refinery);

            Assert.Equal(DevelopStationResult.Ok, refinery.Result);
            Assert.Equal(world.Tuning.DevelopCost, refinery.TreasuryCost);
            Assert.Equal("", refinery.BlockKey);
        }

        [Fact]
        public void For_CarriesTheNodesOwnLevelAndTheCapabilitysCeiling()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = 2;

            DevelopOption refinery = Row(
                DevelopStationPreview.For(world, world.Tuning, "F", "N1"), NodeCapabilityKind.Refinery);

            Assert.Equal(2, refinery.Level);
            Assert.Equal(world.Tuning.MaxLevelFor(NodeCapabilityKind.Refinery), refinery.MaxLevel);
        }

        [Fact]
        public void For_LevelIsThisNodesOnly_NotTheNetworks()
        {
            // The prerequisite cascade is network-wide and the level is not: a Refinery next door
            // unblocks Manufactory here without installing anything here.
            WorldModel world = TestSupport.World();
            SpatialNode elsewhere = AddOwnedFoundedNode(world, "N1", "F");
            elsewhere.Capabilities[NodeCapabilityKind.Refinery] = 1;
            SpatialNode target = AddOwnedFoundedNode(world, "N2", "F");
            target.Inventory["Ingot:Iron"] = 200;
            world.SetTreasury("F", world.Tuning.DevelopCost);

            List<DevelopOption> options = DevelopStationPreview.For(world, world.Tuning, "F", "N2");

            Assert.Equal(0, Row(options, NodeCapabilityKind.Refinery).Level);
            Assert.Equal(DevelopStationResult.Ok, Row(options, NodeCapabilityKind.Manufactory).Result);
        }

        [Fact]
        public void For_NamesTheMissingPrerequisite()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "F");
            world.SetTreasury("F", world.Tuning.DevelopCost);

            DevelopOption shipyard = Row(
                DevelopStationPreview.For(world, world.Tuning, "F", "N1"), NodeCapabilityKind.Shipyard);

            Assert.Equal(DevelopStationResult.PrerequisiteMissing, shipyard.Result);
            Assert.Equal(NodeCapabilityKind.Manufactory.ToString(), shipyard.BlockKey);
        }

        [Fact]
        public void For_CarriesTheTreasuryPairBehindAShortfall()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Ore:Iron"] = 300;
            world.SetTreasury("F", world.Tuning.DevelopCost - 400);

            DevelopOption refinery = Row(
                DevelopStationPreview.For(world, world.Tuning, "F", "N1"), NodeCapabilityKind.Refinery);

            Assert.Equal(DevelopStationResult.InsufficientTreasury, refinery.Result);
            Assert.Equal(world.Tuning.DevelopCost - 400, refinery.BlockHave);
            Assert.Equal(world.Tuning.DevelopCost, refinery.BlockNeed);
            // The window subtracts these to say how far short the faction is; a cost of zero here
            // would render that as the whole price.
            Assert.Equal(400, refinery.BlockNeed - refinery.BlockHave);
        }

        [Fact]
        public void For_NamesTheShortItemAndBothItsNumbers()
        {
            WorldModel world = TestSupport.World();
            world.Tuning.InstallBoms[NodeCapabilityKind.Refinery] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Inventory["Comp:SteelPlate"] = 40;
            world.SetTreasury("F", world.Tuning.DevelopCost);

            DevelopOption refinery = Row(
                DevelopStationPreview.For(world, world.Tuning, "F", "N1"), NodeCapabilityKind.Refinery);

            Assert.Equal(DevelopStationResult.InsufficientItemStock, refinery.Result);
            Assert.Equal("Comp:SteelPlate", refinery.BlockKey);
            Assert.Equal(40, refinery.BlockHave);
            Assert.Equal(100, refinery.BlockNeed);
        }

        [Fact]
        public void For_ReportsAlreadyInstalled_AtTheCeiling()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.FuelDepot] = world.Tuning.MaxLevelFor(NodeCapabilityKind.FuelDepot);

            DevelopOption depot = Row(
                DevelopStationPreview.For(world, world.Tuning, "F", "N1"), NodeCapabilityKind.FuelDepot);

            Assert.Equal(DevelopStationResult.AlreadyInstalled, depot.Result);
            // The gate stops before it resolves a price, so there is no cost to show.
            Assert.Equal(0, depot.TreasuryCost);
        }

        [Fact]
        public void For_ReportsNotFounded_ForEveryRow_OnABareClaim()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;   // claimed, never founded

            List<DevelopOption> options = DevelopStationPreview.For(world, world.Tuning, "F", "N1");

            Assert.NotEmpty(options);
            foreach (DevelopOption option in options)
                Assert.Equal(DevelopStationResult.NotFounded, option.Result);
        }

        [Fact]
        public void For_ReturnsNothing_WhenTheNodeIsUnknown()
        {
            WorldModel world = TestSupport.World();

            Assert.Empty(DevelopStationPreview.For(world, world.Tuning, "F", "N404"));
            Assert.Empty(DevelopStationPreview.For(world, world.Tuning, "F", null));
        }

        [Fact]
        public void For_AgreesWithTheValidator_RowForRow()
        {
            // The whole point of the type: a menu that could disagree with the install behind it
            // would show a Ready row the click then refuses.
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Ore:Iron"] = 300;
            node.Inventory["Ingot:Iron"] = 50;   // short for Manufactory's flat 200
            world.SetTreasury("F", world.Tuning.DevelopCost);

            List<DevelopOption> options = DevelopStationPreview.For(world, world.Tuning, "F", "N1");

            foreach (DevelopOption option in options)
            {
                NodeCapabilityKind? missingPrereq; long cost; string missingItemKey;
                long missingItemHave, missingItemNeed;
                DevelopStationResult expected = DevelopStationValidator.Validate(
                    world, world.Tuning, "F", "N1", option.Capability, out missingPrereq, out cost,
                    out missingItemKey, out missingItemHave, out missingItemNeed);

                Assert.Equal(expected, option.Result);
                Assert.Equal(cost, option.TreasuryCost);
            }
        }
    }
}
