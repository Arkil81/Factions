using System;
using System.Collections.Generic;
using System.Linq;

namespace Factions.Core
{
    /// <summary>The shared world model: graph + private ledger + enactment, fully SE-free. Implements both
    /// seam sides (IWorldView/IFactionActions) and exposes the derived influence heatmap (Layer 2). Used by
    /// the offline sim AND the host dry-run, so both run the exact same mechanics.</summary>
    public sealed partial class WorldModel : IWorldView, IFactionActions
    {
        public DateTime UtcNow { get; set; }
        public SpatialGraph Graph { get; } = new SpatialGraph();
        public IInfluenceField Influence { get; }
        public FactionTuning Tuning { get; set; } = new FactionTuning();   // income knobs (BaseYield/Upkeep/...)

        /// <summary>Raids (by fleet id) whose dwell fight the host is flying in waves
        /// (docs/fight-control.md). The grids decide such a fight even while a side has no hull on the
        /// field: its hulls are queued at the site, not gone. Not persisted; the host owns it.</summary>
        public HashSet<string> WavedMeetings { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Nodes a live fight closes to civilian traffic (FAC-384, docs/fight-control.md): the
        /// fight's own node and every node within <see cref="FightDebris.RadiusKm"/> of it. Mine is not
        /// dispatched to or from one; traffic control holds and diverts around one. Not persisted; the
        /// host owns it.</summary>
        public HashSet<string> ClosedForCombat { get; } = new HashSet<string>(StringComparer.Ordinal);

        private IItemCatalogue _catalogue;
        /// <summary>Item value catalogue. Defaults to <see cref="TuningItemCatalogue"/> the first time it is
        /// read. The SE plugin overrides this with an SE-backed implementation before the first tick.</summary>
        public IItemCatalogue Catalogue
        {
            get { return _catalogue ?? (_catalogue = new TuningItemCatalogue(Tuning)); }
            set { _catalogue = value; }
        }

        private IMemberStandingSource _memberStanding;
        /// <summary>Player-faction member-standing aggregate source. Defaults to
        /// <see cref="NullMemberStandingSource"/> the first time it is read; the SE plugin overrides
        /// it with a live source once the session is up.</summary>
        public IMemberStandingSource MemberStanding
        {
            get { return _memberStanding ?? (_memberStanding = NullMemberStandingSource.Instance); }
            set { _memberStanding = value; }
        }

        private IItemRegistry _registry;
        /// <summary>SubTypeId → ItemKind registry. Defaults to <see cref="NullItemRegistry"/> the first
        /// time it is read. The SE plugin overrides this with <c>SeItemRegistry</c> before the first tick.</summary>
        public IItemRegistry Registry
        {
            get { return _registry ?? (_registry = NullItemRegistry.Instance); }
            set { _registry = value; }
        }

        private IItemPriceCatalogue _priceCatalogue;
        public IItemPriceCatalogue PriceCatalogue
        {
            get { return _priceCatalogue; }
            set { _priceCatalogue = value; }
        }

        private IPrefabCatalogue _prefabCatalogue;
        /// <summary>Ship/station prefab weapon-band lookup. Null (the default) means fleet-combat-power
        /// queries return 0 — the offline sim and any host session that hasn't wired a live SE-backed
        /// catalogue yet degrade gracefully rather than throwing. The SE plugin wires this before the
        /// first tick, mirroring <see cref="Catalogue"/>/<see cref="Registry"/>/<see cref="Planets"/>.</summary>
        public IPrefabCatalogue PrefabCatalogue
        {
            get { return _prefabCatalogue; }
            set { _prefabCatalogue = value; }
        }

        /// <summary>The prefab pool's performance ceiling (FAC-385), as a lookup: true for a prefab
        /// this server will not build. Set by the host from the scanned catalogue; null (the offline
        /// sim, tests that do not set it) bans nothing.</summary>
        public Func<string, bool> PrefabOverCeiling { get; set; }

        /// <summary>The server's prefab ban list (FAC-451), as a lookup: true for a prefab whose design
        /// family an admin has banned. Set by the host from <c>prefab_bans.json</c>; null bans
        /// nothing. A second reason for the same exclusion as <see cref="PrefabOverCeiling"/>, kept
        /// apart so a refusal can say which.</summary>
        public Func<string, bool> PrefabBanned { get; set; }

        /// <summary>The summed DPS of the working weapons on a node's station grid, read from the
        /// grid itself, or null when the node has no station grid in the world. The host points it at
        /// its materialised stations; null (the offline sim, tests that do not set it) reads no station.
        /// A fight the grids decide counts a station with a reading above zero as a defence still
        /// standing, whatever its safe zone.</summary>
        public Func<string, double?> StationWeaponReading { get; set; }

        /// <summary><see cref="StationWeaponReading"/> for <paramref name="nodeId"/>, or null.</summary>
        public double? StationWeaponDps(string nodeId)
        {
            return StationWeaponReading == null || nodeId == null ? null : StationWeaponReading(nodeId);
        }

        public bool IsPrefabOverCeiling(string prefabId)
        {
            return PrefabOverCeiling != null && !string.IsNullOrEmpty(prefabId) && PrefabOverCeiling(prefabId);
        }

        public bool IsPrefabBanned(string prefabId)
        {
            return PrefabBanned != null && !string.IsNullOrEmpty(prefabId) && PrefabBanned(prefabId);
        }

        /// <summary>Why this server will not build <paramref name="prefabId"/>: <see cref="ExclusionBanned"/>,
        /// <see cref="ExclusionOverCeiling"/>, or null when it may. A ban is named first: it is the
        /// admin's explicit word, and it holds whatever the ceiling later says.</summary>
        public string PrefabExclusion(string prefabId)
        {
            if (IsPrefabBanned(prefabId)) return ExclusionBanned;
            if (IsPrefabOverCeiling(prefabId)) return ExclusionOverCeiling;
            return null;
        }

        public const string ExclusionBanned = "banned";
        public const string ExclusionOverCeiling = "over_ceiling";

        private IItemMassCatalogue _itemMasses;
        /// <summary>Per-unit item masses. Null (the default) means the offline sim and any session
        /// that has not wired one: a leg carrying cargo is then priced on dry mass alone and
        /// <see cref="FuelLedger"/> counts it as such. The SE plugin wires
        /// <c>SeItemMassCatalogue</c> beside the manager it already hands the same instance to, so
        /// the fuel meter and the cargo trim read one set of masses.</summary>
        public IItemMassCatalogue ItemMasses
        {
            get { return _itemMasses; }
            set { _itemMasses = value; }
        }

        /// <summary>Nutrition per unit of each edible item, read from SE's consumable definitions by the
        /// host. Null in the offline sim and the tests that do not set one: workers then find nothing
        /// to eat. <see cref="WorkerUpkeep"/>; FAC-493.</summary>
        public INutritionCatalogue Nutrition { get; set; }

        private IItemVolumeCatalogue _itemVolumes;
        /// <summary>Per-unit item volumes. Null (the default) means the offline sim and any session
        /// that has not wired one, and every consumer here falls back to counting units. The SE
        /// plugin wires the same instance it already hands <c>ProductionManager</c> and
        /// <c>OperationsManager</c>, so the volume that weights a
        /// <see cref="SpatialNode.DemandHistory"/> credit is the one <see cref="PriceOracle"/>
        /// divides back out when it reads that credit as a rate.</summary>
        public IItemVolumeCatalogue ItemVolumes
        {
            get { return _itemVolumes; }
            set { _itemVolumes = value; }
        }

        private IFleetSpeedSource _fleetSpeeds;
        /// <summary>What speed each fleet flies, when something models that. Null (the default)
        /// prices every leg at <see cref="FactionTuning.ThrusterCruiseSpeedMs"/> and
        /// <see cref="FactionTuning.FleetSpeedKmPerMin"/>, which is the behaviour before this
        /// existed. See <see cref="IFleetSpeedSource"/>.</summary>
        public IFleetSpeedSource FleetSpeeds
        {
            get { return _fleetSpeeds; }
            set { _fleetSpeeds = value; }
        }

        /// <summary>What every dispatched leg would burn, totalled per faction and per action kind.
        /// In-memory, never persisted, and in measure mode a pure observer — see
        /// <see cref="Factions.Core.FuelLedger"/> and docs/fuel-plan.md.</summary>
        public FuelLedger FuelLedger { get; private set; }

        IItemRegistry IWorldView.ItemRegistry { get { return Registry; } }
        IMemberStandingSource IWorldView.MemberStanding { get { return MemberStanding; } }
        IItemCatalogue IWorldView.Catalogue { get { return Catalogue; } }

        private IRecipeRegistry _recipeRegistry;
        /// <summary>Production recipe registry. Defaults to <see cref="NullRecipeRegistry"/> the first
        /// time it is read. The SE plugin overrides this with <c>SeRecipeRegistry</c> before the first tick.</summary>
        public IRecipeRegistry Recipes
        {
            get { return _recipeRegistry ?? (_recipeRegistry = NullRecipeRegistry.Instance); }
            set { _recipeRegistry = value; }
        }

        IRecipeRegistry IWorldView.RecipeRegistry { get { return Recipes; } }

        // Clocked from this world so Post can stamp FirstPostedUtc itself -- the same "stamps
        // UtcNow so every caller doesn't have to" shape RecordEvent already uses. An unstamped
        // Item: demand was the reason demand age existed only for Ship: demands.
        private readonly DemandRegister _demand;
        /// <summary>Global demand register. Managers post demands here when blocked; other managers
        /// read and consume them. Not accessed by the brain or economy layers.</summary>
        public DemandRegister Demand { get { return _demand; } }
        DemandRegister IWorldView.DemandRegister { get { return _demand; } }

        private readonly InvestmentRegister _investments = new InvestmentRegister();
        /// <summary>Strategic investment request board. Managers post capability gaps here;
        /// the brain reads it during Think to generate high-priority Develop candidates.</summary>
        public InvestmentRegister Investments { get { return _investments; } }
        InvestmentRegister IWorldView.InvestmentRegister { get { return _investments; } }

        public double ExpandStep = 35;   // km a new outpost lands from its parent

        /// <summary>Whether each faction picks its duel tactic from its <see cref="TacticMemory"/>
        /// and learns from every duel's outcome, rather than fighting its personality default.
        /// Runtime only, never persisted: the host sets it on; the sim's tactics scenario compares it off and on.</summary>
        public bool TacticLearning;

        /// <summary>Units of a fuel key held at a seller node for the fuel-key procurements bound to
        /// collect there, keyed (node, key). Derived, never persisted: <see cref="Factions.Core.FuelMarket.RefreshHolds"/>
        /// rebuilds it from the roster's in-flight fleets, which are persisted, so a restart restores
        /// every hold at the first rebuild.</summary>
        private readonly Dictionary<(string NodeId, string ItemKey), long> _procurementHolds =
            new Dictionary<(string NodeId, string ItemKey), long>();

        /// <summary>Units of <paramref name="itemKey"/> held at <paramref name="nodeId"/> for
        /// fuel-key procurements still to collect there.</summary>
        public long ProcurementHeld(string nodeId, string itemKey)
        {
            if (nodeId == null || itemKey == null || _procurementHolds.Count == 0) return 0;
            long held;
            return _procurementHolds.TryGetValue((nodeId, itemKey), out held) ? held : 0;
        }

        internal void ClearProcurementHolds()
        {
            if (_procurementHolds.Count > 0) _procurementHolds.Clear();
        }

        internal void AddProcurementHold(string nodeId, string itemKey, long quantity)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(itemKey) || quantity <= 0) return;
            var key = (nodeId, itemKey);
            long prior;
            _procurementHolds.TryGetValue(key, out prior);
            _procurementHolds[key] = quantity > long.MaxValue - prior ? long.MaxValue : prior + quantity;
        }

        internal void ReleaseProcurementHold(string nodeId, string itemKey, long quantity)
        {
            if (nodeId == null || itemKey == null || quantity <= 0) return;
            var key = (nodeId, itemKey);
            long prior;
            if (!_procurementHolds.TryGetValue(key, out prior)) return;
            if (prior > quantity) _procurementHolds[key] = prior - quantity;
            else _procurementHolds.Remove(key);
        }

        /// <summary>Units of a fuel key that in-flight Haul convoys are still bound to collect at a node,
        /// keyed (node, key). Derived, never persisted, rebuilt beside <see cref="ProcurementHeld"/> by
        /// <see cref="Factions.Core.FuelMarket.RefreshHolds"/>.
        /// It ranks below a procurement hold: see <see cref="Factions.Core.FuelMarket"/>.</summary>
        private readonly Dictionary<(string NodeId, string ItemKey), long> _haulPickupClaims =
            new Dictionary<(string NodeId, string ItemKey), long>();

        /// <summary>Units of <paramref name="itemKey"/> that in-flight Haul convoys are still bound to
        /// collect at <paramref name="nodeId"/>, counted only for fuel keys.</summary>
        public long HaulPickupClaimed(string nodeId, string itemKey)
        {
            if (nodeId == null || itemKey == null || _haulPickupClaims.Count == 0) return 0;
            long claimed;
            return _haulPickupClaims.TryGetValue((nodeId, itemKey), out claimed) ? claimed : 0;
        }

        internal void ClearHaulPickupClaims()
        {
            if (_haulPickupClaims.Count > 0) _haulPickupClaims.Clear();
        }

        internal void AddHaulPickupClaim(string nodeId, string itemKey, long quantity)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(itemKey) || quantity <= 0) return;
            var key = (nodeId, itemKey);
            long prior;
            _haulPickupClaims.TryGetValue(key, out prior);
            _haulPickupClaims[key] = quantity > long.MaxValue - prior ? long.MaxValue : prior + quantity;
        }

        internal void ReleaseHaulPickupClaim(string nodeId, string itemKey, long quantity)
        {
            if (nodeId == null || itemKey == null || quantity <= 0) return;
            var key = (nodeId, itemKey);
            long prior;
            if (!_haulPickupClaims.TryGetValue(key, out prior)) return;
            if (prior > quantity) _haulPickupClaims[key] = prior - quantity;
            else _haulPickupClaims.Remove(key);
        }

        /// <summary>The fuel keys each faction's reserve pass last posted for, by faction tag
        /// (<see cref="FuelReserve.FactionFuelKeys"/>). Written by that pass once per production
        /// cycle and read by <see cref="FuelReserve.Keep"/>, so the surplus rule keeps back exactly
        /// the keys the reserve asks for. Runtime only: empty after a boot until the first
        /// production cycle.</summary>
        private readonly Dictionary<string, HashSet<string>> _fuelReserveKeys =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records the fuel keys <paramref name="factionTag"/>'s reserve pass posts for;
        /// null or empty clears them.</summary>
        public void SetFuelReserveKeys(string factionTag, IList<string> keys)
        {
            if (string.IsNullOrEmpty(factionTag)) return;
            HashSet<string> set;
            if (keys == null || keys.Count == 0)
            {
                if (_fuelReserveKeys.TryGetValue(factionTag, out set)) set.Clear();
                return;
            }
            if (!_fuelReserveKeys.TryGetValue(factionTag, out set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _fuelReserveKeys[factionTag] = set;
            }
            set.Clear();
            for (int i = 0; i < keys.Count; i++)
                if (!string.IsNullOrEmpty(keys[i])) set.Add(keys[i]);
        }

        /// <summary>Whether <paramref name="factionTag"/>'s reserve pass posts for
        /// <paramref name="key"/>.</summary>
        public bool IsFuelReserveKey(string factionTag, string key)
        {
            if (string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(key)) return false;
            HashSet<string> set;
            return _fuelReserveKeys.TryGetValue(factionTag, out set) && set.Contains(key);
        }

        /// <summary>Units each faction's Stock standing orders keep back, by faction tag, then
        /// (node id, item key). Written by <see cref="StockOrders.Publish"/> and read by
        /// <see cref="StockOrders.Keep"/>. Runtime only: empty after a boot until the first
        /// production cycle or the first edit of the list.</summary>
        private readonly Dictionary<string, Dictionary<(string NodeId, string ItemKey), long>> _stockOrderKeeps =
            new Dictionary<string, Dictionary<(string NodeId, string ItemKey), long>>(StringComparer.Ordinal);

        /// <summary>Replaces <paramref name="factionTag"/>'s Stock-order keeps; null or empty clears
        /// them.</summary>
        public void SetStockOrderKeeps(string factionTag, Dictionary<(string NodeId, string ItemKey), long> keeps)
        {
            if (string.IsNullOrEmpty(factionTag)) return;
            if (keeps == null || keeps.Count == 0) _stockOrderKeeps.Remove(factionTag);
            else _stockOrderKeeps[factionTag] = keeps;
        }

        /// <summary>Units of <paramref name="itemKey"/> <paramref name="factionTag"/>'s Stock orders
        /// keep at <paramref name="nodeId"/>; 0 when none do.</summary>
        public long StockOrderKeep(string factionTag, string nodeId, string itemKey)
        {
            if (_stockOrderKeeps.Count == 0 || factionTag == null || nodeId == null || itemKey == null) return 0;
            Dictionary<(string NodeId, string ItemKey), long> keeps;
            long keep;
            return _stockOrderKeeps.TryGetValue(factionTag, out keeps)
                && keeps.TryGetValue((nodeId, itemKey), out keep) ? keep : 0;
        }

        /// <summary>Records <paramref name="factionTag"/>'s Stock-order claims as the last production
        /// pass built them (<see cref="StockClaimFloor"/>), for the shipyard's component absorption.
        /// Null or empty clears them.</summary>
        public void SetStockClaims(string factionTag, StockClaimFloor claims)
        {
            if (string.IsNullOrEmpty(factionTag)) return;
            if (claims == null || claims.IsEmpty) _stockClaims.Remove(factionTag);
            else _stockClaims[factionTag] = claims;
        }

        /// <summary><paramref name="factionTag"/>'s Stock-order claims; null when it has none.</summary>
        public StockClaimFloor StockClaims(string factionTag)
        {
            StockClaimFloor claims;
            return factionTag != null && _stockClaims.TryGetValue(factionTag, out claims) ? claims : null;
        }

        private readonly Dictionary<string, StockClaimFloor> _stockClaims =
            new Dictionary<string, StockClaimFloor>(StringComparer.Ordinal);

        private IPlanetCatalogue _planets;
        private IOreGeography _ores;
        private WorldFeaturePlacement _features;
        /// <summary>Planet catalogue used for zone classification and surface snapping in
        /// <see cref="ExpandFrom"/>. Defaults to <see cref="NullPlanetCatalogue"/> (no planets —
        /// all nodes are DeepSpace). The SE host wires a real <c>SePlanetCatalogue</c> before
        /// the first tick; the offline sim leaves this null.</summary>
        public IPlanetCatalogue Planets
        {
            get { return _planets ?? NullPlanetCatalogue.Instance; }
            set { _planets = value; }
        }

        /// <summary>What minerals are actually in the ground, per position. The SE host wires a real
        /// <c>SeOreGeography</c>; the offline sim leaves this null and gets a world with no ore
        /// anywhere. See docs/ore-geography.md.</summary>
        public IOreGeography Ores
        {
            get { return _ores ?? NullOreGeography.Instance; }
            set { _ores = value; }
        }

        /// <summary>Where the authored world says a node belongs, for the regions it authors. Defaults
        /// to <see cref="WorldFeaturePlacement.Empty"/> — an unauthored world, where every planet rule
        /// applies exactly as it did before features existed. See <c>PlaceNode</c>.</summary>
        public WorldFeaturePlacement Features
        {
            get { return _features ?? WorldFeaturePlacement.Empty; }
            set { _features = value; }
        }

        /// <summary>Which speed tier each stretch of a leg falls in, cached per node pair. Built
        /// from <see cref="Planets"/> and <see cref="Features"/>, so it is world geometry and not a
        /// speed — a retune does not invalidate it. See <see cref="LegZoneProfile"/>.</summary>
        public LegZoneCache ZoneProfiles { get; private set; }

        /// <summary>The tier run table for a straight leg between two points.</summary>
        public LegZoneProfile ZonesFor(Vec3 fromKm, Vec3 toKm)
        {
            _zoneLegScratch[0] = fromKm;
            _zoneLegScratch[1] = toKm;
            return ZonesFor(_zoneLegScratch);
        }

        /// <summary>The tier run table for an already-planned polyline.</summary>
        public LegZoneProfile ZonesFor(IList<Vec3> polylineKm)
        {
            bool hit;
            return ZoneProfiles.Get(Planets, Features, Tuning, polylineKm, out hit);
        }

        // Two-point buffer for the straight-leg overload: the cache keys on the endpoints and never
        // retains the list, and the call sites are all on the single-threaded sim tick.
        private readonly Vec3[] _zoneLegScratch = new Vec3[2];

        private readonly Dictionary<string, long> _treasury = new Dictionary<string, long>();
        // Tags whose treasury moved since the last FlushTreasuryMirror. Every write to _treasury goes
        // through SetTreasury/AddTreasury, so recording here is what makes the mirror exhaustive
        // rather than limited to the Earn/Spend pair.
        private readonly HashSet<string> _treasuryMirrorDirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per-node item inventories live on SpatialNode.Inventory (Phase 2).
        // Net flow is cached here each AccrueIncome tick so brain Sense reads a stable value.
        private Dictionary<string, long> _cachedNetFlow = new Dictionary<string, long>();
        // Ledger (treasury + node inventory at market price) cached each AccrueIncome tick.
        private Dictionary<string, long> _cachedLedger = new Dictionary<string, long>();
        // Total combat capacity (summed WeaponBands Dps across owned ShipStock) cached each
        // AccrueIncome tick — a per-faction node walk is too expensive to redo on every
        // FactionBrain.Sense() call (which needs it for every bordering rival, not just self).
        private Dictionary<string, long> _cachedFleetPower = new Dictionary<string, long>();
        // Outstanding borrowed credits per faction; persisted via BrainDoc.Debt.
        private readonly Dictionary<string, long> _debt = new Dictionary<string, long>();
        // Real income (trade settlement, raid loot) earned between AccrueIncome passes — folded
        // into that pass's income sample, then cleared. Not persisted; a restart just drops
        // whatever accrued since the last pass, which self-corrects within one more pass.
        private readonly Dictionary<string, long> _pendingRealIncomeByTag = new Dictionary<string, long>();
        // Action/dispatch costs (brain-chosen actions, trade dispatch, reinforce/patrol) applied between
        // AccrueIncome passes — folded into that pass's netFlow snapshot, then cleared. Not persisted;
        // same self-correcting-within-one-pass behavior as _pendingRealIncomeByTag.
        private readonly Dictionary<string, long> _pendingBurnByTag = new Dictionary<string, long>();
        // Real deliveries credited between OperationsManager.Tick passes (Haul arrivals, bootstrap
        // instant transfers) -- folded into each destination SpatialNode's DemandHistory EMA and
        // cleared. Not persisted; keyed NodeId -> ItemKey -> weighted quantity this pass.
        // docs/material-vectors.md's DemandHistory addendum.
        private readonly Dictionary<string, Dictionary<string, double>> _pendingDemandHistoryCredit
            = new Dictionary<string, Dictionary<string, double>>();
        private readonly Dictionary<string, DateTime> _centralBankSupportCooldownUntil = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _centralBankGrantCooldownUntil = new Dictionary<string, DateTime>();
        private readonly List<MonetaryEvent> _centralBankEvents = new List<MonetaryEvent>();
        private readonly List<MonetaryEvent> _debtEvents = new List<MonetaryEvent>();
        private readonly List<RaidIncident> _incidents = new List<RaidIncident>();
        private readonly List<NodeSeparationViolation> _separationViolations = new List<NodeSeparationViolation>();
        private readonly List<AbandonedNode> _abandonedNodes = new List<AbandonedNode>();
        private readonly List<ShipEvacuation> _shipEvacuations = new List<ShipEvacuation>();
        private readonly List<FactionEvent> _pendingEvents = new List<FactionEvent>();
        private readonly InfluenceField _field;
        private readonly IRandom _rng;
        private IPriceOracle _priceOracle;
        private const int MonetaryEventHistoryCap = 120;

        /// <summary>Backstop only. The host drains <see cref="PendingEvents"/> every tick; this
        /// bounds the damage if some caller never does, rather than letting a list grow for a
        /// session. Far above one tick's worth of events.</summary>
        private const int PendingEventCap = 512;

        public WorldModel(IRandom rng)
        {
            _rng = rng;
            _field = new InfluenceField(Graph, () => Tuning);
            Influence = _field;
            _demand = new DemandRegister(() => UtcNow);
            FuelLedger = new FuelLedger(DateTime.UtcNow);
            ZoneProfiles = new LegZoneCache();
        }
        public WorldModel() : this(new SeededRandom(1)) { }

        public IPriceOracle PriceOracle
        {
            get { return _priceOracle ?? (_priceOracle = new PriceOracle(this, Tuning)); }
            set { _priceOracle = value; }
        }

        /// <summary>Pre-bake the influence snapshot for the current tick. Must be called once before
        /// AccrueIncome (economy phase) and once before the brain loop (StepOnce), since DecayInfluence
        /// between those phases changes HoldStrength and invalidates the earlier snapshot.</summary>
        public void PrepareInfluenceSnapshot() { _field.BuildSnapshot(); }

        // ---- Resumable counterpart, for the live host's frame-budget-admitted global pass ----------
        // See InfluenceField's own resumable-rebuild comments for the full rationale. These four
        // passthroughs are the only surface the host needs; PrepareInfluenceSnapshot() above is
        // untouched and remains the atomic entry point for Step()/Sim/tests.
        public void BeginInfluenceSnapshotBuild() { _field.BeginSnapshotBuild(); }
        public bool AdvanceInfluenceSnapshotBuild() { return _field.AdvanceSnapshotBuild(); }
        public bool IsInfluenceSnapshotBuildStale() { return _field.IsSnapshotBuildStale(); }
        public void CommitInfluenceSnapshotBuild() { _field.CommitSnapshotBuild(); }
        // Diagnostics only (live troubleshooting, 2026-08-17).
        public int DiagnosticSnapshotBuildIndex { get { return _field.DiagnosticBuildIndex; } }
        public int DiagnosticSnapshotBuildTotal { get { return _field.DiagnosticBuildTotal; } }
        public void AbandonInfluenceSnapshotBuild() { _field.AbandonSnapshotBuild(); }

        public long GetTreasury(string factionTag) { long v; return _treasury.TryGetValue(factionTag, out v) ? v : 0; }

        /// <summary>Cached net flow: real income (<see cref="AddPendingIncome"/>, central-bank
        /// grants/asset purchases) minus station upkeep, debt interest, and action/dispatch costs
        /// (<see cref="AddPendingExpense"/>) accrued since the last pass. Populated by
        /// <see cref="AccrueIncome"/>. Returns 0 before the first accrual tick.</summary>
        public long GetNetFlow(string factionTag)
        {
            long v;
            return _cachedNetFlow.TryGetValue(factionTag, out v) ? v : 0;
        }

        public void SetTreasury(string tag, long amount) { _treasury[tag] = amount; MarkTreasuryMirrorDirty(tag); }

        /// <summary>Records real earned income (trade settlement, raid loot) outside the
        /// AccrueIncome pass that credited it. Folded into FactionState.CentralBankRecentIncome's
        /// EMA on the next AccrueIncome call, then cleared.</summary>
        public void AddPendingIncome(string factionTag, long amount)
        {
            if (string.IsNullOrEmpty(factionTag) || amount <= 0) return;
            long prior;
            _pendingRealIncomeByTag.TryGetValue(factionTag, out prior);
            _pendingRealIncomeByTag[factionTag] = prior + amount;
        }

        /// <summary>Records a real spend (brain action cost, trade dispatch fee, reinforce/patrol cost)
        /// outside the AccrueIncome pass that debited it. Folded into that pass's GetNetFlow snapshot,
        /// then cleared.</summary>
        public void AddPendingExpense(string factionTag, long amount)
        {
            if (string.IsNullOrEmpty(factionTag) || amount <= 0) return;
            long prior;
            _pendingBurnByTag.TryGetValue(factionTag, out prior);
            _pendingBurnByTag[factionTag] = prior + amount;
        }
        public void AddTreasury(string tag, long delta) { _treasury[tag] = GetTreasury(tag) + delta; MarkTreasuryMirrorDirty(tag); }

        /// <summary>Atomic debit-and-track: the single-call replacement for
        /// <c>AddTreasury(tag, -amount) + AddPendingExpense(tag, amount)</c>. Use for any real spend
        /// (brain action cost, trade dispatch fee, reinforce/patrol cost) outside the AccrueIncome pass
        /// so it's guaranteed to show up in the next GetNetFlow snapshot.</summary>
        public void Spend(string tag, long amount)
        {
            AddTreasury(tag, -amount);
            AddPendingExpense(tag, amount);
        }

        /// <summary>Atomic credit-and-track: the single-call replacement for
        /// <c>AddTreasury(tag, amount) + AddPendingIncome(tag, amount)</c>. Use for any real earned
        /// income (trade settlement, raid loot) outside the AccrueIncome pass so it's guaranteed to be
        /// folded into FactionState.CentralBankRecentIncome's EMA on the next AccrueIncome call.</summary>
        public void Earn(string tag, long amount)
        {
            AddTreasury(tag, amount);
            AddPendingIncome(tag, amount);
        }

        /// <summary>A member's withdrawal from the faction wallet: a transfer to someone who belongs to
        /// the faction, not a spend. Debits the treasury without a pending expense, so it never reads
        /// as burn and never lifts the runway term of a central-bank treasury target. Where the money
        /// went is what <see cref="MemberWealth"/> reports.</summary>
        public void BookMemberWithdrawal(string tag, long amount)
        {
            if (string.IsNullOrEmpty(tag) || amount <= 0) return;
            AddTreasury(tag, -amount);
        }

        private IMemberWealth _memberWealth;
        /// <summary>Members' personal balances, read for player-faction grants. Defaults to
        /// <see cref="NullMemberWealth"/>; the SE plugin wires a live reader.</summary>
        public IMemberWealth MemberWealth
        {
            get { return _memberWealth ?? (_memberWealth = NullMemberWealth.Instance); }
            set { _memberWealth = value; }
        }

        private ITreasuryMirror _treasuryMirror;
        /// <summary>Host-visible wallet mirror. Defaults to <see cref="NullTreasuryMirror"/> (offline
        /// sim, dry-run) the first time it is read; the SE plugin wires a live adapter before the
        /// first tick, mirroring <see cref="Catalogue"/>/<see cref="Registry"/>. Authority is not
        /// moved by wiring this -- <see cref="GetTreasury"/> keeps reading the ledger below.</summary>
        public ITreasuryMirror TreasuryMirror
        {
            get { return _treasuryMirror ?? (_treasuryMirror = NullTreasuryMirror.Instance); }
            set { _treasuryMirror = value; }
        }

        private void MarkTreasuryMirrorDirty(string tag)
        {
            if (!string.IsNullOrEmpty(tag)) _treasuryMirrorDirty.Add(tag);
        }

        /// <summary>Number of tags whose treasury has moved and not yet been mirrored. Diagnostics
        /// and tests; the host does not read it to decide whether to flush.</summary>
        public int PendingTreasuryMirrorCount { get { return _treasuryMirrorDirty.Count; } }

        /// <summary>Push every moved treasury to <see cref="TreasuryMirror"/> and return how many
        /// landed. Deferred to an explicit flush rather than pushed from
        /// <see cref="AddTreasury"/> so a global pass that moves one faction's treasury a dozen times
        /// costs the host one wallet write, not a dozen; the value pushed is absolute, so collapsing
        /// them loses nothing. A tag the mirror could not deliver stays dirty and is retried on the
        /// next flush. Call on the host's game thread only -- SE banking is not thread-safe.</summary>
        public int FlushTreasuryMirror()
        {
            if (_treasuryMirrorDirty.Count == 0) return 0;
            ITreasuryMirror mirror = TreasuryMirror;
            List<string> failed = null;
            int pushed = 0;
            foreach (string tag in _treasuryMirrorDirty)
            {
                if (mirror.MirrorTreasury(tag, GetTreasury(tag))) pushed++;
                else (failed ?? (failed = new List<string>())).Add(tag);
            }
            _treasuryMirrorDirty.Clear();
            if (failed != null)
                foreach (string tag in failed) _treasuryMirrorDirty.Add(tag);
            return pushed;
        }

        /// <summary>Records a real delivery's item-volume-weighted quantity against its destination
        /// node, to be folded into <see cref="SpatialNode.DemandHistory"/>'s EMA on the next
        /// <see cref="OperationsManager"/>.Tick call for that node's owning faction. Called only from
        /// realizers of an already-matched <see cref="MaterialVector"/> (a live <c>Item:</c> demand) --
        /// never posted directly -- so <c>DemandHistory</c> stays want-tied by construction.</summary>
        public void AddPendingDemandHistoryCredit(string nodeId, string itemKey, double weightedQty)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(itemKey) || weightedQty <= 0) return;
            Dictionary<string, double> byItem;
            if (!_pendingDemandHistoryCredit.TryGetValue(nodeId, out byItem))
            {
                byItem = new Dictionary<string, double>();
                _pendingDemandHistoryCredit[nodeId] = byItem;
            }
            double prior;
            byItem.TryGetValue(itemKey, out prior);
            byItem[itemKey] = prior + weightedQty;
        }

        /// <summary>Returns a detached snapshot of this node's pending DemandHistory credits without
        /// consuming them. Resumable Operations cycles use this as an exact guarded read; callers may
        /// freely mutate the returned dictionary.</summary>
        public Dictionary<string, double> SnapshotPendingDemandHistory(string nodeId)
        {
            Dictionary<string, double> byItem;
            if (!_pendingDemandHistoryCredit.TryGetValue(nodeId, out byItem)) return null;
            return new Dictionary<string, double>(byItem, StringComparer.Ordinal);
        }

        /// <summary>Consumes pending DemandHistory credits only when they still exactly match a prior
        /// detached snapshot. This is the commit-time compare-and-consume seam for an atomic staged
        /// Operations cycle; credits arriving while the cycle is in flight are never lost.</summary>
        public bool TryConsumePendingDemandHistory(string nodeId, IReadOnlyDictionary<string, double> expected)
        {
            Dictionary<string, double> current;
            bool hasCurrent = _pendingDemandHistoryCredit.TryGetValue(nodeId, out current);
            if (expected == null)
                return !hasCurrent;
            if (!hasCurrent || current.Count != expected.Count) return false;
            foreach (KeyValuePair<string, double> pair in expected)
            {
                double value;
                if (!current.TryGetValue(pair.Key, out value) || value != pair.Value) return false;
            }
            _pendingDemandHistoryCredit.Remove(nodeId);
            return true;
        }

        /// <summary>Removes and returns this pass's pending DemandHistory credit for a node (null if
        /// none), for <see cref="OperationsManager"/> to fold into <see cref="SpatialNode.DemandHistory"/>'s
        /// EMA. Drain-once semantics mirror <see cref="AccrueIncome"/>'s own pending-income clearing.</summary>
        public Dictionary<string, double> DrainPendingDemandHistory(string nodeId)
        {
            Dictionary<string, double> byItem;
            if (_pendingDemandHistoryCredit.TryGetValue(nodeId, out byItem))
            {
                _pendingDemandHistoryCredit.Remove(nodeId);
                return byItem;
            }
            return null;
        }

        /// <summary>Cached ledger (treasury + node inventory at market price). Falls back to treasury before first AccrueIncome tick.</summary>
        public long GetLedger(string factionTag) { long v; return _cachedLedger.TryGetValue(factionTag, out v) ? v : GetTreasury(factionTag); }
        long IWorldView.GetLedger(string factionTag) { return GetLedger(factionTag); }

        public long GetDebt(string factionTag) { long v; return _debt.TryGetValue(factionTag, out v) ? v : 0; }
        long IWorldView.GetDebt(string factionTag) { return GetDebt(factionTag); }

        /// <summary>Cached total combat capacity (see <see cref="_cachedFleetPower"/>). Returns 0
        /// before the first <see cref="AccrueIncome"/> pass or for a faction with no combat-capable
        /// ShipStock/PrefabCatalogue data.</summary>
        public long GetFleetCombatPower(string factionTag)
        {
            long v;
            return _cachedFleetPower.TryGetValue(factionTag, out v) ? v : 0;
        }
        long IWorldView.GetFleetCombatPower(string factionTag) { return GetFleetCombatPower(factionTag); }

        /// <summary><paramref name="targetNodeId"/> null (the default) preserves the original
        /// role-only check. When supplied and resolvable, this checks — per candidate origin node —
        /// whether that node's stock could actually survive the trip to the target, not just whether
        /// it's role-suitable; a network whose only stock is (say) Ion-only and the target is a planet
        /// surface correctly reads as "no stock" here, same shape as
        /// <c>OffensiveDispatch.FindBestOriginNode</c>'s route-aware origin pick.</summary>
        public bool HasShipStockFor(string factionTag, ActionKind kind, string targetNodeId = null)
        {
            SpatialNode targetNode;
            if (targetNodeId == null || !Graph.Nodes.TryGetValue(targetNodeId, out targetNode))
                return ShipPool.NetworkHasSuitable(Graph.OwnedBy(factionTag), kind, _prefabCatalogue);

            foreach (SpatialNode origin in Graph.OwnedBy(factionTag))
            {
                ShipPool.RouteEnvironment route = ShipPool.RouteEnvironment.From(
                    Planets, origin.Position, targetNode.Position, Tuning.MinDryTwr);
                if (ShipPool.HasSuitable(origin, kind, _prefabCatalogue, route)) return true;
            }
            return false;
        }

        public IEnumerable<SpatialNode> SuitableShipOrigins(string factionTag, ActionKind kind)
        {
            return Graph.OwnedBy(factionTag).Where(n => ShipPool.HasSuitable(n, kind, _prefabCatalogue));
        }
        bool IWorldView.HasShipStockFor(string factionTag, ActionKind kind, string targetNodeId)
            { return HasShipStockFor(factionTag, kind, targetNodeId); }

        /// <summary>Sums WeaponBands Dps × stock count across every node <paramref name="factionTag"/>
        /// owns, using <see cref="SpatialNode.ShipStock"/> directly now that it's prefab-keyed (each
        /// entry already carries both prefab identity and count — no separate queue to walk).
        /// Returns 0 if <see cref="PrefabCatalogue"/> is unwired.</summary>
        private long ComputeFleetCombatPower(string factionTag)
        {
            if (_prefabCatalogue == null) return 0;
            long total = 0;
            foreach (SpatialNode n in OwnedNodesForPass(factionTag))
            {
                foreach (var kv in n.ShipStock)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    IReadOnlyList<WeaponBand> bands = _prefabCatalogue.WeaponBands(kv.Key);
                    long perShip = 0;
                    for (int i = 0; i < bands.Count; i++) perShip += (long)bands[i].Dps;
                    total += perShip * kv.Value;
                }
            }
            return total;
        }
        public void SetDebt(string factionTag, long amount) { if (amount > 0) _debt[factionTag] = amount; else _debt.Remove(factionTag); }

        /// <summary>Zeroes both debt counters for a faction — the master ledger (principal + accrued
        /// interest) and, when <paramref name="state"/> is supplied, the support-loan-only principal
        /// counter (<see cref="FactionState.CentralBankSupportDebt"/>) used to gate new-loan eligibility.
        /// Both must clear together or the eligibility gate keeps treating the faction as still indebted
        /// even though the visible master figure reads zero. Records a "forgiveness" debt event for audit.</summary>
        public void ClearDebt(string factionTag, FactionState state)
        {
            long debtBefore = GetDebt(factionTag);
            SetDebt(factionTag, 0);
            if (state != null) state.CentralBankSupportDebt = 0;
            if (debtBefore > 0)
                AppendDebtEvent(factionTag, "forgiveness", -debtBefore, GetTreasury(factionTag), 0, "debt forgiven (operator reset)");
        }

        /// <summary>Total liquid money currently in circulation across all factions' treasuries.
        /// Debt is not added separately here; borrowed money is already reflected in treasury balances.</summary>
        public long GetCurrentMoneySupply()
        {
            // Sum of the treasury ledger itself. Identical to summing GetTreasury over every monetary
            // actor (treasury keys, debt keys, node owners) -- an actor with no treasury entry
            // contributes 0 -- but without the O(nodes) owner walk that identity used to pay for on
            // every call, and AppendMonetaryEvent calls it twice per emitted event.
            long total = 0;
            foreach (long treasury in _treasury.Values)
                total += treasury;
            return total;
        }

        /// <summary>World collateral base used by the central-bank target. Tied to capped, destructible
        /// productive assets rather than infinitely harvestable raw resources.
        /// <para><paramref name="states"/> supplies the in-flight half of the fleet. A dispatched hull
        /// leaves <see cref="SpatialNode.ShipStock"/> for the whole of its journey, so a node-only walk
        /// writes the currency's backing down for the duration of every convoy and writes it back on
        /// arrival — a ship in transit is no less owned, and no less destructible, than one parked.
        /// Omitting the roster reproduces the node-only figure.</para></summary>
        public long GetWorldCollateralValue(IList<FactionState> states = null)
        {
            long total = 0;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (string.IsNullOrEmpty(n.OwnerFactionTag)) continue;
                total += NodeCollateralValue(n);
            }

            if (states != null)
                foreach (FactionState st in states)
                    total += InFlightShipCollateral(st);

            return total;
        }

        /// <summary>True when this faction owns at least one node. Separate from
        /// <see cref="GetFactionCollateralValue"/> on purpose: that answers "what are its assets
        /// worth", which is a different question that today happens to return 0 for the same
        /// input.</summary>
        public bool OwnsAnyNode(string factionTag)
        {
            if (string.IsNullOrEmpty(factionTag)) return false;
            foreach (SpatialNode n in OwnedNodesForPass(factionTag)) return true;
            return false;
        }

        /// <summary>Collateral attributable to one faction's owned assets. <paramref name="state"/> is
        /// that faction's own roster entry and adds its in-flight hulls, for the reason
        /// <see cref="GetWorldCollateralValue"/> gives.</summary>
        public long GetFactionCollateralValue(string factionTag, FactionState state = null)
        {
            if (string.IsNullOrEmpty(factionTag)) return 0;
            long total = 0;
            foreach (SpatialNode n in OwnedNodesForPass(factionTag))
                total += NodeCollateralValue(n);
            return total + InFlightShipCollateral(state);
        }

        /// <summary>What one faction's unresolved fleets are worth as collateral, each hull priced by
        /// <see cref="HullCollateralValue"/> exactly as it would be parked — it is the same hull. A
        /// mixed squadron prices each member by its own prefab. A resolved, intercepted or bench entry
        /// contributes nothing, for the reasons <see cref="CountInFlightHulls"/> gives.</summary>
        private long InFlightShipCollateral(FactionState state)
        {
            if (state == null) return 0;
            long total = 0;
            foreach (PendingFleetAction f in state.PendingFleets)
            {
                if (f.Resolved || f.Intercepted || f.IsBench) continue;
                if (f.Squadron != null && f.Squadron.Count > 0)
                    foreach (string member in f.Squadron) total += HullCollateralValue(member);
                else
                    total += f.HullCount * HullCollateralValue(f.ShipPrefabId);
            }
            return total;
        }

        /// <summary>Collateral one hull of <paramref name="prefabId"/> carries: its catalogue
        /// <see cref="IPrefabCatalogue.Cost"/>, the same component-price sum a store listing starts
        /// from. <see cref="FactionTuning.CentralBankShipValue"/> is the fallback for a prefab the
        /// catalogue does not price, and for a world with no catalogue (the offline sim).
        /// <para>Live 2026-10-09: 9,265 hulls across 104 prefabs, every one priced, median 11.1M,
        /// together 394.9B — against the 1.16B a flat 125,000 per hull had counted.</para></summary>
        private long HullCollateralValue(string prefabId)
        {
            long cost = _prefabCatalogue != null && !string.IsNullOrEmpty(prefabId) ? _prefabCatalogue.Cost(prefabId) : 0;
            return cost > 0 ? cost : Tuning.CentralBankShipValue;
        }

        /// <summary>Hulls aboard <paramref name="state"/>'s unresolved fleets. A resolved or
        /// intercepted fleet is excluded -- its ships are back in <see cref="SpatialNode.ShipStock"/>
        /// or destroyed -- and so is a bench entry, which carries no hull of its own.</summary>
        public static int CountInFlightHulls(FactionState state)
        {
            if (state == null) return 0;
            int hulls = 0;
            foreach (PendingFleetAction f in state.PendingFleets)
            {
                if (f.Resolved || f.Intercepted || f.IsBench) continue;
                hulls += f.HullCount;
            }
            return hulls;
        }

        /// <summary>Hulls parked in <paramref name="node"/>'s <see cref="SpatialNode.ShipStock"/>,
        /// store-listed ones included.</summary>
        public static int CountIdleHulls(SpatialNode node)
        {
            int hulls = 0;
            foreach (var ship in node.ShipStock) if (ship.Value > 0) hulls += ship.Value;
            return hulls;
        }

        /// <summary>Every faction's hulls, parked at the nodes it owns and flying on its own fleets,
        /// keyed by tag. The same two counts <see cref="SnapshotCentralBank"/> sums for the world, so
        /// the per-faction figures always add up to its total. A faction with neither is absent.</summary>
        public Dictionary<string, ShipTally> TallyShipsByFaction(IList<FactionState> states)
        {
            var tally = new Dictionary<string, ShipTally>();
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (string.IsNullOrEmpty(n.OwnerFactionTag)) continue;
                int idle = CountIdleHulls(n);
                if (idle == 0) continue;
                ShipTally t;
                tally.TryGetValue(n.OwnerFactionTag, out t);
                t.Idle += idle;
                tally[n.OwnerFactionTag] = t;
            }
            if (states != null)
                foreach (FactionState st in states)
                {
                    if (st == null || string.IsNullOrEmpty(st.Tag)) continue;
                    int flying = CountInFlightHulls(st);
                    if (flying == 0) continue;
                    ShipTally t;
                    tally.TryGetValue(st.Tag, out t);
                    t.InFlight += flying;
                    tally[st.Tag] = t;
                }
            return tally;
        }

        // World collateral as the money-supply target sees it, and when it was last committed. See
        // AnchoredCollateral. MinValue = never committed, so the first pass anchors to the world as found.
        private long _collateralAnchor;
        private DateTime _collateralAnchorUtc = DateTime.MinValue;

        // What the last completed income pass's monetary policy did, for the snapshot line.
        private string _lastPolicyMode = "";
        private long _lastPolicyLevied;
        private long _lastPolicyRescued;

        /// <summary>The committed anchor, for the host to persist. Restore it with
        /// <see cref="RestoreCollateralAnchor"/> before the first income pass, or a restart during a
        /// war's dip forgets the collateral the war took and the bank contracts against the dip.</summary>
        public long CollateralAnchor { get { return _collateralAnchor; } }
        public DateTime CollateralAnchorUtc { get { return _collateralAnchorUtc; } }

        public void RestoreCollateralAnchor(long anchor, DateTime anchorUtc)
        {
            if (anchor <= 0 || anchorUtc == DateTime.MinValue) return;
            _collateralAnchor = anchor;
            _collateralAnchorUtc = anchorUtc;
        }

        /// <summary>Collateral the target is set against, given the instantaneous figure: equal to it
        /// whenever collateral is at or above the anchor, otherwise the anchor's excess over it decayed
        /// by <see cref="FactionTuning.CentralBankAnchorHalfLife"/> since the last commit. A war that
        /// destroys hulls lowers the instantaneous figure at once; the target follows by half-lives, so
        /// a world that rebuilds in that time never sees the money it already holds as excess. Read-only:
        /// only the income pass's barrier commits (<see cref="CommitCollateralAnchor"/>).</summary>
        private long AnchoredCollateral(long instant)
        {
            if (_collateralAnchorUtc == DateTime.MinValue || _collateralAnchor <= instant) return instant;
            TimeSpan halfLife = Tuning.CentralBankAnchorHalfLife;
            if (halfLife <= TimeSpan.Zero) return instant;
            double elapsed = (UtcNow - _collateralAnchorUtc).TotalSeconds;
            if (elapsed < 0) elapsed = 0;
            double remaining = Math.Pow(0.5, elapsed / halfLife.TotalSeconds);
            return instant + (long)((_collateralAnchor - instant) * remaining);
        }

        private long CommitCollateralAnchor(long instant)
        {
            _collateralAnchor = AnchoredCollateral(instant);
            _collateralAnchorUtc = UtcNow;
            return _collateralAnchor;
        }

        private long TargetFromCollateral(long anchoredCollateral)
        {
            double ratio = Tuning.CentralBankReserveRatio;
            if (ratio <= 0) return 0;
            return (long)(anchoredCollateral * ratio);
        }

        public long GetTargetMoneySupply(IList<FactionState> states = null)
        {
            return TargetFromCollateral(AnchoredCollateral(GetWorldCollateralValue(states)));
        }

        /// <summary>World-level monetary and asset snapshot. <paramref name="states"/> is what makes
        /// <see cref="CentralBankSnapshot.ShipCount"/> a hull total rather than an idle-stock reading:
        /// ships are removed from <see cref="SpatialNode.ShipStock"/> for the whole of a fleet's
        /// journey, so without the roster the count dips on every dispatch. Omitting it leaves
        /// <see cref="CentralBankSnapshot.InFlightShipCount"/> at 0 and the total equal to the idle
        /// count — acceptable only for a caller that reads the money fields.</summary>
        public CentralBankSnapshot SnapshotCentralBank(IList<FactionState> states = null)
        {
            int territoryNodes = 0;
            int foundedNodes = 0;
            int capabilityLevels = 0;
            int idleShips = 0;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (string.IsNullOrEmpty(n.OwnerFactionTag)) continue;
                territoryNodes++;
                if (Economy.HasDevelopedInfrastructure(n)) foundedNodes++;
                foreach (var cap in n.Capabilities) if (cap.Value > 0) capabilityLevels += cap.Value;
                idleShips += CountIdleHulls(n);
            }

            int inFlightShips = 0;
            if (states != null)
                foreach (FactionState st in states)
                    inFlightShips += CountInFlightHulls(st);

            long money = GetCurrentMoneySupply();
            long collateral = GetWorldCollateralValue(states);
            long anchor = AnchoredCollateral(collateral);
            long target = TargetFromCollateral(anchor);
            return new CentralBankSnapshot
            {
                UtcNow = UtcNow,
                CurrentMoneySupply = money,
                WorldCollateralValue = collateral,
                AnchorCollateralValue = anchor,
                LastPolicyMode = _lastPolicyMode,
                LastPolicyLevied = _lastPolicyLevied,
                LastPolicyRescued = _lastPolicyRescued,
                TargetMoneySupply = target,
                MoneySupplyGap = target - money,
                ReserveRatio = Tuning.CentralBankReserveRatio,
                TerritoryNodeCount = territoryNodes,
                FoundedNodeCount = foundedNodes,
                CapabilityLevelCount = capabilityLevels,
                IdleShipCount = idleShips,
                InFlightShipCount = inFlightShips,
                ShipCount = idleShips + inFlightShips,
            };
        }

        public IList<MonetaryEvent> SnapshotCentralBankEvents()
        {
            return new List<MonetaryEvent>(_centralBankEvents);
        }

        public IList<MonetaryEvent> SnapshotDebtEvents()
        {
            return new List<MonetaryEvent>(_debtEvents);
        }

        public IList<MonetaryEvent> SnapshotMonetaryEvents()
        {
            List<MonetaryEvent> events = new List<MonetaryEvent>(_centralBankEvents.Count + _debtEvents.Count);
            events.AddRange(_centralBankEvents);
            events.AddRange(_debtEvents);
            events.Sort((a, b) => a.UtcNow.CompareTo(b.UtcNow));
            return events;
        }

        private long ComputeLedger(string factionTag)
        {
            long val = GetTreasury(factionTag);
            foreach (SpatialNode n in OwnedNodesForPass(factionTag))
                val += NodeInventoryValue(n);
            return val;
        }

        /// <summary>One node's inventory valued at market price -- the inner half of
        /// <see cref="ComputeLedger"/>.</summary>
        private long NodeInventoryValue(SpatialNode n)
        {
            IItemRegistry reg = Registry;
            long val = 0;
            foreach (var kv in n.Inventory)
            {
                if (kv.Value <= 0) continue;
                ItemKind kind;
                if (!reg.TryGetItemKind(StripInvKeyPrefix(kv.Key), out kind)) continue;
                val += ItemCredits.For(kv.Key, kv.Value, Catalogue.CreditValuePer(kind));
            }
            return val;
        }

        // ---- per-node inventory (Phase 3, SubTypeId-keyed) --------------------------------------

        /// <summary>Faction-level item stock aggregated by kind (sums all owned nodes' SubTypeId
        /// amounts that map to <paramref name="kind"/> via the registry).</summary>
        public long GetInventory(string factionTag, ItemKind kind)
        {
            long total = 0;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != factionTag) continue;
                total += SumKind(n.Inventory, kind, Registry);
            }
            return total;
        }

        /// <summary>Total stock of a specific invKey string across all nodes owned by the faction.</summary>
        public long GetFactionInventoryByKey(string factionTag, string invKey)
        {
            long total = 0;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != factionTag) continue;
                long amount;
                if (n.Inventory.TryGetValue(invKey, out amount))
                    total += amount;
            }
            return total;
        }

        /// <summary>Total <see cref="SpatialNode.ReservedInventory"/> stock of a specific invKey
        /// string across all nodes owned by the faction — the reserved-side counterpart to
        /// <see cref="GetFactionInventoryByKey"/>, used by BOM-based install-cost afford checks
        /// where a flat <see cref="ItemKind"/> aggregate isn't specific enough.</summary>
        public long GetFactionReservedInventoryByKey(string factionTag, string invKey)
        {
            long total = 0;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != factionTag) continue;
                long amount;
                if (n.ReservedInventory.TryGetValue(invKey, out amount))
                    total += amount;
            }
            return total;
        }

        /// <summary>Removes up to <paramref name="amount"/> of a specific invKey from the faction's
        /// <see cref="SpatialNode.ReservedInventory"/>, across as many owned nodes as it takes.
        /// Consumes reserved stock (does not return it to routable <c>Inventory</c>) — for
        /// completing a BOM-based capability install. Returns the amount actually removed.</summary>
        public long ConsumeFactionReservedInventoryByKey(string factionTag, string invKey, long amount)
        {
            if (amount <= 0) return 0;
            long remaining = amount;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (remaining <= 0) break;
                if (n.OwnerFactionTag != factionTag) continue;
                long have;
                if (!n.ReservedInventory.TryGetValue(invKey, out have) || have <= 0) continue;
                long take = have < remaining ? have : remaining;
                n.ReservedInventory[invKey] = have - take;
                remaining -= take;
            }
            return amount - remaining;
        }

        /// <summary>Per-node item stock aggregated by kind. Returns 0 if the node doesn't exist.</summary>
        public long GetNodeInventory(string nodeId, ItemKind kind)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n)) return 0;
            return SumKind(n.Inventory, kind, Registry);
        }

        /// <summary>Per-node stock of a specific concrete inventory key. Returns 0 if absent.</summary>
        public long GetNodeInventory(string nodeId, string inventoryKey)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || string.IsNullOrEmpty(inventoryKey)) return 0;
            long stock;
            return n.Inventory.TryGetValue(inventoryKey, out stock) ? stock : 0;
        }

        /// <summary>All SubTypeId → amount pairs for a node (for cargo packing).</summary>
        public IEnumerable<KeyValuePair<string, long>> GetNodeInventoryAll(string nodeId)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n)) return Enumerable.Empty<KeyValuePair<string, long>>();
            return n.Inventory;
        }

        /// <summary>Add (or subtract) a specific SubTypeId at a node. Floors at 0.</summary>
        /// <summary>Credits units a node actually sold to a paying counterparty, for the sale term
        /// in <see cref="PriceOracle"/>'s target-stock ranking. Called from the two bounded channels
        /// — a cross-faction trade settling against the seller's origin node, and a player buying
        /// from the station store. Not from the central-bank buyout, which has no demand-side bound;
        /// see <see cref="SpatialNode.SalesHistory"/>.
        /// <para>Credited at the SELLER's node, because the question the term answers is "how much
        /// of this is bought FROM us" — it is that shelf whose target should rise.</para></summary>
        public void RecordUnitsSold(string nodeId, string inventoryKey, long units)
        {
            // A faction-scalar trade has no origin node to credit -- the seller's stock came from
            // the faction pool rather than a shelf -- so there is nothing per-node to record.
            if (units <= 0 || string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(inventoryKey)) return;
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n)) return;
            double running;
            n.SalesHistory.TryGetValue(inventoryKey, out running);
            n.SalesHistory[inventoryKey] = running + units;
        }

        public void AddNodeInventory(string nodeId, string subTypeId, long delta)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || string.IsNullOrEmpty(subTypeId)) return;
            long cur;
            n.Inventory.TryGetValue(subTypeId, out cur);
            long next = cur + delta;
            n.Inventory[subTypeId] = next < 0 ? 0 : next;
        }

        /// <summary>Add items to a faction's nodes by ItemKind. Positive delta: adds representative
        /// SubTypeId to the first owned node. Negative delta: drains greedily from all SubTypeIds
        /// of that kind across all owned nodes. Used for action item-cost deductions.</summary>
        public void AddInventory(string factionTag, ItemKind kind, long delta)
        {
            if (delta == 0) return;
            string repr = Registry.RepresentativeSubType(kind);
            if (repr == null) return;

            if (delta > 0)
            {
                string key = ApplyInvKeyPrefix(kind, repr);
                foreach (SpatialNode n in Graph.Nodes.Values)
                    if (n.OwnerFactionTag == factionTag) { AddNodeInventory(n.Id, key, delta); return; }
            }
            else
            {
                long remaining = -delta;
                foreach (SpatialNode n in Graph.Nodes.Values)
                {
                    if (n.OwnerFactionTag != factionTag || remaining <= 0) continue;
                    DrainKindFromNode(n, kind, ref remaining, Registry);
                }
            }
        }

        /// <summary>Add a specific SubTypeId to the first owned node of a faction. Used by loot/trade
        /// resolution when no target node is known (faction-level fallback).</summary>
        public void AddSubTypeInventory(string factionTag, string subTypeId, long delta)
        {
            if (delta == 0 || string.IsNullOrEmpty(subTypeId)) return;
            foreach (SpatialNode n in Graph.Nodes.Values)
                if (n.OwnerFactionTag == factionTag) { AddNodeInventory(n.Id, subTypeId, delta); return; }
        }

        /// <summary>Set faction-level inventory by kind: clears all SubTypeIds of that kind across
        /// all owned nodes, then places the full amount as the representative SubTypeId on the first
        /// owned node. Primarily for test setup.</summary>
        public void SetInventory(string factionTag, ItemKind kind, long amount)
        {
            string repr = Registry.RepresentativeSubType(kind);
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != factionTag) continue;
                ClearKindFromNode(n, kind, Registry);
            }
            if (amount > 0 && repr != null)
            {
                string key = ApplyInvKeyPrefix(kind, repr);
                foreach (SpatialNode n in Graph.Nodes.Values)
                    if (n.OwnerFactionTag == factionTag) { n.Inventory[key] = amount; return; }
            }
        }

        // ---- inventory helpers ------------------------------------------------------------------

        public long GetReservedInventory(string factionTag, ItemKind kind)
        {
            long total = 0;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != factionTag) continue;
                total += SumKind(n.ReservedInventory, kind, Registry);
            }
            return total;
        }

        public void AddReservedInventory(string factionTag, ItemKind kind, long delta)
        {
            if (delta == 0) return;
            string repr = Registry.RepresentativeSubType(kind);
            if (repr == null) return;

            if (delta > 0)
            {
                string key = ApplyInvKeyPrefix(kind, repr);
                foreach (SpatialNode n in Graph.Nodes.Values)
                {
                    if (n.OwnerFactionTag != factionTag) continue;
                    long cur;
                    n.ReservedInventory.TryGetValue(key, out cur);
                    n.ReservedInventory[key] = cur + delta;
                    return;
                }
            }
            else
            {
                long remaining = -delta;
                foreach (SpatialNode n in Graph.Nodes.Values)
                {
                    if (n.OwnerFactionTag != factionTag || remaining <= 0) continue;
                    DrainKindFromReservedNode(n, kind, ref remaining, Registry);
                }
                if (remaining > 0) AddInventory(factionTag, kind, -remaining);
            }
        }

        // ---- inventory helpers ------------------------------------------------------------------

        // Strip "Ore:" and "Comp:" prefixes before passing to IItemRegistry.
        // "Ingot:" is intentionally NOT stripped so "Ingot:Iron" reaches the registry as-is,
        // matching the "Ingot:Iron" → ItemKind.Ingot entries and avoiding the bare "Iron" → Ore collision.
        // "Gas:" is kept for the same reason as "Ingot:": gas is litres of a GasProperties
        // definition, not a physical item, and the prefix is what tells "Gas:Hydrogen" apart from
        // the HydrogenBottle the assembler builds.
        internal static string StripInvKeyPrefix(string key)
        {
            if (key.StartsWith("Ore:"))   return key.Substring(4);
            if (key.StartsWith("Comp:"))  return key.Substring(5);
            return key;
        }

        public bool TryGetInventoryItemKind(string inventoryKey, out ItemKind kind)
        {
            kind = default(ItemKind);
            return Registry != null
                && !string.IsNullOrEmpty(inventoryKey)
                && Registry.TryGetItemKind(StripInvKeyPrefix(inventoryKey), out kind);
        }

        public long GetItemBookValue(string inventoryKey, ItemKind fallbackKind)
        {
            if (PriceCatalogue != null)
            {
                long price = PriceCatalogue.BasePrice(inventoryKey);
                if (price > 0) return price;
            }

            return Catalogue != null ? Catalogue.CreditValuePer(fallbackKind) : 0;
        }

        // Apply the correct InvKey prefix for a bare SubTypeId returned by IItemRegistry.
        // internal: NodeYieldResolver builds yield entries from a deposit, whose subtypes are bare,
        // and NodeYieldEntry.SubTypeId is the prefixed inventory key. Both must agree with the
        // node-kind table's own key form or a deposit mints under a key nothing else reads.
        internal static string ApplyInvKeyPrefix(ItemKind kind, string subTypeId)
        {
            switch (kind)
            {
                case ItemKind.Ore:
                case ItemKind.RareOre:       return "Ore:"   + subTypeId;
                case ItemKind.Ingot:         return "Ingot:" + subTypeId;
                case ItemKind.ProcessedGood: return "Comp:"  + subTypeId;
                // Gas carries its own "Gas:" prefix in the representative itself rather than gaining
                // one here, because the two registries do not agree on what represents Hydrogen: the
                // host says gas, the offline one still says a bottle. Prefixing by kind would rewrite
                // the bottle into "Gas:HydrogenBottle", which is neither.
                default:                     return subTypeId;
            }
        }

        // internal: reused by ProductionStagedWorldEffects for faction-aggregate reads over a staged
        // node-inventory overlay, so both the live and staged execution contexts classify keys with
        // the exact same registry-backed logic (see the ItemKindFromInvKey/SumKind divergence bug
        // documented in STATE.md's InstallCostDemand triage).
        internal static long SumKind(Dictionary<string, long> inv, ItemKind kind, IItemRegistry reg)
        {
            long total = 0;
            foreach (var kv in inv)
            {
                if (kv.Value <= 0) continue;
                ItemKind k;
                if (reg.TryGetItemKind(StripInvKeyPrefix(kv.Key), out k) && k == kind) total += kv.Value;
            }
            return total;
        }

        private static void DrainKindFromNode(SpatialNode n, ItemKind kind, ref long remaining, IItemRegistry reg)
        {
            // Collect keys first to avoid modifying the dict during iteration.
            var keys = new List<string>(n.Inventory.Keys);
            foreach (string key in keys)
            {
                if (remaining <= 0) break;
                ItemKind k;
                if (!reg.TryGetItemKind(StripInvKeyPrefix(key), out k) || k != kind) continue;
                long cur;
                if (!n.Inventory.TryGetValue(key, out cur) || cur <= 0) continue;
                long take = cur < remaining ? cur : remaining;
                n.Inventory[key] = cur - take;
                remaining -= take;
            }
            keys.Clear();
        }

        private static void DrainKindFromNode(SpatialNode n, ItemKind kind, long amount, IItemRegistry reg)
        {
            DrainKindFromNode(n, kind, ref amount, reg);
        }

        private static void DrainKindFromReservedNode(SpatialNode n, ItemKind kind, ref long remaining, IItemRegistry reg)
        {
            var keys = new List<string>(n.ReservedInventory.Keys);
            foreach (string key in keys)
            {
                if (remaining <= 0) break;
                ItemKind k;
                if (!reg.TryGetItemKind(StripInvKeyPrefix(key), out k) || k != kind) continue;
                long cur;
                if (!n.ReservedInventory.TryGetValue(key, out cur) || cur <= 0) continue;
                long take = cur < remaining ? cur : remaining;
                n.ReservedInventory[key] = cur - take;
                remaining -= take;
            }
            keys.Clear();
        }

        private static void ClearKindFromNode(SpatialNode n, ItemKind kind, IItemRegistry reg)
        {
            var keys = new List<string>(n.Inventory.Keys);
            foreach (string key in keys)
            {
                ItemKind k;
                if (reg.TryGetItemKind(StripInvKeyPrefix(key), out k) && k == kind)
                    n.Inventory.Remove(key);
            }
            keys.Clear();
        }

        /// <summary>Acts of aggression recorded this tick (raids); the diplomacy step drains then clears them.</summary>
        public IList<RaidIncident> Incidents { get { return _incidents; } }
        public void ClearIncidents() { _incidents.Clear(); }
        public IList<NodeSeparationViolation> SeparationViolations { get { return _separationViolations; } }
        public void ClearSeparationViolations() { _separationViolations.Clear(); }

        /// <summary>Nodes transferred to <see cref="NeutralFaction"/> by <see cref="DecayInfluence"/>
        /// this pass (a node hitting hold 0 with no explicit Withdraw), with the owner they were taken
        /// from. The host drains this to log the territory loss and rename the now-derelict station,
        /// mirroring <see cref="Incidents"/>/<see cref="ClearIncidents"/>.</summary>
        public IList<AbandonedNode> AbandonedNodes { get { return _abandonedNodes; } }
        public void ClearAbandonedNodes() { _abandonedNodes.Clear(); }

        /// <summary>Hulls moved off (or stranded on) nodes abandoned since this was last drained --
        /// both entry points, decay and an explicit <see cref="Withdraw"/>. Drained by the host the
        /// same way <see cref="AbandonedNodes"/> is, because the line belongs to whoever has a
        /// logger.
        /// <para>Provenance: FAC-278.</para></summary>
        public IList<ShipEvacuation> ShipEvacuations { get { return _shipEvacuations; } }
        public void ClearShipEvacuations() { _shipEvacuations.Clear(); }

        /// <summary>Events recorded this tick by whatever resolved them, waiting to be filed into
        /// the rings of the factions they involve. <see cref="FactionEventLog.Drain"/> is the only
        /// intended reader; it clears this. Collected here, rather than written straight to a
        /// <see cref="FactionState"/>, because the sites that know an outcome hold the world and not
        /// the faction list.</summary>
        public IList<FactionEvent> PendingEvents { get { return _pendingEvents; } }
        public void ClearPendingEvents() { _pendingEvents.Clear(); }

        /// <summary>What <see cref="Core.TrafficWatch"/> saw last pass. Runtime only.</summary>
        public TrafficWatchState TrafficWatch { get; } = new TrafficWatchState();

        private readonly List<TrafficEvent> _trafficEvents = new List<TrafficEvent>();

        /// <summary>Backstop only, as <see cref="CombatOutcomeCap"/> is: the host takes
        /// <see cref="TakeTrafficEvents"/> after every arrivals pass and the Sim never does. Larger,
        /// because a hub produces several events a minute (docs/atc-voice.md).</summary>
        public const int TrafficEventCap = 512;

        public IList<TrafficEvent> PendingTrafficEvents { get { return _trafficEvents; } }

        public void AddTrafficEvent(TrafficEvent e)
        {
            if (e == null) return;
            if (_trafficEvents.Count >= TrafficEventCap) _trafficEvents.RemoveAt(0);
            _trafficEvents.Add(e);
        }

        /// <summary>Hands over every queued <see cref="TrafficEvent"/>, oldest first, and empties the
        /// queue. Null when there are none.</summary>
        public List<TrafficEvent> TakeTrafficEvents()
        {
            if (_trafficEvents.Count == 0) return null;
            var taken = new List<TrafficEvent>(_trafficEvents);
            _trafficEvents.Clear();
            return taken;
        }

        private readonly List<CombatOutcome> _combatOutcomes = new List<CombatOutcome>();

        /// <summary>Backstop only: the host takes <see cref="TakeCombatOutcomes"/> after every
        /// arrivals pass, and the Sim never does, so the oldest outcomes past this are dropped.</summary>
        public const int CombatOutcomeCap = 64;

        /// <summary>Meeting resolutions since the last <see cref="TakeCombatOutcomes"/>.</summary>
        public IList<CombatOutcome> PendingCombatOutcomes { get { return _combatOutcomes; } }

        public void AddCombatOutcome(CombatOutcome outcome)
        {
            if (outcome == null) return;
            if (_combatOutcomes.Count >= CombatOutcomeCap) _combatOutcomes.RemoveAt(0);
            _combatOutcomes.Add(outcome);
        }

        /// <summary>Records on the newest queued outcome of <paramref name="attackerFleet"/> that broke
        /// through that it is leaving anyway, and why. The meeting and the resolution that ends the
        /// stay run in one pass, before the host takes the queue, so the player is told what actually
        /// happens rather than that the attack presses on. No-op when nothing matches.</summary>
        public void MarkCombatOutcomeWithdrawal(string attackerFleet, string reason)
        {
            if (string.IsNullOrEmpty(attackerFleet) || string.IsNullOrEmpty(reason)) return;
            for (int i = _combatOutcomes.Count - 1; i >= 0; i--)
            {
                CombatOutcome o = _combatOutcomes[i];
                if (o == null || o.AttackerFleet != attackerFleet) continue;
                if (o.Attack == CombatAttackResult.Landed && o.Withdrawal == null) o.Withdrawal = reason;
                return;
            }
        }

        /// <summary>Hands over every queued <see cref="CombatOutcome"/>, oldest first, and empties
        /// the queue. Null when there are none.</summary>
        public List<CombatOutcome> TakeCombatOutcomes()
        {
            if (_combatOutcomes.Count == 0) return null;
            var taken = new List<CombatOutcome>(_combatOutcomes);
            _combatOutcomes.Clear();
            return taken;
        }

        /// <summary>Records one thing that happened. Stamps <see cref="UtcNow"/> so every caller
        /// agrees on the clock, and drops the record for an actor-less event rather than filing
        /// something nothing can be attributed to.</summary>
        public void RecordEvent(FactionEventKind kind, string actorTag, string otherTag, string nodeId,
                                long amount = 0, double ratio = 0)
        {
            if (kind == FactionEventKind.Unknown || string.IsNullOrEmpty(actorTag)) return;
            if (_pendingEvents.Count >= PendingEventCap) _pendingEvents.RemoveAt(0);

            _pendingEvents.Add(new FactionEvent
            {
                Utc = UtcNow,
                Kind = kind,
                ActorTag = actorTag,
                OtherTag = otherTag,
                NodeId = nodeId,
                Amount = amount,
                Ratio = ratio,
            });
        }

        /// <summary>Creates and claims a new node for <paramref name="factionTag"/> near
        /// <paramref name="fromNodeId"/>, or returns <c>null</c> when no sampled position clears the
        /// separation in force there. A refusal creates nothing and records nothing: separation is a
        /// hard constraint on placement, and a region with no legal position has no node to give.</summary>
        public SpatialNode ExpandFrom(string factionTag, string fromNodeId)
        {
            Vec3 origin = new Vec3(0, 0, 0);
            SpatialNode from;
            if (fromNodeId != null && Graph.Nodes.TryGetValue(fromNodeId, out from))
                origin = from.Position;

            const int MaxAttempts = 8;
            Vec3 pos = origin;
            NodeZone zone = NodeZone.DeepSpace;
            double minSep = 0;
            bool foundLegalCandidate = false;
            double bestScore = double.MinValue;
            // Ranked ahead of roominess rather than blended with it. A blend needs a weight, and there
            // is no exchange rate between "km from the nearest neighbour" and "could ever hold ore";
            // ranking needs none, and it leaves roominess deciding outright whenever the candidates
            // agree -- including when none of them could bear ore, which is the whole of deep space.
            bool bestCouldBearOre = false;
            // Read once: it gates the ranking, not the predicate. CouldBearOreAt stays honest for its
            // other callers -- a flag inside it would hand them "false everywhere" and look like an
            // answer.
            bool preferOreBearingGround = Tuning.ExpandPrefersGroundThatCouldBearOre;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                double step = ExpandStep * (1.0 + attempt * 0.25);
                if (fromNodeId != null)
                {
                    double originMinSep = SeparationAtKm(origin);
                    if (originMinSep > 0)
                    {
                        double reachedStep = originMinSep * (1.0 + Tuning.NodeExpansionReachPct);
                        if (reachedStep > step) step = reachedStep;
                    }
                }

                int sampleCount = Tuning.NodeExpansionSampleCount > 0 ? Tuning.NodeExpansionSampleCount : 1;
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    Vec3 candidatePos = RandomPointOnSphere(origin, step);
                    NodeZone candidateZone = PlaceableZoneAt(candidatePos);

                    bool midAir;
                    candidatePos = PlaceNode(candidatePos, candidateZone, out midAir);
                    if (midAir) continue;

                    double candidateMinSep = SeparationAtKm(candidatePos);
                    if (candidateMinSep > 0 && HasNodeWithin(candidatePos, candidateZone, candidateMinSep))
                        continue;

                    double nearestDistance = NearestNodeDistance(candidatePos);

                    double reachCap = candidateMinSep > 0 ? candidateMinSep * (1.0 + Tuning.NodeExpansionReachPct) : step;
                    double score = candidateMinSep > 0 ? Math.Min(nearestDistance, reachCap) : nearestDistance;
                    bool candidateCouldBearOre = preferOreBearingGround && CouldBearOreAt(candidatePos);
                    if (!foundLegalCandidate
                        || (candidateCouldBearOre && !bestCouldBearOre)
                        || (candidateCouldBearOre == bestCouldBearOre && score > bestScore))
                    {
                        foundLegalCandidate = true;
                        bestScore = score;
                        bestCouldBearOre = candidateCouldBearOre;
                        pos = candidatePos;
                        zone = candidateZone;
                        minSep = candidateMinSep;
                    }
                }
            }

            if (!foundLegalCandidate) return null;

            return CommitNode(factionTag, pos, zone, minSep, fromNodeId);
        }

        /// <summary>Whether a position could hold ore at all — asked of candidate positions before one
        /// is committed to, so it must not sample. Open space outside every body and authored feature
        /// is the decisive case: nothing will ever be found there, however long it is surveyed.
        ///
        /// <para><b>Coverage, not richness.</b> It cannot rank two points on the same body — only ~3%
        /// of a planet's ore texels carry anything, so no cheap point answer can — and it is not meant
        /// to. Composition is the survey's job (<see cref="TrySurveyOnce"/>).</para>
        ///
        /// <para>Public because placement asks this question in more than one place: resolving an
        /// authored region to a coordinate needs the same predicate <see cref="ExpandFrom"/> uses, and
        /// two implementations of it would drift (FAC-106).</para></summary>
        public bool CouldBearOreAt(Vec3 posKm)
        {
            return Ores != null && Ores.CouldBearOre(posKm) == OreProspect.Possible;
        }

        /// <summary>The authored feature claiming <paramref name="posKm"/>, or null — the value
        /// <see cref="SpatialNode.FeatureKey"/> carries. One call site for the rule so commit and
        /// reclassify cannot record different features for one position.</summary>
        private string FeatureKeyAt(Vec3 posKm)
        {
            string name;
            return Features.TryFeatureAt(posKm, out name) ? name : null;
        }

        /// <summary>
        /// Bring one node into being at an already-decided position: ownership, derived content,
        /// separation bookkeeping and topology links.
        ///
        /// <para><b>The single commit point for a node.</b> <see cref="ExpandFrom"/> reaches it after
        /// sampling outward from a parent; <see cref="SeedNodeAt"/> reaches it with a position an
        /// estate recipe chose. Both must produce the same kind of node, and the way to guarantee
        /// that is one method rather than two that agree today.</para>
        /// </summary>
        private SpatialNode CommitNode(string factionTag, Vec3 pos, NodeZone zone, double minSep, string fromNodeId,
                                       string id = null)
        {
            SpatialNode n = Graph.Add(pos, 0.0, id);
            n.OwnerFactionTag = factionTag;
            n.HoldStrength = 0.5;
            n.LastReinforcedUtc = UtcNow;
            n.Zone = zone;
            n.PlanetId = zone != NodeZone.DeepSpace ? NearestPlanetId(pos) : null;
            n.Site = StationSiting.Classify(Planets, pos);
            n.FeatureKey = FeatureKeyAt(pos);
            // Nothing about the parent reaches the child: a node's content is what is under it, so a
            // faction's first node -- which has no parent at all -- resolves by exactly this path.
            ResolveDeposit(n);
            // Unconditional, because ResolveDeposit returns early when the seam declines -- which is
            // every node under NullOreGeography. Without this a node no sampler could reach would be
            // committed at zero rather than at the unsurveyed floor its site allows.
            RefreshNodeContent(n);
            if (minSep > 0)
            {
                NodeSeparationViolation violation;
                if (TryCreateNodeSeparationViolation(n.Id, pos, zone, minSep, out violation))
                    _separationViolations.Add(violation);
            }
            if (fromNodeId != null) Graph.Connect(fromNodeId, n.Id);
            // Beyond the single origin edge above, also connect to every other existing node already
            // within the same reach-cap radius used for placement scoring -- ExpandFrom otherwise
            // only ever produces a single-edge expansion tree (one link back to whichever node it grew
            // from), which leaves SpatialGraph.Frontier -- and therefore Claim-based reclaim of nearby
            // UNKN territory -- blind to anything not exactly one hop from currently-owned ground, even
            // when it sits well within the same local neighborhood in real space. See
            // docs/economy-territory.md's "Abandonment" subsection.
            if (minSep > 0)
            {
                double reachCap = minSep * (1.0 + Tuning.NodeExpansionReachPct);
                foreach (SpatialNode nearby in NodesWithinRadius(pos, reachCap, n.Id))
                    Graph.Connect(n.Id, nearby.Id);
            }
            return n;
        }

        /// <summary>
        /// Create a node for <paramref name="factionTag"/> at an authored position — the estate
        /// seeder's entry point, and the only way a faction's <b>first</b> node comes into being.
        ///
        /// <para>Until this existed the world could not be populated at all: every territorial action
        /// closes over nodes a faction already owns, so `factions init` on an empty graph left every
        /// faction permanently landless (FAC-105). <see cref="ExpandFrom"/> could not fill the gap —
        /// it samples <i>outward from a parent</i>, and with none it samples around the world origin.</para>
        ///
        /// <para>The position is snapped and classified by the same rules as an expanded node —
        /// <see cref="PlaceableZoneAt"/> then <see cref="PlaceNode"/> — so an authored point inside a
        /// planet lifts to its surface, one in a ring lands in a band, and one on a body that forbids
        /// surface nodes goes to orbit instead. Separation is recorded, never enforced: an
        /// over-subscribed region is a refusal the caller makes <i>before</i> seeding
        /// (<see cref="PlacementPlanner"/>), because by here the node is already placed.</para>
        /// </summary>
        public SpatialNode SeedNodeAt(string factionTag, Vec3 posKm)
        {
            bool midAir;
            Vec3 placed = ProjectSeedPosition(posKm, out midAir);
            if (midAir) return null;
            // Re-derive after the snap: lifting a point to a surface or into a ring band can move it
            // across a region boundary, and the zone that matters is the one it ends up in.
            NodeZone finalZone = PlaceableZoneAt(placed);
            return CommitNode(factionTag, placed, finalZone, SeparationAtKm(placed), null);
        }

        /// <summary>Brings a node the <c>UNKN</c> sweep deleted back under its old id, at full hold,
        /// for <paramref name="factionTag"/> — the operator's repair for ground lost to a defect
        /// (FAC-502). The same commit as <see cref="SeedNodeAt"/>, so zone, planet, site and ore
        /// content are re-derived from the position exactly as a new node's are; only the id and the
        /// hold differ. What the node knew from its survey, and what it held, went with the sweep.
        /// Returns null when the id is still in the graph.</summary>
        public SpatialNode RecreateNodeAt(string factionTag, string id, Vec3 posKm)
        {
            if (factionTag == null || string.IsNullOrEmpty(id) || Graph.Nodes.ContainsKey(id)) return null;
            Vec3 placed = ProjectSeedPosition(posKm);
            NodeZone finalZone = PlaceableZoneAt(placed);
            SpatialNode n = CommitNode(factionTag, placed, finalZone, SeparationAtKm(placed), null, id);
            n.HoldStrength = 1.0;
            return n;
        }

        /// <summary>
        /// Where an authored point actually ends up, without committing anything. Read-only, so a
        /// dry run can measure the real placement rather than the point it asked for.
        /// <para>Extracted rather than repeated: a caller computing this itself would be a second
        /// implementation of one rule, which is how the authored separation stayed inert for a day
        /// (FAC-105). <see cref="SeedNodeAt"/> calls this, so the two cannot drift.</para>
        /// </summary>
        public Vec3 ProjectSeedPosition(Vec3 posKm)
        {
            bool midAir;
            return ProjectSeedPosition(posKm, out midAir);
        }

        /// <summary><see cref="ProjectSeedPosition(Vec3)"/>, also saying whether the placement is a
        /// surface snap that could not find the ground (see <see cref="PlaceNode(Vec3, NodeZone, out bool)"/>).</summary>
        public Vec3 ProjectSeedPosition(Vec3 posKm, out bool midAir)
        {
            return PlaceNode(posKm, PlaceableZoneAt(posKm), out midAir);
        }

        /// <summary>
        /// Whether a node could be created at <paramref name="posKm"/>, and where it would land —
        /// deciding nothing and committing nothing. The read-only twin of <see cref="SeedNodeAt"/>,
        /// which is what a player-driven Expand needs before it charges anyone
        /// (docs/economy-territory.md → Expanding; FAC-197).
        ///
        /// <para>Every number comes from the same call <see cref="SeedNodeAt"/> makes, in the same
        /// order, so a probe that says "clear" describes the node the next commit would produce. The
        /// occupancy test is the per-position rule the graph is audited against
        /// (<see cref="ProjectedSeparationDetail"/>): is anything inside <i>my</i> required radius.
        /// It is deliberately not a pairwise test — a node may legally sit inside a wider-spaced
        /// neighbour's bubble, and asserting otherwise here would refuse placements the world model
        /// itself considers sound.</para>
        /// </summary>
        public NodePlacementProbe ProbePlacement(Vec3 posKm)
        {
            bool midAir;
            Vec3 placed = ProjectSeedPosition(posKm, out midAir);
            double separation = SeparationAtKm(placed);
            double nearest = NearestNodeDistance(placed);
            var probe = new NodePlacementProbe
            {
                RequestedKm = posKm,
                PlacedKm = placed,
                Zone = PlaceableZoneAt(placed),
                SeparationKm = separation,
                NearestNodeKm = nearest,
                NoGround = midAir,
            };
            // A non-positive separation is the world declining to impose a rule here, not a radius
            // everything violates -- CommitNode skips its own violation bookkeeping on exactly the
            // same condition. A point with no ground under it is never clear: SeedNodeAt refuses it.
            // Measured per neighbour, so an orbit site owes a surface node below it only that node's
            // own separation (OwedSeparationKm).
            string crowdingId;
            double crowdingKm, owedKm;
            probe.Clear = !midAir && (separation <= 0
                || !TryGetNearestNodeWithin(null, placed, probe.Zone, separation, out crowdingId, out crowdingKm, out owedKm));
            return probe;
        }

        /// <summary>
        /// How many of <paramref name="placedKm"/> sit closer to another node than the separation in
        /// force where they are — counted against each other <b>and</b> against nodes already in the
        /// graph, which nothing else checks a seeded node against.
        ///
        /// <para>Per position, not per pair, matching <see cref="GetNodeSeparationViolations"/>:
        /// "is anything inside my required radius". Separation comes from
        /// <see cref="SeparationAtKm"/>, the same authority, so a projected count and a committed one
        /// mean the same thing and can be compared.</para>
        ///
        /// <para>Positions must already be projected (see <see cref="ProjectSeedPosition"/>).
        /// Measuring the lattice instead would report zero by construction — the lattice is built to
        /// its own spacing, and projection onto real terrain is what can break it.</para>
        /// </summary>
        public int ProjectedSeparationViolations(IList<Vec3> placedKm,
                                                 out double minSpacingKm, out double minRequiredKm)
        {
            double closestOverall;
            IList<ProjectedViolation> detail = ProjectedSeparationDetail(placedKm, out closestOverall);

            minSpacingKm = 0;
            minRequiredKm = 0;
            if (detail.Count == 0)
            {
                // No breach: report the closest pair as the healthy margin it is, and quote no
                // requirement -- there isn't one that was missed.
                if (closestOverall < double.MaxValue) minSpacingKm = closestOverall;
                return 0;
            }

            ProjectedViolation worst = detail[0];
            foreach (ProjectedViolation v in detail)
                if (v.RequiredKm - v.NearestKm > worst.RequiredKm - worst.NearestKm) worst = v;

            minSpacingKm = worst.NearestKm;
            minRequiredKm = worst.RequiredKm;
            return detail.Count;
        }

        /// <summary>
        /// Every projected position sitting closer to another than the separation in force where it
        /// is, with the numbers that made it a violation. <paramref name="closestOverallKm"/> is the
        /// closest pair anywhere, violation or not.
        /// <para>Detail rather than a count, because "1 node in violation" out of 65 is not
        /// actionable — the caller knows which index came from which faction and region, and can
        /// name the offender.</para>
        /// </summary>
        public IList<ProjectedViolation> ProjectedSeparationDetail(IList<Vec3> placedKm,
                                                                   out double closestOverallKm)
        {
            return ProjectedSeparationDetail(placedKm, ExistingNodePositions(), out closestOverallKm);
        }

        /// <summary>Every node position currently in the graph. Snapshot this <b>before</b> committing
        /// anything you intend to measure: on an apply the committed nodes are in the graph too, so a
        /// live read pairs each placed position with its own committed twin at zero distance. That
        /// reported 8 impossible "0 km from its nearest neighbour" violations on the first real
        /// apply — every one an artefact, while the dry run of the same placement was correct.</summary>
        public List<Vec3> ExistingNodePositions()
        {
            var all = new List<Vec3>();
            foreach (SpatialNode n in Graph.Nodes.Values) all.Add(n.Position);
            return all;
        }

        public IList<ProjectedViolation> ProjectedSeparationDetail(IList<Vec3> placedKm,
                                                                   IList<Vec3> existingKm,
                                                                   out double closestOverallKm)
        {
            closestOverallKm = double.MaxValue;
            var result = new List<ProjectedViolation>();
            if (placedKm == null || placedKm.Count == 0) return result;

            var others = new List<Vec3>();
            if (existingKm != null) others.AddRange(existingKm);

            for (int i = 0; i < placedKm.Count; i++)
            {
                Vec3 a = placedKm[i];
                double required = SeparationAtKm(a);
                double nearest = double.MaxValue;

                for (int j = 0; j < placedKm.Count; j++)
                {
                    if (j == i) continue;
                    double d = a.DistanceTo(placedKm[j]);
                    if (d < nearest) nearest = d;
                }
                for (int j = 0; j < others.Count; j++)
                {
                    double d = a.DistanceTo(others[j]);
                    if (d < nearest) nearest = d;
                }

                if (nearest == double.MaxValue) continue;
                if (nearest < closestOverallKm) closestOverallKm = nearest;
                if (required <= 0 || nearest >= required) continue;

                var v = new ProjectedViolation();
                v.Index = i;
                v.PositionKm = a;
                v.NearestKm = nearest;
                v.RequiredKm = required;
                v.Zone = PlaceableZoneAt(a);
                v.InsideFeature = Features.TrySeparationAt(a, out v.FeatureSeparationKm);
                result.Add(v);
            }
            return result;
        }

        public IList<NodeSeparationViolation> GetNodeSeparationViolations()
        {
            var violations = new List<NodeSeparationViolation>();
            var nodes = new List<SpatialNode>(Graph.Nodes.Values);
            for (int i = 0; i < nodes.Count; i++)
            {
                SpatialNode node = nodes[i];
                double requiredMinSepKm = SeparationAtKm(node.Position);
                if (requiredMinSepKm <= 0) continue;

                NodeSeparationViolation violation;
                if (TryCreateNodeSeparationViolation(node.Id, node.Position, node.Zone, requiredMinSepKm, out violation))
                    violations.Add(violation);
            }
            return violations;
        }

        private Vec3 RandomPointOnSphere(Vec3 center, double radius)
        {
            // Rejection-sample a point on the unit sphere, then scale.
            double x, y, z, len;
            int guard = 0;
            do
            {
                x = _rng.NextDouble() * 2.0 - 1.0;
                y = _rng.NextDouble() * 2.0 - 1.0;
                z = _rng.NextDouble() * 2.0 - 1.0;
                len = Math.Sqrt(x * x + y * y + z * z);
                if (++guard > 200) { x = 1; y = z = 0; len = 1; break; }
            } while (len < 1e-9 || len > 1.0); // reject corners of the cube
            double scale = radius / len;
            return new Vec3(center.X + x * scale, center.Y + y * scale, center.Z + z * scale);
        }

        /// <summary>The gravity reading of a position: inside a well (<c>g &gt; 0</c>) or inside the
        /// body is Surface, anywhere else DeepSpace. It never says Orbit -- that zone is authored
        /// (a planet's orbit band, Cauldron's ring) and read by <see cref="PlaceableZoneAt"/> first.
        /// See docs/economy-territory.md, "Zones".</summary>
        /// <summary>
        /// The zone a <b>node</b> may occupy at this position, as opposed to the zone the position
        /// physically is (<see cref="ClassifyZone"/>). They differ on a body whose own generator
        /// definition forbids surface stations: the point really is inside that body's gravity and
        /// really would classify Surface, but nothing may stand there, so it is placed in orbit
        /// instead.
        /// <para>Kept separate from <see cref="ClassifyZone"/> deliberately — zone classification is
        /// a statement about gravity that other systems rely on being physically true, while this is
        /// a placement policy. Every site that turns a position into a node's resting place must use
        /// this one, including the boot-time repair pass: a node placed correctly and then re-snapped
        /// by <see cref="ReclassifyNodes"/> ends up on the forbidden surface anyway.</para>
        /// </summary>
        /// <summary>
        /// The minimum distance between faction nodes at <paramref name="pos"/> — how densely this
        /// place may be settled.
        ///
        /// <para>Precedence, the same shape placement and zone already use: <b>an authored feature
        /// wins; else an authored body wins on its own surface; else the global per-zone floor.</b>
        /// A world that authors nothing gets exactly <c>NodeSeparationFor(zone)</c>, so this is
        /// additive.</para>
        ///
        /// <para><b>Why it stopped being three globals.</b> A single number per zone makes settlement
        /// density a property of the gravity bucket rather than of the place. Cauldron's ring and a
        /// barren stretch of deep space are both DeepSpace; Kor and Tellus are both Surface while one
        /// is a third the size. Separation is the knob that decides how many factions a body can
        /// hold, so it belongs to the body and the region.</para>
        ///
        /// <para><b>Deliberately uncapped</b> (operator, 2026-08-26: *"No cap, authored intent
        /// wins."*). A small authored value on a large region is a very large number of nodes, and
        /// node count is what the influence field and the per-faction brain files scale with. Inside
        /// an authored region the constraint is the author's, not the geometry's.</para>
        /// </summary>
        /// <summary>How far a re-derived worth must differ from the stored one to count as a change.
        /// Below this it is float noise from a re-read of the same evidence, and counting it would
        /// report every node on every boot and rewrite every brain file for nothing.</summary>
        private const double WorthChangeEpsilon = 1e-9;

        /// <summary>Re-derive both readings of a node from what is currently known about it. Called
        /// wherever the deposit changes — the first sample and every survey step — because
        /// <see cref="SpatialNode.ResourceValue"/> and <see cref="SpatialNode.Richness"/> are
        /// readings of the evidence, not stored judgements, and a stale one would leave a surveyed
        /// node still priced as unproven.
        ///
        /// <para>Both together in one call on purpose: they advance off the same survey, so a site
        /// that refreshed one and not the other would leave a node's worth and its density disagreeing
        /// about how far it has been looked at.</para></summary>
        private void RefreshNodeContent(SpatialNode n)
        {
            if (n == null) return;
            n.ResourceValue = NodeContent.ResourceValueFor(n, Planets, Tuning, PriceCatalogue, Registry);
            n.Richness = NodeContent.RichnessFor(n, Tuning);
        }

        /// <summary>The catchment radius for a deposit sampled at <paramref name="pos"/>: half the
        /// separation actually in force there, authored value included. Reads
        /// <see cref="SeparationAtKm"/> rather than the per-zone tuning default, so a body the world
        /// packs more tightly is sampled over correspondingly less ground.</summary>
        public double OreSampleRadiusKmAt(Vec3 pos)
        {
            return Tuning.OreSampleRadiusForSeparation(SeparationAtKm(pos));
        }

        public double SeparationAtKm(Vec3 pos)
        {
            double authored;
            if (Features.TrySeparationAt(pos, out authored)) return authored;

            NodeZone zone = PlaceableZoneAt(pos);
            if (zone == NodeZone.Surface)
            {
                // NearestByCentre, not the gravity well: this is the body whose ground we are on.
                PlanetRecord body = PlanetGeometry.NearestByCentre(Planets, pos);
                if (body != null && body.NodeSeparationKm.HasValue && body.NodeSeparationKm.Value > 0)
                    return body.NodeSeparationKm.Value;
            }
            return Tuning.NodeSeparationFor(zone);
        }

        /// <summary>How far <see cref="ExpandFrom"/> reaches from a node at <paramref name="pos"/>
        /// when linking neighbours — its separation plus the expansion overshoot.</summary>
        private double ReachCapAt(Vec3 pos)
        {
            double minSep = SeparationAtKm(pos);
            return minSep > 0 ? minSep * (1.0 + Tuning.NodeExpansionReachPct) : 0;
        }

        public NodeZone PlaceableZoneAt(Vec3 pos)
        {
            // The authored world wins where it has an opinion; gravity is the fallback for everywhere
            // it does not. Same precedence the recipe already has over placement -- a feature is a
            // statement about a region, a gravity sample is a guess about it.
            NodeZone zone;
            if (!Features.TryZoneAt(pos, out zone)) zone = ClassifyZone(pos);
            if (zone != NodeZone.Surface) return zone;

            // Applied to both paths, so an authored Surface on a body that forbids surface stations
            // cannot produce a combination neither rule would allow on its own.
            PlanetRecord body = GravityPlanetRecord(pos);
            return body != null && !body.AllowsSurfaceStations ? NodeZone.Orbit : zone;
        }

        public NodeZone ClassifyZone(Vec3 pos)
        {
            // Inside the body itself is Surface as a matter of geometry, decided before any gravity
            // reading. SE's natural gravity falls off linearly toward zero going INWARD from the
            // surface, and at the core it reads zero (which would classify DeepSpace). It must be
            // lifted to the surface.
            PlanetRecord body = GravityPlanetRecord(pos);
            if (body != null && body.SurfaceRadius > 0 && pos.DistanceTo(body.Center) < body.SurfaceRadius)
                return NodeZone.Surface;

            // The whole well is Surface. SE cuts natural gravity off at 0.05 g (GRAVITY_LIMIT_STRENGTH),
            // so there is no low-gravity shell for an Orbit zone to occupy: a relative threshold put
            // the Orbit snap on the cutoff itself, where a probe read g == 0 and quoted the 500 km
            // DeepSpace floor. Orbit is authored instead -- each planet's band just outside its well,
            // and Cauldron's ring (FAC-537).
            return Planets.GravityAt(pos) > 0 ? NodeZone.Surface : NodeZone.DeepSpace;
        }

        /// <summary>The nearest planet's own true surface gravity — the denominator for the relative
        /// Orbit snap-band (see <see cref="SnapToOrbitBand"/>). 0 when no planet is nearby. Never throws; callers must guard
        /// divide-by-zero themselves. Samples a synthetic radius rather than
        /// <see cref="IPlanetCatalogue.NearestSurfacePoint"/> (a terrain-following lookup on the live
        /// host) so the value is a stable per-planet constant — see
        /// <see cref="PlanetGeometry.LocalSurfaceG"/>.</summary>
        private double LocalSurfaceG(Vec3 pos)
        {
            return PlanetGeometry.LocalSurfaceG(Planets, GravityPlanetRecord(pos));
        }

        /// <summary>
        /// Where a node at <paramref name="pos"/> actually belongs, given its
        /// <paramref name="zone"/> — the single place the authored world and the planet heuristic are
        /// reconciled.
        ///
        /// <para><b>The orbit snap never outranks a feature; the surface snap still competes by
        /// displacement.</b> The two planet candidates are not the same kind of answer.
        /// <see cref="SnapToOrbitBand"/> is a guess about which radius reads as an orbit, and
        /// <see cref="PlaceableZoneAt"/> already states the rule for a guess: the authored world wins
        /// where it has an opinion, gravity is the fallback for everywhere it does not.
        /// <see cref="IPlanetCatalogue.NearestSurfacePoint"/> is a fact about where the ground is, so
        /// it keeps its contest — a <see cref="WorldFeatureShape.Shell"/> deliberately claims the body
        /// it surrounds, and without the contest a station 1.5 km off that ground would be flung out
        /// to the shell's inner face.</para>
        ///
        /// <para><b>Why the orbit snap had to stop competing.</b> A ring is thin and wide, so for a
        /// point above the ring plane the feature's move is at most its half-thickness while the
        /// radial move to the gravity limit is however far the point is from that radius. Nearest-wins
        /// therefore picked the feature everywhere except close to the gravity-limit radius and far
        /// off the plane — pulling those nodes onto the gravity limit instead of into the authored
        /// ring, 120 of them on the pre-re-seed world, all in one 25 km radius bucket. See
        /// docs/issues/FAC-225.</para>
        ///
        /// <para>A node already where the recipe wants it moves zero either way, a node in a ring
        /// 180 km out is never dragged onto the 9.5 km voxel core the gravity heuristic mistakes for
        /// its surface, and a DeepSpace node has no planet candidate at all, so there the feature
        /// simply places it.</para>
        ///
        /// <para>Outside every feature's claim this is exactly the pre-feature behaviour, which is why
        /// a world nobody has authored features for does not move a single node.</para>
        /// </summary>
        public Vec3 PlaceNode(Vec3 pos, NodeZone zone)
        {
            bool midAir;
            return PlaceNode(pos, zone, out midAir);
        }

        /// <summary><see cref="PlaceNode(Vec3, NodeZone)"/>, also saying whether the result is a
        /// surface snap that left the point standing above its body's highest terrain -- the snap
        /// could not find the ground, and the point is where no station will ever stand. Every
        /// placement path refuses such a point instead of committing a node, or checking separation,
        /// there. An authored feature's own placement is never reported: the recipe is the authority
        /// on where its region puts a node.</summary>
        public Vec3 PlaceNode(Vec3 pos, NodeZone zone, out bool midAir)
        {
            midAir = false;
            Vec3 featurePos;
            bool byFeature = Features.TryPlace(pos, out featurePos);

            Vec3 planetPos = pos;
            bool byPlanet = true;
            if (zone == NodeZone.Surface) planetPos = Planets.NearestSurfacePoint(pos);
            else if (zone == NodeZone.Orbit) planetPos = SnapToOrbitBand(pos);
            else byPlanet = false;

            bool tookFeature = byFeature
                && (!byPlanet || zone == NodeZone.Orbit
                    || featurePos.DistanceSquaredTo(pos) <= planetPos.DistanceSquaredTo(pos));
            if (tookFeature) return featurePos;
            if (!byPlanet) return pos;

            midAir = zone == NodeZone.Surface
                     && StandsAboveTerrain(planetPos);
            return planetPos;
        }

        /// <summary>Radially repositions <paramref name="candidatePos"/> (already classified Orbit) so
        /// its relative gravity (g / that planet's own true surface g) lands at/just inside
        /// <see cref="FactionTuning.OrbitGravityMin"/> — the Orbit snap-band's relative ceiling. Bisects
        /// numerically along the ray from the nearest planet's <see cref="PlanetRecord.Center"/> through
        /// <paramref name="candidatePos"/>, sampling the REAL <see cref="IPlanetCatalogue.GravityAt"/>
        /// at each step rather than assuming any particular falloff curve (the live SE host's true
        /// falloff shape is not assumed to be linear) — the same principle as
        /// <see cref="IPlanetCatalogue.NearestSurfacePoint"/> asking the host directly rather than this
        /// class re-deriving planet geometry. Returns <paramref name="candidatePos"/> unchanged if no
        /// planet is found, or it sits within 1e-6 km of the planet's exact center (no well-defined
        /// radial direction — mirrors <see cref="RandomPointOnSphere"/>'s own degenerate-corner
        /// guard).</summary>
        private Vec3 SnapToOrbitBand(Vec3 candidatePos)
        {
            PlanetRecord planet = GravityPlanetRecord(candidatePos);
            if (planet == null) return candidatePos;

            double dx = candidatePos.X - planet.Center.X;
            double dy = candidatePos.Y - planet.Center.Y;
            double dz = candidatePos.Z - planet.Center.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6) return candidatePos; // degenerate: at the planet's exact center

            double ux = dx / len, uy = dy / len, uz = dz / len;

            double bestR = PlanetGeometry.OrbitBandRadius(Planets, planet, Tuning.OrbitGravityMin, new Vec3(ux, uy, uz));
            if (bestR <= 0) return candidatePos; // no surface-g baseline, or a malformed radius pair
            return new Vec3(planet.Center.X + ux * bestR, planet.Center.Y + uy * bestR, planet.Center.Z + uz * bestR);
        }

        /// <summary>The nearest <see cref="PlanetRecord"/> to <paramref name="pos"/> by
        /// <see cref="PlanetRecord.Center"/> distance, or null when <see cref="Planets"/> has none.
        /// Factored out of <see cref="NearestPlanetId"/> so <see cref="SnapToOrbitBand"/> can reuse the
        /// same lookup instead of re-deriving it.</summary>
        private PlanetRecord NearestPlanetRecord(Vec3 pos)
        {
            return PlanetGeometry.NearestByCentre(Planets, pos);
        }

        /// <summary>The planet whose gravity well actually contains <paramref name="pos"/> — the
        /// nearest one whose <see cref="PlanetRecord.GravityRadius"/> the point is inside — falling
        /// back to <see cref="NearestPlanetRecord"/> when the point is outside every well.
        ///
        /// <para>This, not <see cref="NearestPlanetRecord"/>, is the correct reference for anything
        /// derived from <see cref="IPlanetCatalogue.GravityAt"/>, which reports the gravity of the
        /// well the point is in. The two disagree around a moon: a moon orbits inside its parent's
        /// well, so a point out near the moon can be closer to the MOON's center while all the gravity
        /// there is the PARENT's. Using the moon as the reference then divides the parent's gravity by
        /// the moon's surface gravity and snaps into the moon's band, which is both a wrong
        /// classification and an arbitrarily long reposition.</para></summary>
        private PlanetRecord GravityPlanetRecord(Vec3 pos)
        {
            return PlanetGeometry.GravityWellPlanet(Planets, pos);
        }

        /// <summary>The furthest a legitimate reposition can have moved a node at <paramref name="pos"/>:
        /// the greater of its gravity planet's <c>[SurfaceRadius, GravityRadius]</c> band width
        /// (bounding <see cref="SnapToOrbitBand"/>, which lands inside that band) and its surface
        /// radius (bounding the Surface snap, which can start anywhere inside the body). 0 when there
        /// is no planet at all, i.e. nothing could have moved it.
        ///
        /// <para>A station separated from its node by more than this did not get there through
        /// ordinary repositioning, so re-anchoring it is not a safe automatic repair — the node
        /// itself may be the wrong side of the pair. Used by the drift sweep to fence off exactly
        /// that case instead of asking an operator for a magic distance.</para></summary>
        public double MaxPlausibleSnapDistanceKm(Vec3 pos)
        {
            // A feature placement is a legitimate reposition too, and on a ring it reaches further
            // than the planet band does. Omitting it would make the sweep refuse to re-anchor exactly
            // the stations the authored world just moved.
            double reach = Features.MaxPlacementDisplacementKm(pos);

            PlanetRecord planet = GravityPlanetRecord(pos);
            if (planet != null)
            {
                double bandWidth = planet.GravityRadius - planet.SurfaceRadius;
                // A Surface snap starts anywhere inside the body, so it can legitimately move a node
                // by up to the surface radius -- which on a large planet exceeds the orbit band's
                // width.
                double planetReach = bandWidth > planet.SurfaceRadius ? bandWidth : planet.SurfaceRadius;
                if (planetReach > reach) reach = planetReach;
            }
            return reach > 0 ? reach : 0;
        }

        /// <summary>The nearest <see cref="PlanetRecord.Id"/> to <paramref name="pos"/>, or null when
        /// no planet is in <see cref="Planets"/> at all. Planets are physically isolated environments
        /// for local logistics purposes — this is the identity that distinguishes "the same planet"
        /// from merely "the same <see cref="NodeZone"/> bucket" (a Surface node on Terra and a Surface
        /// node on Mars are both `Surface`, but only `NearestPlanetId` tells them apart). Callers
        /// should gate on the caller's own <see cref="NodeZone"/> already being Surface/Orbit before
        /// trusting this for "on this planet" decisions — a genuinely DeepSpace position still returns
        /// whichever planet happens to be geometrically nearest, however far away.</summary>
        public string NearestPlanetId(Vec3 pos)
        {
            PlanetRecord nearest = NearestPlanetRecord(pos);
            return nearest != null ? nearest.Id : null;
        }

        /// <summary>Samples the ground under <paramref name="n"/> and stores the resulting deposit,
        /// demoting the node to a non-mineable kind when nothing is there.
        /// <para>Only mineable kinds are sampled — a Core or Refinery node is a place, not a
        /// deposit, and sampling one would pay for an answer nothing reads.</para>
        /// <para><b>The node is never moved.</b> Expansion geometry decides where a node sits and
        /// the ground decides what it is; on a map where ore is sparse most mineable rolls therefore
        /// demote, and mineable nodes end up clustered where ore actually is. That is the intent, not
        /// a shortfall — see docs/ore-geography.md.</para>
        /// <para>A seam that declines to sample (deep space today, until world features exist) leaves
        /// <see cref="SpatialNode.Deposit"/> null and the kind untouched, so an unwired host keeps
        /// exactly its current behaviour.</para></summary>
        public void ResolveDeposit(SpatialNode n)
        {
            DepositResolution unused;
            ResolveDeposit(n, out unused);
        }

        /// <summary>As <see cref="ResolveDeposit(SpatialNode)"/>, reporting what happened.
        /// <para>The backfill pass needs the distinction: a node the seam <see
        /// cref="DepositResolution.Declined"/> to answer for still has no deposit and is still
        /// mineable, so a loop keyed only on "has a deposit" would retry it every pass forever.
        /// <see cref="DepositResolution.Sampled"/> and <see cref="DepositResolution.Barren"/> both
        /// leave the candidate set on their own — the first gains a deposit, the second is demoted
        /// out of the mineable kinds.</para></summary>
        public void ResolveDeposit(SpatialNode n, out DepositResolution result)
        {
            result = DepositResolution.NotApplicable;
            if (n == null) return;

            OreSample sample;
            if (!Ores.TrySample(n.Position, OreSampleRadiusKmAt(n.Position), out sample))
            {
                Vec3 projected;
                if (!TryProjectedOrePoint(n.Position, out projected)
                    || !Ores.TrySample(projected, OreSampleRadiusKmAt(projected), out sample))
                {
                    result = DepositResolution.Declined;
                    return;
                }
            }

            // The propensity bias is read from what the node currently reads as, not from what the
            // roll made it: a node is sampled before it is named, so this is the previous derivation
            // biasing the next one rather than a die biasing the first.
            n.Deposit = DepositModel.Derive(sample);
            RefreshNodeContent(n);
            if (n.Deposit.IsBarren)
            {
                result = DepositResolution.Barren;
                return;
            }

            result = DepositResolution.Sampled;
        }

        /// <summary>
        /// Samples one mineable node that has no deposit yet, if the graph still holds any.
        ///
        /// <para><b>Why this exists.</b> <see cref="ExpandFrom"/> is the only other caller of
        /// <see cref="ResolveDeposit"/>, so nodes that arrive any other way — a scan, a migration, a
        /// brain file written before the deposit model existed — never get sampled and keep minting
        /// the node-kind table's global ore mix regardless of what is actually under them. That is
        /// the very defect the deposit model exists to remove, left in place for exactly the nodes a
        /// fresh world starts with. See FAC-083.</para>
        ///
        /// <para>One node per call, because a sample is an area scan over the ore cubemap and the
        /// caller runs on the update tick. The candidate set drains on its own: both a sampled node
        /// and a barren one come away holding a deposit — a barren one holds an empty deposit rather
        /// than none — so neither is a candidate twice, and both round-trip that state through the
        /// brain file, so a restart does not re-offer them. Only a <see cref="DepositResolution.Declined"/>
        /// node, which is left holding nothing, would be retried forever,
        /// which is what <paramref name="attempted"/> is for — deliberately caller-owned and
        /// transient, so a restart re-tries it once. That is correct rather than wasteful: the
        /// authored world features it depends on may have changed since.</para>
        ///
        /// <para>Returns false when nothing was left to do.</para>
        /// </summary>
        public bool TryBackfillOneDeposit(HashSet<string> attempted, out string nodeId, out DepositResolution result)
        {
            nodeId = null;
            result = DepositResolution.NotApplicable;

            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                // Cheapest first: this walks the graph, and only TrySample does real work.
                if (n.Deposit != null) continue;
                if (attempted != null && attempted.Contains(n.Id)) continue;

                if (attempted != null) attempted.Add(n.Id);
                nodeId = n.Id;
                ResolveDeposit(n, out result);
                return true;
            }
            return false;
        }

        /// <summary>Where a node's ore is read when its own position answers nothing: the nearest
        /// point inside the feature claiming it. False when no feature claims the position, or it is
        /// already inside one, so a node on ground that genuinely has nothing is still declined.
        ///
        /// <para><b>The node is not moved.</b> A node off a ring's plane whose clamp would land on a
        /// neighbour is refused the reposition (<see cref="RepositionWouldCrowd"/>), and without this
        /// it holds no deposit forever: 10 nodes 36-66 km off the Cauldron Ring's plane, live
        /// 2026-10-09. Reading the slab beneath it gives it the ground its feature claims it for.
        /// Only consulted after the position itself declined, so a node a planet answers for is never
        /// re-read off a feature. See docs/issues/FAC-213.</para></summary>
        private bool TryProjectedOrePoint(Vec3 posKm, out Vec3 projectedKm)
        {
            if (!Features.TryPlace(posKm, out projectedKm)) return false;
            return projectedKm.DistanceSquaredTo(posKm) > FeatureContainmentMinMoveKmSq;
        }

        /// <summary>True while <paramref name="n"/>'s composition is still less certain than the
        /// survey's target. The survey queue's membership test, and the reason a node leaves it.
        /// <para>A barren node is never a candidate: there is nothing to refine, and re-surveying it
        /// forever would be the exact never-converging loop the paced pass exists to avoid.</para></summary>
        public bool NeedsSurvey(SpatialNode n)
        {
            if (!NodeContent.IsMineable(n)) return false;
            // Surveying is work a faction does. An abandoned node surveying itself would hand back
            // the knowledge abandonment just took, only slower -- the penalty would undo itself over
            // the following days with nothing to show that it had.
            if (n.OwnerFactionTag == null || n.OwnerFactionTag == NeutralFaction.Tag) return false;
            // A stale deposit is in other units; adding to it would mix them. The re-survey owns it.
            if (n.Deposit.IsStale) return false;
            return !SurveyMath.IsSurveyed(n.Deposit.PointsExamined, Tuning.SurveyTargetShare);
        }

        /// <summary>True while <paramref name="n"/>'s deposit was measured in older units and the
        /// re-survey has not replaced it yet. Owned or not: what a node reads is the economy's
        /// concern either way, and the old reading stays live until the new one is ready.</summary>
        public static bool NeedsResurvey(SpatialNode n)
        {
            return n != null && n.Deposit != null && n.Deposit.IsStale;
        }

        /// <summary>
        /// One step of re-measuring a <see cref="NodeDeposit.IsStale"/> deposit in today's units.
        ///
        /// <para><b>The old deposit stays live until the new one has covered the same ground.</b> The
        /// new survey accumulates in <see cref="SpatialNode.PendingDeposit"/> over the same lattice
        /// points the old one walked, and replaces it only once it has examined as many — so every
        /// reader sees the old reading, then the new one at the same confidence, and never a node that
        /// has briefly forgotten its ground. Swapping at a fresh first sample instead would drop every
        /// node to its survey floor for the duration, and a node that briefly mints nothing is one the
        /// brain stops reinforcing.</para>
        ///
        /// <para>Covers at least <see cref="FactionTuning.ResurveyMinimumPoints"/> even when the old
        /// deposit held fewer, so a barren or barely-sampled node gets a real look at the layers and
        /// scatter the old units could not see.</para>
        ///
        /// <para>Returns false when the node needs no re-survey or the seam declined.</para>
        /// </summary>
        public bool TryResurveyStep(SpatialNode n, int probeBudget)
        {
            if (!NeedsResurvey(n) || probeBudget <= 0) return false;

            long target = Math.Max(n.Deposit.PointsExamined, (long)Math.Max(1, Tuning.ResurveyMinimumPoints));
            NodeDeposit pending = n.PendingDeposit;
            long done = pending != null ? pending.PointsExamined : 0;
            int budget = (int)Math.Min(probeBudget, Math.Max(1L, target - done));

            OreSample sample;
            if (!Ores.TryResample(n.Position, OreSampleRadiusKmAt(n.Position), done, budget, out sample))
            {
                Vec3 projected;
                if (!TryProjectedOrePoint(n.Position, out projected)
                    || !Ores.TryResample(projected, OreSampleRadiusKmAt(projected), done, budget, out sample))
                    return false;
            }

            NodeDeposit step = DepositModel.Derive(sample);
            if (pending == null)
            {
                pending = step;
            }
            else
            {
                pending.Accumulate(step.Entries, sample.PointsExamined);
            }
            n.PendingDeposit = pending;

            // A seam that examined nothing would never reach the target; settle for what it has.
            if (pending.PointsExamined >= target || sample.PointsExamined <= 0)
            {
                n.Deposit = pending;
                n.PendingDeposit = null;
                RefreshNodeContent(n);
            }
            return true;
        }

        /// <summary>
        /// Adds <paramref name="probeBudget"/> more observations to one node's deposit, refining its
        /// composition rather than replacing it.
        ///
        /// <para>The first sample established what is <b>there</b>; this establishes how much of
        /// each. Presence never waits for the survey — a node is mineable from its first sample and
        /// only its mix sharpens, which is why this returns quietly rather than gating anything.</para>
        ///
        /// <para>Resumability is carried entirely by <see cref="NodeDeposit.PointsExamined"/>: the
        /// seam is asked for points starting at that offset, and every point is a fixed place
        /// regardless of how many follow. A restart therefore continues the survey instead of
        /// re-walking ground already covered.</para>
        ///
        /// <para>Returns false when the node needs no survey or the seam declined.</para>
        /// </summary>
        public bool TrySurveyOnce(SpatialNode n, int probeBudget)
        {
            if (!NeedsSurvey(n) || probeBudget <= 0) return false;

            OreSample sample;
            if (!Ores.TryResample(n.Position, OreSampleRadiusKmAt(n.Position),
                                  n.Deposit.PointsExamined, probeBudget, out sample))
            {
                Vec3 projected;
                if (!TryProjectedOrePoint(n.Position, out projected)
                    || !Ores.TryResample(projected, OreSampleRadiusKmAt(projected),
                                         n.Deposit.PointsExamined, probeBudget, out sample))
                    return false;
            }

            NodeDeposit step = DepositModel.Derive(sample);
            n.Deposit.Accumulate(step.Entries, sample.PointsExamined);
            RefreshNodeContent(n);

            return true;
        }

        /// <summary>A Campaign focus region: <paramref name="centerNodeId"/> plus every owned node
        /// reachable within <paramref name="traverseDepth"/> proximity hops. A candidate is admitted
        /// at a hop if it sits within <c>2 × Tuning.NodeSeparationFor</c> of its own zone (not the
        /// minimum inter-node placement floor itself — nodes are never actually that close by
        /// construction) of any node already in the region; the candidate's OWN zone sets its
        /// admission radius, so a node's neighborhood scales with how tightly-packed that kind of
        /// space naturally is (tight on a surface, sparse in deep space). Scoped to
        /// <paramref name="factionTag"/>'s owned nodes only — Reinforce/Patrol never act on
        /// non-owned nodes, so there's no reason to admit them into the region.</summary>
        public HashSet<string> ComputeCampaignRegionMembers(string factionTag, string centerNodeId, int traverseDepth)
        {
            var members = new HashSet<string>();
            if (centerNodeId == null || !Graph.Nodes.ContainsKey(centerNodeId)) return members;
            members.Add(centerNodeId);

            List<SpatialNode> owned = Graph.OwnedBy(factionTag).ToList();
            var frontier = new List<string> { centerNodeId };

            for (int hop = 0; hop < traverseDepth && frontier.Count > 0; hop++)
            {
                var nextFrontier = new List<string>();
                foreach (SpatialNode candidate in owned)
                {
                    if (members.Contains(candidate.Id)) continue;
                    double radiusKm = 2.0 * SeparationAtKm(candidate.Position);
                    foreach (string regionNodeId in frontier)
                    {
                        SpatialNode regionNode;
                        if (!Graph.Nodes.TryGetValue(regionNodeId, out regionNode)) continue;
                        if (candidate.Position.DistanceTo(regionNode.Position) <= radiusKm)
                        {
                            members.Add(candidate.Id);
                            nextFrontier.Add(candidate.Id);
                            break;
                        }
                    }
                }
                frontier = nextFrontier;
            }

            return members;
        }

        /// <summary>
        /// Validate and optionally fix the stored <see cref="SpatialNode.Zone"/> for every node by
        /// re-sampling live SE gravity via <see cref="Planets"/>. When <paramref name="applyFix"/> is
        /// true, also repositions: an authored <see cref="WorldFeaturePlacement"/> places every Orbit
        /// node it claims, and every Surface node it claims and moves less than the surface snap
        /// would; otherwise Surface nodes snap to <see cref="IPlanetCatalogue.NearestSurfacePoint"/>
        /// when they sit more than 1 km from the planet surface sphere (e.g. nodes placed before
        /// surface-snapping was introduced) and Orbit nodes outside the tightened relative-gravity
        /// band snap via <see cref="SnapToOrbitBand"/> (e.g. nodes placed before relative Orbit
        /// classification was introduced). See <see cref="PlaceNode"/> for why the orbit snap yields
        /// to a feature outright while the surface snap still competes.
        /// <para><b>Zone labelling is unchanged.</b> A feature decides where a node sits, not what
        /// <see cref="ClassifyZone"/> calls it — retiring the label is a separate step (FAC-096
        /// stage 2), and doing both at once would move the nodes and re-route their logistics in the
        /// same pass.</para>
        /// <para><b>No reposition may crowd a neighbour.</b> Every move above is checked against the
        /// separation in force where it would land, and declined if it breaks it — see
        /// <see cref="RepositionWouldCrowd"/> for why, and for what that cost the live world before
        /// the check existed. <paramref name="log"/>, when supplied, gets one
        /// <c>NODE_REPOSITION_BLOCKED</c> line per refusal, the same per-entry convention
        /// <see cref="ClearOrbitStranding"/> uses.</para>
        /// </summary>
        /// <summary>Smallest Orbit-snap displacement treated as a real reposition: (1 m)^2, in km^2
        /// to match <see cref="SpatialNode.Position"/>'s units. Below this the snap is a no-op and
        /// must not be counted or persisted.</summary>
        private const double OrbitSnapMinMoveKmSq = 1e-6;

        /// <summary>Smallest feature-containment correction treated as a real reposition: (1 mm)^2,
        /// in km^2.
        /// <para><b>Far below <see cref="OrbitSnapMinMoveKmSq"/>, because the two gates answer
        /// different questions.</b> An orbit snap slides a node along a continuous band, so a
        /// centimetre of it is noise worth ignoring. Feature containment is a hard boundary: a node
        /// a centimetre outside a ring is outside it completely, and the ore seam declines the
        /// position — so a displacement floor sized for the first gate silently strands nodes under
        /// the second. This floor exists only to refuse float noise from re-clamping a node already
        /// inside, which <see cref="WorldFeature.NearestPointInside"/> returns unchanged.</para>
        /// </summary>
        private const double FeatureContainmentMinMoveKmSq = 1e-12;

        /// <summary>The longest reposition <see cref="RepositionWouldCrowd"/> lets a node that is
        /// already crowded make: 100 m. Ten times the containment inset plus the largest anchor
        /// movement measured, and 0.3% of the ring's 30 km separation.</summary>
        private const double CrowdedNudgeMaxKm = 0.1;

        public NodeReclassifyResult ReclassifyNodes(bool applyFix, List<string> log = null)
        {
            var result = new NodeReclassifyResult();
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                result.Total++;
                NodeZone newZone = PlaceableZoneAt(n.Position);

                switch (newZone)
                {
                    case NodeZone.Surface: result.Surface++;  break;
                    case NodeZone.Orbit:   result.Orbit++;    break;
                    default:               result.DeepSpace++; break;
                }

                if (n.Zone != newZone)
                {
                    result.ZoneChanged++;
                    if (applyFix) n.Zone = newZone;
                }

                string newPlanetId = newZone != NodeZone.DeepSpace ? NearestPlanetId(n.Position) : null;
                if (n.PlanetId != newPlanetId)
                {
                    result.PlanetIdChanged++;
                    if (applyFix) n.PlanetId = newPlanetId;
                }


                // The authored world's candidate, and the planet heuristic's -- reconciled exactly
                // as PlaceNode reconciles them: the feature takes the Orbit branch outright and
                // competes with the Surface branch by displacement. Spelled out here rather than
                // delegated because each planet branch keeps its own "is this worth moving at all"
                // gate, which a fresh placement does not have.
                Vec3 featurePos;
                bool byFeature = Features.TryPlace(n.Position, out featurePos);
                double featureMoveSq = byFeature ? featurePos.DistanceSquaredTo(n.Position) : double.MaxValue;

                if (newZone == NodeZone.Surface)
                {
                    Vec3 surfPos = Planets.NearestSurfacePoint(n.Position);
                    double surfMoveSq = surfPos.DistanceSquaredTo(n.Position);
                    if (byFeature && featureMoveSq <= surfMoveSq)
                    {
                        if (featureMoveSq > FeatureContainmentMinMoveKmSq
                            && !RepositionWouldCrowd(n, featurePos, ref result, log))
                        {
                            result.FeaturePlaced++;
                            if (applyFix) n.Position = featurePos;
                        }
                    }
                    else if (surfMoveSq > 1.0 // farther than 1 km from surface sphere
                             && !RepositionWouldCrowd(n, surfPos, ref result, log,
                                    groundEvenIfCrowded: StandsAboveTerrain(n.Position)))
                    {
                        result.Repositioned++;
                        if (applyFix) n.Position = surfPos;
                    }
                }
                else if (newZone == NodeZone.Orbit)
                {
                    double g = Planets.GravityAt(n.Position);
                    double localSurfaceG = LocalSurfaceG(n.Position);
                    double relativeG = localSurfaceG > 0 ? g / localSurfaceG : 0;
                    // Count only a real move. SnapToOrbitBand returns its input unchanged when it
                    // has no band to work with (no planet, no surface-gravity baseline, malformed
                    // radii) and lands on the same radius for a node already at the band edge, so
                    // counting the intent instead would report the same nodes on every pass
                    // forever -- and, via FactionRuntime's flush-on-repositioned, rewrite every
                    // faction's brain JSON each boot for no change.
                    bool wantsOrbitSnap = relativeG <= 0 || relativeG > Tuning.OrbitGravityMin;
                    Vec3 snapped = wantsOrbitSnap ? SnapToOrbitBand(n.Position) : n.Position;
                    double orbitMoveSq = wantsOrbitSnap ? snapped.DistanceSquaredTo(n.Position) : double.MaxValue;

                    // No displacement comparison: an authored region outranks the orbit snap
                    // whatever the two distances are. See PlaceNode.
                    if (byFeature)
                    {
                        if (featureMoveSq > FeatureContainmentMinMoveKmSq
                            && !RepositionWouldCrowd(n, featurePos, ref result, log))
                        {
                            result.FeaturePlaced++;
                            if (applyFix) n.Position = featurePos;
                        }
                    }
                    else if (wantsOrbitSnap && orbitMoveSq > OrbitSnapMinMoveKmSq
                             && !RepositionWouldCrowd(n, snapped, ref result, log))
                    {
                        result.OrbitRepositioned++;
                        if (applyFix) n.Position = snapped;
                    }
                }
                else if (byFeature && featureMoveSq > FeatureContainmentMinMoveKmSq
                         && !RepositionWouldCrowd(n, featurePos, ref result, log))
                {
                    // DeepSpace: no planet candidate at all, so the authored world places it alone.
                    result.FeaturePlaced++;
                    if (applyFix) n.Position = featurePos;
                }

                // After the position branches, not beside Zone and PlanetId: Site is read off where
                // the node ends up, and every branch above can still move it. Derived and rebuilt
                // here for the same reason those two are -- none of the three is persisted.
                StationSite newSite = StationSiting.Classify(Planets, n.Position);
                if (n.Site != newSite)
                {
                    result.SiteChanged++;
                    if (applyFix) n.Site = newSite;
                }

                // Same terms as Site: read off the final position, derived, rebuilt every boot.
                string newFeatureKey = FeatureKeyAt(n.Position);
                if (!string.Equals(n.FeatureKey, newFeatureKey, StringComparison.Ordinal))
                {
                    result.FeatureKeyChanged++;
                    if (applyFix) n.FeatureKey = newFeatureKey;
                }

                // Worth last, because every branch above can move the node and what a site is worth
                // depends on where it is. Here rather than only at the four evidence-driven refresh
                // sites (commit, deposit resolution, survey step, abandonment) because a node the ore
                // seam declined for reaches none of them again: it gains no deposit, so it is never
                // surveyed, and it round-trips its stored worth across every restart. A change to how
                // worth is read would otherwise apply to nodes created afterwards and to nothing that
                // already exists.
                double newWorth = NodeContent.ResourceValueFor(n, Planets, Tuning, PriceCatalogue, Registry);
                if (Math.Abs(n.ResourceValue - newWorth) > WorthChangeEpsilon)
                {
                    result.WorthChanged++;
                    if (applyFix) n.ResourceValue = newWorth;
                }

                // Richness beside it, for the same reason and one more: it is newer than every node
                // on disk, so without this pass a world that predates it reads zero everywhere and
                // mints nothing at all. Counted separately because the two move independently — a
                // retune of one saturation constant must not report the other as having changed.
                double newRichness = NodeContent.RichnessFor(n, Tuning);
                if (Math.Abs(n.Richness - newRichness) > WorthChangeEpsilon)
                {
                    result.RichnessChanged++;
                    if (applyFix) n.Richness = newRichness;
                }
            }
            // After the pass, so an apply reports the world it is leaving behind. Nothing else counts
            // this: the per-commit record in CommitNode only fires where a node is BORN, so crowding
            // that arrives any other way had no reader at all.
            result.StandingViolations = GetNodeSeparationViolations().Count;
            return result;
        }

        /// <summary>Whether moving <paramref name="n"/> to <paramref name="targetKm"/> would land it
        /// inside the separation in force there, counting and reporting the refusal when it would.
        ///
        /// <para><b>A retrofit must not create what placement refuses.</b> Every branch of
        /// <see cref="ReclassifyNodes"/> exists to bring a node created under older rules into line
        /// with the current ones, and each judges only its own axis — how far off a surface, how far
        /// out of a gravity band, how far outside an authored feature. None of them can see a
        /// neighbour, so a ring's height-and-band clamp will happily press two settled nodes onto the
        /// same band face — and this pass runs after the commit-time check, which fires only where a
        /// node is born. Scale, measured live 2026-08-29: 158 of 378 nodes inside the Cauldron Ring
        /// stood closer than its authored 30 km, the closest pair 0.40 km apart, 147 of them pinned to
        /// the slab's exact clamp height. See docs/issues/FAC-175.</para>
        ///
        /// <para>Declining leaves the node exactly where it was, which is a position the world has
        /// already been running on. That is strictly better than a silent teleport into a neighbour,
        /// and unlike one it is visible: the refusal is counted in
        /// <see cref="NodeReclassifyResult.SeparationBlocked"/> and, when a
        /// <paramref name="log"/> is supplied, named per node.</para>
        ///
        /// <para><b>A short correction to a node already crowded where it stands is not refused.</b>
        /// It creates nothing: a move of <see cref="CrowdedNudgeMaxKm"/> changes any neighbour distance
        /// by at most that much, against a separation of tens of km. Refusing it only strands the
        /// node outside its feature, where the ore seam declines it. Measured live 2026-10-09: 168
        /// nodes refused on every boot, 158 of them 0-3 m outside the ring and every one of those
        /// already inside 30 km of a neighbour. The standing crowding stays counted in
        /// <see cref="NodeReclassifyResult.StandingViolations"/>. See docs/issues/FAC-213.</para></summary>
        /// <summary>Whether <paramref name="posKm"/> stands more than
        /// <see cref="StationSiting.GroundToleranceKm"/> above the highest terrain of the body under it
        /// (<see cref="PlanetGeometry.HeightAboveTerrainKm"/>): a position no station can stand on.</summary>
        private bool StandsAboveTerrain(Vec3 posKm)
        {
            return PlanetGeometry.HeightAboveTerrainKm(Planets, posKm) > StationSiting.GroundToleranceKm;
        }

        private bool RepositionWouldCrowd(SpatialNode n, Vec3 targetKm, ref NodeReclassifyResult result,
                                          List<string> log, bool groundEvenIfCrowded = false)
        {
            double requiredKm = SeparationAtKm(targetKm);
            if (requiredKm <= 0) return false;

            NodeSeparationViolation violation;
            if (!TryCreateNodeSeparationViolation(n.Id, targetKm, PlaceableZoneAt(targetKm), requiredKm,
                                                  out violation))
                return false;

            string standingNeighbor;
            double standingKm, standingOwedKm;
            if (n.Position.DistanceSquaredTo(targetKm) <= CrowdedNudgeMaxKm * CrowdedNudgeMaxKm
                && TryGetNearestNodeWithin(n.Id, n.Position, n.Zone, requiredKm,
                                           out standingNeighbor, out standingKm, out standingOwedKm))
                return false;

            // A node in mid-air is grounded even when that crowds a neighbour. Its station hovers where
            // nothing can stand, and the separation it passed was measured at that mid-air point. The
            // pair it leaves is counted in StandingViolations like any other.
            if (groundEvenIfCrowded)
            {
                if (log != null)
                    log.Add("NODE_GROUNDED_INSIDE_SEPARATION node=" + n.Id
                            + " neighbor=" + violation.OffendingNeighborId
                            + " actualKm=" + violation.ActualDistanceKm.ToString("0.0")
                            + " requiredKm=" + violation.RequiredMinSepKm.ToString("0.0"));
                return false;
            }

            result.SeparationBlocked++;
            if (log != null)
                log.Add("NODE_REPOSITION_BLOCKED node=" + n.Id
                        + " neighbor=" + violation.OffendingNeighborId
                        + " actualKm=" + violation.ActualDistanceKm.ToString("0.0")
                        + " requiredKm=" + violation.RequiredMinSepKm.ToString("0.0"));
            return true;
        }

        /// <summary>One-time retrofit for nodes created before <see cref="ExpandFrom"/> started
        /// connecting a new node to every existing node within its reach cap, not just its origin
        /// (see that method's own comment). Scans the whole graph for still-unconnected pairs within
        /// the SMALLER of either endpoint's own per-zone reach cap
        /// (<c>NodeSeparationFor(zone) * (1 + NodeExpansionReachPct)</c>) and links them. Requiring
        /// both sides' own reach to agree (not just one) deliberately excludes the case where a
        /// DeepSpace node's large ~625km-class reach would otherwise pull in a Surface/Orbit node
        /// sitting well outside that node's own much smaller ~15-30km-class reach — live-measured to
        /// matter: this world is 90%+ DeepSpace by node count, and an earlier Max-based version
        /// produced ~230 avg neighbors/node, most of it same-zone density rather than meaningful
        /// cross-zone reach. Spatially bucketed (cell size = the largest reach cap across all three
        /// zones — still a safe, if conservative, bucket size under the smaller Min threshold actually
        /// used per pair) rather than a naive O(n²) double loop: each node only compares against its
        /// own bucket + the 26 neighboring buckets. Dry-run (<paramref name="applyFix"/> false) reports
        /// counts without mutating anything — <see cref="TopologyDensifyResult.ElapsedMs"/> is
        /// populated either way so the real cost can be checked live before ever running `apply`.
        /// Mutates only <see cref="SpatialNode.Neighbors"/> via <see cref="SpatialGraph.Connect"/> —
        /// never touches ownership, hold, or any other field.</summary>
        public TopologyDensifyResult DensifyTopology(bool applyFix)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = new TopologyDensifyResult();
            var nodes = new List<SpatialNode>(Graph.Nodes.Values);
            result.NodesScanned = nodes.Count;

            double cellSize = MaxReachCap();
            if (cellSize <= 0)
            {
                result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                return result;
            }

            var buckets = new Dictionary<BucketKey, List<SpatialNode>>();
            foreach (SpatialNode n in nodes)
            {
                BucketKey key = BucketKeyFor(n.Position, cellSize);
                List<SpatialNode> bucket;
                if (!buckets.TryGetValue(key, out bucket)) buckets[key] = bucket = new List<SpatialNode>();
                bucket.Add(n);
            }

            var checkedPairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialNode a in nodes)
            {
                double reachA = ReachCapAt(a.Position);
                BucketKey center = BucketKeyFor(a.Position, cellSize);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    List<SpatialNode> bucket;
                    if (!buckets.TryGetValue(new BucketKey(center.X + dx, center.Y + dy, center.Z + dz), out bucket)) continue;
                    foreach (SpatialNode b in bucket)
                    {
                        if (ReferenceEquals(a, b)) continue;
                        string pairKey = string.CompareOrdinal(a.Id, b.Id) < 0 ? a.Id + "" + b.Id : b.Id + "" + a.Id;
                        if (!checkedPairs.Add(pairKey)) continue;

                        // Both sides' own zone-reach must agree (Min, not Max): a DeepSpace node's
                        // large 625km-class reach must not pull in a Surface/Orbit node sitting well
                        // outside that node's own much smaller 15-30km-class reach just because the
                        // DeepSpace side alone would have swept that far. Cuts out cross-zone
                        // over-reach; same-zone pairs (the overwhelming majority in a mostly-DeepSpace
                        // world) are unaffected either way.
                        double reach = Math.Min(reachA, ReachCapAt(b.Position));
                        if (reach <= 0 || a.Position.DistanceSquaredTo(b.Position) > reach * reach) continue;

                        result.PairsWithinReach++;
                        if (a.Neighbors.Contains(b.Id)) continue; // already linked -- idempotent
                        result.LinksAdded++;
                        if (applyFix) Graph.Connect(a.Id, b.Id);
                    }
                }
            }

            result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        private double ReachCapFor(NodeZone zone)
        {
            double minSep = Tuning.NodeSeparationFor(zone);
            return minSep > 0 ? minSep * (1.0 + Tuning.NodeExpansionReachPct) : 0;
        }

        /// <summary>The largest reach any node can have anywhere in this world — the bucket size
        /// <see cref="DensifyTopology"/> scans with. Authored separations are included because an
        /// authored value may exceed every global floor, and a bucket smaller than the reach being
        /// searched drops pairs silently rather than reporting fewer.</summary>
        private double MaxReachCap()
        {
            double max = Math.Max(ReachCapFor(NodeZone.Surface),
                         Math.Max(ReachCapFor(NodeZone.Orbit), ReachCapFor(NodeZone.DeepSpace)));

            double authored = Features.MaxAuthoredSeparationKm();
            foreach (PlanetRecord p in Planets.Planets)
                if (p != null && p.NodeSeparationKm.HasValue && p.NodeSeparationKm.Value > authored)
                    authored = p.NodeSeparationKm.Value;

            double authoredReach = authored > 0 ? authored * (1.0 + Tuning.NodeExpansionReachPct) : 0;
            return authoredReach > max ? authoredReach : max;
        }

        private struct BucketKey : IEquatable<BucketKey>
        {
            public readonly long X, Y, Z;
            public BucketKey(long x, long y, long z) { X = x; Y = y; Z = z; }
            public bool Equals(BucketKey other) { return X == other.X && Y == other.Y && Z == other.Z; }
            public override bool Equals(object obj) { return obj is BucketKey && Equals((BucketKey)obj); }
            public override int GetHashCode() { return (X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode(); }
        }

        private static BucketKey BucketKeyFor(Vec3 pos, double cellSize)
        {
            return new BucketKey((long)Math.Floor(pos.X / cellSize), (long)Math.Floor(pos.Y / cellSize), (long)Math.Floor(pos.Z / cellSize));
        }

        /// <summary>Network-wide maintenance pass clearing stranded-inventory fallout from the Orbit
        /// zone-classification/placement fix: (a) repositions existing Orbit nodes into the tightened
        /// relative band (delegates to <see cref="ReclassifyNodes"/> — no new placement logic here),
        /// then (b) cancels any node's <see cref="SpatialNode.BuildQueue"/> entry that can no longer be
        /// delivered from its (possibly just-moved) node — refunding <see cref="BuildQueueEntry
        /// .Collected"/> — and (c) dismantles any non-store-reserved <see cref="SpatialNode.ShipStock"/>
        /// that is no longer route-feasible from its own node, crediting the full BOM back to node
        /// inventory. Runs network-wide regardless of which nodes (a) actually touched, since builds/
        /// ships can be stranded by causes other than the Orbit-placement bug (e.g. a same-hint prefab
        /// pool that is uniformly infeasible on Surface too). Dry-run (<paramref name="applyFix"/>
        /// false) reports counts without mutating anything. <paramref name="log"/>, when supplied, gets
        /// one <c>BUILD_CANCELED_ZONE</c>/<c>SHIP_DISMANTLED_STRANDED</c> line per affected entry —
        /// mirrors <see cref="ShipyardManager.Tick"/>'s own per-entry logging convention.</summary>
        public OrbitStrandingClearResult ClearOrbitStranding(bool applyFix, List<string> log = null)
        {
            var result = new OrbitStrandingClearResult();
            NodeReclassifyResult reclass = ReclassifyNodes(applyFix);
            result.NodesRepositioned = reclass.Repositioned + reclass.OrbitRepositioned + reclass.FeaturePlaced;

            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                // Cancel doomed builds.
                for (int i = n.BuildQueue.Count - 1; i >= 0; i--)
                {
                    BuildQueueEntry e = n.BuildQueue[i];
                    if (e.PrefabId == null) continue; // free build, no BOM, nothing to gate
                    // A station design is built where it stands and never flown anywhere, so the
                    // route-feasibility question this pass asks does not apply to it -- and a static
                    // grid with no thrusters fails it by construction.
                    if (ShipyardManager.IsStationRefit(e)) continue;

                    ShipPool.RouteEnvironment buildRoute = ShipPool.RouteEnvironment.From(
                        Planets, n.Position, n.Position, Tuning.MinDryTwr);
                    if (ShipPool.IsRouteFeasible(PrefabCatalogue, e.PrefabId, buildRoute)) continue;

                    result.BuildsCanceled++;
                    long refunded = 0;
                    foreach (var kv in e.Collected)
                    {
                        if (applyFix) AddNodeInventory(n.Id, kv.Key, kv.Value);
                        refunded += kv.Value;
                    }
                    result.BuildItemsRefunded += (int)refunded;
                    if (log != null)
                        log.Add(n.OwnerFactionTag + " BUILD_CANCELED_ZONE node=" + n.Id + " prefab=" + e.PrefabId
                                + " refunded=" + refunded);
                    if (applyFix)
                    {
                        e.Collected.Clear();
                        n.BuildQueue.RemoveAt(i);
                    }
                }

                // Dismantle stranded (non-store) ship stock.
                var prefabIds = new List<string>(n.ShipStock.Keys); // snapshot -- Remove mutates ShipStock
                foreach (string prefabId in prefabIds)
                {
                    int available = ShipPool.Available(n, prefabId);
                    if (available <= 0) continue;

                    ShipPool.RouteEnvironment shipRoute = ShipPool.RouteEnvironment.From(
                        Planets, n.Position, n.Position, Tuning.MinDryTwr);
                    if (ShipPool.IsRouteFeasible(PrefabCatalogue, prefabId, shipRoute)) continue;

                    result.ShipsDismantled += available;
                    long credited = DismantleParkedHulls(n, prefabId, available, applyFix);
                    result.BomUnitsCredited += (int)credited;
                    if (log != null)
                        log.Add(n.OwnerFactionTag + " SHIP_DISMANTLED_STRANDED node=" + n.Id + " prefab=" + prefabId
                                + " count=" + available + " bomCredited=" + credited);
                }
            }
            return result;
        }

        /// <summary>Turns <paramref name="count"/> parked, unreserved hulls of
        /// <paramref name="prefabId"/> at <paramref name="node"/> back into their BOM, credited to that
        /// node's inventory, and returns the units credited. A prefab with no recipe credits nothing and
        /// is still removed. With <paramref name="apply"/> false nothing changes and the return value is
        /// what would have been credited.
        ///
        /// <para>The caller passes no more than <see cref="ShipPool.Available"/>: a reserved shelf hull
        /// has to be handed back first, or <see cref="SpatialNode.ListedForSale"/> ends up larger than
        /// the stock it reserves. The one dismantle loop the operator's stranding sweep
        /// (<see cref="ClearOrbitStranding"/>) and a decommission (<see cref="FleetDecommission"/>)
        /// share.</para></summary>
        internal long DismantleParkedHulls(SpatialNode node, string prefabId, int count, bool apply)
        {
            if (node == null || count <= 0) return 0;
            bool hasRecipe;
            Dictionary<string, long> bom = ProductionManager.GetPrefabBom(Recipes, prefabId, out hasRecipe);
            long credited = 0;
            foreach (var kv in bom)
            {
                long c = kv.Value * count;
                if (apply) AddNodeInventory(node.Id, kv.Key, c);
                credited += c;
            }
            if (apply) ShipPool.Remove(node, prefabId, count);
            return credited;
        }

        /// <summary>Walks every node's dispatch-eligible ship stock (<see cref="ShipPool.Available"/>
        /// — units reserved via <see cref="SpatialNode.ListedForSale"/> are excluded, since a store
        /// purchase draws from them directly and isn't gated by route feasibility) and flags units
        /// whose dry TWR already fails at their OWN node's real gravity/atmosphere. Because <see
        /// cref="ShipPool.IsRouteFeasible"/> requires the origin side to clear <see
        /// cref="FactionTuning.MinDryTwr"/> regardless of destination, a unit that fails here can
        /// never be dispatched anywhere from this node — this is a complete, not approximate, count
        /// of unconditionally-stranded stock. Unscanned prefabs (<c>DryMassKg == 0</c>) and a
        /// disabled gate (<c>MinDryTwr &lt;= 0</c>) are skipped, matching the "unknown, don't gate"
        /// rollout-safety rule everywhere else this data is consumed. Read-only; O(nodes ×
        /// distinct-prefabs-in-stock).</summary>
        public StrandedShipStockResult AuditStrandedShipStock(int maxDetails = 25)
        {
            var result = new StrandedShipStockResult { Details = new List<string>() };
            var strandedPrefabIds = new HashSet<string>();
            double minDryTwr = Tuning.MinDryTwr;

            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.ShipStock.Count == 0) continue;

                double g = Planets.GravityAt(n.Position);
                bool inAtmo = ThrustPhysics.InAtmosphere(Planets, n.Position);
                double air = ThrustPhysics.AirDensity(Planets, n.Position);
                bool nodeHasStranded = false;

                foreach (var kv in n.ShipStock)
                {
                    int available = ShipPool.Available(n, kv.Key);
                    if (available <= 0) continue;
                    result.TotalAvailableUnits += available;

                    if (PrefabCatalogue == null || minDryTwr <= 0) continue;
                    double dryMassKg = PrefabCatalogue.DryMassKg(kv.Key);
                    if (dryMassKg <= 0) continue; // never scanned — unknown, don't count

                    double originTwr = ThrustPhysics.DryTwr(PrefabCatalogue.ThrustBands(kv.Key), dryMassKg, g, air, inAtmo,
                                                            ShipPool.LiftBandFor(PrefabCatalogue, kv.Key));
                    if (originTwr >= minDryTwr) continue;

                    result.StrandedUnits += available;
                    strandedPrefabIds.Add(kv.Key);
                    nodeHasStranded = true;
                    if (result.Details.Count < maxDetails)
                        result.Details.Add("node=" + n.Id + " prefab=" + kv.Key + " count=" + available
                            + " originTwr=" + originTwr.ToString("0.00") + " g=" + g.ToString("0.00")
                            + (inAtmo ? " atmo" : ""));
                }

                if (nodeHasStranded) result.NodesWithStrandedStock++;
            }

            result.DistinctStrandedPrefabs = strandedPrefabIds.Count;
            return result;
        }

        private bool HasNodeWithin(Vec3 pos, NodeZone zone, double radiusKm)
        {
            string neighborId;
            double actualKm, owedKm;
            return TryGetNearestNodeWithin(null, pos, zone, radiusKm, out neighborId, out actualKm, out owedKm);
        }

        /// <summary>The separation a node at a position in <paramref name="zone"/>, held to
        /// <paramref name="requiredKm"/> there, owes <paramref name="neighbor"/>.
        ///
        /// <para>Between an Orbit node and a Surface node it is the smaller of the two places'
        /// separations; every other pair owes the full <paramref name="requiredKm"/>. A planet's orbit
        /// band starts where its gravity ends, 18.8 km above Tellus's ground and 4.9 km above Kor's, so
        /// the band's own 30 km held against the ground would leave its inner part unusable over any
        /// surface node (operator, 2026-11-28: a surface neighbour at its own separation). FAC-537.</para></summary>
        private double OwedSeparationKm(NodeZone zone, double requiredKm, SpatialNode neighbor)
        {
            bool crossesGround = (zone == NodeZone.Orbit && neighbor.Zone == NodeZone.Surface)
                                 || (zone == NodeZone.Surface && neighbor.Zone == NodeZone.Orbit);
            if (!crossesGround) return requiredKm;
            double theirs = SeparationAtKm(neighbor.Position);
            return theirs > 0 && theirs < requiredKm ? theirs : requiredKm;
        }

        private List<SpatialNode> NodesWithinRadius(Vec3 pos, double radiusKm, string excludeNodeId)
        {
            var result = new List<SpatialNode>();
            double r2 = radiusKm * radiusKm;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.Id == excludeNodeId) continue;
                double dx = n.Position.X - pos.X;
                double dy = n.Position.Y - pos.Y;
                double dz = n.Position.Z - pos.Z;
                if (dx * dx + dy * dy + dz * dz <= r2) result.Add(n);
            }
            return result;
        }

        private double NearestNodeDistance(Vec3 pos)
        {
            double bestDistanceSq = double.MaxValue;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                double dx = n.Position.X - pos.X;
                double dy = n.Position.Y - pos.Y;
                double dz = n.Position.Z - pos.Z;
                double distSq = dx * dx + dy * dy + dz * dz;
                if (distSq < bestDistanceSq)
                    bestDistanceSq = distSq;
            }

            if (bestDistanceSq == double.MaxValue)
                return double.MaxValue;

            return Math.Sqrt(bestDistanceSq);
        }

        private bool TryCreateNodeSeparationViolation(string nodeId, Vec3 pos, NodeZone zone, double requiredMinSepKm, out NodeSeparationViolation violation)
        {
            violation = default(NodeSeparationViolation);

            string offendingNeighborId;
            double actualDistanceKm, owedKm;
            if (!TryGetNearestNodeWithin(nodeId, pos, zone, requiredMinSepKm,
                                         out offendingNeighborId, out actualDistanceKm, out owedKm))
                return false;

            violation = new NodeSeparationViolation
            {
                NewNodeId = nodeId,
                Zone = zone,
                OffendingNeighborId = offendingNeighborId,
                ActualDistanceKm = actualDistanceKm,
                RequiredMinSepKm = owedKm,
            };
            return true;
        }

        /// <summary>The nearest node closer to <paramref name="pos"/> than the separation it is owed
        /// (<see cref="OwedSeparationKm"/>, at most <paramref name="radiusKm"/>), skipping
        /// <paramref name="excludeNodeId"/>.</summary>
        private bool TryGetNearestNodeWithin(string excludeNodeId, Vec3 pos, NodeZone zone, double radiusKm,
                                             out string neighborId, out double actualDistanceKm, out double owedKm)
        {
            neighborId = null;
            actualDistanceKm = 0;
            owedKm = radiusKm;

            double r2 = radiusKm * radiusKm;
            double bestDistanceSq = double.MaxValue;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.Id == excludeNodeId) continue;

                double dx = n.Position.X - pos.X;
                double dy = n.Position.Y - pos.Y;
                double dz = n.Position.Z - pos.Z;
                double distSq = dx * dx + dy * dy + dz * dz;
                if (distSq >= r2 || distSq >= bestDistanceSq) continue;

                double owed = OwedSeparationKm(zone, radiusKm, n);
                if (distSq >= owed * owed) continue;

                bestDistanceSq = distSq;
                neighborId = n.Id;
                owedKm = owed;
            }

            if (neighborId == null) return false;
            actualDistanceKm = Math.Sqrt(bestDistanceSq);
            return true;
        }

        /// <summary>Void by contract: this is the <see cref="IFactionActions"/> shape.
        /// <see cref="TryClaim"/> is the form that reports whether the node changed hands.</summary>
        public void Claim(string factionTag, string nodeId) { TryClaim(factionTag, nodeId, false); }

        /// <summary>Takes a claimable node — unowned, or the graveyard faction's — and reports
        /// whether it actually changed hands.
        ///
        /// <para>Refused while another faction's live siege holds the node
        /// (<see cref="NeutralFaction.IsClaimableBy"/>): decay hands a ground-down node to UNKN at
        /// the moment the siege succeeds, and a free Claim would otherwise beat the besieger's
        /// conquest fleet to its own prize.</para>
        ///
        /// <para><paramref name="byPresence"/> is the player-facing claim, taken by a player
        /// standing on the node after the offer's own dwell. Standing on the ground is the strongest
        /// contest there is, so it answers the reservation rather than waiting it out.</para></summary>
        public bool TryClaim(string factionTag, string nodeId, bool byPresence)
        {
            SpatialNode n;
            if (nodeId == null || !Graph.Nodes.TryGetValue(nodeId, out n)) return false;
            if (byPresence ? !NeutralFaction.IsClaimable(n.OwnerFactionTag)
                           : !NeutralFaction.IsClaimableBy(n, factionTag, UtcNow)) return false;

            n.OwnerFactionTag = factionTag;
            n.HoldStrength = 0.45;
            n.LastReinforcedUtc = UtcNow;
            n.ShipyardPaused = false;   // a pause is the previous owner's decision, not the node's
            ShipyardManager.DropRefitsUnusableBy(this, n, factionTag);
            // A siege is against an owner; the one that was running ends with the change of hands.
            PlayerSiege.Reset(n);
            return true;
        }

        public void Reinforce(string factionTag, string nodeId)
        {
            SpatialNode n;
            if (Graph.Nodes.TryGetValue(nodeId, out n))
            {
                n.HoldStrength = Clamp01(n.HoldStrength + Tuning.ReinforceStep);
                n.LastReinforcedUtc = UtcNow;
            }
        }

        /// <summary>The fleet-borne reinforce: the step scaled by how much of the mission the convoy
        /// actually completed. <paramref name="ratio"/> is the resolving fleet's own completion
        /// ratio, so one mauled on the way in delivers proportionally less and one destroyed
        /// delivers nothing at all.
        /// <para>A zero delivery writes nothing -- not the hold, and not
        /// <see cref="SpatialNode.LastReinforcedUtc"/>, whose stamp buys
        /// <see cref="FactionTuning.ReinforceGracePeriod"/> of decay suppression that a failed
        /// mission has not earned. The two-argument overload beside this one is the player
        /// presence-reinforce channel and delivers a full step; it is unchanged.</para></summary>
        public void Reinforce(string factionTag, string nodeId, double ratio)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n)) return;
            double delivered = Tuning.ReinforceStep * Clamp01(ratio);
            if (delivered <= 0) return;
            n.HoldStrength = Clamp01(n.HoldStrength + delivered);
            n.LastReinforcedUtc = UtcNow;
        }

        /// <summary>Install a capability on a held node, or raise its level if already installed.
        /// Callers (brain-driven <c>InvestmentTargeting</c> and player-driven
        /// <see cref="DevelopStationValidator"/>) are responsible for gating the level against
        /// <see cref="FactionTuning.MaxLevelFor"/> before calling this — it does not clamp itself.</summary>
        public void Develop(string factionTag, string nodeId, NodeCapabilityKind capability)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag != factionTag) return;
            int cur;
            n.Capabilities.TryGetValue(capability, out cur);
            n.Capabilities[capability] = cur + 1;
        }

        /// <summary>Attack a RIVAL's source: knock down its hold and spike its threat, and record the
        /// incident so the victim's standing toward us sours. A weak enough ENGINE source can be destroyed
        /// outright (DecayInfluence removes hold &lt;= 0); a scan-backed real asset is only BESIEGED (hold→~0,
        /// no yield/influence) and persists until a re-scan reconciles it. No-op on our own / unowned nodes.
        /// <paramref name="ratio"/> scales damage (1.0 = full, &lt;1 = partial — used by fleet resolution).</summary>
        /// <summary>Void by contract: this is the <see cref="IFactionActions"/> shape. The
        /// victim-returning form is the three-argument overload below.</summary>
        public void Raid(string factionTag, string nodeId) { Raid(factionTag, nodeId, 1.0); }

        /// <summary>Returns the faction the node was taken from, or null when the raid was a no-op
        /// (unknown node, own node, neutral node). Callers that record the raid as a
        /// <see cref="FactionEvent"/> use this rather than re-deriving the victim: the guard above is
        /// the single source of who counts as one.</summary>
        public string Raid(string factionTag, string nodeId, double ratio)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag == null
                || n.OwnerFactionTag == factionTag || n.OwnerFactionTag == NeutralFaction.Tag) return null;
            string victim = n.OwnerFactionTag;
            n.HoldStrength = Clamp01(n.HoldStrength - Tuning.RaidHoldStrip(ratio));
            AddThreat(n, Tuning.RaidThreat * ratio);
            KnockDownSafeZone(n, ratio, factionTag);
            MarkContested(n, ratio, factionTag);
            _incidents.Add(new RaidIncident { Attacker = factionTag, Victim = victim, NodeId = nodeId, Kind = ActionKind.Raid });
            return victim;
        }

        /// <summary>Files the incident for an attack that was destroyed before it landed, so a
        /// defence that succeeds still costs the attacker the victim's standing.
        ///
        /// <para><see cref="Raid"/> and <see cref="Conquer"/> are the only other writers of
        /// <see cref="Incidents"/>, and both need the mission to reach its target. An intercepted
        /// fleet never gets there, so without this the victim of a repelled attack has no
        /// diplomatic memory of having been attacked at all.</para>
        ///
        /// <para><paramref name="missionKind"/> is the attacking mission's own kind; only the
        /// Raid/Conquer distinction survives into the incident, because that is what the drain
        /// selects the relation and fatigue magnitudes by — Retaliate files as a Raid.</para>
        ///
        /// <para>No-op on the same guards <see cref="Raid"/> applies: an absent victim, ourselves,
        /// or the neutral holding faction. Provenance: FAC-275 slice 1.</para></summary>
        public void RecordRepelledAttack(string attackerTag, string victimTag, string nodeId, ActionKind missionKind)
        {
            if (attackerTag == null || victimTag == null
                || victimTag == attackerTag || victimTag == NeutralFaction.Tag) return;
            _incidents.Add(new RaidIncident
            {
                Attacker = attackerTag,
                Victim = victimTag,
                NodeId = nodeId,
                Kind = missionKind == ActionKind.Conquer ? ActionKind.Conquer : ActionKind.Raid,
                Repelled = true,
            });
        }

        /// <summary>Takes a node's station safe zone down for
        /// <see cref="FactionTuning.RaidSafeZoneDownSeconds"/>. A raid that lands is otherwise
        /// invisible in-world — the hold number moves and nothing a player can see changes — so this
        /// is the consequence made physical: the station is shootable and grindable until it returns.
        ///
        /// <para>Any raid that reaches resolution drops it for the full window regardless of
        /// <paramref name="ratio"/>. A raid destroyed on the way in never gets here (it is
        /// <c>Intercepted</c> and skipped), and one that arrives at all got through — scaling the
        /// downtime by how badly it was mauled would make a heavily-defended station recover fastest
        /// from the attacks it only just survived, which is backwards.</para>
        ///
        /// <para>Extends rather than replaces: a second raid inside an existing window pushes the
        /// deadline out, and never pulls it in.</para>
        ///
        /// <para>Core owns the clock; the host owns the SE entity. Setting this is the whole of the
        /// Core side, and it is persisted because the physical zone is re-created from scratch on
        /// every materialisation.</para></summary>
        /// <summary>Marks <paramref name="n"/> contested for
        /// <see cref="FactionTuning.ContestedHoldGainSuppressedSeconds"/>, which withholds its
        /// traffic-driven hold gain without touching its decay. Called at every landed attack, beside
        /// <see cref="KnockDownSafeZone"/> and deliberately not inside it: the two answer different
        /// questions, carry their own tuning and have their own kill switch.
        ///
        /// <para>Not called by <see cref="CaptureNode"/>. A node that has just changed hands stamps
        /// <see cref="SpatialNode.LastReinforcedUtc"/> and is inside its grace period, and the new
        /// owner's first minutes are not the previous owner's siege.</para></summary>
        private void MarkContested(SpatialNode n, double ratio, string attackerTag)
        {
            if (Tuning == null || Tuning.ContestedHoldGainSuppressedSeconds <= 0) return;
            if (ratio <= 0) return;
            DateTime until = UtcNow.AddSeconds(Tuning.ContestedHoldGainSuppressedSeconds);
            if (until <= n.ContestedUntilUtc) return;
            n.ContestedUntilUtc = until;
            // The window and its holder move together: whoever last landed an attack is the siege
            // standing on this node, and is who NeutralFaction.IsClaimableBy reserves it for. A
            // player raid carries no tag and leaves it null, which reserves it against every faction.
            n.ContestedByTag = attackerTag;
        }

        // byTag is whose raid this is; it moves with the deadline, so the window always names the
        // raid that set its current end (see SpatialNode.SafeZoneDownByTag).
        private void KnockDownSafeZone(SpatialNode n, double ratio, string byTag)
        {
            if (Tuning == null || Tuning.RaidSafeZoneDownSeconds <= 0) return;
            if (ratio <= 0) return;
            DateTime until = UtcNow.AddSeconds(Tuning.RaidSafeZoneDownSeconds);
            if (until <= n.SafeZoneDownUntilUtc) return;
            n.SafeZoneDownUntilUtc = until;
            n.SafeZoneDownByTag = byTag;
        }

        /// <summary>A player did damage to <paramref name="nodeId"/>'s station. Raises the node's
        /// <see cref="SpatialNode.Threat"/> and stamps <see cref="SpatialNode.LastPlayerAggressionUtc"/>
        /// so a defender can tell "someone is shooting at us right now" from "something happened here
        /// a while ago" — <c>Threat</c> alone cannot, because a raid resolution raises it too and it
        /// decays rather than expiring.
        ///
        /// <para>Called from the host's damage hook, which accumulates raw SE damage across a tick
        /// and flushes once, so <paramref name="damage"/> is a tick's worth rather than one shot.
        /// Returns false when the node is unknown or the damage is below
        /// <see cref="FactionTuning.PlayerAggressionMinDamage"/> — a collision scrape and a stray
        /// round are not an attack.</para>
        ///
        /// <para>Deliberately takes no attacker identity. Nothing in Core models a player as an
        /// actor, and inventing a half-modelled one here to carry a value no consumer reads would be
        /// worse than the gap. Per-player reputation is the host's, through SE's own faction
        /// reputation API.</para></summary>
        public bool RegisterPlayerAggression(string nodeId, double damage)
        {
            if (nodeId == null || Tuning == null) return false;
            if (damage < Tuning.PlayerAggressionMinDamage) return false;
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag == null
                || n.OwnerFactionTag == NeutralFaction.Tag) return false;

            AddThreat(n, Tuning.PlayerAggressionThreatPerFlush);
            n.LastPlayerAggressionUtc = UtcNow;
            _lastPlayerAggressionAnyUtc = UtcNow;
            RecordEvent(FactionEventKind.PlayerAggression, null, n.OwnerFactionTag, nodeId);
            return true;
        }

        // Most recent player aggression against ANY node. Derived, so it is not persisted: a
        // restart re-learns it from the next round fired, and the only cost of starting empty is
        // that the defensive sweep below stays skipped until then.
        private DateTime _lastPlayerAggressionAnyUtc;

        // Nodes whose station is calling for protection (StationCall), each with the fleet id of the
        // attacker the host could name ("" when none), and nodes whose station cannot call. Set and
        // cleared by the host; not persisted -- a restart ends every call.
        private readonly Dictionary<string, string> _stationCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _silencedStations = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The station at <paramref name="nodeId"/> is calling for protection, against the
        /// fleet <paramref name="attackerFleetId"/> when the host could name it. <c>FleetManager</c>'s
        /// reactive Guard dispatch answers a calling node as it answers one under attack.</summary>
        public void SetStationCall(string nodeId, string attackerFleetId)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            _stationCalls[nodeId] = attackerFleetId ?? "";
        }

        public void ClearStationCall(string nodeId)
        {
            if (nodeId != null) _stationCalls.Remove(nodeId);
        }

        /// <summary>Whether any station is calling: the O(1) gate for the defensive sweep.</summary>
        public bool AnyStationCall { get { return _stationCalls.Count > 0; } }

        /// <summary>Whether the station at <paramref name="nodeId"/> is calling, and the attacker's
        /// fleet id when one was named (null otherwise).</summary>
        public bool IsStationCalling(string nodeId, out string attackerFleetId)
        {
            attackerFleetId = null;
            string id;
            if (nodeId == null || !_stationCalls.TryGetValue(nodeId, out id)) return false;
            attackerFleetId = id.Length > 0 ? id : null;
            return true;
        }

        // Nodes a detected enemy ship is trespassing at (TrespassPolicy.IsIntruder), each with the
        // last time the host saw it there. Set by the host on every pass the ship is seen; not
        // persisted -- a restart forgets every intruder, and the next pass re-marks one still there.
        private readonly Dictionary<string, DateTime> _intruders = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private DateTime _lastIntruderAnyUtc;
        // When each node last took an intruder's threat charge, kept past the mark's own expiry: it
        // is what stops a pilot crossing a border back and forth from charging it on every return.
        private readonly Dictionary<string, DateTime> _intruderThreatUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // When an intruder at each node was last seen at TrespassRung.Intercept. The node's rung is
        // the highest any intruder there has reached within the memory window.
        private readonly Dictionary<string, DateTime> _intruderInterceptUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // The intruder's grid a shadowing Guard follows at each node, with when it was last seen.
        // Kept until it goes unseen for IntruderGridHold, so two pilots at one node do not swap the
        // Guard between them every pass.
        private readonly Dictionary<string, KeyValuePair<long, DateTime>> _intruderGrid =
            new Dictionary<string, KeyValuePair<long, DateTime>>(StringComparer.Ordinal);
        private static readonly TimeSpan IntruderGridHold = TimeSpan.FromSeconds(12);

        /// <summary>Whether the arrivals pass treats enemy NPC fleets in a faction's space as intruders
        /// (<see cref="FleetTrespass"/>). Set by the host from its runtime switch before each pass; a
        /// fixture sets it directly. Off by default.</summary>
        public bool FleetTrespassOn;

        /// <summary>The faction roster route pricing reads relations and personalities from
        /// (<see cref="TransitRisk"/>). Set by the host beside <see cref="FleetTrespassOn"/>; null leaves
        /// every route unpriced. Not persisted.</summary>
        public IList<FactionState> TransitStates;

        /// <summary>What recent dispatches found their lanes cost, for re-picking a source
        /// (<see cref="TransitLedger"/>). Not persisted; it refills from the next dispatches.</summary>
        public TransitLedger TransitLedger { get; } = new TransitLedger();

        /// <summary>How long each fleet has been seen in each enemy's space: the ladder's clock for
        /// fleets, as the host's own book is for pilots. Not persisted; a restart starts every stay
        /// again.</summary>
        public TrespassDwellBook FleetTrespassDwell { get; } = new TrespassDwellBook();

        /// <summary>A detected enemy ship is at <paramref name="nodeId"/>, an owned node. Re-marking
        /// while the mark is live only refreshes it. A fresh mark also raises the node's threat by
        /// <see cref="TrespassPolicy.IntruderThreat"/> unless the node was charged within
        /// <see cref="TrespassPolicy.IntruderThreatInterval"/>. True when the mark is fresh.</summary>
        public bool MarkIntruder(string nodeId)
        {
            return MarkIntruder(nodeId, TrespassRung.Shadow);
        }

        /// <summary><see cref="MarkIntruder(string)"/>, for an intruder that has reached
        /// <paramref name="rung"/> on the faction's ladder (<see cref="TrespassLadder"/>).</summary>
        public bool MarkIntruder(string nodeId, TrespassRung rung)
        {
            return MarkIntruder(nodeId, rung, 0);
        }

        /// <summary><see cref="MarkIntruder(string, TrespassRung)"/>, naming the intruder's grid
        /// (<paramref name="gridEntityId"/>, 0 when unknown) for a Guard to shadow.</summary>
        public bool MarkIntruder(string nodeId, TrespassRung rung, long gridEntityId)
        {
            SpatialNode n;
            if (nodeId == null || !Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag == null
                || n.OwnerFactionTag == NeutralFaction.Tag) return false;
            bool fresh = !IsIntruded(nodeId);
            _intruders[nodeId] = UtcNow;
            _lastIntruderAnyUtc = UtcNow;
            if (rung >= TrespassRung.Intercept) _intruderInterceptUtc[nodeId] = UtcNow;
            if (gridEntityId != 0)
            {
                KeyValuePair<long, DateTime> held;
                if (!_intruderGrid.TryGetValue(nodeId, out held) || held.Key == gridEntityId
                    || UtcNow - held.Value > IntruderGridHold)
                    _intruderGrid[nodeId] = new KeyValuePair<long, DateTime>(gridEntityId, UtcNow);
            }
            if (!fresh) return false;
            DateTime charged;
            if (!_intruderThreatUtc.TryGetValue(nodeId, out charged)
                || UtcNow - charged >= TrespassPolicy.IntruderThreatInterval)
            {
                AddThreat(n, TrespassPolicy.IntruderThreat);
                _intruderThreatUtc[nodeId] = UtcNow;
            }
            return true;
        }

        /// <summary>Whether a detected enemy ship was seen at <paramref name="nodeId"/> within
        /// <see cref="TrespassPolicy.IntruderMemory"/>.</summary>
        public bool IsIntruded(string nodeId)
        {
            DateTime seen;
            if (nodeId == null || _intruders.Count == 0 || !_intruders.TryGetValue(nodeId, out seen)) return false;
            return UtcNow - seen <= TrespassPolicy.IntruderMemory;
        }

        /// <summary>The highest rung an intruder at <paramref name="nodeId"/> has reached within
        /// <see cref="TrespassPolicy.IntruderMemory"/>; <see cref="TrespassRung.None"/> when the
        /// node is not intruded.</summary>
        public TrespassRung IntruderRungAt(string nodeId)
        {
            if (!IsIntruded(nodeId)) return TrespassRung.None;
            DateTime at;
            return _intruderInterceptUtc.TryGetValue(nodeId, out at) && UtcNow - at <= TrespassPolicy.IntruderMemory
                ? TrespassRung.Intercept
                : TrespassRung.Shadow;
        }

        /// <summary>The intruder's grid a Guard at <paramref name="nodeId"/> shadows: the one it
        /// already follows while that is still seen, else the latest marked. 0 when none.</summary>
        public long IntruderGridAt(string nodeId)
        {
            if (!IsIntruded(nodeId)) return 0;
            KeyValuePair<long, DateTime> held;
            return _intruderGrid.TryGetValue(nodeId, out held) && UtcNow - held.Value <= TrespassPolicy.IntruderMemory
                ? held.Key
                : 0;
        }

        /// <summary>The O(1) gate for the defensive sweep: some node was marked within
        /// <see cref="TrespassPolicy.IntruderMemory"/>. Drops expired marks when none is live.</summary>
        public bool AnyIntruder
        {
            get
            {
                if (_intruders.Count == 0) return false;
                if (UtcNow - _lastIntruderAnyUtc <= TrespassPolicy.IntruderMemory) return true;
                _intruders.Clear();
                _intruderInterceptUtc.Clear();
                _intruderGrid.Clear();
                return false;
            }
        }

        /// <summary>Replaces the set of nodes whose station cannot call
        /// (<see cref="StationCallRules.Silenced"/>): no reactive Guard is sent to them.</summary>
        public void SetSilencedStations(IEnumerable<string> nodeIds)
        {
            _silencedStations.Clear();
            if (nodeIds == null) return;
            foreach (string id in nodeIds)
                if (!string.IsNullOrEmpty(id)) _silencedStations.Add(id);
        }

        public bool IsStationSilenced(string nodeId)
        {
            return nodeId != null && _silencedStations.Count > 0 && _silencedStations.Contains(nodeId);
        }

        /// <summary>True when a player has damaged some node recently enough for
        /// <see cref="IsUnderPlayerAttack"/> to be worth asking per node.
        ///
        /// <para>One comparison, so the per-faction defensive sweep can be entered on a player
        /// attack without walking every faction's estate on every tick to discover there is not
        /// one.</para></summary>
        public bool AnyPlayerAttackActive()
        {
            if (Tuning == null || _lastPlayerAggressionAnyUtc == default(DateTime)) return false;
            return (UtcNow - _lastPlayerAggressionAnyUtc).TotalSeconds
                   <= Tuning.PlayerAggressionResponseWindowSeconds;
        }

        /// <summary>A player raid succeeded against <paramref name="nodeId"/>. Applies the same
        /// effect an NPC raid applies when its dwell resolves, so a node taken apart by a player and
        /// one taken apart by a faction end up in the same state rather than in two parallel ones.
        ///
        /// <para>Called once per success, and a single raid produces many: the station path fires
        /// one per <see cref="FactionTuning.PlayerRaidBlockDamageForSuccess"/> of damage
        /// (<see cref="PlayerSiege.RecordRaidDamage"/>), the presence path one per
        /// <see cref="FactionTuning.RaidZoneSiegeDwellSeconds"/> held
        /// (<see cref="MarkPlayerPresenceAttack"/>).</para>
        ///
        /// <para>No loot: interim raid rewards are a later slice, deliberately. Returns the victim
        /// faction, or null when the node is unknown or unowned.</para></summary>
        public string ResolvePlayerRaid(string nodeId) { return ResolvePlayerRaid(nodeId, null); }

        /// <summary><paramref name="attackerFactionTag"/> is the attacking player's SE faction tag,
        /// or null when they have none or it is not known. It attributes the safe-zone window this
        /// success extends (<see cref="SpatialNode.SafeZoneDownByTag"/>), so the attacker's faction
        /// can go on counting fire at the station through that window
        /// (<see cref="PlayerSiege.MayAddRaidDamage"/>). The contest window stays untagged: a
        /// player's siege reserves a derelict against every faction.
        ///
        /// <para>It is also the actor of the <see cref="FactionEventKind.RaidResolved"/> this records.
        /// <see cref="RecordEvent"/> files nothing without an actor, so a raid by a player with no
        /// faction leaves no event.</para></summary>
        public string ResolvePlayerRaid(string nodeId, string attackerFactionTag)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag == null
                || n.OwnerFactionTag == NeutralFaction.Tag) return null;

            string victim = n.OwnerFactionTag;
            n.HoldStrength = Clamp01(n.HoldStrength - Tuning.RaidHoldStrip(1.0));
            AddThreat(n, Tuning.RaidThreat);
            // The zone stays down for the ordinary post-raid window on top of whatever the breach is
            // holding open, so it does not snap back the instant the attacker steps away.
            KnockDownSafeZone(n, 1.0, attackerFactionTag);
            MarkContested(n, 1.0, null);
            RecordEvent(FactionEventKind.RaidResolved, attackerFactionTag, victim, nodeId);
            return victim;
        }

        /// <summary>A player is standing on <paramref name="nodeId"/> and there is no station there
        /// to attack, so the presence itself is the raid: enters phase 2 through
        /// <see cref="PlayerSiege.BeginPresenceRaid"/> and then applies exactly what a successful
        /// station raid applies, so ground taken this way and ground taken by force end up in one
        /// state rather than two parallel ones.
        ///
        /// <para>Returns false when the node is unknown, unowned, founded, or already under a raid.
        /// Whether the player is entitled to attack this owner is the host's question -- Core models
        /// no player and holds no standings.</para></summary>
        public bool BeginPresenceRaid(string nodeId) { return BeginPresenceRaid(nodeId, null); }

        /// <summary><paramref name="attackerFactionTag"/> is the occupier's SE faction tag, or null:
        /// the actor of every strip's <see cref="FactionEventKind.RaidResolved"/>
        /// (<see cref="ResolvePlayerRaid(string, string)"/>).</summary>
        public bool BeginPresenceRaid(string nodeId, string attackerFactionTag)
        {
            SpatialNode n;
            if (Tuning == null || nodeId == null || !Graph.Nodes.TryGetValue(nodeId, out n)) return false;
            if (n.OwnerFactionTag == null || n.OwnerFactionTag == NeutralFaction.Tag) return false;
            if (!PlayerSiege.BeginPresenceRaid(n, Tuning, UtcNow)) return false;

            ResolvePlayerRaid(nodeId, attackerFactionTag);
            n.PresenceStrips = 1;
            MarkPlayerPresenceAttack(nodeId, attackerFactionTag);
            return true;
        }

        /// <summary>Stamps the "a player is attacking this right now" clocks that
        /// <see cref="IsUnderPlayerAttack"/> and <see cref="AnyPlayerAttackActive"/> read, without
        /// touching <see cref="SpatialNode.Threat"/> and without recording an event.
        ///
        /// <para>A presence raid has no damage flush to carry that stamp the way
        /// <see cref="RegisterPlayerAggression"/> does, and it is re-stamped on every tick the
        /// attacker holds the node -- so raising threat on the stamp itself would ratchet it to full
        /// over one hold. Threat is raised by the periodic strip below instead, at the rate a raid
        /// raises it.</para>
        ///
        /// <para><b>Time on station is the presence raid's damage quantum.</b> There is nothing to
        /// shoot, so what an attacker spends is the hold itself: one further
        /// <see cref="ResolvePlayerRaid"/> per <see cref="FactionTuning.RaidZoneSiegeDwellSeconds"/>
        /// of unbroken presence, charged at the start of each interval, with the raid's own opening
        /// strip paying for the first. That gives the presence path the same shape the station path
        /// has -- sustained force, sustained hold loss -- rather than one strip and a stall.</para>
        /// </summary>
        public bool MarkPlayerPresenceAttack(string nodeId) { return MarkPlayerPresenceAttack(nodeId, null); }

        /// <summary><paramref name="attackerFactionTag"/> is the occupier's SE faction tag, or null:
        /// the actor of a strip's <see cref="FactionEventKind.RaidResolved"/>. Each strip counts in
        /// <see cref="SpatialNode.PresenceStrips"/>.</summary>
        public bool MarkPlayerPresenceAttack(string nodeId, string attackerFactionTag)
        {
            SpatialNode n;
            if (nodeId == null || !Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag == null
                || n.OwnerFactionTag == NeutralFaction.Tag) return false;

            n.LastPlayerAggressionUtc = UtcNow;
            _lastPlayerAggressionAnyUtc = UtcNow;

            if (Tuning == null || !PlayerSiege.IsPresenceRaid(n)) return true;
            if (n.PlayerPresenceStripUtc == default(DateTime)) { n.PlayerPresenceStripUtc = UtcNow; return true; }
            if (Tuning.RaidZoneSiegeDwellSeconds <= 0) return true;
            if ((UtcNow - n.PlayerPresenceStripUtc).TotalSeconds < Tuning.RaidZoneSiegeDwellSeconds) return true;

            n.PlayerPresenceStripUtc = UtcNow;
            ResolvePlayerRaid(nodeId, attackerFactionTag);
            n.PresenceStrips++;
            return true;
        }

        /// <summary>True when a player has damaged this node within
        /// <see cref="FactionTuning.PlayerAggressionResponseWindowSeconds"/>.</summary>
        public bool IsUnderPlayerAttack(SpatialNode n)
        {
            if (n == null || Tuning == null || n.LastPlayerAggressionUtc == default(DateTime)) return false;
            return (UtcNow - n.LastPlayerAggressionUtc).TotalSeconds
                   <= Tuning.PlayerAggressionResponseWindowSeconds;
        }

        /// <summary>Attempt to seize a RIVAL's already-weakened node outright. Unlike <see cref="Raid"/>,
        /// which only damages hold/threat, Conquer flips <see cref="SpatialNode.OwnerFactionTag"/> to the
        /// attacker when the node's HoldStrength is at or below <see cref="FactionTuning.ConquerHoldCeiling"/>
        /// at resolution time — i.e. Conquer is the finishing move after Raid/Retaliate softening, not a
        /// first strike. Below the ceiling: ownership flips; Capabilities/Inventory/ShipStock/BuildQueue are
        /// left untouched and carry over to the new owner (a captured node keeps what it had — the point of
        /// conquest is to inherit developed infrastructure, unlike Claim's fresh-node reset); HoldStrength
        /// resets to <see cref="FactionTuning.ConquerCapturedHoldStrength"/> and Threat to
        /// <see cref="FactionTuning.ConquerCapturedThreat"/> (a freshly-seized asset is contested, not
        /// secure). Above the ceiling: Conquer degrades to Raid-equivalent softening (same hold/threat
        /// damage as <see cref="Raid"/>, scaled by <paramref name="ratio"/>) rather than a no-op — an
        /// attempt against a not-yet-weak-enough target still helps break it down for next time. Either
        /// outcome records a <see cref="RaidIncident"/> tagged <see cref="ActionKind.Conquer"/> so the
        /// victim's standing sours harder than a mere raid. A node held by <see cref="NeutralFaction"/> is
        /// the one case that skips all of this and transfers outright — see the neutral branch in the
        /// body. No-op on our own / unowned nodes, mirroring <see cref="Raid"/>.</summary>
        /// <summary>Transfers <paramref name="nodeId"/> to <paramref name="factionTag"/> outright and
        /// returns the faction it was taken from, or null when nothing changed (unknown node, already
        /// theirs, or unowned).
        ///
        /// <para>The transfer itself, with no test of its own: the caller states the precondition.
        /// <see cref="Conquer"/> gates on <see cref="FactionTuning.ConquerHoldCeiling"/> before
        /// calling this, because a fleet action earns a node by grinding its hold down; a player
        /// siege earns it by breaching the zone and holding the wreck, which leaves hold untouched.
        /// One ceiling cannot express both, so the ceiling belongs to the caller that needs it.</para>
        ///
        /// <para>Returning the previous owner rather than void is the point: an ownership change that
        /// silently declines cannot be logged or reported as one.</para></summary>
        public string CaptureNode(string factionTag, string nodeId)
        {
            SpatialNode n;
            if (factionTag == null || nodeId == null
                || !Graph.Nodes.TryGetValue(nodeId, out n)
                || n.OwnerFactionTag == null
                || n.OwnerFactionTag == factionTag) return null;

            string victim = n.OwnerFactionTag;
            n.OwnerFactionTag = factionTag;
            n.HoldStrength = Tuning.ConquerCapturedHoldStrength;
            n.Threat = Tuning.ConquerCapturedThreat;
            n.LastReinforcedUtc = UtcNow;
            n.ShipyardPaused = false;   // a pause is the previous owner's decision, not the node's
            // A station rebuild aimed at a design the taker may not build does not travel with the
            // node -- see ShipyardManager.DropRefitsUnusableBy. Same call in Claim, for a node that
            // reached its new owner by decaying to neutral and being picked up instead.
            ShipyardManager.DropRefitsUnusableBy(this, n, factionTag);
            // A siege is against an owner; the one that was running ends with the change of hands.
            PlayerSiege.Reset(n);
            // The new owner inherits the downed zone rather than a fresh one: the station was just
            // taken by force and the protection is part of what was broken taking it.
            KnockDownSafeZone(n, 1.0, factionTag);
            return victim;
        }

        /// <summary>Gives <paramref name="nodeId"/> to <paramref name="factionTag"/> on an operator's
        /// instruction and returns who held it, or null when nothing changed. The transfer of
        /// <see cref="CaptureNode"/> without what makes that a conquest: the node arrives at full
        /// hold, unthreatened, uncontested and with its zone up, as ground its owner never lost.
        ///
        /// <para>The previous holder's parked hulls fly home first
        /// (<see cref="HullRelocation.Evacuate"/>, recorded in <see cref="ShipEvacuations"/>), as on
        /// abandonment: a restore returns the ground, not the fleet someone else parked on it.
        /// Inventory travels with the node, as it does on every change of hands.</para></summary>
        public string RestoreNode(string factionTag, string nodeId, FactionState priorOwner)
        {
            SpatialNode n;
            if (factionTag == null || nodeId == null
                || !Graph.Nodes.TryGetValue(nodeId, out n)
                || n.OwnerFactionTag == null
                || n.OwnerFactionTag == factionTag) return null;

            string prior = n.OwnerFactionTag;
            n.OwnerFactionTag = factionTag;
            if (prior != NeutralFaction.Tag)
                HullRelocation.Evacuate(this, priorOwner, prior, n, _shipEvacuations);
            n.HoldStrength = 1.0;
            n.Threat = 0;
            n.LastReinforcedUtc = UtcNow;
            n.HoldZeroSinceUtc = default(DateTime);
            n.ContestedUntilUtc = default(DateTime);
            n.ContestedByTag = null;
            n.SafeZoneDownUntilUtc = default(DateTime);
            n.SafeZoneDownByTag = null;
            n.AbandonedUtc = default(DateTime);
            n.ShipyardPaused = false;   // a pause is the previous owner's decision, not the node's
            ShipyardManager.DropRefitsUnusableBy(this, n, factionTag);
            PlayerSiege.Reset(n);
            return prior;
        }

        public string Conquer(string factionTag, string nodeId) { return Conquer(factionTag, nodeId, 1.0); }

        /// <summary>Returns the faction the node was taken from — whether or not it actually changed
        /// hands — or null when the attempt was a no-op. See <see cref="Raid"/>.</summary>
        public string Conquer(string factionTag, string nodeId, double ratio)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag == null
                || n.OwnerFactionTag == factionTag) return null;
            string victim = n.OwnerFactionTag;

            // A derelict is taken, not softened. Hold is the owner's garrison and a derelict has no
            // owner, so there is nothing for the strip below to grind and nothing the ceiling can
            // decide; the incident is skipped for the same reason, since NeutralFaction holds no
            // standing to sour. What the convoy had to beat was the abandoned station's own guns,
            // and reaching resolution rather than being intercepted is it having beaten them.
            if (victim == NeutralFaction.Tag)
            {
                CaptureNode(factionTag, nodeId);
                return victim;
            }

            // The attack lands first and the ceiling is tested against what it left behind, so an
            // attacker that arrives strong enough to strip the node past ConquerHoldCeiling takes it
            // on this mission rather than only softening it for a later one. CaptureNode then sets
            // HoldStrength to ConquerCapturedHoldStrength, overwriting the strip.
            n.HoldStrength = Clamp01(n.HoldStrength - Tuning.RaidHoldStrip(ratio));
            AddThreat(n, Tuning.RaidThreat * ratio);
            KnockDownSafeZone(n, ratio, factionTag);
            MarkContested(n, ratio, factionTag);
            if (n.HoldStrength <= Tuning.ConquerHoldCeiling) CaptureNode(factionTag, nodeId);

            _incidents.Add(new RaidIncident { Attacker = factionTag, Victim = victim, NodeId = nodeId, Kind = ActionKind.Conquer });
            return victim;
        }

        /// <summary>Takes the most valuable items from <paramref name="targetNodeId"/>'s inventory
        /// into an abstract manifest, greedily by per-unit value (highest
        /// first), volume-capped by <paramref name="cargoCapacityLiters"/> and scaled by
        /// <paramref name="ratio"/> (partial raid success carries proportionally less home). The
        /// caller owns delivery of that manifest, allowing itinerary fleets to keep it aboard until
        /// their final return stop. <paramref name="itemVolumes"/> null falls back to a flat
        /// per-unit-litre estimate so looting still functions (bounded, if less precise) with no
        /// volume catalogue wired. When <paramref name="itemMasses"/> and <paramref
        /// name="shipPrefabId"/> are both supplied, the volume-capped manifest is additionally
        /// trimmed (lowest-value items first) so the loaded hull still clears
        /// <c>Tuning.MinDryTwr</c> lifting off <paramref name="targetNodeId"/> — see
        /// <see cref="ThrustPhysics.MaxCargoMassKg"/>. <paramref name="massTrimmed"/> reports
        /// whether that trim actually cut anything, for the caller's log line.
        /// <para>A gas key takes none of the hold: it is sized by <paramref name="gasTankRoom"/> (the raiders' tank room for that gas, see
        /// <see cref="GasCarriage.RoomLitres"/>) scaled by <paramref name="ratio"/>, and a null
        /// <paramref name="gasTankRoom"/> loots no gas. A raider with tanks and no hold still loots gas
        /// into its tanks.</para></summary>
        public Dictionary<string, long> TakeRaidLootNodeInventory(string targetNodeId, double ratio,
            long cargoCapacityLiters, IItemPriceCatalogue prices, IItemVolumeCatalogue itemVolumes,
            IItemMassCatalogue itemMasses = null, string shipPrefabId = null,
            Func<string, long> gasTankRoom = null)
        {
            bool massTrimmed;
            return TakeRaidLootNodeInventory(targetNodeId, ratio, cargoCapacityLiters, prices,
                itemVolumes, itemMasses, shipPrefabId, out massTrimmed, gasTankRoom);
        }

        /// <summary>Overload exposing the mass-trim signal via <paramref name="massTrimmed"/> —
        /// see the other overload's remarks for the full contract.</summary>
        public Dictionary<string, long> TakeRaidLootNodeInventory(string targetNodeId, double ratio,
            long cargoCapacityLiters, IItemPriceCatalogue prices, IItemVolumeCatalogue itemVolumes,
            IItemMassCatalogue itemMasses, string shipPrefabId, out bool massTrimmed,
            Func<string, long> gasTankRoom = null)
        {
            massTrimmed = false;
            // Tanks are a second place loot can go, so an empty hold alone does not end the raid's
            // take while there is tank room.
            bool tanksTakeGas = gasTankRoom != null;
            if (ratio <= 0 || targetNodeId == null) return null;
            if (cargoCapacityLiters <= 0 && !tanksTakeGas) return null;
            SpatialNode target;
            if (!Graph.Nodes.TryGetValue(targetNodeId, out target) || target.Inventory.Count == 0) return null;

            long remainingLiters = cargoCapacityLiters > 0 ? (long)(cargoCapacityLiters * ratio) : 0;
            if (remainingLiters <= 0 && !tanksTakeGas) return null;

            // Snapshot + value-sort: highest per-unit credit value first ("most valuable items").
            var items = new List<KeyValuePair<string, long>>(target.Inventory);
            items.Sort((a, b) => UnitValue(b.Key, prices).CompareTo(UnitValue(a.Key, prices)));

            var cargo = new Dictionary<string, long>();
            foreach (var kv in items)
            {
                if (remainingLiters <= 0 && !tanksTakeGas) break;
                if (kv.Value <= 0) continue;

                if (InventoryKeyMapper.IsGasKey(kv.Key))
                {
                    long room = gasTankRoom != null ? (long)(gasTankRoom(kv.Key) * ratio) : 0;
                    long gasTaken = kv.Value < room ? kv.Value : room;
                    if (gasTaken <= 0) continue;
                    AddNodeInventory(targetNodeId, kv.Key, -gasTaken);
                    cargo[kv.Key] = gasTaken;
                    continue;
                }
                if (remainingLiters <= 0) continue;

                double unitVol = LootUnitLiters(kv.Key, itemVolumes);

                long maxByVol = (long)(remainingLiters / unitVol);
                long taken = kv.Value < maxByVol ? kv.Value : maxByVol;
                if (taken <= 0) continue;

                AddNodeInventory(targetNodeId, kv.Key, -taken);
                cargo[kv.Key] = taken;
                remainingLiters -= (long)(taken * unitVol);
            }
            if (cargo.Count == 0) return null;

            if (itemMasses != null && shipPrefabId != null && Tuning.MinDryTwr > 0)
            {
                // OrbitGravityMin is a relative fraction (see ClassifyZone) -- convert to an absolute
                // floor for this same-point/single-planet route via the local surface g. Unlike the
                // other five OrbitGravityMin->GravityFloorG call sites (two-point routes that can span
                // two planets or deep space, with no single "local planet" to scale against), this one
                // targets exactly one node/one planet, so the conversion is unambiguous.
                double localSurfaceG = LocalSurfaceG(target.Position);
                double gravityFloorG = localSurfaceG > 0 ? Tuning.OrbitGravityMin * localSurfaceG : Tuning.OrbitGravityMin;
                ShipPool.RouteEnvironment liftRoute = ShipPool.RouteEnvironment.From(
                    Planets, target.Position, target.Position, Tuning.MinDryTwr, gravityFloorG);
                double maxCargoMassKg = liftRoute.MaxCargoMassKg(PrefabCatalogue, shipPrefabId);
                if (!double.IsPositiveInfinity(maxCargoMassKg))
                {
                    double totalMassKg = 0;
                    foreach (var kv in cargo) totalMassKg += kv.Value * itemMasses.MassKgPerUnit(kv.Key);

                    // Trim lowest-value items first (items is already sorted highest-value-first).
                    for (int i = items.Count - 1; i >= 0 && totalMassKg > maxCargoMassKg; i--)
                    {
                        string key = items[i].Key;
                        long have;
                        if (!cargo.TryGetValue(key, out have) || have <= 0) continue;

                        double unitMassKg = itemMasses.MassKgPerUnit(key);
                        if (unitMassKg <= 0) continue; // no mass data for this item — can't trim it to help

                        long needToRemove = (long)System.Math.Ceiling((totalMassKg - maxCargoMassKg) / unitMassKg);
                        long remove = have < needToRemove ? have : needToRemove;
                        if (remove <= 0) continue;

                        long remaining = have - remove;
                        if (remaining > 0) cargo[key] = remaining; else cargo.Remove(key);
                        AddNodeInventory(targetNodeId, key, remove); // give the excess back to the node
                        totalMassKg -= remove * unitMassKg;
                        massTrimmed = true;
                    }
                }
            }

            return cargo.Count > 0 ? cargo : null;
        }

        /// <summary>Litres of hold one unit of <paramref name="key"/> takes as raid loot. An unknown
        /// volume counts as 1 L/unit rather than being skipped, so looting still functions (bounded,
        /// if less precise) with no volume catalogue wired.</summary>
        public static double LootUnitLiters(string key, IItemVolumeCatalogue itemVolumes)
        {
            double unitVol = itemVolumes != null ? itemVolumes.VolumeLitersPerUnit(key) : 0;
            return unitVol > 0 ? unitVol : 1.0;
        }

        /// <summary>The hold a raid on <paramref name="nodeId"/> needs to carry off everything it could
        /// loot there: the node's non-gas inventory in litres, read with the loot path's own unit
        /// volumes (<see cref="LootUnitLiters"/>). Gas goes to tanks, not the hold, so it is not
        /// counted. What a fully successful raid takes is capped by the fleet's summed cargo, so hold
        /// beyond this figure carries nothing. 0 for an unknown node or an empty one.</summary>
        public double LootableLiters(string nodeId, IItemVolumeCatalogue itemVolumes)
        {
            SpatialNode node;
            if (nodeId == null || !Graph.Nodes.TryGetValue(nodeId, out node)) return 0;
            double litres = 0;
            foreach (var kv in node.Inventory)
            {
                if (kv.Value <= 0 || InventoryKeyMapper.IsGasKey(kv.Key)) continue;
                litres += kv.Value * LootUnitLiters(kv.Key, itemVolumes);
            }
            return litres;
        }

        /// <summary>Legacy single-leg raid-loot helper. New itinerary fleets use
        /// <see cref="TakeRaidLootNodeInventory"/> and deliver the manifest at their return stop.</summary>
        public long RaidLootNodeInventory(string originNodeId, string targetNodeId, double ratio,
            long cargoCapacityLiters, IItemPriceCatalogue prices, IItemVolumeCatalogue itemVolumes,
            IItemMassCatalogue itemMasses = null, string shipPrefabId = null,
            Func<string, long> gasTankRoom = null)
        {
            if (originNodeId == null) return 0;
            bool massTrimmed;
            Dictionary<string, long> cargo = TakeRaidLootNodeInventory(targetNodeId, ratio,
                cargoCapacityLiters, prices, itemVolumes, itemMasses, shipPrefabId, out massTrimmed, gasTankRoom);
            if (cargo == null) return 0;

            long lootValue = 0;
            foreach (var kv in cargo)
            {
                AddNodeInventory(originNodeId, kv.Key, kv.Value);
                lootValue += ItemCredits.For(kv.Key, kv.Value, UnitValue(kv.Key, prices));
            }
            return lootValue;
        }

        private long UnitValue(string subTypeId, IItemPriceCatalogue prices)
        {
            long v = prices != null ? prices.BasePrice(subTypeId) : 0;
            if (v > 0) return v;
            ItemKind ik;
            return TryGetInventoryItemKind(subTypeId, out ik) ? Tuning.FallbackFor(ik) : 1;
        }

        /// <summary>Base credit value of one <b>priced</b> unit of <paramref name="subTypeId"/>
        /// against this world's own price catalogue, falling back to the item kind's tuned value and
        /// then to 1. The same reading the loot and cargo-trim paths take, exposed so a caller
        /// outside this class need not reproduce the fallback chain.
        ///
        /// <para>A priced unit is the inventory unit for everything but gas, which is priced per
        /// kilolitre and stocked in litres — multiply through <see cref="ItemCredits"/>, never
        /// directly.</para></summary>
        public long ItemUnitValue(string subTypeId)
        {
            return string.IsNullOrEmpty(subTypeId) ? 0 : UnitValue(subTypeId, PriceCatalogue);
        }

        /// <summary>Raises <paramref name="n"/>'s <see cref="SpatialNode.Threat"/> by
        /// <paramref name="amount"/>, clamped to 0..1. The single write for "something hostile is
        /// happening here", paired with <see cref="DecayThreat"/>, which is the only thing that takes
        /// it back down on its own -- a caller adding to the field directly gets the level right and
        /// the provenance wrong, and there is now a rate for these to be balanced against
        /// (<see cref="FactionTuning.ThreatDecayPerHour"/>) rather than a ratchet where every raise
        /// was permanent and none of them had to agree about anything.</summary>
        public void AddThreat(SpatialNode n, double amount)
        {
            if (n == null || amount <= 0) return;
            n.Threat = Clamp01(n.Threat + amount);
        }

        /// <summary>Overload for a caller holding an id rather than the node. A node that is not in
        /// the graph is silently skipped, as every other id-keyed mutator here does.</summary>
        public void AddThreat(string nodeId, double amount)
        {
            SpatialNode n;
            if (nodeId != null && Graph.Nodes.TryGetValue(nodeId, out n)) AddThreat(n, amount);
        }

        /// <summary>Ages <see cref="SpatialNode.Threat"/> on every node by <paramref name="amount"/>,
        /// floored at zero. Called once per global decay pass beside
        /// <see cref="DecayInfluence"/> -- <paramref name="amount"/> is that pass's own share of
        /// <see cref="FactionTuning.ThreatDecayPerHour"/>.
        ///
        /// <para>Every node, not only owned ones: an unowned node accrues threat from raids the same
        /// way, and a graveyard node that kept a raid's threat forever would hand a stale flashpoint
        /// to whichever faction claimed it next. Nothing here is suppressed by the reinforce grace
        /// period either, which exists to protect territory a faction is actively holding and says
        /// nothing about whether anyone is shooting at it.</para>
        ///
        /// <para>Returns the number of nodes whose threat actually moved, so a caller can report
        /// that the pass reached something rather than inferring it from an unchanged world.</para></summary>
        public int DecayThreat(double amount)
        {
            if (amount <= 0) return 0;
            int moved = 0;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.Threat <= 0) continue;
                n.Threat = n.Threat > amount ? n.Threat - amount : 0;
                moved++;
            }
            return moved;
        }

        public void Guard(string factionTag, string nodeId)
        {
            SpatialNode n;
            if (Graph.Nodes.TryGetValue(nodeId, out n)) n.Threat = Clamp01(n.Threat - 0.05);
        }

        public void Patrol(string factionTag, string nodeId)
        {
            SpatialNode n;
            if (Graph.Nodes.TryGetValue(nodeId, out n)) n.Threat = Clamp01(n.Threat - 0.05);
        }

        /// <summary>Abandon a held node — hands it to <see cref="NeutralFaction"/> (UNKN) rather than
        /// nulling its owner, so it stays a persistent, claimable graveyard node instead of vanishing
        /// from the graph. See docs/economy-territory.md.
        /// <para>This form names no faction state, so the hulls on the node have no fleet list to fly
        /// on and stay behind (<see cref="HullRelocation.NoFactionState"/>). A caller holding the
        /// state uses <see cref="Withdraw(FactionState, string)"/>.</para></summary>
        public void Withdraw(string factionTag, string nodeId)
        {
            SpatialNode n;
            if (Graph.Nodes.TryGetValue(nodeId, out n) && n.OwnerFactionTag == factionTag)
                AbandonNode(n, null);
        }

        /// <summary><see cref="Withdraw(string, string)"/> for a caller holding the faction's state:
        /// the node's hulls fly off it on <paramref name="owner"/>'s fleet list
        /// (<see cref="HullRelocation.Evacuate"/>).</summary>
        public void Withdraw(FactionState owner, string nodeId)
        {
            SpatialNode n;
            if (owner != null && nodeId != null && Graph.Nodes.TryGetValue(nodeId, out n)
                && n.OwnerFactionTag == owner.Tag)
                AbandonNode(n, owner);
        }

        /// <summary>Hand <paramref name="n"/> to <see cref="NeutralFaction"/>: the one place a node is
        /// abandoned, reached by an explicit <see cref="Withdraw(FactionState, string)"/>, the insolvency
        /// shed and decay to zero hold. Two paths that agree today are two paths that can stop agreeing.
        /// <paramref name="priorOwner"/> is the losing faction's state, whose fleet list the node's
        /// hulls fly off on; null leaves them on the node.
        ///
        /// <para>What stays with the node for its next owner: inventory (the derelict's loot),
        /// capabilities and the build queue. What leaves: every hull, flown
        /// (<see cref="HullRelocation.Evacuate"/>).</para></summary>
        private void AbandonNode(SpatialNode n, FactionState priorOwner)
        {
            string priorOwnerTag = n.OwnerFactionTag;
            n.OwnerFactionTag = NeutralFaction.Tag;
            n.HoldStrength = 0;
            n.AbandonedUtc = UtcNow;
            n.HoldZeroSinceUtc = default(DateTime);
            n.ShipyardPaused = false;   // a pause is the previous owner's decision, not the node's
            // A siege is against an owner, as in TryClaim and Conquer. Left standing, an occupation
            // runs on against the neutral faction and offers to conquer ground that is already
            // anyone's to claim.
            PlayerSiege.Reset(n);

            HullRelocation.Evacuate(this, priorOwner, priorOwnerTag, n, _shipEvacuations);

            // Part of what was learned here is forfeit with the ground. Refreshed straight after,
            // because worth is read from the survey and a stale value would leave an abandoned node
            // still priced on knowledge its owner no longer has.
            if (n.Deposit != null)
            {
                n.Deposit.Forget(Tuning.SurveyRetainedOnAbandonment);
                RefreshNodeContent(n);
            }
            // A re-survey in flight holds what the old owner had learned too; it starts over against
            // the reduced target rather than handing that knowledge back at the swap.
            n.PendingDeposit = null;
        }

        /// <summary>
        /// Two-phase income tick, run to completion in one call.
        ///
        /// Phase 1 — item production per node: each owned node mints items according to its
        /// free-mint yield profile. Founded and scan-backed stations pay base item upkeep plus
        /// installed capability upkeep; unfounded frontier claims pay neither.
        ///
        /// Phase 2 — credit upkeep drain + net-flow cache + insolvency guard. No surplus-to-credits
        /// conversion; items accumulate until consumed by capabilities or trade.
        ///
        /// This is the synchronous contract every offline caller depends on (Factions.Sim,
        /// FactionRuntime.Step, the Core test suite); the live host instead admits the same pass one
        /// bounded unit at a time against its frame budget. Both drive the identical phase pipeline in
        /// WorldModel.IncomePass.cs, the same way FactionRuntime.Flush() wraps its own phase stepper.
        /// An already-in-flight pass is finished rather than restarted -- restarting would re-charge
        /// factions the pass has already charged.
        /// </summary>
        public void AccrueIncome(IList<FactionState> states = null)
        {
            if (_incomePass == null) BeginIncomePass(states);
            while (!AdvanceIncomePass()) { }
        }

        private long NodeCollateralValue(SpatialNode n)
        {
            long total = Tuning.CentralBankTerritoryNodeValue;
            if (Economy.HasPhysicalStation(n))
            {
                long stationBookValue = n.StationBookValue.HasValue ? n.StationBookValue.Value : 0;
                total += stationBookValue > 0 ? stationBookValue : Tuning.CentralBankFoundedNodeValue;
            }

            foreach (var cap in n.Capabilities)
                if (cap.Value > 0)
                    total += cap.Value * Tuning.CentralBankCapabilityLevelValue;

            foreach (var ship in n.ShipStock)
                if (ship.Value > 0)
                    total += ship.Value * HullCollateralValue(ship.Key);

            return total;
        }

        /// <summary>Issues a grant if eligible. Returns the amount granted (0 if none) so the caller
        /// can attribute it as real income for <see cref="FactionState.CentralBankRecentIncome"/>.</summary>
        private long MaybeIssueCentralBankGrant(string factionTag, FactionState state, long ledger, long debt,
            ref long remainingExpansionBudget)
        {
            string denialReason;
            long collateral = GetFactionCollateralValue(factionTag, state);
            long grant = ComputeCentralBankGrant(factionTag, state, ledger, debt, collateral, remainingExpansionBudget, out denialReason);
            if (grant <= 0)
            {
                long treasury = GetTreasury(factionTag);
                long target = ComputeCentralBankGrantTreasuryTarget(factionTag, collateral);
                if (DebugTraces.CentralBankDenials && !string.IsNullOrEmpty(denialReason) && treasury < target)
                    AppendCentralBankEvent("grant-denied", factionTag, 0, denialReason);
                return 0;
            }

            AddTreasury(factionTag, grant);
            if (state != null)
                SetCentralBankGrantCooldownUntil(factionTag, state, UtcNow + Tuning.CentralBankGrantCooldown);
            remainingExpansionBudget -= grant;
            AppendCentralBankEvent("grant", factionTag, grant,
                "Grant issued to a hopeful faction without creating debt.");
            return grant;
        }

        private void MaybeIssueCentralBankSupportLoan(string factionTag, FactionState state, long ledger, long debt,
            ref long remainingExpansionBudget)
        {
            string denialReason;
            long loan = ComputeCentralBankSupportLoan(factionTag, state, ledger, debt, remainingExpansionBudget, out denialReason);
            if (loan <= 0)
            {
                long treasury = GetTreasury(factionTag);
                long target = ComputeCentralBankSupportTreasuryTarget(factionTag);
                if (DebugTraces.CentralBankDenials && !string.IsNullOrEmpty(denialReason) && treasury < target)
                    AppendCentralBankEvent("support-denied", factionTag, 0, denialReason);
                return;
            }

            AddTreasury(factionTag, loan);
            SetDebt(factionTag, debt + loan);
            if (state != null)
                state.CentralBankSupportDebt += loan;
            SetCentralBankSupportCooldownUntil(factionTag, state, UtcNow + Tuning.CentralBankSupportCooldown);
            remainingExpansionBudget -= loan;
            AppendCentralBankEvent("support-loan", factionTag, loan,
                "Support loan issued against collateral-backed liquidity stress.");
            AppendDebtEvent(factionTag, "support-loan", loan, GetTreasury(factionTag), debt + loan,
                "Central-bank support loan added liquidity before forced shedding.");
        }

        private long ComputeCentralBankGrant(string factionTag, FactionState state, long ledger, long debt, long collateral,
            long remainingExpansionBudget, out string denialReason)
        {
            denialReason = null;
            if (string.IsNullOrEmpty(factionTag)) return 0;
            if (Tuning.CentralBankGrantAmount <= 0)
            {
                denialReason = "Grant policy is disabled.";
                return 0;
            }
            if (remainingExpansionBudget <= 0)
            {
                denialReason = "Central-bank expansion budget is exhausted.";
                return 0;
            }

            // Stated, not incidental. A faction owning no node is already denied below, because
            // GetFactionCollateralValue sums over owned nodes and returns 0 -- but that is a side
            // effect of how collateral happens to be computed, not a rule anyone wrote down. Add any
            // non-node term to collateral (treasury, book value of player-built grids, ships held
            // off-node) and a node-less faction silently becomes grant-eligible, with nothing in the
            // diff to suggest it. The gate that states the rule has to be the gate that enforces it.
            if (!OwnsAnyNode(factionTag))
            {
                denialReason = "Faction owns no node, so it is not grant-eligible.";
                return 0;
            }
            if (collateral <= 0)
            {
                denialReason = "Faction has no collateral for grant policy.";
                return 0;
            }
            long treasuryTarget = ComputeCentralBankGrantTreasuryTarget(factionTag, collateral);
            long treasury = GetTreasury(factionTag);
            if (treasury >= treasuryTarget) return 0;

            // A player faction's members' own wallets count as its liquidity: what they hold they can
            // deposit, so the bank grants only the part of the target they cannot cover themselves.
            long liquidity = treasury;
            long memberCover = GrantMemberCover(state);
            if (memberCover > 0)
            {
                liquidity = memberCover > long.MaxValue - Math.Max(0L, treasury) ? long.MaxValue : treasury + memberCover;
                if (liquidity >= treasuryTarget)
                {
                    denialReason = "Members' own wallets cover the grant target.";
                    return 0;
                }
            }

            DateTime cooldownUntil = GetCentralBankGrantCooldownUntil(factionTag, state);
            if (cooldownUntil > UtcNow)
            {
                denialReason = "Grant cooldown is still active.";
                return 0;
            }

            long needed = treasuryTarget - liquidity;
            long collateralCap = (long)Math.Ceiling(collateral * Tuning.CentralBankGrantCollateralFraction);
            if (collateralCap < 1) collateralCap = 1;
            long grantCap = Math.Min(Tuning.CentralBankGrantAmount, collateralCap);
            long grant = Math.Min(remainingExpansionBudget, Math.Min(grantCap, needed));
            if (grant <= 0)
            {
                denialReason = "Grant request rounded down to zero.";
                return 0;
            }
            return grant;
        }

        /// <summary>The share of a player faction's member wealth counted against its grant target. 0 for
        /// an NPC faction, whose members are crew and agents rather than investors.</summary>
        private long GrantMemberCover(FactionState state)
        {
            if (state == null || !state.IsPlayerFaction) return 0;
            double fraction = Tuning.CentralBankGrantMemberWealthFraction;
            if (fraction <= 0) return 0;
            long wealth = MemberWealth.MemberWealth(state);
            if (wealth <= 0) return 0;
            double counted = wealth * fraction;
            return counted >= long.MaxValue ? long.MaxValue : (long)counted;
        }

        private long ComputeCentralBankSupportLoan(string factionTag, FactionState state, long ledger, long debt,
            long remainingExpansionBudget, out string denialReason)
        {
            denialReason = null;
            if (string.IsNullOrEmpty(factionTag)) return 0;
            if (Tuning.CentralBankSupportLoanAmount <= 0 || Tuning.CentralBankSupportDebtFraction <= 0)
            {
                denialReason = "Support lending policy is disabled.";
                return 0;
            }
            if (remainingExpansionBudget <= 0)
            {
                denialReason = "Central-bank expansion budget is exhausted.";
                return 0;
            }

            long treasuryTarget = ComputeCentralBankSupportTreasuryTarget(factionTag);
            long treasury = GetTreasury(factionTag);
            if (treasury >= treasuryTarget) return 0;

            DateTime cooldownUntil = GetCentralBankSupportCooldownUntil(factionTag, state);
            if (cooldownUntil > UtcNow)
            {
                denialReason = "Support loan cooldown is still active.";
                return 0;
            }

            // The same rule the grant path states explicitly, and for the same reason: a landless
            // faction was denied here only because collateral summed over owned nodes and came to 0.
            // In-flight hulls are a non-node term, so that incidental denial no longer holds.
            // Unreachable today -- the income pass enumerates node owners, so it never visits a
            // landless faction -- which is precisely why the rule is written here rather than left to
            // the enumeration. Do not read a passing test as evidence this gate fired.
            if (!OwnsAnyNode(factionTag))
            {
                denialReason = "Faction owns no node, so it is not eligible for rescue lending.";
                return 0;
            }
            long collateral = GetFactionCollateralValue(factionTag, state);
            if (collateral <= 0)
            {
                denialReason = "Faction has no collateral for rescue lending.";
                return 0;
            }
            if (ledger + collateral <= 0)
            {
                denialReason = "Faction is not collateral-solvent.";
                return 0;
            }

            long supportDebt = state != null ? state.CentralBankSupportDebt : 0;
            long maxSupportDebt = (long)(collateral * Tuning.CentralBankSupportDebtFraction);
            if (maxSupportDebt <= supportDebt)
            {
                denialReason = "Support debt cap reached.";
                return 0;
            }

            long needed = treasuryTarget - treasury;
            long headroom = maxSupportDebt - supportDebt;
            if (headroom <= 0)
            {
                denialReason = "No support-debt headroom remains.";
                return 0;
            }

            long loan = Math.Min(remainingExpansionBudget, Math.Min(Tuning.CentralBankSupportLoanAmount, Math.Min(needed, headroom)));
            if (loan <= 0)
            {
                denialReason = "Support loan request rounded down to zero.";
                return 0;
            }

            // Lending-standard gate: a bank shouldn't keep extending debt once the resulting interest
            // would outrun what the faction actually earns (real income only — grants count, loan
            // principal never does, which would be circular). Income starts at 0, so a faction with no
            // earnings history is denied until it has received at least one grant or sold something.
            double capFraction = Tuning.CentralBankDebtServiceIncomeCapFraction;
            if (capFraction > 0)
            {
                double recentIncome = state != null ? state.CentralBankRecentIncome : 0;
                long projectedInterest = (long)((debt + loan) * Tuning.DebtInterestRate);
                double incomeCap = recentIncome * capFraction;
                if (projectedInterest > incomeCap)
                {
                    denialReason = "Debt service on this loan would exceed the income cap.";
                    return 0;
                }
            }
            return loan;
        }

        private long ComputeCentralBankSupportTreasuryTarget(string factionTag)
        {
            return ComputeCentralBankTreasuryTarget(factionTag, Tuning.CentralBankSupportTreasuryBuffer, Tuning.CentralBankSupportRunwayTicks);
        }

        private long ComputeCentralBankGrantTreasuryTarget(string factionTag, long collateral)
        {
            long treasuryTarget = ComputeCentralBankTreasuryTarget(factionTag, Tuning.CentralBankGrantTreasuryBuffer, Tuning.CentralBankGrantRunwayTicks);
            if (collateral > 0 && Tuning.CentralBankGrantCollateralFraction > 0)
            {
                long collateralTarget = (long)Math.Ceiling(collateral * Tuning.CentralBankGrantCollateralFraction);
                if (collateralTarget > treasuryTarget)
                    treasuryTarget = collateralTarget;
            }
            return treasuryTarget;
        }

        private long ComputeCentralBankTreasuryTarget(string factionTag, long buffer, double runwayTicks)
        {
            long treasuryTarget = buffer;
            // Pass-scoped read: see GetNetFlowForIncomePass. Reading the published cache here would
            // substitute last pass's value for the "cleared at pass start" 0 this has always seen.
            long netFlow = GetNetFlowForIncomePass(factionTag);
            if (runwayTicks > 0 && netFlow < 0)
            {
                double runwayTarget = -netFlow * runwayTicks;
                if (runwayTarget >= long.MaxValue) return long.MaxValue;
                long roundedRunwayTarget = (long)Math.Ceiling(runwayTarget);
                if (roundedRunwayTarget > treasuryTarget)
                    treasuryTarget = roundedRunwayTarget;
            }
            return treasuryTarget;
        }

        private DateTime GetCentralBankSupportCooldownUntil(string factionTag, FactionState state)
        {
            DateTime until = state != null ? state.CentralBankSupportCooldownUntilUtc : DateTime.MinValue;
            DateTime worldUntil;
            if (_centralBankSupportCooldownUntil.TryGetValue(factionTag, out worldUntil) && worldUntil > until)
                until = worldUntil;
            return until;
        }

        private void SetCentralBankSupportCooldownUntil(string factionTag, FactionState state, DateTime untilUtc)
        {
            _centralBankSupportCooldownUntil[factionTag] = untilUtc;
            if (state != null) state.CentralBankSupportCooldownUntilUtc = untilUtc;
        }

        private DateTime GetCentralBankGrantCooldownUntil(string factionTag, FactionState state)
        {
            DateTime until = state != null ? state.CentralBankGrantCooldownUntilUtc : DateTime.MinValue;
            DateTime worldUntil;
            if (_centralBankGrantCooldownUntil.TryGetValue(factionTag, out worldUntil) && worldUntil > until)
                until = worldUntil;
            return until;
        }

        private void SetCentralBankGrantCooldownUntil(string factionTag, FactionState state, DateTime untilUtc)
        {
            _centralBankGrantCooldownUntil[factionTag] = untilUtc;
            if (state != null) state.CentralBankGrantCooldownUntilUtc = untilUtc;
        }

        // The roster-wide asset-purchase sweep now lives in WorldModel.IncomePass.cs as the
        // SelectingAssetSellers/BuyingAssets phases (one faction per bounded unit, same order, same
        // shared remaining-budget arithmetic).

        /// <summary>For each of <paramref name="state"/>'s target rules, what the faction holds past the
        /// target across every node it owns -- the most the bank may buy of that item this pass. Null
        /// when the faction has no target rule, the normal case, so the sweep pays nothing for it.</summary>
        private Dictionary<string, long> CentralBankTargetAllowance(string factionTag, FactionState state)
        {
            if (state == null || state.CentralBankTargets.Count == 0) return null;
            var allowance = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> rule in state.CentralBankTargets) allowance[rule.Key] = -rule.Value;
            foreach (SpatialNode n in OwnedNodesForPass(factionTag))
                foreach (KeyValuePair<string, long> rule in state.CentralBankTargets)
                {
                    long have;
                    if (n.Inventory.TryGetValue(rule.Key, out have) && have > 0) allowance[rule.Key] += have;
                }
            foreach (string key in new List<string>(allowance.Keys))
                if (allowance[key] < 0) allowance[key] = 0;
            return allowance;
        }

        private bool HasCentralBankBuyableProcessedGoodSurplus(string factionTag, FactionState state)
        {
            if (string.IsNullOrEmpty(factionTag)) return false;
            Dictionary<string, long> allowance = CentralBankTargetAllowance(factionTag, state);
            foreach (SpatialNode n in OwnedNodesForPass(factionTag))
            {
                foreach (var kv in n.Inventory)
                {
                    if (!IsBuyableCentralBankProcessedGood(kv.Key)) continue;
                    if (!FactionLifecycle.CentralBankBuys(state, kv.Key)) continue;
                    // A pending ship build's uncollected BOM share is not surplus, however oversupplied
                    // the node's raw stock looks — see SpatialNode.BuildQueueReserved.
                    long available = kv.Value - n.BuildQueueReserved(kv.Key, UtcNow);
                    long aboveTarget;
                    if (allowance != null && allowance.TryGetValue(kv.Key, out aboveTarget) && aboveTarget < available)
                        available = aboveTarget;
                    if (available < Tuning.CentralBankBuyMinLot) continue;
                    double scarcity = PriceOracle.ScarcityMultiplier(n.Id, kv.Key);
                    if (scarcity > Tuning.CentralBankBuyScarcityFloor) continue;
                    return true;
                }
            }
            return false;
        }

        private long BuyCentralBankProcessedGoodSurplus(string factionTag, FactionState state, long budget,
            ref long remainingExpansionBudget)
        {
            long spendBudget = Math.Min(budget, remainingExpansionBudget);
            if (spendBudget <= 0) return 0;

            double fraction = Tuning.CentralBankBuyFraction;
            long spent = 0;
            Dictionary<string, long> allowance = CentralBankTargetAllowance(factionTag, state);

            foreach (SpatialNode n in OwnedNodesForPass(factionTag))
            {
                var keys = new List<string>(n.Inventory.Keys);
                foreach (string key in keys)
                {
                    if (!IsBuyableCentralBankProcessedGood(key)) continue;
                    if (!FactionLifecycle.CentralBankBuys(state, key)) continue;

                    long have;
                    if (!n.Inventory.TryGetValue(key, out have) || have < Tuning.CentralBankBuyMinLot) continue;

                    // A pending ship build's uncollected BOM share is not for sale — leave it for
                    // ShipyardManager's own absorption tick (see SpatialNode.BuildQueueReserved).
                    long available = have - n.BuildQueueReserved(key, UtcNow);
                    if (available < Tuning.CentralBankBuyMinLot) continue;

                    double scarcity = PriceOracle.ScarcityMultiplier(n.Id, key);
                    if (scarcity > Tuning.CentralBankBuyScarcityFloor) continue;

                    ItemKind kind;
                    if (!TryGetInventoryItemKind(key, out kind)) continue;
                    long unitPrice = GetCentralBankPurchasePrice(key, kind, scarcity);
                    if (unitPrice <= 0) continue;
                    if (remainingExpansionBudget < unitPrice || spendBudget < unitPrice) return spent;

                    // Buy 1 % of current stock, respecting the minimum lot, current budget, and the
                    // build-reserved floor above.
                    long units = (long)Math.Ceiling(have * fraction);
                    if (units < Tuning.CentralBankBuyMinLot) units = Tuning.CentralBankBuyMinLot;
                    if (units > available) units = available;
                    // A target rule: never below the faction-wide target, across every node this
                    // pass buys from.
                    long aboveTarget = 0;
                    bool targeted = allowance != null && allowance.TryGetValue(key, out aboveTarget);
                    if (targeted && units > aboveTarget) units = aboveTarget;
                    long affordable = spendBudget / unitPrice;
                    if (affordable <= 0) return spent;
                    if (units > affordable) units = affordable;
                    if (units <= 0) continue;

                    long value = units * unitPrice;
                    if (targeted) allowance[key] -= units;
                    AddNodeInventory(n.Id, key, -units);
                    AddTreasury(factionTag, value);
                    remainingExpansionBudget -= value;
                    spendBudget -= value;
                    spent += value;
                    AppendCentralBankEvent("asset-purchase", factionTag, value,
                        "Central bank bought " + key + " x" + units
                        + " at unitPrice=" + unitPrice
                        + " scarcity=" + scarcity.ToString("0.###") + ".");
                }
            }

            return spent;
        }

        // Returns true iff the item is a ProcessedGood (kind check only — catalog demand
        // is always active for these items, so a demand-gate would suppress all purchases).
        private bool IsBuyableCentralBankProcessedGood(string inventoryKey)
        {
            if (string.IsNullOrEmpty(inventoryKey)) return false;
            if (Registry == null) return true;
            ItemKind kind;
            return Registry.TryGetItemKind(StripInvKeyPrefix(inventoryKey), out kind)
                && kind == ItemKind.ProcessedGood;
        }

        private long GetCentralBankPurchasePrice(string inventoryKey, ItemKind kind, double scarcity)
        {
            long basePrice = GetItemBookValue(inventoryKey, kind);
            if (basePrice <= 0) return 0;
            if (scarcity <= 0) scarcity = Tuning.ScarcityMin > 0 ? Tuning.ScarcityMin : 1.0;
            double scaled = basePrice * Tuning.CentralBankAssetPurchasePriceMultiplier * scarcity;
            long rounded = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
            return rounded > 0 ? rounded : 1;
        }

        private long ComputeCentralBankAdjustmentBudget(long absoluteGap)
        {
            if (absoluteGap <= 0) return 0;
            double fraction = Tuning.CentralBankMaxAdjustmentFractionPerTick;
            if (fraction <= 0) return 0;
            if (fraction >= 1.0) return absoluteGap;
            long budget = (long)Math.Ceiling(absoluteGap * fraction);
            if (budget < 1) budget = 1;
            return budget > absoluteGap ? absoluteGap : budget;
        }

        // The roster-wide contraction levy now lives in WorldModel.IncomePass.cs: its prologue (budget
        // and positive-treasury total) runs in the MoneySupplyBarrier step and its loop body one
        // faction per bounded unit in the Contracting phase, with identical levy arithmetic.

        private void AppendCentralBankEvent(string type, string factionTag, long amount, string message)
        {
            AppendMonetaryEvent(_centralBankEvents, "bank", type, factionTag, amount, message);
        }

        private void AppendDebtEvent(string factionTag, string type, long amount, long treasuryAfter, long debtAfter, string message)
        {
            AppendMonetaryEvent(_debtEvents, "debt", type, factionTag, amount, message, treasuryAfter, debtAfter);
        }

        private void AppendMonetaryEvent(List<MonetaryEvent> sink, string scope, string type, string factionTag,
            long amount, string message, long? treasuryAfterOverride = null, long? debtAfterOverride = null)
        {
            if (sink == null) return;
            sink.Add(new MonetaryEvent
            {
                UtcNow = UtcNow,
                Scope = scope,
                Type = type,
                FactionTag = factionTag,
                Amount = amount,
                TreasuryAfter = treasuryAfterOverride ?? (string.IsNullOrEmpty(factionTag) ? 0 : GetTreasury(factionTag)),
                DebtAfter = debtAfterOverride ?? (string.IsNullOrEmpty(factionTag) ? 0 : GetDebt(factionTag)),
                MoneySupplyAfter = GetCurrentMoneySupply(),
                MoneySupplyGapAfter = TargetMoneySupplyForPass() - GetCurrentMoneySupply(),
                Message = message,
            });
            if (sink.Count > MonetaryEventHistoryCap)
                sink.RemoveRange(0, sink.Count - MonetaryEventHistoryCap);
        }

        private bool IsTrueInsolvencyAfterLiquiditySupport(long effectiveLedger)
        {
            return effectiveLedger <= Tuning.InsolvencyFloor;
        }

        // The free-mint yield table lives in FactionTuning.NodeYieldTable (tunable via tuning.json).

        /// <summary>Insolvency safety net: shed (Withdraw) the single worst net-negative owned source — the same
        /// "drain" the brain's consolidation targets — so a faction at the floor stops bleeding regardless of its
        /// current goal or affordability. Returns the shed source id, or null if nothing it owns is net-negative.
        /// <para>The node's hulls fly off on <paramref name="state"/>'s fleet list at once, and the
        /// records land in <see cref="ShipEvacuations"/> for the host to log after the pass step that
        /// shed it. With no <paramref name="state"/> they stay on the node.</para></summary>
        public string ForceShedWorstDrain(string factionTag, FactionState state = null)
        {
            SpatialNode worst = null; long worstNet = 0;
            foreach (SpatialNode n in OwnedNodesForPass(factionTag))
            {
                long net = Economy.SourceNet(n, _field.Control(n.Position, factionTag), Tuning, Catalogue);
                if (net < 0 && (worst == null || net < worstNet)) { worst = n; worstNet = net; }
            }
            if (worst == null) return null;
            AbandonNode(worst, state);
            return worst.Id;
        }

        /// <summary>Set a capability's level on a held node absolutely — install, raise, lower, or
        /// (at level 0 or below) remove it entirely. <see cref="Develop"/> only ever adds one level,
        /// so this is the primitive the operator-driven grant
        /// (<see cref="CapabilityGrantValidator"/>) needs to name a level outright. The caller is
        /// responsible for clamping against <see cref="FactionTuning.MaxLevelFor"/> — this does not
        /// clamp itself, for the same reason <see cref="Develop"/> doesn't. No-op if the faction
        /// doesn't own the node.</summary>
        public void SetCapabilityLevel(string factionTag, string nodeId, NodeCapabilityKind capability, int level)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n) || n.OwnerFactionTag != factionTag) return;
            if (level <= 0) n.Capabilities.Remove(capability);
            else n.Capabilities[capability] = level;
        }

        /// <summary>Remove an installed capability from a held node. Reduces item upkeep without
        /// abandoning the node entirely. No-op if the faction doesn't own the node or the capability
        /// isn't installed.</summary>
        public void DismantleCapability(string factionTag, string nodeId, NodeCapabilityKind capability)
        {
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n)) return;
            if (n.OwnerFactionTag != factionTag) return;
            int level;
            if (n.Capabilities.TryGetValue(capability, out level) && level > 0)
                n.Capabilities.Remove(capability);
        }

        /// <summary>Decay hold for all owned nodes at a single rate, regardless of who owns them. Kept
        /// as the low-level, per-invocation primitive for simulation harnesses and tests. See the
        /// two-rate overload for player-vs-NPC differentiated decay.</summary>
        public void DecayInfluence(double rate)
        {
            DecayInfluence(rate, rate, null);
        }

        /// <summary>Decay hold for all owned nodes. Decay is suppressed while the node was reinforced
        /// (or planted/claimed/scanned) within <see cref="FactionTuning.ReinforceGracePeriod"/> —
        /// so freshly maintained territory stays stable. Neglected engine nodes (<c>N###</c>) reaching
        /// zero hold are transferred to <see cref="NeutralFaction"/> (UNKN) — territory recedes but the
        /// node itself is never removed from the graph; its ID is recorded in
        /// <see cref="AbandonedNodes"/> so the host can rename the now-derelict station. Scan-backed
        /// nodes (<c>E###</c>/<c>G###</c>) are floored at 0.1 — they always yield something and persist
        /// until a re-scan reconciles them. Already-neutral (UNKN-owned) nodes are skipped entirely —
        /// nothing left to decay. A freshly-abandoned node's <see cref="SpatialNode.AbandonedUtc"/> is
        /// stamped so <see cref="GetExpiredUnknNodeIds"/> can eventually bound how long it persists
        /// unclaimed. <paramref name="playerRate"/> applies to nodes owned by a faction with
        /// <see cref="FactionState.IsPlayerFaction"/> true (looked up in <paramref name="states"/>);
        /// <paramref name="rate"/> applies to everyone else. Pass null <paramref name="states"/> to
        /// apply <paramref name="rate"/> uniformly.</summary>
        public void DecayInfluence(double rate, double playerRate, IList<FactionState> states)
        {
            DecayInfluence(rate, playerRate, states, 0.0);
        }

        /// <summary>As the three-argument overload, additionally netting each node's ship-traffic
        /// hold gain (<see cref="ShipActivity.HoldGainForInterval"/>) against its decay for a pass
        /// covering <paramref name="intervalSeconds"/>. Net movement is
        /// <c>gain - decay</c>: at saturated credit
        /// <see cref="FactionTuning.ShipActivityHoldPerHour"/> exceeds the decay rate, so a
        /// well-trafficked node climbs back towards full hold on traffic alone, capped at 1.0. A
        /// node inside <see cref="FactionTuning.ReinforceGracePeriod"/> skips decay and therefore
        /// the trickle too. Pass 0 (what the shorter overloads do) to decay without any traffic
        /// gain.
        ///
        /// <para>A node inside <see cref="SpatialNode.ContestedUntilUtc"/> is the asymmetric case:
        /// it decays at the ordinary rate and earns no gain, so raid damage accumulates across a
        /// siege instead of healing between its cycles.</para>
        ///
        /// <para>A node at 0 whose owner holds it and can pay is not abandoned yet
        /// (<see cref="FactionTuning.HoldZeroGraceSeconds"/>, <see cref="SpatialNode.HoldZeroSinceUtc"/>).
        /// Without <paramref name="states"/> no owner can be asked, and a node at 0 is abandoned
        /// at once.</para></summary>
        public void DecayInfluence(double rate, double playerRate, IList<FactionState> states, double intervalSeconds)
        {
            HashSet<string> playerTags = null;
            if (states != null)
            {
                playerTags = new HashSet<string>();
                for (int i = 0; i < states.Count; i++)
                    if (states[i] != null && states[i].IsPlayerFaction) playerTags.Add(states[i].Tag);
            }

            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag == null || n.OwnerFactionTag == NeutralFaction.Tag) continue;
                // Ahead of the reinforce-grace skip below: a node paid back to 0.7 is skipped there,
                // and its zero-hold clock must not survive into the next time it reaches 0.
                if (n.HoldStrength > 0) n.HoldZeroSinceUtc = default(DateTime);
                // MinValue means never reinforced — no grace period applies.
                if (n.LastReinforcedUtc != DateTime.MinValue && UtcNow - n.LastReinforcedUtc < Tuning.ReinforceGracePeriod) continue;
                double effectiveRate = (playerTags != null && playerTags.Contains(n.OwnerFactionTag)) ? playerRate : rate;
                // A contested node decays but does not heal. Its own convoys are still flying, so the
                // activity credit keeps accruing and the node resumes climbing the moment the window
                // lapses -- what is withheld is the claim that traffic through ground someone is
                // actively taking counts as undisputed presence.
                double activityGain = (intervalSeconds > 0 && UtcNow >= n.ContestedUntilUtc)
                    ? ShipActivity.HoldGainForInterval(n, Tuning, UtcNow, intervalSeconds)
                    : 0.0;
                n.HoldStrength = Clamp01(n.HoldStrength + activityGain - effectiveRate);
                if (n.HoldStrength <= 0)
                {
                    if (SourceMapping.IsScanBacked(n.Id))
                        n.HoldStrength = 0.1;   // floor: real assets always yield something
                    else if (InHoldZeroGrace(n, StateOf(states, n.OwnerFactionTag)))
                    {
                        // Its owner can still answer: the reinforce manager pays for it the first
                        // pass no attack is standing on it (FleetManager, why=hold_grace).
                    }
                    else
                    {
                        _abandonedNodes.Add(new AbandonedNode { NodeId = n.Id, PriorOwnerTag = n.OwnerFactionTag });
                        RecordEvent(FactionEventKind.NodeLost, n.OwnerFactionTag, null, n.Id);
                        AbandonNode(n, StateOf(states, n.OwnerFactionTag));
                    }
                }
            }
        }

        /// <summary>Whether <paramref name="n"/>, at hold 0, stays with its owner for now. Abandonment
        /// by decay is for an owner that does not respond; one that holds the node and can pay for
        /// it gets <see cref="FactionTuning.HoldZeroGraceSeconds"/> from when hold reached 0, and
        /// never less than the contest window — the paid reinforce is refused while an attack
        /// stands on the node, so the grace has to outlast the siege it answers.
        ///
        /// <para>"Holds" reads the list the reinforce manager walks: a player's own opt-in, or the
        /// brain's ranking minus ground that mints nothing, which the manager never pays for. An
        /// owner below one <see cref="FactionTuning.ReinforceCost"/> cannot answer, and loses the
        /// node on this pass as before.</para></summary>
        private bool InHoldZeroGrace(SpatialNode n, FactionState owner)
        {
            if (owner == null || Tuning == null || Tuning.HoldZeroGraceSeconds <= 0) return false;
            if (!OwnerHolds(owner, n)) return false;
            if (GetTreasury(owner.Tag) < Tuning.ReinforceCost) return false;

            if (n.HoldZeroSinceUtc == default(DateTime)) n.HoldZeroSinceUtc = UtcNow;
            if (UtcNow < n.ContestedUntilUtc) return true;
            return (UtcNow - n.HoldZeroSinceUtc).TotalSeconds < Tuning.HoldZeroGraceSeconds;
        }

        // A player faction's ranked list is projected from its opt-in by the manager each tick,
        // so the opt-in itself is read here: a decay pass before the first projection would
        // otherwise see an empty list and abandon ground the player told us to hold.
        private static bool OwnerHolds(FactionState owner, SpatialNode n)
        {
            if (owner.IsPlayerFaction) return owner.ReinforceOptIn.ContainsKey(n.Id);
            if (!FactionLifecycle.IsAutonomyDisabled(owner) && Economy.MintsNothing(n)) return false;
            for (int i = 0; i < owner.ReinforcePriority.Count; i++)
                if (string.Equals(owner.ReinforcePriority[i].NodeId, n.Id, StringComparison.Ordinal)) return true;
            return false;
        }

        private static FactionState StateOf(IList<FactionState> states, string tag)
        {
            if (states == null || tag == null) return null;
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && string.Equals(states[i].Tag, tag, StringComparison.Ordinal)) return states[i];
            return null;
        }

        /// <summary>Credits one resolved fleet stop at <paramref name="nodeId"/> as ship activity,
        /// returning the node's new credit level. No-op unless the node exists and
        /// <paramref name="actorTag"/> is its owner — someone else's convoy passing through is not
        /// this node's territorial presence. See <see cref="ShipActivity"/>.</summary>
        public double CreditShipActivity(string nodeId, string actorTag)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(actorTag)) return 0.0;
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n)) return 0.0;
            if (!string.Equals(n.OwnerFactionTag, actorTag, StringComparison.Ordinal)) return 0.0;
            return ShipActivity.Credit(n, Tuning, UtcNow);
        }

        /// <summary>UNKN-owned nodes whose <see cref="SpatialNode.AbandonedUtc"/> is older than
        /// <see cref="FactionTuning.UnknGraceDuration"/> -- candidates for permanent removal. Pure
        /// query, mutates nothing: player-proximity gating is host-only (SE-aware), so the caller
        /// decides which of these are actually safe to pass to <see cref="RemoveNode"/>.</summary>
        public IList<string> GetExpiredUnknNodeIds()
        {
            var result = new List<string>();
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != NeutralFaction.Tag) continue;
                if (n.AbandonedUtc == DateTime.MinValue) continue;
                if (UtcNow - n.AbandonedUtc < Tuning.UnknGraceDuration) continue;
                result.Add(n.Id);
            }
            return result;
        }

        /// <summary>One-time operator backfill: stamps <see cref="SpatialNode.AbandonedUtc"/> on every
        /// UNKN-owned node that predates that field's introduction (still at its
        /// <c>DateTime.MinValue</c> default, so <see cref="GetExpiredUnknNodeIds"/> would otherwise
        /// skip it forever) with <c>UtcNow - age</c>. Exists to let an operator deliberately fold the
        /// pre-existing UNKN backlog into the grace-expiry stopgap on demand -- ordinary decay/Withdraw
        /// already stamp this correctly going forward, so this is a bridge for nodes abandoned before
        /// that code shipped, not a replacement for it. Dry-run (<paramref name="applyFix"/> false)
        /// reports the count without mutating anything. Returns the number of nodes matched/stamped.</summary>
        public int BackfillAbandonedUtc(TimeSpan age, bool applyFix)
        {
            int count = 0;
            DateTime target = UtcNow - age;
            foreach (SpatialNode n in Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != NeutralFaction.Tag) continue;
                if (n.AbandonedUtc != DateTime.MinValue) continue;
                count++;
                if (applyFix) n.AbandonedUtc = target;
            }
            return count;
        }

        /// <summary>Deletes <paramref name="nodeId"/> on the operator's instruction, whoever owns it
        /// (bridge verb <c>factions removenode</c>). Its owner's parked hulls fly to the owner's
        /// nearest reachable node first (<see cref="HullRelocation.Evacuate"/>, recorded in
        /// <see cref="ShipEvacuations"/>); everything else on it goes with <see cref="RemoveNode"/>.
        /// The caller tears down the physical station first, as for any removal.</summary>
        public RemovedNodeContents RetireNode(string nodeId, FactionState owner)
        {
            SpatialNode n;
            if (nodeId == null || !Graph.Nodes.TryGetValue(nodeId, out n)) return new RemovedNodeContents();
            string ownerTag = n.OwnerFactionTag;
            // Off the owner first, as AbandonNode does, so the evacuation cannot pick this node as
            // its own landing.
            n.OwnerFactionTag = NeutralFaction.Tag;
            if (ownerTag != null && ownerTag != NeutralFaction.Tag)
                HullRelocation.Evacuate(this, owner, ownerTag, n, _shipEvacuations);
            return RemoveNode(nodeId);
        }

        /// <summary>Permanently deletes a node from the graph -- the stopgap grace-period sink for
        /// <see cref="GetExpiredUnknNodeIds"/> candidates, and the only node-removal path in the
        /// codebase since the 2026-08-12 abandonment redesign made ordinary decay hand nodes to UNKN
        /// instead (see docs/todo.md). Also strips the removed ID from every remaining node that still
        /// listed it as a neighbor -- <see cref="FactionStore.RestoreTopology"/>/
        /// <see cref="SpatialGraph.Frontier"/>/<see cref="GraphPathfinder"/> already tolerate a stale or
        /// missing endpoint regardless, so this is JSON/graph tidiness, not a correctness requirement.
        /// Caller is responsible for any host-side physical station teardown first (see
        /// <c>FactionRuntime.PruneExpiredUnknTerritory</c>) -- this method only ever touches graph
        /// state.
        /// <para>Returns what the removal destroyed (whole hulls, damaged hulls and inventory units),
        /// so the caller's line can say so.</para>
        /// <para>Provenance: FAC-278.</para></summary>
        public RemovedNodeContents RemoveNode(string nodeId)
        {
            var destroyed = new RemovedNodeContents();
            SpatialNode n;
            if (nodeId == null || !Graph.Nodes.TryGetValue(nodeId, out n)) return destroyed;

            // Counted before the node goes. Hulls are normally zero -- AbandonNode flies them off --
            // so a non-zero count is the measurement that says the evacuation had nowhere to send
            // them.
            foreach (var ship in n.ShipStock)
                if (ship.Value > 0) destroyed.Hulls += ship.Value;
            destroyed.DamagedHulls = n.DamagedHulls.Count;
            foreach (var item in n.Inventory)
                if (item.Value > 0) destroyed.ItemUnits += item.Value;

            foreach (string neighborId in n.Neighbors)
            {
                SpatialNode neighbor;
                if (Graph.Nodes.TryGetValue(neighborId, out neighbor)) neighbor.Neighbors.Remove(nodeId);
            }
            Graph.Nodes.Remove(nodeId);
            return destroyed;
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }
}
