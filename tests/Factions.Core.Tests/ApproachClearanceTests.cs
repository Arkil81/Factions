using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="ApproachClearance"/> — the deterministic stop-point chooser behind the
    /// host's clearance probe. Geometry convention throughout: target at the origin, approach from
    /// +X, so the preferred stop point sits at (+StopDist, 0, 0) between the grid and the target.
    /// </summary>
    public class ApproachClearanceTests
    {
        private const double StopDist = 50.0;
        private const double MaxStandoff = 120.0;
        private const double ProbeRadius = 30.0;

        private static readonly Vec3 Target = new Vec3(0, 0, 0);
        private static readonly Vec3 From = new Vec3(10000, 0, 0);
        private static readonly Vec3 Preferred = new Vec3(StopDist, 0, 0);

        private static ClearanceParams P(double stop = StopDist, double standoff = MaxStandoff,
            double radius = ProbeRadius)
            => new ClearanceParams { StopDistanceM = stop, MaxStandoffM = standoff, ProbeRadiusM = radius };

        private static double Dist(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ---- preferred point -----------------------------------------------------------------

        [Fact]
        public void ClearPreferred_ReturnsItUnchanged()
        {
            ClearanceResult r = ApproachClearance.Resolve(Target, From, P(), (pt, rad) => true);

            Assert.False(r.Adjusted);
            Assert.False(r.Blocked);
            Assert.True(Dist(r.StopPointM, Preferred) < 1e-9);
        }

        [Fact]
        public void ClearPreferred_ProbesExactlyOnce()
        {
            int calls = 0;
            ApproachClearance.Resolve(Target, From, P(), (pt, rad) => { calls++; return true; });

            Assert.Equal(1, calls);
        }

        [Fact]
        public void ProbeRadius_IsPassedThroughUnchanged()
        {
            var radii = new List<double>();
            ApproachClearance.Resolve(Target, From, P(), (pt, rad) => { radii.Add(rad); return false; });

            Assert.All(radii, rad => Assert.Equal(ProbeRadius, rad));
        }

        // ---- adjustment ----------------------------------------------------------------------

        [Fact]
        public void BlockedPreferred_PicksNearestLatticeCandidate()
        {
            // Only the immediate vicinity of the preferred point is occupied; the nearest lattice
            // candidates sit one step (= probe radius) away and must win over anything further.
            Func<Vec3, double, bool> isClear = (pt, rad) => Dist(pt, Preferred) > 1.0;

            ClearanceResult r = ApproachClearance.Resolve(Target, From, P(), isClear);

            Assert.True(r.Adjusted);
            Assert.False(r.Blocked);
            Assert.True(isClear(r.StopPointM, ProbeRadius));
            Assert.True(Math.Abs(Dist(r.StopPointM, Preferred) - ProbeRadius) < 1e-6);
        }

        [Fact]
        public void ChosenPoint_IsAlwaysOneTheProbeCalledClear()
        {
            // Occupancy pattern with no geometric meaning, but deterministic: roughly half the
            // candidates read blocked depending on their coordinates.
            Func<Vec3, double, bool> isClear =
                (pt, rad) => ((long)Math.Floor(pt.X + pt.Y * 3 + pt.Z * 7) & 1) == 0;

            ClearanceResult r = ApproachClearance.Resolve(Target, From, P(), isClear);

            if (!r.Blocked) Assert.True(isClear(r.StopPointM, ProbeRadius));
        }

        // ---- envelope ------------------------------------------------------------------------

        [Fact]
        public void EveryProbedCandidate_StaysInsideTheEnvelope()
        {
            var probed = new List<Vec3>();
            ApproachClearance.Resolve(Target, From, P(), (pt, rad) => { probed.Add(pt); return false; });

            Assert.All(probed, pt =>
            {
                double fromTarget = Dist(pt, Target);
                Assert.True(fromTarget <= StopDist + MaxStandoff + 1e-6,
                    "candidate " + fromTarget.ToString("F1") + "m from target exceeds the envelope");
                Assert.True(fromTarget >= StopDist - 1e-6,
                    "candidate " + fromTarget.ToString("F1") + "m from target is inside the stop radius");
            });
        }

        [Fact]
        public void ProbeCount_IsBounded()
        {
            int calls = 0;
            ApproachClearance.Resolve(Target, From, P(), (pt, rad) => { calls++; return false; });

            Assert.InRange(calls, 2, 32);   // preferred + lattice, hard-capped
        }

        [Fact]
        public void AllBlocked_FallsBackToPreferredAndSaysSo()
        {
            ClearanceResult r = ApproachClearance.Resolve(Target, From, P(), (pt, rad) => false);

            Assert.True(r.Blocked);
            Assert.False(r.Adjusted);
            Assert.True(Dist(r.StopPointM, Preferred) < 1e-9);
        }

        [Fact]
        public void StandoffSmallerThanStep_LeavesNoCandidates_FallsBack()
        {
            // A probe radius larger than the whole allowed standoff means no lattice point fits
            // the envelope; a blocked preferred point must fall back, not probe outside it.
            int calls = 0;
            ClearanceResult r = ApproachClearance.Resolve(Target, From,
                P(standoff: 20, radius: 30), (pt, rad) => { calls++; return false; });

            Assert.True(r.Blocked);
            Assert.Equal(1, calls);
        }

        // ---- determinism ---------------------------------------------------------------------

        [Fact]
        public void SameOccupancy_SameResult()
        {
            Func<Vec3, double, bool> isClear =
                (pt, rad) => ((long)Math.Floor(pt.X + pt.Y * 3 + pt.Z * 7) & 1) == 0
                             && Dist(pt, Preferred) > 1.0;

            ClearanceResult a = ApproachClearance.Resolve(Target, From, P(), isClear);
            ClearanceResult b = ApproachClearance.Resolve(Target, From, P(), isClear);

            Assert.Equal(a.Adjusted, b.Adjusted);
            Assert.Equal(a.Blocked, b.Blocked);
            Assert.True(Dist(a.StopPointM, b.StopPointM) < 1e-9);
        }

        [Fact]
        public void NearerCandidate_BeatsFartherOne()
        {
            // Everything within two steps of the preferred point is blocked; the chooser must
            // settle on deviation two-steps-ish, never something at the envelope edge.
            Func<Vec3, double, bool> isClear = (pt, rad) => Dist(pt, Preferred) > ProbeRadius + 1.0;

            ClearanceResult r = ApproachClearance.Resolve(Target, From, P(), isClear);

            Assert.True(r.Adjusted);
            double dev = Dist(r.StopPointM, Preferred);
            Assert.True(dev <= 2 * ProbeRadius + 1e-6,
                "chose deviation " + dev.ToString("F1") + "m when a nearer clear candidate existed");
        }

        // ---- degenerate geometry -------------------------------------------------------------

        [Fact]
        public void FromEqualsTarget_StillStandsOffSomewhere()
        {
            ClearanceResult r = ApproachClearance.Resolve(Target, Target, P(), (pt, rad) => true);

            Assert.False(r.Blocked);
            Assert.True(Math.Abs(Dist(r.StopPointM, Target) - StopDist) < 1e-9);
        }

        [Fact]
        public void VerticalApproach_LatticeStaysPerpendicular()
        {
            // Approach along +Y exercises the basis-selection branch (|dir.X| < 0.9 picks the X
            // reference axis). Block the axial line; the chosen point must carry a lateral
            // component perpendicular to Y, still inside the envelope.
            var fromAbove = new Vec3(0, 10000, 0);
            Func<Vec3, double, bool> isClear = (pt, rad) => Math.Abs(pt.X) + Math.Abs(pt.Z) > 1.0;

            ClearanceResult r = ApproachClearance.Resolve(Target, fromAbove, P(), isClear);

            Assert.True(r.Adjusted);
            Assert.True(isClear(r.StopPointM, ProbeRadius));
            Assert.True(Dist(r.StopPointM, Target) <= StopDist + MaxStandoff + 1e-6);
        }
    }
}
