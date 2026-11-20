using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="ShipPool.RouteEnvironment.From"/> samples the planet catalogue, and on the SE host
    /// each sample is materially more expensive than the arithmetic around it. These tests pin how
    /// many samples one call is allowed to take, and — the half that matters — that it still takes
    /// two when the two ends really are different places.
    /// </summary>
    public class RouteEnvironmentSamplingTests
    {
        /// <summary>Counts every read, and reports a different environment either side of x=100 so a
        /// wrongly-shared sample is visible as a wrong value rather than only as a wrong count.</summary>
        private sealed class CountingCatalogue : IPlanetCatalogue
        {
            public int GravityCalls;
            public int PlanetEnumerations;

            private readonly PlanetRecord _planet = new PlanetRecord
            {
                Id = "P", Name = "Near", Generator = "EarthLike",
                Center = new Vec3(0, 0, 0), SurfaceRadius = 60, GravityRadius = 100,
                AtmosphereRadius = 80, HasAtmosphere = true,
            };

            public IEnumerable<PlanetRecord> Planets
            {
                get
                {
                    PlanetEnumerations++;
                    yield return _planet;
                }
            }

            public double GravityAt(Vec3 posKm)
            {
                GravityCalls++;
                return posKm.DistanceTo(_planet.Center) <= _planet.GravityRadius ? 1.0 : 0.0;
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        [Fact]
        public void From_SamplesOncePerEnd_WhenTheEndsDiffer()
        {
            var cat = new CountingCatalogue();
            var inside = new Vec3(0, 0, 0);      // deep in the well and in atmosphere
            var outside = new Vec3(500, 0, 0);   // outside gravity and atmosphere

            ShipPool.RouteEnvironment env = ShipPool.RouteEnvironment.From(cat, inside, outside, 1.0);

            // The correctness claim first: the ends are genuinely different places, so an
            // over-eager short-circuit must show up as a wrong ANSWER, not merely a low call count.
            Assert.NotEqual(env.OriginGravityG, env.DestGravityG);
            Assert.True(env.OriginInAtmosphere);
            Assert.False(env.DestInAtmosphere);
            // The leg leaves the atmosphere, so it also reads gravity once at the atmosphere's edge.
            Assert.True(env.LeavesAtmosphere);
            Assert.Equal(1.0, env.ExitGravityG);
            Assert.Equal(3, cat.GravityCalls);
            Assert.Equal(2, cat.PlanetEnumerations);
        }

        [Fact]
        public void From_SamplesOncePerEnd_WhenTheLegStaysInOneAtmosphere()
        {
            var cat = new CountingCatalogue();

            ShipPool.RouteEnvironment env = ShipPool.RouteEnvironment.From(cat, new Vec3(0, 0, 0), new Vec3(70, 0, 0), 1.0);

            Assert.False(env.LeavesAtmosphere);
            Assert.Equal(2, cat.GravityCalls);
            Assert.Equal(2, cat.PlanetEnumerations);
        }

        [Fact]
        public void From_SamplesOnce_WhenBothEndsAreTheSamePoint()
        {
            var cat = new CountingCatalogue();
            var here = new Vec3(0, 0, 0);

            ShipPool.RouteEnvironment env = ShipPool.RouteEnvironment.From(cat, here, here, 1.0);

            Assert.Equal(env.OriginGravityG, env.DestGravityG);
            Assert.Equal(env.OriginInAtmosphere, env.DestInAtmosphere);
            Assert.Equal(1, cat.GravityCalls);
            Assert.Equal(1, cat.PlanetEnumerations);
        }

        [Fact]
        public void From_SamePointResultMatchesTheTwoSampleResult()
        {
            var a = new CountingCatalogue();
            var b = new CountingCatalogue();
            var here = new Vec3(10, 20, 30);
            var alsoHere = new Vec3(10, 20, 30);   // equal by value, not the same instance

            ShipPool.RouteEnvironment shortCircuited = ShipPool.RouteEnvironment.From(a, here, here, 1.5, 0.25);
            ShipPool.RouteEnvironment twoEnded = ShipPool.RouteEnvironment.From(b, here, alsoHere, 1.5, 0.25);

            Assert.Equal(twoEnded.OriginGravityG, shortCircuited.OriginGravityG);
            Assert.Equal(twoEnded.DestGravityG, shortCircuited.DestGravityG);
            Assert.Equal(twoEnded.OriginInAtmosphere, shortCircuited.OriginInAtmosphere);
            Assert.Equal(twoEnded.DestInAtmosphere, shortCircuited.DestInAtmosphere);
            Assert.Equal(twoEnded.MinDryTwr, shortCircuited.MinDryTwr);
            Assert.Equal(twoEnded.GravityFloorG, shortCircuited.GravityFloorG);
        }
    }
}
