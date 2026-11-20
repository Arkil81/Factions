using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a cargo hull costs to fly one job, so the hull choice can prefer the one that
    /// moves the load for the least fuel rather than the one with the largest hold.
    ///
    /// <para><b>The ranking is fuel credits per litre carried.</b> Burn is linear in mass, so for a
    /// load every candidate can hold, the lightest hull wins; for a load larger than the small hulls,
    /// a big hull's lower cost per litre wins. No size rule is written down — it falls out of the
    /// burn. The load is <c>min(hold, available)</c>, and the burn is priced at dry mass plus that
    /// load's mass, on the straight source-to-destination leg: every candidate flies the same
    /// geometry, so the ranking needs the profile's shape, not the route chooser's detour.</para>
    ///
    /// <para><b>Fuel is valued at the departing node</b>: the item's price times the node's scarcity
    /// multiplier, so a node sitting on its fuel flies it freely and one short of it prefers the hull
    /// that burns less. Never the import price, which is 0 while the import fallback is off.</para>
    ///
    /// <para>A leg that costs nothing — battery and FSD only, in deep space — ties every hull at 0.
    /// <see cref="Fit"/> breaks that tie: the smallest hold that takes the whole load, otherwise the
    /// largest. An unscanned hull (no dry mass) ranks after every priced one and is still eligible:
    /// unknown is not a gate.</para>
    ///
    /// <para><b>A gas job measures tanks, not the hold.</b> With <see cref="GasKey"/> set (gas
    /// travelling in tanks, <see cref="GasCarriage.InTanks"/>), a hull's capacity is its tank room for
    /// that gas on this leg — tanks less the leg's own burn (<see cref="GasCarriage.HullRoomLitres"/>) —
    /// and <see cref="CarriedLiters"/>, <see cref="Fit"/>, <see cref="MaxCargoLiters"/> and
    /// <see cref="Carries"/> all read that figure. A hull with no room for the gas does not carry
    /// the job and is not eligible for it.</para></summary>
    public sealed class HaulHullCost
    {
        private readonly WorldModel _world;
        private readonly FactionTuning _tuning;
        private readonly string _fuelNodeId;
        private readonly LegImpulseProfile _leg;
        private readonly LegImpulseProfile _ferry;
        private readonly Vec3 _fromKm;
        private readonly Vec3 _toKm;
        private readonly double _availableLiters;
        private readonly double _cargoKgPerLiter;
        private readonly Dictionary<string, double> _costs = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _gasRoom = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Rank for a hull that has no price: after every priced hull, still eligible.</summary>
        public const double UnpricedCost = double.MaxValue;

        /// <summary>Largest hold the job may take, or 0 for any. A right-sized fetch sets it so the
        /// donor sends the hull the search found room for, not whichever of its hulls ranks first:
        /// the specialisation tiers put a plain freighter ahead of an armed courier.</summary>
        public double MaxCargoLiters { get; set; }

        /// <summary>Kilograms per litre of a bulk load whose hull is chosen by how much it carries, or 0
        /// for none. Set, a hold job's capacity (<see cref="CapacityLiters"/>) is the smaller of the hold
        /// and what the hull can lift on the loaded leg (<see cref="PendingFleetResolver.LoadedLiftKg"/>
        /// over this density), so <see cref="Fit"/> does not pick a hold the hull could only fill part
        /// of: live 2026-09-29, a Brokkr-Multi flying ice was <c>cargo_mass_trimmed</c> to 7,499 units.
        /// Not read for a gas job.</summary>
        public double LiftKgPerLiter { get; set; }

        private readonly Dictionary<string, double> _liftLiters = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>The gas this job carries in tanks, or null for a job that rides in the hold. Set
        /// only when <see cref="GasCarriage.InTanks"/> holds for the job's item.</summary>
        public string GasKey { get; set; }

        /// <param name="fuelNodeId">Where the hull draws its fuel — the node it departs from.</param>
        /// <param name="fromKm">Where the load is taken aboard.</param>
        /// <param name="toKm">Where it is delivered.</param>
        /// <param name="availableLiters">Litres the job could fill: the vector plus what would fold
        /// into the same hold at the loading node.</param>
        /// <param name="cargoKgPerLiter">Density of the load; 0 prices the leg dry.</param>
        /// <param name="ferryFromKm">Where a fetched hull starts, flying empty to <paramref name="fromKm"/>
        /// before it loads. Null for a hull already standing at the loading node.</param>
        public HaulHullCost(WorldModel world, FactionTuning tuning, string fuelNodeId, Vec3 fromKm, Vec3 toKm,
                            double availableLiters, double cargoKgPerLiter, Vec3? ferryFromKm = null)
            : this(world, tuning, fuelNodeId, fromKm, toKm, availableLiters, cargoKgPerLiter, ferryFromKm, true)
        {
        }

        private HaulHullCost(WorldModel world, FactionTuning tuning, string fuelNodeId, Vec3 fromKm, Vec3 toKm,
                             double availableLiters, double cargoKgPerLiter, Vec3? ferryFromKm, bool priced)
        {
            _world = world;
            _tuning = tuning;
            _fuelNodeId = fuelNodeId;
            _fromKm = fromKm;
            _toKm = toKm;
            _availableLiters = availableLiters;
            _cargoKgPerLiter = cargoKgPerLiter > 0 ? cargoKgPerLiter : 0;
            if (!priced) return;
            _leg = Profile(world, tuning, fromKm, toKm);
            if (ferryFromKm.HasValue) _ferry = Profile(world, tuning, ferryFromKm.Value, fromKm);
        }

        /// <summary>A ranking with no fuel price: every hull costs <see cref="UnpricedCost"/>, so
        /// <see cref="Fit"/> alone decides. For a tank-carried gas job when the fuel ranking is off,
        /// which still has to measure tanks rather than the hold.</summary>
        public static HaulHullCost Unpriced(WorldModel world, FactionTuning tuning, Vec3 fromKm, Vec3 toKm,
                                            double availableLiters)
        {
            return new HaulHullCost(world, tuning, null, fromKm, toKm, availableLiters, 0, null, false);
        }

        /// <summary>What this job measures a hull by: its hold, capped by its lift when
        /// <see cref="LiftKgPerLiter"/> is set, or with <see cref="GasKey"/> set its tank room for that
        /// gas on this leg.</summary>
        public double CapacityLiters(IPrefabCatalogue cat, string prefabId)
        {
            if (GasKey != null) return GasRoomLiters(prefabId);
            double hold = cat != null ? cat.CargoLiters(prefabId) : 0;
            if (LiftKgPerLiter <= 0 || hold <= 0) return hold;
            double lift = LiftLiters(prefabId);
            return lift < hold ? lift : hold;
        }

        /// <summary>Litres of the load the hull can lift on the loaded leg; +Infinity when unscanned.</summary>
        private double LiftLiters(string prefabId)
        {
            if (prefabId == null) return double.PositiveInfinity;
            double liters;
            if (_liftLiters.TryGetValue(prefabId, out liters)) return liters;
            double kg = PendingFleetResolver.LoadedLiftKg(_world, _tuning, prefabId, _fromKm, _toKm);
            liters = double.IsPositiveInfinity(kg) ? kg : (kg > 0 ? kg / LiftKgPerLiter : 0);
            _liftLiters[prefabId] = liters;
            return liters;
        }

        /// <summary>Whether a hull can carry this job at all. Always true for a hold job, whose
        /// eligibility is <see cref="ShipPool.IsSuitableFor"/>'s; for a gas job, whether the hull has
        /// tank room for the gas on this leg.</summary>
        public bool Carries(string prefabId)
        {
            return GasKey == null || GasRoomLiters(prefabId) > 0;
        }

        private long GasRoomLiters(string prefabId)
        {
            if (prefabId == null) return 0;
            long room;
            if (_gasRoom.TryGetValue(prefabId, out room)) return room;
            room = GasCarriage.HullRoomLitres(_world, _tuning, prefabId, GasKey, _fromKm, _toKm);
            _gasRoom[prefabId] = room;
            return room;
        }

        /// <summary>Litres this hull would carry: its capacity (<see cref="CapacityLiters"/>), or the
        /// whole available load when smaller. An unknown available load reads as a full hold.</summary>
        public double CarriedLiters(string prefabId)
        {
            IPrefabCatalogue cat = _world != null ? _world.PrefabCatalogue : null;
            double cap = CapacityLiters(cat, prefabId);
            if (cap <= 0) return 0;
            return _availableLiters > 0 && _availableLiters < cap ? _availableLiters : cap;
        }

        /// <summary>Fuel credits per litre carried; lower is better. <see cref="UnpricedCost"/> when
        /// the hull or the leg cannot be priced.</summary>
        public double CostPerLiter(string prefabId)
        {
            if (prefabId == null) return UnpricedCost;
            double cached;
            if (_costs.TryGetValue(prefabId, out cached)) return cached;

            double cost = UnpricedCost;
            double carried = CarriedLiters(prefabId);
            double credits = FuelCredits(prefabId, carried);
            if (carried > 0 && credits >= 0) cost = credits / carried;
            _costs[prefabId] = cost;
            return cost;
        }

        /// <summary>Tie-break, higher is better: every capacity (<see cref="CapacityLiters"/>) that
        /// takes the whole available load ranks above every one that does not; among those that do
        /// the smaller wins, among those that do not the larger.</summary>
        public double Fit(string prefabId)
        {
            IPrefabCatalogue cat = _world != null ? _world.PrefabCatalogue : null;
            double cap = CapacityLiters(cat, prefabId);
            if (cap <= 0) return double.MinValue;
            return cap >= _availableLiters ? 1e15 - cap : cap;
        }

        /// <summary>Fuel credits for one hull flying this job, or -1 when it cannot be priced.</summary>
        public double FuelCredits(string prefabId, double carriedLiters)
        {
            IPrefabCatalogue cat = _world != null ? _world.PrefabCatalogue : null;
            if (cat == null || _tuning == null || _leg == null) return -1;
            double dryKg = cat.DryMassKg(prefabId);
            if (dryKg <= 0) return -1;

            var fuelCat = cat as IFuelPrefabCatalogue;
            FuelManifest manifest = fuelCat != null ? fuelCat.FuelManifest(prefabId) : null;
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = cat.ThrustBands(prefabId);

            HullFrame frame = FrameOf(cat, prefabId);
            double loadedKg = dryKg + carriedLiters * _cargoKgPerLiter;
            double credits = Credits(FuelBurn.ForLeg(_leg, loadedKg, bands, manifest, _tuning, frame));
            if (_ferry != null) credits += Credits(FuelBurn.ForLeg(_ferry, dryKg, bands, manifest, _tuning, frame));
            return credits;
        }

        /// <summary>Fuel credits for one hull flying the convoy's whole itinerary: the ferry leg dry
        /// when there is one, then each leg between consecutive <paramref name="legEndsKm"/> at dry mass
        /// plus <paramref name="carriedLiters"/>. Each stop is its own leg because the fuel model bills
        /// each one its own departure and arrival. The load is held at its full planned size
        /// throughout, so a convoy collecting on the way is priced a little high rather than low.
        /// -1 when the hull cannot be priced.</summary>
        public double FuelCreditsAlong(string prefabId, double carriedLiters, IList<Vec3> legEndsKm)
        {
            IPrefabCatalogue cat = _world != null ? _world.PrefabCatalogue : null;
            if (cat == null || _tuning == null || legEndsKm == null || legEndsKm.Count < 2) return -1;
            double dryKg = cat.DryMassKg(prefabId);
            if (dryKg <= 0) return -1;

            var fuelCat = cat as IFuelPrefabCatalogue;
            FuelManifest manifest = fuelCat != null ? fuelCat.FuelManifest(prefabId) : null;
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = cat.ThrustBands(prefabId);
            double loadedKg = dryKg + carriedLiters * _cargoKgPerLiter;
            HullFrame frame = FrameOf(cat, prefabId);

            double credits = 0;
            if (_ferry != null) credits += Credits(FuelBurn.ForLeg(_ferry, dryKg, bands, manifest, _tuning, frame));
            for (int i = 1; i < legEndsKm.Count; i++)
            {
                LegImpulseProfile leg = Profile(_world, _tuning, legEndsKm[i - 1], legEndsKm[i]);
                if (leg != null) credits += Credits(FuelBurn.ForLeg(leg, loadedKg, bands, manifest, _tuning, frame));
            }
            return credits;
        }

        private static HullFrame FrameOf(IPrefabCatalogue cat, string prefabId)
        {
            var frames = cat as IHullFramePrefabCatalogue;
            return frames != null ? frames.HullFrame(prefabId) : null;
        }

        private double Credits(LegBurn burn)
        {
            return CreditsOf(_world, _fuelNodeId, burn);
        }

        /// <summary>What <paramref name="burn"/> is worth at <paramref name="fuelNodeId"/>, the node the
        /// hull departs from: each fuel's unit value times that node's scarcity of it. The valuation the
        /// convoy gate and route pricing (<see cref="TransitPlanner"/>) both read.</summary>
        public static double CreditsOf(WorldModel world, string fuelNodeId, LegBurn burn)
        {
            if (burn == null) return 0;
            double credits = 0;
            foreach (KeyValuePair<string, double> gas in burn.GasLitres)
                credits += Value(world, fuelNodeId, gas.Key, gas.Value);
            if (burn.ReactorFuelKg > 0 && !string.IsNullOrEmpty(burn.ReactorFuelKey))
                credits += Value(world, fuelNodeId, burn.ReactorFuelKey, burn.ReactorFuelKg);
            return credits;
        }

        /// <summary>Credits <paramref name="units"/> of a fuel key are worth at the departing node, in
        /// doubles: a reactor burn is routinely a fraction of a kilogram, which a whole-unit valuation
        /// would round to nothing.</summary>
        private static double Value(WorldModel world, string fuelNodeId, string key, double units)
        {
            if (units <= 0 || world == null) return 0;
            double unitPrice = world.ItemUnitValue(ItemPriceResolver.StripInventoryPrefix(key));
            if (unitPrice <= 0) return 0;
            if (world.PriceOracle != null && fuelNodeId != null)
            {
                double scarcity = world.PriceOracle.ScarcityMultiplier(fuelNodeId, key);
                if (scarcity > 0) unitPrice *= scarcity;
            }
            return units * unitPrice / ItemCredits.UnitsPerPricedUnit(key);
        }

        private static LegImpulseProfile Profile(WorldModel world, FactionTuning tuning, Vec3 fromKm, Vec3 toKm)
        {
            if (world == null || tuning == null || world.FuelLedger == null) return null;
            var polyline = new List<Vec3>(2) { fromKm, toKm };
            bool hit;
            return world.FuelLedger.Profiles.Get(world.Planets, tuning, polyline, out hit);
        }
    }
}
