using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    public class EstateSeederTests
    {
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        // Geometry and separation are DERIVED FROM THE SHIPPED WORLD FILES, never hand-written.
        // They used to be literals, and the moment world_recipe.json gained six FCTM nests every
        // test built on them was measuring a world that no longer existed. A fixture that restates
        // shipped data is a second copy of it, free to disagree -- the same defect as the two
        // separation field names.
        private static WorldRecipe ShippedWorld()
        {
            string why;
            WorldRecipe w = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")),
                out why);
            Assert.True(w != null, why);
            return w;
        }

        private static FakeCatalogue ShippedPlanets()
        {
            var cat = new FakeCatalogue();
            foreach (RecipePlanet rp in ShippedWorld().Planets)
                cat.Records.Add(new PlanetRecord
                {
                    Id = rp.Definition,
                    Name = rp.Name,
                    Generator = rp.Definition,
                    SurfaceRadius = rp.DiameterKm / 2.0,
                    NodeSeparationKm = rp.NodeSeparationKm,
                    AllowsSurfaceStations = !rp.AllowSurfaceNodes.HasValue || rp.AllowSurfaceNodes.Value,
                });
            return cat;
        }

        /// <summary>Every feature the live server sees: the authored ones in world_recipe.json plus
        /// the Cauldron Ring, which is DERIVED from the gas giant rather than authored and so appears
        /// only in the generated world_features.json.</summary>
        private static List<WorldFeature> ShippedFeatures()
        {
            var all = new List<WorldFeature>();
            foreach (RecipeFeature rf in ShippedWorld().Features)
                all.Add(new WorldFeature
                {
                    Name = rf.Name, Shape = rf.Shape, Center = rf.Center, Normal = rf.Normal,
                    InnerRadiusKm = rf.InnerRadiusKm, OuterRadiusKm = rf.OuterRadiusKm,
                    HalfThicknessKm = rf.HalfThicknessKm, Zone = rf.Zone,
                    NodeSeparationKm = rf.NodeSeparationKm, Density = rf.Density,
                });
            all.AddRange(JsonConvert.DeserializeObject<WorldFeatureFile>(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_features.json")))
                .Features);
            return all;
        }

        private static PlacementRegionGeometry LiveGeometry(PlacementRegion r)
        {
            return EstateSeeder.GeometryFrom(ShippedPlanets(), ShippedFeatures())(r);
        }

        private static double AuthoredSeparation(PlacementRegion r)
        {
            var tuning = new FactionTuning
            {
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
            };
            return EstateSeeder.SeparationFrom(ShippedPlanets(), ShippedFeatures(), tuning)(r);
        }

        /// <summary>How many tags the recipe actually gives territory to. Derived, never a literal:
        /// the count changed the moment FCTM's nests were authored, and a hard-coded 11 would have
        /// reported a correct world as a regression.</summary>
        private static int TagsWithTerritory(FactionRecipe recipe)
        {
            int n = 0;
            foreach (FactionEstate f in recipe.Factions)
                if (f.Regions != null && f.Regions.Count > 0) n++;
            return n;
        }

        private static FactionRecipe ShippedRecipe()
        {
            var recipe = JsonConvert.DeserializeObject<FactionRecipe>(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/faction_recipe.json")));
            string why;
            Assert.True(recipe.Validate(out why), why);
            return recipe;
        }

        private static EstateSeedResult SeedShipped(bool apply, WorldModel world = null,
                                                    ICollection<string> roster = null,
                                                    int orphanStationRecords = 0)
        {
            FactionRecipe recipe = ShippedRecipe();
            // Default roster is the recipe's own tags -- these tests are about geometry, and a
            // mismatched roster would refuse before reaching any of it. The roster check has its own
            // tests below, where the mismatch is the subject rather than a confound.
            if (roster == null)
            {
                var tags = new List<string>();
                foreach (FactionEstate f in recipe.Factions) tags.Add(f.Tag);
                roster = tags;
            }
            return EstateSeeder.Seed(recipe, world, 20260826, apply, LiveGeometry, AuthoredSeparation,
                                     roster, orphanStationRecords);
        }

        // ---- the shipped world must actually be buildable ---------------------------------------

        [Fact]
        public void TheShippedRecipeIsBuildable()
        {
            // The whole authored world, against the authored separations, against what the sampler
            // can really place. This is the check that caught Tellus at 40 against a capacity of 38
            // at 15 km, before Tellus was authored at 14 km (capacity 43).
            EstateSeedResult r = SeedShipped(apply: false);

            Assert.False(r.IsOverSubscribed, string.Join(" | ", r.Warnings));
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void TheShippedRecipeSeedsEveryFactionThatAuthorsTerritory()
        {
            EstateSeedResult r = SeedShipped(apply: false);

            // Twelve tags are authored; UNKN holds no regions by design, so eleven get territory.
            Assert.Equal(TagsWithTerritory(ShippedRecipe()), r.FactionsSeeded);

            // Derived from the recipe, not hardcoded. This assertion was `38 + 19 + 11 + 8` and broke
            // the moment the estate was rebalanced against PlacementCapacity's real ceilings -- which
            // is a legitimate edit to an authored data file, not a regression. The property worth
            // holding is that the seeder places exactly what the recipe asks for; the total is the
            // recipe's business.
            var recipe = JsonConvert.DeserializeObject<FactionRecipe>(File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/faction_recipe.json")));
            Assert.Equal(recipe.TotalNodes(), r.NodesSeeded);
        }

        [Fact]
        public void UnknIsAuthoredEmpty_AndStaysEmpty()
        {
            // UNKN is the abandoned-node graveyard. Authoring it with no regions is deliberate, and
            // seeding must not confuse "no regions" with "some default".
            Assert.DoesNotContain(SeedShipped(apply: false).Lines, l => l.StartsWith("UNKN"));
        }

        // ---- refusal comes before placement -----------------------------------------------------

        [Fact]
        public void AnOverSubscribedRegion_RefusesAndPlacesNothing()
        {
            var recipe = new FactionRecipe();
            for (int i = 0; i < 11; i++)
                recipe.Factions.Add(new FactionEstate
                {
                    Tag = "F" + i.ToString("00"),
                    Regions = new List<EstateRegion> { new EstateRegion { Body = "Kor", Nodes = 1 } },
                });

            var world = TestSupport.World();
            // Roster taken from the recipe so the ONLY thing that can refuse here is capacity --
            // otherwise this test would pass for the wrong reason once the roster check exists.
            var roster = new List<string>();
            foreach (FactionEstate f in recipe.Factions) roster.Add(f.Tag);
            EstateSeedResult r = EstateSeeder.Seed(recipe, world, 1, apply: true, LiveGeometry,
                                                   AuthoredSeparation, roster, 0);

            Assert.True(r.IsOverSubscribed);
            Assert.Equal(0, r.NodesSeeded);
            Assert.Contains(r.Warnings, w => w.Contains("capacity") || w.Contains("holds"));
            Assert.Empty(world.Graph.Nodes);      // nothing placed, not even the part that fit
        }

        // ---- separation holds across factions, not just within one -----------------------------

        [Fact]
        public void FactionsSharingABody_DoNotCrowdEachOther()
        {
            var world = TestSupport.World();
            SeedShipped(apply: false);   // plan first, as the verb does

            // Sample the shared lattice the way the seeder does and check the whole region at once.
            var kor = new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "Kor" };
            List<Vec3> lattice = RegionSampler.OnSphere(LiveGeometry(kor).CentreKm, 9.5, 8,
                RegionSampler.SeedFor(20260826, "", "Kor"));

            double min = double.MaxValue;
            for (int i = 0; i < lattice.Count; i++)
                for (int j = i + 1; j < lattice.Count; j++)
                    min = Math.Min(min, lattice[i].DistanceTo(lattice[j]));

            Assert.True(min >= 10.0, "cross-faction spacing on Kor was " + min);
        }

        // ---- determinism -------------------------------------------------------------------------

        [Fact]
        public void SameSeed_SameEstate()
        {
            EstateSeedResult a = SeedShipped(apply: false);
            EstateSeedResult b = SeedShipped(apply: false);
            Assert.Equal(string.Join("\n", a.Lines), string.Join("\n", b.Lines));
        }

        [Fact]
        public void OneFactionsShare_DoesNotMoveBecauseAnothersCountChanged()
        {
            // Homes are seeded per (world, tag, region). If they were drawn from a shared stream,
            // editing one faction's count would silently relocate every faction after it.
            int before = RegionSampler.SeedFor(20260826, "MTPR", "Tellus");
            int after  = RegionSampler.SeedFor(20260826, "MTPR", "Tellus");
            Assert.Equal(before, after);
            Assert.NotEqual(before, RegionSampler.SeedFor(20260826, "MTTL", "Tellus"));
        }

        // ---- apply actually places --------------------------------------------------------------

        [Fact]
        public void ApplyPlacesNodesOwnedByTheAuthoredFactions()
        {
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShipped(apply: true, world);

            Assert.False(r.IsOverSubscribed, string.Join(" | ", r.Warnings));
            Assert.Equal(r.NodesSeeded, world.Graph.Nodes.Count);
            Assert.Equal(15, world.Graph.OwnedBy("SPRT").Count());   // 10 ring + 3 Agni + 2 Kor
        }

        [Fact]
        public void PlanningPlacesNothing()
        {
            var world = TestSupport.World();
            SeedShipped(apply: false, world);
            Assert.Empty(world.Graph.Nodes);
        }
    
        // ---- the resolvers the host verb runs on -------------------------------------------
        //
        // These stand between an authored region name and real geometry. A test that supplied its
        // own geometry lambda -- as every test above does -- could not fail on a wiring gap here,
        // which is exactly how NodeSeparationKm stayed inert (docs/testing-conventions.md).


        /// <summary>A world whose separation floors are the shipped ones. A bare
        /// <c>new WorldModel()</c> carries a default <see cref="FactionTuning"/> whose separations
        /// are all <b>0</b> — the live values come from <c>tuning_defaults.json</c> — and at 0 every
        /// separation check passes vacuously, so a test built on one cannot fail.</summary>
        private static WorldModel SeparatedWorld()
        {
            var w = new WorldModel();
            w.Tuning = new FactionTuning
            {
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
            };
            return w;
        }


        /// <summary>A world holding <b>every feature the shipped world has</b> — the ring and the six
        /// FCTM nests — so a point in any of them is claimed.
        /// <para>It used to hold only the ring, and the moment FCTM's nests were authored the nest
        /// regions silently yielded nothing: geometry resolved (that comes from the shipped files)
        /// but the claim predicate rejected every point, because the world under test did not contain
        /// the feature the recipe was placing into. Build the test world from the same files the
        /// server reads, or it drifts exactly like a hand-written fixture.</para></summary>
        private static WorldModel RingWorld()
        {
            WorldModel w = TestSupport.World();
            w.Planets = ShippedPlanets();
            w.Features = new WorldFeaturePlacement(ShippedFeatures(), w.Planets);
            return w;
        }

        private sealed class WorldFeatureFile { public List<WorldFeature> Features; }

        private sealed class FakeCatalogue : IPlanetCatalogue
        {
            public readonly List<PlanetRecord> Records = new List<PlanetRecord>();
            public IEnumerable<PlanetRecord> Planets { get { return Records; } }

            // The resolvers read the record list and nothing else -- these three exist to satisfy
            // the interface, and any test that needed them would be testing the wrong thing here.
            public double GravityAt(Vec3 posKm) { return 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        [Fact]
        public void GeometryFrom_MatchesAPlanetOnItsGeneratorSubtype()
        {
            var cat = new FakeCatalogue();
            cat.Records.Add(new PlanetRecord
            {
                Id = "1", Name = "Tellus", Generator = "EarthLike",
                Center = new Vec3(100, 200, 300), SurfaceRadius = 30,
                AllowsSurfaceStations = true,
            });

            PlacementRegionGeometry g = EstateSeeder.GeometryFrom(cat, null)(
                new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "EarthLike" });

            Assert.NotNull(g);
            Assert.Equal(30, g.SurfaceRadiusKm);
            Assert.Equal(200, g.CentreKm.Y);          // the centre, not the origin
            Assert.True(g.AllowsSurface);

            // Matched on the generator, not the display name -- the recipe authors the generator.
            Assert.Null(EstateSeeder.GeometryFrom(cat, null)(
                new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "Tellus" }));
        }

        [Fact]
        public void GeometryFrom_KeepsPlanetAndFeatureKeySpacesApart()
        {
            var cat = new FakeCatalogue();
            cat.Records.Add(new PlanetRecord { Id = "1", Generator = "Kor", SurfaceRadius = 19 });
            var features = new List<WorldFeature>
            {
                new WorldFeature { Name = "Kor", InnerRadiusKm = 5, OuterRadiusKm = 9, HalfThicknessKm = 1 },
            };

            Func<PlacementRegion, PlacementRegionGeometry> geo = EstateSeeder.GeometryFrom(cat, features);

            // Same key, two kinds, two answers -- a body called Kor never resolves to a ring called Kor.
            Assert.Equal(19, geo(new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "Kor" }).SurfaceRadiusKm);
            Assert.Equal(9, geo(new PlacementRegion { Kind = PlacementRegionKind.Feature, Key = "Kor" }).OuterRadiusKm);
        }

        [Fact]
        public void GeometryFrom_ReturnsNullForARegionThisWorldDoesNotHave()
        {
            // Null, not a zero-capacity region: the planner reports "no such region" as a refusal,
            // and a silently empty region would read as "authored, and nothing fits".
            Assert.Null(EstateSeeder.GeometryFrom(new FakeCatalogue(), new List<WorldFeature>())(
                new PlacementRegion { Kind = PlacementRegionKind.Feature, Key = "Nowhere" }));
        }

        [Fact]
        public void SeparationFrom_PrefersTheAuthoredValueOverTheZoneFloor()
        {
            var tuning = new FactionTuning();
            var cat = new FakeCatalogue();
            cat.Records.Add(new PlanetRecord { Id = "1", Generator = "Kor", NodeSeparationKm = 10 });
            cat.Records.Add(new PlanetRecord { Id = "2", Generator = "Bare" });   // authors nothing
            var features = new List<WorldFeature>
            {
                new WorldFeature { Name = "Ring", NodeSeparationKm = 50 },
                new WorldFeature { Name = "Plain", Zone = NodeZone.DeepSpace },
            };

            Func<PlacementRegion, double> sep = EstateSeeder.SeparationFrom(cat, features, tuning);

            Assert.Equal(10, sep(new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "Kor" }));
            Assert.Equal(50, sep(new PlacementRegion { Kind = PlacementRegionKind.Feature, Key = "Ring" }));

            // Unauthored falls back to the zone floor, and the two zones differ -- so this is a real
            // assertion about which floor was reached, not just "some number came back".
            Assert.Equal(tuning.NodeSeparationFor(NodeZone.Surface),
                sep(new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = "Bare" }));
            Assert.Equal(tuning.NodeSeparationFor(NodeZone.DeepSpace),
                sep(new PlacementRegion { Kind = PlacementRegionKind.Feature, Key = "Plain" }));
        }

        [Fact]
        public void TheShippedRecipeFitsTheShippedWorld_ThroughTheResolvers()
        {
            // The boot warning's exact question, asked the way boot asks it: resolvers built from
            // the shipped world files, not from geometry this test invented. If this goes red the
            // server logs "will place NOTHING" on its next start.
            FactionRecipe recipe = ShippedRecipe();
            FakeCatalogue cat = ShippedPlanets();
            List<WorldFeature> features = ShippedFeatures();
            Assert.NotEmpty(features);

            PlacementPlan plan = PlacementPlanner.Plan(EstateSeeder.ToAssignments(recipe),
                EstateSeeder.GeometryFrom(cat, features),
                EstateSeeder.SeparationFrom(cat, features, new FactionTuning
                {
                    NodeSeparationSurfaceKm = 15.0, NodeSeparationOrbitKm = 30.0,
                    NodeSeparationDeepSpaceKm = 500.0,
                }));

            Assert.True(plan.IsFeasible, string.Join(" | ", plan.Refusals));
        }

        // ---- projected separation, the number the survey layer reads ------------------------

        [Fact]
        public void AProjectedViolationIsCounted_AndAnUnprojectedOneWouldNotBe()
        {
            // Positive control first: the measure must be able to return non-zero, or its zero on
            // the shipped world proves nothing. Two points 1 km apart on a body separated at 10 km.
            WorldModel world = SeparatedWorld();
            var pts = new List<Vec3> { new Vec3(1000, 0, 0), new Vec3(1001, 0, 0) };

            double spacing, required;
            int v = world.ProjectedSeparationViolations(pts, out spacing, out required);

            Assert.Equal(2, v);                 // per position, not per pair -- both are in violation
            Assert.Equal(1.0, spacing, 3);
            Assert.True(required > spacing, "required " + required + " must exceed the 1 km spacing");
        }

        [Fact]
        public void AHealthyMarginIsNotReportedAsABreach()
        {
            // The live dry run reported "closest pair 13.11 km against 10 km required" beside a
            // violation count -- two different nodes' numbers in one sentence, reading as a breach
            // when 13.11 > 10 is a pass. The reported pair must be the OFFENDING one, and when
            // nothing offends there is no requirement to quote.
            WorldModel world = SeparatedWorld();
            // 400 km apart in deep space: comfortably inside the 500 km floor is impossible here,
            // so use two surface-adjacent points far enough apart to satisfy every floor.
            var pts = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(900, 0, 0) };

            double spacing, required;
            int v = world.ProjectedSeparationViolations(pts, out spacing, out required);

            Assert.Equal(0, v);
            Assert.Equal(900.0, spacing, 3);
            Assert.Equal(0.0, required);        // nothing was breached, so nothing is quoted
        }

        [Fact]
        public void ProjectedViolations_CountAgainstNodesAlreadyInTheGraph()
        {
            // A seeded node is checked against existing territory nowhere else -- if this stops
            // counting, seeding onto an inhabited world goes silently wrong.
            WorldModel world = SeparatedWorld();
            world.SeedNodeAt("AAA", new Vec3(2000, 0, 0));

            double spacing, required;
            int v = world.ProjectedSeparationViolations(
                new List<Vec3> { new Vec3(2000.5, 0, 0) }, out spacing, out required);

            Assert.Equal(1, v);
            Assert.True(spacing < 1.0, "spacing was " + spacing);
        }

        [Fact]
        public void SeedNodeAt_LandsWhereProjectSeedPositionSaidItWould()
        {
            // The two must not drift: the dry run measures ProjectSeedPosition and the apply commits
            // SeedNodeAt. If these ever disagree the dry run's spacing describes a different world.
            WorldModel world = SeparatedWorld();
            var asked = new Vec3(1234.5, -67.8, 90.1);

            Vec3 projected = world.ProjectSeedPosition(asked);
            SpatialNode committed = world.SeedNodeAt("AAA", asked);

            Assert.Equal(projected.X, committed.Position.X, 9);
            Assert.Equal(projected.Y, committed.Position.Y, 9);
            Assert.Equal(projected.Z, committed.Position.Z, 9);
        }

        [Fact]
        public void TheShippedEstateIsMeasuredForSpacingOnADryRun()
        {
            // A dry run must produce the number, not just an apply -- that is the whole point of
            // reporting it before committing.
            WorldModel world = SeparatedWorld();
            EstateSeedResult r = SeedShipped(apply: false, world: world);

            Assert.Empty(world.Graph.Nodes);            // still a dry run
            Assert.True(r.MinSpacingKm > 0, "a dry run measured no spacing at all");
        }

        [Fact]
        public void TheAnnulusSamplerHonoursTheRegionsClaim()
        {
            // The Cauldron Ring's bands do not tile it: the outermost ends at 453.93 km inside a
            // 465 km outer radius. A point in that sliver is disowned by the feature, inherits the
            // deep-space floor and reads as wildly under-separated -- one of nineteen ring nodes
            // did exactly that live, at r=459.94.
            var centre = new Vec3(0, 0, 0);
            Func<Vec3, bool> claims = v =>
            {
                double radial = Math.Sqrt(v.X * v.X + v.Z * v.Z);
                return radial <= 453.93;
            };

            List<Vec3> unfiltered = RegionSampler.InAnnulus(centre, 195, 465, 1.8, 19, 4242);
            List<Vec3> filtered = RegionSampler.InAnnulus(centre, 195, 465, 1.8, 19, 4242, claims);

            // Positive control: the unfiltered draw really does escape, or the filter proves nothing.
            Assert.Contains(unfiltered, v => !claims(v));

            Assert.Equal(19, filtered.Count);
            Assert.All(filtered, v => Assert.True(claims(v),
                "a kept point sits outside the claim at radial "
                + Math.Sqrt(v.X * v.X + v.Z * v.Z).ToString("0.##")));
        }

        [Fact]
        public void AnUnsatisfiableClaimReturnsShortRatherThanHanging()
        {
            // The escalating draw must terminate. A claim nothing satisfies is a recipe error, and
            // it has to surface as SHORT rather than spin a boot.
            List<Vec3> none = RegionSampler.InAnnulus(new Vec3(0, 0, 0), 195, 465, 1.8, 5, 7,
                                                      v => false);
            Assert.Empty(none);
        }

        // ---- Site, the field a seeded node must not arrive without ---------------------------

        private sealed class OnePlanetCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord _p = new PlanetRecord
            {
                Id = "T", Name = "Tellus", Generator = "Tellus",
                Center = new Vec3(0, 0, 0), SurfaceRadius = 30, GravityRadius = 300,
                AllowsSurfaceStations = true,
            };
            public IEnumerable<PlanetRecord> Planets { get { yield return _p; } }
            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_p.Center);
                if (d <= _p.SurfaceRadius) return 1.0;
                if (d >= _p.GravityRadius) return 0.0;
                return 1.0 - (d - _p.SurfaceRadius) / (_p.GravityRadius - _p.SurfaceRadius);
            }
            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_p.Center);
                if (d < 1e-9) return new Vec3(_p.SurfaceRadius, 0, 0);
                double k = _p.SurfaceRadius / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        [Fact]
        public void ASeededNodeArrivesWithItsSiteStamped()
        {
            // StationSite.Space is the zero value, so an unstamped node is indistinguishable from a
            // deep-space one -- and reads that way to ShipPool.HasShipyardOnPlanet, planet-scoped
            // InvestmentTargeting and the respawn tiers until the next boot reclassifies it. A
            // brand-new surface estate would look like no planet-side presence at all.
            WorldModel world = SeparatedWorld();
            world.Planets = new OnePlanetCatalogue();

            SpatialNode surface = world.SeedNodeAt("AAA", new Vec3(30, 0, 0));      // on the ground
            SpatialNode orbital = world.SeedNodeAt("AAA", new Vec3(0, 0, 150));     // in the well
            SpatialNode deep    = world.SeedNodeAt("AAA", new Vec3(0, 0, 5000));    // outside it

            Assert.Equal(StationSite.Surface, surface.Site);

            // The other two are the controls: if Classify were never called, ALL THREE would read
            // Space and the Surface assertion alone could not tell that apart from a correct answer.
            Assert.Equal(StationSite.Orbital, orbital.Site);
            Assert.Equal(StationSite.Space, deep.Site);
        }

        [Fact]
        public void SeededAndExpandedNodesAgreeOnSite()
        {
            // Both routes go through CommitNode, which is the point of having one. If a second
            // creation path ever appears, this is what notices.
            WorldModel world = SeparatedWorld();
            world.Planets = new OnePlanetCatalogue();

            SpatialNode seeded = world.SeedNodeAt("AAA", new Vec3(30, 0, 0));
            SpatialNode grown = world.ExpandFrom("AAA", seeded.Id);

            Assert.NotNull(grown);
            Assert.Equal(StationSiting.Classify(world.Planets, grown.Position), grown.Site);
        }

        // ---- the roster, which nothing checked until a live apply nearly ran ------------------

        [Fact]
        public void ARecipeNamingAFactionThisWorldLacksIsRefused_AndPlacesNothing()
        {
            // Live 2026-08-26: faction_recipe.json was authored against a previous world's factions.
            // Only 3 of 12 tags existed; 45 of 65 nodes would have been placed for owners that do not
            // exist, and every stage would have reported success -- the planner validates regions,
            // not tags.
            var world = new WorldModel();
            EstateSeedResult r = SeedShipped(apply: true, world: world,
                                             roster: new List<string> { "SPRT", "UNKN" });

            Assert.True(r.IsOverSubscribed);
            Assert.Empty(world.Graph.Nodes);            // refused as a whole, as over-subscription is
            Assert.Contains(r.Warnings, w => w.Contains("this world does not have"));
        }

        [Fact]
        public void ANullRosterRefusesRatherThanSkippingTheCheck()
        {
            // "Caller supplied nothing" and "caller supplied a matching roster" must not look the
            // same. A check that silently passes when unwired is the failure mode this whole guard
            // exists to prevent.
            var world = new WorldModel();
            EstateSeedResult r = EstateSeeder.Seed(ShippedRecipe(), world, 1, true,
                                                   LiveGeometry, AuthoredSeparation, null, 0);

            Assert.True(r.IsOverSubscribed);
            Assert.Empty(world.Graph.Nodes);
            Assert.Contains(r.Warnings, w => w.Contains("no faction roster"));
        }

        [Fact]
        public void ARegisteredFactionWithNoEstateIsReportedButNotRefused()
        {
            // UNKN is a graveyard by design and FCTM waits on unauthored features. Deliberately
            // landless and forgotten look identical unless something says so.
            FactionRecipe recipe = ShippedRecipe();
            var roster = new List<string>();
            foreach (FactionEstate f in recipe.Factions) roster.Add(f.Tag);
            roster.Add("ZZZZ");

            EstateSeedResult r = EstateSeeder.Seed(recipe, null, 1, false,
                                                   LiveGeometry, AuthoredSeparation, roster, 0);

            Assert.False(r.IsOverSubscribed);
            Assert.Contains(r.Lines, l => l.Contains("no estate authored for") && l.Contains("ZZZZ"));
        }

        [Fact]
        public void AnApplyDoesNotReportEveryNodeAsCollidingWithItself()
        {
            // The first real apply printed eight "0 km from its nearest neighbour" violations that
            // did not exist: the committed nodes were in the graph by the time the measure ran, so
            // each placed position found its own twin. The dry run of the identical placement was
            // clean, which is how the artefact was spotted. An apply and a dry run must agree.
            EstateSeedResult dry = SeedShipped(apply: false, world: RingWorld());
            EstateSeedResult applied = SeedShipped(apply: true, world: RingWorld());

            Assert.Equal(dry.SeparationViolations, applied.SeparationViolations);
            Assert.Equal(dry.NodesSeeded, applied.NodesSeeded);
            Assert.DoesNotContain(applied.Warnings, w => w.Contains("0 km from"));
        }

        // ---- the genesis bootstrap ------------------------------------------------------------

        [Fact]
        public void FoundedNodesAreNamedOnApplyAndNotOnADryRun()
        {
            // A node has no id until it is committed, so a dry run has nothing to name. If this ever
            // returns ids on a dry run they are fabricated, and the host would try to found them.
            Assert.Empty(SeedShipped(apply: false, world: RingWorld()).FoundNodeIds);

            EstateSeedResult r = SeedShipped(apply: true, world: RingWorld());
            int wanted = 0;
            foreach (FactionEstate f in ShippedRecipe().Factions) wanted += f.Founded;
            Assert.True(wanted > 0, "the shipped recipe authors no bootstrap stations");
            Assert.Equal(wanted, r.FoundNodeIds.Count);
        }

        [Fact]
        public void FoundedNodesComeFromTheRegionsInAuthoringOrder()
        {
            // A faction's power base is authored first and must be founded first -- founding its
            // forward presence while its home stays empty is the wrong world.
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShipped(apply: true, world: world);

            FactionEstate sprt = ShippedRecipe().Factions.Find(f => f.Tag == "SPRT");
            Assert.True(sprt.Founded > 0);
            string firstRegion = sprt.Regions[0].Feature ?? sprt.Regions[0].Body;

            var sprtFounded = new List<SpatialNode>();
            foreach (string id in r.FoundNodeIds)
            {
                SpatialNode n = world.Graph.Nodes[id];
                if (n.OwnerFactionTag == "SPRT") sprtFounded.Add(n);
            }
            Assert.Equal(sprt.Founded, sprtFounded.Count);

            // The Cauldron Ring is SPRT's first region and holds 10 of its 15 nodes, so every one of
            // its 5 bootstrap stations must be in the ring, not on Agni or Kor.
            Assert.Equal("Cauldron Ring", firstRegion);
            foreach (SpatialNode n in sprtFounded)
                Assert.True(world.Features.TrySeparationAt(n.Position, out double sep),
                            "a SPRT bootstrap node landed outside the ring at " + n.Position.X);
        }

        [Fact]
        public void SeedingTwiceIsRefused_RatherThanDoublingTheWorld()
        {
            // SeedNodeAt does not dedupe and the placement is deterministic, so a second apply
            // commits a second copy of every node. The world would silently double.
            WorldModel world = RingWorld();
            EstateSeedResult first = SeedShipped(apply: true, world: world);
            int after = world.Graph.Nodes.Count;
            Assert.Equal(first.NodesSeeded, after);

            EstateSeedResult second = SeedShipped(apply: true, world: world);

            Assert.True(second.IsOverSubscribed);
            Assert.Equal(0, second.NodesSeeded);
            Assert.Equal(after, world.Graph.Nodes.Count);          // nothing was added
            Assert.Contains(second.Warnings, w => w.Contains("already hold territory"));
        }
        // ---- orphan station records from a previous generation (FAC-129) ------------------------

        [Fact]
        public void OrphanStationRecords_RefuseTheApplyAndPlaceNothing()
        {
            // The mechanism behind FAC-129: the territory guard reads the graph, and a station
            // record outlives it. Clear the graph, re-roll, and the last generation's records are
            // stranded while a fresh one is minted on top -- 68 orphans against 56 live nodes.
            WorldModel world = RingWorld();

            EstateSeedResult r = SeedShipped(apply: true, world: world, orphanStationRecords: 68);

            Assert.True(r.IsOverSubscribed);
            Assert.Equal(0, r.NodesSeeded);
            Assert.Empty(world.Graph.Nodes);     // refused before placement, not after
            Assert.Contains(r.Warnings, w => w.Contains("68 orphan station record(s)"));
            Assert.Contains(r.Warnings, w => w.Contains("world purge orphans apply"));
        }

        [Fact]
        public void OrphanStationRecords_AreSaidOutLoudOnADryRunWithoutRefusingIt()
        {
            // A dry run that looked clean and an apply that then refused is how this is learned at
            // the worst moment. The plan still comes back -- the dry run's job is to describe the
            // world it would build -- but it carries the refusal it is heading for.
            WorldModel world = RingWorld();

            EstateSeedResult r = SeedShipped(apply: false, world: world, orphanStationRecords: 68);

            Assert.False(r.IsOverSubscribed);
            Assert.True(r.NodesSeeded > 0);      // the plan survived the warning
            Assert.Contains(r.Warnings, w => w.Contains("68 orphan station record(s)")
                                          && w.Contains("An apply will REFUSE"));
        }

        [Fact]
        public void NoOrphanRecords_IsNotWarnedAbout()
        {
            // The guard must be silent on the healthy path, or the shipped-recipe tests that assert
            // an empty warning list would be asserting on this one instead.
            EstateSeedResult r = SeedShipped(apply: true, world: RingWorld(), orphanStationRecords: 0);

            Assert.False(r.IsOverSubscribed);
            Assert.DoesNotContain(r.Warnings, w => w.Contains("orphan station record"));
        }

        [Fact]
        public void TheOrphanGuardIsReachedEvenThoughAnotherRefusalIsAlsoPending()
        {
            // Both refusals set the same flag, so IsOverSubscribed alone cannot say which fired.
            // The roster here is deliberately WRONG as well -- so the check can fail: if the orphan
            // guard sat after the roster check, this returns the roster wording and nothing else.
            WorldModel world = RingWorld();

            EstateSeedResult r = SeedShipped(apply: true, world: world,
                                             roster: new List<string> { "ZZZZ" },
                                             orphanStationRecords: 3);

            Assert.True(r.IsOverSubscribed);
            Assert.Contains(r.Warnings, w => w.Contains("3 orphan station record(s)"));
            Assert.DoesNotContain(r.Warnings, w => w.Contains("this world does not have"));
            Assert.Empty(world.Graph.Nodes);
        }

        // ---- starting hulls (FactionEstate.Ships) ---------------------------------------------
        //
        // The fleet half of the bootstrap Founded is the station half of. What matters here is not
        // that hulls appear -- it is that a hull never appears somewhere it cannot leave, since
        // stranded stock re-blocks the very dispatch that asked for it.

        /// <summary>A catalogue with two Haul hulls: a cheap one that cannot lift itself out of any
        /// gravity (down-thrust it does not have) and an expensive one that can.</summary>
        private sealed class FakeShipCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, List<string>> RolePool =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, long> Costs = new Dictionary<string, long>();
            public readonly Dictionary<string, double> DryMass = new Dictionary<string, double>();
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> Thrust =
                new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();

            public long CargoLiters(string prefabId) { return 1000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { long v; return Costs.TryGetValue(prefabId, out v) ? v : 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Large"; }
            public double DrillYieldScore(string prefabId) { return 0; }
            public double DryMassKg(string prefabId) { double v; return DryMass.TryGetValue(prefabId, out v) ? v : 0; }

            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                List<string> pool;
                return RolePool.TryGetValue(role ?? "", out pool) ? pool : new List<string>();
            }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> b;
                return Thrust.TryGetValue(prefabId, out b)
                    ? b : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            /// <summary>Ion down-thrust of <paramref name="forceN"/> against <paramref name="massKg"/>.
            /// Ion keeps full effectiveness in vacuum and falls to 20% in atmosphere, matching the
            /// real band shape, so a hull sized for deep space genuinely fails on a surface.</summary>
            /// <summary>A hull whose thrust does not vary with atmosphere, so its TWR is exactly
            /// force / (mass x g x 9.81) and a test can state both ends of a gravity change.</summary>
            public void AddFlatHull(string role, string id, long cost, double massKg, float forceN)
            {
                AddHull(role, id, cost, massKg, forceN);
                foreach (var kv in Thrust[id])
                {
                    var band = kv.Value[0];
                    band.EffectivenessAtMaxInfluence = 1f;
                    Thrust[id][kv.Key] = new List<ThrustBand> { band };
                }
            }

            public void AddHull(string role, string id, long cost, double massKg, float forceN)
            {
                List<string> pool;
                if (!RolePool.TryGetValue(role, out pool)) RolePool[role] = pool = new List<string>();
                pool.Add(id);
                Costs[id] = cost;
                DryMass[id] = massKg;
                Thrust[id] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand>
                    {
                        new ThrustBand
                        {
                            ThrusterType = "Ion", ForceMagnitudeN = forceN,
                            MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                            EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 0.2f,
                            NeedsAtmosphereForInfluence = false,
                        },
                    },
                };
            }
        }

        /// <summary>Uniform gravity everywhere, so a parked hull's TWR is decidable without needing
        /// a node to land on a particular body.</summary>
        private sealed class UniformGravity : IPlanetCatalogue
        {
            private readonly double _g;
            private readonly FakeCatalogue _inner;
            public UniformGravity(double g, FakeCatalogue inner) { _g = g; _inner = inner; }
            public IEnumerable<PlanetRecord> Planets { get { return _inner.Planets; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>One faction, one ring region, everything founded — the smallest world in which a
        /// ship grant is a meaningful statement.</summary>
        private static FactionRecipe ShipRecipe(int nodes, int founded, params EstateShips[] ships)
        {
            var recipe = new FactionRecipe();
            recipe.Factions.Add(new FactionEstate
            {
                Tag = "SPRT",
                Founded = founded,
                Regions = new List<EstateRegion> { new EstateRegion { Feature = "Cauldron Ring", Nodes = nodes } },
                Ships = new List<EstateShips>(ships),
            });
            return recipe;
        }

        private static EstateSeedResult SeedShips(FactionRecipe recipe, WorldModel world, bool apply = true)
        {
            return EstateSeeder.Seed(recipe, world, 20260826, apply, LiveGeometry, AuthoredSeparation,
                                     new List<string> { "SPRT" }, 0);
        }

        private static WorldModel ShipWorld(FakeShipCatalogue cat, double gravityG = 0)
        {
            WorldModel w = RingWorld();
            w.PrefabCatalogue = cat;
            w.Tuning.MinDryTwr = 1.0;
            if (gravityG > 0) w.Planets = new UniformGravity(gravityG, ShippedPlanets());
            return w;
        }

        [Fact]
        public void EveryGrantedHullSitsOnANodeThatIsAlsoBeingFounded()
        {
            var cat = new FakeShipCatalogue();
            cat.AddHull("Haul", "Mule", 1000, 100000, 5000000f);
            WorldModel world = ShipWorld(cat);

            EstateSeedResult r = SeedShips(ShipRecipe(4, 2, new EstateShips { Role = "Haul", Count = 3 }), world);

            Assert.False(r.IsOverSubscribed, string.Join(" | ", r.Warnings));
            Assert.Equal(3, r.ShipGrants.Count);
            // The whole point: a hull at an unfounded node has no station and never dispatches.
            foreach (ShipGrant g in r.ShipGrants)
                Assert.Contains(g.NodeId, r.FoundNodeIds);
            // Round-robin, so three hulls over two stations is 2/1 rather than 3/0.
            Assert.Equal(2, r.ShipGrants.Select(g => g.NodeId).Distinct().Count());
        }

        [Fact]
        public void AHullThatCannotHoldAltitudeWhereItParksIsNotGranted()
        {
            // The only Haul hull is far too heavy for its thrust at 1 g. Granting it would author
            // exactly the stranded stock ShipPool.IsRouteFeasible exists to prevent.
            var cat = new FakeShipCatalogue();
            cat.AddHull("Haul", "Brick", 1000, 1000000, 1000f);
            WorldModel world = ShipWorld(cat, gravityG: 1.0);

            EstateSeedResult r = SeedShips(ShipRecipe(2, 2, new EstateShips { Role = "Haul", Count = 2 }), world);

            Assert.Empty(r.ShipGrants);
            Assert.Contains(r.Warnings, w => w.Contains("no hull can hold altitude"));
            // Not a refusal: the estate is real and already placed, only the hull is withheld.
            Assert.False(r.IsOverSubscribed);
            Assert.NotEmpty(world.Graph.Nodes);
        }

        [Fact]
        public void TheSameCatalogueGrantsTheHullWhenGravityAllowsIt()
        {
            // The negative control for the test above: same recipe, same hull, no gravity. Without
            // this, "no grants" could mean the ship path never ran at all.
            var cat = new FakeShipCatalogue();
            cat.AddHull("Haul", "Brick", 1000, 1000000, 1000f);
            WorldModel world = ShipWorld(cat);

            EstateSeedResult r = SeedShips(ShipRecipe(2, 2, new EstateShips { Role = "Haul", Count = 2 }), world);

            Assert.Equal(2, r.ShipGrants.Count);
            Assert.DoesNotContain(r.Warnings, w => w.Contains("no hull can hold altitude"));
        }

        [Fact]
        public void TheCheapestRouteFeasibleHullWins()
        {
            // Cheapest, not best: a starting fleet should read as what the faction would have built
            // first. The cheapest hull overall is infeasible here, so the ranking has to survive the
            // route filter rather than run before it.
            var cat = new FakeShipCatalogue();
            cat.AddHull("Haul", "TooHeavy", 10, 1000000, 1000f);
            cat.AddHull("Haul", "Cheap", 500, 100000, 5000000f);
            cat.AddHull("Haul", "Dear", 9000, 100000, 5000000f);
            WorldModel world = ShipWorld(cat, gravityG: 1.0);

            EstateSeedResult r = SeedShips(ShipRecipe(2, 1, new EstateShips { Role = "Haul", Count = 1 }), world);

            Assert.Single(r.ShipGrants);
            Assert.Equal("Cheap", r.ShipGrants[0].PrefabId);
        }

        [Fact]
        public void ShipsWithNowhereToParkAreRefusedBeforeAnythingIsPlaced()
        {
            var cat = new FakeShipCatalogue();
            cat.AddHull("Haul", "Mule", 1000, 100000, 5000000f);
            WorldModel world = ShipWorld(cat);

            EstateSeedResult r = SeedShips(ShipRecipe(4, 0, new EstateShips { Role = "Haul", Count = 2 }), world);

            Assert.True(r.IsOverSubscribed);
            Assert.Contains(r.Warnings, w => w.Contains("Founded=0"));
            // Refused BEFORE placement, like every other recipe fault -- not after, which would
            // leave a half-seeded world behind for a file that could have been read first.
            Assert.Empty(world.Graph.Nodes);
        }

        [Fact]
        public void ARoleNoPrefabInThisWorldCarriesIsRefusedBeforeAnythingIsPlaced()
        {
            var cat = new FakeShipCatalogue();
            cat.AddHull("Haul", "Mule", 1000, 100000, 5000000f);   // ...but nothing carries Mine
            WorldModel world = ShipWorld(cat);

            EstateSeedResult r = SeedShips(ShipRecipe(4, 2, new EstateShips { Role = "Mine", Count = 1 }), world);

            Assert.True(r.IsOverSubscribed);
            Assert.Contains(r.Warnings, w => w.Contains("no prefab in this world's catalogue carries that role"));
            Assert.Empty(world.Graph.Nodes);
        }

        [Fact]
        public void ADryRunPlansTheSameHullsButCommitsNothing()
        {
            var cat = new FakeShipCatalogue();
            cat.AddHull("Haul", "Mule", 1000, 100000, 5000000f);
            WorldModel world = ShipWorld(cat);

            EstateSeedResult r = SeedShips(ShipRecipe(4, 2, new EstateShips { Role = "Haul", Count = 3 }),
                                          world, apply: false);

            // The selection is still made and reported, so a dry run describes the plan an apply
            // would commit rather than a different, quieter one.
            Assert.Equal(3, r.ShipGrants.Count);
            Assert.All(r.ShipGrants, g => Assert.Null(g.NodeId));   // no node has an id yet
            Assert.Contains(r.Lines, l => l.Contains("SPRT ships=3"));
            Assert.Empty(world.Graph.Nodes);
        }

        [Fact]
        public void ARecipeWithNoCatalogueStillSeedsItsNodes()
        {
            // Every unit test that does not care about ships runs without a catalogue. Refusing
            // there would make the recipe unusable in exactly the setting it is easiest to test in.
            WorldModel world = RingWorld();

            EstateSeedResult r = SeedShips(ShipRecipe(4, 2, new EstateShips { Role = "Haul", Count = 3 }), world);

            Assert.False(r.IsOverSubscribed, string.Join(" | ", r.Warnings));
            Assert.Empty(r.ShipGrants);
            Assert.Equal(4, world.Graph.Nodes.Count);
        }

        // ---- FAC-224: the position a hull is judged at must be the one it ends up at -------------

        /// <summary>One planet, and gravity that DEPENDS ON POSITION. Every other planet fake in this
        /// file answers the same number everywhere, which is exactly why none of them could catch
        /// FAC-224: a bug about evaluating the wrong position is invisible to a field where every
        /// position is the same.</summary>
        private sealed class GradientGravity : IPlanetCatalogue
        {
            public const double SurfaceRadiusKm = 100.0;
            public const double LatticeRadiusKm = 50.0;    // inside the body, so ClassifyZone says Surface
            public const double WeakG = 1.0;               // where the lattice sits
            public const double StrongG = 1.8;             // where placement puts the node

            private readonly List<PlanetRecord> _records = new List<PlanetRecord>
            {
                new PlanetRecord
                {
                    Id = "1", Name = "Grad", Generator = "Grad", Center = new Vec3(0, 0, 0),
                    SurfaceRadius = SurfaceRadiusKm, GravityRadius = 400.0,
                    AllowsSurfaceStations = true,
                },
            };

            public IEnumerable<PlanetRecord> Planets { get { return _records; } }

            public double GravityAt(Vec3 posKm)
            {
                return posKm.DistanceTo(new Vec3(0, 0, 0)) <= SurfaceRadiusKm * 0.75 ? WeakG : StrongG;
            }

            /// <summary>Lifts a buried point out to the surface — the real thing
            /// <c>WorldModel.PlaceNode</c> does for a Surface node, and a move this fake can measure.</summary>
            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double r = posKm.DistanceTo(new Vec3(0, 0, 0));
                if (r <= 1e-9) return new Vec3(SurfaceRadiusKm, 0, 0);
                double k = SurfaceRadiusKm / r;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        [Fact]
        public void AHullIsJudgedAtThePositionItsNodeEndsUpAt()
        {
            // FAC-224, found on the live re-seed: a hull was granted at 1.04 TWR against a 1.05
            // floor. WorldModel.SeedNodeAt commits at ProjectSeedPosition -- for a surface node that
            // lifts it onto terrain, for an orbital one it pulls it radially to OrbitGravityMin -- so
            // the lattice point and the node sit at different gravities BY DESIGN. Judging the hull
            // where the lattice put it is judging it somewhere it will never be.
            //
            // Here the hull clears the floor at the lattice and misses it at the node, so the two
            // readings give opposite answers and only one of them is the world.
            var cat = new FakeShipCatalogue();
            cat.AddFlatHull("Haul", "Marginal", 1000, 100000, 1471500f);   // 1.50 TWR at 1 g, 0.83 at 1.8 g

            var planets = new GradientGravity();
            WorldModel world = TestSupport.World();
            world.Planets = planets;
            world.PrefabCatalogue = cat;
            world.Tuning.MinDryTwr = 1.05;

            var recipe = new FactionRecipe();
            recipe.Factions.Add(new FactionEstate
            {
                Tag = "SPRT",
                Founded = 2,
                Regions = new List<EstateRegion> { new EstateRegion { Body = "Grad", Nodes = 2 } },
                Ships = new List<EstateShips> { new EstateShips { Role = "Haul", Count = 2 } },
            });

            Func<PlacementRegion, PlacementRegionGeometry> geom = _ => new PlacementRegionGeometry
            {
                CentreKm = new Vec3(0, 0, 0),
                SurfaceRadiusKm = GradientGravity.LatticeRadiusKm,
                AllowsSurface = true,
            };

            EstateSeedResult r = EstateSeeder.Seed(recipe, world, 20260901, true, geom, _ => 5.0,
                                                  new List<string> { "SPRT" }, 0);

            // The setup has to be able to fail three different ways, so each is checked.
            Assert.False(r.IsOverSubscribed, string.Join(" | ", r.Warnings));
            Assert.NotEmpty(world.Graph.Nodes);
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                Assert.True(planets.GravityAt(n.Position) == GradientGravity.StrongG,
                    "placement left a node at " + planets.GravityAt(n.Position)
                    + " g, so it never moved and this test could not fail");
            var atLattice = ShipPool.RouteEnvironment.From(planets,
                new Vec3(GradientGravity.LatticeRadiusKm, 0, 0),
                new Vec3(GradientGravity.LatticeRadiusKm, 0, 0), 1.05);
            Assert.True(ShipPool.IsRouteFeasible(cat, "Marginal", atLattice),
                "the hull must be feasible where the LATTICE put it, or this test proves nothing");

            // The behaviour: refused, and said out loud with the node and its gravity.
            Assert.Empty(r.ShipGrants);
            Assert.Contains(r.Warnings, w => w.Contains("no hull can hold altitude"));
        }

        // ---- the shipped recipe's own fleet ------------------------------------------------------

        [Fact]
        public void TheShippedRecipeAuthorsHullsForEveryFactionThatHoldsAStation()
        {
            // A faction with stations and no fleet cannot Haul, Mine, Trade or defend, which is the
            // state the previous world sat in for six days. UNKN is the deliberate exception: no
            // territory, no stations, nothing to park.
            FactionRecipe recipe = ShippedRecipe();

            foreach (FactionEstate f in recipe.Factions)
            {
                if (f.Founded <= 0) { Assert.Empty(f.Ships ?? new List<EstateShips>()); continue; }
                Assert.True(f.Ships != null && f.Ships.Count > 0,
                    f.Tag + " founds " + f.Founded + " station(s) but authors no hulls.");
            }
        }

        [Fact]
        public void EveryAuthoredHullHasAStationToParkAt()
        {
            // The seeder refuses ships authored against Founded=0 rather than placing them nowhere.
            // Asserting it here means the shipped file is checked without needing a world at all.
            FactionRecipe recipe = ShippedRecipe();

            foreach (FactionEstate f in recipe.Factions)
            {
                int hulls = 0;
                foreach (EstateShips s in f.Ships ?? new List<EstateShips>()) hulls += s.Count;
                if (hulls == 0) continue;
                Assert.True(f.Founded > 0, f.Tag + " authors " + hulls + " hull(s) and founds nothing.");
            }
        }

        [Fact]
        public void TheShippedRecipeAuthorsOneRolePerPool()
        {
            // ShipPool.IsSuitableFor resolves Trade/Gift/Haul to one cargo predicate and
            // Raid/Retaliate/Conquer/Guard/Patrol to one combat predicate. Authoring two roles from
            // one pool reads as two intents and delivers more of the same hull -- so the file uses
            // exactly one name per pool, and this is what keeps it that way.
            var cargo  = new HashSet<string>(new[] { "Trade", "Gift", "Haul", "Ferry" }, StringComparer.OrdinalIgnoreCase);
            var combat = new HashSet<string>(new[] { "Raid", "Patrol", "Guard" }, StringComparer.OrdinalIgnoreCase);

            foreach (FactionEstate f in ShippedRecipe().Factions)
            {
                int cargoRoles = 0, combatRoles = 0;
                foreach (EstateShips s in f.Ships ?? new List<EstateShips>())
                {
                    if (cargo.Contains(s.Role))  cargoRoles++;
                    if (combat.Contains(s.Role)) combatRoles++;
                }
                Assert.True(cargoRoles <= 1, f.Tag + " authors " + cargoRoles + " cargo roles; they share one pool.");
                Assert.True(combatRoles <= 1, f.Tag + " authors " + combatRoles + " combat roles; they share one pool.");
            }
        }

        [Fact]
        public void TheFactorumAuthorsNoMiner()
        {
            // Two independent reasons, and the test exists because either alone would be a reason to
            // change it back: FCTM does not eat (FAC-103), and no Factorum hull carries a drill, so a
            // Mine entry would fall through to the universal pool and hand it an off-livery generic
            // miner. Measured on the live catalogue 2026-09-01: 0 of its 8 ships have a drill.
            foreach (FactionEstate f in ShippedRecipe().Factions)
            {
                if (!string.Equals(f.Tag, "FCTM", StringComparison.Ordinal)) continue;
                Assert.DoesNotContain(f.Ships, s => string.Equals(s.Role, "Mine", StringComparison.OrdinalIgnoreCase));
                Assert.NotEmpty(f.Ships);   // ...but it is not shipless, which is the other failure
                return;
            }
            Assert.Fail("FCTM is not in the shipped recipe.");
        }

        // ---- starting capabilities (FactionEstate.Capabilities) ---------------------------------

        private static FactionRecipe CapRecipe(int nodes, int founded, params EstateCapability[] caps)
        {
            var recipe = new FactionRecipe();
            recipe.Factions.Add(new FactionEstate
            {
                Tag = "SPRT",
                Founded = founded,
                Regions = new List<EstateRegion> { new EstateRegion { Feature = "Cauldron Ring", Nodes = nodes } },
                Capabilities = new List<EstateCapability>(caps),
            });
            return recipe;
        }

        private static EstateCapability Cap(string kind, int count, int level = 0)
        {
            return new EstateCapability { Kind = kind, Count = count, Level = level };
        }

        /// <summary>Ring nodes FIRST, then a Tellus node. Regions are walked in authoring order, so
        /// the ring nodes take the low ids and the on-body node takes the highest — which is what
        /// lets a test tell "chosen because it fits" from "chosen because it sorted first".</summary>
        private static FactionRecipe MixedRegionCapRecipe(params EstateCapability[] caps)
        {
            var recipe = new FactionRecipe();
            recipe.Factions.Add(new FactionEstate
            {
                Tag = "SPRT",
                Founded = 3,
                Regions = new List<EstateRegion>
                {
                    new EstateRegion { Feature = "Cauldron Ring", Nodes = 2 },
                    new EstateRegion { Body = "Tellus", Nodes = 1 },
                },
                Capabilities = new List<EstateCapability>(caps),
            });
            return recipe;
        }

        /// <summary>The founded node with the highest id — the one an id-order tie-break would pick
        /// LAST, and on <see cref="MixedRegionCapRecipe"/> the one standing on a body.</summary>
        private static string HighestId(EstateSeedResult r)
        {
            var ids = new List<string>(r.FoundNodeIds);
            ids.Sort(StringComparer.Ordinal);
            return ids[ids.Count - 1];
        }

        [Fact]
        public void EachAuthoredCapabilityIsInstalledOnAFoundedNode()
        {
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShips(CapRecipe(4, 3, Cap("Refinery", 2), Cap("SensorArray", 1)), world);

            Assert.False(r.IsOverSubscribed, string.Join(" | ", r.Warnings));
            Assert.Equal(3, r.CapabilityGrants.Count);
            foreach (CapabilityGrant g in r.CapabilityGrants)
            {
                Assert.Contains(g.NodeId, r.FoundNodeIds);
                SpatialNode node = world.Graph.Nodes[g.NodeId];
                int level;
                Assert.True(node.Capabilities.TryGetValue(g.Kind, out level) && level >= 1,
                    g.Kind + " was reported on " + g.NodeId + " but is not installed there.");
            }
        }

        [Fact]
        public void ACapabilityIsInstalledAtTheAuthoredLevel()
        {
            WorldModel world = RingWorld();
            world.Tuning.MaxRefineryUpgrades = 15;
            EstateSeedResult r = SeedShips(CapRecipe(4, 2, Cap("Refinery", 1, 4)), world);

            CapabilityGrant g = Assert.Single(r.CapabilityGrants);
            Assert.Equal(4, g.Level);
            Assert.Equal(4, world.Graph.Nodes[g.NodeId].Capabilities[NodeCapabilityKind.Refinery]);
        }

        [Fact]
        public void ALevelAboveTheCapabilitysCeilingIsClampedAndSaidOutLoud()
        {
            // WorldModel.Develop does NOT clamp -- its contract puts the ceiling on the caller, and
            // the ceilings are uneven: Refinery climbs to MaxRefineryUpgrades while a Shipyard,
            // FuelDepot or SensorArray stops at 1. Asking for level 5 of a level-1 capability is an
            // authoring mistake worth naming rather than silently honouring.
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShips(CapRecipe(4, 2, Cap("SensorArray", 1, 5)), world);

            CapabilityGrant g = Assert.Single(r.CapabilityGrants);
            Assert.Equal(1, g.Level);
            Assert.Equal(1, world.Graph.Nodes[g.NodeId].Capabilities[NodeCapabilityKind.SensorArray]);
            Assert.Contains(r.Warnings, w => w.Contains("exceeds this capability's ceiling"));
        }

        [Fact]
        public void CapabilitiesAreInstalledInPrerequisiteOrderWhateverOrderTheyAreAuthoredIn()
        {
            // The authoring order is deliberately backwards. CapabilityPrerequisites gates Shipyard
            // on Manufactory and Manufactory on Refinery, so installing as written would install a
            // Shipyard into an estate with no industry at all.
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShips(
                CapRecipe(4, 3, Cap("Shipyard", 1), Cap("Manufactory", 1), Cap("Refinery", 1)), world);

            Assert.Equal(3, r.CapabilityGrants.Count);
            int refinery = r.CapabilityGrants.FindIndex(g => g.Kind == NodeCapabilityKind.Refinery);
            int manufactory = r.CapabilityGrants.FindIndex(g => g.Kind == NodeCapabilityKind.Manufactory);
            int shipyard = r.CapabilityGrants.FindIndex(g => g.Kind == NodeCapabilityKind.Shipyard);
            Assert.True(refinery < manufactory, "Refinery must be installed before Manufactory");
            Assert.True(manufactory < shipyard, "Manufactory must be installed before Shipyard");
        }

        [Fact]
        public void WhereACapabilityLandsRespondsToWhatIsAlreadyInstalled()
        {
            // The property that makes prerequisite order worth having beyond satisfying the gate:
            // NodeContent.CapabilityAffinityFor scores a Shipyard by HasIndustry, which is true only
            // once something that refines or assembles already stands on the node. So the set is not
            // placed against a frozen world -- each install changes what the next one should choose.
            //
            // Tested through AVOIDANCE rather than attraction, and that is the whole point. A
            // shipyard that SEEKS industry lands on the node the refinery took, which is also the
            // node a plain id tie-break would pick -- so "same node" proves nothing. A shipyard that
            // AVOIDS industry has to land somewhere else, and only a real reading of HasIndustry can
            // put it there.
            WorldModel world = RingWorld();
            world.Tuning.CapabilityAffinity = new Dictionary<string, Dictionary<string, double>>
            {
                ["Refinery"]    = new Dictionary<string, double> { ["Default"] = 0.5 },
                ["Manufactory"] = new Dictionary<string, double> { ["Default"] = 0.5 },
                ["Shipyard"]    = new Dictionary<string, double> { ["HasIndustry"] = 0.1, ["Default"] = 0.9 },
            };

            // Authored BACKWARDS: installed as written, the Shipyard would go first, find no
            // industry anywhere, score every node 0.9 and take the first by id -- which is exactly
            // the node the refinery then lands on.
            EstateSeedResult r = SeedShips(
                CapRecipe(4, 3, Cap("Shipyard", 1), Cap("Manufactory", 1), Cap("Refinery", 1)), world);

            CapabilityGrant refinery = r.CapabilityGrants.Find(g => g.Kind == NodeCapabilityKind.Refinery);
            CapabilityGrant shipyard = r.CapabilityGrants.Find(g => g.Kind == NodeCapabilityKind.Shipyard);

            Assert.NotEqual(refinery.NodeId, shipyard.NodeId);
            Assert.Equal(0.9, shipyard.Affinity);   // the Default row, i.e. a node with no industry

            // And the setup could have failed: the node the shipyard passed over really does carry
            // industry by then, so HasIndustry had something to read.
            Assert.True(NodeContent.CapabilityAffinityFor(
                world.Graph.Nodes[refinery.NodeId], NodeCapabilityKind.Shipyard, world.Tuning) == 0.1,
                "the refinery's node did not read as HasIndustry, so this test proves nothing");
        }

        [Fact]
        public void TheBestNodeIsChosenEvenWhenItIsNotTheFirstById()
        {
            // Affinity has to beat the id tie-break, or "best node" means nothing.
            //
            // The discriminator is HasIndustry rather than OnBody, and that is not arbitrary: in this
            // world EVERY founded node has PlanetId set, ring nodes included, because a ring node
            // sits inside the gas giant's gravity well. OnBody therefore separates nothing here.
            WorldModel world = RingWorld();
            world.Tuning.CapabilityAffinity = new Dictionary<string, Dictionary<string, double>>
            {
                ["Refinery"]  = new Dictionary<string, double> { ["Default"] = 0.5 },
                ["FuelDepot"] = new Dictionary<string, double> { ["HasIndustry"] = 0.1, ["Default"] = 0.9 },
            };

            EstateSeedResult r = SeedShips(CapRecipe(4, 3, Cap("Refinery", 1), Cap("FuelDepot", 1)), world);

            CapabilityGrant refinery = r.CapabilityGrants.Find(g => g.Kind == NodeCapabilityKind.Refinery);
            CapabilityGrant depot = r.CapabilityGrants.Find(g => g.Kind == NodeCapabilityKind.FuelDepot);

            var byId = new List<string>(r.FoundNodeIds);
            byId.Sort(StringComparer.Ordinal);
            Assert.Equal(byId[0], refinery.NodeId);      // ties, so it takes the first by id
            Assert.NotEqual(byId[0], depot.NodeId);      // and the depot does not, because it cannot
            Assert.Equal(0.9, depot.Affinity);
        }

        [Fact]
        public void IndependentCapabilitiesKeepTheOrderTheyWereAuthoredIn()
        {
            // The prerequisite sort must be STABLE. Its first cut walked the list backwards, which
            // reversed every pair that had nothing to do with each other -- a recipe listing Refinery
            // then FuelDepot installed the depot first. Invisible until a capability's affinity reads
            // what is already installed, and then it silently decides placement.
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShips(
                CapRecipe(4, 3, Cap("Refinery", 1), Cap("FuelDepot", 1), Cap("SensorArray", 1)), world);

            Assert.Equal(3, r.CapabilityGrants.Count);
            Assert.Equal(NodeCapabilityKind.Refinery,    r.CapabilityGrants[0].Kind);
            Assert.Equal(NodeCapabilityKind.FuelDepot,   r.CapabilityGrants[1].Kind);
            Assert.Equal(NodeCapabilityKind.SensorArray, r.CapabilityGrants[2].Kind);
        }

        [Fact]
        public void ADryRunInstallsNothingButStillReportsTheTotal()
        {
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShips(CapRecipe(4, 3, Cap("Refinery", 2)), world, apply: false);

            Assert.Empty(r.CapabilityGrants);
            Assert.Empty(world.Graph.Nodes);
            Assert.Contains(r.Lines, l => l.Contains("SPRT capabilities=2"));
        }

        [Fact]
        public void MoreNodesAskedForThanAreFoundedIsWarnedRatherThanRefused()
        {
            WorldModel world = RingWorld();
            EstateSeedResult r = SeedShips(CapRecipe(4, 2, Cap("Refinery", 5)), world);

            Assert.False(r.IsOverSubscribed);
            Assert.Equal(2, r.CapabilityGrants.Count);   // every founded node, and no more
            Assert.Contains(r.Warnings, w => w.Contains("only 2 are founded"));
        }

        [Fact]
        public void ACapabilityAuthoredWithoutItsPrerequisiteIsRefused()
        {
            // The gate is estate-wide, so the question the recipe can answer alone is whether the
            // faction authors the prerequisite at all.
            string why;
            Assert.False(CapRecipe(4, 2, Cap("Shipyard", 1), Cap("Refinery", 1)).Validate(out why));
            Assert.Contains("but not Manufactory", why);
        }

        [Fact]
        public void AnUnknownCapabilityIsRefused()
        {
            string why;
            Assert.False(CapRecipe(4, 2, Cap("Foundry", 1)).Validate(out why));
            Assert.Contains("is not a capability", why);
        }

        [Fact]
        public void AZeroCapabilityCountIsRefused()
        {
            string why;
            Assert.False(CapRecipe(4, 2, Cap("Refinery", 0)).Validate(out why));
            Assert.Contains("greater than zero", why);
        }

        [Fact]
        public void ACapabilityAuthoredTwiceIsRefused()
        {
            string why;
            Assert.False(CapRecipe(4, 2, Cap("Refinery", 1), Cap("refinery", 2)).Validate(out why));
            Assert.Contains("twice", why);
        }

        // ---- recipe validation ------------------------------------------------------------------

        [Fact]
        public void AnUnknownShipRoleIsRefused()
        {
            string why;
            Assert.False(ShipRecipe(2, 1, new EstateShips { Role = "Conquer", Count = 1 }).Validate(out why));
            Assert.Contains("is not a fleet role", why);
        }

        [Fact]
        public void AZeroShipCountIsRefused()
        {
            string why;
            Assert.False(ShipRecipe(2, 1, new EstateShips { Role = "Haul", Count = 0 }).Validate(out why));
            Assert.Contains("greater than zero", why);
        }

        [Fact]
        public void AShipRoleAuthoredTwiceIsRefused()
        {
            string why;
            Assert.False(ShipRecipe(2, 1,
                new EstateShips { Role = "Haul", Count = 1 },
                new EstateShips { Role = "haul", Count = 2 }).Validate(out why));
            Assert.Contains("twice", why);
        }
}
}
