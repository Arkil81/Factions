using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Broader Slice 3 coverage beyond OperationsCycleTests.cs's committed RED contract: a real
    /// (non-bootstrap) Haul dispatch equivalence, and guard invalidation on ship stock — the
    /// mutable surface the RED contract's own scenario (zero ships in the network) never exercises.
    /// </summary>
    public sealed class OperationsCycleEquivalenceTests
    {
        private sealed class FakeHaulCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 1_000_000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                => new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            // 0 = unscanned = "unknown, don't gate" (see IPrefabCatalogue.DryMassKg's own doc comment) —
            // keeps every dry-TWR/mass-trim gate a no-op so the test isolates dispatch/staging behavior.
            public double DryMassKg(string prefabId) { return 0; }

            /// <summary>Only "Miner"-prefixed prefab ids are drill-equipped -- mirrors
            /// OperationsManagerTests.FakePrefabCatalogue so the Mine equivalence scenario gets a
            /// real drill-suitable ship without making the unrelated Haul equivalence tests' "Hauler"
            /// prefab wrongly Mine-suitable too.</summary>
            public double DrillYieldScore(string prefabId)
            {
                return !string.IsNullOrEmpty(prefabId) && prefabId.StartsWith("Miner", StringComparison.Ordinal) ? 1000 : 0;
            }
        }

        /// <summary>Mine's automatic dispatch path converts a shortfall's unit quantity to a target
        /// liter volume via this catalogue -- without one, the conversion yields nothing and Mine
        /// never dispatches at all, regardless of ship/source/demand.</summary>
        private sealed class FakeOreVolumeCatalogue : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string invKey)
                => invKey == "Ore:Iron" ? 1.0 : 0;
        }

        [Fact]
        public void ResumedSweep_RealHaulDispatch_MatchesSynchronousTick()
        {
            var catalogue = new FakeHaulCatalogue();

            WorldModel expectedWorld = BuildWorldWithShip();
            expectedWorld.PrefabCatalogue = catalogue;
            var expectedState = new FactionState { Tag = "F" };
            var expectedLog = new List<string>();
            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, expectedLog);

            WorldModel actualWorld = BuildWorldWithShip();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager(itemVolumes: null, itemMasses: null);
            BeginSweep(manager, states);

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
            }

            // A real Haul convoy dispatched, not the bootstrap instant-transfer fallback.
            Assert.Single(expectedState.PendingFleets);
            Assert.Equal(ActionKind.Haul, expectedState.PendingFleets[0].Kind);
            Assert.Equal(expectedState.PendingFleets.Count, actualState.PendingFleets.Count);
            Assert.Equal(expectedState.PendingFleets[0].Kind, actualState.PendingFleets[0].Kind);
            Assert.Equal(expectedState.PendingFleets[0].OriginNodeId, actualState.PendingFleets[0].OriginNodeId);
            Assert.Equal(expectedState.PendingFleets[0].TargetNodeId, actualState.PendingFleets[0].TargetNodeId);
            Assert.Equal(expectedState.PendingFleets[0].Cargo["Ore"], actualState.PendingFleets[0].Cargo["Ore"]);

            Assert.Equal(expectedWorld.GetNodeInventory("Source", "Ore"), actualWorld.GetNodeInventory("Source", "Ore"));
            Assert.Equal(expectedWorld.GetNodeInventory("Dest", "Ore"), actualWorld.GetNodeInventory("Dest", "Ore"));
            // The one ship at Source was consumed by the dispatch in both paths.
            Assert.Equal(0, actualWorld.Graph.Nodes["Source"].ShipStock["Hauler"]);
        }

        [Fact]
        public void ResumedSweep_MineDispatch_MatchesSynchronousTick()
        {
            var catalogue = new FakeHaulCatalogue();

            WorldModel expectedWorld = BuildWorldWithOreFieldDemand();
            expectedWorld.PrefabCatalogue = catalogue;
            var expectedState = new FactionState { Tag = "F" };
            var expectedLog = new List<string>();
            var volumes = new FakeOreVolumeCatalogue();
            new OperationsManager(itemVolumes: volumes, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, expectedLog);

            WorldModel actualWorld = BuildWorldWithOreFieldDemand();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager(itemVolumes: volumes, itemMasses: null);
            BeginSweep(manager, states);

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
            }

            Assert.Single(expectedState.PendingFleets);
            Assert.Equal(ActionKind.Mine, expectedState.PendingFleets[0].Kind);
            Assert.Equal(expectedState.PendingFleets.Count, actualState.PendingFleets.Count);
            Assert.Equal(expectedState.PendingFleets[0].Kind, actualState.PendingFleets[0].Kind);
            Assert.Equal(expectedState.PendingFleets[0].OriginNodeId, actualState.PendingFleets[0].OriginNodeId);
            Assert.Equal(expectedState.PendingFleets[0].TargetNodeId, actualState.PendingFleets[0].TargetNodeId);
            // Dwell mint/load happens at resolution, not dispatch -- compare the dwell shape itself
            // instead of Cargo (not yet populated at this point in either path).
            Assert.Equal(expectedState.PendingFleets[0].CurrentStopKind, actualState.PendingFleets[0].CurrentStopKind);
            Assert.Equal(expectedState.PendingFleets[0].MineTargetLiters, actualState.PendingFleets[0].MineTargetLiters);
            Assert.Equal(expectedState.PendingFleets[0].EstimatedArrivalUtc, actualState.PendingFleets[0].EstimatedArrivalUtc);
            Assert.Equal(expectedState.PendingFleets[0].MineWeights["Ore:Iron"], actualState.PendingFleets[0].MineWeights["Ore:Iron"]);

            Assert.Equal(expectedWorld.GetNodeInventory("OreField1", "Ore:Iron"), actualWorld.GetNodeInventory("OreField1", "Ore:Iron"));
            Assert.Equal(expectedWorld.Graph.Nodes["OreField1"].LastMinedUtc, actualWorld.Graph.Nodes["OreField1"].LastMinedUtc);
            Assert.Equal(0, actualWorld.Graph.Nodes["OreField1"].ShipStock["Miner"]);
            // Both paths flew it for the same extraction order.
            Assert.Contains(expectedLog, l => l.Contains("MINE_DISPATCHED") && l.EndsWith(" order=Dest:Ore:Iron"));
            Assert.Contains(actualLog, l => l.Contains("MINE_DISPATCHED") && l.EndsWith(" order=Dest:Ore:Iron"));
        }

        /// <summary>The bootstrap Mine mint (FAC-204) is the one Operations effect that writes a
        /// node's MineCooldownUntilUtc, a field the staged cycle previously only ever read. This is
        /// what proves the staged overlay carries the write and the commit publishes it -- inventory
        /// AND cooldown AND log, identical on both paths.</summary>
        [Fact]
        public void ResumedSweep_MineBootstrapMint_MatchesSynchronousTick()
        {
            var catalogue = new FakeHaulCatalogue();
            var volumes = new FakeOreVolumeCatalogue();

            WorldModel expectedWorld = BuildWorldWithOreFieldDemandAndNoDrill();
            expectedWorld.PrefabCatalogue = catalogue;
            var expectedState = new FactionState { Tag = "F" };
            var expectedLog = new List<string>();
            new OperationsManager(itemVolumes: volumes, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, expectedLog);

            WorldModel actualWorld = BuildWorldWithOreFieldDemandAndNoDrill();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            var actualLog = new List<string>();
            RunSweepToCompletion(new OperationsManager(itemVolumes: volumes, itemMasses: null),
                new List<FactionState> { actualState }, actualWorld, actualLog);

            // The synchronous path really did bootstrap -- otherwise every equality below would be
            // an agreement that nothing happened.
            Assert.Contains(expectedLog, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.True(expectedWorld.GetNodeInventory("Dest", "Ore:Iron") > 0);
            Assert.Empty(expectedState.PendingFleets);

            Assert.Contains(actualLog, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(expectedWorld.GetNodeInventory("Dest", "Ore:Iron"),
                         actualWorld.GetNodeInventory("Dest", "Ore:Iron"));
            Assert.Equal(expectedWorld.GetNodeInventory("OreField1", "Ore:Iron"),
                         actualWorld.GetNodeInventory("OreField1", "Ore:Iron"));
            Assert.Equal(expectedWorld.Graph.Nodes["OreField1"].MineCooldownUntilUtc,
                         actualWorld.Graph.Nodes["OreField1"].MineCooldownUntilUtc);
            Assert.Equal(expectedWorld.Graph.Nodes["OreField1"].LastMinedUtc,
                         actualWorld.Graph.Nodes["OreField1"].LastMinedUtc);
            Assert.Equal(expectedState.PendingFleets.Count, actualState.PendingFleets.Count);
            // A cooldown that stayed at its default on both paths would satisfy the equality above
            // while proving the staged write never landed.
            Assert.True(actualWorld.Graph.Nodes["OreField1"].MineCooldownUntilUtc > actualWorld.UtcNow);
        }

        [Fact]
        public void GuardInvalidation_OnShipStockChange_DiscardsAndRestarts()
        {
            var catalogue = new FakeHaulCatalogue();

            WorldModel actualWorld = BuildWorldWithShip();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager(itemVolumes: null, itemMasses: null);
            BeginSweep(manager, states);

            Assert.False(Step(manager, states, actualWorld, actualLog)); // select/capture faction
            Assert.True(IsFactionCycleInProgress(manager));
            Assert.False(Step(manager, states, actualWorld, actualLog)); // advance staged work

            // Invalidate the captured ship-stock read set — a second Hauler appears at Source
            // between frames (e.g. Shipyard finished a build the same tick).
            actualWorld.Graph.Nodes["Source"].ShipStock["Hauler"] = 2;

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 2000, "invalidated Operations cycle did not restart and complete");
            }

            Assert.True(manager.LastTelemetry.Restarts >= 1);
            // The restarted cycle re-captured a fresh guard against 2 Haulers, consumed exactly 1
            // for the single dispatched vector, leaving 1 behind — proof the guard actually saw the
            // post-invalidation stock, not a stale pre-invalidation snapshot.
            Assert.Equal(1, actualWorld.Graph.Nodes["Source"].ShipStock["Hauler"]);
            Assert.Single(actualState.PendingFleets);
        }

        [Fact]
        public void ResumedSweep_AutoMineOrder_MatchesSynchronousTick()
        {
            // Deliberately no demand posted anywhere in BuildWorldWithAutoMineOrder -- the standing
            // order must fire independent of the reactive demand-driven path, and this proves the
            // DispatchingAutoMine phase is actually wired into the staged TryAdvance path the live
            // host calls, not just the synchronous Tick() the rest of this file already covers.
            var catalogue = new FakeHaulCatalogue();

            WorldModel expectedWorld = BuildWorldWithAutoMineOrder();
            expectedWorld.PrefabCatalogue = catalogue;
            var expectedState = new FactionState { Tag = "F" };
            StandingOrderFixtures.AddMineOrder(expectedState, "Hub", 60, expectedWorld.UtcNow);
            var expectedLog = new List<string>();
            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, expectedLog);

            WorldModel actualWorld = BuildWorldWithAutoMineOrder();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            StandingOrderFixtures.AddMineOrder(actualState, "Hub", 60, actualWorld.UtcNow);
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager(itemVolumes: null, itemMasses: null);
            BeginSweep(manager, states);

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
            }

            var expectedMines = expectedState.PendingFleets.Where(f => f.Kind == ActionKind.Mine).ToList();
            var actualMines = actualState.PendingFleets.Where(f => f.Kind == ActionKind.Mine).ToList();
            Assert.NotEmpty(expectedMines); // sanity: the scenario actually produces a dispatch to compare
            Assert.Equal(expectedMines.Count, actualMines.Count);
            Assert.Equal(expectedMines.Select(f => f.OriginNodeId).OrderBy(x => x),
                         actualMines.Select(f => f.OriginNodeId).OrderBy(x => x));
        }

        [Fact]
        public void GuardInvalidation_OnMineAutoOrderRemoved_DiscardsAndRestarts()
        {
            var catalogue = new FakeHaulCatalogue();

            WorldModel actualWorld = BuildWorldWithAutoMineOrder();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            StandingOrderFixtures.AddMineOrder(actualState, "Hub", 60, actualWorld.UtcNow);
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager(itemVolumes: null, itemMasses: null);
            BeginSweep(manager, states);

            Assert.False(Step(manager, states, actualWorld, actualLog)); // select/capture faction
            Assert.True(IsFactionCycleInProgress(manager));

            // Player cancels the standing order mid-cycle -- the guard must notice and discard the
            // stale in-flight cycle rather than committing a dispatch against an order the player
            // just disabled.
            actualState.StandingOrders.Clear();

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 2000, "invalidated Operations cycle did not restart and complete");
            }

            Assert.True(manager.LastTelemetry.Restarts >= 1);
            Assert.Empty(actualState.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
        }

        /// <summary>Multi-stop consolidation is decided from the WHOLE pass's matched vectors, and
        /// the two paths reach that pool differently: the synchronous tick matches every item then
        /// dispatches, while the staged cycle spreads the same two phases over many resumable
        /// TryAdvance calls with the pool and its consumed flags living on the cycle across them.
        /// A vector folded on one path and dispatched separately on the other would be a silent
        /// divergence in convoy count, so this compares the whole itinerary, not just the count.</summary>
        [Fact]
        public void ResumedSweep_ConsolidatedHaul_MatchesSynchronousTick()
        {
            var catalogue = new FakeHaulCatalogue();
            var volumes = new FakeConsolidationVolumeCatalogue();

            WorldModel expectedWorld = BuildWorldWithTwoNearbySources();
            expectedWorld.PrefabCatalogue = catalogue;
            var expectedState = new FactionState { Tag = "F" };
            new OperationsManager(itemVolumes: volumes, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, new List<string>());

            WorldModel actualWorld = BuildWorldWithTwoNearbySources();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var actualLog = new List<string>();
            var manager = new OperationsManager(itemVolumes: volumes, itemMasses: null);
            BeginSweep(manager, states);

            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, actualLog);
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
            }

            // One convoy, not two: SourceB's vector rode SourceA's hull as a Pickup stop.
            PendingFleetAction expected = Assert.Single(expectedState.PendingFleets);
            PendingFleetAction actual = Assert.Single(actualState.PendingFleets);
            Assert.Equal("SourceB", expected.TargetNodeId);
            Assert.Equal(FleetStopKind.Pickup, expected.CurrentStopKind);

            Assert.Equal(expected.OriginNodeId, actual.OriginNodeId);
            Assert.Equal(expected.TargetNodeId, actual.TargetNodeId);
            Assert.Equal(expected.CurrentStopKind, actual.CurrentStopKind);
            Assert.Equal(expected.PickupItemKey, actual.PickupItemKey);
            Assert.Equal(expected.PickupQuantity, actual.PickupQuantity);
            Assert.Equal(expected.Cargo["Ore"], actual.Cargo["Ore"]);
            Assert.Equal(expected.RemainingStops.Select(stop => stop.NodeId + ":" + stop.Kind).ToArray(),
                         actual.RemainingStops.Select(stop => stop.NodeId + ":" + stop.Kind).ToArray());
            Assert.Equal(expectedWorld.GetNodeInventory("SourceA", "Ore"), actualWorld.GetNodeInventory("SourceA", "Ore"));
            Assert.Equal(expectedWorld.GetNodeInventory("SourceB", "Ore"), actualWorld.GetNodeInventory("SourceB", "Ore"));
        }

        /// <summary>Per-prefab holds, for the release scenario: a hull too big for one job beside one
        /// the other job fills.</summary>
        private sealed class SizedHaulCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return prefabId == "Big" ? 900_000 : 5_000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                => new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private sealed class OreAndIngotVolumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string invKey) => invKey == "Ore" || invKey == "Ingot" ? 1.0 : 0;
        }

        /// <summary>A light store top-up (Ingot, urgency 1.5) is tried first and refused on S1's big
        /// hull; a catalogue want (Ore, urgency 1.0) for the same destination follows on S2's hull.</summary>
        private static WorldModel BuildWorldWithARefusedThenAFoldingVector()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.Tuning.HaulMinLoadFraction = 0.10;
            world.Tuning.HaulSmallHullSearchNodes = 0;   // no right-sized fetch: the release is the only way to fold
            SpatialNode dest = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Dest");
            dest.OwnerFactionTag = "F";
            SpatialNode s1 = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "S1");
            s1.OwnerFactionTag = "F";
            s1.Inventory["Ingot"] = 10;
            s1.ShipStock["Big"] = 1;
            SpatialNode s2 = world.Graph.Add(new Vec3(20, 0, 0), 1.0, "S2");
            s2.OwnerFactionTag = "F";
            s2.Inventory["Ore"] = 1000;
            s2.ShipStock["Mid"] = 1;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot", NodeId = "Dest", Quantity = 10,
                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.StoreStock,
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore", NodeId = "Dest", Quantity = 1000,
                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Catalog,
            });
            return world;
        }

        /// <summary>The staged cycle is the path the live host runs, and it carries its own copy of the
        /// dispatch loop: a refused vector must be released there too, and fold into the later convoy
        /// exactly as the synchronous tick folds it.</summary>
        [Fact]
        public void ResumedSweep_ARefusedVectorFoldsIntoALaterConvoy_AsTheSynchronousTickDoes()
        {
            var catalogue = new SizedHaulCatalogue();
            var volumes = new OreAndIngotVolumes();

            WorldModel expectedWorld = BuildWorldWithARefusedThenAFoldingVector();
            expectedWorld.PrefabCatalogue = catalogue;
            var expectedState = new FactionState { Tag = "F" };
            new OperationsManager(itemVolumes: volumes, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, new List<string>());

            WorldModel actualWorld = BuildWorldWithARefusedThenAFoldingVector();
            actualWorld.PrefabCatalogue = catalogue;
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var manager = new OperationsManager(itemVolumes: volumes, itemMasses: null);
            BeginSweep(manager, states);
            int steps = 0;
            bool complete = false;
            while (!complete)
            {
                complete = Step(manager, states, actualWorld, new List<string>());
                Assert.True(++steps < 1000, "Operations sweep did not complete");
            }

            PendingFleetAction expected = Assert.Single(expectedState.PendingFleets);
            PendingFleetAction actual = Assert.Single(actualState.PendingFleets);
            Assert.Equal("S2", actual.OriginNodeId);
            Assert.Equal("Mid", actual.ShipPrefabId);
            Assert.Equal(FleetStopKind.Pickup, actual.CurrentStopKind);
            Assert.Equal("Ingot", actual.PickupItemKey);
            Assert.Equal(expected.OriginNodeId, actual.OriginNodeId);
            Assert.Equal(expected.PickupItemKey, actual.PickupItemKey);
            Assert.Equal(expected.PickupQuantity, actual.PickupQuantity);
            Assert.Equal(1, actualWorld.Graph.Nodes["S1"].ShipStock["Big"]);
        }

        /// <summary>Consolidation budgets the hold in litres, so it is inert without volume data —
        /// FakeOreVolumeCatalogue only knows "Ore:Iron" and would silently switch the feature off.</summary>
        private sealed class FakeConsolidationVolumeCatalogue : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string invKey) => invKey == "Ore" ? 1.0 : 0;
        }

        private static WorldModel BuildWorldWithTwoNearbySources()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.Tuning.HaulMinLoadFraction = 0;   // the subject is staged-vs-synchronous, not the load gate
            SpatialNode dest = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Dest");
            dest.OwnerFactionTag = "F";
            // Only SourceA holds a hull; SourceB is well inside HaulConsolidationRadiusKm of it.
            SpatialNode sourceA = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "SourceA");
            sourceA.OwnerFactionTag = "F";
            sourceA.Inventory["Ore"] = 60;
            sourceA.ShipStock["Hauler"] = 1;
            SpatialNode sourceB = world.Graph.Add(new Vec3(120, 0, 0), 1.0, "SourceB");
            sourceB.OwnerFactionTag = "F";
            sourceB.Inventory["Ore"] = 40;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore", NodeId = "Dest", Quantity = 100,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            return world;
        }

        [Fact]
        public void ResumedSweep_DoesNotTeleport_WhenTheOnlyHullIsAwayOnAnErrand()
        {
            // FAC-199 on the path the live host actually runs. OperationsManagerTests covers the
            // synchronous Tick(); the staged cycle carries its own copy of the gate
            // (OperationsManager.Cycle.cs's PreparingFaction phase), so a fix applied to only one of
            // the two would leave the live server teleporting while the suite went green.
            var catalogue = new FakeHaulCatalogue();
            WorldModel world = BuildWorldWithNoParkedHull();
            world.PrefabCatalogue = catalogue;

            var state = new FactionState { Tag = "F" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind         = ActionKind.Haul,
                ActorTag     = "F",
                ShipPrefabId = "Hauler",
                ConvoySize   = 1,
                OriginNodeId = "Source",
                TargetNodeId = "Elsewhere", // unrelated leg; Cargo left null so it claims no (node, item) pair
            });

            var log = new List<string>();
            RunSweepToCompletion(new OperationsManager(itemVolumes: null, itemMasses: null),
                new List<FactionState> { state }, world, log);

            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.Equal(100, world.GetNodeInventory("Source", "Ore"));
            Assert.Equal(0, world.GetNodeInventory("Dest", "Ore"));
        }

        [Fact]
        public void ResumedSweep_StillTeleports_ForAFactionThatOwnsNoHullAtAll()
        {
            // The discriminator for the test above: the same fixture minus the in-flight hull must
            // reach the bootstrap on this same staged path. Without this, "no ITEM_XFER_BOOTSTRAP"
            // above would be equally consistent with the staged cycle never getting there.
            var catalogue = new FakeHaulCatalogue();
            WorldModel world = BuildWorldWithNoParkedHull();
            world.PrefabCatalogue = catalogue;

            var state = new FactionState { Tag = "F" };
            var log = new List<string>();
            RunSweepToCompletion(new OperationsManager(itemVolumes: null, itemMasses: null),
                new List<FactionState> { state }, world, log);

            Assert.Contains(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.Equal(40, world.GetNodeInventory("Dest", "Ore"));
        }

        /// <summary>Same shape as <see cref="BuildWorldWithShip"/> with the parked hull removed —
        /// which is exactly the world <see cref="ShipPool.Consume"/> leaves behind between a
        /// dispatch and its return.</summary>
        private static WorldModel BuildWorldWithNoParkedHull()
        {
            WorldModel world = BuildWorldWithShip();
            world.Graph.Nodes["Source"].ShipStock.Remove("Hauler");
            return world;
        }

        private static void RunSweepToCompletion(OperationsManager manager, IList<FactionState> states,
            WorldModel world, List<string> log)
        {
            BeginSweep(manager, states);
            int steps = 0;
            while (!Step(manager, states, world, log))
            {
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
            }
        }

        private static WorldModel BuildWorldWithAutoMineOrder()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            SpatialNode hub = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Hub");
            hub.OwnerFactionTag = "F";
            SpatialNode oreField = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "OreField1");
            oreField.OwnerFactionTag = "F";
            oreField.HoldStrength = 1.0;
            oreField.Richness = 1.0;   // ore under it AND measured: the mint and the drill read this
            oreField.Deposit = new NodeDeposit();
            oreField.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            oreField.ShipStock["Miner"] = 1;
            return world;
        }

        private static WorldModel BuildWorldWithShip()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            SpatialNode source = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Source");
            source.OwnerFactionTag = "F";
            source.Inventory["Ore"] = 100;
            source.ShipStock["Hauler"] = 1;
            SpatialNode dest = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "Dest");
            dest.OwnerFactionTag = "F";
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore", NodeId = "Dest", Quantity = 40,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            return world;
        }

        private static WorldModel BuildWorldWithOreFieldDemand()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            SpatialNode oreField = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "OreField1");
            oreField.OwnerFactionTag = "F";
            oreField.HoldStrength = 1.0;
            oreField.Richness = 1.0;   // ore under it AND measured: the mint and the drill read this
            oreField.Deposit = new NodeDeposit();
            oreField.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            oreField.ShipStock["Miner"] = 1;
            SpatialNode dest = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "Dest");
            dest.OwnerFactionTag = "F";
            dest.HoldStrength = 1.0;
            // Dest draws iron far faster than the deposit's passive mint, so the extraction planner
            // opens an order there -- the only automatic Mine trigger since FAC-395 slice 2b.
            dest.DemandHistory["Ore:Iron"] = 1e9;
            return world;
        }

        /// <summary>Same shape as <see cref="BuildWorldWithOreFieldDemand"/> with the parked drill
        /// removed, so the faction owns no Mine-suitable hull anywhere -- the FAC-204 bootstrap
        /// case.</summary>
        private static WorldModel BuildWorldWithOreFieldDemandAndNoDrill()
        {
            WorldModel world = BuildWorldWithOreFieldDemand();
            world.Graph.Nodes["OreField1"].ShipStock.Remove("Miner");
            return world;
        }

        private static void BeginSweep(OperationsManager manager, IList<FactionState> states)
        {
            Invoke(manager, "BeginSweepForTesting", states);
        }

        private static bool Step(OperationsManager manager, IList<FactionState> states,
            WorldModel world, List<string> log)
        {
            return (bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, log);
        }

        private static bool IsFactionCycleInProgress(OperationsManager manager)
        {
            return (bool)Invoke(manager, "IsFactionCycleInProgressForTesting");
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }
    }
}
