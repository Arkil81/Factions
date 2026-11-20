using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Factions.Core
{
    public sealed partial class ProductionManager
    {
        /// <summary>
        /// Cross-frame sweep over a faction roster. Lives for as long as a sweep is in progress;
        /// null between sweeps. Faction identity is tracked by tag, not by <see cref="FactionState"/>
        /// reference or list index, so the sweep survives the host's roster changing underneath it
        /// (a faction registered, forgotten, or gone dormant) between <see cref="TryAdvance"/> calls.
        /// </summary>
        private sealed class ProductionSweepCursor
        {
            internal List<string> FactionTagOrder;
            internal int NextIndex;
            internal ProductionCycleState CurrentCycle;

            /// <summary>Running total across every faction committed so far this sweep.
            /// TryCommitTransaction unconditionally points _lastTelemetry at the just-committed
            /// transaction's own (single-faction) telemetry object — correct for Tick()'s original
            /// one-transaction-per-whole-call shape, wrong once one transaction covers only one
            /// faction. TryAdvance re-points _lastTelemetry back here after every commit so a
            /// multi-faction sweep's counters accumulate instead of being replaced each time.</summary>
            internal ProductionTickTelemetry SweepTelemetry;
        }

        // PostPasses is split into five phases rather than two. The naive split (one scalar phase
        // covering all six original sub-calls, then one per-node phase for the two node-scanning
        // ones) changes which call posts last to a demand key several of these sub-calls share
        // (an unkeyed "Item:X" demand -- DemandRegister.Post is last-write-wins per (faction, kind,
        // node)). RunPostPasses's original order is Capability -> Refinement(all nodes) -> Throughput
        // -> ReservationKeepAlive -> InstallCost -> Founding -> CatalogScalar -> CatalogSurplus(all
        // nodes); reordering any of
        // these changes which post wins for the same key (caught live by
        // RefinementDemand_IngotToComponent_PostsWhenOutputBelowCap and
        // ProductionExecutionContextEquivalenceTests during this change). These five phases preserve
        // that exact relative order while still checkpointing the two genuinely per-node passes.
        private enum ProductionCyclePhase
        {
            Preparing,
            Banks,
            PostPassesPre,
            PostPassesRefinementNodes,
            PostPassesThroughputSlots,
            PostPassesThroughputIncome,
            PostPassesThroughputRates,
            PostPassesThroughputPost,
            PostPassesScalar,
            PostPassesCatalogNodes,
            Complete,
        }

        /// <summary>One capability bank at one node — the bounded work unit the Banks phase advances
        /// one at a time. <see cref="IsLastForNode"/> marks when to flush that node's accumulated
        /// <see cref="ProductionCycleState.ItemDemandNeeds"/> via <see cref="PostNodeBankDemands"/>.</summary>
        private struct BankUnit
        {
            internal SpatialNode Node;
            internal NodeCapabilityKind Cap;
            internal int Level;
            internal bool IsLastForNode;
            /// <summary>True on the first ingot-consuming-tier unit for this node (or the node's very
            /// first unit if it has no ingot-producing-tier capability at all) — the point at which
            /// <see cref="ServiceShipBuildReservations"/> must run, after ingot-producing banks have
            /// ticked but before ingot-consuming ones do. See <see cref="BuildBankUnits"/>.</summary>
            internal bool IsFirstIngotConsumingForNode;
        }

        /// <summary>One faction's in-flight staged cycle, resumable across many <see cref="TryAdvance"/>
        /// calls. Nothing here is visible outside the cycle until <see cref="CommitCycle"/> succeeds.</summary>
        private sealed class ProductionCycleState
        {
            internal FactionState State;
            internal FactionTuning Tuning;
            internal ProductionCycleCommitGuard Guard;
            internal ProductionStagedWorldEffects Effects;
            internal StagedProductionExecutionContext Ctx;
            internal TransactionState Transaction;

            internal ProductionCyclePhase Phase;
            internal FactionCyclePrep Prep;
            internal List<BankUnit> BankUnits;
            internal int BankUnitIndex;
            internal NodeInputNeeds ItemDemandNeeds;

            /// <summary>Node cursor shared by PostPassesRefinementNodes and PostPassesCatalogNodes
            /// (reset to 0 when entering each) — index into <see cref="FactionCyclePrep.OwnedNodes"/>
            /// of the next node to service. Unlike <see cref="BankUnits"/> this needs no per-node
            /// flush/IsLastForNode concept — every node's work in either phase is independent (no
            /// cross-node accumulator).</summary>
            internal int PostPassNodeIndex;
            /// <summary>Offer keys resolved once by PostPassesScalar's
            /// <see cref="ProductionManager.PostCatalogDemandsScalar"/> call, reused by every node in
            /// PostPassesCatalogNodes. Null when surplus-intent is disabled or unresolvable for this
            /// faction — PostPassesCatalogNodes then posts gas only, and is skipped when
            /// <see cref="ProductionManager.CatalogNodePhaseHasWork"/> says there is none.</summary>
            internal IReadOnlyList<string> PostPassCatalogOfferKeys;
            internal DateTime PostPassCatalogExpires;

            /// <summary>How many times this faction's cycle has been discarded and restarted after a
            /// guard invalidation. Reset only when a fresh cycle begins for a faction that previously
            /// committed cleanly; carried forward across restarts of the same stalled attempt.</summary>
            internal int ConsecutiveInvalidations;

            /// <summary>Stopwatch timestamp at BeginCycle, so the sweep can report how long the
            /// longest single cycle stayed in flight (ProductionTickTelemetry.MaxCycleAgeTicks).</summary>
            internal long StartTimestamp;

            // ---- throughput-investment phases -------------------------------------------------
            /// <summary>This faction's active machine slots, collected once (see
            /// ProductionManager.CollectThroughputSlots) and then swept in bounded batches by
            /// PostPassesThroughputRates.</summary>
            internal List<ThroughputSlotRef> ThroughputSlots;
            internal HashSet<NodeCapabilityKind> ThroughputRefineCaps;
            internal HashSet<NodeCapabilityKind> ThroughputManufCaps;
            internal int ThroughputSlotIndex;
            internal int ThroughputIncomeNodeIndex;
            internal double ThroughputOreIncomePerMin;
            internal double ThroughputOreConsumedPerMin;
            internal double ThroughputIngotIncomePerMin;
            internal double ThroughputIngotConsumedPerMin;
            /// <summary>Per-region ore ledger, filled alongside the network rates above from the
            /// same node and slot passes (see ProductionManager.Regions.cs). Null when the faction
            /// has no Refinery anywhere -- that is the bootstrap branch's case.</summary>
            internal Dictionary<NodeRegion, RegionOreLedger> RegionLedger;
            internal Dictionary<string, NodeRegion> NodeRegionById;
        }

        // After this many consecutive guard invalidations for the same faction, stop retrying the
        // staged path and run that one faction fully synchronously (ignoring the deadline) so the
        // sweep always eventually completes. The radius-narrowed guard (see
        // ProductionCycleCommitGuard) already removes invalidation from unrelated map activity; this
        // covers the residual, legitimate case of a faction's own contested territory changing during
        // the multi-frame window its cycle needs to finish.
        private const int RestartBudgetLimit = 3;

        // Machine slots whose rate contributions are summed per PostPassesThroughputRates step.
        // Small enough that one step stays well inside a frame, large enough that per-step overhead
        // stays negligible against the per-slot recipe lookup.
        private const int ThroughputRateBatch = 64;

        private ProductionSweepCursor _sweep;

        /// <summary>True only while one faction has a captured, resumable Production cycle. Host-side
        /// maintenance that mutates Production's guarded read set must defer while this is true;
        /// otherwise it can invalidate every staged attempt before commit and force the synchronous
        /// fallback. The gap between factions remains available to maintenance because no guard is
        /// live then.</summary>
        public bool IsCycleInProgress { get { return _sweep != null && _sweep.CurrentCycle != null; } }

        /// <summary>
        /// Advances the current production sweep over <paramref name="states"/> by as much bounded
        /// work as fits before <paramref name="deadline"/> expires, resuming a prior sweep if one is
        /// in progress or starting a fresh one otherwise. Returns true once every faction in the
        /// roster has had a cycle committed (or skipped for being dormant) — the caller may then
        /// reschedule its next sweep on its normal interval. Returns false if the deadline was hit
        /// before the sweep finished; the caller must call this again (ignoring its own interval)
        /// rather than starting a new sweep, so an in-flight faction cycle is never abandoned mid-way.
        /// </summary>
        public bool TryAdvance(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            FrameDeadline deadline, List<string> log)
        {
            // Taken off the world rather than the constructor so no call site has to be rewired: the
            // fuel reserve pass reads it to learn what a faction's own hulls burn, and it is the same
            // instance for the life of a session.
            _prefabCatalogue = world != null ? world.PrefabCatalogue : null;
            _reserveWorld = world;
            if (_sweep == null)
            {
                _sweep = new ProductionSweepCursor { FactionTagOrder = new List<string>(), NextIndex = 0 };
                foreach (FactionState st in states) _sweep.FactionTagOrder.Add(st.Tag);
                // Fresh baseline per sweep, matching Tick()'s pre-cursor "one call, one telemetry
                // snapshot" contract. Not staged per-cycle-attempt: a restart-budget retry can
                // double-count a bank it already examined once. Telemetry is diagnostic-only (perf.json),
                // never read by decision logic, so this is an accepted, documented limitation rather
                // than a correctness hazard — and invalidation should be rare after the guard narrowing.
                _sweep.SweepTelemetry = new ProductionTickTelemetry();
                _lastTelemetry = _sweep.SweepTelemetry;
            }

            while (!deadline.IsExpired())
            {
                if (StepSweepOnce(states, world, tuning, log))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Runs exactly one step of the sweep state machine — either the sweep-complete check, a
        /// faction-selection step, one bounded cycle-phase unit, or one commit-attempt-and-restart
        /// decision — with no deadline check of its own. Extracted from <see cref="TryAdvance"/>'s
        /// loop body so tests can drive the state machine deterministically (see
        /// <see cref="StepSweepOnceForTesting"/>) without depending on real elapsed time to land a
        /// yield at a specific point. Returns true once the whole sweep has completed.
        /// </summary>
        private bool StepSweepOnce(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            if (_sweep.CurrentCycle == null)
            {
                if (_sweep.NextIndex >= _sweep.FactionTagOrder.Count)
                {
                    _sweep = null;
                    return true;
                }

                // Unlike FleetManager/TradeManager, IsAutonomyDisabled is NOT checked here: production
                // is self-sufficient (driven by DemandRegister/InvestmentRegister/scarcity, not
                // anything FactionBrain.Think computes), so a Magnate-tier player faction's installed
                // capabilities actually run. See docs/economy-territory.md -> "Player-faction claiming".
                FactionState st = ResolveFaction(states, _sweep.FactionTagOrder[_sweep.NextIndex]);
                if (st == null || FactionLifecycle.IsDormant(st, world.UtcNow))
                {
                    _sweep.NextIndex++;
                    return false;
                }
                _sweep.CurrentCycle = BeginCycle(st, world, tuning, 0);
                return false;
            }

            ProductionCycleState cycle = _sweep.CurrentCycle;
            if (!AdvanceCyclePhaseStep(cycle))
                return false; // one bounded unit done; caller re-checks the deadline before the next

            FactionState current = ResolveFaction(states, cycle.State.Tag);
            bool committed = current != null && CommitCycle(cycle, current, world, log);
            RecordCycleAge(cycle);
            if (committed)
            {
                _sweep.CurrentCycle = null;
                _sweep.NextIndex++;
                return false;
            }

            if (current == null)
            {
                // The faction disappeared from the roster entirely mid-cycle (forgotten). Nothing
                // to retry or fall back to; just move on.
                _sweep.CurrentCycle = null;
                _sweep.NextIndex++;
                return false;
            }

            int invalidations = cycle.ConsecutiveInvalidations + 1;
            if (_sweep.SweepTelemetry != null) _sweep.SweepTelemetry.Restarts++;
            if (invalidations >= RestartBudgetLimit)
            {
                RunFactionSynchronously(current, world, tuning, log, invalidations);
                _sweep.CurrentCycle = null;
                _sweep.NextIndex++;
                return false;
            }

            // Non-terminal restart. Previously silent -- only the terminal FORCED_SYNC was logged, so
            // a cycle burning most of its restart budget every sweep was indistinguishable from a
            // healthy one.
            log.Add(current.Tag + " PRODUCTION_CYCLE_RESTART invalidations=" + invalidations
                + " budget=" + RestartBudgetLimit + " phase=" + cycle.Phase);
            _sweep.CurrentCycle = BeginCycle(current, world, tuning, invalidations);
            return false;
        }

        private static FactionState ResolveFaction(IList<FactionState> states, string tag)
        {
            foreach (FactionState st in states)
                if (string.Equals(st.Tag, tag, StringComparison.Ordinal)) return st;
            return null;
        }

        private ProductionCycleState BeginCycle(FactionState st, WorldModel world, FactionTuning tuning,
            int carryOverInvalidations)
        {
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(st, world);
            ProductionStagedWorldEffects effects = ProductionStagedWorldEffects.Capture(st, world);
            var ctx = new StagedProductionExecutionContext(world, effects, st.Tag, world.UtcNow);
            return new ProductionCycleState
            {
                State = st,
                Tuning = tuning,
                Guard = guard,
                Effects = effects,
                Ctx = ctx,
                Transaction = BeginTransaction(),
                Phase = ProductionCyclePhase.Preparing,
                ConsecutiveInvalidations = carryOverInvalidations,
                StartTimestamp = Stopwatch.GetTimestamp(),
            };
        }

        /// <summary>Folds one finished cycle's wall-clock lifetime into the sweep's high-water mark.
        /// Called on both the commit and the discard path — a cycle that kept being invalidated is
        /// exactly the one whose age matters.</summary>
        private void RecordCycleAge(ProductionCycleState cycle)
        {
            if (_sweep == null || _sweep.SweepTelemetry == null || cycle == null) return;
            long age = Stopwatch.GetTimestamp() - cycle.StartTimestamp;
            if (age > _sweep.SweepTelemetry.MaxCycleAgeTicks)
                _sweep.SweepTelemetry.MaxCycleAgeTicks = age;
        }

        /// <summary>Advances one bounded unit of work for <paramref name="cycle"/>. Returns true once
        /// every phase has run and the cycle is ready to commit.</summary>
        private bool AdvanceCyclePhaseStep(ProductionCycleState cycle)
        {
            switch (cycle.Phase)
            {
                case ProductionCyclePhase.Preparing:
                {
                    cycle.Prep = PrepareFaction(cycle.State, cycle.Ctx, cycle.Tuning, cycle.Transaction.Slots,
                        cycle.Transaction.Remainders);
                    if (cycle.Prep == null)
                    {
                        // Went dormant between sweep-start and this cycle beginning. Nothing was
                        // staged; committing the (empty) transaction/effects is a correct no-op.
                        cycle.Phase = ProductionCyclePhase.Complete;
                        return true;
                    }
                    cycle.BankUnits = BuildBankUnits(cycle.Prep.OwnedNodes);
                    cycle.BankUnitIndex = 0;
                    cycle.ItemDemandNeeds = new NodeInputNeeds();
                    cycle.Phase = ProductionCyclePhase.Banks;
                    return false;
                }
                case ProductionCyclePhase.Banks:
                {
                    if (cycle.BankUnitIndex >= cycle.BankUnits.Count)
                    {
                        cycle.Phase = ProductionCyclePhase.PostPassesPre;
                        return false;
                    }
                    BankUnit unit = cycle.BankUnits[cycle.BankUnitIndex];
                    if (unit.IsFirstIngotConsumingForNode)
                        ServiceShipBuildReservations(cycle.State.Tag, unit.Node, cycle.Prep,
                            cycle.Transaction.Remainders, cycle.Ctx);
                    var capEntry = new KeyValuePair<NodeCapabilityKind, int>(unit.Cap, unit.Level);
                    TickOneBank(cycle.Transaction.Slots, cycle.Transaction.Remainders, cycle.State.Tag,
                        unit.Node, capEntry, cycle.Prep, cycle.ItemDemandNeeds, cycle.Ctx);
                    if (unit.IsLastForNode)
                    {
                        PostNodeBankDemands(cycle.Tuning, cycle.Ctx, cycle.State.Tag, unit.Node,
                            cycle.ItemDemandNeeds, cycle.Prep.Now,
                            ChainRequirementFor(cycle.Prep, unit.Node.Id));
                        cycle.ItemDemandNeeds = new NodeInputNeeds();
                    }
                    cycle.BankUnitIndex++;
                    if (cycle.BankUnitIndex >= cycle.BankUnits.Count) cycle.Phase = ProductionCyclePhase.PostPassesPre;
                    return false;
                }
                // PostPasses is split into five phases to keep RunPostPasses's original relative
                // order intact (see the enum's doc comment) while still checkpointing the two
                // genuinely per-node passes (refinement requests, catalog surplus-intent) one node at
                // a time -- same reasoning as Banks' BankUnit chunking, just without a per-node
                // accumulator to flush since these two passes have none (see docs/managers.md).
                case ProductionCyclePhase.PostPassesPre:
                {
                    long phaseStart = Stopwatch.GetTimestamp();
                    _lastTelemetry.InvestmentPasses++;
                    PostCapabilityInvestmentRequests(cycle.State, cycle.Prep.NetworkCaps, cycle.Ctx, cycle.Tuning);
                    _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;

                    _lastTelemetry.RefinementPasses++;
                    cycle.PostPassNodeIndex = 0;
                    cycle.Phase = ProductionCyclePhase.PostPassesRefinementNodes;
                    return false;
                }
                case ProductionCyclePhase.PostPassesRefinementNodes:
                {
                    if (cycle.PostPassNodeIndex >= cycle.Prep.OwnedNodes.Count)
                    {
                        cycle.Phase = ProductionCyclePhase.PostPassesThroughputSlots;
                        return false;
                    }

                    SpatialNode node = cycle.Prep.OwnedNodes[cycle.PostPassNodeIndex];
                    long phaseStart = Stopwatch.GetTimestamp();
                    PostRefinementRequestsForNode(cycle.State.Tag, cycle.Prep.NetworkCaps, node, cycle.Ctx, cycle.Tuning);
                    _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;

                    cycle.PostPassNodeIndex++;
                    if (cycle.PostPassNodeIndex >= cycle.Prep.OwnedNodes.Count)
                        cycle.Phase = ProductionCyclePhase.PostPassesThroughputSlots;
                    return false;
                }
                // The throughput comparison was the single largest cost in the manager (live
                // 2026-08-21: 75.9% of production phase time) and ran as one atomic whole-faction
                // call. Its expensive parts -- the per-node influence income sum and the per-slot
                // rate sums -- are pure reads, so they chunk with a plain cursor and need no staging
                // or guard involvement; only the small decision at the end posts anything.
                case ProductionCyclePhase.PostPassesThroughputSlots:
                {
                    long phaseStart = Stopwatch.GetTimestamp();
                    _lastTelemetry.InvestmentPasses++;
                    HashSet<NodeCapabilityKind> refineCaps, manufCaps;
                    BuildThroughputCapSets(cycle.Prep.NetworkCaps, out refineCaps, out manufCaps);
                    cycle.ThroughputRefineCaps = refineCaps;
                    cycle.ThroughputManufCaps  = manufCaps;
                    cycle.ThroughputSlots      = CollectThroughputSlots(cycle.Transaction.Slots, cycle.State.Tag);
                    cycle.ThroughputSlotIndex        = 0;
                    cycle.ThroughputIncomeNodeIndex  = 0;
                    cycle.ThroughputOreIncomePerMin      = 0.0;
                    cycle.ThroughputOreConsumedPerMin    = 0.0;
                    cycle.ThroughputIngotIncomePerMin    = 0.0;
                    cycle.ThroughputIngotConsumedPerMin  = 0.0;
                    bool hasRefinery = cycle.Prep.NetworkCaps.Contains(NodeCapabilityKind.Refinery);
                    cycle.RegionLedger   = hasRefinery ? new Dictionary<NodeRegion, RegionOreLedger>() : null;
                    cycle.NodeRegionById = hasRefinery ? IndexNodeRegions(cycle.Prep.OwnedNodes) : null;
                    _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;
                    // Ore income is only read on the has-Refinery branch; skip the whole pass
                    // otherwise so no influence query runs that the decision cannot use.
                    cycle.Phase = hasRefinery
                        ? ProductionCyclePhase.PostPassesThroughputIncome
                        : ProductionCyclePhase.PostPassesThroughputRates;
                    return false;
                }
                case ProductionCyclePhase.PostPassesThroughputIncome:
                {
                    if (cycle.ThroughputIncomeNodeIndex >= cycle.Prep.OwnedNodes.Count)
                    {
                        cycle.Phase = ProductionCyclePhase.PostPassesThroughputRates;
                        return false;
                    }
                    long phaseStart = Stopwatch.GetTimestamp();
                    SpatialNode node = cycle.Prep.OwnedNodes[cycle.ThroughputIncomeNodeIndex];
                    double nodeIncome = NodeOreIncomePerMin(node, cycle.State.Tag, cycle.Ctx, cycle.Tuning);
                    cycle.ThroughputOreIncomePerMin += nodeIncome;
                    AccumulateRegionNode(cycle.RegionLedger, node, cycle.NodeRegionById, nodeIncome,
                        cycle.Ctx, cycle.Tuning, cycle.ThroughputRefineCaps);
                    _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;

                    cycle.ThroughputIncomeNodeIndex++;
                    if (cycle.ThroughputIncomeNodeIndex >= cycle.Prep.OwnedNodes.Count)
                        cycle.Phase = ProductionCyclePhase.PostPassesThroughputRates;
                    return false;
                }
                case ProductionCyclePhase.PostPassesThroughputRates:
                {
                    if (cycle.ThroughputSlotIndex >= cycle.ThroughputSlots.Count)
                    {
                        cycle.Phase = ProductionCyclePhase.PostPassesThroughputPost;
                        return false;
                    }
                    long phaseStart = Stopwatch.GetTimestamp();
                    int end = cycle.ThroughputSlotIndex + ThroughputRateBatch;
                    if (end > cycle.ThroughputSlots.Count) end = cycle.ThroughputSlots.Count;
                    // All three rates are summed in the same pass over the batch; they differ only
                    // in their cap set / item-key prefix / side, and a slot that matches none
                    // contributes zero to each.
                    for (int i = cycle.ThroughputSlotIndex; i < end; i++)
                    {
                        ThroughputSlotRef slotRef = cycle.ThroughputSlots[i];
                        cycle.ThroughputOreConsumedPerMin   += SlotRatePerMin(slotRef, cycle.ThroughputRefineCaps, "Ore:",   true);
                        cycle.ThroughputIngotIncomePerMin   += SlotRatePerMin(slotRef, cycle.ThroughputRefineCaps, "Ingot:", false);
                        cycle.ThroughputIngotConsumedPerMin += SlotRatePerMin(slotRef, cycle.ThroughputManufCaps,  "Ingot:", true);
                        if (cycle.RegionLedger != null)
                            AccumulateRegionSlot(cycle.RegionLedger, cycle.NodeRegionById, slotRef, cycle.ThroughputRefineCaps);
                    }
                    cycle.ThroughputSlotIndex = end;
                    _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;

                    if (cycle.ThroughputSlotIndex >= cycle.ThroughputSlots.Count)
                        cycle.Phase = ProductionCyclePhase.PostPassesThroughputPost;
                    return false;
                }
                case ProductionCyclePhase.PostPassesThroughputPost:
                {
                    long phaseStart = Stopwatch.GetTimestamp();
                    DecideThroughputInvestments(cycle.Tuning, cycle.State, cycle.Prep.NetworkCaps, cycle.Ctx,
                        cycle.ThroughputOreIncomePerMin, cycle.ThroughputOreConsumedPerMin,
                        cycle.ThroughputIngotIncomePerMin, cycle.ThroughputIngotConsumedPerMin,
                        StageUtilisation(cycle.ThroughputSlots, cycle.ThroughputRefineCaps, cycle.Prep.OwnedNodes),
                        StageUtilisation(cycle.ThroughputSlots, cycle.ThroughputManufCaps, cycle.Prep.OwnedNodes),
                        MeasureGasStage(cycle.State, cycle.Prep.OwnedNodes, cycle.Ctx, cycle.Tuning));
                    if (cycle.RegionLedger != null)
                        DecideRegionThroughputInvestments(cycle.Tuning, cycle.State, cycle.Ctx, cycle.RegionLedger);
                    _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;
                    cycle.Phase = ProductionCyclePhase.PostPassesScalar;
                    return false;
                }
                case ProductionCyclePhase.PostPassesScalar:
                {
                    long phaseStart = Stopwatch.GetTimestamp();
                    PostReservationKeepAlive(cycle.Tuning, cycle.State, cycle.Ctx);
                    PostInstallCostDemandsForInvestmentRequests(cycle.State.Tag, cycle.Prep.NetworkCaps, cycle.Ctx, cycle.Tuning);
                    PostFoundingCostDemands(cycle.State, cycle.Prep.NetworkCaps, cycle.Ctx, cycle.Tuning);
                    PostFuelReserveDemands(cycle.State.Tag, cycle.Prep.OwnedNodes, cycle.Ctx, cycle.Tuning);
                    PostMunitionReserveDemands(cycle.State.Tag, cycle.Prep.OwnedNodes, cycle.Ctx, cycle.Tuning);
                    PostStockOrderDemands(cycle.State, cycle.Ctx, cycle.Tuning);
                    _lastTelemetry.InvestmentRefinementTicks += Stopwatch.GetTimestamp() - phaseStart;

                    phaseStart = Stopwatch.GetTimestamp();
                    DateTime expires;
                    cycle.PostPassCatalogOfferKeys = PostCatalogDemandsScalar(cycle.State.Tag, cycle.Ctx, cycle.Tuning,
                        cycle.Prep.Now, cycle.Transaction.CatalogDemandSuppressed, out expires);
                    cycle.PostPassCatalogExpires = expires;
                    _lastTelemetry.CatalogTicks += Stopwatch.GetTimestamp() - phaseStart;

                    cycle.PostPassNodeIndex = 0;
                    cycle.Phase = ProductionCyclePhase.PostPassesCatalogNodes;
                    return false;
                }
                case ProductionCyclePhase.PostPassesCatalogNodes:
                {
                    if (!CatalogNodePhaseHasWork(cycle.PostPassCatalogOfferKeys, cycle.Tuning)
                        || cycle.PostPassNodeIndex >= cycle.Prep.OwnedNodes.Count)
                    {
                        cycle.Phase = ProductionCyclePhase.Complete;
                        return true;
                    }

                    SpatialNode node = cycle.Prep.OwnedNodes[cycle.PostPassNodeIndex];
                    long phaseStart = Stopwatch.GetTimestamp();
                    PostCatalogNode(cycle.State.Tag, node, cycle.PostPassCatalogOfferKeys, cycle.Ctx,
                        cycle.Tuning, cycle.PostPassCatalogExpires);
                    _lastTelemetry.CatalogTicks += Stopwatch.GetTimestamp() - phaseStart;

                    cycle.PostPassNodeIndex++;
                    if (cycle.PostPassNodeIndex >= cycle.Prep.OwnedNodes.Count)
                    {
                        cycle.Phase = ProductionCyclePhase.Complete;
                        return true;
                    }
                    return false;
                }
                default:
                    return true;
            }
        }

        /// <summary>Flattens a faction's owned-node capability entries into one ordered list so the
        /// Banks phase can checkpoint on a single integer index instead of nested node/capability
        /// cursors. A node with zero capability entries contributes nothing — matches
        /// <see cref="PostNodeBankDemands"/> being a no-op for an empty demand accumulator either way.</summary>
        private static List<BankUnit> BuildBankUnits(List<SpatialNode> ownedNodes)
        {
            var units = new List<BankUnit>();
            foreach (SpatialNode node in ownedNodes)
            {
                int before = units.Count;
                // Ingot-producing capabilities (Refinery/RefineryII) first, then ingot-consuming ones --
                // ServiceShipBuildReservations must run between the two tiers so its reservation snapshot
                // includes this tick's freshly-minted ingots, not just what survived unconsumed from the
                // previous tick (see ServiceShipBuildReservations's doc comment). node.Capabilities is a
                // Dictionary with no ordering guarantee, so this two-pass emission is what actually
                // enforces the tier order, not incidental insertion order.
                foreach (var capEntry in node.Capabilities)
                    if (IsIngotProducingTier(capEntry.Key))
                        units.Add(new BankUnit { Node = node, Cap = capEntry.Key, Level = capEntry.Value });
                int firstIngotConsumingIndex = units.Count;
                foreach (var capEntry in node.Capabilities)
                    if (!IsIngotProducingTier(capEntry.Key))
                        units.Add(new BankUnit { Node = node, Cap = capEntry.Key, Level = capEntry.Value });
                if (units.Count > firstIngotConsumingIndex)
                {
                    BankUnit firstIngotConsuming = units[firstIngotConsumingIndex];
                    firstIngotConsuming.IsFirstIngotConsumingForNode = true;
                    units[firstIngotConsumingIndex] = firstIngotConsuming;
                }
                if (units.Count > before)
                {
                    BankUnit last = units[units.Count - 1];
                    last.IsLastForNode = true;
                    units[units.Count - 1] = last;
                }
            }
            return units;
        }

        /// <summary>
        /// Publishes a completed cycle's manager-local transaction and staged WorldModel effects.
        /// Returns false without publishing anything if the guard has been invalidated.
        /// <para>Order matters: the transaction never touches <see cref="WorldModel"/>, so committing
        /// it first cannot itself invalidate the guard the world-effects commit checks again right
        /// after — publishing world effects before the transaction would mutate the very node
        /// inventory the guard's own snapshot compares, spuriously failing that second check against
        /// the mutation the first commit had just made (found the hard way writing the equivalence
        /// test for this).</para>
        /// </summary>
        private bool CommitCycle(ProductionCycleState cycle, FactionState currentState, WorldModel world, List<string> log)
        {
            if (!cycle.Guard.IsStillValid(currentState, world)) return false;
            if (!TryCommitTransaction(cycle.Transaction, log)) return false;
            // TryCommitTransaction just pointed _lastTelemetry at this one faction's own transaction
            // telemetry, discarding the running sweep total it replaced — merge it back in and restore
            // the pointer so the next faction (or an external reader) sees the accumulated sweep total.
            RestoreSweepTelemetry(cycle.Transaction.Telemetry);
            return cycle.Effects.TryCommit(cycle.Guard, currentState, world, log);
        }

        private void RestoreSweepTelemetry(ProductionTickTelemetry committed)
        {
            if (_sweep == null) return; // Tick()'s non-sweep tests/callers never see this path in practice
            _sweep.SweepTelemetry.Add(committed);
            _lastTelemetry = _sweep.SweepTelemetry;
        }

        /// <summary>Restart-budget fallback: runs one faction's whole cycle synchronously via the
        /// live (unstaged) path, exactly like <see cref="Tick"/> does for a single faction, so the
        /// sweep always makes forward progress even if this faction's own territory keeps changing
        /// faster than a multi-frame staged cycle can settle.</summary>
        private void RunFactionSynchronously(FactionState st, WorldModel world, FactionTuning tuning,
            List<string> log, int invalidationCount)
        {
            TransactionState transaction = BeginTransaction();
            var ctx = new LiveProductionExecutionContext(world, transaction.Logs);
            TickOneFaction(st, ctx, tuning, transaction.Slots, transaction.CatalogDemandSuppressed,
                transaction.Remainders);
            TryCommitTransaction(transaction, log);
            RestoreSweepTelemetry(transaction.Telemetry);
            log.Add(st.Tag + " PRODUCTION_CYCLE_FORCED_SYNC after=" + invalidationCount + " invalidations");
        }

        // ---- test-only seam ----------------------------------------------------------------------
        // Deterministic step-by-step access to the sweep state machine, so tests can drive exact
        // bounded units — including interleaving WorldModel mutation between them to force a guard
        // invalidation — without depending on real elapsed time to land a yield at a specific point
        // (unlike driving suspension through the public TryAdvance + a tiny FrameDeadline). Same
        // reflection-invoke pattern as BeginTransactionForTesting/TryCommitTransactionForTesting.

        internal void BeginSweepForTesting(IList<FactionState> states)
        {
            _sweep = new ProductionSweepCursor { FactionTagOrder = new List<string>(), NextIndex = 0 };
            foreach (FactionState st in states) _sweep.FactionTagOrder.Add(st.Tag);
            _sweep.SweepTelemetry = new ProductionTickTelemetry();
            _lastTelemetry = _sweep.SweepTelemetry;
        }

        internal bool StepSweepOnceForTesting(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            return StepSweepOnce(states, world, tuning, log);
        }

        internal bool IsSweepInProgressForTesting()
        {
            return _sweep != null;
        }
    }
}
