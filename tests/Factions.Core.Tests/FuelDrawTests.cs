using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="FuelDraw"/>, <see cref="FuelReserve"/> and the mode-2 half of
    /// <see cref="FuelMeter"/> — docs/fuel-plan.md's slice 1 draw. Two claims carry the rest:
    /// mode 1 is byte-for-byte what it was, and mode 2 differs from it only by taking stock, buying
    /// the shortfall and posting a reserve.
    /// </summary>
    public class FuelDrawTests
    {
        private const string HydrogenKey = "Gas:Hydrogen";
        private const string UraniumKey = "Ingot:Uranium";
        private const string Hull = "Freighter";

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
                FuelId = HydrogenKey,
                FuelEfficiency = 1f,
                FuelEnergyDensityMwhPerL = 0.001556f,
            };
        }

        private sealed class HydrogenCatalogue : IPrefabCatalogue, IFuelPrefabCatalogue
        {
            private readonly Dictionary<string, double> _dryKg = new Dictionary<string, double>(StringComparer.Ordinal);

            public HydrogenCatalogue Add(string prefabId, double dryMassKg)
            {
                _dryKg[prefabId] = dryMassKg;
                return this;
            }

            public long CargoLiters(string prefabId) { return 1000000; }
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

            public double DryMassKg(string prefabId)
            {
                double kg;
                return _dryKg.TryGetValue(prefabId, out kg) ? kg : 0;
            }

            public FuelManifest FuelManifest(string prefabId)
            {
                return new FuelManifest { HasFuelData = true, ReactorFuelKey = UraniumKey, ReactorFuelCapacityKg = 100 };
            }
        }

        /// <summary>A flat price book, so the credit fallback's arithmetic is checkable by hand.</summary>
        private sealed class FlatPrices : IItemPriceCatalogue
        {
            private readonly long _price;
            public FlatPrices(long price) { _price = price; }
            public long BasePrice(string subTypeId) { return _price; }
        }

        private static FactionTuning Tuning(int fuelDrawMode)
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
            return tuning;
        }

        private static WorldModel DeepSpaceWorld(int fuelDrawMode, long unitPrice = 3)
        {
            var world = new WorldModel();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = Tuning(fuelDrawMode);
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            world.PrefabCatalogue = new HydrogenCatalogue().Add(Hull, 1.0e6);
            world.PriceCatalogue = new FlatPrices(unitPrice);
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, Vec3 pos, string tag = "F")
        {
            SpatialNode node = world.Graph.Add(pos, 1.0, id);
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            node.StableStationId = 4242;     // founded: a reserve is only posted at one
            return node;
        }

        private static PendingFleetAction HaulFleet(WorldModel world, SpatialNode from, SpatialNode to)
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
                ConvoySize = 1,
                ShipPrefabId = Hull,
                FleetId = "FUEL1B",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            };
        }

        /// <summary>One priced deep-space leg's whole gas cost: 1,000 t through one accelerate and
        /// one brake at 100 m/s, at the vanilla 6.69e-4 L per newton-second.</summary>
        private static double OneLegLitres()
        {
            return 1.0e6 * (2 * 100.0) * ((7.5 / 0.001556) / 7.2e6);
        }

        // ---- (a) the mode gate ----------------------------------------------------------------

        /// <summary>The load-bearing negative: mode 1 still touches nothing. The same scenario at
        /// mode 2 draws, which is what stops this being vacuous.</summary>
        [Fact]
        public void ModeMeasure_TakesNoStockAndNoCredits_WhileModeDrawTakesBoth()
        {
            WorldModel measure = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode ma = AddNode(measure, "A", new Vec3(0, 0, 0));
            SpatialNode mb = AddNode(measure, "B", new Vec3(3000, 0, 0));
            ma.Inventory[HydrogenKey] = 100_000_000;
            ma.Inventory[UraniumKey] = 10_000;     // the FSD cruise is power, and power is uranium
            measure.SetTreasury("F", 1_000_000_000);
            PendingFleetResolver.PlanFleetLeg(HaulFleet(measure, ma, mb), measure, measure.Tuning, false);

            Assert.Equal(100_000_000L, ma.Inventory[HydrogenKey]);
            Assert.Equal(1_000_000_000L, measure.GetTreasury("F"));
            // Measure mode still records the RATE it would have drawn. Depot capacity is sized from
            // this number (ProductionManager's gas stage), so a burn estimate that only existed
            // under a live draw could not say how much supply to build before arming one.
            double perHour = ma.FuelBurnEma[HydrogenKey];
            double expectedPerHour = OneLegLitres() / measure.Tuning.FuelBurnEmaHours;
            Assert.InRange(perHour, expectedPerHour * 0.995, expectedPerHour * 1.005);
            Assert.True(ma.FuelBurnEma[UraniumKey] > 0, "the reactor half is metered too");
            Assert.Equal(0L, measure.FuelLedger.Snapshot(FuelDrawModes.Measure).World.DrawnGasLitres);

            WorldModel draw = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode da = AddNode(draw, "A", new Vec3(0, 0, 0));
            SpatialNode db = AddNode(draw, "B", new Vec3(3000, 0, 0));
            da.Inventory[HydrogenKey] = 100_000_000;
            da.Inventory[UraniumKey] = 10_000;
            draw.SetTreasury("F", 1_000_000_000);
            PendingFleetResolver.PlanFleetLeg(HaulFleet(draw, da, db), draw, draw.Tuning, false);

            long expected = (long)Math.Floor(OneLegLitres());
            Assert.Equal(100_000_000L - expected, da.Inventory[HydrogenKey]);
            Assert.True(expected > 0);
            // Wholly from stock, so nothing was bought.
            Assert.Equal(1_000_000_000L, draw.GetTreasury("F"));
            Assert.Equal(expected, draw.FuelLedger.Snapshot(FuelDrawModes.Draw).World.DrawnGasLitres);
            Assert.True(da.FuelBurnEma[HydrogenKey] > 0);
        }

        /// <summary>Mode 2 prices identically to mode 1 — only the world effects differ.</summary>
        [Fact]
        public void ModeDraw_PricesTheSameLegAsModeMeasure()
        {
            WorldModel measure = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode ma = AddNode(measure, "A", new Vec3(0, 0, 0));
            SpatialNode mb = AddNode(measure, "B", new Vec3(3000, 0, 0));
            PendingFleetResolver.PlanFleetLeg(HaulFleet(measure, ma, mb), measure, measure.Tuning, false);

            WorldModel draw = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode da = AddNode(draw, "A", new Vec3(0, 0, 0));
            SpatialNode db = AddNode(draw, "B", new Vec3(3000, 0, 0));
            PendingFleetResolver.PlanFleetLeg(HaulFleet(draw, da, db), draw, draw.Tuning, false);

            FuelTotals m = measure.FuelLedger.Snapshot(FuelDrawModes.Measure).World;
            FuelTotals d = draw.FuelLedger.Snapshot(FuelDrawModes.Draw).World;
            Assert.Equal(m.TotalGasLitres, d.TotalGasLitres, 6);
            Assert.Equal(m.Legs, d.Legs);
            Assert.Equal(m.Km, d.Km, 6);
        }

        // ---- (b) the shortfall arithmetic -----------------------------------------------------

        [Fact]
        public void AShortfall_IsBoughtAtPriceTimesTheImportPremium_AndOnlyTheShortfall()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw, unitPrice: 3);
            world.Tuning.FuelImportPremium = 2.0;
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));

            long needed = (long)Math.Floor(OneLegLitres());
            long inStock = needed / 4;
            a.Inventory[HydrogenKey] = inStock;
            a.Inventory[UraniumKey] = 10_000;      // reactor fuel in full: the shortfall is gas only
            world.SetTreasury("F", 1_000_000_000_000L);

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            FuelTotals totals = world.FuelLedger.Snapshot(FuelDrawModes.Draw).World;
            Assert.Equal(inStock, totals.DrawnGasLitres);
            Assert.Equal(needed - inStock, totals.ImportedGasLitres);
            Assert.Equal(0L, totals.ShortGasLitres);
            Assert.Equal(0L, totals.ImportedReactorKg);
            Assert.True(totals.DrawnReactorKg > 0, "the FSD cruise really did cost reactor fuel");
            // 3 credits x 2.0 premium = 6 the KILOLITRE, on the shortfall alone, which is in litres.
            long importCost = (needed - inStock) * 6L / GasStoreUnits.LitresPerStoreUnit;
            Assert.Equal(importCost, totals.ImportCredits);
            Assert.Equal(1_000_000_000_000L - importCost, world.GetTreasury("F"));
            Assert.Equal(0L, a.Inventory[HydrogenKey]);

            // The metric decision 1 turns the fallback off on: bought over everything that reached
            // a hull, reactor fuel included -- it is a share of fuel, not of litres.
            double reached = totals.DrawnGasLitres + totals.ImportedGasLitres + totals.DrawnReactorKg;
            Assert.Equal(totals.ImportedGasLitres / reached, totals.ImportedShare, 9);
            Assert.Equal(0.75, totals.ImportedShare, 3);
        }

        /// <summary>A node holding everything imports nothing, so the imported share falls as the
        /// chain closes rather than settling wherever it started.</summary>
        [Fact]
        public void AFullNode_ImportsNothing_SoTheImportedShareIsZero()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[HydrogenKey] = 100_000_000;
            a.Inventory[UraniumKey] = 10_000;
            world.SetTreasury("F", 1_000_000_000);

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            FuelTotals totals = world.FuelLedger.Snapshot(FuelDrawModes.Draw).World;
            Assert.True(totals.DrawnGasLitres > 0);
            Assert.Equal(0L, totals.ImportedGasLitres);
            Assert.Equal(0L, totals.ImportCredits);
            Assert.Equal(0.0, totals.ImportedShare, 9);
        }

        // ---- (c) no credits and no stock is not free fuel -------------------------------------

        [Fact]
        public void NoStockAndNoCredits_FliesShort_RatherThanOnFreeFuel()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));
            world.SetTreasury("F", 0);

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            FuelTotals totals = world.FuelLedger.Snapshot(FuelDrawModes.Draw).World;
            long needed = (long)Math.Floor(OneLegLitres());
            Assert.Equal(0L, totals.DrawnGasLitres);
            Assert.Equal(0L, totals.ImportedGasLitres);
            Assert.Equal(needed, totals.ShortGasLitres);
            // The wallet neither moved nor went negative.
            Assert.Equal(0L, world.GetTreasury("F"));
        }

        /// <summary>A faction that can pay for part of the shortfall buys exactly that part. The
        /// wallet lands at or above zero, never below — a failed debit must not hand out free fuel
        /// and must not invent credits either.</summary>
        [Fact]
        public void APartlyAffordableShortfall_BuysWhatTheTreasuryCovers_AndStopsThere()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw, unitPrice: 2);
            world.Tuning.FuelImportPremium = 1.0;           // 2 credits the kilolitre
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));

            long needed = (long)Math.Floor(OneLegLitres());
            const long CoveredLitres = 50_000;              // 50 kL at 2 credits each
            Assert.True(needed > CoveredLitres, "the leg must want more than the treasury covers, or this is vacuous");
            world.SetTreasury("F", CoveredLitres * 2L / GasStoreUnits.LitresPerStoreUnit);

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            FuelTotals totals = world.FuelLedger.Snapshot(FuelDrawModes.Draw).World;
            Assert.Equal(CoveredLitres, totals.ImportedGasLitres);
            Assert.Equal(100L, totals.ImportCredits);
            Assert.Equal(needed - CoveredLitres, totals.ShortGasLitres);
            Assert.Equal(0L, world.GetTreasury("F"));
            Assert.True(world.GetTreasury("F") >= 0, "a wallet must never go negative");
        }

        // ---- (d) a foreign node is not drawn from ---------------------------------------------

        [Fact]
        public void AForeignNode_IsNotDrawnFrom()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0), tag: "OTHER");
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[HydrogenKey] = 100_000_000;
            world.SetTreasury("F", 1_000_000_000);

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            Assert.Equal(100_000_000L, a.Inventory[HydrogenKey]);
            Assert.Equal(1_000_000_000L, world.GetTreasury("F"));
            Assert.Equal(0L, world.FuelLedger.Snapshot(FuelDrawModes.Draw).World.DrawnGasLitres);
        }

        // ---- (e) a draw is a consumption event --------------------------------------------------

        [Fact]
        public void ADraw_CreditsTheNodesConsumptionHistory()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));
            a.Inventory[HydrogenKey] = 100_000_000;

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            Dictionary<string, double> credits = world.SnapshotPendingDemandHistory(a.Id);
            double weighted;
            Assert.True(credits.TryGetValue(HydrogenKey, out weighted),
                        "a draw is consumption-fed, exactly as a recipe input is");
            Assert.Equal((double)(long)Math.Floor(OneLegLitres()), weighted, 6);
        }

        // ---- (f) the burn estimate -------------------------------------------------------------

        [Fact]
        public void BurnEma_SettlesOnTheRateAndDecaysWhenDrawsStop()
        {
            FactionTuning tuning = Tuning(FuelDrawModes.Draw);
            tuning.FuelBurnEmaHours = 4.0;
            var node = new SpatialNode { Id = "N" };
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            FuelDraw.CreditBurn(node, t0, tuning, HydrogenKey, 4000);
            Assert.Equal(1000.0, FuelDraw.BurnRateAt(node, HydrogenKey, t0, tuning), 6);

            // One time constant later with nothing drawn: down by e.
            double later = FuelDraw.BurnRateAt(node, HydrogenKey, t0.AddHours(4), tuning);
            Assert.Equal(1000.0 / Math.E, later, 6);

            // Drawing again folds the new units into the decayed estimate.
            FuelDraw.CreditBurn(node, t0.AddHours(4), tuning, HydrogenKey, 4000);
            Assert.Equal(1000.0 / Math.E + 1000.0,
                         FuelDraw.BurnRateAt(node, HydrogenKey, t0.AddHours(4), tuning), 6);
        }

        [Fact]
        public void BurnEma_IsOmittedFromTheBrainFileWhenEmpty_AndRoundTripsWhenNot()
        {
            var node = new SpatialNode { Id = "N" };
            Assert.Empty(node.FuelBurnEma);

            FactionTuning tuning = Tuning(FuelDrawModes.Draw);
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            FuelDraw.CreditBurn(node, t0, tuning, HydrogenKey, 6000);
            Assert.Equal(1000.0, node.FuelBurnEma[HydrogenKey], 6);
            Assert.Equal(t0, node.FuelBurnEmaAtUtc);
        }

        // ---- (g) the reserve rows --------------------------------------------------------------

        [Fact]
        public void EveryFoundedNode_PostsAFuelReserve_IncludingOneWithNoHullsParked()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode yard = AddNode(world, "Yard", new Vec3(0, 0, 0));
            SpatialNode outpost = AddNode(world, "Outpost", new Vec3(100, 0, 0));
            yard.ShipStock[Hull] = 3;
            // outpost holds nothing at all -- decision 6: it still asks.

            var state = new FactionState { Tag = "F" };
            var log = new List<string>();
            new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>())
                .Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(world.Demand.SnapshotFactionDemands("F"),
                d => d.Kind == "Need:" + HydrogenKey && d.NodeId == outpost.Id
                     && d.Class == DemandClass.Fuel && d.Quantity == world.Tuning.FuelReserveFloor);
            Assert.Contains(world.Demand.SnapshotFactionDemands("F"),
                d => d.Kind == "Item:" + HydrogenKey && d.NodeId == yard.Id
                     && d.Class == DemandClass.Fuel);
            // Reactor fuel has its own floor, in kilograms, not the gas floor in litres.
            Assert.Contains(world.Demand.SnapshotFactionDemands("F"),
                d => d.Kind == "Need:" + UraniumKey && d.NodeId == outpost.Id
                     && d.Class == DemandClass.Fuel && d.Quantity == world.Tuning.FuelReserveFloorReactorKg);
        }

        /// <summary>A cold node asks for the gas floor in litres and the reactor floor in
        /// kilograms; the two are not interchangeable, and the gas floor for uranium would ask every
        /// node for hundreds of tonnes.</summary>
        [Fact]
        public void AColdNodesReserveFloor_IsPerKindOfFuel()
        {
            FactionTuning tuning = Tuning(FuelDrawModes.Draw);
            tuning.FuelReserveFloor = 500000;
            tuning.FuelReserveFloorReactorKg = 10;
            var node = new SpatialNode { Id = "N", StableStationId = 1 };
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(500000L, FuelReserve.TargetStock(node, HydrogenKey, t0, tuning));
            Assert.Equal(10L, FuelReserve.TargetStock(node, UraniumKey, t0, tuning));
        }

        [Fact]
        public void ANodeAlreadyHoldingItsReserve_AsksForNothing()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            SpatialNode yard = AddNode(world, "Yard", new Vec3(0, 0, 0));
            yard.ShipStock[Hull] = 1;
            yard.Inventory[HydrogenKey] = world.Tuning.FuelReserveFloor * 10;
            yard.Inventory[UraniumKey] = world.Tuning.FuelReserveFloor * 10;

            var state = new FactionState { Tag = "F" };
            new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>())
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Demand.SnapshotFactionDemands("F"), d => d.Class == DemandClass.Fuel);
        }

        [Fact]
        public void MeasureMode_PostsNoFuelReserveAtAll()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode yard = AddNode(world, "Yard", new Vec3(0, 0, 0));
            yard.ShipStock[Hull] = 1;

            var state = new FactionState { Tag = "F" };
            new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>())
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.Demand.SnapshotFactionDemands("F"), d => d.Class == DemandClass.Fuel);
        }

        /// <summary>The reserve tracks the node's own measured burn once it has one.</summary>
        [Fact]
        public void AReserve_IsSizedFromTheNodesOwnBurn_OnceItExceedsTheFloor()
        {
            FactionTuning tuning = Tuning(FuelDrawModes.Draw);
            tuning.FuelReserveHours = 5.0;
            tuning.FuelReserveFloor = 100;
            tuning.FuelBurnEmaHours = 2.0;

            var node = new SpatialNode { Id = "N", StableStationId = 1 };
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(100L, FuelReserve.TargetStock(node, HydrogenKey, t0, tuning));

            FuelDraw.CreditBurn(node, t0, tuning, HydrogenKey, 2000);    // 1,000 units/hour
            Assert.Equal(5000L, FuelReserve.TargetStock(node, HydrogenKey, t0, tuning));
        }

        // ---- (h) ship stock boosts urgency, it does not decide existence -----------------------

        [Fact]
        public void ParkedHulls_RaiseAFuelRowsUrgency_AndLeaveEveryOtherClassAlone()
        {
            FactionTuning tuning = Tuning(FuelDrawModes.Draw);
            tuning.UrgencyStarvationExponent = 1.0;
            tuning.FuelUrgencyPerParkedHull = 0.25;
            tuning.FuelUrgencyHullCap = 4.0;
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var fuel = new DemandEntry { FactionTag = "F", Kind = "Need:" + HydrogenKey, NodeId = "N",
                                         Quantity = 1000, Class = DemandClass.Fuel };
            double dry = DemandUrgency.Score(fuel, 0, 0, now, tuning, 0);
            double withHulls = DemandUrgency.Score(fuel, 0, 0, now, tuning, 4);
            Assert.Equal(dry * 2.0, withHulls, 9);
            Assert.True(withHulls > dry, "a dry shipyard must outrank a dry outpost");

            // The cap binds, so one yard holding a hundred hulls cannot swamp the faction.
            Assert.Equal(dry * 4.0, DemandUrgency.Score(fuel, 0, 0, now, tuning, 100), 9);

            // Every other class ignores the count entirely.
            var install = new DemandEntry { FactionTag = "F", Kind = "Need:Comp:SteelPlate", NodeId = "N",
                                            Quantity = 1000, Class = DemandClass.Install };
            Assert.Equal(DemandUrgency.Score(install, 0, 0, now, tuning, 0),
                         DemandUrgency.Score(install, 0, 0, now, tuning, 50), 9);
        }

        /// <summary>Fuel ranks between a blocked recipe and a committed install, as the plan says.</summary>
        [Fact]
        public void FuelRanksUnderInstall_AndOverARecipeInput()
        {
            FactionTuning tuning = new FactionTuning();
            Assert.True(DemandUrgency.ClassWeight(DemandClass.Fuel, tuning)
                        > DemandUrgency.ClassWeight(DemandClass.RecipeInput, tuning));
            Assert.True(DemandUrgency.ClassWeight(DemandClass.Fuel, tuning)
                        < DemandUrgency.ClassWeight(DemandClass.Install, tuning));
        }

        // ---- (i) the line ----------------------------------------------------------------------

        [Fact]
        public void FuelImportedLine_IsWrittenOnlyWhenTheNodeCouldNotSupply_AndOnlyBehindTheTrace()
        {
            DebugTraces.ResetToDefaults();
            try
            {
                DebugTraces.Fuel = true;
                WorldModel full = DeepSpaceWorld(FuelDrawModes.Draw);
                SpatialNode fa = AddNode(full, "A", new Vec3(0, 0, 0));
                SpatialNode fb = AddNode(full, "B", new Vec3(3000, 0, 0));
                fa.Inventory[HydrogenKey] = 100_000_000;
                fa.Inventory[UraniumKey] = 10_000;
                var fullLog = new List<string>();
                PendingFleetResolver.PlanFleetLeg(HaulFleet(full, fa, fb), full, full.Tuning, false, fullLog);
                Assert.DoesNotContain(fullLog, l => l.StartsWith("FUEL_IMPORTED", StringComparison.Ordinal));

                WorldModel dry = DeepSpaceWorld(FuelDrawModes.Draw);
                dry.Tuning.FuelImportPremium = 1.5;
                SpatialNode da = AddNode(dry, "A", new Vec3(0, 0, 0));
                SpatialNode db = AddNode(dry, "B", new Vec3(3000, 0, 0));
                dry.SetTreasury("F", 1_000_000_000_000L);
                var dryLog = new List<string>();
                PendingFleetResolver.PlanFleetLeg(HaulFleet(dry, da, db), dry, dry.Tuning, false, dryLog);
                string line = Assert.Single(dryLog.FindAll(
                    l => l.StartsWith("FUEL_IMPORTED", StringComparison.Ordinal)));
                Assert.Contains("drawn=0", line, StringComparison.Ordinal);
                Assert.Contains("node=A", line, StringComparison.Ordinal);
                Assert.Contains(" cr=", line, StringComparison.Ordinal);

                // ...and it is behind the trace, like every other per-event line.
                DebugTraces.ResetToDefaults();
                WorldModel quiet = DeepSpaceWorld(FuelDrawModes.Draw);
                quiet.Tuning.FuelImportPremium = 1.5;
                SpatialNode qa = AddNode(quiet, "A", new Vec3(0, 0, 0));
                SpatialNode qb = AddNode(quiet, "B", new Vec3(3000, 0, 0));
                quiet.SetTreasury("F", 1_000_000_000_000L);
                var quietLog = new List<string>();
                PendingFleetResolver.PlanFleetLeg(HaulFleet(quiet, qa, qb), quiet, quiet.Tuning, false, quietLog);
                Assert.DoesNotContain(quietLog, l => l.StartsWith("FUEL_IMPORTED", StringComparison.Ordinal));
            }
            finally { DebugTraces.ResetToDefaults(); }
        }

        // ---- (i2) the fallback off: fly short, ask for the shortfall ---------------------------

        /// <summary>The shipped defaults draw with the import fallback off.</summary>
        [Fact]
        public void TheDefaults_DrawWithTheImportFallbackOff()
        {
            var tuning = new FactionTuning();
            Assert.Equal(FuelDrawModes.Draw, tuning.FuelDrawMode);
            Assert.True(tuning.FuelImportPremium <= 0, "nothing is bought at the default premium");
            Assert.True(tuning.FuelShortfallDemandMinutes > 0, "a shortfall is posted as demand");

            FactionTuning shipped = TestSupport.TuningWithOverride(null);
            Assert.Equal(FuelDrawModes.Draw, shipped.FuelDrawMode);
            Assert.True(shipped.FuelImportPremium <= 0);
            Assert.Equal(tuning.FuelShortfallDemandMinutes, shipped.FuelShortfallDemandMinutes, 9);
        }

        /// <summary>A dry node with a rich treasury buys nothing at the default premium: the hull
        /// flies short, and the node asks for what it went without — a Need: row for the shortfall
        /// and a gross Item: row for the leg's whole burn, both Fuel-class and both expiring
        /// <see cref="FactionTuning.FuelShortfallDemandMinutes"/> out.</summary>
        [Fact]
        public void WithTheFallbackOff_ADryNodeFliesShort_AndAsksForTheShortfall()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            world.Tuning.FuelImportPremium = new FactionTuning().FuelImportPremium;
            world.Tuning.FuelShortfallDemandMinutes = 45;
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));
            long inStock = 1000;
            a.Inventory[HydrogenKey] = inStock;
            world.SetTreasury("F", 1_000_000_000_000L);

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            long needed = (long)Math.Floor(OneLegLitres());
            FuelTotals totals = world.FuelLedger.Snapshot(FuelDrawModes.Draw).World;
            Assert.Equal(inStock, totals.DrawnGasLitres);
            Assert.Equal(0L, totals.ImportedGasLitres);
            Assert.Equal(needed - inStock, totals.ShortGasLitres);
            Assert.Equal(1_000_000_000_000L, world.GetTreasury("F"));

            DemandEntry need;
            Assert.True(world.Demand.TryGet("F", DemandRegister.NeedKind(HydrogenKey), a.Id, null, out need),
                        "the shortfall is asked for at the node the hull left");
            Assert.Equal(needed - inStock, need.Quantity);
            Assert.Equal(DemandClass.Fuel, need.Class);
            Assert.Equal(world.UtcNow.AddMinutes(45), need.ExpiresUtc);

            DemandEntry item;
            Assert.True(world.Demand.TryGet("F", DemandRegister.ItemKind(HydrogenKey), a.Id, null, out item));
            Assert.Equal(needed, item.Quantity);
            Assert.Equal(DemandClass.Fuel, item.Class);

            // Reactor fuel for the FSD cruise is short too, and asked for the same way.
            Assert.True(totals.ShortReactorKg > 0);
            Assert.True(world.Demand.TryGet("F", DemandRegister.NeedKind(UraniumKey), a.Id, null, out need));
            Assert.Equal(totals.ShortReactorKg, need.Quantity);

            // Nothing is posted at the destination.
            Assert.False(world.Demand.TryGet("F", DemandRegister.NeedKind(HydrogenKey), b.Id, null, out need));
        }

        /// <summary>The negative for the test above: a node that supplies the whole leg asks for
        /// nothing, and neither does a shortfall with the demand window set to 0.</summary>
        [Fact]
        public void ASuppliedLeg_OrAZeroWindow_PostsNoShortfallRow()
        {
            WorldModel full = DeepSpaceWorld(FuelDrawModes.Draw);
            full.Tuning.FuelImportPremium = 0;
            SpatialNode fa = AddNode(full, "A", new Vec3(0, 0, 0));
            SpatialNode fb = AddNode(full, "B", new Vec3(3000, 0, 0));
            fa.Inventory[HydrogenKey] = 100_000_000;
            fa.Inventory[UraniumKey] = 10_000;
            PendingFleetResolver.PlanFleetLeg(HaulFleet(full, fa, fb), full, full.Tuning, false);
            Assert.True(full.FuelLedger.Snapshot(FuelDrawModes.Draw).World.DrawnGasLitres > 0);
            Assert.Empty(full.Demand.SnapshotFactionDemands("F"));

            WorldModel off = DeepSpaceWorld(FuelDrawModes.Draw);
            off.Tuning.FuelImportPremium = 0;
            off.Tuning.FuelShortfallDemandMinutes = 0;
            SpatialNode oa = AddNode(off, "A", new Vec3(0, 0, 0));
            SpatialNode ob = AddNode(off, "B", new Vec3(3000, 0, 0));
            PendingFleetResolver.PlanFleetLeg(HaulFleet(off, oa, ob), off, off.Tuning, false);
            Assert.True(off.FuelLedger.Snapshot(FuelDrawModes.Draw).World.ShortGasLitres > 0);
            Assert.Empty(off.Demand.SnapshotFactionDemands("F"));
        }

        /// <summary>A shortfall row never lowers a larger row already standing under the same key —
        /// the node's own reserve, say — in quantity or in expiry.</summary>
        [Fact]
        public void AShortfallRow_NeverLowersALargerRowAlreadyThere()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            world.Tuning.FuelImportPremium = 0;
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));
            DateTime later = world.UtcNow.AddDays(1);
            world.Demand.Post(DemandEntry.ForNeed("F", HydrogenKey, a.Id, later, 999_999_999_999L, DemandClass.Fuel));

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            DemandEntry need;
            Assert.True(world.Demand.TryGet("F", DemandRegister.NeedKind(HydrogenKey), a.Id, null, out need));
            Assert.Equal(999_999_999_999L, need.Quantity);
            Assert.Equal(later, need.ExpiresUtc);
        }

        /// <summary>A dry node's burn estimate counts what its departures needed, not what it handed
        /// out. Counting only the handed-out units would read a dry node as idle, and the reserve and
        /// the depot sizing that read the estimate would both fall toward zero where fuel is
        /// missing.</summary>
        [Fact]
        public void ADryNodesBurnEstimate_CountsWhatTheLegNeeded_NotWhatItHandedOut()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            world.Tuning.FuelImportPremium = 0;
            world.Tuning.FuelBurnEmaHours = 6.0;
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            long needed = (long)Math.Floor(OneLegLitres());
            Assert.Equal(0L, world.FuelLedger.Snapshot(FuelDrawModes.Draw).World.DrawnGasLitres);
            Assert.InRange(FuelDraw.BurnRateAt(a, HydrogenKey, world.UtcNow, world.Tuning),
                           needed / 6.0, (needed + 1) / 6.0);
            Assert.True(FuelReserve.TargetStock(a, HydrogenKey, world.UtcNow, world.Tuning)
                        >= (long)Math.Floor(needed / 6.0 * world.Tuning.FuelReserveHours));

            // ...and a draw handed nothing out, so the consumption history is untouched.
            Dictionary<string, double> history = world.SnapshotPendingDemandHistory(a.Id);
            double weighted;
            Assert.False(history != null && history.TryGetValue(HydrogenKey, out weighted) && weighted > 0,
                         "consumption is what was drawn, not what was needed");
        }

        /// <summary>Stock moves in whole units and a leg's burn does not. Ten legs of 0.25 kg draw
        /// 2 kg and carry the half: rounding each leg up would draw 10 kg, forty times what an ion
        /// cruise of a few hundredths of a kilogram burns.</summary>
        [Fact]
        public void FractionalBurns_AreCarriedBetweenDepartures_NotRoundedUpPerLeg()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Draw);
            world.Tuning.FuelImportPremium = 0;
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            a.Inventory[UraniumKey] = 1000;
            var burn = new LegBurn { HasBurnData = true, ReactorFuelKg = 0.25, ReactorFuelKey = UraniumKey };

            long drawn = 0;
            for (int i = 0; i < 10; i++)
                drawn += FuelDraw.TryFuel(a, burn, "F", world, world.Tuning).DrawnReactorKg;

            Assert.Equal(2L, drawn);
            Assert.Equal(998L, a.Inventory[UraniumKey]);
            Assert.Equal(0.5, a.FuelDrawCarry[UraniumKey], 9);
            // The rate still sees every leg's burn, not only the legs that drew a whole unit.
            Assert.Equal(10 * 0.25 / world.Tuning.FuelBurnEmaHours,
                         FuelDraw.BurnRateAt(a, UraniumKey, world.UtcNow, world.Tuning), 9);
        }

        [Fact]
        public void FuelShortLine_NamesTheShortfall_AndTheDemandedKeys()
        {
            DebugTraces.ResetToDefaults();
            try
            {
                DebugTraces.Fuel = true;
                WorldModel dry = DeepSpaceWorld(FuelDrawModes.Draw);
                dry.Tuning.FuelImportPremium = 0;
                SpatialNode a = AddNode(dry, "A", new Vec3(0, 0, 0));
                SpatialNode b = AddNode(dry, "B", new Vec3(3000, 0, 0));
                dry.SetTreasury("F", 1_000_000_000_000L);
                var log = new List<string>();
                PendingFleetResolver.PlanFleetLeg(HaulFleet(dry, a, b), dry, dry.Tuning, false, log);

                Assert.DoesNotContain(log, l => l.StartsWith("FUEL_IMPORTED", StringComparison.Ordinal));
                string line = Assert.Single(log.FindAll(l => l.StartsWith("FUEL_SHORT", StringComparison.Ordinal)));
                Assert.Contains("node=A", line, StringComparison.Ordinal);
                Assert.Contains("drawn=0", line, StringComparison.Ordinal);
                Assert.Contains("bought=0", line, StringComparison.Ordinal);
                Assert.Contains("short=" + (long)Math.Floor(OneLegLitres()), line, StringComparison.Ordinal);
                Assert.Contains("demanded=2", line, StringComparison.Ordinal);
            }
            finally { DebugTraces.ResetToDefaults(); }
        }

        [Fact]
        public void Report_StatesTheShortShare_WorldAndPerFaction()
        {
            var ledger = new FuelLedger(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var burn = new LegBurn { HasBurnData = true };
            burn.AddGas(HydrogenKey, 1000);
            burn.DeltaVGasLitres = 1000;
            var draw = new FuelDrawResult { DrawnGasLitres = 400, ShortGasLitres = 600 };
            draw.Keys.Add(new FuelKeyDraw { Key = HydrogenKey, IsGas = true });
            ledger.Record(new FuelLegRecord { FactionTag = "F", Kind = ActionKind.Haul, Burn = burn,
                                              LegKm = 100, Hulls = 1, Draw = draw });

            FuelLedgerSnapshot snapshot = ledger.Snapshot(FuelDrawModes.Draw);
            Assert.Equal(0.6, snapshot.World.ShortShare, 9);
            string report = FuelLedgerReport.Format(snapshot, new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
            Assert.Contains("short_share=60%", report, StringComparison.Ordinal);
            Assert.Contains("rshort_share=0%", report, StringComparison.Ordinal);
            Assert.Contains("imported_share=0%", report, StringComparison.Ordinal);
            // The per-faction row carries it too, which is the ramp-up read per faction.
            string factionRow = Array.Find(report.Split('\n'), l => l.TrimStart().StartsWith("F ", StringComparison.Ordinal));
            Assert.NotNull(factionRow);
            Assert.Contains("short=60%", factionRow, StringComparison.Ordinal);
        }

        // ---- (j) the report --------------------------------------------------------------------

        [Fact]
        public void Report_StatesDrawnAgainstImported_AndTheImportedShare()
        {
            var ledger = new FuelLedger(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var burn = new LegBurn { HasBurnData = true };
            burn.AddGas(HydrogenKey, 1000);
            burn.DeltaVGasLitres = 1000;
            var draw = new FuelDrawResult { DrawnGasLitres = 750, BoughtGasLitres = 250, Credits = 1500 };
            draw.Keys.Add(new FuelKeyDraw { Key = HydrogenKey, IsGas = true });
            ledger.Record(new FuelLegRecord { FactionTag = "F", Kind = ActionKind.Haul, Burn = burn,
                                              LegKm = 100, Hulls = 1, Draw = draw });

            string report = FuelLedgerReport.Format(ledger.Snapshot(FuelDrawModes.Draw),
                                                    new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
            Assert.Contains("mode=draw", report, StringComparison.Ordinal);
            Assert.Contains("imported_share=25%", report, StringComparison.Ordinal);
            Assert.Contains("credits=", report, StringComparison.Ordinal);

            // A measure-mode window has nothing drawn, so the line is absent rather than all zeros.
            string measure = FuelLedgerReport.Format(new FuelLedger(new DateTime(2026, 1, 1, 0, 0, 0,
                DateTimeKind.Utc)).Snapshot(FuelDrawModes.Measure),
                new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
            Assert.DoesNotContain("imported_share", measure, StringComparison.Ordinal);
        }

        // ---- (k) a staged dispatch draws once, at commit -----------------------------------------

        /// <summary>The Haul and Mine dispatches run inside a staged cycle. The draw must land once,
        /// against the stock the cycle publishes — not when the leg was priced, which a discarded
        /// cycle would have to unwind.</summary>
        [Fact]
        public void AStagedHaulDispatch_DrawsExactlyOnce()
        {
            var world = new WorldModel();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.Tuning.FuelDrawMode = FuelDrawModes.Draw;
            world.Tuning.HaulMinLoadFraction = 0;   // the subject is the draw, not the load gate
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            world.PrefabCatalogue = new HydrogenCatalogue().Add("Hauler", 1.0e6);
            world.PriceCatalogue = new FlatPrices(3);

            SpatialNode dest = AddNode(world, "Dest", new Vec3(0, 0, 0));
            SpatialNode source = AddNode(world, "Source", new Vec3(10, 0, 0));
            source.Inventory["Ore"] = 100;
            source.Inventory[HydrogenKey] = 50_000_000;
            source.Inventory[UraniumKey] = 10_000;
            source.ShipStock["Hauler"] = 1;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore", NodeId = dest.Id,
                Quantity = 100, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var state = new FactionState { Tag = "F" };
            new OperationsManager(new SingleItemVolumes("Ore", 3.0), new NoMasses())
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            FuelTotals totals = world.FuelLedger.Snapshot(FuelDrawModes.Draw).World;
            Assert.True(totals.Legs > 0, "the scenario must actually dispatch");
            Assert.True(totals.DrawnGasLitres > 0, "a staged dispatch must draw at commit");
            // The stock really moved by exactly what the ledger says it did.
            Assert.Equal(50_000_000L - totals.DrawnGasLitres, source.Inventory[HydrogenKey]);
        }

        private sealed class SingleItemVolumes : IItemVolumeCatalogue
        {
            private readonly string _key;
            private readonly double _litres;
            public SingleItemVolumes(string key, double litres) { _key = key; _litres = litres; }
            public double VolumeLitersPerUnit(string invKey)
            {
                return string.Equals(invKey, _key, StringComparison.Ordinal) ? _litres : 0;
            }
        }

        private sealed class NoMasses : IItemMassCatalogue
        {
            public double MassKgPerUnit(string invKey) { return 0; }
        }
    }
}
