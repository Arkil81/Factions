using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class WorldPurgeScopeTests
    {
        // The verb's own tokens: t[0]="factions", t[1]="world", t[2]="purge", arguments from 3.
        private static string[] Argv(params string[] rest)
        {
            var argv = new string[3 + rest.Length];
            argv[0] = "factions"; argv[1] = "world"; argv[2] = "purge";
            for (int i = 0; i < rest.Length; i++) argv[3 + i] = rest[i];
            return argv;
        }

        private static WorldPurgeScope Parse(params string[] rest)
        {
            WorldPurgeScope scope;
            string error;
            Assert.True(WorldPurgeScope.TryParse(Argv(rest), 3, out scope, out error), error);
            Assert.Null(error);
            return scope;
        }

        [Fact]
        public void BareVerb_IsADryRunOverEverything()
        {
            WorldPurgeScope scope = Parse();
            Assert.False(scope.Apply);
            Assert.False(scope.OrphansOnly);
            Assert.Null(scope.TagFilter);
        }

        [Fact]
        public void Orphans_And_Apply_AreIndependentAndOrderFree()
        {
            WorldPurgeScope a = Parse("orphans", "apply");
            Assert.True(a.Apply);
            Assert.True(a.OrphansOnly);
            Assert.Null(a.TagFilter);

            WorldPurgeScope b = Parse("apply", "orphans");
            Assert.True(b.Apply);
            Assert.True(b.OrphansOnly);
        }

        [Fact]
        public void Orphans_Alone_IsADryRun()
        {
            WorldPurgeScope scope = Parse("orphans");
            Assert.True(scope.OrphansOnly);
            Assert.False(scope.Apply);
        }

        [Fact]
        public void TagFilter_StillWorks_AndCombinesWithOrphans()
        {
            WorldPurgeScope scope = Parse("orphans", "apply", "SPRT");
            Assert.True(scope.OrphansOnly);
            Assert.True(scope.Apply);
            Assert.Equal("SPRT", scope.TagFilter);
        }

        [Fact]
        public void KeywordsAreCaseInsensitive_LikeEveryOtherVerbHere()
        {
            WorldPurgeScope scope = Parse("ORPHANS", "Apply");
            Assert.True(scope.OrphansOnly);
            Assert.True(scope.Apply);
        }

        /// <summary>The defect this parser exists to prevent. The previous shape assigned any
        /// non-<c>apply</c> token to the tag filter, so a misspelled keyword became a tag that
        /// matched no faction — and the verb answered "nothing to purge", which is exactly what a
        /// clean world says. A no-op that reads as success is worse than a refusal.</summary>
        [Theory]
        [InlineData("orphan")]
        [InlineData("orpans")]
        [InlineData("orphans-only")]
        [InlineData("--orphans")]
        public void MisspelledKeyword_IsRefused_NotSilentlyTakenAsATag(string typo)
        {
            WorldPurgeScope scope;
            string error;
            Assert.False(WorldPurgeScope.TryParse(Argv(typo), 3, out scope, out error));
            Assert.Contains(typo, error);
            Assert.Contains("Usage", error);
        }

        [Fact]
        public void AShortTypoIsIndistinguishableFromATag_AndIsAccepted()
        {
            // Deliberately NOT refused: "SPRT" and a 4-character typo are the same shape, and the
            // verb cannot know the faction list from here. The report names the tag it filtered on,
            // which is where a wrong one becomes visible.
            WorldPurgeScope scope = Parse("orph");
            Assert.Equal("orph", scope.TagFilter);
            Assert.False(scope.OrphansOnly);
        }

        [Fact]
        public void ASecondTagIsRefusedRatherThanOverwritingTheFirst()
        {
            WorldPurgeScope scope;
            string error;
            Assert.False(WorldPurgeScope.TryParse(Argv("SPRT", "FCTM"), 3, out scope, out error));
            Assert.Contains("SPRT", error);
        }

        [Fact]
        public void NullAndEmptyTokensAreTolerated()
        {
            // The chat twin passes fixed optional parameters, so empties reach the parser routinely.
            WorldPurgeScope scope;
            string error;
            Assert.True(WorldPurgeScope.TryParse(
                new[] { "factions", "world", "purge", "orphans", null, "", "apply" }, 3, out scope, out error));
            Assert.True(scope.OrphansOnly);
            Assert.True(scope.Apply);
            Assert.Null(scope.TagFilter);
        }
    }
}
