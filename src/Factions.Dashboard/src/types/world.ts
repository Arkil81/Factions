export interface BuildQueueEntry {
  id: string
  role: string
  prefabId: string | null
  bom: Record<string, number>
  collected: Record<string, number>
  reservedInputs: Record<string, number>
  createdUtc: string
  expiresUtc: string
}

/** One row of a node's repair queue: one damaged hull, or `count` identical ones, in the order
 *  the yard serves them -- smallest outstanding bill first. */
export interface RepairRowDto {
  prefabId: string
  count: number
  /** 1-based served position of the row's first hull. */
  position: number
  integrity: number
  state: 'WaitingForParts' | 'Repairing' | 'NoShipyard' | 'YardPaused' | string
  /** Per hull. */
  billUnits: number
  collectedUnits: number
  queuedUtc: string
  /** When a parts-complete hull rejoins stock; absent while it waits for parts. */
  doneUtc?: string | null
  /** Per hull: bill lines still short, and by how much. Absent once the parts are in. */
  missing?: Record<string, number> | null
}

export interface NodeDto {
  id: string
  x: number
  y: number
  z: number
  ownerTag: string | null
  holdStrength: number
  radius: number
  resourceValue: number
  /** Measured ore density, 0..1 -- what the node's mint and drill rate scale by, as against
   *  `resourceValue`, which is price-weighted worth. The two disagree: a thick ice seam is worth
   *  little and mines fast. See docs/issues/FAC-219. */
  richness: number
  /** Bare subtype of the dominating ore ("Iron", "Ice", "Platinum"); empty when there is none. */
  dominantOre: string
  /** Whether the ground has been sampled at all -- which an empty `dominantOre` alone cannot say. */
  surveyed: boolean
  /** Absolute UTC deadline before which this node cannot be mined again. Absent when the node has
   *  never been mined; a deadline already in the past means the cooldown has expired. Both are "no
   *  active cooldown". Compare against the snapshot's own `utcNow`, never the browser clock. */
  mineCooldownUntilUtc?: string | null
  displayName: string | null
  hasStation: boolean
  hasInfrastructure: boolean
  capabilities: Record<string, number> | null
  reservedInventory: Record<string, number> | null
  inventory: Record<string, number> | null
  shipStock: Record<string, number> | null
  buildQueue: BuildQueueEntry[] | null
  /** Damaged hulls waiting here for repair, in served order. Absent when there are none. */
  repairQueue?: RepairRowDto[] | null
  /** This node's live siege, absent when nobody is attacking it. A player shooting a station
   *  produces no fleet, so this is the only thing on the wire that says the node is under attack.
   *  See docs/issues/FAC-258. */
  siege?: SiegeDto | null
}

/** A player siege against one node's station. Three phases, told apart by which stamps are set:
 *  the shield holding (`breachedUtc` absent), the shield down and the station being shot
 *  (`breachedUtc` set), and the raid already won with its conquer clock running (`succeededUtc`
 *  set). */
export interface SiegeDto {
  /** Zone integrity 0..1, 1 being untouched — the same number the in-game safe zone is coloured
   *  by, derived server-side through `PlayerSiege.IntegrityFraction` so the two cannot disagree. */
  integrity: number
  /** When this siege's first absorbed round landed; the dwell is measured from here. */
  startedUtc: string
  /** Seconds the attacker must hold the pool down before the shield drops. Sent with the siege
   *  because it is tunable — a countdown against an assumed value is worse than none. */
  dwellSeconds: number
  /** When the shield went down. Absent while it is still up. */
  breachedUtc?: string | null
  /** Cumulative block damage as a fraction of what the raid needs, 0..1. Stays 0 before the
   *  breach, when nothing can be damaged. */
  raidProgress: number
  /** When the raid succeeded and the conquer clock started. Absent while it has not. */
  succeededUtc?: string | null
}

export interface CampaignObjectiveDto {
  kind: string
  targetNodeId: string
}

export interface CampaignFocusDto {
  id: string
  centerNodeId: string
  priority: number
  setAtUtc: string
  expiresAtUtc: string
  regionMembers: string[]
  objectives: CampaignObjectiveDto[]
}

export interface FactionDto {
  tag: string
  personalityId: string
  treasury: number
  debt: number
  supportDebt: number
  assetValue: number
  netWorth: number
  /** length 5: Security=0, Growth=1, Wealth=2, Rivalry=3, Caution=4 */
  drives: number[]
  relations: Record<string, number>
  fatigue: Record<string, number> | null
  truceUntil: Record<string, string> | null
  campaignFoci: CampaignFocusDto[]
  log: string[]
  demandPressure: Record<string, number> | null
  isDormant: boolean
  nodeCount: number
  /** Hulls in repair across the faction's nodes, and how many of them still wait for parts. */
  hullsInRepair?: number
  hullsWaitingForParts?: number
  /** Every hull the faction owns: parked at its nodes plus aboard its unresolved fleets. */
  shipCount?: number
  idleShipCount?: number
  inFlightShipCount?: number
}

export interface FleetDto {
  fleetId: string
  kind: 'Trade' | 'Raid' | 'Guard' | 'Patrol' | 'Haul' | string
  actorTag: string
  targetTag: string | null
  originNodeId: string | null
  targetNodeId: string | null
  originX: number; originY: number; originZ: number
  targetX: number; targetY: number; targetZ: number
  currentX: number; currentY: number; currentZ: number
  departedUtc: string
  estimatedArrivalUtc: string
  completionRatio: number
  transitProgressRatio: number
  transitDistanceKm: number
  fleetSpeedMinKmPerMin: number
  fleetSpeedKmPerMin: number
  fleetAccelKmPerMin2: number
  cargoValue: number
  cargo: Record<string, number> | null
  convoySize: number
  resolved: boolean
  /** Active clearance polyline the fleet is flying around a planet: total length, distance from the
   * route's start to the END of the current leg, and the clock the whole polyline's single
   * acceleration profile is anchored to. All zero when the fleet is on a plain leg. */
  routeDistanceKm: number
  routeCoveredKm: number
  routeDepartedUtc: string
  currentStopKind: string
  /** When currentStopKind === 'Pickup': the item/quantity this convoy is en route to load at
   * targetNodeId before continuing to its real delivery stop. Null/0 otherwise. */
  pickupItemKey: string | null
  pickupQuantity: number
  remainingStops: FleetStopDto[] | null
  pathNodeIds: string[] | null
  materialisedEntityId: number
  ferryRole: string | null
  /** Prefab SubtypeId of the convoy's ships; "" for free-built, null when unrecorded
   * (brain files persisted before this field was added). Join against the prefabs map. */
  shipPrefabId: string | null
}

export interface FleetStopDto {
  nodeId: string
  kind: string
  hasPosition: boolean
  x: number
  y: number
  z: number
}

export interface WeaponBandDto {
  dps: number
  rangeM: number
}

/** Static per-prefab metadata (cost, cargo, combat loadout) from /api/prefabs, keyed by
 * prefab SubtypeId. Fetched once — the catalogue only changes on a server-side rescan. */
export interface PrefabInfo {
  cost: number
  factionHint: string
  roles: string[] | null
  weaponBands: WeaponBandDto[] | null
  cargoLiters: number
  gridSize: string
}

export type PrefabCatalogue = Record<string, PrefabInfo>

export interface CentralBankDto {
  currentMoneySupply: number
  targetMoneySupply: number
  moneySupplyGap: number
  worldCollateralValue: number
  reserveRatio: number
  territoryNodeCount: number
  foundedNodeCount: number
  capabilityLevelCount: number
  /** Every hull the world owns: idle stock plus ships currently in flight. */
  shipCount: number
  /** Hulls parked at a node. Dips on every dispatch — see shipCount for the stable total. */
  idleShipCount: number
  /** Hulls aboard unresolved fleets. */
  inFlightShipCount: number
  bankEvents: MonetaryEventDto[]
  debtEvents: MonetaryEventDto[]
}

export interface MonetaryEventDto {
  utcNow: string
  scope: string
  factionTag: string
  type: string
  amount: number
  treasuryAfter: number
  debtAfter: number
  moneySupplyAfter: number
  moneySupplyGapAfter: number
  message: string
}

export interface FinanceLineDto {
  utcNow: string | null
  scope: string | null
  type: string | null
  amount: number
  treasuryAfter: number
  debtAfter: number
  message: string | null
}

export interface FinanceSnapshotDto {
  at: string
  factionTag: string
  treasury: number
  assets: number
  netWorth: number
  incomePerQuantum: number
  expensePerQuantum: number
  netPerQuantum: number
  report: FinanceLineDto[]
}

export interface WorldSnapshot {
  utcNow: string
  /** UTC timestamp of the newest brain file on disk. Null if files not found. */
  dataAsOf: string | null
  centralBank: CentralBankDto | null
  nodes: NodeDto[]
  factions: FactionDto[]
  fleets: FleetDto[]
  planets: PlanetDto[]
}

export interface PlanetDto {
  id: string
  name: string
  generator: string
  x: number
  y: number
  z: number
  surfaceRadius: number
  gravityRadius: number
  atmosphereRadius: number
  hasAtmosphere: boolean
}

export interface PerfWindowData {
  calls: number
  avgMs: number
  maxMs: number
  /** The bucket's whole share of the window. This is what ranks actors against each other; avg and
   *  max describe a single call, and for a resumable manager that yields hundreds of times per sweep
   *  the two orderings invert. Absent from perf.json written before 2026-09-08. */
  totalMs?: number
}

export interface ProductionTelemetryData {
  calls: number
  factions: number
  ownedNodes: number
  capabilityBanks: number
  elapsedClamps: number
  slotsVisited: number
  recipesExamined: number
  recipesSkippedNoDemand: number
  blockedRecipes: number
  blockedInputPosts: number
  demandPosts: number
  demandRefreshes: number
  catalogOffersScanned: number
  investmentPasses: number
  refinementPasses: number
  restarts: number
  /** Age of the oldest in-flight resumable cycle. A gauge, not a cost. */
  maxCycleAgeMs: number
  preparationMs: number
  capabilityBankMs: number
  blockedDemandMs: number
  investmentRefinementMs: number
  catalogMs: number
}

/** One OperationsManager sweep window (`FactionRuntime.OT`). Counters and `*Ms` phase timings share
 *  one flat object on the wire; `splitTelemetry` separates them for rendering. */
export interface OperationsTelemetryData {
  calls: number
  factions: number
  ownedNodes: number
  demands: number
  itemGroups: number
  targets: number
  candidateSources: number
  distanceOrderBuilds: number
  vectors: number
  routeCacheHits: number
  routeCacheMisses: number
  directDispatches: number
  pickupDispatches: number
  noShipOutcomes: number
  routeInfeasibleOutcomes: number
  loadCargoFailures: number
  alternateSourceScans: number
  shipyardScans: number
  shipSuitabilityChecks: number
  fallbackCandidates: number
  uniqueFallbackKeys: number
  demandPosts: number
  demandRefreshes: number
  preparationMs: number
  demandHistoryMs: number
  claimIndexMs: number
  targetMatchingMs: number
  routePlanningMs: number
  directDispatchMs: number
  alternatePickupMs: number
  alternateScanMs: number
  alternateRevalidateMs: number
  shipyardFallbackMs: number
  fallbackPublicationMs: number
  yields: number
  restarts: number
  sweepsCompleted: number
}

/** One TradeManager sweep window (`FactionRuntime.TT`). Same flat counter/timing shape as
 *  {@link OperationsTelemetryData}. */
export interface TradeTelemetryData {
  calls: number
  factions: number
  eligibleFactions: number
  partners: number
  unservedSeen: number
  vectors: number
  dispatched: number
  procured: number
  noShip: number
  refusedConsent: number
  claimed: number
  assessRefused: number
  tier1: number
  tier2: number
  tier3: number
  budgetBlocked: number
  eligibilityFilterMs: number
  dispatchMs: number
  yields: number
  restarts: number
  sweepsCompleted: number
}

/** The seven stages one `Think` call is measured in. */
export interface ThinkStagesData {
  sense: PerfWindowData
  tradePriority: PerfWindowData
  reinforcePriority: PerfWindowData
  threatBonus: PerfWindowData
  generate: PerfWindowData
  score: PerfWindowData
  select: PerfWindowData
}

/** The brain's three phases, with Think broken down further. Optional because perf.json from a host
 *  build that predates the breakdown omits it. */
export interface BrainPhasesData {
  think: PerfWindowData
  thinkStages: ThinkStagesData
  enact: PerfWindowData
  log: PerfWindowData
}

export interface EconPhasesData {
  snapshot: PerfWindowData
  income: PerfWindowData
  diplomacy: PerfWindowData
  decay: PerfWindowData
  rename: PerfWindowData
  arrivals: PerfWindowData
  diploEvents: PerfWindowData
  lifecycle: PerfWindowData
  /** Written by the host since the fog-of-war slices and the largest econ phase by maxMs live;
   *  optional because perf.json from an older build omits it. */
  knowledge?: PerfWindowData
}

/** One row of `scheduler.actors[]` -- how an actor is scheduled, what its unit cost, and how the
 *  shared budget treated it. `excessMs` is attributable over-budget time and is what orders the
 *  rechunk queue: an average is blind to call count and a maximum is blind to frequency. */
export interface SchedulerActorData {
  name: string
  intervalMs: number
  estimateMs: number
  calls: number
  avgMs: number
  maxMs: number
  p99Ms: number
  overBudgetCount: number
  excessMs: number
  admissions: number
  completions: number
  yields: number
  skips: number
  notDue: number
  forcedAdmissions: number
  deniedByBudget: number
  maxDeferralMs: number
  estErrorAvgMs: number
  estErrorMaxMs: number
  sweepInProgress: boolean
}

export interface FrameSchedulerData {
  softDeadlineMs: number
  schedulerEnabled?: boolean
  sharedBudgetEnabled?: boolean
  admittedUnitCount?: number
  framesWithNoAdmission?: number
  deniedByBudgetCount?: number
  forcedAdmissionCount?: number
  maxUnitsInOneFrame?: number
  /** frame.maxMs over the largest single actor unit -- what a shared budget can actually deliver.
   *  At or below 1.0 no two units stacked in the window's worst frame. */
  noStackRatio?: number
  actors?: SchedulerActorData[]
  /** Actor names with their attributable over-budget milliseconds, worst first. */
  rechunkQueue?: string[]
  notDueFrames: number
  dueUnsafeCount: number
  deferredWorkCount: number
  oldestDeferredAgeMs: number
  expiredBeforeUnsafeCount: number
  // Global-pass progress -- at live scale a full pass can span multiple 60s perf windows (large
  // worlds: many thousands of influence-graph nodes), so `econ`/`econPhases` can legitimately read
  // all-zero for several consecutive windows while the pass is healthy and progressing. These fields
  // let the dashboard show real in-progress state instead of a misleading blank econ row.
  globalPassPhase?: string
  globalPassSnapshotBuildIndex?: number
  globalPassSnapshotBuildTotal?: number
  globalPassSnapshotRestarts?: number
  globalPassIncomePhase?: string
  globalPassIncomeIndex?: number
  globalPassIncomeTotal?: number
  globalPassElapsedMs?: number
}

/** One FrameTimingWindow (src/Factions.Core/FrameTimingWindow.cs). Percentiles are conservative
 *  histogram bucket upper bounds, not interpolated values -- an exact number here means the sample
 *  landed in the open-ended overflow bucket, which reports maxMs. */
export interface FrameTimingWindowData {
  calls: number
  /** Calls held in the percentile ring. When below `calls`, p50/p95/p99 describe only the window's
   *  most recent `sampledFrames` calls while avg/max/overBudgetCount describe all of it. */
  sampledFrames: number
  budgetMs: number
  totalMs?: number
  avgMs: number
  maxMs: number
  p50Ms: number
  p95Ms: number
  p99Ms: number
  overBudgetCount: number
}

/** Per-actor breakdown of the Factions-owned portion of FactionsPlugin.Update(). `baseUpdate` is
 *  external Torch/SE correlation only and is deliberately not charged to the frame contract. */
export interface HostFramePerfData {
  baseUpdate: FrameTimingWindowData
  instrumentation: FrameTimingWindowData
  playerCache: FrameTimingWindowData
  runtimeDispatch: FrameTimingWindowData
  fleetObservation: FrameTimingWindowData
  stationObservation: FrameTimingWindowData
  asteroidObservation: FrameTimingWindowData
  featureRock?: FrameTimingWindowData
  price: FrameTimingWindowData
  planet: FrameTimingWindowData
  bridge: FrameTimingWindowData
  presence: FrameTimingWindowData
  gps: FrameTimingWindowData
  storeRefresh: FrameTimingWindowData
  contractGen: FrameTimingWindowData
  storeTxn: FrameTimingWindowData
}

export interface PerfSnapshot {
  at: string
  /** Seconds actually covered by this window. */
  windowSeconds: number
  /** Configured window length (`FactionTuning.PerfWindowSeconds`). Absent from an older build. */
  windowTargetSeconds?: number
  brain: PerfWindowData
  /** Absent from a host build that predates the brain-phase breakdown. */
  brainPhases?: BrainPhasesData
  prod:  PerfWindowData
  ops:   PerfWindowData
  yard:  PerfWindowData
  fleet: PerfWindowData
  trade: PerfWindowData
  stock: PerfWindowData
  econ:  PerfWindowData
  econPhases?: EconPhasesData
  obs:   PerfWindowData
  /** Per-frame fleet-transit advance. Written by the host since the resumable-cycle work and never
   *  rendered before 2026-09-08. */
  flight?: PerfWindowData
  stObs: PerfWindowData
  /** Asteroid-field observation. Reported separately from stObs since 2026-08-20 -- it used to be
   *  folded into the host stationObservation bucket, which hid it as the plugin's largest actor. */
  astObs?: PerfWindowData
  flushTotal:      PerfWindowData
  flushBrains:     PerfWindowData
  flushProduction: PerfWindowData
  flushSnapshot:   PerfWindowData
  productionTelemetry: ProductionTelemetryData
  operationsTelemetry?: OperationsTelemetryData
  tradeTelemetry?: TradeTelemetryData
  scheduler: FrameSchedulerData
  frame?: FrameTimingWindowData
  host?: HostFramePerfData
}

export const DRIVE_NAMES = ['Security', 'Growth', 'Wealth', 'Rivalry', 'Caution'] as const
export const DRIVE_COLORS = ['#e74c3c', '#2ecc71', '#f1c40f', '#e67e22', '#3498db'] as const

export interface OnlinePlayerDto {
  steamId: string
  identityId: number
  name: string
  hasPosition: boolean
  x: number
  y: number
  z: number
}
