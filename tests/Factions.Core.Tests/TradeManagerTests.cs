using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Trade as a material vector across the ownership boundary (docs/trade-haul-shaped.md).
    ///
    /// <para>Every test here starts from the same signal Haul starts from: a demand row its owner's
    /// own Operations pass could not source, published on <see cref="FactionState.UnservedDemands"/>.
    /// What is asserted is the vector — that the convoy is sized to the row rather than to the hold,
    /// that it forms only when the seller genuinely consents, that one row is served once, and that
    /// nothing writes a cooldown any more.</para>
    /// </summary>
    public class TradeManagerTests
    {
        // ---- fixtures ------------------------------------------------------------------------

        /// <summary>Seller "A" holds 5,000 Iron at A1 and is glutted in it (scarcity 0.1, under the
        /// consent ceiling). Buyer "B" wants 100 of it at B1 and is short (scarcity 2.0, over the
        /// sale floor). A owns one Trade hull; B owns none, so the sell direction is the only one
        /// that can serve the row unless a test says otherwise.</summary>
        private static void BuildSellerAndBuyer(out WorldModel world, out FactionTuning tuning,
            out FactionState seller, out FactionState buyer, out TradeTest.NodeScarcityOracle oracle,
            long sellerStock = 5000, long want = 100, double urgency = 1.0, string itemKey = "Iron")
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", 1_000_000);
            world.SetTreasury("B", 1_000_000);

            SpatialNode a1 = TradeTest.Node(world, "A1", "A");
            a1.Inventory[itemKey] = sellerStock;
            a1.ShipStock["Trade"] = 1;

            TradeTest.Node(world, "B1", "B", x: 100);

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 0.1)     // the seller is sitting on a glut
                .Set("B1", ItemKind.Ore, 2.0);    // the buyer needs it

            seller = TradeTest.State("A");
            buyer = TradeTest.State("B");
            // Income high enough that the windowed dispatch budget never binds; the test that is
            // about the budget sets it to zero itself.
            seller.CentralBankRecentIncome = 1_000_000;
            buyer.CentralBankRecentIncome = 1_000_000;
            TradeTest.Whitelist(seller, "B");
            TradeTest.Whitelist(buyer, "A");
            TradeTest.Unserved(buyer, "B1", itemKey, want, urgency);
        }

        /// <summary>Inventory keys carry an "Ore:"/"Comp:" prefix the item registry does not key on.
        /// The buyer's row for ore is a real inventory key, and it trades; asking the registry with
        /// the raw key refused every ore and component row before consent was ever evaluated.</summary>
        [Theory]
        [InlineData("Ore:Ice")]
        [InlineData("Comp:SteelPlate")]
        public void APrefixedInventoryKey_IsTraded(string itemKey)
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle, itemKey: itemKey);
            oracle.Set("A1", itemKey, 0.1).Set("B1", itemKey, 2.0);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            PendingFleetAction trade = Assert.Single(Trades(a));
            Assert.Equal(100L, trade.Cargo[itemKey]);
        }

        private static List<string> Tick(TradeManager manager, WorldModel world, FactionTuning tuning,
            params FactionState[] states)
        {
            var log = new List<string>();
            manager.Tick(states.ToList(), world, tuning, log);
            return log;
        }

        private static IEnumerable<PendingFleetAction> Trades(FactionState st)
        {
            return st.PendingFleets.Where(f => f.Kind == ActionKind.Trade);
        }

        // ---- gas in tanks ------------------------------------------------------------------------

        private const string H2 = "Gas:Hydrogen";

        /// <summary>A hydrogen row served in each direction, with the flying side owning a tankless
        /// hull with the better-fitting hold and a tanker. The tanker flies in both, and carries the
        /// row; the tankless hull stays parked.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AGasRow_IsFlownByATankedHull(bool procurement)
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle, want: 500_000,
                sellerStock: 50_000_000, itemKey: H2);
            oracle.Set("A1", H2, 0.1).Set("B1", H2, 2.0);
            var prefabs = (TradeTest.Prefabs)world.PrefabCatalogue;
            prefabs.CargoLitersByPrefab["Trade"] = 600_000;
            prefabs.CargoLitersByPrefab["Tanker"] = 1_000_000;
            prefabs.GasTanksByPrefab["Tanker"] = new Dictionary<string, long> { { H2, 2_000_000 } };

            SpatialNode home = world.Graph.Nodes[procurement ? "B1" : "A1"];
            world.Graph.Nodes["A1"].ShipStock.Clear();
            home.ShipStock["Trade"] = 1;
            home.ShipStock["Tanker"] = 1;

            List<string> log = Tick(TradeTest.Manager(oracle, new TradeTest.Volumes().Set(H2, 1.0)), world, tuning, a, b);

            FactionState flyer = procurement ? b : a;
            PendingFleetAction trade = Assert.Single(Trades(flyer));
            Assert.Equal("Tanker", trade.ShipPrefabId);
            Assert.Equal(procurement, trade.IsProcurement);
            Assert.Equal(1, home.ShipStock["Trade"]);
            Assert.Contains(log, l => l.Contains(procurement ? "PROCURE_DISPATCHED " : "TRADE_DISPATCHED ")
                                      && l.Contains(" prefab=Tanker "));
        }

        // ---- the sell direction ----------------------------------------------------------------

        /// <summary>The finding FAC-284 was filed on: 99.7% of trade cargo rode home unsold because
        /// the packer filled the hold against a node count rather than a want. The convoy now carries
        /// the row's quantity, and the discriminating number is the stock LEFT at the source — the
        /// old packer took the lot.</summary>
        [Fact]
        public void SellVector_IsSizedToTheRow_NotToTheHold()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);

            List<string> log = Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            PendingFleetAction fleet = Assert.Single(Trades(a));
            Assert.Equal(ActionKind.Trade, fleet.Kind);
            Assert.Equal("B", fleet.TargetTag);
            Assert.False(fleet.IsProcurement);
            Assert.Equal(100L, fleet.Cargo["Iron"]);
            Assert.Equal(4900L, world.Graph.Nodes["A1"].Inventory["Iron"]);
            Assert.Contains(log, l => l.StartsWith("TRADE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" A -> B ") && l.Contains(" item=Iron ")
                                      && l.Contains(" qty=100 ") && l.Contains(" from=A1 ") && l.Contains(" to=B1 "));
        }

        /// <summary>Seller-flies is not one-way: the itinerary carries a terminal Return, so the trip
        /// home is a real interceptable leg rather than a terminal at the counterparty's node.</summary>
        [Fact]
        public void SellConvoy_CarriesATerminalReturnToTheSourceNode()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            PendingFleetAction fleet = Assert.Single(Trades(a));
            Assert.Equal("B1", fleet.TargetNodeId);
            FleetStop terminal = Assert.Single(fleet.RemainingStops);
            Assert.Equal(FleetStopKind.Return, terminal.Kind);
            Assert.Equal("A1", terminal.NodeId);
        }

        /// <summary>End to end: the convoy settles at the Service stop for what the buyer can afford,
        /// carries only the refused remainder home, and the hull lands back at the seller's node.</summary>
        [Fact]
        public void SellConvoy_SettlesAtService_ReturnsOnlyTheRefusedRemainder_AndComesHome()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            // 40 units of headroom at 10 credits each; the other 60 ride home.
            world.SetTreasury("B", tuning.InsolvencyFloor + 400);

            TradeManager manager = TradeTest.Manager(oracle);
            var log = new List<string>();
            var states = new List<FactionState> { a, b };
            manager.Tick(states, world, tuning, log);
            Assert.Single(Trades(a));
            Assert.Equal(0, world.Graph.Nodes["A1"].ShipStock["Trade"]);   // consumed at dispatch

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // Service
            Assert.Equal(40L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Contains(log, l => l.StartsWith("TRADE_RESOLVED ", StringComparison.Ordinal)
                                      && l.Contains("settled=400") && l.Contains("returned=600"));
            // The refused 60 are still aboard, flying a real leg home -- not teleported back the
            // instant the sale was refused. That is what the terminal Return stop buys.
            Assert.Equal(4900L, world.Graph.Nodes["A1"].Inventory["Iron"]);
            Assert.False(world.Graph.Nodes["A1"].ShipStock.ContainsKey("Trade")
                         && world.Graph.Nodes["A1"].ShipStock["Trade"] > 0);

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // Return
            Assert.Equal(4960L, world.Graph.Nodes["A1"].Inventory["Iron"]);          // 4900 + the 60 refused
            Assert.Equal(1, world.Graph.Nodes["A1"].ShipStock["Trade"]);             // hull credited home, not to B
        }

        /// <summary>Consent is decided at the source node, before anything flies. A seller that needs
        /// the stock itself forms no vector at all — the discriminating measurement is that the
        /// identical fixture with a consenting seller does dispatch (the test above).</summary>
        [Fact]
        public void SellerScarceInTheItemItself_DoesNotConsent_AndNoConvoyForms()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            oracle.Set("A1", ItemKind.Ore, 1.5);   // above ProcurementSellerScarcityCeiling

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Empty(Trades(a));
            Assert.Empty(Trades(b));
            Assert.Equal(5000L, world.Graph.Nodes["A1"].Inventory["Iron"]);
        }

        /// <summary>FAC-109: a seller node none of whose capabilities uses the item has no reference
        /// stock for it, so the oracle has no opinion. That read as "needs it" and kept stock like
        /// 513 M ice on a refinery node unsold forever. It now sells.</summary>
        [Fact]
        public void SellerNodeWithNoUseForTheItem_Consents()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            var noOpinionAtSeller = new TradeTest.NodeScarcityOracle().Set("B1", ItemKind.Ore, 2.0);

            Tick(TradeTest.Manager(noOpinionAtSeller), world, tuning, a, b);

            Assert.Single(Trades(a));
        }

        /// <summary>The discriminator for the test above: the same fixture with the switch off refuses,
        /// as it did before.</summary>
        [Fact]
        public void SellerNodeWithNoUseForTheItem_SwitchOff_Refuses()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            tuning.TradeSellUnusedStock = false;
            var noOpinionAtSeller = new TradeTest.NodeScarcityOracle().Set("B1", ItemKind.Ore, 2.0);

            Tick(TradeTest.Manager(noOpinionAtSeller), world, tuning, a, b);

            Assert.Empty(Trades(a));
        }

        /// <summary>The consent trace names the gate: with the unused-stock switch off, the refusal
        /// line counts the seller's stocked node under switch_off, and it is written once, not every
        /// pass.</summary>
        [Fact]
        public void ConsentRefusal_IsTracedWithTheGate_OnChangeOnly()
        {
            DebugTraces.ResetToDefaults();
            try
            {
                DebugTraces.Trade = true;
                WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
                BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
                tuning.TradeSellUnusedStock = false;
                TradeManager manager = TradeTest.Manager(new TradeTest.NodeScarcityOracle().Set("B1", ItemKind.Ore, 2.0));

                List<string> log = Tick(manager, world, tuning, a, b);
                string line = Assert.Single(log.FindAll(l => l.StartsWith("TRADE_CONSENT_REFUSED seller=A buyer=B item=Iron", StringComparison.Ordinal)));
                Assert.Contains("stocked_nodes=1", line);
                Assert.Contains("switch_off=1", line);

                List<string> again = Tick(manager, world, tuning, a, b);
                Assert.DoesNotContain(again, l => l.StartsWith("TRADE_CONSENT_REFUSED", StringComparison.Ordinal));
            }
            finally { DebugTraces.ResetToDefaults(); }
        }

        /// <summary>Stock a node asked for itself is not surplus, whatever its capabilities: an open
        /// Item: row for the key at the seller's node keeps it home.</summary>
        [Fact]
        public void SellerNodeWithNoUse_ButItsOwnWantForTheItem_Refuses()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "A", Kind = "Item:Iron", NodeId = "A1", Quantity = 100,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            var noOpinionAtSeller = new TradeTest.NodeScarcityOracle().Set("B1", ItemKind.Ore, 2.0);

            Tick(TradeTest.Manager(noOpinionAtSeller), world, tuning, a, b);

            Assert.Empty(Trades(a));
        }

        /// <summary>A partly served row has its owner's own haul in flight toward it -- that is what
        /// served the part. That haul must not read as a claim on the remainder; a full row's must.</summary>
        [Fact]
        public void APartialRow_IsNotBlockedByItsOwnersHaul_AFullRowIs()
        {
            foreach (bool partial in new[] { true, false })
            {
                WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
                BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
                UnservedDemand row = b.UnservedDemands[0];
                row.Partial = partial;
                b.UnservedDemands[0] = row;
                b.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Haul, ActorTag = "B", OriginNodeId = "B2", TargetNodeId = "B1",
                    Cargo = new Dictionary<string, long> { { "Iron", 5 } },
                });

                Tick(TradeTest.Manager(oracle), world, tuning, a, b);

                if (partial) Assert.Single(Trades(a));
                else Assert.Empty(Trades(a));
            }
        }

        /// <summary>Consent is decided on the item. The seller node is glutted in the item the buyer
        /// wants and short of another subtype of the same kind; the trade must still form. One
        /// number for the kind cannot separate the two, which is what this pins.</summary>
        [Fact]
        public void SellerShortOfASiblingSubtype_StillConsentsToTheGluttedOne()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            oracle.Set("A1", "Iron", 0.1);     // the wanted item: glutted, consent
            oracle.Set("A1", "Cobalt", 1.5);   // a sibling subtype of the same kind: short

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.NotEmpty(Trades(a));
        }

        /// <summary>Stock the seller's own build queue has already claimed is not surplus, so it is
        /// not offered — the same <c>SurplusOf</c> net that Haul's matcher applies.</summary>
        [Fact]
        public void StockClaimedByTheSellersOwnBuildQueue_IsNotOffered()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            var claim = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "Trade",
                ExpiresUtc = world.UtcNow.AddDays(30),
            };
            claim.Bom["Iron"] = 5000;
            world.Graph.Nodes["A1"].BuildQueue.Add(claim);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Empty(Trades(a));
        }

        /// <summary>A faction with hulls and stock serves a neighbour that owns no ship at all. This
        /// is the whole point of keeping the sell direction: eight of the fourteen live factions own
        /// no cargo hull, and nothing teleports for them.</summary>
        [Fact]
        public void HullLessBuyer_IsServedByASeller_WithNoBootstrapTeleport()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);

            List<string> log = Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Single(Trades(a));
            Assert.Empty(Trades(b));
            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.False(world.Graph.Nodes["B1"].Inventory.ContainsKey("Iron"));   // nothing arrived instantly
        }

        // ---- the buy direction ------------------------------------------------------------------

        /// <summary>A buyer that owns a hull flies for itself, and the seller standing beside it with
        /// consenting stock does not get to sell first. The roster deliberately puts the seller ahead
        /// of the buyer: with one interleaved pass the seller would win on list order alone.</summary>
        [Fact]
        public void BuyerThatOwnsAHull_BuysBeforeAnyoneSellsToIt()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["B1"].ShipStock["Trade"] = 1;   // the buyer can fly

            List<string> log = Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Empty(Trades(a));
            PendingFleetAction fleet = Assert.Single(Trades(b));
            Assert.True(fleet.IsProcurement);
            Assert.Equal("A", fleet.TargetTag);
            Assert.Equal("A1", fleet.TargetNodeId);
            Assert.Equal(FleetStopKind.Pickup, fleet.CurrentStopKind);
            Assert.Equal("Iron", fleet.PickupItemKey);
            Assert.Equal(100L, fleet.PickupQuantity);
            FleetStop terminal = Assert.Single(fleet.RemainingStops);
            Assert.Equal(FleetStopKind.Return, terminal.Kind);
            Assert.Equal("B1", terminal.NodeId);
            Assert.Contains(log, l => l.StartsWith("PROCURE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" B <- A ") && l.Contains(" item=Iron ")
                                      && l.Contains(" qty=100 ") && l.Contains(" buy_at=A1 ")
                                      && l.Contains(" deliver=B1 "));
        }

        /// <summary>Money moves for exactly the units that board, and only for the item the vector
        /// named — the seller's other stock is not swept into the hold on the way past. That sweep is
        /// what made 99.7% of trade cargo ride home unsold.</summary>
        [Fact]
        public void ProcurementPaysAtPickup_ForExactlyThePickupQuantity_AndDeliversOnReturn()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["B1"].ShipStock["Trade"] = 1;
            world.Graph.Nodes["A1"].Inventory["Nickel"] = 4000;    // sellable, wanted by nobody

            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);
            Assert.True(Assert.Single(Trades(b)).IsProcurement);

            long buyerBefore = world.GetTreasury("B");
            long sellerBefore = world.GetTreasury("A");

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // Pickup: buy
            Assert.Equal(4900L, world.Graph.Nodes["A1"].Inventory["Iron"]);
            Assert.Equal(4000L, world.Graph.Nodes["A1"].Inventory["Nickel"]);       // untouched
            Assert.Equal(1000L, world.GetTreasury("A") - sellerBefore);             // 100 x 10
            Assert.Equal(1000L, buyerBefore - world.GetTreasury("B"));
            Assert.Contains(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal) && l.Contains("paid=1000"));

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // Return: deliver
            Assert.Equal(100L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.False(world.Graph.Nodes["B1"].Inventory.ContainsKey("Nickel"));
            Assert.Equal(1, world.Graph.Nodes["B1"].ShipStock["Trade"]);
        }

        /// <summary>A seller-flies convoy whose hull was at the source loads at dispatch, and leaves
        /// carrying the book value of that hold rather than 0.</summary>
        [Fact]
        public void SellConvoy_LoadedAtDispatch_CarriesTheBookValueOfItsHold()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.PriceCatalogue = new TradeTest.FlatPrices(25);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            PendingFleetAction fleet = Assert.Single(Trades(a));
            Assert.Equal(100L, fleet.Cargo["Iron"]);
            Assert.Equal(2500L, fleet.CargoValue);
        }

        /// <summary>A procurement's value is what it paid, not the book value: the book price here is
        /// set apart from the settlement price so the two cannot coincide.</summary>
        [Fact]
        public void ProcurementConvoy_CarriesWhatItPaid_NotTheBookValue()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["B1"].ShipStock["Trade"] = 1;
            world.PriceCatalogue = new TradeTest.FlatPrices(25);

            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);
            Assert.Equal(0L, Assert.Single(Trades(b)).CargoValue);   // empty hold until the Pickup

            long buyerBefore = world.GetTreasury("B");
            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);
            long paid = buyerBefore - world.GetTreasury("B");

            Assert.True(paid > 0);
            Assert.NotEqual(2500L, paid);
            Assert.Equal(paid, Assert.Single(Trades(b)).CargoValue);
        }

        // ---- one row, one convoy ------------------------------------------------------------------

        /// <summary>Two sellers, one row. The first dispatch claims (delivery node, item) in the
        /// roster-wide in-flight index for the rest of the sweep, so the second never forms.</summary>
        [Fact]
        public void TwoSellers_CannotBothServeOneRow_InOneSweep()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);

            world.SetTreasury("C", 1_000_000);
            SpatialNode c1 = TradeTest.Node(world, "C1", "C", x: 200);
            c1.Inventory["Iron"] = 5000;
            c1.ShipStock["Trade"] = 1;
            oracle.Set("C1", ItemKind.Ore, 0.1);
            FactionState c = TradeTest.State("C");
            c.CentralBankRecentIncome = 1_000_000;
            TradeTest.Whitelist(c, "B");

            Tick(TradeTest.Manager(oracle), world, tuning, a, c, b);

            Assert.Equal(1, Trades(a).Count() + Trades(c).Count());
        }

        /// <summary>A row another faction is already flying a convoy to is not served again. The
        /// claim index is built over the whole roster, which is what makes a neighbour's convoy
        /// visible at all.</summary>
        [Fact]
        public void RowAlreadyClaimedByAnotherFactionsConvoy_IsSkipped()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);

            world.SetTreasury("C", 1_000_000);
            FactionState c = TradeTest.State("C");
            c.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "C", TargetTag = "B",
                TargetNodeId = "B1", OriginNodeId = "C1",
                Cargo = new Dictionary<string, long> { { "Iron", 100 } },
                DepartedUtc = world.UtcNow.AddMinutes(-1),
                EstimatedArrivalUtc = world.UtcNow.AddHours(1),
            });

            Tick(TradeTest.Manager(oracle), world, tuning, a, c, b);

            Assert.Empty(Trades(a));
        }

        /// <summary>A player Acquisition contract claims its (node, item) pair the same way an
        /// in-flight convoy does, so an NPC seller does not duplicate a delivery a player took.</summary>
        [Fact]
        public void RowClaimedByAPlayerContract_IsSkipped()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            b.ExternalHaulClaims.Add(("B1", "Iron"));

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Empty(Trades(a));
        }

        /// <summary>The single-dispatch-per-sweep return is gone: one faction serves as many rows in
        /// one sweep as it has hulls, budget and consenting stock for.</summary>
        [Fact]
        public void OneFaction_DispatchesSeveralConvoysInOneSweep()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["A1"].ShipStock["Trade"] = 3;
            world.Graph.Nodes["A1"].Inventory["Nickel"] = 5000;
            world.Graph.Nodes["A1"].Inventory["Cobalt"] = 5000;
            TradeTest.Unserved(b, "B1", "Nickel", 100);
            TradeTest.Unserved(b, "B1", "Cobalt", 100);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Equal(3, Trades(a).Count());
        }

        // ---- throttles ------------------------------------------------------------------------------

        /// <summary>Every Trade cooldown is dismantled. The claim, the hull, the convoy assessment
        /// and the fleet cap are what throttle now — nothing writes a clock.</summary>
        [Fact]
        public void Dispatch_WritesNoTradeCooldown_AndLeavesTradeDestCooldownsAlone()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Single(Trades(a));
            Assert.False(a.Cooldowns.ContainsKey(ActionKind.Trade));
            Assert.Empty(a.TradeDestCooldowns);
        }

        /// <summary>A stale cooldown left on a faction by an earlier build no longer gates anything:
        /// the eligibility filter does not read <c>Cooldowns</c> or <c>TradeDestCooldowns</c>.</summary>
        [Fact]
        public void AStandingTradeCooldown_NoLongerBlocksDispatch()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            a.Cooldowns[ActionKind.Trade] = world.UtcNow.AddHours(1);
            a.TradeDestCooldowns["B"] = world.UtcNow.AddHours(1);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Single(Trades(a));
        }

        /// <summary>MaxTradeFleets is kept as the hard safety cap.</summary>
        [Fact]
        public void MaxTradeFleets_StillCapsInFlightConvoys()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            tuning.MaxTradeFleets = 1;
            world.Graph.Nodes["A1"].ShipStock["Trade"] = 3;
            world.Graph.Nodes["A1"].Inventory["Nickel"] = 5000;
            TradeTest.Unserved(b, "B1", "Nickel", 100);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Single(Trades(a));
        }

        /// <summary>The convoy assessment is the shared one: a dribble against a huge want is refused
        /// and the hull handed straight back, on the same <c>HaulMinLoadFraction</c>/
        /// <c>HaulMinClosureFraction</c> pair Haul is judged by.</summary>
        [Fact]
        public void AssessConvoy_RefusesADribbleAgainstALargeWant_AndReturnsTheHull()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle, sellerStock: 5, want: 10_000);
            var volumes = new TradeTest.Volumes().Set("Iron", 1.0);

            List<string> log = Tick(TradeTest.Manager(oracle, volumes), world, tuning, a, b);

            Assert.Empty(Trades(a));
            Assert.Equal(1, world.Graph.Nodes["A1"].ShipStock["Trade"]);   // handed back, not spent
            Assert.Contains(log, l => l.Contains("TRADE_LOAD_GATED"));
        }

        /// <summary>A light convoy whose demand is urgent flies however small it is — the urgency half
        /// of the same gate, and what keeps the load fraction from starving a want that cannot
        /// wait. The seller holds seven: the fixture oracle's reference of 1 puts its consent floor at
        /// 2, so five is its whole consenting margin and the delivery is complete.</summary>
        [Fact]
        public void AssessConvoy_FliesASmallLoadWhoseDemandIsUrgent()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle, sellerStock: 7, want: 5,
                                urgency: 3.0);
            var volumes = new TradeTest.Volumes().Set("Iron", 1.0);

            Tick(TradeTest.Manager(oracle, volumes), world, tuning, a, b);

            Assert.Equal(5L, Assert.Single(Trades(a)).Cargo["Iron"]);
        }

        /// <summary>The same small complete delivery at an ordinary urgency waits: the hull goes back
        /// rather than flying five units in it.</summary>
        [Fact]
        public void AssessConvoy_HoldsASmallCompleteDeliveryThatIsNotUrgent()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle, sellerStock: 7, want: 5,
                                urgency: 1.0);
            var volumes = new TradeTest.Volumes().Set("Iron", 1.0);

            List<string> log = Tick(TradeTest.Manager(oracle, volumes), world, tuning, a, b);

            Assert.Empty(Trades(a));
            Assert.Equal(1, world.Graph.Nodes["A1"].ShipStock["Trade"]);
            Assert.Contains(log, l => l.Contains("TRADE_LOAD_GATED") && l.Contains("urgency=1"));
        }

        /// <summary>No hull anywhere in the seller's estate: one <c>Ship:Trade</c> demand is staged,
        /// carrying the real route the built hull has to fly, and nothing is dispatched or
        /// teleported.</summary>
        [Fact]
        public void NoHullAnywhere_StagesOneShipTradeDemand_WithARealRoute()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["A1"].ShipStock.Clear();

            List<string> log = Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Empty(Trades(a));
            DemandEntry only = Assert.Single(world.Demand.SnapshotShipDemands("A"));
            Assert.Equal("Ship:Trade", only.Kind);
            Assert.Equal("A1", only.NodeId);
            Assert.Equal("B1", only.DestNodeId);
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND") && l.Contains("role=Trade"));
            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
        }

        /// <summary>A faction with no whitelisted partner never reaches vector formation, and a
        /// dormant one never runs at all.</summary>
        [Fact]
        public void EmptyWhitelistOrDormantFaction_DispatchesNothing()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            a.TradePriority.Clear();

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);
            Assert.Empty(Trades(a));

            TradeTest.Whitelist(a, "B");
            a.DormantUntilUtc = world.UtcNow.AddHours(1);
            Tick(TradeTest.Manager(oracle), world, tuning, a, b);
            Assert.Empty(Trades(a));
        }

        /// <summary>The one dispatch-time charge, levied per convoy rather than per sweep.</summary>
        [Fact]
        public void EachConvoy_CostsExactlyOneTradeCost()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            world.Graph.Nodes["A1"].ShipStock["Trade"] = 2;
            world.Graph.Nodes["A1"].Inventory["Nickel"] = 5000;
            TradeTest.Unserved(b, "B1", "Nickel", 100);
            long before = world.GetTreasury("A");

            Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            Assert.Equal(2, Trades(a).Count());
            Assert.Equal(2 * tuning.TradeCost, before - world.GetTreasury("A"));
        }

        /// <summary>The windowed dispatch budget still gates, and now per convoy: a faction with no
        /// recent income gets only the floor, which is one convoy's cost.</summary>
        [Fact]
        public void DispatchBudget_BindsPerConvoy_ForAFactionWithNoRecentIncome()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuildSellerAndBuyer(out world, out tuning, out a, out b, out oracle);
            a.CentralBankRecentIncome = 0;
            world.Graph.Nodes["A1"].ShipStock["Trade"] = 2;
            world.Graph.Nodes["A1"].Inventory["Nickel"] = 5000;
            TradeTest.Unserved(b, "B1", "Nickel", 100);

            TradeManager manager = TradeTest.Manager(oracle);
            Tick(manager, world, tuning, a, b);

            Assert.Single(Trades(a));
            Assert.Equal(1, manager.LastTelemetry.BudgetBlocked);
        }
    }
}
