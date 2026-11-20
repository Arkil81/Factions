using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Sub-unit conservation. Node inventory is whole units while SE keeps fractional quantities, so
    /// the fraction of every charge and every yield is carried instead of truncated away. Without
    /// this a 0.7-ingot recipe loses up to a unit per production event.
    /// </summary>
    public class ProductionFractionalConservationTests
    {
        private const string OreKey = "Ore:Iron";
        private const string OutKey = "Ingot:Iron";

        private sealed class Harness
        {
            public WorldModel World;
            public ProductionManager Manager;
            public List<FactionState> States;
            public SpatialNode Node;
            public long StartOre;

            public void Advance(double seconds)
            {
                World.UtcNow = World.UtcNow.AddSeconds(seconds);
                Manager.Tick(States, World, World.Tuning, new List<string>());
            }

            private long Held(string key)
            {
                long onHand, reserved;
                Node.Inventory.TryGetValue(key, out onHand);
                Node.ReservedInventory.TryGetValue(key, out reserved);
                return onHand + reserved;
            }

            public long Produced(string key) { return Held(key); }
            public long OreConsumed() { return StartOre - Held(OreKey); }
        }

        private static Harness Build(ConversionRecipe recipe, long ore = 10_000_000,
            NodeCapabilityKind cap = NodeCapabilityKind.RefineryII, params string[] demandedKeys)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionIntervalSeconds = 30.0;
            world.Tuning.MaxRefineryUpgrades = 1;
            world.Tuning.MaxManufactoryUpgrades = 1;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[cap] = 1;
            node.Inventory[OreKey] = ore;

            foreach (string key in demandedKeys)
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:" + key, NodeId = "N1",
                    Quantity = 99_999_999, ExpiresUtc = world.UtcNow.AddDays(1),
                });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [cap] = new List<ConversionRecipe> { recipe },
            };
            var h = new Harness
            {
                World = world,
                Node = node,
                StartOre = ore,
                Manager = new ProductionManager(capRecipes),
                States = new List<FactionState> { new FactionState { Tag = "F" } },
            };
            h.Manager.Tick(h.States, world, world.Tuning, new List<string>());   // prime the slots
            return h;
        }

        private static ConversionRecipe OreToIngot(double outAmount, double inAmount = 1.0)
        {
            return new ConversionRecipe
            {
                BlueprintId = "IronIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = inAmount } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = outAmount } },
            };
        }

        [Fact]
        public void AFractionalYield_HoldsItsSeRatio_OverManyPasses()
        {
            // SE's IronOreToIngot is 0.7 ingot per ore. Truncating each event lost up to a unit every
            // time; carrying the fraction keeps the cumulative ratio exact to within one unit.
            Harness h = Build(OreToIngot(0.7), demandedKeys: OutKey);
            for (int pass = 0; pass < 40; pass++) h.Advance(30);

            long consumed = h.OreConsumed();
            Assert.True(consumed > 5_000, "expected a meaningful sample, got " + consumed);
            long exact = (long)Math.Floor(0.7 * consumed);
            Assert.InRange(h.Produced(OutKey), exact - 1, exact);
        }

        [Fact]
        public void ALowYieldRecipe_BanksItsFraction_UntilAWholeUnitExists()
        {
            // Platinum is 0.005 ingot per ore: every individual event rounds to zero. The old
            // zero-output guard answered that by refusing to run at all; carrying the fraction lets
            // the ore convert and the ingot appear once enough batches have run.
            Harness h = Build(OreToIngot(0.005), demandedKeys: OutKey);
            h.Advance(30);

            long consumed = h.OreConsumed();
            Assert.True(consumed > 0, "ore must actually be refined, not held hostage by a rounding guard");
            Assert.Equal((long)Math.Floor(0.005 * consumed), h.Produced(OutKey));
        }

        [Fact]
        public void ASmallSecondOutput_IsNotDroppedWhileItsInputIsCharged()
        {
            // Multi-output recipe: the large output rounds to a whole unit every event, so the batch
            // always ran -- and the small one, rounding to zero, was silently discarded while its
            // share of the input had already been paid for.
            var recipe = new ConversionRecipe
            {
                BlueprintId = "IronIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron",   Amount = 1 } },
                Outputs = new[]
                {
                    new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron",   Amount = 1.0 },
                    new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Silver", Amount = 0.02 },
                },
            };
            Harness h = Build(recipe, demandedKeys: new[] { OutKey, "Ingot:Silver" });
            for (int pass = 0; pass < 20; pass++) h.Advance(30);

            long batches = h.Produced(OutKey);                  // 1.0 per batch
            Assert.True(batches > 1_000, "expected a meaningful sample, got " + batches);
            long exactSilver = (long)Math.Floor(0.02 * batches);
            Assert.InRange(h.Produced("Ingot:Silver"), exactSilver - 1, exactSilver);
        }

        [Fact]
        public void AFractionalInput_IsChargedExactly_NotRoundedUpPerBatch()
        {
            // Consumption truncated each slot's own charge: 39 batches at 0.5 is 19.5 ore, paid as
            // 19. Carrying the half means the bank is billed for exactly what it consumed.
            Harness h = Build(OreToIngot(outAmount: 1.0, inAmount: 0.5), ore: 1_000, demandedKeys: OutKey);
            h.Advance(30);

            long produced = h.Produced(OutKey);     // 6 slots x 30 s x 1.30 = 234 batches
            Assert.Equal(234, produced);
            Assert.Equal(117, h.OreConsumed());     // 234 x 0.5, not 6 x floor(39 x 0.5) = 114
        }

        [Fact]
        public void CarryIsNotSharedBetweenNodes()
        {
            // The ledger is scoped per faction:node:recipe:item -- two nodes refining the same recipe
            // must each bank their own fraction, or one node's rounding would pay for the other's.
            Harness h = Build(OreToIngot(0.7), demandedKeys: OutKey);
            SpatialNode second = h.World.Graph.Add(new Vec3(100, 0, 0), 1.0, "N2");
            second.OwnerFactionTag = "F";
            second.HoldStrength = 1.0;
            second.StableStationId = 43;
            second.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            second.Inventory[OreKey] = 10_000_000;
            h.World.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + OutKey, NodeId = "N2",
                Quantity = 99_999_999, ExpiresUtc = h.World.UtcNow.AddDays(1),
            });

            for (int pass = 0; pass < 10; pass++) h.Advance(30);

            long consumed = 10_000_000 - second.Inventory[OreKey];
            long produced;
            second.Inventory.TryGetValue(OutKey, out produced);
            Assert.True(consumed > 0);
            Assert.InRange(produced, (long)Math.Floor(0.7 * consumed) - 1, (long)Math.Floor(0.7 * consumed));
        }
    }
}
