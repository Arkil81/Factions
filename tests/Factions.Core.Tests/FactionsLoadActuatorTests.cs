using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FactionsLoadActuatorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan ThirtyMin = TimeSpan.FromMinutes(30);

        [Theory]
        [InlineData(ActionKind.Haul)]
        [InlineData(ActionKind.Trade)]
        [InlineData(ActionKind.Mine)]
        [InlineData(ActionKind.Gift)]
        [InlineData(ActionKind.Ferry)]
        public void TrafficMaterialisesAtStepZeroAndResolvesOffScreenFromStepOne(ActionKind kind)
        {
            Assert.Equal(MaterialiseDecision.Materialise, FactionsLoadActuator.Decide(kind, 0));
            Assert.Equal(MaterialiseDecision.ResolveOffScreen, FactionsLoadActuator.Decide(kind, 1));
            Assert.Equal(MaterialiseDecision.ResolveOffScreen, FactionsLoadActuator.Decide(kind, 3));
        }

        [Theory]
        [InlineData(ActionKind.Raid)]
        [InlineData(ActionKind.Retaliate)]
        [InlineData(ActionKind.Conquer)]
        [InlineData(ActionKind.Guard)]
        [InlineData(ActionKind.Patrol)]
        [InlineData(ActionKind.Reinforce)]
        public void CombatMaterialisesThroughStepOneAndWaitsFromStepTwo(ActionKind kind)
        {
            Assert.Equal(MaterialiseDecision.Materialise, FactionsLoadActuator.Decide(kind, 1));
            Assert.Equal(MaterialiseDecision.Wait, FactionsLoadActuator.Decide(kind, 2));
            Assert.Equal(MaterialiseDecision.Wait, FactionsLoadActuator.Decide(kind, 3));
            Assert.False(FactionsLoadActuator.MayMaterialise(kind, 2));
        }

        [Fact]
        public void FreezingKeepsTheFleetWhereItStandsAndDelaysItsArrival()
        {
            var f = new PendingFleetAction
            {
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(100, 0, 0),
                DepartedUtc = T0,
                EstimatedArrivalUtc = T0.AddMinutes(10),
            };
            DateTime now = T0.AddMinutes(4);
            Vec3 before = f.CurrentPosKm(now);

            var frozen = FactionsLoadActuator.Freeze(f, TimeSpan.FromMinutes(3));
            Vec3 after = frozen.CurrentPosKm(now.AddMinutes(3));

            Assert.Equal(before.X, after.X, 6);
            Assert.Equal(T0.AddMinutes(13), frozen.EstimatedArrivalUtc);
            Assert.False(frozen.HasArrived(T0.AddMinutes(11)));
        }

        [Fact]
        public void ARoutedLegsClockMovesWithTheScheduleAndAnUnroutedOneStaysUnset()
        {
            var routed = new PendingFleetAction { DepartedUtc = T0, EstimatedArrivalUtc = T0.AddMinutes(10), RouteDepartedUtc = T0 };
            var plain = new PendingFleetAction { DepartedUtc = T0, EstimatedArrivalUtc = T0.AddMinutes(10) };
            Assert.Equal(T0.AddMinutes(2), FactionsLoadActuator.Freeze(routed, TimeSpan.FromMinutes(2)).RouteDepartedUtc);
            Assert.Equal(default(DateTime), FactionsLoadActuator.Freeze(plain, TimeSpan.FromMinutes(2)).RouteDepartedUtc);
        }

        [Fact]
        public void AHoldFreezesByTheTimeBetweenFrames()
        {
            var t = new CombatHoldTracker();
            TimeSpan by; bool started;
            Assert.True(t.TryHold("F1", T0, ThirtyMin, out by, out started));
            Assert.True(started);
            Assert.Equal(TimeSpan.Zero, by);
            Assert.True(t.TryHold("F1", T0.AddSeconds(1), ThirtyMin, out by, out started));
            Assert.False(started);
            Assert.Equal(TimeSpan.FromSeconds(1), by);
        }

        /// <summary>A held fleet is reported still: the HUD dead-reckons a contact from its speed, and a
        /// held one drew as creeping forward and snapping back every scanner pass (2026-10-01).</summary>
        [Fact]
        public void AFleetIsHoldingOnlyWhileItIsBeingHeld()
        {
            var t = new CombatHoldTracker();
            TimeSpan by; bool started;
            Assert.False(t.IsHolding("F1", T0));
            t.TryHold("F1", T0, ThirtyMin, out by, out started);
            Assert.True(t.IsHolding("F1", T0.AddSeconds(2)));
            Assert.False(t.IsHolding("F1", T0.AddSeconds(10)));
            t.TryHold("F1", T0.AddSeconds(11), ThirtyMin, out by, out started);
            t.Release("F1");
            Assert.False(t.IsHolding("F1", T0.AddSeconds(12)));
        }

        [Fact]
        public void AGapIsNeitherFrozenNorCounted()
        {
            var t = new CombatHoldTracker();
            TimeSpan by; bool started;
            t.TryHold("F1", T0, ThirtyMin, out by, out started);
            // player left range for ten minutes: the fleet flew on normally through it
            Assert.True(t.TryHold("F1", T0.AddMinutes(10), ThirtyMin, out by, out started));
            Assert.Equal(TimeSpan.Zero, by);
            Assert.Equal(TimeSpan.Zero, t.HeldFor("F1"));
        }

        [Fact]
        public void AfterThirtyMinutesOfHoldingTheFleetIsReleasedAndStaysReleased()
        {
            var t = new CombatHoldTracker();
            TimeSpan by; bool started;
            DateTime now = T0;
            t.TryHold("F1", now, ThirtyMin, out by, out started);
            bool held = true;
            for (int i = 0; i < 1800 && held; i++)
            {
                now = now.AddSeconds(1);
                held = t.TryHold("F1", now, ThirtyMin, out by, out started);
            }
            Assert.False(held);
            Assert.True(t.HeldFor("F1") >= ThirtyMin);
            Assert.False(t.TryHold("F1", now.AddSeconds(1), ThirtyMin, out by, out started));
        }

        [Fact]
        public void ReleaseAndPruneForgetFleets()
        {
            var t = new CombatHoldTracker();
            TimeSpan by; bool started;
            t.TryHold("F1", T0, ThirtyMin, out by, out started);
            t.TryHold("F2", T0, ThirtyMin, out by, out started);
            t.Release("F1");
            Assert.Equal(1, t.Count);
            t.Prune(T0.AddMinutes(20), TimeSpan.FromMinutes(10));
            Assert.Equal(0, t.Count);
        }
    }
}
