using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A faction-terminal transfer on an agent is booked into the authority's ledger once.</summary>
    public class NexusWalletTransferTests
    {
        [Fact]
        public void APersonToPersonTransferIsNotQueued()
        {
            var outbox = new NexusTransferOutbox();
            Assert.False(outbox.Add(null, null, 100));
            Assert.False(outbox.Add("AAA", null, 0));
            Assert.Null(outbox.Pending());
        }

        [Fact]
        public void TransfersStayPendingUntilAcknowledged()
        {
            var outbox = new NexusTransferOutbox();
            outbox.Add(null, "AAA", 50000);
            outbox.Add("AAA", null, 2000);
            outbox.Add(null, "BBB", 7);
            outbox.Ack(2);
            NexusWalletTransfer left = Assert.Single(outbox.Pending());
            Assert.Equal(3, left.Seq);
            Assert.Equal("BBB", left.To);
        }

        /// <summary>The failure this exists for: an agent re-sends every unacknowledged transfer in
        /// each report, and a re-send must not move the ledger a second time.</summary>
        [Fact]
        public void AReSentTransferIsBookedOnce()
        {
            var outbox = new NexusTransferOutbox();
            var inbox = new NexusTransferInbox();
            outbox.Add(null, "AAA", 50000);
            long through;
            Assert.Single(inbox.Accept(2, "s1", outbox.Pending(), out through));
            Assert.Equal(1, through);
            Assert.Empty(inbox.Accept(2, "s1", outbox.Pending(), out through));
            Assert.Equal(1, through);

            outbox.Add("AAA", null, 10);
            List<NexusWalletTransfer> fresh = inbox.Accept(2, "s1", outbox.Pending(), out through);
            Assert.Equal(2, Assert.Single(fresh).Seq);
            Assert.Equal(2, through);
        }

        [Fact]
        public void ANewAgentSessionCountsAgainAndAnotherHomeIsSeparate()
        {
            var inbox = new NexusTransferInbox();
            var first = new List<NexusWalletTransfer> { new NexusWalletTransfer { Seq = 1, To = "AAA", Amount = 5 } };
            long through;
            inbox.Accept(2, "s1", first, out through);
            Assert.Single(inbox.Accept(2, "s2", first, out through));
            Assert.Single(inbox.Accept(4, "s2", first, out through));
        }

        [Fact]
        public void NothingSentAcknowledgesNothing()
        {
            long through;
            Assert.Empty(new NexusTransferInbox().Accept(2, "s1", null, out through));
            Assert.Equal(0, through);
        }

        [Fact]
        public void TheReportAndTheReplyCarryThem()
        {
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport
            {
                Session = "s",
                Transfers = new List<NexusWalletTransfer> { new NexusWalletTransfer { Seq = 4, From = "AAA", Amount = 123456789012 } },
            }.Encode(), out back));
            NexusWalletTransfer t = Assert.Single(back.Transfers);
            Assert.Equal(4, t.Seq);
            Assert.Equal("AAA", t.From);
            Assert.Null(t.To);
            Assert.Equal(123456789012, t.Amount);

            NexusStationReply reply;
            var sent = new NexusStationReply { TransfersThrough = 4 };
            Assert.False(sent.IsEmpty);
            Assert.True(NexusStationReply.TryDecode(sent.Encode(), out reply));
            Assert.Equal(4, reply.TransfersThrough);
        }
    }
}
