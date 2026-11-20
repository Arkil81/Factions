using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-384 through traffic control: a station a fight has closed holds its queue, sends its
    /// arrivals elsewhere at once, and does not stop its neighbours' traffic.</summary>
    public class ClosedForCombatTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);
        private static readonly Dictionary<string, Vec3> Stations = new Dictionary<string, Vec3>
        {
            { "N600", new Vec3(0, 0, 0) },
            { "N601", new Vec3(4, 0, 0) },
        };
        private static readonly TrafficPresence.Player[] Player = { new TrafficPresence.Player { Party = "A", PosKm = new Vec3(0, 0, 0) } };

        private static TrafficPresence.Settings S(params string[] closed)
        {
            return new TrafficPresence.Settings
            {
                CeilingRadiusKm = 15, DivertSeconds = 300, DelaySeconds = 300,
                Closed = closed.Length > 0 ? new HashSet<string>(closed) : null,
            };
        }

        private static TrafficPresence.Fleet Arrival(string key, string station, double cost)
        {
            return new TrafficPresence.Fleet
            {
                Key = key, PosKm = new Vec3(14, 0, 0), CostMs = cost, Civilian = true,
                QueueStation = station, Divertable = true,
            };
        }

        private static TrafficPresence.Fleet Departure(string key, string station, double cost)
        {
            return new TrafficPresence.Fleet
            {
                Key = key, PosKm = Stations[station], CostMs = cost, Civilian = true,
                QueueStation = station, QueueDeparture = true,
            };
        }

        private static TrafficPresence.Plan Plan(StationQueues q, TrafficPresence.Settings s, double budget, DateTime now,
                                                 params TrafficPresence.Fleet[] fleets)
        {
            return TrafficPresence.Compute(Player, fleets, budget, s, Stations, q, now);
        }

        [Fact]
        public void AClosedStationClearsNothing_ThoughThereIsRoom_AndItsArrivalDivertsAtOnce()
        {
            var q = new StationQueues();
            Plan(q, S("N600"), 0, T0, Arrival("a", "N600", 1), Departure("d", "N600", 1));
            TrafficPresence.Plan p = Plan(q, S("N600"), 10, T0.AddSeconds(1), Arrival("a", "N600", 1), Departure("d", "N600", 1));

            Assert.Empty(p.Cleared);
            Assert.Contains("d", p.Queued);
            Assert.Equal(new[] { "a" }, p.DivertDue.Select(e => e.Key).ToArray());   // not after 300 s
        }

        [Fact]
        public void TheSameQueueOpen_ClearsAsItAlwaysHas()
        {
            var q = new StationQueues();
            Plan(q, S(), 0, T0, Arrival("a", "N600", 1), Departure("d", "N600", 1));
            TrafficPresence.Plan p = Plan(q, S(), 10, T0.AddSeconds(1), Arrival("a", "N600", 1), Departure("d", "N600", 1));
            Assert.Equal(2, p.Cleared.Count);
            Assert.Empty(p.DivertDue);
        }

        [Fact]
        public void AClosedStationsOlderHead_DoesNotHoldItsNeighbour()
        {
            var q = new StationQueues();
            Plan(q, S("N600"), 0, T0, Departure("old", "N600", 1));
            Plan(q, S("N600"), 0, T0.AddSeconds(5), Departure("old", "N600", 1), Departure("new", "N601", 1));
            TrafficPresence.Plan p = Plan(q, S("N600"), 10, T0.AddSeconds(6), Departure("old", "N600", 1), Departure("new", "N601", 1));

            Assert.Equal(new[] { "new" }, p.Cleared.Select(e => e.Key).ToArray());
            Assert.Contains("old", p.Queued);
        }
    }
}
