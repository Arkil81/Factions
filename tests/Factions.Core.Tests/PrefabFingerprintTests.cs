using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The block-multiset identity behind the Services Terminal's "Send to Node" match
    /// (FAC-283). The host maps a saved prefab and a live grid group onto the same census shape, so
    /// what has to hold here is that the fold is order-blind and that everything the ticket calls
    /// part of the ship's identity actually changes the answer.</summary>
    public class PrefabFingerprintTests
    {
        private static List<KeyValuePair<string, long>> Census(params object[] pairs)
        {
            var list = new List<KeyValuePair<string, long>>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                list.Add(new KeyValuePair<string, long>((string)pairs[i], System.Convert.ToInt64(pairs[i + 1])));
            return list;
        }

        [Fact]
        public void Compute_IsOrderIndependent()
        {
            string a = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 120,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 1,
                       "MyObjectBuilder_Thrust/LargeBlockLargeThrust", 6), "Large", 1);
            string b = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_Thrust/LargeBlockLargeThrust", 6,
                       "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 120,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 1), "Large", 1);

            Assert.Equal(a, b);
            Assert.True(PrefabFingerprint.Matches(a, b));
        }

        [Fact]
        public void Compute_SumsRepeatedKeys_SoAPerBlockWalkMatchesADictionary()
        {
            // The host enumerates blocks; the same definition id arrives once per block.
            string perBlock = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 1,
                       "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 1,
                       "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 1,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 1), "Large", 1);
            string folded = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 3,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 1), "Large", 1);

            Assert.Equal(folded, perBlock);
        }

        [Fact]
        public void Compute_OneBlockDifference_IsAMiss()
        {
            string ship = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 120,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 1), "Large", 1);
            string plusOne = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 121,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 1), "Large", 1);
            string swappedOne = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", 119,
                       "MyObjectBuilder_CubeBlock/LargeHeavyBlockArmorBlock", 1,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 1), "Large", 1);

            Assert.NotEqual(ship, plusOne);
            Assert.NotEqual(ship, swappedOne);
            Assert.False(PrefabFingerprint.Matches(ship, plusOne));
            Assert.False(PrefabFingerprint.Matches(ship, swappedOne));
        }

        [Fact]
        public void Compute_IsGridSizeSensitive()
        {
            List<KeyValuePair<string, long>> blocks = Census("MyObjectBuilder_CubeBlock/ArmorBlock", 40);
            Assert.NotEqual(PrefabFingerprint.Compute(blocks, "Large", 1),
                            PrefabFingerprint.Compute(blocks, "Small", 1));
        }

        [Fact]
        public void Compute_IsGridCountSensitive()
        {
            List<KeyValuePair<string, long>> blocks = Census("MyObjectBuilder_CubeBlock/ArmorBlock", 40);
            Assert.NotEqual(PrefabFingerprint.Compute(blocks, "Large", 1),
                            PrefabFingerprint.Compute(blocks, "Large", 2));
        }

        [Fact]
        public void Compute_EmptyCensus_IsNoneAndNeverMatches()
        {
            Assert.Equal(PrefabFingerprint.None, PrefabFingerprint.Compute(Census(), "Large", 1));
            Assert.Equal(PrefabFingerprint.None, PrefabFingerprint.Compute(null, "Large", 1));
            // A zero or negative count contributes nothing rather than a phantom definition.
            Assert.Equal(PrefabFingerprint.None,
                         PrefabFingerprint.Compute(Census("MyObjectBuilder_CubeBlock/ArmorBlock", 0), "Large", 1));
        }

        [Fact]
        public void Matches_RefusesAnEmptyFingerprintOnEitherSide()
        {
            // A catalogue entry written before the field existed must refuse every hull, not accept
            // every hull.
            string ship = PrefabFingerprint.Compute(Census("MyObjectBuilder_CubeBlock/ArmorBlock", 40), "Large", 1);
            Assert.False(PrefabFingerprint.Matches(ship, ""));
            Assert.False(PrefabFingerprint.Matches("", ship));
            Assert.False(PrefabFingerprint.Matches("", ""));
            Assert.False(PrefabFingerprint.Matches(null, ship));
        }

        [Fact]
        public void Compute_CarriesTheCountsInTheClear()
        {
            // grids:size:blocks:distinctDefs:hash -- the counts are beside the hash, not folded into
            // it, so a hash collision alone cannot make two different hulls match.
            string fp = PrefabFingerprint.Compute(
                Census("MyObjectBuilder_CubeBlock/ArmorBlock", 40,
                       "MyObjectBuilder_Cockpit/LargeBlockCockpit", 2), "Large", 3);
            string[] parts = fp.Split(':');
            Assert.Equal(5, parts.Length);
            Assert.Equal("3", parts[0]);
            Assert.Equal("Large", parts[1]);
            Assert.Equal("42", parts[2]);
            Assert.Equal("2", parts[3]);
            Assert.Equal(16, parts[4].Length);
        }
    }
}
