using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>How much ore is under a node, as against what the ground is worth.
    ///
    /// <para>The pair exists because the two disagree. Measured over the 654 well-surveyed nodes of
    /// the live estate 2026-08-31, deposit density is nearly flat — rare seams run ~8 band-metres per
    /// probe against iron's and ice's ~12 — while ResourceValue spans 0.18 to 0.91 on the same nodes.
    /// Every bit of that spread is ore price. See docs/issues/FAC-219.</para></summary>
    public class NodeRichnessTests
    {
        /// <summary>A node whose deposit measures <paramref name="bandMetresPerProbe"/> per probe
        /// after <paramref name="points"/> probes — the two numbers RichnessFor reads.</summary>
        private static SpatialNode Measured(double bandMetresPerProbe, long points)
        {
            var n = new SpatialNode { Id = "N1", OwnerFactionTag = "F", HoldStrength = 1.0 };
            n.Deposit = new NodeDeposit { PointsExamined = points };
            // Richness accumulates as hits x band depth, so the per-probe rate is the total over the
            // points examined: state the total that produces the rate this fixture wants.
            n.Deposit.Entries.Add(new DepositEntry("Iron", points, bandMetresPerProbe * points));
            return n;
        }

        private static long PointsForFullSurvey(FactionTuning t)
        {
            return SurveyMath.PointsToResolve(t.SurveyTargetShare);
        }

        // ---- the gate ---------------------------------------------------------------------------

        /// <summary>Ore has to be there before it can be mined. Both no-deposit states read zero, and
        /// they read zero rather than the floor on purpose: <see cref="Economy.MintsNothing"/> reads
        /// this, so a floor granted to every deposit-less node would open a mint at each one.</summary>
        [Fact]
        public void GroundWithNoOre_ReadsZero_WhetherUnsampledOrProvenBarren()
        {
            FactionTuning t = TestSupport.Tuning();

            var unsampled = new SpatialNode { Id = "N1" };                       // seam declined: no deposit at all
            var barren = new SpatialNode { Id = "N2" };
            barren.Deposit = new NodeDeposit { PointsExamined = 500000 };        // looked hard, found nothing

            Assert.Equal(0.0, NodeContent.RichnessFor(unsampled, t));
            Assert.Equal(0.0, NodeContent.RichnessFor(barren, t));
            Assert.True(NodeContent.IsUnsampled(unsampled));
            Assert.False(NodeContent.IsUnsampled(barren));   // the two states are distinct...
            Assert.False(NodeContent.IsMineable(barren));    // ...and neither is mineable
        }

        // ---- the survey drags it upward ---------------------------------------------------------

        /// <summary>Ore under it but nobody has measured it: the floor, because unproven is not the
        /// same as poor.</summary>
        [Fact]
        public void OreUnderIt_ButNothingExamined_ReadsTheFloor()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = Measured(12.0, points: 0);
            n.Deposit.Entries.Clear();
            n.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));   // ore present, zero probes

            Assert.Equal(t.RichnessFloor, NodeContent.RichnessFor(n, t), 9);
        }

        /// <summary>Thick ground rises off the floor as it is surveyed and never falls back. This is
        /// the direction ResourceValue gets wrong for ice, whose proven worth (0.181) sits under its
        /// own 0.2 floor, so ordinary ground there gets worse the more you look at it.</summary>
        [Fact]
        public void OrdinaryGround_RisesMonotonically_AsTheSurveyProgresses()
        {
            FactionTuning t = TestSupport.Tuning();
            long full = PointsForFullSurvey(t);

            double previous = t.RichnessFloor;
            foreach (double share in new[] { 0.05, 0.25, 0.50, 0.75, 1.00 })
            {
                double r = NodeContent.RichnessFor(Measured(12.0, (long)(full * share)), t);
                Assert.True(r > previous,
                    "12 band-metres is ordinary ground and must climb off the floor, not sink into it; "
                    + "at " + share + " of the survey it read " + r + " against " + previous);
                previous = r;
            }
            // and it lands on what the ground measures, not on the floor it started from
            Assert.Equal(12.0 / t.RichnessSaturationMetresPerProbe, previous, 9);
        }

        /// <summary>Ground proven thin settles BELOW the floor. The floor means "we have not looked",
        /// not "nothing is ever worse than this" — live, the 23-ore mixed nodes measure ~1
        /// band-metre per probe against ordinary ground's 8-17.</summary>
        [Fact]
        public void GroundProvenThin_SettlesBelowTheFloor()
        {
            FactionTuning t = TestSupport.Tuning();
            double thin = NodeContent.RichnessFor(Measured(1.0, PointsForFullSurvey(t)), t);

            Assert.True(thin < t.RichnessFloor, "proven-thin ground must read below the unproven floor");
            Assert.True(thin > 0, "thin is not barren: there is still ore there");
            Assert.Equal(1.0 / t.RichnessSaturationMetresPerProbe, thin, 9);
        }

        // ---- the reading itself -----------------------------------------------------------------

        /// <summary>Linear in measured density, not logarithmic. The live input spans one and a half
        /// decades (0.63 to 21.29 band-metres per probe) with 85% of the mass between 8 and 17, so a
        /// log map would flatten exactly the range that has to discriminate.</summary>
        [Fact]
        public void ItIsLinearInDensity_NotCompressive()
        {
            FactionTuning t = TestSupport.Tuning();
            long full = PointsForFullSurvey(t);

            double at8 = NodeContent.RichnessFor(Measured(8.0, full), t);
            double at12 = NodeContent.RichnessFor(Measured(12.0, full), t);
            double at16 = NodeContent.RichnessFor(Measured(16.0, full), t);

            Assert.Equal(at12 - at8, at16 - at12, 9);   // equal density steps, equal richness steps
            Assert.Equal(1.5, at12 / at8, 9);           // and the ratio is the density ratio
        }

        /// <summary>Saturation is the world's ore-abundance knob: lower makes the same ground read
        /// richer. The one an operator turns.</summary>
        [Fact]
        public void LoweringSaturation_MakesTheSameGroundRicher()
        {
            FactionTuning lean = TestSupport.Tuning();
            FactionTuning generous = TestSupport.Tuning();
            lean.RichnessSaturationMetresPerProbe = 30.0;
            generous.RichnessSaturationMetresPerProbe = 16.0;

            long full = PointsForFullSurvey(lean);
            Assert.True(NodeContent.RichnessFor(Measured(12.0, full), generous)
                      > NodeContent.RichnessFor(Measured(12.0, full), lean));
        }

        /// <summary>Ground richer than the saturation point clamps at the cap rather than running
        /// past 1.0, which every yield path would then read as a multiplier above unity.</summary>
        [Fact]
        public void GroundRicherThanSaturation_ClampsAtTheCap()
        {
            FactionTuning t = TestSupport.Tuning();
            double r = NodeContent.RichnessFor(Measured(t.RichnessSaturationMetresPerProbe * 4, PointsForFullSurvey(t)), t);
            Assert.Equal(t.RichnessCap, r, 9);
        }

        // ---- the pair -------------------------------------------------------------------------

        /// <summary>The defect this whole split exists for, stated as a test: over the live world's
        /// own numbers, the thickest ground is the least valuable. A single number cannot serve both
        /// readers, and if these two ever agree in ORDER the pair has stopped being necessary.</summary>
        [Fact]
        public void ThickCheapGround_OutRanksThinDearGround_OnRichnessAndNotOnWorth()
        {
            FactionTuning t = TestSupport.Tuning();
            long full = PointsForFullSurvey(t);

            SpatialNode ice = Measured(12.0, full);       // live: ice, 12.00 band-metres, worth 0.181
            SpatialNode platinum = Measured(8.0, full);   // live: platinum, 8.00 band-metres, worth 0.909
            ice.ResourceValue = 0.181;
            platinum.ResourceValue = 0.909;

            Assert.True(NodeContent.RichnessFor(ice, t) > NodeContent.RichnessFor(platinum, t),
                "the thicker seam must read richer");
            Assert.True(ice.ResourceValue < platinum.ResourceValue,
                "while the cheaper seam still reads less valuable");
        }
    }
}
