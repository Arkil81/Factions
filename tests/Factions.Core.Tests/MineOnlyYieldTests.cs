using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="NodeYieldEntry.MineOnly"/> — the split between the two consumers of the node-kind
    /// yield table. The passive per-tick income mint must skip a MineOnly entry; Mine must read the
    /// table unchanged and treat it as an ordinary target. Stone is the material this exists for:
    /// its supply is effectively unlimited, so an effort gate is what bounds it.
    /// </summary>
    public class MineOnlyYieldTests
    {
        private static SpatialNode AddOwnedNode(WorldModel w, string id)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength    = 1.0;
            n.Radius          = 24;
            // Ore comes from the ground since FAC-102, so a node that is meant to mint has a deposit.
            return TestSupport.WithOre(n, "Iron");
        }

        private static long Inv(SpatialNode n, string subTypeId)
        {
            long v;
            return n.Inventory.TryGetValue(subTypeId, out v) ? v : 0;
        }

        // ---- the income pass skips MineOnly entries -------------------------------------------

        [Fact]
        public void AccrueIncome_DoesNotMintTheMineOnlyStoneOre_ButStillMintsOrdinaryOre()
        {
            WorldModel w = TestSupport.World(42);
            SpatialNode n = AddOwnedNode(w, "N1");

            w.AccrueIncome();

            Assert.True(Inv(n, "Ore:Iron") > 0, "an ordinary Ore entry still mints passively");
            Assert.Equal(0, Inv(n, "Ore:Stone"));
        }

        // FAC-233 reversed the 2026-08-23 floor: gravel is mined as Ore:Stone and refined, never
        // minted. The floor was taken before the mining half had run in anger; by 2026-09-02 the
        // mined path moved 84x the mint's volume and every faction with territory held mineable
        // nodes, so nothing was standing on the floor.
        [Fact]
        public void AccrueIncome_NeverMintsGravel_OnAMineableNode()
        {
            WorldModel w = TestSupport.World(42);
            SpatialNode n = AddOwnedNode(w, "N1");

            w.AccrueIncome();

            Assert.Equal(0, Inv(n, "Ingot:Stone"));
            Assert.True(Inv(n, "Ore:Iron") > 0, "the deposit's own ore still mints");
        }

        // The case the mint existed for, and the one it is safe to remove because nothing feeds off
        // it: a node with nothing under it now mints nothing at all rather than gravel.
        [Fact]
        public void AccrueIncome_NeverMintsGravel_OnANodeWithNoDeposit()
        {
            WorldModel w = TestSupport.World(42);
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength    = 1.0;
            n.Radius          = 24;

            w.AccrueIncome();

            Assert.Equal(0, Inv(n, "Ingot:Stone"));
        }

        [Fact]
        public void AccrueIncome_SkipsMineOnly_OnEveryMineableKind()
        {
            WorldModel w = TestSupport.World(42);
            SpatialNode oreField  = AddOwnedNode(w, "N1");
            SpatialNode rareField = AddOwnedNode(w, "N2");
            SpatialNode iceField  = AddOwnedNode(w, "N3");

            w.AccrueIncome();

            Assert.Equal(0, Inv(oreField,  "Ore:Stone"));
            Assert.Equal(0, Inv(rareField, "Ore:Stone"));
            Assert.Equal(0, Inv(iceField,  "Ore:Stone"));
        }

        // Clearing the flag no longer makes a table Ore entry mint, and the reason is worth pinning:
        // the deposit owns the Ore and RareOre channels outright, so a table entry on those channels
        // never reaches a node that has ground. MineOnly survives that precedence because
        // NodeYieldResolver keeps MineOnly entries verbatim -- which is what still lets Mine target
        // stone. See FAC-102.
        [Fact]
        public void ATableOreEntry_NeverReachesANodeWithADeposit_FlagOrNot()
        {
            WorldModel w = TestSupport.World(42);
            foreach (NodeYieldEntry e in w.Tuning.NodeYieldTable)
                if (e.SubTypeId == "Ore:Stone") e.MineOnly = false;
            SpatialNode n = AddOwnedNode(w, "N1");

            w.AccrueIncome();

            Assert.Equal(0, Inv(n, "Ore:Stone"));
            Assert.True(Inv(n, "Ore:Iron") > 0, "the deposit's own ore is what mints");
        }

        // ---- Mine reads the table unchanged ---------------------------------------------------

        [Fact]
        public void MineTargeting_TreatsAMineOnlyEntryAsAnOrdinaryTarget()
        {
            FactionTuning tuning = TestSupport.Tuning();
            var node = TestSupport.WithOre(
                new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 }, "Iron");
            var requested = new Dictionary<string, double> { ["Ore:Stone"] = 75, ["Ore:Iron"] = 25 };

            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, requested);

            Assert.Equal(0.75, weights["Ore:Stone"], 6);
            Assert.Equal(0.25, weights["Ore:Iron"], 6);
        }

        // Stone comes with having ground worth drilling. There is no label left for it to depend on.
        [Fact]
        public void MineTargeting_NaturalMix_IncludesStone_ForAnyNodeWithADeposit()
        {
            FactionTuning tuning = TestSupport.Tuning();
            var node = TestSupport.WithOre(
                new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 }, "Iron");
            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, null);
            Assert.True(weights.ContainsKey("Ore:Stone"));
        }

        [Fact]
        public void MineTargeting_NaturalMix_ExcludesStone_OnANodeWithNoDeposit()
        {
            FactionTuning tuning = TestSupport.Tuning();
            var node = new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 };

            Dictionary<string, double> weights = MineTargeting.ComputeTargetWeights(node, tuning, null);

            Assert.False(weights.ContainsKey("Ore:Stone"));
        }

        // ---- the shipped defaults ------------------------------------------------------------

        // Stone used to sit on the three mineable kinds' rows and nowhere else, which was the table's
        // way of saying "only a node with ore in it has rock worth drilling". With one global table
        // that property moves to the node: MineTargeting reaches the entry only for a node whose
        // deposit says it is mineable. See FAC-102.
        [Fact]
        public void ShippedDefaults_CarryExactlyOneMineOnlyEntry_AndItIsStoneOre()
        {
            FactionTuning tuning = TestSupport.Tuning();
            Assert.NotNull(tuning.NodeYieldTable);

            var mineOnly = new List<NodeYieldEntry>();
            foreach (NodeYieldEntry e in tuning.NodeYieldTable)
                if (e.MineOnly) mineOnly.Add(e);

            NodeYieldEntry stone = Assert.Single(mineOnly);
            Assert.Equal("Ore:Stone", stone.SubTypeId);
            Assert.Equal("Ore", stone.ItemKind);
            Assert.True(stone.Weight > 0, "a zero weight would make Mine skip it entirely");
        }

        // The passive mint must never see it: an effort-gated entry that minted for free would give
        // every node in the world a stone income.
        [Fact]
        public void MineOnlyStone_IsNeverMintedPassively()
        {
            FactionTuning tuning = TestSupport.Tuning();
            foreach (NodeYieldEntry e in tuning.NodeYieldTable)
                if (e.SubTypeId == "Ore:Stone") Assert.True(e.MineOnly);
        }

        // ---- the Ingot channel is gone, both halves of it (FAC-233) ---------------------------

        // The row and the scale are one change. A row with no scale mints nothing but still reads as
        // a live table entry; a scale with no row is worse, because Economy.NodeItemIncome consults
        // the scale alone and would keep valuing every node for gravel it never receives.
        [Fact]
        public void ShippedDefaults_CarryNoIngotChannel_NeitherRowNorScale()
        {
            FactionTuning tuning = TestSupport.Tuning();

            foreach (NodeYieldEntry e in tuning.NodeYieldTable)
                Assert.NotEqual("Ingot", e.ItemKind);

            Assert.Equal(0.0, tuning.FreeMintScaleFor(ItemKind.Ingot), 6);
        }

        // The consequence that made removing the scale mandatory rather than tidy: a node's scored
        // net worth must not include an income channel that no longer exists.
        [Fact]
        public void NodeItemIncome_NoLongerCreditsGravel()
        {
            FactionTuning tuning = TestSupport.Tuning();
            var node = TestSupport.WithOre(
                new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 }, "Iron");

            Assert.Equal(0, Economy.SourceItemYield(node, 1.0, tuning, ItemKind.Ingot));
        }

    }
}
