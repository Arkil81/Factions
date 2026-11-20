using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ItemPriceResolverTests
    {
        [Fact]
        public void ResolveExactPrices_UsesBlueprintChainForMissingItems()
        {
            var seeds = new Dictionary<string, long>
            {
                { ItemPriceResolver.ExactKey("MyObjectBuilder_Ore", "Iron"), 100 }
            };

            var recipes = new List<ConversionRecipe>
            {
                new ConversionRecipe
                {
                    BlueprintId = "IronIngot",
                    Inputs = new[]
                    {
                        new RecipeItem
                        {
                            TypeId = "MyObjectBuilder_Ore",
                            SubTypeId = "Iron",
                            Amount = 1
                        }
                    },
                    Outputs = new[]
                    {
                        new RecipeItem
                        {
                            TypeId = "MyObjectBuilder_Ingot",
                            SubTypeId = "Iron",
                            Amount = 1
                        }
                    }
                },
                new ConversionRecipe
                {
                    BlueprintId = "SteelPlate",
                    Inputs = new[]
                    {
                        new RecipeItem
                        {
                            TypeId = "MyObjectBuilder_Ingot",
                            SubTypeId = "Iron",
                            Amount = 2
                        }
                    },
                    Outputs = new[]
                    {
                        new RecipeItem
                        {
                            TypeId = "MyObjectBuilder_Component",
                            SubTypeId = "SteelPlate",
                            Amount = 1
                        }
                    }
                }
            };

            var resolved = ItemPriceResolver.ResolveExactPrices(recipes, seeds);

            Assert.Equal(100L, resolved[ItemPriceResolver.ExactKey("MyObjectBuilder_Ore", "Iron")]);
            Assert.Equal(100L, resolved[ItemPriceResolver.ExactKey("MyObjectBuilder_Ingot", "Iron")]);
            Assert.Equal(200L, resolved[ItemPriceResolver.ExactKey("MyObjectBuilder_Component", "SteelPlate")]);
        }

        [Fact]
        public void CollapseToSubtypePrices_PrefersHigherExactValueForSharedSubtype()
        {
            var exact = new Dictionary<string, long>
            {
                { ItemPriceResolver.ExactKey("MyObjectBuilder_Ore", "Iron"), 100 },
                { ItemPriceResolver.ExactKey("MyObjectBuilder_Ingot", "Iron"), 140 },
                { ItemPriceResolver.ExactKey("MyObjectBuilder_Component", "SteelPlate"), 200 },
            };

            var collapsed = ItemPriceResolver.CollapseToSubtypePrices(exact);

            Assert.Equal(140L, collapsed["Iron"]);
            Assert.Equal(200L, collapsed["SteelPlate"]);
        }

        [Fact]
        public void ExactKeyFromInventoryKey_NormalizesKnownPrefixes()
        {
            Assert.Equal("MyObjectBuilder_Ore/Iron", ItemPriceResolver.ExactKeyFromInventoryKey("Ore:Iron"));
            Assert.Equal("MyObjectBuilder_Ingot/Iron", ItemPriceResolver.ExactKeyFromInventoryKey("Ingot:Iron"));
            Assert.Equal("MyObjectBuilder_Component/SteelPlate", ItemPriceResolver.ExactKeyFromInventoryKey("Comp:SteelPlate"));
        }
    }
}
