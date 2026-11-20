using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// What the throughput snapshot claims versus what the bank actually did. The nominal rate is
    /// capacity at the current slot assignment; on its own it cannot distinguish a bank running flat
    /// out from one starved of ore, which is how a six-fold shortfall went unnoticed.
    /// </summary>
    public class ProductionThroughputReportingTests
    {
        private const string OreKey = "Ore:Iron";
        private const string OutKey = "Ingot:Iron";

        private static ConversionRecipe Recipe()
        {
            return new ConversionRecipe
            {
                BlueprintId = "IronIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
        }

        private static ThroughputEntry RunAndSnapshot(long orePerPass, int passes,
            MachineProfile profile = null, double productivityBonus = 0.0)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionIntervalSeconds = 30.0;
            world.Tuning.MaxRefineryUpgrades = 1;
            world.Tuning.ProductionProductivityBonus = productivityBonus;
            if (profile != null) world.Tuning.MachineProfiles[NodeCapabilityKind.RefineryII] = profile;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + OutKey, NodeId = "N1",
                Quantity = 99_999_999, ExpiresUtc = world.UtcNow.AddDays(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.RefineryII] = new List<ConversionRecipe> { Recipe() },
            };
            var mgr = new ProductionManager(capRecipes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            node.Inventory[OreKey] = orePerPass;
            mgr.Tick(states, world, world.Tuning, new List<string>());   // prime the slots
            for (int pass = 0; pass < passes; pass++)
            {
                node.Inventory[OreKey] = orePerPass;
                node.Inventory[OutKey] = 0;      // keep the recipe scarce so the bank stays active
                world.UtcNow = world.UtcNow.AddSeconds(30);
                mgr.Tick(states, world, world.Tuning, new List<string>());
            }

            foreach (ThroughputEntry e in mgr.SnapshotThroughput("F"))
                if (e.PrimaryOutputId == OutKey) return e;
            throw new InvalidOperationException("no throughput entry for " + OutKey);
        }

        [Fact]
        public void AWellFedBank_ReportsAnObservedRateMatchingItsCapacity()
        {
            ThroughputEntry e = RunAndSnapshot(orePerPass: 10_000_000, passes: 10);

            // 6 slots x speed 1.30 / TimeSecs 1 x 60 s = 468 items/min of capacity.
            Assert.Equal(468.0, e.NominalItemsPerMinute, 3);
            Assert.Equal(6, e.SlotCount);
            Assert.InRange(e.ObservedItemsPerMinute, 0.9 * e.NominalItemsPerMinute, 1.1 * e.NominalItemsPerMinute);
        }

        [Fact]
        public void AStarvedBank_ReportsFarLessThanItsCapacity()
        {
            // A hundred ore a pass against a bank that could eat 234: the capacity number alone
            // would claim full production, which is how a shortfall stays invisible.
            ThroughputEntry e = RunAndSnapshot(orePerPass: 100, passes: 10);

            Assert.Equal(468.0, e.NominalItemsPerMinute, 3);
            Assert.True(e.ObservedItemsPerMinute > 0, "the bank did produce something");
            Assert.True(e.ObservedItemsPerMinute < 0.75 * e.NominalItemsPerMinute,
                "observed " + e.ObservedItemsPerMinute + " should sit clearly below capacity "
                    + e.NominalItemsPerMinute);
        }

        [Fact]
        public void CapacityReflectsTheWorldMultipliersAndYieldScaling()
        {
            MachineProfile profile = MachinePhysics.DefaultProfile(NodeCapabilityKind.RefineryII);
            profile.WorldSpeedMultiplier = 3.0;
            profile.MaterialEfficiency = 0.5;

            ThroughputEntry e = RunAndSnapshot(orePerPass: 10_000_000, passes: 2,
                profile: profile, productivityBonus: 4.0);

            // (1.30 + 4.0) x 3 = 15.9 per machine; 6 slots x 60 s x yield 0.5 = 2862 items/min.
            Assert.Equal(15.9, e.EffectiveMachineSpeed, 6);
            Assert.Equal(0.5, e.OutputScale, 6);
            Assert.Equal(6 * 15.9 * 60.0 * 0.5, e.NominalItemsPerMinute, 3);
        }
    }
}
