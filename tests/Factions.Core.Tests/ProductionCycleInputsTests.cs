using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionCycleInputsTests
    {
        [Fact]
        public void Capture_FreezesTuningAndRejectsChangedWorldInputs()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(1, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            var state = new FactionState { Tag = "F" };
            var tuning = new FactionTuning { EconomyIntervalSeconds = 30.0 };

            ProductionCycleInputs inputs = ProductionCycleInputs.Capture(state, world, tuning);

            tuning.EconomyIntervalSeconds = 60.0;
            world.UtcNow = world.UtcNow.AddMinutes(1);

            Assert.Equal(new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc), inputs.CapturedUtc);
            Assert.Equal(30.0, inputs.Tuning.EconomyIntervalSeconds);
            Assert.True(inputs.IsStillValid(state, world));

            world.AddNodeInventory("N1", "Ore:Iron", 1);

            Assert.False(inputs.IsStillValid(state, world));
        }
    }
}
