using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The faction recipe's shape, and the two mistakes it exists to refuse: a region with no
    /// authored count, and a region the world does not have. FAC-105.
    /// </summary>
    public class FactionRecipeTests
    {
        private static FactionRecipe Sprt()
        {
            var r = new FactionRecipe();
            r.Factions.Add(new FactionEstate
            {
                Tag = "SPRT",
                Regions = new List<EstateRegion>
                {
                    new EstateRegion { Feature = "Cauldron Ring", Nodes = 12 },
                    new EstateRegion { Body = "Agni", Nodes = 3 },
                }
            });
            return r;
        }

        private static WorldRecipe World()
        {
            var w = new WorldRecipe();
            w.Planets.Add(new RecipePlanet { Name = "Agni", Definition = "Agni", DiameterKm = 30 });
            w.Planets.Add(new RecipePlanet { Name = "Kor", Definition = "Kor", DiameterKm = 19 });
            return w;
        }

        [Fact]
        public void AWellFormedRecipeValidates()
        {
            string why;
            Assert.True(Sprt().Validate(out why), why);
            Assert.Null(why);
        }

        [Fact]
        public void ARegionNeedsExactlyOneOfBodyOrFeature()
        {
            string why;
            var both = new FactionRecipe();
            both.Factions.Add(new FactionEstate
            {
                Tag = "SPRT",
                Regions = new List<EstateRegion> { new EstateRegion { Body = "Kor", Feature = "Cauldron Ring", Nodes = 1 } }
            });
            Assert.False(both.Validate(out why));
            Assert.Contains("not both", why);

            var neither = new FactionRecipe();
            neither.Factions.Add(new FactionEstate
            {
                Tag = "SPRT",
                Regions = new List<EstateRegion> { new EstateRegion { Nodes = 1 } }
            });
            Assert.False(neither.Validate(out why));
            Assert.Contains("neither", why);
        }

        [Fact]
        public void ARegionWithoutAnAuthoredCountIsRefused()
        {
            // The whole point of FAC-105's count rule: at 50 km a 200 km feature holds ~307 sites, so
            // "fill the region" silently produces a 300-node faction. Capacity is not a budget.
            var r = new FactionRecipe();
            r.Factions.Add(new FactionEstate
            {
                Tag = "FCTM",
                Regions = new List<EstateRegion> { new EstateRegion { Feature = "Nest Alpha" } }
            });

            string why;
            Assert.False(r.Validate(out why));
            Assert.Contains("Nodes must be greater than zero", why);
            Assert.Contains("not a budget", why);
        }

        [Fact]
        public void AFactionWithNoRegionsIsLegal()
        {
            // UNKN. It is the destination for abandoned nodes and fills over time on its own.
            var r = new FactionRecipe();
            r.Factions.Add(new FactionEstate { Tag = "UNKN" });

            string why;
            Assert.True(r.Validate(out why), why);
            Assert.Equal(0, r.TotalNodes());
        }

        [Fact]
        public void ATagTwiceIsRefusedRatherThanMerged()
        {
            var r = Sprt();
            r.Factions.Add(new FactionEstate { Tag = "sprt" });

            string why;
            Assert.False(r.Validate(out why));
            Assert.Contains("appears twice", why);
        }

        [Fact]
        public void TheSameRegionTwiceIsRefusedRatherThanSummed()
        {
            var r = new FactionRecipe();
            r.Factions.Add(new FactionEstate
            {
                Tag = "MTDW",
                Regions = new List<EstateRegion>
                {
                    new EstateRegion { Body = "Kor", Nodes = 2 },
                    new EstateRegion { Body = "kor", Nodes = 3 },
                }
            });

            string why;
            Assert.False(r.Validate(out why));
            Assert.Contains("twice", why);
        }

        [Fact]
        public void TotalNodesSumsEveryFaction()
        {
            FactionRecipe r = Sprt();
            r.Factions.Add(new FactionEstate
            {
                Tag = "MTDW",
                Regions = new List<EstateRegion> { new EstateRegion { Body = "Kor", Nodes = 5 } }
            });
            Assert.Equal(20, r.TotalNodes());
        }

        // ---- resolution against the world ---------------------------------------------------

        [Fact]
        public void ABodyIsKeyedByGeneratorNotByDisplayName()
        {
            // PlanetRecord.Name is "Planet" for every body at runtime, so the display name can never
            // be the key. A recipe naming one must be refused, not silently skipped.
            var r = new FactionRecipe();
            r.Factions.Add(new FactionEstate
            {
                Tag = "MTDW",
                Regions = new List<EstateRegion> { new EstateRegion { Body = "Planet", Nodes = 1 } }
            });

            string why;
            Assert.False(r.ResolveAgainst(World(), new[] { "Cauldron Ring" }, out why));
            Assert.Contains("generator subtype, not display name", why);
        }

        [Fact]
        public void ARegionTheWorldDoesNotHaveIsRefused()
        {
            string why;
            var r = new FactionRecipe();
            r.Factions.Add(new FactionEstate
            {
                Tag = "FCTM",
                Regions = new List<EstateRegion> { new EstateRegion { Feature = "Nest Alpha", Nodes = 6 } }
            });
            Assert.False(r.ResolveAgainst(World(), new[] { "Cauldron Ring" }, out why));
            Assert.Contains("no feature named 'Nest Alpha'", why);
        }

        [Fact]
        public void ADerivedRingResolvesEvenThoughItIsNotInTheRecipesFeatureList()
        {
            // The ring is derived from the gas giant, so it never appears in WorldRecipe.Features.
            // Resolving against that list alone would reject the one feature this world has.
            WorldRecipe w = World();
            Assert.Empty(w.Features);

            string why;
            Assert.True(Sprt().ResolveAgainst(w, new[] { "Cauldron Ring" }, out why), why);
        }

        [Fact]
        public void TheShippedFactionRecipeParsesAndResolvesAgainstTheLiveWorld()
        {
            const string dir = "instance/worlds/awl-factions-genesis/";
            var recipe = JsonConvert.DeserializeObject<FactionRecipe>(
                File.ReadAllText(TestSupport.RepoFile(dir + "faction_recipe.json")));

            string why;
            Assert.True(recipe.Validate(out why), why);

            string error;
            WorldRecipe world = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile(dir + "world_recipe.json")), out error);
            Assert.Null(error);

            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(
                File.ReadAllText(TestSupport.RepoFile(dir + "world_features.json")), out status);
            var names = new List<string>();
            foreach (WorldFeature f in features) names.Add(f.Name);
            // The world's features are what the recipe AUTHORS plus what the planner DERIVES.
            // world_features.json holds only the second kind for a world whose planets already
            // exist, because 'world plan --features' regenerates it on apply and apply refuses a
            // world that is not planet-blank. Reading the generated file alone therefore reports a
            // freshly authored feature as missing -- which is what the six FCTM nests hit.
            foreach (RecipeFeature rf in world.Features)
                if (!string.IsNullOrEmpty(rf.Name) && !names.Contains(rf.Name)) names.Add(rf.Name);

            Assert.True(recipe.ResolveAgainst(world, names, out why), why);

            // The brief, asserted rather than described: UNKN holds nothing, and every other tag does.
            FactionEstate unkn = recipe.Factions.Find(f => f.Tag == "UNKN");
            Assert.NotNull(unkn);
            Assert.Empty(unkn.Regions);

            foreach (FactionEstate f in recipe.Factions)
                if (f.Tag != "UNKN") Assert.NotEmpty(f.Regions);
        }

        [Fact]
        public void NoBodyIsAuthoredBeyondWhatItCanActuallyHold()
        {
            // The check that was missing. The first version of the shipped estate put 40 nodes on a
            // Tellus that held 38 at 15 km, and nothing would have said so: placement does not refuse an
            // over-subscribed region, it retries a bounded number of times and then sites the node
            // anyway, so the symptom is a silent separation violation (FAC-105).
            //
            // Capacity comes from PlacementCapacity, not from a number in a ticket -- FAC-105's
            // figures predate OnSphere being rewritten to measure the sampler's lattice rather than
            // estimate area, and every one of them is too high.
            const string dir = "instance/worlds/awl-factions-genesis/";
            var recipe = JsonConvert.DeserializeObject<FactionRecipe>(
                File.ReadAllText(TestSupport.RepoFile(dir + "faction_recipe.json")));

            string error;
            WorldRecipe world = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile(dir + "world_recipe.json")), out error);
            Assert.Null(error);

            var authored = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (FactionEstate f in recipe.Factions)
                foreach (EstateRegion r in f.Regions)
                {
                    if (string.IsNullOrEmpty(r.Body)) continue;
                    int n;
                    authored[r.Body] = (authored.TryGetValue(r.Body, out n) ? n : 0) + r.Nodes;
                }

            foreach (RecipePlanet p in world.Planets)
            {
                int want;
                if (p == null || string.IsNullOrEmpty(p.Definition) || !authored.TryGetValue(p.Definition, out want))
                    continue;

                double sepKm = p.NodeSeparationKm.HasValue ? p.NodeSeparationKm.Value : 15.0;
                double capacity = PlacementCapacity.OnSphere(p.DiameterKm / 2.0, sepKm);

                Assert.True(want <= capacity,
                    p.Definition + ": the estate authors " + want + " surface nodes but the body holds "
                    + capacity + " at " + sepKm + " km. Placement will not refuse the extra -- it will "
                    + "site them in violation of the separation.");
            }
        }
    }
}
