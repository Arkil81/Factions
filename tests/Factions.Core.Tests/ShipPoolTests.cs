using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the unified ship-pool query/consume/return surface (<see cref="ShipPool"/>) and the
    /// Persistence-side migration off the old role-keyed ShipStock/ShipPrefabQueue shape.
    /// </summary>
    public class ShipPoolTests
    {
        private sealed class FakeCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, long> CargoByPrefab = new Dictionary<string, long>();
            public readonly Dictionary<string, float> DpsByPrefab = new Dictionary<string, float>();
            public readonly Dictionary<string, double> DrillByPrefab = new Dictionary<string, double>();
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
                return GridSizeByPrefab.TryGetValue(prefabId, out g) ? g : "Small";
            }

            public readonly Dictionary<string, string[]> NamesByRole = new Dictionary<string, string[]>(StringComparer.Ordinal);
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                string[] names;
                return role != null && NamesByRole.TryGetValue(role, out names) ? names : new string[0];
            }

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

            public double DrillYieldScore(string prefabId)
            {
                double v;
                return DrillByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }
        }

        // Vanilla-shaped curves (matches the live-scanned data): Ion is full in deep space, ~20% at
        // a planet surface; Atmospheric is zero outside atmosphere, full at a planet surface;
        // Hydrogen is full everywhere.
        private static ThrustBand IonBand(float forceN) => new ThrustBand
        {
            ThrusterType = "Ion", ForceMagnitudeN = forceN,
            MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
            EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 0.2f,
            NeedsAtmosphereForInfluence = false,
        };

        private static ThrustBand AtmoBand(float forceN) => new ThrustBand
        {
            ThrusterType = "Atmospheric", ForceMagnitudeN = forceN,
            MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
            EffectivenessAtMinInfluence = 0f, EffectivenessAtMaxInfluence = 1f,
            NeedsAtmosphereForInfluence = true,
        };

        private static ThrustBand HydrogenBand(float forceN) => new ThrustBand
        {
            ThrusterType = "Hydrogen", ForceMagnitudeN = forceN,
            MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
            EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
            NeedsAtmosphereForInfluence = false,
        };

        private static void SetDownThrust(FakeCatalogue cat, string prefabId, double dryMassKg, ThrustBand downBand)
        {
            cat.DryMassKgByPrefab[prefabId] = dryMassKg;
            cat.ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                [ThrustDirection.Down] = new List<ThrustBand> { downBand },
            };
        }

        private static SpatialNode MakeNode(string id = "N1")
        {
            var graph = new SpatialGraph();
            return graph.Add(new Vec3(0, 0, 0), 1.0, id);
        }

        // ---- IsSuitableFor / RequiresShip ---------------------------------------------------

        [Theory]
        [InlineData(ActionKind.Trade, true)]
        [InlineData(ActionKind.Gift, true)]
        [InlineData(ActionKind.Haul, true)]
        [InlineData(ActionKind.Mine, true)]
        [InlineData(ActionKind.Raid, true)]
        [InlineData(ActionKind.Retaliate, true)]
        [InlineData(ActionKind.Conquer, true)]
        [InlineData(ActionKind.Guard, true)]
        [InlineData(ActionKind.Patrol, true)]
        [InlineData(ActionKind.Expand, false)]
        [InlineData(ActionKind.Develop, false)]
        public void RequiresShip_MatchesTheKindsIsSuitableForEverGates(ActionKind kind, bool expected)
        {
            Assert.Equal(expected, ShipPool.RequiresShip(kind));
        }

        [Fact]
        public void IsSuitableFor_TradeAndGift_WantCargoNotCombat()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["Hauler"] = 5000;
            cat.DpsByPrefab["Gunship"] = 500;

            Assert.True(ShipPool.IsSuitableFor(ActionKind.Trade, cat, "Hauler"));
            Assert.False(ShipPool.IsSuitableFor(ActionKind.Trade, cat, "Gunship"));
            Assert.True(ShipPool.IsSuitableFor(ActionKind.Gift, cat, "Hauler"));
        }

        [Fact]
        public void IsSuitableFor_Mine_RequiresARealDrill_NotJustCargo()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["Hauler"] = 5000;             // cargo but no drill -- Trade ship, not a miner
            cat.CargoByPrefab["MinerShip"] = 3000;
            cat.DrillByPrefab["MinerShip"] = 42;             // real drill + cargo -- a genuine miner
            cat.DrillByPrefab["DrillNoCargo"] = 42;          // drill but nowhere to put the ore

            Assert.False(ShipPool.IsSuitableFor(ActionKind.Mine, cat, "Hauler"));
            Assert.True(ShipPool.IsSuitableFor(ActionKind.Mine, cat, "MinerShip"));
            Assert.False(ShipPool.IsSuitableFor(ActionKind.Mine, cat, "DrillNoCargo"));
        }

        [Fact]
        public void IsMineSuitable_MatchesIsSuitableForMine()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["MinerShip"] = 3000;
            cat.DrillByPrefab["MinerShip"] = 42;

            Assert.True(ShipPool.IsMineSuitable(cat, "MinerShip"));
            Assert.False(ShipPool.IsMineSuitable(cat, "Hauler"));
            Assert.False(ShipPool.IsMineSuitable(null, "MinerShip"));
        }

        // GE_BioResearch (a GlobalEncounter station) carries a real drill block, cargo, and weapons
        // in its own scanned stats, but is a static station -- none of the catalogue-based
        // suitability wrappers should ever treat it as a dispatchable ship.
        [Fact]
        public void SuitabilityWrappers_Station_AlwaysFalse_EvenWithRealStats()
        {
            var cat = new FakeCatalogue();
            cat.GridSizeByPrefab["GE_BioResearch"] = "Station";
            cat.DrillByPrefab["GE_BioResearch"] = 703.2;
            cat.CargoByPrefab["GE_BioResearch"] = 991250;
            cat.DpsByPrefab["GE_BioResearch"] = 2587.5f;

            Assert.False(ShipPool.IsMineSuitable(cat, "GE_BioResearch"));
            Assert.False(ShipPool.IsCargoSuitable(cat, "GE_BioResearch"));
            Assert.False(ShipPool.IsCombatSuitable(cat, "GE_BioResearch"));
            Assert.False(ShipPool.IsSuitableFor(ActionKind.Mine, cat, "GE_BioResearch"));
        }

        // ---- pure-value suitability + minimum-capability floors (capability classification) -----

        [Theory]
        [InlineData(0, 0)]     // unarmed, no floor -- still false (nonzero is always required)
        [InlineData(500, 0)]   // armed, no floor -- default behavior unchanged
        [InlineData(500, 500)] // exactly at the floor -- inclusive
        public void IsCombatSuitableValue_TrueCases(double dps, double minDps)
        {
            Assert.Equal(dps > 0, ShipPool.IsCombatSuitableValue(dps, minDps));
        }

        [Fact]
        public void IsCombatSuitableValue_BelowFloor_False()
        {
            Assert.False(ShipPool.IsCombatSuitableValue(499, 500));
        }

        [Fact]
        public void IsCargoSuitableValue_BelowFloor_False()
        {
            Assert.False(ShipPool.IsCargoSuitableValue(499, 500));
            Assert.True(ShipPool.IsCargoSuitableValue(500, 500));
            Assert.False(ShipPool.IsCargoSuitableValue(0, 0)); // zero cargo is never suitable, floor or not
        }

        [Fact]
        public void IsMineSuitableValue_BelowFloor_False()
        {
            Assert.False(ShipPool.IsMineSuitableValue(41, 3000, 42));
            Assert.True(ShipPool.IsMineSuitableValue(42, 3000, 42));
            Assert.False(ShipPool.IsMineSuitableValue(42, 0, 42)); // no cargo -- still fails regardless of floor
        }

        // A station can carry real drill/weapon/cargo blocks (e.g. GE_BioResearch, a GlobalEncounter
        // station with a drill arm and cargo hold) without being a dispatchable "ship" -- GridSize
        // must gate every suitability check regardless of how strong the underlying stats are.
        [Fact]
        public void SuitabilityValues_Station_AlwaysFalse_RegardlessOfStats()
        {
            Assert.False(ShipPool.IsCombatSuitableValue(2587.5, 0, "Station"));
            Assert.False(ShipPool.IsCargoSuitableValue(991250, 0, "Station"));
            Assert.False(ShipPool.IsMineSuitableValue(703.2, 991250, 0, "Station"));

            // Non-station grid sizes (and the default null/unknown) are unaffected.
            Assert.True(ShipPool.IsCombatSuitableValue(2587.5, 0, "Large"));
            Assert.True(ShipPool.IsCargoSuitableValue(991250, 0, "Small"));
            Assert.True(ShipPool.IsMineSuitableValue(703.2, 991250, 0, null));
        }

        [Fact]
        public void IsCombatSuitable_DefaultFloor_MatchesOriginalNonzeroGate()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Gunship"] = 1;
            cat.DpsByPrefab["Unarmed"] = 0;

            Assert.True(ShipPool.IsCombatSuitable(cat, "Gunship"));
            Assert.False(ShipPool.IsCombatSuitable(cat, "Unarmed"));
        }

        [Fact]
        public void IsCombatSuitable_WithFloor_ExcludesIncidentalWeapons()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["TokenDefense"] = 10;  // e.g. a single point-defense turret
            cat.DpsByPrefab["RealWarship"] = 500;

            Assert.False(ShipPool.IsCombatSuitable(cat, "TokenDefense", minDps: 100));
            Assert.True(ShipPool.IsCombatSuitable(cat, "RealWarship", minDps: 100));
        }

        [Fact]
        public void IsCargoSuitable_WithFloor_ExcludesIncidentalCargo()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["DroneWithSpareCrate"] = 125; // one small-grid 1x1x1 block's worth
            cat.CargoByPrefab["RealHauler"] = 15000;

            Assert.False(ShipPool.IsCargoSuitable(cat, "DroneWithSpareCrate", minCargoLiters: 500));
            Assert.True(ShipPool.IsCargoSuitable(cat, "RealHauler", minCargoLiters: 500));
        }

        [Fact]
        public void IsMineSuitable_WithFloor_ExcludesWeakDrills()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["WeakMiner"] = 3000;
            cat.DrillByPrefab["WeakMiner"] = 5;
            cat.CargoByPrefab["StrongMiner"] = 3000;
            cat.DrillByPrefab["StrongMiner"] = 50;

            Assert.False(ShipPool.IsMineSuitable(cat, "WeakMiner", minDrillYield: 40));
            Assert.True(ShipPool.IsMineSuitable(cat, "StrongMiner", minDrillYield: 40));
        }

        [Fact]
        public void TotalDps_Bands_SumsAcrossBands()
        {
            var bands = new[]
            {
                new WeaponBand { Dps = 100, RangeM = 500 },
                new WeaponBand { Dps = 50, RangeM = 2000 },
            };
            Assert.Equal(150, ShipPool.TotalDps(bands));
            Assert.Equal(0, ShipPool.TotalDps((System.Collections.Generic.IReadOnlyList<WeaponBand>)null));
        }

        [Theory]
        [InlineData(ActionKind.Raid)]
        [InlineData(ActionKind.Retaliate)]
        [InlineData(ActionKind.Conquer)]
        [InlineData(ActionKind.Guard)]
        [InlineData(ActionKind.Patrol)]
        public void IsSuitableFor_CombatKinds_WantCombatNotCargo(ActionKind kind)
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["Hauler"] = 5000;
            cat.DpsByPrefab["Gunship"] = 500;

            Assert.True(ShipPool.IsSuitableFor(kind, cat, "Gunship"));
            Assert.False(ShipPool.IsSuitableFor(kind, cat, "Hauler"));
        }

        [Fact]
        public void IsSuitableFor_KindThatNeverNeedsAShip_AlwaysFalse()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["Hauler"] = 5000;
            cat.DpsByPrefab["Hauler"] = 500; // even a ship suitable for everything else

            Assert.False(ShipPool.IsSuitableFor(ActionKind.Expand, cat, "Hauler"));
        }

        // ---- Available / the ListedForSale floor --------------------------------------------

        [Fact]
        public void Available_SubtractsListedForSale_FlooredAtZero()
        {
            SpatialNode n = MakeNode();
            n.ShipStock["Hauler"] = 3;
            n.ListedForSale["Hauler"] = 5; // over-reserved (shouldn't happen, but must not go negative)

            Assert.Equal(0, ShipPool.Available(n, "Hauler"));
        }

        [Fact]
        public void Available_UnreservedUnitsAreVisible()
        {
            SpatialNode n = MakeNode();
            n.ShipStock["Hauler"] = 5;
            n.ListedForSale["Hauler"] = 2;

            Assert.Equal(3, ShipPool.Available(n, "Hauler"));
        }

        [Fact]
        public void HasSuitable_NeverReportsReservedStockAsAvailable()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Gunship"] = 500;

            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 1;
            n.ListedForSale["Gunship"] = 1; // fully reserved

            Assert.False(ShipPool.HasSuitable(n, ActionKind.Raid, cat));
        }

        [Fact]
        public void NetworkHasSuitable_TrueWhenAnyOwnedNodeQualifies()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Gunship"] = 500;

            SpatialNode empty = MakeNode("N1");
            SpatialNode stocked = MakeNode("N2");
            stocked.ShipStock["Gunship"] = 1;

            Assert.True(ShipPool.NetworkHasSuitable(new[] { empty, stocked }, ActionKind.Raid, cat));
            Assert.False(ShipPool.NetworkHasSuitable(new[] { empty }, ActionKind.Raid, cat));
        }

        [Fact]
        public void NetworkSuitableCount_SumsAvailableUnitsAcrossNodes_WithoutDoubleCounting()
        {
            // Raid and Patrol draw from the same combat-suitable pool now — a ship counts once
            // per kind queried, not once per (kind, node) pair summed separately elsewhere.
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Gunship"] = 500;

            SpatialNode a = MakeNode("N1");
            a.ShipStock["Gunship"] = 2;
            SpatialNode b = MakeNode("N2");
            b.ShipStock["Gunship"] = 3;
            b.ListedForSale["Gunship"] = 1; // one reserved, not countable

            int raidCount = ShipPool.NetworkSuitableCount(new[] { a, b }, ActionKind.Raid, cat);
            int patrolCount = ShipPool.NetworkSuitableCount(new[] { a, b }, ActionKind.Patrol, cat);

            Assert.Equal(4, raidCount);   // 2 + (3-1)
            Assert.Equal(4, patrolCount); // same pool, same count — not summed on top of raidCount
        }

        // ---- Consume ---------------------------------------------------------------------------

        [Fact]
        public void Consume_PicksHighestScoringEligiblePrefab()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["WeakGunship"] = 100;
            cat.DpsByPrefab["StrongGunship"] = 900;

            SpatialNode n = MakeNode();
            n.ShipStock["WeakGunship"] = 5;
            n.ShipStock["StrongGunship"] = 5;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Raid, cat, 1, out prefabId);

            Assert.Equal(1, consumed);
            Assert.Equal("StrongGunship", prefabId);
            Assert.Equal(4, n.ShipStock["StrongGunship"]);
            Assert.Equal(5, n.ShipStock["WeakGunship"]); // untouched
        }

        [Fact]
        public void Consume_CapsAtAvailableStock_PartialFulfillment()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Gunship"] = 500;

            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 2;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Raid, cat, 10, out prefabId);

            Assert.Equal(2, consumed); // capped, not the requested 10
            Assert.Equal("Gunship", prefabId);
            Assert.Equal(0, n.ShipStock["Gunship"]);
        }

        [Fact]
        public void Consume_NeverDipsIntoReservedStock()
        {
            var cat = new FakeCatalogue();
            cat.DpsByPrefab["Gunship"] = 500;

            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 1;
            n.ListedForSale["Gunship"] = 1; // the only unit is reserved for sale

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Raid, cat, 1, out prefabId);

            Assert.Equal(0, consumed);
            Assert.Null(prefabId);
            Assert.Equal(1, n.ShipStock["Gunship"]); // untouched — reservation held
        }

        [Fact]
        public void Consume_NothingEligible_ReturnsZeroAndNullPrefab()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["Hauler"] = 5000; // cargo-suitable, not combat-suitable

            SpatialNode n = MakeNode();
            n.ShipStock["Hauler"] = 3;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Raid, cat, 1, out prefabId);

            Assert.Equal(0, consumed);
            Assert.Null(prefabId);
        }

        // ---- cargo purposes leave the drills alone --------------------------------------------

        [Fact]
        public void Consume_ForCargoKind_PrefersNonDrillHull_WhenBothAreFeasible()
        {
            // Haul dispatches before Mine every ops cycle (Mine's shortfall is by definition what is
            // left after Haul's pass) and both draw from this one untagged pool -- so a hull that can
            // both haul and drill was consistently taken for cargo before Mine ever looked, and the
            // Ship:Mine demand that followed built a replacement Haul took too.
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["PureHauler"] = 1000;
            cat.CargoByPrefab["DrillHauler"] = 9000;   // strictly better on the cargo score
            cat.DrillByPrefab["DrillHauler"] = 500;

            SpatialNode n = MakeNode();
            n.ShipStock["PureHauler"] = 1;
            n.ShipStock["DrillHauler"] = 1;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Haul, cat, 1, out prefabId);

            Assert.Equal(1, consumed);
            Assert.Equal("PureHauler", prefabId);
            Assert.Equal(1, n.ShipStock["DrillHauler"]); // the drill is still there for Mine
        }

        [Fact]
        public void Consume_ForCargoKind_FallsBackToDrillHull_WhenItIsTheOnlyCandidate()
        {
            // The preference must never block a dispatch that would have succeeded before.
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["DrillHauler"] = 9000;
            cat.DrillByPrefab["DrillHauler"] = 500;

            SpatialNode n = MakeNode();
            n.ShipStock["DrillHauler"] = 1;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Haul, cat, 1, out prefabId);

            Assert.Equal(1, consumed);
            Assert.Equal("DrillHauler", prefabId);
        }

        [Fact]
        public void Consume_ForMineKind_IsUnaffectedByTheCargoDrillPreference()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["PureHauler"] = 1000;
            cat.CargoByPrefab["DrillHauler"] = 9000;
            cat.DrillByPrefab["DrillHauler"] = 500;

            SpatialNode n = MakeNode();
            n.ShipStock["PureHauler"] = 1;
            n.ShipStock["DrillHauler"] = 1;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Mine, cat, 1, out prefabId);

            Assert.Equal(1, consumed);
            Assert.Equal("DrillHauler", prefabId); // a hauler was never Mine-eligible to begin with
        }

        [Fact]
        public void SuitableCountForRole_CountsOnlyRoleEligibleAvailableUnits()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["Hauler"] = 1000;
            cat.DrillByPrefab["Miner"] = 500;
            cat.CargoByPrefab["Miner"] = 800;

            SpatialNode n = MakeNode();
            n.ShipStock["Hauler"] = 4;
            n.ShipStock["Miner"] = 3;
            n.ListedForSale["Miner"] = 1; // store-reserved units are never fleet-available

            // 4 Haulers + the 2 unreserved Miners: a drill hull with a hold is cargo-suitable too --
            // that overlap is exactly the shared pool the drill preference in Consume exists to manage.
            Assert.Equal(6, ShipPool.SuitableCountForRole(n, "Trade", cat));
            Assert.Equal(2, ShipPool.SuitableCountForRole(n, "Mine", cat));
            Assert.Equal(0, ShipPool.SuitableCountForRole(n, "Raid", cat));
        }

        // ---- Consume with RouteEnvironment (thrust-aware route gate) -----------------------

        private static readonly ShipPool.RouteEnvironment DeepSpaceRoute = new ShipPool.RouteEnvironment
        {
            OriginGravityG = 0, OriginInAtmosphere = false,
            DestGravityG = 0, DestInAtmosphere = false,
            MinDryTwr = 1.05,
        };

        private static readonly ShipPool.RouteEnvironment SurfaceRoute = new ShipPool.RouteEnvironment
        {
            OriginGravityG = 1.0, OriginInAtmosphere = true, OriginAirDensity = 1.0,
            DestGravityG = 1.0, DestInAtmosphere = true, DestAirDensity = 1.0,
            MinDryTwr = 1.05,
        };

        [Fact]
        public void Consume_RouteGate_AtmoOnlyHull_ExcludedFromDeepSpace_SelectedForSurface()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["AtmoHauler"] = 5000;
            SetDownThrust(cat, "AtmoHauler", dryMassKg: 1000, downBand: AtmoBand(20000)); // TWR~2.04 at surface, 0 in deep space

            SpatialNode deepSpaceNode = MakeNode();
            deepSpaceNode.ShipStock["AtmoHauler"] = 1;
            string prefabId;
            int consumed = ShipPool.Consume(deepSpaceNode, ActionKind.Trade, cat, 1, out prefabId, DeepSpaceRoute);
            Assert.Equal(0, consumed);
            Assert.Null(prefabId);
            Assert.Equal(1, deepSpaceNode.ShipStock["AtmoHauler"]); // untouched — nothing consumed

            SpatialNode surfaceNode = MakeNode("N2");
            surfaceNode.ShipStock["AtmoHauler"] = 1;
            consumed = ShipPool.Consume(surfaceNode, ActionKind.Trade, cat, 1, out prefabId, SurfaceRoute);
            Assert.Equal(1, consumed);
            Assert.Equal("AtmoHauler", prefabId);
        }

        [Fact]
        public void Consume_RouteGate_IonOnlyHull_SelectedForDeepSpace_ExcludedFromSurface()
        {
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["IonHauler"] = 5000;
            SetDownThrust(cat, "IonHauler", dryMassKg: 1000, downBand: IonBand(20000)); // +Inf TWR in deep space, ~0.41 at surface

            SpatialNode deepSpaceNode = MakeNode();
            deepSpaceNode.ShipStock["IonHauler"] = 1;
            string prefabId;
            int consumed = ShipPool.Consume(deepSpaceNode, ActionKind.Trade, cat, 1, out prefabId, DeepSpaceRoute);
            Assert.Equal(1, consumed);
            Assert.Equal("IonHauler", prefabId);

            SpatialNode surfaceNode = MakeNode("N2");
            surfaceNode.ShipStock["IonHauler"] = 1;
            consumed = ShipPool.Consume(surfaceNode, ActionKind.Trade, cat, 1, out prefabId, SurfaceRoute);
            Assert.Equal(0, consumed); // 20000*0.2 / (1000*1*9.81) ~= 0.41, well below MinDryTwr=1.05
            Assert.Null(prefabId);
        }

        [Fact]
        public void Consume_RouteGate_NeverScanned_TreatedAsUnknown_NotGated()
        {
            // DryMassKg defaults to 0 (never scanned) — must never be gated, even against a route
            // no real hull could survive, per the rollout-safety "unknown, don't gate" rule.
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["UnscannedHauler"] = 5000;

            SpatialNode n = MakeNode();
            n.ShipStock["UnscannedHauler"] = 1;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Trade, cat, 1, out prefabId, DeepSpaceRoute);

            Assert.Equal(1, consumed);
            Assert.Equal("UnscannedHauler", prefabId);
        }

        [Fact]
        public void Consume_RouteGate_ScannedButNoDownBucket_ExcludedEverywhere()
        {
            // DryMassKg > 0 (genuinely scanned) but only a Backward band — no lift thrusters at all.
            // Distinct from the unscanned case above: this is a real, intentional gate failure.
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["NoLiftHauler"] = 5000;
            cat.DryMassKgByPrefab["NoLiftHauler"] = 1000;
            cat.ThrustBandsByPrefab["NoLiftHauler"] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                [ThrustDirection.Backward] = new List<ThrustBand> { HydrogenBand(50000) },
            };

            SpatialNode deepSpaceNode = MakeNode();
            deepSpaceNode.ShipStock["NoLiftHauler"] = 1;
            string prefabId;
            int consumed = ShipPool.Consume(deepSpaceNode, ActionKind.Trade, cat, 1, out prefabId, DeepSpaceRoute);
            Assert.Equal(0, consumed);

            SpatialNode surfaceNode = MakeNode("N2");
            surfaceNode.ShipStock["NoLiftHauler"] = 1;
            consumed = ShipPool.Consume(surfaceNode, ActionKind.Trade, cat, 1, out prefabId, SurfaceRoute);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void Consume_RouteGate_MinDryTwr_FlipsEligibility()
        {
            // Fixed hull: surface TWR = 20000 / (1000*1*9.81) ~= 2.039. A threshold comfortably
            // below that must pass; one comfortably above must fail — same hull, same route
            // gravity/atmosphere, only the tuning floor changes.
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["EdgeHauler"] = 5000;
            SetDownThrust(cat, "EdgeHauler", dryMassKg: 1000, downBand: AtmoBand(20000));

            var belowThreshold = new ShipPool.RouteEnvironment
            {
                OriginGravityG = 1.0, OriginInAtmosphere = true, OriginAirDensity = 1.0,
                DestGravityG = 1.0, DestInAtmosphere = true, DestAirDensity = 1.0,
                MinDryTwr = 1.5, // hull's ~2.04 clears this
            };
            SpatialNode n1 = MakeNode();
            n1.ShipStock["EdgeHauler"] = 1;
            string prefabId;
            int consumed = ShipPool.Consume(n1, ActionKind.Trade, cat, 1, out prefabId, belowThreshold);
            Assert.Equal(1, consumed);

            var aboveThreshold = new ShipPool.RouteEnvironment
            {
                OriginGravityG = 1.0, OriginInAtmosphere = true, OriginAirDensity = 1.0,
                DestGravityG = 1.0, DestInAtmosphere = true, DestAirDensity = 1.0,
                MinDryTwr = 2.5, // hull's ~2.04 now falls short
            };
            SpatialNode n2 = MakeNode("N2");
            n2.ShipStock["EdgeHauler"] = 1;
            consumed = ShipPool.Consume(n2, ActionKind.Trade, cat, 1, out prefabId, aboveThreshold);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void Consume_RouteGate_PrefersHigherCruiseForce_AmongFeasibleCandidates()
        {
            // Both candidates clear the TWR gate at both ends; the RouteScoreBonus tie-break should
            // prefer the one with more Backward-bucket cruise force per kilogram.
            var cat = new FakeCatalogue();
            cat.CargoByPrefab["SlowHauler"] = 5000;
            cat.CargoByPrefab["FastHauler"] = 5000; // same cargo — score would otherwise tie
            SetDownThrust(cat, "SlowHauler", dryMassKg: 1000, downBand: HydrogenBand(20000));
            SetDownThrust(cat, "FastHauler", dryMassKg: 1000, downBand: HydrogenBand(20000));
            cat.ThrustBandsByPrefab["SlowHauler"][ThrustDirection.Backward] = new List<ThrustBand> { HydrogenBand(1000) };
            cat.ThrustBandsByPrefab["FastHauler"][ThrustDirection.Backward] = new List<ThrustBand> { HydrogenBand(9000) };

            SpatialNode n = MakeNode();
            n.ShipStock["SlowHauler"] = 1;
            n.ShipStock["FastHauler"] = 1;

            string prefabId;
            int consumed = ShipPool.Consume(n, ActionKind.Trade, cat, 1, out prefabId, DeepSpaceRoute);

            Assert.Equal(1, consumed);
            Assert.Equal("FastHauler", prefabId);
        }

        // ---- RouteEnvironment.MaxCargoMassKg (Slice E: loaded-TWR cargo ceiling) ------------

        [Fact]
        public void MaxCargoMassKg_DeepSpace_NoGravityFloor_ReturnsInfinity_NoTeeth()
        {
            // The exact loophole the gravity-floor caveat exists to close: real deep-space gravity
            // samples as 0, and without a floor a mass-growing cargo cap never actually constrains
            // anything there — GravityFloorG left at its default (0) reproduces that loophole.
            var cat = new FakeCatalogue();
            SetDownThrust(cat, "Hauler", dryMassKg: 1000, downBand: HydrogenBand(20000));

            var route = new ShipPool.RouteEnvironment
            {
                OriginGravityG = 0, OriginInAtmosphere = false,
                DestGravityG = 0, DestInAtmosphere = false,
                MinDryTwr = 1.5,
                // GravityFloorG defaults to 0 — not set here.
            };

            Assert.True(double.IsPositiveInfinity(route.MaxCargoMassKg(cat, "Hauler")));
        }

        [Fact]
        public void MaxCargoMassKg_DeepSpace_WithGravityFloor_YieldsFiniteCeiling()
        {
            // Same hull/route as above, but with the gravity floor (tuning.OrbitGravityMin) wired —
            // now the cap has real teeth even on a genuinely space-only leg.
            var cat = new FakeCatalogue();
            SetDownThrust(cat, "Hauler", dryMassKg: 1000, downBand: HydrogenBand(20000));

            var route = new ShipPool.RouteEnvironment
            {
                OriginGravityG = 0, OriginInAtmosphere = false,
                DestGravityG = 0, DestInAtmosphere = false,
                MinDryTwr = 1.5,
                GravityFloorG = 0.01,
            };

            // forceN=20000 (Hydrogen, full everywhere); effectiveG=0.01 (floored);
            // maxTotalMassKg = 20000 / (1.5 * 0.01 * 9.81) = 135915.73...; minus dryMassKg=1000.
            double maxCargoMassKg = route.MaxCargoMassKg(cat, "Hauler");
            Assert.False(double.IsPositiveInfinity(maxCargoMassKg));
            Assert.Equal(134915.7, maxCargoMassKg, 0);
        }

        [Fact]
        public void MaxCargoMassKg_BindingLimitIsTheWorseOfBothEnds()
        {
            var cat = new FakeCatalogue();
            SetDownThrust(cat, "Hauler", dryMassKg: 1000, downBand: HydrogenBand(20000));

            // Origin at real (higher) surface gravity, destination floored deep-space gravity —
            // the higher-gravity origin is the binding (lower cargo ceiling) constraint.
            var route = new ShipPool.RouteEnvironment
            {
                OriginGravityG = 1.0, OriginInAtmosphere = true, OriginAirDensity = 1.0,
                DestGravityG = 0, DestInAtmosphere = false,
                MinDryTwr = 1.5,
                GravityFloorG = 0.01,
            };

            double combined = route.MaxCargoMassKg(cat, "Hauler");
            double originOnly = ThrustPhysics.MaxCargoMassKg(
                cat.ThrustBands("Hauler"), 1000, 1.0, 1.0, true, 1.5, 0.01);
            double destOnly = ThrustPhysics.MaxCargoMassKg(
                cat.ThrustBands("Hauler"), 1000, 0, 0, false, 1.5, 0.01);

            Assert.Equal(originOnly, combined, 3); // higher-gravity origin is the binding constraint
            Assert.True(combined < destOnly); // the floored-deep-space end alone would allow more
        }

        [Fact]
        public void MaxCargoMassKg_UnscannedPrefab_ReturnsInfinity()
        {
            var cat = new FakeCatalogue(); // no entry for "Unknown" at all
            var route = new ShipPool.RouteEnvironment
            {
                OriginGravityG = 0, DestGravityG = 0, MinDryTwr = 1.5, GravityFloorG = 0.01,
            };
            Assert.True(double.IsPositiveInfinity(route.MaxCargoMassKg(cat, "Unknown")));
        }

        [Fact]
        public void MaxCargoMassKg_GateDisabled_MinDryTwrZero_ReturnsInfinity()
        {
            var cat = new FakeCatalogue();
            SetDownThrust(cat, "Hauler", dryMassKg: 1000, downBand: HydrogenBand(20000));
            var route = new ShipPool.RouteEnvironment
            {
                OriginGravityG = 0, DestGravityG = 0, MinDryTwr = 0, GravityFloorG = 0.01,
            };
            Assert.True(double.IsPositiveInfinity(route.MaxCargoMassKg(cat, "Hauler")));
        }

        // ---- Return ------------------------------------------------------------------------

        [Fact]
        public void Return_CreditsTheSpecifiedPrefab()
        {
            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 1;

            ShipPool.Return(n, "Gunship", 2);

            Assert.Equal(3, n.ShipStock["Gunship"]);
        }

        [Fact]
        public void Return_NeverTouchesListedForSale()
        {
            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 1;
            n.ListedForSale["Gunship"] = 1;

            ShipPool.Return(n, "Gunship", 5); // a combat ship returning from a mission

            Assert.Equal(6, n.ShipStock["Gunship"]);
            Assert.Equal(1, n.ListedForSale["Gunship"]); // reservation unchanged
        }

        [Fact]
        public void Return_NullPrefabId_CreditsTheUnknownPrefabBucket()
        {
            SpatialNode n = MakeNode();

            ShipPool.Return(n, null, 1);

            Assert.Equal(1, n.ShipStock[""]);
        }

        // ---- Remove --------------------------------------------------------------------------

        [Fact]
        public void Remove_DecrementsTheSpecifiedPrefab()
        {
            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 5;

            ShipPool.Remove(n, "Gunship", 2);

            Assert.Equal(3, n.ShipStock["Gunship"]);
        }

        [Fact]
        public void Remove_FloorsAtZero()
        {
            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 2;

            ShipPool.Remove(n, "Gunship", 5);

            Assert.Equal(0, n.ShipStock["Gunship"]);
        }

        [Fact]
        public void Remove_NeverTouchesListedForSale()
        {
            SpatialNode n = MakeNode();
            n.ShipStock["Gunship"] = 5;
            n.ListedForSale["Gunship"] = 2;

            ShipPool.Remove(n, "Gunship", 3);

            Assert.Equal(2, n.ShipStock["Gunship"]);
            Assert.Equal(2, n.ListedForSale["Gunship"]); // reservation unchanged
        }

        [Fact]
        public void Remove_NullPrefabId_DecrementsTheUnknownPrefabBucket()
        {
            SpatialNode n = MakeNode();
            n.ShipStock[""] = 3;

            ShipPool.Remove(n, null, 1);

            Assert.Equal(2, n.ShipStock[""]);
        }

        // ---- Persistence migration regression (the Arc 2 Kind-corruption bug) --------------

        [Fact]
        public void ApplyBrainDoc_OldShapeDoc_MigratesRoleKeyedStockIntoUnifiedPool_AndFixesKindKeys()
        {
            var doc = new BrainDoc
            {
                Tag = "ACME",
                Cooldowns = new Dictionary<string, DateTime> { { "Patrol", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } },
                PendingFleets = new List<PendingFleetDoc>
                {
                    new PendingFleetDoc { Kind = "Patrol", ActorTag = "ACME", TargetTag = "ACME", ShipPrefabId = "PatrolCargoship" },
                },
                Sources = new List<SourceDoc>
                {
                    new SourceDoc
                    {
                        Id = "N1",
                        ShipStock = new Dictionary<string, int>
                        {
                            { "Trade", 2 },
                            { "Raid", 1 },
                            { "StoreSmall", 3 },
                        },
                        ShipPrefabQueue = new Dictionary<string, List<string>>
                        {
                            { "Trade", new List<string> { "Hauler", "Hauler" } },
                            { "Raid", new List<string> { "Gunship" } },
                            { "StoreSmall", new List<string> { "Fighter", "Fighter" } }, // 2 listed, but ShipStock says 3
                        },
                    },
                },
            };

            var graph = new SpatialGraph();
            FactionState st = FactionStore.ApplyBrainDoc(doc, graph);

            // Kind: "Patrol" migrates to "Guard" only because this doc is old-shape (has legacy
            // role keys in ShipStock) — this is the exact live corruption bug this gate fixes.
            Assert.Single(st.PendingFleets);
            Assert.Equal(ActionKind.Guard, st.PendingFleets[0].Kind);
            Assert.True(st.Cooldowns.ContainsKey(ActionKind.Guard));
            Assert.False(st.Cooldowns.ContainsKey(ActionKind.Patrol));

            SpatialNode n = graph.Nodes["N1"];
            Assert.Equal(2, n.ShipStock["Hauler"]);
            Assert.Equal(1, n.ShipStock["Gunship"]);
            Assert.Equal(2, n.ShipStock["Fighter"]);
            // ShipStock said 3 StoreSmall but the queue only named 2 prefabs — the excess would
            // migrate to unknown-prefab ("") stock, but that's permanently undispatchable and
            // unsellable (ShipPool's suitability checks default to zero for an unresolvable prefab
            // id), so it's dropped on load rather than left as dead inventory forever.
            Assert.False(n.ShipStock.ContainsKey(""));

            // StoreSmall-sourced ships are reserved via ListedForSale; Trade/Raid are not.
            Assert.Equal(2, n.ListedForSale["Fighter"]);
            Assert.False(n.ListedForSale.ContainsKey(""));
            Assert.False(n.ListedForSale.ContainsKey("Hauler"));
            Assert.False(n.ListedForSale.ContainsKey("Gunship"));
        }

        [Fact]
        public void ApplyBrainDoc_NewShapeDoc_PassesThroughUnchanged_RealPatrolKindSurvives()
        {
            // A current-shape doc's ShipStock is prefab-keyed (no legacy role-string keys) — this
            // must NOT trigger the old-shape migrations, or a real, current Patrol fleet/cooldown
            // gets silently corrupted into Guard (the exact bug this doc-age gate exists to avoid).
            var doc = new BrainDoc
            {
                Tag = "ACME",
                Cooldowns = new Dictionary<string, DateTime> { { "Patrol", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } },
                PendingFleets = new List<PendingFleetDoc>
                {
                    new PendingFleetDoc { Kind = "Patrol", ActorTag = "ACME", TargetTag = "ACME", ShipPrefabId = "PatrolCargoship" },
                },
                Sources = new List<SourceDoc>
                {
                    new SourceDoc
                    {
                        Id = "N1",
                        ShipStock = new Dictionary<string, int> { { "PatrolCargoship", 1 } },
                        ListedForSale = new Dictionary<string, int>(),
                    },
                },
            };

            var graph = new SpatialGraph();
            FactionState st = FactionStore.ApplyBrainDoc(doc, graph);

            Assert.Single(st.PendingFleets);
            Assert.Equal(ActionKind.Patrol, st.PendingFleets[0].Kind); // NOT corrupted to Guard
            Assert.True(st.Cooldowns.ContainsKey(ActionKind.Patrol));
            Assert.False(st.Cooldowns.ContainsKey(ActionKind.Guard));

            SpatialNode n = graph.Nodes["N1"];
            Assert.Equal(1, n.ShipStock["PatrolCargoship"]);
        }

        [Fact]
        public void ApplyBrainDoc_NewShapeDoc_DropsPersistedUnknownPrefabStock()
        {
            // Live evidence (2026-08-10): brain files already persist "" entries directly (from the
            // 2026-08-01 unification migration, before this cleanup existed) — a plain new-shape
            // reload must strip them too, not just the old-shape migration path that creates them.
            var doc = new BrainDoc
            {
                Tag = "ACME",
                Sources = new List<SourceDoc>
                {
                    new SourceDoc
                    {
                        Id = "N1",
                        ShipStock = new Dictionary<string, int> { { "Hauler", 2 }, { "", 215 } },
                        ListedForSale = new Dictionary<string, int> { { "", 5 } },
                    },
                },
            };

            var graph = new SpatialGraph();
            FactionStore.ApplyBrainDoc(doc, graph);

            SpatialNode n = graph.Nodes["N1"];
            Assert.Equal(2, n.ShipStock["Hauler"]);
            Assert.False(n.ShipStock.ContainsKey(""));
            Assert.False(n.ListedForSale.ContainsKey(""));
        }

        [Fact]
        public void LoadBrain_OldShapeSquadron_ObjectEntries_DeserializeWithoutCrashing()
        {
            // Live regression (2026-08-01): a pre-unification brain file persists Squadron as
            // [{"Role":"Raid","PrefabId":"Gunship"}, ...] (the retired SquadronMember struct).
            // PendingFleetDoc.Squadron is now List<string> — without SquadronConverter, Newtonsoft
            // throws JsonReaderException deep inside FactionRuntime.Load(), which Torch does not
            // catch, crashing the entire session load. This exercises the exact JsonConvert
            // .DeserializeObject<BrainDoc> call FactionStore.LoadBrain makes, not just in-memory
            // BrainDoc construction (which never touches the JSON boundary where this broke).
            string json = @"{
                ""Tag"": ""ACME"",
                ""PendingFleets"": [
                    {
                        ""Kind"": ""Raid"",
                        ""ActorTag"": ""ACME"",
                        ""TargetTag"": ""ENEMY"",
                        ""Squadron"": [
                            { ""Role"": ""Raid"", ""PrefabId"": ""Gunship"" },
                            { ""Role"": ""Trade"", ""PrefabId"": ""Hauler"" }
                        ]
                    }
                ]
            }";

            BrainDoc doc = JsonConvert.DeserializeObject<BrainDoc>(json);

            Assert.NotNull(doc);
            Assert.Single(doc.PendingFleets);
            Assert.Equal(new List<string> { "Gunship", "Hauler" }, doc.PendingFleets[0].Squadron);
        }

        [Fact]
        public void LoadBrain_NewShapeSquadron_StringEntries_RoundTrip()
        {
            string json = @"{
                ""Tag"": ""ACME"",
                ""PendingFleets"": [
                    { ""Kind"": ""Raid"", ""ActorTag"": ""ACME"", ""TargetTag"": ""ENEMY"",
                      ""Squadron"": [ ""Gunship"", ""Hauler"" ] }
                ]
            }";

            BrainDoc doc = JsonConvert.DeserializeObject<BrainDoc>(json);

            Assert.Equal(new List<string> { "Gunship", "Hauler" }, doc.PendingFleets[0].Squadron);
        }

        [Fact]
        public void ToBrainDoc_ApplyBrainDoc_RoundTripsUnifiedPoolAndReservation()
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "ACME";
            n.ShipStock["Gunship"] = 3;
            n.ShipStock["Fighter"] = 2;
            n.ListedForSale["Fighter"] = 2;

            var st = new FactionState { Tag = "ACME" };
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), 50);

            var graph2 = new SpatialGraph();
            FactionState st2 = FactionStore.ApplyBrainDoc(doc, graph2);

            SpatialNode n2 = graph2.Nodes["N1"];
            Assert.Equal(3, n2.ShipStock["Gunship"]);
            Assert.Equal(2, n2.ShipStock["Fighter"]);
            Assert.Equal(2, n2.ListedForSale["Fighter"]);
            Assert.False(n2.ListedForSale.ContainsKey("Gunship"));
        }

        // ---- LargestCargoLitersForRole ---------------------------------------------------------

        [Fact]
        public void LargestCargoLitersForRole_ReturnsTheBiggestHullInTheRolePool()
        {
            var cat = new FakeCatalogue();
            cat.NamesByRole["Trade"] = new[] { "Small", "Big", "Mid" };
            cat.CargoByPrefab["Small"] = 1_000;
            cat.CargoByPrefab["Big"] = 9_000;
            cat.CargoByPrefab["Mid"] = 4_000;
            cat.NamesByRole["Mine"] = new[] { "Small" };

            Assert.Equal(9_000, ShipPool.LargestCargoLitersForRole(cat, "Trade"));
            Assert.Equal(1_000, ShipPool.LargestCargoLitersForRole(cat, "Mine")); // per-role, not global
        }

        [Fact]
        public void LargestCargoLitersForRole_ReturnsZeroWhenNoCeilingIsKnown()
        {
            // Callers read 0 as "leave the floor alone" -- an unknown ceiling must not read as a
            // small one, which would clamp every demand down to nothing.
            var cat = new FakeCatalogue();
            cat.NamesByRole["Trade"] = new[] { "Unarmed" }; // in the pool, but zero cargo

            Assert.Equal(0, ShipPool.LargestCargoLitersForRole(null, "Trade"));
            Assert.Equal(0, ShipPool.LargestCargoLitersForRole(cat, "Patrol")); // role absent
            Assert.Equal(0, ShipPool.LargestCargoLitersForRole(cat, "Trade"));
        }

    }
}
