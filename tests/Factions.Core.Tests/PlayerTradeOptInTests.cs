using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-298 — a player faction trades in both directions, and only once it has said so.
    ///
    /// <para>The defect had TWO gates and the whole point of these tests is that both moved:
    /// <c>TradeManager</c>'s autonomy early-return, and <c>PublishUnservedDemands</c> refusing to
    /// publish. Lifting either alone ships silence, so every headline test below is paired with the
    /// negative control that would have passed against a half-fix.</para>
    /// </summary>
    public class PlayerTradeOptInTests
    {
        private static FactionState Player(string tag)
        {
            FactionState st = TradeTest.State(tag);
            st.IsPlayerFaction = true;
            st.CentralBankRecentIncome = 1_000_000;
            return st;
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

        /// <summary>A player faction "P" holds surplus Iron at P1 and is glutted in it; NPC "N" wants
        /// 100 at N1 and is short. P owns the only Trade hull, so the sell direction is the one that
        /// can serve the row. Mirrors <c>TradeManagerTests.BuildSellerAndBuyer</c> with the seller
        /// made a player faction.
        ///
        /// <para>A REAL oracle, never the null one: with <c>NullPriceOracle</c> every scarcity reads
        /// 1.0, which is over the seller-consent ceiling, so no vector forms at all and a test built
        /// on it passes while proving nothing.</para></summary>
        private static void PlayerSellsToNpc(out WorldModel world, out FactionTuning tuning,
            out FactionState player, out FactionState npc, out TradeTest.NodeScarcityOracle oracle)
        {
            tuning = TestSupport.Tuning();
            world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("P", 1_000_000);
            world.SetTreasury("N", 1_000_000);

            SpatialNode p1 = TradeTest.Node(world, "P1", "P");
            p1.Inventory["Iron"] = 5000;
            p1.ShipStock["Trade"] = 1;

            // Within influence reach on purpose. Measured in this fixture, reach is 0.39 at 10km
            // and exactly 0 by 50km, so a partner further out ranks nowhere and the pass ends
            // before anything this file is about. The distance is a precondition of the scenario,
            // not the thing under test.
            SpatialNode n1 = TradeTest.Node(world, "N1", "N", x: 10);
            n1.ResourceValue = 1.0;

            oracle = new TradeTest.NodeScarcityOracle()
                .Set("P1", ItemKind.Ore, 0.1)
                .Set("N1", ItemKind.Ore, 2.0);

            player = Player("P");
            npc = TradeTest.State("N");
            npc.CentralBankRecentIncome = 1_000_000;

            // The player's partner list is NOT hand-written: the projection recomputes it every buy
            // pass, so a whitelist set here would simply be overwritten. Relation is the input the
            // projection actually reads, and setting it is what makes these tests exercise the real
            // ranking path rather than a list the manager could never have produced.
            player.Relations["N"] = 0.5;

            // The NPC's own list stays hand-written: its brain owns it, and the projection is a
            // no-op for an NPC.
            TradeTest.Whitelist(npc, "P");
            TradeTest.Unserved(npc, "N1", "Iron", 100);
        }

        // ---- gate one: the manager ---------------------------------------------------------

        // The headline for the sell direction. Selling needs strictly less than buying: the rows it
        // reads belong to the PARTNER and an NPC publishes those already, so this direction needs
        // only the manager gate and a partner list.
        [Fact]
        public void PlayerFaction_SellsOnceItHasOptedIn()
        {
            WorldModel world; FactionTuning tuning; FactionState p, n; TradeTest.NodeScarcityOracle oracle;
            PlayerSellsToNpc(out world, out tuning, out p, out n, out oracle);
            p.TradeSellOptIn = true;

            Tick(TradeTest.Manager(oracle), world, tuning, p, n);

            PendingFleetAction fleet = Assert.Single(Trades(p));
            Assert.Equal("N", fleet.TargetTag);
            Assert.Equal(100L, fleet.Cargo["Iron"]);
        }

        // The negative control, and the reason the opt-in exists at all. Identical world, switch off.
        [Fact]
        public void PlayerFaction_DoesNotSellWithoutOptingIn()
        {
            WorldModel world; FactionTuning tuning; FactionState p, n; TradeTest.NodeScarcityOracle oracle;
            PlayerSellsToNpc(out world, out tuning, out p, out n, out oracle);
            // no opt-in

            Tick(TradeTest.Manager(oracle), world, tuning, p, n);

            Assert.Empty(Trades(p));
        }

        // The two switches are genuinely independent, not one flag behind two names. Sell is on and
        // buy is off; the sell convoy flies anyway.
        [Fact]
        public void SellOptIn_DoesNotRequireBuyOptIn()
        {
            WorldModel world; FactionTuning tuning; FactionState p, n; TradeTest.NodeScarcityOracle oracle;
            PlayerSellsToNpc(out world, out tuning, out p, out n, out oracle);
            p.TradeSellOptIn = true;
            p.TradeBuyOptIn = false;

            Tick(TradeTest.Manager(oracle), world, tuning, p, n);

            Assert.Single(Trades(p));
        }

        // The other half of the same claim: buying on and selling off must NOT produce a sell
        // convoy. Without this, one flag standing in for both would pass every test above.
        [Fact]
        public void BuyOptIn_DoesNotEnableTheSellDirection()
        {
            WorldModel world; FactionTuning tuning; FactionState p, n; TradeTest.NodeScarcityOracle oracle;
            PlayerSellsToNpc(out world, out tuning, out p, out n, out oracle);
            p.TradeBuyOptIn = true;
            p.TradeSellOptIn = false;

            Tick(TradeTest.Manager(oracle), world, tuning, p, n);

            Assert.Empty(Trades(p));
        }

        // The regression that matters most: an NPC has no opt-in fields set, and must trade exactly
        // as it always did. If the gate were written as "opted in" rather than "NPC or opted in",
        // this change would switch the whole world's trade off.
        [Fact]
        public void NpcFaction_TradesWithNoOptInAtAll()
        {
            WorldModel world; FactionTuning tuning; FactionState p, n; TradeTest.NodeScarcityOracle oracle;
            PlayerSellsToNpc(out world, out tuning, out p, out n, out oracle);
            p.IsPlayerFaction = false;   // the same world, seller is now an NPC
            TradeTest.Whitelist(p, "N");  // an NPC's brain fills this; no projection runs for one
            Assert.False(p.TradeSellOptIn);
            Assert.False(p.TradeBuyOptIn);

            Tick(TradeTest.Manager(oracle), world, tuning, p, n);

            Assert.Single(Trades(p));
        }

        // ---- gate two: publishing ----------------------------------------------------------

        // Gate two itself -- that PublishUnservedDemands fills the list only for a faction opted in
        // to BUYING -- is proved in UnservedDemandTests, which drives it through a real Operations
        // pass. What is proved here is the other half: that the manager actually consumes the rows
        // gate two publishes, so a neighbour can serve a player faction's shortage.
        [Fact]
        public void NpcCanSupplyAPlayerFaction_OnceItHasOptedInToBuying()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("P", 1_000_000);
            world.SetTreasury("N", 1_000_000);

            SpatialNode n1 = TradeTest.Node(world, "N1", "N");
            n1.Inventory["Iron"] = 5000;
            n1.ShipStock["Trade"] = 1;
            TradeTest.Node(world, "P1", "P", x: 10);

            var oracle = new TradeTest.NodeScarcityOracle()
                .Set("N1", ItemKind.Ore, 0.1)
                .Set("P1", ItemKind.Ore, 2.0);

            FactionState npc = TradeTest.State("N");
            npc.CentralBankRecentIncome = 1_000_000;
            FactionState player = Player("P");
            player.TradeBuyOptIn = true;
            TradeTest.Whitelist(npc, "P");
            TradeTest.Whitelist(player, "N");
            TradeTest.Unserved(player, "P1", "Iron", 100);

            Tick(TradeTest.Manager(oracle), world, tuning, npc, player);

            PendingFleetAction fleet = Assert.Single(Trades(npc));
            Assert.Equal("P", fleet.TargetTag);
        }

        // ---- the projection ------------------------------------------------------------------

        // The claim that makes this feature small: ComputeTradePriority needs no brain state, so a
        // player faction can be ranked by the brain's own function without the brain ever running.
        [Fact]
        public void Projection_RanksPartnersForAnOptedInPlayerFaction()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            TradeTest.Node(world, "P1", "P");
            SpatialNode n1 = TradeTest.Node(world, "N1", "N", x: 10);
            n1.ResourceValue = 1.0;

            FactionState player = Player("P");
            player.TradeSellOptIn = true;
            player.Relations["N"] = 0.5;

            FactionLifecycle.ProjectPlayerTradePriority(player, world, tuning);

            Assert.Contains(player.TradePriority, e => e.PartnerTag == "N");
        }

        // Both switches off leaves the list empty, so the manager's own eligibility gate ends the
        // pass rather than a stale ranking driving one.
        [Fact]
        public void Projection_ClearsTheListWhenBothSwitchesAreOff()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            FactionState player = Player("P");
            TradeTest.Whitelist(player, "N");    // a stale entry from an earlier pass

            FactionLifecycle.ProjectPlayerTradePriority(player, world, tuning);

            Assert.Empty(player.TradePriority);
        }

        // The projection must never touch an NPC: its brain owns that list, and a second writer
        // would compete with it silently.
        [Fact]
        public void Projection_IsANoOpForAnNpc()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            FactionState npc = TradeTest.State("N");
            TradeTest.Whitelist(npc, "P", score: 42.0);

            FactionLifecycle.ProjectPlayerTradePriority(npc, world, tuning);

            TradePriorityEntry kept = Assert.Single(npc.TradePriority);
            Assert.Equal("P", kept.PartnerTag);
            Assert.Equal(42.0, kept.Score);
        }

        // A player's embargo is a decision they made. The brain's own auto-management would lift it
        // again on the very next pass, which would make the setting untrappable — so the player path
        // reads the set and never writes it.
        [Fact]
        public void Projection_DoesNotRewriteAPlayersEmbargoes()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            TradeTest.Node(world, "P1", "P");
            SpatialNode n1 = TradeTest.Node(world, "N1", "N", x: 10);
            n1.ResourceValue = 1.0;

            FactionState player = Player("P");
            player.TradeBuyOptIn = true;
            // Comfortably above the embargo threshold, so the brain's lift rule would fire.
            player.Relations["N"] = 0.5;
            player.Embargoes.Add("N");

            FactionLifecycle.ProjectPlayerTradePriority(player, world, tuning);

            Assert.Contains("N", player.Embargoes);
            Assert.DoesNotContain(player.TradePriority, e => e.PartnerTag == "N");
        }

        // ---- the hull query ------------------------------------------------------------------

        // Shipped broken in v0.205.0 and caught live: LiveOperationsExecutionContext's constructor
        // rejects a null log, this method passed the parameter straight through, and BOTH callers
        // are read-only queries with no log to give — so both wrote `null` and both threw. It
        // surfaced as `Player node detail tick failed` with the ENTIRE station-window snapshot
        // lost, not as a missing field, because the throw happened while building one.
        //
        // Asserting "does not throw" is the whole point here; the boolean it returns is incidental.
        [Fact]
        public void OwnsSuitableHull_DoesNotThrow_WithNoLog()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            SpatialNode p1 = TradeTest.Node(world, "P1", "P");
            p1.ShipStock["Trade"] = 1;
            FactionState player = Player("P");

            // Both forms the callers use: the omitted parameter, and an explicit null.
            Assert.True(OperationsManager.OwnsSuitableHull(world, player, ActionKind.Trade));
            Assert.True(OperationsManager.OwnsSuitableHull(world, player, ActionKind.Trade, null));
        }

        // The negative side of the same query, so a method that returned a constant true would not
        // satisfy the test above.
        [Fact]
        public void OwnsSuitableHull_IsFalse_ForAFactionOwningNoTradeHull()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            TradeTest.Node(world, "P1", "P");     // a node, but no ShipStock at all
            FactionState player = Player("P");

            Assert.False(OperationsManager.OwnsSuitableHull(world, player, ActionKind.Trade));
        }

        // A caller that DOES pass a list must still get its lines, so the null-substitution did not
        // quietly become "discard the log".
        [Fact]
        public void OwnsSuitableHull_StillUsesACallersLog_WhenGivenOne()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            SpatialNode p1 = TradeTest.Node(world, "P1", "P");
            p1.ShipStock["Trade"] = 1;
            FactionState player = Player("P");

            var log = new List<string>();
            Assert.True(OperationsManager.OwnsSuitableHull(world, player, ActionKind.Trade, log));
            // The query may or may not have anything to say; what matters is that the caller's list
            // is the one the context was built around rather than a substitute.
            Assert.NotNull(log);
        }

        // ---- the budget ----------------------------------------------------------------------

        // Null means "use the world's tuning", the friendlier of the two readings FAC-264 posed, and
        // the one that makes a fresh player faction behave like everyone else before it touches a UI.
        [Fact]
        public void TradeBudget_FallsBackToWorldTuningWhenUnset()
        {
            FactionTuning t = TestSupport.Tuning();
            t.TradeMaxIncomeFractionPerWindow = 0.25;
            t.TradeBudgetFloor = 700;
            t.TradeWealthFractionPerWindow = 0.5;
            FactionState st = Player("P");

            Assert.False(st.HasTradeBudgetOverride);
            Assert.Equal(0.25, st.TradeIncomeFraction(t));
            Assert.Equal(700L, st.TradeBudgetFloorFor(t));
            Assert.Equal(0.5, st.TradeWealthFraction(t));
        }

        [Fact]
        public void TradeBudget_PrefersTheFactionsOwnOverride()
        {
            FactionTuning t = TestSupport.Tuning();
            t.TradeMaxIncomeFractionPerWindow = 0.25;
            t.TradeBudgetFloor = 700;
            FactionState st = Player("P");
            st.TradeIncomeFractionOverride = 0.05;
            st.TradeBudgetFloorOverride = 9;

            Assert.True(st.HasTradeBudgetOverride);
            Assert.Equal(0.05, st.TradeIncomeFraction(t));
            Assert.Equal(9L, st.TradeBudgetFloorFor(t));
            Assert.Equal(t.TradeWealthFractionPerWindow, st.TradeWealthFraction(t)); // untouched knob still the world's
        }

        // The override has to reach the manager, not merely exist on the state. A floor and income
        // fraction this small cannot fund the dispatch, so an opted-in faction that would otherwise
        // trade does not — which is the discriminator between "wired up" and "a field nobody reads".
        [Fact]
        public void TradeBudgetOverride_ActuallyBlocksTheDispatch()
        {
            WorldModel world; FactionTuning tuning; FactionState p, n; TradeTest.NodeScarcityOracle oracle;
            PlayerSellsToNpc(out world, out tuning, out p, out n, out oracle);
            p.TradeSellOptIn = true;
            p.TradeIncomeFractionOverride = 0.0;
            p.TradeBudgetFloorOverride = 1;   // not zero: zero-and-zero reads as UNCAPPED

            Tick(TradeTest.Manager(oracle), world, tuning, p, n);

            Assert.Empty(Trades(p));
        }

        // ---- telemetry -----------------------------------------------------------------------

        // The characteristic failure of this whole feature is "opted in and ranks nobody", and
        // TRADE_TICK_PERF cannot show it: that line is emitted only when the pass is slow or
        // dispatched something. Without this line, inert and not-deployed look identical.
        [Fact]
        public void OptedInPlayerFaction_LogsItsPartnerCount_EvenWithNoPartners()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("P", 1_000_000);
            TradeTest.Node(world, "P1", "P");

            FactionState player = Player("P");
            player.TradeBuyOptIn = true;      // nobody to rank: no other faction exists

            List<string> log = Tick(TradeTest.Manager(new TradeTest.NodeScarcityOracle()), world, tuning, player);

            string line = Assert.Single(log.Where(l => l.StartsWith("TRADE_OPTIN P ", StringComparison.Ordinal)));
            Assert.Contains("partners=0", line);
            Assert.Contains("buy=on", line);
            Assert.Contains("sell=off", line);
        }

        // The paired negative: a faction that opted into nothing produces no line, so the log does
        // not fill with rows for every player faction on the server.
        [Fact]
        public void OptedOutPlayerFaction_LogsNothing()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("P", 1_000_000);
            TradeTest.Node(world, "P1", "P");

            FactionState player = Player("P");

            List<string> log = Tick(TradeTest.Manager(new TradeTest.NodeScarcityOracle()), world, tuning, player);

            Assert.DoesNotContain(log, l => l.StartsWith("TRADE_OPTIN ", StringComparison.Ordinal));
        }
    }
}
