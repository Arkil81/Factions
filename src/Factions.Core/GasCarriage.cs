using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>How much gas a convoy can carry as cargo. Gas travels in tanks, never in the hold:
    /// every loader caps a gas key by <see cref="RoomLitres"/> and a solid key by the hold, and gas
    /// aboard takes none of the hold's volume. One rule, read by every loader, so a convoy's gas
    /// capacity is the same number whichever path boards it.
    ///
    /// <para><b>The rule.</b> A convoy carries a gas only in tanks built for that gas: the prefab's
    /// <see cref="IPrefabCatalogue.GasTankLiters"/> for the key, summed over every hull flying.
    /// A hull with no tank for the gas carries none of it — there is no hold fallback. Of those
    /// litres, the leg's own burn of the same gas is kept back: the hull fills its tanks at
    /// departure for the burn (<see cref="FuelDraw"/> still takes that from the departing node's
    /// stock) and does not burn its cargo, so what the tanks hold beyond the burn is what can be
    /// carried. Gas units are litres, so the result is a unit count.</para>
    ///
    /// <para>SE-free and pure apart from the leg-profile cache <see cref="FuelMeter.BurnForPolyline"/>
    /// already fills for dispatch pricing.</para></summary>
    public static class GasCarriage
    {
        /// <summary>Litres of tank capacity for <paramref name="gasKey"/> on one hull of
        /// <paramref name="prefabId"/>. 0 for an unknown or tankless prefab, or one whose tanks
        /// hold a different gas.</summary>
        public static long TankLitres(IPrefabCatalogue catalogue, string prefabId, string gasKey)
        {
            if (catalogue == null || string.IsNullOrEmpty(prefabId) || string.IsNullOrEmpty(gasKey)) return 0;
            IReadOnlyDictionary<string, long> tanks = catalogue.GasTankLiters(prefabId);
            if (tanks == null || tanks.Count == 0) return 0;
            long litres;
            if (tanks.TryGetValue(gasKey, out litres)) return litres > 0 ? litres : 0;
            foreach (KeyValuePair<string, long> tank in tanks)
                if (string.Equals(tank.Key, gasKey, StringComparison.OrdinalIgnoreCase))
                    return tank.Value > 0 ? tank.Value : 0;
            return 0;
        }

        /// <summary>Litres of tank capacity for <paramref name="gasKey"/> across every hull
        /// <paramref name="fleet"/> flies: each squadron member once, otherwise
        /// <see cref="PendingFleetAction.ShipPrefabId"/> once per <see cref="PendingFleetAction.ConvoySize"/>
        /// (floored at 1) — the same hull reading the leg's burn is priced over.</summary>
        public static long TankLitres(IPrefabCatalogue catalogue, PendingFleetAction fleet, string gasKey)
        {
            if (catalogue == null || string.IsNullOrEmpty(gasKey)) return 0;
            List<string> hulls = FuelMeter.HullsOf(fleet);
            long total = 0;
            for (int i = 0; i < hulls.Count; i++) total += TankLitres(catalogue, hulls[i], gasKey);
            return total;
        }

        /// <summary>Litres of <paramref name="gasKey"/> the convoy's own flight of the leg
        /// <paramref name="fromKm"/> → <paramref name="toKm"/> burns, summed over its hulls and priced
        /// with the cargo already aboard. The straight leg, not a routed detour: the load is decided
        /// before the leg is planned.
        ///
        /// <para>0 when the burn is unknown or unpriced — fuel pricing off
        /// (<see cref="FuelDrawModes.Off"/>), an immediate itinerary that is never flown, no fleet
        /// speed, no ledger, or no band with burn data. The caller then has the full tank for cargo.</para></summary>
        public static long LegBurnLitres(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                         string gasKey, Vec3 fromKm, Vec3 toKm)
        {
            if (world == null || tuning == null || string.IsNullOrEmpty(gasKey)) return 0;
            if (tuning.FuelDrawMode == FuelDrawModes.Off) return 0;
            if (fleet.ImmediateItinerary || tuning.FleetSpeedKmPerMin <= 0) return 0;

            LegBurn burn = FuelMeter.BurnForPolyline(world, tuning, fleet, new[] { fromKm, toKm });
            if (burn == null || !burn.HasBurnData) return 0;
            double litres;
            if (!burn.GasLitres.TryGetValue(gasKey, out litres) || litres <= 0) return 0;
            return (long)Math.Ceiling(litres);
        }

        /// <summary>Whether cargo of <paramref name="itemKey"/> travels in tanks rather than the hold:
        /// the key is a gas. The one test every hull-choice and load-gate site asks, so none of them
        /// draws the line differently.</summary>
        public static bool InTanks(string itemKey)
        {
            return InventoryKeyMapper.IsGasKey(itemKey);
        }

        /// <summary><see cref="RoomLitres"/> for one empty hull of <paramref name="prefabId"/>: what a
        /// candidate could take aboard on the leg before it is chosen. The hull-choice reading of the
        /// rule the loaders apply, so a hull ranked as carrying the load is one that boards it.</summary>
        public static long HullRoomLitres(WorldModel world, FactionTuning tuning, string prefabId,
                                          string gasKey, Vec3 fromKm, Vec3 toKm)
        {
            if (string.IsNullOrEmpty(prefabId)) return 0;
            var hull = new PendingFleetAction { ShipPrefabId = prefabId, ConvoySize = 1 };
            return RoomLitres(world, tuning, hull, gasKey, fromKm, toKm);
        }

        /// <summary>Litres of <paramref name="gasKey"/> the convoy can still take aboard as cargo for
        /// the leg <paramref name="fromKm"/> → <paramref name="toKm"/>: its tanks for that gas, less
        /// the leg's burn of it (<see cref="LegBurnLitres"/>; the full tank when that is unknown or
        /// unpriced), less what of that gas is already in <see cref="PendingFleetAction.Cargo"/>.
        /// Never negative; 0 for a gas the convoy has no tank for.</summary>
        public static long RoomLitres(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                      string gasKey, Vec3 fromKm, Vec3 toKm)
        {
            if (world == null) return 0;
            long tanks = TankLitres(world.PrefabCatalogue, fleet, gasKey);
            if (tanks <= 0) return 0;

            long room = tanks - LegBurnLitres(world, tuning, fleet, gasKey, fromKm, toKm);
            long aboard;
            if (fleet.Cargo != null && fleet.Cargo.TryGetValue(gasKey, out aboard) && aboard > 0)
                room -= aboard;
            return room > 0 ? room : 0;
        }
    }
}
