using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="FuelBurn"/> and <see cref="LegImpulseProfile"/> — the burn half of
    /// docs/fuel-plan.md. The band constants are the vanilla large-grid thrusters the plan
    /// tabulates, so the numeric assertions check the model against the game rather than against
    /// itself.
    /// </summary>
    public class FuelBurnTests
    {
        // ---- vanilla bands (CubeBlocks_Thrusters.sbc + GasProperties.sbc) --------------------

        private const string HydrogenKey = "Gas:Hydrogen";

        /// <summary>Large-grid large hydrogen thruster: 7.2 MN, 7.5 MW-equivalent, H2 at
        /// 0.001556 MWh/L, efficiency 1. Min == Max influence, so the curve is flat at 1.</summary>
        private static ThrustBand HydrogenBand()
        {
            var band = new ThrustBand();
            band.ThrusterType = "Hydrogen";
            band.ForceMagnitudeN = 7.2e6f;
            band.MinPlanetaryInfluence = 0;
            band.MaxPlanetaryInfluence = 0;
            band.EffectivenessAtMinInfluence = 1;
            band.EffectivenessAtMaxInfluence = 1;
            band.MaxPowerMw = 7.5f;
            band.FuelId = HydrogenKey;
            band.FuelEfficiency = 1f;
            band.FuelEnergyDensityMwhPerL = 0.001556f;
            return band;
        }

        /// <summary>Large-grid large ion thruster: 4.32 MN, 33.6 MW, electric, full in deep space
        /// and 20% at a planet surface.</summary>
        private static ThrustBand IonBand()
        {
            var band = new ThrustBand();
            band.ThrusterType = "Ion";
            band.ForceMagnitudeN = 4.32e6f;
            band.MinPlanetaryInfluence = 0;
            band.MaxPlanetaryInfluence = 1;
            band.EffectivenessAtMinInfluence = 1f;
            band.EffectivenessAtMaxInfluence = 0.2f;
            band.MaxPowerMw = 33.6f;
            band.FuelId = null;
            band.FuelEfficiency = 1f;
            band.FuelEnergyDensityMwhPerL = 0;
            return band;
        }

        private static IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> AllDirections(params ThrustBand[] bands)
        {
            var map = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            var list = new List<ThrustBand>(bands);
            map[ThrustDirection.Forward] = list;
            map[ThrustDirection.Backward] = list;
            map[ThrustDirection.Down] = list;
            return map;
        }

        // ---- tuning ------------------------------------------------------------------------

        /// <summary>Live shipped values: thruster cruise 100 m/s, sim (FSD) cruise 500 km/min =
        /// 8.333 km/s, and the RC.4 FSD mod's own constants.
        ///
        /// <para><c>FleetSpeedMinKmPerMin</c> is set to the live 30 km/min (500 m/s, SE's
        /// <c>LargeShipMaxSpeed</c>) deliberately: no burn term reads it, and a thruster figure that
        /// moved with it would be reading the world's speed limit instead of the speed a hull
        /// flies.</para></summary>
        private static FactionTuning Tuning()
        {
            var tuning = new FactionTuning();
            tuning.FleetSpeedMinKmPerMin = 30.0;
            tuning.ThrusterCruiseSpeedMs = 100.0;
            tuning.FleetSpeedKmPerMin = 500.0;
            tuning.FuelBurnScale = 1.0;
            tuning.FsdGravityDropoutG = 0.01;
            tuning.FsdBaseRequiredPowerMw = 26.0;
            tuning.FsdPowerRequirementMultiplier = 2.0;
            tuning.FsdPowerBySpeedDivider = 8.0;
            tuning.FsdMaxSpeedKmPerS = 2000.0;
            tuning.FsdChargeSeconds = 10.0;
            tuning.FsdChargeMwPerKg = 2.1e-5;
            tuning.ReactorFuelKgPerMwh = 1.0;
            return tuning;
        }

        private static StaticPlanetCatalogue DeepSpace()
        {
            return new StaticPlanetCatalogue(new PlanetRecord[0]);
        }

        /// <summary>One body with a 600 km surface and a 1,000 km gravity limit. The proxy
        /// catalogue reads 1.0 at or inside the surface and ramps linearly to 0 at the limit.</summary>
        private static PlanetRecord TestPlanet(string id, double centreX)
        {
            var planet = new PlanetRecord();
            planet.Id = id;
            planet.Name = id;
            planet.Center = new Vec3(centreX, 0, 0);
            planet.SurfaceRadius = 600;
            planet.GravityRadius = 1000;
            planet.AtmosphereRadius = 0;
            planet.HasAtmosphere = false;
            return planet;
        }

        private static List<Vec3> Line(Vec3 from, Vec3 to)
        {
            var points = new List<Vec3>();
            points.Add(from);
            points.Add(to);
            return points;
        }

        private static double Gas(LegBurn burn, string key)
        {
            double litres;
            return burn.GasLitres.TryGetValue(key, out litres) ? litres : 0;
        }

        // ---- (a) the vanilla hydrogen figure ------------------------------------------------

        [Fact]
        public void SpecificConsumption_LargeHydrogenThruster_Burns4820LitresPerSecondAtFullThrust()
        {
            ThrustBand band = HydrogenBand();
            BandBurnRate rate = FuelBurn.SpecificConsumption(band);

            Assert.True(rate.HasBurnData);
            Assert.Equal(HydrogenKey, rate.FuelId);
            Assert.Equal(0.0, rate.ElectricMwhPerNewtonSecond);

            // One second at full thrust delivers ForceMagnitudeN newton-seconds.
            double litresPerSecond = rate.GasLitresPerNewtonSecond * band.ForceMagnitudeN;
            Assert.InRange(litresPerSecond, 4820 * 0.99, 4820 * 1.01);
        }

        [Fact]
        public void SpecificConsumption_LargeIonThruster_DrawsSevenPointEightJoulesPerNewtonSecond()
        {
            BandBurnRate rate = FuelBurn.SpecificConsumption(IonBand());

            Assert.True(rate.HasBurnData);
            Assert.True(string.IsNullOrEmpty(rate.FuelId));
            Assert.Equal(0.0, rate.GasLitresPerNewtonSecond);

            double joulesPerNewtonSecond = rate.ElectricMwhPerNewtonSecond * 3.6e9;
            Assert.InRange(joulesPerNewtonSecond, 7.8 * 0.99, 7.8 * 1.01);
        }

        // ---- (b) deep space: coasting is free -----------------------------------------------

        [Fact]
        public void ForLeg_DeepSpaceLeg_ChargesOneAccelerateAndBrakeWhateverTheDistance()
        {
            FactionTuning tuning = Tuning();
            StaticPlanetCatalogue planets = DeepSpace();
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = AllDirections(HydrogenBand());
            const double massKg = 1e6;

            LegImpulseProfile shortLeg = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(0, 0, 0), new Vec3(1000, 0, 0)));
            LegImpulseProfile longLeg = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(0, 0, 0), new Vec3(5000, 0, 0)));

            Assert.Equal(0.0, shortLeg.DownImpulsePerKg);
            Assert.Equal(1, shortLeg.ThrusterStretches);
            Assert.Equal(1, shortLeg.FsdEngagements);
            Assert.Equal(200.0, shortLeg.ThrustDeltaVMs, 6);   // 2 x 100 m/s, once
            Assert.Equal(200.0, longLeg.ThrustDeltaVMs, 6);

            LegBurn shortBurn = FuelBurn.ForLeg(shortLeg, massKg, bands, null, tuning);
            LegBurn longBurn = FuelBurn.ForLeg(longLeg, massKg, bands, null, tuning);

            Assert.True(shortBurn.HasBurnData);
            // 1e6 kg x 200 m/s x 6.69e-4 L/Ns — the plan's 134,000 L for a deep-space leg.
            Assert.InRange(Gas(shortBurn, HydrogenKey), 134000 * 0.99, 134000 * 1.01);
            Assert.Equal(Gas(shortBurn, HydrogenKey), Gas(longBurn, HydrogenKey), 6);

            // The FSD side does scale with distance, so the constant litres above is not a
            // profile that simply ignored its polyline.
            Assert.True(longBurn.ElectricMwh > shortBurn.ElectricMwh * 1.5);
        }

        // ---- (c) the FSD phase ---------------------------------------------------------------

        [Fact]
        public void Fsd_ThousandTonneHullAtSimCruise_DrawsTwentyMegawattsAndTwoMegawattHoursOverThreeThousandKm()
        {
            FactionTuning tuning = Tuning();
            const double massKg = 1e6;
            double speedKmPerS = tuning.FleetSpeedKmPerMin / 60.0;   // 8.333 km/s

            double activeMw = FuelBurn.FsdActivePowerMw(tuning, massKg, speedKmPerS);
            Assert.InRange(activeMw, 20.0 * 0.95, 20.0 * 1.05);

            double chargeMwh = FuelBurn.FsdChargeMwh(tuning, massKg);
            Assert.InRange(chargeMwh, 0.13 * 0.99, 0.13 * 1.01);     // 47 MW for 10 s

            LegImpulseProfile leg = LegImpulseProfile.Build(DeepSpace(), tuning,
                Line(new Vec3(0, 0, 0), new Vec3(3000, 0, 0)));
            Assert.Equal(3000.0, leg.FsdDistanceKm, 3);
            Assert.Equal(1, leg.FsdEngagements);

            double cruiseMwh = FuelBurn.FsdEnergyMwh(leg, massKg, tuning) - chargeMwh;
            Assert.InRange(cruiseMwh, 2.0 * 0.95, 2.0 * 1.05);
        }

        // ---- (d) hover inside a well ---------------------------------------------------------

        [Fact]
        public void Build_InWellPath_ChargesDownImpulseAndScalesLinearlyInMass()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });
            // The proxy reads exactly 1.0 anywhere at or inside the 600 km surface radius.
            Assert.Equal(1.0, planets.GravityAt(new Vec3(590, 0, 0)), 6);

            LegImpulseProfile twentyKm = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0)));
            LegImpulseProfile fortyKm = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, -20, 0), new Vec3(590, 20, 0)));

            Assert.Equal(1, twentyKm.ThrusterStretches);
            Assert.Equal(0, twentyKm.FsdEngagements);
            Assert.Equal(0.0, twentyKm.FsdDistanceKm, 6);
            Assert.Equal(1.0, twentyKm.RepresentativeG, 6);
            Assert.False(twentyKm.RepresentativeInAtmosphere);

            // g=1 held for 20 km: 9.81 x 20,000 of speed-free impulse.
            double brakeMs = LegImpulseProfile.ThrusterBrakeSpeedMs(tuning);
            double transitMs = LegImpulseProfile.InGravityTransitSpeedMs(tuning);
            Assert.Equal(9.81 * 20000.0, twentyKm.GravityImpulseIntegralPerKg, 3);

            // 20 km is shorter than the two manoeuvres at its ends (a 6.8 s launch at 100 m/s and a
            // 48.5 s descent at 420 = 21.0 km together), so the whole of it is ends: half at each.
            Assert.Equal(9.81 * 20000.0 * (0.5 / 100.0 + 0.5 / brakeMs), twentyKm.DownImpulsePerKg, 6);

            // 40 km is longer, so the 19 km it does not spend launching or descending is transit at
            // the world's grid limit -- and hover is therefore NOT linear in length. That is the
            // point of the split: pricing the crossing at the launch speed over-charges the legs
            // that dominate live traffic, which average 106 km at 87% hover.
            double accelEndM = LegImpulseProfile.LaunchEndSeconds * 100.0;
            double brakeEndM = LegImpulseProfile.ApproachEndSeconds * brakeMs;
            double inWellM = 40000.0;
            double expectedForty = 9.81 * 40000.0
                * (accelEndM / inWellM / 100.0
                   + brakeEndM / inWellM / brakeMs
                   + (1.0 - (accelEndM + brakeEndM) / inWellM) / transitMs);
            Assert.Equal(expectedForty, fortyKm.DownImpulsePerKg, 6);
            Assert.True(fortyKm.DownImpulsePerKg < 2 * twentyKm.DownImpulsePerKg,
                "the transiting half of the longer leg is flown faster, so it costs less hover than"
                + " doubling the shorter leg would: " + fortyKm.DownImpulsePerKg
                + " against " + (2 * twentyKm.DownImpulsePerKg));

            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = AllDirections(HydrogenBand());
            LegBurn light = FuelBurn.ForImpulse(bands, ThrustDirection.Down,
                1e6 * twentyKm.DownImpulsePerKg, twentyKm.RepresentativeG, false);
            LegBurn heavy = FuelBurn.ForImpulse(bands, ThrustDirection.Down,
                2e6 * twentyKm.DownImpulsePerKg, twentyKm.RepresentativeG, false);

            Assert.True(Gas(light, HydrogenKey) > 0);
            Assert.Equal(2 * Gas(light, HydrogenKey), Gas(heavy, HydrogenKey), 3);
        }

        [Fact]
        public void RetimeHover_ChargesTheMeasuredSeconds_AndLeavesAZeroGProfileAlone()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });

            // A 4 m hop at a 5 m/s peak derives 0.8 s of hover; the hull took 6 s over it.
            LegImpulseProfile hop = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, 0, 0), new Vec3(590, 0.004, 0)), true, 5.0, ThrusterStretchEnds.BrakeOnly);
            Assert.Equal(9.81 * 0.8, hop.DownImpulsePerKg, 3);
            double brakeBefore = hop.BrakeDeltaVMs;

            hop.RetimeHover(6.0);
            Assert.Equal(9.81 * 6.0, hop.DownImpulsePerKg, 3);
            Assert.Equal(brakeBefore, hop.BrakeDeltaVMs, 6);

            LegImpulseProfile deepSpace = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(90000, 0, 0), new Vec3(90000, 0.004, 0)), true, 5.0, ThrusterStretchEnds.BrakeOnly);
            deepSpace.RetimeHover(6.0);
            Assert.Equal(0.0, deepSpace.DownImpulsePerKg, 6);
        }

        // ---- one solved geometry serves every speed -------------------------------------------

        [Fact]
        public void AtSpeed_EqualsARebuildAtThatSpeed_SoTheCacheNeedNotBeKeyedOnSpeed()
        {
            // The claim the seam rests on: rescaling is exact, not an approximation. If this is
            // wrong, a fleet-speed model silently mis-prices every cached leg.
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });
            // A path with a vertex inside the well, so the turn term is exercised too.
            var poly = new List<Vec3> { new Vec3(590, -20, 0), new Vec3(590, 0, 8), new Vec3(590, 20, 0) };

            LegImpulseProfile at100 = LegImpulseProfile.Build(planets, tuning, poly, true, 100.0,
                                                              ThrusterStretchEnds.AccelerateAndBrake);
            Assert.Equal(100.0, at100.AccelerateSpeedMs, 6);
            Assert.Equal(100.0, at100.BrakeSpeedMs, 6);

            foreach (double speed in new[] { 25.0, 60.0, 250.0, 424.6 })
            {
                LegImpulseProfile rebuilt = LegImpulseProfile.Build(planets, tuning, poly, true, speed,
                                                                    ThrusterStretchEnds.AccelerateAndBrake);
                LegImpulseProfile rescaled = at100.AtSpeed(speed);

                Assert.Equal(rebuilt.DownImpulsePerKg, rescaled.DownImpulsePerKg, 6);
                Assert.Equal(rebuilt.AccelerateDeltaVMs, rescaled.AccelerateDeltaVMs, 6);
                Assert.Equal(rebuilt.BrakeDeltaVMs, rescaled.BrakeDeltaVMs, 6);
                Assert.Equal(rebuilt.ThrustDeltaVMs, rescaled.ThrustDeltaVMs, 6);
                Assert.Equal(rebuilt.RepresentativeG, rescaled.RepresentativeG, 9);
                Assert.Equal(rebuilt.ThrusterStretches, rescaled.ThrusterStretches);
                Assert.Equal(rebuilt.FsdDistanceKm, rescaled.FsdDistanceKm, 9);
                Assert.Equal(speed, rescaled.AccelerateSpeedMs, 6);
            }

            // Halving the speed doubles the hover impulse and halves the delta-v: the two terms move
            // in opposite directions, which is why burn is not monotonic in speed.
            LegImpulseProfile half = at100.AtSpeed(50.0);
            Assert.Equal(2.0 * at100.DownImpulsePerKg, half.DownImpulsePerKg, 6);
            Assert.Equal(0.5 * at100.ThrustDeltaVMs, half.ThrustDeltaVMs, 6);
            // and the original is untouched
            Assert.Equal(100.0, at100.AccelerateSpeedMs, 6);
        }

        [Fact]
        public void AtSpeed_WillNotRescaleAMeasuredHover()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });
            LegImpulseProfile p = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0)), true, 100.0, ThrusterStretchEnds.BrakeOnly);

            p.RetimeHover(40.0);
            double measured = p.DownImpulsePerKg;
            Assert.Equal(9.81 * 40.0, measured, 3);

            Assert.Same(p, p.AtSpeed(25.0));
            Assert.Equal(measured, p.DownImpulsePerKg, 6);
        }

        [Fact]
        public void FsdEnergy_AtAGivenCruiseSpeed_OverridesTheTuningDefault()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            LegImpulseProfile deep = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(0, 0, 0), new Vec3(3000, 0, 0)));

            double atDefault = FuelBurn.FsdEnergyMwh(deep, 1000000, tuning);
            double explicitDefault = FuelBurn.FsdEnergyMwh(deep, 1000000, tuning, tuning.FleetSpeedKmPerMin);
            Assert.Equal(atDefault, explicitDefault, 9);
            Assert.Equal(atDefault, FuelBurn.FsdEnergyMwh(deep, 1000000, tuning, 0), 9);

            // Which way a speed cap moves FSD energy depends on the hull's mass, and both directions
            // are asserted so neither can be dropped. Active draw carries a m*v^2 term over a
            // mass-independent 27 MW floor, while the seconds go as 1/v. On a heavy hull the v^2
            // term dominates and flying slower is CHEAPER; on a light hull the floor dominates and
            // the extra seconds make slower DEARER.
            double heavyHalf = FuelBurn.FsdEnergyMwh(deep, 1000000, tuning, tuning.FleetSpeedKmPerMin * 0.5);
            Assert.True(heavyHalf < atDefault, "heavyHalf=" + heavyHalf + " atDefault=" + atDefault);

            double lightDefault = FuelBurn.FsdEnergyMwh(deep, 1000, tuning);
            double lightHalf = FuelBurn.FsdEnergyMwh(deep, 1000, tuning, tuning.FleetSpeedKmPerMin * 0.5);
            Assert.True(lightHalf > lightDefault, "lightHalf=" + lightHalf + " lightDefault=" + lightDefault);
        }

        [Fact]
        public void TwoSpeeds_ChargeHoverHalfAtEachEnd_AndDeltaVAtItsOwnEnd()
        {
            // The defect this fixes: one speed for the whole well over-charged the descent, because
            // hover goes as 1/v and a descent flies about 4x a launch.
            FactionTuning tuning = Tuning();
            tuning.ThrusterCruiseSpeedMs = 100.0;
            tuning.ThrusterApproachSpeedMs = 400.0;
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });

            LegImpulseProfile split = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0)), true);
            Assert.Equal(100.0, split.AccelerateSpeedMs, 6);
            Assert.Equal(400.0, split.BrakeSpeedMs, 6);

            // Half the length at each speed: the integral is 9.81 x 20 km, so hover is
            // I x (0.5/100 + 0.5/400), strictly between pricing it all at 100 and all at 400.
            double integral = split.GravityImpulseIntegralPerKg;
            Assert.Equal(9.81 * 20000.0, integral, 3);
            Assert.Equal(integral * (0.5 / 100.0 + 0.5 / 400.0), split.DownImpulsePerKg, 6);

            LegImpulseProfile allSlow = split.WithSpeeds(100.0, 100.0);
            LegImpulseProfile allFast = split.WithSpeeds(400.0, 400.0);
            Assert.True(split.DownImpulsePerKg < allSlow.DownImpulsePerKg);
            Assert.True(split.DownImpulsePerKg > allFast.DownImpulsePerKg);
            // The old single-speed model is what allSlow is, and it over-charged hover by 1.6x here.
            Assert.Equal(1.6, allSlow.DownImpulsePerKg / split.DownImpulsePerKg, 6);

            // Each end pays its own speed in delta-v, so the brake end is the dearer one.
            Assert.Equal(100.0, split.AccelerateDeltaVMs, 6);
            Assert.Equal(400.0, split.BrakeDeltaVMs, 6);
        }

        [Fact]
        public void AOneEndedStretch_SpendsAllItsHoverAtThatEndsSpeed()
        {
            FactionTuning tuning = Tuning();
            tuning.ThrusterCruiseSpeedMs = 100.0;
            tuning.ThrusterApproachSpeedMs = 400.0;
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });
            var line = Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0));

            LegImpulseProfile launch = LegImpulseProfile.Build(planets, tuning, line, true, 0,
                                                               ThrusterStretchEnds.AccelerateOnly);
            LegImpulseProfile approach = LegImpulseProfile.Build(planets, tuning, line, true, 0,
                                                                 ThrusterStretchEnds.BrakeOnly);
            double integral = launch.GravityImpulseIntegralPerKg;

            Assert.Equal(integral / 100.0, launch.DownImpulsePerKg, 6);
            Assert.Equal(integral / 400.0, approach.DownImpulsePerKg, 6);
            Assert.Equal(0.0, launch.BrakeDeltaVMs, 6);
            Assert.Equal(0.0, approach.AccelerateDeltaVMs, 6);
            // A descent holds itself up for a quarter as long as a climb over the same ground.
            Assert.Equal(4.0, launch.DownImpulsePerKg / approach.DownImpulsePerKg, 6);
        }

        [Fact]
        public void AnExplicitSpeed_GovernsBothEnds_SoTheCalibrationLineIsUnchanged()
        {
            // FUEL_OBSERVED prices a manoeuvre at its own observed peak. That must not be
            // reinterpreted as "the launch end only" now that the tuning carries two speeds.
            FactionTuning tuning = Tuning();
            tuning.ThrusterCruiseSpeedMs = 100.0;
            tuning.ThrusterApproachSpeedMs = 400.0;
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });

            LegImpulseProfile observed = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0)), true, 424.6,
                ThrusterStretchEnds.BrakeOnly);

            Assert.Equal(424.6, observed.AccelerateSpeedMs, 6);
            Assert.Equal(424.6, observed.BrakeSpeedMs, 6);
            Assert.Equal(observed.GravityImpulseIntegralPerKg / 424.6, observed.DownImpulsePerKg, 6);
            Assert.Equal(424.6, observed.BrakeDeltaVMs, 6);
        }

        // ---- impulse is split across a direction's bands by the force each delivers ---------

        [Fact]
        public void ForImpulse_SplitsAcrossBands_InProportionToTheForceEachDeliversHere()
        {
            var mixed = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            mixed[ThrustDirection.Backward] = new List<ThrustBand> { HydrogenBand(), IonBand() };

            // One second of both bands at full thrust, in deep space where each is fully effective.
            LegBurn burn = FuelBurn.ForImpulse(mixed, ThrustDirection.Backward, 7.2e6 + 4.32e6, 0, false);

            Assert.True(burn.HasBurnData);
            Assert.InRange(Gas(burn, HydrogenKey), 4820 * 0.99, 4820 * 1.01);           // the hydrogen band's second
            Assert.InRange(burn.ElectricMwh, 33.6 / 3600 * 0.99, 33.6 / 3600 * 1.01);   // the ion band's second
        }

        [Fact]
        public void ForImpulse_InSpace_GivesAnAtmosphericBandNoShareOfTheImpulse()
        {
            var atmospheric = new ThrustBand();
            atmospheric.ThrusterType = "Atmospheric";
            atmospheric.ForceMagnitudeN = 6.48e6f;
            atmospheric.MinPlanetaryInfluence = 0;
            atmospheric.MaxPlanetaryInfluence = 1;
            atmospheric.EffectivenessAtMinInfluence = 0;
            atmospheric.EffectivenessAtMaxInfluence = 1;
            atmospheric.NeedsAtmosphereForInfluence = true;
            atmospheric.MaxPowerMw = 16.8f;

            var mixed = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            mixed[ThrustDirection.Backward] = new List<ThrustBand> { HydrogenBand(), atmospheric };

            LegBurn burn = FuelBurn.ForImpulse(mixed, ThrustDirection.Backward, 7.2e6, 0, false);

            // The dead atmospheric band takes none of it, so the hydrogen band pays for all of it.
            Assert.InRange(Gas(burn, HydrogenKey), 4820 * 0.99, 4820 * 1.01);
            Assert.Equal(0.0, burn.ElectricMwh);
        }

        // ---- (e) an unscanned band burns nothing and says so ---------------------------------

        [Fact]
        public void ZeroPowerBand_IsUnknownAndBurnsNothing()
        {
            ThrustBand unscanned = HydrogenBand();
            unscanned.MaxPowerMw = 0;

            BandBurnRate rate = FuelBurn.SpecificConsumption(unscanned);
            Assert.False(rate.HasBurnData);
            Assert.Equal(0.0, rate.GasLitresPerNewtonSecond);
            Assert.Equal(0.0, rate.ElectricMwhPerNewtonSecond);

            LegBurn burn = FuelBurn.ForImpulse(AllDirections(unscanned), ThrustDirection.Backward,
                1e8, 0, false);
            Assert.False(burn.HasBurnData);
            Assert.Empty(burn.GasLitres);
            Assert.Equal(0.0, burn.ElectricMwh);
        }

        // ---- lift and thrust on the hull's own frame (FAC-423) --------------------------------

        /// <summary>A profile with one term set, so each test isolates the bucket it is about.</summary>
        private static LegImpulseProfile Profile(double hoverPerKg, double accelerateMs, double brakeMs,
                                                 double g, bool inAtmosphere)
        {
            return new LegImpulseProfile
            {
                DownImpulsePerKg = hoverPerKg,
                AccelerateDeltaVMs = accelerateMs,
                BrakeDeltaVMs = brakeMs,
                RepresentativeG = g,
                RepresentativeInAtmosphere = inAtmosphere,
                // These fixtures are surface legs (full air) or vacuum.
                RepresentativeAirDensity = inAtmosphere ? g : 0,
            };
        }

        private static ThrustBand AtmosphericBand()
        {
            var band = new ThrustBand();
            band.ThrusterType = "Atmospheric";
            band.ForceMagnitudeN = 6.48e6f;
            band.MinPlanetaryInfluence = 0;
            band.MaxPlanetaryInfluence = 1;
            band.EffectivenessAtMinInfluence = 0;
            band.EffectivenessAtMaxInfluence = 1;
            band.NeedsAtmosphereForInfluence = true;
            band.MaxPowerMw = 16.8f;
            band.FuelId = null;
            band.FuelEfficiency = 1f;
            return band;
        }

        /// <summary>Lifts on grid-Right, as the Naginata does: its top is grid-Left.</summary>
        private static HullFrame SideLifter()
        {
            return new HullFrame { Nose = ThrustDirection.Forward, Up = ThrustDirection.Left, Known = true };
        }

        [Fact]
        public void ForLeg_AHullThatLiftsOnItsSide_IsBilledOnThatSide()
        {
            // Hydrogen on grid-Right only, nothing on Down. Billed on Down, the hover found no
            // thruster and cost nothing.
            var bands = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Right, new List<ThrustBand> { HydrogenBand() } },
            };
            LegImpulseProfile hover = Profile(9.81 * 30, 0, 0, 1.0, false);

            LegBurn asDown = FuelBurn.ForLeg(hover, 1e6, bands, null, Tuning());
            LegBurn asFramed = FuelBurn.ForLeg(hover, 1e6, bands, null, Tuning(), SideLifter());

            Assert.Equal(0.0, asDown.HoverGasLitres);
            Assert.True(asFramed.HoverGasLitres > 0);
        }

        [Fact]
        public void ForLeg_AccelerationAndBrakingFollowTheNose()
        {
            // Nose up: accelerating pushes along Up, so the thrusters mounted facing Down do it,
            // and braking uses those facing Up.
            var bands = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Down, new List<ThrustBand> { HydrogenBand() } },
                { ThrustDirection.Up, new List<ThrustBand> { HydrogenBand() } },
            };
            var noseUp = new HullFrame { Nose = ThrustDirection.Up, Up = ThrustDirection.Backward, Known = true };
            LegImpulseProfile dv = Profile(0, 100, 100, 0, false);

            LegBurn framed = FuelBurn.ForLeg(dv, 1e6, bands, null, Tuning(), noseUp);
            LegBurn unframed = FuelBurn.ForLeg(dv, 1e6, bands, null, Tuning());

            Assert.True(framed.DeltaVGasLitres > 0);
            Assert.Equal(0.0, unframed.DeltaVGasLitres);    // Backward/Forward hold nothing
        }

        [Fact]
        public void ForLeg_AnUnknownFrame_BillsExactlyAsBefore()
        {
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = AllDirections(HydrogenBand());
            LegImpulseProfile leg = Profile(9.81 * 20, 100, 420, 1.0, false);
            var bellyDown = new HullFrame { Nose = ThrustDirection.Forward, Up = ThrustDirection.Up, Known = true };

            double none = Gas(FuelBurn.ForLeg(leg, 1e6, bands, null, Tuning()), HydrogenKey);
            double unknown = Gas(FuelBurn.ForLeg(leg, 1e6, bands, null, Tuning(), HullFrame.Unknown), HydrogenKey);
            double known = Gas(FuelBurn.ForLeg(leg, 1e6, bands, null, Tuning(), bellyDown), HydrogenKey);

            Assert.True(none > 0);
            Assert.Equal(none, unknown, 6);
            Assert.Equal(none, known, 6);
        }

        [Fact]
        public void ForLeg_AtmosphericLiftOnTheLiftSide_TakesItsShareOfTheHover()
        {
            // In an atmosphere the side's atmospheric thrusters carry part of the lift, so less of it
            // is hydrogen than on a hull with hydrogen alone.
            var hydrogenOnly = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Right, new List<ThrustBand> { HydrogenBand() } },
            };
            var mixed = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Right, new List<ThrustBand> { HydrogenBand(), AtmosphericBand() } },
            };
            LegImpulseProfile hover = Profile(9.81 * 30, 0, 0, 1.0, true);

            double alone = Gas(FuelBurn.ForLeg(hover, 1e6, hydrogenOnly, null, Tuning(), SideLifter()), HydrogenKey);
            double shared = Gas(FuelBurn.ForLeg(hover, 1e6, mixed, null, Tuning(), SideLifter()), HydrogenKey);

            Assert.True(alone > 0);
            Assert.True(shared < alone * 0.6);             // 6.48 MN of the 13.68 MN is atmospheric
        }

        [Fact]
        public void ForLeg_ThinAirHighInTheWell_SharesTheHoverByAirNotGravity()
        {
            // 3.65 km up a 4.86 km atmosphere: gravity 0.6 g, air 0.25. The atmospheric bank pushes at
            // the air's 25%, so hydrogen carries H / (H + 0.25 A) of the hover, not H / (H + 0.6 A).
            var hydrogenOnly = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Right, new List<ThrustBand> { HydrogenBand() } },
            };
            var mixed = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Right, new List<ThrustBand> { HydrogenBand(), AtmosphericBand() } },
            };
            var thinAir = new LegImpulseProfile
            {
                DownImpulsePerKg = 9.81 * 30,
                RepresentativeG = 0.6,
                RepresentativeInAtmosphere = true,
                RepresentativeAirDensity = 0.25,
            };

            double alone = Gas(FuelBurn.ForLeg(thinAir, 1e6, hydrogenOnly, null, Tuning(), SideLifter()), HydrogenKey);
            double shared = Gas(FuelBurn.ForLeg(thinAir, 1e6, mixed, null, Tuning(), SideLifter()), HydrogenKey);

            Assert.True(alone > 0);
            Assert.Equal(7.2e6 / (7.2e6 + 6.48e6 * 0.25), shared / alone, 3);
        }

        // ---- (f) battery first, then reactor, never a gate -----------------------------------

        [Fact]
        public void ForLeg_ElectricDemand_TakesBatteryFirstThenReactorFuelAndLeavesTheRestUncovered()
        {
            FactionTuning tuning = Tuning();
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = AllDirections(IonBand());
            LegImpulseProfile leg = LegImpulseProfile.Build(DeepSpace(), tuning,
                Line(new Vec3(0, 0, 0), new Vec3(1000, 0, 0)));
            const double massKg = 1e6;

            LegBurn unknownHull = FuelBurn.ForLeg(leg, massKg, bands, null, tuning);
            double totalMwh = unknownHull.ElectricMwh;
            Assert.True(totalMwh > 0.5);                    // thrusters plus FSD, not a vacuous zero
            Assert.Equal(0.0, unknownHull.BatteryMwh);
            Assert.Equal(0.0, unknownHull.ReactorFuelKg);

            var bigBattery = new FuelManifest();
            bigBattery.BatteryMwh = 100;
            bigBattery.HasFuelData = true;
            LegBurn covered = FuelBurn.ForLeg(leg, massKg, bands, bigBattery, tuning);
            Assert.Equal(totalMwh, covered.BatteryMwh, 6);
            Assert.Equal(0.0, covered.ElectricMwh);
            Assert.Equal(0.0, covered.ReactorFuelKg);

            var reactorHull = new FuelManifest();
            reactorHull.BatteryMwh = 0.1;
            reactorHull.ReactorFuelKey = "Ingot:Uranium";
            reactorHull.ReactorFuelCapacityKg = 500;
            reactorHull.HasFuelData = true;
            LegBurn split = FuelBurn.ForLeg(leg, massKg, bands, reactorHull, tuning);
            Assert.Equal(0.1, split.BatteryMwh, 6);
            Assert.Equal("Ingot:Uranium", split.ReactorFuelKey);
            Assert.Equal(totalMwh - 0.1, split.ReactorFuelKg, 6);   // 1 kg per MWh
            Assert.Equal(0.0, split.ElectricMwh);

            var batteryOnly = new FuelManifest();
            batteryOnly.BatteryMwh = 0.1;
            batteryOnly.HasFuelData = true;
            LegBurn shortfall = FuelBurn.ForLeg(leg, massKg, bands, batteryOnly, tuning);
            Assert.Equal(0.1, shortfall.BatteryMwh, 6);
            Assert.Equal(0.0, shortfall.ReactorFuelKg);
            Assert.Null(shortfall.ReactorFuelKey);
            Assert.Equal(totalMwh - 0.1, shortfall.ElectricMwh, 6); // uncovered, not refused
        }

        // ---- (g) the scale ------------------------------------------------------------------

        [Fact]
        public void FuelBurnScale_MultipliesEveryQuantity()
        {
            FactionTuning single = Tuning();
            FactionTuning doubled = Tuning();
            doubled.FuelBurnScale = 2.0;

            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands =
                AllDirections(HydrogenBand(), IonBand());
            LegImpulseProfile leg = LegImpulseProfile.Build(DeepSpace(), single,
                Line(new Vec3(0, 0, 0), new Vec3(1000, 0, 0)));
            const double massKg = 1e6;

            var batteryOnly = new FuelManifest();
            batteryOnly.BatteryMwh = 0.1;
            batteryOnly.HasFuelData = true;
            LegBurn one = FuelBurn.ForLeg(leg, massKg, bands, batteryOnly, single);
            LegBurn two = FuelBurn.ForLeg(leg, massKg, bands, batteryOnly, doubled);

            Assert.True(Gas(one, HydrogenKey) > 0);
            Assert.True(one.ElectricMwh > 0);
            Assert.True(one.BatteryMwh > 0);
            Assert.Equal(2 * Gas(one, HydrogenKey), Gas(two, HydrogenKey), 3);
            // ElectricMwh is the UNCOVERED remainder after the battery clamp, so the doubling is
            // asserted on the total electric demand; the battery is a physical bound that clamps
            // at capacity whatever the scale, and the scaled remainder is what stays uncovered.
            double oneTotalMwh = one.ElectricMwh + one.BatteryMwh;
            double twoTotalMwh = two.ElectricMwh + two.BatteryMwh;
            Assert.Equal(2 * oneTotalMwh, twoTotalMwh, 6);
            Assert.Equal(Math.Min(2 * oneTotalMwh, batteryOnly.BatteryMwh), two.BatteryMwh, 6);

            var reactorHull = new FuelManifest();
            reactorHull.BatteryMwh = 0.1;
            reactorHull.ReactorFuelKey = "Ingot:Uranium";
            reactorHull.HasFuelData = true;
            LegBurn oneReactor = FuelBurn.ForLeg(leg, massKg, bands, reactorHull, single);
            LegBurn twoReactor = FuelBurn.ForLeg(leg, massKg, bands, reactorHull, doubled);
            Assert.True(oneReactor.ReactorFuelKg > 0);
            // Same shape: the reactor takes whatever the clamped battery leaves of the doubled total.
            double oneReactorTotalMwh = oneReactor.BatteryMwh + oneReactor.ReactorFuelKg / single.ReactorFuelKgPerMwh;
            double twoReactorExpectedKg = (2 * oneReactorTotalMwh - twoReactor.BatteryMwh) * doubled.ReactorFuelKgPerMwh;
            Assert.Equal(twoReactorExpectedKg, twoReactor.ReactorFuelKg, 6);
        }

        // ---- (h) the boundary -----------------------------------------------------------------

        [Fact]
        public void GravityBoundaryRadius_SitsBetweenSurfaceAndGravityRadius_AndSplitsTheWellFromTheFsdStretch()
        {
            FactionTuning tuning = Tuning();
            PlanetRecord planet = TestPlanet("P", 0);
            var planets = new StaticPlanetCatalogue(new[] { planet });

            double boundaryKm = PlanetGeometry.GravityBoundaryRadius(planets, planet,
                tuning.FsdGravityDropoutG, new Vec3(1, 0, 0));
            Assert.True(boundaryKm > planet.SurfaceRadius, "boundary at " + boundaryKm);
            Assert.True(boundaryKm < planet.GravityRadius, "boundary at " + boundaryKm);

            // Deep inside the well: half the proxy's surface reading, so thrusters.
            Assert.Equal(0.5, planets.GravityAt(new Vec3(800, 0, 0)), 3);
            LegImpulseProfile inWell = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(800, -5, 0), new Vec3(800, 5, 0)));
            Assert.Equal(1, inWell.ThrusterStretches);
            Assert.Equal(0, inWell.FsdEngagements);
            Assert.Equal(0.0, inWell.FsdDistanceKm, 6);
            Assert.True(inWell.DownImpulsePerKg > 0);

            // Still inside the body's gravity limit, but below the dropout: the FSD runs there.
            Assert.Equal(0.001, planets.GravityAt(new Vec3(999.6, 0, 0)), 5);
            LegImpulseProfile outsideBoundary = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(999.6, -1, 0), new Vec3(999.6, 1, 0)));
            Assert.Equal(1, outsideBoundary.FsdEngagements);
            Assert.Equal(0.0, outsideBoundary.DownImpulsePerKg, 6);
            Assert.Equal(0.0, outsideBoundary.ThrusterDistanceKm, 6);
        }

        // ---- turns and stretch counting --------------------------------------------------------

        [Fact]
        public void Build_TurnInsideAWell_ChargesTheTurnDeltaV_ButAStraightPathDoesNot()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });

            var straight = new List<Vec3>();
            straight.Add(new Vec3(590, -20, 0));
            straight.Add(new Vec3(590, 0, 0));
            straight.Add(new Vec3(590, 20, 0));

            var rightAngle = new List<Vec3>();
            rightAngle.Add(new Vec3(590, -20, 0));
            rightAngle.Add(new Vec3(590, 0, 0));
            rightAngle.Add(new Vec3(590, 0, 20));

            LegImpulseProfile straightLeg = LegImpulseProfile.Build(planets, tuning, straight);
            LegImpulseProfile turningLeg = LegImpulseProfile.Build(planets, tuning, rightAngle);

            double brakeMs = LegImpulseProfile.ThrusterBrakeSpeedMs(tuning);
            double ends = 100.0 + brakeMs;                                       // accelerate + brake only
            Assert.Equal(ends, straightLeg.ThrustDeltaVMs, 6);
            // A turn is half an accelerate and half a brake, so it too costs one of each.
            Assert.Equal(ends + (100.0 + brakeMs) * Math.Sin(Math.PI / 4), turningLeg.ThrustDeltaVMs, 6);
        }

        [Fact]
        public void Build_WellToWellLeg_CountsTwoThrusterStretchesAndOneFsdEngagement()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("A", 0), TestPlanet("B", 10000) });

            LegImpulseProfile leg = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, 0, 0), new Vec3(9410, 0, 0)));

            Assert.Equal(2, leg.ThrusterStretches);
            Assert.Equal(1, leg.FsdEngagements);
            Assert.True(leg.FsdDistanceKm > 7000);
            Assert.True(leg.ThrusterDistanceKm > 0);
            Assert.True(leg.DownImpulsePerKg > 0);
            // Two stretches, each accelerating at the launch speed and braking at the approach one.
            Assert.Equal(2 * (100.0 + LegImpulseProfile.ThrusterBrakeSpeedMs(tuning)), leg.ThrustDeltaVMs, 6);
        }

        [Fact]
        public void Build_DegeneratePolyline_ReportsNoGeometryRatherThanAFreeLeg()
        {
            FactionTuning tuning = Tuning();
            var single = new List<Vec3>();
            single.Add(new Vec3(0, 0, 0));

            LegImpulseProfile profile = LegImpulseProfile.Build(DeepSpace(), tuning, single);
            Assert.False(profile.HasGeometry);
            Assert.Equal(0, profile.ThrusterStretches);
            Assert.Equal(0.0, profile.ThrustDeltaVMs, 6);

            LegBurn burn = FuelBurn.ForLeg(profile, 1e6, AllDirections(HydrogenBand()), null, tuning);
            Assert.False(burn.HasBurnData);
        }
        // ---- (i) thrusters only: the manoeuvre a materialised hull actually flies ---------------

        [Fact]
        public void Build_ThrustersOnly_DeepSpaceLeg_AttributesEveryKilometreToThrustersAndNothingToTheFsd()
        {
            FactionTuning tuning = Tuning();
            StaticPlanetCatalogue planets = DeepSpace();
            List<Vec3> path = Line(new Vec3(0, 0, 0), new Vec3(1000, 0, 0));

            LegImpulseProfile withDrive = LegImpulseProfile.Build(planets, tuning, path);
            LegImpulseProfile thrustersOnly = LegImpulseProfile.Build(planets, tuning, path, true);

            Assert.Equal(1000.0, withDrive.FsdDistanceKm, 3);
            Assert.Equal(1, withDrive.FsdEngagements);

            Assert.Equal(0.0, thrustersOnly.FsdDistanceKm, 6);
            Assert.Equal(0, thrustersOnly.FsdEngagements);
            Assert.Equal(1000.0, thrustersOnly.ThrusterDistanceKm, 3);
            Assert.Equal(1, thrustersOnly.ThrusterStretches);
            // Zero g still costs no lift impulse, and the ends still cost their delta-v.
            Assert.Equal(0.0, thrustersOnly.DownImpulsePerKg, 6);
            Assert.Equal(200.0, thrustersOnly.ThrustDeltaVMs, 6);

            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = AllDirections(HydrogenBand());
            LegBurn driveBurn = FuelBurn.ForLeg(withDrive, 1e6, bands, null, tuning);
            LegBurn thrusterBurn = FuelBurn.ForLeg(thrustersOnly, 1e6, bands, null, tuning);

            Assert.True(driveBurn.ElectricMwh > 0);            // the cruise the abstract fleet pays for
            Assert.Equal(0.0, thrusterBurn.ElectricMwh, 9);    // a hull with no drive block pays none of it
            Assert.Equal(Gas(driveBurn, HydrogenKey), Gas(thrusterBurn, HydrogenKey), 6);
        }

        [Fact]
        public void Build_ThrustersOnly_InsideAWell_ChargesTheSameHoverImpulseAsTheDefaultWalk()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });
            List<Vec3> path = Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0));

            LegImpulseProfile withDrive = LegImpulseProfile.Build(planets, tuning, path);
            LegImpulseProfile thrustersOnly = LegImpulseProfile.Build(planets, tuning, path, true);

            Assert.True(withDrive.DownImpulsePerKg > 0);
            Assert.Equal(withDrive.DownImpulsePerKg, thrustersOnly.DownImpulsePerKg, 6);
            Assert.Equal(withDrive.RepresentativeG, thrustersOnly.RepresentativeG, 6);
            Assert.Equal(withDrive.ThrustDeltaVMs, thrustersOnly.ThrustDeltaVMs, 6);
        }

        // ---- (j) the thruster clock ------------------------------------------------------------

        /// <summary>The two thruster terms answer to <c>ThrusterCruiseSpeedMs</c> in opposite
        /// directions: hover is seconds in the well, so it falls as the speed rises, while Δv is
        /// charged at the speed and rises with it. Neither reads the world's grid speed limit.</summary>
        [Fact]
        public void Build_ThrusterSpeed_ScalesHoverInverselyAndDeltaVLinearly()
        {
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });
            List<Vec3> path = Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0));

            FactionTuning slow = Tuning();                  // 100 m/s
            FactionTuning fast = Tuning();
            fast.ThrusterCruiseSpeedMs = 500.0;             // five times faster
            fast.ThrusterApproachSpeedMs = slow.ThrusterApproachSpeedMs * 5.0;  // both ends, so the law is visible

            LegImpulseProfile atHundred = LegImpulseProfile.Build(planets, slow, path);
            LegImpulseProfile atFiveHundred = LegImpulseProfile.Build(planets, fast, path);

            // g=1 held for 20 km is 9.81 x 20,000 of speed-free impulse, spent half at each end's
            // speed. Five times the speed at both ends is a fifth of the hover and five times the Δv.
            double integral = 9.81 * 20000.0;
            double brake = LegImpulseProfile.ThrusterBrakeSpeedMs(slow);
            Assert.Equal(integral * (0.5 / 100.0 + 0.5 / brake), atHundred.DownImpulsePerKg, 6);
            Assert.Equal(atHundred.DownImpulsePerKg / 5.0, atFiveHundred.DownImpulsePerKg, 6);

            Assert.Equal(100.0 + brake, atHundred.ThrustDeltaVMs, 6);
            Assert.Equal(5.0 * atHundred.ThrustDeltaVMs, atFiveHundred.ThrustDeltaVMs, 6);

            // The world's grid speed limit is not an input to either term.
            FactionTuning fastWorld = Tuning();
            fastWorld.FleetSpeedMinKmPerMin = 300.0;
            LegImpulseProfile unmoved = LegImpulseProfile.Build(planets, fastWorld, path);
            Assert.Equal(atHundred.DownImpulsePerKg, unmoved.DownImpulsePerKg, 6);
            Assert.Equal(atHundred.ThrustDeltaVMs, unmoved.ThrustDeltaVMs, 6);
        }

        /// <summary>The calibration overload: a launch accelerates and never brakes, an approach
        /// engages on a moving grid and only brakes, and each charge lands on its own direction's
        /// bands. The explicit speed overrides the tuning field; 0 falls back to it.</summary>
        [Fact]
        public void Build_OneEndedStretch_ChargesHalfTheDeltaV_AndPutsItOnThatDirectionsBands()
        {
            FactionTuning tuning = Tuning();
            StaticPlanetCatalogue planets = DeepSpace();
            List<Vec3> path = Line(new Vec3(0, 0, 0), new Vec3(10, 0, 0));

            LegImpulseProfile both = LegImpulseProfile.Build(planets, tuning, path, true, 0,
                                                             ThrusterStretchEnds.AccelerateAndBrake);
            LegImpulseProfile launch = LegImpulseProfile.Build(planets, tuning, path, true, 0,
                                                               ThrusterStretchEnds.AccelerateOnly);
            LegImpulseProfile approach = LegImpulseProfile.Build(planets, tuning, path, true, 0,
                                                                 ThrusterStretchEnds.BrakeOnly);

            Assert.Equal(200.0, both.ThrustDeltaVMs, 6);
            Assert.Equal(100.0, both.AccelerateDeltaVMs, 6);
            Assert.Equal(100.0, both.BrakeDeltaVMs, 6);

            Assert.Equal(100.0, launch.ThrustDeltaVMs, 6);
            Assert.Equal(100.0, launch.AccelerateDeltaVMs, 6);
            Assert.Equal(0.0, launch.BrakeDeltaVMs, 6);

            Assert.Equal(100.0, approach.ThrustDeltaVMs, 6);
            Assert.Equal(0.0, approach.AccelerateDeltaVMs, 6);
            Assert.Equal(100.0, approach.BrakeDeltaVMs, 6);

            // Only the Backward bands burn for a launch, and only the Forward bands for an approach.
            var backwardOnly = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            backwardOnly[ThrustDirection.Backward] = new List<ThrustBand> { HydrogenBand() };
            const double massKg = 1e6;

            LegBurn launchBurn = FuelBurn.ForLeg(launch, massKg, backwardOnly, null, tuning);
            LegBurn approachBurn = FuelBurn.ForLeg(approach, massKg, backwardOnly, null, tuning);

            // 1e6 kg x 100 m/s x 6.69e-4 L/Ns: half the two-ended deep-space leg's 134,000 L.
            Assert.InRange(Gas(launchBurn, HydrogenKey), 67000 * 0.99, 67000 * 1.01);
            Assert.Equal(Gas(launchBurn, HydrogenKey), launchBurn.DeltaVGasLitres, 6);
            Assert.Equal(0.0, launchBurn.HoverGasLitres, 6);
            Assert.Equal(0.0, Gas(approachBurn, HydrogenKey), 6);   // it has no Forward bands to brake with

            // The explicit speed is what the ends are charged at; 0 means the tuning field.
            LegImpulseProfile atPeak = LegImpulseProfile.Build(planets, tuning, path, true, 250.0,
                                                               ThrusterStretchEnds.AccelerateOnly);
            Assert.Equal(250.0, atPeak.AccelerateDeltaVMs, 6);
            Assert.Equal(tuning.ThrusterCruiseSpeedMs, launch.AccelerateDeltaVMs, 6);
        }

        /// <summary>The hover and Δv halves of the gas total, which the calibration line prints so a
        /// reading that misses can say which term is wrong.</summary>
        [Fact]
        public void ForLeg_SplitsGasIntoHoverAndDeltaV_WhichSumToTheTotal()
        {
            FactionTuning tuning = Tuning();
            var planets = new StaticPlanetCatalogue(new[] { TestPlanet("P", 0) });
            LegImpulseProfile leg = LegImpulseProfile.Build(planets, tuning,
                Line(new Vec3(590, -10, 0), new Vec3(590, 10, 0)));
            const double massKg = 1e6;

            LegBurn burn = FuelBurn.ForLeg(leg, massKg, AllDirections(HydrogenBand()), null, tuning);

            Assert.True(burn.HoverGasLitres > 0);
            Assert.True(burn.DeltaVGasLitres > 0);
            Assert.Equal(Gas(burn, HydrogenKey), burn.HoverGasLitres + burn.DeltaVGasLitres, 3);

            // Hover still dominates in a well, but by about 2.3x rather than the 10x the
            // single-speed model reported: charging the descent half at the approach speed cuts
            // hover and raises delta-v at the same time. Bounded both ways so neither a return to
            // the old model nor a collapse of the hover term passes.
            Assert.True(burn.HoverGasLitres > burn.DeltaVGasLitres * 2.0,
                        "hover=" + burn.HoverGasLitres + " dv=" + burn.DeltaVGasLitres);
            Assert.True(burn.HoverGasLitres < burn.DeltaVGasLitres * 3.0,
                        "hover=" + burn.HoverGasLitres + " dv=" + burn.DeltaVGasLitres);

            FactionTuning doubled = Tuning();
            doubled.FuelBurnScale = 2.0;
            LegBurn scaled = FuelBurn.ForLeg(leg, massKg, AllDirections(HydrogenBand()), null, doubled);
            Assert.Equal(2 * burn.HoverGasLitres, scaled.HoverGasLitres, 3);
            Assert.Equal(2 * burn.DeltaVGasLitres, scaled.DeltaVGasLitres, 3);
        }

        [Fact]
        public void Build_ThrustersOnly_ChargesADeepSpaceTurn_WhichTheDefaultWalkLeavesFree()
        {
            FactionTuning tuning = Tuning();
            StaticPlanetCatalogue planets = DeepSpace();

            var rightAngle = new List<Vec3>();
            rightAngle.Add(new Vec3(0, 0, 0));
            rightAngle.Add(new Vec3(20, 0, 0));
            rightAngle.Add(new Vec3(20, 20, 0));

            LegImpulseProfile withDrive = LegImpulseProfile.Build(planets, tuning, rightAngle);
            LegImpulseProfile thrustersOnly = LegImpulseProfile.Build(planets, tuning, rightAngle, true);

            Assert.Equal(200.0, withDrive.ThrustDeltaVMs, 6);   // a course change under the FSD is free
            Assert.Equal(200.0 + 2 * 100 * Math.Sin(Math.PI / 4), thrustersOnly.ThrustDeltaVMs, 6);
        }
    }
}
