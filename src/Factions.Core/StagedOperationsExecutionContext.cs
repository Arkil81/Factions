using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// <see cref="IOperationsExecutionContext"/> backed by an <see cref="OperationsStagedWorldEffects"/>
    /// journal for one faction's cycle. Inventory/ship-stock/demand/investment/fleet/log effects are
    /// private until the journal's guarded commit publishes them; read-only node metadata (owned-node
    /// listing, capabilities, position) comes straight from the live <see cref="WorldModel"/> since
    /// Operations never mutates it and <see cref="OperationsCycleCommitGuard"/> already covers it.
    /// </summary>
    public sealed class StagedOperationsExecutionContext : IOperationsExecutionContext
    {
        private readonly WorldModel _world;
        private readonly OperationsStagedWorldEffects _effects;

        public StagedOperationsExecutionContext(WorldModel world, OperationsStagedWorldEffects effects, DateTime utcNow)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _effects = effects ?? throw new ArgumentNullException(nameof(effects));
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }

        public IEnumerable<SpatialNode> OwnedNodes(string factionTag) { return _world.Graph.OwnedBy(factionTag); }

        public long GetNodeInventory(string nodeId, string itemKey) { return _effects.GetNodeInventory(nodeId, itemKey); }
        public void AddNodeInventory(string nodeId, string itemKey, long delta) { _effects.AddNodeInventory(nodeId, itemKey, delta); }

        public int GetShipStock(string nodeId, string prefabId) { return _effects.GetShipStock(nodeId, prefabId); }
        public IEnumerable<KeyValuePair<string, int>> SnapshotShipStock(string nodeId) { return _effects.SnapshotShipStock(nodeId); }

        public int ConsumeShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, int desiredCount,
            out string prefabId, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null, HaulHullCost cost = null)
        {
            return _effects.ConsumeShip(nodeId, kind, cat, desiredCount, out prefabId, route, viaRoute, cost);
        }

        public void ReturnShip(string nodeId, string prefabId, int count) { _effects.ReturnShip(nodeId, prefabId, count); }
        public int AvailableShips(string nodeId, string prefabId) { return _effects.AvailableShips(nodeId, prefabId); }
        public int ConsumeShipPrefab(string nodeId, string prefabId, int desiredCount)
        {
            return _effects.ConsumeShipPrefab(nodeId, prefabId, desiredCount);
        }

        public bool HasSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null)
        {
            return _effects.HasSuitableShip(nodeId, kind, cat, route, viaRoute);
        }

        public bool OwnsSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat)
        {
            return _effects.OwnsSuitableShip(nodeId, kind, cat);
        }

        public DateTime GetLastHaulDepartedUtc(string nodeId) { return _effects.GetLastHaulDepartedUtc(nodeId); }
        public void SetLastHaulDepartedUtc(string nodeId, DateTime value) { _effects.SetLastHaulDepartedUtc(nodeId, value); }

        public DateTime GetLastMinedUtc(string nodeId) { return _effects.GetLastMinedUtc(nodeId); }
        public void SetLastMinedUtc(string nodeId, DateTime value) { _effects.SetLastMinedUtc(nodeId, value); }

        public double InfluenceControl(Vec3 position, string factionTag) { return _world.Influence.Control(position, factionTag); }

        // LastHaulArrivedUtc is never written by Operations (only FleetResolution's Haul arrival
        // handling sets it), so it is always read straight off the live node — same read-only
        // treatment as capabilities/position, and already covered by the guard's node snapshot.
        public DateTime GetLastHaulArrivedUtc(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.LastHaulArrivedUtc : default(DateTime);
        }

        // Staged for an owned node -- the bootstrap Mine mint stamps a cooldown mid-cycle and a
        // second destination's source scan in the same cycle has to see it -- and read straight off
        // the live node for anything the cycle never captured, which is the previous behaviour and
        // is covered by the same guard node snapshot.
        public DateTime GetMineCooldownUntilUtc(string nodeId)
        {
            DateTime staged;
            if (_effects.TryGetMineCooldownUntilUtc(nodeId, out staged)) return staged;
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.MineCooldownUntilUtc : default(DateTime);
        }

        public void SetMineCooldownUntilUtc(string nodeId, DateTime value) { _effects.SetMineCooldownUntilUtc(nodeId, value); }

        public IEnumerable<KeyValuePair<string, double>> SnapshotDemandHistory(string nodeId) { return _effects.SnapshotDemandHistory(nodeId); }
        public double GetDemandHistoryValue(string nodeId, string itemKey) { return _effects.GetDemandHistoryValue(nodeId, itemKey); }
        public void SetDemandHistory(string nodeId, string itemKey, double value) { _effects.SetDemandHistory(nodeId, itemKey, value); }
        public void RemoveDemandHistory(string nodeId, string itemKey) { _effects.RemoveDemandHistory(nodeId, itemKey); }

        public Dictionary<string, double> SnapshotPendingDemandHistory(string nodeId)
        {
            return _world.SnapshotPendingDemandHistory(nodeId);
        }

        public void ConsumeDemandHistoryCredit(string nodeId, Dictionary<string, double> expected)
        {
            _effects.StagePendingDemandHistoryConsumption(nodeId, expected);
        }

        public void AppendPendingFleet(PendingFleetAction fleet) { _effects.AppendPendingFleet(fleet); }
        public string MintFleetId(string actorTag, DateTime departedUtc, string targetId, string extra)
        {
            return _effects.MintFleetId(actorTag, departedUtc, targetId, extra);
        }

        public List<DemandEntry> SnapshotItemDemands(string factionTag) { return _effects.SnapshotItemDemands(factionTag); }

        public bool TryGetShipDemand(string factionTag, string kind, string nodeId, string destNodeId, out DemandEntry entry)
        {
            return _effects.TryGetShipDemand(factionTag, kind, nodeId, destNodeId, out entry);
        }

        public bool PostDemand(DemandEntry demand) { return _effects.PostDemand(demand); }

        public bool PostInvestment(InvestmentRequest investment) { return _effects.PostInvestment(investment); }

        public void AddLog(string message) { _effects.AddLog(message); }

        /// <summary>The journal, so a leg priced in this cycle reaches the world's ledger only if
        /// the cycle commits.</summary>
        public IFuelLegSink FuelSink { get { return _effects; } }
    }
}
