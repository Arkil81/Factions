using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-083: nodes that arrive by any path other than <c>ExpandFrom</c> — a scan, a
    /// migration, an old brain file — must still get their ground sampled. See docs/ore-geography.md.</summary>
    public class DepositBackfillTests
    {
        private sealed class FakeGeography : IOreGeography
        {
            public bool Samplable = true;
            public readonly List<OreObservation> Found = new List<OreObservation>();
            public int Calls;

            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                Calls++;
                if (!Samplable) { sample = null; return false; }
                var s = new OreSample { PointsExamined = 1000 };
                s.Ores.AddRange(Found);
                sample = s;
                return true;
            }
            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                if (!TrySample(posKm, radiusKm, out sample)) return false;
                sample.PointsExamined = count;   // the survey's budget, so accumulation advances
                return true;
            }

            // Tracks TrySample: unsamplable ground is Unknown, ore-bearing ground Possible.
            public OreProspect CouldBearOre(Vec3 posKm)
            {
                if (!Samplable) return OreProspect.Unknown;
                return Found.Count > 0 ? OreProspect.Possible : OreProspect.None;
            }

            public IEnumerable<string> MintableOres { get { yield break; } }
        }

        private static WorldModel World(FakeGeography geo)
        {
            var w = new WorldModel(new SeededRandom(5)) { Tuning = TestSupport.Tuning() };
            w.Ores = geo;
            return w;
        }

        private static SpatialNode Add(WorldModel w, string id)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            return n;
        }

        private static int DrainBackfill(WorldModel w, HashSet<string> attempted, int maxPasses = 100)
        {
            int done = 0;
            for (int i = 0; i < maxPasses; i++)
            {
                string id; DepositResolution r;
                if (!w.TryBackfillOneDeposit(attempted, out id, out r)) return done;
                done++;
            }
            return done;
        }

        [Fact]
        public void AScannedMineableNode_GetsSampled()
        {
            // The whole point: this node never went through ExpandFrom.
            var geo = new FakeGeography();
            geo.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            WorldModel w = World(geo);
            SpatialNode n = Add(w, "E1");
            Assert.Null(n.Deposit);

            string id; DepositResolution r;
            Assert.True(w.TryBackfillOneDeposit(new HashSet<string>(), out id, out r));

            Assert.Equal("E1", id);
            Assert.Equal(DepositResolution.Sampled, r);
            Assert.NotNull(n.Deposit);
            Assert.Equal(1.0, n.Deposit.ShareOf("Iron"), 9);
        }

        [Fact]
        // Inverted at FAC-102: the candidate set is now every node without a deposit, not every node
        // whose kind the roll happened to make mineable. Both of these are sampled and both find iron.
        public void NodesTheRollWouldHaveExcluded_AreCandidates()
        {
            var geo = new FakeGeography();
            geo.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            WorldModel w = World(geo);
            Add(w, "C1");
            Add(w, "R1");

            int done = DrainBackfill(w, new HashSet<string>());

            Assert.Equal(2, done);
            Assert.Equal(2, geo.Calls);
            Assert.All(w.Graph.Nodes.Values, n => Assert.True(NodeContent.IsMineable(n)));
        }

        [Fact]
        public void ANodeThatAlreadyHasADeposit_IsNotResampled()
        {
            var geo = new FakeGeography();
            geo.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            WorldModel w = World(geo);
            SpatialNode n = Add(w, "E1");
            n.Deposit = new NodeDeposit();
            n.Deposit.Entries.Add(new DepositEntry("Cobalt", 1.0, 100));

            string id; DepositResolution r;
            Assert.False(w.TryBackfillOneDeposit(new HashSet<string>(), out id, out r));
            Assert.Equal(0, geo.Calls);
            Assert.Equal(1.0, n.Deposit.ShareOf("Cobalt"), 9);
        }

        [Fact]
        public void TheCandidateSetDrains_AndDoesNotLoopForever()
        {
            // Sampled nodes gain a deposit, barren ones are demoted out of the mineable kinds --
            // so both leave the set on their own. This is the property that makes the pass finite.
            var geo = new FakeGeography();
            geo.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            WorldModel w = World(geo);
            for (int i = 0; i < 5; i++) Add(w, "E" + i);

            int done = DrainBackfill(w, new HashSet<string>());

            Assert.Equal(5, done);
            Assert.Equal(5, geo.Calls);
            foreach (SpatialNode n in w.Graph.Nodes.Values) Assert.NotNull(n.Deposit);
        }

        // Barren ground used to leave the set by demoting the node's kind out of the mineable ones.
        // With the kind gone from the decision, what removes it is the empty deposit it comes away
        // holding: a barren node has a deposit, so it is no longer a candidate. See FAC-102.
        [Fact]
        public void BarrenGround_LeavesTheSet_WithoutASentinel()
        {
            var geo = new FakeGeography(); // samplable, finds nothing
            WorldModel w = World(geo);
            SpatialNode n = Add(w, "E1");

            int done = DrainBackfill(w, new HashSet<string>());

            Assert.Equal(1, done);
            Assert.Equal(1, geo.Calls);              // guard: exactly one sample, never repeated
            Assert.NotNull(n.Deposit);               // holds an empty deposit, not none
            Assert.True(n.Deposit.IsBarren);
            Assert.False(NodeContent.IsMineable(n));
        }

        [Fact]
        public void ADeclinedNode_IsRetriedOnce_ThenLeftAlone()
        {
            // Deep space outside every authored feature. The node keeps its kind and has no deposit,
            // so it looks exactly like one never tried -- the attempted set is the only thing that
            // stops it being resampled on every single pass forever.
            var geo = new FakeGeography { Samplable = false };
            WorldModel w = World(geo);
            SpatialNode n = Add(w, "N1");
            var attempted = new HashSet<string>();

            string id; DepositResolution r;
            Assert.True(w.TryBackfillOneDeposit(attempted, out id, out r));
            Assert.Equal(DepositResolution.Declined, r);
            Assert.Null(n.Deposit);
            Assert.False(NodeContent.IsMineable(n));   // untouched, exactly as before

            Assert.False(w.TryBackfillOneDeposit(attempted, out id, out r));
            Assert.Equal(1, geo.Calls);
        }

        [Fact]
        public void AFreshAttemptedSet_RetriesADeclinedNode()
        {
            // A restart clears the set on purpose: the authored world features a declined node
            // depends on may have changed since.
            var geo = new FakeGeography { Samplable = false };
            WorldModel w = World(geo);
            Add(w, "N1");

            string id; DepositResolution r;
            w.TryBackfillOneDeposit(new HashSet<string>(), out id, out r);
            w.TryBackfillOneDeposit(new HashSet<string>(), out id, out r);

            Assert.Equal(2, geo.Calls);
        }

        [Fact]
        public void BackfillAndExpand_ProduceTheSameDeposit_ForTheSameGround()
        {
            // A scanned node and an expanded one over identical ground must not disagree; they are
            // the same question asked by two callers.
            var geoA = new FakeGeography();
            geoA.Found.Add(new OreObservation("Cobalt", 70, 10.0, 5.0));
            geoA.Found.Add(new OreObservation("Silicon", 30, 10.0, 5.0));
            WorldModel a = World(geoA);
            SpatialNode scanned = Add(a, "E1");
            string id; DepositResolution r;
            a.TryBackfillOneDeposit(new HashSet<string>(), out id, out r);

            var geoB = new FakeGeography();
            geoB.Found.AddRange(geoA.Found);
            WorldModel b = World(geoB);
            SpatialNode expanded = Add(b, "N1");
            b.ResolveDeposit(expanded);

            Assert.Equal(scanned.Deposit.ShareOf("Cobalt"), expanded.Deposit.ShareOf("Cobalt"), 9);
            Assert.Equal(NodeDisplay.DominantOre(scanned), NodeDisplay.DominantOre(expanded));
        }
    }
}
