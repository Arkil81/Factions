using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A deposit weighs what its rock yields, credits every layer at its real thickness and
    /// counts scattered voxel maps, and a deposit measured before that is re-surveyed rather than
    /// added to. The characteristic failures are a yield counted twice in the drill rate and a stale
    /// deposit summed with new observations in different units, so both are pinned directly.</summary>
    public class OreYieldSurveyTests
    {
        private const double Ref = DepositModel.ReferenceYieldRatio;

        private static OreSample Sample(int points, params OreObservation[] ores)
        {
            var s = new OreSample { PointsExamined = points };
            s.Ores.AddRange(ores);
            return s;
        }

        private static OreObservation Band(string ore, int hits, double depth, double ratio)
        {
            return new OreObservation(ore, hits, depth, 0.0, ratio, OreSource.Band);
        }

        // ---- Derive -----------------------------------------------------------------------------

        [Fact]
        public void Richness_IsVolumeTimesYield_OnTheReferenceScale_AndVolumeStaysPlain()
        {
            NodeDeposit d = DepositModel.Derive(Sample(1000, Band("Iron", 50, 20, 5.0)));

            DepositEntry e = d.Entries[0];
            Assert.Equal(1000.0, e.Volume, 9);                // 50 hits x 20 m
            Assert.Equal(1000.0 * 5.0 / Ref, e.Richness, 9);
            Assert.Equal(e.Richness, e.Weight, 9);
        }

        [Fact]
        public void AnUnknownYield_ReadsAsTheReference_AndLeavesVolumeUnmeasured()
        {
            NodeDeposit d = DepositModel.Derive(Sample(1000, new OreObservation("Iron", 50, 20, 0.0)));

            Assert.Equal(1000.0, d.Entries[0].Richness, 9);
            Assert.Equal(0.0, d.Entries[0].Volume, 9);
            double ratio;
            Assert.False(d.TryYieldRatioOf("Iron", out ratio), "an unmeasured yield must fall back to the table");
        }

        [Fact]
        public void YieldDecidesTheMix_NotThickness()
        {
            // Agni's case: as much black sand as silver vein by volume, at 0.002 against 1.
            NodeDeposit d = DepositModel.Derive(Sample(1000,
                Band("Silver", 100, 10, 0.002), Band("Gold", 100, 10, 1.0)));

            Assert.Equal("Gold", DepositModel.DominantOre(d));
            Assert.True(d.ShareOf("Silver") < 0.01);
        }

        [Fact]
        public void SeveralBandsOfOneOre_AreOneEntry()
        {
            NodeDeposit d = DepositModel.Derive(Sample(1000,
                Band("Ice", 10, 20, 5.0), Band("Ice", 30, 5, 1.0), Band("Iron", 5, 10, 5.0)));

            Assert.Equal(2, d.Entries.Count);
            Assert.Equal(10 * 20 + 30 * 5, d.Entries.Find(x => x.SubTypeId == "Ice").Volume, 9);
        }

        [Fact]
        public void ScatterIsNotHeldToTheBandFloor_ButALayerIs()
        {
            NodeDeposit d = DepositModel.Derive(Sample(1000,
                new OreObservation("Silver", 40, 0.02, 0.0, 0.01, OreSource.Scatter),
                new OreObservation("Ice", 40, 1.0, 0.0, 5.0, OreSource.Layer)));

            Assert.Single(d.Entries);
            Assert.Equal("Silver", d.Entries[0].SubTypeId);
        }

        [Fact]
        public void YieldRatio_IsTheMaterialsOwn_AveragedByVolume()
        {
            // Tellus ice: 20 m of 5x lake ice and snow at 1x, the same ore from two materials.
            NodeDeposit d = DepositModel.Derive(Sample(1000, Band("Ice", 10, 20, 5.0), Band("Ice", 10, 20, 1.0)));

            double ratio;
            Assert.True(d.TryYieldRatioOf("Ice", out ratio));
            Assert.Equal(3.0, ratio, 9);
        }

        // ---- the drill rate counts the yield once ------------------------------------------------

        private static SpatialNode NodeWith(NodeDeposit deposit, double richness)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.Deposit = deposit;
            n.Richness = richness;
            return n;
        }

        [Fact]
        public void DrillRate_TakesTheYieldOutOfRichness_BeforeMultiplyingItBackIn()
        {
            // Two nodes cutting the same rock volume: one 8.8x dolomite, one at the reference yield.
            // Richness differs by the yield; what a drill cuts through does not. The ore it delivers
            // per cut must differ by the yield once -- not squared, and not not at all.
            var tuning = TestSupport.Tuning();
            NodeDeposit rich = DepositModel.Derive(Sample(1000, Band("Dolomite", 50, 20, 8.8)));
            NodeDeposit plain = DepositModel.Derive(Sample(1000, Band("Dolomite", 50, 20, Ref)));
            SpatialNode a = NodeWith(rich, 0.2 * 8.8 / Ref);   // kept under the 1.0 cap
            SpatialNode b = NodeWith(plain, 0.2);
            var w = new Dictionary<string, double> { ["Ore:Dolomite"] = 1.0 };

            double fa = MineTargeting.NodeRichnessFactor(a, tuning, w);
            double fb = MineTargeting.NodeRichnessFactor(b, tuning, w);

            Assert.Equal(8.8 / Ref, fa / fb, 6);
        }

        [Fact]
        public void DrillRate_ReadsTheMeasuredYield_OverTheTablesDefault()
        {
            // A BetterStone mineral has no table row, so the table says 1; the ground says 8.8.
            var tuning = TestSupport.Tuning();
            SpatialNode n = NodeWith(DepositModel.Derive(Sample(1000, Band("[S] Dolomite (Mg)", 50, 20, 8.8))), 0.5);

            Assert.Equal(8.8, MineTargeting.OreDensityAt(n, tuning, "Ore:[S] Dolomite (Mg)"), 9);
            Assert.Equal(tuning.DefaultOreDensityRatio,
                MineTargeting.OreDensityAt(NodeWith(new NodeDeposit(), 0.5), tuning, "Ore:[S] Dolomite (Mg)"), 9);
        }

        [Fact]
        public void ALegacyDeposit_DrillsExactlyAsItDidBefore()
        {
            var tuning = TestSupport.Tuning();
            var legacy = new NodeDeposit();
            legacy.Entries.Add(new DepositEntry("Iron", 100, 100));   // no volume: written before it existed
            legacy.PointsExamined = 1000;
            SpatialNode n = NodeWith(legacy, 0.4);

            double f = MineTargeting.NodeRichnessFactor(n, tuning, new Dictionary<string, double> { ["Ore:Iron"] = 1.0 });

            Assert.Equal(0.4 * tuning.OreDensityRatioFor("Ore:Iron"), f, 9);
        }

        // ---- units, persistence and the re-survey -------------------------------------------------

        [Fact]
        public void ANewDeposit_IsInCurrentUnits()
        {
            Assert.Equal(NodeDeposit.CurrentUnits, DepositModel.Derive(Sample(10, Band("Iron", 1, 10, 5))).Units);
            Assert.False(DepositModel.Derive(Sample(10)).IsStale);
        }

        private static SpatialNode RoundTrip(SpatialNode n)
        {
            var st = new FactionState { Tag = "ACME", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var graph = new SpatialGraph();
                SpatialNode copy = graph.Add(n.Position, 0.5, n.Id);
                copy.OwnerFactionTag = "ACME";
                copy.HoldStrength = 0.5;
                copy.Deposit = n.Deposit;
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), reloaded);
                return reloaded.Nodes[n.Id];
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void VolumeAndUnits_SurviveARestart()
        {
            SpatialNode n = NodeWith(DepositModel.Derive(Sample(1000, Band("Iron", 50, 20, 5.0))), 0.5);

            SpatialNode after = RoundTrip(n);

            Assert.Equal(1000.0, after.Deposit.Entries[0].Volume, 9);
            Assert.Equal(NodeDeposit.CurrentUnits, after.Deposit.Units);
        }

        [Fact]
        public void ADepositWrittenBeforeUnits_LoadsStale_AndLeavesThePacedSurvey()
        {
            SpatialNode n = NodeWith(DepositModel.Derive(Sample(1000, Band("Iron", 50, 20, 5.0))), 0.5);
            n.Deposit.Units = 0;   // what an old brain file's absent field loads as

            SpatialNode after = RoundTrip(n);
            var w = new WorldModel(new SeededRandom(5)) { Tuning = TestSupport.Tuning() };

            Assert.True(after.Deposit.IsStale);
            Assert.True(WorldModel.NeedsResurvey(after));
            Assert.False(w.NeedsSurvey(after), "a stale deposit must never be added to in new units");
        }

        /// <summary>Answers with one iron band per point and counts the points it was asked for.</summary>
        private sealed class IronGeography : IOreGeography
        {
            public double Ratio = 5.0;

            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                return TryResample(posKm, radiusKm, 0, 1000, out sample);
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                sample = Sample(count, Band("Iron", count / 10, 20, Ratio));
                return true;
            }

            public OreProspect CouldBearOre(Vec3 posKm) { return OreProspect.Possible; }
            public IEnumerable<string> MintableOres { get { yield return "Iron"; } }
        }

        [Fact]
        public void TheReSurvey_KeepsTheOldReadingLive_UntilItHasCoveredTheSameGround_ThenSwaps()
        {
            var tuning = TestSupport.Tuning();
            tuning.ResurveyMinimumPoints = 1;
            var w = new WorldModel(new SeededRandom(5)) { Tuning = tuning, Ores = new IronGeography() };
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "ACME";
            n.HoldStrength = 1.0;
            var stale = new NodeDeposit { Units = 0, PointsExamined = 10000 };
            stale.Entries.Add(new DepositEntry("Iron", 20000, 20000));
            n.Deposit = stale;

            Assert.True(w.TryResurveyStep(n, 4096));
            Assert.Same(stale, n.Deposit);
            Assert.Equal(4096, n.PendingDeposit.PointsExamined);

            Assert.True(w.TryResurveyStep(n, 4096));
            Assert.Same(stale, n.Deposit);

            Assert.True(w.TryResurveyStep(n, 4096));   // asks only for the 1,808 still owed
            Assert.NotSame(stale, n.Deposit);
            Assert.Null(n.PendingDeposit);
            Assert.False(n.Deposit.IsStale);
            Assert.Equal(10000, n.Deposit.PointsExamined);
            Assert.False(w.TryResurveyStep(n, 4096), "nothing left to re-survey");
        }

        [Fact]
        public void TheReSurvey_LooksAtLeastAFirstSamplesWorth_AtABarelySampledNode()
        {
            var tuning = TestSupport.Tuning();
            tuning.ResurveyMinimumPoints = 5000;
            var w = new WorldModel(new SeededRandom(5)) { Tuning = tuning, Ores = new IronGeography() };
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.Deposit = new NodeDeposit { Units = 0, PointsExamined = 100 };   // sampled barren, long ago

            while (WorldModel.NeedsResurvey(n)) Assert.True(w.TryResurveyStep(n, 4096));

            Assert.Equal(5000, n.Deposit.PointsExamined);
            Assert.False(n.Deposit.IsBarren);
        }

        // ---- scatter arithmetic ------------------------------------------------------------------

        [Fact]
        public void LodZeroShare_IsSesProgressiveScanSplit()
        {
            Assert.Equal(0.28705, ScatterMath.LodZeroShare, 5);
        }

        [Fact]
        public void PlanePerSphereArea_IsHalfAtAFaceCentre_AndAveragesNearOne()
        {
            Assert.Equal(0.5, ScatterMath.PlanePerSphereArea(1.0), 9);
            Assert.Equal(Math.Sqrt(27) / 2.0, ScatterMath.PlanePerSphereArea(1.0 / Math.Sqrt(3.0)), 9);

            // Over the sphere, plane area per sphere area must average to the cube's 6 faces of
            // (R sqrt2)^2 over the sphere's 4 pi R^2: 12 / 4pi.
            double sum = 0; int n = 20000;
            for (long i = 0; i < n; i++)
            {
                double x, y, z;
                ProgressiveLattice.PointOnSphere(i, out x, out y, out z);
                sum += ScatterMath.PlanePerSphereArea(Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))));
            }
            Assert.Equal(12.0 / (4.0 * Math.PI), sum / n, 2);
        }

        [Fact]
        public void VolumePerSquareMetre_IsSharedAmongTheMatchingMappings()
        {
            double one = ScatterMath.VolumePerSquareMetre(0.0034, 1, 100.0, 1.0);
            double two = ScatterMath.VolumePerSquareMetre(0.0034, 2, 100.0, 1.0);

            Assert.Equal(0.0034 * ScatterMath.LodZeroShare * 100.0, one, 12);
            Assert.Equal(one / 2, two, 12);
            Assert.Equal(0.0, ScatterMath.VolumePerSquareMetre(0.0034, 0, 100.0, 1.0), 12);
        }

        // Covered share of ground from a simulation of SE's rule — random place and turn, refused on
        // overlap — at attempted cover x: the model must stay within 10% of it.
        [Theory]
        [InlineData(0.03, 0.028)]
        [InlineData(0.1, 0.080)]
        [InlineData(0.3, 0.174)]
        [InlineData(1.0, 0.303)]
        [InlineData(3.0, 0.384)]
        [InlineData(10.0, 0.443)]
        [InlineData(30.0, 0.476)]
        public void PlacedShare_MatchesASimulationOfOverlapRefusal(double attemptedCover, double simulatedCover)
        {
            double covered = attemptedCover * ScatterMath.PlacedShare(attemptedCover, 1.0);
            Assert.InRange(covered / simulatedCover, 0.9, 1.1);
        }

        [Fact]
        public void PlacedShare_IsWholeWhenSparse_AndCoverNeverPassesJamming()
        {
            Assert.Equal(1.0, ScatterMath.PlacedShare(0.0, 4096.0), 12);
            Assert.InRange(ScatterMath.PlacedShare(1e-9, 4096.0), 0.999, 1.0);
            // Kor's cities: a pipe attempt every ~1000 m2 against a 16384 m2 box.
            double x = 0.001 * 16384.0;
            double covered = x * ScatterMath.PlacedShare(0.001, 16384.0);
            Assert.InRange(covered, 0.4, ScatterMath.JammingCoverage);
        }

        [Fact]
        public void PlacedShare_DependsOnlyOnAttemptedCover()
        {
            Assert.Equal(ScatterMath.PlacedShare(0.002, 500.0), ScatterMath.PlacedShare(0.001, 1000.0), 12);
        }
    }
}
