using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="FlightAttitude"/>: the attitude a native manoeuvre holds, on SE's
    /// gravity-aligned convention (FAC-215).</summary>
    public class FlightAttitudeTests
    {
        private static readonly Vec3 Down1G = new Vec3(0, -9.81, 0);

        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static double Len(Vec3 v) => Math.Sqrt(Dot(v, v));

        private static void Near(Vec3 expected, Vec3 actual)
        {
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);
            Assert.Equal(expected.Z, actual.Z, 6);
        }

        /// <summary>The live case: a surface launch whose first waypoint is straight overhead. SE's
        /// autopilot climbs level, so the manoeuvre must too — nose keeping its heading, up away from
        /// the planet — rather than pointing the nose at the sky.</summary>
        [Fact]
        public void VerticalBearingInGravity_HoldsLevel_AndKeepsTheHeading()
        {
            AttitudeTarget t = FlightAttitude.Resolve(new Vec3(0, 3000, 0), Down1G, new Vec3(0.8, 0.6, 0));
            Assert.True(t.HasUp);
            Near(new Vec3(0, 1, 0), t.Up);
            Near(new Vec3(1, 0, 0), t.Forward);
        }

        [Fact]
        public void ObliqueBearingInGravity_NoseTakesOnlyTheHorizontalPart()
        {
            AttitudeTarget t = FlightAttitude.Resolve(new Vec3(1000, 1000, 0), Down1G, new Vec3(0, 0, -1));
            Near(new Vec3(1, 0, 0), t.Forward);
            Near(new Vec3(0, 1, 0), t.Up);
        }

        /// <summary>Gravity is radial, so away from a planet's pole it is not a world axis.</summary>
        [Fact]
        public void TiltedGravity_UpIsAwayFromThePlanet_AndTheNoseIsSquareToIt()
        {
            AttitudeTarget t = FlightAttitude.Resolve(new Vec3(0, 0, -500), new Vec3(-3, -4, 0), new Vec3(1, 0, 0));
            Near(new Vec3(0.6, 0.8, 0), t.Up);
            Near(new Vec3(0, 0, -1), t.Forward);
        }

        /// <summary>No heading to keep either: the nose itself points along gravity. Any level nose is
        /// correct; it must still be level and a unit vector.</summary>
        [Fact]
        public void VerticalBearingAndVerticalNose_StillResolvesALevelUnitNose()
        {
            AttitudeTarget t = FlightAttitude.Resolve(new Vec3(0, 1, 0), Down1G, new Vec3(0, 1, 0));
            Assert.Equal(0.0, Dot(t.Forward, t.Up), 6);
            Assert.Equal(1.0, Len(t.Forward), 6);
        }

        [Fact]
        public void ZeroGravity_NoseGoesStraightAtTheBearing_AndTheRollIsLeftAlone()
        {
            AttitudeTarget t = FlightAttitude.Resolve(new Vec3(0, 400, 300), new Vec3(0, 0, 0), new Vec3(1, 0, 0));
            Assert.False(t.HasUp);
            Near(new Vec3(0, 0.8, 0.6), t.Forward);
        }

        [Theory]
        [InlineData(1, 0, 0, 0, -9.81, 0)]
        [InlineData(0.3, -0.9, 0.1, 0, -9.81, 0)]
        [InlineData(5, 2, -7, 1.2, -2.5, 7.1)]
        [InlineData(0, 0, 1, 0, 0, -4.2)]
        public void InGravity_TheResultIsAlwaysOrthonormal(double fx, double fy, double fz, double gx, double gy, double gz)
        {
            AttitudeTarget t = FlightAttitude.Resolve(new Vec3(fx, fy, fz), new Vec3(gx, gy, gz), new Vec3(0, 0, -1));
            Assert.True(t.HasUp);
            Assert.Equal(1.0, Len(t.Forward), 6);
            Assert.Equal(1.0, Len(t.Up), 6);
            Assert.Equal(0.0, Dot(t.Forward, t.Up), 6);
        }
    }
}
