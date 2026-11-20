using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>An agent's DecisionLog lines reach the authority's file once each, in slices.</summary>
    public class NexusLogTests
    {
        private static readonly DateTime T = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ABacklogDrainsInSlicesAndEachLineIsFiledOnce()
        {
            var outbox = new NexusLogOutbox();
            for (int i = 0; i < 1200; i++) outbox.Add(T.AddSeconds(i), "L" + i);
            var inbox = new NexusInbox<NexusLogLine>();
            var filed = new List<string>();
            for (int round = 0; round < 5; round++)
            {
                List<NexusLogLine> slice = outbox.Pending();
                if (slice == null) break;
                Assert.True(slice.Count <= NexusLogOutbox.SliceLines);
                long through;
                foreach (NexusLogLine l in inbox.Accept(2, "s", slice, out through)) filed.Add(l.Text);
                // A reply lost on the way back: the same slice comes again and files nothing.
                if (round == 0)
                {
                    long again;
                    Assert.Empty(inbox.Accept(2, "s", outbox.Pending(), out again));
                    Assert.Equal(through, again);
                }
                outbox.Ack(through);
            }
            Assert.Equal(1200, filed.Count);
            Assert.Equal("L0", filed[0]);
            Assert.Equal("L1199", filed[1199]);
            Assert.Equal(0, outbox.Count);
        }

        [Fact]
        public void PastCapacityLinesAreCountedAndTheCountIsFiledWhenThereIsRoom()
        {
            var outbox = new NexusLogOutbox();
            for (int i = 0; i < NexusLogOutbox.Capacity + 7; i++) outbox.Add(T, "L" + i);
            Assert.Equal(NexusLogOutbox.Capacity, outbox.Count);
            Assert.Equal(7, outbox.Dropped);

            outbox.Ack(outbox.Pending()[NexusLogOutbox.SliceLines - 1].Seq);
            outbox.Add(T, "after");
            List<NexusLogLine> all = outbox.Pending(int.MaxValue);
            Assert.Equal("DECISION_LOG_DROPPED lines=7", all[all.Count - 2].Text);
            Assert.Equal("after", all[all.Count - 1].Text);
            Assert.Equal(0, outbox.Dropped);
        }

        [Fact]
        public void TheReportCarriesTheLinesWithTheirTimeAndTheReplyTheAck()
        {
            var report = new NexusAgentReport
            {
                Session = "s",
                Log = new List<NexusLogLine> { new NexusLogLine { Seq = 3, At = T, Text = "GRID_REAPED grids=4" } },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.Equal(3, back.Log[0].Seq);
            Assert.Equal(T, back.Log[0].At);
            Assert.Equal("GRID_REAPED grids=4", back.Log[0].Text);

            var reply = new NexusStationReply { LogThrough = 3 };
            Assert.False(reply.IsEmpty);
            NexusStationReply r;
            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out r));
            Assert.Equal(3, r.LogThrough);
        }
    }
}
