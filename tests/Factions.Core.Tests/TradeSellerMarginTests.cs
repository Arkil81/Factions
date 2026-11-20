using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A seller node's stock is promised once, not once per row (FAC-109's trade findings). Every
    /// vector is capped at the node's consenting margin — the stock above the level where it would
    /// stop consenting, net of its build-queue reserve — less what in-flight convoys and earlier
    /// vectors in the same pass have already claimed. And the buyer's seller is chosen across all its
    /// partners by how much of the row a node can cover, not by which partner ranks first.
    ///
    /// <para>Every seller node here has a real reference stock of 100, and its scarcity is set to
    /// <c>reference / stock</c> as the oracle computes it, so at the 0.50 ceiling it consents down to
    /// a floor of 200 and its margin is <c>stock - 200</c>.</para>
    /// </summary>
    public class TradeSellerMarginTests
    {
        private const long Reference = 100;
        private const long Floor = 200;   // Reference / 0.50

        private static WorldModel NewWorld(out FactionTuning tuning, out TradeTest.NodeScarcityOracle oracle)
        {
            tuning = TestSupport.Tuning();
            tuning.ProcurementSellerScarcityCeiling = 0.50;
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            oracle = new TradeTest.NodeScarcityOracle();
            return world;
        }

        private static SpatialNode SellerNode(WorldModel world, TradeTest.NodeScarcityOracle oracle, string id,
            string owner, long stock, double x = 0, double y = 0)
        {
            SpatialNode n = TradeTest.Node(world, id, owner, x: x, y: y);
            n.Inventory["Iron"] = stock;
            oracle.Set(id, "Iron", (double)Reference / stock).SetReference(id, "Iron", Reference);
            return n;
        }

        private static FactionState Faction(WorldModel world, string tag)
        {
            world.SetTreasury(tag, 1_000_000);
            FactionState st = TradeTest.State(tag);
            st.CentralBankRecentIncome = 1_000_000;
            return st;
        }

        /// <summary>A buyer with one hull at its own node and a row for <paramref name="want"/> Iron
        /// there.</summary>
        private static FactionState Buyer(WorldModel world, string tag, string nodeId, long want,
            double x = 0, double y = 0)
        {
            SpatialNode n = TradeTest.Node(world, nodeId, tag, x: x, y: y);
            n.ShipStock["Trade"] = 1;
            FactionState st = Faction(world, tag);
            TradeTest.Unserved(st, nodeId, "Iron", want);
            return st;
        }

        private static List<string> Tick(TradeManager manager, WorldModel world, FactionTuning tuning,
            params FactionState[] states)
        {
            var log = new List<string>();
            manager.Tick(states.ToList(), world, tuning, log);
            return log;
        }

        private static PendingFleetAction Procurement(FactionState st)
        {
            PendingFleetAction f = Assert.Single(st.PendingFleets, p => p.Kind == ActionKind.Trade);
            Assert.True(f.IsProcurement);
            return f;
        }

        // ---- the margin, and the ledger that books it ------------------------------------------

        /// <summary>The characteristic failure: two rows, one seller node, and each row was handed the
        /// node's whole surplus. Now the pair gets the margin between them — the first its full 600,
        /// the second the 200 left — and the node is left standing exactly at its floor.</summary>
        [Fact]
        public void TwoRowsAgainstOneSellerNode_GetAtMostItsMarginBetweenThem()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000).ShipStock["Trade"] = 2;
            TradeTest.Node(world, "B1", "B", x: 100);
            TradeTest.Node(world, "B2", "B", x: 100, y: 50);
            FactionState a = Faction(world, "A"), b = Faction(world, "B");
            TradeTest.Whitelist(a, "B");
            TradeTest.Unserved(b, "B1", "Iron", 600);
            TradeTest.Unserved(b, "B2", "Iron", 600);

            List<string> log = Tick(TradeTest.Manager(oracle), world, tuning, a, b);

            List<PendingFleetAction> trades = a.PendingFleets.Where(f => f.Kind == ActionKind.Trade).ToList();
            Assert.Equal(2, trades.Count);
            Assert.Equal(1000L - Floor, trades.Sum(f => f.Cargo["Iron"]));
            Assert.Equal(Floor, world.Graph.Nodes["A1"].Inventory["Iron"]);
            Assert.Contains(log, l => l.StartsWith("TRADE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" to=B1 ") && l.Contains(" qty=600 ") && l.EndsWith(" margin=800"));
            Assert.Contains(log, l => l.StartsWith("TRADE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" to=B2 ") && l.Contains(" qty=200 ") && l.EndsWith(" margin=200"));
        }

        /// <summary>The buy direction books too: one buyer's two rows against one seller node share
        /// its margin.</summary>
        [Fact]
        public void OneBuyersTwoRows_ShareTheSellerNodesMargin()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000);
            FactionState a = Faction(world, "A");
            FactionState b = Buyer(world, "B", "B1", 600, x: 100);
            TradeTest.Node(world, "B2", "B", x: 100, y: 50).ShipStock["Trade"] = 1;
            TradeTest.Unserved(b, "B2", "Iron", 600);
            TradeTest.Whitelist(b, "A");

            Tick(TradeTest.Manager(oracle), world, tuning, b, a);

            List<PendingFleetAction> buys = b.PendingFleets.Where(f => f.Kind == ActionKind.Trade).ToList();
            Assert.Equal(2, buys.Count);
            Assert.Equal(1000L - Floor, buys.Sum(f => f.PickupQuantity));
        }

        /// <summary>A seller serving two partners in one pass loads the first at dispatch (tier 1),
        /// so the node's live stock has already fallen by the time the second partner's rows form.
        /// The margin is read once per pass and the booking carries the first convoy, so the second
        /// partner gets exactly the remainder — reading the live stock again would count those units
        /// twice and give it nothing.</summary>
        [Fact]
        public void ASecondPartnerInTheSameSellPass_GetsTheRemainder_NotADoubleCount()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000).ShipStock["Trade"] = 2;
            TradeTest.Node(world, "B1", "B", x: 100);
            TradeTest.Node(world, "C1", "C", x: -100);
            FactionState a = Faction(world, "A"), b = Faction(world, "B"), c = Faction(world, "C");
            TradeTest.Whitelist(a, "B");
            TradeTest.Whitelist(a, "C");
            TradeTest.Unserved(b, "B1", "Iron", 600);
            TradeTest.Unserved(c, "C1", "Iron", 600);

            Tick(TradeTest.Manager(oracle), world, tuning, a, b, c);

            List<PendingFleetAction> trades = a.PendingFleets.Where(f => f.Kind == ActionKind.Trade).ToList();
            Assert.Equal(600L, Assert.Single(trades, f => f.TargetTag == "B").Cargo["Iron"]);
            Assert.Equal(200L, Assert.Single(trades, f => f.TargetTag == "C").Cargo["Iron"]);
            Assert.Equal(Floor, world.Graph.Nodes["A1"].Inventory["Iron"]);
        }

        /// <summary>A booking whose vector never flew is handed back. The first partner's five units
        /// are held by the convoy assessment (light and not urgent) and nothing folds them in, so the
        /// second partner, urgent, is offered the whole margin rather than the margin less five.</summary>
        [Fact]
        public void AVectorThatNeverFlew_HandsItsBookingBack()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000).ShipStock["Trade"] = 2;
            TradeTest.Node(world, "B1", "B", x: 100);
            TradeTest.Node(world, "C1", "C", x: -100);
            FactionState a = Faction(world, "A"), b = Faction(world, "B"), c = Faction(world, "C");
            TradeTest.Whitelist(a, "B");
            TradeTest.Whitelist(a, "C");
            TradeTest.Unserved(b, "B1", "Iron", 5, urgency: 1.0);
            TradeTest.Unserved(c, "C1", "Iron", 800, urgency: 3.0);
            var volumes = new TradeTest.Volumes().Set("Iron", 1.0);

            List<string> log = Tick(TradeTest.Manager(oracle, volumes), world, tuning, a, b, c);

            Assert.Contains(log, l => l.Contains("TRADE_LOAD_GATED") && l.Contains("dest=B1"));
            PendingFleetAction toC = Assert.Single(a.PendingFleets, f => f.Kind == ActionKind.Trade);
            Assert.Equal("C", toC.TargetTag);
            Assert.Equal(800L, toC.Cargo["Iron"]);
        }

        /// <summary>Two buyers, one seller node. The second buyer's pass sees the first's convoy — it
        /// is on the roster as a pending Pickup at that node — and is offered only what is left.</summary>
        [Fact]
        public void ASecondFactionsPass_SeesTheFirstsClaim()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000);
            FactionState a = Faction(world, "A");
            FactionState b = Buyer(world, "B", "B1", 600, x: 100);
            FactionState c = Buyer(world, "C", "C1", 600, x: -100);
            TradeTest.Whitelist(b, "A");
            TradeTest.Whitelist(c, "A");

            List<string> log = Tick(TradeTest.Manager(oracle), world, tuning, b, c, a);

            Assert.Equal(600L, Procurement(b).PickupQuantity);
            Assert.Equal(200L, Procurement(c).PickupQuantity);
            Assert.Contains(log, l => l.StartsWith("PROCURE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" C <- A ") && l.Contains(" qty=200 ") && l.EndsWith(" margin=200"));
        }

        /// <summary>Convoys already in the air before this sweep are claims too — on the current leg
        /// and on a Pickup stop still queued behind it. Cargo already aboard has left the stock and is
        /// not counted again.</summary>
        [Fact]
        public void AnInFlightConvoysPendingPickup_IsClaimed()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000);
            FactionState a = Faction(world, "A");
            FactionState b = Buyer(world, "B", "B1", 600, x: 100);
            TradeTest.Whitelist(b, "A");
            FactionState d = Faction(world, "D");
            d.PendingFleets.Add(InFlight(world, "D", currentPickup: 300));
            d.PendingFleets.Add(InFlight(world, "D", queuedPickup: 200));
            d.PendingFleets.Add(InFlight(world, "D", aboard: 5000));

            Tick(TradeTest.Manager(oracle), world, tuning, b, d, a);

            Assert.Equal(800L - 300 - 200, Procurement(b).PickupQuantity);
        }

        /// <summary>A node whose whole margin is claimed in flight forms no vector at all.</summary>
        [Fact]
        public void AMarginWhollyClaimedInFlight_FormsNoVector()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000);
            FactionState a = Faction(world, "A");
            FactionState b = Buyer(world, "B", "B1", 600, x: 100);
            TradeTest.Whitelist(b, "A");
            FactionState d = Faction(world, "D");
            d.PendingFleets.Add(InFlight(world, "D", currentPickup: 800));

            Tick(TradeTest.Manager(oracle), world, tuning, b, d, a);

            Assert.DoesNotContain(b.PendingFleets, f => f.Kind == ActionKind.Trade);
        }

        /// <summary>A node standing exactly at its floor still consents — its scarcity reads the
        /// ceiling — but has nothing it can part with and stay consenting, so no vector forms.</summary>
        [Fact]
        public void NoSellerWithMargin_FormsNoVector()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SpatialNode a1 = SellerNode(world, oracle, "A1", "A", stock: Floor);
            FactionState a = Faction(world, "A");
            FactionState b = Buyer(world, "B", "B1", 600, x: 100);
            TradeTest.Whitelist(b, "A");
            Assert.True(TradeConsent.SellerConsents(oracle, world, "A", a1, "Iron", tuning));

            Tick(TradeTest.Manager(oracle), world, tuning, b, a);

            Assert.DoesNotContain(b.PendingFleets, f => f.Kind == ActionKind.Trade);
        }

        /// <summary>The build-queue reserve leaves the node too, so it comes off the margin rather
        /// than out of the floor.</summary>
        [Fact]
        public void TheBuildQueueReserve_ComesOffTheMargin()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SpatialNode a1 = SellerNode(world, oracle, "A1", "A", stock: 1000);
            var claim = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "Trade",
                ExpiresUtc = world.UtcNow.AddDays(30),
            };
            claim.Bom["Iron"] = 500;
            a1.BuildQueue.Add(claim);
            FactionState a = Faction(world, "A");
            FactionState b = Buyer(world, "B", "B1", 600, x: 100);
            TradeTest.Whitelist(b, "A");

            Tick(TradeTest.Manager(oracle), world, tuning, b, a);

            Assert.Equal(1000L - 500 - Floor, Procurement(b).PickupQuantity);
        }

        // ---- which seller ------------------------------------------------------------------------

        /// <summary>The second FAC-109 finding: the first-ranked partner with any consenting node won,
        /// so a marginal node beat a depot that could cover the whole row. Coverage now ranks first.</summary>
        [Fact]
        public void ARichPartnerNode_BeatsAMarginalNearerOne()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "S1", "S", stock: 250, x: 100);      // margin 50, near, ranked first
            SellerNode(world, oracle, "R1", "R", stock: 10000, x: 300);    // margin 9800, farther, ranked second
            FactionState s = Faction(world, "S"), r = Faction(world, "R");
            FactionState b = Buyer(world, "B", "B1", 600);
            TradeTest.Whitelist(b, "S", score: 10);
            TradeTest.Whitelist(b, "R", score: 1);

            List<string> log = Tick(TradeTest.Manager(oracle), world, tuning, b, s, r);

            PendingFleetAction f = Procurement(b);
            Assert.Equal("R", f.TargetTag);
            Assert.Equal("R1", f.TargetNodeId);
            Assert.Equal(600L, f.PickupQuantity);
            Assert.Contains(log, l => l.StartsWith("PROCURE_DISPATCHED ", StringComparison.Ordinal)
                                      && l.Contains(" buy_at=R1 ") && l.EndsWith(" margin=9800"));
        }

        /// <summary>Coverage is capped at the row: two nodes that can each cover all of it are equal
        /// on it, and the nearer one wins — a far-off giant does not beat a near node that is enough.</summary>
        [Fact]
        public void WhenBothCoverTheRow_TheNearerWins()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "S1", "S", stock: 100000, x: 300);   // huge, farther, ranked first
            SellerNode(world, oracle, "R1", "R", stock: 2000, x: 100);     // enough, nearer
            FactionState s = Faction(world, "S"), r = Faction(world, "R");
            FactionState b = Buyer(world, "B", "B1", 600);
            TradeTest.Whitelist(b, "S", score: 10);
            TradeTest.Whitelist(b, "R", score: 1);

            Tick(TradeTest.Manager(oracle), world, tuning, b, s, r);

            Assert.Equal("R1", Procurement(b).TargetNodeId);
        }

        /// <summary>Partner rank still breaks what coverage and distance leave tied. "R1" sorts before
        /// "S1", so the node-id fallback alone would pick the lower-ranked partner.</summary>
        [Fact]
        public void EqualCoverageAndDistance_FallBackToPartnerRank()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "S1", "S", stock: 2000, x: 100);
            SellerNode(world, oracle, "R1", "R", stock: 2000, x: -100);
            FactionState s = Faction(world, "S"), r = Faction(world, "R");
            FactionState b = Buyer(world, "B", "B1", 600);
            TradeTest.Whitelist(b, "S", score: 10);
            TradeTest.Whitelist(b, "R", score: 1);

            Tick(TradeTest.Manager(oracle), world, tuning, b, s, r);

            Assert.Equal("S1", Procurement(b).TargetNodeId);
        }

        // ---- the dock ----------------------------------------------------------------------------

        /// <summary>The dock is unchanged and stays the final word: it sells what consent and the
        /// routable stock allow when the convoy arrives, not what the margin was at dispatch. A
        /// convoy asking for more than the margin — as every pre-fix convoy still in the air does —
        /// is served exactly as before.</summary>
        [Fact]
        public void TheDock_SellsWhatConsentAndStockAllow_NotTheMargin()
        {
            FactionTuning tuning; TradeTest.NodeScarcityOracle oracle;
            WorldModel world = NewWorld(out tuning, out oracle);
            SellerNode(world, oracle, "A1", "A", stock: 1000);
            FactionState a = Faction(world, "A");
            FactionState b = Buyer(world, "B", "B1", 600, x: 100);
            TradeTest.Whitelist(b, "A");
            var states = new List<FactionState> { b, a };
            TradeManager manager = TradeTest.Manager(oracle);
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);

            int at = b.PendingFleets.FindIndex(f => f.Kind == ActionKind.Trade);
            PendingFleetAction fleet = b.PendingFleets[at];
            Assert.Equal(600L, fleet.PickupQuantity);
            fleet.PickupQuantity = 1000;   // above the 800 margin
            b.PendingFleets[at] = fleet;

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(0L, world.Graph.Nodes["A1"].Inventory.TryGetValue("Iron", out long left) ? left : 0L);
            Assert.Contains(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal) && l.Contains("paid=10000"));
        }

        // ---- fixtures ----------------------------------------------------------------------------

        /// <summary>A Trade convoy another faction already has in the air against A1's Iron.</summary>
        private static PendingFleetAction InFlight(WorldModel world, string actor, long currentPickup = 0,
            long queuedPickup = 0, long aboard = 0)
        {
            var f = new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = actor, TargetTag = "A", IsProcurement = true,
                OriginNodeId = actor + "1",
                DepartedUtc = world.UtcNow.AddMinutes(-1),
                EstimatedArrivalUtc = world.UtcNow.AddHours(1),
                RemainingStops = new List<FleetStop>(),
            };
            if (currentPickup > 0)
            {
                f.TargetNodeId = "A1";
                f.CurrentStopKind = FleetStopKind.Pickup;
                f.PickupItemKey = "Iron";
                f.PickupQuantity = currentPickup;
            }
            else
            {
                f.TargetNodeId = "X1";
                f.CurrentStopKind = FleetStopKind.Service;
            }
            if (queuedPickup > 0)
                f.RemainingStops.Add(new FleetStop
                {
                    NodeId = "A1", Kind = FleetStopKind.Pickup, PickupItemKey = "Iron", PickupQuantity = queuedPickup,
                });
            if (aboard > 0) f.Cargo = new Dictionary<string, long> { { "Iron", aboard } };
            f.RemainingStops.Add(new FleetStop { NodeId = actor + "1", Kind = FleetStopKind.Return });
            return f;
        }
    }
}
