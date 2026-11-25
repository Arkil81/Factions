using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class WingLiftTests
    {
        private static readonly Vec3 Up = new Vec3(0, 1, 0);
        private static readonly Vec3 Ahead = new Vec3(1, 0, 0);
        private static readonly Vec3 Gravity = new Vec3(0, -9.81, 0);

        /// <summary><paramref name="count"/> flat WingAngled2 blocks, lifting along +Y in +X airflow.</summary>
        private static List<Wing> Wings(int count)
        {
            var list = new List<Wing>();
            for (int i = 0; i < count; i++)
                list.Add(new Wing { Normal = Up, Chord = Ahead, ForceMul = 1.25 });
            return list;
        }

        private static AxisThrust Twr(double massKg, double twr)
        {
            double n = massKg * 9.81 * twr;
            return new AxisThrust { UpN = n, DownN = n / 2, RightN = n, LeftN = n, ForwardN = n, BackwardN = n };
        }

        [Fact]
        public void OnlyTheModsThreeSubtypesAreWings()
        {
            Wing w;
            Assert.True(WingLift.TryDescribe("WingStreight", Up, Ahead, new Vec3(0, 0, -1), out w));
            Assert.Equal(0.75, w.ForceMul);
            Assert.Equal(1, w.Chord.X, 9);
            Assert.True(WingLift.TryDescribe("WingAngled2", Up, Ahead, new Vec3(0, 0, -1), out w));
            Assert.Equal(1.25, w.ForceMul);
            Assert.Equal(1, Math.Sqrt(w.Chord.X * w.Chord.X + w.Chord.Z * w.Chord.Z), 9);
            Assert.True(w.Chord.Z < 0);
            Assert.False(WingLift.TryDescribe("LargeBlockArmorBlock", Up, Ahead, new Vec3(0, 0, -1), out w));
        }

        [Fact]
        public void AtmosphereRampsFromPointFourToPointSeven()
        {
            Assert.Equal(0, WingLift.Atmosphere(0.3));
            Assert.Equal(0.5, WingLift.Atmosphere(0.55), 9);
            Assert.Equal(1, WingLift.Atmosphere(1.0));
        }

        [Fact]
        public void TheForceOpposesOnlyTheSpeedAcrossTheWing()
        {
            List<Wing> wings = Wings(1);
            Vec3 level = WingLift.ForceN(wings, new Vec3(50, 0, 0), 1);
            Assert.Equal(0, level.Y);
            Vec3 sinking = WingLift.ForceN(wings, new Vec3(50, -2, 0), 1);
            Assert.True(sinking.Y > 0);
            Assert.Equal(0, sinking.X);
            Assert.Equal(1.25 * 2 * (50 * 50 + 4) * 2, sinking.Y, 6);
        }

        [Fact]
        public void TheWingIsInertSlowBackwardsOrInVacuum()
        {
            List<Wing> wings = Wings(1);
            Assert.Equal(0, WingLift.ForceN(wings, new Vec3(4, -2, 0), 1).Y);
            Assert.Equal(0, WingLift.ForceN(wings, new Vec3(-50, -2, 0), 1).Y);
            Assert.Equal(0, WingLift.ForceN(wings, new Vec3(50, -2, 0), 0).Y);
        }

        [Fact]
        public void TheSpeedTermSaturatesAtSeventyOneMetresASecond()
        {
            List<Wing> wings = Wings(1);
            double at80 = WingLift.ForceN(wings, new Vec3(80, -1, 0), 1).Y;
            double at200 = WingLift.ForceN(wings, new Vec3(200, -1, 0), 1).Y;
            Assert.Equal(at80, at200, 6);
            Assert.Equal(1.25 * 10000, at80, 6);
        }

        [Fact]
        public void AtTheTargetTheCounterForceCancelsTheWings()
        {
            List<Wing> wings = Wings(3);
            var v = new Vec3(60, 4, 0);
            Vec3 wing = WingLift.ForceN(wings, v, 0.8);
            Vec3 counter = WingLift.CounterN(wings, v, v, 0.8);
            Assert.Equal(-wing.Y, counter.Y, 6);
            Assert.Equal(0, counter.X);
        }

        /// <summary>Vertical speed over time: the thrust law at 10 Hz, the mod's force each 60 Hz frame,
        /// axis capacity clamped the way the host clamps it.</summary>
        private static List<double> Fly(double massKg, int wingCount, Vec3 start, Vec3 wanted, bool counter,
                                        double seconds = 30)
        {
            List<Wing> wings = Wings(wingCount);
            AxisThrust axes = Twr(massKg, 2);
            Vec3 v = start, fractions = new Vec3(0, 0, 0);
            var vz = new List<double>();
            const double dt = 1.0 / 60;
            for (int frame = 0; frame < seconds * 60; frame++)
            {
                if (frame % 6 == 0)
                {
                    Vec3 extra = counter ? WingLift.CounterN(wings, wanted, v, 1) : new Vec3(0, 0, 0);
                    fractions = CivilianThrust.Fractions(wanted, v, Gravity, massKg, axes,
                        CivilianThrust.ResponseSeconds, extra);
                }
                Vec3 thrust = new Vec3(
                    fractions.X * (fractions.X >= 0 ? axes.RightN : axes.LeftN),
                    fractions.Y * (fractions.Y >= 0 ? axes.UpN : axes.DownN), 0);
                Vec3 wing = WingLift.ForceN(wings, v, 1);
                v = new Vec3(v.X + (thrust.X + wing.X) / massKg * dt,
                             v.Y + ((thrust.Y + wing.Y) / massKg + Gravity.Y) * dt, 0);
                vz.Add(v.Y);
            }
            return vz;
        }

        [Fact]
        public void WithoutTheCounterForceACruisingHullCannotDescend()
        {
            List<double> vz = Fly(100000, 4, new Vec3(71, 0, 0), new Vec3(71, -10, 0), false);
            Assert.True(vz[vz.Count - 1] > -6, "end vz " + vz[vz.Count - 1]);
        }

        [Fact]
        public void WithTheCounterForceACruisingHullDescendsAtTheCommandedRate()
        {
            List<double> vz = Fly(100000, 4, new Vec3(71, 0, 0), new Vec3(71, -10, 0), true);
            Assert.InRange(vz[10 * 60], -10.3, -9.5);
            foreach (double z in vz) Assert.True(z >= -10.3, "overshoot " + z);
        }

        [Fact]
        public void ALaunchFromRestDoesNotOvershootItsClimb()
        {
            List<double> vz = Fly(100000, 4, new Vec3(0, 0, 0), new Vec3(100, 10, 0), true);
            foreach (double z in vz) Assert.True(z <= 10.3, "overshoot " + z);
            Assert.InRange(vz[vz.Count - 1], 9.7, 10.3);
        }

        [Fact]
        public void AnOverWingedHullIsSlowedUntilItsClimbFits()
        {
            const double mass = 5000;
            List<Wing> wings = Wings(16);
            AxisThrust axes = Twr(mass, 2);
            var wanted = new Vec3(100, 10, 0);
            double s = WingLift.FittingScale(wings, wanted, Gravity, mass, axes, 1, 0);
            Assert.InRange(s, 0.05, 0.95);
            Vec3 at = new Vec3(wanted.X * s, wanted.Y * s, 0);
            double need = -(mass * Gravity.Y + WingLift.ForceN(wings, at, 1).Y);
            Assert.True(need <= axes.UpN * 1.0001, "need " + need + " of " + axes.UpN);

            List<double> vz = Fly(mass, 16, new Vec3(0, 0, 0), at, true);
            Assert.InRange(vz[vz.Count - 1], at.Y - 0.3, at.Y + 0.3);
        }

        [Fact]
        public void TheScaleRespectsItsFloorAndLeavesUnwingedOrUnliftableHullsAlone()
        {
            List<Wing> wings = Wings(16);
            var wanted = new Vec3(100, 10, 0);
            Assert.Equal(1, WingLift.FittingScale(new List<Wing>(), wanted, Gravity, 5000, Twr(5000, 2), 1, 0));
            Assert.Equal(1, WingLift.FittingScale(wings, wanted, Gravity, 5000, Twr(5000, 0.5), 1, 0));
            double floored = WingLift.FittingScale(wings, wanted, Gravity, 5000, Twr(5000, 2), 1, 60);
            Assert.Equal(60 / Math.Sqrt(100 * 100 + 10 * 10), floored, 9);
        }
    }
}
