using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class RetryGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

        [Fact]
        public void AnUnarmedEntityIsNeverRefused()
        {
            var gate = new RetryGate(TimeSpan.FromSeconds(5));
            Assert.False(gate.Refuses(1, T0));
        }

        /// <summary>The production loop: an approach aborted on lost braking was taken again on
        /// the next frame, 55 times a second. Armed, the next frame is refused.</summary>
        [Fact]
        public void AnArmedEntityIsRefusedOnTheNextFrame_AndUntilTheIntervalPasses()
        {
            var gate = new RetryGate(TimeSpan.FromSeconds(5));
            gate.Arm(1, T0);
            Assert.True(gate.Refuses(1, T0 + Frame));
            Assert.True(gate.Refuses(1, T0 + TimeSpan.FromSeconds(4.9)));
            Assert.False(gate.Refuses(1, T0 + TimeSpan.FromSeconds(5)));
            Assert.Equal(0, gate.Count);
        }

        [Fact]
        public void ArmingOneEntityLeavesOthersFree()
        {
            var gate = new RetryGate(TimeSpan.FromSeconds(5));
            gate.Arm(1, T0);
            Assert.False(gate.Refuses(2, T0 + Frame));
        }

        [Fact]
        public void ReArmingRestartsTheInterval()
        {
            var gate = new RetryGate(TimeSpan.FromSeconds(5));
            gate.Arm(1, T0);
            gate.Arm(1, T0 + TimeSpan.FromSeconds(4));
            Assert.True(gate.Refuses(1, T0 + TimeSpan.FromSeconds(8)));
        }

        [Fact]
        public void PruneDropsOnlyExpiredEntries_AndOnlyPastTheThreshold()
        {
            var gate = new RetryGate(TimeSpan.FromSeconds(5));
            gate.Arm(1, T0);
            gate.Arm(2, T0 + TimeSpan.FromSeconds(10));
            gate.PruneExpired(T0 + TimeSpan.FromSeconds(6), 3);
            Assert.Equal(2, gate.Count);
            gate.PruneExpired(T0 + TimeSpan.FromSeconds(6), 2);
            Assert.Equal(1, gate.Count);
            Assert.True(gate.Refuses(2, T0 + TimeSpan.FromSeconds(6)));
        }
    }
}
