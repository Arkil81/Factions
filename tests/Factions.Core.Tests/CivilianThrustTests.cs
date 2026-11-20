using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CivilianThrustTests
    {
        private static AxisThrust Axes(double n = 1000)
        {
            return new AxisThrust { RightN = n, LeftN = n, UpN = n, DownN = n, ForwardN = n, BackwardN = n };
        }

        [Fact]
        public void VelocityMatch_CoastsInSpace()
        {
            Vec3 v = new Vec3(15, -3, 2);
            Vec3 f = CivilianThrust.Fractions(v, v, new Vec3(), 100, Axes());
            Assert.Equal(0, f.X); Assert.Equal(0, f.Y); Assert.Equal(0, f.Z);
        }

        [Fact]
        public void SmallCorrection_UsesPartialThrustAndAllowsReversing()
        {
            Vec3 forward = CivilianThrust.Fractions(new Vec3(1, 0, 0), new Vec3(), new Vec3(), 100, Axes());
            Vec3 reverse = CivilianThrust.Fractions(new Vec3(-1, 0, 0), new Vec3(), new Vec3(), 100, Axes());
            Assert.Equal(0.05, forward.X, 8);
            Assert.Equal(-0.05, reverse.X, 8);
            Vec3 brake = CivilianThrust.Fractions(new Vec3(), new Vec3(30, 0, 0), new Vec3(), 100, Axes());
            Assert.Equal(-1, brake.X);
        }

        [Fact]
        public void VelocityMatchInGravity_StillSupportsWeight()
        {
            Vec3 f = CivilianThrust.Fractions(new Vec3(), new Vec3(), new Vec3(0, -9.81, 0), 100, Axes());
            Assert.Equal(0.981, f.Y, 8);
            Assert.Equal(0, f.X); Assert.Equal(0, f.Z);
        }

        [Fact]
        public void DriftCorrection_UsesItsOwnAxisCapacity()
        {
            AxisThrust axes = Axes(); axes.LeftN = 200;
            Vec3 f = CivilianThrust.Fractions(new Vec3(10, 0, 0), new Vec3(12, 1, 0), new Vec3(), 100, axes);
            Assert.Equal(-0.5, f.X, 8); Assert.Equal(-0.05, f.Y, 8);
            axes.LeftN = 0;
            f = CivilianThrust.Fractions(new Vec3(), new Vec3(12, 0, 0), new Vec3(), 100, axes);
            Assert.Equal(0, f.X);
        }

        [Fact]
        public void NearCapNoise_DoesNotToggleFullThrustOrBrake()
        {
            for (int i = 0; i < 20; i++)
            {
                double speed = 10 + (i % 2 == 0 ? 0.01 : -0.01);
                Vec3 f = CivilianThrust.Fractions(new Vec3(10, 0, 0), new Vec3(speed, 0, 0), new Vec3(), 100, Axes());
                Assert.Equal(0, f.X);
            }
        }

        [Fact]
        public void OverrideDeadband_SuppressesSmallWritesButAlwaysClearsAndFullyBrakes()
        {
            Assert.False(CivilianThrust.ShouldWrite(0.4, 0.403));
            Assert.True(CivilianThrust.ShouldWrite(0.4, 0.42));
            Assert.True(CivilianThrust.ShouldWrite(0.002, 0));
            Assert.True(CivilianThrust.ShouldWrite(0.999, 1));
        }

        [Fact]
        public void ApproachCeiling_BudgetsResponseAndFinalSettle()
        {
            double v = CivilianThrust.ApproachSpeedMs(1000, 2, 100);
            Assert.True(v * v / 4 + v * CivilianThrust.ResponseSeconds <= 1000.000001);
            Assert.True(CivilianThrust.ApproachSpeedMs(2, 2, 10) <= 1);
            Assert.Equal(0, CivilianThrust.ApproachSpeedMs(0, 2, 10));
            Assert.Equal(0, CivilianThrust.ApproachSpeedMs(100, 0, 10));
        }

        private static FlightSample Sample(double position, double velocity)
        {
            return new FlightSample {
                AnalogueThrust = true, PosM = new Vec3(position, 0, 0), TargetM = new Vec3(),
                VelMs = new Vec3(velocity, 0, 0), DecelMs2 = 2, CruiseMs = 100,
                StopDistanceM = 50, ArriveToleranceM = 5, MinApproachMs = 5, ArriveSpeedMs = 2
            };
        }

        [Fact]
        public void AnalogueApproach_SettlesWithoutRepeatedFullBurnBrakeFlips()
        {
            double position = 1050, velocity = -100, previous = 0;
            int largeFlips = 0;
            bool arrived = false;
            for (int i = 0; i < 2500; i++)
            {
                FlightSample sample = Sample(position, velocity);
                FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, sample);
                Assert.True(cmd.DesiredVelocityMs.HasValue);
                Assert.Equal(0, cmd.FaceTargetWorld.Value.X); // facing remains at the station
                Vec3 f = CivilianThrust.Fractions(cmd.DesiredVelocityMs.Value, sample.VelMs, new Vec3(), 100, Axes(500), CivilianThrust.ApproachResponseSeconds);
                if (Math.Abs(f.X - previous) > 1) largeFlips++;
                previous = f.X;
                velocity += f.X * 5 * CivilianThrust.UpdateSeconds;
                position += velocity * CivilianThrust.UpdateSeconds;
                Assert.True(position >= 49, "must not burn through the standoff");
                if (cmd.Arrived) { arrived = true; break; }
            }
            Assert.True(arrived);
            Assert.True(Math.Abs(velocity) <= 2);
            Assert.InRange(position, 49, 55.1);
            Assert.True(largeFlips <= 1, "initial brake is allowed; repeated full reversals are not");
        }

        [Fact]
        public void AnalogueLaunch_UsesClearanceBearingWithoutAFacingGate()
        {
            FlightSample sample = Sample(1000, 0);
            sample.LaunchHandoffMs = 15;
            sample.LaunchDirWorld = new Vec3(0, 0, -1);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.Launch, sample);
            Assert.Equal(-100, cmd.DesiredVelocityMs.Value.Z);
            Assert.Equal(0, cmd.DesiredVelocityMs.Value.X);
        }

        [Fact]
        public void Combat_RemainsOnLegacyDirectionPath()
        {
            FlightSample sample = Sample(1000, 0);
            sample.HoldRangeM = 500;
            foreach (ManoeuvreKind kind in new[] { ManoeuvreKind.HoldAtRange, ManoeuvreKind.Orbit,
                ManoeuvreKind.Kite, ManoeuvreKind.AttackRun, ManoeuvreKind.Disengage, ManoeuvreKind.FormUp })
                Assert.False(FlightManoeuvre.Tick(kind, sample).DesiredVelocityMs.HasValue);
        }
    }
}
