using System;
using System.Collections.Generic;
using System.IO;
using Factions;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Food slice 1 (docs/food-and-population.md): Farm and FoodProcessor as capabilities. The rule
    /// that carries the design is per node — a crop grows where it is native or where its seed is
    /// held, and a player faction always needs the seed — so most of these pin that rule, first on
    /// its own and then through a real production tick.
    /// </summary>
    public sealed class FoodChainTests
    {
        private const string FruitRecipeId = "Farm/LargeBlockFarmPlot/Fruit";

        // ---- the per-node rule ------------------------------------------------------------------

        [Fact]
        public void ANativeCrop_GrowsWithNoSeed_AndANonNativeOneDoesNot()
        {
            string[] tellus = { "Fruit", "Grain", "Mushrooms", "Vegetables" };
            string[] agni = { "Mushrooms" };

            Assert.True(FarmCrops.Grows("Fruit", tellus, seedsOnly: false, seedsHeld: 0));
            Assert.True(FarmCrops.Grows("Mushrooms", agni, seedsOnly: false, seedsHeld: 0));
            Assert.False(FarmCrops.Grows("Vegetables", agni, seedsOnly: false, seedsHeld: 0));
            // Kor, and every deep-space node: nothing grows wild.
            Assert.False(FarmCrops.Grows("Mushrooms", null, seedsOnly: false, seedsHeld: 0));
        }

        [Fact]
        public void AHeldSeed_GrowsAnyCrop_AndAPlayerFactionNeedsOneEvenForANativeCrop()
        {
            string[] tellus = { "Fruit" };

            Assert.True(FarmCrops.Grows("Vegetables", null, seedsOnly: false, seedsHeld: 1));
            Assert.False(FarmCrops.Grows("Fruit", tellus, seedsOnly: true, seedsHeld: 0));
            Assert.True(FarmCrops.Grows("Fruit", tellus, seedsOnly: true, seedsHeld: 1));
        }

        [Fact]
        public void TheSeed_IsReadBackOutOfTheRecipeId()
        {
            Assert.Equal("Fruit", FarmCrops.SeedOf(new ConversionRecipe { BlueprintId = FruitRecipeId }));
            Assert.Null(FarmCrops.SeedOf(new ConversionRecipe { BlueprintId = "IronOreToIngot" }));
            Assert.Null(FarmCrops.SeedOf(new ConversionRecipe { BlueprintId = "Farm/" }));
        }

        [Fact]
        public void GrowableAt_DropsOnlyFarmRecipes_AndReturnsTheSameListWhenNothingIsDropped()
        {
            var fruit = FarmRecipe("Fruit");
            var veg = FarmRecipe("Vegetables");
            var other = new ConversionRecipe { BlueprintId = "MealPack_FruitBar" };
            var all = new List<ConversionRecipe> { fruit, other, veg };

            List<ConversionRecipe> agni = FarmCrops.GrowableAt(all, null, new[] { "Fruit" }, false, k => 0);
            Assert.Equal(new[] { fruit, other }, agni.ToArray());

            List<ConversionRecipe> tellus = FarmCrops.GrowableAt(all, null,
                new[] { "Fruit", "Vegetables" }, false, k => 0);
            Assert.Same(all, tellus);

            // The seed lookup is by the Seed: key, not the crop's bare one.
            List<ConversionRecipe> seeded = FarmCrops.GrowableAt(all, null, null, false,
                k => k == "Seed:Vegetables" ? 1 : 0);
            Assert.Equal(new[] { other, veg }, seeded.ToArray());
        }

        // ---- through a real production tick -----------------------------------------------------

        [Fact]
        public void AFarmOnABodyWhereFruitIsNative_GrowsFruitFromIce()
        {
            SpatialNode node;
            Tick(native: new[] { "Fruit" }, seed: 0, player: false, out node);

            Assert.True(Stock(node, "Fruit") > 0, "a native crop should grow with no seed held");
            Assert.True(IceHeld(node) < 1000, "the farm should have drawn ice");
        }

        [Fact]
        public void AFarmWhereFruitIsNotNative_GrowsNothing_UntilASeedIsHeld()
        {
            SpatialNode bare;
            Tick(native: new[] { "Mushrooms" }, seed: 0, player: false, out bare);
            Assert.Equal(0, Stock(bare, "Fruit"));
            Assert.Equal(1000, IceHeld(bare));

            SpatialNode seeded;
            Tick(native: new[] { "Mushrooms" }, seed: 1, player: false, out seeded);
            Assert.True(Stock(seeded, "Fruit") > 0);
            // The farm keeps its own replant seed: the one that unlocked the crop is not spent.
            Assert.Equal(1, Stock(seeded, "Seed:Fruit"));
        }

        [Fact]
        public void APlayerFactionsFarm_NeedsTheSeed_EvenWhereTheCropIsNative()
        {
            SpatialNode node;
            Tick(native: new[] { "Fruit" }, seed: 0, player: true, out node);
            Assert.Equal(0, Stock(node, "Fruit"));
        }

        // ---- prerequisites and investment -------------------------------------------------------

        [Fact]
        public void AFoodProcessor_RequiresAFarm()
        {
            Assert.Equal(NodeCapabilityKind.Farm, CapabilityPrerequisites.For(NodeCapabilityKind.FoodProcessor));
            Assert.Null(CapabilityPrerequisites.For(NodeCapabilityKind.Farm));
        }

        [Fact]
        public void ADemandGap_AsksForTheLowestMissingLinkOfTheChain()
        {
            // A meal demand with nothing installed: the FoodProcessor cannot be targeted without a
            // Farm, so the Farm is what must be asked for.
            Assert.Equal(NodeCapabilityKind.Farm, ProductionManager.FirstMissingPrerequisite(
                NodeCapabilityKind.FoodProcessor, new HashSet<NodeCapabilityKind>()));
            Assert.Equal(NodeCapabilityKind.FoodProcessor, ProductionManager.FirstMissingPrerequisite(
                NodeCapabilityKind.FoodProcessor, new HashSet<NodeCapabilityKind> { NodeCapabilityKind.Farm }));
            // Two links deep: PrototechForge -> ManufactoryII -> Manufactory -> Refinery.
            Assert.Equal(NodeCapabilityKind.Refinery, ProductionManager.FirstMissingPrerequisite(
                NodeCapabilityKind.PrototechForge, new HashSet<NodeCapabilityKind>()));
        }

        [Fact]
        public void FarmAndFoodProcessor_AreDemandDrivenOnly()
        {
            // Operator, 2026-10-14: they install when something wants food, not on speculation.
            Assert.DoesNotContain(NodeCapabilityKind.Farm, FactionTuning.AllCapabilities);
            Assert.DoesNotContain(NodeCapabilityKind.FoodProcessor, FactionTuning.AllCapabilities);
        }

        [Fact]
        public void InstalledCounts_CoversEveryOrdinal()
        {
            // Sized by AllCapabilities it was eight long, so PrototechRefinery (8) and both food kinds
            // were never counted, and a FoodProcessor's prerequisite lookup read past the end.
            var node = new SpatialNode { Id = "N1" };
            node.Capabilities[NodeCapabilityKind.PrototechRefinery] = 1;
            node.Capabilities[NodeCapabilityKind.Farm] = 1;
            node.Capabilities[NodeCapabilityKind.FoodProcessor] = 2;

            int[] installed = InvestmentTargeting.InstalledCounts(new[] { node });

            Assert.Equal(1, installed[(int)NodeCapabilityKind.PrototechRefinery]);
            Assert.Equal(1, installed[(int)NodeCapabilityKind.Farm]);
            Assert.Equal(1, installed[(int)NodeCapabilityKind.FoodProcessor]);
        }

        // ---- siting -----------------------------------------------------------------------------

        [Fact]
        public void AFarm_IsSitedWhereTheMostCropsGrow()
        {
            FakeWorld w = SitingWorld();
            Station(w, "KOR", "P-Kor");
            Station(w, "AGNI", "P-Agni");
            Station(w, "TELLUS", "P-Tellus");

            InvestmentTarget target = SiteFarm(w);

            Assert.Equal("TELLUS", target.NodeId);
        }

        [Fact]
        public void AFarm_PrefersFreeSeeds_ButCanStandAnywhere()
        {
            // Operator, 2026-10-14: a farm can produce any crop; the native crop is a preference and a
            // free seed.
            FakeWorld w = SitingWorld();
            Station(w, "KOR", "P-Kor");
            Station(w, "AGNI", "P-Agni");
            Assert.Equal("AGNI", SiteFarm(w).NodeId);

            FakeWorld korOnly = SitingWorld();
            Station(korOnly, "KOR", "P-Kor");
            Assert.Equal("KOR", SiteFarm(korOnly).NodeId);
        }

        [Fact]
        public void SitingPreference_RanksByFreeSeeds()
        {
            FakeWorld w = SitingWorld();
            SpatialNode tellus = Station(w, "TELLUS", "P-Tellus");
            SpatialNode agni = Station(w, "AGNI", "P-Agni");
            SpatialNode kor = Station(w, "KOR", "P-Kor");

            Assert.Equal(1.0, FarmCrops.SitingPreference(tellus, w.Planets, null), 6);
            Assert.Equal(0.4, FarmCrops.SitingPreference(agni, w.Planets, null), 6);
            Assert.Equal(0.2, FarmCrops.SitingPreference(kor, w.Planets, null), 6);
        }

        [Fact]
        public void AHeldSeed_RaisesANodesPreference()
        {
            FakeWorld w = SitingWorld();
            Station(w, "KOR", "P-Kor");
            SpatialNode seeded = Station(w, "KOR2", "P-Kor");
            seeded.Inventory["Seed:Mushrooms"] = 1;

            Assert.Equal("KOR2", SiteFarm(w).NodeId);
        }

        [Fact]
        public void AMealDemand_AsksForAFarm_EvenWhereNothingIsNative()
        {
            List<string> tellus = MealDemandLog(new[] { "Fruit", "Grain", "Mushrooms", "Vegetables" }, ticks: 1);
            Assert.Contains(tellus, l => l.Contains("F INVEST_REQUEST Farm urgency=0.90 for=MealPack_FruitBar"));

            List<string> kor = MealDemandLog(null, ticks: 1);
            Assert.Contains(kor, l => l.Contains("F INVEST_REQUEST Farm urgency=0.90 for=MealPack_FruitBar"));
        }

        [Fact]
        public void AFarmThatLacksACrop_AsksForItsSeed_NotAnotherFarm()
        {
            // A Farm on Agni grows mushrooms. A grain demand used to read as covered by it; what the
            // Farm lacks is one grain seed.
            WorldModel world;
            List<string> log = CropDemandLog(farmOn: "AGNI", ticks: 3, world: out world, bodies: new[] { "AGNI", "KOR" });
            Assert.Single(log.FindAll(l => l.Contains("F FARM_SEED_DEMAND Seed:Grain node=AGNI for=Grain")));
            Assert.DoesNotContain(log, l => l.Contains("INVEST_REQUEST Farm"));
            DemandEntry seed;
            Assert.True(world.Demand.TryGet("F", "Item:Seed:Grain", "AGNI", null, out seed));
            Assert.Equal(1, seed.Quantity);

            // ...and a Farm that does grow it covers it.
            List<string> tellus = CropDemandLog(farmOn: "TELLUS", ticks: 1, world: out world, bodies: new[] { "AGNI", "TELLUS" });
            Assert.DoesNotContain(tellus, l => l.Contains("FARM_SEED_DEMAND"));
        }

        [Fact]
        public void AFarmForACrop_PrefersWhereThatCropIsFree()
        {
            // Agni grows one crop free and so does a seeded Kor, but only Kor's is grain.
            FakeWorld w = SitingWorld();
            Station(w, "AGNI", "P-Agni");
            SpatialNode kor = Station(w, "KOR", "P-Kor");
            kor.Inventory["Seed:Grain"] = 1;
            Assert.Equal("KOR", SiteFarm(w, "Grain").NodeId);

            // Where several grow it free, the most crops still wins.
            Station(w, "TELLUS", "P-Tellus");
            Assert.Equal("TELLUS", SiteFarm(w, "Grain").NodeId);

            // Where none does, the Farm still goes somewhere and fetches the seed.
            FakeWorld agniOnly = SitingWorld();
            Station(agniOnly, "AGNI", "P-Agni");
            Assert.Equal("AGNI", SiteFarm(agniOnly, "Grain").NodeId);
        }

        // ---- machines, keys, badge --------------------------------------------------------------

        [Fact]
        public void AFarm_GrowsAtSesRate_WithNoUpgradeModuleBonus()
        {
            FactionTuning t = TestSupport.World().Tuning;
            t.ProductionProductivityBonus = 4.0;
            MachineModel[] models = MachinePhysics.ResolveAll(t);

            // Six slots at 5.00 stand for the 30 plots one irrigation system waters.
            Assert.Equal(6, models[(int)NodeCapabilityKind.Farm].MachineCount);
            Assert.Equal(5.0, models[(int)NodeCapabilityKind.Farm].Speed, 6);
            // The FoodProcessor is an assembler and does take the bonus.
            Assert.Equal(5.0, models[(int)NodeCapabilityKind.FoodProcessor].Speed, 6);
        }

        [Fact]
        public void ASeed_IsKeyedApartFromItsCrop()
        {
            Assert.Equal("Seed:Fruit", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_SeedItem", "Fruit"));
            Assert.Equal("Fruit", InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_ConsumableItem", "Fruit"));
            Assert.Equal("MyObjectBuilder_SeedItem/Fruit", ItemPriceResolver.ExactKeyFromInventoryKey("Seed:Fruit"));
            Assert.True(NodeTransfer.IsDepositable("Seed:Fruit"));
            Assert.Contains("Seed:", ShipAssetRules.AcceptedCargoPrefixes);

            ItemKind kind;
            Assert.True(NullItemRegistry.Instance.TryGetItemKind("Seed:Grain", out kind));
            Assert.Equal(ItemKind.ProcessedGood, kind);
            Assert.True(NullItemRegistry.Instance.TryGetItemKind("MealPack_VeggieBurger", out kind));
            Assert.Equal(ItemKind.ProcessedGood, kind);
        }

        [Fact]
        public void TheFoodKinds_Badge_AboveTheOldByte()
        {
            var node = new SpatialNode { Id = "N1" };
            node.Capabilities[NodeCapabilityKind.Farm] = 1;
            node.Capabilities[NodeCapabilityKind.FoodProcessor] = 1;

            Assert.Equal((ushort)((1 << 9) | (1 << 10)), NodeDisplay.CapabilityMask(node));
        }

        // ---- authored crops ---------------------------------------------------------------------

        [Fact]
        public void TheGenesisWorld_AuthorsWhatGrowsWild()
        {
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")), out error);
            Assert.True(recipe != null, error);

            SurfaceNodePolicy policy;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(recipe, out policy, out why), why);

            Assert.Equal(new[] { "Fruit", "Grain", "Mushrooms", "Vegetables" }, policy.NativeCropsFor("Tellus"));
            Assert.Equal(new[] { "Mushrooms" }, policy.NativeCropsFor("Agni"));
            // Kor authors an empty list: nothing grows wild, which reads the same as no list.
            Assert.Null(policy.NativeCropsFor("Kor"));

            var record = new PlanetRecord { Id = "1", Generator = "Agni" };
            policy.ApplyTo(record, "Agni", true);
            Assert.Equal(new[] { "Mushrooms" }, record.NativeCrops);
        }

        [Fact]
        public void TwoBodiesOfOneGenerator_MustAgreeOnTheirCrops()
        {
            var recipe = new WorldRecipe();
            recipe.Planets.Add(new RecipePlanet { Name = "A", Definition = "Tellus", NativeCrops = new List<string> { "Fruit" } });
            recipe.Planets.Add(new RecipePlanet { Name = "B", Definition = "Tellus", NativeCrops = new List<string> { "Grain" } });

            SurfaceNodePolicy policy;
            string why;
            Assert.False(SurfaceNodePolicy.TryBuild(recipe, out policy, out why));
            Assert.Contains("NativeCrops", why, StringComparison.Ordinal);
        }

        // ---- config -----------------------------------------------------------------------------

        [Fact]
        public void TheDefaults_WireBothCapabilitiesToTheirSeBlocks()
        {
            CapabilityRecipesConfig cfg = CapabilityRecipesConfig.Defaults();

            Assert.Equal(new[] { "FoodProcessorRations", "FoodProcessorSeeds" }, cfg.Capabilities["FoodProcessor"]["1"].ToArray());
            Assert.Equal(new[] { "LargeBlockFarmPlot" }, cfg.FarmBlocks["Farm"].ToArray());
            Assert.Equal("IrrigationSystem", cfg.InstallBlocks["Farm"]);
            Assert.Equal("FoodProcessor", cfg.InstallBlocks["FoodProcessor"]);
            Assert.Equal("FoodProcessor", CapabilityRecipesConfig.DefaultMachineBlock("FoodProcessor"));
        }

        [Fact]
        public void MergeDefaults_BackfillsTheFoodChainIntoAFileThatPredatesIt()
        {
            var old = new CapabilityRecipesConfig();
            old.FarmBlocks = null;   // a file written before the key existed deserialises it as null

            List<string> added = CapabilityRecipesConfig.MergeDefaults(old);

            Assert.Contains("Capabilities.FoodProcessor", added);
            Assert.Contains("FarmBlocks.Farm", added);
            Assert.Contains("InstallBlocks.Farm", added);
        }

        [Fact]
        public void AFarmLevel_IsChargedItsThirtyPlots()
        {
            CapabilityRecipesConfig cfg = CapabilityRecipesConfig.Defaults();

            Assert.Equal(30, cfg.InstallExtraBlocks["Farm"]["LargeBlockFarmPlot"]);
            Assert.False(cfg.InstallExtraBlocks.ContainsKey("FoodProcessor"));
        }

        [Fact]
        public void MergeDefaults_BackfillsThePlotsIntoAFileThatPredatesThem()
        {
            var old = CapabilityRecipesConfig.Defaults();
            old.InstallExtraBlocks = null;

            List<string> added = CapabilityRecipesConfig.MergeDefaults(old);

            // Every capability with extra blocks comes back, the Habitat's beds (FAC-493) beside the plots.
            Assert.Equal(new[] { "InstallExtraBlocks.Farm", "InstallExtraBlocks.Habitat" }, added.ToArray());
            Assert.Equal(30, old.InstallExtraBlocks["Farm"]["LargeBlockFarmPlot"]);
            Assert.Equal(20, old.InstallExtraBlocks["Habitat"]["LargeBlockBed"]);
        }

        [Fact]
        public void TheFarmBom_IsTheIrrigationSystemPlusThirtyPlots_EveryStageCounted()
        {
            // SE's own construction lists (CubeBlocks_Production.sbc): both name a component in two
            // stages, and a stage dropped would under-charge by that stage.
            var irrigation = new List<KeyValuePair<string, int>>
            {
                Pair("SteelPlate", 80), Pair("Construction", 20), Pair("LargeTube", 10),
                Pair("Motor", 6), Pair("Computer", 5), Pair("SteelPlate", 20),
            };
            var plot = new List<KeyValuePair<string, int>>
            {
                Pair("InteriorPlate", 20), Pair("Construction", 20), Pair("SmallTube", 10),
                Pair("Construction", 20), Pair("InteriorPlate", 20),
            };

            var bom = new Dictionary<string, long>();
            InstallBomBuilder.Add(bom, irrigation, 1);
            InstallBomBuilder.Add(bom, plot, 30);

            Assert.Equal(100,  bom["Comp:SteelPlate"]);
            Assert.Equal(1220, bom["Comp:Construction"]);
            Assert.Equal(10,   bom["Comp:LargeTube"]);
            Assert.Equal(6,    bom["Comp:Motor"]);
            Assert.Equal(5,    bom["Comp:Computer"]);
            Assert.Equal(1200, bom["Comp:InteriorPlate"]);
            Assert.Equal(300,  bom["Comp:SmallTube"]);
            Assert.Equal(7, bom.Count);
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static KeyValuePair<string, int> Pair(string subtype, int count)
        {
            return new KeyValuePair<string, int>(subtype, count);
        }

        private static ConversionRecipe FarmRecipe(string seed)
        {
            return new ConversionRecipe
            {
                BlueprintId = "Farm/LargeBlockFarmPlot/" + seed,
                // One second rather than SE's 35 minutes, so a single tick produces something.
                TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = 10 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_ConsumableItem", SubTypeId = seed, Amount = 1.5 } },
            };
        }

        private static void Tick(string[] native, long seed, bool player, out SpatialNode node)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            world.Planets = new OneBody(new PlanetRecord { Id = "P1", Generator = "Test", NativeCrops = native });
            node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.PlanetId = "P1";
            node.Capabilities[NodeCapabilityKind.Farm] = 1;
            node.Inventory["Ore:Ice"] = 1000;
            if (seed > 0) node.Inventory["Seed:Fruit"] = seed;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Fruit", NodeId = "N1",
                Quantity = 100000, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var recipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Farm, new List<ConversionRecipe> { FarmRecipe("Fruit") } },
            };
            var manager = new ProductionManager(recipes, null, new ScarceOracle());
            var states = new List<FactionState> { new FactionState { Tag = "F", IsPlayerFaction = player } };
            var log = new List<string>();
            // Two ticks: the first stamps the slots' clocks, the second accrues machine time.
            manager.Tick(states, world, world.Tuning, log);
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, log);
        }

        /// <summary>Ice on hand plus ice reserved. Ore held with no Refinery anywhere makes the faction
        /// reserve some toward installing one, which moves it out of Inventory without anything
        /// having consumed it -- only the sum says whether the farm drew any.</summary>
        private static long IceHeld(SpatialNode node)
        {
            long reserved;
            node.ReservedInventory.TryGetValue("Ore:Ice", out reserved);
            return Stock(node, "Ore:Ice") + reserved;
        }

        private static List<string> MealDemandLog(string[] native, int ticks)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            world.Planets = new OneBody(new PlanetRecord { Id = "P1", Generator = "Test", NativeCrops = native });
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.PlanetId = "P1";
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:MealPack_FruitBar", NodeId = "N1",
                Quantity = 10, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var fruitBar = new ConversionRecipe
            {
                BlueprintId = "FruitBar", TimeSecs = 8f,
                Inputs = new[]
                {
                    new RecipeItem { TypeId = "MyObjectBuilder_ConsumableItem", SubTypeId = "Fruit", Amount = 1 },
                    new RecipeItem { TypeId = "MyObjectBuilder_PhysicalObject", SubTypeId = "Grain", Amount = 1 },
                },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_ConsumableItem", SubTypeId = "MealPack_FruitBar", Amount = 1 } },
            };
            var recipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Farm, new List<ConversionRecipe> { FarmRecipe("Fruit"), FarmRecipe("Grain") } },
                { NodeCapabilityKind.FoodProcessor, new List<ConversionRecipe> { fruitBar } },
            };
            var manager = new ProductionManager(recipes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();
            for (int i = 0; i < ticks; i++)
            {
                manager.Tick(states, world, world.Tuning, log);
                world.UtcNow = world.UtcNow.AddSeconds(30);
            }
            return log;
        }

        private static FakeWorld SitingWorld()
        {
            var w = new FakeWorld();
            w.Planets = new Bodies(
                new PlanetRecord { Id = "P-Tellus", Generator = "Tellus", NativeCrops = new[] { "Fruit", "Grain", "Mushrooms", "Vegetables" } },
                new PlanetRecord { Id = "P-Agni", Generator = "Agni", NativeCrops = new[] { "Mushrooms" } },
                new PlanetRecord { Id = "P-Kor", Generator = "Kor" });
            return w;
        }

        private static SpatialNode Station(FakeWorld w, string id, string planetId)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = 42;
            n.PlanetId = planetId;
            n.Zone = NodeZone.Surface;
            n.Site = StationSite.Surface;
            return n;
        }

        private static InvestmentTarget SiteFarm(FakeWorld w, string crop = null)
        {
            return InvestmentTargeting.Resolve(w, "F", new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Farm, Urgency = 0.9,
                ExpiresUtc = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc),
                Crop = crop,
            }, TestSupport.Tuning());
        }

        /// <summary>A grain demand at a faction with a Farm already installed on one of
        /// <paramref name="bodies"/> (node ids AGNI, TELLUS, KOR, each on the body it names).</summary>
        private static List<string> CropDemandLog(string farmOn, int ticks, out WorldModel world, string[] bodies)
        {
            world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            world.Planets = new Bodies(
                new PlanetRecord { Id = "P-TELLUS", Generator = "Tellus", NativeCrops = new[] { "Fruit", "Grain", "Mushrooms", "Vegetables" } },
                new PlanetRecord { Id = "P-AGNI", Generator = "Agni", NativeCrops = new[] { "Mushrooms" } },
                new PlanetRecord { Id = "P-KOR", Generator = "Kor" });
            for (int i = 0; i < bodies.Length; i++)
            {
                SpatialNode n = world.Graph.Add(new Vec3(i * 100, 0, 0), 1.0, bodies[i]);
                n.OwnerFactionTag = "F";
                n.HoldStrength = 1.0;
                n.StableStationId = 1 + i;
                n.PlanetId = "P-" + bodies[i];
                if (bodies[i] == farmOn) n.Capabilities[NodeCapabilityKind.Farm] = 1;
            }
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Grain", NodeId = farmOn,
                Quantity = 10, ExpiresUtc = world.UtcNow.AddHours(1),
            });

            var recipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Farm, new List<ConversionRecipe> { FarmRecipe("Mushrooms"), FarmRecipe("Grain") } },
            };
            var manager = new ProductionManager(recipes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();
            for (int i = 0; i < ticks; i++)
            {
                manager.Tick(states, world, world.Tuning, log);
                world.UtcNow = world.UtcNow.AddSeconds(30);
            }
            return log;
        }

        private sealed class Bodies : IPlanetCatalogue
        {
            private readonly PlanetRecord[] _bodies;
            internal Bodies(params PlanetRecord[] bodies) { _bodies = bodies; }
            public IEnumerable<PlanetRecord> Planets { get { return _bodies; } }
            public double GravityAt(Vec3 posKm) { return 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static long Stock(SpatialNode node, string key)
        {
            long v;
            return node.Inventory.TryGetValue(key, out v) ? v : 0;
        }

        /// <summary>Every output reads as wanted, so the scarcity gate is out of the way and what is
        /// left to decide whether the farm runs is the per-node crop rule.</summary>
        private sealed class ScarceOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return 2.0; }
            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference) { reference = 100; return true; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference) { reference = 100; return true; }
        }

        private sealed class OneBody : IPlanetCatalogue
        {
            private readonly PlanetRecord _body;
            internal OneBody(PlanetRecord body) { _body = body; }
            public IEnumerable<PlanetRecord> Planets { get { yield return _body; } }
            public double GravityAt(Vec3 posKm) { return 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }
    }
}
