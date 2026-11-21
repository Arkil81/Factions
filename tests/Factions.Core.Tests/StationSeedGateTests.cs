using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class StationSeedGateTests
    {
        [Fact]
        public void Authority_withholds_only_its_own_stations()
        {
            Assert.Equal(StationSeedPolicy.WithholdOurs, StationSeedGate.For(false, false, false));
        }

        [Fact]
        public void Agent_before_its_first_snapshot_withholds_every_station()
        {
            Assert.Equal(StationSeedPolicy.WithholdAll, StationSeedGate.For(true, false, true));
        }

        [Fact]
        public void Agent_with_seeded_claims_withholds_only_factions_stations()
        {
            Assert.Equal(StationSeedPolicy.WithholdOurs, StationSeedGate.For(true, true, true));
            Assert.Equal(StationSeedPolicy.WithholdOurs, StationSeedGate.For(true, true, false));
        }

        [Fact]
        public void Deferred_server_without_agent_hands_stays_vanilla()
        {
            Assert.Equal(StationSeedPolicy.Vanilla, StationSeedGate.For(true, false, false));
        }
    }
}
