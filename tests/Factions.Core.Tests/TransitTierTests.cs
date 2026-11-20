using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The environment-based transit model: which tier a stretch of a leg is flown in, and
    /// what the resulting schedule costs.</summary>
    public class TransitTierTests
    {
        // The live contract at 2026-09-17: a 500 m/s grid limit is 30 km/min, cruise 500 km/min.
        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                FleetSpeedMinKmPerMin = 30.0,
                FleetSpeedOrbitKmPerMin = 120.0,
                FleetSpeedKmPerMin = 500.0,
                FleetAccelKmPerMin2 = 4608.0,
                FsdGravityDropoutG = 0.01,
            };
        }

        private static IPlanetCatalogue OneBody(Vec3 centre, double surfaceKm, double gravityKm)
        {
            return new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "b1", Name = "b1", Center = centre, SurfaceRadius = surfaceKm, GravityRadius = gravityKm },
            });
        }

        private static WorldFeaturePlacement Ring(Vec3 centre, double innerKm, double outerKm, double halfThicknessKm)
        {
            var ring = new WorldFeature
            {
                Name = "ring",
                Shape = WorldFeatureShape.Ring,
                Center = centre,
                InnerRadiusKm = innerKm,
                OuterRadiusKm = outerKm,
                HalfThicknessKm = halfThicknessKm,
                Zone = NodeZone.Orbit,
            };
            return new WorldFeaturePlacement(new[] { ring }, null);
        }

        private static List<Vec3> Line(Vec3 from, Vec3 to) { return new List<Vec3> { from, to }; }

        // ---- the two implementations of one curve ------------------------------------------------

        [Fact]
        public void PlainTransitAndScheduleAgreeOnADeepSpaceLeg()
        {
            FactionTuning tuning = Tuning();
            foreach (double distKm in new[] { 0.5, 5.0, 23.9, 48.0, 164.0, 1907.0, 12459.0 })
            {
                PlainTransit plain;
                Assert.True(PlainTransit.TryBuild(distKm, tuning, out plain));

                TransitSchedule schedule;
                Assert.True(TransitSchedule.TryBuild(LegZoneProfile.PlainDeepSpace(distKm), tuning, out schedule));

                Assert.Equal(plain.TotalMins, schedule.TotalMins, 9);
                Assert.Equal(plain.KmAtMins(plain.TotalMins * 0.37), schedule.KmAtMins(plain.TotalMins * 0.37), 7);
                Assert.Equal(plain.MinsAtKm(distKm * 0.62), schedule.MinsAtKm(distKm * 0.62), 9);
            }
        }

        [Fact]
        public void TheScheduleIsItsOwnInverseAcrossTiers()
        {
            FactionTuning tuning = Tuning();
            var zones = new LegZoneProfile();
            zones.HasGeometry = true;
            zones.Add(TransitTier.Gravity, 40.0);
            zones.Add(TransitTier.DeepSpace, 600.0);
            zones.Add(TransitTier.Orbit, 90.0);
            zones.Add(TransitTier.Gravity, 25.0);

            TransitSchedule schedule;
            Assert.True(TransitSchedule.TryBuild(zones, tuning, out schedule));

            for (int i = 1; i < 40; i++)
            {
                double mins = schedule.TotalMins * i / 40.0;
                double km = schedule.KmAtMins(mins);
                Assert.Equal(mins, schedule.MinsAtKm(km), 7);
            }
            Assert.Equal(zones.TotalKm, schedule.TotalKm, 9);
        }

        // ---- what the tiers cost -----------------------------------------------------------------

        [Fact]
        public void GravityCostsTheGridLimitAndOrbitCostsItsOwnCap()
        {
            FactionTuning tuning = Tuning();
            const double km = 100.0;

            double deep = tuning.TransitTimeMins(LegZoneProfile.PlainDeepSpace(km));

            var gravity = new LegZoneProfile(); gravity.HasGeometry = true;
            gravity.Add(TransitTier.Gravity, km);
            var orbit = new LegZoneProfile(); orbit.HasGeometry = true;
            orbit.Add(TransitTier.Orbit, km);

            double inGravity = tuning.TransitTimeMins(gravity);
            double inOrbit = tuning.TransitTimeMins(orbit);

            // A run held at its cap the whole way is the distance over that cap; the ends are already
            // at the grid limit, so the gravity run has no ramp at all.
            Assert.Equal(km / tuning.FleetSpeedMinKmPerMin, inGravity, 6);
            Assert.True(inOrbit < inGravity && inOrbit > deep);
        }

        [Fact]
        public void AnOrbitCapOutsideItsRangeLeavesTheTierFlyingAsOpenSpace()
        {
            var orbit = new LegZoneProfile(); orbit.HasGeometry = true;
            orbit.Add(TransitTier.Orbit, 100.0);

            foreach (double cap in new[] { 0.0, -5.0, 900.0 })
            {
                FactionTuning tuning = Tuning();
                tuning.FleetSpeedOrbitKmPerMin = cap;
                Assert.Equal(tuning.FleetSpeedKmPerMin, TransitSchedule.CapFor(TransitTier.Orbit, tuning));
                Assert.Equal(tuning.TransitTimeMins(LegZoneProfile.PlainDeepSpace(100.0)),
                             tuning.TransitTimeMins(orbit), 9);
            }
        }

        [Fact]
        public void AFleetShedsSpeedBeforeTheWellRatherThanAtItsEdge()
        {
            FactionTuning tuning = Tuning();
            var zones = new LegZoneProfile(); zones.HasGeometry = true;
            zones.Add(TransitTier.DeepSpace, 400.0);
            zones.Add(TransitTier.Gravity, 40.0);

            TransitSchedule schedule;
            Assert.True(TransitSchedule.TryBuild(zones, tuning, out schedule));

            // The speed must already be down to the well's cap on the APPROACH to its edge. Read on
            // the far side it is 30 whether or not anything decelerated, because the gravity run
            // starts at its own cap either way; the whole question is what the last kilometre before
            // the boundary was flown at, and with no braking that is cruise.
            double approach = SpeedToKm(schedule, 400.0);
            Assert.True(approach < tuning.FleetSpeedMinKmPerMin * 1.5,
                        "expected the approach to the well to be near its " + tuning.FleetSpeedMinKmPerMin
                        + " km/min cap; a fleet that never brakes reads " + tuning.FleetSpeedKmPerMin
                        + ", and this read " + approach);

            // Braking from 500 to 30 km/min at 4608 km/min^2 takes 27 km, so a fleet 50 km out is
            // still at cruise: the slowdown is local to the well, not smeared over the leg.
            Assert.Equal(tuning.FleetSpeedKmPerMin, SpeedFromKm(schedule, 350.0), 6);
        }

        /// <summary>Speed in km/min over the short stretch starting at <paramref name="km"/>,
        /// differenced off the schedule itself so it reads what the curve actually does rather than
        /// what it was built from. One-sided on purpose: a window straddling a tier boundary averages
        /// the two sides and would report neither.</summary>
        private static double SpeedFromKm(TransitSchedule schedule, double km)
        {
            const double stepKm = 0.1;
            return stepKm / (schedule.MinsAtKm(km + stepKm) - schedule.MinsAtKm(km));
        }

        /// <summary>Speed in km/min over the short stretch ENDING at <paramref name="km"/>.</summary>
        private static double SpeedToKm(TransitSchedule schedule, double km)
        {
            const double stepKm = 0.01;
            return stepKm / (schedule.MinsAtKm(km) - schedule.MinsAtKm(km - stepKm));
        }

        // ---- which tier a point is in ------------------------------------------------------------

        [Fact]
        public void GravityOutranksAnAuthoredOrbitRegion()
        {
            FactionTuning tuning = Tuning();
            var centre = new Vec3(0, 0, 0);
            IPlanetCatalogue planets = OneBody(centre, 180.0, 305.0);
            WorldFeaturePlacement ring = Ring(centre, 195.0, 465.0, 1.8);

            // Inside the ring AND inside the well: gravity wins.
            Assert.Equal(TransitTier.Gravity,
                         LegZoneProfile.TierAt(planets, ring, new Vec3(250.0, 0, 0), tuning.FsdGravityDropoutG));
            // Inside the ring, outside the well: the authored zone stands.
            Assert.Equal(TransitTier.Orbit,
                         LegZoneProfile.TierAt(planets, ring, new Vec3(400.0, 0, 0), tuning.FsdGravityDropoutG));
            // Outside both.
            Assert.Equal(TransitTier.DeepSpace,
                         LegZoneProfile.TierAt(planets, ring, new Vec3(600.0, 0, 0), tuning.FsdGravityDropoutG));
        }

        [Fact]
        public void APathOverARingsPoleIsOpenSpaceNotRingTraffic()
        {
            FactionTuning tuning = Tuning();
            var centre = new Vec3(0, 0, 0);
            IPlanetCatalogue planets = OneBody(centre, 180.0, 305.0);
            WorldFeaturePlacement ring = Ring(centre, 195.0, 465.0, 1.8);

            // Same radius from the axis, but 50 km above the ring plane: outside its half-thickness.
            // ClaimsForPlacement would call this ring; containment does not.
            var abovePlane = new Vec3(400.0, 50.0, 0);
            Assert.Equal(TransitTier.DeepSpace,
                         LegZoneProfile.TierAt(planets, ring, abovePlane, tuning.FsdGravityDropoutG));

            NodeZone claimed;
            Assert.True(ring.TryZoneAt(abovePlane, out claimed));       // placement claims it
            NodeZone contained;
            Assert.False(ring.TryZoneContaining(abovePlane, out contained)); // containment does not
        }

        // ---- the walk, the shortcut and the cheap form -------------------------------------------

        [Fact]
        public void TheOpenSpaceShortcutMatchesTheSampledWalk()
        {
            FactionTuning tuning = Tuning();
            IPlanetCatalogue planets = OneBody(new Vec3(0, 0, 0), 180.0, 305.0);
            WorldFeaturePlacement ring = Ring(new Vec3(0, 0, 0), 195.0, 465.0, 1.8);

            List<Vec3> clear = Line(new Vec3(5000, 0, 0), new Vec3(5000, 0, 900));
            Assert.True(LegZoneCache.IsPlainDeepSpace(planets, ring, clear));

            LegZoneProfile shortcut = LegZoneCache.Build(planets, ring, tuning, clear);
            LegZoneProfile walked = LegZoneProfile.Build(planets, ring, tuning, clear);

            Assert.True(shortcut.IsPlainDeepSpace);
            Assert.Equal(walked.TotalKm, shortcut.TotalKm, 6);
            Assert.Equal(tuning.TransitTimeMins(walked), tuning.TransitTimeMins(shortcut), 6);
        }

        [Fact]
        public void TheWalkFindsAWellCrossedBetweenTheEndsAndTheCheapFormDoesNot()
        {
            FactionTuning tuning = Tuning();
            IPlanetCatalogue planets = OneBody(new Vec3(0, 0, 0), 180.0, 305.0);

            // Straight through the body's well, with both ends well outside it.
            List<Vec3> through = Line(new Vec3(-2000, 0, 0), new Vec3(2000, 0, 0));

            LegZoneProfile walked = LegZoneProfile.Build(planets, null, tuning, through);
            LegZoneProfile cheap = LegZoneProfile.FromEndpoints(planets, tuning, through[0], through[1]);

            Assert.True(walked.KmIn(TransitTier.Gravity) > 500.0);
            Assert.Equal(0.0, cheap.KmIn(TransitTier.Gravity));
            Assert.True(tuning.TransitTimeMins(cheap) < tuning.TransitTimeMins(walked));
        }

        [Fact]
        public void TheCheapFormPricesAWellAtEitherEnd()
        {
            FactionTuning tuning = Tuning();
            IPlanetCatalogue planets = OneBody(new Vec3(0, 0, 0), 180.0, 305.0);

            // Departing from inside the well, straight out.
            LegZoneProfile cheap = LegZoneProfile.FromEndpoints(planets, tuning,
                                                                new Vec3(200, 0, 0), new Vec3(2000, 0, 0));
            LegZoneProfile walked = LegZoneProfile.Build(planets, null, tuning, Line(new Vec3(200, 0, 0), new Vec3(2000, 0, 0)));

            Assert.True(cheap.KmIn(TransitTier.Gravity) > 90.0);
            Assert.Equal(walked.KmIn(TransitTier.Gravity), cheap.KmIn(TransitTier.Gravity), 0);
            Assert.Equal(walked.TotalKm, cheap.TotalKm, 6);
        }

        [Fact]
        public void NeitherEndCanClaimMoreThanHalfAShortLeg()
        {
            FactionTuning tuning = Tuning();
            IPlanetCatalogue planets = OneBody(new Vec3(0, 0, 0), 180.0, 305.0);

            LegZoneProfile cheap = LegZoneProfile.FromEndpoints(planets, tuning,
                                                                new Vec3(200, 0, 0), new Vec3(210, 0, 0));
            Assert.Equal(10.0, cheap.TotalKm, 6);
            Assert.Equal(10.0, cheap.KmIn(TransitTier.Gravity), 6);
        }

        // ---- the run table -----------------------------------------------------------------------

        [Fact]
        public void BoundingTheRunTableKeepsItsLengthAndNeverMakesTheLegFaster()
        {
            FactionTuning tuning = Tuning();
            var zones = new LegZoneProfile();
            zones.HasGeometry = true;
            for (int i = 0; i < LegZoneProfile.MaxRuns * 2; i++)
                zones.Add(i % 2 == 0 ? TransitTier.DeepSpace : TransitTier.Gravity, 10.0 + i);

            double before = tuning.TransitTimeMins(zones);
            double lengthBefore = zones.TotalKm;

            zones.Bound();

            Assert.True(zones.RunKm.Count <= LegZoneProfile.MaxRuns);
            Assert.Equal(lengthBefore, zones.TotalKm, 6);
            Assert.Equal(lengthBefore, Sum(zones.RunKm), 6);
            Assert.True(tuning.TransitTimeMins(zones) >= before - 1e-9,
                        "bounding made the leg faster than the ground allows");
        }

        [Fact]
        public void AnEmptyOrDegenerateProfileHasNoSchedule()
        {
            FactionTuning tuning = Tuning();
            TransitSchedule schedule;

            Assert.False(TransitSchedule.TryBuild(new LegZoneProfile(), tuning, out schedule));
            Assert.False(TransitSchedule.TryBuild(LegZoneProfile.PlainDeepSpace(100), null, out schedule));

            FactionTuning noAccel = Tuning(); noAccel.FleetAccelKmPerMin2 = 0;
            Assert.False(TransitSchedule.TryBuild(LegZoneProfile.PlainDeepSpace(100), noAccel, out schedule));
            Assert.Equal(0.0, noAccel.TransitTimeMins(100.0));

            FactionTuning inverted = Tuning(); inverted.FleetSpeedKmPerMin = 1.0;
            Assert.False(TransitSchedule.TryBuild(LegZoneProfile.PlainDeepSpace(100), inverted, out schedule));
        }

        private static double Sum(List<double> values)
        {
            double total = 0;
            for (int i = 0; i < values.Count; i++) total += values[i];
            return total;
        }
    }
}
