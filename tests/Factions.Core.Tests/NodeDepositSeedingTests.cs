using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-068 slice 3: a node's deposit is sampled from the world at seeding time, and a
    /// mineable node whose ground turns out to be barren is demoted rather than left advertising ore
    /// nobody can dig. See docs/ore-geography.md.</summary>
    public class NodeDepositSeedingTests
    {
        /// <summary>Test geography. <c>Samplable</c> false models deep space, where the seam declines
        /// rather than reporting barren — the distinction the model turns on.</summary>
        private sealed class FakeOreGeography : IOreGeography
        {
            public bool Samplable = true;
            public readonly List<OreObservation> Found = new List<OreObservation>();
            public int SampleCalls;
            public double LastRadiusKm = -1;

            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                SampleCalls++;
                LastRadiusKm = radiusKm;
                if (!Samplable) { sample = null; return false; }
                var s = new OreSample { PointsExamined = 1000 };
                s.Ores.AddRange(Found);
                sample = s;
                return true;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                if (!TrySample(posKm, radiusKm, out sample)) return false;
                sample.PointsExamined = count;
                return true;
            }

            public OreProspect CouldBearOre(Vec3 posKm)
            {
                return Found.Count > 0 ? OreProspect.Possible : OreProspect.None;
            }

            public IEnumerable<string> MintableOres
            {
                get { foreach (OreObservation o in Found) yield return o.SubTypeId; }
            }
        }

        private static WorldModel MakeWorld(FakeOreGeography ores)
        {
            var world = new WorldModel(new SeededRandom(1234)) { Tuning = TestSupport.Tuning() };
            if (ores != null) world.Ores = ores;
            return world;
        }

        private static SpatialNode AddNode(WorldModel world)
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            return n;
        }

        [Fact]
        public void OreBearingGround_GivesTheNodeADeposit_AndKeepsItsKind()
        {
            var ores = new FakeOreGeography();
            ores.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            ores.Found.Add(new OreObservation("Cobalt", 40, 10.0, 5.0));
            WorldModel world = MakeWorld(ores);
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);

            Assert.NotNull(n.Deposit);
            Assert.False(n.Deposit.IsBarren);
            Assert.True(n.Deposit.ShareOf("Iron") > n.Deposit.ShareOf("Cobalt"));
        }

        [Fact]
        public void BarrenGround_LeavesTheNodeUnmineable()
        {
            var ores = new FakeOreGeography(); // samplable, finds nothing
            WorldModel world = MakeWorld(ores);
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);

            Assert.Equal(1, ores.SampleCalls); // guard: it really did sample
            Assert.NotNull(n.Deposit);
            Assert.True(n.Deposit.IsBarren);
            Assert.False(NodeContent.IsMineable(n));
        }

        [Fact]
        public void BarrenGround_DoesNotMoveTheNode()
        {
            // Expansion geometry owns position; the ground only decides what the node IS. A model
            // that relocated nodes onto ore would put the economy somewhere nobody expanded to.
            var ores = new FakeOreGeography();
            WorldModel world = MakeWorld(ores);
            SpatialNode n = AddNode(world);
            Vec3 before = n.Position;

            world.ResolveDeposit(n);

            Assert.Equal(before.X, n.Position.X, 9);
            Assert.Equal(before.Y, n.Position.Y, 9);
            Assert.Equal(before.Z, n.Position.Z, 9);
        }

        [Fact]
        // Inverted at FAC-102, and pinned that way deliberately: this test used to assert that a node
        // the roll had not made mineable was never sampled, which is the defect. Iron under a Core node
        // is now found and the node becomes mineable.
        public void ANodeTheRollWouldHaveExcluded_IsSampledAnyway()
        {
            var ores = new FakeOreGeography();
            ores.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            WorldModel world = MakeWorld(ores);
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);

            Assert.Equal(1, ores.SampleCalls);
            Assert.NotNull(n.Deposit);
            Assert.True(NodeContent.IsMineable(n));
        }

        [Fact]
        public void SeamDeclinesToSample_LeavesKindAndDepositUntouched()
        {
            // Deep space today, until world features exist. An unwired or non-answering host must
            // keep exactly the pre-change behaviour rather than demoting every mineable node.
            var ores = new FakeOreGeography { Samplable = false };
            WorldModel world = MakeWorld(ores);
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);

            Assert.Equal(1, ores.SampleCalls);
            Assert.Null(n.Deposit);
            Assert.False(NodeContent.IsMineable(n));
        }

        [Fact]
        public void UnwiredWorld_UsesNullOreGeography_AndChangesNothing()
        {
            WorldModel world = MakeWorld(null); // no seam wired at all -- the offline sim's case
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);

            Assert.Null(n.Deposit);
            Assert.False(NodeContent.IsMineable(n));
        }

        /// <summary>Both deposit call sites take the radius from the world, not from tuning
        /// directly. This fixture has no planets, so the node is in deep space — which zone resolved
        /// is not the point here; separation resolution itself is covered in DepositSurveyTests
        /// against a body with an authored value.</summary>
        [Fact]
        public void SampleRadius_ComesFromTheWorldsOwnSeparation()
        {
            var ores = new FakeOreGeography();
            WorldModel world = MakeWorld(ores);
            world.Tuning.NodeSeparationDeepSpaceKm = 13.0;
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);

            Assert.Equal(6.5, ores.LastRadiusKm, 9);
        }

        /// <summary>If the two ever disagreed, a survey would accumulate points taken over different
        /// ground than the sample it is refining, and nothing would say so.</summary>
        [Fact]
        public void SurveyAndFirstSample_AgreeOnTheCatchment()
        {
            var ores = new FakeOreGeography();
            WorldModel world = MakeWorld(ores);
            world.Tuning.NodeSeparationDeepSpaceKm = 11.0;
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);
            double atSample = ores.LastRadiusKm;

            world.TrySurveyOnce(n, 16);

            Assert.Equal(5.5, atSample, 9);
            Assert.Equal(atSample, ores.LastRadiusKm, 9);
        }

        [Fact]
        public void CompositionComesFromTheGround()
        {
            // Two nodes of different kinds over identical ground derive identical deposits. The kind
            // stopped shaping the mix when every ore became one ItemKind; what a node yields is now
            // a fact about where it is.
            var oreGround = new FakeOreGeography();
            oreGround.Found.Add(new OreObservation("Ice", 50, 10.0, 5.0));
            oreGround.Found.Add(new OreObservation("Gold", 50, 10.0, 5.0));
            WorldModel iceWorld = MakeWorld(oreGround);
            SpatialNode ice = AddNode(iceWorld);

            var same = new FakeOreGeography();
            same.Found.AddRange(oreGround.Found);
            WorldModel oreWorld = MakeWorld(same);
            SpatialNode ore = AddNode(oreWorld);

            iceWorld.ResolveDeposit(ice);
            oreWorld.ResolveDeposit(ore);

            Assert.Equal(ice.Deposit.ShareOf("Ice"), ore.Deposit.ShareOf("Ice"), 9);
            Assert.Equal(ice.Deposit.ShareOf("Gold"), ore.Deposit.ShareOf("Gold"), 9);
        }

        // The defect FAC-102 names, pinned: sampling used to be gated on the kind a die had rolled, so
        // a node the roll made industrial sat on a seam it could never see. There is no kind left to
        // gate on -- every node is sampled.
        [Fact]
        public void EveryNode_IsSampled()
        {
            var ores = new FakeOreGeography();
            ores.Found.Add(new OreObservation("Iron", 10, 10.0, 5.0));
            WorldModel world = MakeWorld(ores);
            SpatialNode n = AddNode(world);

            world.ResolveDeposit(n);

            Assert.Equal(1, ores.SampleCalls);
            Assert.NotNull(n.Deposit);
            Assert.True(NodeContent.IsMineable(n));
        }

        // ---- persistence -----------------------------------------------------------------------

        private static NodeDeposit RoundTrip(NodeDeposit deposit)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(10, 0, 20), 0.6, "H1");
            n.OwnerFactionTag = "ACME";
            n.Deposit = deposit;

            var st = new FactionState { Tag = "ACME", FactionId = 7, PersonalityId = "settler" };
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "factions-deposit-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));

                var graph2 = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);
                return graph2.Nodes["H1"].Deposit;
            }
            finally
            {
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Deposit_SurvivesABrainRoundTrip()
        {
            var ores = new FakeOreGeography();
            ores.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            ores.Found.Add(new OreObservation("Cobalt", 40, 10.0, 5.0));
            WorldModel world = MakeWorld(ores);
            SpatialNode seeded = AddNode(world);
            world.ResolveDeposit(seeded);

            NodeDeposit loaded = RoundTrip(seeded.Deposit);

            Assert.NotNull(loaded);
            Assert.Equal(seeded.Deposit.Entries.Count, loaded.Entries.Count);
            Assert.Equal(seeded.Deposit.ShareOf("Iron"), loaded.ShareOf("Iron"), 9);
            Assert.Equal(seeded.Deposit.ShareOf("Cobalt"), loaded.ShareOf("Cobalt"), 9);
            Assert.Equal(DepositModel.DominantOre(seeded.Deposit), DepositModel.DominantOre(loaded));
        }

        [Fact]
        public void NullDeposit_RoundTripsAsNull_NotAsAnEmptyDeposit()
        {
            // "Never sampled" and "sampled and barren" must stay distinguishable across a restart:
            // the seeding pass reads null as work still to do.
            Assert.Null(RoundTrip(null));
        }

        [Fact]
        public void BarrenDeposit_IsOmittedFromTheFile_KeepingBrainJsonLean()
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 0.5, "H1");
            n.OwnerFactionTag = "ACME";
            n.Deposit = new NodeDeposit();

            var st = new FactionState { Tag = "ACME" };
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "factions-deposit-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
                string json = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "brains", "ACME.json"));

                Assert.DoesNotContain("Deposit", json, System.StringComparison.Ordinal);
            }
            finally
            {
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
        }
    }
}
