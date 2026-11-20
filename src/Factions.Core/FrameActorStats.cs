namespace Factions.Core
{
    /// <summary>Read-only snapshot of one actor's scheduling state. A struct so the host can read it
    /// during a perf flush without allocating.</summary>
    public struct FrameActorStats
    {
        public string Name;
        public double IntervalSeconds;
        public double EstimateMilliseconds;
        public double EstimateErrorAvgMilliseconds;
        public double EstimateErrorMaxMilliseconds;
        public double MaxDeferralMilliseconds;
        public double LastForcedOverrunMilliseconds;
        public long Admissions;
        public long Completions;
        public long Yields;
        public long Skips;
        public long NotDue;
        public long ForcedAdmissions;
        public long DeniedByBudget;
        public bool SweepInProgress;
    }
}
