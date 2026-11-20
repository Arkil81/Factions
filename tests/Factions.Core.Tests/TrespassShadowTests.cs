using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 3b — a Guard shadows the intruder it was sent at: which grid it follows, and at
    /// what range.
    /// </summary>
    public sealed class TrespassShadowTests
    {
        [Fact]
        public void ShadowPlan_HoldsTheStandoffWhateverEitherSideCarries()
        {
            CombatPlan plan = TacticalRead.ShadowPlan(TrespassLadder.ShadowStandoffM);

            Assert.Equal(ManoeuvreKind.HoldAtRange, plan.Kind);
            Assert.Equal(TacticArm.Standoff, plan.Arm);
            Assert.Equal(1500.0, plan.HoldRangeM);
            Assert.True(plan.RangeToleranceM > 0 && plan.RangeToleranceM <= plan.HoldRangeM * 0.25);
            // Beyond a gatling's 800 m even at the near edge of the band.
            Assert.True(plan.HoldRangeM - plan.RangeToleranceM > 800.0);
        }

        [Fact]
        public void ShadowPlan_NeverHoldsInsideTheMinimum()
        {
            Assert.Equal(TacticalRead.MinHoldRangeM, TacticalRead.ShadowPlan(10).HoldRangeM);
        }

        [Fact]
        public void TheLeashReachesPastTheStandoff()
        {
            Assert.True(TrespassLadder.ShadowLeashM > TrespassLadder.ShadowStandoffM * 2);
        }

        [Fact]
        public void IntruderGrid_IsTheMarkedGridWhileTheNodeIsIntruded()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            Assert.Equal(0, w.IntruderGridAt("N002"));

            w.MarkIntruder("N002", TrespassRung.Shadow, 4242);
            Assert.Equal(4242, w.IntruderGridAt("N002"));

            // An unnamed mark keeps the grid it has, even once the hold on it has run out.
            w.UtcNow = w.UtcNow.AddSeconds(15);
            w.MarkIntruder("N002", TrespassRung.Shadow, 0);
            Assert.Equal(4242, w.IntruderGridAt("N002"));

            w.UtcNow = w.UtcNow + TrespassPolicy.IntruderMemory + TimeSpan.FromSeconds(1);
            Assert.Equal(0, w.IntruderGridAt("N002"));
        }

        [Fact]
        public void IntruderGrid_StaysOnOnePilotWhileTheyAreSeen_ThenMovesToTheNext()
        {
            WorldModel w = StationCallTests.DefenderWorld();
            w.MarkIntruder("N002", TrespassRung.Shadow, 1);
            for (int i = 0; i < 6; i++)
            {
                w.UtcNow = w.UtcNow.AddSeconds(5);
                w.MarkIntruder("N002", TrespassRung.Shadow, 1);
                w.MarkIntruder("N002", TrespassRung.Shadow, 2);   // a second pilot, marked last every pass
            }
            Assert.Equal(1, w.IntruderGridAt("N002"));

            // The first pilot gone: the second takes over within a couple of passes.
            for (int i = 0; i < 3; i++)
            {
                w.UtcNow = w.UtcNow.AddSeconds(5);
                w.MarkIntruder("N002", TrespassRung.Shadow, 2);
            }
            Assert.Equal(2, w.IntruderGridAt("N002"));
        }
    }
}
