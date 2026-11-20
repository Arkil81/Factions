using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class TrafficPresenceTests
    {
        private static readonly TrafficPresence.Settings S = new TrafficPresence.Settings
        {
            CeilingRadiusKm = 15, OverShareWeight = 0.5, ShedHysteresis = 0.10, ShedMinDistanceKm = 5, MaxShedPerPlan = 1,
        };

        private static TrafficPresence.Player P(string party, double x, int step = 0)
        {
            return new TrafficPresence.Player { Party = party, PosKm = new Vec3(x, 0, 0), GovernorStep = step };
        }

        private static TrafficPresence.Fleet Candidate(string key, double x, double cost)
        {
            return new TrafficPresence.Fleet { Key = key, PosKm = new Vec3(x, 0, 0), CostMs = cost, Civilian = true };
        }

        private static TrafficPresence.Fleet Live(string key, double x, double cost, bool civilian = true, bool sheddable = true)
        {
            return new TrafficPresence.Fleet
            {
                Key = key, PosKm = new Vec3(x, 0, 0), CostMs = cost, Civilian = civilian,
                Materialised = true, Sheddable = sheddable,
            };
        }

        [Fact]
        public void NoPlayersAdmitsNothingAndShedsNothing()
        {
            TrafficPresence.Plan plan = TrafficPresence.Compute(new List<TrafficPresence.Player>(),
                new[] { Candidate("a", 0, 1) }, 10, S);
            Assert.Empty(plan.Admitted);
            Assert.Empty(plan.Shed);
        }

        [Fact]
        public void OnePlayerAloneGetsTheWholeBudget()
        {
            var fleets = new[] { Candidate("a", 1, 4), Candidate("b", 2, 4), Candidate("c", 3, 4) };
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, fleets, 10, S);
            Assert.Equal(10.0, plan.Neighbourhoods[0].ShareMs, 6);
            Assert.Equal(new[] { "a", "b" }, plan.Admitted.OrderBy(k => k).ToArray());
            Assert.Contains("c", plan.Refused);
        }

        /// <summary>The visible set is a clean radius: once the nearest misfit is refused, a farther
        /// fleet small enough to fit is refused too.</summary>
        [Fact]
        public void AdmissionStopsAtTheFirstMisfitNearestFirst()
        {
            var fleets = new[] { Candidate("near", 1, 3), Candidate("big", 4, 9), Candidate("far-small", 10, 0.5) };
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, fleets, 10, S);
            Assert.Equal(new[] { "near" }, plan.Admitted.ToArray());
            Assert.Contains("far-small", plan.Refused);
            Assert.Equal(4.0, plan.Neighbourhoods[0].EffectiveRadiusKm, 6);
        }

        [Fact]
        public void AFleetBeyondTheCeilingIsNotConsidered()
        {
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, new[] { Candidate("a", 20, 1) }, 10, S);
            Assert.Empty(plan.Admitted);
            Assert.Empty(plan.Refused);
        }

        [Fact]
        public void TwoApartPlayersSplitTheBudget()
        {
            var fleets = new List<TrafficPresence.Fleet>();
            for (int i = 0; i < 10; i++) fleets.Add(Candidate("a" + i, 1 + i * 0.1, 1));
            for (int i = 0; i < 10; i++) fleets.Add(Candidate("b" + i, 101 + i * 0.1, 1));
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0), P("B", 100) }, fleets, 10, S);
            Assert.Equal(2, plan.Neighbourhoods.Count);
            Assert.Equal(5, plan.Admitted.Count(k => k.StartsWith("a")));
            Assert.Equal(5, plan.Admitted.Count(k => k.StartsWith("b")));
        }

        /// <summary>Max-min fair: a quiet neighbourhood takes only what it uses and the busy one gets
        /// the rest, not half.</summary>
        [Fact]
        public void AQuietNeighbourhoodReleasesItsShare()
        {
            var fleets = new List<TrafficPresence.Fleet> { Candidate("b0", 101, 1) };
            for (int i = 0; i < 12; i++) fleets.Add(Candidate("a" + i, 1 + i * 0.1, 1));
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0), P("B", 100) }, fleets, 10, S);
            Assert.Contains("b0", plan.Admitted);
            Assert.Equal(9, plan.Admitted.Count(k => k.StartsWith("a")));
        }

        [Fact]
        public void CoLocatedPlayersPoolTheirShares()
        {
            var fleets = new List<TrafficPresence.Fleet>();
            for (int i = 0; i < 10; i++) fleets.Add(Candidate("a" + i, 1 + i * 0.1, 1));
            for (int i = 0; i < 10; i++) fleets.Add(Candidate("c" + i, 201 + i * 0.1, 1));
            // A and B at one hub, C alone elsewhere: the hub's weight is 2 of 3.
            TrafficPresence.Plan plan = TrafficPresence.Compute(
                new[] { P("A", 0), P("B", 2), P("C", 200) }, fleets, 9, S);
            Assert.Equal(2, plan.Neighbourhoods.Count);
            Assert.Equal(6, plan.Admitted.Count(k => k.StartsWith("a")));
            Assert.Equal(3, plan.Admitted.Count(k => k.StartsWith("c")));
        }

        /// <summary>T12: a player at governor step 1 weighs half, so their neighbourhood gets a third
        /// of a contested budget and the other player two thirds.</summary>
        [Fact]
        public void AnOverSharePlayerGetsLessTraffic()
        {
            var fleets = new List<TrafficPresence.Fleet>();
            for (int i = 0; i < 12; i++) fleets.Add(Candidate("a" + i, 1 + i * 0.1, 1));
            for (int i = 0; i < 12; i++) fleets.Add(Candidate("b" + i, 101 + i * 0.1, 1));
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0, step: 1), P("B", 100) }, fleets, 9, S);
            Assert.Equal(3, plan.Admitted.Count(k => k.StartsWith("a")));
            Assert.Equal(6, plan.Admitted.Count(k => k.StartsWith("b")));
        }

        [Fact]
        public void CombatIsCountedButNotGated()
        {
            var fleets = new[]
            {
                new TrafficPresence.Fleet { Key = "raid", PosKm = new Vec3(1, 0, 0), CostMs = 8, Civilian = false },
                Candidate("haul", 2, 3),
            };
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, fleets, 10, S);
            Assert.DoesNotContain("raid", plan.Refused);
            Assert.Contains("haul", plan.Refused);
        }

        [Fact]
        public void OverTheHysteresisTheFarthestSheddableFleetIsShed()
        {
            var fleets = new[] { Live("near", 2, 4), Live("mid", 7, 4), Live("far", 12, 4) };
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, fleets, 10, S);
            Assert.Equal(new[] { "far" }, plan.Shed.ToArray());
        }

        [Fact]
        public void InsideTheHysteresisNothingIsShed()
        {
            var fleets = new[] { Live("near", 2, 5), Live("far", 12, 5.9) };
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, fleets, 10, S);
            Assert.Empty(plan.Shed);
        }

        [Fact]
        public void AFleetCloseToThePlayerOrNotSheddableIsNeverShed()
        {
            var fleets = new[]
            {
                Live("close", 3, 10),
                Live("settling", 12, 10, sheddable: false),
                Live("combat", 13, 10, civilian: false),
            };
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, fleets, 5, S);
            Assert.Empty(plan.Shed);
        }

        [Fact]
        public void AnEmptyBudgetAdmitsNothing()
        {
            TrafficPresence.Plan plan = TrafficPresence.Compute(new[] { P("A", 0) }, new[] { Candidate("a", 1, 0.1) }, -3, S);
            Assert.Empty(plan.Admitted);
            Assert.Equal(0.0, plan.BudgetMs, 6);
        }
    }
}
