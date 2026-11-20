using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Abandoning a node forfeits part of what was learned about it.
    /// <para>Operator direction, 2026-08-26. Surveying is work a faction did; walking away from the
    /// ground gives some of it back. At a week-long full survey this costs days to re-earn, which is
    /// what makes it a consequence rather than a formality.</para></summary>
    public class AbandonmentSurveyTests
    {
        private static WorldModel World()
        {
            return new WorldModel(new SeededRandom(11)) { Tuning = TestSupport.Tuning() };
        }

        private static SpatialNode Held(WorldModel w, string id = "N1", long points = 200000)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.Deposit = new NodeDeposit();
            n.Deposit.Entries.Add(new DepositEntry("Iron", 6000, 6000));
            n.Deposit.Entries.Add(new DepositEntry("Platinum", 2000, 2000));
            n.Deposit.PointsExamined = points;
            return n;
        }

        [Fact]
        public void Withdrawing_ForfeitsPartOfTheSurvey()
        {
            WorldModel w = World();
            SpatialNode n = Held(w);
            long before = n.Deposit.PointsExamined;

            w.Withdraw("F", "N1");

            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
            Assert.Equal((long)(before * w.Tuning.SurveyRetainedOnAbandonment), n.Deposit.PointsExamined);
        }

        [Fact]
        public void DecayingToNothing_ForfeitsItTheSameWay()
        {
            // The two abandonment paths must not drift. This is the one that happens without anyone
            // deciding anything, so it is the one most likely to be forgotten.
            WorldModel w = World();
            SpatialNode n = Held(w);
            n.HoldStrength = 0.01;
            n.LastReinforcedUtc = System.DateTime.MinValue;   // no grace period
            long before = n.Deposit.PointsExamined;

            w.DecayInfluence(1.0, 1.0, new List<FactionState> { new FactionState { Tag = "F" } });

            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
            Assert.Equal((long)(before * w.Tuning.SurveyRetainedOnAbandonment), n.Deposit.PointsExamined);
        }

        // ---- what forgetting must NOT do -----------------------------------------------------------

        [Fact]
        public void Forgetting_LeavesCompositionExactlyAsItWas()
        {
            // The ground did not change because nobody is standing on it. Only the certainty did.
            var d = new NodeDeposit();
            d.Entries.Add(new DepositEntry("Iron", 6000, 6000));
            d.Entries.Add(new DepositEntry("Platinum", 2000, 2000));
            d.PointsExamined = 200000;

            double ironBefore = d.ShareOf("Iron");
            d.Forget(0.5);

            Assert.Equal(ironBefore, d.ShareOf("Iron"), 9);
            Assert.Equal(0.75, d.ShareOf("Iron"), 9);
            Assert.Equal(2, d.Entries.Count);
        }

        /// <summary>The trap this exists to stop: <c>RichnessPerPoint</c> is richness over points, so
        /// cutting points alone would leave the numerator intact and make an abandoned node read as
        /// <b>denser</b> than it was — a penalty that increases the node's worth.</summary>
        [Fact]
        public void Forgetting_DoesNotMakeANodeLookRicher()
        {
            var d = new NodeDeposit();
            d.Entries.Add(new DepositEntry("Iron", 6000, 6000));
            d.PointsExamined = 200000;

            double densityBefore = d.RichnessPerPoint;
            d.Forget(0.5);

            Assert.Equal(densityBefore, d.RichnessPerPoint, 9);
        }

        [Fact]
        public void AbandoningANode_LowersWhatItIsWorth()
        {
            WorldModel w = World();
            SpatialNode n = Held(w);
            w.ResolveDeposit(n);            // refresh worth from the full survey
            double before = n.ResourceValue;

            w.Withdraw("F", "N1");

            Assert.True(n.ResourceValue < before,
                "worth must follow the lost certainty: " + n.ResourceValue + " vs " + before);
        }

        // ---- and nothing hands the knowledge back ---------------------------------------------------

        /// <summary>Without this the penalty undoes itself: the paced survey would keep running on an
        /// abandoned node and restore the points over the following days, with nothing to show it
        /// had. Surveying is work a faction does.</summary>
        [Fact]
        public void AnAbandonedNode_IsNotSurveyedByAnybody()
        {
            WorldModel w = World();
            SpatialNode n = Held(w, points: 1000);

            Assert.True(w.NeedsSurvey(n), "a held node with an unfinished survey should want one");

            w.Withdraw("F", "N1");

            Assert.False(w.NeedsSurvey(n), "nobody is standing on an abandoned node");
        }

        [Fact]
        public void ReclaimingIt_PutsItBackInTheSurveyQueue()
        {
            // Losing the knowledge has to be recoverable, or abandonment is a death sentence rather
            // than a setback.
            WorldModel w = World();
            SpatialNode n = Held(w, points: 1000);
            w.Withdraw("F", "N1");

            n.OwnerFactionTag = "G";

            Assert.True(w.NeedsSurvey(n));
        }

        [Fact]
        public void ANodeWithNoDepositAtAll_IsNotSurveyed()
        {
            WorldModel w = World();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N9");
            n.OwnerFactionTag = "F";

            Assert.False(w.NeedsSurvey(n));
        }

        [Fact]
        public void Forget_AtFullRetention_ChangesNothing()
        {
            var d = new NodeDeposit();
            d.Entries.Add(new DepositEntry("Iron", 100, 100));
            d.PointsExamined = 5000;

            d.Forget(1.0);

            Assert.Equal(5000, d.PointsExamined);
            Assert.Equal(100.0, d.Entries[0].Weight, 9);
        }
    }
}
