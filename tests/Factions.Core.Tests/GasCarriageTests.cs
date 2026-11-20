using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="GasCarriage"/> and the loaders it feeds: gas boards a convoy's tanks for
    /// that gas (less the leg's own burn) and never its hold.
    /// </summary>
    public class GasCarriageTests
    {
        private const string H2 = "Gas:Hydrogen";
        private const string O2 = "Gas:Oxygen";
        private const string Iron = "Ore:Iron";
        private const string B60 = "B60";
        private const string Tankless = "Tankless";

        private const long HoldLitres = 1_340_000;
        private const long TankLitres = 15_000_000;
        private const double IronLitresPerUnit = 0.37;
        private const double DryKg = 1.0e6;

        // ---- fixtures -----------------------------------------------------------------------

        private static ThrustBand HydrogenBand()
        {
            return new ThrustBand
            {
                ThrusterType = "Hydrogen",
                ForceMagnitudeN = 7.2e6f,
                MinPlanetaryInfluence = 0,
                MaxPlanetaryInfluence = 0,
                EffectivenessAtMinInfluence = 1,
                EffectivenessAtMaxInfluence = 1,
                MaxPowerMw = 7.5f,
                FuelId = H2,
                FuelEfficiency = 1f,
                FuelEnergyDensityMwhPerL = 0.001556f,
            };
        }

        /// <summary>B60: a 1.34 ML hold and 15 ML of hydrogen tank, no oxygen tank. Tankless: the
        /// same hold and no tank at all. Both burn hydrogen through one vanilla large band.</summary>
        private sealed class TankCatalogue : IPrefabCatalogue, IFuelPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return HoldLitres; }

            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId)
            {
                return prefabId == B60
                    ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { { H2, TankLitres } }
                    : new Dictionary<string, long>();
            }

            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return 0; }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                var bands = new List<ThrustBand> { HydrogenBand() };
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    { ThrustDirection.Forward, bands },
                    { ThrustDirection.Backward, bands },
                    { ThrustDirection.Down, bands },
                };
            }

            public double DryMassKg(string prefabId) { return DryKg; }

            public FuelManifest FuelManifest(string prefabId)
            {
                return new FuelManifest { HasFuelData = true, ReactorFuelKey = "Ingot:Uranium", ReactorFuelCapacityKg = 100 };
            }
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string invKey)
            {
                if (InventoryKeyMapper.IsGasKey(invKey)) return 1.0;
                return invKey == Iron ? IronLitresPerUnit : 0;
            }
        }

        private static readonly IItemVolumeCatalogue ItemVolumes = new Volumes();

        private static WorldModel World(int fuelDrawMode = FuelDrawModes.Measure)
        {
            var tuning = new FactionTuning();
            tuning.FleetSpeedKmPerMin = 500.0;
            tuning.FleetSpeedMinKmPerMin = 30.0;
            tuning.FleetAccelKmPerMin2 = 50.0;
            tuning.ThrusterCruiseSpeedMs = 100.0;
            tuning.FuelBurnScale = 1.0;
            tuning.FsdGravityDropoutG = 0.01;
            tuning.FsdBaseRequiredPowerMw = 26.0;
            tuning.FsdPowerRequirementMultiplier = 2.0;
            tuning.FsdPowerBySpeedDivider = 8.0;
            tuning.FsdMaxSpeedKmPerS = 2000.0;
            tuning.FsdChargeSeconds = 10.0;
            tuning.FsdChargeMwPerKg = 2.1e-5;
            tuning.ReactorFuelKgPerMwh = 1.0;
            tuning.FuelDrawMode = fuelDrawMode;

            var world = new WorldModel();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = tuning;
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            world.PrefabCatalogue = new TankCatalogue();
            return world;
        }

        private static SpatialNode Node(WorldModel world, string id, Vec3 pos)
        {
            SpatialNode node = world.Graph.Add(pos, 1.0, id);
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            return node;
        }

        private static PendingFleetAction Haul(WorldModel world, SpatialNode from, SpatialNode to, string prefab, int convoy = 1)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Haul,
                ActorTag = "F",
                OriginNodeId = from.Id,
                TargetNodeId = to.Id,
                OriginPosKm = from.Position,
                TargetPosKm = to.Position,
                DepartedUtc = world.UtcNow,
                EstimatedArrivalUtc = world.UtcNow.AddMinutes(10),
                ConvoySize = convoy,
                ShipPrefabId = prefab,
                CargoCapacityLiters = HoldLitres * convoy,
                FleetId = "GAS1",
                LastResolvedSegment = -1,
            };
        }

        /// <summary>One hull's deep-space burn over one accelerate-and-brake pair at 100 m/s, dry,
        /// at the vanilla 6.69e-4 L per newton-second — ~134 kL, rounded up as the cap rounds it.</summary>
        private static long OneHullLegBurn()
        {
            return (long)Math.Ceiling(DryKg * (2 * 100.0) * ((7.5 / 0.001556) / 7.2e6));
        }

        private static long Load(WorldModel world, SpatialNode pickup, SpatialNode dest, ref PendingFleetAction fleet,
                                 string key, long qty)
        {
            bool trimmed;
            return PendingFleetResolver.LoadCargo(pickup, ref fleet, key, qty, world, world.Tuning,
                ItemVolumes, null, dest.Position, out trimmed);
        }

        // ---- the helper -----------------------------------------------------------------------

        [Fact]
        public void TankLitres_IsTheTankForThatGasTimesTheConvoy_AndZeroForAnyOtherGas()
        {
            var cat = new TankCatalogue();
            var fleet = new PendingFleetAction { ShipPrefabId = B60, ConvoySize = 3 };
            Assert.Equal(TankLitres, GasCarriage.TankLitres(cat, B60, H2));
            Assert.Equal(3 * TankLitres, GasCarriage.TankLitres(cat, fleet, H2));
            Assert.Equal(0L, GasCarriage.TankLitres(cat, fleet, O2));
            Assert.Equal(0L, GasCarriage.TankLitres(cat, Tankless, H2));
            // A squadron counts its members, not ShipPrefabId × ConvoySize.
            var squadron = new PendingFleetAction
            {
                ShipPrefabId = B60, ConvoySize = 5, Squadron = new List<string> { B60, Tankless },
            };
            Assert.Equal(TankLitres, GasCarriage.TankLitres(cat, squadron, H2));
        }

        // ---- the loaders ------------------------------------------------------------------

        [Fact]
        public void AB60LoadsItsHydrogenTankLessTheLegsBurn()
        {
            WorldModel world = World();
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[H2] = 100_000_000;
            PendingFleetAction fleet = Haul(world, a, b, B60);

            long burn = GasCarriage.LegBurnLitres(world, world.Tuning, fleet, H2, a.Position, b.Position);
            long loaded = Load(world, a, b, ref fleet, H2, 100_000_000);

            // The priced burn is the hand-computed one, so the cap below is tank minus a real burn.
            Assert.InRange((double)burn, OneHullLegBurn() * 0.995, OneHullLegBurn() * 1.005);
            Assert.InRange(burn, 130_000, 140_000);
            Assert.Equal(TankLitres - burn, loaded);
            Assert.Equal(TankLitres - burn, fleet.Cargo[H2]);
            Assert.Equal(100_000_000 - (TankLitres - burn), a.Inventory[H2]);
        }

        [Fact]
        public void AnUnpricedLegLeavesTheWholeTankForCargo()
        {
            WorldModel world = World(fuelDrawMode: FuelDrawModes.Off);
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[H2] = 100_000_000;
            PendingFleetAction fleet = Haul(world, a, b, B60);

            Assert.Equal(TankLitres, Load(world, a, b, ref fleet, H2, 100_000_000));
        }

        [Fact]
        public void ATanklessHullLoadsNoGas()
        {
            WorldModel world = World();
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[H2] = 100_000_000;
            PendingFleetAction fleet = Haul(world, a, b, Tankless);

            Assert.Equal(0L, Load(world, a, b, ref fleet, H2, 100_000_000));
            Assert.Null(fleet.Cargo);
            Assert.Equal(100_000_000L, a.Inventory[H2]);
        }

        [Fact]
        public void OxygenIntoAHydrogenOnlyHullLoadsNothing()
        {
            WorldModel world = World();
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[O2] = 100_000_000;
            PendingFleetAction fleet = Haul(world, a, b, B60);

            Assert.Equal(0L, Load(world, a, b, ref fleet, O2, 100_000_000));
            Assert.Equal(100_000_000L, a.Inventory[O2]);
        }

        [Fact]
        public void OreAndHydrogenLoadTogetherWithoutCompeting_InEitherOrder()
        {
            long holdIron = (long)(HoldLitres / IronLitresPerUnit);

            // Ore first fills the whole hold; hydrogen still gets its whole tank room.
            WorldModel w1 = World();
            SpatialNode a1 = Node(w1, "A", new Vec3(0, 0, 0));
            SpatialNode b1 = Node(w1, "B", new Vec3(3000, 0, 0));
            a1.Inventory[Iron] = 100_000_000;
            a1.Inventory[H2] = 100_000_000;
            PendingFleetAction f1 = Haul(w1, a1, b1, B60);
            long tankH2 = TankLitres - GasCarriage.LegBurnLitres(w1, w1.Tuning, f1, H2, a1.Position, b1.Position);
            Assert.True(tankH2 < TankLitres, "the burn was priced, so the tank room is not the whole tank");
            Assert.Equal(holdIron, Load(w1, a1, b1, ref f1, Iron, 100_000_000));
            Assert.Equal(tankH2, Load(w1, a1, b1, ref f1, H2, 100_000_000));

            // Hydrogen first leaves the hold empty for the ore.
            WorldModel w2 = World();
            SpatialNode a2 = Node(w2, "A", new Vec3(0, 0, 0));
            SpatialNode b2 = Node(w2, "B", new Vec3(3000, 0, 0));
            a2.Inventory[Iron] = 100_000_000;
            a2.Inventory[H2] = 100_000_000;
            PendingFleetAction f2 = Haul(w2, a2, b2, B60);
            Assert.Equal(tankH2, Load(w2, a2, b2, ref f2, H2, 100_000_000));
            Assert.Equal(holdIron, Load(w2, a2, b2, ref f2, Iron, 100_000_000));
        }

        [Fact]
        public void ConvoySizeMultipliesTheTankAndTheBurn()
        {
            WorldModel world = World();
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[H2] = 100_000_000;
            PendingFleetAction fleet = Haul(world, a, b, B60, convoy: 3);

            long loaded = Load(world, a, b, ref fleet, H2, 100_000_000);

            long burn3 = GasCarriage.LegBurnLitres(world, world.Tuning, fleet, H2, a.Position, b.Position);
            Assert.InRange((double)burn3, 3 * OneHullLegBurn() * 0.995, 3 * OneHullLegBurn() * 1.005);
            Assert.Equal(3 * TankLitres - burn3, loaded);
            Assert.True(loaded > 2 * TankLitres, "three tanks carried more than two would");
        }

        [Fact]
        public void GasAlreadyAboardCountsAgainstTheTank()
        {
            WorldModel world = World(fuelDrawMode: FuelDrawModes.Off);
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[H2] = 100_000_000;
            PendingFleetAction fleet = Haul(world, a, b, B60);

            Assert.Equal(4_000_000L, Load(world, a, b, ref fleet, H2, 4_000_000));
            Assert.Equal(TankLitres - 4_000_000, Load(world, a, b, ref fleet, H2, 100_000_000));
            Assert.Equal(TankLitres, fleet.Cargo[H2]);
            Assert.Equal(0L, Load(world, a, b, ref fleet, H2, 100_000_000));
        }

        [Fact]
        public void ATradeBasketPutsGasInTheTanksAndSolidsInTheHold()
        {
            WorldModel world = World(fuelDrawMode: FuelDrawModes.Off);
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[Iron] = 100_000_000;
            a.Inventory[H2] = 100_000_000;
            a.Inventory[O2] = 100_000_000;
            PendingFleetAction fleet = Haul(world, a, b, B60);
            fleet.Kind = ActionKind.Trade;

            PendingFleetResolver.LoadTradeCargo(a, ref fleet, null, b.Position, world, world.Tuning,
                ItemVolumes, null, null, null, null);

            Assert.Equal((long)(HoldLitres / IronLitresPerUnit), fleet.Cargo[Iron]);
            Assert.Equal(TankLitres, fleet.Cargo[H2]);
            Assert.False(fleet.Cargo.ContainsKey(O2));
            Assert.Equal(100_000_000L, a.Inventory[O2]);
        }

        [Fact]
        public void RaidLootTakesGasOnlyIntoTheRaidersTanks()
        {
            WorldModel world = World(fuelDrawMode: FuelDrawModes.Off);
            SpatialNode target = Node(world, "T", new Vec3(0, 0, 0));
            target.Inventory[H2] = 100_000_000;
            target.Inventory[O2] = 100_000_000;
            target.Inventory[Iron] = 100_000_000;
            var raider = new PendingFleetAction { ShipPrefabId = B60, ConvoySize = 1 };

            Dictionary<string, long> loot = world.TakeRaidLootNodeInventory("T", 0.5, HoldLitres, null, ItemVolumes,
                gasTankRoom: key => GasCarriage.RoomLitres(world, world.Tuning, raider, key, target.Position, target.Position));

            Assert.Equal(TankLitres / 2, loot[H2]);
            Assert.False(loot.ContainsKey(O2));
            Assert.Equal((long)((HoldLitres / 2) / IronLitresPerUnit), loot[Iron]);
        }

        [Fact]
        public void ARaiderWithTanksAndNoHoldLootsGasIntoItsTanks()
        {
            WorldModel world = World(fuelDrawMode: FuelDrawModes.Off);
            SpatialNode target = Node(world, "T", new Vec3(0, 0, 0));
            target.Inventory[H2] = 100_000_000;
            target.Inventory[Iron] = 100_000_000;
            var raider = new PendingFleetAction { ShipPrefabId = B60, ConvoySize = 1 };
            Func<string, long> room = key => GasCarriage.RoomLitres(world, world.Tuning, raider, key, target.Position, target.Position);

            Dictionary<string, long> loot = world.TakeRaidLootNodeInventory("T", 0.5, 0, null, ItemVolumes,
                gasTankRoom: room);

            Assert.Equal(TankLitres / 2, loot[H2]);
            Assert.False(loot.ContainsKey(Iron));
            Assert.Equal(100_000_000L, target.Inventory[Iron]);
        }

        /// <summary>A hold of unknown size is packed by per-kind count caps. Gas in tanks is held
        /// under that cap and the tank room as well: whichever is smaller boards.</summary>
        [Fact]
        public void AnUnknownHoldKeepsTheTradeCountCapOnGas()
        {
            Func<long, long, long> basket = (countCap, tankStock) =>
            {
                WorldModel world = World(fuelDrawMode: FuelDrawModes.Off);
                world.Tuning.TradeCargoHydrogen = countCap;
                SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
                SpatialNode b = Node(world, "B", new Vec3(3000, 0, 0));
                a.Inventory[H2] = tankStock;
                PendingFleetAction fleet = Haul(world, a, b, B60);
                fleet.Kind = ActionKind.Trade;
                fleet.CargoCapacityLiters = 0;
                PendingFleetResolver.LoadTradeCargo(a, ref fleet, null, b.Position, world, world.Tuning,
                    ItemVolumes, null, null, null, null);
                long carried;
                return fleet.Cargo != null && fleet.Cargo.TryGetValue(H2, out carried) ? carried : 0;
            };

            Assert.Equal(1_000L, basket(1_000, 100_000_000));
            // The tank is the smaller ceiling here.
            Assert.Equal(TankLitres, basket(50_000_000, 100_000_000));
            // A cap of zero boards none.
            Assert.Equal(0L, basket(0, 100_000_000));
        }
    }
}
