using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// What a faction's Stock orders hold back from a lower-ranked claimant, on the one integer
    /// scale <see cref="BuildQueueEntry.Priority"/> and <see cref="FactionState.InstallPriority"/>
    /// share. The mirror of <see cref="InstallReserveFloor"/>: a ship build or capability install
    /// ranked below a claim leaves its amount alone, and <b>a tie goes to the build or install</b>,
    /// so every order at rank 0 reproduces the behaviour before Stock orders claimed anything.
    ///
    /// <para><b>Two kinds of claim.</b></para>
    /// <list type="bullet">
    /// <item><b>Item</b>: the order's own item at its node, up to the target. A build absorbing that
    /// item as a component, or a recipe consuming it as an input, leaves the target.</item>
    /// <item><b>Input</b>: the inputs of the recipe that makes the item
    /// (<see cref="RecipeExpansion.TryGetRoute"/>), at every owned bank that can run it, for the
    /// batches that bank could run from its own stock now, capped at the faction's shortfall across
    /// the item's targets. Bounding the claim by what is makeable is what keeps an order blocked on
    /// one input from holding every other input back: a rocket order with no cobalt on hand claims
    /// no iron.</item>
    /// </list>
    ///
    /// <para><b>Rank.</b> <see cref="ClaimRank"/> lifts a rank the player set above 0 by
    /// <see cref="FactionTuning.HighestBuildRolePriority"/>, as <see cref="FactionState.InstallRankFor"/>
    /// does for an install, so priority 1 outranks every fleet build role. A recipe with no rank of
    /// its own (anything not serving a build or install) stands at 0.</para>
    ///
    /// <para>Rebuilt each production pass; the host publishes it on <see cref="WorldModel"/> for the
    /// shipyard's component absorption, which runs on its own clock. Provenance: FAC-501.</para>
    /// </summary>
    public sealed class StockClaimFloor
    {
        public struct Claim
        {
            public long Amount;
            public int  Rank;
        }

        private readonly Dictionary<string, Dictionary<string, List<Claim>>> _items =
            new Dictionary<string, Dictionary<string, List<Claim>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, List<Claim>>> _inputs =
            new Dictionary<string, Dictionary<string, List<Claim>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _serving =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>True when no order claims anything, which is every faction without a Stock order.</summary>
        public bool IsEmpty { get { return _items.Count == 0 && _inputs.Count == 0; } }

        /// <summary>Whether any claim at <paramref name="nodeId"/> outranks <paramref name="rank"/>.</summary>
        public bool AnyAbove(string nodeId, int rank)
        {
            return AnyAbove(_items, nodeId, rank) || AnyAbove(_inputs, nodeId, rank);
        }

        /// <summary>How much of <paramref name="key"/> a claimant ranked <paramref name="rank"/> must
        /// leave at <paramref name="nodeId"/>: every item and input claim there ranked strictly above
        /// it.</summary>
        public long For(string nodeId, string key, int rank)
        {
            return Sum(_items, nodeId, key, rank) + Sum(_inputs, nodeId, key, rank);
        }

        /// <summary>Whether <paramref name="blueprintId"/> is the recipe an input claim at
        /// <paramref name="nodeId"/> exists for. That recipe sees the claimed inputs; every other
        /// recipe at the bank does not.</summary>
        public bool Serves(string nodeId, string blueprintId)
        {
            HashSet<string> serving;
            return blueprintId != null && _serving.TryGetValue(nodeId, out serving) && serving.Contains(blueprintId);
        }

        /// <summary>Per-key floors at <paramref name="nodeId"/> for a recipe ranked
        /// <paramref name="rank"/>, or null when nothing there outranks it. Input claims are left out
        /// when <paramref name="serving"/> -- the recipe the claim exists for.</summary>
        public Dictionary<string, long> FloorsAt(string nodeId, int rank, bool serving)
        {
            Dictionary<string, long> floors = null;
            AddFloors(_items, nodeId, rank, ref floors);
            if (!serving) AddFloors(_inputs, nodeId, rank, ref floors);
            return floors;
        }

        /// <summary>The claim rank of a Stock order ranked <paramref name="orderRank"/>: lifted above
        /// every build role when the player set it above 0, otherwise unchanged.</summary>
        public static int ClaimRank(int orderRank, FactionTuning tuning)
        {
            if (orderRank <= 0 || tuning == null) return orderRank;
            return orderRank + tuning.HighestBuildRolePriority();
        }

        /// <summary>Builds the claims for <paramref name="state"/>'s Stock orders.
        /// <paramref name="inventory"/> reads a node's stock of a key; <paramref name="expansion"/>
        /// and <paramref name="models"/> may be null, which leaves only the item claims (the
        /// shipyard's view).</summary>
        public static StockClaimFloor Build(FactionState state, SpatialGraph graph, IEnumerable<SpatialNode> ownedNodes,
            Func<string, string, long> inventory, FactionTuning tuning, RecipeExpansion expansion, MachineModel[] models)
        {
            var floor = new StockClaimFloor();
            if (state == null || graph == null || inventory == null) return floor;

            Dictionary<(string NodeId, string ItemKey), int> ranks;
            Dictionary<(string NodeId, string ItemKey), long> targets = StockOrders.TargetsWithRanks(state, graph, out ranks);
            if (targets == null || targets.Count == 0) return floor;

            // Per item: the shortfall across its targets, and the strongest rank among the targets
            // still short. The input claim is faction-wide because the item may be made at one
            // station and hauled to another.
            Dictionary<string, long> gapByItem = null;
            Dictionary<string, int> rankByItem = null;
            foreach (KeyValuePair<(string NodeId, string ItemKey), long> t in targets)
            {
                int rank = ClaimRank(ranks != null && ranks.ContainsKey(t.Key) ? ranks[t.Key] : 0, tuning);
                Add(floor._items, t.Key.NodeId, t.Key.ItemKey, new Claim { Amount = t.Value, Rank = rank });

                long gap = t.Value - inventory(t.Key.NodeId, t.Key.ItemKey);
                if (gap <= 0) continue;
                if (gapByItem == null)
                {
                    gapByItem = new Dictionary<string, long>(StringComparer.Ordinal);
                    rankByItem = new Dictionary<string, int>(StringComparer.Ordinal);
                }
                long prior;
                gapByItem.TryGetValue(t.Key.ItemKey, out prior);
                gapByItem[t.Key.ItemKey] = prior + gap;
                int priorRank;
                rankByItem[t.Key.ItemKey] = rankByItem.TryGetValue(t.Key.ItemKey, out priorRank)
                    ? Math.Max(priorRank, rank) : rank;
            }

            if (gapByItem == null || expansion == null || models == null || ownedNodes == null) return floor;

            var banks = new List<SpatialNode>(ownedNodes);
            banks.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (KeyValuePair<string, long> item in gapByItem)
            {
                RecipeExpansion.Route route;
                if (!expansion.TryGetRoute(item.Key, out route) || route.Recipe == null) continue;
                ConversionRecipe recipe = route.Recipe;
                if (recipe.Inputs == null || recipe.Inputs.Length == 0 || recipe.Outputs == null) continue;
                int capIdx = (int)route.Cap;
                if (capIdx < 0 || capIdx >= models.Length) continue;
                MachineModel model = models[capIdx];

                double perBatchOut = 0;
                foreach (RecipeItem outp in recipe.Outputs)
                    if (outp.Amount > 0 && ProductionManager.InvKey(outp) == item.Key) perBatchOut += outp.Amount;
                perBatchOut *= model.OutputScale;
                if (perBatchOut <= 0) continue;

                long batchesLeft = (long)Math.Ceiling(item.Value / perBatchOut);
                int rank = rankByItem[item.Key];
                foreach (SpatialNode bank in banks)
                {
                    if (batchesLeft <= 0) break;
                    int machines;
                    if (!bank.Capabilities.TryGetValue(route.Cap, out machines) || machines < 1) continue;

                    long runnable = long.MaxValue;
                    foreach (RecipeItem inp in recipe.Inputs)
                    {
                        if (inp.Amount <= 0) continue;
                        double perBatch = MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                        if (perBatch <= 0) continue;
                        long have = inventory(bank.Id, ProductionManager.InvKey(inp));
                        long can = have > 0 ? (long)Math.Floor(have / perBatch) : 0;
                        if (can < runnable) runnable = can;
                    }
                    if (runnable == long.MaxValue || runnable <= 0) continue;
                    long batches = Math.Min(runnable, batchesLeft);
                    batchesLeft -= batches;

                    foreach (RecipeItem inp in recipe.Inputs)
                    {
                        if (inp.Amount <= 0) continue;
                        double perBatch = MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                        if (perBatch <= 0) continue;
                        Add(floor._inputs, bank.Id, ProductionManager.InvKey(inp),
                            new Claim { Amount = (long)Math.Ceiling(batches * perBatch), Rank = rank });
                    }
                    HashSet<string> serving;
                    if (!floor._serving.TryGetValue(bank.Id, out serving))
                        floor._serving[bank.Id] = serving = new HashSet<string>(StringComparer.Ordinal);
                    serving.Add(recipe.BlueprintId);
                }
            }
            return floor;
        }

        private static void Add(Dictionary<string, Dictionary<string, List<Claim>>> map, string nodeId, string key, Claim claim)
        {
            if (claim.Amount <= 0) return;
            Dictionary<string, List<Claim>> byKey;
            if (!map.TryGetValue(nodeId, out byKey))
                map[nodeId] = byKey = new Dictionary<string, List<Claim>>(StringComparer.Ordinal);
            List<Claim> claims;
            if (!byKey.TryGetValue(key, out claims)) byKey[key] = claims = new List<Claim>(1);
            claims.Add(claim);
        }

        private static long Sum(Dictionary<string, Dictionary<string, List<Claim>>> map, string nodeId, string key, int rank)
        {
            Dictionary<string, List<Claim>> byKey;
            List<Claim> claims;
            if (nodeId == null || key == null || !map.TryGetValue(nodeId, out byKey) || !byKey.TryGetValue(key, out claims))
                return 0;
            long sum = 0;
            foreach (Claim c in claims)
                if (c.Rank > rank) sum += c.Amount;
            return sum;
        }

        private static bool AnyAbove(Dictionary<string, Dictionary<string, List<Claim>>> map, string nodeId, int rank)
        {
            Dictionary<string, List<Claim>> byKey;
            if (nodeId == null || !map.TryGetValue(nodeId, out byKey)) return false;
            foreach (List<Claim> claims in byKey.Values)
                foreach (Claim c in claims)
                    if (c.Rank > rank) return true;
            return false;
        }

        private static void AddFloors(Dictionary<string, Dictionary<string, List<Claim>>> map, string nodeId, int rank,
                                      ref Dictionary<string, long> floors)
        {
            Dictionary<string, List<Claim>> byKey;
            if (nodeId == null || !map.TryGetValue(nodeId, out byKey)) return;
            foreach (KeyValuePair<string, List<Claim>> kv in byKey)
            {
                long sum = 0;
                foreach (Claim c in kv.Value)
                    if (c.Rank > rank) sum += c.Amount;
                if (sum <= 0) continue;
                if (floors == null) floors = new Dictionary<string, long>(StringComparer.Ordinal);
                long prior;
                floors.TryGetValue(kv.Key, out prior);
                floors[kv.Key] = prior + sum;
            }
        }
    }
}
