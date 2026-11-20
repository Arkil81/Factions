using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// What one unit of a producible item costs, transitively, in the materials only mining can
    /// supply. The static half of a demand chain (docs/demand-chains.md): a live chain is then a
    /// lookup here and a scalar multiply, never a graph walk inside the tick.
    ///
    /// <para><b>Leaves.</b> A key no recipe in this table produces. That is the same fact
    /// <see cref="RecipeReachability"/> closes over, read from the other end: reachability asks
    /// whether a key can be obtained at all, this asks what obtaining it costs.</para>
    ///
    /// <para><b>Route choice.</b> <see cref="IRecipeRegistry.GetRecipesFor"/> can return several
    /// producers of one key. Measured against the live capability set (2026-08-31, 187 recipes over
    /// 162 producible keys): all but three of those are the same blueprint registered on two machine
    /// tiers, which is not a choice of material route at all. The three that are —
    /// <c>Ingot:Iron</c>, <c>Ingot:Nickel</c>, <c>Ingot:Silicon</c> — each offer a dedicated
    /// single-output ore recipe against <c>StoneOreToIngot</c>, the one multi-output recipe in the
    /// set. So the rule is: <b>prefer a single-output recipe; use a joint-product recipe only when it
    /// is the sole producer</b>, charging its whole input to the requested output with no by-product
    /// credit. That leaves every key with exactly one route and keeps this table independent of any
    /// one faction's holdings.</para>
    ///
    /// <para><b>Machine tier is not neutral.</b> Refinery I stamps
    /// <see cref="MachineProfile.MaterialEfficiency"/> 0.70 against RefineryII's 1.00, so the same
    /// ore-to-ingot recipe costs 43% more ore on the lower tier; assemblers divide their inputs by
    /// <see cref="MachineProfile.InputEfficiencyDivisor"/>. A table is therefore built for one
    /// capability set and is only valid for a faction holding it —
    /// <see cref="RecipeExpansionCache"/> keeps one per set.</para>
    /// </summary>
    public sealed class RecipeExpansion
    {
        /// <summary>Expansion depth ceiling. The live catalogue is three deep (component, ingot,
        /// ore); this only has to stop a malformed or modded catalogue, not bound the real one.</summary>
        public const int MaxDepth = 32;

        /// <summary>The recipe chosen to produce one key, and the capability whose machine model
        /// prices it. Provenance — a chain quantity is unreadable without knowing which route
        /// produced it.</summary>
        public struct Route
        {
            public ConversionRecipe Recipe;
            public NodeCapabilityKind Cap;
        }

        private static readonly Dictionary<string, double> s_empty = new Dictionary<string, double>();

        private readonly Dictionary<string, Route> _routes;
        private readonly Dictionary<string, Dictionary<string, double>> _perUnit;
        private readonly Dictionary<string, Dictionary<string, double>> _directInputs;
        private readonly Dictionary<string, int> _heights;
        private readonly int _maxHeight;

        private RecipeExpansion(Dictionary<string, Route> routes,
                                Dictionary<string, Dictionary<string, double>> perUnit,
                                Dictionary<string, Dictionary<string, double>> directInputs,
                                Dictionary<string, int> heights,
                                int maxHeight)
        {
            _routes       = routes;
            _perUnit      = perUnit;
            _directInputs = directInputs;
            _heights      = heights;
            _maxHeight    = maxHeight;
        }

        /// <summary>Inventory keys this table can decompose. Everything else is a leaf.</summary>
        public ICollection<string> ProducibleKeys { get { return _routes.Keys; } }

        /// <summary>How many keys this table decomposes.</summary>
        public int Count { get { return _routes.Count; } }

        /// <summary>True when no recipe in this capability set produces <paramref name="invKey"/> —
        /// only a Mine run, a trade or a gift can close it.</summary>
        public bool IsLeaf(string invKey)
        {
            return string.IsNullOrEmpty(invKey) || !_routes.ContainsKey(invKey);
        }

        /// <summary>The route chosen for <paramref name="invKey"/>, or false when it is a leaf.</summary>
        public bool TryGetRoute(string invKey, out Route route)
        {
            route = default(Route);
            if (string.IsNullOrEmpty(invKey)) return false;
            return _routes.TryGetValue(invKey, out route);
        }

        /// <summary>Height of the tallest chain above the leaves that ends at
        /// <paramref name="invKey"/>: 0 for a leaf, 1 for an ingot over ore, 2 for a component over
        /// both. A walk that visits keys in descending height sees every consumer of a key before
        /// the key itself, which is what lets <see cref="DemandChain"/> net one level at a time
        /// without revisiting.</summary>
        public int Height(string invKey)
        {
            int height;
            if (!string.IsNullOrEmpty(invKey) && _heights.TryGetValue(invKey, out height)) return height;
            return 0;
        }

        /// <summary>The tallest chain in this table. 0 when nothing is producible.</summary>
        public int MaxHeight { get { return _maxHeight; } }

        /// <summary>What one unit of <paramref name="invKey"/> costs at its <b>own</b> level only —
        /// the route's direct inputs, priced by its machine, not decomposed further. False for a
        /// leaf.
        /// <para>Where <see cref="RequirementPerUnit"/> answers "what does this cost in ore",
        /// this answers "what does the next hop down want", which is the question netting per level
        /// asks. The returned map is shared and must not be mutated.</para></summary>
        public bool TryGetDirectInputs(string invKey, out IReadOnlyDictionary<string, double> perUnit)
        {
            perUnit = null;
            if (string.IsNullOrEmpty(invKey)) return false;
            Dictionary<string, double> found;
            if (!_directInputs.TryGetValue(invKey, out found)) return false;
            perUnit = found;
            return true;
        }

        /// <summary>Leaf materials required per unit of <paramref name="invKey"/>, keyed by inventory
        /// key. A leaf expands to one unit of itself, so every key has an answer and a caller never
        /// has to branch on producibility to size a chain.
        /// <para>The returned map is shared and must not be mutated.</para></summary>
        public IReadOnlyDictionary<string, double> RequirementPerUnit(string invKey)
        {
            if (string.IsNullOrEmpty(invKey)) return s_empty;
            Dictionary<string, double> req;
            if (_perUnit.TryGetValue(invKey, out req)) return req;

            var self = new Dictionary<string, double>(1, StringComparer.Ordinal);
            self[invKey] = 1.0;
            return self;
        }

        /// <summary>Builds the table for one capability set.
        /// <paramref name="availableCaps"/> null means "every capability in
        /// <paramref name="capRecipes"/>" — the whole-catalogue view the offline tools want.</summary>
        /// <param name="models">One <see cref="MachineModel"/> per <see cref="NodeCapabilityKind"/>,
        /// as <see cref="MachinePhysics.ResolveAll"/> returns. A capability with no model priced at
        /// neutral (no efficiency scaling), which is what the unstamped sim already assumes.</param>
        public static RecipeExpansion Build(
            IDictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes,
            IEnumerable<NodeCapabilityKind> availableCaps,
            MachineModel[] models)
        {
            var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
            if (capRecipes != null)
            {
                HashSet<NodeCapabilityKind> allowed = null;
                if (availableCaps != null)
                {
                    allowed = new HashSet<NodeCapabilityKind>();
                    foreach (NodeCapabilityKind cap in availableCaps) allowed.Add(cap);
                }

                var candidates = new Dictionary<string, List<Route>>(StringComparer.Ordinal);
                foreach (var entry in capRecipes)
                {
                    if (allowed != null && !allowed.Contains(entry.Key)) continue;
                    if (entry.Value == null) continue;
                    foreach (ConversionRecipe recipe in entry.Value)
                    {
                        if (recipe == null || recipe.Outputs == null) continue;
                        foreach (RecipeItem output in recipe.Outputs)
                        {
                            if (output.Amount <= 0) continue;
                            string key = InvKey(output);
                            if (string.IsNullOrEmpty(key)) continue;

                            List<Route> list;
                            if (!candidates.TryGetValue(key, out list))
                            {
                                list = new List<Route>();
                                candidates[key] = list;
                            }
                            list.Add(new Route { Recipe = recipe, Cap = entry.Key });
                        }
                    }
                }

                foreach (var entry in candidates)
                {
                    Route chosen;
                    if (TryChooseRoute(entry.Value, models, out chosen))
                        routes[entry.Key] = chosen;
                }
            }

            PruneCyclicKeys(routes);

            var perUnit = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (string key in routes.Keys)
                Expand(key, routes, models, perUnit, 0);

            var directInputs = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (var entry in routes)
                directInputs[entry.Key] = DirectInputsOf(entry.Key, entry.Value, models);

            var heights = new Dictionary<string, int>(StringComparer.Ordinal);
            int maxHeight = 0;
            foreach (string key in routes.Keys)
            {
                int height = HeightOf(key, directInputs, heights, 0);
                if (height > maxHeight) maxHeight = height;
            }

            return new RecipeExpansion(routes, perUnit, directInputs, heights, maxHeight);
        }

        /// <summary>Drops every key that sits on a recipe cycle, leaving an acyclic graph behind.
        /// A cycled key becomes a leaf: nothing grounded in mined material produces it, so a chain
        /// that reaches one stops there, exactly as it stops at ore.
        ///
        /// <para>This is the cycle detection docs/demand-chains.md expects to fall out of the
        /// structure. It has to run <em>before</em> the expansion rather than filtering its results,
        /// because a key sitting above a cycle has a real requirement for the cycled input and an
        /// expansion that walked into the loop first would have folded a lap of it into that
        /// number.</para>
        ///
        /// <para>Whole cycles go, never just the edge that closed one: dropping a single member
        /// would leave the rest decomposable through it, and which member survived would depend on
        /// dictionary order.</para></summary>
        private static void PruneCyclicKeys(Dictionary<string, Route> routes)
        {
            var cyclic = new List<string>();
            var seen   = new HashSet<string>(StringComparer.Ordinal);
            var stack  = new List<string>();

            foreach (string key in routes.Keys)
            {
                // Reaches itself, walking only producible inputs? Then it is on a cycle. Cheap at the
                // catalogue's real size (162 keys, three levels) and run once per capability set.
                seen.Clear();
                stack.Clear();
                PushInputs(key, routes, stack);

                bool onCycle = false;
                while (stack.Count > 0)
                {
                    int last = stack.Count - 1;
                    string next = stack[last];
                    stack.RemoveAt(last);

                    if (string.Equals(next, key, StringComparison.Ordinal)) { onCycle = true; break; }
                    if (!seen.Add(next)) continue;
                    PushInputs(next, routes, stack);
                }
                if (onCycle) cyclic.Add(key);
            }

            foreach (string key in cyclic) routes.Remove(key);
        }

        /// <summary>Pushes the producible inputs of <paramref name="key"/>'s route onto
        /// <paramref name="stack"/>. Leaves are not pushed: nothing continues through them.</summary>
        private static void PushInputs(string key, Dictionary<string, Route> routes, List<string> stack)
        {
            Route route;
            if (!routes.TryGetValue(key, out route)) return;
            if (route.Recipe == null || route.Recipe.Inputs == null) return;

            foreach (RecipeItem input in route.Recipe.Inputs)
            {
                if (input.Amount <= 0) continue;
                string inputKey = InvKey(input);
                if (string.IsNullOrEmpty(inputKey)) continue;
                if (routes.ContainsKey(inputKey)) stack.Add(inputKey);
            }
        }

        /// <summary>Picks the one route for a key. See the class remarks for why the shape of this
        /// is a ranking and not a policy hook: on the live catalogue every comparison below the first
        /// is a tie-break that never fires, and it exists so a modded catalogue resolves
        /// deterministically rather than by dictionary order.</summary>
        private static bool TryChooseRoute(List<Route> candidates, MachineModel[] models, out Route chosen)
        {
            chosen = default(Route);
            if (candidates == null || candidates.Count == 0) return false;

            bool have = false;
            foreach (Route candidate in candidates)
            {
                if (!have) { chosen = candidate; have = true; continue; }
                if (BeatsIncumbent(candidate, chosen, models)) chosen = candidate;
            }
            return have;
        }

        private static bool BeatsIncumbent(Route candidate, Route incumbent, MachineModel[] models)
        {
            // Same blueprint on two machine tiers is one route priced two ways: take the better
            // machine. Output scaling dominates -- it is the term that differs between the refinery
            // tiers -- then the input divisor, then the ordinal so the answer is stable.
            if (string.Equals(candidate.Recipe.BlueprintId, incumbent.Recipe.BlueprintId, StringComparison.Ordinal))
            {
                MachineModel a = ModelFor(models, candidate.Cap);
                MachineModel b = ModelFor(models, incumbent.Cap);
                if (a.OutputScale  != b.OutputScale)  return a.OutputScale > b.OutputScale;
                if (a.InputDivisor != b.InputDivisor) return a.InputDivisor > b.InputDivisor;
                return candidate.Cap < incumbent.Cap;
            }

            // A joint-product recipe is opportunistic supply, not a planned route: nobody refines
            // 31,000 stone to obtain 930 iron ingots and three unwanted co-products. Only take one
            // when it is all there is.
            int candidateOutputs = PositiveOutputCount(candidate.Recipe);
            int incumbentOutputs = PositiveOutputCount(incumbent.Recipe);
            bool candidateSingle = candidateOutputs == 1;
            bool incumbentSingle = incumbentOutputs == 1;
            if (candidateSingle != incumbentSingle) return candidateSingle;

            int candidateInputs = PositiveInputCount(candidate.Recipe);
            int incumbentInputs = PositiveInputCount(incumbent.Recipe);
            if (candidateInputs != incumbentInputs) return candidateInputs < incumbentInputs;

            return string.CompareOrdinal(candidate.Recipe.BlueprintId, incumbent.Recipe.BlueprintId) < 0;
        }

        /// <summary>Leaf requirement per unit of <paramref name="key"/>. The graph is acyclic by the
        /// time this runs, so a memoized answer is context-free and the depth bound is only a guard
        /// against a catalogue this cannot answer for.</summary>
        private static Dictionary<string, double> Expand(
            string key,
            Dictionary<string, Route> routes,
            MachineModel[] models,
            Dictionary<string, Dictionary<string, double>> memo,
            int depth)
        {
            Dictionary<string, double> done;
            if (memo.TryGetValue(key, out done)) return done;

            Route route;
            if (depth >= MaxDepth || !routes.TryGetValue(key, out route)) return Self(key);

            double outputPerBatch = OutputPerBatch(route, models, key);
            if (outputPerBatch <= 0) return Self(key);

            MachineModel model = ModelFor(models, route.Cap);
            double batchesPerUnit = 1.0 / outputPerBatch;

            var total = new Dictionary<string, double>(StringComparer.Ordinal);
            if (route.Recipe.Inputs != null)
            {
                foreach (RecipeItem input in route.Recipe.Inputs)
                {
                    if (input.Amount <= 0) continue;
                    string inputKey = InvKey(input);
                    if (string.IsNullOrEmpty(inputKey)) continue;

                    double perUnit = batchesPerUnit * MachinePhysics.InputAmount(
                        input.Amount, model, route.Recipe.IgnoresEfficiencyMultiplier);
                    if (perUnit <= 0) continue;

                    Dictionary<string, double> below = Expand(inputKey, routes, models, memo, depth + 1);
                    foreach (var leaf in below)
                    {
                        double have;
                        total.TryGetValue(leaf.Key, out have);
                        total[leaf.Key] = have + leaf.Value * perUnit;
                    }
                }
            }

            memo[key] = total;
            return total;
        }

        /// <summary>One hop: the route's inputs per unit of its output, with the machine's material
        /// efficiency and input divisor already applied.</summary>
        private static Dictionary<string, double> DirectInputsOf(string key, Route route, MachineModel[] models)
        {
            var direct = new Dictionary<string, double>(StringComparer.Ordinal);
            double outputPerBatch = OutputPerBatch(route, models, key);
            if (outputPerBatch <= 0 || route.Recipe == null || route.Recipe.Inputs == null) return direct;

            MachineModel model = ModelFor(models, route.Cap);
            double batchesPerUnit = 1.0 / outputPerBatch;
            foreach (RecipeItem input in route.Recipe.Inputs)
            {
                if (input.Amount <= 0) continue;
                string inputKey = InvKey(input);
                if (string.IsNullOrEmpty(inputKey)) continue;

                double perUnit = batchesPerUnit * MachinePhysics.InputAmount(
                    input.Amount, model, route.Recipe.IgnoresEfficiencyMultiplier);
                if (perUnit <= 0) continue;

                double have;
                direct.TryGetValue(inputKey, out have);
                direct[inputKey] = have + perUnit;
            }
            return direct;
        }

        private static int HeightOf(string key, Dictionary<string, Dictionary<string, double>> directInputs,
            Dictionary<string, int> heights, int depth)
        {
            int known;
            if (heights.TryGetValue(key, out known)) return known;

            Dictionary<string, double> inputs;
            if (depth >= MaxDepth || !directInputs.TryGetValue(key, out inputs)) return 0;

            int height = 1;
            foreach (string inputKey in inputs.Keys)
            {
                int below = HeightOf(inputKey, directInputs, heights, depth + 1) + 1;
                if (below > height) height = below;
            }
            heights[key] = height;
            return height;
        }

        /// <summary>Units of <paramref name="key"/> one batch of the route yields, after the
        /// machine's material efficiency. Mirrors <c>output = batches * item.Amount *
        /// MaterialEfficiency</c> in <see cref="MachinePhysics"/>.</summary>
        private static double OutputPerBatch(Route route, MachineModel[] models, string key)
        {
            if (route.Recipe == null || route.Recipe.Outputs == null) return 0;
            MachineModel model = ModelFor(models, route.Cap);
            double scale = model.OutputScale > 0 ? model.OutputScale : 1.0;
            foreach (RecipeItem output in route.Recipe.Outputs)
            {
                if (output.Amount <= 0) continue;
                if (string.Equals(InvKey(output), key, StringComparison.Ordinal))
                    return output.Amount * scale;
            }
            return 0;
        }

        private static Dictionary<string, double> Self(string key)
        {
            var self = new Dictionary<string, double>(1, StringComparer.Ordinal);
            self[key] = 1.0;
            return self;
        }

        private static MachineModel ModelFor(MachineModel[] models, NodeCapabilityKind cap)
        {
            int idx = (int)cap;
            if (models == null || idx < 0 || idx >= models.Length)
                return new MachineModel { Speed = 1.0, OutputScale = 1.0, InputDivisor = 1.0 };
            return models[idx];
        }

        private static int PositiveOutputCount(ConversionRecipe recipe)
        {
            if (recipe == null || recipe.Outputs == null) return 0;
            int n = 0;
            foreach (RecipeItem output in recipe.Outputs) if (output.Amount > 0) n++;
            return n;
        }

        private static int PositiveInputCount(ConversionRecipe recipe)
        {
            if (recipe == null || recipe.Inputs == null) return 0;
            int n = 0;
            foreach (RecipeItem input in recipe.Inputs) if (input.Amount > 0) n++;
            return n;
        }

        private static string InvKey(RecipeItem item)
        {
            return InventoryKeyMapper.FromTypeIdAndSubtype(item.TypeId, item.SubTypeId);
        }
    }

    /// <summary>
    /// One <see cref="RecipeExpansion"/> per capability set, built on first ask and kept.
    ///
    /// <para>A faction holding only Refinery I pays 43% more ore per ingot than one holding
    /// RefineryII, so a single shared table would either undersize the first faction's chains or
    /// oversize the second's — and an undersized demand is the mirror of the oversizing
    /// docs/demand-chains.md exists to remove. Keying by capability set costs a handful of tables:
    /// the key collapses to the capabilities that actually carry recipes, so Shipyard and
    /// SensorArray never split one.</para>
    /// </summary>
    public sealed class RecipeExpansionCache
    {
        private readonly IDictionary<NodeCapabilityKind, List<ConversionRecipe>> _capRecipes;
        private readonly MachineModel[] _models;
        private readonly int _recipeBearingMask;
        private readonly Dictionary<int, RecipeExpansion> _byMask = new Dictionary<int, RecipeExpansion>();

        public RecipeExpansionCache(IDictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes,
                                    MachineModel[] models)
        {
            _capRecipes = capRecipes;
            _models     = models;

            int mask = 0;
            if (capRecipes != null)
                foreach (var entry in capRecipes)
                    if (entry.Value != null && entry.Value.Count > 0) mask |= 1 << (int)entry.Key;
            _recipeBearingMask = mask;
        }

        /// <summary>Tables built so far. Diagnostic: an unexpected count means capability sets are
        /// splitting on something that carries no recipes.</summary>
        public int TableCount { get { return _byMask.Count; } }

        /// <summary>The table for a faction holding <paramref name="caps"/>. Null means the whole
        /// catalogue.</summary>
        public RecipeExpansion For(IEnumerable<NodeCapabilityKind> caps)
        {
            int mask = _recipeBearingMask;
            if (caps != null)
            {
                mask = 0;
                foreach (NodeCapabilityKind cap in caps) mask |= 1 << (int)cap;
                mask &= _recipeBearingMask;
            }

            RecipeExpansion table;
            if (_byMask.TryGetValue(mask, out table)) return table;

            var allowed = new List<NodeCapabilityKind>();
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) allowed.Add((NodeCapabilityKind)i);

            table = RecipeExpansion.Build(_capRecipes, allowed, _models);
            _byMask[mask] = table;
            return table;
        }
    }
}
