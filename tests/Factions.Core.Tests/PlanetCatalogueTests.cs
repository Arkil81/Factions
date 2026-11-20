using System.Collections.Generic;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class PlanetCatalogueTests
    {
        [Fact]
        public void StaticCatalogue_ClassifiesAndProjectsAgainstTheConfiguredPlanet()
        {
            var catalogue = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord
                {
                    Id = "earth",
                    Name = "Earthlike",
                    Generator = "EarthLike",
                    Center = new Vec3(100, 0, 0),
                    SurfaceRadius = 60,
                    GravityRadius = 100,
                    AtmosphereRadius = 80,
                    HasAtmosphere = true,
                },
            });

            Assert.Single(catalogue.Planets);
            Assert.Equal(1.0, catalogue.GravityAt(new Vec3(160, 0, 0)), 5);
            Assert.Equal(0.0, catalogue.GravityAt(new Vec3(220, 0, 0)), 5);
            Assert.Equal(160.0, catalogue.NearestSurfacePoint(new Vec3(190, 0, 0)).X, 5);
            Assert.Equal(1.0, catalogue.SurfaceNormal(new Vec3(150, 0, 0)).X, 5);
        }

        [Fact]
        public void RoutePlanner_ReturnsSafeDeterministicEntryAndExitLegsForMultipleBodies()
        {
            var bodies = new[]
            {
                new PlanetRecord { Id = "A", Center = new Vec3(-80, 0, 0), SurfaceRadius = 50 },
                new PlanetRecord { Id = "B", Center = new Vec3(80, 0, 0), SurfaceRadius = 50 },
            };
            PlanetRoutePlan first = PlanetRoutePlanner.BuildPlan(new Vec3(-300, 0, 0), new Vec3(300, 0, 0), bodies, 20, "D33079");
            PlanetRoutePlan second = PlanetRoutePlanner.BuildPlan(new Vec3(-300, 0, 0), new Vec3(300, 0, 0), bodies, 20, "D33079");

            Assert.True(first.IsClear, first.FailureReason);
            Assert.True(first.Waypoints.Count >= 4); // entry+exit for both blocking bodies
            Assert.Equal(first.Waypoints.Count, second.Waypoints.Count);
            for (int i = 0; i < first.Waypoints.Count; i++)
                Assert.Equal(first.Waypoints[i].ToString(), second.Waypoints[i].ToString());
            AssertRouteClears(new Vec3(-300, 0, 0), first.Waypoints, new Vec3(300, 0, 0), bodies, 20);
        }

        [Fact]
        public void RoutePlanner_SamplesAnArc_InsteadOfOneShelfLeg()
        {
            var body = new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 60 };
            var bodies = new[] { body };
            var origin = new Vec3(-400, 0, 0);
            var destination = new Vec3(400, 0, 0);

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(origin, destination, bodies, 10, "ARC1");

            Assert.True(plan.IsClear, plan.FailureReason);
            // A single entry/exit shelf would be two points; an arc is sampled.
            Assert.True(plan.Waypoints.Count >= 3, "expected a sampled arc, got " + plan.Waypoints.Count + " waypoints");
            AssertRouteClears(origin, plan.Waypoints, destination, bodies, 10);

            // Every generated vertex sits on one shell — that is what makes the track read as an arc
            // rather than a detour to an arbitrary offset point.
            double first = body.Center.DistanceTo(plan.Waypoints[0]);
            foreach (Vec3 waypoint in plan.Waypoints)
                Assert.Equal(first, body.Center.DistanceTo(waypoint), 3);
        }

        [Fact]
        public void RoutePlanner_KeepsArcVerticesWithinTheConfiguredAngularStep()
        {
            var body = new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 60 };
            // Endpoints close in on either side, so the tangents force a wide sweep to get around.
            var origin = new Vec3(-75, 0, 0);
            var destination = new Vec3(75, 0, 0);

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(origin, destination, new[] { body }, 5, "ARC2");

            Assert.True(plan.IsClear, plan.FailureReason);
            double maxStepDegrees = 0;
            for (int i = 0; i < plan.Waypoints.Count - 1; i++)
                maxStepDegrees = System.Math.Max(maxStepDegrees,
                    AngleDegrees(plan.Waypoints[i], plan.Waypoints[i + 1], body.Center));

            // The default 36 degrees per vertex is the "ten vertices for a full orbit" density.
            Assert.True(maxStepDegrees <= 36.0 + 1e-6,
                "arc step was " + maxStepDegrees.ToString("0.0") + " degrees");
            Assert.True(maxStepDegrees > 0, "expected intermediate arc vertices for a wide sweep");
        }

        [Fact]
        public void RoutePlanner_OriginOnTheSurface_ClimbsRadiallyInsteadOfFailing()
        {
            var body = new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 60 };
            var bodies = new[] { body };
            var origin = new Vec3(0, -60, 0);      // a node on the surface
            var destination = new Vec3(400, 0, 0); // on the far side of the body from it

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(origin, destination, bodies, 10, "CLIMB");

            Assert.True(plan.IsClear, plan.FailureReason);
            Assert.True(plan.Waypoints.Count > 0);

            // The first hop is straight up: the launch point is radially above the origin.
            Vec3 first = plan.Waypoints[0];
            Assert.Equal(origin.X, first.X, 3);
            Assert.Equal(origin.Z, first.Z, 3);
            Assert.True(System.Math.Abs(first.Y) > 60, "expected a climb above the surface radius");
            AssertRouteClears(origin, plan.Waypoints, destination, bodies, 10);
        }

        [Fact]
        public void RoutePlanner_SurfaceToSurfaceOnTheSameBody_RoutesOverTheTop()
        {
            var body = new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 60 };
            var bodies = new[] { body };
            var origin = new Vec3(0, -60, 0);
            var destination = new Vec3(0, 60, 0); // antipodal surface node

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(origin, destination, bodies, 10, "HOP");

            Assert.True(plan.IsClear, plan.FailureReason);
            Assert.True(plan.Waypoints.Count >= 4);
            Assert.True(plan.DistanceKm > origin.DistanceTo(destination));
            AssertRouteClears(origin, plan.Waypoints, destination, bodies, 10);
        }

        [Fact]
        public void RoutePlanner_LowProfileNeedsAHullThatCanHoldItselfUpDownThere()
        {
            var catalogue = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord
                {
                    Id = "earth", Center = new Vec3(0, 0, 0),
                    SurfaceRadius = 60, GravityRadius = 200, AtmosphereRadius = 70, HasAtmosphere = true,
                },
            });
            PlanetRouteOptions options = PlanetRouteOptions.Defaults;
            var origin = new Vec3(-400, 0, 0);
            var destination = new Vec3(400, 0, 0);

            PlanetRoutePlan lifter = PlanetRoutePlanner.BuildPlan(origin, destination, catalogue,
                Hull(forceN: 5e7f, dryMassKg: 1e5), options, "LIFT");
            PlanetRoutePlan sled = PlanetRoutePlanner.BuildPlan(origin, destination, catalogue,
                Hull(forceN: 1e3f, dryMassKg: 1e5), options, "SLED");
            PlanetRoutePlan unknown = PlanetRoutePlanner.BuildPlan(origin, destination, catalogue,
                RouteHullProfile.Unknown, options, "UNK");

            Assert.True(lifter.IsClear && sled.IsClear && unknown.IsClear);
            double lifterRadius = ArcRadius(lifter);
            double sledRadius = ArcRadius(sled);
            double unknownRadius = ArcRadius(unknown);

            // A hull with lift to spare takes the short low pass just above the terrain; one that
            // cannot hold itself up there, and one with no scanned thrust at all, stay on the outer
            // shell where there is nothing to fight.
            Assert.True(lifterRadius < 70, "lifter arc radius was " + lifterRadius.ToString("0.0"));
            Assert.True(sledRadius > 100, "sled arc radius was " + sledRadius.ToString("0.0"));
            Assert.True(unknownRadius > 100, "unknown-hull arc radius was " + unknownRadius.ToString("0.0"));
        }

        [Fact]
        public void RoutePlanner_OrbitProfileUsesTheSameShellStationsSnapTo()
        {
            var planet = new PlanetRecord
            {
                Id = "earth", Center = new Vec3(0, 0, 0),
                SurfaceRadius = 60, GravityRadius = 200, AtmosphereRadius = 70, HasAtmosphere = true,
            };
            var catalogue = new StaticPlanetCatalogue(new[] { planet });
            PlanetRouteOptions options = PlanetRouteOptions.Defaults;

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(new Vec3(-400, 0, 0), new Vec3(400, 0, 0),
                catalogue, RouteHullProfile.Unknown, options, "ORB");
            double bandRadius = PlanetGeometry.OrbitBandRadius(catalogue, planet, options.OrbitGravityMin,
                new Vec3(1, 0, 0));

            Assert.True(plan.IsClear, plan.FailureReason);
            Assert.True(bandRadius > 0);
            // Vertices sit a sampling-inflation step above the shell so the chords between them touch
            // it rather than cut inside it.
            Assert.InRange(ArcRadius(plan), bandRadius, bandRadius * 1.1);
        }

        [Fact]
        public void RoutePlanner_ReportsFailureInsteadOfEmittingACollidingRoute()
        {
            var bodies = new[] { new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 60 } };
            PlanetRouteOptions options = PlanetRouteOptions.Defaults;
            options.MaxWaypoints = 1; // too small for any arc, which is at least an entry and an exit

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(new Vec3(-400, 0, 0), new Vec3(400, 0, 0),
                new StaticPlanetCatalogue(bodies), RouteHullProfile.Unknown, options, "TIGHT");

            Assert.False(plan.IsClear);
            Assert.NotNull(plan.FailureReason);
            Assert.Empty(plan.Waypoints);
        }

        private static RouteHullProfile Hull(float forceN, double dryMassKg)
        {
            var down = new List<ThrustBand>
            {
                new ThrustBand
                {
                    ThrusterType = "Atmospheric",
                    ForceMagnitudeN = forceN,
                    MinPlanetaryInfluence = 0f,
                    MaxPlanetaryInfluence = 1f,
                    EffectivenessAtMinInfluence = 1f,
                    EffectivenessAtMaxInfluence = 1f,
                },
            };
            var bands = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Down, down },
            };
            return new RouteHullProfile { ThrustBands = bands, DryMassKg = dryMassKg, MinDryTwr = 1.0 };
        }

        private static double ArcRadius(PlanetRoutePlan plan)
        {
            return new Vec3(0, 0, 0).DistanceTo(plan.Waypoints[0]);
        }

        private static double AngleDegrees(Vec3 a, Vec3 b, Vec3 center)
        {
            double ax = a.X - center.X, ay = a.Y - center.Y, az = a.Z - center.Z;
            double bx = b.X - center.X, by = b.Y - center.Y, bz = b.Z - center.Z;
            double la = System.Math.Sqrt(ax * ax + ay * ay + az * az);
            double lb = System.Math.Sqrt(bx * bx + by * by + bz * bz);
            if (la <= 0 || lb <= 0) return 0;
            double cos = (ax * bx + ay * by + az * bz) / (la * lb);
            if (cos > 1) cos = 1; else if (cos < -1) cos = -1;
            return System.Math.Acos(cos) * 180.0 / System.Math.PI;
        }

        /// <summary>Asserts no segment dives below the body's clearance shell, sampling chord
        /// midpoints as well as endpoints so a polyline that only touches the shell at its vertices
        /// cannot pass.</summary>
        private static void AssertRouteClears(Vec3 origin, IList<Vec3> waypoints, Vec3 destination,
            IEnumerable<PlanetRecord> bodies, double clearanceKm)
        {
            var points = new List<Vec3> { origin };
            points.AddRange(waypoints);
            points.Add(destination);
            for (int i = 0; i < points.Count - 1; i++)
                foreach (PlanetRecord body in bodies)
                    Assert.True(PlanetRoutePlanner.SegmentClear(points[i], points[i + 1], body, clearanceKm),
                        "segment " + i + " cut inside " + body.Id);
        }
    }
}
