using System;
using System.Collections.Generic;
using System.Text;

namespace Factions.Core
{
    /// <summary>Where one item could come from for a faction today: whether it makes it, how much it
    /// holds, and which other factions hold some. What the Stock-order item picker shows beside
    /// each item.</summary>
    public sealed class StockItemSource
    {
        /// <summary>A capability installed at one of the faction's own nodes has a recipe with this
        /// output.</summary>
        public bool OwnProduction;
        /// <summary>Units across the faction's own nodes.</summary>
        public long OwnStock;
        /// <summary>Units held by each other faction, by tag.</summary>
        public readonly Dictionary<string, long> OtherStock = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds <see cref="StockItemSource"/> for every item any source touches, for the Stock-order item
    /// picker (FAC-431). A read of current state, not a promise: a faction that holds an item may
    /// not sell it, and one that makes it may lack the inputs. It tells the player where supply could
    /// come from, not that it will.
    /// </summary>
    public static class StockItemSources
    {
        /// <summary>One pass over the graph's nodes and <paramref name="owner"/>'s capabilities.
        /// Items no source touches are absent from the result.</summary>
        public static Dictionary<string, StockItemSource> Build(FactionState owner, SpatialGraph graph,
            IDictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            var result = new Dictionary<string, StockItemSource>(StringComparer.Ordinal);
            if (owner == null || graph == null) return result;

            var ownCaps = new HashSet<NodeCapabilityKind>();
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null || string.IsNullOrEmpty(node.OwnerFactionTag)) continue;
                bool own = string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.Ordinal);
                if (own)
                    foreach (KeyValuePair<NodeCapabilityKind, int> cap in node.Capabilities)
                        if (cap.Value >= 1) ownCaps.Add(cap.Key);

                foreach (KeyValuePair<string, long> held in node.Inventory)
                {
                    if (held.Value <= 0) continue;
                    StockItemSource source = For(result, held.Key);
                    if (own) source.OwnStock += held.Value;
                    else
                    {
                        long prior;
                        source.OtherStock.TryGetValue(node.OwnerFactionTag, out prior);
                        source.OtherStock[node.OwnerFactionTag] = prior + held.Value;
                    }
                }
            }

            if (capRecipes != null)
                foreach (NodeCapabilityKind cap in ownCaps)
                {
                    List<ConversionRecipe> recipes;
                    if (!capRecipes.TryGetValue(cap, out recipes) || recipes == null) continue;
                    foreach (ConversionRecipe recipe in recipes)
                    {
                        if (recipe == null || recipe.Outputs == null) continue;
                        foreach (RecipeItem output in recipe.Outputs)
                        {
                            string key = InventoryKeyMapper.FromTypeIdAndSubtype(output.TypeId, output.SubTypeId);
                            if (key != null) For(result, key).OwnProduction = true;
                        }
                    }
                }
            return result;
        }

        /// <summary>"you make it; you hold 432; IGSL, METC hold it", holders largest first, at most
        /// <paramref name="maxHolders"/> named. "no known source" when nothing applies.</summary>
        public static string Describe(StockItemSource source, int maxHolders = 3)
        {
            if (source == null) return "no known source";
            var parts = new List<string>(3);
            if (source.OwnProduction) parts.Add("you make it");
            if (source.OwnStock > 0) parts.Add("you hold " + source.OwnStock);
            if (source.OtherStock.Count > 0)
            {
                var holders = new List<KeyValuePair<string, long>>(source.OtherStock);
                holders.Sort((a, b) =>
                {
                    int cmp = b.Value.CompareTo(a.Value);
                    return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
                });
                var sb = new StringBuilder();
                int named = Math.Min(maxHolders, holders.Count);
                for (int i = 0; i < named; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(holders[i].Key);
                }
                if (holders.Count > named) sb.Append(" +").Append(holders.Count - named);
                sb.Append(holders.Count == 1 ? " holds it" : " hold it");
                parts.Add(sb.ToString());
            }
            return parts.Count == 0 ? "no known source" : string.Join("; ", parts);
        }

        private static StockItemSource For(Dictionary<string, StockItemSource> result, string key)
        {
            StockItemSource source;
            if (!result.TryGetValue(key, out source))
            {
                source = new StockItemSource();
                result[key] = source;
            }
            return source;
        }
    }
}
