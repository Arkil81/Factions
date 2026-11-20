namespace Factions.Core
{
    /// <summary>
    /// Counters and phase timings for one completed <see cref="ProductionManager.Tick"/> call, or
    /// one <see cref="ProductionManager.TryAdvance"/> sweep across a multi-frame resumable cycle.
    /// Timings use <c>Stopwatch.GetTimestamp()</c> ticks so the Torch host can aggregate them
    /// without a Core dependency on host scheduling or SE APIs.
    /// </summary>
    public sealed class ProductionTickTelemetry
    {
        public int Factions { get; internal set; }
        public int OwnedNodes { get; internal set; }
        public int CapabilityBanks { get; internal set; }
        public int SlotsVisited { get; internal set; }
        public int RecipesExamined { get; internal set; }

        /// <summary>Recipes a capability bank did not evaluate because nothing demanded any of their
        /// outputs. They are reached through the bank recipe index rather than visited, so they cost
        /// no scarcity call; this is how many were skipped, and it moves UP as the index does more
        /// work. <see cref="RecipesExamined"/> plus this is the whole usable recipe set the banks
        /// stood in front of.</summary>
        public int RecipesSkippedNoDemand { get; internal set; }
        public int BlockedRecipes { get; internal set; }
        public int BlockedInputPosts { get; internal set; }
        public int DemandPosts { get; internal set; }
        public int DemandRefreshes { get; internal set; }
        public int CatalogOffersScanned { get; internal set; }
        public int InvestmentPasses { get; internal set; }
        public int RefinementPasses { get; internal set; }

        /// <summary>Machine slots whose credited elapsed time hit the catch-up cap this sweep.
        /// Non-zero in steady state means production is being under-credited — a sweep is running
        /// later than the cap allows for, and the slots are losing the difference.</summary>
        public int ElapsedClamps { get; internal set; }

        /// <summary>Number of times a faction's staged cycle was discarded and restarted after a
        /// guard invalidation. Mirrors OperationsTickTelemetry.Restarts. Non-terminal restarts were
        /// previously invisible — only the terminal PRODUCTION_CYCLE_FORCED_SYNC was logged — so a
        /// cycle burning two of its three-restart budget every sweep looked healthy.</summary>
        public int Restarts { get; internal set; }

        public long PreparationTicks { get; internal set; }
        public long CapabilityBankTicks { get; internal set; }
        public long BlockedDemandTicks { get; internal set; }
        public long InvestmentRefinementTicks { get; internal set; }
        public long CatalogTicks { get; internal set; }

        /// <summary>Longest wall-clock lifetime any single faction's cycle reached this sweep, from
        /// BeginCycle to commit-or-discard. The tripwire for finer resume points lengthening the
        /// window in which a commit guard can be invalidated.</summary>
        public long MaxCycleAgeTicks { get; internal set; }

        /// <summary>Merges another completed cycle's counters into this running total. Used by the
        /// resumable sweep, where each faction commits its own transaction-local telemetry and the
        /// sweep-level total must accumulate across factions rather than being replaced by the last
        /// one committed (see ProductionManager.TryCommitTransaction, which unconditionally swaps
        /// LastTelemetry to the just-committed transaction's own object).</summary>
        internal void Add(ProductionTickTelemetry other)
        {
            if (other == null) return;
            Factions += other.Factions;
            OwnedNodes += other.OwnedNodes;
            CapabilityBanks += other.CapabilityBanks;
            ElapsedClamps += other.ElapsedClamps;
            SlotsVisited += other.SlotsVisited;
            RecipesExamined += other.RecipesExamined;
            RecipesSkippedNoDemand += other.RecipesSkippedNoDemand;
            BlockedRecipes += other.BlockedRecipes;
            BlockedInputPosts += other.BlockedInputPosts;
            DemandPosts += other.DemandPosts;
            DemandRefreshes += other.DemandRefreshes;
            CatalogOffersScanned += other.CatalogOffersScanned;
            InvestmentPasses += other.InvestmentPasses;
            RefinementPasses += other.RefinementPasses;
            Restarts += other.Restarts;
            PreparationTicks += other.PreparationTicks;
            CapabilityBankTicks += other.CapabilityBankTicks;
            BlockedDemandTicks += other.BlockedDemandTicks;
            InvestmentRefinementTicks += other.InvestmentRefinementTicks;
            CatalogTicks += other.CatalogTicks;
            if (other.MaxCycleAgeTicks > MaxCycleAgeTicks) MaxCycleAgeTicks = other.MaxCycleAgeTicks;
        }

        internal void Reset()
        {
            Factions = 0;
            OwnedNodes = 0;
            CapabilityBanks = 0;
            SlotsVisited = 0;
            RecipesExamined = 0;
            RecipesSkippedNoDemand = 0;
            BlockedRecipes = 0;
            BlockedInputPosts = 0;
            DemandPosts = 0;
            DemandRefreshes = 0;
            CatalogOffersScanned = 0;
            InvestmentPasses = 0;
            RefinementPasses = 0;
            Restarts = 0;
            ElapsedClamps = 0;
            PreparationTicks = 0;
            CapabilityBankTicks = 0;
            BlockedDemandTicks = 0;
            InvestmentRefinementTicks = 0;
            CatalogTicks = 0;
            MaxCycleAgeTicks = 0;
        }
    }
}
