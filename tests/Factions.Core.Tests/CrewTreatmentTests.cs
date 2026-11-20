using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CrewTreatmentTests
    {
        [Fact] public void LowHealthMustReachCoverBeforeConsuming()
        {
            var p = new CrewTreatment();
            Assert.Equal(CrewTreatmentIntent.Combat, p.Update(0, 0.5, true, true, false, false));
            Assert.Equal(CrewTreatmentIntent.Retreat, p.Update(1, 0.49, true, true, false, false));
            Assert.Equal(CrewTreatmentIntent.UseKit, p.Update(1, 0.49, true, true, false, true));
        }
        [Fact] public void ActiveMedpackSuppressesRetreatDespiteDamageOrLostCover()
        {
            var p = new CrewTreatment();
            p.Update(1, 0.4, true, true, false, false);
            Assert.Equal(CrewTreatmentIntent.Healing, p.Update(1, 0.1, true, true, true, false));
            Assert.Equal(CrewTreatmentIntent.Healing, p.Update(30, 0.1, true, false, true, false));
            Assert.Equal(CrewTreatmentIntent.Retreat, p.Update(0, 0.1, true, true, false, false));
        }
        [Fact] public void CompletedHealingReturnsToCombatAboveThreshold()
        {
            var p = new CrewTreatment();
            p.Update(1, 0.4, true, true, true, true);
            Assert.Equal(CrewTreatmentIntent.Combat, p.Update(1, 0.65, true, true, false, true));
        }
        [Fact] public void RetreatTimeoutAllowsCombatBeforeRetry()
        {
            var p = new CrewTreatment();
            Assert.Equal(CrewTreatmentIntent.Retreat, p.Update(19, 0.4, true, true, false, false));
            Assert.Equal(CrewTreatmentIntent.Combat, p.Update(1, 0.4, true, true, false, false));
            Assert.Equal(CrewTreatmentIntent.Combat, p.Update(9, 0.4, true, true, false, true));
            Assert.Equal(CrewTreatmentIntent.Retreat, p.Update(1, 0.4, true, true, false, false));
        }
        [Fact] public void EmptyInventoryOrLostThreatCancelsRetreat()
        {
            var p = new CrewTreatment();
            p.Update(1, 0.1, true, true, false, false);
            Assert.Equal(CrewTreatmentIntent.Combat, p.Update(1, 0.1, false, true, false, false));
            p.Update(1, 0.1, true, true, false, false);
            Assert.Equal(CrewTreatmentIntent.Combat, p.Update(1, 0.1, true, false, false, false));
        }
    }
}
