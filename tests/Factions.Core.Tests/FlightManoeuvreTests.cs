using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="FlightManoeuvre"/> — the deceleration profile and manoeuvre geometry that
    /// the host's native flight controller drives SE's AI-thrust path from.
    /// </summary>
    public class FlightManoeuvreTests
    {
        private const double CruiseMs = 100.0;
        private const double StopDistM = 50.0;

        /// <summary>Grid on the +X axis at <paramref name="distM"/> metres from a target at the
        /// origin, closing at <paramref name="speedMs"/>.</summary>
        private static FlightSample Sample(double distM, double speedMs, double decelMs2 = 2.0)
            => new FlightSample
            {
                PosM             = new Vec3(distM, 0, 0),
                TargetM          = new Vec3(0, 0, 0),
                VelMs            = new Vec3(-speedMs, 0, 0),
                DecelMs2         = decelMs2,
                CruiseMs         = CruiseMs,
                StopDistanceM    = StopDistM,
                ArriveToleranceM = 5.0,
                MinApproachMs    = 5.0,
                ArriveSpeedMs    = 2.0,
            };

        private static double Len(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

        // ---- stopping distance ---------------------------------------------------------------

        [Fact]
        public void StoppingDistance_IsVSquaredOverTwoA()
        {
            // 100 m/s at 2 m/s^2 -> 10000/4 = 2500 m
            Assert.Equal(2500.0, FlightManoeuvre.StoppingDistanceM(100, 2), 6);
        }

        [Fact]
        public void StoppingDistance_ZeroSpeed_IsZero()
        {
            Assert.Equal(0.0, FlightManoeuvre.StoppingDistanceM(0, 2));
        }

        [Fact]
        public void StoppingDistance_NoBrakingThrust_IsInfinite()
        {
            // A grid with no usable opposing thrust can never stop — callers must clamp.
            Assert.True(double.IsPositiveInfinity(FlightManoeuvre.StoppingDistanceM(100, 0)));
        }

        // ---- horizon -------------------------------------------------------------------------

        [Fact]
        public void Horizon_ScalesWithWeakThrust()
        {
            // The whole point of a computed horizon: a weak hauler must take over much earlier.
            double weak   = FlightManoeuvre.HorizonM(Sample(9000, CruiseMs, 0.5), 1.5, 15000);
            double strong = FlightManoeuvre.HorizonM(Sample(9000, CruiseMs, 2.0), 1.5, 15000);

            Assert.True(weak > strong,
                "weak-thrust grid must engage further out than a strong one (weak=" + weak + " strong=" + strong + ")");
            // 0.5 m/s^2: 10000 m stopping * 1.5 + 50 = 15050 -> clamped to 15000
            Assert.Equal(15000.0, weak, 6);
            // 2.0 m/s^2: 2500 m stopping * 1.5 + 50 = 3800
            Assert.Equal(3800.0, strong, 6);
        }

        [Fact]
        public void Horizon_UsesCruiseSpeedWhileStillAccelerating()
        {
            // Slow right now, but it will be at cruise on arrival — the horizon must not shrink.
            double crawling = FlightManoeuvre.HorizonM(Sample(9000, 1.0, 2.0), 1.5, 15000);
            double atCruise = FlightManoeuvre.HorizonM(Sample(9000, CruiseMs, 2.0), 1.5, 15000);

            Assert.Equal(atCruise, crawling, 6);
        }

        [Fact]
        public void Horizon_NoBrakingThrust_ClampsToMax()
        {
            double h = FlightManoeuvre.HorizonM(Sample(9000, CruiseMs, 0.0), 1.5, 15000);
            Assert.Equal(15000.0, h, 6);
        }

        // ---- approach profile ----------------------------------------------------------------

        [Fact]
        public void Approach_SpeedCapFallsAsTargetNears()
        {
            double far  = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(2000, 10)).SpeedCapMs;
            double mid  = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(1000, 10)).SpeedCapMs;
            double near = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(200, 10)).SpeedCapMs;

            Assert.True(far > mid && mid > near,
                "speed cap must decrease monotonically on approach (far=" + far + " mid=" + mid + " near=" + near + ")");
        }

        [Fact]
        public void Approach_SpeedCapNeverExceedsCruise()
        {
            // Far enough out that sqrt(2*a*d) would be well above cruise.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(50000, 10));
            Assert.Equal(CruiseMs, cmd.SpeedCapMs, 6);
        }

        [Fact]
        public void Approach_SpeedCapMatchesBrakingCurve()
        {
            // 1050 m out -> 1000 m to the stop point -> sqrt(2*2*1000) = 63.245...
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(1050, 10));
            Assert.Equal(Math.Sqrt(4000.0), cmd.SpeedCapMs, 6);
        }

        [Fact]
        public void Approach_OverSpeedCommandsBrake()
        {
            // 1050 m out, cap ~63 m/s, travelling at 100 -> must brake, not thrust onward.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(1050, 100));
            Assert.Equal(0.0, Len(cmd.MoveDirWorld), 9);
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void Approach_UnderSpeedThrustsTowardTarget()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(1050, 10));

            Assert.Equal(1.0, Len(cmd.MoveDirWorld), 9);   // unit vector
            Assert.Equal(-1.0, cmd.MoveDirWorld.X, 9);     // target is at -X from the grid
        }

        [Fact]
        public void Approach_StopsShortByStopDistance()
        {
            // Exactly at the stop point (50 m out) — inside tolerance, so hold.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(StopDistM, 0));

            Assert.True(cmd.Arrived);
            Assert.Equal(0.0, Len(cmd.MoveDirWorld), 9);
            Assert.Equal(0.0, cmd.SpeedCapMs, 9);
        }

        [Fact]
        public void Approach_StillApproachingJustOutsideTolerance()
        {
            // 56 m out = 6 m past the stop point, tolerance is 5 m -> not arrived yet.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(StopDistM + 6, 0));
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void Approach_OvershootInsideStopRadiusBrakes()
        {
            // Retarget case: the grid is already inside the stop radius when control is taken.
            // Must brake, never thrust the remaining distance into the station — but at 30 m/s it
            // has not "arrived", it is flying through.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(20, 30));

            Assert.Equal(0.0, Len(cmd.MoveDirWorld), 9);
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void Approach_CloseButStillFast_IsNotArrived()
        {
            // Guards a real live-log defect: FLIGHT_STOPPED was reported at 51 m/s because arrival
            // was purely positional. Being near the stop point is not the same as being stopped.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(StopDistM, 51));

            Assert.False(cmd.Arrived);
            Assert.Equal(0.0, Len(cmd.MoveDirWorld), 9);   // still braking
        }

        [Fact]
        public void Approach_CloseAndSlow_IsArrived()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(StopDistM, 1.0));
            Assert.True(cmd.Arrived);
        }

        [Fact]
        public void Approach_MinSpeedFloorKeepsLastMetresMoving()
        {
            // 56 m out -> 6 m to go: outside the 5 m tolerance so still approaching, but
            // sqrt(2*2*6) = 4.9 m/s is under the floor -> floored to 5 so it doesn't creep.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(56, 1));

            Assert.False(cmd.Arrived);
            Assert.Equal(5.0, cmd.SpeedCapMs, 9);
        }

        [Fact]
        public void Approach_FacesTheTarget()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(1050, 10));

            Assert.True(cmd.FaceTargetWorld.HasValue);
            Assert.Equal(0.0, cmd.FaceTargetWorld.Value.X, 9);
        }

        // ---- degenerate inputs ---------------------------------------------------------------

        [Fact]
        public void Approach_SittingExactlyOnTarget_HoldsWithoutNaN()
        {
            var s = Sample(0, 0);
            s.PosM = new Vec3(0, 0, 0);

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Assert.True(cmd.Arrived);
            Assert.Equal(0.0, Len(cmd.MoveDirWorld), 9);
            Assert.False(double.IsNaN(cmd.SpeedCapMs));
        }

        [Fact]
        public void Approach_NoBrakingThrust_StillProducesFiniteCommand()
        {
            // decel 0 -> sqrt(0) = 0 -> floored to MinApproachMs, no NaN, still a unit direction.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Sample(1050, 1, 0.0));

            Assert.False(double.IsNaN(cmd.SpeedCapMs));
            Assert.Equal(5.0, cmd.SpeedCapMs, 9);
            Assert.Equal(1.0, Len(cmd.MoveDirWorld), 9);
        }

        // ---- clearance-resolved stop point ---------------------------------------------------

        [Fact]
        public void StopPoint_FliesToItWhileFacingTheTarget()
        {
            // Parking spot offset laterally from the approach axis: thrust aims at the spot, the
            // nose stays on the station.
            FlightSample s = Sample(1000, 10);
            s.StopPointM = new Vec3(100, 60, 0);

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Vec3 toStop = new Vec3(100 - 1000, 60, 0);
            double len = Len(toStop);
            double dot = (cmd.MoveDirWorld.X * toStop.X + cmd.MoveDirWorld.Y * toStop.Y
                          + cmd.MoveDirWorld.Z * toStop.Z) / len;
            Assert.Equal(1.0, dot, 9);                       // unit vector toward the stop point
            Assert.True(cmd.FaceTargetWorld.HasValue);
            Assert.Equal(0.0, Len(cmd.FaceTargetWorld.Value), 9);   // still the target, at origin
        }

        [Fact]
        public void StopPoint_ArrivalIsAtThePointNotTheTarget()
        {
            // Within tolerance of the stop point and slow: arrived, braking — even though the
            // grid is ~116 m from the target itself.
            FlightSample s = Sample(1000, 10);
            s.StopPointM = new Vec3(100, 60, 0);
            s.PosM = new Vec3(100, 58, 0);   // 2 m from the stop point
            s.VelMs = new Vec3(0, 0, 0);

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Assert.True(cmd.Arrived);
            Assert.Equal(0.0, Len(cmd.MoveDirWorld), 9);
            Assert.Equal(0.0, cmd.SpeedCapMs, 9);
        }

        [Fact]
        public void StopPoint_ZeroStandoff_CapDerivesFromDistanceToThePoint()
        {
            // 130 m short of the stop point at decel 2: cap = sqrt(2*2*130), with no additional
            // StopDistanceM subtracted — the point already embeds its standoff.
            FlightSample s = Sample(1000, 10);
            s.StopPointM = new Vec3(870, 0, 0);

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Assert.Equal(Math.Sqrt(2.0 * 2.0 * 130.0), cmd.SpeedCapMs, 9);
        }

        [Fact]
        public void StopPoint_AtTheLegacyRestPoint_MatchesLegacyCommands()
        {
            // A stop point placed exactly where the legacy rule rests (StopDistanceM short of the
            // target on the approach axis) must reproduce the legacy profile bit-for-bit.
            foreach (double dist in new[] { 5000.0, 500.0, 90.0, 54.0 })
            {
                FlightCommand legacy = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop,
                    Sample(dist, 40));

                FlightSample s = Sample(dist, 40);
                s.StopPointM = new Vec3(StopDistM, 0, 0);
                FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

                Assert.Equal(legacy.SpeedCapMs, cmd.SpeedCapMs, 9);
                Assert.Equal(legacy.Arrived, cmd.Arrived);
                Assert.Equal(legacy.MoveDirWorld.X, cmd.MoveDirWorld.X, 9);
                Assert.Equal(legacy.MoveDirWorld.Y, cmd.MoveDirWorld.Y, 9);
                Assert.Equal(legacy.MoveDirWorld.Z, cmd.MoveDirWorld.Z, 9);
            }
        }

        // ---- launch (FAC-170) ------------------------------------------------------------------

        private const double HandoffMs = 15.0;

        /// <summary>Grid at the origin, target <paramref name="distM"/> away on +X, moving at
        /// <paramref name="speedMs"/> toward it.</summary>
        private static FlightSample LaunchSample(double distM = 10000.0, double speedMs = 0.0)
            => new FlightSample
            {
                PosM            = new Vec3(0, 0, 0),
                TargetM         = new Vec3(distM, 0, 0),
                VelMs           = new Vec3(speedMs, 0, 0),
                DecelMs2        = 2.0,
                CruiseMs        = CruiseMs,
                StopDistanceM   = StopDistM,
                LaunchHandoffMs = HandoffMs,
            };

        [Fact]
        public void Launch_FromRest_ThrustsAtTheTarget()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, LaunchSample());

            Assert.Equal(1.0, cmd.MoveDirWorld.X, 6);   // unit vector straight at the target
            Assert.Equal(0.0, cmd.MoveDirWorld.Y, 6);
            Assert.Equal(CruiseMs, cmd.SpeedCapMs, 6);
            Assert.False(cmd.Complete);
        }

        /// <summary>The whole point of the manoeuvre: a grid at rest must be commanded to move.
        /// Commanding zero is how this codebase expresses "brake and hold", which is exactly the
        /// state the host's stall detector force-fails.</summary>
        [Fact]
        public void Launch_FromRest_DoesNotCommandBrakeAndHold()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, LaunchSample(speedMs: 0.0));
            Assert.True(Len(cmd.MoveDirWorld) > 0.5);
        }

        [Fact]
        public void Launch_BelowHandoffSpeed_IsNotComplete()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                LaunchSample(speedMs: HandoffMs - 0.1));
            Assert.False(cmd.Complete);
        }

        [Fact]
        public void Launch_AtHandoffSpeed_ReportsComplete()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                LaunchSample(speedMs: HandoffMs));
            Assert.True(cmd.Complete);
        }

        /// <summary>Handing back must not brake the grid it just accelerated — the completing tick
        /// still commands the travel direction, because zero would dampen the whole velocity vector.</summary>
        [Fact]
        public void Launch_OnCompletion_StillCommandsThrustNotBraking()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                LaunchSample(speedMs: HandoffMs + 20.0));

            Assert.True(cmd.Complete);
            Assert.Equal(1.0, cmd.MoveDirWorld.X, 6);
            Assert.True(Len(cmd.MoveDirWorld) > 0.5);
        }

        /// <summary>Leg arrival reads <see cref="FlightCommand.Arrived"/>. A launch happens at the
        /// START of a leg, so setting it would resolve the leg at the origin — the exact failure the
        /// separate Complete flag exists to prevent.</summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(5.0)]
        [InlineData(15.0)]
        [InlineData(80.0)]
        public void Launch_NeverReportsArrived(double speedMs)
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                LaunchSample(speedMs: speedMs));
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void Launch_ZeroLengthLeg_CompletesInsteadOfHoldingTheGrid()
        {
            var s = LaunchSample(distM: 0.0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, s);

            Assert.True(cmd.Complete);          // nothing to launch toward — hand it straight back
            Assert.False(cmd.Arrived);
            Assert.Equal(0.0, Len(cmd.MoveDirWorld), 6);
        }

        [Fact]
        public void Launch_FacesTheTarget()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, LaunchSample());
            Assert.True(cmd.FaceTargetWorld.HasValue);
            Assert.Equal(10000.0, cmd.FaceTargetWorld.Value.X, 6);
        }

        /// <summary>The two manoeuvres are bookends and must not be confused. Read on a grid sitting
        /// at rest on its stop point, approach holds it there (zero == brake and hold) while launch
        /// drives it off — which is the whole behavioural difference, stated on one sample.</summary>
        [Fact]
        public void Launch_AndApproach_DisagreeOnAGridAtRestAtTheStopPoint()
        {
            var s = LaunchSample(distM: StopDistM, speedMs: 0.0);

            FlightCommand approach = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);
            FlightCommand launch   = FlightManoeuvre.Tick(ManoeuvreKind.Launch, s);

            Assert.Equal(0.0, Len(approach.MoveDirWorld), 6);   // holds station
            Assert.True(approach.Arrived);

            Assert.True(Len(launch.MoveDirWorld) > 0.5);        // gets under way
            Assert.False(launch.Arrived);
        }

        /// <summary>The handoff speed has to clear the host's stall threshold
        /// (FactionRuntime.StallSpeedKmPerMin = 0.5 km/min = 8.33 m/s) or the manoeuvre hands the
        /// grid back into the force-fail condition it exists to fly it out of.</summary>
        [Fact]
        public void LaunchHandoffDefault_ClearsTheStallThreshold()
        {
            const double stallThresholdMs = 0.5 * 1000.0 / 60.0;   // 8.333 m/s
            Assert.True(new FactionTuning().LaunchHandoffSpeedMs > stallThresholdMs * 1.5);
        }

        /// <summary>A launch must be able to outlive the 15 s stall timeout, or the detector fires
        /// mid-launch and the manoeuvre changes nothing.</summary>
        [Fact]
        public void LaunchTimeoutDefault_OutlivesTheStallTimeout()
        {
            Assert.True(new FactionTuning().LaunchTimeoutSeconds > 15.0);
        }


        // ---- launch engage gate: the anti-chatter property (FAC-192) ---------------------------

        private const double EngageMs = 2.0;

        /// <summary>**The property whose absence destroyed a hull.** A launch ends by reporting
        /// Complete at the handoff speed, and the caller then releases the grid. If the engage gate
        /// were satisfied at that same speed, the two would chatter every frame and the grid would be
        /// pinned at the threshold instead of accelerating to cruise. Live 2026-08-29: 2,822
        /// engage/release cycles on one grid in 3m41s, held at 15.0-15.3 m/s, then destroyed.
        ///
        /// <para>Stated as the invariant rather than one sample: at ANY speed where Launch reports
        /// Complete, the engage gate must be shut.</para></summary>
        [Theory]
        [InlineData(15.0)]
        [InlineData(15.1)]
        [InlineData(15.3)]
        [InlineData(40.0)]
        [InlineData(100.0)]
        public void ShouldBeginLaunch_IsShutAtEverySpeedWhereLaunchReportsComplete(double speedMs)
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                LaunchSample(speedMs: speedMs));
            Assert.True(cmd.Complete, "precondition: this speed must complete a launch");

            Assert.False(FlightManoeuvre.ShouldBeginLaunch(speedMs, false, EngageMs, HandoffMs));
        }

        [Fact]
        public void ShouldBeginLaunch_EngagesAGridAtRest()
        {
            Assert.True(FlightManoeuvre.ShouldBeginLaunch(0.0, false, EngageMs, HandoffMs));
        }

        /// <summary>The band between the two speeds is the hysteresis. A grid inside it is under way
        /// but not yet handed back — neither gate may fire.</summary>
        [Theory]
        [InlineData(2.0)]
        [InlineData(8.0)]
        [InlineData(14.9)]
        public void ShouldBeginLaunch_DoesNotReEngageInsideTheHysteresisBand(double speedMs)
        {
            Assert.False(FlightManoeuvre.ShouldBeginLaunch(speedMs, false, EngageMs, HandoffMs));
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, LaunchSample(speedMs: speedMs));
            Assert.False(cmd.Complete);
        }

        /// <summary>The approach owns the end of a leg; a launch must never take a grid off it.</summary>
        [Fact]
        public void ShouldBeginLaunch_RefusesInsideTheApproachHorizon()
        {
            Assert.False(FlightManoeuvre.ShouldBeginLaunch(0.0, true, EngageMs, HandoffMs));
        }

        /// <summary>A misconfiguration that collapses the hysteresis band is refused outright rather
        /// than allowed to oscillate — the failure mode is a pinned grid, which is worse than no
        /// launch at all.</summary>
        [Theory]
        [InlineData(15.0, 15.0)]
        [InlineData(20.0, 15.0)]
        public void ShouldBeginLaunch_RefusesADegenerateBand(double engage, double handoff)
        {
            Assert.False(FlightManoeuvre.ShouldBeginLaunch(0.0, false, engage, handoff));
        }

        // ---- launch clearance (FAC-299) --------------------------------------------------------

        private const double ClearanceM = 400.0;

        /// <summary>Grid <paramref name="fromOriginM"/> along +X of where the launch engaged, target
        /// <paramref name="distM"/> further on, moving at <paramref name="speedMs"/> toward it.</summary>
        private static FlightSample ClearanceSample(double fromOriginM, double speedMs,
                                                    double distM = 80000.0, double clearanceM = ClearanceM)
            => new FlightSample
            {
                PosM             = new Vec3(fromOriginM, 0, 0),
                LaunchOriginM    = new Vec3(0, 0, 0),
                TargetM          = new Vec3(fromOriginM + distM, 0, 0),
                VelMs            = new Vec3(speedMs, 0, 0),
                DecelMs2         = 2.0,
                CruiseMs         = CruiseMs,
                StopDistanceM    = StopDistM,
                LaunchHandoffMs  = HandoffMs,
                LaunchClearanceM = clearanceM,
            };

        /// <summary>The defect: handing back at handoff speed while the grid is still parked beside
        /// the station it is leaving. Speed alone is not evidence that it has got away.</summary>
        [Fact]
        public void Launch_UpToSpeedButStillAtTheOrigin_IsNotComplete()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                ClearanceSample(fromOriginM: 20.0, speedMs: HandoffMs));
            Assert.False(cmd.Complete);
        }

        [Fact]
        public void Launch_UpToSpeedAndClearOfTheOrigin_ReportsComplete()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                ClearanceSample(fromOriginM: ClearanceM, speedMs: HandoffMs));
            Assert.True(cmd.Complete);
        }

        /// <summary>Both conditions, not either: distance without speed is a grid that has drifted.</summary>
        [Fact]
        public void Launch_ClearOfTheOriginButBelowHandoffSpeed_IsNotComplete()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                ClearanceSample(fromOriginM: ClearanceM * 2, speedMs: HandoffMs - 0.1));
            Assert.False(cmd.Complete);
        }

        /// <summary>A launch held for clearance must keep thrusting. Commanding zero is this
        /// codebase's brake-and-hold, so a held launch that commanded it would shed the speed it is
        /// waiting to keep — the FAC-192 failure, reached through the new condition.</summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(HandoffMs)]
        [InlineData(HandoffMs + 30.0)]
        public void Launch_HeldForClearance_KeepsThrustingAtTheTarget(double speedMs)
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                ClearanceSample(fromOriginM: 20.0, speedMs: speedMs));

            Assert.False(cmd.Complete, "precondition: this sample must still be held for clearance");
            Assert.Equal(1.0, cmd.MoveDirWorld.X, 6);
            Assert.True(Len(cmd.MoveDirWorld) > 0.5);
        }

        /// <summary>A grid already at cruise has nothing left to gain from the launch profile, and
        /// holding it would push it past the leg's own speed limit.</summary>
        [Fact]
        public void Launch_AtCruiseSpeed_CompletesEvenInsideTheClearanceRadius()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                ClearanceSample(fromOriginM: 5.0, speedMs: CruiseMs));
            Assert.True(cmd.Complete);
        }

        /// <summary>Clearance can only be reached on a leg longer than the clearance radius; on a
        /// shorter one, insisting would mean overshooting the destination to satisfy it.</summary>
        [Fact]
        public void Launch_OnALegShorterThanTheClearance_HandsBackOnSpeedAlone()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                ClearanceSample(fromOriginM: 10.0, speedMs: HandoffMs, distM: ClearanceM - 100.0));
            Assert.True(cmd.Complete);
        }

        /// <summary>Zero is the off switch, and the behaviour it restores is exactly the old one.</summary>
        [Fact]
        public void Launch_ZeroClearance_RestoresSpeedOnlyHandback()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                ClearanceSample(fromOriginM: 0.0, speedMs: HandoffMs, clearanceM: 0.0));
            Assert.True(cmd.Complete);
        }

        /// <summary>Falsifiable guard on the shipped numbers, in the shape FAC-192's band guard
        /// established. A clearance the grid cannot cover inside the launch timeout would turn every
        /// launch into a <c>reason=timeout</c> handback — the same open-loop release as before, with
        /// 45 s of thrust wasted first. Coasting at handoff speed is the pessimistic case: a real
        /// launch keeps accelerating.</summary>
        [Fact]
        public void LaunchDefaults_ClearanceIsReachableWellInsideTheTimeout()
        {
            var t = new FactionTuning();
            double secondsAtHandoffSpeed = t.LaunchClearanceM / t.LaunchHandoffSpeedMs;
            Assert.True(secondsAtHandoffSpeed <= 0.75 * t.LaunchTimeoutSeconds,
                "clearance " + t.LaunchClearanceM + " m at " + t.LaunchHandoffSpeedMs
                + " m/s needs " + secondsAtHandoffSpeed + " s of a " + t.LaunchTimeoutSeconds + " s timeout");
        }

        /// <summary>The verify window has to close while there is still something to do about a
        /// failed handback. The host force-fails a grid held below its stall speed for 15 s, and a
        /// retry can only re-engage the launch before that fires.</summary>
        [Fact]
        public void HandbackDefaults_VerifyWindowClosesBeforeTheStallTimeout()
        {
            var t = new FactionTuning();
            Assert.True(t.HandbackVerifySeconds > 0);
            Assert.True(t.HandbackVerifySeconds < 15.0);
            Assert.True(t.HandbackMaxRetries >= 1);
            // and the failure threshold must sit strictly inside (0, handoff) or it can never fire
            Assert.True(t.HandbackMinSpeedFrac > 0.0 && t.HandbackMinSpeedFrac < 1.0);
        }

        /// <summary>The shipped defaults must leave a real band, not merely an ordered pair.</summary>
        [Fact]
        public void LaunchDefaults_LeaveAUsableHysteresisBand()
        {
            var t = new FactionTuning();
            Assert.True(t.LaunchEngageSpeedMs < t.LaunchHandoffSpeedMs);
            Assert.True(t.LaunchHandoffSpeedMs - t.LaunchEngageSpeedMs >= 5.0);
            // and the engage gate must still sit under the host stall threshold it protects against
            Assert.True(t.LaunchEngageSpeedMs < 0.5 * 1000.0 / 60.0);
        }

        // ---- launch departs along a cleared bearing ----------------------------------------------

        /// <summary>Grid at the origin with a target on +X, but the way out resolved to +Y — the
        /// station it is parked against sits between the two.</summary>
        private static FlightSample DepartingSample(double alongDepartM, double speedMs)
        {
            FlightSample s = LaunchSample(speedMs: speedMs);
            s.PosM = new Vec3(0, alongDepartM, 0);      // already some way along the departure
            s.LaunchOriginM = new Vec3(0, 0, 0);
            s.LaunchClearanceM = 400.0;
            s.LaunchDirWorld = new Vec3(0, 1, 0);
            s.VelMs = new Vec3(0, speedMs, 0);
            return s;
        }

        [Fact]
        public void Launch_WithAResolvedBearing_ThrustsAlongItRatherThanAtTheTarget()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, DepartingSample(50, 5));

            Assert.Equal(0.0, cmd.MoveDirWorld.X, 6);
            Assert.Equal(1.0, cmd.MoveDirWorld.Y, 6);
        }

        /// <summary>The nose goes with the departure, not the destination: thrust authority is
        /// highest along it, and the grid is flying that way.</summary>
        [Fact]
        public void Launch_WhileDeparting_FacesAlongTheDepartureBearing()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, DepartingSample(50, 5));

            Assert.True(cmd.FaceTargetWorld.HasValue);
            Vec3 face = cmd.FaceTargetWorld.Value;
            Assert.True(face.Y > 0, "facing should be ahead along +Y, got Y=" + face.Y);
            Assert.Equal(0.0, face.X, 6);
        }

        /// <summary>Once clear of the origin the launch is done with its departure bearing and the
        /// handback aims the grid at its destination again. Without this the deflection would
        /// persist into the cruise leg.</summary>
        [Fact]
        public void Launch_OnceClearAndUpToSpeed_ReturnsToTheTargetBearing()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch,
                DepartingSample(500, HandoffMs + 1));   // past the 400 m clearance, over handoff

            Assert.True(cmd.Complete);
            Assert.True(cmd.MoveDirWorld.X > 0.9,
                "a completed launch should point at the destination, got X=" + cmd.MoveDirWorld.X);
        }

        [Fact]
        public void Launch_WithNoResolvedBearing_IsUnchanged()
        {
            FlightSample s = DepartingSample(50, 5);
            s.LaunchDirWorld = null;

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, s);

            Assert.True(cmd.MoveDirWorld.X > 0.9,
                "without a pass the launch thrusts at the target as before, got X=" + cmd.MoveDirWorld.X);
        }

        [Fact]
        public void Launch_ADegenerateBearingFallsBackToTheTarget()
        {
            FlightSample s = DepartingSample(50, 5);
            s.LaunchDirWorld = new Vec3(0, 0, 0);

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, s);

            Assert.True(cmd.MoveDirWorld.X > 0.9,
                "a zero bearing must not command zero thrust (that is the brake), got X="
                + cmd.MoveDirWorld.X);
        }

        // ---- launch hands back on attitude, not on braking (FAC-334) ---------------------------

        private const double G1 = 9.81;

        /// <summary>Alsvidr from the live catalogue, as forces on the grid: 19.08 MN lifts it belly
        /// down, 13.68 MN pushes it grid-Backward (where its cockpit faces), 6.48 MN grid-Forward.</summary>
        private const double AlsvidrKg = 865674.0;
        private static AxisThrust Alsvidr() => new AxisThrust
        {
            UpN = 19080000, DownN = 5400000, LeftN = 6480000, RightN = 6480000,
            ForwardN = 6480000, BackwardN = 13680000,
        };

        /// <summary>The live failure: climbing grid-Forward first puts Alsvidr's weight on 6.48 MN,
        /// 0.76 TWR at 1 g, and it sank on every launch at METC N110.</summary>
        [Fact]
        public void LiftTwr_GridForwardUp_ReadsTheWeakAxis_AndTheLaunchHandsBack()
        {
            double twr = FlightManoeuvre.LiftTwr(new Vec3(0, 0, -1), Alsvidr(), AlsvidrKg, G1);
            Assert.Equal(0.76, twr, 2);
            Assert.True(FlightManoeuvre.LaunchCannotHoldAltitude(twr, 1.0));
        }

        /// <summary>The attitude the cruise autopilot flies, cockpit forward and up, holds.</summary>
        [Fact]
        public void LiftTwr_CockpitUp_ReadsTheMainEngines_AndTheLaunchHolds()
        {
            double twr = FlightManoeuvre.LiftTwr(new Vec3(0, 0, 1), Alsvidr(), AlsvidrKg, G1);
            Assert.Equal(1.61, twr, 2);
            Assert.False(FlightManoeuvre.LaunchCannotHoldAltitude(twr, 1.0));
        }

        [Fact]
        public void LiftTwr_BellyDown_ReadsTheLiftThrusters()
        {
            Assert.Equal(2.25, FlightManoeuvre.LiftTwr(new Vec3(0, 1, 0), Alsvidr(), AlsvidrKg, G1), 2);
        }

        /// <summary>Pitched halfway between belly-down and grid-Forward-up, both axes push and each
        /// is weighted by how much of up lies along it.</summary>
        [Fact]
        public void LiftTwr_Tilted_WeightsEachAxisByItsShareOfUp()
        {
            double h = Math.Sqrt(0.5);
            double expected = (19080000 * h + 6480000 * h) / (AlsvidrKg * G1);
            Assert.Equal(expected, FlightManoeuvre.LiftTwr(new Vec3(0, h, -h), Alsvidr(), AlsvidrKg, G1), 6);
        }

        [Fact]
        public void LiftTwr_InZeroGravity_NeverHandsBack()
        {
            double twr = FlightManoeuvre.LiftTwr(new Vec3(0, 0, -1), Alsvidr(), AlsvidrKg, 0.0);
            Assert.True(double.IsPositiveInfinity(twr));
            Assert.False(FlightManoeuvre.LaunchCannotHoldAltitude(twr, 1.0));
        }

        [Fact]
        public void LaunchCannotHoldAltitude_ZeroThresholdSwitchesItOff()
        {
            Assert.False(FlightManoeuvre.LaunchCannotHoldAltitude(0.1, 0.0));
        }

        /// <summary>A launch never brakes, so a braking estimate of zero says nothing about it. In
        /// gravity that estimate reads zero for any hull sinking on an axis under 2.5 TWR.</summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(-6.3)]
        public void AbortsOnLostBraking_NeverForALaunch(double decelMs2)
        {
            Assert.False(FlightManoeuvre.AbortsOnLostBraking(ManoeuvreKind.Launch, decelMs2));
        }

        [Fact]
        public void AbortsOnLostBraking_StillAbortsAnApproach()
        {
            Assert.True(FlightManoeuvre.AbortsOnLostBraking(ManoeuvreKind.ApproachAndStop, 0.0));
            Assert.False(FlightManoeuvre.AbortsOnLostBraking(ManoeuvreKind.ApproachAndStop, 0.5));
        }

        // ---- launch gate for a grid no manoeuvre has held yet (FAC-337) ------------------------

        private const double FirstEngageMs = 10.0;

        private static LaunchRefusal Gate(double speedMs, bool withinHorizon = false, bool approachFollows = true,
                                          bool launchedBefore = false, double firstEngageMs = FirstEngageMs)
            => FlightManoeuvre.LaunchGate(speedMs, withinHorizon, approachFollows, launchedBefore,
                                          EngageMs, firstEngageMs, HandoffMs);

        /// <summary>The live failure: a fresh spawn already creeping at 4-7 m/s when first asked was
        /// never launched, stayed under the 8.33 m/s stall speed and was destroyed at 15 s.</summary>
        [Theory]
        [InlineData(4.3)]
        [InlineData(6.5)]
        [InlineData(8.3)]
        public void AFreshGridAlreadyCreeping_IsLaunched(double speedMs)
        {
            Assert.Equal(LaunchRefusal.None, Gate(speedMs));
        }

        /// <summary>The anti-chatter band still holds for a grid a launch has already handed back.</summary>
        [Theory]
        [InlineData(4.3)]
        [InlineData(8.3)]
        public void AGridALaunchHasHeld_KeepsTheLowEngageSpeed(double speedMs)
        {
            Assert.Equal(LaunchRefusal.TooFast, Gate(speedMs, launchedBefore: true));
        }

        /// <summary>FAC-192's invariant, for both kinds of grid and even with the first-engage speed
        /// set above handoff: wherever a launch reports Complete, the gate is shut.</summary>
        [Theory]
        [InlineData(15.0, 10.0)]
        [InlineData(15.0, 40.0)]
        [InlineData(40.0, 40.0)]
        public void NoGridEngagesWhereALaunchWouldReportComplete(double speedMs, double firstEngageMs)
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, LaunchSample(speedMs: speedMs));
            Assert.True(cmd.Complete, "precondition: this speed must complete a launch");
            Assert.NotEqual(LaunchRefusal.None, Gate(speedMs, firstEngageMs: firstEngageMs));
            Assert.NotEqual(LaunchRefusal.None, Gate(speedMs, launchedBefore: true, firstEngageMs: firstEngageMs));
        }

        /// <summary>A fly-through waypoint inside the horizon: no approach will be asked, so a launch
        /// refused here left the grid flown by neither.</summary>
        [Fact]
        public void InsideTheHorizon_OnAFlyThroughLeg_IsLaunched()
        {
            Assert.Equal(LaunchRefusal.None, Gate(0.0, withinHorizon: true, approachFollows: false));
        }

        [Fact]
        public void InsideTheHorizon_WhenAnApproachWillFollow_IsLeftToTheApproach()
        {
            Assert.Equal(LaunchRefusal.InsideApproachHorizon, Gate(0.0, withinHorizon: true, approachFollows: true));
        }

        [Fact]
        public void ADegenerateBand_IsRefusedForAFreshGridToo()
        {
            Assert.Equal(LaunchRefusal.DegenerateBand,
                FlightManoeuvre.LaunchGate(0.0, false, false, false, HandoffMs, FirstEngageMs, HandoffMs));
        }

        // ---- braking plans against what is left after gravity (FAC-338) ------------------------

        private const double Safety = 0.4;

        /// <summary>In zero gravity the two forms are the same number, so nothing off a planet moves.</summary>
        [Fact]
        public void BrakingDecel_InZeroGravity_IsTheDeratedThrust()
        {
            Assert.Equal(8.0, FlightManoeuvre.BrakingDecelMs2(20.0, 0.0, Safety), 6);
        }

        /// <summary>The live failure: an 856 t Alsvidr holds 2.25 TWR, so 22.1 m/s² of thrust against
        /// 9.81 of gravity. Derating the thrust alone reads −0.98 and the controller hands the grid
        /// back mid-descent; derating what is left reads a real, conservative brake.</summary>
        [Fact]
        public void BrakingDecel_DescendingAtOneG_IsPositiveForAHullThatCanBrake()
        {
            double thrust = 2.25 * 9.81;
            Assert.True(thrust * Safety - 9.81 < 0, "precondition: the old form reads <= 0 here");
            Assert.Equal((thrust - 9.81) * Safety, FlightManoeuvre.BrakingDecelMs2(thrust, -9.81, Safety), 6);
            Assert.True(FlightManoeuvre.BrakingDecelMs2(thrust, -9.81, Safety) > 4.0);
        }

        /// <summary>A hull that genuinely cannot out-thrust gravity on the brake axis still reads 0 —
        /// the value that means "no usable approach" — and so does one with no thrust at all.</summary>
        [Theory]
        [InlineData(9.0, -9.81)]
        [InlineData(9.81, -9.81)]
        [InlineData(0.0, 0.0)]
        public void BrakingDecel_IsZeroWhenTheHullCannotBrake(double thrustAccel, double gravityAlongBrake)
        {
            Assert.Equal(0.0, FlightManoeuvre.BrakingDecelMs2(thrustAccel, gravityAlongBrake, Safety), 6);
        }

        /// <summary>Climbing, gravity helps the brake, and the safety factor applies to that too —
        /// the old form left gravity's contribution underated and planned optimistically.</summary>
        [Fact]
        public void BrakingDecel_Climbing_DeratesGravitysHelpToo()
        {
            double planned = FlightManoeuvre.BrakingDecelMs2(20.0, 9.81, Safety);
            Assert.Equal((20.0 + 9.81) * Safety, planned, 6);
            Assert.True(planned < 20.0 * Safety + 9.81, "gravity's help must be derated with the rest");
        }
    }
}
