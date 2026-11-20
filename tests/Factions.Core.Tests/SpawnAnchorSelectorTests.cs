using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class SpawnAnchorSelectorTests
    {
        private static SpawnAnchorCandidate Station(
            string id, double xKm, StationSite site, string planetId = null,
            double xenophobia = 0.05, bool atWar = false)
        {
            return new SpawnAnchorCandidate
            {
                NodeId = id,
                DisplayName = id + " Outpost",
                PositionKm = new Vec3(xKm, 0, 0),
                Site = site,
                PlanetId = planetId,
                OwnerTag = "TRD",
                OwnerXenophobia = xenophobia,
                AtWarWithSpawner = atWar
            };
        }

        private static SpawnAnchorRequest OnPlanet(string planetId, double xKm = 0)
        {
            return new SpawnAnchorRequest
            {
                VanillaPositionKm = new Vec3(xKm, 0, 0),
                PlanetId = planetId,
                OnPlanet = true
            };
        }

        private static SpawnAnchorRequest InSpace(double xKm = 0)
        {
            return new SpawnAnchorRequest { VanillaPositionKm = new Vec3(xKm, 0, 0), OnPlanet = false };
        }

        [Fact]
        public void Placement_PrefersFactionMate_OverANearerStation()
        {
            var stations = new List<SpawnAnchorCandidate> { Station("N1", 1, StationSite.Space) };
            var mates = new List<Vec3> { new Vec3(500, 0, 0) };
            FactionTuning t = TestSupport.Tuning();

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(InSpace(), mates, stations, t);

            Assert.Equal(SpawnAnchorKind.FactionMate, choice.Kind);
            Assert.Equal(t.RespawnMateAnchorRadiusM, choice.RadiusM);
            Assert.Equal(500, choice.PositionKm.X);
        }

        [Fact]
        public void Placement_NearestMateWins_WhenSeveralAreOnline()
        {
            var mates = new List<Vec3> { new Vec3(900, 0, 0), new Vec3(30, 0, 0), new Vec3(200, 0, 0) };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                InSpace(), mates, null, TestSupport.Tuning());

            Assert.Equal(SpawnAnchorKind.FactionMate, choice.Kind);
            Assert.Equal(30, choice.PositionKm.X);
        }

        [Fact]
        public void Placement_OnAPlanet_NeverAnchorsToAnOrbitNode()
        {
            // The whole point of the split: a rover set down beside an orbital station is in vacuum.
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("ORB", 5, StationSite.Orbital, "PERTAM"),
                Station("SURF", 400, StationSite.Surface, "PERTAM")
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                OnPlanet("PERTAM"), null, stations, TestSupport.Tuning());

            Assert.Equal(SpawnAnchorKind.Station, choice.Kind);
            Assert.Equal("SURF", choice.NodeId);
        }

        [Fact]
        public void Placement_OnAPlanet_IgnoresSurfaceStationsOnAnotherPlanet()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("ELSE", 1, StationSite.Surface, "TRITON")
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                OnPlanet("PERTAM"), null, stations, TestSupport.Tuning());

            Assert.Equal(SpawnAnchorKind.None, choice.Kind);
        }

        [Fact]
        public void Placement_InSpace_NeverAnchorsToASurfaceStation()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("SURF", 2, StationSite.Surface, "PERTAM"),
                Station("DEEP", 600, StationSite.Space)
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                InSpace(), null, stations, TestSupport.Tuning());

            Assert.Equal("DEEP", choice.NodeId);
        }

        [Fact]
        public void Placement_SkipsXenophobicOwners_AndFallsBackToTheNextStation()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("RAID", 1, StationSite.Space, xenophobia: 0.90),
                Station("TRADE", 900, StationSite.Space, xenophobia: 0.05)
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                InSpace(), null, stations, TestSupport.Tuning());

            Assert.Equal("TRADE", choice.NodeId);
        }

        [Fact]
        public void Placement_SkipsAFactionAtWarWithTheSpawner()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("ENEMY", 1, StationSite.Space, atWar: true),
                Station("FRIEND", 900, StationSite.Space)
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                InSpace(), null, stations, TestSupport.Tuning());

            Assert.Equal("FRIEND", choice.NodeId);
        }

        [Fact]
        public void Placement_ReturnsNone_WhenNothingQualifies()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("RAID", 1, StationSite.Space, xenophobia: 0.90)
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                InSpace(), null, stations, TestSupport.Tuning());

            Assert.Equal(SpawnAnchorKind.None, choice.Kind);
            Assert.Equal(0.0, choice.RadiusM);
        }

        [Fact]
        public void Placement_UsesTheStationRadius_NotTheMateRadius()
        {
            var stations = new List<SpawnAnchorCandidate> { Station("N1", 10, StationSite.Space) };
            FactionTuning t = TestSupport.Tuning();

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(InSpace(), null, stations, t);

            Assert.Equal(t.RespawnStationAnchorRadiusM, choice.RadiusM);
            Assert.NotEqual(t.RespawnMateAnchorRadiusM, choice.RadiusM);
        }

        [Fact]
        public void Wayfinding_OnAPlanet_PrefersSurface_ThenThatPlanetsOrbit()
        {
            var orbitOnly = new List<SpawnAnchorCandidate>
            {
                Station("ORB", 40, StationSite.Orbital, "PERTAM"),
                Station("FAR", 5, StationSite.Space)
            };

            SpawnAnchorCandidate hit = SpawnAnchorSelector.SelectWayfindingTarget(
                OnPlanet("PERTAM"), new Vec3(0, 0, 0), orbitOnly, TestSupport.Tuning());

            // The deep-space node is nearer, but the planet's own orbit outranks it.
            Assert.Equal("ORB", hit.NodeId);

            orbitOnly.Add(Station("SURF", 300, StationSite.Surface, "PERTAM"));
            hit = SpawnAnchorSelector.SelectWayfindingTarget(
                OnPlanet("PERTAM"), new Vec3(0, 0, 0), orbitOnly, TestSupport.Tuning());

            Assert.Equal("SURF", hit.NodeId);
        }

        [Fact]
        public void Wayfinding_InSpace_PrefersDeepSpace_ThenOrbit_AndOnlyThenSurface()
        {
            var surfaceOnly = new List<SpawnAnchorCandidate>
            {
                Station("SURF", 1, StationSite.Surface, "PERTAM")
            };
            FactionTuning t = TestSupport.Tuning();

            // Last-resort tier: better to point somewhere than to leave an arrival with nothing.
            Assert.Equal("SURF",
                SpawnAnchorSelector.SelectWayfindingTarget(InSpace(), new Vec3(0, 0, 0), surfaceOnly, t).NodeId);

            surfaceOnly.Add(Station("ORB", 700, StationSite.Orbital, "PERTAM"));
            Assert.Equal("ORB",
                SpawnAnchorSelector.SelectWayfindingTarget(InSpace(), new Vec3(0, 0, 0), surfaceOnly, t).NodeId);

            surfaceOnly.Add(Station("DEEP", 900, StationSite.Space));
            Assert.Equal("DEEP",
                SpawnAnchorSelector.SelectWayfindingTarget(InSpace(), new Vec3(0, 0, 0), surfaceOnly, t).NodeId);
        }

        [Fact]
        public void Wayfinding_MeasuresFromWhereThePlayerLanded_NotFromVanillasAnswer()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("NEAR_VANILLA", 0, StationSite.Space),
                Station("NEAR_LANDED", 1000, StationSite.Space)
            };

            SpawnAnchorCandidate hit = SpawnAnchorSelector.SelectWayfindingTarget(
                InSpace(0), new Vec3(1000, 0, 0), stations, TestSupport.Tuning());

            Assert.Equal("NEAR_LANDED", hit.NodeId);
        }

        [Fact]
        public void Wayfinding_NeverNamesAXenophobicOrHostileStation()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("RAID", 1, StationSite.Space, xenophobia: 0.90),
                Station("ENEMY", 2, StationSite.Space, atWar: true)
            };

            Assert.Null(SpawnAnchorSelector.SelectWayfindingTarget(
                InSpace(), new Vec3(0, 0, 0), stations, TestSupport.Tuning()));
        }

        [Fact]
        public void NearestWelcoming_BreaksTiesOnNodeId_SoTheChoiceIsStable()
        {
            var a = new List<SpawnAnchorCandidate>
            {
                Station("B", 10, StationSite.Space),
                Station("A", 10, StationSite.Space)
            };
            var reversed = new List<SpawnAnchorCandidate> { a[1], a[0] };
            FactionTuning t = TestSupport.Tuning();

            Assert.Equal("A", SpawnAnchorSelector.SelectPlacement(InSpace(), null, a, t).NodeId);
            Assert.Equal("A", SpawnAnchorSelector.SelectPlacement(InSpace(), null, reversed, t).NodeId);
        }

        [Fact]
        public void IsWelcoming_TracksTheTunedXenophobiaCeiling()
        {
            FactionTuning t = TestSupport.Tuning();

            // Shipped personalities either side of the 0.5 default.
            Assert.True(SpawnAnchorSelector.IsWelcoming(Station("M", 0, StationSite.Space, xenophobia: 0.05), t));
            Assert.True(SpawnAnchorSelector.IsWelcoming(Station("S", 0, StationSite.Space, xenophobia: 0.30), t));
            Assert.False(SpawnAnchorSelector.IsWelcoming(Station("R", 0, StationSite.Space, xenophobia: 0.90), t));
            Assert.False(SpawnAnchorSelector.IsWelcoming(Station("F", 0, StationSite.Space, xenophobia: 1.00), t));
        }

        // ---- space-respawn regions (WorldFeature.SpaceRespawn) ------------------------------------

        private static SpawnAnchorCandidate InRegion(SpawnAnchorCandidate c)
        {
            c.SpaceRespawnAllowed = true;
            return c;
        }

        private static SpawnAnchorRequest Confined(double xKm = 0, double? fallbackXKm = null)
        {
            return new SpawnAnchorRequest
            {
                VanillaPositionKm = new Vec3(xKm, 0, 0),
                OnPlanet = false,
                SpaceRespawnRestricted = true,
                SpaceRespawnFallbackKm = fallbackXKm.HasValue ? new Vec3(fallbackXKm.Value, 0, 0) : (Vec3?)null
            };
        }

        [Fact]
        public void Placement_Confined_PassesOverANearerStationOutsideTheRegion()
        {
            // The live case: a welcoming UNKN post inside a Factorum nest was the nearest station.
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("NEST", 10, StationSite.Space, xenophobia: 0.30),
                InRegion(Station("RING", 900, StationSite.Orbital))
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                Confined(fallbackXKm: 500), null, stations, TestSupport.Tuning());

            Assert.Equal(SpawnAnchorKind.Station, choice.Kind);
            Assert.Equal("RING", choice.NodeId);
        }

        [Fact]
        public void Placement_Confined_WithNoStationInTheRegion_AnchorsToTheRegionItself()
        {
            var stations = new List<SpawnAnchorCandidate> { Station("NEST", 10, StationSite.Space) };
            FactionTuning t = TestSupport.Tuning();

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                Confined(fallbackXKm: 500), null, stations, t);

            Assert.Equal(SpawnAnchorKind.Region, choice.Kind);
            Assert.Equal(500, choice.PositionKm.X);
            Assert.Equal(t.RespawnRegionAnchorRadiusM, choice.RadiusM);
            Assert.Null(choice.NodeId);
        }

        [Fact]
        public void Placement_Confined_NeverAnchorsToAHostileStationInTheRegion()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                InRegion(Station("WAR", 5, StationSite.Space, atWar: true)),
                InRegion(Station("FCTM", 6, StationSite.Space, xenophobia: 1.00))
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                Confined(fallbackXKm: 500), null, stations, TestSupport.Tuning());

            Assert.Equal(SpawnAnchorKind.Region, choice.Kind);
        }

        [Fact]
        public void Placement_Unconfined_IgnoresTheRegionFlag()
        {
            // A world that authors no respawn region keeps the old behaviour exactly.
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("NEAR", 10, StationSite.Space),
                InRegion(Station("RING", 900, StationSite.Space))
            };

            SpawnAnchorChoice choice = SpawnAnchorSelector.SelectPlacement(
                InSpace(), null, stations, TestSupport.Tuning());

            Assert.Equal("NEAR", choice.NodeId);
            Assert.Equal(SpawnAnchorKind.None,
                SpawnAnchorSelector.SelectRegionFallback(InSpace(), TestSupport.Tuning()).Kind);
        }

        [Fact]
        public void RegionFallback_NeverAppliesOnAPlanet()
        {
            SpawnAnchorRequest req = OnPlanet("PERTAM");
            req.SpaceRespawnRestricted = true;
            req.SpaceRespawnFallbackKm = new Vec3(500, 0, 0);

            Assert.Equal(SpawnAnchorKind.None,
                SpawnAnchorSelector.SelectRegionFallback(req, TestSupport.Tuning()).Kind);
        }

        [Fact]
        public void Wayfinding_Confined_NamesAStationInTheRegion_OverANearerDeepSpaceOne()
        {
            var stations = new List<SpawnAnchorCandidate>
            {
                Station("NEST", 10, StationSite.Space),
                InRegion(Station("RING", 900, StationSite.Orbital))
            };

            SpawnAnchorCandidate target = SpawnAnchorSelector.SelectWayfindingTarget(
                Confined(), new Vec3(0, 0, 0), stations, TestSupport.Tuning());

            Assert.Equal("RING", target.NodeId);
        }
    }
}
