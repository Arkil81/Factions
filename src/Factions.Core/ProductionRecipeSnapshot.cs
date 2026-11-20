using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Factions.Core
{
    /// <summary>Immutable recipe item captured for a staged Production transaction.</summary>
    public sealed class FrozenRecipeItem
    {
        internal FrozenRecipeItem(RecipeItem item)
        {
            TypeId = item.TypeId;
            SubTypeId = item.SubTypeId;
            Amount = item.Amount;
        }

        public string TypeId { get; private set; }
        public string SubTypeId { get; private set; }
        public double Amount { get; private set; }
    }

    /// <summary>Immutable conversion recipe captured for a staged Production transaction.</summary>
    public sealed class FrozenConversionRecipe
    {
        private readonly ReadOnlyCollection<FrozenRecipeItem> _inputs;
        private readonly ReadOnlyCollection<FrozenRecipeItem> _outputs;

        internal FrozenConversionRecipe(ConversionRecipe recipe)
        {
            BlueprintId = recipe.BlueprintId;
            TimeSecs = recipe.TimeSecs;
            _inputs = FreezeItems(recipe.Inputs);
            _outputs = FreezeItems(recipe.Outputs);
        }

        public string BlueprintId { get; private set; }
        public float TimeSecs { get; private set; }
        public IReadOnlyList<FrozenRecipeItem> Inputs { get { return _inputs; } }
        public IReadOnlyList<FrozenRecipeItem> Outputs { get { return _outputs; } }

        private static ReadOnlyCollection<FrozenRecipeItem> FreezeItems(RecipeItem[] source)
        {
            if (source == null) return null;
            var items = new List<FrozenRecipeItem>(source.Length);
            for (int i = 0; i < source.Length; i++) items.Add(new FrozenRecipeItem(source[i]));
            return new ReadOnlyCollection<FrozenRecipeItem>(items);
        }
    }

    /// <summary>
    /// Immutable deep copy of the host-resolved capability recipes used by Production. A staged cycle
    /// captures this at begin time so recipe-list replacement or mutable recipe-array edits cannot
    /// change its plan before guarded commit.
    /// </summary>
    public sealed class ProductionRecipeSnapshot
    {
        private static readonly IReadOnlyList<FrozenConversionRecipe> s_empty =
            new ReadOnlyCollection<FrozenConversionRecipe>(new List<FrozenConversionRecipe>());

        private readonly Dictionary<NodeCapabilityKind, IReadOnlyList<FrozenConversionRecipe>> _recipes;

        private ProductionRecipeSnapshot(Dictionary<NodeCapabilityKind, IReadOnlyList<FrozenConversionRecipe>> recipes)
        {
            _recipes = recipes;
        }

        public static ProductionRecipeSnapshot Capture(Dictionary<NodeCapabilityKind, List<ConversionRecipe>> source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var copy = new Dictionary<NodeCapabilityKind, IReadOnlyList<FrozenConversionRecipe>>(source.Count);
            foreach (KeyValuePair<NodeCapabilityKind, List<ConversionRecipe>> entry in source)
            {
                if (entry.Value == null)
                {
                    copy.Add(entry.Key, null);
                    continue;
                }

                var frozen = new List<FrozenConversionRecipe>(entry.Value.Count);
                foreach (ConversionRecipe recipe in entry.Value)
                    frozen.Add(recipe == null ? null : new FrozenConversionRecipe(recipe));
                copy.Add(entry.Key, new ReadOnlyCollection<FrozenConversionRecipe>(frozen));
            }
            return new ProductionRecipeSnapshot(copy);
        }

        /// <summary>Recipes retain the source list's ordering; absent capabilities return an empty list.</summary>
        public IReadOnlyList<FrozenConversionRecipe> RecipesFor(NodeCapabilityKind capability)
        {
            IReadOnlyList<FrozenConversionRecipe> recipes;
            return _recipes.TryGetValue(capability, out recipes) ? recipes : s_empty;
        }
    }
}
