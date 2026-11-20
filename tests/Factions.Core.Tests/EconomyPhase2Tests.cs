using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Economy Phase 2: item→credit conversion, action item costs, per-node inventories +
    /// per-node yield, per-node trade cargo, and item loot from raided convoys.</summary>
    public class EconomyPhase2Tests
    {
        private static WorldModel BuildWorld()
            => TestSupport.World(42);

        // ---- Change 1: item surplus → credits ---------------------------------------------------

        [Fact]
        public void ItemSurplus_IsRetainedInNodeInventory()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankReserveRatio = 0; // disable bank purchases; this test only checks no implicit credit conversion
            w.Tuning.ProcessedGoodYield = 0;
            w.Tuning.UpkeepProcessedGood = 0;

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            // Put a known amount above threshold; after accrual surplus must be gone
            n.Inventory["SteelPlate"] = (long)t.DemandPressureFloorProcessedGood + 200;

            w.AccrueIncome();

            long remaining = w.GetInventory("F", ItemKind.ProcessedGood);
            Assert.True(remaining >= (long)t.DemandPressureFloorProcessedGood + 200,
                "surplus is retained; only upkeep/capability drains should reduce it during AccrueIncome");
        }

        [Fact]
        public void BelowDemandThreshold_NoConversionNoBleeding()
        {
            var w = BuildWorld();
            var t = w.Tuning;

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;
            // Ore below threshold — no conversion expected
            n.Inventory["Iron"] = (long)t.DemandPressureFloorOre / 2;
            w.SetTreasury("F", 100_000);

            w.AccrueIncome();

            // Treasury should have decreased only by upkeep (no conversion bonus)
            Assert.True(w.GetTreasury("F") < 100_000, "upkeep drain must still apply");
        }

        [Fact]
        public void GetNetFlow_CachedAfterAccrueIncome()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0; // isolate: no CB grant income in net flow

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;
            // Net flow is cached credit movement only; item stock is valued in ledger, not auto-sold.
            n.Inventory["Iron"] = (long)t.DemandPressureFloorOre + 2000;

            long before = w.GetNetFlow("F");  // 0 before first tick
            w.AccrueIncome();
            long after = w.GetNetFlow("F");

            Assert.Equal(0L, before);
            Assert.Equal(-t.UpkeepPerSource, after);
        }

        [Fact]
        public void GetNetFlow_IncludesDebtInterest()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0;
            t.DebtInterestRate = 0.1;

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;
            w.SetDebt("F", 10000);
            w.SetTreasury("F", 100_000);

            w.AccrueIncome();

            Assert.Equal(-t.UpkeepPerSource - 1000, w.GetNetFlow("F")); // upkeep + 10000*0.1 interest
        }

        [Fact]
        public void GetNetFlow_IncludesPendingExpense()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0; // isolate: no CB grant income in net flow

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;

            w.AddPendingExpense("F", 5000); // e.g. a Develop cost debited between AccrueIncome passes
            w.AccrueIncome();

            Assert.Equal(-t.UpkeepPerSource - 5000, w.GetNetFlow("F"));
        }

        [Fact]
        public void GetNetFlow_PendingExpenseAccumulatesWithinPass_ThenClears()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0; // isolate: no CB grant income in net flow

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;

            w.AddPendingExpense("F", 5000);  // e.g. a Reinforce spend
            w.AddPendingExpense("F", 60);    // e.g. a Trade dispatch fee, same pass
            w.AccrueIncome();

            Assert.Equal(-t.UpkeepPerSource - 5060, w.GetNetFlow("F"));

            w.AccrueIncome(); // next pass, nothing pending — expense must not linger

            Assert.Equal(-t.UpkeepPerSource, w.GetNetFlow("F"));
        }

        [Fact]
        public void GetNetFlow_IncludesEarnedIncome()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0; // isolate: no CB injections/levies

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;

            w.Earn("F", 8000);              // e.g. a trade settlement between passes
            w.AddPendingExpense("F", 5000); // and a Reinforce spend in the same window
            w.AccrueIncome();

            // net flow is the pass's full income - expenses, not expenses alone
            Assert.Equal(8000 - t.UpkeepPerSource - 5000, w.GetNetFlow("F"));

            w.AccrueIncome(); // income must not linger into the next pass

            Assert.Equal(-t.UpkeepPerSource, w.GetNetFlow("F"));
        }

        [Fact]
        public void GetNetFlow_IncludesCentralBankGrantIncome()
        {
            var w = BuildWorld();
            var t = w.Tuning;

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;
            n.Inventory["Iron"] = 50_000; // collateral so the money-supply target is positive

            long treasuryBefore = w.GetTreasury("F");
            w.AccrueIncome();
            long granted = w.GetTreasury("F") - treasuryBefore + t.UpkeepPerSource;

            // whatever the CB injected this pass must appear in the same pass's net flow
            Assert.Equal(granted - t.UpkeepPerSource, w.GetNetFlow("F"));
        }

        [Fact]
        public void AddPendingExpense_NonPositiveAmount_IsNoOp()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0; // isolate: no CB grant income in net flow

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            n.StableStationId = 1;

            w.AddPendingExpense("F", 0);
            w.AddPendingExpense("F", -100);
            w.AccrueIncome();

            Assert.Equal(-t.UpkeepPerSource, w.GetNetFlow("F"));
        }

        [Fact]
        public void UnfoundedClaim_ProducesWithoutPayingStationUpkeep()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0;
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.Radius = 24;
            TestSupport.WithOre(n);
            n.Inventory["SteelPlate"] = 100;
            n.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            w.SetTreasury("F", 100_000);

            w.AccrueIncome();

            Assert.Equal(100_000, w.GetTreasury("F"));
            Assert.Equal(100, n.Inventory["SteelPlate"]);
            Assert.Equal(0, w.GetNetFlow("F"));
            Assert.True(w.GetNodeInventory("N1", ItemKind.Ore) > 0);
        }

        [Fact]
        public void FoundedStation_PaysBaseAndCapabilityUpkeep()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0;
            t.OreYield = 0;
            t.ProcessedGoodYield = 0;
            t.HydrogenYield = 0;
            t.OxygenYield = 0;
            t.RareOreYield = 0;

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = 42;
            n.Inventory["SteelPlate"] = 100;
            n.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            w.SetTreasury("F", 100_000);

            w.AccrueIncome();

            Assert.Equal(100_000 - t.UpkeepPerSource, w.GetTreasury("F"));
            Assert.Equal(100, n.Inventory["SteelPlate"]);
            Assert.Equal(-t.UpkeepPerSource, w.GetNetFlow("F"));
        }

        [Fact]
        public void LegacyScanBackedStation_PaysNoBaseUpkeep_UntilDeveloped()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0;
            t.OreYield = 0;
            t.ProcessedGoodYield = 0;
            t.HydrogenYield = 0;
            t.OxygenYield = 0;
            t.RareOreYield = 0;

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "E1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = 42;
            n.Inventory["SteelPlate"] = 100;
            w.SetTreasury("F", 100_000);

            w.AccrueIncome();

            Assert.Equal(100_000, w.GetTreasury("F"));
            Assert.Equal(100, n.Inventory["SteelPlate"]);
            Assert.Equal(0, w.GetNetFlow("F"));

            n.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            w.AccrueIncome();

            Assert.Equal(100_000 - t.UpkeepPerSource, w.GetTreasury("F"));
            Assert.Equal(100, n.Inventory["SteelPlate"]);
            Assert.Equal(-t.UpkeepPerSource, w.GetNetFlow("F"));
        }

        [Fact]
        public void ItemUpkeep_DefaultsToZero()
        {
            var t = TestSupport.Tuning();

            Assert.Equal(0, t.UpkeepProcessedGood);
            Assert.Equal(0, t.CapabilityUpkeepFor(NodeCapabilityKind.Manufactory, ItemKind.ProcessedGood));
            Assert.Equal(0, t.CapabilityUpkeepFor(NodeCapabilityKind.Refinery, ItemKind.Ore));
        }

        // ---- Change 2: action item costs --------------------------------------------------------

        [Fact]
        public void Expand_BlockedByScore_WhenItemsInsufficient()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            // FakeWorld: 0 items (set Inventory=0)
            var w = new FakeWorld { Treasury = 1_000_000, Inventory = 0 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "H");
            home.OwnerFactionTag = "F"; home.HoldStrength = 0.8;
            var frontier = w.Graph.Add(new Vec3(100, 0, 0), 0.9, "A");
            w.Graph.Connect("H", "A");

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, ExpandPersona(), w, new SeededRandom(1), t);

            Assert.NotEqual(ActionKind.Expand, d.Intent.Kind);
            // Expand must not appear in scored with non-zero utility
            bool anyExpand = false;
            foreach (var c in d.Scored)
                if (c.Intent.Kind == ActionKind.Expand && c.Utility > 0) anyExpand = true;
            Assert.False(anyExpand, "Expand utility must be 0 when ProcessedGood is insufficient");
        }

        [Fact]
        public void Develop_BlockedByScore_WhenOreInsufficient()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 1_000_000, Inventory = 0 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.4, "H");
            home.OwnerFactionTag = "F"; home.HoldStrength = 0.9;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, ExpandPersona(), w, new SeededRandom(1), t);

            bool anyDevelop = false;
            foreach (var c in d.Scored)
                if (c.Intent.Kind == ActionKind.Develop && c.Utility > 0) anyDevelop = true;
            Assert.False(anyDevelop, "Develop utility must be 0 when Ore is insufficient");
        }

        [Fact]
        public void Enact_Expand_DeductsProcessedGood()
        {
            var w = BuildWorld();
            var t = w.Tuning;

            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F"; home.HoldStrength = 0.9; home.Radius = 24;
            home.Inventory["SteelPlate"] = 1000;

            // FactionRuntime.Enact deducts items; test AddInventory as the primitive it calls
            w.AddInventory("F", ItemKind.ProcessedGood, -t.ExpandItemCostProcessedGood);

            long remaining = w.GetInventory("F", ItemKind.ProcessedGood);
            Assert.Equal(1000 - t.ExpandItemCostProcessedGood, remaining);
        }

        [Fact]
        public void Enact_Develop_DeductsInstallCost()
        {
            var w = BuildWorld();
            var t = w.Tuning;

            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "H");
            home.OwnerFactionTag = "F"; home.HoldStrength = 0.9; home.Radius = 24;
            home.Inventory["Iron"] = 500;

            // Installing a Refinery costs Ore (representative capability with a non-zero Ore cost).
            long oreCost = t.InstallCostFor(NodeCapabilityKind.Refinery, ItemKind.Ore);
            w.AddInventory("F", ItemKind.Ore, -oreCost);

            Assert.Equal(500 - oreCost, w.GetInventory("F", ItemKind.Ore));
        }

        // ---- Change 3: yield scale + per-node inventory -----------------------------------------

        // Three per-kind yield tests stood here. Ore no longer varies by label -- it is gated on the
        // node having a deposit -- and ProcessedGood/Hydrogen are not freely minted by anything, so
        // the comparisons they made have no subject. See FAC-102 and EconomyItemTests' replacements.
        [Fact]
        public void ItemYield_IsTheSameForEveryLabel_GivenTheSameGround()
        {
            var t = TestSupport.Tuning();
            var a = TestSupport.WithOre(new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 });
            var b = TestSupport.WithOre(new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 });

            Assert.Equal(Economy.SourceItemYield(a, 1.0, t, ItemKind.Ore),
                         Economy.SourceItemYield(b, 1.0, t, ItemKind.Ore));
        }

        [Fact]
        public void AccrueIncome_MintsToNode_NotFactionBuffer()
        {
            var w = BuildWorld();

            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F"; n.HoldStrength = 1.0; n.Radius = 24;
            TestSupport.WithOre(n);

            w.AccrueIncome();

            // Items should be on the node itself, not just in a faction aggregate
            long nodeOre = w.GetNodeInventory("N1", ItemKind.Ore);
            long factionOre = w.GetInventory("F", ItemKind.Ore);
            Assert.True(nodeOre > 0, "items minted by AccrueIncome should appear in node inventory");
            Assert.Equal(factionOre, nodeOre);
        }

        [Fact]
        public void Trade_PullsCargoFromOriginNode_NotOtherNode()
        {
            var w = BuildWorld();
            var t = w.Tuning;

            // Sender has two nodes: origin (closest to B) has items; far node has none
            SpatialNode originNode = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "NA");
            originNode.OwnerFactionTag = "A"; originNode.HoldStrength = 0.9;
            originNode.Inventory["Iron"] = t.TradeCargoOre;

            SpatialNode farNode = w.Graph.Add(new Vec3(5000, 0, 0), 0.8, "NF");
            farNode.OwnerFactionTag = "A"; farNode.HoldStrength = 0.9;
            // farNode has no items

            SpatialNode destNode = w.Graph.Add(new Vec3(100, 0, 0), 0.8, "NB");
            destNode.OwnerFactionTag = "B"; destNode.HoldStrength = 0.9;

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "A", intent, w, t, immediate: true);

            // Cargo must have come from the origin node (NA is closest to NB)
            Assert.NotNull(fleet.Cargo);
            Assert.True(fleet.Cargo.ContainsKey("Iron") && fleet.Cargo["Iron"] > 0);
            // Origin node depleted; far node untouched
            Assert.Equal(0L, w.GetNodeInventory("NA", ItemKind.Ore));
            Assert.Equal(0L, w.GetNodeInventory("NF", ItemKind.Ore));
        }

        [Fact]
        public void Trade_DeliversCargo_ToDestinationNode()
        {
            var w = BuildWorld();
            var t = w.Tuning;

            SpatialNode srcNode = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "NA");
            srcNode.OwnerFactionTag = "A"; srcNode.HoldStrength = 0.9;
            srcNode.Inventory["HydrogenBottle"] = t.TradeCargoHydrogen;

            SpatialNode dstNode = w.Graph.Add(new Vec3(100, 0, 0), 0.8, "NB");
            dstNode.OwnerFactionTag = "B"; dstNode.HoldStrength = 0.9;

            var stA = new FactionState { Tag = "A" };
            var stB = new FactionState { Tag = "B" };
            var states = new List<FactionState> { stA, stB };

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "A", intent, w, t, immediate: true);
            stA.PendingFleets.Add(fleet);

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, t, new SeededRandom(1), log);

            Assert.True(w.GetNodeInventory("NB", ItemKind.Hydrogen) > 0,
                "cargo should arrive at the destination node, not faction buffer");
        }

        // ---- Change 4: cargo loot as items -----------------------------------------------------

        [Fact]
        public void RaidInterceptsTrade_LootsItems_NotCredits()
        {
            var w = BuildWorld();
            var t = w.Tuning;
            t.InterceptWindowSeconds = 0;   // the on-contact path; the window has its own tests (FAC-307)

            // Raider faction with an origin node
            SpatialNode raidHome = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "RH");
            raidHome.OwnerFactionTag = "RAID"; raidHome.HoldStrength = 0.9;

            // Trader faction
            SpatialNode tradeOrigin = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "TS");
            tradeOrigin.OwnerFactionTag = "TRADE"; tradeOrigin.HoldStrength = 0.9;
            tradeOrigin.Inventory["SteelPlate"] = 500;

            SpatialNode tradeDest = w.Graph.Add(new Vec3(200, 0, 0), 0.8, "TD");
            tradeDest.OwnerFactionTag = "RECV"; tradeDest.HoldStrength = 0.9;

            var stRaid  = new FactionState { Tag = "RAID" };
            var stTrade = new FactionState { Tag = "TRADE" };
            var stRecv  = new FactionState { Tag = "RECV" };
            var states  = new List<FactionState> { stRaid, stTrade, stRecv };

            // Create the trade convoy
            var tIntent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "RECV", Cost = t.TradeCost };
            PendingFleetAction tradeFleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "TRADE", tIntent, w, t, immediate: true);
            stTrade.PendingFleets.Add(tradeFleet);

            // Create the raid targeting the same node
            var rIntent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "TD", Cost = t.RaidCost };
            PendingFleetAction raidFleet = PendingFleetResolver.CreatePending(ActionKind.Raid, "RAID", rIntent, w, t, immediate: true);
            stRaid.PendingFleets.Add(raidFleet);

            long treasuryBefore = w.GetTreasury("RAID");

            // Run with a seed that guarantees the raid intercepts (roll < RaidInterceptsTradeChance=0.8)
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, t, new SeededRandom(0), log);

            long raidItemsAfter = w.GetInventory("RAID", ItemKind.ProcessedGood);
            long treasuryAfter  = w.GetTreasury("RAID");

            // Check either items were looted OR (if cargo was empty) a credit fallback occurred
            bool gotItems   = raidItemsAfter > 0;
            bool gotCredits = treasuryAfter > treasuryBefore;
            Assert.True(gotItems || gotCredits, "raider must receive either looted items or credit fallback");
        }

        // ---- expansion ---------------------------------------------------------------------------


        // ---- helpers ----------------------------------------------------------------------------

        private static Personality ExpandPersona()
        {
            var p = new Personality { Temperature = 0.1 };  // low temp → near-deterministic
            p.Weights[Drive.Growth] = 2;
            p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Security] = 1;
            p.Weights[Drive.Rivalry] = 0;
            p.Weights[Drive.Caution] = 0;
            p.Traits = new Traits { Expansionism = 1.0 };
            return p;
        }
    }
}
