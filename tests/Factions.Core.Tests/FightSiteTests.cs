using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-425 slice 2: a materialised fight as controlled airspace.</summary>
    public class FightSiteTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        private static FightHull Hull(string side, string fleet, int member, double costMs = 1.0, double dps = 100)
        {
            return new FightHull { Side = side, FleetId = fleet, Member = member, Prefab = "P", CostMs = costMs, Dps = dps };
        }

        private static FightSite Site(int raiders, int guards, double costMs = 1.0, double raiderDps = 100, double guardDps = 100)
        {
            var site = new FightSite("S", new Vec3(0, 0, 0), T0);
            for (int i = 0; i < raiders; i++) site.Enqueue(Hull("RVN", "RAID", i, costMs, raiderDps), T0);
            for (int i = 0; i < guards; i++)  site.Enqueue(Hull("DEF", "GUARD", i, costMs, guardDps), T0);
            return site;
        }

        /// <summary>FAC-425's fight: 24 raiders and 5 guards. The field holds what the budget holds, split
        /// by strength — not 29 hulls at once.</summary>
        [Fact]
        public void TheLiveFight_PutsABudgetsWorthOnTheField_SplitByStrength()
        {
            FightSite site = Site(24, 5);
            List<FightHull> cleared = site.Admit(6.0, T0);

            Assert.Equal(6, cleared.Count);
            Assert.Equal(6.0, site.UsedMs, 6);
            // 24:5 by strength gives the guard 5/29 of 6 ms — about one hull — and the raid the rest.
            Assert.Equal(5, site.OnField("RVN"));
            Assert.Equal(1, site.OnField("DEF"));
            Assert.Equal(19, site.Queued("RVN"));
            Assert.Equal(4, site.Queued("DEF"));
        }

        [Fact]
        public void EverySideWithAQueue_GetsOneHullOnTheField_EvenOverBudget()
        {
            FightSite site = Site(3, 3, costMs: 5.0);
            List<FightHull> cleared = site.Admit(2.0, T0);

            Assert.Equal(2, cleared.Count);
            Assert.Equal(1, site.OnField("RVN"));
            Assert.Equal(1, site.OnField("DEF"));
        }

        [Fact]
        public void AHeadThatDoesNotFit_StopsItsSide_NotTheOther()
        {
            var site = new FightSite("S", new Vec3(0, 0, 0), T0);
            site.Enqueue(Hull("RVN", "R", 0, 1.0), T0);
            site.Enqueue(Hull("RVN", "R", 1, 10.0), T0);   // the heavy head
            site.Enqueue(Hull("RVN", "R", 2, 1.0), T0);    // behind it, would fit
            site.Enqueue(Hull("DEF", "G", 0, 1.0), T0);
            site.Enqueue(Hull("DEF", "G", 1, 1.0), T0);

            site.Admit(4.0, T0);

            Assert.Equal(1, site.OnField("RVN"));   // FIFO: the 1 ms hull behind the 10 ms head waits
            Assert.Equal(2, site.OnField("DEF"));
        }

        [Fact]
        public void ALostHull_MakesRoomForTheNextInItsQueue()
        {
            FightSite site = Site(4, 4);
            site.Admit(4.0, T0);
            FightHull gone = site.FieldOf("RVN")[0];

            Assert.True(site.Exit(gone.FleetId, gone.Member, FightExitCause.Destroyed, T0.AddSeconds(30)));
            List<FightHull> next = site.Admit(4.0, T0.AddSeconds(31));

            FightHull replacement = Assert.Single(next);
            Assert.Equal("RVN", replacement.Side);
            Assert.Equal(1, site.Lost("RVN"));
            Assert.False(site.Contains(gone.FleetId, gone.Member));
        }

        [Fact]
        public void ADamagedHullRetreats_AndLeavesTheFight()
        {
            FightSite site = Site(2, 2);
            site.Admit(2.0, T0);
            FightHull hurt = site.FieldOf("DEF")[0];

            List<FightExit> exits = site.Review(T0.AddSeconds(5), h => h == hurt ? 0.2 : 0.9, 2.0);

            FightExit exit = Assert.Single(exits);
            Assert.Equal(FightExitCause.Retreated, exit.Cause);
            site.Exit(hurt.FleetId, hurt.Member, exit.Cause, T0.AddSeconds(6));
            Assert.Equal(1, site.RetreatedFrom("DEF"));
            Assert.Equal(0, site.Lost("DEF"));
            Assert.False(site.Contains(hurt.FleetId, hurt.Member));
        }

        [Fact]
        public void ALongServingHullRotates_OnlyWhenItsSideHasAQueue_OnePerReview()
        {
            FightSite site = Site(4, 1);
            site.Admit(3.0, T0);
            Assert.Equal(1, site.OnField("DEF"));

            DateTime late = T0.AddSeconds(FightSite.RotationSeconds);
            List<FightExit> exits = site.Review(late, h => 1.0, 3.0);

            // The guard has nobody behind it, so it stays; the raid rotates one of its hulls, not all.
            FightExit rotate = Assert.Single(exits);
            Assert.Equal("RVN", rotate.Hull.Side);
            Assert.Equal(FightExitCause.Rotated, rotate.Cause);

            site.Exit(rotate.Hull.FleetId, rotate.Hull.Member, FightExitCause.Rotated, late);
            Assert.True(site.Contains(rotate.Hull.FleetId, rotate.Hull.Member));   // back in the queue
            FightHull next = Assert.Single(site.Admit(3.0, late));
            Assert.NotEqual(rotate.Hull.Key, next.Key);                          // to the back, not the front
        }

        [Fact]
        public void AShrunkBudgetSheds_TheOldestOfTheHeaviestSide_NeverASidesLast()
        {
            FightSite site = Site(6, 2);
            site.Admit(8.0, T0);
            Assert.Equal(8.0, site.UsedMs, 6);

            List<FightExit> exits = site.Review(T0.AddSeconds(10), h => 1.0, 2.0);

            Assert.All(exits, e => Assert.Equal(FightExitCause.Shed, e.Cause));
            foreach (FightExit e in exits) site.Exit(e.Hull.FleetId, e.Hull.Member, e.Cause, T0.AddSeconds(11));
            Assert.Equal(1, site.OnField("RVN"));
            Assert.Equal(1, site.OnField("DEF"));
            Assert.Equal(2.0, site.UsedMs, 6);
        }

        [Fact]
        public void AShedHullIsTheNextOneBackIn()
        {
            FightSite site = Site(3, 1);
            site.Admit(4.0, T0);
            List<FightExit> shed = site.Review(T0.AddSeconds(10), h => 1.0, 2.0);
            FightExit first = shed[0];
            foreach (FightExit e in shed) site.Exit(e.Hull.FleetId, e.Hull.Member, e.Cause, T0.AddSeconds(11));

            List<FightHull> back = site.Admit(4.0, T0.AddSeconds(20));
            Assert.Contains(back, h => h.Key == first.Hull.Key);
        }

        [Fact]
        public void TheFightIsDecided_WhenOneSideHasNothingLeft()
        {
            FightSite site = Site(1, 2);
            site.Admit(10.0, T0);
            string winner;
            Assert.False(site.IsDecided(out winner));

            FightHull raider = site.FieldOf("RVN")[0];
            site.Exit(raider.FleetId, raider.Member, FightExitCause.Destroyed, T0.AddSeconds(40));

            Assert.True(site.IsDecided(out winner));
            Assert.Equal("DEF", winner);
        }

        [Fact]
        public void QueuedHullsCountAsStillInTheFight()
        {
            FightSite site = Site(2, 1);
            site.Admit(0.5, T0);   // one each on the field, one raider queued
            FightHull raider = site.FieldOf("RVN")[0];
            site.Exit(raider.FleetId, raider.Member, FightExitCause.Destroyed, T0.AddSeconds(40));

            string winner;
            Assert.False(site.IsDecided(out winner));
            Assert.Equal(1, site.Queued("RVN"));
        }

        [Fact]
        public void EnqueueIsIdempotent_AndWithdrawTakesTheWholeFleet()
        {
            FightSite site = Site(3, 1);
            site.Enqueue(Hull("RVN", "RAID", 0), T0);
            Assert.Equal(3, site.Queued("RVN"));

            site.Admit(2.0, T0);
            Assert.Equal(3, site.Withdraw("RAID"));
            Assert.Equal(0, site.OnField("RVN") + site.Queued("RVN"));
            string winner;
            Assert.True(site.IsDecided(out winner));
            Assert.Equal("DEF", winner);
        }

        [Fact]
        public void ASideWithNoStrengthOnRecord_StillGetsAnEvenShare()
        {
            FightSite site = Site(4, 4, raiderDps: 0, guardDps: 0);
            site.Admit(4.0, T0);
            Assert.Equal(2, site.OnField("RVN"));
            Assert.Equal(2, site.OnField("DEF"));
        }
    }
}
