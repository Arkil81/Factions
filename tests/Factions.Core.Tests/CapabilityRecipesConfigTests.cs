using System.Collections.Generic;
using Factions;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <c>capability_recipes.json</c> is written only when it is missing, so a key added to
    /// <see cref="CapabilityRecipesConfig.Defaults"/> after an instance was created never reaches
    /// that instance. That is how <c>FuelDepot</c> shipped with a <c>GasBlocks</c> default and
    /// resolved nothing on a live server for three days, emitting no line saying so.
    /// <see cref="CapabilityRecipesConfig.MergeDefaults"/> closes it; these tests pin both halves —
    /// that a missing entry is supplied, and that a present one is never overwritten.
    /// </summary>
    public class CapabilityRecipesConfigTests
    {
        /// <summary>The shape of the live file that produced the failure: every key the feature
        /// predates is present, and <c>GasBlocks</c> is simply not there.</summary>
        private const string PreGasChainFile = @"{
  ""Capabilities"": { ""Refinery"": { ""1"": [ ""CommonMetals"" ] } },
  ""InstallBlocks"": { ""RefineryII"": ""LargeRefinery"", ""ManufactoryII"": ""LargeAssembler"" },
  ""InstallPrefabs"": { ""Shipyard"": ""GE_SmallShipyard_Empty"" },
  ""MachineBlocks"": { ""Refinery"": ""Blast Furnace"" },
  ""IgnoredBlueprints"": [],
  ""IgnoredInputKeys"": []
}";

        [Fact]
        public void AFilePredatingGasBlocks_GetsTheFuelDepotGeneratorBack()
        {
            var config = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(PreGasChainFile);

            List<string> added = CapabilityRecipesConfig.MergeDefaults(config);

            Assert.Contains("GasBlocks.FuelDepot", added);
            Assert.Equal(new List<string> { "OxygenGenerator" }, config.GasBlocks["FuelDepot"]);
        }

        [Fact]
        public void AFilePredatingTheFuelDepotInstallBlock_GetsItBack()
        {
            // The gap is per entry, not per map: the live file HAD an InstallBlocks map, just no
            // FuelDepot row in it, which left the capability on its flat 50-Hydrogen install cost.
            var config = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(PreGasChainFile);

            List<string> added = CapabilityRecipesConfig.MergeDefaults(config);

            Assert.Contains("InstallBlocks.FuelDepot", added);
            Assert.Equal("OxygenGenerator", config.InstallBlocks["FuelDepot"]);
        }

        [Fact]
        public void AnOperatorsOwnValue_IsNeverOverwritten()
        {
            const string edited = @"{
  ""InstallBlocks"": { ""RefineryII"": ""SomeModdedRefinery"" },
  ""GasBlocks"": { ""FuelDepot"": [ ""LargeBlockOxygenGeneratorLab"" ] }
}";
            var config = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(edited);

            List<string> added = CapabilityRecipesConfig.MergeDefaults(config);

            Assert.Equal("SomeModdedRefinery", config.InstallBlocks["RefineryII"]);
            Assert.Equal(new List<string> { "LargeBlockOxygenGeneratorLab" }, config.GasBlocks["FuelDepot"]);
            Assert.DoesNotContain("InstallBlocks.RefineryII", added);
            Assert.DoesNotContain("GasBlocks.FuelDepot", added);
        }

        [Fact]
        public void ACompleteFile_AddsNothing_SoTheFileIsNotRewritten()
        {
            // The caller only writes when this list is non-empty. A merge that always reported a
            // change would rewrite the operator's file on every single boot.
            var config = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(
                CapabilityRecipesConfig.DefaultsJson());

            Assert.Empty(CapabilityRecipesConfig.MergeDefaults(config));
        }

        [Fact]
        public void TheIgnoreLists_AreLeftAlone_SoNullStillMeansUseTheBuiltInSet()
        {
            // Writing today's list to disk would freeze it against a later change -- reintroducing,
            // for these two keys, the staleness MergeDefaults exists to remove. Null is the encoding
            // RecipeExclusions.From reads as "use the built-in set".
            const string noIgnoreKeys = @"{ ""Capabilities"": {} }";
            var config = JsonConvert.DeserializeObject<CapabilityRecipesConfig>(noIgnoreKeys);

            CapabilityRecipesConfig.MergeDefaults(config);

            Assert.Null(config.IgnoredBlueprints);
            Assert.Null(config.IgnoredInputKeys);
        }

        [Fact]
        public void AnEmptyFile_GetsTheWholeDefaultSet()
        {
            var config = JsonConvert.DeserializeObject<CapabilityRecipesConfig>("{}");

            CapabilityRecipesConfig.MergeDefaults(config);

            Assert.Equal(new List<string> { "OxygenGenerator" }, config.GasBlocks["FuelDepot"]);
            Assert.Equal("LargeRefinery", config.InstallBlocks["RefineryII"]);
            Assert.Equal("GE_SmallShipyard_Empty", config.InstallPrefabs["Shipyard"]);
            Assert.True(config.Capabilities.ContainsKey("Refinery"));
        }
    }
}
