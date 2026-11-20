using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class WorldPlannerTests
    {
        // Numbers taken from real SE generator definitions so the arithmetic below is checkable
        // against the live world rather than against itself.
        private static PlanetTypeSpec EarthLike()
        {
            return new PlanetTypeSpec
            {
                DefinitionName = "EarthLike",
                HillParamsMax = 0.03,
                SurfaceGravity = 1.0,
                GravityFalloffPower = 7.0,
                AllowSurfaceEconomyStations = true,
                AllowOrbitalEconomyStations = true
            };
        }

        // The gas giant this whole exclusion rule exists for: a 9.5 km voxel core that cannot be
        // landed on, carrying a ring that reaches 450 km.
        private static PlanetTypeSpec GasGiant()
        {
            return new PlanetTypeSpec
            {
                DefinitionName = "RealGasGiant",
                HillParamsMax = 0.0,
                SurfaceGravity = 1.6,
                GravityFalloffPower = 7.0,
                AllowSurfaceEconomyStations = false,
                AllowOrbitalEconomyStations = true
            };
        }

        private static IPlanetTypeCatalogue Catalogue()
        {
            return new StaticPlanetTypeCatalogue(new List<PlanetTypeSpec> { EarthLike(), GasGiant() });
        }

        private static WorldRecipe Recipe(params RecipePlanet[] planets)
        {
            var r = new WorldRecipe
            {
                WorldSeed = 20260824,
                Volume = new WorldVolume { Shape = WorldVolumeShape.Box, ExtentKm = 30000 },
                Placement = new WorldPlacementRules { SeparationMarginKm = 100, MaxAttemptsPerPlanet = 500 }
            };
            r.Planets.AddRange(planets);
            return r;
        }

        private static RecipePlanet Planet(string name, string def, double diameterKm)
        {
            return new RecipePlanet { Name = name, Definition = def, DiameterKm = diameterKm };
        }

        // ---- the min-corner trap ---------------------------------------------------------------

        [Fact]
        public void StorageSizeIsAPowerOfTwoMetres_NotTheDiameter()
        {
            // MyVoxelCoordSystems.FindBestOctreeSize: smallest power of two >= 2 x MaxRadius, floor 32.
            PlanetTypeSpec spec = EarthLike();
            // 120 km diameter -> MaxRadius 61.8 km -> needs 123 600 m -> 131 072 m.
            Assert.Equal(131.072, spec.StorageSizeKm(120), 3);
            // A 19 km gas giant: MaxRadius 9.5 km -> needs 19 000 m -> 32 768 m. This is Cauldron.
            Assert.Equal(32.768, GasGiant().StorageSizeKm(19), 3);
        }

        [Fact]
        public void MinCornerOffsetIsHalfTheStorageCube_ReproducingCauldron()
        {
            // The live world's Cauldron sits at a min corner of 3 m with a centre at 16 387 m.
            // 3 m + 32 768/2 = 16 387. If this ever stops holding, every anchored ring moves 16 km.
            PlanetTypeSpec giant = GasGiant();
            var minCorner = new Vec3(0.003, 0.003, 0.003);
            Vec3 centre = giant.CenterFromMinCornerKm(minCorner, 19);
            Assert.Equal(16.387, centre.X, 3);
            Assert.Equal(16.387, centre.Y, 3);
            Assert.Equal(16.387, centre.Z, 3);
        }

        [Fact]
        public void CentreToMinCornerRoundTrips()
        {
            PlanetTypeSpec spec = EarthLike();
            var centre = new Vec3(1234.5, -6789.0, 42.0);
            Vec3 back = spec.CenterFromMinCornerKm(spec.MinCornerFromCenterKm(centre, 120), 120);
            Assert.Equal(centre.X, back.X, 6);
            Assert.Equal(centre.Y, back.Y, 6);
            Assert.Equal(centre.Z, back.Z, 6);
        }

        [Fact]
        public void PlanDerivesMinCornerFromTheAuthoredCentre()
        {
            RecipePlanet rp = Planet("Tellus", "EarthLike", 120);
            rp.CenterKm = new Vec3(5000, 0, 0);
            WorldPlan plan = WorldPlanner.Plan(Recipe(rp), Catalogue());

            PlannedPlanet pp = plan.Planets[0];
            Assert.Equal(5000, pp.CenterKm.X, 6);
            Assert.Equal(5000 - 131.072 / 2, pp.MinCornerKm.X, 3);
        }

        // ---- gravity limit ---------------------------------------------------------------------

        [Fact]
        public void GravityLimitIsMaxRadiusWhenSurfaceGravityIsTheCutoff()
        {
            // The formula's fixed point: a body whose surface gravity IS the 0.05 g cutoff has its
            // limit exactly at its outermost terrain. Pins the shape of the expression, not a
            // particular planet's numbers.
            var atCutoff = new PlanetTypeSpec
            {
                DefinitionName = "Cutoff",
                HillParamsMax = 0.03,
                SurfaceGravity = PlanetTypeSpec.GravityLimitStrength,
                GravityFalloffPower = 7.0
            };
            Assert.Equal(atCutoff.MaxRadiusKm(120), atCutoff.GravityLimitKm(120), 9);
        }

        [Fact]
        public void GravityLimitFollowsSesFormula()
        {
            // MaxRadius x (SurfaceGravity / 0.05) ^ (1 / falloff), per
            // MySphericalNaturalGravityComponent.CalculateGravityLimit. For these inputs:
            // 60 x 1.03 = 61.8, x 20^(1/7) = 94.809...
            //
            // The live server reports ~93 km for its own 120 km EarthLike; the small gap is this
            // test's HillParamsMax standing in for the real definition's, which is a host-resolved
            // number. Asserting the formula here and reading the real constants from SE at runtime
            // is deliberate -- pinning a live figure this test cannot see would be pinning a guess.
            Assert.Equal(94.809073606, EarthLike().GravityLimitKm(120), 6);
        }

        // ---- the three placement constraints ---------------------------------------------------

        [Fact]
        public void GravityWellsDoNotOverlap()
        {
            WorldRecipe recipe = Recipe(Planet("A", "EarthLike", 120), Planet("B", "EarthLike", 120));
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());

            Assert.True(plan.IsApplicable);
            PlannedPlanet a = plan.Planets[0], b = plan.Planets[1];
            double needed = a.ExclusionRadiusKm + b.ExclusionRadiusKm + 100;
            Assert.True(a.CenterKm.DistanceTo(b.CenterKm) >= needed);
        }

        [Fact]
        public void AFeatureWidensItsBodysExclusionRadius()
        {
            // The rule this exists for: a 450 km ring off a 19 km body. Sizing separation off the
            // planet's own gravity limit would be roughly fifty times too small.
            WorldRecipe recipe = Recipe(Planet("Cauldron", "RealGasGiant", 19), Planet("Tellus", "EarthLike", 120));
            recipe.Features.Add(new RecipeFeature
            {
                Name = "Cauldron Ring",
                Shape = WorldFeatureShape.Ring,
                AnchorPlanet = "Cauldron",
                InnerRadiusKm = 180,
                OuterRadiusKm = 450,
                HalfThicknessKm = 3.6
            });

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.True(plan.IsApplicable);

            PlannedPlanet giant = plan.Planets.Find(p => p.Name == "Cauldron");
            PlannedPlanet earth = plan.Planets.Find(p => p.Name == "Tellus");
            Assert.Equal(450, giant.ExclusionRadiusKm, 3);
            Assert.True(giant.ExclusionRadiusKm > giant.GravityLimitKm);
            Assert.True(giant.CenterKm.DistanceTo(earth.CenterKm) >= 450 + earth.ExclusionRadiusKm + 100);
        }

        [Fact]
        public void OriginKeepOutIsHonoured()
        {
            WorldRecipe recipe = Recipe(Planet("A", "EarthLike", 120));
            recipe.Volume.OriginKeepOutKm = 5000;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            PlannedPlanet a = plan.Planets[0];
            Assert.True(a.Placed);
            double fromOrigin = a.CenterKm.DistanceTo(new Vec3(0, 0, 0));
            Assert.True(fromOrigin >= 5000 + a.ExclusionRadiusKm);
        }

        [Fact]
        public void BodiesStayInsideTheAuthoredVolume()
        {
            WorldRecipe recipe = Recipe(Planet("A", "EarthLike", 120), Planet("B", "EarthLike", 120));
            recipe.Volume.ExtentKm = 4000;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            foreach (PlannedPlanet p in plan.Planets)
            {
                Assert.True(p.Placed);
                // Shrunk by the exclusion radius, so the body and its well stay inside the extent.
                Assert.InRange(p.CenterKm.X, -4000 + p.ExclusionRadiusKm, 4000 - p.ExclusionRadiusKm);
                Assert.InRange(p.CenterKm.Y, -4000 + p.ExclusionRadiusKm, 4000 - p.ExclusionRadiusKm);
                Assert.InRange(p.CenterKm.Z, -4000 + p.ExclusionRadiusKm, 4000 - p.ExclusionRadiusKm);
            }
        }

        [Fact]
        public void SphereVolumeKeepsBodiesInsideTheBall()
        {
            WorldRecipe recipe = Recipe(Planet("A", "EarthLike", 120), Planet("B", "EarthLike", 120));
            recipe.Volume.Shape = WorldVolumeShape.Sphere;
            recipe.Volume.ExtentKm = 8000;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            foreach (PlannedPlanet p in plan.Planets)
            {
                Assert.True(p.Placed);
                Assert.True(p.CenterKm.DistanceTo(new Vec3(0, 0, 0)) <= 8000 - p.ExclusionRadiusKm + 1e-6);
            }
        }

        [Fact]
        public void AVolumeTooSmallForABodyIsRejectedByName()
        {
            WorldRecipe recipe = Recipe(Planet("Cauldron", "RealGasGiant", 19));
            recipe.Volume.ExtentKm = 10;   // smaller than the body's own gravity limit

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.False(plan.IsApplicable);
            Assert.False(plan.Planets[0].Placed);
            Assert.Contains("smaller than this body's own", plan.Planets[0].Rejection);
        }

        [Fact]
        public void AnUnplaceableBodyNamesTheConstraintItKeptFailing()
        {
            // Two bodies that cannot both fit: the volume has room for one exclusion sphere.
            WorldRecipe recipe = Recipe(Planet("A", "EarthLike", 120), Planet("B", "EarthLike", 120));
            recipe.Volume.ExtentKm = 120;
            recipe.Placement.MaxAttemptsPerPlanet = 50;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.False(plan.IsApplicable);
            PlannedPlanet failed = plan.Planets.Find(p => !p.Placed);
            Assert.NotNull(failed);
            Assert.Contains("clearance from", failed.Rejection);
            Assert.Equal(50, failed.Attempts);
        }

        // ---- clearance from what is already in the sector ---------------------------------------

        [Fact]
        public void ABodyIsNotCreatedOnTopOfAnExistingStation()
        {
            // The genesis world has 64 vanilla economy stations 701-9,676 km out in a sector with no
            // planets. Without this, placement reasons only about other planets and can bury one.
            WorldRecipe recipe = Recipe(Planet("Tellus", "EarthLike", 120));
            recipe.Volume.ExtentKm = 3000;

            var stations = new List<PlacementObstacle>();
            for (int i = 0; i < 40; i++)
                stations.Add(new PlacementObstacle("station " + i, new Vec3(i * 60 - 1200, 0, 0)));

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue(), 0, stations);
            Assert.True(plan.IsApplicable);
            Assert.Equal(40, plan.ObstaclesConsidered);

            PlannedPlanet p = plan.Planets[0];
            foreach (PlacementObstacle s2 in stations)
                Assert.True(p.CenterKm.DistanceTo(s2.PositionKm) >= p.TerrainRadiusKm + recipe.Placement.ObstacleMarginKm);
        }

        [Fact]
        public void ClearanceIsMeasuredFromTerrainNotTheGravityWell()
        {
            // A station inside a gravity well is ordinary -- a station inside the voxel is not.
            // Measuring off the exclusion radius would also forbid a station sitting in the very
            // ring it was authored for.
            WorldRecipe recipe = Recipe(Planet("Tellus", "EarthLike", 120));
            recipe.Volume.ExtentKm = 3000;
            recipe.Placement.ObstacleMarginKm = 5;

            PlannedPlanet reference = WorldPlanner.Plan(recipe, Catalogue()).Planets[0];
            Assert.True(reference.TerrainRadiusKm < reference.GravityLimitKm);

            // One station exactly on the previous run's centre: the body must move, but only by
            // terrain + margin, which is well inside where the gravity limit would have pushed it.
            var one = new List<PlacementObstacle> { new PlacementObstacle("Carson Relay", reference.CenterKm) };
            PlannedPlanet moved = WorldPlanner.Plan(recipe, Catalogue(), 0, one).Planets[0];

            double distance = moved.CenterKm.DistanceTo(reference.CenterKm);
            Assert.True(distance >= moved.TerrainRadiusKm + 5);
        }

        [Fact]
        public void NoObstaclesSuppliedIsReportedRatherThanAssumed()
        {
            // "Checked nothing" and "found nothing" must not read the same in a plan.
            WorldPlan plan = WorldPlanner.Plan(Recipe(Planet("A", "EarthLike", 120)), Catalogue());
            Assert.Equal(0, plan.ObstaclesConsidered);
        }

        [Fact]
        public void AnObstacleThatCannotBeClearedNamesItself()
        {
            WorldRecipe recipe = Recipe(Planet("Tellus", "EarthLike", 120));
            recipe.Volume.ExtentKm = 200;
            recipe.Placement.MaxAttemptsPerPlanet = 40;
            recipe.Placement.ObstacleMarginKm = 10000;   // nothing in this volume can clear it

            var one = new List<PlacementObstacle> { new PlacementObstacle("Corsair's Hold", new Vec3(0, 0, 0)) };
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue(), 0, one);

            Assert.False(plan.IsApplicable);
            Assert.Contains("Corsair's Hold", plan.Planets[0].Rejection);
        }

        [Fact]
        public void AnObstaclesOwnRadiusIsRespected()
        {
            WorldRecipe recipe = Recipe(Planet("Tellus", "EarthLike", 120));
            recipe.Volume.ExtentKm = 5000;
            recipe.Placement.ObstacleMarginKm = 0;

            var zone = new List<PlacementObstacle> { new PlacementObstacle("spawn area", new Vec3(0, 0, 0), 800) };
            PlannedPlanet p = WorldPlanner.Plan(recipe, Catalogue(), 0, zone).Planets[0];

            Assert.True(p.Placed);
            Assert.True(p.CenterKm.DistanceTo(new Vec3(0, 0, 0)) >= p.TerrainRadiusKm + 800);
        }

        // ---- determinism -----------------------------------------------------------------------

        [Fact]
        public void SameRecipeAndSeedProduceTheSameWorld()
        {
            WorldPlan a = WorldPlanner.Plan(Recipe(Planet("A", "EarthLike", 120), Planet("B", "EarthLike", 120)), Catalogue());
            WorldPlan b = WorldPlanner.Plan(Recipe(Planet("A", "EarthLike", 120), Planet("B", "EarthLike", 120)), Catalogue());

            for (int i = 0; i < a.Planets.Count; i++)
            {
                Assert.Equal(a.Planets[i].CenterKm.X, b.Planets[i].CenterKm.X, 9);
                Assert.Equal(a.Planets[i].CenterKm.Y, b.Planets[i].CenterKm.Y, 9);
                Assert.Equal(a.Planets[i].CenterKm.Z, b.Planets[i].CenterKm.Z, 9);
                Assert.Equal(a.Planets[i].Seed, b.Planets[i].Seed);
            }
        }

        [Fact]
        public void ADifferentSeedProducesADifferentWorld()
        {
            WorldRecipe one = Recipe(Planet("A", "EarthLike", 120));
            WorldRecipe two = Recipe(Planet("A", "EarthLike", 120));
            two.WorldSeed = one.WorldSeed + 1;

            WorldPlan a = WorldPlanner.Plan(one, Catalogue());
            WorldPlan b = WorldPlanner.Plan(two, Catalogue());
            Assert.NotEqual(a.Planets[0].CenterKm.X, b.Planets[0].CenterKm.X);
        }

        [Fact]
        public void DerivedSeedsAreStableAcrossProcesses()
        {
            // string.GetHashCode is randomised per process, which would make "the same recipe builds
            // the same world" quietly false. This is the guard on that.
            Assert.Equal(WorldRecipe.StableHash("Tellus"), WorldRecipe.StableHash("Tellus"));
            Assert.NotEqual(WorldRecipe.StableHash("Tellus"), WorldRecipe.StableHash("Agni"));

            // Pinned literals: FNV-1a masked to 31 bits, computed independently rather than
            // captured from this implementation's own output. If these move, every seed-derived
            // world silently becomes a different world.
            Assert.Equal(1130808978, WorldRecipe.StableHash("Tellus"));
            Assert.Equal(1006828, WorldRecipe.StableHash("Agni"));
            Assert.Equal(1037939261, WorldRecipe.StableHash("20260824/Tellus"));
        }

        [Fact]
        public void AnAuthoredSeedWinsOverTheDerivedOne()
        {
            RecipePlanet rp = Planet("Tellus", "EarthLike", 120);
            rp.Seed = 4242;
            WorldPlan plan = WorldPlanner.Plan(Recipe(rp), Catalogue());
            Assert.Equal(4242, plan.Planets[0].Seed);
            Assert.Contains("4242", plan.Planets[0].StorageName);
        }

        // ---- plan-time refusals ----------------------------------------------------------------

        [Fact]
        public void AnUnknownDefinitionIsAPlanTimeError()
        {
            // At apply time SE answers this with a bare null, so it has to fail here instead.
            WorldPlan plan = WorldPlanner.Plan(Recipe(Planet("X", "NoSuchPlanet", 120)), Catalogue());
            Assert.False(plan.IsApplicable);
            Assert.Contains(plan.Errors, e => e.Contains("unknown planet definition"));
        }

        [Fact]
        public void MaxPlanetsCountsDistinctTypesNotBodies()
        {
            WorldRecipe five = Recipe(Planet("A", "EarthLike", 120), Planet("B", "EarthLike", 120),
                                      Planet("C", "EarthLike", 120));
            WorldPlan sameType = WorldPlanner.Plan(five, Catalogue(), 1);
            Assert.Equal(1, sameType.DistinctDefinitionCount);
            Assert.Empty(sameType.Errors);   // three bodies, one type, MaxPlanets 1 -- fine

            WorldRecipe mixed = Recipe(Planet("A", "EarthLike", 120), Planet("B", "RealGasGiant", 19));
            WorldPlan twoTypes = WorldPlanner.Plan(mixed, Catalogue(), 1);
            Assert.Equal(2, twoTypes.DistinctDefinitionCount);
            Assert.Contains(twoTypes.Errors, e => e.Contains("MaxPlanets"));
        }

        [Fact]
        public void OversizedBodyIsRejected()
        {
            WorldPlan plan = WorldPlanner.Plan(Recipe(Planet("Huge", "EarthLike", 9000)), Catalogue());
            Assert.Contains(plan.Errors, e => e.Contains("exceeds SE's cap"));
        }

        [Fact]
        public void DuplicateNamesAreRejectedBecauseFeaturesAnchorByName()
        {
            WorldPlan plan = WorldPlanner.Plan(Recipe(Planet("A", "EarthLike", 120), Planet("A", "EarthLike", 120)), Catalogue());
            Assert.Contains(plan.Errors, e => e.Contains("duplicate planet name"));
        }

        // ---- anchors ---------------------------------------------------------------------------

        [Fact]
        public void AFeatureAnchoredToANonExistentPlanetIsAPlanTimeError()
        {
            // Today this is a boot-time WARN and the ring quietly lands near the world origin.
            WorldRecipe recipe = Recipe(Planet("Tellus", "EarthLike", 120));
            recipe.Features.Add(new RecipeFeature
            {
                Name = "Orphan Ring",
                Shape = WorldFeatureShape.Ring,
                AnchorPlanet = "Cauldron",
                InnerRadiusKm = 180,
                OuterRadiusKm = 450,
                HalfThicknessKm = 3.6
            });

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.False(plan.IsApplicable);
            Assert.Contains(plan.Errors, e => e.Contains("is not a planet in this recipe"));
        }

        [Fact]
        public void AnAnchoredFeatureInheritsItsBodysFinalCentre()
        {
            // So the loader's no-anchor fallback lands on the body rather than at the origin.
            WorldRecipe recipe = Recipe(Planet("Cauldron", "RealGasGiant", 19));
            recipe.Features.Add(new RecipeFeature
            {
                Name = "Cauldron Ring",
                Shape = WorldFeatureShape.Ring,
                AnchorPlanet = "Cauldron",
                Center = new Vec3(0, 0, 0),
                InnerRadiusKm = 180,
                OuterRadiusKm = 450,
                HalfThicknessKm = 3.6
            });

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            PlannedPlanet giant = plan.Planets[0];
            PlannedFeature ring = plan.Features[0];

            Assert.Equal("Cauldron", ring.AnchorPlanetName);
            Assert.Equal(giant.CenterKm.X, ring.CenterKm.X, 9);
            Assert.Equal(giant.CenterKm.Y, ring.CenterKm.Y, 9);
            Assert.Equal(giant.CenterKm.Z, ring.CenterKm.Z, 9);
        }

        [Fact]
        public void AFreeFloatingFeatureKeepsItsLiteralCentre()
        {
            WorldRecipe recipe = Recipe(Planet("Tellus", "EarthLike", 120));
            recipe.Features.Add(new RecipeFeature
            {
                Name = "Belt",
                Shape = WorldFeatureShape.Sphere,
                Center = new Vec3(12000, 0, -4000),
                OuterRadiusKm = 600
            });

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            PlannedFeature belt = plan.Features[0];
            Assert.Null(belt.AnchorPlanetName);
            Assert.Equal(12000, belt.CenterKm.X, 6);
        }

        // ---- landability, carried for stage 3 --------------------------------------------------

        [Fact]
        public void LandabilityFlagsAreCarriedIntoThePlan()
        {
            // The answer to "what stops a Surface node being snapped onto a gas giant's core" is a
            // vanilla flag, read rather than invented.
            WorldPlan plan = WorldPlanner.Plan(
                Recipe(Planet("Cauldron", "RealGasGiant", 19), Planet("Tellus", "EarthLike", 120)), Catalogue());

            Assert.False(plan.Planets.Find(p => p.Name == "Cauldron").AllowSurfaceEconomyStations);
            Assert.True(plan.Planets.Find(p => p.Name == "Cauldron").AllowOrbitalEconomyStations);
            Assert.True(plan.Planets.Find(p => p.Name == "Tellus").AllowSurfaceEconomyStations);
        }

        // ---- the estate block, consumed by the estate-seeding stage -----------------------------

        [Fact]
        public void AnAbsentEstateBlockIsALegitimateRecipe()
        {
            // Planets and features alone are a world. Absent must not mean "seed a default estate".
            WorldPlan plan = WorldPlanner.Plan(Recipe(Planet("A", "EarthLike", 120)), Catalogue());
            Assert.True(plan.IsApplicable);
            Assert.Null(Recipe(Planet("A", "EarthLike", 120)).Estate);
        }

        [Fact]
        public void EstateVolumeFallsBackToTheWorldVolume()
        {
            WorldRecipe recipe = Recipe(Planet("A", "EarthLike", 120));
            recipe.Estate = new WorldEstate();
            Assert.Same(recipe.Volume, recipe.EstateVolume());

            recipe.Estate.Volume = new WorldVolume { ExtentKm = 900 };
            Assert.Equal(900, recipe.EstateVolume().ExtentKm, 6);
        }

        [Fact]
        public void EstateValidationRefusesWithAReason()
        {
            string why;
            var estate = new WorldEstate();
            Assert.True(estate.Validate(out why));
            Assert.Null(why);

            estate.NodesPerFaction = new EstateNodeCount { Min = 10, Max = 3 };
            Assert.False(estate.Validate(out why));
            Assert.Contains("at least Min", why);

            estate.NodesPerFaction = new EstateNodeCount { Min = 0, Max = 5 };
            Assert.False(estate.Validate(out why));
            Assert.Contains("greater than zero", why);
        }

        [Fact]
        public void DuplicateEstateOverridesAreRefused()
        {
            var estate = new WorldEstate();
            estate.Factions.Add(new EstateFactionOverride { Tag = "IMPR", Nodes = 8 });
            estate.Factions.Add(new EstateFactionOverride { Tag = "impr", Nodes = 4 });

            string why;
            Assert.False(estate.Validate(out why));
            Assert.Contains("duplicate", why);
        }

        [Fact]
        public void EstateRoundTripsThroughJson()
        {
            string json = @"{
              ""WorldSeed"": 1,
              ""Volume"": { ""ExtentKm"": 10000 },
              ""Estate"": {
                ""NodesPerFaction"": { ""Min"": 3, ""Max"": 10, ""Distribution"": ""Graduated"" },
                ""Factions"": [ { ""Tag"": ""IMPR"", ""Nodes"": 8 } ]
              }
            }";

            string error;
            WorldRecipe recipe = WorldRecipe.Parse(json, out error);
            Assert.Null(error);
            Assert.NotNull(recipe.Estate);
            Assert.Equal(3, recipe.Estate.NodesPerFaction.Min);
            Assert.Equal(10, recipe.Estate.NodesPerFaction.Max);
            Assert.Equal(EstateDistribution.Graduated, recipe.Estate.NodesPerFaction.Distribution);
            Assert.Single(recipe.Estate.Factions);
            Assert.Equal("IMPR", recipe.Estate.Factions[0].Tag);
            Assert.Equal(8, recipe.Estate.Factions[0].Nodes);

            string why;
            Assert.True(recipe.Estate.Validate(out why));
        }

        // ---- recipe parsing --------------------------------------------------------------------

        [Fact]
        public void ABadRecipeRefusesRatherThanDegrading()
        {
            // Unlike world_features.json, which degrades to blank: half-applying a world is not
            // recoverable, so nothing proceeds on a guess.
            string error;
            Assert.Null(WorldRecipe.Parse("{ not json", out error));
            Assert.Contains("unreadable", error);
            Assert.Null(WorldRecipe.Parse("", out error));
        }

        [Fact]
        public void ARecipeRoundTripsThroughJson()
        {
            string json = @"{
              ""WorldSeed"": 7,
              ""Volume"": { ""Shape"": ""Sphere"", ""ExtentKm"": 20000, ""OriginKeepOutKm"": 500 },
              ""Planets"": [
                { ""Name"": ""Tellus"", ""Definition"": ""EarthLike"", ""DiameterKm"": 120,
                  ""CenterKm"": { ""X"": 1000, ""Y"": 0, ""Z"": 0 } },
                { ""Name"": ""Cauldron"", ""Definition"": ""RealGasGiant"", ""DiameterKm"": 19 }
              ],
              ""Features"": [
                { ""Name"": ""Cauldron Ring"", ""Shape"": ""Ring"", ""AnchorPlanet"": ""Cauldron"",
                  ""InnerRadiusKm"": 180, ""OuterRadiusKm"": 450, ""HalfThicknessKm"": 3.6,
                  ""Materials"": [ { ""SubTypeId"": ""Ice"", ""Share"": 1.0, ""BandDepthM"": 12.0 } ] }
              ]
            }";

            string error;
            WorldRecipe recipe = WorldRecipe.Parse(json, out error);
            Assert.Null(error);
            Assert.Equal(WorldVolumeShape.Sphere, recipe.Volume.Shape);
            Assert.Equal(2, recipe.Planets.Count);
            Assert.True(recipe.Planets[0].CenterKm.HasValue);
            Assert.False(recipe.Planets[1].CenterKm.HasValue);

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.True(plan.IsApplicable);
            Assert.Equal(1000, plan.Planets[0].CenterKm.X, 6);
            Assert.Equal("Cauldron", plan.Features[0].AnchorPlanetName);
            Assert.Single(plan.Features[0].Materials);
        }
    }
}
