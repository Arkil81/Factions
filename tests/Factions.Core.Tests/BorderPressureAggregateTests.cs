using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="IInfluenceField.Pressure"/>'s two defining properties, both of which
    /// FAC-248 filed as defects in the previous max-over-nodes/peaks-at-parity aggregate:
    /// it is <b>mass-weighted</b> (one contested node does not speak for a whole estate) and it is
    /// <b>directional</b> (it rises as the owner loses ground rather than peaking at parity).
    ///
    /// <para>Live evidence the old shape produced, 2026-09-04: IGSL read 0.738 border pressure from
    /// MEMN off exactly one contested node out of 119, and SPRT's N254 at 5% control reported 0.104 —
    /// a node it had effectively lost reading as almost frictionless.</para></summary>
    public class BorderPressureAggregateTests
    {
        private const double Radius = 40;

        private static SpatialNode Owned(WorldModel w, string id, string tag, double x, double y, double hold)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, y, 0), 0.5, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = hold;
            n.Radius = Radius;
            return n;
        }

        /// <summary>Every node is far enough from every other that only the pairs a test places
        /// deliberately close are inside the 4r cutoff.</summary>
        private const double FarApart = 400;

        private static WorldModel World()
        {
            var w = new WorldModel();
            w.Tuning.BorderPressureFullContestShare = FactionTuning.DefaultBorderPressureFullContestShare;
            return w;
        }

        // ---- mass weighting -------------------------------------------------------------------

        /// <summary>The FAC-248 headline. The SAME contact geometry — one AAA node sitting beside one
        /// BBB node — must read as far less pressure when AAA owns a large estate around it than when
        /// that node is all AAA has. Under the old max-over-nodes aggregate the two were identical,
        /// which is what let one rim node set a faction-wide standing.</summary>
        [Fact]
        public void OneContestedNode_PressesLessOnALargeEstateThanOnASmallOne()
        {
            var small = World();
            Owned(small, "A0", "AAA", 0, 0, 1.0);
            Owned(small, "B0", "BBB", 10, 0, 1.0);
            small.PrepareInfluenceSnapshot();
            double smallPressure = small.Influence.Pressure("AAA", "BBB");

            var large = World();
            Owned(large, "A0", "AAA", 0, 0, 1.0);
            Owned(large, "B0", "BBB", 10, 0, 1.0);
            for (int i = 1; i < 30; i++) Owned(large, "A" + i, "AAA", 0, i * FarApart, 1.0);
            large.PrepareInfluenceSnapshot();
            double largePressure = large.Influence.Pressure("AAA", "BBB");

            Assert.True(smallPressure > 0.5,
                "a lone node sharing its space with a rival is genuinely contested, got " + smallPressure);
            Assert.True(largePressure < smallPressure / 10.0,
                "the same contact across a 30-node estate must be diluted roughly by estate size; small="
                + smallPressure + " large=" + largePressure);
        }

        /// <summary>Pressure must be something a faction can reduce by winning. Reinforcing a node it
        /// is LOSING — hold 0.5 against a rival's 1.0, brought back up to 1.0 — has to lower the
        /// reading.
        ///
        /// <para>This is the direction that inverts. The old aggregate peaked at parity, so the same
        /// reinforcement walked that node UP from 0.667 to 1.0: recovering ground registered as the
        /// border getting worse, and the only way to lower the number was to stay beaten.</para></summary>
        [Fact]
        public void ReinforcingANodeItIsLosing_LowersPressure()
        {
            double PressureAtHold(double ownHold)
            {
                var w = World();
                Owned(w, "A0", "AAA", 0, 0, ownHold);
                Owned(w, "B0", "BBB", 0, 0, 1.0);                       // rival on the same spot, full hold
                Owned(w, "A1", "AAA", 0, FarApart, 1.0);                // quiet holdings, so neither
                Owned(w, "A2", "AAA", 0, 2 * FarApart, 1.0);            // reading lands on the 1.0 clamp
                w.PrepareInfluenceSnapshot();
                return w.Influence.Pressure("AAA", "BBB");
            }

            double losing = PressureAtHold(0.5);
            double recovered = PressureAtHold(1.0);

            Assert.True(losing < 1.0 && recovered > 0,
                "both readings must be off the bounds or the comparison is vacuous; losing=" + losing
                + " recovered=" + recovered);
            Assert.True(recovered < losing,
                "pressure must fall when the owner reinforces ground it was losing; losing=" + losing
                + " recovered=" + recovered);
        }

        // ---- direction ------------------------------------------------------------------------

        /// <summary>The parity paradox. A node the owner is losing badly must read as MORE pressure
        /// than one at parity, not less. The old <c>2*min/(mine+theirs)</c> peaked at parity and fell
        /// back toward zero as the owner was pushed out.</summary>
        [Fact]
        public void ABesiegedNode_ReadsHigherThanAnEvenlyContestedOne()
        {
            // Both cases sit inside a four-node estate so neither lands on the 1.0 clamp -- at the
            // shipped share, parity across a WHOLE estate is already full contest, so the two
            // readings are only distinguishable below that.
            var parity = World();
            Owned(parity, "A0", "AAA", 0, 0, 1.0);
            for (int i = 1; i < 4; i++) Owned(parity, "A" + i, "AAA", 0, i * FarApart, 1.0);
            Owned(parity, "B0", "BBB", 0, 0, 1.0);   // identical mass on the same spot as A0
            parity.PrepareInfluenceSnapshot();
            double atParity = parity.Influence.Pressure("AAA", "BBB");

            var besieged = World();
            Owned(besieged, "A0", "AAA", 0, 0, 1.0);
            for (int i = 1; i < 4; i++) Owned(besieged, "A" + i, "AAA", 0, i * FarApart, 1.0);
            for (int i = 0; i < 3; i++) Owned(besieged, "B" + i, "BBB", 0, 0, 1.0);  // rival floods A0's spot 3:1
            besieged.PrepareInfluenceSnapshot();
            double whenLosing = besieged.Influence.Pressure("AAA", "BBB");

            Assert.True(atParity > 0 && whenLosing < 1.0,
                "both readings must be off the bounds or the comparison is vacuous; parity=" + atParity
                + " besieged=" + whenLosing);
            Assert.True(whenLosing > atParity,
                "being pushed out must read louder than an even contest; parity=" + atParity
                + " besieged=" + whenLosing);
        }

        /// <summary>An estate that projects no influence of its own — every node decayed to zero hold —
        /// is entirely under whatever its neighbour projects, and must read as maximum pressure. The
        /// old formula returned exactly 0 here, because <c>min(mine, theirs)</c> is 0 when mine is:
        /// the parity paradox at its limit.</summary>
        [Fact]
        public void AnEstateWithNoInfluenceOfItsOwn_ReadsMaximumPressure()
        {
            var w = World();
            Owned(w, "A0", "AAA", 0, 0, 0.0);
            Owned(w, "B0", "BBB", 10, 0, 1.0);
            w.PrepareInfluenceSnapshot();

            Assert.Equal(0.0, w.Influence.SampleAt("A0", "AAA"), 6);
            Assert.Equal(1.0, w.Influence.Pressure("AAA", "BBB"), 3);
        }

        /// <summary>Two factions meeting at one node do not feel each other equally when their estates
        /// differ: the smaller one has proportionally more of itself under the other's influence. The
        /// old aggregate read the same value from both sides of the same contact.</summary>
        [Fact]
        public void PressureIsDirectional_TheSmallerEstateFeelsMore()
        {
            var w = World();
            Owned(w, "A0", "AAA", 0, 0, 1.0);
            Owned(w, "B0", "BBB", 10, 0, 1.0);
            for (int i = 1; i < 10; i++) Owned(w, "A" + i, "AAA", 0, i * FarApart, 1.0);
            w.PrepareInfluenceSnapshot();

            double bbbFeels = w.Influence.Pressure("BBB", "AAA");
            double aaaFeels = w.Influence.Pressure("AAA", "BBB");
            Assert.True(bbbFeels > aaaFeels * 4,
                "the one-node faction must feel the shared border far more than the ten-node one; BBB="
                + bbbFeels + " AAA=" + aaaFeels);
        }

        // ---- degenerate cases and the knob ----------------------------------------------------

        [Fact]
        public void NoRivalInfluenceAnywhere_IsZero()
        {
            var w = World();
            Owned(w, "A0", "AAA", 0, 0, 1.0);
            Owned(w, "B0", "BBB", FarApart, 0, 1.0);   // outside the 4r cutoff
            w.PrepareInfluenceSnapshot();

            Assert.Equal(0.0, w.Influence.Pressure("AAA", "BBB"), 6);
        }

        /// <summary>FAC-188 item 6: a faction that owns nothing has no territory to have border
        /// relations about, and Pressure must keep saying so.</summary>
        [Fact]
        public void ALandlessFaction_FeelsNoPressure()
        {
            var w = World();
            Owned(w, "B0", "BBB", 0, 0, 1.0);
            w.PrepareInfluenceSnapshot();

            Assert.Equal(0.0, w.Influence.Pressure("AAA", "BBB"), 6);
        }

        /// <summary>The knob FAC-248 asked for: the scale of the whole Security/relation-drift chain
        /// has to be movable from tuning.json without a deploy. Halving the full-contest share doubles
        /// the reported pressure for the same geometry.</summary>
        [Fact]
        public void FullContestShare_ScalesTheReportedPressure()
        {
            var w = World();
            Owned(w, "A0", "AAA", 0, 0, 1.0);
            Owned(w, "B0", "BBB", 60, 0, 1.0);
            for (int i = 1; i < 6; i++) Owned(w, "A" + i, "AAA", 0, i * FarApart, 1.0);
            w.PrepareInfluenceSnapshot();

            w.Tuning.BorderPressureFullContestShare = 0.5;
            double atHalf = w.Influence.Pressure("AAA", "BBB");
            w.Tuning.BorderPressureFullContestShare = 0.25;
            double atQuarter = w.Influence.Pressure("AAA", "BBB");

            Assert.True(atHalf > 0 && atHalf < 0.4, "geometry must land off both bounds to be a real test, got " + atHalf);
            Assert.Equal(atHalf * 2.0, atQuarter, 6);
        }

        /// <summary>A zero or negative share would divide or invert the drive chain, and a live
        /// tuning.json predating this field deserializes to zero. The field must fall back, not blow up.</summary>
        [Fact]
        public void ANonsenseFullContestShare_FallsBackToTheShippedDefault()
        {
            var w = World();
            Owned(w, "A0", "AAA", 0, 0, 1.0);
            Owned(w, "B0", "BBB", 60, 0, 1.0);
            for (int i = 1; i < 6; i++) Owned(w, "A" + i, "AAA", 0, i * FarApart, 1.0);
            w.PrepareInfluenceSnapshot();

            w.Tuning.BorderPressureFullContestShare = FactionTuning.DefaultBorderPressureFullContestShare;
            double expected = w.Influence.Pressure("AAA", "BBB");

            w.Tuning.BorderPressureFullContestShare = 0;
            Assert.Equal(expected, w.Influence.Pressure("AAA", "BBB"), 6);
            w.Tuning.BorderPressureFullContestShare = -1;
            Assert.Equal(expected, w.Influence.Pressure("AAA", "BBB"), 6);
        }

        /// <summary>Distance still orders the rivals. Trivial-looking, and the reason it is here is
        /// that the mass-weighted aggregate sums over the OWNER's nodes rather than the rival's — so
        /// "who presses hardest" is no longer read off a single nearest pair, and nothing else in
        /// this file pins that it still tracks proximity at all.</summary>
        [Fact]
        public void ANearerRival_PressesHarderThanADistantOne()
        {
            var w = World();
            Owned(w, "A0", "AAA", 0, 0, 1.0);
            Owned(w, "B0", "BBB", 20, 0, 1.0);      // near
            Owned(w, "C0", "CCC", 100, 0, 1.0);     // further, still inside 4r
            w.PrepareInfluenceSnapshot();

            double fromB = w.Influence.Pressure("AAA", "BBB");
            double fromC = w.Influence.Pressure("AAA", "CCC");
            Assert.True(fromB > fromC, "the nearer rival must press harder; B=" + fromB + " C=" + fromC);
        }
    }
}
