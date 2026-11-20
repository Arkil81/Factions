using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Seam between <see cref="OperationsManager"/>'s matching/dispatch logic and its WorldModel
    /// effects — the Operations-specific counterpart to <see cref="IProductionExecutionContext"/>.
    /// A <see cref="LiveOperationsExecutionContext"/> passes every call straight through to a
    /// <see cref="WorldModel"/>, reproducing today's synchronous <see cref="OperationsManager.Tick"/>
    /// behavior exactly. A <see cref="StagedOperationsExecutionContext"/> records effects privately in
    /// an <see cref="OperationsStagedWorldEffects"/> journal for one faction's cycle until a guarded
    /// commit publishes them.
    ///
    /// <para>Read-only node metadata (position, capabilities, zone, planet) is not staged here —
    /// it comes straight off the live <see cref="SpatialNode"/> via <see cref="OwnedNodes"/>, exactly
    /// like <see cref="IProductionExecutionContext"/>, since <see cref="OperationsCycleCommitGuard"/>
    /// already invalidates the cycle on any change to it.</para>
    /// </summary>
    public interface IOperationsExecutionContext
    {
        /// <summary>The cycle's single frozen instant.</summary>
        DateTime UtcNow { get; }

        /// <summary>The faction's owned nodes — read-only identity/position/capability metadata,
        /// always live (never staged).</summary>
        IEnumerable<SpatialNode> OwnedNodes(string factionTag);

        long GetNodeInventory(string nodeId, string itemKey);
        void AddNodeInventory(string nodeId, string itemKey, long delta);

        int GetShipStock(string nodeId, string prefabId);
        IEnumerable<KeyValuePair<string, int>> SnapshotShipStock(string nodeId);
        /// <summary><paramref name="viaRoute"/> is a second leg the consumed hull must also clear —
        /// see <see cref="ShipPool.Consume"/>. Null (the default) is a one-hop itinerary.</summary>
        int ConsumeShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, int desiredCount,
            out string prefabId, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null, HaulHullCost cost = null);
        void ReturnShip(string nodeId, string prefabId, int count);
        /// <summary>Hulls of <paramref name="prefabId"/> at <paramref name="nodeId"/> dispatch may take:
        /// stock less the <see cref="SpatialNode.ListedForSale"/> reservation (<see cref="ShipPool.Available(SpatialNode, string)"/>).</summary>
        int AvailableShips(string nodeId, string prefabId);
        /// <summary>Consumes exactly <paramref name="prefabId"/> -- see <see cref="ShipPool.ConsumePrefab"/>.
        /// The caller has already decided the hull is suitable and can fly its legs.</summary>
        int ConsumeShipPrefab(string nodeId, string prefabId, int desiredCount);
        bool HasSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null);

        /// <summary>Whether this node holds a <paramref name="kind"/>-suitable hull at all,
        /// ignoring the <see cref="SpatialNode.ListedForSale"/> reservation that
        /// <see cref="HasSuitableShip"/> subtracts. Asks "does the faction own one", not "may
        /// dispatch take one now" — the two answers differ for a hull parked in a store listing,
        /// and only the first one may gate the bootstrap instant transfer.</summary>
        bool OwnsSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat);

        DateTime GetLastHaulDepartedUtc(string nodeId);
        void SetLastHaulDepartedUtc(string nodeId, DateTime value);
        DateTime GetLastHaulArrivedUtc(string nodeId);

        /// <summary>Sim-clock of the last Mine dispatch that mined this node
        /// (<see cref="SpatialNode.LastMinedUtc"/>) — set at dispatch time, same shape as
        /// <see cref="GetLastHaulDepartedUtc"/>/<see cref="SetLastHaulDepartedUtc"/>.</summary>
        DateTime GetLastMinedUtc(string nodeId);
        void SetLastMinedUtc(string nodeId, DateTime value);

        /// <summary>Absolute deadline before which a node cannot be mined again. Written by the
        /// unstaged <c>FleetResolution</c> pass at Mine dwell resolution, and within an Operations
        /// cycle by the bootstrap Mine mint (<c>MINE_BOOTSTRAP</c>), which has no dwell to scale a
        /// cooldown from and stamps <see cref="FactionTuning.MineBootstrapCooldownSeconds"/>.</summary>
        DateTime GetMineCooldownUntilUtc(string nodeId);

        /// <summary>Stamps the deadline above. Staged for the duration of an Operations cycle
        /// exactly as <see cref="SetLastMinedUtc"/> is, so a bootstrap mint's cooldown publishes
        /// together with the inventory that justifies it, or neither does.</summary>
        void SetMineCooldownUntilUtc(string nodeId, DateTime value);

        /// <summary>Faction influence/control fraction (0..1) at an arbitrary position — the
        /// Operations-context counterpart of <see cref="IProductionExecutionContext.InfluenceControl"/>,
        /// used by Mine dispatch to scale <see cref="Economy.SourceItemYield"/> at a mined node the
        /// same way <c>AccrueIncome</c> scales passive yield.</summary>
        double InfluenceControl(Vec3 position, string factionTag);

        IEnumerable<KeyValuePair<string, double>> SnapshotDemandHistory(string nodeId);
        double GetDemandHistoryValue(string nodeId, string itemKey);
        void SetDemandHistory(string nodeId, string itemKey, double value);
        void RemoveDemandHistory(string nodeId, string itemKey);

        /// <summary>Non-destructive read of this pass's pending DemandHistory credits — the staged
        /// counterpart of <see cref="WorldModel.SnapshotPendingDemandHistory"/>.</summary>
        Dictionary<string, double> SnapshotPendingDemandHistory(string nodeId);

        /// <summary>Consumes exactly the credit set a prior <see cref="SnapshotPendingDemandHistory"/>
        /// call returned for this node — live consumes immediately (matching the old destructive
        /// drain, just split into snapshot+consume so both contexts share one shape); staged defers
        /// the actual <see cref="WorldModel.TryConsumePendingDemandHistory"/> call to commit time.</summary>
        void ConsumeDemandHistoryCredit(string nodeId, Dictionary<string, double> expected);

        void AppendPendingFleet(PendingFleetAction fleet);

        /// <summary>A <see cref="PendingFleetAction.FleetId"/> for a fleet this cycle is about to
        /// dispatch, unique among the faction's fleets -- those already in its roster and those this
        /// cycle has minted ids for. See <see cref="PendingFleetAction.MintId(System.Func{string, bool}, string, DateTime, string, string)"/>.</summary>
        string MintFleetId(string actorTag, DateTime departedUtc, string targetId, string extra);

        List<DemandEntry> SnapshotItemDemands(string factionTag);
        bool TryGetShipDemand(string factionTag, string kind, string nodeId, string destNodeId, out DemandEntry entry);
        bool PostDemand(DemandEntry demand);

        bool PostInvestment(InvestmentRequest investment);

        void AddLog(string message);

        /// <summary>Where a leg priced during this cycle is filed. Null on the live context, which
        /// means straight to <see cref="WorldModel.FuelLedger"/>; the staged context returns its
        /// own journal, so a cycle that is discarded and re-run measures its dispatches once.</summary>
        IFuelLegSink FuelSink { get; }
    }
}
