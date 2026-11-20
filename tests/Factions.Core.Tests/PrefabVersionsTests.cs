using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Which version of a design each faction builds new, and how a new version is
    /// numbered.</summary>
    public class PrefabVersionsTests
    {
        private static PrefabVersionMember M(string id, int version, string owner = null)
        {
            return new PrefabVersionMember { Id = id, Version = version, OwnerFactionTag = owner };
        }

        [Fact]
        public void VersionId_appends_the_number_after_the_separator()
        {
            Assert.Equal("Laevateinn@2", PrefabVersions.VersionId("Laevateinn", 2));
            Assert.Equal("Cepheus X-1 Finch@3", PrefabVersions.VersionId("Cepheus X-1 Finch", 3));
        }

        [Fact]
        public void An_every_faction_version_supersedes_the_original_for_everyone()
        {
            var family = new[] { M("Keep", 1), M("Keep@2", 2), M("Keep@3", 3) };
            Assert.Equal("Keep@3", PrefabVersions.Latest("Keep", "AWL", family));
            Assert.Equal("Keep@3", PrefabVersions.Latest("Keep@2", "XYZ", family));
            Assert.Equal("Keep@3", PrefabVersions.Latest("Keep@3", "AWL", family));
        }

        [Fact]
        public void A_faction_line_of_a_content_prefab_supersedes_it_for_that_faction_only()
        {
            var family = new[] { M("Laevateinn", 1), M("Laevateinn@2", 2, "AWL"), M("Laevateinn@3", 3, "XYZ") };

            Assert.Equal("Laevateinn@2", PrefabVersions.Latest("Laevateinn", "AWL", family));
            Assert.Equal("Laevateinn@3", PrefabVersions.Latest("Laevateinn", "XYZ", family));
            Assert.Equal("Laevateinn", PrefabVersions.Latest("Laevateinn", "NPC1", family));
            // Another faction's line is never an upgrade of yours, even when its number is higher.
            Assert.Equal("Laevateinn@2", PrefabVersions.Latest("Laevateinn@2", "AWL", family));
        }

        [Fact]
        public void An_id_absent_from_the_family_reads_as_the_original()
        {
            var family = new[] { M("Keep@2", 2) };
            Assert.Equal("Keep@2", PrefabVersions.Latest("Keep", "AWL", family));
            Assert.Equal("Keep", PrefabVersions.Latest("Keep", "AWL", null));
        }

        [Fact]
        public void NextVersion_is_one_past_the_highest_and_never_below_two()
        {
            Assert.Equal(2, PrefabVersions.NextVersion(new int[0]));
            Assert.Equal(2, PrefabVersions.NextVersion(null));
            Assert.Equal(5, PrefabVersions.NextVersion(new[] { 2, 4, 3 }));
        }

        [Fact]
        public void Two_lines_of_one_content_prefab_never_share_a_number()
        {
            var taken = new List<int> { PrefabVersions.NextVersion(new int[0]) };
            taken.Add(PrefabVersions.NextVersion(taken));
            Assert.Equal(new[] { 2, 3 }, taken);
        }

        private sealed class FakeVersions : IPrefabVersionCatalogue
        {
            public string FamilyOf(string prefabId)
            {
                int at = prefabId.IndexOf(PrefabVersions.Separator);
                return at < 0 ? prefabId : prefabId.Substring(0, at);
            }
            public int VersionOf(string prefabId) { return 1; }
            public string LatestFor(string prefabId, string factionTag) { return prefabId; }
        }

        private sealed class VersionedCatalogue : StationRefitCatalogueStub, IPrefabVersionCatalogue
        {
            private readonly FakeVersions _v = new FakeVersions();
            public string FamilyOf(string prefabId) { return _v.FamilyOf(prefabId); }
            public int VersionOf(string prefabId) { return _v.VersionOf(prefabId); }
            public string LatestFor(string prefabId, string factionTag) { return _v.LatestFor(prefabId, factionTag); }
        }

        [Fact]
        public void SameFamily_reads_the_catalogue_and_falls_back_to_equality_without_one()
        {
            var versioned = new VersionedCatalogue();
            Assert.True(PrefabVersions.SameFamily(versioned, "Keep", "Keep@2"));
            Assert.True(PrefabVersions.SameFamily(versioned, "Keep@2", "Keep@3"));
            Assert.False(PrefabVersions.SameFamily(versioned, "Keep", "Other@2"));

            var plain = new StationRefitCatalogueStub();
            Assert.False(PrefabVersions.SameFamily(plain, "Keep", "Keep@2"));
            Assert.True(PrefabVersions.SameFamily(plain, "keep", "Keep"));
        }

        [Fact]
        public void A_shelf_line_counts_every_version_listed_and_building()
        {
            var a = new SpatialNode { Id = "N1" };
            var b = new SpatialNode { Id = "N2" };
            a.ListedForSale["Keep"] = 1;
            b.ListedForSale["Keep@2"] = 2;
            b.ListedForSale["Other@2"] = 5;
            b.BuildQueue.Add(new BuildQueueEntry { PrefabId = "Keep@3", DestinedForSale = true });
            b.BuildQueue.Add(new BuildQueueEntry { PrefabId = "Keep@3", DestinedForSale = false });
            var owned = new List<SpatialNode> { a, b };
            var versioned = new VersionedCatalogue();

            Assert.Equal(3L, StoreStockManager.CountListed(owned, "Keep", versioned));
            Assert.Equal(1, StoreStockManager.CountShelfBuildsInProgress(owned, "Keep", versioned));
            // Without a catalogue that knows versions, the line counts its own id only.
            Assert.Equal(1L, StoreStockManager.CountListed(owned, "Keep"));
            Assert.Equal(0, StoreStockManager.CountShelfBuildsInProgress(owned, "Keep"));
        }

        [Fact]
        public void Unreferenced_keeps_any_id_a_document_names_as_a_value_or_a_key()
        {
            var candidates = new[] { "Keep", "Keep@2", "Keep@3", "Cepheus X-1 Finch@2" };
            var docs = new[]
            {
                "{\"Nodes\":[{\"StationPrefabId\":\"keep@2\"}]}",           // a value, other case
                "{\"ShipStock\":{\"Cepheus X-1 Finch@2\":1}}",               // a key
                "{\"Log\":\"built Keep@3x\"}",                              // a substring is not a name
            };

            Assert.Equal(new[] { "Keep", "Keep@3" }, PrefabVersions.Unreferenced(candidates, docs));
            Assert.Equal(new[] { "Keep" }, PrefabVersions.Unreferenced(new[] { "Keep" }, null));
        }

        /// <summary>The minimum <see cref="IPrefabCatalogue"/> the helpers cast from.</summary>
        private class StationRefitCatalogueStub : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new List<WeaponBand>(); }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new List<string>(); }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }
    }
}
