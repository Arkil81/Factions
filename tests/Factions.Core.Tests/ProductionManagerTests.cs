using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ProductionManagerTests
    {
        private sealed class FakeVolumeCatalogue : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _litersByKey = new Dictionary<string, double>();
            public FakeVolumeCatalogue Set(string invKey, double litersPerUnit) { _litersByKey[invKey] = litersPerUnit; return this; }
            public double VolumeLitersPerUnit(string invKey)
            {
                double v;
                return _litersByKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        [Fact]
        public void RecipeInputConsumption_CreditsDemandHistory_ItemVolumeWeighted()
        {
            // DemandHistory's gravity term is fed by actual consumption, not delivery (see
            // docs/material-vectors.md's dated correction note) -- this pins the real feed point:
            // consuming a recipe input must stage a pending credit sized to what was actually
            // consumed, item-volume-weighted the same way OperationsManager/FleetResolution do it.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 1000;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:SteelPlate",
                NodeId = "N1",
                Quantity = 300,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 },
                        },
                    },
                },
            };

            var volumes = new FakeVolumeCatalogue().Set("Ingot:Iron", 5.0);
            var manager = new ProductionManager(capRecipes, itemVolumes: volumes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);

            for (int second = 1; second <= 10; second++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(1);
                manager.Tick(states, world, world.Tuning, log);
            }

            long ironAfter;
            node.Inventory.TryGetValue("Ingot:Iron", out ironAfter);
            long consumed = 1000 - ironAfter;
            Assert.True(consumed > 0, "the recipe should have consumed some Ingot:Iron over 10 seconds");

            Dictionary<string, double> credited = world.DrainPendingDemandHistory("N1");
            Assert.NotNull(credited);
            Assert.Equal(consumed * 5.0, credited["Ingot:Iron"], 6);
        }

        [Fact]
        public void MissingManufacturing_PostsIngotInstallDemand_WhenRefineryExists()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:SteelPlate",
                NodeId = "N1",
                Quantity = 300,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "IronIngot",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Iron", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                        },
                    },
                },
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 },
                        },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var state = new FactionState { Tag = "F" };
            var log = new List<string>();

            manager.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.Manufactory);
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ingot:Iron" && d.Quantity >= world.Tuning.InstallCostFor(NodeCapabilityKind.Manufactory, ItemKind.Ingot));
        }

        [Fact]
        public void ShipyardInvestment_PostsProcessedGoodInstallDemand_WhenManufactoryExists()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F",
                Cap = NodeCapabilityKind.Shipyard,
                Urgency = 0.8,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 },
                        },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var state = new FactionState { Tag = "F" };
            var log = new List<string>();

            manager.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= world.Tuning.InstallCostFor(NodeCapabilityKind.Shipyard, ItemKind.ProcessedGood));
        }

        [Fact]
        public void BootstrapManufactory_RetainsOutputAfterOneMinuteOfUpkeep()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 1000;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:SteelPlate",
                NodeId = "N1",
                Quantity = 300,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 },
                        },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);

            for (int second = 1; second <= 60; second++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(1);
                manager.Tick(states, world, world.Tuning, log);
                if (second % 10 == 0) world.AccrueIncome(states);
            }

            Assert.True(world.GetNodeInventory("N1", ItemKind.ProcessedGood) > 0,
                "healthy bootstrap production should accumulate components after station and capability upkeep");
        }

        [Fact]
        public void MultiInputRecipe_DoesNotOccupySlots_WithoutAllIngredients()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 100;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:Motor",
                NodeId = "N1",
                Quantity = 50,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Motor",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Nickel", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Motor", Amount = 1 },
                        },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var state = new FactionState { Tag = "F" };
            var log = new List<string>();

            manager.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Empty(manager.SnapshotThroughput("F"));
            Assert.Contains(world.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");
            Assert.Contains(world.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Ingot:Nickel" && d.NodeId == "N1");
        }

        [Fact]
        public void SlotSwitchesAway_FromMultiInputRecipe_WhenAnotherIngredientRunsOut()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 100;
            node.Inventory["Ingot:Nickel"] = 20;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:Motor",
                NodeId = "N1",
                Quantity = 50,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Motor",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Nickel", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Motor", Amount = 1 },
                        },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 },
                        },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var state = new FactionState { Tag = "F" };
            var log = new List<string>();

            manager.Tick(new List<FactionState> { state }, world, world.Tuning, log);
            Assert.Contains(manager.SnapshotThroughput("F"), t => t.PrimaryOutputId == "Comp:Motor");

            node.Inventory["Ingot:Nickel"] = 0;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:SteelPlate",
                NodeId = "N1",
                Quantity = 50,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            world.UtcNow = world.UtcNow.AddSeconds(5);

            manager.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.DoesNotContain(manager.SnapshotThroughput("F"), t => t.PrimaryOutputId == "Comp:Motor");
            Assert.Contains(manager.SnapshotThroughput("F"), t => t.PrimaryOutputId == "Comp:SteelPlate");
            Assert.True(node.Inventory.ContainsKey("Comp:SteelPlate") && node.Inventory["Comp:SteelPlate"] > 0);
        }

        // ---- input-capped slot allocation --------------------------------------------------

        // Builds a Refinery node with the three real common-metal recipes (Iron/Nickel/Cobalt),
        // owned by faction F, with the given ore stocks and an output demand for each ingot.
        private static (WorldModel world, ProductionManager mgr, SpatialNode node, List<FactionState> states, List<string> log)
            RefineryWorld(long ironOre, long nickelOre, long cobaltOre, IPriceOracle priceOracle = null,
                long ingotDemand = 5000, int machines = 6, bool ironRanksLast = false)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            if (ironOre   > 0) node.Inventory["Ore:Iron"]   = ironOre;
            if (nickelOre > 0) node.Inventory["Ore:Nickel"] = nickelOre;
            if (cobaltOre > 0) node.Inventory["Ore:Cobalt"] = cobaltOre;

            foreach (string ingot in new[] { "Ingot:Iron", "Ingot:Nickel", "Ingot:Cobalt" })
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:" + ingot, NodeId = "N1",
                    Quantity = ingotDemand, ExpiresUtc = world.UtcNow.AddMinutes(5),
                });

            ConversionRecipe Ore2Ingot(string sub, float timeSecs) => new ConversionRecipe
            {
                BlueprintId = sub + "OreToIngot",
                TimeSecs = timeSecs,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = sub, Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = sub, Amount = 1 } },
            };

            // With no oracle every demanded recipe scores a flat 2.0, and the insertion sort's
            // strict `>` keeps ties in list order -- so list order IS rank order here, and putting
            // iron last is how a test makes iron the recipe that loses a contested machine.
            var ordered = ironRanksLast
                ? new List<ConversionRecipe> { Ore2Ingot("Nickel", 0.66f), Ore2Ingot("Cobalt", 3.0f), Ore2Ingot("Iron", 0.05f) }
                : new List<ConversionRecipe> { Ore2Ingot("Iron", 0.05f), Ore2Ingot("Nickel", 0.66f), Ore2Ingot("Cobalt", 3.0f) };
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = ordered,
            };

            if (machines != 6)
                world.Tuning.MachineProfiles[NodeCapabilityKind.Refinery] =
                    new MachineProfile { BaseSpeed = 0.65, MachineCount = machines };

            var mgr = new ProductionManager(capRecipes, null, priceOracle);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            return (world, mgr, node, states, new List<string>());
        }

        // ---- FAC-164: ranked-but-slotless, and output-sized input demand ---------------------

        // Reaching "ranked but slotless": more ranked recipes than machines.
        //
        // These tests used to reach it by setting an absurd input horizon so that every recipe's
        // slot cap floored to zero. That worked only because ComputeSlotCounts' one-machine rescue
        // was restricted to a sole surviving recipe -- the restriction FAC-231 removed, because it
        // was zeroing whole banks live (FCTM N036: 18 machines, 2 ranked, 0 awarded). With the
        // floor now unconditional, a runnable recipe always earns a machine if one is free, so the
        // horizon can no longer produce this state and the tests would have been silently testing
        // the slotted path instead.
        //
        // Machine scarcity is the honest way in, and it is the way the state actually arises in
        // production: rank more recipes than the bank has machines and the round-robin leaves the
        // lowest-ranked with none. The assertions below are unchanged.
        private const int SlotlessMachineCount = 1;

        [Fact]
        public void TwoRankedRecipesWithRealInput_BothGetAMachine_RatherThanTheBankSittingIdle()
        {
            // FAC-231 finding 1. The slot cap is "batches on hand / batches needed to fill the
            // horizon on one machine", and that threshold scales with the recipe's own SPEED: at
            // RefineryII's effective 15.9, IronOreToIngot (TimeSecs 0.05) needs 3,180 units of ore
            // to earn one machine for ten seconds, while CobaltOreToIngot (3.0) needs 53. The
            // fastest recipes demand the most stock -- exactly inverted.
            //
            // The one-machine floor that rescues this was restricted to a SOLE surviving recipe, so
            // with two or more ranked and neither holding a full horizon's worth, every one floored
            // to zero and the whole bank sat idle. Live 2026-09-02: FCTM N036 cap=RefineryII
            // machines=18 awarded=0 ranked=2, and across the window 125 such banks held 798 machines
            // with 767 idle.
            //
            // 50 ore against a 130-batch horizon for iron and a ~10-batch one for nickel: both are
            // runnable, neither fills a horizon, and six machines are free.
            var (world, mgr, node, states, log) = RefineryWorld(ironOre: 50, nickelOre: 50, cobaltOre: 0);

            mgr.Tick(states, world, world.Tuning, log);

            List<ThroughputEntry> t = mgr.SnapshotThroughput("F");
            Assert.True(SlotsFor(t, "Ingot:Iron")   >= 1, "iron ranked and runnable but got no machine");
            Assert.True(SlotsFor(t, "Ingot:Nickel") >= 1, "nickel ranked and runnable but got no machine");
        }

        [Fact]
        public void RankedButSlotlessRecipe_StillPostsItsInputDemand()
        {
            // FAC-164 primary: a refinery holding a little iron ore under a large ingot demand
            // ranked the recipe but earned no slot, and neither posting path covered that state --
            // so it asked for no ore at all and stayed permanently below its own start threshold.
            var (world, mgr, node, states, log) = RefineryWorld(ironOre: 50, nickelOre: 50, cobaltOre: 0,
                machines: SlotlessMachineCount, ironRanksLast: true);

            mgr.Tick(states, world, world.Tuning, log);

            // Precondition: iron really is ranked-but-slotless, not quietly running.
            Assert.Equal(0, SlotsFor(mgr.SnapshotThroughput("F"), "Ingot:Iron"));

            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ore:Iron" && d.NodeId == "N1" && d.Quantity > 0);
        }

        [Fact]
        public void BlockedRecipeInputDemand_IsSizedByOutstandingOutputDemand()
        {
            // FAC-164 secondary: the blocked path sized purely from machine geometry -- a full
            // bank's throughput across the demand TTL -- and never consulted the output want that
            // admitted the recipe. Live that read as 3,013,200 Ore:Stone for 513 gravel.
            var (world, mgr, node, states, log) =
                RefineryWorld(ironOre: 0, nickelOre: 0, cobaltOre: 0, ingotDemand: 10);

            mgr.Tick(states, world, world.Tuning, log);

            DemandEntry ore = Assert.Single(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ore:Cobalt");
            // One cobalt ore per ingot: 10 ingots wanted can consume at most ~10 ore. The
            // uncapped geometry asks for a whole 6-machine bank's TTL of throughput instead.
            Assert.InRange(ore.Quantity, 1, 20);
        }

        [Fact]
        public void SlotlessInputDemand_IsAlsoSizedByOutstandingOutputDemand()
        {
            // Both halves of FAC-164 at once: the newly-covered slotless state must not reintroduce
            // the unbounded sizing the blocked path just lost.
            var (world, mgr, node, states, log) =
                RefineryWorld(ironOre: 50, nickelOre: 50, cobaltOre: 0, ingotDemand: 200,
                    machines: SlotlessMachineCount, ironRanksLast: true);

            mgr.Tick(states, world, world.Tuning, log);

            DemandEntry ore = Assert.Single(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ore:Iron");
            // 200 ingots wanted, one ore per ingot -> the output cap binds at ~200.
            Assert.InRange(ore.Quantity, 1, 200);
        }

        [Fact]
        public void InputDemandLog_ReportsARefreshThatMovesTheSize_NotOnlyTheFirstPost()
        {
            // FAC-165: the register was observed holding 563,000 Ore:Stone while the newest
            // PROD_INPUT_DEMAND line for that key said 53,000, because the emitter fired only on
            // `isNew`. That made "which poster wrote this quantity" unanswerable from the log.
            var (world, mgr, node, states, log) = RefineryWorld(ironOre: 1462, nickelOre: 0, cobaltOre: 3);

            mgr.Tick(states, world, world.Tuning, log);
            int afterFirst = log.FindAll(l => l.Contains("PROD_INPUT_DEMAND Ore:Iron")).Count;
            Assert.Equal(1, afterFirst);

            // Same row, re-posted at a very different size: the ingot want collapses, so FAC-164's
            // cap binds hard and the ore need drops by far more than 2x.
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 10, ExpiresUtc = world.UtcNow.AddMinutes(10),
            });
            world.UtcNow = world.UtcNow.AddSeconds(5);
            mgr.Tick(states, world, world.Tuning, log);

            List<string> lines = log.FindAll(l => l.Contains("PROD_INPUT_DEMAND Ore:Iron"));
            Assert.True(lines.Count > afterFirst,
                "a refresh that moves the size must be logged; saw only " + lines.Count + " line(s)");
            Assert.Contains(lines, l => l.Contains("prev="));
        }

        [Fact]
        public void MultiOutputRecipe_IsSizedByItsLeastHungryDemandedOutput_NotAByProduct()
        {
            // Measured on the live box after v0.80.0 shipped the MAX: ISBT asked 2,918,000 Ore:Stone.
            // The real SE StoneOreToIngot is 1000 ore -> 14 gravel + 30 iron + 2.4 nickel + 4 silicon,
            // and it was the 2.4-unit nickel BY-PRODUCT against a 5,071 Ingot:Nickel demand that set
            // that size -- not the 513 gravel that stone refining is the right source for. Nickel has
            // its own ore. One batch yields every output, so the least-hungry want sets the input need.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            // No stone on hand -> the recipe is blocked, which is the path that posted the 2,918,000.

            void Want(string key, long qty) => world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + key, NodeId = "N1",
                Quantity = qty, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            Want("Ingot:Stone", 513);
            Want("Ingot:Nickel", 5071);
            Want("Ingot:Iron", 11748);

            var stone = new ConversionRecipe
            {
                BlueprintId = "StoneOreToIngot",
                TimeSecs = 10f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Stone", Amount = 1000 } },
                Outputs = new[]
                {
                    new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Stone",  Amount = 14f },
                    new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron",   Amount = 30f },
                    new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Nickel", Amount = 2.4f },
                },
            };
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe> { stone },
            };
            var mgr = new ProductionManager(capRecipes, null, null);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            mgr.Tick(states, world, world.Tuning, new List<string>());

            DemandEntry ore = Assert.Single(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ore:Stone");
            // ceil(513 / 14) = 37 batches x 1000 ore. Nickel would demand ~2113 batches, iron ~392.
            Assert.InRange(ore.Quantity, 1, 40000);
        }

        [Fact]
        public void TrickleOreRecipe_IsCappedToOneSlot_WhileRichRecipeTakesTheRest()
        {
            // The reported bug: 3 cobalt ore must not hold the whole 6-machine bank while
            // 1462 iron ore sits idle. Cobalt is capped to 1 slot; iron takes the remainder.
            var (world, mgr, node, states, log) = RefineryWorld(ironOre: 1462, nickelOre: 0, cobaltOre: 3);

            mgr.Tick(states, world, world.Tuning, log);

            var tp = mgr.SnapshotThroughput("F");
            int ironSlots   = SlotsFor(tp, "Ingot:Iron");
            int cobaltSlots = SlotsFor(tp, "Ingot:Cobalt");

            Assert.True(cobaltSlots <= 1, "cobalt (3 ore) should hold at most 1 slot, held " + cobaltSlots);
            Assert.True(ironSlots >= 5, "iron (1462 ore) should take the rest of the 6-machine bank, held " + ironSlots);
        }

        [Fact]
        public void RichRecipe_RepostsInputDemand_EvenAfterItsDemandWasConsumed()
        {
            // The "demand lost next tick" symptom: iron must keep posting Ore:Iron every tick.
            // Previously hysteresis left iron ranked-but-slotless, so its ore demand was never
            // re-posted after a delivery consumed it.
            var (world, mgr, node, states, log) = RefineryWorld(ironOre: 1462, nickelOre: 0, cobaltOre: 3);

            mgr.Tick(states, world, world.Tuning, log);
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ore:Iron" && d.NodeId == "N1");

            // Simulate an OperationsManager delivery consuming the demand entry.
            world.Demand.Consume("F", "Item:Ore:Iron", "N1");
            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Ore:Iron");

            world.UtcNow = world.UtcNow.AddSeconds(5);
            mgr.Tick(states, world, world.Tuning, log);

            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ore:Iron" && d.NodeId == "N1");
        }

        [Fact]
        public void TwoWellSuppliedRecipes_ShareTheBankFairly()
        {
            // "Fair share + caps": two input-rich recipes split the bank rather than one
            // grabbing everything. Iron and nickel each have plenty of ore → ~3 slots each.
            var scarcity = new FixedScarcityOracle(new Dictionary<string, double>
            {
                { "Ingot:Iron", 3.0 },
                { "Ingot:Nickel", 3.0 },
            });
            var (world, mgr, node, states, log) = RefineryWorld(
                ironOre: 1462, nickelOre: 1161, cobaltOre: 0, priceOracle: scarcity);

            mgr.Tick(states, world, world.Tuning, log);

            var tp = mgr.SnapshotThroughput("F");
            int ironSlots   = SlotsFor(tp, "Ingot:Iron");
            int nickelSlots = SlotsFor(tp, "Ingot:Nickel");

            Assert.Equal(3, ironSlots);
            Assert.Equal(3, nickelSlots);
        }

        [Fact]
        public void SlotProgress_IsPreserved_WhenStockShortfallBlocksABatch_NotDiscarded()
        {
            // Regression: when multiple slots on the same recipe simultaneously cross a full
            // batch of real elapsed machine-time but shared stock can only cover one of them,
            // the slot(s) that lose the race must not have their accumulated progress silently
            // discarded — they should fire on the very next tick once stock returns, not have to
            // reaccumulate the full TimeSecs from scratch.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            // Exactly one batch's worth of Iron on hand — enough for only one of the two slots
            // the horizon-cap formula grants at this (deliberately slow, TimeSecs=20) recipe.
            node.Inventory["Ingot:Iron"] = 3;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Widget", NodeId = "N1",
                Quantity = 100, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Widget", TimeSecs = 20f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 3 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Widget", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var states  = new List<FactionState> { new FactionState { Tag = "F" } };
            var log     = new List<string>();

            // Tick 1: cold start, both slots assigned, Accum=0.
            manager.Tick(states, world, world.Tuning, log);
            // 4 more ticks of 5s each (MaxElapsedSeconds cap) bring Accum from 0 to exactly 1.0
            // in both slots simultaneously (4 * 5s * 1.0 speed / 20s TimeSecs = 1.0).
            for (int i = 0; i < 4; i++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(5);
                manager.Tick(states, world, world.Tuning, log);
            }

            // Exactly one batch fired (consuming all 3 Iron); the other slot's batch of progress
            // was blocked by the now-empty stock.
            long producedSoFar;
            node.Inventory.TryGetValue("Comp:Widget", out producedSoFar);
            Assert.Equal(1L, producedSoFar);

            // Restock generously and tick again with a negligible elapsed time.
            node.Inventory["Ingot:Iron"] = 1000;
            world.UtcNow = world.UtcNow.AddMilliseconds(1);
            manager.Tick(states, world, world.Tuning, log);

            // The second slot's progress must have survived the shortfall tick — it should fire
            // essentially immediately once stock is available, not need another ~20s to catch up.
            long producedAfterRestock;
            node.Inventory.TryGetValue("Comp:Widget", out producedAfterRestock);
            Assert.True(producedAfterRestock >= 2,
                "expected the blocked slot's preserved progress to produce on restock, got " + producedAfterRestock);
        }

        [Fact]
        public void ActiveShipBuild_ProducesViaReservationPass_WithoutFullySuppressingBackgroundWeaponSlots()
        {
            // Supersedes the old bank-monopolization behavior (a Manufactory bank that could run one
            // missing build component used to evict every other ranked recipe from the whole bank,
            // e.g. SemiAutoPistolItem dropping to 0 slots). ServiceShipBuildReservations now gives the
            // ship-build recipe (Thrust) a real guaranteed first pass against its own isolated bucket
            // regardless of what the normal bank loop does afterward -- so the normal loop no longer
            // needs to evict rivals on the ship build's behalf, and background weapon production is no
            // longer fully suppressed just because an unrelated build is outstanding.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 10000;

            var build = new BuildQueueEntry
            {
                Id = "Trade:001",
                Role = "Trade",
                ExpiresUtc = world.UtcNow.AddHours(1),
            };
            build.Bom["Comp:Thrust"] = 100;
            node.BuildQueue.Add(build);

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:SemiAutoPistolItem",
                NodeId = "N1",
                Quantity = 100,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            // A durable BOM-minus-Collected shortage is concrete operational scarcity even when
            // PriceOracle has no reference-stock opinion on component outputs yet.
            var scarcity = new FixedScarcityOracle(new Dictionary<string, double>
            {
                { "Comp:Thrust", 1.0 },
                { "SemiAutoPistolItem", 3.0 },
            });
            var manager = new ProductionManager(ShipPriorityManufactoryRecipes(), null, scarcity);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            for (int i = 0; i < 5; i++)
            {
                manager.Tick(states, world, world.Tuning, new List<string>());
                world.UtcNow = world.UtcNow.AddSeconds(5);
            }

            long thrustCollected;
            build.Collected.TryGetValue("Comp:Thrust", out thrustCollected);
            Assert.True(thrustCollected > 0,
                "expected the reservation pass to produce real Comp:Thrust into the build slot");
            Assert.True(SlotsFor(manager.SnapshotThroughput("F"), "SemiAutoPistolItem") > 0,
                "background weapon production should no longer be fully suppressed by an outstanding build");
        }

        /// <summary>A yard at N1 short of 100 Thrust, and a ManufactoryII at N2 that is not the yard.
        /// N2 sees the BOM only through the oracle's wanted floor -- the yard's <c>Item:</c> row
        /// counts faction-wide -- so it makes Thrust while it holds less than that floor.</summary>
        private static (WorldModel world, SpatialNode yard, SpatialNode bank) RemoteBankWorld(long thrustAtBank)
        {
            var world = TestSupport.World();
            world.Registry = NullItemRegistry.Instance;
            SpatialNode yard = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            yard.OwnerFactionTag = "F";
            yard.HoldStrength = 1.0;
            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:Thrust"] = 100;
            yard.BuildQueue.Add(build);

            SpatialNode bank = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "N2");
            bank.OwnerFactionTag = "F";
            bank.HoldStrength = 1.0;
            bank.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            bank.Inventory["Ingot:Iron"] = 10000;
            if (thrustAtBank > 0) bank.Inventory["Comp:Thrust"] = thrustAtBank;

            // Away from the yard it is the Item: row that raises the wanted floor.
            PostYardNeeds(world, yard, "F");
            return (world, yard, bank);
        }

        [Fact]
        public void ARemoteBank_MakesAShipBomComponent_WhileItHoldsLessThanTheFloor()
        {
            var (world, _, bank) = RemoteBankWorld(thrustAtBank: 0);
            var oracle = new PriceOracle(world, world.Tuning);
            Assert.True(oracle.ScarcityMultiplier(bank.Id, "Comp:Thrust") > 1.0);

            var manager = new ProductionManager(ShipPriorityManufactoryRecipes(), null, oracle);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.True(SlotsFor(manager.SnapshotThroughput("F"), "Comp:Thrust") > 0);
        }

        /// <summary>The discriminator for retiring the neutral-scarcity exemption. A bank that already
        /// holds the floor of a BOM component is a measured glut until a haul drains it; the exemption
        /// used to run the recipe anyway, for as long as the BOM was short anywhere in the faction.</summary>
        [Fact]
        public void ARemoteBank_HoldingTheFloorOfAShipBomComponent_MakesNoMore()
        {
            long floor = TestSupport.World().Tuning.ReferenceStockFor(ItemKind.ProcessedGood);
            Assert.True(floor > 0);
            var (world, _, bank) = RemoteBankWorld(thrustAtBank: floor);
            var oracle = new PriceOracle(world, world.Tuning);
            Assert.True(oracle.ScarcityMultiplier(bank.Id, "Comp:Thrust") <= 1.0);

            var manager = new ProductionManager(ShipPriorityManufactoryRecipes(), null, oracle);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Equal(0, SlotsFor(manager.SnapshotThroughput("F"), "Comp:Thrust"));
            long held;
            bank.Inventory.TryGetValue("Comp:Thrust", out held);
            Assert.Equal(floor, held);
        }

        [Fact]
        public void ACollectedBom_LeavesItsComponentIdle_AndTheBankToBackgroundWork()
        {
            var world = TestSupport.World();
            world.Registry = NullItemRegistry.Instance;
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 10000;
            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:Thrust"] = 100;
            build.Collected["Comp:Thrust"] = 100;
            node.BuildQueue.Add(build);
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:SemiAutoPistolItem", NodeId = "N1",
                Quantity = 100, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            IPriceOracle oracle = YardOracle(world, node, new Dictionary<string, double> { { "SemiAutoPistolItem", 3.0 } });
            var manager = new ProductionManager(ShipPriorityManufactoryRecipes(), null, oracle);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            List<ThroughputEntry> tp = manager.SnapshotThroughput("F");
            Assert.Equal(0, SlotsFor(tp, "Comp:Thrust"));
            Assert.True(SlotsFor(tp, "SemiAutoPistolItem") > 0);
        }

        [Fact]
        public void UnrelatedShipBuildOutput_DoesNotSuppressBackgroundSlots_FromAnotherScarceRecipeOutput()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 10000;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:GravityGenerator"] = 100;
            node.BuildQueue.Add(build);
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1", Quantity = 100, ExpiresUtc = world.UtcNow.AddMinutes(5) });
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:SemiAutoPistolItem", NodeId = "N1", Quantity = 100, ExpiresUtc = world.UtcNow.AddMinutes(5) });

            var scarcity = new FixedScarcityOracle(new Dictionary<string, double>
            {
                { "Comp:Thrust", 1.0 },
                { "Comp:SteelPlate", 3.0 },
                { "SemiAutoPistolItem", 3.0 },
            });
            var manager = new ProductionManager(ShipPriorityMultiOutputManufactoryRecipes(), null, scarcity);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.True(SlotsFor(manager.SnapshotThroughput("F"), "SemiAutoPistolItem") > 0);
        }

        [Fact]
        public void ScarceSharedInput_ItemDemandReflectsBlockedRecipesLargerNeed_NotRankedRecipesSmallerNeed()
        {
            // Regression for the Fort Calico starvation bug: a cheap, always-affordable recipe
            // (ammo) must not clobber a scarce/expensive ship-build recipe's (Thrust) larger
            // Item: demand for the same shared input when both post in the same tick —
            // DemandRegister.Post is last-write-wins per key, so whichever recipe posted second
            // used to win regardless of actual need.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            // Enough Iron for the cheap ammo recipe to actually hold a slot (1/batch, needs >= 10
            // for the 10s input horizon to grant it one) but nowhere near enough for one full
            // Thrust batch (50/batch) — Thrust is "blocked", the ammo recipe is "ranked" and slotted.
            node.Inventory["Ingot:Iron"] = 15;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:Thrust"] = 100;
            node.BuildQueue.Add(build);
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:SemiAutoPistolItem", NodeId = "N1", Quantity = 100, ExpiresUtc = world.UtcNow.AddMinutes(5) });

            IPriceOracle scarcity = YardOracle(world, node, new Dictionary<string, double>
            {
                { "SemiAutoPistolItem", 3.0 },
            });
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Thrust", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 50 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Thrust", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "SemiAutoPistol", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_PhysicalGunObject", SubTypeId = "SemiAutoPistolItem", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes, null, scarcity);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            DemandEntry ironDemand = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            // The size is now the chain requirement, not the bank's appetite (FAC-205): 100
            // Comp:Thrust x 50 Ingot:Iron for the build, plus 100 x 1 for the ammo the catalogue
            // wants. Gross -- the 15 on the shelf is netted downstream, once, by MatchVectors.
            Assert.Equal(100 * 50 + 100 * 1, ironDemand.Quantity);

            // The yard's own Item:Comp:Thrust row is in the register, as it always is live. It caps
            // Thrust's input need at the BOM (5,000); before FAC-468 the row was capped by that one
            // recipe's need and the ammo's 100 was dropped, though the chain carried it.

            // ...and the property this test exists for is untouched: the cheap ammo recipe's own
            // want for the same input is an order of magnitude smaller, and does not clobber it.
            // Before FAC-205 the assertion here was the appetite -- 6 machines x 1.0 speed / 1s
            // TimeSecs x 90s TTL x 50/batch = 27,000 Ingot:Iron for a build that needs 5,000 -- which
            // is the oversizing that made the row unretireable, not the discrimination being tested.
            long ammoAppetite = (long)Math.Ceiling(6 * 1.0 / 1.0 * 90.0 * 1);
            Assert.True(ironDemand.Quantity > ammoAppetite * 5,
                "expected Iron demand to reflect Thrust's need, not the ammo recipe's " + ammoAppetite
                + "; got " + ironDemand.Quantity);
        }

        [Fact]
        public void ScarceSharedInput_OnceEnoughStockArrives_BlockedShipBuildRecipeUnblocksAndProduces()
        {
            // Follow-up to the test above: proves the fix actually unsticks the build, not just
            // the demand number. Once a delivery (simulating OperationsManager routing the larger,
            // correctly-sized demand) brings enough Iron for one Thrust batch, Thrust must become
            // ranked and produce — exactly the sequence that was stuck live on SPRT Fort Calico.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 15;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:Thrust"] = 100;
            node.BuildQueue.Add(build);
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:SemiAutoPistolItem", NodeId = "N1", Quantity = 100, ExpiresUtc = world.UtcNow.AddMinutes(5) });

            IPriceOracle scarcity = YardOracle(world, node, new Dictionary<string, double>
            {
                { "SemiAutoPistolItem", 3.0 },
            });
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Thrust", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 50 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Thrust", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "SemiAutoPistol", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_PhysicalGunObject", SubTypeId = "SemiAutoPistolItem", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes, null, scarcity);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            // Simulate an OperationsManager delivery large enough to cover one Thrust batch.
            world.AddNodeInventory("N1", "Ingot:Iron", 10000);
            world.UtcNow = world.UtcNow.AddSeconds(5);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            long thrustStock;
            node.Inventory.TryGetValue("Comp:Thrust", out thrustStock);
            Assert.True(thrustStock > 0, "expected Thrust to unblock and produce once enough Iron arrived");
        }

        [Fact]
        public void SoleRankedRecipe_StillProduces_WhenOnHandBatchesFallShortOfInputHorizon()
        {
            // A recipe can be the only one ranked in a bank (n == 1, see ComputeSlotCounts's doc
            // comment) and still get assigned zero machines by ComputeSlotCounts's
            // SlotInputHorizonSeconds cap -- that cap is computed from batches-on-hand vs.
            // batches-needed-to-fill-10-seconds-of-one-machine, and an expensive recipe (many input
            // units per batch) can have real, multi-batch material in hand yet still round down to
            // zero under that formula. With no rival ranked in the bank, zero machines for the sole
            // survivor means the entire bank sits idle despite holding usable material.
            //
            // Live-observed on IGSL: a SteelPlate recipe (21 Iron/batch) held 90+ Iron (4+ full
            // batches) yet produced nothing indefinitely, because 90/21 batches fell short of the
            // Manufactory horizon threshold (0.65 batches/sec/machine x 10s ~= 6.5 batches). Plain
            // demanded-output scenario -- no active BuildQueueEntry involved, so this exercises
            // ComputeSlotCounts's own floor in isolation, independent of
            // ProductionManager.ServiceShipBuildReservations's separate dedicated-accumulator pacing.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            // 4 batches' worth (21/batch) -- comfortably above one full batch, comfortably below the
            // ~7-batch horizon threshold this test exists to catch.
            node.Inventory["Ingot:Iron"] = 90;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 3674, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 21 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var log = new List<string>();

            // First tick only warms up the freshly-created machine slot (see the cold-start note on
            // ClearCapabilitySlots); the second is the one that must actually produce.
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);
            world.UtcNow = world.UtcNow.AddSeconds(5);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            long steelPlateStock;
            node.Inventory.TryGetValue("Comp:SteelPlate", out steelPlateStock);
            Assert.True(steelPlateStock > 0,
                "expected the monopolized recipe to actually produce with 4 batches on hand, not idle at zero machines");
        }

        [Fact]
        public void ScarceSharedInput_CheapRecipesCannotPermanentlyStarveBlockedShipBuildRecipe()
        {
            // Live-observed on IGSL (E907737196391231441): two ships in the Shipyard build queue only
            // needed Comp:SteelPlate. Construction/MetalGrid/PowerCell held every Manufactory slot
            // instead, and SteelPlate never got a single one — it needs more Iron per batch than the
            // others. Each of the three cheap recipes independently only needs 1 Iron/batch, so as
            // Iron trickled in a little at a time, all of it was immediately spent on them before
            // SteelPlate's threshold could ever be reached — even though total deliveries easily
            // covered it.
            //
            // ServiceShipBuildReservations closes this by reserving each tick's fresh Iron into
            // SteelPlate's own isolated bucket BEFORE the normal bank loop (and its cheap rivals) ever
            // sees it — real physical isolation, not a floor another recipe's check has to remember to
            // honor. This asserts both halves: the cheap recipes never get a single unit (proving the
            // isolation holds, tick after tick, not just "eventually"), and SteelPlate's own Collected
            // keeps climbing across the run rather than plateauing at one bare batch — the actual
            // regression this whole redesign targets (see docs/todo.md's "priority-allocation pass,
            // not a fixed one-batch floor" entry).
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:SteelPlate"] = 200;
            node.BuildQueue.Add(build);
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Comp:Construction", NodeId = "N1", Quantity = 100, ExpiresUtc = world.UtcNow.AddHours(1) });
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Comp:MetalGrid", NodeId = "N1", Quantity = 100, ExpiresUtc = world.UtcNow.AddHours(1) });
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Comp:PowerCell", NodeId = "N1", Quantity = 100, ExpiresUtc = world.UtcNow.AddHours(1) });

            var scarcity = new FixedScarcityOracle(new Dictionary<string, double>
            {
                { "Comp:SteelPlate", 1.0 },
                { "Comp:Construction", 3.0 },
                { "Comp:MetalGrid", 3.0 },
                { "Comp:PowerCell", 3.0 },
            });
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 5 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "Construction", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Construction", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "MetalGrid", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "MetalGrid", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "PowerCell", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "PowerCell", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes, null, scarcity);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            // Fifty small deliveries of 1 Iron each (50 total), one per tick, mirroring
            // OperationsManager routing a trickle rather than one lump sum.
            long steelPlateAtHalfway = 0;
            for (int i = 0; i < 50; i++)
            {
                world.AddNodeInventory("N1", "Ingot:Iron", 1);
                world.UtcNow = world.UtcNow.AddSeconds(5);
                manager.Tick(states, world, world.Tuning, new List<string>());
                if (i == 24) build.Collected.TryGetValue("Comp:SteelPlate", out steelPlateAtHalfway);
            }

            long steelPlateStock, constructionStock, metalGridStock, powerCellStock;
            build.Collected.TryGetValue("Comp:SteelPlate", out steelPlateStock);
            node.Inventory.TryGetValue("Comp:Construction", out constructionStock);
            node.Inventory.TryGetValue("Comp:MetalGrid", out metalGridStock);
            node.Inventory.TryGetValue("Comp:PowerCell", out powerCellStock);

            Assert.True(steelPlateAtHalfway > 0,
                "expected SteelPlate to unblock once trickled deliveries reached its one-batch threshold");
            Assert.True(steelPlateStock > steelPlateAtHalfway,
                "expected SteelPlate's Collected to keep climbing across the run, not plateau at one bare batch -- "
                + "halfway=" + steelPlateAtHalfway + " final=" + steelPlateStock);
            Assert.Equal(0, constructionStock);
            Assert.Equal(0, metalGridStock);
            Assert.Equal(0, powerCellStock);
        }

        // ---- capability-install input starvation (FAC-279 secondary defect) ------------------

        private static ConversionRecipe IronComponentRecipe(string name, int ironPerBatch)
        {
            return new ConversionRecipe
            {
                BlueprintId = name, TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = ironPerBatch } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = name, Amount = 1 } },
            };
        }

        /// <summary>The four ManufactoryII recipes live EXP's six banks were all ranked on at once,
        /// at their real per-batch Ingot:Iron costs (FAC-279). InteriorPlate/Construction were
        /// admitted as ordinary catalogued wants; SteelPlate/Motor only by the ManufactoryII install
        /// BOM. Motor's 5 Nickel is dropped so Iron is the single contended input under test.</summary>
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> LiveIronCompetitionRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    IronComponentRecipe("InteriorPlate", 3),
                    IronComponentRecipe("Construction", 8),
                    IronComponentRecipe("SteelPlate", 21),
                    IronComponentRecipe("Motor", 20),
                },
            };
        }

        private static readonly Dictionary<string, double> LiveIronCompetitionScarcity =
            new Dictionary<string, double>
            {
                // The cheap pair outranks the install pair, as on live: DemandClass is not a
                // ranking term, so an install component ties or loses on output scarcity alone.
                { "Comp:InteriorPlate", 3.0 },
                { "Comp:Construction",  3.0 },
                { "Comp:SteelPlate",    2.0 },
                { "Comp:Motor",         2.0 },
            };

        private static WorldModel InstallStarvationWorld(out SpatialNode node,
            long steelBomLine = 140, long motorBomLine = 20)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = steelBomLine,
                ["Comp:Motor"]      = motorBomLine,
            };

            node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;

            // Standing catalogue wants for the two cheap components -- the same steady admission the
            // unwanted Shipyard BOM gave live EXP's banks, without depending on that BOM.
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Comp:InteriorPlate", NodeId = "N1", Quantity = 20000, ExpiresUtc = world.UtcNow.AddHours(1) });
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Comp:Construction",  NodeId = "N1", Quantity = 20000, ExpiresUtc = world.UtcNow.AddHours(1) });

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddHours(1),
            });
            return world;
        }

        private static long InstallProgress(SpatialNode node, string key)
        {
            long routable, reserved;
            node.Inventory.TryGetValue(key, out routable);
            node.ReservedInventory.TryGetValue(key, out reserved);
            return routable + reserved;
        }

        [Fact]
        public void ScarceSharedInput_CheapRecipesCannotPermanentlyStarveInstallComponentRecipe()
        {
            // FAC-279 secondary. Live EXP, 2026-09-06: six ManufactoryII banks, Ingot:Iron arriving
            // at 6.0-7.6/min and consumed the tick it appeared, SteelPlate and Motor holding
            // slotCount=1 at every one of them and producing 0.0/min while InteriorPlate and
            // Construction took the whole trickle. The install the player asked for is blocked on
            // exactly the two components nothing will ever make.
            SpatialNode node;
            WorldModel world = InstallStarvationWorld(out node);
            var manager = new ProductionManager(LiveIronCompetitionRecipes(), null,
                new FixedScarcityOracle(LiveIronCompetitionScarcity));
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            long steelAtHalfway = 0;
            for (int i = 0; i < 200; i++)
            {
                world.AddNodeInventory("N1", "Ingot:Iron", 7);   // the live per-node refinery trickle
                world.UtcNow = world.UtcNow.AddSeconds(5);
                manager.Tick(states, world, world.Tuning, new List<string>());
                if (i == 99) steelAtHalfway = InstallProgress(node, "Comp:SteelPlate");
            }

            long steel = InstallProgress(node, "Comp:SteelPlate");
            long motor = InstallProgress(node, "Comp:Motor");
            long interior, construction;
            node.Inventory.TryGetValue("Comp:InteriorPlate", out interior);
            node.Inventory.TryGetValue("Comp:Construction", out construction);

            Assert.True(steel > 0, "expected the install's SteelPlate to be made at all -- got 0 after 200 trickled deliveries"
                + " (InteriorPlate=" + interior + " Construction=" + construction + ")");
            Assert.True(motor > 0, "expected the install's Motor to be made at all -- got 0 after 200 trickled deliveries"
                + " (InteriorPlate=" + interior + " Construction=" + construction + ")");
            // Not just "eventually one batch": the reserve has to survive the whole trickle, or this
            // is the one-bare-batch plateau ComputeReservedInputs was retired for.
            Assert.True(steel > steelAtHalfway,
                "expected the install's SteelPlate to keep climbing across the run, not plateau at one bare batch -- halfway="
                + steelAtHalfway + " final=" + steel);
        }

        [Fact]
        public void HugeInstallClaim_DoesNotMonopoliseAScarceInputOrdinaryRecipesAlsoNeed()
        {
            // The other direction of the same lever, and the reason the reserve is bounded at one
            // batch rather than at the claim's full remaining need. A prefab-derived install BOM can
            // be the whole component catalogue -- the Shipyard's is ~2,500 components (FAC-279's
            // primary defect) -- and the keep-alive reposts its request forever, so a claim sized to
            // the full remaining need never shrinks below what any realistic supply can cover.
            //
            // The BOM lines here are deliberately larger than the run can ever satisfy, so the claim
            // stays live for all 80 ticks and cannot pass by completing. Sized at the full remaining
            // need, this exact run measured InteriorPlate=0 and Construction=0 with 271,415 Ingot:Iron
            // left sitting in routable inventory. If this test can pass because the install finished,
            // it is testing nothing -- raise the BOM lines, don't relax the assertion.
            SpatialNode node;
            WorldModel world = InstallStarvationWorld(out node, 100000, 100000);
            var manager = new ProductionManager(LiveIronCompetitionRecipes(), null,
                new FixedScarcityOracle(LiveIronCompetitionScarcity));
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            for (int i = 0; i < 80; i++)
            {
                world.AddNodeInventory("N1", "Ingot:Iron", 4000);
                world.UtcNow = world.UtcNow.AddSeconds(5);
                manager.Tick(states, world, world.Tuning, new List<string>());
            }

            long interior, construction, ironLeft;
            node.Inventory.TryGetValue("Comp:InteriorPlate", out interior);
            node.Inventory.TryGetValue("Comp:Construction", out construction);
            node.Inventory.TryGetValue("Ingot:Iron", out ironLeft);
            long steel = InstallProgress(node, "Comp:SteelPlate");
            long motor = InstallProgress(node, "Comp:Motor");

            Assert.True(steel > 0 && motor > 0,
                "sanity: the install must still be served when input is plentiful -- SteelPlate=" + steel
                + " Motor=" + motor);
            Assert.True(interior > 0,
                "an install claim must not lock out an ordinary catalogued recipe while the shared input is"
                + " abundant -- InteriorPlate=" + interior + " with " + ironLeft + " Ingot:Iron left unused");
            Assert.True(construction > 0,
                "an install claim must not lock out an ordinary catalogued recipe while the shared input is"
                + " abundant -- Construction=" + construction + " with " + ironLeft + " Ingot:Iron left unused");
        }

        // ---- ship-build input reservation (ServiceShipBuildReservations) --------------------

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> SingleSteelPlateRecipe()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 5 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };
        }

        [Fact]
        public void ShipBuildReservation_MovesInputPhysicallyOutOfRoutableInventory()
        {
            // Mirrors ProductionDoesNotConsumeReservedInstallStock's proof for the capability-install
            // bucket, one level upstream: reserved recipe input must be a real transfer (conserved,
            // not duplicated or destroyed), not just a subtraction another check has to remember.
            var world = TestSupport.World();
            // Pin the production cadence this test's pacing arithmetic depends on. The cold-start
            // credit window is ProductionIntervalSeconds x ProductionCreditCapIntervals, so a shipped
            // interval change would otherwise silently alter how much the priming tick collects and
            // break the assertions for a reason unrelated to what is being tested.
            world.Tuning.ProductionIntervalSeconds = 30.0;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 1000;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:SteelPlate"] = 200;
            node.BuildQueue.Add(build);

            var manager = new ProductionManager(SingleSteelPlateRecipe());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            long ironAfter;
            node.Inventory.TryGetValue("Ingot:Iron", out ironAfter);
            long reserved;
            build.ReservedInputs.TryGetValue("Ingot:Iron", out reserved);
            long collected;
            build.Collected.TryGetValue("Comp:SteelPlate", out collected);

            Assert.True(ironAfter < 1000, "expected some Iron to be reserved out of routable inventory");
            Assert.True(reserved > 0, "expected the entry's own bucket to hold the reserved Iron");
            // Conservation: every unit of Iron is either still routable, sitting reserved in the
            // bucket, or already converted into SteelPlate (5 Iron/unit) -- nothing created or lost.
            // A single cold-start tick can both reserve AND immediately run a batch (mirrors
            // MachineSlot's own cold-start burst, see RunShipBuildRecipe's elapsed-time comment).
            Assert.Equal(1000, ironAfter + reserved + collected * 5);
        }

        [Fact]
        public void ShipBuildReservationPass_CreditsCollectedAtItsOwnGuaranteedPace_EachTick()
        {
            // The reservation pass's dedicated accumulator (RunShipBuildRecipe) guarantees the
            // ship-build recipe real, predictable throughput -- 1 virtual machine's worth, paced by
            // machineSpeed/TimeSecs -- entirely independent of whatever the ordinary per-bank
            // scarcity loop separately does with any leftover Inventory afterward (that loop may ALSO
            // rank and run the same recipe on its own leftover input when the bank has spare capacity,
            // landing its own extra output in ordinary Inventory as normal surplus for ShipyardManager
            // to absorb next -- a deliberate "can go faster than the guaranteed floor" bonus per
            // docs/todo.md, not a pre-absorption-gap violation). This pins the guaranteed floor itself:
            // Collected must grow by exactly one horizon-paced batch set every tick, tick after tick.
            var world = TestSupport.World();
            // Pin the production cadence this test's pacing arithmetic depends on. The cold-start
            // credit window is ProductionIntervalSeconds x ProductionCreditCapIntervals, so a shipped
            // interval change would otherwise silently alter how much the priming tick collects and
            // break the assertions for a reason unrelated to what is being tested.
            world.Tuning.ProductionIntervalSeconds = 30.0;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 10000;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:SteelPlate"] = 200; // stays well above what a few ticks can fully collect
            node.BuildQueue.Add(build);

            var manager = new ProductionManager(SingleSteelPlateRecipe());
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            // Prime once: the accumulator's LastTick defaults to DateTime.MinValue, so its first
            // pass is a cold start and clamps to the credit window. Every pass after that credits
            // its real 5 s, contributing machineSpeed(1.0)*elapsed(5)/TimeSecs(1) = 5 batches =
            // 5 SteelPlate to Collected -- the guaranteed pace this test is about.
            manager.Tick(states, world, world.Tuning, new List<string>());
            long primed;
            build.Collected.TryGetValue("Comp:SteelPlate", out primed);

            for (int i = 1; i <= 5; i++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(5);
                manager.Tick(states, world, world.Tuning, new List<string>());

                long collected;
                build.Collected.TryGetValue("Comp:SteelPlate", out collected);
                Assert.Equal(primed + 5 * i, collected);
            }
        }

        [Fact]
        public void ShipBuildReservation_TopUpTargetsFullRemainingNeed_NotJustOneBatch()
        {
            // Live-observed on IGSL E907737196391231441: the reservation used to target a
            // horizon-paced slice of the recipe's own pacing (ShipBuildReservationHorizonSeconds),
            // which degenerated to exactly one batch's worth whenever the recipe's TimeSecs was large
            // relative to that horizon -- SteelPlate (21 Iron/batch) reserved a few Iron per tick, drained
            // to one batch, repeat, while InteriorPlate/GirderComponent/ComputerComponent kept eating
            // the real surplus left behind in plain Inventory every tick. The reservation must instead
            // greedily claim as much of the entry's FULL remaining need as Inventory currently allows --
            // here, a huge outstanding order (100,000 SteelPlate == 500,000 Iron needed) against a
            // modest 100,000 Iron on hand should reserve essentially all of it in one tick, not a small
            // horizon-sized slice.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 100000;

            var build = new BuildQueueEntry { Id = "Trade:002", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:SteelPlate"] = 100000;
            node.BuildQueue.Add(build);

            var manager = new ProductionManager(SingleSteelPlateRecipe());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            long reserved;
            build.ReservedInputs.TryGetValue("Ingot:Iron", out reserved);
            long ironLeft;
            node.Inventory.TryGetValue("Ingot:Iron", out ironLeft);

            Assert.True(reserved > 90000,
                "expected the reservation to greedily claim essentially all available Iron toward the "
                + "huge outstanding need, not a small horizon-paced slice, reserved=" + reserved);
            Assert.True(ironLeft < 10000,
                "expected the reservation to leave little unprotected Iron behind for cheap recipes to "
                + "sweep, left=" + ironLeft);
        }

        [Fact]
        public void ShipBuildReservation_OnlyTheYardsWorkingSlotReservesInputs()
        {
            // A level-1 yard works one build at a time (SpatialNode.BuildSlotCount), so only the queue
            // head reserves raw inputs toward its BOM -- the entry behind it holds nothing out of
            // routable stock for a need nobody is converting yet. Paired with
            // ShipBuildReservation_TwoWorkingSlotsAtOneNode_ReserveIndependently, which is the same
            // node one level up and is where the two entries must SUM rather than clobber.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 100000;

            var buildA = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            buildA.Bom["Comp:SteelPlate"] = 1000;
            node.BuildQueue.Add(buildA);
            var buildB = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", ExpiresUtc = world.UtcNow.AddHours(1) };
            buildB.Bom["Comp:SteelPlate"] = 1000;
            node.BuildQueue.Add(buildB);

            var manager = new ProductionManager(SingleSteelPlateRecipe());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            long reservedA, reservedB;
            buildA.ReservedInputs.TryGetValue("Ingot:Iron", out reservedA);
            buildB.ReservedInputs.TryGetValue("Ingot:Iron", out reservedB);

            Assert.True(reservedA > 0, "the working slot should have reserved a real share");
            Assert.Equal(0, reservedB); // queued behind buildA -- reserves nothing until promoted
        }

        [Fact]
        public void ShipBuildReservation_TwoWorkingSlotsAtOneNode_ReserveIndependently()
        {
            // The guard FAC-151 retired when a yard went to one working slot, restored now that the
            // slot count is the Shipyard level. The summing behaviour never left
            // MoveInventoryToBuildReserved -- it was only unreachable -- so raising the slot count
            // without this test would put it back in play unwatched.
            //
            // Both entries hold a bucket of their own: two builds sharing an input must ADD up to
            // what the node can supply, never resolve to one entry's bucket. That is also the
            // failure a duplicate BuildQueueEntry.Id produces, since every reservation call resolves
            // (nodeId, entryId) to the first match.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Capabilities[NodeCapabilityKind.Shipyard] = 2;   // two working slots
            node.Inventory["Ingot:Iron"] = 100000;

            var buildA = new BuildQueueEntry { Id = node.MintBuildEntryId("Trade"), Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            buildA.Bom["Comp:SteelPlate"] = 1000;
            node.BuildQueue.Add(buildA);
            var buildB = new BuildQueueEntry { Id = node.MintBuildEntryId("Raid"), Role = "Raid", ExpiresUtc = world.UtcNow.AddHours(1) };
            buildB.Bom["Comp:SteelPlate"] = 1000;
            node.BuildQueue.Add(buildB);

            Assert.NotEqual(buildA.Id, buildB.Id);
            Assert.True(node.IsActiveBuild(buildA));
            Assert.True(node.IsActiveBuild(buildB));

            var manager = new ProductionManager(SingleSteelPlateRecipe());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            long reservedA, reservedB, ironLeft;
            buildA.ReservedInputs.TryGetValue("Ingot:Iron", out reservedA);
            buildB.ReservedInputs.TryGetValue("Ingot:Iron", out reservedB);
            node.Inventory.TryGetValue("Ingot:Iron", out ironLeft);

            Assert.True(reservedA > 0, "first entry should have reserved a real share, reservedA=" + reservedA);
            Assert.True(reservedB > 0, "second entry should also have reserved a real share, not been "
                                       + "clobbered by the first, reservedB=" + reservedB);
            // Independent buckets drawing on one inventory: neither may be funded twice over. Not an
            // equality -- the same tick also CONVERTS reserved iron into collected plate, so the three
            // terms do not add back to the starting stock.
            Assert.True(reservedA + reservedB + ironLeft <= 100000,
                "reservations must not exceed the stock they came from, reservedA=" + reservedA
                + " reservedB=" + reservedB + " left=" + ironLeft);
        }

        [Fact]
        public void ShipBuildReservation_CapturesSameTickIngotOutput_NotJustPriorTickLeftover()
        {
            // Live-observed on IGSL E907737196391231441: with the reservation pass running before ANY
            // of a node's capability banks -- including the Refinery bank that mints the Ingot:Iron it
            // exists to protect -- the reservation snapshot only ever saw whatever survived unconsumed
            // from the PREVIOUS tick, essentially nothing, since ordinary Manufactory-tier recipes
            // sharing the input consumed nearly all of each fresh batch the same tick it was minted
            // (Production History showed IronOreToIngot producing 44 Ingot:Iron practically every
            // cycle with InteriorPlate/SmallTube/GirderComponent consuming ~39 of it the same cycle,
            // and SteelPlate firing exactly once in a 5-minute window even with the "reserve full
            // remaining need" fix already live). This proves the tier-ordering fix directly: on a
            // cold-start tick with ZERO Ingot:Iron on hand beforehand -- only raw Ore:Iron -- the
            // reservation must already hold a real share of the Iron THIS SAME TICK's refining just
            // produced, not wait a full tick behind it.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ore:Iron"] = 100000; // refining never runs dry
            // Deliberately no Ingot:Iron on hand yet -- it must come from THIS tick's refining.

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:SteelPlate"] = 200;
            node.BuildQueue.Add(build);

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 5000, ExpiresUtc = world.UtcNow.AddHours(1),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Widget", NodeId = "N1",
                Quantity = 5000, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "IronIngot", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                    },
                },
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 21 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "Widget", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Widget", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            // First tick only warms up the freshly-created MachineSlot (its LastTick is stamped to
            // `now` at creation, crediting zero elapsed time on the very tick it's created -- the same
            // cold-start behavior other tests in this file warm up for); the second tick, after a real
            // time gap, is the one where Refinery actually mints Ingot:Iron -- exactly the tick this
            // test needs to prove the reservation captures it the same tick it's minted, not one tick
            // behind.
            manager.Tick(states, world, world.Tuning, new List<string>());
            world.UtcNow = world.UtcNow.AddSeconds(5);
            manager.Tick(states, world, world.Tuning, new List<string>());

            long reserved;
            build.ReservedInputs.TryGetValue("Ingot:Iron", out reserved);
            long ironInInventory;
            node.Inventory.TryGetValue("Ingot:Iron", out ironInInventory);
            long oreLeft;
            node.Inventory.TryGetValue("Ore:Iron", out oreLeft);
            Assert.True(reserved > 0,
                "expected the reservation to capture a share of Iron refined this same tick, not just prior-tick leftover -- "
                + "reserved=" + reserved + " ironInInventory=" + ironInInventory + " oreConsumed=" + (100000 - oreLeft));
        }

        // ---- production history --------------------------------------------------------------

        [Fact]
        public void ProductionHistory_RecordsConsumptionAndProduction_CoalescedIntoOneSpanAcrossTicks()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 100000;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 1000, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var log = new List<string>();

            // Three consecutive ticks, a few seconds apart -- well inside the coalescing window --
            // should collapse into a single growing span per (recipe, item, direction), not three rows.
            for (int i = 0; i < 3; i++)
            {
                manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);
                world.UtcNow = world.UtcNow.AddSeconds(5);
            }

            List<ProductionManager.ProductionHistoryEntry> history = manager.SnapshotProductionHistory("F");

            ProductionManager.ProductionHistoryEntry consumed = Assert.Single(
                history, e => e.InvKey == "Ingot:Iron" && !e.IsOutput);
            ProductionManager.ProductionHistoryEntry produced = Assert.Single(
                history, e => e.InvKey == "Comp:SteelPlate" && e.IsOutput);

            Assert.Equal("SteelPlate", consumed.RecipeId);
            Assert.True(consumed.Quantity > 0, "expected consumed Iron across three ticks to be > 0");
            Assert.Equal(consumed.Quantity, produced.Quantity);
            Assert.True(produced.EndUtc > produced.StartUtc,
                "expected the span to have grown across ticks, not reset each time");
        }

        /// <summary>The station window's slice: one node's spans only, the one that ran most recently
        /// first. N1 runs, starves for longer than the coalescing gap, and runs again, so it holds an
        /// old span and a new one for the same recipe — the order has two different ends to get
        /// wrong. N2 runs the same recipe throughout, so the filter has a neighbour to leak.</summary>
        [Fact]
        public void NodeProductionHistory_IsOneNode_MostRecentFirst()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode n1 = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            SpatialNode n2 = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "N2");
            foreach (SpatialNode n in new[] { n1, n2 })
            {
                n.OwnerFactionTag = "F";
                n.HoldStrength = 1.0;
                n.Capabilities[NodeCapabilityKind.Manufactory] = 1;
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = n.Id,
                    Quantity = 1000, ExpiresUtc = world.UtcNow.AddHours(1),
                });
            }
            n1.Inventory["Ingot:Iron"] = 1000;
            n2.Inventory["Ingot:Iron"] = 1000;

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };
            var manager = new ProductionManager(capRecipes);
            var log = new List<string>();

            var factions = new List<FactionState> { new FactionState { Tag = "F" } };
            // Warm-up tick, then a producing one. N1 then starves past the 30 s coalescing gap, and
            // is refilled for two more ticks: its second run is a new span, not the old one grown.
            manager.Tick(factions, world, world.Tuning, log);
            world.UtcNow = world.UtcNow.AddSeconds(5);
            manager.Tick(factions, world, world.Tuning, log);
            n1.Inventory["Ingot:Iron"] = 0;
            world.UtcNow = world.UtcNow.AddSeconds(45);
            manager.Tick(factions, world, world.Tuning, log);
            n1.Inventory["Ingot:Iron"] = 1000;
            for (int i = 0; i < 2; i++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(5);
                manager.Tick(factions, world, world.Tuning, log);
            }

            List<ProductionManager.ProductionHistoryEntry> n1History = manager.SnapshotNodeProductionHistory("F", "N1");
            List<ProductionManager.ProductionHistoryEntry> n2History = manager.SnapshotNodeProductionHistory("F", "N2");

            Assert.NotEmpty(n2History);
            Assert.All(n1History, e => Assert.Equal("N1", e.NodeId));
            Assert.All(n2History, e => Assert.Equal("N2", e.NodeId));
            Assert.Empty(manager.SnapshotNodeProductionHistory("G", "N1"));

            // The precondition that makes the order checkable: two runs, two different ends.
            Assert.True(n1History.Select(e => e.EndUtc).Distinct().Count() >= 2,
                "N1 should hold an old span and a new one");
            for (int i = 1; i < n1History.Count; i++)
                Assert.True(n1History[i - 1].EndUtc >= n1History[i].EndUtc, "spans must be most recent first");
            Assert.True(n1History[0].EndUtc > n1History[n1History.Count - 1].EndUtc);
            // Equal ends break ties output first, so a refresh never swaps a batch's two rows.
            Assert.True(n1History[0].IsOutput);
        }

        [Fact]
        public void ProductionHistory_DropsSpans_OlderThanRetentionWindow()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 10;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 1000, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var log = new List<string>();

            // First tick only warms up the freshly-created machine slot (zero elapsed time credited,
            // same cold-start behavior as everywhere else in this file); the second tick is the one
            // that actually produces.
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);
            world.UtcNow = world.UtcNow.AddSeconds(5);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);
            Assert.Contains(manager.SnapshotProductionHistory("F"), e => e.InvKey == "Comp:SteelPlate");

            // Jump well past both the retention window and the span-continuation gap. Trim now runs
            // unconditionally once per faction per tick (from PrepareFaction), so a single further
            // tick is enough to sweep the stale span even though nothing more can produce.
            node.Inventory["Ingot:Iron"] = 0; // nothing left to produce on the later tick
            world.UtcNow = world.UtcNow.AddMinutes(10);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.DoesNotContain(manager.SnapshotProductionHistory("F"), e => e.InvKey == "Comp:SteelPlate");
        }

        [Fact]
        public void OversuppliedRecipe_ReceivesNoSlots_WhenScarceAlternativeExists()
        {
            var scarcity = new FixedScarcityOracle(new Dictionary<string, double>
            {
                { "Ingot:Iron", 0.5 },
                { "Ingot:Nickel", 3.0 },
            });
            var (world, manager, node, states, log) = RefineryWorld(
                ironOre: 10000, nickelOre: 10000, cobaltOre: 0, priceOracle: scarcity);

            manager.Tick(states, world, world.Tuning, log);

            var throughput = manager.SnapshotThroughput("F");
            Assert.Equal(0, SlotsFor(throughput, "Ingot:Iron"));
            Assert.Equal(6, SlotsFor(throughput, "Ingot:Nickel"));
        }

        [Fact]
        public void RealPriceOracle_UsesConcreteIngotScarcityForSlotAllocation()
        {
            var (world, manager, node, states, log) = RefineryWorld(
                ironOre: 10000, nickelOre: 10000, cobaltOre: 0);
            node.Inventory["Ingot:Iron"] = 1000000;
            var oracle = new PriceOracle(world, world.Tuning);
            oracle.RecomputeAll();
            manager.SetPriceOracle(oracle);

            Assert.True(oracle.ScarcityMultiplier(node.Id, "Ingot:Iron") <= 1.0);
            Assert.True(oracle.ScarcityMultiplier(node.Id, "Ingot:Nickel") > 1.0);

            manager.Tick(states, world, world.Tuning, log);

            var throughput = manager.SnapshotThroughput("F");
            Assert.Equal(0, SlotsFor(throughput, "Ingot:Iron"));
            Assert.Equal(6, SlotsFor(throughput, "Ingot:Nickel"));
        }

        [Fact]
        public void DemandAtOrBelowNeutral_LeavesRefineryBankIdle()
        {
            var scarcity = new FixedScarcityOracle(new Dictionary<string, double>
            {
                { "Ingot:Iron", 0.5 },
                { "Ingot:Nickel", 1.0 },
                { "Ingot:Cobalt", 0.5 },
            });
            var (world, manager, node, states, log) = RefineryWorld(
                ironOre: 10000, nickelOre: 10000, cobaltOre: 10000, priceOracle: scarcity);

            manager.Tick(states, world, world.Tuning, log);

            Assert.Empty(manager.SnapshotThroughput("F"));
        }

        [Fact]
        public void RecipeThatBecomesOversupplied_ReleasesItsAssignedSlots()
        {
            var values = new Dictionary<string, double> { { "Ingot:Iron", 3.0 } };
            var scarcity = new FixedScarcityOracle(values);
            var (world, manager, node, states, log) = RefineryWorld(
                ironOre: 10000, nickelOre: 0, cobaltOre: 0, priceOracle: scarcity);

            manager.Tick(states, world, world.Tuning, log);
            Assert.Equal(6, SlotsFor(manager.SnapshotThroughput("F"), "Ingot:Iron"));

            values["Ingot:Iron"] = 0.5;
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, log);

            Assert.Empty(manager.SnapshotThroughput("F"));
        }

        [Fact]
        public void CatalogDemand_ProductionSurplusIntentDisabled_PreservesCurrentBehavior()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionSurplusIntentEnabled = false;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var manager = new ProductionManager(
                TwoStageRecipes(),
                new StubFactionTypeCatalogue("F", "Trader", new[] { "Comp:SteelPlate" }),
                new StubPriceOracle(100));
            var log = new List<string>();
            var state = new FactionState { Tag = "F" };

            manager.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"), d => d.NodeId == "N1" && d.Kind == "Item:Comp:SteelPlate" && d.Quantity == 200);
            Assert.DoesNotContain(log, l => l.Contains("PG_SURPLUS_DEMAND"));
        }

        [Fact]
        public void CatalogDemand_ProductionSurplusIntentPostsUntilTargetThenStops()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionSurplusIntentEnabled = true;
            world.Tuning.ProductionSurplusTargetMultiple = 2.0;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:SteelPlate"] = 50;

            var manager = new ProductionManager(
                TwoStageRecipes(),
                new StubFactionTypeCatalogue("F", "Trader", new[] { "Comp:SteelPlate", "Comp:Thrust" }),
                new StubPriceOracle(100));
            var log = new List<string>();
            var state = new FactionState { Tag = "F" };

            manager.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(world.Demand.SnapshotItemDemands("F"), d => d.NodeId == "N1" && d.Kind == "Item:Comp:SteelPlate" && d.Quantity == 150);
            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Comp:Thrust");
            Assert.Contains(log, l => l.Contains("PG_SURPLUS_DEMAND") && l.Contains("item=Comp:SteelPlate") && l.Contains("target=200"));

            var atTargetWorld = TestSupport.World();
            atTargetWorld.UtcNow = world.UtcNow;
            atTargetWorld.Tuning.ProductionSurplusIntentEnabled = true;
            atTargetWorld.Tuning.ProductionSurplusTargetMultiple = 2.0;

            SpatialNode atTargetNode = atTargetWorld.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            atTargetNode.OwnerFactionTag = "F";
            atTargetNode.HoldStrength = 1.0;
            atTargetNode.StableStationId = 42;
            atTargetNode.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            atTargetNode.Inventory["Comp:SteelPlate"] = 200;

            log.Clear();
            manager.Tick(new List<FactionState> { state }, atTargetWorld, atTargetWorld.Tuning, log);

            Assert.DoesNotContain(atTargetWorld.Demand.SnapshotItemDemands("F"), d => d.NodeId == "N1" && d.Kind == "Item:Comp:SteelPlate");
            Assert.DoesNotContain(atTargetWorld.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Comp:Thrust");
            Assert.DoesNotContain(log, l => l.Contains("item=Comp:SteelPlate"));
        }

        [Fact]
        public void ProductionManager_SetPriceOracle_RebindsSurplusIntentOracle()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.ProductionSurplusIntentEnabled = true;
            world.Tuning.ProductionSurplusTargetMultiple = 2.0;

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var manager = new ProductionManager(
                TwoStageRecipes(),
                new StubFactionTypeCatalogue("F", "Trader", new[] { "Comp:SteelPlate" }));
            manager.SetPriceOracle(new StubPriceOracle(100));

            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Contains(world.Demand.SnapshotItemDemands("F"), d => d.NodeId == "N1" && d.Kind == "Item:Comp:SteelPlate" && d.Quantity == 200);
            Assert.Contains(log, l => l.Contains("PG_SURPLUS_DEMAND") && l.Contains("item=Comp:SteelPlate"));
        }

        private static int SlotsFor(List<ThroughputEntry> tp, string outputId)
        {
            int total = 0;
            foreach (ThroughputEntry t in tp)
                if (t.PrimaryOutputId == outputId) total += t.SlotCount;
            return total;
        }

        // ---- throughput investment signals --------------------------------------------------

        // Ore→Ingot (Refinery/RefineryII) and Ingot→Comp (Manufactory/ManufactoryII) recipes
        // for tests that exercise PostThroughputInvestmentRequests.
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> TwoStageRecipes()
        {
            var ironIngot = new ConversionRecipe
            {
                BlueprintId = "IronIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
            var steelPlate = new ConversionRecipe
            {
                BlueprintId = "SteelPlate", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot",     SubTypeId = "Iron",       Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
            };
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery]      = new List<ConversionRecipe> { ironIngot },
                [NodeCapabilityKind.RefineryII]    = new List<ConversionRecipe> { ironIngot },
                [NodeCapabilityKind.Manufactory]   = new List<ConversionRecipe> { steelPlate },
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe> { steelPlate },
            };
        }

        [Fact]
        public void NoRefinery_PostsRefineryBootstrap()
        {
            var world = TestSupport.World();
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            // no Refinery capability installed

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.Refinery);
        }

        // OreField node: SourceItemYield ≈ 15625/30 × 2.0 × 6 min⁻¹ ≈ 6246 ore/min.
        // A single Refinery consumes 6 × 0.65/1 × 60 = 234 ore/min — far below income.
        [Fact]
        public void HasRefinery_OreFieldNode_IncomeExceedsCapacity_PostsRefineryII()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            TestSupport.WithOre(node, "Iron");   // ore income comes from the ground now
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Ore:Iron"] = 10_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.RefineryII);
        }

        // Empty node: SourceItemYield ≈ 15625/30 × 0.05 × 6 ≈ 156 ore/min — below Refinery capacity.
        [Fact]
        public void HasRefinery_EmptyNode_IncomeUnderCapacity_DoesNotPostRefineryII()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Ore:Iron"] = 10_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.RefineryII);
        }

        [Fact]
        public void HasBothRefineries_NoManufactory_ActiveRefinery_PostsManufactoryBootstrap()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            TestSupport.WithOre(node, "Iron");   // ore income comes from the ground now
            node.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            node.Inventory["Ore:Iron"] = 10_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.Manufactory);
        }

        // Exposes bug: Manufactory is installed but idle (no component demand this tick), while
        // Refinery+RefineryII are actively producing ingots at (0.65+1.30)×6×60 = 702/min —
        // well above Manufactory capacity of 0.70×6×60 = 252/min.  ManufactoryII should be
        // requested regardless of whether Manufactory machines are currently running.
        [Fact]
        public void HasManufactory_ManufactoryIdle_RefineryOverproducingIngots_PostsManufactoryII()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            TestSupport.WithOre(node, "Iron");   // ore income comes from the ground now
            node.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII]  = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ore:Iron"] = 10_000;
            // Ingot demand opens the refinery pull gate; no component demand leaves Manufactory idle.
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.ManufactoryII);
        }

        // Network already has Refinery+RefineryII but ore income still exceeds total refining
        // capacity → another RefineryII investment should fire so the brain can install a
        // second RefineryII elsewhere in the network.
        // OreField income ≈ 6246 ore/min; Refinery+RefineryII capacity = 702/min — far below.
        [Fact]
        public void HasRefineryAndRefineryII_IncomeStillExceedsNetworkCapacity_PostsAnotherRefineryII()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            TestSupport.WithOre(node, "Iron");   // ore income comes from the ground now
            node.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            node.Inventory["Ore:Iron"] = 10_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.RefineryII);
        }

        // Network already has Manufactory+ManufactoryII but refinery output still exceeds
        // total assembly capacity → another ManufactoryII investment should fire.
        // Refinery+RefineryII output ≈ 702 ingots/min; Manufactory+ManufactoryII = 504/min.
        [Fact]
        public void HasManufactoryII_IngotSurplusStillExceedsNetworkCapacity_PostsAnotherManufactoryII()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            TestSupport.WithOre(node, "Iron");   // ore income comes from the ground now
            node.Capabilities[NodeCapabilityKind.Refinery]      = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII]    = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory]   = 1;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ore:Iron"]   = 10_000;
            node.Inventory["Ingot:Iron"] = 10_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.ManufactoryII);
        }

        // Manufactory running AND still outpaced by refineries → ManufactoryII fires.
        [Fact]
        public void HasManufactory_RefineryOutputExceedsConsumption_PostsManufactoryII()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            TestSupport.WithOre(node, "Iron");   // ore income comes from the ground now
            node.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII]  = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ore:Iron"]   = 10_000;
            node.Inventory["Ingot:Iron"] = 10_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Investments.SnapshotFor("F"), r => r.Cap == NodeCapabilityKind.ManufactoryII);
        }

        // ---- install-cost demand posting ----------------------------------------------------

        // Regression: PostInstallCostDemandsForInvestmentRequests previously skipped any cap
        // already present in networkCaps, so a throughput request for a second RefineryII (which
        // requires 300 PG to install) never drove Manufactory production.  Demand must be posted
        // regardless of whether the cap is already installed somewhere.
        [Fact]
        public void RefineryII_AlreadyInNetwork_ThroughputRequest_DrivesPGDemandOnManufactory()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII]  = 1;  // already installed
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ore:Iron"] = 10_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            // Throughput fires RefineryII request (income ≫ consumed); install demand must follow.
            long pgNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.RefineryII, ItemKind.ProcessedGood);
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
        }

        [Fact]
        public void ManufactoryII_NotInNetwork_InvestmentRequest_DrivesPGDemandOnManufactory()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            long pgNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.ManufactoryII, ItemKind.ProcessedGood);
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
        }

        [Fact]
        public void InstallCostDemand_NotPosted_WhenInventoryAlreadyMeetsThreshold()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII]  = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            long pgNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.RefineryII, ItemKind.ProcessedGood);
            node.Inventory["Comp:SteelPlate"] = pgNeed;  // already have enough

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.RefineryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
        }

        [Fact]
        public void InstallCostDemand_NotPosted_WhenReservedInventoryMeetsThreshold()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII]  = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            long pgNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.RefineryII, ItemKind.ProcessedGood);
            node.ReservedInventory["Comp:SteelPlate"] = pgNeed;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.RefineryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
        }

        [Fact]
        public void InstallCostDemand_Posted_WhenNormalAndReservedInventoryAreBelowThreshold()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery]   = 1;
            node.Capabilities[NodeCapabilityKind.RefineryII]  = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            long pgNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.RefineryII, ItemKind.ProcessedGood);
            node.Inventory["Comp:SteelPlate"] = pgNeed / 4;
            node.ReservedInventory["Comp:SteelPlate"] = pgNeed / 4;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.RefineryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
        }

        // ---- investment reservation -------------------------------------------------------

        [Fact]
        public void InvestmentRequest_MovesInstallStockIntoReservedInventory_AndLogsReservation()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            long pgNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.RefineryII, ItemKind.ProcessedGood);
            node.Inventory["Comp:SteelPlate"] = pgNeed + 25;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.RefineryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Equal(25, node.Inventory["Comp:SteelPlate"]);
            Assert.Equal(pgNeed, node.ReservedInventory["Comp:SteelPlate"]);
            Assert.Contains(log, l => l.Contains("INVEST_RESERVE")
                                   && l.Contains("Comp:SteelPlate")
                                   && l.Contains("qty=" + pgNeed)
                                   && l.Contains("for=RefineryII"));
        }

        [Fact]
        public void ProductionDoesNotConsumeReservedInstallStock()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 100;
            node.Inventory["Comp:SteelPlate"] = 300;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.RefineryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 9999, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);
            world.UtcNow = world.UtcNow.AddSeconds(5);
            manager.Tick(states, world, world.Tuning, log);

            Assert.Equal(300, node.ReservedInventory["Comp:SteelPlate"]);
        }

        // ---- BOM-backed install costs (real block components, not abstract ProcessedGood) ---

        [Fact]
        public void ActiveShipBuild_StillBlocksInstallReservation_AtTheDefaultRanks()
        {
            // The regression that matters most: with no priority set anywhere, an in-progress build
            // keeps the absolute precedence over a capability install it has always had.
            var world = MakeInstallVsBuildWorld();
            SpatialNode node = world.Graph.Nodes["N1"];

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            long reserved;
            node.ReservedInventory.TryGetValue("Comp:MetalGrid", out reserved);
            Assert.Equal(0, reserved);
            Assert.DoesNotContain(log, l => l.Contains("INVEST_RESERVE") && l.Contains("Comp:MetalGrid"));
        }

        [Fact]
        public void InstallRankedAboveTheBuild_ClaimsTheContestedComponent()
        {
            var world = MakeInstallVsBuildWorld();
            SpatialNode node = world.Graph.Nodes["N1"];
            var st = new FactionState { Tag = "F" };
            st.InstallPriority[NodeCapabilityKind.ManufactoryII] = 1;   // outranks the build's default 0

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Equal(80, node.ReservedInventory["Comp:MetalGrid"]);
            Assert.Contains(log, l => l.Contains("INVEST_RESERVE") && l.Contains("Comp:MetalGrid"));

            // Raising the build to the install's own rank restores build precedence for everything
            // claimed from here on -- a tie goes to the build. It does not claw back what the install
            // already staged: reserved stock is released by ReserveInstallStock's own expiry/target
            // phases, never by a later re-ranking.
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII]["Comp:MetalGrid"] = 120;
            node.Inventory["Comp:MetalGrid"] = 40;   // fresh supply arrives, install still 40 short
            // Level with the install's EFFECTIVE rank, not its raw 1: a set install rank is lifted
            // clear of the role-derived ranks builds now start at (FactionState.InstallRankFor), so
            // "equal" is expressed through the same helper rather than a literal that would drift if
            // the role table changed.
            node.BuildQueue[0].Priority = 1 + world.Tuning.HighestBuildRolePriority();

            var log2 = new List<string>();
            manager.Tick(new List<FactionState> { st }, world, world.Tuning, log2);

            Assert.Equal(80, node.ReservedInventory["Comp:MetalGrid"]);   // no further claim
            Assert.Equal(40, node.Inventory["Comp:MetalGrid"]);           // left for the build
            Assert.DoesNotContain(log2, l => l.Contains("INVEST_RESERVE") && l.Contains("Comp:MetalGrid"));
        }

        [Fact]
        public void PausedShipyard_FreesTheContestedComponent_ForTheInstall()
        {
            // The operator's actual case: two builds eating the component a stalled install needs,
            // resolved by pausing the yard instead of re-ranking anything.
            var world = MakeInstallVsBuildWorld();
            SpatialNode node = world.Graph.Nodes["N1"];
            node.ShipyardPaused = true;

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Equal(80, node.ReservedInventory["Comp:MetalGrid"]);
            Assert.DoesNotContain(log, l => l.Contains("SHIPBUILD_RESERVE") && l.Contains("N1"));
        }

        /// <summary>One node with an unfunded ManufactoryII install BOM and an active build, both
        /// wanting the same <c>Comp:MetalGrid</c> — less on hand than the install alone needs.</summary>
        private static WorldModel MakeInstallVsBuildWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] =
                new Dictionary<string, long> { ["Comp:MetalGrid"] = 80 };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Capabilities[NodeCapabilityKind.Shipyard]    = 1;
            node.Inventory["Comp:MetalGrid"] = 80;

            var build = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "SomeHull",
                ExpiresUtc = world.UtcNow.AddHours(23),
            };
            build.Bom["Comp:MetalGrid"] = 200;
            node.BuildQueue.Add(build);

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            return world;
        }

        [Fact]
        public void BomBackedCapability_OnlyReservesItsOwnComponents_LeavesUnrelatedComponentsAlone()
        {
            // Regression for the live bug: Comp:Thrust getting swept into a ManufactoryII install
            // reservation just because it shares ItemKind.ProcessedGood with the real BOM items.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
                ["Comp:Construction"] = 40,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:SteelPlate"] = 150;
            node.Inventory["Comp:Construction"] = 60;
            node.Inventory["Comp:Thrust"] = 5; // not in the BOM — must never be touched

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Equal(100, node.ReservedInventory["Comp:SteelPlate"]);
            Assert.Equal(40, node.ReservedInventory["Comp:Construction"]);
            Assert.Equal(50, node.Inventory["Comp:SteelPlate"]);
            Assert.Equal(20, node.Inventory["Comp:Construction"]);
            Assert.Equal(5, node.Inventory["Comp:Thrust"]);
            Assert.False(node.ReservedInventory.ContainsKey("Comp:Thrust") && node.ReservedInventory["Comp:Thrust"] > 0,
                "Comp:Thrust is not part of ManufactoryII's BOM and must never be reserved for it");
            Assert.Contains(log, l => l.Contains("INVEST_RESERVE") && l.Contains("Comp:SteelPlate") && l.Contains("for=ManufactoryII"));
            Assert.Contains(log, l => l.Contains("INVEST_RESERVE") && l.Contains("Comp:Construction") && l.Contains("for=ManufactoryII"));
            Assert.DoesNotContain(log, l => l.Contains("Comp:Thrust"));
        }

        [Fact]
        public void BomBackedCapability_DemandsSpecificShortComponent_NotAggregateProcessedGood()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:Thrust"] = 500; // plenty of *some* ProcessedGood, but not the BOM item

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= 100);
        }

        // Existing coverage note: InvestmentRequest_MovesInstallStockIntoReservedInventory_AndLogsReservation
        // above already proves a capability with no InstallBoms entry keeps using the flat
        // ItemKind path unaffected — TestSupport.World()'s tuning has no BOMs registered by
        // default, so that test doubles as the regression guard for this change.

        // ---- stale BOM reservation release ---------------------------------------------------

        [Fact]
        public void BomReservation_ForExpiredRequest_IsReleased_WhileAnotherBomRequestIsActive()
        {
            // Regression for the live DVSR bug: Comp:SmallTube 750 (exactly the Shipyard BOM) sat
            // reserved on a node with every capability maxed, long after the Shipyard request
            // expired, because the release phase only ever inspected the *active* target's keys.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.Shipyard] = new Dictionary<string, long>
            {
                ["Comp:SmallTube"] = 750,
            };
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:SteelPlate"] = 150;
            node.ReservedInventory["Comp:SmallTube"] = 750;   // backing Shipyard request already expired

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            long stillReserved;
            node.ReservedInventory.TryGetValue("Comp:SmallTube", out stillReserved);
            Assert.Equal(0, stillReserved);
            Assert.Equal(750, node.Inventory["Comp:SmallTube"]);
            Assert.Contains(log, l => l.Contains("INVEST_RELEASE") && l.Contains("Comp:SmallTube"));

            // The still-requested capability keeps its reservation.
            Assert.Equal(100, node.ReservedInventory["Comp:SteelPlate"]);
        }

        [Fact]
        public void BomReservation_IsReleased_WhenNoBomBackedRequestIsActiveAtAll()
        {
            // The `no BOM request active` case is exactly when a stale reservation has least claim
            // on the stock, so the release must still run -- it used to be skipped entirely.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.ReservedInventory["Comp:SteelPlate"] = 100;   // no investment request of any kind

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            long stillReserved;
            node.ReservedInventory.TryGetValue("Comp:SteelPlate", out stillReserved);
            Assert.Equal(0, stillReserved);
            Assert.Equal(100, node.Inventory["Comp:SteelPlate"]);
            Assert.Contains(log, l => l.Contains("INVEST_RELEASE") && l.Contains("Comp:SteelPlate"));
        }

        [Fact]
        public void BomReservation_ReleasesOnlyTheExcess_WhenRequestStillWantsPartOfIt()
        {
            // Two requests' worth was reserved; one expired. Only the surplus above the surviving
            // request's BOM goes back, not the whole reservation.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.ReservedInventory["Comp:SteelPlate"] = 250;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Equal(100, node.ReservedInventory["Comp:SteelPlate"]);
            Assert.Equal(150, node.Inventory["Comp:SteelPlate"]);
            Assert.Contains(log, l => l.Contains("INVEST_RELEASE") && l.Contains("Comp:SteelPlate"));
        }

        [Fact]
        public void BomReservation_DoesNotReleaseFlatCostReservations_OwnedByTheItemKindPhases()
        {
            // Ore:Iron is not part of any registered BOM, so the BOM release phase must leave it
            // to the flat ItemKind phases rather than draining another capability's staging.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };
            long oreNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.Refinery, ItemKind.Ore);
            Assert.True(oreNeed > 0, "Refinery must have a flat Ore install cost for this test to mean anything");

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.ReservedInventory["Ore:Iron"] = oreNeed;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Refinery,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            long reserved;
            node.ReservedInventory.TryGetValue("Ore:Iron", out reserved);
            Assert.Equal(oreNeed, reserved);
            Assert.DoesNotContain(log, l => l.Contains("INVEST_RELEASE") && l.Contains("Ore:Iron"));
        }

        // ---- post-load release grace ---------------------------------------------------------

        [Fact]
        public void BomReservation_SurvivesTheGraceWindow_AfterALoadWithAnEmptyRegister()
        {
            // A restart reloads ReservedInventory from the brain file but starts InvestmentRegister
            // empty, so without the grace window the first tick hands every staged component back --
            // players would watch install stock evaporate after each restart.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.ReservedInventory["Comp:SteelPlate"] = 100;

            var manager = new ProductionManager(TwoStageRecipes());
            manager.BeginReleaseGrace(world.UtcNow, 300.0);
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Equal(100, node.ReservedInventory["Comp:SteelPlate"]);
            Assert.DoesNotContain(log, l => l.Contains("INVEST_RELEASE"));
        }

        [Fact]
        public void BomReservation_IsReleased_OnceThePostLoadGraceExpires()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.ReservedInventory["Comp:SteelPlate"] = 100;

            var manager = new ProductionManager(TwoStageRecipes());
            manager.BeginReleaseGrace(world.UtcNow, 300.0);
            world.UtcNow = world.UtcNow.AddSeconds(301);   // grace elapsed, request still absent

            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            long stillReserved;
            node.ReservedInventory.TryGetValue("Comp:SteelPlate", out stillReserved);
            Assert.Equal(0, stillReserved);
            Assert.Contains(log, l => l.Contains("INVEST_RELEASE") && l.Contains("Comp:SteelPlate"));
        }

        // ---- ship-build priority over capability-install reservation ------------------------

        [Fact]
        public void ReserveInstallBomStock_PreservesActiveShipBuildFloor_OnlyClaimsSurplus()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:SteelPlate"] = 150; // 100 for the install BOM's target + 50 spare

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:SteelPlate"] = 120; // ship build still needs 120 of it, none collected yet
            node.BuildQueue.Add(build);

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            // 150 on hand - 120 ship-build floor = 30 genuine surplus; install BOM wanted 100 but only 30 is claimable.
            Assert.Equal(30, node.ReservedInventory["Comp:SteelPlate"]);
            Assert.Equal(120, node.Inventory["Comp:SteelPlate"]); // ship-build's full need still sits in routable Inventory
        }

        [Fact]
        public void ReserveInstallBomStock_ExpiredShipBuild_ImposesNoFloor()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:SteelPlate"] = 150;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(-1) }; // expired
            build.Bom["Comp:SteelPlate"] = 120;
            node.BuildQueue.Add(build);

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            // Expired build imposes no floor -- the full 100-unit BOM target is claimable.
            Assert.Equal(100, node.ReservedInventory["Comp:SteelPlate"]);
        }

        [Fact]
        public void ReserveInstallStock_FlatItemKindPath_PreservesActiveShipBuildFloor()
        {
            // Shipyard has no registered InstallBoms entry by default, so its install cost flows
            // through the flat-ItemKind branch -- that branch needs the same ship-build floor.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            long need = world.Tuning.InstallCostFor(NodeCapabilityKind.Shipyard, ItemKind.ProcessedGood);
            node.Inventory["Comp:SteelPlate"] = need + 50; // enough for the flat cost plus spare

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:SteelPlate"] = need; // ship build's outstanding need matches the flat install cost exactly
            node.BuildQueue.Add(build);

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            // Only the 50-unit surplus above the ship-build floor is claimable, short of the full flat cost.
            Assert.Equal(50, node.ReservedInventory["Comp:SteelPlate"]);
        }

        // ---- founding-cost demand posting --------------------------------------------------

        // N### node with hold ≥ FoundHoldThreshold and no station, however thin its ground:
        // ProductionManager must post PG demand so the Manufactory starts building toward 500 PG.
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]   // a player's found never reads this stock
        public void UnfoundedNode_AboveHoldThreshold_DrivesPGDemandOnManufactory(bool playerFaction, bool posts)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // Founded node with Manufactory (the PG source)
            SpatialNode station = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            station.OwnerFactionTag = "F";
            station.HoldStrength    = 1.0;
            station.StableStationId = 42;
            station.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            // Unfounded N### node that qualifies for Found
            SpatialNode unfounded = world.Graph.Add(new Vec3(100, 0, 0), 0.15, "N2");
            unfounded.OwnerFactionTag = "F";
            unfounded.HoldStrength    = 0.7;   // above FoundHoldThreshold (0.6)
            unfounded.StableStationId = 0;

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F", IsPlayerFaction = playerFaction } },
                         world, world.Tuning, new List<string>());

            long pgNeed = world.Tuning.FoundItemCostProcessedGood;
            bool posted = world.Demand.SnapshotItemDemands("F")
                .Any(d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
            Assert.Equal(posts, posted);
        }

        [Fact]
        public void FoundingCostDemand_NotPosted_WhenPGAlreadyMeetsThreshold()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode station = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            station.OwnerFactionTag = "F";
            station.HoldStrength    = 1.0;
            station.StableStationId = 42;
            station.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            SpatialNode unfounded = world.Graph.Add(new Vec3(100, 0, 0), 0.9, "N2");
            unfounded.OwnerFactionTag = "F";
            unfounded.HoldStrength    = 0.7;
            unfounded.StableStationId = 0;

            long pgNeed = world.Tuning.FoundItemCostProcessedGood;
            station.Inventory["Comp:SteelPlate"] = pgNeed;  // already enough PG

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
        }

        [Fact]
        public void FoundingCostDemand_NotPosted_WhenNoNodePassesThresholds()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode station = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            station.OwnerFactionTag = "F";
            station.HoldStrength    = 1.0;
            station.StableStationId = 42;
            station.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            // Unfounded node fails HoldStrength threshold (0.5 < 0.6)
            SpatialNode unfounded = world.Graph.Add(new Vec3(100, 0, 0), 0.9, "N2");
            unfounded.OwnerFactionTag = "F";
            unfounded.HoldStrength    = 0.5;
            unfounded.StableStationId = 0;

            var manager = new ProductionManager(TwoStageRecipes());
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            long pgNeed = world.Tuning.FoundItemCostProcessedGood;
            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity >= pgNeed);
        }

        // ---- catalog demand (PostCatalogDemands) --------------------------------------------

        // Demand quantity at zero stock = ceil(MissingFraction × cap + MinFraction × cap)
        //                                = ceil(0.01 × 1000 + 0.001 × 1000) = 11.
        [Fact]
        public void CatalogDemand_PostsDemand_ProportionalToDeficit_AtZeroStock()
        {
            var world = TestSupport.World();
            world.Tuning.StoreListingCapBulk = 1000;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            // No SteelPlate in inventory — deficit = cap = 1 000.

            var catalogue = new StubFactionTypeCatalogue("F", "Miner",
                offerKeys: new[] { "Comp:SteelPlate" });

            var manager = new ProductionManager(SteelPlateRecipes(), catalogue);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                         world, world.Tuning, new List<string>());

            // ceil(0.01 × 1000 + 0.001 × 1000) = ceil(11) = 11
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity == 11);
        }

        // At half stock: deficit = 500, demand = ceil(0.01×500 + 0.001×1000) = ceil(6) = 6.
        [Fact]
        public void CatalogDemand_ReducesDemand_AsStockIncreases()
        {
            var world = TestSupport.World();
            world.Tuning.StoreListingCapBulk = 1000;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.Inventory["Comp:SteelPlate"] = 500;

            var catalogue = new StubFactionTypeCatalogue("F", "Miner",
                offerKeys: new[] { "Comp:SteelPlate" });

            var manager = new ProductionManager(SteelPlateRecipes(), catalogue);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                         world, world.Tuning, new List<string>());

            // ceil(0.01 × 500 + 0.001 × 1000) = ceil(6) = 6
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity == 6);
        }

        // At full cap: deficit = 0, demand = ceil(0 + 0.001 × 1000) = 1 (minimum standing demand).
        [Fact]
        public void CatalogDemand_PostsMinimumDemand_WhenStockAtCap()
        {
            var world = TestSupport.World();
            world.Tuning.StoreListingCapBulk = 1000;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.Inventory["Comp:SteelPlate"] = 1000; // at cap, deficit = 0

            var catalogue = new StubFactionTypeCatalogue("F", "Miner",
                offerKeys: new[] { "Comp:SteelPlate" });

            var manager = new ProductionManager(SteelPlateRecipes(), catalogue);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                         world, world.Tuning, new List<string>());

            // ceil(0 + 0.001 × 1000) = 1
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate" && d.Quantity == 1);
        }

        [Fact]
        public void CatalogDemand_PostsDistinctProcessedGoodDemands_ForMultipleComponentOffers()
        {
            var world = TestSupport.World();
            world.Tuning.StoreListingCapBulk = 1000;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;

            var catalogue = new StubFactionTypeCatalogue("F", "Miner",
                offerKeys: new[] { "Comp:SteelPlate", "Comp:Thrust" });

            var manager = new ProductionManager(SteelAndThrustRecipes(), catalogue);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                world, world.Tuning, new List<string>());

            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate");
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:Thrust");
        }

        // Items in the OffersList that have no covering recipe must not generate demand —
        // posting unresolvable demands would create permanent noise.
        [Fact]
        public void CatalogDemand_SkipsItem_WhenNoRecipeProducesIt()
        {
            var world = TestSupport.World();
            world.Tuning.StoreListingCapBulk = 1000;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;

            // Catalogue says faction offers SteelPlate, but capRecipes has NO recipe for it.
            var catalogue = new StubFactionTypeCatalogue("F", "Miner",
                offerKeys: new[] { "Comp:SteelPlate" });

            var oreOnlyRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "IronOreToIngot", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                    },
                },
            };

            var manager = new ProductionManager(oreOnlyRecipes, catalogue);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                         world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Comp:SteelPlate");
        }

        // Classifies "SemiAutoPistolItem" and "ElitePistolItem" as ProcessedGood/Weapon; everything
        // else falls through to NullItemRegistry (Component for any Comp: subtype it knows about).
        private sealed class FakeWeaponRegistry : IItemRegistry
        {
            private static bool IsWeapon(string subTypeId)
            {
                return subTypeId == "SemiAutoPistolItem" || subTypeId == "ElitePistolItem";
            }

            public bool TryGetItemKind(string subTypeId, out ItemKind kind)
            {
                if (IsWeapon(subTypeId)) { kind = ItemKind.ProcessedGood; return true; }
                return NullItemRegistry.Instance.TryGetItemKind(subTypeId, out kind);
            }

            public string RepresentativeSubType(ItemKind kind)
            {
                return NullItemRegistry.Instance.RepresentativeSubType(kind);
            }

            public ProcessedGoodCategory CategoryFor(string subTypeId)
            {
                return IsWeapon(subTypeId) ? ProcessedGoodCategory.Weapon : ProcessedGoodCategory.Component;
            }
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> PistolRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Pistol", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_PhysicalGunObject", SubTypeId = "SemiAutoPistolItem", Amount = 1 } },
                    },
                },
            };
        }

        // Mirrors the live "SemiAutoPistolItem -> ElitePistolItem" upgrade recipe used by
        // PostRefinementRequests's Component->Weapon gating tests.
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> PistolUpgradeRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "ElitePistol", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_PhysicalGunObject", SubTypeId = "SemiAutoPistolItem", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_PhysicalGunObject", SubTypeId = "ElitePistolItem", Amount = 1 } },
                    },
                },
            };
        }

        [Fact]
        public void CatalogDemand_WeaponItem_UsesWeaponCapNotBulkCap()
        {
            var world = TestSupport.World();
            world.Registry = new FakeWeaponRegistry();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;

            var catalogue = new StubFactionTypeCatalogue("F", "Militia", offerKeys: new[] { "SemiAutoPistolItem" });
            var manager = new ProductionManager(PistolRecipes(), catalogue);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                         world, world.Tuning, new List<string>());

            long cap = world.Tuning.StoreListingCapWeapon;
            long expectedQty = (long)Math.Ceiling(
                world.Tuning.CatalogDemandMissingFraction * cap +
                world.Tuning.CatalogDemandMinFraction * cap);

            Assert.True(cap < world.Tuning.StoreListingCapBulk);
            Assert.Contains(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:SemiAutoPistolItem" && d.Quantity == expectedQty);
        }

        [Fact]
        public void CatalogDemand_HysteresisSuppressesAtCap_AndResumesBelowBand()
        {
            var world = TestSupport.World();
            world.Registry = new FakeWeaponRegistry();
            world.Tuning.CatalogDemandHysteresis = 0.10;
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;

            var catalogue = new StubFactionTypeCatalogue("F", "Militia", offerKeys: new[] { "SemiAutoPistolItem" });
            var manager = new ProductionManager(PistolRecipes(), catalogue);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            long cap = world.Tuning.StoreListingCapWeapon;

            // Tick 1: zero stock — demand posted, establishing a baseline entry.
            manager.Tick(states, world, world.Tuning, new List<string>());
            DemandEntry initial = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:SemiAutoPistolItem");

            // Tick 2: stock reaches the cap — suppression triggers this tick, entry must not be refreshed.
            node.Inventory["SemiAutoPistolItem"] = cap;
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, new List<string>());
            DemandEntry afterCap = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:SemiAutoPistolItem");
            Assert.Equal(initial.ExpiresUtc, afterCap.ExpiresUtc);

            // Tick 3: a small drawdown (e.g. one bank purchase) — well within the hysteresis band, stays quiet.
            node.Inventory["SemiAutoPistolItem"] = cap - 1;
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, new List<string>());
            DemandEntry afterSmallDip = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:SemiAutoPistolItem");
            Assert.Equal(initial.ExpiresUtc, afterSmallDip.ExpiresUtc);

            // Tick 4: stock drops below cap × (1 - hysteresis) — suppression releases, demand resumes.
            node.Inventory["SemiAutoPistolItem"] = (long)(cap * 0.5);
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, new List<string>());
            DemandEntry afterRelease = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:SemiAutoPistolItem");
            Assert.True(afterRelease.ExpiresUtc > initial.ExpiresUtc);
        }

        // ---- refinement demand gating (PostRefinementRequests) ------------------------------

        // Demand.Post is a shared upsert used by several unrelated posters (e.g. install-cost demands
        // triggered by capability investment requests can also post "Item:Comp:SteelPlate"), so these
        // tests assert on the REFINE_DEMAND log line specifically — the only signal attributable to
        // PostRefinementRequests — rather than on the shared demand register.

        [Fact]
        public void RefinementDemand_OreToIngot_PostsRegardlessOfOutputStock()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Ore:Iron"] = 1000;   // >= RefinementMinQty
            node.Inventory["Ingot:Iron"] = 10_000_000; // far above any plausible cap — must not matter

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Ingot:Iron"));
        }

        [Fact]
        public void RefinementDemand_IngotToComponent_SkippedWhenOutputAtCap()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 1000; // >= RefinementMinQty
            node.Inventory["Comp:SteelPlate"] = world.Tuning.StoreListingCapBulk; // already at cap

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.DoesNotContain(log, m => m.Contains("REFINE_DEMAND Comp:SteelPlate"));
        }

        [Fact]
        public void RefinementDemand_IngotToComponent_PostsWhenOutputBelowCap()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 1000; // >= RefinementMinQty
            // No Comp:SteelPlate in inventory — well below cap.

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Comp:SteelPlate"));
        }

        [Fact]
        public void RefinementDemand_ComponentToWeapon_GatedAtWeaponCap()
        {
            var world = TestSupport.World();
            world.Registry = new FakeWeaponRegistry();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["SemiAutoPistolItem"] = 1000; // >= RefinementMinQty

            long weaponCap = world.Tuning.StoreListingCapWeapon;
            var manager = new ProductionManager(PistolUpgradeRecipes());
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            // At/above cap: no refinement demand.
            node.Inventory["ElitePistolItem"] = weaponCap;
            var logAtCap = new List<string>();
            manager.Tick(states, world, world.Tuning, logAtCap);
            Assert.DoesNotContain(logAtCap, m => m.Contains("REFINE_DEMAND ElitePistolItem"));

            // Below cap: refinement demand resumes.
            node.Inventory["ElitePistolItem"] = 0;
            world.UtcNow = world.UtcNow.AddSeconds(1);
            var logBelowCap = new List<string>();
            manager.Tick(states, world, world.Tuning, logBelowCap);
            Assert.Contains(logBelowCap, m => m.Contains("REFINE_DEMAND ElitePistolItem"));
        }

        [Fact]
        public void RefinementDemand_InputBelowMinQty_PostsNothing_RegardlessOfGate()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 10; // below RefinementMinQty (50)

            var manager = new ProductionManager(TwoStageRecipes());
            var log = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.DoesNotContain(log, m => m.Contains("REFINE_DEMAND"));
        }

        /// <summary>Two Manufactory recipes over one input, Medical declared first — the live order
        /// for <c>Ingot:Iron</c>.</summary>
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> IronToMedicalThenSteel()
        {
            Func<string, ConversionRecipe> comp = sub => new ConversionRecipe
            {
                BlueprintId = sub, TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot",     SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = sub,    Amount = 1 } },
            };
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe> { comp("Medical"), comp("SteelPlate") },
            };
        }

        private static (WorldModel world, SpatialNode node) IronHoldingManufactory()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 1000;
            return (world, node);
        }

        [Fact]
        public void RefinementDemand_ACappedFirstRecipe_DoesNotSilenceTheInputsOtherRecipes()
        {
            var (world, node) = IronHoldingManufactory();
            node.Inventory["Comp:Medical"] = world.Tuning.StoreListingCapBulk;

            var log = new List<string>();
            new ProductionManager(IronToMedicalThenSteel())
                .Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.DoesNotContain(log, m => m.Contains("REFINE_DEMAND Comp:Medical"));
            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Comp:SteelPlate from=Ingot:Iron"));
        }

        [Fact]
        public void RefinementDemand_AsksForEveryOutputTheNodeIsShortOf_NotOnlyTheFirstRecipes()
        {
            var (world, node) = IronHoldingManufactory();

            var log = new List<string>();
            new ProductionManager(IronToMedicalThenSteel())
                .Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Comp:Medical from=Ingot:Iron"));
            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Comp:SteelPlate from=Ingot:Iron"));
        }

        [Fact]
        public void RefinementDemand_AnOutputTheNodeIsNotShortOf_IsNotAskedFor()
        {
            var (world, node) = IronHoldingManufactory();
            // SteelPlate short; Medical carries no reference, so the oracle reads 1.0 for it.
            var oracle = new FixedScarcityOracle(new Dictionary<string, double> { { "Comp:SteelPlate", 2.0 } });

            var log = new List<string>();
            new ProductionManager(IronToMedicalThenSteel(), null, oracle)
                .Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.DoesNotContain(log, m => m.Contains("REFINE_DEMAND Comp:Medical"));
            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Comp:SteelPlate from=Ingot:Iron"));
        }

        [Fact]
        public void RefinementDemand_OreToIngot_PostsEvenWhenTheOracleReadsNoShortage()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Ore:Iron"] = 1000;
            var oracle = new FixedScarcityOracle(new Dictionary<string, double> { { "Ingot:Iron", 0.5 } });

            var log = new List<string>();
            new ProductionManager(TwoStageRecipes(), null, oracle)
                .Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Ingot:Iron from=Ore:Iron"));
        }

        /// <summary>The live gas shape: the generator's oxygen recipe resolves first, and oxygen is
        /// above the refinement pass's shop limit (6.4M L, EXP live). Hydrogen is still asked for.</summary>
        [Fact]
        public void RefinementDemand_IceWithOxygenAtItsCap_StillAsksForHydrogen()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            node.Inventory["Ore:" + world.Tuning.IceOreSubTypeId] = 1_000_000;
            node.Inventory["Gas:Oxygen"] = 6_400_000;

            Func<string, double, ConversionRecipe> gas = (sub, amount) => new ConversionRecipe
            {
                BlueprintId = "Gas/OxygenGenerator/" + sub, TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",           SubTypeId = "Ice", Amount = 25 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_GasProperties", SubTypeId = sub,   Amount = amount } },
            };
            var recipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.FuelDepot] = new List<ConversionRecipe> { gas("Oxygen", 250), gas("Hydrogen", 500) },
            };
            var oracle = new FixedScarcityOracle(new Dictionary<string, double>
            {
                { "Gas:Oxygen", 2.0 }, { "Gas:Hydrogen", 2.0 },
            });

            var log = new List<string>();
            new ProductionManager(recipes, null, oracle)
                .Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            Assert.DoesNotContain(log, m => m.Contains("REFINE_DEMAND Gas:Oxygen"));
            Assert.Contains(log, m => m.Contains("REFINE_DEMAND Gas:Hydrogen from=Ore:Ice"));
        }

        // capRecipes shared by the catalog demand tests: Refinery (ore→ingot) + Manufactory recipes for SteelPlate and Thrust.
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> SteelAndThrustRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "IronOreToIngot", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "NickelOreToIngot", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Nickel", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Nickel", Amount = 1 } },
                    },
                },
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot",     SubTypeId = "Iron",       Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "Thrust", TimeSecs = 1f,
                        Inputs  = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Nickel", Amount = 1 },
                        },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Thrust", Amount = 1 } },
                    },
                },
            };
        }

        // capRecipes shared by the catalog demand tests: Refinery (ore→ingot) + Manufactory (ingot→SteelPlate).
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> SteelPlateRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "IronOreToIngot", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                    },
                },
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot",     SubTypeId = "Iron",       Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            };
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> ShipPriorityManufactoryRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Thrust", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Thrust", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "SemiAutoPistol", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_PhysicalGunObject", SubTypeId = "SemiAutoPistolItem", Amount = 1 } },
                    },
                },
            };
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> ShipPriorityMultiOutputManufactoryRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "ThrustAndSteelPlate", TimeSecs = 1f,
                        Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Thrust", Amount = 1 },
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 },
                        },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "SemiAutoPistol", TimeSecs = 1f,
                        Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_PhysicalGunObject", SubTypeId = "SemiAutoPistolItem", Amount = 1 } },
                    },
                },
            };
        }

        // ---- capability upgrade (level > 1 → more machine slots) ---------------------------

        // Each level adds baseCount (6) machines. Level 2 → 12 slots → twice the slot count
        // when a single well-supplied recipe fills the bank.
        [Fact]
        public void CapabilityLevel2_DoublesMachineSlots_VsLevel1()
        {
            var recipe = new ConversionRecipe
            {
                BlueprintId = "IronIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe> { recipe },
            };

            int SlotsAfterTick(int level)
            {
                var world = TestSupport.World();
                world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
                node.OwnerFactionTag = "F";
                node.HoldStrength = 1.0;
                node.StableStationId = 42;
                node.Capabilities[NodeCapabilityKind.Refinery] = level;
                node.Inventory["Ore:Iron"] = 100_000;   // plenty of ore so every slot can run
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                    Quantity = 99_999, ExpiresUtc = world.UtcNow.AddHours(1),
                });
                var mgr = new ProductionManager(capRecipes);
                mgr.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());
                return SlotsFor(mgr.SnapshotThroughput("F"), "Ingot:Iron");
            }

            int slots1 = SlotsAfterTick(1);
            int slots2 = SlotsAfterTick(2);
            Assert.Equal(6,  slots1);
            Assert.Equal(12, slots2);
        }

        // Level 5 → 30 slots. Verifies the linear scale holds at the tuning cap.
        [Fact]
        public void CapabilityLevel5_Produces30Slots()
        {
            var recipe = new ConversionRecipe
            {
                BlueprintId = "IronIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe> { recipe },
            };

            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 5;
            node.Inventory["Ore:Iron"] = 100_000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 99_999, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var mgr = new ProductionManager(capRecipes);
            mgr.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.Equal(30, SlotsFor(mgr.SnapshotThroughput("F"), "Ingot:Iron"));
        }

        private sealed class StubPriceOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            private readonly long _reference;

            public StubPriceOracle(long reference)
            {
                _reference = reference;
            }

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            {
                if (kind == ItemKind.ProcessedGood)
                {
                    reference = _reference;
                    return true;
                }

                reference = 0;
                return false;
            }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                return 1.0;
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                if (inventoryKey == "Comp:SteelPlate" || inventoryKey == "Comp:Thrust")
                {
                    reference = _reference;
                    return true;
                }

                reference = 0;
                return false;
            }
        }

        /// <summary>Posts the two rows <see cref="ShipyardManager"/> writes for each outstanding key of
        /// <paramref name="node"/>'s build queue -- BOM minus Collected, <see cref="DemandClass.ShipBuild"/>:
        /// the node-pinned <c>Need:</c> row (the oracle's owed term at the yard) and its <c>Item:</c>
        /// twin (the wanted floor elsewhere, and the output demand that caps the BOM recipe's input).</summary>
        private static void PostYardNeeds(WorldModel world, SpatialNode node, string factionTag)
        {
            DateTime expires = world.UtcNow.AddMinutes(5);
            foreach (BuildQueueEntry entry in node.BuildQueue)
                foreach (KeyValuePair<string, long> kv in entry.Bom)
                {
                    long collected;
                    entry.Collected.TryGetValue(kv.Key, out collected);
                    long shortfall = kv.Value - collected;
                    if (shortfall <= 0) continue;
                    world.Demand.Post(new DemandEntry
                    {
                        FactionTag = factionTag, Kind = DemandRegister.ItemKind(kv.Key), NodeId = node.Id,
                        ExpiresUtc = expires, Quantity = shortfall, Class = DemandClass.ShipBuild,
                    });
                    world.Demand.Post(DemandEntry.ForNeed(factionTag, kv.Key, node.Id, expires, shortfall,
                        DemandClass.ShipBuild));
                }
        }

        /// <summary>The oracle a yard's BOM meets live: the real <see cref="PriceOracle"/>, reading
        /// the yard's posted needs. <paramref name="fixedOthers"/> pins keys the offline registry
        /// cannot price (a hand-held weapon) or whose reading the test is not about.</summary>
        private static IPriceOracle YardOracle(WorldModel world, SpatialNode node,
            Dictionary<string, double> fixedOthers)
        {
            world.Registry = NullItemRegistry.Instance;
            PostYardNeeds(world, node, "F");
            var oracle = new PriceOracle(world, world.Tuning);
            foreach (BuildQueueEntry entry in node.BuildQueue)
                foreach (KeyValuePair<string, long> kv in entry.Bom)
                {
                    long collected;
                    entry.Collected.TryGetValue(kv.Key, out collected);
                    if (collected >= kv.Value) continue;
                    Assert.True(oracle.ScarcityMultiplier(node.Id, kv.Key) > 1.0,
                        "the yard's posted need should read as scarcity for " + kv.Key);
                }
            return fixedOthers == null || fixedOthers.Count == 0
                ? (IPriceOracle)oracle
                : new PinnedOverOracle(oracle, fixedOthers);
        }

        /// <summary>Answers from <c>pinned</c> where it has a key and from the real oracle otherwise.</summary>
        private sealed class PinnedOverOracle : IPriceOracle
        {
            private readonly IPriceOracle _real;
            private readonly Dictionary<string, double> _pinned;

            public PinnedOverOracle(IPriceOracle real, Dictionary<string, double> pinned)
            {
                _real = real;
                _pinned = pinned;
            }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double value;
                return _pinned.TryGetValue(inventoryKey, out value) ? value : _real.ScarcityMultiplier(nodeId, inventoryKey);
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                return _real.TryGetReferenceStock(nodeId, inventoryKey, out reference);
            }

            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target)
            {
                return _real.TryGetMeasuredTargetStock(nodeId, inventoryKey, out target);
            }
        }

        private sealed class FixedScarcityOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            private readonly Dictionary<string, double> _scarcity;

            public FixedScarcityOracle(Dictionary<string, double> scarcity)
            {
                _scarcity = scarcity;
            }

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            {
                reference = 0;
                return false;
            }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double value;
                return _scarcity.TryGetValue(inventoryKey, out value) ? value : 1.0;
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                reference = 0;
                return false;
            }
        }

        private sealed class StubFactionTypeCatalogue : IFactionTypeCatalogue
        {
            private static readonly IReadOnlyList<string> Empty = new List<string>().AsReadOnly();
            private readonly string _tag;
            private readonly string _type;
            private readonly IReadOnlyList<string> _offerKeys;

            public StubFactionTypeCatalogue(string tag, string type, string[] offerKeys)
            {
                _tag       = tag;
                _type      = type;
                _offerKeys = new List<string>(offerKeys).AsReadOnly();
            }

            public bool TryGetFactionType(string factionTag, out string factionType)
            {
                if (factionTag == _tag) { factionType = _type; return true; }
                factionType = null;
                return false;
            }

            public IReadOnlyList<string> OfferInvKeys(string factionType)
            {
                return factionType == _type ? _offerKeys : Empty;
            }

            public IReadOnlyList<string> OrderInvKeys(string factionType) { return Empty; }
        }

        // ---- stale reservation release -----------------------------------------------------

        [Fact]
        public void ExpiredInvestmentRequest_ReleasesReservedStockToNormalInventory()
        {
            // Arrange: reservation was made for Refinery (ore cost = 300), but the
            // InvestmentRequest has since expired. On the next tick the excess should be
            // returned to node.Inventory and logged as INVEST_RELEASE.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;

            long oreNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.Refinery, ItemKind.Ore);
            // Pre-populate reserved inventory as if a previous tick had reserved it.
            node.ReservedInventory["Ore:Iron"] = oreNeed;
            // Normal inventory is empty — the ore was moved out.

            // No active InvestmentRequest for Refinery (request expired / not re-posted).
            // world.Investments is empty.

            var manager = new ProductionManager(TwoStageRecipes());
            var log     = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            // All reserved ore must return to normal inventory.
            long reserved;
            node.ReservedInventory.TryGetValue("Ore:Iron", out reserved);
            Assert.Equal(0, reserved);

            long normal;
            node.Inventory.TryGetValue("Ore:Iron", out normal);
            Assert.Equal(oreNeed, normal);

            // Log must contain an INVEST_RELEASE line.
            Assert.Contains(log, l => l.Contains("INVEST_RELEASE")
                                   && l.Contains("Ore:Iron")
                                   && l.Contains("qty=" + oreNeed));
        }

        [Fact]
        public void PartialExpiry_ReleasesOnlyExcessReservation()
        {
            // Two requests were active: Refinery (ore=300) + Shipyard (ore=100 — see s_installOre).
            // Now only the Shipyard request remains. Reserved = 400, target = 100 → release 300.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;

            long refineryOre = world.Tuning.InstallCostFor(NodeCapabilityKind.Refinery, ItemKind.Ore);
            long shipyardOre = world.Tuning.InstallCostFor(NodeCapabilityKind.Shipyard, ItemKind.Ore);
            long totalOre    = refineryOre + shipyardOre;

            node.ReservedInventory["Ore:Iron"] = totalOre; // both requests were previously reserved

            // Only Shipyard request still active.
            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log     = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            // Reserved should be exactly the Shipyard cost.
            long reserved;
            node.ReservedInventory.TryGetValue("Ore:Iron", out reserved);
            Assert.Equal(shipyardOre, reserved);

            // Released back should be the Refinery portion.
            long normal;
            node.Inventory.TryGetValue("Ore:Iron", out normal);
            Assert.Equal(refineryOre, normal);

            Assert.Contains(log, l => l.Contains("INVEST_RELEASE")
                                   && l.Contains("Ore:Iron")
                                   && l.Contains("qty=" + refineryOre));
        }

        [Fact]
        public void ActiveInvestmentRequest_DoesNotReleaseReservation()
        {
            // When the request is still active the reservation should be kept intact.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;

            long oreNeed = world.Tuning.InstallCostFor(NodeCapabilityKind.Refinery, ItemKind.Ore);
            node.ReservedInventory["Ore:Iron"] = oreNeed;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Refinery,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(TwoStageRecipes());
            var log     = new List<string>();
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, log);

            long reserved;
            node.ReservedInventory.TryGetValue("Ore:Iron", out reserved);
            Assert.Equal(oreNeed, reserved);

            Assert.DoesNotContain(log, l => l.Contains("INVEST_RELEASE"));
        }

        // ---- FAC-205: chain-sized input demand ------------------------------------------------

        /// <summary>A node with one blocked Thrust recipe and a build that wants Thrust.</summary>
        private static WorldModel ChainSizingWorld(long ingotIronHeld, long thrustInBom, long thrustCollected)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = ingotIronHeld;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:Thrust"] = thrustInBom;
            if (thrustCollected > 0) build.Collected["Comp:Thrust"] = thrustCollected;
            node.BuildQueue.Add(build);
            return world;
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> ChainSizingRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "Thrust", TimeSecs = 1f,
                        Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 50 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Thrust", Amount = 1 } },
                    },
                },
            };
        }

        [Fact]
        public void InputDemand_IsSizedByTheOutstandingBom_NotTheBanksHorizon()
        {
            // FAC-205. Before this the row was machine count x throughput x horizon and bore no
            // relation to the commitment underneath it: 6 x 90 x 50 = 27,000 Ingot:Iron for a build
            // that still needs 20 Thrust, i.e. 1,000. Such a row can never be met, so its starvation
            // term pins at 1.0 and its age climbs forever.
            WorldModel world = ChainSizingWorld(ingotIronHeld: 15, thrustInBom: 100, thrustCollected: 80);

            var manager = new ProductionManager(ChainSizingRecipes(), null,
                YardOracle(world, world.Graph.Nodes["N1"], null));
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            DemandEntry iron = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            // 20 Thrust outstanding x 50 iron. Gross: the 15 on the shelf is netted downstream.
            Assert.Equal(20 * 50, iron.Quantity);

            // ...and it is classed by the build it serves, not by the hop it sits on. Posting this
            // as RecipeInput ranked the cause at half the weight of its own symptom (FAC-205).
            Assert.Equal(DemandClass.ShipBuild, iron.Class);
        }

        /// <summary>A yard's repair queue with no build at all: two hulls each short of 10 Thrust.
        /// The queue is a committed BOM like a build's, so the Thrust recipe's input is sized by, and
        /// classed as, the repairs.</summary>
        private static WorldModel RepairChainWorld(bool shipyard)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            if (shipyard) node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            node.Inventory["Ingot:Iron"] = 15;
            for (int i = 0; i < 2; i++)
            {
                var hull = new DamagedHull { PrefabId = "Tanto", Integrity = 0.5, QueuedUtc = world.UtcNow };
                hull.Bill["Comp:Thrust"] = 10;
                hull.Bill["Comp:SteelPlate"] = 5;
                hull.Collected["Comp:SteelPlate"] = 5;
                node.DamagedHulls.Add(hull);
            }
            return world;
        }

        [Fact]
        public void ARepairQueue_RootsTheChainAndOpensTheGate_LikeAnActiveBuild()
        {
            WorldModel world = RepairChainWorld(shipyard: true);
            world.Registry = NullItemRegistry.Instance;
            // The repair queue's own poster, so the oracle reads the Need: row the yard really writes.
            RepairSupply.PostDemands(world, world.Graph.Nodes["N1"], "F", TimeSpan.FromMinutes(5), null);
            var oracle = new PriceOracle(world, world.Tuning);
            Assert.True(oracle.ScarcityMultiplier("N1", "Comp:Thrust") > 1.0);

            var manager = new ProductionManager(ChainSizingRecipes(), null, oracle);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            DemandEntry iron = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");
            Assert.Equal(20 * 50, iron.Quantity);   // 2 hulls x 10 Thrust x 50 iron, gross
            Assert.Equal(DemandClass.ShipBuild, iron.Class);
        }

        /// <summary>The discriminator: a node without a Shipyard repairs nothing, so its queue is
        /// not a commitment and the same recipe stays behind the oversupply gate.</summary>
        [Fact]
        public void ARepairQueueAtAYardlessNode_IsNotACommitment()
        {
            WorldModel world = RepairChainWorld(shipyard: false);

            var manager = new ProductionManager(ChainSizingRecipes(), null,
                new FixedScarcityOracle(new Dictionary<string, double> { { "Comp:Thrust", 1.0 } }));
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Ingot:Iron");
        }

        [Fact]
        public void InputServingAStandingWant_IsClassedByThatWant_NotPromotedToTheBuildsClass()
        {
            // The discriminator for the class half, and it has to cut both ways. Inheriting the
            // root's class is only an improvement if the root is the RIGHT one: if every input at a
            // node with a build came out ShipBuild, the class would have stopped discriminating and
            // the inversion would simply have moved.
            //
            // Iron serves the build and ranks with it. Silicon serves only a catalogue want and
            // ranks with THAT -- Catalog, weight 1.0, below the RecipeInput it replaced.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 15;
            node.Inventory["Ingot:Silicon"] = 5;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Comp:Thrust"] = 100;
            node.BuildQueue.Add(build);

            var recipes = ChainSizingRecipes();
            recipes[NodeCapabilityKind.ManufactoryII].Add(new ConversionRecipe
            {
                BlueprintId = "Panel", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Silicon", Amount = 40 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SolarCell", Amount = 1 } },
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SolarCell", NodeId = "N1",
                Quantity = 500, ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Catalog,
            });

            var manager = new ProductionManager(recipes, null, YardOracle(world, node,
                new Dictionary<string, double> { { "Comp:SolarCell", 3.0 } }));
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            List<DemandEntry> posted = world.Demand.SnapshotItemDemands("F");
            DemandEntry iron    = posted.Single(d => d.Kind == "Item:Ingot:Iron"    && d.NodeId == "N1");
            DemandEntry silicon = posted.Single(d => d.Kind == "Item:Ingot:Silicon" && d.NodeId == "N1");

            Assert.Equal(DemandClass.ShipBuild, iron.Class);
            Assert.Equal(DemandClass.Catalog,   silicon.Class);
            Assert.Equal(500 * 40, silicon.Quantity);
        }

        [Fact]
        public void FactionWideWant_ReachesNoNodesChain_SoItsInputKeepsTheOldSizingAndClass()
        {
            // A want posted with no node has nowhere to net against and is not a root, so the input
            // it admits falls through to the pre-FAC-205 path unchanged. This is the one remaining
            // uncapped shape and it is deliberate -- worth pinning, because it is also the shape a
            // reader will mistake for the cap failing.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 15;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = null,
                Quantity = 400, ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Catalog,
            });

            var manager = new ProductionManager(ChainSizingRecipes(), null,
                new FixedScarcityOracle(new Dictionary<string, double> { { "Comp:Thrust", 3.0 } }));
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            DemandEntry iron = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            Assert.Equal(DemandClass.RecipeInput, iron.Class);
            Assert.Equal(400 * 50, iron.Quantity);   // the pre-existing output-demand cap, untouched
        }

        // ---- install BOMs as chain roots -------------------------------------------------------

        /// <summary>A faction whose only commitment is an outstanding Shipyard install, on
        /// <paramref name="industrialNodes"/> capability-bearing nodes plus
        /// <paramref name="bareNodes"/> founded stations with no industry at all.</summary>
        private static WorldModel InstallChainWorld(int industrialNodes, int bareNodes, long thrustInBom)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.Shipyard] =
                new Dictionary<string, long> { ["Comp:Thrust"] = thrustInBom };

            for (int i = 0; i < industrialNodes; i++)
            {
                SpatialNode node = world.Graph.Add(new Vec3(i * 10, 0, 0), 1.0, "N" + (i + 1));
                node.OwnerFactionTag = "F";
                node.HoldStrength    = 1.0;
                node.StableStationId = 40 + i;
                node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            }
            for (int i = 0; i < bareNodes; i++)
            {
                SpatialNode node = world.Graph.Add(new Vec3(0, (i + 1) * 10, 0), 1.0, "B" + (i + 1));
                node.OwnerFactionTag = "F";
                node.HoldStrength    = 1.0;
                node.StableStationId = 90 + i;
            }

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddHours(1),
            });
            return world;
        }

        /// <summary>Two ticks, because the install's own Item: demand is what admits the recipe and
        /// the demanded-key snapshot is taken before the pass that posts it — the same one-tick lag
        /// PROD_INPUT_DEMAND has.</summary>
        private static List<DemandEntry> TickTwiceForInstall(WorldModel world)
        {
            var manager = new ProductionManager(ChainSizingRecipes(), null,
                new FixedScarcityOracle(new Dictionary<string, double> { { "Comp:Thrust", 3.0 } }));
            var factions = new List<FactionState> { new FactionState { Tag = "F" } };
            manager.Tick(factions, world, world.Tuning, new List<string>());
            manager.Tick(factions, world, world.Tuning, new List<string>());
            return world.Demand.SnapshotItemDemands("F");
        }

        [Fact]
        public void InstallBom_RootsTheChain_SoItsInputIsSizedAndClassedByTheInstall()
        {
            // An InvestmentRequest names no node — InvestmentTargeting picks one at enact time — so
            // the Item: rows an install raises are faction-wide, and a faction-wide row is not a
            // chain root. Placed from the claim map instead, the install cascades and the ingot
            // under it carries Install rather than the RecipeInput its own hop would stamp.
            //
            // The CLASS is the discriminating half. At one hop the size is already bounded by the
            // pre-existing output-demand cap, which reads the same 40 from the install's own Item:
            // row — so the quantity below is a consistency check, not evidence of a chain.
            // OreUnderAnInstall_CarriesTheInstallsClass reaches the depth the cap cannot.
            WorldModel world = InstallChainWorld(industrialNodes: 1, bareNodes: 0, thrustInBom: 40);

            DemandEntry iron = TickTwiceForInstall(world)
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            Assert.Equal(40 * 50, iron.Quantity);
            Assert.Equal(DemandClass.Install, iron.Class);
        }

        [Fact]
        public void TwoIndustrialNodes_SplitTheInstallShare_RatherThanEachClaimingAllOfIt()
        {
            // The need is one need, payable from wherever the faction holds the material. Rooting the
            // whole BOM at every node would ask the network to produce it once per station.
            WorldModel world = InstallChainWorld(industrialNodes: 2, bareNodes: 0, thrustInBom: 40);

            List<DemandEntry> posted = TickTwiceForInstall(world);

            Assert.Equal(20 * 50, posted.Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1").Quantity);
            Assert.Equal(20 * 50, posted.Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N2").Quantity);
        }

        [Fact]
        public void AnOutpostWithNoIndustry_DoesNotDiluteTheInstallShare()
        {
            // The discriminator between "split across owned nodes" and "split across the nodes that
            // could actually gather it". Same faction, same BOM, one extra founded station with no
            // capability: the industrial node still owes the whole 40, not half of it. Splitting by
            // owned nodes would read 20 here and halve what the chain asks for.
            WorldModel world = InstallChainWorld(industrialNodes: 1, bareNodes: 1, thrustInBom: 40);

            DemandEntry iron = TickTwiceForInstall(world)
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            Assert.Equal(DemandClass.Install, iron.Class);
            Assert.Equal(40 * 50, iron.Quantity);
            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "B1");
        }

        [Fact]
        public void StockAlreadyGatheredForTheInstall_IsNettedOutOfWhatTheChainAsksFor()
        {
            // The root is what is still OUTSTANDING, not the whole BOM: 60 of the 100 are reserved
            // for this install on the first tick, so the chain asks for the 40 that are missing.
            // Rooting the BOM gross would ask the network to make 100 while holding 60.
            WorldModel world = InstallChainWorld(industrialNodes: 1, bareNodes: 0, thrustInBom: 100);
            world.Graph.Nodes["N1"].Inventory["Comp:Thrust"] = 60;

            DemandEntry iron = TickTwiceForInstall(world)
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            Assert.Equal(60, world.Graph.Nodes["N1"].ReservedInventory["Comp:Thrust"]);
            Assert.Equal(40 * 50, iron.Quantity);
            Assert.Equal(DemandClass.Install, iron.Class);
        }

        [Fact]
        public void AnInstallAlreadyCoveredInFull_RootsNothing()
        {
            // Self-terminating: a claim with nothing outstanding leaves the claim map, and with no
            // other commitment at the node there is no chain and no input demand at all. A root that
            // survived its own commitment would keep asking the network for material the faction is
            // already holding.
            WorldModel world = InstallChainWorld(industrialNodes: 1, bareNodes: 0, thrustInBom: 40);
            world.Graph.Nodes["N1"].Inventory["Comp:Thrust"] = 40;

            List<DemandEntry> posted = TickTwiceForInstall(world);

            Assert.Equal(40, world.Graph.Nodes["N1"].ReservedInventory["Comp:Thrust"]);
            Assert.DoesNotContain(posted, d => d.Kind == "Item:Ingot:Iron");
        }

        [Fact]
        public void OreUnderAnInstall_CarriesTheInstallsClass_NotTheHopItSitsOn()
        {
            // Two hops down, where the class actually decides something: OperationsManager sets a
            // Mine option's urgency from the ore demand's OWN score, so ore rooted in an install
            // enters the merged Haul/Mine pass at Install's weight rather than RecipeInput's.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.Shipyard] =
                new Dictionary<string, long> { ["Comp:Thrust"] = 40 };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;

            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard,
                Urgency = 1.0, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            Dictionary<NodeCapabilityKind, List<ConversionRecipe>> recipes = ChainSizingRecipes();
            recipes[NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
            {
                new ConversionRecipe
                {
                    BlueprintId = "IronIngot", TimeSecs = 1f,
                    Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                    Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                },
            };

            var manager = new ProductionManager(recipes, null, new FixedScarcityOracle(
                new Dictionary<string, double> { { "Comp:Thrust", 3.0 }, { "Ingot:Iron", 3.0 } }));
            var factions = new List<FactionState> { new FactionState { Tag = "F" } };
            // Three, because each hop's demand admits the next bank one tick later.
            for (int i = 0; i < 3; i++) manager.Tick(factions, world, world.Tuning, new List<string>());

            DemandEntry ore = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ore:Iron" && d.NodeId == "N1");

            Assert.Equal(DemandClass.Install, ore.Class);
        }

        [Fact]
        public void ACoveredBuildLine_DoesNotCapOutTheStandingWantSharingTheSameInput()
        {
            // The build's own iron line is covered by what the node holds. A catalogue want for 400
            // Thrust still admits the (blocked) Thrust recipe and still needs 20,000 iron, and the
            // chain must not cap it down to the build's satisfied line.
            //
            // This is the shape that made rooting on ship BOMs alone unsafe: with only the build as
            // a root the cap read ~10 and starved the shop asking for the same input. Standing wants
            // are roots for exactly this reason.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            node.Inventory["Ingot:Iron"] = 15;

            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", ExpiresUtc = world.UtcNow.AddHours(1) };
            build.Bom["Ingot:Iron"] = 10;          // covered by the 15 on the shelf -> requirement 0
            build.Bom["Comp:Thrust"] = 1;
            build.Collected["Comp:Thrust"] = 1;
            node.BuildQueue.Add(build);

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = "N1",
                Quantity = 400, ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Catalog,
            });

            var manager = new ProductionManager(ChainSizingRecipes(), null,
                new FixedScarcityOracle(new Dictionary<string, double> { { "Comp:Thrust", 3.0 } }));
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            DemandEntry iron = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            // Unchanged from before FAC-205: the pre-existing output-demand cap binds at
            // 400 Thrust x 50 iron. The chain's zero does not reduce it further.
            Assert.Equal(400 * 50, iron.Quantity);
        }

        [Fact]
        public void ChainSizedRow_IsNotAChainRoot_SoTheChainCannotFeedItselfAcrossCycles()
        {
            // The reason the exclusion is a provenance flag and not a class test, and the case a
            // ShipBuild-rooted version of this test CANNOT reach.
            //
            // A chain rooted in a catalogue want stamps Catalog on its own lower rows. Class alone
            // therefore cannot tell that row apart from the catalogue want that raised it, so
            // without the flag the bank's own output comes back next cycle as evidence for itself
            // and compounds. Live on v0.106.0, via the ShipBuild path: N007's Ingot:Iron climbed
            // 32,894 -> 76,752 -> 120,610 -> 142,634 against a BOM requiring 40,610, stopping only
            // at the machine appetite that caps it.
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;

            // No build queue at all: the ONLY root is the catalogue want, so any growth across
            // cycles can only have come from the chain re-reading its own output.
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = "N1",
                Quantity = 300, ExpiresUtc = world.UtcNow.AddHours(1), Class = DemandClass.Catalog,
            });

            var manager = new ProductionManager(ChainSizingRecipes(), null,
                new FixedScarcityOracle(new Dictionary<string, double> { { "Comp:Thrust", 3.0 } }));
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            manager.Tick(states, world, world.Tuning, new List<string>());
            DemandEntry first = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            Assert.True(first.ChainSized, "the row must be marked as this chain's own output");
            Assert.Equal(DemandClass.Catalog, first.Class);   // inherited, so class cannot exclude it
            Assert.Equal(300 * 50, first.Quantity);

            // Second pass over unchanged state: the same answer, not a bigger one.
            world.UtcNow = world.UtcNow.AddMinutes(1);
            manager.Tick(states, world, world.Tuning, new List<string>());
            DemandEntry second = world.Demand.SnapshotItemDemands("F")
                .Single(d => d.Kind == "Item:Ingot:Iron" && d.NodeId == "N1");

            Assert.Equal(first.Quantity, second.Quantity);
        }


        // ---- FAC-352: a joint-product recipe is not a source of every key it emits -------------

        /// <summary>Refinery holding the live stone/iron pair. `StoneOreToIngot` emits gravel AND
        /// iron from 1,000 stone; `IronOreToIngot` emits iron alone from 1 iron ore. No ore on hand,
        /// so both recipes are blocked and both post input demand -- the live N007 state.</summary>
        private static (WorldModel, ProductionManager, List<FactionState>, List<string>) StoneIronWorld(
            long ironIngotDemand, long stoneIngotDemand, bool withIronRoute = true)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;

            void Want(string ingot, long qty)
            {
                if (qty <= 0) return;
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:" + ingot, NodeId = "N1",
                    Quantity = qty, ExpiresUtc = world.UtcNow.AddMinutes(5),
                });
            }
            Want("Ingot:Iron", ironIngotDemand);
            Want("Ingot:Stone", stoneIngotDemand);

            var stone = new ConversionRecipe
            {
                BlueprintId = "StoneOreToIngot",
                TimeSecs = 10f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Stone", Amount = 1000 } },
                Outputs = new[]
                {
                    new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Stone", Amount = 14 },
                    new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron",  Amount = 30 },
                },
            };
            var iron = new ConversionRecipe
            {
                BlueprintId = "IronOreToIngot",
                TimeSecs = 0.05f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 0.7f } },
            };

            var ordered = new List<ConversionRecipe> { stone };
            if (withIronRoute) ordered.Add(iron);
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = ordered,
            };

            var mgr = new ProductionManager(capRecipes, null, null);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            return (world, mgr, states, new List<string>());
        }

        private static long OreDemand(WorldModel world, string oreKey)
        {
            long total = 0;
            foreach (DemandEntry d in world.Demand.SnapshotItemDemands("F"))
                if (d.Kind == "Item:" + oreKey) total += d.Quantity;
            return total;
        }

        [Fact]
        public void IronWantedAndStoneIsNotItsRoute_PostsNoStoneOreDemand()
        {
            // Live SPRT/N007: Ore:Stone need=631,000 beside Ore:Iron need=3,131 for ONE iron want.
            // StoneOreToIngot yields 30 Ingot:Iron per 1,000 Ore:Stone against iron ore's 700, and
            // BatchesToSatisfyOutputDemand's MIN landed on iron because gravel was not short -- so
            // the stone row was sized to make every wanted iron ingot out of stone.
            var (world, mgr, states, log) = StoneIronWorld(ironIngotDemand: 20000, stoneIngotDemand: 0);

            mgr.Tick(states, world, world.Tuning, log);

            Assert.Equal(0, OreDemand(world, "Ore:Stone"));
            Assert.True(OreDemand(world, "Ore:Iron") > 0,
                "iron ore is the route for Ingot:Iron and must still be demanded");
        }

        [Fact]
        public void GravelWanted_StillSizesTheStoneOrderItIsTheRouteFor()
        {
            // The other direction, and the one that makes the filter falsifiable: StoneOreToIngot IS
            // the only route to Ingot:Stone, so a gravel want must still reach Ore:Stone. A fix that
            // merely silenced the recipe would pass the test above and fail this one.
            var (world, mgr, states, log) = StoneIronWorld(ironIngotDemand: 0, stoneIngotDemand: 140);

            mgr.Tick(states, world, world.Tuning, log);

            Assert.True(OreDemand(world, "Ore:Stone") > 0,
                "gravel is stone's own product and must still size a stone order");
        }

        [Fact]
        public void SoleProducerKeepsItsOutput_EvenAsAJointProduct()
        {
            // An unrouted key has no better claimant. With no IronOreToIngot in the set, the joint
            // recipe is all there is for Ingot:Iron and must keep asking for stone -- otherwise the
            // filter starves a faction whose only route to a key happens to be a joint product.
            var (world, mgr, states, log) =
                StoneIronWorld(ironIngotDemand: 20000, stoneIngotDemand: 0, withIronRoute: false);

            mgr.Tick(states, world, world.Tuning, log);

            Assert.True(OreDemand(world, "Ore:Stone") > 0,
                "stone is the only route to iron here and must still be demanded");
        }

    }
}
