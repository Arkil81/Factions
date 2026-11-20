using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Immutable read-set guard for one staged production transaction. It never mutates the world.
    /// A future ProductionCycle must reject its staged effects unless this guard still matches at
    /// commit time, preventing a cached production plan from publishing over ownership, inventory,
    /// demand, investment, capability, reservation, or active-build changes made between frames.
    /// <see cref="CapturedUtc"/> is the transaction's frozen semantic time: staged expiry and
    /// elapsed-time calculations must use it, while later wall-clock time alone must not invalidate
    /// the guard. The guard covers only mutable <see cref="WorldModel"/> and <see cref="FactionState"/>
    /// inputs; a future cycle must also freeze its passed tuning and the recipe/catalogue/oracle
    /// decisions it uses before beginning staged work.
    /// </summary>
    public sealed class ProductionCycleCommitGuard
    {
        private readonly string _factionTag;
        private readonly DateTime _dormantUntilUtc;
        private readonly List<NodeSnapshot> _ownedNodes;
        private readonly List<InfluenceSourceSnapshot> _influenceSources;
        private readonly List<DemandEntry> _itemDemands;
        private readonly List<InvestmentRequest> _investments;

        private ProductionCycleCommitGuard(
            FactionState state,
            DateTime capturedUtc,
            List<NodeSnapshot> ownedNodes,
            List<InfluenceSourceSnapshot> influenceSources,
            List<DemandEntry> itemDemands,
            List<InvestmentRequest> investments)
        {
            _factionTag = state.Tag;
            _dormantUntilUtc = state.DormantUntilUtc;
            CapturedUtc = capturedUtc;
            _ownedNodes = ownedNodes;
            _influenceSources = influenceSources;
            _itemDemands = itemDemands;
            _investments = investments;
        }

        public DateTime CapturedUtc { get; private set; }

        public static ProductionCycleCommitGuard Capture(FactionState state, WorldModel world)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (string.IsNullOrEmpty(state.Tag)) throw new ArgumentException("Faction tag is required.", nameof(state));
            if (world == null) throw new ArgumentNullException(nameof(world));

            var nodes = new List<NodeSnapshot>();
            foreach (SpatialNode node in world.Graph.OwnedBy(state.Tag))
                nodes.Add(new NodeSnapshot(node));
            nodes.Sort(NodeSnapshot.CompareById);

            List<InfluenceSourceSnapshot> influenceSources = BuildInfluenceSources(nodes, world);

            List<DemandEntry> demands = world.Demand.SnapshotItemDemands(state.Tag);
            demands.Sort(CompareDemand);
            List<InvestmentRequest> investments = world.Investments.SnapshotFor(state.Tag);
            investments.Sort(CompareInvestment);

            return new ProductionCycleCommitGuard(state, world.UtcNow, nodes, influenceSources, demands, investments);
        }

        /// <summary>
        /// Returns false when the captured faction lifecycle or any production input has changed.
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

            // Re-filter using _ownedNodes (the captured positions), not currentNodes: the owned-node
            // equality check just above already invalidates the guard if any owned position moved, so
            // the reference frame for "in range" cannot have silently drifted underneath this filter.
            List<InfluenceSourceSnapshot> currentInfluenceSources = BuildInfluenceSources(_ownedNodes, world);
            if (!InfluenceSourceListsEqual(_influenceSources, currentInfluenceSources)) return false;

            List<DemandEntry> demands = world.Demand.SnapshotItemDemands(_factionTag);
            demands.Sort(CompareDemand);
            if (!DemandListsEqual(_itemDemands, demands)) return false;

            List<InvestmentRequest> investments = world.Investments.SnapshotFor(_factionTag);
            investments.Sort(CompareInvestment);
            return InvestmentListsEqual(_investments, investments);
        }

        // Influence-source cutoff: InfluenceField.Sample/DominantAt (what Production's
        // ctx.InfluenceControl ultimately calls) skip any source farther than 4x its own radius —
        // contributing exactly zero, not just negligibly. A source outside that range of every one of
        // the faction's own node positions provably cannot affect anything this guard protects, so
        // filtering it out of the invalidation surface loses no correctness; it only stops discarding
        // staged work over changes on the far side of the map that could never have mattered.
        private const double InfluenceCutoffRadiusMultiplier = 4.0;

        private static List<InfluenceSourceSnapshot> BuildInfluenceSources(List<NodeSnapshot> ownedNodes, WorldModel world)
        {
            var sources = new List<InfluenceSourceSnapshot>();
            foreach (SpatialNode candidate in world.Graph.Nodes.Values)
            {
                double radius = InfluenceRadius.For(candidate, world == null ? null : world.Tuning);
                double cutoff = radius * InfluenceCutoffRadiusMultiplier;
                if (IsWithinRangeOfAnyOwnedNode(candidate.Position, ownedNodes, cutoff))
                    sources.Add(new InfluenceSourceSnapshot(candidate, world == null ? null : world.Tuning));
            }
            sources.Sort(InfluenceSourceSnapshot.CompareById);
            return sources;
        }

        private static bool IsWithinRangeOfAnyOwnedNode(Vec3 position, List<NodeSnapshot> ownedNodes, double cutoff)
        {
            foreach (NodeSnapshot owned in ownedNodes)
                if (position.DistanceTo(owned.Position) <= cutoff) return true;
            return false;
        }

        private static bool NodeListsEqual(List<NodeSnapshot> a, List<NodeSnapshot> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        private static bool InfluenceSourceListsEqual(List<InfluenceSourceSnapshot> a, List<InfluenceSourceSnapshot> b)
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
                    || left.Quantity != right.Quantity
                    || left.ExpiresUtc != right.ExpiresUtc)
                    return false;
            }
            return true;
        }

        private static bool InvestmentListsEqual(List<InvestmentRequest> a, List<InvestmentRequest> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                InvestmentRequest left = a[i];
                InvestmentRequest right = b[i];
                if (!StringEquals(left.FactionTag, right.FactionTag)
                    || left.Cap != right.Cap
                    || left.Urgency != right.Urgency
                    || left.ExpiresUtc != right.ExpiresUtc)
                    return false;
            }
            return true;
        }

        private static int CompareDemand(DemandEntry a, DemandEntry b)
        {
            int result = StringComparer.Ordinal.Compare(a.FactionTag, b.FactionTag);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (result != 0) return result;
            return StringComparer.Ordinal.Compare(a.NodeId, b.NodeId);
        }

        private static int CompareInvestment(InvestmentRequest a, InvestmentRequest b)
        {
            int result = StringComparer.Ordinal.Compare(a.FactionTag, b.FactionTag);
            if (result != 0) return result;
            return ((int)a.Cap).CompareTo((int)b.Cap);
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
            private readonly string _id;
            private readonly string _owner;
            private readonly long _stableStationId;
            private readonly double _resourceValue;
            private readonly double _holdStrength;
            private readonly Vec3 _position;
            private readonly List<StringLong> _inventory;
            private readonly List<StringLong> _reservedInventory;
            private readonly List<CapabilityLevel> _capabilities;
            private readonly List<BuildSnapshot> _buildQueue;

            // internal: read by ProductionCycleCommitGuard.BuildInfluenceSources to filter influence
            // sources down to those within range of the faction's own node positions.
            internal Vec3 Position { get { return _position; } }

            // Pause and per-entry Priority are claim inputs the staged pass read, so a mid-cycle
            // change to either must invalidate the commit exactly like an inventory change does.
            private readonly bool _shipyardPaused;

            public NodeSnapshot(SpatialNode node)
            {
                _id = node == null ? null : node.Id;
                _owner = node == null ? null : node.OwnerFactionTag;
                _stableStationId = node == null ? 0 : node.StableStationId;
                _shipyardPaused = node != null && node.ShipyardPaused;
                _resourceValue = node == null ? 0 : node.ResourceValue;
                _holdStrength = node == null ? 0 : node.HoldStrength;
                _position = node == null ? new Vec3() : node.Position;
                _inventory = SnapshotStringLongs(node == null ? null : node.Inventory);
                _reservedInventory = SnapshotStringLongs(node == null ? null : node.ReservedInventory);
                _capabilities = SnapshotCapabilities(node == null ? null : node.Capabilities);
                _buildQueue = SnapshotBuildQueue(node == null ? null : node.BuildQueue);
            }

            public static int CompareById(NodeSnapshot a, NodeSnapshot b)
            {
                return StringComparer.Ordinal.Compare(a._id, b._id);
            }

            public bool Equals(NodeSnapshot other)
            {
                return other != null
                    && StringEquals(_id, other._id)
                    && StringEquals(_owner, other._owner)
                    && _stableStationId == other._stableStationId
                    && _shipyardPaused == other._shipyardPaused
                    && _resourceValue == other._resourceValue
                    && _holdStrength == other._holdStrength
                    && PositionsEqual(_position, other._position)
                    && StringLongListsEqual(_inventory, other._inventory)
                    && StringLongListsEqual(_reservedInventory, other._reservedInventory)
                    && CapabilityListsEqual(_capabilities, other._capabilities)
                    && BuildListsEqual(_buildQueue, other._buildQueue);
            }

            private static List<StringLong> SnapshotStringLongs(Dictionary<string, long> source)
            {
                var result = new List<StringLong>();
                if (source != null)
                    foreach (KeyValuePair<string, long> entry in source)
                        result.Add(new StringLong(entry.Key, entry.Value));
                result.Sort(StringLong.Compare);
                return result;
            }

            private static List<CapabilityLevel> SnapshotCapabilities(Dictionary<NodeCapabilityKind, int> source)
            {
                var result = new List<CapabilityLevel>();
                if (source != null)
                    foreach (KeyValuePair<NodeCapabilityKind, int> entry in source)
                        result.Add(new CapabilityLevel(entry.Key, entry.Value));
                result.Sort(CapabilityLevel.Compare);
                return result;
            }

            private static List<BuildSnapshot> SnapshotBuildQueue(List<BuildQueueEntry> source)
            {
                var result = new List<BuildSnapshot>();
                if (source != null)
                    foreach (BuildQueueEntry entry in source)
                        result.Add(new BuildSnapshot(entry));
                result.Sort(BuildSnapshot.Compare);
                return result;
            }

            private static bool StringLongListsEqual(List<StringLong> a, List<StringLong> b)
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

        private sealed class InfluenceSourceSnapshot
        {
            private readonly string _id;
            private readonly string _owner;
            private readonly Vec3 _position;
            private readonly double _holdStrength;
            private readonly double _radius;

            public InfluenceSourceSnapshot(SpatialNode node, FactionTuning tuning)
            {
                _id = node == null ? null : node.Id;
                _owner = node == null ? null : node.OwnerFactionTag;
                _position = node == null ? new Vec3() : node.Position;
                _holdStrength = node == null ? 0 : node.HoldStrength;
                _radius = InfluenceRadius.For(node, tuning);
            }

            public static int CompareById(InfluenceSourceSnapshot a, InfluenceSourceSnapshot b)
            {
                return StringComparer.Ordinal.Compare(a._id, b._id);
            }

            public bool Equals(InfluenceSourceSnapshot other)
            {
                return other != null
                    && StringEquals(_id, other._id)
                    && StringEquals(_owner, other._owner)
                    && PositionsEqual(_position, other._position)
                    && _holdStrength == other._holdStrength
                    && _radius == other._radius;
            }
        }

        private sealed class BuildSnapshot
        {
            private readonly string _id;
            private readonly string _role;
            private readonly string _prefabId;
            private readonly DateTime _expiresUtc;
            private readonly int _priority;
            private readonly List<StringLong> _bom;
            private readonly List<StringLong> _collected;
            private readonly List<StringLong> _reservedInputs;

            public BuildSnapshot(BuildQueueEntry entry)
            {
                _id = entry == null ? null : entry.Id;
                _role = entry == null ? null : entry.Role;
                _prefabId = entry == null ? null : entry.PrefabId;
                _expiresUtc = entry == null ? DateTime.MinValue : entry.ExpiresUtc;
                _priority = entry == null ? 0 : entry.Priority;
                _bom = Snapshot(entry == null ? null : entry.Bom);
                _collected = Snapshot(entry == null ? null : entry.Collected);
                _reservedInputs = Snapshot(entry == null ? null : entry.ReservedInputs);
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
                    && _priority == other._priority
                    && ListsEqual(_bom, other._bom)
                    && ListsEqual(_collected, other._collected)
                    && ListsEqual(_reservedInputs, other._reservedInputs);
            }

            private static List<StringLong> Snapshot(Dictionary<string, long> source)
            {
                var result = new List<StringLong>();
                if (source != null)
                    foreach (KeyValuePair<string, long> entry in source)
                        result.Add(new StringLong(entry.Key, entry.Value));
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

        private struct StringLong
        {
            private readonly string _key;
            private readonly long _value;
            public StringLong(string key, long value) { _key = key; _value = value; }
            public static int Compare(StringLong a, StringLong b) { return StringComparer.Ordinal.Compare(a._key, b._key); }
            public bool Equals(StringLong other) { return StringEquals(_key, other._key) && _value == other._value; }
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
