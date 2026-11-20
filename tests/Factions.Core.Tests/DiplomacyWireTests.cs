using System;
using System.Collections.Generic;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The player-diplomacy messages (docs/client-plugin.md, Part 17) and the words the
    /// client draws from them. Additive kinds behind their own feature bit, so they ship without a
    /// ProtocolVersion bump — which makes round-tripping the only check that the two sides agree on
    /// their shape.</summary>
    public class DiplomacyWireTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static WireDiplomacyRow Full()
        {
            return new WireDiplomacyRow
            {
                NpcTag = "IMPR",
                TheirView = -0.63,
                TheirBand = WireStanceBand.Hostile,
                YourStance = 0.41,
                YourBand = WireStanceBand.Friendly,
                EmbargoByYou = true,
                EmbargoByThem = true,
                TradeBarred = true,
                Allied = true,
                Implacable = true,
                TruceUntilTicks = Now.AddMinutes(90).Ticks,
                YouOffered = "Alliance",
                YouOfferedExpiresTicks = Now.AddMinutes(20).Ticks,
                TheyOffered = "Truce",
                TheyOfferedExpiresTicks = Now.AddMinutes(7).Ticks,
                GiftReadyTicks = Now.AddMinutes(3).Ticks,
            };
        }

        [Fact]
        public void DiplomacyRequest_RoundTrips()
        {
            bool stream;
            Assert.True(FactionsWire.TryReadDiplomacyRequest(FactionsWire.WriteDiplomacyRequest(true), out stream));
            Assert.True(stream);
            Assert.True(FactionsWire.TryReadDiplomacyRequest(FactionsWire.WriteDiplomacyRequest(false), out stream));
            Assert.False(stream);
        }

        [Fact]
        public void DiplomacySnapshot_RoundTripsEveryFieldOfEveryRow()
        {
            // The all-set row beside an all-default one: a writer that skipped a field passes with
            // one of them and fails with the pair.
            var rows = new List<WireDiplomacyRow>
            {
                Full(),
                new WireDiplomacyRow
                {
                    NpcTag = "SPRT", TheirView = 0, TheirBand = WireStanceBand.Neutral,
                    YourStance = 0, YourBand = WireStanceBand.Neutral, YouOffered = "", TheyOffered = "",
                },
            };

            byte[] data = FactionsWire.WriteDiplomacySnapshot("EXP", true, 50000, 123456, Now.Ticks, rows);

            string tag; bool leader; long cost, treasury, now; List<WireDiplomacyRow> read;
            Assert.True(FactionsWire.TryReadDiplomacySnapshot(data, out tag, out leader, out cost, out treasury, out now, out read));
            Assert.Equal("EXP", tag);
            Assert.True(leader);
            Assert.Equal(50000L, cost);
            Assert.Equal(123456L, treasury);
            Assert.Equal(Now.Ticks, now);
            Assert.Equal(2, read.Count);

            WireDiplomacyRow a = read[0], e = Full();
            Assert.Equal(e.NpcTag, a.NpcTag);
            Assert.Equal(e.TheirView, a.TheirView);
            Assert.Equal(e.TheirBand, a.TheirBand);
            Assert.Equal(e.YourStance, a.YourStance);
            Assert.Equal(e.YourBand, a.YourBand);
            Assert.True(a.EmbargoByYou);
            Assert.True(a.EmbargoByThem);
            Assert.True(a.TradeBarred);
            Assert.True(a.Allied);
            Assert.True(a.Implacable);
            Assert.Equal(e.TruceUntilTicks, a.TruceUntilTicks);
            Assert.Equal("Alliance", a.YouOffered);
            Assert.Equal(e.YouOfferedExpiresTicks, a.YouOfferedExpiresTicks);
            Assert.Equal("Truce", a.TheyOffered);
            Assert.Equal(e.TheyOfferedExpiresTicks, a.TheyOfferedExpiresTicks);
            Assert.Equal(e.GiftReadyTicks, a.GiftReadyTicks);

            WireDiplomacyRow b = read[1];
            Assert.Equal("SPRT", b.NpcTag);
            Assert.Equal(WireStanceBand.Neutral, b.TheirBand);
            Assert.False(b.EmbargoByYou);
            Assert.False(b.TradeBarred);
            Assert.Equal(0L, b.TruceUntilTicks);
            Assert.Equal("", b.YouOffered);
            Assert.Equal(0L, b.GiftReadyTicks);
        }

        [Fact]
        public void DiplomacySnapshot_NoFaction_IsAnEmptyAnswer()
        {
            byte[] data = FactionsWire.WriteDiplomacySnapshot("", false, 0, 0, 0, null);
            string tag; bool leader; long cost, treasury, now; List<WireDiplomacyRow> read;
            Assert.True(FactionsWire.TryReadDiplomacySnapshot(data, out tag, out leader, out cost, out treasury, out now, out read));
            Assert.Equal("", tag);
            Assert.False(leader);
            Assert.Empty(read);
        }

        [Fact]
        public void DiplomacySnapshot_ThatDiffersOnlyInTheClock_DiffersOnlyInThoseBytes()
        {
            // The server's change detection compares a zero-clock copy; this pins that the clock is
            // the one field outside the rows, so zeroing it leaves every row byte comparable.
            var rows = new List<WireDiplomacyRow> { Full() };
            byte[] a = FactionsWire.WriteDiplomacySnapshot("EXP", true, 1, 2, 0L, rows);
            byte[] b = FactionsWire.WriteDiplomacySnapshot("EXP", true, 1, 2, Now.Ticks, rows);
            Assert.Equal(a.Length, b.Length);
            int differing = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) differing++;
            Assert.InRange(differing, 1, 8);
        }

        [Fact]
        public void DiplomacyCommandRequest_RoundTripsEveryAct()
        {
            foreach (WireDiplomacyAct act in Enum.GetValues(typeof(WireDiplomacyAct)))
            {
                WireDiplomacyAct read; string tag;
                Assert.True(FactionsWire.TryReadDiplomacyCommandRequest(
                    FactionsWire.WriteDiplomacyCommandRequest(act, "IMPR"), out read, out tag));
                Assert.Equal(act, read);
                Assert.Equal("IMPR", tag);
            }
        }

        [Fact]
        public void DiplomacyCommandRequest_UnknownAct_IsRefused()
        {
            byte[] data = FactionsWire.WriteDiplomacyCommandRequest(WireDiplomacyAct.Gift, "IMPR");
            data[6] = 42;   // two-byte kind + int version, then the act
            WireDiplomacyAct read; string tag;
            Assert.False(FactionsWire.TryReadDiplomacyCommandRequest(data, out read, out tag));
        }

        [Fact]
        public void DiplomacyCommandResult_RoundTrips()
        {
            bool done; string tag, message;
            Assert.True(FactionsWire.TryReadDiplomacyCommandResult(
                FactionsWire.WriteDiplomacyCommandResult(true, "IMPR", "[EXP] embargoes IMPR."), out done, out tag, out message));
            Assert.True(done);
            Assert.Equal("IMPR", tag);
            Assert.Equal("[EXP] embargoes IMPR.", message);
        }

        [Fact]
        public void ADiplomacyMessage_IsNotReadableAsAShelfMessage()
        {
            byte[] result = FactionsWire.WriteDiplomacyCommandResult(true, "IMPR", "x");
            bool ok; string message;
            Assert.False(FactionsWire.TryReadPlayerShelfCommandResult(result, out ok, out message));
        }

        [Fact]
        public void WireActs_MirrorCoreLeaderActs_ByNameAndOrdinal()
        {
            // The host maps by name; this keeps the two enums from drifting apart unnoticed.
            Assert.Equal(Enum.GetValues(typeof(LeaderAct)).Length, Enum.GetValues(typeof(WireDiplomacyAct)).Length);
            foreach (LeaderAct act in Enum.GetValues(typeof(LeaderAct)))
            {
                WireDiplomacyAct wire = (WireDiplomacyAct)Enum.Parse(typeof(WireDiplomacyAct), act.ToString());
                Assert.Equal((int)act, (int)wire);
            }
            foreach (StanceBand band in Enum.GetValues(typeof(StanceBand)))
            {
                WireStanceBand wire = (WireStanceBand)Enum.Parse(typeof(WireStanceBand), band.ToString());
                Assert.Equal((int)band, (int)wire);
            }
        }

        [Fact]
        public void DiplomacyFeature_IsItsOwnBit()
        {
            Assert.Equal(1L << 20, (long)WireFeatures.Diplomacy);
            Assert.Equal(WireFeatures.None, WireFeatures.Diplomacy & WireFeatures.DockSupply);
        }

        // ---- the words ---------------------------------------------------------------------

        [Fact]
        public void StanceLine_ReadsBothDirectionsEmbargoAndTruce_InAscii()
        {
            WireDiplomacyRow row = Full();
            string line = DiplomacyText.StanceLine(row, Now.Ticks);
            Assert.Equal("IMPR -> you: Hostile -0.63 | you -> IMPR: Friendly +0.41 | embargo: both ways"
                         + " | truce: 1 h 30 min | allied | trade barred", line);
            foreach (char c in line) Assert.True(c < 128, "non-ASCII glyph in the stance line: " + (int)c);

            var quiet = new WireDiplomacyRow { NpcTag = "SPRT", TheirBand = WireStanceBand.Neutral, YourBand = WireStanceBand.Neutral };
            Assert.Equal("SPRT -> you: Neutral 0.00 | you -> SPRT: Neutral 0.00 | embargo: none | truce: none",
                         DiplomacyText.StanceLine(quiet, Now.Ticks));
        }

        [Fact]
        public void EmbargoText_NamesWhoHoldsIt()
        {
            var row = new WireDiplomacyRow { NpcTag = "IMPR" };
            Assert.Equal("none", DiplomacyText.EmbargoText(row));
            row.EmbargoByThem = true;
            Assert.Equal("by IMPR", DiplomacyText.EmbargoText(row));
            row.EmbargoByThem = false; row.EmbargoByYou = true;
            Assert.Equal("by you", DiplomacyText.EmbargoText(row));
        }

        [Fact]
        public void Remaining_CountsAgainstTheGivenClock()
        {
            Assert.Equal("", DiplomacyText.Remaining(0, Now.Ticks));
            Assert.Equal("now", DiplomacyText.Remaining(Now.AddSeconds(-1).Ticks, Now.Ticks));
            Assert.Equal("30 s", DiplomacyText.Remaining(Now.AddSeconds(30).Ticks, Now.Ticks));
            Assert.Equal("42 min", DiplomacyText.Remaining(Now.AddMinutes(42).Ticks, Now.Ticks));
            Assert.Equal("2 h 05 min", DiplomacyText.Remaining(Now.AddMinutes(125).Ticks, Now.Ticks));
        }

        [Fact]
        public void Offers_ReadBothWays_WithPlayerWordsForCoreTerms()
        {
            WireDiplomacyRow row = Full();
            Assert.Equal("You offered IMPR: alliance (expires in 20 min)", DiplomacyText.OffersLine(row, Now.Ticks, true));
            Assert.Equal("IMPR offered you: peace (expires in 7 min)", DiplomacyText.OffersLine(row, Now.Ticks, false));
            Assert.Equal("non-aggression", DiplomacyText.OfferName("NonAggression"));

            row.YouOffered = ""; row.TheyOffered = "";
            Assert.Equal("You have no offer out to IMPR.", DiplomacyText.OffersLine(row, Now.Ticks, true));
            Assert.Equal("IMPR has no offer out to you.", DiplomacyText.OffersLine(row, Now.Ticks, false));

            string tip = DiplomacyText.Tooltip(Full(), Now.Ticks);
            Assert.Contains("You offered IMPR: alliance", tip);
            Assert.Contains("IMPR offered you: peace", tip);
        }

        [Fact]
        public void GiftLine_SaysCostAndWhatIsInTheWay()
        {
            var row = new WireDiplomacyRow { NpcTag = "IMPR" };
            Assert.Equal("Gift: 50,000 credits from the faction treasury (80,000 held).",
                         DiplomacyText.GiftLine(row, 50000, 80000, Now.Ticks));
            Assert.EndsWith("The treasury cannot pay it.", DiplomacyText.GiftLine(row, 50000, 100, Now.Ticks));
            row.GiftReadyTicks = Now.AddMinutes(3).Ticks;
            Assert.EndsWith("The next one can go in 3 min.", DiplomacyText.GiftLine(row, 50000, 80000, Now.Ticks));
            row.Implacable = true;
            Assert.Equal("IMPR cannot be swayed by gifts.", DiplomacyText.GiftLine(row, 50000, 80000, Now.Ticks));
        }
    }
}
