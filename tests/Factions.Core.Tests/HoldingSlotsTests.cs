using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class HoldingSlotsTests
    {
        private static readonly Vec3 Station = new Vec3(0, 0, 0);

        [Fact]
        public void ACostlierShipOnATighterShareStandsFurtherApart()
        {
            double cheap = HoldingSlots.SpacingKm(0.2, 8);
            double costly = HoldingSlots.SpacingKm(2.0, 8);
            double tight = HoldingSlots.SpacingKm(0.2, 1);
            Assert.True(costly > cheap);
            Assert.True(tight > cheap);
            Assert.Equal(HoldingSlots.MinSpacingKm, HoldingSlots.SpacingKm(0, 8), 6);
            Assert.Equal(HoldingSlots.MaxSpacingKm, HoldingSlots.SpacingKm(50, 0.1), 6);
        }

        [Fact]
        public void TheFirstSlotIsOnTheApproachLineAtTheInnerRadius()
        {
            Vec3? p = HoldingSlots.Choose(Station, new Vec3(15, 0, 0), 1.0, new List<Vec3>(), null);
            Assert.True(p.HasValue);
            Assert.Equal(HoldingSlots.InnerKm, p.Value.X, 6);
            Assert.Equal(0.0, p.Value.Y, 6);
            Assert.Equal(0.0, p.Value.Z, 6);
        }

        [Fact]
        public void TheFirstRingCanSitPastTheVisibleRadius()
        {
            Vec3? p = HoldingSlots.Choose(Station, new Vec3(15, 0, 0), 1.0, new List<Vec3>(), null, 17.0);
            Assert.True(p.HasValue);
            Assert.Equal(17.0, p.Value.X, 6);
        }

        [Fact]
        public void SlotsKeepTheirSpacingFromEveryTakenSlot()
        {
            var taken = new List<Vec3>();
            for (int i = 0; i < 20; i++)
            {
                Vec3? p = HoldingSlots.Choose(Station, new Vec3(15, 0, 0), 1.5, taken, null);
                Assert.True(p.HasValue);
                foreach (Vec3 t in taken) Assert.True(t.DistanceTo(p.Value) >= 1.5 - 1e-9);
                taken.Add(p.Value);
            }
        }

        /// <summary>A surface station: no slot below the station, and none whose run to the station
        /// cuts through the body.</summary>
        [Fact]
        public void NearAPlanetNoSlotIsLowerThanTheStation()
        {
            var planet = new PlanetRecord
            {
                Id = "p", Center = new Vec3(0, -60, 0), SurfaceRadius = 60, GravityRadius = 120,
            };
            var catalogue = new StaticPlanetCatalogue(new[] { planet });
            // Approaching from the side and a little below the station's horizon.
            var taken = new List<Vec3>();
            for (int i = 0; i < 10; i++)
            {
                Vec3? p = HoldingSlots.Choose(Station, new Vec3(15, -2, 0), 1.0, taken, catalogue);
                Assert.True(p.HasValue);
                Assert.True(planet.Center.DistanceTo(p.Value) >= 60 + HoldingSlots.PlanetMarginKm - 1e-9);
                Assert.True(PlanetRoutePlanner.SegmentClear(p.Value, Station, planet, 0.0));
                taken.Add(p.Value);
            }
        }

        [Fact]
        public void ReoriginPutsTheFleetAtItsSlotAndKeepsItsTarget()
        {
            DateTime now = new DateTime(2026, 9, 30, 19, 0, 0, DateTimeKind.Utc);
            var tuning = TestSupport.Tuning();
            var fleet = new PendingFleetAction
            {
                OriginPosKm = new Vec3(100, 0, 0), TargetPosKm = Station, TargetNodeId = "N600",
                DepartedUtc = now.AddMinutes(-10), EstimatedArrivalUtc = now.AddMinutes(5),
                RouteDistanceKm = 120, PathNodeIds = new[] { "A", "N600" },
            };
            var slot = new Vec3(4, 0, 0);
            PendingFleetAction moved = HoldingSlots.Reorigin(fleet, slot, null, tuning, now);
            Assert.Equal(0.0, moved.CurrentPosKm(now, tuning).DistanceTo(slot), 6);
            Assert.Equal("N600", moved.TargetNodeId);
            Assert.True(moved.EstimatedArrivalUtc > now);
            Assert.Null(moved.PathNodeIds);
            Assert.Equal(0.0, moved.RouteDistanceKm, 6);
        }
    }
}
