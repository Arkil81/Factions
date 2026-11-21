using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Node stock carried between a node, whose stock is the authority's, and a block on the
    /// agent a player is connected to. The authority's moves are re-sent until applied, and a
    /// withdrawal that lapses unapplied is handed back for its units to return to the node.</summary>
    public class NexusStockMoveTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Hold = TimeSpan.FromSeconds(30);
        private const byte Main = 2, Day = 4;

        private static NexusStockMove Withdraw(long amount)
        {
            return new NexusStockMove { Steam = 7UL, Node = "N1", Key = "Ingot:Iron", Amount = amount, Target = 99 };
        }

        [Fact]
        public void AMoveIsResentUntilTheAgentAppliesIt()
        {
            var box = new NexusStockMoveOutbox();
            NexusStockMove m = Withdraw(100);
            box.Add(Main, m, T0);
            var lapsed = new List<NexusStockMove>();

            List<NexusStockMove> first = box.Pending(Main, "s1", 0, T0.AddSeconds(1), Hold, lapsed);
            List<NexusStockMove> again = box.Pending(Main, "s1", 0, T0.AddSeconds(2), Hold, lapsed);
            Assert.Single(first);
            Assert.Single(again);
            Assert.Equal(m.Seq, again[0].Seq);

            Assert.Null(box.Pending(Main, "s1", m.Seq, T0.AddSeconds(3), Hold, lapsed));
            Assert.Empty(lapsed);
        }

        [Fact]
        public void MovesGoOnlyToTheirOwnAgent()
        {
            var box = new NexusStockMoveOutbox();
            box.Add(Main, Withdraw(1), T0);
            Assert.Null(box.Pending(Day, "d", 0, T0, Hold, null));
            Assert.Single(box.Pending(Main, "m", 0, T0, Hold, null));
        }

        [Fact]
        public void SequenceNumbersRiseWithinOneTick()
        {
            var box = new NexusStockMoveOutbox();
            NexusStockMove a = Withdraw(1), b = Withdraw(2);
            box.Add(Main, a, T0);
            box.Add(Main, b, T0);
            Assert.True(b.Seq > a.Seq);

            // Acknowledging the first leaves the second owed.
            List<NexusStockMove> rest = box.Pending(Main, "m", a.Seq, T0, Hold, null);
            Assert.Single(rest);
            Assert.Equal(b.Seq, rest[0].Seq);
        }

        [Fact]
        public void AMoveNeverAppliedLapsesAfterTheHold()
        {
            var box = new NexusStockMoveOutbox();
            NexusStockMove m = Withdraw(40);
            box.Add(Main, m, T0);
            var lapsed = new List<NexusStockMove>();
            Assert.Single(box.Pending(Main, "m", 0, T0.AddSeconds(29), Hold, lapsed));
            Assert.Empty(lapsed);

            Assert.Null(box.Pending(Main, "m", 0, T0.AddSeconds(31), Hold, lapsed));
            Assert.Single(lapsed);
            Assert.Same(m, lapsed[0]);
            Assert.Equal(0, box.Count(Main));
        }

        [Fact]
        public void AnAgentRestartLapsesWhatItsLastSessionWasOwed()
        {
            var box = new NexusStockMoveOutbox();
            box.Add(Main, Withdraw(5), T0);
            var lapsed = new List<NexusStockMove>();
            Assert.Single(box.Pending(Main, "old", 0, T0, Hold, lapsed));

            // The new session never saw the move; it must not be applied there either.
            Assert.Null(box.Pending(Main, "new", 0, T0.AddSeconds(1), Hold, lapsed));
            Assert.Single(lapsed);
        }

        [Fact]
        public void AMoveQueuedBeforeAnyReportGoesToTheFirstSession()
        {
            var box = new NexusStockMoveOutbox();
            box.Add(Main, Withdraw(5), T0);
            var lapsed = new List<NexusStockMove>();
            Assert.Single(box.Pending(Main, "first", 0, T0, Hold, lapsed));
            Assert.Empty(lapsed);
        }

        [Fact]
        public void AnAppliedMoveDoesNotLapse()
        {
            var box = new NexusStockMoveOutbox();
            NexusStockMove m = Withdraw(5);
            box.Add(Main, m, T0);
            var lapsed = new List<NexusStockMove>();
            Assert.Null(box.Pending(Main, "m", m.Seq, T0.AddMinutes(5), Hold, lapsed));
            Assert.Empty(lapsed);
        }

        [Fact]
        public void MovesAndTheirAcknowledgementSurviveTheWire()
        {
            var reply = new NexusStationReply
            {
                StockMoves = new List<NexusStockMove>
                {
                    new NexusStockMove
                    {
                        Seq = 12, Steam = 76561198000000001UL, Node = "N9", NodeName = "Kestrel", Owner = "EXP",
                        Key = "Ore:Iron", Amount = 250, Asked = 300, Deposit = true, Target = 123456789L,
                    },
                },
            };
            Assert.False(reply.IsEmpty);
            NexusStationReply back;
            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out back));
            NexusStockMove m = Assert.Single(back.StockMoves);
            Assert.Equal(12, m.Seq);
            Assert.Equal(76561198000000001UL, m.Steam);
            Assert.Equal("N9", m.Node);
            Assert.Equal("EXP", m.Owner);
            Assert.Equal("Ore:Iron", m.Key);
            Assert.Equal(250, m.Amount);
            Assert.Equal(300, m.Asked);
            Assert.True(m.Deposit);
            Assert.Equal(123456789L, m.Target);

            var report = new NexusAgentReport
            {
                Session = "s", StockMovesThrough = 12,
                StoreEvents = new List<NexusStoreEvent>
                {
                    new NexusStoreEvent { Kind = NexusStoreEventKind.Stock, Node = "N9", Key = "Ore:Iron", Amount = 250 },
                },
            };
            NexusAgentReport r;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out r));
            Assert.Equal(12, r.StockMovesThrough);
            Assert.Equal(NexusStoreEventKind.Stock, r.StoreEvents[0].Kind);
            Assert.Equal(250, r.StoreEvents[0].Amount);
        }

        [Fact]
        public void AReplyWithNothingButAMoveIsNotEmpty()
        {
            Assert.True(new NexusStationReply().IsEmpty);
            Assert.False(new NexusStationReply { StockMoves = new List<NexusStockMove> { Withdraw(1) } }.IsEmpty);
        }
    }
}
