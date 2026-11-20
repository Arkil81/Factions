using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class WaypointProgressTests
    {
        [Fact]
        public void CirclingWithoutGettingCloserBecomesStuck()
        {
            var p = new WaypointProgress();
            Assert.False(p.Observe(true, 1, 2, 3, 2, 2));
            Assert.False(p.Observe(true, 1, 2, 3, 3, 2));
            Assert.False(p.Observe(true, 1, 2, 3, 2.05, 2));
            Assert.False(p.Observe(true, 1, 2, 3, 3, 2));
            Assert.True(p.Observe(true, 1, 2, 3, 2, 2));
        }

        [Fact]
        public void RealProgressRestartsTheWindow()
        {
            var p = new WaypointProgress();
            p.Observe(true, 0, 0, 0, 5, 2);
            p.Observe(true, 0, 0, 0, 5, 6);
            Assert.False(p.Observe(true, 0, 0, 0, 4.7, 2));
            Assert.Equal(0, p.NoProgressSeconds);
        }

        [Fact]
        public void ReturningToAnOldBestDistanceIsNotProgress()
        {
            var p = new WaypointProgress();
            p.Observe(true, 0, 0, 0, 5, 2);
            p.Observe(true, 0, 0, 0, 4, 2);
            p.Observe(true, 0, 0, 0, 5, 4);
            Assert.True(p.Observe(true, 0, 0, 0, 4, 4));
        }

        [Fact]
        public void SmallImprovementsAccumulateAgainstTheBestBaseline()
        {
            var p = new WaypointProgress();
            p.Observe(true, 0, 0, 0, 5, 2);
            p.Observe(true, 0, 0, 0, 4.9, 2);
            p.Observe(true, 0, 0, 0, 4.8, 2);
            Assert.False(p.Observe(true, 0, 0, 0, 4.7, 2));
            Assert.Equal(0, p.NoProgressSeconds);
        }

        [Fact]
        public void NewWaypointStartsAFreshWindow()
        {
            var p = new WaypointProgress();
            p.Observe(true, 0, 0, 0, 5, 2);
            p.Observe(true, 0, 0, 0, 5, 6);
            Assert.False(p.Observe(true, 0.2, 0, 0, 5, 2));
            Assert.Equal(0, p.NoProgressSeconds);
        }

        [Fact]
        public void IdleSeatedOrWaitingCallerResetsTheWindow()
        {
            var p = new WaypointProgress();
            p.Observe(true, 0, 0, 0, 5, 2);
            Assert.True(p.Observe(true, 0, 0, 0, 5, 8));
            Assert.False(p.Observe(false, 0, 0, 0, 5, 2));
            Assert.False(p.Observe(true, 0, 0, 0, 5, 2));
        }

        [Fact]
        public void InvalidObservationCannotTriggerRecovery()
        {
            var p = new WaypointProgress();
            p.Observe(true, 0, 0, 0, 5, 2);
            Assert.False(p.Observe(true, 0, 0, 0, double.NaN, 20));
            Assert.False(p.Observe(true, 0, 0, 0, 5, 20));
        }
    }
}
