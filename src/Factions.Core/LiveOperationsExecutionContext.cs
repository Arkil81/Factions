using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Thin passthrough <see cref="IOperationsExecutionContext"/> backed directly by a
    /// <see cref="WorldModel"/> — every read and write takes effect immediately, reproducing today's
    /// synchronous <see cref="OperationsManager.Tick"/> behavior exactly. <see cref="UtcNow"/> is
    /// frozen at construction to <c>world.UtcNow</c>; nothing else advances it mid-tick in the
    /// existing synchronous host loop, so this is a no-op change in observed behavior.
    /// </summary>
    public sealed class LiveOperationsExecutionContext : IOperationsExecutionContext
    {
        private readonly WorldModel _world;
        private readonly FactionState _state;
        private readonly List<string> _log;

        public LiveOperationsExecutionContext(WorldModel world, FactionState state, List<string> log)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            UtcNow = world.UtcNow;
        }

        public DateTime UtcNow { get; private set; }

        public IEnumerable<SpatialNode> OwnedNodes(string factionTag) { return _world.Graph.OwnedBy(factionTag); }

        public long GetNodeInventory(string nodeId, string itemKey) { return _world.GetNodeInventory(nodeId, itemKey); }
        public void AddNodeInventory(string nodeId, string itemKey, long delta) { _world.AddNodeInventory(nodeId, itemKey, delta); }

        public int GetShipStock(string nodeId, string prefabId)
        {
            SpatialNode node;
            int value;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) && node.ShipStock.TryGetValue(prefabId, out value) ? value : 0;
        }

        public IEnumerable<KeyValuePair<string, int>> SnapshotShipStock(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.ShipStock : System.Linq.Enumerable.Empty<KeyValuePair<string, int>>();
        }

        public int ConsumeShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, int desiredCount,
            out string prefabId, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null, HaulHullCost cost = null)
        {
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) { prefabId = null; return 0; }
            return ShipPool.Consume(node, kind, cat, desiredCount, out prefabId, route, viaRoute, cost);
        }

        public int AvailableShips(string nodeId, string prefabId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? ShipPool.Available(node, prefabId) : 0;
        }

        public int ConsumeShipPrefab(string nodeId, string prefabId, int desiredCount)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node)
                ? ShipPool.ConsumePrefab(node.ShipStock, node.ListedForSale, prefabId, desiredCount) : 0;
        }

        public void ReturnShip(string nodeId, string prefabId, int count)
        {
            SpatialNode node;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out node)) ShipPool.Return(node, prefabId, count);
        }

        public bool HasSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) && ShipPool.HasSuitable(node, kind, cat, route, viaRoute);
        }

        public bool OwnsSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node)
                && ShipPool.HasSuitable((IReadOnlyDictionary<string, int>)node.ShipStock, null, kind, cat, null);
        }

        public DateTime GetLastHaulDepartedUtc(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.LastHaulDepartedUtc : default(DateTime);
        }

        public void SetLastHaulDepartedUtc(string nodeId, DateTime value)
        {
            SpatialNode node;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out node)) node.LastHaulDepartedUtc = value;
        }

        public DateTime GetLastHaulArrivedUtc(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.LastHaulArrivedUtc : default(DateTime);
        }

        public DateTime GetLastMinedUtc(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.LastMinedUtc : default(DateTime);
        }

        public void SetLastMinedUtc(string nodeId, DateTime value)
        {
            SpatialNode node;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out node)) node.LastMinedUtc = value;
        }

        public DateTime GetMineCooldownUntilUtc(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.MineCooldownUntilUtc : default(DateTime);
        }

        public void SetMineCooldownUntilUtc(string nodeId, DateTime value)
        {
            SpatialNode node;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out node)) node.MineCooldownUntilUtc = value;
        }

        public double InfluenceControl(Vec3 position, string factionTag) { return _world.Influence.Control(position, factionTag); }

        public IEnumerable<KeyValuePair<string, double>> SnapshotDemandHistory(string nodeId)
        {
            SpatialNode node;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.DemandHistory : System.Linq.Enumerable.Empty<KeyValuePair<string, double>>();
        }

        public double GetDemandHistoryValue(string nodeId, string itemKey)
        {
            SpatialNode node;
            double value;
            return _world.Graph.Nodes.TryGetValue(nodeId, out node) && node.DemandHistory.TryGetValue(itemKey, out value) ? value : 0;
        }

        public void SetDemandHistory(string nodeId, string itemKey, double value)
        {
            SpatialNode node;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out node)) node.DemandHistory[itemKey] = value;
        }

        public void RemoveDemandHistory(string nodeId, string itemKey)
        {
            SpatialNode node;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out node)) node.DemandHistory.Remove(itemKey);
        }

        public Dictionary<string, double> SnapshotPendingDemandHistory(string nodeId)
        {
            return _world.SnapshotPendingDemandHistory(nodeId);
        }

        public void ConsumeDemandHistoryCredit(string nodeId, Dictionary<string, double> expected)
        {
            _world.TryConsumePendingDemandHistory(nodeId, expected);
        }

        public string MintFleetId(string actorTag, DateTime departedUtc, string targetId, string extra)
        {
            return PendingFleetAction.MintId(_state.PendingFleets, actorTag, departedUtc, targetId, extra);
        }

        public void AppendPendingFleet(PendingFleetAction fleet)
        {
            FleetMunitions.Arm(ref fleet, _world, _world.Tuning, _log);
            _state.PendingFleets.Add(fleet);
        }

        public List<DemandEntry> SnapshotItemDemands(string factionTag) { return _world.Demand.SnapshotItemDemands(factionTag); }

        public bool TryGetShipDemand(string factionTag, string kind, string nodeId, string destNodeId, out DemandEntry entry)
        {
            return _world.Demand.TryGet(factionTag, kind, nodeId, destNodeId, out entry);
        }

        public bool PostDemand(DemandEntry demand) { return _world.Demand.Post(demand); }

        public bool PostInvestment(InvestmentRequest investment) { return _world.Investments.Post(investment); }

        public void AddLog(string message) { if (message != null) _log.Add(message); }

        /// <summary>Null: every write on this context lands immediately, so a priced leg goes
        /// straight to the world's ledger.</summary>
        public IFuelLegSink FuelSink { get { return null; } }
    }
}
