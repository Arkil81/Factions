using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FrameTimingWindowTests
    {
        [Fact]
        public void Record_TracksEveryFrameAndTreatsBudgetBoundaryAsViolation()
        {
            // 1 MHz makes one tick equal one microsecond; the budget is strictly below 0.5 ms.
            var window = new FrameTimingWindow(1000000L, 0.5, 16);

            window.Record(499L);
            window.Record(500L);
            window.Record(501L);

            Assert.Equal(3L, window.CallCount);
            Assert.Equal(1500L, window.TotalTicks);
            Assert.Equal(501L, window.MaxTicks);
            Assert.Equal(2L, window.OverBudgetCount);
        }

        [Fact]
        public void Percentiles_UseConservativeHistogramBucketsAndResetClearsWindow()
        {
            var window = new FrameTimingWindow(1000000L, 0.5, 16);
            window.Record(101L);
            window.Record(201L);
            window.Record(301L);
            window.Record(401L);

            // Percentiles report the bucket upper bound: never a lower value than the measured sample.
            Assert.Equal(0.3, window.PercentileMilliseconds(0.50), 3);
            Assert.Equal(0.5, window.PercentileMilliseconds(0.95), 3);

            window.Reset();

            Assert.Equal(0L, window.CallCount);
            Assert.Equal(0L, window.OverBudgetCount);
            Assert.Equal(0.0, window.PercentileMilliseconds(0.99), 3);
        }

        [Fact]
        public void Percentiles_ResolveAboveTenMilliseconds_RatherThanSaturatingAtTheTopBucket()
        {
            // The live plugin runs at a ~4 ms frame average against a 0.5 ms budget, where the old
            // top edge (10 ms) collapsed p95 and p99 onto the same value and told us nothing.
            var window = new FrameTimingWindow(1000000L, 0.5, 128);

            for (int i = 0; i < 90; i++) window.Record(3000L);   // 3 ms   -> (2, 5] bucket
            for (int i = 0; i < 8;  i++) window.Record(15000L);  // 15 ms  -> (10, 20] bucket
            window.Record(40000L);                               // 40 ms  -> (20, 50] bucket
            window.Record(80000L);                               // 80 ms  -> (50, 100] bucket

            Assert.Equal(5.0,   window.PercentileMilliseconds(0.50), 3);
            Assert.Equal(20.0,  window.PercentileMilliseconds(0.95), 3);
            Assert.Equal(50.0,  window.PercentileMilliseconds(0.99), 3);
            Assert.Equal(80.0,  window.MaxMilliseconds(), 3);
        }

        [Fact]
        public void Percentiles_BeyondTheLastBucketEdge_StillReportTheExactMaximum()
        {
            var window = new FrameTimingWindow(1000000L, 0.5, 16);

            window.Record(250000L); // 250 ms, past the 100 ms top edge -> overflow bucket

            Assert.Equal(250.0, window.PercentileMilliseconds(0.99), 3);
            Assert.Equal(250.0, window.MaxMilliseconds(), 3);
        }

        [Fact]
        public void Percentiles_SubMillisecondResolutionIsUnchangedByTheAddedUpperEdges()
        {
            var window = new FrameTimingWindow(1000000L, 0.5, 16);

            window.Record(9L);   // -> (0, 0.010]
            window.Record(26L);  // -> (0.025, 0.050]
            window.Record(150L); // -> (0.100, 0.200]

            Assert.Equal(0.010, window.PercentileMilliseconds(0.20), 3);
            Assert.Equal(0.200, window.PercentileMilliseconds(0.99), 3);
        }
    
        [Fact]
        public void ExcessMilliseconds_IsZeroWhenEverySampleIsUnderBudget()
        {
            var w = new FrameTimingWindow(1_000_000, 0.5, 64);
            w.Record(100);
            w.Record(200);
            Assert.Equal(0.0, w.ExcessMilliseconds());
        }

        [Fact]
        public void ExcessMilliseconds_SumsOnlyTheOverageAcrossCalls()
        {
            // 1 MHz frequency: 1 tick == 1 us, so a 0.5 ms budget is 500 ticks.
            var w = new FrameTimingWindow(1_000_000, 0.5, 64);
            w.Record(500);   // exactly at budget: an overrun by the >= rule, excess 0
            w.Record(1500);  // 1000 ticks over
            w.Record(100);   // under, contributes nothing
            w.Record(2500);  // 2000 ticks over
            Assert.Equal(3, w.OverBudgetCount);
            Assert.Equal(3.0, w.ExcessMilliseconds(), 6);
        }

        [Fact]
        public void ExcessMilliseconds_RanksFrequentSmallOverrunsAgainstRareLargeOnes()
        {
            // The property that makes excess the re-chunking metric: neither average nor maximum
            // orders these two the same way excess does.
            var rare = new FrameTimingWindow(1_000_000, 0.5, 64);
            rare.Record(20_500);                                  // one 20 ms overrun

            var frequent = new FrameTimingWindow(1_000_000, 0.5, 64);
            for (int i = 0; i < 100; i++) frequent.Record(1_500); // 100 x 1 ms overrun

            Assert.True(rare.MaxMilliseconds() > frequent.MaxMilliseconds());
            Assert.True(frequent.ExcessMilliseconds() > rare.ExcessMilliseconds());
        }

        [Fact]
        public void Reset_ClearsAccumulatedExcess()
        {
            var w = new FrameTimingWindow(1_000_000, 0.5, 64);
            w.Record(5_000);
            Assert.True(w.ExcessMilliseconds() > 0);
            w.Reset();
            Assert.Equal(0.0, w.ExcessMilliseconds());
        }

        [Fact]
        public void Percentiles_DescribeOnlyTheRingWhileTotalsDescribeTheWholeWindow()
        {
            // The failure this guards: a window longer than the ring leaves p99 reading the tail
            // while count/average/maximum read all of it, with nothing on the row saying the two
            // halves disagree.
            var window = new FrameTimingWindow(1_000_000, 0.5, 4);

            window.Record(9_000);                                   // one 9 ms outlier, then four cheap frames
            for (int i = 0; i < 4; i++) window.Record(20);

            Assert.Equal(5L, window.CallCount);                     // the exact statistics see all five
            Assert.Equal(9.0, window.MaxMilliseconds(), 3);
            Assert.Equal(4L, window.SampleCount);                   // the ring evicted the outlier
            Assert.Equal(0.025, window.PercentileMilliseconds(0.99), 3);
        }

        [Fact]
        public void EnsureSampleCapacity_GrowsTheRingAndLeavesExactStatisticsIntact()
        {
            var window = new FrameTimingWindow(1_000_000, 0.5, 4);
            for (int i = 0; i < 4; i++) window.Record(20);
            window.Record(9_000);

            window.EnsureSampleCapacity(64);

            // Growing discards the held samples but never the exact statistics.
            Assert.Equal(5L, window.CallCount);
            Assert.Equal(9_080L, window.TotalTicks);
            Assert.Equal(9.0, window.MaxMilliseconds(), 3);
            Assert.Equal(1L, window.OverBudgetCount);
            Assert.Equal(0L, window.SampleCount);

            // The grown ring outlives the original capacity, so an outlier eight frames back is
            // still in the percentile where the 4-slot ring would have evicted it. Percentiles report
            // the bucket upper bound, so the 9 ms sample reads 10; the 4-slot ring would read 0.025.
            window.Record(9_000);
            for (int i = 0; i < 8; i++) window.Record(20);
            Assert.Equal(9L, window.SampleCount);
            Assert.Equal(10.0, window.PercentileMilliseconds(1.0), 3);
        }

        [Fact]
        public void EnsureSampleCapacity_IgnoresACapacityThatWouldShrinkTheRing()
        {
            var window = new FrameTimingWindow(1_000_000, 0.5, 16);
            for (int i = 0; i < 8; i++) window.Record(20);

            window.EnsureSampleCapacity(4);

            Assert.Equal(8L, window.SampleCount);
        }

        [Fact]
        public void TotalMilliseconds_SumsEveryCall_SoAFrequentCheapActorOutranksARareExpensiveOne()
        {
            // The live 2026-09-08 reading behind this: obs at 0.119 ms x 3594 is the window's largest
            // consumer while yard at 29.579 ms x 1 shows the largest number on the panel.
            var cheapAndFrequent = new FrameTimingWindow(1_000_000, 0.5, 4096);
            for (int i = 0; i < 3600; i++) cheapAndFrequent.Record(119);
            var expensiveAndRare = new FrameTimingWindow(1_000_000, 0.5, 4096);
            expensiveAndRare.Record(29_579);

            Assert.True(cheapAndFrequent.AverageMilliseconds() < expensiveAndRare.AverageMilliseconds());
            Assert.True(cheapAndFrequent.TotalMilliseconds() > expensiveAndRare.TotalMilliseconds());
            Assert.Equal(428.4, cheapAndFrequent.TotalMilliseconds(), 1);
        }
}
}
