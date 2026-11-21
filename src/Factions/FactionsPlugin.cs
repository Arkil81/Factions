using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Globalization;
using HarmonyLib;
using NLog;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;   // MyPlayerCollection (grid ownership)
using Sandbox.Game.Screens.Helpers;  // MyGps
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Commands;
using Torch.Managers;
using Torch.Managers.ChatManager;
using Torch.Session;
using Factions.Core;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// Factions — NPC-faction economic "brain" host plugin. Deliberately standalone: no hooks into
    /// other plugins yet. It owns the persistent registry (Factions.Core <see cref="FactionStore"/>)
    /// and the SE-facing host features. The decision scheduler lands in Update() next.
    /// </summary>
    public class FactionsPlugin : TorchPluginBase
    {
        public const string PluginName = "Factions";

        /// <summary>Reported to linked FactionsUI clients on uplink, so a version mismatch is
        /// visible in the client log rather than inferred from behaviour.</summary>
        public static readonly string PluginVersion =
            typeof(FactionsPlugin).Assembly.GetName().Version.ToString();
        public static readonly Logger Log = LogManager.GetLogger(PluginName);
        public static FactionsPlugin Instance { get; private set; }

        private TorchSessionManager _sessionManager;

        public string StorageDir { get; private set; }
        /// <summary>This Torch instance's own plugin storage. Holds tuning.json and the
        /// command bridge whatever <see cref="FactionTuning.DataDirectory"/> says.</summary>
        public string InstanceDir { get; private set; }
        public FactionStore Store { get; private set; }
        /// <summary>Always rooted at <see cref="InstanceDir"/>. tuning.json is per-instance
        /// because it names the data directory, and LoadTuning writes the merged file back —
        /// reading it through <see cref="Store"/> would relocate config on the first reload.</summary>
        public FactionStore ConfigStore { get; private set; }
        public FactionRuntime Runtime { get { return _runtime; } }

        /// <summary>This server's own tuning, loaded at session start whether or not it simulates.</summary>
        internal FactionTuning Tuning { get { return _tuning; } }

        /// <summary>Restoration at the Services Terminal; null until the session loads.</summary>
        internal RestorationService Restoration { get { return _restorationService ?? _agentRestoration; } }

        // Command bridge: stdin into Torch -console is unreliable, so commands arrive via a watched
        // file instead. One command per line in <storage>/Factions/bridge/inbox.txt is run with server
        // authority on the game thread; an ack line is written to outbox.txt. We POLL (not FileSystem-
        // Watcher) so the per-tick cost is a single counter compare — the filesystem is only touched
        // once every BridgePollTicks, and only when an inbox file is actually present.
        private const int BridgePollTicks = 120;   // ~2s at 60 ticks/s
        private const int GpsUpdateTicks  = 300;   // ~5s at 60 ticks/s
        private const double DefaultStoreRefreshIntervalSeconds = 120.0;
        private int _tick;
        private int _gpsTick;
        private int _storeTick;
        private int _storeRefreshTicks = (int)(DefaultStoreRefreshIntervalSeconds * 60);

        /// <summary>Store rebuilding admitted per frame; at least one station whatever it costs.</summary>
        private const double StoreRefreshBudgetMs = 3.0;
        private volatile bool _sessionReady;
        // Set when a Nexus cluster exists and this server is not its simulation authority.
        // The bridge keeps polling so an operator can ask a deferred server why it is idle.
        private volatile bool _simulationDeferred;
        /// <summary>What this process does for the cluster; decided beside <see cref="_simulationDeferred"/>.</summary>
        private NexusRole _nexusRole = NexusRole.Standalone;
        /// <summary>Server-to-server transport, registered at init so Nexus's one-shot API publish is
        /// never missed. Null-safe everywhere: a standalone server carries it and never uses it.</summary>
        private NexusRelay _nexusRelay;
        private bool _authorityOnline;
        private int _relayTick;
        /// <summary>Authority: where the players on the cluster's other servers are, as their agents
        /// report them. Read by the <c>factions nexus</c> verb only; no observer reads it yet,
        /// because every observer spawns on this server and a remote position lies in another
        /// server's sector.</summary>
        private RemotePlayerPositions _remotePlayers = new RemotePlayerPositions();
        /// <summary>Agent: materialises rock (and, later, stations) for this server's sectors from
        /// the authority's snapshot. Null on every other role.</summary>
        private AgentHands _agentHands;
        /// <summary>Authority: the snapshot last built, its content hash, and what each agent was
        /// last sent. The sequence is clock-seeded so an authority restart never sends one older
        /// than an agent already holds.</summary>
        private long _snapshotSequence;
        private ulong _snapshotHash;
        private byte[] _snapshotPayload;
        private readonly Dictionary<byte, long> _snapshotSentSeq = new Dictionary<byte, long>();
        private readonly Dictionary<byte, DateTime> _snapshotSentUtc = new Dictionary<byte, DateTime>();
        private NexusPresence _nexusPresence = NexusPresence.Absent;
        private int _nexusServerId;
        private string _nexusDetail = "not probed";
        private string _authorityReason = "not probed";
        private string _bridgeInboxPath;
        private string _bridgeOutboxPath;

        private FactionRuntime _runtime;
        private DecisionLog _decisionLog;
        private readonly FactionTuning _tuning = new FactionTuning();
        private Harmony _harmony;   // shared with the runtime (seed/floor/income knobs)
        private PrefabBookValueCache _prefabBookValues;
        private PlayerPresenceManager _presenceManager;
        private ClaimOfferManager _claimOfferManager;
        private ConquerOfferManager _conquerOfferManager;
        private OfferReplyRouter _offerReplyRouter;
        private PresenceReinforceManager _presenceReinforceManager;
        private StationStoreManager   _storeManager;
        private GlobalPriceUpdater    _priceUpdater;
        private PlanetSnapshotWriter  _planetSnapshotWriter;
        // Held only so `factions world apply` can invalidate the planet index it built at
        // boot -- that verb is the one path in this process that can create a planet
        // after the index exists. See SePlanetCatalogue.Invalidate.
        private SePlanetCatalogue     _planetCatalogue;
        private SeOreGeography        _oreGeography;
        private CompositeOreGeography _worldGeography;
        private WorldFeaturePlacement _featurePlacement;

        private PlayerIndexWriter     _playerIndexWriter;
        private PlayerReputationManager _reputationManager;
        private PlayerScannerManager  _playerScannerManager;
        private TrespassHailer        _trespassHailer;
        /// <summary>Displacement speed for the trespass pass: physics velocity reads ~0 in FSD
        /// supercruise. Its own book, on the pass's own baseline, rather than the signature
        /// sampler's last position, which another pass overwrites.</summary>
        private readonly Factions.Core.TravelSpeedBook _trespassTravel = new Factions.Core.TravelSpeedBook();
        private RespawnPlacement      _respawnPlacement;
        private NodeSpawnManager      _nodeSpawnManager;
        private PlayerMapManager      _playerMapManager;
        private PlayerExpandManager   _playerExpandManager;
        private PlayerMarketManager   _playerMarketManager;
        private Factions.Core.MarketMemory _marketMemory;
        private PlayerAssetsManager   _playerAssetsManager;
        private PlayerNodeDetailManager _playerNodeDetailManager;
        private PlayerNodeTransferManager _playerNodeTransferManager;
        private PlayerShipAssetsManager _playerShipAssetsManager;
        private DockSupplyManager _dockSupplyManager;
        private RestorationService _restorationService;
        private PlayerStationRefitManager _playerStationRefitManager;
        private PlayerEventsManager  _playerEventsManager;
        private PlayerRockManager    _playerRockManager;
        private PlayerStandingOrdersManager _playerStandingOrdersManager;
        private PlayerFleetDoctrineManager _playerFleetDoctrineManager;
        private PlayerDesignVersionManager _playerDesignVersionManager;
        private PlayerTacticsManager _playerTacticsManager;
        private PlayerShelfManager _playerShelfManager;
        private PlayerBankSalesManager _playerBankSalesManager;
        private PlayerDiplomacyManager _playerDiplomacyManager;
        private PlayerWingManager _playerWingManager;
        private PlayerCrewOrderManager _playerCrewOrderManager;
        private AgentTerminalSurfaces _agentTerminalSurfaces;
        private AgentCaptures _agentCaptures;
        /// <summary>Agent: the capture generation of the snapshot whose designs are loaded; null
        /// before the first snapshot. A load is in flight while <see cref="_agentCaptureLoad"/> is set.</summary>
        private int? _agentCapturesLoaded;
        private System.Threading.Tasks.Task<CustomPrefabStore> _agentCaptureLoad;
        private int _agentCaptureLoading;
        private string _agentDataDir;

        /// <summary>Agent: the capture relay; null elsewhere.</summary>
        internal AgentCaptures AgentCaptures { get { return _agentCaptures; } }
        private CombatOutcomeNotices _combatOutcomeNotices;
        private TrafficCallouts _trafficCallouts;
        /// <summary>Authority: station traffic of the last few seconds, for the agents' players.</summary>
        private readonly NexusTrafficRelay _trafficRelay = new NexusTrafficRelay();
        private readonly NexusCombatRelay _combatRelay = new NexusCombatRelay();
        /// <summary>Agent: plays the authority's station traffic to this server's players.</summary>
        private TrafficCallouts _agentTraffic;
        private SensorOcclusion _agentOcclusion;
        private IPlanetCatalogue _agentPlanets;
        private PlayerCampaignsManager _playerCampaignsManager;
        private PlayerFactionDemandManager _playerFactionDemandManager;
        private GridSpeedManager      _gridSpeedManager;
        /// <summary>Tells linked players which of their blocks the load governor has paused.</summary>
        private BlockPauseNotices     _blockPauseNotices;
        private MunitionSpeedManager  _munitionSpeedManager;
        private UplinkManager         _uplinkManager;
        private DebrisAbsorbManager   _debrisAbsorbManager;
        private GridReaper            _gridReaper;
        private readonly FightSiteMemory _fightSites = new FightSiteMemory();
        private readonly List<Vec3>      _fightHulls = new List<Vec3>();
        private DateTime                 _nextFightSweepUtc = DateTime.MinValue;

        // Tracks the last GPS hash sent per (identityId, label) so we can delete the old
        // coord-hashed marker before pushing the new position. Without this, each refresh
        // creates a new entry (coords are part of the hash when EntityId==0) and old ones
        // pile up for their full TTL, leaving a ghost trail.
        private readonly Dictionary<long, Dictionary<string, int>> _gpsHashes =
            new Dictionary<long, Dictionary<string, int>>();

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            InstanceDir = Path.Combine(StoragePath, PluginName);
            Directory.CreateDirectory(InstanceDir);
            // Redirected at session load once tuning.json has been read; see ApplyDataDirectory.
            StorageDir = InstanceDir;
            ConfigStore = new FactionStore(InstanceDir);
            Store = ConfigStore;

            string bridgeDir = Path.Combine(InstanceDir, "bridge");
            Directory.CreateDirectory(bridgeDir);
            _bridgeInboxPath = Path.Combine(bridgeDir, "inbox.txt");
            _bridgeOutboxPath = Path.Combine(bridgeDir, "outbox.txt");

            // Before the session manager on purpose: a framework that publishes its API once, from a
            // session component, reaches only handlers registered before the world loads.
            VpfAmmoBridge.Register();
            // Same reason, same place: Nexus publishes its dispatcher once, at its own session load.
            _nexusRelay = new NexusRelay();
            _nexusRelay.Register();

            _sessionManager = Torch.Managers.GetManager<TorchSessionManager>();
            if (_sessionManager != null) _sessionManager.SessionStateChanged += OnSessionStateChanged;

            // Before the Harmony pass, and before any session: the ore map is read by SE while the world
            // loads, which is earlier than every other catalogue this plugin builds.
            // The recipe is a shared input, so it is read from the data directory -- which is not
            // applied until session load, hence resolved here from the same tuning files. Reading
            // only: a Nexus member that will defer the simulation still runs this world's physics,
            // so it needs the same ore map and must find it in the same place.
            string initDataDir = InstanceDir;
            try { initDataDir = DataDirectory.Resolve(ConfigStore.PeekDataDirectory(), InstanceDir); }
            catch (Exception ex) { Log.Warn("Data directory could not be read at Init (" + ex.Message + ")."); }
            string planetOreStatus;
            PlanetOreControl.Load(LoadPlanetOrePolicy(initDataDir, out planetOreStatus));
            // Same timing for the same reason: SE registers a body for animal spawning when the planet
            // entity is added during world load.
            string faunaStatus;
            FaunaControl.Load(LoadFaunaPolicy(initDataDir, out faunaStatus));

            _harmony = new Harmony("com.awl.factions");
            // Not Harmony.PatchAll: a class that fails costs its own patch group, not the process.
            // Torch does not catch a plugin's Init, so one throw here would stop the whole server.
            HarmonyPatcher.Report patchReport = HarmonyPatcher.PatchAll(_harmony, typeof(FactionsPlugin).Assembly);
            foreach (HarmonyPatcher.GroupFailure failure in patchReport.Failures)
                Log.Error(failure.Exception, failure.Describe());
            Log.Info(patchReport.Summary());

            // Hand-applied rather than by attribute, each wrapping its own bind and reporting it.
            WeaponFireTelemetry.TryPatch(_harmony);
            // MySafeZone.InsertEntityInternal is private.
            SafeZoneMissilePatches.TryPatch(_harmony);
            // MyStoreBlock.BuyPrefabInternal is private.
            StoreShipSaleMarks.TryPatch(_harmony);
            // MyPhysicsBody.AddForce: counted only for a grid under 'factions dampprobe'.
            DampenerProbe.TryPatch(_harmony);
            // MyLandingGear lock/reattach/release: 'factions debug landing'. The client runs the same probe.
            LandingProbe.TryPatch(_harmony, "server", () => DebugTraces.Landing, line => Log.Info(line),
                                  new[] { 0.25, 1.0 });
            if (!LandingProbe.AllBound) Log.Warn("Landing probe NOT fully bound: " + LandingProbe.BindReport + ".");

            StartServerPerf();

            // Applied by hand rather than by attribute: MyCompositeShapes is an internal struct, so
            // [HarmonyPatch(typeof(...))] cannot name it.
            //
            // Logged unconditionally, but only the three facts a later failure is diagnosed from:
            // whether it bound at all, the generator version (pinning it to 0 silently disabled
            // material control outright), and how many sites are seed-scoped (binding somewhere is
            // not binding where the decision is made). What the feature DOES belongs in
            // docs/ore-geography.md, and "it may not fire" is already announced by the per-rock
            // warning, which names the rock. Prose here would print on every restart of a channel
            // that exists for diagnosis.
            FeatureRockMaterialControl.TryApply(_harmony);
            Log.Info(FeatureRockMaterialControl.Active
                ? "Feature rock: material control bound, generator v" + _tuning.FeatureRockGeneratorVersion
                  + ", " + FeatureRockMaterialControl.ScopedSites + " scoped site(s)."
                // The failure case keeps its explanation: it is a fault, it is rare, and the reader
                // needs to know the consequence without going to look it up.
                : "Feature rock: material control NOT bound (" + FeatureRockMaterialControl.InactiveReason
                  + ") -- rock will carry vanilla's global ore mix.");

            CrewNavigationArea.TryPatch(_harmony);
            if (!CrewNavigationArea.Bound)
                Log.Warn("Crew navigation on hulls NOT bound (" + CrewNavigationArea.InactiveReason + "); crew on a grounded hull plan on the terrain.");

            IntruderAlarmEvent.TryApply(_harmony);
            if (!IntruderAlarmEvent.Active)
                Log.Warn("Intruder Alarm event NOT attached to Event Controllers (" + IntruderAlarmEvent.InactiveReason + ").");

            Log.Info("Planet ore: " + planetOreStatus
                   + (PlanetOreControl.Bound ? "" : " -- the apply hook is NOT bound."));

            FaunaControl.TryPatch(_harmony);
            if (FaunaControl.InactiveReason != null && FaunaControl.InactiveReason != FaunaControl.NothingAuthored)
                Log.Warn("Fauna: " + faunaStatus + " -- hooks NOT fully bound (" + FaunaControl.InactiveReason
                       + "); bodies keep their generators' own animals where unbound.");
            else
                Log.Info("Fauna: " + faunaStatus);

            StationMaterializer.DebugBroadcast = msg =>
            {
                try
                {
                    var chat = new ScriptedChatMsg
                    {
                        Author = "[Factions]",
                        Text   = msg,
                        Font   = "White",
                        Color  = VRageMath.Color.Cyan,
                        Target = 0   // 0 = broadcast to all players
                    };
                    MyMultiplayerBase.SendScriptedChatMessage(ref chat);
                }
                catch (Exception ex) { Log.Warn(ex, "DebugBroadcast failed"); }
            };

            Log.Info("Initialised. Storage: " + StorageDir);
            // The plugin version can stay put across a protocol bump, so it cannot say whether a
            // client checkout will link; this line can, from the running server alone.
            Log.Info("Wire protocol: " + Factions.Wire.FactionsWire.ProtocolVersion);
        }

        private void OnSessionStateChanged(ITorchSession session, TorchSessionState state)
        {
            _sessionReady = state == TorchSessionState.Loaded;
            if (state == TorchSessionState.Loaded)
            {
                // Load tuning.json first so operator knobs override code defaults.
                // Runtime-derived fields (FleetSpeedMinKmPerMin, FreeMinerCargoVolumeLiters) are
                // stamped AFTER so SE definitions always win over any stale value in the file.
                bool transitContractMigrated = ConfigStore.LoadTuning(_tuning);

                try { BenchPhantoms.SweepStaleIdentities(); }
                catch (Exception ex) { Log.Warn("Bench: phantom identity sweep failed: " + ex.Message); }

                // After tuning, before the prefab scan: the scan prices shrapnel through this, and the
                // framework's content mods have all published by the time the session reports loaded.
                VpfAmmoBridge.Load((float)_tuning.ShrapnelDamageFraction);
                // After the Vanilla+ read: a beam rides its vanilla gun's shots, and those are capped
                // by the ammo generation that read supplies. ANPaL's content mods answer on request
                // from LoadData on, so no early registration is needed.
                AnpalBeamBridge.Load();
                WeaponStatsHelper.ResetBeamCache();
                WeaponFireTelemetry.Load(StorageDir, _tuning.WeaponTelemetryEnabled);

                // Same side of the Nexus gate as the grid speed below: what a server-side ray can reach is a
                // property of the physics this process runs. SVS compiles with the world, so not in Init().
                if (SvsRayRangeCap.TryPatch(_harmony, _tuning.SvsRayMaxRangeM))
                {
                    if (SvsRayRangeCap.Patched > 0)
                        Log.Info(_tuning.SvsRayMaxRangeM > 0
                            ? "SVS cockpit ray capped at " + _tuning.SvsRayMaxRangeM.ToString("0") + " m ("
                              + SvsRayRangeCap.Patched + " method(s) patched)."
                            : "SVS cockpit ray uncapped: SvsRayMaxRangeM is " + _tuning.SvsRayMaxRangeM + ".");
                }
                else Log.Warn("SVS cockpit ray NOT capped (" + SvsRayRangeCap.InactiveReason
                              + "); it reaches 1,000 km in server physics.");

                // Before the Nexus gate on purpose: a grid speed limit is a property of the world
                // whose physics this process runs, not of whether it owns the economy, so a deferred
                // cluster member must apply it too. SE never saves this with the world -- see
                // GridSpeedManager -- so the tuning file is the only thing that remembers it.
                string gridSpeedLine = GridSpeedManager.ApplyPersistedOverride(_tuning);
                if (gridSpeedLine != null) Log.Info(gridSpeedLine);

                // Same argument, same side of the Nexus gate: how fast a round flies is a property
                // of the world whose physics this process runs. SE's ammo is balanced against a
                // 100 m/s world and a missile is hard-capped at its own DesiredSpeed, so on a world
                // running at 500 the definitions need bringing back into relation -- see
                // MunitionSpeedManager and MissileVelocityInheritancePatch (FAC-222).
                string missileSpeedLine = MunitionSpeedManager.ApplyPersistedOverride(_tuning);
                if (missileSpeedLine != null) Log.Info("Missile speed scale applied: " + missileSpeedLine);

                // One authority per Nexus cluster. Nexus replicates banking, faction, identity and
                // reputation state across every member and applies an absolute balance on receipt,
                // so a second simulating server is a second writer on the same NPC accounts.
                // Decided before the data directory is touched, so a deferred server never reaches
                // for the shared folder at all.
                _nexusPresence = NexusGateway.Probe(out _nexusServerId, out _nexusDetail);
                _simulationDeferred = !NexusAuthority.ShouldSimulate(
                    _nexusPresence, _nexusServerId, _tuning.NexusAuthorityServerId, out _authorityReason);
                // Patches are applied in Init(), before this decision exists; they read the flag.
                SimulationGate.Deferred = _simulationDeferred;
                _nexusRole = NexusAuthority.RoleFor(_nexusPresence, !_simulationDeferred);
                if (_simulationDeferred)
                {
                    Log.Warn("Factions simulation deferred: " + _authorityReason + " (" + _nexusDetail + ")");
                    // Not simulating is not the same as dark: this server still holds players whose
                    // client plugin can reach nobody else. Serve them by relaying to the authority.
                    if (_nexusRole == NexusRole.Agent) StartAgent();
                    return;
                }
                Log.Info("Factions simulation authority: " + _authorityReason + " (" + _nexusDetail + ")");

                if (!ApplyDataDirectory())
                {
                    // Not a Nexus decision, but the same outcome: this process must not simulate.
                    _simulationDeferred = true;
                    SimulationGate.Deferred = true;
                    _nexusRole = _nexusPresence == NexusPresence.Absent ? NexusRole.Standalone : NexusRole.Dark;
                    _authorityReason = "data directory unusable -- see the error above";
                    return;
                }

                CheckDataDirectoryGuards();
                double storeRefreshSec = _tuning.StoreRefreshIntervalSeconds > 0
                    ? _tuning.StoreRefreshIntervalSeconds
                    : DefaultStoreRefreshIntervalSeconds;
                _storeRefreshTicks = (int)(storeRefreshSec * 60);

                // Derive fleet speed floor from the server's large-grid speed limit so servers
                // with higher speed mods benefit automatically. m/s → km/min = × 60 / 1000.
                // TODO: classify fleet sizes and use SmallShipMaxSpeed for small-grid convoys.
                float largeGridMps = MySector.EnvironmentDefinition.LargeShipMaxSpeed;
                _tuning.FleetSpeedMinKmPerMin = GridSpeedPolicy.SpeedFloorKmPerMin(largeGridMps);
                Log.Info(string.Format("Speed floor set to {0:0.#} km/min from large-grid limit {1:0} m/s.",
                    _tuning.FleetSpeedMinKmPerMin, largeGridMps));

                long cargoVol = ResolveCargoVolumeLiters(_tuning.FreeMinerCargoSubTypeId);
                if (cargoVol > 0)
                {
                    _tuning.FreeMinerCargoVolumeLiters = cargoVol;
                    Log.Info(string.Format("Free miner cargo volume set to {0} L from block '{1}'.",
                        cargoVol, _tuning.FreeMinerCargoSubTypeId));
                }
                else
                    Log.Warn("Could not resolve cargo volume from block '"
                             + _tuning.FreeMinerCargoSubTypeId + "'; free ore yield disabled.");

                string decisionLogDir = Path.Combine(StorageDir, "logs");
                _decisionLog = new DecisionLog(decisionLogDir);
                // Always logged, and it names the resolved path rather than the configured one.
                // The decision log follows DataDirectory, so a redirect moves it and leaves the old
                // Factions-<date>.log on disk, readable and frozen -- right name, right date,
                // plausible size. Reading the old path then returns SILENCE, which is
                // indistinguishable from the feature under test having stopped (FAC-095). One line
                // per boot is what makes that answerable instead of archaeological.
                Log.Info("Decision log: " + decisionLogDir);
                WarnAboutStaleInstanceLogs(decisionLogDir);
                string fleetPrefabConfigPath = InputPath("fleet_prefabs.json");
                FleetPrefabConfig prefabConfig = null;
                if (File.Exists(fleetPrefabConfigPath))
                {
                    try { prefabConfig = JsonConvert.DeserializeObject<FleetPrefabConfig>(File.ReadAllText(fleetPrefabConfigPath)); }
                    catch (Exception ex) { Log.Warn(ex, "Failed to load fleet_prefabs.json; ShipyardManager will use free-build mode."); }
                }
                else
                    Log.Info("fleet_prefabs.json not found at " + fleetPrefabConfigPath
                           + " (nor in this instance's own directory); ShipyardManager will use "
                           + "free-build defaults.");
                if (prefabConfig == null) prefabConfig = FleetPrefabConfig.Defaults();
                var fleetSpawner = new VanillaFleetSpawner(fleetPrefabConfigPath, Store);
                fleetSpawner.PlayerWithinKm = AnyPlayerWithinKm;
                _fleetSpawner = fleetSpawner;
                VanillaFleetSpawner.DebugBroadcast = StationMaterializer.DebugBroadcast; // share the same chat delegate
                // Before the first spawn can happen: grids born beside a tracked hull are caught by
                // the entity-add hook, and a hook attached after the fact misses the ones already there.
                SimGridMarks.Attach();
                var itemCatalogue = new SeItemCatalogue(_tuning);
                Log.Info(string.Format("SeItemCatalogue resolved {0}/{1} item kinds from SE definitions.",
                    itemCatalogue.ResolvedCount, FactionTuning.AllItems.Length));
                var itemRegistry = new SeItemRegistry(StorageDir);
                Log.Info(string.Format("SeItemRegistry: {0} loaded from cache, +{1} added, -{2} removed = {3} mapped.",
                    itemRegistry.FromFile, itemRegistry.Added, itemRegistry.Removed, itemRegistry.MappedCount));
                // A whole ItemKind is valued from one representative subtype, so a definition
                // rewritten in place moves the kind silently. Report only -- see SeItemCatalogue.
                foreach (string outlier in itemCatalogue.RepresentativeOutliers(itemRegistry))
                    Log.Warn("SeItemCatalogue representative is atypical of its kind -- " + outlier);
                // A body's ore comes from its own ore cubemap, not from a per-node-kind table --
                // see docs/ore-geography.md. Reported at boot because a world whose planets differ
                // from each other is exactly where the old table was silently wrong.
                // Which bodies a node may stand on is AUTHORED, not read from SE. The engine flag
                // that looks like the answer (AllowSurfaceEconomyStations) defaults true, is true on
                // every vanilla body that declares it, and is declared by no mod here -- uniformly
                // true, so it discriminates nothing, least of all the gas giant it would need to.
                // The recipe wins where it has an opinion; the SE flag still decides everywhere else.
                string surfacePolicyStatus;
                SurfaceNodePolicy surfacePolicy = LoadSurfaceNodePolicy(out surfacePolicyStatus);
                Log.Info("Surface nodes: " + surfacePolicyStatus);
                var planetCatalogue = new SePlanetCatalogue(surfacePolicy);
                _planetCatalogue = planetCatalogue;
                // Stone and anything else carrying a MineOnly row is everywhere by design (FAC-050),
                // so it must not be credited from the surface -- derived from the table rather than
                // named here, so a second such ore needs no code change.
                _oreGeography = new SeOreGeography(
                    oreSpacingM:      _tuning.OreProbeSpacingM,
                    surfaceSpacingM:  _tuning.SurfaceProbeSpacingM,
                    oreMaxPoints:     _tuning.OreProbeMaxPoints,
                    surfaceMaxPoints: _tuning.SurfaceProbeMaxPoints,
                    ubiquitousOres:   UbiquitousOreNames(_tuning));
                foreach (string line in _oreGeography.PlanetOreSummary())
                    Log.Info("Ore geography: " + line);

                // Deep space has no cubemap to read, so it is authored instead. Features take
                // precedence over planets where they overlap -- see CompositeOreGeography.
                string featureStatus;
                List<Factions.Core.WorldFeature> features =
                    WorldFeatureStore.Load(InputPath(WorldFeatureStore.FileName), out featureStatus);
                Log.Info("Ore geography: " + featureStatus);
                // A feature names an ore; what a rock of it yields is its materials' own ratio.
                Dictionary<string, double> asteroidYield = SeOreGeography.AsteroidYieldRatios();
                Log.Info("Ore geography: asteroid yield ratios -- " + string.Join(", ",
                    asteroidYield.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => kv.Key + " " + kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))));
                var featureGeography = new WorldFeatureGeography(features, planetCatalogue, asteroidYield);
                foreach (string unresolved in featureGeography.UnresolvedAnchors)
                    Log.Warn("Ore geography: feature anchor does not match any planet -- " + unresolved);
                _worldGeography = new CompositeOreGeography(featureGeography, _oreGeography);
                // The same features, read for geometry instead of composition: inside an authored
                // region the recipe places nodes, not the gravity heuristic. See FAC-096.
                _featurePlacement = new WorldFeaturePlacement(features, planetCatalogue);
                // Say out loud how much rock each feature asks for, and which authority classifies it.
                // The volume is large by nature -- a 270 km-wide ring is millions of cubic km -- and an
                // operator tuning Density needs to see it rather than discover it by flying into the
                // result. The zone is here because an authored one that failed to parse is otherwise
                // indistinguishable from one that was never written: nothing else reports it, and with
                // no nodes in the region nothing exercises it either.
                foreach (Factions.Core.WorldFeature feature in features)
                {
                    long objects = feature.ObjectCount(_tuning.FeatureMaxDensityPer1000Km3);
                    Log.Info(string.Format(
                        "Ore geography: feature '{0}' spans {1:N0} km3 at density {2:0.###} -- asks for {3:N0} object(s); zone {4}; separation {5}.",
                        feature.Name, feature.VolumeKm3(), feature.Density, objects,
                        feature.Zone.HasValue ? feature.Zone.Value.ToString() : "from gravity",
                        feature.NodeSeparationKm.HasValue
                            ? feature.NodeSeparationKm.Value.ToString("0.###") + " km"
                            : "from zone"));
                }
                var recipeRegistry = new SeRecipeRegistry(StorageDir);
                Log.Info(string.Format("SeRecipeRegistry: {0} loaded from cache, +{1} added, -{2} removed = {3} recipes.",
                    recipeRegistry.FromFile, recipeRegistry.Added, recipeRegistry.Removed, recipeRegistry.Count));
                string capRecipesPath = InputPath("capability_recipes.json");
                // The geography is wired above, and it is what knows which ores this world holds -- the
                // yield table only states stone. Seeding reachability from the table alone excludes
                // every non-stone recipe (FAC-111).
                CapabilityRecipesConfig capConfig = LoadCapabilityRecipesConfig(capRecipesPath);
                var capRecipes = ResolveCapabilityRecipes(capConfig, recipeRegistry, _tuning,
                    _worldGeography != null ? _worldGeography.MintableOres : null);
                Log.Info(string.Format("capability_recipes.json: resolved {0} capability kind(s).",
                    capRecipes.Count));
                var installBoms = ResolveInstallBoms(capConfig, capRecipes);
                foreach (var kv in ResolveInstallPrefabBoms(capConfig)) installBoms[kv.Key] = kv.Value;
                _tuning.InstallBoms = installBoms;
                int habitatBeds = ResolveHabitatBeds(capConfig);
                if (habitatBeds > 0) _tuning.HabitatBedsPerLevel = habitatBeds;
                Log.Info(string.Format(habitatBeds > 0
                        ? "Habitat: a level installs {0} bed(s) and houses {0} worker(s)."
                        : "Habitat: no bed among its install blocks resolved; a level houses {1} worker(s).",
                    habitatBeds, _tuning.HabitatBedsPerLevel));
                Log.Info(string.Format("capability_recipes.json: resolved {0} real-block/prefab install BOM(s).",
                    _tuning.InstallBoms.Count));
                _tuning.MachineProfiles = ResolveMachineProfiles(capConfig);
                foreach (var machineProfile in _tuning.MachineProfiles)
                    Log.Info("MACHINE_PROFILE " + MachinePhysics.Describe(machineProfile.Key, machineProfile.Value,
                        MachinePhysics.TakesProductivityBonus(machineProfile.Key) ? _tuning.ProductionProductivityBonus : 0.0));
                WriteProductionModelFile(capRecipes);
                var itemVolumeCatalogue = new SeItemVolumeCatalogue();
                Log.Info(string.Format("SeItemVolumeCatalogue resolved {0} item volume entries.",
                    itemVolumeCatalogue.Count));
                var itemMassCatalogue = new SeItemMassCatalogue();
                Log.Info(string.Format("SeItemMassCatalogue resolved {0} item mass entries.",
                    itemMassCatalogue.Count));
                var nutritionCatalogue = new SeNutritionCatalogue();
                Log.Info(string.Format("SeNutritionCatalogue resolved {0} edible item(s).", nutritionCatalogue.Count));
                var itemPriceCatalogue = SeItemPriceCatalogue.Load(StorageDir, _tuning, recipeRegistry.GasRecipes);
                Log.Info(string.Format(
                    "SeItemPriceCatalogue loaded {0} subtype price(s), {1} exact price(s); direct={2}, recipe={3}, legacy={4}, unresolved={5}.",
                    itemPriceCatalogue.Count, itemPriceCatalogue.ExactCount, itemPriceCatalogue.DirectSeedCount,
                    itemPriceCatalogue.RecipeResolvedCount, itemPriceCatalogue.LegacyFallbackCount,
                    itemPriceCatalogue.UnresolvedCount));
                if (itemPriceCatalogue.UnresolvedCount > 0)
                {
                    // Split by reason: a blocked chain is a resolver gap, a multi-output blueprint is
                    // an unimplemented derivation, and no producer at all means there is nothing to
                    // derive from and only price data will do.
                    Log.Warn(string.Format(
                        "SeItemPriceCatalogue could not derive a price for {0} item(s) -- "
                        + "blocked-chain={1}, multi-output={2}, no-producer={3}. {4}",
                        itemPriceCatalogue.UnresolvedCount, itemPriceCatalogue.UnresolvedBlockedChain,
                        itemPriceCatalogue.UnresolvedMultiOutput, itemPriceCatalogue.UnresolvedNoProducer,
                        itemPriceCatalogue.UnresolvedSample));
                }
                // Prefab catalogue: scanned from Content/Data/Prefabs subfolders; replaces
                // the hand-curated fleet_prefabs.json role+personality mapping for fleet selection.
                var prefabCatalogue = PrefabCatalogueScanner.Scan(StorageDir, itemPriceCatalogue, _tuning);
                Log.Info(string.Format("PrefabCatalogueScanner: {0} prefab(s) catalogued.",
                    prefabCatalogue.Count));
                // Player-captured prefabs (`!factions ship capture`) persist independently of the
                // vanilla scan above -- reload them from disk and re-populate the catalogue/recipe
                // registry the same way a live capture does (ScanPrefab/AssignCapabilityRoles), so a
                // restart reproduces identical stats with no drift.
                // Data directory only, never the instance fallback: CustomPrefabStore keeps this path
                // and SaveToDisk writes captures back into it, so a fallback would send new captures
                // to the instance dir -- and would keep doing so, since the fallback is chosen
                // exactly when the shared folder is absent.
                string customPrefabDir = Path.Combine(StorageDir, "custom_prefabs");
                WarnIfStrandedCustomPrefabs(customPrefabDir);
                var customPrefabStore = CustomPrefabStore.LoadAll(customPrefabDir);
                int customPrefabCount = 0;
                foreach (var captured in customPrefabStore.AllEntries)
                {
                    bool isStation = string.Equals(captured.Kind, "Station", StringComparison.OrdinalIgnoreCase);
                    var customEntry = new SePrefabCatalogue.Entry
                    {
                        Roles       = new List<string>(),
                        FactionHint = captured.FactionHint,
                        GridSize    = PrefabCatalogueScanner.DetectGridSize(captured.Grids),
                        Kind        = isStation ? "Station" : "Ship",
                        Site        = isStation ? captured.Site : null,
                        Family      = captured.Family,
                        Version     = captured.Family != null ? captured.Version : 0,
                    };
                    PrefabCatalogueScanner.ScanPrefab(captured.Grids, itemPriceCatalogue, customEntry);
                    // A station design carries no fleet role: it is never dispatched, and a role on it
                    // would put a static grid in a ship pool. The site it was measured at is carried
                    // on the manifest instead, since nothing about the saved grids records where they
                    // stood.
                    if (!isStation) PrefabCatalogueScanner.AssignCapabilityRoles(customEntry, _tuning);
                    prefabCatalogue.RegisterCustomPrefab(captured.PrefabId, customEntry, captured.OwnerFactionTag);
                    recipeRegistry.RegisterPrefabRecipe(captured.PrefabId, captured.Grids, fromCapture: true);
                    customPrefabCount++;
                }
                if (customPrefabCount > 0)
                    Log.Info("CustomPrefabStore: re-registered " + customPrefabCount + " captured prefab(s).");
                _prefabBookValues = PrefabBookValueCache.Load(StorageDir, prefabConfig, itemCatalogue, itemPriceCatalogue, itemRegistry, prefabCatalogue);
                // Vanilla station designs become refit targets only once they have a bill and a
                // catalogue entry with a site on it. Runs here because both halves depend on the
                // book-value cache, which is what prices them.
                int stationDesigns = PrefabCatalogueScanner.RegisterStationPrefabs(
                    prefabCatalogue, recipeRegistry, _prefabBookValues, itemPriceCatalogue,
                    customPrefabStore.IsCapture);
                Log.Info(string.Format("Prefab book values cached: {0} fleet prefab(s), {1} station prefab(s); "
                    + "{2} station design(s) offered as refit targets.",
                    _prefabBookValues.FleetValueCount, _prefabBookValues.StationValueCount, stationDesigns));
                // PrefabCatalogueScanner.Scan wrote prefab_catalogue.json before any capture was
                // merged in and before a single station design existed, so it is rewritten here --
                // once, after the last registration -- or the Dashboard API's /api/prefabs (which
                // reads that file verbatim, never the live catalogue) would show neither. Best-effort:
                // the live catalogue is already correct regardless of whether this write succeeds.
                LogPrefabCeiling(prefabCatalogue);
                _prefabBlocks = prefabCatalogue.BlockCount;
                try { File.WriteAllText(Path.Combine(StorageDir, "prefab_catalogue.json"), prefabCatalogue.ToJson()); }
                catch (Exception ex) { Log.Warn(ex, "Could not refresh prefab_catalogue.json after prefab registration."); }
                var factionTypeCatalogue = SeFactionTypeCatalogue.Build();
                Log.Info(string.Format(
                    "SeFactionTypeCatalogue: {0} type(s) with offer lists, {1} tag(s) mapped.",
                    factionTypeCatalogue.TypeCount, factionTypeCatalogue.TagCount));
                var stationNameSelector = StationNameCatalogueBootstrap.GenerateAndLoad(StorageDir, InstanceDir);
                // Wire fleet spawner with catalogue so it uses faction-type-aware prefab selection.
                fleetSpawner.SetCatalogue(prefabCatalogue, factionTypeCatalogue);
                fleetSpawner.SetCustomPrefabStore(customPrefabStore);
                fleetSpawner.SetTuning(_tuning);   // approach profile knobs for the native flight controller
                fleetSpawner.SetPlanets(planetCatalogue);   // gravity for the FUEL_OBSERVED modelled burn
                _runtime = new FactionRuntime(Store, _decisionLog, _tuning,
                    economy: new SeEconomyGateway(),
                    spawner: fleetSpawner,
                    catalogue: itemCatalogue,
                    registry: itemRegistry,
                    recipes: recipeRegistry,
                    prefabConfig: prefabConfig,
                    capRecipes: capRecipes,
                    prefabCatalogue: prefabCatalogue,
                    itemVolumes: itemVolumeCatalogue,
                    itemMasses: itemMassCatalogue,
                    eventWriter: new LiveEventWriter(StorageDir),
                    itemPrices: itemPriceCatalogue,
                    planets: planetCatalogue,
                    ores: _worldGeography,
                    featurePlacement: _featurePlacement,
                    factionTypes: factionTypeCatalogue,
                    stationNameSelector: stationNameSelector);
                _runtime.Nutrition = nutritionCatalogue;
                _runtime.Load();
                // Before anything simulates: vanilla's station cell generator runs on simulation
                // frames, and this handler is still inside MySession.AfterLoading.
                _runtime.SeedStationClaims();
                _runtime.CreditStrandedStationMunitions();
                _runtime.CreditStrandedStationLoot();
                _runtime.SetWorldFeatures(features, planetCatalogue);
                WarnIfEstateDoesNotFit(features, planetCatalogue);
                _runtime.SetCustomPrefabStore(customPrefabStore);
                StoreShipSaleMarks.Store = customPrefabStore;
                _runtime.SetPrefabCatalogueStorageDir(StorageDir);
                _planetSnapshotWriter = new PlanetSnapshotWriter(StorageDir, planetCatalogue);
                _planetSnapshotWriter.Tick(DateTime.UtcNow);
                _playerIndexWriter = new PlayerIndexWriter(StorageDir);
                _playerIndexWriter.RemotePlayers = RemotePlayersNow;
                _playerIndexWriter.RemoteName = steam =>
                {
                    string n;
                    return _remoteNames.TryGetValue(steam, out n) ? n : null;
                };
                _playerIndexWriter.Tick(DateTime.UtcNow);
                // The uplink is the connection layer only; each feature attaches to it afterwards.
                _uplinkManager = new UplinkManager(
                    () => Factions.Wire.WireFeatures.Scanner
                          | Factions.Wire.WireFeatures.Map
                          | Factions.Wire.WireFeatures.Assets
                          | Factions.Wire.WireFeatures.NodeDetail
                          | Factions.Wire.WireFeatures.Events
                          | Factions.Wire.WireFeatures.StandingOrders
                          | Factions.Wire.WireFeatures.Campaigns
                          | Factions.Wire.WireFeatures.FactionDemand
                          | Factions.Wire.WireFeatures.FeatureRock
                          | Factions.Wire.WireFeatures.GridSpeed
                          | Factions.Wire.WireFeatures.Signature
                          | Factions.Wire.WireFeatures.Market
                          | Factions.Wire.WireFeatures.Expand
                          | Factions.Wire.WireFeatures.NodeInventory
                          // Granted here and nowhere else. The agent path below does not advertise
                          // it, and UplinkManager.RemoteWithheldFeatures strips it from a relayed
                          // client: every step of it -- the terminal block, the hull beside it, the
                          // transient spawn -- is an entity in the caller's own sector, which is not
                          // this process when the caller is on another server.
                          | Factions.Wire.WireFeatures.ShipAssets
                          // Station refit. Relayed to an agent's players: the node actions are this
                          // server's, and the agent answers the terminal and the scan itself
                          // (AgentTerminalSurfaces).
                          | Factions.Wire.WireFeatures.StationRefit
                          // Granted here and nowhere else, for the same reason: the scan dialogs'
                          // design question names a hull or a terminal in the caller's sector.
                          | Factions.Wire.WireFeatures.DesignVersions
                          // Granted here and nowhere else, for the same reason: refuel and rearm
                          // write into the tanks and guns of a ship docked beside the caller.
                          | Factions.Wire.WireFeatures.DockSupply
                          // Granted here and nowhere else, for the same reason: a restore welds and
                          // places blocks on a ship beside the caller's terminal.
                          | Factions.Wire.WireFeatures.Restoration
                          // The node window's Build button. Relayable, unlike the three above it:
                          // it names a node and a prefab and touches no entity in the caller's own
                          // sector, so a client on an agent server reaches it like any other node
                          // command.
                          | Factions.Wire.WireFeatures.Build
                          // The Fleet window. Relayable for the same reason Build is: it names
                          // lines, roles and prefabs on a faction's own state and touches no entity
                          // in the caller's sector.
                          | Factions.Wire.WireFeatures.FleetDoctrine
                          // The Tactics window off it: read-only, relayable for the same reason.
                          | Factions.Wire.WireFeatures.Tactics
                          | Factions.Wire.WireFeatures.PlayerShelf
                          // The diplomacy readout and its acts. Granted here and not on the agent
                          // path; a relayed client reaches it here, its identity resolved by
                          // LinkedViewers.
                          | Factions.Wire.WireFeatures.Diplomacy
                          // Wing actions on antennas. Granted here and not on the agent path, and
                          // stripped from a relayed client: the antenna and the hulls it commands are
                          // entities in the caller's own sector.
                          | Factions.Wire.WireFeatures.WingCommand
                          // Crew orders on seats: local to this server for the same reason.
                          | Factions.Wire.WireFeatures.CrewOrders
                          // Respawning at a founded node: this process handles the player's respawn
                          // request and places the character, so it is local for the same reason.
                          | Factions.Wire.WireFeatures.NodeSpawn
                          // Combat outcome notices. Granted here and not on the agent path, and
                          // stripped from a relayed client: the viewers of a fight are the players
                          // in this server's own sectors.
                          | Factions.Wire.WireFeatures.CombatOutcome
                          // The traffic-control voice (FAC-419). Local to this server like combat
                          // notices, and stripped from a relayed client by RemoteWithheldFeatures:
                          // an agent serves it, from the events this server's replies carry.
                          // Offered only with protocol 35: a link request carries no feature list,
                          // so an older client would be granted it and lose its chat whispers.
                          | Factions.Wire.WireFeatures.TrafficVoice
                          // The HUD status panel. The sieges it shows are this server's to run,
                          // wherever their station stands, so it is relayed to an agent's players.
                          | Factions.Wire.WireFeatures.StatusPanel
                          | Factions.Wire.WireFeatures.BlockPause,
                    line => { if (_decisionLog != null) _decisionLog.Write("uplink " + line); });
                _runtime.AttachStatusPanel(new StatusPanelPublisher(_uplinkManager) { RemotePlayers = RemotePlayersNow });
                _runtime.AttachNexusSiege(QueueRemoteNotice, RemotePlayersNow);
                _blockPauseNotices = new BlockPauseNotices(_uplinkManager);
                _gridSpeedManager = new GridSpeedManager(_tuning, _uplinkManager,
                    line => { if (_decisionLog != null) _decisionLog.Write(line); });
                _munitionSpeedManager = new MunitionSpeedManager(_tuning, _uplinkManager,
                    line => { if (_decisionLog != null) _decisionLog.Write(line); });
                CrewIntruders.Sink = line => { if (_decisionLog != null) _decisionLog.Write(line); };
                _playerScannerManager = new PlayerScannerManager(_runtime, StorageDir, _uplinkManager);
                _trespassHailer = new TrespassHailer(_runtime,
                    line => { if (_decisionLog != null) _decisionLog.Write(line); });
                _trespassHailer.RemoteChat = (steam, author, text, font) =>
                    QueueRemoteChat(steam, text, font, author, false);
                _playerMapManager = new PlayerMapManager(_runtime, _uplinkManager, _playerScannerManager);
                _playerExpandManager = new PlayerExpandManager(_runtime, _uplinkManager);
                _playerAssetsManager = new PlayerAssetsManager(_runtime, _uplinkManager);
                _playerNodeDetailManager = new PlayerNodeDetailManager(_runtime, _uplinkManager, _playerScannerManager,
                    () => _storeManager);
                // The price updater is built further down this method; the lambda reads the field
                // when a withdrawal or a send actually moves something, which is long after that.
                _playerShipAssetsManager = new PlayerShipAssetsManager(_runtime, _uplinkManager,
                    nodeId => { if (_priceUpdater != null) _priceUpdater.RecordActivity(nodeId); });
                _playerStationRefitManager = new PlayerStationRefitManager(_runtime, _uplinkManager);
                _playerDesignVersionManager = new PlayerDesignVersionManager(_runtime, _uplinkManager);
                _playerEventsManager = new PlayerEventsManager(_runtime, _uplinkManager);
                _playerStandingOrdersManager = new PlayerStandingOrdersManager(_runtime, _uplinkManager);
                _playerFleetDoctrineManager = new PlayerFleetDoctrineManager(_runtime, _uplinkManager);
                _playerTacticsManager = new PlayerTacticsManager(_runtime, _uplinkManager);
                _playerShelfManager = new PlayerShelfManager(_runtime, _uplinkManager);
                _playerBankSalesManager = new PlayerBankSalesManager(_runtime, _uplinkManager);
                _playerDiplomacyManager = new PlayerDiplomacyManager(_runtime, _uplinkManager);
                _playerWingManager = new PlayerWingManager(_runtime, _uplinkManager);
                _playerCrewOrderManager = new PlayerCrewOrderManager(_uplinkManager);
                _combatOutcomeNotices = new CombatOutcomeNotices(_runtime, _uplinkManager);
                _trafficCallouts = new TrafficCallouts(_runtime, _uplinkManager, TrafficListeners, TrafficBroadcastKm);
                _trespassHailer?.AttachVoice(_trafficCallouts.SendHail);
                _runtime.AttachOccupationSink(_trespassHailer != null ? _trespassHailer.OnOccupation : (Action<OccupationNotice>)null);
                _playerCampaignsManager = new PlayerCampaignsManager(_runtime, _uplinkManager, _playerScannerManager);
                _playerFactionDemandManager = new PlayerFactionDemandManager(_runtime, _uplinkManager);
                _playerRockManager = new PlayerRockManager(_runtime, _uplinkManager);
                if (_nexusRole == NexusRole.Authority)
                {
                    // Remote clients, relayed by the cluster's other servers, take the same handlers.
                    _uplinkManager.ConfigureRelay(_nexusRelay, (byte)_nexusServerId,
                        (byte)_tuning.NexusAuthorityServerId, NexusRole.Authority);
                    _uplinkManager.PositionsReceived = OnRemotePositions;
                    _trafficCallouts.Relay = (e, name, owner, speaker) =>
                        _trafficRelay.Add(NexusTrafficCall.From(e, name, owner, speaker), DateTime.UtcNow);
                    _trafficCallouts.RelayHail = RelayHail;
                    _uplinkManager.CaptureUploadReceived = OnCaptureUpload;
                    _wingReadings = new NexusWingReadings();
                    WingWorld.Relayed = _wingReadings;
                    _uplinkManager.WingCallReceived = OnWingCall;
                    _uplinkManager.HullAskReceived = OnHullAsk;
                    _runtime.CombatRelaySink = (o, sentence) =>
                        _combatRelay.Add(NexusCombatCall.From(o, sentence), DateTime.UtcNow);
                    _runtime.RemoteKillSink = QueueRemoteKill;
                    LinkedViewers.ReportedIdentity = ReportedRemoteIdentity;
                    LinkedViewers.RemotePosition = RemotePositionOf;
                    if (_playerScannerManager != null) _playerScannerManager.RemotePlayers = RemotePlayersNow;
                    if (_playerAssetsManager != null)
                        _playerAssetsManager.RemoteGrids = () => _agentOwnedGrids.Fresh(DateTime.UtcNow, AgentOwnedGridsMaxAge);
                    _uplinkManager.AgentReportReceived = OnAgentReport;
                    _chatCommands = new NexusChatCommands(Torch, _nexusRelay.TrySend, (byte)_nexusServerId,
                                                          (byte)_tuning.NexusAuthorityServerId);
                    _chatCommands.RunVerb = line =>
                    {
                        string answer;
                        return TryRunFactionsVerb(line, out answer) ? (answer ?? "") : null;
                    };
                    _uplinkManager.ChatCommandReceived = _chatCommands.OnCommand;
                    Log.Info("Factions authority accepts relayed client uplinks from the cluster (server "
                             + _nexusServerId + ").");
                    var routed = _fleetSpawner as VanillaFleetSpawner;
                    if (routed != null)
                    {
                        routed.Remote = _fleetRegistry;
                        routed.RouteSpawn = RouteFleetSpawn;
                        routed.FleetOfGrid = _runtime.FleetOfGridForHandover;
                        SectorHandover.Attach((byte)_nexusServerId);
                    }
                    Log.Info("Fleets in agents' sectors are flown there: sector lookup "
                             + (NexusGateway.SectorLookupBound ? "bound" : "unavailable, every fleet stays here") + ".");
                }
                _uplinkManager.Register();
                if (transitContractMigrated)
                {
                    int rescheduled = _runtime.ReschedulePendingFleetTransits();
                    Log.Info("Transit tuning migrated; rescheduled " + rescheduled + " pending fleet(s).");
                }
                if (_prefabBookValues != null)
                {
                    _runtime.ConfigureStationFounder(
                        _prefabBookValues.StationBookValue,
                        prefabId => new List<WeaponBand>(_prefabBookValues.StationWeaponBands(prefabId)),
                        _prefabBookValues.GetStationPool,
                        stationNameSelector,
                        _prefabBookValues.IsKnownStationPrefab,
                        _prefabBookValues.IsFactorumStation);
                    _runtime.ApplyStationBookValues(_prefabBookValues.StationBookValue);
                    _runtime.ApplyStationCombatStats(
                        prefabId => new List<WeaponBand>(_prefabBookValues.StationWeaponBands(prefabId)));
                    int rearmed = _runtime.RebuildFleetWeaponBands();
                    if (rearmed > 0)
                        Log.Info("Re-armed " + rearmed + " in-flight fleet(s) whose weapon bands did not survive the restart.");
                    int backfilled = _runtime.BackfillStationFields();
                    if (backfilled > 0)
                        Log.Info("Backfilled station fields on " + backfilled + " node(s).");
                }
                Log.Info("Session loaded; " + Store.LoadIndex().Count
                         + " faction(s) registered; sim runtime started with " + _runtime.FactionCount + " loaded.");
                LogDebugToggleState();

                // ---- player interaction managers (game-thread, SE-facing) ----------------------
                _presenceManager = new PlayerPresenceManager(_runtime);
                _presenceManager.RemotePlayers = RemotePlayersNow;
                _claimOfferManager = new ClaimOfferManager(_runtime);
                _claimOfferManager.RemotePlayers = RemotePlayersNow;
                _claimOfferManager.RemoteWhisper = (steam, text, offer) => QueueRemoteChat(steam, text, "Blue", null, offer);
                _claimOfferManager.AttachChat(Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>());
                _conquerOfferManager = new ConquerOfferManager(_runtime);
                _conquerOfferManager.AttachChat(Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>());
                _conquerOfferManager.IsRemote = IsRemotePlayer;
                _conquerOfferManager.RemotePlayers = RemotePlayersNow;
                _conquerOfferManager.RemoteWhisper = (identityId, text, offer) =>
                    QueueRemoteNotice(identityId, text, "Red", 0, true, offer);
                _offerReplyRouter = new OfferReplyRouter(_claimOfferManager, _conquerOfferManager);
                _offerReplyRouter.AttachChat(Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>());
                _presenceReinforceManager = new PresenceReinforceManager(_runtime);
                _respawnPlacement = new RespawnPlacement(_runtime, _decisionLog);
                RespawnPlacement.Instance = _respawnPlacement;
                _respawnPlacement.Attach();
                // The authority's players are set down only in its own sectors: a node an agent's
                // sector holds stands on that server, not here.
                FactionRuntime spawnRuntime = _runtime;
                byte spawnHere = (byte)_nexusServerId;
                _nodeSpawnManager = _nexusRole == NexusRole.Authority
                    ? new NodeSpawnManager(() => spawnRuntime.World != null ? spawnRuntime.World.Graph : null,
                                           spawnRuntime.TryGetStationArrivalFrame,
                                           node => !NodeInOtherSector(node, spawnHere), _uplinkManager, _decisionLog)
                    : new NodeSpawnManager(_runtime, _uplinkManager, _decisionLog);
                NodeSpawnManager.Instance = _nodeSpawnManager;
                _runtime.AttachChat(Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>());
                _trespassHailer?.AttachChat(Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>());

                var registeredTags = new System.Collections.Generic.HashSet<string>(
                    System.Linq.Enumerable.Select(Store.LoadIndex(), e => e.Tag),
                    StringComparer.OrdinalIgnoreCase);

                _priceUpdater = new GlobalPriceUpdater(
                    _runtime.World, _runtime.Oracle, _runtime.Registry,
                    StorageDir, _tuning);

                _storeManager = new StationStoreManager(
                    _runtime.World, _runtime.Oracle, _runtime.Registry,
                    _runtime.ItemPrices, _tuning,
                    registeredTags,
                    nodeId => _priceUpdater.RecordActivity(nodeId),
                    factionTypeCatalogue,
                    prefabCatalogue,
                    _prefabBookValues,
                    tag => _runtime.FindFaction(tag));
                StoreShipSaleMarks.ShelfSold = _storeManager.OnPlayerBoughtShip;
                _runtime.CanonicaliseItemKey = _storeManager.TryCanonicaliseItemKey;
                _runtime.ListOrderableItems = _storeManager.OrderableItems;

                _debrisAbsorbManager = new DebrisAbsorbManager(
                    _runtime.World, _tuning, _decisionLog,
                    entityId => _runtime.HoldsMaterialisedGrid(entityId),
                    nodeId => _priceUpdater.RecordActivity(nodeId),
                    PlayersWithinKm);
                _debrisAbsorbManager.AttachChat(Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>());

                // Beside the debris sweep because it walks the same entity set, and the two answer
                // different questions about it: the sweep credits a node for wreckage SE would
                // collect, the reaper closes what the simulation itself put there. The boot pass is
                // requested here rather than run here -- the runtime has already reconciled its
                // fleets by this point, so every grid no surviving fleet claims is litter, and the
                // pass runs on the first tick rather than inside the session-load frame.
                _gridReaper = new GridReaper(
                    _tuning, _decisionLog,
                    entityId => _runtime.HoldsMaterialisedGrid(entityId),
                    entityId => _runtime.IsStationGrid(entityId),
                    AnyPlayerWithinKm);
                _gridReaper.RequestBootPass();

                // After the store manager, because it borrows the invKey -> definition map the store
                // builds for its own listings rather than scanning the definitions a second time.
                _playerNodeTransferManager = new PlayerNodeTransferManager(
                    _runtime, _uplinkManager, _storeManager.TryResolveDefinitionId);
                if (_nexusRole == NexusRole.Authority)
                    _playerNodeTransferManager.QueueRemoteMove = (home, move) => _stockMoves.Add(home, move, DateTime.UtcNow);

                // After the store manager, because it sells off the store's own offer rows and
                // settles through it: one price and one floor for the counter and the terminal.
                _dockSupplyManager = new DockSupplyManager(() => TerminalWorld.Of(_runtime), _uplinkManager, _storeManager, _tuning);
                // After the store manager for the same reason: a restore buys from the same offer
                // rows and settles node purchases through it.
                _restorationService = new RestorationService(_runtime, _uplinkManager, _storeManager);

                // After the store manager, because it attaches to it: player reputation is written
                // from the store seam and read by Diplomacy.Drift through World.MemberStanding.
                _reputationManager = new PlayerReputationManager(
                    _runtime, _tuning, StorageDir,
                    (identityId, nowUtc) =>
                    {
                        DateTime seen;
                        return _playerIndexWriter != null
                               && _playerIndexWriter.TryGetLastSeen(identityId, nowUtc, out seen)
                            ? (DateTime?)seen : null;
                    });
                _runtime.World.MemberStanding = _reputationManager;
                _storeManager.OnPlayerTrade = _reputationManager.RecordTrade;
                // Member reputation display (diplomacy-plan.md rule 4): SE's join/leave/change events,
                // and the projection pass on the economy quantum, right after the relation table is.
                _reputationManager.Attach();
                _runtime.AfterDiplomacyPass = _reputationManager.OnEconomyQuantum;
                // A station refit replaces the MyFactionStation its store items hang off, so the
                // list is empty until something repopulates it. The periodic refresh would get there
                // on its own cadence; this makes the new station tradable in the same frame it stands.
                _runtime.StationStoresChanged = () => { if (_storeManager != null) _storeManager.RefreshAll(); };

                // After the store manager: the market reads listings through it, and both need the
                // same registered-tag set.
                _marketMemory = new Factions.Core.MarketMemory(StorageDir);
                _playerMarketManager = new PlayerMarketManager(
                    _runtime, _uplinkManager, _playerScannerManager, _marketMemory, registeredTags,
                    (node, into) => _storeManager != null ? _storeManager.BuildListings(node, into) : 0,
                    BuildPlayerContacts);

                FactionContractStrategy.InjectStrategies(
                    registeredTags,
                    _runtime.World.Demand,
                    _runtime.ItemPrices,
                    _runtime.World,
                    _runtime);

                // Guard-via-PvEBounty completion signals: the raid's own existing resolution
                // lifecycle drives success/failure, not vanilla's own AI-block-watching (never
                // populated since we never set Targets — see GenerateGuardBountyContract).
                _runtime.PlayerContactSupplier = BuildPlayerContacts;
                _runtime.OnPvEBountyTargetDestroyed = FactionContractStrategy.HandleBountyTargetDestroyed;
                _runtime.OnFleetResolved = FactionContractStrategy.HandleFleetResolved;
            }
            else if (state == TorchSessionState.Unloading)
            {
                _simulationDeferred = false;
                SimulationGate.Deferred = false;
                SimulationGate.Hands = false;
                SimulationGate.HandsPending = false;
                _nexusRole = NexusRole.Standalone;
                _authorityOnline = false;
                _remotePlayers = new RemotePlayerPositions();
                _remoteNow = new List<RemotePlayer>();
                _agentContacts.Clear();
                _agentOwnedGrids.Clear();
                _ownedGridsReportTick = 0;
                _agentLoads.Clear();
                _agentContactsSampled = false;
                LinkedViewers.ReportedIdentity = null;
                LinkedViewers.RemotePosition = null;
                _chatCommands?.Detach();
                _chatCommands = null;
                _noticeOutbox = new NexusNoticeOutbox();
                _stockMoves = new NexusStockMoveOutbox();
                _agentHands?.Close();
                _agentHands = null;
                _gridReaper = null;
                EconomyTickSlicer.TickedOnAgent = false;
                CrewPresence.AgentPrefabOf = null;
                CrewPresence.AgentCatalogue = null;
                FactionWalletTransferPatch.AgentSink = null;
                ClearAgentStoreHooks();
                if (_agentChat != null) _agentChat.MessageProcessing -= OnAgentChat;
                _agentChat = null;
                _snapshotPayload = null;
                _snapshotHash = 0;
                _snapshotSentSeq.Clear();
                _snapshotSentUtc.Clear();
                WeaponFireTelemetry.Save();
                BenchPhantoms.Unload();
                StopRuntime();
            }
        }

        /// <summary>
        /// Loads <c>capability_recipes.json</c> and resolves each capability kind's blueprint class
        /// names to the actual <see cref="ConversionRecipe"/> objects from <paramref name="registry"/>.
        /// The returned dict is passed to <see cref="ProductionManager"/> so it never needs SE APIs.
        /// </summary>
        /// <summary>Reads <c>capability_recipes.json</c> once, seeding it when absent and merging in
        /// any <see cref="CapabilityRecipesConfig.Defaults"/> entry it does not name. Every resolver
        /// below takes the result rather than re-reading the file, so they cannot disagree about it
        /// and the merge happens once.
        ///
        /// <para>The merged config is written back so the operator can see and edit what is actually
        /// in force. The write is best-effort: if it fails the in-memory merge still applies, so a
        /// read-only config directory costs a warning rather than the feature.</para></summary>
        /// <returns>Null only when the file cannot be created or parsed — callers treat that as
        /// "feature off", which is what they did before.</returns>
        private static CapabilityRecipesConfig LoadCapabilityRecipesConfig(string configPath)
        {
            if (!File.Exists(configPath))
            {
                try
                {
                    File.WriteAllText(configPath, CapabilityRecipesConfig.DefaultsJson());
                    Log.Info("capability_recipes.json missing; wrote vanilla defaults.");
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Failed to write default capability_recipes.json; ProductionManager will be idle.");
                    return null;
                }
            }

            CapabilityRecipesConfig config;
            try { config = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(File.ReadAllText(configPath)); }
            catch (Exception ex)
            {
                Log.Warn(ex, "Failed to parse capability_recipes.json; ProductionManager will be idle.");
                return null;
            }
            if (config == null) return null;

            List<string> backfilled = CapabilityRecipesConfig.MergeDefaults(config);
            if (backfilled.Count > 0)
            {
                Log.Info(string.Format("capability_recipes.json: backfilled {0} default entry/entries absent from the file: {1}.",
                    backfilled.Count, string.Join(", ", backfilled.ToArray())));
                try { File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented)); }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Failed to write the backfilled capability_recipes.json; the merge still applies to this session.");
                }
            }
            return config;
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> ResolveCapabilityRecipes(
            CapabilityRecipesConfig config, SeRecipeRegistry registry, FactionTuning tuning,
            IEnumerable<string> geographyOres)
        {
            var result = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>();
            if (config == null) return result;

            // What the economy can actually obtain: what nodes mint, closed over the whole recipe
            // set. Recipes rooted outside it can never run, so they only ever post demand nothing
            // can fill -- the derived half of RecipeExclusions.
            HashSet<string> minted = RecipeReachability.MintedExactKeys(tuning, geographyOres);
            // Synthetic recipes join the closure, not just the capability lists: they are the only
            // producers of Ingot/PrototechScrap and Component/PrototechFrame in this world, so
            // leaving them out would make their own outputs read as unobtainable and the derived
            // exclusion rule would drop the very recipes that consume them.
            IList<ConversionRecipe> synthetics = config.AllSyntheticRecipes();
            var closureRecipes = new List<ConversionRecipe>(registry.AllRecipes);
            closureRecipes.AddRange(synthetics);
            HashSet<string> reachable = RecipeReachability.Resolve(minted, closureRecipes);
            Log.Info(string.Format(
                "RecipeReachability: {0} minted material(s) close over {1} recipe(s) ({2} synthetic) to {3} obtainable material(s).",
                minted.Count, closureRecipes.Count, synthetics.Count, reachable.Count));

            RecipeExclusions exclusions = RecipeExclusions.From(config, reachable);
            var excluded = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // The union of both maps, not just Capabilities: FuelDepot is named only by GasBlocks,
            // because gas production has no blueprint class to list there.
            var capabilityNames = new List<string>();
            foreach (var capEntry in config.Capabilities) capabilityNames.Add(capEntry.Key);
            if (config.GasBlocks != null)
                foreach (var gasEntry in config.GasBlocks)
                    if (!capabilityNames.Contains(gasEntry.Key)) capabilityNames.Add(gasEntry.Key);
            if (config.FarmBlocks != null)
                foreach (var farmEntry in config.FarmBlocks)
                    if (!capabilityNames.Contains(farmEntry.Key)) capabilityNames.Add(farmEntry.Key);

            foreach (string capabilityName in capabilityNames)
            {
                NodeCapabilityKind cap;
                if (!Enum.TryParse(capabilityName, out cap)) continue;

                List<string> classNames = null;
                Dictionary<string, List<string>> levels;
                if (config.Capabilities.TryGetValue(capabilityName, out levels) && levels != null)
                    levels.TryGetValue("1", out classNames);
                if (classNames == null) classNames = new List<string>();

                var recipes = new List<ConversionRecipe>();
                foreach (string className in classNames)
                {
                    MyBlueprintClassDefinition classDef = null;
                    try { classDef = MyDefinitionManager.Static != null
                                         ? MyDefinitionManager.Static.GetBlueprintClass(className)
                                         : null; }
                    catch { }
                    if (classDef == null) continue;

                    foreach (MyBlueprintDefinitionBase bp in classDef)
                    {
                        if (bp == null) continue;
                        ConversionRecipe recipe = registry.GetRecipeByBlueprintId(bp.Id.SubtypeName);
                        if (recipe == null) continue;
                        // Recipes the managers must never see -- see RecipeExclusions for what the
                        // two rules match. Dropping them here is what keeps the host and the sim on
                        // one set: WriteProductionModelFile publishes this same resolved map.
                        string exclusionReason;
                        if (exclusions.ShouldExclude(recipe, out exclusionReason))
                        {
                            excluded[recipe.BlueprintId ?? bp.Id.SubtypeName] = exclusionReason;
                            continue;
                        }
                        recipes.Add(recipe);
                    }
                }

                // Gas generators, which belong to no blueprint class -- see CapabilityRecipesConfig's
                // GasBlocks remarks. Same exclusion gate as everything else: a gas recipe whose Ice
                // has no source in this world must not reach a manager either.
                List<string> gasBlockKeys = null;
                if (config.GasBlocks != null) config.GasBlocks.TryGetValue(capabilityName, out gasBlockKeys);
                if (gasBlockKeys != null)
                {
                    foreach (string blockKey in gasBlockKeys)
                    {
                        int before = recipes.Count;
                        foreach (ConversionRecipe recipe in registry.GetGasRecipesForBlock(blockKey))
                        {
                            string gasExclusionReason;
                            if (exclusions.ShouldExclude(recipe, out gasExclusionReason))
                            {
                                excluded[recipe.BlueprintId ?? blockKey] = gasExclusionReason;
                                continue;
                            }
                            recipes.Add(recipe);
                        }
                        if (recipes.Count == before)
                            Log.Warn(string.Format(
                                "Capability {0}: gas block '{1}' resolved no recipes. Either no MyOxygenGeneratorDefinition carries that key, or every gas it makes was excluded.",
                                capabilityName, blockKey));
                    }
                }

                // Farm plots, which belong to no blueprint class either -- see CapabilityRecipesConfig's
                // FarmBlocks remarks. The registry holds SE's own water figure; the ice scale is a
                // balance decision and is applied here, to a copy, so the cache stays SE's numbers.
                List<string> farmBlockKeys = null;
                if (config.FarmBlocks != null) config.FarmBlocks.TryGetValue(capabilityName, out farmBlockKeys);
                if (farmBlockKeys != null)
                {
                    foreach (string blockKey in farmBlockKeys)
                    {
                        int before = recipes.Count;
                        foreach (ConversionRecipe seRecipe in registry.GetFarmRecipesForBlock(blockKey))
                        {
                            ConversionRecipe recipe = ScaleIce(seRecipe, tuning != null ? tuning.FarmIceScale : 1.0);
                            string farmExclusionReason;
                            if (exclusions.ShouldExclude(recipe, out farmExclusionReason))
                            {
                                excluded[recipe.BlueprintId ?? blockKey] = farmExclusionReason;
                                continue;
                            }
                            recipes.Add(recipe);
                        }
                        if (recipes.Count == before)
                            Log.Warn(string.Format(
                                "Capability {0}: farm block '{1}' resolved no recipes. Either the block carries no farm-plot logic, no seed it accepts is defined, or every crop was excluded.",
                                capabilityName, blockKey));
                    }
                }

                // Recipes this world defines and SE does not -- see CapabilityRecipesConfig's
                // SyntheticRecipes remarks. Same exclusion gate as everything else, so a synthetic
                // recipe rooted in a material nothing supplies is dropped like any other.
                int synthAdded = 0;
                foreach (ConversionRecipe recipe in config.SyntheticRecipesFor(capabilityName))
                {
                    string synthExclusionReason;
                    if (exclusions.ShouldExclude(recipe, out synthExclusionReason))
                    {
                        excluded[recipe.BlueprintId ?? "(unnamed synthetic)"] = synthExclusionReason;
                        continue;
                    }
                    recipes.Add(recipe);
                    synthAdded++;
                }

                // Sort by descending total input amount so resource-intensive recipes run first.
                // This prevents cheap recipes (e.g. ConstructionComponent: 8 Iron) from consuming
                // all shared ingots before expensive ones (e.g. SteelPlate: 21 Iron) get a turn.
                recipes.Sort((a, b) =>
                {
                    double sumA = 0; if (a.Inputs != null) foreach (var inp in a.Inputs) sumA += inp.Amount;
                    double sumB = 0; if (b.Inputs != null) foreach (var inp in b.Inputs) sumB += inp.Amount;
                    return sumB.CompareTo(sumA); // descending
                });

                // Name both sources: a capability can now draw on blueprint classes, gas blocks or
                // both, and "resolved 0" is unreadable without knowing which of them was asked.
                string sources = string.Join(", ", classNames);
                if (gasBlockKeys != null && gasBlockKeys.Count > 0)
                    sources = (sources.Length > 0 ? sources + ", " : "") + "gas:" + string.Join("+", gasBlockKeys);
                if (farmBlockKeys != null && farmBlockKeys.Count > 0)
                    sources = (sources.Length > 0 ? sources + ", " : "") + "farm:" + string.Join("+", farmBlockKeys);
                if (synthAdded > 0)
                    sources = (sources.Length > 0 ? sources + ", " : "") + "synthetic:" + synthAdded;
                if (sources.Length == 0) sources = "(none)";

                if (recipes.Count > 0) result[cap] = recipes;
                else Log.Warn(string.Format("Capability {0} (level 1) resolved no recipes from source(s): {1}.",
                    capabilityName, sources));
                Log.Info(string.Format("Capability {0} (level 1) resolved {1} recipe(s) from source(s): {2}.",
                    capabilityName, recipes.Count, sources));
            }

            if (excluded.Count > 0)
            {
                var parts = new List<string>(excluded.Count);
                foreach (var ex in excluded) parts.Add(ex.Key + " (" + ex.Value + ")");
                Log.Info(string.Format("capability_recipes.json: excluded {0} recipe(s) from every capability: {1}.",
                    excluded.Count, string.Join(", ", parts)));
            }

            if (result.Count == 0)
                Log.Warn("capability_recipes.json resolved 0 capability kind(s); ProductionManager will be idle.");
            return result;
        }

        /// <summary>A copy of <paramref name="recipe"/> with its ice inputs multiplied by
        /// <paramref name="scale"/> — <see cref="FactionTuning.FarmIceScale"/>, which moves what a
        /// crop costs in ice without changing SE's ratios between crops.</summary>
        private static ConversionRecipe ScaleIce(ConversionRecipe recipe, double scale)
        {
            if (recipe == null || recipe.Inputs == null || !(scale > 0) || scale == 1.0) return recipe;
            var inputs = new RecipeItem[recipe.Inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                RecipeItem item = recipe.Inputs[i];   // a struct: this is already a copy
                if (item.TypeId == "MyObjectBuilder_Ore" && item.SubTypeId == "Ice") item.Amount *= scale;
                inputs[i] = item;
            }
            return new ConversionRecipe
            {
                BlueprintId = recipe.BlueprintId,
                Inputs = inputs,
                Outputs = recipe.Outputs,
                TimeSecs = recipe.TimeSecs,
                IgnoresEfficiencyMultiplier = recipe.IgnoresEfficiencyMultiplier,
            };
        }

        /// <summary>
        /// Writes <c>production_model.json</c>: the resolved machine profiles plus the resolved
        /// per-capability recipe lists. Same cache-file role as <c>item_recipe_cache.json</c> — it
        /// exists so <c>Factions.Sim</c> can run a real <c>ProductionManager</c> offline, which it
        /// otherwise cannot, because <c>capability_recipes.json</c> holds SE blueprint *class* names
        /// that only <c>MyDefinitionManager.GetBlueprintClass</c> can expand.
        /// </summary>
        private void WriteProductionModelFile(Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            try
            {
                var doc = new ProductionModelDoc
                {
                    ProductivityBonus = _tuning.ProductionProductivityBonus,
                    Profiles = new Dictionary<string, MachineProfile>(),
                    Recipes  = new Dictionary<string, List<ConversionRecipe>>(),
                };
                foreach (var kv in _tuning.MachineProfiles) doc.Profiles[kv.Key.ToString()] = kv.Value;
                if (capRecipes != null)
                    foreach (var kv in capRecipes) doc.Recipes[kv.Key.ToString()] = kv.Value;

                File.WriteAllText(Path.Combine(StorageDir, "production_model.json"),
                    JsonConvert.SerializeObject(doc, Formatting.Indented));
            }
            catch (Exception ex) { Log.Warn(ex, "Failed to write production_model.json."); }
        }

        /// <summary>Shape of <c>production_model.json</c>. Capability keys are
        /// <see cref="NodeCapabilityKind"/> names so the file stays readable and survives enum reordering.</summary>
        private sealed class ProductionModelDoc
        {
            public double ProductivityBonus { get; set; }
            public Dictionary<string, MachineProfile> Profiles { get; set; }
            public Dictionary<string, List<ConversionRecipe>> Recipes { get; set; }
        }

        /// <summary>
        /// Resolves each capability's machine physics into the SE-free <see cref="MachineProfile"/>
        /// that <c>Factions.Core</c> consumes: the block definition named in
        /// <c>capability_recipes.json</c>'s <c>MachineBlocks</c> map supplies the base speed and
        /// material efficiency, and the world's own session settings supply the speed and
        /// assembler-efficiency multipliers. A capability whose block fails to resolve keeps Core's
        /// default base speed but still receives the world multipliers — those are facts about the
        /// world, not about the block.
        ///
        /// <para>API confirmed against decompiled SE source (2026-08-19):
        /// <c>MyRefineryDefinition.RefineSpeed</c> and <c>.MaterialEfficiency</c> are public float
        /// fields; <c>MyAssemblerDefinition.AssemblySpeed</c> is a public float property and
        /// <c>.IgnoreEfficiencyMultiplier</c> a public bool field;
        /// <c>MySession.Static.RefinerySpeedMultiplier</c>, <c>.AssemblerSpeedMultiplier</c> and
        /// <c>.AssemblerEfficiencyMultiplier</c> are public float properties delegating to
        /// <c>Settings</c>. Machine count stays Core's value — how many machines a capability level
        /// represents is a Factions modelling choice, not an SE constant.</para>
        /// </summary>
        private static Dictionary<NodeCapabilityKind, MachineProfile> ResolveMachineProfiles(
            CapabilityRecipesConfig config)
        {
            var result = new Dictionary<NodeCapabilityKind, MachineProfile>();

            double refineryWorld = 1.0, assemblerWorld = 1.0, assemblerEfficiency = 1.0;
            try
            {
                if (MySession.Static != null)
                {
                    refineryWorld       = MySession.Static.RefinerySpeedMultiplier;
                    assemblerWorld      = MySession.Static.AssemblerSpeedMultiplier;
                    assemblerEfficiency = MySession.Static.AssemblerEfficiencyMultiplier;
                }
            }
            catch (Exception ex) { Log.Warn(ex, "Failed to read world production multipliers; machine profiles will use 1.0."); }
            if (refineryWorld       <= 0) refineryWorld       = 1.0;
            if (assemblerWorld      <= 0) assemblerWorld      = 1.0;
            if (assemblerEfficiency <= 0) assemblerEfficiency = 1.0;

            Dictionary<string, string> machineBlocks =
                config != null && config.MachineBlocks != null
                    ? config.MachineBlocks
                    : new Dictionary<string, string>();

            // Every kind, not FactionTuning.AllCapabilities: that list is what the brain may install
            // unasked, and a kind left off it still runs recipes once something installs it.
            foreach (NodeCapabilityKind cap in Enum.GetValues(typeof(NodeCapabilityKind)))
            {
                MachineProfile profile = MachinePhysics.DefaultProfile(cap);
                if (profile.MachineCount <= 0) continue;   // Shipyard runs no recipes

                // A gas generator is neither a refinery nor an assembler: SE has no
                // GasGeneratorSpeedMultiplier and no efficiency divisor for it, so both stay neutral
                // and its whole rate comes from the recipe's own IceConsumptionPerSecond. A farm plot
                // is the same -- no world multiplier touches growth, and the recipe carries SE's
                // own growth time.
                if (cap == NodeCapabilityKind.FuelDepot || cap == NodeCapabilityKind.Farm)
                {
                    result[cap] = profile;
                    continue;
                }

                bool refineryTier = cap == NodeCapabilityKind.Refinery
                                 || cap == NodeCapabilityKind.RefineryII
                                 || cap == NodeCapabilityKind.PrototechRefinery;
                profile.WorldSpeedMultiplier = refineryTier ? refineryWorld : assemblerWorld;
                if (!refineryTier) profile.InputEfficiencyDivisor = assemblerEfficiency;

                // MachineBlocks is an override, not the only source: an instance whose
                // capability_recipes.json predates the key (it is only seeded when the file is
                // absent) must still resolve real block definitions rather than silently keeping
                // Core's neutral MaterialEfficiency.
                string subtypeId;
                if (!machineBlocks.TryGetValue(cap.ToString(), out subtypeId) || string.IsNullOrEmpty(subtypeId))
                    subtypeId = CapabilityRecipesConfig.DefaultMachineBlock(cap.ToString());
                if (!string.IsNullOrEmpty(subtypeId))
                {
                    MyCubeBlockDefinition blockDef = null;
                    try
                    {
                        if (MyDefinitionManager.Static != null)
                        {
                            foreach (string typeIdCandidate in new[] { "MyObjectBuilder_Refinery", "MyObjectBuilder_Assembler" })
                            {
                                var id = new MyDefinitionId(MyObjectBuilderType.Parse(typeIdCandidate), subtypeId);
                                if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out blockDef) && blockDef != null)
                                    break;
                            }
                        }
                    }
                    catch (Exception ex) { Log.Warn(ex, "Failed to resolve machine block '" + subtypeId + "' for " + cap + "."); }

                    var refineryDef  = blockDef as MyRefineryDefinition;
                    var assemblerDef = blockDef as MyAssemblerDefinition;
                    if (refineryDef != null)
                    {
                        if (refineryDef.RefineSpeed > 0) profile.BaseSpeed = refineryDef.RefineSpeed;
                        if (refineryDef.MaterialEfficiency > 0) profile.MaterialEfficiency = refineryDef.MaterialEfficiency;
                        profile.SourceBlockSubtypeId = subtypeId;
                    }
                    else if (assemblerDef != null)
                    {
                        if (assemblerDef.AssemblySpeed > 0) profile.BaseSpeed = assemblerDef.AssemblySpeed;
                        if (assemblerDef.IgnoreEfficiencyMultiplier) profile.InputEfficiencyDivisor = 1.0;
                        profile.SourceBlockSubtypeId = subtypeId;
                    }
                    else
                    {
                        Log.Warn(string.Format("MachineBlocks[{0}]='{1}' did not resolve to a refinery/assembler definition; {0} keeps its default base speed.",
                            cap, subtypeId));
                    }
                }

                result[cap] = profile;
            }

            return result;
        }

        /// <summary>
        /// Reads <c>capability_recipes.json</c>'s <c>InstallBlocks</c> map and resolves each named SE
        /// cube block's real construction BOM (<see cref="MyCubeBlockDefinition.Components"/>) into a
        /// plain component-subtype → count dictionary, keyed the same way
        /// <c>ProductionManager.InvKey</c> keys manufactured components (<c>"Comp:&lt;SubtypeId&gt;"</c>).
        /// A capability whose block fails to resolve (missing config entry, typo'd subtype, or the
        /// definition isn't found) is simply omitted from the result — <c>FactionTuning</c> callers
        /// fall back to the flat <see cref="ItemKind"/> install cost for any capability with no BOM.
        /// Keeps SE APIs out of <c>Factions.Core</c>, mirroring <see cref="ResolveCapabilityRecipes"/>.
        /// </summary>
        /// <summary>The cube block an <c>InstallBlocks</c> key names, by SubtypeId first and then by
        /// BlockPairName among the blocks whose SubtypeId is empty.
        ///
        /// <para>The pair-name arm is the exact inverse of <c>SeRecipeRegistry.GasBlockKey</c>, and
        /// exists for the same reason: SE's large O2/H2 Generator ships
        /// <c>&lt;SubtypeId&gt;&lt;/SubtypeId&gt;</c>, so a subtype-only lookup cannot find the one
        /// block <c>FuelDepot</c> installs from. Restricting the arm to empty-subtype blocks keeps it
        /// unambiguous — a pair name is otherwise shared by the large and small variants.</para></summary>
        private static MyCubeBlockDefinition ResolveInstallBlockDefinition(string blockKey)
        {
            if (MyDefinitionManager.Static == null || string.IsNullOrEmpty(blockKey)) return null;

            MyCubeBlockDefinition blockDef = null;
            foreach (string typeIdCandidate in new[]
                     { "MyObjectBuilder_Refinery", "MyObjectBuilder_Assembler", "MyObjectBuilder_OxygenGenerator",
                       "MyObjectBuilder_FunctionalBlock", "MyObjectBuilder_MedicalRoom", "MyObjectBuilder_CryoChamber",
                       "MyObjectBuilder_Kitchen" })
            {
                MyObjectBuilderType typeId;
                try { typeId = MyObjectBuilderType.Parse(typeIdCandidate); }
                catch { continue; }
                var id = new MyDefinitionId(typeId, blockKey);
                if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out blockDef) && blockDef != null)
                    return blockDef;
            }

            foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var candidate = defBase as MyCubeBlockDefinition;
                if (candidate == null) continue;
                if (!string.IsNullOrEmpty(candidate.Id.SubtypeName)) continue;
                if (string.Equals(candidate.BlockPairName, blockKey, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        private static Dictionary<NodeCapabilityKind, Dictionary<string, long>> ResolveInstallBoms(
            CapabilityRecipesConfig config,
            Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            var result = new Dictionary<NodeCapabilityKind, Dictionary<string, long>>();
            if (config == null || config.InstallBlocks == null) return result;

            foreach (var entry in config.InstallBlocks)
            {
                NodeCapabilityKind cap;
                if (!Enum.TryParse(entry.Key, out cap)) continue;
                if (string.IsNullOrEmpty(entry.Value)) continue;

                MyCubeBlockDefinition blockDef = null;
                try { blockDef = ResolveInstallBlockDefinition(entry.Value); }
                catch (Exception ex) { Log.Warn(ex, "Failed to resolve install block '" + entry.Value + "' for " + entry.Key + "."); }

                if (blockDef == null || blockDef.Components == null)
                {
                    Log.Warn(string.Format("InstallBlocks[{0}]='{1}' did not resolve to a cube block definition; {0} will use its flat ItemKind install cost.",
                        entry.Key, entry.Value));
                    continue;
                }

                var bom = new Dictionary<string, long>();
                InstallBomBuilder.Add(bom, BlockComponents(blockDef), 1);

                // Blocks the level installs beside its machine (a Farm's plots), each count times.
                string extras = "";
                Dictionary<string, int> extraBlocks;
                if (config.InstallExtraBlocks != null
                    && config.InstallExtraBlocks.TryGetValue(entry.Key, out extraBlocks) && extraBlocks != null)
                {
                    foreach (KeyValuePair<string, int> extra in extraBlocks)
                    {
                        if (extra.Value <= 0 || string.IsNullOrEmpty(extra.Key)) continue;
                        MyCubeBlockDefinition extraDef = null;
                        try { extraDef = ResolveInstallBlockDefinition(extra.Key); }
                        catch (Exception ex) { Log.Warn(ex, "Failed to resolve install block '" + extra.Key + "' for " + entry.Key + "."); }
                        if (extraDef == null || extraDef.Components == null)
                        {
                            Log.Warn(string.Format("InstallExtraBlocks[{0}]['{1}'] did not resolve to a cube block definition; {0} is charged without it.",
                                entry.Key, extra.Key));
                            continue;
                        }
                        InstallBomBuilder.Add(bom, BlockComponents(extraDef), extra.Value);
                        extras += " + " + extra.Value + "x '" + extra.Key + "'";
                    }
                }

                // A block made of what only this capability makes cannot be its own entry price --
                // see InstallBomSelfReference for why that is a deadlock and not a discount.
                List<string> selfProduced = InstallBomSelfReference.StripSelfProduced(cap, bom, capRecipes);

                if (bom.Count > 0)
                {
                    result[cap] = bom;
                    Log.Info(string.Format("InstallBlocks[{0}]='{1}'{4} resolved a {2}-component install BOM{3}.",
                        entry.Key, entry.Value, bom.Count,
                        selfProduced.Count > 0
                            ? ", less " + selfProduced.Count + " component(s) only " + entry.Key
                              + " can make: " + string.Join(", ", selfProduced.ToArray())
                            : "",
                        extras));
                }
                else
                {
                    Log.Warn(string.Format("InstallBlocks[{0}]='{1}' resolved a block with no payable components; {0} will use its flat ItemKind install cost.",
                        entry.Key, entry.Value));
                }
            }

            return result;
        }

        /// <summary>The beds one Habitat level installs: every <c>CryoChamber</c>-typed block among its
        /// <c>InstallBlocks</c> entry and its <c>InstallExtraBlocks</c>, times its count. SE models every
        /// bed as a cryo chamber, so this is the same count the prefab catalogue's
        /// <c>CrewLayout.CryoChambers</c> makes for a hull. Zero when nothing resolves. FAC-493.</summary>
        private static int ResolveHabitatBeds(CapabilityRecipesConfig config)
        {
            if (config == null) return 0;
            string key = NodeCapabilityKind.Habitat.ToString();
            int beds = 0;
            string main;
            if (config.InstallBlocks != null && config.InstallBlocks.TryGetValue(key, out main)
                && ResolveInstallBlockDefinition(main) is MyCryoChamberDefinition)
                beds++;
            Dictionary<string, int> extras;
            if (config.InstallExtraBlocks != null && config.InstallExtraBlocks.TryGetValue(key, out extras) && extras != null)
                foreach (KeyValuePair<string, int> extra in extras)
                    if (extra.Value > 0 && ResolveInstallBlockDefinition(extra.Key) is MyCryoChamberDefinition)
                        beds += extra.Value;
            return beds;
        }

        /// <summary>A block definition's construction list as (component subtype, count) pairs, one
        /// per listed stage, for <see cref="InstallBomBuilder.Add"/>.</summary>
        private static IEnumerable<KeyValuePair<string, int>> BlockComponents(MyCubeBlockDefinition blockDef)
        {
            foreach (MyCubeBlockDefinition.Component component in blockDef.Components)
            {
                if (component.Definition == null) continue;
                yield return new KeyValuePair<string, int>(component.Definition.Id.SubtypeName, component.Count);
            }
        }

        /// <summary>
        /// Whole-prefab counterpart to <see cref="ResolveInstallBoms"/> for a capability with no
        /// single representative SE block (e.g. <c>Shipyard</c> — vanilla SE has no "Shipyard" cube
        /// block, only whole prefab ships/stations named for the role). Sums every block's
        /// <see cref="MyCubeBlockDefinition.Components"/> across the named prefab's <c>CubeGrids</c>
        /// into one BOM — the same walk <c>PrefabBookValueCache.ComputePrefabBookValue</c> already
        /// does per-block for prefab book value, just accumulating component counts instead of
        /// converting to credits.
        /// </summary>
        private static Dictionary<NodeCapabilityKind, Dictionary<string, long>> ResolveInstallPrefabBoms(
            CapabilityRecipesConfig config)
        {
            var result = new Dictionary<NodeCapabilityKind, Dictionary<string, long>>();
            if (config == null || config.InstallPrefabs == null || config.InstallPrefabs.Count == 0) return result;

            IEnumerable<KeyValuePair<string, MyPrefabDefinition>> prefabDefs = null;
            try { if (MyDefinitionManager.Static != null) prefabDefs = MyDefinitionManager.Static.GetPrefabDefinitions(); }
            catch (Exception ex) { Log.Warn(ex, "Failed to load SE prefab definitions for InstallPrefabs."); }
            if (prefabDefs == null) return result;

            foreach (var entry in config.InstallPrefabs)
            {
                NodeCapabilityKind cap;
                if (!Enum.TryParse(entry.Key, out cap)) continue;
                if (string.IsNullOrEmpty(entry.Value)) continue;

                MyPrefabDefinition prefab = null;
                foreach (var pkv in prefabDefs)
                {
                    if (!string.Equals(pkv.Key, entry.Value, StringComparison.OrdinalIgnoreCase)) continue;
                    prefab = pkv.Value;
                    break;
                }
                if (prefab == null || prefab.CubeGrids == null)
                {
                    Log.Warn(string.Format("InstallPrefabs[{0}]='{1}' did not resolve to a prefab definition; {0} will use its flat ItemKind install cost.",
                        entry.Key, entry.Value));
                    continue;
                }

                var bom = new Dictionary<string, long>();
                foreach (MyObjectBuilder_CubeGrid grid in prefab.CubeGrids)
                {
                    if (grid == null || grid.CubeBlocks == null) continue;
                    foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                    {
                        if (block == null) continue;
                        MyCubeBlockDefinition blockDef;
                        try
                        {
                            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out blockDef)) continue;
                        }
                        catch { continue; }
                        if (blockDef == null || blockDef.Components == null) continue;

                        foreach (MyCubeBlockDefinition.Component component in blockDef.Components)
                        {
                            if (component.Count <= 0 || component.Definition == null) continue;
                            string subTypeId = component.Definition.Id.SubtypeName;
                            if (string.IsNullOrEmpty(subTypeId)) continue;
                            string invKey = "Comp:" + subTypeId;
                            long cur;
                            bom.TryGetValue(invKey, out cur);
                            bom[invKey] = cur + component.Count;
                        }
                    }
                }

                if (bom.Count > 0)
                {
                    result[cap] = bom;
                    Log.Info(string.Format("InstallPrefabs[{0}]='{1}' resolved a {2}-component install BOM.", entry.Key, entry.Value, bom.Count));
                }
                else
                {
                    Log.Warn(string.Format("InstallPrefabs[{0}]='{1}' resolved a prefab with no components; {0} will use its flat ItemKind install cost.",
                        entry.Key, entry.Value));
                }
            }

            return result;
        }

        /// <summary>Flush the dry-run sim and close the decision log. Called on session-unload and dispose.</summary>
        /// <summary>
        /// Path to a read-only input, preferring the data directory and falling back to this
        /// instance's own. Logs when it falls back: these loaders all treat a missing file as
        /// "feature off", so an input in the wrong place is silent, not fatal.
        ///
        /// <para>Read-only is the whole test. Anything that writes back must resolve from the data
        /// directory alone — see <c>custom_prefabs</c> at its call site.</para>
        /// </summary>
        /// <summary>Bare ore subtypes carried by a <see cref="NodeYieldEntry.MineOnly"/> row anywhere
        /// in the node-kind yield table — stone today. These are effort-gated and ubiquitous, so the
        /// surface pass must not credit them as a deposit. Derived, not listed.</summary>
        private static IEnumerable<string> UbiquitousOreNames(FactionTuning tuning)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tuning == null || tuning.NodeYieldTable == null) return seen;
            foreach (NodeYieldEntry e in tuning.NodeYieldTable)
            {
                if (e == null || !e.MineOnly || string.IsNullOrEmpty(e.SubTypeId)) continue;
                int colon = e.SubTypeId.IndexOf(':');
                seen.Add(colon >= 0 && colon + 1 < e.SubTypeId.Length
                    ? e.SubTypeId.Substring(colon + 1)
                    : e.SubTypeId);
            }
            return seen;
        }

        private string InputPath(string fileName, bool directory = false)
        {
            bool usedFallback;
            Func<string, bool> exists = directory
                ? (Func<string, bool>)Directory.Exists
                : File.Exists;
            string path = DataDirectory.ResolveInput(fileName, StorageDir, InstanceDir, exists, out usedFallback);
            if (usedFallback)
                Log.Info(fileName + ": not in the data directory, read from this instance ("
                       + path + "). Move it beside the shared data to share it across the cluster.");
            if (!directory)
            {
                WarnIfInputCopiesDiffer(fileName);
                LogInputRead(fileName, path);
            }
            return path;
        }

        private readonly HashSet<string> _inputDivergenceWarned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Warn when an input sits in both the data and the instance directory with different
        /// content (FAC-241): <see cref="DataDirectory.ResolveInput"/> reads the data-directory copy
        /// whenever it exists, so a stale one there silently shadows whatever a deploy or an
        /// operator just wrote to the instance. Returns the line so a verb can put it in its reply.
        /// <para>Logged once per distinct line per process: several loaders resolve the same file,
        /// and the line carries both mtimes and sizes, so a copy that changes again warns again.</para>
        /// </summary>
        private readonly HashSet<string> _inputReadLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// One <c>INPUT_READ</c> line naming the copy of an input actually read, with mtime and size
        /// (FAC-241). Once per distinct line per process: several loaders read the same file, and a
        /// copy that changes logs again because its mtime and size are in the line.
        /// </summary>
        private void LogInputRead(string fileName, string path)
        {
            string line;
            try { line = DataDirectory.DescribeRead(fileName, path); }
            catch (Exception) { return; }   // the loader that reads it reports its own failure
            if (line == null) return;

            bool first;
            lock (_inputReadLogged) first = _inputReadLogged.Add(line);
            if (first) Log.Info(line);
        }

        private string WarnIfInputCopiesDiffer(string fileName)
        {
            return WarnIfInputCopiesDiffer(fileName, StorageDir);
        }

        /// <summary>As above, against an explicit data directory: Init reads before
        /// <see cref="StorageDir"/> is redirected.</summary>
        private string WarnIfInputCopiesDiffer(string fileName, string dataDir)
        {
            string line;
            try { line = DataDirectory.DescribeDivergence(fileName, dataDir, InstanceDir); }
            catch (Exception ex)
            {
                Log.Warn(fileName + ": could not compare the data-directory and instance copies ("
                       + ex.Message + ").");
                return null;
            }
            if (line == null) return null;

            bool first;
            lock (_inputDivergenceWarned) first = _inputDivergenceWarned.Add(line);
            if (first) Log.Warn(line);
            return line;
        }

        /// <summary>
        /// One loud line when captures sit in this instance's own folder while the data directory
        /// has none. Silent divergence is the failure mode: each instance would accumulate its own
        /// captures, and the day the shared folder appears they all drop out of view at once.
        /// </summary>
        private void WarnIfStrandedCustomPrefabs(string dataDirPath)
        {
            if (string.Equals(StorageDir, InstanceDir, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(dataDirPath)) return;

            string local = Path.Combine(InstanceDir, "custom_prefabs");
            if (!Directory.Exists(local)) return;

            int count = 0;
            try { count = Directory.GetFiles(local, "*.sbc").Length; } catch { }
            Log.Warn(count + " captured prefab(s) at " + local + " are not visible to the cluster. "
                   + "Move that folder to " + dataDirPath + " -- captures are written where they are "
                   + "read, so they will not be picked up from the instance directory.");
        }

        /// <summary>
        /// Points <see cref="StorageDir"/>/<see cref="Store"/> at the configured data directory.
        /// Returns false when a directory was configured but cannot be used, in which case the
        /// caller must not start: falling back to the instance directory would quietly write the
        /// cluster's data to the wrong place and leave a migration behind.
        /// </summary>
        private bool ApplyDataDirectory()
        {
            string configured = _tuning.DataDirectory;
            if (!DataDirectory.IsRedirected(configured))
            {
                Log.Info("Data directory: " + StorageDir + " (this instance).");
                return true;
            }

            string resolved = DataDirectory.Resolve(configured, InstanceDir);
            try
            {
                Directory.CreateDirectory(resolved);
                string probe = Path.Combine(resolved, ".factions-write-probe");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Configured DataDirectory '" + configured + "' (resolved to '"
                            + resolved + "') is not usable. Refusing to start rather than writing "
                            + "this instance's data to the wrong place. Fix tuning.json and restart.");
                return false;
            }

            StorageDir = resolved;
            Store = new FactionStore(resolved);
            Log.Info("Data directory: " + resolved + " (shared; tuning.json and the bridge stay in "
                   + InstanceDir + ").");
            return true;
        }

        /// <summary>Records the data directory the last boot resolved to. Lives in the instance
        /// directory on purpose — a marker inside the data directory would move with it and could
        /// never witness the directory changing.</summary>
        private const string RedirectMarkerFileName = ".factions-datadir";

        /// <summary>
        /// Two alarms for a mis-pointed data directory, run once after the directory is resolved.
        /// Neither changes resolution; both only report. Silent when nothing is wrong.
        /// </summary>
        private void CheckDataDirectoryGuards()
        {
            bool redirected = !string.Equals(StorageDir, InstanceDir, StringComparison.OrdinalIgnoreCase);

            // (1) Did this boot land somewhere other than the last one? Catches a silent revert --
            // tuning.json is per-instance and regenerable, and tuning_defaults.json has no key for
            // DataDirectory, so a regenerated file comes back empty and means the instance dir.
            string markerPath = Path.Combine(InstanceDir, RedirectMarkerFileName);
            string remembered = null;
            try { if (File.Exists(markerPath)) remembered = File.ReadAllText(markerPath).Trim(); }
            catch (Exception ex) { Log.Warn(ex, "Could not read " + RedirectMarkerFileName + "."); }

            if (DataDirectory.RedirectChanged(remembered, StorageDir))
                Log.Warn("Data directory CHANGED since the last boot: was '" + remembered
                       + "', now '" + StorageDir + "'. If that was not deliberate, the previous "
                       + "directory still holds this world's brains and index -- fix "
                       + "DataDirectory in " + Path.Combine(InstanceDir, "tuning.json")
                       + " and restart before anything writes to the new one.");

            try { File.WriteAllText(markerPath, StorageDir); }
            catch (Exception ex) { Log.Warn(ex, "Could not write " + RedirectMarkerFileName + "."); }

            // (2) Is the registry we are about to use empty while this instance holds a populated
            // one? That disagreement is never intentional. The reverse -- data dir populated,
            // instance empty -- is the normal steady state and stays silent.
            if (!redirected) return;
            try
            {
                int resolvedCount = Store.LoadIndex().Count;
                int instanceCount = new FactionStore(InstanceDir).LoadIndex().Count;
                if (DataDirectory.RegistrySwapLooksWrong(redirected, resolvedCount, instanceCount))
                    Log.Error("Registry mismatch: the data directory '" + StorageDir + "' has an "
                            + "EMPTY registry while this instance's own directory '" + InstanceDir
                            + "' holds " + instanceCount + " faction(s). Starting here means a "
                            + "landless world, and an empty registry is indistinguishable from a "
                            + "world before 'factions init' -- which is why this is said out loud. "
                            + "Check DataDirectory in tuning.json before letting anything write.");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Registry comparison failed; continuing.");
            }
        }

        /// <summary>
        /// Says so when a <c>logs\</c> directory still exists under the INSTANCE while the decision
        /// log is being written somewhere else.
        ///
        /// <para>A <c>DataDirectory</c> redirect leaves the old tree in place by design -- it is
        /// wanted as a rollback -- so the stale files are not a fault. The fault is that nothing
        /// says which path is live, and the old one keeps answering questions with data frozen at
        /// the moment of the cutover. It fails as silence rather than as an error, which is the
        /// worst way for a diagnostic channel to fail (FAC-095).</para>
        ///
        /// <para>Warn-level and once per boot: it is addressed to whoever is about to grep the
        /// wrong directory, and it stops being emitted the moment the stale tree is removed.</para>
        /// </summary>
        private void WarnAboutStaleInstanceLogs(string liveLogDir)
        {
            try
            {
                string instanceLogDir = Path.Combine(InstanceDir, "logs");
                if (string.Equals(Path.GetFullPath(instanceLogDir).TrimEnd('\\'),
                                  Path.GetFullPath(liveLogDir).TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase))
                    return;                                   // not redirected; nothing to confuse
                if (!Directory.Exists(instanceLogDir)) return; // no ghost tree

                string[] stale = Directory.GetFiles(instanceLogDir, "Factions-*.log");
                if (stale.Length == 0) return;

                Log.Warn("Stale decision logs remain at " + instanceLogDir + " (" + stale.Length
                       + " file(s)). They are NOT being written to -- the live log is " + liveLogDir
                       + ". They will not error when read, they will simply be frozen, so anything "
                       + "grepping the old path sees silence rather than a wrong answer.");
            }
            catch (Exception ex)
            {
                // A diagnostic must never be the thing that breaks a boot.
                Log.Warn(ex, "Could not check for stale instance logs.");
            }
        }

        /// <summary>
        /// The agent half of FAC-066: a deferred server that still answers its own clients. No
        /// runtime and no feature managers -- the uplink, the two managers whose data is this
        /// server's own physics, and the relay to the authority for everything else.
        /// </summary>
        /// <summary>Agent: DecisionLog lines written here, held until the authority files them.</summary>
        private NexusLogOutbox _agentLogOutbox = new NexusLogOutbox();
        /// <summary>Authority: the high-water mark of each agent's DecisionLog lines.</summary>
        private readonly NexusInbox<NexusLogLine> _logInbox = new NexusInbox<NexusLogLine>();

        private void StartAgent()
        {
            // One DecisionLog for the cluster, the authority's: an agent's lines ride its report and
            // are filed there with server=<id>. The shared folder's file is held open by the
            // authority's writer, and the dashboard tails that file alone.
            _agentLogOutbox = new NexusLogOutbox();
            NexusLogOutbox outbox = _agentLogOutbox;
            _decisionLog = new DecisionLog((at, line) => outbox.Add(at, line));
            CrewIntruders.Sink = line => { if (_decisionLog != null) _decisionLog.Write(line); };
            try
            {
                _uplinkManager = new UplinkManager(
                    () => Factions.Wire.WireFeatures.Scanner
                          | Factions.Wire.WireFeatures.Map
                          | Factions.Wire.WireFeatures.Assets
                          | Factions.Wire.WireFeatures.NodeDetail
                          | Factions.Wire.WireFeatures.Events
                          | Factions.Wire.WireFeatures.StandingOrders
                          | Factions.Wire.WireFeatures.Campaigns
                          | Factions.Wire.WireFeatures.FactionDemand
                          | Factions.Wire.WireFeatures.GridSpeed
                          | Factions.Wire.WireFeatures.Signature
                          | Factions.Wire.WireFeatures.Market
                          | Factions.Wire.WireFeatures.Expand
                          | Factions.Wire.WireFeatures.Build
                          | Factions.Wire.WireFeatures.FleetDoctrine
                          | Factions.Wire.WireFeatures.Tactics
                          | Factions.Wire.WireFeatures.PlayerShelf
                          | Factions.Wire.WireFeatures.BlockPause
                          | Factions.Wire.WireFeatures.TrafficVoice
                          | Factions.Wire.WireFeatures.NodeSpawn
                          | Factions.Wire.WireFeatures.CrewOrders
                          | Factions.Wire.WireFeatures.CombatOutcome
                          | Factions.Wire.WireFeatures.DesignVersions
                          | Factions.Wire.WireFeatures.DockSupply
                          | Factions.Wire.WireFeatures.ShipAssets
                          | Factions.Wire.WireFeatures.Restoration
                          | Factions.Wire.WireFeatures.WingCommand,
                    // FeatureRock deliberately absent: nothing materialises rock in this server's
                    // sectors, so a client told to derive it would render rock that is not there.
                    line => { if (_decisionLog != null) _decisionLog.Write("uplink " + line); });
                _uplinkManager.ConfigureRelay(_nexusRelay, (byte)_nexusServerId,
                    (byte)_tuning.NexusAuthorityServerId, NexusRole.Agent);
                _gridSpeedManager = new GridSpeedManager(_tuning, _uplinkManager,
                    line => { if (_decisionLog != null) _decisionLog.Write(line); });
                _munitionSpeedManager = new MunitionSpeedManager(_tuning, _uplinkManager,
                    line => { if (_decisionLog != null) _decisionLog.Write(line); });
                _blockPauseNotices = new BlockPauseNotices(_uplinkManager);
                // The seat and the crew it orders are this server's entities, and the orders live here.
                _playerCrewOrderManager = new PlayerCrewOrderManager(_uplinkManager);
                _agentCaptures = new AgentCaptures(this, _uplinkManager, () => _tuning, () => _agentHands);
                // The antenna, the target and the locks are this server's; the order is the authority's.
                _agentWing = new AgentWing(_uplinkManager);
                _agentTerminalSurfaces = new AgentTerminalSurfaces(this, _uplinkManager);
                AgentSiege.CombatSink = SendAgentCombatNotice;
                _uplinkManager.Register();
                _authorityOnline = false;
                StartAgentChatCommands();
                StartAgentHands();
                Log.Info("Factions agent: relaying client uplinks to Nexus server " + _tuning.NexusAuthorityServerId
                         + " (this is server " + _nexusServerId + "); serving GridSpeed locally; relay "
                         + _nexusRelay.Status());
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Factions agent could not start; clients on this server have no uplink.");
                _uplinkManager = null;
            }
        }

        /// <summary>Factions chat commands run on the cluster's authority for players connected
        /// here or to an agent; null on a standalone server.</summary>
        private NexusChatCommands _chatCommands;

        /// <summary>Agent: this plugin's chat commands go to the authority to run.</summary>
        private void StartAgentChatCommands()
        {
            try
            {
                var chat = new NexusChatCommands(Torch, _nexusRelay.TrySend, (byte)_nexusServerId,
                                                 (byte)_tuning.NexusAuthorityServerId);
                if (!chat.AttachAgent())
                {
                    Log.Warn("Factions agent: Torch's command manager is missing; Factions chat commands refuse here.");
                    return;
                }
                chat.VerbAnswered = (line, answer) =>
                {
                    if (answer == null)
                    {
                        AppendOutbox("ERROR: the authority did not answer '" + line + "'");
                        return;
                    }
                    string flat = answer.Replace("\r", "").Replace("\n", " | ");
                    AppendOutbox("OK: " + line + " (server " + _tuning.NexusAuthorityServerId + ") => " + flat);
                    Log.Info("Bridge verb '" + line + "' ran on server " + _tuning.NexusAuthorityServerId + " => " + flat);
                };
                _chatCommands = chat;
                _uplinkManager.ChatReplyReceived = chat.OnReply;
            }
            catch (Exception ex) { Log.Warn(ex, "Factions agent: Factions chat commands refuse here."); }
        }

        private const int AgentPruneTicks = 60;
        private const int AgentLivenessTicks = 300;
        private const int AuthorityRemotePruneTicks = 1800;
        /// <summary>An agent reports its players' positions once a second, an empty set included,
        /// so a report going quiet means the agent is gone rather than empty.</summary>
        private const int AgentPositionTicks = 60;
        /// <summary>Five missed reports and the authority stops believing an agent's players.</summary>
        private static readonly TimeSpan RemotePositionMaxAge = TimeSpan.FromSeconds(5);

        /// <summary>A siege notice for a player on an agent is dropped unsent after this.</summary>
        private static readonly TimeSpan RemoteNoticeHold = TimeSpan.FromSeconds(30);
        private NexusNoticeOutbox _noticeOutbox = new NexusNoticeOutbox();
        private ChatManagerServer _agentChat;
        private List<RemotePlayer> _remoteNow = new List<RemotePlayer>();
        private DateTime _remoteNowUtc;

        /// <summary>Authority: the players on agents, minus any now connected here, rebuilt at most
        /// once a second -- reports arrive at that rate.</summary>
        private IList<RemotePlayer> RemotePlayersNow()
        {
            if (_remotePlayers.HomeCount == 0) { _remoteNow.Clear(); return _remoteNow; }
            DateTime now = DateTime.UtcNow;
            if (now - _remoteNowUtc < TimeSpan.FromSeconds(1)) return _remoteNow;
            _remoteNowUtc = now;
            var local = new HashSet<ulong>();
            foreach (CachedPlayer p in _cachedPlayers) local.Add(p.SteamId);
            _remoteNow = _remotePlayers.Snapshot(now, RemotePositionMaxAge, local);
            return _remoteNow;
        }

        /// <summary>Authority: where a player on an agent stands, by its last report, and which agent.</summary>
        private bool RemotePositionOf(ulong steamId, out VRageMath.Vector3D worldM, out byte home)
        {
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
            {
                if (remote[i].SteamId != steamId) continue;
                Vec3 m = remote[i].PositionM;
                worldM = new VRageMath.Vector3D(m.X, m.Y, m.Z);
                home = remote[i].HomeServerId;
                return true;
            }
            worldM = VRageMath.Vector3D.Zero;
            home = 0;
            return false;
        }

        /// <summary>Authority: node stock moves for players on agents, re-sent until applied.</summary>
        private NexusStockMoveOutbox _stockMoves = new NexusStockMoveOutbox();
        private static readonly TimeSpan StockMoveHold = TimeSpan.FromSeconds(30);

        /// <summary>Authority: the stock moves owed to <paramref name="home"/>. A withdrawal the agent
        /// never applied, because it restarted or stopped reporting, is returned to the node it was
        /// taken from.</summary>
        private List<NexusStockMove> PendingStockMoves(byte home, NexusAgentReport report)
        {
            var lapsed = new List<NexusStockMove>();
            List<NexusStockMove> pending = _stockMoves.Pending(home, report.Session, report.StockMovesThrough,
                                                               DateTime.UtcNow, StockMoveHold, lapsed);
            WorldModel world = _runtime != null ? _runtime.World : null;
            for (int i = 0; i < lapsed.Count; i++)
            {
                NexusStockMove m = lapsed[i];
                SpatialNode node = null;
                bool returned = !m.Deposit && m.Amount > 0 && world != null && m.Node != null
                                && world.Graph.Nodes.TryGetValue(m.Node, out node);
                if (returned) NodeTransfer.CreditNode(node, m.Key, m.Amount);
                Log.Warn("NEXUS_STOCK_MOVE_LAPSED server=" + home + " node=" + (m.Node ?? "-") + " key=" + m.Key
                         + " amount=" + m.Amount + " dir=" + (m.Deposit ? "deposit" : "withdraw")
                         + " returned=" + (returned ? m.Amount : 0));
            }
            return pending;
        }

        /// <summary>Authority: the identity an agent reported for a player it hosts; 0 when no
        /// current report names them.</summary>
        private long ReportedRemoteIdentity(ulong steamId)
        {
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
                if (remote[i].SteamId == steamId) return remote[i].IdentityId;
            return 0L;
        }

        /// <summary>Authority: whether any player here or on an agent is within range. What holds a
        /// siege open: a station on an agent is besieged by a player this server cannot see.</summary>
        /// <summary>Authority on a cluster: the fleets flown on agents (FAC-506 step 3).</summary>
        private readonly NexusFleetRegistry _fleetRegistry = new NexusFleetRegistry();

        /// <summary>Authority: where a fleet grid spawned at <paramref name="posKm"/> is flown -- here
        /// (0), on the agent whose sector holds it, or nowhere yet (-1) when that agent flies none.</summary>
        private int RouteFleetSpawn(Factions.Core.Vec3 posKm)
        {
            byte server;
            if (!NexusGateway.TryServerAt(new VRageMath.Vector3D(posKm.X * 1000.0, posKm.Y * 1000.0, posKm.Z * 1000.0), out server))
                return 0;
            if (server == 0 || server == (byte)_nexusServerId) return 0;
            return _fleetRegistry.CanFly(server, DateTime.UtcNow) ? server : -1;
        }

        /// <summary>Fleet observation's "is anyone near": local players, and players on an agent that
        /// flies fleets. A player on one that does not would only make a spawn wait forever.</summary>
        private bool AnyPlayerOrFlownWithinKm(Factions.Core.Vec3 posKm, double radiusKm)
        {
            if (AnyPlayerWithinKm(posKm, radiusKm)) return true;
            if (_nexusRole != NexusRole.Authority) return false;
            IList<RemotePlayer> remote = RemotePlayersNow();
            if (remote.Count == 0) return false;
            DateTime now = DateTime.UtcNow;
            double r2 = radiusKm * radiusKm;
            for (int i = 0; i < remote.Count; i++)
            {
                if (!_fleetRegistry.CanFly(remote[i].HomeServerId, now)) continue;
                Vec3 m = remote[i].PositionM;
                double dx = m.X / 1000.0 - posKm.X, dy = m.Y / 1000.0 - posKm.Y, dz = m.Z / 1000.0 - posKm.Z;
                if (dx * dx + dy * dy + dz * dz < r2) return true;
            }
            return false;
        }

        private int FlownRemoteCount()
        {
            if (_nexusRole != NexusRole.Authority) return 0;
            IList<RemotePlayer> remote = RemotePlayersNow();
            DateTime now = DateTime.UtcNow;
            int n = 0;
            for (int i = 0; i < remote.Count; i++) if (_fleetRegistry.CanFly(remote[i].HomeServerId, now)) n++;
            return n;
        }

        private bool AnyPlayerOrRemoteWithinKm(Factions.Core.Vec3 posKm, double radiusKm)
        {
            if (AnyPlayerWithinKm(posKm, radiusKm)) return true;
            IList<RemotePlayer> remote = RemotePlayersNow();
            double r2 = radiusKm * radiusKm;
            for (int i = 0; i < remote.Count; i++)
            {
                Vec3 m = remote[i].PositionM;
                double dx = m.X / 1000.0 - posKm.X, dy = m.Y / 1000.0 - posKm.Y, dz = m.Z / 1000.0 - posKm.Z;
                if (dx * dx + dy * dy + dz * dz < r2) return true;
            }
            return false;
        }

        /// <summary>Authority: queues a siege notice for the agent the player is on. A player on no
        /// reporting agent is told nothing, as an offline one is.</summary>
        private void QueueRemoteNotice(long identityId, string text, string font, int hudMs, bool chat)
        {
            QueueRemoteNotice(identityId, text, font, hudMs, chat, false);
        }

        private void QueueRemoteNotice(long identityId, string text, string font, int hudMs, bool chat, bool offer)
        {
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
            {
                if (remote[i].IdentityId != identityId) continue;
                _noticeOutbox.Add(remote[i].HomeServerId,
                    new NexusNotice { Identity = identityId, Text = text, Font = font, HudMs = hudMs, Chat = chat, Offer = offer },
                    DateTime.UtcNow);
                return;
            }
        }

        /// <summary>Authority: a kill confirmation to a player on an agent, by the identity that agent
        /// reported: the chat line and the HUD combat notice. Their Steam id, or 0 when no agent reports
        /// them.</summary>
        private ulong QueueRemoteKill(long identityId, CombatTone tone, string text)
        {
            if (identityId == 0 || string.IsNullOrEmpty(text)) return 0UL;
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
            {
                if (remote[i].IdentityId != identityId) continue;
                _noticeOutbox.Add(remote[i].HomeServerId,
                    new NexusNotice { Identity = identityId, Text = text, Font = "Blue", Chat = true, Combat = (int)tone + 1 },
                    DateTime.UtcNow);
                return remote[i].SteamId;
            }
            return 0UL;
        }

        /// <summary>Authority: a chat notice to a player on an agent, by Steam id; the notice names them
        /// by the identity that agent reported. False when they are on no reporting agent.</summary>
        private bool QueueRemoteChat(ulong steamId, string text, string font, string author, bool offer)
        {
            if (steamId == 0UL || string.IsNullOrEmpty(text)) return false;
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
            {
                if (remote[i].SteamId != steamId || remote[i].IdentityId == 0) continue;
                _noticeOutbox.Add(remote[i].HomeServerId,
                    new NexusNotice { Identity = remote[i].IdentityId, Text = text, Font = font, Chat = true, Offer = offer, Author = author },
                    DateTime.UtcNow);
                return true;
            }
            return false;
        }

        /// <summary>Authority: this server's identity for one an agent reported, by Steam id, which
        /// every shard shares; the reported one when the agent's player is not found.</summary>
        private long LocalIdentityOfReported(byte home, long reported)
        {
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
            {
                if (remote[i].HomeServerId != home || remote[i].IdentityId != reported || remote[i].SteamId == 0UL) continue;
                long local = MySession.Static != null ? MySession.Static.Players.TryGetIdentityId(remote[i].SteamId) : 0;
                return local != 0 ? local : reported;
            }
            return reported;
        }

        /// <summary>Authority: a trespass hail spoken to a pilot on an agent, from the agent's radio.</summary>
        private bool RelayHail(ulong steamId, Factions.Wire.WireTrafficEvent ev, string ownerTag, byte speaker,
                               SpatialNode node, string nodeName)
        {
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
            {
                if (remote[i].SteamId != steamId) continue;
                _trafficRelay.Add(NexusTrafficCall.HailTo(steamId, (int)ev, ownerTag, speaker, node.Id, node.Position,
                    nodeName, DateTime.UtcNow), DateTime.UtcNow, remote[i].HomeServerId);
                return true;
            }
            return false;
        }

        /// <summary>Authority: per agent, its seated players' grids that are docked by connector.</summary>
        private readonly Dictionary<byte, HashSet<long>> _agentDocked = new Dictionary<byte, HashSet<long>>();

        private bool IsRemotePlayer(long identityId)
        {
            if (identityId == 0) return false;
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
                if (remote[i].IdentityId == identityId) return true;
            return false;
        }

        /// <summary>Agent: a local player's yes or no to an offer the authority made them.</summary>
        private void OnAgentChat(Torch.API.Managers.TorchChatMessage msg, ref bool consumed)
        {
            try
            {
                if (_agentHands == null || !msg.AuthorSteamId.HasValue) return;
                bool isYes;
                string nodeId;
                if (!OfferReply.TryParse(msg.Message, out isYes, out nodeId)) return;
                long identityId = MySession.Static.Players.TryGetIdentityId(msg.AuthorSteamId.Value);
                if (_agentHands.TryAnswer(identityId, isYes, nodeId)) consumed = true;
            }
            catch (Exception ex) { Log.Warn(ex, "Agent offer answer threw."); }
        }

        /// <summary>Agent: sends this server's players' positions to the authority. Read from the
        /// session rather than the position cache, which is filled only on a simulating server.</summary>
        private readonly List<AntennaReading> _relayAntennas = new List<AntennaReading>();

        private void SendPlayerPositions()
        {
            if (MySession.Static == null) return;
            var players = new List<Factions.Wire.RelayPlayerPosition>();
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot) continue;
                var ch = player.Character;
                if (ch == null) continue;
                var moving = player.Controller?.ControlledEntity?.Entity?.GetTopMostParent()?.Physics ?? ch.Physics;
                VRageMath.Vector3D pos = ch.PositionComp.GetPosition();
                VRageMath.Vector3 vel = moving != null ? moving.LinearVelocity : VRageMath.Vector3.Zero;
                var row = new Factions.Wire.RelayPlayerPosition
                {
                    SteamId = player.Id.SteamId,
                    IdentityId = player.Identity != null ? player.Identity.IdentityId : 0,
                    X = pos.X, Y = pos.Y, Z = pos.Z, Vx = vel.X, Vy = vel.Y, Vz = vel.Z,
                };
                // The seat is what the authority's scanner runs from: the grid lives here, so its
                // position and antennas are read here.
                var seat = player.Controller != null
                    ? player.Controller.ControlledEntity as Sandbox.Game.Entities.MyShipController : null;
                if (seat != null && seat.CubeGrid != null && row.IdentityId != 0)
                {
                    VRageMath.Vector3D g = seat.CubeGrid.PositionComp.GetPosition();
                    ShipAntennas.Read(seat.CubeGrid, row.IdentityId, _relayAntennas);
                    row.SeatGridId = seat.CubeGrid.EntityId;
                    row.GridX = g.X; row.GridY = g.Y; row.GridZ = g.Z;
                    row.SensorKm = (float)ShipSensor.StrengthKm(_relayAntennas, _tuning);
                    row.AntennaCount = _relayAntennas.Count;
                }
                players.Add(row);
            }
            byte[] frame = Factions.Wire.NexusRelayWire.Write(Factions.Wire.RelayKind.PlayerPositions,
                (byte)_nexusServerId, 0UL, Factions.Wire.NexusRelayWire.WritePlayerPositions(players));
            _nexusRelay.TrySend((byte)_tuning.NexusAuthorityServerId, frame);
        }

        /// <summary>Agent: the materialisers for this server's sectors. Reads the planet surface
        /// policy and the world features from the configured data directory (read-only; an agent
        /// never writes there) or this instance's copies, as the authority does.</summary>
        private void StartAgentHands()
        {
            try
            {
                string dataDir = InstanceDir;
                if (DataDirectory.IsRedirected(_tuning.DataDirectory))
                {
                    string resolved = DataDirectory.Resolve(_tuning.DataDirectory, InstanceDir);
                    if (Directory.Exists(resolved)) dataDir = resolved;
                }
                _agentDataDir = dataDir;
                string policyStatus;
                SurfaceNodePolicy policy = LoadSurfaceNodePolicy(out policyStatus, dataDir);
                var planets = new SePlanetCatalogue(policy);
                bool usedFallback;
                string featurePath = DataDirectory.ResolveInput(WorldFeatureStore.FileName, dataDir, InstanceDir,
                                                                File.Exists, out usedFallback);
                string featureStatus;
                List<Factions.Core.WorldFeature> features = WorldFeatureStore.Load(featurePath, out featureStatus);
                // Read here, never written: captures are the authority's, and SaveToDisk is not called.
                CustomPrefabStore customPrefabs = null;
                try { customPrefabs = CustomPrefabStore.LoadAll(Path.Combine(dataDir, "custom_prefabs")); }
                catch (Exception ex) { Log.Warn(ex, "Factions agent: captured station designs unreadable; those stations will not stand here."); }
                VanillaFleetSpawner fleetSpawner = null;
                try
                {
                    bool prefabsFallback;
                    string prefabsPath = DataDirectory.ResolveInput("fleet_prefabs.json", dataDir, InstanceDir,
                                                                    File.Exists, out prefabsFallback);
                    fleetSpawner = new VanillaFleetSpawner(prefabsPath, null);
                    fleetSpawner.PlayerWithinKm = AnyPlayerWithinKm;
                    fleetSpawner.SetCustomPrefabStore(customPrefabs);
                    fleetSpawner.SetTuning(_tuning);
                    fleetSpawner.SetPlanets(planets);
                    VanillaFleetSpawner.DebugBroadcast = StationMaterializer.DebugBroadcast;
                    SimGridMarks.Attach();
                    SectorHandover.Attach((byte)_nexusServerId);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Factions agent: fleet spawner unavailable; this server flies no Factions fleets.");
                    fleetSpawner = null;
                }
                _agentHands = new AgentHands(_tuning, features, planets, customPrefabs, fleetSpawner);
                _agentHands.LoadReader = ReadAgentLoad;
                _agentHands.ContactsReader = ReadAgentContacts;
                _agentHands.OwnedGridsReader = ReadAgentOwnedGrids;
                FactionWalletTransferPatch.AgentSink = (from, to, amount) =>
                {
                    if (_agentHands != null) _agentHands.RecordTransfer(from, to, amount);
                };
                StartAgentStores();
                if (fleetSpawner != null && _agentWing != null)
                {
                    // A station's call reads its hits here: on this server's Factions stations, and on
                    // any grid whose call stands.
                    AgentWing wing = _agentWing;
                    AgentHands hands = _agentHands;
                    wing.ReadStationGrids = into => hands.Stations.MaterializedStationGrids(into);
                    wing.NpcOwnerOf = nodeId =>
                    {
                        SpatialGraph graph = hands.Graph;
                        SpatialNode node;
                        if (graph == null || !graph.Nodes.TryGetValue(nodeId, out node) || node == null) return null;
                        FactionState owner = hands.FindFaction(node.OwnerFactionTag);
                        return owner != null && !owner.IsPlayerFaction ? owner.Tag : null;
                    };
                    wing.Decision = line => { if (_decisionLog != null) _decisionLog.Write(line); };
                    wing.EnsureHits = fleetSpawner.EnsureDamageHandler;
                    fleetSpawner.StationAttackWatched = wing.WatchesStation;
                    fleetSpawner.StationAttackerSink = wing.RecordHit;
                }
                if (fleetSpawner != null) CrewPresence.AgentPrefabOf = fleetSpawner.GridPrefabId;
                CrewPresence.AgentCatalogue = LoadAgentPrefabCatalogue(dataDir);
                StartAgentShipAssets();
                StartAgentRestoration();
                // The record mirror stands every snapshot station here, so vanilla's economy tick walks
                // as many as the authority's does; it is sliced the same way, from TickAgent.
                EconomyTickSlicer.TickedOnAgent = true;
                // The pieces a hull sheds and the turrets it carries are marked, so SE's trash keeps
                // off them; nothing else here would close them. No boot pass: the leftover sweep
                // closes the save's grids on the first frame, and the first pass waits an interval.
                if (_agentHands.Fleets != null)
                    _gridReaper = new GridReaper(_tuning, _decisionLog,
                        id => _agentHands != null && _agentHands.Fleets != null && _agentHands.Fleets.Holds(id),
                        id => _agentHands != null && _agentHands.Stations != null
                              && _agentHands.Stations.NodeIdForStationGrid(id) != null,
                        AnyPlayerWithinKm);
                StartAgentDebris();
                StartAgentRespawn(features, planets);
                StartAgentTraffic(planets);
                _uplinkManager.SnapshotReceived = OnNodeSnapshot;
                _uplinkManager.StationReplyReceived = OnStationReply;
                _agentHands.TellObservers = TellPlayersNear;
                _agentChat = Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>();
                if (_agentChat != null) _agentChat.MessageProcessing += OnAgentChat;
                SimulationGate.HandsPending = true;
                Log.Info("Factions agent: materialising stations and rock for this server's sectors from the "
                         + "authority's node snapshot; inputs from " + dataDir + "; " + featureStatus);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Factions agent could not start materialising; this server's sectors show no Factions rock.");
                SimulationGate.HandsPending = false;
                _agentHands = null;
                _gridReaper = null;
                EconomyTickSlicer.TickedOnAgent = false;
                ClearAgentStoreHooks();
            }
        }

        /// <summary>Agent: the station stores show the authority's shelves, and what players do at
        /// them goes back to be booked.</summary>
        private void StartAgentStores()
        {
            _agentHands.Store = new StationStoreManager(null, null, null, null, _tuning, new HashSet<string>(), null);
            EconomyTickSlicer.AgentManages = tag => _agentHands != null && _agentHands.IsRegistryFaction(tag);
            StoreShipSaleMarks.ShelfSoldRemote = (seller, prefab, buyer) =>
            {
                if (_agentHands == null) return;
                _agentHands.RecordStoreEvent(new NexusStoreEvent
                {
                    Kind = NexusStoreEventKind.Ship, Seller = seller, Prefab = prefab, Buyer = buyer, Steam = SteamOf(buyer),
                });
            };
            StoreTransactionHelpers.AgentStores = true;
            // Refuel and rearm write into a ship docked here, out of the shelf the authority priced;
            // what landed goes back to it as a Supply event.
            _agentHands.Store.RemoteSupplySettled = (node, buyer, service, units, credits) =>
            {
                if (_agentHands == null) return;
                _agentHands.RecordStoreEvent(new NexusStoreEvent
                {
                    Kind = NexusStoreEventKind.Supply, Node = node.Id, Seller = service, Credits = credits,
                    Buyer = buyer, Steam = SteamOf(buyer), Parts = new Dictionary<string, long>(units, StringComparer.Ordinal),
                });
            };
            _agentDockSupply = new DockSupplyManager(AgentTerminalWorld, _uplinkManager, _agentHands.Store, _tuning);
        }

        private DockSupplyManager _agentDockSupply;
        private AgentWing _agentWing;
        private PlayerShipAssetsManager _agentShipAssets;

        /// <summary>Agent: the Services Terminal's ship rows, withdraw and send, over the snapshot's
        /// hull stock. Grid storage and the hull are this server's; what moved goes to the authority
        /// as a Hull event.</summary>
        private void StartAgentShipAssets()
        {
            _agentShipAssets = new PlayerShipAssetsManager(null, AgentTerminalWorld,
                () => CrewPresence.AgentCatalogue,
                () => _agentHands != null ? _agentHands.CustomPrefabs : null,
                _uplinkManager, null);
            _agentShipAssets.RemoteHull = (nodeId, prefab, withdrawn, cargo, hold) =>
            {
                if (_agentHands == null) return;
                _agentHands.RecordStoreEvent(new NexusStoreEvent
                {
                    Kind = NexusStoreEventKind.Hull, Node = nodeId, Prefab = prefab, Buy = withdrawn, Hold = hold,
                    Parts = cargo != null && cargo.Count > 0 ? new Dictionary<string, long>(cargo, StringComparer.Ordinal) : null,
                });
            };
            // The node's stock is the authority's: it holds the hull before the clone is staged here.
            _agentShipAssets.AskHold = ask =>
                _uplinkManager != null && _uplinkManager.ForwardFrame(Factions.Wire.RelayKind.HullAsk, 0UL, ask.Encode(), "hull-hold");
            _uplinkManager.HullAnswerReceived = (steamId, payload) =>
            {
                NexusHullAnswer answer;
                if (!NexusHullAnswer.TryDecode(payload, out answer))
                {
                    Log.Warn("NEXUS_HULL_ANSWER_UNREADABLE bytes=" + (payload == null ? 0 : payload.Length));
                    return;
                }
                PlayerShipAssetsManager assets = _agentShipAssets;
                if (assets != null) assets.OnHoldAnswer(answer);
            };
        }

        private RestorationService _agentRestoration;
        private RestorationReferences _agentRestorationRefs;
        private IItemPriceCatalogue _agentPrices;

        /// <summary>Agent: the Repair tab's Restore at a Factions station here. The ship, the terminal
        /// and the welding are this server's; the references are the data directory's designs and
        /// captures, read here and never written. Node components settle as Supply events, and a
        /// design registered here goes to the authority to be written, its fee to the node's owner.</summary>
        private void StartAgentRestoration()
        {
            _agentRestorationRefs = new RestorationReferences(
                () => _agentHands != null ? _agentHands.CustomPrefabs : null,
                (long eid, out string via) =>
                {
                    Func<long, string> prefabOf = CrewPresence.AgentPrefabOf;
                    string prefab = prefabOf != null ? prefabOf(eid) : null;
                    via = prefab != null ? "spawn" : null;
                    return prefab;
                },
                _tuning);
            try { _agentPrices = SeItemPriceCatalogue.Load(StorageDir, _tuning, null); }
            catch (Exception ex) { Log.Warn(ex, "Factions agent: item prices unavailable; Restore here sells no components from the bank."); }
            _agentRestoration = new RestorationService(AgentTerminalWorld, () => _agentRestorationRefs, AgentItemValue,
                                                       AgentRegisterDesign, _uplinkManager, _agentHands.Store);
        }

        /// <summary>As <see cref="WorldModel.ItemUnitValue"/> over the same catalogue the authority
        /// derives, so the bank charges the same here as there; 0 with no catalogue.</summary>
        private long AgentItemValue(string key)
        {
            IItemPriceCatalogue prices = _agentPrices;
            if (prices == null || string.IsNullOrEmpty(key)) return 0;
            long v = prices.BasePrice(key);
            return v > 0 ? v : 1;
        }

        private bool AgentRegisterDesign(RestorationLiveGroup live, long payer, ulong steam, SpatialNode node, long fee,
                                         out string fingerprint)
        {
            VRage.Game.MyObjectBuilder_CubeGrid[] grids = RestorationReferences.DesignOf(live, out fingerprint);
            Sandbox.Game.Entities.MyCubeGrid root = live.Grids[0];
            CustomPrefabStore store = _agentHands != null ? _agentHands.CustomPrefabs : null;
            if (store != null) store.KeepDesign(root.EntityId, fingerprint, grids, root.DisplayName, payer, DateTime.UtcNow);
            bool sent = _agentCaptures != null
                && _agentCaptures.RegisterDesign(steam, payer, node, fee, root.EntityId, fingerprint, root.DisplayName, grids);
            Log.Info("DESIGN_REGISTERED grid=" + root.EntityId + " blocks=" + live.BlockCount + " grids=" + live.Grids.Count
                     + " fingerprint=" + fingerprint + " by=" + payer + " fee=" + fee + " relayed=" + (sent ? "sent" : "failed"));
            return sent;
        }

        private DebrisAbsorbManager _agentDebris;

        /// <summary>Agent: the hourly debris sweep, closing wreckage here and sending each node's
        /// census to the authority, whose stock it is.</summary>
        private void StartAgentDebris()
        {
            _agentDebris = new DebrisAbsorbManager(
                () => _agentHands != null ? _agentHands.Graph : null, _tuning, _decisionLog,
                id => _agentHands != null && _agentHands.Fleets != null && _agentHands.Fleets.Holds(id),
                null, PlayersWithinKm);
            _agentDebris.RemoteCredit = (node, grids, blocks, components, cargo) =>
            {
                if (_agentHands == null) return;
                _agentHands.RecordStoreEvent(new NexusStoreEvent
                {
                    Kind = NexusStoreEventKind.Debris, Node = node, Grids = grids, Blocks = blocks, Parts = components, Cargo = cargo,
                });
            };
            _agentDebris.AttachChat(Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>());
        }

        private RespawnPlacement _agentRespawn;

        /// <summary>Agent: respawn-ship placement and the welcome waypoint, against the snapshot's
        /// founded stations. The players respawn here, not on the authority.</summary>
        private void StartAgentRespawn(List<Factions.Core.WorldFeature> features, SePlanetCatalogue planets)
        {
            var placement = new WorldFeaturePlacement(features, planets);
            _agentRespawn = new RespawnPlacement(
                () => _agentHands != null ? _agentHands.Graph : null,
                () => _tuning,
                () => placement,
                tag => _agentHands != null ? _agentHands.PersonalityOf(tag) : null,
                _decisionLog);
            RespawnPlacement.Instance = _agentRespawn;
            RespawnPlacement.OnAgent = true;
            _agentRespawn.Attach();

            byte here = (byte)_nexusServerId;
            _nodeSpawnManager = new NodeSpawnManager(
                () => _agentHands != null ? _agentHands.Graph : null,
                AgentArrivalFrame,
                node => NodeInThisSector(node, here),
                _uplinkManager, _decisionLog);
            NodeSpawnManager.Instance = _nodeSpawnManager;
            NodeSpawnManager.OnAgent = true;
        }

        private bool AgentArrivalFrame(SpatialNode node, out VRageMath.Vector3D stationPos, out VRageMath.Vector3D stationUp,
                                       out float clearanceM, out string error)
        {
            StationMaterializer stations = _agentHands != null ? _agentHands.Stations : null;
            if (stations == null)
            {
                stationPos = VRageMath.Vector3D.Zero; stationUp = VRageMath.Vector3D.Up; clearanceM = 0f;
                error = "no station materialiser";
                return false;
            }
            return stations.TryGetArrivalFrame(node, out stationPos, out stationUp, out clearanceM, out error);
        }

        /// <summary>Whether Nexus places <paramref name="node"/> in server <paramref name="serverId"/>'s
        /// sectors. False when no sector holds it, or Nexus cannot be read.</summary>
        private static bool NodeInThisSector(SpatialNode node, byte serverId)
        {
            byte at;
            return node != null && NexusGateway.TryServerAt(
                       new VRageMath.Vector3D(node.Position.X, node.Position.Y, node.Position.Z) * 1000.0, out at)
                   && at == serverId;
        }

        /// <summary>Whether the node stands in a sector Nexus gives to another server. False when the
        /// sector lookup cannot say, so an unbound lookup offers every node, as before.</summary>
        private static bool NodeInOtherSector(SpatialNode node, byte serverId)
        {
            byte at;
            return node != null && NexusGateway.TryServerAt(
                       new VRageMath.Vector3D(node.Position.X, node.Position.Y, node.Position.Z) * 1000.0, out at)
                   && at != 0 && at != serverId;
        }

        private void ClearAgentStoreHooks()
        {
            RespawnPlacement.OnAgent = false;
            NodeSpawnManager.OnAgent = false;
            if (NodeSpawnManager.Instance == _nodeSpawnManager) NodeSpawnManager.Instance = null;
            _nodeSpawnManager = null;
            if (_agentRespawn != null)
            {
                try { _agentRespawn.Detach(); }
                catch (Exception ex) { Log.Warn(ex, "Respawn placement detach failed."); }
                if (RespawnPlacement.Instance == _agentRespawn) RespawnPlacement.Instance = null;
                _agentRespawn = null;
            }
            _agentDebris?.DetachChat();
            _agentDebris = null;
            _agentTraffic = null;
            StoreTransactionHelpers.AgentStores = false;
            EconomyTickSlicer.AgentManages = null;
            StoreShipSaleMarks.ShelfSoldRemote = null;
        }

        private static ulong SteamOf(long identityId)
        {
            if (identityId == 0 || MySession.Static == null || MySession.Static.Players == null) return 0UL;
            return MySession.Static.Players.TryGetSteamId(identityId);
        }

        /// <summary>Agent: queues a store or Services Terminal event at a Factions station here; false
        /// when the station is not one.</summary>
        private bool TryRecordAgentStoreEvent(long stationId, NexusStoreEvent e)
        {
            if (_agentHands == null) return false;
            e.Node = _agentHands.NodeIdForStation(stationId);
            if (e.Node == null) return false;
            e.Steam = SteamOf(e.Buyer);
            _agentHands.RecordStoreEvent(e);
            return true;
        }

        /// <summary>Agent: the authority's prefab catalogue sidecar, for crew sizes; null when the data
        /// directory does not have it or it does not parse. Read once: the authority rewrites it only
        /// when a capture adds a design.</summary>
        private static SePrefabCatalogue LoadAgentPrefabCatalogue(string dataDir)
        {
            string path = Path.Combine(dataDir, "prefab_catalogue.json");
            try
            {
                if (!File.Exists(path))
                {
                    Log.Info("Factions agent: no prefab_catalogue.json in " + dataDir + "; crew sizes come from seats.");
                    return null;
                }
                return SePrefabCatalogue.FromJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Factions agent: prefab_catalogue.json unreadable; crew sizes come from seats.");
                return null;
            }
        }

        /// <summary>Agent: a node snapshot from the authority, on the game thread.</summary>
        private void OnNodeSnapshot(byte[] payload)
        {
            if (_agentHands == null) return;
            NexusNodeSnapshot snapshot;
            if (!NexusNodeSnapshot.TryDecode(payload, out snapshot))
            {
                Log.Warn("NEXUS_SNAPSHOT_UNREADABLE bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            bool first = !_agentHands.HasSnapshot;
            if (_agentHands.ApplySnapshot(snapshot, DateTime.UtcNow) && first)
                Log.Info("NEXUS_SNAPSHOT state=first nodes=" + _agentHands.NodeCount + " bytes=" + payload.Length);
            // From the first snapshot on, the station claims exist, so the cell generator can be kept
            // off Factions stations. Before it, vanilla places them as it always has here.
            if (_agentHands.ClaimsSeeded) SimulationGate.Hands = true;
            ReloadAgentCapturesIfMoved(_agentHands.Captures);
        }

        /// <summary>Agent: reads the captured designs again, off the game thread, when the authority's
        /// capture generation has moved since the last load. The first snapshot only sets the mark:
        /// the store was read at boot.</summary>
        private void ReloadAgentCapturesIfMoved(int generation)
        {
            if (!_agentCapturesLoaded.HasValue) { _agentCapturesLoaded = generation; return; }
            if (_agentCapturesLoaded.Value == generation || _agentCaptureLoad != null) return;
            if (_agentDataDir == null) return;
            string dir = Path.Combine(_agentDataDir, "custom_prefabs");
            _agentCaptureLoading = generation;
            _agentCaptureLoad = System.Threading.Tasks.Task.Run(() => CustomPrefabStore.LoadAll(dir));
        }

        /// <summary>Agent: swaps in a finished reload on the game thread; a failed one keeps the old
        /// store and is retried at the next snapshot.</summary>
        private void SwapAgentCaptures()
        {
            System.Threading.Tasks.Task<CustomPrefabStore> load = _agentCaptureLoad;
            if (load == null || !load.IsCompleted) return;
            _agentCaptureLoad = null;
            if (load.IsFaulted || load.Result == null)
            {
                Log.Warn(load.Exception, "NEXUS_CAPTURES_RELOAD_FAILED generation=" + _agentCaptureLoading);
                return;
            }
            _agentHands.ReplaceCustomPrefabs(load.Result);
            // The authority rewrites the catalogue sidecar when a capture adds a design: a Send to
            // node matches a hull against it.
            if (_agentDataDir != null) CrewPresence.AgentCatalogue = LoadAgentPrefabCatalogue(_agentDataDir) ?? CrewPresence.AgentCatalogue;
            _agentCapturesLoaded = _agentCaptureLoading;
            Log.Info("NEXUS_CAPTURES_RELOADED generation=" + _agentCaptureLoading);
        }

        /// <summary>Agent: sends what the materialisers placed until the snapshot shows it applied.</summary>
        private void SendAgentReport()
        {
            if (_agentHands == null) return;
            NexusAgentReport report = _agentHands.BuildReport();
            report.Names = PlayerNamesDue(DateTime.UtcNow);
            report.Log = _agentLogOutbox.Pending();
            report.Docked = DockedSeats();
            report.Muted = _agentWing != null ? _agentWing.MutedNodes() : null;
            byte[] frame = Factions.Wire.NexusRelayWire.Write(Factions.Wire.RelayKind.AgentReport,
                (byte)_nexusServerId, 0UL, report.Encode());
            _nexusRelay.TrySend((byte)_tuning.NexusAuthorityServerId, frame);
        }

        /// <summary>Agent: the grids this server's seated players sit in that are locked to another grid
        /// by a connector; null for none.</summary>
        private List<long> DockedSeats()
        {
            if (MySession.Static == null) return null;
            List<long> docked = null;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Controller == null) continue;
                var seat = player.Controller.ControlledEntity as Sandbox.Game.Entities.MyShipController;
                MyCubeGrid grid = seat != null ? seat.CubeGrid : null;
                if (grid == null || grid.MarkedForClose || !IsConnectorDocked(grid)) continue;
                if (docked == null) docked = new List<long>();
                docked.Add(grid.EntityId);
            }
            return docked;
        }

        /// <summary>Agent: names go with the first report after the connected set changes, and once a
        /// minute besides, so an authority restart recovers them without a reconnect.</summary>
        private static readonly TimeSpan PlayerNamesEvery = TimeSpan.FromSeconds(60);
        private readonly HashSet<ulong> _namesSent = new HashSet<ulong>();
        private DateTime _namesSentUtc;

        private List<NexusPlayerName> PlayerNamesDue(DateTime now)
        {
            if (MySession.Static == null) return null;
            var names = new List<NexusPlayerName>();
            bool changed = false;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                names.Add(new NexusPlayerName { Steam = player.Id.SteamId, Name = player.Identity.DisplayName });
                if (!_namesSent.Contains(player.Id.SteamId)) changed = true;
            }
            if (names.Count != _namesSent.Count) changed = true;
            if (!changed && now - _namesSentUtc < PlayerNamesEvery) return null;
            _namesSent.Clear();
            for (int i = 0; i < names.Count; i++) _namesSent.Add(names[i].Steam);
            _namesSentUtc = now;
            return names.Count > 0 ? names : null;
        }

        /// <summary>Authority: the display names agents reported for the players they host.</summary>
        private readonly Dictionary<ulong, string> _remoteNames = new Dictionary<ulong, string>();

        /// <summary>Authority: one agent's report, on the game thread.</summary>
        private void OnAgentReport(byte homeServerId, byte[] payload)
        {
            NexusAgentReport report;
            if (!NexusAgentReport.TryDecode(payload, out report))
            {
                Log.Warn("NEXUS_REPORT_UNREADABLE home=" + homeServerId + " bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            if (report.Load != null) _agentLoads[homeServerId] = new KeyValuePair<NexusAgentLoad, DateTime>(report.Load, DateTime.UtcNow);
            else _agentLoads.Remove(homeServerId);
            if (report.Contacts != null)
                _agentContacts[homeServerId] = new KeyValuePair<List<NexusPlayerContact>, DateTime>(report.Contacts, DateTime.UtcNow);
            _agentOwnedGrids.Update(homeServerId, report.OwnedGrids, DateTime.UtcNow);
            if (_runtime != null) _runtime.SetAgentMutedStations(homeServerId, report.Muted, DateTime.UtcNow);
            if (report.Names != null)
                for (int i = 0; i < report.Names.Count; i++)
                    if (report.Names[i] != null && report.Names[i].Steam != 0 && report.Names[i].Name != null)
                        _remoteNames[report.Names[i].Steam] = report.Names[i].Name;
            WorldModel world = _runtime != null ? _runtime.World : null;
            if (world == null) return;
            int fields = report.ApplyTo(world.Graph);
            if (fields > 0)
                Log.Info("NEXUS_FIELDS_PLACED home=" + homeServerId + " nodes=" + fields);

            NexusStationTally tally;
            NexusStationReply reply = _runtime.ApplyAgentStations(homeServerId, report, out tally);
            if (tally.Stood > 0 || tally.Closed > 0 || tally.Stranded > 0)
                Log.Info("NEXUS_STATIONS home=" + homeServerId + " stood=" + tally.Stood + " closed=" + tally.Closed
                         + " stranded=" + tally.Stranded + " ammo_drawn=" + tally.AmmoDrawn + " ammo_spent=" + tally.AmmoSpent
                         + " ammo_back=" + tally.AmmoBack + " loot_drawn=" + tally.LootDrawn + " loot_back=" + tally.LootBack);
            List<NexusSiegeEvent> siege = _runtime.ApplyAgentSiege(homeServerId, report, reply);
            for (int i = 0; i < siege.Count; i++)
            {
                if (siege[i].Answer == NexusAnswer.None) continue;
                bool yes = siege[i].Answer == NexusAnswer.Yes;
                if (_offerReplyRouter != null)
                    _offerReplyRouter.Route(siege[i].Attacker, LocalIdentityOfReported(homeServerId, siege[i].Attacker),
                                            yes, siege[i].Node);
            }
            _agentDocked[homeServerId] = report.Docked != null ? new HashSet<long>(report.Docked) : new HashSet<long>();
            BookAgentTransfers(homeServerId, report, reply);
            BookAgentStoreEvents(homeServerId, report, reply);
            FileAgentLog(homeServerId, report, reply);
            FillShelves(homeServerId, report, reply);
            reply.Notices = _noticeOutbox.Pending(homeServerId, report.Session, report.NoticesThrough,
                                                  DateTime.UtcNow, RemoteNoticeHold);
            reply.StockMoves = PendingStockMoves(homeServerId, report);
            DateTime fleetNow = DateTime.UtcNow;
            List<string> answered = _fleetRegistry.Apply(homeServerId, report, fleetNow);
            foreach (long eid in _fleetRegistry.TakeRehomed())
            {
                Log.Info("NEXUS_FLEET_REHOMED eid=" + eid + " home=" + homeServerId);
                _runtime?.ResumeHandedOver(eid, homeServerId);
            }
            if (answered.Count > 0)
                Log.Info("NEXUS_FLEET_SPAWNS home=" + homeServerId + " answered=" + answered.Count
                         + " live=" + _fleetRegistry.LiveCount + " spawning=" + _fleetRegistry.SpawningCount);
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held; List<NexusFleetBands> bands;
            _fleetRegistry.FillReply(homeServerId, fleetNow, out orders, out asks, out held, out bands);
            reply.Orders = orders; reply.Asks = asks; reply.Held = held; reply.Bands = bands;
            if (HasRemotePlayers(homeServerId))
            {
                reply.Traffic = _trafficRelay.Take(homeServerId, fleetNow);
                reply.Combat = _combatRelay.Take(homeServerId, fleetNow);
            }
            else
            {
                _trafficRelay.Skip(homeServerId);
                _combatRelay.Skip(homeServerId);
            }
            // Sent every time, empty or not: an agent takes the zone list whole, so a siege that
            // ended has to be heard as a list without it.
            if (_nexusRelay != null)
                _nexusRelay.TrySend(homeServerId, Factions.Wire.NexusRelayWire.Write(Factions.Wire.RelayKind.StationReply,
                    (byte)_nexusServerId, 0UL, reply.Encode()));
        }

        /// <summary>Agent: whispers a station notice to the players within observation range of a
        /// node, as the authority's runtime does for its own stations.</summary>
        private void TellPlayersNear(Factions.Core.SpatialNode node, string message)
        {
            if (node == null || message == null) return;
            var chat = Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>();
            if (chat == null) return;
            foreach (ulong steamId in PlayersWithinKm(node.Position, _tuning.ObservationRadiusKm))
            {
                try { chat.SendMessageAsOther("Factions", message, VRageMath.Color.CornflowerBlue, steamId); }
                catch (Exception ex) { Log.Warn(ex, "Station notice to " + steamId + " failed."); }
            }
        }

        /// <summary>Agent: the authority's answer to this server's stations, on the game thread.</summary>
        private void OnStationReply(byte[] payload)
        {
            if (_agentHands == null) return;
            NexusStationReply reply;
            if (!NexusStationReply.TryDecode(payload, out reply))
            {
                Log.Warn("NEXUS_STATION_REPLY_UNREADABLE bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            _agentHands.ApplyStationReply(reply);
            ApplyAgentStockMoves(reply.StockMoves);
            _agentLogOutbox.Ack(reply.LogThrough);
            PlayAgentTraffic(reply.Traffic);
            TellAgentCombat(reply.Combat);
        }

        /// <summary>Authority: registers a capture an agent made, and answers it.</summary>
        private NexusWingReadings _wingReadings;
        private DateTime _wingReadingsPrunedUtc;

        /// <summary>Authority: a wing command given on an agent, or a refresh of a standing order's
        /// readings. The readings are kept for <see cref="WingWorld"/>; a command is issued as the
        /// local doors issue theirs, against the commander the agent named. A refresh is answered
        /// only when its order no longer stands.</summary>
        private void OnWingCall(byte home, ulong steamId, byte[] payload)
        {
            NexusWingCall call;
            if (!NexusWingCall.TryDecode(payload, out call))
            {
                Log.Warn("NEXUS_WING_UNREADABLE home=" + home + " bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            DateTime now = DateTime.UtcNow;
            if (_wingReadings != null)
            {
                _wingReadings.Put(call, now);
                if (now - _wingReadingsPrunedUtc > NexusWingReadings.Fresh)
                {
                    _wingReadingsPrunedUtc = now;
                    _wingReadings.Prune(now);
                }
            }
            var result = new NexusWingResult
            {
                Id = call.Id, Command = call.Command, Antenna = call.Antenna, Identity = call.Identity,
                StationGrid = call.Station ? call.AntennaGrid : 0,
            };
            // A station's call: its grid's hits are the agent's to see, and stamped as it saw them.
            if (call.Station && call.SHit != 0 && _runtime != null)
                _runtime.RecordStationAttacker(call.AntennaGrid, call.SHit, now - TimeSpan.FromSeconds(Math.Max(0, call.SHitAgeS)));
            if (call.Command != 0 && (WingVia)call.Via == WingVia.Host
                && (_runtime == null || !_runtime.HostCallsFor(call.AOwnerTag)))
            {
                // An agent raises only its NPC stations' calls; a player's station calls when its owner says so.
                result.Message = "Station call refused: not a station the host calls for.";
                result.Ended = true;
            }
            else if (call.Command == 0)
            {
                if (_runtime != null && (call.Station ? _runtime.HasStationCall(call.AntennaGrid)
                                                      : _runtime.HasWingOrder(call.Identity, call.Antenna))) return;
                result.Ended = true;
            }
            else
            {
                try
                {
                    bool ok = false;
                    result.Message = _runtime == null ? "Wing refused: the Factions runtime is not ready."
                        : _runtime.IssueWing(new WingRequest
                        {
                            Via = (WingVia)call.Via,
                            Command = (WingCommand)call.Command,
                            AntennaEntityId = call.Antenna,
                            CommanderIdentityId = call.Identity,
                            CommanderTag = call.Tag,
                            PresserLock = call.PresserLock,
                            Crosshair = call.Crosshair,
                            PilotLock = call.PilotLock,
                            CombatBlockLock = call.CombatLock,
                            CockpitRetained = call.Retained,
                        }, out ok);
                    result.Ok = ok;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Relayed wing command from server " + home + " failed.");
                    result.Message = "Wing refused: the server could not answer.";
                }
                result.Ended = _runtime == null || (call.Station ? !_runtime.HasStationCall(call.AntennaGrid)
                                                                 : !_runtime.HasWingOrder(call.Identity, call.Antenna));
            }
            if (_nexusRelay != null)
                _nexusRelay.TrySend(home, Factions.Wire.NexusRelayWire.Write(Factions.Wire.RelayKind.WingResult,
                    (byte)_nexusServerId, steamId, result.Encode()));
        }

        private void OnCaptureUpload(byte home, ulong steamId, byte[] payload)
        {
            NexusCaptureUpload up;
            if (!NexusCaptureUpload.TryDecode(payload, out up))
            {
                Log.Warn("NEXUS_CAPTURE_UNREADABLE home=" + home + " bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            NexusCaptureResult result;
            try
            {
                VRage.Game.MyObjectBuilder_CubeGrid[] grids = string.IsNullOrEmpty(up.GridsXml) ? null : CustomPrefabStore.GridsFromXml(up.GridsXml);
                result = _runtime == null ? new NexusCaptureResult { Id = up.Id, Message = "Factions runtime not ready." }
                       : up.Kind == (int)NexusCaptureKind.Design ? _runtime.RegisterRelayedDesign(up, grids, home)
                       : _runtime.RegisterRelayedCapture(up, grids);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Relayed capture from server " + home + " failed.");
                result = new NexusCaptureResult { Id = up.Id, Message = "The scan failed on the server.", Reason = "The server could not answer." };
            }
            if (_nexusRelay != null)
                _nexusRelay.TrySend(home, Factions.Wire.NexusRelayWire.Write(Factions.Wire.RelayKind.CaptureResult,
                    (byte)_nexusServerId, steamId, result.Encode()));
        }

        /// <summary>Agent: tells this server's players near each fight the authority resolved, as its
        /// runtime tells its own -- the chat whisper within a few km, and the HUD notice, in their
        /// faction's tone, to every linked player within observation range.</summary>
        private void TellAgentCombat(List<NexusCombatCall> calls)
        {
            if (calls == null || _uplinkManager == null) return;
            var chat = Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>();
            for (int i = 0; i < calls.Count; i++)
            {
                NexusCombatCall c = calls[i];
                if (c == null || string.IsNullOrEmpty(c.Sentence)) continue;
                try
                {
                    CombatOutcome o = c.ToOutcome();
                    if (chat != null)
                        foreach (ulong steamId in PlayersWithinKm(o.PositionKm, AgentCombatWhisperKm))
                            chat.SendMessageAsOther("Factions", c.Sentence, VRageMath.Color.CornflowerBlue, steamId);
                    List<CombatNotice> notices = o.Recipients(PlayersWithinKm, _tuning.ObservationRadiusKm, LocalFactionTagOf);
                    for (int n = 0; n < notices.Count; n++)
                    {
                        if ((_uplinkManager.GrantedFeatures(notices[n].SteamId) & Factions.Wire.WireFeatures.CombatOutcome) == 0) continue;
                        bool sent = _uplinkManager.Send(notices[n].SteamId, Factions.Wire.FactionsWire.WriteCombatOutcomeNotice(
                            (Factions.Wire.WireCombatTone)(byte)notices[n].Tone, c.Sentence));
                        if (UplinkManager.DebugTraffic)
                            _uplinkManager.WriteTraffic("COMBAT_NOTICE steam=" + notices[n].SteamId + " tone=" + notices[n].Tone
                                                        + " sent=" + sent + " relayed");
                    }
                }
                catch (Exception ex) { Log.Warn(ex, "Agent combat notice failed."); }
            }
        }

        /// <summary>Agent: a combat notice the authority addressed to one player here, on their HUD.</summary>
        private void SendAgentCombatNotice(long identityId, CombatTone tone, string text)
        {
            UplinkManager uplink = _uplinkManager;
            MySession session = MySession.Static;
            if (uplink == null || session == null || session.Players == null) return;
            ulong steamId = session.Players.TryGetSteamId(identityId);
            if (steamId == 0UL || (uplink.GrantedFeatures(steamId) & Factions.Wire.WireFeatures.CombatOutcome) == 0) return;
            bool sent = uplink.Send(steamId, Factions.Wire.FactionsWire.WriteCombatOutcomeNotice((Factions.Wire.WireCombatTone)(byte)tone, text));
            if (UplinkManager.DebugTraffic)
                uplink.WriteTraffic("COMBAT_NOTICE steam=" + steamId + " tone=" + tone + " sent=" + sent + " kill relayed");
        }

        /// <summary>The authority's fleet-message radius: close enough to have plausibly seen it.</summary>
        private const double AgentCombatWhisperKm = 3.0;

        /// <summary>The SE faction tag of a player on this server, or null when they have none.</summary>
        private static string LocalFactionTagOf(ulong steamId)
        {
            MySession session = MySession.Static;
            if (session == null || session.Players == null || session.Factions == null) return null;
            long identity = session.Players.TryGetIdentityId(steamId);
            if (identity == 0) return null;
            var faction = session.Factions.GetPlayerFaction(identity);
            return faction != null ? faction.Tag : null;
        }

        /// <summary>Agent: carries the authority's node stock moves through the blocks they name, once
        /// each, and answers each player's client. What a deposit removed, and what a withdrawal could
        /// not place, goes back to the node as a stock event.</summary>
        private void ApplyAgentStockMoves(List<NexusStockMove> moves)
        {
            if (moves == null || _agentHands == null) return;
            SpatialGraph graph = _agentHands.Graph;
            StationStoreManager store = _agentHands.Store;
            for (int i = 0; i < moves.Count; i++)
            {
                NexusStockMove m = moves[i];
                if (m == null || m.Seq <= _agentHands.StockMovesThrough) continue;
                _agentHands.StockMovesThrough = m.Seq;

                long moved = 0, credit = m.Deposit ? 0 : m.Amount;
                string message;
                bool ok = false;
                SpatialNode node = null;
                if (graph == null || m.Node == null || !graph.Nodes.TryGetValue(m.Node, out node))
                    message = "Factions does not know this station.";
                else
                {
                    try
                    {
                        ok = PlayerNodeTransferManager.ApplyMove(m, node,
                            store != null ? (PlayerNodeTransferManager.DefinitionIdResolver)store.TryResolveDefinitionId : null,
                            out moved, out credit, out message);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "Node stock move " + m.Seq + " failed.");
                        message = "The transfer failed on this server.";
                        moved = 0;
                        credit = m.Deposit ? 0 : m.Amount;
                    }
                }
                if (credit > 0)
                    _agentHands.RecordStoreEvent(new NexusStoreEvent
                    {
                        Kind = NexusStoreEventKind.Stock, Node = m.Node, Key = m.Key, Amount = credit,
                        Steam = m.Steam,
                    });
                if (_uplinkManager != null)
                    _uplinkManager.Send(m.Steam, Factions.Wire.FactionsWire.WriteNodeTransferResult(ok, moved, message));
            }
        }

        /// <summary>Authority: whether an agent has any player connected, by its reports.</summary>
        private bool HasRemotePlayers(byte home)
        {
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
                if (remote[i].HomeServerId == home) return true;
            return false;
        }

        /// <summary>Agent: the station traffic voice, with the authority's events heard on this
        /// server's players' own radios.</summary>
        private void StartAgentTraffic(IPlanetCatalogue planets)
        {
            _agentPlanets = planets;
            _agentOcclusion = null;
            _agentTraffic = new TrafficCallouts(_uplinkManager, () => _tuning, AgentOcclusion, TrafficListeners, TrafficBroadcastKm);
        }

        /// <summary>Built on first use with bodies to build from, as the runtime's is: an early read
        /// before the session registers its planets would leave every sightline clear.</summary>
        private SensorOcclusion AgentOcclusion()
        {
            if (_agentOcclusion != null) return _agentOcclusion;
            SensorOcclusion built = SensorOcclusion.From(_agentPlanets, _tuning);
            if (built.Count > 0) _agentOcclusion = built;
            return built;
        }

        private void PlayAgentTraffic(List<NexusTrafficCall> calls)
        {
            TrafficCallouts voice = _agentTraffic;
            if (voice == null || calls == null) return;
            for (int i = 0; i < calls.Count; i++)
            {
                NexusTrafficCall c = calls[i];
                if (c == null) continue;
                try
                {
                    if (c.Hail == 0) { voice.Play(c.ToEvent(), c.NodeName, c.Owner, c.Speaker); continue; }
                    // A hail to one pilot, from the faction's node nearest them: the snapshot's node.
                    SpatialGraph graph = _agentHands != null ? _agentHands.Graph : null;
                    SpatialNode node;
                    if (c.To == 0UL || graph == null || c.Node == null || !graph.Nodes.TryGetValue(c.Node, out node)) continue;
                    bool local;
                    voice.Hail(c.To, (Factions.Wire.WireTrafficEvent)c.Hail, c.Owner, c.Speaker, node, c.NodeName, out local);
                }
                catch (Exception ex) { Log.Warn(ex, "Agent traffic callout failed."); }
            }
        }
        private const int SnapshotTicks = 600;
        private static readonly TimeSpan SnapshotRefresh = TimeSpan.FromSeconds(60);

        /// <summary>Authority: sends each reporting agent the node snapshot when it changed, and
        /// unconditionally once a minute so a lost frame or an agent restart heals.</summary>
        private void SendSnapshots()
        {
            if (_remotePlayers.HomeCount == 0 || _runtime == null || _runtime.World == null) return;
            WorldModel world = _runtime.World;
            NexusNodeSnapshot snapshot = NexusNodeSnapshot.Build(world.Graph.Nodes.Values, _runtime.States, 0);
            snapshot.Munitions = _runtime.StationGunsFromStock;
            snapshot.Loot = FeatureSwitch.IsOn(FeatureSwitch.StationLoot);
            snapshot.Captures = _runtime.CaptureGeneration;
            StationRecordMirror.Fill(snapshot);
            NexusNodeSnapshot.FillTradeBars(snapshot, _runtime.States, _runtime.Tuning);
            ulong hash = Fnv64(snapshot.Encode());
            if (_snapshotPayload == null || hash != _snapshotHash)
            {
                _snapshotHash = hash;
                _snapshotSequence = Math.Max(_snapshotSequence + 1, DateTime.UtcNow.Ticks);
                snapshot.Sequence = _snapshotSequence;
                _snapshotPayload = snapshot.Encode();
            }
            DateTime now = DateTime.UtcNow;
            foreach (byte home in _remotePlayers.Homes())
            {
                long sentSeq; DateTime sentUtc;
                bool current = _snapshotSentSeq.TryGetValue(home, out sentSeq) && sentSeq == _snapshotSequence;
                if (current && _snapshotSentUtc.TryGetValue(home, out sentUtc) && now - sentUtc < SnapshotRefresh) continue;
                byte[] frame = Factions.Wire.NexusRelayWire.Write(Factions.Wire.RelayKind.NodeSnapshot,
                    (byte)_nexusServerId, 0UL, _snapshotPayload);
                if (_nexusRelay.TrySend(home, frame))
                {
                    _snapshotSentSeq[home] = _snapshotSequence;
                    _snapshotSentUtc[home] = now;
                }
            }
        }

        private static ulong Fnv64(byte[] data)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= 1099511628211UL; }
                return h;
            }
        }

        /// <summary>Authority: one agent's position report, on the game thread.</summary>
        private void OnRemotePositions(byte homeServerId, byte[] payload)
        {
            List<Factions.Wire.RelayPlayerPosition> read;
            if (!Factions.Wire.NexusRelayWire.TryReadPlayerPositions(payload, out read))
            {
                Log.Warn("RELAY_POSITIONS_UNREADABLE home=" + homeServerId + " bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            var players = new List<RemotePlayer>(read.Count);
            for (int i = 0; i < read.Count; i++)
            {
                Factions.Wire.RelayPlayerPosition p = read[i];
                players.Add(new RemotePlayer
                {
                    SteamId = p.SteamId, IdentityId = p.IdentityId,
                    PositionM = new Vec3(p.X, p.Y, p.Z), Velocity = new Vec3(p.Vx, p.Vy, p.Vz),
                    SeatGridId = p.SeatGridId, GridPositionM = new Vec3(p.GridX, p.GridY, p.GridZ),
                    SensorKm = p.SensorKm, AntennaCount = p.AntennaCount,
                });
            }
            if (_remotePlayers.Update(homeServerId, players, DateTime.UtcNow))
                Log.Info("RELAY_POSITIONS home=" + homeServerId + " state=reporting players=" + players.Count);
        }

        /// <summary>Per frame on an agent: deliver relayed frames, sweep the roster, and watch the
        /// authority's liveness so a restart there is followed by a resync here.</summary>
        private void TickAgent()
        {
            if (_uplinkManager == null || _nexusRelay == null) return;
            _nexusRelay.Drain(_uplinkManager.OnRelayInbound, 256);
            _relayTick++;
            if (_chatCommands != null && _relayTick % AgentPruneTicks == 0) _chatCommands.Tick(DateTime.UtcNow);
            if (_agentCaptures != null && _relayTick % AgentPruneTicks == 0) _agentCaptures.Tick(DateTime.UtcNow);
            if (_agentWing != null)
            {
                try { _agentWing.Tick(DateTime.UtcNow); }
                catch (Exception ex) { Log.Warn(ex, "Agent wing tick failed."); }
            }
            if (_agentHands != null)
            {
                SwapAgentCaptures();
                try { _agentShipAssets?.Tick(); }
                catch (Exception ex) { Log.Warn(ex, "Agent ship assets tick failed."); }
                try { _agentRestorationRefs?.Pump(); _agentRestoration?.Pump(); }
                catch (Exception ex) { Log.Warn(ex, "Agent restoration pump failed."); }
                // The per-frame halves of what the materialisers start, run on the authority by
                // FactionRuntime.TickFleetFlight. Without RockContactPhysics an agent's feature rock
                // never gets a collision grid: its patch declines every build outside a hot set that
                // only this scan fills. Without GridAssembly.Tick every close is queued and never
                // carried out: fleet despawns, unheld closes, the leftover sweep and station despawns
                // all go through its paced queue.
                try
                {
                    SpawnPacing.TickFrame();
                    GridAssembler.Instance.Tick();
                    GridAssembly.Tick();
                    RockContactPhysics.Tick();
                    ClipmapWaitSweep.Tick();
                    StationQuiet.Tick(_agentHands.Stations, _tuning);
                    EconomyTickSlicer.Tick();
                }
                catch (Exception ex) { Log.Warn(ex, "Agent frame helpers failed."); }
                if (_relayTick % GpsUpdateTicks == 0)
                {
                    try { SampleGridEmissions(); _agentContactsSampled = true; }
                    catch (Exception ex) { Log.Warn(ex, "Player excitation sample failed."); }
                }
            }
            if (_agentHands != null && _agentHands.HasSnapshot)
            {
                try
                {
                    RefreshPlayerPositionCache();
                    RefreshPlayerPositionsKm();
                    _agentHands.Tick(_playerPositionsKm);
                }
                catch (Exception ex) { Log.Warn(ex, "Agent materialisation failed."); }
                try { _gridReaper?.Tick(); }
                catch (Exception ex) { Log.Warn(ex, "Grid reaper tick failed."); }
                try { _agentDebris?.Tick(); }
                catch (Exception ex) { Log.Warn(ex, "Debris absorb tick failed."); }
            }
            if (_relayTick % AgentPositionTicks == 0)
            {
                try { SendPlayerPositions(); SendAgentReport(); }
                catch (Exception ex) { Log.Warn(ex, "Player position report failed."); }
            }
            if (_relayTick % AgentPruneTicks == 0)
            {
                try { PruneUplinks(); }
                catch (Exception ex) { Log.Warn(ex, "Uplink prune failed."); }
            }
            if (_relayTick % AgentLivenessTicks == 0)
            {
                bool? online = _nexusRelay.IsServerOnline((byte)_tuning.NexusAuthorityServerId);
                bool now = online == true;
                if (now && !_authorityOnline)
                {
                    Log.Info("Nexus authority server " + _tuning.NexusAuthorityServerId + " is online; resyncing "
                             + _uplinkManager.LinkedCount + " linked client(s).");
                    _uplinkManager.ResyncAll();
                }
                else if (!now && _authorityOnline)
                {
                    Log.Warn("Nexus authority server " + _tuning.NexusAuthorityServerId + " is offline; "
                             + "clients keep their link and are resynced when it returns.");
                }
                _authorityOnline = now;
            }
        }

        /// <summary>Per frame on the authority: deliver relayed frames to the same handlers a local
        /// client's messages take, and occasionally drop remote clients the cluster no longer sees.</summary>
        private readonly List<long> _arrivalScratch = new List<long>();

        /// <summary>Authority: a grid an agent flew that Nexus carried into this server's sector is
        /// flown here again, under the fleet the registry held it for.</summary>
        private void AdoptArrivalsAsAuthority()
        {
            SectorHandover.TakeArrivals(_arrivalScratch);
            if (_arrivalScratch.Count == 0) return;
            var routed = _fleetSpawner as VanillaFleetSpawner;
            if (routed == null) return;
            foreach (long eid in _arrivalScratch)
            {
                if (!_fleetRegistry.Exists(eid)) continue;
                NexusFleetOrder template;
                DateTime? damaged;
                if (!_fleetRegistry.TakeBack(eid, out template, out damaged)) continue;
                bool ok = routed.AdoptArrival(eid, template.Key, template.Tag, (ActionKind)template.Kind, template.Prefab,
                                              template.MunitionsId, damaged);
                Log.Info("NEXUS_FLEET_ADOPTED key=" + template.Key + " eid=" + eid + " kind=" + (ActionKind)template.Kind + " ok=" + ok);
                _runtime?.ResumeHandedOver(eid, _nexusServerId);
            }
        }

        private void DrainRelayAsAuthority()
        {
            if (_nexusRole != NexusRole.Authority || _uplinkManager == null || _nexusRelay == null) return;
            _nexusRelay.Drain(_uplinkManager.OnRelayInbound, 256);
            if ((_relayTick + 1) % SnapshotTicks == 0)
            {
                try { SendSnapshots(); }
                catch (Exception ex) { Log.Warn(ex, "Node snapshot send failed."); }
            }
            if ((_relayTick + 1) % AgentPositionTicks == 0 && _remotePlayers.HomeCount > 0)
            {
                foreach (byte home in _remotePlayers.Expire(DateTime.UtcNow, RemotePositionMaxAge))
                    Log.Info("RELAY_POSITIONS home=" + home + " state=expired");
            }
            if (SectorHandover.Attached)
            {
                try { AdoptArrivalsAsAuthority(); }
                catch (Exception ex) { Log.Warn(ex, "Fleet arrival adoption failed."); }
            }
            if ((_relayTick + 1) % AgentPositionTicks == 0)
            {
                List<string> abandoned = _fleetRegistry.Expire(DateTime.UtcNow);
                if (abandoned.Count > 0)
                    Log.Info("NEXUS_FLEET_SPAWNS abandoned=" + abandoned.Count + " live=" + _fleetRegistry.LiveCount);
            }
            if (++_relayTick % AuthorityRemotePruneTicks == 0 && _uplinkManager.RemoteCount > 0)
            {
                var roster = _nexusRelay.OnlinePlayers();
                if (roster != null) _uplinkManager.PruneRemote(new HashSet<ulong>(roster));
            }
        }

        private void StopRuntime()
        {
            try { _runtime?.ReleaseFleetControl(); }
            catch (Exception ex) { Log.Warn(ex, "Flight control shutdown failed."); }
            SimGridMarks.Detach();
            SectorHandover.Detach();
            StoreShipSaleMarks.Store = null;
            StoreShipSaleMarks.ShelfSold = null;
            StationMaterializer.ClaimLog = null;
            _blockPauseNotices?.Dispose();
            _blockPauseNotices = null;
            try { _uplinkManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Uplink shutdown failed."); }
            _uplinkManager = null;
            try { _playerScannerManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player scanner shutdown failed."); }
            _playerScannerManager = null;
            try { _gridSpeedManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Grid speed manager close failed."); }
            _gridSpeedManager = null;
            try { _munitionSpeedManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Munition speed manager close failed."); }
            _munitionSpeedManager = null;
            try { _playerMapManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player map shutdown failed."); }
            _playerMapManager = null;
            try { _playerExpandManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player expand shutdown failed."); }
            _playerExpandManager = null;
            try { _playerNodeDetailManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player node detail manager close failed."); }
            _playerNodeDetailManager = null;
            try { _playerStationRefitManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player station refit manager shutdown failed."); }
            _playerStationRefitManager = null;
            _dockSupplyManager = null;
            _restorationService = null;
            try { _playerShipAssetsManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player ship assets manager close failed."); }
            _playerShipAssetsManager = null;
            try { _playerAssetsManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player assets shutdown failed."); }
            _playerAssetsManager = null;
            try { _playerStandingOrdersManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Standing-orders manager close failed."); }
            _playerStandingOrdersManager = null;
            try { _playerShelfManager?.Close(); }
            catch (Exception ex) { Log.Warn("PlayerShelfManager close failed: " + ex.Message); }
            _playerShelfManager = null;
            try { _playerBankSalesManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player bank sales manager close failed."); }
            _playerBankSalesManager = null;
            try { _playerDiplomacyManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player diplomacy manager close failed."); }
            _playerDiplomacyManager = null;
            try { _playerWingManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player wing manager close failed."); }
            _playerWingManager = null;
            try { _playerCrewOrderManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player crew order manager close failed."); }
            _playerCrewOrderManager = null;
            _agentTerminalSurfaces = null;
            try { _agentCaptures?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Agent capture relay close failed."); }
            _agentCaptures = null;
            try { _agentWing?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Agent wing relay close failed."); }
            _agentWing = null;
            WingWorld.Relayed = null;
            AgentSiege.CombatSink = null;
            _wingReadings = null;
            _agentDockSupply = null;
            try { _agentShipAssets?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Agent ship assets close failed."); }
            _agentShipAssets = null;
            _agentRestoration = null;
            _agentRestorationRefs = null;
            _agentPrices = null;
            _combatOutcomeNotices?.Close();
            _combatOutcomeNotices = null;
            _trespassHailer?.AttachVoice(null);
            _runtime?.AttachOccupationSink(null);
            _trafficCallouts?.Close();
            _trafficCallouts = null;

            try { _playerFleetDoctrineManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Fleet doctrine manager close failed."); }
            _playerFleetDoctrineManager = null;
            _playerDesignVersionManager = null;
            try { _playerTacticsManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Tactics manager close failed."); }
            _playerTacticsManager = null;
            try { _playerCampaignsManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Campaigns manager close failed."); }
            _playerCampaignsManager = null;
            try { _playerFactionDemandManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Faction demand manager close failed."); }
            _playerFactionDemandManager = null;

            try { _playerEventsManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player events shutdown failed."); }
            _playerEventsManager = null;
            try { _playerRockManager?.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Player rock manager close failed."); }
            _playerRockManager = null;
            // Cleared with the manager, not separately: while this stands the replication gate has a
            // live owned-id set to consult, and a stale one would withhold rock from the next session.
            FeatureRockMaterializer.Active = null;
            _presenceManager = null;
            try { _offerReplyRouter?.DetachChat(); }
            catch (Exception ex) { Log.Warn(ex, "Offer reply router detach threw."); }
            _offerReplyRouter = null;
            try { _conquerOfferManager?.DetachChat(); }
            catch (Exception ex) { Log.Warn(ex, "ConquerOfferManager detach threw."); }
            _conquerOfferManager = null;
            try { _claimOfferManager?.DetachChat(); }
            catch (Exception ex) { Log.Warn(ex, "Claim offer chat detach failed."); }
            _claimOfferManager = null;
            _presenceReinforceManager = null;
            _playerNodeTransferManager = null;
            try { _respawnPlacement?.Detach(); }
            catch (Exception ex) { Log.Warn(ex, "Respawn placement detach failed."); }
            RespawnPlacement.Instance = null;
            _respawnPlacement = null;
            NodeSpawnManager.Instance = null;
            _nodeSpawnManager = null;
            try { _debrisAbsorbManager?.DetachChat(); }
            catch (Exception ex) { Log.Warn(ex, "Debris absorb chat detach failed."); }
            _debrisAbsorbManager = null;
            _gridReaper = null;
            try { CrewPresence.Close(); }
            catch (Exception ex) { Log.Warn(ex, "Crew trial shutdown failed."); }
            _storeManager    = null;
            _playerMarketManager?.Flush();
            _playerMarketManager = null;
            _marketMemory    = null;
            _priceUpdater    = null;
            _planetSnapshotWriter = null;
            _planetCatalogue = null;
            _playerIndexWriter    = null;
            try { _reputationManager?.Detach(); }
            catch (Exception ex) { Log.Warn(ex, "Reputation manager detach failed."); }
            _reputationManager    = null;
            try { if (_runtime != null) _runtime.Flush(); }
            catch (Exception ex) { Log.Warn(ex, "Runtime flush on stop failed."); }
            _runtime = null;
            try { if (_decisionLog != null) _decisionLog.Dispose(); } catch { }
            _decisionLog = null;
            _gpsHashes.Clear();
        }

        /// <summary>
        /// Hot-reload tuning.json without restarting Torch. Runtime-derived fields
        /// (FleetSpeedMinKmPerMin, FreeMinerCargoVolumeLiters) are preserved so their
        /// SE-sourced values cannot be accidentally overwritten from disk.
        /// </summary>
        /// <returns>Human-readable result line for the chat command response.</returns>
        public string ReloadTuning()
        {
            try
            {
                // Stash the two SE-derived fields that must not be overwritten from disk.
                double savedSpeed = _tuning.FleetSpeedMinKmPerMin;
                long savedCargo   = _tuning.FreeMinerCargoVolumeLiters;

                ConfigStore.LoadTuning(_tuning);

                // Restore SE-derived values.
                _tuning.FleetSpeedMinKmPerMin    = savedSpeed;
                _tuning.FreeMinerCargoVolumeLiters = savedCargo;

                _runtime?.NotifyTuningReloaded();
                int backfilled = _runtime?.BackfillStationFields() ?? 0;
                return backfilled > 0
                    ? "tuning.json reloaded OK. Backfilled station fields on " + backfilled + " node(s)."
                    : "tuning.json reloaded OK.";
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ReloadTuning failed.");
                return "ReloadTuning failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Resolve cargo volume in litres for a cube block by SubTypeId. Iterates all SE block
        /// definitions at session load so no TypeId reference is needed. Prefers the explicit
        /// InventorySize field (volume = X*Y*Z m³ × 1000); falls back to deriving from grid-unit
        /// block Size × cell size when InventorySize is absent (e.g. SmallBlockLargeContainer).
        /// </summary>
        private long ResolveCargoVolumeLiters(string subTypeId)
        {
            if (string.IsNullOrEmpty(subTypeId)) return 0;
            try
            {
                if (MyDefinitionManager.Static == null) return 0;
                foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
                {
                    var def = defBase as MyCargoContainerDefinition;
                    if (def == null) continue;
                    if (!string.Equals(def.Id.SubtypeName, subTypeId, StringComparison.OrdinalIgnoreCase)) continue;

                    // Explicit InventorySize (m³ per axis): product is total volume in m³.
                    float invVol = def.InventorySize.X * def.InventorySize.Y * def.InventorySize.Z;
                    if (invVol > 0f)
                        return (long)(invVol * 1000f);

                    // No explicit tag: derive from grid-unit block Size × cell-size (m).
                    float cellM = def.CubeSize == VRage.Game.MyCubeSize.Large ? 2.5f : 0.5f;
                    float physVol = def.Size.X * cellM * def.Size.Y * cellM * def.Size.Z * cellM;
                    return (long)(physVol * 1000f);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, PluginName + ": ResolveCargoVolumeLiters failed for '" + subTypeId + "'.");
            }
            return 0;
        }

        // Player positions (+ identity) cached once per Update() frame to avoid repeated
        // GetOnlinePlayers() calls from UpdateObservation (per-fleet) and UpdateStationObservation
        // (per-node). SteamId rides alongside position so fleet-progress chat (PlayersWithinKm) can
        // target specific nearby players without a second GetOnlinePlayers() scan.
        // Vel is what the player is moving at (their ship when seated, their suit otherwise): the traffic
        // voice's Doppler reads the listener's motion as well as the pilot's (FAC-419).
        private struct CachedPlayer
        {
            public VRageMath.Vector3D Pos; public ulong SteamId; public VRageMath.Vector3 Vel;
            /// <summary>The grid whose controls the player is at, null when they are not seated: the
            /// traffic voice's radio reach (FAC-419).</summary>
            public MyCubeGrid Seat; public long IdentityId;
            /// <summary>A bench phantom (<see cref="BenchPhantoms"/>): counted wherever presence is,
            /// never a recipient — it has no client, and Torch broadcasts a chat line whose steam id
            /// it cannot resolve.</summary>
            public bool Phantom;
        }
        private CachedPlayer[] _cachedPlayers = new CachedPlayer[0];

        /// <summary>The live spawner, for the operator's <c>factions assemble</c> test.</summary>
        private VanillaFleetSpawner _fleetSpawner;

        /// <summary>Drops uplinks whose client has disconnected. Built from the live online roster
        /// rather than the position cache, which only holds players that currently have a character.</summary>
        private void PruneUplinks()
        {
            if (_uplinkManager == null || _uplinkManager.LinkedCount == 0) return;
            if (MySession.Static == null) return;
            var online = new HashSet<ulong>();
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                if (player != null && !player.IsBot) online.Add(player.Id.SteamId);
            _uplinkManager.PruneOffline(online);
            _uplinkManager.AddRemoteTo(online);
            _playerMapManager?.PruneOffline(online);
            _playerExpandManager?.PruneOffline(online);
            _playerMarketManager?.PruneOffline(online);
            _playerAssetsManager?.PruneOffline(online);
            _playerNodeDetailManager?.PruneOffline(online);
            _playerEventsManager?.PruneOffline(online);
            _playerRockManager?.PruneOffline(online);
            _playerStandingOrdersManager?.PruneOffline(online);
            _playerFleetDoctrineManager?.PruneOffline(online);
            _playerTacticsManager?.PruneOffline(online);
            _playerShelfManager?.PruneOffline(online);
            _playerBankSalesManager?.PruneOffline(online);
            _playerDiplomacyManager?.PruneOffline(online);
            _playerCampaignsManager?.PruneOffline(online);
            _playerFactionDemandManager?.PruneOffline(online);
        }

        private static string SubtypeOf(Sandbox.Game.Entities.MyCubeBlock block)
        {
            return block == null || block.BlockDefinition == null
                ? null : block.BlockDefinition.Id.SubtypeName;
        }

        /// <summary>Mean emission multiplier across a grid's blocks of one kind, weighting every block
        /// equally. A half-prototech refit is half as quiet as a full one, which is the honest reading
        /// when the emission cannot be attributed to individual blocks — unlike a jump drive, where the
        /// one drive doing the work can be named.</summary>
        private static double MeanBlockEmission<T>(MyCubeGrid grid, Factions.Core.FactionTuning tuning)
            where T : Sandbox.Game.Entities.MyCubeBlock
        {
            if (grid == null || tuning == null) return 1.0;
            double sum = 0; int n = 0;
            AccumulateBlockEmission<T>(grid, tuning, ref sum, ref n);
            return n == 0 ? 1.0 : sum / n;
        }

        private static void AccumulateBlockEmission<T>(MyCubeGrid grid, Factions.Core.FactionTuning tuning,
            ref double sum, ref int count) where T : Sandbox.Game.Entities.MyCubeBlock
        {
            foreach (T block in grid.GetFatBlocks<T>())
            {
                if (block == null) continue;
                sum += tuning.BlockEmissionFor(SubtypeOf(block));
                count++;
            }
        }

        /// <summary>Mean emission across everything that <i>produces</i> the power being drawn —
        /// reactors and batteries together. Reactors alone would miss a battery-only hull entirely,
        /// which is most small ships and every prototech-battery refit.</summary>
        private static double MeanPowerBlockEmission(MyCubeGrid grid, Factions.Core.FactionTuning tuning)
        {
            if (grid == null || tuning == null) return 1.0;
            double sum = 0; int n = 0;
            AccumulateBlockEmission<Sandbox.Game.Entities.MyReactor>(grid, tuning, ref sum, ref n);
            AccumulateBlockEmission<Sandbox.Game.Entities.MyBatteryBlock>(grid, tuning, ref sum, ref n);
            return n == 0 ? 1.0 : sum / n;
        }

        /// <summary>Last known position of a player's grid, for inferring supercruise from displacement.
        /// Keyed by identity and stamped with the grid, so switching ships resets rather than
        /// producing a spurious jump-sized delta.</summary>
        private struct MotionSample
        {
            public long GridEntityId;
            public VRageMath.Vector3D Pos;
            public DateTime At;
            public bool WasSupercruising;

            /// <summary>When this grid last produced an FSD_OBSERVED line. Carried here rather than
            /// in a second dictionary: the sample is keyed by the grid, which is exactly the scope
            /// the sampling interval is per.</summary>
            public DateTime LastFsdTraceAt;
        }

        private readonly Dictionary<long, MotionSample> _gridMotion = new Dictionary<long, MotionSample>();

        /// <summary>Last resolved signature per GRID, keyed by entity id. One value serves both the
        /// HUD readout and the detected-set build, so the number a player is shown is the number
        /// their hull is found by.
        ///
        /// <para><b>Keyed by grid rather than by identity (FAC-463).</b> A hull radiates whether or
        /// not anyone is in the chair, and one player may own several. The seat decides what a player
        /// is *shown*, never what their ship *emits*.</para></summary>
        private readonly Dictionary<long, double> _gridSignature = new Dictionary<long, double>();
        /// <summary>Widest radius each sampled grid is broadcasting over, km. Sampled beside the
        /// signature so the floor and the emission term read one antenna walk (FAC-140).</summary>
        private readonly Dictionary<long, double> _gridBroadcastKm = new Dictionary<long, double>();

        /// <summary>Activity excitation per grid, keyed by entity id. Not persisted — it lives
        /// on a seconds timescale and a reloaded world starts everything at rest.</summary>
        private readonly Dictionary<long, Factions.Core.SignatureExcitation> _gridExcitation =
            new Dictionary<long, Factions.Core.SignatureExcitation>();

        /// <summary>Summed armament per grid, with the block count it was measured at. The gun walk is
        /// the expensive half of a sample and armament only changes when blocks do, so a grid whose
        /// count is unchanged reuses the figure.</summary>
        private struct WeaponSample
        {
            public int Blocks;
            public double Dps;
        }
        private readonly Dictionary<long, WeaponSample> _gridWeapons = new Dictionary<long, WeaponSample>();

        /// <summary>When each unseated grid was last sampled, so the walk is throttled per grid rather
        /// than run for the whole population every pass.</summary>
        private readonly Dictionary<long, DateTime> _gridSampledAt = new Dictionary<long, DateTime>();

        /// <summary>Player-owned grids, refreshed on the same throttle. Enumerating every entity is
        /// the one part of this that scales with the server rather than with the player count.</summary>
        private readonly List<MyCubeGrid> _ownedGrids = new List<MyCubeGrid>();
        private DateTime _ownedGridsAt;
        private readonly HashSet<long> _sampledThisPass = new HashSet<long>();
        private readonly HashSet<long> _liveGrids = new HashSet<long>();

        /// <summary>Samples what each player's grid is doing.
        ///
        /// <para><b>Deliberately on the scanner cadence, not the global pass.</b> The decay windows are
        /// 10-15 s; the global pass runs on the order of minutes, so sampling there would miss almost
        /// every burst and the term would read as permanently at rest. Observation has to be faster
        /// than the shortest tail it is trying to catch — reading it (<see cref="BuildPlayerContacts"/>)
        /// can stay on the slower pass.</para></summary>
        private void SampleGridEmissions()
        {
            if (MySession.Static == null) return;
            // An agent has no runtime: it samples its own players' grids for the authority's sensors
            // (ReadAgentContacts) and its own clients' signature panel.
            Factions.Core.FactionTuning tuning = _runtime != null && _runtime.World != null ? _runtime.World.Tuning
                                               : _nexusRole == NexusRole.Agent ? _tuning : null;
            if (tuning == null) return;

            DateTime now = DateTime.UtcNow;
            long started = Stopwatch.GetTimestamp();
            int seated = 0;
            _sampledThisPass.Clear();
            _liveGrids.Clear();

            // Seated grids first, every pass: they carry the HUD readout, and a player at the controls
            // is the one hull whose bursts are worth catching at this cadence.
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                var controller = player.Controller?.ControlledEntity as Sandbox.Game.Entities.MyShipController;
                MyCubeGrid grid = controller?.CubeGrid;
                if (grid == null || grid.MarkedForClose) continue;

                double signature = SampleGrid(grid, player.Id.SteamId, tuning, now);
                _sampledThisPass.Add(grid.EntityId);
                _liveGrids.Add(grid.EntityId);
                seated++;

                if (_uplinkManager != null && signature > 0
                    && (_uplinkManager.GrantedFeatures(player.Id.SteamId) & Factions.Wire.WireFeatures.Signature) != 0)
                {
                    _uplinkManager.Send(player.Id.SteamId,
                        Factions.Wire.FactionsWire.WriteSignatureStatus(signature));
                }
            }

            // Every other player-owned grid. Throttled per grid rather than per pass, so the walk cost
            // is bounded by the population and not by the tick rate.
            double throttle = tuning.SignatureGridSampleSeconds;
            RefreshOwnedGrids(tuning, now, throttle);
            for (int i = 0; i < _ownedGrids.Count; i++)
            {
                MyCubeGrid grid = _ownedGrids[i];
                if (grid == null || grid.MarkedForClose) continue;
                long id = grid.EntityId;
                _liveGrids.Add(id);
                if (_sampledThisPass.Contains(id)) continue;

                DateTime last;
                if (throttle > 0 && _gridSampledAt.TryGetValue(id, out last)
                    && (now - last).TotalSeconds < throttle) continue;

                SampleGrid(grid, 0, tuning, now);
                _sampledThisPass.Add(id);
            }

            PruneGridStores();

            // Cost, where the population that drives it is visible next to it. Behind the same
            // toggle as the acquire/lose lines, and the string is built only when it is on.
            if (FactionRuntime.DebugSensing && _decisionLog != null)
            {
                double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                _decisionLog.Write("SENSE_SAMPLE seated=" + seated
                           + " owned=" + _ownedGrids.Count
                           + " sampled=" + _sampledThisPass.Count
                           + " tracked=" + _gridSignature.Count
                           + " ms=" + ms.ToString("0.00", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Samples one grid and returns its resolved signature.
        ///
        /// <para>The same walk for a crewed hull and an empty one — what a ship radiates does not
        /// depend on whether anyone is in the chair (FAC-463). Only the HUD readout is seat-gated,
        /// and that is decided by the caller.</para></summary>
        private double SampleGrid(MyCubeGrid grid, ulong steamIdForTrace,
                                  Factions.Core.FactionTuning tuning, DateTime now)
        {
            long id = grid.EntityId;
            Factions.Core.SignatureExcitation ex;
            if (!_gridExcitation.TryGetValue(id, out ex))
            {
                ex = new Factions.Core.SignatureExcitation();
                _gridExcitation[id] = ex;
            }

            // Absolute newtons, not acceleration: a big hull burning gently emits more than a
            // small one burning hard, and the model is meant to say so.
            var thrust = grid.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>();
            if (thrust != null && tuning.SignatureThrustReferenceN > 0)
            {
                ex.Observe(Factions.Core.SignatureSource.Thrust,
                           thrust.FinalThrust.Length() / tuning.SignatureThrustReferenceN
                               * MeanBlockEmission<Sandbox.Game.Entities.MyThrust>(grid, tuning), now);
            }

            // Grid electrical draw, in MW. Absolute for the same reason as thrust — draw tracks
            // hull volume, which is the size signal this reading exists to carry.
            var systems = grid.GridSystems;
            if (systems != null && systems.ResourceDistributor != null
                && tuning.SignaturePowerReferenceMw > 0)
            {
                float mw = systems.ResourceDistributor.TotalRequiredInputByType(
                    Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId, grid);
                if (mw > 0)
                    ex.Observe(Factions.Core.SignatureSource.PowerDraw,
                               mw / tuning.SignaturePowerReferenceMw
                                   * MeanPowerBlockEmission(grid, tuning), now);
            }

            // Loudest antenna wins rather than the sum: broadcasting twice as widely is what makes
            // you conspicuous, not owning two radios. An antenna with broadcasting switched off
            // only listens, and listening is free -- it still extends the sensor (ShipSensor).
            double antenna = 0;
            double broadcastM = 0;
            foreach (var a in grid.GetFatBlocks<Sandbox.Game.Entities.Cube.MyRadioAntenna>())
            {
                if (a == null || !a.IsWorking || !a.EnableBroadcasting.Value) continue;
                if (a.GetRadius() > broadcastM) broadcastM = a.GetRadius();
                var def = a.BlockDefinition as Sandbox.Definitions.MyRadioAntennaDefinition;
                if (def == null || def.MaxBroadcastRadius <= 0) continue;
                double share = a.GetRadius() / def.MaxBroadcastRadius;
                if (share > antenna) antenna = share;
            }
            ex.Observe(Factions.Core.SignatureSource.AntennaPower, antenna, now);

            // A beacon is its own term, not part of the antenna's: it declares four times the reach
            // (200 km against 50) and has no receive side to trade against, so it is weighted apart.
            // A working beacon broadcasts -- unlike an antenna there is no second switch, SE gates
            // its broadcaster on IsWorking alone.
            double beacon = 0;
            foreach (var b in grid.GetFatBlocks<Sandbox.Game.Entities.Cube.MyBeacon>())
            {
                if (b == null || !b.IsWorking || b.RadioBroadcaster == null) continue;
                double radiusM = b.RadioBroadcaster.BroadcastRadius;
                if (radiusM > broadcastM) broadcastM = radiusM;
                // The definition through BlockDefinition: MyBeacon.Definition is internal.
                var bdef = b.BlockDefinition as Sandbox.Definitions.MyBeaconDefinition;
                if (bdef == null || bdef.MaxBroadcastRadius <= 0) continue;
                double bshare = radiusM / bdef.MaxBroadcastRadius;
                if (bshare > beacon) beacon = bshare;
            }
            ex.Observe(Factions.Core.SignatureSource.BeaconPower, beacon, now);

            // The kilometres, not just the 0..1 intensity: a faction must detect this grid at
            // least as far as it is announcing itself, because SE's own HUD already does. The widest
            // of the two blocks wins -- a 200 km beacon is the furthest a grid can announce itself.
            _gridBroadcastKm[id] = broadcastM / 1000.0;

            // IsShooting lives on the ModAPI interface, not on the abstract block base: the
            // concrete guns declare it and MyUserControllableGun itself does not.
            //
            // The same walk answers both gun questions -- who is firing now, and how much armament
            // is bolted on -- so the summed DPS is taken here and cached against the block count
            // rather than costing a second pass over the guns.
            WeaponSample weapons;
            bool haveWeapons = _gridWeapons.TryGetValue(id, out weapons)
                               && weapons.Blocks == grid.BlocksCount;
            double weaponDps = haveWeapons ? weapons.Dps : 0;
            int guns = 0, firing = 0;
            var hullWeapons = haveWeapons ? null : new WeaponStatsHelper.HullWeaponPricer();
            foreach (var gun in grid.GetFatBlocks<Sandbox.Game.Weapons.MyUserControllableGun>())
            {
                if (gun == null) continue;
                guns++;
                var shootable = gun as Sandbox.ModAPI.Ingame.IMyUserControllableGun;
                if (shootable != null && shootable.IsShooting) firing++;
                if (haveWeapons || gun.BlockDefinition == null) continue;
                float dps, rangeM;
                WeaponStatsHelper.ComputeWeaponStats(gun.BlockDefinition, out dps, out rangeM);
                if (WeaponStatsHelper.HullWeaponPricer.Prices(gun.BlockDefinition, dps))
                    hullWeapons.Add(gun.BlockDefinition, dps, rangeM);
            }
            if (hullWeapons != null)
                foreach (var priced in hullWeapons.Finish())
                    if (priced.dps > 0) weaponDps += priced.dps;
            if (!haveWeapons)
                _gridWeapons[id] = new WeaponSample { Blocks = grid.BlocksCount, Dps = weaponDps };
            if (guns > 0)
                ex.Observe(Factions.Core.SignatureSource.WeaponsFire, (double)firing / guns, now);

            // The drives. Charging is a state we can read directly; the jump is an event, and
            // IsJumping covers the whole jump sequence rather than the instant of translation,
            // which is what makes it catchable on a sampling cadence at all.
            // Quietness is read off the drive that is actually doing the work, not averaged over
            // the grid: a prototech drive earns its discount for the jump it performs.
            double charging = 0, jumping = 0;
            foreach (var jd in grid.GetFatBlocks<Sandbox.Game.Entities.MyJumpDrive>())
            {
                if (jd == null) continue;
                double quiet = tuning.BlockEmissionFor(SubtypeOf(jd));
                if (jd.IsJumping && quiet > jumping) jumping = quiet;
                // A drive that is working and not yet full is drawing to fill. Reported at full
                // strength rather than scaled by charge: the draw is what radiates, and it is
                // roughly constant across the wind-up.
                if (jd.IsWorking && jd.StoredPowerRatio < 1.0f && quiet > charging) charging = quiet;
            }
            ex.Observe(Factions.Core.SignatureSource.JumpCharge, charging, now);
            if (jumping > 0) ex.Observe(Factions.Core.SignatureSource.JumpFlash, jumping, now);

            ObserveSupercruise(steamIdForTrace, grid, ex, tuning, now, jumping > 0);

            // Resolved here rather than in BuildPlayerContacts: this pass already walks the grid, and
            // caching it means the number a player is shown and the number a faction detects them by
            // are one value rather than two computations that can drift.
            double signature = grid.Physics != null
                ? Factions.Core.Signature.ForShip(grid.Physics.Mass, weaponDps, 0.0, 1,
                                                  ex.Multiplier(tuning, now), tuning.SignatureShipCeiling)
                : 0;
            _gridSignature[id] = signature;
            _gridSampledAt[id] = now;
            return signature;
        }

        /// <summary>Refreshes the player-owned grid list, at most once per throttle window.
        ///
        /// <para>This is the one part of the sampler that scales with the server rather than with the
        /// players on it, so it is deliberately the throttled part: the enumeration is a cheap
        /// ownership and size test, and the block walk behind it is throttled again per grid.</para>
        ///
        /// <para>The size floor drops debris — a one-block scrap is not a contact — and the ownership
        /// test is <see cref="GridReaper"/>'s, so "whose grid is this" has one answer in this
        /// plugin.</para></summary>
        private void RefreshOwnedGrids(Factions.Core.FactionTuning tuning, DateTime now, double throttle)
        {
            if (throttle > 0 && _ownedGridsAt != default(DateTime)
                && (now - _ownedGridsAt).TotalSeconds < throttle) return;
            _ownedGridsAt = now;
            _ownedGrids.Clear();

            MyPlayerCollection players = MySession.Static.Players;
            int minBlocks = tuning.SignatureGridMinBlocks;
            Sandbox.ModAPI.MyAPIGateway.Entities.GetEntities(null, entity =>
            {
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.MarkedForClose || grid.IsPreview || grid.Projector != null)
                    return false;
                if (grid.BlocksCount < minBlocks) return false;
                if (!GridReaper.IsPlayerOwned(players, grid)) return false;
                _ownedGrids.Add(grid);
                return false;
            });
        }

        /// <summary>Drops per-grid state for grids that are gone. Keyed on the live set this pass
        /// built, so a closed grid stops being a contact rather than lingering at its last
        /// signature.</summary>
        private void PruneGridStores()
        {
            if (_gridSignature.Count == _liveGrids.Count && _gridExcitation.Count == _liveGrids.Count) return;
            var gone = new List<long>();
            foreach (var kv in _gridExcitation) if (!_liveGrids.Contains(kv.Key)) gone.Add(kv.Key);
            for (int i = 0; i < gone.Count; i++)
            {
                long id = gone[i];
                _gridExcitation.Remove(id);
                _gridSignature.Remove(id);
                _gridBroadcastKm.Remove(id);
                _gridWeapons.Remove(id);
                _gridSampledAt.Remove(id);
                _gridMotion.Remove(id);
            }
        }

        /// <summary>Infers FSD supercruise from displacement between samples.
        ///
        /// <para><b>Position delta, never <c>Physics.LinearVelocity</c>.</b> The mod moves grids with
        /// <c>SetWorldMatrix(skipTeleportCheck:true)</c> — a teleport-style write — so physics velocity
        /// can read near zero the whole way and the term would silently never fire.</para>
        ///
        /// <para><b>A jump teleports too, and would otherwise read as an absurdly fast cruise.</b> Two
        /// guards, both needed: the speed must fall inside a band whose floor is far above any vanilla
        /// flight and whose ceiling is below a jump's implied speed, and a grid reporting
        /// <c>IsJumping</c> is skipped outright. Without the ceiling a single jump would light the
        /// entry burst for its full window.</para></summary>
        private void ObserveSupercruise(ulong steamId, MyCubeGrid grid,
            Factions.Core.SignatureExcitation ex, Factions.Core.FactionTuning tuning,
            DateTime now, bool isJumping)
        {
            VRageMath.Vector3D pos = grid.PositionComp.GetPosition();
            MotionSample prior;
            bool hadPrior = _gridMotion.TryGetValue(grid.EntityId, out prior);

            bool cruising = false;
            double intensity = 0;
            double mps = 0;
            if (hadPrior && !isJumping && tuning.SignatureSupercruiseReferenceMps > 0)
            {
                double seconds = (now - prior.At).TotalSeconds;
                if (seconds > 0)
                {
                    mps = VRageMath.Vector3D.Distance(pos, prior.Pos) / seconds;
                    if (mps >= tuning.SignatureSupercruiseMinMps && mps <= tuning.SignatureSupercruiseMaxMps)
                    {
                        cruising = true;
                        intensity = mps / tuning.SignatureSupercruiseReferenceMps;
                    }
                }
            }

            ex.Observe(Factions.Core.SignatureSource.Supercruise, cruising ? intensity : 0, now);
            // The transition, not the state: only the sample where it starts.
            bool entry = cruising && hadPrior && !prior.WasSupercruising;
            if (entry) ex.Observe(Factions.Core.SignatureSource.SupercruiseEntry, 1.0, now);

            // The FSD calibration sample. The toggle is read before anything is computed, and the
            // block walk behind it runs at most once per interval per grid.
            DateTime fsdTraceAt = hadPrior ? prior.LastFsdTraceAt : default(DateTime);
            // steamId 0 is an unseated grid: there is no client to attribute an FSD sample to, and
            // the calibration trace exists to be read against a player's own flight.
            if (DebugTraces.Fuel && steamId != 0 && cruising
                && (entry || fsdTraceAt == default(DateTime)
                    || (now - fsdTraceAt).TotalSeconds >= FsdObservation.IntervalSeconds))
            {
                fsdTraceAt = now;
                LogFsdObserved(steamId, grid, tuning, mps / 1000.0, entry);
            }

            _gridMotion[grid.EntityId] = new MotionSample
            {
                GridEntityId = grid.EntityId,
                Pos = pos,
                At = now,
                WasSupercruising = cruising,
                LastFsdTraceAt = fsdTraceAt
            };
        }

        /// <summary>The FSD mod's drive blocks by subtype — vanilla and prototech, large and small
        /// grid. All four are <c>MyObjectBuilder_UpgradeModule</c> blocks.</summary>
        private static readonly HashSet<string> FsdDriveSubtypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "MyFSDriveLarge", "MyFSDriveSmall",
                "MyPrototechFSDriveLarge", "MyPrototechFSDriveSmall"
            };

        private const string UpgradeModuleTypeId = "MyObjectBuilder_UpgradeModule";

        /// <summary>One FSD_OBSERVED line: what a player's grid is drawing while the position-delta
        /// detector says it is in supercruise, beside <see cref="FuelBurn.FsdActivePowerMw"/> at the
        /// same mass and speed. This is the only calibration the drive half of the burn model can
        /// have — no NPC prefab carries an FSD block.
        ///
        /// <para>Two draws, because they answer different questions. <c>draw=</c> is the whole grid's
        /// required electricity and so includes everything else that is running; <c>fsddraw=</c> is
        /// the drives' own sinks summed, which is the number the formula is meant to reproduce.
        /// <c>ratio=</c> prefers the second and falls back to the first.</para>
        ///
        /// <para>Speed is the detector's own displacement over elapsed time, never
        /// <c>Physics.LinearVelocity</c>: the mod moves the grid with <c>SetWorldMatrix</c>, so
        /// physics velocity can read near zero for the whole cruise.</para>
        ///
        /// <para>A block that will not read becomes an <c>n/a</c> rather than an exception in the
        /// sampling pass.</para></summary>
        private void LogFsdObserved(ulong steamId, MyCubeGrid grid,
            Factions.Core.FactionTuning tuning, double speedKmPerS, bool entry)
        {
            long entityId = grid.EntityId;
            double massKg = grid.Physics != null ? grid.Physics.Mass : 0;

            double gridDrawMw = 0;
            bool gridDrawKnown = false;
            var systems = grid.GridSystems;
            if (systems != null && systems.ResourceDistributor != null)
            {
                gridDrawMw = systems.ResourceDistributor.TotalRequiredInputByType(
                    Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId, grid);
                gridDrawKnown = true;
            }

            int drives = -1;
            double fsdDrawMw = 0;
            bool fsdDrawKnown = false;
            try
            {
                int found = 0;
                double sinkMw = 0;
                bool anySink = false;
                foreach (var block in grid.GetFatBlocks())
                {
                    if (block == null || block.BlockDefinition == null) continue;
                    if (block.BlockDefinition.Id.TypeId.ToString() != UpgradeModuleTypeId) continue;
                    if (!FsdDriveSubtypes.Contains(block.BlockDefinition.Id.SubtypeName)) continue;
                    found++;

                    // From the component container, not MyCubeBlock.ResourceSink: the mod attaches
                    // the drive's sink with Components.Add, which never writes the block's own
                    // m_sinkComp, so the property reads null on exactly these blocks.
                    var sink = block.Components.Get<Sandbox.Game.EntityComponents.MyResourceSinkComponent>();
                    if (sink == null) sink = block.ResourceSink;
                    if (sink == null) continue;

                    // AcceptedResources first: the sink indexes by resource type and a type it does
                    // not carry resolves to someone else's row rather than to an error.
                    foreach (var resource in sink.AcceptedResources)
                    {
                        if (resource != Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId)
                            continue;
                        sinkMw += sink.RequiredInputByType(resource);
                        anySink = true;
                        break;
                    }
                }
                drives = found;
                if (anySink) { fsdDrawMw = sinkMw; fsdDrawKnown = true; }
            }
            catch (Exception ex)
            {
                Log.Warn("FactionsPlugin: FSD block read for eid=" + entityId + " failed: " + ex.Message);
            }

            if (entry)
                Log.Info(FsdObservation.FormatEntry(entityId, steamId, massKg,
                    FuelBurn.FsdChargeMwh(tuning, massKg), gridDrawMw, gridDrawKnown));

            Log.Info(FsdObservation.Format(entityId, steamId, massKg, speedKmPerS,
                gridDrawMw, gridDrawKnown, fsdDrawMw, fsdDrawKnown,
                FuelBurn.FsdActivePowerMw(tuning, massKg, speedKmPerS),
                drives, grid.GridSizeEnum == MyCubeSize.Large));
        }

        /// <summary>The player-controlled grids a faction's sensors can find, measured off the live
        /// grid rather than looked up: a player ship has no prefab, so every catalogue path used for
        /// NPC fleets is unavailable here.
        ///
        /// <para><c>Physics.Mass</c> is the real current mass, cargo included, which is why no
        /// separate cargo term is passed — doing both would count the load twice.</para>
        ///
        /// <para>A player not seated at a ship controller is not a contact. That mirrors the scanner's
        /// own rule that out of the seat is out of the sensor, and it keeps this slice to grids;
        /// detecting a character on foot is a separate question.</para></summary>
        private List<Factions.Core.PlayerContact> BuildPlayerContacts()
        {
            var contacts = new List<Factions.Core.PlayerContact>();
            if (MySession.Static == null) return contacts;

            // Every grid the sampler resolved a signature for, which since FAC-463 is every
            // player-owned grid above the size floor -- seated or not, its owner online or not.
            // A hull radiates whether or not anyone is in the chair.
            foreach (KeyValuePair<long, double> pair in _gridSignature)
            {
                var grid = Sandbox.Game.Entities.MyEntities.GetEntityById(pair.Key) as MyCubeGrid;
                if (grid == null || grid.MarkedForClose || grid.Physics == null) continue;

                long identityId = OwnerIdentityOf(grid);
                if (identityId == 0) continue;   // nobody to attribute it to; not a player contact

                VRageMath.Vector3D pos = grid.PositionComp.GetPosition();
                double broadcastKm;
                if (!_gridBroadcastKm.TryGetValue(pair.Key, out broadcastKm)) broadcastKm = 0;

                contacts.Add(new Factions.Core.PlayerContact
                {
                    IdentityId = identityId,
                    GridEntityId = pair.Key,
                    PositionKm = new Factions.Core.Vec3(pos.X / 1000.0, pos.Y / 1000.0, pos.Z / 1000.0),
                    Signature = pair.Value,
                    BroadcastRadiusKm = broadcastKm
                });
            }

            // Authority: each agent's grids, as it last sampled them. They are in the same world
            // coordinates, and the factions sensing them have their estate in this world model.
            DateTime nowUtc = DateTime.UtcNow;
            foreach (KeyValuePair<byte, KeyValuePair<List<NexusPlayerContact>, DateTime>> kv in _agentContacts)
            {
                if (nowUtc - kv.Value.Value > AgentContactsMaxAge) continue;
                List<NexusPlayerContact> list = kv.Value.Key;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].Identity != 0) contacts.Add(list[i].ToContact());
            }
            return contacts;
        }

        /// <summary>Authority: each agent's latest player contacts and when they arrived.</summary>
        private readonly Dictionary<byte, KeyValuePair<List<NexusPlayerContact>, DateTime>> _agentContacts =
            new Dictionary<byte, KeyValuePair<List<NexusPlayerContact>, DateTime>>();

        /// <summary>An agent samples every <see cref="GpsUpdateTicks"/>; its contacts older than this
        /// are its server gone quiet, not ships standing still.</summary>
        private static readonly TimeSpan AgentContactsMaxAge = TimeSpan.FromSeconds(60);

        /// <summary>Agent: set by each sample, cleared when a report carries it.</summary>
        private bool _agentContactsSampled;

        /// <summary>Agent: the grids sampled here, for the next report; null when nothing was sampled
        /// since the last one.</summary>
        private List<NexusPlayerContact> ReadAgentContacts()
        {
            if (!_agentContactsSampled) return null;
            _agentContactsSampled = false;
            List<Factions.Core.PlayerContact> local = BuildPlayerContacts();
            var list = new List<NexusPlayerContact>(local.Count);
            for (int i = 0; i < local.Count; i++) list.Add(NexusPlayerContact.From(local[i]));
            return list;
        }

        /// <summary>Authority: each agent's player-owned grids, for the assets window.</summary>
        private readonly NexusOwnedGridBook _agentOwnedGrids = new NexusOwnedGridBook();

        /// <summary>Four missed owned-grid reports and an agent's grids drop out of the assets window.</summary>
        private static readonly TimeSpan AgentOwnedGridsMaxAge = TimeSpan.FromSeconds(4 * OwnedGridsReportEvery);

        /// <summary>Agent: the owned-grid list rides one report in this many — once every ~30 s, the
        /// assets window's own cadence. The grids change far slower than positions do.</summary>
        private const int OwnedGridsReportEvery = 30;

        private int _ownedGridsReportTick;

        /// <summary>Agent: the player-owned grids standing here, for the authority's assets window;
        /// null on the reports between. Read from the sampler's owned-grid list, so it costs no
        /// entity sweep of its own.</summary>
        private List<NexusOwnedGrid> ReadAgentOwnedGrids()
        {
            if (_ownedGridsReportTick++ % OwnedGridsReportEvery != 0) return null;
            MyPlayerCollection players = MySession.Static != null ? MySession.Static.Players : null;
            if (players == null) return null;
            var list = new List<NexusOwnedGrid>(_ownedGrids.Count);
            for (int i = 0; i < _ownedGrids.Count; i++)
            {
                MyCubeGrid grid = _ownedGrids[i];
                if (grid == null || grid.MarkedForClose) continue;
                long owner = OwnerIdentityOf(grid);
                if (owner == 0) continue;
                MyIdentity identity = players.TryGetIdentity(owner);
                VRageMath.Vector3D pos = grid.PositionComp != null ? grid.PositionComp.GetPosition() : VRageMath.Vector3D.Zero;
                list.Add(new NexusOwnedGrid
                {
                    Id = grid.EntityId, Name = grid.DisplayName, Static = grid.IsStatic,
                    Size = (byte)grid.GridSizeEnum, Blocks = grid.BlocksCount,
                    X = Math.Round(pos.X), Y = Math.Round(pos.Y), Z = Math.Round(pos.Z),
                    Owner = owner, OwnerName = identity != null ? identity.DisplayName : null,
                });
            }
            return list;
        }

        /// <summary>The non-NPC identity a grid is attributed to, or 0. Big owners first: a hull with
        /// a player majority owner belongs to them whoever built the odd block.</summary>
        private static long OwnerIdentityOf(MyCubeGrid grid)
        {
            MyPlayerCollection players = MySession.Static != null ? MySession.Static.Players : null;
            if (players == null) return 0;
            long id = FirstPlayerIdentity(players, grid.BigOwners);
            return id != 0 ? id : FirstPlayerIdentity(players, grid.SmallOwners);
        }

        private static long FirstPlayerIdentity(MyPlayerCollection players, List<long> owners)
        {
            if (owners == null) return 0;
            for (int i = 0; i < owners.Count; i++)
            {
                long id = owners[i];
                if (id != 0 && !players.IdentityIsNpc(id)) return id;
            }
            return 0;
        }

        /// <summary>Hands the seated real pilots, with the signature this same pass just sampled, to
        /// <see cref="TrespassHailer"/>. A grid the sampler has no figure for (below its size floor,
        /// or not player-owned) goes in at 0, which detection reads as the reference signature.</summary>
        private void TickTrespassHails()
        {
            if (_trespassHailer == null) return;
            CachedPlayer[] players = _cachedPlayers;
            var pilots = new List<TrespassHailer.Pilot>(players.Length);
            var online = new HashSet<long>();
            DateTime wallNow = DateTime.UtcNow;
            foreach (CachedPlayer p in players)
            {
                if (p.Phantom || p.IdentityId == 0) continue;
                online.Add(p.IdentityId);
                MyCubeGrid grid = p.Seat;
                if (grid == null || grid.MarkedForClose) continue;
                long eid = grid.EntityId;
                double sig, bcastKm;
                if (!_gridSignature.TryGetValue(eid, out sig)) sig = 0;
                if (!_gridBroadcastKm.TryGetValue(eid, out bcastKm)) bcastKm = 0;
                VRageMath.Vector3D pos = grid.PositionComp.GetPosition();
                var posKm = new Factions.Core.Vec3(pos.X / 1000.0, pos.Y / 1000.0, pos.Z / 1000.0);
                double physicsMs = grid.Physics != null ? grid.Physics.LinearVelocity.Length() : 0;
                double travelMs = _trespassTravel.Observe(p.IdentityId, eid, posKm, wallNow);
                pilots.Add(new TrespassHailer.Pilot
                {
                    IdentityId = p.IdentityId,
                    SteamId = p.SteamId,
                    GridEntityId = eid,
                    PositionKm = posKm,
                    Signature = sig,
                    BroadcastRadiusKm = bcastKm,
                    SpeedMs = Factions.Core.TravelSpeedBook.Effective(physicsMs, travelMs),
                    Docked = IsConnectorDocked(grid)
                });
            }
            AddRemotePilots(pilots, online, wallNow);
            _trespassTravel.Prune(online);
            _trespassHailer.Tick(pilots, online);
        }

        /// <summary>The seated players on the agents, from their reports: the seat grid's position, its
        /// signature and broadcast from the agent's own contacts, whether it is docked. Under this
        /// server's identity for their Steam id, so their standing is read where this server holds it.</summary>
        private void AddRemotePilots(List<TrespassHailer.Pilot> pilots, HashSet<long> online, DateTime wallNow)
        {
            IList<RemotePlayer> remote = RemotePlayersNow();
            for (int i = 0; i < remote.Count; i++)
            {
                RemotePlayer r = remote[i];
                long identity = r.SteamId != 0 && MySession.Static != null ? MySession.Static.Players.TryGetIdentityId(r.SteamId) : 0;
                if (identity == 0) identity = r.IdentityId;
                if (identity == 0) continue;
                online.Add(identity);
                if (r.SeatGridId == 0) continue;
                var posKm = new Factions.Core.Vec3(r.GridPositionM.X / 1000.0, r.GridPositionM.Y / 1000.0, r.GridPositionM.Z / 1000.0);
                double sig = 0, bcastKm = 0;
                KeyValuePair<List<NexusPlayerContact>, DateTime> contacts;
                if (_agentContacts.TryGetValue(r.HomeServerId, out contacts) && contacts.Key != null)
                    foreach (NexusPlayerContact c in contacts.Key)
                        if (c != null && c.Grid == r.SeatGridId) { sig = c.Signature; bcastKm = c.BroadcastKm; break; }
                HashSet<long> docked;
                Factions.Core.Vec3 v = r.Velocity;
                double physicsMs = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                double travelMs = _trespassTravel.Observe(identity, r.SeatGridId, posKm, wallNow);
                pilots.Add(new TrespassHailer.Pilot
                {
                    IdentityId = identity,
                    SteamId = r.SteamId,
                    GridEntityId = r.SeatGridId,
                    PositionKm = posKm,
                    Signature = sig,
                    BroadcastRadiusKm = bcastKm,
                    SpeedMs = Factions.Core.TravelSpeedBook.Effective(physicsMs, travelMs),
                    Docked = _agentDocked.TryGetValue(r.HomeServerId, out docked) && docked.Contains(r.SeatGridId),
                    Server = r.HomeServerId,
                });
            }
        }

        /// <summary>Whether any connector on <paramref name="grid"/> is locked to another grid.</summary>
        private static bool IsConnectorDocked(MyCubeGrid grid)
        {
            foreach (Sandbox.Game.Entities.Cube.MyShipConnector c in grid.GetFatBlocks<Sandbox.Game.Entities.Cube.MyShipConnector>())
                if (c != null && !c.Closed && c.Connected) return true;
            return false;
        }

        private void RefreshPlayerPositionCache()
        {
            try
            {
                if (MySession.Static == null) { _cachedPlayers = new CachedPlayer[0]; return; }
                var online = MySession.Static.Players.GetOnlinePlayers();
                var buf = new List<CachedPlayer>(online.Count);
                foreach (var player in online)
                {
                    if (player.IsBot) continue;
                    var ch = player.Character;
                    if (ch == null) continue;
                    var moving = player.Controller?.ControlledEntity?.Entity?.GetTopMostParent()?.Physics ?? ch.Physics;
                    var seat = player.Controller?.ControlledEntity as Sandbox.Game.Entities.MyShipController;
                    buf.Add(new CachedPlayer { Pos = ch.PositionComp.GetPosition(), SteamId = player.Id.SteamId,
                                               Vel = moving != null ? moving.LinearVelocity : VRageMath.Vector3.Zero,
                                               Seat = seat?.CubeGrid, IdentityId = player.Identity != null ? player.Identity.IdentityId : 0 });
                }
                for (int i = 0; i < BenchPhantoms.Count; i++)
                {
                    VRageMath.Vector3D pos; VRageMath.Vector3 vel; long identityId; ulong steamId;
                    BenchPhantoms.Presence(i, out pos, out vel, out identityId, out steamId);
                    buf.Add(new CachedPlayer { Pos = pos, SteamId = steamId, Vel = vel, IdentityId = identityId, Phantom = true });
                }
                _cachedPlayers = buf.ToArray();
            }
            catch { _cachedPlayers = new CachedPlayer[0]; }
        }

        /// <summary>Returns true if any online player's character is within <paramref name="radiusKm"/>
        /// of <paramref name="posKm"/>. Uses the per-frame cached positions — call
        /// RefreshPlayerPositionCache() once per Update() before using this. Guards against SE API
        /// errors by relying on the cached array (read-only after cache, no try/catch needed here).
        /// Not called in dry-run because NullFleetSpawner sets SkipsTransit=true.</summary>
        private bool AnyPlayerWithinKm(Factions.Core.Vec3 posKm, double radiusKm)
        {
            CachedPlayer[] players = _cachedPlayers;
            double r2 = radiusKm * radiusKm;
            foreach (CachedPlayer p in players)
            {
                double dx = p.Pos.X / 1000.0 - posKm.X;
                double dy = p.Pos.Y / 1000.0 - posKm.Y;
                double dz = p.Pos.Z / 1000.0 - posKm.Z;
                if (dx * dx + dy * dy + dz * dz < r2) return true;
            }
            return false;
        }

        /// <summary>SteamIds of every online player within <paramref name="radiusKm"/> of
        /// <paramref name="posKm"/> — for fleet-progress chat targeting (see
        /// FactionRuntime.SendNearbyFleetMessage). Additive alongside AnyPlayerWithinKm (bool-only;
        /// used by UpdateStationObservation/PruneExpiredUnknTerritory/UpdateAsteroidFieldObservation),
        /// not a replacement for it.</summary>
        /// <summary>Every online player's position in km — the observation scans are all built
        /// around the players rather than around the nodes, so they need the positions themselves
        /// rather than a proximity predicate.
        ///
        /// <para>Refreshed once per frame beside <see cref="_cachedPlayers"/> and handed out as the
        /// same list, because every per-frame consumer reads it and none of them keeps it. Callers
        /// outside the frame path (a bridge verb) get their own copy.</para></summary>
        private readonly List<Factions.Core.Vec3> _playerPositionsKm = new List<Factions.Core.Vec3>();

        private void RefreshPlayerPositionsKm()
        {
            _playerPositionsKm.Clear();
            CachedPlayer[] players = _cachedPlayers;
            foreach (CachedPlayer p in players)
                _playerPositionsKm.Add(new Factions.Core.Vec3(
                    p.Pos.X / 1000.0, p.Pos.Y / 1000.0, p.Pos.Z / 1000.0));
        }

        private List<Factions.Core.Vec3> PlayerPositionsKm()
        {
            var result = new List<Factions.Core.Vec3>(_cachedPlayers.Length);
            foreach (CachedPlayer p in _cachedPlayers)
                result.Add(new Factions.Core.Vec3(p.Pos.X / 1000.0, p.Pos.Y / 1000.0, p.Pos.Z / 1000.0));
            return result;
        }

        /// <summary>Every online player with where they are, how fast they are moving and the radio set
        /// they are listening on: the traffic voice decides per listener who hears what (FAC-419).</summary>
        private List<TrafficListener> TrafficListeners()
        {
            CachedPlayer[] players = _cachedPlayers;
            var result = new List<TrafficListener>(players.Length);
            foreach (CachedPlayer p in players)
                result.Add(new TrafficListener
                {
                    SteamId = p.SteamId,
                    PosKm = new Factions.Core.Vec3(p.Pos.X / 1000.0, p.Pos.Y / 1000.0, p.Pos.Z / 1000.0),
                    VelMs = new Factions.Core.Vec3(p.Vel.X, p.Vel.Y, p.Vel.Z),
                    Radio = RadioOf(p),
                });
            return result;
        }

        private sealed class CachedRadio
        {
            public long GridId; public DateTime Utc; public RadioReceiver Radio;
            public readonly List<Sandbox.Game.Entities.Cube.MyRadioAntenna> Dishes = new List<Sandbox.Game.Entities.Cube.MyRadioAntenna>();
        }
        private readonly Dictionary<ulong, CachedRadio> _radioCache = new Dictionary<ulong, CachedRadio>();
        private readonly List<AntennaReading> _radioAntennas = new List<AntennaReading>();

        /// <summary>A seated player's antennas, read at most every five seconds per ship: traffic
        /// comes in bursts, and a ship's fit does not change between two calls. Its reflectors' axes are
        /// read on every call, because a ship turns in far less than that.</summary>
        private RadioReceiver RadioOf(CachedPlayer p)
        {
            MyCubeGrid grid = p.Seat;
            if (grid == null || grid.Closed) return default(RadioReceiver);
            DateTime now = DateTime.UtcNow;
            CachedRadio cached;
            if (!_radioCache.TryGetValue(p.SteamId, out cached)) _radioCache[p.SteamId] = cached = new CachedRadio();
            if (cached.GridId != grid.EntityId || (now - cached.Utc).TotalSeconds >= 5)
            {
                ShipAntennas.Read(grid, p.IdentityId, _radioAntennas, cached.Dishes);
                cached.GridId = grid.EntityId;
                cached.Utc = now;
                cached.Radio = new RadioReceiver
                {
                    Seated = true,
                    AntennaCount = _radioAntennas.Count,
                    StrengthKm = ShipSensor.StrengthKm(_radioAntennas, _runtime != null ? _runtime.Tuning : _tuning),
                };
                _radioAntennas.Clear();
            }
            RadioReceiver radio = cached.Radio;
            List<RadioDish> dishes = null;
            for (int i = 0; i < cached.Dishes.Count; i++)
            {
                var antenna = cached.Dishes[i];
                RadioDishKind kind;
                if (antenna == null || antenna.Closed || !antenna.IsWorking || !ShipAntennas.TryDishKind(antenna, out kind)) continue;
                if (dishes == null) dishes = new List<RadioDish>();
                dishes.Add(ShipAntennas.DishOf(antenna, kind));
            }
            if (dishes != null) radio.Dishes = dishes.ToArray();
            return radio;
        }

        private static double TrafficBroadcastKm(long entityId)
        {
            MyCubeGrid grid = MyEntities.GetEntityById(entityId) as MyCubeGrid;
            return grid != null && !grid.Closed ? ShipAntennas.BroadcastKm(grid) : 0;
        }

        private IEnumerable<ulong> PlayersWithinKm(Factions.Core.Vec3 posKm, double radiusKm)
        {
            CachedPlayer[] players = _cachedPlayers;
            double r2 = radiusKm * radiusKm;
            var result = new List<ulong>();
            foreach (CachedPlayer p in players)
            {
                if (p.Phantom) continue;
                double dx = p.Pos.X / 1000.0 - posKm.X;
                double dy = p.Pos.Y / 1000.0 - posKm.Y;
                double dz = p.Pos.Z / 1000.0 - posKm.Z;
                if (dx * dx + dy * dy + dz * dz < r2) result.Add(p.SteamId);
            }
            return result;
        }

        // ---- Frame-gap probe ---------------------------------------------------------------
        //
        // /api/perf and the FrameTimingWindow buckets measure time spent INSIDE Update(). A stall
        // BETWEEN two Update() calls -- Torch/SE engine work, another plugin, a synchronous load, a
        // blocking collection -- is invisible to every one of them, because no plugin code runs
        // during it. This measures exactly that, and reports it beside what this plugin spent in
        // the same frame so the two can be told apart:
        //
        //   TICK_STALL gap=2100.0ms ours=0.8ms   -> 2099 ms of it was not us.
        //
        // Deliberately samples every frame including the deferred-simulation early return, because
        // a stall while the sim is paused is still a stall and would otherwise never be seen.
        internal static bool DebugFrameGap;

        /// <summary>A frame gap past this is a visible hitch rather than a busy frame.</summary>
        private const double FrameGapWarnMs = 250.0;

        private long _lastUpdateEntry;
        private long _lastFrameOwnTicks;

        // ---- server-thread telemetry (see ServerPerfMonitor) ----------------------------------
        private ServerPerfMonitor _serverPerf;
        /// <summary>Rebuilt each census rather than kept in step with spawns and despawns: a stale
        /// reverse index would attribute a despawned fleet's grid to whatever reused its id.</summary>
        private readonly HashSet<long> _serverPerfFleetIds = new HashSet<long>();
        /// <summary>Cached so the per-window census allocates no closures.</summary>
        private Func<long, bool> _isFlyingGrid;
        private Func<long, bool> _isStationGrid;
        private Func<VRageMath.Vector3D, bool> _isNearAnyPlayer;

        /// <summary>Reads <c>server_perf.json</c>, binds the physics hooks and starts the monitor.
        ///
        /// <para>Called from <see cref="Init"/> rather than at session load: the frame gap is worth
        /// measuring across a world load, and a config read must not wait on one. Every failure
        /// short of a disabled config leaves the monitor null and the plugin otherwise untouched —
        /// telemetry does not get to stop the server coming up.</para></summary>
        /// <summary>The prefab pool's performance ceiling, read from server_perf.json at Init and
        /// applied once the catalogue is complete at session load. Independent of whether
        /// telemetry is enabled: it gates what Factions builds, not what it measures.</summary>
        private PrefabCeiling _prefabCeiling = new PrefabCeiling();

        /// <summary>Blocks in a prefab over every grid it spawns, from the catalogue once the session
        /// has loaded; null before. Traffic control prices a fleet with it before the fleet exists.</summary>
        private Func<string, int> _prefabBlocks;
        private readonly List<PendingFleetAction> _materialisedScratch = new List<PendingFleetAction>();

        /// <summary>Traffic control, slice 1 (docs/traffic-control.md): a spawn is starting; ask the
        /// budget whether it would have fitted. Returns the decision-log line when that trace is on.</summary>
        private static string ProbeSpawnAdmission(ServerPerfMonitor perf, Func<string, int> blocksOf, PrefabCeiling law,
                                                  PendingFleetAction fleet, string key, DateTime now)
        {
            int unknown;
            double baseMs = FleetLoadCost.BaseMs(fleet, blocksOf, law, TrafficBudget.UnknownHullMs, out unknown);
            TrafficClass cls = FleetLoadCost.ClassOf(fleet.Kind);
            TrafficBudget.Decision v = perf.ProbeSpawn(key, cls, baseMs, unknown, FleetLoadCost.Blocks(fleet, blocksOf), now);
            if (!DebugTraces.Admission) return null;
            return "TRAFFIC_ADMISSION " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind + " class=" + cls
                   + " base=" + baseMs.ToString("F3", CultureInfo.InvariantCulture) + "ms"
                   + " predicted=" + v.PredictedMs.ToString("F3", CultureInfo.InvariantCulture) + "ms"
                   + " headroom=" + (double.IsNaN(v.HeadroomMs) ? "unmeasured" : v.HeadroomMs.ToString("F2", CultureInfo.InvariantCulture) + "ms")
                   + " would=" + (v.WouldAdmit ? "admit" : "refuse")
                   + (unknown > 0 ? " unknownHulls=" + unknown : "");
        }

        private readonly List<TrafficPresence.Player> _presencePlayers = new List<TrafficPresence.Player>();
        private readonly List<TrafficPresence.Fleet> _presenceFleets = new List<TrafficPresence.Fleet>();

        /// <summary>Traffic control's presence plan for this frame's candidates: the online players
        /// with their governor steps, the Factions budget, and each fleet's calibrated cost. Null
        /// before the first telemetry window, when there is no budget to divide yet.</summary>
        private TrafficPresence.Plan PlanPresence(ServerPerfMonitor perf, List<FactionRuntime.PresenceCandidate> candidates,
                                                  IDictionary<string, Factions.Core.Vec3> stations, StationQueues queues, DateTime now)
        {
            double budget = perf.Traffic.FactionsBudgetMs;
            if (double.IsNaN(budget) || MySession.Static == null) return null;

            _presencePlayers.Clear();
            foreach (CachedPlayer p in _cachedPlayers)
            {
                MyIdentity identity = p.IdentityId != 0 ? MySession.Static.Players.TryGetIdentity(p.IdentityId) : null;
                string party = identity != null ? identity.DisplayName : null;
                _presencePlayers.Add(new TrafficPresence.Player
                {
                    Party = party ?? "",
                    PosKm = new Factions.Core.Vec3(p.Pos.X / 1000.0, p.Pos.Y / 1000.0, p.Pos.Z / 1000.0),
                    GovernorStep = party != null ? perf.GovernorPlayerStep(party) : 0,
                });
            }

            // An agent's players are planned against that agent's own budget: its frame, not this
            // one, pays for the grids flown there. Without them a fleet near one has no player near
            // it here and is refused.
            _presenceServers.Clear();
            if (_nexusRole == NexusRole.Authority)
            {
                IList<RemotePlayer> remote = RemotePlayersNow();
                for (int i = 0; i < remote.Count; i++)
                {
                    RemotePlayer r = remote[i];
                    if (!_fleetRegistry.CanFly(r.HomeServerId, now)) continue;
                    _presencePlayers.Add(new TrafficPresence.Player
                    {
                        Party = r.SteamId.ToString(CultureInfo.InvariantCulture),
                        PosKm = new Factions.Core.Vec3(r.PositionM.X / 1000.0, r.PositionM.Y / 1000.0, r.PositionM.Z / 1000.0),
                        Server = r.HomeServerId,
                    });
                }
                foreach (KeyValuePair<byte, KeyValuePair<NexusAgentLoad, DateTime>> kv in _agentLoads)
                {
                    if (now - kv.Value.Value > AgentLoadMaxAge) continue;
                    NexusAgentLoad load = kv.Value.Key;
                    _presenceServers[kv.Key] = new TrafficPresence.ServerLoad
                    {
                        BudgetMs = load.BudgetMs, KCivilian = load.KCivilian, KCombat = load.KCombat,
                    };
                }
            }

            _presenceFleets.Clear();
            foreach (FactionRuntime.PresenceCandidate c in candidates)
            {
                int unknown;
                TrafficClass cls = FleetLoadCost.ClassOf(c.Fleet.Kind);
                double baseMs = FleetLoadCost.BaseMs(c.Fleet, _prefabBlocks, _prefabCeiling, TrafficBudget.UnknownHullMs, out unknown);
                _presenceFleets.Add(new TrafficPresence.Fleet
                {
                    Key = c.Fleet.FleetId,
                    PosKm = c.PosKm,
                    CostMs = baseMs * perf.Traffic.Multiplier(cls),
                    BaseMs = baseMs,
                    CombatClass = cls == TrafficClass.Combat,
                    Civilian = FactionsLoadActuator.IsTraffic(c.Fleet.Kind),
                    Materialised = c.Materialised,
                    Sheddable = c.Sheddable,
                    QueueStation = c.QueueStation,
                    QueueDeparture = c.QueueDeparture,
                    Divertable = c.Divertable,
                    Inbound = c.Inbound,
                });
            }

            return TrafficPresence.Compute(_presencePlayers, _presenceFleets, budget,
                new TrafficPresence.Settings
                {
                    CeilingRadiusKm = _tuning.ObservationRadiusKm,
                    Closed = _runtime != null && _runtime.World != null ? _runtime.World.ClosedForCombat : null,
                }, stations, queues, now, _presenceServers);
        }

        private readonly NexusTransferInbox _transferInbox = new NexusTransferInbox();
        private readonly NexusStoreInbox _storeInbox = new NexusStoreInbox();
        private readonly NexusShelfSchedule _shelfSchedule = new NexusShelfSchedule();
        private static readonly TimeSpan ShelfRefresh = TimeSpan.FromSeconds(60);

        /// <summary>Authority: files an agent's DecisionLog lines once each, under the time the agent
        /// wrote them, tagged with the server that wrote them.</summary>
        private void FileAgentLog(byte home, NexusAgentReport report, NexusStationReply reply)
        {
            long through;
            List<NexusLogLine> fresh = _logInbox.Accept(home, report.Session, report.Log, out through);
            reply.LogThrough = through;
            if (_decisionLog == null) return;
            string tag = " server=" + home;
            for (int i = 0; i < fresh.Count; i++)
            {
                NexusLogLine l = fresh[i];
                if (l.Text == null) continue;
                DateTime at = l.At == default(DateTime) ? DateTime.Now : l.At.ToLocalTime();
                _decisionLog.WriteAt(at, l.Text + tag);
            }
        }

        /// <summary>Authority: books what players did at an agent's Factions stations, once each,
        /// through the same store manager calls this server's own stations make.</summary>
        /// <summary>Authority: a truce offer or withdrawal a player made on an agent's Factions screen,
        /// judged there (<see cref="FactionStateChangeCheckPatch"/>).</summary>
        private void BookAgentPeaceRequest(byte home, NexusStoreEvent e)
        {
            FactionState player = _runtime.FindFaction(e.Seller);
            FactionState npc = _runtime.FindFaction(e.Key);
            if (player == null || !player.IsPlayerFaction || npc == null || npc.IsPlayerFaction)
            {
                Log.Warn("NEXUS_STORE_EVENT_UNBOOKED server=" + home + " kind=Diplomacy player=" + (e.Seller ?? "-")
                         + " npc=" + (e.Key ?? "-") + " (not a registered player faction and NPC)");
                return;
            }
            if (e.Buy)
            {
                Diplomacy.OfferPeace(player, npc.Tag, _runtime.World.UtcNow);
                FactionDiplomacyPatch.LogPeaceOffered(player, npc, "server" + home, _runtime.World.Tuning);
                return;
            }
            bool withdrawn = VanillaDiplomacy.WithdrawPeace(player, npc.Tag);
            Log.Info("DIPLOMACY_PEACE_WITHDRAWN player=" + player.Tag + " target=" + npc.Tag
                     + " offer=" + (withdrawn ? "removed" : "none") + " server=" + home);
        }

        /// <summary>Authority: a member's conduct on an agent, into the member reputation book as if
        /// SE had reported it here.</summary>
        private void BookAgentConduct(byte home, NexusStoreEvent e, long identityId)
        {
            MySession session = MySession.Static;
            MyFaction target = session != null && session.Factions != null ? session.Factions.TryGetFactionByTag(e.Key) : null;
            if (_reputationManager == null || target == null || identityId == 0)
            {
                Log.Warn("NEXUS_STORE_EVENT_UNBOOKED server=" + home + " kind=Conduct npc=" + (e.Key ?? "-")
                         + " identity=" + identityId + " (no reputation book, faction or identity)");
                return;
            }
            _reputationManager.OnConductAdded(identityId, target.FactionId, (int)e.Amount,
                (VRage.Game.ModAPI.ReputationChangeReason)e.Blocks, e.Buy);
        }

        /// <summary>Authority: hulls held for withdrawals staged on agents.</summary>
        private readonly NexusHullHolds _hullHolds = new NexusHullHolds();

        /// <summary>Authority: holds one hull of a node's stock for a withdrawal an agent's player
        /// started, before the agent stages it, and answers the hold or the refusal.</summary>
        private void OnHullAsk(byte home, ulong steamId, byte[] payload)
        {
            NexusHullAsk ask;
            if (!NexusHullAsk.TryDecode(payload, out ask))
            {
                Log.Warn("NEXUS_HULL_ASK_UNREADABLE home=" + home + " bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            WorldModel world = _runtime != null ? _runtime.World : null;
            NexusHullAnswer answer = world == null
                ? new NexusHullAnswer { Id = ask.Id, Node = ask.Node, Prefab = ask.Prefab, Message = "Factions runtime not ready." }
                : _hullHolds.Take(ask, home, world.Graph, DateTime.UtcNow);
            if (answer.Ok)
            {
                Log.Info("SHIP_WITHDRAW_HELD node=" + ask.Node + " server=" + home + " player=" + ask.Identity
                         + " prefab=" + ask.Prefab + " hold=" + answer.Hold + " remaining=" + answer.Remaining);
                _shelfSchedule.MarkDirty(ask.Node);
            }
            else
                Log.Info("SHIP_WITHDRAW_REFUSED node=" + (ask.Node ?? "") + " server=" + home + " player=" + ask.Identity
                         + " prefab=" + (ask.Prefab ?? "") + " reason=" + answer.Message);
            if (_nexusRelay != null)
                _nexusRelay.TrySend(home, Factions.Wire.NexusRelayWire.Write(Factions.Wire.RelayKind.HullAnswer,
                    (byte)_nexusServerId, steamId, answer.Encode()));
        }

        /// <summary>Authority: returns the holds no agent settled in time to their stock.</summary>
        private void LapseHullHolds(WorldModel world)
        {
            if (_hullHolds.Count == 0 || world == null) return;
            List<NexusHullHolds.Hold> lapsed = _hullHolds.Lapse(world.Graph, DateTime.UtcNow);
            if (lapsed == null) return;
            for (int i = 0; i < lapsed.Count; i++)
            {
                Log.Warn("SHIP_WITHDRAW_HOLD_LAPSED node=" + lapsed[i].Node + " server=" + lapsed[i].Home
                         + " prefab=" + lapsed[i].Prefab + " hold=" + lapsed[i].Id);
                _shelfSchedule.MarkDirty(lapsed[i].Node);
            }
        }

        private void BookAgentStoreEvents(byte home, NexusAgentReport report, NexusStationReply reply)
        {
            long through;
            List<NexusStoreEvent> fresh = _storeInbox.Accept(home, report.Session, report.StoreEvents, out through);
            reply.StoreThrough = through;
            WorldModel world = _runtime != null ? _runtime.World : null;
            if (world == null || _storeManager == null) return;
            LapseHullHolds(world);
            for (int i = 0; i < fresh.Count; i++)
            {
                NexusStoreEvent e = fresh[i];
                long buyer = LocalIdentityOf(e.Steam, e.Buyer);
                try
                {
                    if (e.Kind == NexusStoreEventKind.Diplomacy)
                    {
                        BookAgentPeaceRequest(home, e);
                        continue;
                    }
                    if (e.Kind == NexusStoreEventKind.Conduct)
                    {
                        BookAgentConduct(home, e, buyer);
                        continue;
                    }
                    if (e.Kind == NexusStoreEventKind.Ship)
                    {
                        ShipSaleDecrement d = _storeManager.OnPlayerBoughtShip(e.Seller, e.Prefab);
                        Log.Info("SHIP_SOLD prefab=" + e.Prefab + " server=" + home + " buyer=" + buyer + " seller=" + e.Seller
                                 + (d.Sold
                                     ? " node=" + d.NodeId + " listed=" + d.ListedBefore + "->" + d.ListedAfter
                                       + " stock=" + d.StockBefore + "->" + d.StockAfter
                                     : " listed=none"));
                        if (d.Sold) _shelfSchedule.MarkDirty(d.NodeId);
                        continue;
                    }
                    SpatialNode node;
                    if (e.Node == null || !world.Graph.Nodes.TryGetValue(e.Node, out node))
                    {
                        Log.Warn("NEXUS_STORE_EVENT_UNBOOKED server=" + home + " kind=" + e.Kind + " node=" + (e.Node ?? "-") + " (no such node)");
                        continue;
                    }
                    switch (e.Kind)
                    {
                        case NexusStoreEventKind.Trade:
                            _storeManager.HandleNodeTransaction(node, e.Key, (int)Math.Min(e.Amount, int.MaxValue), e.Buy, e.Credits, buyer);
                            break;
                        case NexusStoreEventKind.Salvage:
                            _storeManager.HandleSalvageSettled(node, buyer, e.Credits, e.Blocks, e.Parts, e.Cargo);
                            break;
                        case NexusStoreEventKind.Repair:
                            _storeManager.HandleRepairSettled(node, buyer, e.Credits, e.Parts);
                            break;
                        case NexusStoreEventKind.Expedite:
                            _storeManager.HandleExpediteSettled(node, buyer, e.Credits);
                            break;
                        case NexusStoreEventKind.Debris:
                            if (_debrisAbsorbManager != null)
                                _debrisAbsorbManager.CreditRemote(node, home, e.Grids, e.Blocks, e.Parts, e.Cargo);
                            break;
                        case NexusStoreEventKind.Stock:
                            if (e.Key != null && e.Amount > 0) NodeTransfer.CreditNode(node, e.Key, e.Amount);
                            break;
                        case NexusStoreEventKind.Supply:
                            _storeManager.HandleDockSupplySettled(node, buyer, e.Seller ?? "supply", e.Parts, e.Credits);
                            break;
                        case NexusStoreEventKind.Hull:
                            BookAgentHull(home, node, e, buyer);
                            break;
                    }
                    _shelfSchedule.MarkDirty(node.Id);
                }
                catch (Exception ex) { Log.Warn(ex, "Booking a store event from server " + home + " failed."); }
            }
        }

        /// <summary>Authority: a hull an agent's player withdrew from a node into grid storage, or sent
        /// into it with its cargo. The agent checked the stock against its snapshot; a withdrawal the
        /// stock no longer covers is logged and leaves the stock at zero.</summary>
        private void BookAgentHull(byte home, SpatialNode node, NexusStoreEvent e, long buyer)
        {
            if (string.IsNullOrEmpty(e.Prefab)) return;
            int stock;
            if (e.Hold != 0)
            {
                NexusHullSettled settled = _hullHolds.Settle(e.Hold, e.Buy, _runtime.World.Graph, node.Id, e.Prefab);
                node.ShipStock.TryGetValue(e.Prefab, out stock);
                if (settled == NexusHullSettled.Lapsed && e.Buy)
                {
                    Log.Warn("SHIP_WITHDRAW_HOLD_LATE node=" + node.Id + " server=" + home + " player=" + buyer
                             + " prefab=" + e.Prefab + " hold=" + e.Hold);
                }
                else
                {
                    Log.Info((e.Buy ? "SHIP_WITHDRAWN" : "SHIP_WITHDRAW_RELEASED") + " node=" + node.Id
                             + " faction=" + (node.OwnerFactionTag ?? "") + " server=" + home + " player=" + buyer
                             + " prefab=" + e.Prefab + " hold=" + e.Hold + " settled=" + settled + " remaining=" + stock);
                    if (_priceUpdater != null && e.Buy) _priceUpdater.RecordActivity(node.Id);
                    return;
                }
            }
            node.ShipStock.TryGetValue(e.Prefab, out stock);
            if (e.Buy)
            {
                if (stock <= 0)
                {
                    Log.Warn("SHIP_WITHDRAW_OVERDRAWN node=" + node.Id + " server=" + home + " player=" + buyer
                             + " prefab=" + e.Prefab + " stock=0");
                    return;
                }
                if (stock > 1) node.ShipStock[e.Prefab] = stock - 1;
                else node.ShipStock.Remove(e.Prefab);
                Log.Info("SHIP_WITHDRAWN node=" + node.Id + " faction=" + (node.OwnerFactionTag ?? "") + " server=" + home
                         + " player=" + buyer + " prefab=" + e.Prefab + " remaining=" + (stock - 1));
            }
            else
            {
                long cargoUnits = 0;
                if (e.Parts != null)
                    foreach (KeyValuePair<string, long> kv in e.Parts)
                    {
                        if (kv.Value <= 0) continue;
                        NodeTransfer.CreditNode(node, kv.Key, kv.Value);
                        cargoUnits += kv.Value;
                    }
                node.ShipStock[e.Prefab] = stock + 1;
                Log.Info("SHIP_SENT node=" + node.Id + " faction=" + (node.OwnerFactionTag ?? "") + " server=" + home
                         + " player=" + buyer + " prefab=" + e.Prefab + " cargokeys=" + (e.Parts == null ? 0 : e.Parts.Count)
                         + " cargounits=" + cargoUnits);
            }
            if (_priceUpdater != null) _priceUpdater.RecordActivity(node.Id);
        }

        /// <summary>This server's identity for a player an agent names: by Steam id, which every shard
        /// shares, else the agent's own identity id.</summary>
        private static long LocalIdentityOf(ulong steamId, long reported)
        {
            if (steamId != 0 && MySession.Static != null && MySession.Static.Players != null)
            {
                long local = MySession.Static.Players.TryGetIdentityId(steamId);
                if (local != 0) return local;
            }
            return reported;
        }

        /// <summary>Authority: the store contents of the agent's standing stations that are due.</summary>
        private void FillShelves(byte home, NexusAgentReport report, NexusStationReply reply)
        {
            if (_storeManager == null || _runtime == null || _runtime.World == null || report.Stations == null) return;
            var standing = new List<string>();
            for (int i = 0; i < report.Stations.Count; i++)
                if (report.Stations[i] != null && !report.Stations[i].Closed) standing.Add(report.Stations[i].Node);
            List<string> due = _shelfSchedule.Due(home, report.Session, standing, DateTime.UtcNow, ShelfRefresh);
            if (due.Count == 0) return;
            var shelves = new List<NexusShelf>(due.Count);
            for (int i = 0; i < due.Count; i++)
            {
                SpatialNode node;
                if (!_runtime.World.Graph.Nodes.TryGetValue(due[i], out node)) continue;
                try { shelves.Add(_storeManager.BuildShelf(node)); }
                catch (Exception ex) { Log.Warn(ex, "Shelf for " + due[i] + " failed."); }
            }
            if (shelves.Count > 0) reply.Shelves = shelves;
        }

        /// <summary>Authority: books an agent's faction-terminal transfers into the ledger, once each,
        /// as <see cref="FactionWalletTransferPatch"/> does for this server's own. Nexus has already
        /// moved the wallet here, so the treasury mirror's next push reads no drift.</summary>
        private void BookAgentTransfers(byte home, NexusAgentReport report, NexusStationReply reply)
        {
            long through;
            List<NexusWalletTransfer> fresh = _transferInbox.Accept(home, report.Session, report.Transfers, out through);
            reply.TransfersThrough = through;
            WorldModel world = _runtime != null ? _runtime.World : null;
            if (world == null) return;
            for (int i = 0; i < fresh.Count; i++)
            {
                NexusWalletTransfer t = fresh[i];
                if (t.From != null && _runtime.FindFaction(t.From) != null)
                {
                    world.Spend(t.From, t.Amount);
                    Log.Info("Faction ledger debited " + t.Amount + " for " + t.From + " (terminal withdrawal on server " + home + ")");
                }
                if (t.To != null && _runtime.FindFaction(t.To) != null)
                {
                    world.Earn(t.To, t.Amount);
                    Log.Info("Faction ledger credited " + t.Amount + " for " + t.To + " (terminal deposit on server " + home + ")");
                }
            }
        }

        /// <summary>Authority: each agent's latest traffic budget and when it arrived.</summary>
        private readonly Dictionary<byte, KeyValuePair<NexusAgentLoad, DateTime>> _agentLoads =
            new Dictionary<byte, KeyValuePair<NexusAgentLoad, DateTime>>();
        private readonly Dictionary<byte, TrafficPresence.ServerLoad> _presenceServers =
            new Dictionary<byte, TrafficPresence.ServerLoad>();

        /// <summary>An agent's budget older than this is not planned against: its server is planned
        /// as one that reported none. Reports come every second.</summary>
        private static readonly TimeSpan AgentLoadMaxAge = TimeSpan.FromSeconds(30);

        /// <summary>Agent: this server's traffic budget for the authority, from its own frame; null
        /// before the first measured window or with server perf off.</summary>
        private NexusAgentLoad ReadAgentLoad()
        {
            ServerPerfMonitor perf = _serverPerf;
            if (perf == null || !perf.Traffic.HasWindow) return null;
            double budget = perf.Traffic.FactionsBudgetMs;
            if (double.IsNaN(budget)) return null;
            return new NexusAgentLoad
            {
                BudgetMs = budget,
                KCivilian = perf.Traffic.CivilianMultiplier,
                KCombat = perf.Traffic.CombatMultiplier,
                FactionsStep = perf.GovernorFactionsActuate ? perf.GovernorFactionsStep : 0,
            };
        }

        private void ReadFleetLoads(FactionRuntime runtime, ServerPerfMonitor perf, List<ServerPerfMonitor.FleetLoad> into)
        {
            _materialisedScratch.Clear();
            runtime.CollectMaterialisedFleets(_materialisedScratch);
            foreach (PendingFleetAction f in _materialisedScratch)
            {
                int unknown;
                into.Add(new ServerPerfMonitor.FleetLoad
                {
                    LeadGridId = f.MaterialisedEntityId,
                    EscortGridIds = f.EscortEntityIds,
                    Class = FleetLoadCost.ClassOf(f.Kind),
                    BaseMs = FleetLoadCost.BaseMs(f, _prefabBlocks, _prefabCeiling, TrafficBudget.UnknownHullMs, out unknown),
                });
            }
        }

        /// <summary>Applies the performance ceiling and names every scanned ship prefab it excluded
        /// (operator, 2026-09-24: the excluded hulls are listed, not only counted).</summary>
        private void LogPrefabCeiling(SePrefabCatalogue catalogue)
        {
            try
            {
                int judged;
                List<SePrefabCatalogue.CeilingExclusion> excluded = catalogue.ApplyPerformanceCeiling(_prefabCeiling, out judged);
                if (!_prefabCeiling.Enabled)
                {
                    Log.Info("Prefab ceiling: off (PrefabCeilingMsPerFrame " + _prefabCeiling.CeilingMs + ").");
                    return;
                }
                var names = new List<string>(excluded.Count);
                foreach (SePrefabCatalogue.CeilingExclusion x in excluded)
                    names.Add(x.PrefabId + " " + x.Blocks + " blocks " + x.PredictedMs.ToString("F2", CultureInfo.InvariantCulture) + " ms");
                Log.Info("Prefab ceiling: " + excluded.Count + " of " + judged + " ship prefab(s) scanned but over "
                         + _prefabCeiling.CeilingMs.ToString("F2", CultureInfo.InvariantCulture) + " ms/frame awake"
                         + " (predicted " + _prefabCeiling.CostPerBlockExpMs.ToString("0.0##E+0", CultureInfo.InvariantCulture)
                         + " x blocks^" + _prefabCeiling.Exponent.ToString("0.##", CultureInfo.InvariantCulture)
                         + "), excluded from every role pool"
                         + (names.Count > 0 ? ": " + string.Join(", ", names.ToArray()) : "") + ".");
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Prefab ceiling could not be applied; the pool is uncapped.");
            }
        }

        private void StartServerPerf()
        {
            try
            {
                ServerPerfConfig config = ServerPerfConfig.Load(InstanceDir);
                _prefabCeiling = new PrefabCeiling
                {
                    CeilingMs = config.PrefabCeilingMsPerFrame,
                    CostPerBlockExpMs = config.PrefabCostCoefficient,
                    Exponent = config.LedgerSizeExponent,
                };

                // The step hook is bound whether or not collection is on: it is one method per
                // frame, and binding it lazily would mean a `factions debug serverperf` mid-session
                // reported "not bound" for the rest of the run.
                PhysicsStepPatches.TryPatch(_harmony);
                HavokMemoryTrace.TryPatch(_harmony);
                RockContactPhysics.TryPatch(_harmony);
                StationQuiet.TryPatch(_harmony);
                StationQuiet.ObserversKm = () => _playerPositionsKm;

                // Spawn pacing is behaviour, not telemetry: it applies whether or not collection is on.
                SpawnPacing.Pacer.SpacingSeconds = config.SpawnSpacingSeconds > 0 ? config.SpawnSpacingSeconds : 0;

                if (!config.Enabled)
                {
                    Log.Info("Server perf: disabled (" + ServerPerfConfig.FileName + ").");
                    return;
                }

                EntityCostPatches.Enabled = config.EntityCostEnabled;
                if (config.EntityCostEnabled)
                {
                    var patchManager = Torch.Managers.GetManager<global::Torch.Managers.PatchManager.PatchManager>();
                    EntityCostPatches.TryPatch(patchManager);
                    PbCostPatches.TryPatch(patchManager);
                    ProductionThrottlePatches.TryPatch(patchManager);
                    PbLimiterPatches.TryPatch(patchManager);
                    if (config.HitchThresholdMs > 0)
                    {
                        HitchProbe.ThresholdTicks = (long)(config.HitchThresholdMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
                        HitchProbe.TryPatch(patchManager);
                        HitchProbe.Enabled = true;
                    }
                }

                _serverPerf = new ServerPerfMonitor(config, InstanceDir);
                Log.Info(_serverPerf.Describe());
            }
            catch (Exception ex)
            {
                _serverPerf = null;
                Log.Warn("Server perf: could not start (" + ex.Message + "); telemetry is off.");
            }
        }

        /// <summary>Closes a telemetry window when one is due. Cheap and unconditional otherwise:
        /// the monitor compares two timestamps and returns.</summary>
        private void TickServerPerf()
        {
            if (_serverPerf == null) return;
            try
            {
                FactionRuntime runtime = _runtime;
                if (runtime != null && runtime.MaterialiseGate == null)
                {
                    ServerPerfMonitor perf = _serverPerf;
                    // With traffic control's presence rule on, it alone decides civilian traffic; the
                    // governor's step-1 hold would release everything it held at once.
                    runtime.MaterialiseGate = kind => perf.GovernorFactionsActuate
                        && !FactionsLoadActuator.IsTraffic(kind)
                        ? FactionsLoadActuator.Decide(kind, perf.GovernorFactionsStep)
                        : MaterialiseDecision.Materialise;
                    runtime.CombatMaxWait = perf.GovernorCombatMaxWait;
                    perf.HeldOffScreenReader = runtime.TakeHeldOffScreenCount;
                    perf.PlayerNotifier = WhisperPlayerByName;
                    perf.CombatWaitReader = counts => { int w, x; runtime.TakeCombatWaitCounts(out w, out x); counts[0] = w; counts[1] = x; };
                    runtime.SpawnAdmissionProbe = (fleet, key, now) => ProbeSpawnAdmission(perf, _prefabBlocks, _prefabCeiling, fleet, key, now);
                    perf.FleetLoadReader = list => ReadFleetLoads(runtime, perf, list);
                    runtime.TrafficPlanner = (candidates, stations, queues, now) => PlanPresence(perf, candidates, stations, queues, now);
                    perf.PresencePlanReader = () => runtime.PresencePlan;
                    perf.PresenceCountReader = counts => { int r, sh; runtime.TakePresenceCounts(out r, out sh); counts[0] = r; counts[1] = sh; };
                    perf.QueueCountReader = runtime.TakeQueueCounts;
                    // Fight Control (FAC-425): a hull is priced by the bench law at the learnt combat
                    // calibration; a site's field is its share of the Factions fleet budget at the step.
                    runtime.WaveHullCostMs = prefab =>
                    {
                        int blocks = _prefabBlocks != null && !string.IsNullOrEmpty(prefab) ? _prefabBlocks(prefab) : 0;
                        double baseMs = blocks > 0 && _prefabCeiling != null ? _prefabCeiling.PredictedAwakeMs(blocks) : TrafficBudget.UnknownHullMs;
                        return baseMs * perf.Traffic.Multiplier(TrafficClass.Combat);
                    };
                    runtime.WaveFieldMs = (sites, simFloor) => FightWaves.FieldMs(perf.Traffic.FactionsBudgetMs, sites,
                        perf.GovernorFactionsActuate ? perf.GovernorFactionsStep : 0, simFloor);
                }
                // An agent has no runtime; its fleet grids are the ones it flies for the authority,
                // and its traffic budget, measured here, travels to the authority in its report.
                AgentFleets agentFleets = runtime == null && _agentHands != null ? _agentHands.Fleets : null;
                if (agentFleets != null && _serverPerf.FleetLoadReader == null)
                {
                    PrefabCeiling law = _prefabCeiling;
                    _serverPerf.FleetLoadReader = list => { AgentFleets f = _agentHands != null ? _agentHands.Fleets : null; if (f != null) f.ReadLoads(list, law); };
                    if (_isStationGrid == null)
                    {
                        _isStationGrid = id => _agentHands != null && _agentHands.Stations != null
                                               && _agentHands.Stations.NodeIdForStationGrid(id) != null;
                        _isNearAnyPlayer = pos => AnyPlayerWithinKm(
                            new Factions.Core.Vec3(pos.X / 1000.0, pos.Y / 1000.0, pos.Z / 1000.0),
                            _tuning.ObservationRadiusKm);
                    }
                }
                _serverPerfFleetIds.Clear();
                if (runtime != null) runtime.CollectFleetGridIds(_serverPerfFleetIds);
                else if (agentFleets != null) agentFleets.CollectGridIds(_serverPerfFleetIds);

                if (_isFlyingGrid == null && runtime != null)
                {
                    _isFlyingGrid = runtime.IsGridUnderFlightControl;
                    _isStationGrid = runtime.IsStationGrid;
                    // The same radius the fleet spawner materialises on, so "far" means "past the
                    // point where anything should have been spawned for anyone". AnyPlayerWithinKm
                    // takes kilometres; SE's entity positions are metres.
                    _isNearAnyPlayer = pos => AnyPlayerWithinKm(
                        new Factions.Core.Vec3(pos.X / 1000.0, pos.Y / 1000.0, pos.Z / 1000.0),
                        _tuning.ObservationRadiusKm);
                }

                _serverPerf.TickFlush(DateTime.UtcNow, _sessionReady && (runtime != null || agentFleets != null),
                                      _serverPerfFleetIds, _isFlyingGrid, _isStationGrid,
                                      _isNearAnyPlayer);
            }
            catch (Exception ex)
            {
                Log.Warn("Server perf: tick failed: " + ex.Message);
            }
        }
        /// <summary>Sends a server whisper to the online player whose identity name is
        /// <paramref name="party"/>, the name the grid census charges their grids to. An offline
        /// player is not told; their blocks are paused all the same.</summary>
        private void WhisperPlayerByName(string party, string message)
        {
            try
            {
                var chat = Torch.CurrentSession?.Managers?.GetManager<ChatManagerServer>();
                if (chat == null || string.IsNullOrEmpty(party) || MySession.Static == null) return;
                foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
                {
                    if (player == null || !string.Equals(player.DisplayName, party, StringComparison.Ordinal)) continue;
                    chat.SendMessageAsOther("Server", message, VRageMath.Color.Orange, player.Id.SteamId);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Server perf: could not tell " + party + " about a pause (" + ex.Message + ").");
            }
        }

        private long _gapWindowStart;
        private int _gapWindowFrames, _gapWindowStalls;
        private double _gapWorstMs, _gapWorstOursMs, _gapOursTotalMs;

        private static double TicksToMs(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>Called first thing in <see cref="Update"/>, before any work. The gap it reports
        /// is wall time since the previous entry, so it covers everything the game thread did
        /// between the two -- this plugin's own previous frame included, which is why the previous
        /// frame's own cost is carried alongside rather than subtracted.</summary>
        private void ProbeFrameGap(long entry)
        {
            if (_lastUpdateEntry != 0)
            {
                double gapMs = TicksToMs(entry - _lastUpdateEntry);
                double oursMs = TicksToMs(_lastFrameOwnTicks);

                _gapWindowFrames++;
                _gapOursTotalMs += oursMs;
                if (gapMs > _gapWorstMs) { _gapWorstMs = gapMs; _gapWorstOursMs = oursMs; }

                // The same two numbers, kept as a distribution rather than as a worst case: this
                // channel reports only the window's largest gap, which cannot separate one hitch
                // from a thousand merely-slow frames. See ServerPerfMonitor.
                if (_serverPerf != null)
                    _serverPerf.SampleFrame(entry - _lastUpdateEntry, _lastFrameOwnTicks);

                if (gapMs >= FrameGapWarnMs)
                {
                    _gapWindowStalls++;
                    if (DebugFrameGap)
                        Log.Info("TICK_STALL gap=" + gapMs.ToString("F1") + "ms"
                                 + " ours=" + oursMs.ToString("F1") + "ms"
                                 + " (the previous frame's own cost; the remainder was outside this plugin)"
                                 + " players=" + (_cachedPlayers == null ? 0 : _cachedPlayers.Length)
                                 + " deferred=" + _simulationDeferred);
                }

                if (_gapWindowStart == 0) _gapWindowStart = entry;
                else if (TicksToMs(entry - _gapWindowStart) >= 30000.0)
                {
                    if (DebugFrameGap)
                        Log.Info("TICK_WINDOW " + (TicksToMs(entry - _gapWindowStart) / 1000.0).ToString("F0") + "s"
                                 + " frames=" + _gapWindowFrames
                                 + " stalls=" + _gapWindowStalls
                                 + " worstGap=" + _gapWorstMs.ToString("F1") + "ms"
                                 + " ourCostInThatFrame=" + _gapWorstOursMs.ToString("F1") + "ms"
                                 + " ourTotalMs=" + _gapOursTotalMs.ToString("F0"));
                    _gapWindowStart = entry;
                    _gapWindowFrames = _gapWindowStalls = 0;
                    _gapWorstMs = _gapWorstOursMs = _gapOursTotalMs = 0;
                }
            }
            _lastUpdateEntry = entry;
        }

        public override void Update()
        {
            ProbeFrameGap(Stopwatch.GetTimestamp());

            // base.Update is recorded for correlation, but excluded from the Factions-owned frame
            // contract: it is Torch/SE work outside this plugin's scheduling control.
            long baseStart = Stopwatch.GetTimestamp();
            long pre = AllocProbe.Begin();
            base.Update();
            long baseTicks = Stopwatch.GetTimestamp() - baseStart;
            AllocProbe.End("pre.base", pre); pre = AllocProbe.Begin();

            // Before the session-ready return on purpose: a window that closed only while the world
            // was up would have no samples across a load, which is exactly when the frame gap is
            // worth having. Sits outside the frame span measured into `_perfFrame`, so the one part
            // of it that costs real time -- the entity walk -- reports itself as `censusMs` rather
            // than hiding in the gap.
            TickServerPerf();
            AllocProbe.End("pre.serverPerf", pre); pre = AllocProbe.Begin();
            BenchSpawner.Tick();
            BenchPhantoms.Tick();
            AllocProbe.End("pre.bench", pre); pre = AllocProbe.Begin();
            CrewPresence.Tick();
            AllocProbe.End("pre.crew", pre);
            DampenerProbe.Tick();
            LandingProbe.Tick();

            if (!_sessionReady) return;
            if (_simulationDeferred)
            {
                if (++_tick % BridgePollTicks == 0) ProcessBridgeInbox();
                if (_nexusRole == NexusRole.Agent)
                {
                    try { TickAgent(); }
                    catch (Exception ex) { Log.Warn(ex, "Agent tick failed."); }
                }
                _lastFrameOwnTicks = 0;   // nothing of ours ran; a gap here is entirely outside us
                return;
            }
            pre = AllocProbe.Begin();
            try { DrainRelayAsAuthority(); }
            catch (Exception ex) { Log.Warn(ex, "Relay drain failed."); }
            AllocProbe.End("pre.relay", pre);

            AllocProbe.Tick();
            long frameStart = Stopwatch.GetTimestamp();
            long playerCacheTicks = 0;
            long runtimeTicks = 0;
            long fleetObservationTicks = 0;
            long stationObservationTicks = 0;
            long priceTicks = 0;
            long asteroidObservationTicks = 0;
            long planetTicks = 0;
            long bridgeTicks = 0;
            long presenceTicks = 0;
            long gpsTicks = 0;
            long storeTicks = 0;
            FactionRuntime runtime = _runtime;

            try
            {
                long actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                RefreshPlayerPositionCache();   // cache once; used by UpdateObservation + UpdateStationObservation
                RefreshPlayerPositionsKm();     // ...and by every scan that queries outward from the players
                playerCacheTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("playerCache");

                if (runtime != null)
                {
                    try
                    {
                        actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                        try { runtime.OnUpdateFrame(new FrameDeadline(frameStart, runtime.GetFrameSoftBudgetMilliseconds())); }   // current whole cycles remain due/unsafe until bounded cursors land
                        finally { runtimeTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("runtime"); }

                        // Real-time autopilot: stays every-frame and self-times into its own
                        // `flight` bucket, because it cannot move onto the coarser interval the
                        // observation scan is destined for.
                        long mid = AllocProbe.Begin();
                        runtime.TickFleetFlight();
                        AllocProbe.End("fleetFlight", mid); mid = AllocProbe.Begin();

                        // Running restorations at Services Terminals: a count check when none runs.
                        try { _restorationService?.Pump(); }
                        catch (Exception ex) { Log.Warn(ex, "Restoration pump failed."); }
                        AllocProbe.End("restoration", mid);

                        actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                        try { runtime.UpdateObservation(AnyPlayerOrFlownWithinKm, PlayersWithinKm, _playerPositionsKm.Count + FlownRemoteCount()); }
                        finally { fleetObservationTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("fleetObservation"); }

                        actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                        try { runtime.UpdateStationObservation(_playerPositionsKm, _tuning.ObservationRadiusKm); }
                        finally { stationObservationTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("stationObservation"); }

                        // Immediately after station observation: a siege only means anything for a
                        // materialised station, and this is the tick that has just decided which
                        // those are.
                        runtime.TickPlayerSiege(AnyPlayerOrRemoteWithinKm);

                        // Coarse cadence (see FactionRuntime._unknPruneNextUtc) -- most calls return
                        // immediately without touching the graph, so no dedicated perf counter here.
                        runtime.PruneExpiredUnknTerritory(pos => AnyPlayerWithinKm(pos, _tuning.ObservationRadiusKm));

                        actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                        try
                        {
                            long am = AllocProbe.Begin();
                            runtime.UpdateAsteroidFieldObservation(_playerPositionsKm, _tuning.ObservationRadiusKm);
                            AllocProbe.End("ast.fields", am); am = AllocProbe.Begin();
                            runtime.UpdateFeatureRockObservation(_playerPositionsKm);
                            AllocProbe.End("ast.featureRock", am);
                            // Immediately after, so a linked client is told about a rock in the
                            // same frame it gained collision -- never before. The manager throttles
                            // itself and returns at once while nobody derives their own rock.
                            am = AllocProbe.Begin();
                            try { _playerRockManager?.Tick(); }
                            catch (Exception rockEx) { Log.Warn(rockEx, "Player rock tick failed."); }
                            finally { AllocProbe.End("ast.rockNaming", am); }
                        }
                        finally { asteroidObservationTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("asteroidObservation"); }

                        // A withdrawal's staged clone needs a frame or more before SE has attached
                        // its subgrids and the blueprint can be copied whole. Returns at once while
                        // none is staged, which is nearly every frame.
                        try { _playerShipAssetsManager?.Tick(); }
                        catch (Exception shipEx) { Log.Warn(shipEx, "Player ship assets tick failed."); }

                        actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                        try { _priceUpdater?.Tick(DateTime.UtcNow); }
                        finally { priceTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("price"); }

                        actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                        try { _planetSnapshotWriter?.Tick(DateTime.UtcNow); }
                        finally { planetTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("planet"); }

                        long tail = AllocProbe.Begin();
                        try { _reputationManager?.Tick(DateTime.UtcNow); }
                        catch (Exception ex) { Log.Warn(ex, "Reputation ledger tick failed."); }
                        AllocProbe.End("reputation", tail); tail = AllocProbe.Begin();
                        try { _playerIndexWriter?.Tick(DateTime.UtcNow); }
                        catch (Exception ex) { Log.Warn(ex, "Player index tick failed."); }
                        AllocProbe.End("playerIndex", tail); tail = AllocProbe.Begin();
                        try { runtime.UpdateDepositBackfill(); }
                        catch (Exception ex) { Log.Warn(ex, "Deposit backfill failed."); }
                        try { runtime.UpdateDepositSurvey(); }
                        catch (Exception ex) { Log.Warn(ex, "Deposit survey failed."); }
                        try { runtime.UpdateDepositResurvey(); }
                        catch (Exception ex) { Log.Warn(ex, "Deposit re-survey failed."); }
                        AllocProbe.End("deposits", tail);
                    }
                    catch (Exception ex) { Log.Warn(ex, "Sim tick failed."); }
                }

                if (++_tick % BridgePollTicks == 0)
                {
                    actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                    ProcessBridgeInbox();
                    bridgeTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("bridge");
                }
                if (++_gpsTick % GpsUpdateTicks == 0)
                {
                    actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                    try { _presenceManager?.Refresh(); }
                    catch (Exception ex) { Log.Warn(ex, "Presence refresh failed."); }
                    try { _conquerOfferManager?.Tick(_runtime.SiegeAttackerFor); }
                    catch (Exception ex) { Log.Warn(ex, "ConquerOfferManager tick threw."); }
                    try { _claimOfferManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Claim offer tick failed."); }
                    try { _presenceReinforceManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Presence reinforce tick failed."); }
                    presenceTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("presence");

                    if (VanillaFleetSpawner.AnyDebugEnabled)
                    {
                        actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                        BroadcastFleetGps();
                        gpsTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("gps");
                    }
                    actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                    try { SampleGridEmissions(); }
                    catch (Exception ex) { Log.Warn(ex, "Player excitation sample failed."); }
                    try { TickTrespassHails(); }
                    catch (Exception ex) { Log.Warn(ex, "Trespass hail tick failed."); }
                    try { _playerScannerManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player scanner tick failed."); }
                    try { _playerMarketManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Market tick failed."); }
                    try { _playerMapManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player map tick failed."); }
                    try { _playerExpandManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player expand tick failed."); }
                    try { _playerAssetsManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player assets tick failed."); }
                    try { _playerNodeDetailManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player node detail tick failed."); }
                    try { _playerEventsManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player events tick failed."); }
                    try { _playerStandingOrdersManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player standing orders tick failed."); }
                    try { _playerShelfManager?.Tick(); }
                    catch (Exception ex) { Log.Warn("PlayerShelfManager tick failed: " + ex.Message); }
                    try { _playerBankSalesManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player bank sales tick failed."); }
                    try { _playerDiplomacyManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player diplomacy tick failed."); }

                    try { _playerFleetDoctrineManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player fleet doctrine tick failed."); }
                    try { _playerTacticsManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player tactics tick failed."); }
                    try { _playerCampaignsManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player campaigns tick failed."); }
                    try { _playerFactionDemandManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Player faction demand tick failed."); }
                    try { PruneUplinks(); }
                    catch (Exception ex) { Log.Warn(ex, "Uplink prune failed."); }
                    try { _debrisAbsorbManager?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Debris absorb tick failed."); }
                    try { _gridReaper?.Tick(); }
                    catch (Exception ex) { Log.Warn(ex, "Grid reaper tick failed."); }
                    try { TickFightDebris(); }
                    catch (Exception ex) { Log.Warn(ex, "Fight debris sweep failed."); }
                    gpsTicks += Stopwatch.GetTimestamp() - actorStart;
                }
                actorStart = Stopwatch.GetTimestamp(); AllocProbe.SectionStart();
                try
                {
                    if (++_storeTick % _storeRefreshTicks == 0) _storeManager?.BeginRefreshPass();
                    _storeManager?.RefreshPassStep(StoreRefreshBudgetMs * StallBackoffScale.Value);
                }
                catch (Exception ex) { Log.Warn(ex, "Store refresh failed."); }
                storeTicks = Stopwatch.GetTimestamp() - actorStart; AllocProbe.SectionEnd("store");
            }
            finally
            {
                // What this plugin spent in this frame, carried so the NEXT frame's gap can be
                // reported beside it.
                _lastFrameOwnTicks = Stopwatch.GetTimestamp() - frameStart;
                if (runtime != null)
                {
                    runtime.RecordHostFrameTiming(
                        baseTicks,
                        frameStart,
                        playerCacheTicks,
                        runtimeTicks,
                        fleetObservationTicks,
                        stationObservationTicks,
                        asteroidObservationTicks,
                        priceTicks,
                        planetTicks,
                        bridgeTicks,
                        presenceTicks,
                        gpsTicks,
                        storeTicks);
                }
            }
        }

        /// <summary>
        /// Called by <see cref="EconomyTickPatches"/> immediately after SE's
        /// <c>MySessionComponentEconomy.UpdateStations()</c> runs.  SE rewrites our store items with
        /// vanilla values; we respond by clearing each managed station and immediately re-populating
        /// from the current world model so players never see vanilla prices.
        /// </summary>
        internal void OnEconomyTickCompleted()
        {
            OnEconomyTickCompleted(false);
        }

        /// <param name="sliced">From <see cref="EconomyTickSlicer"/>, which skips SE's store-item generation
        /// for managed factions: there is nothing vanilla to strip, and the stores are rebuilt a few per
        /// frame instead of in this one.</param>
        internal void OnEconomyTickCompleted(bool sliced)
        {
            if (!_sessionReady) return;
            try
            {
                if (sliced) { _storeManager?.BeginRefreshPass(); return; }
                _storeManager?.ClearAll();
                _storeManager?.RefreshAll();
            }
            catch (Exception ex) { Log.Warn(ex, "OnEconomyTickCompleted failed."); }
        }

        /// <summary>
        /// Called by <see cref="BuyPhysicalItemInternalPatch"/>/<see cref="SellToStationPatch"/>
        /// immediately after a real player buy/sell at a Factions-managed station completes.
        /// Vanilla never calls back into us for this itself (see those patches' doc comments), so
        /// this is the only place a real player transaction reaches <c>SpatialNode.Inventory</c> and
        /// the sim's own treasury. <paramref name="creditsDelta"/> is the real SE wallet balance
        /// change the patch observed (positive = faction gained money, negative = faction paid out).
        /// <paramref name="buyerIdentityId"/> is the SE identity behind the trade, or 0 when the
        /// caller genuinely has no player -- the goods and the credits still land, only the
        /// attribution is dropped.
        /// </summary>
        internal void OnStationItemTransacted(long stationId, MyDefinitionId itemDefId, int amount, bool isBuy, long creditsDelta, long buyerIdentityId = 0)
        {
            if (!_sessionReady) return;
            if (_agentHands != null)
            {
                OnStationItemTransacted(stationId, InventoryKeyMapper.FromTypeIdAndSubtype(itemDefId.TypeId.ToString(), itemDefId.SubtypeName),
                                        amount, isBuy, creditsDelta, buyerIdentityId);
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            try { _storeManager?.HandleStationTransaction(stationId, itemDefId, amount, isBuy, creditsDelta, buyerIdentityId); }
            catch (Exception ex) { Log.Warn(ex, "OnStationItemTransacted failed."); }
            finally { RecordStoreTxnTiming(Stopwatch.GetTimestamp() - t0); }
        }

        /// <summary>Inventory-key overload, for a transaction whose store item has no
        /// <see cref="MyDefinitionId"/> to derive one from. Gas is the case: a gas
        /// <c>MyStoreItem</c> is identified by its <c>ItemTypes</c> alone.</summary>
        internal void OnStationItemTransacted(long stationId, string invKey, int amount, bool isBuy, long creditsDelta, long buyerIdentityId = 0)
        {
            if (!_sessionReady) return;
            if (_agentHands != null)
            {
                if (amount > 0 && !string.IsNullOrEmpty(invKey))
                    TryRecordAgentStoreEvent(stationId, new NexusStoreEvent
                    {
                        Kind = NexusStoreEventKind.Trade, Key = invKey, Amount = amount, Buy = isBuy, Credits = creditsDelta, Buyer = buyerIdentityId,
                    });
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            try { _storeManager?.HandleStationTransaction(stationId, invKey, amount, isBuy, creditsDelta, buyerIdentityId); }
            catch (Exception ex) { Log.Warn(ex, "OnStationItemTransacted failed."); }
            finally { RecordStoreTxnTiming(Stopwatch.GetTimestamp() - t0); }
        }

        /// <summary>Whether the Services Terminal patches should do any work at all: a live session
        /// with a store manager and the settlement switch on. Read from a Harmony prefix before it
        /// spends anything on a grid census.</summary>
        internal bool ServicesSettlementReady
        {
            get
            {
                if (_agentHands != null) return _sessionReady && _tuning != null && _tuning.ServicesTerminalSettlement;
                return _sessionReady && _storeManager != null && _storeManager.ServicesSettlementEnabled;
            }
        }

        /// <summary>Whether an SE station id belongs to a node this brain owns. Same lookup the
        /// settlement handlers use, so a prefix that skips on false skips nothing they would have
        /// settled.</summary>
        /// <summary>Agent: the owner tag of the snapshot node whose station is <paramref name="stationId"/>
        /// and that owner's snapshot row; false when this server stands no such station.</summary>
        internal bool TryAgentStationOwner(long stationId, out NexusSnapshotFaction owner)
        {
            owner = null;
            AgentHands hands = _agentHands;
            if (!_sessionReady || hands == null || hands.Graph == null) return false;
            string nodeId = hands.NodeIdForStation(stationId);
            SpatialNode node;
            if (nodeId == null || !hands.Graph.Nodes.TryGetValue(nodeId, out node)) return false;
            owner = hands.FactionRow(node.OwnerFactionTag);
            return owner != null;
        }

        /// <summary>Agent: the authority's snapshot row for <paramref name="tag"/>; null when it is not
        /// registered, or this is not an agent with hands.</summary>
        internal NexusSnapshotFaction AgentFactionRow(string tag)
        {
            AgentHands hands = _agentHands;
            return hands != null ? hands.FactionRow(tag) : null;
        }

        /// <summary>Agent: queues an event for the authority on the store stream. Dropped when this is
        /// not an agent with hands.</summary>
        internal void RecordAgentStoreEvent(NexusStoreEvent e)
        {
            AgentHands hands = _agentHands;
            if (hands != null && e != null) hands.RecordStoreEvent(e);
        }

        /// <summary>Agent: a registered player-faction member's conduct toward an NPC faction, for the
        /// authority's member reputation book. Conduct by anyone else is not recorded there.</summary>
        internal void RecordAgentConduct(long identityId, long factionId, int delta, VRage.Game.ModAPI.ReputationChangeReason reason, bool propagate)
        {
            AgentHands hands = _agentHands;
            MySession session = MySession.Static;
            if (hands == null || delta == 0 || session == null || session.Factions == null) return;
            MyFaction target = session.Factions.TryGetFactionById(factionId) as MyFaction;
            MyFaction own = session.Factions.GetPlayerFaction(identityId);
            if (target == null || own == null) return;
            NexusSnapshotFaction ownRow = hands.FactionRow(own.Tag);
            if (ownRow == null || !ownRow.Player) return;
            ulong steam = session.Players.TryGetSteamId(identityId);
            hands.RecordConduct(identityId, steam, target.Tag, delta, (int)reason, propagate);
        }

        /// <summary>A whisper to a player connected to this server.</summary>
        internal void WhisperLocal(ulong steamId, string message)
        {
            if (steamId == 0UL || string.IsNullOrEmpty(message)) return;
            try
            {
                ChatManagerServer chat = Torch?.CurrentSession?.Managers?.GetManager<ChatManagerServer>();
                if (chat != null) chat.SendMessageAsOther("Factions", message, VRageMath.Color.CornflowerBlue, steamId);
            }
            catch (Exception ex) { Log.Warn(ex, "Whispering player " + steamId + " failed."); }
        }

        /// <summary>Agent: whether the authority's registry has <paramref name="tag"/>.</summary>
        /// <summary>Agent: the terminal gates' view of the nodes and factions, from the authority's
        /// snapshot; null before the first one.</summary>
        internal TerminalWorld AgentTerminalWorld()
        {
            AgentHands hands = _agentHands;
            if (!_sessionReady || hands == null || hands.Graph == null) return null;
            return new TerminalWorld
            {
                Graph = hands.Graph,
                FindFaction = hands.FindFaction,
                Judge = (identityId, ownerTag) =>
                    StoreRefusal.JudgeOnAgent(this, hands.FactionRow(ownerTag), identityId),
            };
        }

        internal bool IsAgentRegistryFaction(string tag)
        {
            AgentHands hands = _agentHands;
            return hands != null && hands.IsRegistryFaction(tag);
        }

        internal bool IsFactionsStation(long stationId)
        {
            if (_agentHands != null) return _sessionReady && _agentHands.NodeIdForStation(stationId) != null;
            return _sessionReady && _storeManager != null && _storeManager.HasStationNode(stationId);
        }

        /// <summary>
        /// Called by <see cref="SalvageAcceptOfferPatch"/> after a player sold a hull to a Factions
        /// station's Services Terminal. Vanilla credits the player from nowhere and deletes the
        /// wreck; this is where the faction pays for it and the parts land in the node. Provenance:
        /// FAC-280.
        /// </summary>
        internal void OnSalvageSettled(long stationId, long buyerIdentityId, long creditsPaid, int blocks,
            IDictionary<string, long> components, IDictionary<string, long> cargo)
        {
            if (!_sessionReady) return;
            if (_agentHands != null)
            {
                TryRecordAgentStoreEvent(stationId, new NexusStoreEvent { Kind = NexusStoreEventKind.Salvage, Credits = creditsPaid, Buyer = buyerIdentityId, Blocks = blocks,
                    Parts = components != null ? new Dictionary<string, long>(components) : null, Cargo = cargo != null ? new Dictionary<string, long>(cargo) : null });
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            try { _storeManager?.HandleSalvageSettled(stationId, buyerIdentityId, creditsPaid, blocks, components, cargo); }
            catch (Exception ex) { Log.Warn(ex, "OnSalvageSettled failed."); }
            finally { RecordStoreTxnTiming(Stopwatch.GetTimestamp() - t0); }
        }

        /// <summary>Called by <see cref="RepairServicePatch"/> after a player paid a Factions station
        /// to repair a grid. The components vanilla conjures come out of the node instead.</summary>
        internal void OnRepairSettled(long stationId, long buyerIdentityId, long cost,
            IDictionary<string, long> components)
        {
            if (!_sessionReady) return;
            if (_agentHands != null)
            {
                TryRecordAgentStoreEvent(stationId, new NexusStoreEvent { Kind = NexusStoreEventKind.Repair, Credits = cost, Buyer = buyerIdentityId,
                    Parts = components != null ? new Dictionary<string, long>(components) : null });
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            try { _storeManager?.HandleRepairSettled(stationId, buyerIdentityId, cost, components); }
            catch (Exception ex) { Log.Warn(ex, "OnRepairSettled failed."); }
            finally { RecordStoreTxnTiming(Stopwatch.GetTimestamp() - t0); }
        }

        /// <summary>Called by <see cref="GridStorageExpeditePatch"/> after a player paid to expedite a
        /// stored-grid retrieval. A pure service fee: no goods move, only the ledger.</summary>
        internal void OnExpediteSettled(long stationId, long buyerIdentityId, long fee)
        {
            if (!_sessionReady) return;
            if (_agentHands != null)
            {
                TryRecordAgentStoreEvent(stationId, new NexusStoreEvent { Kind = NexusStoreEventKind.Expedite, Credits = fee, Buyer = buyerIdentityId });
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            try { _storeManager?.HandleExpediteSettled(stationId, buyerIdentityId, fee); }
            catch (Exception ex) { Log.Warn(ex, "OnExpediteSettled failed."); }
            finally { RecordStoreTxnTiming(Stopwatch.GetTimestamp() - t0); }
        }

        /// <summary>Perf hook for <see cref="FactionContractStrategy.GenerateContract"/> — called by
        /// vanilla's own contract-board refill loop (<c>MySessionComponentContractSystem.CreateContractsForStation</c>),
        /// outside our own update loop entirely, so this is the only measurement point for it.</summary>
        internal void RecordContractGenTiming(long elapsedTicks)
        {
            if (!_sessionReady) return;
            _runtime?.RecordContractGenTiming(elapsedTicks);
        }

        /// <summary>Perf hook for a single player store transaction (<see cref="OnStationItemTransacted"/>),
        /// distinct from <c>_perfStoreRefresh</c> which only times the periodic <c>StationStoreManager.RefreshAll</c>
        /// pass, not the per-transaction Harmony-patch path.</summary>
        internal void RecordStoreTxnTiming(long elapsedTicks)
        {
            if (!_sessionReady) return;
            _runtime?.RecordStoreTxnTiming(elapsedTicks);
        }

        /// <summary>
        /// Push a GPS marker to every online player's HUD for each active fleet with debug on.
        /// Called every ~5 s while any debug flag is on. Each marker has a 15 s TTL so stale
        /// positions auto-expire cleanly; the 5 s refresh keeps them current.
        /// When the fleet is already materialised, EntityId is set so the marker auto-tracks
        /// the physical grid and the hash stays stable (no flicker).
        /// Colours: Trade=Cyan, Raid=Red, Patrol=Yellow.
        /// </summary>
        private void BroadcastFleetGps()
        {
            if (_runtime == null || MySession.Static == null) return;
            try
            {
                var allFleets = new List<FleetGpsInfo>();
                if (VanillaFleetSpawner.DebugTrade)   allFleets.AddRange(_runtime.GetActiveFleetPositions(ActionKind.Trade));
                if (VanillaFleetSpawner.DebugRaid)    allFleets.AddRange(_runtime.GetActiveFleetPositions(ActionKind.Raid));
                if (VanillaFleetSpawner.DebugGuard)   allFleets.AddRange(_runtime.GetActiveFleetPositions(ActionKind.Guard));
                if (VanillaFleetSpawner.DebugPatrol)  allFleets.AddRange(_runtime.GetActiveFleetPositions(ActionKind.Patrol));
                if (VanillaFleetSpawner.DebugFerry)   allFleets.AddRange(_runtime.GetActiveFleetPositions(ActionKind.Ferry));
                if (allFleets.Count == 0) return;

                TimeSpan ttl = MySession.Static.ElapsedPlayTime.Add(TimeSpan.FromSeconds(15));

                foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                {
                    long identityId = player.Identity.IdentityId;

                    Dictionary<string, int> playerHashes;
                    if (!_gpsHashes.TryGetValue(identityId, out playerHashes))
                    {
                        playerHashes = new Dictionary<string, int>(StringComparer.Ordinal);
                        _gpsHashes[identityId] = playerHashes;
                    }

                    foreach (FleetGpsInfo info in allFleets)
                    {
                        VRageMath.Color colour;
                        if (info.Kind == ActionKind.Raid)        colour = VRageMath.Color.Red;
                        else if (info.Kind == ActionKind.Guard)  colour = VRageMath.Color.Yellow;
                        else if (info.Kind == ActionKind.Patrol) colour = VRageMath.Color.Cyan;
                        else                                      colour = VRageMath.Color.White;

                        var gps = new MyGps
                        {
                            Name          = info.Label,
                            DisplayName   = info.Label,
                            Coords        = new VRageMath.Vector3D(info.PosKm.X * 1000.0,
                                                                   info.PosKm.Y * 1000.0,
                                                                   info.PosKm.Z * 1000.0),
                            GPSColor      = colour,
                            ShowOnHud     = true,
                            AlwaysVisible = false,
                            DiscardAt     = ttl,
                        };
                        // When the fleet is materialised, override coords with entity tracking so
                        // the marker follows the actual grid and hash stays stable across refreshes.
                        if (info.EntityId != 0L) gps.EntityId = info.EntityId;
                        gps.UpdateHash();

                        // Delete the previous marker for this label before sending the new one.
                        // SE hashes GPS by name+coords when EntityId==0, so a position change
                        // produces a new hash rather than updating in place.
                        int prevHash;
                        if (playerHashes.TryGetValue(info.Label, out prevHash) && prevHash != gps.Hash)
                            MySession.Static.Gpss.SendDeleteGpsRequest(identityId, prevHash);

                        playerHashes[info.Label] = gps.Hash;
                        MySession.Static.Gpss.SendAddGpsRequest(identityId, ref gps, info.EntityId, playSoundOnCreation: false);
                    }
                }
            }
            catch (Exception ex) { Log.Warn(ex, "BroadcastFleetGps failed."); }
        }

        /// <summary>
        /// Run any queued bridge commands. Called from <see cref="Update"/> (the game thread), so it is
        /// safe to touch the session/command manager directly. Each non-empty, non-<c>#</c> line is run
        /// with server authority via <see cref="CommandManager.HandleCommandFromServer"/>; the inbox is
        /// consumed (deleted) on read so a command never repeats. Fully guarded — a bad command file
        /// must never stutter or crash the simulation tick.
        /// </summary>
        private void ProcessBridgeInbox()
        {
            try
            {
                if (_bridgeInboxPath == null || !File.Exists(_bridgeInboxPath)) return;

                string raw;
                try { raw = File.ReadAllText(_bridgeInboxPath); }
                catch (IOException ex)
                {
                    // Almost always the writer still holding the handle: whoever wrote the file is
                    // mid-write when this poll lands. Leave it in place and re-read next poll.
                    // Deleting here loses the command outright -- the operator sees only a WARN and
                    // nothing in the outbox, which is how a 'factions debug uplink' went missing on
                    // 2026-08-22 and left traffic logging on.
                    Log.Warn(ex, "Bridge inbox locked; will retry next poll.");
                    return;
                }
                catch (Exception ex)
                {
                    // Anything else is unlikely to fix itself, so clear the file rather than
                    // re-reading it forever.
                    Log.Warn(ex, "Bridge inbox read failed; discarding.");
                    try { File.Delete(_bridgeInboxPath); } catch { }
                    return;
                }
                try { File.Delete(_bridgeInboxPath); } catch { }
                if (string.IsNullOrEmpty(raw)) return;

                foreach (string line in raw.Split('\n'))
                {
                    string cmd = line.Trim();
                    if (cmd.Length == 0 || cmd[0] == '#') continue;          // blank / comment
                    if (cmd[0] == '!') cmd = cmd.Substring(1).Trim();         // accept an optional '!' prefix
                    if (cmd.Length == 0) continue;

                    try
                    {
                        // Factions verbs run DIRECTLY to avoid going through Torch's command
                        // lookup. Non-Factions commands fall through to HandleCommandFromServer.
                        string output;
                        if (TryRunFactionsVerb(cmd, out output))
                        {
                            string flat = (output ?? "").Replace("\r", "").Replace("\n", " | ");
                            AppendOutbox("OK: " + cmd + " => " + flat);
                            Log.Info("Bridge ran '" + cmd + "' => " + flat);
                        }
                        else
                        {
                            // HandleCommandFromServerInternal always skips the first character
                            // (message.Skip(1)), expecting it to be '!'. Re-add it here so that
                            // e.g. "restart" → "!restart" → looks up "restart" correctly.
                            CommandManager commands = Torch.CurrentSession?.Managers?.GetManager<CommandManager>();
                            if (commands != null)
                            {
                                commands.HandleCommandFromServer("!" + cmd);
                                AppendOutbox("SENT: !" + cmd);
                                Log.Info("Bridge forwarded '!" + cmd + "' to Torch.");
                            }
                            else AppendOutbox("ERROR: command manager unavailable for '" + cmd + "'");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendOutbox("ERROR running '" + cmd + "': " + ex.Message);
                        Log.Warn(ex, "Bridge command '" + cmd + "' failed.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Bridge inbox processing failed.");
            }
        }

        internal void AppendOutbox(string line)
        {
            try { File.AppendAllText(_bridgeOutboxPath, DateTime.UtcNow.ToString("u") + "  " + line + Environment.NewLine); }
            catch { }
        }

        /// <summary>
        /// Run a Factions verb (init / scan / list / sources &lt;tag&gt; / forget &lt;tag&gt;) directly and return a
        /// short result string. Returns false (no <paramref name="result"/>) if the line is not a Factions
        /// command, so the bridge can fall back to Torch. Called on the game thread from the bridge.
        /// </summary>
        /// <summary>Below this many rounds a weapon telemetry row is marked [thin] in the readout —
        /// a handful of shots is an anecdote, and a reader tuning on it should be told which rows are
        /// which rather than having to notice.</summary>
        private const long ThinWeaponSampleShots = 20;

        /// <summary>Verbs an agent runs itself: they act on its own grids, debug switches, tuning,
        /// frame timings and weapon counts, not on the simulation.</summary>
        private static readonly HashSet<string> AgentVerbsHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "grids", "debris", "debug", "reload", "serverperf", "weapons",
        };

        public bool TryRunFactionsVerb(string line, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(line)) return false;
            string[] t = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0 || !t[0].Equals("factions", StringComparison.OrdinalIgnoreCase)) return false;

            string verb = t.Length > 1 ? t[1].ToLowerInvariant() : "";
            string arg = t.Length > 2 ? t[2] : null;

            // Answerable on a deferred server, and the only way to confirm the gate's decision on a
            // live cluster -- the test box has no Nexus, so the defer paths run nowhere else.
            if (verb == "nexus")
            {
                result = "nexus: presence=" + _nexusPresence
                       + " thisServerId=" + (_nexusPresence == NexusPresence.Resolved
                            ? _nexusServerId.ToString() : "n/a")
                       + " authorityServerId=" + (_tuning.NexusAuthorityServerId > 0
                            ? _tuning.NexusAuthorityServerId.ToString() : "unset")
                       + " simulating=" + (!_simulationDeferred)
                       + " role=" + _nexusRole
                       + " | " + _authorityReason
                       + " | " + _nexusDetail
                       + " | relay: " + (_uplinkManager != null ? _uplinkManager.RelayStatus()
                                          : (_nexusRelay != null ? _nexusRelay.Status() : "none"))
                       + " | remote players: " + _remotePlayers.Describe(DateTime.UtcNow)
                       + (_agentHands != null ? " | " + _agentHands.Describe(DateTime.UtcNow) : "")
                       + (_nexusRole == NexusRole.Authority && _snapshotPayload != null
                            ? " | snapshot#" + _snapshotSequence + " bytes=" + _snapshotPayload.Length
                              + " agentStations=" + (_runtime != null ? _runtime.AgentStationCount : 0)
                              + " agentFleets=" + _fleetRegistry.LiveCount + "/" + _fleetRegistry.SpawningCount + "spawning" : "");
                return true;
            }

            // A definition question, not a simulation one, so it runs before the deferred gate: a
            // cluster member that is not the authority resolves the same definition set, and that
            // set is the whole point of the dump.
            if (verb == "dumpdefs")
            {
                result = DefinitionDumpWriter.Write(StorageDir);
                return true;
            }

            // A world-physics question, not a simulation one, so it runs before the deferred gate
            // for the same reason dumpdefs does: a cluster member that is not the economy authority
            // still runs physics for its own sectors and still needs its own speed limit.
            if (verb == "gridspeed")
            {
                result = HandleGridSpeed(t);
                return true;
            }

            // Same class of question as gridspeed, and before the deferred gate for the same
            // reason: the ammo definitions belong to the physics this process runs.
            if (verb == "missilespeed")
            {
                result = HandleMissileSpeed(t);
                return true;
            }

            // Why Havok keeps grids awake: one line per dynamic grid beside every wake cause. A
            // physics question about this process, so before the deferred gate like the two above.
            // 'factions awake' lists awake grids only; 'factions awake all' adds the asleep ones as
            // the control.
            if (verb == "awake")
            {
                bool all = t.Length > 2 && string.Equals(t[2], "all", StringComparison.OrdinalIgnoreCase);
                result = AwakeProbe.Report(all);
                return true;
            }

            // The load governor's dry-run decision (docs/load-governor.md, step 4).
            if (verb == "governor")
            {
                result = _serverPerf != null ? _serverPerf.GovernorReport() : "governor: server perf is disabled (" + ServerPerfConfig.FileName + ").";
                return true;
            }

            // Player toolbox step 3 (docs/load-governor.md): what is paused, and the admin override.
            // 'factions throttle' reports; 'factions throttle <player> <refinery|assembler|welder|all> on|off'
            // pauses or releases that class on every grid the player owns, independent of the governor.
            if (verb == "throttle")
            {
                if (_serverPerf == null) { result = "throttle: server perf is disabled (" + ServerPerfConfig.FileName + ")."; return true; }
                if (t.Length < 3) { result = _serverPerf.ThrottleReport(); return true; }
                string state = t.Length >= 5 ? t[4].ToLowerInvariant() : "";
                if (state != "on" && state != "off")
                {
                    result = "Usage: factions throttle [<player> <refinery|assembler|welder|all> on|off]";
                    return true;
                }
                result = _serverPerf.ForceThrottle(t[2], t[3], state == "on");
                return true;
            }

            // Test hulls for validating the load ledger against a controlled change, and staged
            // fleet scenarios (fight/raid/duel/leave/return/status) for live checks of fleet work.
            if (verb == "bench")
            {
                result = BenchSpawner.Handle(t);
                return true;
            }

            // A headless rig for the assembler input-tray rules: character, assembler+cargo grid,
            // items, queue, status (AssemblerTestBench).
            if (verb == "asmtest")
            {
                result = AssemblerTestBench.Handle(t);
                return true;
            }

            if (verb == "crew")
            {
                result = CrewPresence.Handle(t);
                return true;
            }

            // Runtime-only switches for behaviour still under live verification (FeatureSwitch):
            // 'factions feature' lists them, 'factions feature <name> on|off' flips one. Off at boot.
            if (verb == "feature")
            {
                result = HandleFeature(t);
                return true;
            }

            // Grid-local verbs: each reads or changes entities this process holds and needs no
            // simulation, so it answers on every server in the cluster, a Nexus agent included.
            if (verb == "closegrid")
            {
                result = HandleCloseGrid(t);
                return true;
            }
            if (verb == "hullprobe")
            {
                result = HandleHullProbe(t);
                return true;
            }
            if (verb == "dampprobe")
            {
                result = HandleDampProbe(t, null);
                return true;
            }
            if (verb == "surfaceprobe")
            {
                result = HandleSurfaceProbe(t);
                return true;
            }

            // A deferred server has no runtime at all; without this every verb reports the
            // misleading "no session loaded" instead of the actual reason. An agent hands the verb to
            // the authority, whose answer lands in this outbox when it comes; the verbs about this
            // server's own grids and settings run here.
            if (_simulationDeferred && !AgentVerbsHere.Contains(verb))
            {
                if (_nexusRole == NexusRole.Agent && _chatCommands != null && _chatCommands.ForwardVerb(line.Trim()))
                    result = "sent to the authority (server " + _tuning.NexusAuthorityServerId
                           + "); its answer follows in this outbox.";
                else
                    result = "factions: not simulating on this server -- " + _authorityReason
                           + ". Run 'factions nexus' for the full decision.";
                return true;
            }

            switch (verb)
            {
                case "init":
                {
                    InitResult r = InitializeFromNpcFactions();
                    result = "init: added " + r.Added + ", skipped " + r.Skipped + ", ignored " + r.Ignored
                             + ", player-owned " + r.NonNpc
                             + (r.Added > 0 ? " [" + string.Join(", ", r.AddedTags) + "]" : "")
                             + (r.NonNpc > 0 ? " player[" + string.Join(", ", r.NonNpcTags) + "]" : "");
                    return true;
                }
                case "sanitize":
                {
                    result = HandleSanitize(t);
                    return true;
                }
                case "target":
                {
                    result = HandleTarget(t);
                    return true;
                }
                case "world":
                {
                    result = HandleWorld(t);
                    return true;
                }
                case "weapons":
                {
                    // Readout for the fire/hit instrument. "reset" between controlled tests -- a
                    // sample spanning two configurations is not one sample.
                    string sub = t.Length > 2 ? t[2] : null;
                    if (string.Equals(sub, "reset", StringComparison.OrdinalIgnoreCase))
                        result = WeaponFireTelemetry.Reset();
                    else
                    {
                        WeaponFireTelemetry.Save();
                        result = WeaponFireTelemetry.Report(ThinWeaponSampleShots);
                    }
                    return true;
                }
                case "scan":
                {
                    ScanResult r = ScanWorldAssets();
                    result = "scan: " + r.GridsExamined + " grid(s)/" + r.QualifyingStations + " NPC station(s) + "
                             + r.EconomyStationsSeen + " economy station(s) (" + r.EconomyStationsUnspawned
                             + " unspawned added); wrote " + r.SourcesWritten + " source(s) across "
                             + r.FactionsTouched + " faction(s); " + r.UnregisteredGrids + " unregistered; "
                             + r.TopologyLinksAdded + " topology link(s).";
                    return true;
                }
                case "list":
                {
                    List<IndexEntry> idx = Store.LoadIndex();
                    var sb = new StringBuilder("list: " + idx.Count + " faction(s):");
                    foreach (IndexEntry e in idx) sb.Append(" " + e.Tag);
                    result = sb.ToString();
                    return true;
                }
                case "sources":
                {
                    if (string.IsNullOrEmpty(arg)) { result = "sources: usage 'factions sources <tag>'"; return true; }
                    BrainDoc b = Store.LoadBrain(arg);
                    if (b == null) { result = "sources: '" + arg + "' not registered."; return true; }
                    List<SourceDoc> srcs = b.Sources ?? new List<SourceDoc>();
                    var sb = new StringBuilder("sources [" + b.Tag + "]: " + srcs.Count + " source(s)");
                    foreach (SourceDoc s in srcs)
                        sb.Append("\n  " + s.Id + " pos(" + s.X.ToString("0.#") + "," + s.Y.ToString("0.#") + ","
                                  + s.Z.ToString("0.#") + ")km str=" + s.Strength.ToString("0.00") + " r=" + s.Radius.ToString("0"));
                    result = sb.ToString();
                    return true;
                }
                case "scanner":
                {
                    result = HandleScanner(t);
                    return true;
                }
                case "forget":
                {
                    if (string.IsNullOrEmpty(arg)) { result = "forget: usage 'factions forget <tag>'"; return true; }
                    result = ForgetFaction(arg);
                    return true;
                }
                case "sim":
                {
                    result = HandleSim(t);
                    return true;
                }
                case "fuel":
                {
                    result = HandleFuel(t);
                    return true;
                }
                case "reload":
                {
                    // Also a chat command, answered here as well: a verb the bridge does not handle
                    // falls through to Torch's command path, where the null server-player fails
                    // the admin check.
                    result = ReloadTuning();
                    return true;
                }
                case "ignore":
                {
                    result = HandleIgnore(t);
                    return true;
                }
                case "reclaim":
                {
                    result = HandleReclaim(t);
                    return true;
                }
                case "givenode":
                {
                    if (t.Length < 4) { result = "givenode: usage 'factions givenode <tag> <nodeId>'"; return true; }
                    result = _runtime == null ? "givenode: runtime not started (no session loaded)."
                                              : _runtime.AdminGiveNode(t[2], t[3]);
                    return true;
                }
                case "removenode":
                {
                    if (t.Length < 3) { result = "removenode: usage 'factions removenode <nodeId>'"; return true; }
                    result = _runtime == null ? "removenode: runtime not started (no session loaded)."
                                              : _runtime.AdminRemoveNode(t[2]);
                    return true;
                }
                case "recreatenode":
                {
                    double x, y, z;
                    if (t.Length < 7
                        || !double.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        || !double.TryParse(t[5], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        || !double.TryParse(t[6], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    {
                        result = "recreatenode: usage 'factions recreatenode <tag> <nodeId> <xKm> <yKm> <zKm> [found]'";
                        return true;
                    }
                    bool found = t.Length > 7 && string.Equals(t[7], "found", StringComparison.OrdinalIgnoreCase);
                    result = _runtime == null ? "recreatenode: runtime not started (no session loaded)."
                                              : _runtime.AdminRecreateNode(t[2], t[3], new Vec3(x, y, z), found);
                    return true;
                }
                case "assemble":
                {
                    result = HandleAssemble(t);
                    return true;
                }
                case "reconcileterritory":
                {
                    result = HandleReconcileTerritory(t);
                    return true;
                }
                case "reclassify":
                {
                    result = HandleReclassify(t);
                    return true;
                }
                case "densifytopology":
                {
                    result = HandleDensifyTopology(t);
                    return true;
                }
                case "backfillabandonedutc":
                {
                    result = HandleBackfillAbandonedUtc(t);
                    return true;
                }
                case "clearstranding":
                {
                    result = HandleClearStranding(t);
                    return true;
                }
                case "debris":
                {
                    result = HandleDebris(t);
                    return true;
                }
                case "fight":
                {
                    result = _runtime == null ? "fight: runtime not started (no session loaded)." : _runtime.DescribeFightSites();
                    return true;
                }
                case "grids":
                {
                    result = HandleGrids(t);
                    return true;
                }
                case "migratestationprefabs":
                {
                    result = HandleMigrateStationPrefabs(t);
                    return true;
                }
                case "cleardeepspaceflags":
                {
                    result = HandleClearDeepSpaceFlags(t);
                    return true;
                }
                case "fixstationownership":
                {
                    result = HandleFixStationOwnership(t);
                    return true;
                }
                case "fixstationdrift":
                {
                    result = HandleFixStationDrift(t);
                    return true;
                }
                case "node":
                {
                    result = HandleNode(t);
                    return true;
                }
                case "cancelbuild":
                {
                    result = HandleCancelBuild(t);
                    return true;
                }
                case "decommission":
                {
                    result = HandleDecommission(t);
                    return true;
                }
                case "prefab":
                {
                    result = HandlePrefabBan(t);
                    return true;
                }
                case "pausebuild":
                {
                    result = HandlePauseBuild(t);
                    return true;
                }
                case "buildpriority":
                {
                    result = HandleBuildPriority(t);
                    return true;
                }
                case "orders":
                {
                    result = HandleStandingOrders(t);
                    return true;
                }
                case "fleet":
                {
                    result = HandleFleetDoctrine(t);
                    return true;
                }
                case "store":
                {
                    result = HandleShelf(t);
                    return true;
                }
                case "diplomacy":
                {
                    // 'factions diplomacy <playerTag> [verb] [npcTag] [kind]' -- admin twin of the
                    // player's '!factions diplomacy', with no leader check.
                    result = _runtime != null ? _runtime.LeaderDiplomacyAdmin(t)
                                              : "diplomacy: runtime not started (no session loaded).";
                    return true;
                }
                case "develop":
                {
                    result = HandleDevelop(t);
                    return true;
                }
                case "knowledge":
                {
                    // `arg` is t[2]; t[1] is the verb itself. Passing t[1] here handed
                    // DescribeKnowledge the literal string "knowledge", so every call answered
                    // "no such faction 'knowledge'" -- and the length guard could never fire,
                    // because reaching this case already means t.Length >= 2.
                    if (string.IsNullOrEmpty(arg)) { result = "knowledge: usage 'factions knowledge <tag>'"; return true; }
                    if (_runtime == null) { result = "knowledge: runtime not started."; return true; }
                    result = _runtime.DescribeKnowledge(arg);
                    return true;
                }
                case "gravitydump":
                {
                    result = HandleGravityDump(t);
                    return true;
                }
                case "gravityprobe":
                {
                    result = HandleGravityProbe(t);
                    return true;
                }
                case "rockinspect":
                {
                    if (_runtime == null) { result = "rockinspect: runtime not started."; return true; }
                    result = _runtime.InspectFeatureRock(PlayerPositionsKm(),
                        string.IsNullOrEmpty(arg) ? null : arg);
                    return true;
                }
                case "rockpurge":
                {
                    if (_runtime == null) { result = "rockpurge: runtime not started."; return true; }
                    // Edited rock is a player's work and is left alone unless named explicitly;
                    // SE's own voxel reverter is what reclaims that, not us.
                    result = _runtime.PurgeFeatureRock(
                        string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase));
                    return true;
                }
                case "debug":
                {
                    result = ToggleDebug(arg);
                    return true;
                }
                case "serverperf":
                {
                    result = _serverPerf != null
                        ? _serverPerf.Describe()
                        : "Server perf: not running. Set Enabled true in " + ServerPerfConfig.FileName
                          + " (instance dir, per Torch root) and restart. Physics step hook "
                          + (PhysicsStepPatches.Bound ? "is bound." : "is NOT bound.");
                    return true;
                }
                case "rockcost":
                {
                    // The measurement FAC-090 asks for before anyone decides the feature is worth
                    // it. On demand rather than on a counter: it serializes real octrees, which is
                    // far too expensive for a spawn path.
                    result = FeatureRockCostReport.Run(t);
                    return true;
                }
                case "refitapply":
                {
                    result = HandleRefitApply(t);
                    return true;
                }
                case "repinspect":
                {
                    // The read half of the reputation model, and the only way to see a player<->
                    // faction standing on this server at all. Optional argument filters to one tag.
                    if (_reputationManager == null) { result = "repinspect: runtime not started."; return true; }
                    result = _reputationManager.Report(string.IsNullOrEmpty(arg) ? null : arg);
                    return true;
                }
                default:
                    return false; // unknown factions verb -> fall back to Torch
            }
        }

        /// <summary>Toggles a debug-logging flag and reports the resulting state. Shared by the
        /// <c>!factions debug</c> chat command and the bridge verb of the same name, so an operator
        /// gets identical behaviour whichever way the server is reached — the chat command alone
        /// is unreachable from the file bridge, whose commands run with no server player and fail
        /// the admin permission check silently.
        /// <para>The high-volume kinds — <c>uplink</c> and <c>flight</c> — are deliberately
        /// excluded from the no-argument "toggle everything" path: each is off by default and costs
        /// enough log volume to be worth naming explicitly.</para></summary>
        /// <summary>
        /// Names every debug trace and whether it is on, once per session load.
        ///
        /// <para><b>Why this is worth a line.</b> Every toggle is a plain static, so a plugin reload
        /// silently clears all of them. The failure that produces is SILENCE in the log — and
        /// silence is indistinguishable from the feature you were watching having stopped. It cost
        /// a live session fifteen minutes on 2026-08-25: `ROCK_WIRE` stopped at a restart and was
        /// briefly read as the feature under test breaking (FAC-095).</para>
        ///
        /// <para>Deliberately not persisted. A toggle that survived a restart would be a tuning key
        /// with a migration, and leaving a trace on by accident across reboots is its own noise
        /// problem — the debug traces exist precisely because the Torch log is a diagnostic channel
        /// worth keeping quiet. Reporting the reset costs one line; preventing it costs a contract.</para>
        /// </summary>
        private static void LogDebugToggleState()
        {
            var on = new List<string>();
            if (VanillaFleetSpawner.DebugTrade)   on.Add("trade");
            if (VanillaFleetSpawner.DebugRaid)    on.Add("raid");
            if (VanillaFleetSpawner.DebugGuard)   on.Add("guard");
            if (VanillaFleetSpawner.DebugPatrol)  on.Add("patrol");
            if (VanillaFleetSpawner.DebugFerry)   on.Add("ferry");
            if (UplinkManager.DebugTraffic)       on.Add("uplink");
            if (FactionRuntime.DebugSensing)     on.Add("sense");
            if (FactionRuntime.DebugSurvey)       on.Add("survey");
            if (FleetFlightController.DebugFlight) on.Add("flight");
            if (FeatureRockMaterializer.DebugEnabled) on.Add("rock");
            if (PlayerRockManager.DebugEnabled)   on.Add("rockwire");
            if (StationStoreManager.DebugEnabled) on.Add("store");
            foreach (var trace in DebugTraces.All()) if (trace.Value) on.Add(trace.Key);
            if (PlayerReputationManager.DebugEnabled) on.Add("reputation");
            if (PlayerReputationManager.DebugRepSync) on.Add("repsync");
            if (DebugFrameGap)                    on.Add("frame");
            if (ServerPerfMonitor.DebugLog)       on.Add("serverperf");
            if (EntityCostPatches.Enabled)        on.Add("entitycost");
            if (CrewFooting.Debug)                on.Add("footing");
            if (FaunaControl.DebugEnabled)        on.Add("fauna");

            Log.Info(on.Count == 0
                ? "Debug traces: all OFF (they are per-process and were cleared by this reload). "
                + "Re-enable with 'factions debug <kind>': trade, raid, guard, patrol, ferry, uplink, "
                + "sense, flight, footing, rock, rockwire, store, reputation, repsync, frame, survey, fauna, "
                + "produced, itemxfer, upkeep, itemdemand, shipevents, investment, bankdenials, "
                + "braindecisions, slots, grids, fuel, munitions, contracts, econtick, tactics, landing, trespass. A trace you expected and do not see is this, not a fault."
                : "Debug traces ON: " + string.Join(", ", on.ToArray())
                + " (per-process; cleared by the next reload).");
        }

        public static string ToggleDebug(string kind)
        {
            if (string.IsNullOrEmpty(kind))
            {
                // Toggle all together, driven by current Trade flag (backwards compat).
                bool next = !VanillaFleetSpawner.DebugTrade;
                StationMaterializer.DebugEnabled  = next;
                VanillaFleetSpawner.DebugTrade    = next;
                VanillaFleetSpawner.DebugRaid     = next;
                VanillaFleetSpawner.DebugGuard    = next;
                VanillaFleetSpawner.DebugPatrol   = next;
                VanillaFleetSpawner.DebugFerry    = next;
            }
            else if (string.Equals(kind, "trade", StringComparison.OrdinalIgnoreCase))
            {
                VanillaFleetSpawner.DebugTrade = !VanillaFleetSpawner.DebugTrade;
            }
            else if (string.Equals(kind, "raid", StringComparison.OrdinalIgnoreCase))
            {
                VanillaFleetSpawner.DebugRaid = !VanillaFleetSpawner.DebugRaid;
            }
            else if (string.Equals(kind, "guard", StringComparison.OrdinalIgnoreCase))
            {
                VanillaFleetSpawner.DebugGuard = !VanillaFleetSpawner.DebugGuard;
            }
            else if (string.Equals(kind, "patrol", StringComparison.OrdinalIgnoreCase))
            {
                VanillaFleetSpawner.DebugPatrol = !VanillaFleetSpawner.DebugPatrol;
            }
            else if (string.Equals(kind, "ferry", StringComparison.OrdinalIgnoreCase))
            {
                VanillaFleetSpawner.DebugFerry = !VanillaFleetSpawner.DebugFerry;
            }
            else if (string.Equals(kind, "sense", StringComparison.OrdinalIgnoreCase))
            {
                FactionRuntime.DebugSensing = !FactionRuntime.DebugSensing;
                return "Player-sensing trace: " + (FactionRuntime.DebugSensing ? "ON" : "OFF")
                       + " (SENSE_ACQUIRED / SENSE_LOST per faction, on transition only).";
            }
            else if (string.Equals(kind, "uplink", StringComparison.OrdinalIgnoreCase))
            {
                UplinkManager.DebugTraffic = !UplinkManager.DebugTraffic;
                return "Uplink traffic logging: " + (UplinkManager.DebugTraffic ? "ON" : "OFF")
                       + " (link negotiation is always logged).";
            }
            else if (string.Equals(kind, "rock", StringComparison.OrdinalIgnoreCase))
            {
                FeatureRockMaterializer.DebugEnabled = !FeatureRockMaterializer.DebugEnabled;
                return "Feature-rock spawn/despawn trace (ROCK_*, Torch log): "
                       + (FeatureRockMaterializer.DebugEnabled ? "ON" : "OFF")
                       + " (one line per object; off by default because it scales with rock, not with events).";
            }
            else if (string.Equals(kind, "rockwire", StringComparison.OrdinalIgnoreCase))
            {
                PlayerRockManager.DebugEnabled = !PlayerRockManager.DebugEnabled;
                return "Client-derived rock delta trace (ROCK_WIRE, Torch log): "
                       + (PlayerRockManager.DebugEnabled ? "ON" : "OFF")
                       + " -- one line per client per diff. This is the only view of what a linked "
                       + "client was told to build; the rock it derives leaves no other server-side trace.";
            }
            else if (DebugTraces.TryToggle(kind, out bool traceOn, out string traceWhat))
            {
                // The eight former tuning Log* filters plus SLOT_GATE, all on one ephemeral
                // mechanism -- see Factions.Core.DebugTraces for why they stopped being settings.
                return "Trace '" + kind.ToLowerInvariant() + "': " + (traceOn ? "ON" : "OFF")
                       + (traceWhat != null ? " -- " + traceWhat : "")
                       + ". Per-process: a plugin reload clears it.";
            }
            else if (string.Equals(kind, "fauna", StringComparison.OrdinalIgnoreCase))
            {
                FaunaControl.DebugEnabled = !FaunaControl.DebugEnabled;
                return "Recipe fauna trace (FAUNA_ASK, Torch log): "
                       + (FaunaControl.DebugEnabled ? "ON" : "OFF")
                       + " -- one line each time SE asks an authored body which animals spawn: the"
                       + " outcome (authored, native, silent) and the site it was matched at. SE also asks"
                       + " while pathing spiders, so a line is a question, not a wave. "
                       + FaunaControl.Describe();
            }
            else if (string.Equals(kind, "survey", StringComparison.OrdinalIgnoreCase))
            {
                FactionRuntime.DebugSurvey = !FactionRuntime.DebugSurvey;
                return "Deposit-survey pacing trace (SURVEY_PASS, Factions log): "
                       + (FactionRuntime.DebugSurvey ? "ON" : "OFF")
                       + " -- one line per completed rotation: how many nodes are still being"
                       + " surveyed, how many the ore seam refused and parked, and the per-visit probe"
                       + " budget derived from SurveyDurationHours. A parked count that is a large"
                       + " share of candidates means that much of the estate cannot be sampled where"
                       + " it stands.";
            }
            else if (string.Equals(kind, "frame", StringComparison.OrdinalIgnoreCase))
            {
                DebugFrameGap = !DebugFrameGap;
                return "Frame-gap trace (TICK_STALL / TICK_WINDOW, Torch log): "
                       + (DebugFrameGap ? "ON" : "OFF")
                       + " -- wall time between consecutive Update() calls, reported beside what this "
                       + "plugin spent in the frame. /api/perf and the frame buckets only measure time "
                       + "INSIDE Update(), so a stall between two calls is invisible to all of them. "
                       + "A large gap with a small 'ours' is a stall this plugin did not cause.";
            }
            else if (string.Equals(kind, "serverperf", StringComparison.OrdinalIgnoreCase))
            {
                ServerPerfMonitor.DebugLog = !ServerPerfMonitor.DebugLog;
                return "Server-thread telemetry line (SERVER_PERF, Torch log): "
                       + (ServerPerfMonitor.DebugLog ? "ON" : "OFF")
                       + " -- one line per window: tick rate, simulation ratio, the frame-gap "
                       + "distribution, this plugin's share, the Havok step, and the grid census. "
                       + "Collection and the InfluxDB push are governed by "
                       + ServerPerfConfig.FileName + ", not by this toggle; this only prints the "
                       + "window that was collected anyway. `factions perf server` reports the state.";
            }
            else if (string.Equals(kind, "entitycost", StringComparison.OrdinalIgnoreCase))
            {
                if (!EntityCostPatches.Bound)
                    return "Per-entity update cost: NOT AVAILABLE -- the hooks are only patched when "
                           + ServerPerfConfig.FileName + " has EntityCostEnabled true at boot, because "
                           + "they wrap every entity update SE dispatches. Set it and restart.";
                EntityCostPatches.Enabled = !EntityCostPatches.Enabled;
                return "Per-entity update cost (entity_cost / entity_cost_type series): "
                       + (EntityCostPatches.Enabled ? "ON" : "OFF")
                       + " -- every entity update SE's orchestrator dispatches, serial and parallel, "
                       + "attributed to its grid and to its type. Coverage: " + EntityCostPatches.Coverage
                       + ". Programmable-block script cost (pb_cost series) follows the same switch"
                       + (PbCostPatches.Bound ? "." : ", but is not bound.");
            }
            else if (string.Equals(kind, "reputation", StringComparison.OrdinalIgnoreCase))
            {
                PlayerReputationManager.DebugEnabled = !PlayerReputationManager.DebugEnabled;
                return "Player trade-reputation trace (REP_TRADE, Torch log): "
                       + (PlayerReputationManager.DebugEnabled ? "ON" : "OFF")
                       + " -- one line per attributed store transaction, showing the credited value "
                       + "and the remainder carried toward the next step. A transaction that GRANTS a "
                       + "step is logged either way; this toggle adds the ones that only accrue.";
            }
            else if (string.Equals(kind, "repsync", StringComparison.OrdinalIgnoreCase))
            {
                PlayerReputationManager.DebugRepSync = !PlayerReputationManager.DebugRepSync;
                return "Member reputation display trace (REPSYNC_*, Torch log): "
                       + (PlayerReputationManager.DebugRepSync ? "ON" : "OFF")
                       + " -- REPSYNC_SEED when a member's own record is taken from SE, REPSYNC_PROJECT "
                       + "when their SE reputation is set to the faction standing, REPSYNC_CAPTURE when "
                       + "their conduct moves the stored record, REPSYNC_WRITEBACK when they leave, "
                       + "REPSYNC_RESEND_MARKED / REPSYNC_RESEND when a vanilla relation broadcast forces "
                       + "members' values to be sent to clients again, and REPSYNC_PASS per economy "
                       + "quantum that changed anything. Per-process; a reload clears it.";
            }
            else if (string.Equals(kind, "store", StringComparison.OrdinalIgnoreCase))
            {
                StationStoreManager.DebugEnabled = !StationStoreManager.DebugEnabled;
                return "Station store listing trace (STORE_LIST, Torch log): "
                       + (StationStoreManager.DebugEnabled ? "ON" : "OFF")
                       + " -- one line per populated station per refresh, carrying offers, orders and "
                       + "how many OrdersList candidates were skipped for want of a live faction demand. "
                       + "That last count is the only view of the buy-side filter: the store list itself "
                       + "is served to clients and never logged.";
            }
            else if (string.Equals(kind, "footing", StringComparison.OrdinalIgnoreCase))
            {
                CrewFooting.Debug = !CrewFooting.Debug;
                return "Crew footing trace (Torch log): " + (CrewFooting.Debug ? "ON" : "OFF")
                       + " -- CREW_FOOTING_LOST when a crewman in zero gravity has been off his hull's deck for 4"
                       + " frames (speed away from the floor, the hull's largest one-frame velocity change in the"
                       + " 15 frames before and where it fell in the 60-frame cycle), and CREW_FOOTING_END with how"
                       + " it ended: back, landed, departed or seated_or_driven.";
            }
            else if (string.Equals(kind, "flight", StringComparison.OrdinalIgnoreCase))
            {
                FleetFlightController.DebugFlight = !FleetFlightController.DebugFlight;
                return "Flight trace (Torch log): "
                       + (FleetFlightController.DebugFlight ? "ON" : "OFF")
                       + " -- FLIGHT_TRACE every 2 s per flying grid, and the per-flight ENGAGE, RELEASE,"
                       + " STOPPED, STOPPOINT, LAUNCHDIR, LAUNCH_DONE, HANDBACK, AVOID and AVOID_CLEAR lines. FLIGHT_ABORT,"
                       + " FLIGHT_HANDBACK_FAILED and FLIGHT_HANDBACK_GIVEUP are always logged.";
            }
            else
            {
                return "Unknown kind '" + kind + "'. Use: trade, raid, guard, patrol, ferry, uplink, flight, footing, rock, rockwire,"
                       + " store, reputation, frame, or omit for all fleet kinds.";
            }

            return "Fleet debug: Trade=" + (VanillaFleetSpawner.DebugTrade   ? "ON" : "OFF")
                 + " Raid="              + (VanillaFleetSpawner.DebugRaid    ? "ON" : "OFF")
                 + " Guard="             + (VanillaFleetSpawner.DebugGuard   ? "ON" : "OFF")
                 + " Patrol="            + (VanillaFleetSpawner.DebugPatrol  ? "ON" : "OFF")
                 + " Ferry="             + (VanillaFleetSpawner.DebugFerry   ? "ON" : "OFF");
        }

        private string HandleScanner(string[] t)
        {
            if (_playerScannerManager == null) return "scanner: runtime not started.";
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "";
            if (sub == "profile")
                return _playerScannerManager.Profile(t.Length > 3 ? t[3] : null);
            if (sub == "level")
            {
                int level;
                if (t.Length < 5 || !int.TryParse(t[4], out level))
                    return "scanner: usage 'factions scanner level <steamId> <level>'.";
                return _playerScannerManager.SetLevel(t[3], level);
            }
            if (sub == "probe")
            {
                double x, y, z;
                bool large = t.Length > 3 && t[3].Equals("large", StringComparison.OrdinalIgnoreCase);
                bool small = t.Length > 3 && t[3].Equals("small", StringComparison.OrdinalIgnoreCase);
                if ((!large && !small) || t.Length < 7
                    || !double.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !double.TryParse(t[5], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    || !double.TryParse(t[6], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    return "scanner: usage 'factions scanner probe <small|large> <xKm> <yKm> <zKm>'.";
                return _playerScannerManager.Probe(large, new Vec3(x, y, z));
            }
            return "scanner: use profile <steamId>, level <steamId> <level>, or probe <small|large> <xKm> <yKm> <zKm>.";
        }

        /// <summary>Operator controls for the economic simulation (driven via the command bridge).</summary>
        private string HandleSim(string[] t)
        {
            if (_runtime == null) return "sim: runtime not started (no session loaded).";
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                    return _runtime.Status();
                case "pause":
                    _runtime.Pause();
                    return "sim: paused.";
                case "resume":
                    _runtime.Resume();
                    return "sim: resumed @ " + _runtime.Speed.ToString("0.###") + " tick/s.";
                case "reload":
                    _runtime.Reload();
                    return "sim: reloaded " + _runtime.FactionCount + " faction(s) from disk.";
                case "speed":
                {
                    double s;
                    if (t.Length > 3 && double.TryParse(t[3], NumberStyles.Any, CultureInfo.InvariantCulture, out s))
                    {
                        _runtime.SetSpeed(s);
                        return "sim: speed = " + _runtime.Speed.ToString("0.###") + " tick/s.";
                    }
                    return "sim: usage 'factions sim speed <ticksPerSecond>'";
                }
                case "step":
                {
                    int n;
                    if (t.Length > 3 && int.TryParse(t[3], out n) && n > 0)
                    {
                        _runtime.Step(n);
                        return "sim: stepped " + n + " tick(s); now simTick=" + _runtime.SimTick + ".";
                    }
                    return "sim: usage 'factions sim step <n>'";
                }
                case "advance":
                {
                    double seconds;
                    if (t.Length > 3
                        && double.TryParse(t[3], NumberStyles.Any, CultureInfo.InvariantCulture, out seconds)
                        && seconds > 0)
                    {
                        if (_runtime.Speed > 0)
                            return "sim: pause before advancing deterministic time.";
                        if (_runtime.AdvanceTime(seconds))
                            return "sim: advanced " + seconds.ToString("0.###", CultureInfo.InvariantCulture)
                                + " active second(s); activeUtc=" + _runtime.ActiveUtc.ToString("u") + ".";
                    }
                    return "sim: usage 'factions sim advance <seconds>'";
                }
                case "seed":
                {
                    long amt;
                    if (t.Length > 4 && long.TryParse(t[4], out amt))
                        return _runtime.SeedTreasuryFor(t[3], amt)
                            ? "sim: seeded " + t[3] + " treasury = " + amt + "."
                            : "sim: faction '" + t[3] + "' not loaded.";
                    return "sim: usage 'factions sim seed <tag> <amount>'";
                }
                case "cleardebt":
                {
                    if (t.Length > 3 && string.Equals(t[3], "all", StringComparison.OrdinalIgnoreCase))
                    {
                        int cleared = _runtime.ClearAllDebt();
                        return "sim: cleared debt for " + cleared + " faction(s).";
                    }
                    if (t.Length > 3)
                        return _runtime.ClearDebtFor(t[3])
                            ? "sim: cleared debt for " + t[3] + "."
                            : "sim: faction '" + t[3] + "' not loaded.";
                    return "sim: usage 'factions sim cleardebt <tag>|all'";
                }
                case "perf":
                    return _runtime.GetPerfSummary();
                case "strandedstock":
                    return _runtime.StrandedStockReport();
                case "addship":
                {
                    if (t.Length > 5)
                    {
                        int cnt = 1;
                        if (t.Length > 6) int.TryParse(t[6], out cnt);
                        if (cnt <= 0) cnt = 1;
                        return _runtime.AddShipStock(t[3], t[4], t[5], cnt)
                            ? "sim: added " + cnt + "x " + t[5] + " to " + t[4] + " (" + t[3] + ")."
                            : "sim: faction '" + t[3] + "' not loaded, or '" + t[4] + "' not found/not owned by it.";
                    }
                    return "sim: usage 'factions sim addship <tag> <nodeId> <prefabId> [count]'";
                }
                default:
                    return "sim: subcommands — status | speed <x> | pause | resume | step <n> | advance <seconds> | seed <tag> <amt> | cleardebt <tag>|all | reload | perf | strandedstock | addship <tag> <nodeId> <prefabId> [count]";
            }
        }

        /// <summary>Manage the ignore-list (NPC factions excluded from discovery/sim — e.g. animal factions
        /// like spiders' SPID). 'add' also forgets the faction so it leaves the registry + live sim now.</summary>
        private string HandleIgnore(string[] t)
        {
            FactionsConfig cfg = Store.LoadConfig();
            if (cfg.IgnoredTags == null) cfg.IgnoredTags = new List<string>();
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "list";
            string tag = t.Length > 3 ? t[3] : null;
            switch (sub)
            {
                case "list":
                    return "ignore: " + (cfg.IgnoredTags.Count == 0 ? "(none)" : string.Join(", ", cfg.IgnoredTags));
                case "add":
                {
                    if (string.IsNullOrEmpty(tag)) return "ignore: usage 'factions ignore add <tag>'";
                    if (!cfg.IgnoredTags.Any(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase)))
                        cfg.IgnoredTags.Add(tag);
                    Store.SaveConfig(cfg);
                    if (_runtime != null) _runtime.Flush();   // persist live state BEFORE editing the registry...
                    List<IndexEntry> idx = Store.LoadIndex();
                    int removed = idx.RemoveAll(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));
                    if (removed > 0) { Store.SaveIndex(idx); Store.DeleteBrain(tag); }
                    if (_runtime != null) _runtime.Load();    // ...then rebuild from disk (Reload would re-flush + resurrect it)
                    return "ignore: now ignoring '" + tag + "'" + (removed > 0 ? " (forgotten from the registry)." : ".");
                }
                case "remove":
                {
                    if (string.IsNullOrEmpty(tag)) return "ignore: usage 'factions ignore remove <tag>'";
                    int n = cfg.IgnoredTags.RemoveAll(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
                    Store.SaveConfig(cfg);
                    return n > 0 ? "ignore: no longer ignoring '" + tag + "' (run 'factions init' to register it)."
                                 : "ignore: '" + tag + "' was not in the ignore-list.";
                }
                default:
                    return "ignore: subcommands — list | add <tag> | remove <tag>";
            }
        }

        /// <summary>Force-respawn an already-spawned station (e.g. to pick up a fresh orientation
        /// fix) without waiting for a player to wander near it. See
        /// <see cref="StationMaterializer.ForceRespawn"/>.</summary>
        /// <summary>FAC-443 test: assemble one prefab <c>[km]</c> (default 1) off the first online player,
        /// owned by an NPC founder, on demand: 'factions assemble &lt;prefabId&gt; [km]'.</summary>
        private string HandleAssemble(string[] t)
        {
            const string usage = "assemble: usage 'factions assemble <prefabId> [km] [at <nodeId>] [oneframe]'";
            if (t.Length < 3) return usage;
            if (_fleetSpawner == null) return "assemble: no fleet spawner (no session loaded, or dry run).";
            double km = 1.0;
            string nodeId = null;
            bool oneFrame = false;
            for (int i = 3; i < t.Length; i++)
            {
                if (string.Equals(t[i], "oneframe", StringComparison.OrdinalIgnoreCase)) oneFrame = true;
                else if (string.Equals(t[i], "at", StringComparison.OrdinalIgnoreCase) && i + 1 < t.Length) nodeId = t[++i];
                else if (!double.TryParse(t[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out km)) return usage;
            }
            VRageMath.Vector3D origin;
            if (nodeId != null)
            {
                SpatialNode node;
                if (_runtime == null || !_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node))
                    return "assemble: no node '" + nodeId + "'.";
                origin = new VRageMath.Vector3D(node.Position.X * 1000.0, node.Position.Y * 1000.0, node.Position.Z * 1000.0);
            }
            else
            {
                CachedPlayer[] players = _cachedPlayers;
                if (players == null || players.Length == 0)
                    return "assemble: no player online to place it near; name a node with 'at <nodeId>'.";
                origin = players[0].Pos;
            }
            VRageMath.Vector3D at = origin + new VRageMath.Vector3D(km * 1000.0, 0, 0);
            VRageMath.Vector3D? free = MyEntities.FindFreePlaceCustom(at, 150f);
            // Built by an NPC faction's founder, as a real hull is: SE clears a grid's NPC flag at init
            // unless some block was built by an NPC identity, and SE's trash removal deletes an unflagged
            // small grid within a second (2026-10-04, the first Clipper test: its 2-block piston-top grid).
            long npcOwner = 0;
            if (MySession.Static != null && MySession.Static.Factions != null)
                foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                {
                    MyFaction f = kv.Value;
                    if (f != null && f.FounderId != 0 && IsEffectivelyNpc(f)) { npcOwner = f.FounderId; break; }
                }
            return _fleetSpawner.AssembleForTest(t[2], free ?? at, npcOwner, oneFrame);
        }

        /// <summary>Bridge verb 'factions hullprobe &lt;entityId&gt; [refresh]': why a hull can or cannot
        /// push (<see cref="HullThrustProbe"/>). With 'refresh', asks SE to rebuild the hull's conveyor
        /// and thrust groups first and probes again at 1 and 3 s as <c>HULL_PROBE</c> lines.</summary>
        private string HandleHullProbe(string[] t)
        {
            long eid;
            if (t.Length < 3 || !long.TryParse(t[2], out eid)) return "hullprobe: usage 'factions hullprobe <entityId> [refresh]'";
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(eid, out grid, false) || grid == null) return "hullprobe: no grid " + eid + ".";
            string before = HullThrustProbe.Describe(grid);
            if (t.Length < 4 || !string.Equals(t[3], "refresh", StringComparison.OrdinalIgnoreCase))
                return "hullprobe " + eid + ": " + before;
            string did = HullThrustProbe.Refresh(grid);
            if (_fleetSpawner != null) _fleetSpawner.ScheduleHullProbes(eid, "refreshed", 1, 3);
            return "hullprobe " + eid + ": before " + before + "; " + did + "; HULL_PROBE lines follow at 1 and 3 s.";
        }

        /// <summary>Bridge and chat verb 'factions dampprobe [entityId|grid name]': why a grid under dampeners
        /// does not hold still in gravity (<see cref="DampenerProbe"/>). From chat with no target it takes the
        /// grid the caller controls, else the nearest one within <see cref="DampProbeReachM"/>; the averaged
        /// line is then also sent back to the caller.
        /// <para>With <c>trace [seconds]</c> it records the grid frame by frame instead
        /// (<see cref="DampenerProbe.StartTrace"/>), and when the caller is a linked client it asks that client to
        /// record its own prediction corrections over the same seconds, under the same stamp.</para></summary>
        internal string HandleDampProbe(string[] t, VRage.Game.ModAPI.IMyPlayer caller)
        {
            var words = new List<string>();
            for (int i = 2; i < t.Length; i++) words.Add(t[i]);
            int traceAt = words.FindIndex(w => string.Equals(w, "trace", StringComparison.OrdinalIgnoreCase));
            int seconds = DampenerProbe.TraceDefaultSeconds;
            if (traceAt >= 0)
            {
                int parsed;
                if (traceAt + 1 < words.Count && int.TryParse(words[traceAt + 1], out parsed))
                {
                    seconds = parsed;
                    words.RemoveAt(traceAt + 1);
                }
                words.RemoveAt(traceAt);
            }
            string target = words.Count > 0 ? string.Join(" ", words) : null;
            MyCubeGrid grid = ResolveProbeGrid(target, caller);
            if (grid == null)
                return target == null
                    ? "dampprobe: usage 'factions dampprobe [<entityId|grid name>] [trace [seconds]]', or from chat while seated in or within "
                      + DampProbeReachM + " m of the grid."
                    : "dampprobe: no grid '" + target + "'.";
            ulong replyTo = caller != null ? caller.SteamUserId : 0UL;
            if (traceAt < 0) return DampenerProbe.Start(grid, replyTo);

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            seconds = Math.Max(1, Math.Min(DampenerProbe.TraceMaxSeconds, seconds));
            string clientNote = "no client trace (not from a player)";
            if (replyTo != 0)
            {
                clientNote = _uplinkManager != null
                             && _uplinkManager.Send(replyTo, Factions.Wire.FactionsWire.WriteNetTraceStart(grid.EntityId, seconds * 60, stamp))
                    ? "client trace requested from " + replyTo
                    : "no client trace (" + replyTo + " is not linked)";
            }
            return DampenerProbe.StartTrace(grid, replyTo, seconds, stamp, clientNote);
        }

        private const double DampProbeReachM = 300;

        private static MyCubeGrid ResolveProbeGrid(string target, VRage.Game.ModAPI.IMyPlayer caller)
        {
            MyCubeGrid grid;
            long eid;
            if (target != null && long.TryParse(target, out eid))
                return MyEntities.TryGetEntityById(eid, out grid, false) ? grid : null;
            if (target != null)
            {
                foreach (VRage.Game.Entity.MyEntity e in MyEntities.GetEntities())
                {
                    grid = e as MyCubeGrid;
                    if (grid != null && !grid.Closed && string.Equals(grid.DisplayName, target, StringComparison.OrdinalIgnoreCase))
                        return grid;
                }
                return null;
            }
            if (caller == null) return null;
            var seat = caller.Controller != null ? caller.Controller.ControlledEntity as MyCubeBlock : null;
            if (seat != null && seat.CubeGrid != null) return seat.CubeGrid;
            if (caller.Character == null) return null;
            VRageMath.Vector3D at = caller.GetPosition();
            MyCubeGrid best = null;
            double bestD2 = DampProbeReachM * DampProbeReachM;
            foreach (VRage.Game.Entity.MyEntity e in MyEntities.GetEntities())
            {
                grid = e as MyCubeGrid;
                if (grid == null || grid.Closed || grid.IsStatic || grid.Physics == null) continue;
                VRageMath.BoundingBoxD box = grid.PositionComp.WorldAABB;
                double d2 = VRageMath.Vector3D.DistanceSquared(VRageMath.Vector3D.Clamp(at, box.Min, box.Max), at);
                if (d2 < bestD2) { bestD2 = d2; best = grid; }
            }
            return best;
        }

        private string HandleReclaim(string[] t)
        {
            if (t.Length < 4) return "reclaim: usage 'factions reclaim <tag> <nodeId>'";
            if (_runtime == null) return "reclaim: runtime not started (no session loaded).";
            return _runtime.ForceStationRespawn(t[2], t[3]);
        }

        /// <summary>Bridge verb 'factions reconcileterritory [apply]'. This is a one-shot UNKN-only
        /// recovery: it replaces all persisted and live UNKN sources with a bounded catalogue read
        /// directly from the active Sandbox.sbc. It never calls the general world scanner, never
        /// enumerates MyFaction.Stations, and never writes or reloads a non-UNKN brain.</summary>
        private string HandleReconcileTerritory(string[] t)
        {
            if (_runtime == null || MySession.Static == null)
                return "reconcileterritory: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            BrainDoc unkn = Store.LoadBrain(NeutralFaction.Tag);
            if (unkn == null || !string.Equals(unkn.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase))
                return "reconcileterritory: scope=UNKN-only nonUnknWrites=0 blocked=UNKN brain missing.";

            List<string> eligibleIds = TerritoryReconciliationPlan.UnknEconomyRecoveryIds(unkn);
            string checkpointPath = Path.Combine(MySession.Static.CurrentPath, "Sandbox.sbc");
            UnknSaveStationDiscovery.Result discovery = UnknSaveStationDiscovery.Discover(checkpointPath, eligibleIds,
                _prefabBookValues != null ? new Func<string, long>(_prefabBookValues.StationBookValue) : null,
                _prefabBookValues != null
                    ? new Func<string, List<WeaponBand>>(prefabId => new List<WeaponBand>(_prefabBookValues.StationWeaponBands(prefabId)))
                    : null);
            string candidateIds = string.Join(",", discovery.Candidates.Select(s => s.Id));
            string missingIds = string.Join(",", discovery.MissingEligibleIds);
            string scope = "reconcileterritory: scope=UNKN-only nonUnknWrites=0 eligibleIds=["
                + string.Join(",", eligibleIds) + "] checkpointCandidates=" + discovery.Candidates.Count
                + " rawUnknStations=" + discovery.RawUnknStationRecords
                + " ignoredRawUnknStations=" + discovery.IgnoredRawStationRecords
                + " missingEligibleIds=[" + missingIds + "] candidateIds=[" + candidateIds + "]";
            if (!discovery.IsSafe)
                return scope + " blocked=" + (discovery.Error ?? "unsafe candidate set") + ".";

            var persistedBrains = new List<BrainDoc>();
            foreach (IndexEntry entry in Store.LoadIndex())
            {
                BrainDoc brain = Store.LoadBrain(entry.Tag);
                if (brain != null) persistedBrains.Add(brain);
            }
            List<string> persistedCollisions = TerritoryReconciliationPlan.NonUnknSourceCollisions(persistedBrains, discovery.Candidates);
            if (persistedCollisions.Count > 0)
                return scope + " blocked=nonUnknPersistedIdCollision=[" + string.Join(",", persistedCollisions) + "].";

            var collisions = new List<string>();
            foreach (SourceDoc candidate in discovery.Candidates)
            {
                SpatialNode existing;
                if (_runtime.World.Graph.Nodes.TryGetValue(candidate.Id, out existing)
                    && !string.Equals(existing.OwnerFactionTag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase))
                    collisions.Add(candidate.Id + ":" + (existing.OwnerFactionTag ?? "<unowned>"));
            }
            if (collisions.Count > 0)
                return scope + " blocked=nonUnknRuntimeIdCollision=[" + string.Join(",", collisions) + "].";

            int persistedUnknSources = unkn.Sources == null ? 0 : unkn.Sources.Count;
            if (!apply)
                return scope + " persistedUnknSources=" + persistedUnknSources
                    + " (dry run -- take a paired Factions+world backup, then 'factions reconcileterritory apply').";

            TerritoryReconciliationPlan plan = TerritoryReconciliationPlan.Create(new[] { unkn });
            int removedPersistedUnknSources = plan.ReplaceAllUnknSources(unkn, discovery.Candidates);
            Store.SaveBrain(unkn); // The only brain write in this command.
            int removedRuntimeUnknSources = _runtime.ReplaceUnknSources(unkn);
            string applied = scope + " persistedUnknSourcesRemoved=" + removedPersistedUnknSources
                + " runtimeUnknSourcesRemoved=" + removedRuntimeUnknSources
                + " unknSourcesWritten=" + unkn.Sources.Count + " (applied).";
            Log.Info(applied);
            return applied;
        }

        private int CountRuntimeOnlyNodes(ISet<string> serializedSourceIds)
        {
            if (_runtime == null || _runtime.World == null) return 0;
            int count = 0;
            foreach (SpatialNode node in _runtime.World.Graph.Nodes.Values)
                if (!serializedSourceIds.Contains(node.Id)) count++;
            return count;
        }

        /// <summary>Bridge verb 'factions reclassify [apply]' -- dry-run by default; see
        /// <see cref="FactionRuntime.ReclassifyNodes"/>.</summary>
        private string HandleReclassify(string[] t)
        {
            if (_runtime == null) return "reclassify: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            NodeReclassifyResult r = _runtime.ReclassifyNodes(apply);
            return "reclassify: total=" + r.Total + " surface=" + r.Surface + " orbit=" + r.Orbit
                + " deepSpace=" + r.DeepSpace + " zoneChanged=" + r.ZoneChanged + " repositioned=" + r.Repositioned
                + " orbitRepositioned=" + r.OrbitRepositioned + " featurePlaced=" + r.FeaturePlaced
                + " planetIdChanged=" + r.PlanetIdChanged + " featureKeyChanged=" + r.FeatureKeyChanged
                + " separationBlocked=" + r.SeparationBlocked
                + " standingViolations=" + r.StandingViolations
                + (apply ? " (applied)" : " (dry run -- 'factions reclassify apply' to commit)");
        }

        /// <summary>Bridge verb 'factions densifytopology [apply]' -- dry-run by default; see
        /// <see cref="FactionRuntime.DensifyTopology"/>. Read the logged/returned `elapsedMs` before
        /// ever running `apply` live -- back up Instance/Factions/{brains,index.json} first too.</summary>
        private string HandleDensifyTopology(string[] t)
        {
            if (_runtime == null) return "densifytopology: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            TopologyDensifyResult r = _runtime.DensifyTopology(apply);
            return "densifytopology: scanned=" + r.NodesScanned + " pairsWithinReach=" + r.PairsWithinReach
                + " linksAdded=" + r.LinksAdded + " elapsedMs=" + r.ElapsedMs.ToString("0.0")
                + (apply ? " (applied)" : " (dry run -- 'factions densifytopology apply' to commit)");
        }

        /// <summary>Bridge verb 'factions backfillabandonedutc &lt;minutesAgo&gt; [apply]'.</summary>
        private string HandleBackfillAbandonedUtc(string[] t)
        {
            if (_runtime == null) return "backfillabandonedutc: runtime not started (no session loaded).";
            double minutesAgo;
            if (t.Length < 3 || !double.TryParse(t[2], out minutesAgo))
                return "backfillabandonedutc: usage 'factions backfillabandonedutc <minutesAgo> [apply]'";
            bool apply = t.Length > 3 && string.Equals(t[3], "apply", StringComparison.OrdinalIgnoreCase);
            int matched = _runtime.BackfillAbandonedUtc(TimeSpan.FromMinutes(minutesAgo), apply);
            return "backfillabandonedutc: age=" + minutesAgo + "min matched=" + matched
                + (apply ? " (applied)" : " (dry run -- add 'apply' to commit)");
        }

        /// <summary>Bridge verb 'factions clearstranding [apply]' -- dry-run by default; see
        /// <see cref="FactionRuntime.ClearOrbitStranding"/>. Composes <see cref="HandleReclassify"/>'s
        /// own repositioning internally, so running 'factions reclassify apply' first isn't required.</summary>
        private string HandleClearStranding(string[] t)
        {
            if (_runtime == null) return "clearstranding: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            OrbitStrandingClearResult r = _runtime.ClearOrbitStranding(apply);
            return "clearstranding: repositioned=" + r.NodesRepositioned
                + " buildsCanceled=" + r.BuildsCanceled + " itemsRefunded=" + r.BuildItemsRefunded
                + " shipsDismantled=" + r.ShipsDismantled + " bomUnitsCredited=" + r.BomUnitsCredited
                + (apply ? " (applied)" : " (dry run -- 'factions clearstranding apply' to commit)");
        }

        /// <summary>Bridge verb 'factions debris [apply]' -- dry-run by default. Runs the sweep
        /// <see cref="DebrisAbsorbManager.Tick"/> otherwise only runs once an hour, so the feature is
        /// checkable without waiting for it. The dry run reports the refusal counts, which is what
        /// separates "nothing was lying around" from "everything was refused".</summary>
        private string HandleDebris(string[] t)
        {
            if ((_debrisAbsorbManager ?? _agentDebris) == null) return "debris: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            DebrisAbsorbReport report = (_debrisAbsorbManager ?? _agentDebris).Sweep(apply);
            return DebrisAbsorbManager.Describe(report)
                + (apply ? "" : "\n  (dry run -- 'factions debris apply' to commit)");
        }

        /// <summary>Bridge and chat verb 'factions grids &lt;census|reap [apply]&gt;'.
        ///
        /// <para><c>census</c> is the operator's instrument: every grid in the world bucketed by
        /// name, with how many in each bucket are held, station, player-owned and moving, and which
        /// carrier identified each one — the record this process wrote, else the NPC-spawned bit,
        /// else the <c>factions:npc:</c> entity name. <c>reap</c> runs one reaper pass, dry by
        /// default.</para></summary>
        private string HandleGrids(string[] t)
        {
            if (_gridReaper == null) return "grids: runtime not started (no session loaded).";
            string sub = t.Length > 2 ? t[2] : "census";

            if (string.Equals(sub, "census", StringComparison.OrdinalIgnoreCase))
                return GridReaper.Describe(_gridReaper.Census());

            if (string.Equals(sub, "reap", StringComparison.OrdinalIgnoreCase))
            {
                bool apply = t.Length > 3 && string.Equals(t[3], "apply", StringComparison.OrdinalIgnoreCase);
                GridReapReport report = _gridReaper.Sweep(apply);
                return GridReaper.Describe(report)
                     + (apply ? "" : "\n  (dry run -- 'factions grids reap apply' to commit)");
            }

            if (string.Equals(sub, "backfill", StringComparison.OrdinalIgnoreCase))
            {
                bool apply = t.Length > 3 && string.Equals(t[3], "apply", StringComparison.OrdinalIgnoreCase);
                GridBackfillReport report = _gridReaper.BackfillNames(apply);
                return GridReaper.Describe(report)
                     + (apply
                        ? "\n  Re-run 'factions grids census' -- name_only should rise by the written"
                          + " count, and 'factions grids reap' should now see them."
                        : "\n  (dry run -- 'factions grids backfill apply' to commit)");
            }

            if (string.Equals(sub, "fight", StringComparison.OrdinalIgnoreCase))
            {
                bool apply = t.Length > 3 && string.Equals(t[3], "apply", StringComparison.OrdinalIgnoreCase);
                RefreshFightSites(DateTime.UtcNow);
                FightDebrisReport report = _gridReaper.SweepFight(_fightSites, MyPhysics.SimulationRatio < FightDebris.SimFloor, apply);
                return "grids fight: sites=" + report.Sites + " groups=" + report.ClosedGroups + " grids=" + report.Closed
                     + " blocks=" + report.Blocks + " skip_held=" + report.SkippedHeld + " skip_settling=" + report.SkippedSettling
                     + " skip_salvage=" + report.SkippedSalvage + " skip_owned=" + report.SkippedOwned + " skip_cap=" + report.SkippedByCap
                     + (apply ? "" : "\n  (dry run -- 'factions grids fight apply' to commit)");
            }

            return "grids: unknown sub-command '" + sub + "'. Use 'census', 'reap [apply]',"
                 + " 'fight [apply]' or 'backfill [apply]'.";
        }

        /// <summary>The fight sweep (FAC-425, docs/fight-control.md): every
        /// <see cref="FightDebris.SweepSeconds"/>, close what
        /// fights near materialised combat hulls have shed.</summary>
        private void TickFightDebris()
        {
            if (_gridReaper == null || _runtime == null) return;
            DateTime now = DateTime.UtcNow;
            if (now < _nextFightSweepUtc) return;
            _nextFightSweepUtc = now.AddSeconds(FightDebris.SweepSeconds);
            RefreshFightSites(now);
            if (_fightSites.Count == 0) return;
            _gridReaper.SweepFight(_fightSites, MyPhysics.SimulationRatio < FightDebris.SimFloor, true);
        }

        private void RefreshFightSites(DateTime now)
        {
            _fightHulls.Clear();
            _runtime?.CollectCombatHullPositionsKm(_fightHulls);
            _fightSites.Update(_fightHulls, now);
        }

        /// <summary>Bridge verb 'factions migratestationprefabs [apply] [nodeId ...]' -- dry-run by
        /// default, every founded node unless node ids are named; see
        /// <see cref="FactionRuntime.MigrateStationPrefabs"/>. Run 'factions reclassify apply' first
        /// so Zone reflects live gravity before checking prefab mismatches against it.</summary>
        private string HandleMigrateStationPrefabs(string[] t)
        {
            if (_runtime == null) return "migratestationprefabs: runtime not started (no session loaded).";
            bool apply = false;
            var onlyNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 2; i < t.Length; i++)
            {
                if (string.Equals(t[i], "apply", StringComparison.OrdinalIgnoreCase)) apply = true;
                else if (!string.IsNullOrEmpty(t[i])) onlyNodes.Add(t[i]);
            }
            StationPrefabMigrationResult r = _runtime.MigrateStationPrefabs(apply, onlyNodes);
            string header = "migratestationprefabs: checked=" + r.Checked + " mismatched=" + r.Mismatched
                + " outsidePool=" + r.OutsidePool + " refounded=" + r.Refounded + (apply ? " (applied)" : " (dry run -- 'factions migratestationprefabs apply' to commit)");
            if (r.Details == null || r.Details.Count == 0) return header;
            return header + " | " + string.Join(" | ", r.Details);
        }

        /// <summary>Bridge verb 'factions cleardeepspaceflags [apply]' -- dry-run by default; see
        /// <see cref="FactionRuntime.ClearDeepSpaceFlags"/>.</summary>
        private string HandleClearDeepSpaceFlags(string[] t)
        {
            if (_runtime == null) return "cleardeepspaceflags: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            int affected = _runtime.ClearDeepSpaceFlags(apply);
            return "cleardeepspaceflags: " + (apply ? "cleared " : "would clear ") + affected + " station(s)"
                + (apply ? " (applied)" : " (dry run -- 'factions cleardeepspaceflags apply' to commit)");
        }

        /// <summary>Bridge verb 'factions fixstationownership [apply]' -- dry-run by default; see
        /// <see cref="FactionRuntime.FixStationOwnershipMismatches"/>. One-time backfill for stations
        /// left registered under a stale previous owner by an ownership transfer that predates the
        /// 2026-08-17 RenameConqueredStation fix.</summary>
        private string HandleFixStationOwnership(string[] t)
        {
            if (_runtime == null) return "fixstationownership: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            StationOwnershipFixResult r = _runtime.FixStationOwnershipMismatches(apply);
            string header = "fixstationownership: checked=" + r.Checked + " mismatched=" + r.Mismatched
                + " fixed=" + r.Fixed + (apply ? " (applied)" : " (dry run -- 'factions fixstationownership apply' to commit)");
            if (r.Details == null || r.Details.Count == 0) return header;
            return header + " | " + string.Join(" | ", r.Details);
        }

        /// <summary>
        /// Diagnostic inspector for a faction's station node inventory — no persistence side effects.
        /// 'factions node &lt;tag&gt;' lists the faction's station nodes; 'factions node &lt;tag&gt; &lt;nodeId&gt;'
        /// dumps that node's full inventory. Used to watch <see cref="SpatialNode.Inventory"/> react
        /// live to a player buy/sell transaction at the station terminal.
        /// </summary>
        /// <summary>Bridge verb 'factions cancelbuild &lt;tag&gt; &lt;nodeId&gt; &lt;entryId&gt;' —
        /// admin equivalent of the player `!factions station queue cancel` command, for NPC (or any
        /// other non-caller-owned) factions the bridge has no player identity to resolve ownership
        /// through. See <see cref="FactionRuntime.CancelBuildAdmin"/>.</summary>
        private string HandleCancelBuild(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "cancelbuild: runtime not started (no session loaded).";
            if (t.Length < 5) return "cancelbuild: usage 'factions cancelbuild <tag> <nodeId> <entryId>'";
            return _runtime.CancelBuildAdmin(t[2], t[3], t[4]);
        }

        /// <summary>Bridge verb 'factions decommission &lt;tag&gt; &lt;design&gt; [count] [apply]' -- admin
        /// twin of the player's '!factions fleet decommission', for any registered faction. Dry run
        /// unless 'apply'. See <see cref="FactionRuntime.DecommissionAdmin"/>.</summary>
        internal string HandleDecommission(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "decommission: runtime not started (no session loaded).";
            if (t.Length < 4) return "decommission: usage 'factions decommission <tag> <design> [count] [apply]'";
            int count;
            bool apply;
            string error;
            var tail = new List<string>();
            for (int i = 4; i < t.Length; i++) tail.Add(t[i]);
            if (!FactionRuntime.TryReadDecommissionArgs(tail, "apply", out count, out apply, out error))
                return "decommission: " + error;
            return _runtime.DecommissionAdmin(t[2], t[3], count, apply);
        }

        /// <summary>Bridge verb 'factions prefab ban|unban &lt;design&gt;' and 'factions prefab bans' -- the
        /// server's prefab ban list (FAC-451). See <see cref="FactionRuntime.BanPrefab"/>.</summary>
        internal string HandlePrefabBan(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "prefab: runtime not started (no session loaded).";
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "bans";
            string design = t.Length > 3 ? string.Join(" ", t, 3, t.Length - 3) : null;
            switch (sub)
            {
                case "ban":   return _runtime.BanPrefab(design);
                case "unban": return _runtime.UnbanPrefab(design);
                case "bans":
                case "list":  return _runtime.DescribePrefabBans();
                default:      return "prefab: usage 'factions prefab ban|unban <design>' or 'factions prefab bans'";
            }
        }

        /// <summary>Bridge verb 'factions pausebuild &lt;tag&gt; &lt;nodeId&gt; [on|off]' -- admin twin of
        /// the player's own '!factions station queue pause|resume'. Defaults to pausing; pass 'off'
        /// (or 'resume'/'false') to let the yard collect again.</summary>
        private string HandlePauseBuild(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "pausebuild: runtime not started (no session loaded).";
            if (t.Length < 4) return "pausebuild: usage 'factions pausebuild <tag> <nodeId> [on|off]'";
            bool paused = true;
            if (t.Length >= 5)
            {
                string arg = t[4];
                if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("resume", StringComparison.OrdinalIgnoreCase)
                    || arg.Equals("false", StringComparison.OrdinalIgnoreCase)) paused = false;
            }
            return _runtime.PauseShipyardAdmin(t[2], t[3], paused);
        }

        /// <summary>Bridge verb 'factions buildpriority &lt;tag&gt; &lt;nodeId&gt; &lt;entryId&gt; &lt;n&gt;'
        /// -- admin twin of '!factions station queue priority'.</summary>
        private string HandleBuildPriority(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "buildpriority: runtime not started (no session loaded).";
            if (t.Length < 6) return "buildpriority: usage 'factions buildpriority <tag> <nodeId> <entryId> <priority>'";
            int priority;
            if (!int.TryParse(t[5], out priority)) return "buildpriority: '" + t[5] + "' isn't a number.";
            return _runtime.SetBuildPriorityAdmin(t[2], t[3], t[4], priority);
        }

        /// <summary>Bridge verb 'factions develop &lt;nodeId&gt; &lt;capability&gt; &lt;level&gt;' -- the
        /// admin form of `!factions station develop`, charging nothing and naming the level
        /// outright. Level 0 removes the capability; the owning faction is read off the node. A
        /// bridge entry point is needed because `HandleCommandFromServer` no-ops the chat command
        /// (the null server-player fails its permission check). See
        /// <see cref="FactionRuntime.GrantCapabilityAdmin"/>.</summary>
        private string HandleDevelop(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "develop: runtime not started (no session loaded).";
            if (t.Length < 5) return "develop: usage 'factions develop <nodeId> <capability> <level>' -- capabilities: "
                                     + CapabilityTokens.Names();
            NodeCapabilityKind capability;
            if (!CapabilityTokens.TryParse(t[3], out capability))
                return "develop: unknown capability '" + t[3] + "'. Valid: " + CapabilityTokens.Names();
            int level;
            if (!int.TryParse(t[4], out level)) return "develop: '" + t[4] + "' isn't a number.";
            return _runtime.GrantCapabilityAdmin(t[2], capability, level);
        }

        /// <summary>Bridge verb 'factions orders &lt;tag&gt; [list|mine|remove|priority|pause|resume] ...'
        /// -- admin twin of the player's '!factions station orders'. See
        /// <see cref="FactionRuntime.StandingOrdersAdmin"/> for why a bridge entry point is needed at all.</summary>
        private string HandleStandingOrders(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "orders: runtime not started (no session loaded).";
            return _runtime.StandingOrdersAdmin(t);
        }

        /// <summary>Bridge verb 'factions store &lt;tag&gt; [list|add|remove|target|pause|resume|on|off] ...'
        /// -- admin twin of the player's '!factions store', and the only way the shelf can be
        /// exercised on the headless test server. See <see cref="FactionRuntime.ShelfAdmin"/>.</summary>
        private string HandleShelf(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "store: runtime not started (no session loaded).";
            return _runtime.ShelfAdmin(t);
        }

        /// <summary>Bridge verb 'factions fleet &lt;tag&gt; [list|add|remove|priority|pause|resume|target] ...'
        /// -- admin twin of the player's '!factions fleet', and the only way the doctrine can be
        /// exercised on the headless test server. See <see cref="FactionRuntime.FleetDoctrineAdmin"/>.</summary>
        private string HandleFleetDoctrine(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "fleet: runtime not started (no session loaded).";
            return _runtime.FleetDoctrineAdmin(t);
        }

        /// <summary>Bridge verb 'factions fixstationdrift [apply] [nodeId]' -- dry-run by default.
        /// Naming a node restricts the run to it and overrides the implausible-gap fence; see
        /// <see cref="StationMaterializer.FixNodeStationDrift"/>. Whole-map counterpart to the
        /// on-approach heal in <c>BeginSpawn</c>, for stations no player is going to visit soon.</summary>
        private string HandleFixStationDrift(string[] t)
        {
            if (_runtime == null) return "fixstationdrift: runtime not started (no session loaded).";
            bool apply = t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase);
            // 'apply' is optional, so the node id is whichever trailing token is not it.
            string onlyNodeId = null;
            if (apply) { if (t.Length > 3) onlyNodeId = t[3]; }
            else if (t.Length > 2) onlyNodeId = t[2];
            StationDriftFixResult r = _runtime.FixStationPositionDrift(apply, onlyNodeId);
            string header = "fixstationdrift: "
                + (onlyNodeId != null ? "node=" + onlyNodeId + " " : "")
                + "checked=" + r.Checked + " drifted=" + r.Drifted
                + " fixed=" + r.Fixed + " skipped=" + r.Skipped
                + " worst=" + Math.Round(r.WorstDriftM) + "m"
                + (apply ? " (applied)" : " (dry run -- add 'apply' to commit)");
            if (r.Details == null || r.Details.Count == 0) return header;
            return header + " | " + string.Join(" | ", r.Details);
        }

        /// <summary>Bridge verb 'factions gravityprobe &lt;planet&gt;' -- read-only; see
        /// <see cref="FactionRuntime.GetGravityProfile"/>. Reads the host's real gravity falloff at
        /// explicit trial radii, including inside the planet.</summary>
        private string HandleGravityProbe(string[] t)
        {
            if (_runtime == null) return "gravityprobe: runtime not started (no session loaded).";
            string filter = t.Length > 2 ? t[2] : null;
            System.Collections.Generic.List<string> lines = _runtime.GetGravityProfile(filter);
            foreach (string line in lines) Log.Info(line);
            return string.Join(" | ", lines);
        }

        private string HandleNode(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "node: runtime not started (no session loaded).";
            if (t.Length < 3) return "node: usage 'factions node <tag> [nodeId]'";
            string tag = t[2];
            string nodeId = t.Length > 3 ? t[3] : null;

            var matches = new List<SpatialNode>();
            foreach (SpatialNode n in _runtime.World.Graph.Nodes.Values)
            {
                if (!string.Equals(n.OwnerFactionTag, tag, StringComparison.OrdinalIgnoreCase)) continue;
                if (n.StableStationId == 0) continue;
                matches.Add(n);
            }

            if (nodeId == null)
            {
                if (matches.Count == 0) return "node: no station node(s) owned by '" + tag + "'.";
                var sb = new StringBuilder("node [" + tag + "]: " + matches.Count + " station node(s):");
                foreach (SpatialNode n in matches)
                    sb.Append("\n  " + n.Id + " stationId=" + n.StableStationId + " name=\"" + n.DisplayName
                        + "\" items=" + n.Inventory.Count);
                return sb.ToString();
            }

            SpatialNode node = matches.Find(n => string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase));
            if (node == null)
                node = matches.Find(n => n.DisplayName != null
                    && n.DisplayName.IndexOf(nodeId, StringComparison.OrdinalIgnoreCase) >= 0);
            if (node == null) return "node: '" + nodeId + "' not found by id or name (or not owned by '" + tag + "').";

            var isb = new StringBuilder("node [" + node.Id + "] stationId=" + node.StableStationId + " name=\""
                + node.DisplayName + "\"");
            isb.Append("\n  hold=" + node.HoldStrength.ToString("0.00", CultureInfo.InvariantCulture)
                + " shipActivity=" + ShipActivity.CreditsOf(node, _runtime.World.Tuning, _runtime.World.UtcNow)
                    .ToString("0.00", CultureInfo.InvariantCulture)
                + "/" + _runtime.World.Tuning.ShipActivityCreditCap.ToString("0.00", CultureInfo.InvariantCulture)
                + " holdGainPerHour=" + ShipActivity.HoldGainForInterval(node, _runtime.World.Tuning,
                    _runtime.World.UtcNow, 3600.0).ToString("0.000", CultureInfo.InvariantCulture)
                + " holdDecayPerHour=" + _runtime.World.Tuning.HoldDecayForIntervalSeconds(3600.0)
                    .ToString("0.000", CultureInfo.InvariantCulture));
            isb.Append("\n  inventory:");
            foreach (KeyValuePair<string, long> kv in node.Inventory)
                if (kv.Value != 0) isb.Append("\n  " + kv.Key + " = " + kv.Value);
            return isb.ToString();
        }

        /// <summary>
        /// Bridge verb <c>factions surfaceprobe &lt;x&gt; &lt;y&gt; &lt;z&gt;</c> (world metres, i.e. straight off a
        /// player GPS) -- asks the live server what the surface material model actually resolves at a
        /// point, instead of inferring it from a mod's <c>.sbc</c>.
        ///
        /// <para><b>Why it prints what it prints.</b> Three separate explanations for a body reporting
        /// no surface ore predict different things, and a layer list alone cannot tell them apart:
        /// sampling density, layer ordering within a rule, and querying at mean radius rather than at
        /// the terrain. So this probes the point <b>as given</b> and again <b>projected to the
        /// surface</b>, and prints the resolved height, the selected rule with its own
        /// Height/Slope window, and that rule's full layer stack. If the two probes disagree, the
        /// query position is the problem; if they agree and the layer stack has the material at index
        /// 0, density is; if the stack has it deeper, ordering is.</para>
        ///
        /// <para>Read-only, one point per call. Every member it touches is public on a public SE type
        /// -- no reflection.</para>
        /// </summary>
        private string HandleSurfaceProbe(string[] t)
        {
            double x, y, z;
            if (t.Length < 5
                || !double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !double.TryParse(t[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                || !double.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return "surfaceprobe: usage 'factions surfaceprobe <x> <y> <z>' (world metres, as on a GPS).";

            var world = new VRageMath.Vector3D(x, y, z);
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(world);
            if (planet == null) return "surfaceprobe: no planet near " + Fmt(world) + ".";

            Sandbox.Engine.Voxels.Planet.MyPlanetStorageProvider provider = planet.Provider;
            if (provider == null || provider.Closed) return "surfaceprobe: planet provider unavailable.";

            VRageMath.Vector3D centre = planet.PositionComp.GetPosition();
            var sb = new StringBuilder();
            sb.AppendLine("surfaceprobe " + Fmt(world));
            sb.AppendLine("  planet      " + (planet.DisplayName ?? "Planet")
                          + " [" + (planet.Generator == null ? "?" : planet.Generator.Id.SubtypeName) + "]"
                          + "  avgRadius=" + planet.AverageRadius.ToString("0", CultureInfo.InvariantCulture) + "m");
            sb.AppendLine("  centre      " + Fmt(centre));
            sb.AppendLine("  |pos-centre|=" + (world - centre).Length().ToString("0.0", CultureInfo.InvariantCulture)
                          + "m   (avgRadius " + planet.AverageRadius.ToString("0", CultureInfo.InvariantCulture)
                          + "m -> " + ((world - centre).Length() - planet.AverageRadius).ToString("+0.0;-0.0", CultureInfo.InvariantCulture)
                          + "m relative to mean)");

            // Probe 1: the point as given. Probe 2: the same direction projected onto real terrain.
            // The pair is the discriminator -- SurfaceOresFor queries at mean radius, and low terrain
            // (a lake bed) sits below it by definition.
            VRageMath.Vector3D dir = VRageMath.Vector3D.Normalize(world - centre);
            var centreLocal = (VRageMath.Vector3)(world - centre);
            VRageMath.Vector3 surfaceLocal = planet.GetClosestSurfacePointLocal(ref centreLocal);
            VRageMath.Vector3D surfaceWorld = centre + surfaceLocal;
            VRageMath.Vector3D meanWorld = centre + dir * planet.AverageRadius;

            AppendProbe(sb, provider, planet, "as given ", world);
            AppendProbe(sb, provider, planet, "on terrain", surfaceWorld);
            AppendProbe(sb, provider, planet, "mean radius", meanWorld);
            return sb.ToString().TrimEnd();
        }

        private static string Fmt(VRageMath.Vector3D v)
        {
            return "(" + v.X.ToString("0.0", CultureInfo.InvariantCulture)
                 + ", " + v.Y.ToString("0.0", CultureInfo.InvariantCulture)
                 + ", " + v.Z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>One probe line-group. <paramref name="world"/> is a world-space point; the API
        /// takes storage-corner-relative coordinates, which is a different space from the
        /// centre-relative one the cubemap helpers use.</summary>
        private static void AppendProbe(StringBuilder sb, Sandbox.Engine.Voxels.Planet.MyPlanetStorageProvider provider,
            MyPlanet planet, string label, VRageMath.Vector3D world)
        {
            var local = (VRageMath.Vector3)(world - planet.PositionLeftBottomCorner);
            Sandbox.Engine.Voxels.Planet.MyPlanetStorageProvider.SurfacePropertiesExtended props;
            provider.ComputeCombinedMaterialAndSurfaceExtended(local, out props);

            string mat = props.Material == null ? "(null)"
                : props.Material.Id.SubtypeName + " minedOre='" + (props.Material.MinedOre ?? "") + "'";

            sb.AppendLine("  [" + label + "] " + Fmt(world));
            sb.AppendLine("      material   " + mat + "   origin=" + props.Origin);
            sb.AppendLine("      surface    depth=" + props.Depth.ToString("0.0", CultureInfo.InvariantCulture)
                          + " altitude=" + props.Altitude.ToString("0.0", CultureInfo.InvariantCulture)
                          + " heightRatio=" + props.HeightRatio.ToString("0.0000", CultureInfo.InvariantCulture)
                          + " slope=" + props.Slope.ToString("0.00", CultureInfo.InvariantCulture)
                          + " lat=" + props.Latitude.ToString("0.0", CultureInfo.InvariantCulture));
            sb.AppendLine("      map bytes  material=" + props.MaterialValue + " biome=" + props.BiomeValue
                          + " ore=" + props.OreValue);

            Sandbox.Engine.Voxels.Planet.MyPlanetMaterialProvider.PlanetMaterial rule = props.EffectiveRule;
            if (rule == null) { sb.AppendLine("      rule       (none)"); return; }

            var asRule = rule as Sandbox.Engine.Voxels.Planet.MyPlanetMaterialProvider.PlanetMaterialRule;
            sb.AppendLine("      rule       " + (asRule != null
                ? "index=" + asRule.Index
                  + " height=[" + asRule.Height.Min.ToString("0.####", CultureInfo.InvariantCulture)
                  + ".." + asRule.Height.Max.ToString("0.####", CultureInfo.InvariantCulture) + "]"
                  + " slope=[" + asRule.Slope.Min.ToString("0.##", CultureInfo.InvariantCulture)
                  + ".." + asRule.Slope.Max.ToString("0.##", CultureInfo.InvariantCulture) + "]"
                : "(not a rule -- default or biome material)"));

            if (rule.Layers == null || rule.Layers.Length == 0)
            {
                sb.AppendLine("      layers     (none) single material="
                              + (rule.Material == null ? "(null)" : rule.Material.Id.SubtypeName));
                return;
            }
            // SE picks the layer by DEPTH, not by index: the first layer whose band covers
            // (SurfaceDepth + 0.5) wins, and if none does, props.Material keeps the planet's default
            // material from before the rule was even resolved. Annotating index 0 as "the one that
            // counts" is what sent two sessions down a wrong diagnosis, so this marks the layer the
            // depth actually selected -- and says so explicitly when the answer is "none of them".
            float probeDepth = props.Depth + 0.5f;
            int selected = -1;
            for (int i = 0; i < rule.Layers.Length; i++)
            {
                if (rule.Layers[i] != null && probeDepth >= 0f - rule.Layers[i].Depth) { selected = i; break; }
            }

            for (int i = 0; i < rule.Layers.Length; i++)
            {
                Sandbox.Engine.Voxels.Planet.MyPlanetMaterialProvider.VoxelMaterial layer = rule.Layers[i];
                string lm = layer == null || layer.Material == null ? "(null)"
                    : layer.Material.Id.SubtypeName + " minedOre='" + (layer.Material.MinedOre ?? "") + "'";
                sb.AppendLine("      layer[" + i + "]   " + lm
                              + " depth=" + (layer == null ? 0f : layer.Depth).ToString("0.#", CultureInfo.InvariantCulture)
                              + (i == selected ? "   <- selected at this depth" : ""));
            }
            if (selected < 0)
                sb.AppendLine("      layers     NONE cover depth " + probeDepth.ToString("0.0", CultureInfo.InvariantCulture)
                              + " -- material above is the planet DEFAULT, not this rule's."
                              + " Origin still reads Rule: it records which rule was selected, not where the material came from.");
        }

        /// <summary>Bridge verb 'factions gravitydump [filter]' -- ad-hoc diagnostic wrapper around the
        /// pre-existing (previously unwired) <see cref="FactionRuntime.GetNodeGravityDump"/>. Filters
        /// the full per-node dump (position/g/computed-zone/stored-zone/owner) by substring match
        /// (node id, owner tag, or zone name all work) since the unfiltered dump is thousands of lines.
        /// No filter returns the first 50 lines as a sample. Read-only.</summary>
        private string HandleGravityDump(string[] t)
        {
            if (_runtime == null) return "gravitydump: runtime not started (no session loaded).";
            string filter = t.Length > 2 ? t[2] : null;
            List<string> lines = _runtime.GetNodeGravityDump();
            if (filter != null)
            {
                List<string> matched = lines.FindAll(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                if (matched.Count == 0) return "gravitydump: no lines matching '" + filter + "'.";
                return string.Join("\n", matched);
            }
            int take = Math.Min(lines.Count, 50);
            return string.Join("\n", lines.GetRange(0, take))
                + (lines.Count > take ? "\n... (" + lines.Count + " total, use a filter)" : "");
        }

        /// <summary>
        /// Strips SE's minted station layout: every <c>MyFactionStation</c> record goes, and the
        /// faction that owns it stays. <c>orphans</c> narrows it to records no Factions node points
        /// at — the debris <c>world estate</c> leaves when it re-rolls without purging first, and the
        /// only form of this verb that is safe to run on a live estate. The inverse of <see cref="HandleSanitize"/>, which keeps the
        /// stations and removes the factions. Dry-run unless 'apply' is given, because a purge is
        /// not reversible from inside the session.
        /// </summary>
        /// <summary>Chat-command entry point for <c>!factions world purge</c>, so the chat twin and
        /// the bridge verb share one parser and one set of refusals.</summary>
        public string HandleWorldPurgeCommand(string[] t) { return HandleWorldPurge(t); }

        private string HandleWorldPurge(string[] t)
        {
            if (_runtime == null) return "world purge: runtime not started (no session loaded).";

            WorldPurgeScope scope;
            string error;
            if (!WorldPurgeScope.TryParse(t, 3, out scope, out error)) return "world purge: " + error;

            return _runtime.PurgeStations(scope.Apply, scope.TagFilter, scope.OrphansOnly);
        }

        /// <summary>
        /// One-shot world cleanup: reduce the SE faction list to the economy factions that hold
        /// stations, deleting the grids the removed factions own. Dry-run by default;
        /// 'factions sanitize apply' executes. Any removed tag that is also in the Factions
        /// registry is forgotten through the usual flush -> edit disk -> load path.
        /// </summary>
        private string HandleSanitize(string[] t)
        {
            return RunSanitize(t.Length > 2 && string.Equals(t[2], "apply", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// <c>factions world estate [apply] [seed]</c> -- roll every faction's estate from
        /// <c>faction_recipe.json</c>. Dry run unless <c>apply</c> is given, mirroring
        /// <see cref="HandleWorldPurge"/>. The optional seed is the operator's re-roll lever: a
        /// different number is a different world, the same number is the same world every time.
        /// </summary>
        private string HandleWorldEstate(string[] t)
        {
            bool apply = false;
            int worldSeed = DefaultEstateSeed;
            // From 3: t[2] is the sub-verb ("estate") itself, matching HandleWorldPurge. Starting at
            // 2 made the verb reject its own name -- found live, and no Core test can reach this.
            for (int i = 3; i < t.Length; i++)
            {
                if (string.Equals(t[i], "apply", StringComparison.OrdinalIgnoreCase)) { apply = true; continue; }
                int parsed;
                if (int.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    worldSeed = parsed;
                    continue;
                }
                return "world estate: unrecognised argument '" + t[i]
                     + "'. Usage: factions world estate [apply] [seed]";
            }

            if (_runtime == null) return "world estate: runtime not started (no session loaded).";

            string status;
            FactionRecipe recipe = LoadFactionRecipe(out status);
            if (recipe == null) return "world estate: " + status;

            return DivergencePrefix(FactionRecipe.FileName) + _runtime.SeedEstate(recipe, worldSeed, apply);
        }

        /// <summary>
        /// <c>factions world relations [apply]</c> -- write the authored diplomacy from
        /// <c>faction_recipe.json</c>'s <c>Relations</c> block into SE's faction relation table.
        /// Dry run unless <c>apply</c> is given, mirroring <see cref="HandleWorldEstate"/>.
        /// <para>Separate from <c>estate</c> rather than folded into it: relations are meaningful
        /// only once stations exist, and the estate verb is what creates them. One verb doing both
        /// would have to run the second half against a world the first half had just changed.</para>
        /// </summary>
        private string HandleWorldRelations(string[] t)
        {
            bool apply = false;
            for (int i = 3; i < t.Length; i++)
            {
                if (string.Equals(t[i], "apply", StringComparison.OrdinalIgnoreCase)) { apply = true; continue; }
                return "world relations: unrecognised argument '" + t[i]
                     + "'. Usage: factions world relations [apply]";
            }

            if (_runtime == null) return "world relations: runtime not started (no session loaded).";

            string status;
            FactionRecipe recipe = LoadFactionRecipe(out status);
            if (recipe == null) return "world relations: " + status;

            return DivergencePrefix(FactionRecipe.FileName) + _runtime.ApplyRelations(recipe, apply);
        }

        /// <summary>
        /// A divergence line for the head of a verb's reply, or empty. The reply is what an operator
        /// reads before an apply, so a recipe being read from the other directory's copy belongs
        /// there and not only in the log.
        /// </summary>
        private string DivergencePrefix(string fileName)
        {
            string line = WarnIfInputCopiesDiffer(fileName);
            return line == null ? "" : "WARNING " + line + "\n";
        }

        /// <summary>
        /// Plan the authored estate against this world's real geometry and say so at boot if it
        /// cannot fit. Silent on the healthy path and silent when no recipe is authored -- this is a
        /// warning, not telemetry: it speaks only when <c>factions world estate</c> would refuse.
        /// <para>Boot is where this belongs because the refusal is a property of the recipe and the
        /// world, both fixed by the time the session is up. Waiting for someone to run the verb means
        /// discovering an unbuildable world at the moment you wanted to build it.</para>
        /// </summary>
        private void WarnIfEstateDoesNotFit(IEnumerable<Factions.Core.WorldFeature> features,
                                            IPlanetCatalogue planets)
        {
            string status;
            FactionRecipe recipe = LoadFactionRecipe(out status);
            if (recipe == null)
            {
                // An absent recipe is the shipped default and says nothing; one that exists and
                // failed to load is a real problem and would otherwise be silent until the verb ran.
                if (!status.Contains("absent")) Log.Warn("Estate recipe: " + status);
                return;
            }

            PlacementPlan plan;
            try
            {
                plan = PlacementPlanner.Plan(EstateSeeder.ToAssignments(recipe),
                    EstateSeeder.GeometryFrom(planets, features),
                    EstateSeeder.SeparationFrom(planets, features, _tuning));
            }
            catch (Exception ex)
            {
                Log.Warn("Estate recipe: could not be planned against this world (" + ex.Message + ").");
                return;
            }

            if (plan.Refusals.Count == 0) return;

            Log.Warn("Estate recipe asks for more than this world holds -- 'factions world estate' "
                   + "will place NOTHING until the recipe or the authored separation changes:");
            foreach (string refusal in plan.Refusals) Log.Warn("  " + refusal);
        }

        /// <summary>The estate seed used when the operator names none. Fixed, not derived from the
        /// session: the same recipe must roll the same world on every boot, or a dry run would not
        /// describe what an apply is about to do.</summary>
        private const int DefaultEstateSeed = 1;

        /// <summary>
        /// Read <c>faction_recipe.json</c>. Absent, unparseable and structurally invalid are three
        /// different failures and are reported as three different lines -- an estate that silently
        /// does not exist is the failure this whole path was built to stop.
        /// </summary>
        private FactionRecipe LoadFactionRecipe(out string status)
        {
            bool usedFallback;
            string path = DataDirectory.ResolveInput(FactionRecipe.FileName, StorageDir, InstanceDir,
                File.Exists, out usedFallback);
            WarnIfInputCopiesDiffer(FactionRecipe.FileName);
            LogInputRead(FactionRecipe.FileName, path);
            if (!File.Exists(path))
            {
                status = FactionRecipe.FileName + ": absent -- no estate is authored for this world.";
                return null;
            }

            FactionRecipe recipe;
            try { recipe = JsonConvert.DeserializeObject<FactionRecipe>(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                status = FactionRecipe.FileName + ": UNREADABLE (" + ex.Message + ").";
                return null;
            }
            if (recipe == null)
            {
                status = FactionRecipe.FileName + ": UNREADABLE (parsed to nothing).";
                return null;
            }

            string why;
            if (!recipe.Validate(out why))
            {
                status = FactionRecipe.FileName + ": REJECTED (" + why + ").";
                return null;
            }

            status = FactionRecipe.FileName + ": " + recipe.Factions.Count + " factions authored.";
            return recipe;
        }

        /// <summary>
        /// Read the authored per-body ore maps out of <c>world_recipe.json</c>, under the same
        /// absent / unreadable / rejected reporting as the surface rule below: "no map authored" and
        /// "your map failed to load" are the same silence otherwise.
        ///
        /// <para><b>Read at plugin <c>Init</c>, against <paramref name="dataDir"/> rather than
        /// <see cref="StorageDir"/>.</b> SE builds a saved planet's ore table during world load,
        /// before <see cref="ApplyDataDirectory"/> runs, so the caller resolves the shared folder
        /// from the tuning files itself (FAC-241: the recipe is shared, and lives there). The
        /// instance copy is still read when the shared one is absent, and the absent message names
        /// both places.</para>
        /// </summary>
        private PlanetOrePolicy LoadPlanetOrePolicy(string dataDir, out string status)
        {
            PlanetOrePolicy empty;
            string ignored;
            PlanetOrePolicy.TryBuild(null, out empty, out ignored);

            bool usedFallback;
            string path = DataDirectory.ResolveInput(WorldRecipe.FileName, dataDir, InstanceDir,
                File.Exists, out usedFallback);
            WarnIfInputCopiesDiffer(WorldRecipe.FileName, dataDir);
            LogInputRead(WorldRecipe.FileName, path);
            if (!File.Exists(path))
            {
                status = WorldRecipe.FileName + ": absent from " + dataDir
                       + (string.Equals(dataDir, InstanceDir, StringComparison.OrdinalIgnoreCase)
                           ? "" : " and " + InstanceDir)
                       + " -- every body keeps its generator's own ore map. This is the shipped default.";
                return empty;
            }

            string error;
            WorldRecipe recipe;
            try { recipe = WorldRecipe.Parse(File.ReadAllText(path), out error); }
            catch (Exception ex) { recipe = null; error = ex.Message; }
            if (recipe == null)
            {
                status = WorldRecipe.FileName + ": UNREADABLE (" + error
                       + ") -- every body keeps its generator's own ore map, so any authored map is NOT in effect.";
                return empty;
            }

            PlanetOrePolicy policy;
            string why;
            if (!PlanetOrePolicy.TryBuild(recipe, out policy, out why))
            {
                status = WorldRecipe.FileName + ": ore map REJECTED (" + why
                       + ") -- every body keeps its generator's own ore map.";
                return empty;
            }

            if (policy.Count == 0)
            {
                status = WorldRecipe.FileName + ": no OreMappings authored -- every body keeps its "
                       + "generator's own ore map.";
                return policy;
            }

            var parts = new List<string>();
            foreach (PlanetOrePolicy.Entry e in policy.Entries)
                parts.Add(e.Name + " [" + e.Generator + "/" + e.Seed + "]=" + e.Mappings.Count + " entry/entries");
            status = WorldRecipe.FileName + ": " + policy.Count + " authored ore map(s) -- "
                   + string.Join(", ", parts.ToArray())
                   + " read=" + path;
            return policy;
        }

        /// <summary>
        /// Read the authored per-body animal spawn rules out of <c>world_recipe.json</c>, at plugin
        /// <c>Init</c> for the same reason as the ore map: SE decides whether a body spawns animals
        /// when the planet is added during world load. Absent, unreadable and rejected all leave every
        /// body on its generator's own lists, and each says so.
        /// </summary>
        private FaunaPolicy LoadFaunaPolicy(string dataDir, out string status)
        {
            FaunaPolicy empty;
            string ignored;
            FaunaPolicy.TryBuild(null, out empty, out ignored);

            bool usedFallback;
            string path = DataDirectory.ResolveInput(WorldRecipe.FileName, dataDir, InstanceDir,
                File.Exists, out usedFallback);
            if (!File.Exists(path))
            {
                status = WorldRecipe.FileName + ": absent -- every body keeps its generator's own animals.";
                return empty;
            }

            string error;
            WorldRecipe recipe;
            try { recipe = WorldRecipe.Parse(File.ReadAllText(path), out error); }
            catch (Exception ex) { recipe = null; error = ex.Message; }
            if (recipe == null)
            {
                status = WorldRecipe.FileName + ": UNREADABLE (" + error
                       + ") -- every body keeps its generator's own animals, so any authored fauna is NOT in effect.";
                return empty;
            }

            FaunaPolicy policy;
            string why;
            if (!FaunaPolicy.TryBuild(recipe, out policy, out why))
            {
                status = WorldRecipe.FileName + ": fauna REJECTED (" + why
                       + ") -- every body keeps its generator's own animals.";
                return empty;
            }

            if (policy.Count == 0)
            {
                status = WorldRecipe.FileName + ": no Fauna authored -- every body keeps its generator's own animals.";
                return policy;
            }

            var parts = new List<string>();
            foreach (FaunaPolicy.Entry e in policy.Entries)
                parts.Add(e.Name + " [" + e.Generator + "]=" + e.Rules.Count + " rule(s)");
            status = WorldRecipe.FileName + ": " + policy.Count + " body/bodies with authored fauna -- "
                   + string.Join(", ", parts.ToArray()) + " read=" + path;
            return policy;
        }

        /// <summary>
        /// Read the authored per-body surface rule out of <c>world_recipe.json</c>. Absent file,
        /// unparseable file and a recipe with no opinion are all the same outcome — defer to SE's own
        /// flag — but they are reported differently, because "no rule authored" and "your rule failed
        /// to load" are the same silence otherwise.
        /// </summary>
        private SurfaceNodePolicy LoadSurfaceNodePolicy(out string status, string dataDir = null)
        {
            SurfaceNodePolicy empty;
            string ignored;
            SurfaceNodePolicy.TryBuild(null, out empty, out ignored);

            string path = DataDirectory.ResolveInput(WorldRecipe.FileName, dataDir ?? StorageDir, InstanceDir,
                File.Exists, out bool usedFallback);
            WarnIfInputCopiesDiffer(WorldRecipe.FileName);
            LogInputRead(WorldRecipe.FileName, path);
            if (!File.Exists(path))
            {
                status = WorldRecipe.FileName + ": absent -- every body defers to SE's own "
                       + "AllowSurfaceEconomyStations. This is the shipped default.";
                return empty;
            }

            string error;
            WorldRecipe recipe;
            try { recipe = WorldRecipe.Parse(File.ReadAllText(path), out error); }
            catch (Exception ex) { recipe = null; error = ex.Message; }
            if (recipe == null)
            {
                status = WorldRecipe.FileName + ": UNREADABLE (" + error
                       + ") -- every body defers to SE's flag, so any authored surface rule is NOT in effect.";
                return empty;
            }

            SurfaceNodePolicy policy;
            string why;
            if (!SurfaceNodePolicy.TryBuild(recipe, out policy, out why))
            {
                status = WorldRecipe.FileName + ": surface rule REJECTED (" + why
                       + ") -- every body defers to SE's flag.";
                return empty;
            }

            // The separation clause is appended only when something is authored, so this line stays
            // byte-identical on a world that authors none -- docs/live-state.md carries a verified
            // acceptance row against its current shape, and silently reshaping it would invalidate a
            // check that has already passed.
            if (policy.Count == 0 && policy.SeparationCount == 0 && policy.OcclusionCount == 0
                && policy.NativeCropCount == 0)
            {
                status = WorldRecipe.FileName + ": no AllowSurfaceNodes authored -- every body defers to SE's flag.";
                return policy;
            }

            var parts = new List<string>();
            foreach (KeyValuePair<string, bool> e in policy.Entries)
                parts.Add(e.Key + "=" + (e.Value ? "landable" : "NO SURFACE NODES"));

            status = WorldRecipe.FileName + ": " + policy.Count + " authored body rule(s) -- "
                   + string.Join(", ", parts.ToArray());

            // An authored separation that failed to parse is otherwise indistinguishable from one
            // never written: nothing else reports it, and on a world with no nodes nothing exercises it.
            if (policy.SeparationCount > 0)
            {
                var seps = new List<string>();
                foreach (KeyValuePair<string, double> e in policy.SeparationEntries)
                    seps.Add(e.Key + "=" + e.Value.ToString("0.###") + " km");
                status += "; " + policy.SeparationCount + " surface separation(s) -- "
                        + string.Join(", ", seps.ToArray());
            }

            // An authored occluding radius is otherwise unreadable anywhere on a running server: the
            // sweep's bodies= count includes a body occluding at its own terrain radius, so a gas
            // giant reporting SE's 9.5 km voxel core is counted exactly like one at the authored
            // 180 km.
            if (policy.OcclusionCount > 0)
            {
                var occl = new List<string>();
                foreach (KeyValuePair<string, double> e in policy.OcclusionEntries)
                    occl.Add(e.Key + "=" + e.Value.ToString("0.###") + " km");
                status += "; " + policy.OcclusionCount + " occluding radius/radii -- "
                        + string.Join(", ", occl.ToArray());
            }

            // What a Farm grows without seeds. Kor's empty list is reported too: an authored "nothing
            // grows here" and a list that failed to load must not read alike.
            if (policy.NativeCropCount > 0)
            {
                var crops = new List<string>();
                foreach (KeyValuePair<string, string[]> e in policy.NativeCropEntries)
                    crops.Add(e.Key + "=" + (e.Value.Length > 0 ? string.Join("+", e.Value) : "none"));
                status += "; " + policy.NativeCropCount + " native crop list(s) -- "
                        + string.Join(", ", crops.ToArray());
            }

            status += usedFallback ? " (read from this instance)" : "";
            return policy;
        }

        /// <summary>
        /// <c>factions world oremap</c> — every live body's <b>resolved</b> ore map, in the shape
        /// <c>world_recipe.json</c> authors it.
        ///
        /// <para><b>Why a verb and not a file read.</b> The map a body actually carries is the one in
        /// the resolved definition set, and a mod can rewrite that from C# after every <c>.sbc</c>
        /// has had its say — reading a planet mod's files sees a world the server does not have.
        /// Authoring a map by transcribing a <c>.sbc</c> therefore risks changing the body while
        /// intending to copy it.</para>
        ///
        /// <para>It is also the check: run it either side of a restart, and an authored map that was
        /// meant to reproduce what the body already had must produce byte-identical output.</para>
        ///
        /// <para>A command response, not a log line, so it may explain itself.</para>
        /// </summary>
        private string HandleWorldOreMap()
        {
            if (MySession.Static == null) return "world oremap: no session loaded.";

            List<MyPlanet> live = WorldBuilder.LivePlanets();

            // Sorted, because the headline use of this verb is to diff two runs of it. SE's own
            // planet list is in entity-registration order, which is not stable across boots: the
            // first live pair of runs differed only by Agni moving from fourth place to third,
            // with every body's map byte-identical. An unsorted dump makes a real diff unreadable
            // and an identical one look changed.
            live.Sort(delegate (MyPlanet a, MyPlanet b)
            {
                string ga = a == null || a.Generator == null ? "" : a.Generator.Id.SubtypeName;
                string gb = b == null || b.Generator == null ? "" : b.Generator.Id.SubtypeName;
                int byGenerator = string.CompareOrdinal(ga, gb);
                if (byGenerator != 0) return byGenerator;
                return string.CompareOrdinal(a == null ? "" : (a.StorageName ?? ""),
                                             b == null ? "" : (b.StorageName ?? ""));
            });

            var sb = new StringBuilder("world oremap: " + live.Count
                + " body/bodies, as world_recipe.json authors them.");

            foreach (MyPlanet p in live)
            {
                string generator = p.Generator == null ? "?" : p.Generator.Id.SubtypeName;
                sb.Append("\n  \"Name\": \"").Append(p.DisplayName ?? "Planet")
                  .Append("\", \"Definition\": \"").Append(generator)
                  .Append("\"   storage=").Append(p.StorageName ?? "(none)");

                VRage.Game.MyPlanetOreMapping[] maps = p.Generator == null ? null : p.Generator.OreMappings;
                if (maps == null || maps.Length == 0)
                {
                    sb.Append("\n    \"OreMappings\": []");
                    continue;
                }

                // SE keys its decode table by this byte and keeps a list per value; the recipe schema
                // allows one material per value, so a body carrying two is not transcribable as-is.
                var seen = new Dictionary<byte, string>();
                var clashes = new List<string>();
                foreach (VRage.Game.MyPlanetOreMapping m in maps)
                {
                    if (m == null) continue;
                    string other;
                    if (seen.TryGetValue(m.Value, out other)) clashes.Add(m.Value + " (" + other + ", " + m.Type + ")");
                    else seen[m.Value] = m.Type;
                }

                sb.Append("\n    \"OreMappings\": [");
                for (int i = 0; i < maps.Length; i++)
                {
                    VRage.Game.MyPlanetOreMapping m = maps[i];
                    if (m == null) continue;
                    sb.Append("\n      { \"Value\": ").Append(m.Value)
                      .Append(", \"Material\": \"").Append(m.Type ?? "")
                      .Append("\", \"Start\": ").Append(m.Start.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append(", \"Depth\": ").Append(m.Depth.ToString("0.###", CultureInfo.InvariantCulture));
                    string colour = m.TargetColor;
                    if (!string.IsNullOrEmpty(colour))
                        sb.Append(", \"TargetColor\": \"").Append(colour)
                          .Append("\", \"ColorInfluence\": ")
                          .Append(m.ColorInfluence.ToString("0.###", CultureInfo.InvariantCulture));
                    sb.Append(" }").Append(i == maps.Length - 1 ? "" : ",");
                }
                sb.Append("\n    ]");

                if (clashes.Count > 0)
                    sb.Append("\n    NOT TRANSCRIBABLE: value(s) ").Append(string.Join("; ", clashes.ToArray()))
                      .Append(" appear more than once. SE keeps both; the recipe allows one material "
                            + "per value, so authoring this body would refuse on load.");
            }

            return sb.ToString();
        }

        /// <summary>
        /// <c>factions world plan|apply|status|purge</c>. The first three are the world builder:
        /// they create the bodies an authored <c>world_recipe.json</c> describes and optionally emit
        /// the <c>world_features.json</c> whose anchors point at them (docs/world-builder.md).
        /// <c>purge</c> removes vanilla's own faction station records, which is what makes a world
        /// blank enough to build into.
        /// </summary>
        private string HandleWorld(string[] t)
        {
            string sub = t.Length > 2 ? t[2].ToLowerInvariant() : "plan";
            bool wantFeatures = false;
            for (int i = 3; i < t.Length; i++)
                if (string.Equals(t[i], "--features", StringComparison.OrdinalIgnoreCase)) wantFeatures = true;

            if (sub == "purge") return HandleWorldPurge(t);
            if (sub == "estate") return HandleWorldEstate(t);
            if (sub == "relations") return HandleWorldRelations(t);
            if (sub == "oremap") return HandleWorldOreMap();

            if (sub != "plan" && sub != "apply" && sub != "status")
                return "world: usage 'factions world plan|apply|status [--features]', "
                     + "'factions world purge [orphans] [apply] [tag]', 'factions world estate [apply] [seed]', "
                     + "'factions world relations [apply]' or 'factions world oremap'";

            if (MySession.Static == null) return "world: no session loaded.";

            var types = new SePlanetTypeCatalogue();
            List<MyPlanet> live = WorldBuilder.LivePlanets();

            if (sub == "status")
            {
                var sb = new StringBuilder("world: " + live.Count + " planet(s), "
                    + WorldBuilder.CollectObstacles().Count + " station record(s); "
                    + types.Count + " planet type(s) resolved; MaxPlanets="
                    + MySession.Static.Settings.MaxPlanets + ".");
                foreach (MyPlanet p in live)
                {
                    VRageMath.Vector3D c = p.PositionComp.GetPosition();
                    sb.Append("\n  ").Append(p.DisplayName ?? "planet").Append(" [")
                      .Append(p.Generator == null ? "?" : p.Generator.Id.SubtypeName).Append("] at (")
                      .Append((c.X / 1000.0).ToString("0.#")).Append(",")
                      .Append((c.Y / 1000.0).ToString("0.#")).Append(",")
                      .Append((c.Z / 1000.0).ToString("0.#")).Append(") km id=").Append(p.EntityId);
                }
                sb.Append("\n  types: ").Append(string.Join(", ", types.Names().ToArray()));
                return sb.ToString();
            }

            bool usedFallback;
            string recipePath = DataDirectory.ResolveInput(WorldRecipe.FileName, StorageDir, InstanceDir,
                File.Exists, out usedFallback);
            WarnIfInputCopiesDiffer(WorldRecipe.FileName);
            LogInputRead(WorldRecipe.FileName, recipePath);
            if (!File.Exists(recipePath))
                return "world: no " + WorldRecipe.FileName + " at " + recipePath
                     + ". Copy one from instance/worlds/<world>/ — see instance/worlds/README.md.";

            string error;
            WorldRecipe recipe = WorldRecipe.Parse(File.ReadAllText(recipePath), out error);
            if (recipe == null) return "world: " + error;

            List<PlacementObstacle> obstacles = WorldBuilder.CollectObstacles();
            WorldPlan plan = WorldPlanner.Plan(recipe, types, MySession.Static.Settings.MaxPlanets, obstacles);
            string described = WorldBuilder.Describe(plan, live.Count, obstacles.Count)
                             + (usedFallback ? "\n  . recipe read from the instance directory, not the data directory." : "");

            if (sub == "plan")
            {
                // --features emits on PLAN, not only on apply. world_features.json's own header says
                // it is "generated by 'factions world plan'", and it was not: the emit sat below the
                // apply gate, which refuses any world that is not planet-blank. So the documented way
                // to add a feature to an existing world could never be run on one -- found authoring
                // FCTM's nests (FAC-104). Emitting is a file write derived from the recipe; it
                // creates nothing in the session and needs no blank world.
                if (wantFeatures)
                {
                    // Anchors resolve from RealisedEntityId, which only apply sets -- it is the id of
                    // a planet it just created. On an existing world the bodies are already there, so
                    // adopt their ids: without this the emit writes an UNANCHORED ring, and an
                    // unanchored ring stops following the gas giant. Real Gas Giants respawns its own
                    // body "at a position of its choosing" (world_recipe.json), so that is a live
                    // hazard, not a theoretical one. Matched on generator subtype, the same key the
                    // recipe uses for a body.
                    foreach (MyPlanet lp in live)
                    {
                        if (lp == null || lp.Generator == null) continue;
                        string subtype = lp.Generator.Id.SubtypeName;
                        foreach (PlannedPlanet pp in plan.Planets)
                            if (pp != null && pp.RealisedEntityId == 0L
                                && string.Equals(pp.Definition, subtype, StringComparison.OrdinalIgnoreCase))
                            { pp.RealisedEntityId = lp.EntityId; break; }
                    }
                    described += "\n  " + WorldBuilder.EmitFeatures(plan, StorageDir, InstanceDir);
                    // The ring feature and RGG's own config are two halves of one annulus, so they
                    // are emitted by one flag: regenerating either alone is how they come to disagree.
                    // Staging needs nothing apply sets -- PlannedPlanet.StorageName derives from the
                    // recipe alone -- and writes only into Factions' own directory, so it is available
                    // on a built world for the same reason the features emit is (FAC-104).
                    described += StageGasGiants(plan, recipe, justCreated: false);
                }
                return described;
            }

            // Apply. Planet-blank is the one precondition this stage owns; everything else it found
            // is reported above rather than blocked on.
            if (live.Count > 0)
                return described + "\n  REFUSED: this world already holds " + live.Count
                     + " planet(s). Apply only to a planet-blank world.";
            if (!plan.IsApplicable)
                return described + "\n  REFUSED: the plan does not apply cleanly; nothing was created.";

            WorldBuilder.ApplyResult applied = WorldBuilder.Apply(plan, types);
            // The planet index is built once and never rebuilt, because nothing in the runtime can
            // create a planet. This verb can, so it is the one place that has to say so.
            if (applied.Created.Count > 0 && _planetCatalogue != null)
            {
                _planetCatalogue.Invalidate();
                _runtime?.InvalidateSensorOcclusion();
            }
            string result = described + "\n  applied: created " + applied.Created.Count + " body/bodies"
                          + (applied.Created.Count > 0 ? " [" + string.Join(", ", applied.Created.ToArray()) + "]" : "")
                          + ".";
            if (applied.Aborted)
            {
                Log.Warn("World apply ABORTED: " + applied.Failure);
                return result + "\n  ABORTED: " + applied.Failure;
            }

            Log.Info("World apply created " + applied.Created.Count + " planet(s).");
            if (wantFeatures) result += "\n  " + WorldBuilder.EmitFeatures(plan, StorageDir, InstanceDir);

            result += StageGasGiants(plan, recipe, justCreated: true);
            return result;
        }

        /// <summary>Merges this plan's gas giants into a staging copy of Real Gas Giants' config and
        /// returns what to tell the operator, including the stop/copy/start they have to do by hand.
        /// Empty when the recipe has no gas giants. <paramref name="justCreated"/> distinguishes the
        /// bodies apply has just made, which carry no ring at all until this lands, from an existing
        /// world's, which carry whatever the live config already said.</summary>
        private string StageGasGiants(WorldPlan plan, WorldRecipe recipe, bool justCreated)
        {
            RggConfigWriter.Result rgg = RggConfigWriter.Write(plan, MySession.Static.CurrentPath,
                recipe.GasGiantConfigFolder, StorageDir);
            if (!rgg.Ok)
                return "\n  gas giants: staging FAILED -- " + rgg.Failure;
            if (rgg.Written <= 0) return string.Empty;

            return "\n  gas giants: staged " + rgg.Written + " entry/entries ("
                 + rgg.Replaced + " replaced) to " + rgg.StagingPath
                 + (justCreated
                     ? "\n  The bodies exist but are bare defaults -- no ring, no skin -- until this is placed."
                     : "\n  Until this is placed the ring RGG draws is whatever its live config already"
                       + " said, which need not be the annulus world_features.json models.")
                 + "\n  DO NOT RESTART to apply it: Real Gas Giants rewrites its config from memory on"
                 + " every save, including the shutdown save, so a restart destroys the file on the way"
                 + " down. Stop, copy, start:"
                 + "\n    1. stop the server (a full stop, not a bridge restart)"
                 + "\n    2. copy  " + rgg.StagingPath
                 + "\n       over  " + rgg.LivePath
                 + "\n    3. start the server"
                 + "\n  Confirm with 'RGG ReadFromSaveFile SKIP (OverrideFromConfig)' in the Keen log."
                 + " That proves the source, not the radius -- read the ring's OUTER bands in game."
                 + " The first save after that makes it permanent. See docs/issues/FAC-087, FAC-141.";
        }

        /// <summary>Shared by the bridge verb and the chat command.</summary>
        public string RunSanitize(bool apply)
        {
            if (MySession.Static == null || MySession.Static.Factions == null) return "sanitize: no world loaded.";

            FactionSanitizer.Plan plan = FactionSanitizer.Build(Store.LoadConfig().IgnoredTags);
            if (!apply) return FactionSanitizer.Describe(plan, false, 0, 0);

            int gridsClosed, factionsRemoved;
            FactionSanitizer.Apply(plan, out gridsClosed, out factionsRemoved);

            var forgotten = new List<string>();
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IndexEntry e in Store.LoadIndex()) registered.Add(e.Tag);
            foreach (FactionSanitizer.FactionPlan fp in plan.Remove)
                if (registered.Contains(fp.Tag)) { ForgetFaction(fp.Tag); forgotten.Add(fp.Tag); }

            string msg = FactionSanitizer.Describe(plan, true, gridsClosed, factionsRemoved);
            if (forgotten.Count > 0)
                msg += "\n  also forgotten from the Factions registry: " + string.Join(", ", forgotten);
            Log.Info("Sanitize removed " + factionsRemoved + " faction(s) and closed " + gridsClosed + " grid(s).");
            return msg;
        }

        /// <summary>
        /// `factions gridspeed [&lt;large&gt; [&lt;small&gt;]]` — report or set the world's grid speed
        /// limits in m/s. With no arguments it reports; with one it sets large only; with two it sets
        /// both. Omitting the small value deliberately leaves it alone rather than mirroring the
        /// large one: the two are independent limits in SE and a caller that wanted them equal can
        /// say so.
        /// </summary>

        /// <summary>`factions target &lt;nodeId&gt; [keyFilter]` — what this node should be holding of
        /// each item and which term decided it. The in-game instrument for the target-stock ranking:
        /// every number a consumer reads comes off `target`, so a wrong price, a recipe that will
        /// not start, or a glut nothing drains is read here first.
        ///
        /// <para>Rows are sorted by target descending and capped, because a busy node carries
        /// hundreds of keys. `none` rows — nothing consumes, owes, wants or buys it — are omitted
        /// unless the caller names a filter, since they are the majority and carrying them would
        /// bury the answer. The count of omitted rows is printed, so an empty table is
        /// distinguishable from a node this build has no opinion about.</para>
        ///
        /// <para>A command response, not a log line, so it may explain itself.</para></summary>
        public string HandleTarget(string[] t)
        {
            if (_runtime == null) return "factions target: no session loaded.";
            PriceOracle oracle = _runtime.TargetOracle;
            WorldModel world = _runtime.World;
            if (oracle == null || world == null) return "factions target: oracle not wired yet.";

            string nodeId = t.Length > 2 ? t[2] : null;
            if (string.IsNullOrEmpty(nodeId))
                return "factions target <nodeId> [keyFilter] -- e.g. 'factions target N007 Ore:'.";

            SpatialNode node;
            if (!world.Graph.Nodes.TryGetValue(nodeId, out node))
                return "factions target: no node '" + nodeId + "'.";

            string filter = t.Length > 3 ? t[3] : null;

            // Every key the node holds, plus every key it has a flow or a sale record for -- a
            // target of zero on something it is still consuming is exactly the case worth seeing,
            // and stock alone would miss it.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in node.Inventory) keys.Add(kv.Key);
            foreach (var kv in node.DemandHistory) keys.Add(kv.Key);
            foreach (var kv in node.SalesHistory) keys.Add(kv.Key);

            var rows = new List<PriceOracle.TargetBreakdown>();
            var names = new List<string>();
            int hiddenNone = 0;
            foreach (string key in keys)
            {
                if (filter != null && key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                PriceOracle.TargetBreakdown b = oracle.ExplainTarget(nodeId, key);
                if (b.Target <= 0 && filter == null) { hiddenNone++; continue; }
                rows.Add(b);
                names.Add(key);
            }

            var order = new List<int>();
            for (int i = 0; i < rows.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int cmp = rows[b].Target.CompareTo(rows[a].Target);
                return cmp != 0 ? cmp : string.CompareOrdinal(names[a], names[b]);
            });

            var sb = new StringBuilder();
            sb.AppendLine("target " + nodeId + " [" + (node.OwnerFactionTag ?? "-") + "]"
                          + (filter != null ? " filter='" + filter + "'" : "")
                          + " -- " + rows.Count + " row(s) of " + keys.Count + " key(s)"
                          + (hiddenNone > 0 ? ", " + hiddenNone + " with no target omitted" : ""));
            sb.AppendLine("  item                        target      stock  scarcity  won    flow      sold      owed    floor  reach cons");

            int shown = 0;
            foreach (int i in order)
            {
                if (shown++ >= TargetRowCap) break;
                PriceOracle.TargetBreakdown b = rows[i];
                sb.AppendLine("  " + Pad(names[i], 26)
                              + PadLeft(b.Target.ToString(), 9)
                              + PadLeft(b.Stock.ToString(), 11)
                              + PadLeft(b.Scarcity.ToString("0.00"), 10)
                              + "  " + Pad(b.Winner + (b.FloorReason != null ? ":" + b.FloorReason : ""), 13)
                              + PadLeft(b.Flow.ToString(), 8)
                              + PadLeft(b.Sold.ToString(), 10)
                              + PadLeft(b.Owed.ToString(), 9)
                              + PadLeft(b.Floor.ToString(), 8)
                              + PadLeft(b.Reach.ToString("0.00"), 7)
                              + PadLeft(b.Consumers.ToString(), 5));
            }
            if (order.Count > TargetRowCap)
                sb.AppendLine("  ... " + (order.Count - TargetRowCap) + " more; narrow with a key filter.");

            sb.AppendLine("  won=flow: consumed here. sold: bought from here (trade/player, never the bank).");
            sb.AppendLine("  owed: a ship or install BOM. floor:gas|wanted|bankglut. none: no opinion, scarcity 1.00.");
            return sb.ToString();
        }

        /// <summary>Row cap for <see cref="HandleTarget"/>. A live industrial node carries several
        /// hundred keys and the bridge outbox is read by eye.</summary>
        private const int TargetRowCap = 40;

        private static string Pad(string s, int width)
        {
            if (s == null) s = "";
            return s.Length >= width ? s.Substring(0, width) : s + new string(' ', width - s.Length);
        }

        private static string PadLeft(string s, int width)
        {
            if (s == null) s = "";
            return s.Length >= width ? s : new string(' ', width - s.Length) + s;
        }

        /// <summary><c>factions feature</c> lists every <see cref="FeatureSwitch"/> with its state;
        /// <c>factions feature &lt;name&gt; on|off</c> sets one. Unknown names are refused. Nothing is
        /// persisted: every switch returns to its default at the next boot.</summary>
        private static string HandleFeature(string[] t)
        {
            if (t.Length < 3)
            {
                var sb = new StringBuilder("feature:");
                foreach (string name in FeatureSwitch.Known)
                    sb.Append(' ').Append(name).Append('=').Append(FeatureSwitch.IsOn(name) ? "on" : "off");
                return sb.ToString();
            }
            string state = t.Length > 3 ? t[3].ToLowerInvariant() : "";
            if (state != "on" && state != "off")
                return "Usage: factions feature [<name> on|off]";
            if (!FeatureSwitch.Set(t[2], state == "on"))
                return "feature: unknown switch '" + t[2] + "'; known: " + string.Join(", ", FeatureSwitch.Known) + ".";
            Log.Info("Feature switch " + t[2].ToLowerInvariant() + " set " + state + ".");
            return "feature: " + t[2].ToLowerInvariant() + " is " + state + " until it is switched off or the server restarts.";
        }

        public string HandleGridSpeed(string[] t)
        {
            if (_gridSpeedManager == null) return "gridspeed: no session loaded.";
            if (t.Length < 3) return _gridSpeedManager.Describe();

            double large;
            string error;
            if (!GridSpeedPolicy.TryParseSpeed(t[2], out large, out error))
                return "gridspeed: large-grid " + error + ". Usage: factions gridspeed <large> [small]";

            double? small = null;
            if (t.Length > 3)
            {
                double parsedSmall;
                if (!GridSpeedPolicy.TryParseSpeed(t[3], out parsedSmall, out error))
                    return "gridspeed: small-grid " + error + ". Usage: factions gridspeed <large> [small]";
                small = parsedSmall;
            }

            return _gridSpeedManager.Set(large, small, ConfigStore);
        }

        /// <summary>
        /// `factions missilespeed [&lt;scale&gt;]` — report or set the multiplier applied to every
        /// missile-class munition's speed. With no argument it reports; with one it sets, persists
        /// and re-applies from the definitions as they loaded, so the value is absolute rather than
        /// compounding. 1 restores the content exactly.
        /// </summary>
        public string HandleMissileSpeed(string[] t)
        {
            if (_munitionSpeedManager == null) return "missilespeed: no session loaded.";
            if (t.Length < 3) return _munitionSpeedManager.Describe();

            double scale;
            string error;
            if (!MunitionSpeedPolicy.TryParseScale(t[2], out scale, out error))
                return "missilespeed: " + error + ". Usage: factions missilespeed <scale>";

            return _munitionSpeedManager.Set(scale, ConfigStore);
        }

        /// <summary>
        /// `factions fuel [reset]` — what docs/fuel-plan.md's burn model says every leg this
        /// process has dispatched would have cost, per faction and per action kind. The ledger is
        /// in-memory and starts empty at every plugin load; `reset` reopens its window.
        ///
        /// <para>In <c>FuelDrawMode</c> 1 nothing is drawn: the numbers are the model priced
        /// against live traffic, which is the reading the balance decision needs and which the Sim
        /// cannot supply — its traffic mix is not the server's.</para>
        /// </summary>
        public string HandleFuel(string[] t)
        {
            if (_runtime == null || _runtime.World == null) return "fuel: runtime not started (no session loaded).";
            Factions.Core.FuelLedger ledger = _runtime.World.FuelLedger;
            if (ledger == null) return "fuel: no ledger on this world.";

            DateTime nowUtc = DateTime.UtcNow;
            if (t.Length > 2 && t[2].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                ledger.Reset(nowUtc);
                return "fuel: ledger cleared; the window reopens now. Cached leg profiles are kept -- "
                     + "they describe geometry, which clearing a measurement window does not change.";
            }

            int mode = _tuning != null ? _tuning.FuelDrawMode : Factions.Core.FuelDrawModes.Measure;
            return Factions.Core.FuelLedgerReport.Format(ledger.Snapshot(mode), nowUtc);
        }

        /// <summary>
        /// One-shot admin cleanup for a specific physical grid: an orphaned duplicate left behind by
        /// <see cref="StationFounder.MigrateMismatchedStationPrefabs"/>, or a station grid SE's own
        /// economy spawned for a record a Factions node owns. Dry-run by default;
        /// 'factions closegrid &lt;entityId&gt; apply' closes it.
        ///
        /// <para>Grid-local, so it runs on a Nexus agent as well as on the authority: the grids a
        /// player sees on an agent's sector exist only in that process.</para>
        ///
        /// <para>A station record that names the grid is unlinked, not removed
        /// (<see cref="StationMaterializer.UnlinkStationRecords"/>). On this cluster every record
        /// belongs to a Factions node, so the record must survive for the node to materialise its
        /// own grid when a player next comes in range.</para>
        /// </summary>
        private string HandleCloseGrid(string[] t)
        {
            if (t.Length < 3) return "closegrid: usage 'factions closegrid <entityId> [apply]'";
            long entityId;
            if (!long.TryParse(t[2], out entityId)) return "closegrid: '" + t[2] + "' is not a valid entity id.";
            bool apply = t.Length > 3 && string.Equals(t[3], "apply", StringComparison.OrdinalIgnoreCase);

            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid == null)
                return "closegrid: entity " + entityId + " not found (not currently loaded, or already closed).";

            string owner = "unowned";
            if (grid.BigOwners != null && grid.BigOwners.Count > 0)
            {
                VRage.Game.ModAPI.IMyFaction f = MySession.Static.Factions.TryGetPlayerFaction(grid.BigOwners[0]);
                owner = f != null ? f.Tag : "identity " + grid.BigOwners[0];
            }
            string info = "closegrid: found grid \"" + grid.DisplayName + "\" (" + grid.BlocksCount + " blocks, " + owner
                + ") at " + grid.PositionComp.GetPosition();
            List<string> records = StationMaterializer.UnlinkStationRecords(entityId, false);
            string linked = records.Count == 0
                ? "; no station record names it"
                : "; named by station record " + string.Join(", ", records.ToArray());
            if (!apply) return info + linked + " -- dry run, 'factions closegrid " + entityId + " apply' to close it.";

            StationMaterializer.UnlinkStationRecords(entityId, true);
            grid.Close();
            MySession.Static.GetComponent<MySessionComponentEconomy>()?.RemoveStationGrid(entityId);
            string msg = info + linked + " -- closed and deregistered from the economy system"
                + (records.Count > 0 ? "; record(s) unlinked and kept." : ".");
            Log.Info("closegrid: closed \"" + grid.DisplayName + "\" eid=" + entityId + " owner=" + owner
                + " unlinked=" + records.Count);
            return msg;
        }

        /// <summary>
        /// Stands a finished station rebuild up now, without waiting for the station to be empty --
        /// the headless counterpart of `!factions station refit apply`, which needs a player to type
        /// it. With a node id it applies that one; with none, every node holding a finished rebuild.
        /// </summary>
        private string HandleRefitApply(string[] t)
        {
            if (_runtime == null) return "refitapply: runtime not started.";
            string nodeId = t.Length >= 3 ? t[2] : null;
            int applied = _runtime.ApplyPendingStationRefits(true, "bridge", nodeId);
            return "refitapply: stood up " + applied + " pending station rebuild(s)"
                   + (nodeId != null ? " at " + nodeId : " world-wide") + ".";
        }

        /// <summary>
        /// Remove a faction from the registry and from the live runtime. Uses the same flush→edit→load
        /// pattern as <see cref="HandleIgnore"/> so the runtime does not resurrect the faction on the
        /// next Flush (which rebuilds index.json from _states).
        /// </summary>
        public string ForgetFaction(string tag)
        {
            if (_runtime != null) _runtime.Flush();   // persist OTHER factions' live state before editing the registry
            List<IndexEntry> idx = Store.LoadIndex();
            bool registered = idx.Exists(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));
            // The faction's nodes go with its brain; the SE station records they name do not, and a
            // record no node names is outside the vanilla-suppression claim. Removed here, while the
            // graph still says which records were this faction's.
            int stations = registered && _runtime != null ? _runtime.PurgeStationRecordsOf(tag) : 0;
            int removed = idx.RemoveAll(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) { Store.SaveIndex(idx); Store.DeleteBrain(tag); }
            if (_runtime != null) _runtime.Load();    // rebuild from the edited disk state (no Reload — that re-flushes)
            return removed > 0
                ? "Removed '" + tag + "' and " + stations + " of its station record(s)."
                : "'" + tag + "' was not registered.";
        }

        /// <summary>
        /// Register every in-game NPC faction (IsEffectivelyNpc) not already present, writing an index
        /// entry + an initial brain file for each. Existing brains are preserved (idempotent).
        /// </summary>
        public InitResult InitializeFromNpcFactions()
        {
            var result = new InitResult();
            if (MySession.Static == null || MySession.Static.Factions == null) return result;

            if (_runtime != null) _runtime.Flush();   // persist live state before editing the registry (load-after below)

            FactionsConfig cfg = Store.LoadConfig();
            var ignored = new HashSet<string>(cfg.IgnoredTags ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            List<IndexEntry> index = Store.LoadIndex();
            var existing = new HashSet<string>(index.Select(e => e.Tag), StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction f = kv.Value;
                if (f == null) continue;
                if (!IsEffectivelyNpc(f)) { result.NonNpc++; result.NonNpcTags.Add(f.Tag); continue; }
                if (ignored.Contains(f.Tag)) { result.Ignored++; continue; }   // animal/system factions (e.g. SPID)
                if (existing.Contains(f.Tag)) { result.Skipped++; continue; }

                // UNKN (NeutralFaction.Tag) is the abandoned-node graveyard, not a real competitive
                // faction — FactionLifecycle.IsDormant already keeps it out of Think()/managers
                // regardless of PersonalityId; this only steers RenameConqueredStation toward the
                // "derelict" naming theme instead of whatever PersonalityFromFaction would guess.
                var state = new FactionState
                {
                    Tag = f.Tag,
                    FactionId = f.FactionId,
                    PersonalityId = string.Equals(f.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase)
                        ? "derelict" : PersonalityFromFaction(f),
                    RngSeed = FactionLifecycle.DeriveRngSeed(f.FactionId)
                };
                BrainDoc brain = FactionStore.ToBrainDoc(state, Enumerable.Empty<SpatialNode>(), FactionStore.DefaultLogCap);
                brain.Treasury = _tuning.SeedTreasury;   // one-time starting capital, stamped at registration
                Store.SaveBrain(brain);
                index.Add(FactionStore.ToIndexEntry(state, DateTime.UtcNow));
                existing.Add(f.Tag);
                result.AddedTags.Add(f.Tag);
                result.Added++;
            }

            Store.SaveIndex(index);
            if (_runtime != null) _runtime.Load();   // rebuild from the updated registry (flush ran first, above)
            Log.Info("Init registered " + result.Added + " NPC faction(s), skipped " + result.Skipped + ".");
            return result;
        }

        /// <summary>
        /// True if every faction member is a registered NPC identity, an SE bot pedestrian
        /// (station crew, SerialId != 0), or an orphaned save entry with no player behind it.
        /// SE's own <c>IsEveryoneNpc()</c> returns false for bot pedestrians because they are
        /// never added to <c>m_npcIdentities</c>, even though no real player is behind them.
        /// </summary>
        internal static bool IsEffectivelyNpc(MyFaction f)
        {
            foreach (var kv in f.Members)
                if (IsHumanIdentity(kv.Key)) return false;
            return true;
        }

        /// <summary>
        /// True if a human player is behind this identity: not a registered NPC identity, not an SE
        /// bot pedestrian (station crew, SerialId != 0), and not an orphaned save entry. 0 is false.
        ///
        /// Judged from SE's identity-to-player mapping rather than from a live <c>MyPlayer</c>:
        /// <c>TryGetPlayer</c> resolves only *connected* players, while <c>TryGetPlayerId</c> also
        /// answers for a disconnected one, whose mapping <c>MyPlayerCollection.LoadPlayerInternal</c>
        /// keeps when it loads the save. An offline human is therefore still a human here.
        /// </summary>
        internal static bool IsHumanIdentity(long identityId)
        {
            if (identityId == 0) return false;
            if (MySession.Static.Players.IdentityIsNpc(identityId)) return false;

            // Connected: MyPlayer.IsBot also honours the forced-real-player flag, so prefer it.
            MyPlayer connected = MySession.Static.Players.TryGetPlayer(identityId);
            if (connected != null) return !connected.IsBot;

            MyPlayer.PlayerId playerId;
            // In neither map: an orphaned identity with nobody behind it.
            if (!MySession.Static.Players.TryGetPlayerId(identityId, out playerId)) return false;
            // No platform id behind the mapping.
            if (!playerId.IsValid) return false;
            // Bot pedestrian: MyPlayer.IsRealPlayer is SerialId == 0.
            return playerId.SerialId == 0;
        }

        /// <summary>
        /// Infer a starting personality from SE faction metadata. Called once at registration;
        /// the assignment is persisted in the brain file and can be overridden manually.
        ///
        /// Primary signal: f.FactionTypeString (SE's own structured type, persisted in the save).
        /// Fallback: default hostile stance or static-hostile-reputation → raider.
        ///
        /// TODO: "Military" in SE covers mercenary corps, xenophobic closed societies, theocracies —
        /// deserves its own personality profile (high Security, moderate Rivalry, low Expansion,
        /// closed-border Xenophobia). For now it maps to "default" rather than mis-labelling it raider.
        /// Also verify live whether SPRT (Space Pirates) is tagged "Pirate" or "Military" — its long
        /// name suggests Pirate, but worth confirming via 'factions list' after init on a real world.
        /// </summary>
        private static string PersonalityFromFaction(MyFaction f)
        {
            // The Factorum is typed "None" and carries a static -1500 reputation, which reaches the
            // static-hostile fallback below and yields "raider" -- an expanding, trading profile. The
            // "factorum" profile (Expansionism 0.1, Mercantilism 0.0, Xenophobia 1.00) is the one
            // written for it, and its fleet-prefab pools and station-name theme are already keyed on
            // that id. Matched on the tag because SE carries no other signal that separates it.
            if (string.Equals(f.Tag, FactorumFactionTag, StringComparison.OrdinalIgnoreCase))
                return "factorum";

            string t = f.FactionTypeString;
            if (!string.IsNullOrEmpty(t))
            {
                if (t == "Pirate")                return "raider";
                if (t == "Trader")                return "mercantile";
                if (t == "Miner" || t == "Builder") return "settler";
                // "Military" → TODO: own personality; default for now
            }

            // Fallback: faction's baked-in default stance is hostile → raider
            if (f.DefaultFactionRelationshipAndReputation != null &&
                f.DefaultFactionRelationshipAndReputation.Item1 == MyRelationsBetweenFactions.Enemies)
                return "raider";

            // Fallback: static (unearnable) reputation AND very negative start → raider
            if (f.StaticReputation && f.StartingReputation.HasValue && f.StartingReputation.Value < -200)
                return "raider";

            return "default";
        }

        /// <summary>SE's vanilla Factorum faction. Engine-default (<c>IsDefault=true</c> in
        /// <c>Factions.sbc</c>), so it is present on every world and recreated on every load.</summary>
        internal const string FactorumFactionTag = "FCTM";

        /// <summary>Retired in favour of <see cref="FactionLifecycle.DeriveRngSeed"/>, which both
        /// registration paths now share.</summary>

        /// <summary>
        /// Scan the loaded world for static NPC-faction stations and record them as each faction's
        /// Layer-1 influence sources. Only writes to factions already in the registry (use
        /// <see cref="InitializeFromNpcFactions"/> to register first). Reconciliation: a faction's
        /// grid-backed sources are fully replaced by this scan (so removed grids drop out — use-it-or-
        /// lose-it), while engine-created sources are preserved untouched.
        /// MUST be called on the game thread (it enumerates entities).
        /// </summary>
        public ScanResult ScanWorldAssets()
        {
            return ScanWorldAssets(flushBeforeScan: true, reloadAfterScan: true);
        }

        /// <summary>Shared scan/merge transaction. Reconciliation invokes this with both lifecycle
        /// operations disabled because it owns the surrounding flush and final reload.</summary>
        private ScanResult ScanWorldAssets(bool flushBeforeScan, bool reloadAfterScan)
        {
            var result = new ScanResult();
            if (MySession.Static == null || MySession.Static.Factions == null) return result;

            if (flushBeforeScan && _runtime != null) _runtime.Flush();   // persist live engine state before the scan rewrites brains

            List<IndexEntry> index = Store.LoadIndex();
            WorldScanner.ScanData data = WorldScanner.Scan(index.Select(e => e.Tag).ToList(),
                _prefabBookValues != null ? new Func<string, long>(_prefabBookValues.StationBookValue) : null,
                _prefabBookValues != null
                    ? new Func<string, List<WeaponBand>>(prefabId => new List<WeaponBand>(_prefabBookValues.StationWeaponBands(prefabId)))
                    : null);

            result.GridsExamined = data.GridsExamined;
            result.QualifyingStations = data.QualifyingStations;
            result.UnregisteredGrids = data.UnregisteredGrids;
            result.EconomyStationsSeen = data.EconomyStationsSeen;
            result.EconomyStationsUnspawned = data.EconomyStationsUnspawned;

            foreach (KeyValuePair<string, List<SourceDoc>> kv in data.ByFaction)
            {
                BrainDoc brain = Store.LoadBrain(kv.Key);
                if (brain == null) continue; // indexed but no brain file — skip defensively

                // Keep engine sources ("N###"); replace all scanned sources (grid "G###" + economy "E###").
                var merged = new List<SourceDoc>();
                if (brain.Sources != null)
                    foreach (SourceDoc s in brain.Sources)
                        if (!SourceMapping.IsGridSourceId(s.Id) && !SourceMapping.IsEconomyStationSourceId(s.Id)) merged.Add(s);
                merged.AddRange(kv.Value);
                brain.Sources = merged;
                Store.SaveBrain(brain);

                result.SourcesWritten += kv.Value.Count;
                result.FactionsTouched++;
                result.PerFaction[kv.Key] = kv.Value.Count;
            }

            // Reload the live graph so E### nodes are immediately available to StationMaterializer.
            // Flush was already called above; Load() brings the updated brain files back into memory.
            if (reloadAfterScan && _runtime != null)
            {
                _runtime.Load();
                // A scan writes nodes but no edges, which used to leave a freshly scanned world with
                // no topology at all -- every node isolated, and nothing downstream of adjacency
                // (reclaim reach, path segments) able to say anything. Link them the same way the
                // manual retrofit does, then persist, so a fresh world starts connected instead of
                // needing someone to remember `factions densifytopology apply`.
                TopologyDensifyResult densified = _runtime.DensifyTopology(true);
                if (densified.LinksAdded > 0) _runtime.Flush();
                result.TopologyLinksAdded = densified.LinksAdded;
            }

            Log.Info("Scan examined " + result.GridsExamined + " grid(s), "
                      + result.QualifyingStations + " qualifying station(s) + " + result.EconomyStationsSeen
                      + " economy station(s) (" + result.EconomyStationsUnspawned + " unspawned added); wrote "
                      + result.SourcesWritten + " source(s) across " + result.FactionsTouched + " faction(s); "
                     + result.UnregisteredGrids + " on unregistered faction(s); "
                     + result.TopologyLinksAdded + " topology link(s) added.");
            return result;
        }

        public override void Dispose()
        {
            StopRuntime();
            // Before UnpatchAll: the physics postfix accumulates into static state this reads, and
            // the publisher owns a background thread that must be asked to drain.
            if (_serverPerf != null) { _serverPerf.Dispose(); _serverPerf = null; }
            _nexusRelay?.Unregister();
            _harmony?.UnpatchAll(_harmony.Id);
            if (_sessionManager != null) _sessionManager.SessionStateChanged -= OnSessionStateChanged;
            Instance = null;
            base.Dispose();
        }

        public sealed class InitResult
        {
            public int Added;
            public int Skipped;
            public int Ignored;
            public int NonNpc;
            public List<string> AddedTags  = new List<string>();
            public List<string> NonNpcTags = new List<string>();
        }

        public sealed class ScanResult
        {
            public int GridsExamined;
            public int QualifyingStations;
            public int SourcesWritten;
            public int FactionsTouched;
            public int UnregisteredGrids;
            public int EconomyStationsSeen;
            public int EconomyStationsUnspawned;
            /// <summary>Edges created by the post-scan densify. A fresh world reports a large number
            /// once and zero on every rescan afterwards -- the pass is idempotent.</summary>
            public int TopologyLinksAdded;
            public Dictionary<string, int> PerFaction = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
