using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Raid hold damage is the arrival duel's outcome, and the conquer ceiling is tested
    /// against what a conquest's own attack left behind rather than against what it found.</summary>
    public class RaidHoldStripTests
    {
        private static SpatialNode AddOwned(WorldModel w, string id, string owner, double hold)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            return n;
        }

        // ── the formula ──────────────────────────────────────────────────────

        [Fact]
        public void RaidHoldStrip_ScalesTheMaxByTheSurvivingRatioOnTheShippedCurve()
        {
            FactionTuning t = TestSupport.Tuning();

            Assert.Equal(0.35, t.RaidHoldStripMax);
            Assert.Equal(1.5, t.RaidHoldStripExponent);

            Assert.Equal(0.35, t.RaidHoldStrip(1.00), 10);
            Assert.Equal(0.35 * Math.Pow(0.50, 1.5), t.RaidHoldStrip(0.50), 10);   // 0.12374...
            Assert.Equal(0.35 * Math.Pow(0.30, 1.5), t.RaidHoldStrip(0.30), 10);   // 0.05750...
            Assert.Equal(0.0, t.RaidHoldStrip(0.0));
            Assert.Equal(0.0, t.RaidHoldStrip(-1.0));
        }

        /// <summary>The two ratios the ticket calibrates against, to two places — the numbers an
        /// operator reads off the curve when retuning it.</summary>
        [Fact]
        public void RaidHoldStrip_CleanRaidStripsAThirdAndAMauledOneStripsAlmostNothing()
        {
            FactionTuning t = TestSupport.Tuning();

            Assert.Equal(0.35, Math.Round(t.RaidHoldStrip(1.00), 2));
            Assert.Equal(0.06, Math.Round(t.RaidHoldStrip(0.30), 2));
        }

        [Fact]
        public void RaidHoldStrip_ClampsARatioAboveOne()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(t.RaidHoldStrip(1.0), t.RaidHoldStrip(4.0), 10);
        }

        // ── Raid ─────────────────────────────────────────────────────────────

        [Fact]
        public void Raid_AtFullRatio_StripsTheMaxFromAFullHoldNode()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "BETA", hold: 1.0);

            w.Raid("ALFA", "N001", 1.0);

            Assert.Equal(0.65, w.Graph.Nodes["N001"].HoldStrength, 10);
        }

        [Fact]
        public void Raid_ThreeCleanRaids_TakeAFullHoldNodeToTheConquerCeiling()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);

            w.Raid("ALFA", "N001", 1.0);
            Assert.True(n.HoldStrength > w.Tuning.ConquerHoldCeiling);
            w.Raid("ALFA", "N001", 1.0);
            Assert.True(n.HoldStrength > w.Tuning.ConquerHoldCeiling);
            w.Raid("ALFA", "N001", 1.0);

            Assert.Equal(0.0, n.HoldStrength, 10);   // 1.0 - 3 x 0.35, clamped at 0
            Assert.True(n.HoldStrength <= w.Tuning.ConquerHoldCeiling);
        }

        [Fact]
        public void Raid_AtAMauledRatio_StripsProportionallyLess()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "BETA", hold: 1.0);

            w.Raid("ALFA", "N001", 0.30);

            Assert.Equal(1.0 - 0.35 * Math.Pow(0.30, 1.5), w.Graph.Nodes["N001"].HoldStrength, 10);
        }

        // ── Conquer: damage first, then the ceiling ──────────────────────────

        [Fact]
        public void Conquer_AtFullRatio_CapturesANodeItsOwnAttackStripsPastTheCeiling()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            w.Tuning.ConquerCapturedHoldStrength = 0.3;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 0.30);

            string victim = w.Conquer("ALFA", "N001", 1.0);

            Assert.Equal("BETA", victim);
            Assert.Equal("ALFA", n.OwnerFactionTag);                     // 0.30 - 0.35 <= 0.15
            Assert.Equal(0.3, n.HoldStrength, 10);                       // CaptureNode's own value
        }

        [Fact]
        public void Conquer_AtFullRatio_OnlySoftensAFullHoldNode()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);

            w.Conquer("ALFA", "N001", 1.0);

            Assert.Equal("BETA", n.OwnerFactionTag);
            Assert.Equal(0.65, n.HoldStrength, 10);
        }

        [Fact]
        public void Conquer_ThatOnlySoftens_RecordsExactlyOneConquerTaggedIncident()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            AddOwned(w, "N001", "BETA", hold: 1.0);

            w.Conquer("ALFA", "N001", 1.0);

            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.Equal(ActionKind.Conquer, inc.Kind);
            Assert.Equal("ALFA", inc.Attacker);
            Assert.Equal("BETA", inc.Victim);
            Assert.Equal("N001", inc.NodeId);
        }

        [Fact]
        public void Conquer_ThatCaptures_RecordsExactlyOneConquerTaggedIncident()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            AddOwned(w, "N001", "BETA", hold: 0.30);

            w.Conquer("ALFA", "N001", 1.0);

            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.Equal(ActionKind.Conquer, inc.Kind);
            Assert.Equal("ALFA", inc.Attacker);
        }

        // ── one damage function for players and NPCs ─────────────────────────

        [Fact]
        public void ResolvePlayerRaid_StripsWhatACleanNpcRaidStrips()
        {
            var wPlayer = TestSupport.World();
            AddOwned(wPlayer, "N001", "BETA", hold: 1.0);
            var wNpc = TestSupport.World();
            AddOwned(wNpc, "N001", "BETA", hold: 1.0);

            string victim = wPlayer.ResolvePlayerRaid("N001");
            wNpc.Raid("ALFA", "N001", 1.0);

            Assert.Equal("BETA", victim);
            Assert.Equal(0.65, wPlayer.Graph.Nodes["N001"].HoldStrength, 10);
            Assert.Equal(wNpc.Graph.Nodes["N001"].HoldStrength,
                         wPlayer.Graph.Nodes["N001"].HoldStrength, 10);
        }

        // ── one capture gate for players and NPCs ────────────────────────────

        [Fact]
        public void ConquerOfferDue_IsRefusedWhileHoldIsAboveTheCeiling_HoweverLongTheTimerHasRun()
        {
            FactionTuning t = TestSupport.Tuning();
            t.ConquerHoldCeiling = 0.15;
            t.PlayerConquerOfferMinutes = 10.0;
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var n = new SpatialNode { Id = "N001", OwnerFactionTag = "BETA", HoldStrength = 0.65 };
            n.PlayerRaidSucceededUtc = t0;

            Assert.False(PlayerSiege.ConquerOfferDue(n, t, t0.AddMinutes(10)));
            Assert.False(PlayerSiege.ConquerOfferDue(n, t, t0.AddHours(24)));

            n.HoldStrength = 0.15;
            Assert.True(PlayerSiege.ConquerOfferDue(n, t, t0.AddMinutes(10)));
        }

        [Fact]
        public void ConquerOfferDue_StillRequiresTheTimer_OnceHoldIsAtTheCeiling()
        {
            FactionTuning t = TestSupport.Tuning();
            t.ConquerHoldCeiling = 0.15;
            t.PlayerConquerOfferMinutes = 10.0;
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var n = new SpatialNode { Id = "N001", OwnerFactionTag = "BETA", HoldStrength = 0.10 };
            n.PlayerRaidSucceededUtc = t0;

            Assert.False(PlayerSiege.ConquerOfferDue(n, t, t0.AddMinutes(9.9)));
            Assert.True(PlayerSiege.ConquerOfferDue(n, t, t0.AddMinutes(10)));
        }

        // ── sustained player force is sustained hold loss ────────────────────

        /// <summary>A breached station under continuous fire strips once per
        /// <c>PlayerRaidBlockDamageForSuccess</c>, and three thresholds' worth in one unbroken siege
        /// takes a full-hold node to the offer gate — the whole sequence, not each step alone.</summary>
        [Fact]
        public void RecordRaidDamage_FiresOncePerThreshold_AndThreeThresholdsOpenTheOffer()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            w.Tuning.PlayerConquerOfferMinutes = 10.0;
            w.Tuning.PlayerRaidBlockDamageForSuccess = 20000.0;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);
            n.StableStationId = 77;
            n.ZoneBreachedUtc = w.UtcNow;

            int strips = 0;
            for (int i = 0; i < 30; i++)   // 30 x 2,000 = 3 thresholds
            {
                if (!PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 2000.0, false)) continue;
                strips++;
                w.ResolvePlayerRaid("N001");
            }

            Assert.Equal(3, strips);
            Assert.Equal(0.0, n.HoldStrength, 10);                    // 1.0 - 3 x 0.35, clamped
            Assert.True(PlayerSiege.ConquerOfferDue(n, w.Tuning, w.UtcNow.AddMinutes(10)));
        }

        [Fact]
        public void RecordRaidDamage_CarriesOverkillIntoTheNextStrip()
        {
            var w = TestSupport.World();
            w.Tuning.PlayerRaidBlockDamageForSuccess = 20000.0;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);
            n.StableStationId = 77;
            n.ZoneBreachedUtc = w.UtcNow;

            Assert.True(PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 30000.0, false));
            Assert.Equal(10000.0, n.PlayerRaidBlockDamage);           // banked, not zeroed

            Assert.True(PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 10000.0, false));
            Assert.Equal(0.0, n.PlayerRaidBlockDamage);
        }

        [Fact]
        public void RecordRaidDamage_PoweredDown_FiresExactlyOnce_HoweverManyFlushesSeeIt()
        {
            var w = TestSupport.World();
            w.Tuning.PlayerRaidBlockDamageForSuccess = 20000.0;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);
            n.StableStationId = 77;
            n.ZoneBreachedUtc = w.UtcNow;

            int strips = 0;
            for (int i = 0; i < 10; i++)
                if (PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 0.0, true)) strips++;

            Assert.Equal(1, strips);
        }

        /// <summary>The strip max is cut to 0.1 so three strips land at 0.7 and the count is read
        /// off hold exactly — at the shipped 0.35 the third strip clamps at 0 and a fourth would be
        /// indistinguishable from it.</summary>
        [Fact]
        public void MarkPlayerPresenceAttack_StripsOncePerDwellIntervalHeld()
        {
            var w = TestSupport.World();
            w.Tuning.RaidZoneSiegeDwellSeconds = 300.0;
            w.Tuning.RaidHoldStripMax = 0.1;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);
            DateTime t0 = w.UtcNow;

            Assert.True(w.BeginPresenceRaid("N001"));                 // opening strip
            for (int sec = 0; sec < 900; sec += 60)                   // three dwell intervals held
            {
                w.UtcNow = t0.AddSeconds(sec);
                w.MarkPlayerPresenceAttack("N001");
            }

            Assert.Equal(0.7, n.HoldStrength, 10);                    // opening, +300 s, +600 s
        }

        [Fact]
        public void MarkPlayerPresenceAttack_HeldLessThanOneInterval_StripsOnlyTheOpeningOne()
        {
            var w = TestSupport.World();
            w.Tuning.RaidZoneSiegeDwellSeconds = 300.0;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);
            DateTime t0 = w.UtcNow;

            Assert.True(w.BeginPresenceRaid("N001"));
            for (int sec = 0; sec < 300; sec += 30)
            {
                w.UtcNow = t0.AddSeconds(sec);
                w.MarkPlayerPresenceAttack("N001");
            }

            Assert.Equal(0.65, n.HoldStrength, 10);   // a second strip would read 0.30
        }

        /// <summary>The offer timer is anchored on the FIRST success, so an attacker who keeps
        /// working cannot push their own offer away.</summary>
        [Fact]
        public void LossDeadline_DoesNotMove_WhenALaterStripFires()
        {
            var w = TestSupport.World();
            w.Tuning.PlayerConquerOfferMinutes = 10.0;
            w.Tuning.PlayerRaidBlockDamageForSuccess = 20000.0;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);
            n.StableStationId = 77;
            n.ZoneBreachedUtc = w.UtcNow;

            Assert.True(PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 20000.0, false));
            DateTime deadlineAfterFirst = PlayerSiege.LossDeadlineUtc(n, w.Tuning);
            DateTime succeededAfterFirst = n.PlayerRaidSucceededUtc;

            w.UtcNow = w.UtcNow.AddMinutes(5);
            Assert.True(PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 20000.0, false));

            Assert.Equal(succeededAfterFirst, n.PlayerRaidSucceededUtc);
            Assert.Equal(deadlineAfterFirst, PlayerSiege.LossDeadlineUtc(n, w.Tuning));
        }

        /// <summary>Three successful player raids reach the ceiling the offer gate now reads, which
        /// is the player-side equivalent of the three-raid NPC campaign above.</summary>
        [Fact]
        public void ResolvePlayerRaid_ThreeVisits_BringANodeWithinTheOfferGate()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            w.Tuning.PlayerConquerOfferMinutes = 10.0;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);
            n.PlayerRaidSucceededUtc = w.UtcNow;

            w.ResolvePlayerRaid("N001");
            Assert.False(PlayerSiege.ConquerOfferDue(n, w.Tuning, w.UtcNow.AddHours(1)));
            w.ResolvePlayerRaid("N001");
            Assert.False(PlayerSiege.ConquerOfferDue(n, w.Tuning, w.UtcNow.AddHours(1)));
            w.ResolvePlayerRaid("N001");

            Assert.True(PlayerSiege.ConquerOfferDue(n, w.Tuning, w.UtcNow.AddHours(1)));
        }
    }
}
