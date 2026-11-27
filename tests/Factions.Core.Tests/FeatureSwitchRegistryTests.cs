using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Every feature switch was removed with its off path on 2026-10-09; the registry keeps the
    /// physics diagnostic, which changes what it measures and so must start off, and switches past their
    /// first deploy (AGENTS.md rule 8), which start on. A switch before its first deploy starts off.</summary>
    public class FeatureSwitchRegistryTests
    {
        [Fact]
        public void TheRegistry_HoldsTheDiagnosticOff_AndTheDeployedSwitchesOn()
        {
            Assert.Equal(new[] { FeatureSwitch.PhysicsPerWorld, FeatureSwitch.StationVoxels, FeatureSwitch.StationLoot,
                                 FeatureSwitch.FleetTrespass, FeatureSwitch.LearnedCeiling, FeatureSwitch.NexusLeftoverPilot },
                         FeatureSwitch.Known);
            Assert.False(FeatureSwitch.IsOn(FeatureSwitch.PhysicsPerWorld));
            Assert.True(FeatureSwitch.IsOn(FeatureSwitch.StationVoxels));
            Assert.True(FeatureSwitch.IsOn(FeatureSwitch.StationLoot));
            Assert.True(FeatureSwitch.IsOn(FeatureSwitch.FleetTrespass));
            Assert.True(FeatureSwitch.IsOn(FeatureSwitch.LearnedCeiling));
            Assert.True(FeatureSwitch.IsOn(FeatureSwitch.NexusLeftoverPilot));
            Assert.False(FeatureSwitch.Set("rock-shell", true));
            Assert.False(FeatureSwitch.Set("zone-fire", true));
        }
    }
}
