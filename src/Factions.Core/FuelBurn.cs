using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What one <see cref="ThrustBand"/> consumes per delivered newton-second. SE draws
    /// <c>MaxPowerConsumption × ForceMultiplier</c> for <c>ForceMagnitude × ForceMultiplier</c> of
    /// thrust, so the ratio is a constant of the block and the environment cancels out — which is
    /// why this is a property of the band and not a table keyed on a gas name.</summary>
    public struct BandBurnRate
    {
        /// <summary>Inventory key of the gas burned, copied from the band. Null or empty when the
        /// band is electric.</summary>
        public string FuelId;
        /// <summary>Litres of <see cref="FuelId"/> per newton-second. 0 for an electric band.</summary>
        public double GasLitresPerNewtonSecond;
        /// <summary>MWh of electricity per newton-second. 0 for a gas band.</summary>
        public double ElectricMwhPerNewtonSecond;
        /// <summary>False when the band carries no usable consumption data — a band scanned before
        /// the consumption fields existed, or one with no force. Such a band burns nothing rather
        /// than burning a guess.</summary>
        public bool HasBurnData;
    }

    /// <summary>What one leg costs a hull, in items. <see cref="FuelBurn.ForImpulse"/> fills the
    /// raw demand (<see cref="GasLitres"/>, <see cref="ElectricMwh"/>);
    /// <see cref="FuelBurn.ForLeg"/> additionally resolves the electric demand against the hull's
    /// own store, after which <see cref="ElectricMwh"/> holds only the part neither the battery nor
    /// the reactor covered.</summary>
    public sealed class LegBurn
    {
        /// <summary>Litres per gas inventory key (<c>"Gas:Hydrogen"</c> and any modded gas).</summary>
        public readonly Dictionary<string, double> GasLitres = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Electricity the leg needs. Total demand after <see cref="FuelBurn.ForImpulse"/>;
        /// the uncovered remainder after <see cref="FuelBurn.ForLeg"/>.</summary>
        public double ElectricMwh;

        /// <summary>Of <see cref="GasLitres"/>, the litres the <c>Down</c> bands owe for holding the
        /// hull up inside a well. Filled by <see cref="FuelBurn.ForLeg"/>.</summary>
        public double HoverGasLitres;

        /// <summary>Of <see cref="GasLitres"/>, the litres the accelerate and brake charges owe.
        /// Filled by <see cref="FuelBurn.ForLeg"/>. With <see cref="HoverGasLitres"/> it splits the
        /// gas total into the two terms that answer to different inputs — hover to time in the
        /// well, Δv to speed — so a calibration reading can say which one is wrong.</summary>
        public double DeltaVGasLitres;

        /// <summary>True once any band or drive with real data contributed. False means the burn is
        /// unknown, not zero — a range gate must not refuse anything on it.</summary>
        public bool HasBurnData;

        /// <summary>Electricity drawn from the hull's batteries, capped at
        /// <see cref="FuelManifest.BatteryMwh"/>. Free at an owned node: station power is not
        /// modelled. Filled by <see cref="FuelBurn.ForLeg"/> only.</summary>
        public double BatteryMwh;

        /// <summary>Reactor fuel the remaining electricity costs, at
        /// <see cref="FactionTuning.ReactorFuelKgPerMwh"/>. Filled by <see cref="FuelBurn.ForLeg"/>
        /// only.</summary>
        public double ReactorFuelKg;

        /// <summary>Inventory key <see cref="ReactorFuelKg"/> is drawn in, from the hull's own
        /// manifest (<c>"Ingot:Uranium"</c> on a vanilla reactor). Null when nothing is drawn.</summary>
        public string ReactorFuelKey;

        public void AddGas(string fuelId, double litres)
        {
            if (litres <= 0) return;
            string key = fuelId ?? "";
            double existing;
            GasLitres.TryGetValue(key, out existing);
            GasLitres[key] = existing + litres;
        }

        /// <summary>Folds another burn into this one. Used to sum the per-direction impulse charges
        /// of one leg.</summary>
        public void Add(LegBurn other)
        {
            if (other == null) return;
            foreach (KeyValuePair<string, double> pair in other.GasLitres) AddGas(pair.Key, pair.Value);
            ElectricMwh += other.ElectricMwh;
            HoverGasLitres += other.HoverGasLitres;
            DeltaVGasLitres += other.DeltaVGasLitres;
            BatteryMwh += other.BatteryMwh;
            ReactorFuelKg += other.ReactorFuelKg;
            if (other.ReactorFuelKey != null) ReactorFuelKey = other.ReactorFuelKey;
            HasBurnData = HasBurnData || other.HasBurnData;
        }

        /// <summary>Multiplies every quantity. <see cref="FactionTuning.FuelBurnScale"/> is applied
        /// this way, once, after the split — a scale of 0 is a kill switch and is honoured.</summary>
        public void Scale(double factor)
        {
            if (factor == 1.0) return;
            if (GasLitres.Count > 0)
            {
                var keys = new List<string>(GasLitres.Keys);
                for (int i = 0; i < keys.Count; i++) GasLitres[keys[i]] = GasLitres[keys[i]] * factor;
            }
            ElectricMwh *= factor;
            HoverGasLitres *= factor;
            DeltaVGasLitres *= factor;
            BatteryMwh *= factor;
            ReactorFuelKg *= factor;
        }

        /// <summary>Every gas key's litres summed — a report figure, not a draw (a draw is per key).</summary>
        public double TotalGasLitres
        {
            get { double total = 0; foreach (double litres in GasLitres.Values) total += litres; return total; }
        }
    }

    /// <summary>The fuel side of docs/fuel-plan.md: what a leg costs a hull in items, from the
    /// hull-independent impulse profile of the leg (<see cref="LegImpulseProfile"/>) and the
    /// scanned bands of the hull flying it.
    ///
    /// <para>Three charges make up a leg. Thruster phases (everything inside a gravity well, plus
    /// the undock and dock at the ends) are billed as impulse against the direction's bands.
    /// The FSD cruise between wells draws power, not propellant, from the mod's own formula. The
    /// electric total then resolves against the hull's battery and reactor.</para>
    ///
    /// <para>SE-free and pure: every input is an argument, nothing is cached here.</para></summary>
    public static class FuelBurn
    {
        public const double SecondsPerHour = 3600.0;

        /// <summary>The FSD mod's mass term divisor: the mod writes <c>m·(1+v_kms²)/0.528/1e6</c>.
        /// A transcribed mod constant, not a tuning lever — the levers the config actually exposes
        /// are the <c>Fsd*</c> fields on <see cref="FactionTuning"/>.</summary>
        public const double FsdMassPowerDivisor = 528000.0;

        /// <summary>Consumption per delivered newton-second for one band. A gas band burns
        /// <c>MaxPowerMw / (EnergyDensity × Efficiency)</c> litres per second at full thrust — the
        /// vanilla large hydrogen thruster's 4,820 L/s — divided by its force to give litres per
        /// newton-second. An electric band draws <c>MaxPowerMw</c> while delivering
        /// <c>ForceMagnitudeN</c>, i.e. <c>MaxPowerMw/3600</c> MWh per newton-second. A band with no
        /// power draw or no force, or a gas band whose density or efficiency never resolved, comes
        /// back with <see cref="BandBurnRate.HasBurnData"/> false and burns nothing.</summary>
        public static BandBurnRate SpecificConsumption(ThrustBand band)
        {
            var rate = new BandBurnRate();
            rate.FuelId = band.FuelId;
            if (band.MaxPowerMw <= 0 || band.ForceMagnitudeN <= 0) return rate;

            if (!string.IsNullOrEmpty(band.FuelId))
            {
                if (band.FuelEnergyDensityMwhPerL <= 0 || band.FuelEfficiency <= 0) return rate;
                double litresPerSecond = band.MaxPowerMw / (band.FuelEnergyDensityMwhPerL * (double)band.FuelEfficiency);
                rate.GasLitresPerNewtonSecond = litresPerSecond / band.ForceMagnitudeN;
            }
            else
            {
                rate.ElectricMwhPerNewtonSecond = (band.MaxPowerMw / SecondsPerHour) / band.ForceMagnitudeN;
            }
            rate.HasBurnData = true;
            return rate;
        }

        /// <summary>Charges <paramref name="impulseNs"/> newton-seconds along one direction, split
        /// across that direction's bands in proportion to the force each actually delivers at
        /// <paramref name="airDensity"/>/<paramref name="inAtmosphere"/> (the same
        /// effectiveness curve <see cref="ThrustPhysics.ForceN"/> uses, so an atmospheric band
        /// contributes nothing in space and an ion band little at a surface).
        ///
        /// <para>A band with no consumption data still takes its share of the impulse — the hull
        /// really does push with it — and burns nothing for it, so the result is an undercount
        /// rather than a fabrication. <see cref="LegBurn.HasBurnData"/> is false only when no band
        /// consulted had data at all, which includes a missing direction bucket.</para></summary>
        public static LegBurn ForImpulse(IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bandsByDirection,
                                         ThrustDirection direction, double impulseNs,
                                         double airDensity, bool inAtmosphere)
        {
            var burn = new LegBurn();
            if (bandsByDirection == null || impulseNs <= 0) return burn;

            IReadOnlyList<ThrustBand> bands;
            if (!bandsByDirection.TryGetValue(direction, out bands) || bands == null || bands.Count == 0) return burn;

            double totalForceN = ThrustPhysics.ForceN(bands, airDensity, inAtmosphere);
            if (totalForceN <= 0) return burn;

            for (int i = 0; i < bands.Count; i++)
            {
                ThrustBand band = bands[i];
                double forceN = band.ForceMagnitudeN * ThrustPhysics.ForceMultiplier(band, airDensity, inAtmosphere);
                if (forceN <= 0) continue;

                BandBurnRate rate = SpecificConsumption(band);
                if (!rate.HasBurnData) continue;

                double share = impulseNs * (forceN / totalForceN);
                if (rate.GasLitresPerNewtonSecond > 0) burn.AddGas(rate.FuelId, share * rate.GasLitresPerNewtonSecond);
                else burn.ElectricMwh += share * rate.ElectricMwhPerNewtonSecond;
                burn.HasBurnData = true;
            }
            return burn;
        }

        /// <summary>What one hull of <paramref name="massKg"/> (dry plus cargo) burns flying
        /// <paramref name="profile"/>. Lift and hover go to the bands that push the hull toward its
        /// top, acceleration to those that push it along its nose, and braking to those facing its
        /// nose — read off <paramref name="frame"/> (<see cref="HullFrame.LiftBand"/>). An unknown or
        /// absent frame is the belly-down hull: <c>Down</c>, <c>Backward</c> and <c>Forward</c>.
        /// A hull built to lift on its side is billed on that side's thrusters, atmospheric ones
        /// included. A turn is a brake plus an accelerate, so
        /// <see cref="LegImpulseProfile.AccelerateDeltaVMs"/> and
        /// <see cref="LegImpulseProfile.BrakeDeltaVMs"/> carry one each. The FSD
        /// stretches draw power, resolved against the hull's battery first and then its reactor;
        /// a hull with neither keeps the shortfall in <see cref="LegBurn.ElectricMwh"/> rather than
        /// failing, because the abstract fleet is modelled as carrying the drive whatever the
        /// prefab holds. <see cref="FactionTuning.FuelBurnScale"/> multiplies every result.</summary>
        public static LegBurn ForLeg(LegImpulseProfile profile, double massKg,
                                     IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bandsByDirection,
                                     FuelManifest manifest, FactionTuning tuning, HullFrame frame = null)
        {
            var burn = new LegBurn();
            if (profile == null || massKg <= 0 || tuning == null) return burn;

            double air = profile.RepresentativeAirDensity;
            bool inAtmo = profile.RepresentativeInAtmosphere;

            bool known = frame != null && frame.Known;
            ThrustDirection lift = known ? frame.LiftBand : ThrustDirection.Down;
            ThrustDirection push = known ? HullOrientation.Opposite(frame.Nose) : ThrustDirection.Backward;
            ThrustDirection stop = known ? frame.Nose : ThrustDirection.Forward;

            LegBurn hover = ForImpulse(bandsByDirection, lift,
                                       massKg * profile.DownImpulsePerKg, air, inAtmo);
            LegBurn accelerate = ForImpulse(bandsByDirection, push,
                                            massKg * profile.AccelerateDeltaVMs, air, inAtmo);
            LegBurn brake = ForImpulse(bandsByDirection, stop,
                                       massKg * profile.BrakeDeltaVMs, air, inAtmo);
            burn.Add(hover);
            burn.Add(accelerate);
            burn.Add(brake);
            burn.HoverGasLitres = hover.TotalGasLitres;
            burn.DeltaVGasLitres = accelerate.TotalGasLitres + brake.TotalGasLitres;

            double fsdMwh = FsdEnergyMwh(profile, massKg, tuning);
            if (fsdMwh > 0)
            {
                burn.ElectricMwh += fsdMwh;
                burn.HasBurnData = true;
            }

            // Scale before the electric split: the scale calibrates the modelled burn, the battery
            // capacity is a physical bound on how much of it a hull can carry as charge.
            burn.Scale(tuning.FuelBurnScale);
            ResolveElectric(burn, manifest, tuning);
            return burn;
        }

        /// <summary>Electricity the FSD phases of <paramref name="profile"/> cost a hull of
        /// <paramref name="massKg"/>: the cruise draw over the time the stretches take at the sim
        /// cruise speed (<see cref="FactionTuning.FleetSpeedKmPerMin"/>), plus one charge burst per
        /// engagement.</summary>
        public static double FsdEnergyMwh(LegImpulseProfile profile, double massKg, FactionTuning tuning)
        {
            return FsdEnergyMwh(profile, massKg, tuning, 0);
        }

        /// <summary>The same energy at <paramref name="cruiseKmPerMin"/> instead of the tuning
        /// default; at or below 0 means the default. The FSD half of a fleet-speed model enters
        /// here — and it is not monotonic: flying slower buys a lower draw per second and pays for
        /// it in seconds, so the two terms have to be evaluated, not reasoned about.</summary>
        public static double FsdEnergyMwh(LegImpulseProfile profile, double massKg, FactionTuning tuning,
                                          double cruiseKmPerMin)
        {
            if (profile == null || tuning == null || massKg <= 0) return 0;

            double kmPerMin = cruiseKmPerMin > 0 ? cruiseKmPerMin : tuning.FleetSpeedKmPerMin;
            double speedKmPerS = kmPerMin / 60.0;
            double mwh = 0;
            if (profile.FsdDistanceKm > 0 && speedKmPerS > 0)
            {
                double seconds = profile.FsdDistanceKm / speedKmPerS;
                mwh += FsdActivePowerMw(tuning, massKg, speedKmPerS) * seconds / SecondsPerHour;
            }
            if (profile.FsdEngagements > 0) mwh += FsdChargeMwh(tuning, massKg) * profile.FsdEngagements;
            return mwh;
        }

        /// <summary>The RC.4 FSD Supercruise mod's own active draw, large grid:
        /// <c>(Base + 1 + Multiplier·v/vmax + m·(1 + v_kms²)/0.528e6) / Divider</c> MW. ~20 MW for a
        /// 1,000 t hull at the sim cruise of 8.33 km/s. 0 when the divider is not configured.</summary>
        public static double FsdActivePowerMw(FactionTuning tuning, double massKg, double speedKmPerS)
        {
            if (tuning == null || tuning.FsdPowerBySpeedDivider <= 0) return 0;
            double speedTerm = tuning.FsdMaxSpeedKmPerS > 0
                ? tuning.FsdPowerRequirementMultiplier * speedKmPerS / tuning.FsdMaxSpeedKmPerS
                : 0;
            double massTerm = massKg * (1.0 + speedKmPerS * speedKmPerS) / FsdMassPowerDivisor;
            return (tuning.FsdBaseRequiredPowerMw + 1.0 + speedTerm + massTerm) / tuning.FsdPowerBySpeedDivider;
        }

        /// <summary>The charge burst before one engagement: <c>Base + FsdChargeMwPerKg·m</c> MW held
        /// for <see cref="FactionTuning.FsdChargeSeconds"/>. 0.13 MWh for a 1,000 t hull.</summary>
        public static double FsdChargeMwh(FactionTuning tuning, double massKg)
        {
            if (tuning == null || tuning.FsdChargeSeconds <= 0) return 0;
            double mw = tuning.FsdBaseRequiredPowerMw + tuning.FsdChargeMwPerKg * massKg;
            return mw > 0 ? mw * tuning.FsdChargeSeconds / SecondsPerHour : 0;
        }

        /// <summary>Splits <see cref="LegBurn.ElectricMwh"/> into the battery's share and the
        /// reactor fuel the rest costs. An unknown manifest, or one with no reactor, leaves the
        /// uncovered part in <see cref="LegBurn.ElectricMwh"/>: never a gate.</summary>
        private static void ResolveElectric(LegBurn burn, FuelManifest manifest, FactionTuning tuning)
        {
            if (burn.ElectricMwh <= 0) return;

            double remainderMwh = burn.ElectricMwh;
            if (manifest != null && manifest.BatteryMwh > 0)
            {
                burn.BatteryMwh = Math.Min(remainderMwh, manifest.BatteryMwh);
                remainderMwh -= burn.BatteryMwh;
            }
            if (remainderMwh > 0 && manifest != null && !string.IsNullOrEmpty(manifest.ReactorFuelKey)
                && tuning.ReactorFuelKgPerMwh > 0)
            {
                burn.ReactorFuelKey = manifest.ReactorFuelKey;
                burn.ReactorFuelKg = remainderMwh * tuning.ReactorFuelKgPerMwh;
                remainderMwh = 0;
            }
            burn.ElectricMwh = remainderMwh;
        }
    }
}
