using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Conduct on an agent is summed between reports so a fight's per-hit calls reach the
    /// authority as one row per member, faction, reason and propagation.</summary>
    public class NexusConductTallyTests
    {
        private const int Hostility = 1, Contract = 2;

        [Fact]
        public void HitsOnOneFactionSumToOneRow()
        {
            var t = new NexusConductTally();
            for (int i = 0; i < 40; i++) t.Add(7, 70UL, "IMPR", -5, Hostility, false);
            NexusStoreEvent e = Assert.Single(t.Drain());
            Assert.Equal(NexusStoreEventKind.Conduct, e.Kind);
            Assert.Equal(-200, e.Amount);
            Assert.Equal("IMPR", e.Key);
            Assert.Equal(Hostility, e.Blocks);
            Assert.False(e.Buy);
            Assert.Equal(7, e.Buyer);
            Assert.Equal(70UL, e.Steam);
            Assert.Equal(0, t.Count);
            Assert.Empty(t.Drain());
        }

        [Fact]
        public void MemberFactionReasonAndPropagationEachSplitARow()
        {
            var t = new NexusConductTally();
            t.Add(7, 70UL, "IMPR", -5, Hostility, false);
            t.Add(8, 80UL, "IMPR", -5, Hostility, false);   // another member
            t.Add(7, 70UL, "MRCH", -5, Hostility, false);   // another faction
            t.Add(7, 70UL, "IMPR", 50, Contract, true);     // another reason, propagating
            t.Add(7, 70UL, "impr", -5, Hostility, false);   // the same faction, any case
            List<NexusStoreEvent> rows = t.Drain();
            Assert.Equal(4, rows.Count);
            Assert.Equal(-10, rows[0].Amount);
            Assert.Equal(50, rows[3].Amount);
            Assert.True(rows[3].Buy);
        }

        [Fact]
        public void ANetZeroRowIsDroppedAndAnHugeSumIsClamped()
        {
            var t = new NexusConductTally();
            t.Add(7, 70UL, "IMPR", 30, Contract, false);
            t.Add(7, 70UL, "IMPR", -30, Contract, false);
            t.Add(7, 70UL, "MRCH", int.MaxValue, Contract, false);
            t.Add(7, 70UL, "MRCH", int.MaxValue, Contract, false);
            t.Add(7, 70UL, "MRCH", 0, Contract, false);
            NexusStoreEvent e = Assert.Single(t.Drain());
            Assert.Equal("MRCH", e.Key);
            Assert.Equal(int.MaxValue, e.Amount);
        }

        [Fact]
        public void AConductRowSurvivesTheReport()
        {
            var t = new NexusConductTally();
            t.Add(144115188075855900L, 76561198000000001UL, "IMPR", -12, Hostility, true);
            var report = new NexusAgentReport { Session = "s", StoreEvents = t.Drain() };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            NexusStoreEvent e = Assert.Single(back.StoreEvents);
            Assert.Equal(NexusStoreEventKind.Conduct, e.Kind);
            Assert.Equal(-12, e.Amount);
            Assert.Equal(Hostility, e.Blocks);
            Assert.True(e.Buy);
            Assert.Equal(144115188075855900L, e.Buyer);
        }
    }
}
