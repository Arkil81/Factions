using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The capacity estimate exists to make an over-subscribed region a refusal instead of a silent
    /// separation violation, so the tests that matter are the ones pinned to real geometry rather
    /// than to the formula.
    /// </summary>
    public class PlacementCapacityTests
    {
        // Live bodies (planets.json, awl-factions-genesis).
        private const double KorRadiusKm    = 9.5;
        private const double AgniRadiusKm   = 15.0;
        private const double TellusRadiusKm = 30.0;

        private static PlacementRegionGeometry Surface(double r, bool allows = true)
        {
            return new PlacementRegionGeometry { SurfaceRadiusKm = r, AllowsSurface = allows };
        }

        // ---- pinned against exact spherical codes ---------------------------------------------

        [Fact]
        public void Kor_AtThe15kmFloor_HoldsThree_WhichIsBelowTheTetrahedralOptimum()
        {
            // The OPTIMUM is four: a regular tetrahedron inscribed in a 9.5 km sphere has edge
            // 15.51 km. The lattice we actually place with reaches only three at that floor, and
            // capacity reports what gets built rather than what could exist.
            double tetrahedronEdge = KorRadiusKm * Math.Sqrt(8.0 / 3.0);
            Assert.True(tetrahedronEdge > 15.0, "the optimum really is four");

            Assert.Equal(3.0, PlacementCapacity.OnSphere(KorRadiusKm, 15.0));
        }

        [Fact]
        public void Agni_AtThe15kmFloor_HoldsFewerThanTheIcosahedralOptimum()
        {
            // Optimum is twelve (icosahedron, edge 1.05146*R = 15.77 km). The lattice reaches fewer.
            double icosahedronEdge = AgniRadiusKm * 4.0 / Math.Sqrt(10.0 + 2.0 * Math.Sqrt(5.0));
            Assert.True(icosahedronEdge > 15.0);

            double capacity = PlacementCapacity.OnSphere(AgniRadiusKm, 15.0);
            Assert.InRange(capacity, 8.0, 12.0);
        }

        [Fact]
        public void TheOperatorsSeparationCut_MoreThanDoublesKor_ButNotToEleven()
        {
            // The cut from 15 km to 10 km was made to fit eleven claimants on Kor. It does not:
            // the lattice reaches 8 at 10 km. The authored estate assigns Kor exactly 8 nodes, so it
            // sits precisely at the achievable limit with no headroom -- worth knowing before anyone
            // adds a ninth.
            double atFifteen = PlacementCapacity.OnSphere(KorRadiusKm, 15.0);
            double atTen     = PlacementCapacity.OnSphere(KorRadiusKm, 10.0);

            Assert.True(atTen > atFifteen * 2, "the cut must materially help");
            Assert.Equal(8.0, atTen);
        }

        [Fact]
        public void Tellus_At15km_HoldsFewerThanTheAuthoredEstateAsksFor()
        {
            // The authored estate puts 40 nodes on Tellus. The lattice reaches 38 at the 15 km floor,
            // so at that floor the estate over-subscribes it by two -- caught here rather than as
            // silent separation violations at seed time. An area estimate said 52 and hid this.
            Assert.Equal(38.0, PlacementCapacity.OnSphere(TellusRadiusKm, 15.0));
        }

        [Fact]
        public void Tellus_AtItsAuthored14km_HoldsTheFortyNodeEstate()
        {
            // world_recipe.json authors Tellus at 14 km so ten four-node power bases fit.
            Assert.Equal(43.0, PlacementCapacity.OnSphere(TellusRadiusKm, 14.0));
        }

        // ---- rules, not geometry ----------------------------------------------------------------

        [Fact]
        public void AnUnlandableBody_HasZeroSurfaceCapacity_WhateverItsRadius()
        {
            Assert.Equal(0, PlacementCapacity.MaxNodes(
                PlacementRegionKind.PlanetSurface, Surface(TellusRadiusKm, allows: false), 15.0));
        }

        [Fact]
        public void NonPositiveSeparation_IsZero_NotUnbounded()
        {
            // "No floor" must never read as "infinite room" -- that is the silent-overflow shape.
            Assert.Equal(0, PlacementCapacity.MaxNodes(PlacementRegionKind.PlanetSurface, Surface(30), 0));
            Assert.Equal(0, PlacementCapacity.MaxNodes(PlacementRegionKind.PlanetSurface, Surface(30), -5));
        }

        [Fact]
        public void ASeparationWiderThanTheBody_LeavesRoomForExactlyOne()
        {
            Assert.Equal(1, PlacementCapacity.OnSphere(KorRadiusKm, 100.0));
        }

        [Fact]
        public void CapacityIsWhatTheSamplerBuilds_NotAnEstimateBesideIt()
        {
            // The invariant that keeps the planner and the seeder from drifting: whatever capacity
            // reports, the sampler must actually place at that count without breaking the floor.
            foreach (double sep in new[] { 10.0, 15.0, 20.0 })
                foreach (double radius in new[] { 9.5, 15.0, 30.0 })
                {
                    int n = (int)PlacementCapacity.OnSphere(radius, sep);
                    var pts = RegionSampler.OnSphere(new Vec3(0, 0, 0), radius, n, seed: 0);
                    double min = double.MaxValue;
                    for (int i = 0; i < pts.Count; i++)
                        for (int j = i + 1; j < pts.Count; j++)
                            min = Math.Min(min, pts[i].DistanceTo(pts[j]));
                    if (pts.Count > 1)
                        Assert.True(min >= sep,
                            "R=" + radius + " sep=" + sep + " capacity=" + n + " but min spacing " + min);
                }
        }

        [Fact]
        public void CapacityFallsAsTheFloorRises_Monotonically()
        {
            double prev = double.MaxValue;
            foreach (double sep in new[] { 5.0, 10.0, 15.0, 20.0, 25.0 })
            {
                double now = PlacementCapacity.OnSphere(TellusRadiusKm, sep);
                Assert.True(now < prev, "capacity must fall as separation rises (sep=" + sep + ")");
                prev = now;
            }
        }

        // ---- the ring, and the separation change that removes its cliff ------------------------

        [Fact]
        public void TheRing_AtOrbitSeparation_HoldsFarMoreThanTheEstateNeeds()
        {
            // Live bands, outermost span: 195-465 km, half-thickness 1.8 km.
            var ring = new PlacementRegionGeometry
            {
                InnerRadiusKm = 195.0, OuterRadiusKm = 465.0, HalfThicknessKm = 1.8,
            };
            Assert.True(PlacementCapacity.MaxNodes(PlacementRegionKind.Feature, ring, 30.0) > 100.0);
        }

        [Fact]
        public void TheRing_AtTheDeepSpaceFloor_HoldsTwoOrdersOfMagnitudeFewer()
        {
            // Why the ring could not be left on the 500 km floor. Stated as a ratio against the
            // authored 30 km rather than an absolute: the absolute depends on how much of the
            // annulus the nine bands actually occupy, the ratio does not.
            var ring = new PlacementRegionGeometry
            {
                InnerRadiusKm = 195.0, OuterRadiusKm = 465.0, HalfThicknessKm = 1.8,
            };

            double authored = PlacementCapacity.MaxNodes(PlacementRegionKind.Feature, ring, 30.0);
            double deepSpace = PlacementCapacity.MaxNodes(PlacementRegionKind.Feature, ring, 500.0);

            Assert.True(deepSpace < 5.0, "a 500 km floor leaves the ring essentially empty, got " + deepSpace);
            Assert.True(authored / deepSpace > 100.0, "ratio was " + (authored / deepSpace));
        }

        [Fact]
        public void AThinRing_PacksAsAPlane_NotAsAPaddedVolume()
        {
            // Regression on a real bug in this class: clamping the thickness up to the separation
            // treated a 3.6 km ring as 500 km thick and inflated capacity ~139x. A disc thinner than
            // one exclusion sphere holds exactly one layer.
            var ring = new PlacementRegionGeometry
            {
                InnerRadiusKm = 195.0, OuterRadiusKm = 465.0, HalfThicknessKm = 1.8,
            };

            double annulusArea = Math.PI * (465.0 * 465.0 - 195.0 * 195.0);
            double singleLayer = annulusArea / (Math.PI * 250.0 * 250.0) * PlacementCapacity.PlanarPackingEfficiency;

            Assert.Equal(singleLayer, PlacementCapacity.InDisc(195.0, 465.0, 1.8, 500.0), 6);
        }

        [Fact]
        public void AThickDisc_StillPacksByVolume()
        {
            // The other side of the branch: once the disc is thicker than the separation it holds
            // more than one layer and area packing would under-count it.
            double thick = PlacementCapacity.InDisc(0, 100, 200, 10.0);   // 400 km thick, 10 km floor
            double thin  = PlacementCapacity.InDisc(0, 100, 1, 10.0);     // 2 km thick, same floor
            Assert.True(thick > thin * 10, "a thick disc must hold many layers");
        }

        [Fact]
        public void AFeature_AtFiftyKm_IsNotItsOwnBudget()
        {
            // The reason an explicit node count is mandatory: at the old 500 km floor a 200 km
            // feature bounded the estate by geometry; at 50 km it does not.
            var feature = new PlacementRegionGeometry { OuterRadiusKm = 200.0 };
            Assert.True(PlacementCapacity.MaxNodes(PlacementRegionKind.Feature, feature, 500.0) < 1.0);
            Assert.True(PlacementCapacity.MaxNodes(PlacementRegionKind.Feature, feature, 50.0) > 100.0);
        }

        [Fact]
        public void DeepSpace_ReportsUnbounded_RatherThanInventingANumber()
        {
            Assert.True(double.IsPositiveInfinity(PlacementCapacity.MaxNodes(
                PlacementRegionKind.DeepSpace, new PlacementRegionGeometry(), 500.0)));
        }
    }
}
