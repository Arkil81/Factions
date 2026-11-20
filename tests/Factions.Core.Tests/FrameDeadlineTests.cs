using System.Diagnostics;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers the accessors the shared frame budget needs. IsExpired's behaviour is pinned
    /// here too because the three resumable managers and their equivalence suites depend on it.</summary>
    public sealed class FrameDeadlineTests
    {
        [Fact]
        public void ZeroBudget_IsExpiredImmediately()
        {
            Assert.True(new FrameDeadline(0, 0.0).IsExpired());
        }

        [Fact]
        public void Unbounded_IsNeverExpired()
        {
            FrameDeadline d = FrameDeadline.Unbounded();
            Assert.False(d.IsExpired());
            Assert.True(d.RemainingMilliseconds() > 1000.0);
        }

        [Fact]
        public void BudgetMilliseconds_RoundTrips()
        {
            Assert.Equal(0.4, new FrameDeadline(Stopwatch.GetTimestamp(), 0.4).BudgetMilliseconds, 6);
        }

        [Fact]
        public void RemainingMilliseconds_GoesNegativeOnceTheBudgetIsSpent()
        {
            // Start a full budget's worth in the past, so the deadline is deterministically expired
            // without sleeping: remaining must report roughly minus one budget.
            long oneMs = Stopwatch.Frequency / 1000;
            var spent = new FrameDeadline(Stopwatch.GetTimestamp() - 2 * oneMs, 1.0);

            Assert.True(spent.IsExpired());
            Assert.True(spent.RemainingMilliseconds() < 0.0);
            Assert.True(spent.ElapsedMilliseconds() >= 1.0);
        }

        [Fact]
        public void FreshDeadline_ReportsCloseToTheWholeBudgetRemaining()
        {
            // Remaining and Elapsed each read the clock, so they cannot sum to exactly the budget;
            // what matters for admission is that a fresh deadline offers ~the whole budget.
            var d = new FrameDeadline(Stopwatch.GetTimestamp(), 5.0);
            double remaining = d.RemainingMilliseconds();
            Assert.True(remaining <= 5.0, "remaining " + remaining + " exceeded the budget");
            Assert.True(remaining > 4.0, "remaining " + remaining + " lost too much of a fresh budget");
        }
    }
}
