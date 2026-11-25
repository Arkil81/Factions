using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ShipCrewTests
    {
        [Fact]
        public void OnlyADynamicHullWithPhysicsMayBeCrewed()
        {
            Assert.True(ShipCrew.MayCrewHull(isStatic: false, isPreview: false, hasPhysics: true));
            // A projector's preview: dynamic, seats present, no physics body.
            Assert.False(ShipCrew.MayCrewHull(isStatic: false, isPreview: true, hasPhysics: false));
            Assert.False(ShipCrew.MayCrewHull(isStatic: false, isPreview: true, hasPhysics: true));
            Assert.False(ShipCrew.MayCrewHull(isStatic: false, isPreview: false, hasPhysics: false));
            Assert.False(ShipCrew.MayCrewHull(isStatic: true, isPreview: false, hasPhysics: true));
        }

        [Fact]
        public void MissingOrIncompleteSurveyIsUnknown()
        {
            Assert.Equal(CrewHullClass.Unknown, ShipCrew.Estimate(null).Class);
            Assert.Equal(CrewHullClass.Unknown,
                ShipCrew.Estimate(new CrewLayout { PilotStations = 1 }).Class);
        }

        [Fact]
        public void SeatlessHullIsADroneEvenWithCryo()
        {
            var estimate = ShipCrew.Estimate(new CrewLayout { Known = true, CryoChambers = 3 });
            Assert.Equal(CrewHullClass.Drone, estimate.Class);
            Assert.Equal(0, estimate.Desired);
            Assert.Equal(0, estimate.PresenceSlots);
        }

        [Fact]
        public void PilotStationDoesNotBecomeAPassiveSpawnSlot()
        {
            var estimate = ShipCrew.Estimate(new CrewLayout { Known = true, PilotStations = 1 });
            Assert.Equal(CrewHullClass.PilotOnly, estimate.Class);
            Assert.Equal(1, estimate.Desired);
            Assert.Equal(0, estimate.PresenceSlots);
        }

        [Fact]
        public void CrewStationsDetermineComplementWithoutCargoOrWeaponThresholds()
        {
            var estimate = ShipCrew.Estimate(new CrewLayout
                { Known = true, PilotStations = 2, CrewSeats = 5, CryoChambers = 2 });
            Assert.Equal(CrewHullClass.CrewedShip, estimate.Class);
            Assert.Equal(7, estimate.Desired);
            Assert.Equal(5, estimate.PresenceSlots);
        }

        [Fact]
        public void AuthoredComplementAndSpawnLocationsRemainSeparate()
        {
            var estimate = ShipCrew.Estimate(new CrewLayout
                { Known = true, PilotStations = 1, CrewSeats = 3, AuthoredCrew = 10, SpawnPoints = 2 });
            Assert.True(estimate.Authored);
            Assert.Equal(10, estimate.Desired);
            Assert.Equal(2, estimate.PresenceSlots);
        }

        [Fact]
        public void AuthoredStandingCrewCanExistWithoutSeats()
        {
            var estimate = ShipCrew.Estimate(new CrewLayout { Known = true, SpawnPoints = 4 });
            Assert.Equal(CrewHullClass.CrewedShip, estimate.Class);
            Assert.Equal(4, estimate.Desired);
            Assert.Equal(4, estimate.PresenceSlots);
        }

        [Fact]
        public void TrialUsesOnlyEmptyPassiveSeatsAndCapsPresenceWithoutShrinkingDemand()
        {
            var layout = new CrewLayout { Known = true, CrewSeats = 20, AuthoredCrew = 12 };
            Assert.Equal(8, ShipCrew.TrialCount(layout, 20));
            Assert.Equal(2, ShipCrew.TrialCount(layout, 2));
            Assert.Equal(0, ShipCrew.TrialCount(layout, 0));
            Assert.Equal(12, ShipCrew.Estimate(layout).Desired);
        }

        [Fact]
        public void AStationCannotBeUsedAsTheShipMovementTrial()
        {
            var layout = new CrewLayout { Known = true, IsStation = true, CrewSeats = 5 };
            Assert.Equal(CrewHullClass.Station, ShipCrew.Estimate(layout).Class);
            Assert.Equal(0, ShipCrew.TrialCount(layout, 5));
        }
    }
}
