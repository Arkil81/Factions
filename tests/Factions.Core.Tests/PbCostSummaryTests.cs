using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PbCostSummaryTests
    {
        private static PbCostSummary.Block B(long id, long grid, double scheduledMs, double externalMs,
                                             long scheduledRuns = 1, long externalRuns = 0, double maxRunMs = 0)
        {
            return new PbCostSummary.Block
            {
                BlockId = id, GridId = grid,
                ScheduledMs = scheduledMs, ExternalMs = externalMs,
                ScheduledRuns = scheduledRuns, ExternalRuns = externalRuns,
                MaxRunMs = maxRunMs,
            };
        }

        [Fact]
        public void CostIsSpreadOverTheFramesInTheWindow()
        {
            var r = PbCostSummary.Summarise(new[] { B(1, 10, 900, 300, 1800, 3) }, 1800, 0.5, g => "Arkil", 10);

            Assert.Equal(1, r.Blocks);
            Assert.Equal(1200.0 / 1800, r.MsPerFrame, 9);
            Assert.Equal(900.0 / 1800, r.ScheduledMsPerFrame, 9);
            Assert.Equal(300.0 / 1800, r.ExternalMsPerFrame, 9);
            Assert.Equal(1803, r.Runs);
            Assert.Equal(3, r.ExternalRuns);
        }

        [Fact]
        public void OverCountsBlocksAboveTheThresholdPerBlockNotInTotal()
        {
            // Three blocks at 0.3 ms/frame each: 0.9 in total, none of them over 0.5.
            var blocks = new[] { B(1, 10, 300, 0), B(2, 10, 300, 0), B(3, 10, 300, 0), B(4, 11, 600, 0) };
            var r = PbCostSummary.Summarise(blocks, 1000, 0.5, g => g == 10 ? "Arkil" : SimSetTally.Factions, 10);

            Assert.Equal(1, r.Over);
            var arkil = r.Parties.Single(p => p.Party == "Arkil");
            Assert.Equal(3, arkil.Blocks);
            Assert.Equal(0, arkil.Over);
            Assert.Equal(0.9, arkil.MsPerFrame, 9);
            var factions = r.Parties.Single(p => p.Party == SimSetTally.Factions);
            Assert.Equal(1, factions.Over);
        }

        [Fact]
        public void PercentilesAreNearestRankOverBlocks()
        {
            var blocks = new List<PbCostSummary.Block>();
            for (int i = 1; i <= 20; i++) blocks.Add(B(i, 1, i, 0));    // 1..20 ms over 1 frame
            var r = PbCostSummary.Summarise(blocks, 1, 100, g => "p", 5);

            Assert.Equal(10.0, r.P50MsPerFrame, 9);
            Assert.Equal(19.0, r.P95MsPerFrame, 9);
            Assert.Equal(20.0, r.MaxMsPerFrame, 9);
            Assert.Equal(new long[] { 20, 19, 18, 17, 16 }, r.Top.Select(t => t.Block.BlockId).ToArray());
        }

        [Fact]
        public void ABlockThatDidNotRunIsNotCounted()
        {
            var r = PbCostSummary.Summarise(new[] { B(1, 1, 0, 0, 0, 0), B(2, 1, 5, 0) }, 10, 0.5, g => "p", 10);
            Assert.Equal(1, r.Blocks);
        }

        [Fact]
        public void AGridTheCensusDidNotSeeIsUnknownNotUnowned()
        {
            var r = PbCostSummary.Summarise(new[] { B(1, 99, 5, 0) }, 10, 0.5, g => null, 10);
            Assert.Equal(PbCostSummary.UnknownParty, r.Parties.Single().Party);
            Assert.Equal(PbCostSummary.UnknownParty, r.Top.Single().Party);
        }

        [Fact]
        public void MaxRunIsTheLongestSingleRunOfAnyBlock()
        {
            var r = PbCostSummary.Summarise(new[] { B(1, 1, 5, 0, maxRunMs: 2.5), B(2, 1, 5, 0, maxRunMs: 7.25) },
                                            10, 0.5, g => "p", 10);
            Assert.Equal(7.25, r.MaxRunMs, 9);
        }

        [Fact]
        public void AnEmptyWindowIsAllZeros()
        {
            var r = PbCostSummary.Summarise(new PbCostSummary.Block[0], 1800, 0.5, g => "p", 10);
            Assert.Equal(0, r.Blocks);
            Assert.Equal(0.0, r.MsPerFrame);
            Assert.Empty(r.Top);
            Assert.Empty(r.Parties);

            var noFrames = PbCostSummary.Summarise(new[] { B(1, 1, 5, 0) }, 0, 0.5, g => "p", 10);
            Assert.Equal(0, noFrames.Blocks);
        }
    }
}
