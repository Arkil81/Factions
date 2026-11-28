using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// What a Nexus agent materialises for its own sectors (FAC-506 step 2): the authority's node
    /// graph arrives as a <see cref="NexusNodeSnapshot"/>, local players' positions decide what is
    /// near, and the same materialisers the authority runs place the rock. What they decide that the
    /// authority owns goes back in a <see cref="NexusAgentReport"/>.
    ///
    /// <para>No runtime, no persistence: the graph is replaced whole by each newer snapshot and
    /// nothing here is written to disk. An agent restart loses only the unacknowledged report,
    /// and the stable rock entity ids make re-placing it a no-op.</para>
    ///
    /// <para>Stations stand from the same snapshot, through the authority's own
    /// <see cref="StationMaterializer"/>. Where the authority feeds stations' guns and loot from stock,
    /// <see cref="AgentStations"/> does the grid half and the authority the stock half.</para>
    /// </summary>
    internal sealed class AgentHands
    {
        private readonly FactionTuning _tuning;
        private readonly AsteroidFieldMaterializer _asteroids = new AsteroidFieldMaterializer();
        private readonly FeatureRockMaterializer _featureRock = new FeatureRockMaterializer();
        private readonly bool _featureRockReady;
        private readonly StationMaterializer _stations = new StationMaterializer();
        private CustomPrefabStore _customPrefabs;
        private List<FactionState> _factions = new List<FactionState>();
        private readonly Dictionary<string, FactionState> _factionByTag = new Dictionary<string, FactionState>(StringComparer.Ordinal);
        /// <summary>The snapshot's faction rows, for what the stand-ins do not carry (trade bars).</summary>
        private readonly Dictionary<string, NexusSnapshotFaction> _factionRows = new Dictionary<string, NexusSnapshotFaction>(StringComparer.OrdinalIgnoreCase);
        private bool _claimsSeeded;
        private readonly AgentStations _economy;
        private readonly AgentSiege _siege;
        private StationRecordMirror.Tally _records;
        private readonly AgentFleets _fleets;
        private readonly VanillaFleetSpawner _fleetSpawner;
        private static readonly NLog.Logger Log = NLog.LogManager.GetLogger(FactionsPlugin.PluginName);
        /// <summary>This process's id in every report: a new one tells the authority that the
        /// station grids it held for this server are gone.</summary>
        private readonly string _session = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private SpatialGraph _graph;
        private NodeProximityIndex _index;
        private long _sequence = -1;
        private DateTime _snapshotUtc;

        /// <summary>Fields placed here that the authority's snapshot does not show placed yet.
        /// Re-reported on every cadence until it does.</summary>
        private readonly HashSet<string> _unackedFields = new HashSet<string>(StringComparer.Ordinal);

        private readonly List<SpatialNode> _near = new List<SpatialNode>();
        private readonly List<SpatialNode> _keep = new List<SpatialNode>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="customPrefabs">Captured station designs, loaded from the data directory and
        /// only read here: a node standing as a scanned design has no SE prefab to spawn from.</param>
        /// <param name="fleetSpawner">Flies the fleet grids the authority orders here; null when it
        /// could not be built, and then this server flies none and says so in its report.</param>
        public AgentHands(FactionTuning tuning, IEnumerable<WorldFeature> features, IPlanetCatalogue planets,
                          CustomPrefabStore customPrefabs, VanillaFleetSpawner fleetSpawner)
        {
            _tuning = tuning;
            if (fleetSpawner != null)
            {
                _fleets = new AgentFleets(fleetSpawner);
                _fleets.ZonePacing = (stationGridId, attackerDps, sinceOwnBurst) =>
                {
                    string nodeId = _stations.NodeIdForStationGrid(stationGridId);
                    SpatialNode n;
                    if (nodeId == null || _graph == null || !_graph.Nodes.TryGetValue(nodeId, out n)) return ZoneFireCall.Push;
                    return ZoneSiegePacing.Decide(n, _tuning, DateTime.UtcNow, attackerDps, sinceOwnBurst);
                };
            }
            _customPrefabs = customPrefabs;
            _fleetSpawner = fleetSpawner;
            _asteroids.FieldPlaced = id => _unackedFields.Add(id);
            if (features != null && planets != null)
            {
                _featureRock.SetFeatures(features, planets);
                _featureRockReady = true;
            }
            _stations.IsPlayerFactionLookup = tag =>
            {
                FactionState f;
                return tag != null && _factionByTag.TryGetValue(tag, out f) && f.IsPlayerFaction;
            };
            _stations.SpawnsSafeZoneLookup = tag =>
            {
                FactionState f;
                return tag == null || !_factionByTag.TryGetValue(tag, out f)
                    || StationSafeZonePolicy.SpawnsSafeZone(f.PersonalityId);
            };
            // The authority's latest word first: the snapshot's breach stamps trail it by up to a minute.
            _stations.ZoneShouldStartEnabled = nodeId =>
            {
                bool? off = _siege.ZoneOff(nodeId);
                if (off.HasValue) return !off.Value;
                SpatialNode n;
                return _graph == null || !_graph.Nodes.TryGetValue(nodeId, out n)
                    || PlayerSiege.ZoneShouldBeEnabled(n, DateTime.UtcNow);
            };
            _stations.CustomStationGrids = prefabId =>
            {
                VRage.Game.MyObjectBuilder_CubeGrid[] grids;
                return _customPrefabs != null && _customPrefabs.TryGetGrids(prefabId, out grids) ? grids : null;
            };
            _siege = new AgentSiege(_stations);
            _economy = new AgentStations(tuning, _stations);
            _economy.NodeLookup = nodeId =>
            {
                SpatialNode n;
                return _graph != null && nodeId != null && _graph.Nodes.TryGetValue(nodeId, out n) ? n : null;
            };
        }

        /// <summary>Whispers a station notice to the players near a node.</summary>
        public Action<SpatialNode, string> TellObservers
        {
            set { _economy.TellObservers = value; }
        }

        /// <summary>The authority's answer to this server's stations.</summary>
        public void ApplyStationReply(NexusStationReply reply)
        {
            _economy.Apply(reply);
            _siege.Apply(reply);
            _transfers.Ack(reply.TransfersThrough);
            _storeEvents.Ack(reply.StoreThrough);
            ApplyShelves(reply.Shelves);
            if (_fleets != null) _fleets.Apply(reply, DateTime.UtcNow);
        }

        private readonly NexusTransferOutbox _transfers = new NexusTransferOutbox();

        /// <summary>A faction-terminal Deposit or Withdraw made here, for the authority's ledger.</summary>
        public void RecordTransfer(string fromTag, string toTag, long amount)
        {
            if (_transfers.Add(fromTag, toTag, amount))
                Log.Info("NEXUS_WALLET_TRANSFER from=" + (fromTag ?? "-") + " to=" + (toTag ?? "-")
                         + " amount=" + amount + " pending=" + _transfers.Count);
        }

        private readonly NexusOutbox<NexusStoreEvent> _storeEvents = new NexusOutbox<NexusStoreEvent>();
        /// <summary>Conduct since the last report (<see cref="NexusConductTally"/>).</summary>
        private readonly NexusConductTally _conduct = new NexusConductTally();

        /// <summary>Adds a member's conduct toward an NPC faction to the next report.</summary>
        public void RecordConduct(long identityId, ulong steamId, string npcTag, int delta, int reason, bool propagate)
        {
            _conduct.Add(identityId, steamId, npcTag, delta, reason, propagate);
        }

        private void FlushConduct()
        {
            foreach (NexusStoreEvent e in _conduct.Drain()) _storeEvents.Add(e);
        }
        private readonly HashSet<string> _shelfMissed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Writes the authority's shelves into the station stores here; null until the
        /// plugin sets it.</summary>
        public StationStoreManager Store;

        /// <summary>A store or Services Terminal event at a Factions station here, for the authority
        /// to book.</summary>
        public void RecordStoreEvent(NexusStoreEvent e)
        {
            if (e == null) return;
            _storeEvents.Add(e);
            if (e.Kind == NexusStoreEventKind.Diplomacy)
            {
                Log.Info("NEXUS_STORE_EVENT kind=Diplomacy player=" + e.Seller + " npc=" + e.Key
                         + " act=" + (e.Buy ? "offer" : "withdraw") + " sender=" + e.Buyer + " pending=" + _storeEvents.Count);
                return;
            }
            Log.Info("NEXUS_STORE_EVENT kind=" + e.Kind + " node=" + (e.Node ?? "-")
                     + (e.Key != null ? " key=" + e.Key + " amount=" + e.Amount + (e.Buy ? " buy" : " sell") : "")
                     + (e.Prefab != null ? " seller=" + e.Seller + " prefab=" + e.Prefab : "")
                     + " credits=" + e.Credits + " buyer=" + e.Buyer + " pending=" + _storeEvents.Count);
        }

        /// <summary>The authority's node stock moves applied here, through this sequence number.</summary>
        public long StockMovesThrough { get; set; }

        /// <summary>The node whose SE station record is <paramref name="stationId"/>, or null.</summary>
        public string NodeIdForStation(long stationId)
        {
            if (stationId == 0 || _graph == null) return null;
            foreach (SpatialNode n in _graph.Nodes.Values)
                if (n.StableStationId == stationId) return n.Id;
            return null;
        }

        /// <summary>The authority's snapshot row for <paramref name="tag"/>, or null when unregistered.</summary>
        public NexusSnapshotFaction FactionRow(string tag)
        {
            NexusSnapshotFaction row;
            return tag != null && _factionRows.TryGetValue(tag, out row) ? row : null;
        }

        /// <summary>The captured designs this server spawns from; null when none could be read.</summary>
        public CustomPrefabStore CustomPrefabs { get { return _customPrefabs; } }

        /// <summary>Swaps in the captured designs, reloaded after the authority registered one.</summary>
        public void ReplaceCustomPrefabs(CustomPrefabStore store)
        {
            if (store == null) return;
            _customPrefabs = store;
            if (_fleetSpawner != null) _fleetSpawner.SetCustomPrefabStore(store);
        }

        /// <summary>The authority's capture generation in its latest snapshot.</summary>
        public int Captures { get; private set; }

        /// <summary>The node whose standing station a grid of this server is part of, or null.</summary>
        public string NodeIdForStationGrid(long gridEntityId)
        {
            return _stations.NodeIdForStationGrid(gridEntityId);
        }

        /// <summary>The authority's faction <paramref name="tag"/> as the snapshot carries it, or null.</summary>
        public FactionState FindFaction(string tag)
        {
            FactionState f;
            return tag != null && _factionByTag.TryGetValue(tag, out f) ? f : null;
        }

        /// <summary>Whether the authority's registry has this faction, which makes its stations'
        /// stores ours rather than vanilla's.</summary>
        public bool IsRegistryFaction(string tag)
        {
            return tag != null && _factionByTag.ContainsKey(tag);
        }

        private void ApplyShelves(List<NexusShelf> shelves)
        {
            if (shelves == null || Store == null || _graph == null) return;
            for (int i = 0; i < shelves.Count; i++)
            {
                NexusShelf shelf = shelves[i];
                SpatialNode n;
                bool written = shelf != null && shelf.Node != null && _graph.Nodes.TryGetValue(shelf.Node, out n)
                               && Store.ApplyShelf(n, shelf);
                if (written) { _shelfMissed.Remove(shelf.Node); continue; }
                if (shelf != null && shelf.Node != null && _shelfMissed.Add(shelf.Node))
                    Log.Warn("NEXUS_SHELF_UNPLACED node=" + shelf.Node + " (no station record here)");
            }
        }

        /// <summary>A yes or no a local player typed, and the node it names if any; true when it
        /// answered the authority's offer.</summary>
        public bool TryAnswer(long identity, bool yes, string nodeId)
        {
            return _siege.TryAnswer(identity, yes, nodeId);
        }

        /// <summary>Releases SE's projectile hooks. The hands are not used after this.</summary>
        public void Close()
        {
            _siege.Unregister();
        }

        /// <summary>The station materialiser, for the per-frame helpers that take one.</summary>
        public StationMaterializer Stations { get { return _stations; } }

        /// <summary>True once a snapshot has seeded the station claims, from when the vanilla cell
        /// generator is kept off Factions stations.</summary>
        public bool ClaimsSeeded { get { return _claimsSeeded; } }

        public bool HasSnapshot { get { return _graph != null; } }
        public int NodeCount { get { return _graph == null ? 0 : _graph.Nodes.Count; } }

        /// <summary>Takes a newer snapshot; an older or repeated one is ignored. Returns true when taken.</summary>
        public bool ApplySnapshot(NexusNodeSnapshot snapshot, DateTime nowUtc)
        {
            if (snapshot == null || snapshot.Sequence <= _sequence) return false;
            SpatialGraph graph = snapshot.ToGraph();
            // A field placed here and not yet acknowledged must not be tried again on the strength
            // of a snapshot that predates the report.
            var acked = new List<string>();
            foreach (string id in _unackedFields)
            {
                SpatialNode node;
                if (!graph.Nodes.TryGetValue(id, out node)) { acked.Add(id); continue; }   // node gone
                if (node.AsteroidFieldSeed != 0) acked.Add(id);
                else node.AsteroidFieldSeed = AsteroidFieldLayout.FieldSeed(id);
            }
            foreach (string id in acked) _unackedFields.Remove(id);

            _graph = graph;
            _index = NodeProximityIndex.Build(graph, Math.Max(50.0, _tuning.SensorNodePassiveRangeKm * Signature.Max));
            _sequence = snapshot.Sequence;
            _snapshotUtc = nowUtc;

            Captures = snapshot.Captures;
            _economy.Munitions = snapshot.Munitions;
            _economy.Loot = snapshot.Loot;
            _factions = snapshot.ToFactions();
            _factionByTag.Clear();
            foreach (FactionState f in _factions) _factionByTag[f.Tag] = f;
            _factionRows.Clear();
            foreach (NexusSnapshotFaction row in snapshot.Factions)
                if (row != null && row.Tag != null) _factionRows[row.Tag] = row;
            // Before the claims are seeded: a record created here is a station the claims must cover.
            StationRecordMirror.Tally records = StationRecordMirror.Reconcile(snapshot, _stations);
            if (records.Any || (records.NoFaction > 0 && _records.NoFaction != records.NoFaction))
                Log.Info("NEXUS_STATION_RECORDS created=" + records.Created + " moved=" + records.Moved
                         + " renamed=" + records.Renamed + " replaced=" + records.Replaced
                         + (records.NoFaction > 0 ? " noFaction=" + records.NoFaction : ""));
            _records = records;
            if (!_claimsSeeded)
            {
                _stations.SeedClaims(_graph, _factions);
                _claimsSeeded = true;
            }
            else StationMaterializer.MarkClaimsDirty();
            return true;
        }

        /// <summary>Per frame, after the agent has local player positions in km.</summary>
        public void Tick(IList<Vec3> playerPositionsKm)
        {
            if (_graph == null) return;
            _near.Clear();
            _keep.Clear();
            bool anyone = playerPositionsKm != null && playerPositionsKm.Count > 0;
            if (anyone)
            {
                _index.QueryWithinAny(_graph, playerPositionsKm, _tuning.ObservationRadiusKm, _near, _seen);
                _index.QueryWithinAny(_graph, playerPositionsKm,
                                      _tuning.ObservationRadiusKm + FactionRuntime.DespawnHysteresisKm, _keep, _seen);
            }
            // Every frame, players or not: departures and the despawn grace run here too.
            _stations.RefreshClaimsIfDue(_graph, _factions, DateTime.UtcNow);
            _stations.UpdateObservation(_graph, _near, _keep, Math.Max(0, _tuning.StationDespawnGraceSeconds));
            _economy.Tick(DateTime.UtcNow);
            _siege.Register();
            _siege.ApplyZones();
            if (_fleets != null) _fleets.Tick(DateTime.UtcNow);
            if (anyone)
            {
                if (_near.Count > 0) _asteroids.UpdateObservation(_graph, _near, _tuning);
                if (_featureRockReady) _featureRock.UpdateObservation(playerPositionsKm, _graph, _tuning);
            }
            _near.Clear();
            _keep.Clear();
        }

        /// <summary>The report owed to the authority. Sent on every cadence, empty or not: it carries
        /// the session, which is how the authority learns this server restarted.</summary>
        public NexusAgentReport BuildReport()
        {
            FlushConduct();
            var report = new NexusAgentReport
            {
                Session = _session,
                AsteroidFields = _unackedFields.Count > 0 ? new List<string>(_unackedFields) : null,
                Stations = _economy.States(),
                Siege = _siege.Flush(),
                Transfers = _transfers.Pending(),
                StoreEvents = _storeEvents.Pending(),
                NoticesThrough = _siege.NoticesThrough,
                StockMovesThrough = StockMovesThrough,
                Observed = _stations.ObservedNodes(),
            };
            // Fleets wait for the first snapshot: until it, nothing here ticks them.
            if (_fleets != null && _graph != null) _fleets.FillReport(report);
            report.Load = LoadReader != null ? LoadReader() : null;
            report.Contacts = ContactsReader != null ? ContactsReader() : null;
            report.OwnedGrids = OwnedGridsReader != null ? OwnedGridsReader() : null;
            report.ScannerGrids = ScannerGridsReader != null ? ScannerGridsReader() : null;
            return report;
        }

        /// <summary>The player-owned grids sampled here since the last report, for the authority's
        /// sensors; null when nothing was sampled since. Set by the plugin, which owns the sampler.</summary>
        public Func<List<NexusPlayerContact>> ContactsReader { get; set; }

        /// <summary>The player-owned grids standing here, for the authority's assets window; null on
        /// the reports that do not carry them. Set by the plugin, which owns the grid list.</summary>
        public Func<List<NexusOwnedGrid>> OwnedGridsReader { get; set; }

        /// <summary>The player-owned grid groups sampled here since the last report, for the
        /// authority's scanner and map; null when nothing was sampled since.</summary>
        public Func<List<NexusScannerGrid>> ScannerGridsReader { get; set; }

        /// <summary>This server's traffic budget, set by the plugin, which owns server perf.</summary>
        public Func<NexusAgentLoad> LoadReader { get; set; }

        /// <summary>The fleet grids flown here; null until fleet hands are on.</summary>
        public AgentFleets Fleets { get { return _fleets; } }

        /// <summary>A snapshot faction's personality id; null when the snapshot names no such faction.</summary>
        public string PersonalityOf(string tag)
        {
            FactionState f;
            return tag != null && _factionByTag.TryGetValue(tag, out f) ? f.PersonalityId : null;
        }

        /// <summary>The authority's latest node graph; null before the first snapshot.</summary>
        public SpatialGraph Graph { get { return _graph; } }

        public string Describe(DateTime nowUtc)
        {
            if (_graph == null) return "hands: no snapshot";
            int cells, objects;
            cells = _featureRock.LiveCells; objects = _featureRock.LiveObjects;
            return "hands: snapshot#" + _sequence + " nodes=" + _graph.Nodes.Count
                 + " age=" + (int)(nowUtc - _snapshotUtc).TotalSeconds + "s"
                 + " stations=" + _stations.MaterializedCount
                 + " stockFed=" + _economy.Count + (_economy.Munitions ? "" : " ammo=vanilla") + (_economy.Loot ? "" : " loot=vanilla")
                 + " unackedFields=" + _unackedFields.Count
                 + " siegeUnacked=" + _siege.Unacked + " zonesOff=" + _siege.ZonesOff
                 + " fleets=" + (_fleets != null ? _fleets.Count + "/" + _fleets.SpawningCount + "spawning" : "off")
                 + " rock=" + (_featureRockReady ? cells + "c/" + objects + "o" : "off");
        }
    }
}
