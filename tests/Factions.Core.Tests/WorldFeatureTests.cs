using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-068 slice 5: deep space holds minerals only where a world feature says so.
    /// See docs/ore-geography.md.</summary>
    public class WorldFeatureTests
    {
        private static WorldFeature Sphere(string name, Vec3 center, double radiusKm, params string[] ores)
        {
            var f = new WorldFeature
            {
                Name = name,
                Shape = WorldFeatureShape.Sphere,
                Center = center,
                OuterRadiusKm = radiusKm,
            };
            foreach (string o in ores) f.AddMaterial(new FeatureMaterial { SubTypeId = o });
            return f;
        }

        /// <summary>Cauldron's ring in the shape it was first authored in. Centre ~(16.4, 16.4, 16.4) km,
        /// disc in the world XZ plane so the normal is +Y, inner 180 km, outer 450 km, half-thickness
        /// 3.6 km. <b>Fixture coordinates, not live ones</b> — the live ring moved to 195-465 km on
        /// 2026-08-25; nothing here depends on which.
        /// The "giant" the ring hangs off is a 180 km virtual body around a 9.5 km voxel planet, so
        /// the radii come from the record and are not derived from the planet.</summary>
        private static WorldFeature CauldronRing(params string[] ores)
        {
            var f = new WorldFeature
            {
                Name = "Cauldron Ring",
                Shape = WorldFeatureShape.Ring,
                Center = new Vec3(16.387, 16.387, 16.387),
                Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = 180.0,
                OuterRadiusKm = 450.0,
                HalfThicknessKm = 3.6,
            };
            foreach (string o in ores) f.AddMaterial(new FeatureMaterial { SubTypeId = o });
            return f;
        }

        private static WorldFeatureGeography Geo(params WorldFeature[] features)
        {
            return new WorldFeatureGeography(features, null);
        }

        // ---- geometry --------------------------------------------------------------------------

        [Fact]
        public void Sphere_ContainsInside_AndExcludesOutside()
        {
            WorldFeature f = Sphere("Belt", new Vec3(1000, 0, 0), 50, "Iron");

            Assert.True(f.Contains(new Vec3(1030, 0, 0), f.Center));
            Assert.False(f.Contains(new Vec3(1060, 0, 0), f.Center));
        }

        [Fact]
        public void Ring_ExcludesTheHole_AndBeyondTheOuterEdge()
        {
            WorldFeature ring = CauldronRing("Ice");
            Vec3 c = ring.Center;

            Assert.True(ring.Contains(new Vec3(c.X + 300, c.Y, c.Z), c));   // in the annulus
            Assert.False(ring.Contains(new Vec3(c.X + 100, c.Y, c.Z), c));  // inside the hole
            Assert.False(ring.Contains(new Vec3(c.X + 500, c.Y, c.Z), c));  // past the outer edge
        }

        [Fact]
        public void Ring_IsAThinDisc_NotASphere()
        {
            // The whole reason a ring is not a sphere with a radius: at the same distance from the
            // centre, in-plane is inside and out-of-plane is not.
            WorldFeature ring = CauldronRing("Ice");
            Vec3 c = ring.Center;

            Assert.True(ring.Contains(new Vec3(c.X + 300, c.Y + 3.0, c.Z), c));
            Assert.False(ring.Contains(new Vec3(c.X + 300, c.Y + 10.0, c.Z), c));
            Assert.False(ring.Contains(new Vec3(c.X, c.Y + 300, c.Z), c)); // straight up the axis
        }

        [Fact]
        public void Ring_RespectsBandGaps()
        {
            WorldFeature ring = CauldronRing("Ice");
            ring.Bands.Add(new FeatureBand { InnerRadiusKm = 180.0, OuterRadiusKm = 193.5 });
            ring.Bands.Add(new FeatureBand { InnerRadiusKm = 198.9, OuterRadiusKm = 225.9 });
            Vec3 c = ring.Center;

            Assert.True(ring.Contains(new Vec3(c.X + 185, c.Y, c.Z), c));   // band 1
            Assert.False(ring.Contains(new Vec3(c.X + 196, c.Y, c.Z), c));  // the gap between them
            Assert.True(ring.Contains(new Vec3(c.X + 210, c.Y, c.Z), c));   // band 2
            Assert.False(ring.Contains(new Vec3(c.X + 300, c.Y, c.Z), c));  // in the annulus, no band
        }

        [Fact]
        public void Ring_HonoursANonAxisAlignedNormal()
        {
            var f = CauldronRing("Ice");
            f.Normal = new Vec3(1, 0, 0);   // disc in the YZ plane instead
            Vec3 c = f.Center;

            Assert.True(f.Contains(new Vec3(c.X, c.Y + 300, c.Z), c));
            Assert.False(f.Contains(new Vec3(c.X + 300, c.Y, c.Z), c));
        }

        [Fact]
        public void Shell_IsAHollowBall()
        {
            var f = new WorldFeature
            {
                Name = "Halo", Shape = WorldFeatureShape.Shell,
                Center = new Vec3(0, 0, 0), InnerRadiusKm = 100, OuterRadiusKm = 200,
            };

            Assert.False(f.Contains(new Vec3(50, 0, 0), f.Center));
            Assert.True(f.Contains(new Vec3(0, 150, 0), f.Center));
            Assert.False(f.Contains(new Vec3(0, 0, 250), f.Center));
        }

        // ---- geography -------------------------------------------------------------------------

        [Fact]
        public void OutsideEveryFeature_IsNotSamplable_NotBarren()
        {
            // The distinction the composite turns on: "features have nothing to say here" must fall
            // through to the planet reader, while "this authored region is empty" must not.
            WorldFeatureGeography geo = Geo(Sphere("Belt", new Vec3(1000, 0, 0), 50, "Iron"));

            OreSample sample;
            Assert.False(geo.TrySample(new Vec3(0, 0, 0), 7.5, out sample));
            Assert.Null(sample);
        }

        [Fact]
        public void EmptyCatalogue_SamplesNothing_WhichIsTheShippedDefault()
        {
            WorldFeatureGeography geo = Geo();

            OreSample sample;
            Assert.Equal(0, geo.Count);
            Assert.False(geo.TrySample(new Vec3(1000, 0, 0), 7.5, out sample));
            Assert.Empty(geo.MintableOres);
        }

        [Fact]
        public void InsideAFeature_ReportsItsMaterials()
        {
            WorldFeatureGeography geo = Geo(Sphere("Belt", new Vec3(1000, 0, 0), 50, "Iron", "Nickel"));

            OreSample sample;
            Assert.True(geo.TrySample(new Vec3(1010, 0, 0), 7.5, out sample));
            Assert.Equal(2, sample.Ores.Count);

            NodeDeposit deposit = DepositModel.Derive(sample);
            Assert.Equal(0.5, deposit.ShareOf("Iron"), 6);
            Assert.Equal(0.5, deposit.ShareOf("Nickel"), 6);
        }

        [Fact]
        public void MaterialShares_DriveTheDepositMix()
        {
            var f = Sphere("Belt", new Vec3(1000, 0, 0), 50);
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Iron", Share = 3.0 });
            f.AddMaterial(new FeatureMaterial { SubTypeId = "Cobalt", Share = 1.0 });

            OreSample sample;
            Assert.True(Geo(f).TrySample(new Vec3(1010, 0, 0), 7.5, out sample));

            NodeDeposit deposit = DepositModel.Derive(sample);
            Assert.Equal(3.0, deposit.ShareOf("Iron") / deposit.ShareOf("Cobalt"), 3);
        }

        [Fact]
        public void AFeatureWithNoMaterials_IsAuthoredAsBarren_AndStillSamples()
        {
            // "This region is deliberately empty" has to be expressible, and it must still shadow
            // whatever the planet reader would otherwise say beneath it. An EMPTY list says that; a
            // null one says nothing at all -- see AFeatureWithNullMaterials_HasNoOreOpinion.
            WorldFeature f = Sphere("Dead Zone", new Vec3(1000, 0, 0), 50);
            f.Materials = new List<FeatureMaterial>();
            WorldFeatureGeography geo = Geo(f);

            OreSample sample;
            Assert.True(geo.TrySample(new Vec3(1010, 0, 0), 7.5, out sample));
            Assert.True(sample.IsBarren);
        }

        [Fact]
        public void AFeatureWithNullMaterials_HasNoOreOpinion_AndIsTransparent()
        {
            // A placement-only feature -- the operator's "planets as rings without asteroids". It must
            // NOT report barren, or it would blank the surface ore of every body it covers.
            WorldFeature f = Sphere("Placement Only", new Vec3(1000, 0, 0), 50);
            f.Materials = null;

            OreSample sample;
            Assert.False(Geo(f).TrySample(new Vec3(1010, 0, 0), 7.5, out sample));
            Assert.False(f.HasOreOpinion());
        }

        [Fact]
        public void APlacementOnlyFeature_DoesNotShadowThePlanetBeneathIt()
        {
            var planets = new StubGeography { Answer = true, Ore = "Iron" };
            WorldFeature f = Sphere("Placement Only", new Vec3(0, 0, 0), 500);
            f.Materials = null;
            var composite = new CompositeOreGeography(Geo(f), planets);

            OreSample sample;
            Assert.True(composite.TrySample(new Vec3(100, 0, 0), 7.5, out sample));
            Assert.Equal("Iron", sample.Ores[0].SubTypeId);
            Assert.Equal(1, planets.Calls);
        }

        [Fact]
        public void APlacementOnlyFeature_DoesNotShadowAnOreBearingFeatureItContains()
        {
            // Innermost-wins would hand the sample to the placement-only region; having no opinion
            // has to take it out of that contest entirely, not just make it decline afterwards.
            WorldFeature outer = Sphere("Ring", new Vec3(0, 0, 0), 500, "Ice");
            WorldFeature inner = Sphere("Placement Only", new Vec3(0, 0, 0), 50);
            inner.Materials = null;

            OreSample sample;
            Assert.True(Geo(outer, inner).TrySample(new Vec3(10, 0, 0), 7.5, out sample));
            Assert.Equal("Ice", sample.Ores[0].SubTypeId);
        }

        [Fact]
        public void ABandWithMaterials_GivesItsRingAnOpinion_EvenWhenTheRingItselfHasNone()
        {
            WorldFeature ring = CauldronRing();
            ring.Materials = null;
            ring.Bands = new List<FeatureBand>
            {
                new FeatureBand
                {
                    InnerRadiusKm = 180, OuterRadiusKm = 200,
                    Materials = new List<FeatureMaterial> { new FeatureMaterial { SubTypeId = "Iron" } },
                },
            };

            Assert.True(ring.HasOreOpinion());

            OreSample sample;
            Assert.True(Geo(ring).TrySample(new Vec3(16.387 + 190, 16.387, 16.387), 7.5, out sample));
            Assert.Equal("Iron", sample.Ores[0].SubTypeId);
        }

        [Fact]
        public void OverlappingFeatures_TheSmallerOneWins()
        {
            WorldFeatureGeography geo = Geo(
                Sphere("Broad Belt", new Vec3(1000, 0, 0), 500, "Iron"),
                Sphere("Rich Pocket", new Vec3(1000, 0, 0), 20, "Platinum"));

            OreSample sample;
            Assert.True(geo.TrySample(new Vec3(1005, 0, 0), 7.5, out sample));
            Assert.Single(sample.Ores);
            Assert.Equal("Platinum", sample.Ores[0].SubTypeId);
        }

        [Fact]
        public void MintableOres_IsTheUnionAcrossFeatures()
        {
            WorldFeatureGeography geo = Geo(
                Sphere("A", new Vec3(0, 0, 0), 10, "Iron", "Nickel"),
                Sphere("B", new Vec3(5000, 0, 0), 10, "Nickel", "Platinum"));

            var ores = new List<string>(geo.MintableOres);

            Assert.Equal(3, ores.Count);
            Assert.Contains("Platinum", ores);
        }

        // ---- anchoring -------------------------------------------------------------------------

        [Fact]
        public void ParsingAFeatureWithNoMaterialsKey_LeavesItWithNoOreOpinion()
        {
            const string json = @"{ ""Features"": [
                { ""Name"": ""Tellus Orbit"", ""Shape"": ""Shell"", ""AnchorPlanetId"": ""7"",
                  ""InnerRadiusKm"": 40, ""OuterRadiusKm"": 60 } ] }";

            string status;
            WorldFeature f = Assert.Single(WorldFeatureFile.Parse(json, out status));

            Assert.Null(f.Materials);          // absence is "no opinion", not an authored empty region
            Assert.False(f.HasOreOpinion());
        }

        [Fact]
        public void AnchoredFeature_FollowsItsPlanet_NotItsLiteralCenter()
        {
            var planets = new StaticPlanetCatalogue(new List<PlanetRecord>
            {
                new PlanetRecord { Id = "42", Name = "Cauldron", Center = new Vec3(9000, 0, 0), SurfaceRadius = 9.5 },
            });
            WorldFeature ring = CauldronRing("Ice");
            ring.AnchorPlanetId = "42";

            var geo = new WorldFeatureGeography(new[] { ring }, planets);

            OreSample sample;
            Assert.Empty(geo.UnresolvedAnchors);
            Assert.True(geo.TrySample(new Vec3(9300, 0, 0), 7.5, out sample));           // around the planet
            Assert.False(geo.TrySample(new Vec3(316.387, 0, 0), 7.5, out sample));       // at the literal Center
        }

        [Fact]
        public void AnchorNamingAMissingPlanet_IsReported_NotSilentlyDropped()
        {
            WorldFeature ring = CauldronRing("Ice");
            ring.AnchorPlanetId = "does-not-exist";

            var geo = new WorldFeatureGeography(new[] { ring }, new StaticPlanetCatalogue(new List<PlanetRecord>()));

            Assert.Single(geo.UnresolvedAnchors);
            Assert.Contains("Cauldron Ring", geo.UnresolvedAnchors[0]);
            Assert.Equal(1, geo.Count); // still present, at its literal Center
        }

        // ---- composition with the planet reader ------------------------------------------------

        private sealed class StubGeography : IOreGeography
        {
            public bool Answer;
            public string Ore = "Iron";
            public int Calls;

            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                Calls++;
                if (!Answer) { sample = null; return false; }
                sample = new OreSample { PointsExamined = 100 };
                sample.Ores.Add(new OreObservation(Ore, 100, 10.0, 5.0));
                return true;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                if (!TrySample(posKm, radiusKm, out sample)) return false;
                sample.PointsExamined = count;
                return true;
            }

            public OreProspect CouldBearOre(Vec3 posKm) { return OreProspect.Possible; }

            public IEnumerable<string> MintableOres { get { yield return Ore; } }
        }

        [Fact]
        public void Composite_PrefersTheFeature_OverThePlanetBeneathIt()
        {
            // The Cauldron case: the ring sits inside a gas giant's gravity well, and that body
            // carries no ore. A planet-first order would let it shadow the ring.
            var planets = new StubGeography { Answer = true, Ore = "Nothing" };
            var composite = new CompositeOreGeography(
                Geo(Sphere("Ring", new Vec3(0, 0, 0), 500, "Ice")), planets);

            OreSample sample;
            Assert.True(composite.TrySample(new Vec3(100, 0, 0), 7.5, out sample));
            Assert.Equal("Ice", sample.Ores[0].SubTypeId);
            Assert.Equal(0, planets.Calls); // guard: the planet reader was never consulted
        }

        [Fact]
        public void Composite_FallsThroughToThePlanet_WhereNoFeatureApplies()
        {
            var planets = new StubGeography { Answer = true, Ore = "Iron" };
            var composite = new CompositeOreGeography(
                Geo(Sphere("Ring", new Vec3(0, 0, 0), 50, "Ice")), planets);

            OreSample sample;
            Assert.True(composite.TrySample(new Vec3(5000, 0, 0), 7.5, out sample));
            Assert.Equal("Iron", sample.Ores[0].SubTypeId);
            Assert.Equal(1, planets.Calls);
        }

        [Fact]
        public void Composite_WithNoFeaturesAndNoPlanets_SamplesNothing()
        {
            var composite = new CompositeOreGeography(Geo(), new StubGeography { Answer = false });

            OreSample sample;
            Assert.False(composite.TrySample(new Vec3(0, 0, 0), 7.5, out sample));
        }

        // ---- end to end ------------------------------------------------------------------------

        [Fact]
        public void ADeepSpaceNodeInsideARing_GetsTheRingsDeposit()
        {
            var world = new WorldModel(new SeededRandom(7)) { Tuning = TestSupport.Tuning() };
            world.Ores = new CompositeOreGeography(Geo(CauldronRing("Ice")), NullOreGeography.Instance);

            SpatialNode n = world.Graph.Add(new Vec3(316.387, 16.387, 16.387), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.Zone = NodeZone.DeepSpace;

            world.ResolveDeposit(n);

            Assert.NotNull(n.Deposit);
            Assert.Equal("Ice", NodeDisplay.DominantOre(n));
            Assert.Equal(1.0, n.Deposit.ShareOf("Ice"), 6);
        }

        [Fact]
        public void ADeepSpaceNodeOutsideEveryFeature_KeepsItsKind_BecauseNothingWasSampled()
        {
            // No feature and no planet: the seam declines, so seeding leaves the node exactly as it
            // was. Deep space is empty by default rather than aggressively demoting everything.
            var world = new WorldModel(new SeededRandom(7)) { Tuning = TestSupport.Tuning() };
            world.Ores = new CompositeOreGeography(Geo(CauldronRing("Ice")), NullOreGeography.Instance);

            SpatialNode n = world.Graph.Add(new Vec3(50000, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.Zone = NodeZone.DeepSpace;

            world.ResolveDeposit(n);

            Assert.Null(n.Deposit);
            Assert.False(NodeContent.IsMineable(n));
        }

        // ---- file parsing ----------------------------------------------------------------------

        [Fact]
        public void Parse_TheLiveWorldsFeatureFile_ProducesUsableFeatures()
        {
            // Guards the whole authoring path at once: Vec3 must survive JSON as {X,Y,Z}, Shape must
            // deserialise from its name, and Bands/Materials must populate. A silently misparsed file
            // means deep space silently holds no ore, which no other test would catch.
            string json = System.IO.File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_features.json"));

            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(json, out status);

            // Not a count: the world gained six FCTM nests (FAC-104) and this asserted exactly one,
            // so a correct world read as a regression. What the test is actually for is that the
            // file PARSES into usable features -- assert that, and assert the ring specifically.
            Assert.NotEmpty(features);
            Assert.DoesNotContain("REJECTED", status, System.StringComparison.Ordinal);
            Assert.All(features, f => Assert.False(string.IsNullOrEmpty(f.Name)));

            WorldFeature ring = features.Find(f => f.Name == "Cauldron Ring");
            Assert.NotNull(ring);
            Assert.Equal(WorldFeatureShape.Ring, ring.Shape);
            // The live-tuned Cauldron geometry (2026-08-25): the ring sits 15 km further out than
            // RGG's visual ring so its innermost band clears the gas giant's 191.3 km atmosphere.
            Assert.Equal(195.0, ring.InnerRadiusKm, 6);
            Assert.Equal(465.0, ring.OuterRadiusKm, 6);
            Assert.Equal(1.8, ring.HalfThicknessKm, 6);
            Assert.Equal(9, ring.Bands.Count);
            Assert.Equal(1.0, ring.Normal.Y, 6);            // Vec3 really round-tripped
            // The centre is whatever the emit realised: authored 16.387 before the ring was
            // regenerated against live planets, 0 after, and the gas giant sits at 0.001. Both are
            // the same place to within two metres, so pin the RELATIONSHIP rather than the literal.
            Assert.True(System.Math.Abs(ring.Center.X) < 20.0, "ring centre drifted: " + ring.Center.X);
            Assert.Equal("72111577199174151", ring.AnchorPlanetId);
            Assert.Single(ring.Materials);
            Assert.Equal("Ice", ring.Materials[0].SubTypeId);

        }

        [Fact]
        public void Parse_TheShippedExampleFile_CoversTheShapesTheLiveWorldDoesNot()
        {
            // The example file exists to document Sphere and Shell, which this world's single Ring
            // never exercises. Parsed here so the schema reference cannot rot unnoticed.
            string json = System.IO.File.ReadAllText(TestSupport.RepoFile("instance/world_features.example.json"));

            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(json, out status);

            Assert.Equal(2, features.Count);
            Assert.DoesNotContain("REJECTED", status, System.StringComparison.Ordinal);
            Assert.DoesNotContain(features, f => f.Name == "Cauldron Ring");   // the world is not an example

            WorldFeature gas = features.Find(f => f.Name == "Example Gas Pocket");
            Assert.Equal(WorldFeatureShape.Shell, gas.Shape);
            Assert.Equal(2, gas.Materials.Count);
        }

        [Fact]
        public void Parse_TheAuthoredRing_ContainsAPointInItsFirstBand()
        {
            // Parsing the numbers correctly is not the same as the geometry meaning anything, so
            // this walks the parsed record all the way to a containment answer.
            string json = System.IO.File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_features.json"));
            string status;
            WorldFeature ring = WorldFeatureFile.Parse(json, out status).Find(f => f.Name == "Cauldron Ring");
            Vec3 c = ring.Center;

            Assert.True(ring.Contains(new Vec3(c.X + 200, c.Y, c.Z), c));    // inside band 1
            Assert.False(ring.Contains(new Vec3(c.X + 211, c.Y, c.Z), c));   // in the gap after it
            Assert.False(ring.Contains(new Vec3(c.X + 200, c.Y + 10, c.Z), c)); // above the disc
        }

        [Fact]
        public void Parse_Garbage_IsBlankNotAnException()
        {
            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse("{ this is not json", out status);

            Assert.Empty(features);
            Assert.Contains("unreadable", status, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_Empty_IsBlank()
        {
            string status;
            Assert.Empty(WorldFeatureFile.Parse("{ \"Features\": [] }", out status));
            Assert.Contains("no features defined", status, System.StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("{ \"Features\": [ { \"Shape\": \"Sphere\", \"OuterRadiusKm\": 10 } ] }", "no Name")]
        [InlineData("{ \"Features\": [ { \"Name\": \"A\", \"Shape\": \"Sphere\" } ] }", "OuterRadiusKm")]
        [InlineData("{ \"Features\": [ { \"Name\": \"A\", \"Shape\": \"Ring\", \"InnerRadiusKm\": 500, \"OuterRadiusKm\": 100, \"HalfThicknessKm\": 1 } ] }", "InnerRadiusKm must be less")]
        [InlineData("{ \"Features\": [ { \"Name\": \"A\", \"Shape\": \"Ring\", \"InnerRadiusKm\": 10, \"OuterRadiusKm\": 100 } ] }", "HalfThicknessKm")]
        public void Parse_RejectsAFeatureThatCannotDescribeAVolume_AndSaysWhy(string json, string expectedReason)
        {
            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(json, out status);

            Assert.Empty(features);
            Assert.Contains("REJECTED", status, System.StringComparison.Ordinal);
            Assert.Contains(expectedReason, status, System.StringComparison.Ordinal);
        }
    }
}
