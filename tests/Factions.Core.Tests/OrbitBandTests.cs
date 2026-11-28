using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-537: a planet's orbit is an authored band just outside its gravity well, and the well
    /// itself is Surface. The operator asked for a player node in orbit of Tellus on production and
    /// was quoted the 500 km DeepSpace floor: the orbit snap solved for 0.05 g, which is exactly where
    /// SE cuts natural gravity off, so the snapped point read g == 0.
    ///
    /// <para>Measured against a catalogue that cuts off the way SE does
    /// (<c>MySphericalNaturalGravityComponent</c>, <c>GRAVITY_LIMIT_STRENGTH = 0.05</c>), not the
    /// linear proxy the other fixtures use, under which a 0.05 g point is still inside the well and
    /// the defect cannot be seen.</para>
    /// </summary>
    public class OrbitBandTests
    {
        /// <summary>Tellus as the live host reports it: 30 km surface, 31.8 km highest terrain,
        /// falloff 7 from 1 g, cut off below 0.05 g -- which lands the limit at 48.79 km.</summary>
        private sealed class SeTellus : IPlanetCatalogue
        {
            internal const double SurfaceR = 30.0;
            internal const double MaxR = 31.8;
            internal const double GravityR = 48.785;

            internal readonly PlanetRecord Record = new PlanetRecord
            {
                Id = "83859379220048820", Name = "Tellus", Generator = "Tellus",
                Center = new Vec3(0, 0, 0),
                SurfaceRadius = SurfaceR, MaximumRadius = MaxR, GravityRadius = GravityR,
                AtmosphereRadius = 34.86, HasAtmosphere = true,
                NodeSeparationKm = 14.0,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return Record; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(Record.Center);
                if (d <= MaxR) return 1.0;
                double g = Math.Pow(MaxR / d, 7.0);
                return g < 0.05 ? 0.0 : g;
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double d = posKm.DistanceTo(Record.Center);
                if (d < 1e-9) return new Vec3(SurfaceR, 0, 0);
                double k = SurfaceR / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm)
            {
                double d = posKm.DistanceTo(Record.Center);
                return d < 1e-9 ? new Vec3(0, 1, 0) : new Vec3(posKm.X / d, posKm.Y / d, posKm.Z / d);
            }
        }

        /// <summary>The band as <c>world plan --features</c> emits it for Tellus.</summary>
        private static WorldFeature TellusOrbit()
        {
            return new WorldFeature
            {
                Name = "Tellus Orbit",
                Shape = WorldFeatureShape.Shell,
                Center = new Vec3(0, 0, 0),
                InnerRadiusKm = SeTellus.GravityR,
                OuterRadiusKm = SeTellus.GravityR + 30.0,
                NodeSeparationKm = 30.0,
                Zone = NodeZone.Orbit,
                GivesUpHole = true,
                Materials = new List<FeatureMaterial>(),
            };
        }

        private static WorldModel World(bool withBand = true)
        {
            var w = new WorldModel(new SeededRandom(7))
            {
                Planets = new SeTellus(),
                Tuning = new FactionTuning
                {
                    OrbitGravityMin = 0.05,
                    NodeSeparationSurfaceKm = 15.0,
                    NodeSeparationOrbitKm = 30.0,
                    NodeSeparationDeepSpaceKm = 500.0,
                    NodeExpansionReachPct = 0.25,
                    NodeExpansionSampleCount = 4,
                },
            };
            if (withBand) w.Features = new WorldFeaturePlacement(new[] { TellusOrbit() }, null);
            return w;
        }

        private static Vec3 At(double r) { return new Vec3(r, 0, 0); }

        private static SpatialNode Add(WorldModel w, Vec3 pos, string id)
        {
            SpatialNode n = w.Graph.Add(pos, 0.5, id);
            n.Zone = w.PlaceableZoneAt(pos);
            return n;
        }

        // ---- the operator's case -----------------------------------------------------------------

        [Fact]
        public void WithoutTheBand_JustOutsideTheWellQuotesTheDeepSpaceFloor()
        {
            // The defect, pinned so the band is measured against it.
            NodePlacementProbe probe = World(withBand: false).ProbePlacement(At(55));

            Assert.Equal(NodeZone.DeepSpace, probe.Zone);
            Assert.Equal(500.0, probe.SeparationKm, 6);
        }

        [Fact]
        public void InTheBand_AProbeIsAnOrbitSiteAt30Km_WhereItWasAsked()
        {
            NodePlacementProbe probe = World().ProbePlacement(At(55));

            Assert.Equal(NodeZone.Orbit, probe.Zone);
            Assert.Equal(30.0, probe.SeparationKm, 6);
            Assert.Equal(0.0, probe.PlacedKm.DistanceTo(At(55)), 6);
            Assert.False(probe.NoGround);
            Assert.True(probe.Clear);
        }

        [Fact]
        public void BeyondTheBand_IsDeepSpace()
        {
            NodePlacementProbe probe = World().ProbePlacement(At(SeTellus.GravityR + 31.0));

            Assert.Equal(NodeZone.DeepSpace, probe.Zone);
            Assert.Equal(500.0, probe.SeparationKm, 6);
        }

        // ---- the well under the band is Surface ----------------------------------------------------

        [Theory]
        [InlineData(35.0)]   // low in the well
        [InlineData(48.0)]   // just inside the cutoff, at 0.05 g
        public void InsideTheWell_AProbeGoesToTheGround_TheBandGivesItsHoleUp(double r)
        {
            WorldModel w = World();
            Assert.True(w.Planets.GravityAt(At(r)) > 0, "fixture: the point must be inside the well");

            NodePlacementProbe probe = w.ProbePlacement(At(r));

            Assert.Equal(NodeZone.Surface, probe.Zone);
            Assert.Equal(SeTellus.SurfaceR, probe.PlacedKm.DistanceTo(new Vec3(0, 0, 0)), 6);
            Assert.Equal(14.0, probe.SeparationKm, 6);
        }

        [Fact]
        public void AShellThatKeepsItsHole_StillPullsTheWellOutIntoIt()
        {
            // The control for the test above: without GivesUpHole the same band claims the well, so
            // that test is measuring the flag and not an accident of geometry.
            WorldFeature band = TellusOrbit();
            band.GivesUpHole = false;
            WorldModel w = World(withBand: false);
            w.Features = new WorldFeaturePlacement(new[] { band }, null);

            Assert.Equal(NodeZone.Orbit, w.PlaceableZoneAt(At(40)));
        }

        // ---- separation: an orbit site and the ground below it -----------------------------------

        [Fact]
        public void AnOrbitSite_OwesASurfaceNodeBelowItOnlyThatNodesOwnSeparation()
        {
            WorldModel w = World();
            Add(w, At(SeTellus.SurfaceR), "G1");

            // 19.5 km straight up from G1: inside the band's 30 km, outside Tellus's 14 km.
            NodePlacementProbe probe = w.ProbePlacement(At(49.5));

            Assert.Equal(NodeZone.Orbit, probe.Zone);
            Assert.Equal(19.5, probe.NearestNodeKm, 6);
            Assert.True(probe.Clear);
        }

        [Fact]
        public void TwoOrbitSites_StillOweEachOtherTheBandsFull30Km()
        {
            WorldModel w = World();
            Add(w, At(60), "O1");

            NodePlacementProbe probe = w.ProbePlacement(new Vec3(60, 20, 0));

            Assert.Equal(NodeZone.Orbit, probe.Zone);
            Assert.False(probe.Clear);
        }

        [Fact]
        public void AnOrbitSite_StillCrowdsASurfaceNodeInsideItsOwnSeparation()
        {
            // The rule is the smaller separation, not no separation: 10 km from a band node is still
            // inside Tellus's 14 km. The band's inner face is 18.8 km off the ground, so the only way
            // to get that close is a surface node standing on a mountain -- set up directly.
            WorldModel w = World();
            SpatialNode high = w.Graph.Add(At(40), 0.5, "G1");
            high.Zone = NodeZone.Surface;

            Assert.False(w.ProbePlacement(At(50)).Clear);
        }

        [Fact]
        public void ABandNodeOverAGroundNode_IsNotASeparationViolation()
        {
            WorldModel w = World();
            Add(w, At(SeTellus.SurfaceR), "G1");
            Add(w, At(49.5), "O1");
            Add(w, new Vec3(49.5, 20, 0), "O2");   // 20 km from O1: the control, which is one

            IList<NodeSeparationViolation> violations = w.GetNodeSeparationViolations();

            Assert.DoesNotContain(violations, v => v.NewNodeId == "O1" && v.OffendingNeighborId == "G1");
            Assert.DoesNotContain(violations, v => v.NewNodeId == "G1");
            Assert.Contains(violations, v => v.NewNodeId == "O2" && v.OffendingNeighborId == "O1"
                                             && Math.Abs(v.RequiredMinSepKm - 30.0) < 1e-6);
        }

        // ---- boot: what is already there -----------------------------------------------------------

        [Fact]
        public void Reclassify_LeavesABandNodeWhereItIs_AndGroundsAnOldOrbitNodeInTheWell()
        {
            WorldModel w = World();
            SpatialNode band = w.Graph.Add(At(60), 0.5, "O1");
            SpatialNode old = w.Graph.Add(new Vec3(0, 45, 0), 0.5, "O2");
            band.Zone = NodeZone.Orbit;
            old.Zone = NodeZone.Orbit;   // the retired relative reading's label

            NodeReclassifyResult r = w.ReclassifyNodes(applyFix: true);

            Assert.Equal(NodeZone.Orbit, band.Zone);
            Assert.Equal(0.0, band.Position.DistanceTo(At(60)), 6);
            Assert.Equal(NodeZone.Surface, old.Zone);
            Assert.Equal(SeTellus.SurfaceR, old.Position.DistanceTo(new Vec3(0, 0, 0)), 6);
            Assert.Equal(1, r.Repositioned);
            Assert.Equal(0, r.OrbitRepositioned);
        }

        // ---- the recipe and the emitted file -------------------------------------------------------

        private static WorldPlan GenesisPlan()
        {
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")), out error);
            Assert.Null(error);
            var types = new StaticPlanetTypeCatalogue(new List<PlanetTypeSpec>
            {
                Spec("RealGasGiant", false), Spec("Tellus", true), Spec("Agni", true), Spec("Kor", true)
            });
            WorldPlan plan = WorldPlanner.Plan(recipe, types);
            Assert.Empty(plan.Errors);
            return plan;
        }

        private static PlanetTypeSpec Spec(string name, bool surface)
        {
            return new PlanetTypeSpec
            {
                DefinitionName = name,
                HillParamsMax = name == "RealGasGiant" ? 0.0 : 0.03,
                SurfaceGravity = 1.0,
                GravityFalloffPower = 7.0,
                AllowSurfaceEconomyStations = surface,
                AllowOrbitalEconomyStations = true
            };
        }

        [Fact]
        public void TheGenesisRecipe_GivesEveryRockyPlanetABand_AndTheGasGiantNone()
        {
            List<PlannedFeature> bands = GenesisPlan().Features.FindAll(f => f.OrbitRegionWidthKm > 0);

            Assert.Equal(new[] { "Agni Orbit", "Tellus Orbit", "Kor Orbit" }, bands.ConvertAll(f => f.Name).ToArray());
            foreach (PlannedFeature b in bands)
            {
                Assert.Equal(WorldFeatureShape.Shell, b.Shape);
                Assert.Equal(NodeZone.Orbit, b.Zone);
                Assert.Equal(30.0, b.OrbitRegionWidthKm, 6);
                Assert.Equal(30.0, b.NodeSeparationKm);
                Assert.True(b.GivesUpHole);
                Assert.NotNull(b.Materials);
                Assert.Empty(b.Materials);   // barren, not "no opinion"
                Assert.False(b.SpaceRespawn);
            }
        }

        [Fact]
        public void TheCommittedBands_AreWhatTheRecipeEmitsAtTheirGravityRadius()
        {
            string status;
            List<WorldFeature> authored = WorldFeatureFile.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_features.json")), out status);

            foreach (PlannedFeature planned in GenesisPlan().Features.FindAll(f => f.OrbitRegionWidthKm > 0))
            {
                WorldFeature committed = authored.Find(f => f.Name == planned.Name);
                Assert.True(committed != null, planned.Name + " is missing from world_features.json");

                double inner, outer;
                Assert.True(planned.TryResolveRadii(committed.InnerRadiusKm, out inner, out outer));
                Assert.Equal(inner, committed.InnerRadiusKm, 6);
                Assert.Equal(outer, committed.OuterRadiusKm, 6);
                Assert.Equal(planned.Shape, committed.Shape);
                Assert.Equal(planned.Zone, committed.Zone);
                Assert.Equal(planned.NodeSeparationKm, committed.NodeSeparationKm);
                Assert.Equal(planned.GivesUpHole, committed.GivesUpHole);
                Assert.False(string.IsNullOrEmpty(committed.AnchorPlanetId), planned.Name + " must follow its body");
                Assert.NotNull(committed.Materials);
                Assert.Empty(committed.Materials);
            }
        }

        [Fact]
        public void ABandWithNoLiveGravityRadius_IsNotResolved()
        {
            var band = new PlannedFeature { Name = "Tellus Orbit", OrbitRegionWidthKm = 30.0 };
            double inner, outer;

            Assert.False(band.TryResolveRadii(0, out inner, out outer));
            Assert.True(band.TryResolveRadii(48.785, out inner, out outer));
            Assert.Equal(48.785, inner, 6);
            Assert.Equal(78.785, outer, 6);

            // Any other feature keeps its planned radii whatever the body reports.
            var ring = new PlannedFeature { InnerRadiusKm = 195, OuterRadiusKm = 465 };
            Assert.True(ring.TryResolveRadii(0, out inner, out outer));
            Assert.Equal(195.0, inner, 6);
            Assert.Equal(465.0, outer, 6);
        }

        [Fact]
        public void AnOrbitRegionWithNoWidth_IsAPlanError()
        {
            WorldRecipe recipe = new WorldRecipe();
            recipe.Planets.Add(new RecipePlanet
            {
                Name = "Tellus", Definition = "Tellus", DiameterKm = 60,
                CenterKm = new Vec3(5000, 0, 0),
                OrbitRegion = new RecipeOrbitRegion { WidthKm = 0 },
            });
            var types = new StaticPlanetTypeCatalogue(new List<PlanetTypeSpec> { Spec("Tellus", true) });

            WorldPlan plan = WorldPlanner.Plan(recipe, types);

            Assert.Contains(plan.Errors, e => e.Contains("OrbitRegion.WidthKm"));
            Assert.DoesNotContain(plan.Features, f => f.OrbitRegionWidthKm > 0);
        }

        [Fact]
        public void GivesUpHole_IsWrittenOnlyWhereItIsTrue()
        {
            // Rule 4, lean JSON: every feature that predates the flag stays byte-identical.
            string withFlag = JsonConvert.SerializeObject(TellusOrbit());
            WorldFeature plain = TellusOrbit();
            plain.GivesUpHole = false;

            Assert.Contains("\"GivesUpHole\":true", withFlag);
            Assert.DoesNotContain("GivesUpHole", JsonConvert.SerializeObject(plain));
            Assert.True(JsonConvert.DeserializeObject<WorldFeature>(withFlag).GivesUpHole);
        }
    }
}
