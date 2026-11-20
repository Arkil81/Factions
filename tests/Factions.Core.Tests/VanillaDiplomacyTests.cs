using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-394 slice 5: the vanilla Factions screen's diplomacy buttons, judged and mirrored.
    /// The SE glue (VanillaDiplomacySync.cs) only applies these verdicts.</summary>
    public class VanillaDiplomacyTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static VanillaRequestVerdict Judge(VanillaFactionAct act, bool playerSent = true, bool inScope = true,
                                                   bool leader = true, bool admin = false,
                                                   bool vanillaAccepted = true, bool registered = true)
        {
            return VanillaDiplomacy.JudgeRequest(act, playerSent, inScope, leader, admin, vanillaAccepted, registered);
        }

        // ---- the request verdict -------------------------------------------------------------

        [Fact]
        public void SendPeace_PostsAnOffer_AndKeepsTheVanillaFlag()
        {
            Assert.Equal(VanillaRequestVerdict.OfferPeaceAndKeep, Judge(VanillaFactionAct.SendPeaceRequest));
        }

        [Fact]
        public void AcceptPeace_PostsAnOffer_AndSuppressesTheBroadcast()
        {
            // Letting it through would broadcast Neutral to every client while the war stands.
            Assert.Equal(VanillaRequestVerdict.OfferPeaceAndSuppress, Judge(VanillaFactionAct.AcceptPeace));
            Assert.Equal(VanillaRequestVerdict.OfferPeaceAndSuppress, Judge(VanillaFactionAct.AcceptFriendRequest));
        }

        [Fact]
        public void CancelPeace_WithdrawsTheOffer()
        {
            Assert.Equal(VanillaRequestVerdict.WithdrawAndKeep, Judge(VanillaFactionAct.CancelPeaceRequest));
        }

        [Fact]
        public void DeclareWar_PassesThrough_ToTheApplyRouting()
        {
            Assert.Equal(VanillaRequestVerdict.PassThrough, Judge(VanillaFactionAct.DeclareWar));
        }

        [Theory]
        [InlineData(VanillaFactionAct.SendPeaceRequest)]
        [InlineData(VanillaFactionAct.CancelPeaceRequest)]
        [InlineData(VanillaFactionAct.AcceptPeace)]
        [InlineData(VanillaFactionAct.AcceptFriendRequest)]
        [InlineData(VanillaFactionAct.DeclareWar)]
        public void NonLeader_IsRefused_ForEveryDiplomaticAct(VanillaFactionAct act)
        {
            Assert.Equal(VanillaRequestVerdict.RefuseNotLeader, Judge(act, leader: false));
        }

        [Fact]
        public void NonLeader_IsRefused_EvenWhenVanillaAcceptedIt()
        {
            // The refusal must not lean on one SE version's own check.
            Assert.Equal(VanillaRequestVerdict.RefuseNotLeader,
                         Judge(VanillaFactionAct.SendPeaceRequest, leader: false, vanillaAccepted: true));
        }

        [Fact]
        public void NonLeader_IsRefused_BeforeRegistrationIsConsidered()
        {
            Assert.Equal(VanillaRequestVerdict.RefuseNotLeader,
                         Judge(VanillaFactionAct.DeclareWar, leader: false, registered: false));
        }

        [Fact]
        public void Admin_KeepsVanillasExemption()
        {
            Assert.Equal(VanillaRequestVerdict.OfferPeaceAndKeep,
                         Judge(VanillaFactionAct.SendPeaceRequest, leader: false, admin: true));
        }

        [Fact]
        public void ServerOriginatedChange_IsNotAPlayerAct()
        {
            Assert.Equal(VanillaRequestVerdict.PassThrough,
                         Judge(VanillaFactionAct.SendPeaceRequest, playerSent: false, leader: false));
        }

        [Fact]
        public void OutOfScopePair_IsVanillas()
        {
            Assert.Equal(VanillaRequestVerdict.PassThrough,
                         Judge(VanillaFactionAct.SendPeaceRequest, inScope: false, leader: false));
        }

        [Fact]
        public void VanillaStateRefusal_Stands_AndRoutesNothing()
        {
            // e.g. a peace request to a faction SE does not hold as an enemy.
            Assert.Equal(VanillaRequestVerdict.PassThrough,
                         Judge(VanillaFactionAct.SendPeaceRequest, vanillaAccepted: false));
        }

        [Fact]
        public void UnregisteredPlayerFaction_HasNoStateToRouteInto()
        {
            Assert.Equal(VanillaRequestVerdict.PassThrough,
                         Judge(VanillaFactionAct.AcceptPeace, registered: false));
        }

        [Fact]
        public void MembershipChanges_AreNeverJudged()
        {
            Assert.Equal(VanillaRequestVerdict.PassThrough, Judge(VanillaFactionAct.Other, leader: false));
            Assert.Equal(VanillaRequestVerdict.PassThrough, Judge(VanillaFactionAct.None, leader: false));
        }

        // ---- the offer --------------------------------------------------------------------------

        [Fact]
        public void TruceOfferOpen_TracksOfferPeace_UntilTheTtl()
        {
            var t = TestSupport.Tuning();
            var p = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            var impr = new FactionState { Tag = "IMPR" };
            Assert.False(VanillaDiplomacy.TruceOfferOpen(p, impr, T0, t));

            // A player side puts the pair on the player window, not the NPC one.
            Diplomacy.OfferPeace(p, "IMPR", T0);
            Assert.True(VanillaDiplomacy.TruceOfferOpen(p, impr, T0, t));
            Assert.True(VanillaDiplomacy.TruceOfferOpen(p, impr, T0 + t.PeaceOfferTtl + TimeSpan.FromSeconds(1), t),
                        "the NPC TTL does not end a player's offer");
            Assert.True(VanillaDiplomacy.TruceOfferOpen(p, impr, T0 + t.PlayerOfferTtl, t),
                        "ProcessProposals prunes only an offer strictly older than the TTL");
            Assert.False(VanillaDiplomacy.TruceOfferOpen(p, impr, T0 + t.PlayerOfferTtl + TimeSpan.FromSeconds(1), t));
            Assert.False(VanillaDiplomacy.TruceOfferOpen(p, new FactionState { Tag = "METC" }, T0, t), "an offer is to one partner");
        }

        [Fact]
        public void AnAllianceProposal_IsNotAPeaceOffer()
        {
            var t = TestSupport.Tuning();
            var p = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            Diplomacy.Propose(p, "IMPR", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, T0);

            Assert.False(VanillaDiplomacy.TruceOfferOpen(p, new FactionState { Tag = "IMPR" }, T0, t));
            Assert.False(VanillaDiplomacy.WithdrawPeace(p, "IMPR"));
            Assert.True(p.Proposals.ContainsKey("IMPR"), "withdrawing peace leaves an alliance proposal standing");
        }

        [Fact]
        public void ATruceBundledWithAnAlliance_IsAnAllianceProposal()
        {
            // ProcessProposals reads any message carrying Alliance as an alliance proposal, on the
            // pact TTL and the strictly bilateral rule, so it is not a peace offer here either.
            var t = TestSupport.Tuning();
            var p = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            Diplomacy.Propose(p, "IMPR", new[]
            {
                new DiplomacyTerm { Verb = DiplomacyVerb.Truce },
                new DiplomacyTerm { Verb = DiplomacyVerb.Alliance },
            }, T0);

            Assert.False(VanillaDiplomacy.TruceOfferOpen(p, new FactionState { Tag = "IMPR" }, T0, t));
        }

        [Fact]
        public void WithdrawPeace_RemovesTheOffer()
        {
            var t = TestSupport.Tuning();
            var p = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            Diplomacy.OfferPeace(p, "IMPR", T0);

            Assert.True(VanillaDiplomacy.WithdrawPeace(p, "IMPR"));
            Assert.False(VanillaDiplomacy.TruceOfferOpen(p, new FactionState { Tag = "IMPR" }, T0, t));
            Assert.False(VanillaDiplomacy.WithdrawPeace(p, "IMPR"), "nothing left to withdraw");
        }

        // ---- the flag ---------------------------------------------------------------------------

        [Fact]
        public void Reconcile_ShowsAnOpenOffer_AndClearsAGoneOne()
        {
            Assert.Equal(VanillaFactionAct.SendPeaceRequest,   VanillaDiplomacy.ReconcileRequest(true, false, true));
            Assert.Equal(VanillaFactionAct.CancelPeaceRequest, VanillaDiplomacy.ReconcileRequest(true, true, false));
            Assert.Equal(VanillaFactionAct.None,               VanillaDiplomacy.ReconcileRequest(true, true, true));
            Assert.Equal(VanillaFactionAct.None,               VanillaDiplomacy.ReconcileRequest(true, false, false));
        }

        [Fact]
        public void Reconcile_LeavesAPairNotAtWarAlone()
        {
            // SE's request set holds friend requests too, which exist only between Neutral factions.
            Assert.Equal(VanillaFactionAct.None, VanillaDiplomacy.ReconcileRequest(false, true, false));
            Assert.Equal(VanillaFactionAct.None, VanillaDiplomacy.ReconcileRequest(false, false, true));
        }

        [Fact]
        public void ActForBand_MapsEachBandToTheBroadcastThatSetsIt()
        {
            Assert.Equal(VanillaFactionAct.DeclareWar,          VanillaDiplomacy.ActForBand("Enemies"));
            Assert.Equal(VanillaFactionAct.AcceptPeace,         VanillaDiplomacy.ActForBand("Neutral"));
            Assert.Equal(VanillaFactionAct.AcceptFriendRequest, VanillaDiplomacy.ActForBand("Friends"));
            Assert.Equal(VanillaFactionAct.None,                VanillaDiplomacy.ActForBand("nonsense"));
        }

        // ---- the lifecycle, end to end through ProcessProposals ------------------------------

        [Fact]
        public void Lifecycle_AnOfferToAFreshEnemy_StaysPending_ThenLapsesToCancel()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = T0 };
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true }; player.Relations["IMPR"] = -0.9;
            var npc = new FactionState { Tag = "IMPR" }; npc.Relations["EXP"] = -0.9;   // no fatigue: not war-weary

            Diplomacy.OfferPeace(player, "IMPR", w.UtcNow);
            Diplomacy.ProcessProposals(new[] { player, npc }, w, t);

            bool open = VanillaDiplomacy.TruceOfferOpen(player, npc, w.UtcNow, t);
            Assert.True(open, "an unwilling enemy leaves the offer on the table");
            Assert.Equal(VanillaFactionAct.None, VanillaDiplomacy.ReconcileRequest(true, true, open));

            // Past the NPC TTL the player's offer still stands, and its flag stays up.
            w.UtcNow = T0 + t.PeaceOfferTtl + TimeSpan.FromMinutes(1);
            Diplomacy.ProcessProposals(new[] { player, npc }, w, t);
            Assert.True(VanillaDiplomacy.TruceOfferOpen(player, npc, w.UtcNow, t));

            w.UtcNow = T0 + t.PlayerOfferTtl + TimeSpan.FromMinutes(1);
            Diplomacy.ProcessProposals(new[] { player, npc }, w, t);

            open = VanillaDiplomacy.TruceOfferOpen(player, npc, w.UtcNow, t);
            Assert.False(open);
            Assert.False(player.Proposals.ContainsKey("IMPR"), "ProcessProposals pruned it on the same rule");
            Assert.False(Diplomacy.UnderTruce(player, "IMPR", w.UtcNow));
            Assert.Equal(VanillaFactionAct.CancelPeaceRequest, VanillaDiplomacy.ReconcileRequest(true, true, open));
        }

        [Fact]
        public void Lifecycle_AWarWearyEnemy_Concludes_AndTheProjectedBandIsAcceptPeace()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = T0 };
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true }; player.Relations["IMPR"] = -0.9;
            var npc = new FactionState { Tag = "IMPR" }; npc.Relations["EXP"] = -0.9;
            npc.Fatigue["EXP"] = t.AcceptPeaceThreshold;

            Diplomacy.OfferPeace(player, "IMPR", w.UtcNow);
            string a = null, b = null;
            Diplomacy.ProcessProposals(new[] { player, npc }, w, t, onTruce: (x, y) => { a = x; b = y; });

            Assert.Equal("EXP", a);
            Assert.Equal("IMPR", b);
            Assert.False(VanillaDiplomacy.TruceOfferOpen(player, npc, w.UtcNow, t), "a concluded offer is gone");

            // What ProjectRelationsToSe computes for the pair: the more hostile view, floored at 0 by the truce.
            double rel = Math.Min(player.RelationTo("IMPR"), npc.RelationTo("EXP"));
            if (Diplomacy.UnderTruce(player, "IMPR", w.UtcNow)) rel = Math.Max(rel, 0.0);
            ReputationScale scale = ReputationScale.Vanilla;
            string band = scale.BandOf(scale.ReputationFor(rel, t.HostileRelationThreshold, t.AllyRelationThreshold));

            Assert.Equal(VanillaFactionAct.AcceptPeace, VanillaDiplomacy.ActForBand(band));
        }

        [Fact]
        public void Lifecycle_AnNpcOfferToAPlayer_IsShownAsPending()
        {
            var t = TestSupport.Tuning();
            var npc = new FactionState { Tag = "IMPR" };
            Diplomacy.OfferPeace(npc, "EXP", T0);

            bool open = VanillaDiplomacy.TruceOfferOpen(npc, new FactionState { Tag = "EXP", IsPlayerFaction = true }, T0, t);
            Assert.Equal(VanillaFactionAct.SendPeaceRequest, VanillaDiplomacy.ReconcileRequest(true, false, open));
        }
    }
}
