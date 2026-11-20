using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PresenceReinforceTests
    {
        [Fact]
        public void Accumulate_BelowTarget_ReturnsFalseAndPersistsProgress()
        {
            var st = new FactionState { Tag = "EXP" };

            bool fired = PresenceReinforce.Accumulate(st, "N001", 300, 1800);

            Assert.False(fired);
            Assert.Equal(300, st.ReinforcePresenceSeconds["N001"]);
        }

        [Fact]
        public void Accumulate_AcrossMultipleCalls_SumsProgress()
        {
            var st = new FactionState { Tag = "EXP" };

            PresenceReinforce.Accumulate(st, "N001", 300, 1800);
            PresenceReinforce.Accumulate(st, "N001", 300, 1800);

            Assert.Equal(600, st.ReinforcePresenceSeconds["N001"]);
        }

        [Fact]
        public void Accumulate_CrossingTarget_FiresAndResetsToZero()
        {
            var st = new FactionState { Tag = "EXP" };
            st.ReinforcePresenceSeconds["N001"] = 1700;

            bool fired = PresenceReinforce.Accumulate(st, "N001", 200, 1800);

            Assert.True(fired);
            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N001"));
        }

        [Fact]
        public void Accumulate_IsPerNode_DoesNotCrossContaminate()
        {
            var st = new FactionState { Tag = "EXP" };

            PresenceReinforce.Accumulate(st, "N001", 1000, 1800);
            PresenceReinforce.Accumulate(st, "N002", 50, 1800);

            Assert.Equal(1000, st.ReinforcePresenceSeconds["N001"]);
            Assert.Equal(50, st.ReinforcePresenceSeconds["N002"]);
        }

        // ---- Hop spread (docs/economy-territory.md -> "Player-faction claiming" -> Growing) ----

        /// <summary>Builds a straight chain N001-N002-... connected in order, one node per entry in
        /// <paramref name="ownersInOrder"/> with that entry as its owner tag (null = unclaimed).</summary>
        private static WorldModel Chain(params string[] ownersInOrder)
        {
            WorldModel w = TestSupport.World();
            string prev = null;
            for (int i = 0; i < ownersInOrder.Length; i++)
            {
                string id = "N" + (i + 1).ToString("000");
                SpatialNode n = w.Graph.Add(new Vec3(i * 500.0, 0, 0), 0.5, id);
                n.OwnerFactionTag = ownersInOrder[i];
                if (prev != null) w.Graph.Connect(prev, id);
                prev = id;
            }
            return w;
        }

        private static WorldModel OwnedChain(string tag, int length)
        {
            var owners = new string[length];
            for (int i = 0; i < length; i++) owners[i] = tag;
            return Chain(owners);
        }

        [Fact]
        public void Spread_MaxHopsZero_CreditsOnlyThePresenceNode()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = OwnedChain("EXP", 3);

            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001" }, 100, 1800, 0, 0.5, null);

            Assert.Equal(100, st.ReinforcePresenceSeconds["N001"]);
            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N002"));
        }

        [Fact]
        public void Spread_DecaysGeometricallyPerHop()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = OwnedChain("EXP", 4);

            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001" }, 100, 1800, 3, 0.5, null);

            Assert.Equal(100, st.ReinforcePresenceSeconds["N001"]);
            Assert.Equal(50, st.ReinforcePresenceSeconds["N002"]);
            Assert.Equal(25, st.ReinforcePresenceSeconds["N003"]);
            Assert.Equal(12.5, st.ReinforcePresenceSeconds["N004"]);
        }

        [Fact]
        public void Spread_StopsAtMaxHops()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = OwnedChain("EXP", 5);

            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001" }, 100, 1800, 3, 0.5, null);

            Assert.True(st.ReinforcePresenceSeconds.ContainsKey("N004"));   // 3 hops — the edge of range
            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N005"));  // 4 hops — out
        }

        [Fact]
        public void Spread_SkipsForeignNodesButStillTraversesThroughThem()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = Chain("EXP", "NPC", "EXP");

            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001" }, 100, 1800, 3, 0.5, null);

            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N002")); // owned by someone else
            Assert.Equal(25, st.ReinforcePresenceSeconds["N003"]);         // still reached, at hop 2
        }

        [Fact]
        public void Spread_SkipsUnownedAndClaimableNodes()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = Chain("EXP", null, NeutralFaction.Tag);

            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001" }, 100, 1800, 3, 0.5, null);

            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N002"));
            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N003"));
        }

        [Fact]
        public void Spread_UsesClosestSource_DoesNotStack()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = OwnedChain("EXP", 3);

            // Members standing at both ends; N002 is one hop from each and must be credited once.
            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001", "N003" }, 100, 1800, 3, 0.5, null);

            Assert.Equal(50, st.ReinforcePresenceSeconds["N002"]);
            Assert.Equal(100, st.ReinforcePresenceSeconds["N001"]);
            Assert.Equal(100, st.ReinforcePresenceSeconds["N003"]);
        }

        [Fact]
        public void Spread_FiresPerNode_AndReportsHopDistance()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = OwnedChain("EXP", 3);
            st.ReinforcePresenceSeconds["N003"] = 1790; // a hop-2 node about to cross

            var fired = new List<PresenceReinforceFire>();
            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001" }, 100, 1800, 3, 0.5, fired);

            Assert.Single(fired);
            Assert.Equal("N003", fired[0].NodeId);
            Assert.Equal(2, fired[0].Hops);
            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N003")); // reset on fire
        }

        [Fact]
        public void Spread_SkipsNegligibleWeights()
        {
            var st = new FactionState { Tag = "EXP" };
            WorldModel w = OwnedChain("EXP", 9);

            // falloff 0.1: hop 2 is already 0.01, hop 3 is 0.001 — below the credit floor.
            PresenceReinforce.AccumulateWithSpread(st, w.Graph, new[] { "N001" }, 100, 1800, 8, 0.1, null);

            Assert.True(st.ReinforcePresenceSeconds.ContainsKey("N003"));
            Assert.False(st.ReinforcePresenceSeconds.ContainsKey("N004"));
        }

        [Fact]
        public void Accumulate_SurvivesARoundTripThroughPersistence()
        {
            var st = new FactionState { Tag = "EXP" };
            PresenceReinforce.Accumulate(st, "N001", 900, 1800); // a "disconnect" mid-progress

            BrainDoc doc = FactionStore.ToBrainDoc(st, System.Linq.Enumerable.Empty<SpatialNode>(), FactionStore.DefaultLogCap);
            FactionState reloaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Equal(900, reloaded.ReinforcePresenceSeconds["N001"]);
        }
    }
}
