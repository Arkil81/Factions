using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// How much real time a machine slot is credited per pass. The credit window is derived from the
    /// production interval: a fixed window smaller than the interval scales every machine's real
    /// output by window/interval, invisibly to the throughput report.
    /// </summary>
    public class ProductionElapsedCreditTests
    {
        private const string OreKey = "Ore:Iron";
        private const string OutKey = "Ingot:Iron";

        private static ConversionRecipe Recipe()
        {
            return new ConversionRecipe
            {
                BlueprintId = "IronIngot",
                TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
        }

        private sealed class Harness
        {
            public WorldModel World;
            public ProductionManager Manager;
            public List<FactionState> States;
            public SpatialNode Node;

            public void Tick() { Manager.Tick(States, World, World.Tuning, new List<string>()); }

            /// <summary>Ingots on hand plus ingots reserved against an install BOM — reservation moves
            /// stock between the two without consuming it, so the sum is what production actually minted.</summary>
            public long ProducedSoFar()
            {
                long onHand, reserved;
                Node.Inventory.TryGetValue(OutKey, out onHand);
                Node.ReservedInventory.TryGetValue(OutKey, out reserved);
                return onHand + reserved;
            }
        }

        private static Harness Build(double creditCapIntervals = 2.0, double intervalSeconds = 30.0)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionIntervalSeconds = intervalSeconds;
            world.Tuning.ProductionCreditCapIntervals = creditCapIntervals;
            world.Tuning.MaxRefineryUpgrades = 1;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            node.Inventory[OreKey] = 10_000_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + OutKey, NodeId = "N1",
                Quantity = 99_999_999, ExpiresUtc = world.UtcNow.AddDays(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.RefineryII] = new List<ConversionRecipe> { Recipe() },
            };
            return new Harness
            {
                World = world,
                Node = node,
                Manager = new ProductionManager(capRecipes),
                States = new List<FactionState> { new FactionState { Tag = "F" } },
            };
        }

        // 6 slots x speed 1.30 / TimeSecs 1 = 7.8 batches per credited second.
        private const double BatchesPerSecond = 6 * 1.30;

        [Fact]
        public void AScheduledPass_CreditsItsWholeInterval()
        {
            Harness h = Build();
            h.Tick();                                   // prime: assigns slots, starts their clocks
            long primed = h.ProducedSoFar();

            h.World.UtcNow = h.World.UtcNow.AddSeconds(30);
            h.Tick();

            // 30 credited seconds, not the 5 a fixed window used to allow.
            Assert.Equal((long)(30 * BatchesPerSecond), h.ProducedSoFar() - primed);
        }

        [Fact]
        public void RepeatedPasses_CreditRealTime_WithNoSystematicLoss()
        {
            Harness h = Build();
            h.Tick();
            long primed = h.ProducedSoFar();

            for (int pass = 0; pass < 10; pass++)
            {
                h.World.UtcNow = h.World.UtcNow.AddSeconds(30);
                h.Tick();
            }

            // 300 s of wall clock must produce 300 s of output, not one-sixth of it.
            Assert.Equal((long)(300 * BatchesPerSecond), h.ProducedSoFar() - primed);
            Assert.Equal(0, h.Manager.LastTelemetry.ElapsedClamps);
        }

        [Fact]
        public void AGapBeyondTheWindow_ClampsAndReportsIt()
        {
            Harness h = Build();
            h.Tick();
            long primed = h.ProducedSoFar();

            h.World.UtcNow = h.World.UtcNow.AddHours(6);   // server was down
            h.Tick();

            // Bounded to 2 intervals, not six hours of free ore.
            Assert.Equal((long)(60 * BatchesPerSecond), h.ProducedSoFar() - primed);
            Assert.Equal(6, h.Manager.LastTelemetry.ElapsedClamps);
        }

        [Fact]
        public void TheFirstPass_BanksNothing_BecauseSlotClocksStartAtAssignment()
        {
            Harness h = Build();
            h.World.UtcNow = h.World.UtcNow.AddDays(3);   // manager constructed long "before" this pass
            h.Tick();

            Assert.Equal(0, h.ProducedSoFar());
            Assert.Equal(0, h.Manager.LastTelemetry.ElapsedClamps);
        }

        [Fact]
        public void TheWindowTracksTheConfiguredInterval()
        {
            Harness h = Build(intervalSeconds: 10.0);
            h.Tick();
            long primed = h.ProducedSoFar();

            h.World.UtcNow = h.World.UtcNow.AddSeconds(60);
            h.Tick();

            // 2 x 10 s interval = a 20 s window, so a 60 s gap clamps there.
            Assert.Equal((long)(20 * BatchesPerSecond), h.ProducedSoFar() - primed);
        }

    }
}
