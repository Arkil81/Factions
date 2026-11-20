using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="FuelMeter"/>, <see cref="FuelLedger"/> and <see cref="LegProfileCache"/> —
    /// docs/fuel-plan.md's slice 1 in measure-only mode. The load-bearing claim is the negative
    /// one: in measure mode the meter observes the world and changes nothing in it, which is what
    /// makes it safe to price the model against live traffic.
    /// </summary>
    public class FuelMeterTests
    {
        private const string HydrogenKey = "Gas:Hydrogen";
        private const string Hull = "Freighter";

        // ---- fixtures -----------------------------------------------------------------------

        /// <summary>Large-grid large hydrogen thruster: 7.2 MN for 7.5 MW-equivalent, H2 at
        /// 0.001556 MWh/L. 6.69e-4 L per newton-second, the figure docs/fuel-plan.md tabulates.</summary>
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

        /// <summary>Litres of hydrogen per delivered newton-second, from the band above.</summary>
        private static double LitresPerNewtonSecond()
        {
            return (7.5 / 0.001556) / 7.2e6;
        }

        private sealed class HydrogenCatalogue : IPrefabCatalogue, IFuelPrefabCatalogue
        {
            private readonly Dictionary<string, double> _dryKg = new Dictionary<string, double>(StringComparer.Ordinal);
            public int ThrustBandCalls;

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
                ThrustBandCalls++;
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
                return new FuelManifest { HasFuelData = true, ReactorFuelKey = "Ingot:Uranium", ReactorFuelCapacityKg = 100 };
            }
        }

        private sealed class FakeMasses : IItemMassCatalogue
        {
            private readonly Dictionary<string, double> _kg = new Dictionary<string, double>(StringComparer.Ordinal);
            public FakeMasses Set(string key, double kgPerUnit) { _kg[key] = kgPerUnit; return this; }
            public double MassKgPerUnit(string invKey)
            {
                double kg;
                return _kg.TryGetValue(invKey, out kg) ? kg : 0;
            }
        }

        private static WorldModel DeepSpaceWorld(int fuelDrawMode, HydrogenCatalogue catalogue = null)
        {
            var world = new WorldModel();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = Tuning(fuelDrawMode);
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            world.PrefabCatalogue = catalogue ?? new HydrogenCatalogue().Add(Hull, 1.0e6);
            return world;
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

        private static SpatialNode AddNode(WorldModel world, string id, Vec3 pos, string tag = "F")
        {
            SpatialNode node = world.Graph.Add(pos, 1.0, id);
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            return node;
        }

        private static PendingFleetAction HaulFleet(WorldModel world, SpatialNode from, SpatialNode to,
                                                    string prefabId = Hull, List<string> squadron = null)
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
                ShipPrefabId = prefabId,
                Squadron = squadron,
                FleetId = "FUEL01",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            };
        }

        private static double Gas(FuelTotals totals)
        {
            double litres;
            return totals.GasLitres.TryGetValue(HydrogenKey, out litres) ? litres : 0;
        }

        // ---- (a) mode 0 is genuinely free ----------------------------------------------------

        [Fact]
        public void ModeOff_PricesNothingAndRecordsNothing()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Off);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            FuelLedgerSnapshot snapshot = world.FuelLedger.Snapshot(FuelDrawModes.Off);
            Assert.Equal(0, snapshot.World.Legs);
            // The cache is the first thing the meter touches, so zero lookups proves the mode check
            // ran before any of the work and not merely before the record.
            Assert.Equal(0, snapshot.CacheHits + snapshot.CacheMisses);
        }

        // ---- (b) the litres are the vanilla figure -------------------------------------------

        [Fact]
        public void ModeMeasure_RecordsAHaulLegAtTheVanillaHydrogenFigure()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));

            PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false);

            FuelLedgerSnapshot snapshot = world.FuelLedger.Snapshot(FuelDrawModes.Measure);
            Assert.Equal(1, snapshot.World.Legs);
            Assert.Equal(3000.0, snapshot.World.Km, 3);

            // Deep space: no hover, one accelerate and one brake at ThrusterCruiseSpeedMs.
            double expectedLitres = 1.0e6 * (2 * 100.0) * LitresPerNewtonSecond();
            Assert.Equal(expectedLitres, Gas(snapshot.World), 0);
            Assert.Equal(0.0, snapshot.World.HoverGasLitres, 6);
            Assert.Equal(expectedLitres, snapshot.World.DeltaVGasLitres, 0);

            // The FSD cruise is power, and the hull has a reactor to answer it.
            Assert.True(snapshot.World.FsdMwh > 0);
            Assert.True(snapshot.World.ReactorFuelKg > 0);

            FuelTotals haul = snapshot.ByKind[ActionKind.Haul];
            Assert.Equal(1, haul.Legs);
            Assert.Equal(expectedLitres, Gas(haul), 0);
            Assert.Equal(expectedLitres, Gas(snapshot.ByFaction["F"]), 0);
        }

        // ---- (c) a squadron is every hull ----------------------------------------------------

        [Fact]
        public void TwoHullSquadron_BurnsExactlyTwiceOneHull()
        {
            WorldModel single = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode sa = AddNode(single, "A", new Vec3(0, 0, 0));
            SpatialNode sb = AddNode(single, "B", new Vec3(3000, 0, 0));
            PendingFleetResolver.PlanFleetLeg(HaulFleet(single, sa, sb), single, single.Tuning, false);
            double oneHull = Gas(single.FuelLedger.Snapshot(FuelDrawModes.Measure).World);

            WorldModel pair = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode pa = AddNode(pair, "A", new Vec3(0, 0, 0));
            SpatialNode pb = AddNode(pair, "B", new Vec3(3000, 0, 0));
            PendingFleetResolver.PlanFleetLeg(
                HaulFleet(pair, pa, pb, Hull, new List<string> { Hull, Hull }), pair, pair.Tuning, false);
            FuelLedgerSnapshot snapshot = pair.FuelLedger.Snapshot(FuelDrawModes.Measure);

            Assert.Equal(2, snapshot.World.Hulls);
            Assert.Equal(1, snapshot.World.Legs);   // two hulls, one leg
            Assert.Equal(oneHull * 2.0, Gas(snapshot.World), 0);
        }

        // ---- (d) cargo mass ------------------------------------------------------------------

        [Fact]
        public void LoadedHull_BurnsMoreThanDry_AndAMissingMassCatalogueIsCountedNotGuessed()
        {
            WorldModel loaded = DeepSpaceWorld(FuelDrawModes.Measure);
            loaded.ItemMasses = new FakeMasses().Set("Ore:Iron", 1.0);
            SpatialNode la = AddNode(loaded, "A", new Vec3(0, 0, 0));
            SpatialNode lb = AddNode(loaded, "B", new Vec3(3000, 0, 0));
            PendingFleetAction withCargo = HaulFleet(loaded, la, lb);
            withCargo.Cargo = new Dictionary<string, long> { { "Ore:Iron", 500000 } };
            PendingFleetResolver.PlanFleetLeg(withCargo, loaded, loaded.Tuning, false);
            FuelLedgerSnapshot loadedSnapshot = loaded.FuelLedger.Snapshot(FuelDrawModes.Measure);

            WorldModel dry = DeepSpaceWorld(FuelDrawModes.Measure);   // same cargo, no mass catalogue
            SpatialNode da = AddNode(dry, "A", new Vec3(0, 0, 0));
            SpatialNode db = AddNode(dry, "B", new Vec3(3000, 0, 0));
            PendingFleetAction unweighed = HaulFleet(dry, da, db);
            unweighed.Cargo = new Dictionary<string, long> { { "Ore:Iron", 500000 } };
            PendingFleetResolver.PlanFleetLeg(unweighed, dry, dry.Tuning, false);
            FuelLedgerSnapshot drySnapshot = dry.FuelLedger.Snapshot(FuelDrawModes.Measure);

            // 1,000 t dry plus 500 t of ore is 1.5x the mass and so 1.5x the impulse.
            Assert.Equal(Gas(drySnapshot.World) * 1.5, Gas(loadedSnapshot.World), 0);
            Assert.Equal(0, loadedSnapshot.World.LegsDryMassOnly);
            Assert.Equal(1, drySnapshot.World.LegsDryMassOnly);
        }

        // ---- (e) a routed chain is one leg ---------------------------------------------------

        [Fact]
        public void RoutedClearanceChain_IsPricedOnceOverItsWholePolyline()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Measure);
            world.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Name = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50, GravityRadius = 80 },
            });
            SpatialNode a = AddNode(world, "A", new Vec3(-200, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(200, 0, 0));

            PendingFleetAction routed = PendingFleetResolver.PlanFleetLeg(
                HaulFleet(world, a, b), world, world.Tuning, false);

            Assert.Null(routed.TargetNodeId);                 // it really is flying a clearance chain
            Assert.True(routed.RemainingStops.Count >= 3);    // several chords

            FuelLedgerSnapshot snapshot = world.FuelLedger.Snapshot(FuelDrawModes.Measure);
            Assert.Equal(1, snapshot.World.Legs);
            // Priced over the detour, not the blocked straight line.
            Assert.True(snapshot.World.Km > 400.0,
                "the chain must be priced over the detour, not the blocked straight line");
        }

        [Fact]
        public void EveryChordOfARoutedChain_IsSkippedAsItIsFlown()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Measure);
            world.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Name = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50, GravityRadius = 80 },
            });
            SpatialNode a = AddNode(world, "A", new Vec3(-200, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(200, 0, 0));

            var state = new FactionState { Tag = "F" };
            PendingFleetAction routed = PendingFleetResolver.PlanFleetLeg(
                HaulFleet(world, a, b), world, world.Tuning, false);
            int chords = routed.RemainingStops.Count;
            state.PendingFleets.Add(routed);

            var log = new List<string>();
            var states = new List<FactionState> { state };
            for (int i = 0; i < chords + 4 && state.PendingFleets.Count > 0; i++)
            {
                world.UtcNow = world.UtcNow.AddHours(1);
                PendingFleetResolver.ResolveArrivals(states, world, world.Tuning, new SeededRandom(7), log);
            }

            Assert.True(chords >= 3, "the fixture must actually produce a multi-chord chain");
            Assert.Empty(state.PendingFleets);
            Assert.Equal(1, world.FuelLedger.Snapshot(FuelDrawModes.Measure).World.Legs);
        }

        // ---- (f) each later semantic leg is priced -------------------------------------------

        [Fact]
        public void EachLaterSemanticLeg_IsPricedWhenItIsReached()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));

            PendingFleetAction fleet = HaulFleet(world, a, b);
            fleet.RemainingStops = new List<FleetStop> { new FleetStop { NodeId = a.Id, Kind = FleetStopKind.Return } };
            var state = new FactionState { Tag = "F" };
            state.PendingFleets.Add(PendingFleetResolver.PlanFleetLeg(fleet, world, world.Tuning, false));

            var states = new List<FactionState> { state };
            var log = new List<string>();
            for (int i = 0; i < 4 && state.PendingFleets.Count > 0; i++)
            {
                world.UtcNow = world.UtcNow.AddHours(1);
                PendingFleetResolver.ResolveArrivals(states, world, world.Tuning, new SeededRandom(7), log);
            }

            // Out and home: two legs, two charges, and the second cost the same as the first.
            FuelLedgerSnapshot snapshot = world.FuelLedger.Snapshot(FuelDrawModes.Measure);
            Assert.Equal(2, snapshot.World.Legs);
            Assert.Equal(6000.0, snapshot.World.Km, 0);
        }

        // ---- (g) an immediate itinerary is never flown ---------------------------------------

        [Fact]
        public void ImmediateItinerary_RecordsNothing()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));

            PendingFleetAction fleet = HaulFleet(world, a, b);
            fleet.ImmediateItinerary = true;
            PendingFleetResolver.PlanFleetLeg(fleet, world, world.Tuning, true);

            Assert.Equal(0, world.FuelLedger.Snapshot(FuelDrawModes.Measure).World.Legs);
        }

        // ---- (h) the deep-space fast path ----------------------------------------------------

        [Fact]
        public void DeepSpaceFastPath_ReturnsTheSameProfileAsTheSampledWalk()
        {
            FactionTuning tuning = Tuning(FuelDrawModes.Measure);
            var planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Name = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 600, GravityRadius = 1000 },
            });
            // A dog-leg well clear of the body's gravity radius, with an interior vertex: a turn
            // under the drive costs nothing, so the two paths must still agree.
            var polyline = new List<Vec3>
            {
                new Vec3(5000, 0, 0), new Vec3(9000, 4000, 0), new Vec3(12000, 0, 0),
            };

            Assert.True(LegProfileCache.IsDeepSpace(planets, polyline));
            LegImpulseProfile fast = LegProfileCache.Build(planets, tuning, polyline);
            LegImpulseProfile sampled = LegImpulseProfile.Build(planets, tuning, polyline);

            Assert.Equal(sampled.FsdDistanceKm, fast.FsdDistanceKm, 6);
            Assert.Equal(sampled.FsdEngagements, fast.FsdEngagements);
            Assert.Equal(sampled.ThrusterStretches, fast.ThrusterStretches);
            Assert.Equal(sampled.ThrusterDistanceKm, fast.ThrusterDistanceKm, 9);
            Assert.Equal(sampled.DownImpulsePerKg, fast.DownImpulsePerKg, 9);
            Assert.Equal(sampled.AccelerateDeltaVMs, fast.AccelerateDeltaVMs, 9);
            Assert.Equal(sampled.BrakeDeltaVMs, fast.BrakeDeltaVMs, 9);
            Assert.Equal(sampled.RepresentativeG, fast.RepresentativeG, 9);
            Assert.Equal(sampled.HasGeometry, fast.HasGeometry);
        }

        [Fact]
        public void APathThatEntersAWell_IsNotTakenAsDeepSpace()
        {
            var planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Name = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 600, GravityRadius = 1000 },
            });
            // Endpoints both outside the gravity radius; the straight line between them passes
            // through the well. The cheap test has to look at the segment, not at its ends.
            var through = new List<Vec3> { new Vec3(-5000, 0, 0), new Vec3(5000, 0, 0) };
            Assert.False(LegProfileCache.IsDeepSpace(planets, through));

            LegImpulseProfile profile = LegProfileCache.Build(planets, Tuning(FuelDrawModes.Measure), through);
            Assert.True(profile.DownImpulsePerKg > 0, "a leg through a well owes lift impulse");
        }

        // ---- (i) the cache -------------------------------------------------------------------

        [Fact]
        public void Cache_HitsOnARepeatedPairAndIsBounded()
        {
            var cache = new LegProfileCache();
            FactionTuning tuning = Tuning(FuelDrawModes.Measure);
            var planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            var leg = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(3000, 0, 0) };

            bool hit;
            LegImpulseProfile first = cache.Get(planets, tuning, leg, out hit);
            Assert.False(hit);
            LegImpulseProfile second = cache.Get(planets, tuning, leg, out hit);
            Assert.True(hit);
            Assert.Same(first, second);
            Assert.Equal(1, cache.Hits);
            Assert.Equal(1, cache.Misses);

            // Quantisation: 10 m apart is the same node pair as far as impulse is concerned.
            cache.Get(planets, tuning, new List<Vec3> { new Vec3(0, 0, 0), new Vec3(3000.01, 0, 0) }, out hit);
            Assert.True(hit);

            for (int i = 0; i < LegProfileCache.Capacity + 50; i++)
                cache.Get(planets, tuning, new List<Vec3> { new Vec3(0, 0, 0), new Vec3(10 + i, 0, 0) }, out hit);
            Assert.True(cache.Count <= LegProfileCache.Capacity,
                "the table must not grow past its bound: " + cache.Count);
        }

        [Fact]
        public void Cache_IsDroppedWhenTheGravityBoundaryMoves()
        {
            var cache = new LegProfileCache();
            FactionTuning tuning = Tuning(FuelDrawModes.Measure);
            var planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            var leg = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(3000, 0, 0) };

            bool hit;
            cache.Get(planets, tuning, leg, out hit);
            tuning.ThrusterCruiseSpeedMs = 250.0;   // every cached Δv was solved at the old speed
            cache.Get(planets, tuning, leg, out hit);
            Assert.False(hit);
        }

        // ---- (i2) what the two paths cost ----------------------------------------------------

        /// <summary>The acceptance bar for slice 1 is cost, not correctness: at ~1.6 dispatches a
        /// second inside the Torch update tick, a leg that costs a millisecond is a millisecond a
        /// second. The bounds are deliberately loose — a shared build box is not a benchmark rig —
        /// but each is orders of magnitude below the figure that would make the meter unshippable,
        /// so a regression that puts a gravity walk back on the common path fails here.</summary>
        [Fact]
        public void PricingCost_TheDeepSpacePathAndACacheHitAreBothFarUnderATick()
        {
            FactionTuning tuning = Tuning(FuelDrawModes.Measure);
            var planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Name = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 600, GravityRadius = 1000 },
            });

            var cache = new LegProfileCache();
            bool hit;
            const int Legs = 2000;

            // Misses, every one a distinct deep-space pair: the fast path, which is what live
            // traffic between two deep-space nodes takes.
            var missWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Legs; i++)
                cache.Get(planets, tuning, new List<Vec3> { new Vec3(5000 + i, 0, 0), new Vec3(20000 + i, 0, 0) }, out hit);
            missWatch.Stop();
            double msPerDeepSpaceMiss = missWatch.Elapsed.TotalMilliseconds / Legs;

            // Hits on one pair.
            var pair = new List<Vec3> { new Vec3(5000, 0, 0), new Vec3(20000, 0, 0) };
            cache.Get(planets, tuning, pair, out hit);
            var hitWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Legs; i++) cache.Get(planets, tuning, pair, out hit);
            hitWatch.Stop();
            double msPerHit = hitWatch.Elapsed.TotalMilliseconds / Legs;

            Assert.True(msPerDeepSpaceMiss < 0.2,
                "a deep-space miss must not sample gravity: " + msPerDeepSpaceMiss.ToString("0.0000") + " ms");
            Assert.True(msPerHit < 0.02, "a hit is a dictionary lookup: " + msPerHit.ToString("0.0000") + " ms");
        }

        // ---- (j) the line --------------------------------------------------------------------

        [Fact]
        public void FuelDrawnLine_IsWrittenOnlyWhenTheTraceIsOn()
        {
            WorldModel world = DeepSpaceWorld(FuelDrawModes.Measure);
            SpatialNode a = AddNode(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = AddNode(world, "B", new Vec3(3000, 0, 0));
            var log = new List<string>();

            DebugTraces.ResetToDefaults();
            try
            {
                PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false, log);
                Assert.DoesNotContain(log, l => l.StartsWith("FUEL_DRAWN", StringComparison.Ordinal));

                DebugTraces.Fuel = true;
                PendingFleetResolver.PlanFleetLeg(HaulFleet(world, a, b), world, world.Tuning, false, log);
                string line = Assert.Single(log.FindAll(l => l.StartsWith("FUEL_DRAWN", StringComparison.Ordinal)));
                Assert.Contains("kind=Haul", line, StringComparison.Ordinal);
                Assert.Contains("hulls=1", line, StringComparison.Ordinal);
                Assert.Contains("mode=measure", line, StringComparison.Ordinal);
                Assert.Contains("cache=hit", line, StringComparison.Ordinal);   // the first call warmed it
            }
            finally { DebugTraces.ResetToDefaults(); }
        }

        // ---- (k) measure mode changes nothing ------------------------------------------------

        [Fact]
        public void MeasureMode_LeavesEveryWorldEffectIdenticalToModeOff()
        {
            List<string> offLog, measureLog;
            FactionState offState, measureState;
            WorldModel off = RunHaulScenario(FuelDrawModes.Off, out offLog, out offState);
            WorldModel measure = RunHaulScenario(FuelDrawModes.Measure, out measureLog, out measureState);

            // Identical log, line for line: FUEL_DRAWN is behind its trace and off here, so measure
            // mode must be invisible in the decision log as well as in the world. OPS_TICK_PERF is
            // dropped first -- both its numbers and its very presence are wall-clock-gated, so it
            // cannot take part in a determinism assertion at all.
            Assert.Equal(WithoutPerfLines(offLog), WithoutPerfLines(measureLog));
            Assert.NotEmpty(WithoutPerfLines(measureLog));
            Assert.Equal(Describe(off, offState), Describe(measure, measureState));
            Assert.Single(measureState.PendingFleets);

            // ...and the measuring did happen, so the equality above is not vacuous.
            Assert.Equal(0, off.FuelLedger.Snapshot(FuelDrawModes.Off).World.Legs);
            Assert.True(measure.FuelLedger.Snapshot(FuelDrawModes.Measure).World.Legs > 0);
        }

        /// <summary>One real OperationsManager haul dispatch: a demand at Dest, stock and a hull at
        /// Source, one tick. Returns the world so its whole observable state can be compared.</summary>
        private static WorldModel RunHaulScenario(int fuelDrawMode, out List<string> log, out FactionState state)
        {
            var world = new WorldModel();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.Tuning.FuelDrawMode = fuelDrawMode;
            world.Tuning.HaulMinLoadFraction = 0;   // the subject is the fuel model, not the load gate
            world.Tuning.FuelValueCeiling = 0;      // ...nor the fuel-against-value refusal, which off never applies
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            world.PrefabCatalogue = new HydrogenCatalogue().Add("Hauler", 1.0e6);
            world.ItemMasses = new FakeMasses().Set("Ore", 1.0);

            SpatialNode dest = AddNode(world, "Dest", new Vec3(0, 0, 0));
            SpatialNode source = AddNode(world, "Source", new Vec3(10, 0, 0));
            source.Inventory["Ore"] = 100;
            source.ShipStock["Hauler"] = 1;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Ore",
                NodeId = dest.Id,
                Quantity = 100,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            state = new FactionState { Tag = "F" };
            log = new List<string>();
            new OperationsManager(new SingleItemVolumes("Ore", 3.0), new FakeMasses().Set("Ore", 1.0))
                .Tick(new List<FactionState> { state }, world, world.Tuning, log);
            return world;
        }

        /// <summary>Drops OPS_TICK_PERF. It carries wall-clock elapsed times, and it is emitted only
        /// when the tick took longer than a millisecond — so a warm run and a cold one legitimately
        /// differ in whether it appears at all.</summary>
        private static List<string> WithoutPerfLines(List<string> log)
        {
            var kept = new List<string>(log.Count);
            foreach (string line in log)
                if (!line.StartsWith("OPS_TICK_PERF", StringComparison.Ordinal)) kept.Add(line);
            return kept;
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

        /// <summary>Every node's inventory and ship stock, flattened, so two runs can be compared as
        /// one string rather than field by field.</summary>
        private static string Describe(WorldModel world, FactionState state)
        {
            var keys = new List<string>(world.Graph.Nodes.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (string id in keys)
            {
                SpatialNode node = world.Graph.Nodes[id];
                sb.Append(id).Append(" inv:");
                var items = new List<string>(node.Inventory.Keys);
                items.Sort(StringComparer.Ordinal);
                foreach (string item in items) sb.Append(item).Append('=').Append(node.Inventory[item]).Append(',');
                sb.Append(" ships:");
                var ships = new List<string>(node.ShipStock.Keys);
                ships.Sort(StringComparer.Ordinal);
                foreach (string ship in ships) sb.Append(ship).Append('=').Append(node.ShipStock[ship]).Append(',');
                sb.Append('\n');
            }
            sb.Append("treasuryF=").Append(world.GetTreasury("F")).Append('\n');
            sb.Append("demands=").Append(world.Demand.Count).Append('\n');
            foreach (PendingFleetAction fleet in state.PendingFleets)
            {
                sb.Append("fleet ").Append(fleet.FleetId).Append(' ').Append(fleet.Kind)
                  .Append(' ').Append(fleet.OriginNodeId).Append("->").Append(fleet.TargetNodeId)
                  .Append(" eta=").Append(fleet.EstimatedArrivalUtc.ToString("O"))
                  .Append(" cargo:");
                if (fleet.Cargo != null)
                {
                    var cargoKeys = new List<string>(fleet.Cargo.Keys);
                    cargoKeys.Sort(StringComparer.Ordinal);
                    foreach (string key in cargoKeys) sb.Append(key).Append('=').Append(fleet.Cargo[key]).Append(',');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ---- (l) the report ------------------------------------------------------------------

        [Fact]
        public void Report_RanksFactionsByGasAndNamesTheMode()
        {
            var ledger = new FuelLedger(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            ledger.Record(Leg("SMALL", ActionKind.Patrol, 1000));
            ledger.Record(Leg("BIG", ActionKind.Haul, 90000));

            string report = FuelLedgerReport.Format(ledger.Snapshot(FuelDrawModes.Measure),
                                                    new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc));

            Assert.Contains("mode=measure", report, StringComparison.Ordinal);
            Assert.Contains("window 2h 0m", report, StringComparison.Ordinal);
            Assert.True(report.IndexOf("BIG", StringComparison.Ordinal)
                        < report.IndexOf("SMALL", StringComparison.Ordinal),
                        "the heaviest burner is listed first");
            Assert.True(report.Split('\n').Length <= 40, "the response has to stay readable in chat");
        }

        private static FuelLegRecord Leg(string tag, ActionKind kind, double litres)
        {
            var burn = new LegBurn { HasBurnData = true };
            burn.AddGas(HydrogenKey, litres);
            burn.DeltaVGasLitres = litres;
            return new FuelLegRecord { FactionTag = tag, Kind = kind, Burn = burn, LegKm = 100, Hulls = 1 };
        }
    }
}
