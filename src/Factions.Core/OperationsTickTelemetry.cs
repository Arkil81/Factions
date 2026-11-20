namespace Factions.Core
{
    /// <summary>Aggregated timing and semantic counters for one synchronous Operations tick.
    /// Stopwatch timestamp ticks are used so the host can aggregate Core-only data without any
    /// Torch/SE dependency. Phase timings cover non-overlapping manager regions; route planning is
    /// subtracted from the direct/alternate dispatch spans that invoke it.</summary>
    public sealed class OperationsTickTelemetry
    {
        public int Factions { get; internal set; }
        public int OwnedNodes { get; internal set; }
        public int Demands { get; internal set; }
        public int ItemGroups { get; internal set; }
        public int Targets { get; internal set; }
        public int CandidateSources { get; internal set; }
        public int DistanceOrderBuilds { get; internal set; }
        public int Vectors { get; internal set; }
        public int RouteCacheHits { get; internal set; }
        public int RouteCacheMisses { get; internal set; }
        public int DirectDispatches { get; internal set; }
        public int PickupDispatches { get; internal set; }
        /// <summary>Vectors folded into another vector's convoy as an extra pickup stop rather
        /// than dispatching a convoy of their own — see OperationsManager's Haul consolidation.
        /// Counts the extras only, so a convoy collecting from three nodes contributes 2.</summary>
        public int ConsolidatedPickups { get; internal set; }
        public int NoShipOutcomes { get; internal set; }
        public int RouteInfeasibleOutcomes { get; internal set; }
        public int LoadCargoFailures { get; internal set; }
        /// <summary>Haul vectors refused because the acting faction no longer owned the delivery node
        /// or the source at dispatch time. <para>Provenance: FAC-278.</para></summary>
        public int NotOwnedOutcomes { get; internal set; }
        public int AlternateSourceScans { get; internal set; }
        public int ShipyardScans { get; internal set; }
        public int ShipSuitabilityChecks { get; internal set; }
        public int FallbackCandidates { get; internal set; }

        /// <summary>Cargo convoys dispatched on a hull carrying a capability cargo work does not need
        /// — a drill or weapons (<see cref="ShipPool.SpecialistCapabilityCount"/>). <see cref="DualRoleHulls"/>
        /// counts the subset carrying both, the scarcest kind.
        /// <para>These count the OUTCOME, not which of <c>PreferGeneralist</c>'s passes won.
        /// That preference falls back silently by design — its last pass is unrestricted — so from
        /// dispatch counts alone a working preference and one whose every pass empties look
        /// identical. Its predecessor sat at a measured 64% for weeks looking exactly like it worked.
        /// See docs/issues/FAC-235.</para></summary>
        public int SpecialistHulls { get; internal set; }
        public int DualRoleHulls { get; internal set; }
        public int UniqueFallbackKeys { get; internal set; }
        public int DemandPosts { get; internal set; }
        public int DemandRefreshes { get; internal set; }

        // ---- staged/resumable sweep counters (Slice 3) ------------------------------------------
        /// <summary>Number of TryAdvance invocations that hit the deadline before the sweep finished.
        /// Diagnostic-only, mirrors ProductionTickTelemetry's planned equivalent (docs/handover
        /// 2026-08-14). Not reset per-faction; accumulates for the lifetime of a sweep.</summary>
        public int Yields { get; internal set; }
        /// <summary>Number of times a faction's staged cycle was discarded and restarted after a
        /// guard invalidation.</summary>
        public int Restarts { get; internal set; }
        /// <summary>Number of full roster sweeps completed by this manager instance.</summary>
        public int SweepsCompleted { get; internal set; }

        public long PreparationTicks { get; internal set; }
        public long DemandHistoryTicks { get; internal set; }
        public long ClaimIndexTicks { get; internal set; }
        public long TargetMatchingTicks { get; internal set; }
        public long RoutePlanningTicks { get; internal set; }
        public long DirectDispatchTicks { get; internal set; }
        public long AlternatePickupTicks { get; internal set; }
        /// <summary>Sub-span of <see cref="AlternatePickupTicks"/>: the cached-once ordered network
        /// scan (FindNodesWithHaulStock) run on a cache miss.</summary>
        public long AlternateScanTicks { get; internal set; }
        /// <summary>Sub-span of <see cref="AlternatePickupTicks"/>: the per-candidate revalidation
        /// loop, which reruns on every call because ship stock mutates as vectors dispatch. Route
        /// planning is subtracted, matching the enclosing spans' convention.</summary>
        public long AlternateRevalidateTicks { get; internal set; }
        public long ShipyardFallbackTicks { get; internal set; }
        public long FallbackPublicationTicks { get; internal set; }
        /// <summary>NPC extraction planning (<c>OperationsManager.PlanExtraction</c>). A sub-span of
        /// <see cref="PreparationTicks"/>, which encloses it.</summary>
        public long ExtractionPlanTicks { get; internal set; }
        /// <summary>The NPC extraction orders' catchment walks, catchment build included. No other
        /// span encloses them.</summary>
        public long ExtractionWalkTicks { get; internal set; }
        /// <summary>Mine trips the NPC extraction walks attempted (<c>TryFireMine</c> calls, whatever
        /// the outcome): the unit <see cref="FactionTuning.ExtractionMineAttemptsPerStep"/> budgets.</summary>
        public int ExtractionMineAttempts { get; internal set; }
    }
}
