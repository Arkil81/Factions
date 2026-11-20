using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The host-stamped <see cref="MachineProfile"/> reaching the bank loop: speed, output scaling
    /// and input efficiency must all take effect on real inventory, not just in reporting.
    /// </summary>
    public class ProductionMachineModelTests
    {
        private const string OreKey   = "Ore:Iron";
        private const string OutKey   = "Ingot:Iron";
        private const long   StartOre = 1_000_000;

        private static ConversionRecipe Recipe(double inAmount, double outAmount, bool ignoresEfficiency = false)
        {
            return new ConversionRecipe
            {
                BlueprintId = "IronIngot",
                TimeSecs = 0.5f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = inAmount } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = outAmount } },
                IgnoresEfficiencyMultiplier = ignoresEfficiency,
            };
        }

        /// <summary>Primes the bank with one tick (which assigns slots and starts their clocks),
        /// then advances 5 s and measures what the second tick actually moved. With TimeSecs 0.5 and
        /// 5 s credited, each slot runs 10 * speed batches.</summary>
        private static void RunOneTick(NodeCapabilityKind cap, ConversionRecipe recipe,
            MachineProfile profile, double productivityBonus, out long oreConsumed, out long produced)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionProductivityBonus = productivityBonus;
            // Pin the bank at its installed level: an upgrade request would reserve install stock out
            // of the same Inventory this test measures consumption against.
            world.Tuning.MaxRefineryUpgrades = 1;
            world.Tuning.MaxManufactoryUpgrades = 1;
            if (profile != null) world.Tuning.MachineProfiles[cap] = profile;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[cap] = 1;
            node.Inventory[OreKey] = StartOre;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + OutKey, NodeId = "N1",
                Quantity = 9_999_999, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [cap] = new List<ConversionRecipe> { recipe },
            };
            var mgr = new ProductionManager(capRecipes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            mgr.Tick(states, world, world.Tuning, new List<string>());

            world.UtcNow = world.UtcNow.AddSeconds(5);
            mgr.Tick(states, world, world.Tuning, new List<string>());

            // Read the recipe's own consumption/production record rather than raw inventory deltas:
            // install-stock reservation moves ore out of the same Inventory without consuming it.
            // The priming tick produced nothing, so these totals are the second tick's alone.
            oreConsumed = 0;
            produced    = 0;
            foreach (ProductionManager.ProductionHistoryEntry e in mgr.SnapshotProductionHistory("F"))
            {
                if (e.NodeId != "N1") continue;
                if (e.IsOutput && e.InvKey == OutKey) produced += e.Quantity;
                else if (!e.IsOutput && e.InvKey == OreKey) oreConsumed += e.Quantity;
            }
        }

        private static MachineProfile Profile(NodeCapabilityKind cap, double world = 1.0,
            double materialEfficiency = 1.0, double inputDivisor = 1.0)
        {
            MachineProfile p = MachinePhysics.DefaultProfile(cap);
            p.WorldSpeedMultiplier   = world;
            p.MaterialEfficiency     = materialEfficiency;
            p.InputEfficiencyDivisor = inputDivisor;
            return p;
        }

        [Fact]
        public void WorldSpeedMultiplier_ScalesRealOutput_Proportionally()
        {
            long ore1, out1, ore3, out3;
            RunOneTick(NodeCapabilityKind.RefineryII, Recipe(1, 1),
                Profile(NodeCapabilityKind.RefineryII, world: 1.0), 0.0, out ore1, out out1);
            RunOneTick(NodeCapabilityKind.RefineryII, Recipe(1, 1),
                Profile(NodeCapabilityKind.RefineryII, world: 3.0), 0.0, out ore3, out out3);

            // speed 1.30, TimeSecs 0.5, 5 s credited, 6 slots -> 13 batches/slot at 1x, 39 at 3x.
            Assert.Equal(6 * 13, out1);
            Assert.Equal(6 * 39, out3);
            Assert.Equal(3 * out1, out3);
            Assert.Equal(out1, ore1);   // 1:1 recipe
            Assert.Equal(out3, ore3);
        }

        [Fact]
        public void ProductivityBonus_AddsToBaseSpeed_BeforeTheWorldMultiplier()
        {
            long ore, produced;
            RunOneTick(NodeCapabilityKind.RefineryII, Recipe(1, 1),
                Profile(NodeCapabilityKind.RefineryII, world: 3.0), 4.0, out ore, out produced);

            // (1.30 + 4.0) * 3 = 15.9 -> 10 * 15.9 = 159 batches/slot.
            Assert.Equal(6 * 159, produced);
        }

        [Fact]
        public void MaterialEfficiency_ScalesOutput_NotInput()
        {
            long oreFull, outFull, oreHalf, outHalf;
            RunOneTick(NodeCapabilityKind.RefineryII, Recipe(1, 2),
                Profile(NodeCapabilityKind.RefineryII), 0.0, out oreFull, out outFull);
            RunOneTick(NodeCapabilityKind.RefineryII, Recipe(1, 2),
                Profile(NodeCapabilityKind.RefineryII, materialEfficiency: 0.5), 0.0, out oreHalf, out outHalf);

            Assert.Equal(6 * 26, outFull);          // 13 batches x 2 out
            Assert.Equal(6 * 13, outHalf);          // x 0.5 material efficiency
            Assert.Equal(oreFull, oreHalf);         // ore burn is untouched by yield
        }

        [Fact]
        public void AssemblerEfficiency_DividesInput_NotOutput()
        {
            long orePlain, outPlain, oreDivided, outDivided;
            RunOneTick(NodeCapabilityKind.ManufactoryII, Recipe(3, 1),
                Profile(NodeCapabilityKind.ManufactoryII), 0.0, out orePlain, out outPlain);
            RunOneTick(NodeCapabilityKind.ManufactoryII, Recipe(3, 1),
                Profile(NodeCapabilityKind.ManufactoryII, inputDivisor: 3.0), 0.0, out oreDivided, out outDivided);

            // speed 1.00, TimeSecs 0.5 -> 10 batches/slot either way; only the input cost changes.
            Assert.Equal(6 * 10, outPlain);
            Assert.Equal(outPlain, outDivided);
            Assert.Equal(6 * 30, orePlain);
            Assert.Equal(6 * 10, oreDivided);
        }

        [Fact]
        public void BlueprintThatIgnoresEfficiency_IsChargedInFull()
        {
            long oreNormal, outNormal, oreIgnoring, outIgnoring;
            RunOneTick(NodeCapabilityKind.ManufactoryII, Recipe(3, 1),
                Profile(NodeCapabilityKind.ManufactoryII, inputDivisor: 3.0), 0.0, out oreNormal, out outNormal);
            RunOneTick(NodeCapabilityKind.ManufactoryII, Recipe(3, 1, ignoresEfficiency: true),
                Profile(NodeCapabilityKind.ManufactoryII, inputDivisor: 3.0), 0.0, out oreIgnoring, out outIgnoring);

            Assert.Equal(outNormal, outIgnoring);
            Assert.Equal(6 * 10, oreNormal);
            Assert.Equal(6 * 30, oreIgnoring);   // the tools/weapons case: no efficiency discount
        }

        [Fact]
        public void UnstampedTuning_ProducesExactlyThePreProfileResult()
        {
            long oreDefault, outDefault, oreStamped, outStamped;
            RunOneTick(NodeCapabilityKind.RefineryII, Recipe(1, 1), null, 0.0, out oreDefault, out outDefault);
            RunOneTick(NodeCapabilityKind.RefineryII, Recipe(1, 1),
                Profile(NodeCapabilityKind.RefineryII), 0.0, out oreStamped, out outStamped);

            Assert.Equal(6 * 13, outDefault);
            Assert.Equal(outStamped, outDefault);
            Assert.Equal(oreStamped, oreDefault);
        }

        [Fact]
        public void StampedMachineCount_DrivesSlotCount()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            MachineProfile p = MachinePhysics.DefaultProfile(NodeCapabilityKind.RefineryII);
            p.MachineCount = 4;
            world.Tuning.MachineProfiles[NodeCapabilityKind.RefineryII] = p;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 2;   // level 2 -> 2 x MachineCount
            node.Inventory[OreKey] = StartOre;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + OutKey, NodeId = "N1",
                Quantity = 9_999_999, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.RefineryII] = new List<ConversionRecipe> { Recipe(1, 1) },
            };
            var mgr = new ProductionManager(capRecipes);
            mgr.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            int slots = 0;
            foreach (ThroughputEntry t in mgr.SnapshotThroughput("F"))
                if (t.PrimaryOutputId == OutKey) slots += t.SlotCount;
            Assert.Equal(8, slots);
        }
    }
}
