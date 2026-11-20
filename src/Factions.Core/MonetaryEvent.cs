using System;

namespace Factions.Core
{
    /// <summary>Structured monetary event emitted by the world economy for observability surfaces.</summary>
    public sealed class MonetaryEvent
    {
        public DateTime UtcNow { get; set; }
        public string Scope { get; set; }
        public string FactionTag { get; set; }
        public string Type { get; set; }
        public long Amount { get; set; }
        public long TreasuryAfter { get; set; }
        public long DebtAfter { get; set; }
        public long MoneySupplyAfter { get; set; }
        public long MoneySupplyGapAfter { get; set; }
        public string Message { get; set; }
    }
}
