using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Gas surplus demand is the only thing that asks a FuelDepot for its output, and it must not
    /// depend on the faction having an SE faction type. A player faction has none, so it never
    /// resolves offer keys; its depot still has to see a demand for the gas it makes, or it sits
    /// idle on a pile of ice. Both catalog paths are covered: the synchronous <c>Tick</c> and the
    /// resumable <c>TryAdvance</c> sweep, which checkpoints the same per-node phase separately.
    /// </summary>
    public class PlayerFactionGasDemandTests
    {
        private const string HydrogenKey = "Gas:Hydrogen";
        private const long Reference = 10_000_000;

        private static ConversionRecipe IceToHydrogen()
        {
            return new ConversionRecipe
            {
                BlueprintId = "Gas/OxygenGenerator/Hydrogen",
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = 25.0 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_GasProperties", SubTypeId = "Hydrogen", Amount = 500.0 } },
                TimeSecs = 1.0f,
            };
        }

        private static ConversionRecipe IceToOxygen()
        {
            return new ConversionRecipe
            {
                BlueprintId = "Gas/OxygenGenerator/Oxygen",
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = 25.0 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_GasProperties", SubTypeId = "Oxygen", Amount = 250.0 } },
                TimeSecs = 1.0f,
            };
        }

        /// <summary>Oxygen first, as the live scan resolves the generator's two gases.</summary>
        private static ProductionManager Manager(IFactionTypeCatalogue types)
        {
            return new ProductionManager(
                new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
                {
                    { NodeCapabilityKind.FuelDepot, new List<ConversionRecipe> { IceToOxygen(), IceToHydrogen() } },
                },
                types,
                new GasOracle());
        }

        /// <summary>One founded node owned by <paramref name="tag"/> with a depot, ice and no gas.</summary>
        private static WorldModel World(string tag, bool surplusIntent = true)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionSurplusIntentEnabled = surplusIntent;
            world.Tuning.ProductionSurplusTargetMultiple = 2.0;
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            node.Inventory["Ore:" + world.Tuning.IceOreSubTypeId] = 1_000_000;
            return world;
        }

        private static bool HasGasDemand(WorldModel world, string tag)
        {
            foreach (DemandEntry d in world.Demand.SnapshotItemDemands(tag))
                if (d.NodeId == "N1" && d.Kind == "Item:" + HydrogenKey && d.Quantity == 2 * Reference)
                    return true;
            return false;
        }

        [Fact]
        public void AFactionWithNoFactionType_StillAsksItsDepotForGas()
        {
            WorldModel world = World("EXP");
            ProductionManager manager = Manager(new OneTypeCatalogue("SPRT", "Miner", new[] { "Ore:Iron" }));
            var log = new List<string>();

            manager.Tick(new List<FactionState> { new FactionState { Tag = "EXP" } }, world, world.Tuning, log);

            Assert.True(HasGasDemand(world, "EXP"), "no Item:Gas:Hydrogen demand at N1 for a typeless faction");
            Assert.Contains(log, l => l.Contains("EXP GAS_SURPLUS_DEMAND node=N1 item=Gas:Hydrogen"));
        }

        [Fact]
        public void NoFactionTypeCatalogueAtAll_StillAsksForGas()
        {
            WorldModel world = World("EXP");
            var log = new List<string>();

            Manager(null).Tick(new List<FactionState> { new FactionState { Tag = "EXP" } }, world, world.Tuning, log);

            Assert.True(HasGasDemand(world, "EXP"));
        }

        [Fact]
        public void AFactionWithAType_KeepsItsGasDemand()
        {
            WorldModel world = World("SPRT");
            var log = new List<string>();

            Manager(new OneTypeCatalogue("SPRT", "Miner", new[] { "Ore:Iron" }))
                .Tick(new List<FactionState> { new FactionState { Tag = "SPRT" } }, world, world.Tuning, log);

            Assert.True(HasGasDemand(world, "SPRT"));
        }

        [Fact]
        public void SurplusIntentOff_PostsNoGasDemand()
        {
            WorldModel world = World("EXP", surplusIntent: false);
            var log = new List<string>();

            Manager(null).Tick(new List<FactionState> { new FactionState { Tag = "EXP" } }, world, world.Tuning, log);

            Assert.False(HasGasDemand(world, "EXP"));
            Assert.DoesNotContain(log, l => l.Contains("GAS_SURPLUS_DEMAND"));
        }

        [Fact]
        public void TheResumableSweep_AlsoAsksATypelessFactionsDepotForGas()
        {
            WorldModel world = World("EXP");
            ProductionManager manager = Manager(new OneTypeCatalogue("SPRT", "Miner", new[] { "Ore:Iron" }));
            var states = new List<FactionState> { new FactionState { Tag = "EXP" } };
            var log = new List<string>();

            int calls = 0;
            bool done = false;
            while (!done)
            {
                calls++;
                var budget = new FrameDeadline(System.Diagnostics.Stopwatch.GetTimestamp(), 0.0005);
                done = manager.TryAdvance(states, world, world.Tuning, budget, log);
                Assert.True(calls < 100_000, "sweep never completed");
            }

            Assert.True(HasGasDemand(world, "EXP"), "the resumable catalog phase skipped a typeless faction's gas");
        }

        /// <summary>The live shape: oxygen already above its shop limit (6.4M L, EXP). The on-hand
        /// refinement pass asks only for the first ice recipe's output, finds oxygen capped, and never
        /// reaches hydrogen — so the surplus demand is the only thing left that can start the depot.</summary>
        [Fact]
        public void ATypelessFactionsDepot_ProducesHydrogen_WithOxygenAtItsCap()
        {
            WorldModel world = World("EXP");
            world.Graph.Nodes["N1"].Inventory["Gas:Oxygen"] = 6_400_000;
            ProductionManager manager = Manager(new OneTypeCatalogue("SPRT", "Miner", new[] { "Ore:Iron" }));
            var states = new List<FactionState> { new FactionState { Tag = "EXP" } };
            var log = new List<string>();

            for (int i = 0; i < 4; i++)
            {
                manager.Tick(states, world, world.Tuning, log);
                world.UtcNow = world.UtcNow.AddSeconds(30);
            }

            Assert.True(world.GetNodeInventory("N1", HydrogenKey) > 0,
                "the depot made no hydrogen: " + string.Join(" | ", log));
        }

        /// <summary>A gas reference and a scarcity above 1 for both gases, which is what the live
        /// oracle answers for a node holding none; nothing else.</summary>
        private sealed class GasOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            {
                reference = kind == ItemKind.Hydrogen ? Reference : 0;
                return reference > 0;
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                reference = inventoryKey == HydrogenKey ? Reference : 0;
                return reference > 0;
            }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                return inventoryKey == HydrogenKey ? 2.0 : 1.0;
            }
        }

        private sealed class OneTypeCatalogue : IFactionTypeCatalogue
        {
            private static readonly IReadOnlyList<string> Empty = new List<string>().AsReadOnly();
            private readonly string _tag;
            private readonly string _type;
            private readonly IReadOnlyList<string> _offerKeys;

            public OneTypeCatalogue(string tag, string type, string[] offerKeys)
            {
                _tag = tag;
                _type = type;
                _offerKeys = new List<string>(offerKeys).AsReadOnly();
            }

            public bool TryGetFactionType(string factionTag, out string factionType)
            {
                if (factionTag == _tag) { factionType = _type; return true; }
                factionType = null;
                return false;
            }

            public IReadOnlyList<string> OfferInvKeys(string factionType) { return factionType == _type ? _offerKeys : Empty; }

            public IReadOnlyList<string> OrderInvKeys(string factionType) { return Empty; }
        }
    }
}
