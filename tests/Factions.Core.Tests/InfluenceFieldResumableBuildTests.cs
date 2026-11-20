using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="WorldModel.BeginInfluenceSnapshotBuild"/>/<see cref="WorldModel.AdvanceInfluenceSnapshotBuild"/>/
    /// <see cref="WorldModel.CommitInfluenceSnapshotBuild"/>/<see cref="WorldModel.AbandonInfluenceSnapshotBuild"/> --
    /// the Slice 2 resumable counterpart to <see cref="WorldModel.PrepareInfluenceSnapshot"/>, added so
    /// the live host's global pass never blocks one frame for a large world's full rebuild. Gate A
    /// evidence (.hermes/plans/2026-08-17_094339-global-pass-econ-cursor.md) found this rebuild is the
    /// dominant cost (~93%) of the "econ" perf bucket at live scale.</summary>
    public class InfluenceFieldResumableBuildTests
    {
        private static SpatialNode Owned(WorldModel w, string id, string tag, double x, double hold)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, 0, 0), 0.5, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static WorldModel BuildRepresentativeWorld()
        {
            var w = new WorldModel();
            Owned(w, "A1", "AAA", 0, 1.0);
            Owned(w, "A2", "AAA", 30, 0.8);
            Owned(w, "B1", "BBB", 60, 0.5);
            Owned(w, "B2", "BBB", 90, 0.3);
            Owned(w, "C1", "CCC", 500, 0.9); // far away -- outside every other node's cutoff radius
            return w;
        }

        private static void RunChunkedBuildToCompletion(WorldModel w)
        {
            w.BeginInfluenceSnapshotBuild();
            while (!w.AdvanceInfluenceSnapshotBuild()) { }
            Assert.False(w.IsInfluenceSnapshotBuildStale());
            w.CommitInfluenceSnapshotBuild();
        }

        [Fact]
        public void ChunkedBuild_MatchesMonolithicBuild_ForEveryNodePair()
        {
            WorldModel monolithic = BuildRepresentativeWorld();
            monolithic.PrepareInfluenceSnapshot();

            WorldModel chunked = BuildRepresentativeWorld();
            RunChunkedBuildToCompletion(chunked);

            string[] tags = { "AAA", "BBB", "CCC" };
            foreach (string nodeId in new[] { "A1", "A2", "B1", "B2", "C1" })
                foreach (string tag in tags)
                {
                    Assert.Equal(
                        monolithic.Influence.SampleAt(nodeId, tag),
                        chunked.Influence.SampleAt(nodeId, tag));
                    Assert.Equal(
                        monolithic.Influence.ControlAt(nodeId, tag),
                        chunked.Influence.ControlAt(nodeId, tag));
                }
        }

        [Fact]
        public void AdvanceSnapshotBuild_ReturnsTrueExactlyOnceAllRowsProcessed()
        {
            WorldModel w = BuildRepresentativeWorld(); // 5 nodes
            w.BeginInfluenceSnapshotBuild();

            for (int i = 0; i < 4; i++)
                Assert.False(w.AdvanceInfluenceSnapshotBuild(), "should not report done before the last row");
            Assert.True(w.AdvanceInfluenceSnapshotBuild(), "should report done on the last row");
        }

        [Fact]
        public void OldSnapshot_RemainsReadable_UntilCommit()
        {
            WorldModel w = BuildRepresentativeWorld();
            w.PrepareInfluenceSnapshot();
            double before = w.Influence.SampleAt("B1", "AAA");

            // Mutate hold strength (would change the influence value if rebuilt) and start a rebuild,
            // but only advance partway through -- the OLD snapshot must still be what readers see.
            w.Graph.Nodes["A1"].HoldStrength = 5.0;
            w.BeginInfluenceSnapshotBuild();
            w.AdvanceInfluenceSnapshotBuild();

            Assert.Equal(before, w.Influence.SampleAt("B1", "AAA"));

            while (!w.AdvanceInfluenceSnapshotBuild()) { }
            Assert.False(w.IsInfluenceSnapshotBuildStale());
            w.CommitInfluenceSnapshotBuild();

            Assert.NotEqual(before, w.Influence.SampleAt("B1", "AAA"));
        }

        [Fact]
        public void NodeRemovedMidBuild_MarksStale_AndAbandonLeavesOldSnapshotIntact()
        {
            WorldModel w = BuildRepresentativeWorld();
            w.PrepareInfluenceSnapshot();
            double before = w.Influence.SampleAt("B1", "AAA");

            w.BeginInfluenceSnapshotBuild();
            w.AdvanceInfluenceSnapshotBuild();
            w.Graph.Nodes.Remove("C1");
            Assert.True(w.IsInfluenceSnapshotBuildStale());

            w.AbandonInfluenceSnapshotBuild();
            Assert.Equal(before, w.Influence.SampleAt("B1", "AAA"));

            // A fresh build over the now-smaller graph completes cleanly and is not stale.
            w.BeginInfluenceSnapshotBuild();
            while (!w.AdvanceInfluenceSnapshotBuild()) { }
            Assert.False(w.IsInfluenceSnapshotBuildStale());
            w.CommitInfluenceSnapshotBuild();
        }

        [Fact]
        public void NodeAddedMidBuild_MarksStale()
        {
            WorldModel w = BuildRepresentativeWorld();
            w.BeginInfluenceSnapshotBuild();
            w.AdvanceInfluenceSnapshotBuild();
            Owned(w, "D1", "DDD", 200, 0.5);
            Assert.True(w.IsInfluenceSnapshotBuildStale());
            w.AbandonInfluenceSnapshotBuild();
        }

        [Fact]
        public void RepeatedBuilds_ReuseBuffers_AndStayCorrect()
        {
            WorldModel w = BuildRepresentativeWorld();

            for (int i = 0; i < 3; i++)
            {
                RunChunkedBuildToCompletion(w);
            }

            WorldModel monolithic = BuildRepresentativeWorld();
            monolithic.PrepareInfluenceSnapshot();

            Assert.Equal(monolithic.Influence.SampleAt("A2", "BBB"), w.Influence.SampleAt("A2", "BBB"));
        }
    }
}
