using System.Collections.Generic;
using Factions;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A <c>SafeZoneBlock</c> costs five <c>Comp:ZoneChip</c>, and SE's own <c>ZoneChip</c>
    /// blueprint is a one-for-one self-loop in no blueprint class — an economy-price stub, not a
    /// producer. Every structure carrying a safe zone therefore had a component nothing could make.
    /// <c>SynthZoneChip</c> closes it; these tests pin the three things that make it work — that it
    /// exists on ManufactoryII, that the closure it joins actually reaches the chip, and that its
    /// basket is charged in full rather than divided by the world's assembler efficiency.
    /// </summary>
    public sealed class ZoneChipRecipeTests
    {
        private const string ChipKey = "MyObjectBuilder_Component/ZoneChip";

        /// <summary>SE's stub, as the SBC ships it: 1 ZoneChip in, 1 ZoneChip out.</summary>
        private static ConversionRecipe SeSelfLoop()
        {
            return new ConversionRecipe
            {
                BlueprintId = "ZoneChip",
                TimeSecs    = 99999f,
                Inputs      = new[] { Ingredient("MyObjectBuilder_Component", "ZoneChip", 1) },
                Outputs     = new[] { Ingredient("MyObjectBuilder_Component", "ZoneChip", 1) },
            };
        }

        private static RecipeItem Ingredient(string typeId, string subTypeId, double amount)
        {
            var item = new RecipeItem();
            item.TypeId    = typeId;
            item.SubTypeId = subTypeId;
            item.Amount    = amount;
            return item;
        }

        private static string[] MintedIngots()
        {
            return new[]
            {
                "MyObjectBuilder_Ingot/Silicon",
                "MyObjectBuilder_Ingot/Silver",
                "MyObjectBuilder_Ingot/Gold",
                "MyObjectBuilder_Ingot/Platinum",
            };
        }

        [Fact]
        public void TheChipIsMadeOnManufactoryII_AndNowhereElse()
        {
            CapabilityRecipesConfig cfg = CapabilityRecipesConfig.Defaults();

            Assert.Single(cfg.SyntheticRecipesFor("ManufactoryII"));
            Assert.Equal("SynthZoneChip", cfg.SyntheticRecipesFor("ManufactoryII")[0].BlueprintId);

            foreach (var entry in cfg.SyntheticRecipes)
            {
                if (entry.Key == "ManufactoryII") continue;
                foreach (ConversionRecipe recipe in entry.Value)
                    foreach (RecipeItem output in recipe.Outputs)
                        Assert.NotEqual("ZoneChip", output.SubTypeId);
            }
        }

        [Fact]
        public void SeOwnBlueprintCannotBootstrapTheChip_ButTheSyntheticRecipeCan()
        {
            // The characteristic failure: a self-loop needs the thing it makes, so a closure holding
            // only SE's blueprint leaves the chip outside it however much ore the world mints.
            var seOnly = new List<ConversionRecipe> { SeSelfLoop() };
            Assert.DoesNotContain(ChipKey, RecipeReachability.Resolve(MintedIngots(), seOnly));

            var withSynthetic = new List<ConversionRecipe> { SeSelfLoop() };
            withSynthetic.AddRange(CapabilityRecipesConfig.DefaultZoneChipRecipes());
            Assert.Contains(ChipKey, RecipeReachability.Resolve(MintedIngots(), withSynthetic));
        }

        [Fact]
        public void TheBasketIsSiliconSilverGoldAndPlatinum()
        {
            ConversionRecipe chip = CapabilityRecipesConfig.DefaultZoneChipRecipes()[0];

            var subtypes = new List<string>();
            var amounts  = new List<double>();
            foreach (RecipeItem input in chip.Inputs)
            {
                Assert.Equal("MyObjectBuilder_Ingot", input.TypeId);
                subtypes.Add(input.SubTypeId);
                amounts.Add(input.Amount);
            }

            Assert.Equal(new[] { "Silicon", "Silver", "Gold", "Platinum" }, subtypes.ToArray());
            Assert.Equal(new[] { 500.0, 100.0, 20.0, 5.0 }, amounts.ToArray());

            Assert.Single(chip.Outputs);
            Assert.Equal("MyObjectBuilder_Component", chip.Outputs[0].TypeId);
            Assert.Equal("ZoneChip", chip.Outputs[0].SubTypeId);
            Assert.Equal(1, chip.Outputs[0].Amount);
        }

        [Fact]
        public void TheBasketIsChargedInFull_NotDividedByAssemblerEfficiency()
        {
            // ManufactoryII carries the world's AssemblerEfficiencyMultiplier as an input divisor
            // (3.00 on the live server), so without the opt-out a basket that reads 1,021,500 in
            // ingots would cost a third of that.
            ConversionRecipe chip = CapabilityRecipesConfig.DefaultZoneChipRecipes()[0];
            var manufactoryII = new MachineModel { Speed = 15.0, OutputScale = 1.0, InputDivisor = 3.0, MachineCount = 6 };

            Assert.True(chip.IgnoresEfficiencyMultiplier);
            foreach (RecipeItem input in chip.Inputs)
                Assert.Equal(input.Amount,
                    MachinePhysics.InputAmount(input.Amount, manufactoryII, chip.IgnoresEfficiencyMultiplier));

            // The same amounts without the flag — what the recipe would really have cost.
            Assert.Equal(500.0 / 3.0, MachinePhysics.InputAmount(500, manufactoryII, false));
        }

        [Fact]
        public void MergeDefaults_BackfillsTheRecipeIntoAFileThatPredatesIt_WithoutTouchingTheForge()
        {
            // capability_recipes.json is written only when absent, so the live file already carries
            // SyntheticRecipes with a PrototechForge key and no ManufactoryII one. Merging is per
            // entry, not per map, which is the only reason the chip reaches that file at all.
            var old = new CapabilityRecipesConfig();
            old.SyntheticRecipes["PrototechForge"] = new List<ConversionRecipe>
            {
                new ConversionRecipe { BlueprintId = "OperatorsOwnScrapRecipe" },
            };

            List<string> added = CapabilityRecipesConfig.MergeDefaults(old);

            Assert.Contains("SyntheticRecipes.ManufactoryII", added);
            Assert.Equal("SynthZoneChip", old.SyntheticRecipesFor("ManufactoryII")[0].BlueprintId);

            Assert.DoesNotContain("SyntheticRecipes.PrototechForge", added);
            Assert.Equal("OperatorsOwnScrapRecipe", old.SyntheticRecipesFor("PrototechForge")[0].BlueprintId);
        }
    }
}
