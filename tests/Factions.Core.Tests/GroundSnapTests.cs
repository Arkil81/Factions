using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A surface placement that cannot find the ground is refused, not committed where it stands
    /// (FAC-536). Production METC N535 was committed 4.8 km above Tellus because the host's ground
    /// lookup answered only inside the planet's voxel cube (32.77 km per axis from the centre) and
    /// returned its input unchanged outside it, so the "snap" left the node in mid-air and the
    /// separation check ran there.
    /// </summary>
    public class GroundSnapTests
    {
        private const double SurfaceKm = 30.0;
        private const double MaximumKm = 31.8;
        private const double GravityKm = 48.79;
        private const double CubeHalfKm = 32.768;

        private static PlanetRecord Tellus()
        {
            return new PlanetRecord
            {
                Id = "tellus", Name = "Planet", Generator = "Tellus", Center = new Vec3(0, 0, 0),
                SurfaceRadius = SurfaceKm, MaximumRadius = MaximumKm, GravityRadius = GravityKm,
                AtmosphereRadius = 34.86, HasAtmosphere = true,
            };
        }

        /// <summary>A Tellus-sized body whose ground lookup behaves like the pre-fix host when
        /// <paramref name="cubeOnly"/>: it projects onto the surface only inside the voxel cube and
        /// returns its input unchanged outside it.</summary>
        private sealed class Body : IPlanetCatalogue
        {
            private readonly PlanetRecord _p = Tellus();
            private readonly bool _cubeOnly;
            public Body(bool cubeOnly) { _cubeOnly = cubeOnly; }
            public IEnumerable<PlanetRecord> Planets { get { yield return _p; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_p.Center);
                if (d >= GravityKm) return 0;
                if (d <= SurfaceKm) return 1.0;
                return (GravityKm - d) / (GravityKm - SurfaceKm);
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                bool inCube = Math.Abs(posKm.X) <= CubeHalfKm && Math.Abs(posKm.Y) <= CubeHalfKm
                              && Math.Abs(posKm.Z) <= CubeHalfKm;
                if (_cubeOnly && !inCube) return posKm;
                double d = posKm.DistanceTo(_p.Center);
                if (d < 1e-6) return new Vec3(SurfaceKm, 0, 0);
                double k = SurfaceKm / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static WorldModel World(bool cubeOnly, int seed = 1)
        {
            return new WorldModel(new SeededRandom(seed))
            {
                Tuning = new FactionTuning
                {
                    OrbitGravityMin = 0.05,
                    NodeSeparationSurfaceKm = 15.0,
                    NodeSeparationOrbitKm = 30.0,
                    NodeSeparationDeepSpaceKm = 500.0,
                },
                Planets = new Body(cubeOnly),
            };
        }

        // Where production N535 stands relative to Tellus's centre: 34.83 km out, 34.33 km along X.
        private static readonly Vec3 N535 = new Vec3(-34.325, -5.903, 0.083);

        [Fact]
        public void TheN535Point_IsSurfaceAndOutsideTheCube()
        {
            WorldModel w = World(cubeOnly: true);
            Assert.Equal(NodeZone.Surface, w.PlaceableZoneAt(N535));
            Assert.True(Math.Abs(N535.X) > CubeHalfKm);
        }

        [Fact]
        public void PlaceNode_ReportsASnapThatLeftThePointAboveTheTerrain()
        {
            WorldModel w = World(cubeOnly: true);
            bool midAir;
            Vec3 placed = w.PlaceNode(N535, NodeZone.Surface, out midAir);

            Assert.True(midAir);
            Assert.Equal(34.83, placed.DistanceTo(new Vec3(0, 0, 0)), 1);
        }

        [Fact]
        public void PlaceNode_DoesNotReportAGroundedSnap()
        {
            WorldModel w = World(cubeOnly: false);
            bool midAir;
            Vec3 placed = w.PlaceNode(N535, NodeZone.Surface, out midAir);

            Assert.False(midAir);
            Assert.Equal(SurfaceKm, placed.DistanceTo(new Vec3(0, 0, 0)), 6);
        }

        [Fact]
        public void PlaceNode_DoesNotReportWhenTheHostDoesNotKnowTheTerrainCeiling()
        {
            WorldModel w = World(cubeOnly: true);
            foreach (PlanetRecord p in w.Planets.Planets) p.MaximumRadius = 0;
            bool midAir;
            w.PlaceNode(N535, NodeZone.Surface, out midAir);

            Assert.False(midAir);
        }

        [Fact]
        public void ProbeAndSeed_RefuseAPointWithNoGroundUnderIt()
        {
            WorldModel w = World(cubeOnly: true);

            NodePlacementProbe probe = w.ProbePlacement(N535);
            Assert.True(probe.NoGround);
            Assert.False(probe.Clear);

            Assert.Null(w.SeedNodeAt("METC", N535));
            Assert.Empty(w.Graph.Nodes);
        }

        [Fact]
        public void ProbeAndSeed_GroundTheSamePoint_WhenTheLookupReachesTheWholeWell()
        {
            WorldModel w = World(cubeOnly: false);

            NodePlacementProbe probe = w.ProbePlacement(N535);
            Assert.False(probe.NoGround);
            Assert.True(probe.Clear);

            SpatialNode n = w.SeedNodeAt("METC", N535);
            Assert.NotNull(n);
            Assert.Equal(SurfaceKm, n.Position.DistanceTo(new Vec3(0, 0, 0)), 6);
        }

        private static Vec3 OnSurface(Vec3 p, double radiusKm)
        {
            double r = p.DistanceTo(new Vec3(0, 0, 0));
            return new Vec3(p.X * radiusKm / r, p.Y * radiusKm / r, p.Z * radiusKm / r);
        }

        /// <summary>A ground node 13.5 km along the surface from where <paramref name="above"/> grounds:
        /// the production distance from N535's ground point to UVCO N103.</summary>
        private static Vec3 NeighbourOfGroundPoint(Vec3 above)
        {
            Vec3 g = OnSurface(above, SurfaceKm);
            // Tangent at g: g x Z, normalised.
            double tx = g.Y, ty = -g.X, tz = 0;
            double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            return OnSurface(new Vec3(g.X + tx / tl * 14.5, g.Y + ty / tl * 14.5, g.Z), SurfaceKm);
        }

        [Fact]
        public void Reclassify_GroundsAHoveringNode_EvenInsideTheSeparationFloor()
        {
            WorldModel w = World(cubeOnly: false);
            SpatialNode hovering = w.Graph.Add(N535, 1.0, "N535");
            hovering.OwnerFactionTag = "METC";
            SpatialNode neighbour = w.Graph.Add(NeighbourOfGroundPoint(N535), 1.0, "N103");
            neighbour.OwnerFactionTag = "UVCO";
            Assert.InRange(neighbour.Position.DistanceTo(OnSurface(N535, SurfaceKm)), 13.0, 14.0);

            var log = new List<string>();
            NodeReclassifyResult r = w.ReclassifyNodes(true, log);

            Assert.Equal(SurfaceKm, hovering.Position.DistanceTo(new Vec3(0, 0, 0)), 6);
            Assert.Equal(1, r.Repositioned);
            Assert.Equal(0, r.SeparationBlocked);
            Assert.True(r.StandingViolations >= 1);
            Assert.Contains(log, l => l.StartsWith("NODE_GROUNDED_INSIDE_SEPARATION node=N535 neighbor=N103 actualKm=13."));
        }

        [Fact]
        public void Reclassify_StillRefusesToCrowd_ANodeThatIsNotInMidAir()
        {
            // 1.5 km above the 30 km datum is below the 31.8 km terrain ceiling: off its ground point,
            // not hovering. The crowding guard keeps it where it is, as before.
            WorldModel w = World(cubeOnly: false);
            Vec3 low = OnSurface(N535, SurfaceKm + 1.5);
            SpatialNode n = w.Graph.Add(low, 1.0, "N900");
            n.OwnerFactionTag = "METC";
            w.Graph.Add(NeighbourOfGroundPoint(low), 1.0, "N901").OwnerFactionTag = "UVCO";

            var log = new List<string>();
            NodeReclassifyResult r = w.ReclassifyNodes(true, log);

            Assert.Equal(SurfaceKm + 1.5, n.Position.DistanceTo(new Vec3(0, 0, 0)), 6);
            Assert.Equal(0, r.Repositioned);
            Assert.Equal(1, r.SeparationBlocked);
            Assert.Contains(log, l => l.StartsWith("NODE_REPOSITION_BLOCKED node=N900"));
        }

        [Fact]
        public void ExpandFrom_NeverCommitsANodeAboveTheTerrain()
        {
            // N094's production offset from Tellus's centre: a ground node 30.77 km out, one of
            // whose expansions became N535.
            int committed = 0;
            for (int seed = 1; seed <= 40; seed++)
            {
                WorldModel w = World(cubeOnly: true, seed: seed);
                // The whole well is Surface, so every candidate in it is a surface snap and none can
                // escape the refusal by classifying Orbit.
                SpatialNode origin = w.Graph.Add(new Vec3(-24.9, 18.0, 0.5), 1.0, "N094");
                origin.OwnerFactionTag = "METC";

                SpatialNode child = w.ExpandFrom("METC", "N094");
                if (child == null || child.Zone != NodeZone.Surface) continue;
                committed++;
                Assert.True(PlanetGeometry.HeightAboveTerrainKm(w.Planets, child.Position) <= StationSiting.GroundToleranceKm,
                    "seed " + seed + ": committed " + child.Id + " at r="
                    + child.Position.DistanceTo(new Vec3(0, 0, 0)).ToString("0.00") + " km");
            }
            // Not vacuous: some expansions still land, on the ground inside the cube.
            Assert.True(committed > 0);
        }
    }
}
