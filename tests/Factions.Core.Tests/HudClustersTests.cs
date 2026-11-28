using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The HUD bundles scanner markers by SE's <c>IsPOINearby</c> at 10 degrees.</summary>
    public class HudClustersTests
    {
        private const double Angle = HudClusters.ClusterAngleDeg;

        [Fact]
        public void Near_IsAboutTenDegreesSeenFromTheCamera()
        {
            // Two points 100 km out, either side of the view axis. Half the separation against
            // sin(5 deg) of the distance to the midpoint: 8.7 km is the edge.
            Assert.True(HudClusters.Near(-8.5, 0, 100, 8.5, 0, 100, 0, 0, 0, Angle));
            Assert.False(HudClusters.Near(-9.0, 0, 100, 9.0, 0, 100, 0, 0, 0, Angle));
            // The same pair seen from twice as far bundles.
            Assert.True(HudClusters.Near(-9.0, 0, 100, 9.0, 0, 100, 0, 0, -100, Angle));
        }

        [Fact]
        public void Near_ADepthSeparationAlongTheViewCountsToo()
        {
            // SE's test is on the 3D separation, not the screen angle: a grid 1 km in front of a
            // station 30 km out bundles, one 10 km in front does not.
            Assert.True(HudClusters.Near(0, 0, 30, 0, 0, 29, 0, 0, 0, Angle));
            Assert.False(HudClusters.Near(0, 0, 30, 0, 0, 20, 0, 0, 0, Angle));
        }

        [Fact]
        public void Seeds_BundleAroundTheFirstAndNotByChaining()
        {
            // 0 and 1 are near, 1 and 2 are near, 0 and 2 are not: SE adds to the seed's group
            // only what is near the seed, so 2 leads a group of its own.
            double[] xyz = { 0, 0, 100, 10, 0, 100, 20, 0, 100 };
            int[] seeds = HudClusters.Seeds(xyz, new[] { true, true, true }, 0, 0, 0, Angle);
            Assert.Equal(new[] { 0, 0, 2 }, seeds);
        }

        [Fact]
        public void Seeds_AnExemptPointStandsAloneAndBundlesNothing()
        {
            double[] xyz = { 0, 0, 100, 1, 0, 100, 2, 0, 100 };
            Assert.Equal(new[] { 0, 1, 1 }, HudClusters.Seeds(xyz, new[] { false, true, true }, 0, 0, 0, Angle));
            Assert.Equal(new[] { 0, 1, 0 }, HudClusters.Seeds(xyz, new[] { true, false, true }, 0, 0, 0, Angle));
        }

        [Fact]
        public void Seeds_FarApartPointsAreEachTheirOwn()
        {
            double[] xyz = { 0, 0, 100, 50, 0, 100, -50, 0, 100 };
            Assert.Equal(new[] { 0, 1, 2 }, HudClusters.Seeds(xyz, new[] { true, true, true }, 0, 0, 0, Angle));
            Assert.Empty(HudClusters.Seeds(new double[0], new bool[0], 0, 0, 0, Angle));
        }
    }
}
