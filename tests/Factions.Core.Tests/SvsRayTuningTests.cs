using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary><c>SvsRayMaxRangeM</c> reaches the host as shipped: the cap is on unless an operator turns it off.</summary>
    public class SvsRayTuningTests
    {
        [Fact]
        public void ShippedDefault_CapsAtTheSyncDistance()
        {
            Assert.Equal(3000.0, TestSupport.Tuning().SvsRayMaxRangeM);
        }

        [Fact]
        public void AbsentKey_KeepsTheInitialiser_NotZero()
        {
            // A tuning.json written before the field existed lacks the key; 0 would read as "no cap".
            Assert.Equal(3000.0, new FactionTuning().SvsRayMaxRangeM);
        }

        [Fact]
        public void OperatorOverride_IsHonoured()
        {
            Assert.Equal(0.0, TestSupport.TuningWithOverride("{ \"SvsRayMaxRangeM\": 0.0 }").SvsRayMaxRangeM);
            Assert.Equal(5000.0, TestSupport.TuningWithOverride("{ \"SvsRayMaxRangeM\": 5000.0 }").SvsRayMaxRangeM);
        }
    }
}
