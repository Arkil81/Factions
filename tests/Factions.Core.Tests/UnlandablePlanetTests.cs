using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A body whose generator definition forbids surface stations must never receive a Surface node.
    /// The live case is a gas giant: its "surface" is a 9.5 km solid core under a 191 km atmosphere,
    /// and SE's own station generator already refuses to put an outpost there.
    /// </summary>
    public class UnlandablePlanetTests
    {
        /// <summary>One body at the origin, full gravity everywhere inside its well, with the
        /// landability flag under the test's control. <c>NearestSurfacePoint</c> returns a point on
        /// the surface sphere so a snap is observable as a real move.</summary>
        private sealed class OneBodyCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord _planet;
            public OneBodyCatalogue(bool allowsSurface)
            {
                _planet = new PlanetRecord
                {
                    Id = "GG", Name = "Cauldron", Generator = "RealGasGiant",
                    Center = new Vec3(0, 0, 0), SurfaceRadius = 9.5, GravityRadius = 300,
                    AllowsSurfaceStations = allowsSurface,
                };
            }
            public IEnumerable<PlanetRecord> Planets { get { yield return _planet; } }
            // 1 g at/inside the surface sphere, still non-zero out to the gravity radius.
            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_planet.Center);
                if (d <= _planet.SurfaceRadius) return 1.0;
                if (d >= _planet.GravityRadius) return 0.0;
                return 1.0 - (d - _planet.SurfaceRadius) / (_planet.GravityRadius - _planet.SurfaceRadius);
            }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return new Vec3(_planet.SurfaceRadius, 0, 0); }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                OrbitGravityMin = 0.01,
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
                NodeExpansionSampleCount = 4,
                NodeExpansionReachPct = 0.25,
            };
        }

        private static WorldModel World(bool allowsSurface)
        {
            var w = new WorldModel(new SeededRandom(4242)) { Tuning = Tuning() };
            w.Planets = new OneBodyCatalogue(allowsSurface);
            return w;
        }

        // ---- the physical classifier stays physical -------------------------------------------

        [Fact]
        public void ClassifyZone_StillReportsSurface_OnAnUnlandableBody()
        {
            // The point really is inside the body's gravity. ClassifyZone is a statement about
            // gravity that other systems rely on; the landability rule must not corrupt it.
            Assert.Equal(NodeZone.Surface, World(allowsSurface: false).ClassifyZone(new Vec3(2, 0, 0)));
        }

        [Fact]
        public void PlaceableZoneAt_DemotesSurfaceToOrbit_OnAnUnlandableBody()
        {
            Assert.Equal(NodeZone.Orbit, World(allowsSurface: false).PlaceableZoneAt(new Vec3(2, 0, 0)));
        }

        [Fact]
        public void PlaceableZoneAt_LeavesSurfaceAlone_OnALandableBody()
        {
            Assert.Equal(NodeZone.Surface, World(allowsSurface: true).PlaceableZoneAt(new Vec3(2, 0, 0)));
        }

        [Fact]
        public void PlaceableZoneAt_LeavesDeepSpaceAlone()
        {
            Assert.Equal(NodeZone.DeepSpace, World(allowsSurface: false).PlaceableZoneAt(new Vec3(5000, 0, 0)));
        }

        // ---- an absent flag must not change behaviour -------------------------------------------

        [Fact]
        public void PlanetRecord_DefaultsToPermissive_SoAnUnawareCatalogueIsUnchanged()
        {
            // Every pre-existing fake catalogue constructs PlanetRecord without these fields.
            var bare = new PlanetRecord();
            Assert.True(bare.AllowsSurfaceStations);
            Assert.True(bare.AllowsOrbitalStations);
        }

        // ---- the two placement sites -----------------------------------------------------------

        [Fact]
        public void ExpandFrom_NeverPlacesANodeOnAnUnlandableSurface()
        {
            WorldModel w = World(allowsSurface: false);
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 40), 0.5, "N001");
            home.OwnerFactionTag = "F";

            // Expand repeatedly around the forbidden body; not one result may land as Surface. An
            // expansion that finds no legal position places nothing and so has no zone to judge.
            int placed = 0;
            for (int i = 0; i < 40; i++)
            {
                SpatialNode n = w.ExpandFrom("F", "N001");
                if (n == null) continue;
                placed++;
                Assert.NotEqual(NodeZone.Surface, n.Zone);
            }
            Assert.True(placed > 0, "every expansion was refused, so nothing was judged");
        }

        [Fact]
        public void ExpandFrom_StillPlacesSurfaceNodes_WhenTheBodyAllowsThem()
        {
            // Negative control for the test above: same geometry, same seed, flag flipped. Without
            // this, "no Surface nodes" would also pass if Expand simply never produced one here.
            WorldModel w = World(allowsSurface: true);
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 40), 0.5, "N001");
            home.OwnerFactionTag = "F";

            bool anySurface = false;
            for (int i = 0; i < 40; i++)
            {
                SpatialNode n = w.ExpandFrom("F", "N001");
                if (n != null && n.Zone == NodeZone.Surface) anySurface = true;
            }

            Assert.True(anySurface, "expected at least one Surface node when the body allows them");
        }

        [Fact]
        public void ReclassifyNodes_DoesNotRepositionANodeOntoAnUnlandableSurface()
        {
            // ReclassifyNodes runs on EVERY boot and re-snaps Surface nodes onto terrain. Without the
            // same rule here, a node placed correctly is dragged onto the forbidden core at restart.
            WorldModel w = World(allowsSurface: false);
            SpatialNode n = w.Graph.Add(new Vec3(2, 0, 0), 0.5, "N001");
            n.OwnerFactionTag = "F";
            n.Zone = NodeZone.DeepSpace;   // stale, as it is on every boot before the repair pass

            w.ReclassifyNodes(true);

            Assert.Equal(NodeZone.Orbit, n.Zone);
            Assert.NotEqual(new Vec3(9.5, 0, 0), n.Position);   // the surface point the snap would use
        }

        [Fact]
        public void ReclassifyNodes_StillRepositionsOntoALandableSurface()
        {
            WorldModel w = World(allowsSurface: true);
            SpatialNode n = w.Graph.Add(new Vec3(2, 0, 0), 0.5, "N001");
            n.OwnerFactionTag = "F";
            n.Zone = NodeZone.DeepSpace;

            w.ReclassifyNodes(true);

            Assert.Equal(NodeZone.Surface, n.Zone);
            Assert.Equal(9.5, n.Position.X, 3);
        }
    }
}
