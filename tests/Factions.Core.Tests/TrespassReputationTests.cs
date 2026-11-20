using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 4 — an intruder who stays after the Intercept warning loses reputation for the
    /// time they linger: 100 per full 10 minutes of slow, undocked time in one stay. A trade run, a
    /// pass at speed, time docked and a short stay cost nothing.
    /// </summary>
    public sealed class TrespassReputationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Runs one stay on the 5 s pass for <paramref name="seconds"/>, each pass lingering
        /// by <paramref name="lingering"/>; returns the reputation charged.</summary>
        private static int Stay(TrespassDwellBook book, DateTime start, double seconds, Func<double, bool> lingering)
        {
            int charged = 0;
            for (double t = 0; t <= seconds; t += 5)
            {
                DateTime now = start.AddSeconds(t);
                book.Observe(1, "STCG", now);
                charged += book.Linger(1, "STCG", now, lingering(t)) * TrespassLadder.LingerStepReputation;
            }
            return charged;
        }

        [Fact]
        public void Lingering_IsSlowUndockedAndPastTheWarning()
        {
            Assert.True(TrespassLadder.IsLingering(TrespassRung.Intercept, 0, false));
            Assert.True(TrespassLadder.IsLingering(TrespassRung.Intercept, 19.9, false));
            Assert.False(TrespassLadder.IsLingering(TrespassRung.Intercept, 20, false));     // passing through
            Assert.False(TrespassLadder.IsLingering(TrespassRung.Intercept, 0, true));       // docked to trade
            Assert.False(TrespassLadder.IsLingering(TrespassRung.Shadow, 0, false));         // not yet warned
            Assert.False(TrespassLadder.IsLingering(TrespassRung.None, 0, false));
        }

        [Fact]
        public void TenMinutesOfLingering_CostsOneStep_AndAShortStayNothing()
        {
            Assert.Equal(0, Stay(new TrespassDwellBook(), T0, 595, t => true));
            Assert.Equal(100, Stay(new TrespassDwellBook(), T0, 605, t => true));
            Assert.Equal(300, Stay(new TrespassDwellBook(), T0, 1805, t => true));
        }

        [Fact]
        public void ATradeRun_CostsNothing()
        {
            // An hour in a hostile faction's space, warned at the first minute: cruising at speed
            // between stations, and docked at each one to trade. Never slow and undocked at once.
            Func<double, bool> run = t =>
            {
                bool docked = (t % 900) < 300;          // five minutes docked in every fifteen
                double speed = docked ? 0 : 60;         // cruising otherwise
                TrespassRung rung = t >= 60 ? TrespassRung.Intercept : TrespassRung.Shadow;
                return TrespassLadder.IsLingering(rung, speed, docked);
            };
            Assert.Equal(0, Stay(new TrespassDwellBook(), T0, 3600, run));
        }

        [Fact]
        public void ShortStays_DoNotAddUpAcrossVisits()
        {
            var book = new TrespassDwellBook();
            int charged = 0;
            DateTime start = T0;
            for (int visit = 0; visit < 6; visit++)
            {
                charged += Stay(book, start, 300, t => true);   // five lingering minutes
                start = start.AddSeconds(300) + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(30);
            }
            Assert.Equal(0, charged);
        }

        [Fact]
        public void AGapInThePasses_IsBridgedOnlyBriefly()
        {
            var book = new TrespassDwellBook();
            book.Observe(1, "STCG", T0);
            book.Linger(1, "STCG", T0, true);
            // A 50 s gap inside the mark's memory counts as 10 s, not 50.
            book.Observe(1, "STCG", T0.AddSeconds(50));
            book.Linger(1, "STCG", T0.AddSeconds(50), true);
            Assert.Equal(TrespassDwellBook.MaxLingerGapSeconds, book.LingerSeconds(1, "STCG"), 6);
        }

        [Fact]
        public void LingeringOnlyCountsTheSlowPasses()
        {
            // Twenty minutes, slow every other minute: ten lingering minutes, one step.
            Assert.Equal(100, Stay(new TrespassDwellBook(), T0, 1210, t => ((int)(t / 60)) % 2 == 0));
        }
    }
}
