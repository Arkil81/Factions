using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    public class RelationPlannerTests
    {
        private static readonly string[] Roster = { "SPRT", "IGSL", "IMPR", "UNKN" };

        private static FactionRecipe With(RelationRecipe relations)
        {
            return new FactionRecipe { Relations = relations };
        }

        private static int RepOf(RelationPlan plan, string a, string b)
        {
            foreach (RelationPlanEntry e in plan.Entries)
                if ((e.TagA == a && e.TagB == b) || (e.TagA == b && e.TagB == a)) return e.Reputation;
            Assert.True(false, "no entry for " + a + "-" + b);
            return 0;
        }

        [Fact]
        public void EveryPairIsPlannedNotOnlyTheAuthoredOnes()
        {
            // The whole point of authoring: an unnamed pair must be stated, not left holding
            // whatever MyFactionRelationGenerator last computed from a station layout that is gone.
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "IGSL", new Dictionary<string, string> { { "IMPR", "Friends" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.False(plan.IsRefused);
            Assert.Equal(6, plan.Entries.Count);              // C(4,2)
            Assert.Equal(1000, RepOf(plan, "IGSL", "IMPR"));  // FriendlyMid, not the extreme
            Assert.Equal(0, RepOf(plan, "SPRT", "UNKN"));     // default, and it is present
        }

        [Fact]
        public void AWildcardReachesEveryPairTheFactionIsNotNamedFor()
        {
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "SPRT", new Dictionary<string, string> { { "*", "Enemies" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.False(plan.IsRefused);
            Assert.Equal(-1000, RepOf(plan, "SPRT", "IGSL"));
            Assert.Equal(-1000, RepOf(plan, "SPRT", "IMPR"));
            Assert.Equal(-1000, RepOf(plan, "SPRT", "UNKN"));
            Assert.Equal(0, RepOf(plan, "IGSL", "IMPR"));     // untouched by SPRT's wildcard
        }

        [Fact]
        public void AnExplicitPairBeatsAWildcard()
        {
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "SPRT", new Dictionary<string, string> { { "*", "Enemies" }, { "IGSL", "Friends" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.False(plan.IsRefused);
            Assert.Equal(1000, RepOf(plan, "SPRT", "IGSL"));
            Assert.Equal(-1000, RepOf(plan, "SPRT", "IMPR"));
        }

        [Fact]
        public void TwoWildcardsThatDisagreeAboutAPairRefuseRatherThanPickOne()
        {
            // Both authors said "everyone" and they disagree here. Precedence would resolve it, and
            // resolving it silently would present a guess as a decision.
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "SPRT", new Dictionary<string, string> { { "*", "Enemies" } } },
                        { "IGSL", new Dictionary<string, string> { { "*", "Friends" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.True(plan.IsRefused);
            Assert.Contains(plan.Warnings, w => w.Contains("IGSL-SPRT") && w.Contains("explicitly"));
        }

        [Fact]
        public void TwoWildcardsThatAgreeAreFine()
        {
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "SPRT", new Dictionary<string, string> { { "*", "Enemies" } } },
                        { "IMPR", new Dictionary<string, string> { { "*", "Enemies" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.False(plan.IsRefused);
            Assert.Equal(-1000, RepOf(plan, "SPRT", "IMPR"));
        }

        [Fact]
        public void OnePairAuthoredBothWaysWithDifferentStancesIsRefused()
        {
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "IGSL", new Dictionary<string, string> { { "IMPR", "Friends" } } },
                        { "IMPR", new Dictionary<string, string> { { "IGSL", "Enemies" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.True(plan.IsRefused);
        }

        [Fact]
        public void OnePairAuthoredBothWaysWithTheSameStanceIsRedundantNotWrong()
        {
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "IGSL", new Dictionary<string, string> { { "IMPR", "Friends" } } },
                        { "IMPR", new Dictionary<string, string> { { "IGSL", "friends" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.False(plan.IsRefused);
            Assert.Equal(1000, RepOf(plan, "IGSL", "IMPR"));
        }

        [Fact]
        public void ATagThisWorldDoesNotHaveIsRefusedNotSkipped()
        {
            // The EstateSeeder lesson: a silent skip is how 45 of 65 nodes were placed for owners
            // that did not exist, reported as a clean apply.
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "MTPR", new Dictionary<string, string> { { "IGSL", "Enemies" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.True(plan.IsRefused);
            Assert.Contains(plan.Warnings, w => w.Contains("MTPR"));
        }

        [Fact]
        public void ANullRosterRefusesRatherThanPlanningNothing()
        {
            // "Caller supplied nothing" and "caller supplied a roster with no factions in it" must
            // not both produce an empty, un-refused plan.
            RelationPlan plan = RelationPlanner.Build(With(new RelationRecipe()), null, ReputationScale.Vanilla);
            Assert.True(plan.IsRefused);
            Assert.Empty(plan.Entries);
        }

        [Fact]
        public void TheDefaultStanceAppliesToEveryUnnamedPair()
        {
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe { Default = "HostileMin" }), Roster, ReputationScale.Vanilla);

            Assert.False(plan.IsRefused);
            foreach (RelationPlanEntry e in plan.Entries) Assert.Equal(-1500, e.Reputation);
        }

        [Fact]
        public void NoRelationsBlockAtAllPlansEveryPairAtTheNeutralMark()
        {
            RelationPlan plan = RelationPlanner.Build(new FactionRecipe(), Roster, ReputationScale.Vanilla);

            Assert.False(plan.IsRefused);
            Assert.Equal(6, plan.Entries.Count);
            foreach (RelationPlanEntry e in plan.Entries) Assert.Equal(0, e.Reputation);
        }

        [Fact]
        public void TheWorldsOwnScaleIsUsedNotVanillas()
        {
            // The reason the scale is passed in at all. A world that moved its marks must get its
            // own numbers, not a second copy of vanilla's living in Core.
            var scale = new ReputationScale
            {
                HostileMin = -300, HostileMid = -200, NeutralMin = -100,
                NeutralMid = 0, FriendlyMin = 100, FriendlyMid = 200, FriendlyMax = 300,
            };
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "SPRT", new Dictionary<string, string> { { "*", "Enemies" } } },
                    },
                }),
                Roster, scale);

            Assert.Equal(-200, RepOf(plan, "SPRT", "IGSL"));
            Assert.Equal("Enemies", scale.BandOf(-200));
            Assert.Equal("Neutral", scale.BandOf(-99));   // vanilla would call -99 neutral too, but
            Assert.Equal("Enemies", scale.BandOf(-101));  // -101 only lands Enemies on THIS scale
        }

        [Fact]
        public void BandsMatchSesOwnThresholds()
        {
            // TranslateReputationToRelationship: below NeutralMin is Enemies, at or above
            // FriendlyMin is Friends. This function decides whether a turret fires; an approximation
            // of it would report a stance the game does not hold.
            ReputationScale s = ReputationScale.Vanilla;
            Assert.Equal("Enemies", s.BandOf(-1500));
            Assert.Equal("Enemies", s.BandOf(-501));
            Assert.Equal("Neutral", s.BandOf(-500));
            Assert.Equal("Neutral", s.BandOf(0));
            Assert.Equal("Neutral", s.BandOf(499));
            Assert.Equal("Friends", s.BandOf(500));
            Assert.Equal("Friends", s.BandOf(1500));
        }

        [Fact]
        public void ARawReputationOutsideTheScaleIsRefused()
        {
            RelationPlan plan = RelationPlanner.Build(
                With(new RelationRecipe
                {
                    Stances = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "SPRT", new Dictionary<string, string> { { "IGSL", "-9000" } } },
                    },
                }),
                Roster, ReputationScale.Vanilla);

            Assert.True(plan.IsRefused);
        }

        [Fact]
        public void TheShippedRecipesRelationsResolveAgainstTheLiveRoster()
        {
            // The roster is this world's, read from 'factions list' on awl-standalone-test
            // 2026-08-26 -- the same list faction_recipe.json's own comment names.
            string[] live = { "SPRT", "FCTM", "UNKN", "IMPR", "MEMN", "METC", "IGSL",
                              "UVCO", "TFEG", "TFCS", "STCG", "ISBT", "UNGD" };

            var recipe = JsonConvert.DeserializeObject<FactionRecipe>(File.ReadAllText(
                TestSupport.RepoFile("instance/worlds/awl-factions-genesis/faction_recipe.json")));

            string why;
            Assert.True(recipe.Validate(out why), why);

            RelationPlan plan = RelationPlanner.Build(recipe, live, ReputationScale.Vanilla);
            Assert.Empty(plan.Warnings);
            Assert.False(plan.IsRefused);
            Assert.Equal(78, plan.Entries.Count);            // C(13,2), the whole table

            // The brief, asserted rather than described (FAC-104): both antagonists are hostile to
            // everything, and the graveyard is hostile to nobody it is not being raided by.
            foreach (RelationPlanEntry e in plan.Entries)
            {
                bool touchesSprt = e.TagA == "SPRT" || e.TagB == "SPRT";
                bool touchesFctm = e.TagA == "FCTM" || e.TagB == "FCTM";
                if (touchesSprt || touchesFctm) Assert.Equal(-1000, e.Reputation);
                else if (e.TagA == "UNKN" || e.TagB == "UNKN") Assert.Equal(0, e.Reputation);
            }

            Assert.Equal(1000, RepOf(plan, "ISBT", "UNGD"));
            Assert.Equal(1000, RepOf(plan, "METC", "MEMN"));
            Assert.Equal(1000, RepOf(plan, "UVCO", "TFEG"));
            Assert.Equal(0, RepOf(plan, "IGSL", "METC"));    // rival traders, different blocs
        }
    }
}
