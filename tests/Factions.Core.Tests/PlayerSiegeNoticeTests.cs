using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// What a besieging player is told. The status line reads the siege's own clocks, so each phase
    /// is driven through <see cref="PlayerSiege"/> rather than by setting fields, and the numbers in
    /// the text are the ones the state machine will act on.
    /// </summary>
    public class PlayerSiegeNoticeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 8, 19, 55, 48, DateTimeKind.Utc);

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

        private static SpatialNode Node()
        {
            return new SpatialNode
            {
                Id = "N105", OwnerFactionTag = "UNGD", ZoneIntegrity = -1, StableStationId = 77,
                HoldStrength = 0.8,
            };
        }

        [Fact]
        public void NoSiege_HasNoStatus()
        {
            Assert.Null(PlayerSiegeNotice.Status(Node(), Tuning(), T0));
        }

        [Fact]
        public void Began_StatesTheDwellTheRegenAndTheLapse()
        {
            string text = PlayerSiegeNotice.Began(Node(), Tuning());
            Assert.Contains("UNGD's station at N105", text);
            Assert.Contains("5:00", text);
            Assert.Contains("30 s", text);
        }

        [Fact]
        public void EmptiedPool_CountsDownToTheBreach()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);

            string text = PlayerSiegeNotice.Status(n, t, T0.AddSeconds(10));
            // 10 s into a 30 s refill the pool is a third back; 290 s of dwell remain.
            Assert.Contains("shield 33%", text);
            Assert.Contains("4:50 more", text);
        }

        [Fact]
        public void DwellServed_WithShieldUp_SaysToEmptyItNow()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);
            PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(299), 5000);   // one second short

            string text = PlayerSiegeNotice.Status(n, t, T0.AddSeconds(301));
            Assert.Contains("shield 7%", text);
            Assert.Contains("Empty it now", text);
        }

        /// <summary>The live case this exists for: one burst, then nothing. The status must say the
        /// siege is about to be lost while the lapse clock runs, with the time left on it.</summary>
        [Fact]
        public void RecoveredShield_WarnsThatTheSiegeIsAboutToLapse()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);

            DateTime full = T0.AddSeconds(30);
            Assert.False(PlayerSiege.Maintain(n, t, full, false));        // starts the lapse clock
            string text = PlayerSiegeNotice.Status(n, t, full.AddSeconds(12));
            Assert.Contains("recovered", text);
            Assert.Contains("18 s", text);

            Assert.True(PlayerSiege.Maintain(n, t, full.AddSeconds(30), false));
            Assert.Null(PlayerSiegeNotice.Status(n, t, full.AddSeconds(30)));
        }

        [Fact]
        public void Breached_ReportsStationDamageAgainstTheThreshold()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);
            Assert.True(PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(300), 5000).Breached);
            PlayerSiege.RecordRaidDamage(n, t, T0.AddSeconds(310), 4500, false);

            string text = PlayerSiegeNotice.Status(n, t, T0.AddSeconds(311));
            Assert.Contains("Shield DOWN", text);
            Assert.Contains("4,500 / 20,000", text);
        }

        [Fact]
        public void Raided_AboveTheCeiling_AsksForMoreDamage_ThenCountsDownTheOffer()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);
            Assert.True(PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(300), 5000).Breached);
            DateTime won = T0.AddSeconds(320);
            Assert.True(PlayerSiege.RecordRaidDamage(n, t, won, 20000, false));

            string early = PlayerSiegeNotice.Status(n, t, won.AddSeconds(1));
            Assert.Contains("needs 30% or less", early);
            Assert.Contains("wait 9:59 more", early);

            n.HoldStrength = 0.2;
            string waiting = PlayerSiegeNotice.Status(n, t, won.AddMinutes(1));
            Assert.Contains("hold 20%, done", waiting);
            Assert.Contains("wait 9:00 more", waiting);
            Assert.Contains("\"yes\"", PlayerSiegeNotice.Status(n, t, won.AddMinutes(10)));
        }

        /// <summary>The live complaint (N096, 2026-10-11): the player was told first about the wait and
        /// then about the hold, as if the goal had moved. Both conditions are named on every line.</summary>
        [Fact]
        public void Raided_NamesBothConditions_WhicheverIsStillOpen()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);
            Assert.True(PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(300), 5000).Breached);
            DateTime won = T0.AddSeconds(320);
            Assert.True(PlayerSiege.RecordRaidDamage(n, t, won, 24500, false));

            // Hold 80% against a 30% ceiling at 35% per round: two rounds, the next 4,500 in.
            string bothOpen = PlayerSiegeNotice.Status(n, t, won.AddMinutes(2));
            Assert.Contains("hold 80%, needs 30% or less (about 2 more rounds; next 4,500 / 20,000 damage)", bothOpen);
            Assert.Contains("wait 8:00 more", bothOpen);

            string onlyHoldOpen = PlayerSiegeNotice.Status(n, t, won.AddMinutes(11));
            Assert.Contains("needs 30% or less", onlyHoldOpen);
            Assert.Contains("wait done", onlyHoldOpen);
        }

        [Fact]
        public void RaidSucceeded_StatesBothRulesWithTheirNumbers()
        {
            string text = PlayerSiegeNotice.RaidSucceeded(Node(), Tuning());
            Assert.Contains("30% or less", text);
            Assert.Contains("every further 20,000 damage strips 35%", text);
            Assert.Contains("10:00 has passed", text);
        }

        [Fact]
        public void PresenceRaid_IsNotAStationSiege_AndHasNoStatus()
        {
            var n = new SpatialNode { Id = "N9", OwnerFactionTag = "UNGD", ZoneIntegrity = -1 };
            FactionTuning t = Tuning();
            t.PresenceRaidRadiusKm = 2.0;
            Assert.True(PlayerSiege.BeginPresenceRaid(n, t, T0));
            Assert.Null(PlayerSiegeNotice.Status(n, t, T0.AddSeconds(5)));
        }

        // ---- presence raids ---------------------------------------------------------------------

        /// <summary>A presence raid on an unfounded node, begun at T0 through the world so the opening
        /// strip and the strip clock are the ones the state machine set.</summary>
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

        [Fact]
        public void PresenceBegan_StatesTheStripTheCeilingTheWaitAndTheHoldRadius()
        {
            string text = PlayerSiegeNotice.PresenceBegan(new SpatialNode { Id = "N098", OwnerFactionTag = "STCG" },
                                                          Tuning());
            Assert.Contains("STCG's ground at N098", text);
            Assert.Contains("every 5:00 you stay strips 35%", text);
            Assert.Contains("30% or less", text);
            Assert.Contains("10:00 has passed", text);
            Assert.Contains("3.5 km", text);   // start radius 3 + hysteresis 0.5
        }

        [Fact]
        public void PresenceStatus_CountsTheStripsAndTheWait()
        {
            FactionTuning t = Tuning();
            WorldModel w;
            SpatialNode n = PresenceRaid(t, out w);

            // The opening strip took 1.00 to 0.65; one more reaches the 0.30 ceiling.
            string text = PlayerSiegeNotice.PresenceStatus(n, t, T0.AddSeconds(100));
            Assert.Contains("hold 65%, needs 30% or less", text);
            Assert.Contains("about 1 more strip; next in 3:20", text);
            Assert.Contains("wait 8:20 more", text);
        }

        [Fact]
        public void PresenceStatus_FollowsTheNextStrip()
        {
            FactionTuning t = Tuning();
            t.ConquerHoldCeiling = 0.35;     // off the 1 - 2 x 0.35 floating-point boundary
            WorldModel w;
            SpatialNode n = PresenceRaid(t, out w);

            w.UtcNow = T0.AddSeconds(300);
            w.MarkPlayerPresenceAttack("N098");      // the second strip lands

            string text = PlayerSiegeNotice.PresenceStatus(n, t, T0.AddSeconds(310));
            Assert.Contains("hold 30%, done", text);
            Assert.Contains("wait 4:50 more", text);
        }

        [Fact]
        public void PresenceStatus_BothMet_SaysToReplyYes()
        {
            FactionTuning t = Tuning();
            WorldModel w;
            SpatialNode n = PresenceRaid(t, out w);
            n.HoldStrength = 0.1;

            string text = PlayerSiegeNotice.PresenceStatus(n, t, T0.AddMinutes(10).AddSeconds(1));
            Assert.Contains("reply \"yes\"", text);
        }

        /// <summary>The two shapes are disjoint, so each line answers only its own: a station siege
        /// has no presence line, and a presence raid has no station line.</summary>
        [Fact]
        public void PresenceAndStationStatusDoNotCross()
        {
            FactionTuning t = Tuning();
            SpatialNode station = Node();
            PlayerSiege.AbsorbZoneDamage(station, t, T0, 5000);
            Assert.Null(PlayerSiegeNotice.PresenceStatus(station, t, T0.AddSeconds(5)));

            WorldModel w;
            SpatialNode ground = PresenceRaid(t, out w);
            Assert.Null(PlayerSiegeNotice.Status(ground, t, T0.AddSeconds(5)));
        }
    }
}
