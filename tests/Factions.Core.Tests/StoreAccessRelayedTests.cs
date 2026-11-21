using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The store bar on a Nexus agent: the authority records on each NPC's snapshot row the
    /// registered factions it refuses, and the agent judges from that row. Each refused case is
    /// paired with a served one on the same fixture, and every pair the authority refuses must be
    /// refused alike on the agent after the snapshot's round trip.
    /// </summary>
    public class StoreAccessRelayedTests
    {
        private const double Foe = -0.63;

        private static FactionState Player(string tag)
        {
            FactionState st = TradeTest.State(tag);
            st.IsPlayerFaction = true;
            return st;
        }

        private static NexusNodeSnapshot RoundTrip(List<FactionState> factions, FactionTuning t)
        {
            NexusNodeSnapshot s = NexusNodeSnapshot.Build(null, factions, 1);
            NexusNodeSnapshot.FillTradeBars(s, factions, t);
            NexusNodeSnapshot back;
            Assert.True(NexusNodeSnapshot.TryDecode(s.Encode(), out back));
            return back;
        }

        private static NexusSnapshotFaction Row(NexusNodeSnapshot s, string tag)
        {
            return s.Factions.Find(f => f.Tag == tag);
        }

        [Fact]
        public void AnAgentRefusesExactlyWhatTheAuthorityRefuses()
        {
            FactionTuning t = TestSupport.Tuning();
            FactionState impr = TradeTest.State("IMPR"), mrch = TradeTest.State("MRCH");
            FactionState exp = Player("EXP"), cnd = Player("CND"), tra = Player("TRA");
            impr.Relations["EXP"] = Foe;          // hostile
            impr.Embargoes.Add("CND");            // embargo by the owner
            tra.Embargoes.Add("IMPR");            // embargo by the customer
            var all = new List<FactionState> { impr, mrch, exp, cnd, tra };

            NexusNodeSnapshot s = RoundTrip(all, t);

            foreach (FactionState owner in new[] { impr, mrch })
                foreach (FactionState customer in all)
                {
                    StoreAccessDecision here = StoreAccess.Decide(owner, customer, "Neutral", t);
                    StoreAccessDecision there = StoreAccess.DecideRelayed(Row(s, owner.Tag), customer.Tag, "Neutral");
                    Assert.Equal(here.Refused, there.Refused);
                    Assert.Equal(here.Cause, there.Cause);
                    Assert.Equal(here.Message, there.Message);
                }

            Assert.True(StoreAccess.DecideRelayed(Row(s, "IMPR"), "EXP", null).Refused);
            Assert.True(StoreAccess.DecideRelayed(Row(s, "IMPR"), "CND", null).Refused);
            Assert.True(StoreAccess.DecideRelayed(Row(s, "IMPR"), "TRA", null).Refused);
            Assert.False(StoreAccess.DecideRelayed(Row(s, "MRCH"), "EXP", null).Refused);
        }

        [Fact]
        public void ACustomerInNoRegisteredFactionIsJudgedByTheirOwnBand()
        {
            FactionTuning t = TestSupport.Tuning();
            var all = new List<FactionState> { TradeTest.State("IMPR") };
            NexusSnapshotFaction impr = Row(RoundTrip(all, t), "IMPR");

            StoreAccessDecision foe = StoreAccess.DecideRelayed(impr, null, StoreAccess.EnemiesBand);
            Assert.True(foe.Refused);
            Assert.Equal("personal:Enemies", foe.Cause);
            Assert.Equal(StoreAccess.Decide(all[0], null, StoreAccess.EnemiesBand, t).Message, foe.Message);
            Assert.False(StoreAccess.DecideRelayed(impr, null, "Neutral").Refused);
        }

        [Fact]
        public void APlayerOwnerOrAnUnregisteredOwnerIsServed()
        {
            FactionTuning t = TestSupport.Tuning();
            FactionState exp = Player("EXP"), impr = TradeTest.State("IMPR");
            exp.Relations["IMPR"] = Foe;
            exp.Embargoes.Add("IMPR");
            NexusNodeSnapshot s = RoundTrip(new List<FactionState> { exp, impr }, t);

            Assert.Null(Row(s, "EXP").Bars);
            Assert.False(StoreAccess.DecideRelayed(Row(s, "EXP"), "IMPR", StoreAccess.EnemiesBand).Refused);
            Assert.False(StoreAccess.DecideRelayed(null, "EXP", StoreAccess.EnemiesBand).Refused);
        }

        [Fact]
        public void AnOwnerWithNoBarsSendsNone()
        {
            FactionTuning t = TestSupport.Tuning();
            NexusNodeSnapshot s = RoundTrip(new List<FactionState> { TradeTest.State("IMPR"), Player("EXP") }, t);
            Assert.Null(Row(s, "IMPR").Bars);
            Assert.DoesNotContain("Bars", System.Text.Encoding.UTF8.GetString(NexusHandsJson(s)));
        }

        private static byte[] NexusHandsJson(NexusNodeSnapshot s)
        {
            return System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(s,
                new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }));
        }
    }
}
