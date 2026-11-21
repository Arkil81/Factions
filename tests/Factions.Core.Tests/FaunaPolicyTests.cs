using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FaunaPolicyTests
    {
        private static WorldRecipe Recipe(params RecipePlanet[] planets)
        {
            var r = new WorldRecipe { WorldSeed = 1 };
            r.Planets = new List<RecipePlanet>(planets);
            return r;
        }

        private static RecipePlanet Body(string name, params FaunaRule[] rules)
        {
            return new RecipePlanet { Name = name, Definition = name, DiameterKm = 30,
                                      Fauna = new List<FaunaRule>(rules) };
        }

        private static FaunaRule Rule(params string[] animals)
        {
            return new FaunaRule { Animals = new List<string>(animals) };
        }

        private static FaunaPolicy Build(WorldRecipe r)
        {
            FaunaPolicy p;
            string why;
            Assert.True(FaunaPolicy.TryBuild(r, out p, out why), why);
            return p;
        }

        private static string Rejection(WorldRecipe r)
        {
            FaunaPolicy p;
            string why;
            Assert.False(FaunaPolicy.TryBuild(r, out p, out why));
            Assert.Null(p);
            Assert.False(string.IsNullOrEmpty(why));
            return why;
        }

        private static FaunaPolicy.Entry Get(FaunaPolicy p, string generator)
        {
            FaunaPolicy.Entry e;
            Assert.True(p.TryGetBody(generator, 0, out e));
            return e;
        }

        private static FaunaSite Site(double lat = 0, double alt = 0, bool night = false, double? nodeKm = 100)
        {
            return new FaunaSite { LatitudeDeg = lat, AltitudeM = alt, IsNight = night, NearestNodeKm = nodeKm };
        }

        [Fact]
        public void NoFaunaAuthored_LeavesEveryBodyAlone()
        {
            FaunaPolicy p = Build(Recipe(new RecipePlanet { Name = "Tellus", Definition = "Tellus" }));
            Assert.Equal(0, p.Count);
            FaunaPolicy.Entry e;
            Assert.False(p.TryGetBody("Tellus", 0, out e));
        }

        [Fact]
        public void RecipeJson_ParsesTheDocumentedShape()
        {
            const string json = @"{ ""Planets"": [ { ""Name"": ""Tellus"", ""Definition"": ""Tellus"",
              ""Fauna"": [ { ""Animals"": [""Cow_Bot"", ""Sheep_Bot""], ""When"": ""Day"",
                ""Where"": { ""LatitudeDeg"": [-40, 40], ""AltitudeM"": [0, 600], ""AwayFromNodesKm"": 2 },
                ""DelaySeconds"": [125, 270], ""Wave"": [10, 20], ""DistanceM"": [100, 250] } ] } ] }";
            string error;
            WorldRecipe r = WorldRecipe.Parse(json, out error);
            Assert.NotNull(r);

            FaunaRule rule = Get(Build(r), "Tellus").Rules[0];
            Assert.Equal(FaunaTime.Day, rule.When);
            Assert.Equal(new[] { "Cow_Bot", "Sheep_Bot" }, rule.Animals);
            Assert.Equal(new[] { -40.0, 40.0 }, rule.Where.LatitudeDeg);
            Assert.Equal(2.0, rule.Where.AwayFromNodesKm);
            Assert.Equal(new[] { 10, 20 }, rule.Wave);
        }

        [Fact]
        public void FirstMatchingRuleWins_InAuthoredOrder()
        {
            FaunaRule wolves = Rule("Wolf");
            wolves.Where = new FaunaArea { LatitudeDeg = new[] { 50.0, 90.0 } };
            FaunaRule cows = Rule("Cow_Bot");
            FaunaPolicy.Entry body = Get(Build(Recipe(Body("Tellus", wolves, cows))), "Tellus");

            Assert.Same(wolves, FaunaPolicy.Match(body, Site(lat: 60)));
            Assert.Same(cows, FaunaPolicy.Match(body, Site(lat: 10)));
        }

        [Fact]
        public void NoRuleMatches_ReturnsNull_SoTheNativeListCarriesOn()
        {
            FaunaRule r = Rule("Cow_Bot");
            r.Where = new FaunaArea { AltitudeM = new[] { 0.0, 600.0 } };
            FaunaPolicy.Entry body = Get(Build(Recipe(Body("Tellus", r))), "Tellus");

            Assert.Null(FaunaPolicy.Match(body, Site(alt: 900)));
            Assert.Same(r, FaunaPolicy.Match(body, Site(alt: 600)));
        }

        [Fact]
        public void WhenSeparatesDayFromNight()
        {
            FaunaRule day = Rule("Cow_Bot");   day.When = FaunaTime.Day;
            FaunaRule night = Rule("Wolf");    night.When = FaunaTime.Night;
            FaunaPolicy.Entry body = Get(Build(Recipe(Body("Tellus", day, night))), "Tellus");

            Assert.Same(day, FaunaPolicy.Match(body, Site(night: false)));
            Assert.Same(night, FaunaPolicy.Match(body, Site(night: true)));
            Assert.True(FaunaPolicy.AsksNight(body));
        }

        [Fact]
        public void AwayFromNodes_HoldsOnlyAtOrBeyondTheDistance_AndNeverWhenUnknown()
        {
            FaunaRule r = Rule("Wolf");
            r.Where = new FaunaArea { AwayFromNodesKm = 2 };
            FaunaPolicy.Entry body = Get(Build(Recipe(Body("Tellus", r))), "Tellus");

            Assert.Null(FaunaPolicy.Match(body, Site(nodeKm: 1.5)));
            Assert.Same(r, FaunaPolicy.Match(body, Site(nodeKm: 2.0)));
            Assert.Null(FaunaPolicy.Match(body, Site(nodeKm: null)));
            Assert.True(FaunaPolicy.AsksNodes(body));
            Assert.False(FaunaPolicy.AsksTerrain(body));
        }

        [Fact]
        public void EmptyAnimals_IsAnAuthoredSilence_AndDoesNotCountAsHavingAnimals()
        {
            FaunaRule quiet = Rule();
            FaunaPolicy.Entry body = Get(Build(Recipe(Body("Kor", quiet))), "Kor");

            Assert.Same(quiet, FaunaPolicy.Match(body, Site()));
            Assert.False(FaunaPolicy.HasAnimals(body));

            FaunaPolicy.Entry withPups = Get(Build(Recipe(Body("Kor", Rule("LavaPup"), quiet))), "Kor");
            Assert.True(FaunaPolicy.HasAnimals(withPups));
        }

        [Fact]
        public void TwoBodiesOnOneGenerator_AreSeparatedBySeed()
        {
            RecipePlanet a = Body("A", Rule("Cow_Bot"));  a.Definition = "EarthLike"; a.Seed = 11;
            RecipePlanet b = Body("B", Rule("Wolf"));     b.Definition = "EarthLike"; b.Seed = 22;
            FaunaPolicy p = Build(Recipe(a, b));

            FaunaPolicy.Entry e;
            Assert.True(p.TryGetBody("EarthLike", 22, out e));
            Assert.Equal("B", e.Name);
            Assert.False(p.TryGetBody("EarthLike", 33, out e));
        }

        [Fact]
        public void SameGeneratorAndSeed_IsRejected()
        {
            RecipePlanet a = Body("A", Rule("Cow_Bot"));  a.Definition = "EarthLike"; a.Seed = 11;
            RecipePlanet b = Body("B", Rule("Wolf"));     b.Definition = "EarthLike"; b.Seed = 11;
            Assert.Contains("same seed", Rejection(Recipe(a, b)));
        }

        [Fact]
        public void MissingAnimalsList_IsRejected_NotReadAsSilence()
        {
            Assert.Contains("Animals", Rejection(Recipe(Body("Tellus", new FaunaRule()))));
        }

        [Fact]
        public void MalformedRanges_AreRejected()
        {
            FaunaRule inverted = Rule("Cow_Bot");
            inverted.DelaySeconds = new[] { 270.0, 125.0 };
            Assert.Contains("DelaySeconds", Rejection(Recipe(Body("Tellus", inverted))));

            FaunaRule zeroWave = Rule("Cow_Bot");
            zeroWave.Wave = new[] { 0, 3 };
            Assert.Contains("Wave", Rejection(Recipe(Body("Tellus", zeroWave))));

            FaunaRule pastPole = Rule("Cow_Bot");
            pastPole.Where = new FaunaArea { LatitudeDeg = new[] { -95.0, 40.0 } };
            Assert.Contains("LatitudeDeg", Rejection(Recipe(Body("Tellus", pastPole))));

            FaunaRule oneEnded = Rule("Cow_Bot");
            oneEnded.DistanceM = new[] { 100.0 };
            Assert.Contains("DistanceM", Rejection(Recipe(Body("Tellus", oneEnded))));
        }

        [Fact]
        public void ShippedRecipe_AuthorsTheAssignedBodies()
        {
            string error;
            WorldRecipe r = WorldRecipe.Parse(System.IO.File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")), out error);
            Assert.True(r != null, error);
            FaunaPolicy p = Build(r);
            Assert.Equal(3, p.Count);

            FaunaPolicy.Entry tellus = Get(p, "Tellus");
            Assert.Contains("Cow_Bot", FaunaPolicy.Match(tellus, Site(night: false)).Animals);
            Assert.Equal(new[] { "Wolf" }, FaunaPolicy.Match(tellus, Site(night: true)).Animals);
            Assert.Contains("Stone_Monster", FaunaPolicy.Match(Get(p, "Agni"), Site()).Animals);
            Assert.Contains("LavaPup", FaunaPolicy.Match(Get(p, "Kor"), Site()).Animals);

            FaunaPolicy.Entry ignored;
            Assert.False(p.TryGetBody("RealGasGiant", 0, out ignored));
        }

        /// <summary>SE checks a body's bot cap only before a wave, so a wave spawns in full: the
        /// shipped waves are small, and every rule says so rather than taking SE's default.</summary>
        [Fact]
        public void ShippedRecipe_WavesAreSmall()
        {
            string error;
            WorldRecipe r = WorldRecipe.Parse(System.IO.File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")), out error);
            Assert.True(r != null, error);
            int rules = 0;
            foreach (RecipePlanet planet in r.Planets)
            {
                if (planet.Fauna == null) continue;
                foreach (FaunaRule rule in planet.Fauna)
                {
                    rules++;
                    Assert.True(rule.Wave != null && rule.Wave.Length == 2, planet.Name + ": no Wave authored");
                    Assert.True(rule.Wave[1] <= 6, planet.Name + ": a wave of up to " + rule.Wave[1]);
                }
            }
            Assert.Equal(4, rules);
        }

        [Fact]
        public void ShippedRecipe_TellusWolvesComeOncePerNight()
        {
            string error;
            WorldRecipe r = WorldRecipe.Parse(System.IO.File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")), out error);
            Assert.True(r != null, error);
            FaunaRule wolves = null;
            foreach (RecipePlanet planet in r.Planets)
                if (planet.Name == "Tellus" && planet.Fauna != null)
                    foreach (FaunaRule rule in planet.Fauna)
                        if (rule.Animals.Contains("Wolf")) wolves = rule;
            Assert.NotNull(wolves);
            Assert.True(wolves.OncePerNight);
        }

        [Fact]
        public void FaunaWithoutDefinition_IsRejected()
        {
            RecipePlanet p = Body("Tellus", Rule("Cow_Bot"));
            p.Definition = null;
            Assert.Contains("Definition", Rejection(Recipe(p)));
        }
    }
}
