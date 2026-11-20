using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionRecipeSnapshotTests
    {
        [Fact]
        public void Capture_DeepCopiesRecipeListsAndItemsInOriginalOrder()
        {
            var first = new ConversionRecipe
            {
                BlueprintId = "First",
                TimeSecs = 2.5f,
                Inputs = new[] { new RecipeItem { TypeId = "Ore", SubTypeId = "Iron", Amount = 3 } },
                Outputs = new[] { new RecipeItem { TypeId = "Ingot", SubTypeId = "Iron", Amount = 2 } }
            };
            var recipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Refinery, new List<ConversionRecipe> { first } }
            };

            ProductionRecipeSnapshot snapshot = ProductionRecipeSnapshot.Capture(recipes);

            first.BlueprintId = "Mutated";
            first.Inputs[0].Amount = 99;
            recipes[NodeCapabilityKind.Refinery].Add(new ConversionRecipe { BlueprintId = "Second" });

            IReadOnlyList<FrozenConversionRecipe> captured = snapshot.RecipesFor(NodeCapabilityKind.Refinery);
            Assert.Single(captured);
            Assert.Equal("First", captured[0].BlueprintId);
            Assert.Equal(2.5f, captured[0].TimeSecs);
            Assert.Single(captured[0].Inputs);
            Assert.Equal("Ore", captured[0].Inputs[0].TypeId);
            Assert.Equal("Iron", captured[0].Inputs[0].SubTypeId);
            Assert.Equal(3, captured[0].Inputs[0].Amount);
            Assert.Equal("Ingot", captured[0].Outputs[0].TypeId);
        }
    }
}
