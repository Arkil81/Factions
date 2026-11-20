using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.Entities;          // MyCubeGrid
using Sandbox.Game.Entities.Cube;     // MyCubeBlock

namespace Factions
{
    /// <summary>
    /// The agent's half of a stock-fed station (FAC-506 step 2b): the grid work the authority's
    /// <c>OnStationStood</c>/<c>OnStationLootStood</c> do, without the node stock. Stood stations are
    /// stripped and their loot blocks cleared and rolled; the authority answers with grants
    /// (<see cref="NexusStationLedger"/>), which are loaded here and read back on the top-up cadence.
    /// Every station's state goes into each report until its close is acknowledged.
    /// </summary>
    internal sealed class AgentStations
    {
        private sealed class Entry
        {
            public NexusStationState State;
            public List<KeyValuePair<MyCubeBlock, string>> LootBlocks;
            public DateTime ReadUtc;
            public string StationName;
        }

        private readonly FactionTuning _tuning;
        private readonly StationMaterializer _stations;
        private readonly IRandom _rng = new SeededRandom(Environment.TickCount);
        // Every entry not yet acknowledged closed, by stand id, and the newest per node.
        private readonly Dictionary<long, Entry> _byStand = new Dictionary<long, Entry>();
        private readonly Dictionary<string, Entry> _byNode = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, long>> _gridScratch = new List<KeyValuePair<string, long>>();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private long _lastStand;

        /// <summary>The authority's gates, from its latest snapshot.</summary>
        public bool Munitions, Loot;

        /// <summary>Whispers to the players near a node; set by the plugin.</summary>
        public Action<SpatialNode, string> TellObservers;
        /// <summary>The node as the latest snapshot has it, for the loot notice.</summary>
        public Func<string, SpatialNode> NodeLookup;

        public AgentStations(FactionTuning tuning, StationMaterializer stations)
        {
            _tuning = tuning;
            _stations = stations;
            stations.TurretsFromStock = () => Munitions;
            stations.LootFromStock = () => Loot;
            stations.StationStood = OnStood;
            stations.StationClosed = OnClosed;
            stations.LootStood = OnLootStood;
            stations.LootClosed = OnLootClosed;
        }

        public int Count { get { return _byStand.Count; } }

        private Entry Open(string nodeId)
        {
            Entry e;
            if (_byNode.TryGetValue(nodeId, out e) && !e.State.Closed) return e;
            _lastStand = Math.Max(_lastStand + 1, DateTime.UtcNow.Ticks);
            e = new Entry { State = new NexusStationState { Node = nodeId, Stand = _lastStand }, ReadUtc = DateTime.UtcNow };
            _byStand[e.State.Stand] = e;
            _byNode[nodeId] = e;
            return e;
        }

        private string OnStood(string nodeId, List<MyCubeGrid> grids)
        {
            Entry e = Open(nodeId);
            long stripped = FleetMunitionLoader.Strip(grids);
            e.State.AmmoCap = FleetMunitionLoader.WeaponCapacity(grids);
            var seen = new Dictionary<string, long>(StringComparer.Ordinal);
            FleetMunitionLoader.Read(grids, seen);
            e.State.AmmoSeen = seen;
            e.ReadUtc = DateTime.UtcNow;
            return "ammo=from-authority stripped=" + stripped;
        }

        private string OnLootStood(string nodeId, List<MyCubeGrid> grids, bool gunsFromStock)
        {
            var blocks = new List<KeyValuePair<MyCubeBlock, string>>();
            StationLootLoader.LootBlocks(grids, blocks);
            if (blocks.Count == 0) return null;
            Entry e = Open(nodeId);
            long cleared;
            e.State.LootWants = StationLootLoader.Prepare(grids, blocks, gunsFromStock, _rng, out cleared);
            e.LootBlocks = blocks;
            e.StationName = grids != null && grids.Count > 0 && grids[0] != null ? grids[0].DisplayName : null;
            return "loot=from-authority loot_blocks=" + blocks.Count + (cleared > 0 ? " loot_cleared=" + cleared : "");
        }

        private string OnClosed(string nodeId, Dictionary<string, long> observed)
        {
            Entry e;
            if (!_byNode.TryGetValue(nodeId, out e)) return null;
            e.State.Closed = true;
            e.State.AmmoSeen = observed != null ? new Dictionary<string, long>(observed, StringComparer.Ordinal)
                                                : new Dictionary<string, long>(StringComparer.Ordinal);
            return "ammo=to-authority";
        }

        private string OnLootClosed(string nodeId, Dictionary<string, long> observed)
        {
            Entry e;
            if (!_byNode.TryGetValue(nodeId, out e)) return null;
            e.State.Closed = true;
            e.State.LootSeen = observed != null ? new Dictionary<string, long>(observed, StringComparer.Ordinal)
                                                : new Dictionary<string, long>(StringComparer.Ordinal);
            e.LootBlocks = null;
            return "loot=to-authority";
        }

        /// <summary>The authority's answer: loads what has not been loaded, forgets acknowledged closes.</summary>
        public void Apply(NexusStationReply reply)
        {
            if (reply == null) return;
            for (int i = 0; i < reply.Grants.Count; i++)
            {
                NexusStationGrant g = reply.Grants[i];
                Entry e;
                if (g == null || !_byStand.TryGetValue(g.Stand, out e)) continue;
                if (g.CloseAcked)
                {
                    _byStand.Remove(g.Stand);
                    Entry current;
                    if (_byNode.TryGetValue(e.State.Node, out current) && current == e) _byNode.Remove(e.State.Node);
                    continue;
                }
                // A grant that reaches a closed station is never loaded; the close tells the
                // authority so and it returns the grant itself.
                if (e.State.Closed) continue;
                if (g.AmmoGrant != 0 && g.AmmoGrant > e.State.AmmoThrough) LoadAmmo(e, g);
                if (g.LootGrant != 0 && g.LootGrant > e.State.LootThrough) PlaceLoot(e, g);
            }
        }

        private void LoadAmmo(Entry e, NexusStationGrant g)
        {
            CollectGrids(e.State.Node);
            var pool = g.Ammo != null ? new Dictionary<string, long>(g.Ammo, StringComparer.Ordinal)
                                      : new Dictionary<string, long>(StringComparer.Ordinal);
            if (_grids.Count > 0 && pool.Count > 0) FleetMunitionLoader.Load(_grids, pool);
            var unplaced = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> kv in pool) if (kv.Value > 0) unplaced[kv.Key] = kv.Value;
            e.State.AmmoThrough = g.AmmoGrant;
            e.State.AmmoUnplaced = unplaced.Count > 0 ? unplaced : null;
            // Read after the load, so the authority never reads this grant as fired.
            var seen = new Dictionary<string, long>(StringComparer.Ordinal);
            if (_grids.Count > 0) FleetMunitionLoader.Read(_grids, seen);
            e.State.AmmoSeen = seen;
            e.ReadUtc = DateTime.UtcNow;
            _grids.Clear();
        }

        private void PlaceLoot(Entry e, NexusStationGrant g)
        {
            Dictionary<string, long> unplaced;
            long placed = StationLootLoader.Place(e.LootBlocks, g.Loot, out unplaced);
            e.State.LootThrough = g.LootGrant;
            e.State.LootUnplaced = unplaced.Count > 0 ? unplaced : null;
            e.State.LootWants = null;
            SpatialNode node = NodeLookup != null ? NodeLookup(e.State.Node) : null;
            if (node != null && TellObservers != null)
                TellObservers(node, StationLoot.SpawnNotice(e.StationName, node, DateTime.UtcNow, placed));
        }

        /// <summary>Re-reads every standing station whose top-up interval is up, so the authority can
        /// book what was fired and draw the gap. Only after its first grant: before it, the authority
        /// is still answering the stand.</summary>
        public void Tick(DateTime nowUtc)
        {
            if (!Munitions || _byStand.Count == 0 || _tuning.StationMunitionTopUpSeconds <= 0) return;
            TimeSpan interval = TimeSpan.FromSeconds(_tuning.StationMunitionTopUpSeconds);
            foreach (Entry e in _byStand.Values)
            {
                if (e.State.Closed || e.State.AmmoThrough == 0 || nowUtc - e.ReadUtc < interval) continue;
                e.ReadUtc = nowUtc;
                CollectGrids(e.State.Node);
                if (_grids.Count == 0) continue;
                var seen = new Dictionary<string, long>(StringComparer.Ordinal);
                FleetMunitionLoader.Read(_grids, seen);
                e.State.AmmoSeen = seen;
                _grids.Clear();
            }
        }

        public List<NexusStationState> States()
        {
            if (_byStand.Count == 0) return null;
            var list = new List<NexusStationState>(_byStand.Count);
            // Stand order, so a close reaches the authority before the re-stand that replaced it.
            var stands = new List<long>(_byStand.Keys);
            stands.Sort();
            for (int i = 0; i < stands.Count; i++) list.Add(_byStand[stands[i]].State);
            return list;
        }

        private void CollectGrids(string nodeId)
        {
            _grids.Clear();
            _stations.MaterializedStationGrids(_gridScratch);
            for (int i = 0; i < _gridScratch.Count; i++)
                if (_gridScratch[i].Key == nodeId) { StationMaterializer.CollectStation(_gridScratch[i].Value, _grids); break; }
            _gridScratch.Clear();
        }
    }
}
