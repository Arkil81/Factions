using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CrewIntrusionTests
    {
        [Fact] public void DeliberateDamageIsAnActAndAccidentsAreNot()
        {
            Assert.True(CrewIntrusion.IsHostileAct("Grind"));
            Assert.True(CrewIntrusion.IsHostileAct("Bullet"));
            Assert.True(CrewIntrusion.IsHostileAct("Explosion"));
            Assert.False(CrewIntrusion.IsHostileAct("Deformation"));
            Assert.False(CrewIntrusion.IsHostileAct("Fall"));
            Assert.False(CrewIntrusion.IsHostileAct("Weld"));
            Assert.False(CrewIntrusion.IsHostileAct("Temperature"));
            Assert.False(CrewIntrusion.IsHostileAct(null));
        }

        [Fact] public void FactionMembersAreInsideTheirHull()
        {
            Assert.False(CrewIntrusion.IsOutsider(11, 7, 7, 0));
            Assert.True(CrewIntrusion.IsOutsider(11, 8, 7, 0));
            Assert.True(CrewIntrusion.IsOutsider(11, 0, 7, 0));
        }

        [Fact] public void ASoloOwnerIsInsideAndEveryoneElseOutside()
        {
            Assert.False(CrewIntrusion.IsOutsider(11, 0, 0, 11));
            Assert.True(CrewIntrusion.IsOutsider(12, 0, 0, 11));
            Assert.True(CrewIntrusion.IsOutsider(12, 5, 0, 11));
        }

        [Fact] public void EveryPlayerIsOutsideAnUnownedHullAndNobodyIsNeverOutside()
        {
            Assert.True(CrewIntrusion.IsOutsider(12, 5, 0, 0));
            Assert.False(CrewIntrusion.IsOutsider(0, 5, 0, 0));
        }

        [Fact] public void AnIntruderIsMarkedOncePerHull()
        {
            var ledger = new IntruderLedger();
            Assert.True(ledger.Mark(100, 11, 0));
            Assert.False(ledger.Mark(100, 11, 0));
            Assert.True(ledger.Mark(200, 11, 0));
            Assert.True(ledger.IsIntruder(100, 11, 0));
            Assert.False(ledger.IsIntruder(300, 11, 0));
            Assert.False(ledger.Mark(100, 0, 5));
        }

        [Fact] public void FactionMatesOfAnIntruderAreIntrudersOnThatHullOnly()
        {
            var ledger = new IntruderLedger();
            ledger.Mark(100, 11, 5);
            Assert.True(ledger.IsIntruder(100, 12, 5));
            Assert.False(ledger.IsIntruder(100, 13, 6));
            Assert.False(ledger.IsIntruder(100, 13, 0));
            Assert.False(ledger.IsIntruder(200, 12, 5));
        }

        [Fact] public void AFactionlessIntruderSpreadsToNobody()
        {
            var ledger = new IntruderLedger();
            ledger.Mark(100, 11, 0);
            Assert.False(ledger.IsIntruder(100, 12, 0));
        }

        [Fact] public void ForgettingAHullClearsItsIntruders()
        {
            var ledger = new IntruderLedger();
            ledger.Mark(100, 11, 5);
            ledger.Forget(100);
            Assert.False(ledger.Knows(100));
            Assert.False(ledger.IsIntruder(100, 11, 5));
            Assert.False(ledger.IsIntruder(100, 12, 5));
        }

        [Fact] public void AlarmArmsAtOnceAndStandsDownAfterAQuietMinute()
        {
            var alarm = new CrewAlarm();
            Assert.False(alarm.Update(false, 2));
            Assert.True(alarm.Update(true, 2));
            Assert.True(alarm.Update(false, 58));
            Assert.True(alarm.Update(true, 2));
            Assert.True(alarm.Update(false, 59));
            Assert.False(alarm.Update(false, 1));
            Assert.Equal(0, alarm.QuietSeconds);
        }
    }
}
