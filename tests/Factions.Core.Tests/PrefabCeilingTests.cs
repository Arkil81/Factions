using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PrefabCeilingTests
    {
        private static readonly PrefabCeiling C = new PrefabCeiling();

        [Fact]
        public void TheDefaultReproducesTheBenchHullsWithinTheirSpread()
        {
            // measured awake and alone, 2026-09-23: B60 ~0.52, Naginata ~1.0, HighProfileTransport 1.4-2.1
            Assert.InRange(C.PredictedAwakeMs(1208), 0.4, 0.6);
            Assert.InRange(C.PredictedAwakeMs(2026), 0.9, 1.1);
            Assert.InRange(C.PredictedAwakeMs(3401), 1.4, 2.1);
        }

        [Theory]
        [InlineData(9825, true)]    // AtmoCarrier
        [InlineData(4566, true)]    // Angurvadal
        [InlineData(4207, true)]    // Mago
        [InlineData(3425, false)]   // GE_HighProfileTransportB, just under
        [InlineData(3401, false)]   // GE_HighProfileTransport
        [InlineData(242, false)]    // the median ship prefab
        public void AtTwoMillisecondsTheOperatorsListIsExactlyWhatIsOver(int blocks, bool over)
        {
            Assert.Equal(over, C.IsOver(blocks));
        }

        [Fact]
        public void AZeroOrNegativeCeilingDisablesIt()
        {
            Assert.False(new PrefabCeiling { CeilingMs = 0 }.IsOver(100000));
            Assert.False(new PrefabCeiling { CeilingMs = -1 }.IsOver(100000));
        }

        [Fact]
        public void AnUnknownBlockCountIsNeverOver()
        {
            Assert.False(C.IsOver(0));
            Assert.Equal(0.0, C.PredictedAwakeMs(-5));
        }
    }
}
