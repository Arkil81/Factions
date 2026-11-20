using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-068 slice 4: a node's deposit replaces the Ore and RareOre channels of the
    /// node-kind yield table, and nothing else. See docs/ore-geography.md.</summary>
    public class NodeYieldResolverTests
    {
        private static SpatialNode Node(params DepositEntry[] entries)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;   // SelectionScore multiplies by this; 0 would make every score 0
            n.Richness = 1.0;       // and by this, since FAC-219
            if (entries.Length > 0)
            {
                n.Deposit = new NodeDeposit();
                n.Deposit.Entries.AddRange(entries);
            }
            return n;
        }

        private static Dictionary<string, double> WeightsByKind(NodeYieldEntry[] entries, ItemKind kind)
        {
            var byKind = new Dictionary<string, double>();
            foreach (NodeYieldEntry e in entries)
                if (e.ItemKind == kind.ToString() && !e.MineOnly) byKind[e.SubTypeId] = e.Weight;
            return byKind;
        }

        [Fact]
        public void NoDeposit_ReturnsTheKindTableVerbatim()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node();

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            Assert.Same(tuning.NodeYieldTable, entries);
        }

        [Fact]
        public void BarrenDeposit_AlsoReturnsTheTable()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node();
            n.Deposit = new NodeDeposit();

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            Assert.Same(tuning.NodeYieldTable, entries);
        }

        [Fact]
        public void Deposit_ReplacesTheOreChannel_WithItsOwnOres()
        {
            FactionTuning tuning = TestSupport.Tuning();
            // The shipped table's Ore channel is Iron/Nickel/Cobalt/Silicon/Magnesium.
            SpatialNode n = Node(new DepositEntry("Cobalt", 0.75, 75),
                new DepositEntry("Silicon", 0.25, 25));

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);
            Dictionary<string, double> ore = WeightsByKind(entries, ItemKind.Ore);

            Assert.False(ore.ContainsKey("Ore:Iron"));      // present in the table, absent from this ground
            Assert.Equal(0.75, ore["Ore:Cobalt"], 9);
            Assert.Equal(0.25, ore["Ore:Silicon"], 9);
        }

        [Fact]
        public void SharesAreRenormalisedWithinTheOreChannel()
        {
            // Every ore is ItemKind.Ore, so there is one channel and it sums to 1.0 -- which keeps
            // the table's existing contract and leaves magnitude to the free-mint scale.
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Iron", 0.5, 50),
                new DepositEntry("Gold", 0.3, 30),
                new DepositEntry("Silver", 0.2, 20));

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);
            Dictionary<string, double> ore = WeightsByKind(entries, ItemKind.Ore);

            double sum = 0;
            foreach (var kv in ore) sum += kv.Value;
            Assert.Equal(1.0, sum, 9);
            Assert.Equal(0.3, ore["Ore:Gold"], 9);
            Assert.Empty(WeightsByKind(entries, ItemKind.RareOre));
        }

        [Fact]
        public void MineOnlyEntries_SurviveADeposit_SoStoneKeepsItsSource()
        {
            // Stone is the base voxel material everywhere there is rock, gated by effort rather than
            // location, so it never appears in an ore-map sample. Taking it from the table is what
            // keeps FAC-050's gravel source alive.
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Iron", 1.0, 100));

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            NodeYieldEntry stone = entries.FirstOrDefaultOrNull(e => e.SubTypeId == "Ore:Stone" && e.MineOnly);
            Assert.NotNull(stone);
        }

        [Fact]
        public void NonOreChannels_PassThroughUntouched()
        {
            // The ground says what ore is in it. It says nothing about what a settlement manufactures,
            // so ProcessedGood / Hydrogen / Oxygen / the Ingot gravel trickle are not the deposit's
            // business.
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode withDeposit = Node(new DepositEntry("Iron", 1.0, 100));
            SpatialNode plain = Node();

            NodeYieldEntry[] before = NodeYieldResolver.EntriesFor(plain, tuning, NullItemRegistry.Instance);
            NodeYieldEntry[] after = NodeYieldResolver.EntriesFor(withDeposit, tuning, NullItemRegistry.Instance);

            foreach (ItemKind kind in new[] { ItemKind.ProcessedGood, ItemKind.Hydrogen, ItemKind.Oxygen, ItemKind.Ingot })
            {
                Dictionary<string, double> b = WeightsByKind(before, kind);
                Dictionary<string, double> a = WeightsByKind(after, kind);
                Assert.Equal(b.Count, a.Count);
                foreach (var kv in b) Assert.Equal(kv.Value, a[kv.Key], 9);
            }
        }

        [Fact]
        public void UnknownSubType_IsTreatedAsOre_NotDropped()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Thorium", 1.0, 100));

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            Assert.Equal(1.0, WeightsByKind(entries, ItemKind.Ore)["Ore:Thorium"], 9);
        }

        [Fact]
        public void NullRegistry_StillProducesADeposit_ClassifiedAsOre()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Gold", 1.0, 100));

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, null);

            Assert.Equal(1.0, WeightsByKind(entries, ItemKind.Ore)["Ore:Gold"], 9);
        }

        [Fact]
        public void RepeatedCalls_ReuseTheSameArray_SoTheObservationLoopsDoNotAllocate()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Iron", 1.0, 100));

            NodeYieldEntry[] first = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);
            NodeYieldEntry[] second = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            Assert.Same(first, second);
        }

        // The cache keys on the deposit and the tuning instance. It used to key on the node's kind as
        // well, because a demotion changed which non-ore channels applied; with no kind left, the same
        // node and the same deposit must return the very same array rather than rebuilding.
        [Fact]
        public void SameDepositAndTuning_ServesTheCachedArray()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Iron", 1.0, 100));

            NodeYieldEntry[] first = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);
            NodeYieldEntry[] second = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            Assert.Same(first, second);
        }

        [Fact]
        public void ChangingTheDeposit_RebuildsTheCache()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Iron", 1.0, 100));

            NodeYieldEntry[] first = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);
            n.Deposit = new NodeDeposit();
            n.Deposit.Entries.Add(new DepositEntry("Cobalt", 1.0, 100));
            NodeYieldEntry[] second = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            Assert.NotSame(first, second);
            Assert.Equal(1.0, WeightsByKind(second, ItemKind.Ore)["Ore:Cobalt"], 9);
        }

        // ---- the Mine path reads it too --------------------------------------------------------

        [Fact]
        public void MineNaturalMix_ComesFromTheDeposit()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Cobalt", 0.8, 80),
                new DepositEntry("Silicon", 0.2, 20));

            Dictionary<string, double> weights =
                MineTargeting.ComputeTargetWeights(n, tuning, null, NullItemRegistry.Instance);

            Assert.False(weights.ContainsKey("Ore:Iron"));
            Assert.True(weights["Ore:Cobalt"] > weights["Ore:Silicon"]);
        }

        [Fact]
        public void MineRequest_ForAnOreTheGroundLacks_IsRejected()
        {
            // The whole point of FAC-068: a node must not accept an order for ore that is not there.
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Iron", 1.0, 100));
            var requested = new Dictionary<string, double> { { "Ore:Platinum", 1.0 } };

            Dictionary<string, double> weights =
                MineTargeting.ComputeTargetWeights(n, tuning, requested, NullItemRegistry.Instance);

            Assert.Empty(weights);
        }

        [Fact]
        public void MineRequest_ForAnOreTheGroundHas_IsAccepted()
        {
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Platinum", 1.0, 100));
            var requested = new Dictionary<string, double> { { "Ore:Platinum", 1.0 } };

            Dictionary<string, double> weights =
                MineTargeting.ComputeTargetWeights(n, tuning, requested, NullItemRegistry.Instance);

            Assert.Equal(1.0, weights["Ore:Platinum"], 9);
        }

        [Fact]
        public void TwoNodesOfTheSameKind_NowDifferByGround()
        {
            // Before this work every OreField in the world was byte-identical, which is why Mine
            // source selection could only ever fall back to distance.
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode ironNode = Node(new DepositEntry("Iron", 1.0, 100));
            SpatialNode platNode = Node(new DepositEntry("Platinum", 1.0, 100));
            var wantPlatinum = new Dictionary<string, double> { { "Ore:Platinum", 1.0 } };

            double atIron = MineTargeting.SelectionScore(ironNode, tuning, wantPlatinum, NullItemRegistry.Instance);
            double atPlat = MineTargeting.SelectionScore(platNode, tuning, wantPlatinum, NullItemRegistry.Instance);

            // Guard: both nodes must be scorable at all, or "atIron == 0" would pass vacuously --
            // SelectionScore multiplies by Richness and HoldStrength, so an unset fixture
            // zeroes both sides and the test proves nothing.
            var wantIron = new Dictionary<string, double> { { "Ore:Iron", 1.0 } };
            Assert.True(MineTargeting.SelectionScore(ironNode, tuning, wantIron, NullItemRegistry.Instance) > 0);

            Assert.Equal(0, atIron);
            Assert.True(atPlat > 0);
        }

        [Fact]
        public void DepositEntries_CarryThePrefixedInventoryKey_LikeTheTableDoes()
        {
            // The defect this exists to stop: a deposit's subtypes are bare ("Iron") and
            // NodeYieldEntry.SubTypeId is the inventory key ("Ore:Iron"). Emitting the bare form
            // made a deposit mint under a key nothing else read, and no weight assertion could see it.
            FactionTuning tuning = TestSupport.Tuning();
            SpatialNode n = Node(new DepositEntry("Iron", 1.0, 100));

            NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(n, tuning, NullItemRegistry.Instance);

            NodeYieldEntry iron = entries.FirstOrDefaultOrNull(e => e.ItemKind == "Ore" && !e.MineOnly);
            Assert.NotNull(iron);
            Assert.Equal("Ore:Iron", iron.SubTypeId);

            // and it matches the prefixed form the shipped table still uses for the one ore it states
            NodeYieldEntry fromTable = tuning.NodeYieldTable
                .FirstOrDefaultOrNull(e => e.SubTypeId == "Ore:Stone");
            Assert.NotNull(fromTable);
            Assert.Contains(":", fromTable.SubTypeId);
        }
    }

    internal static class NodeYieldEntryTestExtensions
    {
        public static NodeYieldEntry FirstOrDefaultOrNull(this NodeYieldEntry[] entries,
            System.Func<NodeYieldEntry, bool> predicate)
        {
            foreach (NodeYieldEntry e in entries) if (predicate(e)) return e;
            return null;
        }
    }
}
