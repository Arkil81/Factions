using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Choosing a fleet's route by what it costs rather than by whether it collides.</summary>
    public class RouteChooserTests
    {
        // Live contract at v0.261.0: a 500 m/s grid limit is 30 km/min, open-space cruise 500.
        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                FleetSpeedMinKmPerMin = 30.0,
                FleetSpeedOrbitKmPerMin = 120.0,
                FleetSpeedKmPerMin = 500.0,
                FleetAccelKmPerMin2 = 4608.0,
                FsdGravityDropoutG = 0.01,
                PlanetRouteLowAltitudeKm = 2.0,
                PlanetRouteClearanceKm = 10.0,
                PlanetRouteArcDegreesPerVertex = 36.0,
                PlanetRouteMaxWaypoints = 24,
                PlanetRouteMinArcVertices = 3,
                OrbitGravityMin = 0.05,
                FuelDrawMode = FuelDrawModes.Measure,
            };
        }

        // A body shaped like Cauldron: a wide gravity well reaching far past anything solid.
        private static PlanetRecord Body()
        {
            return new PlanetRecord
            {
                Id = "b1", Name = "Cauldron", Center = new Vec3(0, 0, 0),
                SurfaceRadius = 20.0, AtmosphereRadius = 0.0, GravityRadius = 300.0,
            };
        }

        private static WorldModel World(FactionTuning tuning)
        {
            var world = new WorldModel(new SeededRandom(1));
            world.Tuning = tuning;
            world.Planets = new StaticPlanetCatalogue(new[] { Body() });
            return world;
        }

        /// <summary>A point at <paramref name="radiusKm"/>, <paramref name="degrees"/> around the
        /// equator — the geometry the live ring legs have.</summary>
        private static Vec3 AtAngle(double radiusKm, double degrees)
        {
            double r = degrees * Math.PI / 180.0;
            return new Vec3(radiusKm * Math.Cos(r), 0, radiusKm * Math.Sin(r));
        }

        private static PendingFleetAction Fleet()
        {
            return new PendingFleetAction { FleetId = "F1", ActorTag = "ACME", Kind = ActionKind.Trade };
        }

        [Fact]
        public void ALegAcrossAWellGoesAroundItRatherThanThrough()
        {
            FactionTuning tuning = Tuning();
            WorldModel world = World(tuning);

            // Both ends just inside the well, subtending 150 degrees — the live ring-to-ring shape.
            Vec3 from = AtAngle(280, 0), to = AtAngle(280, 150);

            RouteCandidate chosen; int considered;
            PlanetRoutePlan plan = RouteChooser.Choose(world, tuning, Fleet(), RouteHullProfile.Unknown,
                                                       from, to, "F1", out chosen, out considered);

            Assert.True(plan.IsClear);
            Assert.True(considered > 1, "no alternative was costed, so nothing was chosen");
            Assert.NotEqual("direct", chosen.Label);

            // The direct chord is entirely inside the well, so it is billed at the grid limit.
            double directKm = from.DistanceTo(to);
            double directMins = tuning.TransitTimeMins(world.ZonesFor(from, to));
            Assert.Equal(directKm / tuning.FleetSpeedMinKmPerMin, directMins, 1);

            Assert.True(plan.DistanceKm > directKm, "the chosen route should be longer in distance");
            Assert.True(chosen.Minutes < directMins * 0.5,
                        "expected the detour to less than halve the time; direct " + directMins
                        + " min, chosen " + chosen.Minutes);
        }

        [Fact]
        public void TwoRoutesBetweenTheSameEndsAreCostedSeparately()
        {
            FactionTuning tuning = Tuning();
            WorldModel world = World(tuning);
            PlanetRouteOptions options = PlanetRouteOptions.FromTuning(tuning);
            Vec3 from = AtAngle(280, 0), to = AtAngle(280, 150);

            List<double> radii = PlanetRoutePlanner.ShellCandidateRadiiKm(Body(), world.Planets,
                                                                          RouteHullProfile.Unknown, options);
            Assert.True(radii.Count >= 2);

            var minutes = new List<double>();
            var vertexCounts = new List<int>();
            foreach (double r in radii)
            {
                var overrides = new Dictionary<string, double> { { "b1", r } };
                PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(from, to, world.Planets,
                                                                    RouteHullProfile.Unknown, options, "F1", overrides);
                Assert.True(plan.IsClear);
                var polyline = new List<Vec3> { from };
                polyline.AddRange(plan.Waypoints);
                polyline.Add(to);
                vertexCounts.Add(polyline.Count);
                minutes.Add(tuning.TransitTimeMins(world.ZonesFor(polyline)));
            }

            // These two routes have the SAME endpoints and the SAME vertex count, which is exactly
            // what a key made of the ends and the count cannot tell apart — the second candidate then
            // reads the first one's answer and the comparison is silently between one route and
            // itself.
            Assert.Equal(vertexCounts[0], vertexCounts[1]);
            Assert.NotEqual(minutes[0], minutes[1], 3);
        }

        [Fact]
        public void ALegThatNeverMeetsAWellIsFlownDirect()
        {
            FactionTuning tuning = Tuning();
            WorldModel world = World(tuning);

            RouteCandidate chosen; int considered;
            PlanetRoutePlan plan = RouteChooser.Choose(world, tuning, Fleet(), RouteHullProfile.Unknown,
                                                       new Vec3(5000, 0, 0), new Vec3(5000, 0, 900),
                                                       "F1", out chosen, out considered);

            Assert.True(plan.IsClear);
            Assert.Empty(plan.Waypoints);
            Assert.Equal("direct", chosen.Label);
            Assert.Equal(1, considered);
        }

        [Fact]
        public void TheChosenRouteIsAlwaysCollisionClearEvenWhenAnOverrideIsTooLow()
        {
            FactionTuning tuning = Tuning();
            PlanetRouteOptions options = PlanetRouteOptions.FromTuning(tuning);
            IPlanetCatalogue planets = new StaticPlanetCatalogue(new[] { Body() });

            // An override below the body's own surface must not be honoured as given.
            var tooLow = new Dictionary<string, double> { { "b1", 1.0 } };
            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(AtAngle(280, 0), AtAngle(280, 180),
                                                               planets, RouteHullProfile.Unknown,
                                                               options, "F1", tooLow);

            Assert.True(plan.IsClear);
            double collisionFloor = 20.0 + tuning.PlanetRouteClearanceKm;
            foreach (Vec3 waypoint in plan.Waypoints)
                Assert.True(waypoint.DistanceTo(new Vec3(0, 0, 0)) >= collisionFloor - 1e-6,
                            "a waypoint was placed inside the collision floor");
        }

        [Fact]
        public void AnInfeasibleLowPassIsNeverOfferedAsACandidate()
        {
            FactionTuning tuning = Tuning();
            PlanetRouteOptions options = PlanetRouteOptions.FromTuning(tuning);
            IPlanetCatalogue planets = new StaticPlanetCatalogue(new[] { Body() });

            // A hull with no scanned thrust cannot be shown to hold itself up anywhere, so the low
            // pass is not on offer whatever it would cost.
            List<double> radii = PlanetRoutePlanner.ShellCandidateRadiiKm(Body(), planets,
                                                                          RouteHullProfile.Unknown, options);
            double lowFloor = 20.0 + options.LowAltitudeKm;
            Assert.DoesNotContain(lowFloor, radii);

            // What it does offer leaves the well, and never sits below the collision floor.
            Assert.Contains(300.0 + options.FallbackClearanceKm, radii);
            foreach (double r in radii) Assert.True(r >= 20.0 + options.FallbackClearanceKm);
        }

        [Fact]
        public void CandidatesAreBounded()
        {
            FactionTuning tuning = Tuning();
            WorldModel world = World(tuning);

            RouteCandidate chosen; int considered;
            RouteChooser.Choose(world, tuning, Fleet(), RouteHullProfile.Unknown,
                                AtAngle(280, 0), AtAngle(280, 150), "F1", out chosen, out considered);

            Assert.InRange(considered, 1, RouteChooser.MaxCandidates);
        }

        [Fact]
        public void TheFuelTermIsSkippedAtTheDefaultWeight()
        {
            FactionTuning tuning = Tuning();
            Assert.Equal(0.0, tuning.RouteFuelWeightPerMinute);

            WorldModel world = World(tuning);
            RouteCandidate chosen; int considered;
            RouteChooser.Choose(world, tuning, Fleet(), RouteHullProfile.Unknown,
                                AtAngle(280, 0), AtAngle(280, 150), "F1", out chosen, out considered);

            // Not merely zero credits — the cost is the minutes, untouched by a fuel multiplication.
            Assert.Equal(0.0, chosen.FuelCredits);
            Assert.Equal(chosen.Minutes, chosen.Cost, 9);
        }
    }
}
