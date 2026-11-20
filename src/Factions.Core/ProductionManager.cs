using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// Converts raw inputs to processed outputs at nodes that have production capabilities installed.
    /// Runs before <see cref="OperationsManager"/> so freshly produced items are routable
    /// in the same pass, and before <see cref="ShipyardManager"/> so newly assembled components
    /// can be consumed by build checks in the same tick.
    ///
    /// <para>Each installed capability is modelled as a bank of N machines (e.g. Refinery I = a bank
    /// of Blast Furnaces). Each machine has its own <see cref="MachineSlot"/> that accumulates
    /// fractional batches across real time, advancing only as fast as the SE blueprint speed allows.
    /// This replaces the old MaxBatchesPerTick ceiling with physically-derived throughput. Machine
    /// count, speed, output scaling and input efficiency all come from that capability's
    /// <see cref="MachineProfile"/> — see <see cref="MachinePhysics"/> for the SE formulae.</para>
    ///
    /// <para>Each tick the bank is re-balanced from scratch: only recipes with a demanded,
    /// concretely scarce output compete. They are capped by how many machines their current input
    /// can usefully feed over <see cref="FactionTuning.ProductionSlotInputHorizonSeconds"/> (so a recipe with only a trickle
    /// of ore cannot hold machines it cannot keep busy), then fair-shared in scarcity order without
    /// exceeding each recipe's cap. A bank deliberately idles when no demanded output is scarce.
    /// When a recipe loses machines, the freed slots' partial-batch <see cref="MachineSlot.Accum"/>
    /// is consolidated into a surviving slot of the same recipe — so reassignment never discards
    /// accumulated work and no per-tick hysteresis lock is needed.</para>
    ///
    /// <para>The recipe list per capability kind is pre-resolved by the host at startup from
    /// <c>capability_recipes.json</c> and SE blueprint class definitions, keeping SE API
    /// calls out of this SE-free core class.</para>
    /// </summary>
    public sealed partial class ProductionManager : IFactionManager
    {
        // Per-capability machine physics, indexed by NodeCapabilityKind value. Resolved once per
        // faction pass from FactionTuning.MachineProfileFor (host-stamped from the SE block definition
        // and world settings, or Core's default table) and threaded through the bank loop on
        // FactionCyclePrep.Models, so a resumed cycle keeps the model it started with. See
        // MachinePhysics for the SE formulae and the default constants.
        // _models is the same resolution kept for the two readers that run outside a bank pass
        // (SnapshotThroughput on the flush phase, ComputeCapRatePerMin on the investment pass).
        private MachineModel[] _models = MachinePhysics.ResolveAll(null);

        /// <summary>True for the ore-to-ingot tier (Refinery/RefineryII). Used to tick this node's
        /// ingot-producing banks before <see cref="ServiceShipBuildReservations"/> runs, so a ship-build
        /// recipe's reservation snapshot includes ingots this very tick just minted — not just whatever
        /// survived unconsumed from the previous tick. See <see cref="ServiceShipBuildReservations"/>'s
        /// doc comment for the live bug this closes (fresh Ingot:Iron consumed by cheap Manufactory-tier
        /// recipes in the same tick it's refined, before a reservation snapshotted beforehand could ever
        /// see it).</summary>
        private static bool IsIngotProducingTier(NodeCapabilityKind cap)
        {
            return cap == NodeCapabilityKind.Refinery
                || cap == NodeCapabilityKind.RefineryII
                || cap == NodeCapabilityKind.PrototechRefinery;
        }

        // Ceiling on one runnability computation, so a pathologically small per-batch input amount
        // cannot overflow the batch count.
        private const double MaxBatchesCeiling = long.MaxValue / 4.0;

        // Planning horizon for the per-recipe slot cap. A recipe is allowed at most as many machines
        // as its current input can keep busy for this many seconds; anything beyond that is wasted
        // parallelism on a stock that will deplete almost immediately (e.g. 3 cobalt ore must not
        // hold 6 blast furnaces while 1462 iron ore sits idle). Tunable: larger = more machines per
        // recipe before the cap bites; smaller = quicker concentration onto well-supplied recipes.
        private const double SlotInputHorizonSeconds = 10.0;

        // Maximum real-time seconds credited to one machine slot in a single pass, derived from the
        // production interval rather than fixed: a normal pass must credit all of its elapsed time,
        // while a cold start (LastTick == default(DateTime)) or a long server outage stays bounded.
        // A cap below the interval silently scales every machine's output by cap/interval.
        private static double MaxCreditSecondsFor(FactionTuning tuning)
        {
            if (tuning == null) return FactionTuning.DefaultProductionIntervalSeconds * 2.0;
            double intervals = tuning.ProductionCreditCapIntervals > 0 ? tuning.ProductionCreditCapIntervals : 2.0;
            return tuning.ProductionIntervalSecondsOrDefault() * intervals;
        }

        // Per-machine state. Stored as a class so dictionary reads return a reference;
        // mutations are safe without an explicit writeback.
        internal sealed class MachineSlot
        {
            public string   ActiveRecipeId; // null = idle / unassigned
            public double   Accum;          // fractional batches carried over from the last tick
            public DateTime LastTick;       // world.UtcNow when this slot last ran

            public MachineSlot Copy()
            {
                return new MachineSlot
                {
                    ActiveRecipeId = ActiveRecipeId,
                    Accum = Accum,
                    LastTick = LastTick,
                };
            }
        }

        // Key: "{factionTag}:{nodeId}:{capKindIndex}:{slotIndex}"
        // factionTag and nodeId must not contain ':' (SE faction tags = up to 4 uppercase letters;
        // node IDs = "N###" / "E{long}" format ÔÇö neither can contain ':').
        private Dictionary<string, MachineSlot> _slots = new Dictionary<string, MachineSlot>();

        // Key: "{factionTag}:{invKey}". Hysteresis state for PostCatalogDemands: true once stock has
        // reached the item's cap, cleared only once stock drops below cap × (1 - CatalogDemandHysteresis).
        // In-memory only — same transient-scheduling-state pattern as _slots above.
        private Dictionary<string, bool> _catalogDemandSuppressed = new Dictionary<string, bool>();

        // Sub-unit carry for one (faction:node[#buildEntry], recipe, item) triple. Node inventory is
        // whole units while SE keeps fractional quantities in MyFixedPoint, so the fraction of every
        // charge and every yield is banked here and released as soon as it completes a whole unit.
        // Without it a 0.7-ingot-per-batch recipe loses up to one unit on every single production
        // event, which is what made low-yield ores (Cobalt, Silver) run ~23% below their SE ratio.
        // In-memory only, same transient-scheduling-state pattern as _slots above: a restart discards
        // less than one unit per triple.
        private Dictionary<string, double> _remainders = new Dictionary<string, double>();

        // World clock of the most recent pass. SnapshotThroughput runs outside a pass (the host's
        // flush phase) and needs a reference point for the observed-rate window; Core must not read
        // DateTime.UtcNow, so it reports against the last tick it actually saw.
        private DateTime _lastTickUtc;

        // ---- production history (rolling window, in-memory only) -----------------------------
        // Answers "what actually happened, and what consumed this item" over real elapsed time —
        // unlike SnapshotThroughput (a theoretical rate computed from *current* slot assignment),
        // this is real consumption/production, coalesced into spans so a recipe running every tick
        // for minutes shows as one growing row instead of one row per tick.
        private readonly List<ProductionHistoryEntry> _history = new List<ProductionHistoryEntry>();
        private readonly Dictionary<string, ProductionHistoryEntry> _openHistorySpans =
            new Dictionary<string, ProductionHistoryEntry>();
        // How long a completed batch stays visible in the rolling history. Public so the station
        // window can say how far back its History tab reaches without restating the number.
        public const double HistoryRetentionSeconds = 300.0; // 5 minutes
        // A span (same faction+node+recipe+item+direction) keeps growing instead of starting a new
        // row as long as consecutive activity is no more than this many seconds apart -- covers a
        // bank ticking every few seconds without fragmenting into one row per tick, while still
        // splitting into a new row after a real gap (recipe lost its slot, then regained it later).
        private const double HistorySpanGapSeconds = 30.0;

        private readonly Dictionary<NodeCapabilityKind, List<ConversionRecipe>> _capRecipes;

        // Install reservations survive a restart in the brain files, but InvestmentRegister does not
        // -- it is in-memory and TTL-based, so the first tick after a load sees no active request and
        // would release every reservation before the managers have re-posted. DateTime.MinValue (no
        // grace) is the default: only a host that actually boots calls BeginReleaseGrace.
        private DateTime _releaseGraceUntilUtc = DateTime.MinValue;

        // Demand lifetimes are derived from the posting cadence, never fixed. A demand has to outlive
        // the gap to the next sweep or the register is empty when the consumer looks -- and the
        // quantity below is sized off the same span, so a fixed TTL against a longer interval also
        // under-requests inputs in proportion. The x2 floor means one missed sweep does not drop it.
        private static TimeSpan DemandTtlFor(FactionTuning t)
        {
            return TimeSpan.FromSeconds(Math.Max(90.0, ProductionIntervalOf(t) * 2.0));
        }

        private static TimeSpan InvestmentTtlFor(FactionTuning t)
        {
            return TimeSpan.FromSeconds(Math.Max(120.0, ProductionIntervalOf(t) * 2.0));
        }

        private static TimeSpan RefinementDemandTtlFor(FactionTuning t)
        {
            return TimeSpan.FromSeconds(Math.Max(60.0, ProductionIntervalOf(t) * 2.0));
        }

        private static double ProductionIntervalOf(FactionTuning t)
        {
            return t == null ? FactionTuning.DefaultProductionIntervalSeconds : t.ProductionIntervalSecondsOrDefault();
        }

        private static TimeSpan CatalogDemandTtlFor(FactionTuning t)
        {
            return TimeSpan.FromSeconds(Math.Max(180.0, ProductionIntervalOf(t) * 2.0));
        }

        // Urgency posted to InvestmentRegister when ore sits unrefined at a node that lacks
        // the capability to process it. Set to 1.0 (maximum) so the investment request beats
        // Trade for the mercantile personality (merc=0.65):
        //   Develop effective = (0.45 + 0.55 ├ù 0.65) ├ù 1.0 = 0.808
        //   Trade effective   = (0.30 + 0.70 ├ù 0.65) ├ù 1.0 = 0.755
        //   Develop wins ÔåÆ Manufactory / Refinery installs take priority over trading.
        // Bootstrap only: no capability of that tier exists at all, which is a structural gap rather
        // than a measured imbalance, so there is no ratio to ground it in.
        private const double BootstrapInvestmentUrgency = 1.0;

        // "No installed capability can produce this item" -- also structural. Ranked just below a
        // bootstrap and above any ordinary throughput imbalance: a faction that cannot make a thing
        // at all is in a worse position than one making it too slowly, however large that shortfall.
        private const double CapabilityGapInvestmentUrgency = 0.9;
        private const long   RefinementMinQty            = 50;

        // Floor urgency for FactionState.ReservationOptIn keep-alive (see PostReservationKeepAlive).
        // Lower than PostCapabilityInvestmentRequests' 0.6 and PostThroughputInvestmentRequests' 1.0
        // so it never outranks a real reactive signal -- though in practice it never competes on
        // urgency at all, since the keep-alive only posts when no active request already exists for
        // that (faction, cap) this tick.
        private const double ReservationKeepAliveUrgency = 0.2;

        // Pre-built reverse index: invKey ÔåÆ capabilities that can consume it.
        // Sorted by capability tier (lower enum value = simpler machine = lower cost).
        private struct RefinementEntry
        {
            public readonly NodeCapabilityKind Cap;
            public readonly ConversionRecipe   Recipe;
            public RefinementEntry(NodeCapabilityKind c, ConversionRecipe r) { Cap = c; Recipe = r; }
        }
        private readonly Dictionary<string, List<RefinementEntry>> _refinementIndex;

        /// <summary>Per capability, its recipe list indexed by the inventory keys that list can
        /// produce, so a bank reaches the recipes its faction's demand admits without walking the
        /// whole list. Values are positions in that capability's <see cref="_capRecipes"/> list,
        /// ascending, so a candidate set resolved from it arrives in list order and ranks equal
        /// scarcities exactly as a full scan does.
        ///
        /// <para>Only structurally usable recipes are indexed -- inputs, outputs and a positive
        /// <c>TimeSecs</c> -- and only outputs carrying a positive amount, which is the same
        /// admission <see cref="ScarcityPriority"/> applies. <see cref="BankRecipeIndex.UsableCount"/>
        /// is what a bank subtracts its candidate count from to report <c>nodemand</c>, so that
        /// attribution survives without visiting a recipe to produce it.</para></summary>
        private sealed class BankRecipeIndex
        {
            /// <summary>Ordinal, matching the comparer <c>PrepareFaction</c> builds its demanded-key
            /// set with; a looser one here would admit recipes that set would not.</summary>
            public readonly Dictionary<string, List<int>> ByOutputKey =
                new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public int UsableCount;
        }
        private readonly Dictionary<NodeCapabilityKind, BankRecipeIndex> _bankRecipeIndex;

        // _capRecipes' keys in ascending ordinal, so the investment fallback offers the simplest
        // machine that can make the wanted item rather than whichever the dictionary happened to
        // enumerate first. It decides real money: PrototechRefinery outputs the same Ingot: keys as
        // RefineryII, and picked ahead of it would charge a faction a prototech block for ordinary
        // ore refining.
        private readonly List<NodeCapabilityKind> _capsByTier;
        private readonly IFactionTypeCatalogue _factionTypeCatalogue;
        private IPriceOracle _priceOracle;
        private ProductionTickTelemetry _lastTelemetry = new ProductionTickTelemetry();

        /// <summary>Telemetry for the most recently completed production tick. During a resumable
        /// sweep, individual counters update as each checkpoint step runs -- a read between
        /// <see cref="TryAdvance"/> calls can observe a faction's cycle mid-PostPassesNodes with only
        /// some of its nodes' contributions folded in yet. No current caller reads this mid-sweep
        /// (perf.json is only written after a manager-tick round-trip returns), but a future one
        /// should not assume atomicity per faction the way it could before PostPasses was chunked.</summary>
        public ProductionTickTelemetry LastTelemetry { get { return _lastTelemetry; } }
        // Invkeys that appear as outputs in at least one known recipe — catalog demands are filtered

        private readonly HashSet<string> _capRecipeOutputs;

        /// <summary>The <c>Gas:</c> subset of <see cref="_capRecipeOutputs"/>. Its own set because the
        /// surplus-intent pass reaches gas by kind rather than through a faction type's offer list.</summary>
        private readonly HashSet<string> _gasRecipeOutputs;
        private readonly IItemVolumeCatalogue _volumes;

        /// <summary>The world's prefab catalogue, refreshed from <see cref="TryAdvance"/>. Read only
        /// by the fuel reserve pass, to learn which fuel keys a faction's own hulls burn; null (the
        /// offline sim, a test with no catalogue) leaves that pass reading only the keys a node has
        /// already handed out.</summary>
        private IPrefabCatalogue _prefabCatalogue;

        /// <summary>The world, refreshed from <see cref="TryAdvance"/> beside
        /// <see cref="_prefabCatalogue"/>. Written only by the fuel reserve pass, which records the
        /// keys it posts for (<see cref="WorldModel.SetFuelReserveKeys"/>) so the surplus rule keeps
        /// back the same keys.</summary>
        private WorldModel _reserveWorld;

        public ProductionManager(Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes,
                                 IFactionTypeCatalogue factionTypeCatalogue = null,
                                 IPriceOracle priceOracle = null,
                                 IItemVolumeCatalogue itemVolumes = null)
        {
            _capRecipes            = capRecipes ?? new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>();
            _refinementIndex       = BuildRefinementIndex(_capRecipes);
            _bankRecipeIndex       = BuildBankRecipeIndex(_capRecipes);
            _capsByTier            = new List<NodeCapabilityKind>(_capRecipes.Keys);
            _capsByTier.Sort();
            _factionTypeCatalogue  = factionTypeCatalogue;
            _priceOracle           = priceOracle ?? NullPriceOracle.Instance;
            _capRecipeOutputs      = BuildCapRecipeOutputs(_capRecipes);
            _gasRecipeOutputs      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string outputKey in _capRecipeOutputs)
                if (outputKey != null && outputKey.StartsWith(InventoryKeyMapper.GasPrefix, StringComparison.OrdinalIgnoreCase))
                    _gasRecipeOutputs.Add(outputKey);
            _volumes               = itemVolumes;
        }

        public void SetPriceOracle(IPriceOracle priceOracle)
        {
            _priceOracle = priceOracle ?? NullPriceOracle.Instance;
        }

        /// <summary>
        /// Private-to-Production transient effects for one logical tick. Machine scheduling and
        /// catalogue hysteresis are copied before work begins; logs and telemetry are also held
        /// here until the caller accepts the tick. It deliberately does not stage WorldModel writes
        /// yet: the synchronous path below retains its existing world-mutation semantics until the
        /// later complete staged-cycle implementation can atomically publish both boundaries.
        /// </summary>
        internal sealed class TransactionState
        {
            internal readonly ProductionManager Owner;
            internal readonly Dictionary<string, MachineSlot> Slots;
            internal readonly Dictionary<string, bool> CatalogDemandSuppressed;
            internal readonly Dictionary<string, double> Remainders;
            internal readonly List<string> Logs = new List<string>();
            internal readonly ProductionTickTelemetry Telemetry = new ProductionTickTelemetry();
            internal bool Committed;

            internal TransactionState(ProductionManager owner)
            {
                Owner = owner;
                Slots = CopySlots(owner._slots);
                CatalogDemandSuppressed = new Dictionary<string, bool>(owner._catalogDemandSuppressed);
                Remainders = new Dictionary<string, double>(owner._remainders);
            }

            internal void StageSlot(string key, string activeRecipeId, double accum, DateTime lastTick)
            {
                Slots[key] = new MachineSlot
                {
                    ActiveRecipeId = activeRecipeId,
                    Accum = accum,
                    LastTick = lastTick,
                };
            }

            internal void StageCatalogSuppression(string key, bool suppressed)
            {
                CatalogDemandSuppressed[key] = suppressed;
            }

            internal void AddLog(string message)
            {
                if (message != null) Logs.Add(message);
            }

            internal void SetTelemetryFactions(int factions)
            {
                Telemetry.Factions = factions;
            }

            private static Dictionary<string, MachineSlot> CopySlots(Dictionary<string, MachineSlot> source)
            {
                var copy = new Dictionary<string, MachineSlot>(source.Count);
                foreach (KeyValuePair<string, MachineSlot> entry in source)
                    copy.Add(entry.Key, entry.Value == null ? null : entry.Value.Copy());
                return copy;
            }
        }

        private TransactionState BeginTransaction()
        {
            return new TransactionState(this);
        }

        private bool TryCommitTransaction(TransactionState transaction, List<string> log)
        {
            if (transaction == null || transaction.Owner != this || transaction.Committed) return false;

            _slots = transaction.Slots;
            _catalogDemandSuppressed = transaction.CatalogDemandSuppressed;
            _remainders = transaction.Remainders;
            _lastTelemetry = transaction.Telemetry;
            if (log != null) log.AddRange(transaction.Logs);
            transaction.Committed = true;
            return true;
        }

        // This narrow seam is intentionally internal-only. A future staged ProductionCycle will
        // call the same commit operation after its WorldModel effects have passed the guard.
        internal TransactionState BeginTransactionForTesting()
        {
            return BeginTransaction();
        }

        internal bool TryCommitTransactionForTesting(TransactionState transaction,
            ProductionCycleCommitGuard guard, FactionState state, WorldModel world, List<string> log)
        {
            return guard != null && guard.IsStillValid(state, world) && TryCommitTransaction(transaction, log);
        }

        internal bool IsCatalogDemandSuppressedForTesting(string key)
        {
            bool suppressed;
            return _catalogDemandSuppressed.TryGetValue(key, out suppressed) && suppressed;
        }

        private static HashSet<string> BuildCapRecipeOutputs(
            Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (List<ConversionRecipe> recipes in capRecipes.Values)
            {
                if (recipes == null) continue;
                foreach (ConversionRecipe recipe in recipes)
                {
                    if (recipe.Outputs == null) continue;
                    foreach (RecipeItem outp in recipe.Outputs)
                        set.Add(InvKey(outp));
                }
            }
            return set;
        }

        /// <summary>Built once at construction, because <see cref="_capRecipes"/> is fixed for the
        /// manager's lifetime.</summary>
        private static Dictionary<NodeCapabilityKind, BankRecipeIndex> BuildBankRecipeIndex(
            Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            var byCap = new Dictionary<NodeCapabilityKind, BankRecipeIndex>();
            foreach (var capEntry in capRecipes)
            {
                var index = new BankRecipeIndex();
                byCap[capEntry.Key] = index;
                List<ConversionRecipe> recipes = capEntry.Value;
                if (recipes == null) continue;
                for (int i = 0; i < recipes.Count; i++)
                {
                    ConversionRecipe recipe = recipes[i];
                    if (recipe == null) continue;
                    if (recipe.Inputs  == null || recipe.Inputs.Length  == 0) continue;
                    if (recipe.Outputs == null || recipe.Outputs.Length == 0) continue;
                    if (recipe.TimeSecs <= 0f) continue;
                    index.UsableCount++;
                    foreach (RecipeItem outp in recipe.Outputs)
                    {
                        if (outp.Amount <= 0) continue;
                        string key = InvKey(outp);
                        List<int> hits;
                        if (!index.ByOutputKey.TryGetValue(key, out hits))
                        {
                            hits = new List<int>();
                            index.ByOutputKey[key] = hits;
                        }
                        // A recipe naming the same output key twice is one candidate, and the
                        // ascending build order means a repeat can only be the last entry.
                        if (hits.Count == 0 || hits[hits.Count - 1] != i) hits.Add(i);
                    }
                }
            }
            return byCap;
        }

        private static Dictionary<string, List<RefinementEntry>> BuildRefinementIndex(
            Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            var sorted = new List<KeyValuePair<NodeCapabilityKind, List<ConversionRecipe>>>(capRecipes);
            sorted.Sort((a, b) => ((int)a.Key).CompareTo((int)b.Key));

            var index = new Dictionary<string, List<RefinementEntry>>();
            foreach (var capEntry in sorted)
            {
                if (capEntry.Value == null) continue;
                foreach (ConversionRecipe recipe in capEntry.Value)
                {
                    if (recipe.Inputs == null) continue;
                    foreach (RecipeItem inp in recipe.Inputs)
                    {
                        if (string.IsNullOrEmpty(inp.SubTypeId)) continue;
                        string key = InvKey(inp);
                        List<RefinementEntry> list;
                        if (!index.TryGetValue(key, out list))
                        {
                            list = new List<RefinementEntry>();
                            index[key] = list;
                        }
                        list.Add(new RefinementEntry(capEntry.Key, recipe));
                    }
                }
            }
            return index;
        }
 // 24h

        /// <summary>
        /// Runs a full production sweep over <paramref name="states"/> to completion in one call —
        /// today's synchronous contract, preserved as a thin wrapper over <see cref="TryAdvance"/>
        /// with a deadline that never expires. WorldModel effects for each faction are still staged
        /// and guard-checked individually (see <see cref="TryAdvance"/>), but nothing here can
        /// observe a real cross-call suspension: nothing else mutates <paramref name="world"/> during
        /// a single synchronous call, so no cycle should ever need its restart-budget fallback here.
        /// </summary>
        /// <summary>Suppress install-reservation release for <paramref name="graceSeconds"/> from
        /// <paramref name="utcNow"/>. The host calls this once per load: brain files carry
        /// <see cref="SpatialNode.ReservedInventory"/> across a restart but the in-memory, TTL-based
        /// <see cref="InvestmentRegister"/> starts empty, so without the window the first tick reads
        /// every surviving reservation as unbacked and hands it all back to routable inventory a
        /// minute or two before the managers re-post their requests -- visible to players as staged
        /// components briefly evaporating after every restart. Never called = no grace (the
        /// simulator and tests have no boot to speak of).</summary>
        public void BeginReleaseGrace(DateTime utcNow, double graceSeconds)
        {
            _releaseGraceUntilUtc = graceSeconds > 0 ? utcNow.AddSeconds(graceSeconds) : DateTime.MinValue;
        }

        public void Tick(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            var deadline = FrameDeadline.Unbounded();
            while (!TryAdvance(states, world, tuning, deadline, log)) { }
        }

        /// <summary>Per-faction outputs of <see cref="PrepareFaction"/>, threaded through the bank
        /// loop and post-passes. Reused verbatim by <see cref="ProductionCycle"/>'s resumable path so
        /// a suspended cycle resumes against exactly the same prep it started with.</summary>
        internal sealed class FactionCyclePrep
        {
            internal DateTime Now;
            /// <summary>Carried so demand lifetimes can be derived from the production cadence at
            /// every posting site without threading tuning through each signature.</summary>
            internal FactionTuning Tuning;
            /// <summary>Machine physics resolved once at prep time, indexed by NodeCapabilityKind
            /// value. Held here so a resumed cycle keeps the model it began with, the same discipline
            /// the rest of the prep snapshot follows.</summary>
            internal MachineModel[] Models;
            internal double SlotInputHorizonSeconds;
            internal double MaxCreditSeconds;
            internal List<SpatialNode> OwnedNodes;
            internal HashSet<string> OwnedIds;
            internal HashSet<string> DemandedKeys;
            internal Dictionary<string, long> DemandedQuantity;
            /// <summary>Per capability, the recipes this faction's demand admits into a bank this
            /// pass, in recipe-list order. Resolved on a capability's first bank and reused for every
            /// later one: <see cref="DemandedKeys"/> is snapshotted once per faction pass and does
            /// not move while the pass runs, so every bank of a capability sees the same set.</summary>
            internal Dictionary<NodeCapabilityKind, List<ConversionRecipe>> BankCandidates;
            internal HashSet<NodeCapabilityKind> NetworkCaps;
            /// <summary>The route table for this faction's capability set — which recipe actually
            /// produces each key (<see cref="RecipeExpansion.TryGetRoute"/>). Input-demand sizing
            /// reads it so a joint-product recipe is never billed as a source of a key some other
            /// recipe is the chosen route for.</summary>
            internal RecipeExpansion Expansion;
            /// <summary>Per-key outstanding capability-install claims and their ranks, snapshotted
            /// once per faction pass. A ship build ranked below a claim leaves that amount of the key
            /// alone (see <see cref="InstallReserveFloor"/>); empty in the common case where nothing
            /// is being installed.</summary>
            internal Dictionary<string, InstallReserveFloor.Claim> InstallClaims;
            /// <summary>What this faction's Stock orders hold back from lower-ranked builds, installs and
            /// unranked recipes (<see cref="StockClaimFloor"/>); null when they claim nothing.</summary>
            internal StockClaimFloor StockClaims;
            /// <summary>Per node, what that node's outstanding ship builds actually require of each
            /// material, netted against the node's own stock at every level
            /// (<see cref="DemandChain"/>). Caps input demand, which is otherwise sized as the
            /// bank's appetite over its planning horizon and cannot retire (FAC-205).
            ///
            /// <para><b>Node stock, not network stock</b>, and the distinction is not cosmetic. The
            /// posted quantity is what <c>MatchVectors</c> hauls toward this node; netting it
            /// against material the faction happens to hold three jumps away would size the row at
            /// zero and nothing would ever move. Network reach is the right scope for a different
            /// question -- whether a Mine run is needed at all -- and that one is not decided
            /// here.</para>
            ///
            /// <para>A node <b>absent</b> from this map has no commitment, and a key absent from a
            /// node's map has no commitment above it: both are left exactly as they were sized
            /// before. A key present with 0 is covered at a higher level and is genuinely not wanted
            /// here. Install BOMs are deliberately not roots yet -- they are faction-wide while this
            /// is per node -- so an install-only key stays uncapped.</para></summary>
            internal Dictionary<string, Dictionary<string, DemandChain.Requirement>> ChainRequirementByNode;
            /// <summary>True for a player faction, whose farms are real SE blocks planted with real
            /// seeds: none of its crops grows without a seed in stock (<see cref="FarmCrops"/>).</summary>
            internal bool SeedsOnly;
        }

        /// <summary>
        /// Runs one faction's Production pass against <paramref name="ctx"/>. Every WorldModel-facing
        /// effect (inventory, reservation, demand, investment, log) goes through the context, so the
        /// exact same code runs whether <paramref name="ctx"/> is a <see cref="LiveProductionExecutionContext"/>
        /// (today's synchronous behavior) or a <see cref="StagedProductionExecutionContext"/> (a future
        /// resumable cycle). <paramref name="slots"/>/<paramref name="catalogDemandSuppressed"/> are
        /// threaded explicitly rather than read off the shared <see cref="_slots"/>/
        /// <see cref="_catalogDemandSuppressed"/> fields, so a caller (this method, or
        /// <see cref="ProductionCycle"/>'s resumable path) fully controls when staged scheduling state
        /// becomes visible to other readers like <see cref="SnapshotThroughput"/>.
        /// </summary>
        internal void TickOneFaction(FactionState st, IProductionExecutionContext ctx, FactionTuning tuning,
            Dictionary<string, MachineSlot> slots, Dictionary<string, bool> catalogDemandSuppressed,
            Dictionary<string, double> remainders)
        {
            FactionCyclePrep prep = PrepareFaction(st, ctx, tuning, slots, remainders);
            if (prep == null) return; // dormant

            long phaseStart = Stopwatch.GetTimestamp();
            foreach (SpatialNode node in prep.OwnedNodes)
            {
                var itemDemandNeeds = new NodeInputNeeds();
                // Ingot-producing banks (Refinery/RefineryII) tick first so this tick's freshly-minted
                // ingots are on hand -- ServiceShipBuildReservations then reserves recipe inputs into
                // each active BuildQueueEntry's own isolated bucket and runs a dedicated first pass of
                // production against that reserved stock, all before the ingot-consuming banks
                // (Manufactory/ManufactoryII/Shipyard) get a chance to spend those same fresh ingots on
                // ordinary demand. See ServiceShipBuildReservations's doc comment.
                foreach (var capEntry in node.Capabilities)
                    if (IsIngotProducingTier(capEntry.Key))
                        TickOneBank(slots, remainders, st.Tag, node, capEntry, prep, itemDemandNeeds, ctx);
                ServiceShipBuildReservations(st.Tag, node, prep, remainders, ctx);
                foreach (var capEntry in node.Capabilities)
                    if (!IsIngotProducingTier(capEntry.Key))
                        TickOneBank(slots, remainders, st.Tag, node, capEntry, prep, itemDemandNeeds, ctx);
                PostNodeBankDemands(tuning, ctx, st.Tag, node, itemDemandNeeds, prep.Now,
                    ChainRequirementFor(prep, node.Id));
            }
            _lastTelemetry.CapabilityBankTicks += Stopwatch.GetTimestamp() - phaseStart;

            RunPostPasses(slots, st, ctx, tuning, catalogDemandSuppressed, prep);
        }

        /// <summary>
        /// One-shot prep: owned nodes, stale-slot pruning, and the demanded-keys/network-caps snapshots
        /// every later phase reads. Returns null for a dormant faction (nothing else should run).
        /// </summary>
        private FactionCyclePrep PrepareFaction(FactionState st, IProductionExecutionContext ctx,
            FactionTuning tuning, Dictionary<string, MachineSlot> slots,
            Dictionary<string, double> remainders)
        {
            DateTime now = ctx.UtcNow;
            _lastTickUtc = now;
            // No IsAutonomyDisabled check -- see the matching comment in ProductionManager.Cycle.cs.
            if (FactionLifecycle.IsDormant(st, now)) return null;
            _lastTelemetry.Factions++;

            // Runs once per faction per tick regardless of whether anything actually produced --
            // RecordProductionHistory only fires on real activity, so a recipe that goes idle (loses
            // its slot, or the whole bank blocks) would otherwise never trigger a trim and its last
            // span would appear to sit "in window" forever. This is cheap (O(history size), not
            // O(recipes)) and guarantees SnapshotProductionHistory never returns a stale span even
            // when nothing at all is happening for this faction right now.
            TrimHistory(now);

            long phaseStart = Stopwatch.GetTimestamp();
            var ownedNodes = new List<SpatialNode>();
            var ownedIds   = new HashSet<string>();
            foreach (SpatialNode n in ctx.OwnedNodes(st.Tag))
            {
                ownedNodes.Add(n);
                ownedIds.Add(n.Id);
            }
            _lastTelemetry.OwnedNodes += ownedNodes.Count;

            // Network caps only need installed-capability levels (static per node this tick), not the
            // bank loop's results, so they can be built now — the resumable cycle needs them available
            // before the bank loop even if it never revisits this method.
            var networkCaps = new HashSet<NodeCapabilityKind>();
            foreach (SpatialNode n in ownedNodes)
                foreach (var kv in n.Capabilities)
                    if (kv.Value >= 1) networkCaps.Add(kv.Key);

            // Resolve machine physics once per faction pass. Also published to _models for the two
            // readers that run outside a bank pass (SnapshotThroughput, ComputeCapRatePerMin).
            MachineModel[] models = MachinePhysics.ResolveAll(tuning);
            _models = models;
            double horizonSeconds = tuning != null && tuning.ProductionSlotInputHorizonSeconds > 0
                ? tuning.ProductionSlotInputHorizonSeconds : SlotInputHorizonSeconds;
            double maxCreditSeconds = MaxCreditSecondsFor(tuning);

            if (_expansions == null) _expansions = new RecipeExpansionCache(_capRecipes, models);
            RecipeExpansion expansion = _expansions.For(networkCaps);

            // Before the install reservation, which has to leave what a higher-ranked Stock order
            // claims. Published for the shipyard's component absorption, which runs on its own clock.
            StockClaimFloor stockClaims = _reserveWorld == null ? null
                : StockClaimFloor.Build(st, _reserveWorld.Graph, ownedNodes, ctx.GetNodeInventory, tuning, expansion, models);
            if (_reserveWorld != null) _reserveWorld.SetStockClaims(st.Tag, stockClaims);
            if (stockClaims != null && stockClaims.IsEmpty) stockClaims = null;

            // Release is suppressed inside the post-load grace window (see _releaseGraceUntilUtc).
            ReserveInstallStock(st, st.Tag, ownedNodes, ctx, tuning, stockClaims, allowRelease: now >= _releaseGraceUntilUtc);

            // Remove stale slot entries for nodes this faction no longer owns.
            // Prevents reclaimed nodes from inheriting old accumulator state.
            PruneSlots(slots, st.Tag, ownedIds);
            // Size-gated: the ledger only needs bounding, not immediate accuracy, and this runs per
            // faction per pass on the same path as PruneSlots.
            if (remainders != null && remainders.Count >= RemainderPruneThreshold)
                PruneRemainders(remainders, st.Tag, ownedIds);

            // Snapshot demanded output keys for the pull gate. Demands posted later
            // (PROD_INPUT_DEMAND) are invisible until the next tick — bus stays unambiguous.
            // Active ship build queues are durable commitments, so their missing BOM keys also
            // open the gate without depending on the short-lived ShipyardManager demand refresh.
            var demandedKeys = new HashSet<string>();
            // How much of each key is actually wanted, from the same staged snapshot. Input demand
            // used to be sized purely from machine geometry and never consulted the output want that
            // admitted the recipe -- 3,013,200 Ore:Stone asked where 513 gravel was wanted (FAC-164).
            var demandedQty = new Dictionary<string, long>(StringComparer.Ordinal);
            // Standing wants -- a catalogue or store target -- are chain roots beside the ship BOMs.
            // They have to be, or the cap is blind to them: with ship BOMs alone, a node whose build
            // happens to share an input caps that input at the build's requirement and silently
            // starves the shop asking for the same thing.
            //
            // A root must be an ORIGINAL want, never one this chain produced. Two exclusions carry
            // that, and neither is optional:
            //
            //   ChainSized  -- the row is this chain's own output. Rooting on it feeds the chain its
            //                  own answer and compounds every cycle. Live on v0.106.0: N007's
            //                  Ingot:Iron climbed 32,894 -> 76,752 -> 120,610 -> 142,634 over half an
            //                  hour against a BOM requiring 40,610, stopping only at the machine
            //                  appetite that caps it.
            //   ShipBuild   -- ShipyardManager posts one Item: row per outstanding BOM line, which is
            //                  the same commitment the build queue below already contributes. Rooting
            //                  on both counts every ship BOM twice. Correctness hygiene rather than a
            //                  fix for observed behaviour: the pre-existing output-demand cap
            //                  (FAC-164) clamps each level back to what the demand justifies, so the
            //                  double is not visible in a posted quantity and no test here separates
            //                  it. It becomes visible the moment the chain requirement itself is
            //                  exposed rather than only used as a cap -- which FAC-208 will do.
            //
            // RecipeInput is excluded too, as the cascade the chain computes directly -- but it is no
            // longer the load-bearing test, because a chain-reached row is reclassed to what it
            // serves and leaves that class behind.
            //
            // Posters share one Item:{key} slot and DemandRegister.Post is last-write-wins, so a
            // chain-sized write can mask a genuine standing want for the same key at the same node.
            // The catalogue pass runs after the bank loop within a cycle, so it reclaims the slot
            // and the want is rooted on the next pass rather than lost.
            var standingRoots = new Dictionary<string, Dictionary<string, DemandChain.Requirement>>(
                StringComparer.Ordinal);
            foreach (DemandEntry de in ctx.SnapshotItemDemands(st.Tag))
                if (de.Kind.StartsWith("Item:"))
                {
                    string k = de.Kind.Substring(5);
                    demandedKeys.Add(k);
                    long prior;
                    demandedQty.TryGetValue(k, out prior);
                    demandedQty[k] = prior + (de.Quantity > 0 ? de.Quantity : 0);

                    if (de.ChainSized || de.Quantity <= 0) continue;
                    if (de.Class == DemandClass.RecipeInput || de.Class == DemandClass.ShipBuild) continue;
                    // Faction-wide: no node to root it at. An install BOM, the one commitment
                    // posted this way, is placed instead by BuildChainRequirements, from the claim
                    // map rather than from this contended unkeyed row.
                    if (string.IsNullOrEmpty(de.NodeId)) continue;
                    Dictionary<string, DemandChain.Requirement> atNode;
                    if (!standingRoots.TryGetValue(de.NodeId, out atNode))
                    {
                        atNode = new Dictionary<string, DemandChain.Requirement>(StringComparer.Ordinal);
                        standingRoots[de.NodeId] = atNode;
                    }
                    DemandChain.Requirement had;
                    atNode.TryGetValue(k, out had);
                    atNode[k] = new DemandChain.Requirement
                    {
                        Quantity = had.Quantity + de.Quantity,
                        Class    = de.Class > had.Class ? de.Class : had.Class,
                    };
                }
            HashSet<string> activeShipBuildOutputs = ActiveShipBuildOutputKeys(ownedNodes, now);
            foreach (string outputKey in activeShipBuildOutputs) demandedKeys.Add(outputKey);

            Dictionary<string, InstallReserveFloor.Claim> installClaims =
                InstallReserveFloor.ByKey(st, ctx.SnapshotInvestments(st.Tag), ownedNodes, tuning);

            _lastTelemetry.PreparationTicks += Stopwatch.GetTimestamp() - phaseStart;

            return new FactionCyclePrep
            {
                SeedsOnly = st.IsPlayerFaction,
                Now = now,
                Tuning = tuning,
                Models = models,
                SlotInputHorizonSeconds = horizonSeconds,
                MaxCreditSeconds = maxCreditSeconds,
                OwnedNodes = ownedNodes,
                OwnedIds = ownedIds,
                DemandedKeys = demandedKeys,
                DemandedQuantity = demandedQty,
                BankCandidates = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>(),
                NetworkCaps = networkCaps,
                InstallClaims = installClaims,
                StockClaims = stockClaims,
                Expansion = expansion,
                ChainRequirementByNode = BuildChainRequirements(
                    st.Tag, ownedNodes, standingRoots, installClaims, expansion, now, ctx),
            };
        }

        /// <summary>One <see cref="RecipeExpansion"/> per capability set, built on first ask. Keyed on
        /// the capabilities that actually carry recipes, so the live world holds two of these.</summary>
        private RecipeExpansionCache _expansions;

        /// <summary>This node's chain requirement, or null when it carries no commitment — which
        /// every consumer reads as "size it the way it was sized before".</summary>
        private static Dictionary<string, DemandChain.Requirement> ChainRequirementFor(
            FactionCyclePrep prep, string nodeId)
        {
            if (prep == null || prep.ChainRequirementByNode == null) return null;
            Dictionary<string, DemandChain.Requirement> found;
            return prep.ChainRequirementByNode.TryGetValue(nodeId, out found) ? found : null;
        }

        /// <summary>Stands in for a paused yard's queue: its entries are never serviced, so they
        /// are not commitments, but the node's standing wants still are.</summary>
        private static readonly List<BuildQueueEntry> EmptyBuildQueue = new List<BuildQueueEntry>();

        /// <summary>Each node's commitment-rooted material requirement for this pass, for the nodes
        /// that have one. Nodes with no active build are absent rather than empty, so the common
        /// case costs a failed dictionary lookup and no walk at all.
        ///
        /// <para>Roots are the same active ship builds <see cref="ActiveShipBuildOutputKeys"/>
        /// already recognises -- an entry that is paused, expired or waiting for a slot is not
        /// something bank capacity should be sized for, and sizing and gating disagreeing about
        /// which builds count would be worse than either answer -- and the node's repair queue
        /// (<see cref="RepairSupply.AddOutstanding"/>), which that set also covers, plus this node's standing wants
        /// and its <see cref="FactionNeedShare"/> of the faction's outstanding capability-install
        /// BOMs.</para></summary>
        private Dictionary<string, Dictionary<string, DemandChain.Requirement>> BuildChainRequirements(
            string factionTag,
            List<SpatialNode> ownedNodes,
            Dictionary<string, Dictionary<string, DemandChain.Requirement>> standingRoots,
            Dictionary<string, InstallReserveFloor.Claim> installClaims,
            RecipeExpansion expansion, DateTime now, IProductionExecutionContext ctx)
        {
            var byNode = new Dictionary<string, Dictionary<string, DemandChain.Requirement>>(
                StringComparer.Ordinal);
            if (expansion == null) return byNode;

            // Counted once for the whole pass, and only when there is an install to place: this is
            // the divisor FactionNeedShare splits an unpinned need by.
            bool anyInstallClaim = installClaims != null && installClaims.Count > 0;
            int installHolders = anyInstallClaim ? FactionNeedShare.HolderCount(ownedNodes) : 0;

            foreach (SpatialNode node in ownedNodes)
            {
                Dictionary<string, DemandChain.Requirement> roots = null;
                // A paused yard's entries are never serviced, so they are not roots -- but the
                // node's standing wants still are, and are merged below.
                foreach (BuildQueueEntry build in node.ShipyardPaused
                    ? EmptyBuildQueue : node.BuildQueue)
                {
                    if (build == null || build.ExpiresUtc <= now) continue;
                    if (!node.IsActiveBuild(build)) continue;
                    foreach (KeyValuePair<string, long> bomItem in build.Bom)
                    {
                        long collected;
                        build.Collected.TryGetValue(bomItem.Key, out collected);
                        long outstanding = bomItem.Value - collected;
                        if (outstanding <= 0) continue;

                        if (roots == null)
                            roots = new Dictionary<string, DemandChain.Requirement>(StringComparer.Ordinal);
                        DemandChain.Requirement prior;
                        roots.TryGetValue(bomItem.Key, out prior);
                        // ShipBuild is the class the whole chain under this entry inherits: the
                        // components are already sunk into it and a stall refunds them, so leaving
                        // any level of it unmet costs what the build costs.
                        roots[bomItem.Key] = new DemandChain.Requirement
                        {
                            Quantity = prior.Quantity + outstanding,
                            Class    = DemandClass.ShipBuild,
                        };
                    }
                }
                // The node's repair queue roots its chain the way an active build does, and for
                // the same reason: its outstanding bill is posted ShipBuild-class, which the
                // standing-root pass excludes. Nothing for a yard that does not service repairs.
                if (node.DamagedHulls.Count > 0)
                {
                    var repairShort = new Dictionary<string, long>(StringComparer.Ordinal);
                    if (RepairSupply.AddOutstanding(node, repairShort) > 0)
                    {
                        if (roots == null)
                            roots = new Dictionary<string, DemandChain.Requirement>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, long> part in repairShort)
                        {
                            DemandChain.Requirement prior;
                            roots.TryGetValue(part.Key, out prior);
                            roots[part.Key] = new DemandChain.Requirement
                            {
                                Quantity = prior.Quantity + part.Value,
                                Class    = DemandClass.ShipBuild,
                            };
                        }
                    }
                }
                Dictionary<string, DemandChain.Requirement> standing;
                if (standingRoots != null && standingRoots.TryGetValue(node.Id, out standing))
                {
                    if (roots == null)
                        roots = new Dictionary<string, DemandChain.Requirement>(StringComparer.Ordinal);
                    foreach (var want in standing)
                    {
                        DemandChain.Requirement had;
                        roots.TryGetValue(want.Key, out had);
                        roots[want.Key] = new DemandChain.Requirement
                        {
                            Quantity = had.Quantity + want.Value.Quantity,
                            Class    = want.Value.Class > had.Class ? want.Value.Class : had.Class,
                        };
                    }
                }

                // A capability install's BOM is the third commitment kind DemandChain's contract
                // names, and the only one that is faction-wide: an InvestmentRequest carries no node
                // (InvestmentTargeting picks one at enact time), so it raises no node-scoped demand
                // row for the standing-root pass above to place. Sharing the claim across
                // capability-bearing nodes is what puts it in the chain, so the ingots and ore under
                // an install inherit the class the install actually costs.
                //
                // Capability-bearing only, for the reason PriceOracle's identical share carries: an
                // outpost with no industry is not a place a faction gathers an install BOM. The claim
                // map holds BOM-backed capabilities alone (InstallReserveFloor) -- one charged
                // through the flat per-ItemKind cost names no key and so cannot root a chain.
                if (anyInstallClaim && FactionNeedShare.HasAnyCapability(node))
                {
                    foreach (KeyValuePair<string, InstallReserveFloor.Claim> claim in installClaims)
                    {
                        long share = FactionNeedShare.For(claim.Value.Outstanding,
                            ctx.GetNodeInventory(node.Id, claim.Key),
                            ctx.GetFactionInventoryByKey(factionTag, claim.Key), installHolders);
                        if (share <= 0) continue;

                        if (roots == null)
                            roots = new Dictionary<string, DemandChain.Requirement>(StringComparer.Ordinal);
                        DemandChain.Requirement had;
                        roots.TryGetValue(claim.Key, out had);
                        roots[claim.Key] = new DemandChain.Requirement
                        {
                            Quantity = had.Quantity + share,
                            Class    = had.Class > DemandClass.Install ? had.Class : DemandClass.Install,
                        };
                    }
                }
                if (roots == null) continue;

                string nodeId = node.Id;
                byNode[nodeId] = DemandChain.Requirements(roots, expansion,
                    key => ctx.GetNodeInventory(nodeId, key));
            }
            return byNode;
        }

        private static readonly List<ConversionRecipe> NoCandidates = new List<ConversionRecipe>();

        /// <summary>The recipes some demanded output admits into <paramref name="cap"/>'s bank, in
        /// recipe-list order, resolved once per faction pass and cached on
        /// <see cref="FactionCyclePrep.BankCandidates"/>.
        ///
        /// <para>The walk is over the demand set through <see cref="_bankRecipeIndex"/>, not over the
        /// recipe list: a recipe no demand names is turned away by the bank's first gate, so reaching
        /// it costs a <see cref="ScarcityPriority"/> call per bank per tick to learn nothing. On
        /// ManufactoryII that is the majority of the list. Nothing is excluded from the index itself
        /// -- a recipe becomes a candidate the tick something demands its output.</para>
        ///
        /// <para>Order is the recipe list's, not the demand set's, because the bank ranks equal
        /// scarcities by which it saw first.</para></summary>
        private List<ConversionRecipe> BankCandidatesFor(FactionCyclePrep prep, NodeCapabilityKind cap)
        {
            List<ConversionRecipe> cached;
            if (prep.BankCandidates.TryGetValue(cap, out cached)) return cached;
            cached = ResolveBankCandidates(cap, prep.DemandedKeys);
            prep.BankCandidates[cap] = cached;
            return cached;
        }

        private List<ConversionRecipe> ResolveBankCandidates(NodeCapabilityKind cap, HashSet<string> demandedKeys)
        {
            if (demandedKeys == null || demandedKeys.Count == 0) return NoCandidates;
            List<ConversionRecipe> recipes;
            if (!_capRecipes.TryGetValue(cap, out recipes) || recipes == null) return NoCandidates;
            BankRecipeIndex index;
            if (!_bankRecipeIndex.TryGetValue(cap, out index)) return NoCandidates;

            List<int> picked = null;
            foreach (string key in demandedKeys)
            {
                List<int> hits;
                if (!index.ByOutputKey.TryGetValue(key, out hits)) continue;
                if (picked == null) picked = new List<int>();
                picked.AddRange(hits);
            }
            if (picked == null) return NoCandidates;

            // A recipe with two demanded outputs was picked twice; sorting puts its duplicates
            // adjacent, which both restores list order and makes the de-duplication a scan.
            picked.Sort();
            var candidates = new List<ConversionRecipe>(picked.Count);
            int previous = -1;
            for (int i = 0; i < picked.Count; i++)
            {
                if (picked[i] == previous) continue;
                previous = picked[i];
                candidates.Add(recipes[previous]);
            }
            return candidates;
        }

        /// <summary>One capability bank at one node — the bounded unit a resumable cycle checkpoints
        /// between. No-ops for an unset/unrecognised/zero-speed capability entry.</summary>
        private void TickOneBank(Dictionary<string, MachineSlot> slots,
            Dictionary<string, double> remainders, string factionTag, SpatialNode node,
            KeyValuePair<NodeCapabilityKind, int> capEntry, FactionCyclePrep prep,
            NodeInputNeeds itemDemandNeeds,
            IProductionExecutionContext ctx)
        {
            if (capEntry.Value < 1) return;
            NodeCapabilityKind cap    = capEntry.Key;
            int                capIdx = (int)cap;
            if (capIdx < 0 || capIdx >= prep.Models.Length) return;
            MachineModel model = prep.Models[capIdx];
            int baseCount = model.MachineCount;
            if (baseCount <= 0 || model.Speed <= 0) return;
            // Level > 1 = capability upgrade: each additional level adds baseCount slots.
            int level        = capEntry.Value;
            int machineCount = baseCount * level;

            List<ConversionRecipe> recipes;
            if (!_capRecipes.TryGetValue(cap, out recipes) || recipes == null) return;
            _lastTelemetry.CapabilityBanks++;
            List<ConversionRecipe> candidates = BankCandidatesFor(prep, cap);
            BankRecipeIndex index;
            int noDemandRecipes = _bankRecipeIndex.TryGetValue(cap, out index)
                ? index.UsableCount - candidates.Count
                : 0;
            // A Farm's candidates are faction-wide like every bank's, but whether a crop grows is
            // a fact about this node: its body and its seed stock. Filtered here, after the shared
            // candidate set, so the per-faction cache is never narrowed to one node's answer.
            if (cap == NodeCapabilityKind.Farm)
            {
                string nodeId = node.Id;
                candidates = FarmCrops.GrowableAt(candidates, node, FarmCrops.NativeAt(node, ctx.Planets),
                    prep.SeedsOnly, key => ctx.GetNodeInventory(nodeId, key));
            }
            TickCapabilityBank(prep.Tuning, slots, remainders, factionTag, node, capIdx, machineCount, model,
                recipes, candidates, noDemandRecipes, prep.DemandedKeys, prep.DemandedQuantity,
                prep.Expansion, prep.InstallClaims, prep.StockClaims,
                itemDemandNeeds, ctx, prep.Now, prep.SlotInputHorizonSeconds, prep.MaxCreditSeconds);
        }

        /// <summary>Runs once per node per tick, after that node's ingot-producing banks (Refinery/
        /// RefineryII) have ticked but before its ingot-consuming banks (Manufactory/ManufactoryII/
        /// Shipyard) do — see <see cref="IsIngotProducingTier"/> and the two-pass loop in
        /// <see cref="TickOneFaction"/>/<see cref="BuildBankUnits"/> that enforces this ordering. For
        /// every active (unexpired) <see cref="BuildQueueEntry"/> at <paramref name="node"/> with
        /// outstanding BOM, reserves recipe inputs into that entry's own isolated
        /// <see cref="BuildQueueEntry.ReservedInputs"/> bucket — physically out of routable
        /// <c>Inventory</c>, the same isolation <see cref="ReserveInstallStock"/> already uses for
        /// capability-install stock and <see cref="ShipyardManager.ServiceEntry"/> already uses for
        /// collected components — then immediately runs a dedicated first pass of production against
        /// that reserved stock, crediting output straight into <see cref="BuildQueueEntry.Collected"/>.
        /// Only once this returns does the ordinary per-bank scarcity/fair-share loop
        /// (<see cref="TickCapabilityBank"/>) run for the ingot-consuming banks, completely unaware any
        /// of this happened — it only ever sees <c>Inventory</c> already net of whatever this pass
        /// pulled into buckets.
        ///
        /// <para><b>The tier ordering above is load-bearing, not cosmetic — an earlier version of this
        /// pass ran before ANY of a node's capability banks, including the very Refinery bank that
        /// mints the ingots it exists to protect.</b> Live-observed on IGSL <c>E907737196391231441</c>:
        /// with reservation and all capability banks flattened into one <c>node.Capabilities</c> loop,
        /// the reservation snapshot only ever saw whatever `Ingot:Iron` survived UNCONSUMED from the
        /// *previous* tick — essentially nothing, because `InteriorPlate`/`SmallTube`/`GirderComponent`
        /// (ordinary Manufactory-tier recipes sharing the same input) ran in that same flattened loop,
        /// moments after Refinery minted a fresh 44 units, and consumed ~90% of it before the *next*
        /// tick's reservation pass ever got a look. Production History showed this directly:
        /// `IronOreToIngot` producing 44 `Ingot:Iron` essentially every cycle, `InteriorPlate`(18)+
        /// `SmallTube`(15)+`GirderComponent`(6)=39 of it consumed the same cycle, and the `SteelPlate`
        /// ship-build recipe firing exactly once in a 5-minute window despite the "reserve the full
        /// remaining need" fix above already being live — the greedy target sizing was correct but
        /// structurally starved of anything to reserve, since ingots were gone before the *next* tick's
        /// snapshot could ever claim them. Splitting the tier order so ingot production happens first,
        /// then reservation, then ingot consumption closes this for good: the reservation pass now sees
        /// the exact same tick's fresh mint, before any Manufactory-tier recipe gets a chance at it.</para>
        ///
        /// <para>Replaces the old <c>ComputeReservedInputs</c> floor-subtraction approach: that only
        /// ever withheld one bare batch's worth of input from ordinary recipes' view, while the
        /// withheld stock still physically sat in routable <c>Inventory</c> — nothing capped how much
        /// of the *surplus above that floor* a cheap recipe's already-accumulated machine-time could
        /// sweep in one shot (live-observed on IGSL: <c>Comp:SteelPlate</c> permanently starved behind
        /// <c>Comp:Construction</c>/<c>Comp:MetalGrid</c>/<c>Comp:PowerCell</c> despite three rounds of
        /// floor fixes — see docs/todo.md's "priority-allocation pass, not a fixed one-batch floor"
        /// entry). Real isolation — stock genuinely absent from <c>Inventory</c>, not just a
        /// subtraction check another recipe's <see cref="MaxRunnableBatches"/> call has to remember to
        /// honor — closes that gap for good: once reserved, no other recipe or consumer can ever see or
        /// take it, so surplus can accumulate across many ticks toward a large order instead of being
        /// capped at one batch.</para>
        ///
        /// <para>The reservation top-up targets the entry's <b>full remaining need</b> (every batch
        /// still outstanding on the BOM), not a slice sized to the recipe's own machine-pacing — a
        /// pacing-sized cap was tried first and was wrong: whenever the horizon window rounded down to
        /// ~1 batch (any recipe whose <c>TimeSecs</c> is large relative to the horizon), the bucket
        /// only ever topped up to exactly one batch's worth before the run phase drained it, so real
        /// surplus above that one batch sat back in plain <c>Inventory</c> every tick — un-isolated and
        /// exposed to whatever cheap recipe's <c>Accum</c> happened to fire that tick, reproducing the
        /// original starvation this whole redesign exists to fix, just moved one layer deeper. Since
        /// <see cref="IProductionExecutionContext.MoveInventoryToBuildReserved"/> already caps the
        /// actual move at whatever's currently in <c>Inventory</c>, targeting the full remaining need
        /// is safe — it only ever reserves what genuinely exists, greedily, every tick, rather than
        /// artificially throttling the claim. This deliberately lets one ship-build order claim 100% of
        /// a shared input's current supply over unrelated demanded recipes until its own need is met —
        /// the whole point of a priority reservation, not an accident. Multiple entries at one node
        /// wanting the same input key each reserve independently against whatever's left in
        /// <c>Inventory</c> at the time they run (in <c>BuildQueue</c> list order), so they sum rather
        /// than clobber each other's reservation, but an earlier entry can still fully claim a scarce
        /// input ahead of a later one sharing it — see docs/todo.md's live-verification note on
        /// `E907737196391231441` for the observed cross-entry-fairness follow-up this implies.</para>
        /// </summary>
        private void ServiceShipBuildReservations(string factionTag, SpatialNode node,
            FactionCyclePrep prep, Dictionary<string, double> remainders, IProductionExecutionContext ctx)
        {
            DateTime now = prep.Now;
            if (node.ShipyardPaused) return;
            foreach (BuildQueueEntry entry in EntriesByPriority(node))
            {
                if (entry.ExpiresUtc <= now || entry.Bom.Count == 0) continue;
                // A build waiting for a slot converts nothing, so reserving raw inputs toward its BOM
                // would hold them out of routable stock for a need nobody is working on.
                if (!node.IsActiveBuild(entry)) continue;

                foreach (KeyValuePair<string, long> bomItem in entry.Bom)
                {
                    string outputKey = bomItem.Key;
                    long outstanding = bomItem.Value - ctx.GetBuildCollected(node.Id, entry.Id, outputKey);
                    if (outstanding <= 0) continue;

                    foreach (KeyValuePair<NodeCapabilityKind, int> capEntry in node.Capabilities)
                    {
                        if (capEntry.Value < 1 || outstanding <= 0) continue;
                        int capIdx = (int)capEntry.Key;
                        if (capIdx < 0 || capIdx >= prep.Models.Length) continue;
                        MachineModel model = prep.Models[capIdx];
                        if (model.Speed <= 0) continue;

                        List<ConversionRecipe> recipes;
                        if (!_capRecipes.TryGetValue(capEntry.Key, out recipes) || recipes == null) continue;

                        foreach (ConversionRecipe recipe in recipes)
                        {
                            if (outstanding <= 0) break;
                            if (recipe.Inputs == null || recipe.Inputs.Length == 0) continue;
                            if (recipe.TimeSecs <= 0f) continue;
                            double outputPerBatch = OutputAmountFor(recipe, outputKey);
                            if (outputPerBatch <= 0) continue;

                            outstanding = RunShipBuildRecipe(factionTag, node, entry, recipe, outputKey,
                                outstanding, outputPerBatch, model, now, prep, remainders, ctx);
                        }
                    }
                }
            }
        }

        /// <summary>One recipe's contribution to <see cref="ServiceShipBuildReservations"/> for one
        /// entry/output-key pair: reserve/top-up, then run. Returns the entry's still-outstanding need
        /// for <paramref name="outputKey"/> after this recipe ran (so the caller can stop scanning
        /// further recipes once satisfied).</summary>
        private long RunShipBuildRecipe(string factionTag, SpatialNode node, BuildQueueEntry entry,
            ConversionRecipe recipe, string outputKey, long outstanding, double outputPerBatch,
            MachineModel model, DateTime now, FactionCyclePrep prep,
            Dictionary<string, double> remainders, IProductionExecutionContext ctx)
        {
            double maxCreditSeconds = prep.MaxCreditSeconds;
            long remainingBatchesNeeded = (long)Math.Ceiling(outstanding / (outputPerBatch * model.OutputScale));
            if (remainingBatchesNeeded <= 0) return outstanding;
            string buildScope = BuildRemainderScope(factionTag, node.Id, entry.Id);

            // ---- reserve/top-up: target the entry's FULL remaining need, not a pacing-sized
            // slice -- MoveInventoryToBuildReserved already caps the actual move at whatever's
            // currently in Inventory, so this greedily claims 100% of what's available every tick
            // rather than throttling the claim to what the recipe's own machine-pacing could use
            // this tick. See this method's doc comment for why a horizon-paced cap was wrong. ----
            foreach (RecipeItem inp in recipe.Inputs)
            {
                if (inp.Amount <= 0) continue;
                string invKey = InvKey(inp);
                long target  = (long)Math.Ceiling(remainingBatchesNeeded
                        * MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier));
                long already = ctx.GetBuildReservedInput(node.Id, entry.Id, invKey);
                long need    = target - already;
                if (need <= 0) continue;

                // Leave what a stronger claim on this same key is owed: another entry at this node
                // whose Priority outranks ours and still has the key outstanding on its own BOM, any
                // capability install ranked above us, and any Stock order ranked above us. All are zero
                // at the default ranks, so the greedy full-remaining-need claim below is unchanged
                // until someone sets a priority.
                long floor = node.BuildQueueReserved(invKey, now, entry.Priority + 1)
                           + InstallReserveFloor.For(prep.InstallClaims, invKey, entry.Priority)
                           + StockFloor(prep.StockClaims, node.Id, invKey, entry.Priority);
                if (floor > 0)
                {
                    long claimable = ctx.GetNodeInventory(node.Id, invKey) - floor;
                    if (claimable <= 0) continue;
                    if (need > claimable) need = claimable;
                }

                long moved = ctx.MoveInventoryToBuildReserved(node.Id, entry.Id, invKey, need);
                if (moved > 0)
                    ctx.AddLog(factionTag + " SHIPBUILD_RESERVE " + invKey + " +" + moved
                            + " entry=" + entry.Id + " node=" + node.Id);
            }

            // ---- run: this entry's own dedicated accumulator, paced identically to a real
            // MachineSlot but never contending with the bank's own machineCount. ----
            DateTime lastTick;
            double accum = ctx.GetBuildRecipeAccum(node.Id, entry.Id, recipe.BlueprintId, out lastTick);
            // Matches MachineSlot's own cold-start handling: a never-ticked slot's LastTick defaults to
            // DateTime.MinValue, so (now - lastTick) is huge and clamps straight to the credit window --
            // deliberately not a special-cased zero (see ClearCapabilitySlots's doc comment).
            double elapsed = Math.Min((now - lastTick).TotalSeconds, maxCreditSeconds);
            accum += elapsed * model.Speed / recipe.TimeSecs;

            long batches = (long)Math.Floor(accum);
            accum -= batches;
            batches = Math.Min(batches, remainingBatchesNeeded);

            if (batches > 0)
            {
                long runnable = MaxRunnableFromReserved(node, entry, recipe, model, remainders, buildScope, ctx);
                if (runnable < batches)
                {
                    // Preserve unspent elapsed-time progress, same discipline TickCapabilityBank uses.
                    accum += (batches - runnable);
                    batches = runnable;
                }
            }
            ctx.SetBuildRecipeProgress(node.Id, entry.Id, recipe.BlueprintId, accum, now);
            if (batches <= 0) return outstanding;

            foreach (RecipeItem inp in recipe.Inputs)
            {
                if (inp.Amount <= 0) continue;
                string invKey = InvKey(inp);
                long qty = AccrueWhole(remainders, buildScope, recipe.BlueprintId, invKey,
                    batches * MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier));
                if (qty <= 0) continue;
                ctx.AddBuildReservedInput(node.Id, entry.Id, invKey, -qty);

                double litersPerUnit = _volumes != null ? _volumes.VolumeLitersPerUnit(invKey) : 0;
                double weighted = litersPerUnit > 0 ? qty * litersPerUnit : qty;
                ctx.AddPendingDemandHistoryCredit(node.Id, invKey, weighted);
                RecordProductionHistory(factionTag, node.Id, recipe.BlueprintId, invKey, qty, false, now);
            }

            if (recipe.Outputs != null)
                foreach (RecipeItem outp in recipe.Outputs)
                {
                    if (outp.Amount <= 0) continue;
                    string outKey = InvKey(outp);
                    long produce = AccrueWhole(remainders, buildScope, recipe.BlueprintId, outKey,
                        batches * outp.Amount * model.OutputScale);
                    if (produce <= 0) continue;

                    long stillNeeded = 0;
                    long bomNeed;
                    if (entry.Bom.TryGetValue(outKey, out bomNeed))
                        stillNeeded = Math.Max(0, bomNeed - ctx.GetBuildCollected(node.Id, entry.Id, outKey));

                    long toCollected = Math.Min(produce, stillNeeded);
                    long overflow    = produce - toCollected;
                    if (toCollected > 0) ctx.AddBuildCollected(node.Id, entry.Id, outKey, toCollected);
                    if (overflow > 0) ctx.AddNodeInventory(node.Id, outKey, overflow);

                    RecordProductionHistory(factionTag, node.Id, recipe.BlueprintId, outKey, produce, true, now);
                    ctx.AddLog(factionTag + " SHIPBUILD_PRODUCED " + outKey + " +" + produce
                            + " entry=" + entry.Id + " from=" + batches + "x" + recipe.BlueprintId + " node=" + node.Id);
                }

            return Math.Max(0, entry.Bom[outputKey] - ctx.GetBuildCollected(node.Id, entry.Id, outputKey));
        }

        /// <summary>Max batches <paramref name="recipe"/> could run right now against only
        /// <paramref name="entry"/>'s own isolated <see cref="BuildQueueEntry.ReservedInputs"/> bucket
        /// — the <see cref="BuildQueueEntry"/>-scoped counterpart to <see cref="MaxRunnableBatches"/>.</summary>
        private static long MaxRunnableFromReserved(SpatialNode node, BuildQueueEntry entry,
            ConversionRecipe recipe, MachineModel model, Dictionary<string, double> remainders,
            string scope, IProductionExecutionContext ctx)
        {
            if (recipe.Inputs == null || recipe.Inputs.Length == 0) return 0;
            long max = long.MaxValue;
            bool anyInput = false;
            foreach (RecipeItem inp in recipe.Inputs)
            {
                if (inp.Amount <= 0) continue;
                anyInput = true;
                string invKey = InvKey(inp);
                long have = ctx.GetBuildReservedInput(node.Id, entry.Id, invKey);
                if (have <= 0) return 0;
                double perBatch = MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                if (perBatch <= 0) continue;
                double avail = have - CarriedRemainder(remainders, scope, recipe.BlueprintId, invKey);
                long canRun = avail > 0 ? (long)Math.Floor(Math.Min(avail / perBatch, MaxBatchesCeiling)) : 0;
                if (canRun < max) max = canRun;
            }
            if (!anyInput) return 0;
            return max == long.MaxValue ? 0 : max;
        }

        /// <summary>The per-batch yield of <paramref name="recipe"/> for one specific output key, or 0
        /// if the recipe doesn't produce it.</summary>
        private static double OutputAmountFor(ConversionRecipe recipe, string outputKey)
        {
            if (recipe.Outputs == null) return 0;
            foreach (RecipeItem outp in recipe.Outputs)
                if (outp.Amount > 0 && InvKey(outp) == outputKey) return outp.Amount;
            return 0;
        }

        /// <summary>Last quantity logged per (faction, node, inventory key), so
        /// <see cref="PostNodeBankDemands"/> can report a refresh that moves the size rather than only
        /// the first post. Same cardinality as the node's own demand rows, and reset with the manager.
        /// </summary>
        private readonly Dictionary<string, long> _lastLoggedInputNeed =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>One node's input needs for this tick, as every bank's recipes contributed them.
        /// <para>Two combinations, because they answer different rows. <see cref="Max"/> is the
        /// largest single contribution per input: a contribution is a machine appetite, sized as if
        /// its recipe held the whole bank, and two of them are not twice the machines. That sizes an
        /// unchained row. <see cref="Sum"/> adds the recipes together: each contribution is also
        /// capped by its own recipe's output demand, and output demands add. A chain-bound row is
        /// capped by the sum, because the chain it is checked against sums the same demands and still
        /// bounds the row. Capping it by the max dropped every share but the largest. A build's 5,000
        /// <c>Ingot:Iron</c> hid a catalogue want's 100 at the same node (FAC-468).</para></summary>
        internal sealed class NodeInputNeeds
        {
            public readonly Dictionary<string, long> Max = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> Sum = new Dictionary<string, long>(StringComparer.Ordinal);

            public void Add(string inputKey, long needed)
            {
                if (needed <= 0) return;
                long existing;
                Max.TryGetValue(inputKey, out existing);
                if (needed > existing) Max[inputKey] = needed;
                Sum.TryGetValue(inputKey, out existing);
                Sum[inputKey] = existing + needed;
            }
        }

        /// <summary>Posts one node's consolidated PROD_INPUT_DEMAND set once every bank at that node
        /// has run, combining contributions as <see cref="NodeInputNeeds"/> describes.
        ///
        /// <para>Two caps, and which one applies is decided by whether a commitment leads to the key.
        /// A chain-bound row is sized by the commitment (<see cref="TryGetBindingChain"/>). An
        /// unchained row is sized by machine geometry, which counts installed machines rather than
        /// throughput and had no bound at all: live, a node whose measured consumption justified
        /// 443,234 <c>Ore:Stone</c> posted 12,252,000. It is capped at
        /// <see cref="IPriceOracle.TryGetMeasuredTargetStock"/> — the same per-node target every
        /// store price, buy order and contract reward reads, whose flow term is what the node
        /// actually consumes.</para>
        ///
        /// <para><b>Measured, not the floor.</b> A target standing on the per-kind floor alone is a
        /// bootstrap value sized to open the scarcity gate, and reading it as an order size asks for
        /// 60 of an ingot a refinery consumes by the thousand. Live on v0.277.0 that pinned 462 of
        /// 956 capped rows at exactly 60, <c>Ingot:Iron</c> among them.</para>
        ///
        /// <para>The cap is not applied to a chain-bound row. A commitment is a burst the flow term
        /// cannot have observed — a hull's ore is one level below a BOM the target's <c>owed</c> term
        /// counts in ingots — and the chain already bounds it by the real requirement.</para>
        ///
        /// <para>No measured target is "no opinion", not "want none", and passes the row through: a
        /// node that has never consumed the key has no flow to read, and capping it there would stop
        /// it ever bootstrapping.</para></summary>
        private void PostNodeBankDemands(FactionTuning tuning, IProductionExecutionContext ctx, string factionTag,
            SpatialNode node, NodeInputNeeds itemDemandNeeds, DateTime now,
            Dictionary<string, DemandChain.Requirement> chainRequirement)
        {
            foreach (var kv in itemDemandNeeds.Max)
            {
                if (kv.Value <= 0) continue;

                DemandChain.Requirement chain;
                bool chained = TryGetBindingChain(kv.Key, chainRequirement, out chain);

                // The appetite this row is sized from: the largest single recipe's for an unchained
                // row, every recipe's together for a chain-bound one (see NodeInputNeeds).
                long appetite = chained ? itemDemandNeeds.Sum[kv.Key] : kv.Value;
                long need = appetite;
                long target;
                bool targetBound = false;
                if (chained)
                {
                    if (chain.Quantity < need) need = chain.Quantity;
                }
                else if (_priceOracle.TryGetMeasuredTargetStock(node.Id, kv.Key, out target) && target > 0 && target < need)
                {
                    need = target;
                    targetBound = true;
                }
                if (need <= 0) continue;

                // A chained input is classed by the commitment it serves, not by what it locally is.
                // Stamping RecipeInput on the ore a stalled hull is waiting for ranked the cause at
                // half the weight of its own symptom, and level with every background input in the
                // network (FAC-205).
                DemandClass demandClass = chained ? chain.Class : DemandClass.RecipeInput;

                bool isNew = PostDemand(ctx, new DemandEntry
                {
                    FactionTag = factionTag,
                    Kind       = "Item:" + kv.Key,
                    NodeId     = node.Id,
                    ExpiresUtc = now.Add(DemandTtlFor(tuning)),
                    Quantity   = need,
                    Class      = demandClass,
                    ChainSized = chained,
                });
                // Log a new row, and any refresh that moves the quantity by 2x or more. `isNew`
                // alone made every later size of a row invisible: the register can hold a value ten
                // times what the last logged line says, with nothing naming what posted it.
                // The posted size, never the pre-cap one -- a line that reports what the bank wanted
                // rather than what it asked for cannot show the cap working or failing.
                string logKey = factionTag + ":" + node.Id + ":" + kv.Key;
                long prev;
                bool known = _lastLoggedInputNeed.TryGetValue(logKey, out prev);
                bool jumped = known && (need >= prev * 2 || need * 2 <= prev);
                if (isNew || !known || jumped)
                {
                    ctx.AddLog(factionTag + " PROD_INPUT_DEMAND " + kv.Key
                            + " need=" + need
                            + " cls=" + demandClass
                            + (need != appetite ? " appetite=" + appetite : "")
                            + (need != appetite && !targetBound ? " chain=capped" : "")
                            + (targetBound ? " target=capped" : "")
                            + (known ? " prev=" + prev : "")
                            + " node=" + node.Id);
                    _lastLoggedInputNeed[logKey] = need;
                }
            }
        }

        /// <summary>Whether a chain actually binds this input — a commitment leads to it AND still
        /// wants some of it. Both the quantity cap and the class inheritance hang off this one
        /// predicate, so they can never disagree about which rows the chain speaks for.
        ///
        /// <para>Three cases return false and leave the row exactly as it was sized and classed
        /// before: no chain at this node, the key absent from it, and the key present at zero.
        /// <b>None of them is evidence that nothing wants the key</b> — catalog and store demands
        /// admit recipes of their own and their blocked inputs land in the same accumulator, so
        /// treating a zero as "do not ask" would starve production the chain was never entitled to
        /// speak for. The frontier therefore rises as a shrinking number rather than a vanishing
        /// row; closing that needs standing demand accounted for beside the commitment, which is
        /// docs/demand-chains.md open question 2 and not this work.</para></summary>
        private static bool TryGetBindingChain(string invKey,
            Dictionary<string, DemandChain.Requirement> chainRequirement,
            out DemandChain.Requirement requirement)
        {
            requirement = default(DemandChain.Requirement);
            if (chainRequirement == null || chainRequirement.Count == 0) return false;
            if (!chainRequirement.TryGetValue(invKey, out requirement)) return false;
            return requirement.Quantity > 0;
        }

        /// <summary>Investment/refinement/throughput/install/founding/catalog passes — one bounded
        /// unit. Cheaper than the bank loop (O(networkCaps) / O(ownedNodes), not O(nodes x recipes)),
        /// so it is not split further in this first resumable slice.</summary>
        private void RunPostPasses(Dictionary<string, MachineSlot> slots, FactionState st,
            IProductionExecutionContext ctx, FactionTuning tuning,
            Dictionary<string, bool> catalogDemandSuppressed, FactionCyclePrep prep)
        {
            long phaseStart = Stopwatch.GetTimestamp();
            // Order no longer decides which urgency survives for a shared (factionTag, cap):
            // InvestmentRegister.Post keeps the stronger claim within a tick, because a throughput
            // urgency is now a measured ratio and a small one must not erase a structural
            // capability-gap request for the same capability.
            _lastTelemetry.InvestmentPasses++;
            PostCapabilityInvestmentRequests(st, prep.NetworkCaps, ctx, tuning);
            _lastTelemetry.RefinementPasses++;
            PostRefinementRequests(st.Tag, prep.NetworkCaps, prep.OwnedNodes, ctx, tuning);
            _lastTelemetry.InvestmentPasses++;
            PostThroughputInvestmentRequests(slots, st, prep.NetworkCaps, prep.OwnedNodes, ctx, tuning);
            PostReservationKeepAlive(tuning, st, ctx);
            PostInstallCostDemandsForInvestmentRequests(st.Tag, prep.NetworkCaps, ctx, tuning);
            PostFoundingCostDemands(st, prep.NetworkCaps, ctx, tuning);
            PostFuelReserveDemands(st.Tag, prep.OwnedNodes, ctx, tuning);
            PostMunitionReserveDemands(st.Tag, prep.OwnedNodes, ctx, tuning);
            PostWorkerFoodDemands(st.Tag, prep.NetworkCaps, prep.OwnedNodes, ctx, tuning);
            PostStockOrderDemands(st, ctx, tuning);
            _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;

            phaseStart = Stopwatch.GetTimestamp();
            PostCatalogDemands(st.Tag, ctx, tuning, prep.Now, catalogDemandSuppressed);
            _lastTelemetry.CatalogTicks += Stopwatch.GetTimestamp() - phaseStart;
        }

        /// <summary>Keeps an <see cref="InvestmentRequest"/> alive for every capability in
        /// <see cref="FactionState.ReservationOptIn"/> that no reactive pass
        /// (<see cref="PostCapabilityInvestmentRequests"/>/<see cref="PostThroughputInvestmentRequests"/>)
        /// already covered this tick -- without this, opting in to a capability with no unmet Item
        /// demand and no measured throughput imbalance does nothing forever, since
        /// <see cref="ReserveInstallStock"/>/<see cref="ReserveInstallBomStock"/> only ever stage
        /// material toward an active request. A true no-op for NPC factions (<c>ReservationOptIn</c>
        /// is only ever populated for a player faction) and never touches the brain's strategic loop
        /// (already disabled for player factions) -- it only ever feeds this manager's own
        /// reservation claiming. Must run after both reactive posting passes so
        /// <see cref="IProductionExecutionContext.SnapshotInvestments"/> reflects what they already
        /// did this tick: <see cref="InvestmentRegister"/> overwrites unconditionally on repost ("last
        /// post wins"), so posting here first would silently downgrade a legitimate higher-urgency
        /// reactive request to the keep-alive's floor urgency the same tick.
        /// <para>Reposts every tick to refresh the TTL. Only a genuinely new entry is logged, so a
        /// steady opt-in stays quiet.</para></summary>
        private static void PostReservationKeepAlive(FactionTuning tuning, FactionState st, IProductionExecutionContext ctx)
        {
            if (!st.IsPlayerFaction || st.ReservationOptIn.Count == 0) return;

            // Only a *higher-urgency* reactive request blocks a repost ("last post wins" would
            // downgrade it to the floor). The keep-alive's own standing entry must be reposted every
            // tick: Post is the only thing that extends ExpiresUtc, so skipping it lets the entry age
            // out at InvestmentTtl, and the next tick's ReserveInstallStock reads an empty register
            // and hands the whole staged BOM back to routable inventory before this pass runs again.
            var claimedByReactive = new HashSet<NodeCapabilityKind>();
            foreach (InvestmentRequest r in ctx.SnapshotInvestments(st.Tag))
                if (r.TargetPlanetId == null && r.Urgency > ReservationKeepAliveUrgency)
                    claimedByReactive.Add(r.Cap);

            foreach (NodeCapabilityKind cap in st.ReservationOptIn)
            {
                if (claimedByReactive.Contains(cap)) continue;
                bool isNew = ctx.PostInvestment(new InvestmentRequest
                {
                    FactionTag = st.Tag,
                    Cap        = cap,
                    Urgency    = ReservationKeepAliveUrgency,
                    ExpiresUtc = ctx.UtcNow.Add(InvestmentTtlFor(tuning)),
                });
                if (isNew)
                    ctx.AddLog(st.Tag + " RESERVE_KEEPALIVE " + cap);
            }
        }

        // ---- slot management ----------------------------------------------------------------

        /// <summary>Scope for a bank's remainders: everything at one node for one faction.</summary>
        private static string RemainderScope(string factionTag, string nodeId)
        {
            return factionTag + ":" + nodeId;
        }

        /// <summary>Scope for a ship-build entry's own reserved bucket, which is isolated from the
        /// node's routable inventory and must not share carry with the bank.</summary>
        private static string BuildRemainderScope(string factionTag, string nodeId, string entryId)
        {
            return factionTag + ":" + nodeId + "#" + entryId;
        }

        private static string RemainderKey(string scope, string recipeId, string invKey)
        {
            return scope + "|" + recipeId + "|" + invKey;
        }

        /// <summary>The sub-unit amount currently carried for one triple.</summary>
        private static double CarriedRemainder(Dictionary<string, double> remainders,
            string scope, string recipeId, string invKey)
        {
            double carried;
            if (remainders == null) return 0.0;
            remainders.TryGetValue(RemainderKey(scope, recipeId, invKey), out carried);
            return carried;
        }

        /// <summary>Converts a fractional quantity into the whole units to apply to inventory,
        /// banking the sub-unit part for the next event. Cumulative whole units always equal
        /// floor(cumulative exact), so the error is bounded by one unit forever rather than
        /// accumulating one truncation per event.</summary>
        private static long AccrueWhole(Dictionary<string, double> remainders,
            string scope, string recipeId, string invKey, double exact)
        {
            if (remainders == null) return (long)exact;
            string key = RemainderKey(scope, recipeId, invKey);
            double carried;
            remainders.TryGetValue(key, out carried);
            double total = exact + carried;
            long whole = (long)Math.Floor(total);
            double rest = total - whole;
            if (rest > 1e-9) remainders[key] = rest;
            else remainders.Remove(key);
            return whole;
        }

        /// <summary>Ledger size at which ownership pruning starts running. Below it the carry is
        /// already small enough not to need bounding, and the scan is skipped.</summary>
        private const int RemainderPruneThreshold = 4096;

        /// <summary>Drops carry for nodes this faction no longer owns, the same ownership rule
        /// <see cref="PruneSlots"/> applies -- this is what bounds the ledger.</summary>
        private static void PruneRemainders(Dictionary<string, double> remainders, string factionTag,
            HashSet<string> ownedNodeIds)
        {
            if (remainders == null || remainders.Count == 0) return;
            string prefix = factionTag + ":";
            var toRemove = new List<string>();
            foreach (string key in remainders.Keys)
            {
                if (!key.StartsWith(prefix)) continue;
                string rest = key.Substring(prefix.Length);
                int cut = rest.IndexOfAny(new[] { '#', '|' });
                string nodeId = cut >= 0 ? rest.Substring(0, cut) : rest;
                if (!ownedNodeIds.Contains(nodeId)) toRemove.Add(key);
            }
            foreach (string k in toRemove) remainders.Remove(k);
        }

        private static void PruneSlots(Dictionary<string, MachineSlot> slots, string factionTag, HashSet<string> ownedNodeIds)
        {
            string prefix = factionTag + ":";
            var toRemove = new List<string>();
            foreach (string key in slots.Keys)
            {
                if (!key.StartsWith(prefix)) continue;
                string rest  = key.Substring(prefix.Length);
                int    colon = rest.IndexOf(':');
                string nodeId = colon >= 0 ? rest.Substring(0, colon) : rest;
                if (!ownedNodeIds.Contains(nodeId)) toRemove.Add(key);
            }
            foreach (string k in toRemove) slots.Remove(k);
        }

        /// <summary>Sum of a still-outstanding, active (unexpired) ship-build BOM need for exactly
        /// <paramref name="invKey"/> at <paramref name="node"/> that outranks an install claim of
        /// <paramref name="installRank"/> -- the floor ReserveInstallStock/ReserveInstallBomStock
        /// must not claim into. At the default ranks (build 0, install 0) a tie goes to the build, so
        /// an in-progress ship build (an already-collected, already-committed investment) is never
        /// starved by a capability upgrade the player can always retry later. Raising the capability's
        /// <see cref="FactionState.InstallPriority"/> above a build's own
        /// <see cref="BuildQueueEntry.Priority"/> is what lets a stalled install claim the contested
        /// component instead. A paused shipyard contributes nothing at any rank.</summary>
        /// <summary>A node's build queue in working order -- see <see cref="SpatialNode.OrderedBuildQueue"/>,
        /// which owns the rule so this manager, <see cref="ShipyardManager"/> and
        /// <see cref="SpatialNode.IsActiveBuild"/> cannot disagree about which entry is at the head.
        /// Ordering matters here and not in the floor-based checks because recipe INPUTS are claimed
        /// only in this one greedy pass -- a floor can express "leave what another entry's BOM still
        /// needs" for a component, but not "leave the ingots another entry will want", which only
        /// whoever runs first can decide.
        /// <para>Every caller skips an entry that is not active, and within the active set this is
        /// <see cref="SpatialNode.ClaimOrderedBuildQueue"/>'s order -- so the rank still decides who
        /// claims an input first, and only which entries are in the contest moved.</para></summary>
        internal static List<BuildQueueEntry> EntriesByPriority(SpatialNode node)
        {
            return node.OrderedBuildQueue();
        }

        private static long ShipBuildFloor(SpatialNode node, string invKey, DateTime now, int installRank)
        {
            return node.BuildQueueReserved(invKey, now, installRank);
        }

        /// <summary>What a claimant ranked <paramref name="rank"/> leaves of <paramref name="invKey"/>
        /// at <paramref name="nodeId"/> for the faction's Stock orders (<see cref="StockClaimFloor"/>):
        /// only claims strictly above it, so a tie goes to the build or install.</summary>
        private static long StockFloor(StockClaimFloor claims, string nodeId, string invKey, int rank)
        {
            return claims == null ? 0 : claims.For(nodeId, invKey, rank);
        }

        private static HashSet<string> ActiveShipBuildOutputKeys(List<SpatialNode> ownedNodes, DateTime now)
        {
            var result = new HashSet<string>();
            foreach (SpatialNode node in ownedNodes)
            {
                // A paused shipyard will never absorb, so holding its BOM keys' pull gate open would
                // dedicate bank capacity to components nothing is waiting for.
                if (node.ShipyardPaused) continue;
                foreach (BuildQueueEntry build in node.BuildQueue)
                {
                    if (build == null || build.ExpiresUtc <= now) continue;
                    // Same reason as the paused check above: a build waiting for a slot is not what
                    // the bank's capacity should be dedicated to.
                    if (!node.IsActiveBuild(build)) continue;
                    foreach (KeyValuePair<string, long> bomItem in build.Bom)
                    {
                        long collected;
                        build.Collected.TryGetValue(bomItem.Key, out collected);
                        if (bomItem.Value > collected) result.Add(bomItem.Key);
                    }
                }
                // A repair bill is the same kind of commitment: parts already absorbed are sunk
                // into the waiting hulls, and the yard posts the rest as ShipBuild-class demand.
                RepairSupply.AddOutstandingKeys(node, result);
            }
            return result;
        }

        // ---- capability bank tick -----------------------------------------------------------

        private void TickCapabilityBank(
            FactionTuning tuning,
            Dictionary<string, MachineSlot> slots, Dictionary<string, double> remainders,
            string factionTag, SpatialNode node, int capIdx,
            int machineCount, MachineModel model, List<ConversionRecipe> recipes,
            List<ConversionRecipe> candidates, int noDemandRecipes,
            HashSet<string> demandedKeys, Dictionary<string, long> demandedQuantity,
            RecipeExpansion expansion,
            Dictionary<string, InstallReserveFloor.Claim> installClaims,
            StockClaimFloor stockClaims,
            NodeInputNeeds itemDemandNeeds,
            IProductionExecutionContext ctx, DateTime now, double horizonSeconds, double maxCreditSeconds)
        {
            // What this bank must leave alone for the recipes serving an outstanding capability
            // install. Null -- and every check below skipped -- whenever nothing is being installed,
            // which is the overwhelmingly common case (see InstallReserveFloor.ByKey).
            InstallInputReserve installReserve = ComputeInstallInputReserve(candidates, installClaims, model);
            var inputFloors = new BankInputFloors(installReserve, stockClaims, node.Id);
            // Demand admits a recipe; concrete output scarcity ranks it, with no exemption for a ship
            // BOM. The BOM reaches this loop through the oracle like any other need: the shipyard posts
            // its shortfall as a node-pinned "Need:", which is the owed term at the yard, and its
            // "Item:" row raises the wanted floor at every other node of the faction. A node already
            // holding that floor of a BOM component is a measured glut until a haul drains it.
            // ServiceShipBuildReservations already ran this tick and gave the yard's own recipes a
            // guaranteed first pass against their isolated bucket, so this loop only ever competes for
            // genuinely leftover node.Inventory.
            var ranked         = new List<ConversionRecipe>();
            var priorityValues = new List<double>();
            var runnable    = new List<long>();   // batches of input on hand, aligned with ranked
            var blocked     = new List<ConversionRecipe>();

            // Attribution for DebugTraces.SlotGate. Counted unconditionally -- three int increments
            // against a loop that already calls ScarcityPriority and MaxRunnableBatches per recipe --
            // so the toggle only gates the formatting and the write. nodemand is carried in whole:
            // it is the size of what the candidate set left out, which the caller knows without
            // visiting a recipe.
            int gateNoDemand = noDemandRecipes, gateOversupplied = 0, gateNoInput = 0;
            // The oversupplied total split by whether the oracle actually had a reference to measure
            // against. Rejections read 1.00 either way (see ScarcityReading), so without this split
            // the trace cannot say whether the economy is in glut or simply unpriced -- which want
            // opposite fixes. Only filled when the trace is on; IsMeasuredOversupply costs a lookup.
            int gateGlut = 0, gateNoOpinion = 0;
            string worstOversupplied = null;
            double worstOversuppliedScarcity = 0.0;

            _lastTelemetry.RecipesSkippedNoDemand += noDemandRecipes;
            foreach (ConversionRecipe recipe in candidates)
            {
                _lastTelemetry.RecipesExamined++;
                if (recipe.Inputs  == null || recipe.Inputs.Length  == 0) continue;
                if (recipe.Outputs == null || recipe.Outputs.Length == 0) continue;
                if (recipe.TimeSecs <= 0f) continue;

                ScarcityReading reading = ScarcityPriority(node, recipe, demandedKeys);
                double scarcity = reading.Priority;
                // Two checks rather than one compound condition so the two reasons can be told apart.
                // They are different findings: nobody asked, versus asked and already oversupplied.
                // Every candidate carries a demanded output, so this holds the gate against the index
                // disagreeing with ScarcityPriority rather than doing the filtering itself.
                if (!reading.HasDemandedOutput)
                {
                    gateNoDemand++;
                    continue;
                }
                if (scarcity <= 1.0)
                {
                    gateOversupplied++;
                    if (DebugTraces.SlotGate)
                    {
                        if (IsMeasuredOversupply(node.Id, reading)) gateGlut++; else gateNoOpinion++;
                        // Worst = most oversupplied = lowest unfloored reading, not the first one
                        // seen. Reporting the first was harmless only while every value was the
                        // clamped 1.00; now that the real figure is carried it would pick an
                        // arbitrary member of a range.
                        if (worstOversupplied == null || reading.BestRaw < worstOversuppliedScarcity)
                        {
                            worstOversupplied = recipe.BlueprintId;
                            worstOversuppliedScarcity = reading.BestRaw;
                        }
                    }
                    continue;
                }

                long runnableBatches = MaxRunnableBatches(node, recipe, model, remainders,
                    RemainderScope(factionTag, node.Id), ctx,
                    inputFloors.For(recipe.BlueprintId));
                if (runnableBatches <= 0)
                {
                    _lastTelemetry.BlockedRecipes++;
                    gateNoInput++;
                    blocked.Add(recipe);
                    continue;
                }

                // Insertion sort (lists are small — typically 2–8 entries).
                int pos = ranked.Count;
                for (int i = 0; i < priorityValues.Count; i++)
                    if (scarcity > priorityValues[i]) { pos = i; break; }
                ranked.Insert(pos, recipe);
                priorityValues.Insert(pos, scarcity);
                runnable.Insert(pos, runnableBatches);
            }

            long blockedDemandStart = Stopwatch.GetTimestamp();
            foreach (ConversionRecipe recipe in blocked)
                PostRecipeInputDemands(tuning, recipe, machineCount, model, demandedQuantity, expansion, itemDemandNeeds);
            _lastTelemetry.BlockedDemandTicks += Stopwatch.GetTimestamp() - blockedDemandStart;

            if (ranked.Count == 0)
            {
                if (DebugTraces.SlotGate)
                    ctx.AddLog(SlotGateLine(factionTag, node.Id, (NodeCapabilityKind)capIdx, machineCount,
                        0, 0, gateNoDemand, gateOversupplied, gateGlut, gateNoOpinion, gateNoInput, 0,
                        worstOversupplied, worstOversuppliedScarcity));
                ClearCapabilitySlots(slots, factionTag, node.Id, capIdx, machineCount, now);
                return;
            }

            // Fair-share scarce recipes in priority order, capped by what current input can
            // usefully feed over SlotInputHorizonSeconds; surplus capacity may remain idle.
            int[] slotCount = ComputeSlotCounts(ranked, runnable, model.Speed, machineCount, horizonSeconds);

            if (DebugTraces.SlotGate)
            {
                int awarded = 0, zeroSlotRecipes = 0;
                for (int i = 0; i < slotCount.Length; i++)
                {
                    awarded += slotCount[i];
                    if (slotCount[i] == 0) zeroSlotRecipes++;
                }
                // Only when the bank actually left machines idle -- a saturated bank has nothing to
                // explain, and reporting it would make this trace scale with banks rather than with
                // the problem.
                if (awarded < machineCount)
                    ctx.AddLog(SlotGateLine(factionTag, node.Id, (NodeCapabilityKind)capIdx, machineCount,
                        ranked.Count, awarded, gateNoDemand, gateOversupplied, gateGlut, gateNoOpinion,
                        gateNoInput, zeroSlotRecipes, worstOversupplied, worstOversuppliedScarcity));
            }

            // Re-point physical slots at their target recipes, consolidating any partial-batch work
            // freed by a shrinking recipe into a surviving slot of that recipe (no work discarded).
            MachineSlot[] bankSlots = RebalanceSlots(slots, factionTag, node, capIdx, machineCount,
                ranked, slotCount, recipes, now);

            // Run production on each assigned slot.
            string bankScope = RemainderScope(factionTag, node.Id);
            for (int slotIdx = 0; slotIdx < machineCount; slotIdx++)
            {
                _lastTelemetry.SlotsVisited++;
                MachineSlot slot = bankSlots[slotIdx];
                if (slot.ActiveRecipeId == null) continue;

                ConversionRecipe active = FindRecipe(recipes, slot.ActiveRecipeId);
                if (active == null) { slot.ActiveRecipeId = null; slot.Accum = 0.0; continue; }

                // Accumulate fractional batches from elapsed real time.
                double elapsed = (now - slot.LastTick).TotalSeconds;
                slot.LastTick = now;
                if (elapsed < 0) elapsed = 0.0;
                if (elapsed > maxCreditSeconds) { elapsed = maxCreditSeconds; _lastTelemetry.ElapsedClamps++; }
                slot.Accum += elapsed * model.Speed / active.TimeSecs;

                long batches = (long)Math.Floor(slot.Accum);
                slot.Accum  -= batches;

                if (batches <= 0) continue;

                long canRunBatches = MaxRunnableBatches(node, active, model, remainders, bankScope, ctx,
                    inputFloors.For(active.BlueprintId));
                if (canRunBatches < batches)
                {
                    // Restore the elapsed-time progress this slot couldn't spend — a momentary
                    // stock shortfall must not erase real machine-time already accrued, or a
                    // slot on a scarce/expensive recipe can be reset to zero every time it's
                    // one delivery away from completing a batch.
                    slot.Accum += (batches - canRunBatches);
                    batches = canRunBatches;
                }
                if (batches <= 0) continue;

                // Consume inputs. This is DemandHistory's gravity term's real feed point -- credited
                // here (actual consumption), never at Haul-arrival/delivery, so the gravity signal is
                // bounded by this bank's own processing throughput rather than a self-reinforcing
                // delivery loop (docs/material-vectors.md's dated correction note has the full account).
                if (active.Inputs != null)
                    foreach (RecipeItem inp in active.Inputs)
                    {
                        if (inp.Amount <= 0) continue;
                        string invKey = InvKey(inp);
                        long qty = AccrueWhole(remainders, bankScope, active.BlueprintId, invKey,
                            batches * MachinePhysics.InputAmount(inp.Amount, model, active.IgnoresEfficiencyMultiplier));
                        if (qty <= 0) continue;
                        ctx.AddNodeInventory(node.Id, invKey, -qty);

                        double litersPerUnit = _volumes != null ? _volumes.VolumeLitersPerUnit(invKey) : 0;
                        double weighted = litersPerUnit > 0 ? qty * litersPerUnit : qty;
                        ctx.AddPendingDemandHistoryCredit(node.Id, invKey, weighted);
                        RecordProductionHistory(factionTag, node.Id, active.BlueprintId, invKey, qty, false, now);
                    }

                // Produce outputs.
                if (active.Outputs != null)
                    foreach (RecipeItem outp in active.Outputs)
                    {
                        if (outp.Amount <= 0) continue;
                        string outKey = InvKey(outp);
                        long produce = AccrueWhole(remainders, bankScope, active.BlueprintId, outKey,
                            batches * outp.Amount * model.OutputScale);
                        if (produce <= 0) continue;
                        ctx.AddNodeInventory(node.Id, outKey, produce);
                        RecordProductionHistory(factionTag, node.Id, active.BlueprintId, outKey, produce, true, now);
                        ctx.AddLog(factionTag + " PRODUCED " + outKey + " +" + produce
                                + " from=" + batches + "x" + active.BlueprintId + " node=" + node.Id);
                    }
            }

            // Post consolidated input demands for each recipe holding slots in this bank.
            PostBankInputDemands(tuning, node, slotCount, ranked, model, horizonSeconds,
                demandedQuantity, expansion, itemDemandNeeds, ctx);
        }

        /// <summary>What one bank must leave untouched this tick for the recipes that serve an
        /// outstanding capability-install claim, keyed by recipe INPUT, plus the blueprint ids of
        /// those recipes (which see raw, unreduced inventory).</summary>
        private sealed class InstallInputReserve
        {
            internal Dictionary<string, long> ByInputKey;
            internal HashSet<string>          ServingBlueprints;
        }

        /// <summary>The input floors one bank's recipes run against: the install reserve and the
        /// faction's Stock-order claims (<see cref="StockClaimFloor"/>), combined per recipe by which
        /// of the two it serves. A recipe here has no rank of its own, so it stands at 0 and leaves
        /// only claims a player ranked above 0.
        ///
        /// <para>Where no Stock order claims anything above 0 at this node -- every bank of every
        /// faction without a ranked order -- this is exactly <see cref="InstallInputFloorFor"/> and
        /// allocates nothing. Elsewhere at most four maps per bank, one per (serves install, serves
        /// a stock claim) pair, built on first ask. A struct, held in a local, so the common bank
        /// pays no allocation for it.</para></summary>
        private struct BankInputFloors
        {
            private readonly InstallInputReserve _install;
            private readonly StockClaimFloor _stock;
            private readonly string _nodeId;
            private Dictionary<string, long>[] _byServing;
            private bool[] _built;

            internal BankInputFloors(InstallInputReserve install, StockClaimFloor stock, string nodeId)
            {
                _install = install;
                _stock = stock != null && stock.AnyAbove(nodeId, 0) ? stock : null;
                _nodeId = nodeId;
                _byServing = null;
                _built = null;
            }

            internal Dictionary<string, long> For(string blueprintId)
            {
                if (_stock == null) return InstallInputFloorFor(_install, blueprintId);

                bool servesInstall = _install != null && _install.ServingBlueprints.Contains(blueprintId);
                bool servesStock = _stock.Serves(_nodeId, blueprintId);
                int slot = (servesInstall ? 1 : 0) | (servesStock ? 2 : 0);
                if (_byServing == null)
                {
                    _byServing = new Dictionary<string, long>[4];
                    _built = new bool[4];
                }
                if (_built[slot]) return _byServing[slot];

                Dictionary<string, long> floors = _stock.FloorsAt(_nodeId, 0, servesStock);
                if (!servesInstall && _install != null)
                {
                    if (floors == null) floors = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, long> kv in _install.ByInputKey)
                    {
                        long prior;
                        floors.TryGetValue(kv.Key, out prior);
                        floors[kv.Key] = prior + kv.Value;
                    }
                }
                _byServing[slot] = floors;
                _built[slot] = true;
                return floors;
            }
        }

        /// <summary>
        /// The capability-install counterpart to <see cref="ServiceShipBuildReservations"/>, one level
        /// up from <see cref="InstallReserveFloor"/>. That floor protects an install's FINISHED
        /// components from a ship build re-absorbing them; nothing protected the raw INPUTS of the
        /// recipes that would make those components, and a bank ranks on output scarcity alone —
        /// <see cref="DemandClass"/> is not a ranking term — so a cheap co-demanded recipe sharing the
        /// same ingot simply spent the trickle first, every tick, forever.
        ///
        /// <para>Live EXP, 2026-09-06 (FAC-279): six ManufactoryII banks, <c>Ingot:Iron</c> minted at
        /// 6.0–7.6/min per node and consumed the tick it appeared, <c>SteelPlate</c> (21 Iron/batch)
        /// and <c>Motor</c> (20) holding <c>slotCount=1</c> at every one of them and producing
        /// <b>0.0/min</b>, while <c>InteriorPlate</c> (3) and <c>Construction</c> (8) took all of it.
        /// The faction held zero <c>Ingot:Iron</c> anywhere.</para>
        ///
        /// <para><b>Ranking cannot fix this, and that was measured rather than argued.</b> An
        /// expensive recipe below its one-batch threshold fails
        /// <see cref="MaxRunnableBatches"/> and lands in <c>blocked</c> — it is never on the
        /// <c>ranked</c> list at all, so no ranking term can reorder it. Reproduced at the live
        /// recipe set and a 7 Iron/tick trickle, the install pair produced 0 with its output scarcity
        /// set below the competitors' (2.0 vs 3.0), and 0 again at 5.0 and at 99.0 — identical to the
        /// digit across all three. Only when input was already plentiful did rank change anything,
        /// which is exactly the regime that has no starvation to fix.</para>
        ///
        /// <para><b>Sized at ONE batch of each serving recipe, not at the claim's full remaining
        /// need — and that bound was chosen by measurement, against the ship-build precedent.</b>
        /// <see cref="ServiceShipBuildReservations"/> claims a build's full remaining need greedily,
        /// which is safe there because a <see cref="BuildQueueEntry"/> expires and its BOM is one
        /// hull. An install request does neither: <c>PostReservationKeepAlive</c> reposts it forever,
        /// and a prefab-derived BOM can be the whole component catalogue (the Shipyard's is ~2,500
        /// components — FAC-279's primary defect). Sizing this reserve the same greedy way was
        /// implemented and measured first, and it locked the shared ingot outright: at a 100,000-line
        /// BOM with Iron arriving at 4,000/tick, <c>InteriorPlate</c> and <c>Construction</c> produced
        /// <b>0</b> across 80 ticks while <b>271,415 Iron sat idle</b> in routable inventory. At one
        /// batch the same run produces 790 of each and the install still gets 395 of each — the
        /// starvation fix survives the bound, the monopoly does not.</para>
        ///
        /// <para>One batch is enough because the gap being closed is an <i>accumulation</i> gap, not a
        /// throughput one. The starving recipe fails <see cref="MaxRunnableBatches"/> only while stock
        /// sits below its first batch; withhold exactly that much and stock climbs across ticks until
        /// it clears, at which point <see cref="ComputeSlotCounts"/>'s unconditional one-machine floor
        /// guarantees it a machine and it competes on raw, unreduced inventory like anything else. The
        /// reserve is applied at both points that read live stock — admission and the slot's own
        /// recheck — so a slot that had already accrued batches cannot drain what admission withheld.
        /// <b>This is the shape of the retired <c>ComputeReservedInputs</c> floor, deliberately.</b>
        /// What defeated that floor for ship builds was the surplus <i>above</i> it being swept while
        /// a large multi-batch order needed to accumulate; an install's recipe needs to reach one
        /// batch and no further, and the FAC-231 machine floor that was missing then now exists.
        /// Measured at the live recipe set: 7 Iron/tick, install output 0 before and climbing after.</para>
        ///
        /// <para>It needs no release phase and so has no bucket to leak from: the claim comes from
        /// <see cref="InstallReserveFloor.ByKey"/>, already net of what is staged in
        /// <see cref="SpatialNode.ReservedInventory"/> and dropping a key entirely once its BOM line is
        /// met or its <see cref="InvestmentRequest"/> expires. It is recomputed fresh every tick and
        /// never accumulates. Applied in full at every bank rather than split across them — same
        /// reasoning as <see cref="InstallReserveFloor.For"/>.</para>
        ///
        /// <para><b>Known gap.</b> Two recipes both serving the same install both see raw inventory and
        /// race: measured at 7 Iron/tick with both BOM lines unreachable, <c>Motor</c> (20 Iron) took
        /// the whole trickle and <c>SteelPlate</c> (21) produced 0. Bounded by the BOM in practice —
        /// once a line is met its claim drops and the other recipe proceeds — but it is a real
        /// intra-install ordering, unaddressed here.</para>
        /// </summary>
        private static InstallInputReserve ComputeInstallInputReserve(
            List<ConversionRecipe> candidates, Dictionary<string, InstallReserveFloor.Claim> installClaims,
            MachineModel model)
        {
            if (installClaims == null || installClaims.Count == 0
                || candidates == null || candidates.Count == 0) return null;

            Dictionary<string, long> byInput = null;
            HashSet<string> serving = null;
            foreach (ConversionRecipe recipe in candidates)
            {
                if (recipe.Inputs  == null || recipe.Inputs.Length  == 0) continue;
                if (recipe.Outputs == null || recipe.Outputs.Length == 0) continue;

                bool servesClaim = false;
                foreach (RecipeItem outp in recipe.Outputs)
                {
                    if (outp.Amount <= 0) continue;
                    InstallReserveFloor.Claim claim;
                    if (!installClaims.TryGetValue(InvKey(outp), out claim) || claim.Outstanding <= 0) continue;
                    if (outp.Amount * model.OutputScale <= 0) continue;
                    servesClaim = true;
                    break;
                }
                if (!servesClaim) continue;

                if (serving == null) serving = new HashSet<string>(StringComparer.Ordinal);
                serving.Add(recipe.BlueprintId);
                if (byInput == null) byInput = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (RecipeItem inp in recipe.Inputs)
                {
                    if (inp.Amount <= 0) continue;
                    double perBatch = MachinePhysics.InputAmount(
                        inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                    if (perBatch <= 0) continue;
                    string key = InvKey(inp);
                    long prior;
                    byInput.TryGetValue(key, out prior);
                    byInput[key] = prior + (long)Math.Ceiling(perBatch);
                }
            }

            if (byInput == null) return null;
            var reserve = new InstallInputReserve();
            reserve.ByInputKey        = byInput;
            reserve.ServingBlueprints = serving;
            return reserve;
        }

        /// <summary>The reserve a given recipe must respect: nothing when no install claim is live at
        /// this bank, and nothing for the recipes the reserve exists for — they see raw inventory, the
        /// same asymmetry the ship-build reservation has between an entry's own bucket and everyone
        /// else's view of <c>Inventory</c>.</summary>
        private static Dictionary<string, long> InstallInputFloorFor(
            InstallInputReserve reserve, string blueprintId)
        {
            if (reserve == null) return null;
            return reserve.ServingBlueprints.Contains(blueprintId) ? null : reserve.ByInputKey;
        }

        /// <summary>
        /// Decides how many of the bank's <paramref name="machineCount"/> machines each ranked recipe
        /// should run this tick. Each recipe is first capped by how many machines its current input
        /// can usefully feed over <see cref="SlotInputHorizonSeconds"/> (a recipe with only a trickle
        /// of ore is capped low so it can't hold machines it can't keep busy). The machines are then
        /// fair-shared across recipes in scarcity-priority order (highest first), never exceeding
        /// any recipe's cap; slots a capped recipe cannot use cascade to recipes still under cap.
        /// If the total cap is below <paramref name="machineCount"/>, the surplus machines stay idle.
        ///
        /// <para>When exactly one recipe remains ranked — the sole survivor of bank-monopolization,
        /// or simply the only demanded/scarce recipe this bank has right now — it always gets a floor
        /// of one machine if it has at least one full batch on hand, even if that batch falls short
        /// of a full <see cref="SlotInputHorizonSeconds"/> window. Without this floor, an expensive
        /// recipe (many input units per batch, e.g. SteelPlate's 21 Iron) can have real material
        /// sitting on hand — enough for several batches — yet still round down to zero machines
        /// simply because "batches on hand" divided by "batches needed to fill 10 seconds of one
        /// machine" is less than 1, a threshold that scales with the recipe's own batch cost and has
        /// nothing to do with whether the material exists. Live-observed: a ship-build recipe that
        /// had already monopolized its bank (every rival recipe evicted by
        /// <see cref="TickCapabilityBank"/>'s bank-monopolization step) and held 90+ Iron — over four
        /// full batches — still received zero slots and produced nothing, because 90/21 ≈ 4 batches
        /// fell short of the ~7-batch horizon threshold, and with no other recipe left in the bank to
        /// absorb the freed capacity, all machines simply sat idle.</para>
        ///
        /// <para><b>The floor applies to every ranked recipe, not only a sole survivor.</b> It was
        /// restricted to <c>n == 1</c> for a real reason that no longer holds, and the restriction
        /// then became the dominant cause of idle machines. The original reasoning: flooring every
        /// recipe let cheap competitors consume, every tick, the surplus a blocked ship-build recipe
        /// needed to accumulate — because the ship-build "reservation" was then only a subtraction
        /// other recipes had to honour, and horizon-capping a competitor to zero was the accidental
        /// protection that made it work.
        ///
        /// <para><see cref="ServiceShipBuildReservations"/> superseded that: a build's inputs are now
        /// moved physically out of routable <c>Inventory</c> into the entry's own bucket before this
        /// loop runs, so a competitor cannot see them however many machines it holds. The protection
        /// the restriction provided is now structural, and
        /// <c>ScarceSharedInput_CheapRecipesCannotPermanentlyStarveBlockedShipBuildRecipe</c> is the
        /// regression test that says so — it passes with this floor unconditional.</para>
        ///
        /// <para><b>What the restriction cost.</b> The cap is "batches on hand / batches needed to
        /// fill the horizon on one machine", and that threshold scales with the recipe's own speed:
        /// at RefineryII's effective 15.9, <c>IronOreToIngot</c> (TimeSecs 0.05) needs <b>3,180</b>
        /// units of ore to earn a single machine for ten seconds, while <c>CobaltOreToIngot</c>
        /// (TimeSecs 3.0) needs 53. The fastest recipes demand the most stock — exactly inverted. With
        /// two or more recipes ranked and neither holding a full horizon's worth, every one floored to
        /// zero and the whole bank sat idle: live 2026-09-02, <c>FCTM N036 cap=RefineryII machines=18
        /// awarded=0 ranked=2</c>, and across the window 125 such banks held 798 machines with 767
        /// idle (96.1%). See FAC-231.</para>
        /// </summary>
        private static int[] ComputeSlotCounts(
            List<ConversionRecipe> ranked, List<long> runnable, double machineSpeed, int machineCount,
            double horizonSeconds)
        {
            int n = ranked.Count;
            int[] cap = new int[n];
            for (int r = 0; r < n; r++)
            {
                double perMachineBatchesPerSec = machineSpeed / ranked[r].TimeSecs;
                double horizonBatches = perMachineBatchesPerSec * horizonSeconds;
                int useful = horizonBatches > 0
                    ? (int)Math.Floor(runnable[r] / horizonBatches)
                    : 0;
                if (useful <= 0 && runnable[r] >= 1) useful = 1;
                if (useful > machineCount) useful = machineCount;
                cap[r] = useful;
            }

            int[] counts = new int[n];
            int remaining = machineCount;
            bool progress = true;
            while (remaining > 0 && progress)
            {
                progress = false;
                for (int r = 0; r < n && remaining > 0; r++)
                {
                    if (counts[r] >= cap[r]) continue;
                    counts[r]++;
                    remaining--;
                    progress = true;
                }
            }
            return counts;
        }

        /// <summary>
        /// Materialises this bank's machine slots and re-points them at the target recipes so each
        /// ranked recipe ends up with exactly <paramref name="slotCount"/> slots. Slots already on the
        /// right recipe (within count) are left untouched so their accumulated work is preserved. When
        /// a recipe must shed slots, the freed slots' <see cref="MachineSlot.Accum"/> is consolidated
        /// into a surviving slot of the same recipe before they are reassigned ÔÇö so switching never
        /// discards a partial batch (the reason the old hysteresis lock existed). Returns the slot
        /// array indexed by slot number for the production pass.
        /// </summary>
        private static MachineSlot[] RebalanceSlots(
            Dictionary<string, MachineSlot> allSlots,
            string factionTag, SpatialNode node, int capIdx, int machineCount,
            List<ConversionRecipe> ranked, int[] slotCount, List<ConversionRecipe> recipes, DateTime now)
        {
            var slots = new MachineSlot[machineCount];
            for (int i = 0; i < machineCount; i++)
            {
                string key = factionTag + ":" + node.Id + ":" + capIdx + ":" + i;
                MachineSlot s;
                if (!allSlots.TryGetValue(key, out s))
                {
                    s = new MachineSlot { LastTick = now };
                    allSlots[key] = s;
                }
                slots[i] = s;
            }

            // Map each target recipe id to its ranked index.
            var targetIndex = new Dictionary<string, int>(ranked.Count);
            for (int r = 0; r < ranked.Count; r++)
                targetIndex[ranked[r].BlueprintId] = r;

            // Bucket slots by the recipe they currently run; slots on a non-target recipe (or idle)
            // go straight into the free pool ÔÇö their accumulated work is for a recipe no longer
            // running here, so it cannot be consolidated and is dropped (<1 batch).
            var slotsByRecipe = new List<int>[ranked.Count];
            for (int r = 0; r < ranked.Count; r++) slotsByRecipe[r] = new List<int>();
            var freeSlots = new List<int>();
            for (int i = 0; i < machineCount; i++)
            {
                int r;
                if (slots[i].ActiveRecipeId != null && targetIndex.TryGetValue(slots[i].ActiveRecipeId, out r))
                    slotsByRecipe[r].Add(i);
                else
                {
                    slots[i].ActiveRecipeId = null;
                    slots[i].Accum = 0.0;
                    freeSlots.Add(i);
                }
            }

            // Shrink phase: release surplus slots, consolidating their Accum into a kept slot.
            for (int r = 0; r < ranked.Count; r++)
            {
                List<int> mine = slotsByRecipe[r];
                int keep = slotCount[r];
                if (keep > mine.Count) keep = mine.Count;
                if (mine.Count <= keep) continue;

                int sink = keep > 0 ? mine[keep - 1] : -1; // any kept slot works as the consolidation sink
                for (int k = keep; k < mine.Count; k++)
                {
                    int idx = mine[k];
                    if (sink >= 0) slots[sink].Accum += slots[idx].Accum; // preserve partial work
                    slots[idx].Accum = 0.0;
                    slots[idx].ActiveRecipeId = null;
                    freeSlots.Add(idx);
                }
                mine.RemoveRange(keep, mine.Count - keep);
            }

            // Grow phase: recipes under their target pull from the free pool.
            for (int r = 0; r < ranked.Count; r++)
            {
                int need = slotCount[r] - slotsByRecipe[r].Count;
                while (need > 0 && freeSlots.Count > 0)
                {
                    int idx = freeSlots[freeSlots.Count - 1];
                    freeSlots.RemoveAt(freeSlots.Count - 1);
                    slots[idx].ActiveRecipeId = ranked[r].BlueprintId;
                    slots[idx].Accum = 0.0;
                    need--;
                }
            }

            // Whatever the bank can't usefully run stays idle.
            foreach (int idx in freeSlots)
            {
                slots[idx].ActiveRecipeId = null;
                slots[idx].Accum = 0.0;
            }

            return slots;
        }

        /// <summary>
        /// Contributes this bank's need for each active recipe's inputs into <paramref name="itemDemandNeeds"/>
        /// (see <see cref="NodeInputNeeds"/> for how contributions to one input combine, so a cheap
        /// recipe's small need can never clobber a scarce/expensive recipe's larger one for the same
        /// shared input). Quantity is enough to
        /// keep all slots on that recipe busy for the full <see cref="DemandTtl"/>. Contribution is
        /// suppressed when current stock exceeds the half-TTL threshold — prevents per-tick demand
        /// spam on well-stocked machines.
        /// </summary>
        private static void PostBankInputDemands(
            FactionTuning tuning, SpatialNode node, int[] slotCount,
            List<ConversionRecipe> ranked, MachineModel model, double horizonSeconds,
            Dictionary<string, long> demandedQuantity, RecipeExpansion expansion,
            NodeInputNeeds itemDemandNeeds,
            IProductionExecutionContext ctx)
        {
            for (int r = 0; r < ranked.Count; r++)
            {
                ConversionRecipe recipe = ranked[r];
                if (recipe.Inputs == null) continue;

                long wantedBatches = BatchesToSatisfyOutputDemand(recipe, model, demandedQuantity, expansion);

                if (slotCount[r] <= 0)
                {
                    PostStartupInputDemands(recipe, model, horizonSeconds, wantedBatches,
                        itemDemandNeeds);
                    continue;
                }

                // Total throughput for the slots on this recipe: batches/sec.
                double batchesPerSec = slotCount[r] * model.Speed / recipe.TimeSecs;

                foreach (RecipeItem inp in recipe.Inputs)
                {
                    if (inp.Amount <= 0) continue;
                    string inpKey = InvKey(inp);
                    double perBatch = MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);

                    long have = ctx.GetNodeInventory(node.Id, inpKey);
                    if (have < 0) have = 0;

                    // Re-post threshold: half of what would be consumed over DemandTtl.
                    long repostBelow = (long)Math.Ceiling(batchesPerSec * (DemandTtlFor(tuning).TotalSeconds / 2.0) * perBatch);
                    if (have >= repostBelow) continue;

                    long needed = (long)Math.Ceiling(batchesPerSec * DemandTtlFor(tuning).TotalSeconds * perBatch);
                    needed = CapByOutputDemand(needed, wantedBatches, perBatch);
                    itemDemandNeeds.Add(inpKey, needed);
                }
            }
        }

        /// <summary>
        /// Posts input demand for a recipe that ranked -- its output is wanted and it holds at least
        /// one batch of input -- but earned no slot, because <see cref="ComputeSlotCounts"/> caps a
        /// recipe at <c>floor(runnable / horizonBatches)</c> and a node holding less than one
        /// machine-horizon of input floors to zero. Before FAC-164 that third state posted nothing at
        /// all: the slotted loop skipped it and the blocked loop never saw it, so a refinery sitting on
        /// a little iron ore under a large ingot demand asked for neither ore nor anything else and
        /// stayed permanently just below its own start threshold.
        /// Sized at one machine's horizon -- enough to earn exactly one slot, not a whole bank's worth
        /// -- and capped by what the output demand can consume.
        /// <para><see cref="DemandEntry.Quantity"/> is a gross target for the node, not a shortfall:
        /// <see cref="OperationsManager.MatchVectors"/> subtracts the destination's own stock before
        /// forming a vector. Netting the stock out here too would subtract it twice, so a haul could
        /// only ever form while the node held less than half a machine-horizon.</para>
        /// </summary>
        private static void PostStartupInputDemands(
            ConversionRecipe recipe, MachineModel model, double horizonSeconds,
            long wantedBatches, NodeInputNeeds itemDemandNeeds)
        {
            if (recipe.TimeSecs <= 0f || model.Speed <= 0 || horizonSeconds <= 0) return;

            double horizonBatches = model.Speed / recipe.TimeSecs * horizonSeconds;
            if (horizonBatches <= 0) return;

            foreach (RecipeItem inp in recipe.Inputs)
            {
                if (inp.Amount <= 0) continue;
                string inpKey = InvKey(inp);
                double perBatch = MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                if (perBatch <= 0) continue;

                long needed = (long)Math.Ceiling(horizonBatches * perBatch);
                needed = CapByOutputDemand(needed, wantedBatches, perBatch);
                if (needed <= 0) continue;

                itemDemandNeeds.Add(inpKey, needed);
            }
        }

        /// <summary>
        /// How many batches of <paramref name="recipe"/> the faction's outstanding Item: demand for its
        /// outputs actually asks for -- the <b>MIN</b> over its demanded outputs.
        /// <para>One batch yields <i>every</i> output at once, so the least-hungry wanted product is the
        /// one that sets a sane input need; the hungrier ones are served by their own, far more direct
        /// recipes. Taking the MAX instead lets a by-product drive the whole bank, which is exactly what
        /// v0.80.0 shipped and the live box then measured: <c>StoneOreToIngot</c> is 1000 Ore:Stone ->
        /// 14 gravel + 30 iron + 2.4 nickel + 4 silicon, and ISBT's 5,071 <c>Ingot:Nickel</c> demand
        /// divided by that 2.4 asked for ~2,900 batches -- 2,918,000 Ore:Stone, against the 513 gravel
        /// that is the only thing stone refining is actually the right source for (FAC-164). Nickel has
        /// its own ore. MIN gives 37 batches, ~37,000 ore.
        /// Under-asking is the safe direction: input demand is re-posted every economy cycle and grows
        /// again while the shortfall persists, whereas over-asking is spent haulage that never repays.</para>
        /// Returns 0 when nothing in the demand map names any output, which callers read as "no cap"
        /// rather than "want none": recipes also enter the ranking through an active ship BOM, and
        /// those keep the mechanical sizing they had before FAC-164.
        /// </summary>
        /// <summary>Returned when every demanded output of a recipe belongs to some other recipe's
        /// route, i.e. this one is pure by-product supply for the demand on the table. Distinct from
        /// 0, which means no output of this recipe is demanded at all and leaves the mechanical size
        /// untouched — here the demand is real but it is not this recipe's to answer, so its inputs
        /// are capped to nothing.</summary>
        private const long ByproductOnlyBatches = -1;

        /// <summary>How many batches of <paramref name="recipe"/> the faction's outstanding Item:
        /// demand for its outputs actually asks for — the <b>MIN</b> over the demanded outputs this
        /// recipe is the chosen route for.
        ///
        /// <para>One batch yields <i>every</i> output at once, so the least-hungry wanted product is
        /// the one that sets a sane input need; the hungrier ones are served by their own, far more
        /// direct recipes. Taking the MAX instead lets a by-product drive the whole bank.</para>
        ///
        /// <para><paramref name="expansion"/> is what makes "served by their own recipes" a fact
        /// rather than an assumption. <see cref="RecipeExpansion"/> already picks one route per key
        /// and already prefers a single-output recipe over a joint-product one, so reading it here
        /// is single-sourcing that decision rather than restating it: <c>StoneOreToIngot</c> is the
        /// route for <c>Ingot:Stone</c> and is sized by gravel demand, while <c>Ingot:Iron</c>
        /// belongs to <c>IronOreToIngot</c> and no longer sizes a stone order. Without it the MIN
        /// degenerates onto whichever demanded output has the worst yield — live, 1,000
        /// <c>Ore:Stone</c> per 30 <c>Ingot:Iron</c> against iron ore's 700.</para>
        ///
        /// <para>A key the table has no route for is left to this recipe: an unrouted key has no
        /// better claimant, and skipping it would silence the only producer.</para></summary>
        private static long BatchesToSatisfyOutputDemand(
            ConversionRecipe recipe, MachineModel model, Dictionary<string, long> demandedQuantity,
            RecipeExpansion expansion)
        {
            if (recipe == null || recipe.Outputs == null || demandedQuantity == null) return 0;
            long batches = 0;
            bool anyDemanded = false;
            foreach (RecipeItem outp in recipe.Outputs)
            {
                if (outp.Amount <= 0) continue;
                string outKey = InvKey(outp);
                long wanted;
                if (!demandedQuantity.TryGetValue(outKey, out wanted) || wanted <= 0) continue;
                anyDemanded = true;
                if (!IsRouteFor(expansion, recipe, outKey)) continue;
                double perBatch = outp.Amount * model.OutputScale;
                if (perBatch <= 0) continue;
                long need = (long)Math.Ceiling(wanted / perBatch);
                if (need < 1) need = 1;
                if (batches == 0 || need < batches) batches = need;
            }
            if (batches == 0 && anyDemanded) return ByproductOnlyBatches;
            return batches;
        }

        /// <summary>Whether <paramref name="recipe"/> is the route <paramref name="expansion"/>
        /// chose for <paramref name="outKey"/>. True when there is no table or no route for the key,
        /// so an unrouted key keeps its only producer.</summary>
        private static bool IsRouteFor(RecipeExpansion expansion, ConversionRecipe recipe, string outKey)
        {
            if (expansion == null) return true;
            RecipeExpansion.Route route;
            if (!expansion.TryGetRoute(outKey, out route) || route.Recipe == null) return true;
            return string.Equals(route.Recipe.BlueprintId, recipe.BlueprintId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Clamps a mechanically-sized input need to what the outstanding output demand can consume.
        /// Machine geometry alone asked for a bank's full throughput across the demand TTL whether or
        /// not anyone wanted the product -- 3,013,200 Ore:Stone posted against the 513 gravel actually
        /// demanded (FAC-164). A <paramref name="wantedBatches"/> of 0 means "no known output demand"
        /// and passes <paramref name="needed"/> through untouched.
        /// </summary>
        private static long CapByOutputDemand(long needed, long wantedBatches, double perBatch)
        {
            if (wantedBatches == ByproductOnlyBatches) return 0;
            if (wantedBatches <= 0 || perBatch <= 0) return needed;
            long cap = (long)Math.Ceiling(wantedBatches * perBatch);
            return needed < cap ? needed : cap;
        }

        private static ConversionRecipe FindRecipe(List<ConversionRecipe> recipes, string blueprintId)
        {
            if (blueprintId == null) return null;
            foreach (ConversionRecipe r in recipes)
                if (r.BlueprintId == blueprintId) return r;
            return null;
        }

        /// <summary>Idles every machine in this bank. Slot objects are created (not just skipped) even
        /// on a bank's first-ever idle tick and their clock (<see cref="MachineSlot.LastTick"/>) is
        /// kept running regardless — otherwise a bank that goes from idle straight to holding a
        /// scarcity-gated recipe would have its slot
        /// created fresh with <c>LastTick = now</c> on the very tick it starts producing, losing the
        /// real elapsed time the input spent accumulating while the bank sat blocked and delaying
        /// its first batch by a full extra tick versus a bank that happened to hold an unrelated
        /// recipe (and therefore a live slot) the whole time.</summary>
        private static void ClearCapabilitySlots(Dictionary<string, MachineSlot> slots,
            string factionTag, string nodeId, int capIdx, int machineCount, DateTime now)
        {
            for (int slotIdx = 0; slotIdx < machineCount; slotIdx++)
            {
                string slotKey = factionTag + ":" + nodeId + ":" + capIdx + ":" + slotIdx;
                MachineSlot slot;
                if (!slots.TryGetValue(slotKey, out slot))
                {
                    slots[slotKey] = new MachineSlot { LastTick = now };
                    continue;
                }
                slot.ActiveRecipeId = null;
                slot.Accum = 0.0;
                slot.LastTick = now;
            }
        }

        /// <summary>
        /// Contributes a blocked recipe's full-bank-scale need for each input into
        /// <paramref name="itemDemandNeeds"/> (combined with any other recipe's need for the same key
        /// as <see cref="NodeInputNeeds"/> describes). Sized as if this recipe held every
        /// machine in the bank, since a blocked recipe currently holds none and needs enough input
        /// to become runnable at all — this is deliberately the larger of the two contributors when
        /// a cheap, already-runnable recipe shares the same input.
        /// </summary>
        private void PostRecipeInputDemands(
            FactionTuning tuning, ConversionRecipe recipe, int machineCount, MachineModel model,
            Dictionary<string, long> demandedQuantity, RecipeExpansion expansion,
            NodeInputNeeds itemDemandNeeds)
        {
            if (recipe == null || recipe.Inputs == null || recipe.TimeSecs <= 0f) return;

            long wantedBatches = BatchesToSatisfyOutputDemand(recipe, model, demandedQuantity, expansion);
            double batchesPerSec = machineCount * model.Speed / recipe.TimeSecs;
            foreach (RecipeItem inp in recipe.Inputs)
            {
                if (inp.Amount <= 0) continue;
                string inpKey = InvKey(inp);
                double perBatch = MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                long needed = (long)Math.Ceiling(batchesPerSec * DemandTtlFor(tuning).TotalSeconds * perBatch);
                needed = CapByOutputDemand(needed, wantedBatches, perBatch);
                _lastTelemetry.BlockedInputPosts++;
                itemDemandNeeds.Add(inpKey, needed);
            }
        }

        private bool PostDemand(IProductionExecutionContext ctx, DemandEntry demand)
        {
            bool isNew = ctx.PostDemand(demand);
            if (isNew) _lastTelemetry.DemandPosts++;
            else _lastTelemetry.DemandRefreshes++;
            return isNew;
        }

        // ---- investment / refinement requests -----------------------------------------------

        /// <summary>
        /// For each active Item: demand that no installed capability can produce, finds an
        /// uninstalled capability in <see cref="_capRecipes"/> that could and posts an
        /// <see cref="InvestmentRequest"/> so the brain can prioritise installing it.
        /// </summary>
        private void PostCapabilityInvestmentRequests(FactionState st,
            HashSet<NodeCapabilityKind> networkCaps, IProductionExecutionContext ctx, FactionTuning tuning)
        {
            string factionTag = st.Tag;
            List<DemandEntry> itemDemands = ctx.SnapshotItemDemands(factionTag);
            if (itemDemands.Count == 0) return;

            foreach (DemandEntry de in itemDemands)
            {
                if (!de.Kind.StartsWith("Item:")) continue;
                string itemKey = de.Kind.Substring(5);

                bool covered = false;
                // A crop is covered only where an installed Farm grows it. Any Farm at all used to
                // count, so a Farm on Agni answered for Grain and nothing ever asked again.
                string uncoveredCrop = null;
                foreach (NodeCapabilityKind cap in networkCaps)
                {
                    List<ConversionRecipe> recipes;
                    if (!_capRecipes.TryGetValue(cap, out recipes)) continue;
                    foreach (ConversionRecipe recipe in recipes)
                    {
                        if (recipe.Outputs == null) continue;
                        bool outputs = false;
                        foreach (RecipeItem outp in recipe.Outputs)
                            if (InvKey(outp) == itemKey) { outputs = true; break; }
                        if (!outputs) continue;
                        string crop = FarmCrops.SeedOf(recipe);
                        if (crop != null && !AnyFarmGrows(st, ctx, crop)) { uncoveredCrop = crop; continue; }
                        covered = true;
                        break;
                    }
                    if (covered) break;
                }
                if (covered) continue;
                // A Farm grows any crop it holds a seed for; what it lacks is the seed, not a second
                // irrigation system.
                if (uncoveredCrop != null)
                {
                    PostCropSeedDemand(st, ctx, tuning, uncoveredCrop, itemKey);
                    continue;
                }

                foreach (NodeCapabilityKind candidate in _capsByTier)
                {
                    if (networkCaps.Contains(candidate)) continue;
                    List<ConversionRecipe> candidateRecipes;
                    if (!_capRecipes.TryGetValue(candidate, out candidateRecipes) || candidateRecipes == null) continue;
                    bool canProduce = false;
                    string forCrop = null;
                    foreach (ConversionRecipe recipe in candidateRecipes)
                    {
                        if (recipe.Outputs == null) continue;
                        foreach (RecipeItem outp in recipe.Outputs)
                            if (InvKey(outp) == itemKey) { canProduce = true; break; }
                        if (canProduce) { forCrop = FarmCrops.SeedOf(recipe); break; }
                    }
                    if (!canProduce) continue;
                    // The capability that makes the item may sit on one this faction lacks: a meal
                    // needs a FoodProcessor and a FoodProcessor needs a Farm. Asking for the top of
                    // the chain only ever resolves to "prerequisite capability missing", and nothing
                    // else posts the bottom, so ask for the lowest missing link instead.
                    NodeCapabilityKind wanted = FirstMissingPrerequisite(candidate, networkCaps);
                    // A Magnate-tier player faction's components are never silently reserved
                    // toward a capability it didn't opt into (see FactionLifecycle.CanPostInvestmentFor).
                    if (!FactionLifecycle.CanPostInvestmentFor(st, wanted)) continue;
                    // A Farm asked for by a crop prefers where that crop is native; one asked for as a
                    // meal's prerequisite prefers where the most crops are.
                    string farmCrop = candidate == NodeCapabilityKind.Farm ? forCrop : null;

                    bool isNew = ctx.PostInvestment(new InvestmentRequest
                    {
                        FactionTag = factionTag,
                        Cap        = wanted,
                        Urgency    = CapabilityGapInvestmentUrgency,
                        ExpiresUtc = ctx.UtcNow.Add(InvestmentTtlFor(tuning)),
                        Crop       = farmCrop,
                    });
                    if (isNew)
                        ctx.AddLog(factionTag + " INVEST_REQUEST " + wanted
                                + " urgency=" + CapabilityGapInvestmentUrgency.ToString("F2", CultureInfo.InvariantCulture)
                                + (farmCrop != null ? " crop=" + farmCrop : "")
                                + " for=" + itemKey);

                    PostInstallCostDemands(factionTag, wanted, networkCaps, ctx, tuning);
                    break;
                }
            }
        }

        /// <summary>Asks for one seed of <paramref name="crop"/> at the faction's largest Farm: it
        /// keeps its own replant seed, so one is all it ever needs. A gross target, so a node that
        /// already holds one asks for nothing more (<see cref="OperationsManager.MatchVectors"/>).</summary>
        private void PostCropSeedDemand(FactionState st, IProductionExecutionContext ctx, FactionTuning tuning,
            string crop, string forItem)
        {
            SpatialNode farm = null;
            int best = 0;
            foreach (SpatialNode n in ctx.OwnedNodes(st.Tag))
            {
                int level;
                if (n.Capabilities.TryGetValue(NodeCapabilityKind.Farm, out level) && level > best)
                {
                    best = level;
                    farm = n;
                }
            }
            if (farm == null) return;

            string seedKey = InventoryKeyMapper.SeedPrefix + crop;
            bool isNew = PostDemand(ctx, new DemandEntry
            {
                FactionTag = st.Tag,
                Kind       = "Item:" + seedKey,
                NodeId     = farm.Id,
                ExpiresUtc = ctx.UtcNow.Add(DemandTtlFor(tuning)),
                Quantity   = 1,
                Class      = DemandClass.RecipeInput,
            });
            if (isNew)
                ctx.AddLog(st.Tag + " FARM_SEED_DEMAND " + seedKey + " node=" + farm.Id + " for=" + forItem);
        }

        /// <summary>Whether a station with a Farm installed grows <paramref name="crop"/> — what makes
        /// that crop covered.</summary>
        private static bool AnyFarmGrows(FactionState st, IProductionExecutionContext ctx, string crop)
        {
            foreach (SpatialNode n in ctx.OwnedNodes(st.Tag))
            {
                int level;
                if (n.Capabilities.TryGetValue(NodeCapabilityKind.Farm, out level) && level > 0
                    && GrowsAt(st, ctx, n, crop)) return true;
            }
            return false;
        }

        private static bool GrowsAt(FactionState st, IProductionExecutionContext ctx, SpatialNode n, string crop)
        {
            return FarmCrops.Grows(crop, FarmCrops.NativeAt(n, ctx.Planets), st.IsPlayerFaction,
                ctx.GetNodeInventory(n.Id, InventoryKeyMapper.SeedPrefix + crop));
        }

        /// <summary>The lowest capability in <paramref name="cap"/>'s prerequisite chain that
        /// <paramref name="installed"/> lacks, or <paramref name="cap"/> itself when its whole chain
        /// is present.</summary>
        public static NodeCapabilityKind FirstMissingPrerequisite(NodeCapabilityKind cap,
            ICollection<NodeCapabilityKind> installed)
        {
            NodeCapabilityKind current = cap;
            for (int guard = 0; guard < FactionTuning.CapabilityKindCount; guard++)
            {
                NodeCapabilityKind? prereq = CapabilityPrerequisites.For(current);
                if (!prereq.HasValue || (installed != null && installed.Contains(prereq.Value))) return current;
                current = prereq.Value;
            }
            return current;
        }

        private static readonly ItemKind[] s_installCostKinds =
            new ItemKind[] { ItemKind.Ore, ItemKind.Ingot, ItemKind.ProcessedGood, ItemKind.Hydrogen };
        private static readonly ItemKind[] s_noInstallCostKinds = new ItemKind[0];

        private void PostInstallCostDemands(string factionTag, NodeCapabilityKind targetCap,
            HashSet<NodeCapabilityKind> networkCaps, IProductionExecutionContext ctx, FactionTuning tuning)
        {
            Dictionary<string, long> bom;
            if (tuning.TryGetInstallBom(targetCap, out bom))
            {
                // BOM-backed capability: demand the exact components a real block needs, not a
                // same-ItemKind stand-in — so routing brings in SteelPlate/Construction/etc.
                // specifically instead of whatever ProcessedGood happens to be cheapest to produce.
                foreach (var kv in bom)
                {
                    string key = kv.Key;
                    long need = kv.Value;
                    long have = ctx.GetFactionInventoryByKey(factionTag, key)
                              + ctx.GetFactionReservedInventoryByKey(factionTag, key);
                    if (have >= need) continue;

                    bool isNew = PostDemand(ctx, new DemandEntry
                    {
                        FactionTag = factionTag,
                        Kind       = "Item:" + key,
                        ExpiresUtc = ctx.UtcNow.Add(InvestmentTtlFor(tuning)),
                        Quantity   = need - have,
                        Class      = DemandClass.Install,
                    });
                    // The same outstanding requirement, addressed as an operational need so
                    // PriceOracle can raise this component's reference stock while the install is
                    // pending. It rides beside the Item: demand rather than replacing it: several
                    // posters share that unkeyed slot and PostCatalogDemandsScalar overwrites this
                    // quantity later in the same cycle (see DemandEntry's "Need:" note).
                    PostDemand(ctx, DemandEntry.ForNeed(factionTag, key, null,
                        ctx.UtcNow.Add(InvestmentTtlFor(tuning)), need - have, DemandClass.Install));
                    if (isNew)
                        ctx.AddLog(factionTag + " INSTALL_DEMAND " + key
                                + " need=" + (need - have) + " for=" + targetCap);
                }
                return;
            }

            // Flat per-ItemKind fallback for a capability with no real block/prefab BOM. Deliberately
            // raises no "Need:" twin: it posts the WHOLE kind-level cost against every producible
            // subtype of that kind ("300 ProcessedGood -- any of these will do"), so feeding it to
            // PriceOracle would raise the reference of all of them to 300 each and target a multiple
            // of the real need. Only the exact per-component BOM branch above carries a concrete
            // enough quantity to be a reference.
            foreach (ItemKind kind in s_installCostKinds)
            {
                long need = tuning.InstallCostFor(targetCap, kind);
                if (need <= 0) continue;
                if (GetInstallStockAvailable(ctx, factionTag, kind) >= need) continue;

                foreach (NodeCapabilityKind installedCap in networkCaps)
                {
                    List<ConversionRecipe> recipes;
                    if (!_capRecipes.TryGetValue(installedCap, out recipes)) continue;
                    foreach (ConversionRecipe recipe in recipes)
                    {
                        if (recipe.Outputs == null) continue;
                        foreach (RecipeItem outp in recipe.Outputs)
                        {
                            string outKey = InvKey(outp);
                            if (ItemKindFromInvKey(outKey) != kind) continue;
                            bool isNew = PostDemand(ctx, new DemandEntry
                            {
                                FactionTag = factionTag,
                                Kind       = "Item:" + outKey,
                                ExpiresUtc = ctx.UtcNow.Add(InvestmentTtlFor(tuning)),
                                Quantity   = need,
                                Class      = DemandClass.Install,
                            });
                            if (isNew)
                                ctx.AddLog(factionTag + " INSTALL_DEMAND " + outKey
                                        + " need=" + need + " for=" + targetCap);
                        }
                    }
                }
            }
        }

        private static long GetInstallStockAvailable(IProductionExecutionContext ctx, string factionTag, ItemKind kind)
        {
            return ctx.GetFactionInventory(factionTag, kind) + ctx.GetFactionReservedInventory(factionTag, kind);
        }

        private void PostInstallCostDemandsForInvestmentRequests(string factionTag,
            HashSet<NodeCapabilityKind> networkCaps, IProductionExecutionContext ctx, FactionTuning tuning)
        {
            List<InvestmentRequest> requests = ctx.SnapshotInvestments(factionTag);
            foreach (InvestmentRequest req in requests)
                PostInstallCostDemands(factionTag, req.Cap, networkCaps, ctx, tuning);
        }

        /// <summary>Every founded node's fuel reserve, once per cycle. docs/fuel-plan.md's "Every
        /// founded node asks for a reserve": the keys are whatever this faction's own hulls burn, and
        /// the quantity is its measured burn over <see cref="FactionTuning.FuelReserveHours"/>,
        /// floored. Silent below <see cref="FuelDrawModes.Draw"/> — a reserve is a want for fuel that
        /// is actually consumed, and nothing consumes it until the draw is armed.</summary>
        private void PostFuelReserveDemands(string factionTag, List<SpatialNode> ownedNodes,
            IProductionExecutionContext ctx, FactionTuning tuning)
        {
            if (tuning == null || tuning.FuelDrawMode < FuelDrawModes.Draw) return;
            if (ownedNodes == null || ownedNodes.Count == 0)
            {
                if (_reserveWorld != null) _reserveWorld.SetFuelReserveKeys(factionTag, null);
                return;
            }

            List<string> keys = FuelReserve.FactionFuelKeys(ownedNodes, _prefabCatalogue);
            if (_reserveWorld != null) _reserveWorld.SetFuelReserveKeys(factionTag, keys);
            if (keys.Count == 0) return;

            DateTime now = ctx.UtcNow;
            DateTime expires = now.Add(CatalogDemandTtlFor(tuning));
            for (int i = 0; i < ownedNodes.Count; i++)
                FuelReserve.PostForNode(factionTag, ownedNodes[i], keys, ctx, tuning, now, expires);

            if (DebugTraces.Fuel) TraceFuelBuffers(factionTag, ownedNodes, keys, ctx, tuning, now);
        }

        /// <summary>Last departure buffer written to <c>FUEL_BUFFER</c> per (faction, node, key), so
        /// the line fires on a material change rather than once a node per cycle. Filled only while
        /// <c>factions debug fuel</c> is on.</summary>
        private readonly Dictionary<string, long> _fuelBufferTraced = new Dictionary<string, long>(StringComparer.Ordinal);

        private void TraceFuelBuffers(string factionTag, List<SpatialNode> ownedNodes, List<string> keys,
            IProductionExecutionContext ctx, FactionTuning tuning, DateTime now)
        {
            for (int i = 0; i < ownedNodes.Count; i++)
            {
                SpatialNode node = ownedNodes[i];
                if (node == null || node.StableStationId == 0) continue;
                for (int k = 0; k < keys.Count; k++)
                {
                    string key = keys[k];
                    long buffer = FuelReserve.BufferTarget(node, key, now, tuning);
                    string traceKey = factionTag + "|" + node.Id + "|" + key;
                    long prior;
                    _fuelBufferTraced.TryGetValue(traceKey, out prior);
                    if (!FuelReserve.BufferChangedMaterially(prior, buffer)) continue;

                    if (buffer > 0) _fuelBufferTraced[traceKey] = buffer;
                    else _fuelBufferTraced.Remove(traceKey);
                    ctx.AddLog(FuelReserve.FormatBufferLine(factionTag, node.Id, key,
                        FuelDraw.BurnRateAt(node, key, now, tuning), tuning.FuelBufferLeadHours, buffer,
                        ctx.GetNodeInventory(node.Id, key)));
                }
            }
        }

        /// <summary>Every founded node's standing ammunition reserve, once per cycle, beside the fuel
        /// reserve and for the same reason: a fleet leaves with what its node holds, so the node has
        /// to be stocked before the departure, not after. See
        /// <see cref="FleetMunitions.PostReserveForNode"/>. Silent when the munition model is off or
        /// <see cref="FactionTuning.MunitionReserveLoads"/> is 0.</summary>
        private void PostMunitionReserveDemands(string factionTag, List<SpatialNode> ownedNodes,
            IProductionExecutionContext ctx, FactionTuning tuning)
        {
            if (tuning == null || tuning.MunitionDrawMode < MunitionDrawModes.Draw || tuning.MunitionReserveLoads <= 0) return;
            if (ownedNodes == null || ownedNodes.Count == 0 || _prefabCatalogue == null) return;

            var itemRows = new Dictionary<string, DemandEntry>(StringComparer.Ordinal);
            foreach (DemandEntry d in ctx.SnapshotItemDemands(factionTag))
                if (d.NodeId != null) itemRows[d.NodeId + "|" + d.Kind] = d;
            var perHull = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

            DateTime expires = ctx.UtcNow.Add(CatalogDemandTtlFor(tuning));
            for (int i = 0; i < ownedNodes.Count; i++)
                FleetMunitions.PostReserveForNode(factionTag, ownedNodes[i], _prefabCatalogue, ctx, tuning,
                                                  expires, itemRows, perHull);
        }

        /// <summary>Every Habitat's food, once per cycle beside the reserves (<see cref="WorkerFoodDemand"/>).
        /// The candidate items a node may name when nothing is held elsewhere are what this faction's
        /// installed capabilities output, gathered only when some node has workers to feed.</summary>
        private void PostWorkerFoodDemands(string factionTag, HashSet<NodeCapabilityKind> networkCaps,
            List<SpatialNode> ownedNodes, IProductionExecutionContext ctx, FactionTuning tuning)
        {
            INutritionCatalogue food = _reserveWorld != null ? _reserveWorld.Nutrition : null;
            if (food == null || tuning == null || ownedNodes == null) return;

            HashSet<string> producible = null;
            DateTime expires = ctx.UtcNow.Add(CatalogDemandTtlFor(tuning));
            for (int i = 0; i < ownedNodes.Count; i++)
            {
                SpatialNode n = ownedNodes[i];
                if (n == null || n.Workers <= 0) continue;
                if (producible == null) producible = OutputsOf(networkCaps);

                long units; double have, target; bool isNew;
                string key = WorkerFoodDemand.PostForNode(factionTag, n, ctx, tuning, food, producible, expires,
                    out units, out have, out target, out isNew);
                if (key != null && isNew)
                    ctx.AddLog(factionTag + " WORKER_FOOD_DEMAND " + key + " node=" + n.Id + " units=" + units
                        + " nutrition=" + have.ToString("0.##", CultureInfo.InvariantCulture)
                        + "/" + target.ToString("0.##", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Every inventory key a recipe of an installed capability outputs.</summary>
        private HashSet<string> OutputsOf(HashSet<NodeCapabilityKind> networkCaps)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (networkCaps == null) return keys;
            foreach (NodeCapabilityKind cap in networkCaps)
            {
                List<ConversionRecipe> recipes;
                if (!_capRecipes.TryGetValue(cap, out recipes) || recipes == null) continue;
                foreach (ConversionRecipe recipe in recipes)
                {
                    if (recipe.Outputs == null) continue;
                    foreach (RecipeItem outp in recipe.Outputs) keys.Add(InvKey(outp));
                }
            }
            return keys;
        }

        /// <summary>The faction's Stock standing orders, once per cycle after the reserves, so a
        /// reserve row at the same node and item is already in the register when the order decides
        /// whether it is covered. Also republishes the orders' keeps (<see cref="StockOrders.Keep"/>)
        /// from the current list and ownership.</summary>
        private void PostStockOrderDemands(FactionState st, IProductionExecutionContext ctx, FactionTuning tuning)
        {
            if (st == null || _reserveWorld == null) return;
            Dictionary<(string NodeId, string ItemKey), long> targets = StockOrders.Publish(st, _reserveWorld);
            if (targets == null) return;

            var itemRows = new Dictionary<string, DemandEntry>(StringComparer.Ordinal);
            foreach (DemandEntry d in ctx.SnapshotItemDemands(st.Tag))
                if (d.NodeId != null) itemRows[d.NodeId + "|" + d.Kind] = d;
            StockOrders.PostDemands(st.Tag, targets, ctx, ctx.UtcNow.Add(CatalogDemandTtlFor(tuning)), itemRows);
        }

        /// <summary>Stocks the processed goods an NPC found holds back, once the faction has a claim
        /// the brain would found (<c>FactionBrain.AddFoundingCandidates</c>). Not for a player faction:
        /// its found is paid from the treasury alone and never reads this stock.</summary>
        private void PostFoundingCostDemands(FactionState st, HashSet<NodeCapabilityKind> networkCaps,
            IProductionExecutionContext ctx, FactionTuning tuning)
        {
            if (st.IsPlayerFaction) return;
            string factionTag = st.Tag;
            bool hasCandidate = false;
            foreach (SpatialNode n in ctx.OwnedNodes(factionTag))
            {
                if (n.StableStationId != 0) continue;
                if (SourceMapping.IsScanBacked(n.Id)) continue;
                if (n.HoldStrength < tuning.FoundHoldThreshold) continue;
                hasCandidate = true;
                break;
            }
            if (!hasCandidate) return;

            long pgNeed = tuning.FoundItemCostProcessedGood;
            if (pgNeed <= 0 || ctx.GetFactionInventory(factionTag, ItemKind.ProcessedGood) >= pgNeed) return;

            foreach (NodeCapabilityKind installedCap in networkCaps)
            {
                List<ConversionRecipe> recipes;
                if (!_capRecipes.TryGetValue(installedCap, out recipes)) continue;
                foreach (ConversionRecipe recipe in recipes)
                {
                    if (recipe.Outputs == null) continue;
                    foreach (RecipeItem outp in recipe.Outputs)
                    {
                        string outKey = InvKey(outp);
                        if (ItemKindFromInvKey(outKey) != ItemKind.ProcessedGood) continue;
                        bool isNew = PostDemand(ctx, new DemandEntry
                        {
                            FactionTag = factionTag,
                            Kind       = "Item:" + outKey,
                            ExpiresUtc = ctx.UtcNow.Add(InvestmentTtlFor(tuning)),
                            Quantity   = pgNeed,
                            Class      = DemandClass.Install,
                        });
                        if (isNew)
                            ctx.AddLog(factionTag + " FOUNDING_DEMAND " + outKey + " need=" + pgNeed);
                    }
                }
            }
        }

        /// <summary>
        /// For each node item held above <see cref="RefinementMinQty"/> that an installed capability
        /// can refine, posts an output demand to open the pull gate. Investment signals are handled
        /// separately by <see cref="PostThroughputInvestmentRequests"/>.
        ///
        /// <para>Every recipe an installed capability has for the held item is visited, and each
        /// distinct output is asked for at most once per node. Ore→Ingot outputs always post. Every
        /// other pairing (Ingot→Component, Ice→Gas, Component→Weapon/Ammo/Consumable upgrades) posts
        /// only when the node is short of the output — the same scarcity the pull gate admits on — and
        /// the faction holds less than its shop-limit cap: an input sitting around is not reason to
        /// make an output nobody is short of, and one capped output does not silence the input's
        /// other recipes.</para>
        /// </summary>
        private void PostRefinementRequests(string factionTag, HashSet<NodeCapabilityKind> networkCaps,
            List<SpatialNode> ownedNodes, IProductionExecutionContext ctx, FactionTuning tuning)
        {
            foreach (SpatialNode node in ownedNodes)
                PostRefinementRequestsForNode(factionTag, networkCaps, node, ctx, tuning);
        }

        /// <summary>One node's share of <see cref="PostRefinementRequests"/> — the bounded unit the
        /// resumable cycle's PostPassesNodes phase checkpoints on. Every node is independent (no
        /// cross-node accumulator), so this can run in any order at any cadence relative to other
        /// nodes.</summary>
        private void PostRefinementRequestsForNode(string factionTag, HashSet<NodeCapabilityKind> networkCaps,
            SpatialNode node, IProductionExecutionContext ctx, FactionTuning tuning)
        {
            if (_refinementIndex.Count == 0) return;

            var snapshot = new List<KeyValuePair<string, long>>(ctx.SnapshotNodeInventory(node.Id));
            var asked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in snapshot)
            {
                if (kv.Value < RefinementMinQty) continue;

                List<RefinementEntry> entries;
                if (!_refinementIndex.TryGetValue(kv.Key, out entries)) continue;

                bool inputIsOre = ItemKindFromInvKey(kv.Key) == ItemKind.Ore;
                foreach (RefinementEntry e in entries)
                {
                    if (!networkCaps.Contains(e.Cap)) continue;
                    if (e.Recipe.Outputs != null)
                    {
                        foreach (RecipeItem outp in e.Recipe.Outputs)
                        {
                            string outKey = InvKey(outp);
                            if (!asked.Add(outKey)) continue; // already asked for at this node
                            bool isOreToIngot = inputIsOre && ItemKindFromInvKey(outKey) == ItemKind.Ingot;
                            if (!isOreToIngot)
                            {
                                if (!NodeIsShortOf(node, outKey)) continue;
                                long cap = ResolveStoreCap(ctx, tuning, outKey);
                                if (cap > 0 && ctx.GetFactionInventoryByKey(factionTag, outKey) >= cap)
                                    continue; // already at/above its shop limit — no need to refine more
                            }

                            bool isNew = PostDemand(ctx, new DemandEntry
                            {
                                FactionTag = factionTag,
                                Kind       = "Item:" + outKey,
                                ExpiresUtc = ctx.UtcNow.Add(RefinementDemandTtlFor(tuning)),
                                Quantity   = EstimateYield(e.Recipe, kv.Value),
                                Class      = DemandClass.RecipeInput,
                            });
                            if (isNew)
                                ctx.AddLog(factionTag + " REFINE_DEMAND " + outKey
                                        + " from=" + kv.Key + " node=" + node.Id);
                        }
                    }
                }
            }
        }

        /// <summary>Whether <paramref name="node"/> is short of <paramref name="invKey"/> by the same
        /// reading <see cref="ScarcityPriority"/> admits on: a multiplier above 1.0, or any output at
        /// all when no oracle is bound (the legacy scheduling isolated callers rely on).</summary>
        private bool NodeIsShortOf(SpatialNode node, string invKey)
        {
            if (ReferenceEquals(_priceOracle, NullPriceOracle.Instance)) return true;
            return _priceOracle.ScarcityMultiplier(node.Id, invKey) > 1.0;
        }

        /// <summary>
        /// Posts <see cref="InvestmentRequest"/> entries when a production stage is throughput-
        /// constrained: income rate exceeds the installed capability's consumption rate. Covers
        /// two stages ÔÇö OreÔåÆIngot (Refinery chain) and IngotÔåÆComponent (Manufactory chain) ÔÇö
        /// including bootstrap requests when no capability of that tier exists yet.
        /// The brain's <see cref="NodeContent.CapabilityAffinityFor"/> scoring then selects
        /// the best node to develop.
        /// </summary>
        /// <summary>One of this faction's active machine slots, with the capability index already
        /// parsed out of its <c>tag:node:capIdx:slotIdx</c> key. The slot dictionary is shared across
        /// every faction, so resolving the throughput rates straight from it meant re-scanning and
        /// re-parsing all of it once per rate query; collecting this list once per cycle replaces
        /// three such scans with one.</summary>
        private struct ThroughputSlotRef
        {
            internal int CapIdx;
            internal string RecipeId;
            /// <summary>The node the slot belongs to -- the middle segment of the slot key, which
            /// the network-wide rates never needed and the per-region ledger cannot do without.</summary>
            internal string NodeId;
        }

        /// <summary>Scans the shared slot dictionary once, keeping this faction's active slots.
        /// Selection is deliberately identical to the per-query filter it replaces (tag prefix,
        /// non-null ActiveRecipeId, parsable in-range capability index) so the rate totals are
        /// unchanged.</summary>
        private List<ThroughputSlotRef> CollectThroughputSlots(Dictionary<string, MachineSlot> slots,
            string factionTag)
        {
            string prefix = factionTag + ":";
            var result = new List<ThroughputSlotRef>();
            foreach (var kv in slots)
            {
                if (!kv.Key.StartsWith(prefix)) continue;
                MachineSlot slot = kv.Value;
                if (slot.ActiveRecipeId == null) continue;

                string rest       = kv.Key.Substring(prefix.Length);
                int    lastColon  = rest.LastIndexOf(':');
                if (lastColon < 0) continue;
                string withoutSlot = rest.Substring(0, lastColon);
                int    secondLast  = withoutSlot.LastIndexOf(':');
                if (secondLast < 0) continue;
                int capIdx;
                if (!int.TryParse(withoutSlot.Substring(secondLast + 1), out capIdx)) continue;
                if (capIdx < 0 || capIdx >= _models.Length) continue;

                result.Add(new ThroughputSlotRef
                {
                    CapIdx   = capIdx,
                    RecipeId = slot.ActiveRecipeId,
                    NodeId   = withoutSlot.Substring(0, secondLast),
                });
            }
            return result;
        }

        /// <summary>Splits the network's installed capabilities into the refinery-tier and
        /// manufactory-tier sets the throughput comparison measures against.</summary>
        private static void BuildThroughputCapSets(HashSet<NodeCapabilityKind> networkCaps,
            out HashSet<NodeCapabilityKind> refineCaps, out HashSet<NodeCapabilityKind> manufCaps)
        {
            refineCaps = new HashSet<NodeCapabilityKind>();
            if (networkCaps.Contains(NodeCapabilityKind.Refinery))   refineCaps.Add(NodeCapabilityKind.Refinery);
            if (networkCaps.Contains(NodeCapabilityKind.RefineryII)) refineCaps.Add(NodeCapabilityKind.RefineryII);
            // A prototech refinery refines the same Ingots class, four times faster. Left out of this
            // set its throughput would not count as installed capacity, and the stage-1 comparison
            // would keep asking for another refinery while the ore was already being consumed.
            if (networkCaps.Contains(NodeCapabilityKind.PrototechRefinery)) refineCaps.Add(NodeCapabilityKind.PrototechRefinery);

            manufCaps = new HashSet<NodeCapabilityKind>();
            if (networkCaps.Contains(NodeCapabilityKind.Manufactory))   manufCaps.Add(NodeCapabilityKind.Manufactory);
            if (networkCaps.Contains(NodeCapabilityKind.ManufactoryII)) manufCaps.Add(NodeCapabilityKind.ManufactoryII);
            if (networkCaps.Contains(NodeCapabilityKind.PrototechForge)) manufCaps.Add(NodeCapabilityKind.PrototechForge);
        }

        /// <summary>One owned node's ore + rare-ore income, in units/min.</summary>
        private static double NodeOreIncomePerMin(SpatialNode n, string factionTag,
            IProductionExecutionContext ctx, FactionTuning tuning)
        {
            double control  = ctx.InfluenceControl(n.Position, factionTag);
            double econToMin = 60.0 / tuning.EconomyIntervalSeconds;
            return (Economy.SourceItemYield(n, control, tuning, ItemKind.Ore)
                 +  Economy.SourceItemYield(n, control, tuning, ItemKind.RareOre)) * econToMin;
        }

        /// <summary>This slot's contribution (units/min) to the rate identified by
        /// <paramref name="caps"/>/<paramref name="invKeyPrefix"/>/<paramref name="inputSide"/>, or
        /// zero when it does not belong to that query. Extracted so all three rates can be summed in
        /// a single pass over the collected slots instead of one pass each.</summary>
        private double SlotRatePerMin(ThroughputSlotRef s, HashSet<NodeCapabilityKind> caps,
            string invKeyPrefix, bool inputSide)
        {
            if (caps.Count == 0) return 0.0;
            var cap = (NodeCapabilityKind)s.CapIdx;
            if (!caps.Contains(cap)) return 0.0;

            MachineModel model = _models[s.CapIdx];
            if (model.Speed <= 0) return 0.0;

            List<ConversionRecipe> recipes;
            if (!_capRecipes.TryGetValue(cap, out recipes)) return 0.0;
            ConversionRecipe recipe = FindRecipe(recipes, s.RecipeId);
            if (recipe == null || recipe.TimeSecs <= 0f) return 0.0;

            double batchesPerMin = model.Speed / recipe.TimeSecs * 60.0;
            RecipeItem[] items   = inputSide ? recipe.Inputs : recipe.Outputs;
            if (items == null) return 0.0;

            double total = 0.0;
            foreach (RecipeItem item in items)
                if (InvKey(item).StartsWith(invKeyPrefix))
                    total += batchesPerMin * (inputSide
                        ? MachinePhysics.InputAmount(item.Amount, model, recipe.IgnoresEfficiencyMultiplier)
                        : item.Amount * model.OutputScale);
            return total;
        }

        /// <summary>
        /// Sums the rate (units/min) of items matching <paramref name="invKeyPrefix"/> across the
        /// collected slots running capabilities in <paramref name="caps"/>. Pass
        /// <paramref name="inputSide"/>=true to measure consumption (recipe inputs), false to measure
        /// production (recipe outputs).
        /// </summary>
        private double ComputeCapRatePerMin(List<ThroughputSlotRef> slotRefs,
            HashSet<NodeCapabilityKind> caps, string invKeyPrefix, bool inputSide)
        {
            double total = 0.0;
            for (int i = 0; i < slotRefs.Count; i++)
                total += SlotRatePerMin(slotRefs[i], caps, invKeyPrefix, inputSide);
            return total;
        }

        /// <summary>How badly a production stage is out of balance, as urgency: 0 at parity, 1.0 once
        /// income is twice what the installed machines consume.
        ///
        /// <para><see cref="InvestmentRequest.Urgency"/> has always documented itself as "computed by
        /// the posting manager from stall duration or shortfall magnitude", and every poster ignored
        /// that and sent a constant. <see cref="DecideThroughputInvestments"/> already measures both
        /// rates and then threw the magnitude away, so a faction 3% oversupplied asked exactly as
        /// loudly as one 300% oversupplied -- and since the request is re-posted every tick and
        /// <see cref="InvestmentTargeting"/> skips only nodes already at max level, a flat 1.0 wins
        /// every Develop tick on some fresh node forever. Grounding it is what lets the request decay
        /// as capacity catches up, without level count standing in as a proxy for satisfaction.</para>
        ///
        /// <para><paramref name="consumedPerMin"/> of zero means nothing installed is consuming the
        /// input at all, which is the strongest imbalance there is rather than an undefined ratio --
        /// see stage 2's own note on an idle Manufactory.</para></summary>
        internal static double ImbalanceUrgency(double incomePerMin, double consumedPerMin)
        {
            if (consumedPerMin <= 0) return 1.0;
            double excess = incomePerMin / consumedPerMin - 1.0;
            if (excess <= 0) return 0.0;
            return excess >= 1.0 ? 1.0 : excess;
        }

        /// <summary>What fraction of a stage's installed machines are actually holding a recipe.
        ///
        /// <para><b>This is the term that stops the request asking for a remedy it cannot use.</b>
        /// Slot allocation is just-in-time -- a slot holds a recipe only while that recipe has inputs
        /// AND live demand this tick -- so <see cref="CollectThroughputSlots"/> sees only the running
        /// ones and the consumption rate beneath it is sampled from exactly those instants. An
        /// under-run bank therefore reports a small <c>consumed</c>, which inflates
        /// <see cref="ImbalanceUrgency"/> and asks for more machines; the new machines are idle for
        /// the same reason, so the ratio never falls. The loop has no fixed point, because adding
        /// capacity cannot raise consumption when the constraint is input arrival or demand.</para>
        ///
        /// <para>Measured live 2026-09-02: <b>50 busy slots against 384 installed machines
        /// world-wide (13%)</b>, five factions at exactly 0%, FCTM at 7 of 66. Scaling by
        /// utilisation says an imbalance matters in proportion to how saturated the stage already
        /// is, which is the honest reading of a number sampled this way.</para>
        ///
        /// <para>Returns 1.0 when nothing is installed -- there is no dilution to apply, and the
        /// bootstrap branch owns that case anyway.</para></summary>
        private double StageUtilisation(List<ThroughputSlotRef> slotRefs,
            HashSet<NodeCapabilityKind> caps, List<SpatialNode> ownedNodes)
        {
            if (caps == null || caps.Count == 0) return 1.0;

            int busy = 0;
            if (slotRefs != null)
                for (int i = 0; i < slotRefs.Count; i++)
                {
                    int idx = slotRefs[i].CapIdx;
                    if (idx >= 0 && caps.Contains((NodeCapabilityKind)idx)) busy++;
                }

            int installed = 0;
            if (ownedNodes != null)
                foreach (SpatialNode node in ownedNodes)
                {
                    if (node == null || node.Capabilities == null) continue;
                    foreach (KeyValuePair<NodeCapabilityKind, int> cap in node.Capabilities)
                    {
                        if (cap.Value <= 0 || !caps.Contains(cap.Key)) continue;
                        int idx = (int)cap.Key;
                        if (idx < 0 || idx >= _models.Length) continue;
                        installed += cap.Value * _models[idx].MachineCount;
                    }
                }

            if (installed <= 0) return 1.0;
            double u = (double)busy / installed;
            return u > 1.0 ? 1.0 : u;
        }

        /// <summary>What the gas stage decides on: the fuel this faction's nodes report burning, the
        /// fuel its installed FuelDepots could make, and the ice a further depot would have to
        /// eat.</summary>
        internal struct GasStageRates
        {
            public double BurnPerMin;
            public double CapacityPerMin;
            public double IcePerLevelPerMin;
            public long   IceUnits;
            public string FuelKey;
            /// <summary>What one depot level makes of <see cref="FuelKey"/>, units per minute: the
            /// conversion <see cref="CapacityPerMin"/> is levels times.</summary>
            public double LevelPerMin;
            /// <summary>FuelDepot levels installed across the estate.</summary>
            public int Levels;
            /// <summary>Levels an install could still add at founded, depot-suitable nodes, up to the
            /// level cap. Zero means the estate is full and a request would have nowhere to land.</summary>
            public int Headroom;
            /// <summary>Ice the faction's Mine trips bound for its ice hubs deliver in an hour
            /// (<see cref="StandingExtractionOrder.InFlightPerHour"/>, summed).</summary>
            public double IceInFlight;
            /// <summary>True when the faction's last extraction plan holds an open ice order: not
            /// paused, with a positive target rate.</summary>
            public bool IceOrderOpen;
            /// <summary>The same measurements per region (<see cref="NodeRegion"/>), with open-space
            /// nodes under <see cref="NodeRegion.None"/>. What siting reads; null when nothing was
            /// measured.</summary>
            public Dictionary<NodeRegion, GasRegionLedger> Regions;
        }

        /// <summary>Measures the gas stage in one pass over the owned nodes.
        ///
        /// <para><b>Capacity, not observed consumption</b> — the one place this stage deliberately
        /// departs from stages 1 and 2. A depot holds no recipe whenever its gas is not demanded
        /// this tick, so a utilisation-damped reading of running slots reports "capacity is not the
        /// constraint" at exactly the moment the fleet is burning more than the estate can make.
        /// What is installed is a property of the estate rather than of this tick, so the comparison
        /// has a fixed point: every level added raises it and the urgency falls.</para>
        ///
        /// <para>The burn rate comes from <see cref="SpatialNode.FuelBurnEma"/>, which
        /// <see cref="FuelDraw.MeterBurn"/> keeps current in measure mode as well as under a live
        /// draw. Reactor fuel is excluded: <c>Ingot:Uranium</c> is a refinery product and belongs to
        /// stage 1.</para>
        ///
        /// <para>The same pass fills a ledger per <see cref="NodeRegion"/> — burn, levels, headroom,
        /// ice held and ice in flight — for <see cref="DecideFuelDepotGrowth"/> to site the next level
        /// by. Ice in flight is read off <paramref name="st"/>'s published extraction plan, one entry
        /// per ice order; no fleet loop.</para></summary>
        private GasStageRates MeasureGasStage(FactionState st, List<SpatialNode> ownedNodes,
            IProductionExecutionContext ctx, FactionTuning tuning)
        {
            var rates = new GasStageRates();
            if (ownedNodes == null || ctx == null || tuning == null) return rates;

            int capIdx = (int)NodeCapabilityKind.FuelDepot;
            if (capIdx < 0 || capIdx >= _models.Length) return rates;
            MachineModel model = _models[capIdx];
            if (model.MachineCount <= 0 || model.Speed <= 0) return rates;

            string iceKey = "Ore:" + tuning.IceOreSubTypeId;
            DateTime now = ctx.UtcNow;
            var burnPerMin = new Dictionary<string, double>(StringComparer.Ordinal);
            int slots = 0;
            int maxLevel = tuning.MaxLevelFor(NodeCapabilityKind.FuelDepot);
            Dictionary<string, double> iceInFlightByHub = IceInFlightByHub(st, iceKey, ref rates);
            rates.Regions = new Dictionary<NodeRegion, GasRegionLedger>();

            foreach (SpatialNode node in ownedNodes)
            {
                if (node == null) continue;
                GasRegionLedger region = GasRegionFor(rates.Regions, NodeRegion.Of(node));

                int level;
                if (node.Capabilities == null
                    || !node.Capabilities.TryGetValue(NodeCapabilityKind.FuelDepot, out level) || level < 0)
                    level = 0;
                if (level > 0)
                {
                    slots += level * model.MachineCount;
                    rates.Levels += level;
                    region.Levels += level;
                }

                // Where another level could still go: the gates InvestmentTargeting resolves with
                // that are properties of the node alone -- founded, below the cap, some affinity.
                if (node.StableStationId != 0 && level < maxLevel
                    && NodeContent.CapabilityAffinityFor(node, NodeCapabilityKind.FuelDepot, tuning) > 0)
                {
                    rates.Headroom += maxLevel - level;
                    region.Headroom += maxLevel - level;
                }

                long ice = ctx.GetNodeInventory(node.Id, iceKey);
                rates.IceUnits += ice;
                region.IceUnits += ice;

                double inFlight;
                if (iceInFlightByHub != null && iceInFlightByHub.TryGetValue(node.Id, out inFlight))
                {
                    rates.IceInFlight += inFlight;
                    region.IceInFlight += inFlight;
                }

                foreach (KeyValuePair<string, double> burned in node.FuelBurnEma)
                {
                    if (!burned.Key.StartsWith(InventoryKeyMapper.GasPrefix, StringComparison.Ordinal)) continue;
                    double perHour = FuelDraw.BurnRateAt(node, burned.Key, now, tuning);
                    if (perHour <= 0) continue;
                    double prior;
                    burnPerMin.TryGetValue(burned.Key, out prior);
                    burnPerMin[burned.Key] = prior + perHour / 60.0;
                    region.BurnPerMin += perHour / 60.0;
                }
            }

            // Slots run one gas at a time off a shared ice stream, so capacity is only meaningful
            // against a named gas: the one being burned hardest is the one those slots would hold.
            double dominant = 0;
            foreach (KeyValuePair<string, double> kv in burnPerMin)
            {
                rates.BurnPerMin += kv.Value;
                if (kv.Value > dominant) { dominant = kv.Value; rates.FuelKey = kv.Key; }
            }

            List<ConversionRecipe> recipes;
            if (!_capRecipes.TryGetValue(NodeCapabilityKind.FuelDepot, out recipes) || recipes == null)
                return rates;

            // An estate that has not burned anything yet still has a capacity, so with no fuel named
            // the key falls back to the richest gas the depots can make: capacity is a property of
            // what is installed and is reported as one whether or not a fleet has flown.
            ConversionRecipe chosen = rates.FuelKey != null ? RecipeMaking(recipes, rates.FuelKey) : null;
            if (chosen == null)
            {
                foreach (ConversionRecipe recipe in recipes)
                    if (BestGasRate(recipe) > BestGasRate(chosen)) chosen = recipe;
                if (chosen == null) return rates;
                rates.FuelKey = RichestGasOutput(chosen);
            }

            double batchesPerMin = model.Speed / chosen.TimeSecs * 60.0;
            foreach (RecipeItem outp in chosen.Outputs)
                if (InvKey(outp) == rates.FuelKey)
                {
                    rates.LevelPerMin = model.MachineCount * batchesPerMin * outp.Amount * model.OutputScale;
                    rates.CapacityPerMin = slots * batchesPerMin * outp.Amount * model.OutputScale;
                }
            if (chosen.Inputs != null && chosen.Inputs.Length > 0)
                rates.IcePerLevelPerMin = model.MachineCount * batchesPerMin * chosen.Inputs[0].Amount;
            return rates;
        }

        /// <summary>The gas recipe whose output is <paramref name="key"/>, or null.</summary>
        private static ConversionRecipe RecipeMaking(List<ConversionRecipe> recipes, string key)
        {
            foreach (ConversionRecipe recipe in recipes)
            {
                if (recipe == null || recipe.TimeSecs <= 0 || recipe.Outputs == null) continue;
                foreach (RecipeItem outp in recipe.Outputs)
                    if (InvKey(outp) == key) return recipe;
            }
            return null;
        }

        /// <summary>The gas key <paramref name="recipe"/> makes most of per second.</summary>
        private static string RichestGasOutput(ConversionRecipe recipe)
        {
            string key = null;
            double best = 0;
            foreach (RecipeItem outp in recipe.Outputs)
            {
                string outKey = InvKey(outp);
                if (!outKey.StartsWith(InventoryKeyMapper.GasPrefix, StringComparison.Ordinal)) continue;
                double rate = outp.Amount / recipe.TimeSecs;
                if (rate > best) { best = rate; key = outKey; }
            }
            return key;
        }

        /// <summary>Output units a second of the gas <paramref name="recipe"/> makes, for picking the
        /// richest one when no burn has named a fuel yet. Zero for a null recipe, so the first
        /// candidate always wins.</summary>
        private static double BestGasRate(ConversionRecipe recipe)
        {
            if (recipe == null || recipe.TimeSecs <= 0 || recipe.Outputs == null) return 0;
            double best = 0;
            foreach (RecipeItem outp in recipe.Outputs)
            {
                if (!InvKey(outp).StartsWith(InventoryKeyMapper.GasPrefix, StringComparison.Ordinal)) continue;
                double rate = outp.Amount / recipe.TimeSecs;
                if (rate > best) best = rate;
            }
            return best;
        }

        /// <summary>Formats one stage's rates for the THROUGHPUT_INVEST line, so a posted urgency can
        /// be checked against the measurement it came from rather than taken on trust.</summary>
        private static string ThroughputReason(double incomePerMin, double consumedPerMin, double utilisation)
        {
            return "throughput in/out="
                + (consumedPerMin > 0
                    ? (incomePerMin / consumedPerMin).ToString("F2", CultureInfo.InvariantCulture)
                    : "idle")
                + " util=" + utilisation.ToString("F2", CultureInfo.InvariantCulture)
                + " income=" + incomePerMin.ToString("F1", CultureInfo.InvariantCulture)
                + " consumed=" + consumedPerMin.ToString("F1", CultureInfo.InvariantCulture);
        }

        /// <summary>The investment decision itself, given the four measured rates. Shared by the
        /// synchronous pass and the resumable cycle so both reach the same conclusion from the same
        /// inputs.</summary>
        private void DecideThroughputInvestments(FactionTuning tuning, FactionState st, HashSet<NodeCapabilityKind> networkCaps,
            IProductionExecutionContext ctx, double oreIncomePerMin, double oreConsumedPerMin,
            double ingotIncomePerMin, double ingotConsumedPerMin,
            double refineUtilisation, double manufUtilisation, GasStageRates gas)
        {
            bool hasRefinery    = networkCaps.Contains(NodeCapabilityKind.Refinery);
            bool hasManufactory = networkCaps.Contains(NodeCapabilityKind.Manufactory);

            // ---- Stage 1: Ore -> Ingot (Refinery chain) ---------------------------------
            // Bootstrap if no Refinery at all; otherwise compare network ore income against
            // total installed Refinery+RefineryII consumption. The comparison fires regardless
            // of whether RefineryII already exists in the network -- a faction may need multiple
            // RefineryII installations before supply and capacity balance.
            if (!hasRefinery)
            {
                PostThroughputInvestment(tuning, st, NodeCapabilityKind.Refinery, ctx,
                    BootstrapInvestmentUrgency, "bootstrap");
            }
            else if (oreConsumedPerMin > 0 && oreIncomePerMin > oreConsumedPerMin)
            {
                PostThroughputInvestment(tuning, st, BestRefineryUpgrade(networkCaps), ctx,
                    ImbalanceUrgency(oreIncomePerMin, oreConsumedPerMin) * refineUtilisation,
                    ThroughputReason(oreIncomePerMin, oreConsumedPerMin, refineUtilisation));
            }

            // ---- Stage 2: Ingot -> Component (Manufactory chain) ------------------------
            // Same pattern: bootstrap if no Manufactory, then compare refinery output against
            // total Manufactory+ManufactoryII consumption -- fires even when ManufactoryII
            // already exists (the network may need more) and even when Manufactory machines
            // are idle (ingotConsumedPerMin == 0), since an idle Manufactory falling behind
            // refinery output is a stronger signal, not a reason to suppress the upgrade.
            if (!hasManufactory)
            {
                if (ingotIncomePerMin > 0)
                    PostThroughputInvestment(tuning, st, NodeCapabilityKind.Manufactory, ctx,
                        BootstrapInvestmentUrgency, "bootstrap");
            }
            else if (ingotIncomePerMin > ingotConsumedPerMin)
            {
                // An idle bank now asks for NOTHING, which deliberately reverses this stage's older
                // note that "an idle Manufactory falling behind refinery output is a stronger
                // signal". That was written when the urgency was a constant and the only question
                // was whether to post at all. Now the number means "how much more capacity do I
                // need", and a bank running no recipes cannot use another machine -- something else
                // is blocking it, and asking for capacity buries that.
                PostThroughputInvestment(tuning, st, NodeCapabilityKind.ManufactoryII, ctx,
                    ImbalanceUrgency(ingotIncomePerMin, ingotConsumedPerMin) * manufUtilisation,
                    ThroughputReason(ingotIncomePerMin, ingotConsumedPerMin, manufUtilisation));
            }

            // ---- Stage 3: Ice -> Gas (FuelDepot chain) ----------------------------------
            // Asks only for MORE: a faction with no depot at all is PostCapabilityInvestmentRequests'
            // case, which fires off the gas demand itself. Without this stage nothing ever asks for a
            // second one, because that poster marks an item covered once any installed capability can
            // output it -- so one depot answered a whole faction's fuel at any burn.
            if (networkCaps.Contains(NodeCapabilityKind.FuelDepot))
                DecideFuelDepotGrowth(tuning, st, ctx, gas);
        }

        /// <summary>The gas stage's own reason string. It does not borrow
        /// <see cref="ThroughputReason"/> because the damper is ice cover rather than utilisation,
        /// and a line that printed <c>util=</c> for it would be read as the other two stages'
        /// number.</summary>
        private static string GasStageReason(GasStageRates gas, double iceCover)
        {
            return "gas burn/capacity="
                + (gas.CapacityPerMin > 0
                    ? (gas.BurnPerMin / gas.CapacityPerMin).ToString("F2", CultureInfo.InvariantCulture)
                    : "none")
                + " ice_cover=" + iceCover.ToString("F2", CultureInfo.InvariantCulture)
                + " burn=" + gas.BurnPerMin.ToString("F1", CultureInfo.InvariantCulture)
                + " capacity=" + gas.CapacityPerMin.ToString("F1", CultureInfo.InvariantCulture)
                + " fuel=" + (gas.FuelKey ?? "-");
        }

        /// <summary>The refinery tier a throughput-constrained faction should ask for next.
        ///
        /// <para>A prototech refinery is a strict upgrade on RefineryII and the only reason it is not
        /// simply always the answer is that its install BOM is a real prototech block: it cannot be
        /// paid for without <c>Comp:PrototechFrame</c>, which only a <see cref="NodeCapabilityKind.PrototechForge"/>
        /// can make. Owning a forge is therefore the honest test of whether the faction can afford
        /// one, and it is what keeps this from posting a demand nobody in the world can fill.</para></summary>
        private static NodeCapabilityKind BestRefineryUpgrade(HashSet<NodeCapabilityKind> networkCaps)
        {
            return networkCaps.Contains(NodeCapabilityKind.PrototechForge)
                ? NodeCapabilityKind.PrototechRefinery
                : NodeCapabilityKind.RefineryII;
        }

        /// <summary>Synchronous whole-faction throughput pass. The resumable cycle runs the same
        /// steps across several phases (see ProductionManager.Cycle.cs).</summary>
        private void PostThroughputInvestmentRequests(Dictionary<string, MachineSlot> slots, FactionState st,
            HashSet<NodeCapabilityKind> networkCaps, List<SpatialNode> ownedNodes,
            IProductionExecutionContext ctx, FactionTuning tuning)
        {
            HashSet<NodeCapabilityKind> refineCaps, manufCaps;
            BuildThroughputCapSets(networkCaps, out refineCaps, out manufCaps);
            List<ThroughputSlotRef> slotRefs = CollectThroughputSlots(slots, st.Tag);

            double oreIncomePerMin = 0.0;
            // The per-region ledger rides the same has-Refinery node pass the network income does:
            // a faction with no refinery anywhere is the bootstrap branch's case, not a region's.
            Dictionary<NodeRegion, RegionOreLedger> ledger = null;
            if (networkCaps.Contains(NodeCapabilityKind.Refinery))
            {
                ledger = new Dictionary<NodeRegion, RegionOreLedger>();
                Dictionary<string, NodeRegion> regionById = IndexNodeRegions(ownedNodes);
                foreach (SpatialNode n in ownedNodes)
                {
                    double income = NodeOreIncomePerMin(n, st.Tag, ctx, tuning);
                    oreIncomePerMin += income;
                    AccumulateRegionNode(ledger, n, regionById, income, ctx, tuning, refineCaps);
                }
                for (int i = 0; i < slotRefs.Count; i++)
                    AccumulateRegionSlot(ledger, regionById, slotRefs[i], refineCaps);
            }

            double oreConsumedPerMin    = ComputeCapRatePerMin(slotRefs, refineCaps, "Ore:",   inputSide: true);
            double ingotIncomePerMin    = ComputeCapRatePerMin(slotRefs, refineCaps, "Ingot:", inputSide: false);
            double ingotConsumedPerMin  = ComputeCapRatePerMin(slotRefs, manufCaps,  "Ingot:", inputSide: true);

            DecideThroughputInvestments(tuning, st, networkCaps, ctx, oreIncomePerMin, oreConsumedPerMin,
                ingotIncomePerMin, ingotConsumedPerMin,
                StageUtilisation(slotRefs, refineCaps, ownedNodes),
                StageUtilisation(slotRefs, manufCaps, ownedNodes),
                MeasureGasStage(st, ownedNodes, ctx, tuning));
            if (ledger != null) DecideRegionThroughputInvestments(tuning, st, ctx, ledger);
        }

        private void PostThroughputInvestment(FactionTuning tuning, FactionState st, NodeCapabilityKind cap,
            IProductionExecutionContext ctx, double urgency, string reason)
        {
            PostThroughputInvestment(tuning, st, cap, ctx, urgency, reason, NodeRegion.None);
        }

        /// <summary>The same, scoped to <paramref name="region"/> (<see cref="NodeRegion.Scope"/>)
        /// unless it is <see cref="NodeRegion.None"/>, which posts the unscoped request.</summary>
        private void PostThroughputInvestment(FactionTuning tuning, FactionState st, NodeCapabilityKind cap,
            IProductionExecutionContext ctx, double urgency, string reason, NodeRegion region)
        {
            // A Magnate-tier player faction's components are never silently reserved toward a
            // capability it didn't opt into (see FactionLifecycle.CanPostInvestmentFor).
            if (!FactionLifecycle.CanPostInvestmentFor(st, cap)) return;
            string factionTag = st.Tag;

            bool isNew = ctx.PostInvestment(region.Scope(new InvestmentRequest
            {
                FactionTag = factionTag,
                Cap        = cap,
                Urgency    = urgency,
                ExpiresUtc = ctx.UtcNow.Add(InvestmentTtlFor(tuning)),
            }));
            if (isNew)
                ctx.AddLog(factionTag + " THROUGHPUT_INVEST " + cap
                        + " urgency=" + urgency.ToString("F2", CultureInfo.InvariantCulture)
                        + " (" + reason + ")");
        }

        // ---- catalog demands ----------------------------------------------------------------

        /// <summary>
        /// Posts standing <c>Item:</c> demands for every item in the faction type's
        /// <c>OffersList</c> that our production chain can actually make. These are background
        /// signals that keep the production pull-gate open even when no operational demand exists,
        /// ensuring each faction continuously produces what its type is supposed to offer.
        ///
        /// <para>Items filtered out: those absent from <see cref="_capRecipeOutputs"/> (no recipe
        /// in <c>capability_recipes.json</c> produces them ÔÇö consumables, weapons, etc.). Posting
        /// unresolvable demands would create permanent noise; those become backlog items instead.</para>
        /// </summary>
        private void PostCatalogDemands(string factionTag, IProductionExecutionContext ctx, FactionTuning tuning,
                                        DateTime now, Dictionary<string, bool> catalogDemandSuppressed)
        {
            DateTime expires;
            IReadOnlyList<string> offerKeys = PostCatalogDemandsScalar(factionTag, ctx, tuning, now,
                catalogDemandSuppressed, out expires);
            if (!CatalogNodePhaseHasWork(offerKeys, tuning)) return;

            foreach (SpatialNode node in ctx.OwnedNodes(factionTag))
                PostCatalogNode(factionTag, node, offerKeys, ctx, tuning, expires);
        }

        /// <summary>Whether the per-node catalog phase has anything to post: the faction type's
        /// offer keys when it has any, gas whenever surplus-intent is on and a gas recipe resolved.
        /// Shared by <see cref="PostCatalogDemands"/> and the resumable cycle.</summary>
        private bool CatalogNodePhaseHasWork(IReadOnlyList<string> offerKeys, FactionTuning tuning)
        {
            return offerKeys != null || GasSurplusEnabled(tuning);
        }

        private bool GasSurplusEnabled(FactionTuning tuning)
        {
            return tuning.ProductionSurplusIntentEnabled && _gasRecipeOutputs.Count > 0;
        }

        /// <summary>One node's catalog work: <see cref="PostCatalogSurplusForNode"/> when the faction
        /// has offer keys, then <see cref="PostGasSurplusForNode"/>, which does not need them.</summary>
        private void PostCatalogNode(string factionTag, SpatialNode node, IReadOnlyList<string> offerKeys,
            IProductionExecutionContext ctx, FactionTuning tuning, DateTime expires)
        {
            if (offerKeys != null) PostCatalogSurplusForNode(factionTag, node, offerKeys, ctx, tuning, expires);
            if (GasSurplusEnabled(tuning)) PostGasSurplusForNode(factionTag, node, ctx, tuning, expires);
        }

        /// <summary>The faction-scalar half of <see cref="PostCatalogDemands"/> — standing catalog
        /// demand + hysteresis, bounded by the faction type's offer list, independent of node count.
        /// Returns the resolved offer keys for the caller to feed into
        /// <see cref="PostCatalogSurplusForNode"/> per node, or null when surplus-intent is disabled
        /// or this faction has no resolvable catalogue. Null skips only the offer half of
        /// <see cref="PostCatalogNode"/>; gas still posts.</summary>
        private IReadOnlyList<string> PostCatalogDemandsScalar(string factionTag, IProductionExecutionContext ctx,
            FactionTuning tuning, DateTime now, Dictionary<string, bool> catalogDemandSuppressed, out DateTime expires)
        {
            expires = now.Add(CatalogDemandTtlFor(tuning));
            if (_factionTypeCatalogue == null) return null;

            string factionType;
            if (!_factionTypeCatalogue.TryGetFactionType(factionTag, out factionType)) return null;

            IReadOnlyList<string> offerKeys = _factionTypeCatalogue.OfferInvKeys(factionType);
            if (offerKeys == null || offerKeys.Count == 0) return null;

            foreach (string invKey in offerKeys)
            {
                _lastTelemetry.CatalogOffersScanned++;
                if (!_capRecipeOutputs.Contains(invKey)) continue;

                ItemKind kind;
                ProcessedGoodCategory category = ResolveCategory(ctx, invKey, out kind);
                long cap = tuning.StoreListingCapForItemKind(kind, category);
                if (cap <= 0) continue;

                long stock = ctx.GetFactionInventoryByKey(factionTag, invKey);

                // Hysteresis: once a weapon/ammo/consumable's stock reaches its (comparatively small)
                // cap, go quiet and stay quiet through small drawdowns (e.g. a central-bank purchase)
                // until stock drops below cap × (1 - hysteresis). Bulk/Component items keep their
                // original floor-forever behavior — their cap is sized for a whole faction's industrial
                // base, not a single item, so the standing minimum floor there is intentional.
                if (category != ProcessedGoodCategory.Component)
                {
                    string suppressKey = factionTag + ":" + invKey;
                    bool suppressed;
                    catalogDemandSuppressed.TryGetValue(suppressKey, out suppressed);
                    if (suppressed)
                    {
                        if (stock < cap * (1.0 - tuning.CatalogDemandHysteresis))
                            catalogDemandSuppressed[suppressKey] = false;
                        else
                            continue;
                    }
                    else if (stock >= cap)
                    {
                        catalogDemandSuppressed[suppressKey] = true;
                        continue;
                    }
                }

                // demand = ceil(MissingFraction × (cap - stock) + MinFraction × cap)
                // At 0 stock and 1 M cap: ceil(0.01×1M + 0.001×1M) = 11 000.
                // Approaches MinFraction × cap (e.g. 1 000) as stock nears cap.
                long deficit = cap > stock ? cap - stock : 0;
                long demandQty = (long)Math.Ceiling(
                    tuning.CatalogDemandMissingFraction * deficit +
                    tuning.CatalogDemandMinFraction * cap);
                if (demandQty <= 0) continue;

                bool isNew = PostDemand(ctx, new DemandEntry
                {
                    FactionTag = factionTag,
                    Kind       = "Item:" + invKey,
                    ExpiresUtc = expires,
                    Quantity   = demandQty,
                    Class      = DemandClass.Catalog,
                });
                if (isNew)
                    ctx.AddLog(factionTag + " CATALOG_DEMAND " + invKey
                            + " type=" + factionType + " stock=" + stock
                            + " cap=" + cap + " qty=" + demandQty);
            }

            if (!tuning.ProductionSurplusIntentEnabled) return null;

            return offerKeys;
        }

        /// <summary>One node's share of the surplus-intent half of <see cref="PostCatalogDemands"/> —
        /// the bounded unit the resumable cycle's PostPassesNodes phase checkpoints on. Every node is
        /// independent (no cross-node accumulator); <paramref name="offerKeys"/> and
        /// <paramref name="expires"/> come from <see cref="PostCatalogDemandsScalar"/>'s single
        /// faction-scalar resolution, computed once per cycle rather than once per node.</summary>
        private void PostCatalogSurplusForNode(string factionTag, SpatialNode node,
            IReadOnlyList<string> offerKeys, IProductionExecutionContext ctx, FactionTuning tuning, DateTime expires)
        {
            foreach (string invKey in offerKeys)
            {
                _lastTelemetry.CatalogOffersScanned++;
                if (string.IsNullOrEmpty(invKey)) continue;
                if (!_capRecipeOutputs.Contains(invKey)) continue;
                ItemKind pgKind;
                if (!ctx.TryGetInventoryItemKind(invKey, out pgKind) || pgKind != ItemKind.ProcessedGood) continue;
                if (ctx.Registry == null || ctx.Registry.CategoryFor(StripComponentPrefix(invKey)) != ProcessedGoodCategory.Component)
                    continue; // weapons/ammo/consumables are sized by the catalog-demand cap+hysteresis above, not this surplus-intent mechanism

                long reference;
                if (!_priceOracle.TryGetReferenceStock(node.Id, invKey, out reference)) continue;
                if (reference <= 0) continue;

                long target = (long)Math.Ceiling(reference * tuning.ProductionSurplusTargetMultiple);
                if (target <= 0) continue;

                long stock = ctx.GetNodeInventory(node.Id, invKey);
                if (stock >= target) continue;

                long demandQty = target - stock;
                if (demandQty <= 0) continue;

                bool isNew = PostDemand(ctx, new DemandEntry
                {
                    FactionTag = factionTag,
                    Kind = "Item:" + invKey,
                    NodeId = node.Id,
                    ExpiresUtc = expires,
                    Quantity = demandQty,
                    Class = DemandClass.Catalog,
                });
                if (isNew)
                    ctx.AddLog(factionTag + " PG_SURPLUS_DEMAND node=" + node.Id
                            + " item=" + invKey
                            + " stock=" + stock + " target=" + target + " qty=" + demandQty);
            }
        }

        /// <summary>Gas runs the same shortfall rule as <see cref="PostCatalogSurplusForNode"/>
        /// against its own key set rather than against the faction type's offer keys, because gas
        /// can never appear there: they come from the SE faction type's OffersList, and
        /// FactionTypes.sbc names physical items only. Nor does it wait on a faction type at all —
        /// a player faction has none, and its FuelDepot would otherwise never see a demand for what
        /// it makes. Without this the one kind whose supply chain has no other consumer would never
        /// post a demand, so no FuelDepot would ever be invested in or run.</summary>
        private void PostGasSurplusForNode(string factionTag, SpatialNode node,
            IProductionExecutionContext ctx, FactionTuning tuning, DateTime expires)
        {
            foreach (string invKey in _gasRecipeOutputs)
            {
                _lastTelemetry.CatalogOffersScanned++;
                PostSurplusIntentDemand(factionTag, node, invKey, ctx, tuning, expires, "GAS_SURPLUS_DEMAND");
            }
        }

        /// <summary>The reference-stock shortfall rule shared by the two loops above: a node wants
        /// its reference times <see cref="FactionTuning.ProductionSurplusTargetMultiple"/>, and asks
        /// for the difference. Extracted so gas cannot drift from components.</summary>
        private void PostSurplusIntentDemand(string factionTag, SpatialNode node, string invKey,
            IProductionExecutionContext ctx, FactionTuning tuning, DateTime expires, string logVerb)
        {
            long reference;
            if (!_priceOracle.TryGetReferenceStock(node.Id, invKey, out reference)) return;
            if (reference <= 0) return;

            long target = (long)Math.Ceiling(reference * tuning.ProductionSurplusTargetMultiple);
            if (target <= 0) return;

            long stock = ctx.GetNodeInventory(node.Id, invKey);
            if (stock >= target) return;

            // A fuel key's row is the one the reserve posted earlier this cycle. Overwriting it
            // with a net top-up must not take a departure node's row below its buffer.
            long demandQty = FuelReserve.ProtectBuffer(node, invKey, stock, target - stock, ctx.UtcNow, tuning);
            if (demandQty <= 0) return;

            bool isNew = PostDemand(ctx, new DemandEntry
            {
                FactionTag = factionTag,
                Kind = "Item:" + invKey,
                NodeId = node.Id,
                ExpiresUtc = expires,
                Quantity = demandQty,
                Class = DemandClass.Catalog,
            });
            if (isNew)
                ctx.AddLog(factionTag + " " + logVerb + " node=" + node.Id
                        + " item=" + invKey
                        + " stock=" + stock + " target=" + target + " qty=" + demandQty);
        }

        // ---- throughput snapshot ------------------------------------------------------------

        /// <summary>Returns the theoretical throughput (items/min) for every active machine slot
        /// belonging to <paramref name="factionTag"/>. Each entry represents one recipe running on
        /// one or more slots at a node. Idle/unassigned slots are excluded.</summary>
        public List<ThroughputEntry> SnapshotThroughput(string factionTag)
        {
            string prefix = factionTag + ":";
            // Group: key = "nodeId:capIdx:recipeId" ÔåÆ accumulated slot state
            var groups = new Dictionary<string, ThroughputAccum>();

            foreach (var kv in _slots)
            {
                if (!kv.Key.StartsWith(prefix)) continue;
                MachineSlot slot = kv.Value;
                if (slot.ActiveRecipeId == null) continue;

                // Key format: "{tag}:{nodeId}:{capIdx}:{slotIdx}"
                // Parse from the right so nodeId (which never has ':') is whatever remains.
                string rest     = kv.Key.Substring(prefix.Length);
                int lastColon   = rest.LastIndexOf(':');
                if (lastColon < 0) continue;
                string withoutSlot = rest.Substring(0, lastColon);
                int secondLast = withoutSlot.LastIndexOf(':');
                if (secondLast < 0) continue;
                string nodeId  = withoutSlot.Substring(0, secondLast);
                int    capIdx;
                if (!int.TryParse(withoutSlot.Substring(secondLast + 1), out capIdx)) continue;
                if (capIdx < 0 || capIdx >= _models.Length) continue;

                MachineModel model = _models[capIdx];
                if (model.Speed <= 0) continue;

                NodeCapabilityKind cap = (NodeCapabilityKind)capIdx;
                List<ConversionRecipe> recipes;
                if (!_capRecipes.TryGetValue(cap, out recipes)) continue;
                ConversionRecipe recipe = FindRecipe(recipes, slot.ActiveRecipeId);
                if (recipe == null || recipe.TimeSecs <= 0f) continue;

                string groupKey = nodeId + ":" + capIdx + ":" + slot.ActiveRecipeId;
                ThroughputAccum g;
                if (!groups.TryGetValue(groupKey, out g))
                {
                    g = new ThroughputAccum
                    {
                        NodeId             = nodeId,
                        Cap                = cap,
                        Recipe             = recipe,
                        BatchesPerSecPerSlot = model.Speed / recipe.TimeSecs,
                        MachineSpeed = model.Speed,
                        OutputScale = model.OutputScale,
                    };
                    groups[groupKey] = g;
                }
                g.SlotCount++;
            }

            Dictionary<string, double> observedRates = BuildObservedRates(factionTag);
            var result = new List<ThroughputEntry>(groups.Count);
            foreach (ThroughputAccum g in groups.Values)
            {
                ConversionRecipe r = g.Recipe;
                double batchesPerMin = g.BatchesPerSecPerSlot * g.SlotCount * 60.0;
                string inputId  = (r.Inputs  != null && r.Inputs.Length  > 0) ? InvKey(r.Inputs[0])  : "";
                string outputId = (r.Outputs != null && r.Outputs.Length > 0) ? InvKey(r.Outputs[0]) : "";
                double outputAmt = (r.Outputs != null && r.Outputs.Length > 0) ? r.Outputs[0].Amount : 0.0;
                result.Add(new ThroughputEntry
                {
                    FactionTag      = factionTag,
                    NodeId          = g.NodeId,
                    Cap             = g.Cap,
                    PrimaryInputId  = inputId,
                    PrimaryOutputId = outputId,
                    NominalItemsPerMinute = batchesPerMin * outputAmt * g.OutputScale,
                    ObservedItemsPerMinute = LookupObservedRate(observedRates, g.NodeId, r.BlueprintId, outputId),
                    EffectiveMachineSpeed = g.MachineSpeed,
                    OutputScale     = g.OutputScale,
                    SlotCount       = g.SlotCount,
                });
            }
            return result;
        }

        private sealed class ThroughputAccum
        {
            public string             NodeId;
            public NodeCapabilityKind Cap;
            public ConversionRecipe   Recipe;
            public double             BatchesPerSecPerSlot;
            public double             MachineSpeed;
            public double             OutputScale;
            public int                SlotCount;
        }

        // ---- production history --------------------------------------------------------------

        /// <summary>One real, already-happened span of consumption or production — as opposed to
        /// <see cref="ThroughputEntry"/>, which is a theoretical rate projected from the current
        /// tick's slot assignment. Consecutive activity for the same
        /// (faction, node, recipe, item, direction) within <see cref="HistorySpanGapSeconds"/>
        /// extends <see cref="EndUtc"/>/<see cref="Quantity"/> in place instead of adding a new
        /// row, so a recipe that holds a slot for minutes shows as one growing span.</summary>
        public sealed class ProductionHistoryEntry
        {
            public DateTime StartUtc;
            public DateTime EndUtc;
            public string   FactionTag;
            public string   NodeId;
            public string   RecipeId;
            public string   InvKey;
            public bool     IsOutput;
            public long     Quantity;
        }

        /// <summary>Observed output rate per minute for every (node, recipe, item) this faction
        /// produced within the history retention window, built in one pass over the spans. A span is
        /// a coalesced range, so one straddling the window edge contributes only the share of its
        /// quantity that falls inside. This is the number to trust when it disagrees with the nominal
        /// rate: the gap is input starvation, not a reporting error.
        ///
        /// <para>Built once per snapshot rather than scanned per entry — the flush phase snapshots
        /// every active recipe at every node, so a per-entry scan is history-size times entry-count
        /// work on the host's flush path.</para></summary>
        private Dictionary<string, double> BuildObservedRates(string factionTag)
        {
            var rates = new Dictionary<string, double>();
            if (_history.Count == 0) return rates;
            DateTime now = _lastTickUtc;
            if (now == default(DateTime)) return rates;
            DateTime windowStart = now.AddSeconds(-HistoryRetentionSeconds);
            double perMinute = 60.0 / HistoryRetentionSeconds;

            foreach (ProductionHistoryEntry e in _history)
            {
                if (!e.IsOutput || e.FactionTag != factionTag) continue;
                if (e.EndUtc < windowStart || e.StartUtc > now) continue;

                double share;
                double spanSeconds = (e.EndUtc - e.StartUtc).TotalSeconds;
                if (spanSeconds <= 0) share = e.Quantity;
                else
                {
                    DateTime from = e.StartUtc > windowStart ? e.StartUtc : windowStart;
                    DateTime to   = e.EndUtc   < now         ? e.EndUtc   : now;
                    double overlap = (to - from).TotalSeconds;
                    if (overlap <= 0) continue;
                    share = e.Quantity * (overlap / spanSeconds);
                }

                string key = ObservedRateKey(e.NodeId, e.RecipeId, e.InvKey);
                double running;
                rates.TryGetValue(key, out running);
                rates[key] = running + share * perMinute;
            }
            return rates;
        }

        private static string ObservedRateKey(string nodeId, string recipeId, string invKey)
        {
            return nodeId + "|" + recipeId + "|" + invKey;
        }

        private static double LookupObservedRate(Dictionary<string, double> rates,
            string nodeId, string recipeId, string invKey)
        {
            if (rates == null || string.IsNullOrEmpty(invKey)) return 0.0;
            double rate;
            return rates.TryGetValue(ObservedRateKey(nodeId, recipeId, invKey), out rate) ? rate : 0.0;
        }

        /// <summary>Records one real consumption/production event into the rolling history. Called
        /// from <see cref="TickCapabilityBank"/> at the same two points that already mutate node
        /// inventory for this batch, so history can never disagree with what was actually applied.</summary>
        private void RecordProductionHistory(string factionTag, string nodeId, string recipeId,
            string invKey, long qty, bool isOutput, DateTime now)
        {
            if (qty <= 0) return;
            string key = factionTag + "|" + nodeId + "|" + recipeId + "|" + invKey + "|" + (isOutput ? "O" : "I");

            ProductionHistoryEntry span;
            if (_openHistorySpans.TryGetValue(key, out span)
                && (now - span.EndUtc).TotalSeconds <= HistorySpanGapSeconds)
            {
                span.EndUtc    = now;
                span.Quantity += qty;
            }
            else
            {
                span = new ProductionHistoryEntry
                {
                    StartUtc   = now,
                    EndUtc     = now,
                    FactionTag = factionTag,
                    NodeId     = nodeId,
                    RecipeId   = recipeId,
                    InvKey     = invKey,
                    IsOutput   = isOutput,
                    Quantity   = qty,
                };
                _history.Add(span);
                _openHistorySpans[key] = span;
            }
        }

        /// <summary>Full-scan rebuild dropping spans whose last activity fell outside the retention
        /// window. Called once per faction per tick from <see cref="PrepareFaction"/> — not just
        /// opportunistically from <see cref="RecordProductionHistory"/> — so a span still gets swept
        /// even if its recipe goes idle and stops recording entirely (otherwise it would look "in
        /// window" forever). O(history size), which stays small (bounded by real activity within the
        /// retention window), so running it every tick is cheap. A span's position in
        /// <see cref="_history"/> reflects when it was *first* created, not when it was last
        /// extended, so an index-based front-truncation would incorrectly stop at a long-lived span
        /// sitting near the front while newer-but-shorter spans further back had already gone stale —
        /// hence the full scan rather than a truncation.</summary>
        private void TrimHistory(DateTime now)
        {
            if (_history.Count == 0) return;
            DateTime cutoff = now.AddSeconds(-HistoryRetentionSeconds);
            var kept = new List<ProductionHistoryEntry>(_history.Count);
            foreach (ProductionHistoryEntry e in _history)
            {
                if (e.EndUtc >= cutoff) { kept.Add(e); continue; }
                string key = e.FactionTag + "|" + e.NodeId + "|" + e.RecipeId + "|" + e.InvKey + "|" + (e.IsOutput ? "O" : "I");
                ProductionHistoryEntry openSpan;
                if (_openHistorySpans.TryGetValue(key, out openSpan) && ReferenceEquals(openSpan, e))
                    _openHistorySpans.Remove(key);
            }
            _history.Clear();
            _history.AddRange(kept);
        }

        /// <summary>Returns the rolling production-history window for one faction, oldest first.
        /// Callers should treat the returned list as a snapshot — later recording may mutate the
        /// same <see cref="ProductionHistoryEntry"/> objects in place (an in-progress span's
        /// <see cref="ProductionHistoryEntry.EndUtc"/>/<see cref="ProductionHistoryEntry.Quantity"/>
        /// keep growing), so serialize promptly rather than holding onto it.</summary>
        public List<ProductionHistoryEntry> SnapshotProductionHistory(string factionTag)
        {
            var result = new List<ProductionHistoryEntry>();
            foreach (ProductionHistoryEntry e in _history)
                if (e.FactionTag == factionTag) result.Add(e);
            return result;
        }

        /// <summary>One node's slice of <see cref="SnapshotProductionHistory"/>, most recent activity
        /// first — the order the station window and the dashboard both read it in, so a row cap
        /// drops the oldest spans rather than the ones still running. Same aliasing caveat: the
        /// entries are live objects, so serialize them before the next tick.</summary>
        public List<ProductionHistoryEntry> SnapshotNodeProductionHistory(string factionTag, string nodeId)
        {
            var result = new List<ProductionHistoryEntry>();
            foreach (ProductionHistoryEntry e in _history)
                if (e.FactionTag == factionTag && e.NodeId == nodeId) result.Add(e);
            // Stable on ties: List.Sort is not, and two spans of one batch (its input and its output)
            // end on the same tick, so the tie-break keeps a refresh from swapping them.
            result.Sort((a, b) =>
            {
                int byEnd = b.EndUtc.CompareTo(a.EndUtc);
                if (byEnd != 0) return byEnd;
                int byRecipe = string.CompareOrdinal(a.RecipeId, b.RecipeId);
                if (byRecipe != 0) return byRecipe;
                if (a.IsOutput != b.IsOutput) return a.IsOutput ? -1 : 1;
                return string.CompareOrdinal(a.InvKey, b.InvKey);
            });
            return result;
        }

        // ---- helpers ------------------------------------------------------------------------

        private static long EstimateYield(ConversionRecipe recipe, long inputQty)
        {
            if (recipe.Inputs == null || recipe.Inputs.Length == 0 || recipe.Inputs[0].Amount <= 0)
                return inputQty;
            if (recipe.Outputs == null || recipe.Outputs.Length == 0) return 0;
            long batches = (long)(inputQty / recipe.Inputs[0].Amount);
            long total   = 0;
            foreach (RecipeItem outp in recipe.Outputs)
                if (outp.Amount > 0) total += (long)(batches * outp.Amount);
            return total > 0 ? total : inputQty;
        }

        private static long MaxRunnableBatches(SpatialNode node, ConversionRecipe recipe,
            MachineModel model, Dictionary<string, double> remainders, string scope,
            IProductionExecutionContext ctx)
        {
            return MaxRunnableBatches(node, recipe, model, remainders, scope, ctx, null);
        }

        /// <summary>As above, but with <paramref name="inputFloor"/> subtracted from what this recipe
        /// is allowed to see of each input key — the capability-install input reserve
        /// (<see cref="ComputeInstallInputReserve"/>). Null for the recipe the reserve exists for, and
        /// for every recipe at every bank whenever nothing is being installed.</summary>
        private static long MaxRunnableBatches(SpatialNode node, ConversionRecipe recipe,
            MachineModel model, Dictionary<string, double> remainders, string scope,
            IProductionExecutionContext ctx, Dictionary<string, long> inputFloor)
        {
            if (node == null || recipe == null || recipe.Inputs == null || recipe.Inputs.Length == 0) return 0;

            long max = long.MaxValue;
            bool anyInput = false;
            foreach (RecipeItem inp in recipe.Inputs)
            {
                if (inp.Amount <= 0) continue;
                anyInput = true;
                string key  = InvKey(inp);
                long   have = ctx.GetNodeInventory(node.Id, key);
                if (have < 0) have = 0;
                if (inputFloor != null)
                {
                    long withheld;
                    if (inputFloor.TryGetValue(key, out withheld) && withheld > 0)
                    {
                        have -= withheld;
                        if (have < 0) have = 0;
                    }
                }
                if (have <= 0) return 0;
                // Exact, not ceil(amount): a 0.5-per-batch input supports twice as many batches as
                // units on hand, and the carry already owed is charged before this batch's own
                // share, so floor(b*a + carry) can never exceed what is there.
                double perBatch = MachinePhysics.InputAmount(inp.Amount, model, recipe.IgnoresEfficiencyMultiplier);
                if (perBatch <= 0) continue;
                double avail = have - CarriedRemainder(remainders, scope, recipe.BlueprintId, key);
                long canRun = avail > 0 ? (long)Math.Floor(Math.Min(avail / perBatch, MaxBatchesCeiling)) : 0;
                if (canRun < max) max = canRun;
            }
            if (!anyInput) return 0;
            return max == long.MaxValue ? 0 : max;
        }

        /// <summary>What <see cref="ScarcityPriority"/> saw, kept alongside the value it schedules on.
        /// <see cref="Priority"/> is unchanged from before this struct existed — floored at 1.0,
        /// because a ranking comparison wants a neutral rather than a discount. <see cref="BestRaw"/>
        /// is that same figure BEFORE the floor, and <see cref="BestKey"/> names the output it came
        /// from.
        ///
        /// <para><b>The unfloored figure exists because the pull gate rejects on
        /// <c>Priority &lt;= 1.0</c> — exactly the region the floor collapses.</b> Every recipe that
        /// gate turns away therefore reads <c>Priority == 1.0</c> by construction, whether
        /// <see cref="IPriceOracle"/> answered <see cref="FactionTuning.ScarcityMin"/> (0.50, a
        /// measured glut) or 1.0 (no reference stock at all, so no opinion). Reporting
        /// <see cref="Priority"/> for a rejected recipe measures the floor and not the economy: that
        /// is why all 107 rejections logged for FAC-231 Finding 2 read exactly <c>1.00</c>, a
        /// tautology of this clamp rather than evidence about the oracle.</para></summary>
        internal struct ScarcityReading
        {
            /// <summary>Scheduling value: <c>max(1.0, BestRaw)</c>. What ranks the bank.</summary>
            public double Priority;
            /// <summary>Highest multiplier any demanded output earned, unfloored. Left at 1.0 when
            /// the recipe has no demanded output at all.</summary>
            public double BestRaw;
            /// <summary>Inventory key <see cref="BestRaw"/> came from; null when none.</summary>
            public string BestKey;
            /// <summary>True when at least one output is on the demand bus.</summary>
            public bool HasDemandedOutput;
        }

        private ScarcityReading ScarcityPriority(SpatialNode node, ConversionRecipe recipe,
                                                 HashSet<string> demandedKeys)
        {
            var reading = new ScarcityReading { Priority = 1.0, BestRaw = 1.0, BestKey = null };
            if (node == null || recipe == null || recipe.Outputs == null) return reading;

            foreach (RecipeItem output in recipe.Outputs)
            {
                if (output.Amount <= 0) continue;
                string key = InvKey(output);
                if (!demandedKeys.Contains(key)) continue;

                // A live runtime always supplies PriceOracle. Preserve legacy scheduling for
                // isolated callers that deliberately omit an oracle (including existing tests).
                double scarcity = ReferenceEquals(_priceOracle, NullPriceOracle.Instance)
                    ? 2.0
                    : _priceOracle.ScarcityMultiplier(node.Id, key);

                // First demanded output seeds BestRaw outright: it may be below 1.0, and seeding
                // from the 1.0 initialiser would re-introduce the very floor this field exists to
                // see past.
                if (!reading.HasDemandedOutput || scarcity > reading.BestRaw)
                {
                    reading.BestRaw = scarcity;
                    reading.BestKey = key;
                }
                reading.HasDemandedOutput = true;
                if (scarcity > reading.Priority) reading.Priority = scarcity;
            }
            return reading;
        }

        /// <summary>Splits a scarcity-gate rejection into the two states <see cref="ScarcityReading.Priority"/>
        /// cannot tell apart. A reading below 1.0 is a measured glut and settles itself. A reading of
        /// exactly 1.0 is either a node sitting precisely at its reference or an item the oracle holds
        /// no reference for at all, and only <see cref="IPriceOracle.TryGetReferenceStock"/> separates
        /// those two.
        ///
        /// <para>Called only from behind <see cref="DebugTraces.SlotGate"/>, and only for the
        /// ambiguous <c>BestRaw == 1.0</c> subset: the lookup is a cache read rather than a
        /// recomputation, but this sits on the per-recipe bank loop and the trace must not change what
        /// the untraced server costs.</para></summary>
        private bool IsMeasuredOversupply(string nodeId, ScarcityReading reading)
        {
            if (reading.BestRaw < 1.0) return true;
            if (reading.BestKey == null) return false;
            long reference;
            return _priceOracle.TryGetReferenceStock(nodeId, reading.BestKey, out reference) && reference > 0;
        }

        internal static string InvKey(RecipeItem item)
        {
            if (item.TypeId == "MyObjectBuilder_Ore")       return "Ore:"   + item.SubTypeId;
            if (item.TypeId == "MyObjectBuilder_Ingot")     return "Ingot:" + item.SubTypeId;
            if (item.TypeId == "MyObjectBuilder_Component") return "Comp:"  + item.SubTypeId;
            // Gas is not a physical item in SE -- it is litres of a GasProperties definition, which
            // is what an O2/H2 generator's ProducedGases names. See SeRecipeRegistry's gas pass.
            if (item.TypeId == "MyObjectBuilder_GasProperties") return "Gas:" + item.SubTypeId;
            if (item.TypeId == "MyObjectBuilder_SeedItem") return InventoryKeyMapper.SeedPrefix + item.SubTypeId;
            return item.SubTypeId;
        }

        /// <summary>Full item BOM for a prefab's first matching "Prefab" recipe — inventory key → total
        /// units required. Empty dictionary and <paramref name="hasRecipe"/>=false when no recipe is
        /// registered for <paramref name="prefabId"/>. Single-sourced here so both
        /// <see cref="ShipyardManager.SelectPrefabBom"/> (build-time BOM) and
        /// <see cref="WorldModel.ClearOrbitStranding"/> (refund-time BOM on dismantle) call this
        /// instead of independently re-deriving the <c>GetRecipesFor("Prefab", ...)</c> + first-recipe
        /// -only loop.</summary>
        internal static Dictionary<string, long> GetPrefabBom(IRecipeRegistry recipes, string prefabId, out bool hasRecipe)
        {
            var bom = new Dictionary<string, long>();
            hasRecipe = false;
            if (recipes == null || prefabId == null) return bom;
            foreach (ConversionRecipe recipe in recipes.GetRecipesFor("Prefab", prefabId))
            {
                hasRecipe = true;
                if (recipe.Inputs != null)
                    foreach (RecipeItem inp in recipe.Inputs)
                    {
                        if (string.IsNullOrEmpty(inp.SubTypeId)) continue;
                        string key = InvKey(inp);
                        long cur;
                        bom.TryGetValue(key, out cur);
                        bom[key] = cur + (long)inp.Amount;
                    }
                break; // first recipe only
            }
            return bom;
        }

        /// <summary>One bank's idle-capacity attribution. Every count is a recipe count except
        /// <paramref name="awarded"/> and <paramref name="machines"/>, which are machines -- the two
        /// rank differently and reporting either alone picks the wrong fix (2026-08-28: the horizon
        /// gate hit the most recipes and stranded the least ore).</summary>
        private static string SlotGateLine(string factionTag, string nodeId, NodeCapabilityKind cap,
            int machines, int ranked, int awarded, int noDemand, int oversupplied, int glut,
            int noOpinion, int noInput, int zeroSlots, string worstOversupplied,
            double worstOversuppliedScarcity)
        {
            string line = factionTag + " SLOT_GATE node=" + nodeId + " cap=" + cap
                + " machines=" + machines.ToString(CultureInfo.InvariantCulture)
                + " awarded=" + awarded.ToString(CultureInfo.InvariantCulture)
                + " ranked=" + ranked.ToString(CultureInfo.InvariantCulture)
                + " | nodemand=" + noDemand.ToString(CultureInfo.InvariantCulture)
                + " oversupplied=" + oversupplied.ToString(CultureInfo.InvariantCulture)
                + " glut=" + glut.ToString(CultureInfo.InvariantCulture)
                + " noopinion=" + noOpinion.ToString(CultureInfo.InvariantCulture)
                + " noinput=" + noInput.ToString(CultureInfo.InvariantCulture)
                + " zeroslots=" + zeroSlots.ToString(CultureInfo.InvariantCulture);
            // Renamed from firstOversupplied: it now genuinely is the lowest reading in the bank,
            // and the value after @ is the UNFLOORED multiplier -- the old token could only ever
            // print 1.00 (see ScarcityReading).
            if (worstOversupplied != null)
                line += " worstOversupplied=" + worstOversupplied
                     + "@" + worstOversuppliedScarcity.ToString("F2", CultureInfo.InvariantCulture);
            return line;
        }

        private static ItemKind ItemKindFromInvKey(string key)
        {
            if (key.StartsWith("Ore:"))   return ItemKind.Ore;
            if (key.StartsWith("Ingot:")) return ItemKind.Ingot;
            if (key.StartsWith("Comp:"))  return ItemKind.ProcessedGood;
            if (key.StartsWith("Gas:"))   return key == "Gas:Oxygen" ? ItemKind.Oxygen : ItemKind.Hydrogen;
            if (InventoryKeyMapper.IsSeedKey(key)) return ItemKind.ProcessedGood;
            return ItemKind.Hydrogen;
        }

        // ProcessedGood inventory keys are either "Comp:<subtype>" or the bare subtype (weapons,
        // ammo, consumables) — never Ore:/Ingot:-prefixed. IItemRegistry.CategoryFor expects the
        // bare subtype form.
        private static string StripComponentPrefix(string invKey)
        {
            return invKey.StartsWith("Comp:") ? invKey.Substring(5) : invKey;
        }

        // Unclassified subtypes (e.g. Datapad, or a genuine component caught mid-scan at startup)
        // default to the conservative small cap rather than the bulk cap, so an item we know nothing
        // about can't silently claim a million-unit standing demand.
        private static ProcessedGoodCategory ResolveCategory(IProductionExecutionContext ctx, string invKey, out ItemKind kind)
        {
            if (ctx.TryGetInventoryItemKind(invKey, out kind))
            {
                return kind == ItemKind.ProcessedGood && ctx.Registry != null
                    ? ctx.Registry.CategoryFor(StripComponentPrefix(invKey))
                    : ProcessedGoodCategory.Component;
            }
            kind = ItemKind.ProcessedGood;
            return ProcessedGoodCategory.Consumable;
        }

        private static long ResolveStoreCap(IProductionExecutionContext ctx, FactionTuning tuning, string invKey)
        {
            ItemKind kind;
            ProcessedGoodCategory category = ResolveCategory(ctx, invKey, out kind);
            return tuning.StoreListingCapForItemKind(kind, category);
        }

        private static Dictionary<ItemKind, long> ReserveInstallStock(FactionState state, string factionTag,
            List<SpatialNode> ownedNodes, IProductionExecutionContext ctx, FactionTuning tuning,
            StockClaimFloor stockClaims, bool allowRelease)
        {
            var reservedNow = new Dictionary<ItemKind, long>();
            List<InvestmentRequest> requests = ctx.SnapshotInvestments(factionTag);

            // Keys belonging to any registered install BOM (see FactionTuning.InstallBoms) are
            // tracked by the separate per-key phases below and must never be double-counted by the
            // flat ItemKind phases — otherwise a BOM-reserved SteelPlate would also inflate the
            // legacy ProcessedGood bucket and could be "released" as spurious excess, or worse, an
            // unrelated capability's flat PG cost could still scoop it up.
            var bomKeys = new HashSet<string>();
            foreach (var bom in tuning.InstallBoms.Values)
                foreach (string key in bom.Keys) bomKeys.Add(key);

            // Phase 1: sum what is currently reserved across all owned nodes.
            foreach (SpatialNode node in ownedNodes)
            {
                var snapshot = new List<KeyValuePair<string, long>>(ctx.SnapshotReservedInventory(node.Id));
                foreach (var kv in snapshot)
                {
                    if (kv.Value <= 0 || bomKeys.Contains(kv.Key)) continue;
                    ItemKind kind = ItemKindFromInvKey(kv.Key);
                    long cur;
                    reservedNow.TryGetValue(kind, out cur);
                    reservedNow[kind] = cur + kv.Value;
                }
            }

            // Phase 2: compute the total reservation target from active investment requests.
            var cumulativeTarget = new Dictionary<ItemKind, long>();
            foreach (InvestmentRequest req in requests)
            {
                Dictionary<string, long> bom;
                if (tuning.TryGetInstallBom(req.Cap, out bom)) continue; // handled by the BOM phases below
                foreach (ItemKind kind in s_installCostKinds)
                {
                    long cost = tuning.InstallCostFor(req.Cap, kind);
                    if (cost <= 0) continue;
                    long prior;
                    cumulativeTarget.TryGetValue(kind, out prior);
                    cumulativeTarget[kind] = prior + cost;
                }
            }

            // Phase 3: release excess — stock reserved for caps whose InvestmentRequests have
            // since expired. Return the excess to normal inventory so it becomes routable again.
            foreach (ItemKind kind in allowRelease ? s_installCostKinds : s_noInstallCostKinds)
            {
                long target;
                cumulativeTarget.TryGetValue(kind, out target); // 0 when no active request
                long reserved;
                reservedNow.TryGetValue(kind, out reserved);
                long excess = reserved - target;
                if (excess <= 0) continue;

                long toRelease = excess;
                foreach (SpatialNode node in ownedNodes)
                {
                    if (toRelease <= 0) break;
                    var keys = new List<string>();
                    foreach (var kv in ctx.SnapshotReservedInventory(node.Id)) keys.Add(kv.Key);
                    foreach (string key in keys)
                    {
                        if (toRelease <= 0) break;
                        if (bomKeys.Contains(key) || ItemKindFromInvKey(key) != kind) continue;
                        long release = ctx.MoveReservedToInventory(node.Id, key, toRelease);
                        if (release <= 0) continue;
                        toRelease -= release;
                        ctx.AddLog(factionTag + " INVEST_RELEASE " + key
                                + " qty=" + release + " node=" + node.Id);
                    }
                }
                reservedNow[kind] = reserved - (excess - toRelease);
            }

            // Phase 4: top up — move additional stock from normal inventory into reserved
            // until we reach the cumulative target for each kind. Uses the same per-request
            // running-accumulation as the original so we move exactly the right amount.
            var runningTarget = new Dictionary<ItemKind, long>();
            foreach (InvestmentRequest req in requests)
            {
                Dictionary<string, long> bom;
                if (tuning.TryGetInstallBom(req.Cap, out bom)) continue; // handled by the BOM phases below
                foreach (ItemKind kind in s_installCostKinds)
                {
                    long cost = tuning.InstallCostFor(req.Cap, kind);
                    if (cost <= 0) continue;

                    long prior;
                    runningTarget.TryGetValue(kind, out prior);
                    long target = prior + cost;
                    runningTarget[kind] = target;

                    long already;
                    reservedNow.TryGetValue(kind, out already);
                    long stillNeed = target - already;
                    if (stillNeed <= 0) continue;

                    foreach (SpatialNode node in ownedNodes)
                    {
                        if (stillNeed <= 0) break;
                        var keys = new List<string>();
                        foreach (var kv in ctx.SnapshotNodeInventory(node.Id)) keys.Add(kv.Key);
                        foreach (string key in keys)
                        {
                            if (stillNeed <= 0) break;
                            if (bomKeys.Contains(key) || ItemKindFromInvKey(key) != kind) continue;
                            // Never claim stock an active ship build at this node still needs, unless
                            // this capability has been ranked above that build -- at the default ranks
                            // an in-progress build is an already-committed investment and takes
                            // priority over a capability upgrade the player can always retry later.
                            int installRank = state.InstallRankFor(req.Cap, tuning);
                            long floor = ShipBuildFloor(node, key, ctx.UtcNow, installRank)
                                       + StockFloor(stockClaims, node.Id, key, installRank);
                            long claimable = ctx.GetNodeInventory(node.Id, key) - floor;
                            if (claimable <= 0) continue;
                            long move = ctx.MoveInventoryToReserved(node.Id, key, Math.Min(stillNeed, claimable));
                            if (move <= 0) continue;
                            stillNeed -= move;
                            long nowReserved;
                            reservedNow.TryGetValue(kind, out nowReserved);
                            reservedNow[kind] = nowReserved + move;
                            ctx.AddLog(factionTag + " INVEST_RESERVE " + key
                                    + " qty=" + move + " for=" + req.Cap + " node=" + node.Id);
                        }
                    }
                }
            }

            ReserveInstallBomStock(state, factionTag, ownedNodes, requests, bomKeys, tuning, ctx, stockClaims, allowRelease);

            return reservedNow;
        }

        /// <summary>
        /// BOM counterpart to the flat-<see cref="ItemKind"/> phases above, for capabilities with a
        /// registered real-block install BOM (<see cref="FactionTuning.InstallBoms"/>) — same
        /// tally/target/release/top-up shape, keyed by specific component invKey instead of
        /// <see cref="ItemKind"/>, so only the exact components a real Refinery/Assembler block
        /// needs are ever reserved. A capability with no registered BOM is untouched here; it goes
        /// through the flat-cost phases above instead.
        /// <para>Every currently reserved BOM key is re-checked against the active requests each
        /// tick, including when no BOM-backed request is active at all — a key reserved for a
        /// request that has since expired has no claim on it any more and must go back to routable
        /// inventory. <paramref name="bomKeys"/> (every key of every registered BOM) bounds what
        /// this function owns; flat-cost reservations belong to the caller's phases.</para>
        /// </summary>
        private static void ReserveInstallBomStock(FactionState state, string factionTag, List<SpatialNode> ownedNodes,
            List<InvestmentRequest> requests, HashSet<string> bomKeys, FactionTuning tuning,
            IProductionExecutionContext ctx, StockClaimFloor stockClaims, bool allowRelease)
        {
            // Phase 1: tally what's currently reserved, per key.
            var reservedNow = new Dictionary<string, long>();
            foreach (SpatialNode node in ownedNodes)
            {
                var snapshot = new List<KeyValuePair<string, long>>(ctx.SnapshotReservedInventory(node.Id));
                foreach (var kv in snapshot)
                {
                    if (kv.Value <= 0 || !bomKeys.Contains(kv.Key)) continue;
                    long cur;
                    reservedNow.TryGetValue(kv.Key, out cur);
                    reservedNow[kv.Key] = cur + kv.Value;
                }
            }

            // Phase 2: cumulative per-key target across every active BOM-backed request. Stays
            // empty when none is active, which is exactly when Phase 3 has the most to release.
            var cumulativeTarget = new Dictionary<string, long>();
            foreach (InvestmentRequest req in requests)
            {
                Dictionary<string, long> bom;
                if (!tuning.TryGetInstallBom(req.Cap, out bom)) continue;
                foreach (var kv in bom)
                {
                    long prior;
                    cumulativeTarget.TryGetValue(kv.Key, out prior);
                    cumulativeTarget[kv.Key] = prior + kv.Value;
                }
            }

            // Phase 3: release excess back to routable inventory. Driven by what is currently
            // reserved, not by the target's keys: an expired request drops its keys out of the
            // target entirely, so a target-keyed loop would never look at that stock again.
            var reservedKeys = allowRelease ? new List<string>(reservedNow.Keys) : new List<string>();
            foreach (string key in reservedKeys)
            {
                long target;
                cumulativeTarget.TryGetValue(key, out target);   // 0 when no active request wants it
                long reserved = reservedNow[key];
                long excess = reserved - target;
                if (excess <= 0) continue;

                long toRelease = excess;
                foreach (SpatialNode node in ownedNodes)
                {
                    if (toRelease <= 0) break;
                    long release = ctx.MoveReservedToInventory(node.Id, key, toRelease);
                    if (release <= 0) continue;
                    toRelease -= release;
                    ctx.AddLog(factionTag + " INVEST_RELEASE " + key + " qty=" + release + " node=" + node.Id);
                }
                reservedNow[key] = reserved - (excess - toRelease);
            }

            // Phase 4: top up from routable inventory until each key's cumulative target is met.
            var runningTarget = new Dictionary<string, long>();
            foreach (InvestmentRequest req in requests)
            {
                Dictionary<string, long> bom;
                if (!tuning.TryGetInstallBom(req.Cap, out bom)) continue;
                foreach (var kv in bom)
                {
                    long prior;
                    runningTarget.TryGetValue(kv.Key, out prior);
                    long target = prior + kv.Value;
                    runningTarget[kv.Key] = target;

                    long already;
                    reservedNow.TryGetValue(kv.Key, out already);
                    long stillNeed = target - already;
                    if (stillNeed <= 0) continue;

                    foreach (SpatialNode node in ownedNodes)
                    {
                        if (stillNeed <= 0) break;
                        // Never claim stock an active ship build at this node still needs, unless this
                        // capability has been ranked above that build -- at the default ranks an
                        // in-progress build is an already-committed investment and takes priority over
                        // a capability upgrade the player can always retry later.
                        int installRank = state.InstallRankFor(req.Cap, tuning);
                        long floor = ShipBuildFloor(node, kv.Key, ctx.UtcNow, installRank)
                                   + StockFloor(stockClaims, node.Id, kv.Key, installRank);
                        long claimable = ctx.GetNodeInventory(node.Id, kv.Key) - floor;
                        if (claimable <= 0) continue;
                        long move = ctx.MoveInventoryToReserved(node.Id, kv.Key, Math.Min(stillNeed, claimable));
                        if (move <= 0) continue;
                        stillNeed -= move;
                        long nowReserved;
                        reservedNow.TryGetValue(kv.Key, out nowReserved);
                        reservedNow[kv.Key] = nowReserved + move;
                        ctx.AddLog(factionTag + " INVEST_RESERVE " + kv.Key
                                + " qty=" + move + " for=" + req.Cap + " node=" + node.Id);
                    }
                }
            }
        }
    }
}
