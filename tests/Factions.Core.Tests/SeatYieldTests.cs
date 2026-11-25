using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class SeatYieldTests
    {
        private static readonly Vec3 Head = new Vec3(0, 1.7, 0);
        private static readonly Vec3 Ahead = new Vec3(0, 0, -1);

        [Fact]
        public void LookingAtASeatWithinReachIsReaching()
        {
            Assert.True(SeatYield.Aims(Head, Ahead, new Vec3(0, 1.0, -2)));
        }

        [Fact]
        public void LookingAwayOrStandingTooFarIsNot()
        {
            Assert.False(SeatYield.Aims(Head, new Vec3(1, 0, 0), new Vec3(0, 1.0, -2)));
            Assert.False(SeatYield.Aims(Head, Ahead, new Vec3(0, 1.7, -3.5)));
            Assert.False(SeatYield.Aims(Head, new Vec3(0, 0, 0), new Vec3(0, 1.0, -2)));
        }

        [Fact]
        public void StandingOnTheEntryPointIsReachingWhateverTheLook()
        {
            Assert.True(SeatYield.Aims(Head, new Vec3(0, 0, 1), new Vec3(0, 1.3, -0.2)));
        }

        [Fact]
        public void ADwellOfConsecutivePassesYields()
        {
            var watch = new SeatYieldWatch();
            Assert.False(watch.Observe(10, 1, true));
            watch.EndPass();
            Assert.True(watch.Observe(10, 1, true));
        }

        [Fact]
        public void AGlanceDoesNotYield()
        {
            var watch = new SeatYieldWatch();
            Assert.False(watch.Observe(10, 1, true));
            watch.EndPass();
            Assert.False(watch.Observe(10, 1, false));
            watch.EndPass();
            Assert.False(watch.Observe(10, 1, true));
        }

        [Fact]
        public void TheDwellIsPerPlayerAndPerSeat()
        {
            var watch = new SeatYieldWatch();
            Assert.False(watch.Observe(10, 1, true));
            watch.EndPass();
            Assert.False(watch.Observe(10, 2, true));
            Assert.False(watch.Observe(11, 1, true));
        }

        [Fact]
        public void AHeldSeatRefusesOnlyABot()
        {
            Assert.True(SeatYield.RefusesBot(true, false, false));
            Assert.False(SeatYield.RefusesBot(true, true, false));
            Assert.False(SeatYield.RefusesBot(false, false, false));
            Assert.False(SeatYield.RefusesBot(true, false, true));
        }

        [Fact]
        public void AYieldedSeatIsHeldForThirtySeconds()
        {
            var watch = new SeatYieldWatch();
            var t0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
            watch.Hold(10, t0);
            Assert.True(watch.IsHeld(10, t0.AddSeconds(29)));
            Assert.False(watch.IsHeld(10, t0.AddSeconds(30)));
            Assert.False(watch.IsHeld(11, t0));
            Assert.Equal(1, watch.HeldCount(t0.AddSeconds(1)));
            Assert.Equal(0, watch.HeldCount(t0.AddSeconds(31)));
        }
    }
}
