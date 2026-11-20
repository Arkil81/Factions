using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-116: ground that was sampled and found empty must stay sampled across a restart.
    /// <para>"Sampled and barren" and "nobody has looked" drive opposite decisions — leave it alone,
    /// versus go and look — and a barren deposit has no entries to write, so the point count is the
    /// only thing that can carry the difference to disk.</para></summary>
    public class BarrenSurveyRoundTripTests
    {
        /// <summary>A geography that answers, and finds nothing — the case
        /// <see cref="IOreGeography"/> is explicit about, and the overwhelming majority of positions
        /// on a real planet.</summary>
        private sealed class BarrenGeography : IOreGeography
        {
            public int Calls;
            public int Points = 1000;

            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                Calls++;
                sample = new OreSample { PointsExamined = Points };   // answered; Ores stays empty
                return true;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                return TrySample(posKm, radiusKm, out sample);
            }

            public OreProspect CouldBearOre(Vec3 posKm) { return OreProspect.None; }

            public IEnumerable<string> MintableOres { get { yield break; } }
        }

        private static OreSample Barren(int points)
        {
            return new OreSample { PointsExamined = points };
        }

        // ---- Derive keeps the count -------------------------------------------------------------

        [Fact]
        public void ASampleThatFoundNothing_KeepsThePointsItExamined()
        {
            NodeDeposit d = DepositModel.Derive(Barren(1234));

            Assert.True(d.IsBarren);
            Assert.Equal(1234, d.PointsExamined);
        }

        [Fact]
        public void ANullSample_IsTheOneCaseWithNothingToRecord()
        {
            NodeDeposit d = DepositModel.Derive(null);

            Assert.True(d.IsBarren);
            Assert.Equal(0, d.PointsExamined);
        }

        [Fact]
        public void EveryBandTooThinToCount_StillCountsAsHavingLooked()
        {
            var sample = new OreSample { PointsExamined = 900 };
            sample.Ores.Add(new OreObservation("Iron", 50, DepositModel.MinBandDepthM / 2, 0.0));

            NodeDeposit d = DepositModel.Derive(sample);

            Assert.True(d.IsBarren);
            Assert.Equal(900, d.PointsExamined);
        }

        [Fact]
        public void TwoBarrenNodes_DoNotShareOneDeposit()
        {
            // A deposit is mutated in place by Accumulate and Forget, so a shared instance would let
            // either reach every barren node in the world at once.
            NodeDeposit a = DepositModel.Derive(Barren(1000));
            NodeDeposit b = DepositModel.Derive(Barren(1000));

            Assert.NotSame(a, b);

            a.Forget(0.5);
            Assert.Equal(500, a.PointsExamined);
            Assert.Equal(1000, b.PointsExamined);
        }

        // ---- the round trip ---------------------------------------------------------------------

        private static SpatialGraph GraphWith(string id, NodeDeposit deposit)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(1, 2, 3), 0.5, id);
            n.OwnerFactionTag = "ACME";
            n.HoldStrength = 0.5;
            n.Deposit = deposit;
            return graph;
        }

        private static SpatialNode RoundTrip(SpatialGraph graph, string id)
        {
            var st = new FactionState { Tag = "ACME", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), reloaded);
                return reloaded.Nodes[id];
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ABarrenNode_ComesBackSurveyed()
        {
            SpatialNode after = RoundTrip(GraphWith("N1", DepositModel.Derive(Barren(2500))), "N1");

            Assert.NotNull(after.Deposit);
            Assert.True(after.Deposit.IsBarren);
            Assert.Equal(2500, after.Deposit.PointsExamined);
            Assert.True(NodeDisplay.Surveyed(after), "a node sampled and found empty has been surveyed");
            Assert.False(NodeContent.IsMineable(after), "barren ground is still not mineable");
        }

        [Fact]
        public void ANodeNobodyHasLookedAt_ComesBackUnsurveyed()
        {
            // The distinction the point count exists to carry: this must NOT be repaired into a
            // barren deposit, or the two states collapse in the other direction.
            SpatialNode after = RoundTrip(GraphWith("N1", null), "N1");

            Assert.Null(after.Deposit);
            Assert.False(NodeDisplay.Surveyed(after));
        }

        [Fact]
        public void ABarrenNodeWritesItsCountAndNoEntryList_KeepingBrainJsonLean()
        {
            SpatialGraph graph = GraphWith("N1", DepositModel.Derive(Barren(2500)));
            var st = new FactionState { Tag = "ACME" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
                string json = File.ReadAllText(Path.Combine(dir, "brains", "ACME.json"));

                Assert.Contains("\"DepositPoints\"", json, StringComparison.Ordinal);
                Assert.DoesNotContain("\"Deposit\":", json, StringComparison.Ordinal);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ASurveyedNodeWithOre_StillRoundTripsItsCount()
        {
            var sample = new OreSample { PointsExamined = 4096 };
            sample.Ores.Add(new OreObservation("Iron", 60, 10.0, 5.0));

            SpatialNode after = RoundTrip(GraphWith("N1", DepositModel.Derive(sample)), "N1");

            Assert.NotNull(after.Deposit);
            Assert.False(after.Deposit.IsBarren);
            Assert.Equal(4096, after.Deposit.PointsExamined);
            Assert.Equal("Iron", DepositModel.DominantOre(after.Deposit));
        }

        [Fact]
        public void AnOldBrainWithNeitherField_LoadsAsNeverSampled()
        {
            // Backwards compatibility, and it is self-healing: a pre-FAC-116 file recorded a barren
            // node as nothing at all, so it re-enters the backfill once and persists correctly after.
            var doc = new BrainDoc
            {
                Tag = "ACME",
                Sources = new List<SourceDoc> { new SourceDoc { Id = "N1", X = 1, Y = 2, Z = 3, Strength = 0.5 } },
            };

            var graph = new SpatialGraph();
            FactionStore.ApplyBrainDoc(doc, graph);

            Assert.Null(graph.Nodes["N1"].Deposit);
        }

        // ---- the invariant the round trip exists to hold ----------------------------------------

        [Fact]
        public void AfterARestart_ABarrenNodeIsNotOfferedForBackfillAgain()
        {
            var geo = new BarrenGeography();
            var w = new WorldModel(new SeededRandom(5)) { Tuning = TestSupport.Tuning(), Ores = geo };
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "ACME";
            n.HoldStrength = 1.0;

            string id; DepositResolution result;
            Assert.True(w.TryBackfillOneDeposit(new HashSet<string>(), out id, out result));
            Assert.Equal(DepositResolution.Barren, result);
            int callsBeforeRestart = geo.Calls;

            // The restart. `attempted` is deliberately transient, so the deposit is the only thing
            // that can keep this node out of the queue on the far side.
            SpatialNode after = RoundTrip(w.Graph, "N1");
            var w2 = new WorldModel(new SeededRandom(5)) { Tuning = TestSupport.Tuning(), Ores = geo };
            w2.Graph.Nodes[after.Id] = after;

            string id2; DepositResolution result2;
            Assert.False(w2.TryBackfillOneDeposit(new HashSet<string>(), out id2, out result2),
                "a node already sampled and found barren must not be re-sampled after a restart");
            Assert.Equal(callsBeforeRestart, geo.Calls);
        }
    }
}
