using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Immutable read-set guard for one staged Operations transaction (Slice 3) — the Operations
    /// counterpart to <see cref="ProductionCycleCommitGuard"/>. Never mutates the world. A staged
    /// Operations cycle rejects its staged effects unless this guard still matches at commit time,
    /// preventing a cached matching/dispatch plan from publishing over ownership, inventory, ship
    /// stock, demand, pending-fleet, external-claim, or pending-history-credit changes made between
    /// frames. <see cref="CapturedUtc"/> is the cycle's frozen semantic time; staged TTL and
    /// elapsed-time calculations must use it, never a later wall-clock read.
    ///
    /// <para>Deliberately does <b>not</b> guard <see cref="InvestmentRequest"/>s: Operations only ever
    /// writes investments (<see cref="ShipPool.MaybePostLocalShipyardInvestment"/>'s local-Shipyard
    /// trigger), it never reads them for any matching/dispatch decision, so invalidating on an
    /// unrelated manager's investment change would only cost restarts for no correctness benefit.</para>
    ///
    /// <para>Like <see cref="ProductionCycleCommitGuard"/>, this does not guard tuning or catalogue
    /// (<see cref="IPrefabCatalogue"/>/<see cref="IItemVolumeCatalogue"/>/<see cref="IItemMassCatalogue"/>/
    /// <see cref="IPlanetCatalogue"/>) inputs — an accepted, documented gap shared with the Production
    /// precedent; these change only on a plugin config reload, far rarer than the owned-node/demand
    /// surface this guard does cover.</para>
    /// </summary>
    public sealed class OperationsCycleCommitGuard
    {
        private readonly string _factionTag;
        private readonly DateTime _dormantUntilUtc;
        private readonly List<NodeSnapshot> _ownedNodes;
        private readonly List<DemandEntry> _demands;
        private readonly List<FleetSnapshot> _pendingFleets;
        private readonly List<ClaimSnapshot> _externalClaims;
        // nodeId -> captured pending-history-credit snapshot (null entries never stored: an empty/no
        // snapshot is represented by the node simply being absent from this dictionary).
        private readonly Dictionary<string, Dictionary<string, double>> _pendingHistoryCredits;
        // One fingerprint of the whole standing-order list, in rank order. Player-supplied config
        // only -- NextScanUtc is a manager-owned cursor written directly by ScanStandingMineOrder,
        // not staged, so it's deliberately excluded (see that method's doc comment). This is what
        // detects "player changed, re-ranked, paused or removed a standing order while a cycle was
        // mid-flight" and invalidates the stale cycle. A single ordered string rather than a
        // per-order dictionary because RANK now decides which order wins a contested deposit, so a
        // pure reordering with identical contents is a real change the guard must catch.
        private readonly string _standingOrders;

        private OperationsCycleCommitGuard(
            FactionState state,
            DateTime capturedUtc,
            List<NodeSnapshot> ownedNodes,
            List<DemandEntry> demands,
            List<FleetSnapshot> pendingFleets,
            List<ClaimSnapshot> externalClaims,
            Dictionary<string, Dictionary<string, double>> pendingHistoryCredits,
            string standingOrders)
        {
            _factionTag = state.Tag;
            _dormantUntilUtc = state.DormantUntilUtc;
            CapturedUtc = capturedUtc;
            _ownedNodes = ownedNodes;
            _demands = demands;
            _pendingFleets = pendingFleets;
            _externalClaims = externalClaims;
            _pendingHistoryCredits = pendingHistoryCredits;
            _standingOrders = standingOrders;
        }

        public DateTime CapturedUtc { get; private set; }

        public static OperationsCycleCommitGuard Capture(FactionState state, WorldModel world)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrEmpty(state.Tag)) throw new ArgumentException("Faction tag is required.", nameof(state));
            if (world == null) throw new ArgumentNullException(nameof(world));

            var nodes = new List<NodeSnapshot>();
            var pendingHistoryCredits = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (SpatialNode node in world.Graph.OwnedBy(state.Tag))
            {
                nodes.Add(new NodeSnapshot(node));
                Dictionary<string, double> credits = world.SnapshotPendingDemandHistory(node.Id);
                if (credits != null && credits.Count > 0) pendingHistoryCredits[node.Id] = credits;
            }
            nodes.Sort(NodeSnapshot.CompareById);

            List<DemandEntry> demands = world.Demand.SnapshotFactionDemands(state.Tag);
            demands.Sort(CompareDemand);

            var pendingFleets = new List<FleetSnapshot>();
            foreach (PendingFleetAction fleet in state.PendingFleets)
                pendingFleets.Add(new FleetSnapshot(fleet));

            var externalClaims = new List<ClaimSnapshot>();
            foreach (var claim in state.ExternalHaulClaims)
                externalClaims.Add(new ClaimSnapshot(claim.NodeId, claim.ItemKey));
            externalClaims.Sort(ClaimSnapshot.Compare);

            string standingOrders = StandingOrdersFingerprint(state);

            return new OperationsCycleCommitGuard(state, world.UtcNow, nodes, demands,
                pendingFleets, externalClaims, pendingHistoryCredits, standingOrders);
        }

        /// <summary>
        /// Returns false when the captured faction lifecycle or any Operations input has changed.
        /// The caller must discard and rebuild staged work rather than publishing a stale transaction.
        /// </summary>
        public bool IsStillValid(FactionState state, WorldModel world)
        {
            if (state == null || world == null) return false;
            if (!StringEquals(_factionTag, state.Tag) || _dormantUntilUtc != state.DormantUntilUtc) return false;

            var currentNodes = new List<NodeSnapshot>();
            foreach (SpatialNode node in world.Graph.OwnedBy(_factionTag))
                currentNodes.Add(new NodeSnapshot(node));
            currentNodes.Sort(NodeSnapshot.CompareById);
            if (!NodeListsEqual(_ownedNodes, currentNodes)) return false;

            List<DemandEntry> demands = world.Demand.SnapshotFactionDemands(_factionTag);
            demands.Sort(CompareDemand);
            if (!DemandListsEqual(_demands, demands)) return false;

            var currentFleets = new List<FleetSnapshot>();
            foreach (PendingFleetAction fleet in state.PendingFleets)
                currentFleets.Add(new FleetSnapshot(fleet));
            if (!FleetListsEqual(_pendingFleets, currentFleets)) return false;

            var currentClaims = new List<ClaimSnapshot>();
            foreach (var claim in state.ExternalHaulClaims)
                currentClaims.Add(new ClaimSnapshot(claim.NodeId, claim.ItemKey));
            currentClaims.Sort(ClaimSnapshot.Compare);
            if (!ClaimListsEqual(_externalClaims, currentClaims)) return false;

            foreach (NodeSnapshot node in _ownedNodes)
            {
                Dictionary<string, double> expected;
                _pendingHistoryCredits.TryGetValue(node.Id, out expected);
                Dictionary<string, double> current = world.SnapshotPendingDemandHistory(node.Id);
                if (!PendingHistoryEqual(expected, current)) return false;
            }

            if (!StringEquals(StandingOrdersFingerprint(state), _standingOrders)) return false;

            return true;
        }

        /// <summary>Stable string fingerprint of a faction's standing orders IN RANK ORDER, covering
        /// only player-supplied config -- what detects "the player changed the list while a cycle was
        /// mid-flight." Deliberately excludes <see cref="StandingOrder.NextScanUtc"/> and
        /// <see cref="StandingOrder.LastScanLogSignature"/>, both manager-owned cursors, same
        /// reasoning as <see cref="SpatialNode.LastMinedUtc"/>/<see cref="SpatialNode.MineCooldownUntilUtc"/>
        /// not needing guard coverage beyond the node snapshot's own. Mine is the only kind left on
        /// this list: fleet composition moved to FactionState.FleetDoctrine, which Operations never
        /// reads and which therefore needs no coverage here.</summary>
        private static string StandingOrdersFingerprint(FactionState state)
        {
            var sb = new System.Text.StringBuilder();
            foreach (StandingOrder order in state.StandingOrdersByRank())
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(order.Id).Append(':').Append(order.Kind)
                  .Append(':').Append(order.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(':').Append(order.Paused ? '1' : '0')
                  .Append(':').Append(order.HubNodeId)
                  .Append(':').Append(order.PeriodSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (order.OreWeights != null && order.OreWeights.Count > 0)
                {
                    var keys = new List<string>(order.OreWeights.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string k in keys)
                        sb.Append(';').Append(k).Append('=')
                          .Append(order.OreWeights[k].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        private static bool PendingHistoryEqual(Dictionary<string, double> expected, Dictionary<string, double> current)
        {
            int expectedCount = expected == null ? 0 : expected.Count;
            int currentCount = current == null ? 0 : current.Count;
            if (expectedCount != currentCount) return false;
            if (expectedCount == 0) return true;
            foreach (KeyValuePair<string, double> pair in expected)
            {
                double value;
                if (!current.TryGetValue(pair.Key, out value) || value != pair.Value) return false;
            }
            return true;
        }

        private static bool NodeListsEqual(List<NodeSnapshot> a, List<NodeSnapshot> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private static bool DemandListsEqual(List<DemandEntry> a, List<DemandEntry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                DemandEntry left = a[i];
                DemandEntry right = b[i];
                if (!StringEquals(left.FactionTag, right.FactionTag)
                    || !StringEquals(left.Kind, right.Kind)
                    || !StringEquals(left.NodeId, right.NodeId)
                    || !StringEquals(left.DestNodeId, right.DestNodeId)
                    || left.Quantity != right.Quantity
                    || left.ExpiresUtc != right.ExpiresUtc
                    || left.MinCargoLiters != right.MinCargoLiters
                    || !StringEquals(left.RequiredPlanetId, right.RequiredPlanetId)
                    || left.RequiredZone != right.RequiredZone)
                    return false;
            }
            return true;
        }

        private static bool FleetListsEqual(List<FleetSnapshot> a, List<FleetSnapshot> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private static bool ClaimListsEqual(List<ClaimSnapshot> a, List<ClaimSnapshot> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private static int CompareDemand(DemandEntry a, DemandEntry b)
        {
            int result = StringComparer.Ordinal.Compare(a.FactionTag, b.FactionTag);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(a.NodeId, b.NodeId);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.DestNodeId, b.DestNodeId);
        }

        private static bool StringEquals(string a, string b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static bool PositionsEqual(Vec3 a, Vec3 b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        }

        private sealed class NodeSnapshot
        {
            internal readonly string Id;
            private readonly string _owner;
            private readonly Vec3 _position;
            private readonly NodeZone _zone;
            private readonly string _planetId;
            private readonly List<StringLong> _inventory;
            private readonly List<StringInt> _shipStock;
            private readonly List<StringInt> _listedForSale;
            private readonly List<CapabilityLevel> _capabilities;
            private readonly List<BuildSnapshot> _buildQueue;
            private readonly List<StringDouble> _demandHistory;
            private readonly DateTime _lastHaulDepartedUtc;
            private readonly DateTime _lastHaulArrivedUtc;
            private readonly DateTime _lastMinedUtc;
            private readonly DateTime _mineCooldownUntilUtc;

            public NodeSnapshot(SpatialNode node)
            {
                Id = node.Id;
                _owner = node.OwnerFactionTag;
                _position = node.Position;
                _zone = node.Zone;
                _planetId = node.PlanetId;
                _inventory = SnapshotStringLongs(node.Inventory);
                _shipStock = SnapshotStringInts(node.ShipStock);
                _listedForSale = SnapshotStringInts(node.ListedForSale);
                _capabilities = SnapshotCapabilities(node.Capabilities);
                _buildQueue = SnapshotBuildQueue(node.BuildQueue);
                _demandHistory = SnapshotStringDoubles(node.DemandHistory);
                _lastHaulDepartedUtc = node.LastHaulDepartedUtc;
                _lastHaulArrivedUtc = node.LastHaulArrivedUtc;
                _lastMinedUtc = node.LastMinedUtc;
                _mineCooldownUntilUtc = node.MineCooldownUntilUtc;
            }

            public static int CompareById(NodeSnapshot a, NodeSnapshot b)
            {
                return StringComparer.Ordinal.Compare(a.Id, b.Id);
            }

            public bool Equals(NodeSnapshot other)
            {
                return other != null
                    && StringEquals(Id, other.Id)
                    && StringEquals(_owner, other._owner)
                    && PositionsEqual(_position, other._position)
                    && _zone == other._zone
                    && StringEquals(_planetId, other._planetId)
                    && StringLongListsEqual(_inventory, other._inventory)
                    && StringIntListsEqual(_shipStock, other._shipStock)
                    && StringIntListsEqual(_listedForSale, other._listedForSale)
                    && CapabilityListsEqual(_capabilities, other._capabilities)
                    && BuildListsEqual(_buildQueue, other._buildQueue)
                    && StringDoubleListsEqual(_demandHistory, other._demandHistory)
                    && _lastHaulDepartedUtc == other._lastHaulDepartedUtc
                    && _lastHaulArrivedUtc == other._lastHaulArrivedUtc
                    && _lastMinedUtc == other._lastMinedUtc
                    && _mineCooldownUntilUtc == other._mineCooldownUntilUtc;
            }

            private static List<StringLong> SnapshotStringLongs(Dictionary<string, long> source)
            {
                var result = new List<StringLong>();
                foreach (KeyValuePair<string, long> entry in source) result.Add(new StringLong(entry.Key, entry.Value));
                result.Sort(StringLong.Compare);
                return result;
            }

            private static List<StringInt> SnapshotStringInts(Dictionary<string, int> source)
            {
                var result = new List<StringInt>();
                foreach (KeyValuePair<string, int> entry in source) result.Add(new StringInt(entry.Key, entry.Value));
                result.Sort(StringInt.Compare);
                return result;
            }

            private static List<StringDouble> SnapshotStringDoubles(Dictionary<string, double> source)
            {
                var result = new List<StringDouble>();
                foreach (KeyValuePair<string, double> entry in source) result.Add(new StringDouble(entry.Key, entry.Value));
                result.Sort(StringDouble.Compare);
                return result;
            }

            private static List<CapabilityLevel> SnapshotCapabilities(Dictionary<NodeCapabilityKind, int> source)
            {
                var result = new List<CapabilityLevel>();
                foreach (KeyValuePair<NodeCapabilityKind, int> entry in source) result.Add(new CapabilityLevel(entry.Key, entry.Value));
                result.Sort(CapabilityLevel.Compare);
                return result;
            }

            private static List<BuildSnapshot> SnapshotBuildQueue(List<BuildQueueEntry> source)
            {
                var result = new List<BuildSnapshot>();
                foreach (BuildQueueEntry entry in source) result.Add(new BuildSnapshot(entry));
                result.Sort(BuildSnapshot.Compare);
                return result;
            }

            private static bool StringLongListsEqual(List<StringLong> a, List<StringLong> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            private static bool StringIntListsEqual(List<StringInt> a, List<StringInt> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            private static bool StringDoubleListsEqual(List<StringDouble> a, List<StringDouble> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            private static bool CapabilityListsEqual(List<CapabilityLevel> a, List<CapabilityLevel> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            private static bool BuildListsEqual(List<BuildSnapshot> a, List<BuildSnapshot> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }
        }

        private sealed class BuildSnapshot
        {
            private readonly string _id;
            private readonly string _role;
            private readonly string _prefabId;
            private readonly DateTime _expiresUtc;
            private readonly List<StringLong> _bom;
            private readonly List<StringLong> _collected;

            public BuildSnapshot(BuildQueueEntry entry)
            {
                _id = entry.Id;
                _role = entry.Role;
                _prefabId = entry.PrefabId;
                _expiresUtc = entry.ExpiresUtc;
                _bom = Snapshot(entry.Bom);
                _collected = Snapshot(entry.Collected);
            }

            public static int Compare(BuildSnapshot a, BuildSnapshot b)
            {
                return StringComparer.Ordinal.Compare(a._id, b._id);
            }

            public bool Equals(BuildSnapshot other)
            {
                return other != null
                    && StringEquals(_id, other._id)
                    && StringEquals(_role, other._role)
                    && StringEquals(_prefabId, other._prefabId)
                    && _expiresUtc == other._expiresUtc
                    && ListsEqual(_bom, other._bom)
                    && ListsEqual(_collected, other._collected);
            }

            private static List<StringLong> Snapshot(Dictionary<string, long> source)
            {
                var result = new List<StringLong>();
                foreach (KeyValuePair<string, long> entry in source) result.Add(new StringLong(entry.Key, entry.Value));
                result.Sort(StringLong.Compare);
                return result;
            }

            private static bool ListsEqual(List<StringLong> a, List<StringLong> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }
        }

        /// <summary>Value snapshot of one pending fleet — full field equality, not just FleetId,
        /// since matching reads Kind/Resolved/Intercepted/target/cargo/pickup fields directly.</summary>
        private sealed class FleetSnapshot
        {
            private readonly string _fleetId;
            private readonly ActionKind _kind;
            private readonly bool _resolved;
            private readonly bool _intercepted;
            private readonly string _targetNodeId;
            private readonly List<StringLong> _cargo;
            private readonly FleetStopKind _currentStopKind;
            private readonly string _pickupItemKey;
            private readonly string _relocationRole;
            private readonly long _pickupQuantity;
            private readonly List<StringDouble> _mineWeights;
            private readonly double _mineTargetLiters;
            private readonly List<StopSnapshot> _remainingStops;

            public FleetSnapshot(PendingFleetAction fleet)
            {
                _fleetId = fleet.FleetId;
                _kind = fleet.Kind;
                _resolved = fleet.Resolved;
                _intercepted = fleet.Intercepted;
                _targetNodeId = fleet.TargetNodeId;
                _cargo = Snapshot(fleet.Cargo);
                _currentStopKind = fleet.CurrentStopKind;
                _pickupItemKey = fleet.PickupItemKey;
                _relocationRole = fleet.RelocationRole;
                _pickupQuantity = fleet.PickupQuantity;
                _mineWeights = SnapshotDoubles(fleet.MineWeights);
                _mineTargetLiters = fleet.MineTargetLiters;
                _remainingStops = new List<StopSnapshot>();
                if (fleet.RemainingStops != null)
                    foreach (FleetStop stop in fleet.RemainingStops)
                        _remainingStops.Add(new StopSnapshot(stop));
            }

            public bool Equals(FleetSnapshot other)
            {
                return other != null
                    && StringEquals(_fleetId, other._fleetId)
                    && _kind == other._kind
                    && _resolved == other._resolved
                    && _intercepted == other._intercepted
                    && StringEquals(_targetNodeId, other._targetNodeId)
                    && ListsEqual(_cargo, other._cargo)
                    && _currentStopKind == other._currentStopKind
                    && StringEquals(_pickupItemKey, other._pickupItemKey)
                    && StringEquals(_relocationRole, other._relocationRole)
                    && _pickupQuantity == other._pickupQuantity
                    && DoubleListsEqual(_mineWeights, other._mineWeights)
                    && _mineTargetLiters == other._mineTargetLiters
                    && StopsEqual(_remainingStops, other._remainingStops);
            }

            private static List<StringLong> Snapshot(Dictionary<string, long> source)
            {
                var result = new List<StringLong>();
                if (source != null)
                    foreach (KeyValuePair<string, long> entry in source) result.Add(new StringLong(entry.Key, entry.Value));
                result.Sort(StringLong.Compare);
                return result;
            }

            /// <summary>Null for an absent or empty weight map. Only Mine stops carry one, while a
            /// routed detour queues a run of clearance stops that never do — and this runs twice per
            /// operations cycle per faction, so the empty-list allocation is worth skipping.</summary>
            private static List<StringDouble> SnapshotDoubles(Dictionary<string, double> source)
            {
                if (source == null || source.Count == 0) return null;
                var result = new List<StringDouble>();
                foreach (KeyValuePair<string, double> entry in source) result.Add(new StringDouble(entry.Key, entry.Value));
                result.Sort(StringDouble.Compare);
                return result;
            }

            private static bool ListsEqual(List<StringLong> a, List<StringLong> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            private static bool DoubleListsEqual(List<StringDouble> a, List<StringDouble> b)
            {
                if (a == null || b == null) return a == null && b == null;
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            private static bool StopsEqual(List<StopSnapshot> a, List<StopSnapshot> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            private struct StopSnapshot
            {
                private readonly string _nodeId;
                private readonly FleetStopKind _kind;
                private readonly Vec3 _positionKm;
                private readonly bool _hasPosition;
                private readonly string _pickupItemKey;
                private readonly long _pickupQuantity;
                private readonly double _durationSeconds;
                private readonly List<StringDouble> _mineWeights;
                private readonly double _mineTargetLiters;

                public StopSnapshot(FleetStop stop)
                {
                    _nodeId = stop.NodeId;
                    _kind = stop.Kind;
                    _positionKm = stop.PositionKm;
                    _hasPosition = stop.HasPosition;
                    _pickupItemKey = stop.PickupItemKey;
                    _pickupQuantity = stop.PickupQuantity;
                    _durationSeconds = stop.DurationSeconds;
                    _mineWeights = SnapshotDoubles(stop.MineWeights);
                    _mineTargetLiters = stop.MineTargetLiters;
                }

                public bool Equals(StopSnapshot other)
                {
                    return StringEquals(_nodeId, other._nodeId)
                        && _kind == other._kind
                        && PositionsEqual(_positionKm, other._positionKm)
                        && _hasPosition == other._hasPosition
                        && StringEquals(_pickupItemKey, other._pickupItemKey)
                        && _pickupQuantity == other._pickupQuantity
                        && _durationSeconds == other._durationSeconds
                        && DoubleListsEqual(_mineWeights, other._mineWeights)
                        && _mineTargetLiters == other._mineTargetLiters;
                }
            }
        }

        private struct ClaimSnapshot
        {
            private readonly string _nodeId;
            private readonly string _itemKey;
            public ClaimSnapshot(string nodeId, string itemKey) { _nodeId = nodeId; _itemKey = itemKey; }
            public static int Compare(ClaimSnapshot a, ClaimSnapshot b)
            {
                int result = StringComparer.Ordinal.Compare(a._nodeId, b._nodeId);
                return result != 0 ? result : StringComparer.Ordinal.Compare(a._itemKey, b._itemKey);
            }
            public bool Equals(ClaimSnapshot other) { return StringEquals(_nodeId, other._nodeId) && StringEquals(_itemKey, other._itemKey); }
        }

        private struct StringLong
        {
            private readonly string _key;
            private readonly long _value;
            public StringLong(string key, long value) { _key = key; _value = value; }
            public static int Compare(StringLong a, StringLong b) { return StringComparer.Ordinal.Compare(a._key, b._key); }
            public bool Equals(StringLong other) { return StringEquals(_key, other._key) && _value == other._value; }
        }

        private struct StringInt
        {
            private readonly string _key;
            private readonly int _value;
            public StringInt(string key, int value) { _key = key; _value = value; }
            public static int Compare(StringInt a, StringInt b) { return StringComparer.Ordinal.Compare(a._key, b._key); }
            public bool Equals(StringInt other) { return StringEquals(_key, other._key) && _value == other._value; }
        }

        private struct StringDouble
        {
            private readonly string _key;
            private readonly double _value;
            public StringDouble(string key, double value) { _key = key; _value = value; }
            public static int Compare(StringDouble a, StringDouble b) { return StringComparer.Ordinal.Compare(a._key, b._key); }
            public bool Equals(StringDouble other) { return StringEquals(_key, other._key) && _value == other._value; }
        }

        private struct CapabilityLevel
        {
            private readonly NodeCapabilityKind _cap;
            private readonly int _level;
            public CapabilityLevel(NodeCapabilityKind cap, int level) { _cap = cap; _level = level; }
            public static int Compare(CapabilityLevel a, CapabilityLevel b) { return ((int)a._cap).CompareTo((int)b._cap); }
            public bool Equals(CapabilityLevel other) { return _cap == other._cap && _level == other._level; }
        }
    }
}
