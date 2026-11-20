using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="FleetRules.IsSamePlanetSurface"/> is the only gate that removes a physical
    /// requirement: when it holds, Patrol and Guard dispatch with no ship at all. So it has to be
    /// right about the ground, and these fixtures put nodes at real positions rather than asserting
    /// through <see cref="SpatialNode.Zone"/>. See docs/issues/FAC-096 stage 2.
    /// </summary>
    public class FleetRulesTests
    {
        // ---- fake planet catalogue ---------------------------------------------------------

        /// <summary>Real geometry, not an identity function: <see cref="NearestSurfacePoint"/>
        /// projects onto the body's surface sphere and <see cref="GravityAt"/> falls to zero at the
        /// gravity limit. An earlier version returned its input from NearestSurfacePoint, which made
        /// every position in the world read as standing on terrain.</summary>
        private sealed class FakePlanetCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord[] _planets;
            public FakePlanetCatalogue(params PlanetRecord[] planets) { _planets = planets; }
            public IEnumerable<PlanetRecord> Planets { get { return _planets; } }

            private PlanetRecord Nearest(Vec3 posKm)
            {
                PlanetRecord best = null;
                double bestDist = double.MaxValue;
                foreach (PlanetRecord p in _planets)
                {
                    double d = posKm.DistanceTo(p.Center);
                    if (d < bestDist) { best = p; bestDist = d; }
                }
                return best;
            }

            public double GravityAt(Vec3 posKm)
            {
                PlanetRecord p = Nearest(posKm);
                if (p == null) return 0;
                return posKm.DistanceTo(p.Center) >= p.GravityRadius ? 0 : 1.0;
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                PlanetRecord p = Nearest(posKm);
                if (p == null) return posKm;
                double d = posKm.DistanceTo(p.Center);
                if (d < 1e-6) return new Vec3(p.Center.X + p.SurfaceRadius, p.Center.Y, p.Center.Z);
                double k = p.SurfaceRadius / d;
                return new Vec3(p.Center.X + (posKm.X - p.Center.X) * k,
                                p.Center.Y + (posKm.Y - p.Center.Y) * k,
                                p.Center.Z + (posKm.Z - p.Center.Z) * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static PlanetRecord Planet(double cx, double cy, double cz)
            => new PlanetRecord { Id = "P" + cx, Center = new Vec3(cx, cy, cz), SurfaceRadius = 60, GravityRadius = 100 };

        /// <summary>The measured gas giant: a 9.5 km voxel core under a 191 km atmosphere, gravity
        /// limit 305 km. Relative gravity calls everything out to ~220 km "surface" here.</summary>
        private static PlanetRecord GasGiant()
            => new PlanetRecord { Id = "GG", Center = new Vec3(0, 0, 0), SurfaceRadius = 9.5, GravityRadius = 305,
                                  AtmosphereRadius = 191.3, HasAtmosphere = true };

        private static SpatialNode Node(double x, double y, double z, NodeZone zone)
            => new SpatialNode { Position = new Vec3(x, y, z), Zone = zone };

        // ---- the ground ---------------------------------------------------------------------

        [Fact]
        public void BothOnTheGround_SamePlanet_ReturnsTrue()
        {
            var cats = new FakePlanetCatalogue(Planet(0, 0, 0));
            Assert.True(FleetRules.IsSamePlanetSurface(
                Node(60, 0, 0, NodeZone.Surface), Node(-60, 0, 0, NodeZone.Surface), cats));
        }

        [Fact]
        public void BothOnTheGround_DifferentPlanets_ReturnsFalse()
        {
            var cats = new FakePlanetCatalogue(Planet(0, 0, 0), Planet(1000, 0, 0));
            Assert.False(FleetRules.IsSamePlanetSurface(
                Node(60, 0, 0, NodeZone.Surface), Node(1060, 0, 0, NodeZone.Surface), cats));
        }

        [Fact]
        public void NoPlanets_ReturnsFalse()
        {
            var cats = new FakePlanetCatalogue();
            Assert.False(FleetRules.IsSamePlanetSurface(
                Node(1, 0, 0, NodeZone.Surface), Node(-1, 0, 0, NodeZone.Surface), cats));
        }

        [Fact]
        public void OriginOffTheGround_ReturnsFalse()
        {
            // 20 km above a 60 km surface, still inside the gravity well.
            var cats = new FakePlanetCatalogue(Planet(0, 0, 0));
            Assert.False(FleetRules.IsSamePlanetSurface(
                Node(80, 0, 0, NodeZone.Surface), Node(-60, 0, 0, NodeZone.Surface), cats));
        }

        [Fact]
        public void TargetOffTheGround_ReturnsFalse()
        {
            var cats = new FakePlanetCatalogue(Planet(0, 0, 0));
            Assert.False(FleetRules.IsSamePlanetSurface(
                Node(60, 0, 0, NodeZone.Surface), Node(-80, 0, 0, NodeZone.Surface), cats));
        }

        [Fact]
        public void BothInDeepSpace_ReturnsFalse()
        {
            var cats = new FakePlanetCatalogue(Planet(0, 0, 0));
            Assert.False(FleetRules.IsSamePlanetSurface(
                Node(500, 0, 0, NodeZone.DeepSpace), Node(-500, 0, 0, NodeZone.DeepSpace), cats));
        }

        // ---- the defect this gate had ------------------------------------------------------

        [Fact]
        public void TroopsDoNotWalkAcrossAGasGiantsAtmosphere()
        {
            // 150 km either side of a 9.5 km core: 300 km apart, both deep in open air. Relative
            // gravity labels both Surface, which is what the gate used to read -- and a true answer
            // here dispatches Patrol/Guard with no ship at all.
            var cats = new FakePlanetCatalogue(GasGiant());
            SpatialNode a = Node(150, 0, 0, NodeZone.Surface);
            SpatialNode b = Node(-150, 0, 0, NodeZone.Surface);

            Assert.Equal(300.0, a.Position.DistanceTo(b.Position), 6);
            Assert.Equal(NodeZone.Surface, a.Zone);          // the label the old gate believed
            Assert.Equal(NodeZone.Surface, b.Zone);
            Assert.False(FleetRules.IsSamePlanetSurface(a, b, cats));
        }

        [Fact]
        public void OnTheGasGiantsOwnCore_ItStillCounts()
        {
            // The body is small, not fictional -- two nodes actually on its 9.5 km terrain are on the
            // same ground, and refusing them would be the opposite error.
            var cats = new FakePlanetCatalogue(GasGiant());
            Assert.True(FleetRules.IsSamePlanetSurface(
                Node(9.5, 0, 0, NodeZone.Surface), Node(-9.5, 0, 0, NodeZone.Surface), cats));
        }

        [Fact]
        public void ABodyThatCannotBeLandedOn_NeverCounts()
        {
            PlanetRecord giant = GasGiant();
            giant.AllowsSurfaceStations = false;
            var cats = new FakePlanetCatalogue(giant);

            Assert.False(FleetRules.IsSamePlanetSurface(
                Node(9.5, 0, 0, NodeZone.Surface), Node(-9.5, 0, 0, NodeZone.Surface), cats));
        }

        // ---- the label no longer decides, in either direction --------------------------------

        [Fact]
        public void AStaleSurfaceLabelInDeepSpace_DoesNotGrantAGroundOp()
        {
            // Zone is derived at boot and not persisted, so it can disagree with where a node is now.
            var cats = new FakePlanetCatalogue(Planet(0, 0, 0));
            Assert.False(FleetRules.IsSamePlanetSurface(
                Node(5000, 0, 0, NodeZone.Surface), Node(5001, 0, 0, NodeZone.Surface), cats));
        }

        [Fact]
        public void AStaleDeepSpaceLabelOnRealGround_DoesNotRefuseAGroundOp()
        {
            var cats = new FakePlanetCatalogue(Planet(0, 0, 0));
            Assert.True(FleetRules.IsSamePlanetSurface(
                Node(60, 0, 0, NodeZone.DeepSpace), Node(-60, 0, 0, NodeZone.DeepSpace), cats));
        }
    }
}
