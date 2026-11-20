using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>What the station window's Ground panel shows: how far the survey has got, where the
    /// two readings are heading, and the deposit ore by ore.
    ///
    /// <para>Each is a second view of a number the node already carries, so the tests pin the views
    /// to the numbers rather than to a separate expectation: the target is what the reading reaches at
    /// a full survey, and the worth column is the value sum split, not a parallel estimate of it.</para></summary>
    public class GroundBreakdownTests
    {
        private sealed class Prices : IItemPriceCatalogue
        {
            private readonly Dictionary<string, long> _p = new Dictionary<string, long>();
            public Prices Set(string sub, long price) { _p[sub] = price; return this; }
            public long BasePrice(string sub)
            {
                long v;
                return !string.IsNullOrEmpty(sub) && _p.TryGetValue(sub, out v) ? v : 0;
            }
        }

        private static Prices Priced()
        {
            return new Prices().Set("Ore:Iron", 100).Set("Ore:Platinum", 350);
        }

        /// <summary>A node with nine parts iron to one part platinum, examined at
        /// <paramref name="points"/> probes, at 12 band-metres per probe overall.</summary>
        private static SpatialNode IronWithPlatinum(long points)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 0.0, "N1");
            n.Deposit = new NodeDeposit { PointsExamined = points };
            double total = Math.Max(1, points) * 12.0;
            n.Deposit.Entries.Add(new DepositEntry("Iron", total * 0.9, total * 0.9));
            n.Deposit.Entries.Add(new DepositEntry("Platinum", total * 0.1, total * 0.1));
            return n;
        }

        private static long FullSurvey(FactionTuning t)
        {
            return SurveyMath.PointsToResolve(t.SurveyTargetShare);
        }

        // ---- the targets are where the readings go -------------------------------------------------

        [Fact]
        public void AtAFullSurvey_BothReadingsEqualTheirTargets()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = IronWithPlatinum(FullSurvey(t));

            Assert.Equal(1.0, NodeContent.SurveyConfidence(n, t), 9);
            Assert.Equal(NodeContent.ResourceValueTarget(n, null, t, Priced(), NullItemRegistry.Instance),
                         NodeContent.ResourceValueFor(n, null, t, Priced(), NullItemRegistry.Instance), 9);
            Assert.Equal(NodeContent.RichnessTarget(n, t), NodeContent.RichnessFor(n, t), 9);
        }

        /// <summary>Partway through, each reading sits exactly the confidence's fraction of the way from
        /// its floor to its target — the relation the window's "rising to" reads off.</summary>
        [Fact]
        public void PartwayThrough_EachReadingIsTheConfidenceFractionOfTheWayToItsTarget()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode n = IronWithPlatinum(FullSurvey(t) / 4);

            double c = NodeContent.SurveyConfidence(n, t);
            Assert.InRange(c, 0.24, 0.26);

            double rvTarget = NodeContent.ResourceValueTarget(n, null, t, Priced(), NullItemRegistry.Instance);
            double rv = NodeContent.ResourceValueFor(n, null, t, Priced(), NullItemRegistry.Instance);
            Assert.Equal(t.UnsurveyedResourceValue + (rvTarget - t.UnsurveyedResourceValue) * c, rv, 9);

            double rTarget = NodeContent.RichnessTarget(n, t);
            Assert.Equal(t.RichnessFloor + (rTarget - t.RichnessFloor) * c, NodeContent.RichnessFor(n, t), 9);
            Assert.NotEqual(rTarget, NodeContent.RichnessFor(n, t), 3);
        }

        [Fact]
        public void GroundWithNoOre_HasNothingToSurvey_AndItsWorthIsAlreadyItsTarget()
        {
            FactionTuning t = TestSupport.Tuning();
            var graph = new SpatialGraph();
            SpatialNode barren = graph.Add(new Vec3(0, 0, 0), 0.0, "N1");
            barren.Deposit = new NodeDeposit { PointsExamined = 500000 };

            Assert.Equal(0.0, NodeContent.SurveyConfidence(barren, t));
            Assert.Equal(0.0, NodeContent.RichnessTarget(barren, t));
            Assert.Equal(NodeContent.ResourceValueFor(barren, null, t, Priced(), NullItemRegistry.Instance),
                         NodeContent.ResourceValueTarget(barren, null, t, Priced(), NullItemRegistry.Instance), 9);
            Assert.Empty(DepositModel.Breakdown(barren.Deposit, Priced(), NullItemRegistry.Instance));
        }

        // ---- the ore mix ----------------------------------------------------------------------------

        /// <summary>Platinum is a tenth of the ore at 3.5x iron, so it carries 0.35 / (0.9 + 0.35) = 28%
        /// of the worth. That gap between the share and the worth is what the panel exists to show.</summary>
        [Fact]
        public void TheWorthColumn_IsShareTimesPrice_OverTheTotal()
        {
            FactionTuning t = TestSupport.Tuning();
            List<OreBreakdown> rows = DepositModel.Breakdown(
                IronWithPlatinum(FullSurvey(t)).Deposit, Priced(), NullItemRegistry.Instance);

            OreBreakdown iron = rows.Single(r => r.SubTypeId == "Iron");
            OreBreakdown platinum = rows.Single(r => r.SubTypeId == "Platinum");

            Assert.Equal("Iron", rows[0].SubTypeId);   // heaviest first
            Assert.Equal(0.9, iron.Share, 9);
            Assert.Equal(0.1, platinum.Share, 9);
            Assert.Equal(1.0, iron.PriceRatio, 9);
            Assert.Equal(3.5, platinum.PriceRatio, 9);
            Assert.Equal(0.35 / 1.25, platinum.WorthShare, 9);
            Assert.Equal(1.0, rows.Sum(r => r.WorthShare), 9);
        }

        /// <summary>The worth column splits the very sum a node's worth is derived from: each row's
        /// share of it, times the sum, gives back that row's term.</summary>
        [Fact]
        public void TheWorthColumn_ReadsBackToValuePerGround()
        {
            FactionTuning t = TestSupport.Tuning();
            NodeDeposit d = IronWithPlatinum(FullSurvey(t)).Deposit;

            double total;
            Assert.True(DepositModel.TryValuePerGround(d, Priced(), NullItemRegistry.Instance, out total));
            OreBreakdown platinum = DepositModel.Breakdown(d, Priced(), NullItemRegistry.Instance)
                .Single(r => r.SubTypeId == "Platinum");

            // Platinum's own term: its density times its price ratio.
            Assert.Equal(12.0 * 0.1 * 3.5, platinum.WorthShare * total, 6);
        }

        /// <summary>Early in a survey a small share is under the noise floor and is marked unresolved;
        /// the dominant ore is not. Later both clear it.</summary>
        [Fact]
        public void ASmallShare_IsUnresolved_UntilTheSurveyCanTellItFromNoise()
        {
            FactionTuning t = TestSupport.Tuning();

            // 100 probes resolve shares above ~73%: iron's 90% clears it, platinum's 10% does not.
            List<OreBreakdown> early = DepositModel.Breakdown(
                IronWithPlatinum(100).Deposit, Priced(), NullItemRegistry.Instance);
            Assert.True(early.Single(r => r.SubTypeId == "Iron").Resolved);
            Assert.False(early.Single(r => r.SubTypeId == "Platinum").Resolved);

            List<OreBreakdown> late = DepositModel.Breakdown(
                IronWithPlatinum(FullSurvey(t)).Deposit, Priced(), NullItemRegistry.Instance);
            Assert.All(late, r => Assert.True(r.Resolved));
        }

        /// <summary>An unpriced world knows nothing about worth: shares still read, price and worth are
        /// zero rather than a guess.</summary>
        [Fact]
        public void OnAnUnpricedWorld_SharesStillRead_AndPriceAndWorthAreZero()
        {
            FactionTuning t = TestSupport.Tuning();
            List<OreBreakdown> rows = DepositModel.Breakdown(
                IronWithPlatinum(FullSurvey(t)).Deposit, null, NullItemRegistry.Instance);

            Assert.Equal(2, rows.Count);
            Assert.Equal(0.9, rows[0].Share, 9);
            Assert.All(rows, r => Assert.Equal(0.0, r.PriceRatio));
            Assert.All(rows, r => Assert.Equal(0.0, r.WorthShare));
        }
    }
}
