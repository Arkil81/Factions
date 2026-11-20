using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class LoadGovernorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        private const double Frame = 1000.0 / 60.0;   // 16.667 ms

        private static LoadGovernor.Input In(double busyPct, double factionsMs, params object[] players)
        {
            var i = new LoadGovernor.Input { BusyMs = busyPct / 100.0 * Frame, FactionsMs = factionsMs };
            for (int k = 0; k + 1 < players.Length; k += 2) i.PlayerMs[(string)players[k]] = (double)players[k + 1];
            return i;
        }

        private static LoadGovernor G() { return new LoadGovernor(new LoadGovernor.Settings()); }

        [Fact]
        public void BelowTheAdjustLineItOnlyMonitors()
        {
            var g = G();
            var d = g.Evaluate(In(90, 3.0, "Arkil", 6.0), T0);
            Assert.Equal(LoadGovernor.Pressure.Hold, d.Pressure);
            Assert.False(d.Changed);
            Assert.Equal(0, g.FactionsStep);
        }

        [Fact]
        public void AtNinetyFivePercentBusyFactionsIsCutFirst()
        {
            var g = G();
            var d = g.Evaluate(In(96, 3.0, "Arkil", 9.0), T0);
            Assert.Equal(LoadGovernor.Pressure.Short, d.Pressure);
            Assert.Equal("busy", d.Cause);
            Assert.True(d.Changed);
            Assert.Equal(1, g.FactionsStep);
            Assert.Equal(0, g.PlayerStep("Arkil"));
            Assert.Equal(2.25, d.FactionsTargetMs, 6);   // 25% off 3.0
        }

        [Fact]
        public void SimBelowTheFloorTriggersEvenWithFrameHeadroom()
        {
            var g = G();
            var i = In(70, 3.0);
            i.SimRatio = 0.75;
            var d = g.Evaluate(i, T0);
            Assert.Equal(LoadGovernor.Pressure.Short, d.Pressure);
            Assert.Equal("sim", d.Cause);
        }

        /// <summary>FAC-425: a fight took sim to 0.47 inside one interval. With the fast path, a window sim
        /// under 0.6 puts Factions at the combat step at once; without it the ladder still climbs one step.</summary>
        [Theory]
        [InlineData(true, 2)]
        [InlineData(false, 1)]
        public void ASimCollapse_GoesStraightToTheCombatStep_OnlyWithTheFastPath(bool fast, int step)
        {
            var g = G();
            var i = In(70, 3.0);
            i.SimRatio = 0.5;
            i.FastPath = fast;
            LoadGovernor.Decision d = g.Evaluate(i, T0);
            Assert.Equal(step, g.FactionsStep);
            Assert.True(d.Changed);

            // Already at the combat step: the fast path does nothing more, and the interval governs again.
            i.SimRatio = 0.5;
            g.Evaluate(i, T0.AddSeconds(10));
            Assert.Equal(step, g.FactionsStep);
        }

        [Fact]
        public void TheFastPathNeedsTheCollapse_NotMerelyTheFloor()
        {
            var g = G();
            var i = In(70, 3.0);
            i.SimRatio = 0.7;   // under the 0.8 floor, over the 0.6 fast floor
            i.FastPath = true;
            g.Evaluate(i, T0);
            Assert.Equal(1, g.FactionsStep);
        }

        [Fact]
        public void AtMostOneStepPerInterval()
        {
            var g = G();
            g.Evaluate(In(98, 3.0), T0);
            var d = g.Evaluate(In(98, 3.0), T0.AddSeconds(60));
            Assert.Equal("holding (interval)", d.Action);
            Assert.Equal(1, g.FactionsStep);
            g.Evaluate(In(98, 3.0), T0.AddSeconds(180));
            Assert.Equal(2, g.FactionsStep);
        }

        [Fact]
        public void PlayersAreSteppedOnlyAfterFactionsIsExhausted()
        {
            var g = G();
            var t = T0;
            // 15.6 + 3.0 = 18.6 ms busy. Even with Factions at its 0.5 ms floor the player pool is
            // 15.83 - 0.5 = 15.33, so the player is over share -- but Factions still goes first.
            for (int k = 0; k < 3; k++) { g.Evaluate(In(111.6, 3.0, "Arkil", 15.6), t); t = t.AddSeconds(180); }
            Assert.Equal(3, g.FactionsStep);
            Assert.Equal(0, g.PlayerStep("Arkil"));
            var d = g.Evaluate(In(111.6, 3.0, "Arkil", 15.6), t);
            Assert.Equal(1, g.PlayerStep("Arkil"));
            Assert.StartsWith("players step up", d.Action);
        }

        [Fact]
        public void APlayerWithinThePoolLeftAfterTheFactionsFloorIsNotStepped()
        {
            // Short by 0.67 ms, and cutting Factions from 3.0 toward 0.5 covers it: the player keeps
            // their share and the controller says it has nothing more to take once Factions is done.
            var g = G();
            var t = T0;
            LoadGovernor.Decision d = null;
            for (int k = 0; k < 4; k++) { d = g.Evaluate(In(99, 3.0, "Arkil", 12.0), t); t = t.AddSeconds(180); }
            Assert.Equal(3, g.FactionsStep);
            Assert.Equal(0, g.PlayerStep("Arkil"));
            Assert.Equal("at ceiling", d.Action);
        }

        [Fact]
        public void FactionsAtItsFloorIsSkippedStraightToPlayers()
        {
            var g = G();
            var d = g.Evaluate(In(99, 0.4, "Arkil", 14.0), T0);
            Assert.Equal(0, g.FactionsStep);
            Assert.Equal(1, g.PlayerStep("Arkil"));
        }

        [Fact]
        public void AnUnderSharePlayerIsNeverStepped()
        {
            var g = G();
            // heavy uses 12, light uses 0.5: light is under any fair share
            var d = g.Evaluate(In(99, 0.2, "heavy", 12.0, "light", 0.5), T0);
            Assert.Equal(1, g.PlayerStep("heavy"));
            Assert.Equal(0, g.PlayerStep("light"));
        }

        [Fact]
        public void TheStepsCapAtTheToolboxAndThenItSaysSo()
        {
            var g = G();
            var t = T0;
            LoadGovernor.Decision d = null;
            for (int k = 0; k < 6; k++) { d = g.Evaluate(In(99, 0.1, "Arkil", 15.0), t); t = t.AddSeconds(180); }
            Assert.Equal(LoadGovernor.PlayerMaxStep, g.PlayerStep("Arkil"));
            Assert.Equal("at ceiling", d.Action);
        }

        [Fact]
        public void BetweenReleaseAndAdjustItHolds()
        {
            var g = G();
            g.Evaluate(In(97, 3.0), T0);
            var d = g.Evaluate(In(90, 3.0), T0.AddSeconds(600));
            Assert.Equal(LoadGovernor.Pressure.Hold, d.Pressure);
            Assert.Equal(1, g.FactionsStep);
        }

        [Fact]
        public void ReleaseRunsPlayersFirstThenFactions()
        {
            var g = G();
            var t = T0;
            g.Evaluate(In(99, 0.4, "Arkil", 14.0), t);          // player step 1 (factions at floor)
            t = t.AddSeconds(180);
            g.Evaluate(In(99, 3.0, "Arkil", 11.0), t);           // factions now has cost: step 1
            Assert.Equal(1, g.FactionsStep);
            Assert.Equal(1, g.PlayerStep("Arkil"));
            t = t.AddSeconds(180);
            var d = g.Evaluate(In(60, 3.0, "Arkil", 4.0), t);
            Assert.StartsWith("players step down", d.Action);
            Assert.Equal(0, g.PlayerStep("Arkil"));
            Assert.Equal(1, g.FactionsStep);
            t = t.AddSeconds(180);
            g.Evaluate(In(60, 3.0, "Arkil", 4.0), t);
            Assert.Equal(0, g.FactionsStep);
        }

        [Fact]
        public void APlayerWhoLeavesTakesTheirStepWithThem()
        {
            var g = G();
            g.Evaluate(In(99, 0.1, "Arkil", 15.0), T0);
            Assert.Equal(1, g.PlayerStep("Arkil"));
            g.Evaluate(In(99, 0.1), T0.AddSeconds(10));
            Assert.Equal(0, g.PlayerStep("Arkil"));
        }

        [Fact]
        public void OnePlayerAloneIsEntitledToTheWholePool()
        {
            var s = LoadGovernor.FairShares(new Dictionary<string, double> { { "a", 3.0 } }, 10.0);
            Assert.Equal(10.0, s["a"], 9);
        }

        [Fact]
        public void HeavyPlayersAreCappedAtACommonLevelAndLightOnesAreEntitledToIt()
        {
            var s = LoadGovernor.FairShares(new Dictionary<string, double> { { "light", 1.0 }, { "mid", 5.0 }, { "heavy", 9.0 } }, 9.0);
            // light takes 1, leaving 8 for two players: level 4, both capped
            Assert.Equal(4.0, s["mid"], 9);
            Assert.Equal(4.0, s["heavy"], 9);
            Assert.Equal(4.0, s["light"], 9);
        }

        [Fact]
        public void WithEnoughPoolEachGetsTheirUseAndAnEqualCutOfTheSlack()
        {
            var s = LoadGovernor.FairShares(new Dictionary<string, double> { { "a", 1.0 }, { "b", 2.0 } }, 5.0);
            Assert.Equal(2.0, s["a"], 9);
            Assert.Equal(3.0, s["b"], 9);
        }

        [Fact]
        public void OtherLoadAndTheFactionsFloorComeOffThePool()
        {
            var g = G();
            // busy 99% = 16.5 ms; players 10, factions 3 -> other 3.5; pool = 15.833 - 3.5 - 0.5
            var d = g.Evaluate(In(99, 3.0, "Arkil", 10.0), T0);
            Assert.Equal(3.5, d.OtherMs, 3);
            Assert.Equal(0.95 * Frame - 3.5 - 0.5, d.PlayerPoolMs, 3);
        }

        private static LoadGovernor.Input Tail(LoadGovernor.Input i, params object[] causes)
        {
            for (int k = 0; k + 1 < causes.Length; k += 2)
            {
                i.TailFrames++;
                i.TailStallMs += (double)causes[k + 1];
                double v;
                i.TailStallByCause.TryGetValue((string)causes[k], out v);
                i.TailStallByCause[(string)causes[k]] = v + (double)causes[k + 1];
            }
            return i;
        }

        [Fact]
        public void OneLongFrameIsShortOnTheTailWhileTheAverageIsClear()
        {
            // The FAC-445 shape: a 343 ms economy-tick frame in an otherwise idle window.
            var d = G().Evaluate(Tail(In(55, 1.0), "MySessionComponentEconomy", 326.0), T0);
            Assert.Equal(LoadGovernor.Pressure.Clear, d.Pressure);
            Assert.Equal(LoadGovernor.Pressure.Short, d.TailPressure);
            Assert.Equal("MySessionComponentEconomy", d.TailTopCause);
            Assert.Equal(1, d.TailFrames);
        }

        [Fact]
        public void TheTailIsReportedAndDoesNotStepAnything()
        {
            var g = G();
            var d = g.Evaluate(Tail(In(55, 3.0, "Arkil", 9.0), "Factions", 900.0), T0);
            Assert.Equal(LoadGovernor.Pressure.Short, d.TailPressure);
            Assert.False(d.Changed);
            Assert.Equal(0, g.FactionsStep);
            Assert.Equal(0, g.PlayerStep("Arkil"));
        }

        [Fact]
        public void TheTailHoldsBetweenReleaseAndShort()
        {
            Assert.Equal(LoadGovernor.Pressure.Clear, G().Evaluate(Tail(In(55, 1.0), "MySector", 99.0), T0).TailPressure);
            Assert.Equal(LoadGovernor.Pressure.Hold, G().Evaluate(Tail(In(55, 1.0), "MySector", 100.0), T0).TailPressure);
            Assert.Equal(LoadGovernor.Pressure.Short, G().Evaluate(Tail(In(55, 1.0), "MySector", 150.0, "MyPhysics", 100.0), T0).TailPressure);
        }

        [Fact]
        public void TheTailNamesItsLargestCauseAndFactionsShare()
        {
            var d = G().Evaluate(Tail(In(55, 1.0), "MySector", 120.0, "Factions", 90.0, "MySector", 200.0), T0);
            Assert.Equal("MySector", d.TailTopCause);
            Assert.Equal(320.0, d.TailTopCauseMs, 3);
            Assert.Equal(90.0, d.TailFactionsMs, 3);
            Assert.Equal(410.0, d.TailStallMs, 3);
            Assert.Equal(3, d.TailFrames);
        }

        [Fact]
        public void AnEmptyTailIsClear()
        {
            var d = G().Evaluate(In(55, 1.0), T0);
            Assert.Equal(LoadGovernor.Pressure.Clear, d.TailPressure);
            Assert.Equal("", d.TailTopCause);
        }
    }
}
