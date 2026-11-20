using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// How far an item's usefulness reaches <b>above</b> it in the recipe graph: how many further
    /// refinement steps stand on it, how many distinct things consume it, and in what quantity.
    ///
    /// <para><see cref="RecipeExpansion"/> answers the opposite question — what one unit of a key
    /// costs below it — and cannot be inverted cheaply per query, because a key's consumers are
    /// scattered across every recipe's input list. This walks the whole registry once and keeps the
    /// inverted edges.</para>
    ///
    /// <para><b>Direct consumers only, deliberately.</b> Summing a leaf's requirement over every
    /// root double-counts anything that roots many chains — <c>docs/demand-chains.md</c> names
    /// <c>Ingot:Iron</c> as exactly that case. Breadth here counts the distinct keys whose recipes
    /// take this one as an input, so a key that feeds thirty components scores thirty once rather
    /// than once per ship above them.</para>
    /// </summary>
    public sealed class ItemDownstreamIndex
    {
        /// <summary>Depth ceiling for the upward walk. The live catalogue is three deep; this only
        /// has to stop a malformed or modded graph, not bound the real one.</summary>
        private const int MaxSteps = 32;

        private sealed class Node
        {
            public readonly HashSet<string> Consumers = new HashSet<string>(StringComparer.Ordinal);
            public double QuantityAbove;
            public int Steps = -1;      // -1 = not yet resolved
            public bool Resolving;
        }

        private readonly Dictionary<string, Node> _byKey;
        private readonly Dictionary<string, double> _rawScore;
        private readonly double _maxRaw;

        private ItemDownstreamIndex(Dictionary<string, Node> byKey,
                                    Dictionary<string, double> rawScore, double maxRaw)
        {
            _byKey    = byKey;
            _rawScore = rawScore;
            _maxRaw   = maxRaw;
        }

        /// <summary>An index over an empty catalogue: every key terminal, every weight neutral.</summary>
        public static readonly ItemDownstreamIndex Empty = new ItemDownstreamIndex(
            new Dictionary<string, Node>(StringComparer.Ordinal),
            new Dictionary<string, double>(StringComparer.Ordinal), 0.0);

        /// <summary>Distinct keys whose recipes consume <paramref name="invKey"/> directly.
        /// 0 for a terminal product — nothing is made from it.</summary>
        public int ConsumerCount(string invKey)
        {
            Node n;
            return !string.IsNullOrEmpty(invKey) && _byKey.TryGetValue(invKey, out n) ? n.Consumers.Count : 0;
        }

        /// <summary>How many further refinement steps stand above <paramref name="invKey"/>: 0 for a
        /// terminal product, 1 for an ingot whose only consumer is a finished component, 2 when that
        /// component is itself built on.</summary>
        public int StepsAbove(string invKey)
        {
            Node n;
            if (string.IsNullOrEmpty(invKey) || !_byKey.TryGetValue(invKey, out n)) return 0;
            return n.Steps > 0 ? n.Steps : 0;
        }

        /// <summary>The item's reach above it, normalised across the whole catalogue to 0..1.
        /// 0 = nothing is made from this; 1 = the most widely and deeply consumed key there is.
        /// <para>A relative score rather than an absolute one so the scale follows whatever
        /// catalogue is loaded instead of a constant tuned against one mod set.</para></summary>
        public double Reach(string invKey)
        {
            if (_maxRaw <= 0 || string.IsNullOrEmpty(invKey)) return 0.0;
            double raw;
            if (!_rawScore.TryGetValue(invKey, out raw) || raw <= 0) return 0.0;
            double reach = raw / _maxRaw;
            return reach > 1.0 ? 1.0 : reach;
        }

        /// <summary>Inverts every recipe in <paramref name="recipes"/> into consumer edges and
        /// scores each key once. Null or empty yields <see cref="Empty"/>.</summary>
        public static ItemDownstreamIndex Build(IRecipeRegistry recipes)
        {
            if (recipes == null) return Empty;

            var byKey = new Dictionary<string, Node>(StringComparer.Ordinal);
            bool any = false;
            foreach (ConversionRecipe recipe in recipes.AllRecipes)
            {
                any = true;
                if (recipe == null || recipe.Inputs == null || recipe.Outputs == null) continue;
                foreach (RecipeItem input in recipe.Inputs)
                {
                    if (input.Amount <= 0) continue;
                    string inputKey = ProductionManager.InvKey(input);
                    if (string.IsNullOrEmpty(inputKey)) continue;

                    Node node;
                    if (!byKey.TryGetValue(inputKey, out node))
                    {
                        node = new Node();
                        byKey[inputKey] = node;
                    }
                    foreach (RecipeItem output in recipe.Outputs)
                    {
                        if (output.Amount <= 0) continue;
                        string outputKey = ProductionManager.InvKey(output);
                        if (string.IsNullOrEmpty(outputKey) || outputKey == inputKey) continue;
                        if (node.Consumers.Add(outputKey))
                            node.QuantityAbove += input.Amount / output.Amount;
                    }
                }
            }
            if (!any || byKey.Count == 0) return Empty;

            foreach (string key in new List<string>(byKey.Keys))
                ResolveSteps(byKey, key, 0);

            // Three terms, each compressed before they are added so no single one can swamp the
            // others: depth is small and linear, breadth and quantity are long-tailed and logged.
            var raw = new Dictionary<string, double>(byKey.Count, StringComparer.Ordinal);
            double maxRaw = 0;
            foreach (var kv in byKey)
            {
                Node n = kv.Value;
                if (n.Consumers.Count == 0) continue;
                double score = (n.Steps > 0 ? n.Steps : 0)
                             + Log2(1.0 + n.Consumers.Count)
                             + Log2(1.0 + n.QuantityAbove);
                raw[kv.Key] = score;
                if (score > maxRaw) maxRaw = score;
            }
            return new ItemDownstreamIndex(byKey, raw, maxRaw);
        }

        /// <summary>Longest upward path from <paramref name="key"/>, memoised. A cycle resolves to
        /// the depth already reached rather than recursing: the graph is not guaranteed acyclic
        /// (ice makes gas, and a modded recipe may close the loop).</summary>
        private static int ResolveSteps(Dictionary<string, Node> byKey, string key, int depth)
        {
            Node node;
            if (!byKey.TryGetValue(key, out node)) return 0;   // terminal: nothing consumes it
            if (node.Steps >= 0) return node.Steps;
            if (node.Resolving || depth >= MaxSteps) return 0;

            node.Resolving = true;
            int best = 0;
            foreach (string consumer in node.Consumers)
            {
                int above = ResolveSteps(byKey, consumer, depth + 1);
                if (above + 1 > best) best = above + 1;
            }
            node.Resolving = false;
            node.Steps = best;
            return best;
        }

        private static double Log2(double v)
        {
            return v <= 1.0 ? 0.0 : Math.Log(v) / Math.Log(2.0);
        }
    }
}
