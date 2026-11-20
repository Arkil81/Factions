using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;
using VRage.Game;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Computes real per-block thrust data from SE thruster block definitions, for caching into
    /// <see cref="SePrefabCatalogue"/>. Mirrors <see cref="WeaponStatsHelper"/>'s shape — a
    /// per-block resolver plus a same-curve grouping pass — but unlike weapon Dps/range, nearly
    /// every field here is a raw <see cref="MyThrustDefinition"/> constant copied verbatim: no
    /// ammo-chain or duty-cycle resolution needed. The one lookup is the fuel gas's energy density,
    /// resolved through <c>MyGasProperties</c> exactly as <c>MyThrust.Init</c> resolves it.
    ///
    /// <para>API confirmed against decompiled SE source (2026-08-09):
    /// <c>MyThrust.ThrustForwardVector => Base6Directions.GetIntVector(base.Orientation.Forward)</c>
    /// and <c>MyThrusterBlockThrustComponent.CalculateForceMultiplier</c> — the placed block's
    /// mounted facing (<c>MyObjectBuilder_CubeBlock.BlockOrientation.Forward</c>) is the same value
    /// SE itself uses to resolve applied thrust direction, and the effectiveness curve is a plain
    /// lerp between <c>EffectivenessAtMinInfluence</c>/<c>EffectivenessAtMaxInfluence</c> over
    /// <c>MinPlanetaryInfluence</c>/<c>MaxPlanetaryInfluence</c>, short-circuited to
    /// <c>EffectivenessAtMinInfluence</c> when <c>NeedsAtmosphereForInfluence</c> and not in
    /// atmosphere. See <see cref="ThrustPhysics.ForceMultiplier"/> (Factions.Core) for the ported
    /// evaluation.</para>
    /// </summary>
    internal static class ThrustStatsHelper
    {
        /// <summary>Per-block thrust data, keyed by the block's own mounted facing. <paramref
        /// name="band"/> is null for any block that is not a thruster
        /// (<see cref="MyThrustDefinition"/> or a subtype).</summary>
        public static void ComputeThrustStats(MyCubeBlockDefinition blockDef, Base6Directions.Direction mountedForward,
            out ThrustDirection direction, out ThrustBand? band)
        {
            direction = ThrustDirection.Forward;
            band = null;

            var thrustDef = blockDef as MyThrustDefinition;
            if (thrustDef == null) return;

            string fuelKey;
            float efficiency, energyDensity;
            ResolveFuel(thrustDef, out fuelKey, out efficiency, out energyDensity);

            direction = ToThrustDirection(mountedForward);
            band = new ThrustBand
            {
                ThrusterType                = thrustDef.ThrusterType.ToString(),
                ForceMagnitudeN             = thrustDef.ForceMagnitude,
                MinPlanetaryInfluence       = thrustDef.MinPlanetaryInfluence,
                MaxPlanetaryInfluence       = thrustDef.MaxPlanetaryInfluence,
                EffectivenessAtMinInfluence = thrustDef.EffectivenessAtMinInfluence,
                EffectivenessAtMaxInfluence = thrustDef.EffectivenessAtMaxInfluence,
                NeedsAtmosphereForInfluence = thrustDef.NeedsAtmosphereForInfluence,
                MaxPowerMw                  = thrustDef.MaxPowerConsumption,
                FuelId                      = fuelKey,
                FuelEfficiency              = efficiency,
                FuelEnergyDensityMwhPerL    = energyDensity,
            };
        }

        /// <summary>What the band burns, from the definition's own fuel converter: the gas's
        /// inventory key, the converter efficiency, and the gas's energy density in MWh per litre.
        /// A band whose converter names no fuel is electric — null key, efficiency 1, density 0 —
        /// which is what SE itself falls back to (<c>MyThrust.Init</c> substitutes an electricity
        /// <c>MyGasProperties</c> with <c>EnergyDensity</c> 1 when the id resolves to nothing).
        /// The key is built by the same mapper the scanner's gas tanks use, so a gas is spelled one
        /// way across the catalogue.</summary>
        private static void ResolveFuel(MyThrustDefinition thrustDef,
            out string fuelKey, out float efficiency, out float energyDensityMwhPerL)
        {
            fuelKey = null;
            efficiency = 1f;
            energyDensityMwhPerL = 0f;

            MyFuelConverterInfo converter = thrustDef.FuelConverter;
            if (converter == null || converter.FuelId.IsNull()
                || string.IsNullOrEmpty(converter.FuelId.SubtypeName)) return;

            string key = InventoryKeyMapper.FromTypeIdAndSubtype(
                converter.FuelId.TypeId.ToString(), converter.FuelId.SubtypeName);
            if (string.IsNullOrEmpty(key)) return;

            fuelKey = key;
            efficiency = converter.Efficiency;

            MyDefinitionId fuelId = converter.FuelId;
            MyGasProperties gas;
            if (MyDefinitionManager.Static != null
                && MyDefinitionManager.Static.TryGetDefinition(fuelId, out gas)
                && gas != null)
                energyDensityMwhPerL = gas.EnergyDensity;
        }

        /// <summary>SE's six directions in Core's vocabulary, and back. Kept host-side so
        /// Factions.Core stays SE-free; every host caller that needs to speak
        /// <see cref="ThrustDirection"/> goes through here rather than writing its own switch.</summary>
        public static Base6Directions.Direction ToBase6Direction(ThrustDirection d)
        {
            switch (d)
            {
                case ThrustDirection.Forward:  return Base6Directions.Direction.Forward;
                case ThrustDirection.Backward: return Base6Directions.Direction.Backward;
                case ThrustDirection.Up:       return Base6Directions.Direction.Up;
                case ThrustDirection.Down:     return Base6Directions.Direction.Down;
                case ThrustDirection.Left:     return Base6Directions.Direction.Left;
                default:                       return Base6Directions.Direction.Right;
            }
        }

        public static ThrustDirection ToThrustDirection(Base6Directions.Direction d)
        {
            switch (d)
            {
                case Base6Directions.Direction.Forward:  return ThrustDirection.Forward;
                case Base6Directions.Direction.Backward: return ThrustDirection.Backward;
                case Base6Directions.Direction.Up:       return ThrustDirection.Up;
                case Base6Directions.Direction.Down:     return ThrustDirection.Down;
                case Base6Directions.Direction.Left:     return ThrustDirection.Left;
                case Base6Directions.Direction.Right:    return ThrustDirection.Right;
                default:                                 return ThrustDirection.Forward;
            }
        }

        /// <summary>Groups a prefab's raw per-direction, per-block <see cref="ThrustBand"/> list
        /// into one band per distinct propulsion-curve signature present in that direction (exact
        /// match on <c>ThrusterType</c> + the four curve constants + the atmosphere flag + the fuel
        /// the band burns, see <see cref="SameCurve"/> — these
        /// are raw definition constants, not derived/jittered floats, so unlike
        /// <see cref="WeaponStatsHelper.GroupIntoBands"/> no tolerance bucketing is needed).
        /// Directions whose list ends up empty are dropped — the result is sparse, matching
        /// <see cref="IPrefabCatalogue.ThrustBands"/>'s contract.</summary>
        public static Dictionary<ThrustDirection, List<ThrustBand>> GroupIntoBands(
            Dictionary<ThrustDirection, List<ThrustBand>> raw)
        {
            var result = new Dictionary<ThrustDirection, List<ThrustBand>>();
            foreach (var kv in raw)
            {
                List<ThrustBand> grouped = GroupOneDirection(kv.Value);
                if (grouped.Count > 0) result[kv.Key] = grouped;
            }
            return result;
        }

        private static List<ThrustBand> GroupOneDirection(List<ThrustBand> perBlockBands)
        {
            var bands = new List<ThrustBand>();
            foreach (ThrustBand stat in perBlockBands)
            {
                bool merged = false;
                for (int i = 0; i < bands.Count; i++)
                {
                    ThrustBand b = bands[i];
                    if (SameCurve(b, stat))
                    {
                        b.ForceMagnitudeN += stat.ForceMagnitudeN;
                        b.MaxPowerMw += stat.MaxPowerMw;
                        bands[i] = b;
                        merged = true;
                        break;
                    }
                }
                if (!merged) bands.Add(stat);
            }
            return bands;
        }

        /// <summary>Whether two per-block bands may be summed into one. The curve constants decide
        /// the force side; the fuel key and converter efficiency decide the consumption side, and
        /// both are compared because <see cref="ThrustBand.MaxPowerMw"/> is summed across the merge
        /// — two blocks on different fuels, or on the same fuel at different efficiencies, do not
        /// share one litres-per-newton-second constant and must stay apart. The key comparison is
        /// ordinal: it is a machine-built inventory key, not text a culture collates.</summary>
        private static bool SameCurve(ThrustBand a, ThrustBand b)
        {
            return a.ThrusterType == b.ThrusterType
                && a.MinPlanetaryInfluence == b.MinPlanetaryInfluence
                && a.MaxPlanetaryInfluence == b.MaxPlanetaryInfluence
                && a.EffectivenessAtMinInfluence == b.EffectivenessAtMinInfluence
                && a.EffectivenessAtMaxInfluence == b.EffectivenessAtMaxInfluence
                && a.NeedsAtmosphereForInfluence == b.NeedsAtmosphereForInfluence
                && string.Equals(a.FuelId ?? "", b.FuelId ?? "", StringComparison.Ordinal)
                && a.FuelEfficiency == b.FuelEfficiency;
        }
    }
}
