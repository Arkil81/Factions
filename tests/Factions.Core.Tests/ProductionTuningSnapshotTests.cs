using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionTuningSnapshotTests
    {
        [Fact]
        public void Capture_DeepCopiesThroughputYieldInputs()
        {
            var row = new Dictionary<string, double>
            {
                { ItemKind.Ore.ToString(), 0.5 },
                { ItemKind.RareOre.ToString(), 0.25 }
            };
            var tuning = new FactionTuning
            {
                FreeMiningRunIntervalTicks = 30,
                FreeMinerCargoVolumeLiters = 3000,
                OreYield = 100,
                RareOreYield = 40,
                FreeMintScale = row
            };

            ProductionTuningSnapshot snapshot = ProductionTuningSnapshot.Capture(tuning);

            row[ItemKind.Ore.ToString()] = 0.1;
            tuning.FreeMiningRunIntervalTicks = 1;
            tuning.FreeMinerCargoVolumeLiters = 1;
            tuning.OreYield = 1;
            tuning.RareOreYield = 1;

            Assert.Equal(30, snapshot.FreeMiningRunIntervalTicks);
            Assert.Equal(3000, snapshot.FreeMinerCargoVolumeLiters);
            Assert.Equal(100, snapshot.OreYield);
            Assert.Equal(40, snapshot.RareOreYield);
            Assert.Equal(0.5, snapshot.FreeMintScaleFor(ItemKind.Ore));
            Assert.Equal(0.25, snapshot.FreeMintScaleFor(ItemKind.RareOre));
        }

        [Fact]
        public void Capture_FreezesEveryProductionDecisionValue()
        {
            var tuning = new FactionTuning
            {
                EconomyIntervalSeconds = 30.0,
                FoundHoldThreshold = 0.6,
                FoundItemCostProcessedGood = 400,
                StoreListingCapBulk = 1000,
                StoreListingCapWeapon = 10,
                StoreListingCapAmmunition = 50,
                StoreListingCapConsumable = 5,
                CatalogDemandMissingFraction = 0.1,
                CatalogDemandMinFraction = 0.01,
                CatalogDemandHysteresis = 0.2,
                ProductionSurplusIntentEnabled = true,
                ProductionSurplusTargetMultiple = 2.5
            };

            ProductionTuningSnapshot snapshot = ProductionTuningSnapshot.Capture(tuning);

            tuning.EconomyIntervalSeconds = 60.0;
            tuning.FoundHoldThreshold = 0.1;
            tuning.FoundItemCostProcessedGood = 1;
            tuning.StoreListingCapBulk = 1;
            tuning.StoreListingCapWeapon = 1;
            tuning.StoreListingCapAmmunition = 1;
            tuning.StoreListingCapConsumable = 1;
            tuning.CatalogDemandMissingFraction = 0.9;
            tuning.CatalogDemandMinFraction = 0.9;
            tuning.CatalogDemandHysteresis = 0.9;
            tuning.ProductionSurplusIntentEnabled = false;
            tuning.ProductionSurplusTargetMultiple = 1.0;

            Assert.Equal(30.0, snapshot.EconomyIntervalSeconds);
            Assert.Equal(0.6, snapshot.FoundHoldThreshold);
            Assert.Equal(400, snapshot.FoundItemCostProcessedGood);
            Assert.Equal(1000, snapshot.StoreListingCapForItemKind(ItemKind.Ingot, ProcessedGoodCategory.Component));
            Assert.Equal(10, snapshot.StoreListingCapForItemKind(ItemKind.ProcessedGood, ProcessedGoodCategory.Weapon));
            Assert.Equal(50, snapshot.StoreListingCapForItemKind(ItemKind.ProcessedGood, ProcessedGoodCategory.Ammunition));
            Assert.Equal(5, snapshot.StoreListingCapForItemKind(ItemKind.ProcessedGood, ProcessedGoodCategory.Consumable));
            Assert.Equal(0.1, snapshot.CatalogDemandMissingFraction);
            Assert.Equal(0.01, snapshot.CatalogDemandMinFraction);
            Assert.Equal(0.2, snapshot.CatalogDemandHysteresis);
            Assert.True(snapshot.ProductionSurplusIntentEnabled);
            Assert.Equal(2.5, snapshot.ProductionSurplusTargetMultiple);
        }
    }
}
