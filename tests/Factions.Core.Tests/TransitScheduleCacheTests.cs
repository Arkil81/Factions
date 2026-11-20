using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>TransitSchedule.TryGet: the per-profile memo a fleet's every-frame position read goes through.</summary>
    public class TransitScheduleCacheTests
    {
        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                FleetSpeedMinKmPerMin = 30.0, FleetSpeedOrbitKmPerMin = 120.0,
                FleetSpeedKmPerMin = 500.0, FleetAccelKmPerMin2 = 4608.0,
            };
        }

        private static LegZoneProfile Zones()
        {
            var z = new LegZoneProfile { HasGeometry = true };
            z.Add(TransitTier.Gravity, 20.0);
            z.Add(TransitTier.DeepSpace, 400.0);
            z.Add(TransitTier.Orbit, 60.0);
            return z;
        }

        [Fact]
        public void SameTuning_ReturnsTheSameSchedule_AndItMatchesAFreshBuild()
        {
            FactionTuning t = Tuning();
            LegZoneProfile z = Zones();
            TransitSchedule a, b, fresh;
            Assert.True(TransitSchedule.TryGet(z, t, out a));
            Assert.True(TransitSchedule.TryGet(z, t, out b));
            Assert.Same(a, b);
            Assert.True(TransitSchedule.TryBuild(Zones(), t, out fresh));
            Assert.Equal(fresh.TotalMins, a.TotalMins, 9);
            foreach (double m in new[] { 0.0, 0.3, 1.1, 2.0, a.TotalMins })
                Assert.Equal(fresh.ProgressRatio(m), a.ProgressRatio(m), 9);
        }

        [Fact]
        public void ChangedSpeedTuning_Rebuilds()
        {
            FactionTuning t = Tuning();
            LegZoneProfile z = Zones();
            TransitSchedule a, b;
            Assert.True(TransitSchedule.TryGet(z, t, out a));
            t.FleetSpeedKmPerMin = 250.0;
            Assert.True(TransitSchedule.TryGet(z, t, out b));
            Assert.NotSame(a, b);
            Assert.True(b.TotalMins > a.TotalMins);
        }

        [Fact]
        public void AddingARun_DropsTheCachedSchedule()
        {
            FactionTuning t = Tuning();
            LegZoneProfile z = Zones();
            TransitSchedule a, b;
            Assert.True(TransitSchedule.TryGet(z, t, out a));
            z.Add(TransitTier.Gravity, 30.0);
            Assert.True(TransitSchedule.TryGet(z, t, out b));
            Assert.NotSame(a, b);
            Assert.True(b.TotalKm > a.TotalKm);
        }

        [Fact]
        public void ARefusedBuild_StaysRefused_WithoutRebuilding()
        {
            FactionTuning t = Tuning();
            t.FleetAccelKmPerMin2 = 0;
            LegZoneProfile z = Zones();
            TransitSchedule s;
            Assert.False(TransitSchedule.TryGet(z, t, out s));
            Assert.Null(s);
            Assert.False(TransitSchedule.TryGet(z, t, out s));
        }

        [Fact]
        public void FleetPosition_IsUnchangedThroughTheCache()
        {
            FactionTuning t = Tuning();
            var f = new PendingFleetAction
            {
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(480, 0, 0),
                DepartedUtc = new System.DateTime(2026, 10, 6, 12, 0, 0, System.DateTimeKind.Utc),
                LegZones = Zones(),
            };
            var at = f.DepartedUtc.AddMinutes(0.8);
            Vec3 first = f.CurrentPosKm(at, t);
            Vec3 again = f.CurrentPosKm(at, t);
            Assert.Equal(first.X, again.X, 9);
            TransitSchedule fresh;
            Assert.True(TransitSchedule.TryBuild(Zones(), t, out fresh));
            Assert.Equal(480 * fresh.ProgressRatio(0.8), first.X, 6);
        }
    }
}
