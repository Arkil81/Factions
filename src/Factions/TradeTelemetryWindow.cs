using Factions.Core;

namespace Factions
{
    /// <summary>Window accumulator for <see cref="TradeTickTelemetry"/>, mirroring
    /// <see cref="OperationsTelemetryWindow"/>'s shape. Recorded once per completed sweep in
    /// <c>FactionRuntime</c>, and served to <c>/api/perf</c> as <c>tradeTelemetry</c>.</summary>
    internal sealed class TradeTelemetryWindow
    {
        public int Calls;
        public long Factions, EligibleFactions, Partners, UnservedSeen, Vectors;
        public long Dispatched, Procured, NoShip, RefusedConsent, Claimed, AssessRefused;
        public long Tier1, Tier2, Tier3, BudgetBlocked;
        public long Yields, Restarts, SweepsCompleted;
        public long EligibilityFilterTicks, DispatchTicks;

        public void Record(TradeTickTelemetry v)
        {
            Calls++;
            Factions += v.Factions; EligibleFactions += v.EligibleFactions;
            Partners += v.Partners; UnservedSeen += v.UnservedSeen; Vectors += v.Vectors;
            Dispatched += v.Dispatched; Procured += v.Procured; NoShip += v.NoShip;
            RefusedConsent += v.RefusedConsent; Claimed += v.Claimed; AssessRefused += v.AssessRefused;
            Tier1 += v.Tier1; Tier2 += v.Tier2; Tier3 += v.Tier3; BudgetBlocked += v.BudgetBlocked;
            Yields += v.Yields; Restarts += v.Restarts; SweepsCompleted += v.SweepsCompleted;
            EligibilityFilterTicks += v.EligibilityFilterTicks; DispatchTicks += v.DispatchTicks;
        }

        public void Reset()
        {
            Calls = 0;
            Factions = EligibleFactions = Partners = UnservedSeen = Vectors = 0;
            Dispatched = Procured = NoShip = RefusedConsent = Claimed = AssessRefused = 0;
            Tier1 = Tier2 = Tier3 = BudgetBlocked = 0;
            Yields = Restarts = SweepsCompleted = 0;
            EligibilityFilterTicks = DispatchTicks = 0;
        }
    }
}
