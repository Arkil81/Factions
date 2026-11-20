using System;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The player offer window: any offer where either side is a player faction stays open for
    /// <see cref="FactionTuning.PlayerOfferTtl"/> (a day), because a person has to be online to answer
    /// it. NPC-to-NPC offers keep <see cref="FactionTuning.PeaceOfferTtl"/> /
    /// <see cref="FactionTuning.PactOfferTtl"/>. Every "stays open" headline is paired with the
    /// NPC-to-NPC offer lapsing at the old TTL on the same clock.
    /// </summary>
    public class PlayerOfferWindowTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static FactionState Player(string tag = "EXP") { return new FactionState { Tag = tag, IsPlayerFaction = true }; }
        private static FactionState Npc(string tag = "IMPR") { return new FactionState { Tag = tag, PersonalityId = "settler" }; }

        private static void Pass(WorldModel w, FactionTuning t, params FactionState[] roster)
        {
            Diplomacy.ProcessProposals(roster, w, t);
        }

        [Fact]
        public void TheShippedDefault_IsADay_AndMuchLongerThanTheNpcTtls()
        {
            var t = TestSupport.Tuning();
            Assert.Equal(TimeSpan.FromHours(24), t.PlayerOfferTtl);
            Assert.Equal(TimeSpan.FromHours(24), new FactionTuning().PlayerOfferTtl);
            Assert.True(t.PlayerOfferTtl > t.PeaceOfferTtl && t.PlayerOfferTtl > t.PactOfferTtl);
        }

        [Fact]
        public void OfferTtl_PicksThePlayerWindow_WhenEitherSideIsAPlayer()
        {
            var t = TestSupport.Tuning();
            Assert.Equal(t.PeaceOfferTtl, Diplomacy.OfferTtl(Npc("A"), Npc("B"), false, t));
            Assert.Equal(t.PactOfferTtl, Diplomacy.OfferTtl(Npc("A"), Npc("B"), true, t));
            Assert.Equal(t.PlayerOfferTtl, Diplomacy.OfferTtl(Player(), Npc(), false, t));
            Assert.Equal(t.PlayerOfferTtl, Diplomacy.OfferTtl(Npc(), Player(), false, t));
            Assert.Equal(t.PlayerOfferTtl, Diplomacy.OfferTtl(Npc(), Player(), true, t));
            Assert.Equal(t.PlayerOfferTtl, Diplomacy.OfferTtl(Player(), null, false, t));
            Assert.Equal(t.PeaceOfferTtl, Diplomacy.OfferTtl(Npc(), null, false, t));

            t.PlayerOfferTtl = TimeSpan.Zero;   // off switch
            Assert.Equal(t.PeaceOfferTtl, Diplomacy.OfferTtl(Player(), Npc(), false, t));
        }

        [Fact]
        public void AnNpcToNpcOffer_StillLapsesAtTheOldTtl_BesideAPlayerOfferThatDoesNot()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            w.UtcNow = T0;
            // Nobody willing, nobody weary: both offers just sit.
            var a = Npc("A"); var b = Npc("B"); var player = Player(); var impr = Npc("IMPR");
            a.Relations["B"] = -0.9; b.Relations["A"] = -0.9;
            player.Relations["IMPR"] = -0.9; impr.Relations["EXP"] = -0.9;
            Diplomacy.OfferPeace(a, "B", T0);
            Diplomacy.OfferPeace(player, "IMPR", T0);
            Diplomacy.Propose(b, "A", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, T0);

            w.UtcNow = T0 + t.PeaceOfferTtl;   // exactly at the TTL: still open
            Pass(w, t, a, b, player, impr);
            Assert.True(a.Proposals.ContainsKey("B"));

            w.UtcNow = T0 + t.PeaceOfferTtl + TimeSpan.FromSeconds(1);
            Pass(w, t, a, b, player, impr);
            Assert.False(a.Proposals.ContainsKey("B"), "NPC-to-NPC truce offer lapses at PeaceOfferTtl");
            Assert.True(b.Proposals.ContainsKey("A"), "an alliance proposal keeps its own longer TTL");
            Assert.True(player.Proposals.ContainsKey("IMPR"), "a player's offer is on the player window");

            w.UtcNow = T0 + t.PactOfferTtl + TimeSpan.FromSeconds(1);
            Pass(w, t, a, b, player, impr);
            Assert.False(b.Proposals.ContainsKey("A"), "NPC-to-NPC alliance lapses at PactOfferTtl");
            Assert.True(player.Proposals.ContainsKey("IMPR"));

            w.UtcNow = T0 + t.PlayerOfferTtl;
            Pass(w, t, a, b, player, impr);
            Assert.True(player.Proposals.ContainsKey("IMPR"), "exactly at the player TTL: still open");

            w.UtcNow = T0 + t.PlayerOfferTtl + TimeSpan.FromSeconds(1);
            Pass(w, t, a, b, player, impr);
            Assert.False(player.Proposals.ContainsKey("IMPR"), "and it lapses once the day is up");
        }

        [Fact]
        public void ALeaderCanAnswerAnNpcPeaceOffer_HoursLater()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var npc = Npc(); var player = Player();
            player.Relations["IMPR"] = -1.0; npc.Relations["EXP"] = -0.8;

            w.UtcNow = T0;
            Diplomacy.OfferPeace(npc, "EXP", T0);
            Pass(w, t, npc, player);                      // the player has not answered: nothing concludes

            w.UtcNow = T0 + TimeSpan.FromHours(6);        // the leader logs in
            Pass(w, t, npc, player);
            Assert.True(npc.Proposals.ContainsKey("EXP"), "their offer is still standing");
            Assert.True(PlayerDiplomacy.OfferPeace(player, npc, t, w.UtcNow).Done);
            Pass(w, t, npc, player);

            Assert.True(Diplomacy.UnderTruce(player, "IMPR", w.UtcNow));
            Assert.Equal(t.TruceRelation, player.RelationTo("IMPR"));
        }

        [Fact]
        public void APlayersOffer_ConcludesWhenTheNpcTiresHoursLater()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var npc = Npc(); var player = Player();
            player.Relations["IMPR"] = -1.0; npc.Relations["EXP"] = -0.8;

            w.UtcNow = T0;
            Assert.True(PlayerDiplomacy.OfferPeace(player, npc, t, T0).Done);
            Pass(w, t, npc, player);
            Assert.False(Diplomacy.UnderTruce(player, "IMPR", w.UtcNow));

            w.UtcNow = T0 + TimeSpan.FromHours(3);
            npc.Fatigue["EXP"] = t.AcceptPeaceThreshold;  // the war has worn them down
            Pass(w, t, npc, player);
            Assert.True(Diplomacy.UnderTruce(player, "IMPR", w.UtcNow));
        }

        [Fact]
        public void APlayersAlliance_OutlivesThePactTtl_AndConcludesOnALateCounter()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var npc = Npc(); var player = Player();

            w.UtcNow = T0;
            Assert.True(PlayerDiplomacy.Propose(player, npc, DiplomacyVerb.Alliance, t, T0).Done);

            w.UtcNow = T0 + t.PactOfferTtl + TimeSpan.FromHours(1);
            Pass(w, t, npc, player);
            Assert.True(player.Proposals.ContainsKey("IMPR"));

            Diplomacy.Propose(npc, "EXP", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, w.UtcNow);
            Pass(w, t, npc, player);
            Assert.True(Diplomacy.InPactWith(player, "IMPR"));
        }

        /// <summary>Both sides' offers are hours old when the pass first sees them (a restart, or a
        /// long pass gap). The freshness test on the partner's offer must use the player window too,
        /// or neither direction finds the other willing.</summary>
        [Fact]
        public void TwoStandingOffers_HoursOld_StillMeet()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var npc = Npc(); var player = Player();
            player.Relations["IMPR"] = -1.0; npc.Relations["EXP"] = -0.8;
            Diplomacy.OfferPeace(npc, "EXP", T0);
            Diplomacy.OfferPeace(player, "IMPR", T0);

            w.UtcNow = T0 + TimeSpan.FromHours(2);
            Pass(w, t, npc, player);
            Assert.True(Diplomacy.UnderTruce(player, "IMPR", w.UtcNow));
        }

        [Fact]
        public void TwoStandingAllianceProposals_HoursOld_StillMeet()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var npc = Npc(); var player = Player();
            var alliance = new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } };
            Diplomacy.Propose(npc, "EXP", alliance, T0);
            Diplomacy.Propose(player, "IMPR", alliance, T0);

            w.UtcNow = T0 + TimeSpan.FromHours(2);
            Pass(w, t, npc, player);
            Assert.True(Diplomacy.InPactWith(player, "IMPR"));
        }

        [Fact]
        public void Proposals_SurviveARestart_AndAnEmptySetWritesNothing()
        {
            var st = new FactionState { Tag = "EXP", FactionId = 7, PersonalityId = "settler", RngSeed = 1, IsPlayerFaction = true };
            BrainDoc empty = FactionStore.ToBrainDoc(st, new SpatialNode[0], FactionStore.DefaultLogCap);
            Assert.Null(empty.Proposals);

            st.Proposals["IMPR"] = new DiplomacyMessage
            {
                Terms = new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce }, new DiplomacyTerm { Verb = DiplomacyVerb.NonAggression, Param = "5000" } },
                IssuedUtc = T0,
            };
            st.Proposals["METC"] = new DiplomacyMessage { Terms = new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, IssuedUtc = T0.AddHours(2) };

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, new SpatialNode[0], FactionStore.DefaultLogCap));
                FactionState back = FactionStore.ApplyBrainDoc(store.LoadBrain("EXP"), new SpatialGraph());

                Assert.Equal(2, back.Proposals.Count);
                DiplomacyMessage impr = back.Proposals["IMPR"];
                Assert.Equal(T0, impr.IssuedUtc);
                Assert.Equal(DateTimeKind.Utc, impr.IssuedUtc.Kind);
                Assert.Equal(DiplomacyVerb.Truce, impr.Terms[0].Verb);
                Assert.Null(impr.Terms[0].Param);
                Assert.Equal(DiplomacyVerb.NonAggression, impr.Terms[1].Verb);
                Assert.Equal("5000", impr.Terms[1].Param);
                Assert.Equal(DiplomacyVerb.Alliance, back.Proposals["METC"].Terms[0].Verb);
                Assert.Equal(T0.AddHours(2), back.Proposals["METC"].IssuedUtc);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
