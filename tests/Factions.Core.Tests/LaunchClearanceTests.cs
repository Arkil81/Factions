using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="LaunchClearance"/> — which way a grid leaves its origin when the bearing to
    /// its destination runs through the station it is parked against.
    /// </summary>
    public class LaunchClearanceTests
    {
        private const double ClearanceM = 400.0;
        private const double ProbeR     = 30.0;

        private static readonly Vec3 Origin = new Vec3(0, 0, 0);
        private static readonly Vec3 Target = new Vec3(10000, 0, 0);   // due +X, 10 km out

        private static double Len(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        /// <summary>Probe that blocks any bearing within <paramref name="halfAngleRad"/> of +X —
        /// a station sitting between the grid and where it wants to go.</summary>
        private static Func<Vec3, double, double, bool> BlockCone(double halfAngleRad)
        {
            return (dir, len, rad) => Math.Acos(Math.Min(1.0, Math.Max(-1.0, dir.X))) > halfAngleRad;
        }

        [Fact]
        public void ClearWayOut_TakesTheBearingToTheTargetUntouched()
        {
            LaunchClearanceResult r = LaunchClearance.Resolve(
                Origin, Target, ClearanceM, ProbeR, (dir, len, rad) => true);

            Assert.False(r.Adjusted);
            Assert.False(r.Blocked);
            Assert.Equal(1.0, r.DirWorld.X, 6);
            Assert.Equal(0.0, r.DeflectionRad, 6);
        }

        [Fact]
        public void StationInTheWay_DeflectsAroundIt()
        {
            // Blocked out to 20 degrees: the 15-degree ring cannot clear it, the 30-degree one can.
            LaunchClearanceResult r = LaunchClearance.Resolve(
                Origin, Target, ClearanceM, ProbeR, BlockCone(20.0 * Math.PI / 180.0));

            Assert.True(r.Adjusted);
            Assert.False(r.Blocked);
            Assert.Equal(30.0, r.DeflectionRad * 180.0 / Math.PI, 3);
            Assert.Equal(1.0, Len(r.DirWorld), 6);
        }

        /// <summary>The chosen bearing must actually satisfy the probe it was chosen by — the
        /// failure that would otherwise pass every other assertion here while flying the grid into
        /// the hull anyway.</summary>
        [Fact]
        public void ChosenBearing_SatisfiesTheProbe()
        {
            Func<Vec3, double, double, bool> probe = BlockCone(20.0 * Math.PI / 180.0);
            LaunchClearanceResult r = LaunchClearance.Resolve(Origin, Target, ClearanceM, ProbeR, probe);

            Assert.True(probe(r.DirWorld, ClearanceM, ProbeR));
        }

        [Fact]
        public void PrefersTheLeastDeflectedClearBearing()
        {
            // Only a 15-degree deflection is needed; it must not reach for a wider ring.
            LaunchClearanceResult r = LaunchClearance.Resolve(
                Origin, Target, ClearanceM, ProbeR, BlockCone(5.0 * Math.PI / 180.0));

            Assert.Equal(15.0, r.DeflectionRad * 180.0 / Math.PI, 3);
            // Still heading broadly toward the destination.
            Assert.True(Dot(r.DirWorld, new Vec3(1, 0, 0)) > 0.9);
        }

        [Fact]
        public void BoxedIn_ReportsBlockedAndFallsBackToTheTargetBearing()
        {
            LaunchClearanceResult r = LaunchClearance.Resolve(
                Origin, Target, ClearanceM, ProbeR, (dir, len, rad) => false);

            Assert.True(r.Blocked);
            Assert.False(r.Adjusted);
            Assert.Equal(1.0, r.DirWorld.X, 6);   // status quo: exactly what it did before the pass
        }

        /// <summary>Determinism is what keeps a launch from weaving: the same geometry has to give
        /// the same bearing on every re-probe.</summary>
        [Fact]
        public void SameGeometry_ResolvesToTheSameBearingEveryTime()
        {
            Func<Vec3, double, double, bool> probe = BlockCone(20.0 * Math.PI / 180.0);

            LaunchClearanceResult a = LaunchClearance.Resolve(Origin, Target, ClearanceM, ProbeR, probe);
            LaunchClearanceResult b = LaunchClearance.Resolve(Origin, Target, ClearanceM, ProbeR, probe);

            Assert.Equal(a.DirWorld.X, b.DirWorld.X, 12);
            Assert.Equal(a.DirWorld.Y, b.DirWorld.Y, 12);
            Assert.Equal(a.DirWorld.Z, b.DirWorld.Z, 12);
        }

        [Fact]
        public void EveryCandidateIsAUnitVector()
        {
            var seen = new List<Vec3>();
            LaunchClearance.Resolve(Origin, Target, ClearanceM, ProbeR,
                (dir, len, rad) => { seen.Add(dir); return false; });

            Assert.NotEmpty(seen);
            foreach (Vec3 d in seen) Assert.Equal(1.0, Len(d), 9);
        }

        [Fact]
        public void ProbeCountIsBounded()
        {
            int calls = 0;
            LaunchClearance.Resolve(Origin, Target, ClearanceM, ProbeR,
                (dir, len, rad) => { calls++; return false; });

            Assert.True(calls <= 41, "probe budget exceeded: " + calls);
        }

        [Fact]
        public void ZeroClearance_DisablesThePassEntirely()
        {
            int calls = 0;
            LaunchClearanceResult r = LaunchClearance.Resolve(Origin, Target, 0, ProbeR,
                (dir, len, rad) => { calls++; return false; });

            Assert.Equal(0, calls);
            Assert.False(r.Adjusted);
            Assert.False(r.Blocked);
        }

        [Fact]
        public void TargetOnTopOfTheOrigin_StillProducesADirection()
        {
            LaunchClearanceResult r = LaunchClearance.Resolve(
                Origin, Origin, ClearanceM, ProbeR, (dir, len, rad) => true);

            Assert.Equal(1.0, Len(r.DirWorld), 6);
        }

        /// <summary>The bearing basis must be stable for a target on the +X axis, where
        /// <see cref="LaunchClearance"/>'s perpendicular-basis pick flips.</summary>
        [Theory]
        [InlineData(10000, 0, 0)]
        [InlineData(-10000, 0, 0)]
        [InlineData(0, 10000, 0)]
        [InlineData(0, 0, 10000)]
        [InlineData(5000, 5000, 5000)]
        public void ResolvesAUnitBearingForAnyTargetAxis(double x, double y, double z)
        {
            LaunchClearanceResult r = LaunchClearance.Resolve(
                Origin, new Vec3(x, y, z), ClearanceM, ProbeR,
                (dir, len, rad) => Math.Abs(dir.Y) > 0.4);   // forces a deflection on most axes

            Assert.Equal(1.0, Len(r.DirWorld), 6);
        }
    }
}
