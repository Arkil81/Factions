using System;
using System.Collections.Generic;
using Factions;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The Factorum's production tier. Every Factorum hull in the catalogue needs prototech
    /// components, no vanilla assembler carries the <c>PrototechComponents</c> blueprint class, and
    /// SE defines no producer at all for two of the materials that class consumes. These cover the
    /// three halves of closing that: the tier exists and only it holds the class, the synthetic
    /// recipes supply what SE does not, and neither of them displaces a cheaper vanilla machine.
    /// </summary>
    public sealed class PrototechTierTests
    {
        private const string ScrapKey = "Ingot:PrototechScrap";
        private const string FrameKey = "Comp:PrototechFrame";

        // ---- the class lives on one capability, and it is not a vanilla assembler ---------------

        [Fact]
        public void PrototechComponents_IsCarriedByTheForgeAndByNothingElse()
        {
            CapabilityRecipesConfig cfg = CapabilityRecipesConfig.Defaults();

            Assert.Contains("PrototechComponents", cfg.Capabilities["PrototechForge"]["1"]);
            foreach (var entry in cfg.Capabilities)
                foreach (var level in entry.Value)
                    if (entry.Key != "PrototechForge")
                        Assert.DoesNotContain("PrototechComponents", level.Value);
        }

        [Fact]
        public void PrototechMachineBlocks_AreTheSeBlocksThatActuallyCarryThose()
        {
            CapabilityRecipesConfig cfg = CapabilityRecipesConfig.Defaults();

            Assert.Equal("LargePrototechAssembler", cfg.MachineBlocks["PrototechForge"]);
            Assert.Equal("LargePrototechRefinery",  cfg.MachineBlocks["PrototechRefinery"]);
            // Same answer through the lookup-time fallback, for an instance whose file predates the
            // key -- the file is only seeded when absent.
            Assert.Equal("LargePrototechAssembler",
                CapabilityRecipesConfig.DefaultMachineBlock("PrototechForge"));
            Assert.Equal("LargePrototechRefinery",
                CapabilityRecipesConfig.DefaultMachineBlock("PrototechRefinery"));
        }

        [Fact]
        public void PrototechRefinery_DrawsTheSameIngotsClassAsRefineryII()
        {
            // It buys machine physics, not recipes: RefineSpeed 5.5 / MaterialEfficiency 2.0 against
            // LargeRefinery's 1.3 / 1.0. A different class list here would silently be a content
            // change rather than a throughput one.
            CapabilityRecipesConfig cfg = CapabilityRecipesConfig.Defaults();
            Assert.Equal(cfg.Capabilities["RefineryII"]["1"], cfg.Capabilities["PrototechRefinery"]["1"]);
        }

        [Fact]
        public void MergeDefaults_BackfillsTheTierIntoAFileThatPredatesIt()
        {
            // The live capability_recipes.json is only written when absent, so this is the path the
            // running server actually takes -- not a fresh Defaults().
            var old = new CapabilityRecipesConfig();
            old.Capabilities["Refinery"] = new Dictionary<string, List<string>>
            {
                { "1", new List<string> { "CommonMetals" } },
            };

            List<string> added = CapabilityRecipesConfig.MergeDefaults(old);

            Assert.Contains("Capabilities.PrototechForge", added);
            Assert.Contains("Capabilities.PrototechRefinery", added);
            Assert.Contains("SyntheticRecipes.PrototechForge", added);
            Assert.Contains("MachineBlocks.PrototechForge", added);
            Assert.Contains("InstallBlocks.PrototechForge", added);
            Assert.Contains("InstallBlocks.PrototechRefinery", added);
            Assert.Equal(2, old.SyntheticRecipesFor("PrototechForge").Count);
        }

        [Fact]
        public void AForgeBom_LosesOnlyThePartsNothingButAForgeCanMake()
        {
            // LargePrototechAssembler is 1 PrototechFrame, 240 PrototechPanel, 20 PrototechMachinery
            // and 2 PrototechCoolingUnit -- all forge output -- plus ordinary Construction, MetalGrid,
            // Computer and Display. Charged verbatim the first forge can never be built and the tier
            // is unreachable, silently. Stripped to nothing it would be free. The ordinary half is
            // both payable and a real price, so that is exactly what must survive.
            var bom = new Dictionary<string, long>
            {
                { "Comp:PrototechFrame", 1 }, { "Comp:PrototechPanel", 240 },
                { "Comp:PrototechMachinery", 20 }, { "Comp:PrototechCoolingUnit", 2 },
                { "Comp:Construction", 130 }, { "Comp:MetalGrid", 80 },
                { "Comp:Computer", 200 }, { "Comp:Display", 10 },
            };

            List<string> dropped = InstallBomSelfReference.StripSelfProduced(
                NodeCapabilityKind.PrototechForge, bom, TwoTierRecipes());

            Assert.Equal(new[] { "Comp:PrototechCoolingUnit", "Comp:PrototechFrame",
                                 "Comp:PrototechMachinery", "Comp:PrototechPanel" }, dropped.ToArray());
            Assert.Equal(4, bom.Count);
            Assert.Equal(130, bom["Comp:Construction"]);
            Assert.Equal(200, bom["Comp:Computer"]);
        }

        [Fact]
        public void APrototechRefineryBom_KeepsItsPrototechParts_BecauseAForgeMakesThem()
        {
            // The gate, and the half of the rule that must NOT fire. A component another capability
            // can make stays on the BOM however scarce it is: that is what makes "you cannot buy a
            // prototech machine without a forge" real rather than incidental.
            var bom = new Dictionary<string, long>
            {
                { "Comp:PrototechFrame", 1 }, { "Comp:PrototechPanel", 675 },
                { "Comp:Construction", 40 }, { "Comp:Computer", 20 },
            };

            List<string> dropped = InstallBomSelfReference.StripSelfProduced(
                NodeCapabilityKind.PrototechRefinery, bom, TwoTierRecipes());

            Assert.Empty(dropped);
            Assert.Equal(4, bom.Count);
            Assert.Equal(1, bom["Comp:PrototechFrame"]);
        }

        [Fact]
        public void AComponentNothingProduces_StaysOnTheBom()
        {
            // Dropping it would quietly cheapen the install. An install nobody can pay for is a
            // different problem, and it has its own signal.
            var bom = new Dictionary<string, long> { { "Comp:Unobtainium", 5 }, { "Comp:PrototechPanel", 10 } };

            List<string> dropped = InstallBomSelfReference.StripSelfProduced(
                NodeCapabilityKind.PrototechForge, bom, TwoTierRecipes());

            Assert.Equal(new[] { "Comp:PrototechPanel" }, dropped.ToArray());
            Assert.Equal(5, bom["Comp:Unobtainium"]);
        }

        [Fact]
        public void BothPrototechBlocks_AreChargedTheirRealBom()
        {
            CapabilityRecipesConfig cfg = CapabilityRecipesConfig.Defaults();
            Assert.Equal("LargePrototechAssembler", cfg.InstallBlocks["PrototechForge"]);
            Assert.Equal("LargePrototechRefinery",  cfg.InstallBlocks["PrototechRefinery"]);
            // And the flat cost still exists as the fallback for a host that cannot resolve the block.
            Assert.True(new FactionTuning().InstallCostFor(NodeCapabilityKind.PrototechForge, ItemKind.ProcessedGood) > 0);
        }

        // ---- the two recipes SE does not have ---------------------------------------------------

        [Fact]
        public void SyntheticRecipes_ProduceTheTwoMaterialsSeHasNoProducerFor()
        {
            IList<ConversionRecipe> recipes = CapabilityRecipesConfig.DefaultPrototechRecipes();

            ConversionRecipe scrap = Named(recipes, "SynthPrototechScrap");
            ConversionRecipe frame = Named(recipes, "SynthPrototechFrame");

            Assert.Equal("MyObjectBuilder_Ingot",     scrap.Outputs[0].TypeId);
            Assert.Equal("PrototechScrap",            scrap.Outputs[0].SubTypeId);
            Assert.Equal("MyObjectBuilder_Component", frame.Outputs[0].TypeId);
            Assert.Equal("PrototechFrame",            frame.Outputs[0].SubTypeId);

            // The frame is made OF scrap: the chain is two steps, and reversing them would make the
            // frame producible without the tier ever running the first recipe.
            Assert.Equal("PrototechScrap", frame.Inputs[0].SubTypeId);
            // Neither may consume its own output -- SE's own stubs do exactly that, which is why
            // they resolve into nothing.
            foreach (RecipeItem input in scrap.Inputs)
                Assert.NotEqual("PrototechScrap", input.SubTypeId);
        }

        [Fact]
        public void SyntheticScrap_IsPaidForInTheOresThatRefineWorst()
        {
            // Platinum refines at 0.5% and gold and uranium at 1%, so pricing scrap in those is what
            // makes a Factorum hull cost deep-space ore rather than iron. Dropping them would leave
            // the recipe nominally intact and quietly free.
            ConversionRecipe scrap = Named(CapabilityRecipesConfig.DefaultPrototechRecipes(), "SynthPrototechScrap");
            var subtypes = new List<string>();
            foreach (RecipeItem input in scrap.Inputs) subtypes.Add(input.SubTypeId);

            Assert.Contains("Platinum", subtypes);
            Assert.Contains("Gold",     subtypes);
            Assert.Contains("Uranium",  subtypes);
        }

        [Fact]
        public void ReachabilityClosure_MustIncludeTheSyntheticsOrTheFrameRecipeIsDropped()
        {
            // The trap this guards: RecipeExclusions' derived rule drops a recipe whose FIRST input
            // nothing in the world produces, and the frame recipe's first input is scrap, which only
            // the other synthetic recipe makes. Resolve the closure over SE's recipes alone and the
            // host silently deletes the recipe that exists to make Factorum hulls buildable.
            var minted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MyObjectBuilder_Ore/Iron", "MyObjectBuilder_Ore/Gold", "MyObjectBuilder_Ore/Platinum",
                "MyObjectBuilder_Ore/Uranium", "MyObjectBuilder_Ore/Silver", "MyObjectBuilder_Ore/Cobalt",
            };
            var vanilla = new List<ConversionRecipe>
            {
                OreToIngot("Iron"), OreToIngot("Gold"), OreToIngot("Platinum"),
                OreToIngot("Uranium"), OreToIngot("Silver"), OreToIngot("Cobalt"),
            };
            IList<ConversionRecipe> synthetics = CapabilityRecipesConfig.DefaultPrototechRecipes();
            ConversionRecipe frame = Named(synthetics, "SynthPrototechFrame");

            HashSet<string> withoutSynthetics = RecipeReachability.Resolve(minted, vanilla);
            var withSynthetics = new List<ConversionRecipe>(vanilla);
            withSynthetics.AddRange(synthetics);
            HashSet<string> withThem = RecipeReachability.Resolve(minted, withSynthetics);

            string reason;
            Assert.True(Exclusions(withoutSynthetics).ShouldExclude(frame, out reason));
            Assert.Contains("unreachable", reason);
            Assert.False(Exclusions(withThem).ShouldExclude(frame, out reason));
        }

        // ---- machine physics and install economics ----------------------------------------------

        [Fact]
        public void EveryCapability_HasAMachineProfileAndAnInstallCost()
        {
            // Both tables are ordinal-indexed arrays read through a length guard, so a kind added
            // past their end reads as speed 0 (never produces) and cost 0 (installs free) with no
            // error anywhere.
            var tuning = new FactionTuning();
            foreach (NodeCapabilityKind cap in FactionTuning.AllCapabilities)
            {
                MachineProfile profile = MachinePhysics.DefaultProfile(cap);
                bool producer = cap != NodeCapabilityKind.Shipyard;
                if (producer)
                {
                    Assert.True(profile.BaseSpeed > 0, cap + " has no machine speed");
                    Assert.True(profile.MachineCount > 0, cap + " has no machines");
                }

                long cost = tuning.InstallCostFor(cap, ItemKind.Ore)
                          + tuning.InstallCostFor(cap, ItemKind.Ingot)
                          + tuning.InstallCostFor(cap, ItemKind.ProcessedGood)
                          + tuning.InstallCostFor(cap, ItemKind.Hydrogen);
                Assert.True(cost > 0, cap + " installs for free");
            }
        }

        [Fact]
        public void PrototechRefinery_IsTheFasterMachine()
        {
            Assert.Equal(5.50, MachinePhysics.DefaultProfile(NodeCapabilityKind.PrototechRefinery).BaseSpeed, 2);
            Assert.True(MachinePhysics.DefaultProfile(NodeCapabilityKind.PrototechRefinery).BaseSpeed
                      > MachinePhysics.DefaultProfile(NodeCapabilityKind.RefineryII).BaseSpeed);
        }

        [Fact]
        public void PrototechCapabilities_SitAboveTheirVanillaTier()
        {
            Assert.Equal(NodeCapabilityKind.ManufactoryII,
                CapabilityPrerequisites.For(NodeCapabilityKind.PrototechForge));
            Assert.Equal(NodeCapabilityKind.RefineryII,
                CapabilityPrerequisites.For(NodeCapabilityKind.PrototechRefinery));
        }

        // ---- the tier runs, and does not displace a cheaper one ---------------------------------

        [Fact]
        public void AForge_TurnsOrdinaryIngotsIntoScrapAndThenIntoFrames()
        {
            WorldModel world = ForgeWorld();
            SpatialNode node = world.Graph.Nodes["N1"];
            var manager = new ProductionManager(ForgeRecipes());
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();

            for (int i = 0; i < 60; i++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(30);
                manager.Tick(states, world, world.Tuning, log);
            }

            long scrap; node.Inventory.TryGetValue(ScrapKey, out scrap);
            long frames; node.Inventory.TryGetValue(FrameKey, out frames);
            Assert.True(scrap > 0 || frames > 0, "the forge produced no prototech material at all");
            Assert.True(frames > 0, "scrap was made but never became a frame -- frames=" + frames + " scrap=" + scrap);
        }

        [Fact]
        public void AnIngotDemand_AsksForRefineryII_NotThePrototechRefinery()
        {
            // Both tiers output the same Ingot: keys, so both are candidates. Ordering the fallback
            // by tier is what stops an ordinary faction being charged a prototech block to refine
            // iron -- and its install BOM starts with a component only a forge can make, so the
            // request would never be fillable either.
            var log = RunIngotShortfall(bothRefineryTiers: true);

            Assert.Contains(log, l => l.Contains("INVEST_REQUEST RefineryII"));
            Assert.DoesNotContain(log, l => l.Contains("INVEST_REQUEST PrototechRefinery"));
        }

        [Fact]
        public void ThePrototechRefineryIsAGenuineCandidate_WhenNothingCheaperCanRefine()
        {
            // The other half of the tie-break: without this, the assertion above would pass just as
            // well if the prototech refinery were never a candidate for an ingot demand at all.
            var log = RunIngotShortfall(bothRefineryTiers: false);

            Assert.Contains(log, l => l.Contains("INVEST_REQUEST PrototechRefinery"));
        }

        [Fact]
        public void AThroughputConstrainedForgeOwner_UpgradesToThePrototechRefinery()
        {
            // Stage 1's upgrade target, given the same constrained rates in both runs: ore income
            // outrunning what the installed refineries consume. A faction running a forge can pay a
            // prototech block's install BOM, because that BOM starts with a frame only a forge makes;
            // one without a forge must be sent to RefineryII or the request is unfillable forever.
            List<string> plain    = ThroughputInvestments(withForge: false);
            List<string> withForge = ThroughputInvestments(withForge: true);

            Assert.Contains(plain, l => l.Contains("THROUGHPUT_INVEST RefineryII"));
            Assert.DoesNotContain(plain, l => l.Contains("THROUGHPUT_INVEST PrototechRefinery"));
            Assert.Contains(withForge, l => l.Contains("THROUGHPUT_INVEST PrototechRefinery"));
            Assert.DoesNotContain(withForge, l => l.Contains("THROUGHPUT_INVEST RefineryII"));
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static ConversionRecipe Named(IList<ConversionRecipe> recipes, string blueprintId)
        {
            foreach (ConversionRecipe r in recipes)
                if (r.BlueprintId == blueprintId) return r;
            throw new Xunit.Sdk.XunitException("no synthetic recipe named " + blueprintId);
        }

        private static RecipeExclusions Exclusions(HashSet<string> reachable)
        {
            return new RecipeExclusions(new List<string>(), new List<string>(), reachable);
        }

        private static ConversionRecipe OreToIngot(string subType)
        {
            return new ConversionRecipe
            {
                BlueprintId = subType + "OreToIngot",
                TimeSecs    = 1f,
                Inputs      = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = subType, Amount = 1 } },
                Outputs     = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = subType, Amount = 1 } },
            };
        }

        /// <summary>A forge that makes prototech components and a ManufactoryII that makes ordinary
        /// ones -- the minimum needed to tell "only this capability makes it" from "something else
        /// does".</summary>
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> TwoTierRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.PrototechForge, new List<ConversionRecipe>
                    {
                        Makes("PrototechFrame"), Makes("PrototechPanel"),
                        Makes("PrototechMachinery"), Makes("PrototechCoolingUnit"),
                    } },
                { NodeCapabilityKind.ManufactoryII, new List<ConversionRecipe>
                    {
                        Makes("Construction"), Makes("MetalGrid"), Makes("Computer"), Makes("Display"),
                    } },
            };
        }

        private static ConversionRecipe Makes(string component)
        {
            return new ConversionRecipe
            {
                BlueprintId = component,
                TimeSecs    = 1f,
                Inputs      = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                Outputs     = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = component, Amount = 1 } },
            };
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> ForgeRecipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.PrototechForge,
                  new List<ConversionRecipe>(CapabilityRecipesConfig.DefaultPrototechRecipes()) },
            };
        }

        private static WorldModel ForgeWorld()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.PrototechForge] = 1;
            foreach (var stock in new Dictionary<string, long>
            {
                { "Ingot:Iron", 200000 }, { "Ingot:Silver", 40000 }, { "Ingot:Gold", 20000 },
                { "Ingot:Platinum", 10000 }, { "Ingot:Uranium", 10000 }, { "Ingot:Cobalt", 20000 },
            })
                node.Inventory[stock.Key] = stock.Value;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + ScrapKey, NodeId = "N1",
                Quantity = 500, ExpiresUtc = world.UtcNow.AddHours(6),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + FrameKey, NodeId = "N1",
                Quantity = 50, ExpiresUtc = world.UtcNow.AddHours(6),
            });
            return world;
        }

        private static List<string> RunIngotShortfall(bool bothRefineryTiers)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            // Owns something, but nothing whose recipes refine iron, so every tier that has those
            // recipes is a live candidate for the demand below. Each candidate's prerequisite is
            // installed (with no recipes of its own): a demand gap asks for the lowest MISSING link
            // of a chain, so without them this would only ever ask for the prerequisite and the
            // tie-break between the two tiers would never be reached.
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            if (!bothRefineryTiers) node.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            node.Inventory["Ore:Iron"] = 1000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 500, ExpiresUtc = world.UtcNow.AddMinutes(30),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.PrototechRefinery, new List<ConversionRecipe> { OreToIngot("Iron") } },
            };
            if (bothRefineryTiers)
                capRecipes[NodeCapabilityKind.RefineryII] = new List<ConversionRecipe> { OreToIngot("Iron") };

            var manager = new ProductionManager(capRecipes);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);
            return log;
        }

        /// <summary>Stage 1's decision at a fixed pair of rates. Driven directly rather than through
        /// the rate machinery, which is exercised by ProductionCycleTests: what is under test here is
        /// which capability a throughput-constrained faction is sent to, not how the rates are
        /// measured.</summary>
        private static List<string> ThroughputInvestments(bool withForge)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            // Rich enough that the free-miner model gives it real ore income: stage 1 only fires
            // when income outruns what the installed refineries consume.
            node.Richness = 1.0;
            node.ResourceValue = 1.0;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            if (withForge) node.Capabilities[NodeCapabilityKind.PrototechForge] = 1;
            node.Inventory["Ore:Iron"] = 1000;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                Quantity = 500, ExpiresUtc = world.UtcNow.AddMinutes(30),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Refinery, new List<ConversionRecipe> { OreToIngot("Iron") } },
            };
            if (withForge)
                capRecipes[NodeCapabilityKind.PrototechForge] =
                    new List<ConversionRecipe>(CapabilityRecipesConfig.DefaultPrototechRecipes());

            var manager = new ProductionManager(capRecipes);
            var state = new FactionState { Tag = "F" };
            var log = new List<string>();
            var ctx = new LiveProductionExecutionContext(world, log);
            var networkCaps = new HashSet<NodeCapabilityKind>(node.Capabilities.Keys);

            System.Reflection.MethodInfo decide = typeof(ProductionManager).GetMethod(
                "DecideThroughputInvestments",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(decide);
            decide.Invoke(manager, new object[]
            {
                world.Tuning, state, networkCaps, ctx,
                /* oreIncomePerMin   */ 100.0,
                /* oreConsumedPerMin */  10.0,
                /* ingotIncomePerMin */   0.0,
                /* ingotConsumed     */   0.0,
                /* refineUtilisation */   1.0,
                /* manufUtilisation  */   1.0,
                /* gas               */ System.Activator.CreateInstance(
                    typeof(ProductionManager).GetNestedType("GasStageRates",
                        System.Reflection.BindingFlags.NonPublic)),
            });

            var throughput = new List<string>();
            foreach (string line in log) if (line.Contains("THROUGHPUT_INVEST")) throughput.Add(line);
            return throughput;
        }
    }
}
