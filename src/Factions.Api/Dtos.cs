// Explicit, because this file is also source-included by Factions.Core.Tests, which
// disables nullable reference types project-wide.
#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Factions.Api
{
    public sealed class CommandRequest
    {
        public string Command { get; set; } = "";
    }

    public sealed class WorldSnapshotDto
    {
        public DateTime UtcNow { get; set; }
        /// <summary>UTC timestamp of the newest brain file on disk. Null if no files found.</summary>
        public DateTime? DataAsOf { get; set; }
        public CentralBankDto? CentralBank { get; set; }
        public List<NodeDto> Nodes { get; set; } = new();
        public List<FactionDto> Factions { get; set; } = new();
        public List<FleetDto> Fleets { get; set; } = new();
        public List<PlanetDto> Planets { get; set; } = new();

        /// <summary>
        /// Factions named by <c>index.json</c> whose brain could not be read, and why — one entry per
        /// tag, empty on the healthy path.
        ///
        /// <para><b>Why this is a field and not a swallowed exception.</b> A brain that fails to load
        /// is skipped, and a skipped brain is indistinguishable from a faction that does not exist:
        /// the snapshot is simply smaller and everything downstream of it is internally consistent.
        /// Found live 2026-08-26 — the dashboard showed <b>2</b> factions of 13 and 45 nodes of 110,
        /// with no error anywhere, because the API was one commit behind the plugin and the two
        /// disagreed about the shape of <c>Knowledge</c>. The only two that loaded were the only two
        /// with no knowledge entries. Nothing said so.</para>
        /// </summary>
        public List<BrainLoadFailureDto> BrainLoadFailures { get; set; } = new();

        /// <summary>Inputs this snapshot needed and could not read, and why — empty on the healthy
        /// path.
        ///
        /// <para><b>Why this is a field and not a silent default.</b> Every tuning loader here opens
        /// with <c>if (!File.Exists(path)) return;</c>, so a file that is not where the loader looked
        /// leaves the <see cref="Factions.Core.FactionTuning"/> at its field defaults and the
        /// snapshot is built on them. For the transit contract those defaults are zero, and a zero
        /// transit contract does not render as an error: it renders as every fleet in the world
        /// standing still at 100% progress. Found live 2026-09-03 (FAC-242) — all 59 fleets read
        /// <c>speed=0 progress=1</c> because both reads went to the shared data directory, where
        /// neither tuning file has ever lived.</para>
        /// </summary>
        public List<InputLoadFailureDto> InputLoadFailures { get; set; } = new();
    }

    /// <summary>One faction whose brain is on the index but could not be read.</summary>
    public sealed class BrainLoadFailureDto
    {
        public string Tag { get; set; } = "";
        /// <summary>Exception type and message. Usually a schema disagreement between this build and
        /// whichever build wrote the file, so the type alone is often the whole diagnosis.</summary>
        public string Error { get; set; } = "";
    }

    /// <summary>One input the snapshot depends on that could not be read where it was looked for.</summary>
    public sealed class InputLoadFailureDto
    {
        /// <summary>What was being loaded — a file name, or the name of the contract several files
        /// contribute to.</summary>
        public string Input { get; set; } = "";
        /// <summary>What went wrong, naming the directories searched, and what the snapshot shows
        /// as a result.</summary>
        public string Error { get; set; } = "";
    }

    public sealed class PlanetDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Planet";
        public string Generator { get; set; } = "Unknown";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double SurfaceRadius { get; set; }
        public double GravityRadius { get; set; }
        public double AtmosphereRadius { get; set; }
        public bool HasAtmosphere { get; set; }
    }

    public sealed class CentralBankDto
    {
        public long CurrentMoneySupply { get; set; }
        public long TargetMoneySupply { get; set; }
        public long MoneySupplyGap { get; set; }
        public long WorldCollateralValue { get; set; }
        /// <summary>Collateral the target is set against; above <see cref="WorldCollateralValue"/>
        /// while a recent fall is still being phased in.</summary>
        public long AnchorCollateralValue { get; set; }
        public double ReserveRatio { get; set; }
        public int TerritoryNodeCount { get; set; }
        public int FoundedNodeCount { get; set; }
        public int CapabilityLevelCount { get; set; }
        /// <summary>Every hull the world owns, idle plus in flight. Kept as the name an older
        /// dashboard build already reads, so it picks up the total without a rebuild.</summary>
        public int ShipCount { get; set; }
        public int IdleShipCount { get; set; }
        public int InFlightShipCount { get; set; }
        public List<MonetaryEventDto> BankEvents { get; set; } = new();
        public List<MonetaryEventDto> DebtEvents { get; set; } = new();
    }

    public sealed class MonetaryEventDto
    {
        public DateTime UtcNow { get; set; }
        public string Scope { get; set; } = "";
        public string FactionTag { get; set; } = "";
        public string Type { get; set; } = "";
        public long Amount { get; set; }
        public long TreasuryAfter { get; set; }
        public long DebtAfter { get; set; }
        public long MoneySupplyAfter { get; set; }
        public long MoneySupplyGapAfter { get; set; }
        public string Message { get; set; } = "";
    }

    public sealed class NodeDto
    {
        public string Id { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? OwnerTag { get; set; }
        public double HoldStrength { get; set; }
        public double Radius { get; set; }
        public double ResourceValue { get; set; }
        /// <summary>Measured ore density, 0..1 — what this node's mint and drill rate scale by.
        /// Sent beside <see cref="ResourceValue"/> rather than folded into it because the two answer
        /// different questions and, live, disagree: a thick ice seam is worth little and mines fast.
        /// See docs/issues/FAC-219.</summary>
        public double Richness { get; set; }
        /// <summary>The bare subtype of the ore dominating this node's deposit ("Iron", "Ice",
        /// "Platinum"), or empty when there is none. What a node is called is derived from this and
        /// the two fields below, in the browser — the same three answers the SE client is sent, so
        /// the two front ends cannot disagree about what a node is. See FAC-102 / FAC-114.</summary>
        public string DominantOre { get; set; } = "";
        /// <summary>Whether this node's ground has been sampled at all. Empty ore and never looked
        /// at are opposite instructions to a reader — do not bother, versus go and look — and an
        /// empty <see cref="DominantOre"/> alone cannot tell them apart.</summary>
        public bool Surveyed { get; set; }
        /// <summary>Absolute UTC deadline before which this node cannot be mined again, or null when
        /// it has never been mined.
        ///
        /// <para>Absent means eligible-and-never-mined, which is the state of almost every node —
        /// 1,418 of 1,459 when FAC-210 was filed — so the field is omitted rather than written as
        /// <see cref="DateTime.MinValue"/>, which a browser would render as a cooldown that expired
        /// in the year 1. A deadline that has already passed is still sent: expired and never-mined
        /// are different facts, and only the second is absent.</para>
        ///
        /// <para>The deadline ships, not a computed <c>onCooldown</c> boolean, because the snapshot
        /// is cached for 5 s and held longer by the browser — a server-side boolean is stale on
        /// arrival and cannot say how much time is left.</para></summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? MineCooldownUntilUtc { get; set; }
        public bool HasStation { get; set; }
        public bool HasInfrastructure { get; set; }
        public string? DisplayName { get; set; }
        public Dictionary<string, int>? Capabilities { get; set; }
        public Dictionary<string, long>? ReservedInventory { get; set; }
        public Dictionary<string, long>? Inventory { get; set; }
        public Dictionary<string, int>? ShipStock { get; set; }
        public List<BuildQueueDto>? BuildQueue { get; set; }
        /// <summary>This node's repair queue (<c>SpatialNode.DamagedHulls</c>) in the order the yard
        /// serves it, identical hulls folded into one row (<c>HullDamage.QueueView</c>). Absent when
        /// no hull is in repair here.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RepairRowDto>? RepairQueue { get; set; }
        /// <summary>This node's live siege, or absent when nobody is attacking it — which is every
        /// node on an ordinary board, so the whole object is omitted rather than written as zeros
        /// on all 1,400-odd of them (CLAUDE.md rule 4).
        ///
        /// <para>A siege is the one attack on a node that arrives with no fleet behind it: a player
        /// shooting a station produces no <see cref="FleetDto"/>, so a map that reads attacks off
        /// fleets shows an untouched world while a station is being taken. See FAC-258.</para></summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SiegeDto? Siege { get; set; }
    }

    /// <summary>A player siege in progress against one node's station, as a reader needs it.
    ///
    /// <para>Sent only for a node that carries one. The three phases a client has to tell apart are
    /// the shield holding (<see cref="BreachedUtc"/> null), the shield down and the station being
    /// shot (<see cref="BreachedUtc"/> set), and the raid already won and its conquer clock running
    /// (<see cref="SucceededUtc"/> set).</para></summary>
    public sealed class SiegeDto
    {
        /// <summary>Zone integrity, 0..1, 1 being untouched. Derived through
        /// <c>PlayerSiege.IntegrityFraction</c> — the same call the host colours the in-game safe
        /// zone with, so the map and the bubble agree by construction rather than by coincidence.
        ///
        /// <para>The fraction ships rather than the raw pool because the pool's size is a tuning
        /// number the browser has no other reason to know, and 0..1 is what both surfaces
        /// draw.</para></summary>
        public double Integrity { get; set; }
        /// <summary>When the current siege's first absorbed round landed. The dwell is measured
        /// from here.</summary>
        public DateTime StartedUtc { get; set; }
        /// <summary>Seconds the attacker must hold the pool down before the shield drops
        /// (<c>FactionTuning.RaidZoneSiegeDwellSeconds</c>). Sent with the siege rather than assumed
        /// by the client for the same reason a fleet carries its own speed: it is tunable, and a
        /// countdown computed against the wrong number is worse than none.</summary>
        public double DwellSeconds { get; set; }
        /// <summary>When the shield went down, or null while it is still up. While set, the pool is
        /// frozen and the fight is with the station itself.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? BreachedUtc { get; set; }
        /// <summary>Cumulative player block damage against the station as a fraction of what the
        /// raid needs to succeed, 0..1. Meaningful only once <see cref="BreachedUtc"/> is set;
        /// before the breach nothing can be damaged and it stays 0.</summary>
        public double RaidProgress { get; set; }
        /// <summary>When the raid met its success criterion and the conquer clock started, or null
        /// while it has not.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? SucceededUtc { get; set; }
    }

    /// <summary>One row of a node's repair queue: one damaged hull, or <see cref="Count"/>
    /// identical ones.</summary>
    public sealed class RepairRowDto
    {
        public string PrefabId { get; set; } = "";
        public int Count { get; set; }
        /// <summary>1-based served position of the row's first hull.</summary>
        public int Position { get; set; }
        public double Integrity { get; set; }
        /// <summary><c>RepairState</c> name: WaitingForParts, Repairing, NoShipyard, YardPaused.</summary>
        public string State { get; set; } = "";
        /// <summary>Per hull.</summary>
        public long BillUnits { get; set; }
        public long CollectedUnits { get; set; }
        public DateTime QueuedUtc { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? DoneUtc { get; set; }
        /// <summary>Per hull: the bill lines still short, and by how much. Absent once the parts are in.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, long>? Missing { get; set; }
    }

    public sealed class BuildQueueDto
    {
        public string Id { get; set; } = "";
        public string Role { get; set; } = "";
        public string? PrefabId { get; set; }
        public Dictionary<string, long> Bom { get; set; } = new();
        public Dictionary<string, long> Collected { get; set; } = new();
        public Dictionary<string, long> ReservedInputs { get; set; } = new();
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    public sealed class CampaignObjectiveDto
    {
        public string Kind { get; set; } = "";
        public string TargetNodeId { get; set; } = "";
    }

    public sealed class CampaignFocusDto
    {
        public string Id { get; set; } = "";
        public string CenterNodeId { get; set; } = "";
        public double Priority { get; set; }
        public DateTime SetAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public List<string> RegionMembers { get; set; } = new();
        public List<CampaignObjectiveDto> Objectives { get; set; } = new();
    }

    public sealed class FactionDto
    {
        public string Tag { get; set; } = "";
        public string PersonalityId { get; set; } = "default";
        public long Treasury { get; set; }
        public long Debt { get; set; }
        public long SupportDebt { get; set; }
        public long AssetValue { get; set; }
        public long NetWorth { get; set; }
        public double[] Drives { get; set; } = new double[5];
        public Dictionary<string, double> Relations { get; set; } = new();
        public Dictionary<string, double>? Fatigue { get; set; }
        public Dictionary<string, DateTime>? TruceUntil { get; set; }
        public List<CampaignFocusDto> CampaignFoci { get; set; } = new();
        public List<string> Log { get; set; } = new();
        public Dictionary<string, double>? DemandPressure { get; set; }
        public bool IsDormant { get; set; }
        public int NodeCount { get; set; }
        /// <summary>Hulls in repair across the faction's nodes, and how many of them still wait for
        /// parts.</summary>
        public int HullsInRepair { get; set; }
        public int HullsWaitingForParts { get; set; }
        /// <summary>Every hull the faction owns, idle plus in flight; the same counts the Central
        /// Bank sums for the world.</summary>
        public int ShipCount { get; set; }
        public int IdleShipCount { get; set; }
        public int InFlightShipCount { get; set; }
    }

    public sealed class ProductionSnapshotDto
    {
        public DateTime At { get; set; }
        public List<ThroughputEntryDto> Entries { get; set; } = new();
    }

    public sealed class ThroughputEntryDto
    {
        public string FactionTag      { get; set; } = "";
        public string NodeId          { get; set; } = "";
        public string Cap             { get; set; } = "";
        public string PrimaryInputId  { get; set; } = "";
        public string PrimaryOutputId { get; set; } = "";
        /// <summary>Capacity: machine speed x slots x yield scaling, ignoring input supply.</summary>
        public double NominalItemsPerMinute  { get; set; }
        /// <summary>What actually reached inventory per minute over the history window.</summary>
        public double ObservedItemsPerMinute { get; set; }
        public double EffectiveMachineSpeed  { get; set; }
        public double OutputScale            { get; set; }
        public int    SlotCount       { get; set; }
    }

    public sealed class ProductionHistorySnapshotDto
    {
        public DateTime At { get; set; }
        public List<ProductionHistoryEntryDto> Entries { get; set; } = new();
    }

    public sealed class ProductionHistoryEntryDto
    {
        public string   FactionTag { get; set; } = "";
        public string   NodeId     { get; set; } = "";
        public string   RecipeId   { get; set; } = "";
        public string   InvKey     { get; set; } = "";
        public bool     IsOutput   { get; set; }
        public long     Quantity   { get; set; }
        public DateTime StartUtc   { get; set; }
        public DateTime EndUtc     { get; set; }
    }

    public sealed class DemandSnapshotDto
    {
        public DateTime At { get; set; }
        /// <summary>Rows the per-faction cap dropped across the roster. Non-zero means this is a
        /// bounded view: every demanded key is still represented (the cap is spent round-robin
        /// across keys), but a key's row list is a prefix of its real one.</summary>
        public int Truncated { get; set; }
        public List<DemandRowDto> Entries { get; set; } = new();
    }

    /// <summary>One live row of the demand register.
    ///
    /// <para><see cref="Quantity"/> is GROSS by contract — <c>MatchVectors</c> nets the
    /// destination's own stock at dispatch — so <see cref="HaveAtNode"/> travels with it. A row read
    /// without it says nothing about whether anything will actually move.</para></summary>
    public sealed class DemandRowDto
    {
        public string   FactionTag  { get; set; } = "";
        public string   Kind        { get; set; } = "";
        public string?  NodeId      { get; set; }
        public long     Quantity    { get; set; }
        public long     HaveAtNode  { get; set; }
        public string   DemandClass { get; set; } = "";
        public bool     ChainSized  { get; set; }
        public DateTime ExpiresUtc  { get; set; }
    }

    public sealed class PerfWindowDto
    {
        public int    Calls { get; set; }
        public double AvgMs { get; set; }
        public double MaxMs { get; set; }
    }

    public sealed class FrameTimingWindowDto
    {
        public long   Calls           { get; set; }
        public long   SampledFrames   { get; set; }
        public double BudgetMs        { get; set; }
        public double AvgMs           { get; set; }
        public double MaxMs           { get; set; }
        public double P50Ms           { get; set; }
        public double P95Ms           { get; set; }
        public double P99Ms           { get; set; }
        public long   OverBudgetCount { get; set; }
    }

    public sealed class HostFramePerfDto
    {
        public FrameTimingWindowDto BaseUpdate         { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto Instrumentation    { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto PlayerCache        { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto RuntimeDispatch    { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto FleetObservation   { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto StationObservation { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto AsteroidObservation{ get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto Price              { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto Planet             { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto Bridge             { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto Presence           { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto Gps                { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto StoreRefresh       { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto ContractGen        { get; set; } = new FrameTimingWindowDto();
        public FrameTimingWindowDto StoreTxn           { get; set; } = new FrameTimingWindowDto();
    }

    /// <summary>Phase breakdown of <c>PerfSnapshotDto.Econ</c> (<c>DrainGlobalPass</c>) — Slice 1 of
    /// <c>.hermes/plans/2026-08-17_094339-global-pass-econ-cursor.md</c>, added to localize which
    /// sub-phase (snapshot rebuild, income accrual, diplomacy, decay, station rename, arrival
    /// resolution, diplomatic events, lifecycle transitions) is the dashboard's reported several-
    /// hundred-ms "econ" cost before choosing a cursor granularity for it.</summary>
    public sealed class EconPhasesDto
    {
        public PerfWindowDto Snapshot    { get; set; } = new PerfWindowDto();
        public PerfWindowDto Income      { get; set; } = new PerfWindowDto();
        public PerfWindowDto Diplomacy   { get; set; } = new PerfWindowDto();
        public PerfWindowDto Decay       { get; set; } = new PerfWindowDto();
        public PerfWindowDto Rename      { get; set; } = new PerfWindowDto();
        public PerfWindowDto Arrivals    { get; set; } = new PerfWindowDto();
        public PerfWindowDto DiploEvents { get; set; } = new PerfWindowDto();
        public PerfWindowDto Lifecycle   { get; set; } = new PerfWindowDto();
    }

    public sealed class ThinkStagesDto
    {
        public PerfWindowDto Sense             { get; set; } = new PerfWindowDto();
        public PerfWindowDto TradePriority     { get; set; } = new PerfWindowDto();
        public PerfWindowDto ReinforcePriority { get; set; } = new PerfWindowDto();
        public PerfWindowDto ThreatBonus       { get; set; } = new PerfWindowDto();
        public PerfWindowDto Generate          { get; set; } = new PerfWindowDto();
        public PerfWindowDto Score             { get; set; } = new PerfWindowDto();
        public PerfWindowDto Select            { get; set; } = new PerfWindowDto();
    }

    public sealed class BrainPhasesDto
    {
        public PerfWindowDto Think { get; set; } = new PerfWindowDto();
        public ThinkStagesDto ThinkStages { get; set; } = new ThinkStagesDto();
        public PerfWindowDto Enact { get; set; } = new PerfWindowDto();
        public PerfWindowDto Log   { get; set; } = new PerfWindowDto();
    }

    public sealed class ProductionTelemetryDto
    {
        public int Calls { get; set; }
        public long Factions { get; set; }
        public long OwnedNodes { get; set; }
        public long CapabilityBanks { get; set; }
        /// <summary>Machine slots whose credited time hit the catch-up window. Non-zero in steady
        /// state means passes run later than the window allows and production is under-credited.</summary>
        public long ElapsedClamps { get; set; }
        public long SlotsVisited { get; set; }
        public long RecipesExamined { get; set; }
        /// <summary>Recipes the bank index reached past because nothing demanded their output.
        /// Rises as the index saves more work; with <see cref="RecipesExamined"/> it is the whole
        /// usable recipe set.</summary>
        public long RecipesSkippedNoDemand { get; set; }
        public long BlockedRecipes { get; set; }
        public long BlockedInputPosts { get; set; }
        public long DemandPosts { get; set; }
        public long DemandRefreshes { get; set; }
        public long CatalogOffersScanned { get; set; }
        public long InvestmentPasses { get; set; }
        public long RefinementPasses { get; set; }
        public double PreparationMs { get; set; }
        public double CapabilityBankMs { get; set; }
        public double BlockedDemandMs { get; set; }
        public long Restarts { get; set; }
        public double MaxCycleAgeMs { get; set; }
        public double InvestmentRefinementMs { get; set; }
        public double CatalogMs { get; set; }
    }

    public sealed class OperationsTelemetryDto
    {
        public int Calls { get; set; }
        public long Factions { get; set; }
        public long OwnedNodes { get; set; }
        public long Demands { get; set; }
        public long ItemGroups { get; set; }
        public long Targets { get; set; }
        public long CandidateSources { get; set; }
        public long DistanceOrderBuilds { get; set; }
        public long Vectors { get; set; }
        public long RouteCacheHits { get; set; }
        public long RouteCacheMisses { get; set; }
        public long DirectDispatches { get; set; }
        public long PickupDispatches { get; set; }
        public long NoShipOutcomes { get; set; }
        public long RouteInfeasibleOutcomes { get; set; }
        public long LoadCargoFailures { get; set; }
        public long AlternateSourceScans { get; set; }
        public long ShipyardScans { get; set; }
        public long ShipSuitabilityChecks { get; set; }
        public long FallbackCandidates { get; set; }
        public long UniqueFallbackKeys { get; set; }
        public long DemandPosts { get; set; }
        public long DemandRefreshes { get; set; }
        public double PreparationMs { get; set; }
        public double DemandHistoryMs { get; set; }
        public double ClaimIndexMs { get; set; }
        public double TargetMatchingMs { get; set; }
        public double RoutePlanningMs { get; set; }
        public double DirectDispatchMs { get; set; }
        public double AlternatePickupMs { get; set; }
        public double AlternateScanMs { get; set; }
        public double AlternateRevalidateMs { get; set; }
        public double ShipyardFallbackMs { get; set; }
        public double FallbackPublicationMs { get; set; }
        public long Yields { get; set; }
        public long Restarts { get; set; }
        public long SweepsCompleted { get; set; }
    }

    public sealed class TradeTelemetryDto
    {
        public int Calls { get; set; }
        /// <summary>Roster size walked this window. The sweep visits every faction once per
        /// direction; this counts the buy pass alone, so it stays a roster count.</summary>
        public long Factions { get; set; }
        public long EligibleFactions { get; set; }
        /// <summary>(actor, partner) pairs examined, across both directions.</summary>
        public long Partners { get; set; }
        /// <summary>Unserved demand rows read, after folding rows naming the same (node, item).</summary>
        public long UnservedSeen { get; set; }
        /// <summary>Rows that found a consenting source and became a material vector.</summary>
        public long Vectors { get; set; }
        /// <summary>Sell-direction convoys dispatched.</summary>
        public long Dispatched { get; set; }
        /// <summary>Buy-direction convoys dispatched.</summary>
        public long Procured { get; set; }
        /// <summary>Vectors with no hull anywhere that could fly them, which staged a Ship:Trade
        /// demand instead.</summary>
        public long NoShip { get; set; }
        /// <summary>Rows no whitelisted partner would part with the stock for.</summary>
        public long RefusedConsent { get; set; }
        /// <summary>Rows already claimed by an in-flight convoy or a player contract.</summary>
        public long Claimed { get; set; }
        /// <summary>Convoys the load gate judged not worth their hull.</summary>
        public long AssessRefused { get; set; }
        public long Tier1 { get; set; }
        public long Tier2 { get; set; }
        public long Tier3 { get; set; }
        public long BudgetBlocked { get; set; }
        public double EligibilityFilterMs { get; set; }
        public double DispatchMs { get; set; }
        public long Yields { get; set; }
        public long Restarts { get; set; }
        public long SweepsCompleted { get; set; }
    }

    public sealed class SchedulerActorDto
    {
        public string Name             { get; set; } = "";
        public double IntervalMs       { get; set; }
        public double EstimateMs       { get; set; }
        public long   Calls            { get; set; }
        public double AvgMs            { get; set; }
        public double MaxMs            { get; set; }
        public double P99Ms            { get; set; }
        public long   OverBudgetCount  { get; set; }
        public double ExcessMs         { get; set; }
        public long   Admissions       { get; set; }
        public long   Completions      { get; set; }
        public long   Yields           { get; set; }
        public long   Skips            { get; set; }
        public long   NotDue           { get; set; }
        public long   ForcedAdmissions { get; set; }
        public long   DeniedByBudget   { get; set; }
        public double MaxDeferralMs    { get; set; }
        public double EstErrorAvgMs    { get; set; }
        public double EstErrorMaxMs    { get; set; }
        public bool   SweepInProgress  { get; set; }
    }

    public sealed class FrameSchedulerDto
    {
        public bool   SchedulerEnabled      { get; set; }
        public bool   SharedBudgetEnabled   { get; set; }
        public long   AdmittedUnitCount     { get; set; }
        public long   FramesWithNoAdmission { get; set; }
        public long   DeniedByBudgetCount   { get; set; }
        public long   ForcedAdmissionCount  { get; set; }
        public int    MaxUnitsInOneFrame    { get; set; }
        public double NoStackRatio          { get; set; }
        public List<SchedulerActorDto> Actors { get; set; } = new List<SchedulerActorDto>();
        public List<string> RechunkQueue      { get; set; } = new List<string>();

        public double SoftDeadlineMs { get; set; }
        public long NotDueFrames { get; set; }
        public long DueUnsafeCount { get; set; }
        public long DeferredWorkCount { get; set; }
        public double OldestDeferredAgeMs { get; set; }
        public long ExpiredBeforeUnsafeCount { get; set; }
        // Diagnostics only (live troubleshooting, 2026-08-17) -- perf.json is written as an anonymous
        // object and this Api project's /api/perf handler round-trips it through this typed Dto, so
        // any host-side field without a matching property here is silently dropped, not just absent
        // from the file (confirmed live: the fields existed in perf.json but never reached callers).
        public string GlobalPassPhase { get; set; } = "";
        public int GlobalPassSnapshotBuildIndex { get; set; }
        public int GlobalPassSnapshotBuildTotal { get; set; }
        public int GlobalPassSnapshotRestarts { get; set; }
        public string GlobalPassIncomePhase { get; set; } = "";
        public int GlobalPassIncomeIndex { get; set; }
        public int GlobalPassIncomeTotal { get; set; }
        public double GlobalPassElapsedMs { get; set; }
    }

    public sealed class PerfSnapshotDto
    {
        public DateTime     At            { get; set; }
        public double       WindowSeconds { get; set; }
        public PerfWindowDto Brain  { get; set; } = new PerfWindowDto();
        public BrainPhasesDto BrainPhases { get; set; } = new BrainPhasesDto();
        public PerfWindowDto Prod   { get; set; } = new PerfWindowDto();
        public PerfWindowDto Ops    { get; set; } = new PerfWindowDto();
        public PerfWindowDto Yard   { get; set; } = new PerfWindowDto();
        public PerfWindowDto Fleet  { get; set; } = new PerfWindowDto();
        public PerfWindowDto Trade  { get; set; } = new PerfWindowDto();
        public PerfWindowDto Stock  { get; set; } = new PerfWindowDto();
        public PerfWindowDto Econ   { get; set; } = new PerfWindowDto();
        public EconPhasesDto EconPhases { get; set; } = new EconPhasesDto();
        public PerfWindowDto Obs    { get; set; } = new PerfWindowDto();
        public PerfWindowDto Flight { get; set; } = new PerfWindowDto();
        public PerfWindowDto StObs  { get; set; } = new PerfWindowDto();
        public PerfWindowDto FlushTotal      { get; set; } = new PerfWindowDto();
        public PerfWindowDto FlushBrains     { get; set; } = new PerfWindowDto();
        public PerfWindowDto FlushProduction { get; set; } = new PerfWindowDto();
        public PerfWindowDto FlushSnapshot   { get; set; } = new PerfWindowDto();
        public ProductionTelemetryDto ProductionTelemetry { get; set; } = new ProductionTelemetryDto();
        public OperationsTelemetryDto OperationsTelemetry { get; set; } = new OperationsTelemetryDto();
        public TradeTelemetryDto TradeTelemetry { get; set; } = new TradeTelemetryDto();
        public FrameSchedulerDto Scheduler { get; set; } = new FrameSchedulerDto();
        public FrameTimingWindowDto Frame { get; set; } = new FrameTimingWindowDto();
        public HostFramePerfDto Host { get; set; } = new HostFramePerfDto();
    }

    public sealed class FleetDto
    {
        public string FleetId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string ActorTag { get; set; } = "";
        public string? TargetTag { get; set; }
        public string? OriginNodeId { get; set; }
        public string? TargetNodeId { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double OriginZ { get; set; }
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public double TargetZ { get; set; }
        public double CurrentX { get; set; }
        public double CurrentY { get; set; }
        public double CurrentZ { get; set; }
        public DateTime DepartedUtc { get; set; }
        public DateTime EstimatedArrivalUtc { get; set; }
        public double CompletionRatio { get; set; }
        public double TransitProgressRatio { get; set; }
        public double TransitDistanceKm { get; set; }
        public double FleetSpeedMinKmPerMin { get; set; }
        public double FleetSpeedKmPerMin { get; set; }
        public double FleetAccelKmPerMin2 { get; set; }
        public long CargoValue { get; set; }
        public Dictionary<string, long>? Cargo { get; set; }
        public int ConvoySize { get; set; }
        public bool Resolved { get; set; }
        /// <summary>Active clearance polyline: its total length, how much of it precedes the current
        /// leg, and the clock its single acceleration profile is anchored to. All zero when the fleet
        /// is not flying a routed detour. Lets a client animate the whole arc continuously instead of
        /// restarting at every waypoint.</summary>
        public double RouteDistanceKm { get; set; }
        public double RouteCoveredKm { get; set; }
        public DateTime RouteDepartedUtc { get; set; }
        public string CurrentStopKind { get; set; } = "Service";
        /// <summary>When CurrentStopKind == "Pickup": the item/quantity this convoy is en route to
        /// load at TargetNodeId before continuing to its real delivery stop. Null/0 otherwise.</summary>
        public string? PickupItemKey { get; set; }
        public long PickupQuantity { get; set; }
        public List<FleetStopDto>? RemainingStops { get; set; }
        public string[]? PathNodeIds { get; set; }
        public long MaterialisedEntityId { get; set; }
        public string? FerryRole { get; set; }
        /// <summary>Prefab SubtypeId of the convoy's ships; "" for free-built, null for
        /// pre-Muster brain files that never recorded one. Joins against /api/prefabs.</summary>
        public string? ShipPrefabId { get; set; }
    }

    public sealed class FleetStopDto
    {
        public string NodeId { get; set; } = "";
        public string Kind { get; set; } = "Service";
        public bool HasPosition { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public sealed class WeaponBandDto
    {
        public double Dps { get; set; }
        public double RangeM { get; set; }
    }

    /// <summary>Mirrors Factions.Core.ThrustBand — one propulsion-curve signature's summed force,
    /// within a single ThrustDirection bucket (see PrefabDto.ThrustBands).</summary>
    public sealed class ThrustBandDto
    {
        public string ThrusterType { get; set; } = "";
        public double ForceMagnitudeN { get; set; }
        public double MinPlanetaryInfluence { get; set; }
        public double MaxPlanetaryInfluence { get; set; }
        public double EffectivenessAtMinInfluence { get; set; }
        public double EffectivenessAtMaxInfluence { get; set; }
        public bool NeedsAtmosphereForInfluence { get; set; }
    }

    /// <summary>Static per-prefab metadata from prefab_catalogue.json (written once at session
    /// load by PrefabCatalogueScanner) — cost, cargo, and combat loadout for a single ship. Keyed
    /// by prefab SubtypeId in the /api/prefabs response; join against FleetDto.ShipPrefabId.</summary>
    public sealed class PrefabDto
    {
        public long Cost { get; set; }
        public string FactionHint { get; set; } = "All";
        public string[]? Roles { get; set; }
        public List<WeaponBandDto>? WeaponBands { get; set; }
        public long CargoLiters { get; set; }
        public string GridSize { get; set; } = "Large";
        /// <summary>Sparse, keyed by mounted thruster facing ("Forward"/"Backward"/"Up"/"Down"/
        /// "Left"/"Right") — only directions with at least one mounted thruster are present.</summary>
        public Dictionary<string, List<ThrustBandDto>>? ThrustBands { get; set; }
        public double DryMassKg { get; set; }
    }
}
