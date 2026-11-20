using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The combat kinds of <see cref="FlightManoeuvre.Tick"/>: range keeping against a moving
    /// target, with translation and facing independent and the braking rule honoured.
    /// </summary>
    public class CombatManoeuvreTests
    {
        private const double Decel = 2.0;
        private const double Cruise = 100.0;

        /// <summary>Grid on +X at <paramref name="distM"/> from a target at the origin, moving at
        /// <paramref name="velX"/> along X (negative closes).</summary>
        private static FlightSample Sample(double distM, double holdM, double velX = 0, double decel = Decel)
            => new FlightSample
            {
                PosM            = new Vec3(distM, 0, 0),
                TargetM         = new Vec3(0, 0, 0),
                VelMs           = new Vec3(velX, 0, 0),
                DecelMs2        = decel,
                CruiseMs        = Cruise,
                ArriveSpeedMs   = 2.0,
                HoldRangeM      = holdM,
                RangeToleranceM = 25.0,
            };

        private static double Len(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static bool IsZero(Vec3 v) => v.X == 0 && v.Y == 0 && v.Z == 0;

        // ---- HoldAtRange ------------------------------------------------------------------

        [Fact]
        public void HoldAtRange_FarOutAtRest_ClosesAndFacesTarget()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(3000, 1000));
            Assert.True(cmd.MoveDirWorld.X < -0.99, "should thrust toward the target");
            Assert.True(cmd.FaceTargetWorld.HasValue);
            Assert.Equal(0.0, Len(cmd.FaceTargetWorld.Value), 6);
            Assert.False(cmd.Arrived);
        }

        [Theory]
        [InlineData(1030)]
        [InlineData(1100)]
        [InlineData(1500)]
        [InlineData(3000)]
        [InlineData(20000)]
        public void HoldAtRange_NeverAsksForASpeedItCannotStopFromBeforeTheRange(double dist)
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(dist, 1000));
            double stop = FlightManoeuvre.StoppingDistanceM(cmd.SpeedCapMs, Decel);
            Assert.True(stop <= dist - 1000 + 1e-6, "stop " + stop + " vs gap " + (dist - 1000));
        }

        [Fact]
        public void HoldAtRange_ClosingFasterThanItCanStop_ThrustsAgainstTheClose()
        {
            // 100 m out of the shell at 2 m/s^2 allows 20 m/s; closing at 50.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(1100, 1000, velX: -50));
            Assert.True(cmd.MoveDirWorld.X > 0.99, "should brake away from the target");
        }

        [Fact]
        public void HoldAtRange_OnStation_BrakesAndHolds()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(1010, 1000, velX: -1));
            Assert.True(IsZero(cmd.MoveDirWorld));
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void HoldAtRange_TooClose_OpensTheRange()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(400, 1000));
            Assert.True(cmd.MoveDirWorld.X > 0.99);
            Assert.Equal(0.0, Len(cmd.FaceTargetWorld.Value), 6);
        }

        [Fact]
        public void HoldAtRange_MovingTarget_IsFollowedOnStation()
        {
            FlightSample s = Sample(1000, 1000);
            s.TargetVelMs = new Vec3(0, 30, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.True(cmd.MoveDirWorld.Y > 0.99, "should match the target's velocity");
            Assert.Equal(30.0, cmd.SpeedCapMs, 6);
        }

        [Fact]
        public void HoldAtRange_NeverAsksForMoreThanCruise()
        {
            FlightSample s = Sample(50000, 1000);
            s.TargetVelMs = new Vec3(0, 80, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.True(cmd.SpeedCapMs <= Cruise + 1e-9);
        }

        // ---- Orbit ------------------------------------------------------------------------

        [Fact]
        public void Orbit_OnTheCircle_MovesAcrossTheRadiusAndFacesTarget()
        {
            FlightSample s = Sample(600, 600);
            s.OrbitSpeedMs = 20;
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Orbit, s);
            Assert.Equal(0.0, Dot(cmd.MoveDirWorld, new Vec3(1, 0, 0)), 6);
            Assert.Equal(1.0, Len(cmd.MoveDirWorld), 6);
            Assert.Equal(0.0, Len(cmd.FaceTargetWorld.Value), 6);
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void Orbit_KeepsTheWayItIsAlreadyGoingRound()
        {
            FlightSample s = Sample(600, 600);
            s.OrbitSpeedMs = 20;
            s.VelMs = new Vec3(0, 0, 5);   // the default start would go round the other way
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Orbit, s);
            Assert.True(cmd.MoveDirWorld.Z > 0.99);
        }

        [Fact]
        public void Orbit_SpeedIsCappedByWhatTheGridCanTurn()
        {
            // v^2/r <= a: at 1 m/s^2 on a 400 m circle, 20 m/s.
            FlightSample s = Sample(400, 400, decel: 1.0);
            s.OrbitSpeedMs = 200;
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Orbit, s);
            Assert.Equal(20.0, cmd.SpeedCapMs, 6);
            Assert.Equal(20.0, FlightManoeuvre.OrbitSpeedCapMs(400, 1.0), 9);
        }

        // ---- Kite -------------------------------------------------------------------------

        [Fact]
        public void Kite_TargetInside_FallsBackWhileFacingIt()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Kite, Sample(400, 1000));
            Assert.True(cmd.MoveDirWorld.X > 0.99);
            Assert.Equal(0.0, Len(cmd.FaceTargetWorld.Value), 6);
        }

        [Fact]
        public void Kite_TargetOutside_NeverCloses()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Kite, Sample(3000, 1000));
            Assert.True(IsZero(cmd.MoveDirWorld), "a kite beyond its range holds rather than closing");
        }

        [Fact]
        public void Kite_OpeningIsCappedToStopAtTheRange()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Kite, Sample(900, 1000));
            Assert.True(FlightManoeuvre.StoppingDistanceM(cmd.SpeedCapMs, Decel) <= 100 + 1e-6);
        }

        // ---- AttackRun --------------------------------------------------------------------

        [Fact]
        public void AttackRun_Inbound_ClosesAndReportsTheEndOfTheLeg()
        {
            FlightSample s = Sample(2000, 1500);
            s.InnerRangeM = 300;
            s.RunInbound = true;
            FlightCommand far = FlightManoeuvre.Tick(ManoeuvreKind.AttackRun, s);
            Assert.True(far.MoveDirWorld.X < -0.99);
            Assert.False(far.PhaseDone);
            Assert.True(FlightManoeuvre.StoppingDistanceM(far.SpeedCapMs, Decel) <= 2000 - 300 + 1e-6);

            s.PosM = new Vec3(310, 0, 0);
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.AttackRun, s).PhaseDone);
        }

        [Fact]
        public void AttackRun_Outbound_OpensAndReportsTheEndOfTheLeg()
        {
            FlightSample s = Sample(300, 1500);
            s.InnerRangeM = 300;
            s.RunInbound = false;
            FlightCommand near = FlightManoeuvre.Tick(ManoeuvreKind.AttackRun, s);
            Assert.True(near.MoveDirWorld.X > 0.99);
            Assert.False(near.PhaseDone);
            Assert.Equal(0.0, Len(near.FaceTargetWorld.Value), 6);

            s.PosM = new Vec3(1490, 0, 0);
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.AttackRun, s).PhaseDone);
        }

        // ---- Disengage --------------------------------------------------------------------

        [Fact]
        public void Disengage_LeavesNoseFirstUntilClear()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Disengage, Sample(500, 3000));
            Assert.True(cmd.MoveDirWorld.X > 0.99);
            Assert.True(cmd.FaceTargetWorld.Value.X > 500, "nose along the way out");
            Assert.False(cmd.PhaseDone);
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.Disengage, Sample(3100, 3000)).PhaseDone);
        }

        /// <summary>A retreat must read as one (operator, bench fight L1: a break-off was "difficult
        /// to differentiate from drifting off"): the nose is along the burn, tail to the enemy, and
        /// the burn is straight away whatever the enemy does -- a target sliding sideways or closing
        /// does not bend it.</summary>
        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(0, 60, 0)]
        [InlineData(-80, 0, 30)]
        public void Disengage_FacesAlongItsBurn_StraightAwayFromTheEnemy(double tvx, double tvy, double tvz)
        {
            FlightSample s = Sample(800, 3000);
            s.PosM = new Vec3(480, 640, 0);                // 800 m off, on a bearing of (0.6, 0.8, 0)
            s.TargetVelMs = new Vec3(tvx, tvy, tvz);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s);

            Vec3 move = cmd.MoveDirWorld;
            Assert.Equal(1.0, Len(move), 9);
            Assert.Equal(0.6, move.X, 9);
            Assert.Equal(0.8, move.Y, 9);
            Assert.Equal(0.0, move.Z, 9);
            Assert.Equal(Cruise, cmd.SpeedCapMs, 9);

            Vec3 nose = new Vec3(cmd.FaceTargetWorld.Value.X - s.PosM.X, cmd.FaceTargetWorld.Value.Y - s.PosM.Y,
                                 cmd.FaceTargetWorld.Value.Z - s.PosM.Z);
            Assert.Equal(1.0, Dot(nose, move) / Len(nose), 9);
        }

        // ---- gravity ----------------------------------------------------------------------

        [Fact]
        public void Opening_InGravity_ClimbsInsteadOfRunningAlongTheGround()
        {
            FlightSample s = Sample(100, 1000);   // level with a surface target
            s.UpWorld = new Vec3(0, 1, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.True(cmd.MoveDirWorld.Y >= FlightManoeuvre.MinOpeningClimb - 1e-9);
            Assert.True(cmd.MoveDirWorld.X > 0);

            FlightCommand leave = FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s);
            Assert.True(leave.MoveDirWorld.Y >= FlightManoeuvre.MinOpeningClimb - 1e-9);
        }

        [Fact]
        public void Opening_InSpace_IsStraightAway()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(100, 1000));
            Assert.Equal(1.0, cmd.MoveDirWorld.X, 9);
        }

        [Fact]
        public void ClimbingAway_LeavesASteepEnoughBearingAlone()
        {
            var steep = new Vec3(0.6, 0.8, 0);
            Vec3 r = FlightManoeuvre.ClimbingAway(steep, new Vec3(0, 1, 0));
            Assert.Equal(0.6, r.X, 9);
            Assert.Equal(0.8, r.Y, 9);
            Vec3 down = FlightManoeuvre.ClimbingAway(new Vec3(0, -1, 0), new Vec3(0, 1, 0));
            Assert.Equal(1.0, down.Y, 9);
        }

        [Fact]
        public void Closing_InGravity_IsNotBent()
        {
            FlightSample s = Sample(3000, 1000);
            s.UpWorld = new Vec3(0, 1, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.Equal(-1.0, cmd.MoveDirWorld.X, 9);
        }

        // ---- common -----------------------------------------------------------------------

        [Theory]
        [InlineData(ManoeuvreKind.HoldAtRange)]
        [InlineData(ManoeuvreKind.Orbit)]
        [InlineData(ManoeuvreKind.Kite)]
        [InlineData(ManoeuvreKind.AttackRun)]
        [InlineData(ManoeuvreKind.Disengage)]
        public void CombatKinds_NeverReportLegArrival(ManoeuvreKind kind)
        {
            FlightSample s = Sample(1000, 1000);
            s.InnerRangeM = 1000;
            s.RunInbound = true;
            FlightCommand cmd = FlightManoeuvre.Tick(kind, s);
            Assert.False(cmd.Arrived);
            Assert.False(cmd.Complete);
            Assert.True(FlightManoeuvre.IsCombat(kind));
        }

        [Fact]
        public void IsCombat_ExcludesTheLegManoeuvres()
        {
            Assert.False(FlightManoeuvre.IsCombat(ManoeuvreKind.ApproachAndStop));
            Assert.False(FlightManoeuvre.IsCombat(ManoeuvreKind.Launch));
        }

        [Fact]
        public void RangeSpeedCap_IsTheInverseOfStoppingDistance()
        {
            double cap = FlightManoeuvre.RangeSpeedCapMs(400, 2.0, 1000);
            Assert.Equal(40.0, cap, 9);
            Assert.Equal(400.0, FlightManoeuvre.StoppingDistanceM(cap, 2.0), 6);
            Assert.Equal(0.0, FlightManoeuvre.RangeSpeedCapMs(400, 0, 1000));
            Assert.Equal(30.0, FlightManoeuvre.RangeSpeedCapMs(4000, 2.0, 30));
        }
    }
}
