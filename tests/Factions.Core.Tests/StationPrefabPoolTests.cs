using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Which designs a founding may draw. The fixture is the live <c>station_prefabs.json</c> key set
    /// of 2026-09-17, where the civilian surface pool held 41 designs, 17 of them Factorum structures and 19 of the rest
    /// designs SE does not found.
    /// </summary>
    public class StationPrefabPoolTests
    {
        private static readonly string[] LiveStationPrefabs =
        {
            "Economy_MiningStation_1", "Economy_MiningStation_2", "Economy_MiningStation_3",
            "Economy_OrbitalStation_1", "Economy_OrbitalStation_2", "Economy_OrbitalStation_3",
            "Economy_OrbitalStation_4", "Economy_OrbitalStation_5", "Economy_OrbitalStation_6",
            "Economy_OrbitalStation_7",
            "Economy_Outpost_1", "Economy_Outpost_10", "Economy_Outpost_11", "Economy_Outpost_11_Pirate",
            "Economy_Outpost_12", "Economy_Outpost_12_Pirate", "Economy_Outpost_13", "Economy_Outpost_13_Pirate",
            "Economy_Outpost_14", "Economy_Outpost_2", "Economy_Outpost_3", "Economy_Outpost_4",
            "Economy_Outpost_5", "Economy_Outpost_6", "Economy_Outpost_7", "Economy_Outpost_8", "Economy_Outpost_9",
            "Economy_SpaceStation_1", "Economy_SpaceStation_10", "Economy_SpaceStation_11",
            "Economy_SpaceStation_11_Pirate", "Economy_SpaceStation_12", "Economy_SpaceStation_13",
            "Economy_SpaceStation_14", "Economy_SpaceStation_14_Pirate", "Economy_SpaceStation_15",
            "Economy_SpaceStation_15_Pirate", "Economy_SpaceStation_2", "Economy_SpaceStation_3",
            "Economy_SpaceStation_4", "Economy_SpaceStation_5",
            "GE_Ambush", "GE_AmbushB", "GE_BioResearch", "GE_BioResearchB", "GE_DroneDepotA", "GE_DroneDepotB",
            "GE_FactorumGunPlatform_A", "GE_FactorumGunPlatform_B", "GE_FactorumGunPlatform_C",
            "GE_ListeningPost", "GE_ListeningPostB", "GE_ListeningPostC",
            "GE_LogisticsFacility", "GE_LogisticsFacilityB", "GE_StorageFacility", "GE_StorageFacilityB",
            "RE01_FactorumRelaySatellite",
        };

        private static readonly HashSet<string> Factorum = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GE_Ambush", "GE_AmbushB", "GE_BioResearch", "GE_BioResearchB", "GE_DroneDepotA", "GE_DroneDepotB",
            "GE_FactorumGunPlatform_A", "GE_FactorumGunPlatform_B", "GE_FactorumGunPlatform_C",
            "GE_ListeningPost", "GE_ListeningPostB", "GE_ListeningPostC",
            "GE_LogisticsFacility", "GE_LogisticsFacilityB", "GE_StorageFacility", "GE_StorageFacilityB",
            "RE01_FactorumRelaySatellite",
        };

        // SE's per-station-type tables as shipped (Content/Data/Stations_Economy.sbc): the Outposts
        // type lists Outpost_10-14 and three pirate outposts; the OrbitalStations and SpaceStations
        // types list SpaceStation_10-15 and three pirate stations. Outpost_1-9 and SpaceStation_1-5
        // are commented out; OrbitalStation_* and MiningStation_* appear under no type.
        private static readonly Dictionary<string, string> SeListedSites = BuildSeListedSites();

        private static Dictionary<string, string> BuildSeListedSites()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 10; i <= 14; i++) d["Economy_Outpost_" + i] = StationPrefabSite.Surface;
            foreach (int i in new[] { 11, 12, 13 }) d["Economy_Outpost_" + i + "_Pirate"] = StationPrefabSite.Surface;
            for (int i = 10; i <= 15; i++) d["Economy_SpaceStation_" + i] = StationPrefabSite.Space;
            foreach (int i in new[] { 11, 14, 15 }) d["Economy_SpaceStation_" + i + "_Pirate"] = StationPrefabSite.Space;
            return d;
        }

        private static List<string> Pool(bool isSpace, string style)
        {
            return StationPrefabPool.Select(LiveStationPrefabs, isSpace, style, Factorum.Contains, SeListedSites);
        }

        private static PoolMismatch Classify(string ownerStyle, string prefabId, bool isSpace,
            IDictionary<string, string> seSites = null)
        {
            return StationPrefabPool.Classify(ownerStyle, prefabId, isSpace, Factorum.Contains, seSites ?? SeListedSites);
        }

        [Fact]
        public void CivilianPools_AreTheDesignsSeFounds()
        {
            Assert.Equal(new[] { "Economy_Outpost_10", "Economy_Outpost_11", "Economy_Outpost_12",
                                 "Economy_Outpost_13", "Economy_Outpost_14" },
                Pool(false, StationStyle.Civilian));
            Assert.Equal(new[] { "Economy_SpaceStation_10", "Economy_SpaceStation_11", "Economy_SpaceStation_12",
                                 "Economy_SpaceStation_13", "Economy_SpaceStation_14", "Economy_SpaceStation_15" },
                Pool(true, StationStyle.Civilian));
        }

        [Fact]
        public void PiratePools_AreSeListedPirateVariants()
        {
            Assert.Equal(new[] { "Economy_Outpost_11_Pirate", "Economy_Outpost_12_Pirate", "Economy_Outpost_13_Pirate" },
                Pool(false, StationStyle.Pirate));
            Assert.Equal(new[] { "Economy_SpaceStation_11_Pirate", "Economy_SpaceStation_14_Pirate", "Economy_SpaceStation_15_Pirate" },
                Pool(true, StationStyle.Pirate));
        }

        [Fact]
        public void OldGenerationUnlistedAndFactorumDesigns_JoinNoTradingPool()
        {
            var every = new List<string>();
            foreach (bool isSpace in new[] { false, true })
                foreach (string style in new[] { StationStyle.Civilian, StationStyle.Pirate })
                    every.AddRange(Pool(isSpace, style));

            Assert.Equal(17, every.Count);   // non-empty: the absences below are not an empty pool passing
            Assert.DoesNotContain("Economy_Outpost_7", every);
            Assert.DoesNotContain("Economy_SpaceStation_3", every);
            Assert.DoesNotContain(every, id => id.StartsWith("Economy_OrbitalStation_", StringComparison.Ordinal));
            Assert.DoesNotContain(every, id => id.StartsWith("Economy_MiningStation_", StringComparison.Ordinal));
            Assert.DoesNotContain(every, id => Factorum.Contains(id));
        }

        [Fact]
        public void FactorumStructure_StaysOut_EvenWhenSeListsIt()
        {
            var seSites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GE_ListeningPost"] = StationPrefabSite.Surface,
                ["Economy_Outpost_10"] = StationPrefabSite.Surface,
            };
            var ids = new[] { "GE_ListeningPost", "Economy_Outpost_10" };

            Assert.Equal(new[] { "Economy_Outpost_10" },
                StationPrefabPool.Select(ids, false, StationStyle.Civilian, Factorum.Contains, seSites));
        }

        [Fact]
        public void NoSeTables_LeavesTheTradingPoolsEmpty()
        {
            Assert.Empty(StationPrefabPool.Select(LiveStationPrefabs, false, StationStyle.Civilian, Factorum.Contains, null));
            Assert.Empty(StationPrefabPool.Select(LiveStationPrefabs, true, StationStyle.Pirate, Factorum.Contains,
                new Dictionary<string, string>()));
        }

        [Fact]
        public void FactorumPool_IsTheWholeFamily_OnEitherSite()
        {
            List<string> surface = Pool(false, StationStyle.Factorum);
            List<string> space = Pool(true, StationStyle.Factorum);

            Assert.Equal(17, surface.Count);
            Assert.Equal(surface, space);
            foreach (string id in surface) Assert.Contains(id, Factorum);
        }

        [Fact]
        public void Classify_NamesWhatIsWrongWithAStandingDesign()
        {
            Assert.Equal(PoolMismatch.FactorumStructure, Classify(StationStyle.Civilian, "GE_ListeningPost", false));
            Assert.Equal(PoolMismatch.FactorumStructure, Classify(StationStyle.Pirate, "GE_AmbushB", false));
            Assert.Equal(PoolMismatch.NotFoundedHere, Classify(StationStyle.Civilian, "Economy_Outpost_7", false));
            Assert.Equal(PoolMismatch.NotFoundedHere, Classify(StationStyle.Civilian, "Economy_OrbitalStation_5", false));
            Assert.Equal(PoolMismatch.NotFoundedHere, Classify(StationStyle.Civilian, "Economy_Outpost_10", true));
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Civilian, "Economy_Outpost_10", false));
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Civilian, "Economy_SpaceStation_12", true));
        }

        [Fact]
        public void Classify_PirateOwnerAcceptsItsFallbackFamily()
        {
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Pirate, "Economy_Outpost_12_Pirate", false));
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Pirate, "Economy_Outpost_10", false));
            Assert.Equal(PoolMismatch.NotFoundedHere, Classify(StationStyle.Pirate, "Economy_Outpost_7", false));
        }

        [Fact]
        public void Classify_FactorumOwnerIsNeverOutside()
        {
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Factorum, "Economy_Outpost_7", false));
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Factorum, "GE_ListeningPost", true));
        }

        [Fact]
        public void Classify_WithoutSeTables_AnswersOnlyTheFactorumQuestion()
        {
            var none = new Dictionary<string, string>();
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Civilian, "Economy_Outpost_7", false, none));
            Assert.Equal(PoolMismatch.FactorumStructure, Classify(StationStyle.Civilian, "GE_ListeningPost", false, none));
            Assert.Equal(PoolMismatch.None, Classify(StationStyle.Civilian, null, false));
        }

        [Fact]
        public void OfPrefab_NameFirst_ThenSeLists_ElseNull()
        {
            var seSites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Economy_OrbitalStation_1"] = StationPrefabSite.Space,
            };
            Assert.Equal(StationPrefabSite.Surface, StationPrefabSite.OfPrefab("Economy_Outpost_12_Pirate", null));
            Assert.Equal(StationPrefabSite.Space, StationPrefabSite.OfPrefab("Economy_SpaceStation_3", null));
            Assert.Equal(StationPrefabSite.Space, StationPrefabSite.OfPrefab("Economy_OrbitalStation_1", seSites));
            Assert.Null(StationPrefabSite.OfPrefab("Economy_MiningStation_1", seSites));
            Assert.Null(StationPrefabSite.OfPrefab(null, seSites));
        }
    }
}
