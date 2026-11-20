using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The HUD status panel: what a siege or occupation shows there, that a ticking countdown is not
    /// a change worth sending, the wire shape, and that an owner abandoning the node ends the raid.
    /// </summary>
    public class StatusPanelTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 15, 19, 49, 51, DateTimeKind.Utc);

        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                RaidZoneIntegrityPool = 1000.0,
                RaidZoneIntegrityRegenSeconds = 30.0,
                RaidZoneRaidLapseSeconds = 30.0,
                RaidZoneSiegeDwellSeconds = 300.0,
                PlayerRaidBlockDamageForSuccess = 20000.0,
                PlayerConquerOfferMinutes = 10.0,
                ConquerHoldCeiling = 0.3,
                ObservationRadiusKm = 15.0,
            };
        }

        private static SpatialNode PresenceRaid(FactionTuning t, out WorldModel w)
        {
            w = TestSupport.World();
            w.Tuning = t;
            w.UtcNow = T0;
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.7, "N098");
            n.OwnerFactionTag = "STCG";
            n.HoldStrength = 1.0;
            Assert.True(w.BeginPresenceRaid("N098"));
            return n;
        }

        private static string Texts(StatusPanelItem item)
        {
            var parts = new List<string>();
            foreach (StatusPanelLine line in item.Lines) parts.Add(line.Text);
            return string.Join(" | ", parts);
        }

        [Fact]
        public void PresencePanel_HeadsWithTheNode_AndCountsDownTheStripAndTheOffer()
        {
            FactionTuning t = Tuning();
            WorldModel w;
            SpatialNode n = PresenceRaid(t, out w);

            StatusPanelItem item = PlayerSiegeNotice.PresencePanel(n, t, T0.AddSeconds(100));
            Assert.Equal("siege:N098", item.Key);
            Assert.Equal(StatusPanelTone.Pending, item.Tone);
            Assert.Equal("OCCUPYING  N098 (STCG)", item.Lines[0].Text);
            Assert.Contains("hold 65%, needs 30% or less", Texts(item));
            Assert.Contains("next strip in {clock} (1 left)", Texts(item));
            Assert.Contains("offer in {clock}", Texts(item));
            Assert.Contains("stay within 3.5 km", Texts(item));

            // The deadlines are the state machine's own: the next strip a dwell after the opening
            // one, the offer the offer wait after the raid began.
            Assert.Contains(item.Lines, l => l.CountdownTo == T0.AddSeconds(300));
            Assert.Contains(item.Lines, l => l.CountdownTo == T0.AddMinutes(10));
        }

        /// <summary>The whole reason for deadlines: a second passing changes nothing the player is
        /// sent. The old five-second notice changed text on every send.</summary>
        [Fact]
        public void APassingSecond_IsNotAChange_ButAStripIs()
        {
            FactionTuning t = Tuning();
            WorldModel w;
            SpatialNode n = PresenceRaid(t, out w);

            long at100 = PlayerSiegeNotice.PresencePanel(n, t, T0.AddSeconds(100)).Digest();
            long at101 = PlayerSiegeNotice.PresencePanel(n, t, T0.AddSeconds(101)).Digest();
            Assert.Equal(at100, at101);

            w.UtcNow = T0.AddSeconds(300);
            w.MarkPlayerPresenceAttack("N098");      // the second strip lands
            long afterStrip = PlayerSiegeNotice.PresencePanel(n, t, T0.AddSeconds(301)).Digest();
            Assert.NotEqual(at101, afterStrip);
        }

        [Fact]
        public void PresencePanel_BothMet_IsReady_AndSaysToReplyYes()
        {
            FactionTuning t = Tuning();
            WorldModel w;
            SpatialNode n = PresenceRaid(t, out w);
            n.HoldStrength = 0.1;

            StatusPanelItem item = PlayerSiegeNotice.PresencePanel(n, t, T0.AddMinutes(10).AddSeconds(1));
            Assert.Equal(StatusPanelTone.Ready, item.Tone);
            Assert.Contains("reply \"yes\" in chat", Texts(item));
        }

        [Fact]
        public void StationPanel_CountsDownToTheBreach_AndHasNoPresenceShape()
        {
            FactionTuning t = Tuning();
            var n = new SpatialNode
            {
                Id = "N105", OwnerFactionTag = "UNGD", ZoneIntegrity = -1, StableStationId = 77, HoldStrength = 0.8,
            };
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);

            StatusPanelItem item = PlayerSiegeNotice.StatusPanel(n, t, T0.AddSeconds(10));
            Assert.Equal("SIEGE  N105 (UNGD)", item.Lines[0].Text);
            Assert.Contains("shield 35%", Texts(item));     // a third back, in steps of five
            Assert.Contains(item.Lines, l => l.Text.Contains("{clock} to break it") && l.CountdownTo == T0.AddSeconds(300));
            Assert.Null(PlayerSiegeNotice.PresencePanel(n, t, T0.AddSeconds(10)));
        }

        [Fact]
        public void NoRaid_HasNoPanel()
        {
            var n = new SpatialNode { Id = "N1", OwnerFactionTag = "UNGD", ZoneIntegrity = -1 };
            Assert.Null(PlayerSiegeNotice.PresencePanel(n, Tuning(), T0));
            Assert.Null(PlayerSiegeNotice.StatusPanel(n, Tuning(), T0));
        }

        // ---- abandonment ---------------------------------------------------------------------

        /// <summary>N098, 2026-10-15: STCG withdrew from the node two minutes before the offer was
        /// due. The raid ran on against the neutral owner until a claim reset it.</summary>
        [Fact]
        public void AnOwnerWithdrawingFromAnOccupiedNode_EndsTheOccupation()
        {
            FactionTuning t = Tuning();
            WorldModel w;
            SpatialNode n = PresenceRaid(t, out w);

            w.UtcNow = T0.AddMinutes(8);
            w.Withdraw("STCG", "N098");

            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
            Assert.False(PlayerSiege.IsPresenceRaid(n));
            Assert.Equal(default(DateTime), n.PlayerRaidSucceededUtc);
            Assert.Null(PlayerSiegeNotice.PresencePanel(n, t, w.UtcNow));
        }

        [Fact]
        public void AbandonedUnderRaid_NamesThePriorOwner_AndTheClaim()
        {
            string text = PlayerSiegeNotice.AbandonedUnderRaid("N098", "STCG");
            Assert.StartsWith("STCG abandoned N098", text);
            Assert.Contains("claim offer", text);
        }

        // ---- wire ------------------------------------------------------------------------------

        [Fact]
        public void Wire_RoundTripsItemsLinesAndCountdowns()
        {
            var sent = new List<WireStatusPanelItem>
            {
                new WireStatusPanelItem
                {
                    Key = "siege:N098", Tone = 1,
                    Lines = new[]
                    {
                        new WireStatusPanelLine { Text = "OCCUPYING  N098 (STCG)", CountdownSeconds = -1f },
                        new WireStatusPanelLine { Text = "offer in {clock}", CountdownSeconds = 125.5f },
                    },
                },
            };

            List<WireStatusPanelItem> read;
            Assert.True(FactionsWire.TryReadStatusPanel(FactionsWire.WriteStatusPanel(sent), out read));
            Assert.Single(read);
            Assert.Equal("siege:N098", read[0].Key);
            Assert.Equal(1, read[0].Tone);
            Assert.Equal(2, read[0].Lines.Length);
            Assert.Equal(-1f, read[0].Lines[0].CountdownSeconds);
            Assert.Equal("offer in {clock}", read[0].Lines[1].Text);
            Assert.Equal(125.5f, read[0].Lines[1].CountdownSeconds);
        }

        [Fact]
        public void Wire_AnEmptyPanelIsAClear()
        {
            List<WireStatusPanelItem> read;
            Assert.True(FactionsWire.TryReadStatusPanel(FactionsWire.WriteStatusPanel(new List<WireStatusPanelItem>()), out read));
            Assert.Empty(read);
        }

        [Fact]
        public void Wire_RefusesAnotherKind()
        {
            List<WireStatusPanelItem> read;
            Assert.False(FactionsWire.TryReadStatusPanel(FactionsWire.WriteSignatureStatus(1.0), out read));
        }
    }
}
