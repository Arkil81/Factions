using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class UnseenLandingsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 13, 49, 40, DateTimeKind.Utc);

        // The N096 case: the sim position is 14.5 km from the player and passes the 15 km gate; the
        // grid landed 16.4 km off and failed the 16 km keep test.
        private static readonly Vec3 Player = new Vec3(0, 0, 0);
        private static readonly Vec3 Sim = new Vec3(14.5, 0, 0);
        private static readonly Vec3 Landed = new Vec3(16.4, 0, 0);

        private static Func<Vec3, double, bool> Near(Vec3 player)
        {
            return (p, km) => p.DistanceTo(player) < km;
        }

        [Fact]
        public void A_fleet_that_has_not_moved_is_held_while_nobody_could_keep_the_grid()
        {
            var u = new UnseenLandings();
            u.Note("A5BD02", Landed, Sim, T0);

            // Without the record this is the second spawn of the loop, 8 s later at the same place.
            Assert.True(u.Holds("A5BD02", Sim, T0.AddSeconds(8), Near(Player), 16));
            Assert.True(u.Holds("A5BD02", Sim, T0.AddSeconds(60), Near(Player), 16));
        }

        [Fact]
        public void A_player_who_closes_in_on_the_landing_lets_it_spawn_and_drops_the_record()
        {
            var u = new UnseenLandings();
            u.Note("A5BD02", Landed, Sim, T0);

            var nearer = new Vec3(1.0, 0, 0); // 15.4 km from the landing
            Assert.False(u.Holds("A5BD02", Sim, T0.AddSeconds(20), Near(nearer), 16));
            Assert.Equal(0, u.Count);
        }

        [Fact]
        public void The_prediction_moves_with_the_fleet()
        {
            // The fleet has flown 3 km toward the player since: the landing would be 13.4 km off.
            Vec3 simNow = new Vec3(11.5, 0, 0);
            Vec3 predicted = UnseenLandings.PredictedLanding(Landed, Sim, simNow);
            Assert.Equal(13.4, predicted.X, 6);

            var u = new UnseenLandings();
            u.Note("A5BD02", Landed, Sim, T0);
            Assert.False(u.Holds("A5BD02", simNow, T0.AddSeconds(30), Near(Player), 16));
        }

        [Fact]
        public void The_record_expires_after_the_latch()
        {
            var u = new UnseenLandings();
            u.Note("A5BD02", Landed, Sim, T0);

            Assert.True(u.Holds("A5BD02", Sim, T0 + UnseenLandings.LatchFor - TimeSpan.FromSeconds(1), Near(Player), 16));
            Assert.False(u.Holds("A5BD02", Sim, T0 + UnseenLandings.LatchFor, Near(Player), 16));
            Assert.Equal(0, u.Count);
        }

        [Fact]
        public void Release_and_prune_drop_records_and_other_fleets_are_untouched()
        {
            var u = new UnseenLandings();
            u.Note("A5BD02", Landed, Sim, T0);
            u.Note("23AE2D", Landed, Sim, T0.AddSeconds(100));

            Assert.False(u.Holds("FFFFFF", Sim, T0, Near(Player), 16));

            u.Release("A5BD02");
            Assert.False(u.Holds("A5BD02", Sim, T0.AddSeconds(1), Near(Player), 16));

            u.Prune(T0.AddSeconds(100) + UnseenLandings.LatchFor);
            Assert.Equal(0, u.Count);
        }

        [Fact]
        public void A_fleet_without_an_id_is_never_recorded()
        {
            var u = new UnseenLandings();
            u.Note(null, Landed, Sim, T0);
            u.Note("", Landed, Sim, T0);
            Assert.Equal(0, u.Count);
            Assert.False(u.Holds(null, Sim, T0, Near(Player), 16));
        }
    }
}
