using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Which of a Farm's recipes may run at a given node. A Farm recipe grows one crop from ice
    /// (the host builds one per seed a farm plot accepts, from SE's own farm-plot and seed
    /// definitions), and whether that crop grows <i>here</i> depends on the node, not the
    /// capability: docs/food-and-population.md.
    ///
    /// <list type="bullet">
    /// <item>An NPC farm grows any crop native to its body with no seed stock — the body's
    /// forageables (<see cref="PlanetRecord.NativeCrops"/>). That is the food floor: "you can always
    /// grow shrooms" on Agni, and everything on Tellus.</item>
    /// <item>Any other crop needs its seed (<c>Seed:&lt;crop&gt;</c>) in the node's stock. The farm
    /// keeps its own replant seed, so one seed is enough and none is consumed.</item>
    /// <item>A player faction's farm is real SE blocks planted with real seeds, so it always needs
    /// the seed, native or not.</item>
    /// </list>
    /// </summary>
    public static class FarmCrops
    {
        /// <summary>Prefix of every Farm recipe id: <c>Farm/&lt;block&gt;/&lt;seed&gt;</c>.</summary>
        public const string RecipePrefix = "Farm/";

        public static bool IsFarmRecipe(ConversionRecipe recipe)
        {
            return recipe != null && recipe.BlueprintId != null
                && recipe.BlueprintId.StartsWith(RecipePrefix, StringComparison.Ordinal);
        }

        /// <summary>The seed subtype a Farm recipe plants — the last segment of its id — or null
        /// when the recipe is not a Farm recipe.</summary>
        public static string SeedOf(ConversionRecipe recipe)
        {
            if (!IsFarmRecipe(recipe)) return null;
            string id = recipe.BlueprintId;
            int slash = id.LastIndexOf('/');
            return slash >= RecipePrefix.Length && slash + 1 < id.Length ? id.Substring(slash + 1) : null;
        }

        /// <summary>Whether a crop grows at a node. <paramref name="seedsHeld"/> is the node's stock
        /// of that crop's seed.</summary>
        public static bool Grows(string seed, string[] nativeCrops, bool seedsOnly, long seedsHeld)
        {
            if (string.IsNullOrEmpty(seed)) return false;
            if (seedsHeld > 0) return true;
            if (seedsOnly || nativeCrops == null) return false;
            for (int i = 0; i < nativeCrops.Length; i++)
                if (string.Equals(nativeCrops[i], seed, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The crops native to the body a node stands on or orbits, or null for a node in
        /// deep space or on a body where nothing grows wild.</summary>
        public static string[] NativeAt(SpatialNode node, IPlanetCatalogue planets)
        {
            if (node == null || string.IsNullOrEmpty(node.PlanetId) || planets == null) return null;
            foreach (PlanetRecord p in planets.Planets)
                if (p != null && p.Id == node.PlanetId) return p.NativeCrops;
            return null;
        }

        /// <summary>How many distinct crops a Farm at <paramref name="node"/> could grow today, with no
        /// seed brought in: those native to its body plus those whose seed the node holds.
        /// <paramref name="seedsOnly"/> counts held seeds alone, which is a player faction's case.</summary>
        public static int GrowableCount(SpatialNode node, IPlanetCatalogue planets, bool seedsOnly = false)
        {
            if (node == null) return 0;
            var crops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] native = seedsOnly ? null : NativeAt(node, planets);
            if (native != null)
                foreach (string c in native)
                    if (!string.IsNullOrEmpty(c)) crops.Add(c);
            if (node.Inventory != null)
                foreach (KeyValuePair<string, long> kv in node.Inventory)
                    if (kv.Value > 0 && InventoryKeyMapper.IsSeedKey(kv.Key))
                        crops.Add(kv.Key.Substring(InventoryKeyMapper.SeedPrefix.Length));
            return crops.Count;
        }

        /// <summary>The four crops SE's farm plot accepts.</summary>
        public const int VanillaCropCount = 4;

        /// <summary>How much a Farm at <paramref name="node"/> is preferred, in (0, 1]. A Farm grows
        /// any crop given its seed, and a native crop is a seed it never has to fetch, so every node
        /// is eligible and the free seeds rank them: with no crop named, Tellus (all four) 1, Agni
        /// (mushrooms) 0.4, Kor 0.2. A request naming <paramref name="crop"/> weighs that crop growing
        /// here as much as all four together, so the Farm goes where the crop it was asked for is
        /// free before it goes where the most others are.</summary>
        public static double SitingPreference(SpatialNode node, IPlanetCatalogue planets, string crop)
        {
            double score = 1 + GrowableCount(node, planets);
            double max = 1 + VanillaCropCount;
            if (!string.IsNullOrEmpty(crop))
            {
                max += VanillaCropCount;
                long held = 0;
                if (node != null) node.Inventory.TryGetValue(InventoryKeyMapper.SeedPrefix + crop, out held);
                if (Grows(crop, NativeAt(node, planets), false, held)) score += VanillaCropCount;
            }
            return Math.Min(1.0, score / max);
        }

        /// <summary><paramref name="candidates"/> less every Farm recipe whose crop does not grow at
        /// <paramref name="node"/>. Returns the input list itself when nothing is dropped, so a node
        /// that can grow everything allocates nothing.</summary>
        public static List<ConversionRecipe> GrowableAt(List<ConversionRecipe> candidates, SpatialNode node,
            string[] nativeCrops, bool seedsOnly, Func<string, long> stock)
        {
            if (candidates == null || candidates.Count == 0) return candidates;
            List<ConversionRecipe> kept = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                ConversionRecipe r = candidates[i];
                bool keep = true;
                if (IsFarmRecipe(r))
                {
                    string seed = SeedOf(r);
                    long held = stock != null && seed != null ? stock(InventoryKeyMapper.SeedPrefix + seed) : 0;
                    keep = Grows(seed, nativeCrops, seedsOnly, held);
                }
                if (keep)
                {
                    if (kept != null) kept.Add(r);
                }
                else if (kept == null)
                {
                    kept = new List<ConversionRecipe>(candidates.Count);
                    for (int j = 0; j < i; j++) kept.Add(candidates[j]);
                }
            }
            return kept ?? candidates;
        }
    }
}
