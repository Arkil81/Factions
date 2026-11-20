using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One ingredient in a <see cref="ConversionRecipe"/> — either an input or an output.</summary>
    public struct RecipeItem
    {
        /// <summary>SE TypeId string, e.g. "MyObjectBuilder_Ore".</summary>
        public string TypeId;
        /// <summary>SE SubtypeName, e.g. "Iron".</summary>
        public string SubTypeId;
        /// <summary>Amount as a double. For Ore/Ingot TypeIds this is volume in m³;
        /// for Component/Component-like TypeIds this is a unit count.</summary>
        public double Amount;
    }

    /// <summary>A single production recipe extracted from an SE blueprint definition.</summary>
    public sealed class ConversionRecipe
    {
        /// <summary>Blueprint subtype name — unique identifier (e.g. "SteelPlateBlueprint").</summary>
        public string BlueprintId;
        public RecipeItem[] Inputs;
        public RecipeItem[] Outputs;
        /// <summary>Base production time in seconds (unmodified by machine speed multipliers).</summary>
        public float TimeSecs;

        /// <summary>SE <c>MyBlueprintDefinitionBase.IgnoreEfficiencyMultiplier</c>: this blueprint's
        /// prerequisites are charged in full, ignoring the world's assembler efficiency multiplier.
        /// Set on the hand tools and weapons ManufactoryII builds.</summary>
        public bool IgnoresEfficiencyMultiplier;
    }

    /// <summary>
    /// Read-only access to the full set of item-conversion recipes loaded from SE definitions.
    /// Indexed by output item so callers can ask "what does it take to produce X?".
    /// The SE plugin wires <c>SeRecipeRegistry</c> at startup; tests use <see cref="NullRecipeRegistry"/>.
    /// </summary>
    public interface IRecipeRegistry
    {
        /// <summary>All recipes that produce the given output TypeId + SubTypeId.
        /// Returns an empty enumerable when nothing is mapped.</summary>
        IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId);

        /// <summary>All recipes in the registry.</summary>
        IEnumerable<ConversionRecipe> AllRecipes { get; }

        /// <summary>Total number of recipes.</summary>
        int Count { get; }
    }

    /// <summary>No-op <see cref="IRecipeRegistry"/> for the offline sim and unit tests.</summary>
    public sealed class NullRecipeRegistry : IRecipeRegistry
    {
        public static readonly NullRecipeRegistry Instance = new NullRecipeRegistry();

        private static readonly ConversionRecipe[] s_empty = new ConversionRecipe[0];

        public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
        {
            return s_empty;
        }

        public IEnumerable<ConversionRecipe> AllRecipes { get { return s_empty; } }
        public int Count { get { return 0; } }
    }
}
