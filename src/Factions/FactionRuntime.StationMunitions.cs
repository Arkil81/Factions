using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Sandbox.Game.Entities;    // MyCubeGrid

namespace Factions
{
    /// <summary>
    /// A materialised station's ammunition, host half (FAC-441; rules in Core's
    /// <see cref="StationMunitions"/>). When a station stands, its scan's or prefab's magazines are
    /// stripped and a load is drawn from its node; every
    /// <see cref="FactionTuning.StationMunitionTopUpSeconds"/> its grids are read, what went is booked
    /// as spent, and the load is drawn back up; when it despawns, what is aboard is credited back.
    /// Vanilla's free turret refill is held off by <see cref="StationTurretRefillPatch"/>.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        // Per standing station: the load it is kept at, fixed when it stands, and when it was last
        // read. Both dropped when it despawns.
        private readonly Dictionary<string, Dictionary<string, long>> _stationAmmoTarget =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _stationAmmoReadUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, long>> _stationAmmoScratch = new List<KeyValuePair<string, long>>();
        private readonly List<MyCubeGrid> _stationAmmoGrids = new List<MyCubeGrid>();

        // The stations standing on Nexus agents, whose stock is drawn here.
        private readonly NexusStationLedger _agentStations = new NexusStationLedger();

        /// <summary>Stations this authority feeds on Nexus agents.</summary>
        public int AgentStationCount { get { return _agentStations.Count; } }

        /// <summary>Whether stations' guns are fed from stock — the gate an agent is told in the snapshot.</summary>
        public bool StationGunsFromStock { get { return StationMunitions.Enabled(_tuning); } }

        /// <summary>Applies one agent's station states to the world and returns the reply owed to it.</summary>
        public NexusStationReply ApplyAgentStations(byte home, NexusAgentReport report, out NexusStationTally tally)
        {
            tally = new NexusStationTally();
            if (_world == null || report == null) return new NexusStationReply();
            TimeSpan interval = TimeSpan.FromSeconds(Math.Max(0, _tuning.StationMunitionTopUpSeconds));
            return _agentStations.Apply(home, report.Session, report.Stations, _world,
                StationMunitions.Enabled(_tuning), FeatureSwitch.IsOn(FeatureSwitch.StationLoot),
                interval, DateTime.UtcNow, out tally);
        }

        private void ConfigureStationMunitions()
        {
            _stationMaterializer.TurretsFromStock = () => StationMunitions.Enabled(_tuning);
            _stationMaterializer.StationStood = OnStationStood;
            _stationMaterializer.StationClosed = OnStationClosed;
        }

        /// <summary>Hands back the magazines every station grid held when the server last stopped —
        /// none of them is saved. Call once, after load, before any station stands.</summary>
        public void CreditStrandedStationMunitions()
        {
            if (_world == null) return;
            int nodes;
            long credited = StationMunitions.CreditAfterRestart(_world, out nodes);
            if (credited > 0)
                FactionsPlugin.Log.Info("Station munitions: credited " + credited.ToString(CultureInfo.InvariantCulture)
                    + " magazine(s) back to " + nodes.ToString(CultureInfo.InvariantCulture)
                    + " node(s) from station grids that did not survive the restart.");
        }

        private string OnStationStood(string nodeId, List<MyCubeGrid> grids)
        {
            SpatialNode node;
            if (_world == null || nodeId == null || !_world.Graph.Nodes.TryGetValue(nodeId, out node)) return null;

            long stripped = FleetMunitionLoader.Strip(grids);
            Dictionary<string, long> target = StationMunitions.FullLoad(node, _world.PrefabCatalogue, _tuning);
            if (target.Count == 0) target = FleetMunitionLoader.WeaponCapacity(grids);
            _stationAmmoTarget[nodeId] = target;
            _stationAmmoReadUtc[nodeId] = DateTime.UtcNow;

            long spent, drawn, placed;
            TopUpStation(node, grids, target, out spent, out drawn, out placed);
            return "ammo=" + StationMunitions.Aboard(node).ToString(CultureInfo.InvariantCulture)
                 + "/" + Sum(target).ToString(CultureInfo.InvariantCulture)
                 + " stripped=" + stripped.ToString(CultureInfo.InvariantCulture);
        }

        private string OnStationClosed(string nodeId, Dictionary<string, long> observed)
        {
            _stationAmmoTarget.Remove(nodeId);
            _stationAmmoReadUtc.Remove(nodeId);
            SpatialNode node;
            if (_world == null || !_world.Graph.Nodes.TryGetValue(nodeId, out node)) return null;
            long spent;
            long credited = StationMunitions.Credit(_world, node, observed, out spent);
            return "ammo_back=" + credited.ToString(CultureInfo.InvariantCulture)
                 + " spent=" + spent.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads and tops up every standing stock-fed station whose interval is up. Walks the
        /// standing set, which is the size of what players are near, not of the world.</summary>
        private void TopUpStandingStations(DateTime now)
        {
            if (_world == null || !StationMunitions.Enabled(_tuning) || _tuning.StationMunitionTopUpSeconds <= 0) return;
            _stationMaterializer.MaterializedStationGrids(_stationAmmoScratch);
            TimeSpan interval = TimeSpan.FromSeconds(_tuning.StationMunitionTopUpSeconds);
            for (int i = 0; i < _stationAmmoScratch.Count; i++)
            {
                string nodeId = _stationAmmoScratch[i].Key;
                long gridId = _stationAmmoScratch[i].Value;
                if (!StationMaterializer.StockFedGrids.Contains(gridId)) continue;
                DateTime last;
                if (_stationAmmoReadUtc.TryGetValue(nodeId, out last) && now - last < interval) continue;
                _stationAmmoReadUtc[nodeId] = now;

                SpatialNode node;
                Dictionary<string, long> target;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) continue;
                _stationAmmoGrids.Clear();
                StationMaterializer.CollectStation(gridId, _stationAmmoGrids);
                if (_stationAmmoGrids.Count == 0) continue;
                if (!_stationAmmoTarget.TryGetValue(nodeId, out target))
                {
                    target = StationMunitions.FullLoad(node, _world.PrefabCatalogue, _tuning);
                    if (target.Count == 0) target = FleetMunitionLoader.WeaponCapacity(_stationAmmoGrids);
                    _stationAmmoTarget[nodeId] = target;
                }

                long spent, drawn, placed;
                TopUpStation(node, _stationAmmoGrids, target, out spent, out drawn, out placed);
                if (DebugTraces.Munitions && (spent > 0 || drawn > 0))
                    _log.Write("STATION_MUNITIONS " + (node.OwnerFactionTag ?? "?") + " node=" + nodeId
                               + " spent=" + spent.ToString(CultureInfo.InvariantCulture)
                               + " drawn=" + drawn.ToString(CultureInfo.InvariantCulture)
                               + " placed=" + placed.ToString(CultureInfo.InvariantCulture)
                               + " aboard=" + StationMunitions.Aboard(node).ToString(CultureInfo.InvariantCulture)
                               + "/" + Sum(target).ToString(CultureInfo.InvariantCulture));
            }
            _stationAmmoScratch.Clear();
            _stationAmmoGrids.Clear();
        }

        /// <summary>One read-and-refill: books what the grids no longer hold as spent, draws the gap
        /// to <paramref name="target"/> from node stock, places it, and returns what found no room.</summary>
        private void TopUpStation(SpatialNode node, List<MyCubeGrid> grids, Dictionary<string, long> target,
                                  out long spent, out long drawn, out long placed)
        {
            var observed = new Dictionary<string, long>(StringComparer.Ordinal);
            FleetMunitionLoader.Read(grids, observed);
            spent = StationMunitions.ApplyObserved(_world, node, observed);
            Dictionary<string, long> pool = StationMunitions.Draw(_world, node, target, observed);
            drawn = Sum(pool);
            placed = drawn > 0 ? FleetMunitionLoader.Load(grids, pool) : 0;
            if (placed < drawn) StationMunitions.ReturnUnplaced(_world, node, pool);
        }

        private static long Sum(Dictionary<string, long> load)
        {
            long total = 0;
            foreach (KeyValuePair<string, long> kv in load) if (kv.Value > 0) total += kv.Value;
            return total;
        }
    }
}
