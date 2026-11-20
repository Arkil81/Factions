using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Rule 2 of docs/diplomacy-plan.md: a customer is refused at a station whose owner is barred
    /// from trading with them. A member of a registered faction is judged by
    /// <see cref="Diplomacy.TradeBarred"/>, a customer in none by their own reputation band, and the
    /// owning faction's own members never. Every refused row is paired with a served row on the
    /// same fixture, so a decision that refuses everything cannot pass.
    /// </summary>
    public class StoreAccessTests
    {
        private const double Foe = -0.63;   // IMPR's standing toward EXP on 09-26

        private static FactionState Npc() { return TradeTest.State("IMPR"); }

        private static FactionState Player()
        {
            FactionState st = TradeTest.State("EXP");
            st.IsPlayerFaction = true;
            return st;
        }

        public enum Holder { None, Owner, Customer }

        [Theory]
        //           owner->cust  cust->owner  embargo          refused  cause            message
        [InlineData(0.0,          0.0,         Holder.None,     false,   null,            null)]
        [InlineData(0.5,          0.5,         Holder.None,     false,   null,            null)]
        [InlineData(-0.3,         -0.3,        Holder.None,     false,   null,            null)]   // AT the line is not below it
        [InlineData(Foe,          0.0,         Holder.None,     true,    "hostile:IMPR",  "IMPR is hostile to EXP — trade refused")]
        [InlineData(0.0,          Foe,         Holder.None,     true,    "hostile:EXP",   "EXP is hostile to IMPR — trade refused")]
        [InlineData(0.5,          0.5,         Holder.Owner,    true,    "embargo:IMPR",  "IMPR has embargoed EXP — trade refused")]
        [InlineData(0.5,          0.5,         Holder.Customer, true,    "embargo:EXP",   "EXP has embargoed IMPR — trade refused")]
        [InlineData(Foe,          0.0,         Holder.Owner,    true,    "embargo:IMPR",  "IMPR has embargoed EXP — trade refused")]   // the deliberate act is named first
        [InlineData(Foe,          Foe,         Holder.Customer, true,    "hostile:IMPR",  "IMPR is hostile to EXP — trade refused")]   // the owner's side is named first
        public void FactionMember_TruthTable(double ownerToCustomer, double customerToOwner, Holder embargo,
                                             bool refused, string cause, string message)
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(-0.3, t.HostileRelationThreshold);   // the rows above are written against it
            FactionState owner = Npc(), customer = Player();
            owner.Relations["EXP"] = ownerToCustomer;
            customer.Relations["IMPR"] = customerToOwner;
            if (embargo == Holder.Owner) owner.Embargoes.Add("EXP");
            if (embargo == Holder.Customer) customer.Embargoes.Add("IMPR");

            StoreAccessDecision d = StoreAccess.Decide(owner, customer, "Neutral", t);

            Assert.Equal(refused, d.Refused);
            Assert.Equal(cause, d.Cause);
            Assert.Equal(message, d.Message);
            // The Core predicate and the store decision cannot disagree for a member.
            Assert.Equal(Diplomacy.TradeBarred(owner, customer, t), d.Refused);
        }

        /// <summary>A member is judged by their faction, not by their own record: a personal band of
        /// Enemies does not refuse a member whose faction the owner will trade with.</summary>
        [Fact]
        public void FactionMember_PersonalBandIsNotRead()
        {
            FactionTuning t = TestSupport.Tuning();
            FactionState owner = Npc(), customer = Player();

            Assert.False(StoreAccess.Decide(owner, customer, StoreAccess.EnemiesBand, t).Refused);

            owner.Relations["EXP"] = Foe;   // control: the same fixture refuses once the faction is barred
            Assert.True(StoreAccess.Decide(owner, customer, "Friends", t).Refused);
        }

        [Theory]
        [InlineData("Enemies", true)]
        [InlineData("enemies", true)]
        [InlineData("Neutral", false)]
        [InlineData("Friends", false)]
        [InlineData(null,      false)]   // unreadable: served
        [InlineData("",        false)]
        public void NoFaction_JudgedByPersonalBand(string band, bool refused)
        {
            StoreAccessDecision d = StoreAccess.Decide(Npc(), null, band, TestSupport.Tuning());
            Assert.Equal(refused, d.Refused);
            if (refused)
            {
                Assert.Equal("IMPR will not trade with you", d.Message);
                Assert.Equal("personal:Enemies", d.Cause);
            }
            else
            {
                Assert.Null(d.Message);
                Assert.Null(d.Cause);
            }
        }

        /// <summary>The band string the host passes is SE's enum name, and Core's own mirror of
        /// <c>TranslateReputationToRelationship</c> spells it the same way.</summary>
        [Fact]
        public void EnemiesBand_MatchesReputationScale()
        {
            ReputationScale s = ReputationScale.Vanilla;
            Assert.Equal(StoreAccess.EnemiesBand, s.BandOf(s.NeutralMin - 1));
            Assert.NotEqual(StoreAccess.EnemiesBand, s.BandOf(s.NeutralMin));
        }

        /// <summary>A member of the owner is never refused at its own stations, whatever the owner's
        /// sets say — including a degenerate self-embargo that <see cref="Diplomacy.TradeBarred"/>
        /// alone would read as barred.</summary>
        [Fact]
        public void OwnFaction_IsNeverRefused()
        {
            FactionTuning t = TestSupport.Tuning();
            FactionState owner = Npc();
            owner.Embargoes.Add("IMPR");
            owner.Relations["IMPR"] = -1.0;
            Assert.True(Diplomacy.TradeBarred(owner, owner, t));   // the guard is what serves them

            Assert.False(StoreAccess.Decide(owner, owner, StoreAccess.EnemiesBand, t).Refused);
            FactionState sameTagOtherCase = TradeTest.State("impr");
            Assert.False(StoreAccess.Decide(owner, sameTagOtherCase, null, t).Refused);
        }

        [Fact]
        public void UnregisteredOwner_IsServed()
        {
            Assert.False(StoreAccess.Decide(null, Player(), StoreAccess.EnemiesBand, TestSupport.Tuning()).Refused);
            Assert.False(StoreAccess.Decide(null, null, StoreAccess.EnemiesBand, TestSupport.Tuning()).Refused);
        }

        /// <summary>A null tuning drops the relation half, as it does for the predicate; the embargo
        /// is a set and still refuses.</summary>
        [Fact]
        public void NullTuning_EmbargoStillRefuses_HostilityDoesNot()
        {
            FactionState owner = Npc(), customer = Player();
            owner.Relations["EXP"] = Foe;
            Assert.False(StoreAccess.Decide(owner, customer, null, null).Refused);

            owner.Embargoes.Add("EXP");
            StoreAccessDecision d = StoreAccess.Decide(owner, customer, null, null);
            Assert.True(d.Refused);
            Assert.Equal("embargo:IMPR", d.Cause);
        }

        // ---- the notice throttle -------------------------------------------------------------

        private static readonly DateTime T0 = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Throttle_TellsOncePerPlayerPerOwnerPerWindow()
        {
            var th = new RefusalNoticeThrottle();
            Assert.True(th.ShouldNotify(1, "IMPR", T0, 30));
            Assert.False(th.ShouldNotify(1, "IMPR", T0.AddSeconds(29.9), 30));
            Assert.False(th.ShouldNotify(1, "impr", T0.AddSeconds(10), 30));   // same owner, other casing
            Assert.True(th.ShouldNotify(1, "IMPR", T0.AddSeconds(30), 30));    // window elapsed
            Assert.False(th.ShouldNotify(1, "IMPR", T0.AddSeconds(45), 30));   // measured from the last telling

            Assert.True(th.ShouldNotify(2, "IMPR", T0.AddSeconds(45), 30));    // another player
            Assert.True(th.ShouldNotify(1, "SPRT", T0.AddSeconds(45), 30));    // another owner
        }

        [Fact]
        public void Throttle_ZeroCooldownTellsEveryTime()
        {
            var th = new RefusalNoticeThrottle();
            Assert.True(th.ShouldNotify(1, "IMPR", T0, 0));
            Assert.True(th.ShouldNotify(1, "IMPR", T0, 0));
        }

        [Fact]
        public void Throttle_IsBounded()
        {
            var th = new RefusalNoticeThrottle();
            for (int i = 0; i < 5000; i++) th.ShouldNotify(i, "IMPR", T0, 30);
            // Past the bound, stamps older than the window are pruned on the next insert.
            th.ShouldNotify(-1, "IMPR", T0.AddSeconds(60), 30);
            Assert.True(th.Count <= 4097, "count " + th.Count);
        }

        [Fact]
        public void CooldownDefault_IsShipped()
        {
            Assert.Equal(30.0, new FactionTuning().StoreRefusalNoticeCooldownSeconds);
            Assert.Equal(30.0, TestSupport.Tuning().StoreRefusalNoticeCooldownSeconds);
        }
    }
}
