using System.Collections.Generic;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The static half of a demand chain: what one unit of a producible key costs in mined material.
    /// Numbers here mirror the live catalogue and the live world's machine profiles
    /// (production_model.json, 2026-08-31) so a regression shows up as a wrong quantity rather than
    /// only as a wrong shape.
    /// </summary>
    public sealed class RecipeExpansionTests
    {
        // The live world: assemblers divide inputs by 3, Refinery I scales output by 0.70 and
        // RefineryII by 1.00. Anything not stamped is neutral.
        private static MachineModel[] LiveModels()
        {
            var models = new MachineModel[7];
            for (int i = 0; i < models.Length; i++)
                models[i] = new MachineModel { Speed = 1.0, OutputScale = 1.0, InputDivisor = 1.0, MachineCount = 6 };

            models[(int)NodeCapabilityKind.Refinery]      = Model(outputScale: 0.70, inputDivisor: 1.0);
            models[(int)NodeCapabilityKind.RefineryII]    = Model(outputScale: 1.00, inputDivisor: 1.0);
            models[(int)NodeCapabilityKind.Manufactory]   = Model(outputScale: 1.00, inputDivisor: 3.0);
            models[(int)NodeCapabilityKind.ManufactoryII] = Model(outputScale: 1.00, inputDivisor: 3.0);
            return models;
        }

        private static MachineModel Model(double outputScale, double inputDivisor)
        {
            return new MachineModel
            {
                Speed = 1.0, OutputScale = outputScale, InputDivisor = inputDivisor, MachineCount = 6,
            };
        }

        private static RecipeItem Ore(string subType, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = subType, Amount = amount };
        }

        private static RecipeItem Ingot(string subType, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = subType, Amount = amount };
        }

        private static RecipeItem Comp(string subType, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = subType, Amount = amount };
        }

        private static ConversionRecipe Recipe(string blueprintId, RecipeItem[] inputs, RecipeItem[] outputs,
            float timeSecs = 1f, bool ignoresEfficiency = false)
        {
            return new ConversionRecipe
            {
                BlueprintId = blueprintId,
                Inputs = inputs,
                Outputs = outputs,
                TimeSecs = timeSecs,
                IgnoresEfficiencyMultiplier = ignoresEfficiency,
            };
        }

        // ThrustComponent and IronOreToIngot, verbatim from the live catalogue.
        private static ConversionRecipe ThrustComponent()
        {
            return Recipe("ThrustComponent",
                new[] { Ingot("Iron", 30.0), Ingot("Cobalt", 10.0), Ingot("Gold", 1.0), Ingot("Platinum", 0.4) },
                new[] { Comp("Thrust", 1.0) }, 4f);
        }

        private static ConversionRecipe IronOreToIngot()
        {
            return Recipe("IronOreToIngot", new[] { Ore("Iron", 1.0) }, new[] { Ingot("Iron", 0.7) }, 0.05f);
        }

        private static ConversionRecipe StoneOreToIngot()
        {
            return Recipe("StoneOreToIngot", new[] { Ore("Stone", 1000.0) },
                new[] { Ingot("Stone", 14.0), Ingot("Iron", 30.0), Ingot("Nickel", 2.4), Ingot("Silicon", 4.0) },
                10f);
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> Caps(
            params KeyValuePair<NodeCapabilityKind, ConversionRecipe[]>[] entries)
        {
            var caps = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>();
            foreach (var entry in entries)
                caps[entry.Key] = new List<ConversionRecipe>(entry.Value);
            return caps;
        }

        private static KeyValuePair<NodeCapabilityKind, ConversionRecipe[]> On(
            NodeCapabilityKind cap, params ConversionRecipe[] recipes)
        {
            return new KeyValuePair<NodeCapabilityKind, ConversionRecipe[]>(cap, recipes);
        }

        [Fact]
        public void ComponentChain_ChargesTheAssemblerEfficiencyDivisor_NotTheRawBlueprintAmount()
        {
            // FAC-205's own worked example. The blueprint says 30 Ingot:Iron per Thrust, but this
            // world's AssemblerEfficiencyMultiplier is 3 and ThrustComponent does not opt out of it,
            // so the real cost is 10 -- and 14.286 Ore:Iron at a RefineryII's 1.00 material
            // efficiency. Reading the raw 30 is a 3x oversize, which is the exact failure mode the
            // chain exists to remove.
            var caps = Caps(
                On(NodeCapabilityKind.ManufactoryII, ThrustComponent()),
                On(NodeCapabilityKind.RefineryII, IronOreToIngot()));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());
            IReadOnlyDictionary<string, double> perThrust = table.RequirementPerUnit("Comp:Thrust");

            Assert.Equal(10.0 / 0.7, perThrust["Ore:Iron"], 6);
            Assert.Equal(31.0 * 10.0 / 0.7, perThrust["Ore:Iron"] * 31.0, 6);   // 442.9 for the live build
        }

        [Fact]
        public void RefineryTier_ChangesTheOreCost_SoOneTableCannotServeBoth()
        {
            // Refinery I's MaterialEfficiency 0.70 against RefineryII's 1.00: the same recipe, 43%
            // more ore. A single shared table would undersize one of these factions.
            var tierTwo = Caps(
                On(NodeCapabilityKind.ManufactoryII, ThrustComponent()),
                On(NodeCapabilityKind.RefineryII, IronOreToIngot()));
            var tierOne = Caps(
                On(NodeCapabilityKind.ManufactoryII, ThrustComponent()),
                On(NodeCapabilityKind.Refinery, IronOreToIngot()));

            double best  = RecipeExpansion.Build(tierTwo, null, LiveModels()).RequirementPerUnit("Comp:Thrust")["Ore:Iron"];
            double worst = RecipeExpansion.Build(tierOne, null, LiveModels()).RequirementPerUnit("Comp:Thrust")["Ore:Iron"];

            Assert.Equal(10.0 / 0.7,        best,  6);
            Assert.Equal(10.0 / (0.7 * 0.7), worst, 6);
            Assert.True(worst > best * 1.4, "tier I should cost materially more ore: " + worst + " vs " + best);
        }

        [Fact]
        public void DedicatedRecipe_BeatsTheJointProductRecipe_ForTheSameOutput()
        {
            // StoneOreToIngot yields iron, but as one of four co-products of 1000 stone. Following it
            // would charge a chain 33 stone per iron ingot and demand three materials nobody asked
            // for.
            var caps = Caps(On(NodeCapabilityKind.RefineryII, StoneOreToIngot(), IronOreToIngot()));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());
            RecipeExpansion.Route route;

            Assert.True(table.TryGetRoute("Ingot:Iron", out route));
            Assert.Equal("IronOreToIngot", route.Recipe.BlueprintId);

            IReadOnlyDictionary<string, double> perIngot = table.RequirementPerUnit("Ingot:Iron");
            Assert.Equal(1.0 / 0.7, perIngot["Ore:Iron"], 6);
            Assert.False(perIngot.ContainsKey("Ore:Stone"));
        }

        [Fact]
        public void JointProductRecipe_IsUsedWhenItIsTheOnlyProducer_WithNoByProductCredit()
        {
            // Ingot:Stone has no dedicated route in the live capability set. Dropping joint recipes
            // outright would make it a leaf -- an unminable material the chain would then demand a
            // Mine run for.
            var caps = Caps(On(NodeCapabilityKind.RefineryII, StoneOreToIngot()));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());

            Assert.False(table.IsLeaf("Ingot:Stone"));
            Assert.Equal(1000.0 / 14.0, table.RequirementPerUnit("Ingot:Stone")["Ore:Stone"], 6);
            // The whole batch is charged to whichever output is asked for, so the co-products cost
            // their own share of the same ore rather than arriving free.
            Assert.Equal(1000.0 / 30.0, table.RequirementPerUnit("Ingot:Iron")["Ore:Stone"], 6);
        }

        [Fact]
        public void SameBlueprintOnTwoTiers_ResolvesToTheBetterMachine_NotToBoth()
        {
            // The live catalogue registers 21 of its 25 multi-producer keys this way. Treating them
            // as two routes would double-count or pick by dictionary order.
            var caps = Caps(
                On(NodeCapabilityKind.Refinery, IronOreToIngot()),
                On(NodeCapabilityKind.RefineryII, IronOreToIngot()));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());
            RecipeExpansion.Route route;

            Assert.True(table.TryGetRoute("Ingot:Iron", out route));
            Assert.Equal(NodeCapabilityKind.RefineryII, route.Cap);
            Assert.Equal(1.0 / 0.7, table.RequirementPerUnit("Ingot:Iron")["Ore:Iron"], 6);
        }

        [Fact]
        public void KeyNoRecipeProduces_IsALeaf_AndExpandsToOneOfItself()
        {
            var caps = Caps(On(NodeCapabilityKind.RefineryII, IronOreToIngot()));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());

            Assert.True(table.IsLeaf("Ore:Iron"));
            Assert.False(table.IsLeaf("Ingot:Iron"));

            IReadOnlyDictionary<string, double> ore = table.RequirementPerUnit("Ore:Iron");
            Assert.Equal(1, ore.Count);
            Assert.Equal(1.0, ore["Ore:Iron"], 6);
        }

        [Fact]
        public void RecipeCycle_Terminates_AndBothKeysReadAsLeaves()
        {
            // Ingot to ingot, both directions. Nothing in the cycle is grounded in mined material,
            // so neither key has a chain -- and saying so is the point: a consumer must not read a
            // number that came out of going round the loop a bounded number of times.
            var aToB = Recipe("AtoB", new[] { Ingot("A", 2.0) }, new[] { Ingot("B", 1.0) });
            var bToA = Recipe("BtoA", new[] { Ingot("B", 2.0) }, new[] { Ingot("A", 1.0) });
            var caps = Caps(On(NodeCapabilityKind.RefineryII, aToB, bToA));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());

            Assert.True(table.IsLeaf("Ingot:A"));
            Assert.True(table.IsLeaf("Ingot:B"));
            Assert.Equal(0, table.Count);

            IReadOnlyDictionary<string, double> perB = table.RequirementPerUnit("Ingot:B");
            Assert.Equal(1, perB.Count);
            Assert.Equal(1.0, perB["Ingot:B"], 6);
        }

        [Fact]
        public void KeyAboveACycle_KeepsItsChain_AndStopsAtTheCycledInput()
        {
            // The cycle must not swallow the level above it. A component whose input is trapped in a
            // cycle still has a real, sizeable requirement for that input -- that is where its
            // frontier is.
            var aToB   = Recipe("AtoB", new[] { Ingot("A", 2.0) }, new[] { Ingot("B", 1.0) });
            var bToA   = Recipe("BtoA", new[] { Ingot("B", 2.0) }, new[] { Ingot("A", 1.0) });
            var widget = Recipe("Widget", new[] { Ingot("B", 6.0) }, new[] { Comp("Widget", 1.0) });
            var caps = Caps(
                On(NodeCapabilityKind.RefineryII, aToB, bToA),
                On(NodeCapabilityKind.ManufactoryII, widget));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());

            Assert.False(table.IsLeaf("Comp:Widget"));
            Assert.Equal(2.0, table.RequirementPerUnit("Comp:Widget")["Ingot:B"], 6);   // 6 / inDiv 3
        }

        [Fact]
        public void CapabilitySet_ThatCarriesNoRecipes_SharesOneTable()
        {
            // Shipyard and SensorArray run no recipes. Splitting a table on them would multiply the
            // cache for nothing.
            var caps = Caps(
                On(NodeCapabilityKind.ManufactoryII, ThrustComponent()),
                On(NodeCapabilityKind.RefineryII, IronOreToIngot()));
            var cache = new RecipeExpansionCache(caps, LiveModels());

            RecipeExpansion bare = cache.For(new[]
                { NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.RefineryII });
            RecipeExpansion withYard = cache.For(new[]
                { NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.RefineryII,
                  NodeCapabilityKind.Shipyard, NodeCapabilityKind.SensorArray });

            Assert.Same(bare, withYard);
            Assert.Equal(1, cache.TableCount);
        }

        [Fact]
        public void CapabilitySet_ThatChangesTheRefineryTier_GetsItsOwnTable()
        {
            var caps = Caps(
                On(NodeCapabilityKind.ManufactoryII, ThrustComponent()),
                On(NodeCapabilityKind.Refinery, IronOreToIngot()),
                On(NodeCapabilityKind.RefineryII, IronOreToIngot()));
            var cache = new RecipeExpansionCache(caps, LiveModels());

            double tierOne = cache.For(new[] { NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.Refinery })
                                  .RequirementPerUnit("Comp:Thrust")["Ore:Iron"];
            double tierTwo = cache.For(new[] { NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.RefineryII })
                                  .RequirementPerUnit("Comp:Thrust")["Ore:Iron"];

            Assert.Equal(2, cache.TableCount);
            Assert.Equal(10.0 / (0.7 * 0.7), tierOne, 6);
            Assert.Equal(10.0 / 0.7,         tierTwo, 6);
        }

        [Fact]
        public void FactionWithoutTheRefinery_SeesTheIngotAsALeaf_NotAsFreeOre()
        {
            // The whole reason the table is per capability set: a faction that cannot refine must see
            // its chain stop at Ingot:Iron. Reading the network-wide table would tell it to mine ore
            // it has no way to convert.
            var caps = Caps(
                On(NodeCapabilityKind.ManufactoryII, ThrustComponent()),
                On(NodeCapabilityKind.RefineryII, IronOreToIngot()));
            var cache = new RecipeExpansionCache(caps, LiveModels());

            RecipeExpansion noRefinery = cache.For(new[] { NodeCapabilityKind.ManufactoryII });
            IReadOnlyDictionary<string, double> perThrust = noRefinery.RequirementPerUnit("Comp:Thrust");

            Assert.True(noRefinery.IsLeaf("Ingot:Iron"));
            Assert.Equal(10.0, perThrust["Ingot:Iron"], 6);
            Assert.False(perThrust.ContainsKey("Ore:Iron"));
        }

        [Fact]
        public void RecipeThatIgnoresTheEfficiencyMultiplier_IsChargedInFull()
        {
            // 22 of ManufactoryII's 150 recipes set this. Applying the divisor to them undersizes
            // their chains by two thirds.
            var full = Recipe("HandTool", new[] { Ingot("Iron", 30.0) }, new[] { Comp("Tool", 1.0) },
                ignoresEfficiency: true);
            var caps = Caps(On(NodeCapabilityKind.ManufactoryII, full, ThrustComponent()));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, LiveModels());

            Assert.Equal(30.0, table.RequirementPerUnit("Comp:Tool")["Ingot:Iron"], 6);
            Assert.Equal(10.0, table.RequirementPerUnit("Comp:Thrust")["Ingot:Iron"], 6);
        }

        [Fact]
        public void EmptyCatalogue_ProducesNothing_AndEveryKeyReadsAsALeaf()
        {
            RecipeExpansion empty = RecipeExpansion.Build(null, null, null);

            Assert.Equal(0, empty.Count);
            Assert.True(empty.IsLeaf("Comp:Thrust"));
            Assert.Equal(1.0, empty.RequirementPerUnit("Comp:Thrust")["Comp:Thrust"], 6);
            Assert.Empty(empty.RequirementPerUnit(null));
        }

        [Fact]
        public void UnstampedModels_PriceEveryMachineNeutrally_MatchingTheOfflineSim()
        {
            // Factions.Sim and the unit suite run without host-stamped profiles. The table must still
            // build, at the raw blueprint amounts.
            var caps = Caps(
                On(NodeCapabilityKind.ManufactoryII, ThrustComponent()),
                On(NodeCapabilityKind.RefineryII, IronOreToIngot()));

            RecipeExpansion table = RecipeExpansion.Build(caps, null, null);

            // No assembler divisor and no refinery scaling: the raw 30 ingots, and the ore the
            // recipe's own 0.7 yield asks for.
            Assert.Equal(30.0 / 0.7, table.RequirementPerUnit("Comp:Thrust")["Ore:Iron"], 6);
            Assert.False(table.RequirementPerUnit("Comp:Thrust").ContainsKey("Ingot:Iron"));
            Assert.Equal(1.0 / 0.7, table.RequirementPerUnit("Ingot:Iron")["Ore:Iron"], 6);
        }
    }
}
