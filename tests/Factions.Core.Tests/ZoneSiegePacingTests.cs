using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-490: a raid fires on a protected zone to start the siege, keeps it alive with a hit
    /// inside the refill time, and fires continuously only when a full pool would just be emptied as
    /// the breach comes due — not all the time.</summary>
    public class ZoneSiegePacingTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private static FactionTuning Tuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.RaidZoneIntegrityPool = 1000;
            t.RaidZoneIntegrityRegenSeconds = 30;
            t.RaidZoneRaidLapseSeconds = 30;
            t.RaidZoneSiegeDwellSeconds = 300;
            return t;
        }

        private static SpatialNode Node()
        {
            var g = new SpatialGraph();
            SpatialNode n = g.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "EXP";
            n.StationPrefabId = "Outpost";
            n.StableStationId = 7;
            return n;
        }

        [Fact]
        public void NoSiegeYet_FiresToStartIt()
        {
            Assert.Equal(ZoneFireCall.Start, ZoneSiegePacing.Decide(Node(), Tuning(), T0, 1800, double.MaxValue));
        }

        [Fact]
        public void AHitJustLanded_Holds_ThenKeepsAliveInsideTheRefill()
        {
            FactionTuning t = Tuning();
            SpatialNode n = Node();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 50, "UNGD");

            Assert.Equal(ZoneFireCall.Hold, ZoneSiegePacing.Decide(n, t, T0.AddSeconds(5), 1800, double.MaxValue));
            Assert.Equal(ZoneFireCall.Hold, ZoneSiegePacing.Decide(n, t, T0.AddSeconds(19), 1800, double.MaxValue));
            Assert.Equal(ZoneFireCall.KeepAlive, ZoneSiegePacing.Decide(n, t, T0.AddSeconds(20), 1800, double.MaxValue));
            // The keep-alive comes before the pool refills, so the lapse clock never starts.
            Assert.True(ZoneSiegePacing.KeepAliveSeconds(t) < t.RaidZoneIntegrityRegenSeconds);
        }

        [Fact]
        public void ThePushStartsWhenAFullPoolWouldJustEmptyAsTheBreachIsDue()
        {
            FactionTuning t = Tuning();
            SpatialNode n = Node();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 50, "UNGD");
            // 1800 dps against 33.3/s of regen drains 1000 in ~0.56 s; the push opens 5.56 s before 300 s.
            PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(290), 10, "UNGD");
            Assert.Equal(ZoneFireCall.Hold, ZoneSiegePacing.Decide(n, t, T0.AddSeconds(294), 1800, double.MaxValue));
            Assert.Equal(ZoneFireCall.Push, ZoneSiegePacing.Decide(n, t, T0.AddSeconds(295), 1800, double.MaxValue));

            // A weaker raider starts earlier: 100 dps nets 66.7/s, 15 s to drain, so from 280 s.
            SpatialNode m = Node();
            PlayerSiege.AbsorbZoneDamage(m, t, T0, 50, "UNGD");
            PlayerSiege.AbsorbZoneDamage(m, t, T0.AddSeconds(278), 10, "UNGD");
            Assert.Equal(ZoneFireCall.Hold, ZoneSiegePacing.Decide(m, t, T0.AddSeconds(279), 100, double.MaxValue));
            Assert.Equal(ZoneFireCall.Push, ZoneSiegePacing.Decide(m, t, T0.AddSeconds(280), 100, double.MaxValue));
        }

        [Fact]
        public void ARaiderThatCannotOutDamageTheRegen_OnlyKeepsAlive()
        {
            FactionTuning t = Tuning();
            SpatialNode n = Node();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 50, "UNGD");
            PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(310), 10, "UNGD");
            Assert.Equal(ZoneFireCall.Hold, ZoneSiegePacing.Decide(n, t, T0.AddSeconds(311), 30, double.MaxValue));
        }

        [Fact]
        public void PacedFire_BreachesTheZone_OnAFractionOfTheRoundsContinuousFireSpends()
        {
            FactionTuning t = Tuning();
            SpatialNode n = Node();
            const double dps = 1800;
            double fired = 0;
            DateTime breached = default(DateTime);
            for (int s = 0; s < 400 && breached == default(DateTime); s++)
            {
                DateTime now = T0.AddSeconds(s);
                // The host's per-tick maintenance, with the raid present: what would lapse the siege.
                Assert.False(PlayerSiege.Maintain(n, t, now, true), "the siege lapsed at " + s + " s");
                if (!ZoneSiegePacing.Fires(ZoneSiegePacing.Decide(n, t, now, dps, double.MaxValue))) continue;
                fired += dps;
                PlayerSiege.AbsorbZoneDamage(n, t, now, dps, "UNGD");
                if (n.ZoneBreachedUtc != default(DateTime)) breached = n.ZoneBreachedUtc;
            }
            Assert.NotEqual(default(DateTime), breached);
            Assert.True((breached - T0).TotalSeconds <= 305, "breached at " + (breached - T0).TotalSeconds + " s");
            // Continuous fire to the same breach spends ~300 x 1800; paced fire under a tenth of it.
            Assert.True(fired < 300 * dps / 10, "fired " + fired);
        }
    }
}
