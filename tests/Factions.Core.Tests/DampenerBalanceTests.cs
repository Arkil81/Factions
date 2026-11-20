using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The two quantities <c>factions dampprobe</c> derives. Live 2026-10-12: a ~1,000 t large grid
    /// with no subgrids and empty cargo climbs ~1 m/s under dampeners in 1 g, piloted or not.</summary>
    public class DampenerBalanceTests
    {
        private const double G = 9.81;

        [Fact]
        public void A_dampener_that_believes_the_true_mass_holds_still()
        {
            Assert.Equal(0.0, DampenerBalance.EquilibriumClimbMs(G, 1000000, 1000000), 9);
        }

        [Fact]
        public void A_dampener_that_overcounts_mass_by_a_quarter_climbs_about_one_metre_per_second()
        {
            // m/m_d = 0.8 -> 9.81 * 0.2 / 2 = 0.981 m/s: the observed creep is a ~25% overcount.
            Assert.Equal(0.981, DampenerBalance.EquilibriumClimbMs(G, 1000000, 1250000), 6);
        }

        [Fact]
        public void A_dampener_that_undercounts_mass_sinks()
        {
            Assert.True(DampenerBalance.EquilibriumClimbMs(G, 1000000, 800000) < 0);
        }

        [Fact]
        public void A_missing_mass_reads_no_climb_rather_than_dividing_by_zero()
        {
            Assert.Equal(0.0, DampenerBalance.EquilibriumClimbMs(G, 1000000, 0));
            Assert.Equal(0.0, DampenerBalance.EquilibriumClimbMs(G, 0, 1000000));
        }

        [Fact]
        public void Thrust_lift_equal_to_weight_leaves_nothing_unaccounted()
        {
            Assert.Equal(0.0, DampenerBalance.UnaccountedLiftN(G, 1000000, G * 1000000), 6);
        }

        [Fact]
        public void Thrust_lift_short_of_weight_is_an_upward_push_from_elsewhere()
        {
            // A steady climb on 80% of the weight in thrust: 20% came from something else.
            Assert.Equal(0.2 * G * 1000000, DampenerBalance.UnaccountedLiftN(G, 1000000, 0.8 * G * 1000000), 3);
        }
    }
}
