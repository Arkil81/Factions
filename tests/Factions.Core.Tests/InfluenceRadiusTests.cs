using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-250: how far a node projects influence is <b>earned</b>, and decided in one place.
    ///
    /// <para>It used to be decided in three that disagreed — `WorldModel.SourceRadius` 24 km,
    /// `SourceMapping.BaseRadiusKm` 40 km, and a 40 km field initialiser. A census of the live estate
    /// found all 503 nodes at exactly 24.0, so the divergence was latent: a node arriving through a
    /// dormant path would have projected 4.6x the volume of every other node on the map.</para></summary>
    public class InfluenceRadiusTests
    {
        private static SpatialNode Node(bool founded = false, params int[] capabilityLevels)
        {
            var n = new SpatialNode { Id = "N1", OwnerFactionTag = "A", HoldStrength = 1.0 };
            if (founded) n.StableStationId = 42;
            var kinds = FactionTuning.AllCapabilities;
            for (int i = 0; i < capabilityLevels.Length; i++) n.Capabilities[kinds[i]] = capabilityLevels[i];
            return n;
        }

        /// <summary>The shape the operator asked for: a floor, a large step at founding, and
        /// capabilities that keep paying off on top.</summary>
        [Fact]
        public void RadiusIsEarned_FloorThenFoundingThenCapabilities()
        {
            var t = TestSupport.Tuning();

            double bareClaim = InfluenceRadius.For(Node(), t);
            double founded = InfluenceRadius.For(Node(founded: true), t);
            double developed = InfluenceRadius.For(Node(founded: true, 1), t);
            double deeper = InfluenceRadius.For(Node(founded: true, 3), t);
            double broader = InfluenceRadius.For(Node(founded: true, 1, 1, 1), t);

            Assert.Equal(t.InfluenceRadiusFloorKm, bareClaim, 6);
            Assert.Equal(t.InfluenceRadiusFoundedKm, founded, 6);
            Assert.True(founded - bareClaim > t.InfluenceRadiusPerCapabilityLevelKm,
                "founding must be a bigger step than one capability level, or 'a big leap' is not what it is");
            Assert.True(developed > founded, "installing a capability must pay off in reach");
            Assert.True(deeper > developed, "LEVELS count, not just presence -- deepening a capability pays too");
            Assert.Equal(deeper, broader, 6);   // three levels is three levels, however they are spread
        }

        /// <summary>The ceiling, and where it came from: 40 km was the dormant scan-backed constant
        /// this ticket removed. It survives as what a fully developed station reaches.</summary>
        [Fact]
        public void TheCeilingHolds_AndIsTheOldDormantConstant()
        {
            var t = TestSupport.Tuning();
            Assert.Equal(40.0, t.InfluenceRadiusMaxKm, 6);
            Assert.Equal(t.InfluenceRadiusMaxKm, InfluenceRadius.For(Node(founded: true, 99), t), 6);
        }

        /// <summary>An explicit `SpatialNode.Radius` still wins, because the offline sim's scenarios
        /// set it deliberately (a 200 km node that is meant to dominate its region). Losing that would
        /// silently rewrite every demo world.</summary>
        [Fact]
        public void AnExplicitRadius_StillOverridesTheDerivedOne()
        {
            var t = TestSupport.Tuning();
            var n = Node();
            n.Radius = 200;
            Assert.Equal(200.0, InfluenceRadius.For(n, t), 6);
        }

        /// <summary>No tuning, no node, and a nonsense floor all have to answer something usable —
        /// this is called from inside the influence snapshot, which cannot throw.</summary>
        [Fact]
        public void DegenerateInputs_FallBackRatherThanBreak()
        {
            Assert.Equal(InfluenceRadius.FallbackKm, InfluenceRadius.For(null, TestSupport.Tuning()), 6);
            Assert.Equal(InfluenceRadius.FallbackKm, InfluenceRadius.For(Node(), null), 6);

            var t = TestSupport.Tuning();
            t.InfluenceRadiusFloorKm = 0;
            Assert.Equal(InfluenceRadius.FallbackKm, InfluenceRadius.For(Node(), t), 6);
        }

        /// <summary>The field is derived, so it must not be persisted — a stored radius would pin a
        /// node to what it was worth the day it was saved and stop tracking the development it
        /// measures. A node that gains a station after a save/load must project the founded radius.</summary>
        [Fact]
        public void RadiusIsNotPersisted_SoItTracksDevelopmentAcrossAReload()
        {
            var t = TestSupport.Tuning();
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            n.OwnerFactionTag = "A"; n.HoldStrength = 1.0;
            Assert.Equal(t.InfluenceRadiusFloorKm, InfluenceRadius.For(n, t), 6);

            var st = new FactionState { Tag = "A" };
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("A"), FactionStore.DefaultLogCap);
            var graph2 = new SpatialGraph();
            FactionStore.ApplyBrainDoc(doc, graph2);
            SpatialNode loaded = graph2.Nodes["N1"];

            Assert.Equal(0.0, loaded.Radius, 6);   // nothing stored -> derive
            Assert.Equal(t.InfluenceRadiusFloorKm, InfluenceRadius.For(loaded, t), 6);

            // ...and the key does not survive into the JSON at all. Dropping the assignment alone
            // left `"Radius": 0.0` on every node -- a non-nullable double serialises its default --
            // which is several hundred dead keys per brain file against CLAUDE.md's lean-JSON rule.
            // Caught live: the acceptance line said the key would be ABSENT and it was still there.
            Assert.DoesNotContain("\"Radius\"", Newtonsoft.Json.JsonConvert.SerializeObject(doc));

            loaded.StableStationId = 7;            // it gets founded after the reload
            Assert.Equal(t.InfluenceRadiusFoundedKm, InfluenceRadius.For(loaded, t), 6);
        }

        /// <summary>The standing guard for the regression the live check can no longer see.
        ///
        /// <para>v0.159.1's acceptance was "the `Radius` key goes 542 → 0 in the live brains", and it
        /// passed. But that check consumed itself: with the key absent there is nothing left to read,
        /// so if host code ever starts assigning a radius again the live grep cannot notice — pointed
        /// out by world-builder-3 on closing the row, and correct.</para>
        ///
        /// <para>This is the guard that survives it. `WorldModel.CommitNode` is the single commit
        /// point for every host-created node (`ExpandFrom` and `SeedNodeAt` both reach it), and it
        /// must leave `Radius` at 0 so `InfluenceRadius` derives. An assignment reintroduced there
        /// would make the override win silently and pin the node to a fixed radius forever.</para>
        ///
        /// <para>The other half is a one-line grep, recorded here because it is the check nobody will
        /// re-derive: <c>git grep -n '\.Radius = ' -- 'src/*.cs'</c> must return **only**
        /// `src/Factions.Sim/Program.cs`. Core and the host assign it nowhere.</para></summary>
        [Fact]
        public void AHostCreatedNode_LeavesRadiusUnset_SoItDerives()
        {
            var w = TestSupport.World();
            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "P001");
            parent.OwnerFactionTag = "F";

            SpatialNode child = w.ExpandFrom("F", "P001");

            Assert.NotNull(child);
            Assert.Equal(0.0, child.Radius, 9);
            Assert.Equal(w.Tuning.InfluenceRadiusFloorKm, InfluenceRadius.For(child, w.Tuning), 6);
        }

        /// <summary>End to end: the field itself reads the derived radius, so a developed station
        /// genuinely projects further than a bare claim of identical hold. Without this the helper
        /// could be correct and unused.</summary>
        [Fact]
        public void TheInfluenceField_ReadsTheDerivedRadius()
        {
            double SampleAtDistance(bool founded, int capLevels)
            {
                var w = TestSupport.World();
                SpatialNode src = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "SRC");
                src.OwnerFactionTag = "A"; src.HoldStrength = 1.0;
                if (founded) src.StableStationId = 42;
                if (capLevels > 0) src.Capabilities[FactionTuning.AllCapabilities[0]] = capLevels;
                SpatialNode probe = w.Graph.Add(new Vec3(20, 0, 0), 0.5, "PROBE");
                probe.OwnerFactionTag = "B"; probe.HoldStrength = 1.0;
                w.PrepareInfluenceSnapshot();
                return w.Influence.SampleAt("PROBE", "A");
            }

            double claim = SampleAtDistance(false, 0);
            double founded = SampleAtDistance(true, 0);
            double developed = SampleAtDistance(true, 4);

            Assert.True(founded > claim, "a founded station must reach further than a bare claim; claim="
                + claim + " founded=" + founded);
            Assert.True(developed > founded, "a developed station must reach further still; founded="
                + founded + " developed=" + developed);
        }
    }
}
