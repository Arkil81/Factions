using System;
using System.Collections.Generic;
using System.Linq;

namespace Factions.Core
{
    /// <summary>
    /// Pure helper for resolving exact SE item prices from a seed set plus production recipes.
    /// Exact keys are stored as <c>TypeId/SubTypeId</c> so that colliding subtype names such as
    /// ore vs. ingot can still be priced independently before callers collapse back to the legacy
    /// subtype-only cache.
    /// </summary>
    public static class ItemPriceResolver
    {
        public static string ExactKey(string typeId, string subTypeId)
        {
            if (string.IsNullOrEmpty(typeId) || string.IsNullOrEmpty(subTypeId)) return null;
            return typeId + "/" + subTypeId;
        }

        public static string ExtractSubtype(string exactKey)
        {
            if (string.IsNullOrEmpty(exactKey)) return null;
            int slash = exactKey.IndexOf('/');
            return slash >= 0 && slash + 1 < exactKey.Length ? exactKey.Substring(slash + 1) : exactKey;
        }

        public static string ExactKeyFromInventoryKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.StartsWith("Ore:"))
                return ExactKey("MyObjectBuilder_Ore", key.Substring(4));
            if (key.StartsWith("Ingot:"))
                return ExactKey("MyObjectBuilder_Ingot", key.Substring(6));
            if (key.StartsWith("Comp:"))
                return ExactKey("MyObjectBuilder_Component", key.Substring(5));
            if (key.StartsWith("Gas:"))
                return ExactKey("MyObjectBuilder_GasProperties", key.Substring(4));
            if (InventoryKeyMapper.IsSeedKey(key))
                return ExactKey("MyObjectBuilder_SeedItem", key.Substring(InventoryKeyMapper.SeedPrefix.Length));
            if (key.StartsWith("HydrogenBottle"))
                return ExactKey("MyObjectBuilder_GasContainerObject", key);
            if (key.StartsWith("OxygenBottle"))
                return ExactKey("MyObjectBuilder_OxygenContainerObject", key);
            return null;
        }

        public static string StripInventoryPrefix(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (key.StartsWith("Ore:")) return key.Substring(4);
            if (key.StartsWith("Ingot:")) return key.Substring(6);
            if (key.StartsWith("Comp:")) return key.Substring(5);
            if (key.StartsWith("Gas:")) return key.Substring(4);
            return key;
        }

        public static Dictionary<string, long> ResolveExactPrices(
            IEnumerable<ConversionRecipe> recipes,
            IDictionary<string, long> seedExactPrices)
        {
            var prices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (seedExactPrices != null)
            {
                foreach (KeyValuePair<string, long> kv in seedExactPrices)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                        prices[kv.Key] = kv.Value;
            }

            if (recipes == null) return prices;

            var pending = new List<ConversionRecipe>();
            foreach (ConversionRecipe recipe in recipes)
                if (IsResolvableRecipe(recipe))
                    pending.Add(recipe);

            bool progress;
            int guard = 0;
            do
            {
                progress = false;
                guard++;

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    ConversionRecipe recipe = pending[i];
                    string outputKey;
                    double outputAmount;
                    if (!TryGetSingleOutput(recipe, out outputKey, out outputAmount))
                    {
                        pending.RemoveAt(i);
                        continue;
                    }

                    long candidate;
                    if (!TryResolveRecipe(recipe, prices, outputAmount, out candidate))
                        continue;

                    long existing;
                    if (!prices.TryGetValue(outputKey, out existing) || candidate < existing)
                    {
                        prices[outputKey] = candidate;
                        progress = true;
                    }

                    pending.RemoveAt(i);
                }
            }
            while (progress && pending.Count > 0 && guard < 256);

            return prices;
        }

        public static Dictionary<string, long> CollapseToSubtypePrices(
            IDictionary<string, long> exactPrices)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (exactPrices == null) return result;

            foreach (KeyValuePair<string, long> kv in exactPrices)
            {
                if (kv.Value <= 0) continue;
                string subtype = ExtractSubtype(kv.Key);
                if (string.IsNullOrEmpty(subtype)) continue;

                long existing;
                if (!result.TryGetValue(subtype, out existing) || kv.Value > existing)
                    result[subtype] = kv.Value;
            }
            return result;
        }

        private static bool IsResolvableRecipe(ConversionRecipe recipe)
        {
            string outputKey;
            double outputAmount;
            return TryGetSingleOutput(recipe, out outputKey, out outputAmount)
                && recipe.Inputs != null
                && recipe.Inputs.Length > 0
                && outputAmount > 0;
        }

        private static bool TryGetSingleOutput(ConversionRecipe recipe, out string outputKey, out double outputAmount)
        {
            outputKey = null;
            outputAmount = 0;
            if (recipe == null || recipe.Outputs == null || recipe.Outputs.Length != 1) return false;

            RecipeItem output = recipe.Outputs[0];
            outputKey = ExactKey(output.TypeId, output.SubTypeId);
            outputAmount = output.Amount;
            return !string.IsNullOrEmpty(outputKey) && outputAmount > 0;
        }

        private static bool TryResolveRecipe(ConversionRecipe recipe, IDictionary<string, long> prices,
            double outputAmount, out long candidate)
        {
            candidate = 0;
            long total = 0;
            foreach (RecipeItem input in recipe.Inputs)
            {
                long unitPrice;
                if (!TryLookupPrice(input, prices, out unitPrice))
                    return false;

                total += (long)Math.Round(input.Amount * unitPrice, MidpointRounding.AwayFromZero);
            }

            candidate = (long)Math.Round(total / outputAmount, MidpointRounding.AwayFromZero);
            return candidate > 0;
        }

        private static bool TryLookupPrice(RecipeItem item, IDictionary<string, long> prices, out long price)
        {
            price = 0;
            string exactKey = ExactKey(item.TypeId, item.SubTypeId);
            if (!string.IsNullOrEmpty(exactKey) && prices.TryGetValue(exactKey, out price))
                return true;

            string legacyKey = StripInventoryPrefix(item.SubTypeId);
            if (!string.IsNullOrEmpty(legacyKey) && prices.TryGetValue(legacyKey, out price))
                return true;

            return false;
        }
    }
}
