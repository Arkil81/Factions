using System;
using System.Collections.Generic;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A miner on its dwell works a ring around the deposit (<see cref="MineWander"/>), and
    /// leaves for its delivery stop from wherever on that ring it got to.</summary>
    public class MineWanderTests
    {
        private sealed class UpCatalogue : IPlanetCatalogue
        {
            private readonly double _g;
            private readonly Vec3 _up;
            public UpCatalogue(double g, Vec3 up) { _g = g; _up = up; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return _up; }
        }

        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>A dwell leg; <paramref name="actor"/> is what tells two of them apart.</summary>
        private static PendingFleetAction Dwell(string actor, Vec3 deposit, Vec3? up = null, double dwellSec = 600)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = actor, FleetId = "abc123",
                OriginNodeId = "Ore1", TargetNodeId = "Ore1",
                OriginPosKm = deposit, TargetPosKm = deposit,
                DepartedUtc = T0, EstimatedArrivalUtc = T0.AddSeconds(dwellSec),
                CurrentStopKind = FleetStopKind.Mine,
                MineWanderUp = up,
                LastResolvedSegment = -1,
            };
        }

        private static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        /// <summary>Least distance from <paramref name="c"/> to the segment a-b.</summary>
        private static double SegmentDistance(Vec3 a, Vec3 b, Vec3 c)
        {
            var ab = new Vec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var ac = new Vec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            double len2 = Dot(ab, ab);
            double t = len2 > 0 ? Math.Max(0, Math.Min(1, Dot(ac, ab) / len2)) : 0;
            var p = new Vec3(a.X + ab.X * t, a.Y + ab.Y * t, a.Z + ab.Z * t);
            return p.DistanceTo(c);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EveryRingLeg_IsLongEnough_AndStaysClearOfTheDeposit(bool inGravity)
        {
            var deposit = new Vec3(1000, -250, 40);
            Vec3? up = inGravity ? new Vec3(0, 0.6, 0.8) : (Vec3?)null;
            double shortest = double.MaxValue, closest = double.MaxValue;
            for (int f = 0; f < 200; f++)
            {
                PendingFleetAction fleet = Dwell("F" + f.ToString("X4"), deposit, up);
                MineWander.Ring ring = MineWander.RingOf(fleet);
                for (int i = 0; i < 60; i++)
                {
                    Vec3 a = MineWander.Waypoint(ring, i), b = MineWander.Waypoint(ring, i + 1);
                    shortest = Math.Min(shortest, a.DistanceTo(b));
                    closest = Math.Min(closest, SegmentDistance(a, b, deposit));
                }
            }
            Assert.True(shortest >= MineWander.MinLegKm, "shortest ring leg " + shortest + " km");
            Assert.True(closest >= MineWander.ClearanceKm, "closest approach " + closest + " km");
        }

        [Fact]
        public void InGravity_EveryWaypointSitsAboveTheTangentPlane()
        {
            var deposit = new Vec3(0, 60, 0);
            var up = new Vec3(0, 1, 0);
            for (int f = 0; f < 100; f++)
            {
                MineWander.Ring ring = MineWander.RingOf(Dwell("G" + f, deposit, up));
                for (int i = 0; i < 40; i++)
                {
                    Vec3 w = MineWander.Waypoint(ring, i);
                    double height = Dot(new Vec3(w.X - deposit.X, w.Y - deposit.Y, w.Z - deposit.Z), up);
                    Assert.True(height >= MineWander.GravityLiftKm - 1e-9, "waypoint " + i + " at " + height + " km");
                }
            }
        }

        [Fact]
        public void InDeepSpace_TheRingIsNotAlwaysOnePlane()
        {
            // The other arm of the gravity test: without a tilt every ring would sit in one plane.
            var deposit = new Vec3(0, 0, 0);
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int f = 0; f < 50; f++)
            {
                Vec3 w = MineWander.Waypoint(MineWander.RingOf(Dwell("S" + f, deposit)), 3);
                minY = Math.Min(minY, w.Y);
                maxY = Math.Max(maxY, w.Y);
            }
            Assert.True(maxY - minY > 20, "waypoint Y spread " + (maxY - minY) + " km");
        }

        [Fact]
        public void TheMiner_LeavesTheDeposit_AndIsOutOnTheRingAfterTheOutboundLeg()
        {
            var tuning = TestSupport.Tuning();
            tuning.FleetSpeedKmPerMin = 60;   // 1 km/s
            var deposit = new Vec3(500, 500, 500);
            PendingFleetAction fleet = Dwell("ABC123", deposit);

            Assert.Equal(0.0, fleet.CurrentPosKm(T0, tuning).DistanceTo(deposit), 9);
            // The outbound leg is at most RingMax + SpaceTilt long, so 60 s at 1 km/s is past it.
            for (int s = 60; s <= 600; s += 7)
            {
                double d = fleet.CurrentPosKm(T0.AddSeconds(s), tuning).DistanceTo(deposit);
                Assert.True(d >= MineWander.ClearanceKm, "at " + s + " s the miner is " + d + " km out");
            }
        }

        [Fact]
        public void ThePath_IsAPureFunctionOfTheDwellLeg()
        {
            var tuning = TestSupport.Tuning();
            var deposit = new Vec3(10, 20, 30);
            DateTime at = T0.AddSeconds(137);
            Vec3 a = Dwell("ABC", deposit).CurrentPosKm(at, tuning);
            PendingFleetAction renamed = Dwell("ABC", deposit);
            renamed.FleetId = "other-process-id";   // FleetId is not an input
            Vec3 b = renamed.CurrentPosKm(at, tuning);
            Vec3 other = Dwell("ABD", deposit).CurrentPosKm(at, tuning);
            Assert.Equal(0.0, a.DistanceTo(b), 12);
            Assert.True(a.DistanceTo(other) > 1.0, "two factions flew the same ring");
        }

        [Fact]
        public void LegAt_Advances_AndNamesTheWaypointBeingFlownTo()
        {
            var tuning = TestSupport.Tuning();
            tuning.FleetSpeedKmPerMin = 60;      // 1 km/s: the outbound leg takes its length in seconds
            tuning.ThrusterCruiseSpeedMs = 1000; // 1 km/s: a ring leg is NominalLegKm seconds
            PendingFleetAction fleet = Dwell("ABC123", new Vec3(0, 0, 0));
            MineWander.Ring ring = MineWander.RingOf(fleet);
            double outbound = fleet.OriginPosKm.DistanceTo(MineWander.Waypoint(ring, 0));

            double t;
            Assert.Equal(0, MineWander.LegAt(fleet, ring, T0.AddSeconds(outbound / 2), tuning, out t));
            Assert.Equal(1, MineWander.LegAt(fleet, ring, T0.AddSeconds(outbound + 1), tuning, out t));
            Assert.Equal(4, MineWander.LegAt(fleet, ring, T0.AddSeconds(outbound + 3 * MineWander.NominalLegKm + 1), tuning, out t));

            // Mid-leg the abstract position is between the two waypoints the leg joins.
            Vec3 mid = MineWander.PositionAt(fleet, T0.AddSeconds(outbound + 1.5 * MineWander.NominalLegKm), tuning);
            Vec3 w1 = MineWander.Waypoint(ring, 1), w2 = MineWander.Waypoint(ring, 2);
            Assert.Equal(w1.DistanceTo(w2), mid.DistanceTo(w1) + mid.DistanceTo(w2), 6);
        }

        /// <summary>The ring is flown at the materialised grid's cruise speed, not the abstract transit
        /// speed. At production's 500 km/min a 20 km leg took 2.4 s, the abstract miner lapped its ring
        /// every 20 s, and it swept back into a player's range within seconds of its grid leaving it, so
        /// each miner near a player was spawned again about once a minute.</summary>
        [Fact]
        public void TheRing_IsFlownAtCruiseSpeed_NotAtTransitSpeed()
        {
            var tuning = TestSupport.Tuning();
            tuning.FleetSpeedKmPerMin = 500;
            tuning.ThrusterCruiseSpeedMs = 100;
            PendingFleetAction fleet = Dwell("ABC123", new Vec3(0, 0, 0), dwellSec: 3600);
            MineWander.Ring ring = MineWander.RingOf(fleet);
            double outboundSec = fleet.OriginPosKm.DistanceTo(MineWander.Waypoint(ring, 0)) / (500 / 60.0);
            DateTime onRing = T0.AddSeconds(outboundSec + 1);

            double t;
            Assert.Equal(1, MineWander.LegAt(fleet, ring, onRing, tuning, out t));
            Assert.Equal(1, MineWander.LegAt(fleet, ring, onRing.AddSeconds(199 - 1), tuning, out t));
            Assert.Equal(2, MineWander.LegAt(fleet, ring, onRing.AddSeconds(200), tuning, out t));

            // Second by second over ten minutes, no faster than a real ring leg flown in 200 s: the
            // longest is under 30 km, so 0.15 km/s. Before the fix it was over 8 km/s.
            Vec3 prev = MineWander.PositionAt(fleet, onRing, tuning);
            double fastest = 0;
            for (int s = 1; s <= 600; s++)
            {
                Vec3 p = MineWander.PositionAt(fleet, onRing.AddSeconds(s), tuning);
                fastest = Math.Max(fastest, p.DistanceTo(prev));
                prev = p;
            }
            Assert.True(fastest <= 0.15, "the abstract miner moved " + fastest.ToString("F2") + " km in one second");
        }

        [Fact]
        public void UpAt_IsTheSurfaceNormalInGravity_AndNullInDeepSpace()
        {
            Assert.Null(MineWander.UpAt(new UpCatalogue(0, new Vec3(0, 1, 0)), new Vec3(0, 0, 0)));
            Vec3? up = MineWander.UpAt(new UpCatalogue(0.8, new Vec3(0, 2, 0)), new Vec3(0, 0, 0));
            Assert.True(up.HasValue);
            Assert.Equal(1.0, up.Value.Y, 9);
        }

        [Fact]
        public void Applies_OnlyToAMinerOnItsDwell()
        {
            PendingFleetAction dwell = Dwell("A", new Vec3(0, 0, 0));
            Assert.True(MineWander.Applies(dwell));
            PendingFleetAction delivering = dwell;
            delivering.CurrentStopKind = FleetStopKind.Service;
            Assert.False(MineWander.Applies(delivering));
            PendingFleetAction haul = dwell;
            haul.Kind = ActionKind.Haul;
            Assert.False(MineWander.Applies(haul));
        }

        // ---- Core integration ----------------------------------------------------------------

        private sealed class UnitVolumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string invKey) { return 1.0; }
        }

        private static PendingFleetAction DwellFor(WorldModel world, SpatialNode source, DateTime departed, DateTime ends)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = source.Id, TargetNodeId = source.Id,
                OriginPosKm = source.Position, TargetPosKm = source.Position,
                DepartedUtc = departed, EstimatedArrivalUtc = ends,
                CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { ["Ore:Iron"] = 1.0 },
                MineTargetLiters = 1000,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "Dest1", Kind = FleetStopKind.Service } },
                CargoCapacityLiters = 1_000_000,
                FleetId = "abc123",
                LastResolvedSegment = -1,
            };
        }

        [Fact]
        public void TheDeliveryLeg_DepartsFromTheRing_NotFromTheDeposit()
        {
            WorldModel world = TestSupport.World();
            SpatialNode source = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Ore1");
            source.OwnerFactionTag = "F";
            SpatialNode dest = world.Graph.Add(new Vec3(400, 0, 0), 1.0, "Dest1");
            dest.OwnerFactionTag = "F";
            FactionTuning tuning = world.Tuning;

            var st = new FactionState { Tag = "F" };
            PendingFleetAction dwell = DwellFor(world, source, world.UtcNow.AddSeconds(-300), world.UtcNow);
            Vec3 ringPos = MineWander.PositionAt(dwell, world.UtcNow, tuning);
            Assert.True(ringPos.DistanceTo(source.Position) >= MineWander.ClearanceKm);   // the arrangement
            st.PendingFleets.Add(dwell);

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, tuning,
                new SeededRandom(1), new List<string>(), null, null, null, new UnitVolumes());

            PendingFleetAction f = st.PendingFleets[0];
            Assert.Equal(FleetStopKind.Service, f.CurrentStopKind);
            Assert.Equal(0.0, f.OriginPosKm.DistanceTo(ringPos), 6);
            Assert.Null(f.MineWanderUp);
            Assert.False(MineWander.Applies(f));
        }

        [Fact]
        public void AMaterialisedMinerOnItsDwell_IsNotDeferred_SoTheDwellEndsOnItsOwnClock()
        {
            WorldModel world = TestSupport.World();
            SpatialNode source = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Ore1");
            source.OwnerFactionTag = "F";
            world.Graph.Add(new Vec3(400, 0, 0), 1.0, "Dest1").OwnerFactionTag = "F";

            var st = new FactionState { Tag = "F" };
            PendingFleetAction dwell = DwellFor(world, source, world.UtcNow.AddSeconds(-300), world.UtcNow);
            dwell.MaterialisedEntityId = 4242;   // a grid is out on the ring
            st.PendingFleets.Add(dwell);

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, world.Tuning,
                new SeededRandom(1), log, null, null, null, new UnitVolumes());

            Assert.Contains(log, l => l.Contains("MINE_EXTRACTED"));
            Assert.Equal(FleetStopKind.Service, st.PendingFleets[0].CurrentStopKind);
        }

        [Fact]
        public void AMaterialisedDelivery_IsStillDeferred()
        {
            // The other arm: only the dwell is exempt. A miner's grid flying its delivery leg must
            // not be resolved out from under it.
            WorldModel world = TestSupport.World();
            world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Ore1").OwnerFactionTag = "F";
            SpatialNode dest = world.Graph.Add(new Vec3(400, 0, 0), 1.0, "Dest1");
            dest.OwnerFactionTag = "F";

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Ore1", TargetNodeId = "Dest1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = dest.Position,
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                Cargo = new Dictionary<string, long> { ["Ore:Iron"] = 100 },
                MaterialisedEntityId = 4242,
                FleetId = "abc123",
                LastResolvedSegment = -1,
            });

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, world.Tuning,
                new SeededRandom(1), log, null, null, null, new UnitVolumes());

            Assert.DoesNotContain(log, l => l.Contains("MINE_RESOLVED"));
            Assert.False(st.PendingFleets[0].Resolved);
        }
    }
}
