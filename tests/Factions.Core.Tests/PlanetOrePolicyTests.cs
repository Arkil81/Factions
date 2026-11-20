using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PlanetOrePolicyTests
    {
        private static WorldRecipe Recipe(int worldSeed, params RecipePlanet[] planets)
        {
            var r = new WorldRecipe { WorldSeed = worldSeed };
            r.Planets = new List<RecipePlanet>(planets);
            return r;
        }

        private static RecipeOreMapping Map(byte value, string material, float start = 5f, float depth = 10f)
        {
            return new RecipeOreMapping { Value = value, Material = material, Start = start, Depth = depth };
        }

        private static PlanetOrePolicy Build(WorldRecipe r)
        {
            PlanetOrePolicy p;
            string why;
            Assert.True(PlanetOrePolicy.TryBuild(r, out p, out why), why);
            return p;
        }

        private static string Rejection(WorldRecipe r)
        {
            PlanetOrePolicy p;
            string why;
            Assert.False(PlanetOrePolicy.TryBuild(r, out p, out why));
            Assert.Null(p);
            Assert.False(string.IsNullOrEmpty(why));
            return why;
        }

        [Fact]
        public void NoAuthoredMap_LeavesEveryBodyAlone()
        {
            PlanetOrePolicy p = Build(Recipe(1,
                new RecipePlanet { Name = "Tellus", Definition = "Tellus", DiameterKm = 30 }));

            Assert.Equal(0, p.Count);
            IList<RecipeOreMapping> got;
            Assert.False(p.TryGet("Tellus", 1234, out got));
        }

        [Fact]
        public void AuthoredMap_IsFoundByGenerator()
        {
            PlanetOrePolicy p = Build(Recipe(1, new RecipePlanet
            {
                Name = "Tellus", Definition = "Tellus", DiameterKm = 30,
                OreMappings = new List<RecipeOreMapping> { Map(10, "Cattierite_01"), Map(20, "Kamacite_01") }
            }));

            IList<RecipeOreMapping> got;
            Assert.True(p.TryGet("Tellus", 99, out got));
            Assert.Equal(2, got.Count);
            Assert.Equal("Cattierite_01", got[0].Material);
        }

        /// <summary>The seed is ignored when one entry names the generator. A body created before the
        /// map was authored, or whose seed was edited, must still get its ore rather than silently
        /// keeping vanilla's while the boot line claims a map was loaded.</summary>
        [Fact]
        public void SoleEntryForAGenerator_MatchesAnySeed()
        {
            PlanetOrePolicy p = Build(Recipe(7, new RecipePlanet
            {
                Name = "Kor", Definition = "Kor", DiameterKm = 19, Seed = 4242,
                OreMappings = new List<RecipeOreMapping> { Map(10, "Autunite_01") }
            }));

            IList<RecipeOreMapping> got;
            Assert.True(p.TryGet("Kor", -1, out got));
            Assert.Single(got);
        }

        /// <summary>Two bodies on one generator is the case a generator-only key cannot express, and
        /// the reason the seed is part of the lookup at all.</summary>
        [Fact]
        public void TwoBodiesOnOneGenerator_AreSeparatedBySeed()
        {
            PlanetOrePolicy p = Build(Recipe(1,
                new RecipePlanet
                {
                    Name = "Twin A", Definition = "EarthLike", DiameterKm = 60, Seed = 111,
                    OreMappings = new List<RecipeOreMapping> { Map(10, "Iron_01") }
                },
                new RecipePlanet
                {
                    Name = "Twin B", Definition = "EarthLike", DiameterKm = 60, Seed = 222,
                    OreMappings = new List<RecipeOreMapping> { Map(10, "Kamacite_01") }
                }));

            IList<RecipeOreMapping> a, b, none;
            Assert.True(p.TryGet("EarthLike", 111, out a));
            Assert.True(p.TryGet("EarthLike", 222, out b));
            Assert.Equal("Iron_01", a[0].Material);
            Assert.Equal("Kamacite_01", b[0].Material);

            // A seed neither body carries matches nothing: guessing between two authored maps is
            // worse than leaving the generator's own in place.
            Assert.False(p.TryGet("EarthLike", 333, out none));
        }

        /// <summary>An empty list is an authored statement that the body is barren, not an absence of
        /// opinion. The gas giant's own generator ships exactly this.</summary>
        [Fact]
        public void EmptyList_IsAuthoredBarrenAndNotInheritance()
        {
            PlanetOrePolicy p = Build(Recipe(1, new RecipePlanet
            {
                Name = "Cauldron", Definition = "RealGasGiant", DiameterKm = 19,
                OreMappings = new List<RecipeOreMapping>()
            }));

            Assert.Equal(1, p.Count);
            IList<RecipeOreMapping> got;
            Assert.True(p.TryGet("RealGasGiant", 0, out got));
            Assert.Empty(got);
        }

        [Fact]
        public void TwoEntriesOnOneValue_AreRejected()
        {
            string why = Rejection(Recipe(1, new RecipePlanet
            {
                Name = "Tellus", Definition = "Tellus", DiameterKm = 30,
                OreMappings = new List<RecipeOreMapping> { Map(10, "Iron_01"), Map(10, "Kamacite_01") }
            }));

            Assert.Contains("10", why);
            Assert.Contains("Kamacite_01", why);
        }

        [Fact]
        public void ZeroDepth_IsRejected()
        {
            string why = Rejection(Recipe(1, new RecipePlanet
            {
                Name = "Tellus", Definition = "Tellus", DiameterKm = 30,
                OreMappings = new List<RecipeOreMapping> { Map(10, "Iron_01", depth: 0f) }
            }));

            Assert.Contains("Depth", why);
        }

        [Fact]
        public void NegativeStart_IsRejected()
        {
            string why = Rejection(Recipe(1, new RecipePlanet
            {
                Name = "Tellus", Definition = "Tellus", DiameterKm = 30,
                OreMappings = new List<RecipeOreMapping> { Map(10, "Iron_01", start: -1f) }
            }));

            Assert.Contains("Start", why);
        }

        [Fact]
        public void MissingMaterial_IsRejected()
        {
            string why = Rejection(Recipe(1, new RecipePlanet
            {
                Name = "Tellus", Definition = "Tellus", DiameterKm = 30,
                OreMappings = new List<RecipeOreMapping> { Map(10, null) }
            }));

            Assert.Contains("Material", why);
        }

        [Fact]
        public void MapWithoutADefinition_IsRejected()
        {
            string why = Rejection(Recipe(1, new RecipePlanet
            {
                Name = "Nameless", DiameterKm = 30,
                OreMappings = new List<RecipeOreMapping> { Map(10, "Iron_01") }
            }));

            Assert.Contains("Definition", why);
        }

        /// <summary>Two entries that resolve to the same (generator, seed) leave no way to pick one,
        /// so it is an authoring error rather than a last-wins.</summary>
        [Fact]
        public void TwoBodiesWithTheSameGeneratorAndSeed_AreRejected()
        {
            string why = Rejection(Recipe(1,
                new RecipePlanet
                {
                    Name = "A", Definition = "EarthLike", DiameterKm = 60, Seed = 5,
                    OreMappings = new List<RecipeOreMapping> { Map(10, "Iron_01") }
                },
                new RecipePlanet
                {
                    Name = "B", Definition = "EarthLike", DiameterKm = 60, Seed = 5,
                    OreMappings = new List<RecipeOreMapping> { Map(10, "Kamacite_01") }
                }));

            Assert.Contains("same seed", why);
        }

        /// <summary>Seeds left at 0 are derived from the world seed and the body name, so two
        /// unseeded bodies on one generator still separate rather than colliding.</summary>
        [Fact]
        public void UnseededBodiesOnOneGenerator_DeriveDistinctSeeds()
        {
            PlanetOrePolicy p = Build(Recipe(1,
                new RecipePlanet
                {
                    Name = "A", Definition = "EarthLike", DiameterKm = 60,
                    OreMappings = new List<RecipeOreMapping> { Map(10, "Iron_01") }
                },
                new RecipePlanet
                {
                    Name = "B", Definition = "EarthLike", DiameterKm = 60,
                    OreMappings = new List<RecipeOreMapping> { Map(10, "Kamacite_01") }
                }));

            IList<RecipeOreMapping> a, b;
            Assert.True(p.TryGet("EarthLike", WorldRecipe.ResolveSeed(1, new RecipePlanet { Name = "A" }), out a));
            Assert.True(p.TryGet("EarthLike", WorldRecipe.ResolveSeed(1, new RecipePlanet { Name = "B" }), out b));
            Assert.Equal("Iron_01", a[0].Material);
            Assert.Equal("Kamacite_01", b[0].Material);
        }
    }
}
