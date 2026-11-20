using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The presence plan across Nexus servers: each server's players share that server's
    /// budget, priced at its multipliers.</summary>
    public class TrafficPresenceServerTests
    {
        private static readonly TrafficPresence.Settings S = new TrafficPresence.Settings
        {
            CeilingRadiusKm = 15, OverShareWeight = 0.5, ShedHysteresis = 0.10, ShedMinDistanceKm = 5, MaxShedPerPlan = 1,
        };

        private static TrafficPresence.Player P(string party, double x, byte server = 0)
        {
            return new TrafficPresence.Player { Party = party, PosKm = new Vec3(x, 0, 0), Server = server };
        }

        private static TrafficPresence.Fleet Candidate(string key, double x, double baseMs, double localK = 1.0)
        {
            return new TrafficPresence.Fleet
            {
                Key = key, PosKm = new Vec3(x, 0, 0), CostMs = baseMs * localK, BaseMs = baseMs, Civilian = true,
            };
        }

        private static TrafficPresence.Fleet Live(string key, double x, double baseMs)
        {
            return new TrafficPresence.Fleet
            {
                Key = key, PosKm = new Vec3(x, 0, 0), CostMs = baseMs, BaseMs = baseMs, Civilian = true,
                Materialised = true, Sheddable = true,
            };
        }

        private static Dictionary<byte, TrafficPresence.ServerLoad> Load(byte server, double budget, double k = 1.0)
        {
            return new Dictionary<byte, TrafficPresence.ServerLoad>
            {
                { server, new TrafficPresence.ServerLoad { BudgetMs = budget, KCivilian = k, KCombat = k } },
            };
        }

        private static TrafficPresence.Plan Plan(IList<TrafficPresence.Player> players, IList<TrafficPresence.Fleet> fleets,
                                                 double localBudget, IDictionary<byte, TrafficPresence.ServerLoad> servers)
        {
            return TrafficPresence.Compute(players, fleets, localBudget, S, null, null, DateTime.MinValue, servers);
        }

        /// <summary>The failure this exists for: a player on an agent, ships near them, and the
        /// authority's budget nowhere near either. The agent's own budget decides.</summary>
        [Fact]
        public void AnAgentsPlayerIsAdmittedAgainstTheAgentsBudget()
        {
            var fleets = new[] { Candidate("a", 1001, 4), Candidate("b", 1002, 4), Candidate("c", 1003, 4) };
            TrafficPresence.Plan plan = Plan(new[] { P("main", 1000, 2) }, fleets, 100, Load(2, 10));
            Assert.Equal(new[] { "a", "b" }, plan.Admitted.OrderBy(k => k).ToArray());
            Assert.Contains("c", plan.Refused);
            Assert.Equal(10.0, plan.Neighbourhoods[0].ShareMs, 6);
        }

        /// <summary>Without remote players the plan has nobody near the agent's ships, and refuses all
        /// of them; with them, the authority's own players keep the authority's budget.</summary>
        [Fact]
        public void EachServerSharesOnlyItsOwnBudget()
        {
            var players = new[] { P("dawn", 0), P("main", 1000, 2) };
            var fleets = new[]
            {
                Candidate("d1", 1, 4), Candidate("d2", 2, 4), Candidate("d3", 3, 4),
                Candidate("m1", 1001, 4), Candidate("m2", 1002, 4), Candidate("m3", 1003, 4),
            };
            TrafficPresence.Plan plan = Plan(players, fleets, 100, Load(2, 4));
            Assert.Equal(new[] { "d1", "d2", "d3", "m1" }, plan.Admitted.OrderBy(k => k).ToArray());
            Assert.Equal(new[] { "m2", "m3" }, plan.Refused.OrderBy(k => k).ToArray());
        }

        /// <summary>Two players a kilometre apart but on different servers are in different frames:
        /// they do not pool, and each server's budget is its own.</summary>
        [Fact]
        public void PlayersOnDifferentServersNeverShareANeighbourhood()
        {
            TrafficPresence.Plan plan = Plan(new[] { P("dawn", 0), P("main", 1, 2) }, new TrafficPresence.Fleet[0], 10, Load(2, 6));
            Assert.Equal(2, plan.Neighbourhoods.Count);
            Assert.Contains(plan.Neighbourhoods, n => n.Server == 0);
            Assert.Contains(plan.Neighbourhoods, n => n.Server == 2);
        }

        [Fact]
        public void AFleetNearAnAgentsPlayerIsPricedAtTheAgentsMultiplier()
        {
            // Base 4 each; the authority prices at 1, the agent measured 2.5: only one fits in 10.
            var fleets = new[] { Candidate("a", 1001, 4), Candidate("b", 1002, 4) };
            TrafficPresence.Plan plan = Plan(new[] { P("main", 1000, 2) }, fleets, 100, Load(2, 10, 2.5));
            Assert.Equal(new[] { "a" }, plan.Admitted.ToArray());
            Assert.Contains("b", plan.Refused);
        }

        /// <summary>An agent that sent no load is planned as today: nothing near its players is held.</summary>
        [Fact]
        public void AServerThatReportedNoLoadIsNotLimited()
        {
            var fleets = Enumerable.Range(0, 30).Select(i => Candidate("m" + i, 1000 + i * 0.3, 5)).ToArray();
            TrafficPresence.Plan plan = Plan(new[] { P("main", 1000, 2) }, fleets, 1, null);
            Assert.Equal(30, plan.Admitted.Count);
            Assert.Empty(plan.Refused);
        }

        [Fact]
        public void OverTheAgentsBudgetItsFarthestSheddableShipIsShed()
        {
            var fleets = new[] { Live("near", 1001, 4), Live("mid", 1006, 4), Live("far", 1010, 4) };
            TrafficPresence.Plan plan = Plan(new[] { P("main", 1000, 2) }, fleets, 100, Load(2, 6));
            Assert.Equal(new[] { "far" }, plan.Shed.ToArray());
        }

        [Fact]
        public void TheReportCarriesTheLoadAndAnOldReportReadsNone()
        {
            var report = new NexusAgentReport
            {
                Session = "s",
                Load = new NexusAgentLoad { BudgetMs = 3.25, KCivilian = 1.4, KCombat = 2.0, FactionsStep = 2 },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.NotNull(back.Load);
            Assert.Equal(3.25, back.Load.BudgetMs, 6);
            Assert.Equal(1.4, back.Load.KCivilian, 6);
            Assert.Equal(2.0, back.Load.KCombat, 6);
            Assert.Equal(2, back.Load.FactionsStep);

            NexusAgentReport old;
            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport { Session = "s" }.Encode(), out old));
            Assert.Null(old.Load);
        }
    }
}
