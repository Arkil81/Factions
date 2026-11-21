using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A hull withdrawn on a Nexus agent is held by the authority first: the last hull
    /// cannot be held twice, a release returns it, and a hold nobody settles goes back to stock.</summary>
    public class NexusHullHoldsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        private static SpatialGraph Graph(int stock, int listed = 0)
        {
            var graph = new SpatialGraph();
            var node = new SpatialNode { Id = "N1", OwnerFactionTag = "EXP" };
            if (stock > 0) node.ShipStock["Hauler"] = stock;
            if (listed > 0) node.ListedForSale["Hauler"] = listed;
            graph.Nodes["N1"] = node;
            return graph;
        }

        private static NexusHullAsk Ask(long id, string tag = "EXP")
        {
            return new NexusHullAsk { Id = id, Node = "N1", Prefab = "Hauler", Tag = tag, Identity = 42 };
        }

        private static int Stock(SpatialGraph g)
        {
            int n;
            g.Nodes["N1"].ShipStock.TryGetValue("Hauler", out n);
            return n;
        }

        [Fact]
        public void TheLastHullCannotBeHeldTwice()
        {
            SpatialGraph g = Graph(1);
            var holds = new NexusHullHolds();
            NexusHullAnswer first = holds.Take(Ask(1), 2, g, T0);
            NexusHullAnswer second = holds.Take(Ask(2), 4, g, T0);
            Assert.True(first.Ok);
            Assert.NotEqual(0, first.Hold);
            Assert.Equal(0, first.Remaining);
            Assert.False(second.Ok);
            Assert.Equal(2, second.Id);
            Assert.Equal(0, Stock(g));
        }

        [Fact]
        public void AListedHullIsNotHeld()
        {
            SpatialGraph g = Graph(1, listed: 1);
            Assert.False(new NexusHullHolds().Take(Ask(1), 2, g, T0).Ok);
            Assert.Equal(1, Stock(g));
        }

        [Fact]
        public void AnotherFactionCannotHoldTheHull()
        {
            SpatialGraph g = Graph(2);
            Assert.False(new NexusHullHolds().Take(Ask(1, "OTH"), 2, g, T0).Ok);
            Assert.Equal(2, Stock(g));
        }

        [Fact]
        public void AWithdrawalKeepsTheHullOutAndAReleaseReturnsIt()
        {
            SpatialGraph g = Graph(2);
            var holds = new NexusHullHolds();
            long a = holds.Take(Ask(1), 2, g, T0).Hold;
            long b = holds.Take(Ask(2), 2, g, T0).Hold;
            Assert.NotEqual(a, b);
            Assert.Equal(0, Stock(g));
            Assert.Equal(NexusHullSettled.Withdrawn, holds.Settle(a, true, g, "N1", "Hauler"));
            Assert.Equal(NexusHullSettled.Released, holds.Settle(b, false, g, "N1", "Hauler"));
            Assert.Equal(1, Stock(g));
            Assert.Equal(0, holds.Count);
        }

        [Fact]
        public void AnUnsettledHoldLapsesBackToStock()
        {
            SpatialGraph g = Graph(1);
            var holds = new NexusHullHolds();
            long id = holds.Take(Ask(1), 2, g, T0).Hold;
            Assert.Null(holds.Lapse(g, T0 + NexusHullHolds.Life));
            List<NexusHullHolds.Hold> lapsed = holds.Lapse(g, T0 + NexusHullHolds.Life + TimeSpan.FromSeconds(1));
            Assert.Equal(id, Assert.Single(lapsed).Id);
            Assert.Equal(1, Stock(g));
            // Settled late, the hold is known to have lapsed and returns nothing twice.
            Assert.Equal(NexusHullSettled.Lapsed, holds.Settle(id, false, g, "N1", "Hauler"));
            Assert.Equal(1, Stock(g));
        }

        [Fact]
        public void AReleaseOfAnUnknownHoldReturnsTheHull()
        {
            SpatialGraph g = Graph(0);
            var holds = new NexusHullHolds();
            Assert.Equal(NexusHullSettled.Unknown, holds.Settle(77, false, g, "N1", "Hauler"));
            Assert.Equal(1, Stock(g));
            Assert.Equal(NexusHullSettled.Unknown, holds.Settle(78, true, g, "N1", "Hauler"));
            Assert.Equal(1, Stock(g));
        }

        [Fact]
        public void AskAndAnswerSurviveTheWire()
        {
            NexusHullAsk ask;
            Assert.True(NexusHullAsk.TryDecode(Ask(9).Encode(), out ask));
            Assert.Equal(9, ask.Id);
            Assert.Equal("EXP", ask.Tag);
            var answer = new NexusHullAnswer { Id = 9, Ok = true, Hold = 123, Node = "N1", Prefab = "Hauler", Remaining = 3 };
            NexusHullAnswer back;
            Assert.True(NexusHullAnswer.TryDecode(answer.Encode(), out back));
            Assert.True(back.Ok);
            Assert.Equal(123, back.Hold);
            Assert.Equal(3, back.Remaining);
        }
    }
}
