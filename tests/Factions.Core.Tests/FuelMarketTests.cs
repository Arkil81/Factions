using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Fuel across the ownership boundary (<see cref="FuelMarket"/>): a fuel-poor
    /// faction buys hydrogen or ice from a partner. Measured live 2026-09-29 (FAC-109): MEMN and TFCS
    /// each burned ~120 ML/h of hydrogen, held ~6.6 ML and were 78–83% short, and the trade that
    /// should have fed them was refused (every burning seller node read scarce).
    ///
    /// <para>Seller "A" owns A1: founded, burning 10,000 L/h of hydrogen, so it keeps a 5,000 L
    /// departure buffer (0.5 h lead, over the 1,000 L floor). Its hydrogen reference is 60,000, which
    /// its own fuel need raises; at 100,000 L of stock the scarcity test (<c>60,000 / 100,000 = 0.6</c>,
    /// over the 0.5 ceiling) refuses. Buyer "B" owns B1 with a hydrogen tanker.</para>
    /// </summary>
    public class FuelMarketTests
    {
        private const string H2 = "Gas:Hydrogen";
        private const string Ice = "Ore:Ice";
        private const long Reference = 60_000;
        private const long Keep = 5_000;

        private static FactionTuning Tuning()
        {
            FactionTuning tuning = TestSupport.Tuning();
            tuning.ProcurementSellerScarcityCeiling = 0.50;
            tuning.FuelDrawMode = FuelDrawModes.Draw;
            tuning.FuelBurnEmaHours = 1_000_000;   // the burn holds across the day a leg takes here
            tuning.FuelReserveHours = 6.0;
            tuning.FuelReserveFloor = 1_000;
            tuning.FuelBufferLeadHours = 0.5;
            tuning.FuelBufferMaxFloors = 100.0;
            tuning.HaulMinLoadFraction = 0.1;
            return tuning;
        }

        private sealed class Fixture
        {
            public WorldModel World;
            public FactionTuning Tuning;
            public TradeTest.NodeScarcityOracle Oracle;
            public FactionState Seller, Buyer;
            public SpatialNode SellerNode, BuyerNode;
            public TradeTest.Volumes Volumes;
            public List<FactionState> States { get { return new List<FactionState> { Buyer, Seller }; } }
        }

        /// <summary>A hydrogen seller and a buyer with a row for <paramref name="want"/> of it.</summary>
        private static Fixture Build(long sellerStock = 100_000, long want = 50_000)
        {
            var f = new Fixture { Tuning = Tuning(), World = TradeTest.World() };
            f.World.Tuning = f.Tuning;
            f.World.SetTreasury("A", 1_000_000_000);
            f.World.SetTreasury("B", 1_000_000_000);
            f.World.SetFuelReserveKeys("A", new List<string> { H2 });
            f.World.SetFuelReserveKeys("B", new List<string> { H2 });

            var prefabs = (TradeTest.Prefabs)f.World.PrefabCatalogue;
            prefabs.CargoLitersByPrefab["Tanker"] = 1_000_000;
            prefabs.GasTanksByPrefab["Tanker"] = new Dictionary<string, long> { { H2, 100_000 } };

            f.SellerNode = TradeTest.Node(f.World, "A1", "A");
            f.SellerNode.StableStationId = 1;
            f.SellerNode.Inventory[H2] = sellerStock;
            FuelDraw.CreditBurn(f.SellerNode, f.World.UtcNow, f.Tuning, H2, 10_000 * f.Tuning.FuelBurnEmaHours);

            f.BuyerNode = TradeTest.Node(f.World, "B1", "B", x: 100);
            f.BuyerNode.StableStationId = 2;
            f.BuyerNode.ShipStock["Tanker"] = 1;

            f.Oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", H2, (double)Reference / sellerStock).SetReference("A1", H2, Reference)
                .Set("B1", H2, 3.0);
            f.Volumes = new TradeTest.Volumes().Set(H2, 1.0).Set(Ice, 0.37);
            f.World.ItemVolumes = f.Volumes;

            f.Seller = TradeTest.State("A");
            f.Buyer = TradeTest.State("B");
            f.Seller.CentralBankRecentIncome = 1_000_000_000;
            f.Buyer.CentralBankRecentIncome = 1_000_000_000;
            TradeTest.Whitelist(f.Buyer, "A");
            TradeTest.Unserved(f.Buyer, "B1", H2, want);
            return f;
        }

        private static TradeManager Manager(Fixture f)
        {
            return TradeTest.Manager(f.Oracle, f.Volumes);
        }

        private static List<string> Tick(TradeManager manager, Fixture f)
        {
            var log = new List<string>();
            manager.Tick(f.States, f.World, f.Tuning, log);
            return log;
        }

        private static IEnumerable<PendingFleetAction> Procurements(FactionState st)
        {
            return st.PendingFleets.Where(p => p.Kind == ActionKind.Trade && p.IsProcurement);
        }

        // ---- fix 1: a seller sells above its keep --------------------------------------------

        /// <summary>The characteristic failure: a burning seller holding twenty times its keep reads
        /// scarce and forms no vector. It sells.</summary>
        [Fact]
        public void ABurningSellerAboveItsKeep_SellsHydrogen()
        {
            Fixture f = Build();

            List<string> log = Tick(Manager(f), f);

            PendingFleetAction buy = Assert.Single(Procurements(f.Buyer));
            Assert.Equal("A1", buy.TargetNodeId);
            Assert.Equal(50_000L, buy.PickupQuantity);
            Assert.Contains(log, l => l.StartsWith("PROCURE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" item=" + H2 + " ") && l.EndsWith(" margin=95000"));
        }

        /// <summary>The vector is capped at the stock above the keep, not at the scarcity floor.</summary>
        [Fact]
        public void TheMargin_IsTheStockAboveTheKeep()
        {
            Fixture f = Build(want: 500_000);
            TradeConsent.Reason reason;
            long surplus = FuelMarket.SellableAboveKeep(f.SellerNode, H2, f.World, f.Tuning);
            Assert.Equal(100_000L - Keep,
                TradeConsent.ConsentingMargin(f.Oracle, f.World, "A", f.SellerNode, H2, surplus, f.Tuning, null, out reason));
            Assert.Equal(TradeConsent.Reason.Consents, reason);

            Tick(Manager(f), f);

            Assert.Equal(100_000L - Keep, Assert.Single(Procurements(f.Buyer)).PickupQuantity);
        }

        /// <summary>A node at its keep has nothing to sell and reads scarce, whatever its reference.</summary>
        [Fact]
        public void ANodeAtItsKeep_RefusesAsScarce()
        {
            Fixture f = Build(sellerStock: Keep);
            f.Oracle.Set("A1", H2, 0.1);   // the scarcity test alone would consent

            Assert.Equal(TradeConsent.Reason.Scarce,
                TradeConsent.Evaluate(f.Oracle, f.World, "A", f.SellerNode, H2, f.Tuning));
            Tick(Manager(f), f);
            Assert.Empty(Procurements(f.Buyer));
        }

        /// <summary>Only fuel keys: iron at the same node keeps the scarcity test.</summary>
        [Fact]
        public void ANonFuelKey_KeepsTheScarcityTest()
        {
            Fixture f = Build();
            f.SellerNode.Inventory["Iron"] = 100_000;
            f.Oracle.Set("A1", "Iron", 0.6).SetReference("A1", "Iron", Reference);

            Assert.Equal(TradeConsent.Reason.Scarce,
                TradeConsent.Evaluate(f.Oracle, f.World, "A", f.SellerNode, "Iron", f.Tuning));
        }

        /// <summary>A key outside the seller's reserve keys is not a fuel key.</summary>
        [Fact]
        public void HydrogenTheSellerDoesNotKeep_IsNotAFuelKey()
        {
            Fixture f = Build();
            f.World.SetFuelReserveKeys("A", null);

            Assert.False(FuelMarket.IsFuelKey(f.World, "A", H2, f.Tuning));
            Assert.True(FuelMarket.IsFuelKey(f.World, "A", Ice, f.Tuning));
            Assert.Equal(TradeConsent.Reason.Scarce,
                TradeConsent.Evaluate(f.Oracle, f.World, "A", f.SellerNode, H2, f.Tuning));
        }

        /// <summary>The dock sells what the node holds above its keep, and leaves the keep.</summary>
        [Fact]
        public void TheDock_SellsDownToTheKeep_AndNoFurther()
        {
            Fixture f = Build(want: 500_000);
            TradeManager manager = Manager(f);
            var log = Tick(manager, f);
            Assert.Single(Procurements(f.Buyer));

            TradeTest.ResolveOneLeg(f.States, f.World, f.Tuning, log, f.Oracle, manager);

            Assert.Equal(Keep, f.SellerNode.Inventory[H2]);
            Assert.Equal(100_000L - Keep, Assert.Single(Procurements(f.Buyer)).Cargo[H2]);
        }

        /// <summary>Stock that falls while the convoy is in flight is sold only down to the keep.</summary>
        [Fact]
        public void StockThatFellInFlight_IsSoldDownToTheKeep()
        {
            Fixture f = Build(want: 500_000);
            f.Oracle.Set("A1", H2, 0.1).SetReference("A1", H2, 1);   // both sides consent at dispatch
            TradeManager manager = Manager(f);
            var log = Tick(manager, f);
            Assert.Single(Procurements(f.Buyer));

            f.SellerNode.Inventory[H2] = 50_000;
            TradeTest.ResolveOneLeg(f.States, f.World, f.Tuning, log, f.Oracle, manager);

            Assert.Equal(Keep, f.SellerNode.Inventory.ContainsKey(H2) ? f.SellerNode.Inventory[H2] : 0);
            Assert.Equal(45_000L, Assert.Single(Procurements(f.Buyer)).Cargo[H2]);
        }

        // ---- the ice keep --------------------------------------------------------------------

        /// <summary>Half an hour of the depot's measured ice draw: 3,700 L per 10 s pass at 0.37 L per
        /// unit is 10,000 units a pass, 3.6 M an hour, 1.8 M kept.</summary>
        [Fact]
        public void TheIceKeep_IsTheLeadOfTheDepotsMeasuredDraw()
        {
            Fixture f = Build();
            f.Tuning.OpsIntervalSeconds = 10;
            f.SellerNode.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            f.SellerNode.DemandHistory[Ice] = 3_700;

            Assert.Equal(3_600_000.0, ExtractionPlanner.DrawPerHour(f.SellerNode, Ice, f.Volumes, f.Tuning), 3);
            Assert.Equal(1_800_000L, FuelMarket.IceKeep(f.SellerNode, Ice, f.World, f.Tuning));
            Assert.Equal(0L, FuelMarket.IceKeep(f.SellerNode, H2, f.World, f.Tuning));

            f.SellerNode.Capabilities.Remove(NodeCapabilityKind.FuelDepot);
            Assert.Equal(0L, FuelMarket.IceKeep(f.SellerNode, Ice, f.World, f.Tuning));   // no depot, no keep
        }

        /// <summary>A depot seller of ice sells above the keep and reads scarce at or under it.</summary>
        [Fact]
        public void ADepotSellsIceAboveItsKeep()
        {
            Fixture f = Build();
            f.Tuning.OpsIntervalSeconds = 10;
            f.SellerNode.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            f.SellerNode.DemandHistory[Ice] = 3_700;              // keep 1.8 M
            f.SellerNode.Inventory[Ice] = 2_000_000;
            f.Oracle.Set("A1", Ice, 5.0).SetReference("A1", Ice, 10_000_000);

            TradeConsent.Reason reason;
            long surplus = 2_000_000;   // SurplusOf: ice is no reserve key, so the whole stock
            Assert.Equal(200_000L,
                TradeConsent.ConsentingMargin(f.Oracle, f.World, "A", f.SellerNode, Ice, surplus, f.Tuning, null, out reason));
            Assert.Equal(TradeConsent.Reason.Consents, reason);

            f.SellerNode.Inventory[Ice] = 1_800_000;
            Assert.Equal(TradeConsent.Reason.Scarce,
                TradeConsent.Evaluate(f.Oracle, f.World, "A", f.SellerNode, Ice, f.Tuning));
        }

        // ---- fix 2: a procurement holds its quantity until pickup ---------------------------

        private static long Held(Fixture f)
        {
            return f.World.ProcurementHeld("A1", H2);
        }

        private static long Surplus(Fixture f)
        {
            return FuelMarket.SellableAboveKeep(f.SellerNode, H2, f.World, f.Tuning);
        }

        /// <summary>The seller's own haul to A2, which wants 100,000 L: it takes whatever A1's
        /// routable surplus offers, loading at dispatch from a tanker parked at A1.</summary>
        private static void SellerHaulsAway(Fixture f)
        {
            SpatialNode a2 = TradeTest.Node(f.World, "A2", "A", x: -100);
            a2.StableStationId = 3;
            f.SellerNode.ShipStock["Tanker"] = 1;
            f.World.Demand.Post(new DemandEntry
            {
                FactionTag = "A", Kind = "Item:" + H2, NodeId = "A2", Quantity = 100_000,
                ExpiresUtc = f.World.UtcNow.AddMinutes(5),
            });
            new OperationsManager(f.Volumes).Tick(f.States, f.World, f.Tuning, new List<string>());
        }

        private static long Bought(Fixture f)
        {
            PendingFleetAction buy = Assert.Single(Procurements(f.Buyer));
            long v;
            return buy.Cargo != null && buy.Cargo.TryGetValue(H2, out v) ? v : 0;
        }

        /// <summary>The characteristic failure: the procurement's margin was checked at dispatch, the
        /// seller's own haul took the stock while the buyer flew out, and the buyer came back short.
        /// Held, the haul takes only what is left over, and the buyer collects its whole quantity.</summary>
        [Fact]
        public void AHeldProcurement_IsNotHauledAwayBeforePickup()
        {
            Fixture f = Build();
            TradeManager manager = Manager(f);
            var log = Tick(manager, f);
            Assert.Equal(50_000L, Held(f));

            SellerHaulsAway(f);
            Assert.Equal(55_000L, f.SellerNode.Inventory[H2]);      // 100 000 - (95 000 - 50 000 held)

            TradeTest.ResolveOneLeg(f.States, f.World, f.Tuning, log, f.Oracle, manager);
            Assert.Equal(50_000L, Bought(f));
            Assert.Contains(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal) && l.Contains(" items=1 "));
        }

        /// <summary>The hold comes off every reader of the surplus, and the ledger does not count it a
        /// second time: a second buyer is offered the stock above the keep less the first's hold.</summary>
        [Fact]
        public void ASecondBuyer_IsOfferedTheRemainder_NotADoubleCount()
        {
            Fixture f = Build();
            SpatialNode c1 = TradeTest.Node(f.World, "C1", "C", x: 0, y: 100);
            c1.StableStationId = 4;
            c1.ShipStock["Tanker"] = 1;
            f.Oracle.Set("C1", H2, 3.0);
            FactionState c = TradeTest.State("C");
            f.World.SetTreasury("C", 1_000_000_000);
            c.CentralBankRecentIncome = 1_000_000_000;
            TradeTest.Whitelist(c, "A");
            TradeTest.Unserved(c, "C1", H2, 90_000);

            Manager(f).Tick(new List<FactionState> { f.Buyer, c, f.Seller }, f.World, f.Tuning, new List<string>());

            Assert.Equal(50_000L, Assert.Single(Procurements(f.Buyer)).PickupQuantity);
            Assert.Equal(45_000L, Assert.Single(Procurements(c)).PickupQuantity);
            Assert.Equal(95_000L, f.World.ProcurementHeld("A1", H2));
            Assert.Equal(0L, Surplus(f));
        }

        // ---- every exit releases the hold ---------------------------------------------------

        private static Fixture Dispatched(out TradeManager manager, out List<string> log)
        {
            Fixture f = Build();
            manager = Manager(f);
            log = Tick(manager, f);
            Assert.Equal(50_000L, Held(f));
            Assert.Equal(45_000L, Surplus(f));
            return f;
        }

        private static void Mutate(Fixture f, Func<PendingFleetAction, PendingFleetAction> change)
        {
            int i = f.Buyer.PendingFleets.FindIndex(p => p.Kind == ActionKind.Trade && p.IsProcurement);
            f.Buyer.PendingFleets[i] = change(f.Buyer.PendingFleets[i]);
        }

        private static void AssertReleased(Fixture f)
        {
            Assert.Equal(0L, Held(f));
            Assert.Equal(100_000L - Keep, Surplus(f));
        }

        [Fact]
        public void Release_OnPickup()
        {
            TradeManager manager; List<string> log;
            Fixture f = Dispatched(out manager, out log);

            TradeTest.ResolveOneLeg(f.States, f.World, f.Tuning, log, f.Oracle, manager);

            Assert.Equal(50_000L, Bought(f));
            Assert.Equal(0L, Held(f));
            Assert.Equal(45_000L, Surplus(f));   // the 50 000 left with the buyer, not with the hold
        }

        [Fact]
        public void Release_OnCancel()
        {
            TradeManager manager; List<string> log;
            Fixture f = Dispatched(out manager, out log);

            Mutate(f, p => { p.Resolved = true; return p; });
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);

            AssertReleased(f);
        }

        /// <summary>Lost in flight: intercepted, then removed by the arrivals cleanup.</summary>
        [Fact]
        public void Release_OnLoss()
        {
            TradeManager manager; List<string> log;
            Fixture f = Dispatched(out manager, out log);

            Mutate(f, p => { p.Intercepted = true; return p; });
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);
            AssertReleased(f);

            PendingFleetResolver.ResolveArrivals(f.States, f.World, f.Tuning, new SeededRandom(1), log);
            Assert.Empty(Procurements(f.Buyer));
            AssertReleased(f);
        }

        /// <summary>A detour that requeues the Pickup keeps the hold; a reroute that drops it releases.</summary>
        [Fact]
        public void Release_OnReroute_ButNotOnADetourThatKeepsThePickup()
        {
            TradeManager manager; List<string> log;
            Fixture f = Dispatched(out manager, out log);

            Mutate(f, p =>
            {
                var pickup = new FleetStop { NodeId = p.TargetNodeId, Kind = FleetStopKind.Pickup,
                                             PickupItemKey = p.PickupItemKey, PickupQuantity = p.PickupQuantity };
                p.RemainingStops.Insert(0, pickup);
                p.CurrentStopKind = FleetStopKind.Navigation;
                p.TargetNodeId = null;
                return p;
            });
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);
            Assert.Equal(50_000L, Held(f));

            Mutate(f, p =>
            {
                p.RemainingStops.RemoveAll(st => st.Kind == FleetStopKind.Pickup);
                p.CurrentStopKind = FleetStopKind.Return;
                p.TargetNodeId = "B1";
                return p;
            });
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);
            AssertReleased(f);
        }

        /// <summary>A leg routed around a planet (<see cref="PendingFleetResolver.PlanFleetLeg"/>)
        /// flies clearance waypoints with no target node and no target tag, the Pickup requeued behind
        /// them. The hold is keyed on the Pickup stop's node, so the routing keeps it.</summary>
        [Fact]
        public void ARoutedOutboundLeg_KeepsItsHold()
        {
            TradeManager manager; List<string> log;
            Fixture f = Dispatched(out manager, out log);

            Mutate(f, p =>
            {
                var pickup = new FleetStop { NodeId = p.TargetNodeId, Kind = FleetStopKind.Pickup,
                                             PickupItemKey = p.PickupItemKey, PickupQuantity = p.PickupQuantity };
                p.RemainingStops.Insert(0, pickup);
                p.RemainingStops.Insert(0, new FleetStop { Kind = FleetStopKind.Navigation, HasPosition = true,
                                                           PositionKm = new Vec3(50, 50, 0) });
                // PlanFleetLeg's rewrite of the current leg onto the first clearance waypoint.
                p.TargetNodeId = null;
                p.TargetTag = null;
                p.TargetPosKm = new Vec3(50, 0, 0);
                p.CurrentStopKind = FleetStopKind.Navigation;
                p.PickupItemKey = null;
                p.PickupQuantity = 0;
                return p;
            });
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);

            Assert.Equal(50_000L, Held(f));
            Assert.Equal(45_000L, Surplus(f));
        }

        /// <summary>Expired: resolved and retired by the arrivals cleanup, which rebuilds the holds
        /// after it.</summary>
        [Fact]
        public void Release_OnExpiry()
        {
            TradeManager manager; List<string> log;
            Fixture f = Dispatched(out manager, out log);

            Mutate(f, p => { p.Resolved = true; return p; });
            PendingFleetResolver.ResolveArrivals(f.States, f.World, f.Tuning, new SeededRandom(1), log);

            Assert.Empty(Procurements(f.Buyer));
            AssertReleased(f);
        }

        /// <summary>Nothing about the hold is persisted: the fleet is, with its pickup, and the hold
        /// is rebuilt from the fleet a restart loads.</summary>
        [Fact]
        public void TheHold_IsRebuiltFromTheFleetsARestartLoads()
        {
            TradeManager manager; List<string> log;
            Fixture f = Dispatched(out manager, out log);

            FactionState restored = FactionStore.ApplyBrainDoc(
                FactionStore.ToBrainDoc(f.Buyer, f.World.Graph.OwnedBy("B"), FactionStore.DefaultLogCap),
                f.World.Graph);
            FuelMarket.RefreshHolds(new List<FactionState>(), f.World, f.Tuning);   // a boot: nothing held yet
            Assert.Equal(0L, Held(f));

            FuelMarket.RefreshHolds(new List<FactionState> { restored, f.Seller }, f.World, f.Tuning);
            Assert.Equal(50_000L, Held(f));
        }

        // ---- an in-flight haul's pickup claims its stock, below a hold ----------------------

        private static long Claimed(Fixture f)
        {
            return f.World.HaulPickupClaimed("A1", H2);
        }

        private static IEnumerable<PendingFleetAction> Hauls(FactionState st)
        {
            return st.PendingFleets.Where(p => p.Kind == ActionKind.Haul);
        }

        /// <summary>The seller's own haul to A2 for <paramref name="qty"/>, in a tanker fetched from A3:
        /// it flies out to A1 before it loads, so the stock it will take is still at A1.</summary>
        private static PendingFleetAction SellerHaulsFromAfar(Fixture f, long qty)
        {
            SpatialNode a2 = TradeTest.Node(f.World, "A2", "A", x: -100);
            a2.StableStationId = 3;
            SpatialNode a3 = TradeTest.Node(f.World, "A3", "A", x: -50);
            a3.StableStationId = 5;
            a3.ShipStock["Tanker"] = 1;
            f.World.Demand.Post(new DemandEntry
            {
                FactionTag = "A", Kind = "Item:" + H2, NodeId = "A2", Quantity = qty,
                ExpiresUtc = f.World.UtcNow.AddMinutes(5),
            });
            new OperationsManager(f.Volumes).Tick(new List<FactionState> { f.Seller }, f.World, f.Tuning,
                new List<string>());
            PendingFleetAction haul = Assert.Single(Hauls(f.Seller));
            Assert.Equal(FleetStopKind.Pickup, haul.CurrentStopKind);
            Assert.Equal("A1", haul.TargetNodeId);
            Assert.Equal(qty, haul.PickupQuantity);
            return haul;
        }

        /// <summary>The characteristic failure, live 2026-09-29: IGSL's haul AFFABC was dispatched to
        /// collect 8,676,867 H2 at N267, a second later IMPR's procurement 713BDC was offered
        /// <c>margin=8677142</c> at the same node, the haul loaded it all and 713BDC bought
        /// <c>items=0</c>. Claimed, the procurement is offered only what the haul leaves, and both
        /// collect in full.</summary>
        [Fact]
        public void AProcurementAfterTheSellersHaul_IsOfferedOnlyWhatTheHaulLeaves()
        {
            Fixture f = Build(want: 90_000);
            f.Oracle.Set("A1", H2, 0.1).SetReference("A1", H2, 1);
            PendingFleetAction haul = SellerHaulsFromAfar(f, 60_000);

            TradeManager manager = Manager(f);
            List<string> log = Tick(manager, f);

            Assert.Equal(60_000L, Claimed(f));
            PendingFleetAction buy = Assert.Single(Procurements(f.Buyer));
            Assert.Equal(35_000L, buy.PickupQuantity);
            Assert.Contains(log, l => l.StartsWith("PROCURE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.EndsWith(" margin=35000"));

            TradeTest.ResolveOneLeg(f.States, f.World, f.Tuning, log, f.Oracle, manager);

            Assert.Equal(35_000L, Bought(f));
            Assert.Equal(60_000L, Assert.Single(Hauls(f.Seller)).Cargo[H2]);
            Assert.Equal(Keep, f.SellerNode.Inventory[H2]);
            Assert.Equal(0L, Claimed(f));
            Assert.Contains(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal) && l.Contains(" items=1 "));
        }

        /// <summary>A haul of the seller's own, bound for A1 for 80,000, reaches the node before the
        /// buyer holding 50,000 there. It loads only what stands above the hold and the keep, and the
        /// buyer then collects its whole hold.</summary>
        [Fact]
        public void AHaulArrivingAfterAHold_LoadsAroundIt()
        {
            Fixture f = Build();
            f.Oracle.Set("A1", H2, 0.1).SetReference("A1", H2, 1);
            TradeManager manager = Manager(f);
            List<string> log = Tick(manager, f);
            Assert.Equal(50_000L, Held(f));

            TradeTest.Node(f.World, "A2", "A", x: -100).StableStationId = 3;
            f.Seller.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "A", FleetId = "HAUL01",
                OriginNodeId = "A1", TargetNodeId = "A1",
                OriginPosKm = new Vec3(-50, 0, 0), TargetPosKm = f.SellerNode.Position,
                DepartedUtc = f.World.UtcNow, EstimatedArrivalUtc = f.World.UtcNow.AddMinutes(1),
                ShipPrefabId = "Tanker", ConvoySize = 1, CargoCapacityLiters = 1_000_000,
                CurrentStopKind = FleetStopKind.Pickup, PickupItemKey = H2, PickupQuantity = 80_000,
                LastResolvedSegment = -1,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "A2", Kind = FleetStopKind.Service } },
            });
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);
            Assert.Equal(80_000L, Claimed(f));
            // The claim comes off the surplus, but not off the docking buyer's own hold.
            Assert.Equal(0L, Surplus(f));
            Assert.Equal(50_000L, FuelMarket.SellableAboveKeep(f.SellerNode, H2, f.World, f.Tuning, 50_000));

            // The seller's fleets resolve first, so the haul reaches A1 before the buyer.
            f.World.UtcNow = f.World.UtcNow.AddDays(1);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { f.Seller, f.Buyer }, f.World, f.Tuning,
                new SeededRandom(1), log, prices: new TradeTest.FlatPrices(10), oracle: f.Oracle,
                tradeInboundBudget: manager.InboundBudget);

            long hauled = Assert.Single(Hauls(f.Seller)).Cargo[H2];
            Assert.Equal(100_000L - Keep - 50_000L, hauled);
            Assert.Contains(log, l => l.StartsWith("HAUL_PICKUP ", StringComparison.Ordinal)
                                      && l.EndsWith(" held=" + (80_000L - hauled)));
            Assert.Equal(50_000L, Bought(f));
            Assert.Equal(Keep, f.SellerNode.Inventory[H2]);
            Assert.Equal(0L, Claimed(f));
        }

        // ---- an empty buy names its gate ----------------------------------------------------

        /// <summary>B's procurement standing at its Pickup at A1 for 50,000 of
        /// <paramref name="item"/>, in a Tanker.</summary>
        private static PendingFleetAction StandingProcurement(string item = H2, long qty = 50_000,
            string prefab = "Tanker")
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Trade, IsProcurement = true, ActorTag = "B", TargetTag = "A",
                FleetId = "BUY001", OriginNodeId = "B1", TargetNodeId = "A1",
                CurrentStopKind = FleetStopKind.Pickup, PickupItemKey = item, PickupQuantity = qty,
                ShipPrefabId = prefab, ConvoySize = 1, CargoCapacityLiters = 1_000_000,
            };
        }

        /// <summary>The empty-buy reason <see cref="PendingFleetResolver.BuyTradeCargo(SpatialNode, string, ref PendingFleetAction, string, Vec3, WorldModel, FactionTuning, IItemVolumeCatalogue, IItemMassCatalogue, IPriceOracle, IItemPriceCatalogue, FactionState, SpendBudget, FactionState, out string)"/>
        /// names for <paramref name="fleet"/> at A1; null when something boarded.</summary>
        private static string Reason(Fixture f, PendingFleetAction fleet, long unitPrice = 10,
            SpendBudget budget = null, IItemMassCatalogue masses = null, string sellerTag = "A")
        {
            string reason;
            PendingFleetResolver.BuyTradeCargo(f.SellerNode, sellerTag, ref fleet, "B1", f.BuyerNode.Position,
                f.World, f.Tuning, f.Volumes, masses, f.Oracle, new TradeTest.FlatPrices(unitPrice), f.Buyer, budget,
                f.Seller, out reason);
            return reason;
        }

        private static Fixture Consenting()
        {
            Fixture f = Build();
            f.Oracle.Set("A1", H2, 0.1).SetReference("A1", H2, 1);
            return f;
        }

        [Fact]
        public void EmptyBuyReason_Bought_IsNull()
        {
            Assert.Null(Reason(Consenting(), StandingProcurement()));
        }

        [Fact]
        public void EmptyBuyReason_Seller()
        {
            Fixture f = Consenting();
            Assert.Equal("seller", Reason(f, StandingProcurement(), sellerTag: ""));
            PendingFleetAction own = StandingProcurement();
            own.ActorTag = "A";
            Assert.Equal("seller", Reason(f, own));
        }

        [Fact]
        public void EmptyBuyReason_Refused()
        {
            Fixture f = Consenting();
            f.Seller.IsPlayerFaction = true;   // a player seller with its sell switch off
            Assert.Equal("refused", Reason(f, StandingProcurement()));
        }

        [Fact]
        public void EmptyBuyReason_Nothing()
        {
            Assert.Equal("nothing", Reason(Consenting(), StandingProcurement(qty: 0)));
        }

        [Fact]
        public void EmptyBuyReason_Unpriced()
        {
            Fixture f = Consenting();
            f.SellerNode.Inventory["Mystery"] = 100_000;
            Assert.Equal("unpriced", Reason(f, StandingProcurement(item: "Mystery"), unitPrice: 0));
        }

        /// <summary>A key outside the fuel rules refused by the scarcity test.</summary>
        [Fact]
        public void EmptyBuyReason_Consent()
        {
            Fixture f = Consenting();
            f.SellerNode.Inventory["Iron"] = 100_000;
            f.Oracle.Set("A1", "Iron", 0.6).SetReference("A1", "Iron", Reference);
            Assert.Equal("consent", Reason(f, StandingProcurement(item: "Iron")));
        }

        /// <summary>A fuel node down to its keep refuses at consent, and reads as the empty node it is.</summary>
        [Fact]
        public void EmptyBuyReason_Stock()
        {
            Fixture f = Consenting();
            f.SellerNode.Inventory[H2] = Keep;
            Assert.Equal("stock", Reason(f, StandingProcurement()));
        }

        /// <summary>Another buyer's hold takes the node's whole excess.</summary>
        [Fact]
        public void EmptyBuyReason_Held()
        {
            Fixture f = Consenting();
            f.Buyer.PendingFleets.Add(StandingProcurement());
            PendingFleetAction other = StandingProcurement(qty: 95_000);
            other.FleetId = "BUY002";
            f.Buyer.PendingFleets.Add(other);
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);
            Assert.Equal(145_000L, Held(f));

            Assert.Equal("held", Reason(f, f.Buyer.PendingFleets[0]));
        }

        /// <summary>A haul of the seller's own, already bound to collect the whole excess.</summary>
        [Fact]
        public void EmptyBuyReason_Claimed()
        {
            Fixture f = Consenting();
            f.Seller.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "A", FleetId = "HAUL01", TargetNodeId = "A1",
                CurrentStopKind = FleetStopKind.Pickup, PickupItemKey = H2, PickupQuantity = 95_000,
                ShipPrefabId = "Tanker", ConvoySize = 1,
            });
            FuelMarket.RefreshHolds(f.States, f.World, f.Tuning);
            Assert.Equal(95_000L, Claimed(f));

            Assert.Equal("claimed", Reason(f, StandingProcurement()));
        }

        [Fact]
        public void EmptyBuyReason_Floor()
        {
            Fixture f = Consenting();
            f.World.SetTreasury("B", f.Tuning.InsolvencyFloor);
            Assert.Equal("floor", Reason(f, StandingProcurement()));
        }

        [Fact]
        public void EmptyBuyReason_Budget()
        {
            Fixture f = Consenting();
            var budget = new SpendBudget();
            budget.Record("B", f.World.UtcNow, long.MaxValue / 4, f.Tuning.SpendBudgetWindowSeconds);
            Assert.Equal("budget", Reason(f, StandingProcurement(), budget: budget));
        }

        [Fact]
        public void EmptyBuyReason_Tank()
        {
            Fixture f = Consenting();
            Assert.Equal("tank", Reason(f, StandingProcurement(prefab: "NoTanks")));
        }

        [Fact]
        public void EmptyBuyReason_Mass()
        {
            Fixture f = Consenting();
            f.World.Planets = new TradeTest.ConstantGravity(1.0);
            ((TradeTest.Prefabs)f.World.PrefabCatalogue).SetHydrogenDown("Tanker", dryMassKg: 1_000, forceN: 15_000);
            var masses = new TradeTest.Masses().Set(H2, 1_000_000);
            Assert.Equal("mass", Reason(f, StandingProcurement(), masses: masses));
        }

        /// <summary>Ice, a solid, into a hold already full.</summary>
        [Fact]
        public void EmptyBuyReason_Hold()
        {
            Fixture f = Consenting();
            f.SellerNode.Inventory[Ice] = 100_000;
            f.Oracle.Set("A1", Ice, 0.1).SetReference("A1", Ice, 1);
            f.Volumes.Set("Stone", 1.0);
            PendingFleetAction fleet = StandingProcurement(item: Ice);
            fleet.CargoCapacityLiters = 10;
            fleet.Cargo = new Dictionary<string, long> { { "Stone", 10 } };
            Assert.Equal("hold", Reason(f, fleet));
        }

        /// <summary>A line on which nothing boarded carries the reason.</summary>
        [Fact]
        public void ProcureBought_WithNoItems_CarriesTheReason()
        {
            Fixture f = Build();
            f.Oracle.Set("A1", H2, 0.1).SetReference("A1", H2, 1);
            TradeManager manager = Manager(f);
            List<string> log = Tick(manager, f);
            PendingFleetAction buy = Assert.Single(Procurements(f.Buyer));

            f.SellerNode.Inventory[H2] = 0;   // drained while it flew
            TradeTest.ResolveOneLeg(f.States, f.World, f.Tuning, log, f.Oracle, manager);

            string empty = "PROCURE_BOUGHT " + buy.LogId + " B <- A @ A1 items=0 paid=0";
            Assert.Contains(empty + " reason=stock", log);
        }

        [Fact]
        public void ProcureBought_WithItems_CarriesNoReason()
        {
            Fixture f = Build();
            TradeManager manager = Manager(f);
            List<string> log = Tick(manager, f);
            TradeTest.ResolveOneLeg(f.States, f.World, f.Tuning, log, f.Oracle, manager);

            string bought = Assert.Single(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal));
            Assert.Contains(" items=1 ", bought);
            Assert.DoesNotContain(" reason=", bought);
        }

        // ---- fix 3: ice flies the hull that carries the most of it --------------------------

        private const long BrokkrLiters = 8_500;      // ~23k ice
        private const long B60Liters = 1_332_000;     // ~3.6 M ice

        /// <summary>Seller A1 holds 5 M ice it will sell (reference 1, scarcity 0.1: every gate
        /// consents). Buyer B wants <paramref name="want"/> at B1 and parks a Brokkr at B2, the donor
        /// nearest the seller, and a B60 at B3, farther out. Ice is 0.37 L per unit.</summary>
        private static Fixture BuildIce(long want)
        {
            Fixture f = Build();
            f.Buyer.UnservedDemands.Clear();
            f.BuyerNode.ShipStock.Clear();
            var prefabs = (TradeTest.Prefabs)f.World.PrefabCatalogue;
            prefabs.CargoLitersByPrefab["Brokkr"] = BrokkrLiters;
            prefabs.CargoLitersByPrefab["B60"] = B60Liters;

            f.SellerNode.Inventory[Ice] = 5_000_000;
            f.Oracle.Set("A1", Ice, 0.1).SetReference("A1", Ice, 1).Set("B1", Ice, 3.0);
            TradeTest.Node(f.World, "B2", "B", x: 50).ShipStock["Brokkr"] = 1;
            TradeTest.Node(f.World, "B3", "B", x: 200).ShipStock["B60"] = 1;
            TradeTest.Unserved(f.Buyer, "B1", Ice, want);
            return f;
        }

        /// <summary>The characteristic failure: an ice load larger than a Brokkr's hold flew in the
        /// Brokkr parked nearest the seller. It flies the B60.</summary>
        [Fact]
        public void ABulkIceProcurement_FliesTheLargestHold()
        {
            Fixture f = BuildIce(want: 1_000_000);

            List<string> log = Tick(Manager(f), f);

            PendingFleetAction buy = Assert.Single(Procurements(f.Buyer));
            Assert.Equal("B60", buy.ShipPrefabId);
            Assert.Equal("B3", buy.OriginNodeId);
            Assert.Equal(1_000_000L, buy.PickupQuantity);
            Assert.Equal(1, f.World.Graph.Nodes["B2"].ShipStock["Brokkr"]);
            Assert.Contains(log, l => l.StartsWith("PROCURE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" item=" + Ice + " ") && l.Contains(" prefab=B60 "));
        }

        /// <summary>A load a small hold takes whole flies the smallest hold that takes it.</summary>
        [Fact]
        public void ASmallIceLoad_FliesTheSmallestHoldThatTakesItWhole()
        {
            Fixture f = BuildIce(want: 20_000);   // 7,400 L

            Tick(Manager(f), f);

            Assert.Equal("Brokkr", Assert.Single(Procurements(f.Buyer)).ShipPrefabId);
        }

        /// <summary>The sell direction takes its hull at the seller's own node (tier 1) by the same
        /// rule: the B60 for a bulk load, the Brokkr for one it takes whole.</summary>
        [Theory]
        [InlineData(1_000_000L, "B60")]
        [InlineData(20_000L, "Brokkr")]
        public void TheSellDirection_PicksByTheSameRule(long want, string expected)
        {
            Fixture f = BuildIce(want);
            f.Buyer.UnservedDemands.Clear();
            f.Buyer.TradePriority.Clear();
            f.World.Graph.Nodes["B2"].ShipStock.Clear();
            f.World.Graph.Nodes["B3"].ShipStock.Clear();
            f.SellerNode.ShipStock["Brokkr"] = 1;
            f.SellerNode.ShipStock["B60"] = 1;
            TradeTest.Whitelist(f.Seller, "B");
            TradeTest.Unserved(f.Buyer, "B1", Ice, want);

            Tick(Manager(f), f);

            PendingFleetAction sale = Assert.Single(f.Seller.PendingFleets, p => p.Kind == ActionKind.Trade);
            Assert.False(sale.IsProcurement);
            Assert.Equal(expected, sale.ShipPrefabId);
        }

        /// <summary>Hydrogen is not a bulk key: its hull is still chosen by tank room.</summary>
        [Fact]
        public void HydrogenIsNotABulkJob()
        {
            Fixture f = Build();
            var prefabs = (TradeTest.Prefabs)f.World.PrefabCatalogue;
            prefabs.CargoLitersByPrefab["B60"] = B60Liters;
            f.BuyerNode.ShipStock["B60"] = 1;   // no tank: never a hydrogen hull

            Tick(Manager(f), f);

            Assert.Equal("Tanker", Assert.Single(Procurements(f.Buyer)).ShipPrefabId);
        }

        private const long MidLiters = 20_000;

        /// <summary>A 20,000-unit ice load (7,400 L) fits a Brokkr's 8,500 L hold, but in 1 g the
        /// Brokkr lifts only a fraction of its 20,000 kg: live 2026-09-29 a Brokkr-Multi flying ice was
        /// <c>cargo_mass_trimmed</c> to 7,499 units. A "Mid" hull (20,000 L, unscanned, so no lift
        /// ceiling) takes the load whole; the B60 would too, but the load gate refuses a 7,400 L load in
        /// its 1.3 ML hold. Ice is 1 kg and 0.37 L per unit; <paramref name="knownMass"/> false leaves
        /// ice's mass unknown, so no lift ceiling can be read.</summary>
        private static Fixture BuildLiftLimitedIce(bool knownMass, out TradeManager manager)
        {
            Fixture f = BuildIce(want: 20_000);
            f.Tuning.HaulSmallHullSearchNodes = 5;
            f.World.Planets = new TradeTest.ConstantGravity(1.0);
            var prefabs = (TradeTest.Prefabs)f.World.PrefabCatalogue;
            prefabs.CargoLitersByPrefab["Mid"] = MidLiters;
            prefabs.SetHydrogenDown("Brokkr", dryMassKg: 1_000, forceN: 40_000);
            TradeTest.Node(f.World, "B4", "B", x: 120).ShipStock["Mid"] = 1;

            // The Brokkr flies the leg dry, and lifts under the load's 7,400 L.
            var route = ShipPool.RouteEnvironment.From(f.World.Planets, f.SellerNode.Position, f.BuyerNode.Position,
                f.Tuning.MinDryTwr, f.Tuning.OrbitGravityMin);
            Assert.True(ShipPool.IsRouteFeasible(prefabs, "Brokkr", route));
            double liftLiters = PendingFleetResolver.LoadedLiftKg(f.World, f.Tuning, "Brokkr",
                f.SellerNode.Position, f.BuyerNode.Position) * 0.37;
            Assert.InRange(liftLiters, 1.0, 20_000 * 0.37 - 1);

            manager = TradeTest.Manager(f.Oracle, f.Volumes, knownMass ? new TradeTest.Masses().Set(Ice, 1.0) : null);
            return f;
        }

        /// <summary>Tier 2, the buy direction: the donor search ranks each donor's hull by what it
        /// lifts, so the Mid wins over the nearer Brokkr. With ice's mass unknown the Brokkr is the
        /// smallest hold that takes the load.</summary>
        [Theory]
        [InlineData(true, "Mid", "B4")]
        [InlineData(false, "Brokkr", "B2")]
        public void AMassLimitedHull_LosesToOneThatLiftsTheLoad(bool knownMass, string prefab, string origin)
        {
            TradeManager manager;
            Fixture f = BuildLiftLimitedIce(knownMass, out manager);

            manager.Tick(f.States, f.World, f.Tuning, new List<string>());

            PendingFleetAction buy = Assert.Single(Procurements(f.Buyer));
            Assert.Equal(prefab, buy.ShipPrefabId);
            Assert.Equal(origin, buy.OriginNodeId);
        }

        /// <summary>Tier 1, the sell direction: the Brokkr and the Mid both at the seller's node.</summary>
        [Theory]
        [InlineData(true, "Mid")]
        [InlineData(false, "Brokkr")]
        public void AMassLimitedHull_LosesAtTheSellersOwnNodeToo(bool knownMass, string expected)
        {
            TradeManager manager;
            Fixture f = BuildLiftLimitedIce(knownMass, out manager);
            f.Buyer.UnservedDemands.Clear();
            f.Buyer.TradePriority.Clear();
            foreach (string id in new[] { "B2", "B3", "B4" }) f.World.Graph.Nodes[id].ShipStock.Clear();
            f.SellerNode.ShipStock["Brokkr"] = 1;
            f.SellerNode.ShipStock["Mid"] = 1;
            TradeTest.Whitelist(f.Seller, "B");
            TradeTest.Unserved(f.Buyer, "B1", Ice, 20_000);

            manager.Tick(f.States, f.World, f.Tuning, new List<string>());

            PendingFleetAction sale = Assert.Single(f.Seller.PendingFleets, p => p.Kind == ActionKind.Trade);
            Assert.False(sale.IsProcurement);
            Assert.Equal(expected, sale.ShipPrefabId);
        }
    }
}
