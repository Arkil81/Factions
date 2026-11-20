using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The rule both client features draw a moving contact by. It only ran on a live client before
    /// it was shared, so the arrival clamp -- the half that decides whether a convoy parks at its
    /// destination or sails through it -- had never been asserted anywhere.
    /// </summary>
    public sealed class DeadReckonTests
    {
        private static void At(
            double elapsed, out double x, out double y, out double z,
            double velY = 10.0, double targetY = 1000.0)
        {
            DeadReckon.Extrapolate(
                0, 0, 0,
                0, velY, 0,
                0, targetY, 0,
                elapsed,
                out x, out y, out z);
        }

        [Fact]
        public void AdvancesAlongVelocity_SoAContactMovesBetweenServerPasses()
        {
            double x, y, z;
            At(5.0, out x, out y, out z);
            Assert.Equal(50.0, y, 9);
            Assert.Equal(0.0, x, 9);
            Assert.Equal(0.0, z, 9);
        }

        [Fact]
        public void ClampsAtTheTarget_RatherThanSailingPastIt()
        {
            // 10/s for 500 s is 5000 along an axis whose target is at 1000. The characteristic
            // failure without the clamp is a convoy drawn far beyond a station it has reached.
            double x, y, z;
            At(500.0, out x, out y, out z);
            Assert.Equal(1000.0, y, 9);
        }

        [Fact]
        public void StationaryContact_IsExactlyWhereItWasSampled()
        {
            // A node is anchored and carries no motion; the target is left at the origin, which is
            // the shape that would snap it there if a zero velocity were extrapolated anyway.
            double x, y, z;
            DeadReckon.Extrapolate(
                7, 8, 9,
                0, 0, 0,
                0, 0, 0,
                30.0,
                out x, out y, out z);
            Assert.Equal(7.0, x, 9);
            Assert.Equal(8.0, y, 9);
            Assert.Equal(9.0, z, 9);
        }

        [Fact]
        public void NonPositiveElapsed_ReturnsTheSampleUnchanged()
        {
            double x, y, z;
            At(0.0, out x, out y, out z);
            Assert.Equal(0.0, y, 9);
            At(-1.0, out x, out y, out z);
            Assert.Equal(0.0, y, 9);
        }

        [Fact]
        public void ATargetAlreadyReached_DoesNotDragTheContactBackwards()
        {
            // Sample sitting on its target: every extrapolation overshoots by definition, and the
            // clamp must resolve to the same point rather than to the origin.
            double x, y, z;
            DeadReckon.Extrapolate(
                0, 1000, 0,
                0, 10, 0,
                0, 1000, 0,
                5.0,
                out x, out y, out z);
            Assert.Equal(1000.0, y, 9);
        }
    }
}
