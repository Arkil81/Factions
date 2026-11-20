using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-096 stage 2: what kind of station belongs somewhere is a geometric question,
    /// asked of the world rather than read off a node's cached <see cref="NodeZone"/> label.</summary>
    public class StationSitingTests
    {
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        /// <summary>The gas giant from the live measurement: a 9.5 km voxel core inside a 191.3 km
        /// atmosphere, gravity limit 304.89 km, and gravity that peaks well above the surface rather
        /// than at it — the profile that makes relative-gravity classification call a 195 km ring
        /// "Surface".</summary>
        private sealed class GasGiantCatalogue : IPlanetCatalogue
        {
            internal const double SurfaceR = 9.5;
            internal const double GravityR = 304.89;

            private static readonly double[] _r = { 0, 9.5, 39.04, 157.19, 186.73, 216.27, 245.81, 275.35, 304.89 };
            private static readonly double[] _g = { 0, 1.0556, 2.0, 2.0, 1.5467, 0.5533, 0.2258, 0.1020, 0.0 };

            internal readonly PlanetRecord Record = new PlanetRecord
            {
                Id = "Cauldron", Name = "Cauldron", Generator = "GasGiantType_Cauldron",
                Center = new Vec3(0, 0, 0),
                SurfaceRadius = SurfaceR, GravityRadius = GravityR,
                AtmosphereRadius = 191.3, HasAtmosphere = true,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return Record; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(Record.Center);
                if (d >= GravityR) return 0;
                for (int i = 1; i < _r.Length; i++)
                {
                    if (d > _r[i]) continue;
                    double t = (d - _r[i - 1]) / (_r[i] - _r[i - 1]);
                    return _g[i - 1] + t * (_g[i] - _g[i - 1]);
                }
                return 0;
            }

            internal int SurfaceLookups;

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                SurfaceLookups++;
                double d = posKm.DistanceTo(Record.Center);
                if (d < 1e-6) return new Vec3(SurfaceR, 0, 0);
                double k = SurfaceR / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>An ordinary planet: 30 km terrain, gravity limit 48.79 km, gravity peaking at the
        /// surface as relative classification assumes.</summary>
        private sealed class OrdinaryPlanetCatalogue : IPlanetCatalogue
        {
            internal const double SurfaceR = 30.0;
            internal const double GravityR = 48.79;

            internal readonly PlanetRecord Record = new PlanetRecord
            {
                Id = "Tellus", Name = "Tellus", Generator = "EarthLike",
                Center = new Vec3(0, 0, 0),
                SurfaceRadius = SurfaceR, GravityRadius = GravityR,
                AtmosphereRadius = 34.86, HasAtmosphere = true,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return Record; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(Record.Center);
                if (d >= GravityR) return 0;
                if (d <= SurfaceR) return d / SurfaceR;
                return (GravityR - d) / (GravityR - SurfaceR);
            }

            internal int SurfaceLookups;

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                SurfaceLookups++;
                double d = posKm.DistanceTo(Record.Center);
                if (d < 1e-6) return new Vec3(SurfaceR, 0, 0);
                double k = SurfaceR / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        // ---- the terrain lookup is a real host cost, and deep space must not pay it -------------

        [Fact]
        public void ADeepSpacePosition_CostsNoTerrainLookup()
        {
            // NearestSurfacePoint is MyPlanet.GetClosestSurfacePointGlobal on the live host and can
            // touch voxel data. Classify runs for every node on every boot, and this world is mostly
            // deep space, so paying it there would be thousands of pointless terrain reads per boot.
            var planets = new GasGiantCatalogue();

            Assert.Equal(StationSite.Space, StationSiting.Classify(planets, new Vec3(5000, 0, 0)));
            Assert.Equal(0, planets.SurfaceLookups);
        }

        [Fact]
        public void APositionInsideTheWell_StillPaysIt_BecauseTerrainDecidesThere()
        {
            var planets = new GasGiantCatalogue();

            StationSiting.Classify(planets, new Vec3(200, 0, 0));

            Assert.Equal(1, planets.SurfaceLookups);
        }

        [Fact]
        public void TheGuardDoesNotChangeAnyAnswer_AcrossTheWholeRadialRange()
        {
            // Pinned against the pre-guard rule computed inline: the guard may only remove work.
            var planets = new GasGiantCatalogue();
            foreach (double r in new double[] { 4, 9.5, 12, 100, 195, 250, 304, 304.89, 320, 1000, 5000 })
            {
                var pos = new Vec3(r, 0, 0);
                StationSite expected = Unguarded(planets, pos);
                Assert.Equal(expected, StationSiting.Classify(planets, pos));
            }
        }

        /// <summary>The rule as it stood before the cost guard, recomputed here so the guard is
        /// measured against it rather than against its own output.</summary>
        private static StationSite Unguarded(IPlanetCatalogue planets, Vec3 posKm)
        {
            PlanetRecord nearest = PlanetGeometry.NearestByCentre(planets, posKm);
            if (nearest == null) return StationSite.Space;
            if (nearest.AllowsSurfaceStations)
            {
                if (nearest.SurfaceRadius > 0 && posKm.DistanceTo(nearest.Center) < nearest.SurfaceRadius)
                    return StationSite.Surface;
                Vec3 ground = planets.NearestSurfacePoint(posKm);
                if (ground.DistanceSquaredTo(posKm) <= StationSiting.GroundToleranceKm * StationSiting.GroundToleranceKm)
                    return StationSite.Surface;
            }
            return planets.GravityAt(posKm) > 0 ? StationSite.Orbital : StationSite.Space;
        }

        // ---- the defect stage 2 closes ---------------------------------------------------------

        [Fact]
        public void ARingNodeOnAGasGiant_IsNotASurfaceStation()
        {
            // The whole point: relative gravity calls 195 km "Surface" on this body, so reading the
            // label founded a landed Outpost floating in a debris disc.
            var planets = new GasGiantCatalogue();
            var world = new WorldModel { Planets = planets, Tuning = ZoneTuning() };

            Assert.Equal(NodeZone.Surface, world.ClassifyZone(new Vec3(195, 0, 0)));   // the label, still wrong
            Assert.Equal(StationSite.Orbital, StationSiting.Classify(planets, new Vec3(195, 0, 0)));
        }

        [Fact]
        public void EveryAuthoredRingBand_ReadsAsOrbital_NotSurface()
        {
            var planets = new GasGiantCatalogue();
            double[] bandCentres = { 201.75, 227.4, 259.0, 299.9 };   // live bands 1-4, mid-band
            foreach (double r in bandCentres)
                Assert.Equal(StationSite.Orbital, StationSiting.Classify(planets, new Vec3(r, 0, 0)));
        }

        [Fact]
        public void ANodeOnTheGasGiantsOwnCore_IsStillASurfaceStation()
        {
            // The ring gives up the hole in its middle, so the body itself still founds outposts.
            var planets = new GasGiantCatalogue();
            Assert.Equal(StationSite.Surface, StationSiting.Classify(planets, new Vec3(GasGiantCatalogue.SurfaceR, 0, 0)));
            Assert.Equal(StationSite.Surface, StationSiting.Classify(planets, new Vec3(4, 0, 0)));   // buried
        }

        // ---- ordinary planets keep the station type they have today ----------------------------

        [Fact]
        public void OnAnOrdinaryPlanet_TheGroundIsStillSurface()
        {
            var planets = new OrdinaryPlanetCatalogue();
            Assert.Equal(StationSite.Surface, StationSiting.Classify(planets, new Vec3(30, 0, 0)));
            Assert.Equal(StationSite.Surface, StationSiting.Classify(planets, new Vec3(30.9, 0, 0)));  // within tolerance
        }

        [Fact]
        public void JustAboveTheGround_IsOrbital_NotSurface()
        {
            var planets = new OrdinaryPlanetCatalogue();
            Assert.Equal(StationSite.Orbital, StationSiting.Classify(planets, new Vec3(32, 0, 0)));
        }

        [Fact]
        public void ANodeSnappedToTheOrbitBand_KeepsTheOrbitalTypeItHasToday()
        {
            // The orbit snap lands on the gravity limit. Orbital-vs-Space is still exactly
            // "GravityAt > 0", so that node must not silently become a SpaceStation.
            var planets = new OrdinaryPlanetCatalogue();
            var world = new WorldModel { Planets = planets, Tuning = ZoneTuning() };
            SpatialNode n = world.Graph.Add(new Vec3(40, 0, 0), 0.5, "N1");
            world.ReclassifyNodes(true);

            Assert.Equal(NodeZone.Orbit, n.Zone);
            Assert.Equal(StationSite.Orbital, StationSiting.Classify(planets, n.Position));
        }

        [Fact]
        public void OutsideEveryWell_IsSpace()
        {
            var planets = new OrdinaryPlanetCatalogue();
            Assert.Equal(StationSite.Space, StationSiting.Classify(planets, new Vec3(5000, 0, 0)));
        }

        [Fact]
        public void WithNoPlanetsAtAll_EverythingIsSpace()
        {
            Assert.Equal(StationSite.Space, StationSiting.Classify(NullPlanetCatalogue.Instance, new Vec3(1, 2, 3)));
            Assert.Equal(StationSite.Space, StationSiting.Classify(null, new Vec3(1, 2, 3)));
        }

        // ---- an unlandable body never yields a surface station ---------------------------------

        [Fact]
        public void AnUnlandableBody_NeverYieldsASurfaceStation_EvenOnItsTerrain()
        {
            // Same answer SurfaceNodePolicy gives placement, asked of the station type directly
            // rather than inherited through a demoted NodeZone label.
            var planets = new OrdinaryPlanetCatalogue();
            planets.Record.AllowsSurfaceStations = false;

            Assert.Equal(StationSite.Orbital, StationSiting.Classify(planets, new Vec3(30, 0, 0)));
            Assert.Equal(StationSite.Orbital, StationSiting.Classify(planets, new Vec3(10, 0, 0)));  // buried
        }

        [Fact]
        public void AnUnlandableBody_StillYieldsSpaceOutsideItsWell()
        {
            var planets = new OrdinaryPlanetCatalogue();
            planets.Record.AllowsSurfaceStations = false;

            Assert.Equal(StationSite.Space, StationSiting.Classify(planets, new Vec3(5000, 0, 0)));
        }

        // ---- the derived Site field, and its default ------------------------------------------

        [Fact]
        public void AnUnsetSite_ReadsAsSpace_SoItGrantsNothing()
        {
            // Site is derived and not persisted, so "never set" is a real state: a node loaded before
            // its first ReclassifyNodes, or a fixture that forgot the field. Every consumer asks "is
            // this on the ground", so the default must be the answer that grants nothing.
            var fresh = new SpatialNode();

            Assert.Equal(StationSite.Space, fresh.Site);
            Assert.Equal(default(StationSite), StationSite.Space);
        }

        [Fact]
        public void ReclassifyNodes_DerivesSiteBesideZoneAndPlanetId()
        {
            // Unlandable, as the live world authors this body -- otherwise the Surface snap drags the
            // 150 km node onto the 9.5 km core before Site is read, which is FAC-096's own defect.
            var planets = new GasGiantCatalogue();
            planets.Record.AllowsSurfaceStations = false;
            var w = new WorldModel(new SeededRandom(1)) { Tuning = ZoneTuning(), Planets = planets };

            SpatialNode ground = w.Graph.Add(new Vec3(4, 0, 0), 0.5, "G");     // inside the body
            SpatialNode inAir  = w.Graph.Add(new Vec3(150, 0, 0), 0.5, "A");
            SpatialNode far    = w.Graph.Add(new Vec3(5000, 0, 0), 0.5, "F");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            // An unlandable body yields no Surface anywhere, so both near nodes read Orbital.
            Assert.Equal(StationSite.Orbital, ground.Site);
            Assert.Equal(StationSite.Orbital, inAir.Site);   // deep atmosphere is not ground
            Assert.Equal(StationSite.Space,   far.Site);
            Assert.Equal(2, r.SiteChanged);                  // the two that moved off the Space default
        }

        [Fact]
        public void SiteAndZoneDisagreeExactlyWhereTheAtmosphereIsDeep()
        {
            // The whole reason Site exists: relative gravity calls 150 km "Surface" on this body.
            var planets = new GasGiantCatalogue();
            var w = new WorldModel(new SeededRandom(1)) { Tuning = ZoneTuning(), Planets = planets };
            var inAir = new Vec3(150, 0, 0);

            Assert.Equal(NodeZone.Surface,    w.ClassifyZone(inAir));
            Assert.Equal(StationSite.Orbital, StationSiting.Classify(planets, inAir));
        }

        [Fact]
        public void ReclassifyNodes_IsIdempotentForSite()
        {
            var planets = new GasGiantCatalogue();
            planets.Record.AllowsSurfaceStations = false;
            var w = new WorldModel(new SeededRandom(1)) { Tuning = ZoneTuning(), Planets = planets };
            w.Graph.Add(new Vec3(150, 0, 0), 0.5, "A");

            Assert.Equal(1, w.ReclassifyNodes(true).SiteChanged);
            Assert.Equal(0, w.ReclassifyNodes(true).SiteChanged);
        }

        private static FactionTuning ZoneTuning()
        {
            return new FactionTuning
            {
                SurfaceGravityThreshold = 0.5,
                OrbitGravityMin = 0.05,
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
            };
        }
    }
}
