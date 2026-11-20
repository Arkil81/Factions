using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class NexusFleetsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        private const byte Day = 4;

        private static NexusFleetOrder SpawnOrder(string key)
        {
            return new NexusFleetOrder { Key = key, Kind = (int)ActionKind.Trade, Tag = "DEF", Prefab = "Hauler", X = 1, Tx = 5 };
        }

        private static NexusFleetGrid GridRow(string key, long eid, double x = 1)
        {
            return new NexusFleetGrid { Key = key, Eid = eid, X = x, Speed = 3, Owner = "DEF" };
        }

        private static NexusFleetRegistry Flying(DateTime now)
        {
            var r = new NexusFleetRegistry();
            r.Apply(Day, "s1", true, null, null, null, 0, now);
            return r;
        }

        [Fact]
        public void ASpawn_AnswersOnlyOnce_WhenTheAgentReportsItsGrid()
        {
            NexusFleetRegistry r = Flying(T0);
            Assert.True(r.CanFly(Day, T0));
            r.Spawn(Day, SpawnOrder("k1"), T0);
            r.Spawn(Day, SpawnOrder("k1"), T0);   // the observation loop asks every frame

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0, out orders, out asks, out held);
            Assert.Single(orders);
            Assert.Equal(NexusFleetOp.Spawn, orders[0].Op);
            Assert.Null(held);

            long eid;
            Assert.False(r.TryTakeSpawn("k1", out eid));
            List<string> done = r.Apply(Day, "s1", true, new[] { GridRow("k1", 900) }, null, null, orders[0].Seq, T0.AddSeconds(1));
            Assert.Equal(new[] { "k1" }, done);
            Assert.True(r.TryTakeSpawn("k1", out eid));
            Assert.Equal(900, eid);
            Assert.False(r.TryTakeSpawn("k1", out eid));

            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            Assert.Null(orders);                    // acked
            Assert.Equal(new long[] { 900 }, held);
            Assert.Equal("Hauler", r.PrefabOf(900));
        }

        [Fact]
        public void AGridMissingFromANewerReport_IsGone_ShotDownOnlyIfTheAgentDidNotCloseIt()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("a"), T0);
            r.Spawn(Day, SpawnOrder("b"), T0);
            r.Apply(Day, "s1", true, new[] { GridRow("a", 1), GridRow("b", 2) }, null, null, 0, T0);
            r.Apply(Day, "s1", true, new NexusFleetGrid[0], null, new long[] { 2 }, 0, T0.AddSeconds(1));

            Assert.False(r.Exists(1));
            Assert.False(r.ClosedByUs(1));
            Assert.False(r.Exists(2));
            Assert.True(r.ClosedByUs(2));
        }

        [Fact]
        public void ADespawn_IsGoneAndClosedByUsAtOnce_AndIsOrdered()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("a"), T0);
            r.Apply(Day, "s1", true, new[] { GridRow("a", 1) }, null, null, long.MaxValue, T0);
            Assert.Equal(1, r.Despawn(1, T0));
            Assert.False(r.Exists(1));
            Assert.True(r.ClosedByUs(1));
            Assert.Equal(0, r.Despawn(1, T0));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0, out orders, out asks, out held);
            Assert.Equal(NexusFleetOp.Despawn, orders[0].Op);
            Assert.Null(held);
            // The agent's next report still carries it: it is not resurrected.
            r.Apply(Day, "s1", true, new[] { GridRow("a", 1) }, null, null, 0, T0.AddSeconds(1));
            Assert.False(r.Exists(1));
        }

        [Fact]
        public void AnAgentRestart_LosesItsGridsAsClosedByUs_AndFailsItsSpawns()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("a"), T0);
            r.Apply(Day, "s1", true, new[] { GridRow("a", 1) }, null, null, 0, T0);
            r.Spawn(Day, SpawnOrder("b"), T0);

            List<string> done = r.Apply(Day, "s2", true, null, null, null, 0, T0.AddSeconds(1));
            Assert.Contains("b", done);
            Assert.False(r.Exists(1));
            Assert.True(r.ClosedByUs(1));
            long eid;
            Assert.True(r.TryTakeSpawn("b", out eid));
            Assert.Equal(0, eid);
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            Assert.Null(orders);
        }

        [Fact]
        public void ASilentAgent_IsWrittenOff_AndNoLongerFlies()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("a"), T0);
            r.Apply(Day, "s1", true, new[] { GridRow("a", 1) }, null, null, 0, T0);
            DateTime later = T0 + NexusFleetRegistry.MaxSilence + TimeSpan.FromSeconds(1);
            Assert.False(r.CanFly(Day, later));
            r.Expire(later);
            Assert.True(r.ClosedByUs(1));
            Assert.Empty(r.Expire(later));   // once
        }

        [Fact]
        public void Asks_AreAnsweredFromTheReport_AndStopBeingSentWhenNotRepeated()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("a"), T0);
            r.Apply(Day, "s1", true, new[] { GridRow("a", 1) }, null, null, 0, T0);
            Assert.False(r.AskLaunch(1, new Vec3(5, 0, 0), true, T0));    // reported not launching
            Assert.False(r.AskApproach(1, new Vec3(5, 0, 0), T0));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            Assert.True(asks[0].Launch && asks[0].Through && asks[0].Approach);
            Assert.Equal(5, asks[0].Lx);

            var row = GridRow("a", 1); row.Launching = true; row.Approaching = true;
            r.Apply(Day, "s1", true, new[] { row }, null, null, 0, T0.AddSeconds(1));
            Assert.True(r.AskLaunch(1, new Vec3(5, 0, 0), false, T0.AddSeconds(1)));
            Assert.True(r.AskApproach(1, new Vec3(5, 0, 0), T0.AddSeconds(1)));

            r.FillReply(Day, T0.AddSeconds(1) + NexusFleetRegistry.AskFresh + TimeSpan.FromSeconds(1), out orders, out asks, out held);
            Assert.Null(asks);
        }

        [Fact]
        public void Latches_FireOncePerAgentCount()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("a"), T0);
            var row = GridRow("a", 1); row.ThrustLost = 1;
            r.Apply(Day, "s1", true, new[] { row }, null, null, 0, T0);
            Assert.True(r.ConsumeThrustLost(1));
            Assert.False(r.ConsumeThrustLost(1));
            Assert.False(r.ConsumeLaunchGiveUp(1));
            var again = GridRow("a", 1); again.ThrustLost = 1; again.GaveUp = 1;
            r.Apply(Day, "s1", true, new[] { again }, null, null, 0, T0.AddSeconds(1));
            Assert.False(r.ConsumeThrustLost(1));
            Assert.True(r.ConsumeLaunchGiveUp(1));
        }

        [Fact]
        public void AnUnansweredSpawn_IsGivenUp_SoTheKeyCanSpawnAgain()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("a"), T0);
            DateTime late = T0 + NexusFleetRegistry.SpawnTimeout + TimeSpan.FromSeconds(1);
            r.Apply(Day, "s1", true, null, null, null, 0, late);   // still reporting, never answering
            Assert.Contains("a", r.Expire(late));
            long eid;
            Assert.True(r.TryTakeSpawn("a", out eid));
            Assert.Equal(0, eid);
            Assert.False(r.IsSpawning("a"));
            r.Spawn(Day, SpawnOrder("a"), late);
            Assert.True(r.IsSpawning("a"));
        }

        [Fact]
        public void AnEscortAsk_RidesTheReply_UntilStoppedOrStale()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("f#1"), T0);
            r.Apply(Day, "s1", true, new[] { GridRow("f#1", 7) }, null, null, 0, T0);
            Assert.False(r.AskEscort(7, 6, 2, new Vec3(9, 0, 0), T0));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0, out orders, out asks, out held);
            Assert.True(asks[0].Escort);
            Assert.Equal(6, asks[0].Anchor);
            Assert.Equal(2, asks[0].Slot);
            Assert.Equal(9, asks[0].Ex);
            Assert.False(asks[0].Launch || asks[0].Approach);

            var row = GridRow("f#1", 7); row.Escorting = true;
            r.Apply(Day, "s1", true, new[] { row }, null, null, 0, T0.AddSeconds(1));
            Assert.True(r.AskEscort(7, 6, 2, new Vec3(9, 0, 0), T0.AddSeconds(1)));

            r.StopEscort(7);
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            Assert.Null(asks);
            r.AskEscort(7, 6, 2, new Vec3(9, 0, 0), T0.AddSeconds(1));
            r.FillReply(Day, T0.AddSeconds(1) + NexusFleetRegistry.AskFresh + TimeSpan.FromSeconds(1), out orders, out asks, out held);
            Assert.Null(asks);
        }

        [Fact]
        public void SquadronHulls_CountAsSpawningUnderTheirFleetKey_AndOnlyThat()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Spawn(Day, SpawnOrder("raid:7#1"), T0);
            Assert.True(r.IsSpawningUnder("raid:7"));
            Assert.False(r.IsSpawningUnder("raid:"));
            Assert.False(r.IsSpawningUnder("raid:70"));
            Assert.False(r.IsSpawningUnder("raid:7#1x"));
            r.Spawn(Day, SpawnOrder("trade:3"), T0);
            Assert.True(r.IsSpawningUnder("trade:3"));
        }

        [Fact]
        public void AnUnknownGrid_IsNotAdopted()
        {
            NexusFleetRegistry r = Flying(T0);
            r.Apply(Day, "s1", true, new[] { GridRow("stray", 77) }, null, null, 0, T0);
            Assert.False(r.Owns(77));
        }

        [Fact]
        public void Orders_SurviveAnAuthorityRestart_InSequence()
        {
            var first = new NexusFleetRegistry();
            first.Apply(Day, "s1", true, null, null, null, 0, T0);
            first.Spawn(Day, SpawnOrder("a"), T0);
            List<NexusFleetOrder> o1; List<NexusFleetAsk> asks; List<long> held;
            first.FillReply(Day, T0, out o1, out asks, out held);

            var inbox = new NexusOrderInbox();
            Assert.Single(inbox.Accept(o1));
            Assert.Empty(inbox.Accept(o1));         // a re-send applies nothing

            var restarted = new NexusFleetRegistry();
            restarted.Apply(Day, "s1", true, null, null, null, inbox.Through, T0.AddSeconds(30));
            restarted.Spawn(Day, SpawnOrder("b"), T0.AddSeconds(30));
            List<NexusFleetOrder> o2;
            restarted.FillReply(Day, T0.AddSeconds(30), out o2, out asks, out held);
            List<NexusFleetOrder> applied = inbox.Accept(o2);
            Assert.Equal("b", applied[0].Key);
        }

        private static NexusFleetRegistry WithGrid(DateTime now, long eid = 900)
        {
            NexusFleetRegistry r = Flying(now);
            r.Spawn(Day, SpawnOrder("k1"), now);
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, now, out orders, out asks, out held);
            r.Apply(Day, "s1", true, new[] { GridRow("k1", eid) }, null, null, orders[0].Seq, now);
            long taken;
            r.TryTakeSpawn("k1", out taken);
            return r;
        }

        private static NexusAgentReport Report(DateTime clock, params NexusFleetGrid[] grids)
        {
            return new NexusAgentReport
            {
                Session = "s1", Fleets = true, ClockUtc = clock, OrdersThrough = long.MaxValue,
                Grids = new List<NexusFleetGrid>(grids),
            };
        }

        [Fact]
        public void AFightAsk_CarriesTheOpponentsAndBands_AndLapsesIntoStopWhenNotRepeated()
        {
            NexusFleetRegistry r = WithGrid(T0);
            r.BandsOf = eid => eid == 900 ? new[] { new WeaponBand { Dps = 50, RangeM = 800 } } : null;
            r.OpponentsOf = (label, grids) => { grids.Add(777); return "RIV squadron"; };
            var own = new[] { new WeaponBand { Dps = 40, RangeM = 600 } };
            Assert.False(r.AskFight(900, new Vec3(1, 2, 3), own, "DEF guard", true, TargetScope.PreferOpponents, null, null, 777, T0));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held; List<NexusFleetBands> bands;
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held, out bands);
            NexusFleetAsk a = Assert.Single(asks);
            Assert.Equal(NexusCombat.Fight, a.Combat);
            Assert.Equal(2, a.Hy);
            Assert.Equal("RIV squadron", a.OpponentsLabel);
            Assert.Equal(new long[] { 777 }, a.Opponents);
            Assert.Equal(777, a.Pursue);
            Assert.Equal(600, a.FightBands[0].RangeM);
            Assert.Equal(800, Assert.Single(bands).Bands[0].RangeM);

            // The agent took it: the next frame's ask answers true.
            NexusFleetGrid row = GridRow("k1", 900);
            row.Fighting = true;
            r.Apply(Day, Report(T0.AddSeconds(2), row), T0.AddSeconds(2));
            Assert.True(r.AskFight(900, new Vec3(1, 2, 3), own, "DEF guard", true, TargetScope.PreferOpponents, null, null, 777, T0.AddSeconds(2)));

            // Not asked again: the reply stops carrying it, and the agent stops fighting.
            r.FillReply(Day, T0.AddSeconds(2) + NexusFleetRegistry.AskFresh + TimeSpan.FromSeconds(1), out orders, out asks, out held, out bands);
            Assert.Null(asks);
            r.AskShadow(900, new Vec3(0, 0, 0), 777, 1500, 4000, "DEF guard", T0.AddSeconds(10));
            r.StopFighting(900);
            r.FillReply(Day, T0.AddSeconds(10), out orders, out asks, out held, out bands);
            Assert.Null(asks);
        }

        [Fact]
        public void AHoldAsk_IsSentWhileAsked_AndClearedByAnAskWithout()
        {
            NexusFleetRegistry r = WithGrid(T0);
            r.AskHold(900, true, T0);
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0, out orders, out asks, out held);
            Assert.True(Assert.Single(asks).Hold);
            r.AskHold(900, false, T0.AddSeconds(1));
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            Assert.Null(asks);
        }

        [Fact]
        public void AHostileHit_IsShiftedByTheAgentClock_AndItsAttackerIsAnsweredForAWhile()
        {
            NexusFleetRegistry r = WithGrid(T0);
            // The agent's clock runs 30 s behind ours.
            DateTime agentNow = T0.AddSeconds(5);
            DateTime ourNow = T0.AddSeconds(35);
            NexusFleetGrid row = GridRow("k1", 900);
            row.Hit = agentNow.AddSeconds(-2);
            row.HitBy = 4242; row.HitByOwner = 77; row.HitByTag = "PLR";
            row.HitByPlaced = true; row.HitByX = 9; row.HitByY = 8; row.HitByZ = 7;
            r.Apply(Day, Report(agentNow, row), ourNow);

            long by;
            Assert.Equal(ourNow.AddSeconds(-2), r.LastHostileHit(900, out by));
            Assert.Equal(4242, by);
            Assert.NotNull(r.RecentHostileHit(900, ourNow, TimeSpan.FromSeconds(20), out by));
            Assert.Null(r.RecentHostileHit(900, ourNow.AddSeconds(30), TimeSpan.FromSeconds(20), out by));
            Assert.Equal(0, by);
            Assert.Equal(new Vec3(9, 8, 7), r.ContactPositionKm(4242));
            Assert.Equal(77, r.ContactOwner(4242));
            Assert.Equal("PLR", r.ContactTag(4242));

            r.Expire(ourNow + NexusFleetRegistry.ContactMemory + TimeSpan.FromSeconds(1));
            Assert.Null(r.ContactPositionKm(4242));
        }

        [Fact]
        public void FightDamage_IsTakenAsTheDeltaOfTheAgentsTotals()
        {
            NexusFleetRegistry r = WithGrid(T0);
            NexusFleetGrid row = GridRow("k1", 900);
            row.Dealt = 100; row.Taken = 40;
            r.Apply(Day, Report(T0.AddSeconds(1), row), T0.AddSeconds(1));
            double dealt, taken;
            r.TakeFightDamage(900, out dealt, out taken);
            Assert.Equal(100, dealt); Assert.Equal(40, taken);
            r.TakeFightDamage(900, out dealt, out taken);
            Assert.Equal(0, dealt); Assert.Equal(0, taken);
            row = GridRow("k1", 900);
            row.Dealt = 130; row.Taken = 40;
            r.Apply(Day, Report(T0.AddSeconds(2), row), T0.AddSeconds(2));
            r.TakeFightDamage(900, out dealt, out taken);
            Assert.Equal(30, dealt); Assert.Equal(0, taken);
        }

        [Fact]
        public void CombatReads_ComeFromTheLatestRow_AndNotFromAGoneGrid()
        {
            NexusFleetRegistry r = WithGrid(T0);
            Assert.Null(r.ReadIntegrity(900));
            Assert.Null(r.CombatManoeuvre(900));
            NexusFleetGrid row = GridRow("k1", 900);
            row.Integrity = 0.4; row.OutOfAction = true; row.Controlled = true; row.Manoeuvre = (int)ManoeuvreKind.Orbit;
            r.Apply(Day, Report(T0.AddSeconds(1), row), T0.AddSeconds(1));
            Assert.Equal(0.4, r.ReadIntegrity(900));
            Assert.True(r.IsOutOfAction(900));
            Assert.True(r.IsControlled(900));
            Assert.Equal(ManoeuvreKind.Orbit, r.CombatManoeuvre(900));
            r.Apply(Day, Report(T0.AddSeconds(2)), T0.AddSeconds(2));
            Assert.Null(r.ReadIntegrity(900));
            Assert.False(r.IsOutOfAction(900));
        }

        [Fact]
        public void IntegrityAndWithhold_AreOrders_OnlyForALiveGrid()
        {
            NexusFleetRegistry r = WithGrid(T0);
            Assert.Equal(0, r.WatchIntegrity(900, 0.5, T0));
            Assert.Equal(1, r.Withhold(900, T0));
            r.ForgetWithheld(900, T0);
            Assert.Equal(-1, r.WatchIntegrity(1234, 0.5, T0));
            Assert.Equal(0, r.Withhold(1234, T0));
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0, out orders, out asks, out held);
            Assert.Equal(new[] { NexusFleetOp.Integrity, NexusFleetOp.Withhold, NexusFleetOp.Unwithhold },
                         orders.ConvertAll(o => o.Op).ToArray());
            Assert.Equal(0.5, orders[0].Tx);
        }

        [Fact]
        public void Munitions_AreTakenOnceFromTheAgentsTotals_AndOutOnlyWhileLive()
        {
            NexusFleetRegistry r = WithGrid(T0);
            NexusAgentReport rep = Report(T0.AddSeconds(1), GridRow("k1", 900));
            rep.Munitions = new List<NexusMunitionsTotal>
            {
                new NexusMunitionsTotal { Id = "m1", Live = true, Out = true, Closed = new Dictionary<string, long> { { "Ammo:A", 5 } } },
            };
            r.Apply(Day, rep, T0.AddSeconds(1));
            Assert.True(r.IsOutOfMunitions("m1"));
            Dictionary<string, long> seen;
            Assert.True(r.TryTakeObservedMunitions("m1", out seen));
            Assert.Equal(5, seen["Ammo:A"]);
            Assert.False(r.TryTakeObservedMunitions("m1", out seen));

            rep = Report(T0.AddSeconds(2), GridRow("k1", 900));
            rep.Munitions = new List<NexusMunitionsTotal>
            {
                new NexusMunitionsTotal { Id = "m1", Live = false, Closed = new Dictionary<string, long> { { "Ammo:A", 8 } } },
            };
            r.Apply(Day, rep, T0.AddSeconds(2));
            Assert.False(r.IsOutOfMunitions("m1"));
            Assert.True(r.TryTakeObservedMunitions("m1", out seen));
            Assert.Equal(3, seen["Ammo:A"]);

            r.RetainMunitions(new List<string>());
            Assert.False(r.TryTakeObservedMunitions("m1", out seen));
        }

        [Fact]
        public void ALeave_NeedsAControlledGrid_AndIsNotDoneUntilTheAgentHasAppliedItAndCleared()
        {
            NexusFleetRegistry r = WithGrid(T0);
            Assert.False(r.Leave(900, new Vec3(0, 0, 0), 3000, "repelled", T0));   // not under the controller

            NexusFleetGrid row = GridRow("k1", 900);
            row.Controlled = true;
            NexusAgentReport rep = Report(T0.AddSeconds(1), row);
            rep.OrdersThrough = 0;
            r.Apply(Day, rep, T0.AddSeconds(1));
            r.AskFight(900, new Vec3(1, 1, 1), null, "DEF guard", false, TargetScope.PreferOpponents, null, null, 0, T0.AddSeconds(1));
            Assert.True(r.Leave(900, new Vec3(4, 5, 6), 3000, "beaten", T0.AddSeconds(1)));
            Assert.False(r.IsLeaveDone(900));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            NexusFleetOrder leave = Assert.Single(orders);
            Assert.Equal(NexusFleetOp.Leave, leave.Op);
            Assert.Equal(3000, leave.ClearM);
            Assert.Equal("beaten", leave.Reason);
            Assert.Equal(5, leave.Ty);
            Assert.Null(asks);                       // the leave ends the fight ask

            // A report from before the agent applied it: still leaving, whatever the row says.
            row = GridRow("k1", 900); row.Controlled = true;
            rep = Report(T0.AddSeconds(2), row); rep.OrdersThrough = leave.Seq - 1;
            r.Apply(Day, rep, T0.AddSeconds(2));
            Assert.False(r.IsLeaveDone(900));

            row = GridRow("k1", 900); row.Controlled = true; row.Leaving = true;
            rep = Report(T0.AddSeconds(3), row); rep.OrdersThrough = leave.Seq;
            r.Apply(Day, rep, T0.AddSeconds(3));
            Assert.False(r.IsLeaveDone(900));

            row = GridRow("k1", 900); row.Controlled = true;
            rep = Report(T0.AddSeconds(4), row); rep.OrdersThrough = leave.Seq;
            r.Apply(Day, rep, T0.AddSeconds(4));
            Assert.True(r.IsLeaveDone(900));
            Assert.True(r.IsLeaveDone(1234));         // unknown, as a missing local grid
        }

        [Fact]
        public void AWingAsk_RidesTheReplyWhileAsked_AndEndWingIsAnOrderThatDropsIt()
        {
            NexusFleetRegistry r = WithGrid(T0);
            var order = new TacticOrder { TargetEntityId = 55, AnchorEntityId = 66 };
            Assert.False(r.AskWing(900, 66, 55, true, false, order, 2, "DEF raid", new[] { new WeaponBand { Dps = 9, RangeM = 700 } }, T0));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0, out orders, out asks, out held);
            NexusFleetAsk a = Assert.Single(asks);
            Assert.True(a.Wing);
            Assert.Equal(66, a.WingAnchor); Assert.Equal(55, a.WingTarget);
            Assert.True(a.WingHolding); Assert.Equal(2, a.WingSlot); Assert.Equal("DEF raid", a.WingLabel);
            Assert.Equal(55, a.WingOrder.TargetEntityId);
            Assert.Equal(700, a.WingBands[0].RangeM);

            NexusFleetGrid row = GridRow("k1", 900);
            row.Winged = true;
            r.Apply(Day, Report(T0.AddSeconds(1), row), T0.AddSeconds(1));
            Assert.True(r.AskWing(900, 66, 55, true, false, order, 2, "DEF raid", null, T0.AddSeconds(1)));

            r.EndWing(900, new Vec3(7, 8, 9), T0.AddSeconds(1));
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            Assert.Null(asks);
            NexusFleetOrder end = Assert.Single(orders);
            Assert.Equal(NexusFleetOp.EndWing, end.Op);
            Assert.Equal(9, end.Tz);
        }

        [Fact]
        public void AGridAPlayerTook_IsLetGoOnRelease_SoTheAgentKeepsItUnheld()
        {
            NexusFleetRegistry r = WithGrid(T0);
            r.Release(900, T0);                       // still the fleet's: kept
            Assert.True(r.Owns(900));

            NexusFleetGrid row = GridRow("k1", 900);
            row.Owner = "PLR";
            r.Apply(Day, Report(T0.AddSeconds(1), row), T0.AddSeconds(1));
            Assert.Equal("PLR", r.OwnerTag(900));    // the runtime reads the capture from this
            r.Release(900, T0.AddSeconds(1));
            Assert.False(r.Owns(900));

            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Day, T0.AddSeconds(1), out orders, out asks, out held);
            Assert.Null(held);
            Assert.Equal(NexusFleetOp.Release, orders[orders.Count - 1].Op);

            // Still in the agent's next report: not adopted again.
            r.Apply(Day, Report(T0.AddSeconds(2), row), T0.AddSeconds(2));
            Assert.False(r.Owns(900));
        }

        [Fact]
        public void AWreckAPlayerTook_IsLetGoWhenForgotten()
        {
            NexusFleetRegistry r = WithGrid(T0);
            r.Withhold(900, T0);
            NexusFleetGrid row = GridRow("k1", 900);
            row.Owner = "PLR";
            r.Apply(Day, Report(T0.AddSeconds(1), row), T0.AddSeconds(1));
            r.ForgetWithheld(900, T0.AddSeconds(1));
            Assert.False(r.Owns(900));
        }

        [Fact]
        public void TheFleetFields_SurviveTheCodec()
        {
            var report = new NexusAgentReport
            {
                Session = "s1", Fleets = true, OrdersThrough = 9,
                Grids = new List<NexusFleetGrid> { new NexusFleetGrid { Key = "k", Eid = 5, X = 1.5, ThrustLost = 2 } },
                SpawnFailed = new List<string> { "f" }, Closed = new List<long> { 4 },
                ClockUtc = T0,
                Munitions = new List<NexusMunitionsTotal> { new NexusMunitionsTotal { Id = "m", Live = true, Out = true } },
            };
            report.Grids[0].Hit = T0; report.Grids[0].HitBy = 8; report.Grids[0].Integrity = 0.25;
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.True(back.Fleets);
            Assert.Equal(1.5, back.Grids[0].X);
            Assert.Equal(2, back.Grids[0].ThrustLost);
            Assert.Equal("f", back.SpawnFailed[0]);
            Assert.Equal(T0, back.ClockUtc.ToUniversalTime());
            Assert.Equal(T0, back.Grids[0].Hit.ToUniversalTime());
            Assert.Equal(0.25, back.Grids[0].Integrity);
            Assert.Equal(-1, back.Grids[0].Manoeuvre);
            Assert.True(back.Munitions[0].Out);

            var reply = new NexusStationReply
            {
                Orders = new List<NexusFleetOrder> { new NexusFleetOrder { Seq = 3, Op = NexusFleetOp.Retarget, Eid = 5, Tx = 2 } },
                Asks = new List<NexusFleetAsk> { new NexusFleetAsk { Eid = 5, Launch = true, Lx = 1 } },
                Held = new List<long> { 5 },
                Bands = new List<NexusFleetBands> { new NexusFleetBands { Eid = 5, Bands = new List<WeaponBand> { new WeaponBand { Dps = 3, RangeM = 4 } } } },
            };
            reply.Asks[0].Combat = NexusCombat.Fight;
            reply.Asks[0].Tactic = new TacticOrder { TargetEntityId = 11 };
            reply.Asks[0].Scope = TargetScope.PreferOpponents;
            reply.Asks[0].Wing = true;
            reply.Asks[0].WingOrder = new TacticOrder { AnchorEntityId = 12 };
            reply.Orders[0].ClearM = 2500;
            NexusStationReply r;
            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out r));
            Assert.Equal(NexusFleetOp.Retarget, r.Orders[0].Op);
            Assert.True(r.Asks[0].Launch);
            Assert.Equal(5, r.Held[0]);
            Assert.Equal(4, r.Bands[0].Bands[0].RangeM);
            Assert.Equal(NexusCombat.Fight, r.Asks[0].Combat);
            Assert.Equal(11, r.Asks[0].Tactic.Value.TargetEntityId);
            Assert.Null(r.Asks[0].Learned);
            Assert.True(r.Asks[0].Wing);
            Assert.Equal(12, r.Asks[0].WingOrder.AnchorEntityId);
            Assert.Equal(2500, r.Orders[0].ClearM);
        }
    }
}
