using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-101: a deposit's composition is surveyed over time rather than sampled once.
    /// Two properties here would fail <b>silently</b> if broken — the lattice moving under an
    /// accumulating survey, and shares not being derived — so both are pinned directly.</summary>
    public class DepositSurveyTests
    {
        // ---- the progressive lattice: the part that silently does not work if it is wrong --------

        [Fact]
        public void LatticePoint_IsFixed_HoweverManyPointsFollow()
        {
            // The defect this exists to stop: the old sunflower computed rho from (i+0.5)/N, so
            // growing the sample moved every point already probed. A survey accumulating across a
            // growing N would then re-probe moved ground forever while looking like it ran.
            for (long i = 0; i < 50; i++)
            {
                double x1, y1, x2, y2;
                ProgressiveLattice.PointOnDisc(i, 1.0, out x1, out y1);
                ProgressiveLattice.PointOnDisc(i, 1.0, out x2, out y2);
                Assert.Equal(x1, x2, 12);
                Assert.Equal(y1, y2, 12);
            }
        }

        [Fact]
        public void LatticePoints_AreDistinct_SoASurveyCoversNewGround()
        {
            var seen = new HashSet<string>();
            for (long i = 0; i < 2000; i++)
            {
                double x, y;
                ProgressiveLattice.PointOnDisc(i, 1000.0, out x, out y);
                seen.Add(x.ToString("0.000") + "," + y.ToString("0.000"));
            }
            // Allow a little collision from rounding to millimetres; the point is that the sequence
            // does not cycle or collapse.
            Assert.True(seen.Count > 1950, "expected ~2000 distinct points, got " + seen.Count);
        }

        [Fact]
        public void LatticePrefix_IsSpreadOverTheDisc_NotClusteredAtTheCentre()
        {
            // Any prefix must be usable, because a survey is always a prefix. Equal-area means about
            // half the points fall inside r/sqrt(2).
            int inner = 0, n = 500;
            for (long i = 0; i < n; i++)
            {
                double x, y;
                ProgressiveLattice.PointOnDisc(i, 1.0, out x, out y);
                if (System.Math.Sqrt(x * x + y * y) <= 1.0 / System.Math.Sqrt(2.0)) inner++;
            }
            Assert.InRange(inner / (double)n, 0.42, 0.58);
        }

        [Fact]
        public void LatticePointsStayInsideTheDisc()
        {
            for (long i = 0; i < 1000; i++)
            {
                double x, y;
                ProgressiveLattice.PointOnDisc(i, 7.5, out x, out y);
                Assert.True(System.Math.Sqrt(x * x + y * y) <= 7.5 + 1e-9);
            }
        }

        // ---- accumulation ------------------------------------------------------------------------

        private static DepositEntry E(string ore, double weight) { return new DepositEntry(ore, weight, weight); }

        [Fact]
        public void Accumulate_AddsToExistingOres_RatherThanReplacingThem()
        {
            var d = new NodeDeposit();
            d.Accumulate(new[] { E("Iron", 10), E("Gold", 10) }, 100);
            d.Accumulate(new[] { E("Iron", 30) }, 100);

            Assert.Equal(200, d.PointsExamined);
            Assert.Equal(0.8, d.ShareOf("Iron"), 9);
            Assert.Equal(0.2, d.ShareOf("Gold"), 9);
        }

        [Fact]
        public void Accumulate_AppendsAnOreTheFirstStepNeverSaw()
        {
            var d = new NodeDeposit();
            d.Accumulate(new[] { E("Iron", 10) }, 100);
            d.Accumulate(new[] { E("Platinum", 10) }, 100);

            Assert.Equal(2, d.Entries.Count);
            Assert.Equal(0.5, d.ShareOf("Platinum"), 9);
        }

        [Fact]
        public void SharesAreDerived_SoTheyAlwaysSumToOne_HoweverManyStepsRan()
        {
            var d = new NodeDeposit();
            for (int step = 0; step < 7; step++)
                d.Accumulate(new[] { E("Iron", 3), E("Nickel", 2), E("Gold", 1) }, 50);

            double sum = 0;
            foreach (DepositEntry e in d.Entries) sum += d.ShareOf(e.SubTypeId);
            Assert.Equal(1.0, sum, 9);
            Assert.Equal(0.5, d.ShareOf("Iron"), 9);
        }

        [Fact]
        public void DominantOre_CanChange_AsASurveyRefinesTheMix()
        {
            // The reason surveying is worth doing at all: a first sample can be unrepresentative,
            // and later evidence must be able to overturn it.
            var d = new NodeDeposit();
            d.Accumulate(new[] { E("Iron", 10), E("Gold", 8) }, 100);
            Assert.Equal("Iron", DepositModel.DominantOre(d));

            d.Accumulate(new[] { E("Gold", 40) }, 400);
            Assert.Equal("Gold", DepositModel.DominantOre(d));
        }

        [Fact]
        public void Accumulate_IgnoresZeroAndEmptyObservations()
        {
            var d = new NodeDeposit();
            d.Accumulate(new[] { E("Iron", 10), E("", 5), E("Gold", 0) }, 100);

            Assert.Single(d.Entries);
            Assert.Equal(1.0, d.ShareOf("Iron"), 9);
        }

        // ---- the stopping rule -------------------------------------------------------------------

        [Fact]
        public void PointsToResolve_RisesSteeplyAsTheShareFalls()
        {
            // This relation is why the survey is paced rather than taken at once, so the shape is
            // worth pinning: a 1% share costs roughly five times a 5% one.
            long at5 = SurveyMath.PointsToResolve(0.05);
            long at1 = SurveyMath.PointsToResolve(0.01);

            Assert.InRange(at5, 5000, 5300);
            Assert.InRange(at1, 26000, 27500);
            Assert.True(at1 > at5 * 4);
        }

        [Fact]
        public void ShareFloor_IsTheInverseOfPointsToResolve()
        {
            // One relation exposed both ways: "may the survey stop" and "how well surveyed is this"
            // must never disagree about the same node.
            foreach (double share in new[] { 0.5, 0.2, 0.05, 0.01 })
            {
                long n = SurveyMath.PointsToResolve(share);
                Assert.True(SurveyMath.ShareFloorFor(n) <= share + 1e-9,
                    "n for " + share + " should resolve it, floor was " + SurveyMath.ShareFloorFor(n));
                Assert.False(SurveyMath.IsSurveyed(n - 1, share));
                Assert.True(SurveyMath.IsSurveyed(n, share));
            }
        }

        [Fact]
        public void AnUnsurveyedDeposit_ClaimsToResolveNothing()
        {
            // The honest answer before any evidence, rather than a flattering one.
            Assert.Equal(1.0, new NodeDeposit().ResolvedShareFloor, 9);
        }

        // ---- the catchment: read from the separation actually in force ---------------------------

        private sealed class OneBody : IPlanetCatalogue
        {
            public readonly List<PlanetRecord> Records = new List<PlanetRecord>();
            public IEnumerable<PlanetRecord> Planets { get { return Records; } }
            public double GravityAt(Vec3 posKm) { return 1.0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static WorldModel WorldWithBody(double? authoredSeparationKm)
        {
            var world = new WorldModel(new SeededRandom(7)) { Tuning = new FactionTuning() };
            world.Tuning.NodeSeparationSurfaceKm = 15.0;
            var cat = new OneBody();
            cat.Records.Add(new PlanetRecord
            {
                Id = "1", Name = "Kor", Generator = "Kor",
                Center = new Vec3(0, 0, 0), SurfaceRadius = 30, GravityRadius = 60,
                NodeSeparationKm = authoredSeparationKm,
            });
            world.Planets = cat;
            return world;
        }

        /// <summary>The defect this exists to stop, found 2026-08-26: the catchment was read from
        /// <c>FactionTuning.NodeSeparationFor(zone)</c>, which is the global fallback. Per-body
        /// separation arrived as a new lookup rather than a change to that one, so the call site kept
        /// compiling and kept returning 7.5 km for a body the world packs at 10.</summary>
        [Fact]
        public void Catchment_ReadsTheBodysAuthoredSeparation_NotTheGlobalDefault()
        {
            WorldModel world = WorldWithBody(10.0);

            Assert.Equal(5.0, world.OreSampleRadiusKmAt(new Vec3(0, 29, 0)), 9);
        }

        [Fact]
        public void Catchment_FallsBackToTheZoneDefault_WhereNothingIsAuthored()
        {
            WorldModel world = WorldWithBody(null);

            Assert.Equal(7.5, world.OreSampleRadiusKmAt(new Vec3(0, 29, 0)), 9);
        }

        [Fact]
        public void Catchment_IsAlwaysHalfWhateverSeparationApplies()
        {
            // The whole reason the fraction is a half: a node owns the ground nearer to it than to
            // any neighbour, so two catchments one separation apart touch and do not overlap.
            foreach (double separation in new[] { 8.0, 10.0, 15.0, 30.0 })
            {
                WorldModel world = WorldWithBody(separation);
                Assert.Equal(separation, world.OreSampleRadiusKmAt(new Vec3(0, 29, 0)) * 2.0, 9);
            }
        }

        [Fact]
        public void Catchment_FallsBackWhenASeparationIsMissingOrNonsense()
        {
            // A malformed configuration must not sample a zero-radius disc: that reads barren
            // everywhere, and a barren node holds a deposit, so nothing would ever revisit it.
            var t = new FactionTuning();
            t.OreSampleRadiusKm = 7.5;

            Assert.Equal(7.5, t.OreSampleRadiusForSeparation(0.0), 9);
            Assert.Equal(7.5, t.OreSampleRadiusForSeparation(-1.0), 9);

            t.OreSampleSeparationFraction = 0.0;
            Assert.Equal(7.5, t.OreSampleRadiusForSeparation(15.0), 9);
        }

        [Fact]
        public void ShareFloorImproves_MonotonicallyWithPoints()
        {
            double previous = 1.0;
            foreach (long n in new long[] { 1, 10, 100, 1000, 10000, 100000 })
            {
                double floor = SurveyMath.ShareFloorFor(n);
                Assert.True(floor < previous, "floor must improve with more points");
                previous = floor;
            }
        }
    }
}
