using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The Build dialog's hull pick-list for one yard. It is a convenience, never an authorisation —
    /// <see cref="ShipyardManager.QueueBuild"/> re-checks everything — so what these pin is that the
    /// list a player is shown is not misleading: no station designs, no hull that fits no queueable
    /// role, the roles each hull does fit, the BOM units it costs, and whether it could actually
    /// leave the yard it would be built at.
    /// </summary>
    public class BuildOptionsCatalogueTests
    {
        // ---- fakes -----------------------------------------------------------------------------

        /// <summary>Gravity everywhere, with an atmosphere — the shape
        /// <c>ShipyardManagerTests</c>'s own route tests use, so an ion hull's ~20% effectiveness
        /// curve is exercised rather than stubbed.</summary>
        private sealed class ConstantGravityCatalogue : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravityCatalogue(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets
            {
                get
                {
                    return new[] { new PlanetRecord { Center = new Vec3(0, 0, 0), SurfaceRadius = 1,
                        GravityRadius = 1_000_000, AtmosphereRadius = 1_000_000, HasAtmosphere = true } };
                }
            }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>One recipe per registered prefab, one input row per component. A prefab that was
        /// never registered has no recipe at all, which is the free-build case.</summary>
        private sealed class Recipes : IRecipeRegistry
        {
            private readonly Dictionary<string, Dictionary<string, long>> _bomByPrefab
                = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

            public Recipes Add(string prefabId, params KeyValuePair<string, long>[] components)
            {
                var bom = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, long> c in components) bom[c.Key] = c.Value;
                _bomByPrefab[prefabId] = bom;
                return this;
            }

            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                Dictionary<string, long> bom;
                if (outputTypeId != "Prefab" || outputSubTypeId == null
                    || !_bomByPrefab.TryGetValue(outputSubTypeId, out bom)) yield break;
                var inputs = new List<RecipeItem>();
                foreach (KeyValuePair<string, long> kv in bom)
                    inputs.Add(new RecipeItem
                    {
                        TypeId = "MyObjectBuilder_Component", SubTypeId = kv.Key, Amount = kv.Value,
                    });
                yield return new ConversionRecipe
                {
                    BlueprintId = "Prefab/" + outputSubTypeId,
                    Inputs = inputs.ToArray(),
                    Outputs = new[] { new RecipeItem { TypeId = "Prefab", SubTypeId = outputSubTypeId, Amount = 1.0 } },
                };
            }

            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count { get { return _bomByPrefab.Count; } }
        }

        /// <summary>A prefab catalogue that also answers <see cref="IStationPrefabCatalogue"/>, so
        /// the station-design filter has something to filter on — an absent implementation means "no
        /// station designs" and would make that test vacuous.</summary>
        private sealed class Catalogue : IPrefabCatalogue, IStationPrefabCatalogue
        {
            public readonly Dictionary<string, long> Cargo = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> Dps = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> Drill = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Size = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> DryMass = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly HashSet<string> Stations = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> Thrust
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>(StringComparer.Ordinal);

            /// <summary>A cargo hull that can lift itself anywhere: hydrogen thrust, flat curve.</summary>
            public Catalogue Hauler(string prefabId, long cargoLiters)
            {
                Cargo[prefabId] = cargoLiters;
                Size[prefabId] = "Large";
                DryMass[prefabId] = 1000;
                Thrust[prefabId] = Down("Hydrogen", 500_000f, 0f, 0f, 1f, 1f, false);
                return this;
            }

            /// <summary>An ion hull: full effectiveness in deep space, 20% at a surface — the
            /// vanilla curve, and the one that cannot leave a 1g yard.</summary>
            public Catalogue IonHauler(string prefabId, long cargoLiters)
            {
                Cargo[prefabId] = cargoLiters;
                Size[prefabId] = "Large";
                DryMass[prefabId] = 1000;
                Thrust[prefabId] = Down("Ion", 5000f, 0f, 1f, 1f, 0.2f, false);
                return this;
            }

            public Catalogue Warship(string prefabId, double dps)
            {
                Dps[prefabId] = dps;
                Size[prefabId] = "Small";
                DryMass[prefabId] = 1000;
                Thrust[prefabId] = Down("Atmospheric", 500_000f, 0f, 1f, 0f, 1f, true);
                return this;
            }

            /// <summary>A design the catalogue calls a station whose GRID SIZE does not say so.
            /// That divergence is real: <c>SePrefabCatalogue</c> sets <c>Kind = "Station"</c> from
            /// how the design was registered and <c>GridSize</c> from
            /// <c>PrefabCatalogueScanner.DetectGridSize</c>, which only answers "Station" when a
            /// grid is actually static — so a station design built on a non-static grid reads
            /// "Large" here.
            ///
            /// <para>It is also the only case where the station filter does any work.
            /// <c>ShipPool</c>'s three suitability checks already refuse <c>GridSize ==
            /// "Station"</c>, so a design that agrees with itself is kept out whether the filter
            /// runs or not — a fake spelling it "Station" makes this test pass with the filter
            /// deleted.</para></summary>
            public Catalogue Station(string prefabId)
            {
                // Cargo and drills both, so the only thing that can keep it out is the station
                // filter rather than a failed suitability check.
                Cargo[prefabId] = 100_000;
                Drill[prefabId] = 5.0;
                Size[prefabId] = "Large";
                Stations.Add(prefabId);
                return this;
            }

            private static Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> Down(
                string type, float forceN, float minInf, float maxInf, float effMin, float effMax, bool needsAtmo)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    { ThrustDirection.Down, new List<ThrustBand> { new ThrustBand
                        {
                            ThrusterType = type, ForceMagnitudeN = forceN,
                            MinPlanetaryInfluence = minInf, MaxPlanetaryInfluence = maxInf,
                            EffectivenessAtMinInfluence = effMin, EffectivenessAtMaxInfluence = effMax,
                            NeedsAtmosphereForInfluence = needsAtmo,
                        } } },
                };
            }

            public long CargoLiters(string prefabId)
            {
                long v; return Cargo.TryGetValue(prefabId, out v) ? v : 0;
            }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId)
            {
                return new Dictionary<string, long>();
            }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                double dps;
                if (!Dps.TryGetValue(prefabId, out dps) || dps <= 0) return new WeaponBand[0];
                return new[] { new WeaponBand { Dps = (float)dps, RangeM = 800 } };
            }
            public string GridSize(string prefabId)
            {
                string v; return Size.TryGetValue(prefabId, out v) ? v : "Small";
            }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                return new string[0];
            }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> v;
                return Thrust.TryGetValue(prefabId, out v)
                    ? v : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId)
            {
                double v; return DryMass.TryGetValue(prefabId, out v) ? v : 0;
            }
            public double DrillYieldScore(string prefabId)
            {
                double v; return Drill.TryGetValue(prefabId, out v) ? v : 0;
            }

            public bool IsStationPrefab(string prefabId) { return Stations.Contains(prefabId); }
            public string Site(string prefabId) { return Stations.Contains(prefabId) ? "Space" : null; }
        }

        // ---- scaffolding -----------------------------------------------------------------------

        private static WorldModel MakeWorld(Catalogue catalogue, Recipes recipes, double gravity)
        {
            WorldModel world = TestSupport.World(seed: 20260913);
            world.UtcNow = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = catalogue;
            world.Recipes = recipes;
            world.Planets = new ConstantGravityCatalogue(gravity);
            return world;
        }

        private static SpatialNode AddYard(WorldModel world)
        {
            SpatialNode yard = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            yard.OwnerFactionTag = "EXP";
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return yard;
        }

        private static List<KeyValuePair<string, string>> Candidates(params string[] rolePrefabPairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < rolePrefabPairs.Length; i += 2)
                list.Add(new KeyValuePair<string, string>(rolePrefabPairs[i], rolePrefabPairs[i + 1]));
            return list;
        }

        private static BuildPrefabOption Row(List<BuildPrefabOption> rows, string prefabId)
        {
            foreach (BuildPrefabOption row in rows)
                if (row.PrefabId == prefabId) return row;
            return null;
        }

        // ---- the filters -------------------------------------------------------------------------

        [Fact]
        public void StationDesigns_AreNeverOffered()
        {
            var catalogue = new Catalogue().Hauler("Hauler", 42_000).Station("OrbitalOutpost");
            // Both carry a recipe, so the recipe filter cannot be what keeps the station out.
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Hauler").Add("OrbitalOutpost"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard,
                Candidates("Trade", "Hauler", "Trade", "OrbitalOutpost"));

            // The station design is cargo-suitable and drill-suitable, and its grid size reads
            // "Large" rather than "Station" — see Catalogue.Station. So the station filter is the
            // only thing that can keep it out of this list. Characteristic failure: a row for
            // OrbitalOutpost, which is a build that would put a station design on a hull's queue
            // slot and hand the player a design they cannot fly.
            Assert.NotNull(Row(rows, "Hauler"));
            Assert.Null(Row(rows, "OrbitalOutpost"));
            // And the paired reading that says the filter is what did it: with the catalogue no
            // longer calling it a station, the same row IS offered.
            catalogue.Stations.Clear();
            List<BuildPrefabOption> unfiltered = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard,
                Candidates("Trade", "Hauler", "Trade", "OrbitalOutpost"));
            Assert.NotNull(Row(unfiltered, "OrbitalOutpost"));
        }

        [Fact]
        public void AHullThatFitsNoQueueableRole_IsNotOffered_AndTheRolesItDoesFitAreCarried()
        {
            // Warship has Dps and no cargo: combat-suitable, not cargo-suitable. Offering it under
            // Trade would be offering a refusal, and the dialog would narrow its hull combo to a
            // hull the server will not build for that role.
            var catalogue = new Catalogue().Hauler("Hauler", 42_000).Warship("Warship", 500);
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Hauler").Add("Warship"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard,
                Candidates("Trade", "Hauler", "Trade", "Warship",
                           "Raid", "Warship", "Patrol", "Warship", "Raid", "Hauler"));

            BuildPrefabOption hauler = Row(rows, "Hauler");
            BuildPrefabOption warship = Row(rows, "Warship");
            Assert.NotNull(hauler);
            Assert.NotNull(warship);
            // Offered for Trade and nothing else, even though a Raid candidacy was handed in.
            Assert.Equal(new[] { "Trade" }, hauler.Roles.ToArray());
            // Both combat roles, in PlayerQueueRoles order, and no Trade.
            Assert.Equal(new[] { "Raid", "Patrol" }, warship.Roles.ToArray());
        }

        [Fact]
        public void ARoleThatIsNotPlayerQueueable_IsDropped()
        {
            // "Guard" is a real brain role and not one of PlayerQueueRoles; QueueBuild refuses it
            // with UnknownRole, so a dialog offering it would be offering a refusal.
            var catalogue = new Catalogue().Hauler("Hauler", 42_000);
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Hauler"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Guard", "Hauler"));

            Assert.Empty(rows);
        }

        // ---- the numbers -------------------------------------------------------------------------

        [Fact]
        public void BomUnits_AreSummedFromTheRecipeRegistry()
        {
            var catalogue = new Catalogue().Hauler("Hauler", 42_000);
            Recipes recipes = new Recipes().Add("Hauler",
                new KeyValuePair<string, long>("Comp:SteelPlate", 1000),
                new KeyValuePair<string, long>("Comp:Construction", 240));
            WorldModel world = MakeWorld(catalogue, recipes, gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Hauler"));

            Assert.Equal(1240, Row(rows, "Hauler").BomUnits);
        }

        [Fact]
        public void AHullWithNoRecipe_IsNotOffered()
        {
            // ShipyardManager.QueueBuild answers UnknownPrefab for a hull the registry holds no
            // bill for, so a row for it would be an offer of a refusal -- the same reason the
            // standing-order pick-list filters these out.
            var catalogue = new Catalogue().Hauler("Hauler", 42_000);
            WorldModel world = MakeWorld(catalogue, new Recipes(), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Hauler"));

            Assert.Null(Row(rows, "Hauler"));
        }

        [Fact]
        public void TheStatsAreTheCataloguesOwn()
        {
            var catalogue = new Catalogue().Hauler("Hauler", 42_000).Warship("Warship", 500);
            catalogue.Drill["Hauler"] = 2.5;
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Hauler").Add("Warship"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Hauler", "Raid", "Warship"));

            BuildPrefabOption hauler = Row(rows, "Hauler");
            Assert.Equal(42_000, hauler.CargoLiters);
            Assert.Equal(0.0, hauler.Dps);
            Assert.Equal("Large", hauler.GridSize);
            Assert.Equal(2.5, hauler.DrillYieldScore);
            Assert.Equal("hydro", hauler.Propulsion);

            BuildPrefabOption warship = Row(rows, "Warship");
            Assert.Equal(500.0, warship.Dps);
            Assert.Equal("Small", warship.GridSize);
            Assert.Equal("atmo", warship.Propulsion);
        }

        [Fact]
        public void Propulsion_NamesEveryThrustKindTheHullMounts_InAFixedOrder()
        {
            // The complaint the doctrine was written for is "the managers build ion hulls". A
            // player cannot see that from a prefab id, and the dictionary ThrustBands returns has
            // no iteration-order contract — so the order is pinned here rather than observed.
            var catalogue = new Catalogue().Hauler("Mixed", 1000);
            catalogue.Thrust["Mixed"] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Down, new List<ThrustBand> {
                    new ThrustBand { ThrusterType = "Hydrogen", ForceMagnitudeN = 500_000f,
                        MaxPlanetaryInfluence = 0f, EffectivenessAtMinInfluence = 1f,
                        EffectivenessAtMaxInfluence = 1f },
                    new ThrustBand { ThrusterType = "Atmospheric", ForceMagnitudeN = 1000f,
                        MaxPlanetaryInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = true } } },
                { ThrustDirection.Backward, new List<ThrustBand> {
                    new ThrustBand { ThrusterType = "Ion", ForceMagnitudeN = 1000f } } },
            };
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Mixed"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Mixed"));

            Assert.Equal("atmo+ion+hydro", Row(rows, "Mixed").Propulsion);
        }

        [Fact]
        public void AHullWithNoThrusters_ReadsNone()
        {
            var catalogue = new Catalogue().Hauler("Drifter", 1000);
            catalogue.Thrust.Remove("Drifter");
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Drifter"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Drifter"));

            Assert.Equal("none", Row(rows, "Drifter").Propulsion);
        }

        // ---- the yard-local route gate -----------------------------------------------------------

        [Fact]
        public void AHullThatCannotLeaveThisYard_IsOfferedButSaysSo()
        {
            // Both hulls are cargo-suitable and both are offered: the one-shot build sits outside
            // the doctrine and the player may still choose a hull that stays put. What must not
            // happen is the dialog saying nothing about it.
            var catalogue = new Catalogue().Hauler("Hydro", 42_000).IonHauler("Ion", 42_000);
            WorldModel world = MakeWorld(catalogue,
                new Recipes().Add("Hydro").Add("Ion"), gravity: 1.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Hydro", "Trade", "Ion"));

            Assert.True(Row(rows, "Hydro").CanFlyFromHere);
            Assert.False(Row(rows, "Ion").CanFlyFromHere);
        }

        [Fact]
        public void TheSameIonHull_FliesFromAZeroGravityYard()
        {
            // The paired reading that makes the test above mean something: the ion hull is not
            // simply always false. Same catalogue, same hull, a yard with no gravity.
            var catalogue = new Catalogue().IonHauler("Ion", 42_000);
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Ion"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Ion"));

            Assert.True(Row(rows, "Ion").CanFlyFromHere);
        }

        // ---- shape ---------------------------------------------------------------------------

        [Fact]
        public void OneRowPerHull_OrderedById_HoweverManyRolesItFits()
        {
            var catalogue = new Catalogue().Warship("Zebra", 500).Warship("Alpha", 300);
            WorldModel world = MakeWorld(catalogue, new Recipes().Add("Zebra").Add("Alpha"), gravity: 0.0);
            SpatialNode yard = AddYard(world);

            List<BuildPrefabOption> rows = BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard,
                Candidates("Raid", "Zebra", "Patrol", "Zebra", "Raid", "Alpha", "Patrol", "Alpha"));

            // Two hulls, four candidacies: the stats are a property of the hull and repeating them
            // per role would send the same numbers twice. Ordered so two openings of the same
            // window offer the same combo.
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alpha", rows[0].PrefabId);
            Assert.Equal("Zebra", rows[1].PrefabId);
        }

        [Fact]
        public void NoCatalogue_OffersNothingRatherThanThrowing()
        {
            WorldModel world = MakeWorld(new Catalogue(), new Recipes(), gravity: 0.0);
            SpatialNode yard = AddYard(world);
            world.PrefabCatalogue = null;

            Assert.Empty(BuildOptionsCatalogue.Assemble(
                world, world.Tuning, yard, Candidates("Trade", "Hauler")));
        }
    }
}
