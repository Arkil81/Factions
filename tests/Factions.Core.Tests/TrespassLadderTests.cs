using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 3a — the escalation ladder: how long a faction shadows an intruder before it
    /// intercepts, the Guard that holds fire while it shadows, and the Guard that goes home when the
    /// intruder leaves.
    /// </summary>
    public sealed class TrespassLadderTests
    {
        // ---- how long a faction shadows ---------------------------------------------------------------

        [Fact]
        public void ShadowSeconds_BaselineIsThreeMinutes()
        {
            Assert.Equal(180.0, TrespassLadder.ShadowSeconds(0.4, 0.5, -500, false), 6);
        }

        [Fact]
        public void ShadowSeconds_ARaiderIsQuickAndAMerchantPatient()
        {
            Assert.Equal(60.0, TrespassLadder.ShadowSeconds(0.9, 0.5, -500, false), 6);
            double merchant = TrespassLadder.ShadowSeconds(0.2, 0.5, -500, false);
            Assert.InRange(merchant, 270.0, 290.0);
        }

        [Fact]
        public void ShadowSeconds_DepthAndStandingEachHalveItAtMost()
        {
            Assert.Equal(90.0, TrespassLadder.ShadowSeconds(0.4, 1.0, -500, false), 6);
            Assert.Equal(90.0, TrespassLadder.ShadowSeconds(0.4, 0.5, -1500, false), 6);
            Assert.Equal(45.0, TrespassLadder.ShadowSeconds(0.4, 1.0, -1500, false), 6);
            // Below the Enemies band edge and above the extremes, nothing moves further.
            Assert.Equal(180.0, TrespassLadder.ShadowSeconds(0.4, 0.3, -200, false), 6);
            Assert.Equal(90.0, TrespassLadder.ShadowSeconds(0.4, 0.5, -3000, false), 6);
        }

        [Fact]
        public void ShadowSeconds_AFactionThatStartsHostileSkipsShadow()
        {
            Assert.Equal(0.0, TrespassLadder.ShadowSeconds(0.2, 0.5, -500, true));
            Assert.Equal(TrespassRung.Intercept, TrespassLadder.RungFor(0, 0));
        }

        [Fact]
        public void StartsHostile_IsImplacableOrPirate()
        {
            Assert.True(TrespassLadder.StartsHostile(Personalities.Get("factorum"), "None"));
            Assert.True(TrespassLadder.StartsHostile(Personalities.Get("default"), "Pirate"));
            Assert.False(TrespassLadder.StartsHostile(Personalities.Get("default"), "Trader"));
            Assert.False(TrespassLadder.StartsHostile(null, null));
        }

        [Fact]
        public void RungFor_ShadowUntilTheTimeIsUp()
        {
            Assert.Equal(TrespassRung.Shadow, TrespassLadder.RungFor(179.9, 180));
            Assert.Equal(TrespassRung.Intercept, TrespassLadder.RungFor(180, 180));
        }

        // ---- the clock --------------------------------------------------------------------------------

        private static readonly DateTime T0 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Dwell_RunsWhileSeenAndRestartsAfterAGap()
        {
            var book = new TrespassDwellBook();
            Assert.Equal(0.0, book.Observe(1, "STCG", T0));
            Assert.Equal(55.0, book.Observe(1, "STCG", T0.AddSeconds(55)));
            Assert.Equal(110.0, book.Observe(1, "STCG", T0.AddSeconds(110)));
            // Another faction's clock is its own.
            Assert.Equal(0.0, book.Observe(1, "TFCS", T0.AddSeconds(110)));

            DateTime back = T0.AddSeconds(110) + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            Assert.Equal(0.0, book.Observe(1, "STCG", back));
        }

        [Fact]
        public void Dwell_AStepUpIsAnnouncedOncePerStay()
        {
            var book = new TrespassDwellBook();
            book.Observe(1, "STCG", T0);
            Assert.True(book.StepTo(1, "STCG", TrespassRung.Shadow));
            Assert.False(book.StepTo(1, "STCG", TrespassRung.Shadow));
            Assert.True(book.StepTo(1, "STCG", TrespassRung.Intercept));
            Assert.False(book.StepTo(1, "STCG", TrespassRung.Intercept));
            Assert.False(book.StepTo(1, "STCG", TrespassRung.Shadow));

            // A new stay steps up again.
            DateTime back = T0 + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            book.Observe(1, "STCG", back);
            Assert.True(book.StepTo(1, "STCG", TrespassRung.Intercept));

            book.Prune(back + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1));
            Assert.Equal(0, book.Count);
        }

        // ---- the node's rung --------------------------------------------------------------------------

        [Fact]
        public void NodeRung_IsTheHighestSeenWithinTheMemory()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            Assert.Equal(TrespassRung.None, w.IntruderRungAt("N002"));

            w.MarkIntruder("N002", TrespassRung.Shadow);
            Assert.Equal(TrespassRung.Shadow, w.IntruderRungAt("N002"));

            w.UtcNow = w.UtcNow.AddSeconds(5);
            w.MarkIntruder("N002", TrespassRung.Intercept);
            w.UtcNow = w.UtcNow.AddSeconds(5);
            w.MarkIntruder("N002", TrespassRung.Shadow);   // a second, newer pilot
            Assert.Equal(TrespassRung.Intercept, w.IntruderRungAt("N002"));

            // The intercepted pilot gone past the memory, the newer one still shadowed.
            for (int i = 0; i < 13; i++)
            {
                w.UtcNow = w.UtcNow.AddSeconds(5);
                w.MarkIntruder("N002", TrespassRung.Shadow);
            }
            Assert.Equal(TrespassRung.Shadow, w.IntruderRungAt("N002"));
        }

        // ---- the Guard --------------------------------------------------------------------------------

        private static PendingFleetAction DispatchGuardAgainst(WorldModel w, FactionState def, Action mark)
        {
            mark();
            new FleetManager().Tick(new[] { def }, w, StationCallTests.GuardTuning(), new List<string>());
            return Assert.Single(def.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        [Fact]
        public void AGuardSentAtAnIntruderIsMarkedSo_AndOneSentAtACallIsNot()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            Assert.True(DispatchGuardAgainst(w, def, () => w.MarkIntruder("N002")).AgainstIntruder);

            WorldModel w2 = StationCallTests.DefenderWorld();
            var def2 = new FactionState { Tag = "DEF", RngSeed = 1 };
            Assert.False(DispatchGuardAgainst(w2, def2, () => w2.SetStationCall("N002", null)).AgainstIntruder);
        }

        [Fact]
        public void AGuard_HoldsFireOnlyWhileTheNodeIsShadowedAndNothingElseHappens()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            PendingFleetAction g = DispatchGuardAgainst(w, def, () => w.MarkIntruder("N002", TrespassRung.Shadow));
            var states = new List<FactionState> { def };

            Assert.True(TrespassLadder.GuardHoldsFire(g, w, states));

            // Intercept.
            w.MarkIntruder("N002", TrespassRung.Intercept);
            Assert.False(TrespassLadder.GuardHoldsFire(g, w, states));
        }

        [Fact]
        public void AGuard_FightsWhenTheShadowedNodeIsAttackedCalledOrRaided()
        {
            FactionTuning t = StationCallTests.GuardTuning();

            WorldModel shot = StationCallTests.DefenderWorld();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            PendingFleetAction g = DispatchGuardAgainst(shot, def, () => shot.MarkIntruder("N002"));
            Assert.True(TrespassLadder.GuardHoldsFire(g, shot, new[] { def }));
            shot.RegisterPlayerAggression("N002", 10000);
            Assert.False(TrespassLadder.GuardHoldsFire(g, shot, new[] { def }));

            WorldModel called = StationCallTests.DefenderWorld();
            var def2 = new FactionState { Tag = "DEF", RngSeed = 1 };
            PendingFleetAction g2 = DispatchGuardAgainst(called, def2, () => called.MarkIntruder("N002"));
            called.SetStationCall("N002", null);
            Assert.False(TrespassLadder.GuardHoldsFire(g2, called, new[] { def2 }));

            WorldModel raided = StationCallTests.DefenderWorld();
            var def3 = new FactionState { Tag = "DEF", RngSeed = 1 };
            PendingFleetAction g3 = DispatchGuardAgainst(raided, def3, () => raided.MarkIntruder("N002"));
            FactionState rad = StationCallTests.Raider(raided, "N002");
            Assert.True(TrespassLadder.GuardHoldsFire(g3, raided, new[] { def3, rad }));   // still inbound
            raided.UtcNow = raided.UtcNow.AddMinutes(4);
            raided.MarkIntruder("N002");
            Assert.False(TrespassLadder.GuardHoldsFire(g3, raided, new[] { def3, rad }));   // landed
        }

        [Fact]
        public void AnOrdinaryGuard_NeverHoldsFire()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            PendingFleetAction g = DispatchGuardAgainst(w, def, () => w.SetStationCall("N002", null));
            w.ClearStationCall("N002");
            w.MarkIntruder("N002", TrespassRung.Shadow);
            Assert.False(TrespassLadder.GuardHoldsFire(g, w, new[] { def }));
        }

        [Fact]
        public void AGuardSentAtAnIntruder_GoesHomeWhenTheIntruderLeaves_AndAnOrdinaryOneServesItsWindow()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            PendingFleetAction g = DispatchGuardAgainst(w, def, () => w.MarkIntruder("N002"));
            var states = new List<FactionState> { def };

            // Arrive with the intruder still there: the Guard stays.
            w.UtcNow = g.EstimatedArrivalUtc.AddSeconds(1);
            w.MarkIntruder("N002");
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), new List<string>());
            Assert.Single(def.PendingFleets.Where(f => f.Kind == ActionKind.Guard));

            // The intruder gone past the memory: the Guard is released, long before its window ends.
            w.UtcNow = w.UtcNow + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            Assert.True(w.UtcNow < g.EstimatedArrivalUtc.Add(w.Tuning.GuardDuration));
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(3), new List<string>());
            Assert.Empty(def.PendingFleets.Where(f => f.Kind == ActionKind.Guard));

            // Control: a call answered the same way, then cleared, keeps its Guard.
            WorldModel w2 = StationCallTests.DefenderWorld();
            var def2 = new FactionState { Tag = "DEF", RngSeed = 1 };
            PendingFleetAction g2 = DispatchGuardAgainst(w2, def2, () => w2.SetStationCall("N002", null));
            var states2 = new List<FactionState> { def2 };
            w2.UtcNow = g2.EstimatedArrivalUtc.AddSeconds(1);
            PendingFleetResolver.ResolveArrivals(states2, w2, w2.Tuning, new SeededRandom(3), new List<string>());
            w2.ClearStationCall("N002");
            w2.UtcNow = w2.UtcNow + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            PendingFleetResolver.ResolveArrivals(states2, w2, w2.Tuning, new SeededRandom(3), new List<string>());
            Assert.Single(def2.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        [Fact]
        public void AgainstIntruder_SurvivesARestartAndIsOmittedWhenFalse()
        {
            var st = new FactionState { Tag = "DEF" };
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Guard, ActorTag = "DEF", AgainstIntruder = true });
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Guard, ActorTag = "DEF" });

            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(st, new SpatialNode[0], 10));
            FactionState back = FactionStore.ApplyBrainDoc(JsonConvert.DeserializeObject<BrainDoc>(json), new SpatialGraph());

            Assert.True(back.PendingFleets[0].AgainstIntruder);
            Assert.False(back.PendingFleets[1].AgainstIntruder);
            Assert.Equal(1, json.Split(new[] { "AgainstIntruder" }, StringSplitOptions.None).Length - 1);
        }
    }
}
