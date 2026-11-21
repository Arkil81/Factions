using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;   // invariant log formatting (FAC-061)
using System.IO;
using System.Linq;
using System.Text;
using Factions.Core;
using Newtonsoft.Json;
using Sandbox.Game.Entities;    // MyCubeGrid (ship capture)
using Torch.Managers.ChatManager; // ChatManagerServer (fleet-progress whispers)
using VRage.Game;               // MyObjectBuilder_CubeGrid (ship capture)
using VRage.Game.ModAPI;        // IMyPlayer (ship capture)
using VRageMath;

namespace Factions
{
    /// <summary>
    /// The economic simulation. Owns one SE-free <see cref="WorldModel"/> built from the persisted
    /// faction brains (Layer-1 sources from the world scan + the private-ledger treasury), runs think
    /// cycles from the plugin's Update(), and streams decisions to the day-rolling <see cref="DecisionLog"/>.
    ///
    /// Enactment mutates the abstract model only — nothing is deserialized into SE grids — so a
    /// tick does NO SE work (safe on the game thread). Economic and manager work use an active
    /// simulation clock that pauses with the runtime. Planet positions are sampled at Expand time via
    /// <see cref="IPlanetCatalogue"/> to classify nodes (Surface/Orbit/DeepSpace) and snap surface
    /// nodes to the planet sphere.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private const double DefaultSpeed = 0.1;                    // retained for Pause/Resume speed memory
        // Per-faction wall-clock tick scheduling — defaults match FactionTuning doc comments.
        private const double DefaultFactionTickIntervalSeconds = 60.0;
        private const double DefaultGlobalPassIntervalSeconds  = 30.0;

        private const double DefaultArrivalsIntervalSeconds    = 10.0;
        private const double DefaultFlushEveryRealSeconds      =  30.0;
        private const double DefaultPerfWindowSeconds          =  60.0;
        private const double DefaultFleetIntervalSeconds       =   1.0;
        private const double DefaultOpsIntervalSeconds         =  10.0;
        private const double DefaultShipyardIntervalSeconds    =  60.0;
        private const double DefaultTradeIntervalSeconds       =  60.0;
        private const double DefaultFrameSoftBudgetMilliseconds =  0.40;
        private const double DriftDespawnThresholdKm = 500.0;  // despawn a fleet grid if it lags >500 km behind sim position
        private const double StallSpeedKmPerMin      = 0.5;    // grid is "stalled" if speed stays below this
        // A stalled grid still this close to where it began its leg never got under way; it is
        // dematerialised rather than written off. Covers a launch climb that hands back and is
        // braked to rest, and is far short of any leg a hull could crash part-way along.
        private const double StallDepartureRadiusKm  = 1.0;
        private const double PlayerInteractionKm     = 2.0;    // defer despawn/fail while player is this close to grid
        // Spawn triggers at ObservationRadiusKm (sim pos); despawn at this larger radius (physical pos).
        // The hysteresis gap must exceed the spawn clearance offset (~0.6 km) so a freshly spawned
        // grid never immediately re-enters the M=0→spawn path due to the random positional offset.
        internal const double DespawnHysteresisKm     = 1.0;
        private static readonly TimeSpan StallTimeout           = TimeSpan.FromSeconds(15);  // force-fail after stalling this long (no player nearby)
        // Quiet period before a de-materialised fleet may take a grid again, so an admin deleting a
        // grid while standing next to it does not get it re-spawned in their face every tick.
        private static readonly TimeSpan RematerialiseBackoff   = TimeSpan.FromSeconds(60);
        // A grid that has just appeared is exempt from the leave-range exit for this long. The spawn
        // request reads the fleet's sim position and the exit reads the grid's, with an async prefab
        // load between them; a player under way crosses the 1 km hysteresis in that gap, and the
        // grid would be closed in the second it arrived and requested again on the next frame.
        private static readonly TimeSpan SpawnSettleGrace       = TimeSpan.FromSeconds(10);
        // Quiet period after a leave-range despawn before the same fleet may request another grid,
        // so a sim marker still inside the observation bubble cannot re-spawn what was just closed.
        private static readonly TimeSpan LeaveRangeRespawnBackoff = TimeSpan.FromSeconds(5);
        // Quiet period after the launch ladder gives up on flying a grid out of where it is parked.
        // Long enough that the abstract leg has moved on before the fleet may ask for a grid again,
        // so the replacement does not materialise back into the spot that would not release it.
        private static readonly TimeSpan LaunchGiveUpBackoff     = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan StallTimeoutWithPlayer = TimeSpan.FromMinutes(30);  // override the 2km proximity guard after this long — clears crashed debris near bases
        private static readonly TimeSpan SafezoneArrivalGrace   = TimeSpan.FromSeconds(5);   // grace period once a grid enters its target's safezone before resolving/retargeting
        // A materialised fleet grid within this distance of its leg's target has arrived. It was the
        // station safe-zone radius until that grew to 500 m; arrival stays at 150 m so a hauler is
        // not resolved while still on its approach (it comes to rest ApproachStopDistanceM short).
        private const double ArrivalRadiusM = 150.0;
        private const double FleetMessageRadiusKm = 3.0;        // "plausibly close enough to see it" — separate from ObservationRadiusKm
        private const string FleetWhisperAuthor = "Factions";

        private readonly FactionStore _store;
        private readonly DecisionLog _log;
        private readonly FactionBrain _brain = new FactionBrain();
        private IEnumerable<WorldFeature> _worldFeatures;
        private IPlanetCatalogue _worldPlanets;
        private SensorOcclusion _sensorOcclusion;
        private readonly FactionTuning _tuning;    // shared with the plugin so seed/floor knobs agree (income, insolvency)
        private readonly Dictionary<string, Personality> _personalities = Personalities.All();
        private readonly IEconomyGateway _economy; // null = dry-run (no SE credits minted)
        private readonly IPlanetCatalogue _planets; // null = NullPlanetCatalogue (no planet awareness)

        /// <summary>Handed to every <see cref="WorldModel"/> <see cref="Load"/> builds. Set before the
        /// first Load: the world does not exist until then.</summary>
        public INutritionCatalogue Nutrition { get; set; }
        private readonly IOreGeography _ores;      // null = NullOreGeography (no ore anywhere)
        // null = WorldFeaturePlacement.Empty (an unauthored world; every planet rule applies unchanged)
        private readonly WorldFeaturePlacement _featurePlacement;
        internal string CrewPrefab(long gridId) => (_spawner as VanillaFleetSpawner)?.GridPrefabId(gridId);

        private readonly IFleetSpawner _spawner;   // null = no MES, abstract-only; but host always passes NullFleetSpawner for GridExists
        private readonly IItemCatalogue _catalogue;  // null = WorldModel defaults to TuningItemCatalogue
        private readonly IItemRegistry _registry;   // null = WorldModel defaults to NullItemRegistry
        private readonly IRecipeRegistry _recipes;  // null = WorldModel defaults to NullRecipeRegistry
        private readonly IPrefabCatalogue _prefabCatalogue;  // null = no cargo capacity / no role-based picker
        private CustomPrefabStore _customPrefabStore; // player-captured prefabs (`!factions ship capture`); set via SetCustomPrefabStore
        // Whether an id names a station prefab the book-value walk priced -- the one set of refit
        // targets that is not necessarily in the prefab catalogue (the Factorum family is priced and
        // deliberately unlisted). Read by the capture name gate; null until the host wires it.
        private Func<string, bool> _isKnownStationPrefab;
        private string _prefabCatalogueStorageDir; // set via SetPrefabCatalogueStorageDir; lets CaptureShipForPlayer refresh prefab_catalogue.json's on-disk sidecar immediately
        private readonly IFactionTypeCatalogue _factionTypesCatalogue; // null = picker uses "All" fallback
        private readonly IItemVolumeCatalogue _itemVolumes;  // null = use count caps for cargo loading
        private readonly IItemMassCatalogue _itemMasses;     // null = mass-aware TWR checks skip cargo mass
        private readonly IItemPriceCatalogue _itemPrices;    // null = use tuning fallback values
        private readonly StationMaterializer _stationMaterializer = new StationMaterializer();
        private PlayerSiegeManager _playerSiege;
        private StatusPanelPublisher _statusPanel;
        private Action<OccupationNotice> _occupationSink;
        private readonly StationFounder      _stationFounder      = new StationFounder();
        private readonly AsteroidFieldMaterializer _asteroidFieldMaterializer = new AsteroidFieldMaterializer();
        private readonly FeatureRockMaterializer _featureRockMaterializer = new FeatureRockMaterializer();
        private NameSelector _stationNameSelector;

        // Tracks when each materialised grid (keyed by entity ID) first dropped below StallSpeedKmPerMin.
        // Cleared when the grid moves again, is despawned, or is force-failed. Not persisted — resets on restart.
        private readonly Dictionary<long, DateTime> _stalledSince = new Dictionary<long, DateTime>();

        // Where each materialised grid began its current leg, read by the stall verdict to tell a
        // grid that never got under way from one that flew and stopped. Not persisted; pruned by age.
        private readonly LegOrigins _legOrigins = new LegOrigins();
        private static readonly TimeSpan LegOriginPruneAge = TimeSpan.FromMinutes(10);

        // When each materialised grid first entered its current target's safezone radius (see
        // UpdateObservation's Outcome 4). Not persisted — resets on restart, self-healing (just
        // restarts the 5s grace clock).
        private readonly Dictionary<long, DateTime> _safezoneArrivedSince = new Dictionary<long, DateTime>();

        // When each materialised grid began holding station for an engagement dwell (Outcome 4a).
        // Not persisted; a reboot re-derives it from the fleet's own leg on the next observation
        // pass, and the only thing it gates is a one-shot log line.
        private readonly Dictionary<long, DateTime> _engagementHeldSince = new Dictionary<long, DateTime>();

        // FleetId -> the grids that just completed a leg and are waiting for Core's
        // AdvanceToNextStop (next ResolveArrivals tick) to compute the next leg's TargetPosKm, at
        // which point UpdateObservation's M==0 branch reclaims them via IFleetSpawner.Retarget
        // instead of spawning fresh grids. Not persisted: a reboot inside this ~1s window falls back
        // to a fresh spawn for that one leg (same class of risk the pre-existing SpawningId async
        // gap already accepts). SweepOrphanedStashes is what guarantees an entry is not left holding
        // live grids when its fleet leaves PendingFleets instead of flying the next leg.
        private readonly Dictionary<string, StashedSquadron> _pendingRetargetEid = new Dictionary<string, StashedSquadron>(StringComparer.Ordinal);

        // Scratch for SweepOrphanedStashes, reused so the sweep allocates nothing.
        private readonly HashSet<string> _liveFleetIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _orphanedStashes = new List<string>();
        private DateTime _lastStashSweepUtc;
        // The sweep walks every pending fleet, and UpdateObservation runs per frame. A stash is
        // normally consumed within a second by the reclaim, and one that never will be is in no
        // hurry, so the sweep is paced rather than run at frame rate.
        private static readonly TimeSpan StashSweepInterval = TimeSpan.FromSeconds(1);

        /// <summary>A squadron parked between two legs of its itinerary, and the leg it was parked
        /// on. Index 0 of <see cref="Eids"/> is the lead and the rest are its escorts, so a squadron
        /// keeps every hull across a leg boundary.</summary>
        private struct StashedSquadron
        {
            public long[] Eids;
            /// <summary>The completed leg's <see cref="PendingFleetAction.DepartedUtc"/>. Core stamps
            /// a fresh one when it advances the stop, so a value that no longer matches the fleet's
            /// is how the reclaim knows TargetPosKm is the NEXT leg's target and not the one these
            /// hulls are already sitting on.</summary>
            public DateTime LegDepartedUtc;
        }

        // FleetId -> hold on materialising again, armed when a fleet's grid vanished without combat
        // damage (RematerialiseBackoff) or was closed by the leave-range exit
        // (LeaveRangeRespawnBackoff). Entries are dropped once they expire, so this stays as small
        // as the number of fleets held in the last minute. Not persisted: the backoff only exists
        // to avoid a same-tick respawn loop, and a reboot ends that anyway.
        private readonly TimedHold<string> _rematerialiseAfter = new TimedHold<string>(StringComparer.Ordinal);

        // Grid eid -> the settle grace armed when its SPAWNED line was written; see SpawnSettleGrace.
        // Expired entries are pruned each frame, so it holds only grids under ten seconds old.
        private readonly TimedHold<long> _spawnSettle = new TimedHold<long>();
        // Grids that have already written their one FLEET_SPAWN_HELD line. Emptied whenever no grid
        // is inside its settle grace, and on the despawn that ends a held grid's life.
        private readonly HashSet<long> _settleHeldReported = new HashSet<long>();
        // Grid eid -> marked when a grid in its settle grace has no player within the despawn radius,
        // cleared the first time one is. Still marked when the leave-range exit closes it: the grid
        // landed where nobody could keep it (FAC-512).
        private readonly TimedHold<long> _landedUnseen = new TimedHold<long>();
        private static readonly TimeSpan LandedUnseenFor = TimeSpan.FromSeconds(60);
        // FleetId -> the last such landing, so the next spawn is let through only where it would be
        // kept rather than retried at the same place (FAC-512).
        private readonly UnseenLandings _unseenLandings = new UnseenLandings();

        private ChatManagerServer _chat; // set via AttachChat; null = fleet-progress messages are skipped
        private Func<Vec3, double, IEnumerable<ulong>> _playersWithin; // set at the top of each UpdateObservation call

        // Tracks fleet keys (ActorTag_DepartedTicks) whose spawn callback is pending (DoSpawn returned 0 but
        // the prefab callback has not yet fired). Value is the timestamp the spawn was attempted — entries
        // older than SpawnInFlightTimeout are cleared so permanently-failing prefabs do not block forever.
        // Prevents burst materialisation: the per-kind cap also blocks entries whose spawn is in flight,
        // not just entries that already have a live MaterialisedEntityId.
        private readonly Dictionary<string, DateTime> _spawnsInFlight = new Dictionary<string, DateTime>();
        private static readonly TimeSpan SpawnInFlightTimeout = TimeSpan.FromSeconds(10);

        // Fleet keys already reported as having no materialisation wiring for their kind. The report
        // is worth exactly one line per fleet: the fleet stays eligible and re-asks every tick a
        // player is near it, and nothing about the answer changes until the kind gets a spawn path.
        // Cleared on Load() — the same host-local diagnostic lifetime _blockedCombatFleetReports has.
        private readonly HashSet<string> _unsupportedSpawnKinds = new HashSet<string>(StringComparer.Ordinal);

        // Scratch buffer the spawn calls fill with a squadron's escort ids. Reused so polling a
        // spawn every frame allocates nothing; the ids are copied out before they are stored on the
        // fleet. Only ever touched inside UpdateObservation's own call stack.
        private readonly List<long> _escortScratch = new List<long>();

        // Host-local diagnostic throttle for stock-gated combat actions. Entries expire on use and
        // are cleared on Load(), so blocked-report suppression never affects persisted simulation state.
        private readonly OffensiveDispatch.BlockedFleetReports _blockedCombatFleetReports =
            new OffensiveDispatch.BlockedFleetReports();

        // Log sink handed to OffensiveDispatch. The Core helper emits each line unprefixed; this
        // prepends the simulation tick so a dispatch reads like every other DecisionLog line.
        // Cached so a dispatch allocates no delegate.
        private Action<string> _dispatchLogSink;
        private Action<string> DispatchLogSink
        {
            get
            {
                if (_dispatchLogSink == null)
                    _dispatchLogSink = line => _log.Write("t" + _simTick + " " + line);
                return _dispatchLogSink;
            }
        }

        private readonly ProductionManager _productionManager;
        private readonly ShipyardManager _shipyardManager;
        private OperationsManager _opsManager = new OperationsManager();
        private readonly StoreStockManager _storeStockManager;
        private readonly FleetManager _posManager = new FleetManager();
        private TradeManager _tradeManager;
        private PriceOracle _oracle;   // constructed in Load() after world+registries are wired
        private readonly List<string> _managerLog = new List<string>();

        // ---- Per-faction wall-clock tick scheduler ----------------------------------
        // Each faction fires its combined economy+brain tick independently at wall-clock
        // intervals.  One faction per Update() frame; managers fire on independent cadences.
        // Eliminates the 300% SE CPU spike caused by synchronized global economy+brain bursts.
        private DateTime   _globalPassNextUtc = DateTime.MinValue; // BuildSnapshot+Decay+Diplomacy
        private readonly DateTime[] _managerNextUtc = new DateTime[6]; // [0]=prod [1]=ops [2]=yard [3]=fleet [4]=trade [5]=store-stock
        // When the in-flight ProductionManager sweep first became due (null while no sweep is
        // suspended). _managerNextUtc[0] is deliberately not advanced while a sweep is incomplete, so
        // the next tick is naturally deferred until the previous one is actually ready to publish;
        // this just remembers the original due time so DrainManagerPhase can log how late that made
        // the following tick once the sweep finally does complete.
        private DateTime?  _productionSweepDueUtc;
        // Operations counterpart to _productionSweepDueUtc, added when OperationsManager gained its
        // own resumable TryAdvance cycle (Slice 3). Same semantics: null while no Operations sweep is
        // suspended; _managerNextUtc[1] is deliberately not advanced while one is incomplete.
        private DateTime?  _operationsSweepDueUtc;
        // Trade counterpart to _productionSweepDueUtc/_operationsSweepDueUtc, added when TradeManager
        // gained its own resumable TryAdvance cycle (Slice B of the TradeManager resumable-cycle
        // plan). Same semantics: null while no Trade sweep is suspended; _managerNextUtc[4] is
        // deliberately not advanced while one is incomplete.
        private DateTime?  _tradeSweepDueUtc;
        // Shipyard counterpart to the three above, added when ShipyardManager gained its own
        // resumable TryAdvance cycle (FAC-291; Design 1 per-faction-only chunking, mirroring Trade).
        // Same semantics: null while no Shipyard sweep is suspended; _managerNextUtc[2] is
        // deliberately not advanced while one is incomplete.
        private DateTime?  _shipyardSweepDueUtc;
        // GlobalPass counterpart to _productionSweepDueUtc/_operationsSweepDueUtc (Slice 2 of
        // .hermes/plans/2026-08-17_094339-global-pass-econ-cursor.md). Null while no pass is
        // in-flight; _globalPassNextUtc is deliberately not advanced while one is incomplete.
        private DateTime?  _globalPassSweepDueUtc;
        // Flush counterpart to the three sweeps above (frame-budget plan Task 12's last open item).
        // Unlike Production/Operations/GlobalPass, Flush only reads _states/_world and writes to
        // disk/log -- it never mutates simulation state -- so there is no staged-effects/commit-guard
        // apparatus, just a phase enum + per-phase checkpoint (StepFlushOnce/DrainFlush below),
        // mirroring StepGlobalPassOnce/DrainGlobalPass's shape rather than Production/Operations'.
        // Null while no flush sweep is in-flight; _lastFlushUtc is deliberately not advanced while
        // one is incomplete.
        private DateTime?  _flushSweepDueUtc;
        private FlushPhase? _flushPhase;
        private int    _flushFactionIndex;
        private List<IndexEntry> _flushIndex; // accumulated across BrainDocs, written once at its end
        private List<object> _flushProductionEntries;        // accumulated across ProductionThroughput, written once at its end
        private List<object> _flushProductionHistoryEntries;
        private List<object> _flushDemandEntries; // accumulated across ProductionHistory, written once at its end
        private long   _flushElapsedTicks;    // whole in-flight sweep's real work, recorded into _perfFlushTotal once on completion
        // Coarse cadence gate for PruneExpiredUnknTerritory -- a multi-hour-scale grace period needs
        // no per-tick precision, so this sweep only actually runs every UnknPruneIntervalMinutes
        // rather than on every Update(). Plain "next due" field, not part of the resumable-pass
        // machinery above. Ordinary decay/Withdraw trickle a handful of newly-expired nodes per sweep
        // (cheap), but an operator can deliberately backfill AbandonedUtc across the whole pre-existing
        // UNKN backlog at once (`FactionRuntime.BackfillAbandonedUtc`) to fold it into this same
        // mechanism -- MaxUnknRemovalsPerSweep below is what keeps THAT case from trying to delete
        // thousands of ~230-neighbor-average nodes (post-densify) synchronously in one Update() tick;
        // each RemoveNode call is O(that node's own degree) for cleanup, so an uncapped mass sweep is
        // the same class of surprise WorldModel.DensifyTopology's live elapsedMs already taught this
        // session not to assume away.
        private DateTime _unknPruneNextUtc = DateTime.MinValue;
        private const double UnknPruneIntervalMinutes = 3.0;
        private const int MaxUnknRemovalsPerSweep = 500;
        // Null when no pass is in flight. Non-null between DrainGlobalPass calls that span more
        // than one frame -- only the BuildingSnapshot phase can actually take more than one call
        // (Gate A live evidence: it alone is ~93% of the whole pass); every other phase remains a
        // single atomic call, exactly as before, just now checkpointed at a phase boundary so the
        // frame deadline is honored between phases instead of never being checked at all.
        private GlobalPassPhase? _globalPassPhase;
        private double _globalPassCycleIntervalSeconds;
        private long   _globalPassElapsedTicks; // real work only, across the WHOLE in-flight pass (diagnostic; see globalPassElapsedMs)
        private int    _globalPassSnapshotRestarts; // diagnostic: BuildingSnapshot restarts this pass (topology churn)
        // Per-income-phase cost of the pass in flight, keyed by IncomePassPhase name. Aggregated only
        // at pass completion (one INCOME_PASS_PERF line), never per admitted unit -- this is what says
        // which income phase to split next, or that none needs splitting.
        private readonly Dictionary<string, long> _globalPassIncomePhaseTicks = new Dictionary<string, long>();
        private int    _globalPassIncomeSteps;
        // Per-DrainGlobalPass-INVOCATION accumulators, reset at the start of every call -- unlike
        // _globalPassElapsedTicks (whole in-flight pass), these back _perfEcon*'s PerfWindow.Record()
        // calls, so "econ"/econPhases.* are scoped the same way as every other row (prod/ops/etc.:
        // cost of one actor dispatch within one Update() frame), not "cost of the whole multi-minute
        // pass" -- a pass can now span thousands of invocations at live scale, and recording only once
        // per completed pass made "econ" incomparable to every other row in the same panel (caught
        // live 2026-08-17 after node-count reduction made this visible: "econ" still read one huge
        // number twice a minute while every other row read small per-cycle numbers).
        private long _invSnapshotTicks, _invIncomeTicks, _invDiplomacyTicks, _invDecayTicks;
        private long _invKnowledgeTicks;
        /// <summary>Rebuilt only when the graph's node set changes -- nodes do not move, so the
        /// broad-phase this feeds is stable across ownership and capability drift.
        ///
        /// <para>Shared by every proximity consumer, not just the knowledge sweep it was built for:
        /// the station and asteroid observation scans and the player-presence scan all ask the same
        /// question about the same immobile node set, and one index answers all of them.</para></summary>
        private NodeProximityIndex _nodeIndex;
        private int _knowledgeSweepCursor;
        private int _knowledgeTouchedThisPass;
        private long _knowledgeBlockedAtPassStart;
        /// <summary>Reused across fleets within one sweep so a segment query does not allocate per
        /// fleet -- the segment broad phase visits many more cells than a point query did.</summary>
        /// <summary>Set by the host at init. Supplies the player-controlled grids a faction's sensors
        /// can find, measured off live grids because a player ship has no prefab to look up. Called
        /// once per knowledge sweep, not per faction and not per frame.</summary>
        internal Func<List<PlayerContact>> PlayerContactSupplier;

        /// <summary>Debug trace for FAC-137's player detection. Per-process and not persisted, like
        /// every other trace here.</summary>
        public static bool DebugSensing;

        private readonly List<PlayerContact> _playerContacts = new List<PlayerContact>();
        private readonly List<PlayerContact> _sensedScratch = new List<PlayerContact>();
        private readonly List<SensorSource> _sensorScratch = new List<SensorSource>();

        private readonly List<SpatialNode> _knowledgeScratch = new List<SpatialNode>();
        private readonly HashSet<string> _knowledgeScratchSeen = new HashSet<string>(StringComparer.Ordinal);
        private long _invRenameTicks, _invDiploEventsTicks, _invLifecycleTicks;
        private DateTime   _lastFlushUtc = DateTime.MinValue;
        // A per-frame flush step that hit a file another process held (FAC-371): how many since the
        // last warning, and when the next warning may be written.
        private int        _flushDeferredSinceWarn;
        private DateTime   _nextFlushDeferWarnUtc = DateTime.MinValue;
        private readonly FleetPrefabConfig _prefabConfig;    // null = free-build (delegate not wired)
        private readonly ILiveEventWriter _eventWriter;   // null = no live events emitted

        // ---- Per-frame performance windows (reset every GetPerfWindowSeconds()) ----
        // ---- shared frame budget ----------------------------------------------------------
        // Actor ids are registration order and are fixed; RunRuntimeActor switches on them.
        private const int ActorProd = 0, ActorOps = 1, ActorYard = 2, ActorFleet = 3, ActorTrade = 4,
                          ActorStock = 5, ActorGlobalPass = 6, ActorBrain = 7, ActorFlush = 8,
                          ActorArrivals = 9;
        private const int RuntimeActorCount = 10;
        private FrameScheduler _scheduler;
        private DateTime _starvationNextLogUtc = DateTime.MinValue;
        private const double StarvationLogIntervalSeconds = 30.0;

        private PerfWindow _perfBrain;
        // Sub-spans of _perfBrain, split out so the brain-cadence decision and any future Think/Enact
        // cursor have a measured ratio to work from -- _perfBrain alone merges all three and cannot
        // say which half a 12-21 ms faction tick actually spends its time in.
        private PerfWindow _perfBrainThink;
        private PerfWindow _perfBrainEnact;
        private PerfWindow _perfBrainLog;
        // Think's internal stages, so a tick that costs 15x the average can be attributed to one of
        // them rather than to Think as a whole.
        private PerfWindow _perfThinkSense;
        private PerfWindow _perfThinkTradePriority;
        private PerfWindow _perfThinkReinforcePriority;
        private PerfWindow _perfThinkThreatBonus;
        private PerfWindow _perfThinkGenerate;
        private PerfWindow _perfThinkScore;
        private PerfWindow _perfThinkSelect;
        private PerfWindow _perfProduction;
        private PerfWindow _perfOps;
        private PerfWindow _perfShipyard;
        private PerfWindow _perfFleet;
        private PerfWindow _perfTrade;
        private PerfWindow _perfStoreStock;
        private PerfWindow _perfEconomy;
        // Phase breakdown of _perfEconomy/DrainGlobalPass -- Slice 1 of
        // .hermes/plans/2026-08-17_094339-global-pass-econ-cursor.md. Measurement only, no behavior
        // change. Reuses this same PerfWindow accumulator every other manager bucket already uses,
        // rather than EconomyTickProfiler's Slot: that type profiles vanilla SE's unrelated 600 s
        // MySessionComponentEconomy.UpdateStations tick and isn't wired into /api/perf, so it isn't
        // actually the machinery already covering this bucket despite the name collision.
        private PerfWindow _perfEconSnapshot;
        private PerfWindow _perfEconIncome;
        private PerfWindow _perfEconDiplomacy;
        private PerfWindow _perfEconDecay;
        private PerfWindow _perfEconRename;
        private PerfWindow _perfEconKnowledge;
        private PerfWindow _perfEconArrivals;
        private PerfWindow _perfEconDiploEvents;
        private PerfWindow _perfEconLifecycle;
        private PerfWindow _perfObs;
        private PerfWindow _perfFlight;
        private PerfWindow _perfStObs;
        private PerfWindow _perfAstObs;
        // Transient by design: a restart re-tries declined nodes once, which is correct rather than
        // wasteful -- the authored world features they depend on may have changed since.
        private readonly HashSet<string> _depositBackfillAttempted = new HashSet<string>(StringComparer.Ordinal);
        private bool _depositBackfillDone;
        private int _depositBackfillSampled, _depositBackfillBarren, _depositBackfillDeclined;
        private List<string> _resurveyKeys;
        private int _resurveyCursor;
        private bool _resurveyDone;
        private int _resurveyStale, _resurveyReplaced;
        private readonly Dictionary<string, int> _resurveyDeclines = new Dictionary<string, int>(StringComparer.Ordinal);
        private List<string> _surveyCursorKeys;
        private int _surveyCursor;
        private long _surveyProbesSpent;
        /// <summary>Nodes the ore seam has refused, and how many times in a row. A node reaching
        /// <see cref="FactionTuning.SurveyDeclinesBeforeParking"/> is parked: it keeps its deposit and
        /// its points, and simply stops being offered a visit it cannot use. Cleared on restart, and
        /// on the rebuild below, so a node is always re-tried rather than parked for good.</summary>
        private readonly Dictionary<string, int> _surveyDeclines = new Dictionary<string, int>();
        private int _surveyParked;
        private int _surveyCandidates;
        private int _surveyPasses;
        /// <summary>Set the moment the cursor wraps past the end of the key list, and consumed by the
        /// next call's rebuild.
        /// <para>The pass boundary cannot be read off the cursor being zero. A call returns as soon as
        /// it surveys one node, so the cursor comes to rest one past whichever candidate that was —
        /// zero only when that candidate happened to be the very last key, roughly one pass in
        /// <c>candidates</c>. Inferring the boundary that way left the rebuild and its telemetry
        /// effectively unreachable.</para></summary>
        private bool _surveyPassComplete;

        /// <summary>SURVEY_PASS trace: one line per completed rotation, reporting the derived budget
        /// and how much of the rotation is going nowhere. Off by default like every other trace here.
        /// <para>The survey had no telemetry at all before this: <c>_surveyProbesSpent</c> was
        /// written and never read, so a rotation spending a quarter of its budget on ground no reader
        /// covers looked exactly like one that was working.</para></summary>
        public static bool DebugSurvey;
        // Persistence writes -- unlike the manager buckets above, Flush()/WriteSnapshot() are called
        // from many sites (registry mutations, Reload(), offline capture, not just the scheduled
        // FlushEveryRealSeconds path), so these are recorded inside the methods themselves rather
        // than at each call site.
        private PerfWindow _perfFlushTotal;
        private PerfWindow _perfFlushBrains;
        private PerfWindow _perfFlushProduction;
        private PerfWindow _perfFlushSnapshot;
        // Top-level FactionsPlugin.Update envelope. The frame window excludes base.Update(), which
        // is still recorded separately as an external Torch/SE correlation signal.
        private readonly FrameTimingWindow _perfFrame = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfInstrumentation = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfBaseUpdate = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfPlayerCache = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfRuntimeDispatch = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfFleetObservation = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfStationObservation = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfAsteroidObservation = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfFeatureRock = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfPrice = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfPlanet = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfBridge = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfPresence = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfGps = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly FrameTimingWindow _perfStoreRefresh = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        // Called from outside FactionsPlugin.Update() entirely — GenerateContract is invoked directly
        // by vanilla's MySessionComponentContractSystem.CreateContractsForStation contract-board refill
        // loop, and store transactions land via the StoreTransactionPatches Harmony postfixes — neither
        // has an existing call-site measurement, unlike every other bucket above.
        private readonly FrameTimingWindow _perfContractGen = new FrameTimingWindow(Stopwatch.Frequency, 1.0, 4096);
        private readonly FrameTimingWindow _perfStoreTxn = new FrameTimingWindow(Stopwatch.Frequency, 0.5, 4096);
        private readonly ProductionTelemetryWindow _perfProductionTelemetry = new ProductionTelemetryWindow();
        private readonly OperationsTelemetryWindow _perfOperationsTelemetry = new OperationsTelemetryWindow();
        private readonly TradeTelemetryWindow _perfTradeTelemetry = new TradeTelemetryWindow();
        private long _deadlineNotDueFrames;
        private long _deadlineDueUnsafeCount;
        private long _deadlineExpiredBeforeUnsafeCount;
        private DateTime _perfWindowStartUtc;
        private DateTime _lastPerfFlushUtc = DateTime.MinValue;
        /// <summary>Every <see cref="FrameTimingWindow"/> above, so the sample ring that backs their
        /// percentiles can be sized from the configured window length in one place rather than at
        /// each declaration.</summary>
        private FrameTimingWindow[] _allFrameWindows;

        private WorldModel _world;
        private List<FactionState> _states = new List<FactionState>();
        private long _simTick;
        private long _arrivalsRunCount;
        private DateTime _lastRealUtc;
        private DateTime _activeUtc;
        private double _resumeSpeed = DefaultSpeed;

        public double Speed { get; private set; }
        public long SimTick { get { return _simTick; } }
        public int FactionCount { get { return _states.Count; } }
        /// <summary>The live roster, read-only. For host managers that walk every faction once per
        /// economy quantum (the member-reputation projection); not for per-frame use.</summary>
        public IList<FactionState> States { get { return _states.AsReadOnly(); } }

        /// <summary>Runs at the end of each diplomacy phase, after the SE relation table has been
        /// projected — the economy-quantum seat for host work that reads the standings the phase just
        /// settled. Game thread; a throw is logged and swallowed.</summary>
        public Action AfterDiplomacyPass { get; set; }
        public DateTime ActiveUtc { get { return _activeUtc; } }

        // Expose internals needed by host-side managers (presence, store, price updater).
        public WorldModel          World     { get { return _world; } }
        public StationMaterializer StationsMaterializer { get { return _stationMaterializer; } }
        public IPriceOracle        Oracle    { get { return _oracle; } }
        /// <summary>The concrete oracle, for the one caller that needs the target decomposition
        /// rather than the scarcity number — <c>factions target</c>. Not on
        /// <see cref="IPriceOracle"/>: the breakdown is an explanation of one implementation, and a
        /// null oracle has nothing to explain.</summary>
        public PriceOracle         TargetOracle { get { return _oracle; } }
        public IItemRegistry       Registry  { get { return _registry; } }
        public IItemPriceCatalogue ItemPrices { get { return _itemPrices; } }
        public FactionTuning       Tuning    { get { return _tuning; } }

        /// <summary>What one node actually refined and built over the production history window, most
        /// recent first — the station window's History tab. The entries are live and keep growing, so
        /// the caller serializes them on the same frame. Game thread.</summary>
        public List<ProductionManager.ProductionHistoryEntry> ProductionHistoryFor(string factionTag, string nodeId)
        {
            return _productionManager.SnapshotNodeProductionHistory(factionTag, nodeId);
        }

        /// <summary>Fired from <see cref="UpdateObservation"/> when any fleet's materialised grid is
        /// confirmed destroyed — Guard-via-PvEBounty's success signal. Argument is the destroyed
        /// fleet's <see cref="PendingFleetAction.FleetId"/> (not a contract ID — the torch-host
        /// handler owns its own accepted-bounty-by-FleetId tracking and must silently no-op for the
        /// common case where this fleet never had a bounty). Set by <c>FactionsPlugin</c> at session
        /// load.</summary>
        public Action<string> OnPvEBountyTargetDestroyed { get; set; }
        /// <summary>Fired from <see cref="PendingFleetResolver.ResolveArrivals"/>'s <c>onFleetRemoved</c>
        /// callback for every fleet removed from <c>PendingFleets</c>, win or lose — Guard-via-PvEBounty's
        /// failure signal: if the removed fleet had an accepted bounty and it wasn't the destroy-path
        /// above that just handled it, the torch-host handler fails that contract. Argument is the
        /// removed fleet's FleetId; a dictionary-miss (the common case for a fleet with no bounty) must
        /// be a silent no-op.</summary>
        public Action<string> OnFleetResolved { get; set; }

        /// <summary>Capability → recipes, as the production manager was built with. Read by
        /// <see cref="StockItemSourcesFor"/>; null in a host that supplies none.</summary>
        private readonly Dictionary<NodeCapabilityKind, List<ConversionRecipe>> _capRecipes;

        public FactionRuntime(FactionStore store, DecisionLog log, FactionTuning tuning = null,
                              IEconomyGateway economy = null, IFleetSpawner spawner = null,
                              IItemCatalogue catalogue = null, IItemRegistry registry = null,
                              IRecipeRegistry recipes = null, FleetPrefabConfig prefabConfig = null,
                              Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes = null,
                              IPrefabCatalogue prefabCatalogue = null,
                              IItemVolumeCatalogue itemVolumes = null,
                              IItemMassCatalogue itemMasses = null,
                              ILiveEventWriter eventWriter = null,
                              IItemPriceCatalogue itemPrices = null,
                              IPlanetCatalogue planets = null,
                              IOreGeography ores = null,
                              WorldFeaturePlacement featurePlacement = null,
                              IFactionTypeCatalogue factionTypes = null,
                              NameSelector stationNameSelector = null)
        {
            _store = store;
            _log = log;
            StationMaterializer.ClaimLog = log != null ? (Action<string>)log.Write : null;
            _tuning = tuning ?? new FactionTuning();
            _economy = economy;
            _planets = planets;
            _ores = ores;
            _featurePlacement = featurePlacement;
            _stationNameSelector = stationNameSelector;
            _stationFounder.Configure(null, null, null, _planets, stationNameSelector);
            _spawner = spawner;
            _catalogue = catalogue;
            _registry = registry;
            _recipes = recipes;
            _prefabConfig = prefabConfig;
            _prefabCatalogue = prefabCatalogue;
            _itemVolumes = itemVolumes;
            _itemMasses = itemMasses;
            _itemPrices = itemPrices;
            _eventWriter = eventWriter;
            _factionTypesCatalogue = factionTypes;
            _capRecipes = capRecipes;
            _productionManager = new ProductionManager(capRecipes, factionTypes, itemVolumes: _itemVolumes);
            _storeStockManager = new StoreStockManager(factionTypes);
            _shipyardManager = new ShipyardManager(_itemVolumes);
            Speed = DefaultSpeed;   // auto-run slow
        }

        /// <summary>Wire the captured-prefab store (called by <c>FactionsPlugin</c>, same time as
        /// <see cref="VanillaFleetSpawner.SetCustomPrefabStore"/>) — <see cref="CaptureShipForPlayer"/>
        /// writes new captures here.</summary>
        public void SetCustomPrefabStore(CustomPrefabStore store)
        {
            _customPrefabStore = store;
        }

        /// <summary>The captured-prefab store, for a caller that needs a prefab's raw grids rather
        /// than its catalogue metadata — the Services Terminal's withdrawal spawns one.</summary>
        public CustomPrefabStore CapturedPrefabs { get { return _customPrefabStore; } }

        /// <summary>The live catalogue as its concrete type, or null when the host is running
        /// without one. <see cref="Factions.Core.IPrefabCatalogue"/> deliberately knows nothing
        /// about fingerprints or ownership scope, and both are host concerns.</summary>
        public SePrefabCatalogue Catalogue { get { return _prefabCatalogue as SePrefabCatalogue; } }

        /// <summary>Directory containing <c>prefab_catalogue.json</c> (the read-only diagnostic
        /// sidecar <c>PrefabCatalogueScanner</c> writes at session load, also served verbatim by the
        /// Dashboard API's <c>/api/prefabs</c> endpoint). Set once at plugin startup so
        /// <see cref="CaptureShipForPlayer"/> can refresh that file immediately after a live capture
        /// — without this, a mid-session capture is fully functional in the live in-memory catalogue
        /// (real <c>CargoLiters</c>/<c>DrillYieldScore</c>, dispatchable, etc.) but invisible to the
        /// Dashboard until the next full session restart re-scans everything from scratch, which
        /// surfaced live 2026-08-18 as FleetPanel showing "Prefab '...' not in catalogue" for an
        /// actively-dispatched, perfectly real captured ship.</summary>
        public void SetPrefabCatalogueStorageDir(string dir)
        {
            _prefabCatalogueStorageDir = dir;
        }

        /// <summary>Wire the chat manager (mirrors <see cref="ClaimOfferManager.AttachChat"/>'s
        /// pattern) so <see cref="UpdateObservation"/>
        /// can whisper immersive fleet-progress messages to players nearby a spawned grid. Null
        /// (never attached, or session unloaded) silently skips messaging — no exception, no
        /// fallback broadcast.</summary>
        public void AttachChat(ChatManagerServer chat)
        {
            _chat = chat;
        }

        /// <summary>(Re)build the world from disk: each brain's sources + private ledger. Resumes a reboot
        /// from the last flushed state. Caller should <see cref="Flush"/> first if in-memory state matters.</summary>
        public void Load()
        {
            _blockedCombatFleetReports.Clear();
            _unsupportedSpawnKinds.Clear();

            // A reload is a reconciliation with disk, not a fresh session: both the world and the
            // faction list below are rebuilt from the brain files, which are only as fresh as the
            // phased flush last reached. Event rings are the one piece of live state with no other
            // way back -- an unflushed raid is simply gone -- so they are carried across by hand.
            // See FactionEventLog.Merge.
            Dictionary<string, List<FactionEvent>> carriedRings = null;
            if (_states != null)
            {
                foreach (FactionState prior in _states)
                {
                    if (prior == null || string.IsNullOrEmpty(prior.Tag) || prior.RecentEvents.Count == 0) continue;
                    if (carriedRings == null)
                        carriedRings = new Dictionary<string, List<FactionEvent>>(StringComparer.OrdinalIgnoreCase);
                    carriedRings[prior.Tag] = new List<FactionEvent>(prior.RecentEvents);
                }
            }
            // Recorded this tick but not yet drained: the old world is about to be thrown away.
            List<FactionEvent> carriedPending = null;
            if (_world != null && _world.PendingEvents.Count > 0)
                carriedPending = new List<FactionEvent>(_world.PendingEvents);
            // Wire prefab list picker: catalogue-driven (faction-type + role) when available,
            // falling back to free-build mode (no BOM, any prefab) otherwise.
            if (_prefabCatalogue != null)
            {
                IPrefabCatalogue capturedCatalogue   = _prefabCatalogue;
                IFactionTypeCatalogue capturedTypes  = _factionTypesCatalogue;
                _shipyardManager.PrefabListPicker = (role, factionTag) =>
                {
                    IReadOnlyList<string> candidates = capturedCatalogue.GetNamesForRole(
                        role, PrefabHintFor(capturedTypes, factionTag));
                    return (IList<string>)PrefabOwnershipFilter.Apply(candidates, capturedCatalogue, factionTag);
                };
            }
            else if (_prefabConfig != null)
            {
                // Legacy fallback: fleet_prefabs.json personality-based picker.
                FleetPrefabConfig capturedConfig = _prefabConfig;
                _shipyardManager.PrefabListPicker = (role, factionTag) =>
                    capturedConfig.ListOptions(role, "default");
            }

            _world = new WorldModel(new SeededRandom(20260606)) { Tuning = _tuning };
            if (_registry != null) _world.Registry  = _registry;
            if (_recipes  != null) _world.Recipes   = _recipes;
            if (_catalogue != null) _world.Catalogue = _catalogue;
            if (_itemPrices != null) _world.PriceCatalogue = _itemPrices;
            if (_planets  != null) _world.Planets   = _planets;
            if (Nutrition != null) _world.Nutrition = Nutrition;
            if (_ores     != null) _world.Ores      = _ores;
            // Wired before Load()'s ReclassifyNodes runs -- that pass is what repositions nodes, and
            // without the authored world in hand it would place ring nodes by the planet heuristic.
            if (_featurePlacement != null) _world.Features = _featurePlacement;
            if (_prefabCatalogue != null) _world.PrefabCatalogue = _prefabCatalogue;
            var cappedCatalogue = _prefabCatalogue as SePrefabCatalogue;
            if (cappedCatalogue != null) _world.PrefabOverCeiling = cappedCatalogue.IsOverPerformanceCeiling;
            if (cappedCatalogue != null) _world.PrefabBanned = cappedCatalogue.IsBanned;
            LoadPrefabBans();
            _world.StationWeaponReading = StationWeaponReading;
            // The lift gate's kill switch lives on the catalogue, which is what every gate asks for a
            // hull's frame (see SePrefabCatalogue.LiftFromHullFrame).
            var frames = _prefabCatalogue as SePrefabCatalogue;
            if (frames != null && _tuning != null) frames.LiftFromHullFrame = _tuning.LiftFromHullFrame;
            // Wired before the SetTreasury loop below, so loading every brain leaves each tag dirty
            // and the first frame after Load reconciles the SE wallet to the sim ledger (FAC-181).
            if (_economy != null)
                _world.TreasuryMirror = new LedgerMirror(_economy, FindFaction, msg => _log.Write(msg));
            // Construct oracle and TradeManager after registries are wired so RecomputeAll
            // uses the real SeItemRegistry (not NullItemRegistry) for RareOre resolution.
            _oracle = new PriceOracle(_world, _tuning);
            _world.PriceOracle = _oracle;
            _productionManager.SetPriceOracle(_oracle);
            _tradeManager = new TradeManager(_oracle, _itemVolumes, _itemMasses);
            _opsManager = new OperationsManager(_itemVolumes, _itemMasses);
            // The NPC extraction planner turns a hub's fuel burn into the ice it implies through the
            // FuelDepot recipes, which the production manager holds.
            _opsManager.IcePerGasUnitSource = _productionManager.DepotIcePerGasUnit;
            // The same masses the cargo trim reads, so the fuel meter prices a loaded hull on the
            // mass it was actually loaded to rather than on its dry hull.
            _world.ItemMasses = _itemMasses;
            // The same volumes ProductionManager weights a DemandHistory credit by, so PriceOracle
            // divides back out exactly what was multiplied in.
            _world.ItemVolumes = _itemVolumes;
            _activeUtc = DateTime.UtcNow;
            _world.UtcNow = _activeUtc;
            _store.LoadCentralBankState(_world);
            _states = new List<FactionState>();
            var loadedDocs = new List<BrainDoc>();
            foreach (IndexEntry e in _store.LoadIndex())
            {
                BrainDoc doc = _store.LoadBrain(e.Tag);
                if (doc == null) continue;
                var migrations = new List<string>();
                FactionState st = FactionStore.ApplyBrainDoc(doc, _world.Graph, _tuning.CampaignCooldown, migrations);  // re-adds sources + restores dormancy
                // A migration rewrites persisted player intent (today: a retired Build standing order
                // becoming a doctrine line). Loud once, on the load that did it.
                foreach (string migration in migrations) _log.Write(migration);
                int bankKept = FactionLifecycle.ExpandLegacyBankSellOptOut(st, BankBuyableKeys());
                if (bankKept > 0)
                    _log.Write("BANK_KEEP_MIGRATED " + st.Tag + " kept=" + bankKept);
                // Clamp to the floor (a faction persisted in debt resumes in debt, NOT bailed out to seed capital —
                // that was the old `>0 ? : Seed` bug). Only a never-funded faction (penniless, landless, awake) gets
                // its one-time seed here, covering brains an older init wrote without starting capital; fresh inits
                // now stamp the seed at registration, and a collapsed faction is reborn via FactionLifecycle, not here.
                long treasury = Math.Max(doc.Treasury, _tuning.InsolvencyFloor);
                bool neverFunded = doc.Treasury == 0 && (doc.Sources == null || doc.Sources.Count == 0)
                                   && st.DormantUntilUtc == DateTime.MinValue;
                _world.SetTreasury(st.Tag, neverFunded ? _tuning.SeedTreasury : treasury);
                _world.SetDebt(st.Tag, doc.Debt ?? 0);
                // Migrate faction-level inventory from old brain files (per-node is now in SourceDoc).
                // Keys may be ItemKind names (old) or SubTypeId strings (new); use DeserializeCargo to
                // normalise both to SubTypeId, then add SubTypeId directly to first owned node.
                if (doc.Inventory != null)
                {
                    var migrated = FactionStore.DeserializeCargo(doc.Inventory);
                    if (migrated != null)
                        foreach (var kv in migrated)
                            if (kv.Value > 0)
                                _world.AddSubTypeInventory(st.Tag, kv.Key, kv.Value);
                }
                // A node's kind is not migrated here any more: it is derived from the node's deposit
                // and its site by ReclassifyNodes, which runs after every brain is loaded. See FAC-102.
                BackfillDisplayNames(st);
                _states.Add(st);
                loadedDocs.Add(doc);
            }
            RestoreCarriedEvents(carriedRings, carriedPending);

            int topologyLinksRestored = FactionStore.RestoreTopology(loadedDocs, _world.Graph);
            if (topologyLinksRestored > 0)
                _log.Write("runtime restored " + topologyLinksRestored + " persisted territory link(s).");
            // Zone/PlanetId reset to defaults by ApplyBrainDoc above -- re-derive from live gravity
            // every boot. Also re-derives each node's worth, which is why it must run AFTER the
            // registry and price catalogue are wired above: with no prices a surveyed deposit reads
            // as its ceiling, and this pass writes what it reads.
            ReclassifyNodes(true);
            _simTick = 0;
            _lastRealUtc = DateTime.UtcNow;
            ReconcileAndResolveOnLoad();   // resolve overdue fleets (server was offline during transit)
            // One projection pass after load, so a save carrying the founding recipe's static pins
            // converges to what the brains actually hold before the first brain tick reads either
            // (FAC-274). The count is the readout: a boot that changes nothing is the steady state.
            ProjectRelationsToSe("load");
            _oracle.RecomputeAll();        // pre-warm scarcity reference cache before first manager tick
            // ReservedInventory came back from the brain files; InvestmentRegister did not (in-memory,
            // TTL-based). Give the managers a window to re-post before an unbacked reservation counts
            // as stale, or every restart visibly dumps staged install components back into inventory.
            _productionManager.BeginReleaseGrace(_world.UtcNow, _tuning.InstallReservationReleaseGraceSeconds);
            if (_tuning.InstallReservationReleaseGraceSeconds > 0)
                _log.Write("runtime install-reservation release held for "
                        + _tuning.InstallReservationReleaseGraceSeconds.ToString("0") + "s after load.");
            // Reset scheduler — all timers at MinValue means every slot is immediately due.
            // The drain methods process one item per Update() frame, so on first boot all 13
            // factions drain over 13 consecutive frames rather than in one burst.
            _globalPassNextUtc = DateTime.MinValue;
            for (int i = 0; i < 6; i++) _managerNextUtc[i] = DateTime.MinValue;
            _productionSweepDueUtc = null;
            _operationsSweepDueUtc = null;
            _tradeSweepDueUtc = null;
            _shipyardSweepDueUtc = null;
            // _states was just rebuilt above -- a suspended flush sweep's _flushFactionIndex/_flushIndex
            // reference the OLD list and must not resume against the new one (index could now be out of
            // range, or silently write the wrong faction's brain under the old index). Every Load() call
            // site that can race an in-flight sweep already flushes first (registry-mutation pattern,
            // and Flush() itself always drains any in-progress sweep before running fresh) -- this reset
            // is a defensive backstop for the remaining call sites, not the primary correctness guarantee.
            _flushPhase = null;
            _flushSweepDueUtc = null;
            // Same reasoning for the global pass: Load() built a brand-new WorldModel above, so an
            // in-flight pass's Core-side cursor (influence snapshot build, income pass) is gone while
            // this host-side phase field would still say "resume where you were" -- and resuming
            // BuildingSnapshot against a fresh InfluenceField dereferences a null build order.
            _globalPassPhase = null;
            _globalPassSweepDueUtc = null;
            _lastFlushUtc = DateTime.UtcNow;
            foreach (FactionState st in _states) st.NextTickRealUtc = DateTime.MinValue;
            ResetPerfWindows();
            _perfWindowStartUtc = DateTime.UtcNow;
            _lastPerfFlushUtc   = DateTime.UtcNow;
            _log.Write("runtime loaded: " + _states.Count + " faction(s); speed=" + Speed.ToString("0.###")
                       + " tick/s; " + (Speed > 0 ? "running" : "paused"));
        }

        /// <summary>Persist in-memory state, then rebuild from disk (picks up external changes from init).</summary>
        public void Reload() { Flush(); Load(); }

        /// <summary>
        /// Backfills station prefab identity and cached book value onto loaded station nodes, then
        /// flushes the enriched docs so the collateral base survives the next restart without a rescan.
        /// </summary>
        /// <summary>
        /// Wires the book-value lookup, weapon-band lookup, and prefab pool into
        /// <see cref="StationFounder"/> so that all node fields (StableStationId, StationPrefabId,
        /// StationBookValue, StationWeapons) are set atomically when a faction founds a new station.
        /// </summary>
        public void ConfigureStationFounder(Func<string, long> bookValueLookup,
            Func<string, System.Collections.Generic.List<WeaponBand>> weaponBandsLookup,
            Func<bool, string, System.Collections.Generic.IList<string>> prefabPool,
            NameSelector stationNameSelector = null,
            Func<string, bool> isKnownStationPrefab = null,
            Func<string, bool> isFactorumStation = null)
        {
            _stationNameSelector = stationNameSelector;
            _isKnownStationPrefab = isKnownStationPrefab;
            _stationFounder.Configure(bookValueLookup, weaponBandsLookup, prefabPool, _planets,
                stationNameSelector, IsRegisteredPlayerFaction, isFactorumStation);
            _stationMaterializer.IsPlayerFactionLookup = IsRegisteredPlayerFaction;
            _stationMaterializer.SpawnsSafeZoneLookup = SpawnsSafeZoneFor;
            ConfigureStationMunitions();
            ConfigureStationLoot();
            // A node standing as a player-scanned design has no SE prefab definition to spawn from;
            // this is the only route from its id to the grids it was captured as.
            _stationMaterializer.CustomStationGrids = prefabId =>
            {
                MyObjectBuilder_CubeGrid[] grids;
                return _customPrefabStore != null && _customPrefabStore.TryGetGrids(prefabId, out grids)
                    ? grids : null;
            };
            SePrefabCatalogue refitCatalogue = _prefabCatalogue as SePrefabCatalogue;
            _stationFounder.ConfigureRefit(
                prefabId => refitCatalogue != null ? refitCatalogue.Cost(prefabId) : 0L,
                prefabId => refitCatalogue != null
                    ? new List<WeaponBand>(refitCatalogue.WeaponBands(prefabId)) : null,
                IsCapturedDesign);
            // A station that respawns mid-siege, or inside an NPC raid's knockdown window, must not
            // come back protected: the physical zone is re-created from scratch every materialisation.
            _stationMaterializer.ZoneShouldStartEnabled = nodeId =>
            {
                SpatialNode n;
                return _world == null || !_world.Graph.Nodes.TryGetValue(nodeId, out n)
                    || PlayerSiege.ZoneShouldBeEnabled(n, _world.UtcNow);
            };
            if (_playerSiege == null && _world != null)
            {
                _playerSiege = new PlayerSiegeManager(_world, _tuning, _stationMaterializer);
                _playerSiege.CreditRaidZoneHit = CreditRaidZoneHit;
                _playerSiege.RaidDwellingAt = RaidDwellingAt;
                _playerSiege.Panel = _statusPanel;
                _playerSiege.Occupied = _occupationSink;
                _playerSiege.RemoteNotice = _remoteSiegeNotice;
                _playerSiege.RemotePlayers = _remoteSiegePlayers;
                _playerSiege.ObservedOnAgent = IsObservedOnAgent;
                _playerSiege.Register();
                var vanillaSpawner = _spawner as VanillaFleetSpawner;
                if (vanillaSpawner != null)
                {
                    vanillaSpawner.StationDamageSink = RecordStationBlockDamage;
                    vanillaSpawner.StationAttackWatched = IsStationGridWatched;
                    vanillaSpawner.StationAttackerSink = RecordStationAttacker;
                    vanillaSpawner.EnsureDamageHandler();
                }
            }
            RebuildStationNameUsage();
            RetryFallbackStationNames();
            ApplyPersistedStationDisplayNames();
            MigrateStationPrefabs(false);   // dry run only -- logs mismatches every boot; apply stays a manual step

            // Last step of the station-wiring block, and that is deliberate: a pending rebuild has to
            // be standing before the first materialisation, and this method is the last thing the
            // session-load handler runs for stations -- still inside MySession.AfterLoading, so no
            // UpdateStationObservation tick has happened yet and no grid is up to be dropped. It also
            // has to run AFTER ConfigureRefit above, or a swap would write a book value the catalogue
            // was never asked for.
            ApplyPendingStationRefits(force: true, reason: "boot");

            // After the pending refits stand, so a node that has just moved off an old version no
            // longer holds it.
            CollectSupersededCaptures();
        }

        /// <summary>
        /// Applies a persisted tag-free catalogue name to its live MyFactionStation and materialized
        /// grid. The offline migration owns selection; this host pass only reconciles live entities.
        /// </summary>
        private void ApplyPersistedStationDisplayNames()
        {
            if (_world == null) return;
            int applied = 0;
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (_stationFounder.ApplyPersistedDisplayName(node)) applied++;
            }
            if (applied > 0)
            {
                FlushOrDefer("station display-name reconcile");
                _log.Write("STATION_DISPLAY_RECONCILED " + applied + " node(s)");
            }
        }

        /// <summary>
        /// Points the name selector at the live world and derives its in-use name set from it.
        /// Collision protection only, not a rename migration: node/station/grid names are never
        /// mutated here. Deriving the set is what returns a conquered, abandoned or destroyed
        /// station's name to the pool -- nothing else releases names.
        /// </summary>
        private void RebuildStationNameUsage()
        {
            if (_stationNameSelector == null || _world == null) return;
            _stationNameSelector.InUseProvider = CollectStationNamesInUse;
            _stationNameSelector.RefreshNamesInUse();
        }

        /// <summary>
        /// Every station name the world currently carries, with the owner tag prefix stripped.
        /// Uniqueness is world-wide, so neutral-held and unowned nodes count too: a name is free
        /// only when no node anywhere carries it.
        /// </summary>
        private IEnumerable<string> CollectStationNamesInUse()
        {
            var names = new List<string>();
            if (_world == null) return names;

            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (node == null) continue;
                string name = node.StableStationId != 0
                    ? ResolveEconomyStationDisplayName(node.StableStationId)
                    : null;
                if (string.IsNullOrEmpty(name)) name = node.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                if (!string.IsNullOrEmpty(node.OwnerFactionTag))
                {
                    string tagPrefix = node.OwnerFactionTag + " ";
                    if (name.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(tagPrefix.Length);
                }
                names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// Retries name selection for nodes still stuck on the generic "Outpost" fallback from an
        /// earlier pool exhaustion. Must run after <see cref="RebuildStationNameUsage"/> (so names
        /// really in use are already excluded) and before
        /// <see cref="ApplyPersistedStationDisplayNames"/> (so a freshly-selected
        /// node.DisplayName is pushed onto the live MyFactionStation/grid by that existing pass).
        /// </summary>
        private void RetryFallbackStationNames()
        {
            if (_world == null) return;
            int renamed = _stationFounder.RetryFallbackStationNames(_world.Graph, _states);
            if (renamed > 0)
                _log.Write("STATION_FALLBACK_RENAMED " + renamed + " node(s)");
        }

        /// <summary>
        /// Backfills StationPrefabId and StationBookValue on all nodes that have a StableStationId
        /// but are missing one or both fields. Called on reload so existing nodes catch up after
        /// the founding logic is improved. Returns the number of nodes patched.
        /// </summary>
        public int BackfillStationFields()
        {
            if (_world == null) return 0;
            int count = _stationFounder.BackfillStationFields(_world.Graph, _states);
            if (count > 0) FlushOrDefer("station field backfill");
            return count;
        }

        /// <summary>Re-arms every in-flight fleet that came back from disk without weapon bands, from
        /// the hull it was dispatched with. Returns the number rebuilt. See
        /// <see cref="PendingFleetResolver.RebuildMissingWeaponBands"/> for why a restart disarmed
        /// them. No Flush: the bands are derived and are not written back.</summary>
        public int RebuildFleetWeaponBands()
        {
            if (_world == null) return 0;
            return PendingFleetResolver.RebuildMissingWeaponBands(_states, _world.PrefabCatalogue);
        }

        public void ApplyStationBookValues(Func<string, long> stationBookValueLookup)
        {
            if (_world == null || stationBookValueLookup == null) return;

            bool changed = false;
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (node == null || !Economy.HasPhysicalStation(node)) continue;

                string prefabId = node.StationPrefabId;
                if (string.IsNullOrEmpty(prefabId))
                {
                    prefabId = ResolveLiveStationPrefabName(node.StableStationId);
                    if (!string.IsNullOrEmpty(prefabId))
                    {
                        node.StationPrefabId = prefabId;
                        changed = true;
                    }
                }

                if (string.IsNullOrEmpty(prefabId)) continue;

                long bookValue = stationBookValueLookup(prefabId);
                if (bookValue <= 0) continue;
                if (!node.StationBookValue.HasValue || node.StationBookValue.Value != bookValue)
                {
                    node.StationBookValue = bookValue;
                    changed = true;
                }
            }

            if (changed) FlushOrDefer("station book values");
        }

        /// <summary>Station counterpart to <see cref="ApplyStationBookValues"/>: backfills
        /// <see cref="SpatialNode.StationWeapons"/> from the cached prefab weapon-band lookup so
        /// existing station-backed nodes pick up combat stats without a rescan.</summary>
        public void ApplyStationCombatStats(Func<string, System.Collections.Generic.List<WeaponBand>> weaponBandsLookup)
        {
            if (_world == null || weaponBandsLookup == null) return;

            bool changed = false;
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (node == null || !Economy.HasPhysicalStation(node)) continue;
                if (string.IsNullOrEmpty(node.StationPrefabId)) continue;
                if (node.StationWeapons != null && node.StationWeapons.Count > 0) continue;

                System.Collections.Generic.List<WeaponBand> bands = weaponBandsLookup(node.StationPrefabId);
                if (bands == null || bands.Count == 0) continue;
                node.StationWeapons = bands;
                changed = true;
            }

            if (changed) FlushOrDefer("station combat stats");
        }

        /// <summary>Records one completed host frame. The caller supplies the start timestamp,
        /// allowing the frame envelope to include category aggregation before its terminal counter
        /// update. The terminal two scalar increments are the only intentionally unmeasured observer cost.</summary>
        public void RecordHostFrameTiming(
            long baseUpdateTicks,
            long frameStartTimestamp,
            long playerCacheTicks,
            long runtimeTicks,
            long fleetObservationTicks,
            long stationObservationTicks,
            long asteroidObservationTicks,
            long priceTicks,
            long planetTicks,
            long bridgeTicks,
            long presenceTicks,
            long gpsTicks,
            long storeRefreshTicks)
        {
            long aggregationStart = Stopwatch.GetTimestamp();
            _perfBaseUpdate.Record(baseUpdateTicks);
            _perfPlayerCache.Record(playerCacheTicks);
            _perfRuntimeDispatch.Record(runtimeTicks);
            _perfFleetObservation.Record(fleetObservationTicks);
            _perfStationObservation.Record(stationObservationTicks);
            _perfAsteroidObservation.Record(asteroidObservationTicks);
            _perfPrice.Record(priceTicks);
            _perfPlanet.Record(planetTicks);
            _perfBridge.Record(bridgeTicks);
            _perfPresence.Record(presenceTicks);
            _perfGps.Record(gpsTicks);
            _perfStoreRefresh.Record(storeRefreshTicks);
            _perfInstrumentation.Record(Stopwatch.GetTimestamp() - aggregationStart);
            _perfFrame.Record(Stopwatch.GetTimestamp() - frameStartTimestamp);
        }

        /// <summary>Called every Update() frame. Drains at most one manager phase, one global pass
        /// (snapshot+decay+diplomacy), and one faction tick per frame. Top-level host timing records
        /// the complete composed frame; this method's one-manager policy is not a frame-budget guarantee.</summary>
        public void OnUpdateFrame()
        {
            OnUpdateFrame(new FrameDeadline(Stopwatch.GetTimestamp(), GetFrameSoftBudgetMilliseconds()));
        }

        internal void OnUpdateFrame(FrameDeadline deadline)
        {
            if (_world == null) return;
            // A pending restoration match, a bounded slice; nothing when none is pending.
            RestorationRefs.Pump();
            // The one read of each switch: Core takes it from the world as a bool.
            _world.TacticLearning = true;
            DateTime now = DateTime.UtcNow;
            _lastRealUtc = now;
            bool dueUnsafe = false;

            // Every frame, and deliberately not on any slower cadence. Events are recorded into
            // WorldModel.PendingEvents by whichever actor resolved them -- arrivals run on their own
            // cadence, managers on theirs -- and PendingEvents is NOT persisted. Anything still
            // sitting there when the process stops is gone, including from the flush on shutdown,
            // because that writes rings and this has not reached one yet.
            // (Found live 2026-08-22: a TRADE_RESOLVED 22 seconds before a deploy restart was in
            // neither participant's history afterwards. Draining on the global-pass cadence, as this
            // first did, left a window minutes wide on a day with 15 restarts in it.)
            // Costs a count check when there is nothing to file, which is the normal case.
            FactionEventLog.Drain(_states, _world, _tuning.RecentEventCap);

            // Same cadence and the same reason: the mirror must not be gated behind Speed > 0, or a
            // paused server's `sim step` would move the ledger with the visible wallet left behind.
            // Costs a count check when nothing moved, which is the normal case.
            _world.FlushTreasuryMirror();

            if ((now - _lastPerfFlushUtc).TotalSeconds >= GetPerfWindowSeconds())
            {
                RecordDueUnsafe(deadline);
                dueUnsafe = true;
                _lastPerfFlushUtc = now;
                WritePerfSnapshot(now);
            }
            if (Speed <= 0)
            {
                if (!dueUnsafe) _deadlineNotDueFrames++;
                return;
            }

            if (!_tuning.FrameSchedulerEnabled)
            {
                // Legacy path: four independently-budgeted drains, every one of them evaluated every
                // frame. Retained as the kill switch only, and removed once the scheduler has soaked.
                dueUnsafe |= DrainManagerPhase(now, deadline);
                dueUnsafe |= DrainGlobalPass(now, deadline);
                dueUnsafe |= DrainFactionTick(now, deadline);
                dueUnsafe |= DrainFlush(now, deadline);
                if (!dueUnsafe) _deadlineNotDueFrames++;
                return;
            }

            if (_scheduler == null)
            {
                _scheduler = BuildScheduler();
                SyncPerfSampleCapacity();
            }
            // One assignment per frame, hoisted out of the individual drains. It used to happen as a
            // side effect of DrainFactionTick running unconditionally; now that admission can decline
            // every actor, leaving it there would let the sim clock go stale on quiet frames.
            _world.UtcNow = _activeUtc = now;

            _scheduler.BeginFrame(deadline.StartTimestamp, now, GetFrameSoftBudgetMilliseconds());
            int actorId;
            FrameDeadline unitDeadline;
            while (_scheduler.TryAdmitNext(out actorId, out unitDeadline))
            {
                long t0 = Stopwatch.GetTimestamp();
                FrameActorOutcome outcome;
                long allocMark = AllocProbe.Begin();
                try { outcome = RunRuntimeActor(actorId, now, unitDeadline); }
                catch (Exception ex)
                {
                    // One misbehaving actor must not take the rest of the frame's actors with it,
                    // which the old straight-line drain sequence could not promise.
                    _log.Write("sched actor " + actorId + " threw: " + ex);
                    outcome = FrameActorOutcome.Skipped;
                }
                if (allocMark != 0) AllocProbe.End(_scheduler.GetStats(actorId).Name, allocMark);
                double deferredMs = _scheduler.RecordCompletion(actorId, Stopwatch.GetTimestamp() - t0, outcome, now);
                if (outcome == FrameActorOutcome.Completed) LogSweepDeferred(actorId, deferredMs);
            }
            _scheduler.EndFrame();
            ReportStarvation(now);
        }

        /// <summary>The fast operator signal for the failure this design has to avoid. A shared budget
        /// that starves an actor used to be visible only as a zero in a perf.json bucket, which is an
        /// hour of diffing; this makes it a grep.</summary>
        private void ReportStarvation(DateTime now)
        {
            int actorId;
            double deferredMs;
            if (!_scheduler.TryGetStarvingActor(now, out actorId, out deferredMs)) return;
            if (now < _starvationNextLogUtc) return;
            _starvationNextLogUtc = now.AddSeconds(StarvationLogIntervalSeconds);
            FrameActorStats st = _scheduler.GetStats(actorId);
            _log.Write("sched SCHED_STARVATION actor=" + st.Name
                + " deferredMs=" + deferredMs.ToString("0")
                + " intervalMs=" + (st.IntervalSeconds * 1000.0).ToString("0")
                + " estimateMs=" + st.EstimateMilliseconds.ToString("0.00")
                + " admissions=" + st.Admissions
                + " deniedByBudget=" + st.DeniedByBudget
                + " forced=" + st.ForcedAdmissions);
        }

        /// <summary>Keeps the familiar <c>*_SWEEP_DEFERRED</c> operator signal, now measured by the
        /// scheduler rather than by each drain's own latch. Only actors whose unit is a whole
        /// multi-frame sweep have a meaningful "late" to report.</summary>
        private void LogSweepDeferred(int actorId, double deferredMs)
        {
            string name = ActorLogName(actorId);
            if (name == null) return;
            double intervalMs = _scheduler.GetStats(actorId).IntervalSeconds * 1000.0;
            if (deferredMs <= intervalMs) return;
            _log.Write("mgr " + name + "_SWEEP_DEFERRED by=" + (deferredMs - intervalMs).ToString("0.0")
                + "ms -- consider raising that actor's interval or FrameSoftBudgetMilliseconds");
        }


        // ---- shared frame budget: registration and dispatch --------------------------------------

        /// <summary>Builds the actor registry. Seed costs come from the live 2026-08-21 baseline;
        /// they only ever affect whether an actor is admitted as a second-or-later unit in a frame,
        /// so a wrong seed delays an actor by at most one frame and can never starve it.</summary>
        private FrameScheduler BuildScheduler()
        {
            var sched = new FrameScheduler(Stopwatch.Frequency, null);
            Register(sched, ActorProd,       "prod",       GetManagerInterval(0),  1.60, true);
            Register(sched, ActorOps,        "ops",        GetManagerInterval(1),  0.60, true);
            // Seed is the per-faction chunk cost now, not the whole-roster 17.40 ms the sweep used
            // to cost in one call (FAC-291): the unit the scheduler admits is one faction's pass.
            Register(sched, ActorYard,       "yard",       GetManagerInterval(2),  6.00, true);
            Register(sched, ActorFleet,      "fleet",      GetManagerInterval(3),  1.20, false);
            Register(sched, ActorTrade,      "trade",      GetManagerInterval(4),  8.80, true);
            Register(sched, ActorStock,      "stock",      GetManagerInterval(5),  0.60, false);
            Register(sched, ActorGlobalPass, "globalPass", GetGlobalPassIntervalSeconds(),  0.60, true);
            Register(sched, ActorBrain,      "brain",      BrainActorIntervalSeconds(),     13.00, false);
            Register(sched, ActorFlush,      "flush",      GetFlushEveryRealSeconds(),      6.20, true);
            // Fleet arrival resolution is per-fleet work with no dependency on the influence snapshot
            // it used to queue behind as a global-pass phase. Gating it on the economy cadence meant a
            // fleet whose ETA had passed waited for a whole pass to complete -- minutes, at live scale.
            // Seed measured standalone (2026-08-21: 2.0 ms avg / 2.9 ms max). The 9.1 ms the old
            // econPhases.arrivals bucket reported was this work running inside the global pass, where
            // it inherited that pass's own accumulated per-invocation context.
            Register(sched, ActorArrivals,   "arrivals",   GetArrivalsIntervalSeconds(),    2.00, false);
            sched.SetSharedBudgetEnabled(_tuning.FrameSharedBudgetEnabled);
            if (_tuning.FrameMaxUnitsPerFrame > 0) sched.SetMaxUnitsPerFrame(_tuning.FrameMaxUnitsPerFrame);
            return sched;
        }

        private static void Register(FrameScheduler sched, int expectedId, string name,
            double intervalSeconds, double seedMs, bool resumable)
        {
            int id = sched.RegisterActor(new FrameActorSpec
            {
                Name = name,
                IntervalSeconds = intervalSeconds,
                SeedCostMilliseconds = seedMs,
                Resumable = resumable,
                // Staggered so actors sharing an interval do not co-land on the same frame forever.
                InitialPhaseOffsetSeconds = expectedId * 0.037,
            });
            if (id != expectedId)
                throw new InvalidOperationException("frame actor '" + name + "' registered as " + id
                    + " but RunRuntimeActor dispatches it as " + expectedId);
        }

        /// <summary>One admission ticks exactly one faction, so the actor has to be admitted once per
        /// faction per FactionTickIntervalSeconds for every faction to keep its own cadence. Scheduling
        /// it at the faction interval instead would tick one faction a minute regardless of roster
        /// size.</summary>
        private double BrainActorIntervalSeconds()
        {
            int factions = _states == null || _states.Count < 1 ? 1 : _states.Count;
            return GetFactionTickIntervalSeconds() / factions;
        }

        /// <summary>Re-syncs actor intervals from tuning and roster size, so a reload or a roster
        /// change does not require a restart to take effect on cadence.</summary>
        private void SyncSchedulerIntervals()
        {
            if (_scheduler == null) return;
            for (int slot = 0; slot < 6; slot++) _scheduler.SetInterval(slot, GetManagerInterval(slot));
            _scheduler.SetInterval(ActorGlobalPass, GetGlobalPassIntervalSeconds());
            _scheduler.SetInterval(ActorBrain,      BrainActorIntervalSeconds());
            _scheduler.SetInterval(ActorFlush,      GetFlushEveryRealSeconds());
            _scheduler.SetInterval(ActorArrivals,   GetArrivalsIntervalSeconds());
            _scheduler.SetSharedBudgetEnabled(_tuning.FrameSharedBudgetEnabled);
        }

        /// <summary>Sizes every frame-timing window's percentile sample ring to the number of frames
        /// one perf window can hold, so a raised <see cref="FactionTuning.PerfWindowSeconds"/> does
        /// not leave p50/p95/p99 describing only the window's tail while avg/max/over describe all of
        /// it. 60 fps is SE's simulation rate; the cap bounds the allocation on an absurd setting.</summary>
        private void SyncPerfSampleCapacity()
        {
            if (_allFrameWindows == null)
                _allFrameWindows = new[]
                {
                    _perfFrame, _perfInstrumentation, _perfBaseUpdate, _perfPlayerCache,
                    _perfRuntimeDispatch, _perfFleetObservation, _perfStationObservation,
                    _perfAsteroidObservation, _perfFeatureRock, _perfPrice, _perfPlanet, _perfBridge,
                    _perfPresence, _perfGps, _perfStoreRefresh, _perfContractGen, _perfStoreTxn,
                };

            double frames = GetPerfWindowSeconds() * 60.0;
            int capacity = frames >= MaxPerfSampleCapacity ? MaxPerfSampleCapacity : (int)Math.Ceiling(frames);
            if (capacity < MinPerfSampleCapacity) capacity = MinPerfSampleCapacity;
            for (int i = 0; i < _allFrameWindows.Length; i++)
                _allFrameWindows[i].EnsureSampleCapacity(capacity);
        }

        private const int MinPerfSampleCapacity = 4096;
        private const int MaxPerfSampleCapacity = 65536;

        /// <summary>Runs one admitted actor's bounded unit and reports what it did. The scheduler owns
        /// selection and rescheduling; this owns only dispatch.</summary>
        private FrameActorOutcome RunRuntimeActor(int actorId, DateTime now, FrameDeadline unitDeadline)
        {
            switch (actorId)
            {
                case ActorProd:       return RunProductionUnit(now, unitDeadline);
                case ActorOps:        return RunOperationsUnit(now, unitDeadline);
                case ActorYard:       return RunShipyardUnit(now, unitDeadline);
                case ActorFleet:      return RunSynchronousManagerUnit(3);
                case ActorTrade:      return RunTradeUnit(now, unitDeadline);
                case ActorStock:      return RunSynchronousManagerUnit(5);
                case ActorGlobalPass: return RunGlobalPassUnit(now, unitDeadline);
                case ActorBrain:      return RunBrainUnit(now);
                case ActorFlush:      return RunFlushUnit(now, unitDeadline);
                case ActorArrivals:   return RunArrivalsUnit();
            }
            return FrameActorOutcome.Skipped;
        }

        private FrameActorOutcome RunProductionUnit(DateTime now, FrameDeadline deadline)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool complete = _productionManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
            _perfProduction.Record(Stopwatch.GetTimestamp() - t0);
            FlushManagerLog();
            if (!complete) return FrameActorOutcome.Yielded;
            _perfProductionTelemetry.Record(_productionManager.LastTelemetry);
            return FrameActorOutcome.Completed;
        }

        private FrameActorOutcome RunOperationsUnit(DateTime now, FrameDeadline deadline)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool complete = _opsManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
            _perfOps.Record(Stopwatch.GetTimestamp() - t0);
            FlushManagerLog();
            if (!complete) return FrameActorOutcome.Yielded;
            _perfOperationsTelemetry.Record(_opsManager.LastTelemetry);
            return FrameActorOutcome.Completed;
        }

        private FrameActorOutcome RunTradeUnit(DateTime now, FrameDeadline deadline)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool complete = _tradeManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
            _perfTrade.Record(Stopwatch.GetTimestamp() - t0);
            FlushManagerLog();
            if (!complete) return FrameActorOutcome.Yielded;
            _perfTradeTelemetry.Record(_tradeManager.LastTelemetry);
            return FrameActorOutcome.Completed;
        }

        private FrameActorOutcome RunShipyardUnit(DateTime now, FrameDeadline deadline)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool complete = _shipyardManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
            _perfShipyard.Record(Stopwatch.GetTimestamp() - t0);
            FlushManagerLog();
            return complete ? FrameActorOutcome.Completed : FrameActorOutcome.Yielded;
        }

        /// <summary>Fleet and store-stock have no resumable form: each runs its whole roster in one
        /// call and always completes.</summary>
        private FrameActorOutcome RunSynchronousManagerUnit(int slot)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (slot == 3) { _posManager.Tick(_states, _world, _tuning, _managerLog);        _perfFleet.Record(Stopwatch.GetTimestamp() - t0); }
            else           { _storeStockManager.Tick(_states, _world, _tuning, _managerLog); _perfStoreStock.Record(Stopwatch.GetTimestamp() - t0); }
            FlushManagerLog();
            return FrameActorOutcome.Completed;
        }

        /// <summary>Resolves any fleet whose ETA has passed. Runs on its own cadence rather than as a
        /// global-pass phase: nothing here reads the influence snapshot, so queueing it behind a
        /// snapshot rebuild only delayed arrivals by however long the whole pass took.</summary>
        private FrameActorOutcome RunArrivalsUnit()
        {
            long t0 = Stopwatch.GetTimestamp();
            _arrivalsRunCount++;
            ResolveArrivals();
            _perfEconArrivals.Record(Stopwatch.GetTimestamp() - t0);
            return FrameActorOutcome.Completed;
        }

        private FrameActorOutcome RunGlobalPassUnit(DateTime now, FrameDeadline deadline)
        {
            if (_globalPassPhase == null)
            {
                // StepGlobalPassOnce's completion path reads this to reschedule and to report how
                // late the pass ran, so it has to be stamped before the first step.
                if (_globalPassSweepDueUtc == null) _globalPassSweepDueUtc = now;
                _globalPassCycleIntervalSeconds = GetGlobalPassIntervalSeconds();
                _globalPassElapsedTicks = 0;
                _globalPassSnapshotRestarts = 0;
                _world.BeginInfluenceSnapshotBuild();
                _globalPassPhase = GlobalPassPhase.BuildingSnapshot;
            }
            _invSnapshotTicks = 0; _invIncomeTicks = 0; _invDiplomacyTicks = 0; _invDecayTicks = 0;
            _invRenameTicks = 0; _invDiploEventsTicks = 0; _invLifecycleTicks = 0;
            _invKnowledgeTicks = 0;

            bool complete = false;
            while (!deadline.IsExpired())
            {
                if (StepGlobalPassOnce(now)) { complete = true; break; }
            }
            RecordGlobalPassInvocationPerf();
            return complete ? FrameActorOutcome.Completed : FrameActorOutcome.Yielded;
        }

        private FrameActorOutcome RunFlushUnit(DateTime now, FrameDeadline deadline)
        {
            if (_flushPhase == null)
            {
                _flushIndex = new List<IndexEntry>();
                _flushProductionEntries = new List<object>();
                _flushProductionHistoryEntries = new List<object>();
                _flushDemandEntries = new List<object>();
                _flushDemandDropped = 0;
                _flushFactionIndex = 0;
                _flushElapsedTicks = 0;
                _flushPhase = FlushPhase.BrainDocs;
            }
            bool complete = false;
            while (!deadline.IsExpired())
            {
                bool done;
                if (!TryStepFlushInFrame(now, out done)) break;
                if (done) { complete = true; break; }
            }
            return complete ? FrameActorOutcome.Completed : FrameActorOutcome.Yielded;
        }

        /// <summary>One faction's brain tick. Picks the most-overdue due faction rather than the first
        /// in list order, and steps past dormant ones instead of spending the whole unit on a no-op --
        /// bounded, so the skip loop can never itself blow a frame.</summary>
        private FrameActorOutcome RunBrainUnit(DateTime now)
        {
            const int DormantSkipLimit = 16;
            for (int skipped = 0; skipped < DormantSkipLimit; skipped++)
            {
                int index;
                if (!FactionTickSelection.TryPickMostOverdue(_states, now, out index))
                    return FrameActorOutcome.NotDue;

                FactionState st = _states[index];
                st.NextTickRealUtc = now.AddSeconds(GetFactionTickIntervalSeconds());
                if (FactionLifecycle.IsDormant(st, now) || FactionLifecycle.IsAutonomyDisabled(st))
                    continue;

                long t0 = Stopwatch.GetTimestamp();
                Personality p;
                if (!_personalities.TryGetValue(st.PersonalityId ?? "default", out p))
                    p = _personalities["default"];
                var rng = new SeededRandom(unchecked(st.RngSeed + (int)now.Ticks));
                Decision d = _brain.Think(st, p, _world, rng, _tuning);
                long tThink = Stopwatch.GetTimestamp();
                Enact(st, d, rng);
                long tEnact = Stopwatch.GetTimestamp();
                LogBrainDecision("tick", st, d);
                long tEnd = Stopwatch.GetTimestamp();
                RecordBrainPerf(t0, tThink, tEnact, tEnd);
                return FrameActorOutcome.Completed;
            }
            return FrameActorOutcome.Skipped;
        }

        private void RecordBrainPerf(long t0, long tThink, long tEnact, long tEnd)
        {
            BrainThinkTelemetry think = _brain.LastThink;
            _perfThinkSense.Record(think.SenseTicks);
            _perfThinkTradePriority.Record(think.TradePriorityTicks);
            _perfThinkReinforcePriority.Record(think.ReinforcePriorityTicks);
            _perfThinkThreatBonus.Record(think.ThreatBonusTicks);
            _perfThinkGenerate.Record(think.GenerateTicks);
            _perfThinkScore.Record(think.ScoreTicks);
            _perfThinkSelect.Record(think.SelectTicks);
            _perfBrainThink.Record(tThink - t0);
            _perfBrainEnact.Record(tEnact - tThink);
            _perfBrainLog.Record(tEnd - tEnact);
            _perfBrain.Record(tEnd - t0);
        }

        private static string ActorLogName(int actorId)
        {
            switch (actorId)
            {
                case ActorProd:       return "PRODUCTION";
                case ActorOps:        return "OPERATIONS";
                case ActorTrade:      return "TRADE";
                case ActorGlobalPass: return "GLOBAL_PASS";
                case ActorFlush:      return "FLUSH";
            }
            return null;
        }

        private void RecordDueUnsafe(FrameDeadline deadline)
        {
            _deadlineDueUnsafeCount++;
            if (deadline.IsExpired()) _deadlineExpiredBeforeUnsafeCount++;
        }

        // Global pass phases, in the same order the pre-Slice-2 monolithic body ran them.
        // BuildingSnapshot and AccruingIncome each span more than one call; every other phase is still
        // a single atomic call, checkpointed at its own phase boundary so the frame deadline is honored
        // between phases. BuildingSnapshot was chunked first (2026-08-17, ~93% of the pass then);
        // AccruingIncome followed once live evidence made it the dominant remaining single-frame block
        // (2026-08-19: 144-146 ms avg over two 60 s windows, ~365x FrameSoftBudgetMilliseconds, and
        // frame.maxMs tracking it exactly). Neither needs staged-effects/commit-guard apparatus, for
        // two different reasons: the snapshot rebuild publishes via one atomic buffer swap, and the
        // income pass chunks at boundaries that were already atomic in the synchronous version, with a
        // monotone cursor that never rewinds (see WorldModel.IncomePass.cs's header) and its three
        // published caches double-buffered.
        private enum GlobalPassPhase
        {
            BuildingSnapshot,
            AccruingIncome,
            DriftingDiplomacy,
            DecayingInfluence,
            RenamingAbandoned,
            SweepingKnowledge,
            ApplyingDiplomaticEvents,
            SteppingLifecycle,
        }

        // Flush phases, in the same order the original monolithic Flush()/WriteSnapshot() body ran
        // them. WorldSnapshot is the only phase that remains a single atomic call (live evidence,
        // 2026-08-17: it's cheap, ~3ms); every other phase is a per-faction loop admitted one faction
        // per StepFlushOnce call -- see StepFlushOnce's own comment for why no staged-effects/
        // commit-guard apparatus is needed. ProductionThroughput/ProductionHistory were split out of
        // a single atomic ProductionSnapshots phase after live perf.json evidence showed it was the
        // dominant remaining single-frame cost (~130ms, consistently across two 60s windows) once
        // BrainDocs/EventSnapshots were already chunked.
        private enum FlushPhase
        {
            BrainDocs,
            ProductionThroughput,
            ProductionHistory,
            DemandRegister,
            EventSnapshots,
            WorldSnapshot,
        }

        // Global pass: snapshot + economy + decay + diplomacy + lifecycle + arrivals.
        // Runs every GlobalPassIntervalSeconds (default 30 s). All economy ops still run here once per
        // pass for all factions — economy is a global system and AccrueIncome charges upkeep to every
        // owner. Since 2026-08-19 the income pass is itself resumable across frames, but it is still
        // one pass in one total order: its money-supply barrier (the whole-roster central-bank state
        // that makes a naive per-faction split wrong) is a phase of its own that cannot run until every
        // faction has been charged, and its cursor never rewinds, so no faction is charged twice. See
        // WorldModel.IncomePass.cs.
        private bool DrainGlobalPass(DateTime now, FrameDeadline deadline)
        {
            if (_world == null) return false;
            if (_globalPassPhase == null)
            {
                if (now < _globalPassNextUtc) return false;
                // _globalPassNextUtc defaults to DateTime.MinValue until the first pass ever
                // completes (same pre-existing pattern _managerNextUtc's elements share); using it
                // verbatim as the "due" baseline here would make GLOBAL_PASS_DEFERRED's logged delay
                // and _globalPassNextUtc's own next-scheduled value nonsensically huge on the very
                // first pass. Fall back to `now` in that case only.
                if (_globalPassSweepDueUtc == null)
                    _globalPassSweepDueUtc = _globalPassNextUtc == DateTime.MinValue ? now : _globalPassNextUtc;
                _globalPassCycleIntervalSeconds = GetGlobalPassIntervalSeconds();
                _globalPassElapsedTicks = 0;
                _globalPassSnapshotRestarts = 0;
                _world.UtcNow = _activeUtc = now;
                _world.BeginInfluenceSnapshotBuild();
                _globalPassPhase = GlobalPassPhase.BuildingSnapshot;
            }
            RecordDueUnsafe(deadline);

            // DrainManagerPhase runs immediately before this in OnUpdateFrame's sequence and reuses
            // the SAME shared `deadline`, so it always gets a fresh-looking budget purely by going
            // first -- its own admitted unit (Production/Ops TryAdvance) routinely overshoots the tiny
            // FrameSoftBudgetMilliseconds budget on its own (individually measured several ms, live
            // 2026-08-17), which leaves `deadline` already expired by the time DrainGlobalPass is
            // reached almost every frame. Live-confirmed starvation: 8+ minutes post-deploy with
            // econPhases.snapshot.calls still 0. A fresh, independently-timed deadline restores the
            // same "at least one admitted unit per frame" guarantee DrainManagerPhase already gets for
            // free by being scheduled first -- not a widened budget, the same size, just not
            // pre-exhausted by an earlier actor's own overshoot. DrainFactionTick (after this one)
            // already tolerates the same class of prior-actor overshoot by not gating its own
            // admission on `deadline` at all, so this isn't a new category of frame-time slack, just
            // extending the existing "each actor gets its own shot" precedent to a second
            // deadline-respecting actor.
            _invSnapshotTicks = 0; _invIncomeTicks = 0; _invDiplomacyTicks = 0; _invDecayTicks = 0;
            _invRenameTicks = 0; _invDiploEventsTicks = 0; _invLifecycleTicks = 0;
            _invKnowledgeTicks = 0;

            var ownDeadline = new FrameDeadline(Stopwatch.GetTimestamp(), GetFrameSoftBudgetMilliseconds());
            bool complete = false;
            while (!ownDeadline.IsExpired())
            {
                if (StepGlobalPassOnce(now)) { complete = true; break; }
            }

            RecordGlobalPassInvocationPerf();
            return complete;
        }

        /// <summary>Publishes this single <see cref="DrainGlobalPass"/> call's accumulated per-phase
        /// work into the same-shaped <see cref="PerfWindow"/> every other manager bucket uses, so
        /// "econ"/econPhases.* report cost-per-Update()-cycle like "prod"/"ops" do, not cost-per-
        /// completed-pass (a pass can span many thousands of invocations at live scale).</summary>
        /// <summary>One line per completed income pass: total cost and the split across the pass's own
        /// phases. Aggregated at completion only, never per admitted unit.</summary>
        private void LogIncomePassPerf()
        {
            double freq = Stopwatch.Frequency;
            long total = 0;
            foreach (long ticks in _globalPassIncomePhaseTicks.Values) total += ticks;
            var sb = new StringBuilder();
            sb.Append("global-pass INCOME_PASS_PERF steps=").Append(_globalPassIncomeSteps)
              .Append(" totalMs=").Append(Math.Round(total / freq * 1000.0, 3).ToString("0.###"));
            foreach (var kv in _globalPassIncomePhaseTicks)
                sb.Append(' ').Append(kv.Key).Append('=')
                  .Append(Math.Round(kv.Value / freq * 1000.0, 3).ToString("0.###"));
            _log.Write(sb.ToString());
            _globalPassIncomePhaseTicks.Clear();
            _globalPassIncomeSteps = 0;
        }

        private void RecordGlobalPassInvocationPerf()
        {
            long total = 0;
            if (_invSnapshotTicks    > 0) { _perfEconSnapshot.Record(_invSnapshotTicks);       total += _invSnapshotTicks; }
            if (_invIncomeTicks      > 0) { _perfEconIncome.Record(_invIncomeTicks);           total += _invIncomeTicks; }
            if (_invDiplomacyTicks   > 0) { _perfEconDiplomacy.Record(_invDiplomacyTicks);     total += _invDiplomacyTicks; }
            if (_invDecayTicks       > 0) { _perfEconDecay.Record(_invDecayTicks);             total += _invDecayTicks; }
            if (_invRenameTicks      > 0) { _perfEconRename.Record(_invRenameTicks);           total += _invRenameTicks; }
            if (_invKnowledgeTicks   > 0) { _perfEconKnowledge.Record(_invKnowledgeTicks);     total += _invKnowledgeTicks; }
            if (_invDiploEventsTicks > 0) { _perfEconDiploEvents.Record(_invDiploEventsTicks); total += _invDiploEventsTicks; }
            if (_invLifecycleTicks   > 0) { _perfEconLifecycle.Record(_invLifecycleTicks);     total += _invLifecycleTicks; }
            if (total > 0) _perfEconomy.Record(total);
        }

        /// <summary>Runs exactly one bounded unit of the global pass — either one query-node row of
        /// the snapshot rebuild, or one whole subsequent phase — with no deadline check of its own.
        /// Returns true once the entire pass (through SteppingLifecycle) has completed.</summary>
        private bool StepGlobalPassOnce(DateTime now)
        {
            _world.UtcNow = _activeUtc = now;
            long p0 = Stopwatch.GetTimestamp();
            switch (_globalPassPhase.Value)
            {
                case GlobalPassPhase.BuildingSnapshot:
                {
                    bool doneScanning = _world.AdvanceInfluenceSnapshotBuild();
                    long elapsed0 = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed0;
                    _invSnapshotTicks += elapsed0;
                    if (!doneScanning) return false;
                    if (_world.IsInfluenceSnapshotBuildStale())
                    {
                        // Topology changed structurally (a node added/removed) mid-build -- discard
                        // and restart this phase fresh rather than commit a structurally-stale
                        // result. See InfluenceField.IsSnapshotBuildStale's own doc comment for why
                        // pure ownership/hold drift on still-existing nodes is not treated the same way.
                        _world.AbandonInfluenceSnapshotBuild();
                        _world.BeginInfluenceSnapshotBuild();
                        _globalPassSnapshotRestarts++;
                        if (_globalPassSnapshotRestarts % 10 == 0)
                            _log.Write("global-pass SNAPSHOT_BUILD_CHURN restarts=" + _globalPassSnapshotRestarts
                                + " -- topology is changing faster than the snapshot rebuild can complete");
                        return false;
                    }
                    _world.CommitInfluenceSnapshotBuild();
                    _globalPassPhase = GlobalPassPhase.AccruingIncome;
                    return false;
                }
                case GlobalPassPhase.AccruingIncome:
                {
                    // Resumable, one bounded unit per call (WorldModel.IncomePass.cs). Deliberately no
                    // IsStale/Abandon/restart counterpart to BuildingSnapshot's above: an income pass
                    // mutates real ledgers as it advances, so restarting one would re-charge factions it
                    // has already charged. Its cursor is monotone and never rewinds instead.
                    if (!_world.IsIncomePassInProgress()) _world.BeginIncomePass(_states);
                    string incomePhase = _world.DiagnosticIncomePassPhase;
                    bool doneIncome = _world.AdvanceIncomePass();
                    // The insolvency shed abandons a node inside the pass; its evacuation lines are
                    // written in the same step that flew the hulls.
                    LogShipEvacuations();
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed;
                    _invIncomeTicks += elapsed;
                    long phaseTicks;
                    _globalPassIncomePhaseTicks.TryGetValue(incomePhase, out phaseTicks);
                    _globalPassIncomePhaseTicks[incomePhase] = phaseTicks + elapsed;
                    _globalPassIncomeSteps++;
                    if (!doneIncome) return false;
                    LogIncomePassPerf();
                    _globalPassPhase = GlobalPassPhase.DriftingDiplomacy;
                    return false;
                }
                case GlobalPassPhase.DriftingDiplomacy:
                {
                    foreach (FactionState st in _states)
                    {
                        if (FactionLifecycle.IsDormant(st, now)) continue;
                        Personality p;
                        if (!_personalities.TryGetValue(st.PersonalityId ?? "default", out p))
                            p = _personalities["default"];
                        Diplomacy.Drift(st, p, _world, _tuning);
                        Diplomacy.AccrueFatigue(st, _world, _tuning);
                        // MilitaryPosture erodes slowly when not reinforced by a fresh Muster dispatch —
                        // a floor a faction settles toward, not a one-way ratchet toward permanent posture 1.0.
                        if (_tuning.MilitaryPostureDecayPerHour > 0 && st.MilitaryPosture > 0)
                            st.MilitaryPosture = Math.Max(0.0, st.MilitaryPosture
                                - _tuning.MilitaryPostureDecayPerHour * (_globalPassCycleIntervalSeconds / 3600.0));
                    }
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed;
                    _invDiplomacyTicks += elapsed;
                    _globalPassPhase = GlobalPassPhase.DecayingInfluence;
                    return false;
                }
                case GlobalPassPhase.DecayingInfluence:
                {
                    _world.DecayInfluence(_tuning.HoldDecayForIntervalSeconds(_globalPassCycleIntervalSeconds),
                        _tuning.PlayerHoldDecayForIntervalSeconds(_globalPassCycleIntervalSeconds), _states,
                        _globalPassCycleIntervalSeconds);
                    _world.DecayThreat(_tuning.ThreatDecayForIntervalSeconds(_globalPassCycleIntervalSeconds));
                    RunWorkerUpkeep(now);
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed;
                    _invDecayTicks += elapsed;
                    _globalPassPhase = GlobalPassPhase.RenamingAbandoned;
                    return false;
                }
                case GlobalPassPhase.RenamingAbandoned:
                {
                    RenameAbandonedStationsFromDecay();
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed;
                    _invRenameTicks += elapsed;
                    _globalPassPhase = GlobalPassPhase.SweepingKnowledge;
                    return false;
                }
                case GlobalPassPhase.SweepingKnowledge:
                {
                    // One faction per call, like every other per-faction phase here: the sweep is
                    // bounded work but it is not free, and admitting the whole roster in one frame is
                    // exactly the ungoverned-observer shape FAC-016 flags.
                    if (_knowledgeSweepCursor == 0) EnsureNodeIndex();
                    if (_knowledgeSweepCursor == 0) RefreshPlayerContacts();
                    if (_knowledgeSweepCursor == 0) _knowledgeBlockedAtPassStart = SensorOcclusion.BlockedLines;
                    if (_knowledgeSweepCursor < _states.Count)
                    {
                        FactionState st = _states[_knowledgeSweepCursor++];
                        DateTime nowUtc = _world.UtcNow;
                        SensorOcclusion occlusion = SensorOcclusion;
                        int touched = KnowledgeSweep.SweepOwnedNodes(
                            _world.Graph, _nodeIndex, st, _tuning, nowUtc, occlusion);
                        // A convoy surveys the whole leg it flew since the previous sweep, not just
                        // where it happens to be now. The previous position is derived from the
                        // fleet's own progress one sweep ago -- TransitProgressRatio clamps at both
                        // ends, so a fleet that departed mid-interval yields its origin and one that
                        // arrived yields its target, with nothing extra persisted.
                        DateTime priorUtc = nowUtc.AddSeconds(-_globalPassCycleIntervalSeconds);
                        for (int i = 0; i < st.PendingFleets.Count; i++)
                        {
                            PendingFleetAction fleet = st.PendingFleets[i];
                            if (fleet.Intercepted || fleet.Resolved) continue;
                            // An arrived fleet is deliberately NOT skipped. At 1500 km/min against a
                            // 180 s sweep, every leg under 4500 km begins and ends between two
                            // sweeps, so skipping arrivals meant most fleets were never swept at all
                            // -- the larger half of FAC-081. The clamping in TransitProgressRatio
                            // makes this exact: the segment is wherever it was last sweep to its
                            // target, and re-sweeping is idempotent because Touch only renews.
                            touched += KnowledgeSweep.SweepFleetSegment(
                                _world.Graph, _nodeIndex, st,
                                fleet.CurrentPosKm(priorUtc, _tuning),
                                fleet.CurrentPosKm(nowUtc, _tuning),
                                _tuning, nowUtc, _knowledgeScratch, _knowledgeScratchSeen, occlusion);
                        }
                        _knowledgeTouchedThisPass += touched;
                        SweepSensedPlayers(st, nowUtc);
                        long elapsedOne = Stopwatch.GetTimestamp() - p0;
                        _globalPassElapsedTicks += elapsedOne;
                        _invKnowledgeTicks += elapsedOne;
                        return false;
                    }
                    if (DebugTraces.Sensing)
                    {
                        SensorOcclusion field = SensorOcclusion;
                        _log.Write("KNOWLEDGE_SWEEP touched=" + _knowledgeTouchedThisPass
                                   + " blocked=" + (field.BlockedLines - _knowledgeBlockedAtPassStart)
                                   + " bodies=" + field.Count
                                   + " lift=" + field.LiftKm.ToString("0.##") + "km");
                    }
                    _knowledgeSweepCursor = 0;
                    _knowledgeTouchedThisPass = 0;
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed;
                    _invKnowledgeTicks += elapsed;
                    _globalPassPhase = GlobalPassPhase.ApplyingDiplomaticEvents;
                    return false;
                }
                case GlobalPassPhase.ApplyingDiplomaticEvents:
                {
                    ApplyDiplomaticEvents();
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed;
                    _invDiploEventsTicks += elapsed;
                    _globalPassPhase = GlobalPassPhase.SteppingLifecycle;
                    return false;
                }
                case GlobalPassPhase.SteppingLifecycle:
                default:
                {
                    FactionLifecycle.Step(_states, _world, _tuning,
                        tag => _log.Write("global-pass CONQUERED " + tag + " -> dormant"));
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _globalPassElapsedTicks += elapsed;
                    _invLifecycleTicks += elapsed;
                    _simTick++;

                    DateTime nextDueIfInstant = _globalPassSweepDueUtc.Value.AddSeconds(_globalPassCycleIntervalSeconds);
                    if (now > nextDueIfInstant)
                    {
                        double deferredMs = (now - nextDueIfInstant).TotalMilliseconds;
                        _log.Write("mgr GLOBAL_PASS_DEFERRED by=" + deferredMs.ToString("0.0")
                            + "ms -- consider raising GlobalPassIntervalSeconds or FrameSoftBudgetMilliseconds");
                    }
                    _globalPassNextUtc = _globalPassSweepDueUtc.Value.AddSeconds(_globalPassCycleIntervalSeconds);
                    _globalPassSweepDueUtc = null;
                    _globalPassPhase = null;
                    return true;
                }
            }
        }

        // Per-faction brain tick: one faction per Update() frame, chosen by wall-clock schedule.
        // Only Think + Enact; economy runs separately in DrainGlobalPass once per pass for all factions.
        // Each faction gets a brain tick once every FactionTickIntervalSeconds (default 60 s) of real time.
        private bool DrainFactionTick(DateTime now, FrameDeadline deadline)
        {
            if (_world == null) return false;
            _world.UtcNow = _activeUtc = now;
            foreach (FactionState st in _states)
            {
                if (st.NextTickRealUtc > now) continue;
                RecordDueUnsafe(deadline);
                st.NextTickRealUtc = now.AddSeconds(GetFactionTickIntervalSeconds());
                if (FactionLifecycle.IsDormant(st, now) || FactionLifecycle.IsAutonomyDisabled(st)) return true;
                long t0 = Stopwatch.GetTimestamp();
                Personality p;
                if (!_personalities.TryGetValue(st.PersonalityId ?? "default", out p))
                    p = _personalities["default"];
                var rng = new SeededRandom(unchecked(st.RngSeed + (int)now.Ticks));
                Decision d = _brain.Think(st, p, _world, rng, _tuning);
                long tThink = Stopwatch.GetTimestamp();
                Enact(st, d, rng);
                long tEnact = Stopwatch.GetTimestamp();
                LogBrainDecision("tick", st, d);
                long tEnd = Stopwatch.GetTimestamp();
                BrainThinkTelemetry think = _brain.LastThink;
                _perfThinkSense.Record(think.SenseTicks);
                _perfThinkTradePriority.Record(think.TradePriorityTicks);
                _perfThinkReinforcePriority.Record(think.ReinforcePriorityTicks);
                _perfThinkThreatBonus.Record(think.ThreatBonusTicks);
                _perfThinkGenerate.Record(think.GenerateTicks);
                _perfThinkScore.Record(think.ScoreTicks);
                _perfThinkSelect.Record(think.SelectTicks);
                _perfBrainThink.Record(tThink - t0);
                _perfBrainEnact.Record(tEnact - tThink);
                _perfBrainLog.Record(tEnd - tEnact);
                _perfBrain.Record(tEnd - t0);
                return true; // one faction per frame
            }
            return false;
        }

        // Periodic real-time-triggered flush (every GetFlushEveryRealSeconds()). Mirrors
        // DrainGlobalPass's shape exactly, including the "own fresh deadline" rationale
        // (FactionRuntime.cs's DrainGlobalPass comment): running last in the sequence means the
        // shared `deadline` is routinely pre-exhausted by an earlier actor's own overshoot, which
        // would starve Flush indefinitely without this. Returns true only once the whole sweep
        // (potentially spanning several frames) completes in this call -- same convention
        // DrainGlobalPass uses, RecordDueUnsafe below already accounts for "did work happen"
        // regardless of whether the sweep as a whole finished.
        private bool DrainFlush(DateTime now, FrameDeadline deadline)
        {
            if (_world == null) return false;
            if (_flushPhase == null)
            {
                if (now < _lastFlushUtc.AddSeconds(GetFlushEveryRealSeconds())) return false;
                if (_flushSweepDueUtc == null) _flushSweepDueUtc = now;
                _flushIndex = new List<IndexEntry>();
                _flushProductionEntries = new List<object>();
                _flushProductionHistoryEntries = new List<object>();
                _flushFactionIndex = 0;
                _flushElapsedTicks = 0;
                _flushPhase = FlushPhase.BrainDocs;
            }
            RecordDueUnsafe(deadline);

            var ownDeadline = new FrameDeadline(Stopwatch.GetTimestamp(), GetFrameSoftBudgetMilliseconds());
            bool complete = false;
            while (!ownDeadline.IsExpired())
            {
                bool done;
                if (!TryStepFlushInFrame(now, out done)) break;
                if (done) { complete = true; break; }
            }
            return complete;
        }

        /// <summary>Retries a brain write that another process holds (<see cref="Flush"/> only).
        /// Each retry sleeps the calling thread <see cref="FactionStore.BrainWriteRetryDelayMs"/>.</summary>
        private const int ImmediateFlushLockedRetries = 3;

        /// <summary>One <see cref="StepFlushOnce"/> from a per-frame sweep. A write the file system
        /// refuses is not retried here, since a retry would sleep the frame: the step returns false,
        /// the sweep stays on that faction, and the next flush unit resumes it (FAC-371). Warns at
        /// most once a minute, so a file that stays held is still reported.</summary>
        private bool TryStepFlushInFrame(DateTime now, out bool done)
        {
            try
            {
                done = StepFlushOnce(now, 0);
                return true;
            }
            catch (Exception ex) when (FactionStore.IsRefusedWrite(ex))
            {
                done = false;
                _flushDeferredSinceWarn++;
                if (now >= _nextFlushDeferWarnUtc)
                {
                    FactionsPlugin.Log.Warn("Flush step deferred to the next frame (" + _flushDeferredSinceWarn
                                            + " since the last warning): " + ex.Message);
                    _flushDeferredSinceWarn = 0;
                    _nextFlushDeferWarnUtc = now.AddSeconds(60);
                }
                return false;
            }
        }

        /// <summary>One phase's single unit of work per call, mirroring StepGlobalPassOnce. Unlike
        /// Production/Operations' cycles, Flush only reads <see cref="_states"/>/<see cref="_world"/>
        /// and writes to disk/log -- it never mutates simulation state -- so no staged-effects/
        /// commit-guard apparatus is needed here; a resumed sweep simply continues writing files it
        /// hasn't gotten to yet. Composes correctly with the public synchronous <see cref="Flush"/>
        /// too: if a <see cref="DrainFlush"/> sweep is already mid-phase when Flush() is called
        /// directly (e.g. a bridge command's flush-before-registry-edit), Flush() resumes and finishes
        /// this same in-progress sweep rather than starting a second one. Returns true once
        /// WorldSnapshot has run (whole sweep complete).</summary>
        private bool StepFlushOnce(DateTime now, int lockedRetries)
        {
            long p0 = Stopwatch.GetTimestamp();
            switch (_flushPhase.Value)
            {
                case FlushPhase.BrainDocs:
                {
                    if (_states.Count > 0)
                    {
                        FactionState st = _states[_flushFactionIndex];
                        // Drop lapsed entries before serialising. This is the retention bound on a
                        // persisted collection, and it is purely a file-size measure: HasLive already
                        // ignores a lapsed entry, so compacting cannot change what anything reads.
                        st.Knowledge.Compact(_world.UtcNow, _tuning.DiscoveryHorizon);
                        BrainDoc doc = FactionStore.ToBrainDoc(st, _world.Graph.OwnedBy(st.Tag), FactionStore.DefaultLogCap);
                        doc.Treasury = _world.GetTreasury(st.Tag);
                        long docDebt = _world.GetDebt(st.Tag);
                        if (docDebt > 0) doc.Debt = docDebt;
                        doc.Inventory = null;  // inventory is now per-node in SourceDoc; field kept for migration reads
                        _store.SaveBrain(doc, lockedRetries);
                        _flushIndex.Add(FactionStore.ToIndexEntry(st, _world.UtcNow));
                        _flushFactionIndex++;
                    }
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _flushElapsedTicks += elapsed;
                    _perfFlushBrains.Record(elapsed);
                    if (_flushFactionIndex < _states.Count) return false;
                    _store.SaveIndex(_flushIndex);
                    _store.SaveCentralBankState(_world);
                    _flushFactionIndex = 0;
                    _flushPhase = FlushPhase.ProductionThroughput;
                    return false;
                }
                case FlushPhase.ProductionThroughput:
                {
                    if (_states.Count > 0)
                    {
                        FactionState st = _states[_flushFactionIndex];
                        foreach (ThroughputEntry e in _productionManager.SnapshotThroughput(st.Tag))
                        {
                            _flushProductionEntries.Add(new
                            {
                                factionTag      = e.FactionTag,
                                nodeId          = e.NodeId,
                                cap             = e.Cap.ToString(),
                                primaryInputId  = e.PrimaryInputId,
                                primaryOutputId = e.PrimaryOutputId,
                                nominalItemsPerMinute  = Math.Round(e.NominalItemsPerMinute, 2),
                                observedItemsPerMinute = Math.Round(e.ObservedItemsPerMinute, 2),
                                effectiveMachineSpeed  = Math.Round(e.EffectiveMachineSpeed, 3),
                                outputScale            = Math.Round(e.OutputScale, 3),
                                slotCount       = e.SlotCount,
                            });
                        }
                        _flushFactionIndex++;
                    }
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _flushElapsedTicks += elapsed;
                    _perfFlushProduction.Record(elapsed);
                    if (_flushFactionIndex < _states.Count) return false;
                    WriteProductionSnapshotFile(_flushProductionEntries);
                    _flushFactionIndex = 0;
                    _flushPhase = FlushPhase.ProductionHistory;
                    return false;
                }
                case FlushPhase.ProductionHistory:
                {
                    if (_states.Count > 0)
                    {
                        FactionState st = _states[_flushFactionIndex];
                        foreach (ProductionManager.ProductionHistoryEntry e in _productionManager.SnapshotProductionHistory(st.Tag))
                        {
                            _flushProductionHistoryEntries.Add(new
                            {
                                factionTag = e.FactionTag,
                                nodeId     = e.NodeId,
                                recipeId   = e.RecipeId,
                                invKey     = e.InvKey,
                                isOutput   = e.IsOutput,
                                quantity   = e.Quantity,
                                startUtc   = e.StartUtc.ToString("o"),
                                endUtc     = e.EndUtc.ToString("o"),
                            });
                        }
                        _flushFactionIndex++;
                    }
                    long elapsed = Stopwatch.GetTimestamp() - p0;
                    _flushElapsedTicks += elapsed;
                    _perfFlushProduction.Record(elapsed);
                    if (_flushFactionIndex < _states.Count) return false;
                    WriteProductionHistorySnapshotFile(_flushProductionHistoryEntries);
                    _flushFactionIndex = 0;
                    _flushPhase = FlushPhase.DemandRegister;
                    return false;
                }
                case FlushPhase.DemandRegister:
                {
                    if (_states.Count > 0)
                    {
                        FactionState st = _states[_flushFactionIndex];
                        List<DemandEntry> rows = _world.Demand.SnapshotItemDemands(st.Tag);
                        foreach (DemandEntry e in SelectDemandRows(rows))
                        {
                            // The node's own stock of the key rides along because Quantity is GROSS
                            // by contract (docs/material-vectors.md) -- MatchVectors nets the
                            // destination's stock at dispatch, so a row read without it says
                            // nothing about whether anything will actually move.
                            long have = e.NodeId != null ? _world.GetNodeInventory(e.NodeId, KeyOf(e.Kind)) : 0;
                            _flushDemandEntries.Add(new
                            {
                                factionTag = st.Tag,
                                kind       = e.Kind,
                                nodeId     = e.NodeId,
                                quantity   = e.Quantity,
                                haveAtNode = have,
                                demandClass = e.Class.ToString(),
                                chainSized = e.ChainSized,
                                expiresUtc = e.ExpiresUtc.ToString("o"),
                            });
                        }
                        _flushDemandDropped += Math.Max(0, rows.Count - Math.Min(rows.Count, MaxDemandRowsPerFaction));
                    }
                    _flushFactionIndex++;
                    long demandElapsed = Stopwatch.GetTimestamp() - p0;
                    _flushElapsedTicks += demandElapsed;
                    if (_flushFactionIndex < _states.Count) return false;
                    WriteDemandSnapshotFile(_flushDemandEntries);
                    _flushFactionIndex = 0;
                    _flushPhase = _eventWriter != null ? FlushPhase.EventSnapshots : FlushPhase.WorldSnapshot;
                    return false;
                }
                case FlushPhase.EventSnapshots:
                {
                    if (_flushFactionIndex == 0)
                    {
                        _world.Demand.Prune(_world.UtcNow);
                        _world.Investments.Prune(_world.UtcNow);
                    }
                    if (_states.Count > 0)
                    {
                        FactionState st = _states[_flushFactionIndex];
                        List<DemandEntry> demands = _world.Demand.SnapshotShipDemands(st.Tag);
                        // Merge item + positioning demands into same snapshot
                        demands.AddRange(_world.Demand.SnapshotItemDemands(st.Tag));
                        demands.AddRange(_world.Demand.SnapshotPositioningDemands(st.Tag));
                        // Investment requests carry a capability, not a node -- the node is the
                        // brain's Phase-1 choice. Resolve it here so the dashboard can show what
                        // each signal is aiming at (see InvestmentTargeting).
                        List<InvestmentSignal> investments =
                            InvestmentTargeting.SnapshotSignals(_world, st.Tag, _tuning);
                        // Most urgent first, so the per-kind row cap the snapshot applies keeps the
                        // rows worth looking at rather than simply the oldest-posted ones. The
                        // ranked rows carry the score and node stock they were placed by, which is
                        // what the panel shows beside each quantity.
                        List<DemandSnapshotRow> ranked =
                            DemandSnapshotOrder.RankForSnapshot(demands, _world, _tuning);
                        _eventWriter.WriteDemandsSnapshot(st.Tag, ranked, investments);
                        _flushFactionIndex++;
                    }
                    _flushElapsedTicks += Stopwatch.GetTimestamp() - p0;
                    if (_flushFactionIndex < _states.Count) return false;
                    _eventWriter.WriteHeartbeat(_simTick);
                    _flushPhase = FlushPhase.WorldSnapshot;
                    return false;
                }
                case FlushPhase.WorldSnapshot:
                default:
                {
                    WriteSnapshot(); // records its own _perfFlushSnapshot internally
                    _flushElapsedTicks += Stopwatch.GetTimestamp() - p0;
                    _perfFlushTotal.Record(_flushElapsedTicks);
                    if (_flushSweepDueUtc != null)
                    {
                        DateTime nextDueIfInstant = _flushSweepDueUtc.Value.AddSeconds(GetFlushEveryRealSeconds());
                        if (now > nextDueIfInstant)
                        {
                            double deferredMs = (now - nextDueIfInstant).TotalMilliseconds;
                            _log.Write("FLUSH_DEFERRED by=" + deferredMs.ToString("0.0")
                                + "ms -- consider raising FlushEveryRealSeconds or FrameSoftBudgetMilliseconds");
                        }
                        _flushSweepDueUtc = null;
                    }
                    _lastFlushUtc = now;
                    _flushPhase = null;
                    return true;
                }
            }
        }

        // Run the most-overdue manager (one per Update() frame).
        // Each manager has an independent interval so Fleet (1 s) fires 60× more often than
        // Trade (60 s) without blocking the others when overdue.
        private bool DrainManagerPhase(DateTime now, FrameDeadline deadline)
        {
            if (_world == null) return false;
            int mostOverdue = -1;
            double maxOverdueSec = 0;
            for (int i = 0; i < _managerNextUtc.Length; i++)
            {
                if (_managerNextUtc[i] > now) continue;
                double overdue = (now - _managerNextUtc[i]).TotalSeconds;
                if (overdue > maxOverdueSec) { maxOverdueSec = overdue; mostOverdue = i; }
            }
            if (mostOverdue < 0) return false;
            RecordDueUnsafe(deadline);
            _world.UtcNow = _activeUtc = now;
            long t0 = Stopwatch.GetTimestamp();
            switch (mostOverdue)
            {
                case 0:
                {
                    // Remember when this sweep first became due (a no-op if one is already in
                    // flight) so a completion that spans several frames can report how late that
                    // made the following tick, without needing the reschedule below to have already
                    // happened.
                    if (_productionSweepDueUtc == null)
                        _productionSweepDueUtc = _managerNextUtc[0] == DateTime.MinValue ? now : _managerNextUtc[0];
                    bool complete = _productionManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
                    _perfProduction.Record(Stopwatch.GetTimestamp() - t0);
                    if (!complete)
                    {
                        // Sweep still in progress: leave _managerNextUtc[0] in the past so it keeps
                        // winning "most overdue" and resumes on a later frame instead of a fresh sweep
                        // starting over it. Flush whatever any already-committed faction logged this
                        // call — no reason to hold those back until the whole sweep finishes.
                        FlushManagerLog();
                        return true;
                    }
                    // Recorded only once the sweep completes: LastTelemetry is the cumulative
                    // in-progress sweep total, so recording it on every yielded invocation
                    // multiplies the same totals by the invocation count. Case 1 does the same.
                    _perfProductionTelemetry.Record(_productionManager.LastTelemetry);
                    DateTime nextDueIfInstant = _productionSweepDueUtc.Value.AddSeconds(GetManagerInterval(0));
                    if (now > nextDueIfInstant)
                    {
                        double deferredMs = (now - nextDueIfInstant).TotalMilliseconds;
                        _log.Write("mgr PRODUCTION_SWEEP_DEFERRED by=" + deferredMs.ToString("0.0")
                            + "ms -- consider raising ProductionIntervalSeconds or FrameSoftBudgetMilliseconds");
                    }
                    _productionSweepDueUtc = null;
                    break;
                }
                case 1:
                {
                    // Mirrors case 0's ProductionManager.TryAdvance wiring exactly (see its comments
                    // for the full rationale) -- Operations gained the same resumable staged-cycle
                    // shape in Slice 3. Telemetry is recorded only once the sweep actually completes,
                    // not on every yielded invocation: LastTelemetry is the cumulative in-progress
                    // sweep total, so recording it on every yield would double/triple/... -count the
                    // same totals into _perfOperationsTelemetry.
                    if (_operationsSweepDueUtc == null)
                        _operationsSweepDueUtc = _managerNextUtc[1] == DateTime.MinValue ? now : _managerNextUtc[1];
                    bool complete = _opsManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
                    _perfOps.Record(Stopwatch.GetTimestamp() - t0);
                    if (!complete)
                    {
                        FlushManagerLog();
                        return true;
                    }
                    _perfOperationsTelemetry.Record(_opsManager.LastTelemetry);
                    DateTime opsNextDueIfInstant = _operationsSweepDueUtc.Value.AddSeconds(GetManagerInterval(1));
                    if (now > opsNextDueIfInstant)
                    {
                        double deferredMs = (now - opsNextDueIfInstant).TotalMilliseconds;
                        _log.Write("mgr OPERATIONS_SWEEP_DEFERRED by=" + deferredMs.ToString("0.0")
                            + "ms -- consider raising OperationsIntervalSeconds or FrameSoftBudgetMilliseconds");
                    }
                    _operationsSweepDueUtc = null;
                    break;
                }
                case 2:
                {
                    // Mirrors case 4's TradeManager.TryAdvance wiring exactly (see its comments for
                    // the full rationale) -- ShipyardManager gained the same Design 1 per-faction-only
                    // resumable cycle in FAC-291. It records no LastTelemetry of its own, so the only
                    // thing held back until completion is the reschedule.
                    if (_shipyardSweepDueUtc == null)
                        _shipyardSweepDueUtc = _managerNextUtc[2] == DateTime.MinValue ? now : _managerNextUtc[2];
                    bool complete = _shipyardManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
                    _perfShipyard.Record(Stopwatch.GetTimestamp() - t0);
                    if (!complete)
                    {
                        FlushManagerLog();
                        return true;
                    }
                    DateTime yardNextDueIfInstant = _shipyardSweepDueUtc.Value.AddSeconds(GetManagerInterval(2));
                    if (now > yardNextDueIfInstant)
                    {
                        double deferredMs = (now - yardNextDueIfInstant).TotalMilliseconds;
                        _log.Write("mgr SHIPYARD_SWEEP_DEFERRED by=" + deferredMs.ToString("0.0")
                            + "ms -- consider raising ShipyardIntervalSeconds or FrameSoftBudgetMilliseconds");
                    }
                    _shipyardSweepDueUtc = null;
                    break;
                }
                case 3: _posManager.Tick(_states, _world, _tuning, _managerLog);        _perfFleet.Record(Stopwatch.GetTimestamp() - t0);      break;
                case 4:
                {
                    // Mirrors case 1's OperationsManager.TryAdvance wiring exactly (see its comments
                    // for the full rationale) -- TradeManager gained its own resumable cycle in Slice B
                    // of the TradeManager resumable-cycle plan (Design 1: per-faction-only chunking,
                    // no staged-effects/commit-guard apparatus -- see TradeManager.Cycle.cs's header).
                    // Telemetry is recorded only once the sweep actually completes, not on every
                    // yielded invocation, for the same reason as Operations' wiring.
                    if (_tradeSweepDueUtc == null)
                        _tradeSweepDueUtc = _managerNextUtc[4] == DateTime.MinValue ? now : _managerNextUtc[4];
                    bool complete = _tradeManager.TryAdvance(_states, _world, _tuning, deadline, _managerLog);
                    _perfTrade.Record(Stopwatch.GetTimestamp() - t0);
                    if (!complete)
                    {
                        FlushManagerLog();
                        return true;
                    }
                    _perfTradeTelemetry.Record(_tradeManager.LastTelemetry);
                    DateTime tradeNextDueIfInstant = _tradeSweepDueUtc.Value.AddSeconds(GetManagerInterval(4));
                    if (now > tradeNextDueIfInstant)
                    {
                        double deferredMs = (now - tradeNextDueIfInstant).TotalMilliseconds;
                        _log.Write("mgr TRADE_SWEEP_DEFERRED by=" + deferredMs.ToString("0.0")
                            + "ms -- consider raising TradeIntervalSeconds or FrameSoftBudgetMilliseconds");
                    }
                    _tradeSweepDueUtc = null;
                    break;
                }
                case 5: _storeStockManager.Tick(_states, _world, _tuning, _managerLog); _perfStoreStock.Record(Stopwatch.GetTimestamp() - t0); break;
            }
            FlushManagerLog();
            _managerNextUtc[mostOverdue] = now.AddSeconds(GetManagerInterval(mostOverdue));
            return true;
        }

        /// <summary>Run N combined economy+brain cycles synchronously (operator burst), then persist.</summary>
        public void Step(int n)
        {
            if (_world == null || n <= 0) return;
            _world.TacticLearning = true;
            DateTime now = DateTime.UtcNow;
            double globalPassIntervalSeconds = GetGlobalPassIntervalSeconds();
            _world.UtcNow = _activeUtc = now;
            for (int i = 0; i < n; i++)
            {
                // Global pass: snapshot + economy + decay + diplomacy + lifecycle + arrivals
                _world.PrepareInfluenceSnapshot();
                _world.AccrueIncome(_states);
                LogShipEvacuations();
                foreach (FactionState st in _states)
                {
                    if (FactionLifecycle.IsDormant(st, now)) continue;
                    Personality p;
                    if (!_personalities.TryGetValue(st.PersonalityId ?? "default", out p))
                        p = _personalities["default"];
                    Diplomacy.Drift(st, p, _world, _tuning);
                    Diplomacy.AccrueFatigue(st, _world, _tuning);
                    if (_tuning.MilitaryPostureDecayPerHour > 0 && st.MilitaryPosture > 0)
                        st.MilitaryPosture = Math.Max(0.0, st.MilitaryPosture
                            - _tuning.MilitaryPostureDecayPerHour * (globalPassIntervalSeconds / 3600.0));
                }
                _world.DecayInfluence(_tuning.HoldDecayForIntervalSeconds(globalPassIntervalSeconds),
                    _tuning.PlayerHoldDecayForIntervalSeconds(globalPassIntervalSeconds), _states,
                    globalPassIntervalSeconds);
                _world.DecayThreat(_tuning.ThreatDecayForIntervalSeconds(globalPassIntervalSeconds));
                RenameAbandonedStationsFromDecay();
                ResolveArrivals();
                ApplyDiplomaticEvents();
                FactionLifecycle.Step(_states, _world, _tuning,
                    tag => _log.Write("step CONQUERED " + tag + " -> dormant"));
                _simTick++;
                // Brain tick: all factions in this step
                foreach (FactionState st in _states)
                {
                    if (FactionLifecycle.IsDormant(st, now) || FactionLifecycle.IsAutonomyDisabled(st)) continue;
                    Personality p;
                    if (!_personalities.TryGetValue(st.PersonalityId ?? "default", out p))
                        p = _personalities["default"];
                    var rng = new SeededRandom(unchecked(st.RngSeed + (int)now.Ticks + i));
                    Decision d = _brain.Think(st, p, _world, rng, _tuning);
                    Enact(st, d, rng);
                    if (_tuning.DebugLogBrainDecisions)
                        LogBrainDecision("step", st, d);
                }
            }
            // Reschedule per-faction timers so the first real-time drain doesn't double-fire.
            double factionInterval = GetFactionTickIntervalSeconds();
            foreach (FactionState st in _states) st.NextTickRealUtc = now.AddSeconds(factionInterval);
            _globalPassNextUtc = now.AddSeconds(globalPassIntervalSeconds);
            Flush();
        }

        private void LogBrainDecision(string prefix, FactionState st, Decision d)
        {
            if (_tuning.DebugLogBrainDecisions)
            {
                _log.Write(prefix + " " + FactionBrain.FormatDecisionDebug(st, d));
                return;
            }
            if (d.Intent.Kind != ActionKind.Idle && DebugTraces.BrainDecisions)
                _log.Write(prefix + " " + st.Tag + " " + d.Explanation);
        }

        /// <summary>Fast-forward economy+brain by a simulated number of real seconds (operator command;
        /// only valid while paused).  Runs faction ticks proportional to the elapsed time.</summary>
        public bool AdvanceTime(double seconds)
        {
            if (_world == null || Speed > 0 || double.IsNaN(seconds)
                || double.IsInfinity(seconds) || seconds <= 0)
                return false;
            _activeUtc = _activeUtc.AddSeconds(seconds);
            int ticks = Math.Max(1, (int)(seconds / GetFactionTickIntervalSeconds()));
            Step(ticks);   // Step handles global pass + all factions + flush
            return true;
        }

        // ---- Scheduling interval helpers -------------------------------------------------------
        private double GetFactionTickIntervalSeconds() =>
            _tuning.FactionTickIntervalSeconds > 0 ? _tuning.FactionTickIntervalSeconds : DefaultFactionTickIntervalSeconds;
        private double GetArrivalsIntervalSeconds() =>
            _tuning.ArrivalsIntervalSeconds > 0 ? _tuning.ArrivalsIntervalSeconds : DefaultArrivalsIntervalSeconds;

        private double GetGlobalPassIntervalSeconds() =>
            _tuning.GlobalPassIntervalSeconds > 0 ? _tuning.GlobalPassIntervalSeconds : DefaultGlobalPassIntervalSeconds;
        private double GetFlushEveryRealSeconds() =>
            _tuning.FlushEveryRealSeconds > 0 ? _tuning.FlushEveryRealSeconds : DefaultFlushEveryRealSeconds;
        private double GetPerfWindowSeconds() =>
            _tuning.PerfWindowSeconds > 0 ? _tuning.PerfWindowSeconds : DefaultPerfWindowSeconds;
        internal double GetFrameSoftBudgetMilliseconds() =>
            _tuning.FrameSoftBudgetMilliseconds > 0 ? _tuning.FrameSoftBudgetMilliseconds : DefaultFrameSoftBudgetMilliseconds;

        private double GetManagerInterval(int index)
        {
            switch (index)
            {
                case 0: return _tuning.ProductionIntervalSecondsOrDefault();
                case 1: return _tuning.OpsIntervalSeconds        > 0 ? _tuning.OpsIntervalSeconds        : DefaultOpsIntervalSeconds;
                case 2: return _tuning.ShipyardIntervalSeconds   > 0 ? _tuning.ShipyardIntervalSeconds   : DefaultShipyardIntervalSeconds;
                case 3: return _tuning.FleetIntervalSeconds      > 0 ? _tuning.FleetIntervalSeconds      : DefaultFleetIntervalSeconds;
                case 4: return _tuning.TradeIntervalSeconds      > 0 ? _tuning.TradeIntervalSeconds      : DefaultTradeIntervalSeconds;
                default: return _tuning.StoreStockIntervalSeconds > 0 ? _tuning.StoreStockIntervalSeconds : DefaultShipyardIntervalSeconds;
            }
        }

        private void ResolveArrivals()
        {
            ApplyMunitionReadings();
            SettleResolvingHulls();
            SnapshotLiveHulls();
            // Keep the pre-resolution ownership only for Conquer fleets. That lets the host rename
            // exactly the stations captured by this resolution pass, never stations merely present
            // when the plugin starts or on unrelated ownership changes.
            var conquerTargets = new List<Tuple<string, string, string>>();
            foreach (FactionState state in _states)
            {
                foreach (PendingFleetAction fleet in state.PendingFleets)
                {
                    if (fleet.Kind != ActionKind.Conquer || fleet.TargetNodeId == null) continue;
                    SpatialNode target;
                    if (_world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out target) && target.OwnerFactionTag != null)
                        conquerTargets.Add(Tuple.Create(fleet.TargetNodeId, state.Tag, target.OwnerFactionTag));
                }
            }

            var buf = new List<string>();
            // _simTick advances once per completed global pass, which arrivals no longer rides on, so
            // seeding from it alone would hand consecutive resolutions the identical stream.
            var rng = new SeededRandom(unchecked((int)(_simTick * 1103515245L + _arrivalsRunCount * 2654435761L)));
            _departSlots.Clear();
            _world.FleetTrespassOn = FeatureSwitch.IsOn(FeatureSwitch.FleetTrespass);
            _world.TransitStates = _states;
            PendingFleetResolver.ResolveArrivals(_states, _world, _tuning, rng, buf, _economy,
                eid =>
                {
                    // Despawn the physical grid if the spawner holds one.
                    // Called just before the PendingFleetAction is removed from PendingFleets,
                    // so this runs before UpdateObservation would lose track of it.
                    // The removal loop in Core writes nothing, so without this line a grid that
                    // failed to close here left no trace at all (FAC-057's least observable site).
                    if (_spawner == null) return;
                    // A surviving hull of an attack its meeting stopped flies out before it closes.
                    if (DepartBeforeClose(eid, DateTime.UtcNow, buf)) return;
                    int expiredClosed = _spawner.Despawn(eid);
                    buf.Add("FLEET_EXPIRED_DESPAWN eid=" + eid + " closed=" + expiredClosed);
                }, _itemPrices, _itemVolumes, _oracle, _tradeManager?.InboundBudget,
                onFleetRemoved: OnFleetResolved, itemMasses: _itemMasses);

            PublishCombatOutcomes();
            PublishTrafficEvents();
            ApplyDuelDamageToLiveGrids(buf);

            bool renamedCapture = false;
            foreach (Tuple<string, string, string> conquer in conquerTargets)
            {
                SpatialNode captured;
                if (!_world.Graph.Nodes.TryGetValue(conquer.Item1, out captured)) continue;
                if (!string.Equals(captured.OwnerFactionTag, conquer.Item2, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(conquer.Item3, conquer.Item2, StringComparison.OrdinalIgnoreCase)) continue;

                FactionState newOwner = _states.FirstOrDefault(s => string.Equals(s.Tag, conquer.Item2, StringComparison.OrdinalIgnoreCase));
                if (newOwner != null && _stationFounder.RenameConqueredStation(captured, newOwner))
                {
                    renamedCapture = true;
                    buf.Add("STATION_RENAMED " + conquer.Item1 + " -> " + captured.DisplayName);
                }
            }
            if (renamedCapture) FlushOrDefer("capture renames");

            foreach (string msg in buf) _log.Write("t" + _simTick + " " + msg);
            buf.Clear();
        }

        /// <summary>Renames a founded station's node for its new owner via the same
        /// <see cref="StationFounder.RenameConqueredStation"/> the Conquer path below uses.
        /// No-op for unfounded nodes (StableStationId == 0) or an unresolved owner. <paramref name="verb"/>
        /// is the log tag (e.g. "STATION_ABANDONED", "STATION_CLAIMED").</summary>
        /// <summary>Live-roster lookup by tag, or null if not registered.</summary>
        public FactionState FindFaction(string tag)
        {
            return _states.FirstOrDefault(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Idempotently registers a Magnate-tier player faction (live roster + index + brain
        /// file) the first time it accepts a claim offer — no restart required to participate in
        /// income/diplomacy/persistence from that point on. No-op (returns the existing state) if
        /// already registered. See docs/economy-territory.md -> "Player-faction claiming".</summary>
        public FactionState EnsurePlayerFactionRegistered(string tag, long seFactionId)
        {
            FactionState existing = _states.FirstOrDefault(
                s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var state = new FactionState
            {
                Tag = tag,
                FactionId = seFactionId,
                IsPlayerFaction = true,
                // Left at 0 every faction registered this way shares one RNG stream with every other.
                RngSeed = FactionLifecycle.DeriveRngSeed(seFactionId),
            };
            // No seed treasury, deliberately: the NPC sweep stamps SeedTreasury here and a human gets
            // no free capital (operator, 2026-08-30). Stated because the omission otherwise reads as
            // the same oversight as the seed above, two lines away, and invites being "fixed".
            // The consequence is real -- a player faction starts InsolvencyFloor from collapse with
            // four managers already spending -- and is why FactionLifecycle.Step exempts it.
            _states.Add(state);

            // The ledger starts from what the faction's SE account already holds: members may have
            // deposited before the faction owned anything, and a deposit to a faction the plugin does
            // not track yet is not captured (FactionWalletTransferPatch only credits a registered
            // tag). Starting at 0 would let the mirror's next push take that money away.
            long wallet = 0L;
            if (_economy != null && seFactionId != 0 && _economy.TryGetFactionBalance(seFactionId, out wallet) && wallet > 0)
                _world.SetTreasury(tag, wallet);
            else wallet = 0L;

            BrainDoc brain = FactionStore.ToBrainDoc(state, Enumerable.Empty<SpatialNode>(), FactionStore.DefaultLogCap);
            brain.Treasury = wallet;
            _store.SaveBrain(brain);
            List<IndexEntry> index = _store.LoadIndex();
            index.Add(FactionStore.ToIndexEntry(state, _world.UtcNow));
            _store.SaveIndex(index);
            _log.Write("PLAYER_FACTION_REGISTERED " + tag + " treasury=" + wallet);

            return state;
        }

        /// <summary>Claims an unclaimed/derelict node for a player faction — same ownership transfer
        /// and station-rename side effect as the brain's own Claim action (<see cref="Enact"/>'s
        /// ActionKind.Claim case), callable directly from the claim-offer flow rather than through a
        /// brain Decision. Rename only fires if the node already carries a founded station (the
        /// derelict fast-path); a virgin, never-founded node has nothing to rename.</summary>
        public void ClaimNodeForPlayerFaction(FactionState newOwner, string nodeId)
        {
            if (_world == null || newOwner == null || string.IsNullOrEmpty(nodeId)) return;
            // byPresence: the offer this answers already required the player to stand on the node,
            // so it answers a besieger's claim reservation instead of waiting it out. The rename
            // only runs once the transfer has -- an SE-side rename on a node that did not change
            // hands leaves the station themed for a faction that does not own it.
            string priorOwner = PriorOwnerOf(nodeId);
            if (_world.TryClaim(newOwner.Tag, nodeId, true))
            {
                LogNodeClaimed(newOwner.Tag, nodeId, priorOwner);
                RenameStationForNewOwner(nodeId, newOwner, "STATION_CLAIMED");
            }
        }

        private string PriorOwnerOf(string nodeId)
        {
            SpatialNode node;
            return nodeId != null && _world.Graph.Nodes.TryGetValue(nodeId, out node) ? node.OwnerFactionTag : null;
        }

        /// <summary>Writes <c>NODE_CLAIMED</c> for a claim that went through: who took the node, from
        /// whom (<c>UNKN</c> for a derelict, <c>none</c> for a node nobody held) and what it inherited
        /// (<see cref="NodeHoldings"/>) -- the hulls, repair queue, build queue and inventory a derelict
        /// hands to whoever claims it.</summary>
        private void LogNodeClaimed(string tag, string nodeId, string priorOwner)
        {
            SpatialNode node;
            if (nodeId == null || !_world.Graph.Nodes.TryGetValue(nodeId, out node)) return;
            _log.Write("t" + _simTick + " NODE_CLAIMED " + tag + " node=" + nodeId + " from=" + (priorOwner ?? "none")
                       + NodeHoldings.Of(node).Suffix());
        }

        /// <summary>Takes an owned node from its faction for <paramref name="newOwner"/> after a
        /// player siege, and reports whether it actually changed hands.
        ///
        /// <para>Separate from <see cref="ClaimNodeForPlayerFaction"/> because the two have opposite
        /// preconditions: that one claims a node nobody holds, so it goes through
        /// <see cref="WorldModel.Claim"/> and its claimable-owner guard; this one takes a node a live
        /// faction does hold, which that guard exists to refuse.</para>
        ///
        /// <para>The station rename only runs once the transfer has: the SE-side registration moving
        /// while the model owner does not leaves the station enumerable under a faction that does not
        /// own it and findable by none that do, which no later lookup can recover from.</para></summary>
        public bool CaptureNodeForPlayerFaction(FactionState newOwner, string nodeId)
        {
            if (_world == null || newOwner == null || string.IsNullOrEmpty(nodeId)) return false;
            if (_world.CaptureNode(newOwner.Tag, nodeId) == null) return false;
            RenameStationForNewOwner(nodeId, newOwner, "STATION_CONQUERED");
            return true;
        }

        /// <summary>Bridge verb <c>factions givenode &lt;tag&gt; &lt;nodeId&gt;</c>: gives a node to a
        /// faction on the operator's instruction (<see cref="WorldModel.RestoreNode"/>), moving the SE
        /// station registration and name with it. The repair for ground lost to a defect, as EXP's
        /// nodes were to FAC-502. A node the sweep has deleted cannot be given back: there is nothing
        /// left in the graph to give.</summary>
        public string AdminGiveNode(string tag, string nodeId)
        {
            if (_world == null) return "givenode: no session loaded.";
            FactionState newOwner = FindFaction(tag);
            if (newOwner == null) return "givenode: no faction '" + tag + "'.";
            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !_world.Graph.Nodes.TryGetValue(nodeId, out node))
                return "givenode: no node '" + nodeId + "' (an expired node is gone from the graph).";
            if (string.Equals(node.OwnerFactionTag, newOwner.Tag, StringComparison.Ordinal))
                return "givenode: " + nodeId + " is already " + newOwner.Tag + "'s.";

            FactionState priorState = FindFaction(node.OwnerFactionTag);
            string prior = _world.RestoreNode(newOwner.Tag, nodeId, priorState);
            if (prior == null) return "givenode: " + nodeId + " did not change hands.";

            _log.Write("t" + _simTick + " NODE_GIVEN " + newOwner.Tag + " node=" + nodeId + " from=" + prior
                       + NodeHoldings.Of(node).Suffix());
            RenameStationForNewOwner(nodeId, newOwner, "STATION_GIVEN");
            LogShipEvacuations();
            FlushOrDefer("givenode " + nodeId);
            return "givenode: " + nodeId + " -> " + newOwner.Tag + " (from " + prior + "), hold 1.00, station "
                   + (node.StableStationId != 0 ? "'" + node.DisplayName + "'" : "none") + ".";
        }

        /// <summary>Bridge verb <c>factions removenode &lt;nodeId&gt;</c>: deletes a node whoever owns
        /// it, by the expiry sweep's own steps (<see cref="PruneExpiredUnknTerritory"/>) — the station's
        /// grid, safe zone, rock and SE record go (<see cref="StationMaterializer.PermanentlyRemove"/>),
        /// then the node (<see cref="WorldModel.RetireNode"/>), after its owner's parked hulls have
        /// flown home. Inventory on the node is lost with it. The operator's repair for a node placed
        /// where it should not be, as MEMN's and METC's were on ground FAC-502 had emptied.</summary>
        public string AdminRemoveNode(string nodeId)
        {
            if (_world == null) return "removenode: no session loaded.";
            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !_world.Graph.Nodes.TryGetValue(nodeId, out node))
                return "removenode: no node '" + nodeId + "'.";

            string owner = node.OwnerFactionTag;
            string name = node.DisplayName;
            bool founded = node.StableStationId != 0;
            if (founded)
                _stationMaterializer.PermanentlyRemove(nodeId, node.StableStationId, owner ?? NeutralFaction.Tag);
            RemovedNodeContents destroyed = _world.RetireNode(nodeId, FindFaction(owner));
            _log.Write("t" + _simTick + " NODE_REMOVED node=" + nodeId + " owner=" + (owner ?? "-")
                       + " founded=" + founded + " hulls=" + destroyed.Hulls + " items=" + destroyed.ItemUnits
                       + " damaged=" + destroyed.DamagedHulls);
            LogShipEvacuations();
            Flush();
            return "removenode: " + nodeId + " (" + (owner ?? "unowned")
                   + (founded ? ", station '" + name + "'" : "") + ") removed; " + destroyed.ItemUnits
                   + " item unit(s) went with it, " + destroyed.Hulls + " hull(s) could not fly home.";
        }

        /// <summary>Bridge verb <c>factions recreatenode &lt;tag&gt; &lt;nodeId&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; [found]</c>:
        /// brings a node the <c>UNKN</c> sweep deleted back under its old id at <paramref name="posKm"/>
        /// (<see cref="WorldModel.RecreateNodeAt"/>), and with <paramref name="found"/> stands a new
        /// standard station on it free of charge. The old station is not recoverable — the sweep
        /// closed its grid and purged its SE record — so a founded node gets a fresh one.</summary>
        public string AdminRecreateNode(string tag, string nodeId, Vec3 posKm, bool found)
        {
            if (_world == null) return "recreatenode: no session loaded.";
            FactionState owner = FindFaction(tag);
            if (owner == null) return "recreatenode: no faction '" + tag + "'.";
            SpatialNode node = _world.RecreateNodeAt(owner.Tag, nodeId, posKm);
            if (node == null) return "recreatenode: " + nodeId + " is still in the graph; use givenode.";

            IList<NodeSeparationViolation> violations = _world.SeparationViolations;
            for (int i = 0; i < violations.Count; i++)
                _log.Write("t" + _simTick + " NODE_SEPARATION_VIOLATION node=" + violations[i].NewNodeId
                           + " zone=" + violations[i].Zone
                           + " neighbor=" + violations[i].OffendingNeighborId
                           + " actualKm=" + violations[i].ActualDistanceKm.ToString("0.0")
                           + " requiredKm=" + violations[i].RequiredMinSepKm.ToString("0.0"));
            _world.ClearSeparationViolations();

            _log.Write("t" + _simTick + " NODE_RECREATED " + owner.Tag + " node=" + nodeId
                       + " pos=" + node.Position.X.ToString("0.000", CultureInfo.InvariantCulture)
                       + "," + node.Position.Y.ToString("0.000", CultureInfo.InvariantCulture)
                       + "," + node.Position.Z.ToString("0.000", CultureInfo.InvariantCulture)
                       + " zone=" + node.Zone + " planet=" + (node.PlanetId ?? "-")
                       + " ore=" + NodeDisplay.DominantOre(node));

            string station = "none";
            if (found)
            {
                if (_stationFounder.FoundStation(nodeId, owner, _world.Graph, false))
                {
                    station = "'" + node.DisplayName + "'";
                    _log.Write("STATION_FOUNDED_BY_ADMIN " + owner.Tag + " " + nodeId + " style=standard");
                }
                else station = "founding failed";
            }
            Flush();
            return "recreatenode: " + nodeId + " -> " + owner.Tag + " at " + node.Zone
                   + (node.PlanetId != null ? " on " + node.PlanetId : "") + ", hold 1.00, station " + station + ".";
        }

        /// <summary>True for a tag registered via <see cref="EnsurePlayerFactionRegistered"/>. The
        /// single lookup wired into both <see cref="StationFounder.Configure"/> and
        /// <see cref="StationMaterializer.IsPlayerFactionLookup"/> so a Magnate-tier player faction's
        /// station can bypass their otherwise-NPC-only `IsEffectivelyNpc()` guards — see
        /// docs/economy-territory.md -> "Player-faction claiming" -> Founding.</summary>
        public bool IsRegisteredPlayerFaction(string tag)
        {
            FactionState s = FindFaction(tag);
            return s != null && s.IsPlayerFaction;
        }

        /// <summary>Whether <paramref name="tag"/>'s stations materialise inside a protective safe
        /// zone — see <see cref="StationSafeZonePolicy"/>. An unregistered tag keeps its zone: the
        /// policy can only ever remove protection where a known personality asks for it.</summary>
        internal bool SpawnsSafeZoneFor(string tag)
        {
            FactionState s = FindFaction(tag);
            return s == null || StationSafeZonePolicy.SpawnsSafeZone(s.PersonalityId);
        }

        /// <summary>The prefab livery to query the catalogue with for <paramref name="tag"/> — SE's
        /// faction type where it has one, and the faction's personality where it does not. See
        /// <see cref="PrefabFactionHint"/> for why the two are asked separately.</summary>
        internal string PrefabHintFor(IFactionTypeCatalogue types, string tag)
        {
            string seType = null;
            if (types != null) types.TryGetFactionType(tag, out seType);
            FactionState s = FindFaction(tag);
            return PrefabFactionHint.Resolve(seType, s != null ? s.PersonalityId : null);
        }

        /// <summary>Founds a real station for a Magnate-tier player faction on the node itself, in the
        /// player-chosen style — the fourth verb of the player station-management surface (see
        /// <see cref="Commands.PlayerStationCommands"/>) and what the station window's Found button
        /// asks for. Validation is delegated to <see cref="FoundStationValidator"/> so the chat verb,
        /// the client's own preview and this all refuse for the same reasons and quote the same
        /// price. Returns a chat-ready result message.
        ///
        /// <para>Placement is identical to the NPC path; only the style override is new, and
        /// <see cref="Enact"/> never sets it. The charge lands after the station does, because
        /// <see cref="StationFounder.FoundStation"/> is the step that can still fail on SE's side —
        /// spending first would take the money for a station that was never built.</para></summary>
        public string FoundStationForPlayer(FactionState owner, string nodeId, bool isPirateStyle)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            long cost, have;
            FoundStationResult result = FoundStationValidator.Validate(
                _world, _tuning, owner.Tag, nodeId, out cost, out have);
            switch (result)
            {
                case FoundStationResult.NodeNotFound: return "No node '" + nodeId + "'.";
                case FoundStationResult.NotOwned: return nodeId + " isn't owned by your faction.";
                case FoundStationResult.AlreadyFounded: return nodeId + " already has a station.";
                case FoundStationResult.InsufficientTreasury:
                    return "Founding " + nodeId + " costs " + cost + " credits — ["
                        + owner.Tag + "] has " + have + ".";
            }

            if (!_stationFounder.FoundStation(nodeId, owner, _world.Graph, isPirateStyle))
                return "Something went wrong founding " + nodeId + ". Try again shortly.";

            _world.Spend(owner.Tag, cost);
            Flush();
            _log.Write("STATION_FOUNDED_BY_PLAYER " + owner.Tag + " " + nodeId
                + " style=" + (isPirateStyle ? "fortified" : "standard") + " cost=" + cost);
            return nodeId + " is now a real station (" + cost
                + " credits spent). It'll appear once someone gets close.";
        }

        /// <summary>Charges every node's workers their food and air for the time since the last pass
        /// (<see cref="WorkerUpkeep"/>). Runs inside the global pass on its own clock per node, so the
        /// pass's cadence sets how often stock moves, not how much is eaten. Only a departure is
        /// logged; eating is visible in the node's stock. FAC-493.</summary>
        private void RunWorkerUpkeep(DateTime now)
        {
            WorkerUpkeep.RunAll(_world.Graph.Nodes.Values, _tuning, _world.Nutrition, _world.Planets, now, (node, r) =>
            {
                if (r.Left <= 0) return;
                _log.Write("WORKERS_LEFT " + (node.OwnerFactionTag ?? "-") + " node=" + node.Id
                    + " left=" + r.Left.ToString("0.##", CultureInfo.InvariantCulture)
                    + " remaining=" + WorkerUpkeep.Headcount(node)
                    + " fed=" + r.FedShare.ToString("0.##", CultureInfo.InvariantCulture)
                    + " nutrition=" + r.NutritionEaten.ToString("0.##", CultureInfo.InvariantCulture)
                    + "/" + r.NutritionOwed.ToString("0.##", CultureInfo.InvariantCulture)
                    + " oxygen=" + r.OxygenDrawn + "/" + Math.Ceiling(r.OxygenOwed).ToString(CultureInfo.InvariantCulture)
                    + " beds=" + WorkerUpkeep.Capacity(node, _tuning));
            });
        }

        /// <summary>Buys workers out of a civilian pool for a player's Habitat — what the station
        /// window's Recruit button asks for. <see cref="WorkerRecruitValidator"/> decides, so the window's
        /// price and this reply never disagree; a request for more than the beds left or the treasury
        /// buys is trimmed to what fits. Returns a chat-ready result message. FAC-493.</summary>
        public string RecruitWorkersForPlayer(FactionState owner, string nodeId, int count)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            int granted;
            long cost, have;
            WorkerRecruitResult result = WorkerRecruitValidator.Validate(
                _world, _tuning, owner.Tag, nodeId, count, out granted, out cost, out have);
            switch (result)
            {
                case WorkerRecruitResult.NodeNotFound: return "No node '" + nodeId + "'.";
                case WorkerRecruitResult.NotOwned: return nodeId + " isn't owned by your faction.";
                case WorkerRecruitResult.NoHabitat: return nodeId + " has no Habitat to house workers.";
                case WorkerRecruitResult.NoCivilianPool:
                    return "There is nobody to recruit near " + nodeId + ". Workers are recruited on Tellus.";
                case WorkerRecruitResult.NoRoom: return nodeId + "'s Habitat is full.";
                case WorkerRecruitResult.InsufficientTreasury:
                    return "A recruit costs " + cost + " credits — [" + owner.Tag + "] has " + have + ".";
            }

            SpatialNode node = _world.Graph.Nodes[nodeId];
            WorkerRecruitValidator.Admit(node, granted, _world.UtcNow);
            _world.Spend(owner.Tag, cost);
            Flush();
            _log.Write("WORKERS_RECRUITED_BY_PLAYER " + owner.Tag + " node=" + nodeId + " count=" + granted
                + " asked=" + count + " cost=" + cost + " workers=" + WorkerUpkeep.Headcount(node)
                + " beds=" + WorkerUpkeep.Capacity(node, _tuning));
            return granted + (granted == 1 ? " worker" : " workers") + " recruited at " + nodeId + " ("
                + cost + " credits spent)" + (granted < count ? "; that is all that fits." : ".");
        }

        /// <summary>Player-driven rename of the caller's own already-founded station — the write
        /// half of the chat-command station-management surface (see
        /// <see cref="Commands.PlayerStationCommands"/>). Unlike
        /// <see cref="StationFounder.RenameConqueredStation"/> (auto-picks a catalogue name on
        /// ownership transfer), this applies the player's own text verbatim via the same
        /// <see cref="StationFounder.ApplyPersistedDisplayName"/> push used to reconcile a name
        /// after the offline migration. Returns a chat-ready result message.</summary>
        public string RenameStationForPlayer(FactionState owner, string nodeId, string newName)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(newName)) return "Give the station a name.";

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !_world.Graph.Nodes.TryGetValue(nodeId, out node))
                return "No node '" + nodeId + "'.";
            if (!string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
                return nodeId + " isn't owned by your faction.";
            if (node.StableStationId == 0)
                return nodeId + " has no founded station yet.";

            node.DisplayName = newName;
            _stationFounder.ApplyPersistedDisplayName(node);
            Flush();
            _log.Write("STATION_RENAMED_BY_PLAYER " + owner.Tag + " " + nodeId + " -> " + newName);
            return nodeId + " is now called \"" + newName + "\".";
        }

        /// <summary>Player-driven capability install on the caller's own already-founded station —
        /// the third verb of the chat-command station-management surface (see
        /// <see cref="Commands.PlayerStationCommands"/>). Validation is delegated to
        /// <see cref="DevelopStationValidator"/> so the gate (ownership, founded, prerequisite
        /// cascade, treasury, item stock) is identical to what <see cref="Enact"/> already requires
        /// of a brain-driven Develop — a player-triggered install is never cheaper than the same
        /// install would be for an NPC faction. Returns a chat-ready result message.</summary>
        public string DevelopStationForPlayer(FactionState owner, string nodeId, NodeCapabilityKind capability)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            NodeCapabilityKind? missingPrereq;
            long cost;
            string missingItemKey;
            long missingItemHave, missingItemNeed;
            DevelopStationResult result = DevelopStationValidator.Validate(
                _world, _tuning, owner.Tag, nodeId, capability, out missingPrereq, out cost, out missingItemKey,
                out missingItemHave, out missingItemNeed);
            switch (result)
            {
                case DevelopStationResult.NotPlayerDevelopable:
                    return capability + " is not something a faction can install — it belongs to the Factorum.";
                case DevelopStationResult.NodeNotFound: return "No node '" + nodeId + "'.";
                case DevelopStationResult.NotOwned: return nodeId + " isn't owned by your faction.";
                case DevelopStationResult.NotFounded: return nodeId + " has no founded station yet.";
                case DevelopStationResult.AlreadyInstalled:
                    return nodeId + " already has " + capability + " at its max level ("
                        + _tuning.MaxLevelFor(capability) + ").";
                case DevelopStationResult.PrerequisiteMissing:
                    return "Requires " + missingPrereq + " installed somewhere in your faction's network first.";
                case DevelopStationResult.InsufficientTreasury:
                    return capability + " costs " + cost + " credits — [" + owner.Tag + "] has " + _world.GetTreasury(owner.Tag) + ".";
                case DevelopStationResult.InsufficientItemStock:
                    return capability + " needs more " + missingItemKey + " — have " + missingItemHave
                        + ", need " + missingItemNeed + ".";
            }

            _world.Develop(owner.Tag, nodeId, capability);
            _world.Spend(owner.Tag, cost);
            _oracle?.InvalidateNode(nodeId);   // parity with Enact's Develop case -- capability set changed
            Flush();

            int newLevel = 0;
            SpatialNode node;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out node)) node.Capabilities.TryGetValue(capability, out newLevel);
            _log.Write("STATION_DEVELOPED_BY_PLAYER " + owner.Tag + " " + nodeId + " cap=" + capability
                + " level=" + newLevel + " cost=" + cost);
            return newLevel > 1
                ? nodeId + " now has " + capability + " at level " + newLevel + " (" + cost + " credits spent)."
                : nodeId + " now has " + capability + " installed (" + cost + " credits spent).";
        }

        /// <summary>Operator-driven capability grant — the admin form of `develop`, reached from
        /// chat as `!factions station develop &lt;nodeId&gt; &lt;capability&gt; &lt;level&gt;` and from the
        /// file bridge as `factions develop &lt;nodeId&gt; &lt;capability&gt; &lt;level&gt;`.
        ///
        /// <para>It is an overload of the player's own <see cref="DevelopStationForPlayer"/>, not an
        /// entry point into it: a grant names a level outright and charges nothing, where the player
        /// form raises the level by one and pays the prerequisite cascade, the treasury and the
        /// component BOM for it. The policy it does keep lives in
        /// <see cref="CapabilityGrantValidator"/> — a real faction holds the node, the node has a
        /// founded station, and the level is clamped to
        /// <see cref="FactionTuning.MaxLevelFor"/>. Level 0 removes the capability. The owning
        /// faction is read off the node: there is no caller faction to check it against.</para></summary>
        public string GrantCapabilityAdmin(string nodeId, NodeCapabilityKind capability, int level)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(nodeId)) return "Usage: develop <nodeId> <capability> <level>";

            string ownerTag;
            int currentLevel, grantedLevel, maxLevel;
            CapabilityGrantResult result = CapabilityGrantValidator.Validate(
                _world, _tuning, nodeId, capability, level,
                out ownerTag, out currentLevel, out grantedLevel, out maxLevel);
            switch (result)
            {
                case CapabilityGrantResult.NodeNotFound: return "No node '" + nodeId + "'.";
                case CapabilityGrantResult.Unowned:      return nodeId + " isn't held by any faction.";
                case CapabilityGrantResult.NotFounded:   return nodeId + " has no founded station yet.";
            }
            if (FindState(ownerTag) == null)
                return nodeId + " is held by '" + ownerTag + "', which isn't registered with Factions.";

            string clampNote = level > maxLevel
                ? " (" + level + " clamped to the " + capability + " cap of " + maxLevel + ")"
                : "";
            if (grantedLevel == currentLevel)
                return "[" + ownerTag + "] " + nodeId + " already has " + capability
                       + (currentLevel == 0 ? " uninstalled" : " at level " + currentLevel)
                       + " — nothing changed" + clampNote + ".";

            _world.SetCapabilityLevel(ownerTag, nodeId, capability, grantedLevel);
            _oracle?.InvalidateNode(nodeId);   // parity with Enact's Develop case -- capability set changed
            Flush();

            // `requested=` only appears when the clamp moved the number. The chat/bridge reply says
            // so too, but that reply is read once and the day log is the durable record -- without
            // this a clamped grant reads identically to one that asked for exactly what it got.
            _log.Write("STATION_CAPABILITY_SET_BY_ADMIN " + ownerTag + " " + nodeId + " cap=" + capability
                + " level=" + grantedLevel + " was=" + currentLevel
                + (grantedLevel != level ? " requested=" + level : ""));
            return grantedLevel == 0
                ? "[" + ownerTag + "] " + nodeId + " no longer has " + capability
                  + " (was level " + currentLevel + ")."
                : "[" + ownerTag + "] " + nodeId + " now has " + capability + " at level " + grantedLevel
                  + " (was " + currentLevel + ")" + clampNote + ".";
        }

        /// <summary>Where a node would land if this player created one where they are standing, and
        /// what it would cost them — decided without charging or creating anything, so the same call
        /// serves the in-flight readout and the chat verb's own preview
        /// (docs/economy-territory.md → Expanding). Null when the runtime has no world yet.</summary>
        public PlayerExpandQuote QuoteExpandForPlayer(FactionState owner, string factionTag,
                                                      long playerIdentityId, long factionIdentityId,
                                                      Vec3 posKm)
        {
            if (_world == null) return null;
            return NewExpandService().Quote(
                ExpandRequest(owner, factionTag, playerIdentityId, factionIdentityId, posKm));
        }

        /// <summary>
        /// Create a node for a player faction where the player is standing — the player's own Expand
        /// (FAC-197). The charge is the gate and it goes to an SE wallet, never the private ledger: a
        /// player faction is registered with no seed treasury, so a ledger-priced Expand would be a
        /// loop with no entrance.
        ///
        /// <para>Registration is deferred into the transaction rather than done up front, because a
        /// faction that owns nothing has never been registered — the roster entry is written on the
        /// first claim — and a refused charge must not leave one behind as its only trace.</para>
        /// </summary>
        public PlayerExpandResult ExpandForPlayer(FactionState owner, string factionTag,
                                                  long playerIdentityId, long factionIdentityId,
                                                  Vec3 posKm)
        {
            if (_world == null) return null;

            PlayerExpandResult result = NewExpandService().Commit(
                ExpandRequest(owner, factionTag, playerIdentityId, factionIdentityId, posKm),
                () => EnsurePlayerFactionRegistered(factionTag, factionIdentityId));
            // A new node lives only in the graph until written, and the flag that decides which wallet
            // the NEXT one is charged to lives on the brain doc -- the same reason the estate seeder
            // flushes as soon as it applies.
            if (result != null && result.Ok) Flush();
            return result;
        }

        private PlayerExpandService NewExpandService()
        {
            return new PlayerExpandService(_world, _economy, msg => _log.Write(msg));
        }

        private static PlayerExpandRequest ExpandRequest(FactionState owner, string factionTag,
                                                         long playerIdentityId, long factionIdentityId,
                                                         Vec3 posKm)
        {
            return new PlayerExpandRequest
            {
                FactionTag = factionTag,
                Owner = owner,
                PlayerIdentityId = playerIdentityId,
                FactionIdentityId = factionIdentityId,
                PositionKm = posKm,
            };
        }

        /// <summary>Player-driven toggle for <see cref="FactionState.ReservationOptIn"/> — the write
        /// half of the `!factions station develop &lt;capability&gt; reserve` chat command (see
        /// <see cref="Commands.PlayerStationCommands"/>). Flips whether ProductionManager may post
        /// InvestmentRequests/reserve real components toward <paramref name="capability"/> on this
        /// faction's behalf (see <see cref="FactionLifecycle.CanPostInvestmentFor"/>) — found live
        /// 2026-08-16 that without this opt-in, components got silently locked toward capabilities the
        /// player never chose to install. Enabling now also keeps a standing low-urgency
        /// InvestmentRequest alive for the capability (see
        /// <c>ProductionManager.PostReservationKeepAlive</c>) so staging starts even with no unmet
        /// demand or throughput signal to trigger it reactively — found live 2026-08-18 that the
        /// toggle alone did nothing until an unrelated reactive trigger happened to fire. Returns a
        /// chat-ready result message reporting the new state.</summary>
        /// <summary>Player-driven write for <see cref="FactionState.ReinforceOptIn"/> — which of the
        /// faction's nodes `FleetManager` keeps at hold while nobody is online, and in what order.
        ///
        /// <para><paramref name="rank"/> null toggles the node off the list. Nothing else can add to
        /// it: an NPC's ranking is its brain's, and this one is durable precisely because a player's
        /// is not recomputed from anything.</para></summary>
        public string SetReinforceOptInForPlayer(FactionState owner, string nodeId, int? rank)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(nodeId)) return "Name the node to hold.";

            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node))
                return "No node '" + nodeId + "'.";
            if (node.OwnerFactionTag != owner.Tag)
                return nodeId + " is not yours to hold — [" + owner.Tag + "] does not own it.";

            if (!rank.HasValue)
            {
                if (!owner.ReinforceOptIn.Remove(nodeId))
                    return nodeId + " was not being held.";
                Flush();
                _log.Write("REINFORCE_OPTIN_BY_PLAYER " + owner.Tag + " node=" + nodeId + " off");
                return nodeId + " will no longer be reinforced. Its hold will decay from here.";
            }

            owner.ReinforceOptIn[nodeId] = rank.Value;
            Flush();
            _log.Write("REINFORCE_OPTIN_BY_PLAYER " + owner.Tag + " node=" + nodeId + " rank=" + rank.Value);
            return nodeId + " will be kept at hold, rank " + rank.Value
                 + ". Costs " + _tuning.ReinforceCost + " credits per top-up, from the faction treasury.";
        }

        /// <summary>Sets one of the faction's three reinforce spend knobs, or clears it back to the
        /// world's tuning when <paramref name="value"/> is null.
        ///
        /// <para><b>Refuses to leave income fraction and floor both at zero.</b>
        /// <see cref="SpendBudget.CanSpend"/> reads that pair as <i>uncapped</i>, so a player setting
        /// them to zero to stop spending would get the opposite — unlimited upkeep out of their own
        /// treasury. Stopping upkeep is what <c>hold &lt;node&gt; off</c> is for.</para></summary>
        public string SetReinforceBudgetForPlayer(FactionState owner, string knob, double? value)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (value.HasValue && value.Value < 0) return "A budget share cannot be negative.";

            string k = (knob ?? "").ToLowerInvariant();
            double? income = owner.ReinforceIncomeFractionOverride;
            long? floor = owner.ReinforceBudgetFloorOverride;
            double? wealth = owner.ReinforceWealthFractionOverride;

            switch (k)
            {
                case "income":   income = value; break;
                case "floor":    floor  = value.HasValue ? (long?)value.Value : null; break;
                case "treasury": wealth = value; break;
                default:
                    return "Unknown knob '" + knob + "'. One of: income (share of recent income), "
                         + "treasury (share of spendable surplus), floor (flat per-window minimum). "
                         + "Add 'default' to clear one.";
            }

            // The pair that means "uncapped", which is never what a player typing 0 wants.
            double effIncome = income.HasValue ? income.Value : _tuning.ReinforceMaxIncomeFractionPerWindow;
            long effFloor = floor.HasValue ? floor.Value : _tuning.ReinforceBudgetFloor;
            if (effIncome <= 0 && effFloor <= 0)
                return "That would leave income and floor both at zero, which the budget reads as "
                     + "UNLIMITED, not as nothing. To stop maintaining a node use "
                     + "!factions station hold <nodeId> off.";

            owner.ReinforceIncomeFractionOverride = income;
            owner.ReinforceBudgetFloorOverride = floor;
            owner.ReinforceWealthFractionOverride = wealth;
            Flush();
            _log.Write("REINFORCE_BUDGET_BY_PLAYER " + owner.Tag + " " + k + "="
                       + (value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "default"));
            return DescribeReinforceBudget(owner);
        }

        /// <summary>The faction's three reinforce spend knobs, each marked as its own or the world's.</summary>
        public string DescribeReinforceBudget(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            var sb = new StringBuilder();
            sb.AppendLine("[" + owner.Tag + "] reinforce budget per "
                          + _tuning.SpendBudgetWindowSeconds.ToString("0") + "s window:");
            sb.AppendLine("  income   " + owner.ReinforceIncomeFraction(_tuning).ToString("0.###", CultureInfo.InvariantCulture)
                          + (owner.ReinforceIncomeFractionOverride.HasValue ? "  (yours)" : "  (world default)"));
            sb.AppendLine("  treasury " + owner.ReinforceWealthFraction(_tuning).ToString("0.###", CultureInfo.InvariantCulture)
                          + (owner.ReinforceWealthFractionOverride.HasValue ? "  (yours)" : "  (world default)"));
            sb.AppendLine("  floor    " + owner.ReinforceBudgetFloorFor(_tuning)
                          + (owner.ReinforceBudgetFloorOverride.HasValue ? "  (yours)" : "  (world default)"));
            sb.AppendLine("  each top-up costs " + _tuning.ReinforceCost + "; your treasury is still the hard limit.");
            return sb.ToString();
        }

        /// <summary>What the faction is currently holding, highest rank first — the read half of
        /// <see cref="SetReinforceOptInForPlayer"/>. Reports a node it no longer owns rather than
        /// hiding it: the entry is kept on purpose so retaking the node resumes upkeep.</summary>
        public string DescribeReinforceOptIn(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (owner.ReinforceOptIn.Count == 0)
                return "[" + owner.Tag + "] is holding nothing. !factions station hold <nodeId> [rank] to start.";

            var rows = new List<KeyValuePair<string, int>>(owner.ReinforceOptIn);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            sb.AppendLine("[" + owner.Tag + "] holding " + rows.Count + " node(s), " + _tuning.ReinforceCost + " per top-up:");
            foreach (KeyValuePair<string, int> kv in rows)
            {
                SpatialNode n;
                bool known = _world.Graph.Nodes.TryGetValue(kv.Key, out n);
                string state = !known ? "unknown node"
                             : n.OwnerFactionTag != owner.Tag ? "NOT YOURS — upkeep paused, resumes if you retake it"
                             : "hold " + n.HoldStrength.ToString("0.00");
                sb.AppendLine("  rank " + kv.Value + "  " + kv.Key + "  (" + state + ")");
            }
            return sb.ToString();
        }

        /// <summary>Switches one direction of automated trade on or off for a player faction.
        ///
        /// <para>Two directions rather than one because they cost different things: selling moves
        /// goods the faction already has, buying spends its treasury. Buying also makes the
        /// faction's shortage visible to every neighbour, since it is the same flag that releases
        /// <c>PublishUnservedDemands</c> -- so switching it on is what lets an NPC convoy come to
        /// you, not only what lets yours go out.</para></summary>
        public string SetTradeOptInForPlayer(FactionState owner, string direction, bool on)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            string d = (direction ?? "").ToLowerInvariant();
            if (d != "buy" && d != "sell" && d != "both")
                return "Which direction? buy (fly out to acquire what you are short of), "
                     + "sell (fly surplus to a partner that wants it), or both.";

            if (d == "buy" || d == "both") owner.TradeBuyOptIn = on;
            if (d == "sell" || d == "both") owner.TradeSellOptIn = on;
            Flush();
            _log.Write("TRADE_OPTIN_BY_PLAYER " + owner.Tag + " " + d + "=" + (on ? "on" : "off")
                       + " buy=" + (owner.TradeBuyOptIn ? "on" : "off")
                       + " sell=" + (owner.TradeSellOptIn ? "on" : "off"));
            return DescribeTradeOptIn(owner);
        }

        /// <summary>Sets one of the faction's three trade spend knobs, or clears it back to the
        /// world's tuning when <paramref name="value"/> is null. The trade twin of
        /// <see cref="SetReinforceBudgetForPlayer"/>, and it refuses the same pair for the same
        /// reason: income fraction and floor both at zero read as UNCAPPED, not as nothing.</summary>
        public string SetTradeBudgetForPlayer(FactionState owner, string knob, double? value)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (value.HasValue && value.Value < 0) return "A budget share cannot be negative.";

            string k = (knob ?? "").ToLowerInvariant();
            double? income = owner.TradeIncomeFractionOverride;
            long? floor = owner.TradeBudgetFloorOverride;
            double? wealth = owner.TradeWealthFractionOverride;

            switch (k)
            {
                case "income":   income = value; break;
                case "floor":    floor  = value.HasValue ? (long?)value.Value : null; break;
                case "treasury": wealth = value; break;
                default:
                    return "Unknown knob '" + knob + "'. One of: income (share of recent income), "
                         + "treasury (share of spendable surplus), floor (flat per-window minimum). "
                         + "Add 'default' to clear one.";
            }

            double effIncome = income.HasValue ? income.Value : _tuning.TradeMaxIncomeFractionPerWindow;
            long effFloor = floor.HasValue ? floor.Value : _tuning.TradeBudgetFloor;
            if (effIncome <= 0 && effFloor <= 0)
                return "That would leave income and floor both at zero, which the budget reads as "
                     + "UNLIMITED, not as nothing. To stop trading use "
                     + "!factions station trade off.";

            owner.TradeIncomeFractionOverride = income;
            owner.TradeBudgetFloorOverride = floor;
            owner.TradeWealthFractionOverride = wealth;
            Flush();
            _log.Write("TRADE_BUDGET_BY_PLAYER " + owner.Tag + " " + k + "="
                       + (value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "default"));
            return DescribeTradeBudget(owner);
        }

        /// <summary>The faction's three trade spend knobs, each marked as its own or the world's.</summary>
        public string DescribeTradeBudget(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            var sb = new StringBuilder();
            sb.AppendLine("[" + owner.Tag + "] trade budget per "
                          + _tuning.SpendBudgetWindowSeconds.ToString("0") + "s window:");
            sb.AppendLine("  income   " + owner.TradeIncomeFraction(_tuning).ToString("0.###", CultureInfo.InvariantCulture)
                          + (owner.TradeIncomeFractionOverride.HasValue ? "  (yours)" : "  (world default)"));
            sb.AppendLine("  treasury " + owner.TradeWealthFraction(_tuning).ToString("0.###", CultureInfo.InvariantCulture)
                          + (owner.TradeWealthFractionOverride.HasValue ? "  (yours)" : "  (world default)"));
            sb.AppendLine("  floor    " + owner.TradeBudgetFloorFor(_tuning)
                          + (owner.TradeBudgetFloorOverride.HasValue ? "  (yours)" : "  (world default)"));
            sb.AppendLine("  each dispatch costs " + _tuning.TradeCost
                          + "; the goods are paid for separately at the stop.");
            return sb.ToString();
        }

        /// <summary>Every player-visible processed good, as inventory keys: the catalogue the Bank
        /// sales list and the chat verb offer. Empty off the live SE registry (the offline one has no
        /// names and no visibility flag).</summary>
        public List<string> BankBuyableKeys()
        {
            var se = _registry as SeItemRegistry;
            return se != null ? se.BankBuyableKeys() : new List<string>();
        }

        public string BankItemName(string inventoryKey)
        {
            var se = _registry as SeItemRegistry;
            return se != null ? se.DisplayNameFor(inventoryKey) : BankSalesReport.SubType(inventoryKey);
        }

        /// <summary>Sets one bank-sale rule on each of <paramref name="keys"/> for a player faction. A
        /// key that is not a processed good is skipped: the bank never buys it, so a rule on it would
        /// be a promise about nothing.</summary>
        public string SetCentralBankRuleForPlayer(FactionState owner, IList<string> keys, BankSaleMode mode, long target)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (keys == null || keys.Count == 0) return "No items named.";
            if (mode == BankSaleMode.AboveTarget && target <= 0)
                return "A target must be at least 1. To sell all oversupply, choose Sell oversupply.";

            var changed = new List<string>();
            int skipped = 0;
            foreach (string key in keys)
            {
                ItemKind kind;
                if (!_world.TryGetInventoryItemKind(key, out kind) || kind != ItemKind.ProcessedGood)
                {
                    skipped++;
                    continue;
                }
                if (owner.SetCentralBankRule(key, mode, target)) changed.Add(key);
            }

            string rule = BankRuleText(mode, target);
            if (changed.Count > 0)
            {
                Flush();
                const int shown = 8;
                string list = string.Join(",", changed.Take(shown))
                              + (changed.Count > shown ? ",+" + (changed.Count - shown) : "");
                _log.Write("BANK_RULE_BY_PLAYER " + owner.Tag + " mode=" + mode
                           + (mode == BankSaleMode.AboveTarget ? " target=" + target : "")
                           + " n=" + changed.Count + " items=" + list);
            }

            string reply = changed.Count == 0 ? "No change."
                : changed.Count == 1 ? BankItemName(changed[0]) + ": " + rule + "."
                : changed.Count + " item(s): " + rule + ".";
            if (skipped > 0) reply += " " + skipped + " not a bank item, ignored.";
            return reply;
        }

        /// <summary>A rule in the words the window and the chat verb use.</summary>
        public static string BankRuleText(BankSaleMode mode, long target)
        {
            switch (mode)
            {
                case BankSaleMode.DontSell: return "don't sell";
                case BankSaleMode.AboveTarget:
                    return "sell oversupply above " + target.ToString("N0", CultureInfo.InvariantCulture);
                default: return "sell oversupply";
            }
        }

        /// <summary>The bare chat readback: every item with a rule other than the default.</summary>
        public string DescribeCentralBankRules(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            var sb = new StringBuilder();
            if (owner.CentralBankRuleCount == 0)
            {
                sb.AppendLine("[" + owner.Tag + "] every item: sell oversupply to the central bank.");
            }
            else
            {
                var lines = new List<string>();
                foreach (string key in owner.CentralBankDontSell)
                    lines.Add(BankItemName(key) + ": " + BankRuleText(BankSaleMode.DontSell, 0));
                foreach (KeyValuePair<string, long> kv in owner.CentralBankTargets)
                    lines.Add(BankItemName(kv.Key) + ": " + BankRuleText(BankSaleMode.AboveTarget, kv.Value));
                lines.Sort(StringComparer.OrdinalIgnoreCase);
                const int shown = 30;
                sb.AppendLine("[" + owner.Tag + "] bank-sale rules (" + lines.Count + "; every other item sells oversupply):");
                foreach (string line in lines.Take(shown)) sb.AppendLine("  " + line);
                if (lines.Count > shown) sb.AppendLine("  +" + (lines.Count - shown) + " more");
            }
            sb.AppendLine("  !factions station banksell sell <item|all>          -- sell oversupply (the default)");
            sb.AppendLine("  !factions station banksell target <item|all> <n>    -- sell oversupply above n faction-wide");
            sb.AppendLine("  !factions station banksell keep <item|all>          -- don't sell");
            sb.Append("  The full list is in the Store window: Assets -> Store -> Bank sales...");
            return sb.ToString();
        }

        /// <summary>Resolves the chat verb's item argument: "all", an inventory key, a subtype or a
        /// display name, case-insensitively. "all" means every listed item, except when resetting to
        /// the default, where it means every item that has a rule. Empty when nothing matches.</summary>
        public List<string> ResolveBankItems(FactionState owner, string arg, BankSaleMode mode)
        {
            var result = new List<string>();
            if (owner == null || string.IsNullOrEmpty(arg)) return result;
            List<BankSalesRow> rows = BankSalesReport.Build(_world, owner, BankBuyableKeys());
            if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (BankSalesRow row in rows)
                    if (mode != BankSaleMode.SellOversupply || row.Mode != BankSaleMode.SellOversupply)
                        result.Add(row.Key);
                return result;
            }
            foreach (BankSalesRow row in rows)
                if (string.Equals(row.Key, arg, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(BankSalesReport.SubType(row.Key), arg, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(BankItemName(row.Key), arg, StringComparison.OrdinalIgnoreCase))
                    result.Add(row.Key);
            return result;
        }

        /// <summary>The read half of <see cref="SetTradeOptInForPlayer"/>: both switches, the partner
        /// list the projection produced, and whether the faction owns a hull that can carry a trade.
        ///
        /// <para>The last two are here because both are otherwise <b>invisible failures</b>. An
        /// opted-in faction that ranks zero partners never emits a <c>TRADE_TICK_PERF</c> line at
        /// all, and a buy pass with no suitable hull returns without incrementing a counter. Either
        /// looks exactly like the feature not being deployed.</para></summary>
        public string DescribeTradeOptIn(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            var sb = new StringBuilder();
            sb.AppendLine("[" + owner.Tag + "] trade: buy " + (owner.TradeBuyOptIn ? "ON" : "off")
                          + ", sell " + (owner.TradeSellOptIn ? "ON" : "off"));

            if (!owner.TradeBuyOptIn && !owner.TradeSellOptIn)
            {
                sb.AppendLine("  Nothing is dispatched, and no other faction can see what you are short of.");
                sb.AppendLine("  !factions station trade buy on   -- fly out to acquire, and let neighbours supply you");
                sb.AppendLine("  !factions station trade sell on  -- fly surplus to a partner that has asked for it");
                return sb.ToString();
            }

            // Project on demand so the readback shows what the next pass will actually use rather
            // than whatever the last one left behind.
            FactionLifecycle.ProjectPlayerTradePriority(owner, _world, _tuning);

            if (owner.TradePriority.Count == 0)
            {
                sb.AppendLine("  NO PARTNERS RANKED — nothing will be dispatched in either direction.");
                sb.AppendLine("  A partner needs relation above " + _tuning.HostileRelationThreshold.ToString("0.##", CultureInfo.InvariantCulture)
                              + ", no embargo, and your influence at one of its nodes above "
                              + _tuning.TradeReachThreshold.ToString("0.###", CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                sb.AppendLine("  " + owner.TradePriority.Count + " partner(s) ranked:");
                int shown = 0;
                foreach (TradePriorityEntry e in owner.TradePriority)
                {
                    if (shown++ >= 8) { sb.AppendLine("  ... and " + (owner.TradePriority.Count - 8) + " more"); break; }
                    sb.AppendLine("    " + e.PartnerTag + "  score " + e.Score.ToString("0.###", CultureInfo.InvariantCulture));
                }
            }

            if (owner.Embargoes.Count > 0)
                sb.AppendLine("  embargoed: " + string.Join(", ", new List<string>(owner.Embargoes).ToArray()));

            bool hasHull = OperationsManager.OwnsSuitableHull(_world, owner, ActionKind.Trade);
            sb.AppendLine("  trade-capable hull owned: " + (hasHull ? "yes" : "NO — the buy direction cannot dispatch without one"));
            sb.AppendLine("  unserved rows published: " + owner.UnservedDemands.Count
                          + (owner.TradeBuyOptIn ? "" : "  (buy is off, so neighbours cannot see your shortage)"));
            sb.AppendLine("  in-flight trade fleets: " + TradeManager.InFlightTradeCount(owner) + " of " + _tuning.MaxTradeFleets);
            return sb.ToString();
        }

        /// <summary>One leader act of a player faction toward an NPC, or the diplomacy readout
        /// (docs/diplomacy-plan.md, rule 3). Shared by the <c>!factions diplomacy</c> chat command,
        /// which checks the caller is a leader before it gets here, and the admin bridge verb, which
        /// does not. The rule itself is <see cref="PlayerDiplomacy"/> in Core.
        ///
        /// <para>An act that changed something writes one <c>DIPLOMACY_*</c> line to the Torch log,
        /// beside the vanilla war button's <c>DIPLOMACY_WAR</c>, and is flushed at once. War also
        /// re-projects the SE relation table now rather than at the next diplomacy pass, so turrets
        /// and the Factions screen agree with the declaration immediately.</para></summary>
        public string LeaderDiplomacy(FactionState player, string verb, string npcTag, string arg)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (player == null) return "Your faction is not registered with Factions yet.";

            LeaderAct act;
            string error;
            if (!PlayerDiplomacy.TryParse(verb, arg, out act, out error)) return error;

            bool done;
            return LeaderDiplomacy(player, act, npcTag, out done);
        }

        /// <summary>The same entry point with the act already parsed, for a caller that never had a
        /// verb string: the FactionsUI Diplomacy window (<see cref="PlayerDiplomacyManager"/>).
        /// <paramref name="done"/> is true when the act changed something.</summary>
        public string LeaderDiplomacy(FactionState player, LeaderAct act, string npcTag, out bool done)
        {
            done = false;
            if (_world == null) return "Factions runtime not ready.";
            if (player == null) return "Your faction is not registered with Factions yet.";

            DateTime now = _world.UtcNow;
            if (act == LeaderAct.Status)
                return PlayerDiplomacy.Describe(player, _states, string.IsNullOrEmpty(npcTag) ? null : npcTag, _tuning, now);

            FactionState npc = string.IsNullOrEmpty(npcTag) ? null : FindFaction(npcTag);
            string refusal = PlayerDiplomacy.TargetRefusal(player, npc, npcTag);
            if (refusal != null) return refusal;

            LeaderActResult r = PlayerDiplomacy.Act(act, player, npc, _world, _tuning, now);
            if (!r.Done) return r.Response;

            done = true;
            FactionsPlugin.Log.Info(r.LogLine);
            if (act == LeaderAct.War) ProjectRelationsToSe("leader");
            Flush();
            return r.Response;
        }

        /// <summary>Bridge verb <c>factions diplomacy &lt;playerTag&gt; [verb] [npcTag] [kind]</c>: the admin
        /// twin of <c>!factions diplomacy</c>, naming the player faction explicitly. No leader check:
        /// the bridge is the operator.</summary>
        public string LeaderDiplomacyAdmin(string[] t)
        {
            if (_world == null) return "diplomacy: runtime not started (no session loaded).";
            if (t == null || t.Length < 3)
                return "Usage: factions diplomacy <playerTag> [" + PlayerDiplomacy.Usage + "]";
            FactionState player = FindFaction(t[2]);
            if (player == null) return "diplomacy: no faction '" + t[2] + "'.";
            if (!player.IsPlayerFaction) return "diplomacy: [" + player.Tag + "] is not a player faction.";
            string verb = t.Length > 3 ? t[3] : null;
            string npcTag = t.Length > 4 ? t[4] : null;
            string kind = t.Length > 5 ? t[5] : null;
            return LeaderDiplomacy(player, verb, npcTag, kind);
        }

        public string ToggleReservationForPlayer(FactionState owner, NodeCapabilityKind capability)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            bool nowEnabled;
            if (owner.ReservationOptIn.Remove(capability))
            {
                nowEnabled = false;
            }
            else
            {
                owner.ReservationOptIn.Add(capability);
                nowEnabled = true;
            }

            Flush();
            _log.Write("STATION_RESERVATION_TOGGLED_BY_PLAYER " + owner.Tag + " cap=" + capability + " enabled=" + nowEnabled);
            return nowEnabled
                ? capability + " investment/reservation is now ENABLED — Factions will start staging materials toward it."
                : capability + " investment/reservation is now DISABLED — your components will not be reserved toward it.";
        }

        /// <summary>Player-facing list for `!factions station queue [nodeId]` — see
        /// <see cref="Commands.PlayerStationCommands"/>. Formats every <see cref="BuildQueueEntry"/>
        /// at <paramref name="nodeId"/> (or across every node the caller owns when omitted) into
        /// chat text. Read-only — no Core call needed, same idiom <see cref="Commands.PlayerStationCommands.Status"/>
        /// already uses for reading <c>node.Capabilities</c>/<c>node.Inventory</c> directly.</summary>
        public string ListBuildQueueForPlayer(FactionState owner, string nodeId)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            var sb = new StringBuilder();
            int count;
            if (!string.IsNullOrEmpty(nodeId))
            {
                SpatialNode node;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node))
                    return "No node '" + nodeId + "'.";
                if (!string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
                    return nodeId + " isn't owned by your faction.";
                count = AppendQueueLines(sb, node);
            }
            else
            {
                count = 0;
                foreach (SpatialNode node in _world.Graph.OwnedBy(owner.Tag))
                    count += AppendQueueLines(sb, node);
            }

            if (count == 0)
                return "[" + owner.Tag + "] has no active builds" + (nodeId != null ? " at " + nodeId : "") + ".";
            return sb.ToString();
        }

        private static int AppendQueueLines(StringBuilder sb, SpatialNode node)
        {
            int n = 0;
            foreach (BuildQueueEntry e in node.BuildQueue)
            {
                long bomTotal = 0, collected = 0;
                foreach (var kv in e.Bom) bomTotal += kv.Value;
                foreach (var kv in e.Collected) collected += kv.Value;
                string pct = bomTotal > 0 ? (collected * 100.0 / bomTotal).ToString("0") + "%" : "100%";
                sb.AppendLine(node.Id + "  " + e.Id + "  prefab=" + (e.PrefabId ?? "(free)")
                    + "  " + collected + "/" + bomTotal + " (" + pct + ")"
                    + "  prio=" + e.Priority
                    + (node.ShipyardPaused ? "  PAUSED" : "  expires=" + e.ExpiresUtc.ToString("u")));
                n++;
            }
            return n;
        }

        /// <summary>Player-facing cancel for `!factions station queue cancel &lt;nodeId&gt; &lt;entryId&gt;` —
        /// see <see cref="Commands.PlayerStationCommands"/>. Refunds the entry's collected
        /// components and reserved inputs back to node inventory (nothing destroyed) via
        /// <see cref="ShipyardManager.CancelBuild"/>.</summary>
        public string CancelBuildForPlayer(FactionState owner, string nodeId, string entryId)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(entryId))
                return "Usage: !factions station queue cancel <nodeId> <entryId>";

            BuildQueueEntry cancelled;
            PlayerQueueResult result = _shipyardManager.CancelBuild(_world, owner.Tag, nodeId, entryId, out cancelled, _managerLog);
            switch (result)
            {
                case PlayerQueueResult.NodeNotFound:  return "No node '" + nodeId + "'.";
                case PlayerQueueResult.NotOwned:      return nodeId + " isn't owned by your faction.";
                case PlayerQueueResult.EntryNotFound: return "No build '" + entryId + "' at " + nodeId + ".";
            }

            Flush();
            return "Cancelled " + entryId + " (" + (cancelled.PrefabId ?? "free build") + ") at " + nodeId
                   + " — components returned to inventory.";
        }

        /// <summary>Admin bridge cancel for `factions cancelbuild &lt;tag&gt; &lt;nodeId&gt; &lt;entryId&gt;`
        /// (see <see cref="FactionsPlugin.HandleCancelBuild"/>) — same refund/removal via <see
        /// cref="ShipyardManager.CancelBuild"/> as <see cref="CancelBuildForPlayer"/>, but for any
        /// registered faction tag directly (NPC included), since the player-facing command can only
        /// ever act on the calling player's own faction.</summary>
        public string CancelBuildAdmin(string factionTag, string nodeId, string entryId)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(entryId))
                return "Usage: factions cancelbuild <tag> <nodeId> <entryId>";

            BuildQueueEntry cancelled;
            PlayerQueueResult result = _shipyardManager.CancelBuild(_world, factionTag, nodeId, entryId, out cancelled, _managerLog);
            switch (result)
            {
                case PlayerQueueResult.NodeNotFound:  return "No node '" + nodeId + "'.";
                case PlayerQueueResult.NotOwned:      return nodeId + " isn't owned by '" + factionTag + "'.";
                case PlayerQueueResult.EntryNotFound: return "No build '" + entryId + "' at " + nodeId + ".";
            }

            Flush();
            return "Cancelled " + entryId + " (" + (cancelled.PrefabId ?? "free build") + ") at " + nodeId
                   + " for " + factionTag + " — components returned to inventory.";
        }

        /// <summary>Player-facing pause/resume for `!factions station queue pause|resume [nodeId]` —
        /// see <see cref="Commands.PlayerStationCommands"/>. Delegates to <see cref="ShipyardManager.PauseShipyard"/>,
        /// which stops the yard claiming anything new and releases its uncollected BOM share back to
        /// the rest of the network, while leaving what each build already holds untouched.</summary>
        public string PauseShipyardForPlayer(FactionState owner, string nodeId, bool paused)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            return PauseShipyardInternal(owner.Tag, nodeId, paused, forPlayer: true);
        }

        /// <summary>Admin bridge pause for `factions pausebuild &lt;tag&gt; &lt;nodeId&gt; [on|off]`
        /// (see <see cref="FactionsPlugin.HandlePauseBuild"/>) — same Core call as
        /// <see cref="PauseShipyardForPlayer"/>, for any registered faction tag including NPCs, since
        /// a starved capability install is just as likely on an NPC yard.</summary>
        public string PauseShipyardAdmin(string factionTag, string nodeId, bool paused)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(nodeId))
                return "Usage: factions pausebuild <tag> <nodeId> [on|off]";
            return PauseShipyardInternal(factionTag, nodeId, paused, forPlayer: false);
        }

        private string PauseShipyardInternal(string factionTag, string nodeId, bool paused, bool forPlayer)
        {
            if (string.IsNullOrEmpty(nodeId))
                return "Usage: !factions station queue " + (paused ? "pause" : "resume") + " [nodeId]";

            PlayerQueueResult result = _shipyardManager.PauseShipyard(_world, _tuning, factionTag, nodeId, paused, _managerLog);
            switch (result)
            {
                case PlayerQueueResult.NodeNotFound: return "No node '" + nodeId + "'.";
                case PlayerQueueResult.NotOwned:
                    return forPlayer
                        ? nodeId + " isn't owned by your faction."
                        : nodeId + " isn't owned by '" + factionTag + "'.";
                case PlayerQueueResult.NoShipyard:   return nodeId + " has no Shipyard capability installed.";
            }

            Flush();
            _log.Write("SHIPYARD_PAUSE_BY_PLAYER " + factionTag + " node=" + nodeId + " paused=" + paused);
            return paused
                ? nodeId + " shipyard PAUSED — its builds keep what they already hold but claim nothing "
                    + "more, and the components they still needed are back in circulation."
                : nodeId + " shipyard RESUMED — builds start collecting again and their deadlines are re-armed.";
        }

        /// <summary>Player-facing claim rank for `!factions station queue priority [nodeId]
        /// &lt;entryId&gt; &lt;n&gt;` — see <see cref="Commands.PlayerStationCommands"/> and
        /// <see cref="BuildQueueEntry.Priority"/>.</summary>
        public string SetBuildPriorityForPlayer(FactionState owner, string nodeId, string entryId, int priority)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            return SetBuildPriorityInternal(owner.Tag, nodeId, entryId, priority, forPlayer: true);
        }

        /// <summary>Admin bridge counterpart of <see cref="SetBuildPriorityForPlayer"/> for
        /// `factions buildpriority &lt;tag&gt; &lt;nodeId&gt; &lt;entryId&gt; &lt;n&gt;`.</summary>
        public string SetBuildPriorityAdmin(string factionTag, string nodeId, string entryId, int priority)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(entryId))
                return "Usage: factions buildpriority <tag> <nodeId> <entryId> <priority>";
            return SetBuildPriorityInternal(factionTag, nodeId, entryId, priority, forPlayer: false);
        }

        private string SetBuildPriorityInternal(string factionTag, string nodeId, string entryId, int priority,
            bool forPlayer)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(entryId))
                return "Usage: !factions station queue priority [nodeId] <entryId> <priority>";

            PlayerQueueResult result = _shipyardManager.SetBuildPriority(_world, factionTag, nodeId, entryId,
                priority, _managerLog);
            switch (result)
            {
                case PlayerQueueResult.NodeNotFound:  return "No node '" + nodeId + "'.";
                case PlayerQueueResult.NotOwned:
                    return forPlayer
                        ? nodeId + " isn't owned by your faction."
                        : nodeId + " isn't owned by '" + factionTag + "'.";
                case PlayerQueueResult.EntryNotFound: return "No build '" + entryId + "' at " + nodeId + ".";
            }

            Flush();
            _log.Write("BUILD_PRIORITY_BY_PLAYER " + factionTag + " node=" + nodeId + " entry=" + entryId
                    + " priority=" + priority);
            return "Build " + entryId + " at " + nodeId + " is now priority " + priority
                   + " — higher wins a contested component against this station's other builds and against "
                   + "capability installs ranked below it.";
        }

        /// <summary>Player-facing claim rank for a capability install
        /// (`!factions station develop &lt;capability&gt; priority &lt;n&gt;`) — writes
        /// <see cref="FactionState.InstallPriority"/>, the install-side half of the same scale
        /// <see cref="BuildQueueEntry.Priority"/> uses. Mirrors
        /// <see cref="ToggleReservationForPlayer"/>'s shape: faction-wide, no nodeId.</summary>
        public string SetInstallPriorityForPlayer(FactionState owner, NodeCapabilityKind capability, int priority)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            if (priority == 0) owner.InstallPriority.Remove(capability);
            else owner.InstallPriority[capability] = priority;

            Flush();
            _log.Write("INSTALL_PRIORITY_BY_PLAYER " + owner.Tag + " cap=" + capability + " priority=" + priority);
            return priority > 0
                ? capability + " install is now priority " + priority + " — it outranks any ship build below "
                    + priority + ", so components it still needs are staged toward the install first."
                : capability + " install is now priority " + priority
                    + " — ship builds at or above that rank claim contested components first (the default).";
        }

        /// <summary>Player-facing build for `!factions station queue build &lt;nodeId&gt; &lt;prefabId&gt;`
        /// and the Build dialog — see <see cref="Commands.PlayerStationCommands"/>. Queues
        /// <paramref name="prefabId"/> at <paramref name="nodeId"/>. The player names a hull and no
        /// role; what it answers is read off the hull (<see cref="ShipyardManager.QueueBuild"/>).</summary>
        public string QueueBuildForPlayer(FactionState owner, string nodeId, string prefabId)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(prefabId))
                return "Usage: !factions station queue build <nodeId> <prefabId>";

            // A design's name means its latest version for this faction; an older version is never
            // built new.
            prefabId = ResolveDesignForPlayer(prefabId, owner.Tag);

            // Faction-restricted captures (`!factions ship capture faction ...`) are access-controlled
            // here, not in ShipyardManager.QueueBuild -- ownership is a host-layer access-control
            // concern layered on top of Core's purely physical suitability check.
            var owned = _world.PrefabCatalogue as IOwnedPrefabCatalogue;
            if (owned != null)
            {
                string restrictedTo = owned.OwnerFactionTag(prefabId);
                if (!string.IsNullOrEmpty(restrictedTo) && !string.Equals(restrictedTo, owner.Tag, StringComparison.OrdinalIgnoreCase))
                    return "'" + prefabId + "' is a faction-restricted capture, not usable by [" + owner.Tag + "].";
            }

            Dictionary<string, long> bom;
            PlayerQueueResult result = _shipyardManager.QueueBuild(_world, _tuning, owner.Tag, nodeId, null, prefabId, out bom, _managerLog);
            switch (result)
            {
                case PlayerQueueResult.NodeNotFound: return "No node '" + nodeId + "'.";
                case PlayerQueueResult.NotOwned:     return nodeId + " isn't owned by your faction.";
                case PlayerQueueResult.NoShipyard:   return nodeId + " has no Shipyard capability installed.";
                case PlayerQueueResult.UnknownPrefab:
                    return "'" + prefabId + "' isn't a buildable ship prefab — try !factions station queue prefabs <role>";
                case PlayerQueueResult.Banned:
                    return BannedRefusal(prefabId);
                case PlayerQueueResult.OverPerformanceCeiling:
                    return "'" + prefabId + "' is over this server's performance ceiling for ship prefabs and cannot be built.";
            }

            Flush();
            var sb = new StringBuilder();
            sb.AppendLine("Queued " + prefabId + " at " + nodeId + ". Needs:");
            foreach (var kv in bom) sb.AppendLine("  " + kv.Key + " x" + kv.Value);
            SpatialNode queuedAt;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out queuedAt) && queuedAt.ShipyardPaused)
                sb.AppendLine("NOTE: this shipyard is PAUSED — the build won't collect anything until "
                    + "!factions station queue resume.");
            return sb.ToString();
        }

        /// <summary>Player-facing prefab discovery for `!factions station queue prefabs &lt;role&gt;` —
        /// the catalogue-curated candidate set for the role (the faction's own hint pool, same one
        /// <see cref="ShipyardManager"/>'s own automatic selection draws from, plus the universal
        /// "All"-hint pool). Not exhaustive: <see cref="QueueBuildForPlayer"/> itself accepts any
        /// prefab that passes <see cref="ShipPool.IsSuitableForRole"/>, not just this list — this is
        /// a discovery aid only, since no Factions prefab ID is shown anywhere in SE's own UI.</summary>
        public string ListPrefabsForPlayer(FactionState owner, string role)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(role) || Array.IndexOf(ShipyardManager.PlayerQueueRoles, role) < 0)
                return "Unknown role '" + role + "'. Valid: " + string.Join(", ", ShipyardManager.PlayerQueueRoles);
            if (_world.PrefabCatalogue == null) return "No prefab catalogue loaded.";

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_shipyardManager.PrefabListPicker != null)
            {
                IList<string> picked = _shipyardManager.PrefabListPicker(role, owner.Tag);
                if (picked != null)
                    foreach (string id in picked) names.Add(id);
            }
            IReadOnlyList<string> universal = PrefabOwnershipFilter.Apply(
                _world.PrefabCatalogue.GetNamesForRole(role, "All"), _world.PrefabCatalogue, owner.Tag);
            foreach (string id in universal) names.Add(id);

            if (names.Count == 0) return "No known " + role + " prefabs in the catalogue.";
            return role + " prefabs: " + string.Join(", ", names);
        }

        /// <summary>Player-facing one-shot Mine dispatch for `!factions station mine &lt;sourceNodeId&gt;
        /// [destNodeId] [oreWeights]` — see <see cref="Commands.PlayerStationCommands"/>.
        /// <paramref name="destNodeId"/> optional: omit to auto-resolve via live demand, falling back
        /// to a local top-up at <paramref name="sourceNodeId"/> itself when nothing is currently
        /// short. <paramref name="oreWeightsSpec"/> optional (e.g. <c>"Iron:50,Nickel:50"</c>): omit
        /// for the node's natural full mix — parsed at this host boundary via
        /// <see cref="OreWeightSpec.TryParse"/> so Core's own API stays typed, never string-based.
        /// Routes through <see cref="OperationsManager.TriggerPlayerMine"/> so this is never
        /// cheaper/easier than the automatic demand-driven Mine path — only the "does a live demand
        /// entry exist" precondition is bypassed. Reports <see cref="MineDispatchOutcome"/> honestly:
        /// a validation pass (<see cref="PlayerMineResult.Ok"/>) used to always print the same
        /// "dispatch triggered" line even when no route-feasible drill ship existed anywhere and the
        /// whole thing silently fell back to a <c>Ship:Mine</c> demand (e.g. an atmosphere-only miner
        /// aimed at an Orbit/DeepSpace destination) — surfaced live 2026-08-18 as a false-positive
        /// success message with no error and no log trace the player could see.</summary>
        public string TriggerMineForPlayer(FactionState owner, string sourceNodeId, string destNodeId, string oreWeightsSpec)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(sourceNodeId))
                return "Usage: !factions station mine <sourceNodeId> [destNodeId] [oreWeights]";

            Dictionary<string, double> requestedWeights;
            string parseError;
            if (!OreWeightSpec.TryParse(oreWeightsSpec, out requestedWeights, out parseError))
                return "Bad ore weights: " + parseError;

            MineDispatchOutcome dispatchOutcome;
            PlayerMineResult result = _opsManager.TriggerPlayerMine(_world, _tuning, owner, sourceNodeId, destNodeId, requestedWeights, _managerLog, out dispatchOutcome);
            switch (result)
            {
                case PlayerMineResult.SourceNotFound:     return "No node '" + sourceNodeId + "'.";
                case PlayerMineResult.SourceNotOwned:     return sourceNodeId + " isn't owned by your faction.";
                case PlayerMineResult.SourceNotMineable:  return sourceNodeId + " isn't a mineable node (Ore/RareOre/Ice field).";
                case PlayerMineResult.InvalidOreWeights:  return sourceNodeId + " doesn't yield any of the requested ores.";
                case PlayerMineResult.DestNotFound:       return "No node '" + destNodeId + "'.";
                case PlayerMineResult.DestNotOwned:       return destNodeId + " isn't owned by your faction.";
                case PlayerMineResult.OnCooldown:         return sourceNodeId + " was mined too recently — still on cooldown.";
            }

            Flush();
            if (dispatchOutcome == MineDispatchOutcome.NoShipRouteInfeasible)
                return "A drill ship exists in the network but can't survive the route to " + sourceNodeId
                    + " — queued a Ship:Mine build demand for a route-capable hull instead.";
            if (dispatchOutcome != MineDispatchOutcome.Dispatched)
                return "No route-capable drill ship available at " + sourceNodeId
                    + " (or any other owned node) for that destination — queued a Ship:Mine build demand instead.";
            return "Mine dispatch triggered at " + sourceNodeId
                + (string.IsNullOrEmpty(destNodeId) ? " (destination auto-selected)." : " -> " + destNodeId + ".");
        }

        /// <summary>Back-compatible entry point for `!factions station mine auto &lt;hubNodeId&gt;
        /// &lt;periodSeconds|off&gt; [oreWeights]` — see <see cref="Commands.PlayerStationCommands"/>.
        /// The verb predates <see cref="FactionState.StandingOrders"/>, when a hub could hold exactly
        /// one order; it now operates on the Mine orders anchored at that hub:
        /// <list type="bullet">
        /// <item>none at the hub — creates one, exactly as before;</item>
        /// <item>exactly one — updates it in place, exactly as before (so re-issuing the verb still
        /// adjusts rather than accumulating duplicates);</item>
        /// <item>several — refuses and points at `!factions station orders`, because "the" order at
        /// that hub is no longer a thing this verb can name. Silently picking one would edit an order
        /// the player did not mean.</item>
        /// </list>
        /// <c>off</c> removes every Mine order at the hub and says how many.
        /// <paramref name="oreWeightsSpec"/> optional (e.g. <c>"Iron:50,Nickel:50"</c>): omit for each
        /// catchment node's own natural full mix — parsed once here (not per-candidate) and stored on
        /// the order itself. No positivity floor beyond "must be &gt; 0": the real lower bound on a
        /// useful rescan is dynamic (see <see cref="OperationsManager.ScanStandingMineOrder"/>), not a
        /// constant this layer could clamp to.</summary>
        public string ToggleMineAutoForPlayer(FactionState owner, string hubNodeId, double periodSeconds, string oreWeightsSpec)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(hubNodeId))
                return "Usage: !factions station mine auto <hubNodeId> <periodSeconds|off> [oreWeights]";

            List<StandingOrder> atHub = MineOrdersAtHub(owner, hubNodeId);

            if (periodSeconds <= 0)
            {
                if (atHub.Count == 0) return "No standing auto-mine order at " + hubNodeId + ".";
                foreach (StandingOrder o in atHub) owner.RemoveStandingOrder(o);
                Flush();
                _log.Write("STATION_MINE_AUTO_TOGGLED_BY_PLAYER " + owner.Tag + " " + hubNodeId
                           + " enabled=false removed=" + atHub.Count);
                return atHub.Count == 1
                    ? "Auto-mine order at " + hubNodeId + " cancelled."
                    : "Cancelled " + atHub.Count + " auto-mine orders at " + hubNodeId + ".";
            }

            if (atHub.Count > 1)
                return hubNodeId + " has " + atHub.Count + " standing mine orders — this verb can't tell which you mean. "
                    + "Use !factions station orders to list them, then `orders remove <id>` or `orders mine` to add another.";

            SpatialNode hub;
            string hubError;
            if (!TryResolveOwnedNode(owner, hubNodeId, out hub, out hubError)) return hubError;

            Dictionary<string, double> requestedWeights;
            string parseError;
            if (!OreWeightSpec.TryParse(oreWeightsSpec, out requestedWeights, out parseError))
                return "Bad ore weights: " + parseError;
            if (requestedWeights != null && requestedWeights.Count == 0) requestedWeights = null;

            if (atHub.Count == 1)
            {
                StandingOrder existing = atHub[0];
                existing.PeriodSeconds = periodSeconds;
                existing.OreWeights = requestedWeights;
                existing.NextScanUtc = _world.UtcNow;
                owner.StandingOrdersRevision++;   // a player edit, so an open window must see it
                Flush();
                _log.Write("STATION_MINE_AUTO_TOGGLED_BY_PLAYER " + owner.Tag + " " + hubNodeId
                           + " enabled=true period=" + periodSeconds.ToString("0") + " order=" + existing.Id);
                return "Auto-mine order " + existing.Id + " at " + hubNodeId + " updated — rescanning at least every "
                    + periodSeconds.ToString("0") + "s (sooner as catchment nodes come off cooldown).";
            }

            string capError;
            if (!TryReserveStandingOrderSlot(owner, out capError)) return capError;
            StandingOrder order = NewMineOrder(owner, hubNodeId, periodSeconds, requestedWeights);
            Flush();
            _log.Write("STATION_MINE_AUTO_TOGGLED_BY_PLAYER " + owner.Tag + " " + hubNodeId
                       + " enabled=true period=" + periodSeconds.ToString("0") + " order=" + order.Id);
            return "Auto-mine order " + order.Id + " set at " + hubNodeId + " — rescanning at least every "
                + periodSeconds.ToString("0") + "s (sooner as catchment nodes come off cooldown).";
        }

        /// <summary>Player-facing list for `!factions station mine auto` (no args) — the Mine half of
        /// <see cref="ListStandingOrdersForPlayer"/>, kept because the verb predates the unified list.</summary>
        public string ListMineAutoForPlayer(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            List<StandingOrder> orders = owner.StandingOrdersByRank(StandingOrderKind.Mine);
            if (orders.Count == 0) return "[" + owner.Tag + "] has no standing auto-mine orders.";

            var sb = new StringBuilder();
            foreach (StandingOrder o in orders) sb.AppendLine(DescribeStandingOrder(owner.Tag, o));
            sb.Append("All standing orders: !factions station orders   |   fleet composition: !factions fleet");
            return sb.ToString();
        }

        // ---- standing orders (FAC-008) --------------------------------------------------------
        //
        // One list, one set of verbs. Every mutator below follows the same shape the other
        // player-facing runtime wrappers already use: validate against the world, mutate Core state,
        // Flush(), write one DecisionLog line, return chat text.

        /// <summary>Player-facing list for `!factions station orders` — every standing order this
        /// faction holds, in the order the managers evaluate them (see
        /// <see cref="FactionState.StandingOrdersByRank"/>). Read-only, same idiom
        /// <see cref="ListBuildQueueForPlayer"/> uses.</summary>
        public string ListStandingOrdersForPlayer(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (owner.StandingOrders.Count == 0)
                return "[" + owner.Tag + "] has no standing orders. !factions station orders help for syntax.";

            var sb = new StringBuilder();
            foreach (StandingOrder o in owner.StandingOrdersByRank()) sb.AppendLine(DescribeStandingOrder(owner.Tag, o));
            sb.Append(owner.StandingOrders.Count).Append('/').Append(_tuning.StandingOrderCap)
              .Append(" standing orders. Higher rank is evaluated first and wins a contested deposit; a stock order at rank 1 or more is supplied before ship builds.")
              .Append(Environment.NewLine)
              .Append("What your fleet is made of is a separate list: !factions fleet");
            return sb.ToString();
        }

        /// <summary>Adds a standing Mine order for `!factions station orders mine &lt;hubNodeId&gt;
        /// &lt;periodSeconds&gt; [oreWeights]`. Unlike <see cref="ToggleMineAutoForPlayer"/> this
        /// always ADDS — several orders on one hub, differing in ore weights and rank, is the case the
        /// old one-order-per-hub dictionary could not express and the reason this verb exists.</summary>
        public string AddStandingMineOrderForPlayer(FactionState owner, string hubNodeId, double periodSeconds, string oreWeightsSpec)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(hubNodeId) || periodSeconds <= 0)
                return "Usage: !factions station orders mine <hubNodeId> <periodSeconds> [oreWeights]";

            SpatialNode hub;
            string hubError;
            if (!TryResolveOwnedNode(owner, hubNodeId, out hub, out hubError)) return hubError;

            Dictionary<string, double> requestedWeights;
            string parseError;
            if (!OreWeightSpec.TryParse(oreWeightsSpec, out requestedWeights, out parseError))
                return "Bad ore weights: " + parseError;
            if (requestedWeights != null && requestedWeights.Count == 0) requestedWeights = null;

            string capError;
            if (!TryReserveStandingOrderSlot(owner, out capError)) return capError;

            StandingOrder order = NewMineOrder(owner, hubNodeId, periodSeconds, requestedWeights);
            Flush();
            _log.Write("STANDING_ORDER_ADDED_BY_PLAYER " + owner.Tag + " " + order.Id + " kind=Mine hub=" + hubNodeId
                       + " period=" + periodSeconds.ToString("0"));
            return "Standing order " + order.Id + " added: " + DescribeStandingOrder(owner.Tag, order);
        }

        /// <summary>Resolves what a player typed as an item to the inventory key node stock is kept
        /// under, in its canonical case. Wired by the host to the store's definition map; null until
        /// then, and a Stock order cannot be created while it is.</summary>
        public delegate bool ItemKeyCanonicaliser(string input, out string invKey);

        public ItemKeyCanonicaliser CanonicaliseItemKey { get; set; }

        /// <summary>One entry of the Stock-order item list.</summary>
        public struct OrderableItem
        {
            public string Key;
            public string Name;
            public string Category;
        }

        /// <summary>Every item a Stock order may name. Wired by the host beside
        /// <see cref="CanonicaliseItemKey"/>, from the same definition map; null until then.</summary>
        public Func<List<OrderableItem>> ListOrderableItems { get; set; }

        /// <summary>Where each item could come from for <paramref name="owner"/> today, for the item
        /// list (<see cref="StockItemSources"/>).</summary>
        public Dictionary<string, StockItemSource> StockItemSourcesFor(FactionState owner)
        {
            if (_world == null || owner == null) return new Dictionary<string, StockItemSource>();
            return StockItemSources.Build(owner, _world.Graph, _capRecipes);
        }

        /// <summary>The node argument of a Stock order that means every founded station
        /// (<see cref="StandingOrder.AllStations"/>).</summary>
        public const string AllStationsNodeArg = "all";

        /// <summary>Adds a Stock standing order for `!factions station orders stock &lt;nodeId|all&gt;
        /// &lt;item&gt; &lt;quantity&gt;`: keep <paramref name="quantity"/> of the item at the node, or
        /// at every founded station the faction owns (<see cref="StockOrders"/>). The item need not have
        /// a source anywhere yet: the order's demand is what makes it visible to anyone who could
        /// supply it.</summary>
        public string AddStandingStockOrderForPlayer(FactionState owner, string nodeId, string item, long quantity)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(item) || quantity <= 0)
                return "Usage: !factions station orders stock <nodeId|all> <item> <quantity>";

            bool allStations = string.Equals(nodeId, AllStationsNodeArg, StringComparison.OrdinalIgnoreCase);
            SpatialNode node = null;
            string nodeError;
            if (!allStations && !TryResolveOwnedNode(owner, nodeId, out node, out nodeError)) return nodeError;

            string itemKey;
            if (CanonicaliseItemKey == null || !CanonicaliseItemKey(item, out itemKey))
                return "'" + item + "' isn't an item. Ores, ingots and components take a prefix "
                    + "(Ore:Iron, Ingot:Iron, Comp:SteelPlate); everything else is its subtype (NATO_25x184mm).";

            string capError;
            if (!TryReserveStandingOrderSlot(owner, out capError)) return capError;

            var order = new StandingOrder
            {
                Id = owner.MintStandingOrderId(),
                Kind = StandingOrderKind.Stock,
                CreatedUtc = _world.UtcNow,
                NodeId = allStations ? null : node.Id,
                AllStations = allStations,
                ItemKey = itemKey,
                TargetQuantity = quantity,
            };
            owner.AddStandingOrder(order);
            StockOrders.Publish(owner, _world);
            Flush();
            _log.Write("STANDING_ORDER_ADDED_BY_PLAYER " + owner.Tag + " " + order.Id + " kind=Stock node="
                       + (allStations ? "ALL" : node.Id) + " item=" + itemKey + " target=" + quantity);
            return "Standing order " + order.Id + " added: " + DescribeStandingOrder(owner.Tag, order);
        }

        // ---- fleet doctrine (docs/fleet-doctrine-plan.md) ---------------------------------------
        //
        // Same shape as the standing-order mutators above: validate against the world, mutate Core
        // state, Flush(), write one DecisionLog line, return chat text. The entry points below are
        // the ONLY way a line is created or changed -- the chat verbs, the bridge verb and (from
        // slice 3) the client all route through them, so a client pick-list is a convenience and
        // never an authorisation.

        /// <summary>Player-facing list for `!factions fleet` -- every doctrine line, in the order
        /// the shipyard evaluates them.</summary>
        public string ListFleetDoctrineForPlayer(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (owner.FleetDoctrine.Count == 0)
                return "[" + owner.Tag + "] has no fleet doctrine, so Factions picks your hulls for you. "
                    + "!factions fleet help for syntax.";

            var sb = new StringBuilder();
            foreach (DoctrineLine line in owner.DoctrineByRank()) sb.AppendLine(DescribeDoctrineLine(owner, line));
            sb.Append(owner.FleetDoctrine.Count).Append('/').Append(_tuning.FleetDoctrineCap)
              .Append(" doctrine lines. While this list is non-empty it is the only thing that builds "
                      + "FLEET hulls for you: nothing else is picked, and a line at its target stays "
                      + "there. Your shop stocks itself separately -- !factions store.");
            return sb.ToString();
        }

        /// <summary>Adds a doctrine line for `!factions fleet add &lt;prefabId&gt; &lt;count&gt;
        /// [concurrent=n] [node=Nxxx]`. Everything is validated HERE rather than discovered on the
        /// first evaluation pass: a line that could never place a build would otherwise sit in the
        /// player's list looking healthy and silently do nothing.
        ///
        /// <para>A line names a hull and no role. The pressure it answers is every role its hull is
        /// suitable for (<see cref="ShipyardManager.HighestRankedLineFor"/>), which is a rule rather
        /// than a guess: it is the test fleet dispatch applies to the finished hull.</para></summary>
        public string AddDoctrineLineForPlayer(FactionState owner, string prefabId,
            int targetCount, int maxConcurrent, string scopeNodeId)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(prefabId) || targetCount <= 0)
                return "Usage: !factions fleet add <prefabId> <count> [concurrent=<n>] [node=<nodeId>]";

            if (!ShipyardManager.FitsAnyPlayerRole(_world.PrefabCatalogue, prefabId))
                return "Prefab '" + prefabId + "' isn't suitable for any build role ("
                    + string.Join(", ", ShipyardManager.PlayerQueueRoles)
                    + "). !factions station queue prefabs <role> lists the ones that are.";

            if (_world.IsPrefabBanned(prefabId)) return BannedRefusal(prefabId);

            if (!ShipyardManager.HasBuildRecipe(_world.Recipes, prefabId))
                return "No build recipe for prefab '" + prefabId + "' — nothing could ever be built for it.";

            if (!string.IsNullOrEmpty(scopeNodeId))
            {
                SpatialNode scope;
                string scopeError;
                if (!TryResolveOwnedNode(owner, scopeNodeId, out scope, out scopeError)) return scopeError;
                int shipyardLevel;
                if (!scope.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel) || shipyardLevel < 1)
                    return scopeNodeId + " has no Shipyard, so a line scoped to it could never build anything.";
                scopeNodeId = scope.Id;
            }

            string capError;
            if (!TryReserveDoctrineSlot(owner, out capError)) return capError;

            bool wasInactive = owner.FleetDoctrine.Count == 0;
            var line = new DoctrineLine
            {
                Id = owner.MintDoctrineLineId(),
                PrefabId = prefabId,
                TargetCount = targetCount,
                MaxConcurrent = maxConcurrent > 0 ? maxConcurrent : 1,
                ScopeNodeId = string.IsNullOrEmpty(scopeNodeId) ? null : scopeNodeId,
                CreatedUtc = _world.UtcNow,
            };
            owner.AddDoctrineLine(line);
            Flush();
            _log.Write("DOCTRINE_LINE_ADDED_BY_PLAYER " + owner.Tag + " " + line.Id + " prefab=" + prefabId
                       + " target=" + targetCount + " concurrent=" + line.MaxConcurrent
                       + " scope=" + (line.ScopeNodeId ?? "any"));

            string text = "Doctrine line " + line.Id + " added: " + DescribeDoctrineLine(owner, line);
            // The first line is the switch: say so once, because from here on the managers stop
            // choosing hulls and a role with no line is a role that is not built.
            if (wasInactive && owner.IsPlayerFaction)
                text += Environment.NewLine
                    + "Your doctrine is now the only thing that builds FLEET hulls for [" + owner.Tag
                    + "]. A role with no line is not built. Shop stock is separate -- !factions store.";
            return text;
        }

        /// <summary>Removes a doctrine line for `!factions fleet remove &lt;id&gt;`. The id is never
        /// reused (see <see cref="FactionState.MintDoctrineLineId"/>), so removing one cannot
        /// renumber the others. Removing the LAST line hands hull selection back to the managers,
        /// which is a big enough change to say out loud.</summary>
        public string RemoveDoctrineLineForPlayer(FactionState owner, string lineId)
        {
            DoctrineLine line;
            string error;
            if (!TryResolveDoctrineLine(owner, lineId, out line, out error)) return error;

            owner.RemoveDoctrineLine(line);
            Flush();
            _log.Write("DOCTRINE_LINE_REMOVED_BY_PLAYER " + owner.Tag + " " + line.Id
                       + " prefab=" + (line.PrefabId ?? "?"));
            string text = "Doctrine line " + line.Id + " removed.";
            if (owner.FleetDoctrine.Count == 0 && owner.IsPlayerFaction)
                text += " That was the last line: Factions will pick hulls for [" + owner.Tag + "] again.";
            return text;
        }

        /// <summary>Ranks a doctrine line for `!factions fleet priority &lt;id&gt; &lt;n&gt;`. Higher
        /// is evaluated first, and evaluated first is what wins a shipyard slot two lines both want.
        /// Deliberately the SAME scale as <see cref="BuildQueueEntry.Priority"/> and
        /// <see cref="FactionState.InstallPriority"/>: a build a line places competes for components
        /// with the builds and capability installs already there.</summary>
        public string SetDoctrineLinePriorityForPlayer(FactionState owner, string lineId, int rank)
        {
            DoctrineLine line;
            string error;
            if (!TryResolveDoctrineLine(owner, lineId, out line, out error)) return error;

            int previous = line.Priority;
            owner.SetDoctrineLinePriority(line, rank);
            Flush();
            _log.Write("DOCTRINE_LINE_PRIORITY_BY_PLAYER " + owner.Tag + " " + line.Id
                       + " from=" + previous + " to=" + rank);
            return "Doctrine line " + line.Id + " ranked " + rank + " (was " + previous + "): "
                + DescribeDoctrineLine(owner, line);
        }

        /// <summary>Pauses or resumes a doctrine line for `!factions fleet pause|resume &lt;id&gt;`.
        /// The line keeps its id and rank and stops being evaluated -- distinct from removing it, the
        /// same way pausing a shipyard is distinct from cancelling its build. A paused line also
        /// stops answering pressure, so waiting work for its role reads as unlined.</summary>
        public string SetDoctrineLinePausedForPlayer(FactionState owner, string lineId, bool paused)
        {
            DoctrineLine line;
            string error;
            if (!TryResolveDoctrineLine(owner, lineId, out line, out error)) return error;

            if (line.Paused == paused)
                return "Doctrine line " + line.Id + " is already " + (paused ? "paused." : "running.");
            owner.SetDoctrineLinePaused(line, paused);
            Flush();
            _log.Write("DOCTRINE_LINE_PAUSED_BY_PLAYER " + owner.Tag + " " + line.Id + " paused=" + paused);
            return "Doctrine line " + line.Id + (paused ? " paused." : " resumed.");
        }

        /// <summary>Sets how many hulls a line wants owned, for `!factions fleet target &lt;id&gt;
        /// &lt;n&gt;`. Lowering it scraps nothing -- the target is a ceiling as well as a floor, so a
        /// smaller number simply stops replacing losses.</summary>
        public string SetDoctrineLineTargetForPlayer(FactionState owner, string lineId, int targetCount)
        {
            DoctrineLine line;
            string error;
            if (!TryResolveDoctrineLine(owner, lineId, out line, out error)) return error;
            if (targetCount <= 0)
                return "A target below 1 is a line you do not want: !factions fleet remove " + line.Id + ".";

            int previous = line.TargetCount;
            owner.SetDoctrineLineTarget(line, targetCount);
            Flush();
            _log.Write("DOCTRINE_LINE_TARGET_BY_PLAYER " + owner.Tag + " " + line.Id
                       + " from=" + previous + " to=" + targetCount);
            return "Doctrine line " + line.Id + " target " + targetCount + " (was " + previous + "): "
                + DescribeDoctrineLine(owner, line);
        }

        /// <summary>Sets how many builds of a line's hull may run at once, for `!factions fleet
        /// concurrent &lt;id&gt; &lt;n&gt;` and the Fleet window's own control. One build per
        /// (node, role) is a separate cap the shipyard model applies regardless, so raising this
        /// above the number of eligible yards in scope changes nothing.</summary>
        public string SetDoctrineLineConcurrencyForPlayer(FactionState owner, string lineId, int maxConcurrent)
        {
            DoctrineLine line;
            string error;
            if (!TryResolveDoctrineLine(owner, lineId, out line, out error)) return error;
            if (maxConcurrent <= 0)
                return "At-once must be at least 1: !factions fleet pause " + line.Id
                    + " is how you stop a line without deleting it.";

            int previous = line.MaxConcurrent;
            owner.SetDoctrineLineConcurrency(line, maxConcurrent);
            Flush();
            _log.Write("DOCTRINE_LINE_CONCURRENCY_BY_PLAYER " + owner.Tag + " " + line.Id
                       + " from=" + previous + " to=" + maxConcurrent);
            return "Doctrine line " + line.Id + " builds " + maxConcurrent + " at once (was " + previous
                + "): " + DescribeDoctrineLine(owner, line);
        }

        /// <summary>Bridge verb `factions fleet &lt;tag&gt; [list|add|remove|priority|pause|resume|target] …`
        /// -- the admin twin of the player's own `!factions fleet`, and the only way this slice can be
        /// exercised on the headless test server: `HandleCommandFromServer` silently no-ops the chat
        /// commands (the null server-player fails the permission check), and these verbs additionally
        /// resolve the CALLER's faction, which a bridge command has none of. Every mutation routes
        /// into the same player-facing method the chat verb calls -- an entry point, never a second
        /// implementation, so verifying through it verifies the real path.</summary>
        public string FleetDoctrineAdmin(string[] t)
        {
            if (_world == null) return "fleet: runtime not started (no session loaded).";
            if (t == null || t.Length < 3)
                return "Usage: factions fleet <tag> [list|add|remove|priority|pause|resume|target] ...";

            FactionState owner = FindState(t[2]);
            if (owner == null) return "No faction '" + t[2] + "'.";

            string sub = t.Length > 3 ? t[3] : "list";
            switch (sub.ToLowerInvariant())
            {
                case "list":
                    return ListFleetDoctrineForPlayer(owner);

                case "add":
                {
                    int count;
                    if (t.Length < 6 || !int.TryParse(t[5], out count) || count <= 0)
                        return "Usage: factions fleet <tag> add <prefabId> <count> [concurrent=<n>] [node=<nodeId>]";
                    int concurrent = 1;
                    string scopeNodeId = null;
                    string optionError;
                    if (!TryReadDoctrineOptions(t, 6, ref concurrent, ref scopeNodeId, out optionError))
                        return optionError;
                    return AddDoctrineLineForPlayer(owner, t[4], count, concurrent, scopeNodeId);
                }

                case "remove":
                    if (t.Length < 5) return "Usage: factions fleet <tag> remove <lineId>";
                    return RemoveDoctrineLineForPlayer(owner, t[4]);

                case "priority":
                {
                    int rank;
                    if (t.Length < 6 || !int.TryParse(t[5], out rank))
                        return "Usage: factions fleet <tag> priority <lineId> <n>";
                    return SetDoctrineLinePriorityForPlayer(owner, t[4], rank);
                }

                case "target":
                {
                    int target;
                    if (t.Length < 6 || !int.TryParse(t[5], out target))
                        return "Usage: factions fleet <tag> target <lineId> <n>";
                    return SetDoctrineLineTargetForPlayer(owner, t[4], target);
                }

                case "concurrent":
                {
                    int concurrency;
                    if (t.Length < 6 || !int.TryParse(t[5], out concurrency))
                        return "Usage: factions fleet <tag> concurrent <lineId> <n>";
                    return SetDoctrineLineConcurrencyForPlayer(owner, t[4], concurrency);
                }

                case "pause":
                    if (t.Length < 5) return "Usage: factions fleet <tag> pause <lineId>";
                    return SetDoctrineLinePausedForPlayer(owner, t[4], true);

                case "resume":
                    if (t.Length < 5) return "Usage: factions fleet <tag> resume <lineId>";
                    return SetDoctrineLinePausedForPlayer(owner, t[4], false);

                default:
                    return "fleet: unknown sub-verb '" + sub
                        + "'. Expected list, add, remove, priority, target, concurrent, pause or resume.";
            }
        }

        /// <summary>Reads the optional `concurrent=` / `node=` tail of a doctrine `add`, from
        /// <paramref name="firstIndex"/> on. An unrecognised token is REFUSED rather than ignored:
        /// silently dropping a typo'd option would create a line that is not the one the player asked
        /// for. Shared by the chat verb and the bridge verb so the two grammars cannot drift.</summary>
        public static bool TryReadDoctrineOptions(IList<string> tokens, int firstIndex,
            ref int concurrent, ref string scopeNodeId, out string error)
        {
            for (int i = firstIndex; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (string.IsNullOrEmpty(token)) continue;
                int eq = token.IndexOf('=');
                string key = eq > 0 ? token.Substring(0, eq) : token;
                string value = eq > 0 && eq < token.Length - 1 ? token.Substring(eq + 1) : null;
                if (value == null)
                {
                    error = "'" + token + "' isn't a doctrine option. Expected concurrent=<n> or node=<nodeId>.";
                    return false;
                }

                if (key.Equals("node", StringComparison.OrdinalIgnoreCase))
                    scopeNodeId = value.Equals("any", StringComparison.OrdinalIgnoreCase) ? null : value;
                else if (key.Equals("concurrent", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, out concurrent) || concurrent <= 0)
                    {
                        error = "concurrent= must be a positive number.";
                        return false;
                    }
                }
                else
                {
                    error = "'" + key + "' isn't a doctrine option. Expected concurrent=<n> or node=<nodeId>.";
                    return false;
                }
            }
            error = null;
            return true;
        }

        /// <summary>Enforces <see cref="FactionTuning.FleetDoctrineCap"/> at the only place lines are
        /// created. The list is persisted, so it is bounded per the repo's own rule; the cap is not a
        /// balance knob and the message says so rather than pretending the limit is a game rule.</summary>
        private bool TryReserveDoctrineSlot(FactionState owner, out string error)
        {
            if (owner.FleetDoctrine.Count >= _tuning.FleetDoctrineCap)
            {
                error = "[" + owner.Tag + "] already holds " + owner.FleetDoctrine.Count
                    + " doctrine lines (limit " + _tuning.FleetDoctrineCap
                    + "). Remove one with !factions fleet remove <id>.";
                return false;
            }
            error = null;
            return true;
        }

        private bool TryResolveDoctrineLine(FactionState owner, string lineId, out DoctrineLine line, out string error)
        {
            line = null;
            if (_world == null) { error = "Factions runtime not ready."; return false; }
            if (owner == null) { error = "Your faction is not registered with Factions yet."; return false; }
            if (string.IsNullOrEmpty(lineId)) { error = "Which line? !factions fleet lists them by id."; return false; }

            line = owner.FindDoctrineLine(lineId);
            if (line == null)
            {
                error = "[" + owner.Tag + "] has no doctrine line '" + lineId + "'. !factions fleet lists them.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>One chat line per doctrine line. The counts come from the same Core counters the
        /// manager evaluates against, never a second implementation -- a listing that computed them
        /// its own way could tell a player a line was satisfied while the shipyard kept building.</summary>
        private string DescribeDoctrineLine(FactionState owner, DoctrineLine line)
        {
            var sb = new StringBuilder();
            sb.Append(line.Id).Append("  rank=").Append(line.Priority);
            if (line.Paused) sb.Append("  PAUSED");
            sb.Append("  ").Append(line.PrefabId);
            if (_world != null)
            {
                List<string> fits = ShipyardManager.RolesFitting(_world.PrefabCatalogue, line.PrefabId);
                sb.Append("  fits=").Append(fits.Count > 0 ? string.Join("/", fits) : "none");
            }
            sb.Append("  target=").Append(line.TargetCount)
              .Append("  at=").Append(string.IsNullOrEmpty(line.ScopeNodeId) ? "any" : line.ScopeNodeId)
              .Append("  concurrent=").Append(line.MaxConcurrent > 0 ? line.MaxConcurrent : 1);
            if (_world != null)
            {
                long flying = ShipyardManager.CountDoctrineHullsInFlight(owner, line, _world.PrefabCatalogue);
                long repair = ShipyardManager.CountDoctrineHullsInRepair(_world, owner.Tag, line);
                sb.Append("  have=").Append(ShipyardManager.CountDoctrineHave(_world, owner, line))
                  .Append(" (flying ").Append(flying);
                if (repair > 0) sb.Append(", in repair ").Append(repair);
                sb.Append(')');
            }
            return sb.ToString();
        }

        // ---- Player shelf: what this faction's shop sells (FAC-322) -----------------------------

        /// <summary>`!factions store` — the shelf, one line per row, plus the on/off state. The
        /// counts come from the same Core counters <see cref="StoreStockManager"/> evaluates against,
        /// never a second implementation.</summary>
        public string ListShelfForPlayer(FactionState owner)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            var sb = new StringBuilder();
            if (owner.StoreShelfOptOut)
                sb.AppendLine("Shelf restocking is OFF for [" + owner.Tag
                              + "]. What is already on sale stays on sale. !factions store on to resume.");
            if (owner.Shelf.Count == 0)
            {
                sb.Append("[").Append(owner.Tag).Append("] names no hulls, so Factions picks what the shop stocks. ")
                  .Append("!factions store add <prefabId> <count> to choose.");
                return sb.ToString();
            }
            foreach (ShelfLine line in owner.Shelf) sb.AppendLine(DescribeShelfLine(owner, line));
            sb.Append(owner.Shelf.Count).Append('/').Append(_tuning.PlayerShelfCap)
              .Append(" shelf lines. While this list is non-empty it is the only thing that stocks your shop.");
            return sb.ToString();
        }

        /// <summary>`!factions store add &lt;prefabId&gt; &lt;count&gt;`. No role argument, unlike a
        /// doctrine line: the role a shelf build is filed under is the hull's own capability class
        /// (<see cref="StoreShelfPlan.ClassOf"/>), and asking a player to name it would let them file
        /// a drill hull as cargo and then wonder why the shop shows it under the wrong heading.</summary>
        public string AddShelfLineForPlayer(FactionState owner, string prefabId, int targetCount)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(prefabId) || targetCount <= 0)
                return "Usage: !factions store add <prefabId> <count>";

            string role = StoreShelfPlan.ClassOf(_world.PrefabCatalogue, prefabId);
            if (role == null)
                return "'" + prefabId + "' has no cargo, weapons or drills the catalogue can see, so nothing "
                    + "would ever class it as sellable stock.";

            if (_world.IsPrefabBanned(prefabId)) return BannedRefusal(prefabId);

            if (!ShipyardManager.HasBuildRecipe(_world.Recipes, prefabId))
                return "No build recipe for prefab '" + prefabId + "' — nothing could ever be built for it.";

            if (owner.ShelfLineFor(prefabId) != null)
                return "[" + owner.Tag + "] already sells '" + prefabId
                    + "'. Change how many with !factions store target <id> <n>.";

            if (owner.Shelf.Count >= _tuning.PlayerShelfCap)
                return "[" + owner.Tag + "] already holds " + owner.Shelf.Count
                    + " shelf lines (limit " + _tuning.PlayerShelfCap
                    + "). Remove one with !factions store remove <id>.";

            bool wasInactive = owner.Shelf.Count == 0;
            var line = new ShelfLine
            {
                Id = owner.MintShelfLineId(),
                PrefabId = prefabId,
                TargetCount = targetCount,
                CreatedUtc = _world.UtcNow,
            };
            owner.AddShelfLine(line);
            Flush();
            _log.Write("SHELF_LINE_ADDED_BY_PLAYER " + owner.Tag + " " + line.Id + " prefab=" + prefabId
                       + " role=" + role + " target=" + targetCount);

            string text = "Shelf line " + line.Id + " added: " + DescribeShelfLine(owner, line);
            // Said once, on the line that flips it: from here the class balance and the small/large
            // slot targets stop applying to this faction.
            if (wasInactive && owner.IsPlayerFaction)
                text += Environment.NewLine
                    + "Your shelf is now the only thing that stocks [" + owner.Tag
                    + "]'s shop. A hull with no line is not stocked.";
            return text;
        }

        /// <summary>`!factions store remove &lt;id&gt;`. The hulls already on sale are handed back by
        /// <see cref="StoreShelfRelease"/>'s next pass, over its own budget, rather than un-listed
        /// here: releasing stock is that pass's job and doing it twice is how a shelf drains.</summary>
        public string RemoveShelfLineForPlayer(FactionState owner, string lineId)
        {
            ShelfLine line;
            string error;
            if (!TryResolveShelfLine(owner, lineId, out line, out error)) return error;

            owner.RemoveShelfLine(line);
            Flush();
            _log.Write("SHELF_LINE_REMOVED_BY_PLAYER " + owner.Tag + " " + line.Id
                       + " prefab=" + (line.PrefabId ?? "?"));
            return "Shelf line " + line.Id + " (" + line.PrefabId + ") removed. Copies already on sale are "
                + "handed back over the next few store passes."
                + (owner.Shelf.Count == 0
                    ? Environment.NewLine + "That was the last line, so Factions picks what the shop stocks again."
                    : "");
        }

        /// <summary>`!factions store target &lt;id&gt; &lt;n&gt;`.</summary>
        public string SetShelfLineTargetForPlayer(FactionState owner, string lineId, int targetCount)
        {
            ShelfLine line;
            string error;
            if (!TryResolveShelfLine(owner, lineId, out line, out error)) return error;
            if (targetCount < 0) return "A shelf target cannot be negative. Use 0, or remove the line.";

            owner.SetShelfLineTarget(line, targetCount);
            Flush();
            _log.Write("SHELF_LINE_TARGET_BY_PLAYER " + owner.Tag + " " + line.Id + " target=" + targetCount);
            return "Shelf line " + line.Id + " target set to " + targetCount + ": " + DescribeShelfLine(owner, line);
        }

        /// <summary>`!factions store pause|resume &lt;id&gt;`. A paused line keeps what it holds --
        /// it stops restocking, which is not the same as handing stock back.</summary>
        public string SetShelfLinePausedForPlayer(FactionState owner, string lineId, bool paused)
        {
            ShelfLine line;
            string error;
            if (!TryResolveShelfLine(owner, lineId, out line, out error)) return error;

            owner.SetShelfLinePaused(line, paused);
            Flush();
            _log.Write("SHELF_LINE_" + (paused ? "PAUSED" : "RESUMED") + "_BY_PLAYER "
                       + owner.Tag + " " + line.Id);
            return "Shelf line " + line.Id + (paused ? " paused: " : " resumed: ") + DescribeShelfLine(owner, line);
        }

        /// <summary>`!factions store on|off` — the switch a list cannot express. Off refunds every
        /// store build in flight and stops raising more; it does not un-list what is already for
        /// sale, because the player stopped ordering stock, not offering it.</summary>
        public string SetStoreShelfOptOutForPlayer(FactionState owner, bool optOut)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            owner.SetStoreShelfOptOut(optOut);
            Flush();
            _log.Write("SHELF_STOCKING_" + (optOut ? "OFF" : "ON") + "_BY_PLAYER " + owner.Tag);
            return optOut
                ? "Shelf restocking is OFF for [" + owner.Tag + "]. Builds in flight for the shop are refunded "
                  + "on the next store pass; what is already on sale stays on sale."
                : "Shelf restocking is ON for [" + owner.Tag + "]."
                  + (owner.Shelf.Count == 0
                      ? " No shelf lines, so Factions picks what the shop stocks."
                      : " Your " + owner.Shelf.Count + " shelf line(s) decide what it stocks.");
        }

        /// <summary>The bridge twin of the `store` family, for the same reason
        /// <see cref="FleetDoctrineAdmin"/> exists: the headless test server has no client and
        /// `HandleCommandFromServer` no-ops the chat verbs. Every mutation routes into the same
        /// player-facing method the chat verb calls.</summary>
        public string ShelfAdmin(string[] t)
        {
            if (_world == null) return "store: runtime not started (no session loaded).";
            if (t == null || t.Length < 3)
                return "Usage: factions store <tag> [list|add|remove|target|pause|resume|on|off] ...";

            FactionState owner = FindState(t[2]);
            if (owner == null) return "No faction '" + t[2] + "'.";

            string sub = t.Length > 3 ? t[3] : "list";
            switch (sub.ToLowerInvariant())
            {
                case "list":
                    return ListShelfForPlayer(owner);

                case "add":
                {
                    int count;
                    if (t.Length < 6 || !int.TryParse(t[5], out count) || count <= 0)
                        return "Usage: factions store <tag> add <prefabId> <count>";
                    return AddShelfLineForPlayer(owner, t[4], count);
                }

                case "remove":
                    if (t.Length < 5) return "Usage: factions store <tag> remove <lineId>";
                    return RemoveShelfLineForPlayer(owner, t[4]);

                case "target":
                {
                    int target;
                    if (t.Length < 6 || !int.TryParse(t[5], out target))
                        return "Usage: factions store <tag> target <lineId> <n>";
                    return SetShelfLineTargetForPlayer(owner, t[4], target);
                }

                case "pause":
                    if (t.Length < 5) return "Usage: factions store <tag> pause <lineId>";
                    return SetShelfLinePausedForPlayer(owner, t[4], true);

                case "resume":
                    if (t.Length < 5) return "Usage: factions store <tag> resume <lineId>";
                    return SetShelfLinePausedForPlayer(owner, t[4], false);

                case "off": return SetStoreShelfOptOutForPlayer(owner, true);
                case "on":  return SetStoreShelfOptOutForPlayer(owner, false);

                default:
                    return "store: unknown sub-verb '" + sub
                        + "'. Expected list, add, remove, target, pause, resume, on or off.";
            }
        }

        private bool TryResolveShelfLine(FactionState owner, string lineId, out ShelfLine line, out string error)
        {
            line = null;
            if (_world == null) { error = "Factions runtime not ready."; return false; }
            if (owner == null) { error = "Your faction is not registered with Factions yet."; return false; }
            if (string.IsNullOrEmpty(lineId)) { error = "Which line? !factions store lists them by id."; return false; }

            for (int i = 0; i < owner.Shelf.Count; i++)
                if (string.Equals(owner.Shelf[i].Id, lineId, StringComparison.OrdinalIgnoreCase))
                {
                    line = owner.Shelf[i];
                    error = null;
                    return true;
                }

            error = "[" + owner.Tag + "] has no shelf line '" + lineId + "'. !factions store lists them.";
            return false;
        }

        /// <summary>One chat line per shelf line, counted by the same Core helpers the manager uses.
        /// <c>on_sale</c> is <see cref="SpatialNode.ListedForSale"/> across the estate, which is the
        /// shelf's "have" -- deliberately not ship stock, since a hull the fleet owns is not for
        /// sale.</summary>
        private string DescribeShelfLine(FactionState owner, ShelfLine line)
        {
            var sb = new StringBuilder();
            sb.Append(line.Id);
            if (line.Paused) sb.Append("  PAUSED");
            sb.Append("  ").Append(line.PrefabId).Append("  target=").Append(line.TargetCount);
            if (_world != null)
            {
                var owned = new List<SpatialNode>(_world.Graph.OwnedBy(owner.Tag));
                sb.Append("  on_sale=").Append(StoreStockManager.CountListed(owned, line.PrefabId, _world.PrefabCatalogue))
                  .Append("  building=").Append(StoreStockManager.CountShelfBuildsInProgress(owned, line.PrefabId, _world.PrefabCatalogue));
                string role = StoreShelfPlan.ClassOf(_world.PrefabCatalogue, line.PrefabId);
                sb.Append("  as=").Append(role ?? "unclassed");
            }
            return sb.ToString();
        }

        // ---- Player campaigns (docs/campaigns.md -> A player faction) ---------------------------

        /// <summary>Places a raid or conquest directive for a player faction on <paramref name="nodeId"/>
        /// (or the region it stands in). Every refusal is <see cref="OffensiveCampaigns.TryStartPlayer"/>'s
        /// own; this only adds the persistence flush and the decision-log line. <paramref name="knowledge"/>
        /// is the viewer's knowledge exactly as the map builds it — the player's scanner store plus
        /// their faction's — so a node the map would not draw cannot be named here either.</summary>
        public string PlacePlayerCampaign(FactionState owner, string nodeId, CampaignObjectiveKind kind,
            bool regionScope, IList<FactionKnowledge> knowledge, out CampaignFocus placed)
        {
            placed = null;
            if (owner == null || !owner.IsPlayerFaction) return "Your faction is not registered with Factions yet.";
            string word = OffensiveCampaigns.KindWord(kind);
            OffensiveCampaigns.PlayerResult result;
            if (kind == CampaignObjectiveKind.DefendRegion)
            {
                // The defensive twin (FAC-309 slice 5): the region is the same neighbourhood a brain
                // nomination gets, computed here because IWorldView does not expose the BFS. The
                // wire's region-scope flag is not read -- a picket is a region by definition.
                HashSet<string> members = _world.ComputeCampaignRegionMembers(owner.Tag, nodeId, _tuning.CampaignRegionTraverseDepth);
                result = CampaignFocusLifecycle.TryStartPlayer(owner, _tuning, _world.UtcNow, _world, nodeId, members, out placed);
            }
            else
            {
                result = OffensiveCampaigns.TryStartPlayer(
                    owner, _tuning, _world.UtcNow, _world, nodeId, kind, regionScope, knowledge, out placed);
            }
            if (result != OffensiveCampaigns.PlayerResult.Ok)
            {
                _log.Write("PLAYER_CAMPAIGN REFUSED " + owner.Tag + " kind=" + word + " node=" + (nodeId ?? "-")
                           + " scope=" + (regionScope ? "region" : "node") + " reason=" + result);
                return DescribePlayerCampaignRefusal(result, kind);
            }
            Flush();
            CampaignObjective objective = placed.Objectives.Count > 0 ? placed.Objectives[0] : null;
            if (kind == CampaignObjectiveKind.DefendRegion)
            {
                _log.Write("PLAYER_CAMPAIGN PLACED " + owner.Tag + " kind=defend node=" + placed.CenterNodeId
                           + " region=- focus=" + placed.Id + " staging=" + placed.CenterNodeId
                           + " members=" + placed.RegionMembers.Count
                           + " until=" + placed.ExpiresAtUtc.ToString("HH:mm:ss"));
                return "Defensive focus placed: " + placed.Id + ". Your fleet keeps a patrol circuit over "
                       + placed.RegionMembers.Count + " of your station(s) around " + placed.CenterNodeId
                       + " whenever it has combat hulls and the treasury covers the cost.";
            }
            _log.Write("PLAYER_CAMPAIGN PLACED " + owner.Tag + " kind=" + word
                       + " node=" + (objective != null && objective.TargetNodeId != null ? objective.TargetNodeId : "-")
                       + " region=" + (objective != null ? (NodeRegion.FromObjective(objective).ToKey() ?? "-") : "-")
                       + " focus=" + placed.Id + " staging=" + placed.CenterNodeId
                       + " until=" + placed.ExpiresAtUtc.ToString("HH:mm:ss"));
            return (kind == CampaignObjectiveKind.SeizeRegion ? "Conquest" : "Raid") + " campaign placed: "
                   + placed.Id + ". Your fleet dispatches from " + placed.CenterNodeId
                   + " whenever it has combat hulls and the treasury covers the cost.";
        }

        /// <summary>Cancels one of the player's own directives by focus id. A brain-nominated focus
        /// on the same faction is not reachable from here, by design.</summary>
        public string CancelPlayerCampaign(FactionState owner, string focusId, out CampaignFocus removed)
        {
            removed = null;
            if (owner == null || !owner.IsPlayerFaction) return "Your faction is not registered with Factions yet.";
            OffensiveCampaigns.PlayerResult result = OffensiveCampaigns.CancelPlayer(owner, focusId, out removed);
            if (result != OffensiveCampaigns.PlayerResult.Ok) return "No campaign of yours with id " + focusId + ".";
            Flush();
            _log.Write("PLAYER_CAMPAIGN CANCELLED " + owner.Tag + " focus=" + removed.Id);
            return "Campaign " + removed.Id + " cancelled.";
        }

        private string DescribePlayerCampaignRefusal(OffensiveCampaigns.PlayerResult result, CampaignObjectiveKind kind)
        {
            switch (result)
            {
                case OffensiveCampaigns.PlayerResult.UnknownNode: return "No such node.";
                case OffensiveCampaigns.PlayerResult.OwnNode: return "That node is already yours.";
                case OffensiveCampaigns.PlayerResult.NotKnown: return "Your faction does not know that node yet.";
                case OffensiveCampaigns.PlayerResult.PactPartner: return "Its owner is your ally; break the pact first.";
                case OffensiveCampaigns.PlayerResult.CapReached:
                    return "Your faction already holds " + _tuning.PlayerCampaignCap + " campaigns; cancel one first.";
                case OffensiveCampaigns.PlayerResult.Duplicate: return "A campaign of that kind already covers that place.";
                case OffensiveCampaigns.PlayerResult.NoRegion: return "That node stands in no region a campaign can name; target the node itself.";
                case OffensiveCampaigns.PlayerResult.NoStaging: return "Your faction owns no node to stage from.";
                case OffensiveCampaigns.PlayerResult.BadKind: return "Not a raid and not a conquest.";
                case OffensiveCampaigns.PlayerResult.NotOwnNode: return "A defensive focus needs one of your own stations at its centre.";
                default: return "Factions is not ready for that yet.";
            }
        }

        /// <summary>Removes a standing order for `!factions station orders remove &lt;orderId&gt;`.
        /// The id is never reused (see <see cref="FactionState.MintStandingOrderId"/>), so removing
        /// one cannot renumber the others.</summary>
        public string RemoveStandingOrderForPlayer(FactionState owner, string orderId)
        {
            StandingOrder order;
            string error;
            if (!TryResolveStandingOrder(owner, orderId, out order, out error)) return error;

            owner.RemoveStandingOrder(order);
            if (order.Kind == StandingOrderKind.Stock) StockOrders.Publish(owner, _world);
            Flush();
            _log.Write("STANDING_ORDER_REMOVED_BY_PLAYER " + owner.Tag + " " + order.Id + " kind=" + order.Kind);
            return "Standing order " + order.Id + " removed.";
        }

        /// <summary>Ranks a standing order for `!factions station orders priority &lt;orderId&gt;
        /// &lt;n&gt;`. Higher is evaluated first, and evaluated first is what wins a catchment deposit
        /// two Mine orders both reach or a shipyard two Build goals both want. Deliberately the SAME
        /// scale as <see cref="BuildQueueEntry.Priority"/> and <see cref="FactionState.InstallPriority"/>:
        /// a build a goal places competes for components with the builds and capability installs
        /// already there.</summary>
        public string SetStandingOrderPriorityForPlayer(FactionState owner, string orderId, int rank)
        {
            StandingOrder order;
            string error;
            if (!TryResolveStandingOrder(owner, orderId, out order, out error)) return error;

            int previous = order.Priority;
            owner.SetStandingOrderPriority(order, rank);
            Flush();
            _log.Write("STANDING_ORDER_PRIORITY_BY_PLAYER " + owner.Tag + " " + order.Id
                       + " from=" + previous + " to=" + rank);
            return "Standing order " + order.Id + " ranked " + rank + " (was " + previous + "): " + DescribeStandingOrder(owner.Tag, order);
        }

        /// <summary>Pauses or resumes a standing order for `!factions station orders
        /// pause|resume &lt;orderId&gt;`. The order keeps its id and rank and stops being evaluated —
        /// distinct from removing it, the same way pausing a shipyard is distinct from cancelling its
        /// build.</summary>
        public string SetStandingOrderPausedForPlayer(FactionState owner, string orderId, bool paused)
        {
            StandingOrder order;
            string error;
            if (!TryResolveStandingOrder(owner, orderId, out order, out error)) return error;

            if (order.Paused == paused)
                return "Standing order " + order.Id + " is already " + (paused ? "paused." : "running.");
            owner.SetStandingOrderPaused(order, paused);
            // Resuming re-arms the scan cursor: a Mine order paused for an hour should scan now, not
            // sit until a NextScanUtc computed before the pause finally comes round.
            if (!paused && order.Kind == StandingOrderKind.Mine && _world != null) order.NextScanUtc = _world.UtcNow;
            if (order.Kind == StandingOrderKind.Stock) StockOrders.Publish(owner, _world);
            Flush();
            _log.Write("STANDING_ORDER_PAUSED_BY_PLAYER " + owner.Tag + " " + order.Id + " paused=" + paused);
            return "Standing order " + order.Id + (paused ? " paused." : " resumed.");
        }

        /// <summary>Bridge verb `factions orders &lt;tag&gt; [list|mine|stock|remove|priority|pause|resume] …`
        /// — the admin twin of the player's own `!factions station orders`, exactly as
        /// <see cref="CancelBuildAdmin"/>/<see cref="PauseShipyardAdmin"/>/<see cref="SetBuildPriorityAdmin"/>
        /// are the admin twins of the `queue` verbs.
        ///
        /// <para>It exists for the same reason those do: a player-faction feature cannot be exercised
        /// on the headless test server at all otherwise. `HandleCommandFromServer` silently no-ops the
        /// chat commands (the null server-player fails the permission check), and the standing-order
        /// verbs additionally resolve the CALLER's faction, which a bridge command has none of. Every
        /// mutation routes into the same player-facing method the chat verb calls — this is an entry
        /// point, never a second implementation, so verifying through it verifies the real path.</para></summary>
        public string StandingOrdersAdmin(string[] t)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (t == null || t.Length < 3)
                return "Usage: factions orders <tag> [list|mine|stock|remove|priority|pause|resume] ...";

            FactionState owner = FindState(t[2]);
            if (owner == null) return "No faction '" + t[2] + "'.";

            string sub = t.Length > 3 ? t[3] : "list";
            switch (sub.ToLowerInvariant())
            {
                case "list":
                    return ListStandingOrdersForPlayer(owner);

                case "mine":
                {
                    double period;
                    if (t.Length < 6 || !double.TryParse(t[5], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out period) || period <= 0)
                        return "Usage: factions orders <tag> mine <hubNodeId> <periodSeconds> [oreWeights]";
                    return AddStandingMineOrderForPlayer(owner, t[4], period, t.Length > 6 ? t[6] : null);
                }

                case "stock":
                {
                    long quantity;
                    if (t.Length < 7 || !long.TryParse(t[6], out quantity) || quantity <= 0)
                        return "Usage: factions orders <tag> stock <nodeId|all> <item> <quantity>";
                    return AddStandingStockOrderForPlayer(owner, t[4], t[5], quantity);
                }

                case "build":
                    // Retired, not silently renamed: a script or a player that still sends this must
                    // be told where the feature went rather than get an "unknown sub-verb".
                    return "orders build is retired — fleet composition is its own list now. "
                        + "Use: factions fleet " + owner.Tag + " add <prefabId> <count> [concurrent=] [node=]";

                case "remove":
                    if (t.Length < 5) return "Usage: factions orders <tag> remove <orderId>";
                    return RemoveStandingOrderForPlayer(owner, t[4]);

                case "priority":
                {
                    int rank;
                    if (t.Length < 6 || !int.TryParse(t[5], out rank))
                        return "Usage: factions orders <tag> priority <orderId> <n>";
                    return SetStandingOrderPriorityForPlayer(owner, t[4], rank);
                }

                case "pause":
                    if (t.Length < 5) return "Usage: factions orders <tag> pause <orderId>";
                    return SetStandingOrderPausedForPlayer(owner, t[4], true);

                case "resume":
                    if (t.Length < 5) return "Usage: factions orders <tag> resume <orderId>";
                    return SetStandingOrderPausedForPlayer(owner, t[4], false);

                default:
                    return "orders: unknown sub-verb '" + sub + "'. Expected list, mine, stock, remove, priority, pause or resume.";
            }
        }

        /// <summary>The nodes a standing order could name, for the client's create dialog: every
        /// node <paramref name="owner"/> holds, flagged with what it is eligible for. Eligibility is
        /// decided here, server-side, by the same rules the scan and <c>QueueBuild</c> apply — the
        /// client has no graph and must not be left to guess.</summary>
        public List<KeyValuePair<SpatialNode, int>> StandingOrderNodeOptions(FactionState owner)
        {
            var result = new List<KeyValuePair<SpatialNode, int>>();
            if (_world == null || owner == null) return result;

            var owned = new List<SpatialNode>(_world.Graph.OwnedBy(owner.Tag));
            foreach (SpatialNode node in owned)
            {
                int flags = 0;
                if (OperationsManager.HasMineCatchment(node, owned, _tuning)) flags |= 1;   // MineHub
                int shipyardLevel;
                if (node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel)
                    && shipyardLevel >= 1)
                    flags |= 2;                                                             // Shipyard
                if (flags != 0) result.Add(new KeyValuePair<SpatialNode, int>(node, flags));
            }
            return result;
        }

        /// <summary>The owned yards a doctrine line may be scoped to: a Shipyard at level 1 or
        /// better, which is exactly what <see cref="AddDoctrineLineForPlayer"/> requires of a
        /// <c>node=</c>. A narrowing of <see cref="StandingOrderNodeOptions"/>'s Shipyard flag rather
        /// than a second eligibility rule.</summary>
        public List<SpatialNode> DoctrineScopeNodeOptions(FactionState owner)
        {
            var result = new List<SpatialNode>();
            if (_world == null || owner == null) return result;
            foreach (SpatialNode node in _world.Graph.OwnedBy(owner.Tag))
            {
                int shipyardLevel;
                if (node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel)
                    && shipyardLevel >= 1)
                    result.Add(node);
            }
            result.Sort(delegate (SpatialNode a, SpatialNode b)
            {
                return string.CompareOrdinal(a.Id, b.Id);
            });
            return result;
        }

        /// <summary>The hull pick-list for the Fleet window's new-line dialog: every hull a doctrine
        /// line could name, described by <see cref="BuildOptionsCatalogue"/> exactly as the Build
        /// dialog's is.
        ///
        /// <para>Narrower than <see cref="BuildOptionsForPlayer"/> by one hull class, and for the
        /// opposite reason: a hull with no build recipe is dropped here because a line naming one
        /// would sit in the player's doctrine looking healthy and never place anything, whereas the
        /// Build dialog queues once and reports the refusal immediately. Both checks are made again
        /// in <see cref="AddDoctrineLineForPlayer"/> — this list is a convenience, never an
        /// authorisation.</para>
        ///
        /// <para><c>CanFlyFromHere</c> on these rows means "can leave AT LEAST ONE owned yard": a
        /// line is placed per yard and skips the ones the hull cannot leave, so the only thing worth
        /// warning about before the line exists is a hull that can leave none of them.</para></summary>
        public List<BuildPrefabOption> DoctrineHullOptions(FactionState owner)
        {
            var result = new List<BuildPrefabOption>();
            if (_world == null || owner == null || _world.PrefabCatalogue == null) return result;

            var candidates = new List<KeyValuePair<string, string>>();
            foreach (string role in ShipyardManager.PlayerQueueRoles)
            {
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_shipyardManager.PrefabListPicker != null)
                {
                    IList<string> picked = _shipyardManager.PrefabListPicker(role, owner.Tag);
                    if (picked != null)
                        foreach (string id in picked) names.Add(id);
                }
                IReadOnlyList<string> universal = PrefabOwnershipFilter.Apply(
                    _world.PrefabCatalogue.GetNamesForRole(role, "All"), _world.PrefabCatalogue, owner.Tag);
                foreach (string id in universal) names.Add(id);

                foreach (string id in names)
                {
                    // Only what a line could actually build: AddDoctrineLineForPlayer checks both,
                    // so offering a hull that fails either would be offering a refusal.
                    if (!ShipPool.IsSuitableForRole(role, _world.PrefabCatalogue, id)) continue;
                    if (!ShipyardManager.HasBuildRecipe(_world.Recipes, id)) continue;
                    candidates.Add(new KeyValuePair<string, string>(role, id));
                }
            }

            return BuildOptionsCatalogue.AssembleAcrossYards(
                _world, _tuning, DoctrineScopeNodeOptions(owner), candidates);
        }

        /// <summary>The Build dialog's hull pick-list for one owned shipyard: the same candidate set
        /// <see cref="ListPrefabsForPlayer"/> prints for the chat verb, described by
        /// <see cref="BuildOptionsCatalogue"/>.
        ///
        /// <para>Wider than <see cref="DoctrineHullOptions"/> by one hull class on purpose:
        /// that list drops a prefab with no build recipe because a doctrine line naming one would sit
        /// in the player's list looking healthy and never place anything, whereas this dialog queues
        /// once and immediately, and reports what happened. The recipe check still runs — in
        /// <see cref="ShipyardManager.QueueBuild"/>, where it always did.</para>
        ///
        /// <para>Empty for a node this faction does not own or that has no Shipyard: the dialog has
        /// nothing to offer there, and the same two checks are made again when the build is
        /// queued.</para></summary>
        public List<BuildPrefabOption> BuildOptionsForPlayer(FactionState owner, string nodeId)
        {
            var empty = new List<BuildPrefabOption>();
            if (_world == null || owner == null || _world.PrefabCatalogue == null) return empty;
            if (string.IsNullOrEmpty(nodeId)) return empty;

            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) return empty;
            if (!string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase)) return empty;
            int shipyardLevel;
            if (!node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel) || shipyardLevel < 1)
                return empty;

            var candidates = new List<KeyValuePair<string, string>>();
            foreach (string role in ShipyardManager.PlayerQueueRoles)
            {
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_shipyardManager.PrefabListPicker != null)
                {
                    IList<string> picked = _shipyardManager.PrefabListPicker(role, owner.Tag);
                    if (picked != null)
                        foreach (string id in picked) names.Add(id);
                }
                IReadOnlyList<string> universal = PrefabOwnershipFilter.Apply(
                    _world.PrefabCatalogue.GetNamesForRole(role, "All"), _world.PrefabCatalogue, owner.Tag);
                foreach (string id in universal) names.Add(id);
                foreach (string id in names) candidates.Add(new KeyValuePair<string, string>(role, id));
            }

            return BuildOptionsCatalogue.Assemble(_world, _tuning, node, candidates);
        }

        /// <summary>Mine orders anchored at <paramref name="hubNodeId"/>, in rank order.</summary>
        private static List<StandingOrder> MineOrdersAtHub(FactionState owner, string hubNodeId)
        {
            var result = new List<StandingOrder>();
            foreach (StandingOrder o in owner.StandingOrdersByRank(StandingOrderKind.Mine))
                if (string.Equals(o.HubNodeId, hubNodeId, StringComparison.OrdinalIgnoreCase)) result.Add(o);
            return result;
        }

        private StandingOrder NewMineOrder(FactionState owner, string hubNodeId, double periodSeconds,
            Dictionary<string, double> oreWeights)
        {
            var order = new StandingOrder
            {
                Id = owner.MintStandingOrderId(),
                Kind = StandingOrderKind.Mine,
                CreatedUtc = _world.UtcNow,
                HubNodeId = hubNodeId,
                PeriodSeconds = periodSeconds,
                OreWeights = oreWeights,
                NextScanUtc = _world.UtcNow,
            };
            owner.AddStandingOrder(order);
            return order;
        }

        /// <summary>Enforces <see cref="FactionTuning.StandingOrderCap"/> at the only place orders are
        /// created. The list is persisted, so it is bounded per the repo's own rule; the cap is not a
        /// balance knob and the message says so rather than pretending the limit is a game rule.</summary>
        private bool TryReserveStandingOrderSlot(FactionState owner, out string error)
        {
            if (owner.StandingOrders.Count >= _tuning.StandingOrderCap)
            {
                error = "[" + owner.Tag + "] already holds " + owner.StandingOrders.Count
                    + " standing orders (limit " + _tuning.StandingOrderCap
                    + "). Remove one with !factions station orders remove <id>.";
                return false;
            }
            error = null;
            return true;
        }

        private bool TryResolveStandingOrder(FactionState owner, string orderId, out StandingOrder order, out string error)
        {
            order = null;
            if (_world == null) { error = "Factions runtime not ready."; return false; }
            if (owner == null) { error = "Your faction is not registered with Factions yet."; return false; }
            if (string.IsNullOrEmpty(orderId)) { error = "Which order? !factions station orders lists them by id."; return false; }

            order = owner.FindStandingOrder(orderId);
            if (order == null)
            {
                error = "[" + owner.Tag + "] has no standing order '" + orderId + "'. !factions station orders lists them.";
                return false;
            }
            error = null;
            return true;
        }

        private bool TryResolveOwnedNode(FactionState owner, string nodeId, out SpatialNode node, out string error)
        {
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node))
            {
                error = "No node '" + nodeId + "'.";
                return false;
            }
            if (!string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
            {
                error = nodeId + " isn't owned by your faction.";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>One chat line per standing order. <paramref name="ownerTag"/> is unused now that
        /// Mine is the only kind left -- kept on the signature because every call site reads as
        /// "describe THIS faction's order", and the client-facing describer beside it
        /// (<see cref="DescribeDoctrineLine"/>) genuinely needs the faction.</summary>
        private string DescribeStandingOrder(string ownerTag, StandingOrder order)
        {
            var sb = new StringBuilder();
            sb.Append(order.Id).Append("  rank=").Append(order.Priority);
            if (order.Paused) sb.Append("  PAUSED");

            if (order.Kind == StandingOrderKind.Mine)
            {
                sb.Append("  mine hub=").Append(order.HubNodeId)
                  .Append("  period=").Append(order.PeriodSeconds.ToString("0")).Append('s');
                if (order.OreWeights != null && order.OreWeights.Count > 0)
                {
                    sb.Append("  ores=");
                    bool first = true;
                    foreach (var kv in order.OreWeights)
                    {
                        if (!first) sb.Append(',');
                        sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("0"));
                        first = false;
                    }
                }
                if (!order.Paused && _world != null)
                {
                    double secsUntilNext = (order.NextScanUtc - _world.UtcNow).TotalSeconds;
                    sb.Append("  next_scan=").Append(secsUntilNext > 0 ? secsUntilNext.ToString("0") + "s" : "due");
                }
            }
            else if (order.Kind == StandingOrderKind.Stock && order.AllStations)
            {
                sb.Append("  stock ALL stations  item=").Append(order.ItemKey)
                  .Append("  target=").Append(order.TargetQuantity);
                FactionState owner = _world != null ? FindState(ownerTag) : null;
                if (owner != null)
                {
                    long target;
                    List<SpatialNode> applied = StockOrders.AppliedStations(owner, _world.Graph, order, out target);
                    int shortAt = 0;
                    foreach (SpatialNode station in applied)
                    {
                        long have;
                        station.Inventory.TryGetValue(order.ItemKey, out have);
                        if (have < target) shortAt++;
                    }
                    sb.Append("  short at ").Append(shortAt).Append('/').Append(applied.Count).Append(" stations");
                }
            }
            else if (order.Kind == StandingOrderKind.Stock)
            {
                sb.Append("  stock node=").Append(order.NodeId)
                  .Append("  item=").Append(order.ItemKey)
                  .Append("  target=").Append(order.TargetQuantity);
                SpatialNode node;
                if (_world != null && order.NodeId != null && _world.Graph.Nodes.TryGetValue(order.NodeId, out node))
                {
                    if (!string.Equals(node.OwnerFactionTag, ownerTag, StringComparison.Ordinal))
                        sb.Append("  (node not held)");
                    else
                    {
                        long have;
                        node.Inventory.TryGetValue(order.ItemKey ?? "", out have);
                        sb.Append("  have=").Append(have);
                    }
                }
            }
            return sb.ToString();
        }

        private const int MaxCapturedPrefabNameLength = 60;

        private const string ShipCaptureUsage  = "!factions ship capture <own|all> <name>";
        private const string LiveryCaptureUsage = "!factions livery capture <tag> <name>";
        private const string StationScanUsage  = "!factions station scan <own|all> <name>";

        /// <summary>The block a player reaches node inventory and ship assets through. Matched on the
        /// definition subtype, the same test <c>PlayerShipAssetsManager</c> applies to a live block —
        /// here against an object builder, which is all a scan has.</summary>
        private const string ServicesTerminalSubtype = "ServicesTerminal";

        /// <summary>Player-facing capture for `!factions ship capture &lt;own|all&gt; &lt;name&gt;` —
        /// see <see cref="Commands.PlayerShipCommands"/>. Captures the ship <paramref name="player"/>
        /// is looking at into the Factions prefab pool, immediately usable via <see
        /// cref="QueueBuildForPlayer"/> — no session restart required, since none of this touches SE's
        /// own prefab-definition system (see <see cref="CustomPrefabSpawner"/>'s class doc). Scoped to
        /// <paramref name="owner"/>'s faction only, or every faction, per <paramref name="scope"/>.</summary>
        public string CaptureShipForPlayer(FactionState owner, IMyPlayer player, string scope, string name)
        {
            // The name and scope are checked before the raycast so a mistyped verb answers with the
            // usage line instead of "look directly at the hull" -- the shared body re-checks them,
            // which costs a dictionary lookup and keeps one set of rules.
            CaptureTarget ignoredTarget;
            SePrefabCatalogue ignoredCatalogue;
            string refusal;
            if (!TryValidateCaptureRequest(owner, scope, name, ShipCaptureUsage,
                    out ignoredTarget, out ignoredCatalogue, out refusal))
                return refusal;

            List<MyCubeGrid> grids;
            string error;
            if (!ShipCaptureScanner.TryFindTargetedGrids(player, _tuning.ShipCaptureMaxRangeM, out grids, out error))
                return error;

            return CaptureGridsForPlayer(owner, player, grids, scope, name);
        }

        /// <summary>
        /// The capture itself, from a group of grids the caller has already found. Two front ends
        /// reach it and neither owns any of the rules below: `!factions ship capture` raycasts
        /// (<see cref="CaptureShipForPlayer"/>), and the Services Terminal's Scan button resolves an
        /// entity id and checks its own 200 m radius first (FAC-283).
        /// </summary>
        public string CaptureGridsForPlayer(FactionState owner, IMyPlayer player, List<MyCubeGrid> grids,
                                            string scope, string name)
        {
            return CaptureGrids(owner, player, grids, scope, name, null, ShipCaptureUsage);
        }

        /// <summary>Admin capture for `!factions livery capture &lt;tag&gt; &lt;name&gt;`: the ship
        /// <paramref name="admin"/> is looking at, captured for <paramref name="factionTag"/> alone
        /// and under that faction's livery (<see cref="PrefabHintFor"/>; <c>Factorum</c> for FCTM),
        /// so a mandatory-livery faction builds it as its own hull. FAC-480.</summary>
        public string CaptureShipForFaction(IMyPlayer admin, string factionTag, string name)
        {
            if (admin == null) return "Run this in game, looking at the ship.";
            FactionState target = FindFaction(factionTag);
            if (target == null) return "No registered faction [" + (factionTag ?? "") + "].";

            CaptureTarget ignoredTarget;
            SePrefabCatalogue ignoredCatalogue;
            string refusal;
            if (!TryValidateCaptureRequest(target, "own", name, LiveryCaptureUsage,
                    out ignoredTarget, out ignoredCatalogue, out refusal))
                return refusal;

            List<MyCubeGrid> grids;
            string error;
            if (!ShipCaptureScanner.TryFindTargetedGrids(admin, _tuning.ShipCaptureMaxRangeM, out grids, out error))
                return error;

            return CaptureGrids(target, admin, grids, "own", name,
                PrefabHintFor(_factionTypesCatalogue, target.Tag), LiveryCaptureUsage);
        }

        /// <summary>Admin retag for `!factions livery set &lt;prefabId&gt; &lt;tag|all&gt;`: moves a
        /// captured ship design to one faction under its livery, or back to every faction under the
        /// universal one. The catalogue entry and the capture manifest change together. FAC-480.</summary>
        public string SetCaptureLivery(string prefabId, string factionTag)
        {
            var catalogue = _prefabCatalogue as SePrefabCatalogue;
            if (_world == null || catalogue == null || _customPrefabStore == null) return "Factions runtime not ready.";
            string id;
            if (!catalogue.TryGetCanonicalId(prefabId, out id) || !_customPrefabStore.IsCapture(id))
                return "'" + (prefabId ?? "") + "' is not a captured design.";
            if (catalogue.IsStationPrefab(id)) return "'" + id + "' is a station design; a livery applies to ships.";

            string ownerTag = null;
            string hint = PrefabFactionHint.Universal;
            if (!string.Equals(factionTag, "all", StringComparison.OrdinalIgnoreCase))
            {
                FactionState target = FindFaction(factionTag);
                if (target == null) return "No registered faction [" + (factionTag ?? "") + "].";
                ownerTag = target.Tag;
                hint = PrefabHintFor(_factionTypesCatalogue, target.Tag);
            }

            catalogue.SetLivery(id, hint, ownerTag);
            bool written = _customPrefabStore.SetLivery(id, ownerTag, hint);
            CaptureGeneration++;
            RefreshCatalogueSidecar(catalogue);
            FactionsPlugin.Log.Info("SHIP_LIVERY prefab=" + id + " scope=" + (ownerTag ?? "all") + " livery=" + hint
                                    + (written ? "" : " manifest=unwritten"));
            return id + " is now " + (ownerTag == null ? "every faction's, universal livery" : "[" + ownerTag + "]'s alone, livery " + hint)
                   + (written ? "." : ". The capture manifest could not be written: it reverts at the next restart.");
        }

        /// <summary>Rewrites the read-only <c>prefab_catalogue.json</c> sidecar the Dashboard API reads
        /// verbatim (never the live catalogue). Best-effort: the live catalogue already holds the change.</summary>
        private void RefreshCatalogueSidecar(SePrefabCatalogue catalogue)
        {
            if (string.IsNullOrEmpty(_prefabCatalogueStorageDir)) return;
            try { File.WriteAllText(Path.Combine(_prefabCatalogueStorageDir, "prefab_catalogue.json"), catalogue.ToJson()); }
            catch { /* best-effort refresh only -- live catalogue is unaffected */ }
        }

        /// <summary>The ship capture both front ends and the admin verb share. <paramref name="factionHint"/>
        /// is the livery the design is built under; null is the universal one every player capture gets.</summary>
        private string CaptureGrids(FactionState owner, IMyPlayer player, List<MyCubeGrid> grids,
                                    string scope, string name, string factionHint, string usage)
        {
            CaptureTarget target;
            SePrefabCatalogue catalogue;
            string refusal;
            if (!TryValidateCaptureRequest(owner, scope, name, usage,
                    out target, out catalogue, out refusal))
                return refusal;
            if (grids == null || grids.Count == 0) return "That's not a ship — look directly at the hull.";

            string error;
            if (!ShipCaptureScanner.PlayerHasAccessToAll(player, grids, out error))
                return error;

            // The reverse of the station scan's static rule, and the reason it is here rather than
            // only there: a static grid admitted through this door would enter the fleet pool as a
            // hull, which is the failure the station kind exists to prevent.
            foreach (MyCubeGrid staticGrid in grids)
                if (staticGrid != null && staticGrid.IsStatic)
                    return "That is a station — use !factions station scan.";

            int totalBlocks = 0;
            foreach (MyCubeGrid g in grids) totalBlocks += g.BlocksCount;
            if (totalBlocks > _tuning.ShipCaptureMaxBlocks)
                return "That ship has " + totalBlocks + " blocks — the cap is " + _tuning.ShipCaptureMaxBlocks + ".";

            string mismatch = DescribeVersionMismatch(catalogue, target, false, null);
            if (mismatch != null) return mismatch;

            var builders = new MyObjectBuilder_CubeGrid[grids.Count];
            for (int i = 0; i < grids.Count; i++)
                builders[i] = (MyObjectBuilder_CubeGrid)grids[i].GetObjectBuilder(true);

            return RegisterShipCapture(owner, player.IdentityId, builders, totalBlocks, target, catalogue, factionHint);
        }

        /// <summary>The registration half of a ship capture, from grids already checked: the
        /// catalogue entry, the recipe, the store, the sidecar and the <c>SHIP_SCANNED</c> line. A
        /// Nexus agent's capture arrives here too (<see cref="RegisterRelayedCapture"/>).</summary>
        private string RegisterShipCapture(FactionState owner, long identityId, MyObjectBuilder_CubeGrid[] builders,
                                           int totalBlocks, CaptureTarget target, SePrefabCatalogue catalogue,
                                           string factionHint)
        {
            string prefabId = target.PrefabId;
            bool global = target.OwnerTag == null;
            var entry = new SePrefabCatalogue.Entry
            {
                Roles       = new List<string>(),
                FactionHint = string.IsNullOrEmpty(factionHint) ? PrefabFactionHint.Universal : factionHint,
                GridSize    = PrefabCatalogueScanner.DetectGridSize(builders),
                Family      = target.Family,
                Version     = target.Family != null ? target.Version : 0,
            };
            PrefabCatalogueScanner.ScanPrefab(builders, _itemPrices, entry);
            PrefabCatalogueScanner.AssignCapabilityRoles(entry, _tuning);

            string ownerFactionTag = target.OwnerTag;
            catalogue.RegisterCustomPrefab(prefabId, entry, ownerFactionTag);

            var recipes = _recipes as SeRecipeRegistry;
            if (recipes != null) recipes.RegisterPrefabRecipe(prefabId, builders, fromCapture: true);

            if (_customPrefabStore != null)
                _customPrefabStore.Register(prefabId, builders, ownerFactionTag, identityId, owner.Tag,
                    null, null, target.Family, target.Version, factionHint);
            CaptureGeneration++;

            // Without this a mid-session capture stays invisible to the Dashboard ("not in
            // catalogue") until the next full restart.
            RefreshCatalogueSidecar(catalogue);

            // One line per capture, from whichever front end made it -- the chat verb answers the
            // player in chat and the terminal answers in a dialog, so neither reply is a record.
            FactionsPlugin.Log.Info("SHIP_SCANNED prefab=" + prefabId
                                  + " scope=" + (global ? "all" : ownerFactionTag)
                                  + (factionHint != null ? " livery=" + entry.FactionHint : "")
                                  + VersionLogFields(target)
                                  + " blocks=" + totalBlocks);

            var sb = new StringBuilder();
            sb.AppendLine(DescribeCaptureTarget("Captured", target));
            sb.AppendLine("  roles: " + (entry.Roles.Count > 0 ? string.Join(", ", entry.Roles) : "none (no role floor cleared)"));
            sb.AppendLine("  cost=" + entry.Cost + "  cargo=" + entry.CargoLiters + "L  dps="
                          + ShipPool.TotalDps(entry.WeaponBands).ToString("0") + "  drill=" + entry.DrillYieldScore.ToString("0"));
            if (factionHint != null)
                sb.Append("Livery " + entry.FactionHint + ", [" + owner.Tag + "] only: its shipyards choose it for the roles above.");
            else
                sb.Append("Queue it with: !factions station queue build <nodeId> " + (target.Family ?? prefabId));
            return sb.ToString();
        }

        /// <summary>Player-facing station scan for `!factions station scan &lt;own|all&gt; &lt;name&gt;` —
        /// see <see cref="Commands.PlayerStationCommands"/>. The station counterpart of
        /// <see cref="CaptureShipForPlayer"/>: same raycast, same reach, same access check, same
        /// sanitised global name; a different acceptance rule and a different catalogue kind. What
        /// it produces is a refit target, never a fleet hull — see
        /// <see cref="CaptureStationGridsForPlayer"/>.</summary>
        public string CaptureStationForPlayer(FactionState owner, IMyPlayer player, string scope, string name)
        {
            CaptureTarget ignoredTarget;
            SePrefabCatalogue ignoredCatalogue;
            string refusal;
            if (!TryValidateCaptureRequest(owner, scope, name, StationScanUsage,
                    out ignoredTarget, out ignoredCatalogue, out refusal))
                return refusal;

            List<MyCubeGrid> grids;
            string error;
            if (!ShipCaptureScanner.TryFindTargetedGrids(player, _tuning.ShipCaptureMaxRangeM, out grids, out error))
                return error;

            return CaptureStationGridsForPlayer(owner, player, grids, scope, name);
        }

        /// <summary>
        /// The station scan itself, from a group of grids the caller has already found — the
        /// station-side counterpart of <see cref="CaptureGridsForPlayer"/>, and the single body both
        /// front ends run: the chat verb raycasts, and the Services Terminal's Scan station button
        /// hands in the terminal's own grid group.
        ///
        /// <para>Four acceptance rules beyond a ship capture's, each refused with the sentence that
        /// says which one failed:</para>
        /// <list type="bullet">
        ///   <item><b>Static only.</b> Every grid in the group. A design still on a ship hull is not
        ///   a station, and admitting one would put a flyable grid on a node.</item>
        ///   <item><b>Its own block ceiling</b> (<see cref="FactionTuning.StationCaptureMaxBlocks"/>).</item>
        ///   <item><b>Store, Contract, Services Terminal.</b> The three blocks that make a founded
        ///   node a working trading post; a design without them would found a node that trades with
        ///   nobody.</item>
        ///   <item><b>A measured site.</b> Where the grid actually stood, classified through the same
        ///   <see cref="StationSiting"/> a node goes through, collapsed to the two-way split the
        ///   refit gate compares on. Measured, never declared.</item>
        /// </list>
        ///
        /// <para>No capability roles are assigned: a station is never dispatched, and a role on it
        /// would offer a 1200-block static grid to a shipyard as a Guard.</para>
        /// </summary>
        public string CaptureStationGridsForPlayer(FactionState owner, IMyPlayer player, List<MyCubeGrid> grids,
                                                   string scope, string name)
        {
            CaptureTarget target;
            SePrefabCatalogue catalogue;
            string refusal;
            if (!TryValidateCaptureRequest(owner, scope, name, StationScanUsage,
                    out target, out catalogue, out refusal))
                return refusal;
            if (grids == null || grids.Count == 0) return "That's not a station — look directly at the hull.";

            string error;
            if (!ShipCaptureScanner.PlayerHasAccessToAll(player, grids, out error))
                return error;

            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null) continue;
                if (!grid.IsStatic)
                    return "That is not a station: " + grid.DisplayName + " is a ship grid.";
            }

            int totalBlocks = 0;
            foreach (MyCubeGrid g in grids) totalBlocks += g.BlocksCount;
            if (totalBlocks > _tuning.StationCaptureMaxBlocks)
                return "That station has " + totalBlocks + " blocks — the cap is " + _tuning.StationCaptureMaxBlocks + ".";

            // The design's root is the grid the station is built around: it lands on the node's own
            // position and axes, and its entity id becomes StationEntityId. The mechanical group
            // enumerates in no particular order, so the largest grid is promoted to index 0 rather
            // than whichever one the group handed back first -- before the builders are taken, so
            // the saved design, the measured site and the spawned station all agree on which grid
            // that is.
            PromoteLargestGridToRoot(grids);

            var builders = new MyObjectBuilder_CubeGrid[grids.Count];
            for (int i = 0; i < grids.Count; i++)
                builders[i] = (MyObjectBuilder_CubeGrid)grids[i].GetObjectBuilder(true);

            Vector3D worldPos = grids[0].PositionComp.GetPosition();
            var posKm = new Vec3(worldPos.X / 1000.0, worldPos.Y / 1000.0, worldPos.Z / 1000.0);
            string adoptNode = null;
            if (_stationMaterializer != null)
                foreach (MyCubeGrid grid in grids)
                {
                    if (grid == null) continue;
                    adoptNode = _stationMaterializer.NodeIdForStationGrid(grid.EntityId);
                    if (adoptNode != null) break;
                }
            return RegisterStationCapture(owner, player.IdentityId, builders, totalBlocks, posKm, target, catalogue, adoptNode);
        }

        /// <summary>The registration half of a station scan, from grids already checked and with the
        /// root grid first: the trading-post rules, the measured site, the catalogue entry, the
        /// recipe, the store, <c>STATION_SCANNED</c>, and the adoption of <paramref name="adoptNodeId"/>
        /// when the scan was of its standing station. A Nexus agent's scan arrives here too.</summary>
        private string RegisterStationCapture(FactionState owner, long identityId, MyObjectBuilder_CubeGrid[] builders,
                                              int totalBlocks, Vec3 posKm, CaptureTarget target,
                                              SePrefabCatalogue catalogue, string adoptNodeId)
        {
            string prefabId = target.PrefabId;
            bool global = target.OwnerTag == null;
            int stores, contracts, services;
            CountTradingPostBlocks(builders, out stores, out contracts, out services);
            if (stores == 0 || contracts == 0 || services == 0)
            {
                var missing = new List<string>(3);
                if (stores == 0) missing.Add("Store block");
                if (contracts == 0) missing.Add("Contract block");
                if (services == 0) missing.Add("Services Terminal");
                return "A station design needs a Store block, a Contract block and a Services Terminal; missing: "
                       + string.Join(", ", missing) + ".";
            }

            // SE binds a store, contract board or services terminal to its station through
            // MyFactionCollection.GetStationByGridId, which matches one id: the station's
            // StationEntityId, set from the root grid alone. A block of these three on a subgrid
            // therefore reaches no station however the grids are registered with the economy
            // component, and the design would stand up as a node that cannot trade.
            int rootStores, rootContracts, rootServices;
            CountTradingPostBlocks(new[] { builders[0] }, out rootStores, out rootContracts, out rootServices);
            if (rootStores == 0 || rootContracts == 0 || rootServices == 0)
            {
                var offGrid = new List<string>(3);
                if (rootStores == 0) offGrid.Add("Store block");
                if (rootContracts == 0) offGrid.Add("Contract block");
                if (rootServices == 0) offGrid.Add("Services Terminal");
                return "The Store block, Contract block and Services Terminal must all sit on the design's "
                       + "main grid, or they bind to no station and it trades with nobody. On a subgrid: "
                       + string.Join(", ", offGrid) + ".";
            }

            IPlanetCatalogue planets = _world != null && _world.Planets != null ? _world.Planets : _planets;
            // The three-way classification collapsed by the same helper the refit gate compares on,
            // so an orbital scan is offered at every non-surface node rather than at none.
            string site = StationPrefabSite.For(StationSiting.Classify(planets, posKm));

            string mismatch = DescribeVersionMismatch(catalogue, target, true, site);
            if (mismatch != null) return mismatch;

            var entry = new SePrefabCatalogue.Entry
            {
                Roles       = new List<string>(),
                FactionHint = "All",
                GridSize    = "Station",
                Kind        = "Station",
                Site        = site,
                Family      = target.Family,
                Version     = target.Family != null ? target.Version : 0,
            };
            PrefabCatalogueScanner.ScanPrefab(builders, _itemPrices, entry);

            string ownerFactionTag = target.OwnerTag;
            catalogue.RegisterCustomPrefab(prefabId, entry, ownerFactionTag);

            var recipes = _recipes as SeRecipeRegistry;
            if (recipes != null) recipes.RegisterPrefabRecipe(prefabId, builders, fromCapture: true);

            if (_customPrefabStore != null)
                _customPrefabStore.Register(prefabId, builders, ownerFactionTag, identityId, owner.Tag,
                    "Station", site, target.Family, target.Version);
            CaptureGeneration++;

            // Same best-effort sidecar refresh a ship capture does, and for the same reason: the
            // Dashboard reads prefab_catalogue.json verbatim, never the live catalogue.
            if (!string.IsNullOrEmpty(_prefabCatalogueStorageDir))
            {
                try { File.WriteAllText(Path.Combine(_prefabCatalogueStorageDir, "prefab_catalogue.json"), catalogue.ToJson()); }
                catch { /* best-effort refresh only -- live catalogue is unaffected */ }
            }

            double dps = ShipPool.TotalDps(entry.WeaponBands);
            _log.Write("STATION_SCANNED prefab=" + prefabId
                     + " scope=" + (global ? "all" : ownerFactionTag)
                     + VersionLogFields(target)
                     + " site=" + site
                     + " blocks=" + totalBlocks
                     + " dps=" + dps.ToString("0", CultureInfo.InvariantCulture)
                     + " cost=" + entry.Cost.ToString(CultureInfo.InvariantCulture));

            var sb = new StringBuilder();
            sb.AppendLine(DescribeCaptureTarget("Scanned station", target));
            sb.AppendLine("  site=" + site + " blocks=" + totalBlocks + " cost=" + entry.Cost
                          + " dps=" + dps.ToString("0") + " store=" + stores + " contracts=" + contracts
                          + " services=" + services);
            string adopted = AdoptScannedStation(owner, adoptNodeId, prefabId);
            if (adopted != null) sb.AppendLine(adopted);
            sb.Append("Refit a node with: !factions station refit <nodeId> " + (target.Family ?? prefabId));
            return sb.ToString();
        }

        /// <summary>Moves the grid with the most blocks to index 0, the position every consumer of a
        /// captured design treats as the root: <c>CustomPrefabSpawner.TrySpawnStatic</c> maps the
        /// group into the station's frame around it, <c>LocalBoundingBox</c> measures it for the
        /// placement solver, and <c>StationMaterializer</c> claims the first spawned grid as the
        /// station. Largest-by-block-count is SE's own rule for the main grid of a group (see
        /// <c>MyRepairServiceComponent</c>). Stable otherwise: a tie keeps the group's order.</summary>
        internal static void PromoteLargestGridToRoot(List<MyCubeGrid> grids)
        {
            if (grids == null || grids.Count < 2) return;
            int best = 0;
            int bestBlocks = grids[0] != null ? grids[0].BlocksCount : 0;
            for (int i = 1; i < grids.Count; i++)
            {
                int blocks = grids[i] != null ? grids[i].BlocksCount : 0;
                if (blocks <= bestBlocks) continue;
                best = i;
                bestBlocks = blocks;
            }
            if (best == 0) return;
            MyCubeGrid root = grids[best];
            grids[best] = grids[0];
            grids[0] = root;
        }

        /// <summary>Counts the three blocks that make a design a working trading post. The store and
        /// contract board are matched on object-builder type; the Services Terminal has no type of
        /// its own and is matched on subtype, the same string a live block is matched on.</summary>
        private static void CountTradingPostBlocks(MyObjectBuilder_CubeGrid[] grids,
            out int stores, out int contracts, out int services)
        {
            stores = 0;
            contracts = 0;
            services = 0;
            if (grids == null) return;
            foreach (MyObjectBuilder_CubeGrid grid in grids)
            {
                if (grid == null || grid.CubeBlocks == null) continue;
                foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                {
                    if (block == null) continue;
                    if (block is Sandbox.Common.ObjectBuilders.MyObjectBuilder_StoreBlock) stores++;
                    else if (block is Sandbox.Common.ObjectBuilders.MyObjectBuilder_ContractBlock) contracts++;
                    else if (string.Equals(block.SubtypeName, ServicesTerminalSubtype, StringComparison.OrdinalIgnoreCase))
                        services++;
                }
            }
        }

        /// <summary>
        /// Every station design this node could be rebuilt as, structured — the single body both
        /// refit surfaces read. <c>!factions station prefabs</c> renders it as text and the client's
        /// refit panel puts each field in a column; formatting one from the other's prose is how the
        /// two would come to disagree about what a design costs.
        ///
        /// <para>Filtered to the node's own site, because that is the one property a refit can never
        /// cross, and to what this faction may use (<see cref="PrefabOwnershipFilter"/>). The bill on
        /// each row is the net difference from the design the node stands as now, resolved through
        /// the same <see cref="ShipyardManager.NetRefitBom"/> the queued refit is charged with.</para>
        /// </summary>
        public StationRefitOverview DescribeStationRefit(FactionState owner, string nodeId)
        {
            var overview = new StationRefitOverview { NodeId = nodeId };
            if (_world == null) { overview.Refusal = "Factions runtime not ready."; return overview; }
            if (owner == null) { overview.Refusal = "Your faction is not registered with Factions yet."; return overview; }

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !_world.Graph.Nodes.TryGetValue(nodeId, out node))
            {
                overview.Refusal = "No node '" + nodeId + "'.";
                return overview;
            }
            if (!string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
            {
                overview.Refusal = nodeId + " isn't owned by your faction.";
                return overview;
            }
            if (node.StableStationId == 0)
            {
                overview.Refusal = nodeId + " has no station yet — !factions station found builds the first one, free.";
                return overview;
            }

            SePrefabCatalogue catalogue = _prefabCatalogue as SePrefabCatalogue;
            if (catalogue == null) { overview.Refusal = "No prefab catalogue loaded."; return overview; }

            overview.CurrentPrefabId = node.StationPrefabId;
            overview.PendingPrefabId = node.PendingStationPrefabId;
            overview.Site = RefitStationValidator.SiteOf(_world, node);
            overview.SiegeBlocked = RefitStationValidator.IsUnderSiege(node, _world.UtcNow);
            overview.Observed = (_stationMaterializer != null && _stationMaterializer.IsObserved(node.Id))
                             || IsObservedOnAgent(node.Id);

            // The refit entry on this node's own queue, if one is collecting. Role is the only thing
            // that separates it from a hull build sharing the same yard.
            for (int i = 0; i < node.BuildQueue.Count; i++)
            {
                BuildQueueEntry entry = node.BuildQueue[i];
                if (!ShipyardManager.IsStationRefit(entry)) continue;
                overview.CollectingPrefabId = entry.PrefabId;
                foreach (KeyValuePair<string, long> kv in entry.Bom) overview.CollectingBomUnits += kv.Value;
                foreach (KeyValuePair<string, long> kv in entry.Collected) overview.CollectedUnits += kv.Value;
                break;
            }

            var found = new List<string>();
            foreach (string id in catalogue.Ids)
            {
                if (!catalogue.IsStationPrefab(id)) continue;
                if (!StationPrefabSite.Matches(catalogue.Site(id), overview.Site)) continue;
                found.Add(id);
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> usable = PrefabOwnershipFilter.Apply(found, catalogue, owner.Tag);
            // The design the node stands as is listed even when a newer version supersedes it, so
            // the player sees what they are upgrading from.
            if (!string.IsNullOrEmpty(node.StationPrefabId) && found.Contains(node.StationPrefabId, StringComparer.OrdinalIgnoreCase)
                && !usable.Contains(node.StationPrefabId, StringComparer.OrdinalIgnoreCase))
            {
                var withCurrent = new List<string>(usable) { node.StationPrefabId };
                withCurrent.Sort(StringComparer.OrdinalIgnoreCase);
                usable = withCurrent;
            }

            for (int i = 0; i < usable.Count; i++)
            {
                string id = usable[i];
                bool targetHasRecipe;
                Dictionary<string, long> net = ShipyardManager.NetRefitBom(_world.Recipes,
                    node.StationPrefabId, id, out targetHasRecipe);
                long units = 0;
                foreach (KeyValuePair<string, long> kv in net) units += kv.Value;

                overview.Candidates.Add(new StationRefitCandidate
                {
                    PrefabId    = id,
                    Captured    = IsCapturedDesign(id),
                    Site        = catalogue.Site(id),
                    BlockCount  = catalogue.BlockCount(id),
                    Dps         = ShipPool.TotalDps(catalogue.WeaponBands(id)),
                    Cost        = catalogue.Cost(id),
                    HasRecipe   = targetHasRecipe,
                    NetBom      = net,
                    NetBomUnits = units,
                    Current     = string.Equals(id, node.StationPrefabId, StringComparison.OrdinalIgnoreCase),
                });
            }
            return overview;
        }

        /// <summary>Player-facing candidate list for `!factions station prefabs [nodeId]` — every
        /// station design this node could be rebuilt as, and what each costs <b>from where it
        /// stands</b> rather than gross. The rows come from
        /// <see cref="DescribeStationRefit"/>; this only renders them.</summary>
        public string ListStationPrefabsForPlayer(FactionState owner, string nodeId)
        {
            StationRefitOverview overview = DescribeStationRefit(owner, nodeId);
            if (overview.Refusal != null) return overview.Refusal;
            if (overview.Candidates.Count == 0)
                return "No station designs for a " + SiteWord(overview.Site) + " node yet — scan one with "
                       + "!factions station scan <own|all> <name>.";

            var sb = new StringBuilder();
            sb.AppendLine(nodeId + " is a " + SiteWord(overview.Site) + " node standing as '"
                          + (overview.CurrentPrefabId ?? "(unknown)") + "'. Designs it could be rebuilt as:");
            for (int i = 0; i < overview.Candidates.Count; i++)
            {
                StationRefitCandidate c = overview.Candidates[i];
                sb.Append("  ").Append(c.PrefabId)
                  .Append("  ").Append(c.Captured ? "scanned" : "vanilla")
                  .Append("  ").Append(c.Site)
                  .Append("  blocks=").Append(c.BlockCount)
                  .Append(" dps=").Append(c.Dps.ToString("0"))
                  .Append(" cost=").Append(c.Cost)
                  .Append(" refit-bom=").Append(c.HasRecipe
                      ? c.NetBomUnits.ToString(CultureInfo.InvariantCulture) : "n/a");
                if (c.Current)
                {
                    string latest = PrefabVersions.LatestFor(_world.PrefabCatalogue, c.PrefabId, owner.Tag);
                    sb.Append(string.Equals(latest, c.PrefabId, StringComparison.OrdinalIgnoreCase)
                        ? " (current)" : " (current; superseded by " + latest + ")");
                }
                sb.AppendLine();
            }
            sb.Append("Rebuild with: !factions station refit ").Append(nodeId).Append(" <prefabId>");
            return sb.ToString();
        }

        /// <summary>Whether a design came from a player scan rather than the content walk. The
        /// capture store is the only place that survives — an "all"-scoped capture carries no owner
        /// tag, so the catalogue entry alone cannot tell the two apart.</summary>
        private bool IsCapturedDesign(string prefabId)
        {
            return _customPrefabStore != null && _customPrefabStore.IsCapture(prefabId);
        }

        private static string SiteWord(StationSite site)
        {
            return site == StationSite.Surface ? "surface" : "space";
        }

        /// <summary>Player-facing station refit for `!factions station refit [nodeId] &lt;prefabId&gt;
        /// [force]` — queues the components this node has to collect to be rebuilt as another design.
        /// A thin wrapper over <see cref="ShipyardManager.QueueRefit"/>, which owns every gate; the
        /// only rule added here is the same host-layer ownership check
        /// <see cref="QueueBuildForPlayer"/> applies to an explicitly-typed capture id.</summary>
        public string QueueRefitForPlayer(FactionState owner, string nodeId, string prefabId, bool force)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(prefabId))
                return "Usage: !factions station refit [nodeId] <prefabId> [force]";

            prefabId = ResolveDesignForPlayer(prefabId, owner.Tag);

            var owned = _world.PrefabCatalogue as IOwnedPrefabCatalogue;
            if (owned != null)
            {
                string restrictedTo = owned.OwnerFactionTag(prefabId);
                if (!string.IsNullOrEmpty(restrictedTo)
                    && !string.Equals(restrictedTo, owner.Tag, StringComparison.OrdinalIgnoreCase))
                    return "'" + prefabId + "' is a faction-restricted capture, not usable by [" + owner.Tag + "].";
            }

            BuildQueueEntry queued;
            StationRefitResult verdict = ShipyardManager.QueueRefit(_world, _tuning, owner.Tag, nodeId,
                prefabId, force, out queued, _managerLog);
            if (verdict != StationRefitResult.Ok)
                return DescribeRefitRefusal(verdict, owner, nodeId, prefabId);

            Flush();
            var sb = new StringBuilder();
            long units = 0;
            if (queued != null)
                foreach (KeyValuePair<string, long> kv in queued.Bom) units += kv.Value;

            if (units == 0)
            {
                sb.Append("Refit of ").Append(nodeId).Append(" to '").Append(prefabId)
                  .Append("' needed nothing this station does not already stand as — the rebuild is paid for ")
                  .Append("and stands the next time the station is built in world.");
                return sb.ToString();
            }

            sb.AppendLine("Refit of " + nodeId + " to '" + prefabId + "' queued. Needs:");
            foreach (KeyValuePair<string, long> kv in queued.Bom)
                sb.AppendLine("  " + kv.Key + " x" + kv.Value);
            SpatialNode pendingAt;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out pendingAt)
                && !string.IsNullOrEmpty(pendingAt.PendingStationPrefabId)
                && !string.Equals(pendingAt.PendingStationPrefabId, prefabId, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("NOTE: '" + pendingAt.PendingStationPrefabId + "' was already built and waiting here. "
                    + "Finishing this refit supersedes it, and what it cost is not refunded — "
                    + "!factions station refit apply " + nodeId + " stands it up instead.");
            SpatialNode queuedAt;
            if (_world.Graph.Nodes.TryGetValue(nodeId, out queuedAt) && queuedAt.ShipyardPaused)
                sb.AppendLine("NOTE: this station is PAUSED — the refit won't collect anything until "
                    + "!factions station queue resume.");
            sb.Append("Track it with: !factions station queue " + nodeId);
            return sb.ToString();
        }

        /// <summary>One sentence per <see cref="StationRefitResult"/>, so a refusal names the gate
        /// rather than the enum. The site mismatch is stated by name on both sides, which is the
        /// only refusal a player cannot work out from the verb they typed.</summary>
        private string DescribeRefitRefusal(StationRefitResult verdict, FactionState owner, string nodeId, string prefabId)
        {
            switch (verdict)
            {
                case StationRefitResult.NodeNotFound:
                    return "No node '" + nodeId + "'.";
                case StationRefitResult.NotOwned:
                    return nodeId + " isn't owned by your faction.";
                case StationRefitResult.NotFounded:
                    return nodeId + " has no station yet — !factions station found builds the first one, free.";
                case StationRefitResult.UnknownPrefab:
                    return "'" + prefabId + "' isn't a station design — try !factions station prefabs " + nodeId;
                case StationRefitResult.NotStationPrefab:
                    return "'" + prefabId + "' is a ship hull, not a station design.";
                case StationRefitResult.PrefabNotUsable:
                    return "'" + prefabId + "' is a faction-restricted capture, not usable by [" + owner.Tag + "].";
                case StationRefitResult.SiteMismatch:
                    return DescribeSiteMismatch(nodeId, prefabId);
                case StationRefitResult.AlreadyThisPrefab:
                    return nodeId + " already stands as '" + prefabId + "'.";
                case StationRefitResult.AlreadyPending:
                    return "'" + prefabId + "' is already built and waiting to be applied; use "
                           + "!factions station refit apply " + nodeId + ".";
                case StationRefitResult.NoRecipe:
                    return "'" + prefabId + "' has no build recipe, so there is no bill to charge for the rebuild.";
                case StationRefitResult.SiegeInProgress:
                    return nodeId + " is under siege — you do not rebuild the walls while they are being shot at.";
                case StationRefitResult.BuildInProgress:
                    return "A ship build at " + nodeId + " is mid-collection and would lose the working slot. "
                           + "Add 'force' to queue the refit anyway — the build keeps everything it has collected.";
                case StationRefitResult.RefitInProgress:
                    return "A refit is already queued at " + nodeId
                           + ". Add 'force' to replace it, refunding what it has collected.";
            }
            return "Refit refused.";
        }

        // Held rebuilds already reported, keyed "<nodeId>|<reason>". A hold is a state, not an event:
        // the sweep re-reaches every pending node on its own cadence, so without this the same line
        // would be filed every twenty seconds for as long as a player stands at their station -- or
        // for as long as the design stays unresolvable. The reason is part of the key so a node that
        // was held for an observer and is now held for a missing design says so. Both keys are
        // cleared the moment its rebuild lands, so a second hold is news again.
        private readonly HashSet<string> _refitHeldLogged = new HashSet<string>(StringComparer.Ordinal);
        private DateTime _refitSweepNextUtc = DateTime.MinValue;

        /// <summary>How often the pending-rebuild sweep walks the graph. A rebuild is already paid
        /// for and is waiting on a player to walk away, so the cost of noticing a minute late is
        /// nothing; the cost of asking on every frame is a graph walk at 60 Hz.</summary>
        private const double RefitSweepIntervalSeconds = 20.0;

        /// <summary>Set by <c>FactionsPlugin</c>. Called after a rebuild lands, because a station's
        /// store items live on the <c>MyFactionStation</c> object the swap replaced. Unset means the
        /// periodic store refresh picks it up on its own cadence instead.</summary>
        public Action StationStoresChanged;

        /// <summary>Cadence-gated sweep for rebuilds waiting on an empty station. Called from the
        /// station-observation tick, immediately after it has decided which stations are standing —
        /// which is exactly the information <see cref="StationMaterializer.IsObserved"/> answers
        /// from.</summary>
        private void SweepPendingStationRefits()
        {
            // Real time, not the sim clock: a rebuild is waiting on a player walking away, which
            // happens whether or not the sim is running, and the sim clock stops advancing at all
            // while the runtime is paused.
            DateTime now = DateTime.UtcNow;
            if (_world == null || now < _refitSweepNextUtc) return;
            _refitSweepNextUtc = now.AddSeconds(RefitSweepIntervalSeconds);
            ApplyPendingStationRefits(force: false, reason: "sweep");
        }

        /// <summary>Stands up every rebuild Core has already paid for and the host has not applied
        /// yet. <paramref name="force"/> false holds a node a player is standing at and says so once;
        /// true swaps regardless, which is what <c>station refit apply</c> and the boot pass are.
        ///
        /// <para>Returns how many actually landed. <paramref name="onlyNodeId"/> narrows it to one
        /// node, for the player verb.</para></summary>
        public int ApplyPendingStationRefits(bool force, string reason, string onlyNodeId = null)
        {
            if (_world == null) return 0;

            List<SpatialNode> pending = null;
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (node == null || string.IsNullOrEmpty(node.PendingStationPrefabId)) continue;
                if (onlyNodeId != null && !string.Equals(node.Id, onlyNodeId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (pending == null) pending = new List<SpatialNode>();
                pending.Add(node);
            }
            if (pending == null) return 0;

            int applied = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                string detail;
                if (TryApplyPendingRefit(pending[i], force, out detail)) applied++;
            }
            if (applied > 0)
            {
                Flush();
                // The store list is rebuilt from the station object the swap replaced, so it is
                // empty until something repopulates it.
                Action stores = StationStoresChanged;
                if (stores != null)
                {
                    try { stores(); }
                    catch (Exception ex) { _log.Write("STATION_REFIT_STORE_REFRESH_FAILED reason=" + reason + " " + ex.Message); }
                }
            }
            return applied;
        }

        /// <summary>One node's rebuild. False means nothing changed — held for an observer, or the
        /// SE side refused the swap — and <paramref name="detail"/> says which.</summary>
        private bool TryApplyPendingRefit(SpatialNode node, bool force, out string detail)
        {
            detail = null;
            string prefabId = node.PendingStationPrefabId;
            string tag = node.OwnerFactionTag;
            if (string.IsNullOrEmpty(prefabId) || string.IsNullOrEmpty(tag)) return false;

            if (!force && (_stationMaterializer.IsObserved(node.Id) || IsObservedOnAgent(node.Id)))
            {
                if (_refitHeldLogged.Add(node.Id + "|observed"))
                {
                    _log.Write(tag + " STATION_REFIT_HELD @" + node.Id + " prefab=" + prefabId
                               + " reason=observed");
                    WhisperFactionMember(tag, "The rebuild of " + node.Id + " is finished and paid for. "
                        + "It will be standing the next time you arrive — or type "
                        + "!factions station refit apply " + node.Id + " to have it swapped now.");
                }
                detail = "held";
                return false;
            }

            double dpsBefore = ShipPool.TotalDps(node.StationWeapons);
            long valueBefore = node.StationBookValue ?? 0L;
            string oldPrefab = node.StationPrefabId;

            FactionState owner = FindState(tag);
            string swapDetail;
            if (!_stationFounder.ReplaceStationPrefab(node, owner, prefabId, _stationMaterializer, out swapDetail))
            {
                // Left pending on purpose: a rebuild the player has already paid for is never
                // dropped for a refusal. A design that does not resolve refuses identically on every
                // pass until the content or the capture comes back, so it is filed once, the way a
                // hold for an observer is; the transient refusals (no session, no faction, no station
                // record) are news each time because the next sweep may not repeat them.
                bool unresolvable = swapDetail != null
                    && swapDetail.StartsWith(StationFounder.UnresolvableDetailPrefix, StringComparison.Ordinal);
                if (!unresolvable)
                {
                    _log.Write(tag + " STATION_REFIT_REFUSED @" + node.Id + " prefab=" + prefabId
                               + " reason=" + swapDetail);
                }
                else if (_refitHeldLogged.Add(node.Id + "|unresolvable"))
                {
                    _log.Write(tag + " STATION_REFIT_HELD @" + node.Id + " prefab=" + prefabId
                               + " reason=unresolvable detail=" + swapDetail);
                }
                detail = swapDetail;
                return false;
            }

            _refitHeldLogged.Remove(node.Id + "|observed");
            _refitHeldLogged.Remove(node.Id + "|unresolvable");
            double dpsAfter = ShipPool.TotalDps(node.StationWeapons);
            long valueAfter = node.StationBookValue ?? 0L;

            // An upgrade to a newer version of the same design hands back what it replaced, the way
            // a hull refit does; credited at the swap, the moment those blocks are gone.
            Dictionary<string, long> credit = HullRefit.StationUpgradeCredit(_world.PrefabCatalogue, _world.Recipes,
                                                                             oldPrefab, prefabId);
            long creditedUnits = 0;
            foreach (KeyValuePair<string, long> kv in credit)
            {
                if (kv.Value <= 0) continue;
                long have;
                node.Inventory.TryGetValue(kv.Key, out have);
                node.Inventory[kv.Key] = have + kv.Value;
                creditedUnits += kv.Value;
            }

            _log.Write(tag + " STATION_REFIT_APPLIED @" + node.Id + " "
                       + (oldPrefab ?? "(none)") + " -> " + prefabId
                       + " dps=" + dpsBefore.ToString("0") + "->" + dpsAfter.ToString("0")
                       + " value=" + valueBefore.ToString(CultureInfo.InvariantCulture)
                       + "->" + valueAfter.ToString(CultureInfo.InvariantCulture)
                       + (creditedUnits > 0 ? " credited_units=" + creditedUnits.ToString(CultureInfo.InvariantCulture) : ""));
            detail = swapDetail;
            return true;
        }

        /// <summary>Whether a player is standing at this node's station right now — what decides
        /// whether a finished rebuild lands on its own or has to be forced. The client surface reads
        /// it to say which of the two a player is looking at; the sweep reads the materializer
        /// directly.</summary>
        public bool IsStationObserved(string nodeId)
        {
            return _stationMaterializer.IsObserved(nodeId) || IsObservedOnAgent(nodeId);
        }

        /// <summary>Player-facing force for `!factions station refit apply [nodeId]` — swaps the
        /// design now, dropping whoever is standing in the old one, rather than waiting for the
        /// station to empty.</summary>
        public string ApplyRefitNowForPlayer(FactionState owner, string nodeId)
        {
            if (_world == null) return "Factions runtime not ready.";
            if (owner == null) return "Your faction is not registered with Factions yet.";

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !_world.Graph.Nodes.TryGetValue(nodeId, out node))
                return "No node '" + nodeId + "'.";
            if (!string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
                return nodeId + " isn't owned by your faction.";
            if (string.IsNullOrEmpty(node.PendingStationPrefabId))
                return nodeId + " has no finished rebuild waiting. Queue one with "
                       + "!factions station refit " + nodeId + " <prefabId>.";

            string prefabId = node.PendingStationPrefabId;
            int applied = ApplyPendingStationRefits(force: true, reason: "player", onlyNodeId: nodeId);
            if (applied == 0)
                return "Could not stand '" + prefabId + "' up at " + nodeId
                       + " — see the Factions log. The rebuild is still paid for and will be retried.";

            return nodeId + " now stands as '" + prefabId + "'. Anything that was inside the old "
                   + "station is outside it now; the new one appears as you approach.";
        }

        /// <summary>Whispers one online member of <paramref name="factionTag"/>, mirroring
        /// <c>ClaimOfferManager</c>'s pattern. Silent when chat is not attached or nobody from that
        /// faction is online — the log line is the durable record either way.</summary>
        private void WhisperFactionMember(string factionTag, string message)
        {
            Sandbox.Game.World.MySession session = Sandbox.Game.World.MySession.Static;
            if (_chat == null || string.IsNullOrEmpty(factionTag) || session == null || session.Players == null) return;
            try
            {
                foreach (Sandbox.Game.World.MyPlayer player in session.Players.GetOnlinePlayers())
                {
                    if (player == null || player.IsBot || player.Identity == null) continue;
                    Sandbox.Game.World.MyFaction seFaction =
                        session.Factions.GetPlayerFaction(player.Identity.IdentityId) as Sandbox.Game.World.MyFaction;
                    if (seFaction == null || !string.Equals(seFaction.Tag, factionTag, StringComparison.Ordinal))
                        continue;
                    _chat.SendMessageAsOther(FleetWhisperAuthor, message, Color.CornflowerBlue, player.Id.SteamId);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Write("STATION_REFIT_WHISPER_FAILED " + factionTag + " " + ex.Message);
            }
        }

        private string DescribeSiteMismatch(string nodeId, string prefabId)
        {
            SePrefabCatalogue catalogue = _prefabCatalogue as SePrefabCatalogue;
            string prefabSite = catalogue != null ? catalogue.Site(prefabId) : null;
            string prefabWord = string.Equals(prefabSite, StationPrefabSite.Surface, StringComparison.OrdinalIgnoreCase)
                ? "surface" : "space";
            SpatialNode node;
            string nodeWord = _world.Graph.Nodes.TryGetValue(nodeId, out node)
                ? SiteWord(RefitStationValidator.SiteOf(_world, node)) : "space";
            return "'" + prefabId + "' is a " + prefabWord + " design; " + nodeId + " is a " + nodeWord + " node.";
        }

        private static string SanitizePrefabId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            string id = sb.ToString();
            if (id.Length > MaxCapturedPrefabNameLength) id = id.Substring(0, MaxCapturedPrefabNameLength);
            return id.Length > 0 ? id : null;
        }

        private void RenameStationForNewOwner(string nodeId, FactionState newOwner, string verb)
        {
            SpatialNode node;
            if (nodeId == null || newOwner == null
                || !_world.Graph.Nodes.TryGetValue(nodeId, out node) || node.StableStationId == 0) return;
            if (_stationFounder.RenameConqueredStation(node, newOwner))
                _log.Write("t" + _simTick + " " + verb + " " + nodeId + " -> " + node.DisplayName);
        }

        /// <summary>Renames a founded station to the "derelict" theme after it becomes UNKN-owned
        /// (explicit Withdraw, or decay expiry — see <see cref="WorldModel.AbandonedNodes"/>).</summary>
        private void RenameAbandonedStation(string nodeId)
        {
            FactionState unkn = _states.FirstOrDefault(s => string.Equals(s.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase));
            RenameStationForNewOwner(nodeId, unkn, "STATION_ABANDONED");
        }

        /// <summary>Drains <see cref="WorldModel.AbandonedNodes"/> (nodes DecayInfluence just handed
        /// to UNKN this pass): logs each territory loss, emits the live event, and renames each
        /// founded station. Call immediately after every <see cref="WorldModel.DecayInfluence"/>
        /// invocation.</summary>
        private void RenameAbandonedStationsFromDecay()
        {
            IList<AbandonedNode> abandoned = _world.AbandonedNodes;
            for (int i = 0; i < abandoned.Count; i++)
            {
                AbandonedNode lost = abandoned[i];
                _log.Write("t" + _simTick + " TERRITORY_DECAYED " + lost.PriorOwnerTag + " " + lost.NodeId);
                if (_eventWriter != null)
                    _eventWriter.WriteTerritoryLoss(lost.PriorOwnerTag, lost.NodeId, "decay");
                RenameAbandonedStation(lost.NodeId);
            }
            _world.ClearAbandonedNodes();
            LogShipEvacuations();
        }

        /// <summary>Drains <see cref="WorldModel.ShipEvacuations"/> -- the evacuation fleets Core flew
        /// off a node its faction just lost, and the hulls it could not move. Called wherever a node
        /// can be abandoned: after the decay pass, after an explicit Withdraw, and after each income
        /// pass step (the insolvency shed).</summary>
        private void LogShipEvacuations()
        {
            IList<ShipEvacuation> moved = _world.ShipEvacuations;
            for (int i = 0; i < moved.Count; i++)
                _log.Write("t" + _simTick + " " + moved[i].LogLine());
            _world.ClearShipEvacuations();
        }

        private void Enact(FactionState st, Decision d, SeededRandom rng)
        {
            FactionIntent it = d.Intent;
            if (it.Kind == ActionKind.Idle) return;
            if (it.Cost > 0 && it.Cost > _world.GetTreasury(st.Tag)) return;   // can't afford (credits) -> no-op

            // Item-cost safety check (Score already gates; this is a defensive second pass)
            if (it.Kind == ActionKind.Expand
                && _world.GetInventory(st.Tag, ItemKind.ProcessedGood) < _tuning.ExpandItemCostProcessedGood) return;
            if (it.Kind == ActionKind.Develop && it.TargetCapability.HasValue)
            {
                NodeCapabilityKind cap = it.TargetCapability.Value;
                Dictionary<string, long> bom;
                if (_tuning.TryGetInstallBom(cap, out bom))
                {
                    foreach (var kv in bom)
                        if (_world.GetFactionInventoryByKey(st.Tag, kv.Key) + _world.GetFactionReservedInventoryByKey(st.Tag, kv.Key) < kv.Value)
                            return;
                }
                else
                {
                    if (_world.GetInventory(st.Tag, ItemKind.Ore)          + _world.GetReservedInventory(st.Tag, ItemKind.Ore)          < _tuning.InstallCostFor(cap, ItemKind.Ore))          return;
                    if (_world.GetInventory(st.Tag, ItemKind.Ingot)        + _world.GetReservedInventory(st.Tag, ItemKind.Ingot)        < _tuning.InstallCostFor(cap, ItemKind.Ingot))        return;
                    if (_world.GetInventory(st.Tag, ItemKind.ProcessedGood) + _world.GetReservedInventory(st.Tag, ItemKind.ProcessedGood) < _tuning.InstallCostFor(cap, ItemKind.ProcessedGood)) return;
                    if (_world.GetInventory(st.Tag, ItemKind.Hydrogen)      + _world.GetReservedInventory(st.Tag, ItemKind.Hydrogen)      < _tuning.InstallCostFor(cap, ItemKind.Hydrogen))      return;
                }
            }
            if (it.Kind == ActionKind.Found
                && _world.GetInventory(st.Tag, ItemKind.ProcessedGood) < _tuning.FoundItemCostProcessedGood) return;

            bool enacted = true;
            switch (it.Kind)
            {
                case ActionKind.Expand:
                {
                    SpatialNode newNode = _world.ExpandFrom(st.Tag, it.TargetNodeId);
                    if (newNode == null)
                    {
                        // Nothing near the origin clears the separation in force, so nothing was
                        // created and nothing is charged. The attempt still spends the cooldown:
                        // a saturated region would otherwise have the brain re-rolling the same
                        // draw every tick ahead of every other action it could take.
                        _log.Write("t" + _simTick + " EXPAND_REFUSED " + st.Tag + " from=" + (it.TargetNodeId ?? "none")
                                   + " why=no_legal_position");
                        st.Cooldowns[ActionKind.Expand] = _world.UtcNow + _tuning.CooldownFor(ActionKind.Expand);
                        return;
                    }
                    _log.Write("t" + _simTick + " EXPAND " + st.Tag + " -> " + newNode.Id + " ore=" + NodeDisplay.DominantOre(newNode) + " from=" + (it.TargetNodeId ?? "none"));
                    IList<NodeSeparationViolation> separationViolations = _world.SeparationViolations;
                    for (int i = 0; i < separationViolations.Count; i++)
                    {
                        NodeSeparationViolation violation = separationViolations[i];
                        _log.Write("t" + _simTick + " NODE_SEPARATION_VIOLATION node=" + violation.NewNodeId
                                   + " zone=" + violation.Zone
                                   + " neighbor=" + violation.OffendingNeighborId
                                   + " actualKm=" + violation.ActualDistanceKm.ToString("0.0")
                                   + " requiredKm=" + violation.RequiredMinSepKm.ToString("0.0"));
                    }
                    _world.ClearSeparationViolations();
                    break;
                }
                case ActionKind.Claim:
                {
                    string priorOwner = PriorOwnerOf(it.TargetNodeId);
                    if (!_world.TryClaim(st.Tag, it.TargetNodeId, false))
                    {
                        SpatialNode refused;
                        _world.Graph.Nodes.TryGetValue(it.TargetNodeId ?? "", out refused);
                        if (refused != null && NeutralFaction.IsReservedAgainst(refused, st.Tag, _world.UtcNow))
                            _log.Write("t" + _simTick + " CLAIM_BLOCKED " + st.Tag + " node=" + it.TargetNodeId
                                       + " contested_by=" + (refused.ContestedByTag ?? "player"));
                        break;
                    }
                    LogNodeClaimed(st.Tag, it.TargetNodeId, priorOwner);
                    // No-ops for a virgin (never-founded) node; renames a reclaimed derelict's
                    // station to the claiming faction's own theme (it was left "derelict"-named
                    // by RenameAbandonedStation while UNKN-owned).
                    RenameStationForNewOwner(it.TargetNodeId, st, "STATION_CLAIMED");
                    break;
                }
                // Reinforce moved to FleetManager: brain no longer dispatches Reinforce directly
                // case ActionKind.Reinforce: removed — FleetManager operational
                case ActionKind.Develop:
                    if (it.TargetCapability.HasValue)
                    {
                        _world.Develop(st.Tag, it.TargetNodeId, it.TargetCapability.Value);
                        _oracle?.InvalidateNode(it.TargetNodeId);  // capability set changed; stale reference stock
                        _log.Write("t" + _simTick + " DEVELOPED " + st.Tag + " cap=" + it.TargetCapability.Value + " node=" + it.TargetNodeId);
                    }
                    break;
                case ActionKind.Withdraw:
                    _world.Withdraw(st, it.TargetNodeId);
                    RenameAbandonedStation(it.TargetNodeId);
                    LogShipEvacuations();
                    break;
                case ActionKind.DismantleCapability:
                    if (it.TargetCapability.HasValue)
                    {
                        _world.DismantleCapability(st.Tag, it.TargetNodeId, it.TargetCapability.Value);
                        _oracle?.InvalidateNode(it.TargetNodeId);
                        _log.Write("t" + _simTick + " DISMANTLE " + st.Tag + " cap=" + it.TargetCapability.Value + " node=" + it.TargetNodeId);
                    }
                    break;
                case ActionKind.Negotiate: Diplomacy.OfferPeace(st, it.TargetFactionTag, _world.UtcNow); break;
                case ActionKind.ProposePact:
                    Diplomacy.Propose(st, it.TargetFactionTag,
                        new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, _world.UtcNow);
                    _log.Write("t" + _simTick + " PROPOSE_PACT " + st.Tag + " -> " + it.TargetFactionTag);
                    break;
                case ActionKind.Campaign:
                {
                    NodeRegion campaignRegion = NodeRegion.None;
                    bool namesRegion = it.TargetRegion != null && NodeRegion.TryFromKey(it.TargetRegion, out campaignRegion);
                    bool terror = it.Cause.HasValue && it.Cause.Value == CampaignCause.Terror;
                    if (terror && namesRegion)
                    {
                        // A terror campaign: the node is the staging base, the region is the target.
                        bool terrorStarted = TerrorCampaigns.TryStart(st, _tuning, _world.UtcNow, it.TargetNodeId, campaignRegion,
                            _world, _prefabCatalogue);
                        enacted = terrorStarted;
                        CampaignObjective terrorObjective = terrorStarted ? TerrorCampaigns.Active(st) : null;
                        _log.Write("t" + _simTick + " TERROR_CAMPAIGN " + (terrorStarted ? "STARTED " : "REJECTED ")
                                   + st.Tag + " " + campaignRegion.Describe() + " staging=" + it.TargetNodeId
                                   + " hulls=" + _tuning.TerrorCampaignHulls
                                   + " dps=" + (terrorObjective != null ? terrorObjective.WantedCombatDps : 0)
                                   + " escalation=" + st.TerrorEscalation + (st.TerrorRevenge ? " revenge" : "")
                                   + " until=" + (_world.UtcNow + _tuning.TerrorCampaignDuration).ToString("HH:mm:ss"));
                        break;
                    }
                    // A raid or conquest campaign: the focus stands on the staging node and carries
                    // one objective naming the region (or the single node) it means to act on.
                    // FleetManager dispatches under it; nothing tactical happens here.
                    if (it.ObjectiveKind.HasValue && (namesRegion || it.ObjectiveNodeId != null))
                    {
                        CampaignCause cause = it.Cause.HasValue ? it.Cause.Value : CampaignCause.Predation;
                        CampaignObjective startedObjective;
                        bool objectiveStarted = OffensiveCampaigns.TryStart(st, _tuning, _world.UtcNow, it.TargetNodeId,
                            campaignRegion, it.ObjectiveNodeId, it.ObjectiveKind.Value, cause,
                            _world, _prefabCatalogue, out startedObjective);
                        enacted = objectiveStarted;
                        if (objectiveStarted)
                            _log.Write("t" + _simTick + " CAMPAIGN_OBJECTIVE STARTED " + st.Tag
                                       + " kind=" + OffensiveCampaigns.KindWord(it.ObjectiveKind.Value)
                                       + " cause=" + cause
                                       + " region=" + (campaignRegion.ToKey() ?? "-")
                                       + " node=" + (it.ObjectiveNodeId ?? "-")
                                       + " staging=" + it.TargetNodeId
                                       + " budget_dps=" + startedObjective.WantedCombatDps
                                       + (it.LaneBoost > 0
                                           ? " lane=" + it.LaneBoost.ToString("0.000", CultureInfo.InvariantCulture)
                                             + " lane_credits=" + it.LaneCredits.ToString("0", CultureInfo.InvariantCulture)
                                           : "")
                                       + " until=" + (_world.UtcNow + _tuning.OffensiveCampaignDuration).ToString("HH:mm:ss"));
                        break;
                    }
                    HashSet<string> members = _world.ComputeCampaignRegionMembers(st.Tag, it.TargetNodeId, _tuning.CampaignRegionTraverseDepth);
                    bool started = CampaignFocusLifecycle.TryStart(st, _tuning, _world.GetFleetCombatPower(st.Tag),
                        _world.UtcNow, it.TargetNodeId, members);
                    enacted = started;
                    _log.Write("t" + _simTick + " CAMPAIGN_FOCUS " + (started ? "STARTED " : "REJECTED ")
                               + st.Tag + " region=" + it.TargetNodeId + " active=" + st.CampaignFoci.Count);
                    break;
                }
                case ActionKind.Muster:
                {
                    // Deterrence build-up — state only, no dispatch (mirrors ActionKind.Campaign above).
                    // Both firing modes raise MilitaryPosture; the Rivalry-driven (targeted) mode also
                    // records which rival is being out-built, consumed by ShipyardManager/Enact's
                    // ConvoySize scaling alongside the untargeted posture.
                    st.MilitaryPosture = Math.Min(1.0, st.MilitaryPosture + _tuning.MilitaryPostureGain);
                    if (it.TargetFactionTag != null)
                    {
                        st.MusterFocusTag = it.TargetFactionTag;
                        st.MusterFocusPriority = st.Drives[Drive.Rivalry];
                        st.MusterSetAtUtc = _world.UtcNow;
                        _log.Write("t" + _simTick + " MUSTER_FOCUS " + st.Tag + " target=" + it.TargetFactionTag
                                   + " posture=" + st.MilitaryPosture.ToString("0.00")
                                   + " priority=" + st.MusterFocusPriority.ToString("0.00"));
                    }
                    else
                    {
                        _log.Write("t" + _simTick + " MUSTER " + st.Tag + " posture=" + st.MilitaryPosture.ToString("0.00"));
                    }
                    break;
                }
                case ActionKind.Found:
                {
                    bool founded = _stationFounder.FoundStation(it.TargetNodeId, st, _world.Graph);
                    if (founded) _log.Write("t" + _simTick + " FOUND " + st.Tag + " node=" + it.TargetNodeId);
                    break;
                }

                case ActionKind.FinishBuild:
                    _shipyardManager.ForceCollect(st, _world, it.TargetNodeId, _tuning, _managerLog);
                    break;

                // Gift is an in-flight abstract event: its effect applies on arrival, and the
                // physical grid materialises only when a player observes the interpolated position.
                // Gated by ShipStock -- ShipyardManager must have built a ship at the origin node.
                // Every other fleet is operational and dispatched by a manager: Raid, Retaliate and
                // Conquer by FleetManager's offensive block under a campaign objective, Patrol and
                // Guard by its patrol and defend blocks, Trade by TradeManager.
                case ActionKind.Gift:
                {
                    bool immediate = _spawner == null || _spawner.SkipsTransit;
                    PendingFleetAction fleet;
                    // A false return is blocked-on-stock: abort without cost or cooldown so the
                    // brain can pick something else next tick.
                    if (!OffensiveDispatch.Dispatch(
                            st, it, _world, _tuning, immediate, _prefabCatalogue,
                            _itemVolumes, _itemMasses, _blockedCombatFleetReports,
                            DispatchLogSink, _eventWriter, out fleet))
                        return;
                    break;
                }
            }

            // Deduct item costs (after the action succeeds, before the credit cost)
            switch (it.Kind)
            {
                case ActionKind.Expand:
                    _world.AddInventory(st.Tag, ItemKind.ProcessedGood, -_tuning.ExpandItemCostProcessedGood);
                    break;
                case ActionKind.Develop:
                    if (it.TargetCapability.HasValue)
                    {
                        NodeCapabilityKind cap = it.TargetCapability.Value;
                        Dictionary<string, long> bom;
                        if (_tuning.TryGetInstallBom(cap, out bom))
                        {
                            foreach (var kv in bom)
                                _world.ConsumeFactionReservedInventoryByKey(st.Tag, kv.Key, kv.Value);
                        }
                        else
                        {
                            long oreDeduct   = _tuning.InstallCostFor(cap, ItemKind.Ore);
                            long ingotDeduct = _tuning.InstallCostFor(cap, ItemKind.Ingot);
                            long pgDeduct    = _tuning.InstallCostFor(cap, ItemKind.ProcessedGood);
                            long h2Deduct    = _tuning.InstallCostFor(cap, ItemKind.Hydrogen);
                            if (oreDeduct   > 0) _world.AddReservedInventory(st.Tag, ItemKind.Ore,          -oreDeduct);
                            if (ingotDeduct > 0) _world.AddReservedInventory(st.Tag, ItemKind.Ingot,        -ingotDeduct);
                            if (pgDeduct    > 0) _world.AddReservedInventory(st.Tag, ItemKind.ProcessedGood, -pgDeduct);
                            if (h2Deduct    > 0) _world.AddReservedInventory(st.Tag, ItemKind.Hydrogen,      -h2Deduct);
                        }
                    }
                    break;
                case ActionKind.Found:
                    _world.AddInventory(st.Tag, ItemKind.ProcessedGood, -_tuning.FoundItemCostProcessedGood);
                    _world.AddInventory(st.Tag, ItemKind.Hydrogen, -_tuning.FoundItemCostHydrogen);
                    break;
            }

            if (!enacted) return;
            _world.Spend(st.Tag, it.Cost);

            // Fleet-kind cooldowns get jitter so convoys on parallel lanes don't pulse in sync.
            TimeSpan baseCooldown = _tuning.CooldownFor(it.Kind);
            bool isFleetKind = it.Kind == ActionKind.Gift;
            TimeSpan jitter = isFleetKind ? TimeSpan.FromMinutes(rng.NextDouble() * _tuning.FleetCooldownJitter.TotalMinutes) : TimeSpan.Zero;
            if (it.Kind != ActionKind.Campaign)
                st.Cooldowns[it.Kind] = _world.UtcNow + baseCooldown + jitter;

            // Gift gates the destination lane to prevent repeat convoys to the same partner.
            if (it.Kind == ActionKind.Gift && it.TargetFactionTag != null)
            {
                TimeSpan destJitter = TimeSpan.FromMinutes(rng.NextDouble() * _tuning.FleetCooldownJitter.TotalMinutes);
                st.TradeDestCooldowns[it.TargetFactionTag] = _world.UtcNow + _tuning.GiftDestCooldown + destJitter;
            }
        }

        private TimeSpan CooldownFor(ActionKind kind) { return _tuning.CooldownFor(kind); }

        /// <summary>Slice A's observable. There is no consumer of the knowledge store yet, so the
        /// only honest way to see it working is to count it -- and the failure modes are countable:
        /// an empty set means detection never ran, and a set equal to the whole graph means the
        /// filtering did not. "No exceptions in the log" would distinguish neither.</summary>
        public string DescribeKnowledge(string tag)
        {
            FactionState st = FindState(tag);
            if (st == null) return "knowledge: no such faction '" + tag + "'.";
            DateTime nowUtc = _world.UtcNow;
            TimeSpan horizon = _tuning.DiscoveryHorizon;

            int owned = 0;
            foreach (SpatialNode n in _world.Graph.Nodes.Values)
                if (n != null && n.OwnerFactionTag == st.Tag) owned++;

            double birthrightRange = _tuning.BirthrightRangeKm;
            HashSet<string> birthright = KnowledgeView.KnownNodeIds(
                _world.Graph, st.Tag, null, nowUtc, horizon, birthrightRange);
            HashSet<string> known = KnowledgeView.KnownNodeIds(
                _world.Graph, st.Tag, st.Knowledge, nowUtc, horizon, birthrightRange);

            int stored = st.Knowledge.Count;
            int live = 0;
            foreach (KeyValuePair<string, DateTime> kv in st.Knowledge.Entries)
                if (horizon <= TimeSpan.Zero || nowUtc - kv.Value < horizon) live++;

            int graphNodes = _world.Graph.Nodes.Count;
            double pct = graphNodes > 0 ? 100.0 * known.Count / graphNodes : 0.0;
            return "knowledge " + st.Tag
                 + ": owned=" + owned
                 + " birthright=" + birthright.Count
                 + " learned=" + live + "/" + stored + " live"
                 + " known=" + known.Count + "/" + graphNodes
                 + " (" + pct.ToString("0.0") + "% of graph)"
                 + " horizon=" + (int)horizon.TotalDays + "d"
                 // Whether the brains are actually gated on any of this. Without it the verb reports
                 // the same numbers whether slice C is on or off, and those numbers are the record,
                 // not the behaviour.
                 + " gated=" + (_tuning.KnowledgeGatedBrains ? "on" : "off");
        }

        /// <summary>Faction state by tag, for the map's viewer union. Read-only use.</summary>
        internal FactionState FindStateForMap(string tag)
        {
            return FindState(tag);
        }

        private FactionState FindState(string tag)
        {
            foreach (FactionState s in _states)
                if (string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        // Manager log entries use "<TAG> <KEYWORD> ..." (most managers) or "<KEYWORD> <TAG> ..."
        // (TradeManager). HasKeyword matches either: keyword at position 0 or after a space.
        private static bool HasKeyword(string msg, string kw)
        {
            int i = msg.IndexOf(kw, StringComparison.Ordinal);
            return i >= 0 && (i == 0 || msg[i - 1] == ' ');
        }

        /// <summary>Writes every unsuppressed line currently in <see cref="_managerLog"/> to <see
        /// cref="_log"/> (prefixed "mgr ") and clears the buffer. Shared by every exit path of <see
        /// cref="DrainManagerPhase"/> — a yielded resumable-sweep return and the normal end-of-call
        /// path both need the exact same flush.</summary>
        /// <summary>
        /// Folds the event rings captured at the top of <see cref="Load"/> back into the freshly
        /// loaded factions, and files anything that was recorded but not yet drained when the old
        /// world was discarded.
        ///
        /// <para>Only factions that came back from disk receive anything: a tag that is gone after
        /// the reload -- forgotten, ignored, merged away -- stays gone rather than being resurrected
        /// by its own history.</para>
        /// </summary>
        private void RestoreCarriedEvents(Dictionary<string, List<FactionEvent>> carriedRings,
                                          List<FactionEvent> carriedPending)
        {
            int merged = 0;
            if (carriedRings != null)
            {
                foreach (FactionState st in _states)
                {
                    List<FactionEvent> carried;
                    if (st == null || st.Tag == null || !carriedRings.TryGetValue(st.Tag, out carried)) continue;
                    merged += FactionEventLog.Merge(st, carried, _tuning.RecentEventCap);
                }
            }

            int filed = 0;
            if (carriedPending != null)
                foreach (FactionEvent e in carriedPending)
                    filed += FactionEventLog.File(_states, e, _tuning.RecentEventCap);

            if (merged > 0 || filed > 0)
                _log.Write("load_reconcile events carried=" + merged + " undrained=" + filed);
        }

        private void FlushManagerLog()
        {
            foreach (string msg in _managerLog)
                if (!IsManagerLogSuppressed(msg)) _log.Write("mgr " + msg);
            _managerLog.Clear();
        }

        private bool IsManagerLogSuppressed(string msg)
        {
            if (!DebugTraces.Produced     && HasKeyword(msg, "PRODUCED"))          return true;
            if (!DebugTraces.ItemXfer     && HasKeyword(msg, "ITEM_XFER"))         return true;
            if (!DebugTraces.UpkeepIncome && (HasKeyword(msg, "UPKEEP") || HasKeyword(msg, "INCOME"))) return true;
            if (!DebugTraces.ItemDemand   && (HasKeyword(msg, "PROD_INPUT_DEMAND") || HasKeyword(msg, "ITEM_DEMAND")
                                          || HasKeyword(msg, "REFINE_DEMAND")     || HasKeyword(msg, "INSTALL_DEMAND"))) return true;
            if (!DebugTraces.ShipEvents   && (HasKeyword(msg, "SHIP_DEMAND") || HasKeyword(msg, "BUILD_QUEUED")
                                          || HasKeyword(msg, "SHIP_BUILT")   || HasKeyword(msg, "BUILD_EXPIRED")
                                          || HasKeyword(msg, "BUILD_SLOTS"))) return true;
            if (!DebugTraces.Investment   && (HasKeyword(msg, "INVEST_REQUEST") || HasKeyword(msg, "REFINE_INVEST"))) return true;
            return false;
        }

        /// <summary>
        /// Call after hot-reloading tuning.json. Re-runs the oracle's reference-stock cache so
        /// any buffer-horizon or capability-weight changes take effect immediately.
        /// </summary>
        public void NotifyTuningReloaded()
        {
            _oracle?.RecomputeAll();
            SyncPerfSampleCapacity();
            InvalidateSensorOcclusion();   // SensorHorizonLiftKm is baked into the built field
        }

        /// <summary>The bodies that block a sightline, for every sensing path on this host.
        ///
        /// <para>Built on first read and reused: planets do not move, and this is read once per
        /// detection pair in the knowledge sweep. An empty answer is <b>not</b> cached — early in a
        /// session <c>MyPlanets</c> has not registered its bodies yet, and caching that would leave
        /// every sightline clear for the life of the process with nothing to heal it.</para></summary>
        public SensorOcclusion SensorOcclusion
        {
            get
            {
                if (_sensorOcclusion != null) return _sensorOcclusion;
                IPlanetCatalogue planets = _world != null && _world.Planets != null ? _world.Planets : _planets;
                SensorOcclusion built = SensorOcclusion.From(planets, _tuning);
                if (built.Count == 0) return built;
                _sensorOcclusion = built;
                return _sensorOcclusion;
            }
        }

        /// <summary>Drops the built occlusion field so the next read rebuilds it. Two things change
        /// it: a tuning reload (the lift) and <c>factions world apply</c> (the bodies).</summary>
        public void InvalidateSensorOcclusion()
        {
            _sensorOcclusion = null;
        }

        /// <summary>Applies a versioned transit contract to all still-pending fleets without moving
        /// them backwards or forwards on their routes.</summary>
        public int ReschedulePendingFleetTransits()
        {
            int changed = PendingFleetResolver.ReschedulePendingFleetTransits(_states, _world, _tuning, _world.UtcNow);
            if (changed > 0) Flush();
            return changed;
        }

        // ---- PvEBounty target lookup (Guard→PvEBounty contract pattern) -----------------------

        /// <summary>Finds an unresolved incoming Raid/Retaliate/Conquer targeting <paramref
        /// name="nodeId"/> — the candidate a station's contract-generation path should offer a bounty
        /// against. A pure lookup, no side effects (see docs/contracts.md's Guard-via-PvEBounty
        /// non-goals): finding, or even offering, a bounty must never itself change the fleet.</summary>
        public bool TryFindIncomingThreat(string nodeId, out PendingFleetAction action)
        {
            return PendingFleetResolver.TryFindIncomingThreat(_states, nodeId, out action);
        }

        // ---- Material vector lookups + claim tracking (Slice C player hauling contracts) ------

        // Per-faction caches for the O(n) rebuilds TryMatchHaulSource/TryMatchHaulDemand would
        // otherwise redo on every single call -- live-confirmed 2026-08-08: vanilla's own
        // per-station contract-board-refill retry loop (CreateContractsForStation) calls
        // GenerateContract synchronously, hundreds of times in one real-time burst (274,233 "Contract
        // generated" lines logged in one day; a GC memory climb from ~950MB to ~3.2GB across the four
        // 30s samples spanning one such burst). Identical shape to the pre-existing
        // FindNodeForStation/FindItemDefinition freeze this file already fixed with short-TTL/
        // session caches (see StationIndexTtl/s_stationToNode above) -- WorldModel.Demand
        // .SnapshotItemDemands scans the ENTIRE GLOBAL demand list (not just this faction's) and
        // WorldModel.Graph.OwnedBy scans the entire global node graph, both on every single call;
        // neither result changes meaningfully within one real-time burst. Keyed by real wall-clock
        // time (DateTime.UtcNow), not the sim clock, matching StationIndexTtl's own reasoning --
        // the burst is a real-time phenomenon (SE's Update loop), unrelated to sim-tick cadence.
        private static readonly TimeSpan HaulLookupCacheTtl = TimeSpan.FromSeconds(5);
        private readonly Dictionary<string, (List<DemandEntry> Demands, DateTime BuiltUtc)> _haulDemandCache =
            new Dictionary<string, (List<DemandEntry>, DateTime)>();
        private readonly Dictionary<string, (List<SpatialNode> Nodes, DateTime BuiltUtc)> _haulOwnedNodeCache =
            new Dictionary<string, (List<SpatialNode>, DateTime)>();

        private List<DemandEntry> CachedItemDemands(string factionTag)
        {
            (List<DemandEntry> Demands, DateTime BuiltUtc) cached;
            DateTime now = DateTime.UtcNow;
            if (_haulDemandCache.TryGetValue(factionTag, out cached) && now - cached.BuiltUtc <= HaulLookupCacheTtl)
                return cached.Demands;

            List<DemandEntry> demands = _world.Demand.SnapshotItemDemands(factionTag);
            demands.Sort((a, b) => b.Quantity.CompareTo(a.Quantity));
            _haulDemandCache[factionTag] = (demands, now);
            return demands;
        }

        private List<SpatialNode> CachedOwnedNodes(string factionTag)
        {
            (List<SpatialNode> Nodes, DateTime BuiltUtc) cached;
            DateTime now = DateTime.UtcNow;
            if (_haulOwnedNodeCache.TryGetValue(factionTag, out cached) && now - cached.BuiltUtc <= HaulLookupCacheTtl)
                return cached.Nodes;

            List<SpatialNode> owned = new List<SpatialNode>(_world.Graph.OwnedBy(factionTag));
            _haulOwnedNodeCache[factionTag] = (owned, now);
            return owned;
        }

        /// <summary>Dest-anchored: does any node <paramref name="factionTag"/> owns have real surplus
        /// of <paramref name="subTypeId"/> that could serve <paramref name="nodeId"/>'s outstanding
        /// need? A pure lookup (see <see cref="OperationsManager.TryFindSource"/>) — used by the
        /// Acquisition contract path to know whether it's backed by a real in-network vector.</summary>
        public bool TryMatchHaulSource(string factionTag, string nodeId, string subTypeId, long quantity,
            out MaterialVector vector)
        {
            List<SpatialNode> owned = CachedOwnedNodes(factionTag);
            return OperationsManager.TryFindSource(factionTag, subTypeId, nodeId, quantity, owned, _world, out vector);
        }

        /// <summary>Source-anchored: does <paramref name="sourceNodeId"/>'s own surplus match any
        /// outstanding demand elsewhere in the faction? The mirror of <see cref="TryMatchHaulSource"/>
        /// — used to generate a player Haul contract at a station that currently holds real surplus.</summary>
        public bool TryMatchHaulDemand(string factionTag, string sourceNodeId, out MaterialVector vector)
        {
            List<DemandEntry> demands = CachedItemDemands(factionTag);
            return OperationsManager.TryFindDemand(factionTag, sourceNodeId, demands, _world, out vector);
        }

        /// <summary>Claims/releases a <c>(NodeId, ItemKey)</c> pair on behalf of an accepted player
        /// Acquisition/Haul contract, exactly mirroring the in-flight-NPC-convoy check
        /// <see cref="OperationsManager.Tick"/> already applies — see <see
        /// cref="FactionState.ExternalHaulClaims"/>. No-op if the faction isn't found.</summary>
        public void ClaimHaulTarget(string factionTag, string nodeId, string itemKey)
        {
            FactionState st = FindState(factionTag);
            st?.ExternalHaulClaims.Add((nodeId, itemKey));
        }

        public void ReleaseHaulTarget(string factionTag, string nodeId, string itemKey)
        {
            FactionState st = FindState(factionTag);
            st?.ExternalHaulClaims.Remove((nodeId, itemKey));
        }

        // -------------------------------------------------------------------------------------

        /// <summary>Resolve any fleet events that were in-transit when the server was offline
        /// (EstimatedArrivalUtc &lt;= world.UtcNow). Called once during Load() so a reboot never leaves
        /// factions in a permanently-in-transit state. Assumes full completion ratio = 1.0 (player-
        /// friendly: treats downtime as an uncontested transit).</summary>
        private void ReconcileAndResolveOnLoad()
        {
            var buf = new List<string>();
            var rng = new SeededRandom(20260606);

            // Strip cooldown entries with sim-clock timestamps: any entry more than 24 h in the
            // future is from the old 6× sim clock. The longest legitimate cooldown is ExpandCooldown
            // (8 h), so 24 h is a safe threshold that won't clip anything valid.
            DateTime cooldownCutoff = _world.UtcNow.AddHours(24);
            foreach (FactionState st in _states)
            {
                var staleCooldownKinds = new List<ActionKind>();
                foreach (var kv in st.Cooldowns)
                    if (kv.Value > cooldownCutoff) staleCooldownKinds.Add(kv.Key);
                foreach (ActionKind k in staleCooldownKinds)
                {
                    st.Cooldowns.Remove(k);
                    buf.Add("stale_cooldown " + st.Tag + " " + k + " — clock migration, cleared");
                }
                staleCooldownKinds.Clear();

                var staleDestKeys = new List<string>();
                foreach (var kv in st.TradeDestCooldowns)
                    if (kv.Value > cooldownCutoff) staleDestKeys.Add(kv.Key);
                foreach (string dest in staleDestKeys)
                {
                    st.TradeDestCooldowns.Remove(dest);
                    buf.Add("stale_dest_cooldown " + st.Tag + " -> " + dest + " — clock migration, cleared");
                }
                staleDestKeys.Clear();
            }

            // Snapshot Conquer targets before the Core resolver removes finished fleet records.
            // This handles successful captures that complete while Torch was offline too.
            var offlineConquerTargets = new List<Tuple<string, string, string>>();
            foreach (FactionState st in _states)
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    if (fleet.Kind != ActionKind.Conquer || fleet.TargetNodeId == null) continue;
                    SpatialNode target;
                    if (_world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out target) && target.OwnerFactionTag != null)
                        offlineConquerTargets.Add(Tuple.Create(fleet.TargetNodeId, st.Tag, target.OwnerFactionTag));
                }

            // Snapshot entity IDs of materialised fleets before reconciliation so that any whose
            // fleet record gets resolved can have their physical grid despawned (prevents orphaned
            // grids persisting in the world save after a reboot).
            // A squadron's escorts are grids in the same world with the same fate as their lead, so
            // they are snapshot and subtracted alongside it — otherwise a fleet resolved across the
            // restart leaves every hull but the lead behind as an orphan.
            var eidsBeforeResolve = new HashSet<long>();
            foreach (FactionState st in _states)
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (f.MaterialisedEntityId > 0)
                        eidsBeforeResolve.Add(f.MaterialisedEntityId);
                    if (f.EscortEntityIds != null)
                        for (int ei = 0; ei < f.EscortEntityIds.Count; ei++)
                            if (f.EscortEntityIds[ei] > 0)
                                eidsBeforeResolve.Add(f.EscortEntityIds[ei]);
                }

            PendingFleetResolver.ReconcileAndResolveOnLoad(_states, _world, _tuning, rng, buf, _economy, _itemPrices, _itemVolumes,
                _oracle, _tradeManager?.InboundBudget, OnFleetResolved, _itemMasses);

            bool renamedOfflineCapture = false;
            foreach (Tuple<string, string, string> conquer in offlineConquerTargets)
            {
                SpatialNode captured;
                if (!_world.Graph.Nodes.TryGetValue(conquer.Item1, out captured)) continue;
                if (!string.Equals(captured.OwnerFactionTag, conquer.Item2, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(conquer.Item3, conquer.Item2, StringComparison.OrdinalIgnoreCase)) continue;
                FactionState newOwner = _states.FirstOrDefault(s => string.Equals(s.Tag, conquer.Item2, StringComparison.OrdinalIgnoreCase));
                if (newOwner != null && _stationFounder.RenameConqueredStation(captured, newOwner))
                {
                    renamedOfflineCapture = true;
                    buf.Add("STATION_RENAMED " + conquer.Item1 + " -> " + captured.DisplayName);
                }
            }
            if (renamedOfflineCapture) FlushOrDefer("offline capture renames");

            // Despawn grids whose fleet was resolved (no longer in any PendingFleets list).
            if (_spawner != null && eidsBeforeResolve.Count > 0)
            {
                foreach (FactionState st in _states)
                    foreach (PendingFleetAction f in st.PendingFleets)
                    {
                        eidsBeforeResolve.Remove(f.MaterialisedEntityId);
                        if (f.EscortEntityIds != null)
                            for (int ei = 0; ei < f.EscortEntityIds.Count; ei++)
                                eidsBeforeResolve.Remove(f.EscortEntityIds[ei]);
                    }
                foreach (long eid in eidsBeforeResolve)
                {
                    // closed=0 means the spawner found no grid behind the id: it was already
                    // gone, or the close did not take. Not warned on — an orphan sweep finding
                    // nothing behind a stale id is its ordinary case, not a fault.
                    int closed = _spawner.Despawn(eid);
                    buf.Add("despawn_orphan eid=" + eid + " closed=" + closed);
                }
                eidsBeforeResolve.Clear();
            }

            foreach (string msg in buf) _log.Write("load_reconcile " + msg);
            buf.Clear();
        }

        /// <summary>Apply discrete raid fallout, then conclude any truces where peace overtures meet
        /// a willing partner. Continuous relation drift and war fatigue run on the economy cadence.</summary>
        private void ApplyDiplomaticEvents()
        {
            // The drain itself lives in Core so the offline sim applies the identical rule; only
            // the sink for its lines is ours.
            long tick = _simTick;
            Diplomacy.ApplyIncidents(_states, _world, _tuning, line => _log.Write("t" + tick + " " + line));

            // Events are drained per frame (see OnUpdateFrame), not here: this phase runs on the
            // global-pass cadence, and a recorded event that waits that long is one a restart can
            // still destroy. Kept as a belt-and-braces call for the direct Step() path, which does
            // not go through OnUpdateFrame at all.
            FactionEventLog.Drain(_states, _world, _tuning.RecentEventCap);
            Diplomacy.ProcessProposals(_states, _world, _tuning,
                onTruce: (a, b) =>
                {
                    _log.Write("t" + _simTick + " TRUCE " + a + " <-> " + b + " (war fatigue -> peace)");
                    // The SE table is no longer written here: the projection pass below runs in the
                    // same phase, sees the truce that was just stamped, and floors the pair at
                    // Neutral. Kept as a line because "peace concluded" is the event; "the table
                    // moved" is the projection's own DIPLOMACY_SE_RELATION. For a player pair that
                    // band change is also what reaches clients: the projection raises vanilla's
                    // AcceptPeace broadcast, which clears the white flag (BroadcastPlayerBand).
                    _log.Write("DIPLOMACY_PEACE_CONCLUDED " + a + " <-> " + b
                             + " (truce settled; SE relation table follows on this pass's projection)");
                },
                onPact:  (a, b) => _log.Write("t" + _simTick + " PACT "  + a + " <-> " + b + " (formal alliance)"));

            ProjectRelationsToSe("diplomacy");
            ReconcileVanillaRequests();

            Action after = AfterDiplomacyPass;
            if (after != null)
            {
                try { after(); }
                catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Post-diplomacy host pass failed."); }
            }
        }

        /// <summary>
        /// Projects Factions' own relation model onto SE's faction table (FAC-274).
        ///
        /// <para><b>Factions is authoritative; SE's table is a view of it.</b> Before this,
        /// <c>m_relationsBetweenFactions</c> was written once at founding from
        /// <c>faction_recipe.json</c> and never again, so a pair the brain had moved to +0.86 was
        /// still <c>Enemies</c> to every turret in the world — and nothing at all projected an
        /// NPC–NPC standing or a standing that <i>fell</i>. This replaces that one-way, player-only
        /// bridge (<c>ProjectTruceToSe</c>, folded in here) with the whole table.</para>
        ///
        /// <para><b>Which of the two directions wins.</b> The brain's <c>Relations</c> are
        /// directional — A may hold B at +0.9 while B holds A at −0.5 — and SE stores one cell per
        /// unordered pair. The <b>more hostile</b> of the two views is the one projected: a
        /// relationship is only as friendly as its unhappier half, and the alternative (a mean)
        /// would let an aggressor's own good opinion of its victim disarm the victim's turrets.</para>
        ///
        /// <para><b>An active truce floors the pair at Neutral</b>, whichever side holds it. That is
        /// what makes folding <c>ProjectTruceToSe</c> safe: a concluded truce leaves both standings
        /// where they were (only <c>TruceUntil</c> moves), so without the floor the very next
        /// projection would put the pair straight back to Enemies and the player's "accept peace"
        /// click would be undone within one pass.</para>
        ///
        /// <para><b>Written on band change, not on every pass.</b> The table is a session-wide
        /// structure and the interesting event is a stance flipping, not a standing drifting.
        /// <c>GetRelationBetweenFactions</c> answers for a pair with no row at all from
        /// <c>MyPerGameSettings.DefaultFactionRelationshipAndReputation</c> (Enemies/HostileMid), so
        /// such a pair reads as Enemies and is written on the first pass that disagrees.</para>
        ///
        /// <para><c>SetReputationBetweenFactions</c> is the safe door: it writes one relation entry
        /// and touches no member reputations, unlike the <c>ChangeFactionRelation</c> path
        /// <see cref="FactionDiplomacyPatch"/> intercepts (FAC-185). MUST be called on the game
        /// thread — same seat as <see cref="ApplyRelations"/>.</para>
        ///
        /// <returns>How many pairs were written.</returns>
        /// </summary>
        private int ProjectRelationsToSe(string reason)
        {
            if (_states == null || _states.Count < 2) return 0;
            try
            {
                Sandbox.Game.World.MySession session = Sandbox.Game.World.MySession.Static;
                if (session == null || session.Factions == null) return 0;
                ReputationScale scale = ReadReputationScale(session);
                if (scale == null) return 0;   // no economy component: this world has no reputation scale

                DateTime nowUtc = _world != null ? _world.UtcNow : DateTime.UtcNow;
                int changed = 0, pairs = 0;
                for (int i = 0; i < _states.Count; i++)
                {
                    FactionState a = _states[i];
                    if (a == null || string.IsNullOrEmpty(a.Tag)) continue;
                    Sandbox.Game.World.MyFaction fa = session.Factions.TryGetFactionByTag(a.Tag);
                    if (fa == null) continue;

                    for (int j = i + 1; j < _states.Count; j++)
                    {
                        FactionState b = _states[j];
                        if (b == null || string.IsNullOrEmpty(b.Tag)) continue;
                        Sandbox.Game.World.MyFaction fb = session.Factions.TryGetFactionByTag(b.Tag);
                        if (fb == null) continue;
                        pairs++;

                        double rel = Math.Min(a.RelationTo(b.Tag), b.RelationTo(a.Tag));
                        if (Diplomacy.UnderTruce(a, b.Tag, nowUtc) || Diplomacy.UnderTruce(b, a.Tag, nowUtc))
                            rel = Math.Max(rel, 0.0);   // a ceasefire is at least Neutral in SE, whatever they think
                        // Two sides of a live bench scenario shoot at each other whatever the model says;
                        // the pair is projected back from the model once the scenario ends.
                        if (BenchForcesHostile(a.Tag, b.Tag)) rel = -1.0;

                        int rep = scale.ReputationFor(rel, _tuning.HostileRelationThreshold, _tuning.AllyRelationThreshold);
                        Tuple<VRage.Game.MyRelationsBetweenFactions, int> current =
                            session.Factions.GetRelationBetweenFactions(fa.FactionId, fb.FactionId);
                        int before = current != null ? current.Item2 : scale.NeutralMid;
                        string bandBefore = scale.BandOf(before), bandAfter = scale.BandOf(rep);
                        if (bandBefore == bandAfter) continue;

                        session.Factions.SetReputationBetweenFactions(fa.FactionId, fb.FactionId, rep);
                        session.Factions.SetReputationBetweenFactions(fb.FactionId, fa.FactionId, rep);
                        changed++;
                        if (DebugTraces.Diplomacy)
                            _log.Write("DIPLOMACY_SE_RELATION " + a.Tag + " " + b.Tag + " rel="
                                     + rel.ToString("0.00", CultureInfo.InvariantCulture) + " -> " + rep
                                     + " (" + bandAfter + ", was " + before + " " + bandBefore + ")");
                        BroadcastPlayerBand(a, fa, b, fb, bandAfter);
                    }
                }

                if (changed > 0)
                    _log.Write("DIPLOMACY_SE_SYNC " + reason + " changed=" + changed + " of " + pairs + " pair(s)");
                return changed;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Projecting Factions' relations into SE failed; the game "
                    + "still reads the previous relation table for this pass.");
                return 0;
            }
        }

        /// <summary>
        /// Tells every connected client about a band change the projection just wrote, for a
        /// PlayerMade faction and a managed NPC only (FAC-394 slice 5).
        ///
        /// <para><b>Chosen: raise the matching vanilla state change</b> — DeclareWar, AcceptPeace
        /// or AcceptFriendRequest — through <see cref="VanillaDiplomacyBroadcast"/>. SE has no
        /// relation-replication event, and this is the broadcast vanilla's own buttons use, so an
        /// unmodded client updates its table, its Factions screen and any white flag at once.
        /// Clients hold the band's midpoint rather than the exact projected number; SE reads only
        /// the band. The server's own table is left as projected (the broadcast is skipped on
        /// apply there). NPC–NPC pairs are not broadcast: no client acts on them.</para>
        ///
        /// <para><b>Direction is chosen for SE's achievements, which key on it.</b> War is raised as
        /// the NPC's act and peace as the player faction's, so neither the DeclareWar nor the WinWin
        /// achievement (both need a PlayerMade <c>to</c> side and the local leader on the right
        /// side) fires for a stance the brain moved.</para>
        /// </summary>
        private static void BroadcastPlayerBand(FactionState a, Sandbox.Game.World.MyFaction fa,
                                                FactionState b, Sandbox.Game.World.MyFaction fb, string band)
        {
            FactionState player, npc;
            Sandbox.Game.World.MyFaction fPlayer, fNpc;
            if (IsManagedPlayerFaction(a, fa) && !b.IsPlayerFaction) { player = a; fPlayer = fa; npc = b; fNpc = fb; }
            else if (IsManagedPlayerFaction(b, fb) && !a.IsPlayerFaction) { player = b; fPlayer = fb; npc = a; fNpc = fa; }
            else return;

            VanillaFactionAct act = VanillaDiplomacy.ActForBand(band);
            if (act == VanillaFactionAct.None) return;
            if (act == VanillaFactionAct.DeclareWar)
                VanillaDiplomacyBroadcast.Raise(act, fNpc.FactionId, fPlayer.FactionId, npc.Tag, player.Tag, "projection");
            else
                VanillaDiplomacyBroadcast.Raise(act, fPlayer.FactionId, fNpc.FactionId, player.Tag, npc.Tag, "projection");
        }

        /// <summary>A registered player faction whose SE faction is PlayerMade — the side of a pair
        /// the vanilla Factions screen's buttons act for.</summary>
        private static bool IsManagedPlayerFaction(FactionState s, Sandbox.Game.World.MyFaction se)
        {
            return s != null && s.IsPlayerFaction && se != null && se.FactionType == VRage.Game.MyFactionTypes.PlayerMade;
        }

        /// <summary>
        /// Makes SE's peace-request set (the white flag on the vanilla Factions screen) match the
        /// truce offers <see cref="Diplomacy"/> holds, for every PlayerMade faction and managed NPC
        /// at war in SE, in both directions (FAC-394 slice 5).
        ///
        /// <para>An offer that lapsed on <see cref="Diplomacy.OfferTtl"/> (the player window, since
        /// every pair here has a player side), or that
        /// ProcessProposals dropped, gets CancelPeaceRequest; an open offer with no flag (posted by
        /// the brain toward a player, by a command, or surviving a restart) gets SendPeaceRequest,
        /// which a player can then accept from the screen. A concluded truce is not handled here:
        /// the projection that runs just before moves the pair out of Enemies and raises
        /// AcceptPeace, which clears the flag. The rules are
        /// <see cref="VanillaDiplomacy.ReconcileRequest"/>'s. Game thread only.</para>
        ///
        /// <returns>How many broadcasts were raised.</returns>
        /// </summary>
        private int ReconcileVanillaRequests()
        {
            if (_states == null || _states.Count < 2 || _tuning == null) return 0;
            try
            {
                Sandbox.Game.World.MySession session = Sandbox.Game.World.MySession.Static;
                if (session == null || session.Factions == null) return 0;
                DateTime nowUtc = _world != null ? _world.UtcNow : DateTime.UtcNow;
                int raised = 0;
                for (int i = 0; i < _states.Count; i++)
                {
                    FactionState p = _states[i];
                    if (p == null || !p.IsPlayerFaction || string.IsNullOrEmpty(p.Tag)) continue;
                    Sandbox.Game.World.MyFaction fp = session.Factions.TryGetFactionByTag(p.Tag);
                    if (!IsManagedPlayerFaction(p, fp)) continue;

                    for (int j = 0; j < _states.Count; j++)
                    {
                        FactionState n = _states[j];
                        if (n == null || n.IsPlayerFaction || string.IsNullOrEmpty(n.Tag)) continue;
                        Sandbox.Game.World.MyFaction fn = session.Factions.TryGetFactionByTag(n.Tag);
                        if (fn == null) continue;

                        Tuple<VRage.Game.MyRelationsBetweenFactions, int> rel =
                            session.Factions.GetRelationBetweenFactions(fp.FactionId, fn.FactionId);
                        bool atWar = rel != null && rel.Item1 == VRage.Game.MyRelationsBetweenFactions.Enemies;
                        if (!atWar) continue;

                        raised += ReconcileVanillaRequest(session, p, fp, n, fn, nowUtc);
                        raised += ReconcileVanillaRequest(session, n, fn, p, fp, nowUtc);
                    }
                }
                return raised;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Reconciling vanilla peace requests failed; the Factions screen "
                    + "keeps its previous peace flags for this pass.");
                return 0;
            }
        }

        private int ReconcileVanillaRequest(Sandbox.Game.World.MySession session,
                                            FactionState from, Sandbox.Game.World.MyFaction fFrom,
                                            FactionState to, Sandbox.Game.World.MyFaction fTo, DateTime nowUtc)
        {
            bool shown = session.Factions.IsPeaceRequestStateSent(fFrom.FactionId, fTo.FactionId);
            bool open = VanillaDiplomacy.TruceOfferOpen(from, to, nowUtc, _tuning);
            VanillaFactionAct act = VanillaDiplomacy.ReconcileRequest(true, shown, open);
            if (act == VanillaFactionAct.None) return 0;
            return VanillaDiplomacyBroadcast.Raise(act, fFrom.FactionId, fTo.FactionId, from.Tag, to.Tag, "reconcile") ? 1 : 0;
        }

        /// <summary>Writes one line to the DecisionLog. For the SE glue outside this class
        /// (Harmony patches), whose debug traces belong beside the runtime's own.</summary>
        public void WriteDecisionLine(string line)
        {
            if (line != null) _log.Write(line);
        }

        /// <summary>Whispers one player by Steam id, as the runtime's other player messages do.
        /// Silent when chat is not attached.</summary>
        public void WhisperPlayer(ulong steamId, string message)
        {
            if (_chat == null || steamId == 0UL || string.IsNullOrEmpty(message)) return;
            try { _chat.SendMessageAsOther(FleetWhisperAuthor, message, Color.CornflowerBlue, steamId); }
            catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Whispering player " + steamId + " failed."); }
        }

        /// <summary>Replaces only UNKN's live graph/state from an already-persisted brain document.
        /// Unlike <see cref="Load"/>, this never flushes, reloads, or reconstructs another faction.
        /// It exists solely for the explicit territory-recovery transaction.</summary>
        public int ReplaceUnknSources(BrainDoc unkn)
        {
            if (_world == null || unkn == null || !string.Equals(unkn.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase))
                return 0;

            var remove = _world.Graph.Nodes.Values
                .Where(n => string.Equals(n.OwnerFactionTag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Id).ToList();
            foreach (string id in remove) _world.Graph.Nodes.Remove(id);
            // Do not scrub retained nodes' neighbor lists: links can cross brain files and altering
            // a non-UNKN link here would become a later non-UNKN brain write. Traversal ignores a
            // missing endpoint; the explicit recovery is authorized to replace UNKN only.

            FactionState replacement = FactionStore.ApplyBrainDoc(unkn, _world.Graph, _tuning.CampaignCooldown);
            int stateIndex = _states.FindIndex(s => string.Equals(s.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase));
            if (stateIndex >= 0) _states[stateIndex] = replacement;
            else _states.Add(replacement);
            _oracle.RecomputeAll();
            return remove.Count;
        }

        /// <summary>Trims runtime-only graph nodes whose IDs are absent from the durable source set.
        /// Used only by the explicit territory reconciliation transaction after it has flushed state.</summary>
        public int TrimUnserializedNodes(ISet<string> serializedSourceIds)
        {
            return _world == null ? 0 : _world.Graph.TrimToSourceIds(serializedSourceIds);
        }

        /// <summary>Persist every faction's brain (sources + ledger + drives/cooldowns), the index,
        /// production/demand snapshots, and the world snapshot log -- synchronously, to completion.
        /// Callers outside <see cref="OnUpdateFrame"/> (shutdown, the registry-mutation "flush before
        /// editing disk" pattern, <see cref="Step"/>) depend on this running to completion in one call;
        /// only the periodic real-time trigger (<see cref="DrainFlush"/>) is deadline-checkpointed
        /// across frames. If a <see cref="DrainFlush"/> sweep is already mid-phase, this resumes and
        /// finishes that same sweep rather than starting a second one (see <see cref="StepFlushOnce"/>'s
        /// doc comment).</summary>
        public void Flush()
        {
            if (_world == null) return;
            if (_flushPhase == null)
            {
                _flushIndex = new List<IndexEntry>();
                _flushProductionEntries = new List<object>();
                _flushProductionHistoryEntries = new List<object>();
                _flushFactionIndex = 0;
                _flushElapsedTicks = 0;
                _flushPhase = FlushPhase.BrainDocs;
            }
            DateTime now = DateTime.UtcNow;
            while (!StepFlushOnce(now, ImmediateFlushLockedRetries)) { }
        }

        /// <summary><see cref="Flush"/> for a pass that only writes back what it has just backfilled
        /// in memory, where nothing reads the disk before the next periodic flush. A write that still
        /// fails after the retries leaves the sweep in progress for the periodic flush to finish,
        /// instead of throwing out of session load (FAC-371).</summary>
        private void FlushOrDefer(string pass)
        {
            try { Flush(); }
            catch (Exception ex) when (FactionStore.IsRefusedWrite(ex))
            {
                FactionsPlugin.Log.Warn("Flush after " + pass + " deferred to the periodic flush: " + ex.Message);
            }
        }

        /// <summary>Writes the already-accumulated <see cref="FlushPhase.ProductionThroughput"/>
        /// entries (one faction's <see cref="ProductionManager.SnapshotThroughput"/> per
        /// <see cref="StepFlushOnce"/> call) to <c>production.json</c> once the whole roster is done.</summary>
        private void WriteProductionSnapshotFile(List<object> entries)
        {
            try
            {
                string path = Path.Combine(_store.BaseDir, "production.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(new
                {
                    at      = DateTime.UtcNow.ToString("o"),
                    entries = entries,
                }));
            }
            catch { /* best-effort */ }
        }

        /// <summary>Per-faction row cap on the demand snapshot. The register is unbounded by design
        /// -- it is a live working set, not a log -- and this file is a diagnostic, so it stays
        /// bounded (the repo's bound-persisted-collections rule).
        ///
        /// <para><b>The cap is spent round-robin across keys, never as a prefix.</b> A prefix drops
        /// whole keys off the end for exactly the faction big enough to hit the cap -- the first cut
        /// pinned IGSL at 2,000 rows and hid every one of its <c>Ore:Stone</c> rows, which was the
        /// faction the reader was looking at. Round-robin costs one grouping pass and guarantees
        /// every key the faction demands is represented before any key gets a second row.</para></summary>
        private const int MaxDemandRowsPerFaction = 4000;

        /// <summary>At most <see cref="MaxDemandRowsPerFaction"/> of one faction's rows, spent
        /// round-robin across demand kinds so every key the faction wants is represented before any
        /// key takes a second slot. Under the cap this returns everything, in register order.
        ///
        /// <para>Order within a key is the register's own, so the rows kept for a key are that key's
        /// first ones rather than an arbitrary subset -- a prefix per key is fine, a prefix per
        /// faction is not, because only the second can drop a key entirely.</para></summary>
        private static List<DemandEntry> SelectDemandRows(List<DemandEntry> rows)
        {
            if (rows == null) return new List<DemandEntry>();
            if (rows.Count <= MaxDemandRowsPerFaction) return rows;

            var byKind = new Dictionary<string, List<DemandEntry>>(StringComparer.Ordinal);
            var order  = new List<string>();
            foreach (DemandEntry e in rows)
            {
                string k = e.Kind ?? "";
                List<DemandEntry> bucket;
                if (!byKind.TryGetValue(k, out bucket)) { byKind[k] = bucket = new List<DemandEntry>(); order.Add(k); }
                bucket.Add(e);
            }

            var picked = new List<DemandEntry>(MaxDemandRowsPerFaction);
            for (int round = 0; picked.Count < MaxDemandRowsPerFaction; round++)
            {
                bool any = false;
                for (int i = 0; i < order.Count && picked.Count < MaxDemandRowsPerFaction; i++)
                {
                    List<DemandEntry> bucket = byKind[order[i]];
                    if (round >= bucket.Count) continue;
                    picked.Add(bucket[round]);
                    any = true;
                }
                if (!any) break;
            }
            return picked;
        }

        /// <summary>Rows the cap dropped across the whole roster this flush, reported on the
        /// snapshot so a reader can tell a bounded view from a complete one.</summary>
        private int _flushDemandDropped;

        /// <summary>The inventory key inside an <c>"Item:&lt;key&gt;"</c> demand kind, or the kind
        /// itself when it carries no prefix.</summary>
        private static string KeyOf(string demandKind)
        {
            if (string.IsNullOrEmpty(demandKind)) return demandKind;
            return demandKind.StartsWith("Item:", StringComparison.Ordinal) ? demandKind.Substring(5) : demandKind;
        }

        /// <summary>Writes the accumulated <see cref="FlushPhase.DemandRegister"/> rows to
        /// <c>demand.json</c>.
        ///
        /// <para>This is the register's only pull-based view. <c>PROD_INPUT_DEMAND</c> logs a row on
        /// its first post and on a 2x move, so a row posted every cycle at a stable value goes
        /// silent -- and silence is also what an absent row looks like. Counts and means taken from
        /// that log are properties of the emitter as much as of the register (FAC-354).</para></summary>
        private void WriteDemandSnapshotFile(List<object> entries)
        {
            try
            {
                string path = Path.Combine(_store.BaseDir, "demand.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(new
                {
                    at        = DateTime.UtcNow.ToString("o"),
                    truncated = _flushDemandDropped,
                    entries   = entries,
                }));
            }
            catch { /* best-effort */ }
        }

        /// <summary>Writes the already-accumulated <see cref="FlushPhase.ProductionHistory"/> entries
        /// (one faction's <see cref="ProductionManager.SnapshotProductionHistory"/> per
        /// <see cref="StepFlushOnce"/> call) to <c>production_history.json</c> once the whole roster is
        /// done. This is the rolling real-consumption/production window, as opposed to
        /// <see cref="WriteProductionSnapshotFile"/>'s theoretical current-rate snapshot.</summary>
        private void WriteProductionHistorySnapshotFile(List<object> entries)
        {
            try
            {
                string path = Path.Combine(_store.BaseDir, "production_history.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(new
                {
                    at      = DateTime.UtcNow.ToString("o"),
                    entries = entries,
                }));
            }
            catch { /* best-effort */ }
        }

        private void WriteSnapshot()
        {
            long snapshotStart = Stopwatch.GetTimestamp();
            CentralBankSnapshot bank = _world.SnapshotCentralBank(_states);
            foreach (FactionState st in _states)
            {
                double maxThreat = 0;
                foreach (SpatialNode n in _world.Graph.OwnedBy(st.Tag))
                    if (n.Threat > maxThreat) maxThreat = n.Threat;
                int infrastructureCount = DevelopedInfrastructureCount(st.Tag);
                int stationCount = PhysicalStationCount(st.Tag);
                _log.Write("snap t" + _simTick + " " + st.Tag + " tr=" + _world.GetTreasury(st.Tag)
                           + " nf=" + _world.GetNetFlow(st.Tag) + " src=" + _world.Graph.OwnedBy(st.Tag).Count()
                           + " debt=" + _world.GetDebt(st.Tag)
                           + " sdebt=" + st.CentralBankSupportDebt
                           + " infra=" + infrastructureCount
                           + " legacy=" + Math.Max(0, stationCount - infrastructureCount)
                           + " thr=" + maxThreat.ToString("0.00"));
            }
            _log.Write("cbank t" + _simTick + " " + FormatCentralBankSnapshot(bank));
            WriteCentralBankSnapshot(bank);
            _perfFlushSnapshot.Record(Stopwatch.GetTimestamp() - snapshotStart);
        }

        private void WriteCentralBankSnapshot(CentralBankSnapshot bank)
        {
            try
            {
                string path = Path.Combine(_store.BaseDir, "central_bank.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(new
                {
                    at = DateTime.UtcNow.ToString("o"),
                    simTick = _simTick,
                    snapshot = new
                    {
                        currentMoneySupply = bank.CurrentMoneySupply,
                        targetMoneySupply = bank.TargetMoneySupply,
                        moneySupplyGap = bank.MoneySupplyGap,
                        worldCollateralValue = bank.WorldCollateralValue,
                        reserveRatio = bank.ReserveRatio,
                        territoryNodeCount = bank.TerritoryNodeCount,
                        foundedNodeCount = bank.FoundedNodeCount,
                        capabilityLevelCount = bank.CapabilityLevelCount,
                        shipCount = bank.ShipCount,
                        idleShipCount = bank.IdleShipCount,
                        inFlightShipCount = bank.InFlightShipCount,
                    },
                    bankEvents = _world.SnapshotCentralBankEvents().Select(e => new
                    {
                        utcNow = e.UtcNow.ToString("o"),
                        scope = e.Scope,
                        factionTag = e.FactionTag,
                        type = e.Type,
                        amount = e.Amount,
                        treasuryAfter = e.TreasuryAfter,
                        debtAfter = e.DebtAfter,
                        moneySupplyAfter = e.MoneySupplyAfter,
                        moneySupplyGapAfter = e.MoneySupplyGapAfter,
                        message = e.Message,
                    }),
                    debtEvents = _world.SnapshotDebtEvents().Select(e => new
                    {
                        utcNow = e.UtcNow.ToString("o"),
                        scope = e.Scope,
                        factionTag = e.FactionTag,
                        type = e.Type,
                        amount = e.Amount,
                        treasuryAfter = e.TreasuryAfter,
                        debtAfter = e.DebtAfter,
                        moneySupplyAfter = e.MoneySupplyAfter,
                        moneySupplyGapAfter = e.MoneySupplyGapAfter,
                        message = e.Message,
                    }),
                }));
            }
            catch { /* best-effort */ }
        }

        public void SetSpeed(double s)
        {
            Speed = s < 0 ? 0 : s;
            if (Speed > 0) _resumeSpeed = Speed;
            _lastRealUtc = DateTime.UtcNow;
            _log.Write("speed set to " + Speed.ToString("0.###") + " tick/s");
        }

        public void Pause() { SetSpeed(0); }
        public void Resume() { SetSpeed(_resumeSpeed); }

        /// <summary>
        /// Clear all action cooldowns (and optionally active truces) for every loaded faction so
        /// they can act again immediately. Intended for testing and operator resets after bulk
        /// fast-forward runs push cooldown timestamps far into the future.
        /// Flushes to disk immediately so the change survives a server restart.
        /// </summary>
        /// <param name="clearTruces">Also clear TruceUntil windows (non-aggression pacts).</param>
        /// <returns>Number of factions whose cooldowns were cleared.</returns>
        public int ResetCooldowns(bool clearTruces = false)
        {
            if (_world == null) return 0;
            int count = 0;
            foreach (FactionState st in _states)
            {
                if (st.Cooldowns.Count > 0 || st.TradeDestCooldowns.Count > 0)
                {
                    st.Cooldowns.Clear();
                    st.TradeDestCooldowns.Clear();
                    count++;
                }
                if (clearTruces) st.TruceUntil.Clear();
            }
            Flush();
            _log.Write("resetCooldowns: cleared cooldowns for " + count + " faction(s)"
                       + (clearTruces ? " (truces also cleared)" : ""));
            return count;
        }

        /// <summary>
        /// Validate and optionally correct zone classification for every node in the graph.
        /// Re-samples live SE gravity via the planet catalogue and compares with each node's stored
        /// <see cref="SpatialNode.Zone"/>. When <paramref name="applyFix"/> is true, updates Zone and
        /// snaps any Surface nodes that are farther than 1 km from the planet surface sphere. Called
        /// on every <see cref="Load"/> (see there) since Zone/PlanetId are not persisted in
        /// <see cref="SourceDoc"/> and reset to their C# defaults on every restart otherwise. Only
        /// flushes on a real reposition -- Position is a real persisted
        /// field, but Zone/PlanetId are not, so a Flush triggered by ZoneChanged alone would rewrite
        /// every faction's brain JSON on every single boot for a field that never round-trips anyway.
        /// </summary>
        public NodeReclassifyResult ReclassifyNodes(bool applyFix)
        {
            if (_world == null) return new NodeReclassifyResult();
            var blocked = new List<string>();
            NodeReclassifyResult result = _world.ReclassifyNodes(applyFix, blocked);
            bool moved = result.Repositioned > 0 || result.OrbitRepositioned > 0 || result.FeaturePlaced > 0;
            // Worth is persisted, so a re-derivation that only lives in memory is undone by the next
            // load. It joins the flush gate for the same reason a reposition does.
            if (applyFix && (moved || result.WorthChanged > 0))
            {
                Flush();
            }
            if (applyFix && (result.ZoneChanged > 0 || moved || result.SeparationBlocked > 0
                             || result.WorthChanged > 0))
            {
                _log.Write("reclassify: fixed " + result.ZoneChanged + " zone(s), repositioned "
                           + result.Repositioned + " surface node(s), " + result.OrbitRepositioned
                           + " orbit node(s), " + result.FeaturePlaced + " feature-placed node(s), "
                           + result.SeparationBlocked + " blocked by separation, re-derived worth on "
                           + result.WorthChanged + " node(s); "
                           + result.StandingViolations + " node(s) standing in violation");
            }
            // One line per refusal: a count alone cannot be chased back to a node, and this is the
            // only place the world says a retrofit could not be applied.
            for (int i = 0; i < blocked.Count; i++) _log.Write(blocked[i]);
            return result;
        }

        /// <summary>One-time retrofit linking nodes created before <see cref="WorldModel.ExpandFrom"/>
        /// started densifying on creation -- see <see cref="WorldModel.DensifyTopology"/>. Dry-run by
        /// default (bridge verb `factions densifytopology [apply]`, <see cref="FactionsPlugin"/>);
        /// always logs <see cref="TopologyDensifyResult.ElapsedMs"/> so the real cost is directly
        /// observable before an operator ever runs `apply` live, per this codebase's own recent lesson
        /// that a seemingly-cheap whole-graph pass can cost multiple real seconds at this world's scale.
        /// Only flushes on an actual apply that added at least one link.</summary>
        public TopologyDensifyResult DensifyTopology(bool applyFix)
        {
            if (_world == null) return new TopologyDensifyResult();
            TopologyDensifyResult result = _world.DensifyTopology(applyFix);
            if (applyFix && result.LinksAdded > 0) Flush();
            _log.Write("densifytopology: scanned=" + result.NodesScanned + " pairsWithinReach="
                       + result.PairsWithinReach + " linksAdded=" + result.LinksAdded
                       + " elapsedMs=" + result.ElapsedMs.ToString("0.0") + (applyFix ? " (applied)" : " (dry run)"));
            return result;
        }

        /// <summary>Network-wide cleanup for Orbit-zone-stranded builds/ships: repositions Orbit nodes
        /// (see <see cref="ReclassifyNodes"/>), cancels now-infeasible <see cref="SpatialNode
        /// .BuildQueue"/> entries (refunding <see cref="BuildQueueEntry.Collected"/>), and dismantles
        /// now-infeasible non-store <see cref="SpatialNode.ShipStock"/> (crediting its BOM). See
        /// <see cref="WorldModel.ClearOrbitStranding"/>. Flush trigger is broader than
        /// <see cref="ReclassifyNodes"/>'s own -- BuildQueue/Inventory/ShipStock are all real persisted
        /// fields, so any of the three counts being nonzero means live state changed.</summary>
        public OrbitStrandingClearResult ClearOrbitStranding(bool applyFix)
        {
            if (_world == null) return new OrbitStrandingClearResult();
            var log = new List<string>();
            OrbitStrandingClearResult result = _world.ClearOrbitStranding(applyFix, log);
            if (applyFix)
            {
                foreach (string line in log) _log.Write(line);
            }
            if (applyFix && (result.NodesRepositioned > 0 || result.BuildsCanceled > 0 || result.ShipsDismantled > 0))
            {
                Flush();
                _log.Write("clearstranding: repositioned " + result.NodesRepositioned
                           + " node(s), canceled " + result.BuildsCanceled + " build(s) (refunded "
                           + result.BuildItemsRefunded + " item(s)), dismantled " + result.ShipsDismantled
                           + " ship(s) (credited " + result.BomUnitsCredited + " item(s))");
            }
            return result;
        }

        /// <summary>
        /// Re-founds every already-founded station whose live MyStationTypeEnum/prefab no longer
        /// matches its node's Zone -- run <see cref="ReclassifyNodes"/> first so Zone reflects live
        /// gravity before checking prefab mismatches against it. See
        /// <see cref="StationFounder.MigrateMismatchedStationPrefabs"/> for the mechanics.
        /// </summary>
        public StationPrefabMigrationResult MigrateStationPrefabs(bool applyFix,
            System.Collections.Generic.ICollection<string> onlyNodes = null)
        {
            if (_world == null) return new StationPrefabMigrationResult { Details = new System.Collections.Generic.List<string>() };
            StationPrefabMigrationResult result = _stationFounder.MigrateMismatchedStationPrefabs(
                _world.Graph, _states, applyFix, _world.UtcNow, _stationMaterializer, onlyNodes);
            if (applyFix && result.Refounded > 0)
            {
                Flush();
            }
            if (result.Mismatched > 0)
            {
                _log.Write("migrateStationPrefabs: checked=" + result.Checked + " mismatched=" + result.Mismatched
                           + " outsidePool=" + result.OutsidePool
                           + (applyFix ? " refounded=" + result.Refounded : " (dry run)"));
            }
            return result;
        }

        /// <summary>
        /// Clears MyFactionStation.IsDeepSpaceStation on every already-founded station that still
        /// carries it from before FoundStation stopped setting it. Design choice, not a bug fix --
        /// see <see cref="StationFounder.ClearDeepSpaceFlags"/>. Pure SE-side field: no Flush()
        /// needed, it round-trips through SE's own world save, not our brain JSON.
        /// </summary>
        public int ClearDeepSpaceFlags(bool applyFix)
        {
            if (_world == null) return 0;
            int affected = _stationFounder.ClearDeepSpaceFlags(_world.Graph, applyFix);
            if (applyFix && affected > 0)
                _log.Write("clearDeepSpaceFlags: cleared IsDeepSpaceStation on " + affected + " station(s)");
            return affected;
        }

        /// <summary>
        /// One-time operator backfill for stations left registered under their previous owner's
        /// MyFaction.m_stations dict by an ownership transfer that predates the 2026-08-17 fix to
        /// <see cref="StationFounder.RenameConqueredStation"/> -- see
        /// <see cref="StationFounder.FixStationOwnershipMismatches"/>. Pure SE-side registration
        /// fix: no Flush() needed, node.OwnerFactionTag (the persisted brain state) never changes.
        /// </summary>
        public StationOwnershipFixResult FixStationOwnershipMismatches(bool applyFix)
        {
            if (_world == null) return new StationOwnershipFixResult { Details = new System.Collections.Generic.List<string>() };
            StationOwnershipFixResult result = _stationFounder.FixStationOwnershipMismatches(_world.Graph, applyFix);
            if (result.Mismatched > 0)
            {
                _log.Write("fixStationOwnership: checked=" + result.Checked + " mismatched=" + result.Mismatched
                           + (applyFix ? " fixed=" + result.Fixed : " (dry run)"));
            }
            return result;
        }

        /// <summary>
        /// Return per-node position (km) and raw gravity values for diagnostic logging.
        /// The caller is responsible for writing each line to the Torch logger.
        /// </summary>
        public System.Collections.Generic.List<string> GetNodeGravityDump()
        {
            var lines = new System.Collections.Generic.List<string>();
            if (_world == null) { lines.Add("DumpNodeGravity: world not loaded"); return lines; }
            lines.Add("DumpNodeGravity: " + _world.Graph.Nodes.Count + " node(s)"
                      + "  SurfThresh=" + _world.Tuning.SurfaceGravityThreshold
                      + "  OrbitMin=" + _world.Tuning.OrbitGravityMin);
            foreach (System.Collections.Generic.KeyValuePair<string, SpatialNode> kv in _world.Graph.Nodes)
            {
                SpatialNode n = kv.Value;
                double g = _world.Planets.GravityAt(n.Position);
                NodeZone gravityZone = _world.ClassifyZone(n.Position);
                NodeZone zone = _world.PlaceableZoneAt(n.Position);
                lines.Add("node=" + n.Id
                          + "  pos=(" + n.Position.X.ToString("0.#") + ","
                                      + n.Position.Y.ToString("0.#") + ","
                                      + n.Position.Z.ToString("0.#") + ") km"
                          + "  g=" + g.ToString("0.0000")
                          + "  zone=" + zone
                          + "  gravityZone=" + gravityZone
                          + "  stored=" + n.Zone
                          + "  owner=" + (n.OwnerFactionTag ?? "-"));
            }
            return lines;
        }

        /// <summary>Read-only diagnostic: samples <see cref="IPlanetCatalogue.GravityAt"/> along a
        /// fixed ray out from each matching planet's center, at trial radii from the center through
        /// the surface and out past the gravity radius. Reports each sample relative to that planet's
        /// own surface gravity and the <see cref="NodeZone"/> the sample would classify as, so the
        /// host's real falloff curve can be read directly instead of assumed -- including INSIDE the
        /// planet, which no live node position can be sampled at (they get repositioned out).</summary>
        public System.Collections.Generic.List<string> GetGravityProfile(string planetFilter)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (_world == null) { lines.Add("gravityprobe: world not loaded"); return lines; }

            lines.Add("gravityprobe: SurfThresh=" + _world.Tuning.SurfaceGravityThreshold
                      + " OrbitMin=" + _world.Tuning.OrbitGravityMin);

            foreach (PlanetRecord p in _world.Planets.Planets)
            {
                if (!string.IsNullOrEmpty(planetFilter)
                    && (p.Generator ?? "").IndexOf(planetFilter, StringComparison.OrdinalIgnoreCase) < 0
                    && (p.Id ?? "").IndexOf(planetFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                lines.Add("planet=" + p.Generator + " id=" + p.Id
                          + " surfaceR=" + p.SurfaceRadius.ToString("0.##")
                          + " gravityR=" + p.GravityRadius.ToString("0.##"));

                double surfaceG = SampleGravityAtRadius(p, p.SurfaceRadius);
                lines.Add("  surfaceG=" + surfaceG.ToString("0.0000") + " (the LocalSurfaceG baseline)");

                var radii = new System.Collections.Generic.List<double>();
                for (int i = 0; i <= 10; i++) radii.Add(p.SurfaceRadius * (i / 10.0));               // inside
                for (int i = 1; i <= 10; i++)                                                        // band
                    radii.Add(p.SurfaceRadius + (p.GravityRadius - p.SurfaceRadius) * (i / 10.0));
                radii.Add(p.GravityRadius * 1.05);                                                   // outside

                foreach (double r in radii)
                {
                    double g = SampleGravityAtRadius(p, r);
                    double rel = surfaceG > 0 ? g / surfaceG : 0;
                    lines.Add("  r=" + r.ToString("0.00") + "km"
                              + " (" + (p.SurfaceRadius > 0 ? (r / p.SurfaceRadius).ToString("0.00") : "-") + "xR)"
                              + " g=" + g.ToString("0.0000")
                              + " rel=" + rel.ToString("0.0000")
                              + " -> " + ZoneForRelative(g, rel));
                }
            }
            if (lines.Count == 1) lines.Add("gravityprobe: no planet matching '" + planetFilter + "'.");
            return lines;
        }

        private double SampleGravityAtRadius(PlanetRecord p, double radiusKm)
        {
            return _world.Planets.GravityAt(new Vec3(p.Center.X + radiusKm, p.Center.Y, p.Center.Z));
        }

        /// <summary>The zone <see cref="WorldModel.ClassifyZone"/> would report for a sample, stated
        /// in terms already computed by the caller so the probe does not re-derive the reference
        /// planet (which is the very thing under test).</summary>
        private string ZoneForRelative(double g, double relativeG)
        {
            if (g <= 0) return "DeepSpace";
            return relativeG >= _world.Tuning.SurfaceGravityThreshold ? "Surface" : "Orbit";
        }

        public bool SeedTreasuryFor(string tag, long amount)
        {
            if (_world == null) return false;
            FactionState st = _states.FirstOrDefault(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (st == null) return false;
            _world.SetTreasury(st.Tag, amount);
            _log.Write("seed " + st.Tag + " treasury=" + amount);
            return true;
        }

        /// <summary>Operator correction tool (`factions sim addship &lt;tag&gt; &lt;nodeId&gt;
        /// &lt;prefabId&gt; [count]`), not a gameplay action -- directly credits
        /// <paramref name="count"/> unit(s) of <paramref name="prefabId"/> to <paramref name="nodeId"/>'s
        /// <see cref="SpatialNode.ShipStock"/>. Mutates the live world model only (same pattern as
        /// <see cref="SeedTreasuryFor"/>/<see cref="ClearDebtFor"/>) -- persists through the normal
        /// flush/save cycle, no direct file edit needed. Validates the node exists and is owned by
        /// <paramref name="tag"/>; returns false on any validation failure.</summary>
        public bool AddShipStock(string tag, string nodeId, string prefabId, int count)
        {
            if (_world == null || string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(prefabId)) return false;
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return false;
            if (!string.Equals(node.OwnerFactionTag, tag, StringComparison.OrdinalIgnoreCase)) return false;

            int add = count > 0 ? count : 1;
            int cur;
            node.ShipStock.TryGetValue(prefabId, out cur);
            node.ShipStock[prefabId] = cur + add;
            _log.Write("addship " + tag + " " + nodeId + " " + prefabId + " +" + add + " -> " + (cur + add));
            return true;
        }

        /// <summary>Clears one faction's central-bank debt (both the master ledger and the
        /// support-loan-only counter). Returns false if the faction isn't loaded.</summary>
        public bool ClearDebtFor(string tag)
        {
            if (_world == null) return false;
            FactionState st = _states.FirstOrDefault(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (st == null) return false;
            long before = _world.GetDebt(st.Tag);
            _world.ClearDebt(st.Tag, st);
            _log.Write("cleardebt " + st.Tag + " debt " + before + " -> 0");
            return true;
        }

        /// <summary>Clears central-bank debt for every currently loaded faction. Returns the number
        /// of factions that had non-zero debt cleared.</summary>
        public int ClearAllDebt()
        {
            if (_world == null) return 0;
            int cleared = 0;
            foreach (FactionState st in _states)
            {
                long before = _world.GetDebt(st.Tag);
                if (before <= 0 && st.CentralBankSupportDebt <= 0) continue;
                _world.ClearDebt(st.Tag, st);
                _log.Write("cleardebt " + st.Tag + " debt " + before + " -> 0");
                cleared++;
            }
            return cleared;
        }

        /// <summary>Restore transient flight settings before session/plugin shutdown.</summary>
        public void ReleaseFleetControl()
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla != null) vanilla.ReleaseAllControl();
        }

        /// <summary>Advances any grid under native flight control (terminal approach autopilot).
        /// Runs every frame on its own <c>host.fleetFlight</c> bucket rather than inside
        /// <see cref="UpdateObservation"/>: it is real-time flight control, so unlike the observation
        /// scan it cannot be moved onto a coarser interval, and folding the two together left their
        /// costs indistinguishable.</summary>
        public void TickFleetFlight()
        {
            long a = AllocProbe.Begin();
            SpawnPacing.TickFrame();
            AllocProbe.End("ff.pacing", a); a = AllocProbe.Begin();
            GridAssembler.Instance.Tick();
            AllocProbe.End("ff.assembler", a); a = AllocProbe.Begin();
            EconomyTickSlicer.Tick();
            AllocProbe.End("ff.econSlicer", a); a = AllocProbe.Begin();
            GridAssembly.Tick();
            AllocProbe.End("ff.disassembly", a); a = AllocProbe.Begin();
            RockContactPhysics.Tick();
            AllocProbe.End("ff.rockContact", a); a = AllocProbe.Begin();
            ClipmapWaitSweep.Tick();
            StationQuiet.Tick(_stationMaterializer, _tuning);
            AllocProbe.End("ff.stationQuiet", a);
            if (_world == null || _spawner == null || _spawner.SkipsTransit) return;
            long t0 = Stopwatch.GetTimestamp();
            a = AllocProbe.Begin();
            _spawner.TickFlight();
            AllocProbe.End("ff.flight", a);
            _perfFlight.Record(Stopwatch.GetTimestamp() - t0);
        }

        /// <summary>Every grid entity id this plugin currently has materialised for a fleet — the
        /// lead hull and its escorts. For the grid census, which has to tell our ships apart from
        /// the player's and from vanilla's, and cannot do it by ownership: a fleet grid and a station
        /// grid are owned by the same NPC faction.
        ///
        /// <para>Ids below 1 are lifecycle sentinels, not entities — <c>SpawningId</c> and the
        /// destroyed/absent encodings — and none of them names a grid in the world.</para></summary>
        public void CollectFleetGridIds(HashSet<long> into)
        {
            if (into == null) return;
            foreach (FactionState st in _states)
            {
                if (st == null || st.PendingFleets == null) continue;
                for (int i = 0; i < st.PendingFleets.Count; i++)
                {
                    PendingFleetAction fleet = st.PendingFleets[i];
                    if (fleet.MaterialisedEntityId > 0) into.Add(fleet.MaterialisedEntityId);
                    if (fleet.EscortEntityIds == null) continue;
                    for (int e = 0; e < fleet.EscortEntityIds.Count; e++)
                        if (fleet.EscortEntityIds[e] > 0) into.Add(fleet.EscortEntityIds[e]);
                }
            }
        }

        /// <summary>Whether a grid is under the terminal-approach autopilot right now — one of our
        /// ships actually flying, as opposed to one parked at a node. The distinction the physics
        /// hypothesis turns on, and one nothing else can answer from outside.</summary>
        public bool IsGridUnderFlightControl(long entityId)
        {
            var vanillaSpawner = _spawner as VanillaFleetSpawner;
            return vanillaSpawner != null && vanillaSpawner.IsFlying(entityId);
        }


        /// <summary>
        /// Observation-range materialisation loop — called from the Torch Update() tick.
        /// For each in-flight (not yet arrived) <see cref="PendingFleetAction"/>:
        /// <list type="bullet">
        /// <item>If not materialised and a player is within <see cref="FactionTuning.ObservationRadiusKm"/>
        ///   of the fleet's current interpolated position → spawn a grid via the spawner.</item>
        /// <item>If materialised but the grid no longer exists → combat damage on record means it was
        ///   destroyed (force-arrive next tick at ratio 0); no damage on record means it merely
        ///   vanished (admin delete, cleanup, reload) and the fleet de-materialises and flies on.</item>
        /// <item>If materialised but all players have left range → despawn the grid; the abstract event
        ///   continues in-flight and will re-materialise on the next player approach.</item>
        /// </list>
        /// No-op when the spawner skips transit (NullFleetSpawner) — only runs with a real MES spawner.
        /// <paramref name="isPlayerNear"/> is a delegate because the player-position query is SE-side
        /// and must not be called from within Core. <paramref name="playersWithin"/> is the
        /// identity-carrying counterpart, used only to target immersive fleet-progress chat
        /// messages (see <see cref="SendNearbyFleetMessage"/>) — never for materialisation logic.
        /// </summary>
        /// <summary>Rebuilds the player-target list once per knowledge sweep. Cheap by cadence rather
        /// than by cleverness: the supplier walks live grids, and this runs on the global pass, not
        /// per faction and not per frame.</summary>
        private void RefreshPlayerContacts()
        {
            _playerContacts.Clear();
            if (PlayerContactSupplier == null) return;
            try
            {
                List<PlayerContact> supplied = PlayerContactSupplier();
                if (supplied != null) _playerContacts.AddRange(supplied);
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the global pass down with it. An empty list reads as
                // "sees nobody", which is the safe direction for a detection set.
                FactionsPlugin.Log.Warn("Player contact supplier failed: " + ex.Message);
            }
        }

        /// <summary>One faction's detected-player set, rebuilt from geometry. Nothing reads it to make
        /// a decision yet — that lands with fleet retargeting — so this deliberately only records.
        ///
        /// <para>The transition diff is computed before the set is replaced, because "who is new" and
        /// "who was lost" are the only things worth a log line; the steady state is every faction
        /// seeing the same player every sweep, forever.</para></summary>
        private void SweepSensedPlayers(FactionState st, DateTime nowUtc)
        {
            if (st == null) return;
            if (_playerContacts.Count == 0)
            {
                if (DebugSensing && st.SensedPlayers.Count > 0)
                    foreach (PlayerContact lost in st.SensedPlayers)
                        _log.Write("SENSE_LOST " + st.Tag + " identity=" + lost.IdentityId
                                   + " eid=" + lost.GridEntityId + " reason=no_contacts");
                st.SensedPlayers.Clear();
                return;
            }

            _sensorScratch.Clear();
            PlayerSensing.CollectFactionSensors(_world.Graph, st, _tuning, nowUtc, _sensorScratch);
            PlayerSensing.Detect(_sensorScratch, _playerContacts, _sensedScratch, SensorOcclusion);

            if (DebugSensing)
            {
                for (int i = 0; i < _sensedScratch.Count; i++)
                {
                    PlayerContact now = _sensedScratch[i];
                    if (PlayerSensing.SeesGrid(st.SensedPlayers, now.GridEntityId)) continue;
                    _log.Write("SENSE_ACQUIRED " + st.Tag + " identity=" + now.IdentityId
                               + " eid=" + now.GridEntityId
                               + " sig=" + now.Signature.ToString("0.###", CultureInfo.InvariantCulture)
                               + " bcastKm=" + now.BroadcastRadiusKm.ToString("0.#", CultureInfo.InvariantCulture)
                               + " sensors=" + _sensorScratch.Count
                               + " nearestKm=" + NearestSensorKm(now.PositionKm).ToString("0.#", CultureInfo.InvariantCulture));
                }
                for (int i = 0; i < st.SensedPlayers.Count; i++)
                {
                    PlayerContact was = st.SensedPlayers[i];
                    if (PlayerSensing.SeesGrid(_sensedScratch, was.GridEntityId)) continue;
                    _log.Write("SENSE_LOST " + st.Tag + " identity=" + was.IdentityId
                               + " eid=" + was.GridEntityId
                               + " nearestKm=" + NearestSensorKm(was.PositionKm).ToString("0.#", CultureInfo.InvariantCulture));
                }
            }

            st.SensedPlayers.Clear();
            st.SensedPlayers.AddRange(_sensedScratch);
        }

        /// <summary>Distance to the closest of this faction's sensors, for the trace only. Reported
        /// rather than the detection range itself because it is the number an operator can check by
        /// flying, which is what makes the acceptance test falsifiable.</summary>
        private double NearestSensorKm(Vec3 posKm)
        {
            double best = double.MaxValue;
            for (int i = 0; i < _sensorScratch.Count; i++)
            {
                double d2 = Sensing.DistanceSquared(_sensorScratch[i].PositionKm, posKm);
                if (d2 < best) best = d2;
            }
            return best == double.MaxValue ? -1 : Math.Sqrt(best);
        }

        /// <summary>Set by the host from the load governor: what a fleet of this kind does when a
        /// player comes near. Null means materialise as usual. See <see cref="FactionsLoadActuator"/>.</summary>
        public Func<ActionKind, MaterialiseDecision> MaterialiseGate { get; set; }

        /// <summary>Set by the host from traffic control's budget: told of every spawn as it starts
        /// (the fleet and its key), it answers with a line for the decision log, or null. Slice 1 of
        /// docs/traffic-control.md only observes; the spawn goes ahead whatever it returns.</summary>
        public Func<PendingFleetAction, string, DateTime, string> SpawnAdmissionProbe { get; set; }

        /// <summary>Every fleet with a live grid, for the traffic budget's per-window comparison.</summary>
        public void CollectMaterialisedFleets(List<PendingFleetAction> into)
        {
            if (into == null) return;
            foreach (FactionState st in _states)
            {
                if (st == null || st.PendingFleets == null) continue;
                for (int i = 0; i < st.PendingFleets.Count; i++)
                    if (st.PendingFleets[i].MaterialisedEntityId > 0) into.Add(st.PendingFleets[i]);
            }
        }

        private void ProbeSpawnAdmission(PendingFleetAction fleet, string fleetKey, DateTime now)
        {
            if (SpawnAdmissionProbe == null || fleet.IsBench) return;
            string line = SpawnAdmissionProbe(fleet, fleetKey, now);
            if (line != null) _log.Write(line);
        }

        /// <summary>The bound on a combat wait (operator, 2026-09-24: 30 minutes). Set by the host.</summary>
        public TimeSpan CombatMaxWait { get; set; } = TimeSpan.FromMinutes(30);

        private readonly CombatHoldTracker _combatHolds = new CombatHoldTracker();
        private readonly HashSet<string> _waitingThisWindow = new HashSet<string>();
        private int _waitExpiredCount;

        /// <summary>Distinct combat fleets held, and waits that ran out, since the last read. The
        /// telemetry window drains both.</summary>
        public void TakeCombatWaitCounts(out int waiting, out int expired)
        {
            waiting = _waitingThisWindow.Count;
            expired = _waitExpiredCount;
            _waitingThisWindow.Clear();
            _waitExpiredCount = 0;
        }

        /// <summary>Holds a combat fleet this frame: freezes its schedule by the time since the last
        /// frame it was held. Returns false once its wait has reached <see cref="CombatMaxWait"/>,
        /// and the caller materialises it.</summary>
        private bool HoldCombat(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            TimeSpan freezeBy;
            bool started;
            if (!_combatHolds.TryHold(fleet.FleetId, now, CombatMaxWait, out freezeBy, out started))
            {
                // Once per hold: the caller materialises this frame and releases the hold.
                _waitExpiredCount++;
                if (DebugTraces.Governor)
                    _log.Write("FLEET_WAIT_EXPIRED " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                               + " held=" + _combatHolds.HeldFor(fleet.FleetId).TotalMinutes.ToString("F1", CultureInfo.InvariantCulture) + "min");
                return false;
            }
            if (freezeBy > TimeSpan.Zero) st.PendingFleets[fi] = FactionsLoadActuator.Freeze(fleet, freezeBy);
            _waitingThisWindow.Add(fleet.FleetId ?? "");
            if (started && DebugTraces.Governor)
                _log.Write("FLEET_WAITING " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                           + " eta=" + fleet.EstimatedArrivalUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>Fleets currently held off-screen by <see cref="MaterialiseGate"/>, so each is
        /// counted and logged once per hold rather than once per frame.</summary>
        private readonly HashSet<string> _heldOffScreen = new HashSet<string>();

        /// <summary>Distinct fleets held since the last read. The telemetry window drains it and
        /// clears the set with it, so a fleet held across windows is counted once per window and the
        /// set never outgrows one window's worth of fleets.</summary>
        private int _heldOffScreenCount;

        public int TakeHeldOffScreenCount()
        {
            int n = _heldOffScreenCount;
            _heldOffScreenCount = 0;
            _heldOffScreen.Clear();
            return n;
        }

        private void NoteHeldOffScreen(PendingFleetAction fleet)
        {
            string id = fleet.FleetId ?? "";
            if (!_heldOffScreen.Add(id)) return;
            _heldOffScreenCount++;
            if (DebugTraces.Governor)
                _log.Write("FLEET_HELD_OFFSCREEN " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind);
        }

        /// <summary>A fleet-observation pass longer than this writes <c>OBS_SLOW</c> under <c>factions debug spawncost</c>,
        /// naming the fleet whose iteration took longest.</summary>
        private const double SlowObservationMs = 50.0;
        private long _obsPhQueue, _obsPhPresence, _obsPhGate, _obsPhBegin;
        private static readonly Dictionary<int, string> ObsAllocKeys = new Dictionary<int, string>();

        /// <summary><c>obs.&lt;kind&gt;.&lt;abstract|live|spawning&gt;</c> for <c>ALLOC_SPLIT</c>, built once per key.</summary>
        private static string ObsAllocKey(int key)
        {
            string s;
            if (ObsAllocKeys.TryGetValue(key, out s)) return s;
            int state = key % 4;
            s = "obs." + (ActionKind)(key / 4) + "." + (state == 1 ? "live" : state == 2 ? "spawning" : "abstract");
            ObsAllocKeys[key] = s;
            return s;
        }

        private string ObsPhases()
        {
            double f = 1000.0 / Stopwatch.Frequency;
            return " queueMs=" + (_obsPhQueue * f).ToString("F1") + " presenceMs=" + (_obsPhPresence * f).ToString("F1")
                 + " gateMs=" + (_obsPhGate * f).ToString("F1") + " beginMs=" + (_obsPhBegin * f).ToString("F1");
        }

        public void UpdateObservation(
            Func<Vec3, double, bool> isPlayerWithin, Func<Vec3, double, IEnumerable<ulong>> playersWithin,
            int onlinePlayerCount)
        {
            if (_world == null || _spawner == null || _spawner.SkipsTransit) return;
            long _obsT0 = Stopwatch.GetTimestamp();

            _playersWithin = playersWithin;
            // Use real-time UTC directly: _world.UtcNow is only updated in StepOnce (every ~10 s),
            // which would make the sim position jump 50 km each tick and false-trigger despawns.
            DateTime now = DateTime.UtcNow;

            // Commanders' orders first, so this frame's flight asks below see which hulls obey one.
            TickWing(now, isPlayerWithin);

            // Drop expired re-materialisation holds for fleets that resolved while backed off and
            // will therefore never reach the gate that clears its own entry, and expired settle
            // graces for grids that were never asked to leave.
            _rematerialiseAfter.Prune(now);
            _combatHolds.Prune(now, TimeSpan.FromMinutes(10));
            _spawnSettle.Prune(now);
            _landedUnseen.Prune(now);
            _unseenLandings.Prune(now);
            _legOrigins.Prune(now, LegOriginPruneAge);
            if (_spawnSettle.Count == 0 && _settleHeldReported.Count > 0) _settleHeldReported.Clear();
            SweepDepartures(now, isPlayerWithin);
            SweepDisabledHulls(now);
            PruneSteering(now);
            TickFightWaves(now, isPlayerWithin);
            RefreshPresencePlan(now, isPlayerWithin, onlinePlayerCount > 0);

            // Nothing can be materialised for nobody, so with an empty roster the only fleets with
            // work left are those already holding a grid, those with a squadron parked by a retarget
            // stash, and those still inside a re-materialisation backoff. Deciding that costs one
            // field read per fleet, against the arrival maths, dictionary probes and position
            // interpolation the loop body would otherwise perform for every one of them, every frame.
            bool spawnPossible = onlinePlayerCount > 0
                                 || _pendingRetargetEid.Count > 0
                                 || _rematerialiseAfter.Count > 0;

            bool slowTrace = DebugTraces.SpawnCost;
            bool allocTrace = AllocProbe.On;
            long allocIter = 0;
            int allocKey = -1;
            long cpu0 = slowTrace ? GridAssembler.ThreadCpuNow() : -1;
            int gc0 = slowTrace ? GC.CollectionCount(0) : 0, gc1 = slowTrace ? GC.CollectionCount(1) : 0, gc2 = slowTrace ? GC.CollectionCount(2) : 0;
            long iterStart = 0, slowestTicks = 0;
            string iterFleet = null, slowestFleet = null;
            int iterations = 0;
            foreach (FactionState st in _states)
            {
                if (!spawnPossible && !HoldsAnyGrid(st)) continue;

                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction fleet = st.PendingFleets[fi];
                    if (allocTrace)
                    {
                        long b = AllocProbe.Now();
                        if (allocKey >= 0) AllocProbe.AddKeyed(ObsAllocKey(allocKey), b - allocIter);
                        allocIter = b;
                        allocKey = (int)fleet.Kind * 4
                                   + (fleet.MaterialisedEntityId > 0 ? 1 : fleet.MaterialisedEntityId == PendingFleetAction.SpawningId ? 2 : 0);
                    }
                    if (slowTrace)
                    {
                        long ts = Stopwatch.GetTimestamp();
                        if (iterFleet != null && ts - iterStart > slowestTicks) { slowestTicks = ts - iterStart; slowestFleet = iterFleet + ObsPhases(); }
                        _obsPhQueue = _obsPhPresence = _obsPhGate = _obsPhBegin = 0;
                        iterStart = ts;
                        iterFleet = fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                                    + " eid=" + fleet.MaterialisedEntityId;
                        iterations++;
                    }
                    if (fleet.Intercepted) continue;

                    if (fleet.HasArrived(now))
                    {
                        if (fleet.MaterialisedEntityId > 0
                            || fleet.MaterialisedEntityId == PendingFleetAction.SpawningId)
                        {
                            // Live grid or in-flight spawn — fall through to the branch below.
                            // ResolveArrivals defers economic resolution in both cases, so the
                            // physical outcome settles before the abstract effect applies.
                        }
                        else if (HasStashAwaitingReclaim(fleet))
                        {
                            // Hulls parked at the end of a leg Core has already advanced past. The
                            // new leg can arrive the instant it departs — two stops at one node —
                            // and gating their reclaim on !HasArrived then never lets it run at all,
                            // which leaves the grids in the world with no fleet record on them.
                        }
                        else if (fleet.IsActiveGuard(now, _tuning))
                        {
                            // A Guard past arrival is standing its duty window at the node, not
                            // waiting to resolve: that window is the whole point of the kind, and
                            // its transit is seconds long. It stays eligible to materialise for the
                            // window's length, and the hold branch below keeps the grid on station.
                        }
                        else
                        {
                            // No grid and no spawn pending — abstract resolution in ResolveArrivals.
                            continue;
                        }
                    }

                    if (fleet.MaterialisedEntityId == 0)
                    {
                        // Retarget-in-place continuation: this fleet just finished a leg inside its
                        // previous target's safezone (Outcome 4 below) and was waiting for Core to
                        // compute the next leg's TargetPosKm via AdvanceToNextStop. HasArrived only
                        // goes false (reaching this branch at all) once that's happened, so
                        // fleet.TargetPosKm is already the new leg's target — reclaim the existing
                        // grid instead of spawning a fresh one. Bypasses the hard-cap/proximity gates
                        // below entirely: those gate *new* spawns, and this isn't one.
                        StashedSquadron stash;
                        if (!string.IsNullOrEmpty(fleet.FleetId)
                            && _pendingRetargetEid.TryGetValue(fleet.FleetId, out stash)
                            && stash.Eids != null && stash.Eids.Length > 0)
                        {
                            // Core has not computed the next leg yet, so TargetPosKm is still the
                            // stop these hulls are parked on. Waiting is the only safe move: falling
                            // through would spawn a second set of grids beside the parked ones.
                            if (stash.LegDepartedUtc == fleet.DepartedUtc) continue;

                            _pendingRetargetEid.Remove(fleet.FleetId);
                            long[] savedEids = stash.Eids;
                            long savedEid = savedEids[0];
                            if (_spawner.GridExists(savedEid) && _spawner.Retarget(savedEid, fleet.TargetPosKm))
                            {
                                // Escorts fly the lead's waypoint, so the same rewire carries the
                                // whole squadron into the next leg. One whose grid did not survive
                                // the stash is dropped here without costing a hull -- the escort
                                // sweep in the live branch is what writes a hull off.
                                List<long> keptEscorts = null;
                                int droppedEscorts = 0;
                                int droppedGrids = 0;
                                for (int si = 1; si < savedEids.Length; si++)
                                {
                                    long escortEid = savedEids[si];
                                    if (escortEid <= 0) continue;
                                    if (_spawner.GridExists(escortEid) && _spawner.Retarget(escortEid, fleet.TargetPosKm))
                                        (keptEscorts ?? (keptEscorts = new List<long>())).Add(escortEid);
                                    else
                                    {
                                        droppedEscorts++;
                                        droppedGrids += _spawner.Despawn(escortEid);
                                    }
                                }
                                var retargeted = fleet;
                                retargeted.MaterialisedEntityId = savedEid;
                                retargeted.EscortEntityIds = keptEscorts;
                                st.PendingFleets[fi] = retargeted;
                                _log.Write("FLEET_RETARGETED " + fleet.LogId + " " + fleet.ActorTag
                                           + " eid=" + savedEid + " stop=" + fleet.CurrentStopKind
                                           + " target=" + (fleet.TargetNodeId ?? "?")
                                           + EscortTally(keptEscorts == null ? 0 : keptEscorts.Count, savedEids.Length - 1)
                                           + (droppedEscorts > 0 ? " closed=" + droppedGrids : ""));
                                continue;
                            }
                            // No lead to follow and no other reference to the escorts: close them
                            // here or they stay in the world with nothing tracking them.
                            int strandedEscorts = DespawnEids(savedEids, 1, out int strandedGrids);
                            _log.Write("FLEET_RETARGET_FAILED " + fleet.LogId + " eid=" + savedEid
                                       + EscortsToken(strandedEscorts)
                                       + " closed=" + strandedGrids
                                       + " — grid gone or autopilot rewire failed; falling back to a fresh spawn");
                            // falls through to the hard-cap/proximity-gated spawn path below
                        }

                        // A bench attacker leaving its fight flies out on the grids it has and is
                        // not put back once they are gone (BenchScenario.InAftermath).
                        if (BenchScenario.InAftermath(fleet)) continue;

                        // Hard cap: at most 1 live-or-spawning fleet per faction per combat/patrol
                        // kind. The cap inspects MaterialisedEntityId directly — SpawningId (-2)
                        // counts as active, so the previous fleet's in-flight spawn blocks all peers
                        // even before the async callback fires and before ResolveArrivals sees the
                        // eid. It counts fleet entries, not grids: a squadron is one fleet however
                        // many hulls it puts in the sky, so its escorts are deliberately not read
                        // here — reading them would make a squadron block itself.
                        // A bench scenario is staged to be seen: it is neither held back by this cap
                        // nor counted against a real fleet's.
                        if (IsCappedSpawnKind(fleet.Kind) && !fleet.IsBench)
                        {
                            bool gridAlreadyActive = false;
                            for (int oi = 0; oi < st.PendingFleets.Count; oi++)
                            {
                                if (oi == fi) continue;
                                PendingFleetAction other = st.PendingFleets[oi];
                                if (other.Kind != fleet.Kind || other.IsBench) continue;
                                if (other.MaterialisedEntityId > 0
                                    || other.MaterialisedEntityId == PendingFleetAction.SpawningId)
                                { gridAlreadyActive = true; break; }
                            }
                            if (gridAlreadyActive) continue;
                        }

                        // A fleet whose grid just vanished without combat damage, or was closed by
                        // the leave-range exit, holds off briefly before taking another one; see
                        // RematerialiseBackoff and LeaveRangeRespawnBackoff.
                        if (!string.IsNullOrEmpty(fleet.FleetId) && _rematerialiseAfter.IsHeld(fleet.FleetId, now))
                            continue;

                        // Not yet materialised — check player proximity to sim position
                        Vec3 currentPos = fleet.CurrentPosKm(now, _tuning);
                        if (!isPlayerWithin(currentPos, _tuning.ObservationRadiusKm))
                        {
                            _unseenLandings.Release(fleet.FleetId);
                            continue;
                        }

                        // Its last grid landed where no player could keep it: spawn again only where
                        // the next one would be kept (UnseenLandings).
                        if (_unseenLandings.Count > 0
                            && _unseenLandings.Holds(fleet.FleetId, currentPos, now, isPlayerWithin,
                                                     _tuning.ObservationRadiusKm + DespawnHysteresisKm)) continue;

                        // The load governor's Factions step (FactionsLoadActuator): under load,
                        // traffic a player came near resolves off-screen, and combat waits with its
                        // schedule frozen until the step drops or the wait reaches its bound.
                        // Traffic control's presence rule: civilian traffic materialises only inside
                        // the nearest player's share of the budget (FactionRuntime.Presence.cs).
                        long ph0 = slowTrace ? Stopwatch.GetTimestamp() : 0;
                        bool queueHeld = QueueHolds(st, fi, fleet, now);
                        long ph1 = slowTrace ? Stopwatch.GetTimestamp() : 0;
                        if (slowTrace) _obsPhQueue += ph1 - ph0;
                        if (queueHeld) continue;
                        fleet = st.PendingFleets[fi];
                        bool presenceRefused = PresenceRefuses(fleet);
                        long ph2 = slowTrace ? Stopwatch.GetTimestamp() : 0;
                        if (slowTrace) _obsPhPresence += ph2 - ph1;
                        if (presenceRefused) continue;

                        // A fight site owns its fleets' materialisation (FightWaves); under load it
                        // shrinks its field rather than holding the fleets off.
                        if (WaveHolds(st, fi, fleet, now)) continue;

                        MaterialiseDecision gate = MaterialiseGate != null && !fleet.IsBench ? MaterialiseGate(fleet.Kind) : MaterialiseDecision.Materialise;
                        if (slowTrace) _obsPhGate += Stopwatch.GetTimestamp() - ph2;
                        if (gate == MaterialiseDecision.ResolveOffScreen)
                        {
                            NoteHeldOffScreen(fleet);
                            continue;
                        }
                        if (gate == MaterialiseDecision.Wait && HoldCombat(st, fi, fleet, now)) continue;
                        _heldOffScreen.Remove(fleet.FleetId ?? "");
                        _combatHolds.Release(fleet.FleetId);

                        long ph3 = slowTrace ? Stopwatch.GetTimestamp() : 0;
                        BeginMaterialize(st, fi, fleet, now);
                        if (slowTrace) _obsPhBegin += Stopwatch.GetTimestamp() - ph3;
                    }
                    else if (fleet.MaterialisedEntityId == PendingFleetAction.SpawningId)
                    {
                        // Dedup guard: if an earlier same-kind entry is already the designated
                        // spawner (SpawningId or live grid), dismiss this duplicate. Without this,
                        // each entry with M=SpawningId independently calls SpawnPrefab — no burst
                        // cap applies in this branch — producing N simultaneous physical grids.
                        if (IsCappedSpawnKind(fleet.Kind) && !fleet.IsBench)
                        {
                            bool priorSpawnerExists = false;
                            for (int oi = 0; oi < fi; oi++)
                            {
                                PendingFleetAction prior = st.PendingFleets[oi];
                                if (prior.Kind != fleet.Kind || prior.IsBench) continue;
                                if (prior.MaterialisedEntityId > 0
                                    || prior.MaterialisedEntityId == PendingFleetAction.SpawningId)
                                { priorSpawnerExists = true; break; }
                            }
                            if (priorSpawnerExists)
                            {
                                var dismissed = fleet;
                                dismissed.MaterialisedEntityId = -1;
                                // Read off the live leg BEFORE the force-arrive below overwrites the
                                // window it is measured against -- see PendingFleetResolver.LossRatioFor.
                                dismissed.ForcedRatio = PendingFleetResolver.LossRatioFor(fleet, now);
                                dismissed.EstimatedArrivalUtc = now;
                                dismissed.ForcedRatioCause = "spawn_dedup";
                                st.PendingFleets[fi] = dismissed;
                                _log.Write("SPAWN_DEDUP " + fleet.LogId + " " + fleet.ActorTag
                                           + " kind=" + fleet.Kind + " — duplicate dismissed");
                                continue;
                            }
                        }

                        // Poll every tick until the async spawn callback fires or we time out.
                        string fleetKey = FleetKey(fleet);
                        _escortScratch.Clear();
                        bool unsupportedKind;
                        long eid = SpawnFleetGrid(fleet, st.FactionId, now, fleetKey, _escortScratch, out unsupportedKind);
                        if (eid > 0)
                        {
                            _spawnsInFlight.Remove(fleetKey);
                            _meetingSpawnAt.Remove(fleetKey);
                            var updated = fleet;
                            updated.MaterialisedEntityId = eid;
                            updated.EscortEntityIds = _escortScratch.Count > 0 ? new List<long>(_escortScratch) : null;
                            st.PendingFleets[fi] = updated;
                            WatchHullIntegrity(updated);
                            _spawnSettle.Arm(eid, now, SpawnSettleGrace);
                            NoteSpawned(eid, now);
                            _log.Write("SPAWNED " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                                       + " eid=" + eid + " escorts=" + _escortScratch.Count);
                            SendNearbyFleetMessage(fleet.CurrentPosKm(now, _tuning), SpawnMessageFor(fleet), fleetTraffic: true);
                        }
                        else if (unsupportedKind)
                        {
                            // A kind with no spawn wiring returns the same 0 an in-flight prefab load
                            // does. Left as SpawningId it would cycle through SPAWN_TIMEOUT and back
                            // forever, so it is put down as unmaterialised and reported instead.
                            _spawnsInFlight.Remove(fleetKey);
                            var updated = fleet;
                            updated.MaterialisedEntityId = 0;
                            st.PendingFleets[fi] = updated;
                            ReportUnsupportedSpawnKind(fleet, fleetKey);
                        }
                        else
                        {
                            var routing = _spawner as VanillaFleetSpawner;

                            // Still waiting its turn, and no player is near the fleet any more: the grid
                            // would land unseen and be closed at the end of its settle grace, a full
                            // spawn for nothing (FAC-512: 81 of 2,117 spawns over two days). Dropped
                            // instead; the gate asks again if a player comes back. The keep radius,
                            // not the gate's, so the two cannot alternate on a player at the edge. A
                            // squadron's hulls land one by one and a fight's are its own; both wait.
                            if (routing != null && !fleet.IsBench && !SpawnsAsSquadron(fleet)
                                && !_meetingSpawnAt.ContainsKey(fleetKey)
                                && !isPlayerWithin(fleet.CurrentPosKm(now, _tuning), _tuning.ObservationRadiusKm + DespawnHysteresisKm)
                                && routing.WithdrawSpawn(fleetKey))
                            {
                                _spawnsInFlight.Remove(fleetKey);
                                var withdrawn = fleet;
                                withdrawn.MaterialisedEntityId = 0;
                                st.PendingFleets[fi] = withdrawn;
                                _log.Write("SPAWN_WITHDRAWN " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                                           + " — no player within despawn radius");
                                continue;
                            }

                            // Waiting in the spawn queue is not a stalled spawn: keep its clock fresh.
                            // Nor is a spawn an agent is still answering (FAC-506): the agent's own
                            // queue and timeout run there, and the registry gives up a silent one.
                            if (SpawnPacing.Pacer.IsQueued(SpawnPacing.FleetKey(fleetKey))
                                || (routing != null && routing.AwaitsRemoteSpawn(fleetKey)))
                                _spawnsInFlight[fleetKey] = now;
                            DateTime startedAt;
                            if (_spawnsInFlight.TryGetValue(fleetKey, out startedAt)
                                && (now - startedAt) >= SpawnTimeoutFor(fleet))
                            {
                                // Spawn stalled (prefab callback never fired) — abandon and retry later.
                                _spawnsInFlight.Remove(fleetKey);
                                _meetingSpawnAt.Remove(fleetKey);
                                var updated = fleet;
                                updated.MaterialisedEntityId = 0;
                                st.PendingFleets[fi] = updated;
                                _log.Write("SPAWN_TIMEOUT " + fleet.LogId + " " + fleet.ActorTag
                                           + " kind=" + fleet.Kind + " key=" + fleetKey);
                            }
                        }
                    }
                    else if (fleet.MaterialisedEntityId > 0)
                    {
                        if (!_spawner.GridExists(fleet.MaterialisedEntityId))
                        {
                            long goneEid = fleet.MaterialisedEntityId;

                            // A grid can leave the world for reasons that are not combat: an admin
                            // deleting it, an engine cleanup, a reload that did not restore it, or
                            // our own despawn closing it along with a fleet-mate. Only a grid that
                            // was being shot apart, and that we did not close, may cost the faction
                            // its hull (GridLossRule); otherwise the fleet de-materialises and flies
                            // on abstractly, re-spawning the next time a player is near.
                            DateTime? lastDamage = _spawner.LastDamageUtc(goneEid);
                            bool closedByUs = _spawner.ClosedByUs(goneEid);
                            bool destroyedInCombat = GridLossRule.IsDestroyed(closedByUs, lastDamage, now);
                            // Read the lead's prefab while the spawner still knows the grid; the
                            // dispatch-time hull is the fallback for a grid it never materialised.
                            string leadPrefab = _spawner.GridPrefabId(goneEid) ?? fleet.ShipPrefabId;

                            _stalledSince.Remove(goneEid);
                            _safezoneArrivedSince.Remove(goneEid);
                            _engagementHeldSince.Remove(goneEid);
                            _spawner.ReleaseControl(goneEid);

                            // At a fight site a lead is one hull of the wave, not the fleet.
                            if (WaveLeadGone(st, fi, fleet, goneEid, destroyedInCombat, leadPrefab,
                                             destroyedInCombat ? "grid_destroyed" : "dematerialised", now)) continue;

                            // An attack whose lead was shot down has lost its meeting (Core,
                            // SettleLeadLost): its escorts still flying are the survivors, which fly
                            // out of the fight rather than closing here. Escorts already gone are
                            // settled first, so only the ones still flying are counted as survivors.
                            bool attackLost = destroyedInCombat && PendingFleetResolver.MissionHangsOnLead(fleet);
                            if (attackLost) fleet = SweepEscorts(st, fi, fleet, now);

                            // Read the escorts' integrity before they close; a lead lost in combat
                            // gives up its own hull slot here.
                            fleet = SettleHulls(fleet, goneEid, destroyedInCombat ? "grid_destroyed" : "dematerialised",
                                                destroyedInCombat ? goneEid : 0, leadPrefab);

                            // Escorts exist only while the lead does: otherwise the rest of the
                            // squadron leaves the world with it.
                            int escortsClosed = attackLost ? 0 : DespawnEscorts(fleet.EscortEntityIds, out _);

                            if (!destroyedInCombat)
                            {
                                var dematerialised = fleet;
                                dematerialised.MaterialisedEntityId = 0;   // eligible to re-spawn
                                dematerialised.EscortEntityIds = null;
                                st.PendingFleets[fi] = dematerialised;
                                if (!string.IsNullOrEmpty(fleet.FleetId))
                                    _rematerialiseAfter.Arm(fleet.FleetId, now, RematerialiseBackoff);
                                _log.Write("FLEET_DEMATERIALISED " + fleet.LogId + " " + fleet.ActorTag
                                           + " eid=" + goneEid + " kind=" + fleet.Kind
                                           + EscortsToken(escortsClosed)
                                           + " why=" + (closedByUs ? "closed_by_us" : lastDamage.HasValue ? "damage_stale" : "no_damage")
                                           + " — grid gone; fleet continues, stock intact");
                                continue;
                            }

                            // Grid was destroyed by players: force-arrive next tick
                            var updated = fleet;
                            updated.MaterialisedEntityId = -1;  // sentinel: destroyed
                            // Kept for an attack that lost: Core hands the survivors' grids to the
                            // departure when it retires the fleet.
                            if (!attackLost) updated.EscortEntityIds = null;
                            // One hull died here, not the whole squadron: the escorts that were
                            // still flying are still eligible to be credited back on resolution.
                            // SettleHulls above has already written the lead's hull off. A lead
                            // shot down is worth nothing to the fleet, whatever part of its leg had run.
                            updated.EstimatedArrivalUtc = now;  // arrive next tick
                            updated.ForcedRatio = 0.0;
                            updated.ForcedRatioCause = "grid_destroyed";
                            st.PendingFleets[fi] = updated;
                            _log.Write("FLEET_DESTROYED " + fleet.LogId + " " + fleet.ActorTag + " eid=" + goneEid
                                       + " last_damage=" + lastDamage.Value.ToString("HH:mm:ss")
                                       + " stop=" + fleet.CurrentStopKind
                                       + EscortsToken(escortsClosed)
                                       + (attackLost ? " survivors_leave=" + EscortCount(updated) : "")
                                       + " -> force-resolve next tick (ratio=0.00)");
                            // Guard-via-PvEBounty success signal: any confirmed-destroyed grid is
                            // reported here, whether or not it ever had a bounty -- the torch-host
                            // handler owns its own accepted-bounty-by-FleetId lookup and must no-op
                            // silently for the (overwhelmingly common) case where it didn't.
                            if (!string.IsNullOrEmpty(fleet.FleetId))
                                OnPvEBountyTargetDestroyed?.Invoke(fleet.FleetId);
                            DismissSiblings(st, fi, fleet, now);
                        }
                        else
                        {
                            // The lead is alive, so its escorts are checked on their own: an escort
                            // that dies must cost one hull and nothing else. Runs before the lead's
                            // own checks so a squadron that loses an escort this tick still reports
                            // the right strength on the lines below.
                            fleet = SweepEscorts(st, fi, fleet, now);

                            // Use physical grid position for the player-proximity check — NOT sim
                            // position. The sim moves at 300 km/min; using sim pos would despawn
                            // the grid ~10 s after spawn even with the player standing next to it.
                            Vec3? physKm = _spawner.GetGridPositionKm(fleet.MaterialisedEntityId);
                            Vec3 checkPos = physKm.HasValue ? physKm.Value : fleet.CurrentPosKm(now, _tuning);
                            double departedKm = physKm.HasValue
                                ? _legOrigins.Observe(fleet.MaterialisedEntityId, fleet.DepartedUtc, physKm.Value, now)
                                : -1;

                            // --- Outcome 3: capture — owner faction changed, player keeps the prize ---
                            string ownerTag = _spawner.GetGridOwnerFactionTag(fleet.MaterialisedEntityId);
                            if (ownerTag != null
                                && !string.Equals(ownerTag, fleet.ActorTag, StringComparison.OrdinalIgnoreCase))
                            {
                                long capturedEid = fleet.MaterialisedEntityId;
                                if (WaveLeadGone(st, fi, fleet, capturedEid, true,
                                                 _spawner.GridPrefabId(capturedEid) ?? fleet.ShipPrefabId, "grid_captured", now))
                                {
                                    _log.Write("FLEET_CAPTURED " + fleet.LogId + " " + fleet.ActorTag + " eid=" + capturedEid
                                               + " new_owner=" + ownerTag + " wave=1");
                                    continue;
                                }
                                var captured = fleet;
                                // An attack whose lead was taken has lost its meeting; its escorts
                                // still flying fly out of the fight (Core, SettleLeadLost).
                                bool attackLost = PendingFleetResolver.MissionHangsOnLead(fleet);
                                captured.MaterialisedEntityId = -1;
                                captured.ForcedRatio = 0.0;
                                captured.EstimatedArrivalUtc = now;
                                captured.ForcedRatioCause = "grid_captured";
                                // Taking the lead takes the fleet, but only the hull the player is
                                // standing in: the escorts leave with it and stay creditable.
                                captured = SettleHulls(captured, capturedEid, "grid_captured", capturedEid,
                                                       _spawner.GridPrefabId(capturedEid) ?? fleet.ShipPrefabId);
                                int capturedEscorts = attackLost ? 0 : DespawnEscorts(fleet.EscortEntityIds, out _);
                                if (!attackLost) captured.EscortEntityIds = null;
                                st.PendingFleets[fi] = captured;
                                _stalledSince.Remove(capturedEid);
                                _safezoneArrivedSince.Remove(capturedEid);
                                _engagementHeldSince.Remove(capturedEid);
                                _spawner.ReleaseControl(capturedEid);   // the player owns it now
                                _log.Write("FLEET_CAPTURED " + fleet.LogId + " " + fleet.ActorTag + " eid=" + capturedEid
                                           + " new_owner=" + ownerTag + EscortsToken(capturedEscorts)
                                           + (attackLost ? " survivors_leave=" + EscortCount(captured) : "") + " (ratio=0)");
                                DismissSiblings(st, fi, fleet, now);
                                continue;
                            }

                            // A clearance waypoint is a transit milestone, not a destination. Braking
                            // to rest and re-wiring the autopilot at each one would turn a smooth arc
                            // into a stop-and-go crawl, so the convoy stays on its cruise autopilot
                            // and passes the waypoint at a fly-through radius instead.
                            bool flyThrough = fleet.CurrentStopKind == FleetStopKind.Navigation;

                            // Launch, then terminal approach. Both controllers decide for themselves
                            // whether to take over and report whether they now own the grid, so the
                            // stall detector below can exempt a grid that is slow for a legitimate
                            // reason -- still accelerating off its origin, or deliberately braking.
                            //
                            // Launch is asked FIRST because Begin is idempotent on kind: asking for an
                            // approach on a launching grid would convert it in place and take the whole
                            // cruise leg away from the vanilla autopilot's collision avoidance.
                            //
                            // A lead under a wing commander's order is flown by the order instead,
                            // and asks for none of it (FactionRuntime.Wing).
                            if (FlyCommanded(fleet)) continue;

                            // A queued arrival spawned at its holding slot (traffic control, T4) is
                            // flown to the slot, not its target, and asks for no launch: the approach
                            // controller parks it there and keeps it there, which also keeps the stall
                            // detector off it. The arrival check below still measures the target, so
                            // a parked holder never completes its leg.
                            // A civilian under fire runs, and a miner on its dwell flies its ring
                            // (FactionRuntime.Steering). Either way the grid goes where it is steered,
                            // asks for no approach, and does not complete its leg here.
                            Vec3 steerTo;
                            Steer steer = SteerTraffic(fleet, checkPos, now, out steerTo);
                            bool steered = steer != Steer.None;

                            Vec3 holdSlot = default(Vec3);
                            bool holding = !steered && HoldsLiveAt(st, fi, fleet, now, out holdSlot);
                            if (holding) fleet = st.PendingFleets[fi];
                            Vec3 flyTo = holding ? holdSlot : steered ? steerTo : fleet.TargetPosKm;
                            // A wandering miner may still need the launch to get off the mark; a fleeing
                            // ship is already rewired onto its run.
                            bool launching = !holding && steer != Steer.Flee
                                             && _spawner.BeginLaunch(fleet.MaterialisedEntityId, flyTo, flyThrough || steered);

                            // --- Outcome 0: the launch ladder gave up on flying this grid ---
                            // Every retry is exhausted and the cruise autopilot still will not fly it.
                            // Despawning and letting the leg run abstractly is what a player leaving
                            // range already does and it costs the faction nothing, which a stall
                            // force-fail (ratio=0, hull and cargo destroyed) emphatically is not.
                            // Checked before the stall detector so the grid can never reach it.
                            if (_spawner.ConsumeLaunchGiveUp(fleet.MaterialisedEntityId))
                            {
                                long stuckEid = fleet.MaterialisedEntityId;
                                fleet = SettleHulls(fleet, stuckEid, "launch_giveup");
                                int stuckGrids = _spawner.Despawn(stuckEid);
                                int escortsClosed = DespawnEscorts(fleet.EscortEntityIds, out int stuckEscortGrids);
                                var released = fleet;
                                released.MaterialisedEntityId = 0;
                                released.EscortEntityIds = null;
                                st.PendingFleets[fi] = released;
                                _stalledSince.Remove(stuckEid);
                                _settleHeldReported.Remove(stuckEid);
                                if (!string.IsNullOrEmpty(fleet.FleetId))
                                    _rematerialiseAfter.Arm(fleet.FleetId, now, LaunchGiveUpBackoff);
                                _log.Write("FLEET_LAUNCH_GIVEUP " + fleet.LogId + " " + fleet.ActorTag
                                           + " eid=" + stuckEid
                                           + EscortsToken(escortsClosed)
                                           + " closed=" + (stuckGrids + stuckEscortGrids)
                                           + " — could not be flown off its origin; leg continues abstractly");
                                // A fight site would spawn the same hull straight back into this launch,
                                // and while it still counts the fleet the dwell is judged as fought by
                                // grids that are gone. It leaves the site, and the duel fights the rest.
                                BarFromWaves(released, "launch_giveup", now);
                                continue;
                            }

                            // --- Outcome 0b: the grid has no thrusters left ---
                            // Nothing can fly it again, and it drifts at whatever speed it had, so the
                            // stall detector may never reach it -- and if it does, its force-fail
                            // destroys the cargo of a fleet nobody shot down. Dematerialised like a
                            // launch give-up: the leg continues abstractly with stock intact. A hull
                            // that hostile fire left without thrust is not stalled but disabled: it
                            // is written off where it lies and never comes back
                            // (FactionRuntime.Disabled).
                            if (_spawner.ConsumeThrustLost(fleet.MaterialisedEntityId))
                            {
                                long thrustlessEid = fleet.MaterialisedEntityId;
                                // A waved lead is one hull of the wave: never the fleet's write-off.
                                if (IsWaved(fleet))
                                {
                                    // Shot out of action, its hull alone is lost and its grid left as a wreck.
                                    if (WaveHullShotOutOfAction(st, fi, thrustlessEid, WaveSlotOf(fleet, thrustlessEid),
                                                                fleet.ShipPrefabId, true, "thrust_lost", now))
                                        continue;
                                    // Off the field, still the fleet's: settled, closed, back in the queue.
                                    if (WaveLeadGone(st, fi, fleet, thrustlessEid, false,
                                                     _spawner.GridPrefabId(thrustlessEid) ?? fleet.ShipPrefabId, "thrust_lost", now))
                                    {
                                        _spawner.Despawn(thrustlessEid);
                                        continue;
                                    }
                                }
                                if (TryDisableLead(st, fi, fleet, now)) continue;
                                fleet = SettleHulls(fleet, thrustlessEid, "thrust_lost");
                                int thrustlessGrids = _spawner.Despawn(thrustlessEid);
                                int escortsClosed = DespawnEscorts(fleet.EscortEntityIds, out int thrustlessEscortGrids);
                                var released = fleet;
                                released.MaterialisedEntityId = 0;
                                released.EscortEntityIds = null;
                                st.PendingFleets[fi] = released;
                                _stalledSince.Remove(thrustlessEid);
                                _safezoneArrivedSince.Remove(thrustlessEid);
                                _engagementHeldSince.Remove(thrustlessEid);
                                _settleHeldReported.Remove(thrustlessEid);
                                if (!string.IsNullOrEmpty(fleet.FleetId))
                                    _rematerialiseAfter.Arm(fleet.FleetId, now, RematerialiseBackoff);
                                _log.Write("FLEET_THRUST_LOST " + fleet.LogId + " " + fleet.ActorTag
                                           + " eid=" + thrustlessEid
                                           + " stop=" + fleet.CurrentStopKind
                                           + EscortsToken(escortsClosed)
                                           + " closed=" + (thrustlessGrids + thrustlessEscortGrids)
                                           + " -> dematerialised, leg continues abstractly");
                                continue;
                            }

                            bool manoeuvring = launching
                                || (!steered && (!flyThrough || holding)
                                    && _spawner.BeginManoeuvre(fleet.MaterialisedEntityId, flyTo));

                            // Escorts hold formation on the lead, or fight beside it, when escort
                            // control is on; they own none of the checks below. Asked after the lead,
                            // so a lead taken this frame already counts toward the bubble cap.
                            FlyEscorts(fleet);

                            // --- Outcome 4: reached target — resolve after a grace period ---
                            // Distance-to-TargetPosKm check against ArrivalRadiusM — NOT a live
                            // MySafeZone entity query, since the destination's safezone entity may not
                            // even be spawned yet if no player has been near it.
                            // Runs before the stall/despawn-on-leave checks below: a grid parked at its
                            // target is *supposed* to look stationary and must not be swept up by those.
                            double safezoneRadiusKm = ArrivalRadiusM * 0.001;
                            if (flyThrough)
                                safezoneRadiusKm = Math.Max(ArrivalRadiusM,
                                                            _tuning.NavigationFlyThroughRadiusM) * 0.001;
                            bool inArrivalRange = !steered && checkPos.DistanceTo(fleet.TargetPosKm) <= safezoneRadiusKm;

                            // A clearance-adjusted stop point may rest outside the safezone radius
                            // (up to ApproachStopDistanceM + ApproachMaxStandoffM from the node).
                            // Accept the controller's own "at rest at my stop point" as arrival too,
                            // bounded to that envelope so a controller fault can never resolve a leg
                            // from arbitrary distance. Feeds the same grace timer below — arrival
                            // timing semantics are unchanged either way.
                            if (!inArrivalRange && !steered && manoeuvring
                                && _spawner.IsManoeuvreComplete(fleet.MaterialisedEntityId))
                            {
                                double envelopeKm = (_tuning.ApproachStopDistanceM
                                                     + _tuning.ApproachMaxStandoffM
                                                     + _tuning.ApproachArriveToleranceM) * 0.001;
                                inArrivalRange = checkPos.DistanceTo(fleet.TargetPosKm) <= envelopeKm;
                            }

                            // --- Outcome 4a: holding station through an engagement dwell ---
                            // A raider on its Engage leg has arrived at what it came to attack and is
                            // fighting there. Completing the leg here -- releasing control and either
                            // despawning or handing it the next stop -- is exactly what used to make
                            // the dwell unreachable: the grid left within five seconds of touching the
                            // target while the abstract fleet counted down alone. So the leg is not
                            // completed while the dwell runs. The grid keeps its controller, stays
                            // where it is, and stays shootable for the whole window.
                            //
                            // Nothing here ends the dwell. Core does, on its own clock (ResolveArrivals
                            // exempts this leg from the materialised-fleet defer for that reason), and
                            // when it advances the fleet to its return leg TargetPosKm moves away from
                            // here, this branch stops matching, and the ordinary approach/arrival
                            // machinery below takes the grid home.
                            // An interception window (FAC-307) is the same hold: the convoy and the
                            // raid that caught it both park at the meeting node, shootable, until
                            // Core decides the encounter or a player does.
                            // A Guard standing its duty window is the same hold again: it was
                            // sent to sit at this node, and completing the leg would despawn the
                            // one grid the defence mechanic exists to put in front of a player.
                            if (fleet.HoldsStation(now, _tuning))
                            {
                                // Weapons dry: a fleet with nothing to fire does not hold station
                                // to be shot at. It breaks off and Core takes it home.
                                if (TryRetreatOutOfAmmunition(st, fi, ref fleet, now)) continue;
                                if (!_engagementHeldSince.ContainsKey(fleet.MaterialisedEntityId))
                                {
                                    _engagementHeldSince[fleet.MaterialisedEntityId] = now;
                                    _log.Write("FLEET_HOLDING_STATION " + fleet.LogId + " " + fleet.ActorTag
                                               + " eid=" + fleet.MaterialisedEntityId
                                               + " @ " + (fleet.TargetNodeId ?? "?")
                                               + " until=" + fleet.HoldEndsUtc(_tuning).ToString("HH:mm:ss"));
                                    SendNearbyFleetMessage(checkPos, ArrivalMessageFor(fleet), fleetTraffic: true);
                                }
                                // The stall detector would otherwise read a deliberately-stationary
                                // raider as crashed debris and force-fail it at ratio 0 -- a raid
                                // cancelled for doing precisely what the mechanic asks of it.
                                _stalledSince.Remove(fleet.MaterialisedEntityId);
                                _safezoneArrivedSince.Remove(fleet.MaterialisedEntityId);
                                HoldFight(st, fi, fleet, now);
                                continue;
                            }
                            // The dwell just ended and Core has already moved this fleet onto its next
                            // leg. The grid is still parked under an ApproachAndStop the flight
                            // controller aimed at the node it is sitting on, and Begin is idempotent
                            // on kind -- asking it again for the new target would not re-aim it. Only
                            // a Retarget (release, then wire a fresh cruise autopilot) actually sends
                            // it home, which is the same call the ordinary leg-complete path makes.
                            // A repelled lead breaking away from the node is left to it until clear.
                            if (TickRepelledLeave(fleet, now)) continue;
                            if (_engagementHeldSince.Remove(fleet.MaterialisedEntityId))
                            {
                                // Repelled: seen to break off and run before it is sent home.
                                if (WasRepelled(fleet) && StartRepelledLeave(fleet, now)) continue;
                                bool wired = _spawner.Retarget(fleet.MaterialisedEntityId, fleet.TargetPosKm);
                                // The escorts held station on the same waypoint and need the same
                                // fresh cruise leg, or they sit at the target while the lead leaves.
                                int escortsRewired = RetargetEscorts(fleet.EscortEntityIds, fleet.TargetPosKm);
                                _log.Write("FLEET_ENGAGEMENT_OVER " + fleet.LogId + " " + fleet.ActorTag
                                           + " eid=" + fleet.MaterialisedEntityId
                                           + " stop=" + fleet.CurrentStopKind
                                           + " -> " + (fleet.TargetNodeId ?? "[navigation]")
                                           + " rewired=" + wired
                                           + EscortTally(escortsRewired, EscortCount(fleet)));
                                SendNearbyFleetMessage(checkPos, DepartureMessageFor(fleet), fleetTraffic: true);
                                continue;
                            }

                            if (inArrivalRange)
                            {
                                DateTime arrivedAt;
                                bool firstTouch = !_safezoneArrivedSince.TryGetValue(fleet.MaterialisedEntityId, out arrivedAt);
                                if (firstTouch)
                                {
                                    _safezoneArrivedSince[fleet.MaterialisedEntityId] = now;
                                    // Clearance waypoints stay out of the log: a routed detour crosses
                                    // several, and none of them mean anything to an operator.
                                    if (!flyThrough)
                                    {
                                        _log.Write("FLEET_TARGET_REACHED " + fleet.LogId + " " + fleet.ActorTag
                                                   + " eid=" + fleet.MaterialisedEntityId + " stop=" + fleet.CurrentStopKind);
                                        SendNearbyFleetMessage(checkPos, ArrivalMessageFor(fleet), fleetTraffic: true);
                                    }
                                }
                                // No grace on a fly-through: the point of not braking is that there is
                                // nothing to settle before handing the grid its next waypoint.
                                if (flyThrough || (!firstTouch && (now - arrivedAt) >= SafezoneArrivalGrace))
                                {
                                    long reachedEid = fleet.MaterialisedEntityId;
                                    fleet = SettleHulls(fleet, reachedEid, "leg_end");
                                    _safezoneArrivedSince.Remove(reachedEid);
                                    _stalledSince.Remove(reachedEid);
                                    // The approach is over either way: Retarget re-wires the cruise
                                    // autopilot for the next leg, and Despawn closes the grid.
                                    _spawner.ReleaseControl(reachedEid);
                                    bool hasNextLeg = fleet.RemainingStops != null && fleet.RemainingStops.Count > 0
                                                       && !string.IsNullOrEmpty(fleet.FleetId);
                                    var resolved = fleet;
                                    resolved.MaterialisedEntityId = 0;
                                    resolved.EscortEntityIds = null;
                                    if (hasNextLeg)
                                    {
                                        // Stash the whole squadron, lead first, so the next leg
                                        // reclaims every hull instead of respawning a short fleet.
                                        //
                                        // FleetId is not unique (FAC-058), so two fleets can stash
                                        // under one key and the second write drops the first's hulls
                                        // in the world with nothing left holding their ids. Close
                                        // what is displaced: the fleet that loses them respawns on
                                        // its next leg, which is what FLEET_RETARGET_FAILED already
                                        // does, and an orphan is the one outcome nothing recovers.
                                        StashedSquadron displaced;
                                        if (_pendingRetargetEid.TryGetValue(fleet.FleetId, out displaced))
                                        {
                                            int displacedClosed = DespawnEidsExcept(displaced.Eids, reachedEid, out int displacedGrids);
                                            _log.Write("FLEET_STASH_DISPLACED " + fleet.LogId + " " + fleet.ActorTag
                                                       + " hulls=" + (displaced.Eids == null ? 0 : displaced.Eids.Length)
                                                       + " closed=" + displacedClosed + " grids=" + displacedGrids);
                                        }
                                        _pendingRetargetEid[fleet.FleetId] = new StashedSquadron
                                        {
                                            Eids = StashEids(reachedEid, fleet.EscortEntityIds),
                                            LegDepartedUtc = fleet.DepartedUtc,
                                        };
                                        SendNearbyFleetMessage(checkPos, DepartureMessageFor(fleet), fleetTraffic: true);
                                        if (!flyThrough)
                                            _log.Write("FLEET_LEG_COMPLETE " + fleet.LogId + " " + fleet.ActorTag + " eid=" + reachedEid
                                                       + EscortsToken(EscortCount(fleet)));
                                    }
                                    else
                                    {
                                        SendNearbyFleetMessage(checkPos, PreDespawnMessageFor(fleet), fleetTraffic: true);
                                        int arrivedGrids = _spawner.Despawn(reachedEid);
                                        int escortsClosed = DespawnEscorts(fleet.EscortEntityIds, out int arrivedEscortGrids);
                                        _log.Write("FLEET_ARRIVED_DESPAWN " + fleet.LogId + " " + fleet.ActorTag + " eid=" + reachedEid
                                                   + EscortsToken(escortsClosed)
                                                   + " closed=" + (arrivedGrids + arrivedEscortGrids));
                                        // The grid is at the stop, so the fleet is too. Left on its abstract
                                        // schedule it was an approaching arrival again until that ETA: at a
                                        // queued station it rejoined, cleared, and was spawned again back
                                        // along its leg (2026-10-03 at N600: one Naginata spawned three times
                                        // for one trip home, each spawn a ~460 ms frame).
                                        if (resolved.EstimatedArrivalUtc > now) resolved.EstimatedArrivalUtc = now;
                                    }
                                    st.PendingFleets[fi] = resolved;
                                }
                                continue; // still in grace, or just acted — defer every other check this tick either way
                            }

                            // Update stall timer regardless of player proximity (crashed grid stays stalled
                            // even while a player is fighting it; timer is only *acted on* when no player is
                            // within 2 km, so legitimate combat slowdowns don't prematurely force-fail).
                            // A grid under native flight control is exempt: the approach profile holds it
                            // below StallSpeedKmPerMin (8.3 m/s) for far longer than StallTimeout while it
                            // is still outside the safezone ring, which would otherwise force-fail a
                            // perfectly healthy arrival as crashed debris and destroy its cargo.
                            double speedKmPerMin = _spawner.GetGridSpeedKmPerMin(fleet.MaterialisedEntityId);
                            // A launch handback still being checked is flown by the retry ladder, which
                            // ends in a grid under way or a lossless give-up; the stall clock must not
                            // race it to a force-fail.
                            bool isStalled = !holding && !manoeuvring && !_spawner.IsManoeuvring(fleet.MaterialisedEntityId)
                                             && speedKmPerMin >= 0 && speedKmPerMin < StallSpeedKmPerMin;
                            if (isStalled)
                            {
                                if (!_stalledSince.ContainsKey(fleet.MaterialisedEntityId))
                                    _stalledSince[fleet.MaterialisedEntityId] = now;
                            }
                            else
                            {
                                _stalledSince.Remove(fleet.MaterialisedEntityId);
                            }

                            // --- Outcome 2 (deferred): player is interacting — don't clean up yet ---
                            // Exception: a stalled grid that has lingered past StallTimeoutWithPlayer
                            // is treated as crashed debris and cleaned up even with the player nearby.
                            // This prevents wrecks from accumulating permanently at player bases.
                            if (isPlayerWithin(checkPos, PlayerInteractionKm))
                            {
                                if (_landedUnseen.Count > 0) _landedUnseen.Release(fleet.MaterialisedEntityId);
                                DateTime stalledAt;
                                bool crashedTooLong = isStalled
                                    && _stalledSince.TryGetValue(fleet.MaterialisedEntityId, out stalledAt)
                                    && (now - stalledAt) >= StallTimeoutWithPlayer;
                                if (!crashedTooLong) continue;
                            }

                            // --- Outcome 1b: over its neighbourhood's share — shed back to abstract ---
                            // Traffic control sheds the farthest civilian grid when a player's share of
                            // the budget is exceeded; the fleet carries on unseen, as if the player had
                            // left, and the presence rule decides when it may come back.
                            if (PresenceSheds(fleet, now))
                            {
                                CloseForRange(st, fi, fleet, fleet.MaterialisedEntityId, now, "FLEET_SHED");
                                continue;
                            }

                            // --- Outcome 1: player left range — despawn and resume abstract transit ---
                            // Despawn radius is ObservationRadiusKm + DespawnHysteresisKm so the exit
                            // threshold sits outside the entry threshold.  Without this gap the random
                            // 300–600 m spawn-clearance offset can put the physical grid just beyond
                            // the observation radius on the very first M>0 frame, immediately firing
                            // Outcome 1, resetting M=0, and re-triggering spawn in a tight 60 Hz loop.
                            // The gap covers a standing player. A moving one crosses it during the
                            // prefab load, so a grid inside its settle grace is not judged here at
                            // all, and a grid that is closed here holds its fleet off the spawn path
                            // for a moment so the sim marker cannot request it straight back.
                            double despawnRadiusKm = _tuning.ObservationRadiusKm + DespawnHysteresisKm;
                            if (!isPlayerWithin(checkPos, despawnRadiusKm))
                            {
                                long leftEid = fleet.MaterialisedEntityId;
                                if (_spawnSettle.IsHeld(leftEid, now))
                                {
                                    if (!_landedUnseen.IsHeld(leftEid, now)) _landedUnseen.Arm(leftEid, now, LandedUnseenFor);
                                    DateTime spawnedAt;
                                    if (_settleHeldReported.Add(leftEid) && _spawnSettle.TryGetSince(leftEid, out spawnedAt))
                                        _log.Write("FLEET_SPAWN_HELD " + fleet.LogId + " " + fleet.ActorTag + " eid=" + leftEid
                                                   + " age=" + (now - spawnedAt).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s"
                                                   + " — no player within despawn radius; holding for the settle grace");
                                }
                                else
                                {
                                    bool unseen = _landedUnseen.IsHeld(leftEid, now);
                                    if (unseen)
                                    {
                                        _landedUnseen.Release(leftEid);
                                        _unseenLandings.Note(fleet.FleetId, checkPos, fleet.CurrentPosKm(now, _tuning), now);
                                    }
                                    CloseForRange(st, fi, fleet, leftEid, now, "FLEET_DESPAWN", unseen ? " landed=unseen" : null);
                                    continue;
                                }
                            }
                            else if (_landedUnseen.Count > 0) _landedUnseen.Release(fleet.MaterialisedEntityId);

                            if (physKm.HasValue)
                            {
                                // Player is present but not too close — check drift and stall
                                double driftKm = fleet.CurrentPosKm(now, _tuning).DistanceTo(physKm.Value);
                                if (driftKm > DriftDespawnThresholdKm)
                                {
                                    long driftEid = fleet.MaterialisedEntityId;
                                    fleet = SettleHulls(fleet, driftEid, "drift");
                                    int driftGrids = _spawner.Despawn(driftEid);
                                    // Drift is measured on the lead; the escorts flew the same leg
                                    // and are the same distance behind the sim position.
                                    int escortsClosed = DespawnEscorts(fleet.EscortEntityIds, out int driftEscortGrids);
                                    var updated = fleet;
                                    updated.MaterialisedEntityId = 0;
                                    updated.EscortEntityIds = null;
                                    st.PendingFleets[fi] = updated;
                                    _stalledSince.Remove(driftEid);
                                    _log.Write("FLEET_DRIFT " + fleet.LogId + " " + fleet.ActorTag + " eid=" + driftEid
                                               + " driftKm=" + driftKm.ToString("F0")
                                               + EscortsToken(escortsClosed)
                                               + " closed=" + (driftGrids + driftEscortGrids) + " — despawned");
                                    continue;
                                }

                                // --- Stall fail: crashed/stuck grid with no player interaction ---
                                DateTime stalledAt;
                                if (isStalled
                                    && _stalledSince.TryGetValue(fleet.MaterialisedEntityId, out stalledAt)
                                    && (now - stalledAt) >= StallTimeout)
                                {
                                    long stalledEid = fleet.MaterialisedEntityId;
                                    double duration = (now - stalledAt).TotalSeconds;

                                    // What left the grid with nothing flying it, read before the
                                    // despawn clears the flight state: the stall verdict's lines
                                    // are the only record of it with flight debug off.
                                    var vanillaSpawner = _spawner as VanillaFleetSpawner;
                                    string stallState = " steer=" + steer + " holding=" + holding
                                        + " flyThrough=" + flyThrough
                                        + (vanillaSpawner != null
                                            ? " " + vanillaSpawner.FlightTally(stalledEid) + " " + vanillaSpawner.AnchorTally(stalledEid)
                                            : "");

                                    // A grid still where it began this leg never got under way: it
                                    // was not wrecked, nothing flew it off its stop. It leaves the
                                    // world the way a launch give-up does and loses nothing
                                    // (FAC-500); only a grid that flew and then stopped is debris.
                                    if (departedKm >= 0 && departedKm < StallDepartureRadiusKm)
                                    {
                                        fleet = SettleHulls(fleet, stalledEid, "stalled_at_origin");
                                        int parkedGrids = _spawner.Despawn(stalledEid);
                                        int parkedEscorts = DespawnEscorts(fleet.EscortEntityIds, out int parkedEscortGrids);
                                        var released = fleet;
                                        released.MaterialisedEntityId = 0;
                                        released.EscortEntityIds = null;
                                        st.PendingFleets[fi] = released;
                                        _stalledSince.Remove(stalledEid);
                                        _settleHeldReported.Remove(stalledEid);
                                        _legOrigins.Forget(stalledEid);
                                        if (!string.IsNullOrEmpty(fleet.FleetId))
                                            _rematerialiseAfter.Arm(fleet.FleetId, now, LaunchGiveUpBackoff);
                                        _log.Write("FLEET_STALLED_AT_ORIGIN " + fleet.LogId + " " + fleet.ActorTag + " eid=" + stalledEid
                                                   + " stop=" + fleet.CurrentStopKind
                                                   + " moved=" + (departedKm * 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "m"
                                                   + " speed=" + speedKmPerMin.ToString("F2", CultureInfo.InvariantCulture) + " km/min"
                                                   + " duration=" + duration.ToString("F0", CultureInfo.InvariantCulture) + "s"
                                                   + stallState
                                                   + EscortsToken(parkedEscorts)
                                                   + " closed=" + (parkedGrids + parkedEscortGrids)
                                                   + " -> dematerialised, leg continues abstractly");
                                        BarFromWaves(released, "stalled_at_origin", now);
                                        continue;
                                    }

                                    string stalledPrefab = _spawner.GridPrefabId(stalledEid) ?? fleet.ShipPrefabId;
                                    fleet = SettleHulls(fleet, stalledEid, "grid_stalled", stalledEid, stalledPrefab);
                                    int stalledGrids = _spawner.Despawn(stalledEid);
                                    // The detector only ever watches the lead, so only the lead's
                                    // hull is written off; its escorts leave the world with it.
                                    int escortsClosed = DespawnEscorts(fleet.EscortEntityIds, out int stalledEscortGrids);
                                    var updated = fleet;
                                    updated.MaterialisedEntityId = -1;
                                    updated.EscortEntityIds = null;
                                    updated.ForcedRatio = PendingFleetResolver.LossRatioFor(fleet, now);
                                    updated.EstimatedArrivalUtc = now;
                                    updated.ForcedRatioCause = "grid_stalled";
                                    st.PendingFleets[fi] = updated;
                                    _stalledSince.Remove(stalledEid);
                                    _log.Write("FLEET_STALLED " + fleet.LogId + " " + fleet.ActorTag + " eid=" + stalledEid
                                               + " speed=" + speedKmPerMin.ToString("F2") + " km/min"
                                               + " duration=" + duration.ToString("F0") + "s"
                                               + " moved=" + (departedKm * 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "m"
                                               + stallState
                                               + EscortsToken(escortsClosed)
                                               + " closed=" + (stalledGrids + stalledEscortGrids) + " -> force-fail (ratio=0)");
                                    DismissSiblings(st, fi, fleet, now);
                                }
                            }
                        }
                    }
                }
            }
            if (allocTrace && allocKey >= 0) AllocProbe.AddKeyed(ObsAllocKey(allocKey), AllocProbe.Now() - allocIter);
            long tLoopEnd = Stopwatch.GetTimestamp();
            if (slowTrace && iterFleet != null && tLoopEnd - iterStart > slowestTicks) { slowestTicks = tLoopEnd - iterStart; slowestFleet = iterFleet + ObsPhases(); }
            SweepOrphanedStashes(now);
            long tEnd = Stopwatch.GetTimestamp();
            _perfObs.Record(tEnd - _obsT0);
            if (slowTrace && (tEnd - _obsT0) * 1000 > SlowObservationMs * Stopwatch.Frequency)
                FactionsPlugin.Log.Info("OBS_SLOW ms=" + ((tEnd - _obsT0) * 1000.0 / Stopwatch.Frequency).ToString("F1")
                         + " fleets=" + iterations
                         + " slowestMs=" + (slowestTicks * 1000.0 / Stopwatch.Frequency).ToString("F1")
                         + " slowest=" + (slowestFleet ?? "-")
                         + " sweepMs=" + ((tEnd - tLoopEnd) * 1000.0 / Stopwatch.Frequency).ToString("F1")
                         + " cpuMs=" + (cpu0 < 0 ? "?" : ((GridAssembler.ThreadCpuNow() - cpu0) / 10000.0).ToString("F1"))
                         + " gc=" + (GC.CollectionCount(0) - gc0) + "/" + (GC.CollectionCount(1) - gc1) + "/" + (GC.CollectionCount(2) - gc2));
        }

        /// <summary>Whether this fleet has hulls stashed on a leg Core has since advanced past, so
        /// <see cref="PendingFleetAction.TargetPosKm"/> is already the next leg's target and they can
        /// be re-aimed now.</summary>
        /// <summary>Whether any of this faction's pending fleets is holding a physical grid --
        /// materialised, or mid-spawn. The one thing that still needs advancing when there is nobody
        /// to materialise anything new for.</summary>
        private static bool HoldsAnyGrid(FactionState st)
        {
            for (int i = 0; i < st.PendingFleets.Count; i++)
                if (st.PendingFleets[i].MaterialisedEntityId != 0) return true;
            return false;
        }

        private bool HasStashAwaitingReclaim(PendingFleetAction fleet)
        {
            if (fleet.MaterialisedEntityId != 0 || string.IsNullOrEmpty(fleet.FleetId)) return false;
            StashedSquadron stash;
            return _pendingRetargetEid.TryGetValue(fleet.FleetId, out stash)
                   && stash.Eids != null && stash.Eids.Length > 0
                   && stash.LegDepartedUtc != fleet.DepartedUtc;
        }

        /// <summary>Closes squadrons stashed against a fleet that is no longer in anybody's
        /// PendingFleets. The reclaim above is the only other consumer of a stash, so a fleet that
        /// resolves, is intercepted or is dropped while its hulls sit parked between legs otherwise
        /// leaves them in the world permanently, referenced by nothing that could ever clean them
        /// up — and leaves the entry behind to grow the map for the process's lifetime.</summary>
        private void SweepOrphanedStashes(DateTime now)
        {
            if (_pendingRetargetEid.Count == 0) return;
            if ((now - _lastStashSweepUtc) < StashSweepInterval) return;
            _lastStashSweepUtc = now;

            _liveFleetIds.Clear();
            foreach (FactionState st in _states)
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    string fleetId = st.PendingFleets[fi].FleetId;
                    if (!string.IsNullOrEmpty(fleetId)) _liveFleetIds.Add(fleetId);
                }

            _orphanedStashes.Clear();
            foreach (KeyValuePair<string, StashedSquadron> kv in _pendingRetargetEid)
                if (!_liveFleetIds.Contains(kv.Key)) _orphanedStashes.Add(kv.Key);

            for (int i = 0; i < _orphanedStashes.Count; i++)
            {
                string fleetId = _orphanedStashes[i];
                StashedSquadron stash = _pendingRetargetEid[fleetId];
                _pendingRetargetEid.Remove(fleetId);
                int hulls = stash.Eids == null ? 0 : stash.Eids.Length;
                int closed = DespawnEids(stash.Eids, 0, out int orphanedGrids);
                _log.Write("FLEET_STASH_ORPHANED fleet=" + fleetId
                           + " hulls=" + hulls + " closed=" + closed + " grids=" + orphanedGrids);
            }
        }

        /// <summary>Kicks off the async spawn for a not-yet-materialised fleet and records the
        /// resulting state (live grid or in-flight spawn). Only ever called from
        /// <see cref="UpdateObservation"/>'s player-proximity trigger — Guard-via-PvEBounty
        /// deliberately does NOT force an early spawn: abstract transit runs at
        /// <see cref="FactionTuning.FleetSpeedKmPerMin"/> (thousands of km/min) while a materialised
        /// grid's real flight speed is orders of magnitude slower, so a grid spawned before a player
        /// is actually nearby could never keep pace with the fleet's abstract position anyway.</summary>
        /// <summary>Closes a live fleet's grids and returns it to abstract transit, holding it off the
        /// spawn path for a moment so the sim marker cannot request it straight back. The fleet keeps
        /// its hulls; <paramref name="eventName"/> names why in the decision log.</summary>
        private void CloseForRange(FactionState st, int fi, PendingFleetAction fleet, long eid, DateTime now, string eventName,
                                   string suffix = null)
        {
            fleet = SettleHulls(fleet, eid, "player_left");
            int grids = _spawner.Despawn(eid);
            int escortsClosed = DespawnEscorts(fleet.EscortEntityIds, out int escortGrids);
            var updated = fleet;
            updated.MaterialisedEntityId = 0;
            updated.EscortEntityIds = null;
            st.PendingFleets[fi] = updated;
            _stalledSince.Remove(eid);
            _settleHeldReported.Remove(eid);
            _holdAimed.Remove(eid);
            if (!string.IsNullOrEmpty(fleet.FleetId))
                _rematerialiseAfter.Arm(fleet.FleetId, now, LeaveRangeRespawnBackoff);
            _log.Write(eventName + " " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                       + EscortsToken(escortsClosed)
                       + " closed=" + (grids + escortGrids) + (suffix ?? ""));
        }

        private void BeginMaterialize(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            // A fleet joining a dwell fight already under way brings it up to now first, so its hulls
            // appear as the duel left them, and appears at the range of the bracket being fought.
            fleet = PlaceInMeeting(st, fi, fleet, now);

            // DoSpawn always returns 0 on the first call (registers the prefab callback) and the eid
            // on the subsequent poll once the callback fires. Set SpawningId immediately so peers and
            // ResolveArrivals both see this fleet as active before the callback fires.
            string fleetKey = FleetKey(fleet);
            _escortScratch.Clear();
            bool unsupportedKind;
            long eid = SpawnFleetGrid(fleet, st.FactionId, now, fleetKey, _escortScratch, out unsupportedKind);
            if (eid > 0)
            {
                // Synchronous result (unusual but handled).
                _meetingSpawnAt.Remove(fleetKey);
                var updated = fleet;
                updated.MaterialisedEntityId = eid;
                updated.EscortEntityIds = _escortScratch.Count > 0 ? new List<long>(_escortScratch) : null;
                st.PendingFleets[fi] = updated;
                WatchHullIntegrity(updated);
                _spawnSettle.Arm(eid, now, SpawnSettleGrace);
                NoteSpawned(eid, now);
                _log.Write("SPAWNED " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                           + " eid=" + eid + " escorts=" + _escortScratch.Count);
                SendNearbyFleetMessage(fleet.CurrentPosKm(now, _tuning), SpawnMessageFor(fleet), fleetTraffic: true);
                ProbeSpawnAdmission(fleet, fleetKey, now);
            }
            else if (unsupportedKind)
            {
                // This kind has no spawn wiring at all, which returns the same 0 an in-flight prefab
                // load does. Marking it SpawningId would wait for a callback nobody registered, so
                // the fleet stays unmaterialised and says so once instead.
                ReportUnsupportedSpawnKind(fleet, fleetKey);
            }
            else
            {
                // Async in progress — mark as spawning so peers and ResolveArrivals defer.
                var updated = fleet;
                updated.MaterialisedEntityId = PendingFleetAction.SpawningId;
                st.PendingFleets[fi] = updated;
                _spawnsInFlight[fleetKey] = now; // keep for timeout tracking only
                ProbeSpawnAdmission(fleet, fleetKey, now);
            }
        }

        /// <summary>Where a fleet about to materialise appears, by fleet key, when it joins a dwell
        /// fight: set as its spawn begins, read by every poll of that spawn, dropped when it lands.</summary>
        private readonly Dictionary<string, Vec3> _meetingSpawnAt = new Dictionary<string, Vec3>(StringComparer.Ordinal);

        /// <summary>Runs <see cref="PendingFleetResolver.BeforeMaterialise"/> for a fleet about to
        /// spawn and records where it should appear: its opponent's grid (or abstract position, or the
        /// node's station) plus the separation of the bracket being fought, along the line from the
        /// opponent to where the fleet would otherwise have spawned. Returns the fleet as it now
        /// stands, since the duel may have written damage into its hulls.</summary>
        private PendingFleetAction PlaceInMeeting(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            string key = FleetKey(fleet);
            _meetingSpawnAt.Remove(key);
            int si = _states.IndexOf(st);
            if (si < 0) return fleet;
            var lines = new List<string>();
            var rng = new SeededRandom(unchecked((int)(now.Ticks ^ (fleet.FleetId ?? "").GetHashCode())));
            MeetingSpawn spawn = PendingFleetResolver.BeforeMaterialise(_states, _world, _tuning, si, fi, rng, lines);
            foreach (string line in lines) _log.Write("t" + _simTick + " " + line);
            fleet = st.PendingFleets[fi];
            if (!spawn.InMeeting) return fleet;

            Vec3 anchor = spawn.OpponentPosKm;
            if (spawn.OpponentSi >= 0 && _spawner != null)
            {
                long oppEid = _states[spawn.OpponentSi].PendingFleets[spawn.OpponentFi].MaterialisedEntityId;
                Vec3? grid = oppEid > 0 ? _spawner.GetGridPositionKm(oppEid) : null;
                if (grid.HasValue) anchor = grid.Value;
            }
            _meetingSpawnAt[key] = spawn.PositionKm(fleet.CurrentPosKm(now, _tuning), anchor);
            return fleet;
        }

        /// <summary>Reports, once per fleet, that its <see cref="ActionKind"/> reached the spawn
        /// trigger with no materialisation path behind it. The fleet stays unmaterialised and keeps
        /// flying abstractly; the line is what separates that from a prefab load that is merely
        /// slow, which reads identically at the call site.</summary>
        private void ReportUnsupportedSpawnKind(PendingFleetAction fleet, string fleetKey)
        {
            if (!_unsupportedSpawnKinds.Add(fleetKey)) return;
            _log.Write("SPAWN_UNSUPPORTED " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind);
        }

        /// <summary>
        /// Station-materialization loop — called from the Torch Update() tick alongside
        /// <see cref="UpdateObservation"/>. Checks each E### economy-station node against player
        /// proximity and manages the spawn / grace-timer / despawn cycle.
        /// No-op when no session is loaded (<c>_world == null</c>).
        /// </summary>
        /// <summary>Claims every founded station against vanilla's cell generator, from the graph
        /// alone. Must run at session load rather than on the first observation tick -- see
        /// <see cref="StationMaterializer.SeedClaims"/> for why that window matters.</summary>
        public void SeedStationClaims()
        {
            if (_world == null) return;
            _stationMaterializer.SeedClaims(_world.Graph, _states);
        }

        /// <summary>Advances every player siege: charges the fire a safe zone absorbed this tick,
        /// expires abandoned sieges, and pushes the resulting zone state and colour. Takes the
        /// proximity predicate per call, the same way <see cref="UpdateObservation"/> does, because
        /// presence is what holds a breach open.</summary>
        public void TickPlayerSiege(Func<Vec3, double, bool> anyPlayerWithinKm)
        {
            if (_playerSiege == null) return;
            // Retried here in case SE's damage system was not up when the sinks were wired.
            (_spawner as VanillaFleetSpawner)?.EnsureDamageHandler();
            _playerSiege.Tick(anyPlayerWithinKm);
        }

        /// <summary>Hands the siege manager the HUD status panel. The uplink is built after the
        /// runtime, so this arrives late; a siege manager built later still picks it up.</summary>
        internal void AttachStatusPanel(StatusPanelPublisher panel)
        {
            _statusPanel = panel;
            if (_playerSiege != null) _playerSiege.Panel = panel;
        }

        /// <summary>Hands the siege manager the sink for occupation steps (the owner's hail, charge
        /// and memory, <see cref="TrespassHailer.OnOccupation"/>). Null detaches it. Arrives after the
        /// runtime is built, so a siege manager built later still picks it up.</summary>
        internal void AttachOccupationSink(Action<OccupationNotice> sink)
        {
            _occupationSink = sink;
            if (_playerSiege != null) _playerSiege.Occupied = sink;
        }

        /// <summary>Identity of the player besieging a node, or 0 -- what the conquer offer asks.</summary>
        public long SiegeAttackerFor(string nodeId)
        {
            return _playerSiege == null ? 0L : _playerSiege.AttackerFor(nodeId);
        }

        /// <summary>Feeds real block damage against a materialised station into the siege, for the
        /// phase after its zone is down. Called from the host damage hook.</summary>
        public void RecordStationBlockDamage(long gridEntityId, long attackerIdentityId, double damage)
        {
            if (_playerSiege == null) return;
            _playerSiege.RecordBlockDamage(gridEntityId, attackerIdentityId, damage);
        }

        /// <summary>Builds or rebuilds the shared proximity index when the node set has moved.
        ///
        /// <para>Cell size is a speed knob, not a correctness one -- <c>QueryWithin</c> widens its cell
        /// span for any radius, so a query larger than one cell still answers exactly. Sized to the
        /// passive sensing envelope's worst case, which is the largest radius anyone asks for; the
        /// observation scans ask for far less and so visit one cell block.</para></summary>
        private void EnsureNodeIndex()
        {
            if (_world == null) return;
            if (_nodeIndex != null && !_nodeIndex.IsStaleFor(_world.Graph)) return;
            _nodeIndex = NodeProximityIndex.Build(
                _world.Graph, Math.Max(50.0, _tuning.SensorNodePassiveRangeKm * Signature.Max));
        }

        /// <summary>Nodes within <paramref name="radiusKm"/> of a single point, through the shared
        /// index. Appends; the caller owns <paramref name="into"/>.</summary>
        public void NodesWithinKmOf(Vec3 posKm, double radiusKm, List<SpatialNode> into)
        {
            if (_world == null || into == null) return;
            EnsureNodeIndex();
            if (_nodeIndex == null) return;
            _nodeIndex.QueryWithin(_world.Graph, posKm, radiusKm, into);
        }

        /// <summary>Nodes within <paramref name="radiusKm"/> of ANY of <paramref name="pointsKm"/>,
        /// each once. An empty or null list costs nothing, which is the case that matters: this is a
        /// per-frame path and most frames have nobody online.</summary>
        public void NodesWithinKmOfAny(
            IList<Vec3> pointsKm, double radiusKm, List<SpatialNode> into, HashSet<string> scratch)
        {
            if (_world == null || into == null || pointsKm == null || pointsKm.Count == 0) return;
            EnsureNodeIndex();
            if (_nodeIndex == null) return;
            _nodeIndex.QueryWithinAny(_world.Graph, pointsKm, radiusKm, into, scratch);
        }

        // Reused across frames so the per-frame observation path allocates nothing.
        private readonly List<SpatialNode> _observedScratch = new List<SpatialNode>();
        private readonly List<SpatialNode> _keepScratch = new List<SpatialNode>();
        private readonly HashSet<string> _observedSeen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Advances station materialisation for wherever the online players are.
        ///
        /// <para><paramref name="playerPositionsKm"/> replaces the "is any player near this node"
        /// predicate this used to hand down. The predicate meant one walk of the whole graph per
        /// frame whatever the roster was; the positions mean a cell query per player, and none at
        /// all when nobody is online. The vanilla-suppression claim set moved out of the same loop
        /// into <c>RefreshClaimsIfDue</c> -- it is founded-set data that no player can change, and
        /// polling it at 60 Hz was what made the walk unconditional.</para></summary>
        public void UpdateStationObservation(IList<Vec3> playerPositionsKm, double radiusKm)
        {
            if (_world == null) return;
            long _stT0 = Stopwatch.GetTimestamp();
            _stationMaterializer.RefreshClaimsIfDue(_world.Graph, _states, DateTime.UtcNow);
            _observedScratch.Clear();
            NodesWithinKmOfAny(playerPositionsKm, radiusKm, _observedScratch, _observedSeen);
            _keepScratch.Clear();
            NodesWithinKmOfAny(playerPositionsKm, radiusKm + DespawnHysteresisKm, _keepScratch, _observedSeen);
            _stationMaterializer.UpdateObservation(_world.Graph, _observedScratch, _keepScratch,
                Math.Max(0, _tuning.StationDespawnGraceSeconds));
            _observedScratch.Clear();
            _keepScratch.Clear();
            TopUpStandingStations(DateTime.UtcNow);
            // Immediately after: observation has just decided which stations are standing, and that
            // is the one thing a held rebuild is waiting on.
            SweepPendingStationRefits();
            _perfStObs.Record(Stopwatch.GetTimestamp() - _stT0);
        }

        /// <summary>Stopgap grace-period sweep: permanently deletes any UNKN node whose
        /// <see cref="SpatialNode.AbandonedUtc"/> is older than <see cref="FactionTuning.UnknGraceDuration"/>
        /// (default 8h) -- a time-based upper bound on UNKN's territory independent of whether
        /// Claim-based reclaim is keeping pace. Skips (leaves for a later sweep, no timer reset) any
        /// candidate with a player currently nearby, using the same <paramref name="isPlayerNear"/>
        /// delegate <see cref="UpdateStationObservation"/> already uses, so a station a player is
        /// standing near never simply vanishes out from under them. Runs on a coarse cadence (see
        /// <see cref="_unknPruneNextUtc"/>), not every tick, and processes at most
        /// <see cref="MaxUnknRemovalsPerSweep"/> nodes per call -- any remaining eligible candidates
        /// are simply found again by the next sweep (no cursor/state needed, GetExpiredUnknNodeIds is
        /// a fresh scan every time). No-op when no session is loaded.</summary>
        public void PruneExpiredUnknTerritory(Func<Vec3, bool> isPlayerNear)
        {
            if (_world == null) return;
            if (_world.UtcNow < _unknPruneNextUtc) return;
            _unknPruneNextUtc = _world.UtcNow.AddMinutes(UnknPruneIntervalMinutes);

            IList<string> candidates = _world.GetExpiredUnknNodeIds();
            if (candidates.Count == 0) return;

            int removed = 0;
            foreach (string nodeId in candidates)
            {
                if (removed >= MaxUnknRemovalsPerSweep) break;

                SpatialNode node;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) continue;
                if (isPlayerNear(node.Position)) continue; // leave it for a later sweep

                bool founded = node.StableStationId != 0;
                if (founded)
                    _stationMaterializer.PermanentlyRemove(nodeId, node.StableStationId, NeutralFaction.Tag);

                RemovedNodeContents destroyed = _world.RemoveNode(nodeId);
                removed++;
                _log.Write("UNKN_EXPIRED node=" + nodeId + " founded=" + founded
                           + " graceHours=" + _tuning.UnknGraceDuration.TotalHours.ToString("0.0")
                           + " hulls=" + destroyed.Hulls + " items=" + destroyed.ItemUnits
                           + " damaged=" + destroyed.DamagedHulls);
            }

            if (removed > 0) Flush();
            if (candidates.Count > removed)
                _log.Write("UNKN_EXPIRED_DEFERRED " + (candidates.Count - removed) + " candidate(s) left for the next sweep");
        }

        /// <summary>One-time operator backfill wrapper -- see <see cref="WorldModel.BackfillAbandonedUtc"/>.
        /// Folds the pre-existing UNKN backlog (nodes abandoned before <see cref="SpatialNode.AbandonedUtc"/>
        /// existed) into the same grace-expiry mechanism ordinary decay/Withdraw already feed. Bridge
        /// verb `factions backfillabandonedutc &lt;minutesAgo&gt; [apply]`.</summary>
        public int BackfillAbandonedUtc(TimeSpan age, bool applyFix)
        {
            if (_world == null) return 0;
            int matched = _world.BackfillAbandonedUtc(age, applyFix);
            if (applyFix && matched > 0) Flush();
            _log.Write("backfillabandonedutc: age=" + age.TotalMinutes.ToString("0.0") + "min matched=" + matched
                       + (applyFix ? " (applied)" : " (dry run)"));
            return matched;
        }

        /// <summary>
        /// Asteroid-field materialization loop — called from the Torch Update() tick alongside
        /// <see cref="UpdateStationObservation"/>. Spawns physical asteroids near eligible nodes
        /// (OreField/IceField/RareOreField, per tuning) the first time a player gets near them; a
        /// spawn-once lifecycle, unlike stations, since a voxel map carries no live-simulation cost
        /// to justify despawning. No-op when no session is loaded (<c>_world == null</c>).
        /// </summary>
        public void UpdateAsteroidFieldObservation(IList<Vec3> playerPositionsKm, double radiusKm)
        {
            if (_world == null) return;
            long _astT0 = Stopwatch.GetTimestamp();
            _observedScratch.Clear();
            NodesWithinKmOfAny(playerPositionsKm, radiusKm, _observedScratch, _observedSeen);
            _asteroidFieldMaterializer.UpdateObservation(_world.Graph, _observedScratch, _tuning);
            _observedScratch.Clear();
            _perfAstObs.Record(Stopwatch.GetTimestamp() - _astT0);
        }

        /// <summary>The authored features this world's rock comes from, with their anchors resolved
        /// once. Called at session load, after the planet catalogue exists.</summary>
        public void SetWorldFeatures(IEnumerable<WorldFeature> features, IPlanetCatalogue planets)
        {
            // Held as well as handed on: the estate seeder measures a Feature region out of this
            // same resolved list, and taking it from the materializer would make one consumer's
            // internals the other's contract.
            _worldFeatures = features;
            _worldPlanets = planets;
            _featureRockMaterializer.SetFeatures(features, planets);
        }

        /// <summary>
        /// Seed every faction's estate from <c>faction_recipe.json</c>. Dry run by default; the
        /// caller supplies the recipe because the plugin owns input-file resolution.
        /// <para>Refuses as a whole rather than partially placing: an over-subscribed region is a
        /// recipe to fix, and half an estate on disk is worse than none.</para>
        /// <para>MUST be called on the game thread, on a dry run as well as an apply: the orphan
        /// count below reads SE's faction collection.</para>
        /// </summary>
        public string SeedEstate(FactionRecipe recipe, int worldSeed, bool apply)
        {
            if (_world == null) return "world estate: runtime not started (no session loaded).";
            if (recipe == null) return "world estate: no recipe.";

            // The live registry, not the recipe's own tags: the point of the check is to catch a
            // recipe written against a different world's factions.
            var registered = new List<string>();
            if (_states != null)
                foreach (FactionState st in _states)
                    if (st != null && !string.IsNullOrEmpty(st.Tag)) registered.Add(st.Tag);

            // Station records SE still holds that no node points at. Core cannot see them and the
            // graph does not remember them, so a re-roll onto a cleared graph would mint a fresh
            // generation on top of the old one -- FAC-129. Same builder the purge verb plans from,
            // so the count the refusal quotes is the count 'world purge orphans' would remove.
            int orphans = StationPurger.Build(_world.Graph, null, true).Remove.Count;

            EstateSeedResult result = EstateSeeder.Seed(recipe, _world, worldSeed, apply,
                EstateSeeder.GeometryFrom(_worldPlanets, _worldFeatures),
                EstateSeeder.SeparationFrom(_worldPlanets, _worldFeatures, _tuning),
                registered, orphans);

            var sb = new StringBuilder();
            sb.Append("world estate (seed ").Append(worldSeed).Append("): ")
              .Append(apply && !result.IsOverSubscribed ? "APPLIED" : "dry run").AppendLine();
            foreach (string w in result.Warnings) sb.Append("  ! ").AppendLine(w);
            foreach (string l in result.Lines) sb.Append("  ").AppendLine(l);
            sb.Append("  ").Append(result.FactionsSeeded).Append(" factions, ")
              .Append(result.NodesSeeded).Append(" nodes.");
            if (result.MinSpacingKm > 0)
            {
                // Two different sentences, because a breach and a healthy margin are two different
                // facts and one wording for both is how a passing number reads as a failing one.
                if (result.SeparationViolations > 0)
                    sb.AppendLine().Append("  ").Append(result.SeparationViolations)
                      .Append(" node(s) in violation; worst is ")
                      .Append(result.MinSpacingKm.ToString("0.##")).Append(" km apart where ")
                      .Append(result.MinSpacingRequiredKm.ToString("0.##")).Append(" km is required.");
                else
                    sb.AppendLine().Append("  0 nodes in violation; closest pair ")
                      .Append(result.MinSpacingKm.ToString("0.##"))
                      .Append(" km, inside no node's required distance.");
            }

            if (result.IsOverSubscribed)
                return sb.AppendLine().Append("  Nothing placed -- resolve the warning(s) above and re-run.").ToString();

            if (apply)
            {
                // The bootstrap stations. Core cannot do this -- a station is an SE entity -- so the
                // recipe names how many and Core names which nodes; the founding happens here.
                int founded = 0, failed = 0;
                var standing = new HashSet<string>(StringComparer.Ordinal);
                foreach (string nodeId in result.FoundNodeIds)
                {
                    SpatialNode node;
                    if (!_world.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) { failed++; continue; }
                    FactionState st = FindState(node.OwnerFactionTag);
                    if (st == null) { failed++; continue; }
                    if (_stationFounder.FoundStation(nodeId, st, _world.Graph)) { founded++; standing.Add(nodeId); }
                    else failed++;
                }
                if (result.FoundNodeIds.Count > 0)
                {
                    sb.AppendLine().Append("  founded ").Append(founded).Append(" station(s)");
                    if (failed > 0) sb.Append(", ").Append(failed).Append(" FAILED");
                    sb.Append('.');
                }

                // The starting hulls. Core chose which prefab and which node; this withholds any
                // grant whose station did not actually stand up, because a hull at an unfounded node
                // is stranded stock by construction -- nothing dispatches from a node with no
                // station, so it would sit there permanently.
                int hulls = 0, withheld = 0;
                foreach (ShipGrant g in result.ShipGrants)
                {
                    SpatialNode node;
                    if (g.NodeId == null || !standing.Contains(g.NodeId)
                        || !_world.Graph.Nodes.TryGetValue(g.NodeId, out node) || node == null)
                    { withheld++; continue; }
                    ShipPool.Return(node, g.PrefabId, g.Count);
                    hulls += g.Count;
                }
                if (result.ShipGrants.Count > 0)
                {
                    sb.AppendLine().Append("  granted ").Append(hulls).Append(" hull(s)");
                    if (withheld > 0)
                        sb.Append(", ").Append(withheld).Append(" WITHHELD (station did not found)");
                    sb.Append('.');
                }

                // Seeded nodes live only in the graph until written; the same reason PurgeStations
                // flushes. Without this a restart before the next autosave loses the whole estate.
                // Founding sets StableStationId on the node, so it must precede the flush.
                Flush();
                sb.AppendLine().Append("  Flushed to disk.");
            }
            else sb.AppendLine().Append("  Re-run with 'apply' to commit.");

            return sb.ToString();
        }

        /// <summary>
        /// Bridge verb <c>factions world relations [apply]</c> — write the authored diplomacy from
        /// <c>faction_recipe.json</c> into SE's faction relation table. Dry run by default, and the
        /// dry run is the point: it prints the live reputation beside the authored one for every
        /// pair, which is the read that did not exist when FAC-108 was filed.
        ///
        /// <para><b>Why this replaces SE's own generator rather than re-running it.</b>
        /// <c>MyFactionRelationGenerator</c> runs once, behind the same one-shot flag that mints the
        /// factions and their stations, and it derives reputation from station distances that this
        /// world then purged and re-placed. Re-running it after founding is possible, but measured
        /// against this estate it changes 25 of 55 pairs and flips 14 across a band while deriving
        /// them from degenerate inputs — see <see cref="RelationPlanner"/>. The authored file is the
        /// world's statement instead.</para>
        ///
        /// <para>Writes both directions, as the generator does. SE keys the table on an unordered
        /// pair, so the second write is a no-op; doing it anyway costs nothing and does not depend on
        /// that staying true. MUST be called on the game thread.</para>
        /// </summary>
        public string ApplyRelations(FactionRecipe recipe, bool apply)
        {
            if (_world == null) return "world relations: runtime not started (no session loaded).";
            if (recipe == null) return "world relations: no recipe.";

            Sandbox.Game.World.MySession session = Sandbox.Game.World.MySession.Static;
            if (session == null || session.Factions == null)
                return "world relations: no session loaded.";

            ReputationScale scale = ReadReputationScale(session);
            if (scale == null)
                return "world relations: this world has no economy session component, so it carries "
                     + "no reputation scale to author against.";

            // The live registry, not the recipe's own tags -- same check EstateSeeder makes, for the
            // same reason: a recipe written against another world's factions.
            var registered = new List<string>();
            if (_states != null)
                foreach (FactionState st in _states)
                    if (st != null && !string.IsNullOrEmpty(st.Tag)) registered.Add(st.Tag);

            RelationPlan plan = RelationPlanner.Build(recipe, registered, scale);

            var sb = new StringBuilder();
            sb.Append("world relations: ")
              .Append(apply && !plan.IsRefused ? "APPLIED" : "dry run").AppendLine();
            foreach (string w in plan.Warnings) sb.Append("  ! ").AppendLine(w);

            if (plan.IsRefused)
                return sb.Append("  Nothing written -- fix the recipe and re-run.").ToString();

            int changed = 0, unchanged = 0, unresolved = 0;
            var lines = new List<string>();
            foreach (RelationPlanEntry e in plan.Entries)
            {
                Sandbox.Game.World.MyFaction fa = session.Factions.TryGetFactionByTag(e.TagA);
                Sandbox.Game.World.MyFaction fb = session.Factions.TryGetFactionByTag(e.TagB);
                if (fa == null || fb == null)
                {
                    // The registry knows a tag SE does not. Reported per pair rather than refused:
                    // the rest of the table is still authorable and saying which pair was skipped is
                    // more use than refusing all of it.
                    unresolved++;
                    lines.Add("? " + e.TagA + "-" + e.TagB + " skipped: "
                            + (fa == null ? e.TagA : e.TagB) + " is not an SE faction.");
                    continue;
                }

                Tuple<VRage.Game.MyRelationsBetweenFactions, int> current =
                    session.Factions.GetRelationBetweenFactions(fa.FactionId, fb.FactionId);
                int before = current != null ? current.Item2 : scale.NeutralMid;

                if (before == e.Reputation) unchanged++;
                else
                {
                    changed++;
                    lines.Add(e.TagA + "-" + e.TagB + "  " + before + " (" + scale.BandOf(before) + ")"
                            + " -> " + e.Reputation + " (" + scale.BandOf(e.Reputation) + ")"
                            + "  [" + e.Source + "]");
                }

                if (!apply) continue;

                // Written whether or not it differs, and that is the point. GetRelationBetweenFactions
                // answers for a pair with NO row by falling back to
                // MyPerGameSettings.DefaultFactionRelationshipAndReputation (Enemies / HostileMid), so
                // skipping the equal ones leaves an authored pair holding the right value for the
                // wrong reason -- a static global that happens to agree, not the file. Measured live
                // 2026-08-26: the three pairs among SE's default factions (FCTM/SPRT/UNKN) came back
                // from a save with no row at all, correct only by coincidence.
                session.Factions.SetReputationBetweenFactions(fa.FactionId, fb.FactionId, e.Reputation);
                session.Factions.SetReputationBetweenFactions(fb.FactionId, fa.FactionId, e.Reputation);
            }

            lines.Sort(StringComparer.Ordinal);
            foreach (string l in lines) sb.Append("  ").AppendLine(l);

            sb.Append("  ").Append(plan.Entries.Count).Append(" pair(s): ")
              .Append(changed).Append(" changed, ").Append(unchanged).Append(" already correct");
            if (unresolved > 0) sb.Append(", ").Append(unresolved).Append(" SKIPPED");
            sb.Append('.');

            if (!apply)
                sb.AppendLine().Append("  Re-run with 'apply' to commit.");
            else
                // Relations live in the SE checkpoint, not in any Factions file, so there is nothing
                // of ours to flush -- they reach disk on the next world save.
                sb.AppendLine().Append("  Written to the session; they persist on the next world save.");

            return sb.ToString();
        }

        /// <summary>The running world's reputation marks, or null when no session is loaded or the
        /// world carries no economy component. Callers that convert between SE reputation and the
        /// sim's -1..1 relation scale must go through this rather than assuming vanilla's numbers.</summary>
        public ReputationScale CurrentReputationScale()
        {
            Sandbox.Game.World.MySession session = Sandbox.Game.World.MySession.Static;
            return session == null ? null : ReadReputationScale(session);
        }

        /// <summary>The running world's reputation marks, read off the economy session component so
        /// Core plans against the numbers this world actually uses rather than a second copy of
        /// vanilla's. Null when the world carries no economy component at all.</summary>
        private static ReputationScale ReadReputationScale(Sandbox.Game.World.MySession session)
        {
            Sandbox.Game.SessionComponents.MySessionComponentEconomy economy =
                session.GetComponent<Sandbox.Game.SessionComponents.MySessionComponentEconomy>();
            if (economy == null || economy.EconomyDefinition == null) return null;

            VRage.Game.Definitions.SessionComponents.MySessionComponentEconomyDefinition d =
                economy.EconomyDefinition;
            return new ReputationScale
            {
                HostileMin  = d.ReputationHostileMin,
                HostileMid  = d.ReputationHostileMid,
                NeutralMin  = d.ReputationNeutralMin,
                NeutralMid  = d.ReputationNeutralMid,
                FriendlyMin = d.ReputationFriendlyMin,
                FriendlyMid = d.ReputationFriendlyMid,
                FriendlyMax = d.ReputationFriendlyMax,
            };
        }

        /// <summary>
        /// Feature-rock materialization — called from the Torch Update() tick alongside
        /// <see cref="UpdateAsteroidFieldObservation"/>. Unlike that one, nodes are not the trigger:
        /// rock exists where a feature says so and player presence alone decides when it is built,
        /// with a founded station's bubble the only thing that suppresses an object.
        /// No-op when no session is loaded (<c>_world == null</c>).
        /// </summary>
        public void UpdateFeatureRockObservation(IList<Vec3> playerPositionsKm)
        {
            if (_world == null) return;
            long t0 = Stopwatch.GetTimestamp();
            _featureRockMaterializer.UpdateObservation(playerPositionsKm, _world.Graph, _tuning);
            _perfFeatureRock.Record(Stopwatch.GetTimestamp() - t0);
        }

        /// <summary>Closes every feature rock so the next approach rebuilds it. See
        /// <see cref="FeatureRockMaterializer.PurgeAll"/>.</summary>
        public string PurgeFeatureRock(bool includeEdited)
        {
            return _featureRockMaterializer.PurgeAll(includeEdited);
        }

        /// <summary>Reads back what a rock is actually made of. See
        /// <see cref="FeatureRockMaterializer.Inspect"/>.</summary>
        public string InspectFeatureRock(IList<Vec3> playerPositionsKm, string storageNameOrNull)
        {
            return _featureRockMaterializer.Inspect(playerPositionsKm, storageNameOrNull);
        }

        /// <summary>Live feature-rock counts, for telemetry.</summary>
        public void FeatureRockCounts(out int cells, out int objects)
        {
            cells = _featureRockMaterializer.LiveCells;
            objects = _featureRockMaterializer.LiveObjects;
        }

        /// <summary>
        /// Samples the ground under mineable nodes that arrived without a deposit — from a scan, a
        /// migration, or a brain file written before the deposit model existed. Called from the Torch
        /// Update() tick.
        ///
        /// <para>Without this, only nodes a faction creates by expanding ever get a deposit, so a
        /// freshly scanned world mints the node-kind table's global ore mix everywhere regardless of
        /// what lies under it — FAC-068's defect, left in place for exactly the estate a new world
        /// starts with. See FAC-083.</para>
        ///
        /// <para>Paced at <see cref="FactionTuning.DepositBackfillPerTick"/>, and the pass
        /// <b>retires itself</b>: once the graph holds no candidates the per-frame cost is one
        /// boolean rather than a graph walk forever. Nothing is missed by latching — a node created
        /// later reaches <c>ResolveDeposit</c> through <c>ExpandFrom</c> directly.</para>
        ///
        /// <para><b>Must run after <c>ReclassifyNodes</c></b>, which is what stamps <c>Zone</c> and
        /// <c>PlanetId</c>; sampling before that reads an unclassified position. The Update() tick is
        /// comfortably after <c>Load</c>.</para>
        /// </summary>
        public void UpdateDepositBackfill()
        {
            // Backfill resolves a missing deposit and refreshes ResourceValue just like the paced
            // survey below. It is another out-of-band writer to Production's guarded node snapshot,
            // so preserve the same no-writer window while a staged faction cycle is live.
            if (_world == null || _depositBackfillDone || _productionManager.IsCycleInProgress) return;
            int budget = Math.Max(0, _tuning.DepositBackfillPerTick);
            if (budget == 0) return;

            for (int i = 0; i < budget; i++)
            {
                string nodeId;
                DepositResolution result;
                if (!_world.TryBackfillOneDeposit(_depositBackfillAttempted, out nodeId, out result))
                {
                    _depositBackfillDone = true;
                    _log.Write("DEPOSIT_BACKFILL_COMPLETE sampled=" + _depositBackfillSampled
                               + " barren=" + _depositBackfillBarren
                               + " declined=" + _depositBackfillDeclined);
                    return;
                }

                if (result == DepositResolution.Sampled) _depositBackfillSampled++;
                else if (result == DepositResolution.Barren) _depositBackfillBarren++;
                else if (result == DepositResolution.Declined) _depositBackfillDeclined++;
            }
        }

        /// <summary>
        /// Refines one node's deposit composition, a few probes at a time. Called from the Torch
        /// Update() tick after the first-pass backfill.
        ///
        /// <para>A single sample establishes <b>what is there</b> cheaply; establishing <b>how much of
        /// each</b> needs thousands of probes, which is affordable spread over minutes and is not
        /// affordable in one tick. So composition is surveyed rather than sampled: presence never
        /// waits, and the mix sharpens while a faction holds the ground. See FAC-101.</para>
        ///
        /// <para>Round-robin by cursor rather than always-least-surveyed, so one stubborn node cannot
        /// monopolise the budget and every node advances. A node leaves the rotation when its
        /// composition is certain enough for <see cref="FactionTuning.SurveyTargetShare"/> — the
        /// survey stops rather than trickling forever, because precision improves as the square root
        /// of the sample and the last tenth costs more than the first nine.</para>
        /// </summary>
        public void UpdateDepositSurvey()
        {
            // Surveying rewrites SpatialNode.ResourceValue after every probe. Production captures
            // that exact field in its staged-cycle commit guard, so allowing this out-of-band writer
            // to run between bounded Production steps predictably rolls the cycle back. Preserve the
            // guard instead of weakening it: the round-robin survey simply resumes after the short
            // Production cycle completes, without resetting its cursor or losing progress.
            if (_world == null || _productionManager.IsCycleInProgress) return;

            var nodes = _world.Graph.Nodes;
            if (nodes.Count == 0) return;

            // One candidate per call: the budget is probes, not nodes, and spreading it thinner would
            // pay the per-node setup cost repeatedly for no extra coverage.
            var keys = _surveyCursorKeys;
            if (keys == null || _surveyCursor >= keys.Count || _surveyPassComplete)
            {
                // Rebuilt at the top of every pass, not once: a node created after the snapshot --
                // every Expand, every seeded estate -- is otherwise invisible to the survey until the
                // process restarts.
                keys = new List<string>(nodes.Keys);
                _surveyCursorKeys = keys;
                _surveyCursor = 0;
                _surveyPassComplete = false;
                _surveyCandidates = CountSurveyCandidates(nodes);
                LogSurveyPass();
                if (keys.Count == 0) return;
            }

            int budget = SurveyMath.ProbesPerVisit(_tuning.SurveyTargetShare, _surveyCandidates,
                _tuning.SurveyDurationHours, HostFramesPerSecond, _tuning.SurveyMaxProbesPerTick);
            if (budget <= 0) return;

            for (int scanned = 0; scanned < keys.Count; scanned++)
            {
                string id = keys[_surveyCursor];
                // Recorded where the wrap happens rather than inferred from the resting cursor, which
                // a mid-scan wrap steps straight over.
                int next = _surveyCursor + 1;
                if (next >= keys.Count) { next = 0; _surveyPassComplete = true; }
                _surveyCursor = next;

                SpatialNode n;
                if (!nodes.TryGetValue(id, out n)) continue;
                if (!_world.NeedsSurvey(n)) continue;
                if (IsSurveyParked(id)) continue;

                if (_world.TrySurveyOnce(n, budget))
                {
                    _surveyProbesSpent += budget;
                    _surveyDeclines.Remove(id);
                    return;
                }

                // The seam had nothing to say about this position. Keep scanning rather than
                // returning: the frame is already spent, and handing it to a node that can use it is
                // the difference between a survey that paces and one that spends its whole budget on
                // ground no reader covers.
                NoteSurveyDecline(id);
            }
        }

        /// <summary>
        /// Re-measures deposits stored in older units, one node per frame at
        /// <see cref="FactionTuning.SurveyMaxProbesPerTick"/>. Called from the Torch Update() tick.
        ///
        /// <para>The paced survey cannot do this: it advances a node by a few dozen probes a visit so a
        /// full survey takes days, and a stale deposit must not be added to at all. So a stale node
        /// leaves that rotation (<see cref="WorldModel.NeedsSurvey"/>) and is re-walked here at the
        /// per-frame ceiling, its old reading live until the new one covers the same ground. An
        /// estate of a thousand surveyed nodes takes about twenty minutes. See
        /// <see cref="WorldModel.TryResurveyStep"/>.</para>
        ///
        /// <para>Retires itself once a full pass finds nothing stale, like the backfill. A node the
        /// seam keeps refusing is parked after <see cref="FactionTuning.SurveyDeclinesBeforeParking"/>
        /// and keeps its old reading.</para>
        /// </summary>
        public void UpdateDepositResurvey()
        {
            if (_world == null || _resurveyDone || _productionManager.IsCycleInProgress) return;

            var nodes = _world.Graph.Nodes;
            if (_resurveyKeys == null || _resurveyCursor >= _resurveyKeys.Count)
            {
                var stale = new List<string>();
                foreach (var kv in nodes)
                    if (WorldModel.NeedsResurvey(kv.Value) && !IsResurveyParked(kv.Key)) stale.Add(kv.Key);
                if (stale.Count == 0)
                {
                    _resurveyDone = true;
                    if (_resurveyStale > 0)
                        _log.Write("DEPOSIT_RESURVEY_COMPLETE replaced=" + _resurveyReplaced
                                   + " parked=" + _resurveyDeclines.Count);
                    return;
                }
                if (_resurveyKeys == null)
                {
                    _resurveyStale = stale.Count;
                    _log.Write("DEPOSIT_RESURVEY_START stale=" + stale.Count
                               + " units=" + NodeDeposit.CurrentUnits
                               + " probesPerStep=" + _tuning.SurveyMaxProbesPerTick);
                }
                _resurveyKeys = stale;
                _resurveyCursor = 0;
            }

            int budget = Math.Max(1, _tuning.SurveyMaxProbesPerTick);
            while (_resurveyCursor < _resurveyKeys.Count)
            {
                string id = _resurveyKeys[_resurveyCursor++];
                SpatialNode n;
                if (!nodes.TryGetValue(id, out n) || !WorldModel.NeedsResurvey(n)) continue;
                if (IsResurveyParked(id)) continue;

                if (_world.TryResurveyStep(n, budget))
                {
                    _resurveyDeclines.Remove(id);
                    if (!WorldModel.NeedsResurvey(n)) _resurveyReplaced++;
                    return;
                }
                int declines;
                _resurveyDeclines[id] = _resurveyDeclines.TryGetValue(id, out declines) ? declines + 1 : 1;
            }
        }

        private bool IsResurveyParked(string id)
        {
            int declines;
            return _resurveyDeclines.TryGetValue(id, out declines)
                && declines >= Math.Max(1, _tuning.SurveyDeclinesBeforeParking);
        }

        /// <summary>Frames the host calls <c>Update()</c> per second, which is what turns a survey
        /// duration into a per-visit probe budget. Fixed by the SE server tick, not by us.</summary>
        private const double HostFramesPerSecond = 60.0;

        /// <summary>Nodes the next pass will actually spend probes on: those still needing survey,
        /// less those parked. Parked nodes are excluded because they are the denominator's whole
        /// problem — counting ground no reader covers stretches the derived budget over work that
        /// will never happen, and every other node's survey slows to pay for it.</summary>
        private int CountSurveyCandidates(Dictionary<string, SpatialNode> nodes)
        {
            int count = 0;
            foreach (var kv in nodes)
                if (_world.NeedsSurvey(kv.Value) && !IsSurveyParked(kv.Key)) count++;
            return count;
        }

        private bool IsSurveyParked(string id)
        {
            int declines;
            return _surveyDeclines.TryGetValue(id, out declines)
                && declines >= Math.Max(1, _tuning.SurveyDeclinesBeforeParking);
        }

        private void LogSurveyPass()
        {
            _surveyPasses++;
            if (!DebugSurvey) return;
            _log.Write("SURVEY_PASS n=" + _surveyPasses
                       + " candidates=" + _surveyCandidates
                       + " parked=" + _surveyParked
                       + " probesPerVisit=" + SurveyMath.ProbesPerVisit(_tuning.SurveyTargetShare,
                             _surveyCandidates, _tuning.SurveyDurationHours, HostFramesPerSecond,
                             _tuning.SurveyMaxProbesPerTick)
                       + " targetHours=" + _tuning.SurveyDurationHours.ToString("0.##")
                       + " probesSpent=" + _surveyProbesSpent);
        }

        private void NoteSurveyDecline(string id)
        {
            int declines;
            declines = _surveyDeclines.TryGetValue(id, out declines) ? declines + 1 : 1;
            _surveyDeclines[id] = declines;
            if (declines == Math.Max(1, _tuning.SurveyDeclinesBeforeParking)) _surveyParked++;
        }

        /// <summary>Admin sweep ('factions fixstationdrift', bridge) — see
        /// <see cref="StationMaterializer.FixNodeStationDrift"/>. Pure SE-side position fix: no
        /// Flush() needed, node.Position (the persisted brain state) is the source of truth here and
        /// never changes.</summary>
        public StationDriftFixResult FixStationPositionDrift(bool applyFix, string onlyNodeId = null)
        {
            if (_world == null) return new StationDriftFixResult { Details = new System.Collections.Generic.List<string>() };
            StationDriftFixResult result = _stationMaterializer.FixNodeStationDrift(
                _world.Graph, applyFix, n => _world.MaxPlausibleSnapDistanceKm(n.Position) * 1000.0, onlyNodeId);
            if (result.Drifted > 0)
            {
                _log.Write("fixStationDrift: checked=" + result.Checked + " drifted=" + result.Drifted
                           + " skipped=" + result.Skipped + " worst=" + Math.Round(result.WorstDriftM) + "m"
                           + (applyFix ? " fixed=" + result.Fixed : " (dry run)"));
            }
            return result;
        }

        /// <summary>Admin escape hatch ('factions reclaim', chat and bridge) — see <see cref="StationMaterializer.ForceRespawn"/>.</summary>
        /// <summary>
        /// Bridge verb <c>factions world purge [apply]</c> — strip every SE station record from the
        /// world while leaving the faction that owns it in place. Dry-run by default.
        ///
        /// SE mints factions, their stations and their relations behind a single one-shot flag
        /// (<c>MySessionComponentEconomy.BeforeStart</c>), so a world cannot keep the factions
        /// without also inheriting the station layout they were generated with. This is the step
        /// that lets the estate stop being SE's and start being ours; re-placing it afterwards is a
        /// separate act, and relations must be regenerated after that because SE derives them from
        /// station distances.
        ///
        /// Nodes that referenced a purged station survive as un-founded nodes — see
        /// <see cref="StationPurger"/>. MUST be called on the game thread.
        /// </summary>
        public string PurgeStations(bool apply, string tagFilter, bool orphansOnly = false)
        {
            if (_world == null) return "world purge: runtime not started (no session loaded).";

            StationPurger.Plan plan = StationPurger.Build(_world.Graph, tagFilter, orphansOnly);
            if (!apply) return StationPurger.Describe(plan, false, 0, 0, 0);

            int removed, grids, zones;
            StationPurger.Apply(plan, _world.Graph, _stationMaterializer, out removed, out grids, out zones);

            // The cleared node fields live in the graph until a flush writes them; without this a
            // restart before the next automatic flush restores every node's StableStationId while
            // the station it names is already gone.
            Flush();

            string msg = StationPurger.Describe(plan, true, removed, grids, zones);
            _log.Write("world purge" + (orphansOnly ? " [orphans]" : "") + ": removed " + removed
                       + " station record(s), closed " + grids + " grid(s) and " + zones
                       + " safe zone(s); " + plan.NodeBacked + " node(s) reverted to un-founded; "
                       + plan.SkippedNodeBacked + " node-backed station(s) left untouched.");
            return msg;
        }

        /// <summary>Removes the SE station records a faction is about to stop owning through
        /// <c>factions forget</c>: the record of every node <paramref name="tag"/> holds, wherever it
        /// is filed, and every record filed under <paramref name="tag"/> that no node names. A record
        /// filed under <paramref name="tag"/> that another faction's node names is that node's and is
        /// left alone. The nodes themselves go with the brain, so they are not edited here. Returns
        /// the number of records removed. MUST be called on the game thread.</summary>
        public int PurgeStationRecordsOf(string tag)
        {
            if (_world == null || string.IsNullOrEmpty(tag)) return 0;

            var nodeByStation = new Dictionary<long, string>();
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
                if (node != null && node.StableStationId != 0
                    && string.Equals(node.OwnerFactionTag, tag, StringComparison.OrdinalIgnoreCase))
                    nodeByStation[node.StableStationId] = node.Id;
            foreach (StationPurger.StationPlan sp in StationPurger.Build(_world.Graph, tag, true).Remove)
                if (!nodeByStation.ContainsKey(sp.StationId)) nodeByStation[sp.StationId] = null;

            int removed = 0;
            foreach (KeyValuePair<long, string> kv in nodeByStation)
            {
                string heldBy;
                removed += _stationMaterializer.PurgeStationRecordAnywhere(kv.Key, tag, kv.Value, out heldBy);
            }
            return removed;
        }

        public string ForceStationRespawn(string factionTag, string nodeId)
        {
            if (_world == null) return "reclaim: no session loaded.";
            return _stationMaterializer.ForceRespawn(_world.Graph, factionTag, nodeId);
        }

        // Stable key across reboots, identifying one fleet event. Every component is persisted.
        // DepartedUtc alone is not enough: it is a per-tick snapshot (WorldModel.UtcNow), so every
        // fleet a faction dispatches in the same tick shares it, and keying on it made unrelated
        // fleets each other's "siblings" and each other's in-flight spawn.
        private readonly Dictionary<string, DateTime> _ammoCheckedUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly TimeSpan AmmoCheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>Breaks off a materialised fleet holding station whose live grids have fired
        /// everything they carried (<see cref="PendingFleetResolver.BeginRetreat"/>). Read at most
        /// every <see cref="AmmoCheckInterval"/> per fleet: it walks the grids' blocks. Only a fleet
        /// armed from node stock is judged — one that was never armed fires its bands without
        /// limit. Returns true when the fleet was sent home, having written it back.</summary>
        private bool TryRetreatOutOfAmmunition(FactionState st, int fi, ref PendingFleetAction fleet, DateTime now)
        {
            if (!fleet.MunitionsArmed || fleet.RetreatReason != null) return false;
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null) return false;
            string id = MunitionsId(fleet);
            DateTime last;
            if (_ammoCheckedUtc.TryGetValue(id, out last) && now - last < AmmoCheckInterval) return false;
            _ammoCheckedUtc[id] = now;
            if (!vanilla.IsOutOfMunitions(id)) return false;
            if (!PendingFleetResolver.BeginRetreat(ref fleet, now, _tuning, "out_of_ammo")) return false;

            // What it fired live is spent; the record says so before the fleet leaves.
            long spent = FleetMunitions.ApplyObserved(ref fleet, new Dictionary<string, long>(), _world);
            st.PendingFleets[fi] = fleet;
            _ammoCheckedUtc.Remove(id);
            _log.Write("FLEET_RETREATING " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                       + " eid=" + fleet.MaterialisedEntityId + " @" + (fleet.TargetNodeId ?? "?")
                       + " reason=out_of_ammo spent=" + spent.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>The id a fleet's munitions are staged and read under. Stable for the fleet's
        /// life, unlike <see cref="FleetKey"/>, which moves with every leg.</summary>
        private static string MunitionsId(PendingFleetAction fleet)
            => fleet.ActorTag + ":" + (fleet.FleetId ?? FleetKey(fleet));

        private readonly HashSet<string> _munitionIdScratch = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Folds what despawned grids were read to hold back into each armed fleet that is
        /// no longer in the world, and drops readings for fleets that are gone. A fleet still
        /// materialised keeps accumulating: a reading of one dropped escort is not what the whole
        /// fleet holds.</summary>
        private void ApplyMunitionReadings()
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null) return;
            _munitionIdScratch.Clear();
            foreach (FactionState state in _states)
            {
                for (int fi = 0; fi < state.PendingFleets.Count; fi++)
                {
                    PendingFleetAction fleet = state.PendingFleets[fi];
                    if (!fleet.MunitionsArmed || fleet.Munitions == null) continue;
                    _munitionIdScratch.Add(MunitionsId(fleet));
                    if (fleet.MaterialisedEntityId > 0 || fleet.MaterialisedEntityId == PendingFleetAction.SpawningId) continue;
                    ApplyMunitionReading(vanilla, fleet);
                }
            }
            vanilla.RetainMunitions(_munitionIdScratch);
        }

        /// <summary>Reads, before Core resolves them, the grids of every materialised fleet whose
        /// fight or hold this pass can end: a dwell that is over, a Guard whose duty window is, and
        /// every fleet standing at a node where such a dwell is. Their hull integrity and the damage
        /// they dealt and took go into the fleet, so a resolution decided by the grids reads them as
        /// they stand and a fleet Core retires takes its damage with it. One read per fleet per fight,
        /// not per pass: a fleet still mid-dwell is not read here.</summary>
        private void SettleResolvingHulls()
        {
            if (!(_spawner is VanillaFleetSpawner)) return;
            DateTime now = _world.UtcNow;
            _resolvingNodes.Clear();
            // Every node where a dwell ends, materialised or not: a duel there can damage a defender
            // standing in the world, and it must start from what that grid really carries.
            foreach (FactionState st in _states)
                foreach (PendingFleetAction f in st.PendingFleets)
                    if (EndsThisPass(f, now) && f.TargetNodeId != null)
                        _resolvingNodes.Add(f.TargetNodeId);
            foreach (FactionState st in _states)
            {
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.MaterialisedEntityId <= 0) continue;
                    bool ends = EndsThisPass(f, now);
                    bool standsInIt = f.TargetNodeId != null && _resolvingNodes.Contains(f.TargetNodeId)
                                      && f.StandsAsDefender(now, _tuning);
                    if (!ends && !standsInIt) continue;
                    st.PendingFleets[fi] = SettleHulls(f, f.MaterialisedEntityId, "resolve");
                }
            }
        }

        private readonly HashSet<string> _resolvingNodes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Where a player respawning at <paramref name="node"/> should be set down
        /// (<see cref="StationMaterializer.TryGetArrivalFrame"/>).</summary>
        internal bool TryGetStationArrivalFrame(SpatialNode node, out VRageMath.Vector3D stationPos,
            out VRageMath.Vector3D stationUp, out float clearanceM, out string error)
        {
            return _stationMaterializer.TryGetArrivalFrame(node, out stationPos, out stationUp, out clearanceM, out error);
        }

        /// <summary><see cref="WorldModel.StationWeaponReading"/>: the working weapons on a node's
        /// materialised station grid, or null when it has none in the world. Read only when a fight
        /// the grids decided resolves at that node.</summary>
        private double? StationWeaponReading(string nodeId)
        {
            long eid = _stationMaterializer.StationGridEntityIdFor(nodeId);
            if (eid <= 0) return null;
            var grid = Sandbox.Game.Entities.MyEntities.GetEntityById(eid) as Sandbox.Game.Entities.MyCubeGrid;
            if (grid == null || grid.Closed || grid.MarkedForClose) return null;
            return FleetFlightController.WorkingWeaponDps(grid);
        }

        /// <summary>Each materialised fleet's hulls as the arrivals pass found them, by fleet id:
        /// integrity (null for whole) and slot count.</summary>
        private readonly Dictionary<string, KeyValuePair<List<double>, int>> _liveHullsBefore =
            new Dictionary<string, KeyValuePair<List<double>, int>>(StringComparer.Ordinal);

        private void SnapshotLiveHulls()
        {
            _liveHullsBefore.Clear();
            if (!(_spawner is VanillaFleetSpawner)) return;
            foreach (FactionState st in _states)
                foreach (PendingFleetAction f in st.PendingFleets)
                    if (f.MaterialisedEntityId > 0 && !string.IsNullOrEmpty(f.FleetId))
                        _liveHullsBefore[f.FleetId] = new KeyValuePair<List<double>, int>(
                            f.HullIntegrity != null ? new List<double>(f.HullIntegrity) : null, f.HullSlotCount);
        }

        /// <summary>After the arrivals pass: a fleet still in the world whose hulls an abstract fight
        /// damaged has that damage put into its grids' blocks, as a hull that spawns damaged does
        /// (<see cref="VanillaFleetSpawner.WatchIntegrity"/>), and its integrity watch restarted there.
        /// Otherwise the next reading of the grid, still whole, would overwrite the damage. One
        /// <c>HULL_DAMAGE_APPLIED</c> line per fleet.</summary>
        private void ApplyDuelDamageToLiveGrids(List<string> buf)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null || _liveHullsBefore.Count == 0) return;
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    KeyValuePair<List<double>, int> before;
                    if (f.MaterialisedEntityId <= 0 || f.FleetId == null
                        || !_liveHullsBefore.TryGetValue(f.FleetId, out before)) continue;
                    if (!HullDamage.Differs(before.Key, before.Value, f)) continue;
                    CollectHullEids(f, f.MaterialisedEntityId);
                    int[] slots = MapHullSlots(f, _hullEids);
                    int grids = 0, blocks = 0;
                    for (int k = 0; k < _hullEids.Count; k++)
                    {
                        int damaged = vanilla.WatchIntegrity(_hullEids[k], slots[k] >= 0 ? f.IntegrityOf(slots[k]) : 1.0);
                        if (damaged < 0) continue;
                        grids++;
                        blocks += damaged;
                    }
                    if (grids > 0)
                        buf.Add("HULL_DAMAGE_APPLIED " + f.LogId + " " + f.ActorTag + " hulls=" + HullDamage.Describe(f)
                                + " grids=" + grids + " blocks_damaged=" + blocks);
                }
            }
            _liveHullsBefore.Clear();
        }

        /// <summary>A materialised fleet whose leg or duty window this arrivals pass closes: the only
        /// ones Core resolves while their grids are still in the world.</summary>
        private bool EndsThisPass(PendingFleetAction f, DateTime now)
        {
            bool dwelling = f.CurrentStopKind == FleetStopKind.Engage || f.CurrentStopKind == FleetStopKind.Intercept;
            if (dwelling && f.HasArrived(now) && !f.Resolved) return true;
            return f.Kind == ActionKind.Guard && f.Resolved && now >= f.EstimatedArrivalUtc.Add(_tuning.GuardDuration);
        }

        /// <summary>Applies one fleet's pending reading. The fleet's munitions map is shared by every
        /// copy of the struct, so the stored fleet sees the change without a write-back.</summary>
        private void ApplyMunitionReading(VanillaFleetSpawner vanilla, PendingFleetAction fleet)
        {
            Dictionary<string, long> observed;
            if (!vanilla.TryTakeObservedMunitions(MunitionsId(fleet), out observed)) return;
            long spent = FleetMunitions.ApplyObserved(ref fleet, observed, _world);
            if (DebugTraces.Munitions)
            {
                long left = 0;
                foreach (KeyValuePair<string, long> held in fleet.Munitions) left += held.Value;
                _log.Write("MUNITIONS_OBSERVED " + fleet.LogId + " " + fleet.ActorTag
                           + " spent=" + spent.ToString(CultureInfo.InvariantCulture)
                           + " left=" + left.ToString(CultureInfo.InvariantCulture));
            }
        }

        private struct FleetKeyEntry
        {
            public long Ticks;
            public ActionKind Kind;
            public string Target;
            public string Key;
        }

        /// <summary>Built keys by actor tag, then fleet id. A spawning fleet's key is asked for every frame while it
        /// waits its spawn turn; building it fresh each time was a measurable share of fleet observation's
        /// allocation (2026-10-06). Game thread only; cleared wholesale when it grows past a bound.</summary>
        private static readonly Dictionary<string, Dictionary<string, FleetKeyEntry>> FleetKeys =
            new Dictionary<string, Dictionary<string, FleetKeyEntry>>(StringComparer.Ordinal);
        private const int FleetKeyCacheBound = 8192;
        private static int _fleetKeyCount;

        private static string FleetKey(PendingFleetAction fleet)
        {
            string tag = fleet.ActorTag ?? "";
            string id = fleet.FleetId ?? "";
            string target = fleet.TargetNodeId ?? fleet.TargetTag ?? "?";
            Dictionary<string, FleetKeyEntry> byId;
            if (!FleetKeys.TryGetValue(tag, out byId))
            {
                byId = new Dictionary<string, FleetKeyEntry>(StringComparer.Ordinal);
                FleetKeys[tag] = byId;
            }
            FleetKeyEntry e;
            if (byId.TryGetValue(id, out e) && e.Ticks == fleet.DepartedUtc.Ticks && e.Kind == fleet.Kind
                && string.Equals(e.Target, target, StringComparison.Ordinal))
                return e.Key;
            string key = fleet.ActorTag + "_" + fleet.DepartedUtc.Ticks.ToString()
                         + "_" + fleet.Kind
                         + "_" + target
                         + "_" + id;
            if (++_fleetKeyCount > FleetKeyCacheBound) { FleetKeys.Clear(); _fleetKeyCount = 0; FleetKeys[tag] = byId; byId.Clear(); }
            byId[id] = new FleetKeyEntry { Ticks = fleet.DepartedUtc.Ticks, Kind = fleet.Kind, Target = target, Key = key };
            return key;
        }

        /// <summary>
        /// After a physical grid is force-failed (stall/destroy/capture), dismiss every other
        /// PendingFleetAction in the same faction that shares its FleetKey. The key carries the
        /// FleetId, which is minted unique within a faction's roster at dispatch and re-minted on
        /// load where a brain repeats one (PendingFleetAction.MintId / RemintDuplicateIds), so the
        /// fleets of one dispatch tick -- a consolidated haul's convoys to one pickup among them --
        /// never match here. What can still match is an entry that is a copy of the failed one.
        /// A dismissed entry closes whatever grid it holds and resolves through
        /// PendingFleetResolver.RecallToOrigin: its hull and cargo go home rather than being
        /// written off, since an entry that reached the roster drew both at dispatch.
        /// </summary>
        private void DismissSiblings(FactionState st, int skipFi, PendingFleetAction failed, DateTime now)
        {
            for (int oi = 0; oi < st.PendingFleets.Count; oi++)
            {
                if (oi == skipFi) continue;
                PendingFleetAction sib = st.PendingFleets[oi];
                if (sib.Resolved || sib.Intercepted) continue;
                if (!sib.IsDuplicateOf(failed)) continue;
                // A dismissed duplicate keeps no grid. Both entries of a duplicate pair spawn when
                // the kind is uncapped, so the sibling may well hold a lead of its own by now, and
                // once its record reads -1 nothing else will ever close that grid.
                string gridToken = "";
                if (sib.MaterialisedEntityId > 0)
                {
                    long sibEid = sib.MaterialisedEntityId;
                    _stalledSince.Remove(sibEid);
                    _safezoneArrivedSince.Remove(sibEid);
                    _engagementHeldSince.Remove(sibEid);
                    _spawnSettle.Release(sibEid);
                    _settleHeldReported.Remove(sibEid);
                    int closed = _spawner.Despawn(sibEid);
                    gridToken = " eid=" + sibEid + " closed=" + closed;
                }
                int escortsClosed = DespawnEscorts(sib.EscortEntityIds, out _);
                var d = sib;
                d.MaterialisedEntityId = -1;
                d.EscortEntityIds      = null;
                d.EstimatedArrivalUtc  = now;
                d.ForcedRatio          = 0.0;
                d.ForcedRatioCause     = PendingFleetResolver.SiblingDismissCause;
                st.PendingFleets[oi]   = d;
                _log.Write("SIBLING_DISMISS " + sib.LogId + " " + sib.ActorTag + " kind=" + sib.Kind
                           + gridToken + EscortsToken(escortsClosed));
            }
        }

        /// <summary>Starts (or polls) the materialisation of one fleet. Returns the lead grid's
        /// entity id, or 0 while the spawn is still in flight — and sets
        /// <paramref name="unsupportedKind"/> when the 0 means "this kind has no spawn path" rather
        /// than "not yet", which are otherwise the same answer. A squadron's other hulls are appended
        /// to <paramref name="escortsOut"/>; every other fleet leaves it empty.</summary>
        private long SpawnFleetGrid(PendingFleetAction fleet, long factionId, DateTime now, string key,
                                    List<long> escortsOut, out bool unsupportedKind)
        {
            unsupportedKind = false;
            if (key == null) key = FleetKey(fleet);
            Vec3 pos;
            if (!_meetingSpawnAt.TryGetValue(key, out pos)) pos = fleet.CurrentPosKm(now, _tuning);
            // Every kind hands the spawner the hull the dispatch consumed, so the grid a player sees
            // is the ship the faction actually paid for. "" means the fleet named no hull, which is
            // the spawner's cue to fall back to a role lookup.
            string prefabId = fleet.ShipPrefabId ?? "";

            // An armed fleet brings its own magazines; the grid's are replaced by them at spawn.
            if (fleet.MunitionsArmed && fleet.Munitions != null)
            {
                var vanilla = _spawner as VanillaFleetSpawner;
                if (vanilla != null)
                {
                    ApplyMunitionReading(vanilla, fleet);
                    vanilla.StageMunitions(key, MunitionsId(fleet), fleet.Munitions);
                }
            }

            // A squadron dispatch consumed one hull per member, so it materialises one grid per
            // member: the lead carries the fleet's flight control and the escorts fly the same
            // waypoint. Squadron[0] is the preferred lead — the hull ShipPrefabId names.
            if (SpawnsAsSquadron(fleet))
                return _spawner.SpawnSquadron(key, fleet.Kind, fleet.ActorTag, factionId, pos,
                                              fleet.TargetPosKm, SpawnHullsFor(fleet), escortsOut);

            switch (fleet.Kind)
            {
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer: return _spawner.SpawnRaid(key, fleet.ActorTag, factionId, pos, fleet.TargetPosKm, prefabId);
                case ActionKind.Trade:  return _spawner.SpawnTrade(key, fleet.ActorTag, factionId, pos, fleet.TargetPosKm, prefabId);
                case ActionKind.Guard:
                case ActionKind.Patrol:
                // A reinforce flies a Patrol hull and holds an Engage dwell at its destination, so it
                // has to materialise or a player can never reach it (FAC-327).
                case ActionKind.Reinforce: return _spawner.SpawnPatrol(key, fleet.ActorTag, factionId, pos, fleet.TargetPosKm, prefabId);
                case ActionKind.Haul:   return _spawner.SpawnHaul(key, fleet.ActorTag, factionId, pos, fleet.TargetPosKm, prefabId);
                case ActionKind.Gift:   return _spawner.SpawnGift(key, fleet.ActorTag, factionId, pos, fleet.TargetPosKm, prefabId);
                // On its dwell a miner is out on the ring around its deposit, so it is aimed at the
                // ring's next waypoint rather than at the deposit it is working.
                case ActionKind.Mine:   return _spawner.SpawnMine(key, fleet.ActorTag, factionId, pos, SpawnTargetFor(fleet, now), prefabId);
                // Ferry: nothing dispatches one, so it has no spawn path.
                default:
                    unsupportedKind = true;
                    return 0;
            }
        }

        /// <summary>True when this fleet materialises as several grids: one of the kinds that fly a
        /// squadron, carrying more than one hull. Guard and Patrol name one since FAC-312; Trade/Haul
        /// convoys never do, so they keep flying the single grid they always have.</summary>
        private static bool SpawnsAsSquadron(PendingFleetAction fleet)
        {
            if (fleet.Squadron == null || fleet.Squadron.Count <= 1) return false;
            return fleet.Kind == ActionKind.Raid
                || fleet.Kind == ActionKind.Retaliate
                || fleet.Kind == ActionKind.Conquer
                || fleet.Kind == ActionKind.Guard
                || fleet.Kind == ActionKind.Patrol
                || fleet.Kind == ActionKind.Reinforce;
        }

        /// <summary>Kinds the per-faction materialisation cap and SPAWN_DEDUP apply to: everything
        /// that flies an armed grid at somewhere, plus the patrol pair. Both gates count fleet
        /// entries rather than grids, so a squadron counts once.</summary>
        private static bool IsCappedSpawnKind(ActionKind kind)
        {
            return kind == ActionKind.Raid
                || kind == ActionKind.Retaliate
                || kind == ActionKind.Conquer
                || kind == ActionKind.Guard
                || kind == ActionKind.Patrol
                || kind == ActionKind.Reinforce;
        }

        /// <summary>How long to wait for a fleet's spawn callbacks before abandoning them: one
        /// <see cref="SpawnInFlightTimeout"/> per grid it is materialising, so a squadron is not
        /// timed out on the clock its second prefab load is still running against.</summary>
        private static TimeSpan SpawnTimeoutFor(PendingFleetAction fleet)
        {
            int grids = SpawnsAsSquadron(fleet) ? fleet.Squadron.Count : 1;
            return grids <= 1 ? SpawnInFlightTimeout : TimeSpan.FromTicks(SpawnInFlightTimeout.Ticks * grids);
        }

        /// <summary>Escort grids this fleet currently holds.</summary>
        private static int EscortCount(PendingFleetAction fleet)
        {
            return fleet.EscortEntityIds == null ? 0 : fleet.EscortEntityIds.Count;
        }

        /// <summary>Escort count for a log line, or nothing at all when there are none — a
        /// single-hull fleet's lines read exactly as they always have.</summary>
        private static string EscortsToken(int count)
        {
            return count <= 0 ? "" : " escorts=" + count;
        }

        /// <summary>Escorts that came through an operation against the number that went into it, or
        /// nothing at all for a fleet that has none.</summary>
        private static string EscortTally(int live, int of)
        {
            return of <= 0 ? "" : " escorts=" + live + "/" + of;
        }

        /// <summary>Closes every escort in <paramref name="escortEids"/> and returns how many
        /// <b>hulls</b> were actually found and closed; <paramref name="gridsClosed"/> is how many
        /// grids that took, which is larger whenever an escort carries subgrids. The caller nulls the
        /// list on the fleet copy it stores: escorts exist only while the lead does.</summary>
        private int DespawnEscorts(List<long> escortEids, out int gridsClosed)
        {
            gridsClosed = 0;
            if (escortEids == null || escortEids.Count == 0 || _spawner == null) return 0;
            int closed = 0;
            for (int i = 0; i < escortEids.Count; i++)
            {
                if (escortEids[i] <= 0) continue;
                int grids = _spawner.Despawn(escortEids[i]);
                gridsClosed += grids;
                if (grids > 0) closed++;
            }
            return closed;
        }

        /// <summary>Closes the grids in <paramref name="eids"/> from <paramref name="from"/> onward
        /// and returns how many were found and closed.</summary>
        /// <summary>Closes a displaced stash's hulls, leaving <paramref name="keepEid"/> alone — the
        /// grid the incoming stash is about to hold, which a colliding <see cref="PendingFleetAction.FleetId"/>
        /// could otherwise put on both sides of the swap.</summary>
        private int DespawnEidsExcept(long[] eids, long keepEid, out int gridsClosed)
        {
            gridsClosed = 0;
            if (eids == null || _spawner == null) return 0;
            int closed = 0;
            for (int i = 0; i < eids.Length; i++)
            {
                if (eids[i] <= 0 || eids[i] == keepEid) continue;
                int grids = _spawner.Despawn(eids[i]);
                gridsClosed += grids;
                if (grids > 0) closed++;
            }
            return closed;
        }

        private int DespawnEids(long[] eids, int from, out int gridsClosed)
        {
            gridsClosed = 0;
            if (eids == null || _spawner == null) return 0;
            int closed = 0;
            for (int i = from; i < eids.Length; i++)
            {
                if (eids[i] <= 0) continue;
                int grids = _spawner.Despawn(eids[i]);
                gridsClosed += grids;
                if (grids > 0) closed++;
            }
            return closed;
        }

        /// <summary>Points every escort at the lead's new waypoint on the cruise autopilot and
        /// returns how many took it. Without escort control this is the only steering escorts ever
        /// get; with it, each is taken back into formation on the next frame, and this is the
        /// waypoint any escort the controller refuses flies.</summary>
        private int RetargetEscorts(List<long> escortEids, Vec3 targetKm)
        {
            if (escortEids == null || escortEids.Count == 0 || _spawner == null) return 0;
            int rewired = 0;
            for (int i = 0; i < escortEids.Count; i++)
                if (escortEids[i] > 0 && _spawner.Retarget(escortEids[i], targetKm)) rewired++;
            return rewired;
        }

        /// <summary>Lead first, then its escorts — the shape <see cref="_pendingRetargetEid"/> holds
        /// a squadron in between legs.</summary>
        private static long[] StashEids(long leadEid, List<long> escortEids)
        {
            int escortCount = escortEids == null ? 0 : escortEids.Count;
            var stash = new long[1 + escortCount];
            stash[0] = leadEid;
            for (int i = 0; i < escortCount; i++) stash[1 + i] = escortEids[i];
            return stash;
        }

        private readonly List<long> _hullEids = new List<long>();

        /// <summary>The hulls a squadron spawns: every surviving member, so a hull already written off
        /// does not come back as a grid.</summary>
        private static IReadOnlyList<string> SpawnHulls(PendingFleetAction fleet)
        {
            return fleet.HullsToReturn;
        }

        /// <summary>The hull slot each grid in <paramref name="eids"/> (lead first, then escorts)
        /// flies, or -1 where none can be named. A single-hull fleet's one grid is slot 0; a
        /// squadron's grids are matched to <see cref="PendingFleetAction.HullsToReturn"/> by prefab
        /// in order, which is the order they were spawned in. Two hulls of one prefab may trade
        /// slots this way, and nothing distinguishes them but their integrity, which goes with the
        /// grid that is read.</summary>
        private int[] MapHullSlots(PendingFleetAction fleet, List<long> eids)
        {
            var slots = new int[eids.Count];
            int count = fleet.HullSlotCount;
            var taken = new bool[count];
            bool isSquadron = fleet.Squadron != null && fleet.Squadron.Count > 0;
            IReadOnlyList<string> hulls = fleet.HullsToReturn;
            // A waved fleet's grids carry the slot they were cleared for (FightWaves).
            for (int k = 0; k < eids.Count; k++)
            {
                int explicitSlot = WaveSlotOf(fleet, eids[k]);
                slots[k] = -1;
                if (explicitSlot >= 0 && explicitSlot < count && !taken[explicitSlot])
                {
                    slots[k] = explicitSlot;
                    taken[explicitSlot] = true;
                }
            }
            for (int k = 0; k < eids.Count; k++)
            {
                if (slots[k] >= 0) continue;
                if (!isSquadron)
                {
                    if (count > 0 && !taken[0]) { slots[k] = 0; taken[0] = true; }
                    continue;
                }
                string prefab = _spawner.GridPrefabId(eids[k]);
                if (prefab == null) continue;
                for (int j = 0; j < count && j < hulls.Count; j++)
                {
                    if (taken[j] || !string.Equals(hulls[j], prefab, StringComparison.Ordinal)) continue;
                    slots[k] = j;
                    taken[j] = true;
                    break;
                }
            }
            return slots;
        }

        private readonly List<long> _escortEidScratch = new List<long>(1);

        /// <summary>Credits <paramref name="damage"/> a station's zone absorbed from hull
        /// <paramref name="gridEid"/> to the fleet that grid belongs to, when that fleet is on its
        /// attack dwell at <paramref name="nodeId"/> (<see cref="PendingFleetAction.FightZoneHits"/>).
        /// Answers the fleet's faction tag, or null when the grid is no such fleet.</summary>
        private string CreditRaidZoneHit(long gridEid, string nodeId, double damage)
        {
            if (gridEid <= 0 || nodeId == null || damage <= 0) return null;
            foreach (FactionState st in _states)
            {
                List<PendingFleetAction> fleets = st.PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (f.MaterialisedEntityId != gridEid
                        && (f.EscortEntityIds == null || !f.EscortEntityIds.Contains(gridEid))) continue;
                    if (f.CurrentStopKind != FleetStopKind.Engage
                        || !string.Equals(f.TargetNodeId, nodeId, StringComparison.Ordinal)) return null;
                    f.FightZoneHits += damage;
                    fleets[fi] = f;
                    return f.ActorTag;
                }
            }
            return null;
        }

        /// <summary>Whether a fleet of <paramref name="tag"/> is still on its attack dwell at
        /// <paramref name="nodeId"/>.</summary>
        private bool RaidDwellingAt(string nodeId, string tag)
        {
            foreach (FactionState st in _states)
            {
                if (!string.Equals(st.Tag, tag, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (PendingFleetAction f in st.PendingFleets)
                    if (f.CurrentStopKind == FleetStopKind.Engage
                        && string.Equals(f.TargetNodeId, nodeId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Moves what <paramref name="eids"/> dealt and took in the world since they were last
        /// read into the fleet's fight totals (<see cref="PendingFleetAction.FightDamageDealt"/>).</summary>
        private static void BankFightDamage(VanillaFleetSpawner vanilla, ref PendingFleetAction fleet, List<long> eids)
        {
            for (int k = 0; k < eids.Count; k++)
            {
                double dealt, taken;
                vanilla.TakeFightDamage(eids[k], out dealt, out taken);
                fleet.FightDamageDealt += dealt;
                fleet.FightDamageTaken += taken;
            }
        }

        private void CollectHullEids(PendingFleetAction fleet, long leadEid)
        {
            _hullEids.Clear();
            if (leadEid > 0) _hullEids.Add(leadEid);
            if (fleet.EscortEntityIds != null)
                for (int i = 0; i < fleet.EscortEntityIds.Count; i++)
                    if (fleet.EscortEntityIds[i] > 0) _hullEids.Add(fleet.EscortEntityIds[i]);
        }

        /// <summary>The hull slot the grid <paramref name="eid"/> flies in this fleet, or -1.</summary>
        private int HullSlotOf(PendingFleetAction fleet, long eid)
        {
            CollectHullEids(fleet, fleet.MaterialisedEntityId);
            int[] slots = MapHullSlots(fleet, _hullEids);
            for (int k = 0; k < _hullEids.Count; k++) if (_hullEids[k] == eid) return slots[k];
            return -1;
        }

        /// <summary>Starts the integrity watch on a freshly spawned fleet's grids, each at its hull
        /// slot's recorded integrity (which damages its blocks when below whole), and logs
        /// <c>HULL_SPAWNED_DAMAGED</c> when any was.</summary>
        private void WatchHullIntegrity(PendingFleetAction fleet)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null) return;
            CollectHullEids(fleet, fleet.MaterialisedEntityId);
            int[] slots = MapHullSlots(fleet, _hullEids);
            bool anyDamaged = false;
            int blocks = 0;
            for (int k = 0; k < _hullEids.Count; k++)
            {
                double integrity = slots[k] >= 0 ? fleet.IntegrityOf(slots[k]) : 1.0;
                if (integrity < 1.0) anyDamaged = true;
                int damaged = vanilla.WatchIntegrity(_hullEids[k], integrity);
                if (damaged > 0) blocks += damaged;
            }
            if (anyDamaged)
                _log.Write("HULL_SPAWNED_DAMAGED " + fleet.LogId + " " + fleet.ActorTag
                           + " hulls=" + HullDamage.Describe(fleet) + " blocks_damaged=" + blocks);
        }

        /// <summary>Settles a fleet's hulls as its grids leave the world: each live grid's integrity
        /// reading is written into its hull slot, and <paramref name="lostEid"/>, when set, names the
        /// grid whose hull is written off here -- by slot, or by <paramref name="lostPrefab"/> through
        /// <see cref="PendingFleetAction.MarkHullLost"/> when no integrity watch can name one. Call it before the
        /// grids are despawned; returns the fleet, which the caller stores. Logs <c>HULL_DAMAGED</c>
        /// once when a reading moved.</summary>
        private PendingFleetAction SettleHulls(PendingFleetAction fleet, long leadEid, string cause,
                                               long lostEid = 0, string lostPrefab = null)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null)
            {
                if (lostEid != 0) fleet.MarkHullLost(lostPrefab);
                return fleet;
            }
            CollectHullEids(fleet, leadEid);
            BankFightDamage(vanilla, ref fleet, _hullEids);

            CollectHullEids(fleet, leadEid);
            int[] slots = MapHullSlots(fleet, _hullEids);
            bool changed = false;
            int lostSlot = -1;
            for (int k = 0; k < _hullEids.Count; k++)
            {
                long eid = _hullEids[k];
                if (eid == lostEid) { lostSlot = slots[k]; continue; }
                if (slots[k] < 0) continue;
                double? reading = vanilla.ReadIntegrity(eid);
                if (!reading.HasValue) continue;
                double before = fleet.IntegrityOf(slots[k]);
                fleet.SetIntegrity(slots[k], reading.Value);
                if (Math.Abs(fleet.IntegrityOf(slots[k]) - before) > 0.0005) changed = true;
            }
            if (lostEid != 0)
            {
                if (lostSlot >= 0 && fleet.Squadron != null && fleet.Squadron.Count > 0) fleet.MarkHullLostAt(lostSlot);
                else fleet.MarkHullLost(lostPrefab);
                // A hull gone from the fleet fires nothing more.
                if (!changed) HullDamage.RescaleBands(ref fleet, _world.PrefabCatalogue);
            }
            if (changed)
            {
                HullDamage.RescaleBands(ref fleet, _world.PrefabCatalogue);
                _log.Write("HULL_DAMAGED " + fleet.LogId + " " + fleet.ActorTag + " cause=" + cause
                           + " hulls=" + HullDamage.Describe(fleet));
            }
            return fleet;
        }

        /// <summary>Checks a materialised squadron's escorts against the world and settles the ones
        /// that are no longer flying for this faction. An escort gone with fresh damage on record
        /// costs its hull; one that merely vanished costs nothing; one whose owner changed is a
        /// capture, which costs the hull and leaves the grid to the player who took it. Only the
        /// lead's loss can fail the fleet, so nothing here touches
        /// <see cref="PendingFleetAction.MaterialisedEntityId"/>. Returns the fleet as it now
        /// stands, and writes it back into <paramref name="st"/>.</summary>
        private PendingFleetAction SweepEscorts(FactionState st, int fi, PendingFleetAction fleet, DateTime now)
        {
            List<long> escorts = fleet.EscortEntityIds;
            if (escorts == null || escorts.Count == 0) return fleet;

            var swept = fleet;
            bool changed = false;
            for (int ei = escorts.Count - 1; ei >= 0; ei--)
            {
                long escortEid = escorts[ei];
                bool gone = escortEid <= 0 || !_spawner.GridExists(escortEid);

                string ownerTag = gone ? null : _spawner.GetGridOwnerFactionTag(escortEid);
                bool taken = !gone && ownerTag != null
                             && !string.Equals(ownerTag, fleet.ActorTag, StringComparison.OrdinalIgnoreCase);
                if (!gone && !taken)
                {
                    // Still flying for its faction -- unless it has just been shot out of action.
                    if (TryDisableEscort(fleet, ref swept, escortEid, now)) changed = true;
                    continue;
                }

                // Same discriminator the lead gets: absence alone never destroys stock, only
                // absence with damage on record inside the loss window does.
                DateTime? lastDamage = _spawner.LastDamageUtc(escortEid);
                bool lostForGood = taken
                                   || GridLossRule.IsDestroyed(_spawner.ClosedByUs(escortEid), lastDamage, now);
                string prefabId = _spawner.GridPrefabId(escortEid);
                // Which hull slot this grid flew, read while it is still in the list.
                int lostSlot = lostForGood ? HullSlotOf(swept, escortEid) : -1;
                var vanillaSpawner = _spawner as VanillaFleetSpawner;
                if (vanillaSpawner != null)
                {
                    _escortEidScratch.Clear();
                    _escortEidScratch.Add(escortEid);
                    BankFightDamage(vanillaSpawner, ref swept, _escortEidScratch);
                }

                escorts.RemoveAt(ei);
                changed = true;
                _stalledSince.Remove(escortEid);
                _safezoneArrivedSince.Remove(escortEid);
                _engagementHeldSince.Remove(escortEid);
                _spawner.ReleaseControl(escortEid);

                if (lostForGood)
                {
                    // No fallback prefab here: ShipPrefabId names the LEAD's hull, so guessing with
                    // it would write off a ship that is still flying. A null prefab (a grid this
                    // process did not spawn, e.g. one adopted across a restart) leaves the hull
                    // creditable, which is the same way round every other unproven loss is settled.
                    if (lostSlot >= 0) swept.MarkHullLostAt(lostSlot);
                    else swept.MarkHullLost(prefabId);
                    if (lostSlot >= 0 && HasWaveSlots(swept)) WaveSlotRemoved(swept, lostSlot, now);
                    HullDamage.RescaleBands(ref swept, _world.PrefabCatalogue);
                    _log.Write("ESCORT_LOST " + fleet.LogId + " " + fleet.ActorTag
                               + " eid=" + escortEid + " prefab=" + (prefabId ?? "?")
                               + " remaining=" + escorts.Count
                               + (taken ? " cause=captured" : ""));
                }
                else
                {
                    if (HasWaveSlots(swept)) WaveGridGone(swept, escortEid, now);
                    _log.Write("ESCORT_GONE " + fleet.LogId + " " + fleet.ActorTag
                               + " eid=" + escortEid + " prefab=" + (prefabId ?? "?")
                               + " remaining=" + escorts.Count);
                }
            }

            if (!changed) return fleet;
            if (escorts.Count == 0) swept.EscortEntityIds = null;
            st.PendingFleets[fi] = swept;
            return swept;
        }

        /// <summary>Whispers an immersive fleet-progress message to every online player currently
        /// within <see cref="FleetMessageRadiusKm"/> of <paramref name="posKm"/> — mirrors
        /// <see cref="ClaimOfferManager"/>'s Whisper pattern, but targets spatial proximity rather
        /// than faction membership. No-op silently when chat isn't
        /// attached, the player-proximity delegate wasn't wired (dry-run/no-MES), or the message is
        /// null (e.g. an ActionKind this feature has no copy for).</summary>
        /// <param name="fleetTraffic">True for the grid-lifecycle messages (spawn, arrival, departure,
        /// pre-despawn). A player whose client speaks station traffic (FAC-419) is skipped for those:
        /// the voice replaces them. Combat outcomes and tactic hints are not traffic and always go.</param>
        private void SendNearbyFleetMessage(Vec3 posKm, string message, bool fleetTraffic = false)
        {
            if (_chat == null || _playersWithin == null || message == null) return;
            Func<ulong, bool> speaks = fleetTraffic ? SpeaksTraffic : null;
            foreach (ulong steamId in _playersWithin(posKm, FleetMessageRadiusKm))
            {
                if (speaks != null && speaks(steamId)) continue;
                _chat.SendMessageAsOther(FleetWhisperAuthor, message, Color.CornflowerBlue, steamId);
            }
        }

        private static string SpawnMessageFor(PendingFleetAction f)
        {
            switch (f.Kind)
            {
                case ActionKind.Haul:  return "A [" + f.ActorTag + "] haul convoy has departed toward " + (f.TargetNodeId ?? "its destination") + ".";
                case ActionKind.Trade: return "A [" + f.ActorTag + "] trade convoy has set out for " + (f.TargetNodeId ?? "its destination") + ".";
                case ActionKind.Gift:  return "A [" + f.ActorTag + "] gift convoy is under way to " + (f.TargetNodeId ?? "its destination") + ".";
                case ActionKind.Mine:  return MineWander.Applies(f)
                    ? "A [" + f.ActorTag + "] mining ship is working the field around " + (f.TargetNodeId ?? "its deposit") + "."
                    : "A [" + f.ActorTag + "] mining ship is under way to " + (f.TargetNodeId ?? "its destination") + ".";
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer: return "A [" + f.ActorTag + "] raiding fleet has launched, bound for " + (f.TargetNodeId ?? "its target") + "!";
                case ActionKind.Guard:
                case ActionKind.Patrol: return "A [" + f.ActorTag + "] patrol has taken to space toward " + (f.TargetNodeId ?? "its post") + ".";
                case ActionKind.Reinforce: return "A [" + f.ActorTag + "] relief squadron is under way to " + (f.TargetNodeId ?? "its post") + ".";
                default: return null;
            }
        }

        private static string ArrivalMessageFor(PendingFleetAction f)
        {
            if (f.CurrentStopKind == FleetStopKind.Navigation) return null; // internal route-clearance waypoint — no player-facing meaning
            switch (f.Kind)
            {
                case ActionKind.Haul:  return "The [" + f.ActorTag + "] haul convoy has reached " + (f.TargetNodeId ?? "its stop") + " and is loading cargo.";
                case ActionKind.Trade: return "A [" + f.ActorTag + "] trade convoy has arrived at " + (f.TargetNodeId ?? "its destination") + ".";
                case ActionKind.Gift:  return "A [" + f.ActorTag + "] gift convoy has arrived at " + (f.TargetNodeId ?? "its destination") + ".";
                case ActionKind.Mine:  return "The [" + f.ActorTag + "] mining ship has reached " + (f.TargetNodeId ?? "its stop") + " and is unloading ore.";
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer: return "A [" + f.ActorTag + "] raiding fleet has arrived at " + (f.TargetNodeId ?? "its target") + "!";
                case ActionKind.Guard:
                case ActionKind.Patrol: return "A [" + f.ActorTag + "] patrol has taken up position at " + (f.TargetNodeId ?? "its post") + ".";
                case ActionKind.Reinforce: return "A [" + f.ActorTag + "] relief squadron has arrived at " + (f.TargetNodeId ?? "its post") + ".";
                default: return null;
            }
        }

        private static string DepartureMessageFor(PendingFleetAction f)
        {
            if (f.CurrentStopKind == FleetStopKind.Navigation) return null;
            switch (f.Kind)
            {
                case ActionKind.Haul:  return "The [" + f.ActorTag + "] haul convoy is underway again, pushing on toward its next stop.";
                case ActionKind.Trade: return "The [" + f.ActorTag + "] trade convoy has finished loading and is departing for its next stop.";
                default: return "The [" + f.ActorTag + "] " + f.Kind.ToString().ToLowerInvariant() + " fleet is moving on to its next stop.";
            }
        }

        private static string PreDespawnMessageFor(PendingFleetAction f)
        {
            switch (f.Kind)
            {
                case ActionKind.Haul:  return "The [" + f.ActorTag + "] haul convoy has finished unloading and is powering down its drives.";
                case ActionKind.Trade: return "The [" + f.ActorTag + "] trade convoy's business is concluded; it slips away from view.";
                case ActionKind.Gift:  return "The [" + f.ActorTag + "] gift convoy has made its delivery and slips away from view.";
                case ActionKind.Mine:  return "The [" + f.ActorTag + "] mining ship has unloaded its ore and is powering down its drives.";
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer: return "The [" + f.ActorTag + "] raiding fleet withdraws, its business here finished.";
                case ActionKind.Guard:
                case ActionKind.Patrol: return "The [" + f.ActorTag + "] patrol breaks off station and heads for home.";
                case ActionKind.Reinforce: return "The [" + f.ActorTag + "] relief squadron stands down and heads for home.";
                default: return null;
            }
        }

        /// <summary>
        /// Returns the label + current interpolated position (km, world-space) for every active
        /// fleet of the given <paramref name="kind"/> that has not yet arrived or been resolved.
        /// Used by the debug GPS overlay in <see cref="FactionsPlugin"/>. Includes the materialised
        /// entity ID (0 if not yet spawned) so the caller can set <c>MyGps.EntityId</c> when the
        /// grid exists (hash then depends only on name × entityId, so the marker stays stable and
        /// auto-tracks the grid).
        /// </summary>
        public List<ScannerFleetCandidate> GetScannerFleetCandidates()
        {
            var result = new List<ScannerFleetCandidate>();
            if (_world == null) return result;
            DateTime now = DateTime.UtcNow;
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    if (fleet.Intercepted) continue;
                    bool isActiveGuard = fleet.IsActiveGuard(now, _world.Tuning);
                    if (fleet.Resolved && !isActiveGuard) continue;
                    if (!fleet.Resolved && fleet.HasArrived(now)) continue;
                    string key = PlayerScanner.FleetContactKey(fleet);
                    result.Add(new ScannerFleetCandidate
                    {
                        Key = key,
                        PositionKm = fleet.CurrentPosKm(now, _tuning),
                        VelocityKmPerMin = SampleFleetVelocity(fleet, now),
                        TargetPosKm = fleet.TargetPosKm,
                        EntityId = fleet.MaterialisedEntityId > 0 ? fleet.MaterialisedEntityId : 0L,
                        OwnerTag = st.Tag,
                        Signature = FleetSensing.SignatureOf(fleet, _world.PrefabCatalogue, _itemVolumes, _tuning, now),
                        BroadcastRadiusKm = BroadcastKmOf(fleet),
                        // Non-null only while the raid is actually fighting, which is what promotes
                        // it from a signature-scaled contact to a flat-range broadcast.
                        EngagementTargetNodeId = fleet.CurrentStopKind == FleetStopKind.Engage
                                                 || fleet.CurrentStopKind == FleetStopKind.Intercept
                            ? fleet.TargetNodeId : null,
                        MissionKind = (byte)fleet.Kind,
                        ConvoySize = fleet.ConvoySize,
                        TacticHint = TacticHintFor(fleet, now),
                    });
                }
            }
            return result;
        }

        /// <summary>What a fleet is announcing about itself, km, or 0.
        ///
        /// <para>Only a <b>materialised</b> fleet can announce anything: an abstract convoy has no
        /// grid, so SE's own antenna HUD has nothing to draw and there is nothing for our fog to
        /// contradict (FAC-140). The entity lookup therefore runs only for a fleet that has spawned,
        /// which needs a player inside <c>ObservationRadiusKm</c> and is a handful at most.</para></summary>
        private static double BroadcastKmOf(PendingFleetAction fleet)
        {
            if (fleet.MaterialisedEntityId <= 0) return 0;
            MyCubeGrid grid = MyEntities.GetEntityById(fleet.MaterialisedEntityId) as MyCubeGrid;
            return grid != null ? ShipAntennas.BroadcastKm(grid) : 0;
        }

        /// <summary>Whether this engine still believes <paramref name="entityId"/> is a hull it owns
        /// — a materialised fleet lead, one of its escorts, a squadron stashed between legs, or any
        /// grid whose <see cref="SimGridRecord"/> names a fleet that is still pending.
        /// <see cref="DebrisAbsorbManager"/> and <see cref="GridReaper"/> ask before closing
        /// anything, so a hull we intend to fly again is never judged on its physics.
        ///
        /// <para><b>The record clause is what covers a prefab's subgrids.</b> A spawn produces one
        /// lead and however many rotor-mounted turrets the prefab carries; only the lead's id is in
        /// <c>MaterialisedEntityId</c>, so without this every turret on a flying hull reads as
        /// unheld. A squadron spawns under <c>&lt;key&gt;#&lt;i&gt;</c>, which is why the match is a
        /// prefix and not an equality.</para></summary>
        internal bool HoldsMaterialisedGrid(long entityId)
        {
            if (entityId == 0) return false;
            if (IsLiveHull(entityId)) return true;

            SimGridRecord record;
            string recordKey = SimGridMarks.TryGet(entityId, out record) && record != null
                               && !string.IsNullOrEmpty(record.FleetKey) ? record.FleetKey : null;
            if (recordKey == null) return false;

            // FAC-425: a piece a hull shed in a fight carries that hull's fleet key, so the record
            // clause alone would hold every piece of wreckage until the whole fleet resolves. A record
            // holds a grid only while it is still attached to a hull the fleet is flying -- or while the
            // fleet is mid-spawn, when its hulls are not yet named.
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    if (!FleetKeyCovers(FleetKey(fleet), recordKey)) continue;
                    if (fleet.MaterialisedEntityId == PendingFleetAction.SpawningId) return true;
                    return AttachedToLiveHull(entityId);
                }
            }
            return false;
        }

        private readonly List<MyCubeGrid> _attachScratch = new List<MyCubeGrid>();

        /// <summary>Whether <paramref name="entityId"/>'s mechanical group holds a hull this engine
        /// is flying, departing, keeping as a wreck, or has stashed between legs — so a prefab's
        /// rotor turrets count and a piece that broke off does not.</summary>
        private bool AttachedToLiveHull(long entityId)
        {
            var grid = MyEntities.GetEntityById(entityId) as MyCubeGrid;
            if (grid == null || grid.MarkedForClose) return false;
            _attachScratch.Clear();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Mechanical != null)
            {
                try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, _attachScratch); }
                catch (Exception) { _attachScratch.Clear(); }
            }
            if (!_attachScratch.Contains(grid)) _attachScratch.Add(grid);
            try
            {
                for (int g = 0; g < _attachScratch.Count; g++)
                {
                    MyCubeGrid member = _attachScratch[g];
                    if (member != null && IsLiveHull(member.EntityId)) return true;
                }
                return false;
            }
            finally { _attachScratch.Clear(); }
        }

        /// <summary>The id is a hull itself — a lead, an escort, a stashed member, a departing or
        /// disabled hull. By id only; the record clause is <see cref="HoldsMaterialisedGrid"/>'s.</summary>
        private bool IsLiveHull(long eid)
        {
            if (eid == 0) return false;
            if (IsDeparting(eid) || IsDisabledWreck(eid) || IsWaveLeaving(eid)) return true;
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    if (fleet.MaterialisedEntityId == eid) return true;
                    List<long> escorts = fleet.EscortEntityIds;
                    if (escorts == null) continue;
                    for (int i = 0; i < escorts.Count; i++)
                        if (escorts[i] == eid) return true;
                }
            }
            foreach (KeyValuePair<string, StashedSquadron> stash in _pendingRetargetEid)
            {
                long[] eids = stash.Value.Eids;
                if (eids == null) continue;
                for (int i = 0; i < eids.Length; i++)
                    if (eids[i] == eid) return true;
            }
            return false;
        }

        /// <summary>The positions, in km, of every hull of every materialised combat fleet — where the
        /// fight sweep looks for wreckage. Combat as the governor reads it
        /// (<see cref="FactionsLoadActuator.IsCombat"/>).</summary>
        internal void CollectCombatHullPositionsKm(List<Vec3> into)
        {
            if (into == null) return;
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    if (fleet.MaterialisedEntityId <= 0 || !FactionsLoadActuator.IsCombat(fleet.Kind)) continue;
                    AddGridPositionKm(fleet.MaterialisedEntityId, into);
                    List<long> escorts = fleet.EscortEntityIds;
                    if (escorts == null) continue;
                    for (int i = 0; i < escorts.Count; i++) AddGridPositionKm(escorts[i], into);
                }
            }
        }

        private static void AddGridPositionKm(long eid, List<Vec3> into)
        {
            if (eid <= 0) return;
            var grid = MyEntities.GetEntityById(eid) as MyCubeGrid;
            if (grid == null || grid.MarkedForClose || grid.PositionComp == null) return;
            VRageMath.Vector3D p = grid.PositionComp.GetPosition();
            into.Add(new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0));
        }

        /// <summary>Whether <paramref name="entityId"/> is a station grid: one this plugin
        /// materialised for a founded node, or one an SE faction claims as a station. A station is
        /// spawned from a prefab like a hull is, so it carries the NPC-spawned bit and would
        /// otherwise read to the reaper as exactly the litter it exists to close.</summary>
        internal bool IsStationGrid(long entityId)
        {
            if (entityId == 0) return false;
            if (_stationMaterializer != null && _stationMaterializer.NodeIdForStationGrid(entityId) != null)
                return true;

            if (Sandbox.Game.World.MySession.Static == null
                || Sandbox.Game.World.MySession.Static.Factions == null) return false;
            foreach (var pair in Sandbox.Game.World.MySession.Static.Factions)
            {
                var faction = pair.Value;
                if (faction == null) continue;
                foreach (var station in faction.Stations)
                    if (station != null && station.StationEntityId == entityId) return true;
            }
            return false;
        }

        /// <summary>Whether <paramref name="recordKey"/> — the key a grid was marked under — belongs
        /// to the fleet keyed <paramref name="fleetKey"/>. Equal, or a squadron member's
        /// <c>&lt;fleetKey&gt;#&lt;i&gt;</c>.</summary>
        private static bool FleetKeyCovers(string fleetKey, string recordKey)
        {
            if (string.IsNullOrEmpty(fleetKey)) return false;
            if (string.Equals(fleetKey, recordKey, StringComparison.Ordinal)) return true;
            return recordKey.Length > fleetKey.Length
                && recordKey[fleetKey.Length] == '#'
                && string.CompareOrdinal(recordKey, 0, fleetKey, 0, fleetKey.Length) == 0;
        }

        /// <summary>
        /// Every active fleet, with the extra detail the map window draws: destination, itinerary
        /// and velocity. Same selection rules as <see cref="GetScannerFleetCandidates"/> — the two
        /// features must agree on which fleets exist, and on what each one is called.
        /// </summary>
        internal List<MapFleetInfo> GetMapFleets()
        {
            var result = new List<MapFleetInfo>();
            if (_world == null) return result;
            DateTime now = DateTime.UtcNow;
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    if (fleet.Intercepted) continue;
                    bool isActiveGuard = fleet.IsActiveGuard(now, _world.Tuning);
                    if (fleet.Resolved && !isActiveGuard) continue;
                    if (!fleet.Resolved && fleet.HasArrived(now)) continue;

                    result.Add(new MapFleetInfo
                    {
                        Key = PlayerScanner.FleetContactKey(fleet),
                        Kind = fleet.Kind,
                        OwnerTag = st.Tag,
                        OriginNodeId = fleet.OriginNodeId,
                        TargetNodeId = fleet.TargetNodeId,
                        PositionKm = fleet.CurrentPosKm(now, _tuning),
                        VelocityKmPerMin = SampleFleetVelocity(fleet, now),
                        TargetPosKm = fleet.TargetPosKm,
                        EstimatedArrivalUtc = fleet.EstimatedArrivalUtc,
                        ConvoySize = fleet.ConvoySize,
                        Signature = FleetSensing.SignatureOf(fleet, _world.PrefabCatalogue, _itemVolumes, _tuning, now),
                        BroadcastRadiusKm = BroadcastKmOf(fleet),
                        MaterialisedEntityId = fleet.MaterialisedEntityId > 0 ? fleet.MaterialisedEntityId : 0L,
                        RoutedStops = ResolveStopPositions(fleet)
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Velocity as a finite difference over the live acceleration profile, rather than a second
        /// implementation of it. The sample naturally decays to zero as the fleet settles onto its
        /// target, which is what a dead-reckoning client should see.
        /// </summary>
        private Vec3 SampleFleetVelocity(PendingFleetAction fleet, DateTime now)
        {
            const double sampleMinutes = 0.1;   // 6 s: long enough to be stable, short enough to be local
            if (IsHeldStill(fleet, now)) return new Vec3();
            Vec3 here = fleet.CurrentPosKm(now, _tuning);
            Vec3 next = fleet.CurrentPosKm(now.AddMinutes(sampleMinutes), _tuning);
            return new Vec3(
                (next.X - here.X) / sampleMinutes,
                (next.Y - here.Y) / sampleMinutes,
                (next.Z - here.Z) / sampleMinutes);
        }

        /// <summary>
        /// The routed part of the itinerary as world positions. A stop either carries an explicit
        /// coordinate (computed navigation legs do) or names a graph node; one that resolves to
        /// neither is dropped rather than drawn at the origin.
        ///
        /// <para>Stops past <see cref="FleetItinerary.RoutedStopCount"/> are not sent at all. They
        /// have no route yet — the resolver plans each leg on arrival at the one before it — so the
        /// only line a client could draw for them is a straight one through whatever body lies
        /// between, which is exactly what it is not.</para>
        /// </summary>
        private List<MapFleetStopInfo> ResolveStopPositions(PendingFleetAction fleet)
        {
            if (fleet.RemainingStops == null || fleet.RemainingStops.Count == 0) return null;
            int routed = FleetItinerary.RoutedStopCount(fleet.RemainingStops);
            if (routed <= 0) return null;
            var stops = new List<MapFleetStopInfo>(routed);
            for (int i = 0; i < routed; i++)
            {
                FleetStop stop = fleet.RemainingStops[i];
                Vec3 position;
                if (stop.HasPosition)
                {
                    position = stop.PositionKm;
                }
                else
                {
                    SpatialNode node;
                    if (string.IsNullOrEmpty(stop.NodeId)
                        || !_world.Graph.Nodes.TryGetValue(stop.NodeId, out node)
                        || node == null)
                    {
                        continue;
                    }
                    position = node.Position;
                }
                stops.Add(new MapFleetStopInfo { PositionKm = position, Kind = stop.Kind });
            }
            return stops;
        }

        public List<FleetGpsInfo> GetActiveFleetPositions(ActionKind kind)
        {
            var result = new List<FleetGpsInfo>();
            if (_world == null) return result;
            // Use real wall-clock time: _world.UtcNow only advances in StepOnce (~10 s intervals),
            // which would freeze GPS positions for the full sim-tick window between updates.
            DateTime now = DateTime.UtcNow;
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    if (fleet.Kind != kind) continue;
                    if (fleet.Intercepted) continue;
                    // Guard shows while actively holding (Resolved but within GuardDuration window).
                    bool isActiveGuard = fleet.IsActiveGuard(now, _world.Tuning);
                    if (fleet.Resolved && !isActiveGuard) continue;
                    // In-transit fleets: skip once past estimated arrival (they'll resolve next tick).
                    if (!fleet.Resolved && fleet.HasArrived(now)) continue;
                    string target = fleet.TargetTag ?? fleet.TargetNodeId ?? "?";
                    string label = isActiveGuard
                        ? fleet.ActorTag + " GUARDING"
                        : fleet.ActorTag + ">" + target + " " + kind.ToString();
                    if (label.Length > 32) label = label.Substring(0, 32);
                    result.Add(new FleetGpsInfo
                    {
                        Kind        = kind,
                        Label       = label,
                        PosKm       = fleet.CurrentPosKm(now, _tuning),
                        EntityId    = fleet.MaterialisedEntityId > 0 ? fleet.MaterialisedEntityId : 0L,
                    });
                }
            }
            return result;
        }

        public string Status()
        {
            if (_world == null) return "sim: not loaded.";
            CentralBankSnapshot bank = _world.SnapshotCentralBank(_states);
            var sb = new StringBuilder();
            sb.Append("sim ").Append(Speed > 0 ? "RUNNING" : "PAUSED").Append(" @ ").Append(Speed.ToString("0.###"))
              .Append(" tick/s; simTick=").Append(_simTick)
              .Append("; activeUtc=").Append(_activeUtc.ToString("u"))
              .Append("; ").Append(_states.Count).Append(" faction(s)")
              .Append("\n  bank ").Append(FormatCentralBankSnapshot(bank));
            foreach (FactionState st in _states.Take(16))
            {
                int infrastructureCount = DevelopedInfrastructureCount(st.Tag);
                int stationCount = PhysicalStationCount(st.Tag);
                sb.Append("\n  ").Append(st.Tag.PadRight(6))
                  .Append(" tr=").Append(_world.GetTreasury(st.Tag).ToString().PadLeft(8))
                  .Append(" nf=").Append(_world.GetNetFlow(st.Tag).ToString().PadLeft(7))
                  .Append(" debt=").Append(_world.GetDebt(st.Tag).ToString().PadLeft(7))
                  .Append(" sdebt=").Append(st.CentralBankSupportDebt.ToString().PadLeft(7))
                  .Append(" src=").Append(_world.Graph.OwnedBy(st.Tag).Count())
                  .Append(" infra=").Append(infrastructureCount)
                  .Append(" legacy=").Append(Math.Max(0, stationCount - infrastructureCount));
                if (FactionLifecycle.IsDormant(st, _world.UtcNow)) sb.Append(" (dormant)");
            }
            return sb.ToString();
        }

        /// <summary>Diagnostic (read-only) — reports how much dispatch-eligible ship stock is
        /// unconditionally stranded (can never pass the Slice C route-feasibility gate for any
        /// destination from its current node). See <see cref="WorldModel.AuditStrandedShipStock"/>.</summary>
        public string StrandedStockReport()
        {
            if (_world == null) return "strandedstock: not loaded.";
            StrandedShipStockResult r = _world.AuditStrandedShipStock();
            var sb = new StringBuilder();
            double pct = r.TotalAvailableUnits > 0 ? 100.0 * r.StrandedUnits / r.TotalAvailableUnits : 0;
            sb.Append("strandedstock: ").Append(r.StrandedUnits).Append("/").Append(r.TotalAvailableUnits)
              .Append(" unit(s) (").Append(pct.ToString("0.0")).Append("%) across ")
              .Append(r.NodesWithStrandedStock).Append(" node(s), ")
              .Append(r.DistinctStrandedPrefabs).Append(" distinct prefab(s)");
            foreach (string line in r.Details) sb.Append("\n  ").Append(line);
            return sb.ToString();
        }

        private int PhysicalStationCount(string factionTag)
        {
            int count = 0;
            foreach (SpatialNode n in _world.Graph.OwnedBy(factionTag))
                if (Economy.HasPhysicalStation(n)) count++;
            return count;
        }

        private int DevelopedInfrastructureCount(string factionTag)
        {
            int count = 0;
            foreach (SpatialNode n in _world.Graph.OwnedBy(factionTag))
                if (Economy.HasDevelopedInfrastructure(n)) count++;
            return count;
        }

        private static string ResolveLiveStationPrefabName(long stationId)
        {
            if (stationId == 0 || Sandbox.Game.World.MySession.Static == null
                || Sandbox.Game.World.MySession.Static.Factions == null)
                return null;

            foreach (var factionPair in Sandbox.Game.World.MySession.Static.Factions)
            {
                var faction = factionPair.Value;
                if (faction == null) continue;
                foreach (var station in faction.Stations)
                {
                    if (station == null || station.StationEntityId != stationId) continue;
                    return station.PrefabName;
                }
            }

            return null;
        }

        private void BackfillDisplayNames(FactionState st)
        {
            if (Sandbox.Game.World.MySession.Static == null) return;
            int filled = 0;
            foreach (SpatialNode n in _world.Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != st.Tag) continue;
                if (!SourceMapping.IsScanBacked(n.Id)) continue;
                if (!string.IsNullOrEmpty(n.DisplayName)) continue;
                if (n.StableStationId == 0) continue;

                // For grid-backed nodes StableStationId is the live grid entity ID.
                VRage.ModAPI.IMyEntity entity;
                if (Sandbox.ModAPI.MyAPIGateway.Entities.TryGetEntityById(n.StableStationId, out entity))
                {
                    var grid = entity as Sandbox.Game.Entities.MyCubeGrid;
                    if (grid != null) { n.DisplayName = grid.DisplayName; filled++; continue; }
                }

                // For economy-station nodes StableStationId is MyStation.Id — name lives in the savefile.
                string name = ResolveEconomyStationDisplayName(n.StableStationId);
                if (!string.IsNullOrEmpty(name)) { n.DisplayName = name; filled++; }
            }
            if (filled > 0)
                _log.Write("BACKFILL DisplayName: " + st.Tag + " filled " + filled + " node(s); will persist on next Flush");
        }

        private static string ResolveEconomyStationDisplayName(long stationMyId)
        {
            if (stationMyId == 0 || Sandbox.Game.World.MySession.Static == null
                || Sandbox.Game.World.MySession.Static.Factions == null)
                return null;
            foreach (var kv in Sandbox.Game.World.MySession.Static.Factions)
            {
                var f = kv.Value;
                if (f == null) continue;
                foreach (var station in f.Stations)
                {
                    if (station == null || station.Id != stationMyId) continue;
                    return station.Name;
                }
            }
            return null;
        }

        // ---- Perf snapshot helpers -------------------------------------------------------

        private void WritePerfSnapshot(DateTime now)
        {
            double freq = Stopwatch.Frequency;
            double windowSec = (now - _perfWindowStartUtc).TotalSeconds;
            if (windowSec <= 0) windowSec = 1;

            // Controlled grids summed over every flight tick, and the escorts' share: flight.totalMs
            // divided by gridTicks is what one controlled grid costs per tick.
            long flightGridTicks = 0, flightEscortTicks = 0;
            int flightMaxControlled = 0, flightCapRefusals = 0;
            var flightSpawner = _spawner as VanillaFleetSpawner;
            if (flightSpawner != null)
                flightSpawner.TakeFlightTickCounts(out flightGridTicks, out flightEscortTicks,
                                                   out flightMaxControlled, out flightCapRefusals);

            string summary = "[" + windowSec.ToString("0.0") + "s]"
                + " brain:"  + FmtPW(_perfBrain,       freq)
                + " prod:"   + FmtPW(_perfProduction,  freq)
                + " ops:"    + FmtPW(_perfOps,         freq)
                + " yard:"   + FmtPW(_perfShipyard,    freq)
                + " fleet:"  + FmtPW(_perfFleet,       freq)
                + " trade:"  + FmtPW(_perfTrade,       freq)
                + " stock:"  + FmtPW(_perfStoreStock,  freq)
                + " global:" + FmtPW(_perfEconomy,     freq)
                + " [snap:"    + FmtPW(_perfEconSnapshot,    freq)
                + " income:"   + FmtPW(_perfEconIncome,      freq)
                + " diplo:"    + FmtPW(_perfEconDiplomacy,   freq)
                + " decay:"    + FmtPW(_perfEconDecay,       freq)
                + " rename:"   + FmtPW(_perfEconRename,      freq)
                + " knowledge:" + FmtPW(_perfEconKnowledge, freq)
                + " arrive:"   + FmtPW(_perfEconArrivals,    freq)
                + " diploEvt:" + FmtPW(_perfEconDiploEvents, freq)
                + " lifecyc:"  + FmtPW(_perfEconLifecycle,   freq) + "]"
                + " obs:"    + FmtPW(_perfObs,         freq)
                + " flight:" + FmtPW(_perfFlight,      freq)
                + " flightGrids:" + flightGridTicks + "/" + flightEscortTicks
                + " max=" + flightMaxControlled + " capped=" + flightCapRefusals
                + " stObs:"  + FmtPW(_perfStObs,       freq)
                + " astObs:" + FmtPW(_perfAstObs,      freq)
                + " contractGen:" + FmtFW(_perfContractGen)
                + " storeTxn:"    + FmtFW(_perfStoreTxn)
                + " frame:"  + FmtFW(_perfFrame);
            _log.Write("perf " + summary);

            try
            {
                string path = Path.Combine(_store.BaseDir, "perf.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(new
                {
                    at            = now.ToString("o"),
                    windowSeconds = Math.Round(windowSec, 1),
                    windowTargetSeconds = Math.Round(GetPerfWindowSeconds(), 1),
                    brain  = PW(_perfBrain,       freq),
                    brainPhases = new
                    {
                        think = PW(_perfBrainThink, freq),
                        thinkStages = new
                        {
                            sense             = PW(_perfThinkSense,             freq),
                            tradePriority     = PW(_perfThinkTradePriority,     freq),
                            reinforcePriority = PW(_perfThinkReinforcePriority, freq),
                            threatBonus       = PW(_perfThinkThreatBonus,       freq),
                            generate          = PW(_perfThinkGenerate,          freq),
                            score             = PW(_perfThinkScore,             freq),
                            select            = PW(_perfThinkSelect,            freq),
                        },
                        enact = PW(_perfBrainEnact, freq),
                        log   = PW(_perfBrainLog,   freq),
                    },
                    prod   = PW(_perfProduction,  freq),
                    ops    = PW(_perfOps,         freq),
                    yard   = PW(_perfShipyard,    freq),
                    fleet  = PW(_perfFleet,       freq),
                    trade  = PW(_perfTrade,       freq),
                    stock  = PW(_perfStoreStock,  freq),
                    econ   = PW(_perfEconomy,     freq),
                    econPhases = new
                    {
                        snapshot    = PW(_perfEconSnapshot,    freq),
                        income      = PW(_perfEconIncome,      freq),
                        diplomacy   = PW(_perfEconDiplomacy,   freq),
                        decay       = PW(_perfEconDecay,       freq),
                        rename      = PW(_perfEconRename,      freq),
                        knowledge   = PW(_perfEconKnowledge,   freq),
                        arrivals    = PW(_perfEconArrivals,    freq),
                        diploEvents = PW(_perfEconDiploEvents, freq),
                        lifecycle   = PW(_perfEconLifecycle,   freq),
                    },
                    obs    = PW(_perfObs,         freq),
                    flight = PW(_perfFlight,      freq),
                    flightGrids = new
                    {
                        gridTicks = flightGridTicks,
                        escortTicks = flightEscortTicks,
                        maxControlled = flightMaxControlled,
                        capRefusals = flightCapRefusals,
                    },
                    stObs  = PW(_perfStObs,       freq),
                    astObs = PW(_perfAstObs,      freq),
                    flushTotal      = PW(_perfFlushTotal,      freq),
                    flushBrains     = PW(_perfFlushBrains,     freq),
                    flushProduction = PW(_perfFlushProduction, freq),
                    flushSnapshot   = PW(_perfFlushSnapshot,   freq),
                    productionTelemetry = PT(_perfProductionTelemetry, freq),
                    operationsTelemetry = OT(_perfOperationsTelemetry, freq),
                    tradeTelemetry = TT(_perfTradeTelemetry, freq),
                    scheduler = new
                    {
                        softDeadlineMs = GetFrameSoftBudgetMilliseconds(),
                        schedulerEnabled = _tuning.FrameSchedulerEnabled,
                        sharedBudgetEnabled = _scheduler != null && _scheduler.SharedBudgetEnabled,
                        admittedUnitCount = _scheduler == null ? 0 : _scheduler.AdmittedUnitCount,
                        framesWithNoAdmission = _scheduler == null ? 0 : _scheduler.FramesWithNoAdmission,
                        deniedByBudgetCount = _scheduler == null ? 0 : _scheduler.DeniedByBudgetCount,
                        forcedAdmissionCount = _scheduler == null ? 0 : _scheduler.ForcedAdmissionCount,
                        maxUnitsInOneFrame = _scheduler == null ? 0 : _scheduler.MaxUnitsInOneFrame,
                        // frame.maxMs divided by the largest single actor unit. This is what a shared
                        // budget can actually deliver -- "no two units stacked" -- and unlike an
                        // over-budget count it is not pinned at ~100% and cannot be improved by
                        // inflating cadences.
                        noStackRatio = SchedulerNoStackRatio(),
                        actors = SchedulerActorRows(freq),
                        rechunkQueue = SchedulerRechunkQueue(),
                        notDueFrames = _deadlineNotDueFrames,
                        dueUnsafeCount = _deadlineDueUnsafeCount,
                        // ProductionManager and OperationsManager have had a resumable (multi-frame)
                        // cycle since Slice 3; DrainGlobalPass gained one too (2026-08-17, Slice 2 of
                        // the global-pass econ cursor plan); DrainFlush gained one too (frame-budget
                        // plan Task 12); TradeManager gained one too (Slice B of the TradeManager
                        // resumable-cycle plan), so this is 0-5. oldestDeferredAgeMs is the
                        // longest-waiting of the five (the smallest/earliest DateTime), since that is
                        // the one closest to actually starving other managers of a manager-slot frame.
                        deferredWorkCount = (_productionSweepDueUtc == null ? 0 : 1) + (_operationsSweepDueUtc == null ? 0 : 1)
                            + (_globalPassSweepDueUtc == null ? 0 : 1) + (_flushSweepDueUtc == null ? 0 : 1)
                            + (_tradeSweepDueUtc == null ? 0 : 1),
                        oldestDeferredAgeMs = Math.Round((now - OldestDeferredSweepDueUtc(now)).TotalMilliseconds, 1),
                        expiredBeforeUnsafeCount = _deadlineExpiredBeforeUnsafeCount,
                        // Diagnostics only (live troubleshooting, 2026-08-17) -- remove once the
                        // global-pass live investigation is closed out.
                        globalPassPhase = _globalPassPhase == null ? "idle" : _globalPassPhase.Value.ToString(),
                        globalPassSnapshotBuildIndex = _world == null ? 0 : _world.DiagnosticSnapshotBuildIndex,
                        globalPassSnapshotBuildTotal = _world == null ? 0 : _world.DiagnosticSnapshotBuildTotal,
                        globalPassSnapshotRestarts = _globalPassSnapshotRestarts,
                        globalPassIncomePhase = _world == null ? "idle" : _world.DiagnosticIncomePassPhase,
                        globalPassIncomeIndex = _world == null ? 0 : _world.DiagnosticIncomePassIndex,
                        globalPassIncomeTotal = _world == null ? 0 : _world.DiagnosticIncomePassTotal,
                        globalPassElapsedMs = Math.Round(_globalPassElapsedTicks / freq * 1000.0, 1),
                    },
                    // frame is the Factions-owned Update() body; baseUpdate is external Torch/SE
                    // correlation only and is deliberately not charged to the 0.5 ms contract.
                    frame = FW(_perfFrame),
                    host = new
                    {
                        baseUpdate = FW(_perfBaseUpdate),
                        instrumentation = FW(_perfInstrumentation),
                        playerCache = FW(_perfPlayerCache),
                        runtimeDispatch = FW(_perfRuntimeDispatch),
                        fleetObservation = FW(_perfFleetObservation),
                        stationObservation = FW(_perfStationObservation),
                        asteroidObservation = FW(_perfAsteroidObservation),
                        featureRock = FW(_perfFeatureRock),
                        price = FW(_perfPrice),
                        planet = FW(_perfPlanet),
                        bridge = FW(_perfBridge),
                        presence = FW(_perfPresence),
                        gps = FW(_perfGps),
                        storeRefresh = FW(_perfStoreRefresh),
                        contractGen = FW(_perfContractGen),
                        storeTxn = FW(_perfStoreTxn),
                    },
                }));
            }
            catch { /* best-effort */ }

            ResetPerfWindows();
            _perfWindowStartUtc = now;
        }

        public string GetPerfSummary()
        {
            double freq = Stopwatch.Frequency;
            double windowSec = (DateTime.UtcNow - _perfWindowStartUtc).TotalSeconds;
            if (windowSec <= 0) windowSec = 1;
            return "perf [" + windowSec.ToString("0.0") + "s]"
                + " brain:"  + FmtPW(_perfBrain,       freq)
                + " prod:"   + FmtPW(_perfProduction,  freq)
                + " ops:"    + FmtPW(_perfOps,         freq)
                + " yard:"   + FmtPW(_perfShipyard,    freq)
                + " fleet:"  + FmtPW(_perfFleet,       freq)
                + " trade:"  + FmtPW(_perfTrade,       freq)
                + " stock:"  + FmtPW(_perfStoreStock,  freq)
                + " global:" + FmtPW(_perfEconomy,     freq)
                + " [snap:"    + FmtPW(_perfEconSnapshot,    freq)
                + " income:"   + FmtPW(_perfEconIncome,      freq)
                + " diplo:"    + FmtPW(_perfEconDiplomacy,   freq)
                + " decay:"    + FmtPW(_perfEconDecay,       freq)
                + " rename:"   + FmtPW(_perfEconRename,      freq)
                + " knowledge:" + FmtPW(_perfEconKnowledge, freq)
                + " arrive:"   + FmtPW(_perfEconArrivals,    freq)
                + " diploEvt:" + FmtPW(_perfEconDiploEvents, freq)
                + " lifecyc:"  + FmtPW(_perfEconLifecycle,   freq) + "]"
                + " obs:"    + FmtPW(_perfObs,         freq)
                + " flight:" + FmtPW(_perfFlight,      freq)
                + " stObs:"  + FmtPW(_perfStObs,       freq)
                + " astObs:" + FmtPW(_perfAstObs,      freq)
                + " contractGen:" + FmtFW(_perfContractGen)
                + " storeTxn:"    + FmtFW(_perfStoreTxn);
        }

        private void ResetPerfWindows()
        {
            _perfBrain.Reset();      _perfProduction.Reset(); _perfOps.Reset();
            _perfBrainThink.Reset();  _perfBrainEnact.Reset(); _perfBrainLog.Reset();
            _perfThinkSense.Reset();  _perfThinkTradePriority.Reset(); _perfThinkReinforcePriority.Reset();
            _perfThinkThreatBonus.Reset(); _perfThinkGenerate.Reset(); _perfThinkScore.Reset(); _perfThinkSelect.Reset();
            _perfShipyard.Reset();   _perfFleet.Reset();      _perfTrade.Reset();   _perfStoreStock.Reset();
            _perfEconomy.Reset();    _perfObs.Reset();        _perfStObs.Reset();    _perfAstObs.Reset();
            _perfFlight.Reset();
            if (_scheduler != null) _scheduler.ResetWindow();
            _perfEconSnapshot.Reset(); _perfEconIncome.Reset();      _perfEconDiplomacy.Reset(); _perfEconDecay.Reset();
            _perfEconRename.Reset();   _perfEconArrivals.Reset();    _perfEconDiploEvents.Reset(); _perfEconLifecycle.Reset();
            _perfEconKnowledge.Reset();
            _perfFlushTotal.Reset(); _perfFlushBrains.Reset(); _perfFlushProduction.Reset(); _perfFlushSnapshot.Reset();
            _perfFrame.Reset();      _perfInstrumentation.Reset(); _perfBaseUpdate.Reset(); _perfPlayerCache.Reset();
            _perfRuntimeDispatch.Reset(); _perfFleetObservation.Reset(); _perfStationObservation.Reset();
            _perfAsteroidObservation.Reset();
            _perfFeatureRock.Reset();
            _perfPrice.Reset();      _perfPlanet.Reset();     _perfBridge.Reset();
            _perfPresence.Reset();   _perfGps.Reset();        _perfStoreRefresh.Reset();
            _perfContractGen.Reset(); _perfStoreTxn.Reset();
            _perfProductionTelemetry.Reset();
            _perfOperationsTelemetry.Reset();
            _perfTradeTelemetry.Reset();
            _deadlineNotDueFrames = 0;
            _deadlineDueUnsafeCount = 0;
            _deadlineExpiredBeforeUnsafeCount = 0;
        }

        /// <summary>Records one <see cref="FactionContractStrategy.GenerateContract"/> call — invoked
        /// directly by vanilla's contract-board refill loop, outside this runtime's own update cycle,
        /// so this is the only measurement point available for it.</summary>
        internal void RecordContractGenTiming(long elapsedTicks) { _perfContractGen.Record(elapsedTicks); }

        /// <summary>Records one player store transaction (<c>StoreTransactionPatches</c> Harmony
        /// postfix → <c>FactionsPlugin.OnStationItemTransacted</c>), distinct from
        /// <see cref="_perfStoreRefresh"/> which only times the periodic <c>RefreshAll</c> pass.</summary>
        internal void RecordStoreTxnTiming(long elapsedTicks) { _perfStoreTxn.Record(elapsedTicks); }

        /// <summary>The earliest (longest-waiting) of the resumable managers' due timestamps, or
        /// <paramref name="now"/> (zero age) if none has a sweep in flight. See the <c>scheduler</c>
        /// perf.json object's <c>oldestDeferredAgeMs</c> field.</summary>

        /// <summary>Per-actor scheduling and cost rows for perf.json.</summary>
        private object[] SchedulerActorRows(double freq)
        {
            if (_scheduler == null) return new object[0];
            var rows = new object[_scheduler.ActorCount];
            for (int i = 0; i < _scheduler.ActorCount; i++)
            {
                FrameActorStats st = _scheduler.GetStats(i);
                FrameTimingWindow w = _scheduler.WindowOf(i);
                rows[i] = new
                {
                    name = st.Name,
                    intervalMs = Math.Round(st.IntervalSeconds * 1000.0, 1),
                    estimateMs = Math.Round(st.EstimateMilliseconds, 3),
                    calls = w.CallCount,
                    avgMs = Math.Round(w.AverageMilliseconds(), 3),
                    maxMs = Math.Round(w.MaxMilliseconds(), 3),
                    p99Ms = Math.Round(w.PercentileMilliseconds(0.99), 3),
                    overBudgetCount = w.OverBudgetCount,
                    excessMs = Math.Round(w.ExcessMilliseconds(), 1),
                    admissions = st.Admissions,
                    completions = st.Completions,
                    yields = st.Yields,
                    skips = st.Skips,
                    notDue = st.NotDue,
                    forcedAdmissions = st.ForcedAdmissions,
                    deniedByBudget = st.DeniedByBudget,
                    maxDeferralMs = Math.Round(st.MaxDeferralMilliseconds, 1),
                    estErrorAvgMs = Math.Round(st.EstimateErrorAvgMilliseconds, 3),
                    estErrorMaxMs = Math.Round(st.EstimateErrorMaxMilliseconds, 3),
                    sweepInProgress = st.SweepInProgress,
                };
            }
            return rows;
        }

        /// <summary>Actor names ranked by attributable over-budget milliseconds, worst first. This is
        /// the queue that names what to re-chunk next: an average is blind to call count and a maximum
        /// is blind to frequency, so neither orders a rare fat unit against a frequent thin one.</summary>
        private string[] SchedulerRechunkQueue()
        {
            if (_scheduler == null) return new string[0];
            int n = _scheduler.ActorCount;
            var names = new string[n];
            var excess = new double[n];
            for (int i = 0; i < n; i++)
            {
                names[i] = _scheduler.GetStats(i).Name;
                excess[i] = _scheduler.WindowOf(i).ExcessMilliseconds();
            }
            // Selection sort over <= 16 entries, top 5; avoids allocating a comparer every flush.
            int take = n < 5 ? n : 5;
            var result = new string[take];
            for (int k = 0; k < take; k++)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                    if (excess[i] > 0.0 && (best < 0 || excess[i] > excess[best])) best = i;
                if (best < 0) { Array.Resize(ref result, k); break; }
                result[k] = names[best] + "=" + excess[best].ToString("0");
                excess[best] = 0.0;
            }
            return result;
        }

        private double SchedulerNoStackRatio()
        {
            if (_scheduler == null) return 0.0;
            double worstActor = 0.0;
            for (int i = 0; i < _scheduler.ActorCount; i++)
            {
                double m = _scheduler.WindowOf(i).MaxMilliseconds();
                if (m > worstActor) worstActor = m;
            }
            if (worstActor <= 0.0) return 0.0;
            return Math.Round(_perfFrame.MaxMilliseconds() / worstActor, 3);
        }

        private DateTime OldestDeferredSweepDueUtc(DateTime now)
        {
            // Five inline comparisons rather than a foreach over a fresh array: this runs from the
            // perf snapshot, so an allocation here is pure waste.
            DateTime oldest = now;
            bool any = false;
            Consider(_productionSweepDueUtc, ref oldest, ref any);
            Consider(_operationsSweepDueUtc, ref oldest, ref any);
            Consider(_globalPassSweepDueUtc, ref oldest, ref any);
            Consider(_flushSweepDueUtc,      ref oldest, ref any);
            Consider(_tradeSweepDueUtc,      ref oldest, ref any);
            return oldest;
        }

        private static void Consider(DateTime? due, ref DateTime oldest, ref bool any)
        {
            if (due == null) return;
            if (!any || due.Value < oldest) oldest = due.Value;
            any = true;
        }

        private static object PT(ProductionTelemetryWindow w, double frequency)
        {
            return new
            {
                calls = w.Calls,
                factions = w.Factions,
                ownedNodes = w.OwnedNodes,
                capabilityBanks = w.CapabilityBanks,
                elapsedClamps = w.ElapsedClamps,
                slotsVisited = w.SlotsVisited,
                recipesExamined = w.RecipesExamined,
                recipesSkippedNoDemand = w.RecipesSkippedNoDemand,
                blockedRecipes = w.BlockedRecipes,
                blockedInputPosts = w.BlockedInputPosts,
                demandPosts = w.DemandPosts,
                demandRefreshes = w.DemandRefreshes,
                catalogOffersScanned = w.CatalogOffersScanned,
                investmentPasses = w.InvestmentPasses,
                refinementPasses = w.RefinementPasses,
                restarts = w.Restarts,
                maxCycleAgeMs = Math.Round(w.MaxCycleAgeTicks / frequency * 1000.0, 3),
                preparationMs = Math.Round(w.PreparationTicks / frequency * 1000.0, 3),
                capabilityBankMs = Math.Round(w.CapabilityBankTicks / frequency * 1000.0, 3),
                blockedDemandMs = Math.Round(w.BlockedDemandTicks / frequency * 1000.0, 3),
                investmentRefinementMs = Math.Round(w.InvestmentRefinementTicks / frequency * 1000.0, 3),
                catalogMs = Math.Round(w.CatalogTicks / frequency * 1000.0, 3),
            };
        }

        private static object OT(OperationsTelemetryWindow w, double frequency)
        {
            return new
            {
                calls = w.Calls, factions = w.Factions, ownedNodes = w.OwnedNodes, demands = w.Demands,
                itemGroups = w.ItemGroups, targets = w.Targets, candidateSources = w.CandidateSources,
                distanceOrderBuilds = w.DistanceOrderBuilds, vectors = w.Vectors,
                routeCacheHits = w.RouteCacheHits, routeCacheMisses = w.RouteCacheMisses,
                directDispatches = w.DirectDispatches, pickupDispatches = w.PickupDispatches,
                noShipOutcomes = w.NoShipOutcomes, routeInfeasibleOutcomes = w.RouteInfeasibleOutcomes, loadCargoFailures = w.LoadCargoFailures,
                alternateSourceScans = w.AlternateSourceScans, shipyardScans = w.ShipyardScans, shipSuitabilityChecks = w.ShipSuitabilityChecks,
                fallbackCandidates = w.FallbackCandidates, uniqueFallbackKeys = w.UniqueFallbackKeys, demandPosts = w.DemandPosts, demandRefreshes = w.DemandRefreshes,
                preparationMs = Math.Round(w.PreparationTicks / frequency * 1000.0, 3), demandHistoryMs = Math.Round(w.DemandHistoryTicks / frequency * 1000.0, 3),
                claimIndexMs = Math.Round(w.ClaimIndexTicks / frequency * 1000.0, 3), targetMatchingMs = Math.Round(w.TargetMatchingTicks / frequency * 1000.0, 3),
                routePlanningMs = Math.Round(w.RoutePlanningTicks / frequency * 1000.0, 3), directDispatchMs = Math.Round(w.DirectDispatchTicks / frequency * 1000.0, 3),
                alternatePickupMs = Math.Round(w.AlternatePickupTicks / frequency * 1000.0, 3),
                alternateScanMs = Math.Round(w.AlternateScanTicks / frequency * 1000.0, 3),
                alternateRevalidateMs = Math.Round(w.AlternateRevalidateTicks / frequency * 1000.0, 3),
                shipyardFallbackMs = Math.Round(w.ShipyardFallbackTicks / frequency * 1000.0, 3),
                fallbackPublicationMs = Math.Round(w.FallbackPublicationTicks / frequency * 1000.0, 3),
                yields = w.Yields, restarts = w.Restarts, sweepsCompleted = w.SweepsCompleted,
            };
        }

        private static object TT(TradeTelemetryWindow w, double frequency)
        {
            return new
            {
                calls = w.Calls, factions = w.Factions, eligibleFactions = w.EligibleFactions,
                partners = w.Partners, unservedSeen = w.UnservedSeen, vectors = w.Vectors,
                dispatched = w.Dispatched, procured = w.Procured, noShip = w.NoShip,
                refusedConsent = w.RefusedConsent, claimed = w.Claimed, assessRefused = w.AssessRefused,
                tier1 = w.Tier1, tier2 = w.Tier2, tier3 = w.Tier3, budgetBlocked = w.BudgetBlocked,
                eligibilityFilterMs = Math.Round(w.EligibilityFilterTicks / frequency * 1000.0, 3),
                dispatchMs = Math.Round(w.DispatchTicks / frequency * 1000.0, 3),
                yields = w.Yields, restarts = w.Restarts, sweepsCompleted = w.SweepsCompleted,
            };
        }

        private static string FmtPW(PerfWindow w, double freq)
        {
            if (w.CallCount == 0) return "0x";
            double avg = w.TotalTicks / freq / w.CallCount * 1000.0;
            double max = w.MaxTicks   / freq               * 1000.0;
            return w.CallCount + "x " + avg.ToString("0.00") + "/" + max.ToString("0.00") + "ms";
        }

        /// <summary>totalMs is the bucket's whole share of the window and is the number that ranks
        /// actors against each other; avg and max describe one call. For a resumable manager that
        /// yields hundreds of times per sweep the two orderings invert, and avg alone cannot be
        /// multiplied back out client-side because it is rounded here.</summary>
        private static object PW(PerfWindow w, double freq)
        {
            double avg = w.CallCount == 0 ? 0.0 : Math.Round(w.TotalTicks / freq / w.CallCount * 1000.0, 3);
            double max = Math.Round(w.MaxTicks / freq * 1000.0, 3);
            double total = Math.Round(w.TotalTicks / freq * 1000.0, 3);
            return new { calls = w.CallCount, avgMs = avg, maxMs = max, totalMs = total };
        }

        private static string FmtFW(FrameTimingWindow w)
        {
            if (w.CallCount == 0) return "0x";
            return w.CallCount + "x " + w.AverageMilliseconds().ToString("0.000")
                + "/" + w.MaxMilliseconds().ToString("0.000") + "ms"
                + " p99=" + w.PercentileMilliseconds(0.99).ToString("0.000")
                + " over=" + w.OverBudgetCount;
        }

        private static object FW(FrameTimingWindow w)
        {
            return new
            {
                calls = w.CallCount,
                sampledFrames = w.SampleCount,
                budgetMs = Math.Round(w.BudgetMilliseconds, 3),
                totalMs = Math.Round(w.TotalMilliseconds(), 3),
                avgMs = Math.Round(w.AverageMilliseconds(), 3),
                maxMs = Math.Round(w.MaxMilliseconds(), 3),
                p50Ms = Math.Round(w.PercentileMilliseconds(0.50), 3),
                p95Ms = Math.Round(w.PercentileMilliseconds(0.95), 3),
                p99Ms = Math.Round(w.PercentileMilliseconds(0.99), 3),
                overBudgetCount = w.OverBudgetCount,
            };
        }

        private static string FormatCentralBankSnapshot(CentralBankSnapshot snap)
        {
            return "money=" + snap.CurrentMoneySupply
                   + " target=" + snap.TargetMoneySupply
                   + " gap=" + snap.MoneySupplyGap
                   + " coll=" + snap.WorldCollateralValue
                   + " anchor=" + snap.AnchorCollateralValue
                   + " policy=" + (string.IsNullOrEmpty(snap.LastPolicyMode) ? "none" : snap.LastPolicyMode)
                   + " levied=" + snap.LastPolicyLevied
                   + " rescued=" + snap.LastPolicyRescued
                   + " rr=" + snap.ReserveRatio.ToString("0.###")
                   + " terr=" + snap.TerritoryNodeCount
                   + " founded=" + snap.FoundedNodeCount
                   + " cap=" + snap.CapabilityLevelCount
                   + " ships=" + snap.ShipCount
                   + " (idle=" + snap.IdleShipCount + " flight=" + snap.InFlightShipCount + ")";
        }
        private struct PerfWindow
        {
            public long TotalTicks;
            public long MaxTicks;
            public int  CallCount;

            public void Record(long elapsed)
            {
                TotalTicks += elapsed;
                if (elapsed > MaxTicks) MaxTicks = elapsed;
                CallCount++;
            }

            public void Reset() { TotalTicks = 0; MaxTicks = 0; CallCount = 0; }
        }
    }

    /// <summary>DTO returned by <see cref="FactionRuntime.GetActiveFleetPositions"/>.</summary>
    public struct FleetGpsInfo
    {
        /// <summary>Kind of fleet action (Trade, Raid, Patrol).</summary>
        public ActionKind Kind;
        /// <summary>HUD label (≤32 chars — SE GPS name limit).</summary>
        public string Label;
        /// <summary>Interpolated world position in km.</summary>
        public Vec3 PosKm;
        /// <summary>0 while the fleet is not yet materialised; the grid entity ID while it is.</summary>
        public long EntityId;
    }
}
