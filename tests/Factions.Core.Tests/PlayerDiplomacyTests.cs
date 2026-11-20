using System;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Rule 3 of docs/diplomacy-plan.md: a player faction's stance toward an NPC is its leader's.
    /// It does not drift, the NPC's acts do not move it, and it changes only by a leader act or a
    /// concluded truce. The NPC's view of the player keeps its own rules.
    ///
    /// <para>Every "does not move" headline is paired with the same fixture run for an NPC, so a
    /// fixture that moves nothing cannot pass it.</para>
    /// </summary>
    public class PlayerDiplomacyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static Personality Persona()
        {
            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = 0.5, Aggression = 0.5, Mercantilism = 0.5 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1;
            return p;
        }

        /// <summary>NPC "A" at A1 and "EXP" at B1, close enough that border pressure is real.</summary>
        private static WorldModel Bordering()
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
            var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "EXP"; b.HoldStrength = 0.8; b.Radius = 30;
            return w;
        }

        private sealed class FixedStanding : IMemberStandingSource
        {
            private readonly string _playerTag;
            private readonly double _value;
            public FixedStanding(string playerTag, double value) { _playerTag = playerTag; _value = value; }
            public bool TryGetAggregate(string npcFactionTag, string playerFactionTag, out double aggregate)
            {
                aggregate = _value;
                return playerFactionTag == _playerTag;
            }
        }

        private static FactionState Player(string tag = "EXP") { return new FactionState { Tag = tag, IsPlayerFaction = true }; }
        private static FactionState Npc(string tag = "A") { return new FactionState { Tag = tag, PersonalityId = "settler" }; }

        // ---- no drift for the player's own stance -------------------------------------------

        [Fact]
        public void PlayerStance_DoesNotDrift_WhileAnNpcInTheSameSeatDoes()
        {
            var t = TestSupport.Tuning();

            // A stance an earlier build drifted to: kept, not reset.
            var player = Player("A"); player.Relations["EXP"] = 0.37;
            var control = Npc("A"); control.Relations["EXP"] = 0.37;
            for (int i = 0; i < 40; i++)
            {
                Diplomacy.Drift(player, Persona(), Bordering(), t);
                Diplomacy.Drift(control, Persona(), Bordering(), t);
            }

            Assert.Equal(0.37, player.RelationTo("EXP"), 12);
            Assert.NotEqual(0.37, control.RelationTo("EXP"), 3);
        }

        [Fact]
        public void PlayerThatNeverActed_ReadsZero_AfterDrift()
        {
            var t = TestSupport.Tuning();
            var player = Player("A");
            var control = Npc("A");
            for (int i = 0; i < 40; i++)
            {
                Diplomacy.Drift(player, Persona(), Bordering(), t);
                Diplomacy.Drift(control, Persona(), Bordering(), t);
            }
            Assert.False(player.Relations.ContainsKey("EXP"));
            Assert.Equal(0.0, player.RelationTo("EXP"));
            Assert.NotEqual(0.0, control.RelationTo("EXP"), 3);
        }

        /// <summary>The NPC side of the same pair still follows the member aggregate and ledger.</summary>
        [Fact]
        public void NpcViewOfThePlayer_StillDrifts()
        {
            var t = TestSupport.Tuning();
            var npc = Npc("A");
            for (int i = 0; i < 40; i++)
            {
                WorldModel w = Bordering(); w.MemberStanding = new FixedStanding("EXP", -1.0);
                Diplomacy.Drift(npc, Persona(), w, t);
            }
            Assert.True(npc.RelationTo("EXP") < -0.1, "bad member conduct must still sour the NPC's view, got " + npc.RelationTo("EXP"));
        }

        // ---- the NPC's acts do not move the player's stance ----------------------------------

        [Fact]
        public void BeingRaided_RecordsOnTheLedger_ButLeavesTheStance()
        {
            var t = TestSupport.Tuning();
            var player = Player(); var control = Npc("B");
            for (int i = 0; i < 5; i++) { Diplomacy.HitRelation(player, "A", t); Diplomacy.HitRelation(control, "A", t); }

            Assert.Equal(0.0, player.RelationTo("A"));
            Assert.True(player.EarnedLedgerTo("A") < 0, "the raids are still on the record");
            Assert.True(control.RelationTo("A") < 0, "control: an NPC's standing does move");
        }

        [Fact]
        public void TradeGiftAndAllyDefence_DoNotLiftTheStance()
        {
            var t = TestSupport.Tuning();
            var player = Player(); player.Relations["A"] = -1.0;
            var control = Npc("B"); control.Relations["A"] = -1.0;
            foreach (FactionState s in new[] { player, control })
            {
                for (int i = 0; i < 50; i++) Diplomacy.LiftRelation(s, "A", t);
                Diplomacy.GiftRelation(s, "A", t);
                Diplomacy.LiftRelation(s, "A", t.PactDefenceRelationBoost);
            }
            Assert.Equal(-1.0, player.RelationTo("A"));
            Assert.True(control.RelationTo("A") > -1.0);
        }

        // ---- truce: bilateral for a player, concluded truce sets the stance ------------------

        [Fact]
        public void PlayerFatigue_DoesNotAcceptAnNpcPeaceOffer()
        {
            var t = TestSupport.Tuning();
            WorldModel w = Bordering();

            var npc = Npc("A"); var player = Player();
            player.Relations["A"] = -1.0;
            player.Fatigue["A"] = 1.0;
            Diplomacy.OfferPeace(npc, "EXP", w.UtcNow);
            Diplomacy.ProcessProposals(new[] { npc, player }, w, t);
            Assert.False(Diplomacy.UnderTruce(player, "A", w.UtcNow));
            Assert.Equal(-1.0, player.RelationTo("A"));

            // Control: the same fatigue on an NPC accepts.
            var npc2 = Npc("A"); var other = Npc("EXP");
            other.Relations["A"] = -1.0;
            other.Fatigue["A"] = 1.0;
            Diplomacy.OfferPeace(npc2, "EXP", w.UtcNow);
            Diplomacy.ProcessProposals(new[] { npc2, other }, w, t);
            Assert.True(Diplomacy.UnderTruce(other, "A", w.UtcNow));
        }

        [Fact]
        public void TheLeadersOwnOffer_ConcludesTheTruce_AndSetsTheStance()
        {
            var t = TestSupport.Tuning();
            WorldModel w = Bordering();
            var npc = Npc("A"); var player = Player();
            player.Relations["A"] = -1.0; npc.Relations["EXP"] = -0.8;

            Diplomacy.OfferPeace(npc, "EXP", w.UtcNow);
            LeaderActResult r = PlayerDiplomacy.OfferPeace(player, npc, t, w.UtcNow);
            Assert.True(r.Done);
            Diplomacy.ProcessProposals(new[] { npc, player }, w, t);

            Assert.True(Diplomacy.UnderTruce(player, "A", w.UtcNow));
            Assert.Equal(t.TruceRelation, player.RelationTo("A"));
            Assert.Equal(t.TruceRelation, npc.RelationTo("EXP"));
        }

        // ---- leader acts ----------------------------------------------------------------------

        [Fact]
        public void War_SetsBothSides_AndEndsATruce()
        {
            var t = TestSupport.Tuning();
            var npc = Npc(); var player = Player();
            player.Relations["A"] = 0.1; npc.Relations["EXP"] = 0.1;
            player.TruceUntil["A"] = Now.AddHours(1); npc.TruceUntil["EXP"] = Now.AddHours(1);
            Diplomacy.OfferPeace(player, "A", Now);

            LeaderActResult r = PlayerDiplomacy.DeclareWar(player, npc, t, Now);

            Assert.True(r.Done);
            Assert.StartsWith("DIPLOMACY_WAR player=EXP target=A ", r.LogLine);
            Assert.Equal(PlayerDiplomacy.WarStance, player.RelationTo("A"));
            Assert.True(npc.RelationTo("EXP") < t.HostileRelationThreshold);
            Assert.False(player.TruceUntil.ContainsKey("A"));
            Assert.False(npc.TruceUntil.ContainsKey("EXP"));
            Assert.False(player.Proposals.ContainsKey("A"));
            Assert.True(Diplomacy.TradeBarred(player, npc, t));
        }

        [Fact]
        public void Embargo_BarsTradeBothWays_AndLeavesBothStances()
        {
            var t = TestSupport.Tuning();
            var npc = Npc(); var player = Player();
            player.Relations["A"] = 0.5; npc.Relations["EXP"] = 0.5;
            Assert.False(Diplomacy.TradeBarred(player, npc, t));   // control: open before

            LeaderActResult on = PlayerDiplomacy.SetEmbargo(player, npc, true);
            Assert.True(on.Done);
            Assert.Equal("DIPLOMACY_EMBARGO player=EXP target=A on", on.LogLine);
            Assert.True(Diplomacy.TradeBarred(player, npc, t));
            Assert.True(Diplomacy.TradeBarred(npc, player, t));
            Assert.Equal(0.5, player.RelationTo("A"));
            Assert.Equal(0.5, npc.RelationTo("EXP"));
            Assert.False(PlayerDiplomacy.SetEmbargo(player, npc, true).Done);   // already on: no second line

            LeaderActResult off = PlayerDiplomacy.SetEmbargo(player, npc, false);
            Assert.True(off.Done);
            Assert.Equal("DIPLOMACY_EMBARGO player=EXP target=A off", off.LogLine);
            Assert.False(Diplomacy.TradeBarred(player, npc, t));
            Assert.False(Diplomacy.TradeBarred(npc, player, t));
        }

        [Fact]
        public void Gift_IsRefused_WhenTheTreasuryCannotPay()
        {
            var t = TestSupport.Tuning();
            Assert.True(t.GiftCost > 0, "the refusal is only meaningful with a positive gift cost");
            WorldModel w = TestSupport.World();
            var npc = Npc(); var player = Player();
            w.SetTreasury("EXP", t.GiftCost - 1);

            LeaderActResult r = PlayerDiplomacy.Gift(player, npc, w, t, Now);

            Assert.False(r.Done);
            Assert.Null(r.LogLine);
            Assert.Contains("Nothing was paid", r.Response);
            Assert.Equal(t.GiftCost - 1, w.GetTreasury("EXP"));
            Assert.Equal(0.0, npc.RelationTo("EXP"));
        }

        [Fact]
        public void Gift_PaysAndWarmsTheirView_LeavesTheStance_ThenCoolsDown()
        {
            var t = TestSupport.Tuning();
            WorldModel w = TestSupport.World();
            var npc = Npc(); var player = Player();
            player.Relations["A"] = -0.2; npc.Relations["EXP"] = -0.5;
            w.SetTreasury("EXP", t.GiftCost * 3);

            LeaderActResult r = PlayerDiplomacy.Gift(player, npc, w, t, Now);

            Assert.True(r.Done);
            Assert.StartsWith("DIPLOMACY_GIFT player=EXP target=A cost=" + t.GiftCost + " ", r.LogLine);
            Assert.Equal(t.GiftCost * 2, w.GetTreasury("EXP"));
            Assert.Equal(-0.5 + t.GiftRelationBoost, npc.RelationTo("EXP"), 9);
            Assert.Equal(-0.2, player.RelationTo("A"));

            LeaderActResult again = PlayerDiplomacy.Gift(player, npc, w, t, Now.AddSeconds(1));
            Assert.False(again.Done);
            Assert.Equal(t.GiftCost * 2, w.GetTreasury("EXP"));

            LeaderActResult later = PlayerDiplomacy.Gift(player, npc, w, t, Now + t.GiftDestCooldown + TimeSpan.FromSeconds(1));
            Assert.True(later.Done);
            Assert.Equal(t.GiftCost, w.GetTreasury("EXP"));
        }

        [Fact]
        public void Gift_ToAnImplacableFaction_IsRefusedBeforePaying()
        {
            var t = TestSupport.Tuning();
            WorldModel w = TestSupport.World();
            var npc = new FactionState { Tag = "A", PersonalityId = "factorum" };
            var player = Player();
            w.SetTreasury("EXP", t.GiftCost * 2);
            Assert.False(PlayerDiplomacy.Gift(player, npc, w, t, Now).Done);
            Assert.Equal(t.GiftCost * 2, w.GetTreasury("EXP"));
        }

        [Fact]
        public void Peace_IsRefusedWithNoWar_AndPostsATruceOfferInOne()
        {
            var t = TestSupport.Tuning();
            var npc = Npc(); var player = Player();
            Assert.False(PlayerDiplomacy.OfferPeace(player, npc, t, Now).Done);
            Assert.False(player.Proposals.ContainsKey("A"));

            npc.Relations["EXP"] = -0.8;   // THEY hold us hostile: that is a war too
            LeaderActResult r = PlayerDiplomacy.OfferPeace(player, npc, t, Now);
            Assert.True(r.Done);
            Assert.StartsWith("DIPLOMACY_PEACE_OFFERED player=EXP target=A requested=Truce ", r.LogLine);
            Assert.Equal(DiplomacyVerb.Truce, player.Proposals["A"].Terms[0].Verb);
            Assert.Equal(0.0, player.RelationTo("A"));   // unchanged until concluded
        }

        [Fact]
        public void Propose_PostsTheRightVerb_AndAllianceConcludesOnlyWhenTheyProposeToo()
        {
            var t = TestSupport.Tuning();
            WorldModel w = Bordering();
            var npc = Npc(); var player = Player();

            LeaderActResult na = PlayerDiplomacy.Propose(player, npc, DiplomacyVerb.NonAggression, t, w.UtcNow);
            Assert.True(na.Done);
            Assert.Equal("DIPLOMACY_PROPOSED player=EXP target=A terms=NonAggression", na.LogLine);
            Assert.Equal(DiplomacyVerb.NonAggression, player.Proposals["A"].Terms[0].Verb);

            LeaderActResult al = PlayerDiplomacy.Propose(player, npc, DiplomacyVerb.Alliance, t, w.UtcNow);
            Assert.True(al.Done);
            Assert.Equal(DiplomacyVerb.Alliance, player.Proposals["A"].Terms[0].Verb);

            Diplomacy.ProcessProposals(new[] { npc, player }, w, t);
            Assert.False(Diplomacy.InPactWith(player, "A"));   // one-sided: nothing concludes

            Diplomacy.Propose(npc, "EXP", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, w.UtcNow);
            Diplomacy.ProcessProposals(new[] { npc, player }, w, t);
            Assert.True(Diplomacy.InPactWith(player, "A"));
            Assert.False(PlayerDiplomacy.Propose(player, npc, DiplomacyVerb.Alliance, t, w.UtcNow).Done);
        }

        [Fact]
        public void Withdraw_RemovesTheOffer_AndSaysSoWhenThereIsNone()
        {
            var t = TestSupport.Tuning();
            var npc = Npc(); var player = Player();
            Assert.False(PlayerDiplomacy.Withdraw(player, npc).Done);

            PlayerDiplomacy.Propose(player, npc, DiplomacyVerb.NonAggression, t, Now);
            LeaderActResult r = PlayerDiplomacy.Withdraw(player, npc);
            Assert.True(r.Done);
            Assert.Equal("DIPLOMACY_WITHDRAWN player=EXP target=A terms=NonAggression", r.LogLine);
            Assert.False(player.Proposals.ContainsKey("A"));
        }

        // ---- who may act, and the parser -----------------------------------------------------

        [Fact]
        public void OnlyALeaderActs_AnyMemberReadsStatus()
        {
            foreach (LeaderAct act in Enum.GetValues(typeof(LeaderAct)).Cast<LeaderAct>())
            {
                Assert.Null(PlayerDiplomacy.Authorise(act, true, "EXP"));
                if (act == LeaderAct.Status) Assert.Null(PlayerDiplomacy.Authorise(act, false, "EXP"));
                else Assert.NotNull(PlayerDiplomacy.Authorise(act, false, "EXP"));
            }
        }

        [Theory]
        [InlineData("war", null, LeaderAct.War)]
        [InlineData("EMBARGO", null, LeaderAct.Embargo)]
        [InlineData("lift", null, LeaderAct.Lift)]
        [InlineData("gift", null, LeaderAct.Gift)]
        [InlineData("peace", null, LeaderAct.Peace)]
        [InlineData("withdraw", null, LeaderAct.Withdraw)]
        [InlineData(null, null, LeaderAct.Status)]
        [InlineData("status", null, LeaderAct.Status)]
        [InlineData("propose", "nonaggression", LeaderAct.NonAggression)]
        [InlineData("propose", "Alliance", LeaderAct.Alliance)]
        public void TryParse_KnowsEveryVerb(string verb, string kind, LeaderAct expected)
        {
            LeaderAct act; string error;
            Assert.True(PlayerDiplomacy.TryParse(verb, kind, out act, out error));
            Assert.Equal(expected, act);
        }

        [Fact]
        public void TryParse_RefusesUnknownVerbsAndBareProposals()
        {
            LeaderAct act; string error;
            Assert.False(PlayerDiplomacy.TryParse("nuke", null, out act, out error));
            Assert.NotNull(error);
            Assert.False(PlayerDiplomacy.TryParse("propose", null, out act, out error));
            Assert.False(PlayerDiplomacy.TryParse("propose", "marriage", out act, out error));
        }

        [Fact]
        public void TargetRefusal_OnlyAManagedNpc()
        {
            var player = Player();
            Assert.Null(PlayerDiplomacy.TargetRefusal(player, Npc(), "A"));
            Assert.NotNull(PlayerDiplomacy.TargetRefusal(player, null, "ZZZ"));
            Assert.NotNull(PlayerDiplomacy.TargetRefusal(player, player, "EXP"));
            Assert.NotNull(PlayerDiplomacy.TargetRefusal(player, Player("OTHR"), "OTHR"));
            Assert.NotNull(PlayerDiplomacy.TargetRefusal(player, new FactionState { Tag = NeutralFaction.Tag }, NeutralFaction.Tag));
        }

        [Fact]
        public void Status_ShowsStanceViewEmbargoTruceAndOffersBothWays()
        {
            var t = TestSupport.Tuning();
            var player = Player(); var a = Npc("A"); var b = Npc("B");
            player.Relations["A"] = -1.0; a.Relations["EXP"] = -0.6;
            a.Embargoes.Add("EXP");
            player.TruceUntil["B"] = Now.AddMinutes(10);
            Diplomacy.OfferPeace(a, "EXP", Now);
            PlayerDiplomacy.Propose(player, b, DiplomacyVerb.Alliance, t, Now);

            string all = PlayerDiplomacy.Describe(player, new[] { player, a, b, Player("OTHR") }, null, t, Now);
            Assert.Contains("A  stance -1.00 (hostile)  view -0.60 (hostile)  embargo by them  TRADE BARRED", all);
            Assert.Contains("they offered Truce", all);
            Assert.Contains("truce until 00:10Z", all);
            Assert.Contains("you offered Alliance", all);
            Assert.DoesNotContain("OTHR", all);

            string one = PlayerDiplomacy.Describe(player, new[] { a, b }, "b", t, Now);
            Assert.DoesNotContain("  A  ", one);
            Assert.Contains("  B  ", one);
        }

        // ---- the readout the FactionsUI snapshot is built from ---------------------------------

        [Fact]
        public void Readout_OneRowPerDealableNpc_SortedByTag_WithEveryFieldFromTheState()
        {
            var t = TestSupport.Tuning();
            var player = Player(); var b = Npc("B"); var a = Npc("A"); var f = new FactionState { Tag = "F", PersonalityId = "factorum" };
            player.Relations["A"] = -1.0; a.Relations["EXP"] = -0.6;
            player.Embargoes.Add("A");
            a.Embargoes.Add("EXP");
            b.Relations["EXP"] = t.AllyRelationThreshold + 0.05;
            player.TruceUntil["B"] = Now.AddMinutes(10);
            Diplomacy.OfferPeace(a, "EXP", Now);
            Assert.True(PlayerDiplomacy.Propose(player, b, DiplomacyVerb.Alliance, t, Now).Done);
            // After the proposal: an alliance already holding refuses a second one.
            b.PactWith.Add("EXP");
            player.TradeDestCooldowns["A"] = Now.AddMinutes(5);
            player.TradeDestCooldowns["B"] = Now.AddMinutes(-5);   // lapsed: may gift now

            var rows = PlayerDiplomacy.Readout(player,
                new[] { player, b, a, f, Player("OTHR"), new FactionState { Tag = NeutralFaction.Tag } }, t, Now);

            Assert.Equal(new[] { "A", "B", "F" }, rows.Select(r => r.NpcTag).ToArray());

            DiplomacyReadoutRow ra = rows[0];
            Assert.Equal(-0.6, ra.TheirView);
            Assert.Equal(StanceBand.Hostile, ra.TheirBand);
            Assert.Equal(-1.0, ra.YourStance);
            Assert.Equal(StanceBand.Hostile, ra.YourBand);
            Assert.True(ra.EmbargoByYou);
            Assert.True(ra.EmbargoByThem);
            Assert.True(ra.TradeBarred);
            Assert.False(ra.Allied);
            Assert.False(ra.Implacable);
            Assert.Null(ra.TruceUntilUtc);
            Assert.Null(ra.YouOffered);
            Assert.Equal("Truce", ra.TheyOffered);
            Assert.Equal(Now + Diplomacy.OfferTtl(a, player, false, t), ra.TheyOfferedExpiresUtc);
            Assert.Equal(Now.AddMinutes(5), ra.GiftReadyUtc);

            DiplomacyReadoutRow rb = rows[1];
            Assert.Equal(StanceBand.Friendly, rb.TheirBand);
            Assert.Equal(StanceBand.Neutral, rb.YourBand);
            Assert.False(rb.EmbargoByYou);
            Assert.False(rb.EmbargoByThem);
            Assert.False(rb.TradeBarred);
            Assert.True(rb.Allied);
            Assert.Equal(Now.AddMinutes(10), rb.TruceUntilUtc);
            Assert.Equal("Alliance", rb.YouOffered);
            Assert.Equal(Now + Diplomacy.OfferTtl(player, b, true, t), rb.YouOfferedExpiresUtc);
            Assert.Null(rb.TheyOffered);
            Assert.Null(rb.GiftReadyUtc);

            Assert.True(rows[2].Implacable);
        }

        [Fact]
        public void Readout_ATruceOnTheNpcsSideAlone_StillReads_AndALapsedOneDoesNot()
        {
            var t = TestSupport.Tuning();
            var player = Player(); var a = Npc("A");
            player.TruceUntil["A"] = Now.AddMinutes(-1);   // lapsed on the player's side
            a.TruceUntil["EXP"] = Now.AddMinutes(3);

            Assert.Equal(Now.AddMinutes(3), PlayerDiplomacy.ReadoutRow(player, a, t, Now).TruceUntilUtc);

            a.TruceUntil["EXP"] = Now.AddMinutes(-2);
            Assert.Null(PlayerDiplomacy.ReadoutRow(player, a, t, Now).TruceUntilUtc);
        }

        [Fact]
        public void Readout_DropsAnOfferPastItsTtl_BeforeTheNextPassPrunesIt()
        {
            var t = TestSupport.Tuning();
            var player = Player(); var a = Npc("A");
            Diplomacy.OfferPeace(a, "EXP", Now);
            TimeSpan ttl = Diplomacy.OfferTtl(a, player, false, t);

            Assert.Equal("Truce", PlayerDiplomacy.ReadoutRow(player, a, t, Now + ttl).TheyOffered);
            DiplomacyReadoutRow late = PlayerDiplomacy.ReadoutRow(player, a, t, Now + ttl + TimeSpan.FromSeconds(1));
            Assert.Null(late.TheyOffered);
            Assert.Null(late.TheyOfferedExpiresUtc);
            Assert.True(a.Proposals.ContainsKey("EXP"), "the fixture must still hold the stale offer");
        }

        [Fact]
        public void Readout_IsEmptyForNoPlayer_AndDescribeFormatsTheSameRows()
        {
            var t = TestSupport.Tuning();
            Assert.Empty(PlayerDiplomacy.Readout(null, new[] { Npc() }, t, Now));

            var player = Player(); var a = Npc("A");
            player.Relations["A"] = -1.0; a.Relations["EXP"] = -0.6;
            DiplomacyReadoutRow row = PlayerDiplomacy.ReadoutRow(player, a, t, Now);
            string line = PlayerDiplomacy.Describe(player, new[] { a }, null, t, Now);
            Assert.Contains("stance " + row.YourStance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), line);
            Assert.Contains("view " + row.TheirView.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), line);
        }

        [Fact]
        public void BandOf_SplitsAtBothThresholds()
        {
            var t = TestSupport.Tuning();
            Assert.Equal(StanceBand.Hostile, PlayerDiplomacy.BandOf(t.HostileRelationThreshold - 0.001, t));
            Assert.Equal(StanceBand.Neutral, PlayerDiplomacy.BandOf(t.HostileRelationThreshold, t));
            Assert.Equal(StanceBand.Neutral, PlayerDiplomacy.BandOf(t.AllyRelationThreshold - 0.001, t));
            Assert.Equal(StanceBand.Friendly, PlayerDiplomacy.BandOf(t.AllyRelationThreshold, t));
        }
    }
}
