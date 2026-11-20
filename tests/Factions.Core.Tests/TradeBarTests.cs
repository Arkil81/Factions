using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Rule 1 of docs/diplomacy-plan.md: trade is barred when EITHER side says so. The actor's own
    /// <see cref="FactionState.TradePriority"/> reads only the actor's view, so a partner that held
    /// the actor hostile or embargoed was traded with anyway -- the live case was IMPR at -0.63
    /// toward EXP, EXP in IMPR's embargo set, and 23 <c>PROCURE_… EXP &lt;- IMPR</c> in one day.
    ///
    /// <para>Every fixture here whitelists the partner by hand, so the actor's own view is friendly
    /// by construction and only the second key -- <see cref="Diplomacy.TradeBarred"/>, or a player
    /// partner's switch -- can stop the trade. Each headline is paired with the unbarred control
    /// on the identical fixture, so "nothing traded" cannot pass on a fixture that trades nothing.</para>
    /// </summary>
    public class TradeBarTests
    {
        private const double Foe = -0.63;       // IMPR's standing toward EXP on 09-26
        private const double Friend = 0.5;

        // ---- fixtures ------------------------------------------------------------------------

        /// <summary>Seller "A" glutted in Iron at A1 with one Trade hull; buyer "B" short of it at
        /// B1 and hull-less, so only the SELL direction can serve the row. Mirrors
        /// <c>TradeManagerTests.BuildSellerAndBuyer</c>.</summary>
        private static void SellFixture(out WorldModel world, out FactionTuning tuning,
            out FactionState seller, out FactionState buyer, out TradeTest.NodeScarcityOracle oracle)
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", 1_000_000);
            world.SetTreasury("B", 1_000_000);

            SpatialNode a1 = TradeTest.Node(world, "A1", "A");
            a1.Inventory["Iron"] = 5000;
            a1.ShipStock["Trade"] = 1;
            TradeTest.Node(world, "B1", "B", x: 100);

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 0.1)
                .Set("B1", ItemKind.Ore, 2.0);

            seller = TradeTest.State("A");
            buyer = TradeTest.State("B");
            seller.CentralBankRecentIncome = 1_000_000;
            buyer.CentralBankRecentIncome = 1_000_000;
            seller.Relations["B"] = Friend;
            buyer.Relations["A"] = Friend;
            TradeTest.Whitelist(seller, "B");
            TradeTest.Whitelist(buyer, "A");
            TradeTest.Unserved(buyer, "B1", "Iron", 100);
        }

        /// <summary>Buyer "A" short of Iron at A1 with two Trade hulls; seller "B" glutted at B1 and
        /// hull-less, so only the BUY direction can serve the row. Mirrors
        /// <c>TradeProcurementTests.BuildBuyerAndSeller</c>.</summary>
        private static void BuyFixture(out WorldModel world, out FactionTuning tuning,
            out FactionState buyer, out FactionState seller, out TradeTest.NodeScarcityOracle oracle)
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", 1_000_000);
            world.SetTreasury("B", tuning.InsolvencyFloor);

            SpatialNode home = TradeTest.Node(world, "A1", "A");
            home.ShipStock["Trade"] = 2;
            SpatialNode sellerNode = TradeTest.Node(world, "B1", "B", x: 100);
            sellerNode.Inventory["Iron"] = 5000;

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 2.0)
                .Set("B1", ItemKind.Ore, 0.1);

            buyer = TradeTest.State("A");
            seller = TradeTest.State("B");
            buyer.CentralBankRecentIncome = 1_000_000;
            seller.CentralBankRecentIncome = 1_000_000;
            buyer.Relations["B"] = Friend;
            seller.Relations["A"] = Friend;
            TradeTest.Whitelist(buyer, "B");
            TradeTest.Unserved(buyer, "A1", "Iron", 100);
        }

        private static List<string> Tick(TradeManager manager, WorldModel world, FactionTuning tuning,
            List<FactionState> states)
        {
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);
            return log;
        }

        private static IEnumerable<PendingFleetAction> Trades(FactionState st)
        {
            return st.PendingFleets.Where(f => f.Kind == ActionKind.Trade);
        }

        private static FactionState PlayerState(string tag)
        {
            FactionState st = TradeTest.State(tag);
            st.IsPlayerFaction = true;
            st.CentralBankRecentIncome = 1_000_000;
            return st;
        }

        // ---- the predicate -------------------------------------------------------------------

        public enum Side { None, A, B }

        [Theory]
        //           a->b     b->a     embargo  expected
        [InlineData(0.0,     0.0,     Side.None, false)]   // neutral pair
        [InlineData(0.5,     0.5,     Side.None, false)]   // friendly pair
        [InlineData(-0.63,   0.5,     Side.None, true)]    // a hostile, b friendly
        [InlineData(0.5,     -0.63,   Side.None, true)]    // b hostile, a friendly
        [InlineData(-0.63,   -0.63,   Side.None, true)]    // open war
        [InlineData(-0.3,    -0.3,    Side.None, false)]   // AT the line is not below it
        [InlineData(-0.2999, -0.3001, Side.None, true)]    // just under on one side
        [InlineData(0.5,     0.5,     Side.A,    true)]    // a embargoes b
        [InlineData(0.5,     0.5,     Side.B,    true)]    // b embargoes a
        public void TradeBarred_TruthTable(double aToB, double bToA, Side embargoHolder, bool expected)
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(-0.3, t.HostileRelationThreshold);   // the rows above are written against it
            FactionState a = TradeTest.State("A"), b = TradeTest.State("B");
            a.Relations["B"] = aToB;
            b.Relations["A"] = bToA;
            if (embargoHolder == Side.A) a.Embargoes.Add("B");
            if (embargoHolder == Side.B) b.Embargoes.Add("A");

            Assert.Equal(expected, Diplomacy.TradeBarred(a, b, t));
            Assert.Equal(expected, Diplomacy.TradeBarred(b, a, t));   // symmetric
        }

        [Fact]
        public void TradeBarred_IsNullSafe()
        {
            FactionTuning t = TestSupport.Tuning();
            FactionState a = TradeTest.State("A");
            a.Relations["B"] = Foe;
            a.Embargoes.Add("B");

            Assert.False(Diplomacy.TradeBarred(a, null, t));
            Assert.False(Diplomacy.TradeBarred(null, a, t));
            Assert.False(Diplomacy.TradeBarred(null, null, t));

            // A null tuning skips the relation half only; the embargo is a set, not a threshold.
            FactionState b = TradeTest.State("B");
            Assert.True(Diplomacy.TradeBarred(a, b, null));
            a.Embargoes.Clear();
            Assert.False(Diplomacy.TradeBarred(a, b, null));
        }

        /// <summary>A missing relation reads neutral, never hostile: two factions that have never
        /// met are not barred.</summary>
        [Fact]
        public void TradeBarred_NoRelationEntry_IsNeutral()
        {
            Assert.False(Diplomacy.TradeBarred(TradeTest.State("A"), TradeTest.State("B"), TestSupport.Tuning()));
        }

        // ---- the passes: the operator's IMPR/EXP case -----------------------------------------

        /// <summary>The live case: the SELLER holds the buyer hostile, the buyer's own view is
        /// friendly and whitelists it. The buyer's procurement convoy must not form.</summary>
        [Fact]
        public void BuyPass_SellerHostileToBuyer_BuysNothing_ThoughTheBuyersViewIsFriendly()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuyFixture(out world, out tuning, out a, out b, out oracle);
            b.Relations["A"] = Foe;

            TradeManager manager = TradeTest.Manager(oracle);
            Tick(manager, world, tuning, new List<FactionState> { a, b });

            Assert.Empty(Trades(a));
            Assert.True(manager.LastTelemetry.Barred >= 1);
            Assert.Equal(5000L, world.Graph.Nodes["B1"].Inventory["Iron"]);
        }

        /// <summary>The mirror: the BUYER holds the seller hostile, the seller's own view is friendly
        /// and whitelists it. The seller's convoy must not form.</summary>
        [Fact]
        public void SellPass_BuyerHostileToSeller_SellsNothing_ThoughTheSellersViewIsFriendly()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            SellFixture(out world, out tuning, out a, out b, out oracle);
            b.Relations["A"] = Foe;

            TradeManager manager = TradeTest.Manager(oracle);
            List<string> log = Tick(manager, world, tuning, new List<FactionState> { a, b });

            Assert.Empty(Trades(a));
            Assert.Equal(5000L, world.Graph.Nodes["A1"].Inventory["Iron"]);
            Assert.DoesNotContain(log, l => l.StartsWith("TRADE_DISPATCHED ", StringComparison.Ordinal));
            Assert.True(manager.LastTelemetry.Barred >= 1);
        }

        /// <summary>The actor's own hostility bars too, even with a stale whitelist entry that still
        /// names the partner: the list is recomputed per brain cycle, the pass runs ten times as often.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EitherPass_ActorHostileWithAStaleWhitelist_TradesNothing(bool selling)
        {
            WorldModel world; FactionTuning tuning; FactionState actor, partner; TradeTest.NodeScarcityOracle oracle;
            if (selling) SellFixture(out world, out tuning, out actor, out partner, out oracle);
            else BuyFixture(out world, out tuning, out actor, out partner, out oracle);
            actor.Relations[partner.Tag] = Foe;

            Tick(TradeTest.Manager(oracle), world, tuning, new List<FactionState> { actor, partner });

            Assert.Empty(Trades(actor));
            Assert.Empty(Trades(partner));
        }

        // ---- embargo on either side, both directions -----------------------------------------

        [Theory]
        [InlineData(true,  true)]    // sell pass, the seller (actor) embargoes
        [InlineData(true,  false)]   // sell pass, the buyer (partner) embargoes
        [InlineData(false, true)]    // buy pass, the buyer (actor) embargoes
        [InlineData(false, false)]   // buy pass, the seller (partner) embargoes
        public void AnEmbargoOnEitherSide_BarsBothDirections(bool selling, bool actorEmbargoes)
        {
            WorldModel world; FactionTuning tuning; FactionState actor, partner; TradeTest.NodeScarcityOracle oracle;
            if (selling) SellFixture(out world, out tuning, out actor, out partner, out oracle);
            else BuyFixture(out world, out tuning, out actor, out partner, out oracle);
            // Relations stay friendly on both sides: the embargo alone must bar.
            if (actorEmbargoes) actor.Embargoes.Add(partner.Tag);
            else partner.Embargoes.Add(actor.Tag);

            TradeManager manager = TradeTest.Manager(oracle);
            Tick(manager, world, tuning, new List<FactionState> { actor, partner });

            Assert.Empty(Trades(actor));
            Assert.Empty(Trades(partner));
            Assert.True(manager.LastTelemetry.Barred >= 1);
        }

        // ---- the regression guard ------------------------------------------------------------

        /// <summary>An unbarred pair trades exactly as before, in both directions, and counts no
        /// bar. Both sides sit just above the hostile line -- cold but not barred -- so a guard that
        /// read `&lt;=` or used the embargo line instead would fail here.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnUnbarredPair_TradesAsBefore(bool selling)
        {
            WorldModel world; FactionTuning tuning; FactionState actor, partner; TradeTest.NodeScarcityOracle oracle;
            if (selling) SellFixture(out world, out tuning, out actor, out partner, out oracle);
            else BuyFixture(out world, out tuning, out actor, out partner, out oracle);
            actor.Relations[partner.Tag] = tuning.HostileRelationThreshold;
            partner.Relations[actor.Tag] = tuning.HostileRelationThreshold;

            TradeManager manager = TradeTest.Manager(oracle);
            List<string> log = Tick(manager, world, tuning, new List<FactionState> { actor, partner });

            PendingFleetAction fleet = Assert.Single(Trades(actor));
            Assert.Equal(partner.Tag, fleet.TargetTag);
            Assert.Equal(!selling, fleet.IsProcurement);
            Assert.Equal(0, manager.LastTelemetry.Barred);
            Assert.Equal(0, manager.LastTelemetry.OptedOut);
            Assert.Contains(log, l => l.StartsWith("TRADE_TICK_PERF " + actor.Tag + " ", StringComparison.Ordinal)
                                      && l.Contains(" barred=0 ") && l.Contains(" optedOut=0 "));
        }

        /// <summary>A barred partner is skipped, not the whole pass: a second, open partner is still
        /// served, and the perf line names the one that was barred.</summary>
        [Fact]
        public void ABarredPartner_DoesNotStopTheRestOfThePass()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            SellFixture(out world, out tuning, out a, out b, out oracle);
            TradeTest.Node(world, "C1", "C", x: 100, y: 10);
            oracle.Set("C1", ItemKind.Ore, 2.0);
            FactionState c = TradeTest.State("C");
            c.CentralBankRecentIncome = 1_000_000;
            world.SetTreasury("C", 1_000_000);
            TradeTest.Unserved(c, "C1", "Iron", 100);
            a.TradePriority.Clear();
            TradeTest.Whitelist(a, "B", 2.0);   // ranked first, and barred
            TradeTest.Whitelist(a, "C", 1.0);
            b.Embargoes.Add("A");

            TradeManager manager = TradeTest.Manager(oracle);
            List<string> log = Tick(manager, world, tuning, new List<FactionState> { a, b, c });

            PendingFleetAction fleet = Assert.Single(Trades(a));
            Assert.Equal("C", fleet.TargetTag);
            Assert.Contains(log, l => l.StartsWith("TRADE_TICK_PERF A ", StringComparison.Ordinal) && l.Contains(" barred=1 "));
        }

        // ---- in flight when the bar starts: both settlements ----------------------------------

        [Theory]
        [InlineData(false)]   // the seller turns hostile
        [InlineData(true)]    // the seller embargoes
        public void ProcurementDock_BarStartsInFlight_BuysNothing_AndFliesHomeEmpty(bool embargo)
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuyFixture(out world, out tuning, out a, out b, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);
            Assert.True(Assert.Single(Trades(a)).IsProcurement);

            if (embargo) b.Embargoes.Add("A"); else b.Relations["A"] = Foe;
            long buyerStart = world.GetTreasury("A"), sellerStart = world.GetTreasury("B");

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // the dock
            Assert.Equal(5000L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Equal(buyerStart, world.GetTreasury("A"));
            Assert.Equal(sellerStart, world.GetTreasury("B"));
            Assert.Contains(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal)
                                      && l.Contains(" paid=0") && l.Contains(" refused=barred"));

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // home
            long delivered;
            world.Graph.Nodes["A1"].Inventory.TryGetValue("Iron", out delivered);
            Assert.Equal(0L, delivered);
        }

        /// <summary>The control for the test above on the identical fixture: with no bar the dock
        /// buys, so the refusal there is the bar and not the fixture.</summary>
        [Fact]
        public void ProcurementDock_Unbarred_Buys()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuyFixture(out world, out tuning, out a, out b, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);
            Assert.Equal(4900L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.DoesNotContain(log, l => l.Contains(" refused="));
        }

        [Theory]
        [InlineData(false)]   // the buyer turns hostile
        [InlineData(true)]    // the buyer embargoes
        public void SellSettlement_BarStartsInFlight_SettlesNothing_WarmsNothing_AndTheHoldComesHome(bool embargo)
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            SellFixture(out world, out tuning, out a, out b, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);
            Assert.False(Assert.Single(Trades(a)).IsProcurement);
            Assert.Equal(4900L, world.Graph.Nodes["A1"].Inventory["Iron"]);   // loaded at dispatch

            if (embargo) b.Embargoes.Add("A"); else b.Relations["A"] = Foe;
            double aToB = a.RelationTo("B"), bToA = b.RelationTo("A");
            long sellerStart = world.GetTreasury("A"), buyerStart = world.GetTreasury("B");

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // Service
            long atBuyer;
            world.Graph.Nodes["B1"].Inventory.TryGetValue("Iron", out atBuyer);
            Assert.Equal(0L, atBuyer);
            Assert.Equal(sellerStart, world.GetTreasury("A"));
            Assert.Equal(buyerStart, world.GetTreasury("B"));
            Assert.Equal(aToB, a.RelationTo("B"));    // a barred trade lifts nobody
            Assert.Equal(bToA, b.RelationTo("A"));
            Assert.Contains(log, l => l.StartsWith("TRADE_RESOLVED ", StringComparison.Ordinal)
                                      && l.Contains(" settled=0") && l.Contains(" refused_counterparty=100")
                                      && l.Contains(" refused=barred"));

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);   // Return
            Assert.Equal(5000L, world.Graph.Nodes["A1"].Inventory["Iron"]);          // the whole hold came home
        }

        /// <summary>The control: the identical fixture, unbarred, settles and warms both sides.</summary>
        [Fact]
        public void SellSettlement_Unbarred_SettlesAndWarms()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            SellFixture(out world, out tuning, out a, out b, out oracle);
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);
            double aToB = a.RelationTo("B");

            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);
            Assert.Equal(100L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.True(a.RelationTo("B") > aToB);
            Assert.DoesNotContain(log, l => l.Contains(" refused="));
        }

        // ---- the embargo is managed below the hostile line -----------------------------------

        private static void ComputeTradePriority(FactionState s, IWorldView world, FactionTuning t)
        {
            MethodInfo m = typeof(FactionBrain).GetMethod("ComputeTradePriority",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            m.Invoke(null, new object[] { s, world, t, true });
        }

        /// <summary>A relation that fell straight past the cold-war band into open war used to skip
        /// the embargo branch entirely, so no embargo was placed. The embargo is now read by the
        /// partner too, so it must be placed -- and lifted again once the relation recovers.</summary>
        [Fact]
        public void Embargo_IsAddedBelowTheHostileLine_AndLiftedOnRecovery()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            TradeTest.Node(world, "A1", "A");
            SpatialNode b1 = TradeTest.Node(world, "B1", "B", x: 10);
            b1.ResourceValue = 1.0;
            FactionState a = TradeTest.State("A");

            a.Relations["B"] = Foe;
            Assert.True(Foe < tuning.HostileRelationThreshold);
            ComputeTradePriority(a, world, tuning);
            Assert.Contains("B", a.Embargoes);
            Assert.DoesNotContain(a.TradePriority, e => e.PartnerTag == "B");

            a.Relations["B"] = tuning.TruceRelation;   // a truce's wary peace
            Assert.True(tuning.TruceRelation > tuning.EmbargoRelationThreshold + tuning.EmbargoHysteresis);
            ComputeTradePriority(a, world, tuning);
            Assert.DoesNotContain("B", a.Embargoes);
            Assert.Contains(a.TradePriority, e => e.PartnerTag == "B");
        }

        /// <summary>An embargo placed earlier is kept, not lifted, while the relation stays below the
        /// hostile line: the lift reads the relation, and a hostile one has not recovered.</summary>
        [Fact]
        public void Embargo_HeldBelowTheHostileLine_IsKept()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            TradeTest.Node(world, "A1", "A");
            TradeTest.Node(world, "B1", "B", x: 10).ResourceValue = 1.0;
            FactionState a = TradeTest.State("A");
            a.Embargoes.Add("B");
            a.Relations["B"] = Foe;

            ComputeTradePriority(a, world, tuning);

            Assert.Contains("B", a.Embargoes);
        }

        // ---- the player's switches -----------------------------------------------------------

        /// <summary>A player faction's sell switch is its own: an NPC's buy pass does not buy from a
        /// player whose <see cref="FactionState.TradeSellOptIn"/> is off, even when the player
        /// opted in to BUYING. The control flips only the sell switch.</summary>
        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 1)]
        public void BuyPass_HonoursAPlayerSellersSwitch(bool sellOptIn, int expectedConvoys)
        {
            WorldModel world; FactionTuning tuning; FactionState npc, ignored; TradeTest.NodeScarcityOracle oracle;
            BuyFixture(out world, out tuning, out npc, out ignored, out oracle);
            FactionState player = PlayerState("B");
            player.Relations["A"] = Friend;
            player.TradeBuyOptIn = true;
            player.TradeSellOptIn = sellOptIn;

            TradeManager manager = TradeTest.Manager(oracle);
            Tick(manager, world, tuning, new List<FactionState> { npc, player });

            Assert.Equal(expectedConvoys, Trades(npc).Count());
            Assert.Equal(sellOptIn ? 0 : 1, manager.LastTelemetry.OptedOut);
        }

        /// <summary>The mirror: an NPC's sell pass does not deliver to a player partner whose buy
        /// switch is off, even with a row on its list.</summary>
        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 1)]
        public void SellPass_HonoursAPlayerBuyersSwitch(bool buyOptIn, int expectedConvoys)
        {
            WorldModel world; FactionTuning tuning; FactionState npc, ignored; TradeTest.NodeScarcityOracle oracle;
            SellFixture(out world, out tuning, out npc, out ignored, out oracle);
            FactionState player = PlayerState("B");
            player.Relations["A"] = Friend;
            player.TradeBuyOptIn = buyOptIn;
            TradeTest.Unserved(player, "B1", "Iron", 100);

            TradeManager manager = TradeTest.Manager(oracle);
            Tick(manager, world, tuning, new List<FactionState> { npc, player });

            Assert.Equal(expectedConvoys, Trades(npc).Count());
        }

        /// <summary>A player who switches selling off while an NPC's convoy is flying to buy keeps
        /// the stock: the dock asks the same switch.</summary>
        [Fact]
        public void ProcurementDock_PlayerSwitchesSellingOffInFlight_BuysNothing()
        {
            WorldModel world; FactionTuning tuning; FactionState npc, ignored; TradeTest.NodeScarcityOracle oracle;
            BuyFixture(out world, out tuning, out npc, out ignored, out oracle);
            FactionState player = PlayerState("B");
            player.Relations["A"] = Friend;
            player.TradeSellOptIn = true;
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { npc, player };
            var log = new List<string>();
            manager.Tick(states, world, tuning, log);
            Assert.Single(Trades(npc));

            player.TradeSellOptIn = false;
            TradeTest.ResolveOneLeg(states, world, tuning, log, oracle, manager);

            Assert.Equal(5000L, world.Graph.Nodes["B1"].Inventory["Iron"]);
            Assert.Contains(log, l => l.StartsWith("PROCURE_BOUGHT ", StringComparison.Ordinal)
                                      && l.Contains(" paid=0") && l.Contains(" refused=opted_out"));
        }

        // ---- the trace -------------------------------------------------------------------------

        [Fact]
        public void PartnerClosedTrace_NamesTheReason_OnChangeOnly_AndOnlyWhenArmed()
        {
            WorldModel world; FactionTuning tuning; FactionState a, b; TradeTest.NodeScarcityOracle oracle;
            BuyFixture(out world, out tuning, out a, out b, out oracle);
            b.Embargoes.Add("A");
            TradeManager manager = TradeTest.Manager(oracle);
            var states = new List<FactionState> { a, b };

            bool was = DebugTraces.Trade;
            try
            {
                DebugTraces.Trade = false;
                Assert.DoesNotContain(Tick(manager, world, tuning, states), l => l.StartsWith("TRADE_PARTNER_CLOSED", StringComparison.Ordinal));

                DebugTraces.Trade = true;
                List<string> first = Tick(manager, world, tuning, states);
                Assert.Contains(first, l => l == "TRADE_PARTNER_CLOSED actor=A partner=B dir=buy reason=embargo:B");
                Assert.DoesNotContain(Tick(manager, world, tuning, states), l => l.StartsWith("TRADE_PARTNER_CLOSED", StringComparison.Ordinal));
            }
            finally { DebugTraces.Trade = was; }
        }
    }
}
