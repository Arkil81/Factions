using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CrewVisitTests
    {
        [Fact] public void NeverEnteredShipsStayEmpty()
        {
            var visit = new CrewVisit();
            for (int i = 0; i < 200; i++) Assert.False(visit.Update(false, 2));
        }
        [Fact] public void LastVisitorLeavingExpiresEvenWithNearbyObservers()
        {
            var visit = new CrewVisit();
            Assert.True(visit.Update(true, 2));
            Assert.True(visit.Update(false, 18));
            Assert.False(visit.Update(false, 2));
        }
        [Fact] public void AnotherVisitorOrBriefExcursionKeepsTheCrew()
        {
            var visit = new CrewVisit();
            visit.Update(true, 2);
            visit.Update(false, 18);
            Assert.True(visit.Update(true, 2));
            Assert.Equal(0, visit.AbsentSeconds);
            Assert.True(visit.Update(false, 18));
        }
        [Fact] public void LaterEntryStartsANewVisit()
        {
            var visit = new CrewVisit();
            visit.Update(true, 2);
            Assert.False(visit.Update(false, 20));
            Assert.True(visit.Update(true, 2));
            Assert.Equal(0, visit.AbsentSeconds);
        }
    }
}
