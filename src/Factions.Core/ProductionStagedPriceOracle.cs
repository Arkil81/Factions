using System;

namespace Factions.Core
{
    /// <summary>
    /// Price-oracle adapter for a Production journal. Reference stock remains delegated to the live
    /// oracle because capability/reference metadata is immutable under the cycle guard, while every
    /// concrete-item scarcity answer reads the journal's current staged inventory. It intentionally
    /// retains no answer cache: Production may create or consume the item earlier in the same cycle.
    /// </summary>
    public sealed class ProductionStagedPriceOracle : IPriceOracle, IOfferKeepBack, IScarcityAtStock
    {
        private readonly IPriceOracle _source;
        private readonly ProductionStagedWorldEffects _effects;
        private readonly double _scarcityMin;
        private readonly double _scarcityMax;

        public ProductionStagedPriceOracle(IPriceOracle source, ProductionStagedWorldEffects effects,
                                           double scarcityMin, double scarcityMax)
        {
            _source = source ?? NullPriceOracle.Instance;
            _effects = effects ?? throw new ArgumentNullException(nameof(effects));
            _scarcityMin = scarcityMin;
            _scarcityMax = scarcityMax;
        }

        public double ScarcityMultiplier(string nodeId, string inventoryKey)
        {
            return ScarcityAtStock(nodeId, inventoryKey, _effects.GetNodeInventory(nodeId, inventoryKey));
        }

        public double ScarcityAtStock(string nodeId, string inventoryKey, long stock)
        {
            long reference;
            if (!TryGetReferenceStock(nodeId, inventoryKey, out reference) || reference <= 0) return 1.0;
            return ScarcityCurve.At(reference, stock, _scarcityMin, _scarcityMax);
        }

        public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
        {
            return _source.TryGetReferenceStock(nodeId, inventoryKey, out reference);
        }

        public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target)
        {
            return _source.TryGetMeasuredTargetStock(nodeId, inventoryKey, out target);
        }

        /// <summary>The source's keep-back, or its reference stock when the source has none — the
        /// same fallback <see cref="StationListings"/> applies to an oracle without one.</summary>
        public bool TryGetKeepBackStock(string nodeId, string inventoryKey, out long keepBack)
        {
            var source = _source as IOfferKeepBack;
            return source != null
                ? source.TryGetKeepBackStock(nodeId, inventoryKey, out keepBack)
                : _source.TryGetReferenceStock(nodeId, inventoryKey, out keepBack);
        }
    }
}
