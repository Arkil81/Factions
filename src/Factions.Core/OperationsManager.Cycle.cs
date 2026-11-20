using System;
using System.Collections.Generic;

namespace Factions.Core
{
    public sealed partial class OperationsManager
    {
        /// <summary>
        /// Cross-frame sweep over a faction roster — the Operations counterpart to
        /// <see cref="ProductionManager"/>'s <c>ProductionSweepCursor</c>. Lives for as long as a
        /// sweep is in progress; null between sweeps. Faction identity is tracked by tag, not by
        /// <see cref="FactionState"/> reference or list index, so the sweep survives the host's
        /// roster changing underneath it between <see cref="TryAdvance"/> calls.
        /// </summary>
        private sealed class OperationsSweepCursor
        {
            internal List<string> FactionTagOrder;
            internal int NextIndex;
            internal OperationsCycleState CurrentCycle;
        }

        // Unlike ProductionManager.Cycle.cs's phase split (chosen to preserve RunPostPasses's exact
        // sub-call order across several genuinely-scalar phases), Operations' expensive work is
        // concentrated in per-vector dispatch/fallback (live-measured 2026-08-14: 94.7% of a
        // representative faction pass), so DispatchingVectors — one MaterialVector's dispatch
        // decision per step — is the phase that actually matters for bounding frame time.
        // MatchingItems is coarser (one whole item group's matching per step, not per-target/per-
        // candidate) because matching itself measured cheap (4.2%) relative to dispatch; this mirrors
        // Production's own PostPassesScalar precedent of not decomposing to the finest conceivable
        // grain when the cost profile doesn't demand it.
        private enum OperationsCyclePhase
        {
            PreparingFaction,
            MatchingItems,
            DispatchingExtraction,
            DispatchingVectors,
            DispatchingAutoMine,
            PublishingFallbacks,
            Complete,
        }

        /// <summary>One faction's in-flight staged Operations cycle, resumable across many
        /// <see cref="TryAdvance"/> calls. Nothing here is visible outside the cycle until
        /// <see cref="OperationsStagedWorldEffects.TryCommit"/> succeeds.</summary>
        private sealed class OperationsCycleState
        {
            internal FactionState State;
            internal OperationsCycleCommitGuard Guard;
            internal OperationsStagedWorldEffects Effects;
            internal StagedOperationsExecutionContext Ctx;
            internal FactionTickContext Context;

            internal OperationsCyclePhase Phase;

            internal List<DemandEntry> AllDemands;
            internal List<SpatialNode> OwnedNodes;
            internal Dictionary<string, List<DemandEntry>> DemandsByItem;
            internal List<string> ItemKeys;
            internal int ItemIndex;
            internal bool HasCargoShipAnywhere;
            /// <summary>Mine's counterpart of <see cref="HasCargoShipAnywhere"/>, already reduced to
            /// the decision -- see <c>OperationsManager.MineBootstrapAllowed</c>. Frozen at the same
            /// point in the cycle for the same reason: the ownership reading scans PendingFleets.</summary>
            internal bool AllowMineBootstrap;
            internal HashSet<(string NodeId, string ItemKey)> InFlightHaulPairs;
            internal Dictionary<(string NodeId, string ItemKey), long> InFlightHaulQuantities;

            internal List<MaterialVector> PendingVectors;
            /// <summary>This cycle's unserved rows, accumulated across MatchingItems and staged for
            /// publication at commit — so a guard invalidation discards them with everything else
            /// rather than leaving a half-built set on the live FactionState.</summary>
            internal List<UnservedDemand> UnservedDemands;
            internal int VectorIndex;
            // Built once at the MatchingItems -> DispatchingVectors transition, when every item's
            // vectors are known: which of them share a delivery node, and which have already been
            // folded onto another convoy. Rebuilt from PendingVectors when a guard-invalidated
            // cycle restarts, like every other derived field here.
            internal HaulVectorPool VectorPool;

            /// <summary>This pass's extraction plan (null for a player faction), planned in
            /// PreparingFaction before anything is staged and kept as the faction's previous plan only
            /// when the cycle commits (<c>OperationsManager.CommitExtractionPlan</c>).</summary>
            internal List<StandingExtractionOrder> ExtractionPlan;
            /// <summary>The plan's open orders, neediest first, walked in DispatchingExtraction --
            /// before DispatchingVectors, as in TickOneFaction.</summary>
            internal List<StandingExtractionOrder> ExtractionOrders;
            internal int ExtractionIndex;
            /// <summary>The order at <see cref="ExtractionIndex"/> while its walk is in progress: one
            /// step walks it for at most <see cref="FactionTuning.ExtractionMineAttemptsPerStep"/> Mine
            /// trips and the next step resumes it. Null between orders.</summary>
            internal ExtractionOrderWalk ExtractionWalk;

            // Standing Mine orders (FactionState.StandingOrders), snapshotted once per cycle at
            // PreparingFaction like ItemKeys -- one order's whole catchment scan per TryAdvance call
            // (see OperationsManager.ScanStandingMineOrder's own doc comment for why it isn't
            // sub-chunked any finer). Snapshotted IN RANK ORDER, so a resumed cycle scans the same
            // sequence it started with even if the player re-ranks mid-cycle (the commit guard
            // invalidates that cycle anyway; this only keeps the resumed pass self-consistent).
            internal List<string> AutoMineOrderKeys;
            internal int AutoMineIndex;
            // Cross-order accounting for this faction's whole scan. A local in the synchronous path;
            // here it must outlive the TryAdvance call that starts the phase, since orders are
            // scanned one per call. See StandingMineScanPass for what it is protecting against.
            internal StandingMineScanPass AutoMinePass;

            // Local aggregation mirroring TickOneFaction's own end-of-body totals
            // (_lastTelemetry.DirectDispatches += directDispatchCount, etc.) — published to
            // _lastTelemetry once, in PublishingFallbacks, exactly like the synchronous path.
            internal int DispatchedCount, DirectDispatchCount, NoShipCount, RouteInfeasibleCount, LoadFailCount, TeleportedCount;
            /// <summary>Convoys the FAC-232 load gate refused. The staged cycle is the path the live
            /// host actually runs, so a counter wired only into TickOneFaction would read 0 forever.</summary>
            internal int LoadGatedCount;
            /// <summary>Vectors refused because the acting faction no longer owns the delivery node
            /// or the source. <para>Provenance: FAC-278.</para></summary>
            internal int NotOwnedCount;

            // OPS_TICK_PERF diagnostics (see OperationsManager.MaybeLogOpsTickPerf) — captured/
            // accumulated across every StepSweepOnce call this cycle spans, so the eventual perf line
            // (emitted once, in PublishingFallbacks) reports this faction's own real elapsed time, not
            // wall-clock-including-idle-gaps between resumed frames.
            internal OpsPerfBaseline Baseline;
            internal long TotalElapsedTicks, MatchElapsedTicks, DispatchElapsedTicks;
        }

        /// <summary>Extraction orders done: rank the Haul vectors and enter the vector pass.
        /// <para>Called from both exits of <see cref="OperationsCyclePhase.DispatchingExtraction"/>
        /// (no orders at all, and the last order), so the two cannot drift apart.</para></summary>
        private static void StartDispatching(OperationsCycleState cycle, FactionTuning tuning)
        {
            SortByUrgency(cycle.PendingVectors);
            cycle.VectorPool = new HaulVectorPool(cycle.PendingVectors, tuning);
            cycle.VectorIndex = 0;
            cycle.Phase = OperationsCyclePhase.DispatchingVectors;
        }

        // No restart-budget escape hatch here, unlike Production's RestartBudgetLimit +
        // RunFactionSynchronously fallback: HANDOVER 2026-08-14's Slice 3 architecture requires
        // Operations to discard and restart on every guard invalidation, with telemetry, and never
        // fall back to a whole-faction synchronous run on the Torch update thread.
        private OperationsSweepCursor _sweep;

        /// <summary>
        /// Advances the current Operations sweep over <paramref name="states"/> by as much bounded
        /// work as fits before <paramref name="deadline"/> expires, resuming a prior sweep if one is
        /// in progress or starting a fresh one otherwise. Returns true once every faction in the
        /// roster has had a cycle committed (or skipped for being dormant) — the caller may then
        /// reschedule its next sweep on its normal interval. Returns false if the deadline was hit
        /// before the sweep finished; the caller must call this again rather than starting a new
        /// sweep, so an in-flight faction cycle is never abandoned mid-way.
        /// </summary>
        public bool TryAdvance(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            FrameDeadline deadline, List<string> log)
        {
            if (_sweep == null)
            {
                _sweep = new OperationsSweepCursor { FactionTagOrder = new List<string>(), NextIndex = 0 };
                foreach (FactionState st in states) _sweep.FactionTagOrder.Add(st.Tag);
                _lastTelemetry = new OperationsTickTelemetry();
            }

            while (!deadline.IsExpired())
            {
                if (StepSweepOnce(states, world, tuning, log))
                    return true;
            }
            _lastTelemetry.Yields++;
            return false;
        }

        /// <summary>
        /// Runs exactly one step of the sweep state machine — either the sweep-complete check, a
        /// faction-selection step, one bounded cycle-phase unit, or one commit-attempt-and-restart
        /// decision — with no deadline check of its own.
        ///
        /// <para>Unlike <see cref="ProductionManager.Cycle"/>'s <c>StepSweepOnce</c> (which always
        /// returns false after advancing past a faction, needing one extra call to notice the roster
        /// is exhausted), this checks for sweep completion immediately after every
        /// <c>_sweep.NextIndex</c> advance via <see cref="AdvanceOrFinishSweep"/> — required by
        /// <c>OperationsCycleTests.ResumedSweep_PublishesOneFactionAtomically_AndMatchesSynchronousTick</c>'s
        /// RED contract, which asserts the world is unpublished on every step where <c>complete</c> is
        /// false; a deferred completion signal would report the commit step itself as "not complete"
        /// even though it just published.</para>
        /// </summary>
        private bool StepSweepOnce(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            if (_sweep.CurrentCycle == null)
            {
                if (_sweep.NextIndex >= _sweep.FactionTagOrder.Count)
                {
                    _sweep = null;
                    _lastTelemetry.SweepsCompleted++;
                    return true;
                }

                // No IsAutonomyDisabled check -- see the matching comment in OperationsManager.cs.
                FactionState st = ResolveFaction(states, _sweep.FactionTagOrder[_sweep.NextIndex]);
                if (st == null || FactionLifecycle.IsDormant(st, world.UtcNow))
                {
                    PublishUnservedDemands(st, null);
                    _sweep.NextIndex++;
                    return AdvanceOrFinishSweep();
                }
                FuelMarket.RefreshHolds(states, world, tuning);
                _sweep.CurrentCycle = BeginOperationsCycle(st, world);
                return false;
            }

            OperationsCycleState cycle = _sweep.CurrentCycle;
            long stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bool phaseComplete = AdvanceCyclePhaseStep(cycle, world, tuning);
            cycle.TotalElapsedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stepStart;
            if (!phaseComplete)
                return false; // one bounded unit done; caller re-checks the deadline before the next

            FactionState current = ResolveFaction(states, cycle.State.Tag);
            bool committed = current != null && CommitOperationsCycle(cycle, current, world, log);
            if (committed)
            {
                CommitExtractionPlan(current, cycle.ExtractionPlan, world.UtcNow, tuning, log != null ? (Action<string>)log.Add : null);
                _sweep.CurrentCycle = null;
                _sweep.NextIndex++;
                return AdvanceOrFinishSweep();
            }

            if (current == null)
            {
                // The faction disappeared from the roster entirely mid-cycle (forgotten). Nothing
                // to retry or fall back to; just move on.
                _sweep.CurrentCycle = null;
                _sweep.NextIndex++;
                return AdvanceOrFinishSweep();
            }

            _lastTelemetry.Restarts++;
            // Re-check dormancy before restarting: the guard invalidation that brought us here can
            // itself be *caused* by the faction going dormant mid-cycle (OperationsCycleCommitGuard
            // compares DormantUntilUtc), and unlike the initial faction-selection branch above, a
            // naive restart would otherwise commit a full Operations pass -- real Haul dispatch,
            // inventory movement -- for a faction that should now be inert.
            if (FactionLifecycle.IsDormant(current, world.UtcNow))
            {
                PublishUnservedDemands(current, null);
                _sweep.CurrentCycle = null;
                _sweep.NextIndex++;
                return AdvanceOrFinishSweep();
            }
            _sweep.CurrentCycle = BeginOperationsCycle(current, world);
            return false;
        }

        /// <summary>Checks whether advancing <c>_sweep.NextIndex</c> just exhausted the roster and, if
        /// so, ends the sweep in the same call rather than deferring to a redundant extra step.</summary>
        private bool AdvanceOrFinishSweep()
        {
            if (_sweep.NextIndex >= _sweep.FactionTagOrder.Count)
            {
                _sweep = null;
                _lastTelemetry.SweepsCompleted++;
                return true;
            }
            return false;
        }

        private static FactionState ResolveFaction(IList<FactionState> states, string tag)
        {
            foreach (FactionState st in states)
                if (string.Equals(st.Tag, tag, StringComparison.Ordinal)) return st;
            return null;
        }

        private OperationsCycleState BeginOperationsCycle(FactionState st, WorldModel world)
        {
            OperationsCycleCommitGuard guard = OperationsCycleCommitGuard.Capture(st, world);
            OperationsStagedWorldEffects effects = OperationsStagedWorldEffects.Capture(st, world);
            var ctx = new StagedOperationsExecutionContext(world, effects, world.UtcNow);
            return new OperationsCycleState
            {
                State = st,
                Guard = guard,
                Effects = effects,
                Ctx = ctx,
                Context = new FactionTickContext(),
                UnservedDemands = new List<UnservedDemand>(),
                Phase = OperationsCyclePhase.PreparingFaction,
                Baseline = OpsPerfBaseline.Capture(_lastTelemetry),
            };
        }

        /// <summary>Advances one bounded unit of work for <paramref name="cycle"/>. Returns true once
        /// every phase has run and the cycle is ready to commit.</summary>
        private bool AdvanceCyclePhaseStep(OperationsCycleState cycle, WorldModel world, FactionTuning tuning)
        {
            switch (cycle.Phase)
            {
                case OperationsCyclePhase.PreparingFaction:
                {
                    // Semantic counters below (Factions/Demands/OwnedNodes/ItemGroups/Targets/Vectors)
                    // can double-count across a guard-invalidated restart of the same faction, exactly
                    // like ProductionManager.Cycle.cs's BankUnit accounting — diagnostic-only
                    // (perf.json), never read by decision logic; documented, accepted limitation.
                    long prepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    cycle.AllDemands = cycle.Ctx.SnapshotItemDemands(cycle.State.Tag);
                    cycle.OwnedNodes = new List<SpatialNode>();
                    foreach (SpatialNode n in cycle.Ctx.OwnedNodes(cycle.State.Tag)) cycle.OwnedNodes.Add(n);

                    // Planned before anything is staged, like TickOneFaction, so both paths plan from
                    // the same world; kept as the previous plan only if this cycle commits.
                    cycle.ExtractionPlan = PlanExtraction(cycle.State, cycle.OwnedNodes, cycle.Ctx, world, tuning);
                    cycle.ExtractionOrders = DispatchableExtractionOrders(cycle.ExtractionPlan);
                    cycle.ExtractionIndex = 0;

                    // A standing auto-mine order or an NPC extraction order must still run even when
                    // this faction has zero live demand -- that's precisely the gap it exists to close
                    // (see TickOneFaction's matching relaxation). Every later phase degrades to cheap
                    // no-op passes over an empty demand set when there's no demand and no order,
                    // preserving today's fast early-out for the common case.
                    if (cycle.AllDemands.Count == 0 && cycle.State.StandingOrders.Count == 0
                        && cycle.ExtractionOrders.Count == 0)
                    {
                        cycle.Phase = OperationsCyclePhase.Complete;
                        return true;
                    }
                    _lastTelemetry.Factions++;
                    _lastTelemetry.Demands += cycle.AllDemands.Count;
                    _lastTelemetry.OwnedNodes += cycle.OwnedNodes.Count;
                    _lastTelemetry.PreparationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - prepStart;

                    long historyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    FoldDemandHistory(cycle.OwnedNodes, cycle.Ctx, tuning);
                    DecayFleetRequirements(cycle.State, tuning);
                    _lastTelemetry.DemandHistoryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - historyStart;

                    prepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    cycle.DemandsByItem = new Dictionary<string, List<DemandEntry>>();
                    foreach (DemandEntry demand in cycle.AllDemands)
                    {
                        string sub = demand.Kind.Substring(5); // "Item:X" → "X"
                        List<DemandEntry> bucket;
                        if (!cycle.DemandsByItem.TryGetValue(sub, out bucket))
                        {
                            bucket = new List<DemandEntry>();
                            cycle.DemandsByItem[sub] = bucket;
                        }
                        bucket.Add(demand);
                    }
                    foreach (List<DemandEntry> itemDemands in cycle.DemandsByItem.Values)
                    {
                        var destinations = new HashSet<string>(StringComparer.Ordinal);
                        foreach (DemandEntry demand in itemDemands)
                            if (demand.NodeId != null) destinations.Add(demand.NodeId);
                        foreach (string destinationNodeId in destinations)
                        {
                            int count;
                            cycle.Context.DestinationItemCounts.TryGetValue(destinationNodeId, out count);
                            cycle.Context.DestinationItemCounts[destinationNodeId] = count + 1;
                        }
                    }
                    _lastTelemetry.ItemGroups += cycle.DemandsByItem.Count;
                    _lastTelemetry.PreparationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - prepStart;
                    cycle.ItemKeys = new List<string>(cycle.DemandsByItem.Keys);
                    cycle.ItemIndex = 0;

                    cycle.HasCargoShipAnywhere = NetworkOwnsSuitable(cycle.Ctx, cycle.State, cycle.OwnedNodes, ActionKind.Haul, world.PrefabCatalogue);
                    cycle.AllowMineBootstrap = MineBootstrapAllowed(cycle.State, cycle.OwnedNodes, cycle.Ctx, world, tuning);

                    // Same frozen-once-per-cycle in-flight index as TickOneFaction's own claims
                    // block — read straight off the live FactionState (Operations never mutates
                    // PendingFleets/ExternalHaulClaims until commit, so this is correct for both the
                    // captured-guard instant and everything read through it during this cycle).
                    long claimsStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    cycle.InFlightHaulPairs = new HashSet<(string NodeId, string ItemKey)>();
                    IndexInFlightHaulPairs(cycle.State, cycle.InFlightHaulPairs);
                    IndexInFlightMine(cycle.State, cycle.Context);
                    cycle.InFlightHaulQuantities = null;
                    _lastTelemetry.ClaimIndexTicks += System.Diagnostics.Stopwatch.GetTimestamp() - claimsStart;

                    cycle.PendingVectors = new List<MaterialVector>();
                    ResetRequirementPass();
                    cycle.AutoMineOrderKeys = new List<string>();
                    foreach (StandingOrder mineOrder in cycle.State.StandingOrdersByRank(StandingOrderKind.Mine))
                        cycle.AutoMineOrderKeys.Add(mineOrder.Id);
                    cycle.AutoMineIndex = 0;
                    cycle.AutoMinePass = new StandingMineScanPass();
                    cycle.Phase = OperationsCyclePhase.MatchingItems;
                    return false;
                }
                case OperationsCyclePhase.MatchingItems:
                {
                    if (cycle.ItemIndex >= cycle.ItemKeys.Count)
                    {
                        cycle.ItemIndex = 0;
                        cycle.Phase = OperationsCyclePhase.DispatchingExtraction;
                        return false;
                    }

                    string subTypeId = cycle.ItemKeys[cycle.ItemIndex];
                    List<DemandEntry> itemDemands = cycle.DemandsByItem[subTypeId];

                    List<DemandEntry> pending = new List<DemandEntry>();
                    List<DemandEntry> flying = null;
                    foreach (DemandEntry d in itemDemands)
                    {
                        if (d.NodeId == null) continue;
                        if (!cycle.InFlightHaulPairs.Contains((d.NodeId, subTypeId))) pending.Add(d);
                        else (flying ?? (flying = new List<DemandEntry>())).Add(d);
                    }
                    if (flying != null)
                        PublishInFlightRemainders(cycle.State.Tag, subTypeId, flying,
                            cycle.InFlightHaulQuantities ?? (cycle.InFlightHaulQuantities = InFlightHaulQuantities(cycle.State)),
                            cycle.Ctx, world, tuning, cycle.UnservedDemands);
                    _lastTelemetry.Targets += pending.Count;

                    long matchStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    List<MaterialVector> vectors = MatchVectors(cycle.State.Tag, subTypeId, pending,
                        cycle.OwnedNodes, cycle.Ctx, world, tuning, cycle.Context, cycle.UnservedDemands);
                    long matchElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - matchStart;
                    cycle.MatchElapsedTicks += matchElapsed;
                    _lastTelemetry.TargetMatchingTicks += matchElapsed;
                    cycle.PendingVectors.AddRange(vectors);
                    _lastTelemetry.Vectors += vectors.Count;
                    // The staged cycle is the path the live host actually runs -- recording only in
                    // TickOneFaction would leave every live requirement empty forever.
                    ObserveFleetRequirement(cycle.State, vectors, tuning);

                    cycle.ItemIndex++;
                    if (cycle.ItemIndex >= cycle.ItemKeys.Count)
                    {
                        cycle.ItemIndex = 0;
                        cycle.Phase = OperationsCyclePhase.DispatchingExtraction;
                    }
                    return false;
                }
                case OperationsCyclePhase.DispatchingExtraction:
                {
                    // NPC extraction orders' catchment walks, before any Haul vector dispatches -- the
                    // same position and the same reason as TickOneFaction: a Haul pass run first could
                    // spend the drill a starved hub's order was planned for. Shares AutoMinePass with
                    // the player orders, so claims stay pass-wide. One step walks the current order
                    // for at most ExtractionMineAttemptsPerStep trips; a longer walk resumes on the
                    // next step, so no step's work grows with the catchment.
                    if (cycle.ExtractionWalk == null)
                    {
                        if (cycle.ExtractionIndex >= cycle.ExtractionOrders.Count)
                        {
                            StartDispatching(cycle, tuning);
                            return false;
                        }
                        cycle.ExtractionWalk = BeginExtractionWalk(cycle.State,
                            cycle.ExtractionOrders[cycle.ExtractionIndex], cycle.AutoMinePass, cycle.OwnedNodes,
                            cycle.Ctx, world, tuning, cycle.Context, cycle.AllowMineBootstrap);
                    }
                    if (cycle.ExtractionWalk != null
                        && !StepExtractionWalk(cycle.State, cycle.ExtractionWalk, cycle.AutoMinePass, cycle.OwnedNodes,
                            cycle.Ctx, world, tuning, cycle.Context, tuning.ExtractionMineAttemptsPerStep))
                        return false;
                    cycle.ExtractionWalk = null;
                    cycle.ExtractionIndex++;
                    if (cycle.ExtractionIndex >= cycle.ExtractionOrders.Count) StartDispatching(cycle, tuning);
                    return false;
                }
                case OperationsCyclePhase.DispatchingVectors:
                {
                    // One Haul vector's dispatch decision per TryAdvance call, most urgent first.
                    if (cycle.VectorIndex >= cycle.PendingVectors.Count)
                    {
                        cycle.Phase = OperationsCyclePhase.DispatchingAutoMine;
                        return false;
                    }

                    // A vector already folded onto an earlier convoy as an extra pickup stop is
                    // skipped, not re-dispatched — see OperationsManager.HaulVectorPool.
                    if (cycle.VectorPool.IsConsumed(cycle.VectorIndex))
                    {
                        cycle.VectorIndex++;
                        if (cycle.VectorIndex >= cycle.PendingVectors.Count)
                            cycle.Phase = OperationsCyclePhase.DispatchingAutoMine;
                        return false;
                    }

                    MaterialVector v = cycle.PendingVectors[cycle.VectorIndex];
                    // Claimed on take, not on success — same reason as the synchronous loop: a
                    // vector that gets its own convoy must not also be folded onto a later one.
                    cycle.VectorPool.Consume(cycle.VectorIndex);
                    long dispatchStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (cycle.HasCargoShipAnywhere)
                    {
                        // Same nested-ticks subtraction as TickOneFaction's own dispatch loop: DispatchHaul
                        // can internally add to RoutePlanningTicks/AlternatePickupTicks/ShipyardFallbackTicks
                        // (via GetRoute/GetAlternateSource/the Shipyard fallback path), which must not also
                        // be double-counted into DirectDispatchTicks.
                        long directStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        long routeBefore = _lastTelemetry.RoutePlanningTicks;
                        long alternateBefore = _lastTelemetry.AlternatePickupTicks;
                        long shipyardBefore = _lastTelemetry.ShipyardFallbackTicks;
                        HaulOutcome outcome = DispatchHaul(cycle.State, cycle.VectorIndex, cycle.VectorPool,
                            cycle.Ctx, world, tuning, cycle.Context);
                        long nested = (_lastTelemetry.RoutePlanningTicks - routeBefore)
                            + (_lastTelemetry.AlternatePickupTicks - alternateBefore)
                            + (_lastTelemetry.ShipyardFallbackTicks - shipyardBefore);
                        long directElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - directStart - nested;
                        if (directElapsed > 0) _lastTelemetry.DirectDispatchTicks += directElapsed;
                        switch (outcome)
                        {
                            case HaulOutcome.Dispatched:
                                cycle.DirectDispatchCount++;
                                cycle.DispatchedCount++;
                                break;
                            case HaulOutcome.DispatchedViaPickup:
                                cycle.DispatchedCount++;
                                break;
                            case HaulOutcome.NoShipAnywhere:  cycle.NoShipCount++;          break;
                            case HaulOutcome.RouteInfeasible: cycle.RouteInfeasibleCount++; break;
                            case HaulOutcome.LoadCargoFailed: cycle.LoadFailCount++;        break;
                            case HaulOutcome.LoadTooLight:
                                cycle.LoadGatedCount++;
                                cycle.VectorPool.Release(cycle.VectorIndex);   // nothing flew; a later convoy may carry it
                                break;
                            case HaulOutcome.DestNotOwned:
                            case HaulOutcome.SourceNotOwned:  cycle.NotOwnedCount++;        break;
                        }
                    }
                    else { RealizeBootstrapInstant(v, cycle.Ctx); cycle.TeleportedCount++; }
                    cycle.DispatchElapsedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - dispatchStart;

                    cycle.VectorIndex++;
                    if (cycle.VectorIndex >= cycle.PendingVectors.Count)
                        cycle.Phase = OperationsCyclePhase.DispatchingAutoMine;
                    return false;
                }
                case OperationsCyclePhase.DispatchingAutoMine:
                {
                    // One standing Mine order's whole catchment scan per TryAdvance call -- see
                    // OperationsManager.ScanStandingMineOrder's doc comment for why this isn't
                    // sub-chunked any finer (a hub's catchment is inherently local/small, and the
                    // staged context's AppendPendingFleet is write-only, so a finer per-candidate
                    // cursor would need to persist a running open-slots counter across resumed calls
                    // for no real benefit).
                    if (cycle.AutoMineIndex >= cycle.AutoMineOrderKeys.Count)
                    {
                        cycle.Phase = OperationsCyclePhase.PublishingFallbacks;
                        return false;
                    }

                    // Looked up by id rather than held as a reference: the order may have been
                    // removed since the snapshot, and a removed order must simply not scan.
                    StandingOrder order = cycle.State.FindStandingOrder(cycle.AutoMineOrderKeys[cycle.AutoMineIndex]);
                    if (order != null && order.Kind == StandingOrderKind.Mine && !order.Paused
                        && world.UtcNow >= order.NextScanUtc)
                        ScanStandingMineOrder(cycle.State, order, cycle.AutoMinePass, cycle.OwnedNodes, cycle.Ctx, world, tuning, cycle.Context);

                    cycle.AutoMineIndex++;
                    if (cycle.AutoMineIndex >= cycle.AutoMineOrderKeys.Count) cycle.Phase = OperationsCyclePhase.PublishingFallbacks;
                    return false;
                }
                case OperationsCyclePhase.PublishingFallbacks:
                {
                    long publishStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    PublishFallbacks(tuning, cycle.Context, cycle.Ctx);
                    cycle.Effects.StageUnservedDemands(cycle.UnservedDemands);
                    cycle.TotalElapsedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - publishStart;
                    _lastTelemetry.DirectDispatches += cycle.DirectDispatchCount;
                    _lastTelemetry.NoShipOutcomes += cycle.NoShipCount;
                    _lastTelemetry.NotOwnedOutcomes += cycle.NotOwnedCount;
                    _lastTelemetry.RouteInfeasibleOutcomes += cycle.RouteInfeasibleCount;
                    _lastTelemetry.LoadCargoFailures += cycle.LoadFailCount;

                    MaybeLogOpsTickPerf(cycle.Ctx, cycle.State.Tag, cycle.Baseline, cycle.DemandsByItem.Count,
                        cycle.AllDemands.Count, cycle.OwnedNodes.Count, cycle.PendingVectors.Count,
                        cycle.InFlightHaulPairs.Count, cycle.DispatchedCount, cycle.NoShipCount,
                        cycle.RouteInfeasibleCount, cycle.LoadFailCount, cycle.LoadGatedCount,
                        cycle.NotOwnedCount, cycle.TeleportedCount,
                        cycle.TotalElapsedTicks, cycle.MatchElapsedTicks, cycle.DispatchElapsedTicks);

                    cycle.Phase = OperationsCyclePhase.Complete;
                    return true;
                }
                default:
                    return true;
            }
        }

        /// <summary>
        /// Publishes a completed cycle's staged effects. Returns false without publishing anything if
        /// the guard has been invalidated — the caller discards and restarts, never falling back to a
        /// synchronous whole-faction run.
        /// </summary>
        private bool CommitOperationsCycle(OperationsCycleState cycle, FactionState currentState, WorldModel world, List<string> log)
        {
            return cycle.Effects.TryCommit(cycle.Guard, currentState, world, log);
        }

        // ---- test-only seam ----------------------------------------------------------------------
        // Deterministic step-by-step access to the sweep state machine, mirroring
        // ProductionManager.Cycle.cs's BeginSweepForTesting/StepSweepOnceForTesting exactly, so tests
        // can drive exact bounded units — including interleaving WorldModel mutation between them to
        // force a guard invalidation — without depending on real elapsed time.

        internal void BeginSweepForTesting(IList<FactionState> states)
        {
            _sweep = new OperationsSweepCursor { FactionTagOrder = new List<string>(), NextIndex = 0 };
            foreach (FactionState st in states) _sweep.FactionTagOrder.Add(st.Tag);
            _lastTelemetry = new OperationsTickTelemetry();
        }

        internal bool StepSweepOnceForTesting(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            return StepSweepOnce(states, world, tuning, log);
        }

        internal bool IsSweepInProgressForTesting()
        {
            return _sweep != null;
        }

        internal bool IsFactionCycleInProgressForTesting()
        {
            return _sweep != null && _sweep.CurrentCycle != null;
        }
    }
}
