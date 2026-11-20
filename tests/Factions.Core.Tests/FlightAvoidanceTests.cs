using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="FlightAvoidance"/> and its composition into
    /// <see cref="FlightManoeuvre.Tick"/> — the steer-around term every native manoeuvre passes
    /// through.
    /// </summary>
    public class FlightAvoidanceTests
    {
        private const double SelfR = 20.0;
        private const double Margin = 25.0;
        private const double Lookahead = 8.0;

        /// <summary>Grid 2 km out on +X from a target at the origin, closing along -X at
        /// <paramref name="speedMs"/>, well outside the terminal bubble so the profile thrusts.</summary>
        private static FlightSample Approach(double speedMs, params FlightObstacle[] obstacles)
        {
            return new FlightSample
            {
                PosM              = new Vec3(2000, 0, 0),
                TargetM           = new Vec3(0, 0, 0),
                VelMs             = new Vec3(-speedMs, 0, 0),
                DecelMs2          = 2.0,
                CruiseMs          = 100.0,
                StopDistanceM     = 50.0,
                ArriveToleranceM  = 5.0,
                MinApproachMs     = 5.0,
                ArriveSpeedMs     = 2.0,
                SelfCenterM       = new Vec3(2000, 0, 0),
                SelfRadiusM       = SelfR,
                Obstacles         = obstacles,
                ObstacleCount     = obstacles.Length,
                AvoidLookaheadSec = Lookahead,
                AvoidMarginM      = Margin,
            };
        }

        private static FlightObstacle Obstacle(double x, double y, double z, double r,
                                               double vx = 0, double vy = 0, double vz = 0)
        {
            return new FlightObstacle { CenterM = new Vec3(x, y, z), RadiusM = r, VelMs = new Vec3(vx, vy, vz) };
        }

        private static double Len(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static FlightCommand WithoutAvoidance(FlightSample s)
        {
            s.Obstacles = null;
            s.ObstacleCount = 0;
            return FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);
        }

        // ---- no threat: the manoeuvre's own command is untouched -----------------------------

        [Fact]
        public void NoObstacles_CommandIsTheManoeuvresOwn()
        {
            var s = Approach(20);
            s.Obstacles = null;
            s.ObstacleCount = 0;
            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Assert.Equal(-1, cmd.Avoid.WorstIndex);
            Assert.Equal(-1.0, cmd.MoveDirWorld.X, 9);
        }

        [Fact]
        public void ObstacleWellOffThePath_IsIgnored()
        {
            // 300 m to the side of the track; separation is 20 + 20 + 25 = 65 m.
            var s = Approach(20, Obstacle(1900, 300, 0, 20));
            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Assert.Equal(-1, cmd.Avoid.WorstIndex);
            Assert.Equal(WithoutAvoidance(s).MoveDirWorld.X, cmd.MoveDirWorld.X, 9);
        }

        [Fact]
        public void ObstacleBehind_IsIgnored()
        {
            var s = Approach(20, Obstacle(2200, 0, 0, 20));
            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);
            Assert.Equal(-1, cmd.Avoid.WorstIndex);
        }

        [Fact]
        public void ObstacleBeyondTheLookahead_IsIgnored()
        {
            // 20 m/s x 8 s = 160 m of look-ahead; the obstacle is 500 m down the track.
            var s = Approach(20, Obstacle(1500, 0, 0, 20));
            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);
            Assert.Equal(-1, cmd.Avoid.WorstIndex);
        }

        [Fact]
        public void ZeroSelfRadius_SwitchesAvoidanceOff()
        {
            var s = Approach(20, Obstacle(1900, 0, 0, 20));
            s.SelfRadiusM = 0;
            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);
            Assert.Equal(-1, cmd.Avoid.WorstIndex);
        }

        // ---- a threat bends the course --------------------------------------------------------

        [Fact]
        public void ParkedShipOnTheTrack_DeflectsSideways()
        {
            // Slightly off-centre (+Y 10 m), so the sidestep has a side to pick: away from it, -Y.
            var s = Approach(20, Obstacle(1900, 10, 0, 20));
            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Assert.Equal(0, cmd.Avoid.WorstIndex);
            Assert.True(cmd.Avoid.Urgency > 0);
            Assert.True(cmd.MoveDirWorld.Y < -0.1, "steers away from the obstacle's side");
            Assert.Equal(1.0, Len(cmd.MoveDirWorld), 6);
        }

        [Fact]
        public void CloserThreat_IsMoreUrgent()
        {
            var far  = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Approach(20, Obstacle(1880, 10, 0, 20)));
            var near = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Approach(20, Obstacle(1960, 10, 0, 20)));
            Assert.True(near.Avoid.Urgency > far.Avoid.Urgency);
        }

        [Fact]
        public void UrgencyFallsToZeroAtTheEdgeOfTheSeparation()
        {
            // Continuity is what keeps a grid from chattering: just inside the separation sphere
            // the course bends by almost nothing.
            double sep = SelfR + 20 + Margin;
            var s = Approach(20, Obstacle(1950, sep - 0.5, 0, 20));
            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);

            Assert.True(cmd.Avoid.Urgency < 0.02);
            Assert.True(cmd.MoveDirWorld.X < -0.98);
        }

        [Fact]
        public void OverlappingNow_PushesAway()
        {
            // Stationary, overlapping, obstacle on +Y: the grid is pushed toward -Y.
            var s = Approach(0.0, Obstacle(2000, 30, 0, 20));
            var a = FlightAvoidance.Assess(s, null);

            Assert.Equal(0, a.WorstIndex);
            Assert.True(a.SteerDir.Y < -0.99);
        }

        [Fact]
        public void BrakingCommand_IsLeftAloneForAFaintThreat_AndReplacedForAnUrgentOne()
        {
            // 100 m/s at 2 km with 2 m/s^2 of braking: over the sqrt cap, so the profile brakes.
            var faint  = Approach(100, Obstacle(1250, 60, 0, 20));
            var urgent = Approach(100, Obstacle(1900, 5, 0, 20));

            var f = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, faint);
            var u = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, urgent);

            Assert.Equal(0.0, Len(WithoutAvoidance(faint).MoveDirWorld), 9);
            Assert.True(f.Avoid.Urgency > 0 && f.Avoid.Urgency < FlightAvoidance.BrakeOverrideUrgency);
            Assert.Equal(0.0, Len(f.MoveDirWorld), 9);

            Assert.True(u.Avoid.Urgency >= FlightAvoidance.BrakeOverrideUrgency);
            Assert.Equal(1.0, Len(u.MoveDirWorld), 6);
            // Never toward the obstacle: the command either brakes the closure or sidesteps.
            Assert.True(u.MoveDirWorld.X > -0.01);
        }

        // ---- head-on: two grids pick opposite sides ------------------------------------------

        [Fact]
        public void HeadOn_TwoGridsSidestepToOppositeSides()
        {
            // A at x=0 heading +X, B at x=300 heading -X, both 20 m/s, dead-on.
            var a = new FlightSample
            {
                SelfCenterM = new Vec3(0, 0, 0), VelMs = new Vec3(20, 0, 0), SelfRadiusM = SelfR,
                Obstacles = new[] { Obstacle(300, 0, 0, SelfR, -20, 0, 0) }, ObstacleCount = 1,
                AvoidLookaheadSec = Lookahead, AvoidMarginM = Margin,
            };
            var b = new FlightSample
            {
                SelfCenterM = new Vec3(300, 0, 0), VelMs = new Vec3(-20, 0, 0), SelfRadiusM = SelfR,
                Obstacles = new[] { Obstacle(0, 0, 0, SelfR, 20, 0, 0) }, ObstacleCount = 1,
                AvoidLookaheadSec = Lookahead, AvoidMarginM = Margin,
            };

            var ra = FlightAvoidance.Assess(a, null);
            var rb = FlightAvoidance.Assess(b, null);

            Assert.Equal(0, ra.WorstIndex);
            Assert.Equal(0, rb.WorstIndex);

            // Lateral parts (across X) point opposite ways, so the two diverge.
            var latA = new Vec3(0, ra.SteerDir.Y, ra.SteerDir.Z);
            var latB = new Vec3(0, rb.SteerDir.Y, rb.SteerDir.Z);
            Assert.True(Len(latA) > 0.5);
            Assert.True(Dot(latA, latB) < -0.99 * Len(latA) * Len(latB));
        }

        [Fact]
        public void OvertakingAMovingShip_UsesRelativeVelocity()
        {
            // An obstacle ahead moving the same way at the same speed never gets closer.
            var s = Approach(20, Obstacle(1900, 0, 0, 20, -20, 0, 0));
            Assert.Equal(-1, FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s).Avoid.WorstIndex);

            // Coming the other way it closes at 40 m/s, and 100 m is inside the look-ahead.
            var oncoming = Approach(20, Obstacle(1900, 5, 0, 20, 20, 0, 0));
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, oncoming).Avoid.Urgency > 0);
        }

        // ---- the goal is reachable ------------------------------------------------------------

        [Fact]
        public void LargeShipWhoseSphereHoldsTheStopPoint_DoesNotBlockArrival()
        {
            // Grid 150 m from the target, heading in at 5 m/s to a stop point 50 m from the target.
            // A 200 m-radius parked ship centred on the far side of the target: its separation
            // sphere contains the stop point, so it is shrunk to leave the stop point on its surface.
            var s = Approach(5, Obstacle(-170, 0, 0, 200));
            s.PosM = new Vec3(150, 0, 0);
            s.SelfCenterM = new Vec3(150, 0, 0);
            s.StopPointM = new Vec3(50, 0, 0);

            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);
            Assert.Equal(-1, cmd.Avoid.WorstIndex);
            Assert.True(cmd.MoveDirWorld.X < -0.99);
        }

        [Fact]
        public void ObstaclePastTheStopPoint_IsNotAThreat()
        {
            // The grid will be at rest 50 m from the target before it could reach a ship parked
            // on the far side, even though its straight line runs into it.
            var s = Approach(10, Obstacle(-60, 0, 0, 10));
            s.PosM = new Vec3(120, 0, 0);
            s.SelfCenterM = new Vec3(120, 0, 0);
            s.StopPointM = new Vec3(50, 0, 0);

            Assert.Equal(-1, FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s).Avoid.WorstIndex);
        }

        // ---- launch goes through the same term -------------------------------------------------

        [Fact]
        public void Launch_ShipInTheDepartureCorridor_Deflects()
        {
            var s = new FlightSample
            {
                PosM = new Vec3(0, 0, 0), SelfCenterM = new Vec3(0, 0, 0),
                TargetM = new Vec3(80000, 0, 0), VelMs = new Vec3(10, 0, 0),
                CruiseMs = 100, LaunchHandoffMs = 15, LaunchClearanceM = 400,
                SelfRadiusM = SelfR, AvoidLookaheadSec = Lookahead, AvoidMarginM = Margin,
                Obstacles = new[] { Obstacle(60, -8, 0, 20) }, ObstacleCount = 1,
            };

            var cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, s);

            Assert.Equal(0, cmd.Avoid.WorstIndex);
            Assert.True(cmd.MoveDirWorld.Y > 0.1, "sidesteps away from the ship on its -Y side");
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void CountBelowArrayLength_IgnoresStaleEntries()
        {
            // The host reuses its array; an entry past the count is last tick's and must not count.
            var s = Approach(20, Obstacle(1900, 300, 0, 20), Obstacle(1900, 5, 0, 20));
            s.ObstacleCount = 1;
            Assert.Equal(-1, FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s).Avoid.WorstIndex);
        }
    }
}
