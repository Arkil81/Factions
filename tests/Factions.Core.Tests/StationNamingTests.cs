using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the two rules that decide whether a station can be named at all: a name is in use
    /// only while a station carries it (names are derived from the world, never accumulated), and
    /// an exhausted pool disambiguates rather than failing.
    /// </summary>
    public sealed class StationNamingTests : IDisposable
    {
        private readonly string _dir;

        public StationNamingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "factions-naming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
        }

        /// <summary>Builds a catalogue with a pool of exactly stems x suffixes usable names.</summary>
        private NameSelector Selector(string[] stems, string[] suffixes)
        {
            var additions = new
            {
                personalities = new Dictionary<string, object>
                {
                    { "default", new Dictionary<string, object>
                        {
                            { "SpaceStations", new { stems = stems, suffixes = suffixes, possessiveSuffixes = new string[0] } }
                        }
                    }
                }
            };

            string catalogueJson = StationNameCatalogue.GenerateCatalogue(
                string.Empty, Newtonsoft.Json.JsonConvert.SerializeObject(additions));
            string path = Path.Combine(_dir, "catalogue-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, catalogueJson);

            var selector = new NameSelector(path, null);
            Assert.True(selector.Initialize());
            return selector;
        }

        private static Func<IEnumerable<string>> Provider(List<string> live)
        {
            return () => live;
        }

        [Fact]
        public void SelectName_DoesNotReuseANameAStationAlreadyCarries()
        {
            var live = new List<string>();
            NameSelector selector = Selector(new[] { "Alpha", "Beta" }, new[] { "Reach" });
            selector.InUseProvider = Provider(live);
            selector.RefreshNamesInUse();

            string first = selector.SelectName("AAA", "default", "SpaceStations", "N1");
            live.Add(first);

            string second = selector.SelectName("AAA", "default", "SpaceStations", "N2");

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void RefreshNamesInUse_ReturnsARetiredNameToThePool()
        {
            // The regression this whole change exists for: a name held by a station that no longer
            // exists must come back. Before, the used-names ledger only ever grew.
            var live = new List<string>();
            NameSelector selector = Selector(new[] { "Alpha" }, new[] { "Reach" });   // pool of exactly one
            selector.InUseProvider = Provider(live);
            selector.RefreshNamesInUse();

            string first = selector.SelectName("AAA", "default", "SpaceStations", "N1");
            Assert.Equal("Alpha Reach", first);
            live.Add(first);
            selector.RefreshNamesInUse();

            // Station lost: the world no longer reports the name.
            live.Clear();
            selector.RefreshNamesInUse();

            string reused = selector.SelectName("BBB", "default", "SpaceStations", "N2");
            Assert.Equal("Alpha Reach", reused);
        }

        [Fact]
        public void SelectName_IsUniqueAcrossFactions_NotJustWithinOne()
        {
            var live = new List<string>();
            NameSelector selector = Selector(new[] { "Alpha", "Beta" }, new[] { "Reach" });
            selector.InUseProvider = Provider(live);
            selector.RefreshNamesInUse();

            string a = selector.SelectName("AAA", "default", "SpaceStations", "N1");
            live.Add(a);
            string b = selector.SelectName("BBB", "default", "SpaceStations", "N2");

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void SelectName_DisambiguatesWithAnOrdinal_WhenThePoolIsExhausted()
        {
            var live = new List<string>();
            NameSelector selector = Selector(new[] { "Alpha", "Beta" }, new[] { "Reach" });  // pool of two
            selector.InUseProvider = Provider(live);
            selector.RefreshNamesInUse();

            var issued = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                string name = selector.SelectName("AAA", "default", "SpaceStations", "N" + i);
                Assert.False(string.IsNullOrEmpty(name));       // exhaustion must never fail
                Assert.DoesNotContain(name, issued);            // and must never collide
                issued.Add(name);
                live.Add(name);
            }

            // Two came from the pool; the rest carry a roman ordinal on a catalogue name.
            Assert.Equal(2, issued.FindAll(n => n == "Alpha Reach" || n == "Beta Reach").Count);
            foreach (string name in issued)
            {
                if (name == "Alpha Reach" || name == "Beta Reach") continue;
                Assert.True(name.StartsWith("Alpha Reach ", StringComparison.Ordinal)
                            || name.StartsWith("Beta Reach ", StringComparison.Ordinal), name);
            }
        }

        [Fact]
        public void SelectName_IsDeterministicForTheSameNode()
        {
            var live = new List<string>();
            NameSelector a = Selector(new[] { "Alpha", "Beta", "Gamma" }, new[] { "Reach", "Anchor" });
            a.InUseProvider = Provider(live);
            a.RefreshNamesInUse();

            NameSelector b = Selector(new[] { "Alpha", "Beta", "Gamma" }, new[] { "Reach", "Anchor" });
            b.InUseProvider = Provider(new List<string>());
            b.RefreshNamesInUse();

            Assert.Equal(a.SelectName("AAA", "default", "SpaceStations", "N42"),
                         b.SelectName("AAA", "default", "SpaceStations", "N42"));
        }

        [Fact]
        public void SelectName_IgnoresSingleWordStems()
        {
            // A bare stem is not a station name; only stem+suffix combinations are offered.
            var live = new List<string>();
            NameSelector selector = Selector(new[] { "Alpha" }, new[] { "Reach" });
            selector.InUseProvider = Provider(live);
            selector.RefreshNamesInUse();

            string name = selector.SelectName("AAA", "default", "SpaceStations", "N1");
            Assert.Equal("Alpha Reach", name);
        }

        [Fact]
        public void SelectName_FallsBackToTheDefaultPool_ForAnUnmappedPersonality()
        {
            var live = new List<string>();
            NameSelector selector = Selector(new[] { "Alpha" }, new[] { "Reach" });
            selector.InUseProvider = Provider(live);
            selector.RefreshNamesInUse();

            Assert.Equal("Alpha Reach",
                selector.SelectName("AAA", "somethingUnmapped", "SpaceStations", "N1"));
        }

        [Fact]
        public void SelectName_ReturnsNull_WhenTheCatalogueHasNoEntryForTheType()
        {
            var live = new List<string>();
            NameSelector selector = Selector(new[] { "Alpha" }, new[] { "Reach" });
            selector.InUseProvider = Provider(live);
            selector.RefreshNamesInUse();

            Assert.Null(selector.SelectName("AAA", "default", "Outposts", "N1"));
        }

        /// <summary>A FactionNames.sbc holding one Military type, enough to tell a personality that
        /// draws vanilla names from one that does not.</summary>
        private const string MilitaryOnlyFactionNamesXml =
            "<Definitions><FactionNames><FactionName><Type>Military</Type><StationTypeNames>"
            + "<StationType><StationTypeId Subtype=\"SpaceStations\" /><Names>"
            + "<string>Fort Bothwell</string><string>Cedarbrook Armory</string>"
            + "</Names></StationType></StationTypeNames></FactionName></FactionNames></Definitions>";

        private static List<StationNameCatalogue.CatalogueName> NamesFor(
            StationNameCatalogue.MasterCatalogue catalogue, string personalityId, string stationType)
        {
            foreach (StationNameCatalogue.CatalogueEntry e in catalogue.Entries)
                if (string.Equals(e.PersonalityId, personalityId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.StationType, stationType, StringComparison.OrdinalIgnoreCase))
                    return e.Names;
            return new List<StationNameCatalogue.CatalogueName>();
        }

        [Fact]
        public void GenerateCatalogue_GivesFactorumNoVanillaNames_WhereDefaultStillGetsThem()
        {
            // FCTM is a precursor machine intelligence, not a human military: mapping it back onto
            // the vanilla Military type would hand it "Fort Bothwell" again.
            var additions = new
            {
                personalities = new Dictionary<string, object>
                {
                    { "factorum", new Dictionary<string, object>
                        {
                            { "SpaceStations", new { stems = new[] { "7071 Kronos" }, suffixes = new[] { "Foundry" }, possessiveSuffixes = new string[0] } }
                        }
                    },
                    { "default", new Dictionary<string, object>
                        {
                            { "SpaceStations", new { stems = new[] { "Alpha" }, suffixes = new[] { "Reach" }, possessiveSuffixes = new string[0] } }
                        }
                    }
                }
            };

            var catalogue = Newtonsoft.Json.JsonConvert
                .DeserializeObject<StationNameCatalogue.MasterCatalogue>(
                    StationNameCatalogue.GenerateCatalogue(
                        MilitaryOnlyFactionNamesXml,
                        Newtonsoft.Json.JsonConvert.SerializeObject(additions)));

            List<StationNameCatalogue.CatalogueName> factorum = NamesFor(catalogue, "factorum", "SpaceStations");
            List<StationNameCatalogue.CatalogueName> deflt = NamesFor(catalogue, "default", "SpaceStations");

            Assert.DoesNotContain(factorum, n => string.Equals(n.Source, "vanilla", StringComparison.Ordinal));
            Assert.Contains(factorum, n => n.Name == "7071 Kronos Foundry");

            // The same run proves the vanilla path works at all, so the assertion above cannot pass
            // merely because nothing was parsed.
            Assert.Contains(deflt, n => n.Name == "Fort Bothwell" && n.Source == "vanilla");
        }

        [Fact]
        public void ShippedAdditions_GiveFactorumItsOwnVocabulary_WithNoPossessives()
        {
            string json = File.ReadAllText(TestSupport.RepoFile("instance/station_name_additions.json"));
            var root = Newtonsoft.Json.JsonConvert
                .DeserializeObject<StationNameCatalogue.NameAdditionsRoot>(json);

            Dictionary<string, StationNameCatalogue.PersonalityNameAdditions> factorum = root.Personalities["factorum"];
            Dictionary<string, StationNameCatalogue.PersonalityNameAdditions> deflt = root.Personalities["default"];

            foreach (string stationType in new[] { "Outposts", "OrbitalStations", "SpaceStations" })
            {
                StationNameCatalogue.PersonalityNameAdditions f = factorum[stationType];
                StationNameCatalogue.PersonalityNameAdditions d = deflt[stationType];

                // A machine intelligence does not own things in the possessive ("Aegis's Guard").
                Assert.Empty(f.PossessiveSuffixes);

                // The whole point of the block: it must not be a copy of default's vocabulary.
                Assert.Empty(f.Stems.Intersect(d.Stems, StringComparer.OrdinalIgnoreCase));
            }

            // Deep space carries the registry-coded Titan names; the two inner zones do not.
            Assert.All(factorum["SpaceStations"].Stems,
                stem => Assert.Matches(@"^[1-9][0-9]{3} [A-Z][a-z]+$", stem));
            Assert.DoesNotContain(factorum["Outposts"].Stems, s => s.Contains(" "));
            Assert.DoesNotContain(factorum["OrbitalStations"].Stems, s => s.Contains(" "));
        }

        [Theory]
        [InlineData(2, "II")]
        [InlineData(4, "IV")]
        [InlineData(9, "IX")]
        [InlineData(14, "XIV")]
        [InlineData(40, "XL")]
        [InlineData(99, "XCIX")]
        [InlineData(500, "D")]
        public void Roman_FormatsOrdinals(int value, string expected)
        {
            Assert.Equal(expected, NameSelector.Roman(value));
        }
    }
}
