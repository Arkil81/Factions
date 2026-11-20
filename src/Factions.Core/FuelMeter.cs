using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>Values of <see cref="FactionTuning.FuelDrawMode"/>. Plain integers rather than an
    /// enum so the field round-trips through <c>tuning.json</c> as the number the defaults file
    /// carries.</summary>
    public static class FuelDrawModes
    {
        /// <summary>Nothing is priced and nothing is recorded.</summary>
        public const int Off = 0;
        /// <summary>Every leg is priced into <see cref="FuelLedger"/>. No world effect.</summary>
        public const int Measure = 1;
        /// <summary>Everything <see cref="Measure"/> does, and then the burn is taken out of the
        /// departing node's stock, the shortfall bought through the treasury, and a reserve posted
        /// at every founded node.</summary>
        public const int Draw = 2;

        public static string Name(int mode)
        {
            if (mode == Off) return "off";
            if (mode == Draw) return "draw";
            return "measure";
        }
    }

    /// <summary>Prices one flown leg and files it. The single entry point every dispatch site uses,
    /// so the sites cannot drift in what they charge or in what they skip.
    ///
    /// <para>docs/fuel-plan.md's slice 1 in measure-only mode: the model is priced against the
    /// server's own traffic before anything is drawn, because the Sim's traffic mix is not the
    /// server's and the balance question cannot be answered from a simulated day.
    /// <b>Nothing here touches inventory, treasury, demand or a dispatch decision.</b></para>
    ///
    /// <para>The cost is one cached <see cref="LegImpulseProfile"/> lookup plus per-hull
    /// arithmetic. <see cref="FactionTuning.FuelDrawMode"/> 0 returns before any of it.</para></summary>
    public static class FuelMeter
    {
        /// <summary>The straight leg between two points — what every site that does not route
        /// flies.</summary>
        public static void PriceLeg(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                    Vec3 fromKm, Vec3 toKm, IList<string> log, IFuelLegSink sink)
        {
            PriceLeg(world, tuning, fleet, fromKm, toKm, log, sink, null);
        }

        /// <summary>The same, told which node the leg departs from so
        /// <see cref="FuelDrawModes.Draw"/> can take the burn out of its stock. Null draws nothing,
        /// which is every mode below Draw and every site with no resolved origin.</summary>
        public static void PriceLeg(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                    Vec3 fromKm, Vec3 toKm, IList<string> log, IFuelLegSink sink,
                                    SpatialNode originNode)
        {
            if (tuning == null || tuning.FuelDrawMode == FuelDrawModes.Off) return;
            PriceLeg(world, tuning, fleet, new[] { fromKm, toKm }, log, sink, originNode);
        }

        /// <summary>The whole polyline of a routed clearance chain, priced once when the chain is
        /// planned. Its chords are not priced again as they are flown: the chain is one leg with
        /// one acceleration profile, and charging each chord a full accelerate-and-brake pair would
        /// bill a smooth detour several times over.</summary>
        public static void PriceLeg(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                    IList<Vec3> polylineKm, IList<string> log, IFuelLegSink sink)
        {
            PriceLeg(world, tuning, fleet, polylineKm, log, sink, null);
        }

        /// <summary>The same, told which node the chain departs from. A chain is drawn for once, at
        /// its start, exactly as it is priced once.</summary>
        public static void PriceLeg(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                    IList<Vec3> polylineKm, IList<string> log, IFuelLegSink sink,
                                    SpatialNode originNode)
        {
            if (tuning == null || tuning.FuelDrawMode == FuelDrawModes.Off) return;
            if (world == null || world.FuelLedger == null) return;
            // An immediate itinerary is never flown -- it resolves the tick it departs -- so there
            // is no leg to charge.
            if (fleet.ImmediateItinerary || tuning.FleetSpeedKmPerMin <= 0) return;
            if (polylineKm == null || polylineKm.Count < 2) return;

            long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            FuelLedger ledger = world.FuelLedger;

            LegSpeeds speeds = world.FleetSpeeds != null ? world.FleetSpeeds.SpeedsFor(fleet) : LegSpeeds.Default;

            bool cacheHit;
            LegImpulseProfile profile = ledger.Profiles.Get(world.Planets, tuning, polylineKm,
                                                           speeds.ThrusterMs, out cacheHit);

            FuelLegRecord record = PriceConvoy(world, tuning, fleet, profile, polylineKm, speeds);
            record.ProfileCacheHit = cacheHit;
            record.OriginNodeId = originNode != null ? originNode.Id : null;

            if (sink != null)
            {
                // A staged cycle draws at commit, against the stock its own overlay publishes, so a
                // cycle that is discarded and re-run takes the fuel exactly once.
                sink.RecordFuelLeg(record);
            }
            else
            {
                record.Draw = Draw(world, tuning, fleet, originNode, record.Burn, log);
                ledger.Record(record);
            }

            if (DebugTraces.Fuel)
            {
                string line = Format(fleet, record, tuning);
                if (sink != null) sink.AddFuelLog(line);
                else if (log != null) log.Add(line);
            }

            ledger.AddPricingTicks(System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks);
        }

        /// <summary>Takes one leg's burn out of <paramref name="originNode"/> and writes the
        /// <c>FUEL_IMPORTED</c> line when the node could not supply it. The one place the draw and
        /// its line are paired, so the direct and staged dispatch paths cannot report it
        /// differently. Returns null when nothing was drawn.</summary>
        public static FuelDrawResult Draw(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                          SpatialNode originNode, LegBurn burn, IList<string> log)
        {
            if (tuning == null || originNode == null || burn == null) return null;
            if (tuning.FuelDrawMode < FuelDrawModes.Draw)
            {
                FuelDraw.MeterBurn(originNode, burn, world != null ? world.UtcNow : DateTime.MinValue, tuning);
                return null;
            }

            FuelDrawResult draw = FuelDraw.TryFuel(originNode, burn, fleet.ActorTag, world, tuning);
            if (!draw.Any) return null;

            // Only a departure the node could not supply is an event; one drawn wholly from stock is
            // the normal case at ~1.6 dispatches a second and says nothing a total does not.
            if (DebugTraces.Fuel && log != null && ShouldReport(draw))
                log.Add(FormatImport(fleet.LogId, fleet.ActorTag, originNode.Id, draw));
            return draw;
        }

        /// <summary>Whether a draw is worth a line: only a departure the node could not supply in
        /// full. One drawn wholly from stock is the normal case at ~1.6 dispatches a second and says
        /// nothing the ledger total does not.</summary>
        public static bool ShouldReport(FuelDrawResult draw)
        {
            return draw != null
                && (draw.BoughtGasLitres > 0 || draw.BoughtReactorKg > 0
                    || draw.ShortGasLitres > 0 || draw.ShortReactorKg > 0);
        }

        /// <summary>The FUEL_IMPORTED line, or FUEL_SHORT when nothing was bought. <c>drawn=</c> and
        /// <c>bought=</c> are gas units and <c>rkg=</c>/<c>rbought=</c>/<c>rshort=</c> reactor fuel,
        /// so <c>bought ÷ (drawn + bought)</c> reads off the line. <c>short=</c> is fuel the hull
        /// flew without; <c>demanded=</c> counts the keys posted as demand at the node.</summary>
        public static string FormatImport(string logId, string actorTag, string nodeId, FuelDrawResult draw)
        {
            var sb = new StringBuilder(160);
            bool bought = draw.BoughtGasLitres > 0 || draw.BoughtReactorKg > 0;
            sb.Append(bought ? "FUEL_IMPORTED " : "FUEL_SHORT ").Append(logId ?? "?")
              .Append(' ').Append(actorTag ?? "?")
              .Append(" node=").Append(nodeId ?? "?")
              .Append(" drawn=").Append(draw.DrawnGasLitres.ToString(CultureInfo.InvariantCulture))
              .Append(" bought=").Append(draw.BoughtGasLitres.ToString(CultureInfo.InvariantCulture))
              .Append(" short=").Append(draw.ShortGasLitres.ToString(CultureInfo.InvariantCulture))
              .Append(" rkg=").Append(draw.DrawnReactorKg.ToString(CultureInfo.InvariantCulture))
              .Append(" rbought=").Append(draw.BoughtReactorKg.ToString(CultureInfo.InvariantCulture))
              .Append(" rshort=").Append(draw.ShortReactorKg.ToString(CultureInfo.InvariantCulture))
              .Append(" cr=").Append(draw.Credits.ToString(CultureInfo.InvariantCulture));
            if (draw.DemandedKeys > 0)
                sb.Append(" demanded=").Append(draw.DemandedKeys.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Sums the burn over every hull in the convoy. Each hull carries its own bands,
        /// dry mass and fuel manifest, and its share of the cargo in proportion to that dry mass —
        /// burn is linear in mass, but the bands that turn mass into litres are not shared, so the
        /// convoy cannot be priced as one lump.</summary>
        /// <summary>What <paramref name="fleet"/> would burn flying <paramref name="polylineKm"/>, with
        /// nothing recorded, nothing drawn and no line written. For a caller comparing routes before
        /// one is chosen: it is the same hull resolution and the same profile cache the priced path
        /// uses, so a candidate's burn and the burn it is later charged agree by construction.
        /// Null when there is no geometry to price.</summary>
        public static LegBurn BurnForPolyline(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                              IList<Vec3> polylineKm)
        {
            if (world == null || tuning == null || world.FuelLedger == null) return null;
            if (polylineKm == null || polylineKm.Count < 2) return null;

            LegSpeeds speeds = world.FleetSpeeds != null ? world.FleetSpeeds.SpeedsFor(fleet) : LegSpeeds.Default;
            bool cacheHit;
            LegImpulseProfile profile = world.FuelLedger.Profiles.Get(world.Planets, tuning, polylineKm,
                                                                     speeds.ThrusterMs, out cacheHit);
            return PriceConvoy(world, tuning, fleet, profile, polylineKm, speeds).Burn;
        }

        private static FuelLegRecord PriceConvoy(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                                 LegImpulseProfile profile, IList<Vec3> polylineKm, LegSpeeds speeds)
        {
            var record = new FuelLegRecord
            {
                FactionTag = fleet.ActorTag,
                Kind = fleet.Kind,
                LogId = fleet.LogId,
                Burn = new LegBurn(),
                LegKm = PolylineLengthKm(polylineKm),
            };

            IPrefabCatalogue catalogue = world.PrefabCatalogue;
            if (catalogue == null) return record;
            var fuelCatalogue = catalogue as IFuelPrefabCatalogue;
            var frameCatalogue = catalogue as IHullFramePrefabCatalogue;

            List<string> hulls = HullsOf(fleet);
            record.Hulls = hulls.Count;
            if (hulls.Count == 0) return record;

            double dryTotalKg = 0;
            for (int i = 0; i < hulls.Count; i++) dryTotalKg += catalogue.DryMassKg(hulls[i]);

            double cargoMassKg = CargoMassKg(fleet, world.ItemMasses);
            // A hold with contents and no mass for them is an undercount worth counting. An empty
            // hold is not: a sweep flies dry and is priced correctly dry.
            record.DryMassOnly = fleet.Cargo != null && fleet.Cargo.Count > 0 && cargoMassKg <= 0;
            double cargoPerDryKg = dryTotalKg > 0 ? cargoMassKg / dryTotalKg : 0;

            for (int i = 0; i < hulls.Count; i++)
            {
                string prefabId = hulls[i];
                double dryKg = catalogue.DryMassKg(prefabId);
                if (dryKg <= 0) continue;
                double massKg = dryKg * (1.0 + cargoPerDryKg);

                FuelManifest manifest = fuelCatalogue != null ? fuelCatalogue.FuelManifest(prefabId) : null;
                HullFrame frame = frameCatalogue != null ? frameCatalogue.HullFrame(prefabId) : null;
                LegBurn burn = FuelBurn.ForLeg(profile, massKg, catalogue.ThrustBands(prefabId), manifest, tuning, frame);
                record.Burn.Add(burn);
                record.FsdMwh += FuelBurn.FsdEnergyMwh(profile, massKg, tuning, speeds.CruiseKmPerMin) * tuning.FuelBurnScale;
            }
            return record;
        }

        /// <summary>Every hull actually flying the leg. A squadron lists its members and replaces
        /// the aggregate prefab; anything else is <see cref="PendingFleetAction.ShipPrefabId"/>
        /// once per <see cref="PendingFleetAction.ConvoySize"/> — the same reading
        /// <see cref="ShipPool.ConvoyWeaponBands"/> takes, so a squadron is never counted twice.</summary>
        internal static List<string> HullsOf(PendingFleetAction fleet)
        {
            var hulls = new List<string>();
            if (fleet.Squadron != null && fleet.Squadron.Count > 0)
            {
                for (int i = 0; i < fleet.Squadron.Count; i++)
                    if (!string.IsNullOrEmpty(fleet.Squadron[i])) hulls.Add(fleet.Squadron[i]);
                return hulls;
            }
            if (string.IsNullOrEmpty(fleet.ShipPrefabId)) return hulls;
            int count = fleet.ConvoySize > 0 ? fleet.ConvoySize : 1;
            for (int i = 0; i < count; i++) hulls.Add(fleet.ShipPrefabId);
            return hulls;
        }

        /// <summary>Mass of what the convoy has aboard for this leg, from the same per-unit masses
        /// the cargo-trim path reads. 0 when no mass catalogue is wired or the hold is empty, which
        /// prices the leg dry and is counted as such rather than folded in.</summary>
        public static double CargoMassKg(PendingFleetAction fleet, IItemMassCatalogue masses)
        {
            if (masses == null || fleet.Cargo == null || fleet.Cargo.Count == 0) return 0;
            double totalKg = 0;
            foreach (KeyValuePair<string, long> item in fleet.Cargo)
                totalKg += item.Value * masses.MassKgPerUnit(item.Key);
            return totalKg > 0 ? totalKg : 0;
        }

        public static double PolylineLengthKm(IList<Vec3> polylineKm)
        {
            if (polylineKm == null || polylineKm.Count < 2) return 0;
            double lengthKm = 0;
            for (int i = 0; i < polylineKm.Count - 1; i++) lengthKm += polylineKm[i].DistanceTo(polylineKm[i + 1]);
            return lengthKm;
        }

        /// <summary>The FUEL_DRAWN line. <c>mwh=</c> is the leg's whole electric demand, of which
        /// <c>uncovered=</c> is the part neither a battery nor a reactor answered and <c>rkg=</c>
        /// is what the reactor's share cost in fuel.</summary>
        public static string Format(PendingFleetAction fleet, FuelLegRecord record, FactionTuning tuning)
        {
            LegBurn burn = record.Burn;
            double reactorMwh = tuning != null && tuning.ReactorFuelKgPerMwh > 0
                ? burn.ReactorFuelKg / tuning.ReactorFuelKgPerMwh : 0;
            double electricMwh = burn.BatteryMwh + reactorMwh + burn.ElectricMwh;

            var sb = new StringBuilder(160);
            sb.Append("FUEL_DRAWN ").Append(fleet.LogId)
              .Append(' ').Append(record.FactionTag ?? "?")
              .Append(" kind=").Append(record.Kind)
              .Append(" hulls=").Append(record.Hulls.ToString(CultureInfo.InvariantCulture))
              .Append(" km=").Append(Fixed(record.LegKm, 0))
              .Append(" h2=").Append(Fixed(burn.TotalGasLitres, 0))
              .Append(" hover=").Append(Fixed(burn.HoverGasLitres, 0))
              .Append(" dv=").Append(Fixed(burn.DeltaVGasLitres, 0))
              .Append(" mwh=").Append(Fixed(electricMwh, 3))
              .Append(" rkg=").Append(Fixed(burn.ReactorFuelKg, 2))
              .Append(" uncovered=").Append(Fixed(burn.ElectricMwh, 3))
              .Append(" cache=").Append(record.ProfileCacheHit ? "hit" : "miss")
              .Append(" mode=").Append(FuelDrawModes.Name(tuning == null ? FuelDrawModes.Measure : tuning.FuelDrawMode));
            return sb.ToString();
        }

        private static string Fixed(double value, int decimals)
        {
            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
    }
}
