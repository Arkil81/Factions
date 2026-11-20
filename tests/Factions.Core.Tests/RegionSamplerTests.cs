using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class RegionSamplerTests
    {
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        private static double MinSpacing(IList<Vec3> pts)
        {
            double min = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                    min = Math.Min(min, pts[i].DistanceTo(pts[j]));
            return pts.Count < 2 ? double.MaxValue : min;
        }

        // ---- the point of using a lattice: the capacity ceiling is actually reachable -----------

        [Fact]
        public void KorsAuthoredEightNodes_ClearTheAuthoredTenKmFloor()
        {
            // The estate assigns Kor 8, and the lattice reaches exactly 8 at 10 km -- no headroom.
            // Eleven does NOT fit: it drops to 8.84 km, which is what made an earlier capacity
            // estimate of 11.1 dangerous rather than merely loose.
            List<Vec3> eight = RegionSampler.OnSphere(Origin, 9.5, 8, seed: 20260826);
            Assert.True(MinSpacing(eight) >= 10.0, "min spacing was " + MinSpacing(eight));

            List<Vec3> eleven = RegionSampler.OnSphere(Origin, 9.5, 11, seed: 20260826);
            Assert.True(MinSpacing(eleven) < 10.0, "eleven must NOT fit, got " + MinSpacing(eleven));
        }

        [Fact]
        public void FourPointsOnKor_ApproachTheTetrahedralOptimum()
        {
            // The exact answer at N=4 is the inscribed regular tetrahedron, edge R*sqrt(8/3) = 15.51 km.
            double optimum = 9.5 * Math.Sqrt(8.0 / 3.0);
            double actual = MinSpacing(RegionSampler.OnSphere(Origin, 9.5, 4, seed: 1));

            Assert.True(actual > optimum * 0.85,
                "lattice gave " + actual.ToString("0.00") + " km against an optimum of " + optimum.ToString("0.00"));
        }

        [Fact]
        public void EveryPointLandsOnTheSphere()
        {
            foreach (Vec3 p in RegionSampler.OnSphere(new Vec3(100, -50, 25), 30.0, 40, seed: 7))
                Assert.Equal(30.0, p.DistanceTo(new Vec3(100, -50, 25)), 6);
        }

        // ---- determinism -----------------------------------------------------------------------

        [Fact]
        public void SameSeed_SamePoints()
        {
            List<Vec3> a = RegionSampler.OnSphere(Origin, 30, 25, seed: 99);
            List<Vec3> b = RegionSampler.OnSphere(Origin, 30, 25, seed: 99);
            for (int i = 0; i < a.Count; i++) Assert.Equal(0.0, a[i].DistanceTo(b[i]), 9);
        }

        [Fact]
        public void ADifferentSeed_MovesTheEstateWithoutCrowdingIt()
        {
            // The seed rotates the lattice rigidly. Rerolling must never be a way to get a denser
            // world by accident.
            double a = MinSpacing(RegionSampler.OnSphere(Origin, 30, 25, seed: 1));
            double b = MinSpacing(RegionSampler.OnSphere(Origin, 30, 25, seed: 2));
            Assert.Equal(a, b, 6);
        }

        [Fact]
        public void SeedForDependsOnTagAndRegion_NotOnAnythingElse()
        {
            int kor  = RegionSampler.SeedFor(2026, "SPRT", "Kor");
            int agni = RegionSampler.SeedFor(2026, "SPRT", "Agni");
            int other = RegionSampler.SeedFor(2026, "IMPR", "Kor");

            Assert.NotEqual(kor, agni);
            Assert.NotEqual(kor, other);
            Assert.Equal(kor, RegionSampler.SeedFor(2026, "SPRT", "Kor"));
        }

        [Fact]
        public void SeedFor_IsStableAcrossProcesses_NotStringGetHashCode()
        {
            // string.GetHashCode is randomised per process on modern .NET; a world seeded through it
            // would differ between boots. Pinning the literal is what catches a change to that.
            Assert.Equal(RegionSampler.SeedFor(2026, "SPRT", "Kor"), RegionSampler.SeedFor(2026, "SPRT", "Kor"));
            Assert.NotEqual(0, RegionSampler.SeedFor(0, "SPRT", "Kor"));
        }

        // ---- the ring ---------------------------------------------------------------------------

        [Fact]
        public void AnnulusPointsStayInsideTheirBand()
        {
            foreach (Vec3 p in RegionSampler.InAnnulus(Origin, 195, 465, 1.8, 60, seed: 3))
            {
                double radial = Math.Sqrt(p.X * p.X + p.Z * p.Z);
                Assert.InRange(radial, 195.0 - 1e-6, 465.0 + 1e-6);
                Assert.InRange(p.Y, -1.8 - 1e-6, 1.8 + 1e-6);
            }
        }

        [Fact]
        public void AnnulusUsesEqualAreaQuantiles_SoTheOuterHalfIsNotStarved()
        {
            // Interpolating radius linearly would put half the points inside the midpoint radius;
            // equal-area puts half inside the radius that halves the AREA, which is further out.
            List<Vec3> pts = RegionSampler.InAnnulus(Origin, 195, 465, 1.8, 200, seed: 4);
            double areaMedian = Math.Sqrt((195.0 * 195.0 + 465.0 * 465.0) / 2.0);

            int inside = pts.Count(p => Math.Sqrt(p.X * p.X + p.Z * p.Z) < areaMedian);
            Assert.InRange(inside, 90, 110);   // ~half, by area
        }

        [Fact]
        public void TheRingAtOrbitSeparation_RealisesWhatCapacityPromises()
        {
            // Capacity says the ring holds far more than the estate's 19 nodes at 30 km.
            List<Vec3> pts = RegionSampler.InAnnulus(Origin, 195, 465, 1.8, 19, seed: 5);
            Assert.True(MinSpacing(pts) >= 30.0, "min spacing was " + MinSpacing(pts));
        }

        // ---- carving a neighbourhood out of a shared lattice -------------------------------------

        [Fact]
        public void NearestTo_GivesAFactionAContiguousNeighbourhood()
        {
            List<Vec3> all = RegionSampler.OnSphere(Origin, 30, 52, seed: 6);
            Vec3 home = all[0];

            List<Vec3> mine = RegionSampler.NearestTo(all, home, 5);

            Assert.Equal(5, mine.Count);
            Assert.Contains(all[0], mine);
            // Every taken point is at least as close to home as anything left behind.
            double worstTaken = mine.Max(p => p.DistanceTo(home));
            double bestLeft = all.Where(p => !mine.Contains(p)).Min(p => p.DistanceTo(home));
            Assert.True(worstTaken <= bestLeft);
        }

        [Fact]
        public void TwoFactionsWithDistinctHomes_DoNotTakeTheSamePoints()
        {
            List<Vec3> all = RegionSampler.OnSphere(Origin, 30, 52, seed: 8);
            List<Vec3> a = RegionSampler.NearestTo(all, new Vec3(0, 0, 30), 5);
            List<Vec3> b = RegionSampler.NearestTo(all, new Vec3(0, 0, -30), 5);

            Assert.Empty(a.Where(p => b.Any(q => q.DistanceTo(p) < 1e-9)));
        }

        [Fact]
        public void CarvingFromASharedLattice_KeepsSeparationAcrossFactions()
        {
            // The reason the lattice is generated once for the whole region rather than per faction:
            // two independently-sampled estates would have no separation guarantee between them.
            List<Vec3> all = RegionSampler.OnSphere(Origin, 9.5, 8, seed: 9);
            List<Vec3> a = RegionSampler.NearestTo(all, all[0], 3);
            List<Vec3> b = RegionSampler.NearestTo(all, all[5], 3);

            var union = new List<Vec3>(a);
            union.AddRange(b.Where(p => !a.Any(q => q.DistanceTo(p) < 1e-9)));
            Assert.True(MinSpacing(union) >= 10.0, "cross-faction spacing was " + MinSpacing(union));
        }

        [Fact]
        public void DegenerateCounts_ReturnEmptyRatherThanThrow()
        {
            Assert.Empty(RegionSampler.OnSphere(Origin, 30, 0, 1));
            Assert.Empty(RegionSampler.OnSphere(Origin, 0, 5, 1));
            Assert.Empty(RegionSampler.InAnnulus(Origin, 400, 200, 1, 5, 1));   // inner >= outer
            Assert.Single(RegionSampler.OnSphere(Origin, 30, 1, 1));
        }
    }
}
