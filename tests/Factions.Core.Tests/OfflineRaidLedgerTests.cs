using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-534: offline raids and combat logging.</summary>
    public class OfflineRaidLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        private const long A = 101, B = 102, V = 201;

        [Fact]
        public void Classify_OnlineGraceOffline_AndUnknownLastSeenIsOffline()
        {
            TimeSpan grace = TimeSpan.FromMinutes(15);
            Assert.Equal(OfflineState.Online, OfflineRaidLedger.Classify(null, T0, grace));
            Assert.Equal(OfflineState.Grace, OfflineRaidLedger.Classify(T0.AddMinutes(-14), T0, grace));
            Assert.Equal(OfflineState.Offline, OfflineRaidLedger.Classify(T0.AddMinutes(-15), T0, grace));
            Assert.Equal(OfflineState.Offline, OfflineRaidLedger.Classify(DateTime.MinValue, T0, grace));
        }

        [Fact]
        public void Raid_BeginsOnTheFirstOfflineHit_WarnsOnRepeat_AndEndsWithTotals()
        {
            FactionTuning t = TestSupport.Tuning();
            var l = new OfflineRaidLedger();
            Assert.False(l.Hit(A, V, "VIC", 1, 50, 0, OfflineState.Online, T0, t).Began);
            Assert.False(l.Hit(A, V, "VIC", 1, 50, 0, OfflineState.Grace, T0, t).Began);
            Assert.Equal(0, l.OpenRaids);

            OfflineRaidVerdict first = l.Hit(A, V, "VIC", 1, 100, 1, OfflineState.Offline, T0, t);
            Assert.True(first.Began);
            Assert.True(first.WarnAttacker);
            OfflineRaidVerdict soon = l.Hit(A, V, "VIC", 2, 100, 2, OfflineState.Offline, T0.AddMinutes(1), t);
            Assert.False(soon.Began);
            Assert.False(soon.WarnAttacker);
            Assert.True(l.Hit(A, V, "VIC", 2, 100, 0, OfflineState.Offline, T0.AddMinutes(5), t).WarnAttacker);
            // Someone coming online does not end the raid on record: the same raid goes on being counted.
            Assert.False(l.Hit(A, V, "VIC", 1, 10, 0, OfflineState.Online, T0.AddMinutes(6), t).Began);

            var ended = new List<OfflineRaidSummary>();
            l.TakeEnded(T0.AddMinutes(10), t, ended);
            Assert.Empty(ended);
            l.TakeEnded(T0.AddMinutes(11), t, ended);
            OfflineRaidSummary s = Assert.Single(ended);
            Assert.Equal(A, s.Attacker);
            Assert.Equal("VIC", s.VictimKey);
            Assert.Equal(2, s.Grids);
            Assert.Equal(3, s.BlocksDestroyed);
            Assert.Equal(310.0, s.Damage, 3);
            Assert.Equal(T0, s.StartedUtc);
            Assert.Equal(T0.AddMinutes(6), s.LastHitUtc);
            Assert.Equal(0, l.OpenRaids);
            Assert.True(l.Hit(A, V, "VIC", 1, 1, 0, OfflineState.Offline, T0.AddMinutes(12), t).Began);
        }

        [Fact]
        public void Raid_IsPerAttackerAndVictim()
        {
            FactionTuning t = TestSupport.Tuning();
            var l = new OfflineRaidLedger();
            Assert.True(l.Hit(A, V, "VIC", 1, 1, 0, OfflineState.Offline, T0, t).Began);
            Assert.True(l.Hit(B, V, "VIC", 1, 1, 0, OfflineState.Offline, T0, t).Began);
            Assert.True(l.Hit(A, 202, "id:202", 9, 1, 0, OfflineState.Offline, T0, t).Began);
            Assert.Equal(3, l.OpenRaids);
        }

        [Fact]
        public void CombatLog_AVictimLeavingUnderFire_IsRecorded_AndItsAttackersAreNotOfflineRaiders()
        {
            FactionTuning t = TestSupport.Tuning();
            var l = new OfflineRaidLedger();
            l.Hit(A, V, "VIC", 1, 10, 0, OfflineState.Online, T0, t);
            l.Hit(B, V, "VIC", 1, 10, 0, OfflineState.Online, T0.AddMinutes(1), t);
            CombatLogRecord r;
            Assert.False(l.ObserveOnline("VIC", true, T0.AddMinutes(1), t, out r));
            Assert.True(l.ObserveOnline("VIC", false, T0.AddMinutes(2), t, out r));
            Assert.Equal(B, r.Attacker);
            Assert.Equal(T0.AddMinutes(1), r.LastHostileUtc);
            Assert.False(l.ObserveOnline("VIC", false, T0.AddMinutes(2.1), t, out r));   // once per logoff

            // Past the grace window, both attackers keep shooting: the same fight, not a raid.
            DateTime later = T0.AddMinutes(2);
            for (int i = 1; i <= 20; i++)
            {
                later = T0.AddMinutes(2 + i);
                Assert.True(l.Hit(A, V, "VIC", 1, 10, 0, OfflineState.Offline, later, t).CombatLogged);
                Assert.True(l.Hit(B, V, "VIC", 1, 10, 0, OfflineState.Offline, later, t).CombatLogged);
            }
            Assert.Equal(0, l.OpenRaids);

            // The exemption lasts the fight: back after the end window, it is a raid.
            l.TakeEnded(later.AddMinutes(6), t, null);
            Assert.True(l.Hit(A, V, "VIC", 1, 10, 0, OfflineState.Offline, later.AddMinutes(6), t).Began);
        }

        [Fact]
        public void CombatLog_NotForAFactionAlreadyOffline_NorForALogoffLongAfterTheLastShot()
        {
            FactionTuning t = TestSupport.Tuning();
            var l = new OfflineRaidLedger();
            CombatLogRecord r;
            l.Hit(A, V, "OFF", 1, 10, 0, OfflineState.Grace, T0, t);
            Assert.False(l.ObserveOnline("OFF", false, T0.AddSeconds(5), t, out r));

            l.Hit(A, V, "VIC", 1, 10, 0, OfflineState.Online, T0, t);
            Assert.False(l.ObserveOnline("VIC", false, T0.AddMinutes(5), t, out r));
        }
    }
}
