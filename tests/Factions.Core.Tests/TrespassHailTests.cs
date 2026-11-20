using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 1 — a faction hails a ship it detects in its space, once per entry, in a tone
    /// set by the relation.
    /// </summary>
    public sealed class TrespassHailTests
    {
        private const double Guard = 300;
        private static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        // ---- whose space -------------------------------------------------------------------------

        private static InfluenceField Field(WorldModel w)
        {
            FactionTuning t = TestSupport.Tuning();
            return new InfluenceField(w.Graph, () => t);
        }

        [Fact]
        public void SpaceOwnerAt_NamesTheDominantFactionNearItsNode()
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "ALFA"; a.HoldStrength = 1.0;
            var b = w.Graph.Add(new Vec3(40, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "BETA"; b.HoldStrength = 1.0;

            Assert.Equal("ALFA", TrespassPolicy.SpaceOwnerAt(Field(w), new Vec3(5, 0, 0), 0.03));
            Assert.Equal("BETA", TrespassPolicy.SpaceOwnerAt(Field(w), new Vec3(35, 0, 0), 0.03));
        }

        [Fact]
        public void SpaceOwnerAt_IsNobodyInOpenSpaceOrBelowTheThreshold()
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "ALFA"; a.HoldStrength = 1.0;

            Assert.Null(TrespassPolicy.SpaceOwnerAt(Field(w), new Vec3(1000, 0, 0), 0.03));
            // Inside the field's reach, but weaker than the threshold asks for.
            Assert.Null(TrespassPolicy.SpaceOwnerAt(Field(w), new Vec3(5, 0, 0), 5.0));
        }

        [Fact]
        public void SpaceOwnerAt_NeutralGroundIsNobodysSpace()
        {
            var w = TestSupport.World();
            var n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N001"); n.OwnerFactionTag = NeutralFaction.Tag; n.HoldStrength = 1.0;

            Assert.Null(TrespassPolicy.SpaceOwnerAt(Field(w), new Vec3(1, 0, 0), 0.0));
        }

        // ---- tone and text -------------------------------------------------------------------------

        [Theory]
        [InlineData("Enemies", HailTone.Warning)]
        [InlineData("Neutral", HailTone.Notice)]
        [InlineData("Friends", HailTone.Welcome)]
        [InlineData(null, HailTone.Notice)]
        [InlineData("garbage", HailTone.Notice)]
        public void ToneFor_FollowsTheRelationBand(string band, HailTone expected)
        {
            Assert.Equal(expected, TrespassPolicy.ToneFor(band, false));
        }

        [Fact]
        public void ToneFor_AMemberIsNeverHailedByTheirOwnFaction()
        {
            Assert.Equal(HailTone.None, TrespassPolicy.ToneFor("Friends", true));
            Assert.Equal(HailTone.None, TrespassPolicy.ToneFor("Enemies", true));
        }

        [Fact]
        public void HailText_NamesTheFactionAndDiffersByTone()
        {
            string welcome = TrespassPolicy.HailText(HailTone.Welcome, "Universal Commerce");
            string notice = TrespassPolicy.HailText(HailTone.Notice, "Universal Commerce");
            string warning = TrespassPolicy.HailText(HailTone.Warning, "Universal Commerce");

            Assert.Contains("Universal Commerce", welcome);
            Assert.Contains("Universal Commerce", notice);
            Assert.Contains("Universal Commerce", warning);
            Assert.Equal(3, new HashSet<string> { welcome, notice, warning }.Count);
            Assert.Null(TrespassPolicy.HailText(HailTone.None, "Universal Commerce"));
        }

        // ---- once per entry ------------------------------------------------------------------------

        [Fact]
        public void Book_HailsOnEntryAndNotWhileStayingInside()
        {
            var book = new TrespassHailBook();

            Assert.True(book.Observe(1, "ALFA", T0, Guard));
            for (int i = 1; i <= 100; i++)
                Assert.False(book.Observe(1, "ALFA", T0.AddSeconds(5 * i), Guard));
        }

        [Fact]
        public void Book_ReentryInsideTheGuardIsNotHailed()
        {
            var book = new TrespassHailBook();
            Assert.True(book.Observe(1, "ALFA", T0, Guard));
            Assert.False(book.Observe(1, null, T0.AddSeconds(10), Guard));

            Assert.False(book.Observe(1, "ALFA", T0.AddSeconds(60), Guard));
        }

        [Fact]
        public void Book_ReentryAfterTheGuardIsHailedAgain()
        {
            var book = new TrespassHailBook();
            Assert.True(book.Observe(1, "ALFA", T0, Guard));
            Assert.False(book.Observe(1, null, T0.AddSeconds(10), Guard));

            Assert.True(book.Observe(1, "ALFA", T0.AddSeconds(10 + Guard), Guard));
        }

        [Fact]
        public void Book_GuardRunsFromTheExit_SoABorderIdlerIsNotHailedEveryWindow()
        {
            // Long after the first hail, a ship that keeps slipping out and back within the guard is
            // still the same visit. Measured from the hail alone this would re-hail at T0+Guard.
            var book = new TrespassHailBook();
            Assert.True(book.Observe(1, "ALFA", T0, Guard));
            DateTime t = T0;
            for (int i = 0; i < 20; i++)
            {
                t = t.AddSeconds(Guard * 0.9);
                Assert.False(book.Observe(1, null, t, Guard));
                t = t.AddSeconds(30);
                Assert.False(book.Observe(1, "ALFA", t, Guard));
            }
        }

        [Fact]
        public void Book_CrossingIntoAnotherFactionsSpaceIsAnEntryThere()
        {
            var book = new TrespassHailBook();
            Assert.True(book.Observe(1, "ALFA", T0, Guard));
            Assert.True(book.Observe(1, "BETA", T0.AddSeconds(5), Guard));
            Assert.Equal("BETA", book.InsideOf(1));
            // And straight back is inside ALFA's guard, measured from the moment it left.
            Assert.False(book.Observe(1, "ALFA", T0.AddSeconds(10), Guard));
        }

        [Fact]
        public void Book_PilotsAreIndependent()
        {
            var book = new TrespassHailBook();
            Assert.True(book.Observe(1, "ALFA", T0, Guard));
            Assert.True(book.Observe(2, "ALFA", T0, Guard));
        }

        [Fact]
        public void Book_LoggingOutIsLeaving()
        {
            var book = new TrespassHailBook();
            Assert.True(book.Observe(1, "ALFA", T0, Guard));

            DateTime logout = T0.AddSeconds(Guard * 3);
            book.Prune(new List<long>(), logout, Guard);
            Assert.Null(book.InsideOf(1));

            // Back within the guard of the logout: the same visit.
            Assert.False(book.Observe(1, "ALFA", logout.AddSeconds(60), Guard));
            Assert.False(book.Observe(1, null, logout.AddSeconds(61), Guard));
            // Back after it: a new one.
            Assert.True(book.Observe(1, "ALFA", logout.AddSeconds(61 + Guard), Guard));
        }

        // ---- who speaks -----------------------------------------------------------------------------

        [Fact]
        public void Speaker_IsTheNearestOwnedNodeEvenWhenItHasNoName()
        {
            // Live shape: an unnamed outpost beside the ship, the faction's only named station 1,500 km off.
            var w = TestSupport.World();
            var outpost = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N098"); outpost.OwnerFactionTag = "STCG";
            var home = w.Graph.Add(new Vec3(1500, 0, 0), 0.5, "N027"); home.OwnerFactionTag = "STCG";
            home.DisplayName = "Greenhold Holding";
            var other = w.Graph.Add(new Vec3(1, 0, 0), 0.5, "N096"); other.OwnerFactionTag = "EXP";
            other.DisplayName = "Sentinel Post";

            SpatialNode nearest = TrespassPolicy.NearestOwnedNode(w.Graph, "STCG", new Vec3(2, 0, 0));

            Assert.Same(outpost, nearest);
            Assert.Equal("Stellar Corp", TrespassPolicy.HailAuthor(nearest, "Stellar Corp"));
        }

        [Fact]
        public void Speaker_IsTheStationWhenTheNearestNodeHasAName()
        {
            var w = TestSupport.World();
            var n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N099"); n.OwnerFactionTag = "TFCS";
            n.DisplayName = "Cornerstone Works";

            Assert.Equal("Cornerstone Works control",
                TrespassPolicy.HailAuthor(TrespassPolicy.NearestOwnedNode(w.Graph, "TFCS", new Vec3(20, 0, 0)), "TFCS"));
            Assert.Null(TrespassPolicy.NearestOwnedNode(w.Graph, "NONE", new Vec3(0, 0, 0)));
        }

        [Fact]
        public void Book_PruneKeepsOnlinePilotsAndDropsExpiredStamps()
        {
            var book = new TrespassHailBook();
            Assert.True(book.Observe(1, "ALFA", T0, Guard));
            Assert.True(book.Observe(2, "BETA", T0, Guard));
            Assert.False(book.Observe(2, null, T0.AddSeconds(5), Guard));

            book.Prune(new List<long> { 1, 2 }, T0.AddSeconds(5 + Guard), Guard);

            Assert.Equal("ALFA", book.InsideOf(1));
            Assert.Equal(0, book.StampCount);
            // Pruning a stamp of someone still inside must not turn staying into an entry.
            Assert.False(book.Observe(1, "ALFA", T0.AddSeconds(6 + Guard), Guard));
        }
    }
}
