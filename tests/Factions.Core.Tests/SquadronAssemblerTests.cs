using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Verifies SquadronAssembler.Assemble's composition rules:
    ///   - Preferred: 1 large-grid combat ship + 1 cargo-capable ship + up to maxFighters small-grid
    ///     combat ships, drawn from the node's unified ship pool by prefab tag.
    ///   - Falls back to any-grid-size combat ship when nothing matches the preferred slots.
    ///   - Never assembles a squadron with zero combat power (a pure cargo hauler is never taken alone).
    ///   - Returns empty only when nothing available has any combat power.
    ///   - Never dips into store-reserved (ListedForSale) stock.
    ///   - Consumes chosen ships from the pool.
    /// </summary>
    public class SquadronAssemblerTests
    {
        private sealed class FakeCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, float> DpsByPrefab = new Dictionary<string, float>();
            public readonly Dictionary<string, long> CargoByPrefab = new Dictionary<string, long>();
            public readonly Dictionary<string, string> GridSizeByPrefab = new Dictionary<string, string>();
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>();
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> ThrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();

            public long CargoLiters(string prefabId)
            {
                long v;
                return CargoByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float dps;
                if (!DpsByPrefab.TryGetValue(prefabId, out dps) || dps <= 0) return new WeaponBand[0];
                return new[] { new WeaponBand { Dps = dps, RangeM = 800 } };
            }
            public string GridSize(string prefabId)
            {
                string g;
                return GridSizeByPrefab.TryGetValue(prefabId, out g) ? g : "Unknown";
            }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands;
                return ThrustBandsByPrefab.TryGetValue(prefabId, out bands)
                    ? bands : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return DryMassKgByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }

            public double DrillYieldScore(string prefabId) { return 0; }

            /// <summary>Down-facing Atmospheric-only thruster (dead in deep space) — used to prove
            /// SquadronAssembler's route gate excludes a route-infeasible candidate from a slot.</summary>
            public void SetAtmoOnlyDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Atmospheric", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                        EffectivenessAtMinInfluence = 0f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = true,
                    }},
                };
            }
        }

        private static readonly ShipPool.RouteEnvironment DeepSpaceRoute = new ShipPool.RouteEnvironment
        {
            OriginGravityG = 0, OriginInAtmosphere = false,
            DestGravityG = 0, DestInAtmosphere = false,
            MinDryTwr = 1.05,
        };

        private static WorldModel MakeWorld() => TestSupport.World(1);

        private static SpatialNode AddNode(WorldModel w, string id)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.6, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 0.8;
            return n;
        }

        private static void Stock(SpatialNode n, string prefabId, int count)
        {
            int stock;
            n.ShipStock.TryGetValue(prefabId, out stock);
            n.ShipStock[prefabId] = stock + count;
        }

        private static void Reserve(SpatialNode n, string prefabId, int count)
        {
            int reserved;
            n.ListedForSale.TryGetValue(prefabId, out reserved);
            n.ListedForSale[prefabId] = reserved + count;
        }

        [Fact]
        public void Assemble_PicksLargeCombatCargoAndFighters_WhenAllAvailable()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();

            cat.DpsByPrefab["BigGunship"] = 3000; cat.GridSizeByPrefab["BigGunship"] = "Large";
            Stock(node, "BigGunship", 1);

            cat.CargoByPrefab["Hauler"] = 5000; cat.GridSizeByPrefab["Hauler"] = "Large";
            Stock(node, "Hauler", 1);

            cat.DpsByPrefab["Fighter"] = 500; cat.GridSizeByPrefab["Fighter"] = "Small";
            Stock(node, "Fighter", 6); // more than maxFighters

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4);

            Assert.False(result.MissingLargeCombat);
            Assert.False(result.MissingCargo);
            Assert.Equal(1 + 1 + 4, result.Squadron.Count);
            Assert.Contains("BigGunship", result.Squadron);
            Assert.Contains("Hauler", result.Squadron);
            Assert.Equal(4, result.Squadron.FindAll(id => id == "Fighter").Count);

            // Consumed from the pool: 2 fighters left behind (6 in stock, 4 taken).
            Assert.Equal(2, node.ShipStock["Fighter"]);
            Assert.Equal(0, node.ShipStock["BigGunship"]);
            Assert.Equal(0, node.ShipStock["Hauler"]);
        }

        [Fact]
        public void Assemble_FallsBackToFightersOnly_WhenNoLargeOrCargoAvailable()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();

            cat.DpsByPrefab["Fighter"] = 500; cat.GridSizeByPrefab["Fighter"] = "Small";
            Stock(node, "Fighter", 2);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4);

            Assert.True(result.MissingLargeCombat);
            Assert.True(result.MissingCargo);
            Assert.Equal(2, result.Squadron.Count);
            Assert.All(result.Squadron, id => Assert.Equal("Fighter", id));
        }

        [Fact]
        public void Assemble_FallsBackToAnyGridSizeCombatShip_WhenNothingMatchesPreferredSlots()
        {
            // Combat-suitable but neither "Large" nor "Small" (e.g. a prefab the catalogue can't
            // classify by grid size) — must still be usable as the guaranteed minimum, not skipped.
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["OddHull"] = 700; // GridSize left unset -> "Unknown"
            Stock(node, "OddHull", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4);

            Assert.Single(result.Squadron);
            Assert.Equal("OddHull", result.Squadron[0]);
        }

        [Fact]
        public void Assemble_ReturnsEmpty_WhenNothingCombatSuitableExists()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();
            // Only a cargo-capable, unarmed ship in stock — no combat power anywhere.
            cat.CargoByPrefab["Hauler"] = 5000;
            Stock(node, "Hauler", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4);

            Assert.Empty(result.Squadron);
            // Nothing consumed when assembly finds no usable combat ship.
            Assert.Equal(1, node.ShipStock["Hauler"]);
        }

        [Fact]
        public void Assemble_ReturnsEmpty_WhenNodeHasNoStockAtAll()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4);

            Assert.Empty(result.Squadron);
            Assert.True(result.MissingLargeCombat);
            Assert.True(result.MissingCargo);
        }

        [Fact]
        public void Assemble_NeverDipsIntoStoreReservedStock()
        {
            // Sole fighter in stock is entirely reserved for sale — assembly must treat it as
            // unavailable, not raid the store's inventory.
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Fighter"] = 500; cat.GridSizeByPrefab["Fighter"] = "Small";
            Stock(node, "Fighter", 1);
            Reserve(node, "Fighter", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4);

            Assert.Empty(result.Squadron);
            Assert.Equal(1, node.ShipStock["Fighter"]); // untouched
            Assert.Equal(1, node.ListedForSale["Fighter"]); // still reserved
        }

        // ---- route-aware gating (SquadronAssembler routes through ShipPool.IsRouteFeasible) ----

        [Fact]
        public void Assemble_RouteGate_ExcludesAtmoOnlyLargeCombat_FromDeepSpaceTarget_FallsBackToFeasibleFighter()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();

            // Large combat ship: plenty of Dps, but Atmospheric-only thrust — dead in deep space.
            cat.DpsByPrefab["AtmoGunship"] = 3000; cat.GridSizeByPrefab["AtmoGunship"] = "Large";
            cat.SetAtmoOnlyDown("AtmoGunship", dryMassKg: 1000, forceN: 20000);
            Stock(node, "AtmoGunship", 1);

            // Small fighter: no thrust data at all (unscanned) -> "unknown, don't gate" -> feasible.
            cat.DpsByPrefab["Fighter"] = 500; cat.GridSizeByPrefab["Fighter"] = "Small";
            Stock(node, "Fighter", 2);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4, route: DeepSpaceRoute);

            Assert.True(result.MissingLargeCombat); // the only Large candidate was route-infeasible
            Assert.DoesNotContain("AtmoGunship", result.Squadron);
            Assert.Contains("Fighter", result.Squadron);
            Assert.Equal(1, node.ShipStock["AtmoGunship"]); // untouched — never consumed
        }

        [Fact]
        public void Assemble_RouteGate_ExcludesAtmoOnlyGuaranteedMinimum_ReturnsEmpty_WhenNothingElseFeasible()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();

            // Sole combat-suitable ship in stock: Atmospheric-only, dead in deep space. Even the
            // guaranteed-minimum fallback (any grid size) must respect the route gate.
            cat.DpsByPrefab["AtmoOddHull"] = 700;
            cat.SetAtmoOnlyDown("AtmoOddHull", dryMassKg: 500, forceN: 8000);
            Stock(node, "AtmoOddHull", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4, route: DeepSpaceRoute);

            Assert.Empty(result.Squadron);
            Assert.Equal(1, node.ShipStock["AtmoOddHull"]); // untouched
        }

        [Fact]
        public void Assemble_RouteGate_ExcludesAtmoOnlyCargo_FromDeepSpaceTarget()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();

            cat.DpsByPrefab["Fighter"] = 500; cat.GridSizeByPrefab["Fighter"] = "Small";
            Stock(node, "Fighter", 1);

            // Cargo-capable but Atmospheric-only thrust -> excluded from the cargo slot in deep space.
            cat.CargoByPrefab["AtmoHauler"] = 5000;
            cat.SetAtmoOnlyDown("AtmoHauler", dryMassKg: 800, forceN: 15000);
            Stock(node, "AtmoHauler", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4, route: DeepSpaceRoute);

            Assert.True(result.MissingCargo);
            Assert.DoesNotContain("AtmoHauler", result.Squadron);
            Assert.Equal(1, node.ShipStock["AtmoHauler"]); // untouched
        }

        [Fact]
        public void Assemble_NoRouteEnvironment_BehavesIdenticallyToBeforeRouteGate()
        {
            // Regression guard: an Atmospheric-only hull with route data present must still be
            // selected exactly as before when no RouteEnvironment is supplied at all.
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["AtmoGunship"] = 3000; cat.GridSizeByPrefab["AtmoGunship"] = "Large";
            cat.SetAtmoOnlyDown("AtmoGunship", dryMassKg: 1000, forceN: 20000);
            Stock(node, "AtmoGunship", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4); // route omitted

            Assert.False(result.MissingLargeCombat);
            Assert.Contains("AtmoGunship", result.Squadron);
        }

        [Fact]
        public void Assemble_PicksUnreservedUnits_LeavingReservedStockAlone()
        {
            // 3 in stock, 1 reserved for sale -> only 2 are actually available to the squadron.
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Fighter"] = 500; cat.GridSizeByPrefab["Fighter"] = "Small";
            Stock(node, "Fighter", 3);
            Reserve(node, "Fighter", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4);

            Assert.Equal(2, result.Squadron.Count);
            Assert.Equal(1, node.ShipStock["Fighter"]); // 2 consumed, 1 left (the reserved one)
            Assert.Equal(1, node.ListedForSale["Fighter"]); // reservation untouched
        }

        // ---- FAC-422: the cargo slot, one pool, and where each hull came from ----

        private static FakeCatalogue CargoCatalogue()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Gunship"] = 3000; cat.GridSizeByPrefab["Gunship"] = "Large";
            cat.DpsByPrefab["ArmedHauler"] = 1000; cat.CargoByPrefab["ArmedHauler"] = 50000;
            cat.GridSizeByPrefab["ArmedHauler"] = "Large";
            cat.CargoByPrefab["Drill"] = 375000; cat.GridSizeByPrefab["Drill"] = "Small";
            return cat;
        }

        /// <summary>Operator, 2026-09-29: the cargo slot can be a combat ship with decent cargo. An
        /// armed hull whose hold covers the loot takes it, and the 0-Dps drill ship stays home.</summary>
        [Fact]
        public void CargoSlot_TakesAnArmedHullWithAdequateHold_OverAHauler()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            FakeCatalogue cat = CargoCatalogue();
            Stock(node, "Gunship", 1); Stock(node, "ArmedHauler", 1); Stock(node, "Drill", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4, lootLiters: 40000);

            Assert.False(result.MissingCargo);
            Assert.Equal(new[] { "Gunship", "ArmedHauler" }, result.Squadron);
            Assert.Equal(1, node.ShipStock["Drill"]);
        }

        /// <summary>Adequate is the squadron's summed hold against what the raid can carry off: the
        /// lead's own hold counts, so a small armed hold that tops it up is enough.</summary>
        [Fact]
        public void CargoSlot_Adequacy_CountsTheHoldAlreadyInTheSquadron()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            FakeCatalogue cat = CargoCatalogue();
            cat.CargoByPrefab["Gunship"] = 200000;
            Stock(node, "Gunship", 1); Stock(node, "ArmedHauler", 1); Stock(node, "Drill", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4, lootLiters: 240000);

            Assert.Contains("ArmedHauler", result.Squadron);
            Assert.DoesNotContain("Drill", result.Squadron);
        }

        /// <summary>...and a non-combat hauler only when no armed hull brings the hold up to the loot:
        /// the fallback is the biggest hold available.</summary>
        [Fact]
        public void CargoSlot_FallsBackToTheHauler_WhenNoArmedHullCarriesTheLoot()
        {
            WorldModel w = MakeWorld();
            SpatialNode node = AddNode(w, "N1");
            FakeCatalogue cat = CargoCatalogue();
            Stock(node, "Gunship", 1); Stock(node, "ArmedHauler", 1); Stock(node, "Drill", 1);

            var result = SquadronAssembler.Assemble(node, cat, maxFighters: 4, lootLiters: 300000);

            Assert.False(result.MissingCargo);
            Assert.Equal(new[] { "Gunship", "Drill" }, result.Squadron);
            Assert.Equal(1, node.ShipStock["ArmedHauler"]);
        }

        /// <summary>One pool: the lead is the strongest large hull wherever it is parked, and each
        /// member's source is recorded so a refusal can send it home.</summary>
        [Fact]
        public void Assemble_DrawsTheStrongestHullFromAnySource_AndRecordsWhereEachCameFrom()
        {
            WorldModel w = MakeWorld();
            SpatialNode origin = AddNode(w, "O");
            SpatialNode depot = AddNode(w, "D");
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Weak"] = 500; cat.GridSizeByPrefab["Weak"] = "Large";
            cat.DpsByPrefab["Strong"] = 5000; cat.GridSizeByPrefab["Strong"] = "Large";
            Stock(origin, "Weak", 5);
            Stock(depot, "Strong", 2);
            var contributors = new List<string>();

            var result = SquadronAssembler.Assemble(origin, cat, maxFighters: 0, maxHulls: 3, targetDps: 100000,
                                                    reinforcements: new[] { depot }, contributingNodeIds: contributors);

            Assert.Equal(new[] { "Strong", "Strong", "Weak" }, result.Squadron);
            Assert.Equal(new[] { "D", "D", "O" }, result.SourceNodeIds);
            Assert.Equal(new[] { "D" }, contributors);
            Assert.Equal(4, origin.ShipStock["Weak"]);
            Assert.Equal(0, depot.ShipStock["Strong"]);
        }

        /// <summary>A hull that cannot fly its own leg to the origin cannot join the squadron there.</summary>
        [Fact]
        public void Assemble_SkipsAReinforcementHull_ThatCannotReachTheOrigin()
        {
            WorldModel w = MakeWorld();
            SpatialNode origin = AddNode(w, "O");
            SpatialNode depot = AddNode(w, "D");
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Fighter"] = 500; cat.GridSizeByPrefab["Fighter"] = "Small";
            cat.DpsByPrefab["AtmoGunship"] = 3000; cat.GridSizeByPrefab["AtmoGunship"] = "Large";
            cat.SetAtmoOnlyDown("AtmoGunship", dryMassKg: 1000, forceN: 20000);
            Stock(origin, "Fighter", 1);
            Stock(depot, "AtmoGunship", 1);

            var result = SquadronAssembler.Assemble(origin, cat, maxFighters: 4, maxHulls: 5, targetDps: 100000,
                                                    reinforcements: new[] { depot },
                                                    musterRoute: n => DeepSpaceRoute);

            Assert.Equal(new[] { "Fighter" }, result.Squadron);
            Assert.Equal(1, depot.ShipStock["AtmoGunship"]);
        }
    }
}
