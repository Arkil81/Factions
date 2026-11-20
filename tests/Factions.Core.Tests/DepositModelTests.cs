using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="DepositModel.Derive"/> — the policy half of the ore-geography
    /// model (docs/ore-geography.md). The measurement half is <see cref="IOreGeography"/>, which is
    /// host-side and not exercised here.</summary>
    public class DepositModelTests
    {

        private static OreSample Sample(int pointsExamined, params OreObservation[] ores)
        {
            var s = new OreSample { PointsExamined = pointsExamined };
            s.Ores.AddRange(ores);
            return s;
        }

        private static OreObservation Ore(string subType, int hits, double bandDepthM = 10.0, double overburdenM = 5.0)
        {
            return new OreObservation(subType, hits, bandDepthM, overburdenM);
        }

        [Fact]
        public void Derive_NullSample_IsBarren()
        {
            NodeDeposit d = DepositModel.Derive(null);

            Assert.NotNull(d);
            Assert.True(d.IsBarren);
        }

        [Fact]
        public void Derive_SampleWithNoOre_IsBarren_AndIsNotAnError()
        {
            // Barren ground is the normal outcome for most positions, not a failure to sample:
            // the points WERE examined, they simply found nothing.
            OreSample s = Sample(pointsExamined: 21000);

            NodeDeposit d = DepositModel.Derive(s);

            Assert.True(s.IsBarren);
            Assert.Equal(21000, s.PointsExamined);
            Assert.True(d.IsBarren);
        }

        [Fact]
        public void Derive_SharesSumToOne()
        {
            OreSample s = Sample(1000, Ore("Iron", 60), Ore("Nickel", 30), Ore("Gold", 10));

            NodeDeposit d = DepositModel.Derive(s);

            Assert.Equal(3, d.Entries.Count);
            double sum = 0;
            for (int i = 0; i < d.Entries.Count; i++) sum += d.ShareOf(d.Entries[i].SubTypeId);
            Assert.Equal(1.0, sum, 9);
        }

        [Fact]
        public void Derive_RichnessScalesWithBothAreaAndBandThickness()
        {
            // Equal area, thicker band -> richer. This is the whole point of using Depth as well
            // as hit count; a hit-count-only model would call these two identical.
            OreSample s = Sample(1000, Ore("Iron", 50, bandDepthM: 4.0), Ore("Nickel", 50, bandDepthM: 16.0));

            NodeDeposit d = DepositModel.Derive(s);

            Assert.Equal(4.0, d.ShareOf("Nickel") / d.ShareOf("Iron"), 9);
        }

        [Fact]
        public void Derive_BandThinnerThanMinimum_IsDropped_BecauseSeGeneratesNoVoxelsForIt()
        {
            OreSample s = Sample(1000,
                Ore("Iron", 50, bandDepthM: 10.0),
                Ore("Platinum", 50, bandDepthM: DepositModel.MinBandDepthM - 0.1));

            NodeDeposit d = DepositModel.Derive(s);

            Assert.Equal(0, d.ShareOf("Platinum"));
            Assert.Equal(1.0, d.ShareOf("Iron"), 9);
        }

        [Fact]
        public void Derive_BandExactlyAtMinimum_IsKept()
        {
            // Guards the boundary explicitly so the drop rule cannot silently become exclusive.
            OreSample s = Sample(1000, Ore("Platinum", 50, bandDepthM: DepositModel.MinBandDepthM));

            NodeDeposit d = DepositModel.Derive(s);

            Assert.False(d.IsBarren);
            Assert.Equal(1.0, d.ShareOf("Platinum"), 9);
        }

        /// <summary>Every band too thin to mine leaves nothing, and nothing is barren — the one
        /// remaining way a sample with ore in it still yields no deposit, now that tuning can no
        /// longer suppress an ore at the point of measurement.</summary>
        [Fact]
        public void Derive_EveryBandThinnerThanMinimum_IsBarren()
        {
            OreSample s = Sample(1000,
                Ore("Iron", 50, bandDepthM: DepositModel.MinBandDepthM - 0.1),
                Ore("Gold", 50, bandDepthM: DepositModel.MinBandDepthM - 0.5));

            NodeDeposit d = DepositModel.Derive(s);

            Assert.True(d.IsBarren);
        }

        [Fact]
        public void Derive_UnknownSubType_IsKeptAsOre_NotDropped()
        {
            // A mod ore the registry has not caught up with is still ore in the ground. Dropping it
            // would reintroduce the curated-list failure this model exists to remove.
            OreSample s = Sample(1000, Ore("Thorium", 50));

            NodeDeposit d = DepositModel.Derive(s);

            ItemKind unusedKind;
            Assert.False(NullItemRegistry.Instance.TryGetItemKind("Thorium", out unusedKind));
            Assert.Equal(1.0, d.ShareOf("Thorium"), 9);
        }

        [Fact]
        public void Derive_ZeroHits_IsDropped()
        {
            OreSample s = Sample(1000, Ore("Iron", 50), Ore("Silver", 0));

            NodeDeposit d = DepositModel.Derive(s);

            Assert.Equal(0, d.ShareOf("Silver"));
            Assert.Equal(1.0, d.ShareOf("Iron"), 9);
        }

        [Fact]
        public void DominantOre_IsTheRichest_NotTheFirstSampled()
        {
            OreSample s = Sample(1000, Ore("Iron", 10), Ore("Cobalt", 90), Ore("Nickel", 40));

            NodeDeposit d = DepositModel.Derive(s);

            Assert.Equal("Cobalt", DepositModel.DominantOre(d));
        }

        [Fact]
        public void DominantOre_EqualRichness_IsStableRegardlessOfSampleOrder()
        {
            // A node's derived kind label must not depend on iteration order, or the same ground
            // would name itself differently between two boots.
            OreSample a = Sample(1000, Ore("Iron", 50), Ore("Cobalt", 50));
            OreSample b = Sample(1000, Ore("Cobalt", 50), Ore("Iron", 50));

            string fromA = DepositModel.DominantOre(DepositModel.Derive(a));
            string fromB = DepositModel.DominantOre(DepositModel.Derive(b));

            Assert.Equal(fromA, fromB);
        }

        [Fact]
        public void DominantOre_Barren_IsNull()
        {
            Assert.Null(DepositModel.DominantOre(new NodeDeposit()));
            Assert.Null(DepositModel.DominantOre(null));
        }

        [Fact]
        public void NullOreGeography_DeclinesToSample_WhichIsNotABarrenSample()
        {
            OreSample sample;
            bool sampled = NullOreGeography.Instance.TrySample(new Vec3(0, 0, 0), 7.5, out sample);

            Assert.False(sampled);
            Assert.Null(sample);
            Assert.Empty(NullOreGeography.Instance.MintableOres);
        }

        // ---- the deposit is a measurement, and carries no tuning -----------------------------------

        /// <summary>A deposit's weight is exactly what was observed: hits times band thickness, with
        /// no tuning factor anywhere in it. Pinned because the unit has to stay stable across a
        /// retune — a survey accumulates across restarts, so a factor applied here would sum new
        /// observations onto old ones in different units and nothing would report it.</summary>
        [Fact]
        public void Weight_IsExactlyHitsTimesBandDepth()
        {
            var sample = new OreSample { PointsExamined = 100 };
            sample.Ores.Add(new OreObservation("Iron", 70, 20, 0));
            sample.Ores.Add(new OreObservation("Platinum", 30, 40, 0));

            NodeDeposit d = DepositModel.Derive(sample);

            Assert.Equal(70 * 20.0, d.Entries.Find(e => e.SubTypeId == "Iron").Weight, 6);
            Assert.Equal(30 * 40.0, d.Entries.Find(e => e.SubTypeId == "Platinum").Weight, 6);
        }

        /// <summary>No ore is privileged or suppressed by what it is. FAC-068 slice 6 retired
        /// "rare" as a category an ore is in or out of; this keeps that true at the point of
        /// measurement, where a reintroduced curated list would be least visible.</summary>
        [Fact]
        public void NoOreIsWeightedDifferentlyForBeingRare()
        {
            var sample = new OreSample { PointsExamined = 100 };
            sample.Ores.Add(new OreObservation("Iron", 50, 20, 0));
            sample.Ores.Add(new OreObservation("Platinum", 50, 20, 0));

            NodeDeposit d = DepositModel.Derive(sample);

            Assert.Equal(0.5, d.ShareOf("Iron"), 9);
            Assert.Equal(0.5, d.ShareOf("Platinum"), 9);
        }

        /// <summary>An ore the registry has never heard of is still ore in the ground. Derive no
        /// longer consults a registry at all, which is what makes this structural rather than a
        /// behaviour that could regress.</summary>
        [Fact]
        public void AnUnknownModOre_IsMeasuredLikeAnyOther()
        {
            var sample = new OreSample { PointsExamined = 100 };
            sample.Ores.Add(new OreObservation("Iron", 50, 20, 0));
            sample.Ores.Add(new OreObservation("Xenite", 50, 20, 0));

            NodeDeposit d = DepositModel.Derive(sample);

            Assert.Equal(2, d.Entries.Count);
            Assert.Equal(0.5, d.ShareOf("Xenite"), 9);
        }

    }
}
