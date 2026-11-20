using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Thin passthrough <see cref="IProductionExecutionContext"/> backed directly by a
    /// <see cref="WorldModel"/> — every read and write takes effect immediately, reproducing today's
    /// synchronous <see cref="ProductionManager.Tick"/> behavior exactly. <see cref="UtcNow"/> is
    /// frozen at construction to <c>world.UtcNow</c>; nothing else advances it mid-cycle in the
    /// existing synchronous host loop, so this is a no-op change in observed behavior.
    /// </summary>
    public sealed class LiveProductionExecutionContext : IProductionExecutionContext
    {
        private readonly WorldModel _world;
        private readonly List<string> _log;

        public LiveProductionExecutionContext(WorldModel world, List<string> log)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            UtcNow = world.UtcNow;
        }

        public DateTime UtcNow { get; private set; }
        public IItemRegistry Registry { get { return _world.Registry; } }
        public IPlanetCatalogue Planets { get { return _world.Planets; } }

        public IEnumerable<SpatialNode> OwnedNodes(string factionTag) { return _world.Graph.OwnedBy(factionTag); }
        public double InfluenceControl(Vec3 position, string factionTag) { return _world.Influence.Control(position, factionTag); }
        public bool TryGetInventoryItemKind(string inventoryKey, out ItemKind kind) { return _world.TryGetInventoryItemKind(inventoryKey, out kind); }

        public long GetNodeInventory(string nodeId, string inventoryKey) { return _world.GetNodeInventory(nodeId, inventoryKey); }

        public long GetReservedInventory(string nodeId, string inventoryKey)
        {
            SpatialNode node;
            long value;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) && node.ReservedInventory.TryGetValue(inventoryKey, out value)
                ? value : 0;
        }

        public IEnumerable<KeyValuePair<string, long>> SnapshotNodeInventory(string nodeId)
        {
            return _world.GetNodeInventoryAll(nodeId);
        }

        public IEnumerable<KeyValuePair<string, long>> SnapshotReservedInventory(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.ReservedInventory : System.Linq.Enumerable.Empty<KeyValuePair<string, long>>();
        }

        public void AddNodeInventory(string nodeId, string inventoryKey, long delta) { _world.AddNodeInventory(nodeId, inventoryKey, delta); }

        public void AddPendingDemandHistoryCredit(string nodeId, string itemKey, double weightedQty)
            { _world.AddPendingDemandHistoryCredit(nodeId, itemKey, weightedQty); }

        public void AddReservedInventory(string nodeId, string inventoryKey, long delta)
        {
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node) || string.IsNullOrEmpty(inventoryKey)) return;
            long value;
            node.ReservedInventory.TryGetValue(inventoryKey, out value);
            value += delta;
            node.ReservedInventory[inventoryKey] = value < 0 ? 0 : value;
        }

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

        private BuildQueueEntry FindBuildEntry(string nodeId, string entryId)
        {
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return null;
            foreach (BuildQueueEntry entry in node.BuildQueue)
                if (entry.Id == entryId) return entry;
            return null;
        }

        public long GetBuildCollected(string nodeId, string entryId, string invKey)
        {
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            long value;
            return entry != null && entry.Collected.TryGetValue(invKey, out value) ? value : 0;
        }

        public void AddBuildCollected(string nodeId, string entryId, string invKey, long delta)
        {
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            if (entry == null || string.IsNullOrEmpty(invKey)) return;
            long value;
            entry.Collected.TryGetValue(invKey, out value);
            value += delta;
            entry.Collected[invKey] = value < 0 ? 0 : value;
            // A component manufactured into this build by its own dedicated production is progress,
            // exactly like one absorbed from node inventory, and must reset the abandonment clock the
            // same way. ShipyardManager cannot see this path -- it only observes its own absorb loop
            // -- so a build fed entirely by ProductionManager would otherwise accrue stall time while
            // visibly filling up, and be refunded for making no progress while making progress.
            if (delta > 0) entry.StalledSeconds = 0;
        }

        public long GetBuildReservedInput(string nodeId, string entryId, string invKey)
        {
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            long value;
            return entry != null && entry.ReservedInputs.TryGetValue(invKey, out value) ? value : 0;
        }

        public IEnumerable<KeyValuePair<string, long>> SnapshotBuildReservedInputs(string nodeId, string entryId)
        {
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            return entry != null ? entry.ReservedInputs : System.Linq.Enumerable.Empty<KeyValuePair<string, long>>();
        }

        public void AddBuildReservedInput(string nodeId, string entryId, string invKey, long delta)
        {
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            if (entry == null || string.IsNullOrEmpty(invKey)) return;
            long value;
            entry.ReservedInputs.TryGetValue(invKey, out value);
            value += delta;
            entry.ReservedInputs[invKey] = value < 0 ? 0 : value;
        }

        public long MoveInventoryToBuildReserved(string nodeId, string entryId, string invKey, long quantity)
        {
            if (quantity <= 0) return 0;
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            if (entry == null) return 0;
            long available = GetNodeInventory(nodeId, invKey);
            long moved = available < quantity ? available : quantity;
            if (moved <= 0) return 0;
            AddNodeInventory(nodeId, invKey, -moved);
            AddBuildReservedInput(nodeId, entryId, invKey, moved);
            return moved;
        }

        public double GetBuildRecipeAccum(string nodeId, string entryId, string blueprintId, out DateTime lastTick)
        {
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            BuildRecipeSlot slot;
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
            BuildQueueEntry entry = FindBuildEntry(nodeId, entryId);
            if (entry == null || string.IsNullOrEmpty(blueprintId)) return;
            BuildRecipeSlot slot;
            if (!entry.RecipeProgress.TryGetValue(blueprintId, out slot))
            {
                slot = new BuildRecipeSlot();
                entry.RecipeProgress[blueprintId] = slot;
            }
            slot.Accum = accum;
            slot.LastTick = lastTick;
        }

        public long GetFactionInventory(string factionTag, ItemKind kind) { return _world.GetInventory(factionTag, kind); }
        public long GetFactionReservedInventory(string factionTag, ItemKind kind) { return _world.GetReservedInventory(factionTag, kind); }
        public long GetFactionInventoryByKey(string factionTag, string inventoryKey) { return _world.GetFactionInventoryByKey(factionTag, inventoryKey); }
        public long GetFactionReservedInventoryByKey(string factionTag, string inventoryKey) { return _world.GetFactionReservedInventoryByKey(factionTag, inventoryKey); }

        public List<DemandEntry> SnapshotItemDemands(string factionTag) { return _world.Demand.SnapshotItemDemands(factionTag); }
        public bool PostDemand(DemandEntry demand) { return _world.Demand.Post(demand); }
        public List<InvestmentRequest> SnapshotInvestments(string factionTag) { return _world.Investments.SnapshotFor(factionTag); }
        public bool PostInvestment(InvestmentRequest investment) { return _world.Investments.Post(investment); }

        public void AddLog(string message) { if (message != null) _log.Add(message); }
    }
}
