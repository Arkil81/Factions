using System.Collections.Generic;
using Factions;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="RecipeExclusions"/> — the gate deciding which resolved recipes any manager
    /// is allowed to see. Recipe shapes below are the real vanilla SE ones, taken from the live
    /// <c>item_recipe_cache.json</c>.
    /// </summary>
    public class RecipeExclusionsTests
    {
        private static ConversionRecipe Recipe(string blueprintId, params string[] inputKeys)
        {
            var inputs = new RecipeItem[inputKeys.Length];
            for (int i = 0; i < inputKeys.Length; i++)
            {
                string[] parts = inputKeys[i].Split('/');
                inputs[i] = new RecipeItem { TypeId = parts[0], SubTypeId = parts[1], Amount = 1 };
            }
            return new ConversionRecipe
            {
                BlueprintId = blueprintId,
                TimeSecs    = 1f,
                Inputs      = inputs,
                Outputs     = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
        }

        private static ConversionRecipe ScrapIngotToIronIngot()
        {
            return Recipe("ScrapIngotToIronIngot", "MyObjectBuilder_Ingot/Scrap");
        }

        // ReactorComponent's FIRST input is Ingot/Stone (gravel) -- one subtype away from the
        // Ore/Stone rule, and the recipe every other exclusion must not touch.
        private static ConversionRecipe ReactorComponent()
        {
            return Recipe("ReactorComponent",
                "MyObjectBuilder_Ingot/Stone", "MyObjectBuilder_Ingot/Iron", "MyObjectBuilder_Ingot/Silicon");
        }

        private static bool Excludes(RecipeExclusions ex, ConversionRecipe recipe)
        {
            string reason;
            return ex.ShouldExclude(recipe, out reason);
        }

        private static RecipeExclusions Defaults()
        {
            return RecipeExclusions.From(new CapabilityRecipesConfig());
        }

        [Fact]
        public void Default_ExcludesScrapIngotToIronIngot()
        {
            string reason;
            Assert.True(Defaults().ShouldExclude(ScrapIngotToIronIngot(), out reason));
            Assert.Equal("blueprint", reason);
        }

        [Fact]
        public void Default_ExcludesScrapOreRefining()
        {
            Assert.True(Excludes(Defaults(), Recipe("ScrapToIronIngot", "MyObjectBuilder_Ore/Scrap")));
        }

        // Stone ore has a real source: the mineable node kinds carry a MineOnly Ore:Stone yield
        // entry, so every blueprint refining it into gravel can actually run and must be admitted.
        // Gravel is ReactorComponent's first input.
        [Fact]
        public void Default_KeepsStoneOreRefining_AllVanillaBlueprints()
        {
            RecipeExclusions ex = Defaults();
            Assert.False(Excludes(ex, Recipe("StoneOreToIngot",                   "MyObjectBuilder_Ore/Stone")));
            Assert.False(Excludes(ex, Recipe("Position0010_StoneOreToIngotBasic", "MyObjectBuilder_Ore/Stone")));
            Assert.False(Excludes(ex, Recipe("StoneOreToIngot_Deconstruction",    "MyObjectBuilder_Ore/Stone")));
        }

        // The input rule is fully qualified for this reason alone: matching the subtype "Stone" on
        // its own also matches Ingot/Stone, dropping ReactorComponent from every capability and
        // deadlocking any ship BOM that needs it.
        [Fact]
        public void Default_KeepsReactorComponent_DespiteIngotStoneFirstInput()
        {
            Assert.False(Excludes(Defaults(), ReactorComponent()));
        }

        [Fact]
        public void Default_KeepsOrdinaryOreRefining()
        {
            RecipeExclusions ex = Defaults();
            Assert.False(Excludes(ex, Recipe("IronOreToIngot",     "MyObjectBuilder_Ore/Iron")));
            Assert.False(Excludes(ex, Recipe("UraniumOreToIngot",  "MyObjectBuilder_Ore/Uranium")));
        }

        // A capability_recipes.json written before either key existed deserializes them as null.
        // The file is only seeded when absent, so null must mean the built-in set, not "exclude none".
        [Fact]
        public void AbsentKeys_FallBackToBuiltInDefaults()
        {
            var legacy = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(
                "{\"Capabilities\":{},\"InstallBlocks\":{},\"InstallPrefabs\":{}}");
            Assert.Null(legacy.IgnoredBlueprints);
            Assert.Null(legacy.IgnoredInputKeys);

            RecipeExclusions ex = RecipeExclusions.From(legacy);
            Assert.True(Excludes(ex, ScrapIngotToIronIngot()));
            Assert.True(Excludes(ex, Recipe("ScrapToIronIngot", "MyObjectBuilder_Ore/Scrap")));
        }

        [Fact]
        public void EmptyLists_ExcludeNothing()
        {
            var cfg = new CapabilityRecipesConfig
            {
                IgnoredBlueprints = new List<string>(),
                IgnoredInputKeys  = new List<string>(),
            };
            RecipeExclusions ex = RecipeExclusions.From(cfg);
            Assert.False(Excludes(ex, ScrapIngotToIronIngot()));
            Assert.False(Excludes(ex, Recipe("ScrapToIronIngot", "MyObjectBuilder_Ore/Scrap")));
        }

        [Fact]
        public void OperatorList_ReplacesDefaults_AndMatchesCaseInsensitively()
        {
            var cfg = new CapabilityRecipesConfig
            {
                IgnoredBlueprints = new List<string> { "  ironoretoingot  " },
                IgnoredInputKeys  = new List<string>(),
            };
            RecipeExclusions ex = RecipeExclusions.From(cfg);
            Assert.True(Excludes(ex, Recipe("IronOreToIngot", "MyObjectBuilder_Ore/Iron")));
            Assert.False(Excludes(ex, ScrapIngotToIronIngot()));   // no longer on the list
        }

        [Fact]
        public void InputRule_MatchesFirstInputOnly()
        {
            var cfg = new CapabilityRecipesConfig
            {
                IgnoredBlueprints = new List<string>(),
                IgnoredInputKeys  = new List<string> { "MyObjectBuilder_Ingot/Silicon" },
            };
            RecipeExclusions ex = RecipeExclusions.From(cfg);
            Assert.False(Excludes(ex, ReactorComponent()));                                     // Silicon is input 3
            Assert.True(Excludes(ex, Recipe("X", "MyObjectBuilder_Ingot/Silicon")));            // ...and input 1 here
        }

        [Fact]
        public void RecipeWithNoInputs_IsNotExcludedByTheInputRule()
        {
            var noInputs = new ConversionRecipe { BlueprintId = "Odd", TimeSecs = 1f, Inputs = new RecipeItem[0] };
            Assert.False(Excludes(Defaults(), noInputs));
            Assert.False(Excludes(Defaults(), new ConversionRecipe { BlueprintId = "Odder", TimeSecs = 1f }));
        }

        // A freshly seeded file must carry the same two lists the absent-key fallback would supply,
        // so an operator editing it sees what is being excluded rather than an invisible rule.
        [Fact]
        public void SeededDefaultsJson_RoundTripsToTheSameExclusions()
        {
            var seeded = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(
                CapabilityRecipesConfig.DefaultsJson());
            Assert.Equal(RecipeExclusions.DefaultIgnoredBlueprints(), seeded.IgnoredBlueprints);
            Assert.Equal(RecipeExclusions.DefaultIgnoredInputKeys(),  seeded.IgnoredInputKeys);

            RecipeExclusions ex = RecipeExclusions.From(seeded);
            Assert.True(Excludes(ex, ScrapIngotToIronIngot()));
            Assert.False(Excludes(ex, ReactorComponent()));
        }
    }
}
