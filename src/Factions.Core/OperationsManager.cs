using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>A matched shape — "N units of ItemKey should move from SourceNodeId to
    /// DeliverNodeId" — produced by <see cref="OperationsManager.MatchVectors"/> against the
    /// existing "Item:" demand signal. Not a persisted registry: matched fresh every tick from
    /// live inventory and <see cref="DemandRegister"/> state. See docs/material-vectors.md for the
    /// full design. <see cref="DeliverNodeId"/> always equals <see cref="NeedNodeId"/> for now —
    /// the two are kept distinct so a later accessibility-aware delivery point can be layered in
    /// without a shape change.</summary>
    public struct MaterialVector
    {
        public string FactionTag;
        public string ItemKey;         // concrete SubTypeId
        public long   Quantity;
        public string SourceNodeId;    // surplus node the match selected
        public string NeedNodeId;      // the node the Item: demand named
        public string DeliverNodeId;   // where cargo is actually dropped; == NeedNodeId today

        /// <summary><see cref="DemandUrgency.Score"/> of the demand this vector was matched against,
        /// carried here so the dispatch loop can rank vectors across item boundaries without
        /// re-reading the register. Higher goes first.
        /// <para>It is the demand's urgency, not this vector's share of it: several vectors can
        /// serve one destination from different sources, and they are equally urgent -- what differs
        /// between them is which source is nearest, which <see cref="MatchVectors"/> has already
        /// decided by the time they are emitted.</para></summary>
        public double Urgency;

        /// <summary>The destination's unmet quantity of <see cref="ItemKey"/> at match time, which
        /// <see cref="Quantity"/> is this vector's contribution toward. Carried so the dispatch gate
        /// can ask what share of the want this convoy actually closes — a question the load's volume
        /// cannot answer, since a small load may be a complete delivery or a useless dribble and
        /// those need opposite decisions. See docs/issues/FAC-232.</summary>
        public long Shortfall;
    }

    /// <summary>
    /// Fulfils "Item:{SubTypeId}" demands by matching surplus inventory from faction-owned nodes
    /// to the nodes that need the items (production input or ship BOM component), then dispatching
    /// a real cargo-carrying <see cref="ActionKind.Haul"/> convoy for each match — real transit
    /// time, real distance, interceptable like any other fleet action. See docs/material-vectors.md.
    ///
    /// <para>Runs at the manager cadence (10× brain rate). Per item per faction it services EVERY
    /// demanded node in one pass, not just the single largest shortfall — smallest-shortfall-first,
    /// against a shared surplus pool tracked across destinations. Smallest-first maximizes the
    /// count of demands cleared per tick and stops a handful of huge shortfalls from perpetually
    /// starving small, easily-satisfiable ones (e.g. a shipyard blocked on 2 units of a component
    /// while another node's shortfall of thousands eats all available surplus every tick).</para>
    ///
    /// <para>Anti-oscillation: <see cref="ShipyardManager"/> absorbs delivered components from
    /// <see cref="SpatialNode.Inventory"/> into <see cref="BuildQueueEntry.Collected"/> each tick,
    /// so routed items immediately leave the node's routable stock. OperationsManager sees an honest
    /// empty inventory and never routes those items away — no commitment state or floor math needed.
    /// The remaining gap — stock a source node's own build queue still needs but hasn't absorbed
    /// yet this tick — is closed separately via <see cref="SpatialNode.BuildQueueReserved"/>.
    /// </para>
    ///
    /// <para>Its own <see cref="Tick"/> only ever transfers between nodes owned by the same faction.
    /// The dispatch core underneath it is shared, though: <see cref="TradeManager"/> calls
    /// <c>DispatchVector</c> with a <see cref="VectorDispatchProfile"/> naming a different owner at
    /// each end, so a cross-faction trade flies the same tiers, load gate and consolidation with
    /// consent at the source and payment at the stop added. See docs/trade-haul-shaped.md.</para>
    /// </summary>
    /// <summary>Result of a player-triggered Mine dispatch (see
    /// <see cref="OperationsManager.TriggerPlayerMine"/>/<see cref="OperationsManager.TryFireMine"/>) —
    /// mirrors the <see cref="PlayerQueueResult"/> pattern so the host chat command maps to text
    /// without string-sniffing.</summary>
    public enum PlayerMineResult
    {
        Ok, SourceNotFound, SourceNotOwned, SourceNotMineable, DestNotFound, DestNotOwned, OnCooldown, InvalidOreWeights
    }

    /// <summary>Real outcome of a <see cref="OperationsManager.DispatchMine"/> attempt, once
    /// <see cref="PlayerMineResult"/>'s validation has already passed -- distinguishes "a real drill
    /// ship is now dwelling" from "no route-feasible ship existed anywhere, silently fell back to a
    /// <c>Ship:Mine</c> demand instead" (the same live-outcome distinction <see cref="HaulOutcome"/>
    /// already makes for Haul). Both a same-node dwell and a cross-node dwell (via Tier 1 or Tier 2
    /// pickup) count as <see cref="Dispatched"/> -- the player-facing message only needs "did a real
    /// ship start working this," not which tier. <see cref="NoShipRouteInfeasible"/> narrows
    /// <see cref="NoShipAvailable"/>: Mine-suitable stock exists at an alternate node but nothing
    /// survives the alt-to-source route gate (the same distinction HaulOutcome.RouteInfeasible draws)
    /// -- a Ship:Mine demand is staged in both cases.</summary>
    public enum MineDispatchOutcome { Dispatched, NoShipAvailable, NoShipRouteInfeasible, ClosedForCombat }

    public sealed partial class OperationsManager : IFactionManager
    {
        /// <summary>Why a matched <see cref="MaterialVector"/> did or didn't turn into a real Haul
        /// dispatch. NoShipAnywhere and RouteInfeasible both fall through to the same SHIP_DEMAND
        /// fallback today and were previously indistinguishable from the log alone.
        /// <para>NoGasTank: the vector's gas travels in tanks (<see cref="GasCarriage.InTanks"/>) and
        /// the faction owns no cargo hull with a tank for it, so no tier can carry it and none is
        /// tried.</para></summary>
        internal enum HaulOutcome { Dispatched, DispatchedViaPickup, NoShipAnywhere, RouteInfeasible, LoadCargoFailed, LoadTooLight, DestNotOwned, SourceNotOwned, Other, NoGasTank }

        /// <summary>Which ownership boundary a <see cref="MaterialVector"/> crosses, and what shape
        /// of convoy serves it. Every field the dispatch core reads that a cross-faction vector
        /// answers differently from an intra-faction one lives here, so <c>TradeManager</c> flies the
        /// same tiers, the same <see cref="AssessConvoy"/> gate and the same consolidation rather
        /// than a second copy of them. See docs/trade-haul-shaped.md.</summary>
        internal struct VectorDispatchProfile
        {
            /// <summary><see cref="ActionKind.Haul"/> or <see cref="ActionKind.Trade"/>; stamped on
            /// the <see cref="PendingFleetAction"/> and read by settlement.</summary>
            public ActionKind Kind;
            /// <summary>Who must own the vector's source node at dispatch. The re-check keeps its
            /// job of catching a node that changed hands since the match.</summary>
            public string SourceOwnerTag;
            /// <summary>Who must own the delivery node at dispatch.</summary>
            public string DeliverOwnerTag;
            /// <summary><see cref="PendingFleetAction.TargetTag"/>: the counterparty a Trade convoy
            /// settles with. Null for Haul, which has none.</summary>
            public string CounterpartyTag;
            /// <summary>Buyer-flies. The hull is never taken at the source (it belongs to the
            /// seller), and the itinerary's terminal stop is a <see cref="FleetStopKind.Return"/> at
            /// the delivery node rather than a <see cref="FleetStopKind.Service"/>.</summary>
            public bool IsProcurement;
            /// <summary>Seller-flies. Appends a terminal <see cref="FleetStopKind.Return"/> at the
            /// source node after the delivery, so the hull comes home instead of being credited to
            /// the counterparty's node.</summary>
            public bool ReturnsToSource;
            /// <summary>The <c>context</c> string on a local-shipyard investment request, so the
            /// request names the traffic that raised it.</summary>
            public string InvestmentReason;

            /// <summary>The intra-faction shape: both endpoints are <paramref name="factionTag"/>'s,
            /// nothing settles, and the hull is credited to the delivery node.</summary>
            internal static VectorDispatchProfile ForHaul(string factionTag)
            {
                return new VectorDispatchProfile
                {
                    Kind             = ActionKind.Haul,
                    SourceOwnerTag   = factionTag,
                    DeliverOwnerTag  = factionTag,
                    InvestmentReason = "haul",
                };
            }
        }

        /// <summary>What a dispatch attempt produced, for a caller that writes its own dispatch log
        /// line. <see cref="Tier"/> is 1 for a hull taken at the source, 2 for one fetched from
        /// elsewhere, 3 when only a <c>Ship:</c> demand was staged, and 0 when nothing happened.</summary>
        internal struct VectorDispatchInfo
        {
            public int    Tier;
            public string PrefabId;
            public long   LoadedQuantity;
            public string LogId;
            public string FirstLegNodeId;
        }

        // Derived from the posting cadence, not fixed: a ship demand has to outlive the gap to the
        // next sweep or the register is empty when the consumer looks. The x2 floor means one missed
        // sweep does not drop it.
        private static TimeSpan ShipDemandTtlFor(FactionTuning t)
        {
            return TimeSpan.FromSeconds(Math.Max(30.0, OpsIntervalOf(t) * 2.0));
        }

        private static double OpsIntervalOf(FactionTuning t)
        {
            if (t == null) return 10.0;
            return t.OpsIntervalSeconds > 0 ? t.OpsIntervalSeconds : 10.0;
        }

        private readonly IItemVolumeCatalogue _volumes;
        private readonly IItemMassCatalogue _masses;
        private OperationsTickTelemetry _lastTelemetry = new OperationsTickTelemetry();

        /// <summary>Latest completed synchronous tick. Replaced at every Tick entry so callers never
        /// observe counters carried forward from a no-demand or dormant-only pass.</summary>
        public OperationsTickTelemetry LastTelemetry { get { return _lastTelemetry; } }

        internal struct RouteKey : IEquatable<RouteKey>
        {
            private readonly string _origin;
            private readonly string _destination;
            private readonly double _minDryTwr;
            private readonly double _gravityFloor;
            public RouteKey(string origin, string destination, double minDryTwr, double gravityFloor)
            { _origin = origin; _destination = destination; _minDryTwr = minDryTwr; _gravityFloor = gravityFloor; }
            public bool Equals(RouteKey other)
            {
                return string.Equals(_origin, other._origin, StringComparison.Ordinal)
                    && string.Equals(_destination, other._destination, StringComparison.Ordinal)
                    && _minDryTwr.Equals(other._minDryTwr) && _gravityFloor.Equals(other._gravityFloor);
            }
            public override bool Equals(object obj) { return obj is RouteKey && Equals((RouteKey)obj); }
            public override int GetHashCode()
            {
                unchecked { int h = _origin == null ? 0 : StringComparer.Ordinal.GetHashCode(_origin); h = h * 397 ^ (_destination == null ? 0 : StringComparer.Ordinal.GetHashCode(_destination)); h = h * 397 ^ _minDryTwr.GetHashCode(); return h * 397 ^ _gravityFloor.GetHashCode(); }
            }
        }

        internal struct FallbackKey : IEquatable<FallbackKey>
        {
            public readonly string FactionTag, Role, BuildNodeId, DestinationNodeId;
            public FallbackKey(string factionTag, string role, string buildNodeId, string destinationNodeId)
            { FactionTag = factionTag; Role = role; BuildNodeId = buildNodeId; DestinationNodeId = destinationNodeId; }
            public bool Equals(FallbackKey other)
            {
                return string.Equals(FactionTag, other.FactionTag, StringComparison.Ordinal) && string.Equals(Role, other.Role, StringComparison.Ordinal)
                    && string.Equals(BuildNodeId, other.BuildNodeId, StringComparison.Ordinal) && string.Equals(DestinationNodeId, other.DestinationNodeId, StringComparison.Ordinal);
            }
            public override bool Equals(object obj) { return obj is FallbackKey && Equals((FallbackKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = FactionTag == null ? 0 : StringComparer.Ordinal.GetHashCode(FactionTag);
                    h = h * 397 ^ (Role == null ? 0 : StringComparer.Ordinal.GetHashCode(Role));
                    h = h * 397 ^ (BuildNodeId == null ? 0 : StringComparer.Ordinal.GetHashCode(BuildNodeId));
                    return h * 397 ^ (DestinationNodeId == null ? 0 : StringComparer.Ordinal.GetHashCode(DestinationNodeId));
                }
            }
        }

        /// <summary>Value side of <see cref="FactionTickContext.Fallbacks"/> -- MinCargoLiters plus
        /// the ViaNodeId of whichever post currently "wins" for this key (see <see cref="StageFallback"/>'s
        /// preserve-the-larger-cargo merge). ViaNodeId deliberately does NOT participate in
        /// <see cref="FallbackKey"/>'s identity: two vectors sharing the same (build, dest) but
        /// different real pickup sources must still collapse into ONE demand (ShipyardManager tracks
        /// only one BuildQueueEntry per node+role regardless), not fragment and lose the "always keep
        /// the largest cargo requirement" guarantee the dictionary-level merge already provides.</summary>
        internal struct FallbackValue
        {
            public double MinCargoLiters;
            public string ViaNodeId;
        }

        internal sealed class FactionTickContext
        {
            public readonly Dictionary<RouteKey, ShipPool.RouteEnvironment> Routes = new Dictionary<RouteKey, ShipPool.RouteEnvironment>();
            public readonly Dictionary<string, List<string>> AlternateSources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<SpatialNode>> DistanceOrders = new Dictionary<string, List<SpatialNode>>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> DestinationItemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Shipyards = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly HashSet<string> ShipyardsResolved = new HashSet<string>(StringComparer.Ordinal);
            /// <summary><see cref="MineTargeting.HardestInCatchment"/> per destination node id: the
            /// owned nodes and their gravity do not change within a pass.</summary>
            public readonly Dictionary<string, KeyValuePair<SpatialNode, double>> HardestGravityByAnchor =
                new Dictionary<string, KeyValuePair<SpatialNode, double>>(StringComparer.Ordinal);
            public readonly Dictionary<FallbackKey, FallbackValue> Fallbacks = new Dictionary<FallbackKey, FallbackValue>();
            public readonly List<FallbackKey> FallbackOrder = new List<FallbackKey>();

            /// <summary>Largest buildable Trade-role cargo capacity, resolved lazily once per faction
            /// tick (the catalogue cannot change mid-tick) so the Tier-3 clamp doesn't rescan the
            /// whole role pool per blocked vector. -1 = not yet resolved.</summary>
            public long LargestHaulCargoLiters = -1;

            /// <summary>Per gas key: whether the faction owns a cargo hull with a tank for it
            /// (<c>OwnsGasTank</c>), resolved once per pass. Ownership does not change within one.</summary>
            public readonly Dictionary<string, bool> GasTankOwnership = new Dictionary<string, bool>(StringComparer.Ordinal);

            // Real Haul convoys successfully dispatched THIS cycle, keyed (destNodeId, itemKey) —
            // cargo hasn't arrived yet (real transit time), so ctx.GetNodeInventory alone can't see
            // it. The one-shot player Mine's destination pick (ResolveMineDestination) nets this
            // against outstanding shortfall so a same-tick dispatch isn't treated as if it never
            // happened.
            public readonly Dictionary<(string NodeId, string ItemKey), long> DispatchedThisCycle
                = new Dictionary<(string, string), long>();

            public void RecordDispatchedThisCycle(string nodeId, string itemKey, long qty)
            {
                if (qty <= 0) return;
                var key = (nodeId, itemKey);
                long existing;
                DispatchedThisCycle.TryGetValue(key, out existing);
                DispatchedThisCycle[key] = existing + qty;
            }

            /// <summary>Units this faction's Mine trips from PRIOR cycles will deliver, keyed
            /// (delivery node, item): cargo already loaded on a trip whose dwell has resolved, plus the
            /// target of a dwell not yet resolved, split by its weights. Captured once, before any
            /// dispatch this pass (<see cref="IndexInFlightMine"/>), so it never overlaps
            /// <see cref="DispatchedThisCycle"/> -- this pass's own Mine dispatches are recorded there,
            /// under both the live context (which appends to PendingFleets at once) and the staged
            /// one (which does not).</summary>
            public readonly Dictionary<(string NodeId, string ItemKey), long> InFlightMineUnits
                = new Dictionary<(string, string), long>();

            /// <summary>Deposits a Mine trip is dwelling at or flying to dwell at -- from prior
            /// cycles (seeded by <see cref="IndexInFlightMine"/>) and from this pass's own
            /// dispatches. A deposit's cooldown is written only when its dwell RESOLVES, so without
            /// this set a deposit mid-dwell reads as free and a second trip stacks on it.</summary>
            public readonly HashSet<string> WorkedMineSources = new HashSet<string>(StringComparer.Ordinal);

            public long InFlightMineFor(string nodeId, string itemKey)
            {
                long units;
                return InFlightMineUnits.TryGetValue((nodeId, itemKey), out units) ? units : 0;
            }

            /// <summary>Extraction rate, litres per hour, of every Mine trip still to finish its
            /// dwell, keyed (delivery node, item): the trip's target volume over its dwell, split by
            /// its weights. Seeded by <see cref="IndexInFlightMine"/> from prior cycles and extended
            /// by <see cref="RecordMineDispatch"/> for this pass's own trips -- one dictionary, since
            /// the seed is read once before any dispatch. What an NPC extraction order's target rate
            /// is closed against (<see cref="ScanExtractionOrder"/>).</summary>
            public readonly Dictionary<(string NodeId, string ItemKey), double> MineRateLitersPerHour
                = new Dictionary<(string, string), double>();

            public double MineRateToward(string nodeId, string itemKey)
            {
                double rate;
                return MineRateLitersPerHour.TryGetValue((nodeId, itemKey), out rate) ? rate : 0;
            }

            public void AddMineRate(string nodeId, Dictionary<string, double> weights, double targetLiters,
                double dwellSeconds)
            {
                if (nodeId == null || weights == null || targetLiters <= 0 || dwellSeconds <= 0) return;
                double litersPerHour = targetLiters / dwellSeconds * 3600.0;
                foreach (KeyValuePair<string, double> kv in weights)
                {
                    if (kv.Value <= 0) continue;
                    var key = (nodeId, kv.Key);
                    double prior;
                    MineRateLitersPerHour.TryGetValue(key, out prior);
                    MineRateLitersPerHour[key] = prior + litersPerHour * kv.Value;
                }
            }
        }

        public OperationsManager(IItemVolumeCatalogue itemVolumes = null, IItemMassCatalogue itemMasses = null)
        {
            _volumes = itemVolumes;
            _masses = itemMasses;
        }

        public void Tick(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            _lastTelemetry = new OperationsTickTelemetry();
            FuelMarket.RefreshHolds(states, world, tuning);
            foreach (FactionState st in states)
            {
                // No IsAutonomyDisabled check -- self-sufficient (demand-routing driven), so goods
                // still get hauled between a Magnate-tier player faction's own nodes. See
                // docs/economy-territory.md -> "Player-faction claiming".
                if (FactionLifecycle.IsDormant(st, world.UtcNow)) { PublishUnservedDemands(st, null); continue; }
                var ctx = new LiveOperationsExecutionContext(world, st, log);
                TickOneFaction(st, ctx, world, tuning);
            }
        }

        /// <summary>One faction's whole Operations pass — snapshot/history-fold/claims-index/match/
        /// dispatch/fallback-publish — expressed entirely against <paramref name="ctx"/> so the exact
        /// same code runs whether <paramref name="ctx"/> is a <see cref="LiveOperationsExecutionContext"/>
        /// (this method's only caller today, via <see cref="Tick"/>) or a <see
        /// cref="StagedOperationsExecutionContext"/> (the resumable cycle in OperationsManager.Cycle.cs,
        /// Slice 3). <paramref name="world"/> is still passed through for read-only catalogue/graph
        /// access (<see cref="WorldModel.PrefabCatalogue"/>, <see cref="WorldModel.Planets"/>, node
        /// position/capability lookups) that Operations never stages.</summary>
        internal void TickOneFaction(FactionState st, IOperationsExecutionContext ctx, WorldModel world,
            FactionTuning tuning)
        {
            {
                // Wraps the WHOLE per-faction body, not just matching/dispatch — a prior version of
                // this telemetry only timed those two phases and completely missed the in-flight-Haul
                // lookup below, which live evidence (2026-08-08) showed was actually the dominant
                // cost. Total is what should be compared against bridge `sim perf`'s ops:_x figure.
                var factionStopwatch = System.Diagnostics.Stopwatch.StartNew();

                // LastTelemetry is a whole-Tick aggregate, while OPS_TICK_PERF is one line per
                // faction. Capture the aggregate baseline so MaybeLogOpsTickPerf's deltas stay local
                // to this faction's own items/demands/nodes/vectors, not the whole sweep so far.
                OpsPerfBaseline before = OpsPerfBaseline.Capture(_lastTelemetry);
                ResetRequirementPass();

                long preparationStart = System.Diagnostics.Stopwatch.GetTimestamp();
                List<DemandEntry> allDemands = ctx.SnapshotItemDemands(st.Tag);
                // A standing Mine order (see FactionState.StandingOrders) or an NPC extraction order
                // must still run even when this faction has zero live demand -- that's precisely the
                // gap it exists to close. Everything below this point degrades to cheap no-op passes
                // over an empty demand set when there's no demand and no order, preserving today's
                // fast early-out for the common case.
                var ownedNodes = new List<SpatialNode>();
                foreach (SpatialNode n in ctx.OwnedNodes(st.Tag))
                    ownedNodes.Add(n);

                // Above the early-out, not below it. Sales are credited by actors OUTSIDE this
                // faction -- a player at its store, another faction's convoy arriving -- so the node
                // that sold something is precisely the one that may carry no demand of its own, and
                // a decay behind the early-out would leave its target open forever.
                DecaySalesHistory(ownedNodes, DemandHistoryAlpha(tuning));

                // Planned before anything this pass writes -- FoldDemandHistory included -- so the
                // staged cycle, which defers its writes, plans from the same world. See PlanExtraction.
                List<StandingExtractionOrder> extractionPlan = PlanExtraction(st, ownedNodes, ctx, world, tuning);
                CommitExtractionPlan(st, extractionPlan, ctx.UtcNow, tuning, ctx.AddLog);
                List<StandingExtractionOrder> extractionOrders = DispatchableExtractionOrders(extractionPlan);

                if (allDemands.Count == 0 && st.StandingOrders.Count == 0 && extractionOrders.Count == 0)
                { PublishUnservedDemands(st, null); return; }

                _lastTelemetry.Factions++;
                _lastTelemetry.Demands += allDemands.Count;
                _lastTelemetry.OwnedNodes += ownedNodes.Count;
                var context = new FactionTickContext();
                _lastTelemetry.PreparationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - preparationStart;

                long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                FoldDemandHistory(ownedNodes, ctx, tuning);
                DecayFleetRequirements(st, tuning);
                _lastTelemetry.DemandHistoryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phaseStart;

                // Group demands by SubTypeId.
                var demandsByItem = new Dictionary<string, List<DemandEntry>>();
                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (DemandEntry demand in allDemands)
                {
                    string sub = demand.Kind.Substring(5); // "Item:X" → "X"
                    List<DemandEntry> bucket;
                    if (!demandsByItem.TryGetValue(sub, out bucket))
                    {
                        bucket = new List<DemandEntry>();
                        demandsByItem[sub] = bucket;
                    }
                    bucket.Add(demand);
                }
                // A full owned-node geometry order is only worth building when at least two item
                // groups can reuse it. For a destination demanded by one item, keep the old sparse
                // O(K log K) source sort (K = nodes with surplus) instead of sorting every owned
                // node in the common many-consumers/few-sources topology.
                foreach (List<DemandEntry> itemDemands in demandsByItem.Values)
                {
                    var destinations = new HashSet<string>(StringComparer.Ordinal);
                    foreach (DemandEntry demand in itemDemands)
                        if (demand.NodeId != null) destinations.Add(demand.NodeId);
                    foreach (string destinationNodeId in destinations)
                    {
                        int count;
                        context.DestinationItemCounts.TryGetValue(destinationNodeId, out count);
                        context.DestinationItemCounts[destinationNodeId] = count + 1;
                    }
                }
                _lastTelemetry.ItemGroups += demandsByItem.Count;
                _lastTelemetry.PreparationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phaseStart;

                // Perf telemetry only (docs/material-vectors.md's open question on whether a full
                // rescan-and-rematch every manager tick needs to become delta/dirty-tracking instead
                // of recomputed from scratch — this gives real numbers to decide that from, matching
                // this repo's established "measure against the live world before selecting an
                // optimization" convention rather than guessing at a cache up front).
                var matchStopwatch = System.Diagnostics.Stopwatch.StartNew();
                int dispatchCount = 0;
                long matchElapsedTicks = 0;
                long dispatchElapsedTicks = 0;

                // Outcome breakdown for the matched-but-not-dispatched gap: a faction can show a
                // healthy `vectors=`/dispatchMs (real matching work happening) while still dispatching
                // nothing at all if every match rejects at the same downstream gate. NoShipAnywhere
                // (FindNodeWithHaulStock found zero Haul-suitable stock anywhere in the network) and
                // RouteInfeasible (stock exists somewhere, but every route to it fails MinDryTwr —
                // e.g. an Ion-thruster hull that can't lift off a Surface-zone node) were previously
                // indistinguishable from the log alone; both fall through to the same SHIP_DEMAND
                // fallback. See docs/todo.md's live investigation of this gap, 2026-08-12.
                int dispatchedCount = 0, directDispatchCount = 0, noShipCount = 0, routeInfeasibleCount = 0, loadFailCount = 0, teleportedCount = 0;
                int notOwnedCount = 0;
                // Convoys the load gate refused (FAC-232). Counted beside the other reject reasons so
                // OPS_TICK_PERF shows whether the gate fires at all -- a gate that never fires and a
                // gate that was never wired in look identical from dispatch counts alone.
                int loadGatedCount = 0;

                // Bootstrap fallback: a faction that OWNS zero cargo-suitable hulls anywhere in its
                // network cannot dispatch a real Haul convoy — but it also can't ever BUILD its
                // first cargo ship without moving BOM components to its Shipyard node first. Real Haul
                // dispatch alone is a chicken-and-egg deadlock for a brand-new (or fleet-wiped)
                // faction, since unlike the pre-Slice-B instant transfer, it now has a ship
                // precondition the old path never had. Checked once per faction per tick (not
                // per-vector): self-terminating — the moment any cargo-suitable ship exists anywhere
                // (built using materials this very fallback delivered), real Haul dispatch takes over
                // and this stops firing on its own, no separate flag or expiry needed.
                //
                // Ownership, not availability — see NetworkOwnsSuitable. A hull away on an errand is
                // absent from ShipStock, so the availability reading treats a busy fleet as no fleet.
                bool hasCargoShipAnywhere = NetworkOwnsSuitable(ctx, st, ownedNodes, ActionKind.Haul, world.PrefabCatalogue);

                // Mine's counterpart of the same deadlock, on the same ownership reading and
                // computed the same once per faction per tick -- see RealizeMineBootstrapInstant.
                bool allowMineBootstrap = MineBootstrapAllowed(st, ownedNodes, ctx, world, tuning);

                // Precomputed ONCE per faction per tick: which (destination, item) pairs already
                // have a Haul convoy in flight. Live-measured 2026-08-08: the previous approach —
                // a fresh O(PendingFleets) scan per demand entry — dominated this method's real cost
                // far more than matching itself once the distance-sort fix landed. A busy faction's
                // `sim perf` ops:_x figure stayed ~35-40ms/call even after that fix cut matchMs to
                // ~2ms, because this scan reran per demand (thousands per tick) against hundreds of
                // concurrently in-flight convoys — O(demands × pendingFleets). One O(pendingFleets)
                // pass building this set, then an O(1) lookup per demand, is O(demands + pendingFleets).
                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var inFlightHaulPairs = new HashSet<(string NodeId, string ItemKey)>();
                IndexInFlightHaulPairs(st, inFlightHaulPairs);
                IndexInFlightMine(st, context);
                _lastTelemetry.ClaimIndexTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phaseStart;

                // Every item's vectors are matched before ANY of them dispatches, so a whole
                // faction's matches are visible at once — that is what lets vectors bound for the
                // same destination be consolidated onto one convoy across item boundaries, not just
                // across the sources of a single item. The staged cycle in OperationsManager.Cycle.cs
                // has always had this shape (match-all-then-dispatch-all); this pass now matches it,
                // which is also what keeps OperationsCycleEquivalenceTests honest.
                var pendingVectors = new List<MaterialVector>();
                var unservedDemands = new List<UnservedDemand>();
                Dictionary<(string NodeId, string ItemKey), long> inFlightQuantities = null;
                foreach (var itemEntry in demandsByItem)
                {
                    string subTypeId = itemEntry.Key;

                    // Exclude destinations already served by an in-flight Haul BEFORE matching, not
                    // after: matching a destination whose dispatch will just be discarded still
                    // "spends" its share of this tick's surplus snapshot, starving a different,
                    // genuinely-unserved destination competing for the same item this same tick.
                    List<DemandEntry> pending = new List<DemandEntry>();
                    List<DemandEntry> flying = null;
                    foreach (DemandEntry d in itemEntry.Value)
                    {
                        if (d.NodeId == null) continue;
                        if (!inFlightHaulPairs.Contains((d.NodeId, subTypeId))) pending.Add(d);
                        else (flying ?? (flying = new List<DemandEntry>())).Add(d);
                    }
                    if (flying != null)
                        PublishInFlightRemainders(st.Tag, subTypeId, flying,
                            inFlightQuantities ?? (inFlightQuantities = InFlightHaulQuantities(st)),
                            ctx, world, tuning, unservedDemands);

                    _lastTelemetry.Targets += pending.Count;

                    matchStopwatch.Restart();
                    List<MaterialVector> vectors = MatchVectors(st.Tag, subTypeId, pending, ownedNodes, ctx, world, tuning, context, unservedDemands);
                    matchElapsedTicks += matchStopwatch.ElapsedTicks;
                    _lastTelemetry.TargetMatchingTicks += matchStopwatch.ElapsedTicks;

                    pendingVectors.AddRange(vectors);
                    dispatchCount += vectors.Count;
                    _lastTelemetry.Vectors += vectors.Count;
                    ObserveFleetRequirement(st, vectors, tuning);
                }

                // NPC extraction orders take their drills BEFORE the Haul pass, the position the
                // retired Mine options held: a cargo hull is Haul-suitable on capacity alone, so a
                // Haul pass run first could spend the drill a starved hub's order was planned for.
                // The pass is shared with the player orders below, so claims stay pass-wide.
                var minePass = new StandingMineScanPass();
                foreach (StandingExtractionOrder order in extractionOrders)
                    ScanExtractionOrder(st, order, minePass, ownedNodes, ctx, world, tuning, context, allowMineBootstrap);

                SortByUrgency(pendingVectors);
                var pool = new HaulVectorPool(pendingVectors, tuning);
                matchStopwatch.Restart();

                for (int vi = 0; vi < pendingVectors.Count; )
                {
                    if (pool.IsConsumed(vi)) { vi++; continue; }
                    // Claimed the moment this pass takes it, not when it succeeds: a vector that has
                    // had its own convoy dispatched must never also be folded onto a LATER vector's
                    // convoy as an extra pickup. Companions() only skips indexes already consumed,
                    // and the loop's own cursor is not a claim -- it stops this vector being
                    // re-dispatched, but says nothing to a later vector looking backwards for
                    // company. A failed attempt is claimed too: the next cycle rematches it from
                    // live inventory anyway, and leaving it foldable would send a second convoy to
                    // stock the first one is already standing on. A refusal (LoadTooLight) is
                    // released again below: nothing flew, so a later convoy may carry it.
                    pool.Consume(vi);
                    if (hasCargoShipAnywhere)
                    {
                        long directStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        long routeBefore = _lastTelemetry.RoutePlanningTicks;
                        long alternateBefore = _lastTelemetry.AlternatePickupTicks;
                        long shipyardBefore = _lastTelemetry.ShipyardFallbackTicks;
                        HaulOutcome outcome = DispatchHaul(st, vi, pool, ctx, world, tuning, context);
                        long nested = (_lastTelemetry.RoutePlanningTicks - routeBefore)
                            + (_lastTelemetry.AlternatePickupTicks - alternateBefore)
                            + (_lastTelemetry.ShipyardFallbackTicks - shipyardBefore);
                        long directElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - directStart - nested;
                        if (directElapsed > 0) _lastTelemetry.DirectDispatchTicks += directElapsed;
                        switch (outcome)
                        {
                            case HaulOutcome.Dispatched:
                                directDispatchCount++;
                                dispatchedCount++;
                                break;
                            case HaulOutcome.DispatchedViaPickup:
                                dispatchedCount++;
                                break;
                            case HaulOutcome.NoShipAnywhere:  noShipCount++;          break;
                            case HaulOutcome.RouteInfeasible: routeInfeasibleCount++; break;
                            case HaulOutcome.LoadCargoFailed: loadFailCount++;        break;
                            case HaulOutcome.LoadTooLight:    loadGatedCount++; pool.Release(vi); break;
                            case HaulOutcome.DestNotOwned:
                            case HaulOutcome.SourceNotOwned:  notOwnedCount++;         break;
                        }
                    }
                    else { RealizeBootstrapInstant(pendingVectors[vi], ctx); teleportedCount++; }
                    vi++;
                }
                dispatchElapsedTicks += matchStopwatch.ElapsedTicks;

                _lastTelemetry.DirectDispatches += directDispatchCount;
                _lastTelemetry.NoShipOutcomes += noShipCount;
                _lastTelemetry.RouteInfeasibleOutcomes += routeInfeasibleCount;
                _lastTelemetry.LoadCargoFailures += loadFailCount;
                _lastTelemetry.NotOwnedOutcomes += notOwnedCount;

                // Rank order, and one shared pass across every order: two orders whose catchments
                // overlap must not both dispatch against the same deposit before either dwell
                // resolves (MineCooldownUntilUtc is written at dwell resolution, not at dispatch, so
                // the cooldown cannot serialize them within a tick). See StandingMineScanPass.
                foreach (StandingOrder order in st.StandingOrdersByRank(StandingOrderKind.Mine))
                {
                    if (order.Paused || world.UtcNow < order.NextScanUtc) continue;
                    ScanStandingMineOrder(st, order, minePass, ownedNodes, ctx, world, tuning, context);
                }

                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                PublishFallbacks(tuning, context, ctx);
                PublishUnservedDemands(st, unservedDemands);
                _lastTelemetry.FallbackPublicationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phaseStart;

                MaybeLogOpsTickPerf(ctx, st.Tag, before, demandsByItem.Count, allDemands.Count,
                    ownedNodes.Count, dispatchCount, inFlightHaulPairs.Count, dispatchedCount,
                    noShipCount, routeInfeasibleCount, loadFailCount, loadGatedCount, notOwnedCount, teleportedCount,
                    factionStopwatch.ElapsedTicks, matchElapsedTicks, dispatchElapsedTicks);
            }
        }

        /// <summary>Value snapshot of every sweep-cumulative <see cref="OperationsTickTelemetry"/>
        /// counter <see cref="MaybeLogOpsTickPerf"/> reports as a per-faction delta — captured once
        /// at the start of a faction's processing (whole-Tick body in <see cref="TickOneFaction"/>,
        /// cycle begin in <c>OperationsManager.Cycle.cs</c>) so the reported deltas stay local to that
        /// one faction rather than the whole sweep.</summary>
        private struct OpsPerfBaseline
        {
            public int RouteHits, RouteMisses, AlternateScans, ShipyardScans, SuitabilityChecks, FallbackCandidates, FallbackKeys, DemandPosts, DemandRefreshes;
            public int SpecialistHulls, DualRoleHulls;
            public long Preparation, DemandHistory, ClaimIndex, RoutePlanning, DirectDispatch, AlternatePickup, ShipyardFallback, FallbackPublication;
            public long ExtractionPlan, ExtractionWalk;
            public int ExtractionMineAttempts;

            public static OpsPerfBaseline Capture(OperationsTickTelemetry t)
            {
                return new OpsPerfBaseline
                {
                    RouteHits = t.RouteCacheHits, RouteMisses = t.RouteCacheMisses,
                    AlternateScans = t.AlternateSourceScans, ShipyardScans = t.ShipyardScans,
                    SuitabilityChecks = t.ShipSuitabilityChecks, FallbackCandidates = t.FallbackCandidates,
                    SpecialistHulls = t.SpecialistHulls, DualRoleHulls = t.DualRoleHulls,
                    FallbackKeys = t.UniqueFallbackKeys, DemandPosts = t.DemandPosts, DemandRefreshes = t.DemandRefreshes,
                    Preparation = t.PreparationTicks, DemandHistory = t.DemandHistoryTicks, ClaimIndex = t.ClaimIndexTicks,
                    RoutePlanning = t.RoutePlanningTicks, DirectDispatch = t.DirectDispatchTicks,
                    AlternatePickup = t.AlternatePickupTicks, ShipyardFallback = t.ShipyardFallbackTicks,
                    FallbackPublication = t.FallbackPublicationTicks,
                    ExtractionPlan = t.ExtractionPlanTicks, ExtractionWalk = t.ExtractionWalkTicks,
                    ExtractionMineAttempts = t.ExtractionMineAttempts,
                };
            }
        }

        /// <summary>Shared OPS_TICK_PERF line builder for both the live whole-Tick body
        /// (<see cref="TickOneFaction"/>) and the staged resumable cycle (<c>OperationsManager.Cycle.cs</c>,
        /// the only path the live host actually calls as of Slice 3 — see HANDOVER.md) — the same
        /// diagnostic line either way, logged via <paramref name="ctx"/> so a discarded/restarted
        /// staged cycle never emits one for work that didn't commit.</summary>
        private void MaybeLogOpsTickPerf(IOperationsExecutionContext ctx, string factionTag, OpsPerfBaseline before,
            int itemGroups, int demandsCount, int ownedNodesCount, int dispatchCount, int inFlightCount,
            int dispatchedCount, int noShipCount, int routeInfeasibleCount, int loadFailCount, int loadGatedCount,
            int notOwnedCount, int teleportedCount, long totalElapsedTicks, long matchElapsedTicks, long dispatchElapsedTicks)
        {
            double totalMs = TicksToMs(totalElapsedTicks);
            // A teleporting tick is the CHEAP one — it skips route planning, suitability checks and
            // dispatch entirely — so the elapsed-time threshold below selects against exactly the
            // ticks this counter exists to measure. FAC-199 measured that under-count at ~50x
            // (a whole day's proxy read 119 for MEMN; the direct window read 124 in three minutes).
            // Any bootstrap transfer therefore forces the line regardless of how fast it was.
            if (totalMs <= PerfLogThresholdMs && teleportedCount == 0) return;

            OperationsTickTelemetry t = _lastTelemetry;
            ctx.AddLog("OPS_TICK_PERF " + factionTag + " items=" + itemGroups
                    + " demands=" + demandsCount + " nodes=" + ownedNodesCount
                    + " vectors=" + dispatchCount + " inFlight=" + inFlightCount
                    + " dispatched=" + dispatchedCount + " teleported=" + teleportedCount
                    + " noShip=" + noShipCount
                    + " routeInfeasible=" + routeInfeasibleCount + " loadFail=" + loadFailCount
                    + " loadGated=" + loadGatedCount
                    + " notOwned=" + notOwnedCount
                    + " routeHits=" + (t.RouteCacheHits - before.RouteHits)
                    + " routeMisses=" + (t.RouteCacheMisses - before.RouteMisses)
                    + " alternateScans=" + (t.AlternateSourceScans - before.AlternateScans)
                    + " shipyardScans=" + (t.ShipyardScans - before.ShipyardScans)
                    + " suitabilityChecks=" + (t.ShipSuitabilityChecks - before.SuitabilityChecks)
                    + " fallbackCandidates=" + (t.FallbackCandidates - before.FallbackCandidates)
                    + " specialistHulls=" + (t.SpecialistHulls - before.SpecialistHulls)
                    + " dualRoleHulls=" + (t.DualRoleHulls - before.DualRoleHulls)
                    + " fallbackKeys=" + (t.UniqueFallbackKeys - before.FallbackKeys)
                    + " demandPosts=" + (t.DemandPosts - before.DemandPosts)
                    + " demandRefreshes=" + (t.DemandRefreshes - before.DemandRefreshes)
                    + " totalMs=" + totalMs.ToString("0.00")
                    + " matchMs=" + TicksToMs(matchElapsedTicks).ToString("0.00")
                    + " dispatchMs=" + TicksToMs(dispatchElapsedTicks).ToString("0.00")
                    + " prepMs=" + TicksToMs(t.PreparationTicks - before.Preparation).ToString("0.00")
                    + " historyMs=" + TicksToMs(t.DemandHistoryTicks - before.DemandHistory).ToString("0.00")
                    + " claimsMs=" + TicksToMs(t.ClaimIndexTicks - before.ClaimIndex).ToString("0.00")
                    + " routeMs=" + TicksToMs(t.RoutePlanningTicks - before.RoutePlanning).ToString("0.00")
                    + " directMs=" + TicksToMs(t.DirectDispatchTicks - before.DirectDispatch).ToString("0.00")
                    + " alternateMs=" + TicksToMs(t.AlternatePickupTicks - before.AlternatePickup).ToString("0.00")
                    + " shipyardMs=" + TicksToMs(t.ShipyardFallbackTicks - before.ShipyardFallback).ToString("0.00")
                    + " publishMs=" + TicksToMs(t.FallbackPublicationTicks - before.FallbackPublication).ToString("0.00")
                    + " planMs=" + TicksToMs(t.ExtractionPlanTicks - before.ExtractionPlan).ToString("0.00")
                    + " extractMs=" + TicksToMs(t.ExtractionWalkTicks - before.ExtractionWalk).ToString("0.00")
                    + " mineAttempts=" + (t.ExtractionMineAttempts - before.ExtractionMineAttempts));
        }

        /// <summary>A faction's tick cost above which <c>OPS_TICK_PERF</c> is logged even with nothing
        /// teleported: only worth a line once the cost could plausibly matter against the frame budget,
        /// so the common cheap tick writes nothing. Not a tuning field: a diagnostic threshold, not
        /// gameplay behaviour. Settable so a test can say what "cheap" and "slow" mean instead of
        /// inheriting the speed of the machine it runs on; the host never sets it.</summary>
        public double PerfLogThresholdMs { get; set; } = 1.0;

        private static double TicksToMs(long stopwatchTicks)
            => stopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Hygiene floor only -- key space is already bounded per-node by distinct items ever
        // delivered there; pure EMA decay never hits exact 0, so this just stops an infinite tail
        // of near-zero doubles. docs/material-vectors.md's DemandHistory addendum.
        private const double DemandHistoryPruneBelow = 0.5;

        /// <summary>Whether the faction <em>owns</em> a <paramref name="kind"/>-suitable hull
        /// anywhere — parked, reserved for store sale, or away on a mission — as opposed to
        /// <see cref="ShipPool.NetworkHasSuitable"/>'s "may one be dispatched this instant".
        ///
        /// <para>The two differ in exactly the cases that make the difference between a faction
        /// that cannot move goods and one that is merely busy: <see cref="SpatialNode.ShipStock"/>
        /// holds only <em>parked</em> hulls (<see cref="ShipPool.Consume"/> removes one at dispatch,
        /// <see cref="ShipPool.Return"/> credits it back on arrival) and
        /// <see cref="SpatialNode.ListedForSale"/> is subtracted on top. So a one-hull faction reads
        /// as fleetless for the whole duration of every errand that hull flies, and again for as
        /// long as it sits in a store listing.</para>
        ///
        /// <para>Only this reading may gate <see cref="RealizeBootstrapInstant"/>. On the
        /// availability reading the bootstrap is not a bootstrap at all: it makes logistics faster
        /// the fewer hulls you own, since a busy fleet is indistinguishable from no fleet.</para></summary>
        internal static bool NetworkOwnsSuitable(IOperationsExecutionContext ctx, FactionState st,
            List<SpatialNode> ownedNodes, ActionKind kind, IPrefabCatalogue cat)
        {
            foreach (SpatialNode n in ownedNodes)
                if (ctx.OwnsSuitableShip(n.Id, kind, cat)) return true;

            // Away on a mission: consumed out of ShipStock at dispatch and credited back by
            // ShipPool.Return on arrival, so between those two moments the hull exists only here.
            foreach (PendingFleetAction f in st.PendingFleets)
            {
                if (f.Resolved || f.Intercepted) continue;
                // "" is a free-built convoy with no pooled hull behind it — it returns nothing to
                // ShipStock either, so it is not a hull the faction can dispatch again.
                if (!string.IsNullOrEmpty(f.ShipPrefabId) && ShipPool.IsSuitableFor(kind, cat, f.ShipPrefabId))
                    return true;
                if (f.Squadron != null)
                    foreach (string prefabId in f.Squadron)
                        if (!string.IsNullOrEmpty(prefabId) && ShipPool.IsSuitableFor(kind, cat, prefabId))
                            return true;
            }
            return false;
        }

        /// <summary>Drains this pass's pending delivery credits (Haul arrivals, bootstrap instant
        /// transfers) into each owned node's <see cref="SpatialNode.DemandHistory"/> EMA, decaying
        /// every existing entry whether or not it was credited this pass -- same shape as
        /// <see cref="WorldModel"/>'s own <c>CentralBankRecentIncome</c> EMA fold, just per
        /// (node, item) instead of per faction.</summary>
        private static void FoldDemandHistory(List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx, FactionTuning tuning)
        {
            double alpha = DemandHistoryAlpha(tuning);
            foreach (SpatialNode n in ownedNodes)
            {
                // Non-destructive snapshot + explicit consume (rather than a single destructive
                // drain) so a staged cycle can defer the actual consumption to a guarded commit —
                // see IOperationsExecutionContext.ConsumeDemandHistoryCredit's contract. Live consumes
                // immediately after the snapshot, with nothing else able to run in between (Torch's
                // single-threaded manager tick), so this is exactly equivalent to the old one-shot drain.
                Dictionary<string, double> creditedThisPass = ctx.SnapshotPendingDemandHistory(n.Id);
                bool hasExisting = false;
                foreach (var kv in ctx.SnapshotDemandHistory(n.Id)) { hasExisting = true; break; }
                if (!hasExisting && (creditedThisPass == null || creditedThisPass.Count == 0)) continue;

                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in ctx.SnapshotDemandHistory(n.Id)) keys.Add(kv.Key);
                if (creditedThisPass != null) keys.UnionWith(creditedThisPass.Keys);
                foreach (string item in keys)
                {
                    double prior = ctx.GetDemandHistoryValue(n.Id, item);
                    double thisPass = 0;
                    if (creditedThisPass != null) creditedThisPass.TryGetValue(item, out thisPass);
                    double decayed = alpha * thisPass + (1.0 - alpha) * prior;
                    if (decayed < DemandHistoryPruneBelow) ctx.RemoveDemandHistory(n.Id, item);
                    else ctx.SetDemandHistory(n.Id, item, decayed);
                }
                ctx.ConsumeDemandHistoryCredit(n.Id, creditedThisPass);
            }
        }

        /// <summary>The EMA smoothing factor both histories age on, from one place so they cannot
        /// drift apart.</summary>
        private static double DemandHistoryAlpha(FactionTuning tuning)
        {
            double periods = tuning.DemandHistoryEmaPeriods > 0 ? tuning.DemandHistoryEmaPeriods : 1.0;
            return 2.0 / (periods + 1.0);
        }

        /// <summary>Ages <see cref="SpatialNode.SalesHistory"/> on the same period as the demand EMA
        /// beside it, so one tuning number describes the memory of both.
        /// <para>It is a <b>decaying accumulator</b>, not an EMA: sales are credited directly by
        /// <c>FleetResolution</c> and the host's store, neither of which runs inside the staged
        /// Operations cycle, so there is no pending-credit set to fold and nothing to separate
        /// "arrived this pass" from the running total. A steady rate therefore settles at
        /// <c>unitsPerPass / alpha</c>; <see cref="PriceOracle"/> multiplies by <c>alpha</c> on read
        /// so the value it ranks on is in units per pass, the same denomination as the demand
        /// term.</para></summary>
        private static void DecaySalesHistory(List<SpatialNode> ownedNodes, double alpha)
        {
            double keep = 1.0 - alpha;
            foreach (SpatialNode n in ownedNodes)
            {
                if (n.SalesHistory.Count == 0) continue;
                List<string> keys = null;
                foreach (var kv in n.SalesHistory)
                {
                    if (keys == null) keys = new List<string>(n.SalesHistory.Count);
                    keys.Add(kv.Key);
                }
                foreach (string key in keys)
                {
                    double decayed = n.SalesHistory[key] * keep;
                    if (decayed < DemandHistoryPruneBelow) n.SalesHistory.Remove(key);
                    else n.SalesHistory[key] = decayed;
                }
            }
        }

        /// <summary>Matches surplus to shortfall for one item across one faction's owned nodes —
        /// every node with an outstanding shortfall is a target this tick, not just the single
        /// largest one. Smallest-shortfall-first: cheap demands clear immediately instead of
        /// waiting behind whatever node currently has the biggest need, which — under
        /// winner-take-all — could starve them indefinitely. Pure match: does not mutate world
        /// state or log; callers realize the returned vectors.</summary>
        /// <summary>~10-minute-target neglect override for an inbound (destination) node — see
        /// docs/material-vectors.md's DemandHistory addendum. <c>default(DateTime)</c> (never
        /// touched) counts as immediately hot, not a "no data" case: every fresh node starts hot,
        /// which is also why every pre-existing test scenario (whose nodes never set this field)
        /// degenerates to today's exact ordering.</summary>
        private static bool IsInboundHot(string destNodeId, IOperationsExecutionContext ctx, FactionTuning tuning)
        {
            if (tuning.HaulNeglectThreshold <= TimeSpan.Zero) return false;
            DateTime lastArrived = ctx.GetLastHaulArrivedUtc(destNodeId);
            if (lastArrived == default(DateTime)) return true;
            return (ctx.UtcNow - lastArrived) >= tuning.HaulNeglectThreshold;
        }

        /// <summary>Which (destination, item) pairs this faction already has a real Haul convoy
        /// committed to, so matching skips them instead of dispatching a duplicate for cargo that is
        /// already on its way. Shared by the synchronous pass (<see cref="TickOneFaction"/>) and the
        /// staged cycle (<c>OperationsManager.Cycle.cs</c>) so the two cannot drift.
        /// <para>A convoy's claim is read from the stop it will actually DELIVER at, not from
        /// whatever leg it happens to be flying: a consolidated Haul spends most of its life en route
        /// to pickup nodes with its real destination sitting in <see cref="PendingFleetAction.RemainingStops"/>,
        /// and every item it has already loaded plus every item it is still scheduled to collect
        /// claims that destination. Reading <see cref="PendingFleetAction.TargetNodeId"/> instead
        /// would claim a pickup node it is merely passing through — and claim nothing at all for a
        /// convoy currently flying a coordinate-only clearance leg, whose TargetNodeId is null.</para></summary>
        private static void IndexInFlightHaulPairs(FactionState st, HashSet<(string NodeId, string ItemKey)> pairs)
        {
            IndexInFlightPairs(st, pairs, false);
        }

        /// <summary>Units the faction's own in-flight Hauls are carrying or will collect toward each
        /// (delivery node, item): the quantity behind the in-flight haul
        /// claims. A player contract claims a pair without a quantity, so it reads as covering all of
        /// it.</summary>
        internal static Dictionary<(string NodeId, string ItemKey), long> InFlightHaulQuantities(FactionState st)
        {
            var q = new Dictionary<(string NodeId, string ItemKey), long>();
            foreach (PendingFleetAction f in st.PendingFleets)
            {
                if (f.Resolved || f.Intercepted || f.Kind != ActionKind.Haul) continue;
                string node = DeliveryNodeOf(f);
                if (node == null) continue;
                if (f.Cargo != null)
                    foreach (KeyValuePair<string, long> kv in f.Cargo) AddQuantity(q, node, kv.Key, kv.Value);
                if (f.CurrentStopKind == FleetStopKind.Pickup && !string.IsNullOrEmpty(f.PickupItemKey))
                    AddQuantity(q, node, f.PickupItemKey, f.PickupQuantity);
                if (f.RemainingStops != null)
                    foreach (FleetStop stop in f.RemainingStops)
                        if (stop.Kind == FleetStopKind.Pickup && !string.IsNullOrEmpty(stop.PickupItemKey))
                            AddQuantity(q, node, stop.PickupItemKey, stop.PickupQuantity);
            }
            foreach (var claim in st.ExternalHaulClaims) q[claim] = long.MaxValue;
            return q;
        }

        private static void AddQuantity(Dictionary<(string NodeId, string ItemKey), long> q, string node, string item, long units)
        {
            if (units <= 0) return;
            long prior;
            q.TryGetValue((node, item), out prior);
            q[(node, item)] = prior == long.MaxValue || units > long.MaxValue - prior ? long.MaxValue : prior + units;
        }

        /// <summary>The rows <see cref="MatchVectors"/> never sees because the owner already has a
        /// haul in flight toward them. A steady trickle keeps a depot's row in that state almost all
        /// the time, so without this the under-half rule could never publish it. What is in flight
        /// counts as covered; under <see cref="FactionTuning.UnservedPublishCoverage"/> of the
        /// shortfall, the remainder is offered to partners as a partial row.</summary>
        private static void PublishInFlightRemainders(string factionTag, string subTypeId, List<DemandEntry> flying,
            Dictionary<(string NodeId, string ItemKey), long> inFlight, IOperationsExecutionContext ctx,
            WorldModel world, FactionTuning tuning, List<UnservedDemand> unserved)
        {
            if (unserved == null || tuning == null || tuning.UnservedPublishCoverage <= 0) return;
            foreach (DemandEntry demand in flying)
            {
                SpatialNode destNode;
                if (!world.Graph.Nodes.TryGetValue(demand.NodeId, out destNode) || !IsOwnedBy(destNode, factionTag)) continue;
                long have = ctx.GetNodeInventory(demand.NodeId, subTypeId);
                long shortfall = demand.Quantity - have;
                if (shortfall <= 0) continue;
                long covered;
                inFlight.TryGetValue((demand.NodeId, subTypeId), out covered);
                if (covered >= tuning.UnservedPublishCoverage * shortfall) continue;
                double history = ctx.GetDemandHistoryValue(demand.NodeId, subTypeId);
                unserved.Add(new UnservedDemand
                {
                    NodeId    = demand.NodeId,
                    ItemKey   = subTypeId,
                    Remaining = shortfall - covered,
                    Class     = demand.Class,
                    Urgency   = DemandUrgency.Score(demand, have, history, ctx.UtcNow, tuning, FuelDraw.ParkedHulls(destNode)),
                    Partial   = true,
                });
            }
        }

        /// <summary>Roster-wide companion of <see cref="IndexInFlightHaulPairs(FactionState, HashSet{ValueTuple{string, string}})"/>:
        /// the same (delivery node, item) claim set folded over every faction's fleets and including
        /// <see cref="ActionKind.Trade"/> convoys, because a row at one faction's node can be served
        /// by a convoy another faction is flying. One claim per row across the whole roster is what
        /// stops two factions serving it at once — see docs/trade-haul-shaped.md.</summary>
        internal static void IndexInFlightHaulPairs(IList<FactionState> roster, HashSet<(string NodeId, string ItemKey)> pairs)
        {
            if (roster == null) return;
            foreach (FactionState st in roster)
                if (st != null) IndexInFlightPairs(st, pairs, true);
        }

        /// <summary>The claims a partly served row respects: in-flight Trade convoys and player
        /// contracts, but not its owner's own Haul, which is what served the part.</summary>
        internal static void IndexInFlightTradePairs(IList<FactionState> roster, HashSet<(string NodeId, string ItemKey)> pairs)
        {
            if (roster == null) return;
            foreach (FactionState st in roster)
                if (st != null) IndexInFlightPairs(st, pairs, true, false);
        }

        private static void IndexInFlightPairs(FactionState st, HashSet<(string NodeId, string ItemKey)> pairs,
            bool includeTrade, bool includeHaul = true)
        {
            foreach (PendingFleetAction f in st.PendingFleets)
            {
                if (f.Resolved || f.Intercepted) continue;
                bool indexed = (includeHaul && f.Kind == ActionKind.Haul) || (includeTrade && f.Kind == ActionKind.Trade);
                if (!indexed) continue;

                string deliveryNodeId = DeliveryNodeOf(f);
                if (deliveryNodeId == null) continue;

                if (f.Cargo != null)
                    foreach (string itemKey in f.Cargo.Keys)
                        pairs.Add((deliveryNodeId, itemKey));
                if (f.CurrentStopKind == FleetStopKind.Pickup && !string.IsNullOrEmpty(f.PickupItemKey))
                    pairs.Add((deliveryNodeId, f.PickupItemKey));
                if (f.RemainingStops != null)
                    foreach (FleetStop stop in f.RemainingStops)
                        if (stop.Kind == FleetStopKind.Pickup && !string.IsNullOrEmpty(stop.PickupItemKey))
                            pairs.Add((deliveryNodeId, stop.PickupItemKey));
            }

            // An accepted player Acquisition/Haul contract claims its (node, item) pair the same
            // way — see docs/material-vectors.md's Slice C.
            foreach (var claim in st.ExternalHaulClaims)
                pairs.Add(claim);
        }

        /// <summary>Seeds <paramref name="context"/>'s Mine in-flight index from
        /// <paramref name="st"/>'s unresolved Mine fleets: what each will deliver where
        /// (<see cref="FactionTickContext.InFlightMineUnits"/>) and which deposit each is working or
        /// flying to work (<see cref="FactionTickContext.WorkedMineSources"/>). Called once per
        /// faction pass, before any dispatch, by both <see cref="TickOneFaction"/> and the staged
        /// cycle, for the same reason as <see cref="IndexInFlightHaulPairs(FactionState, HashSet{ValueTuple{string, string}})"/>.
        ///
        /// <para>A trip whose dwell has resolved carries its load in <see cref="PendingFleetAction.Cargo"/>
        /// and counts that. One still to dwell counts its target volume split by its weights -- the
        /// same arithmetic <c>FleetResolution</c>'s Mine stop mints with, at a completion ratio of 1.
        /// An interception that cuts the dwell short shows up as a smaller delivery and a shortfall
        /// on a later cycle; it is not anticipated here.</para></summary>
        private void IndexInFlightMine(FactionState st, FactionTickContext context)
        {
            foreach (PendingFleetAction f in st.PendingFleets)
            {
                if (f.Resolved || f.Intercepted || f.Kind != ActionKind.Mine) continue;

                string worked = null;
                Dictionary<string, double> weights = null;
                double targetLiters = 0;
                double dwellSeconds = 0;
                if (f.CurrentStopKind == FleetStopKind.Mine)
                {
                    worked = f.TargetNodeId;
                    weights = f.MineWeights;
                    targetLiters = f.MineTargetLiters;
                    // A dwell leg departs when the dwell starts and arrives when it ends.
                    dwellSeconds = (f.EstimatedArrivalUtc - f.DepartedUtc).TotalSeconds;
                }
                else if (f.RemainingStops != null)
                    foreach (FleetStop stop in f.RemainingStops)
                        if (stop.Kind == FleetStopKind.Mine)
                        {
                            worked = stop.NodeId;
                            weights = stop.MineWeights;
                            targetLiters = stop.MineTargetLiters;
                            dwellSeconds = stop.DurationSeconds;
                            break;
                        }
                if (worked != null) context.WorkedMineSources.Add(worked);

                string dest = DeliveryNodeOf(f);
                if (dest == null) continue;
                if (f.Cargo != null)
                    foreach (KeyValuePair<string, long> kv in f.Cargo)
                        AddQuantity(context.InFlightMineUnits, dest, kv.Key, kv.Value);
                if (weights != null && targetLiters > 0)
                {
                    foreach (KeyValuePair<string, long> kv in MineUnitsByItem(weights, targetLiters))
                        AddQuantity(context.InFlightMineUnits, dest, kv.Key, kv.Value);
                    context.AddMineRate(dest, weights, targetLiters, dwellSeconds);
                }
            }
        }

        /// <summary>A dwell's target volume in units per item -- <paramref name="targetLiters"/> split
        /// by <paramref name="weights"/>, as the Mine stop mints it. Items with no volume data mint
        /// nothing and are omitted.</summary>
        private IEnumerable<KeyValuePair<string, long>> MineUnitsByItem(Dictionary<string, double> weights,
            double targetLiters)
        {
            foreach (KeyValuePair<string, double> kv in weights)
            {
                double litersPerUnit = _volumes != null ? _volumes.VolumeLitersPerUnit(kv.Key) : 0;
                if (litersPerUnit <= 0) continue;
                long units = (long)(targetLiters * kv.Value / litersPerUnit);
                if (units > 0) yield return new KeyValuePair<string, long>(kv.Key, units);
            }
        }

        /// <summary>Books a Mine dispatch made THIS pass: its deposit is now worked, its target is
        /// committed toward <paramref name="destNodeId"/> the same way a same-pass Haul's cargo is
        /// (<see cref="FactionTickContext.RecordDispatchedThisCycle"/>), and its extraction rate joins
        /// <see cref="FactionTickContext.MineRateLitersPerHour"/>.</summary>
        private void RecordMineDispatch(FactionTickContext context, string sourceNodeId, string destNodeId,
            Dictionary<string, double> weights, double targetLiters, double dwellSeconds)
        {
            if (context == null) return;
            context.WorkedMineSources.Add(sourceNodeId);
            foreach (KeyValuePair<string, long> kv in MineUnitsByItem(weights, targetLiters))
                context.RecordDispatchedThisCycle(destNodeId, kv.Key, kv.Value);
            context.AddMineRate(destNodeId, weights, targetLiters, dwellSeconds);
        }

        /// <summary>The node <paramref name="f"/> will actually DELIVER at, which is what its claim
        /// is read from — not whatever leg it happens to be flying.
        /// <para>A Haul or a selling Trade convoy delivers at its <see cref="FleetStopKind.Service"/>
        /// stop: consolidated itineraries carry that stop in <see cref="PendingFleetAction.RemainingStops"/>
        /// while the fleet spends most of its life en route to pickup nodes, and a convoy on a
        /// coordinate-only clearance leg has no <see cref="PendingFleetAction.TargetNodeId"/> at all.
        /// A procurement convoy is the mirror image — it buys at a partner's node and delivers the
        /// goods home, so its delivery point is the itinerary's terminal
        /// <see cref="FleetStopKind.Return"/> stop.</para></summary>
        private static string DeliveryNodeOf(PendingFleetAction f)
        {
            if (f.IsProcurement)
            {
                if (f.RemainingStops != null)
                    for (int i = f.RemainingStops.Count - 1; i >= 0; i--)
                        if (f.RemainingStops[i].Kind == FleetStopKind.Return && f.RemainingStops[i].NodeId != null)
                            return f.RemainingStops[i].NodeId;
                return f.ReturnNodeId ?? f.OriginNodeId;
            }

            string deliveryNodeId = f.TargetNodeId;
            if (f.RemainingStops != null)
                foreach (FleetStop stop in f.RemainingStops)
                    if (stop.Kind == FleetStopKind.Service && stop.NodeId != null)
                    {
                        deliveryNodeId = stop.NodeId;
                        break;
                    }
            return deliveryNodeId;
        }

        /// <summary>Outbound (source) mirror of <see cref="IsInboundHot"/>.</summary>
        private static bool IsOutboundHot(string sourceNodeId, IOperationsExecutionContext ctx, FactionTuning tuning)
        {
            if (tuning.HaulNeglectThreshold <= TimeSpan.Zero) return false;
            DateTime lastDeparted = ctx.GetLastHaulDepartedUtc(sourceNodeId);
            if (lastDeparted == default(DateTime)) return true;
            return (ctx.UtcNow - lastDeparted) >= tuning.HaulNeglectThreshold;
        }

        /// <param name="unserved">Sink for the rows this pass found no owned source for at all —
        /// what a whitelisted partner may serve across the ownership boundary
        /// (docs/trade-haul-shaped.md). Appended to, never cleared, so one faction's whole pass
        /// accumulates across its item groups. Null skips the accounting entirely.</param>
        private List<MaterialVector> MatchVectors(string factionTag, string subTypeId,
            List<DemandEntry> demands, List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            FactionTickContext context, List<UnservedDemand> unserved)
        {
            var vectors = new List<MaterialVector>();

            // Hot/History precomputed ONCE per target here, never inside the sort comparator below —
            // this file already paid once for that exact mistake with DistanceTo (see the comment on
            // the candidates array further down); not repeating it for the new fields.
            var targets = new List<(string NodeId, long Shortfall, bool Hot, double History, double Urgency, DemandClass Class)>();
            // Every key but a fuel gas. Fuel gas is left to move as before: live 2026-10-10, holding
            // it back raised the share of fuel departures flown without from 8% to 11%, because two
            // nodes each keeping their own Fuel-class row no longer refilled whichever one departed
            // next (FAC-211). A gas the faction does not keep as fuel has no departure to starve, and
            // exempting it too left every node a source and a destination of the same oxygen: live
            // 2026-11-21, 73% of IGSL's 92,843 hauls were oxygen, 65% of its volume matched by a haul
            // the other way on the same pair.
            bool keepsFuel = FuelReserve.Keeps(world, factionTag, subTypeId, tuning);
            Dictionary<string, (long Quantity, DemandClass Class)> ownWant =
                !(InventoryKeyMapper.IsGasKey(subTypeId) && keepsFuel)
                    ? new Dictionary<string, (long Quantity, DemandClass Class)>(StringComparer.Ordinal) : null;
            // A fuel gas moves instead by gradient: between two nodes both short of their own rows it
            // flows from the fuller to the emptier, and never past the point that evens them out. The
            // emptier one is usually the node that just departed, so it is still refilled, and the
            // fuller one cannot be fed back what it gave. Live 2026-11-22: 60% of ISBT's 12.65 GL of
            // hydrogen in 14 h was matched by a haul the other way on the same pair.
            Dictionary<string, long> fuelRow =
                ownWant == null ? new Dictionary<string, long>(StringComparer.Ordinal) : null;
            Dictionary<string, long> movedThisPass =
                fuelRow != null ? new Dictionary<string, long>(StringComparer.Ordinal) : null;
            foreach (DemandEntry demand in demands)
            {
                if (demand.NodeId == null) continue;
                if (ownWant != null) RecordOwnWant(ownWant, demand);
                if (fuelRow != null && demand.Quantity > 0)
                {
                    long priorRow;
                    if (!fuelRow.TryGetValue(demand.NodeId, out priorRow) || demand.Quantity > priorRow)
                        fuelRow[demand.NodeId] = demand.Quantity;
                }
                SpatialNode destNode;
                if (!world.Graph.Nodes.TryGetValue(demand.NodeId, out destNode)) continue;
                // A demand outlives the ownership of the node that raised it -- its TTL is minutes
                // and hold decay is continuous. Matching one anyway allocates this tick's surplus to
                // a destination the dispatch guard will then refuse, starving destinations the
                // faction can actually serve for as long as the stale row lives.
                if (!IsOwnedBy(destNode, factionTag)) continue;

                long have = ctx.GetNodeInventory(demand.NodeId, subTypeId);
                long shortfall = demand.Quantity - have;
                if (shortfall <= 0) continue;

                double history = ctx.GetDemandHistoryValue(demand.NodeId, subTypeId);
                // Both of this scorer's live inputs are already in hand here, so ranking costs a
                // multiply rather than a second pass over the register.
                double urgency = DemandUrgency.Score(demand, have, history, ctx.UtcNow, tuning,
                                                     FuelDraw.ParkedHulls(destNode));
                targets.Add((demand.NodeId, shortfall, IsInboundHot(demand.NodeId, ctx, tuning), history, urgency, demand.Class));
            }

            if (targets.Count == 0) return vectors;
            // Staged: an inbound-neglected destination jumps the queue outright regardless of
            // shortfall size; within the same hot/cold tier, smallest-shortfall-first is the
            // unchanged baseline; on an exact shortfall tie, DemandHistory's gravity term (a
            // historically-hungrier consumer) breaks it. docs/material-vectors.md's DemandHistory
            // addendum.
            targets.Sort((a, b) =>
            {
                if (a.Hot != b.Hot) return a.Hot ? -1 : 1;
                int cmp = a.Shortfall.CompareTo(b.Shortfall);
                return cmp != 0 ? cmp : b.History.CompareTo(a.History);
            });

            // Surplus available per source node, snapshotted ONCE up front from pre-tick
            // inventory — not read lazily mid-loop. Otherwise a node serviced early as a
            // destination would show its just-delivered stock as live inventory and could
            // immediately get drained back out to feed a later, bigger destination in the
            // same pass (round-tripping units instead of routing pre-tick surplus).
            var remaining = new Dictionary<string, long>();
            var surplusNodes = new List<SpatialNode>();
            int surplusNodeCount = 0;
            // The part of a source's surplus its own row still wants, which only a destination of a
            // higher class may draw. Without it two nodes short of one key each read the other's
            // stock as surplus, and the row sizes decide which way it goes this cycle: live
            // 2026-10-08/09, a shipment came back within the hour 31.6% of the time when the
            // sender's own row jumped up after it shipped, against 5.7% when it did not (FAC-211).
            Dictionary<string, (long Reserve, DemandClass Class)> ownReserve =
                ownWant != null ? new Dictionary<string, (long Reserve, DemandClass Class)>(StringComparer.Ordinal) : null;
            foreach (SpatialNode n in ownedNodes)
            {
                long capped = SurplusOf(n, subTypeId, ctx, keepsFuel, tuning, world);
                remaining[n.Id] = capped;
                (long Quantity, DemandClass Class) want;
                if (ownReserve != null && capped > 0 && ownWant.TryGetValue(n.Id, out want))
                {
                    long reserve = OwnWantReserve(capped, ctx.GetNodeInventory(n.Id, subTypeId), want.Quantity);
                    if (reserve > 0) ownReserve[n.Id] = (reserve, want.Class);
                }
                if (capped > 0)
                {
                    surplusNodeCount++;
                    surplusNodes.Add(n);
                }
            }
            _lastTelemetry.CandidateSources += surplusNodeCount;

            foreach (var target in targets)
            {
                string destNodeId = target.NodeId;
                long   shortfall  = target.Shortfall;
                // HaulMaxBatchFraction < 1.0 caps how much of a shortfall one vector may carry,
                // biasing toward smaller/more frequent Haul trips for the "busy feel" goal — default
                // 1.0 (no cap) leaves today's full-shortfall-per-vector behavior unchanged.
                // docs/material-vectors.md's DemandHistory addendum.
                long cap = (tuning.HaulMaxBatchFraction > 0 && tuning.HaulMaxBatchFraction < 1.0)
                    ? Math.Max(1, (long)Math.Ceiling(shortfall * tuning.HaulMaxBatchFraction))
                    : shortfall;
                long   transferred = 0;

                SpatialNode destNode;
                world.Graph.Nodes.TryGetValue(destNodeId, out destNode); // present — targets only ever named a real node above
                Vec3 destPos = destNode.Position;

                // Nearest-source-first: once this vector becomes a real Haul convoy (Slice B), the
                // chosen source directly sets the convoy's transit distance/ETA/interception exposure
                // — draining whichever source happened to be added to the graph first (the old,
                // distance-blind order) could route a shortfall past a much closer surplus node.
                // Secondary key (node Id) makes the ordering deterministic on an exact-distance tie
                // rather than relying on an unstable-on-ties sort. Sorted from surplusNodes, not the
                // full ownedNodes — a faction's pure-consumer nodes (the common case: most nodes want
                // this item, few have any of it to spare) never need re-sorting once per demanded
                // destination, only the handful that could ever be a source.
                //
                // Distance is computed ONCE per (target, source) pair here, not inside the sort
                // comparator — live-measured 2026-08-08: a naive `List.Sort` comparator calling
                // `DistanceTo` (an allocation-free but sqrt-bearing call) on every comparison costs
                // O(k log k) sqrt calls per target instead of O(k); for a busy faction (~90 candidate
                // nodes, hundreds of targets per manager tick) that showed up live as a 226ms single
                // OperationsManager.Tick() call — long enough to stall a Torch Update() frame outright.
                // Hot precomputed here too, same discipline as Dist — an outbound-neglected source
                // (docs/material-vectors.md's DemandHistory addendum) can now win over a marginally
                // closer one that's shipping regularly; within the same tier, nearest-first is the
                // unchanged baseline.
                int destinationItemCount;
                context.DestinationItemCounts.TryGetValue(destNodeId, out destinationItemCount);
                List<SpatialNode> candidates = destinationItemCount > 1
                    ? GetDistanceOrder(context, destNodeId, destPos, ownedNodes)
                    : BuildDistanceOrder(destPos, surplusNodes);
                // A lane whose last dispatch paid for hostile space reads that cost as distance
                // (TransitLedger), so a cleaner source the cost would buy the extra distance to
                // comes first. A new list: the cached distance order is shared.
                if (world.TransitLedger.HasAny(factionTag))
                {
                    List<SpatialNode> priced = TransitPlanner.ReorderByLane(candidates, factionTag, destNodeId, destPos, world);
                    if (!ReferenceEquals(priced, candidates))
                    {
                        LogHaulRepick(ctx, factionTag, subTypeId, destNodeId, priced, candidates, remaining, world);
                        candidates = priced;
                    }
                }

                // Preserve the live semantics that a source dispatched earlier in this same tick
                // immediately becomes non-hot: the immutable distance order is cached, while the
                // hot/cold tier is evaluated on each pass instead of frozen into that cache.
                for (int hotPass = 0; hotPass < 2 && transferred < cap; hotPass++)
                {
                    bool wantHot = hotPass == 0;
                    foreach (SpatialNode src in candidates)
                    {
                        if (src.Id == destNodeId || IsOutboundHot(src.Id, ctx, tuning) != wantHot) continue;

                        long available = remaining[src.Id];
                        (long Reserve, DemandClass Class) reserved;
                        // Only a destination whose class outranks the source's own row may draw it.
                        if (ownReserve != null && ownReserve.TryGetValue(src.Id, out reserved) && target.Class <= reserved.Class)
                            available -= reserved.Reserve;
                        if (fuelRow != null)
                        {
                            long srcRow;
                            if (fuelRow.TryGetValue(src.Id, out srcRow))
                            {
                                long srcStock = ctx.GetNodeInventory(src.Id, subTypeId)
                                              - world.HaulPickupClaimed(src.Id, subTypeId) + MovedThisPass(movedThisPass, src.Id);
                                long destStock = ctx.GetNodeInventory(destNodeId, subTypeId) + MovedThisPass(movedThisPass, destNodeId);
                                long destRow;
                                fuelRow.TryGetValue(destNodeId, out destRow);
                                long even = FuelGradientAllowance(srcStock, srcRow, destStock, destRow);
                                if (even < available) available = even;
                            }
                        }
                        if (available <= 0) continue;

                        long xfer = available < (cap - transferred)
                            ? available
                            : (cap - transferred);
                        if (xfer <= 0) continue;

                        vectors.Add(new MaterialVector
                        {
                            FactionTag    = factionTag,
                            ItemKey       = subTypeId,
                            Quantity      = xfer,
                            SourceNodeId  = src.Id,
                            NeedNodeId    = destNodeId,
                            DeliverNodeId = destNodeId,
                            Shortfall     = shortfall,
                            Urgency       = target.Urgency,
                        });
                        remaining[src.Id] -= xfer;
                        transferred += xfer;
                        if (movedThisPass != null)
                        {
                            movedThisPass[src.Id] = MovedThisPass(movedThisPass, src.Id) - xfer;
                            movedThisPass[destNodeId] = MovedThisPass(movedThisPass, destNodeId) + xfer;
                        }

                        if (transferred >= cap) break;
                    }
                }

                // Unserved, and so offered to a partner, when the owner's own estate covers less than
                // UnservedPublishCoverage of the shortfall: nothing at all, or only a trickle. What is
                // offered is the remainder, so the partner never duplicates the owner's own haul.
                bool publish = transferred == 0
                    || (tuning.UnservedPublishCoverage > 0 && transferred < tuning.UnservedPublishCoverage * shortfall);
                if (unserved != null && publish && shortfall - transferred > 0)
                    unserved.Add(new UnservedDemand
                    {
                        NodeId    = destNodeId,
                        ItemKey   = subTypeId,
                        Remaining = shortfall - transferred,
                        Class     = target.Class,
                        Urgency   = target.Urgency,
                        Partial   = transferred > 0,
                    });
            }

            return vectors;
        }

        // "faction|dest|item" -> the last haul re-pick line, so it logs on change.
        private readonly Dictionary<string, string> _lastHaulRepick = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>One <c>TRANSIT_REPICK</c> line when the lane-priced order puts a different source with
        /// stock first than the plain distance order does, on change only.</summary>
        private void LogHaulRepick(IOperationsExecutionContext ctx, string factionTag, string itemKey, string destNodeId,
            List<SpatialNode> priced, List<SpatialNode> plain, Dictionary<string, long> remaining, WorldModel world)
        {
            SpatialNode chose = FirstStocked(priced, destNodeId, remaining);
            SpatialNode over = FirstStocked(plain, destNodeId, remaining);
            string key = factionTag + "|" + destNodeId + "|" + itemKey;
            if (chose == null || over == null || ReferenceEquals(chose, over))
            {
                _lastHaulRepick.Remove(key);
                return;
            }
            string signature = chose.Id + ">" + over.Id;
            string prior;
            if (_lastHaulRepick.TryGetValue(key, out prior) && prior == signature) return;
            _lastHaulRepick[key] = signature;
            SpatialNode dest;
            world.Graph.Nodes.TryGetValue(destNodeId, out dest);
            double overKm = dest != null ? over.Position.DistanceTo(dest.Position) : 0;
            double choseKm = dest != null ? chose.Position.DistanceTo(dest.Position) : 0;
            ctx.AddLog(TransitPlanner.FormatRepick(factionTag, itemKey, destNodeId, chose.Id, over.Id,
                world.TransitLedger.PenaltyKm(factionTag, over.Id, destNodeId, world.UtcNow), overKm, choseKm));
        }

        private static SpatialNode FirstStocked(List<SpatialNode> order, string destNodeId, Dictionary<string, long> remaining)
        {
            for (int i = 0; i < order.Count; i++)
            {
                long left;
                if (order[i].Id != destNodeId && remaining.TryGetValue(order[i].Id, out left) && left > 0) return order[i];
            }
            return null;
        }

        private static long MovedThisPass(Dictionary<string, long> moved, string nodeId)
        {
            long v;
            return moved != null && moved.TryGetValue(nodeId, out v) ? v : 0;
        }

        /// <summary>How much of a fuel gas a source short of its own row may send a destination: the
        /// amount that leaves both at the same share of their rows, and nothing when the source is
        /// already the emptier of the two. A source at or above its row is not limited, and neither
        /// is a destination without a row (a zero row reads as already full, so it gets nothing).</summary>
        public static long FuelGradientAllowance(long srcStock, long srcRow, long destStock, long destRow)
        {
            if (srcRow <= 0 || srcStock >= srcRow) return long.MaxValue;
            if (destRow <= 0 || srcStock <= 0) return 0;
            // (srcStock - x) / srcRow = (destStock + x) / destRow, solved for x.
            double even = ((double)srcStock * destRow - (double)destStock * srcRow) / ((double)srcRow + destRow);
            return even <= 0 ? 0 : (long)Math.Floor(even);
        }

        /// <summary>A node's own row for the key being matched: the largest quantity and the strongest
        /// class among its rows, in case more than one names it.</summary>
        private static void RecordOwnWant(Dictionary<string, (long Quantity, DemandClass Class)> ownWant, DemandEntry demand)
        {
            if (demand.Quantity <= 0) return;
            (long Quantity, DemandClass Class) prior;
            if (ownWant.TryGetValue(demand.NodeId, out prior))
                ownWant[demand.NodeId] = (Math.Max(prior.Quantity, demand.Quantity),
                                          demand.Class > prior.Class ? demand.Class : prior.Class);
            else
                ownWant[demand.NodeId] = (demand.Quantity, demand.Class);
        }

        /// <summary>How much of <paramref name="surplus"/> a source keeps for its own row. The row is
        /// gross, so what the node can spare is its stock above the row, and never more than the
        /// surplus the build reserve, keeps and holds already left. The two overlap (a fuel row and the
        /// fuel keep want the same tanks), which is why this is a min and not a further subtraction.</summary>
        public static long OwnWantReserve(long surplus, long stock, long ownRowQuantity)
        {
            if (surplus <= 0 || ownRowQuantity <= 0) return 0;
            long spare = stock - ownRowQuantity;
            if (spare < 0) spare = 0;
            return spare >= surplus ? 0 : surplus - spare;
        }

        /// <summary>Replaces <paramref name="st"/>'s published unserved set with this pass's rows.
        /// A dormant faction, one whose acquisition is switched off, and a pass that ended before
        /// matching all publish nothing — the list is cleared either way, so a stale set never
        /// outlives the pass that produced it.
        ///
        /// <para><b>This list is read by two parties, not one</b>, which is why the gate is
        /// acquisition rather than autonomy. <see cref="TradeManager"/>'s buy pass reads the
        /// faction's OWN rows to decide where to fly; its sell pass reads a PARTNER's rows to decide
        /// what to carry them. So a faction that publishes nothing is not merely passive — it is
        /// invisible, and no neighbour can discover it is short. For a player faction that has not
        /// opted in, that silence is the intent: the human supplies its cross-border decisions.
        /// Opting in to buying is what makes the shortage legible to everyone else.</para></summary>
        internal static void PublishUnservedDemands(FactionState st, List<UnservedDemand> unserved)
        {
            if (st == null) return;
            st.UnservedDemands.Clear();
            if (unserved == null || unserved.Count == 0 || !FactionLifecycle.TradeBuyEnabled(st)) return;
            st.UnservedDemands.AddRange(unserved);
        }

        /// <summary>Orders one faction's matched vectors for dispatch: most urgent first
        /// (<see cref="MaterialVector.Urgency"/>), ties keeping the order they were matched in.
        ///
        /// <para><b>Why this exists.</b> Vectors are matched one item at a time and appended, so the
        /// dispatch list arrived in <see cref="DemandRegister"/> insertion order -- the order items
        /// were first ever posted. That is not a priority, and where hulls are scarce it decided
        /// outright who got one: live 2026-08-31, ISBT owned a single hull across 70 nodes and
        /// logged 93 no_ship_anywhere blocks in a day while its shipyard sat one component short.</para>
        ///
        /// <para>Stable on ties, deliberately: <see cref="List{T}.Sort"/> is not, and dispatch order
        /// decides which vectors fold onto one convoy (<see cref="HaulVectorPool.Companions"/>), so
        /// an unstable tie-break would make identical input consolidate differently run to run.</para></summary>
        public static void SortByUrgency(List<MaterialVector> vectors)
        {
            if (vectors == null || vectors.Count < 2) return;

            var ordered = new List<(MaterialVector Vector, int Index)>(vectors.Count);
            for (int i = 0; i < vectors.Count; i++) ordered.Add((vectors[i], i));
            ordered.Sort((a, b) =>
            {
                int cmp = b.Vector.Urgency.CompareTo(a.Vector.Urgency);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });
            for (int i = 0; i < ordered.Count; i++) vectors[i] = ordered[i].Vector;
        }

        private List<SpatialNode> GetDistanceOrder(FactionTickContext context, string destinationNodeId,
            Vec3 destinationPosition, List<SpatialNode> ownedNodes)
        {
            List<SpatialNode> ordered;
            if (context.DistanceOrders.TryGetValue(destinationNodeId, out ordered)) return ordered;

            ordered = BuildDistanceOrder(destinationPosition, ownedNodes);
            context.DistanceOrders[destinationNodeId] = ordered;
            _lastTelemetry.DistanceOrderBuilds++;
            return ordered;
        }

        private static List<SpatialNode> BuildDistanceOrder(Vec3 destinationPosition, List<SpatialNode> nodes)
        {
            var candidates = new List<(SpatialNode Node, double DistanceSquared)>(nodes.Count);
            foreach (SpatialNode node in nodes)
            {
                double dx = node.Position.X - destinationPosition.X;
                double dy = node.Position.Y - destinationPosition.Y;
                double dz = node.Position.Z - destinationPosition.Z;
                candidates.Add((node, dx * dx + dy * dy + dz * dz));
            }
            candidates.Sort((a, b) =>
            {
                int cmp = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Node.Id, b.Node.Id);
            });
            var ordered = new List<SpatialNode>(candidates.Count);
            foreach (var candidate in candidates) ordered.Add(candidate.Node);
            return ordered;
        }

        /// <summary>A node's routable stock of an item: on-hand inventory minus whatever its own
        /// pending ship build has already claimed via <see cref="SpatialNode.BuildQueueReserved"/> —
        /// don't route a source's own uncollected BOM share away from under ShipyardManager's next
        /// absorption tick — and minus the fuel the node keeps for its own next departures
        /// (<see cref="FuelReserve.Keep"/>), and minus what fuel-key procurements hold there until
        /// they collect it (<see cref="WorldModel.ProcurementHeld"/>), and minus what in-flight Haul
        /// convoys are bound to collect there (<see cref="WorldModel.HaulPickupClaimed"/>, ranked below the
        /// holds: <see cref="FuelMarket.AfterHaulClaims"/>) — both only ever non-zero for a fuel key. The fuel keep
        /// and a Stock standing order's keep are one hold (<see cref="KeepOf"/>). Shared by <see cref="MatchVectors"/>, the
        /// single-target matchers below and the trade seller ledger, so the surplus definition can't
        /// drift between the batched and ad-hoc query paths.</summary>
        internal static long SurplusOf(SpatialNode n, string subTypeId, WorldModel world, FactionTuning tuning)
        {
            return SurplusOf(n, subTypeId, world, tuning, 0);
        }

        /// <summary><see cref="SurplusOf(SpatialNode, string, WorldModel, FactionTuning)"/> as the
        /// convoy holding <paramref name="heldForBuyer"/> of the node's procurement holds sees it: its
        /// own share is its to collect, so only the rest of the holds comes off, and the Haul claims
        /// come off only the stock above its share.</summary>
        internal static long SurplusOf(SpatialNode n, string subTypeId, WorldModel world, FactionTuning tuning,
            long heldForBuyer)
        {
            DateTime nowUtc = world.UtcNow;
            long stock;
            n.Inventory.TryGetValue(subTypeId, out stock);
            long available = stock - n.BuildQueueReserved(subTypeId, nowUtc)
                           - KeepOf(n, subTypeId, nowUtc, world, tuning);
            long held = world.ProcurementHeld(n.Id, subTypeId) - heldForBuyer;
            if (held > 0) available -= held;
            available = FuelMarket.AfterHaulClaims(available, heldForBuyer,
                world.HaulPickupClaimed(n.Id, subTypeId));
            return available > 0 ? available : 0;
        }

        /// <summary>Units of <paramref name="subTypeId"/> <paramref name="n"/> keeps back for its own
        /// use: the largest of the fuel keep (<see cref="FuelReserve.Keep"/>), the owner's Stock
        /// standing orders (<see cref="StockOrders.Keep"/>) and its workers' food
        /// (<see cref="WorkerFoodDemand.KeepUnits"/>).</summary>
        internal static long KeepOf(SpatialNode n, string subTypeId, DateTime nowUtc, WorldModel world, FactionTuning tuning)
        {
            return Math.Max(Math.Max(FuelReserve.Keep(n, subTypeId, nowUtc, world, tuning), StockOrders.Keep(n, subTypeId, world)),
                            WorkerFoodDemand.KeepUnits(n, subTypeId, tuning, world.Nutrition));
        }

        /// <summary>Execution-context-based counterpart of <see cref="SurplusOf(SpatialNode, string, WorldModel, FactionTuning)"/>
        /// for a staged Operations cycle — stock comes from <paramref name="ctx"/> (mutated within the
        /// cycle as earlier items/vectors dispatch), while <see cref="SpatialNode.BuildQueueReserved"/>
        /// stays a read-only live lookup (Operations never mutates BuildQueue). <paramref name="keepsFuel"/>
        /// is <see cref="FuelReserve.Keeps"/> for the owning faction and this key, asked once per
        /// item by the caller rather than once per node. The procurement holds and the Haul claims are
        /// read live off <paramref name="world"/>, like <see cref="SpatialNode.BuildQueueReserved"/>;
        /// a haul this cycle dispatches is not in the claims until the next rebuild, and the cycle's
        /// own <c>remaining</c> bookkeeping covers it until then.</summary>
        private static long SurplusOf(SpatialNode n, string subTypeId, IOperationsExecutionContext ctx,
            bool keepsFuel, FactionTuning tuning, WorldModel world)
        {
            long stock = ctx.GetNodeInventory(n.Id, subTypeId);
            long available = stock - n.BuildQueueReserved(subTypeId, ctx.UtcNow)
                           - Math.Max(Math.Max(keepsFuel ? FuelReserve.KeepAt(n, subTypeId, ctx.UtcNow, tuning) : 0,
                                               StockOrders.Keep(n, subTypeId, world)),
                                      WorkerFoodDemand.KeepUnits(n, subTypeId, tuning, world.Nutrition));
            available -= world.ProcurementHeld(n.Id, subTypeId);
            available = FuelMarket.AfterHaulClaims(available, 0, world.HaulPickupClaimed(n.Id, subTypeId));
            return available > 0 ? available : 0;
        }

        /// <summary>Dest-anchored, single-target version of <see cref="MatchVectors"/>: "does any
        /// owned node have surplus of <paramref name="subTypeId"/> that <paramref name="needNodeId"/>
        /// could draw on?" Used by contract generation (an ad-hoc query outside the batched manager
        /// tick, not a persisted match) to know whether a real vector backs a demand before offering a
        /// player contract for it. Same nearest-source-first, node-Id-tiebreak ordering as the batched
        /// matcher. Returns <c>false</c> (and a default vector) when no owned node anywhere has any
        /// surplus of the item.</summary>
        public static bool TryFindSource(string factionTag, string subTypeId, string needNodeId,
            long quantity, List<SpatialNode> ownedNodes, WorldModel world, out MaterialVector vector)
        {
            vector = default(MaterialVector);
            if (quantity <= 0) return false;

            SpatialNode destNode;
            if (!world.Graph.Nodes.TryGetValue(needNodeId, out destNode)) return false;
            Vec3 destPos = destNode.Position;

            SpatialNode best = null;
            long bestAvailable = 0;
            double bestDist = 0;
            foreach (SpatialNode n in ownedNodes)
            {
                if (n.Id == needNodeId) continue;
                long available = SurplusOf(n, subTypeId, world, world.Tuning);
                if (available <= 0) continue;

                double dist = n.Position.DistanceTo(destPos);
                if (best == null || dist < bestDist
                    || (dist == bestDist && string.CompareOrdinal(n.Id, best.Id) < 0))
                {
                    best = n;
                    bestAvailable = available;
                    bestDist = dist;
                }
            }
            if (best == null) return false;

            vector = new MaterialVector
            {
                FactionTag    = factionTag,
                ItemKey       = subTypeId,
                Quantity      = Math.Min(quantity, bestAvailable),
                SourceNodeId  = best.Id,
                NeedNodeId    = needNodeId,
                DeliverNodeId = needNodeId,
            };
            return true;
        }

        /// <summary>Source-anchored: "does <paramref name="sourceNodeId"/>'s own surplus match any
        /// outstanding demand elsewhere in the faction?" The mirror image of <see cref="TryFindSource"/>
        /// — used to generate a player Haul contract at a station that currently holds real surplus,
        /// rather than at the node with the shortfall. <paramref name="demands"/> should already be
        /// sorted by desirability (callers pass the same quantity-descending order the Acquisition
        /// contract path already applies) — the first entry this source can partially or fully cover
        /// wins; no scan for a "better" match beyond that, mirroring Acquisition's own first-eligible-
        /// candidate style. Returns <c>false</c> when this source has no surplus that matches any
        /// outstanding demand.</summary>
        public static bool TryFindDemand(string factionTag, string sourceNodeId,
            List<DemandEntry> demands, WorldModel world, out MaterialVector vector)
        {
            vector = default(MaterialVector);

            SpatialNode source;
            if (!world.Graph.Nodes.TryGetValue(sourceNodeId, out source)) return false;

            foreach (DemandEntry d in demands)
            {
                if (d.NodeId == null || string.Equals(d.NodeId, sourceNodeId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!d.Kind.StartsWith("Item:", StringComparison.Ordinal)) continue;
                string subTypeId = d.Kind.Substring(5);

                long surplus = SurplusOf(source, subTypeId, world, world.Tuning);
                if (surplus <= 0) continue;

                SpatialNode destNode;
                if (!world.Graph.Nodes.TryGetValue(d.NodeId, out destNode)) continue;
                long have;
                destNode.Inventory.TryGetValue(subTypeId, out have);
                long shortfall = d.Quantity - have;
                if (shortfall <= 0) continue;

                vector = new MaterialVector
                {
                    FactionTag    = factionTag,
                    ItemKey       = subTypeId,
                    Quantity      = Math.Min(surplus, shortfall),
                    SourceNodeId  = sourceNodeId,
                    NeedNodeId    = d.NodeId,
                    DeliverNodeId = d.NodeId,
                };
                return true;
            }
            return false;
        }

        /// <summary>Bootstrap-only realizer: today's pre-Haul instant, accounting-only transfer,
        /// used exclusively when <c>Tick</c> finds zero cargo-suitable ships anywhere in the
        /// faction's network (see the caller for why — breaks the chicken-and-egg deadlock a
        /// brand-new faction would otherwise hit). Distinctly tagged in the log
        /// (<c>ITEM_XFER_BOOTSTRAP</c>, not <c>HAUL_DISPATCHED</c>/the old <c>ITEM_XFER</c>) so live
        /// monitoring can tell bootstrap activity apart from normal operation and confirm it actually
        /// stops once the faction has a ship.</summary>
        private static void RealizeBootstrapInstant(MaterialVector v, IOperationsExecutionContext ctx)
        {
            ctx.AddNodeInventory(v.SourceNodeId, v.ItemKey, -v.Quantity);
            ctx.AddNodeInventory(v.DeliverNodeId, v.ItemKey, v.Quantity);

            // DemandHistory is NOT credited here (or on a real Haul arrival) -- it's credited on
            // actual consumption (ProductionManager's recipe-input use, ShipyardManager's BOM
            // absorption) instead of delivery, since a delivery-fed gravity term is a pure
            // self-reinforcing loop with no external anchor (docs/material-vectors.md's dated
            // correction note has the full account).

            ctx.AddLog(v.FactionTag + " ITEM_XFER_BOOTSTRAP " + v.ItemKey + " +" + v.Quantity
                    + " from=" + v.SourceNodeId + " to=" + v.DeliverNodeId
                    + " (no cargo-suitable ship in network yet)");
        }

        /// <summary>Per-(faction, item) last logged Haul block signature. Log-on-change dedup only:
        /// every ops cycle re-matches the same unmet demand, and an unchanged block must not re-log
        /// each time. Unstaged manager state -- a guard-discarded staged cycle can consume one
        /// transition (one suppressed re-log), never affect a dispatch. Bounded by faction x item,
        /// not by node, so it cannot grow with the graph.</summary>
        private readonly Dictionary<string, string> _lastHaulBlockSignature =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Same log-on-change discipline as <see cref="_lastHaulBlockSignature"/>, for the
        /// load gate. Keyed by faction x item x SOURCE rather than faction x item: the gate's
        /// decision is a property of which source is dribbling, and two sources of the same item can
        /// be refused for genuinely different reasons. Cleared by a successful dispatch of that
        /// (faction, item) so a later re-gate logs again.</summary>
        private readonly Dictionary<string, string> _lastLoadGateSignature =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Realizes a vector, then names the item when it did not turn into a dispatch.
        /// <see cref="HaulOutcome"/> is counted in aggregate on OPS_TICK_PERF, which says how many
        /// vectors were rejected but never which ones -- so a whole item class can go unserved
        /// indefinitely while the totals look busy. Mirrors <c>MINE_DISPATCH_BLOCKED</c>.</summary>
        private HaulOutcome DispatchHaul(FactionState st, int vectorIndex, HaulVectorPool pool,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            FactionTickContext context)
        {
            string factionTag = st.Tag;
            MaterialVector v = pool[vectorIndex];
            HaulAlternate alt;
            VectorDispatchInfo info;
            HaulOutcome outcome = DispatchHaulCore(st, vectorIndex, pool, ctx, world, tuning, context,
                VectorDispatchProfile.ForHaul(factionTag), out alt, out info);

            // A gated convoy has already written its own HAUL_LOAD_GATED naming the real reason, the
            // load and the hull. Falling through to the block logging below would report it a SECOND
            // time as reason=node_missing -- the default arm -- which is both a lie about why and an
            // inflation of the very count HAUL_BLOCKED is used to measure. Live 2026-09-02: 120
            // node_missing lines in one window against 0 in the pre-gate baseline.
            if (outcome == HaulOutcome.LoadTooLight) return outcome;

            string key = factionTag + "|" + v.ItemKey;
            if (outcome == HaulOutcome.Dispatched || outcome == HaulOutcome.DispatchedViaPickup)
            {
                // Clear on success (no log) so a later re-block with the same reason logs again
                // instead of being deduped across an intervening dispatch.
                _lastHaulBlockSignature[key] = "dispatched";
                _lastLoadGateSignature.Remove(key + "|" + v.SourceNodeId);
                return outcome;
            }

            // The signature is the reason CLASS alone. One unserved item routinely produces a
            // vector per (source, destination) pair every cycle, all failing the same way, so
            // folding the node ids into the signature makes every vector its own "change" and
            // logs all of them -- 9,580 lines in four minutes, live. The nodes stay in the
            // message as one worked example of the class.
            string reasonClass;
            switch (outcome)
            {
                case HaulOutcome.NoShipAnywhere:  reasonClass = "no_ship_anywhere";  break;
                case HaulOutcome.RouteInfeasible: reasonClass = "route_infeasible";  break;
                case HaulOutcome.LoadCargoFailed: reasonClass = "load_cargo_failed"; break;
                case HaulOutcome.DestNotOwned:    reasonClass = "dest_not_owned";    break;
                case HaulOutcome.SourceNotOwned:  reasonClass = "src_not_owned";     break;
                case HaulOutcome.NoGasTank:       reasonClass = "no_gas_tank";       break;
                default:                          reasonClass = "node_missing";      break;
            }

            string existing;
            if (_lastHaulBlockSignature.TryGetValue(key, out existing) && existing == reasonClass) return outcome;
            _lastHaulBlockSignature[key] = reasonClass;

            ctx.AddLog(factionTag + " HAUL_BLOCKED " + v.ItemKey + " x" + v.Quantity
                    + " src=" + v.SourceNodeId + " dest=" + v.DeliverNodeId
                    + " reason=" + reasonClass
                    + (outcome == HaulOutcome.RouteInfeasible ? DescribeHaulAlternate(alt, v.SourceNodeId, v.DeliverNodeId) : "")
                    + ((outcome == HaulOutcome.NoShipAnywhere || outcome == HaulOutcome.RouteInfeasible)
                       && !GasCarriage.InTanks(v.ItemKey)
                       ? " -- Ship:Trade demand staged" : ""));
            return outcome;
        }

        /// <summary>The alternate half of a route-infeasible block line: which donor was tried, which
        /// hull it refused, and the leg that actually failed. A fetched hull flies
        /// alt-&gt;source-&gt;dest, so either of the first two hops can be the one that refused it and
        /// the line names whichever it was -- printing a leg that was not what failed reads as an
        /// impossible claim, especially when two of the three nodes coincide.</summary>
        private static string DescribeHaulAlternate(HaulAlternate alt, string sourceNodeId, string destNodeId)
        {
            string node = alt.BlockedNodeId ?? alt.NodeId;
            if (node == null) return " alt=?";
            string leg = alt.BlockedOnDelivery && destNodeId != null
                ? sourceNodeId + "->" + destNodeId
                : node + "->" + sourceNodeId;
            return " alt=" + node
                + (alt.BlockedPrefabId != null ? " prefab=" + alt.BlockedPrefabId : "")
                + " leg=" + leg;
        }

        /// <summary>Realizes a vector as a real cargo convoy: consumes a cargo-suitable ship from
        /// the source node, loads it up to the vector's quantity (capped by cargo volume when known),
        /// and dispatches a one-way <see cref="ActionKind.Haul"/> fleet — real distance, real transit
        /// time, interceptable like Trade/Ferry/Patrol via the same <see cref="PendingFleetResolver"/>
        /// machinery. No ship available at this particular source (but the network has one elsewhere)
        /// → the vector is simply not realized this tick (mirrors <see cref="FleetManager"/> skipping
        /// a Position: demand when no source ship exists) — the caller's network-wide bootstrap check
        /// is the only fallback path; this method itself never falls back to an instant transfer.
        /// <para>Returns a <see cref="HaulOutcome"/> classifying why a match didn't turn into a real
        /// dispatch, so <see cref="Tick"/> can report a per-faction breakdown instead of only a raw
        /// match count that says nothing about the downstream reject reason.</para></summary>
        /// <summary>Cross-ownership entry point for the same core: <c>TradeManager</c> hands it a
        /// vector whose two endpoints belong to different factions, with the profile saying which is
        /// which. Everything downstream -- the three tiers, <see cref="AssessConvoy"/>, consolidation,
        /// the <c>Ship:</c> fallback -- is the code the Haul path runs, not a copy of it.
        /// See docs/trade-haul-shaped.md.</summary>
        internal HaulOutcome DispatchVector(FactionState st, int vectorIndex, HaulVectorPool pool,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            FactionTickContext context, VectorDispatchProfile profile, out VectorDispatchInfo info)
        {
            HaulAlternate alt;
            return DispatchHaulCore(st, vectorIndex, pool, ctx, world, tuning, context, profile, out alt, out info);
        }

        private HaulOutcome DispatchHaulCore(FactionState st, int vectorIndex, HaulVectorPool pool,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            FactionTickContext context, VectorDispatchProfile profile, out HaulAlternate alt,
            out VectorDispatchInfo info)
        {
            string factionTag = st.Tag;
            MaterialVector v = pool[vectorIndex];
            alt = new HaulAlternate();
            info = new VectorDispatchInfo();
            SpatialNode sourceNode, destNode;
            if (!world.Graph.Nodes.TryGetValue(v.SourceNodeId, out sourceNode)) return HaulOutcome.Other;
            if (!world.Graph.Nodes.TryGetValue(v.DeliverNodeId, out destNode)) return HaulOutcome.Other;

            // Last line, after MatchVectors' own ownership filter: a staged cycle spans frames, so a
            // node can go to UNKN between the match and the dispatch. Delivering to a node the
            // faction no longer owns hands the cargo AND the hull to the graveyard the grace sweep
            // empties. Loading from one would be taking someone else's stock. Each end is checked
            // against the owner the caller expects there, which for a cross-border vector is a
            // different faction at each end.
            if (!IsOwnedBy(destNode, profile.DeliverOwnerTag)) return HaulOutcome.DestNotOwned;
            if (!IsOwnedBy(sourceNode, profile.SourceOwnerTag)) return HaulOutcome.SourceNotOwned;

            // Gas in tanks: only a hull with a tank for the gas can carry it, so a faction owning
            // none has no tier to try. Refused here, before a hull is taken, rather than taking a
            // tankless one, loading nothing and handing it back every pass.
            bool gasInTanks = GasCarriage.InTanks(v.ItemKey);
            if (gasInTanks && !OwnsGasTank(context, st, v.ItemKey, ctx, world)) return HaulOutcome.NoGasTank;

            ShipPool.RouteEnvironment route = GetRoute(context, sourceNode, destNode, world, tuning);
            bool bulk = CarriesInBulk(profile, v, tuning);
            // The hull-home end: where a fetched hull may come from and where a built one belongs.
            // Buyer-flies loads at a node this faction does not own, so its hull can only come from
            // the delivery end.
            SpatialNode hullHomeNode = profile.IsProcurement ? destNode : sourceNode;
            string prefabId = null;
            // Tier 1 takes the hull at the loading node, which for buyer-flies is the seller's -- so
            // that tier does not exist in that direction and the convoy always flies out empty.
            int consumed = profile.IsProcurement
                ? 0
                : ctx.ConsumeShip(sourceNode.Id, ActionKind.Haul, world.PrefabCatalogue, 1, out prefabId, route,
                                  null, HullCostFor(pool, vectorIndex, sourceNode, sourceNode, destNode, world, tuning, bulk));
            if (consumed <= 0)
            {
                // A ship is needed to VISIT sourceNode (pick up cargo), not to be BUILT there --
                // most owned nodes are pure producers with no Shipyard and no reason to ever have
                // one (see docs/material-vectors.md's DemandHistory addendum, the N1853 finding).
                // Tier 2: a suitable ship exists elsewhere in the network -- dispatch it directly as
                // a single Haul convoy with a Pickup leg (fetch cargo at sourceNode) followed by the
                // real delivery, instead of posting a Position:Trade demand for FleetManager to
                // resolve as a separate Ferry fleet on a later tick (see docs/fleet-waypoints-plan.md).
                // Tier 3 (Shipyard build) is the fallback when tier 2 finds nothing route-feasible.
                long alternateStart = System.Diagnostics.Stopwatch.GetTimestamp();
                long alternateRouteBefore = _lastTelemetry.RoutePlanningTicks;
                string bulkDonor = bulk
                    ? FindBulkDonor(context, factionTag, sourceNode, destNode, route,
                        HullCostFor(pool, vectorIndex, sourceNode, sourceNode, destNode, world, tuning, true), ctx, world, tuning)
                    : null;
                alt = bulkDonor != null
                    ? new HaulAlternate { NodeId = bulkDonor }
                    : GetAlternateSource(context, factionTag, sourceNode.Id, destNode, ctx, world, tuning,
                        gasInTanks ? HullCostFor(pool, vectorIndex, sourceNode, sourceNode, destNode, world, tuning) : null);
                bool pickupLoadGated = false;
                bool dispatchedViaPickup = alt.NodeId != null
                    && TryDispatchHaulViaPickup(factionTag, vectorIndex, pool, sourceNode, destNode, alt.NodeId, route, ctx, world, tuning, context,
                        profile, out pickupLoadGated, out info);
                long alternateElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - alternateStart
                    - (_lastTelemetry.RoutePlanningTicks - alternateRouteBefore);
                if (alternateElapsed > 0) _lastTelemetry.AlternatePickupTicks += alternateElapsed;
                // A gated convoy is a decision not to fly, not a missing hull. Falling through here
                // would stage a Ship:Trade demand and report NoShipAnywhere -- building a replacement
                // for a hull we deliberately just handed back, and lying in the telemetry about why.
                if (pickupLoadGated) return HaulOutcome.LoadTooLight;
                if (!dispatchedViaPickup && gasInTanks)
                {
                    // A Ship:Trade demand names a hold size and no tank, so a hull built for it need
                    // not carry this gas; nothing is staged for a gas load. The faction owns a tanked
                    // hull (checked above), and this vector waits for one to come free.
                    info.Tier = 3;
                    return alt.NodeId == null && alt.BlockedNodeId == null
                        ? HaulOutcome.NoShipAnywhere : HaulOutcome.RouteInfeasible;
                }
                if (!dispatchedViaPickup)
                {
                    long shipyardStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    string shipyardNodeId = GetShipyard(context, factionTag, hullHomeNode.Id, world);

                    // Haul/Trade share the same cargo-suitability definition (ShipPool.IsSuitableFor),
                    // so this is the channel that actually produces a usable hull for this vector's
                    // route. minCargoLiters mirrors the volume math the success path below already
                    // does for the same v.ItemKey/v.Quantity, scaled by HaulShipDemandCargoShare (a
                    // hull is sized against the sustained flow, not against one catch-up shipment)
                    // and clamped to a size some hull can actually satisfy -- falls back to the
                    // legacy boolean-flag value (1) when volume data isn't available, rather than
                    // posting an unbounded demand.
                    double minCargoLiters = 1;
                    if (_volumes != null)
                    {
                        double litersPerUnit = _volumes.VolumeLitersPerUnit(v.ItemKey);
                        if (litersPerUnit > 0)
                            minCargoLiters = ClampToLargestHaulHull(
                                ShipDemandCargoFor(v.Quantity * litersPerUnit, tuning), context, world);
                    }
                    // sourceNode is the real pickup leg the built hull must ALSO survive reaching --
                    // shipyardNodeId is chosen as the nearest Shipyard to the PICKUP, not the final
                    // delivery, so when it happens to coincide with destNode (a common case: the
                    // shipyard is itself the material's own destination) the buildNode->destNode leg
                    // alone is trivially local and hides the real, possibly off-planet pickup leg.
                    StageFallback(context, factionTag, "Trade", shipyardNodeId ?? hullHomeNode.Id, destNode.Id, minCargoLiters, sourceNode.Id);
                    ShipPool.MaybePostLocalShipyardInvestment(ctx, tuning, st, sourceNode, destNode,
                        profile.InvestmentReason ?? "haul");
                    _lastTelemetry.ShipyardFallbackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - shipyardStart;
                    info.Tier = 3;
                    return alt.NodeId == null && alt.BlockedNodeId == null
                        ? HaulOutcome.NoShipAnywhere : HaulOutcome.RouteInfeasible;
                }
                return HaulOutcome.DispatchedViaPickup;
            }
            prefabId = prefabId ?? "";

            long cargoCapacityLiters = world.PrefabCatalogue != null && prefabId.Length > 0
                ? world.PrefabCatalogue.CargoLiters(prefabId) : 0;
            // The capacity a gas load is judged and logged against: the hull's tank room for it on
            // this leg. The hold for anything else.
            long gasCapLiters = gasInTanks
                ? GasCarriage.HullRoomLitres(world, tuning, prefabId, v.ItemKey, sourceNode.Position, destNode.Position)
                : 0;
            long judgedCapacityLiters = gasInTanks ? gasCapLiters : cargoCapacityLiters;

            // Other vectors matched this same pass for this same destination, whose sources sit
            // close enough to sourceNode to be worth collecting on the way. Chosen BEFORE the
            // primary load, because the first leg's mass/TWR ceiling is computed against wherever
            // this convoy actually flies next -- a pickup detour, not the destination. Nothing is
            // committed until the primary load succeeds below.
            List<HaulPickup> extras = SelectExtraPickups(vectorIndex, pool, sourceNode, destNode, prefabId,
                cargoCapacityLiters, v.Quantity, world, tuning, context);

            // Same-node extras never need a stop of their own: the convoy is standing at their
            // source already, so they load straight into the hold at dispatch alongside the primary.
            var remoteExtras = new List<HaulPickup>();
            var localExtras = new List<HaulPickup>();
            foreach (HaulPickup extra in extras)
            {
                if (string.Equals(extra.Vector.SourceNodeId, sourceNode.Id, StringComparison.Ordinal))
                    localExtras.Add(extra);
                else remoteExtras.Add(extra);
            }

            // Before LoadCargo, so a refusal costs a ReturnShip and never an inventory unwind.
            HaulViability viability = AssessConvoy(v, extras, judgedCapacityLiters, tuning,
                ConvoyFuelCredits(prefabId, sourceNode, sourceNode, extras, destNode, v, judgedCapacityLiters, world, tuning),
                CargoCredits(v, extras, destNode.Id, world));
            if (viability.Light)
            {
                // The hull standing here is too big for the job. A smaller one elsewhere fetches it,
                // urgent or not; urgency decides only what happens when there is none -- an urgent
                // convoy flies the hull it has, anything else waits for a convoy to fold it in or for
                // its urgency to rise.
                bool smallGated;
                VectorDispatchInfo smallInfo;
                string donorId = FindRightSizedDonor(context, factionTag, sourceNode, destNode, viability.PlannedLiters,
                                                     ctx, world, tuning,
                                                     gasInTanks ? HullCostFor(pool, vectorIndex, sourceNode, sourceNode, destNode, world, tuning) : null);
                if (donorId != null
                    && TryDispatchHaulViaPickup(factionTag, vectorIndex, pool, sourceNode, destNode, donorId, route, ctx,
                                                world, tuning, context, profile, out smallGated, out smallInfo, prefabId,
                                                RightSizedCapacity(viability.PlannedLiters, tuning)))
                {
                    ctx.ReturnShip(sourceNode.Id, prefabId, 1);
                    info = smallInfo;
                    return HaulOutcome.DispatchedViaPickup;
                }
            }
            if (!viability.WorthFlying)
            {
                ctx.ReturnShip(sourceNode.Id, prefabId, 1);
                LogLoadGated(ctx, factionTag, profile, v, viability, judgedCapacityLiters, prefabId, sourceNode.Id, destNode.Id);
                return HaulOutcome.LoadTooLight;
            }

            List<FleetStop> pickupStops = BuildPickupStops(remoteExtras);
            bool consolidated = pickupStops.Count > 0;
            // The first leg ends at the first pickup when there is one, and only otherwise at the
            // destination. Everything downstream -- ETA, mass ceiling, FleetId -- is derived from
            // that leg, not from the delivery.
            string firstLegNodeId = consolidated ? pickupStops[0].NodeId : destNode.Id;
            SpatialNode firstLegNode = consolidated ? remoteExtras[0].Node : destNode;

            double distKm = sourceNode.Position.DistanceTo(firstLegNode.Position);
            DateTime departed = ctx.UtcNow;
            DateTime arrival = tuning.FleetSpeedKmPerMin > 0
                ? departed.AddMinutes(tuning.TransitTimeMins(distKm))
                : departed;

            var haul = new PendingFleetAction
            {
                Kind                = profile.Kind,
                TargetTag           = profile.CounterpartyTag,
                ActorTag            = factionTag,
                OriginNodeId        = sourceNode.Id,
                TargetNodeId        = firstLegNodeId,
                OriginPosKm         = sourceNode.Position,
                TargetPosKm         = firstLegNode.Position,
                DepartedUtc         = departed,
                EstimatedArrivalUtc = arrival,
                CargoCapacityLiters = cargoCapacityLiters,
                ConvoySize          = 1,
                ShipPrefabId        = prefabId,
                FleetId             = ctx.MintFleetId(factionTag, departed, destNode.Id, v.SourceNodeId + ":" + v.ItemKey),
                LastResolvedSegment = -1,
            };
            if (consolidated)
            {
                haul.CurrentStopKind = FleetStopKind.Pickup;
                haul.PickupItemKey   = pickupStops[0].PickupItemKey;
                haul.PickupQuantity  = pickupStops[0].PickupQuantity;
                haul.RemainingStops  = new List<FleetStop>();
                for (int i = 1; i < pickupStops.Count; i++) haul.RemainingStops.Add(pickupStops[i]);
                haul.RemainingStops.Add(new FleetStop { NodeId = destNode.Id, Kind = FleetStopKind.Service });
            }
            // Seller-flies is not one-way. Without a terminal Return the convoy's whole terminal
            // happens at the counterparty's node: the hull is credited on the spot and whatever the
            // buyer refused is teleported back to the origin at the same instant. The Return stop
            // makes the trip home a real, interceptable leg, and lands the refused remainder where
            // the cargo actually came from rather than at whichever node the itinerary last departed.
            if (profile.ReturnsToSource)
            {
                if (haul.RemainingStops == null) haul.RemainingStops = new List<FleetStop>();
                haul.RemainingStops.Add(new FleetStop { NodeId = sourceNode.Id, Kind = FleetStopKind.Return });
            }

            bool massTrimmed;
            long qty = PendingFleetResolver.LoadCargo(ctx, sourceNode, ref haul, v.ItemKey, v.Quantity,
                world, tuning, _volumes, _masses, firstLegNode.Position, out massTrimmed);
            if (qty <= 0)
            {
                ctx.ReturnShip(sourceNode.Id, prefabId, 1); // nothing fits — give the ship back, dispatch nothing
                return HaulOutcome.LoadCargoFailed;
            }
            ctx.SetLastHaulDepartedUtc(sourceNode.Id, ctx.UtcNow);
            RecordHullSpecialisation(world, prefabId);

            // Local extras are only claimed for whatever actually fit -- LoadCargo re-caps against
            // the hold the primary just filled, so one that returns 0 is left in the pool to earn
            // its own convoy rather than being silently dropped.
            int localFolded = 0;
            long localFoldedQty = 0;
            foreach (HaulPickup extra in localExtras)
            {
                bool extraTrimmed;
                long extraQty = PendingFleetResolver.LoadCargo(ctx, sourceNode, ref haul, extra.Vector.ItemKey,
                    extra.Vector.Quantity, world, tuning, _volumes, _masses, firstLegNode.Position, out extraTrimmed);
                if (extraQty <= 0) continue;
                pool.Consume(extra.PoolIndex);
                context.RecordDispatchedThisCycle(destNode.Id, extra.Vector.ItemKey, extraQty);
                localFolded++;
                localFoldedQty += extraQty;
            }

            foreach (HaulPickup extra in remoteExtras)
            {
                pool.Consume(extra.PoolIndex);
                ctx.SetLastHaulDepartedUtc(extra.Vector.SourceNodeId, ctx.UtcNow);
                context.RecordDispatchedThisCycle(destNode.Id, extra.Vector.ItemKey, extra.Vector.Quantity);
            }
            _lastTelemetry.ConsolidatedPickups += localFolded + remoteExtras.Count;

            // LoadCargo fills the manifest and nothing else; the Pickup stops add their own loads
            // to this as they board.
            haul.CargoValue = PendingFleetResolver.CargoValueOf(haul.Cargo, world, world.PriceCatalogue);

            haul = PlanTransitAndLeg(haul, ctx, world, tuning, sourceNode.Id, destNode.Id, true);
            ctx.AppendPendingFleet(haul);

            // Read off the manifest, not off qty: after consolidation the hold can hold several
            // items, so only the manifest knows what is actually aboard.
            double loadLiters = CargoLitersOf(haul.Cargo);
            double queuedLiters = 0;
            for (int i = 0; i < remoteExtras.Count; i++)
                queuedLiters += LitersOf(remoteExtras[i].Vector.ItemKey, remoteExtras[i].Vector.Quantity);

            if (profile.Kind == ActionKind.Haul)
                ctx.AddLog(haul.LogId + " " + factionTag + " HAUL_DISPATCHED " + v.ItemKey + " x" + qty
                        + " " + sourceNode.Id + " -> " + destNode.Id
                        + " dist=" + distKm.ToString("0.0", CultureInfo.InvariantCulture) + "km"
                        + " eta=" + arrival.ToString("HH:mm:ss")
                        + (prefabId.Length > 0 ? " prefab=" + prefabId : "")
                        + (massTrimmed ? " cargo_mass_trimmed" : "")
                        + DescribeConsolidation(localFolded, localFoldedQty, remoteExtras)
                        + (gasInTanks
                           ? DescribeGasLoad(qty, qty, gasCapLiters)
                           : DescribeLoad(loadLiters, loadLiters + queuedLiters, cargoCapacityLiters))
                        + " urgency=" + v.Urgency.ToString("0.###", CultureInfo.InvariantCulture));
            context.RecordDispatchedThisCycle(destNode.Id, v.ItemKey, qty);
            info.Tier = 1;
            info.PrefabId = prefabId;
            info.LoadedQuantity = qty;
            info.LogId = haul.LogId;
            info.FirstLegNodeId = firstLegNodeId;
            return HaulOutcome.Dispatched;
        }

        /// <summary>The vectors one faction matched this pass, indexed by delivery node and flagged
        /// as they are consumed. A vector folded onto another convoy as an extra pickup must be
        /// skipped when the dispatch loop reaches it, or the same cargo is committed twice — once
        /// aboard the convoy that will collect it, once by a second convoy dispatched for it.</summary>
        internal sealed class HaulVectorPool
        {
            private readonly IList<MaterialVector> _vectors;
            private readonly bool[] _consumed;
            private readonly Dictionary<string, List<int>> _byDestination;

            internal HaulVectorPool(IList<MaterialVector> vectors, FactionTuning tuning)
            {
                _vectors = vectors;
                _consumed = new bool[vectors.Count];
                ConsolidationEnabled = tuning != null && tuning.HaulConsolidationRadiusKm > 0
                                       && tuning.HaulMaxPickupNodes > 1;
                // Only the grouping consolidation actually reads is built. Switched off, the pool
                // degenerates to a consumed-flag array over the same list, so the dispatch loop has
                // one shape whether the feature is on or not.
                if (!ConsolidationEnabled) return;
                _byDestination = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (int i = 0; i < vectors.Count; i++)
                {
                    string destinationNodeId = vectors[i].DeliverNodeId;
                    if (destinationNodeId == null) continue;
                    List<int> group;
                    if (!_byDestination.TryGetValue(destinationNodeId, out group))
                    {
                        group = new List<int>();
                        _byDestination[destinationNodeId] = group;
                    }
                    group.Add(i);
                }
            }

            internal bool ConsolidationEnabled { get; private set; }
            internal int Count { get { return _vectors.Count; } }
            internal MaterialVector this[int index] { get { return _vectors[index]; } }
            internal bool IsConsumed(int index) { return _consumed[index]; }
            internal void Consume(int index) { _consumed[index] = true; }
            /// <summary>Returns a vector the dispatcher took and refused, so a later convoy to the same
            /// destination this cycle can fold it in. Only for a refusal: a vector that flew, or failed
            /// for any other reason, stays claimed.</summary>
            internal void Release(int index) { _consumed[index] = false; }

            /// <summary>Still-unconsumed vectors bound for <paramref name="index"/>'s delivery node,
            /// itself excluded, in match order. Empty when consolidation is switched off.
            /// <para>"Unconsumed" is the whole guarantee, and it only holds because the dispatch loop
            /// consumes a vector when it takes it rather than only when it folds one. Otherwise this
            /// keeps returning vectors that already have a convoy of their own — they sit at lower
            /// indexes the loop has moved past, so nothing else would exclude them.</para></summary>
            internal List<int> Companions(int index)
            {
                var companions = new List<int>();
                if (!ConsolidationEnabled) return companions;
                string destinationNodeId = _vectors[index].DeliverNodeId;
                List<int> group;
                if (destinationNodeId == null || !_byDestination.TryGetValue(destinationNodeId, out group))
                    return companions;
                foreach (int i in group)
                    if (i != index && !_consumed[i]) companions.Add(i);
                return companions;
            }
        }

        /// <summary>One vector folded onto another vector's convoy, resolved against the graph once
        /// here so the route ordering below never re-looks-up a node.</summary>
        private struct HaulPickup
        {
            public int PoolIndex;
            public MaterialVector Vector;
            public SpatialNode Node;
        }

        /// <summary>Which of <paramref name="vectorIndex"/>'s same-destination companions this convoy
        /// will also collect: sources within <see cref="FactionTuning.HaulConsolidationRadiusKm"/> of
        /// <paramref name="loadingNode"/> — the node the convoy actually starts loading at, so the
        /// radius bounds the detour rather than describing a catchment — taken nearest-first while
        /// both the node budget (<see cref="FactionTuning.HaulMaxPickupNodes"/>) and the hold's
        /// remaining volume still allow, then ordered into the shortest route.
        /// <para>Volume is budgeted against each vector's REQUESTED quantity, the most it could ever
        /// load. The real load at each stop is re-capped against stock, remaining volume and the
        /// leg's mass ceiling when the convoy arrives (<see cref="PendingFleetResolver.LoadCargo"/>),
        /// so this plan can only over-reserve the hold, never overflow it. An item with no volume
        /// data is left out entirely rather than folded in unbudgeted.</para>
        /// <para>The hull is already chosen by the time this runs, and it was only ever gated on the
        /// source-to-destination regime. A detour is a leg it was never asked about, so each
        /// candidate is re-tested with <see cref="ShipPool.IsRouteFeasible"/> on both the hop out to
        /// it and its own hop to the destination — otherwise consolidation could route an
        /// orbit-only hull down to a Surface node it cannot lift off.</para>
        /// <para>Gas carried in tanks (<see cref="GasCarriage.InTanks"/>) takes no part: a gas primary
        /// collects nothing and a gas companion is never folded. Its capacity is the hull's tank room,
        /// not this hold budget, and the hull was chosen for that room alone.</para></summary>
        private List<HaulPickup> SelectExtraPickups(int vectorIndex, HaulVectorPool pool, SpatialNode loadingNode,
            SpatialNode destNode, string prefabId, long cargoCapacityLiters, long primaryQuantity,
            WorldModel world, FactionTuning tuning, FactionTickContext context)
        {
            var chosen = new List<HaulPickup>();
            if (!pool.ConsolidationEnabled || cargoCapacityLiters <= 0 || _volumes == null) return chosen;

            MaterialVector primary = pool[vectorIndex];
            if (GasCarriage.InTanks(primary.ItemKey)) return chosen;
            double primaryLitersPerUnit = _volumes.VolumeLitersPerUnit(primary.ItemKey);
            if (primaryLitersPerUnit <= 0) return chosen;
            double remainingLiters = cargoCapacityLiters - primaryQuantity * primaryLitersPerUnit;
            if (remainingLiters <= 0) return chosen;

            List<int> companions = pool.Companions(vectorIndex);
            if (companions.Count == 0) return chosen;

            // Nearest-first, so the cheapest detours claim the hold ahead of the costlier ones —
            // the same bias MatchVectors already applies when it picks a vector's source at all.
            // Ties break on source node then item so a hold that fills mid-list fills the same way
            // every run.
            var candidates = new List<(HaulPickup Pickup, double DistanceKm)>();
            foreach (int companionIndex in companions)
            {
                MaterialVector companion = pool[companionIndex];
                if (GasCarriage.InTanks(companion.ItemKey)) continue;
                SpatialNode companionNode;
                if (companion.SourceNodeId == null
                    || !world.Graph.Nodes.TryGetValue(companion.SourceNodeId, out companionNode)) continue;
                double distanceKm = loadingNode.Position.DistanceTo(companionNode.Position);
                if (distanceKm > tuning.HaulConsolidationRadiusKm) continue;
                _lastTelemetry.ShipSuitabilityChecks += 2;
                if (!ShipPool.IsRouteFeasible(world.PrefabCatalogue, prefabId,
                        GetRoute(context, loadingNode, companionNode, world, tuning))
                    || !ShipPool.IsRouteFeasible(world.PrefabCatalogue, prefabId,
                        GetRoute(context, companionNode, destNode, world, tuning))) continue;
                candidates.Add((new HaulPickup
                {
                    PoolIndex = companionIndex,
                    Vector = companion,
                    Node = companionNode,
                }, distanceKm));
            }
            if (candidates.Count == 0) return chosen;
            candidates.Sort((a, b) =>
            {
                int cmp = a.DistanceKm.CompareTo(b.DistanceKm);
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(a.Pickup.Vector.SourceNodeId, b.Pickup.Vector.SourceNodeId);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Pickup.Vector.ItemKey, b.Pickup.Vector.ItemKey);
            });

            var collectionNodes = new HashSet<string>(StringComparer.Ordinal) { loadingNode.Id };
            foreach (var candidate in candidates)
            {
                double litersPerUnit = _volumes.VolumeLitersPerUnit(candidate.Pickup.Vector.ItemKey);
                if (litersPerUnit <= 0) continue;
                double liters = candidate.Pickup.Vector.Quantity * litersPerUnit;
                if (liters > remainingLiters) continue;
                bool newNode = !collectionNodes.Contains(candidate.Pickup.Vector.SourceNodeId);
                if (newNode && collectionNodes.Count >= tuning.HaulMaxPickupNodes) continue;
                if (newNode) collectionNodes.Add(candidate.Pickup.Vector.SourceNodeId);
                remainingLiters -= liters;
                chosen.Add(candidate.Pickup);
            }
            return chosen.Count == 0 ? chosen : OrderPickupRoute(loadingNode, destNode.Position, chosen);
        }

        /// <summary>Orders a consolidated convoy's collection stops into the shortest route that
        /// leaves <paramref name="start"/>, visits every pickup node once and ends at
        /// <paramref name="destPos"/>: nearest-neighbour seeded, then 2-opt reversals. Pickups
        /// sharing a node stay adjacent so the node is routed once and its items load back to back,
        /// and pickups at <paramref name="start"/> itself lead — the convoy is standing there, they
        /// are not stops on any route.</summary>
        private static List<HaulPickup> OrderPickupRoute(SpatialNode start, Vec3 destPos, List<HaulPickup> picks)
        {
            var atStart = new List<HaulPickup>();
            var remoteNodeOrder = new List<SpatialNode>();
            var remoteByNode = new Dictionary<string, List<HaulPickup>>(StringComparer.Ordinal);
            foreach (HaulPickup pick in picks)
            {
                if (string.Equals(pick.Node.Id, start.Id, StringComparison.Ordinal)) { atStart.Add(pick); continue; }
                List<HaulPickup> bucket;
                if (!remoteByNode.TryGetValue(pick.Node.Id, out bucket))
                {
                    bucket = new List<HaulPickup>();
                    remoteByNode[pick.Node.Id] = bucket;
                    remoteNodeOrder.Add(pick.Node);
                }
                bucket.Add(pick);
            }

            var route = new List<SpatialNode>();
            var unvisited = new List<SpatialNode>(remoteNodeOrder);
            Vec3 at = start.Position;
            while (unvisited.Count > 0)
            {
                int best = 0;
                double bestDistance = at.DistanceTo(unvisited[0].Position);
                for (int i = 1; i < unvisited.Count; i++)
                {
                    double distance = at.DistanceTo(unvisited[i].Position);
                    if (distance < bestDistance
                        || (distance == bestDistance && string.CompareOrdinal(unvisited[i].Id, unvisited[best].Id) < 0))
                    {
                        best = i;
                        bestDistance = distance;
                    }
                }
                at = unvisited[best].Position;
                route.Add(unvisited[best]);
                unvisited.RemoveAt(best);
            }
            TwoOptPickupRoute(start.Position, destPos, route);

            var ordered = new List<HaulPickup>(picks.Count);
            ordered.AddRange(atStart);
            foreach (SpatialNode node in route) ordered.AddRange(remoteByNode[node.Id]);
            return ordered;
        }

        /// <summary>In-place 2-opt over <paramref name="route"/>: reverses the run between two stops
        /// whenever that shortens the whole <paramref name="startPos"/> -&gt; stops -&gt;
        /// <paramref name="destPos"/> path, until a full sweep finds no improvement. Only the two
        /// boundary edges change length under a reversal, which is what the comparison below prices.
        /// Strict-improvement-only and fixed scan order, so the result is deterministic; the sweep
        /// cap is a guard against a floating-point cycle, not an expected exit.</summary>
        private static void TwoOptPickupRoute(Vec3 startPos, Vec3 destPos, List<SpatialNode> route)
        {
            if (route.Count < 2) return;
            bool improved = true;
            for (int sweep = 0; improved && sweep < 16; sweep++)
            {
                improved = false;
                for (int i = 0; i < route.Count - 1; i++)
                    for (int j = i + 1; j < route.Count; j++)
                    {
                        Vec3 before = i == 0 ? startPos : route[i - 1].Position;
                        Vec3 after = j == route.Count - 1 ? destPos : route[j + 1].Position;
                        double current = before.DistanceTo(route[i].Position) + route[j].Position.DistanceTo(after);
                        double reversed = before.DistanceTo(route[j].Position) + route[i].Position.DistanceTo(after);
                        if (reversed >= current) continue;
                        route.Reverse(i, j - i + 1);
                        improved = true;
                    }
            }
        }

        private static List<FleetStop> BuildPickupStops(List<HaulPickup> picks)
        {
            var stops = new List<FleetStop>(picks.Count);
            foreach (HaulPickup pick in picks)
                stops.Add(new FleetStop
                {
                    NodeId         = pick.Vector.SourceNodeId,
                    Kind           = FleetStopKind.Pickup,
                    PickupItemKey  = pick.Vector.ItemKey,
                    PickupQuantity = pick.Vector.Quantity,
                });
            return stops;
        }

        /// <summary>Names a convoy the load gate refused. Log-on-change per (faction, item, source),
        /// the same discipline <c>HAUL_BLOCKED</c> uses and for the same reason: one unserved item
        /// produces a vector per (source, destination) every cycle, all refused identically, and
        /// logging each one buried a four-minute window in 9,580 lines once already.
        /// <para>Emitted because a gate that silently drops work is indistinguishable from a gate
        /// that never fires. The counts are the acceptance measure for docs/issues/FAC-232.</para>
        /// <para>A gas load in tanks (<see cref="GasCarriage.InTanks"/>) names its figures <c>gasL=</c> and
        /// <c>gasCapL=</c> in place of <c>loadL=</c> and <c>capL=</c>: the capacity is tank room, not
        /// the hold.</para></summary>
        private void LogLoadGated(IOperationsExecutionContext ctx, string factionTag, VectorDispatchProfile profile,
            MaterialVector v, HaulViability viability, long capacityLiters, string prefabId,
            string sourceNodeId, string destNodeId)
        {
            bool gasInTanks = GasCarriage.InTanks(v.ItemKey);
            string key = factionTag + "|" + v.ItemKey + "|" + sourceNodeId;
            string signature = "gated";
            string existing;
            if (_lastLoadGateSignature.TryGetValue(key, out existing) && existing == signature) return;
            _lastLoadGateSignature[key] = signature;

            CultureInfo inv = CultureInfo.InvariantCulture;
            string verb = profile.Kind == ActionKind.Trade ? " TRADE_LOAD_GATED " : " HAUL_LOAD_GATED ";
            ctx.AddLog(factionTag + verb + v.ItemKey + " x" + v.Quantity
                    + " src=" + sourceNodeId + " dest=" + destNodeId
                    + (gasInTanks ? " gasL=" : " loadL=") + viability.PlannedLiters.ToString("0.#", inv)
                    + (gasInTanks ? " gasCapL=" : " capL=") + capacityLiters
                    + " fill=" + viability.LoadFraction.ToString("0.###", inv)
                    + " closes=" + viability.ClosureFraction.ToString("0.###", inv)
                    + " urgency=" + v.Urgency.ToString("0.###", inv)
                    + " reason=" + (viability.FuelTooDear ? "fuel" : "light")
                    + (viability.FuelCredits > 0 ? " fuelCr=" + viability.FuelCredits.ToString("0", inv) : "")
                    + (viability.CargoCredits > 0 ? " cargoCr=" + viability.CargoCredits.ToString("0", inv) : "")
                    + " shortfall=" + v.Shortfall
                    + (string.IsNullOrEmpty(prefabId) ? "" : " prefab=" + prefabId));
        }

        /// <summary>Files what this pass's cargo jobs turned out to require, for
        /// docs/fleet-fitness-plan.md's Slice 0. **One observation per trip: each vector's own
        /// <see cref="MaterialVector.Quantity"/>, never what the convoy carried** — carried volume is
        /// censored by the hull that happened to be available, so a faction owning only small drones
        /// would record "small was enough" forever. A vector's quantity is set by
        /// <c>MatchVectors</c> from the source's surplus and the want before any hull is chosen, so it
        /// exists whatever the fleet looks like.
        ///
        /// <para>Per vector, not per want. A want served from three sources is three trips, and a
        /// hull is bought for one trip; the destination's whole shortfall was filed here before, and
        /// it pinned every faction's p90 in the top bucket (FAC-237). Gas is not filed: it travels in
        /// tanks, which the cargo floor does not size (<see cref="FleetFitness.TripLiters"/>).</para>
        ///
        /// <para>A vector that is re-matched on a later pass is a trip still wanted and is filed again,
        /// the same way the want was before. Within one pass it is filed once.</para></summary>
        private void ObserveFleetRequirement(FactionState st, List<MaterialVector> vectors, FactionTuning tuning)
        {
            if (st == null || vectors == null || vectors.Count == 0 || _volumes == null) return;
            if (tuning != null && tuning.FleetRequirementPercentile <= 0) return;

            RoleRequirement requirement = st.RequirementFor(HaulShipRole);
            for (int i = 0; i < vectors.Count; i++)
            {
                MaterialVector v = vectors[i];
                double liters = FleetFitness.TripLiters(v, _volumes);
                if (liters <= 0) continue;
                if (!_requirementSeenThisPass.Add(v.SourceNodeId + "|" + v.DeliverNodeId + "|" + v.ItemKey)) continue;
                requirement.Observe(liters);
            }
        }

        /// <summary>The role a blocked cargo vector actually posts a ship demand for -- Haul and Trade
        /// share one cargo-suitability definition and <c>StageFallback</c> posts "Trade", so the
        /// requirement has to be filed under the role a builder will later look up.</summary>
        private const string HaulShipRole = "Trade";

        /// <summary>Ages this faction's role requirements one Operations pass, on the same cadence
        /// <see cref="FoldDemandHistory"/> runs.
        /// <para>Without this the histogram accumulates forever and the requirement becomes the
        /// faction's whole history rather than its present — a fleet would still be sized by a
        /// traffic pattern that ended days ago, and any one-off regime change (a large stock release,
        /// a war) would contaminate it permanently instead of fading.</para>
        /// <para>Retain is derived from the EMA period so the histogram and
        /// <see cref="RoleRequirement.UnmetEma"/> share one memory length rather than drifting
        /// apart.</para></summary>
        private static void DecayFleetRequirements(FactionState st, FactionTuning tuning)
        {
            if (st == null || st.FleetRequirements.Count == 0) return;
            double periods = tuning != null ? tuning.FleetRequirementEmaPeriods : 0;
            if (periods <= 0) return;

            double retain = 1.0 - (2.0 / (periods + 1.0));
            if (retain <= 0 || retain >= 1.0) return;
            foreach (var kv in st.FleetRequirements)
                if (kv.Value != null) kv.Value.Decay(retain);
        }

        /// <summary>Clears the per-pass want dedup. Called at the start of each faction's pass on
        /// both the synchronous and staged paths.</summary>
        private void ResetRequirementPass() { _requirementSeenThisPass.Clear(); }

        /// <summary>Trips (source, destination, item) already counted this pass. Cleared per faction
        /// pass, so it bounds by live vectors rather than growing with the graph.</summary>
        private readonly HashSet<string> _requirementSeenThisPass = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Records how specialised the hull a cargo convoy just took is. See
        /// <see cref="OperationsTickTelemetry.SpecialistHulls"/> for why the outcome is counted rather
        /// than which of <c>PreferGeneralist</c>'s passes won.</summary>
        private void RecordHullSpecialisation(WorldModel world, string prefabId)
        {
            if (world == null || world.PrefabCatalogue == null || string.IsNullOrEmpty(prefabId)) return;
            int specialisms = ShipPool.SpecialistCapabilityCount(world.PrefabCatalogue, prefabId);
            if (specialisms <= 0) return;
            _lastTelemetry.SpecialistHulls++;
            if (specialisms > 1) _lastTelemetry.DualRoleHulls++;
        }

        /// <summary>Whether a planned convoy is worth the hull it is about to take, and why not when
        /// it is not.
        /// <para><b>This is the one place that question is asked.</b> Today it weighs the load against
        /// the hold and against the share of the want the trip closes; a fuel-consumption cost is the
        /// next term to sit beside them (docs/issues/FAC-038), which is why this is a named
        /// assessment returning a reason rather than a condition inlined at the dispatch site.</para>
        /// </summary>
        internal struct HaulViability
        {
            public bool   WorthFlying;
            /// <summary>The plan fills less than <see cref="FactionTuning.HaulMinLoadFraction"/> of a
            /// hull whose capacity and load are both known: worth a right-sized hull if one exists,
            /// whether or not urgency lets this one fly.</summary>
            public bool   Light;
            /// <summary>The plan's fuel would cost more than <see cref="FactionTuning.FuelValueCeiling"/>
            /// times its cargo value. Refused whatever the urgency.</summary>
            public bool   FuelTooDear;
            public double FuelCredits;      // -1 when the hull or the leg cannot be priced
            public double CargoCredits;     // 0 when no item in the plan has a price
            public double LoadFraction;     // planned litres / hull capacity
            public double ClosureFraction;  // planned units / the destination's shortfall
            public double PlannedLiters;
        }

        /// <summary>Prices the itinerary through hostile space (an escort when one is cheaper, see
        /// <see cref="TransitPlanner"/>), then plans the first leg, forwarding the transit lines to the
        /// operations log. Without the trespass catch this is <see cref="PendingFleetResolver.PlanFleetLeg"/>.</summary>
        private static PendingFleetAction PlanTransitAndLeg(PendingFleetAction fleet, IOperationsExecutionContext ctx,
            WorldModel world, FactionTuning tuning, string sourceNodeId, string deliverNodeId, bool mayEscort)
        {
            if (!TransitRisk.Applies(world))
                return PendingFleetResolver.PlanFleetLeg(fleet, world, tuning, false, null, ctx.FuelSink);
            if (mayEscort) TransitPlanner.ConsiderEscort(ref fleet, ctx, world, tuning, sourceNodeId, deliverNodeId);
            var transitLog = new List<string>();
            fleet = PendingFleetResolver.PlanFleetLeg(fleet, world, tuning, false, null, ctx.FuelSink, null, transitLog);
            for (int i = 0; i < transitLog.Count; i++) ctx.AddLog(transitLog[i]);
            return fleet;
        }

        /// <summary>Judges the convoy PLAN -- the primary vector plus everything consolidation folded
        /// onto it -- before any inventory moves, so a refusal costs only a
        /// <see cref="IOperationsExecutionContext.ReturnShip"/> and never has to unwind a load.
        ///
        /// <para>A convoy flies if it fills <see cref="FactionTuning.HaulMinLoadFraction"/> of the
        /// hold, OR if its demand is urgent enough not to wait (<see cref="MaterialVector.Urgency"/> at
        /// or above <see cref="FactionTuning.HaulHoldUrgency"/>). Either way a light convoy is flagged
        /// <see cref="HaulViability.Light"/> and the dispatcher first tries a right-sized hull from
        /// elsewhere; without one, an urgent convoy flies the hull it has and anything else waits for a
        /// convoy to fold it in. Urgency carries the demand's class, starvation
        /// and age, so a starving install or recipe input flies at once while a catalogue top-up
        /// waits, and a wait ends as the demand ages. See docs/issues/FAC-393.</para>
        ///
        /// <para>An unknown hull capacity or a missing volume reads as "cannot judge", which flies:
        /// the gate must never refuse on absent data.</para>
        ///
        /// <para><b>Gas in tanks</b> (<see cref="GasCarriage.InTanks"/>)
        /// takes the same rule against a different capacity: <paramref name="capacityLiters"/> is the
        /// hull's tank room for the gas on this leg — tanks less the leg's own burn, the litres the
        /// loaders can actually board — and the plan is the vector's units (gas units are litres),
        /// capped at that room because nothing beyond it boards. So a load is light when it fills less
        /// than <see cref="FactionTuning.HaulMinLoadFraction"/> of the <em>tank room</em>, never of the
        /// hold, which gas does not use; hull choice has already taken the smallest room that holds
        /// the load (<see cref="HaulHullCost.Fit"/>), so a light gas load is one the faction owns no
        /// smaller tank for, and it then waits for urgency or fetches a right-sized tank exactly as a
        /// light solid load does. A gas convoy carries no extras.</para></summary>
        private HaulViability AssessConvoy(MaterialVector primary, List<HaulPickup> extras,
            long capacityLiters, FactionTuning tuning, double fuelCredits, double cargoCredits)
        {
            bool gasInTanks = GasCarriage.InTanks(primary.ItemKey);
            var result = new HaulViability { WorthFlying = true, FuelCredits = fuelCredits, CargoCredits = cargoCredits };

            // Fuel against value, whatever the urgency: an urgent want is still not worth more fuel
            // than the goods are. Never on absent data -- an unpriced hull or cargo cannot be judged.
            result.FuelTooDear = tuning != null && tuning.FuelValueCeiling > 0
                && fuelCredits > 0 && cargoCredits > 0
                && fuelCredits > tuning.FuelValueCeiling * cargoCredits;

            double liters = gasInTanks
                ? (capacityLiters > 0 && capacityLiters < primary.Quantity ? capacityLiters : primary.Quantity)
                : LitersOf(primary.ItemKey, primary.Quantity);
            long units = primary.Quantity;
            if (extras != null)
                for (int i = 0; i < extras.Count; i++)
                {
                    liters += LitersOf(extras[i].Vector.ItemKey, extras[i].Vector.Quantity);
                    if (string.Equals(extras[i].Vector.ItemKey, primary.ItemKey, StringComparison.Ordinal)
                        && string.Equals(extras[i].Vector.DeliverNodeId, primary.DeliverNodeId, StringComparison.Ordinal))
                        units += extras[i].Vector.Quantity;   // same want, so it counts toward closing it
                }
            result.PlannedLiters = liters;
            result.WorthFlying = !result.FuelTooDear;

            if (tuning == null || tuning.HaulMinLoadFraction <= 0) return result;   // load gate disabled
            if (capacityLiters <= 0 || liters <= 0) return result;                  // cannot judge the load

            result.LoadFraction = liters / capacityLiters;
            if (result.LoadFraction >= tuning.HaulMinLoadFraction) return result;

            result.Light = true;
            result.ClosureFraction = primary.Shortfall > 0 ? (double)units / primary.Shortfall : 1.0;
            if (primary.Urgency < tuning.HaulHoldUrgency) result.WorthFlying = false;
            return result;
        }

        /// <summary>Litres one manifest occupies, using the same volume catalogue
        /// <see cref="PendingFleetResolver.LoadCargo"/> budgets the hold with. An item the catalogue
        /// does not know contributes 0 — the same reading every other volume call site here takes.
        /// <para>Summed from the manifest rather than from a unit count, because a consolidated
        /// convoy's hold is routinely MIXED: consolidation groups on destination, not on item, so
        /// units alone cannot be converted to litres at all.</para></summary>
        private double CargoLitersOf(Dictionary<string, long> cargo)
        {
            if (cargo == null || _volumes == null) return 0;
            double liters = 0;
            foreach (var kv in cargo) liters += _volumes.VolumeLitersPerUnit(kv.Key) * kv.Value;
            return liters;
        }

        private double LitersOf(string itemKey, long quantity)
        {
            return _volumes == null ? 0 : _volumes.VolumeLitersPerUnit(itemKey) * quantity;
        }

        /// <summary><see cref="DescribeLoad"/> for gas carried in tanks: <c>gasL=</c> is the gas aboard,
        /// <c>planL=</c> what the itinerary will board when that is more, and <c>gasCapL=</c> the hull's
        /// tank room for the gas on the loaded leg (<see cref="GasCarriage.HullRoomLitres"/>) — the
        /// figure the load was capped at. Gas units are litres.</summary>
        private static string DescribeGasLoad(long loadedLiters, long planLiters, long gasCapLiters)
        {
            return " gasL=" + loadedLiters
                 + (planLiters > loadedLiters ? " planL=" + planLiters : "")
                 + " gasCapL=" + gasCapLiters;
        }

        /// <summary>How full this convoy actually leaves, and how full its itinerary expects it to
        /// end up. Emitted because the dispatcher computes both the hull's capacity and the load's
        /// volume and has never compared them: a hull is chosen on route feasibility and cargo
        /// suitability alone, so a 3,375 L hauler will fly 38 L of gravel without anything noticing.
        /// See docs/issues/FAC-232.
        /// <para><paramref name="planLiters"/> exceeding <paramref name="capacityLiters"/> is not a
        /// defect: a pickup stop's share is re-capped against stock, remaining volume and the leg's
        /// mass ceiling on arrival, so the plan may legitimately over-reserve.</para></summary>
        private static string DescribeLoad(double loadLiters, double planLiters, long capacityLiters)
        {
            if (capacityLiters <= 0) return "";
            // Invariant, not the ambient culture: a comma decimal separator would both misread as
            // a field break and diverge between the box and the container.
            return " loadL=" + loadLiters.ToString("0.#", CultureInfo.InvariantCulture)
                 + (planLiters > loadLiters
                    ? " planL=" + planLiters.ToString("0.#", CultureInfo.InvariantCulture) : "")
                 + " capL=" + capacityLiters;
        }

        /// <summary>The consolidation half of a HAUL_DISPATCHED line: how many other vectors this
        /// convoy absorbed, how many of them cost it a stop rather than loading at the node it
        /// was already standing at, and how much they carry. Without it a merged dispatch is
        /// indistinguishable from a plain one and the vectors it absorbed just look like they went
        /// missing.
        /// <para><b>The two quantities are not the same kind of number and are not summed.</b>
        /// <paramref name="loadedAtOriginQty"/> is what <see cref="PendingFleetResolver.LoadCargo"/>
        /// actually put in the hold, already re-capped against stock, volume and the leg's mass
        /// ceiling. <paramref name="pickupStops"/> carry their REQUESTED quantity, which is re-capped
        /// the same way only when the convoy reaches each stop. Adding them would report a plan as
        /// though it were a load.</para>
        /// <para>The line's own <c>x&lt;qty&gt;</c> covers the primary vector alone, so without these
        /// a per-item unit total summed from this log undercounts every consolidated convoy by
        /// everything it folded.</para></summary>
        private static string DescribeConsolidation(int loadedAtOrigin, long loadedAtOriginQty,
            List<HaulPickup> pickupStops)
        {
            int merged = loadedAtOrigin + pickupStops.Count;
            if (merged == 0) return "";

            long queued = 0;
            for (int i = 0; i < pickupStops.Count; i++) queued += pickupStops[i].Vector.Quantity;

            return " consolidated=" + merged + " pickupStops=" + pickupStops.Count
                 + (loadedAtOriginQty > 0 ? " foldedQty=" + loadedAtOriginQty : "")
                 + (queued > 0 ? " queuedQty=" + queued : "");
        }

        /// <summary>Tier 2 of the ship-acquisition fallback: a cargo-suitable ship exists at
        /// <paramref name="altNodeId"/> elsewhere in the network. Consumes it there and dispatches
        /// one <see cref="ActionKind.Haul"/> convoy with a <see cref="FleetStopKind.Pickup"/> leg to
        /// <paramref name="sourceNode"/> (fetch the vector's cargo) followed by a queued delivery
        /// stop at <paramref name="destNode"/> — one convoy, one <c>FleetId</c>, from dispatch to
        /// delivery, instead of a separate Ferry fleet resolved on an earlier tick. Returns false
        /// (no fleet dispatched, caller falls through to the Shipyard-build last resort) when
        /// <paramref name="altNodeId"/>'s stock exists but isn't actually route-feasible — same
        /// any-vs-route-feasible distinction <see cref="FindNodeWithHaulStock"/> already draws for
        /// its own candidate search.
        /// <para><b>Both legs gate, not just the pickup.</b> This convoy flies alt→source→dest and
        /// <c>HAUL_SHIP_RETURNED</c> leaves the hull at <paramref name="destNode"/>, so a hull gated
        /// only on the empty hop out can be delivered somewhere it cannot lift off again — where it
        /// then fails the origin end of every future dispatch and blocks the node it is standing on.
        /// <paramref name="deliveryRoute"/> is the source→dest pair the direct tier already sampled,
        /// passed in rather than re-derived so both tiers ask one question of one route.</para></summary>
        private bool TryDispatchHaulViaPickup(string factionTag, int vectorIndex, HaulVectorPool pool, SpatialNode sourceNode,
            SpatialNode destNode, string altNodeId, ShipPool.RouteEnvironment deliveryRoute,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning, FactionTickContext context,
            VectorDispatchProfile profile, out bool loadGated, out VectorDispatchInfo info,
            string passedOverPrefabId = null, double maxCargoLiters = 0)
        {
            loadGated = false;
            info = new VectorDispatchInfo();
            MaterialVector v = pool[vectorIndex];
            SpatialNode altNode;
            if (!world.Graph.Nodes.TryGetValue(altNodeId, out altNode)) return false;

            ShipPool.RouteEnvironment pickupRoute = GetRoute(context, altNode, sourceNode, world, tuning);
            string prefabId;
            HaulHullCost cost = HullCostFor(pool, vectorIndex, altNode, sourceNode, destNode, world, tuning,
                CarriesInBulk(profile, v, tuning));
            if (cost != null) cost.MaxCargoLiters = maxCargoLiters;
            int consumed = ctx.ConsumeShip(altNode.Id, ActionKind.Haul, world.PrefabCatalogue, 1, out prefabId,
                pickupRoute, deliveryRoute, cost);
            if (consumed <= 0) return false;
            prefabId = prefabId ?? "";

            long cargoCapacityLiters = world.PrefabCatalogue != null && prefabId.Length > 0
                ? world.PrefabCatalogue.CargoLiters(prefabId) : 0;
            // A gas load is judged against the fetched hull's tank room on the loaded leg, and plans
            // only what that room takes: the rest of the vector stays unserved for another convoy.
            bool gasInTanks = GasCarriage.InTanks(v.ItemKey);
            long gasCapLiters = gasInTanks
                ? GasCarriage.HullRoomLitres(world, tuning, prefabId, v.ItemKey, sourceNode.Position, destNode.Position)
                : 0;
            long judgedCapacityLiters = gasInTanks ? gasCapLiters : cargoCapacityLiters;
            long plannedQuantity = gasInTanks && gasCapLiters < v.Quantity ? gasCapLiters : v.Quantity;

            double distKm = altNode.Position.DistanceTo(sourceNode.Position);
            DateTime departed = ctx.UtcNow;
            DateTime arrival = tuning.FleetSpeedKmPerMin > 0
                ? departed.AddMinutes(tuning.TransitTimeMins(distKm))
                : departed;

            var haul = new PendingFleetAction
            {
                Kind                = profile.Kind,
                TargetTag           = profile.CounterpartyTag,
                IsProcurement       = profile.IsProcurement,
                ActorTag            = factionTag,
                OriginNodeId        = altNode.Id,
                TargetNodeId        = sourceNode.Id,
                OriginPosKm         = altNode.Position,
                TargetPosKm         = sourceNode.Position,
                DepartedUtc         = departed,
                EstimatedArrivalUtc = arrival,
                CargoCapacityLiters = cargoCapacityLiters,
                ConvoySize          = 1,
                ShipPrefabId        = prefabId,
                CurrentStopKind     = FleetStopKind.Pickup,
                PickupItemKey       = v.ItemKey,
                PickupQuantity      = plannedQuantity,
                FleetId             = ctx.MintFleetId(factionTag, departed, sourceNode.Id, altNode.Id + ":" + v.ItemKey),
                LastResolvedSegment = -1,
            };

            // Consolidation applies to the fetched-hull tier too: the convoy is already flying a
            // Pickup itinerary, so collecting the other vectors bound for this destination costs it
            // extra stops, not extra convoys. Radius is measured from sourceNode -- the node this
            // convoy actually starts loading at -- not from altNode, which it only passes through
            // to pick up a hull. Nothing here can fail after the ship is consumed, so extras are
            // committed as they are turned into stops.
            List<HaulPickup> extras = SelectExtraPickups(vectorIndex, pool, sourceNode, destNode, prefabId,
                cargoCapacityLiters, v.Quantity, world, tuning, context);
            HaulViability viability = AssessConvoy(v, extras, judgedCapacityLiters, tuning,
                ConvoyFuelCredits(prefabId, altNode, sourceNode, extras, destNode, v, judgedCapacityLiters, world, tuning),
                CargoCredits(v, extras, destNode.Id, world));
            if (!viability.WorthFlying)
            {
                ctx.ReturnShip(altNode.Id, prefabId, 1);
                LogLoadGated(ctx, factionTag, profile, v, viability, judgedCapacityLiters, prefabId, sourceNode.Id, destNode.Id);
                loadGated = true;
                return false;
            }

            haul.RemainingStops = BuildPickupStops(extras);
            // Buyer-flies has nothing to hand over at the far end: the goods are bought at the pickup
            // and the delivery node is home, which is exactly what a Return stop is. Seller-flies
            // hands over at a Service stop and then comes home.
            haul.RemainingStops.Add(new FleetStop
            {
                NodeId = destNode.Id,
                Kind = profile.IsProcurement ? FleetStopKind.Return : FleetStopKind.Service,
            });
            if (profile.ReturnsToSource)
                haul.RemainingStops.Add(new FleetStop { NodeId = sourceNode.Id, Kind = FleetStopKind.Return });

            haul = PlanTransitAndLeg(haul, ctx, world, tuning, sourceNode.Id, destNode.Id, true);
            ctx.AppendPendingFleet(haul);
            _lastTelemetry.PickupDispatches++;
            _lastTelemetry.ConsolidatedPickups += extras.Count;
            RecordHullSpecialisation(world, prefabId);

            foreach (HaulPickup extra in extras)
            {
                pool.Consume(extra.PoolIndex);
                ctx.SetLastHaulDepartedUtc(extra.Vector.SourceNodeId, ctx.UtcNow);
                context.RecordDispatchedThisCycle(destNode.Id, extra.Vector.ItemKey, extra.Vector.Quantity);
            }

            // Nothing is aboard on a Pickup leg -- the convoy is flying out to fetch a hull and then
            // to load. Every litre here is still a plan, the primary vector's share included.
            double pickupPlanLiters = LitersOf(v.ItemKey, v.Quantity);
            for (int i = 0; i < extras.Count; i++)
                pickupPlanLiters += LitersOf(extras[i].Vector.ItemKey, extras[i].Vector.Quantity);

            if (profile.Kind == ActionKind.Haul)
                ctx.AddLog(haul.LogId + " " + factionTag + " HAUL_DISPATCHED " + v.ItemKey + " x" + plannedQuantity
                        + " " + sourceNode.Id + " -> " + destNode.Id
                        + " via_pickup=" + altNode.Id
                        + " eta_pickup=" + arrival.ToString("HH:mm:ss")
                        + (prefabId.Length > 0 ? " prefab=" + prefabId : "")
                        + (string.IsNullOrEmpty(passedOverPrefabId) ? "" : " passed_over=" + passedOverPrefabId)
                        + DescribeConsolidation(0, 0, extras)
                        + (gasInTanks
                           ? DescribeGasLoad(0, plannedQuantity, gasCapLiters)
                           : DescribeLoad(0, pickupPlanLiters, cargoCapacityLiters))
                        + " urgency=" + v.Urgency.ToString("0.###", CultureInfo.InvariantCulture));
            context.RecordDispatchedThisCycle(destNode.Id, v.ItemKey, plannedQuantity);
            info.Tier = 2;
            info.PrefabId = prefabId;
            info.LoadedQuantity = plannedQuantity;
            info.LogId = haul.LogId;
            info.FirstLegNodeId = altNode.Id;
            return true;
        }

        /// <summary>Whether <paramref name="node"/> is still the acting faction's at dispatch time.
        /// <para>Provenance: FAC-278.</para></summary>
        private static bool IsOwnedBy(SpatialNode node, string factionTag)
        {
            return node != null && node.OwnerFactionTag != null
                && string.Equals(node.OwnerFactionTag, factionTag, StringComparison.Ordinal);
        }

        /// <summary>The fuel ranking for a hull flying <paramref name="pool"/>[<paramref name="vectorIndex"/>]
        /// from <paramref name="loadingNode"/> to <paramref name="destNode"/>, departing from
        /// <paramref name="hullNode"/> (the loading node itself, or a donor it is fetched from). The
        /// load it is ranked against is the vector plus its same-destination companions at the loading
        /// node, which fold into the same hold at no extra stop. Null when the ranking is switched off
        /// (<see cref="FactionTuning.HaulRankHullsByFuel"/>), which restores largest-hold-first.
        /// <para>A gas vector carried in tanks (<see cref="GasCarriage.InTanks"/>) always gets a ranking,
        /// with <see cref="HaulHullCost.GasKey"/> set so only a hull with tank room for the gas is
        /// eligible and the smallest room that takes the load wins the tie-break. With the fuel ranking
        /// off that ranking is <see cref="HaulHullCost.Unpriced"/>. Its load is the vector alone: gas
        /// does not consolidate, and a hold job's gas companions do not fold into its hold.</para>
        /// <para>A <paramref name="bulk"/> job (<see cref="CarriesInBulk"/>) is always
        /// <see cref="HaulHullCost.Unpriced"/>, so <see cref="HaulHullCost.Fit"/> alone picks: the
        /// smallest capacity that takes the whole load, otherwise the largest, where a hull's capacity is
        /// the smaller of its hold and what it lifts on the loaded leg
        /// (<see cref="HaulHullCost.LiftKgPerLiter"/>, unset when the item's mass is unknown).</para></summary>
        private HaulHullCost HullCostFor(HaulVectorPool pool, int vectorIndex, SpatialNode hullNode,
            SpatialNode loadingNode, SpatialNode destNode, WorldModel world, FactionTuning tuning, bool bulk = false)
        {
            MaterialVector v = pool[vectorIndex];
            bool gasInTanks = GasCarriage.InTanks(v.ItemKey);
            bool ranked = tuning != null && tuning.HaulRankHullsByFuel && world.FuelLedger != null
                && tuning.FuelDrawMode != FuelDrawModes.Off;
            if (!ranked && !gasInTanks && !bulk) return null;
            if (gasInTanks)
            {
                HaulHullCost gasCost = ranked
                    ? new HaulHullCost(world, tuning, hullNode.Id, loadingNode.Position, destNode.Position,
                                       v.Quantity, KgPerLiter(v.ItemKey),
                                       ReferenceEquals(hullNode, loadingNode) ? (Vec3?)null : hullNode.Position)
                    : HaulHullCost.Unpriced(world, tuning, loadingNode.Position, destNode.Position, v.Quantity);
                gasCost.GasKey = v.ItemKey;
                return gasCost;
            }
            double liters = LitersOf(v.ItemKey, v.Quantity);
            foreach (int i in pool.Companions(vectorIndex))
            {
                MaterialVector companion = pool[i];
                if (GasCarriage.InTanks(companion.ItemKey)) continue;
                if (string.Equals(companion.SourceNodeId, loadingNode.Id, StringComparison.Ordinal))
                    liters += LitersOf(companion.ItemKey, companion.Quantity);
            }
            if (bulk && !gasInTanks)
            {
                HaulHullCost bulkCost = HaulHullCost.Unpriced(world, tuning, loadingNode.Position, destNode.Position, liters);
                bulkCost.LiftKgPerLiter = KgPerLiter(v.ItemKey);
                return bulkCost;
            }
            Vec3? ferryFrom = ReferenceEquals(hullNode, loadingNode) ? (Vec3?)null : hullNode.Position;
            return new HaulHullCost(world, tuning, hullNode.Id, loadingNode.Position, destNode.Position,
                                    liters, KgPerLiter(v.ItemKey), ferryFrom);
        }

        /// <summary>Whether a vector's hull is chosen by how much of it the hull carries rather than by
        /// fuel per litre: an ice Trade vector (either direction). Ice moves in bulk between planets,
        /// and the fuel ranking plus the nearest-donor rule put half of MEMN's ice trips (2026-09-29)
        /// in a Brokkr-Multi carrying ~23k ice where a B60 hold takes ~3.6 M.</summary>
        private static bool CarriesInBulk(VectorDispatchProfile profile, MaterialVector v, FactionTuning tuning)
        {
            return profile.Kind == ActionKind.Trade && FuelMarket.IsIceKey(v.ItemKey, tuning);
        }

        /// <summary>For a bulk job (<see cref="CarriesInBulk"/>): the donor, among the first
        /// <see cref="FactionTuning.HaulSmallHullSearchNodes"/> of the network's haul-stock nodes
        /// (nearest first), whose hull <paramref name="cost"/> fits the load best
        /// (<see cref="HaulHullCost.Fit"/>), nearer first on a tie. A donor's hull is the one its
        /// consume would take: available, cargo-suitable, feasible on both the pickup and the delivery
        /// leg, least specialised first — so the route and specialisation gates are the consume's own.
        /// Null when no donor has one, and the caller falls back to the nearest donor.</summary>
        private string FindBulkDonor(FactionTickContext context, string factionTag, SpatialNode sourceNode,
            SpatialNode destNode, ShipPool.RouteEnvironment deliveryRoute, HaulHullCost cost,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            if (cost == null || cat == null || tuning == null) return null;

            List<string> candidates;
            if (!context.AlternateSources.TryGetValue(sourceNode.Id, out candidates))
            {
                candidates = FindNodesWithHaulStock(context, factionTag, sourceNode.Id, ctx, world, tuning);
                context.AlternateSources[sourceNode.Id] = candidates;
            }

            int limit = tuning.HaulSmallHullSearchNodes > 0 ? tuning.HaulSmallHullSearchNodes : int.MaxValue;
            string best = null;
            double bestFit = double.MinValue;
            int searched = 0;
            foreach (string candidateId in candidates)
            {
                if (searched >= limit) break;
                SpatialNode candidate;
                if (!world.Graph.Nodes.TryGetValue(candidateId, out candidate)) continue;
                searched++;
                ShipPool.RouteEnvironment pickupRoute = GetRoute(context, candidate, sourceNode, world, tuning);
                double fit;
                if (!ConsumableFit(ctx, candidateId, cat, cost, pickupRoute, deliveryRoute, out fit)) continue;
                if (best == null || fit > bestFit) { best = candidateId; bestFit = fit; }
            }
            return best;
        }

        /// <summary>The <see cref="HaulHullCost.Fit"/> of the hull a cargo consume at
        /// <paramref name="nodeId"/> would take for <paramref name="cost"/>'s job: among its available,
        /// cargo-suitable hulls that clear both legs, the least specialised tier
        /// (<see cref="ShipPool.SpecialistCapabilityCount"/>: none, one, any), then the best fit. False
        /// when there is none.</summary>
        private bool ConsumableFit(IOperationsExecutionContext ctx, string nodeId, IPrefabCatalogue cat,
            HaulHullCost cost, ShipPool.RouteEnvironment pickupRoute, ShipPool.RouteEnvironment deliveryRoute,
            out double fit)
        {
            fit = double.MinValue;
            int bestTier = int.MaxValue;
            foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(nodeId))
            {
                if (kv.Value <= 0 || ctx.AvailableShips(nodeId, kv.Key) <= 0) continue;
                _lastTelemetry.ShipSuitabilityChecks++;
                if (!ShipPool.IsSuitableFor(ActionKind.Haul, cat, kv.Key) || !cost.Carries(kv.Key)) continue;
                if (!ShipPool.IsRouteFeasible(cat, kv.Key, pickupRoute)
                    || !ShipPool.IsRouteFeasible(cat, kv.Key, deliveryRoute)) continue;
                int tier = Math.Min(ShipPool.SpecialistCapabilityCount(cat, kv.Key), 2);
                double f = cost.Fit(kv.Key);
                if (tier < bestTier || (tier == bestTier && f > fit)) { bestTier = tier; fit = f; }
            }
            return bestTier != int.MaxValue;
        }

        /// <summary>Litres a planned load of <paramref name="quantity"/> units occupies: its units for a
        /// gas carried in tanks (gas units are litres), otherwise its volume in the hold.</summary>
        private double PlanLiters(string itemKey, long quantity)
        {
            return GasCarriage.InTanks(itemKey) ? quantity : LitersOf(itemKey, quantity);
        }

        /// <summary>The largest hold <paramref name="plannedLiters"/> still fills to
        /// <see cref="FactionTuning.HaulMinLoadFraction"/>; 0 when either is unknown. One figure for the
        /// donor search and the donor's consume, so the hull sent is one the search counted.</summary>
        private static double RightSizedCapacity(double plannedLiters, FactionTuning tuning)
        {
            if (tuning == null || tuning.HaulMinLoadFraction <= 0 || plannedLiters <= 0) return 0;
            return plannedLiters / tuning.HaulMinLoadFraction;
        }

        /// <summary>A donor within the first <see cref="FactionTuning.HaulSmallHullSearchNodes"/> of
        /// the network's haul-stock nodes holding a cargo hull small enough that
        /// <paramref name="plannedLiters"/> fills <see cref="FactionTuning.HaulMinLoadFraction"/> of
        /// it, and able to fly donor-to-source-to-destination. Null when none is, or when the job's
        /// volume is unknown: a right-sized hull cannot be named for a load of unknown size.
        /// <para>With <paramref name="gasCost"/> (a gas load in tanks), size is the hull's tank room for
        /// the gas on the loaded leg (<see cref="HaulHullCost.CapacityLiters"/>) and a hull with no
        /// room is not a candidate — the same reading the donor's consume applies.</para></summary>
        private string FindRightSizedDonor(FactionTickContext context, string factionTag, SpatialNode sourceNode,
            SpatialNode destNode, double plannedLiters, IOperationsExecutionContext ctx, WorldModel world,
            FactionTuning tuning, HaulHullCost gasCost = null)
        {
            if (tuning == null || tuning.HaulSmallHullSearchNodes <= 0 || tuning.HaulMinLoadFraction <= 0) return null;
            if (plannedLiters <= 0 || world.PrefabCatalogue == null) return null;
            double maxCapacity = RightSizedCapacity(plannedLiters, tuning);

            List<string> candidates;
            if (!context.AlternateSources.TryGetValue(sourceNode.Id, out candidates))
            {
                candidates = FindNodesWithHaulStock(context, factionTag, sourceNode.Id, ctx, world, tuning);
                context.AlternateSources[sourceNode.Id] = candidates;
            }

            IPrefabCatalogue cat = world.PrefabCatalogue;
            ShipPool.RouteEnvironment deliveryRoute = GetRoute(context, sourceNode, destNode, world, tuning);
            int searched = 0;
            foreach (string candidateId in candidates)
            {
                if (searched >= tuning.HaulSmallHullSearchNodes) break;
                SpatialNode candidate;
                if (!world.Graph.Nodes.TryGetValue(candidateId, out candidate)) continue;
                searched++;
                ShipPool.RouteEnvironment pickupRoute = GetRoute(context, candidate, sourceNode, world, tuning);
                foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(candidateId))
                {
                    if (kv.Value <= 0 || !ShipPool.IsSuitableFor(ActionKind.Haul, cat, kv.Key)) continue;
                    double cap = gasCost != null ? gasCost.CapacityLiters(cat, kv.Key) : cat.CargoLiters(kv.Key);
                    if (cap <= 0 || cap > maxCapacity) continue;
                    _lastTelemetry.ShipSuitabilityChecks++;
                    if (ShipPool.IsRouteFeasible(cat, kv.Key, pickupRoute)
                        && ShipPool.IsRouteFeasible(cat, kv.Key, deliveryRoute))
                        return candidateId;
                }
            }
            return null;
        }

        /// <summary>Fuel credits for <paramref name="prefabId"/> flying this convoy: from
        /// <paramref name="hullNode"/> (empty, when it is fetched) to the loading node, through each
        /// remote pickup in route order, to the destination. Valued at the node the hull departs from.
        /// -1 when the ceiling is off or the hull cannot be priced.</summary>
        private double ConvoyFuelCredits(string prefabId, SpatialNode hullNode, SpatialNode loadingNode,
            List<HaulPickup> extras, SpatialNode destNode, MaterialVector v, long capacityLiters,
            WorldModel world, FactionTuning tuning)
        {
            if (tuning == null || tuning.FuelValueCeiling <= 0 || string.IsNullOrEmpty(prefabId)) return -1;
            if (tuning.FuelDrawMode == FuelDrawModes.Off) return -1;   // the fuel model is off: no decision reads it
            if (world.FuelLedger == null || capacityLiters <= 0) return -1;

            double planned = PlanLiters(v.ItemKey, v.Quantity);
            var ends = new List<Vec3> { loadingNode.Position };
            string lastStop = loadingNode.Id;
            if (extras != null)
                foreach (HaulPickup extra in extras)
                {
                    planned += LitersOf(extra.Vector.ItemKey, extra.Vector.Quantity);
                    if (string.Equals(extra.Vector.SourceNodeId, lastStop, StringComparison.Ordinal)) continue;
                    ends.Add(extra.Node.Position);
                    lastStop = extra.Vector.SourceNodeId;
                }
            ends.Add(destNode.Position);

            Vec3? ferryFrom = ReferenceEquals(hullNode, loadingNode) ? (Vec3?)null : hullNode.Position;
            var cost = new HaulHullCost(world, tuning, hullNode.Id, loadingNode.Position, destNode.Position,
                                        planned, KgPerLiter(v.ItemKey), ferryFrom);
            return cost.FuelCreditsAlong(prefabId, planned < capacityLiters ? planned : capacityLiters, ends);
        }

        /// <summary>What the plan's goods are worth at the destination: each item's price times the
        /// destination's scarcity of it, the same valuation fuel gets at its node. 0 when no item in the
        /// plan has a price.</summary>
        private static double CargoCredits(MaterialVector v, List<HaulPickup> extras, string destNodeId, WorldModel world)
        {
            double credits = ItemCreditsAt(v.ItemKey, v.Quantity, destNodeId, world);
            if (extras != null)
                foreach (HaulPickup extra in extras)
                    credits += ItemCreditsAt(extra.Vector.ItemKey, extra.Vector.Quantity, destNodeId, world);
            return credits;
        }

        private static double ItemCreditsAt(string key, long quantity, string nodeId, WorldModel world)
        {
            if (quantity <= 0 || string.IsNullOrEmpty(key)) return 0;
            double price = world.ItemUnitValue(ItemPriceResolver.StripInventoryPrefix(key));
            if (price <= 0) return 0;
            if (world.PriceOracle != null)
            {
                double scarcity = world.PriceOracle.ScarcityMultiplier(nodeId, key);
                if (scarcity > 0) price *= scarcity;
            }
            return quantity * price / ItemCredits.UnitsPerPricedUnit(key);
        }

        private double KgPerLiter(string itemKey)
        {
            double litersPerUnit = _volumes != null ? _volumes.VolumeLitersPerUnit(itemKey) : 0;
            double kgPerUnit = _masses != null ? _masses.MassKgPerUnit(itemKey) : 0;
            return litersPerUnit > 0 && kgPerUnit > 0 ? kgPerUnit / litersPerUnit : 0;
        }

        private ShipPool.RouteEnvironment GetRoute(FactionTickContext context, SpatialNode origin, SpatialNode destination,
            WorldModel world, FactionTuning tuning, double gravityFloorG = -1)
        {
            double gravityFloor = gravityFloorG < 0 ? tuning.OrbitGravityMin : gravityFloorG;
            var key = new RouteKey(origin.Id, destination.Id, tuning.MinDryTwr, gravityFloor);
            ShipPool.RouteEnvironment route;
            if (context.Routes.TryGetValue(key, out route))
            {
                _lastTelemetry.RouteCacheHits++;
                return route;
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            route = ShipPool.RouteEnvironment.From(world.Planets, origin.Position, destination.Position,
                tuning.MinDryTwr, gravityFloor);
            _lastTelemetry.RoutePlanningTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _lastTelemetry.RouteCacheMisses++;
            context.Routes[key] = route;
            return route;
        }

        /// <summary>A Haul alternate-source outcome: a donor whose stock survives the pickup leg, or
        /// -- when nothing does -- the nearest donor that was refused and the hull that was refused,
        /// so the block line can say which. Mirrors <see cref="MineAlternate"/>.</summary>
        private struct HaulAlternate
        {
            public string NodeId;
            public string BlockedNodeId;
            public string BlockedPrefabId;
            /// <summary>The refused hull cleared the pickup hop and failed the delivery leg instead.
            /// A fetched hull flies donor→pickup→delivery and is left standing at the delivery node,
            /// so the two refusals are different facts and naming the wrong one sends the next reader
            /// to a leg that was never the problem.</summary>
            public bool BlockedOnDelivery;
        }

        /// <summary><paramref name="destNode"/> is where a fetched hull ends up, so a donor whose
        /// stock cannot hold altitude there is not a donor. Checking only the pickup hop here hands
        /// <see cref="TryDispatchHaulViaPickup"/> a node its own consume then refuses, which burns
        /// the dispatch and names the pickup leg in the block line — the leg that was fine.
        /// <para>With <paramref name="gasCost"/> (a gas load in tanks) a donor must hold a hull with tank
        /// room for the gas (<see cref="HaulHullCost.Carries"/>), the predicate the donor's consume
        /// applies. A node whose cargo hulls have none is skipped, not reported as blocked: nothing
        /// there was refused a route.</para></summary>
        private HaulAlternate GetAlternateSource(FactionTickContext context, string factionTag, string sourceNodeId,
            SpatialNode destNode, IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            HaulHullCost gasCost = null)
        {
            List<string> candidates;
            if (!context.AlternateSources.TryGetValue(sourceNodeId, out candidates))
            {
                _lastTelemetry.AlternateSourceScans++;
                long scanStart = System.Diagnostics.Stopwatch.GetTimestamp();
                long scanRouteBefore = _lastTelemetry.RoutePlanningTicks;
                candidates = FindNodesWithHaulStock(context, factionTag, sourceNodeId, ctx, world, tuning);
                // FindNodesWithHaulStock plans routes internally, so subtract them here exactly as the
                // enclosing alternate/direct spans do -- otherwise this sub-span is not comparable to
                // its parent and can exceed it.
                long scanElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - scanStart
                    - (_lastTelemetry.RoutePlanningTicks - scanRouteBefore);
                if (scanElapsed > 0) _lastTelemetry.AlternateScanTicks += scanElapsed;
                context.AlternateSources[sourceNodeId] = candidates;
            }

            // Ship stock mutates as vectors dispatch. Keep the expensive ordered network scan cached,
            // but revalidate each candidate's cheap local stock before reuse so consuming the first
            // node does not hide a second available Hauler later in the same faction tick.
            long revalidateStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long revalidateRouteBefore = _lastTelemetry.RoutePlanningTicks;
            var found = new HaulAlternate();
            SpatialNode source;
            world.Graph.Nodes.TryGetValue(sourceNodeId, out source);
            foreach (string candidateId in candidates)
            {
                SpatialNode candidate;
                if (!world.Graph.Nodes.TryGetValue(candidateId, out candidate)) continue;
                _lastTelemetry.ShipSuitabilityChecks++;
                if (!ctx.HasSuitableShip(candidate.Id, ActionKind.Haul, world.PrefabCatalogue, null)) continue;
                if (gasCost != null && !HasTankedShip(ctx, candidate.Id, world.PrefabCatalogue, gasCost, null, null)) continue;
                if (source == null) { found.NodeId = candidateId; break; }

                ShipPool.RouteEnvironment route = GetRoute(context, candidate, source, world, tuning, 0);
                ShipPool.RouteEnvironment? deliveryRoute = destNode != null
                    ? GetRoute(context, source, destNode, world, tuning, 0)
                    : (ShipPool.RouteEnvironment?)null;
                _lastTelemetry.ShipSuitabilityChecks++;
                if (gasCost != null
                    ? HasTankedShip(ctx, candidate.Id, world.PrefabCatalogue, gasCost, route, deliveryRoute)
                    : ctx.HasSuitableShip(candidate.Id, ActionKind.Haul, world.PrefabCatalogue, route, deliveryRoute))
                { found.NodeId = candidateId; break; }

                if (found.BlockedNodeId == null)
                {
                    bool onDelivery;
                    found.BlockedNodeId = candidateId;
                    found.BlockedPrefabId = DescribeRefusedHaulHull(ctx, world.PrefabCatalogue, candidateId,
                        route, deliveryRoute, out onDelivery);
                    found.BlockedOnDelivery = onDelivery;
                }
            }
            long revalidateElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - revalidateStart
                - (_lastTelemetry.RoutePlanningTicks - revalidateRouteBefore);
            if (revalidateElapsed > 0) _lastTelemetry.AlternateRevalidateTicks += revalidateElapsed;
            // A merely-suitable donor is reported as blocked rather than returned as usable.
            // ShipPool.HasSuitable and ShipPool.Consume apply the identical predicate
            // (IsSuitableFor && IsRouteFeasible), so handing one to TryDispatchHaulViaPickup can only
            // burn an attempt rediscovering what this scan already proved, and names an alt->src leg
            // in the block log as though src->dest had failed.
            return found;
        }

        /// <summary>Whether <paramref name="nodeId"/> has an available cargo hull that carries
        /// <paramref name="gasCost"/>'s gas (<see cref="HaulHullCost.Carries"/>) and clears both legs:
        /// <see cref="IOperationsExecutionContext.HasSuitableShip"/>'s predicate with the tank test
        /// added, which is what <see cref="ShipPool.Consume(IDictionary{string, int}, IReadOnlyDictionary{string, int}, ActionKind, IPrefabCatalogue, int, out string, ShipPool.RouteEnvironment?, ShipPool.RouteEnvironment?, HaulHullCost)"/>
        /// applies for a gas job.</summary>
        private static bool HasTankedShip(IOperationsExecutionContext ctx, string nodeId, IPrefabCatalogue cat,
            HaulHullCost gasCost, ShipPool.RouteEnvironment? route, ShipPool.RouteEnvironment? viaRoute)
        {
            foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(nodeId))
            {
                if (kv.Value <= 0 || ctx.AvailableShips(nodeId, kv.Key) <= 0) continue;
                if (!ShipPool.IsSuitableFor(ActionKind.Haul, cat, kv.Key) || !gasCost.Carries(kv.Key)) continue;
                if (ShipPool.IsRouteFeasible(cat, kv.Key, route) && ShipPool.IsRouteFeasible(cat, kv.Key, viaRoute))
                    return true;
            }
            return false;
        }

        /// <summary>Whether <paramref name="st"/> owns a cargo hull with a tank for
        /// <paramref name="gasKey"/> anywhere — parked at an owned node (store listing included) or away
        /// on a mission — on the ownership reading <see cref="NetworkOwnsSuitable"/> takes, and for the
        /// same reason: a busy tanker is not a missing one. Resolved once per gas per faction pass.</summary>
        private static bool OwnsGasTank(FactionTickContext context, FactionState st, string gasKey,
            IOperationsExecutionContext ctx, WorldModel world)
        {
            bool owns;
            if (context.GasTankOwnership.TryGetValue(gasKey, out owns)) return owns;
            owns = false;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            foreach (SpatialNode n in ctx.OwnedNodes(st.Tag))
            {
                foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(n.Id))
                    if (kv.Value > 0 && CarriesGas(cat, kv.Key, gasKey)) { owns = true; break; }
                if (owns) break;
            }
            if (!owns)
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (f.Resolved || f.Intercepted) continue;
                    if (CarriesGas(cat, f.ShipPrefabId, gasKey)) { owns = true; break; }
                    if (f.Squadron != null)
                        foreach (string prefabId in f.Squadron)
                            if (CarriesGas(cat, prefabId, gasKey)) { owns = true; break; }
                    if (owns) break;
                }
            context.GasTankOwnership[gasKey] = owns;
            return owns;
        }

        private static bool CarriesGas(IPrefabCatalogue cat, string prefabId, string gasKey)
        {
            return !string.IsNullOrEmpty(prefabId) && ShipPool.IsSuitableFor(ActionKind.Haul, cat, prefabId)
                && GasCarriage.TankLitres(cat, prefabId, gasKey) > 0;
        }

        /// <summary>Names the best cargo hull in <paramref name="nodeId"/>'s stock that
        /// <paramref name="route"/> refuses -- the difference between "a hull the gate is wrong about"
        /// and "a hull that genuinely cannot fly this regime". Best = largest
        /// <see cref="IPrefabCatalogue.CargoLiters"/>, matching what <see cref="ShipPool.Consume"/>
        /// would have preferred; ordinal tie-break keeps it deterministic. Diagnostic only -- never
        /// gates a dispatch.</summary>
        private static string DescribeRefusedHaulHull(IOperationsExecutionContext ctx, IPrefabCatalogue cat,
            string nodeId, ShipPool.RouteEnvironment? route, ShipPool.RouteEnvironment? deliveryRoute,
            out bool refusedOnDelivery)
        {
            string best = null;
            long bestCargo = -1;
            refusedOnDelivery = false;
            foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(nodeId))
            {
                if (kv.Value <= 0) continue;
                if (!ShipPool.IsCargoSuitable(cat, kv.Key)) continue;
                bool pickupOk = ShipPool.IsRouteFeasible(cat, kv.Key, route);
                if (pickupOk && ShipPool.IsRouteFeasible(cat, kv.Key, deliveryRoute)) continue;
                long cargo = cat != null ? cat.CargoLiters(kv.Key) : 0;
                if (best == null || cargo > bestCargo
                    || (cargo == bestCargo && string.CompareOrdinal(kv.Key, best) < 0))
                {
                    best = kv.Key;
                    bestCargo = cargo;
                    refusedOnDelivery = pickupOk;   // it flies the hop and cannot leave the far end
                }
            }
            return best;
        }

        private string GetShipyard(FactionTickContext context, string factionTag, string sourceNodeId, WorldModel world)
        {
            string result;
            if (context.ShipyardsResolved.Contains(sourceNodeId))
            {
                context.Shipyards.TryGetValue(sourceNodeId, out result);
                return result;
            }

            _lastTelemetry.ShipyardScans++;
            result = FindShipyardNode(factionTag, sourceNodeId, world);
            context.ShipyardsResolved.Add(sourceNodeId);
            if (result != null) context.Shipyards[sourceNodeId] = result;
            return result;
        }

        /// <summary>Scales a blocked shipment's real volume down to the size a *hull* should be built
        /// for. One vector's quantity is a local-shortage snapshot -- a node that has gone without
        /// posts one large catch-up shipment -- and sizing 1:1 against it builds for the spike
        /// instead of the flow, so the same faction gets a bigger hull for having been starved
        /// longer rather than for shipping more. <see cref="FactionTuning.HaulShipDemandCargoShare"/>
        /// (0.2, i.e. about five trips) is the divisor. Never returns below the nominal 1-litre
        /// "must be cargo-capable" sentinel, so a share of 0 degrades to "any cargo hull" rather
        /// than "no cargo requirement at all".</summary>
        private static double ShipDemandCargoFor(double shipmentLiters, FactionTuning tuning)
        {
            double share = tuning != null ? tuning.HaulShipDemandCargoShare : 0;
            if (share <= 0) return 1;
            return Math.Max(1, shipmentLiters * share);
        }

        /// <summary>Clamps a Tier-3 cargo floor to the largest hull the demand's own role can
        /// actually build -- the Haul-side counterpart of <c>TradeManager.MinCargoLitersFor</c>'s
        /// closing <c>Math.Min</c>, and for the same reason: <see
        /// cref="ShipyardManager.SelectPrefabBom"/> tolerates an unreachable floor by dropping it and
        /// ranking the unfiltered pool cheapest-first, so a shipment larger than every hull gets the
        /// SMALLEST one rather than the largest. Clamping keeps hull size monotonic in shipping
        /// volume at the top of the range.
        /// <para>The role is "Trade", not "Haul", because that is the role
        /// <see cref="StageFallback"/> actually posts for a blocked Haul vector (Haul and Trade share
        /// <c>ShipPool.IsSuitableFor</c>'s cargo definition, and <c>AssignCapabilityRoles</c> grants
        /// both to the same prefabs) -- the ceiling must come from the pool the demand will really be
        /// served from.</para>
        /// <para>A zero ceiling (no catalogue, or no cargo prefab for the role) leaves the floor
        /// untouched: an unknown ceiling is not evidence of a small one.</para></summary>
        private static double ClampToLargestHaulHull(double minCargoLiters, FactionTickContext context, WorldModel world)
        {
            if (minCargoLiters <= 0) return minCargoLiters;
            if (context.LargestHaulCargoLiters < 0)
                context.LargestHaulCargoLiters = ShipPool.LargestCargoLitersForRole(world.PrefabCatalogue, "Trade");
            long cap = context.LargestHaulCargoLiters;
            return cap > 0 ? Math.Min(minCargoLiters, cap) : minCargoLiters;
        }

        internal void StageFallback(FactionTickContext context, string factionTag, string role, string nodeId,
            string destinationNodeId, double minCargoLiters, string viaNodeId = null)
        {
            var key = new FallbackKey(factionTag, role, nodeId, destinationNodeId);
            FallbackValue existing;
            if (context.Fallbacks.TryGetValue(key, out existing))
            {
                // ViaNodeId travels WITH whichever post's cargo requirement currently wins -- that's
                // the vector actually dictating this demand's real constraint, so its pickup leg is
                // the one the built hull needs to survive too.
                if (minCargoLiters > existing.MinCargoLiters)
                    context.Fallbacks[key] = new FallbackValue { MinCargoLiters = minCargoLiters, ViaNodeId = viaNodeId };
            }
            else
            {
                context.Fallbacks[key] = new FallbackValue { MinCargoLiters = minCargoLiters, ViaNodeId = viaNodeId };
                context.FallbackOrder.Add(key);
            }
            _lastTelemetry.FallbackCandidates++;
        }

        internal void PublishFallbacks(FactionTuning tuning, FactionTickContext context, IOperationsExecutionContext ctx)
        {
            _lastTelemetry.UniqueFallbackKeys += context.Fallbacks.Count;
            context.FallbackOrder.Sort(CompareFallbackKeys);

            // ShipyardManager only ever maintains ONE BuildQueueEntry per node+role, so posting one
            // Ship demand per distinct blocked destination sharing that same build slot just piles up
            // redundant entries in DemandRegister -- dozens observed live on a busy Shipyard node --
            // without producing any additional build. Collapse each contiguous (faction, role,
            // buildNode) run down to one representative demand: CompareFallbackKeys already sorts on
            // exactly that prefix, so same-group keys are always adjacent. The representative is the
            // run's closest-to-median cargo need, the same principle
            // ShipyardManager.BuildRepresentativeDemands applies on the read side.
            int i = 0;
            while (i < context.FallbackOrder.Count)
            {
                int runEnd = i + 1;
                while (runEnd < context.FallbackOrder.Count
                       && SameFallbackGroup(context.FallbackOrder[i], context.FallbackOrder[runEnd]))
                    runEnd++;

                FallbackKey rep = runEnd - i == 1
                    ? context.FallbackOrder[i]
                    : ClosestToMedianFallback(context.FallbackOrder, i, runEnd, context.Fallbacks);
                FallbackValue repValue = context.Fallbacks[rep];
                PostShipDemand(tuning, rep.FactionTag, rep.Role, rep.BuildNodeId,
                    rep.DestinationNodeId, repValue.MinCargoLiters, ctx, repValue.ViaNodeId);

                i = runEnd;
            }
        }

        private static bool SameFallbackGroup(FallbackKey a, FallbackKey b)
        {
            return string.Equals(a.FactionTag, b.FactionTag, StringComparison.Ordinal)
                && string.Equals(a.Role, b.Role, StringComparison.Ordinal)
                && string.Equals(a.BuildNodeId, b.BuildNodeId, StringComparison.Ordinal);
        }

        private static FallbackKey ClosestToMedianFallback(List<FallbackKey> order, int start, int end,
            Dictionary<FallbackKey, FallbackValue> values)
        {
            int count = end - start;
            var sortedCargo = new List<double>(count);
            for (int k = start; k < end; k++) sortedCargo.Add(values[order[k]].MinCargoLiters);
            sortedCargo.Sort();
            double median = count % 2 == 1
                ? sortedCargo[count / 2]
                : (sortedCargo[count / 2 - 1] + sortedCargo[count / 2]) / 2.0;

            FallbackKey best = order[start];
            double bestDist = double.MaxValue;
            for (int k = start; k < end; k++)
            {
                double dist = Math.Abs(values[order[k]].MinCargoLiters - median);
                if (dist < bestDist) { bestDist = dist; best = order[k]; }
            }
            return best;
        }

        private static int CompareFallbackKeys(FallbackKey a, FallbackKey b)
        {
            int cmp = string.CompareOrdinal(a.FactionTag, b.FactionTag);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.Role, b.Role);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.BuildNodeId, b.BuildNodeId);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.DestinationNodeId, b.DestinationNodeId);
        }

        private void PostShipDemand(FactionTuning tuning, string factionTag, string role, string nodeId, string destNodeId,
            double minCargoLiters, IOperationsExecutionContext ctx, string viaNodeId = null)
        {
            // A staged cycle's execution context mirrors DemandRegister.Post's own refresh-preserve
            // behavior independently (see OperationsStagedWorldEffects), so this needs its own check
            // too. Equivalent fallback vectors may carry different quantities, so preserve the
            // strongest exact route demand on refresh.
            string kind = "Ship:" + role;
            DemandEntry existing;
            if (ctx.TryGetShipDemand(factionTag, kind, nodeId, destNodeId, out existing)
                && existing.MinCargoLiters > minCargoLiters)
                minCargoLiters = existing.MinCargoLiters;
            bool isNew = ctx.PostDemand(DemandEntry.ForShip(factionTag, role, nodeId, ctx.UtcNow.Add(ShipDemandTtlFor(tuning)),
                destNodeId: destNodeId, minCargoLiters: minCargoLiters, viaNodeId: viaNodeId,
                postedUtc: ctx.UtcNow));
            if (isNew)
            {
                _lastTelemetry.DemandPosts++;
                ctx.AddLog(factionTag + " SHIP_DEMAND role=" + role + " node=" + nodeId
                        + " dest=" + destNodeId
                        + (viaNodeId != null ? " via=" + viaNodeId : "")
                        + " minCargoLiters=" + minCargoLiters.ToString("0"));
            }
            else _lastTelemetry.DemandRefreshes++;
        }

        /// <summary>Nearest owned node (excluding <paramref name="excludeNodeId"/>, the node being
        /// restocked) with Haul-suitable ship stock, preferring one whose stock can actually survive
        /// the ferry-in leg — same shape as <see cref="TradeManager"/>'s identically-named helper
        /// (Haul/Trade share <see cref="ShipPool.IsSuitableFor"/>'s cargo branch). Falls back to the
        /// nearest merely cargo-suitable node when none is route-feasible; returns <c>null</c> only
        /// when nothing suitable exists anywhere in the network.</summary>
        private List<string> FindNodesWithHaulStock(FactionTickContext context, string factionTag, string excludeNodeId,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning)
        {
            SpatialNode excludeNode = null;
            bool hasDest = excludeNodeId != null && world.Graph.Nodes.TryGetValue(excludeNodeId, out excludeNode);

            var routeFeasible = new List<(string NodeId, double Distance)>();
            var merelySuitable = new List<(string NodeId, double Distance)>();

            foreach (SpatialNode n in ctx.OwnedNodes(factionTag))
            {
                if (n.Id == excludeNodeId) continue;
                _lastTelemetry.ShipSuitabilityChecks++;
                if (!ctx.HasSuitableShip(n.Id, ActionKind.Haul, world.PrefabCatalogue, null)) continue;

                double d = hasDest ? n.Position.DistanceTo(excludeNode.Position) : 0;
                bool feasible = !hasDest;
                if (hasDest)
                {
                    ShipPool.RouteEnvironment route = GetRoute(context, n, excludeNode, world, tuning, 0);
                    _lastTelemetry.ShipSuitabilityChecks++;
                    feasible = ctx.HasSuitableShip(n.Id, ActionKind.Haul, world.PrefabCatalogue, route);
                }
                if (feasible) routeFeasible.Add((n.Id, d));
                else merelySuitable.Add((n.Id, d));
            }

            Comparison<(string NodeId, double Distance)> byDistance = (a, b) =>
            {
                int cmp = a.Distance.CompareTo(b.Distance);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.NodeId, b.NodeId);
            };
            routeFeasible.Sort(byDistance);
            merelySuitable.Sort(byDistance);

            var result = new List<string>(routeFeasible.Count + merelySuitable.Count);
            foreach (var candidate in routeFeasible) result.Add(candidate.NodeId);
            foreach (var candidate in merelySuitable) result.Add(candidate.NodeId);
            return result;
        }

        /// <summary>Returns the faction node with a Shipyard capability that is closest to
        /// <paramref name="originNodeId"/>. Returns <c>null</c> if no Shipyard exists in the network.
        /// The ship will be built and stocked here, not at the (possibly Shipyard-less) node that
        /// failed to dispatch — see <see cref="TradeManager"/>'s identically-named helper.</summary>
        private static string FindShipyardNode(string factionTag, string originNodeId, WorldModel world)
        {
            SpatialNode originNode;
            Vec3 originPos = world.Graph.Nodes.TryGetValue(originNodeId, out originNode)
                ? originNode.Position : new Vec3();

            string bestId   = null;
            double bestDist = double.MaxValue;
            foreach (SpatialNode n in world.Graph.OwnedBy(factionTag))
            {
                int level;
                if (!n.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level) || level < 1) continue;
                double d = n.Position.DistanceTo(originPos);
                if (d < bestDist) { bestDist = d; bestId = n.Id; }
            }
            return bestId;
        }

        // ---- Mine: active extraction from owned nodes whose ground holds ore ----------------------
        // Its own loop -- deliberately does not thread a synthetic supply source through
        // MatchVectors/DispatchHaul's real-inventory matching pipeline (see docs/managers.md's
        // "Mine" section for the rationale). Reuses generic ship/route/cargo mechanics
        // (GetRoute, PendingFleetResolver.LoadCargo/PlanFleetLeg, the Pickup-leg PendingFleetAction
        // shape) but keeps its own catchment walk (WalkMineCatchment) and hull choice.

        /// <summary>One scored catchment deposit in <see cref="FindMineCatchment"/>'s ranking, kept
        /// with its score and distance so the sort does not rescore it.</summary>
        private struct MineCandidate
        {
            public readonly SpatialNode Node;
            public readonly double Score;
            public readonly double Distance;
            public MineCandidate(SpatialNode node, double score, double distance)
            {
                Node = node; Score = score; Distance = distance;
            }
        }

        /// <summary>Outcome of the Tier-2 alternate-node search (<see cref="FindMineAlternateShipNode"/>).
        /// <see cref="NodeId"/> is set only when that node's stock actually survives the pickup leg, so
        /// the caller can dispatch against it without re-deriving the gate. When it is null but
        /// <see cref="BlockedNodeId"/> is set, drill-equipped stock exists somewhere and none of it can
        /// fly its leg -- <see cref="BlockedPrefabId"/> names the representative hull that was refused,
        /// which is the difference between "build a drill ship" and "the drill ships you have are the
        /// wrong regime for this deposit".</summary>
        private struct MineAlternate
        {
            public string NodeId;
            /// <summary>The hull at <see cref="NodeId"/> the rank chose -- see <see cref="MineHull"/>.</summary>
            public MineHull Hull;
            public string BlockedNodeId;
            public string BlockedPrefabId;
            /// <summary>The refused hull cleared the pickup hop and failed the delivery leg instead —
            /// see <see cref="HaulAlternate.BlockedOnDelivery"/>.</summary>
            public bool BlockedOnDelivery;
        }

        /// <summary>The OTHER owned node holding the best Mine-suitable (drill-equipped, see
        /// <see cref="ShipPool.IsMineSuitable"/>) hull that can actually survive the pickup leg back to
        /// <paramref name="excludeNodeId"/> and the delivery leg after it -- the Tier 2 candidate
        /// <see cref="DispatchMine"/> weighs against a hull already at the source. "Best" is
        /// <see cref="MineHullOutranks"/>: a doctrine hull, then the highest drill rate, then the
        /// nearest. Ranks candidates route-feasibility-first, and when nothing anywhere can
        /// fly its leg reports that as a diagnosis rather than as a node: returning the geometric
        /// nearest instead would hand <see cref="DispatchMineViaPickup"/> a pickup it has already been
        /// proven cannot happen (<see cref="ShipPool.HasSuitable"/> and <see cref="ShipPool.Consume"/>
        /// apply the identical eligibility predicate), burning a dispatch attempt to rediscover it and
        /// naming a src->dest leg in the block log that was never the leg that failed.
        /// <para>Bounded exactly as before -- one pass over owned nodes -- because the per-candidate
        /// route sample goes through <see cref="GetRoute"/>, which memoizes into
        /// <c>FactionTickContext.Routes</c> and is already shared with every other dispatch decision
        /// this cycle. No new cache, no new phase.</para>
        /// <para><paramref name="hullNodes"/>, when given, is the subset of
        /// <paramref name="ownedNodes"/> that held an idle Mine hull when the pass first asked
        /// (<see cref="StandingMineScanPass.MineHullNodes"/>), in the same order. Within one pass a
        /// node's idle hulls only ever go down -- a dispatch takes one, and the only return is to the
        /// node it was just taken from -- so every node skipped this way would have failed the
        /// <see cref="IOperationsExecutionContext.HasSuitableShip"/> test below, and the answer is the
        /// same. The refused-hull description is taken once, for the node the search settles on,
        /// instead of for every nearer blocked node on the way.</para></summary>
        private MineAlternate FindMineAlternateShipNode(string excludeNodeId, List<SpatialNode> ownedNodes,
            ShipPool.RouteEnvironment deliveryRoute, IOperationsExecutionContext ctx, IPrefabCatalogue cat,
            FactionTickContext context, WorldModel world, FactionTuning tuning, HashSet<string> doctrinePrefabs,
            List<SpatialNode> hullNodes = null)
        {
            SpatialNode excludeNode = null;
            foreach (SpatialNode n in ownedNodes)
                if (n.Id == excludeNodeId) { excludeNode = n; break; }

            var found = new MineAlternate();
            double blockedDist = double.MaxValue;
            ShipPool.RouteEnvironment? blockedRoute = null;
            foreach (SpatialNode n in hullNodes ?? ownedNodes)
            {
                if (n.Id == excludeNodeId) continue;
                if (!ctx.HasSuitableShip(n.Id, ActionKind.Mine, cat, null, null)) continue;
                double dist = excludeNode != null ? n.Position.DistanceTo(excludeNode.Position) : 0;

                ShipPool.RouteEnvironment? route = null;
                if (excludeNode != null)
                    route = GetRoute(context, n, excludeNode, world, tuning);

                MineHull hull;
                if (route != null && BestMineHullAt(ctx, cat, n.Id, route, deliveryRoute, doctrinePrefabs, out hull))
                {
                    // Fastest first, nearest among equals (FAC-395): a slow hull next door used to
                    // win over a drill forty times its rate one node further out.
                    hull.Distance = dist;
                    if (found.NodeId == null || MineHullOutranks(hull, n.Id, found.Hull, found.NodeId))
                    {
                        found.NodeId = n.Id;
                        found.Hull = hull;
                    }
                    continue;
                }

                if (found.BlockedNodeId == null || dist < blockedDist
                    || (dist == blockedDist && string.CompareOrdinal(n.Id, found.BlockedNodeId) < 0))
                {
                    found.BlockedNodeId = n.Id;
                    blockedRoute = route;
                    blockedDist = dist;
                }
            }
            // Only a search that found no flyable hull reports the refused one.
            if (found.NodeId == null && found.BlockedNodeId != null)
            {
                bool onDelivery;
                found.BlockedPrefabId = DescribeRefusedMineHull(ctx, cat, found.BlockedNodeId, blockedRoute,
                    deliveryRoute, out onDelivery);
                found.BlockedOnDelivery = onDelivery;
            }
            return found;
        }

        /// <summary>One idle drill hull a Mine dispatch could fly: which prefab, its
        /// <see cref="IPrefabCatalogue.DrillYieldScore"/> (drill rate is that times two tuning
        /// constants, so the score ranks rates exactly), whether the faction's doctrine names it, and
        /// how far it sits from the deposit.</summary>
        private struct MineHull
        {
            public string PrefabId;
            public double Score;
            public bool Doctrine;
            public double Distance;
        }

        /// <summary>Whether hull <paramref name="a"/> at <paramref name="aNodeId"/> is the better pick
        /// than <paramref name="b"/> at <paramref name="bNodeId"/> (FAC-395).
        ///
        /// <para><b>Doctrine first.</b> A player whose doctrine names a Mine hull gets that hull
        /// whenever one is idle and can fly the trip -- the doctrine is the player's statement of which
        /// hull does this job, and the rank below must not overrule it. Empty for every NPC.</para>
        ///
        /// <para><b>Then the highest drill rate.</b> The dwell is <c>volume / rate</c> and the
        /// deposit's cooldown is that dwell again, so a slow hull holds the ship AND the seam
        /// proportionally longer for the same load. Transit is not comparable: the whole search is
        /// minutes at fleet speed, while a Spadi at ~8 L/s against a WHI-24Drill at ~1,616 L/s is a
        /// 200-fold difference in dwell.</para>
        ///
        /// <para><b>Then the nearest</b>, then node and prefab id, so equal hulls resolve the same way
        /// under the live and the staged context.</para></summary>
        private static bool MineHullOutranks(MineHull a, string aNodeId, MineHull b, string bNodeId)
        {
            if (a.Doctrine != b.Doctrine) return a.Doctrine;
            if (a.Score != b.Score) return a.Score > b.Score;
            if (a.Distance != b.Distance) return a.Distance < b.Distance;
            int cmp = string.CompareOrdinal(aNodeId, bNodeId);
            if (cmp != 0) return cmp < 0;
            return string.CompareOrdinal(a.PrefabId, b.PrefabId) < 0;
        }

        /// <summary>The best idle Mine hull at <paramref name="nodeId"/> that clears both
        /// <paramref name="route"/> and <paramref name="viaRoute"/>, by <see cref="MineHullOutranks"/>.
        /// Same eligibility as <see cref="ShipPool.Consume"/> for <see cref="ActionKind.Mine"/> --
        /// suitable, route-feasible on every leg, not reserved by a store listing -- so a hull this
        /// names is one <see cref="IOperationsExecutionContext.ConsumeShipPrefab"/> can take.
        /// <see cref="MineHull.Distance"/> is left 0 for the caller to set.</summary>
        private static bool BestMineHullAt(IOperationsExecutionContext ctx, IPrefabCatalogue cat, string nodeId,
            ShipPool.RouteEnvironment? route, ShipPool.RouteEnvironment? viaRoute, HashSet<string> doctrinePrefabs,
            out MineHull best)
        {
            best = new MineHull();
            bool found = false;
            foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(nodeId))
            {
                if (kv.Value <= 0 || string.IsNullOrEmpty(kv.Key)) continue;
                if (!ShipPool.IsSuitableFor(ActionKind.Mine, cat, kv.Key)) continue;
                if (!ShipPool.IsRouteFeasible(cat, kv.Key, route) || !ShipPool.IsRouteFeasible(cat, kv.Key, viaRoute)) continue;
                if (ctx.AvailableShips(nodeId, kv.Key) <= 0) continue;
                var hull = new MineHull
                {
                    PrefabId = kv.Key,
                    Score = cat != null ? cat.DrillYieldScore(kv.Key) : 0,
                    Doctrine = doctrinePrefabs != null && doctrinePrefabs.Contains(PrefabVersions.FamilyOf(cat, kv.Key)),
                };
                if (!found || MineHullOutranks(hull, nodeId, best, nodeId)) { best = hull; found = true; }
            }
            return found;
        }

        /// <summary>The prefabs of a player faction's active doctrine lines whose hull is suitable for
        /// Mine (<see cref="ShipPool.IsMineSuitable"/>), or null when there are none (every NPC, and
        /// every player without such a line). A line names a hull, not a role. Paused lines do not
        /// count: a paused line has stopped speaking for the fleet. Held as design families, so
        /// every version of a line's hull is its hull.</summary>
        private static HashSet<string> MineDoctrinePrefabs(FactionState st, IPrefabCatalogue cat)
        {
            if (!FactionLifecycle.DoctrineActive(st)) return null;
            HashSet<string> result = null;
            foreach (DoctrineLine line in st.FleetDoctrine)
            {
                if (line == null || line.Paused || string.IsNullOrEmpty(line.PrefabId)) continue;
                if (!ShipPool.IsMineSuitable(cat, line.PrefabId)) continue;
                (result ?? (result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                    .Add(PrefabVersions.FamilyOf(cat, line.PrefabId));
            }
            return result;
        }

        /// <summary>Names the best drill-equipped hull in <paramref name="nodeId"/>'s stock that
        /// <paramref name="route"/> refuses -- the one detail <c>MINE_DISPATCH_BLOCKED</c> was missing,
        /// and the whole difference between "a hull the gate is wrong about" and "a hull that genuinely
        /// cannot fly this regime". Best = highest <see cref="IPrefabCatalogue.DrillYieldScore"/> so the
        /// name matches the hull <see cref="ShipPool.Consume"/> would have preferred; ordinal tie-break
        /// keeps it deterministic. Diagnostic only -- never gates a dispatch.</summary>
        private static string DescribeRefusedMineHull(IOperationsExecutionContext ctx, IPrefabCatalogue cat,
            string nodeId, ShipPool.RouteEnvironment? route, ShipPool.RouteEnvironment? deliveryRoute,
            out bool refusedOnDelivery)
        {
            string best = null;
            double bestScore = double.NegativeInfinity;
            refusedOnDelivery = false;
            foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(nodeId))
            {
                if (kv.Value <= 0) continue;
                if (!ShipPool.IsMineSuitable(cat, kv.Key)) continue;
                bool pickupOk = ShipPool.IsRouteFeasible(cat, kv.Key, route);
                if (pickupOk && ShipPool.IsRouteFeasible(cat, kv.Key, deliveryRoute)) continue;
                double score = cat != null ? cat.DrillYieldScore(kv.Key) : 0;
                if (best == null || score > bestScore
                    || (score == bestScore && string.CompareOrdinal(kv.Key, best) < 0))
                {
                    best = kv.Key;
                    bestScore = score;
                    refusedOnDelivery = pickupOk;
                }
            }
            return best;
        }

        /// <summary>Resolves a liters/second mining rate for <paramref name="prefabId"/> from
        /// <see cref="IPrefabCatalogue.DrillYieldScore"/> (the summed swept cross-section of the
        /// prefab's drill blocks -- see <see cref="MineTargeting.DrillBlockScore"/>)
        /// scaled by <see cref="FactionTuning.MineDrillFallbackLitersPerScorePoint"/>. Deliberately
        /// the only rate source -- an earlier "real physical reading" path (a companion interface
        /// reading a drill's raw <c>CutOutRadius</c>/<c>Speed</c> block fields directly) was removed
        /// after live testing showed near-instant Mine dwell times; decompiled SE source confirmed
        /// <c>Speed</c> is a rendering-only field (drives the drill head's visual spin animation), not
        /// a cutting-rate multiplier, so that path was built on a wrong physical assumption, not just
        /// an uncalibrated one. A tuning-owned scalar also keeps Mine's own pacing independent of any
        /// live/mod-adjustable SE mining mechanic (e.g. a server-side "mining tick rate" plugin).
        ///
        /// <para>Since FAC-228 the conversion is derived rather than fitted: the score is the summed
        /// <c>CutOutRadius^3</c> of the prefab's drill blocks (m^3),
        /// <see cref="FactionTuning.MineDrillLitersPerScorePoint"/> carries SE's own cut cadence and
        /// harvest ratio, and <see cref="FactionTuning.MineDutyCycle"/> is the single operator scalar
        /// -- the fraction of a dwell actually spent cutting fresh ore. The per-ore density term is
        /// NOT here: it depends on what is being targeted, so it lives in
        /// <see cref="MineTargeting.NodeRichnessFactor"/> beside the node.</para></summary>
        private static double ResolveDrillRate(IPrefabCatalogue catalogue, string prefabId, FactionTuning tuning)
        {
            if (catalogue == null || string.IsNullOrEmpty(prefabId)) return 0;
            return catalogue.DrillYieldScore(prefabId) * tuning.MineDrillLitersPerScorePoint
                 * tuning.MineDutyCycle;
        }

        /// <summary>The hull's own drill rate for <c>MINE_DISPATCHED</c>, in L/s: before the rock's
        /// richness and density, so the figure names the hull and not the deposit.</summary>
        private static string FormatDrillRate(IPrefabCatalogue catalogue, string prefabId, FactionTuning tuning)
        {
            return ResolveDrillRate(catalogue, prefabId, tuning).ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>Stages the <c>Ship:Mine</c> demand this dispatch could not serve.
        ///
        /// <para>The via node is the catchment's hardest gravity, not <paramref name="sourceNode"/>.
        /// <c>ShipyardManager.SelectPrefabBom</c> gates its candidate pool on exactly the demand's
        /// (node, dest, via) nodes, so the via is what decides which hulls are buildable — and a Mine
        /// hull serves a rotating set of deposits, so today's winner is one sample of what it has to
        /// survive. Live 2026-08-31, ISBT staged this with <c>via=N007</c>, its own shipyard 346 km
        /// out past the gas giant's gravity limit: at g = 0 <see cref="ThrustPhysics.DryTwr"/>
        /// short-circuits to +Infinity, the filter refused nothing, and cheapest-first minted a hull
        /// that could not lift off two thirds of the catchment.</para>
        ///
        /// <para>Falls back to <paramref name="sourceNode"/> whenever nothing in the catchment is
        /// strictly harder to lift off than it — including a wholly deep-space estate, where that is
        /// the previous behaviour exactly.</para>
        ///
        /// <para>Posts no cargo floor (the <c>1</c> sentinel): every trip fills whatever hold flies
        /// it, so any drill-equipped hull serves, and the build is ranked by drill rate per BOM cost
        /// (<c>ShipyardManager</c>). Rate-limited by <see cref="StageFallback"/>'s per-pass key and
        /// the demand register's refresh, not here.</para></summary>
        private void StageMineFallback(FactionTickContext context, string factionTag, SpatialNode sourceNode,
            SpatialNode destNode, List<SpatialNode> ownedNodes, WorldModel world, FactionTuning tuning)
        {
            string shipyardNodeId = GetShipyard(context, factionTag, sourceNode.Id, world);
            // MineTargeting.HardestGravityDeposit, with its incumbent-free half kept per destination
            // for the pass: an order walk stages this once per blocked deposit, all around one hub.
            KeyValuePair<SpatialNode, double> hardest;
            if (world.Planets == null || tuning == null)
                hardest = new KeyValuePair<SpatialNode, double>(null, 0);
            else if (!context.HardestGravityByAnchor.TryGetValue(destNode.Id, out hardest))
            {
                double g;
                SpatialNode n = MineTargeting.HardestInCatchment(ownedNodes, destNode.Position, world.Planets, tuning, out g);
                hardest = new KeyValuePair<SpatialNode, double>(n, g);
                context.HardestGravityByAnchor[destNode.Id] = hardest;
            }
            SpatialNode viaNode = MineTargeting.ChallengeIncumbent(hardest.Key, hardest.Value, sourceNode, world.Planets);
            StageFallback(context, factionTag, "Mine", shipyardNodeId ?? sourceNode.Id, destNode.Id,
                1, viaNode.Id);
        }

        /// <summary>Per-(faction, deposit) last logged Mine block reason. Log-on-change dedup only:
        /// an order re-attempts a blocked deposit every ops cycle, and an unchanged block reason must not
        /// re-log each time. Unstaged manager state -- a guard-discarded staged cycle can consume one
        /// transition (one suppressed re-log), never affect a dispatch.</summary>
        private readonly Dictionary<string, string> _lastMineBlockSignature =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Logs a Mine block once per (faction, deposit) per change of
        /// <paramref name="signature"/>.
        ///
        /// <para><paramref name="measurements"/> is rendered into the line but never into the dedup
        /// key. A block reason that carries a continuously varying quantity would otherwise present a
        /// different signature on every cycle and defeat the dedup entirely: the same block, restated
        /// with drifting decimals, for as long as it lasts. Identifiers that name <i>which</i>
        /// situation this is -- an alternate node, a refused hull -- belong in the signature, because
        /// a different one is a different block worth a line.</para></summary>
        private void LogMineBlockedOnChange(IOperationsExecutionContext ctx, string factionTag,
            string sourceNodeId, string destNodeId, string signature, string measurements = null)
        {
            string key = factionTag + "|" + sourceNodeId;
            string existing;
            if (_lastMineBlockSignature.TryGetValue(key, out existing) && existing == signature) return;
            _lastMineBlockSignature[key] = signature;
            ctx.AddLog(factionTag + " MINE_DISPATCH_BLOCKED src=" + sourceNodeId + " dest=" + destNodeId
                    + " reason=" + signature
                    + (string.IsNullOrEmpty(measurements) ? "" : " " + measurements)
                    + " -- Ship:Mine demand staged");
        }

        /// <summary>Clears the block signature on a successful dispatch (no log) so a later re-block
        /// with the same reason logs again instead of being deduped across an intervening success.</summary>
        private void MarkMineDispatched(string factionTag, string sourceNodeId)
        {
            _lastMineBlockSignature[factionTag + "|" + sourceNodeId] = "dispatched";
        }

        /// <summary>Whether a <c>no_ship_anywhere</c> Mine block may realise itself as a bootstrap
        /// mint this cycle. Evaluated ONCE per faction per cycle beside Haul's own
        /// <c>hasCargoShipAnywhere</c>, because the ownership reading scans
        /// <see cref="FactionState.PendingFleets"/> and the Mine pass runs once per destination.
        ///
        /// <para><b>Ownership, not availability</b> -- see <see cref="NetworkOwnsSuitable"/>. A
        /// faction whose one drill is out on an errand owns a drill, and gets nothing here: on the
        /// availability reading this would make mining cheaper the fewer hulls you own.</para>
        ///
        /// <para>Never for a player faction. Unlike Haul's bootstrap, which relocates stock that
        /// already exists, this one creates matter; players mine with real drills.</para>
        ///
        /// <para>The two tuning reads are the disable switches, ordered before the scan so a
        /// switched-off fallback costs nothing:
        /// <see cref="FactionTuning.MineBootstrapYieldFraction"/> at 0 turns it off, and a
        /// <see cref="FactionTuning.FreeMinerCargoVolumeLiters"/> the host never stamped leaves no
        /// hold to size a mint from.</para></summary>
        private static bool MineBootstrapAllowed(FactionState st, List<SpatialNode> ownedNodes,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning)
        {
            if (st == null || tuning == null || world == null) return false;
            if (st.IsPlayerFaction) return false;
            if (tuning.MineBootstrapYieldFraction <= 0) return false;
            if (tuning.FreeMinerCargoVolumeLiters <= 0) return false;
            return !NetworkOwnsSuitable(ctx, st, ownedNodes, ActionKind.Mine, world.PrefabCatalogue);
        }

        /// <summary>Bootstrap-only realizer for Mine, the counterpart of
        /// <see cref="RealizeBootstrapInstant"/>: an instant, accounting-only mint at the
        /// DESTINATION for a faction that owns no Mine-suitable hull anywhere.
        ///
        /// <para>The deadlock it breaks: raw ore has no producing recipe, so Haul can only shuffle
        /// ore that already exists and the only way to put new ore in the ground-to-refinery chain
        /// is a Mine dispatch, which needs a drill -- which needs a refinery fed with ore to build.
        /// Measured live 2026-09-06 on four factions holding single-digit units of iron at a
        /// shipyard whose head build was the very drill that would have ended it.</para>
        ///
        /// <para>Sized at <see cref="FactionTuning.MineBootstrapYieldFraction"/> of the free-miner
        /// hold rather than at any hull's capacity -- there is no hull -- capped by
        /// <paramref name="requestedTotalLiters"/> when one is given (an NPC extraction order asks
        /// for a full hold, 0, so it takes the whole bootstrap), and split across <paramref name="targetWeights"/> with the same
        /// liters-to-units arithmetic a real Mine dwell uses. Lands at the destination, not the
        /// deposit, because the refinery that posted the demand reads the destination.</para>
        ///
        /// <para>DemandHistory is NOT credited, for the same reason
        /// <see cref="RealizeBootstrapInstant"/> does not credit it: gravity is earned on
        /// consumption, not on delivery.</para>
        ///
        /// <para>The source deposit takes a real
        /// <see cref="SpatialNode.MineCooldownUntilUtc"/> stamp, which is what rate-limits this --
        /// <see cref="TryFireMine"/> refuses a cooling node, so one seam cannot be tapped every
        /// ops cycle, and an extraction order stops its scan at the first no-hull outcome
        /// (<see cref="WalkMineCatchment"/>), so one order mints at most once per pass.
        /// Self-terminating otherwise: the first Mine-suitable hull the faction owns flips
        /// <see cref="MineBootstrapAllowed"/> and this never fires again.</para></summary>
        private void RealizeMineBootstrapInstant(string factionTag, SpatialNode sourceNode, SpatialNode destNode,
            Dictionary<string, double> targetWeights, double requestedTotalLiters,
            IOperationsExecutionContext ctx, FactionTuning tuning)
        {
            double capLiters = tuning.FreeMinerCargoVolumeLiters * tuning.MineBootstrapYieldFraction;
            if (capLiters <= 0) return;
            double totalLiters = requestedTotalLiters > 0 && requestedTotalLiters < capLiters
                ? requestedTotalLiters : capLiters;

            var minted = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, double> kv in targetWeights)
            {
                double litersPerUnit = _volumes != null ? _volumes.VolumeLitersPerUnit(kv.Key) : 0;
                if (litersPerUnit <= 0) continue;
                long qty = (long)(totalLiters * kv.Value / litersPerUnit);
                if (qty <= 0) continue;
                ctx.AddNodeInventory(destNode.Id, kv.Key, qty);
                minted.Append(kv.Key).Append('x').Append(qty).Append(' ');
            }
            // Nothing to charge a cooldown for: no item in the basket had volume data, or every
            // share rounded to zero units.
            if (minted.Length == 0) return;

            ctx.SetLastMinedUtc(sourceNode.Id, ctx.UtcNow);
            ctx.SetMineCooldownUntilUtc(sourceNode.Id, ctx.UtcNow.AddSeconds(tuning.MineBootstrapCooldownSeconds));

            // Unconditional, unlike ITEM_XFER_BOOTSTRAP's per-vector line: this fires at most once
            // per source per cooldown, so it is as low-volume as MINE_DISPATCHED.
            ctx.AddLog(factionTag + " MINE_BOOTSTRAP src=" + sourceNode.Id + " dest=" + destNode.Id
                    + " " + minted.ToString().TrimEnd()
                    + " cooldown=" + tuning.MineBootstrapCooldownSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                    + " (no drill in network yet)");
        }

        /// <summary>Flies one Mine trip from <paramref name="sourceNode"/> to
        /// <paramref name="destNode"/>. Weighs Tier 1 (a hull already at the source dwells right
        /// there) against Tier 2 (the best Mine-suitable hull at another owned node travels to the
        /// source first, then dwells) and flies the better hull by <see cref="MineHullOutranks"/> --
        /// doctrine, then drill rate, then distance, so Tier 1 keeps every tie. Neither tier finds a
        /// drill-equipped, route-feasible hull -> stages a <c>Ship:Mine</c> demand
        /// (<see cref="StageMineFallback"/>) and, for a faction that owns no drill at all and
        /// <paramref name="allowBootstrap"/>, mints the bootstrap basket
        /// (<see cref="RealizeMineBootstrapInstant"/>). Returns the real outcome (see
        /// <see cref="MineDispatchOutcome"/>) so a caller can tell a trip that flew from a demand
        /// that was staged. <paramref name="requestedTotalLiters"/> 0 fills the hold
        /// (<see cref="ClampTargetLiters"/>); every caller asks for that since the NPC demand-row
        /// trigger was retired. <paramref name="orderTag"/> names the NPC extraction order the trip
        /// serves, appended to <c>MINE_DISPATCHED</c>; null for a player trip.</summary>
        private MineDispatchOutcome DispatchMine(FactionState st, SpatialNode sourceNode, SpatialNode destNode,
            Dictionary<string, double> targetWeights, double requestedTotalLiters, List<SpatialNode> ownedNodes,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning, FactionTickContext context,
            bool allowBootstrap, string orderTag = null, List<SpatialNode> hullNodes = null)
        {
            if (targetWeights == null || targetWeights.Count == 0) return MineDispatchOutcome.NoShipAvailable;
            string factionTag = st.Tag;

            // A fight closes its node to Mine (FAC-384): nothing is dispatched to or from it, and no
            // demand is staged -- the hull exists, the node is busy.
            if (world.ClosedForCombat.Count > 0
                && (world.ClosedForCombat.Contains(sourceNode.Id) || world.ClosedForCombat.Contains(destNode.Id)))
            {
                string key = factionTag + "|" + sourceNode.Id;
                string existing;
                if (!_lastMineBlockSignature.TryGetValue(key, out existing) || existing != "combat")
                {
                    _lastMineBlockSignature[key] = "combat";
                    ctx.AddLog(factionTag + " MINE_DISPATCH_BLOCKED src=" + sourceNode.Id + " dest=" + destNode.Id
                               + " reason=combat");
                }
                return MineDispatchOutcome.ClosedForCombat;
            }

            ShipPool.RouteEnvironment route = GetRoute(context, sourceNode, destNode, world, tuning);
            IPrefabCatalogue cat = world.PrefabCatalogue;

            // The fastest idle drill that can fly the trip, wherever it is parked within the search --
            // not whichever hull happens to be standing on the deposit (FAC-395). The hull at the
            // source keeps the trip on a tie: it is the nearest, and it needs no pickup leg.
            HashSet<string> doctrinePrefabs = MineDoctrinePrefabs(st, cat);
            MineHull local;
            bool hasLocal = BestMineHullAt(ctx, cat, sourceNode.Id, route, null, doctrinePrefabs, out local);
            MineAlternate alt = FindMineAlternateShipNode(sourceNode.Id, ownedNodes, route, ctx,
                cat, context, world, tuning, doctrinePrefabs, hullNodes);
            // Only a strictly better hull is fetched: on doctrine and rate alike the one already at
            // the source is the nearest by definition, whatever the alternate's node id.
            bool fetch = alt.NodeId != null
                && (!hasLocal
                    || (alt.Hull.Doctrine != local.Doctrine ? alt.Hull.Doctrine : alt.Hull.Score > local.Score));

            if (!fetch)
            {
                string prefabId = hasLocal ? local.PrefabId : null;
                int consumed = hasLocal ? ctx.ConsumeShipPrefab(sourceNode.Id, local.PrefabId, 1) : 0;
                // Defensive: the rank above applies Consume's own eligibility, so this only runs if the
                // two ever disagree -- and then the generic pick is still a real hull at the source.
                if (consumed <= 0)
                    consumed = ctx.ConsumeShip(sourceNode.Id, ActionKind.Mine, cat, 1, out prefabId, route);
                if (consumed > 0)
                    return DispatchMineDirect(factionTag, sourceNode, destNode, targetWeights, requestedTotalLiters, prefabId, ownedNodes, ctx, world, tuning, context, orderTag);
            }

            if (alt.NodeId == null)
            {
                StageMineFallback(context, factionTag, sourceNode, destNode, ownedNodes, world, tuning);
                if (alt.BlockedNodeId == null)
                {
                    // The block line stands whether or not the bootstrap fires: the hull is still
                    // missing, and the mint below is what is being done about it meanwhile.
                    LogMineBlockedOnChange(ctx, factionTag, sourceNode.Id, destNode.Id, "no_ship_anywhere");
                    if (allowBootstrap)
                        RealizeMineBootstrapInstant(factionTag, sourceNode, destNode, targetWeights,
                            requestedTotalLiters, ctx, tuning);
                    return MineDispatchOutcome.NoShipAvailable;
                }
                // Drill stock exists and no hull anywhere survives a leg of the itinerary. Name the
                // one that actually failed -- the pickup hop alt->source, or the delivery leg the
                // hull would be left standing at the far end of.
                ShipPool.MaybePostLocalShipyardInvestment(ctx, tuning, st, sourceNode, destNode, "mine");
                LogMineBlockedOnChange(ctx, factionTag, sourceNode.Id, destNode.Id,
                    "route_infeasible alt=" + alt.BlockedNodeId
                        + (alt.BlockedPrefabId != null ? " prefab=" + alt.BlockedPrefabId : "")
                        + " leg=" + (alt.BlockedOnDelivery
                            ? sourceNode.Id + "->" + destNode.Id
                            : alt.BlockedNodeId + "->" + sourceNode.Id));
                return MineDispatchOutcome.NoShipRouteInfeasible;
            }

            SpatialNode altNode;
            if (!world.Graph.Nodes.TryGetValue(alt.NodeId, out altNode)) return MineDispatchOutcome.NoShipAvailable;

            return DispatchMineViaPickup(st, sourceNode, destNode, altNode, alt.Hull.PrefabId, targetWeights, requestedTotalLiters, ownedNodes, route, ctx, world, tuning, context, orderTag);
        }

        /// <summary>Computes the dwell duration (gather + overburden) for a target volume at
        /// <paramref name="prefabId"/>'s real rate and <paramref name="sourceNode"/>'s richness for
        /// <paramref name="targetWeights"/> -- shared by both dispatch tiers. Returns 0 dwell seconds
        /// (caller must treat as "can't mine") when the effective rate is non-positive.
        ///
        /// <para>Takes neither the faction nor an execution context since FAC-228: with influence
        /// control out of the rate, extraction speed depends only on the hull and the rock, so who is
        /// mining cannot change how long it takes.</para></summary>
        private static double ComputeMineDwellSeconds(SpatialNode sourceNode, double totalTargetLiters,
            Dictionary<string, double> targetWeights, string prefabId,
            WorldModel world, FactionTuning tuning, out double effectiveLitersPerSecond)
        {
            // No InfluenceControl read here since FAC-228: a drill cuts the same rock at the same
            // speed whoever lives next door, and this was a per-pair O(sources) query on the hot path.
            double richness = MineTargeting.NodeRichnessFactor(sourceNode, tuning, targetWeights);
            double drillBaseLitersPerSecond = ResolveDrillRate(world.PrefabCatalogue, prefabId, tuning);
            effectiveLitersPerSecond = drillBaseLitersPerSecond * richness;
            if (effectiveLitersPerSecond <= 0) return 0;

            double gatherSeconds = totalTargetLiters / effectiveLitersPerSecond;
            double overburdenSeconds = gatherSeconds * tuning.MineOverburdenFraction;
            return gatherSeconds + overburdenSeconds;
        }

        private MineDispatchOutcome DispatchMineDirect(string factionTag, SpatialNode sourceNode, SpatialNode destNode,
            Dictionary<string, double> targetWeights, double requestedTotalLiters, string prefabId,
            List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx, WorldModel world,
            FactionTuning tuning, FactionTickContext context, string orderTag = null)
        {
            prefabId = prefabId ?? "";
            long cargoCapacityLiters = world.PrefabCatalogue != null && prefabId.Length > 0
                ? world.PrefabCatalogue.CargoLiters(prefabId) : 0;
            double totalTargetLiters = ClampTargetLiters(requestedTotalLiters, cargoCapacityLiters);

            double effectiveLitersPerSecond;
            double dwellSeconds = ComputeMineDwellSeconds(sourceNode, totalTargetLiters, targetWeights,
                prefabId, world, tuning, out effectiveLitersPerSecond);
            if (effectiveLitersPerSecond <= 0)
            {
                ctx.ReturnShip(sourceNode.Id, prefabId, 1);
                StageMineFallback(context, factionTag, sourceNode, destNode, ownedNodes, world, tuning);
                LogMineBlockedOnChange(ctx, factionTag, sourceNode.Id, destNode.Id, "drill_rate_zero");
                return MineDispatchOutcome.NoShipAvailable;
            }

            DateTime departed = ctx.UtcNow;
            var mine = new PendingFleetAction
            {
                Kind                = ActionKind.Mine,
                ActorTag            = factionTag,
                OriginNodeId        = sourceNode.Id,
                TargetNodeId        = sourceNode.Id, // dwelling at the source for this initial leg
                OriginPosKm         = sourceNode.Position,
                TargetPosKm         = sourceNode.Position,
                DepartedUtc         = departed,
                EstimatedArrivalUtc = departed.AddSeconds(dwellSeconds),
                CargoCapacityLiters = cargoCapacityLiters,
                ConvoySize          = 1,
                ShipPrefabId        = prefabId,
                CurrentStopKind     = FleetStopKind.Mine,
                MineWeights         = new Dictionary<string, double>(targetWeights),
                MineTargetLiters    = totalTargetLiters,
                MineWanderUp        = MineWander.UpAt(world.Planets, sourceNode.Position),
                RemainingStops      = new List<FleetStop>
                {
                    new FleetStop { NodeId = destNode.Id, Kind = FleetStopKind.Service }
                },
                FleetId             = ctx.MintFleetId(factionTag, departed, destNode.Id, sourceNode.Id),
                LastResolvedSegment = -1,
            };
            ctx.AppendPendingFleet(mine);
            RecordMineDispatch(context, sourceNode.Id, destNode.Id, targetWeights, totalTargetLiters, dwellSeconds);

            ctx.AddLog(mine.LogId + " " + factionTag + " MINE_DISPATCHED " + sourceNode.Id + " -> " + destNode.Id
                    + " target=" + totalTargetLiters.ToString("0") + "L"
                    + " dwell=" + dwellSeconds.ToString("0") + "s"
                    + (prefabId.Length > 0 ? " prefab=" + prefabId : "")
                    + " drillRate=" + FormatDrillRate(world.PrefabCatalogue, prefabId, tuning)
                    + (orderTag != null ? " order=" + orderTag : ""));
            MarkMineDispatched(factionTag, sourceNode.Id);
            return MineDispatchOutcome.Dispatched;
        }

        /// <summary><paramref name="deliveryRoute"/> is the source→dest pair the direct tier already
        /// sampled: the fetched hull flies alt→source→dest, and a gate on the pickup hop alone lets
        /// it be sent to a delivery node it cannot hold altitude at.</summary>
        private MineDispatchOutcome DispatchMineViaPickup(FactionState st, SpatialNode sourceNode, SpatialNode destNode,
            SpatialNode altNode, string chosenPrefabId, Dictionary<string, double> targetWeights, double requestedTotalLiters,
            List<SpatialNode> ownedNodes, ShipPool.RouteEnvironment deliveryRoute,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning, FactionTickContext context,
            string orderTag = null)
        {
            string factionTag = st.Tag;
            ShipPool.RouteEnvironment pickupRoute = GetRoute(context, altNode, sourceNode, world, tuning);
            // The hull FindMineAlternateShipNode ranked, already checked against both legs; the
            // generic pick stays as the fallback for a caller that names none.
            string prefabId = chosenPrefabId;
            int consumed = !string.IsNullOrEmpty(chosenPrefabId)
                ? ctx.ConsumeShipPrefab(altNode.Id, chosenPrefabId, 1) : 0;
            if (consumed <= 0)
                consumed = ctx.ConsumeShip(altNode.Id, ActionKind.Mine, world.PrefabCatalogue, 1, out prefabId,
                    pickupRoute, deliveryRoute);
            if (consumed <= 0)
            {
                // Defensive: DispatchMine only reaches here with a node whose stock already cleared
                // this exact route gate. Kept so a future caller that picks its own alt node still
                // gets the fallback + investment nudge instead of a silent no-op.
                StageMineFallback(context, factionTag, sourceNode, destNode, ownedNodes, world, tuning);
                ShipPool.MaybePostLocalShipyardInvestment(ctx, tuning, st, sourceNode, destNode, "mine");
                bool refusedOnDelivery;
                string refused = DescribeRefusedMineHull(ctx, world.PrefabCatalogue, altNode.Id,
                    pickupRoute, deliveryRoute, out refusedOnDelivery);
                LogMineBlockedOnChange(ctx, factionTag, sourceNode.Id, destNode.Id,
                    "route_infeasible alt=" + altNode.Id
                        + (refused != null ? " prefab=" + refused : "")
                        + " leg=" + (refusedOnDelivery
                            ? sourceNode.Id + "->" + destNode.Id
                            : altNode.Id + "->" + sourceNode.Id));
                return MineDispatchOutcome.NoShipRouteInfeasible;
            }
            prefabId = prefabId ?? "";

            long cargoCapacityLiters = world.PrefabCatalogue != null && prefabId.Length > 0
                ? world.PrefabCatalogue.CargoLiters(prefabId) : 0;
            double totalTargetLiters = ClampTargetLiters(requestedTotalLiters, cargoCapacityLiters);

            double effectiveLitersPerSecond;
            double dwellSeconds = ComputeMineDwellSeconds(sourceNode, totalTargetLiters, targetWeights,
                prefabId, world, tuning, out effectiveLitersPerSecond);
            if (effectiveLitersPerSecond <= 0)
            {
                ctx.ReturnShip(altNode.Id, prefabId, 1);
                StageMineFallback(context, factionTag, sourceNode, destNode, ownedNodes, world, tuning);
                LogMineBlockedOnChange(ctx, factionTag, sourceNode.Id, destNode.Id, "drill_rate_zero");
                return MineDispatchOutcome.NoShipAvailable;
            }

            double distKm = altNode.Position.DistanceTo(sourceNode.Position);
            DateTime departed = ctx.UtcNow;
            DateTime arrivalAtSource = tuning.FleetSpeedKmPerMin > 0
                ? departed.AddMinutes(tuning.TransitTimeMins(distKm))
                : departed;

            var mine = new PendingFleetAction
            {
                Kind                = ActionKind.Mine,
                ActorTag            = factionTag,
                OriginNodeId        = altNode.Id,
                TargetNodeId        = sourceNode.Id,
                OriginPosKm         = altNode.Position,
                TargetPosKm         = sourceNode.Position,
                DepartedUtc         = departed,
                EstimatedArrivalUtc = arrivalAtSource,
                CargoCapacityLiters = cargoCapacityLiters,
                ConvoySize          = 1,
                ShipPrefabId        = prefabId,
                CurrentStopKind     = FleetStopKind.Navigation, // travel to the source first, then dwell there
                RemainingStops      = new List<FleetStop>
                {
                    new FleetStop { NodeId = sourceNode.Id, Kind = FleetStopKind.Mine,
                        DurationSeconds = dwellSeconds, MineWeights = new Dictionary<string, double>(targetWeights),
                        MineTargetLiters = totalTargetLiters },
                    new FleetStop { NodeId = destNode.Id, Kind = FleetStopKind.Service }
                },
                FleetId             = ctx.MintFleetId(factionTag, departed, sourceNode.Id, altNode.Id),
                LastResolvedSegment = -1,
            };
            mine = PlanTransitAndLeg(mine, ctx, world, tuning, sourceNode.Id, destNode.Id, false);
            ctx.AppendPendingFleet(mine);
            RecordMineDispatch(context, sourceNode.Id, destNode.Id, targetWeights, totalTargetLiters, dwellSeconds);

            ctx.AddLog(mine.LogId + " " + factionTag + " MINE_DISPATCHED " + sourceNode.Id + " -> " + destNode.Id
                    + " via_pickup=" + altNode.Id
                    + " target=" + totalTargetLiters.ToString("0") + "L"
                    + " dwell=" + dwellSeconds.ToString("0") + "s"
                    + " eta_pickup=" + arrivalAtSource.ToString("HH:mm:ss")
                    + (prefabId.Length > 0 ? " prefab=" + prefabId : "")
                    + " drillRate=" + FormatDrillRate(world.PrefabCatalogue, prefabId, tuning)
                    + (orderTag != null ? " order=" + orderTag : ""));
            MarkMineDispatched(factionTag, sourceNode.Id);
            return MineDispatchOutcome.Dispatched;
        }

        /// <summary>Caps a requested target volume at the dispatched ship's real cargo capacity (once
        /// known), same "the ship's own hold is the natural ceiling" idea as every other cargo-loading
        /// call site in this file. Falls back to the cargo capacity itself (or 1L as a last resort) when
        /// no positive request was given (the player-triggered "just fill the hold" case).</summary>
        private static double ClampTargetLiters(double requestedTotalLiters, long cargoCapacityLiters)
        {
            if (requestedTotalLiters <= 0)
                return cargoCapacityLiters > 0 ? cargoCapacityLiters : 1;
            return cargoCapacityLiters > 0 && requestedTotalLiters > cargoCapacityLiters
                ? cargoCapacityLiters : requestedTotalLiters;
        }

        // ---- standing-order Mine dispatch: player orders, NPC extraction orders, one-shot ---------
        // See docs/economy-territory.md "Player-faction claiming" and docs/managers.md's Mine section.
        // Every Mine trip routes through TryFireMine into the same DispatchMine machinery: a player's
        // hand-authored standing orders (ScanStandingMineOrder), an NPC's planner-authored extraction
        // orders (ScanExtractionOrder, docs/mining-plan.md), and the one-shot `station mine` command.
        // Both kinds of order walk their hub's catchment with the same WalkMineCatchment.

        /// <summary>Used only by the one-shot manual trigger (<see cref="TriggerPlayerMine"/>) when the
        /// player omits an explicit destination: sums live shortfall across the whole
        /// <paramref name="targetWeights"/> set per destination, and the biggest combined shortfall
        /// wins. In-flight-Haul de-duplication is deliberately skipped rather than rebuilding
        /// TickOneFaction's full claims index for a rare, ad hoc command -- worst case a slightly
        /// suboptimal destination pick, never a correctness issue. No live demand -> falls back to
        /// <paramref name="sourceNodeId"/> itself (a local top-up, a legitimate zero-distance
        /// dispatch). Never used by a standing or extraction order -- its destination is always its
        /// hub.</summary>
        private static string ResolveMineDestination(string factionTag, Dictionary<string, double> targetWeights,
            string sourceNodeId, IOperationsExecutionContext ctx, FactionTickContext context)
        {
            var demandKinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string subTypeId in targetWeights.Keys) demandKinds.Add("Item:" + subTypeId);

            var shortfallByDestination = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (DemandEntry d in ctx.SnapshotItemDemands(factionTag))
            {
                if (d.NodeId == null || !demandKinds.Contains(d.Kind)) continue;
                string subTypeId = d.Kind.Substring(5); // "Item:X" -> "X"
                long have = ctx.GetNodeInventory(d.NodeId, subTypeId);
                long committedThisCycle;
                context.DispatchedThisCycle.TryGetValue((d.NodeId, subTypeId), out committedThisCycle);
                long shortfall = d.Quantity - have - committedThisCycle;
                if (shortfall <= 0) continue;
                long cur;
                shortfallByDestination.TryGetValue(d.NodeId, out cur);
                shortfallByDestination[d.NodeId] = cur + shortfall;
            }

            string best = null;
            long bestShortfall = 0;
            foreach (var kv in shortfallByDestination)
            {
                if (best == null || kv.Value > bestShortfall
                    || (kv.Value == bestShortfall && string.CompareOrdinal(kv.Key, best) < 0))
                {
                    best = kv.Key;
                    bestShortfall = kv.Value;
                }
            }
            return best ?? sourceNodeId;
        }

        /// <summary>Shared validate-and-dispatch core for both the one-shot manual trigger and the
        /// standing-order scan. <paramref name="requestedWeights"/> (SubTypeId -&gt; percentage, not
        /// necessarily normalized) drives the actual mined composition when given -- see
        /// <see cref="MineTargeting.ComputeTargetWeights"/>; null/empty falls back to the node's own
        /// natural full mix. <paramref name="destNodeId"/> may be null (one-shot only) -- resolved via
        /// <see cref="ResolveMineDestination"/> when omitted. <paramref name="ownedNodes"/> may be null
        /// (one-shot only) -- built fresh when omitted; callers that already have the list (the
        /// standing-order scan, evaluating many candidates against the same owned-node set) should pass
        /// it to avoid rebuilding it per candidate. Validates: node exists -&gt; owned by
        /// <paramref name="st"/> (both ends, after resolution) -&gt; source is mineable and the
        /// requested ores (or its natural mix) actually intersect what it yields -&gt; off cooldown
        /// (<see cref="SpatialNode.MineCooldownUntilUtc"/> -- belongs to the node being mined, its
        /// deposit regenerating; says nothing about the destination) -&gt; calls the existing
        /// <see cref="DispatchMine"/> unchanged.</summary>
        private PlayerMineResult TryFireMine(FactionState st, string sourceNodeId, string destNodeId,
            Dictionary<string, double> requestedWeights, List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx,
            WorldModel world, FactionTuning tuning, FactionTickContext context, out MineDispatchOutcome dispatchOutcome,
            bool allowBootstrap = false, string orderTag = null, List<SpatialNode> hullNodes = null)
        {
            dispatchOutcome = MineDispatchOutcome.NoShipAvailable;

            SpatialNode sourceNode;
            if (string.IsNullOrEmpty(sourceNodeId) || !world.Graph.Nodes.TryGetValue(sourceNodeId, out sourceNode))
                return PlayerMineResult.SourceNotFound;
            if (!string.Equals(sourceNode.OwnerFactionTag, st.Tag, StringComparison.OrdinalIgnoreCase))
                return PlayerMineResult.SourceNotOwned;
            if (!NodeContent.IsMineable(sourceNode))
                return PlayerMineResult.SourceNotMineable;

            Dictionary<string, double> targetWeights = MineTargeting.ComputeTargetWeights(sourceNode, tuning, requestedWeights, world.Registry);
            if (targetWeights.Count == 0)
                return requestedWeights != null && requestedWeights.Count > 0
                    ? PlayerMineResult.InvalidOreWeights
                    : PlayerMineResult.SourceNotMineable;

            if (ctx.UtcNow < ctx.GetMineCooldownUntilUtc(sourceNode.Id))
                return PlayerMineResult.OnCooldown;

            if (string.IsNullOrEmpty(destNodeId))
                destNodeId = ResolveMineDestination(st.Tag, targetWeights, sourceNodeId, ctx, context);

            SpatialNode destNode;
            if (!world.Graph.Nodes.TryGetValue(destNodeId, out destNode))
                return PlayerMineResult.DestNotFound;
            if (!string.Equals(destNode.OwnerFactionTag, st.Tag, StringComparison.OrdinalIgnoreCase))
                return PlayerMineResult.DestNotOwned;

            if (ownedNodes == null)
            {
                ownedNodes = new List<SpatialNode>();
                foreach (SpatialNode n in ctx.OwnedNodes(st.Tag)) ownedNodes.Add(n);
            }

            // 0 = no explicit total requested -- DispatchMine/ClampTargetLiters treats that as "fill
            // the ship's own hold," for every order and the one-shot command alike.
            // allowBootstrap is true only for an NPC extraction order whose faction owns no drill
            // (MineBootstrapAllowed): the bootstrap mint creates matter, and a player-triggered or
            // standing Mine order must never be able to conjure ore out of a fleet it does not have.
            dispatchOutcome = DispatchMine(st, sourceNode, destNode, targetWeights, 0, ownedNodes, ctx, world, tuning, context,
                allowBootstrap && !st.IsPlayerFaction, orderTag, hullNodes);
            return PlayerMineResult.Ok;
        }

        /// <summary>Whether <paramref name="state"/> OWNS a hull anywhere in its estate suitable for
        /// <paramref name="kind"/> -- the same question, through the same rule, that the dispatch
        /// passes ask before forming any vector.
        ///
        /// <para>Exists so a player-facing readback can report the answer. <c>RunBuyPass</c> returns
        /// silently when this is false and increments no counter, so without a way to ask it
        /// directly "you own no trade hull" and "the feature is not running" are the same
        /// observation. Ownership, not availability, per FAC-199: a faction whose one hauler is out
        /// on an errand still owns it.</para>
        ///
        /// <para><paramref name="log"/> is OPTIONAL and defaults to a sink this method owns.
        /// <see cref="LiveOperationsExecutionContext"/>'s constructor rejects a null log outright,
        /// and every caller of this method is a read-only query with no log of its own to hand over
        /// -- so passing the parameter straight through made a null the natural thing to write and an
        /// exception the result. The throw surfaced as the ENTIRE node-detail snapshot failing, not
        /// as a missing field, because it happened while building one.</para></summary>
        public static bool OwnsSuitableHull(WorldModel world, FactionState state, ActionKind kind, List<string> log = null)
        {
            if (world == null || state == null) return false;
            var ownedNodes = new List<SpatialNode>();
            foreach (SpatialNode n in world.Graph.OwnedBy(state.Tag)) ownedNodes.Add(n);
            var ctx = new LiveOperationsExecutionContext(world, state, log ?? new List<string>());
            return NetworkOwnsSuitable(ctx, state, ownedNodes, kind, world.PrefabCatalogue);
        }

        /// <summary>One-shot entry point for the `!factions station mine` chat command (see
        /// <see cref="Commands.PlayerStationCommands"/> in the host) -- <paramref name="destNodeId"/>
        /// optional, pass null/empty to auto-resolve via <see cref="ResolveMineDestination"/>.
        /// <paramref name="requestedWeights"/> optional, pass null/empty for the node's natural full
        /// mix. Builds a fresh <see cref="LiveOperationsExecutionContext"/> (a trivial passthrough
        /// constructor -- the live host's real per-tick driver is the staged <c>TryAdvance</c> cycle
        /// guarded by <see cref="OperationsCycleCommitGuard"/>, which safely discards/restarts an
        /// in-flight cycle if this mutates state underneath it, so no separate staging is needed
        /// here). <paramref name="dispatchOutcome"/> is only meaningful when the return value is
        /// <see cref="PlayerMineResult.Ok"/> -- see <see cref="MineDispatchOutcome"/>'s doc comment;
        /// lets the host tell the player apart "a real ship is now working this" from "no
        /// route-feasible drill ship existed, queued a Ship:Mine demand instead" (both silently
        /// returned the exact same generic success message before this distinction existed).</summary>
        public PlayerMineResult TriggerPlayerMine(WorldModel world, FactionTuning tuning, FactionState state,
            string sourceNodeId, string destNodeId, Dictionary<string, double> requestedWeights, List<string> log,
            out MineDispatchOutcome dispatchOutcome)
        {
            var ctx = new LiveOperationsExecutionContext(world, state, log);
            var context = new FactionTickContext();
            PlayerMineResult result = TryFireMine(state, sourceNodeId, destNodeId, requestedWeights, null, ctx, world, tuning, context, out dispatchOutcome);
            if (result == PlayerMineResult.Ok) PublishFallbacks(tuning, context, ctx);
            return result;
        }

        /// <summary>Owned mineable nodes within <see cref="FactionTuning.MineSearchRadiusKm"/> of
        /// <paramref name="hub"/>'s position -- the catchment pool for one standing auto-mine order,
        /// ordered best-first for <paramref name="requestedWeights"/>.
        /// Membership shares <see cref="NodeContent.IsMineable"/> plus a "yields at least one Ore/RareOre
        /// item" check (via <see cref="MineTargeting.ComputeTargetWeights"/>'s natural-mix fallback),
        /// and is not keyed to one subtype -- every reachable owned deposit counts, since the
        /// hub-anchored order feeds whatever's nearby rather than one demand's specific item.
        ///
        /// <para><paramref name="requestedWeights"/> (the order's own
        /// <see cref="StandingOrder.OreWeights"/>, null for each node's natural mix) affects only the
        /// ORDER, never membership -- <c>Count</c> is the hub's concurrency budget, so filtering here
        /// would silently shrink it. An order asking for rare ore therefore reaches its RareOreFields
        /// before its OreFields when the hub has fewer open slots than deposits.</para>
        ///
        /// <para><paramref name="yieldingOnly"/> drops the deposits that score zero for
        /// <paramref name="requestedWeights"/>. Only an NPC extraction order sets it: it mines one ore
        /// and is bounded by its target rate, not by a slot count the filter could shrink.</para></summary>
        private static List<SpatialNode> FindMineCatchment(SpatialNode hub, List<SpatialNode> ownedNodes,
            FactionTuning tuning, Dictionary<string, double> requestedWeights, IItemRegistry registry,
            bool yieldingOnly = false)
        {
            var scored = new List<MineCandidate>();
            foreach (SpatialNode n in ownedNodes)
            {
                if (!IsMineCatchmentMember(n, tuning, registry)) continue;
                double dist = n.Position.DistanceTo(hub.Position);
                if (tuning.MineSearchRadiusKm > 0 && dist > tuning.MineSearchRadiusKm) continue;
                double score = MineTargeting.SelectionScore(n, tuning, requestedWeights, registry);
                // An extraction order mines one ore, and has no slot budget for a deposit that holds
                // none of it to spend: leaving it in would only log invalid_weights per pass.
                if (yieldingOnly && score <= 0) continue;
                scored.Add(new MineCandidate(n, score, dist));
            }

            // Richest first, nearest among equals -- the scan walks this order and stops when the
            // hub's open slots run out, so the order decides which deposits get the scarce fleets.
            // Membership is deliberately NOT filtered by score: the count is the concurrency budget
            // (see ScanStandingMineOrder), and dropping a poor deposit here would silently shrink it.
            scored.Sort(CompareMineCandidates);

            var result = new List<SpatialNode>(scored.Count);
            foreach (MineCandidate c in scored) result.Add(c.Node);
            return result;
        }

        /// <summary>Catchment membership, whatever the order asks for: a mineable deposit that
        /// yields at least one Ore/RareOre item.</summary>
        private static bool IsMineCatchmentMember(SpatialNode n, FactionTuning tuning, IItemRegistry registry)
        {
            return NodeContent.IsMineable(n)
                && MineTargeting.ComputeTargetWeights(n, tuning, null, registry).Count > 0;
        }

        private static int CompareMineCandidates(MineCandidate a, MineCandidate b)
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;
            cmp = a.Distance.CompareTo(b.Distance);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Node.Id, b.Node.Id);
        }

        /// <summary>Whether <paramref name="hub"/> could anchor a standing Mine order at all — that
        /// is, whether anything mineable this faction owns is within reach of it. Public so a
        /// player-facing surface can offer only hubs that would actually do something, using the
        /// same catchment rule the scan itself applies rather than a second guess at it.</summary>
        public static bool HasMineCatchment(SpatialNode hub, List<SpatialNode> ownedNodes, FactionTuning tuning,
            IItemRegistry registry = null)
        {
            if (hub == null || ownedNodes == null) return false;
            // Null weights: membership does not depend on them, only the returned order does.
            return FindMineCatchment(hub, ownedNodes, tuning, null, registry).Count > 0;
        }

        /// <summary>Counts this faction's own in-flight Mine fleets already headed toward
        /// <paramref name="hubNodeId"/> -- what enforces the standing order's "never more simultaneous
        /// fleets than there are distinct catchment deposits" concurrency ceiling, together with each
        /// candidate's own cooldown. Reuses <see cref="FactionState.PendingFleets"/> instead of
        /// inventing new bookkeeping. A Tier-2 pickup-route Mine dispatch (see
        /// <see cref="DispatchMineViaPickup"/>) sets <c>TargetNodeId</c> to the pickup stop (the mined
        /// source node), not the real destination -- the hub only appears as a queued
        /// <see cref="FleetStopKind.Service"/> stop, the exact same ambiguity <c>TickOneFaction</c>'s
        /// own in-flight-Haul index already resolves the same way.</summary>
        private static int CountInFlightMineFleetsToward(FactionState st, string hubNodeId)
        {
            int count = 0;
            foreach (PendingFleetAction f in st.PendingFleets)
            {
                if (f.Kind != ActionKind.Mine || f.Resolved || f.Intercepted) continue;
                // Under the dwell model the CURRENT leg's target is always the source node being
                // mined (Tier 1: dwelling there directly; Tier 2: traveling there via Navigation) --
                // the hub only ever appears as a queued Service stop, never as TargetNodeId itself.
                // The direct TargetNodeId==hubNodeId check stays only for a hand-seeded/synthetic
                // fleet (e.g. test fixtures) that skips the real dispatch path.
                if (f.TargetNodeId == hubNodeId) { count++; continue; }
                if (f.RemainingStops != null)
                    foreach (FleetStop stop in f.RemainingStops)
                        if (stop.Kind == FleetStopKind.Service && stop.NodeId == hubNodeId) { count++; break; }
            }
            return count;
        }

        /// <summary>The real lower bound on a useful rescan isn't a constant -- it's "whenever the next
        /// catchment node currently on cooldown actually clears," which is dynamic and shifts with
        /// which nodes fired when. Returns whichever comes first: that dynamic moment, or
        /// <paramref name="periodSecondsCeiling"/> from now (the player-set fallback poll ceiling,
        /// still worth rechecking on for newly-stocked Mine ships or newly-annexed catchment
        /// territory even when nothing is currently cooling down).</summary>
        private static DateTime ComputeNextScanUtc(List<SpatialNode> catchment, DateTime now,
            double periodSecondsCeiling, IOperationsExecutionContext ctx)
        {
            DateTime ceiling = now.AddSeconds(periodSecondsCeiling > 0 ? periodSecondsCeiling : 60.0);

            DateTime earliest = DateTime.MaxValue;
            foreach (SpatialNode n in catchment)
            {
                DateTime deadline = ctx.GetMineCooldownUntilUtc(n.Id);
                if (deadline > now && deadline < earliest) earliest = deadline;
            }
            return earliest < ceiling ? earliest : ceiling;
        }

        /// <summary>Accounting shared by every standing Mine order scanned in one faction pass.
        /// Two things need it, and neither is visible through the normal reads:
        ///
        /// <para><b>Claimed sources.</b> A real trip writes <see cref="SpatialNode.MineCooldownUntilUtc"/>
        /// at Mine DWELL RESOLUTION, not at dispatch (see that field's doc comment; the bootstrap
        /// mint's own stamp never applies here, since a standing order cannot bootstrap), so a
        /// deposit this pass just dispatched against still reads as available. Without this set,
        /// two orders whose catchments overlap would both fire at the same deposit in the same tick.
        /// The order that actually dispatched claims it; a later one is told which.
        ///
        /// <para>Only a REAL dispatch claims. A <see cref="PlayerMineResult.Ok"/> that staged a
        /// <c>Ship:Mine</c> demand instead of flying has taken nothing: the staged thing is a demand
        /// at a shipyard, keyed in the <see cref="DemandRegister"/> by
        /// <c>(faction, kind, node, dest)</c>, so a later order restaging it is a refresh rather than
        /// a second trip. Claiming on that flavour let an order that could never fly hold a whole
        /// shared catchment against every peer indefinitely (FAC-046, found live: one order claimed
        /// 10 of 11 deposits on every scan for 90 minutes while dispatching none of them).</para></para>
        ///
        /// <para><b>Dispatches per hub.</b> Two orders sharing a hub must share that hub's catchment
        /// budget — "never more simultaneous FLEETS than there are distinct deposits" — so this
        /// counts real dispatches only, for the same reason the claim set does. The count cannot
        /// simply be re-read per order:
        /// <see cref="CountInFlightMineFleetsToward"/> reads <see cref="FactionState.PendingFleets"/>,
        /// which the LIVE context appends to immediately and the STAGED context does not
        /// (<see cref="IOperationsExecutionContext.AppendPendingFleet"/> is write-only there). Adding
        /// this pass's own dispatches to a fresh read would therefore double-count under the live
        /// context and be correct under the staged one. So the hub's pre-pass in-flight count is
        /// captured ONCE, on first touch -- before this pass can have dispatched anything toward it,
        /// under either context -- and this pass's dispatches are added to that fixed baseline.</para></summary>
        private sealed class StandingMineScanPass
        {
            /// <summary>Source node id -&gt; the id of the order whose dispatch took it this pass.</summary>
            private readonly Dictionary<string, string> _claimedSources =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal bool TryGetClaimant(string sourceNodeId, out string orderId)
            {
                return _claimedSources.TryGetValue(sourceNodeId, out orderId);
            }
            private readonly Dictionary<string, int> _baselineByHub = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _dispatchedByHub = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Mine fleets already headed for <paramref name="hubNodeId"/>: the count as it
            /// stood when this pass first looked at that hub, plus what this pass has since sent
            /// there. Identical under the live and staged contexts, which is the whole point.</summary>
            internal int InFlightToward(FactionState st, string hubNodeId)
            {
                int baseline;
                if (!_baselineByHub.TryGetValue(hubNodeId, out baseline))
                {
                    baseline = CountInFlightMineFleetsToward(st, hubNodeId);
                    _baselineByHub[hubNodeId] = baseline;
                }
                int dispatched;
                _dispatchedByHub.TryGetValue(hubNodeId, out dispatched);
                return baseline + dispatched;
            }

            internal void RecordDispatch(string hubNodeId, string sourceNodeId, string orderId)
            {
                _claimedSources[sourceNodeId] = orderId;
                int dispatched;
                _dispatchedByHub.TryGetValue(hubNodeId, out dispatched);
                _dispatchedByHub[hubNodeId] = dispatched + 1;
            }

            // ---- per-pass indexes: built once, read by every order's walk ------------------------
            //
            // Each order used to rebuild both per call: its catchment by scoring every owned node
            // (FindMineCatchment), and each trip's drill search by testing every owned node for an
            // idle hull (FindMineAlternateShipNode). On IGSL's estate that is ~220 nodes x ~60 orders
            // for the first and x every attempted trip for the second, each pass. Neither answer
            // changes within a pass in a way that matters: see each member.

            private List<SpatialNode> _mineHullNodes;
            private List<SpatialNode> _mineableNodes;
            private Dictionary<string, double[]> _scoresByOre;

            /// <summary>The owned nodes that held an idle Mine hull when the pass first asked, in
            /// <paramref name="ownedNodes"/> order. A superset of the nodes holding one at any later
            /// point in the pass: a dispatch only takes hulls, and the one return
            /// (<see cref="IOperationsExecutionContext.ReturnShip"/>) gives a hull back to the node it
            /// was just taken from. So <see cref="FindMineAlternateShipNode"/> searching this list
            /// instead of every owned node finds the same hull.</summary>
            internal List<SpatialNode> MineHullNodes(IOperationsExecutionContext ctx, List<SpatialNode> ownedNodes,
                IPrefabCatalogue cat)
            {
                if (_mineHullNodes == null)
                {
                    _mineHullNodes = new List<SpatialNode>();
                    foreach (SpatialNode n in ownedNodes)
                        if (ctx.HasSuitableShip(n.Id, ActionKind.Mine, cat, null, null)) _mineHullNodes.Add(n);
                }
                return _mineHullNodes;
            }

            /// <summary><see cref="FindMineCatchment"/> for an extraction order -- <c>{ore: 1}</c>,
            /// yielding deposits only -- from membership and scores computed once per pass: which owned
            /// nodes are mineable deposits, and each one's selection score per ore. Both read only the
            /// ground, richness, hold strength and tuning, none of which an Operations pass writes.
            /// Same members, same order.</summary>
            internal List<SpatialNode> ExtractionCatchment(SpatialNode hub, string oreKey,
                Dictionary<string, double> weights, List<SpatialNode> ownedNodes, FactionTuning tuning,
                IItemRegistry registry)
            {
                if (_mineableNodes == null)
                {
                    _mineableNodes = new List<SpatialNode>();
                    foreach (SpatialNode n in ownedNodes)
                        if (IsMineCatchmentMember(n, tuning, registry)) _mineableNodes.Add(n);
                    _scoresByOre = new Dictionary<string, double[]>(StringComparer.Ordinal);
                }
                double[] scores;
                if (!_scoresByOre.TryGetValue(oreKey, out scores))
                {
                    scores = new double[_mineableNodes.Count];
                    for (int i = 0; i < scores.Length; i++)
                        scores[i] = MineTargeting.SelectionScore(_mineableNodes[i], tuning, weights, registry);
                    _scoresByOre[oreKey] = scores;
                }

                var scored = new List<MineCandidate>();
                for (int i = 0; i < _mineableNodes.Count; i++)
                {
                    if (scores[i] <= 0) continue;
                    SpatialNode n = _mineableNodes[i];
                    double dist = n.Position.DistanceTo(hub.Position);
                    if (tuning.MineSearchRadiusKm > 0 && dist > tuning.MineSearchRadiusKm) continue;
                    scored.Add(new MineCandidate(n, scores[i], dist));
                }
                scored.Sort(CompareMineCandidates);
                var result = new List<SpatialNode>(scored.Count);
                foreach (MineCandidate c in scored) result.Add(c.Node);
                return result;
            }
        }

        /// <summary>Runs one standing Mine order's full catchment scan in one call -- discovers the
        /// catchment (<see cref="FindMineCatchment"/>), tops up in-flight Mine fleets toward the
        /// order's hub up to catchment size (<see cref="CountInFlightMineFleetsToward"/> plus what
        /// <paramref name="pass"/> already dispatched there), then reschedules
        /// <see cref="StandingOrder.NextScanUtc"/> adaptively (<see cref="ComputeNextScanUtc"/>).
        /// Deliberately NOT sub-chunked to one candidate per call the way <c>DispatchingMine</c> is:
        /// a hub's catchment is inherently local (bounded by
        /// <see cref="FactionTuning.MineSearchRadiusKm"/>, typically a handful of nodes, not the whole
        /// roster), and <see cref="IOperationsExecutionContext.AppendPendingFleet"/> is write-only in
        /// the staged context -- resolving a whole hub's open-slot accounting in one local loop within
        /// a single call avoids needing to persist a running open-slots counter across resumed
        /// <c>TryAdvance</c> calls, which a finer per-candidate cursor would require.
        /// <para><paramref name="pass"/> carries the only state that must survive BETWEEN orders in
        /// the same faction pass; it is rebuilt per pass and never persisted. In the resumable cycle
        /// it lives on the cycle rather than a local, since one order is scanned per
        /// <c>TryAdvance</c>.</para>
        /// <para><see cref="StandingOrder.NextScanUtc"/> is a manager-owned cursor written directly
        /// here, not staged through <see cref="OperationsStagedWorldEffects"/> -- accepted gap, same
        /// shape as the guard's own documented tuning/catalogue/investment exclusions: on a
        /// guard-invalidated restart the write from the discarded attempt is not rolled back, so the
        /// worst case is a slightly delayed next scan for that one order, never a duplicate or lost
        /// dispatch (those are governed by the staged/guarded <see cref="DispatchMine"/> call itself,
        /// which is unaffected).</para></summary>
        private void ScanStandingMineOrder(FactionState st, StandingOrder order, StandingMineScanPass pass,
            List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            FactionTickContext context)
        {
            string hubNodeId = order.HubNodeId;
            SpatialNode hub;
            if (string.IsNullOrEmpty(hubNodeId)
                || !world.Graph.Nodes.TryGetValue(hubNodeId, out hub)
                || !string.Equals(hub.OwnerFactionTag, st.Tag, StringComparison.OrdinalIgnoreCase))
            {
                order.NextScanUtc = world.UtcNow.AddSeconds(order.PeriodSeconds > 0 ? order.PeriodSeconds : 60.0);
                LogStandingMineScan(st, order, ctx, "hub=" + (hubNodeId ?? "(none)") + " hub_unavailable", 0);
                return;
            }

            List<SpatialNode> catchment = FindMineCatchment(hub, ownedNodes, tuning, order.OreWeights, world.Registry);
            int inFlight = pass.InFlightToward(st, hubNodeId);
            var walk = new MineCatchmentWalk
            {
                OrderId = order.Id,
                HubNodeId = hubNodeId,
                Weights = order.OreWeights,
                OpenSlots = catchment.Count - inFlight,
            };
            int openAtStart = walk.OpenSlots;
            WalkMineCatchment(st, catchment, walk, pass, ownedNodes, ctx, world, tuning, context);
            int dispatched = walk.Dispatched;
            System.Text.StringBuilder tokens = walk.Tokens;

            string signature = "order=" + order.Id
                + " hub=" + hubNodeId
                + " rank=" + order.Priority
                + " catchment=" + catchment.Count
                + " inflight=" + inFlight
                + " open=" + openAtStart
                + " dispatched=" + dispatched
                + (tokens.Length > 0 ? " [" + tokens + "]" : "");
            LogStandingMineScan(st, order, ctx, signature, dispatched);

            order.NextScanUtc = ComputeNextScanUtc(catchment, world.UtcNow, order.PeriodSeconds, ctx);
        }

        /// <summary>One order's walk over its hub's catchment, and what it has done so far. The
        /// fields above <see cref="Dispatched"/> are the order's limits; a player standing order sets
        /// only <see cref="OpenSlots"/>, which reproduces the walk exactly as it ran before NPC
        /// extraction orders shared it.</summary>
        private sealed class MineCatchmentWalk
        {
            /// <summary>Recorded as the claimant of every deposit this order's trips take.</summary>
            public string OrderId;
            public string HubNodeId;
            /// <summary>Composition requested of each deposit; null for its natural mix.</summary>
            public Dictionary<string, double> Weights;
            /// <summary>Trips (and staged demands) this order may still spend; decremented on every
            /// <see cref="PlayerMineResult.Ok"/>. <see cref="int.MaxValue"/>: no slot cap.</summary>
            public int OpenSlots = int.MaxValue;
            /// <summary>Non-null for an extraction order: the ore whose in-flight extraction rate
            /// toward the hub (<see cref="FactionTickContext.MineRateToward"/>) is closed against
            /// <see cref="TargetLitersPerHour"/> before each candidate.</summary>
            public string RateOreKey;
            public double TargetLitersPerHour;
            /// <summary>Ends the walk at the first trip that found no idle drill anywhere: every later
            /// deposit would find the same, and repeat the fallback search and the bootstrap mint.</summary>
            public bool StopOnNoShip;
            public bool AllowBootstrap;
            /// <summary>Appended to <c>MINE_DISPATCHED</c> as <c>order=</c>; null for a player.</summary>
            public string OrderTag;

            public int Dispatched;
            /// <summary><see cref="TryFireMine"/> calls so far, whatever their outcome.</summary>
            public int Attempts;
            /// <summary>Index into the catchment of the next candidate: where a walk that yielded to
            /// its attempt budget resumes.</summary>
            public int Next;
            public readonly System.Text.StringBuilder Tokens = new System.Text.StringBuilder();

            public void Token(string token)
            {
                if (Tokens.Length > 0) Tokens.Append(' ');
                Tokens.Append(token);
            }
        }

        /// <summary>Walks <paramref name="catchment"/> in order, firing one Mine trip per free deposit
        /// (<see cref="TryFireMine"/>, full hold) until <paramref name="walk"/>'s limits close. Shared
        /// by a player's standing order and an NPC's extraction order. Per candidate, in this order:
        /// the slot cap; the rate cap (extraction orders); a deposit another order took this pass
        /// (<paramref name="pass"/>); a deposit a trip is already working
        /// (<see cref="FactionTickContext.WorkedMineSources"/>); then the trip itself.
        ///
        /// <para>Cost: one <see cref="TryFireMine"/> per candidate, which is O(1) for a cooling
        /// deposit and one pass over the nodes holding a drill
        /// (<see cref="StandingMineScanPass.MineHullNodes"/>, <see cref="FindMineAlternateShipNode"/>)
        /// for a dispatch or a fallback. No fleet scan: every in-flight read is a dictionary lookup
        /// into indexes built once per pass.</para>
        ///
        /// <para><b>Resumable.</b> With <paramref name="attemptBudget"/> above zero the walk stops
        /// before its next <see cref="TryFireMine"/> once it has made that many in this call and
        /// returns false; calling it again with the same <paramref name="walk"/> continues at
        /// <see cref="MineCatchmentWalk.Next"/>. The staged cycle paces a long walk across steps this
        /// way (<see cref="FactionTuning.ExtractionMineAttemptsPerStep"/>); the candidates, and every
        /// decision about them, are the ones a single call makes. Returns true once the walk is
        /// over.</para></summary>
        private bool WalkMineCatchment(FactionState st, List<SpatialNode> catchment, MineCatchmentWalk walk,
            StandingMineScanPass pass, List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx,
            WorldModel world, FactionTuning tuning, FactionTickContext context, int attemptBudget = 0)
        {
            List<SpatialNode> hullNodes = pass.MineHullNodes(ctx, ownedNodes, world.PrefabCatalogue);
            int attemptsThisCall = 0;
            for (; walk.Next < catchment.Count; walk.Next++)
            {
                SpatialNode candidate = catchment[walk.Next];
                if (walk.OpenSlots <= 0)
                {
                    walk.Token("remaining=no_open_slots");
                    break;
                }
                if (walk.RateOreKey != null
                    && context.MineRateToward(walk.HubNodeId, walk.RateOreKey) >= walk.TargetLitersPerHour)
                {
                    walk.Token("remaining=target_met");
                    break;
                }
                // Already taken this pass by an order ranked above this one. Not an error and not a
                // cooldown -- naming it distinctly is what makes a contested catchment diagnosable
                // from the log instead of looking like a deposit that silently never fires.
                string claimant;
                if (pass.TryGetClaimant(candidate.Id, out claimant))
                {
                    // Names the winner rather than asserting it outranked this order: at equal rank
                    // the tie-break is age then id, so "higher" would be untrue as often as not.
                    walk.Token(candidate.Id + ":claimed_by_" + claimant);
                    continue;
                }
                // A trip from an earlier scan (or another order this pass) is dwelling here or flying
                // here to dwell. For a player order it already counts in the hub's in-flight count
                // when it is bound for this hub, so skipping the deposit spends no slot: the slot goes
                // to the next deposit instead of stacking a second trip on this one before its
                // cooldown is written.
                if (context.WorkedMineSources.Contains(candidate.Id))
                {
                    walk.Token(candidate.Id + ":working");
                    continue;
                }
                // Checked here, after the free skips and before the trip, so a resumed call re-reads
                // the caps above for this same candidate; nothing between the two calls moves them.
                if (attemptBudget > 0 && attemptsThisCall >= attemptBudget) return false;
                attemptsThisCall++;
                walk.Attempts++;
                // The walk doesn't distinguish dispatch tiers -- a player order's adaptive rescan and an
                // extraction order's next pass both retry a deposit that found no hull.
                MineDispatchOutcome outcome;
                PlayerMineResult result = TryFireMine(st, candidate.Id, walk.HubNodeId, walk.Weights, ownedNodes,
                    ctx, world, tuning, context, out outcome, walk.AllowBootstrap, walk.OrderTag, hullNodes);
                // An Ok spends this order's own slot whether or not anything flew -- a staged
                // Ship:Mine demand is this order's attempt on that deposit, and retrying it in the
                // same pass would achieve nothing. The pass-wide claim is narrower: see
                // StandingMineScanPass.
                if (result == PlayerMineResult.Ok) walk.OpenSlots--;
                if (result == PlayerMineResult.Ok && outcome == MineDispatchOutcome.Dispatched)
                    pass.RecordDispatch(walk.HubNodeId, candidate.Id, walk.OrderId);

                walk.Token(candidate.Id + ":" + ScanCandidateToken(result, outcome, candidate.Id, ctx));
                if (result == PlayerMineResult.Ok && outcome == MineDispatchOutcome.Dispatched) walk.Dispatched++;
                if (walk.StopOnNoShip && result == PlayerMineResult.Ok && outcome == MineDispatchOutcome.NoShipAvailable)
                {
                    walk.Token("remaining=no_idle_drill");
                    break;
                }
            }
            walk.Next = catchment.Count;
            return true;
        }

        /// <summary>One summary token per catchment candidate for the MINE_AUTO_SCAN line. The
        /// cooldown token embeds the expiry timestamp, which stays constant until the cooldown
        /// clears -- so an unchanged wait state self-dedupes under the log-on-change rule.</summary>
        private static string ScanCandidateToken(PlayerMineResult result, MineDispatchOutcome outcome,
            string candidateId, IOperationsExecutionContext ctx)
        {
            if (result == PlayerMineResult.Ok)
            {
                if (outcome == MineDispatchOutcome.Dispatched) return "dispatched";
                if (outcome == MineDispatchOutcome.NoShipRouteInfeasible) return "no_route_ship_demand_staged";
                if (outcome == MineDispatchOutcome.ClosedForCombat) return "closed_for_combat";
                return "no_ship_demand_staged";
            }
            if (result == PlayerMineResult.OnCooldown)
                return "cooldown_until=" + ctx.GetMineCooldownUntilUtc(candidateId).ToString("HH:mm:ss");
            if (result == PlayerMineResult.InvalidOreWeights) return "invalid_weights";
            return result.ToString().ToLowerInvariant();
        }

        /// <summary>Emits the per-order MINE_AUTO_SCAN summary only when something dispatched or the
        /// summary's content changed since the last scan -- a 120s-period order in a steady state
        /// (all deposits cooling down, or waiting on a ship) stays silent instead of spamming.</summary>
        private static void LogStandingMineScan(FactionState st, StandingOrder order,
            IOperationsExecutionContext ctx, string signature, int dispatched)
        {
            if (dispatched == 0 && string.Equals(signature, order.LastScanLogSignature, StringComparison.Ordinal))
                return;
            order.LastScanLogSignature = signature;
            ctx.AddLog(st.Tag + " MINE_AUTO_SCAN " + signature);
        }
    }
}
