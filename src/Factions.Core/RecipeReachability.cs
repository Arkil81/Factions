using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Which materials the economy can actually obtain, and therefore which recipes can ever run.
    ///
    /// <para>Seeded by what this world can supply -- <see cref="FactionTuning.NodeYieldTable"/>,
    /// including <see cref="NodeYieldEntry.MineOnly"/> entries (a Mine run is a real source), plus
    /// every ore the geography can place -- and closed over
    /// the recipe set: a recipe whose inputs are all obtainable is admitted, and its outputs become
    /// obtainable in turn, to a fixed point. Anything left outside the closure has no source in this
    /// world.</para>
    ///
    /// <para>This replaces hand-curated name lists for the "this recipe can never run" question. The
    /// two entries <see cref="FactionTuning"/>-era code shipped -- scrap ore and the scrap-ingot
    /// blueprint -- were each justified by exactly this computation, evaluated once by hand; a mod
    /// that injects a player-only ore into a vanilla blueprint class produces the same shape and
    /// would otherwise need its own entry. Deriving it means the answer tracks the world instead of
    /// the list. See docs/mod-compatibility.md.</para>
    /// </summary>
    public static class RecipeReachability
    {
        /// <summary>The exact <c>TypeId/SubTypeId</c> keys this world can supply: what the yield
        /// table states, plus every ore its geography can place.
        ///
        /// <para><b>Both halves are needed and neither is sufficient.</b> The table states the things
        /// that are not geography — stone, which is the base voxel material anywhere there is rock at
        /// all. Every other ore comes from the ground, so <see cref="IOreGeography.MintableOres"/> is
        /// what answers for it. Seeding from the table alone made the closure stone-only the moment
        /// FAC-102 shrank that table, which excluded the whole ore-to-ingot tier and everything rooted
        /// in it. See FAC-111.</para>
        ///
        /// <para><paramref name="geographyOres"/> are bare subtypes ("Iron", "Ice") and are read as
        /// <see cref="ItemKind.Ore"/>; rarity is a price-derived property of a deposit rather than a
        /// separate inventory family, so there is no second prefix to choose between. Yield entries
        /// are already in the prefixed inventory form (<c>Ore:Iron</c>). Either way
        /// <see cref="ItemPriceResolver.ExactKeyFromInventoryKey"/> is the single conversion to
        /// recipe-input form, and a key whose prefix it does not recognise is skipped rather than
        /// guessed at.</para>
        ///
        /// <para>A null or empty <paramref name="geographyOres"/> is a world with no ore geography
        /// wired — the offline sim and unit tests — and leaves the table as the whole seed, which is
        /// the pre-existing behaviour for those callers.</para></summary>
        public static HashSet<string> MintedExactKeys(FactionTuning tuning,
                                                      IEnumerable<string> geographyOres)
        {
            var minted = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            if (tuning != null && tuning.NodeYieldTable != null)
            {
                foreach (NodeYieldEntry entry in tuning.NodeYieldTable)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.SubTypeId)) continue;
                    string exact = ItemPriceResolver.ExactKeyFromInventoryKey(entry.SubTypeId);
                    if (!string.IsNullOrEmpty(exact)) minted.Add(exact);
                }
            }

            if (geographyOres != null)
            {
                foreach (string subType in geographyOres)
                {
                    if (string.IsNullOrEmpty(subType)) continue;
                    string exact = ItemPriceResolver.ExactKeyFromInventoryKey("Ore:" + subType);
                    if (!string.IsNullOrEmpty(exact)) minted.Add(exact);
                }
            }
            return minted;
        }

        /// <summary>Closes <paramref name="mintedExactKeys"/> over <paramref name="recipes"/>: every
        /// material obtainable by minting, or by any chain of recipes rooted in it. A recipe counts
        /// once all of its inputs are obtainable, so the pass repeats until nothing new is added --
        /// bounded by the recipe count, since each pass that changes anything admits at least one
        /// recipe.</summary>
        public static HashSet<string> Resolve(IEnumerable<string> mintedExactKeys,
                                              IEnumerable<ConversionRecipe> recipes)
        {
            var reachable = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (mintedExactKeys != null)
                foreach (string key in mintedExactKeys)
                    if (!string.IsNullOrEmpty(key)) reachable.Add(key);

            var pending = new List<ConversionRecipe>();
            if (recipes != null)
                foreach (ConversionRecipe r in recipes)
                    if (r != null) pending.Add(r);

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < pending.Count; i++)
                {
                    ConversionRecipe r = pending[i];
                    if (r == null) continue;
                    if (!InputsAllReachable(r, reachable)) continue;

                    pending[i] = null;                  // admitted; never re-tested
                    if (r.Outputs != null)
                    {
                        foreach (RecipeItem output in r.Outputs)
                        {
                            string key = RecipeExclusionKey(output);
                            if (key != null && reachable.Add(key)) changed = true;
                        }
                    }
                    changed = true;
                }
            }
            return reachable;
        }

        /// <summary>True when every input of <paramref name="recipe"/> is in
        /// <paramref name="reachable"/>. A recipe with no inputs is trivially reachable; an input
        /// missing either half of its key cannot be matched and is treated as reachable rather than
        /// blocking a recipe on a malformed definition.</summary>
        public static bool InputsAllReachable(ConversionRecipe recipe, HashSet<string> reachable)
        {
            if (recipe == null || recipe.Inputs == null || recipe.Inputs.Length == 0) return true;
            if (reachable == null) return false;

            foreach (RecipeItem input in recipe.Inputs)
            {
                string key = RecipeExclusionKey(input);
                if (key == null) continue;
                if (!reachable.Contains(key)) return false;
            }
            return true;
        }

        /// <summary>An item's fully qualified <c>TypeId/SubTypeId</c>, or null when either half is
        /// missing. Matches the key form the operator's own exclusion lists are written in.</summary>
        public static string RecipeExclusionKey(RecipeItem item)
        {
            if (string.IsNullOrEmpty(item.TypeId) || string.IsNullOrEmpty(item.SubTypeId)) return null;
            return item.TypeId + "/" + item.SubTypeId;
        }
    }
}
