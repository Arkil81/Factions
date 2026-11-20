using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="ReputationScale.ReputationFor"/> — the pure half of FAC-274. Factions' own
    /// −1..1 standing is authoritative and SE's table is a projection of it, so the property that
    /// matters is not the numbers but the <b>bands</b>: a standing the brain treats as hostile has
    /// to land where SE's turrets agree, and one it treats as an alliance has to land in Friends.
    /// Everything here is asserted through <see cref="ReputationScale.BandOf"/>, which mirrors
    /// <c>TranslateReputationToRelationship</c> exactly, rather than against a literal reputation
    /// that could drift from the band it is supposed to sit in.
    /// </summary>
    public class ReputationProjectionTests
    {
        // The live tuning values (tuning_defaults.json, 2026-09-06).
        private const double Hostile = -0.3;
        private const double Ally    =  0.4;

        private static int Rep(double relation, ReputationScale scale = null)
        {
            return (scale ?? ReputationScale.Vanilla).ReputationFor(relation, Hostile, Ally);
        }

        private static string Band(double relation, ReputationScale scale = null)
        {
            ReputationScale s = scale ?? ReputationScale.Vanilla;
            return s.BandOf(s.ReputationFor(relation, Hostile, Ally));
        }

        [Fact]
        public void HostileRelationThresholdLandsInsideEnemies()
        {
            // The whole reason this is piecewise and not a straight line. An even map of -1..1 onto
            // HostileMin..FriendlyMax sends -0.3 to -450, which BandOf reads as Neutral -- a faction
            // the brain considers a valid war target with SE's turrets holding fire.
            Assert.Equal("Enemies", Band(Hostile));
            Assert.Equal(ReputationScale.Vanilla.HostileMid, Rep(Hostile));
            Assert.Equal("Enemies", Band(Hostile - 0.01));
            Assert.Equal("Enemies", Band(-1.0));
            Assert.Equal(ReputationScale.Vanilla.HostileMin, Rep(-1.0));
        }

        [Fact]
        public void AllyRelationThresholdLandsInsideFriends()
        {
            Assert.Equal("Friends", Band(Ally));
            Assert.Equal(ReputationScale.Vanilla.FriendlyMid, Rep(Ally));
            Assert.Equal("Friends", Band(Ally + 0.01));
            Assert.Equal("Friends", Band(1.0));
            Assert.Equal(ReputationScale.Vanilla.FriendlyMax, Rep(1.0));
        }

        [Fact]
        public void ZeroIsNeutralMid_AndTheBandAroundItIsNeutral()
        {
            Assert.Equal(ReputationScale.Vanilla.NeutralMid, Rep(0.0));
            Assert.Equal("Neutral", Band(0.0));
            // Between the two thresholds and away from them, the pair is Neutral in SE too.
            Assert.Equal("Neutral", Band(-0.05));
            Assert.Equal("Neutral", Band(0.05));
        }

        [Fact]
        public void TheBandsAreContiguousAcrossTheWholeRange()
        {
            // Walk the range and assert the band only ever moves Enemies -> Neutral -> Friends, never
            // back. A non-monotonic curve would let a standing that FELL raise SE's reputation.
            string band = "Enemies";
            int last = int.MinValue;
            for (int i = -100; i <= 100; i++)
            {
                double rel = i / 100.0;
                int rep = Rep(rel);
                Assert.True(rep >= last, "reputation must be monotonic in relation, at " + rel);
                last = rep;

                string here = Band(rel);
                if (band == "Enemies" && here == "Neutral") band = "Neutral";
                else if (band == "Neutral" && here == "Friends") band = "Friends";
                Assert.Equal(band, here);
            }
            Assert.Equal("Friends", band);   // the walk actually reached all three
        }

        [Fact]
        public void TheProjectionUsesTheWorldsOwnMarks_NotVanillas()
        {
            // Same reason ReputationScale is passed in rather than hard-coded: a world that widened
            // its economy definition must get its own numbers, not a second copy of vanilla's.
            var wide = new ReputationScale
            {
                HostileMin = -3000, HostileMid = -2000, NeutralMin = -1000, NeutralMid = 0,
                FriendlyMin = 1000, FriendlyMid = 2000, FriendlyMax = 3000,
            };
            Assert.Equal(-2000, Rep(Hostile, wide));
            Assert.Equal( 2000, Rep(Ally, wide));
            Assert.Equal("Enemies", Band(Hostile, wide));
            Assert.Equal("Friends", Band(Ally, wide));
        }

        [Fact]
        public void ADegenerateThresholdFallsBackToAStraightLine_StillMonotonic()
        {
            // A threshold outside its own half cannot be a knot. The fallback must not produce a
            // curve that doubles back -- a broken config should be dull, not wrong.
            ReputationScale s = ReputationScale.Vanilla;
            int last = int.MinValue;
            for (int i = -100; i <= 100; i++)
            {
                int rep = s.ReputationFor(i / 100.0, 0.0, 0.0);
                Assert.True(rep >= last, "degenerate thresholds must still be monotonic");
                last = rep;
            }
            Assert.Equal(s.HostileMin,  s.ReputationFor(-1.0, 0.0, 0.0));
            Assert.Equal(s.NeutralMid,  s.ReputationFor( 0.0, 0.0, 0.0));
            Assert.Equal(s.FriendlyMax, s.ReputationFor( 1.0, 0.0, 0.0));
        }

        [Fact]
        public void OutOfRangeStandingsAreClampedToTheScale()
        {
            Assert.Equal(ReputationScale.Vanilla.HostileMin,  Rep(-9.0));
            Assert.Equal(ReputationScale.Vanilla.FriendlyMax, Rep( 9.0));
        }
    }
}
