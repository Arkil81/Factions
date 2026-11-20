using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;
using Sandbox.Game;                    // MyInventory
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// What one materialised grid holds in fuel at an instant, plus the two model inputs that only
    /// the live grid can supply: its thrust bands and its fuel manifest. Two of these — one at
    /// engage, one at the manoeuvre's end — are the observed half of
    /// <c>FleetFlightController</c>'s FUEL_OBSERVED line.
    ///
    /// <para>Read from the live blocks rather than from <c>prefab_catalogue.json</c>: a hull in the
    /// world has taken damage, been repaired and been refuelled since it spawned, and the whole
    /// point of the calibration is to compare the model against what actually happened to that
    /// hull. The catalogue entry is what the model would have assumed.</para>
    ///
    /// <para>Every field distinguishes "none aboard" from "nothing to read": a hull with no gas
    /// tanks reports <see cref="HasGasTanks"/> false rather than zero litres, and the line prints
    /// <c>n/a</c> for it. A zero from a hull that does have tanks is a real, empty tank.</para>
    /// </summary>
    internal sealed class GridFuelState
    {
        /// <summary>Litres held per gas inventory key, under the same key the scanner and the
        /// bands use (<see cref="InventoryKeyMapper"/>). Empty when the hull has no gas tanks.</summary>
        public readonly Dictionary<string, double> GasLitres = new Dictionary<string, double>(StringComparer.Ordinal);
        public bool HasGasTanks;

        /// <summary>Summed <c>MyBatteryBlock.CurrentStoredPower</c>, MWh.</summary>
        public double BatteryMwh;
        public bool HasBatteries;

        /// <summary>Kilograms of reactor fuel in the reactors' own inventories — only items whose
        /// definition id one of that reactor's <c>FuelInfos</c> names, so a reactor being used as
        /// cargo is not counted as fuelled.</summary>
        public double ReactorFuelKg;
        public bool HasReactors;

        public double MassKg;
        public Vector3D PosWorld;
        public DateTime Utc;

        /// <summary>The live hull's thrust bands, grouped exactly as the prefab scanner groups a
        /// prefab's. Filled only on the engage snapshot; the modelled burn needs it once.</summary>
        public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> Bands;

        /// <summary>The live hull's battery store and reactor fuel key, in the shape
        /// <see cref="FuelBurn.ForLeg"/> resolves electricity against. Filled with the bands.</summary>
        public FuelManifest Manifest;

        /// <summary>Reads one grid's fuel state in a single pass over its fat blocks.
        /// <paramref name="withModelInputs"/> additionally builds the thrust bands and the manifest,
        /// which only the engage end needs.
        ///
        /// <para>Never throws: a block that will not read is left out of the sum and its
        /// <c>Has*</c> flag stays as the rest of the pass found it, because this runs inside the
        /// Torch update tick and a flight must not die of a diagnostic.</para></summary>
        public static GridFuelState Capture(MyCubeGrid grid, bool withModelInputs)
        {
            var state = new GridFuelState();
            state.Utc = DateTime.UtcNow;
            if (grid == null) return state;

            state.PosWorld = grid.PositionComp.GetPosition();
            if (grid.Physics != null) state.MassKg = grid.Physics.Mass;

            Dictionary<ThrustDirection, List<ThrustBand>> rawBands = withModelInputs
                ? new Dictionary<ThrustDirection, List<ThrustBand>>()
                : null;
            FuelManifest manifest = withModelInputs ? new FuelManifest() : null;

            foreach (MyCubeBlock block in grid.GetFatBlocks())
            {
                if (block == null) continue;
                try
                {
                    var tank = block as MyGasTank;
                    if (tank != null) { AddTank(state, tank); continue; }

                    var battery = block as MyBatteryBlock;
                    if (battery != null)
                    {
                        state.HasBatteries = true;
                        state.BatteryMwh += battery.CurrentStoredPower;
                        if (manifest != null) manifest.BatteryMwh += battery.MaxStoredPower;
                        continue;
                    }

                    var reactor = block as MyReactor;
                    if (reactor != null)
                    {
                        state.HasReactors = true;
                        state.ReactorFuelKg += ReactorFuelKilograms(reactor);
                        if (manifest != null) AddReactorToManifest(reactor.BlockDefinition, manifest);
                        continue;
                    }

                    if (rawBands == null) continue;
                    var thruster = block as MyThrust;
                    if (thruster != null) AddThruster(rawBands, thruster);
                }
                catch (Exception)
                {
                    // One unreadable block is one term missing from a sum, not a failed flight.
                }
            }

            if (withModelInputs)
            {
                manifest.HasFuelData = true;
                state.Manifest = manifest;
                state.Bands = AsReadOnly(ThrustStatsHelper.GroupIntoBands(rawBands));
            }
            return state;
        }

        private static void AddTank(GridFuelState state, MyGasTank tank)
        {
            MyGasTankDefinition def = tank.BlockDefinition;
            if (def == null || def.Capacity <= 0f) return;

            string key = InventoryKeyMapper.FromTypeIdAndSubtype(
                def.StoredGasId.TypeId.ToString(), def.StoredGasId.SubtypeName);
            if (string.IsNullOrEmpty(key)) return;

            state.HasGasTanks = true;
            double held;
            state.GasLitres.TryGetValue(key, out held);
            state.GasLitres[key] = held + tank.FilledRatio * def.Capacity;
        }

        /// <summary>Kilograms of its own fuel one reactor holds. The amount is a unit count, so it
        /// is multiplied by the fuel item's mass per unit — 1 kg for vanilla uranium ingots, and a
        /// modded ingot needs no case here.</summary>
        private static double ReactorFuelKilograms(MyReactor reactor)
        {
            MyReactorDefinition def = reactor.BlockDefinition;
            if (def == null || def.FuelInfos == null || def.FuelInfos.Length == 0) return 0;

            double kg = 0;
            for (int i = 0; i < reactor.InventoryCount; i++)
            {
                MyInventory inv = reactor.GetInventory(i);
                if (inv == null) continue;
                foreach (MyPhysicalInventoryItem item in inv.GetItems())
                {
                    if (item.Content == null) continue;
                    MyDefinitionId id = item.Content.GetId();
                    for (int f = 0; f < def.FuelInfos.Length; f++)
                    {
                        MyReactorDefinition.FuelInfo fuel = def.FuelInfos[f];
                        if (fuel.FuelId != id) continue;
                        float massPerUnit = fuel.FuelDefinition != null ? fuel.FuelDefinition.Mass : 1f;
                        kg += (double)item.Amount * massPerUnit;
                        break;
                    }
                }
            }
            return kg;
        }

        /// <summary>The reactor half of the live manifest, computed the way
        /// <c>PrefabCatalogueScanner.AccumulateReactorFuel</c> computes the prefab's: inventory
        /// volume over the fuel item's volume per unit, times its mass per unit. Kept here rather
        /// than shared with the scanner because that one accumulates into a catalogue entry from an
        /// object-builder walk; this one reads a live block.</summary>
        private static void AddReactorToManifest(MyReactorDefinition def, FuelManifest manifest)
        {
            if (def == null || def.FuelInfos == null || def.FuelInfos.Length == 0) return;

            MyReactorDefinition.FuelInfo fuel = def.FuelInfos[0];
            string fuelKey = InventoryKeyMapper.FromTypeIdAndSubtype(
                fuel.FuelId.TypeId.ToString(), fuel.FuelId.SubtypeName);
            if (string.IsNullOrEmpty(fuelKey)) return;

            if (string.IsNullOrEmpty(manifest.ReactorFuelKey)) manifest.ReactorFuelKey = fuelKey;
            else if (!string.Equals(manifest.ReactorFuelKey, fuelKey, StringComparison.OrdinalIgnoreCase)) return;

            MyPhysicalItemDefinition fuelItem = fuel.FuelDefinition;
            if (fuelItem == null || fuelItem.Volume <= 0f || fuelItem.Mass <= 0f) return;
            if (def.InventoryMaxVolume <= 0f) return;

            manifest.ReactorFuelCapacityKg += def.InventoryMaxVolume / fuelItem.Volume * fuelItem.Mass;
        }

        private static void AddThruster(Dictionary<ThrustDirection, List<ThrustBand>> rawBands, MyThrust thruster)
        {
            ThrustDirection direction;
            ThrustBand? band;
            ThrustStatsHelper.ComputeThrustStats(thruster.BlockDefinition, thruster.Orientation.Forward,
                out direction, out band);
            if (!band.HasValue) return;

            List<ThrustBand> forDirection;
            if (!rawBands.TryGetValue(direction, out forDirection))
                rawBands[direction] = forDirection = new List<ThrustBand>();
            forDirection.Add(band.Value);
        }

        /// <summary><c>IReadOnlyDictionary</c> is not variant in its value type, so the grouped
        /// bands need an explicit wrap to be passed to <see cref="FuelBurn.ForLeg"/>. Bounded by
        /// the six directions.</summary>
        private static IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> AsReadOnly(
            Dictionary<ThrustDirection, List<ThrustBand>> bands)
        {
            var result = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(bands.Count);
            foreach (KeyValuePair<ThrustDirection, List<ThrustBand>> kv in bands) result[kv.Key] = kv.Value;
            return result;
        }
    }
}
