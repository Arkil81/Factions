using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Bench phantom players: where they are placed, how a patrol moves, and when a parked
    /// one is put back. The host's identities, hulls and characters are live-verified.</summary>
    public class PhantomPlanTests
    {
        private static SpatialNode Node(string id, string owner, double x = 0, double y = 0, double z = 0)
        {
            return new SpatialNode { Id = id, OwnerFactionTag = owner, Position = new Vec3(x, y, z) };
        }

        private static List<SpatialNode> Nodes(int count)
        {
            var nodes = new List<SpatialNode>();
            for (int i = 0; i < count; i++) nodes.Add(Node("N" + i.ToString("00"), "A", x: i * 100));
            return nodes;
        }

        [Fact]
        public void PhantomSteamIdsAreRecognisedAndRealOnesAreNot()
        {
            Assert.True(PhantomPlan.IsPhantomSteamId(PhantomPlan.SteamIdBase + 1));
            Assert.True(PhantomPlan.IsPhantomSteamId(PhantomPlan.SteamIdBase + 31));
            Assert.False(PhantomPlan.IsPhantomSteamId(76561198000000000UL));
            Assert.False(PhantomPlan.IsPhantomSteamId(0));
        }

        [Fact]
        public void CandidatesAreTheOwnedNodesInIdOrder()
        {
            var nodes = new List<SpatialNode> { Node("C", "A"), Node("A", "B"), Node("B", null), Node("D", "") };
            List<SpatialNode> c = PhantomPlan.Candidates(nodes);
            Assert.Equal(new[] { "A", "C" }, c.Select(n => n.Id).ToArray());
        }

        [Fact]
        public void SpreadPicksDistinctNodesUntilTheyRunOut()
        {
            List<SpatialNode> nodes = Nodes(5);
            List<SpatialNode> three = PhantomPlan.Spread(nodes, 3, 7);
            Assert.Equal(3, three.Distinct().Count());

            List<SpatialNode> seven = PhantomPlan.Spread(nodes, 7, 7);
            Assert.Equal(5, seven.Distinct().Count());
            Assert.Equal(seven[0], seven[5]);
        }

        [Fact]
        public void SpreadIsDeterministicForASeedAndMovesWithIt()
        {
            List<SpatialNode> nodes = Nodes(20);
            string a = string.Join(",", PhantomPlan.Spread(nodes, 6, 3).Select(n => n.Id));
            string b = string.Join(",", PhantomPlan.Spread(nodes, 6, 3).Select(n => n.Id));
            string c = string.Join(",", PhantomPlan.Spread(nodes, 6, 4).Select(n => n.Id));
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void SpreadOfNothingIsEmpty()
        {
            Assert.Empty(PhantomPlan.Spread(new List<SpatialNode>(), 4, 1));
        }

        [Fact]
        public void ARouteNeverRepeatsANodeBackToBackNorLoopsOntoItsStart()
        {
            List<SpatialNode> nodes = Nodes(4);
            for (int seed = 0; seed < 200; seed++)
            {
                List<SpatialNode> r = PhantomPlan.Route(nodes, 5, new Random(seed));
                Assert.Equal(5, r.Count);
                for (int i = 1; i < r.Count; i++) Assert.NotSame(r[i - 1], r[i]);
                Assert.NotSame(r[r.Count - 1], r[0]);
            }
        }

        [Fact]
        public void ARouteOverTwoNodesAlternatesAndTerminates()
        {
            List<SpatialNode> nodes = Nodes(2);
            List<SpatialNode> r = PhantomPlan.Route(nodes, 5, new Random(1));
            Assert.Equal(5, r.Count);
            for (int i = 1; i < r.Count; i++) Assert.NotSame(r[i - 1], r[i]);
        }

        [Fact]
        public void AStandoffOverAPlanetIsStraightUpFromItsCentre()
        {
            var node = new Vec3(0, 60, 0);
            Vec3 s = PhantomPlan.Standoff(node, new Vec3(0, 0, 0), 3, new Random(1));
            Assert.Equal(0, s.X, 9);
            Assert.Equal(63, s.Y, 9);
            Assert.Equal(0, s.Z, 9);
        }

        [Fact]
        public void AStandoffInDeepSpaceIsTheGivenDistanceAway()
        {
            var node = new Vec3(1000, -500, 20);
            for (int seed = 0; seed < 50; seed++)
                Assert.Equal(3.0, PhantomPlan.Standoff(node, null, 3, new Random(seed)).DistanceTo(node), 9);
        }

        [Fact]
        public void APlanetCentreOnTheNodeFallsBackToARandomDirection()
        {
            var node = new Vec3(5, 5, 5);
            Assert.Equal(2.0, PhantomPlan.Standoff(node, node, 2, new Random(1)).DistanceTo(node), 9);
        }

        [Fact]
        public void HoldPutsBackAWandererStopsADrifterAndLeavesASleeperAlone()
        {
            var anchor = new Vec3(0, 0, 0);
            bool teleport, stop;

            PhantomPlan.Hold(new Vec3(0.2, 0, 0), anchor, 0, out teleport, out stop);
            Assert.True(teleport);
            Assert.False(stop);

            PhantomPlan.Hold(new Vec3(0.05, 0, 0), anchor, 3, out teleport, out stop);
            Assert.False(teleport);
            Assert.True(stop);

            PhantomPlan.Hold(new Vec3(0.05, 0, 0), anchor, 0.1, out teleport, out stop);
            Assert.False(teleport);
            Assert.False(stop);
        }

        [Fact]
        public void APatrolSteersAtItsWaypointAtItsSpeed()
        {
            var patrol = new PhantomPatrol(new List<Vec3> { new Vec3(10, 0, 0), new Vec3(0, 10, 0) }, 100);
            PhantomPatrol.Step s = patrol.Advance(new Vec3(0, 0, 0));
            Assert.False(s.Teleport);
            Assert.False(s.Advanced);
            Assert.Equal(100, s.VelocityMs.X, 9);
            Assert.Equal(0, s.VelocityMs.Y, 9);
        }

        [Fact]
        public void APatrolMovesOnAtAWaypointAndCountsALapAtTheLoop()
        {
            var patrol = new PhantomPatrol(new List<Vec3> { new Vec3(10, 0, 0), new Vec3(0, 10, 0) }, 100);
            PhantomPatrol.Step s = patrol.Advance(new Vec3(10, 0.1, 0));
            Assert.True(s.Advanced);
            Assert.Equal(1, patrol.Next);
            Assert.True(s.VelocityMs.Y > 0);

            s = patrol.Advance(new Vec3(0, 10, 0));
            Assert.True(s.Advanced);
            Assert.Equal(0, patrol.Next);
            Assert.Equal(1, patrol.Laps);
        }

        [Fact]
        public void APatrolThatMakesNoProgressIsPutOnItsWaypoint()
        {
            var target = new Vec3(10, 0, 0);
            var patrol = new PhantomPatrol(new List<Vec3> { target, new Vec3(0, 10, 0) }, 100);
            var stuck = new Vec3(5, 0, 0);
            PhantomPatrol.Step s = default(PhantomPatrol.Step);
            int seconds = 0;
            while (!s.Teleport && seconds < 100) { s = patrol.Advance(stuck); seconds++; }
            Assert.True(s.Teleport);
            Assert.Equal(PhantomPatrol.StuckSeconds + 1, seconds);
            Assert.Equal(0, s.TeleportToKm.DistanceTo(target), 9);
        }

        [Fact]
        public void APatrolMakingProgressIsNeverTeleported()
        {
            var patrol = new PhantomPatrol(new List<Vec3> { new Vec3(100, 0, 0), new Vec3(0, 0, 0) }, 100);
            var pos = new Vec3(0, 0, 0);
            for (int i = 0; i < 600; i++)
            {
                PhantomPatrol.Step s = patrol.Advance(pos);
                Assert.False(s.Teleport);
                pos = new Vec3(pos.X + s.VelocityMs.X / 1000.0, pos.Y + s.VelocityMs.Y / 1000.0, pos.Z + s.VelocityMs.Z / 1000.0);
            }
        }

        [Fact]
        public void AVirtualPatrolTravelsItsSpeedAndTurnsAtTheWaypoint()
        {
            var patrol = new PhantomPatrol(new List<Vec3> { new Vec3(1, 0, 0), new Vec3(1, 1, 0) }, 400);
            Vec3 p = patrol.AdvanceVirtual(new Vec3(0, 0, 0), 1.0);
            Assert.Equal(0.4, p.X, 9);
            p = patrol.AdvanceVirtual(p, 1.0);
            p = patrol.AdvanceVirtual(p, 1.0);
            Assert.Equal(1.0, p.X, 9);
            Assert.Equal(1, patrol.Next);
            p = patrol.AdvanceVirtual(p, 1.0);
            Assert.Equal(0.4, p.Y, 9);
        }

        [Fact]
        public void APatrolNeedsAWaypoint()
        {
            Assert.Throws<ArgumentException>(() => new PhantomPatrol(new List<Vec3>(), 10));
        }

        [Fact]
        public void WithinKeepsOnlyTheCandidatesInsideTheRadiusInOrder()
        {
            var a = new SpatialNode { Id = "N001", OwnerFactionTag = "X", Position = new Vec3(0, 0, 0) };
            var b = new SpatialNode { Id = "N002", OwnerFactionTag = "X", Position = new Vec3(50, 0, 0) };
            var c = new SpatialNode { Id = "N003", OwnerFactionTag = "X", Position = new Vec3(0, 150, 0) };
            var d = new SpatialNode { Id = "N004", OwnerFactionTag = "X", Position = new Vec3(0, 0, 100) };
            var got = PhantomPlan.Within(new List<SpatialNode> { a, b, c, d }, new Vec3(0, 0, 0), 100);
            Assert.Equal(new[] { "N001", "N002", "N004" }, got.ConvertAll(n => n.Id).ToArray());
        }
    }
}
