using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class StationQueueTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc);
        private static readonly TrafficPresence.Settings S = new TrafficPresence.Settings
        {
            CeilingRadiusKm = 15, DivertSeconds = 300, DelaySeconds = 300,
        };
        private static readonly Dictionary<string, Vec3> Stations = new Dictionary<string, Vec3>
        {
            { "N600", new Vec3(0, 0, 0) },
        };
        private static readonly TrafficPresence.Player[] Player = { new TrafficPresence.Player { Party = "A", PosKm = new Vec3(0, 0, 0) } };

        private static TrafficPresence.Fleet Arrival(string key, double x, double cost, bool divertable = true)
        {
            return new TrafficPresence.Fleet
            {
                Key = key, PosKm = new Vec3(x, 0, 0), CostMs = cost, Civilian = true,
                QueueStation = "N600", Divertable = divertable,
            };
        }

        private static TrafficPresence.Fleet Departure(string key, double cost)
        {
            return new TrafficPresence.Fleet
            {
                Key = key, PosKm = new Vec3(0.1, 0, 0), CostMs = cost, Civilian = true,
                QueueStation = "N600", QueueDeparture = true,
            };
        }

        private static TrafficPresence.Plan Plan(StationQueues q, double budget, DateTime now, params TrafficPresence.Fleet[] fleets)
        {
            return TrafficPresence.Compute(Player, fleets, budget, S, Stations, q, now);
        }

        [Fact]
        public void StationTrafficIsClearedInArrivalOrderWhileItFits()
        {
            var q = new StationQueues();
            // b is nearer, but a asked first: order is the queue's, not distance.
            Plan(q, 0, T0, Arrival("a", 14, 2));
            TrafficPresence.Plan p = Plan(q, 3, T0.AddSeconds(1), Arrival("a", 14, 2), Arrival("b", 5, 2));
            Assert.Equal(new[] { "a" }, p.Cleared.Select(e => e.Key).ToArray());
            Assert.Contains("b", p.Queued);
            Assert.DoesNotContain("a", p.Queued);
            // Admitted in the same plan, so it cannot lose the room it was cleared for.
            Assert.Contains("a", p.Admitted);
        }

        [Fact]
        public void ArrivalsAndDeparturesShareOneQueue()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Departure("d", 1));
            Plan(q, 0, T0.AddSeconds(1), Departure("d", 1), Arrival("a", 14, 1));
            Assert.Equal(1, q.Position("d"));
            Assert.Equal(2, q.Position("a"));
            TrafficPresence.Plan p = Plan(q, 1, T0.AddSeconds(2), Departure("d", 1), Arrival("a", 14, 1));
            Assert.Equal(new[] { "d" }, p.Cleared.Select(e => e.Key).ToArray());
        }

        /// <summary>Strict FIFO: a big head that does not fit holds a small one behind it.</summary>
        [Fact]
        public void AHeadThatDoesNotFitBlocksTheQueue()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("big", 14, 5));
            TrafficPresence.Plan p = Plan(q, 2, T0.AddSeconds(1), Arrival("big", 14, 5), Arrival("small", 13, 0.5));
            Assert.Empty(p.Cleared);
            Assert.Contains("small", p.Queued);
        }

        [Fact]
        public void AClearedFleetIsAdmittedAheadOfOtherTraffic()
        {
            var q = new StationQueues();
            Plan(q, 2, T0, Arrival("a", 14, 2));                   // cleared on its first plan
            var passing = new TrafficPresence.Fleet { Key = "p", PosKm = new Vec3(1, 0, 0), CostMs = 2, Civilian = true };
            TrafficPresence.Plan p = Plan(q, 2, T0.AddSeconds(1), Arrival("a", 13, 2), passing);
            Assert.Contains("a", p.Admitted);
            Assert.Contains("p", p.Refused);
            Assert.Empty(p.Joined);
        }

        [Fact]
        public void AnArrivalDivertsAfterFiveMinutesAndADepartureHasItsDelayCalledOnce()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("a", 14, 1), Departure("d", 1));
            TrafficPresence.Plan early = Plan(q, 0, T0.AddSeconds(299), Arrival("a", 14, 1), Departure("d", 1));
            Assert.Empty(early.DivertDue);
            Assert.Empty(early.DelayDue);

            TrafficPresence.Plan due = Plan(q, 0, T0.AddSeconds(300), Arrival("a", 14, 1), Departure("d", 1));
            Assert.Equal(new[] { "a" }, due.DivertDue.Select(e => e.Key).ToArray());
            Assert.Equal(new[] { "d" }, due.DelayDue.Select(e => e.Key).ToArray());

            TrafficPresence.Plan later = Plan(q, 0, T0.AddSeconds(400), Arrival("a", 14, 1), Departure("d", 1));
            Assert.Empty(later.DelayDue);
            Assert.Contains("d", later.Queued);
        }

        [Fact]
        public void AnArrivalWithNowhereToGoWaitsAndHasItsDelayCalled()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("a", 14, 1));
            q.MarkUndivertable("a");
            TrafficPresence.Plan due = Plan(q, 0, T0.AddSeconds(300), Arrival("a", 14, 1));
            Assert.Empty(due.DivertDue);
            Assert.Equal(new[] { "a" }, due.DelayDue.Select(e => e.Key).ToArray());
        }

        [Fact]
        public void AFleetNoLongerStationTrafficLeavesTheQueue()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("a", 14, 1), Arrival("b", 13, 1));
            Plan(q, 0, T0.AddSeconds(1), Arrival("b", 13, 1));
            Assert.Equal(0, q.Position("a"));
            Assert.Equal(1, q.Position("b"));
        }

        [Fact]
        public void QueuedTrafficIsNotCountedAsRefused()
        {
            var q = new StationQueues();
            TrafficPresence.Plan p = Plan(q, 0, T0, Arrival("a", 14, 1));
            Assert.Empty(p.Refused);
            Assert.Equal(1, p.Neighbourhoods[0].QueuedArrivals);
            Assert.Equal(1.0, p.Neighbourhoods[0].DemandMs, 6);
        }

        [Fact]
        public void AQueuedArrivalWithRoomHoldsVisiblyAndStaysQueued()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("big", 14, 5));                          // head, blocks
            TrafficPresence.Plan p = Plan(q, 2, T0.AddSeconds(1), Arrival("big", 14, 5), Arrival("small", 4, 0.5));
            Assert.Contains("small", p.Admitted);
            Assert.Contains("small", p.Queued);
            Assert.Empty(p.Cleared);
            Assert.Equal(1, p.Neighbourhoods[0].HoldingVisible);
            Assert.Empty(p.Refused);
        }

        [Fact]
        public void ADepartureNeverHoldsVisibly()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("big", 14, 5));
            TrafficPresence.Plan p = Plan(q, 2, T0.AddSeconds(1), Arrival("big", 14, 5), Departure("d", 0.5));
            Assert.DoesNotContain("d", p.Admitted);
            Assert.Contains("d", p.Queued);
        }

        /// <summary>A ship already holding at its slot is paid for: clearing it costs nothing more,
        /// so it goes when it reaches the head even with no room left.</summary>
        [Fact]
        public void AVisiblyHoldingShipIsClearedAtNoFurtherCost()
        {
            var q = new StationQueues();
            var holding = Arrival("h", 4, 2);
            holding.Materialised = true;
            Plan(q, 0, T0, holding);
            Assert.Equal(1, q.Position("h"));
            TrafficPresence.Plan p = Plan(q, 2, T0.AddSeconds(1), holding);
            Assert.Equal(new[] { "h" }, p.Cleared.Select(e => e.Key).ToArray());
            Assert.Equal(2.0, p.Neighbourhoods[0].UsedMs, 6);
        }

        [Fact]
        public void AQueuedStationReportsItsNeighbourhoodShare()
        {
            var q = new StationQueues();
            TrafficPresence.Plan p = Plan(q, 7, T0, Arrival("a", 14, 20));
            Assert.Equal(7.0, p.StationShareMs["N600"], 6);
        }

        /// <summary>A holder parked past the radius keeps its place; it is neither dropped nor shown.</summary>
        [Fact]
        public void AHolderBeyondTheRadiusStaysQueuedAndUnseen()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("a", 14, 1), Arrival("b", 13, 1));
            // a has been moved out to a slot 18 km away; the budget would have room for it.
            TrafficPresence.Plan p = Plan(q, 0.5, T0.AddSeconds(1), Arrival("a", 18, 1), Arrival("b", 13, 1));
            Assert.Equal(1, q.Position("a"));
            Assert.Contains("a", p.Queued);
            Assert.DoesNotContain("a", p.Admitted);
        }

        /// <summary>An arrival joins at the hold radius, 20 km out, with no player near it: it queues
        /// there, is not admitted, and is no holding candidate a player at the station could see.</summary>
        [Fact]
        public void AnArrivalJoinsAtTheHoldRadiusUnseen()
        {
            var q = new StationQueues();
            TrafficPresence.Plan p = Plan(q, 0, T0, Arrival("a", 20, 1));
            Assert.Equal(1, q.Position("a"));
            Assert.Contains("a", p.Queued);
            Assert.DoesNotContain("a", p.Admitted);
            Assert.Equal(0, p.Neighbourhoods[0].HoldingVisible);
        }

        /// <summary>A cleared arrival that is out past the radius keeps its clearance: it does not
        /// rejoin at the back when it comes back into reach (the 10-01 loop).</summary>
        [Fact]
        public void AClearedArrivalBeyondTheRadiusKeepsItsClearance()
        {
            var q = new StationQueues();
            Plan(q, 10, T0, Arrival("a", 14, 1));
            Assert.True(q.HasClearance("a"));
            Plan(q, 10, T0.AddSeconds(1), Arrival("a", 18, 1));
            Assert.True(q.HasClearance("a"));
            TrafficPresence.Plan p = Plan(q, 10, T0.AddSeconds(2), Arrival("a", 14, 1));
            Assert.Empty(p.Joined);
            Assert.Contains("a", p.Admitted);
        }

        /// <summary>A blocked head shrinks the radius: a ship flying away past the shed distance goes,
        /// though the share is not over, so its room goes to the queue (2026-10-02 at N600: the share
        /// was full of departures flying the whole 16 km out while 30 ships queued).</summary>
        [Fact]
        public void ABlockedHeadShedsAShipFlyingAway()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("head", 14, 2));
            var leaving = new TrafficPresence.Fleet
            {
                Key = "out", PosKm = new Vec3(9, 0, 0), CostMs = 1.5, Civilian = true, Materialised = true, Sheddable = true,
            };
            var docking = new TrafficPresence.Fleet
            {
                Key = "in", PosKm = new Vec3(12, 0, 0), CostMs = 1.5, Civilian = true, Materialised = true, Sheddable = true,
                Inbound = true,
            };
            // Share 3 is exactly used: not over, but the head (2) does not fit.
            TrafficPresence.Plan p = Plan(q, 3, T0.AddSeconds(1), Arrival("head", 14, 2), leaving, docking);
            Assert.Empty(p.Cleared);
            Assert.Equal(new[] { "out" }, p.Shed.ToArray());
            Assert.Equal(5.0, p.Neighbourhoods[0].EffectiveRadiusKm);
        }

        /// <summary>Over the share, a ship flying away goes before a farther ship coming in to dock.
        /// 2026-10-01 at N600 a cleared arrival that had waited 199 s was shed 64 s after spawning when
        /// the share dropped.</summary>
        [Fact]
        public void OverTheShareAShipFlyingAwayGoesBeforeOneDocking()
        {
            var q = new StationQueues();
            var leaving = new TrafficPresence.Fleet
            {
                Key = "out", PosKm = new Vec3(7, 0, 0), CostMs = 2, Civilian = true, Materialised = true, Sheddable = true,
            };
            var docking = new TrafficPresence.Fleet
            {
                Key = "in", PosKm = new Vec3(12, 0, 0), CostMs = 2, Civilian = true, Materialised = true, Sheddable = true,
                Inbound = true,
            };
            TrafficPresence.Plan p = Plan(q, 3, T0, leaving, docking);
            Assert.Equal(new[] { "out" }, p.Shed.ToArray());
        }

        [Fact]
        public void WithNothingQueuedAFullShareShedsNothing()
        {
            var q = new StationQueues();
            var leaving = new TrafficPresence.Fleet
            {
                Key = "out", PosKm = new Vec3(9, 0, 0), CostMs = 1.5, Civilian = true, Materialised = true, Sheddable = true,
            };
            TrafficPresence.Plan p = Plan(q, 1.5, T0, leaving);
            Assert.Empty(p.Shed);
        }

        /// <summary>The 09-30 jam: the share was full of other ships and the head never fitted. What a
        /// blocked head needs is held back from everything after it.</summary>
        [Fact]
        public void NothingTakesTheRoomABlockedHeadIsWaitingFor()
        {
            var q = new StationQueues();
            Plan(q, 0, T0, Arrival("head", 14, 2));
            var passing = new TrafficPresence.Fleet { Key = "p", PosKm = new Vec3(1, 0, 0), CostMs = 1, Civilian = true };
            var parked = new TrafficPresence.Fleet
            {
                Key = "m", PosKm = new Vec3(2, 0, 0), CostMs = 1, Civilian = true, Materialised = true,
            };
            // Share 2: 1 is in use, so the head (2) does not fit, and the 1 left is its, not p's.
            TrafficPresence.Plan p = Plan(q, 2, T0.AddSeconds(1), Arrival("head", 14, 2), passing, parked);
            Assert.Empty(p.Cleared);
            Assert.DoesNotContain("p", p.Admitted);
        }

        [Fact]
        public void WithoutQueuesStationTrafficIsOrdinaryTraffic()
        {
            TrafficPresence.Plan p = TrafficPresence.Compute(Player, new[] { Arrival("a", 14, 1) }, 5, S);
            Assert.Contains("a", p.Admitted);
            Assert.Empty(p.Queued);
        }
    }
}
