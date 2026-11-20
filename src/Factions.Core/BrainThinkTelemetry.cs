namespace Factions.Core
{
    /// <summary>Per-stage timings for one <see cref="FactionBrain.Think"/> call, in
    /// <c>Stopwatch.GetTimestamp()</c> ticks so the host can aggregate them without a Core
    /// dependency on host scheduling. Diagnostic only; nothing reads these to make a decision.</summary>
    public sealed class BrainThinkTelemetry
    {
        public long SenseTicks { get; internal set; }
        public long TradePriorityTicks { get; internal set; }
        public long ReinforcePriorityTicks { get; internal set; }
        public long ThreatBonusTicks { get; internal set; }
        public long GenerateTicks { get; internal set; }
        public long ScoreTicks { get; internal set; }
        public long SelectTicks { get; internal set; }
        public int Candidates { get; internal set; }
    }
}
