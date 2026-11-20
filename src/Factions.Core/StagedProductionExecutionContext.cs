using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// <see cref="IProductionExecutionContext"/> backed by a <see cref="ProductionStagedWorldEffects"/>
    /// journal for one faction's cycle. Inventory/reservation/demand/investment/log effects are
    /// private until the journal's guarded commit publishes them; read-only node metadata (owned-node
    /// listing, capabilities, position) and influence come straight from the live <see cref="WorldModel"/>
    /// since they are not mutated by Production and are already covered by
    /// <see cref="ProductionCycleCommitGuard"/>'s own invalidation surface.
    /// </summary>
    public sealed class StagedProductionExecutionContext : IProductionExecutionContext
    {
        private readonly WorldModel _world;
        private readonly ProductionStagedWorldEffects _effects;
        private readonly string _factionTag;

        public StagedProductionExecutionContext(WorldModel world, ProductionStagedWorldEffects effects,
                                                 string factionTag, DateTime utcNow)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _effects = effects ?? throw new ArgumentNullException(nameof(effects));
            _factionTag = factionTag;
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }
        public IItemRegistry Registry { get { return _world.Registry; } }
        public IPlanetCatalogue Planets { get { return _world.Planets; } }

        public IEnumerable<SpatialNode> OwnedNodes(string factionTag) { return _world.Graph.OwnedBy(factionTag); }
        public double InfluenceControl(Vec3 position, string factionTag) { return _world.Influence.Control(position, factionTag); }
        public bool TryGetInventoryItemKind(string inventoryKey, out ItemKind kind) { return _world.TryGetInventoryItemKind(inventoryKey, out kind); }

        public long GetNodeInventory(string nodeId, string inventoryKey) { return _effects.GetNodeInventory(nodeId, inventoryKey); }
        public long GetReservedInventory(string nodeId, string inventoryKey) { return _effects.GetReservedInventory(nodeId, inventoryKey); }
        public IEnumerable<KeyValuePair<string, long>> SnapshotNodeInventory(string nodeId) { return _effects.SnapshotNodeInventory(nodeId); }
        public IEnumerable<KeyValuePair<string, long>> SnapshotReservedInventory(string nodeId) { return _effects.SnapshotReservedInventory(nodeId); }
        public void AddNodeInventory(string nodeId, string inventoryKey, long delta) { _effects.AddNodeInventory(nodeId, inventoryKey, delta); }

        public void AddPendingDemandHistoryCredit(string nodeId, string itemKey, double weightedQty)
            { _effects.AddPendingDemandHistoryCredit(nodeId, itemKey, weightedQty); }
        public void AddReservedInventory(string nodeId, string inventoryKey, long delta) { _effects.AddReservedInventory(nodeId, inventoryKey, delta); }
        public long MoveInventoryToReserved(string nodeId, string inventoryKey, long quantity) { return _effects.MoveInventoryToReserved(nodeId, inventoryKey, quantity); }
        public long MoveReservedToInventory(string nodeId, string inventoryKey, long quantity) { return _effects.MoveReservedToInventory(nodeId, inventoryKey, quantity); }

        public long GetBuildCollected(string nodeId, string entryId, string invKey) { return _effects.GetBuildCollected(nodeId, entryId, invKey); }
        public void AddBuildCollected(string nodeId, string entryId, string invKey, long delta) { _effects.AddBuildCollected(nodeId, entryId, invKey, delta); }
        public long GetBuildReservedInput(string nodeId, string entryId, string invKey) { return _effects.GetBuildReservedInput(nodeId, entryId, invKey); }
        public void AddBuildReservedInput(string nodeId, string entryId, string invKey, long delta) { _effects.AddBuildReservedInput(nodeId, entryId, invKey, delta); }
        public IEnumerable<KeyValuePair<string, long>> SnapshotBuildReservedInputs(string nodeId, string entryId) { return _effects.SnapshotBuildReservedInputs(nodeId, entryId); }
        public long MoveInventoryToBuildReserved(string nodeId, string entryId, string invKey, long quantity) { return _effects.MoveInventoryToBuildReserved(nodeId, entryId, invKey, quantity); }
        public double GetBuildRecipeAccum(string nodeId, string entryId, string blueprintId, out DateTime lastTick) { return _effects.GetBuildRecipeAccum(nodeId, entryId, blueprintId, out lastTick); }
        public void SetBuildRecipeProgress(string nodeId, string entryId, string blueprintId, double accum, DateTime lastTick) { _effects.SetBuildRecipeProgress(nodeId, entryId, blueprintId, accum, lastTick); }

        public long GetFactionInventory(string factionTag, ItemKind kind) { return _effects.GetFactionInventory(kind); }
        public long GetFactionReservedInventory(string factionTag, ItemKind kind) { return _effects.GetFactionReservedInventory(kind); }
        public long GetFactionInventoryByKey(string factionTag, string inventoryKey) { return _effects.GetFactionInventoryByKey(inventoryKey); }
        public long GetFactionReservedInventoryByKey(string factionTag, string inventoryKey) { return _effects.GetFactionReservedInventoryByKey(inventoryKey); }

        public List<DemandEntry> SnapshotItemDemands(string factionTag) { return _effects.SnapshotItemDemands(); }
        public bool PostDemand(DemandEntry demand) { return _effects.PostDemand(demand); }
        public List<InvestmentRequest> SnapshotInvestments(string factionTag) { return _effects.SnapshotInvestments(); }
        public bool PostInvestment(InvestmentRequest investment) { return _effects.PostInvestment(investment); }

        public void AddLog(string message) { _effects.AddLog(message); }
    }
}
