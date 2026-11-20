using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The livery a faction draws hulls from. The case this exists for is measured, not imagined:
    /// the live catalogue on awl-standalone-test reports <c>FCTM=None</c> (Torch log, 2026-09-01),
    /// so every Factorum query fell through to the universal pool and FCTM flew an Idris.
    /// </summary>
    public class PrefabFactionHintTests
    {
        [Theory]
        [InlineData("Miner")]
        [InlineData("Trader")]
        [InlineData("Builder")]
        [InlineData("Pirate")]
        [InlineData("Military")]
        public void SeTypeWinsWhereverSeHasOne(string seType)
        {
            // Even against a personality that would otherwise imply a livery -- SE having an opinion
            // is the whole reason to defer to it, and a faction it has typed must never be
            // re-liveried behind its back.
            Assert.Equal(seType, PrefabFactionHint.Resolve(seType, "factorum"));
        }

        [Fact]
        public void ANoneTypedFactorumResolvesToTheFactorumLivery()
        {
            Assert.Equal("Factorum", PrefabFactionHint.Resolve("None", "factorum"));
        }

        [Fact]
        public void AnUnresolvedTypeAlsoConsultsThePersonality()
        {
            // "The catalogue could not answer" and "the catalogue answered None" are different
            // states and must reach the same livery, or FCTM's hulls would depend on whether the
            // type catalogue happened to load.
            Assert.Equal("Factorum", PrefabFactionHint.Resolve(null, "factorum"));
        }

        [Fact]
        public void ANoneTypedFactionWithNoLiveryPersonalityFallsToTheUniversalPool()
        {
            // UNKN is the live case: type None, personality "derelict", no Factorum content of its
            // own. Handing it the Factorum livery would be a silent second effect of this change.
            Assert.Equal("All", PrefabFactionHint.Resolve("None", "derelict"));
            Assert.Equal("All", PrefabFactionHint.Resolve("None", null));
        }

        [Fact]
        public void ARaiderKeepsThePirateTypeSeAlreadyGaveIt()
        {
            // SPRT live: type Pirate, personality raider. There is deliberately no raider row in the
            // personality table -- it would only restate SE's answer, except in the one case where
            // SE disagreed, which is the case worth respecting.
            Assert.Equal("Pirate", PrefabFactionHint.Resolve("Pirate", "raider"));
        }

        [Fact]
        public void ARaiderWithNoSeTypeIsNotQuietlyGivenAPirateLivery()
        {
            // The negative that pins the paragraph above: if a raider row were ever added, this is
            // the assertion that would notice.
            Assert.Equal("All", PrefabFactionHint.Resolve("None", "raider"));
        }

        [Fact]
        public void ThePersonalityMatchIsCaseInsensitive()
        {
            // The id is persisted lowercase in index.json, but nothing enforces that on the way in.
            Assert.Equal("Factorum", PrefabFactionHint.Resolve("None", "Factorum"));
        }
    }

    /// <summary>
    /// Whether a faction's stations stand inside a protective safe zone. The safe zone is what makes
    /// a station unshootable, so this decides whether it can be attacked at all.
    /// </summary>
    public class StationSafeZonePolicyTests
    {
        [Fact]
        public void TheFactorumsStationsStandUnprotected()
        {
            Assert.False(StationSafeZonePolicy.SpawnsSafeZone("factorum"));
            Assert.False(StationSafeZonePolicy.SpawnsSafeZone("Factorum"));
        }

        [Theory]
        [InlineData("raider")]
        [InlineData("settler")]
        [InlineData("mercantile")]
        [InlineData("derelict")]
        [InlineData("default")]
        [InlineData(null)]
        [InlineData("")]
        public void EveryOtherFactionKeepsItsSafeZone(string personality)
        {
            // The bound that matters: this policy may only ever REMOVE protection where it is asked
            // for. An unknown or absent personality must not silently expose a faction's stations.
            Assert.True(StationSafeZonePolicy.SpawnsSafeZone(personality));
        }
    }

    /// <summary>
    /// Which family of station prefabs a faction builds from. Replaced a single <c>isRaider</c>
    /// boolean, which could express two families and had no room for a third.
    /// </summary>
    public class StationStyleTests
    {
        [Theory]
        [InlineData("raider", "Pirate")]
        [InlineData("factorum", "Factorum")]
        [InlineData("Factorum", "Factorum")]
        [InlineData("settler", "Civilian")]
        [InlineData("mercantile", "Civilian")]
        [InlineData("derelict", "Civilian")]
        [InlineData(null, "Civilian")]
        public void APersonalityBuildsInItsOwnStyle(string personality, string expected)
        {
            Assert.Equal(expected, StationStyle.ForPersonality(personality));
        }

        [Fact]
        public void AnUnknownPersonalityBuildsCivilian()
        {
            // A faction can only ever GAIN a distinctive style by being given a personality that
            // names one -- a typo must not produce a Factorum outpost.
            Assert.Equal("Civilian", StationStyle.ForPersonality("factorvm"));
        }

        [Fact]
        public void APlayerOverrideStillChoosesBetweenTheTwoPlayerFacingLooks()
        {
            // The bool? contract the player station commands are written to, unchanged.
            Assert.Equal("Pirate", StationStyle.Resolve(true, "settler"));
            Assert.Equal("Civilian", StationStyle.Resolve(false, "raider"));
        }

        [Fact]
        public void NoOverrideDefersToThePersonality()
        {
            Assert.Equal("Factorum", StationStyle.Resolve(null, "factorum"));
            Assert.Equal("Pirate", StationStyle.Resolve(null, "raider"));
        }

        [Fact]
        public void APlayerCannotOverrideIntoTheFactorumStyle()
        {
            // Its family has no store and no contract board. That is a property of the faction, not
            // a look to pick -- so the override, which is a look, cannot reach it.
            Assert.NotEqual("Factorum", StationStyle.Resolve(true, "factorum"));
            Assert.NotEqual("Factorum", StationStyle.Resolve(false, "factorum"));
        }
    }
}
