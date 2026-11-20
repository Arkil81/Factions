using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// What survives of this class after the concentrating allocator was retired on 2026-08-30 —
    /// store buy rows are now sized independently against the whole budget, so nothing divides a
    /// window between rows. The sizing rule and its bounds are covered where the behaviour moved,
    /// in <see cref="StationListingsTests"/>.
    /// </summary>
    public class BudgetAllocationTests
    {
        [Fact]
        public void ShortfallScoreDiscriminatesWhereScarcityWouldSaturate()
        {
            // Both of the operator's live rows priced at exactly ScarcityMax 3.0, so the multiplier
            // called them equal. The unclamped ratio separates them.
            Assert.Equal(1.0, BudgetAllocation.ShortfallScore(15000, 0));
            Assert.Equal(0.0, BudgetAllocation.ShortfallScore(15000, 15000));
            Assert.True(BudgetAllocation.ShortfallScore(15000, 513) > BudgetAllocation.ShortfallScore(15000, 12000));
        }

        [Fact]
        public void NoReferenceScoresNeutralRatherThanUnwanted()
        {
            // An item the oracle has no opinion about is not thereby known to be unwanted, and a 0
            // would push it behind everything permanently.
            Assert.Equal(BudgetAllocation.NeutralScore, BudgetAllocation.ShortfallScore(0, 100));
        }
    }
}
