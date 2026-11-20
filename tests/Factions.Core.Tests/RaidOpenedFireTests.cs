using System;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A player firing on a station whose zone a raid by their own faction took down. That fire is
    /// phase 2 of a player raid: it enters the breach through <see cref="PlayerSiege.EnterOpenedRaid"/>
    /// and from there runs the one state machine -- block damage toward the success threshold, a hold
    /// strip per success, the conquer-offer clock, presence holding the breach, abandonment.
    /// <see cref="PlayerSiege.MayAddRaidDamage"/> is the single rule for who qualifies.
    /// </summary>
    public class RaidOpenedFireTests
    {
        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            FactionTuning t = w.Tuning;
            t.RaidSafeZoneDownSeconds = 1800.0;
            t.RaidZoneIntegrityPool = 1000.0;
            t.RaidZoneIntegrityRegenSeconds = 30.0;
            t.RaidZoneRaidLapseSeconds = 30.0;
            t.RaidZoneSiegeDwellSeconds = 300.0;
            t.PlayerRaidBlockDamageForSuccess = 20000.0;
            t.PlayerConquerOfferMinutes = 10.0;
            t.ConquerHoldCeiling = 0.15;
            return w;
        }

        // A founded station owned by SPRT: the live case was SPRT's N165 under EXP's campaign.
        private static SpatialNode Station(WorldModel w, double hold = 1.0)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N165");
            n.OwnerFactionTag = "SPRT";
            n.HoldStrength = hold;
            n.StableStationId = 165;
            n.ZoneIntegrity = -1;
            return n;
        }

        // ── the qualifying case ──────────────────────────────────────────────

        [Fact]
        public void OwnFactionsRaid_OpensTheStation_ToItsPlayersFire()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 0.68);

            Assert.Equal("EXP", n.SafeZoneDownByTag);
            Assert.True(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow.AddSeconds(60), "EXP"));
        }

        [Fact]
        public void FirstFire_EntersPhaseTwo_AndLeavesTheZoneTimerAlone()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 0.68);
            DateTime until = n.SafeZoneDownUntilUtc;
            DateTime now = w.UtcNow.AddSeconds(60);

            Assert.True(PlayerSiege.EnterOpenedRaid(n, w.Tuning, now, "EXP"));

            Assert.Equal(now, n.ZoneBreachedUtc);
            Assert.Equal(0.0, n.ZoneIntegrity, 6);
            Assert.Equal(until, n.SafeZoneDownUntilUtc);
            Assert.Equal(PlayerSiege.ZoneState.Breached, PlayerSiege.StateOf(n, now));
            // Entered once: the next flush is already phase 2 and enters nothing.
            Assert.False(PlayerSiege.EnterOpenedRaid(n, w.Tuning, now.AddSeconds(1), "EXP"));
        }

        [Fact]
        public void EnteredFire_CountsExactlyAsPhaseTwo_StripsHoldAndStartsTheOfferClock()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, hold: 1.0);
            w.Raid("EXP", n.Id, 0.68);
            double afterNpcRaid = n.HoldStrength;
            w.UtcNow = w.UtcNow.AddSeconds(60);
            DateTime firstFire = w.UtcNow;

            Assert.True(PlayerSiege.EnterOpenedRaid(n, w.Tuning, w.UtcNow, "EXP"));
            Assert.False(PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 15000, false));
            Assert.Equal(15000.0, n.PlayerRaidBlockDamage, 6);

            Assert.True(PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 10000, false));
            w.ResolvePlayerRaid(n.Id, "EXP");

            Assert.Equal(firstFire, n.PlayerRaidSucceededUtc);
            Assert.Equal(5000.0, n.PlayerRaidBlockDamage, 6);   // overkill carries, as in any phase 2
            Assert.Equal(Math.Max(0.0, afterNpcRaid - w.Tuning.RaidHoldStrip(1.0)), n.HoldStrength, 6);

            // Grind the hold to the ceiling and the offer falls due on the siege's own clock.
            for (int i = 0; i < 5 && n.HoldStrength > w.Tuning.ConquerHoldCeiling; i++)
            {
                Assert.True(PlayerSiege.RecordRaidDamage(n, w.Tuning, w.UtcNow, 20000, false));
                w.ResolvePlayerRaid(n.Id, "EXP");
            }
            Assert.True(n.HoldStrength <= w.Tuning.ConquerHoldCeiling);
            Assert.False(PlayerSiege.ConquerOfferDue(n, w.Tuning, firstFire.AddMinutes(9)));
            Assert.True(PlayerSiege.ConquerOfferDue(n, w.Tuning, firstFire.AddMinutes(10)));
        }

        [Fact]
        public void LatestRaidOwnsTheWindow()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 1.0);
            w.UtcNow = w.UtcNow.AddSeconds(120);
            w.Raid("RIVL", n.Id, 1.0);

            Assert.Equal("RIVL", n.SafeZoneDownByTag);
            Assert.True(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, "RIVL"));
            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, "EXP"));
        }

        [Fact]
        public void APlayersOwnSuccess_AttributesTheWindowToTheirFaction()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.ResolvePlayerRaid(n.Id, "EXP");

            Assert.Equal("EXP", n.SafeZoneDownByTag);
            Assert.True(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow.AddSeconds(5), "EXP"));
        }

        // ── negative controls ────────────────────────────────────────────────

        [Fact]
        public void AnUnrelatedFactionsPlayer_GetsNothing()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 0.68);
            DateTime now = w.UtcNow.AddSeconds(60);

            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, now, "ZED"));
            Assert.False(PlayerSiege.EnterOpenedRaid(n, w.Tuning, now, "ZED"));
            Assert.False(PlayerSiege.RecordRaidDamage(n, w.Tuning, now, 50000, true));
            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);
            Assert.Equal(0.0, n.PlayerRaidBlockDamage, 6);
            Assert.Equal(default(DateTime), n.PlayerRaidSucceededUtc);
        }

        [Fact]
        public void AFactionlessPlayer_GetsNothing()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 1.0);

            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, null));
            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, ""));
        }

        [Fact]
        public void AZoneDownForAnotherReason_IsNotOpenedToAnyone()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            // A window with no raider on record: a save from before the attribution existed, or a
            // player's success whose faction was not known.
            n.SafeZoneDownUntilUtc = w.UtcNow.AddMinutes(30);

            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, "EXP"));

            w.ResolvePlayerRaid(n.Id);   // untagged player success extends it, still nobody's
            Assert.Null(n.SafeZoneDownByTag);
            Assert.False(PlayerSiege.EnterOpenedRaid(n, w.Tuning, w.UtcNow, "EXP"));
        }

        [Fact]
        public void OwnFactionsRaid_OnceExpired_OpensNothing()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 1.0);

            Assert.True(PlayerSiege.MayAddRaidDamage(n, w.Tuning, n.SafeZoneDownUntilUtc.AddSeconds(-1), "EXP"));
            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, n.SafeZoneDownUntilUtc, "EXP"));
            Assert.False(PlayerSiege.EnterOpenedRaid(n, w.Tuning, n.SafeZoneDownUntilUtc.AddMinutes(1), "EXP"));
        }

        [Fact]
        public void TheOwner_TheUnfoundedNode_AndASwitchedOffPool_AreRefused()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            n.SafeZoneDownUntilUtc = w.UtcNow.AddMinutes(30);
            n.SafeZoneDownByTag = "SPRT";
            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, "SPRT"));

            n.SafeZoneDownByTag = "EXP";
            n.StableStationId = 0;
            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, "EXP"));

            n.StableStationId = 165;
            w.Tuning.RaidZoneIntegrityPool = 0;
            Assert.False(PlayerSiege.EnterOpenedRaid(n, w.Tuning, w.UtcNow, "EXP"));
            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);
        }

        // ── maintenance on this entry path ───────────────────────────────────

        [Fact]
        public void Presence_HoldsTheBreach_PastTheWindow()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 1.0);
            DateTime past = n.SafeZoneDownUntilUtc.AddMinutes(5);
            PlayerSiege.EnterOpenedRaid(n, w.Tuning, w.UtcNow.AddSeconds(60), "EXP");

            Assert.False(PlayerSiege.Maintain(n, w.Tuning, past, attackerPresent: true));
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, past));
        }

        [Fact]
        public void Leaving_HandsTheZoneBackToItsWindow_AndReturningReentersPhaseTwo()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 1.0);
            DateTime until = n.SafeZoneDownUntilUtc;
            DateTime t1 = w.UtcNow.AddSeconds(60);
            PlayerSiege.EnterOpenedRaid(n, w.Tuning, t1, "EXP");

            Assert.False(PlayerSiege.Maintain(n, w.Tuning, t1.AddSeconds(10), attackerPresent: false));
            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);
            Assert.Equal(until, n.SafeZoneDownUntilUtc);
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, t1.AddSeconds(10)));
            Assert.True(PlayerSiege.ZoneShouldBeEnabled(n, until));

            Assert.True(PlayerSiege.EnterOpenedRaid(n, w.Tuning, t1.AddSeconds(20), "EXP"));
        }

        [Fact]
        public void Abandoned_TheRaidLapses_OnceThePoolHasStoodFull()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            w.Raid("EXP", n.Id, 1.0);
            DateTime now = w.UtcNow.AddSeconds(60);
            PlayerSiege.EnterOpenedRaid(n, w.Tuning, now, "EXP");
            PlayerSiege.RecordRaidDamage(n, w.Tuning, now, 25000, false);
            Assert.NotEqual(default(DateTime), n.PlayerRaidSucceededUtc);

            DateTime left = now.AddSeconds(5);
            PlayerSiege.Maintain(n, w.Tuning, left, attackerPresent: false);
            // 30 s to refill, then 30 s standing full.
            DateTime full = left.AddSeconds(31);
            Assert.False(PlayerSiege.Maintain(n, w.Tuning, full, false));
            Assert.True(PlayerSiege.Maintain(n, w.Tuning, full.AddSeconds(31), false));

            Assert.Equal(default(DateTime), n.PlayerRaidSucceededUtc);
            Assert.Equal(0.0, n.PlayerRaidBlockDamage, 6);
        }

        // ── persistence ──────────────────────────────────────────────────────

        [Fact]
        public void TheRaiderOfAnOpenWindow_SurvivesARestart_AndIsOmittedWhenAbsent()
        {
            var graph = new SpatialGraph();
            SpatialNode a = graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            a.OwnerFactionTag = "SPRT";
            a.SafeZoneDownUntilUtc = new DateTime(2026, 9, 29, 19, 38, 34, DateTimeKind.Utc);
            a.SafeZoneDownByTag = "EXP";
            SpatialNode b = graph.Add(new Vec3(1, 0, 0), 0.5, "N2");
            b.OwnerFactionTag = "SPRT";

            var st = new FactionState { Tag = "SPRT", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("SPRT"), FactionStore.DefaultLogCap));
                string json = File.ReadAllText(Path.Combine(dir, "brains", "SPRT.json"));
                int first = json.IndexOf("\"SafeZoneDownByTag\"", StringComparison.Ordinal);
                Assert.True(first >= 0);
                Assert.Equal(-1, json.IndexOf("\"SafeZoneDownByTag\"", first + 1, StringComparison.Ordinal));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("SPRT"), reloaded);
                Assert.Equal("EXP", reloaded.Nodes["N1"].SafeZoneDownByTag);
                Assert.Null(reloaded.Nodes["N2"].SafeZoneDownByTag);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
