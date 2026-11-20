using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>What a node will hand out and what it will take in at a Services Terminal
    /// (FAC-283). The host half is SE entity work with no offline harness, so these two rules are
    /// the part that can be pinned: a listing is never withdrawn from under itself, and a cargo key
    /// the node's economy cannot consume refuses the send by name rather than becoming dead
    /// stock.</summary>
    public class ShipAssetRulesTests
    {
        private static Dictionary<string, int> Ships(params object[] pairs)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                d[(string)pairs[i]] = System.Convert.ToInt32(pairs[i + 1]);
            return d;
        }

        private static List<KeyValuePair<string, long>> Census(params object[] pairs)
        {
            var list = new List<KeyValuePair<string, long>>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                list.Add(new KeyValuePair<string, long>((string)pairs[i], System.Convert.ToInt64(pairs[i + 1])));
            return list;
        }

        // ---- withdrawable ------------------------------------------------------------------------

        [Fact]
        public void WithdrawableCount_IsStockLessListed_FlooredAtZero()
        {
            Assert.Equal(2, ShipAssetRules.WithdrawableCount(5, 3));
            Assert.Equal(5, ShipAssetRules.WithdrawableCount(5, 0));
            Assert.Equal(0, ShipAssetRules.WithdrawableCount(3, 3));
            Assert.Equal(0, ShipAssetRules.WithdrawableCount(1, 4));
            Assert.Equal(0, ShipAssetRules.WithdrawableCount(0, 0));
            // A negative listing is not a credit toward withdrawal.
            Assert.Equal(2, ShipAssetRules.WithdrawableCount(2, -3));
        }

        [Fact]
        public void WithdrawableAt_DeductsTheListing()
        {
            List<ShipAssetOffer> offers = ShipAssetRules.WithdrawableAt(
                Ships("Hauler", 4, "Escort", 1), Ships("Hauler", 3));

            Assert.Equal(2, offers.Count);
            Assert.Contains(offers, o => o.PrefabId == "Hauler" && o.Count == 1);
            Assert.Contains(offers, o => o.PrefabId == "Escort" && o.Count == 1);
        }

        [Fact]
        public void WithdrawableAt_ANodeWithOnlyListedCopies_YieldsNoRow()
        {
            List<ShipAssetOffer> offers = ShipAssetRules.WithdrawableAt(
                Ships("Hauler", 2), Ships("Hauler", 2));

            Assert.Empty(offers);
        }

        [Fact]
        public void WithdrawableAt_SkipsTheFreeBuildKey()
        {
            List<ShipAssetOffer> offers = ShipAssetRules.WithdrawableAt(
                Ships("", 3, "Hauler", 1), null);

            Assert.Single(offers);
            Assert.Equal("Hauler", offers[0].PrefabId);
        }

        [Fact]
        public void WithdrawableAt_EmptyOrNullStock_YieldsNothing()
        {
            Assert.Empty(ShipAssetRules.WithdrawableAt(null, null));
            Assert.Empty(ShipAssetRules.WithdrawableAt(Ships(), Ships("Hauler", 1)));
            Assert.Empty(ShipAssetRules.WithdrawableAt(Ships("Hauler", 0), null));
        }

        // ---- cargo filter ------------------------------------------------------------------------

        [Fact]
        public void FilterCargo_CreditsTheThreeAcceptedPrefixes()
        {
            var accepted = new Dictionary<string, long>();
            var refused = new List<string>();
            bool ok = ShipAssetRules.FilterCargo(
                Census("Ore:Iron", 500, "Ingot:Silicon", 40, "Comp:SteelPlate", 12),
                accepted, refused);

            Assert.True(ok);
            Assert.Empty(refused);
            Assert.Equal(3, accepted.Count);
            Assert.Equal(500L, accepted["Ore:Iron"]);
            Assert.Equal(40L, accepted["Ingot:Silicon"]);
            Assert.Equal(12L, accepted["Comp:SteelPlate"]);
        }

        [Fact]
        public void FilterCargo_SumsRepeatedKeysAcrossInventories()
        {
            var accepted = new Dictionary<string, long>();
            var refused = new List<string>();
            bool ok = ShipAssetRules.FilterCargo(
                Census("Ore:Iron", 100, "Ore:Iron", 250), accepted, refused);

            Assert.True(ok);
            Assert.Equal(350L, accepted["Ore:Iron"]);
        }

        [Fact]
        public void FilterCargo_SkipsGasWithoutRefusing()
        {
            var accepted = new Dictionary<string, long>();
            var refused = new List<string>();
            bool ok = ShipAssetRules.FilterCargo(
                Census("Gas:Hydrogen", 8000, "Ore:Iron", 10), accepted, refused);

            Assert.True(ok);
            Assert.Empty(refused);
            Assert.Single(accepted);
            Assert.Equal(10L, accepted["Ore:Iron"]);
        }

        [Fact]
        public void FilterCargo_RefusesABareSubtypeKey_NamingIt()
        {
            var accepted = new Dictionary<string, long>();
            var refused = new List<string>();
            // What InventoryKeyMapper emits for ammunition and bottles: no family prefix at all.
            bool ok = ShipAssetRules.FilterCargo(
                Census("NATO_25x184mm", 200, "HydrogenBottle", 1, "Ore:Iron", 10),
                accepted, refused);

            Assert.False(ok);
            Assert.Equal(2, refused.Count);
            Assert.Contains("NATO_25x184mm", refused);
            Assert.Contains("HydrogenBottle", refused);
        }

        [Fact]
        public void FilterCargo_RefusedKeysAreDeduplicatedAndOrdered()
        {
            var refused = new List<string>();
            ShipAssetRules.FilterCargo(
                Census("Zzz", 1, "AAA", 2, "Zzz", 3), null, refused);

            Assert.Equal(new[] { "AAA", "Zzz" }, refused);
        }

        [Fact]
        public void FilterCargo_EmptyCensus_IsAccepted()
        {
            var accepted = new Dictionary<string, long>();
            var refused = new List<string>();
            Assert.True(ShipAssetRules.FilterCargo(Census(), accepted, refused));
            Assert.True(ShipAssetRules.FilterCargo(null, accepted, refused));
            Assert.Empty(accepted);
            Assert.Empty(refused);
        }

        [Fact]
        public void IsAcceptedCargoKey_MatchesOnThePrefixNotTheSubtype()
        {
            Assert.True(ShipAssetRules.IsAcceptedCargoKey("Ore:Stone"));
            Assert.True(ShipAssetRules.IsAcceptedCargoKey("Ingot:Stone"));
            Assert.True(ShipAssetRules.IsAcceptedCargoKey("Comp:Construction"));
            Assert.False(ShipAssetRules.IsAcceptedCargoKey("Stone"));
            Assert.False(ShipAssetRules.IsAcceptedCargoKey("Gas:Oxygen"));
            Assert.False(ShipAssetRules.IsAcceptedCargoKey(""));
            Assert.False(ShipAssetRules.IsAcceptedCargoKey(null));
        }
    }
}
