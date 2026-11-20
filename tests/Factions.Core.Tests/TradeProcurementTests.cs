using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The buy direction of Trade (FAC-148, reshaped by FAC-284): a faction short of a material
    /// flies its own hull out to buy it from a partner, instead of only ever waiting for a partner to
    /// decide to sell. Two legs — Pickup at the partner's node, where the goods are consented to and
    /// paid for, then Return home, which unloads the hold and credits the hull back.
    ///
    /// <para>This file is about what happens <em>at the dock</em>: the convoy is already flying, and
    /// what is asserted is that it buys exactly the vector it was formed for, that the seller's
    /// consent and the buyer's budget are re-checked there, and that a refusal leaves the stock where
    /// it is. Which convoy forms in the first place is asserted in <see cref="TradeManagerTests"/>.
    /// A procurement fleet and a sell fleet both carry <c>Kind == Trade</c> and both set
    /// <c>TargetTag</c> to the partner, so those two fields cannot tell them apart —
    /// <c>IsProcurement</c> is the discriminator.</para>
    /// </summary>
    public class TradeProcurementTests
    {
        // ---- helpers -----------------------------------------------------------------------

        /// <summary>A buyer ("A") short of ore with one Trade hull parked at its own node, a seller
        /// ("B") glutted in it, and a brain that has already whitelisted B. Scarcity is set
        /// explicitly: 2.0 at the buyer (needs it) and 0.1 at the seller (well under
        /// <see cref="FactionTuning.ProcurementSellerScarcityCeiling"/>, so B will part with it).</summary>
        private static void BuildBuyerAndSeller(out WorldModel world, out FactionTuning tuning,
            out FactionState buyer, out FactionState seller, out TradeTest.NodeScarcityOracle oracle,
            long sellerStock = 5000, long buyerTreasury = 1_000_000, long want = 100)
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", buyerTreasury);
            world.SetTreasury("B", tuning.InsolvencyFloor);

            SpatialNode home = TradeTest.Node(world, "A1", "A");
            home.ShipStock["Trade"] = 2;

            SpatialNode sellerNode = TradeTest.Node(world, "B1", "B", x: 100);
            sellerNode.Inventory["Iron"] = sellerStock;

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 2.0)    // the buyer needs ore
                .Set("B1", ItemKind.Ore, 0.1);   // the seller is sitting on a glut

            buyer = TradeTest.State("A");
            seller = TradeTest.State("B");
            buyer.CentralBankRecentIncome = 1_000_000;
            seller.CentralBankRecentIncome = 1_000_000;
            TradeTest.Whitelist(buyer, "B");
            TradeTest.Unserved(buyer, "A1", "Iron", want);
        }

        private static PendingFleetAction DispatchOne(TradeManager manager, IList<FactionState> states,
            WorldModel world, FactionTuning tuning, List<string> log)
        {
            manager.Tick(states, world, tuning, log);
            PendingFleetAction fleet = Assert.Single(states[0].PendingFleets);
            Assert.True(fleet.IsProcurement, "the buyer should be buying, not selling");
            return fleet;
        }

        // ---- dispatch ------------------------------------------------------------------------

        [Fact]
        public void BuyerShortOfWhatAPartnerIsGluttedIn_DispatchesAProcurementConvoy()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);

            var log = new List<string>();
            PendingFleetAction fleet = DispatchOne(TradeTest.Manager(oracle),
                new List<FactionState> { a, b }, world, tuning, log);

            Assert.Equal(ActionKind.Trade, fleet.Kind);
            Assert.Equal("B", fleet.TargetTag);
            Assert.Equal("B1", fleet.TargetNodeId);                       // first leg flies to the seller
            Assert.Equal(FleetStopKind.Pickup, fleet.CurrentStopKind);
            Assert.Equal("Iron", fleet.PickupItemKey);
            Assert.Equal(100L, fleet.PickupQuantity);

            FleetStop last = Assert.Single(fleet.RemainingStops);
            Assert.Equal(FleetStopKind.Return, last.Kind);                // then straight home
            Assert.Equal("A1", last.NodeId);
        }

        /// <summary>Consent is asked before the vector forms, not only at the dock: a seller who
        /// needs the stock themselves is never flown to.</summary>
        [Fact]
        public void SellerScarceInTheItemToo_IsNeverFlownTo()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);
            oracle.Set("B1", ItemKind.Ore, 1.5);   // above the consent ceiling

            var log = new List<string>();
            TradeTest.Manager(oracle).Tick(new List<FactionState> { a, b }, world, tuning, log);

            Assert.Empty(a.PendingFleets);
        }

        /// <summary>A buyer that owns no hull anywhere does not fly: it is served by the sell
        /// direction, and nothing teleports for it.</summary>
        [Fact]
        public void BuyerThatOwnsNoHull_DoesNotFlyOutToBuy()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["A1"].ShipStock.Clear();

            var log = new List<string>();
            TradeTest.Manager(oracle).Tick(new List<FactionState> { a, b }, world, tuning, log);

            Assert.Empty(a.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
        }

        /// <summary>Buyer-flies never takes a hull at the loading node, because that node belongs to
        /// the seller. Tier 1 does not exist in this direction; the convoy always flies out empty
        /// from the buyer's own estate.</summary>
        [Fact]
        public void ProcurementNeverConsumesTheSellersOwnHull()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["B1"].ShipStock["Trade"] = 2;   // the seller has hulls of its own

            var log = new List<string>();
            PendingFleetAction fleet = DispatchOne(TradeTest.Manager(oracle),
                new List<FactionState> { a, b }, world, tuning, log);

            Assert.Equal("A1", fleet.OriginNodeId);                            // flew from home
            Assert.Equal(2, world.Graph.Nodes["B1"].ShipStock["Trade"]);       // the seller's are untouched
            Assert.Equal(1, world.Graph.Nodes["A1"].ShipStock["Trade"]);       // one of the buyer's two spent
        }

        // ---- settlement ----------------------------------------------------------------------

        [Fact]
        public void ResolvesAcrossBothLegs_GoodsComeHomeAndTheSellerIsPaid()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);
            long buyerStart = world.GetTreasury("A");
            long sellerStart = world.GetTreasury("B");

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // buy
            Assert.Equal(4900L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Equal(1000L, world.GetTreasury("B") - sellerStart);
            Assert.Equal(1000L, buyerStart - world.GetTreasury("A"));
            Assert.Contains(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal));

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // home
            Assert.Equal(100L, world.Graph.Nodes["A1"].Inventory["Iron"]);
            Assert.Equal(2, world.Graph.Nodes["A1"].ShipStock["Trade"]);   // one consumed, credited back
        }

        /// <summary>Affordability is decided at the dock, not at dispatch: the convoy is already
        /// flying when the treasury is drained, and it comes home empty rather than pushing the buyer
        /// below the insolvency floor.</summary>
        [Fact]
        public void BuyerCannotAfford_LeavesTheStockWithTheSeller()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);

            world.SetTreasury("A", tuning.InsolvencyFloor);
            long sellerStart = world.GetTreasury("B");

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(5000L, world.Graph.Nodes["B1"].Inventory["Iron"]);  // nothing taken
            Assert.Equal(sellerStart, world.GetTreasury("B"));               // nothing paid
            Assert.Equal(tuning.InsolvencyFloor, world.GetTreasury("A"));    // never pushed below the floor
        }

        /// <summary>Partial affordability buys the part it can pay for, and the money moves for
        /// exactly that — the units that board, not the units asked for.</summary>
        [Fact]
        public void BuyerCanOnlyAffordPartOfTheRow_BuysThatMuchAndPaysForExactlyIt()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);

            world.SetTreasury("A", tuning.InsolvencyFloor + 250);   // 25 units at 10 credits
            long sellerStart = world.GetTreasury("B");

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(4975L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Equal(250L, world.GetTreasury("B") - sellerStart);
            Assert.Equal(tuning.InsolvencyFloor, world.GetTreasury("A"));
            Assert.Contains(log, l => l.Contains("PROCURE_BOUGHT") && l.Contains("paid=250"));
        }

        /// <summary>Stock the seller's own build queue has claimed is not for sale, re-checked at the
        /// dock exactly as it was when the vector formed.</summary>
        [Fact]
        public void DoesNotBuyStockTheSellersOwnBuildQueueHasClaimed()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);

            // Claimed AFTER dispatch: the dock is the last word, not the match.
            SpatialNode sellerNode = world.Graph.Nodes["B1"];
            var claim = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "Trade",
                ExpiresUtc = world.UtcNow.AddDays(30),
            };
            claim.Bom["Iron"] = 5000;
            sellerNode.BuildQueue.Add(claim);

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(5000L, world.Graph.Nodes["B1"].Inventory["Iron"]);
        }

        /// <summary>FAC-109: the dock applies the same consent rule as the matcher, so a seller node
        /// with no use for the item -- no reference stock, no opinion -- that the matcher agreed to buy
        /// from also sells at the dock, rather than the convoy flying out to be refused.</summary>
        [Fact]
        public void ASellerNodeWithNoUseForTheItem_SellsAtTheDock()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);
            var noOpinionAtSeller = new TradeTest.NodeScarcityOracle().Set("A1", ItemKind.Ore, 2.0);

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(noOpinionAtSeller);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);
            TradeTest.ResolveOneLeg(states, world, tuning, log, noOpinionAtSeller, manager);

            Assert.Equal(4900L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.DoesNotContain(log, l => l.Contains("PROCURE_BOUGHT") && l.Contains("paid=0"));
        }

        /// <summary>The consent test is re-run at the dock: a seller whose scarcity rose while the
        /// convoy was in the air refuses, and the hold stays empty.</summary>
        [Fact]
        public void SellerScarcityRisesInFlight_TheDockRefuses()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);

            oracle.Set("B1", ItemKind.Ore, 1.5);

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(5000L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Contains(log, l => l.Contains("PROCURE_BOUGHT") && l.Contains("paid=0"));
        }

        /// <summary>The dock's consent test reads the item. The seller is short of the item the
        /// convoy came for and glutted in a sibling subtype of the same kind; the hold must stay
        /// empty. One number for the kind cannot tell the two apart.</summary>
        [Fact]
        public void SellerShortOfTheItemButGluttedInASiblingSubtype_TheDockRefuses()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle);

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);

            oracle.Set("B1", "Cobalt", 0.1);   // a sibling subtype of the same kind: glutted
            oracle.Set("B1", "Iron", 1.5);     // the item the convoy came for: short

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(5000L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Contains(log, l => l.Contains("PROCURE_BOUGHT") && l.Contains("paid=0"));
        }

        /// <summary>The hold is filled to the row, not to its own capacity: a seller sitting on far
        /// more than the vector asked for keeps the difference.</summary>
        [Fact]
        public void BuysOnlyTheRowsQuantity_NotEverythingTheSellerWouldPartWith()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildBuyerAndSeller(out world, out tuning, out a, out b, out oracle, sellerStock: 100_000, want: 250);
            world.Graph.Nodes["B1"].Inventory["Nickel"] = 50_000;   // sellable, and not what was asked for

            var log = new List<string>();
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            DispatchOne(manager, states, world, tuning, log);

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(99_750L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Equal(50_000L, world.Graph.Nodes["B1"].Inventory["Nickel"]);
            Assert.Equal(250L, Assert.Single(a.PendingFleets).Cargo["Iron"]);
            Assert.Single(Assert.Single(a.PendingFleets).Cargo);
        }
    }
}
