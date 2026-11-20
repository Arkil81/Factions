using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The region-keyed view a colonisation decision decides against. FAC-106.</summary>
    public class RegionMaterialIndexTests
    {
        private sealed class Ore : IPlanetOreLookup
        {
            public readonly Dictionary<string, Dictionary<string, double>> ByPlanet =
                new Dictionary<string, Dictionary<string, double>>();

            public IEnumerable<KeyValuePair<string, double>> SurfaceOreShares(string planetId)
            {
                Dictionary<string, double> d;
                return ByPlanet.TryGetValue(planetId, out d)
                    ? (IEnumerable<KeyValuePair<string, double>>)d
                    : new Dictionary<string, double>();
            }
        }

        private static PlanetRecord Planet(string id, string generator, Vec3 centre, double radiusKm,
                                           double sepKm, bool landable = true)
        {
            return new PlanetRecord
            {
                Id = id, Name = "Planet", Generator = generator, Center = centre,
                SurfaceRadius = radiusKm, GravityRadius = radiusKm * 4,
                AllowsSurfaceStations = landable, NodeSeparationKm = sepKm,
            };
        }

        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                NodeSeparationSurfaceKm = 15, NodeSeparationOrbitKm = 30, NodeSeparationDeepSpaceKm = 500
            };
        }

        private static RegionMaterialIndex Build(IEnumerable<PlanetRecord> planets, Ore ore,
                                                 IEnumerable<SpatialNode> nodes)
        {
            return RegionMaterialIndex.Build(
                new StaticPlanetCatalogue(planets), new List<KeyValuePair<WorldFeature, Vec3>>(),
                nodes, ore, Tuning(), new Vec3(0, 0, 0));
        }


        // ---- a planet's ore belongs to its terrain ----------------------------------------------

        [Fact]
        public void OnlyTheSurfaceYieldsABodysOre_OrbitIsSomewhereToStand()
        {
            var ore = new Ore();
            ore.ByPlanet["agni"] = new Dictionary<string, double> { { "Platinum", 0.9 } };
            RegionMaterialIndex index = Build(
                new[] { Planet("agni", "Agni", new Vec3(500, 0, 0), 15, 10) }, ore, new List<SpatialNode>());

            var yielding = new List<ColonisationCandidate>(index.RegionsYielding("Platinum"));
            Assert.Single(yielding);
            Assert.Equal(PlacementRegionKind.PlanetSurface, yielding[0].Region.Kind);
            Assert.Equal("Agni", yielding[0].Region.Key);

            // The orbit exists, and is reachable as a foothold -- it just yields nothing.
            var near = new List<ColonisationCandidate>(index.RegionsNear(yielding[0].Region));
            Assert.Contains(near, c => c.Region.Kind == PlacementRegionKind.PlanetOrbit && c.Region.Key == "Agni");
        }

        [Fact]
        public void ABodysOwnOrbitIsTheNearestDiversionFromItsSurface()
        {
            var ore = new Ore();
            ore.ByPlanet["agni"] = new Dictionary<string, double> { { "Platinum", 0.9 } };
            RegionMaterialIndex index = Build(new[]
            {
                Planet("agni", "Agni", new Vec3(500, 0, 0), 15, 10),
                Planet("kor",  "Kor",  new Vec3(4000, 0, 0), 9.5, 10),
            }, ore, new List<SpatialNode>());

            var near = new List<ColonisationCandidate>(index.RegionsNear(
                new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "Agni" }));

            Assert.Equal(PlacementRegionKind.PlanetOrbit, near[0].Region.Kind);
            Assert.Equal("Agni", near[0].Region.Key);   // nearest first, and its own orbit is at zero
        }

        [Fact]
        public void AnUnlandableBodyStillOffersItsOreSoTheDecisionDivertsRatherThanIgnoringIt()
        {
            var ore = new Ore();
            ore.ByPlanet["gg"] = new Dictionary<string, double> { { "Platinum", 0.9 } };
            RegionMaterialIndex index = Build(
                new[] { Planet("gg", "RealGasGiant", new Vec3(100, 0, 0), 9.5, 10, landable: false) },
                ore, new List<SpatialNode>());

            var yielding = new List<ColonisationCandidate>(index.RegionsYielding("Platinum"));
            Assert.Single(yielding);
            Assert.Equal(0, yielding[0].Remaining());   // reads as full, not as absent
        }

        // ---- occupancy ---------------------------------------------------------------------------

        [Fact]
        public void OccupancyCountsEveryOwner()
        {
            var ore = new Ore();
            ore.ByPlanet["agni"] = new Dictionary<string, double> { { "Platinum", 0.9 } };
            var nodes = new List<SpatialNode>
            {
                new SpatialNode { Id = "N1", PlanetId = "agni", Zone = NodeZone.Surface, OwnerFactionTag = "SPRT" },
                new SpatialNode { Id = "N2", PlanetId = "agni", Zone = NodeZone.Surface, OwnerFactionTag = "MTDW" },
                new SpatialNode { Id = "N3", PlanetId = "agni", Zone = NodeZone.Orbit,   OwnerFactionTag = "MTDW" },
            };
            RegionMaterialIndex index = Build(
                new[] { Planet("agni", "Agni", new Vec3(500, 0, 0), 15, 10) }, ore, nodes);

            var yielding = new List<ColonisationCandidate>(index.RegionsYielding("Platinum"));
            Assert.Equal(2, yielding[0].EffectiveOccupied());   // both surface nodes; the orbital one is elsewhere
        }

        [Fact]
        public void SiblingsForDifferentMaterialsShareOneOccupancyCount()
        {
            // A region yielding two materials produces two candidates. They describe one region and
            // must not each count the same nodes.
            var ore = new Ore();
            ore.ByPlanet["agni"] = new Dictionary<string, double> { { "Platinum", 0.6 }, { "Silicon", 0.4 } };
            var nodes = new List<SpatialNode>
            {
                new SpatialNode { Id = "N1", PlanetId = "agni", Zone = NodeZone.Surface, OwnerFactionTag = "SPRT" },
            };
            RegionMaterialIndex index = Build(
                new[] { Planet("agni", "Agni", new Vec3(500, 0, 0), 15, 10) }, ore, nodes);

            var plat = new List<ColonisationCandidate>(index.RegionsYielding("Platinum"));
            var sili = new List<ColonisationCandidate>(index.RegionsYielding("Silicon"));

            Assert.Equal(1, plat[0].EffectiveOccupied());
            Assert.Equal(1, sili[0].EffectiveOccupied());
            Assert.Equal(0.6, plat[0].Share, 6);
            Assert.Equal(0.4, sili[0].Share, 6);
        }

        [Fact]
        public void TheAuthoredSeparationDecidesCapacityNotTheZoneFloor()
        {
            var ore = new Ore();
            ore.ByPlanet["kor"] = new Dictionary<string, double> { { "Uranium", 1.0 } };

            // Kor at the authored 10 km holds ~11; at the 15 km zone floor it holds ~4.
            RegionMaterialIndex authored = Build(
                new[] { Planet("kor", "Kor", new Vec3(0, 0, 0), 9.5, 10) }, ore, new List<SpatialNode>());
            RegionMaterialIndex floored = Build(
                new[] { Planet("kor", "Kor", new Vec3(0, 0, 0), 9.5, 15) }, ore, new List<SpatialNode>());

            double at10 = new List<ColonisationCandidate>(authored.RegionsYielding("Uranium"))[0].Remaining();
            double at15 = new List<ColonisationCandidate>(floored.RegionsYielding("Uranium"))[0].Remaining();

            Assert.True(at10 > at15, "10 km must fit more than 15 km: " + at10 + " vs " + at15);

            // Derived from PlacementCapacity, never hardcoded. An earlier version of this test
            // asserted "about eleven", copied from FAC-105's prose -- which predates OnSphere being
            // rewritten to measure what RegionSampler's lattice actually places (8) instead of
            // estimating exclusion-cap area (11.1). A number written in a ticket is not a fact about
            // the code, and this assertion now cannot go stale that way.
            Assert.Equal(PlacementCapacity.OnSphere(9.5, 10), at10, 6);
            Assert.Equal(PlacementCapacity.OnSphere(9.5, 15), at15, 6);
        }

        // ---- features, from the world this server runs -------------------------------------------

        [Fact]
        public void TheShippedRingIsIndexedAsAPlatinumSource()
        {
            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_features.json")), out status);
            WorldFeature ring = features.Find(f => f.Name == "Cauldron Ring");
            Assert.NotNull(ring);

            var withCentres = new List<KeyValuePair<WorldFeature, Vec3>>
            {
                new KeyValuePair<WorldFeature, Vec3>(ring, ring.Center)
            };
            RegionMaterialIndex index = RegionMaterialIndex.Build(
                new StaticPlanetCatalogue(new List<PlanetRecord>()), withCentres,
                new List<SpatialNode>(), null, Tuning(), new Vec3(0, 0, 0));

            // Authored: Platinum on the inner band, Uranium and Gold/Silver on the outer hairlines.
            foreach (string ore in new[] { "Platinum", "Uranium", "Gold", "Silver", "Ice" })
            {
                var hits = new List<ColonisationCandidate>(index.RegionsYielding(ore));
                Assert.True(hits.Count == 1, ore + " should be indexed to the ring exactly once, was " + hits.Count);
                Assert.Equal("Cauldron Ring", hits[0].Region.Key);
            }

            // 30 km authored on the ring, not the 500 km deep-space floor.
            Assert.Equal(30.0, new List<ColonisationCandidate>(index.RegionsYielding("Platinum"))[0].SeparationKm, 6);
        }
    }
}
