using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CrewSightlineTests
    {
        [Fact] public void ShooterOnlyHitsDoNotBlockTheShot()
        {
            var ray = new CrewSightline();
            ray.Observe(0, true, false);
            ray.Observe(0.1, true, false);
            Assert.True(ray.Clear);
        }
        [Fact] public void AWallBeforeTheTargetBlocksRegardlessOfHitOrder()
        {
            var ray = new CrewSightline();
            ray.Observe(10, false, true);
            ray.Observe(2, false, false);
            ray.Observe(20, false, true);
            Assert.False(ray.Clear);
        }
        [Fact] public void AWallBeyondTheTargetDoesNotBlockIt()
        {
            var ray = new CrewSightline();
            ray.Observe(10, false, false);
            ray.Observe(2, false, true);
            Assert.True(ray.Clear);
        }
        [Fact] public void AnotherCrewMemberInFrontOfTheTargetBlocksFire()
        {
            var ray = new CrewSightline();
            ray.Observe(4, false, true);
            ray.Observe(1, false, false);
            ray.Observe(0, true, false);
            Assert.False(ray.Clear);
        }
        [Fact] public void UnknownHitsAndInvalidDistancesCannotAuthorizeFire()
        {
            var ray = new CrewSightline();
            ray.Observe(double.NaN, false, false);
            ray.Observe(1, false, true);
            Assert.False(ray.Clear);
        }
    }
}
