using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CrewEngagementTests
    {
        [Fact] public void LosingSightEndsFireAndResumesPursuitEvenAtCloseRange()
        {
            var crew = new CrewEngagement();
            Assert.Equal(CrewCombatIntent.Fire, crew.Update(true, 10, true, true));
            Assert.Equal(CrewCombatIntent.Pursue, crew.Update(true, 10, false, true));
            Assert.Equal(CrewCombatIntent.Aim, crew.Update(true, 10, true, false));
        }
        [Fact] public void RangeHysteresisAvoidsAlternatingBetweenWalkingAndShooting()
        {
            var crew = new CrewEngagement();
            Assert.Equal(CrewCombatIntent.Pursue, crew.Update(true, 27, true, true));
            Assert.Equal(CrewCombatIntent.Fire, crew.Update(true, 25, true, true));
            Assert.Equal(CrewCombatIntent.Fire, crew.Update(true, 29, true, true));
            Assert.Equal(CrewCombatIntent.Pursue, crew.Update(true, 31, true, true));
            Assert.Equal(CrewCombatIntent.Pursue, crew.Update(true, 27, true, true));
        }
        [Fact] public void TargetLossResetsRangeState()
        {
            var crew = new CrewEngagement();
            crew.Update(true, 10, true, true);
            Assert.Equal(CrewCombatIntent.Idle, crew.Update(false, 10, true, true));
            Assert.Equal(CrewCombatIntent.Pursue, crew.Update(true, 27, true, true));
        }
        [Theory]
        [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
        public void InvalidDistanceCannotAuthorizeFire(double distance)
        {
            Assert.Equal(CrewCombatIntent.Idle, new CrewEngagement().Update(true, distance, true, true));
        }
    }
}
