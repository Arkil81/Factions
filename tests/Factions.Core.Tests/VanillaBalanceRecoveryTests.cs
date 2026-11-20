using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class VanillaBalanceRecoveryTests
    {
        private static FactionTuning Tuning(bool suppress)
        {
            return new FactionTuning { SuppressVanillaFactionBalanceRecovery = suppress };
        }

        [Fact]
        public void ADeferredServer_Suppresses_WithNoRuntime()
        {
            Assert.True(VanillaBalanceRecovery.Suppress(true, null, Tuning(true)));
            Assert.True(VanillaBalanceRecovery.Suppress(true, null, null));
        }

        [Fact]
        public void ADeferredServer_RunsVanilla_OnlyWhenItsOwnTuningSaysSo()
        {
            Assert.False(VanillaBalanceRecovery.Suppress(true, null, Tuning(false)));
            Assert.False(VanillaBalanceRecovery.Suppress(true, Tuning(true), Tuning(false)));
        }

        [Fact]
        public void TheSimulatingServer_KeepsItsContract()
        {
            Assert.False(VanillaBalanceRecovery.Suppress(false, null, Tuning(true)));   // runtime not up yet
            Assert.True(VanillaBalanceRecovery.Suppress(false, Tuning(true), null));
            Assert.False(VanillaBalanceRecovery.Suppress(false, Tuning(false), Tuning(true)));
        }
    }
}
