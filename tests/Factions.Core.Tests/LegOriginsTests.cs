using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class LegOriginsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 0, 13, 4, DateTimeKind.Utc);
        private static readonly DateTime Leg1 = T0.AddMinutes(-4);
        private static readonly DateTime Leg2 = T0;

        [Fact]
        public void The_first_observation_of_a_leg_is_its_origin()
        {
            var origins = new LegOrigins();

            Assert.Equal(0.0, origins.Observe(7, Leg1, new Vec3(100, 0, 0), T0));
            Assert.Equal(3.0, origins.Observe(7, Leg1, new Vec3(103, 0, 0), T0.AddSeconds(30)), 9);
        }

        [Fact]
        public void A_grid_retargeted_in_place_is_re_originated_where_it_sits()
        {
            // The FAC-500 shape: a hauler flies 40 km to its pickup, then is retargeted onto the next
            // leg without moving. Measured against the first leg's origin it would read 40 km and look
            // like a grid that flew and then stopped.
            var origins = new LegOrigins();
            origins.Observe(7, Leg1, new Vec3(0, 0, 0), T0.AddMinutes(-4));
            origins.Observe(7, Leg1, new Vec3(40, 0, 0), T0.AddSeconds(-1));

            Assert.Equal(0.0, origins.Observe(7, Leg2, new Vec3(40, 0, 0), T0));
            Assert.Equal(0.01, origins.Observe(7, Leg2, new Vec3(40.01, 0, 0), T0.AddSeconds(36)), 9);
        }

        [Fact]
        public void Grids_are_tracked_independently()
        {
            var origins = new LegOrigins();
            origins.Observe(7, Leg1, new Vec3(0, 0, 0), T0);
            origins.Observe(8, Leg1, new Vec3(50, 0, 0), T0);

            Assert.Equal(5.0, origins.Observe(7, Leg1, new Vec3(5, 0, 0), T0.AddSeconds(10)), 9);
            Assert.Equal(0.0, origins.Observe(8, Leg1, new Vec3(50, 0, 0), T0.AddSeconds(10)), 9);
        }

        [Fact]
        public void A_grid_skipped_for_less_than_the_prune_age_keeps_its_origin()
        {
            var origins = new LegOrigins();
            origins.Observe(7, Leg1, new Vec3(0, 0, 0), T0);

            origins.Prune(T0.AddMinutes(9), TimeSpan.FromMinutes(10));

            Assert.Equal(1, origins.Count);
            Assert.Equal(20.0, origins.Observe(7, Leg1, new Vec3(20, 0, 0), T0.AddMinutes(9)), 9);
        }

        [Fact]
        public void Prune_drops_only_grids_unseen_for_longer_than_the_age()
        {
            var origins = new LegOrigins();
            origins.Observe(7, Leg1, new Vec3(0, 0, 0), T0);
            origins.Observe(8, Leg1, new Vec3(0, 0, 0), T0.AddMinutes(5));

            origins.Prune(T0.AddMinutes(11), TimeSpan.FromMinutes(10));

            Assert.Equal(1, origins.Count);
            // 7 was dropped, so its next observation starts a fresh origin.
            Assert.Equal(0.0, origins.Observe(7, Leg1, new Vec3(20, 0, 0), T0.AddMinutes(11)));
            Assert.Equal(20.0, origins.Observe(8, Leg1, new Vec3(20, 0, 0), T0.AddMinutes(11)), 9);
        }

        [Fact]
        public void Forget_starts_the_next_observation_afresh()
        {
            var origins = new LegOrigins();
            origins.Observe(7, Leg1, new Vec3(0, 0, 0), T0);
            origins.Forget(7);

            Assert.Equal(0, origins.Count);
            Assert.Equal(0.0, origins.Observe(7, Leg1, new Vec3(9, 0, 0), T0.AddSeconds(1)));
        }
    }
}
