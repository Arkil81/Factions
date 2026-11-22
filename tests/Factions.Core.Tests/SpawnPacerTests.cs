using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class SpawnPacerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        private static bool Begin(SpawnPacer p, string key, double atSeconds)
        {
            double waited;
            return p.TryBegin(key, T0.AddSeconds(atSeconds), out waited);
        }

        /// <summary>2026-10-03 at N600: a Naginata@2 landing is a ~460 ms frame, and a fixed 1 s let three
        /// land in three seconds. The gap after a landing scales with what that frame cost.</summary>
        [Fact]
        public void AHeavyLandingHoldsTheNextSpawnLonger()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "heavy", 0));
            p.Landed("heavy", T0.AddSeconds(2));
            p.ReportLandingFrame("heavy", 460);
            Assert.Equal(9.2, p.GapSeconds, 3);
            Assert.False(Begin(p, "next", 5));
            Assert.False(Begin(p, "next", 11));
            Assert.True(Begin(p, "next", 11.3));
        }

        [Fact]
        public void IsWaiting_IsExact_AndEndsAtTheGrantOrAWithdraw()
        {
            var p = new SpawnPacer(10.0);
            Assert.True(Begin(p, "fleet:a", 0));
            Assert.False(Begin(p, "fleet:ab", 1));
            Assert.False(Begin(p, "fleet:c", 1));
            Assert.True(p.IsWaiting("fleet:ab"));
            Assert.False(p.IsWaiting("fleet:a"));    // in flight, not waiting; and no prefix match
            Assert.True(p.Withdraw("fleet:c"));
            Assert.False(p.IsWaiting("fleet:c"));
            p.Landed("fleet:a", T0.AddSeconds(2));
            Assert.True(Begin(p, "fleet:ab", 12.1));
            Assert.False(p.IsWaiting("fleet:ab"));
            Assert.False(p.IsWaiting(null));
        }

        [Fact]
        public void ALightLandingKeepsThePlainSpacing()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "light", 0));
            p.Landed("light", T0.AddSeconds(2));
            p.ReportLandingFrame("light", 30);
            Assert.Equal(1.0, p.GapSeconds, 3);
            Assert.True(Begin(p, "next", 3.1));
        }

        [Fact]
        public void TheGapIsCappedAndOnlyTheLastLandingCounts()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "station", 0));
            p.Landed("station", T0.AddSeconds(1));
            p.ReportLandingFrame("someone-else", 5000);
            Assert.Equal(1.0, p.GapSeconds, 3);
            p.ReportLandingFrame("station", 5000);
            Assert.Equal(SpawnPacer.MaxGapSeconds, p.GapSeconds, 3);
        }

        [Fact]
        public void TheFirstSpawnStartsAtOnce()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "a", 0));
            Assert.Equal(0, p.Queued);
        }

        [Fact]
        public void OnlyOneSpawnIsInFlight()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "a", 0));
            Assert.False(Begin(p, "b", 0.5));
            Assert.False(Begin(p, "b", 5));   // a has not landed
            Assert.Equal(1, p.Queued);
        }

        [Fact]
        public void TheNextStartsASpacingAfterTheLastLanded()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "a", 0));
            Assert.False(Begin(p, "b", 0.1));
            p.Landed("a", T0.AddSeconds(0.2));
            Assert.False(Begin(p, "b", 0.9));
            Assert.True(Begin(p, "b", 1.3));
        }

        [Fact]
        public void TheQueueIsFirstComeFirstServed()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "a", 0));
            Assert.False(Begin(p, "b", 0.1));
            Assert.False(Begin(p, "c", 0.2));
            p.Landed("a", T0.AddSeconds(0.3));
            Assert.False(Begin(p, "c", 1.5));   // b is ahead of it
            double waited;
            Assert.True(p.TryBegin("b", T0.AddSeconds(1.5), out waited));
            Assert.Equal(1.4, waited, 6);
            p.Landed("b", T0.AddSeconds(1.6));
            Assert.True(Begin(p, "c", 2.7));
        }

        [Fact]
        public void AKeyThatStopsAskingIsDroppedFromTheHead()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "a", 0));
            Assert.False(Begin(p, "gone", 0.1));
            Assert.False(Begin(p, "c", 0.2));
            p.Landed("a", T0.AddSeconds(0.3));
            // "gone" never asks again; after the stale window c moves up.
            Assert.False(Begin(p, "c", 5));
            Assert.True(Begin(p, "c", 11));
            Assert.Equal(1, p.DroppedFromQueue);
        }

        [Fact]
        public void ASpawnThatNeverLandsIsReleasedAfterTheStaleWindow()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "lost", 0));
            Assert.False(Begin(p, "b", 1));
            Assert.False(Begin(p, "b", 29));
            Assert.False(Begin(p, "b", 30.5));   // released at 30.5, spacing from then
            Assert.True(Begin(p, "b", 31.6));
            Assert.Equal(1, p.StaleInFlight);
        }

        [Fact]
        public void IsQueuedMatchesByPrefixSoASquadronMemberCounts()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "fleet:X#0", 0));
            Assert.False(Begin(p, "fleet:X#1", 0.1));
            Assert.True(p.IsQueued("fleet:X"));
            Assert.False(p.IsQueued("fleet:Y"));
        }

        [Fact]
        public void ZeroSpacingDisablesPacing()
        {
            var p = new SpawnPacer(0);
            Assert.True(Begin(p, "a", 0));
            Assert.True(Begin(p, "b", 0));
            Assert.Equal(0, p.QueueLength);
        }

        [Fact]
        public void LandingAnotherKeyDoesNotFreeTheSlot()
        {
            var p = new SpawnPacer(1.0);
            Assert.True(Begin(p, "a", 0));
            p.Landed("zzz", T0.AddSeconds(0.5));
            Assert.False(Begin(p, "b", 2));
        }
    }
}
