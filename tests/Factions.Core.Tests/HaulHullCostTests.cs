using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="HaulHullCost"/> and the cost-ranked branch of <see cref="ShipPool.Consume"/>:
    /// a haul takes the hull that moves its load for the least fuel per litre, which is the light
    /// hull for a small load and the big hull for a big one. docs/issues/FAC-393.
    /// </summary>
    public class HaulHullCostTests
    {
        private const string Small = "Courier";     // 100 t, 10,000 L
        private const string Big = "Freighter";     // 1,000 t, 500,000 L
        private const string Unscanned = "Mystery"; // no dry mass: cannot be priced

        private static ThrustBand HydrogenBand()
        {
            return new ThrustBand
            {
                ThrusterType = "Hydrogen",
                ForceMagnitudeN = 7.2e6f,
                EffectivenessAtMinInfluence = 1,
                EffectivenessAtMaxInfluence = 1,
                MaxPowerMw = 7.5f,
                FuelId = "Gas:Hydrogen",
                FuelEfficiency = 1f,
                FuelEnergyDensityMwhPerL = 0.001556f,
            };
        }

        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, double> _dry = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                { Small, 1.0e5 }, { Big, 1.0e6 }, { Unscanned, 0 },
            };
            private readonly Dictionary<string, long> _cap = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                { Small, 10_000 }, { Big, 500_000 }, { Unscanned, 5_000 },
            };

            public long CargoLiters(string prefabId) { long c; return _cap.TryGetValue(prefabId, out c) ? c : 0; }
            public double DryMassKg(string prefabId) { double d; return _dry.TryGetValue(prefabId, out d) ? d : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
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
        }

        private sealed class FlatPrices : IItemPriceCatalogue
        {
            public long BasePrice(string subTypeId) { return 300; }
        }

        private static WorldModel World()
        {
            var world = new WorldModel();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            world.PrefabCatalogue = new Catalogue();
            world.PriceCatalogue = new FlatPrices();
            world.PriceOracle = new ScarceOracle(1.0);
            return world;
        }

        private static HaulHullCost Job(WorldModel world, double liters, Vec3? ferryFrom = null)
        {
            return new HaulHullCost(world, world.Tuning, "Source", new Vec3(0, 0, 0), new Vec3(3000, 0, 0),
                                    liters, cargoKgPerLiter: 1.0, ferryFromKm: ferryFrom);
        }

        private static string Pick(WorldModel world, HaulHullCost cost, params string[] stock)
        {
            var shipStock = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string id in stock) shipStock[id] = 1;
            string prefabId;
            int taken = ShipPool.Consume(shipStock, null, ActionKind.Haul, world.PrefabCatalogue, 1, out prefabId,
                                         null, null, cost);
            Assert.Equal(1, taken);
            return prefabId;
        }

        [Fact]
        public void ALoadBothHullsHold_IsCheaperPerLitreInTheLighterHull()
        {
            WorldModel world = World();
            HaulHullCost cost = Job(world, 1_000);

            Assert.True(cost.CostPerLiter(Small) > 0, "the leg really costs fuel, or the comparison is vacuous");
            Assert.True(cost.CostPerLiter(Small) < cost.CostPerLiter(Big));
            Assert.Equal(Small, Pick(world, cost, Small, Big));
        }

        /// <summary>The discriminator for the test above: a load only the big hull holds makes the big
        /// hull the cheaper one per litre, so the ranking is not simply "smallest first".</summary>
        [Fact]
        public void ALoadOnlyTheBigHullHolds_IsCheaperPerLitreInTheBigHull()
        {
            WorldModel world = World();
            HaulHullCost cost = Job(world, 500_000);

            Assert.Equal(10_000, cost.CarriedLiters(Small), 6);
            Assert.Equal(500_000, cost.CarriedLiters(Big), 6);
            Assert.True(cost.CostPerLiter(Big) < cost.CostPerLiter(Small));
            Assert.Equal(Big, Pick(world, cost, Small, Big));
        }

        [Fact]
        public void WithoutACost_TheLargestHoldIsTaken_AsBefore()
        {
            WorldModel world = World();
            var shipStock = new Dictionary<string, int>(StringComparer.Ordinal) { { Small, 1 }, { Big, 1 } };
            string prefabId;
            ShipPool.Consume(shipStock, null, ActionKind.Haul, world.PrefabCatalogue, 1, out prefabId);
            Assert.Equal(Big, prefabId);
        }

        /// <summary>A hull flown empty from a donor first pays for that leg too.</summary>
        [Fact]
        public void AFetchedHull_PaysForTheFerryLeg()
        {
            WorldModel world = World();
            double local = Job(world, 1_000).CostPerLiter(Small);
            double fetched = Job(world, 1_000, ferryFrom: new Vec3(-3000, 0, 0)).CostPerLiter(Small);
            Assert.True(fetched > local);
        }

        /// <summary>A hull with no scanned mass cannot be priced; it ranks after every priced hull and
        /// is still taken when it is all there is.</summary>
        [Fact]
        public void AnUnscannedHull_RanksLast_ButIsStillEligible()
        {
            WorldModel world = World();
            HaulHullCost cost = Job(world, 1_000);
            Assert.Equal(HaulHullCost.UnpricedCost, cost.CostPerLiter(Unscanned));
            Assert.Equal(Big, Pick(world, cost, Unscanned, Big));
            Assert.Equal(Unscanned, Pick(world, cost, Unscanned));
        }

        /// <summary>Fuel is valued at the departing node: the same burn costs more where fuel is
        /// scarce.</summary>
        [Fact]
        public void FuelIsValuedAtTheDepartingNodesScarcity()
        {
            WorldModel plain = World();
            WorldModel scarce = World();
            scarce.PriceOracle = new ScarceOracle(3.0);
            double at1 = Job(plain, 1_000).FuelCredits(Small, 1_000);
            double at3 = Job(scarce, 1_000).FuelCredits(Small, 1_000);
            Assert.True(at1 > 0);
            Assert.Equal(3.0 * at1, at3, 6);
        }

        private sealed class ScarceOracle : IPriceOracle
        {
            private readonly double _m;
            public ScarceOracle(double multiplier) { _m = multiplier; }
            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return _m; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference) { reference = 0; return false; }
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
        }
    }
}
