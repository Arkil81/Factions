using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Private WorldModel/FactionState effects for one staged Operations cycle (Slice 3). All reads
    /// and writes the cycle's matching/dispatch logic makes go through this journal until
    /// <see cref="TryCommit"/> validates the captured read set and publishes everything at once. This
    /// class deliberately owns no persisted state; an incomplete instance is discarded on reload.
    /// See docs/handover/2026-08-14 HANDOVER.md's read/mutation map for the full effect inventory this
    /// mirrors.
    /// </summary>
    public sealed class OperationsStagedWorldEffects : IFuelLegSink
    {
        private sealed class NodeOverlay
        {
            public readonly Dictionary<string, long> Inventory;
            public readonly Dictionary<string, int> ShipStock;
            // Read-only copy — Operations never mutates store listings, but Consume/HasSuitable need
            // it to compute Available() against the same frozen instant as ShipStock.
            public readonly Dictionary<string, int> ListedForSale;
            public readonly Dictionary<string, double> DemandHistory;
            public DateTime LastHaulDepartedUtc;
            public DateTime LastMinedUtc;
            public DateTime MineCooldownUntilUtc;

            public NodeOverlay(SpatialNode node)
            {
                Inventory = new Dictionary<string, long>(node.Inventory);
                ShipStock = new Dictionary<string, int>(node.ShipStock);
                ListedForSale = new Dictionary<string, int>(node.ListedForSale);
                DemandHistory = new Dictionary<string, double>(node.DemandHistory);
                LastHaulDepartedUtc = node.LastHaulDepartedUtc;
                LastMinedUtc = node.LastMinedUtc;
                MineCooldownUntilUtc = node.MineCooldownUntilUtc;
            }
        }

        private readonly Dictionary<string, NodeOverlay> _nodes;
        private readonly List<PendingFleetAction> _pendingFleets = new List<PendingFleetAction>();
        private readonly List<DemandEntry> _demandPosts = new List<DemandEntry>();
        private readonly List<InvestmentRequest> _investmentPosts = new List<InvestmentRequest>();
        // Upsert-key tracking so PostInvestment's isNew return matches live InvestmentRegister.Post's
        // semantics (false on a same-key refresh) -- without this, MaybePostLocalShipyardInvestment's
        // isNew-gated log line fires once per failed vector instead of once per cycle whenever several
        // vectors in the same cycle route toward the same shipyard-less planet.
        private readonly HashSet<string> _investmentKeys = new HashSet<string>(StringComparer.Ordinal);
        // Staged non-destructive-until-commit consumption of this cycle's pending DemandHistory
        // credits: exactly the snapshot each node captured at Preparing time, replayed at commit via
        // WorldModel.TryConsumePendingDemandHistory's compare-and-consume. Never drained early — see
        // FoldDemandHistory's staged counterpart in OperationsManager.Cycle.cs.
        private readonly Dictionary<string, Dictionary<string, double>> _pendingHistoryCredits
            = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        private readonly List<string> _logs = new List<string>();
        // This cycle's unserved demand rows, held here rather than written straight to FactionState
        // so an invalidated cycle discards them along with every other staged effect. Empty until
        // StageUnservedDemands runs, which is what makes a cycle that ended before matching (no
        // demands, no standing order) clear the faction's previous set at commit.
        private readonly List<UnservedDemand> _unservedDemands = new List<UnservedDemand>();
        // Faction-scoped demand upsert view mirroring DemandRegister.Post's last-write-wins
        // semantics, keyed exactly like DemandRegister's own composite key, so a same-cycle read
        // (PostShipDemand's max-capacity preserve) sees exactly what live Tick() would see.
        private readonly Dictionary<string, DemandEntry> _demandsByKey = new Dictionary<string, DemandEntry>(StringComparer.Ordinal);
        private bool _committed;

        private OperationsStagedWorldEffects(Dictionary<string, NodeOverlay> nodes)
        {
            _nodes = nodes;
        }

        public static OperationsStagedWorldEffects Capture(FactionState state, WorldModel world)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (world == null) throw new ArgumentNullException(nameof(world));

            var nodes = new Dictionary<string, NodeOverlay>(StringComparer.Ordinal);
            foreach (SpatialNode node in world.Graph.OwnedBy(state.Tag))
                nodes[node.Id] = new NodeOverlay(node);

            var effects = new OperationsStagedWorldEffects(nodes);
            foreach (DemandEntry demand in world.Demand.SnapshotFactionDemands(state.Tag))
                effects._demandsByKey[DemandKey(demand)] = demand;
            foreach (InvestmentRequest investment in world.Investments.SnapshotFor(state.Tag))
                effects._investmentKeys.Add(InvestmentKey(investment));
            foreach (PendingFleetAction fleet in state.PendingFleets)
                if (fleet.FleetId != null) effects._heldFleetIds.Add(fleet.FleetId);

            return effects;
        }

        // ---- inventory ----------------------------------------------------------------------------

        public long GetNodeInventory(string nodeId, string itemKey)
        {
            NodeOverlay node;
            long value;
            return _nodes.TryGetValue(nodeId, out node) && node.Inventory.TryGetValue(itemKey, out value) ? value : 0;
        }

        public void AddNodeInventory(string nodeId, string itemKey, long delta)
        {
            NodeOverlay node;
            if (!_nodes.TryGetValue(nodeId, out node) || string.IsNullOrEmpty(itemKey)) return;
            long current;
            node.Inventory.TryGetValue(itemKey, out current);
            long next = current + delta;
            node.Inventory[itemKey] = next < 0 ? 0 : next;
        }

        // ---- ship stock -----------------------------------------------------------------------------

        public int GetShipStock(string nodeId, string prefabId)
        {
            NodeOverlay node;
            int value;
            return _nodes.TryGetValue(nodeId, out node) && node.ShipStock.TryGetValue(prefabId, out value) ? value : 0;
        }

        public IEnumerable<KeyValuePair<string, int>> SnapshotShipStock(string nodeId)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node) ? node.ShipStock : System.Linq.Enumerable.Empty<KeyValuePair<string, int>>();
        }

        public int ConsumeShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, int desiredCount,
            out string prefabId, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null, HaulHullCost cost = null)
        {
            NodeOverlay node;
            if (!_nodes.TryGetValue(nodeId, out node)) { prefabId = null; return 0; }
            return ShipPool.Consume(node.ShipStock, node.ListedForSale, kind, cat, desiredCount, out prefabId, route, viaRoute, cost);
        }

        public int AvailableShips(string nodeId, string prefabId)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node)
                ? ShipPool.Available(node.ShipStock, node.ListedForSale, prefabId) : 0;
        }

        public int ConsumeShipPrefab(string nodeId, string prefabId, int desiredCount)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node)
                ? ShipPool.ConsumePrefab(node.ShipStock, node.ListedForSale, prefabId, desiredCount) : 0;
        }

        public void ReturnShip(string nodeId, string prefabId, int count)
        {
            NodeOverlay node;
            if (_nodes.TryGetValue(nodeId, out node)) ShipPool.Return(node.ShipStock, prefabId, count);
        }

        public bool HasSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat, ShipPool.RouteEnvironment? route,
            ShipPool.RouteEnvironment? viaRoute = null)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node)
                && ShipPool.HasSuitable(node.ShipStock, node.ListedForSale, kind, cat, route, viaRoute);
        }

        public bool OwnsSuitableShip(string nodeId, ActionKind kind, IPrefabCatalogue cat)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node) && ShipPool.HasSuitable(node.ShipStock, null, kind, cat, null);
        }

        // ---- haul timestamps ------------------------------------------------------------------------

        public DateTime GetLastHaulDepartedUtc(string nodeId)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node) ? node.LastHaulDepartedUtc : default(DateTime);
        }

        public void SetLastHaulDepartedUtc(string nodeId, DateTime value)
        {
            NodeOverlay node;
            if (_nodes.TryGetValue(nodeId, out node)) node.LastHaulDepartedUtc = value;
        }

        // ---- mine timestamp --------------------------------------------------------------------------

        public DateTime GetLastMinedUtc(string nodeId)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node) ? node.LastMinedUtc : default(DateTime);
        }

        public void SetLastMinedUtc(string nodeId, DateTime value)
        {
            NodeOverlay node;
            if (_nodes.TryGetValue(nodeId, out node)) node.LastMinedUtc = value;
        }

        /// <summary>False for a node this cycle never captured (anything the faction does not own),
        /// so the caller can fall back to the live value rather than read an unstaged node as
        /// "never cooled". The bootstrap Mine mint only ever stamps an owned deposit, so the staged
        /// branch is the one that matters; the fallback preserves the read for every other caller.</summary>
        public bool TryGetMineCooldownUntilUtc(string nodeId, out DateTime value)
        {
            NodeOverlay node;
            if (_nodes.TryGetValue(nodeId, out node)) { value = node.MineCooldownUntilUtc; return true; }
            value = default(DateTime);
            return false;
        }

        public void SetMineCooldownUntilUtc(string nodeId, DateTime value)
        {
            NodeOverlay node;
            if (_nodes.TryGetValue(nodeId, out node)) node.MineCooldownUntilUtc = value;
        }

        // ---- demand history -------------------------------------------------------------------------

        public IEnumerable<KeyValuePair<string, double>> SnapshotDemandHistory(string nodeId)
        {
            NodeOverlay node;
            return _nodes.TryGetValue(nodeId, out node) ? node.DemandHistory : System.Linq.Enumerable.Empty<KeyValuePair<string, double>>();
        }

        public double GetDemandHistoryValue(string nodeId, string itemKey)
        {
            NodeOverlay node;
            double value;
            return _nodes.TryGetValue(nodeId, out node) && node.DemandHistory.TryGetValue(itemKey, out value) ? value : 0;
        }

        public void SetDemandHistory(string nodeId, string itemKey, double value)
        {
            NodeOverlay node;
            if (_nodes.TryGetValue(nodeId, out node)) node.DemandHistory[itemKey] = value;
        }

        public void RemoveDemandHistory(string nodeId, string itemKey)
        {
            NodeOverlay node;
            if (_nodes.TryGetValue(nodeId, out node)) node.DemandHistory.Remove(itemKey);
        }

        /// <summary>Stages this node's pending-credit snapshot for an exact compare-and-consume at
        /// commit — see <see cref="WorldModel.TryConsumePendingDemandHistory"/>. Called once per
        /// owned node during the Preparing phase with the same snapshot <see cref="OperationsCycleCommitGuard"/>
        /// captured, so a successful guard check guarantees this consume succeeds at commit.</summary>
        public void StagePendingDemandHistoryConsumption(string nodeId, Dictionary<string, double> expected)
        {
            _pendingHistoryCredits[nodeId] = expected;
        }

        // ---- pending fleets -------------------------------------------------------------------------

        public void AppendPendingFleet(PendingFleetAction fleet) { _pendingFleets.Add(fleet); }

        /// <summary>Every id this cycle must not mint again: the faction's roster as captured, and
        /// each id minted since. An id minted for a dispatch that then fails stays reserved, which
        /// costs nothing -- the cycle's next mint simply takes the next ordinal. The commit guard
        /// refuses a cycle whose roster changed after capture, so the captured set is still the
        /// roster the staged fleets join.</summary>
        private readonly HashSet<string> _heldFleetIds = new HashSet<string>(StringComparer.Ordinal);

        public string MintFleetId(string actorTag, DateTime departedUtc, string targetId, string extra)
        {
            string id = PendingFleetAction.MintId(_heldFleetIds.Contains, actorTag, departedUtc, targetId, extra);
            _heldFleetIds.Add(id);
            return id;
        }

        // ---- fuel ledger ----------------------------------------------------------------------------

        private readonly List<FuelLegRecord> _fuelLegs = new List<FuelLegRecord>();
        private readonly List<string> _fuelLogs = new List<string>();

        /// <summary>Buffers a leg the cycle priced. A discarded cycle never dispatched those legs,
        /// so it must not have measured them either.</summary>
        public void RecordFuelLeg(FuelLegRecord record) { _fuelLegs.Add(record); }

        /// <summary>The leg's FUEL_DRAWN line, buffered with the record it belongs to.</summary>
        public void AddFuelLog(string line) { if (line != null) _fuelLogs.Add(line); }

        // ---- demand register ------------------------------------------------------------------------

        public List<DemandEntry> SnapshotItemDemands(string factionTag)
        {
            var result = new List<DemandEntry>();
            foreach (DemandEntry d in _demandsByKey.Values)
                if (string.Equals(d.FactionTag, factionTag, StringComparison.Ordinal)
                    && d.Kind != null && d.Kind.StartsWith("Item:", StringComparison.Ordinal))
                    result.Add(d);
            return result;
        }

        public bool TryGetShipDemand(string factionTag, string kind, string nodeId, string destNodeId, out DemandEntry entry)
        {
            // OperationsManager only ever posts and reads fleet demands, so this asks for the
            // fleet row explicitly -- the same default the live DemandRegister.TryGet takes.
            return _demandsByKey.TryGetValue(
                DemandKey(factionTag, kind, nodeId, destNodeId, false, null), out entry);
        }

        public bool PostDemand(DemandEntry demand)
        {
            string key = DemandKey(demand);
            DemandEntry prior;
            bool isNew = !_demandsByKey.TryGetValue(key, out prior);
            // Same refresh-preserve contract as live DemandRegister.Post -- the staged entry is what
            // TryGetShipDemand/SnapshotItemDemands read back this cycle, and it is what the commit
            // replays, so dropping a preserved field here loses it for good.
            if (!isNew) demand = DemandEntry.PreserveOnRefresh(prior, demand);
            _demandsByKey[key] = demand;
            _demandPosts.Add(demand);
            return isNew;
        }

        // ---- investments ----------------------------------------------------------------------------

        public bool PostInvestment(InvestmentRequest investment)
        {
            _investmentPosts.Add(investment);
            return _investmentKeys.Add(InvestmentKey(investment));
        }

        // ---- unserved demands ---------------------------------------------------------------------

        /// <summary>Stages the rows this cycle's matching found no owned source for. Replaces
        /// anything staged earlier, so a restarted phase cannot double-publish a row.</summary>
        public void StageUnservedDemands(IEnumerable<UnservedDemand> rows)
        {
            _unservedDemands.Clear();
            if (rows == null) return;
            foreach (UnservedDemand row in rows) _unservedDemands.Add(row);
        }

        // ---- logs -------------------------------------------------------------------------------------

        public void AddLog(string message)
        {
            if (message != null) _logs.Add(message);
        }

        /// <summary>The node ids captured for this cycle — the faction's owned nodes at begin time.</summary>
        public IEnumerable<string> NodeIds { get { return _nodes.Keys; } }

        /// <summary>
        /// Atomically publishes this journal only if the captured Operations read set still holds.
        /// Pending DemandHistory credit consumption is attempted first — see <see
        /// cref="StagePendingDemandHistoryConsumption"/> — and if any staged consume unexpectedly
        /// fails (should never happen once the guard has already passed, since nothing else runs
        /// between the guard check and this call in Torch's single-threaded manager tick), the whole
        /// commit aborts before anything else publishes, preserving atomicity.
        /// </summary>
        public bool TryCommit(OperationsCycleCommitGuard guard, FactionState state, WorldModel world, List<string> log)
        {
            if (_committed || guard == null || state == null || world == null || !guard.IsStillValid(state, world))
                return false;

            foreach (var credit in _pendingHistoryCredits)
                if (!world.TryConsumePendingDemandHistory(credit.Key, credit.Value))
                    return false;

            foreach (KeyValuePair<string, NodeOverlay> entry in _nodes)
            {
                SpatialNode live;
                if (!world.Graph.Nodes.TryGetValue(entry.Key, out live)) return false;
                Replace(live.Inventory, entry.Value.Inventory);
                ReplaceInt(live.ShipStock, entry.Value.ShipStock);
                ReplaceDouble(live.DemandHistory, entry.Value.DemandHistory);
                live.LastHaulDepartedUtc = entry.Value.LastHaulDepartedUtc;
                live.LastMinedUtc = entry.Value.LastMinedUtc;
                live.MineCooldownUntilUtc = entry.Value.MineCooldownUntilUtc;
            }
            // Armed here rather than when the fleet was staged, for the reason the fuel draw below
            // is: the overlays have just published, so the draw reads the stock this cycle really
            // leaves behind and a discarded cycle draws nothing.
            foreach (PendingFleetAction staged in _pendingFleets)
            {
                PendingFleetAction fleet = staged;
                FleetMunitions.Arm(ref fleet, world, world.Tuning, log);
                state.PendingFleets.Add(fleet);
            }
            if (world.FuelLedger != null)
            {
                // The draw runs here, not when the leg was priced: the overlays above have just
                // published, so it reads the stock this cycle really leaves behind, and a cycle that
                // was discarded and re-run takes the fuel exactly once.
                for (int i = 0; i < _fuelLegs.Count; i++)
                {
                    FuelLegRecord leg = _fuelLegs[i];
                    leg.Draw = DrawForLeg(leg, state, world, log);
                    world.FuelLedger.Record(leg);
                }
            }
            if (log != null) log.AddRange(_fuelLogs);
            OperationsManager.PublishUnservedDemands(state, _unservedDemands);
            foreach (DemandEntry demand in _demandPosts) world.Demand.Post(demand);
            foreach (InvestmentRequest investment in _investmentPosts) world.Investments.Post(investment);
            if (log != null) log.AddRange(_logs);
            _committed = true;
            return true;
        }

        /// <summary>One committed leg's draw, or null when the mode is below
        /// <see cref="FuelDrawModes.Draw"/>, the site named no origin, or the node has since gone.
        /// The FUEL_IMPORTED line goes into the same log the cycle's other lines do.</summary>
        private static FuelDrawResult DrawForLeg(FuelLegRecord leg, FactionState state, WorldModel world,
                                                 List<string> log)
        {
            if (world.Tuning == null) return null;
            if (string.IsNullOrEmpty(leg.OriginNodeId) || leg.Burn == null) return null;

            SpatialNode node;
            if (!world.Graph.Nodes.TryGetValue(leg.OriginNodeId, out node)) return null;

            if (world.Tuning.FuelDrawMode < FuelDrawModes.Draw)
            {
                FuelDraw.MeterBurn(node, leg.Burn, world.UtcNow, world.Tuning);
                return null;
            }

            FuelDrawResult draw = FuelDraw.TryFuel(node, leg.Burn, state, world, world.Tuning);
            if (!draw.Any) return null;
            if (DebugTraces.Fuel && log != null && FuelMeter.ShouldReport(draw))
                log.Add(FuelMeter.FormatImport(leg.LogId, state.Tag, node.Id, draw));
            return draw;
        }

        private static void Replace(Dictionary<string, long> target, Dictionary<string, long> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, long> item in source) target.Add(item.Key, item.Value);
        }

        private static void ReplaceInt(Dictionary<string, int> target, Dictionary<string, int> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, int> item in source) target.Add(item.Key, item.Value);
        }

        private static void ReplaceDouble(Dictionary<string, double> target, Dictionary<string, double> source)
        {
            target.Clear();
            foreach (KeyValuePair<string, double> item in source) target.Add(item.Key, item.Value);
        }

        private static string DemandKey(DemandEntry demand)
        {
            return DemandKey(demand.FactionTag, demand.Kind, demand.NodeId, demand.DestNodeId,
                demand.DestinedForSale, demand.RequiredGridSize);
        }

        private static string DemandKey(string factionTag, string kind, string nodeId, string destNodeId,
            bool destinedForSale, string requiredGridSize)
        {
            return (factionTag ?? string.Empty) + "" + (kind ?? string.Empty) + ""
                + (nodeId ?? string.Empty) + "" + (destNodeId ?? string.Empty) + ""
                + (destinedForSale ? "1" : "0") + "" + (requiredGridSize ?? string.Empty);
        }

        private static string InvestmentKey(InvestmentRequest investment)
        {
            return (investment.FactionTag ?? string.Empty) + "" + ((int)investment.Cap).ToString()
                + "" + (investment.TargetPlanetId ?? string.Empty)
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
