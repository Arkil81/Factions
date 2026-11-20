using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Private WorldModel effects for one staged Production cycle. All reads and writes use private
    /// inventory/reservation overlays until <see cref="TryCommit"/> validates the captured read set.
    /// This class deliberately owns no persisted state; an incomplete instance is discarded on reload.
    /// </summary>
    public sealed class ProductionStagedWorldEffects
    {
        private static readonly KeyValuePair<string, long>[] s_emptyLongs = new KeyValuePair<string, long>[0];

        private sealed class BuildEntryOverlay
        {
            public readonly Dictionary<string, long> Collected;
            public readonly Dictionary<string, long> ReservedInputs;
            public readonly Dictionary<string, (double Accum, DateTime LastTick)> RecipeProgress
                = new Dictionary<string, (double Accum, DateTime LastTick)>(StringComparer.Ordinal);
            /// <summary>Any component was manufactured into this entry this cycle — commit resets the
            /// live entry's <see cref="BuildQueueEntry.StalledSeconds"/>, mirroring the live context.</summary>
            public bool Progressed;

            public BuildEntryOverlay(BuildQueueEntry entry)
            {
                Collected = Copy(entry.Collected);
                ReservedInputs = Copy(entry.ReservedInputs);
                foreach (var kv in entry.RecipeProgress)
                    RecipeProgress[kv.Key] = (kv.Value.Accum, kv.Value.LastTick);
            }
        }

        private sealed class NodeOverlay
        {
            public readonly Dictionary<string, long> Inventory;
            public readonly Dictionary<string, long> ReservedInventory;
            public readonly Dictionary<string, BuildEntryOverlay> BuildEntries = new Dictionary<string, BuildEntryOverlay>(StringComparer.Ordinal);

            public NodeOverlay(SpatialNode node)
            {
                Inventory = Copy(node.Inventory);
                ReservedInventory = Copy(node.ReservedInventory);
                foreach (BuildQueueEntry entry in node.BuildQueue)
                    if (entry.Id != null) BuildEntries[entry.Id] = new BuildEntryOverlay(entry);
            }
        }

        private readonly Dictionary<string, NodeOverlay> _nodes;
        private readonly List<DemandEntry> _demands = new List<DemandEntry>();
        private readonly List<InvestmentRequest> _investments = new List<InvestmentRequest>();
        // Staged DemandHistory credits from this cycle's real consumption -- flushed to WorldModel
        // only on a successful TryCommit, same discipline as _demands/_investments above. No dedup
        // needed: WorldModel.AddPendingDemandHistoryCredit already accumulates additively per node/item.
        private readonly List<(string NodeId, string ItemKey, double Weighted)> _historyCredits
            = new List<(string NodeId, string ItemKey, double Weighted)>();
        private readonly List<string> _logs = new List<string>();
        private readonly HashSet<string> _demandKeys;
        private readonly HashSet<string> _investmentKeys;
        // Upsert-by-key views mirroring DemandRegister/InvestmentRegister's own Post semantics, so a
        // same-cycle read (e.g. PostCapabilityInvestmentRequests reading demands PostRecipeInputDemands
        // staged moments earlier) sees exactly what the equivalent live Tick() would see — live Post()
        // is immediately visible to a later Snapshot() in the same call, and staged must match that.
        private readonly Dictionary<string, DemandEntry> _demandsByKey = new Dictionary<string, DemandEntry>(StringComparer.Ordinal);
        // Keyed by the same InvestmentKey() string as _investmentKeys below (FactionTag+Cap+TargetPlanetId+TargetSite+TargetFeature)
        // rather than Cap alone -- a Terra-scoped and a Mars-scoped Shipyard request must not collapse
        // into one entry here just because InvestmentRegister itself now keeps them separate too.
        private readonly Dictionary<string, InvestmentRequest> _investmentsByCap = new Dictionary<string, InvestmentRequest>(StringComparer.Ordinal);
        private readonly IItemRegistry _registry;
        private bool _committed;

        private ProductionStagedWorldEffects(Dictionary<string, NodeOverlay> nodes,
                                             HashSet<string> demandKeys,
                                             HashSet<string> investmentKeys,
                                             IItemRegistry registry)
        {
            _nodes = nodes;
            _demandKeys = demandKeys;
            _investmentKeys = investmentKeys;
            _registry = registry ?? NullItemRegistry.Instance;
        }

        public static ProductionStagedWorldEffects Capture(FactionState state, WorldModel world)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (world == null) throw new ArgumentNullException(nameof(world));

            var nodes = new Dictionary<string, NodeOverlay>();
            foreach (SpatialNode node in world.Graph.OwnedBy(state.Tag))
                nodes.Add(node.Id, new NodeOverlay(node));

            var effects = new ProductionStagedWorldEffects(nodes,
                new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal),
                world.Registry);

            foreach (DemandEntry demand in world.Demand.SnapshotItemDemands(state.Tag))
            {
                string key = DemandKey(demand);
                effects._demandKeys.Add(key);
                effects._demandsByKey[key] = demand;
            }
            foreach (InvestmentRequest investment in world.Investments.SnapshotFor(state.Tag))
            {
                effects._investmentKeys.Add(InvestmentKey(investment));
                effects._investmentsByCap[InvestmentKey(investment)] = investment;
            }

            return effects;
        }

        public long GetNodeInventory(string nodeId, string inventoryKey)
        {
            NodeOverlay node;
            long value;
            return _nodes.TryGetValue(nodeId, out node) && node.Inventory.TryGetValue(inventoryKey, out value)
                ? value : 0;
        }

        public long GetReservedInventory(string nodeId, string inventoryKey)
        {
            NodeOverlay node;
            long value;
            return _nodes.TryGetValue(nodeId, out node) && node.ReservedInventory.TryGetValue(inventoryKey, out value)
                ? value : 0;
        }

        public void AddNodeInventory(string nodeId, string inventoryKey, long delta)
        {
            NodeOverlay node;
            if (!_nodes.TryGetValue(nodeId, out node) || string.IsNullOrEmpty(inventoryKey)) return;
            AddClamped(node.Inventory, inventoryKey, delta);
        }

        public void AddReservedInventory(string nodeId, string inventoryKey, long delta)
        {
            NodeOverlay node;
            if (!_nodes.TryGetValue(nodeId, out node) || string.IsNullOrEmpty(inventoryKey)) return;
            AddClamped(node.ReservedInventory, inventoryKey, delta);
        }

        public void AddPendingDemandHistoryCredit(string nodeId, string itemKey, double weightedQty)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(itemKey) || weightedQty <= 0) return;
            _historyCredits.Add((nodeId, itemKey, weightedQty));
        }

        /// <summary>
        /// Physically separates up to <paramref name="quantity"/> from staged normal inventory.
        /// The return value is the amount actually moved, so callers cannot reserve stock twice.
        /// </summary>
        public long MoveInventoryToReserved(string nodeId, string inventoryKey, long quantity)
        {
            if (quantity <= 0) return 0;
            long available = GetNodeInventory(nodeId, inventoryKey);
            long moved = available < quantity ? available : quantity;
            if (moved <= 0) return 0;
            AddNodeInventory(nodeId, inventoryKey, -moved);
            AddReservedInventory(nodeId, inventoryKey, moved);
            return moved;
        }

        /// <summary>Returns staged reserved stock to normal inventory and reports the actual release.</summary>
        public long MoveReservedToInventory(string nodeId, string inventoryKey, long quantity)
        {
            if (quantity <= 0) return 0;
            long available = GetReservedInventory(nodeId, inventoryKey);
            long moved = available < quantity ? available : quantity;
            if (moved <= 0) return 0;
            AddReservedInventory(nodeId, inventoryKey, -moved);
            AddNodeInventory(nodeId, inventoryKey, moved);
            return moved;
        }

        private BuildEntryOverlay GetBuildEntry(string nodeId, string entryId)
        {
            NodeOverlay node;
            BuildEntryOverlay entry;
            if (_nodes.TryGetValue(nodeId, out node) && node.BuildEntries.TryGetValue(entryId, out entry)) return entry;
            return null;
        }

        public long GetBuildCollected(string nodeId, string entryId, string invKey)
        {
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            long value;
            return entry != null && entry.Collected.TryGetValue(invKey, out value) ? value : 0;
        }

        public void AddBuildCollected(string nodeId, string entryId, string invKey, long delta)
        {
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            if (entry == null || string.IsNullOrEmpty(invKey)) return;
            AddClamped(entry.Collected, invKey, delta);
            if (delta > 0) entry.Progressed = true;
        }

        public long GetBuildReservedInput(string nodeId, string entryId, string invKey)
        {
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            long value;
            return entry != null && entry.ReservedInputs.TryGetValue(invKey, out value) ? value : 0;
        }

        public void AddBuildReservedInput(string nodeId, string entryId, string invKey, long delta)
        {
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            if (entry == null || string.IsNullOrEmpty(invKey)) return;
            AddClamped(entry.ReservedInputs, invKey, delta);
        }

        public IEnumerable<KeyValuePair<string, long>> SnapshotBuildReservedInputs(string nodeId, string entryId)
        {
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            if (entry == null) return s_emptyLongs;
            return entry.ReservedInputs;
        }

        public long MoveInventoryToBuildReserved(string nodeId, string entryId, string invKey, long quantity)
        {
            if (quantity <= 0) return 0;
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            if (entry == null) return 0;
            long available = GetNodeInventory(nodeId, invKey);
            long moved = available < quantity ? available : quantity;
            if (moved <= 0) return 0;
            AddNodeInventory(nodeId, invKey, -moved);
            AddClamped(entry.ReservedInputs, invKey, moved);
            return moved;
        }

        public double GetBuildRecipeAccum(string nodeId, string entryId, string blueprintId, out DateTime lastTick)
        {
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            (double Accum, DateTime LastTick) slot;
            if (entry != null && entry.RecipeProgress.TryGetValue(blueprintId, out slot))
            {
                lastTick = slot.LastTick;
                return slot.Accum;
            }
            lastTick = default(DateTime);
            return 0;
        }

        public void SetBuildRecipeProgress(string nodeId, string entryId, string blueprintId, double accum, DateTime lastTick)
        {
            BuildEntryOverlay entry = GetBuildEntry(nodeId, entryId);
            if (entry == null || string.IsNullOrEmpty(blueprintId)) return;
            entry.RecipeProgress[blueprintId] = (accum, lastTick);
        }

        /// <summary>Stages a DemandRegister.Post with the same new-versus-refresh return semantics.
        /// Immediately visible to <see cref="SnapshotItemDemands"/>, matching live DemandRegister.Post.</summary>
        public bool PostDemand(DemandEntry demand)
        {
            string key = DemandKey(demand);
            bool isNew = _demandKeys.Add(key);
            DemandEntry prior;
            // Same refresh-preserve contract as live DemandRegister.Post (see PreserveOnRefresh).
            if (!isNew && _demandsByKey.TryGetValue(key, out prior))
                demand = DemandEntry.PreserveOnRefresh(prior, demand);
            _demands.Add(demand);
            _demandsByKey[key] = demand;
            return isNew;
        }

        /// <summary>All active Item: demands staged so far this cycle, merged with what was already
        /// posted live at capture time — mirrors <see cref="DemandRegister.SnapshotItemDemands"/>'s
        /// same-call visibility of an immediately preceding Post.</summary>
        public List<DemandEntry> SnapshotItemDemands()
        {
            var result = new List<DemandEntry>(_demandsByKey.Count);
            foreach (DemandEntry demand in _demandsByKey.Values)
                if (demand.Kind != null && demand.Kind.StartsWith("Item:", StringComparison.Ordinal))
                    result.Add(demand);
            return result;
        }

        /// <summary>Stages an InvestmentRegister.Post with the same new-versus-refresh return semantics.
        /// Immediately visible to <see cref="SnapshotInvestments"/>, matching live InvestmentRegister.Post.</summary>
        public bool PostInvestment(InvestmentRequest investment)
        {
            string key = InvestmentKey(investment);
            _investmentsByCap[key] = investment;
            bool isNew = _investmentKeys.Add(key);
            _investments.Add(investment);
            return isNew;
        }

        public void AddLog(string message)
        {
            if (message != null) _logs.Add(message);
        }

        /// <summary>All active investment requests staged so far this cycle, merged with what was
        /// already posted live at capture time — same-call visibility as <see cref="SnapshotItemDemands"/>.</summary>
        public List<InvestmentRequest> SnapshotInvestments()
        {
            return new List<InvestmentRequest>(_investmentsByCap.Values);
        }

        /// <summary>The node ids captured for this cycle — the faction's owned nodes at begin time.</summary>
        public IEnumerable<string> NodeIds { get { return _nodes.Keys; } }

        /// <summary>All staged inventory key/amount pairs at a node. Empty if the node wasn't captured.</summary>
        public IEnumerable<KeyValuePair<string, long>> SnapshotNodeInventory(string nodeId)
        {
            NodeOverlay node;
            if (!_nodes.TryGetValue(nodeId, out node)) return s_emptyLongs;
            return node.Inventory;
        }

        /// <summary>All staged reserved-inventory key/amount pairs at a node. Empty if not captured.</summary>
        public IEnumerable<KeyValuePair<string, long>> SnapshotReservedInventory(string nodeId)
        {
            NodeOverlay node;
            if (!_nodes.TryGetValue(nodeId, out node)) return s_emptyLongs;
            return node.ReservedInventory;
        }

        /// <summary>Total staged stock of a specific invKey across every captured node — the staged
        /// counterpart to <see cref="WorldModel.GetFactionInventoryByKey"/>.</summary>
        public long GetFactionInventoryByKey(string invKey)
        {
            long total = 0;
            foreach (NodeOverlay node in _nodes.Values)
            {
                long amount;
                if (node.Inventory.TryGetValue(invKey, out amount)) total += amount;
            }
            return total;
        }

        /// <summary>Reserved-side counterpart to <see cref="GetFactionInventoryByKey"/>.</summary>
        public long GetFactionReservedInventoryByKey(string invKey)
        {
            long total = 0;
            foreach (NodeOverlay node in _nodes.Values)
            {
                long amount;
                if (node.ReservedInventory.TryGetValue(invKey, out amount)) total += amount;
            }
            return total;
        }

        /// <summary>Staged counterpart to <see cref="WorldModel.GetInventory"/> — classifies each
        /// staged key via the same registry-backed <see cref="WorldModel.SumKind"/> logic as the live
        /// aggregate, so a key never classifies differently just because it's read through the journal.</summary>
        public long GetFactionInventory(ItemKind kind)
        {
            long total = 0;
            foreach (NodeOverlay node in _nodes.Values)
                total += WorldModel.SumKind(node.Inventory, kind, _registry);
            return total;
        }

        /// <summary>Reserved-side counterpart to <see cref="GetFactionInventory"/>.</summary>
        public long GetFactionReservedInventory(ItemKind kind)
        {
            long total = 0;
            foreach (NodeOverlay node in _nodes.Values)
                total += WorldModel.SumKind(node.ReservedInventory, kind, _registry);
            return total;
        }

        /// <summary>
        /// Atomically publishes this journal only if the captured Production read set still holds.
        /// Returns false without publishing any effect after a conflict or a prior successful commit.
        /// </summary>
        public bool TryCommit(ProductionCycleCommitGuard guard, FactionState state, WorldModel world)
        {
            return TryCommit(guard, state, world, null);
        }

        public bool TryCommit(ProductionCycleCommitGuard guard, FactionState state, WorldModel world, List<string> log)
        {
            if (_committed || guard == null || state == null || world == null || !guard.IsStillValid(state, world))
                return false;

            foreach (KeyValuePair<string, NodeOverlay> entry in _nodes)
            {
                SpatialNode live;
                if (!world.Graph.Nodes.TryGetValue(entry.Key, out live)) return false;
                Replace(live.Inventory, entry.Value.Inventory);
                Replace(live.ReservedInventory, entry.Value.ReservedInventory);

                foreach (BuildQueueEntry liveEntry in live.BuildQueue)
                {
                    BuildEntryOverlay overlay;
                    if (liveEntry.Id == null || !entry.Value.BuildEntries.TryGetValue(liveEntry.Id, out overlay)) continue;
                    Replace(liveEntry.Collected, overlay.Collected);
                    Replace(liveEntry.ReservedInputs, overlay.ReservedInputs);
                    if (overlay.Progressed) liveEntry.StalledSeconds = 0;
                    foreach (var kv in overlay.RecipeProgress)
                    {
                        BuildRecipeSlot slot;
                        if (!liveEntry.RecipeProgress.TryGetValue(kv.Key, out slot))
                        {
                            slot = new BuildRecipeSlot();
                            liveEntry.RecipeProgress[kv.Key] = slot;
                        }
                        slot.Accum = kv.Value.Accum;
                        slot.LastTick = kv.Value.LastTick;
                    }
                }
            }
            foreach (DemandEntry demand in _demands) world.Demand.Post(demand);
            foreach (InvestmentRequest investment in _investments) world.Investments.Post(investment);
            foreach (var c in _historyCredits) world.AddPendingDemandHistoryCredit(c.NodeId, c.ItemKey, c.Weighted);
            if (log != null) log.AddRange(_logs);
            _committed = true;
            return true;
        }

        private static Dictionary<string, long> Copy(Dictionary<string, long> source)
        {
            return source == null ? new Dictionary<string, long>() : new Dictionary<string, long>(source);
        }

        private static void Replace(Dictionary<string, long> target, Dictionary<string, long> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, long> item in source) target.Add(item.Key, item.Value);
        }

        private static void AddClamped(Dictionary<string, long> values, string key, long delta)
        {
            long current;
            values.TryGetValue(key, out current);
            long next = current + delta;
            values[key] = next < 0 ? 0 : next;
        }

        private static string DemandKey(DemandEntry demand)
        {
            return (demand.FactionTag ?? string.Empty) + "\u001f" + (demand.Kind ?? string.Empty) + "\u001f" + (demand.NodeId ?? string.Empty);
        }

        private static string InvestmentKey(InvestmentRequest investment)
        {
            return (investment.FactionTag ?? string.Empty) + "\u001f" + ((int)investment.Cap).ToString()
                + "\u001f" + (investment.TargetPlanetId ?? string.Empty)
                + SiteTerm(investment.TargetSite)
                + "\u001f" + (investment.TargetFeature ?? string.Empty);
        }

        /// <summary>The staged key must split the two regions of one body exactly as
        /// <see cref="InvestmentRegister"/>'s own dedupe key does, or a ground request and a well
        /// request for the same planet fold into one staged entry.</summary>
        private static string SiteTerm(StationSite? site)
        {
            return site == null ? string.Empty : "\u001f" + ((int)site.Value).ToString();
        }
    }
}
