using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FleetTransitTests
    {
        private static FactionTuning Tuning(double acceleration)
        {
            return new FactionTuning
            {
                FleetSpeedMinKmPerMin = 1.0,
                FleetSpeedKmPerMin = 10.0,
                FleetAccelKmPerMin2 = acceleration,
            };
        }

        [Fact]
        public void TransitProgress_UsesTheAccelerationProfileRatherThanLinearTime()
        {
            FactionTuning tuning = Tuning(1.0);
            const double distanceKm = 20.0;
            double totalMins = tuning.TransitTimeMins(distanceKm);
            double atQuarterTime = tuning.TransitProgressRatio(distanceKm, totalMins * 0.25);
            double atHalfTime = tuning.TransitProgressRatio(distanceKm, totalMins * 0.50);
            double atThreeQuarterTime = tuning.TransitProgressRatio(distanceKm, totalMins * 0.75);

            Assert.True(atQuarterTime < 0.25, "departure should be slower than the old linear interpolation");
            Assert.Equal(0.5, atHalfTime, 6);
            Assert.True(atThreeQuarterTime > 0.75, "the fleet should cover more distance while decelerating late in transit");
        }

        [Fact]
        public void PlanetNavigationStops_UseWholeRouteTimingProfileForLegEta()
        {
            var tuning = Tuning(1.0);
            WorldModel w = new WorldModel(new SeededRandom(1));
            w.UtcNow = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });
            SpatialNode origin = w.Graph.Add(new Vec3(-200, 0, 0), 0.5, "A1");
            origin.OwnerFactionTag = "A";
            SpatialNode target = w.Graph.Add(new Vec3(200, 0, 0), 0.5, "B1");
            target.OwnerFactionTag = "B";

            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Raid, "A",
                new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1" }, w, tuning, immediate: false);
            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(origin.Position, target.Position,
                w.Planets.Planets, 10, fleet.FleetId);
            double expectedMins = tuning.TransitElapsedMinsAtDistance(plan.DistanceKm,
                origin.Position.DistanceTo(fleet.TargetPosKm));

            Assert.Equal(expectedMins, (fleet.EstimatedArrivalUtc - fleet.DepartedUtc).TotalMinutes, 6);
        }

        [Fact]
        public void TransitReschedule_PreservesCurrentPositionAndUsesTheNewArrivalSchedule()
        {
            DateTime now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
            FactionTuning oldTuning = Tuning(1.0);
            FactionTuning newTuning = Tuning(100.0);
            double oldDuration = oldTuning.TransitTimeMins(20.0);

            var world = new WorldModel(new SeededRandom(1));
            world.UtcNow = now;
            SpatialNode origin = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            origin.OwnerFactionTag = "A";
            SpatialNode target = world.Graph.Add(new Vec3(20, 0, 0), 0.5, "B1");
            target.OwnerFactionTag = "B";
            world.Graph.Connect("A1", "B1");

            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Trade,
                ActorTag = "A",
                OriginNodeId = "A1",
                TargetNodeId = "B1",
                OriginPosKm = origin.Position,
                TargetPosKm = target.Position,
                PathNodeIds = new[] { "A1", "B1" },
                DepartedUtc = now.AddMinutes(-oldDuration * 0.5),
                EstimatedArrivalUtc = now.AddMinutes(oldDuration * 0.5),
            };
            var actor = new FactionState { Tag = "A" };
            actor.PendingFleets.Add(fleet);
            var states = new List<FactionState> { actor };
            MethodInfo reschedule = typeof(PendingFleetResolver).GetMethod("ReschedulePendingFleetTransits");

            Assert.NotNull(reschedule);
            int changed = (int)reschedule.Invoke(null, new object[] { states, world, newTuning, now });
            PendingFleetAction updated = actor.PendingFleets[0];
            double newPositionRatio = newTuning.TransitProgressRatio(
                20.0, (now - updated.DepartedUtc).TotalMinutes);

            Assert.Equal(1, changed);
            Assert.Equal(0.5, newPositionRatio, 6);
            Assert.True(updated.EstimatedArrivalUtc < now.AddMinutes(oldDuration * 0.5),
                "the remaining ETA must shrink under the higher acceleration tuning");
        }
    }
}
