using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class GridLossRuleTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 29, 20, 24, 1, DateTimeKind.Utc);

        [Fact]
        public void GridWeClosed_IsNeverDestroyed_EvenWithARecentHit()
        {
            // Live 2026-09-29 [40A672]: a Laevateinn grazed 47 s earlier was closed as a fleet-mate by
            // our own despawn and booked FLEET_DESTROYED, writing off hull and cargo.
            Assert.False(GridLossRule.IsDestroyed(true, Now.AddSeconds(-47), Now));
            Assert.False(GridLossRule.IsDestroyed(true, Now, Now));
        }

        [Fact]
        public void GridGoneWithoutUs_IsDestroyed_OnlyWithAHitInsideTheWindow()
        {
            Assert.True(GridLossRule.IsDestroyed(false, Now.AddSeconds(-47), Now));
            Assert.True(GridLossRule.IsDestroyed(false, Now - GridLossRule.DamageWindow, Now));
            Assert.False(GridLossRule.IsDestroyed(false, Now - GridLossRule.DamageWindow - TimeSpan.FromSeconds(1), Now));
            Assert.False(GridLossRule.IsDestroyed(false, null, Now));
        }

        [Fact]
        public void ZeroAmountEvents_DoNotStamp()
        {
            Assert.False(GridLossRule.Stamps(0.0));
            Assert.False(GridLossRule.Stamps(-1.0));
            Assert.True(GridLossRule.Stamps(0.5));
        }
    }
}
