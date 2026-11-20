using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Immutable copy of every mutable <see cref="FactionTuning"/> value read by
    /// <see cref="ProductionManager"/>. A staged Production cycle captures one at begin time so a
    /// tuning reload cannot change its decisions before its guarded atomic commit.
    /// </summary>
    public sealed class ProductionTuningSnapshot
    {
        private readonly Dictionary<string, double> _freeMintScale;

        private ProductionTuningSnapshot(FactionTuning tuning)
        {
            EconomyIntervalSeconds = tuning.EconomyIntervalSeconds;
            FreeMiningRunIntervalTicks = tuning.FreeMiningRunIntervalTicks;
            FreeMinerCargoVolumeLiters = tuning.FreeMinerCargoVolumeLiters;
            OreYield = tuning.OreYield;
            RareOreYield = tuning.RareOreYield;
            _freeMintScale = CopyScales(tuning.FreeMintScale);

            FoundHoldThreshold = tuning.FoundHoldThreshold;
            FoundItemCostProcessedGood = tuning.FoundItemCostProcessedGood;

            StoreListingCapBulk = tuning.StoreListingCapBulk;
            StoreListingCapWeapon = tuning.StoreListingCapWeapon;
            StoreListingCapAmmunition = tuning.StoreListingCapAmmunition;
            StoreListingCapConsumable = tuning.StoreListingCapConsumable;

            CatalogDemandMissingFraction = tuning.CatalogDemandMissingFraction;
            CatalogDemandMinFraction = tuning.CatalogDemandMinFraction;
            CatalogDemandHysteresis = tuning.CatalogDemandHysteresis;
            ProductionSurplusIntentEnabled = tuning.ProductionSurplusIntentEnabled;
            ProductionSurplusTargetMultiple = tuning.ProductionSurplusTargetMultiple;

            ProductionProductivityBonus = tuning.ProductionProductivityBonus;
            ProductionSlotInputHorizonSeconds = tuning.ProductionSlotInputHorizonSeconds;
            ProductionCreditCapIntervals = tuning.ProductionCreditCapIntervals;
            ProductionIntervalSeconds = tuning.ProductionIntervalSeconds;
        }

        public double EconomyIntervalSeconds { get; private set; }
        public int FreeMiningRunIntervalTicks { get; private set; }
        public long FreeMinerCargoVolumeLiters { get; private set; }
        public long OreYield { get; private set; }
        public long RareOreYield { get; private set; }
        public double FoundHoldThreshold { get; private set; }
        public long FoundItemCostProcessedGood { get; private set; }

        public long StoreListingCapBulk { get; private set; }
        public long StoreListingCapWeapon { get; private set; }
        public long StoreListingCapAmmunition { get; private set; }
        public long StoreListingCapConsumable { get; private set; }

        public double CatalogDemandMissingFraction { get; private set; }
        public double CatalogDemandMinFraction { get; private set; }
        public double CatalogDemandHysteresis { get; private set; }
        public bool ProductionSurplusIntentEnabled { get; private set; }
        public double ProductionSurplusTargetMultiple { get; private set; }

        public double ProductionProductivityBonus { get; private set; }
        public double ProductionSlotInputHorizonSeconds { get; private set; }
        public double ProductionCreditCapIntervals { get; private set; }
        public double ProductionIntervalSeconds { get; private set; }

        public static ProductionTuningSnapshot Capture(FactionTuning tuning)
        {
            if (tuning == null) throw new ArgumentNullException(nameof(tuning));
            return new ProductionTuningSnapshot(tuning);
        }

        /// <summary>The global free-mint multiplier for <paramref name="itemKind"/>, snapshotted so a
        /// resumed production cycle keeps the value it started with. Ungated by design: the deposit
        /// test that gates ore lives on the node, not in this snapshot.</summary>
        public double FreeMintScaleFor(ItemKind itemKind)
        {
            if (_freeMintScale == null) return 0;
            double value;
            return _freeMintScale.TryGetValue(itemKind.ToString(), out value) ? value : 0;
        }

        private static Dictionary<string, double> CopyScales(Dictionary<string, double> source)
        {
            if (source == null) return null;
            return new Dictionary<string, double>(source, StringComparer.Ordinal);
        }

        public long StoreListingCapForItemKind(ItemKind kind, ProcessedGoodCategory category)
        {
            switch (kind)
            {
                case ItemKind.Ore:
                case ItemKind.RareOre:
                case ItemKind.Ingot:
                    return StoreListingCapBulk;
                case ItemKind.ProcessedGood:
                    switch (category)
                    {
                        case ProcessedGoodCategory.Weapon: return StoreListingCapWeapon;
                        case ProcessedGoodCategory.Ammunition: return StoreListingCapAmmunition;
                        case ProcessedGoodCategory.Consumable: return StoreListingCapConsumable;
                        default: return StoreListingCapBulk;
                    }
                default:
                    return StoreListingCapConsumable;
            }
        }
    }
}
