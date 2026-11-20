using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class OperationsManagerTests
    {
        // ---- helpers --------------------------------------------------------------------

        /// <summary>Ship stock is unified and tag-based (see ShipPool) — Haul dispatch requires a
        /// prefab the catalogue actually classifies as cargo-suitable (CargoLiters > 0), not just a
        /// nonzero count. Capacity is set high enough that none of these tests hit the volume cap —
        /// they exercise the matching/dispatch logic, not cargo-loading math (that's covered where
        /// FleetConvoyTests exercises FixedVolumeCatalogue-driven capping for Trade).</summary>
        private sealed class FakePrefabCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>();
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> ThrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();

            /// <summary>Full-effectiveness-everywhere thrust (mirrors vanilla Hydrogen) so a test can
            /// exercise the mass-trim ceiling without also tripping the (unrelated) dry-TWR route gate.</summary>
            public void SetHydrogenDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Hydrogen", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
                        EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = false,
                    }},
                };
            }

            /// <summary>Per-prefab cargo override; unset prefabs keep the old flat 1,000,000 so every
            /// existing test is unaffected. Mirrors DryMassKgByPrefab's shape.</summary>
            public readonly Dictionary<string, long> CargoLitersByPrefab = new Dictionary<string, long>();
            public long CargoLiters(string prefabId)
            {
                long v;
                return prefabId != null && CargoLitersByPrefab.TryGetValue(prefabId, out v) ? v : 1_000_000;
            }
            /// <summary>Per-prefab gas tanks; unset prefabs have none, as before.</summary>
            public readonly Dictionary<string, Dictionary<string, long>> GasTanksByPrefab
                = new Dictionary<string, Dictionary<string, long>>();
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId)
            {
                Dictionary<string, long> tanks;
                return prefabId != null && GasTanksByPrefab.TryGetValue(prefabId, out tanks)
                    ? tanks : new Dictionary<string, long>();
            }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            /// <summary>Mirrors <see cref="DrillYieldScore"/>'s naming convention on the other axis:
            /// a prefab named "Gun*" is armed, everything else is not. Lets a test build a hull that
            /// is combat-capable, drill-capable, both or neither without a catalogue fixture.</summary>
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return !string.IsNullOrEmpty(prefabId) && prefabId.IndexOf("Gun", StringComparison.Ordinal) >= 0
                    ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } }
                    : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            /// <summary>Per-role buildable pool; unset roles keep the old empty list, so every
            /// existing test sees no ceiling and its cargo floor stays unclamped. Mirrors
            /// CargoLitersByPrefab's shape.</summary>
            public readonly Dictionary<string, string[]> NamesByRole = new Dictionary<string, string[]>(StringComparer.Ordinal);
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                string[] names;
                return role != null && NamesByRole.TryGetValue(role, out names) ? names : new string[0];
            }

            /// <summary>Only "Miner"-prefixed prefab ids are drill-equipped -- mirrors
            /// StockMineShip's default prefabId, so Mine tests get a real drill-suitable ship while
            /// a plain Haul/Trade "Hauler" (cargo-only, no drill) correctly does NOT qualify for Mine.</summary>
            public double DrillYieldScore(string prefabId)
            {
                return !string.IsNullOrEmpty(prefabId) && prefabId.StartsWith("Miner", StringComparison.Ordinal) ? 1000 : 0;
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
        }

        private sealed class FakeMassCatalogue : IItemMassCatalogue
        {
            private readonly Dictionary<string, double> _massByKey = new Dictionary<string, double>();
            public FakeMassCatalogue Set(string invKey, double kgPerUnit) { _massByKey[invKey] = kgPerUnit; return this; }
            public double MassKgPerUnit(string invKey)
            {
                double v;
                return _massByKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        private sealed class FakeVolumeCatalogue : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _litersByKey = new Dictionary<string, double>();
            public FakeVolumeCatalogue Set(string invKey, double litersPerUnit) { _litersByKey[invKey] = litersPerUnit; return this; }
            public double VolumeLitersPerUnit(string invKey)
            {
                double v;
                return _litersByKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        private sealed class ConstantGravityCatalogue : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravityCatalogue(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new FakePrefabCatalogue();
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, string tag = "F")
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            return n;
        }

        private static FactionState MakeState(string tag = "F")
            => new FactionState { Tag = tag };

        /// <summary>Like <see cref="AddNode"/> but positioned -- catchment membership is a real
        /// distance check against FactionTuning.MineSearchRadiusKm, so a test that needs two hubs
        /// out of each other's reach cannot use the origin-only helper.</summary>
        private static SpatialNode AddNodeAt(WorldModel world, string id, Vec3 pos, string tag = "F")
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            return n;
        }

        private static void PostItemDemand(WorldModel world, string tag, string subTypeId, string nodeId, long quantity,
            DemandClass demandClass = DemandClass.Unclassified)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag = tag,
                Kind       = "Item:" + subTypeId,
                NodeId     = nodeId,
                Quantity   = quantity,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
                Class      = demandClass,
            });
        }

        /// <summary>Stocks <paramref name="count"/> cargo-suitable ships at <paramref name="node"/> —
        /// one Haul dispatch consumes exactly one per call, so a source serving N destinations in a
        /// single tick needs N ships stocked.</summary>
        private static void StockHaulShips(SpatialNode node, int count, string prefabId = "Hauler")
        {
            node.ShipStock[prefabId] = count;
        }

        /// <summary>Gives <paramref name="prefabId"/> a hydrogen tank the size of the default hold, so
        /// a test hauling hydrogen as its subject has a hull that boards it (gas travels only in
        /// tanks) with the same capacity the hold offers a solid.</summary>
        private static void GiveHydrogenTank(WorldModel world, string prefabId = "Hauler", long litres = 1_000_000)
        {
            ((FakePrefabCatalogue)world.PrefabCatalogue).GasTanksByPrefab[prefabId]
                = new Dictionary<string, long> { { "Gas:Hydrogen", litres } };
        }

        // A Mine dispatch books its target volume into units per item via IItemVolumeCatalogue, and
        // the extraction planner reads an ore's litres per unit from it. Wired in here (not just the
        // dedicated Mine tests) since it's inert for every non-Ore item key the rest of this file's
        // Tick(...) callers use.
        private static readonly IItemVolumeCatalogue MineTestVolumes =
            new FakeVolumeCatalogue().Set("Ore:Iron", 1.0).Set("Ore:Stone", 1.0).Set("Ore:Gold", 1.0);

        private static List<string> Tick(WorldModel world, FactionState st)
        {
            return Tick(world, st, TestSupport.Tuning());
        }

        /// <summary>Same tick with an explicit tuning, for the cases whose subject IS a tuning value.
        /// <paramref name="world"/>'s own Tuning is repointed too, so the reads that go through the
        /// world (node richness, yield tables) cannot disagree with the one being passed in.</summary>
        private static List<string> Tick(WorldModel world, FactionState st, FactionTuning tuning,
            double perfLogThresholdMs = 1.0)
        {
            world.Tuning = tuning;
            var log = new List<string>();
            new OperationsManager(itemVolumes: MineTestVolumes) { PerfLogThresholdMs = perfLogThresholdMs }
                .Tick(new List<FactionState> { st }, world, tuning, log);
            return log;
        }

        /// <summary>Every tick is cheap: no cost reaches it, so only a forced line can appear.</summary>
        private const double NeverSlowMs = double.MaxValue;

        private static PendingFleetAction HaulTo(FactionState st, string nodeId)
            => st.PendingFleets.Single(f => f.Kind == ActionKind.Haul && f.TargetNodeId == nodeId);

        private static IEnumerable<PendingFleetAction> HaulsTo(FactionState st, string nodeId)
            => st.PendingFleets.Where(f => f.Kind == ActionKind.Haul && f.TargetNodeId == nodeId);

        // ---- matching + dispatch tests -----------------------------------------------------

        /// <summary>FAC-278: hold decay hands a node to UNKN while its Item demand is still standing.
        /// Live 2026-09-02 that dispatched a haul to N260 three minutes after N260 decayed, and the
        /// grace sweep deleted node and hull together.
        ///
        /// <para>The discriminating measurement is the OTHER destination. A stale demand sorts ahead
        /// of a bigger live one (smallest-shortfall-first), so matching it spends this tick's surplus
        /// snapshot on ground the faction cannot deliver to and the live destination is served
        /// short — every cycle, for as long as the stale row lives.</para></summary>
        [Fact]
        public void AStaleDemandOnALostNode_TakesNoSurplusFromALiveDestination()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 50;
            StockHaulShips(source, 2);

            AddNode(world, "Lost");
            AddNode(world, "Live");
            PostItemDemand(world, "F", "Explosives", "Lost", 10);   // posted while it was still ours
            PostItemDemand(world, "F", "Explosives", "Live", 50);
            world.Graph.Nodes["Lost"].OwnerFactionTag = NeutralFaction.Tag;   // decayed after the post

            List<string> log = Tick(world, st);

            PendingFleetAction only = Assert.Single(st.PendingFleets);
            Assert.Equal("Live", only.TargetNodeId);
            Assert.Equal(50, only.Cargo["Explosives"]);   // the whole surplus, not 40
            Assert.Empty(HaulsTo(st, "Lost"));
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Hauler"]);   // second hull unspent
            // Skipped in matching, so it does not restate a block line every cycle either.
            Assert.DoesNotContain(log, l => l.Contains("HAUL_BLOCKED"));
        }

        [Fact]
        public void MultipleDemandNodes_AllGetServiced_NotJustTheLargestShortfall()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 1000;
            StockHaulShips(source, 2); // one dispatch to BigNeed, one to SmallNeed

            var bigNeed   = AddNode(world, "BigNeed");
            var smallNeed = AddNode(world, "SmallNeed");

            // BigNeed has by far the larger shortfall, but SmallNeed's tiny need should no longer
            // be starved out just because it isn't the single largest demand this tick.
            PostItemDemand(world, "F", "Explosives", "BigNeed", 500);
            PostItemDemand(world, "F", "Explosives", "SmallNeed", 2);

            Tick(world, st);

            Assert.Equal(2, HaulTo(st, "SmallNeed").Cargo["Explosives"]);
            Assert.Equal(500, HaulTo(st, "BigNeed").Cargo["Explosives"]);
            // Cargo is loaded (debited from the source) at dispatch, not at arrival.
            Assert.Equal(1000 - 502, world.GetNodeInventory("Source", "Explosives"));
        }

        [Fact]
        public void SmallestShortfallFirst_ClearsMoreDemandsWhenSurplusIsLimited()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 12; // enough for the two small ones, not the big one
            StockHaulShips(source, 3);

            var bigNeed    = AddNode(world, "BigNeed");
            var smallNeed1 = AddNode(world, "SmallNeed1");
            var smallNeed2 = AddNode(world, "SmallNeed2");

            PostItemDemand(world, "F", "Explosives", "BigNeed", 100);
            PostItemDemand(world, "F", "Explosives", "SmallNeed1", 2);
            PostItemDemand(world, "F", "Explosives", "SmallNeed2", 2);

            Tick(world, st);

            // Both small demands fully cleared first...
            Assert.Equal(2, HaulTo(st, "SmallNeed1").Cargo["Explosives"]);
            Assert.Equal(2, HaulTo(st, "SmallNeed2").Cargo["Explosives"]);
            // ...leaving whatever's left (8) for the big one, not zero.
            Assert.Equal(8, HaulTo(st, "BigNeed").Cargo["Explosives"]);
        }

        [Fact]
        public void SameSourceUnitsAreNotDoubleCounted_AcrossMultipleDestinationsInOneTick()
        {
            var world = MakeWorld();
            var st = MakeState();

            var sourceA = AddNode(world, "SourceA");
            sourceA.Inventory["Explosives"] = 5;
            StockHaulShips(sourceA, 1);
            var sourceB = AddNode(world, "SourceB");
            sourceB.Inventory["Explosives"] = 5;
            StockHaulShips(sourceB, 1);

            var need1 = AddNode(world, "Need1");
            var need2 = AddNode(world, "Need2");

            PostItemDemand(world, "F", "Explosives", "Need1", 5);
            PostItemDemand(world, "F", "Explosives", "Need2", 5);

            Tick(world, st);

            long total = HaulsTo(st, "Need1").Sum(f => f.Cargo["Explosives"])
                       + HaulsTo(st, "Need2").Sum(f => f.Cargo["Explosives"]);
            Assert.Equal(10, total); // exactly the total surplus available, not more
            Assert.Equal(0, world.GetNodeInventory("SourceA", "Explosives"));
            Assert.Equal(0, world.GetNodeInventory("SourceB", "Explosives"));
        }

        [Fact]
        public void MultipleHaulsToSameDestinationInOneTick_GetDistinctFleetIds()
        {
            // Live-found (2026-08-08): every other ActionKind dispatches at most once per
            // (actor, tick, destination), so PendingFleetAction.DeriveId's three inputs were always
            // unique. Haul breaks that assumption routinely -- one node needing two different items
            // in the same tick produces two Haul convoys sharing actor/departedUtc/targetId, which
            // collided into the same 6-hex FleetId before DeriveId gained its source+item
            // differentiator. Not an economic bug (each fleet's own Cargo/crediting was always
            // correct) but a real diagnostic-correlation one.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 50;
            source.Inventory["SteelPlate"] = 50;
            StockHaulShips(source, 2);

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);
            PostItemDemand(world, "F", "SteelPlate", "Need", 50);

            Tick(world, st);

            List<PendingFleetAction> hauls = HaulsTo(st, "Need").ToList();
            Assert.Equal(2, hauls.Count);
            Assert.NotEqual(hauls[0].FleetId, hauls[1].FleetId);
        }

        /// <summary>Two hauls that leave in one tick from one donor for one pickup, bound for
        /// different destinations. Every input the id hash reads is the same for both -- actor,
        /// tick, pickup node, donor and item -- so only a minted dispatch ordinal tells them apart.</summary>
        private static FactionState DispatchTwoHaulsThroughOnePickup(WorldModel world, bool staged,
                                                                     out List<string> log)
        {
            var st = MakeState();
            StockHaulShips(AddNode(world, "Elsewhere"), 2);
            AddNode(world, "Source").Inventory["Ingot:Iron"] = 100;
            AddNodeAt(world, "Dest1", new Vec3(400, 0, 0));
            AddNodeAt(world, "Dest2", new Vec3(0, 400, 0));
            PostItemDemand(world, "F", "Ingot:Iron", "Dest1", 50);
            PostItemDemand(world, "F", "Ingot:Iron", "Dest2", 50);

            log = new List<string>();
            var states = new List<FactionState> { st };
            if (staged)
            {
                var manager = new OperationsManager(itemVolumes: MineTestVolumes);
                bool done = false;
                for (int i = 0; i < 100 && !done; i++)
                    done = manager.TryAdvance(states, world, world.Tuning, FrameDeadline.Unbounded(), log);
                Assert.True(done, "Operations sweep did not complete");
            }
            else
                new OperationsManager(itemVolumes: MineTestVolumes).Tick(states, world, world.Tuning, log);
            return st;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HaulsThroughOnePickupInOneTick_GetDistinctFleetIds(bool staged)
        {
            // Live 2026-09-29 (EXP, N217): five hauls left one tick for one pickup, each with its own
            // hull, prefab and destination, and all five logged as [40A672].
            var world = MakeWorld();
            List<string> log;
            FactionState st = DispatchTwoHaulsThroughOnePickup(world, staged, out log);

            List<PendingFleetAction> hauls = st.PendingFleets.Where(f => f.Kind == ActionKind.Haul).ToList();
            Assert.Equal(2, hauls.Count);
            Assert.All(hauls, h => Assert.Equal("Source", h.TargetNodeId));
            Assert.All(hauls, h => Assert.Equal(FleetStopKind.Pickup, h.CurrentStopKind));
            Assert.Equal(hauls[0].DepartedUtc, hauls[1].DepartedUtc);
            Assert.NotEqual(hauls[0].FleetId, hauls[1].FleetId);
            Assert.False(hauls[1].IsDuplicateOf(hauls[0]));
            // The dispatch lines carry the ids the roster holds, one each.
            Assert.Equal(2, log.Count(l => l.Contains("HAUL_DISPATCHED")));
            foreach (PendingFleetAction h in hauls)
                Assert.Single(log, l => l.Contains("HAUL_DISPATCHED") && l.Contains(h.LogId));
        }

        [Fact]
        public void HaulsThroughOnePickup_OneLostAtThePickup_TheOtherFliesOnWithItsCargo()
        {
            // The live cost: one of the pickup-mates was destroyed at N217 and the host's
            // duplicate sweep wrote the others off at ratio 0 with their hydrogen aboard. The
            // sweep dismisses what IsDuplicateOf matches, so it must match none of the mates.
            var world = MakeWorld();
            List<string> log;
            FactionState st = DispatchTwoHaulsThroughOnePickup(world, false, out log);
            Assert.Equal(2, st.PendingFleets.Count);

            // What the host writes when a grid is destroyed, then its sibling sweep.
            PendingFleetAction lost = st.PendingFleets[0];
            lost.MaterialisedEntityId = -1;
            lost.ForcedRatio = 0.0;
            lost.ForcedRatioCause = "grid_destroyed";
            lost.EstimatedArrivalUtc = world.UtcNow;
            st.PendingFleets[0] = lost;
            Assert.DoesNotContain(st.PendingFleets.Skip(1), f => f.IsDuplicateOf(lost));

            string survivorId = st.PendingFleets[1].FleetId;
            var resolveLog = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, world.Tuning,
                                                 new SeededRandom(1), resolveLog);

            Assert.DoesNotContain(resolveLog, l => l.Contains("sibling_dismiss") || l.Contains("FLEET_RECALLED"));
            PendingFleetAction survivor = Assert.Single(st.PendingFleets, f => f.FleetId == survivorId);
            Assert.False(survivor.Resolved);
            Assert.Null(survivor.ForcedRatio);
            Assert.Equal(FleetStopKind.Service, survivor.CurrentStopKind);
            Assert.StartsWith("Dest", survivor.TargetNodeId);
            Assert.Equal(50, survivor.Cargo["Ingot:Iron"]);
        }

        [Fact]
        public void NoSurplusAnywhere_NoTransferHappens()
        {
            var world = MakeWorld();
            var st = MakeState();

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 5);

            var log = Tick(world, st);

            Assert.Equal(0, world.GetNodeInventory("Need", "Explosives"));
            Assert.Empty(st.PendingFleets);
            // Not Assert.Empty(log): OPS_TICK_PERF is a timing-gated diagnostic that fires whenever
            // the tick crosses PerfLogThresholdMs, which a cold process does on its own. Asserting an
            // empty log made this test depend on how warm the runner happened to be -- it failed run
            // alone and passed in the full suite. What it means is that nothing was moved, so that is
            // what it now asserts. See FAC-100.
            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER") || l.Contains("HAUL_DISPATCHED"));
        }

        [Fact]
        public void DemandAlreadySatisfied_IsSkipped()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            StockHaulShips(source, 5);

            var satisfied = AddNode(world, "Satisfied");
            satisfied.Inventory["Explosives"] = 10;
            PostItemDemand(world, "F", "Explosives", "Satisfied", 5); // already has more than it needs

            Tick(world, st);

            // No dispatch, no transfer should have occurred into or out of the already-satisfied node.
            Assert.Equal(10, world.GetNodeInventory("Satisfied", "Explosives"));
            Assert.Equal(100, world.GetNodeInventory("Source", "Explosives"));
            Assert.Empty(st.PendingFleets);
        }

        [Fact]
        public void BuildQueueReservedStock_IsNotRoutedAwayToAnotherNodesDemand()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Comp:SteelPlate"] = 100;
            StockHaulShips(source, 5);

            // Source's own pending ship build needs all 100 units — none of it is routable surplus,
            // even though another node also wants Comp:SteelPlate this tick.
            // ExpiresUtc matters: an expired entry no longer withholds anything, since the next
            // ShipyardManager tick prunes and refunds it anyway (SpatialNode.BuildQueueReserved).
            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", ExpiresUtc = world.UtcNow.AddHours(23) };
            entry.Bom["Comp:SteelPlate"] = 100;
            source.BuildQueue.Add(entry);

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Comp:SteelPlate", "Need", 50);

            Tick(world, st);

            Assert.Equal(100, world.GetNodeInventory("Source", "Comp:SteelPlate"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Comp:SteelPlate"));
            Assert.Empty(st.PendingFleets);
        }

        [Fact]
        public void BuildQueuePartiallyReservedStock_OnlyTheUnreservedPortionRoutes()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Comp:SteelPlate"] = 100;
            StockHaulShips(source, 5);

            // Source's build only needs 80 of its 100 units — the remaining 20 is genuine surplus.
            // ExpiresUtc matters: an expired entry no longer withholds anything, since the next
            // ShipyardManager tick prunes and refunds it anyway (SpatialNode.BuildQueueReserved).
            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", ExpiresUtc = world.UtcNow.AddHours(23) };
            entry.Bom["Comp:SteelPlate"] = 80;
            source.BuildQueue.Add(entry);

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Comp:SteelPlate", "Need", 50);

            Tick(world, st);

            Assert.Equal(80, world.GetNodeInventory("Source", "Comp:SteelPlate"));
            Assert.Equal(20, HaulTo(st, "Need").Cargo["Comp:SteelPlate"]);
        }

        [Fact]
        public void NearestSourceIsPreferredOverFartherSourceWithEnoughSurplusEitherWay()
        {
            var world = MakeWorld();
            var st = MakeState();

            var need = AddNode(world, "Need");
            need.Position = new Vec3(0, 0, 0);

            var near = AddNode(world, "Near");
            near.Position = new Vec3(1, 0, 0);
            near.Inventory["Explosives"] = 50;
            StockHaulShips(near, 1);

            var far = AddNode(world, "Far");
            far.Position = new Vec3(100, 0, 0);
            far.Inventory["Explosives"] = 50;
            StockHaulShips(far, 1);

            PostItemDemand(world, "F", "Explosives", "Need", 20);

            Tick(world, st);

            // Either source alone could fully cover the 20-unit shortfall — the nearer one must win,
            // not whichever happened to be added to the graph first.
            PendingFleetAction fleet = HaulTo(st, "Need");
            Assert.Equal("Near", fleet.OriginNodeId);
            Assert.Equal(20, fleet.Cargo["Explosives"]);
            Assert.Equal(50, world.GetNodeInventory("Far", "Explosives")); // untouched
        }

        // ---- dispatch mechanics (ship gating, duplicate prevention, arrival) ---------------

        [Fact]
        public void NoShipAtChosenSource_ButNetworkHasOneElsewhere_DispatchesPickupChainedHaul_NoInstantTransfer()
        {
            // The faction isn't ship-less network-wide (Elsewhere has one) — this specific vector's
            // chosen source just doesn't. That's ordinary Haul friction, not the bootstrap deadlock,
            // so it must NOT fall back to an instant transfer — the point this test guards. Since a
            // Haul-suitable ship exists elsewhere in the network, this is the tier-2 case (see
            // docs/fleet-waypoints-plan.md): one Haul convoy fetches the ship from Elsewhere via a
            // Pickup leg, then delivers for real, rather than asking for a new one to be built at
            // Source (which may have no Shipyard at all, as here) or posting a Position:Trade demand
            // for FleetManager to resolve as a separate fleet on a later tick.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100; // surplus exists...
            // ...but no ship is stocked at Source.

            var elsewhere = AddNode(world, "Elsewhere");
            StockHaulShips(elsewhere, 1); // ship exists in the network, just not where this vector needs it

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st);

            Assert.Empty(world.Investments.SnapshotFor("F")); // no planet-scoped escalation on a DeepSpace leg
            Assert.Equal(100, world.GetNodeInventory("Source", "Explosives")); // untouched at dispatch — not an instant transfer, loads at the pickup stop
            Assert.Equal(0, world.GetNodeInventory("Need", "Explosives")); // not delivered yet either — real transit, not teleportation

            Assert.Empty(world.Demand.SnapshotShipDemands("F"));
            Assert.Empty(world.Demand.SnapshotPositioningDemands("F")); // no Position: demand — dispatched directly instead

            PendingFleetAction fleet = Assert.Single(st.PendingFleets);
            Assert.Equal(ActionKind.Haul, fleet.Kind);
            Assert.Equal("Elsewhere", fleet.OriginNodeId);
            Assert.Equal("Source", fleet.TargetNodeId);
            Assert.Equal(FleetStopKind.Pickup, fleet.CurrentStopKind);
        }

        [Fact]
        public void NoShipAnywhereInNetwork_BootstrapFallsBackToInstantTransfer()
        {
            // True bootstrap deadlock: zero cargo-suitable ships anywhere in the faction, so real
            // Haul dispatch is impossible — but the faction also can't ever BUILD its first cargo
            // ship without moving BOM components somewhere first. Must fall back to an instant,
            // distinctly-tagged transfer rather than deadlocking forever.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            // No ship stocked anywhere in the whole faction network.

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st);

            Assert.Empty(st.PendingFleets); // instant, not a fleet dispatch
            Assert.Equal(50, world.GetNodeInventory("Need", "Explosives"));
            Assert.Equal(50, world.GetNodeInventory("Source", "Explosives"));
            Assert.Contains(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
        }

        [Fact]
        public void BootstrapFallback_DoesNotFireWhileTheFactionsOnlyHullIsAwayOnAnErrand()
        {
            // FAC-199. ShipStock holds only PARKED hulls -- ShipPool.Consume removes one at dispatch
            // and ShipPool.Return credits it back on arrival -- so the availability reading sees a
            // one-hull faction as fleetless for the whole duration of every errand that hull flies.
            // On that reading the bootstrap inverts the economy: the fewer hulls you own, the faster
            // and cheaper your logistics, because a busy fleet is indistinguishable from no fleet.
            // Live on 2026-08-31 this moved ~147,000 units of ore into one node from six sources in
            // a single tick, including 51,801 Ore:Gold, for four factions that all owned hulls.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            // Nothing parked anywhere: the faction's one hauler is out, exactly as ShipPool.Consume
            // leaves the world between dispatch and return.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind          = ActionKind.Haul,
                ActorTag      = "F",
                ShipPrefabId  = "Hauler",
                ConvoySize    = 1,
                OriginNodeId  = "Source",
                TargetNodeId  = "Elsewhere", // unrelated leg; Cargo left null so it claims no (node, item) pair
            });

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st);

            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Explosives"));   // nothing teleported
            Assert.Equal(100, world.GetNodeInventory("Source", "Explosives")); // and nothing left the source
        }

        [Fact]
        public void BootstrapFallback_DoesNotFireWhileTheFactionsOnlyHullSitsInAStoreListing()
        {
            // Same defect, second door: ShipPool's availability reading subtracts ListedForSale on
            // top of the parked/away split, so listing the last hauler in a store would otherwise
            // buy the faction free instant logistics. Ownership is what gates the bootstrap, and a
            // listed hull is still owned until someone buys it.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            StockHaulShips(source, 1);
            source.ListedForSale["Hauler"] = 1; // reserved for sale -- unavailable to dispatch, still owned

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st);

            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Explosives"));
            Assert.Equal(100, world.GetNodeInventory("Source", "Explosives"));
        }

        [Fact]
        public void BootstrapFallback_StillFiresWhenAnInFlightFleetCarriesNoCargoSuitableHull()
        {
            // The narrowed gate must not close the deadlock it exists to break. A faction whose only
            // pending fleet is a combat squadron owns no cargo hull, so it is as unable to move BOM
            // components to its shipyard as one with an empty PendingFleets list.
            var world = MakeWorld();
            var st = MakeState();

            // The fake catalogue gives every unlisted prefab 1,000,000 cargo litres, which would make
            // a "Fighter" cargo-suitable by default; zero it so the hull really is combat-only.
            ((FakePrefabCatalogue)world.PrefabCatalogue).CargoLitersByPrefab["Fighter"] = 0;

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind         = ActionKind.Raid,
                ActorTag     = "F",
                ShipPrefabId = "Fighter", // combat hull: not Haul-suitable
                ConvoySize   = 1,
                TargetNodeId = "Elsewhere",
            });

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.Equal(50, world.GetNodeInventory("Need", "Explosives"));
        }

        [Fact]
        public void BootstrapTransfer_IsCountedOnOpsTickPerf_EvenBelowTheElapsedTimeThreshold()
        {
            // FAC-199's second half. A teleporting tick is the CHEAP one -- it skips route planning,
            // suitability checks and dispatch entirely -- so PerfLogThresholdMs selects against
            // exactly the ticks the counter has to measure. The threshold is set out of reach, so a line
            // here at all is the threshold bypass working, and requiring teleported= to be non-zero on
            // it says which bypass.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            // No hull owned anywhere -- genuine bootstrap.

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st, TestSupport.Tuning(), NeverSlowMs);

            string perf = Assert.Single(log.Where(l => l.StartsWith("OPS_TICK_PERF", StringComparison.Ordinal)));
            Assert.Contains(" teleported=1", perf);
        }

        [Fact]
        public void OpsTickPerf_StaysSilentOnACheapTickThatTeleportedNothing()
        {
            // The discriminator for the test above: without a bootstrap transfer the same cheap tick
            // must still be below the threshold and emit no line at all. Otherwise "a line appeared"
            // would prove nothing about the bypass. "Cheap" is the test's threshold, not the clock's.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            StockHaulShips(source, 1);

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st, TestSupport.Tuning(), NeverSlowMs);

            Assert.DoesNotContain(log, l => l.StartsWith("OPS_TICK_PERF", StringComparison.Ordinal));
        }

        [Fact]
        public void BootstrapFallback_StopsOnceTheFactionHasAnyCargoShipAnywhere()
        {
            // Self-terminating: the moment ANY cargo-suitable ship exists anywhere in the network —
            // even at a node unrelated to this specific vector — real Haul dispatch takes back over.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            StockHaulShips(source, 1);

            var unrelated = AddNode(world, "Unrelated");
            StockHaulShips(unrelated, 1); // this faction is no longer ship-less, even though Unrelated has nothing to do with this vector

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            var log = Tick(world, st);

            Assert.DoesNotContain(log, l => l.Contains("ITEM_XFER_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Explosives")); // not delivered yet — real transit
            Assert.Equal(50, HaulTo(st, "Need").Cargo["Explosives"]);
        }

        [Fact]
        public void HaulInFlight_BlocksDuplicateDispatchForSamePairNextTick()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            StockHaulShips(source, 5); // plenty of ships — the guard, not stock, must be what blocks the second dispatch

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 20);

            Tick(world, st);
            Assert.Single(st.PendingFleets);

            // Demand is still active (transit hasn't delivered anything yet) — a second tick must not
            // dispatch a second convoy for the same (Need, Explosives) pair while the first is in flight.
            PostItemDemand(world, "F", "Explosives", "Need", 20);
            Tick(world, st);

            Assert.Single(st.PendingFleets);
        }

        /// <summary>FAC-109 lever (a): a departure node's hydrogen buffer row is served by one haul
        /// and the haul in flight counts against it. A buffer that did not see its own inbound
        /// hydrogen would send a second hull every pass until the first landed.</summary>
        [Fact]
        public void AFuelBufferRow_CountsItsInFlightHydrogen_AndIsNotServedTwice()
        {
            var world = MakeWorld();
            GiveHydrogenTank(world);
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();
            tuning.FuelBurnEmaHours = 2.0;

            var depot = AddNodeAt(world, "Depot", new Vec3(0, 0, 0));
            depot.Inventory["Gas:Hydrogen"] = 50_000_000;
            StockHaulShips(depot, 5);

            var yard = AddNodeAt(world, "Yard", new Vec3(50, 0, 0));
            yard.StableStationId = 1;
            FuelDraw.CreditBurn(yard, world.UtcNow, tuning, "Gas:Hydrogen", 4_000_000 * tuning.FuelBurnEmaHours);
            long buffer = FuelReserve.BufferTarget(yard, "Gas:Hydrogen", world.UtcNow, tuning);
            Assert.True(buffer > 0);

            PostItemDemand(world, "F", "Gas:Hydrogen", "Yard", buffer, DemandClass.Fuel);
            Tick(world, st, tuning);
            Assert.Single(HaulsTo(st, "Yard"));

            PostItemDemand(world, "F", "Gas:Hydrogen", "Yard", buffer, DemandClass.Fuel);
            Tick(world, st, tuning);
            Assert.Single(HaulsTo(st, "Yard"));
        }

        /// <summary>The fuel-buffer haul above: hydrogen boards only a hull's hydrogen tank. A
        /// tankless hull boards none of it, so no haul leaves; given a tank, the load is the tank.</summary>
        [Fact]
        public void AFuelBufferHaul_LoadsOnlyWhatTheTankHolds()
        {
            Assert.Equal(-1L, FuelBufferHaulLoad(tankLitres: 0));
            Assert.Equal(500_000L, FuelBufferHaulLoad(tankLitres: 500_000));
        }

        /// <summary>What the one haul to Yard carries, or -1 when none left.</summary>
        private static long FuelBufferHaulLoad(long tankLitres)
        {
            var world = MakeWorld();
            if (tankLitres > 0)
                ((FakePrefabCatalogue)world.PrefabCatalogue).GasTanksByPrefab["Hauler"]
                    = new Dictionary<string, long> { { "Gas:Hydrogen", tankLitres } };
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();
            tuning.FuelBurnEmaHours = 2.0;

            var depot = AddNodeAt(world, "Depot", new Vec3(0, 0, 0));
            depot.Inventory["Gas:Hydrogen"] = 50_000_000;
            StockHaulShips(depot, 5);

            var yard = AddNodeAt(world, "Yard", new Vec3(50, 0, 0));
            yard.StableStationId = 1;
            FuelDraw.CreditBurn(yard, world.UtcNow, tuning, "Gas:Hydrogen", 4_000_000 * tuning.FuelBurnEmaHours);
            long buffer = FuelReserve.BufferTarget(yard, "Gas:Hydrogen", world.UtcNow, tuning);
            PostItemDemand(world, "F", "Gas:Hydrogen", "Yard", buffer, DemandClass.Fuel);
            Tick(world, st, tuning);

            PendingFleetAction[] hauls = HaulsTo(st, "Yard").ToArray();
            if (hauls.Length == 0) return -1;
            Assert.Single(hauls);
            return hauls[0].Cargo["Gas:Hydrogen"];
        }

        /// <summary>A node that has just received hydrogen into its reserve, and burns hydrogen
        /// itself, is a haul source for no more than its stock less the departure buffer it keeps
        /// (<see cref="FuelReserve.Keep"/>). Run through the synchronous tick and the staged cycle,
        /// which share the matcher's per-item surplus snapshot.</summary>
        [Theory]
        [InlineData(false, 100_000L, 95_000L)]
        [InlineData(true, 100_000L, 95_000L)]
        [InlineData(false, 5_000L, -1L)]
        [InlineData(true, 4_000L, -1L)]
        public void AReserveNodesJustDeliveredHydrogen_IsRoutedOnlyAboveItsKeep(bool staged,
            long yardStock, long expectedLoad)
        {
            Assert.Equal(expectedLoad, ReserveHaulLoad(staged, yardStock, 10_000));
        }

        /// <summary>A depot with a large stock and a small burn keeps only its floor and still
        /// supplies the whole shortfall.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADepotWithSmallBurn_StillSuppliesAboveItsKeep(bool staged)
        {
            Assert.Equal(500_000L, ReserveHaulLoad(staged, 900_000, 100));
        }

        /// <summary>Hydrogen hauled out of Yard toward Other, or -1 when no haul left. Yard is founded
        /// and burns <paramref name="burnPerHour"/>; with a 0.5 h buffer lead and a 1,000 L floor a
        /// burn of 10,000 L/h keeps 5,000 L and 100 L/h keeps the floor.</summary>
        private static long ReserveHaulLoad(bool staged, long yardStock, double burnPerHour)
        {
            var world = MakeWorld();
            GiveHydrogenTank(world);
            world.SetFuelReserveKeys("F", new List<string> { "Gas:Hydrogen" });
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();
            tuning.FuelDrawMode = FuelDrawModes.Draw;
            tuning.FuelBurnEmaHours = 2.0;
            tuning.FuelReserveHours = 6.0;
            tuning.FuelReserveFloor = 1_000;
            tuning.FuelBufferLeadHours = 0.5;
            tuning.FuelBufferMaxFloors = 100.0;

            var yard = AddNodeAt(world, "Yard", new Vec3(0, 0, 0));
            yard.StableStationId = 1;
            FuelDraw.CreditBurn(yard, world.UtcNow, tuning, "Gas:Hydrogen", burnPerHour * tuning.FuelBurnEmaHours);
            yard.Inventory["Gas:Hydrogen"] = yardStock;
            StockHaulShips(yard, 2);

            AddNodeAt(world, "Other", new Vec3(50, 0, 0));
            PostItemDemand(world, "F", "Gas:Hydrogen", "Other", 500_000, DemandClass.Fuel);

            if (staged)
            {
                world.Tuning = tuning;
                var manager = new OperationsManager(itemVolumes: MineTestVolumes);
                var states = new List<FactionState> { st };
                var log = new List<string>();
                int passes = 0;
                while (!manager.TryAdvance(states, world, tuning, FrameDeadline.Unbounded(), log))
                    Assert.True(++passes < 1000, "Operations sweep did not complete");
            }
            else
            {
                Tick(world, st, tuning);
            }

            PendingFleetAction[] hauls = HaulsTo(st, "Other").ToArray();
            if (hauls.Length == 0) return -1;
            Assert.Single(hauls);
            return hauls[0].Cargo["Gas:Hydrogen"];
        }

        private sealed class FruitOnly : INutritionCatalogue
        {
            public double NutritionPerUnit(string invKey) { return invKey == "Fruit" ? 0.06 : 0; }
        }

        /// <summary>A Habitat's own harvest is a haul source only above what its workers will eat over
        /// <see cref="FactionTuning.WorkerFoodReserveHours"/> (<see cref="WorkerFoodDemand.KeepUnits"/>):
        /// four workers keep 892 fruit, so of 2,000 a processor elsewhere gets 1,108; with nobody living
        /// there it gets the lot. Through the synchronous tick and the staged cycle, which read the
        /// surplus through different overloads.</summary>
        [Theory]
        [InlineData(false, 4.0, 1_108L)]
        [InlineData(true, 4.0, 1_108L)]
        [InlineData(false, 0.0, 2_000L)]
        [InlineData(true, 0.0, 2_000L)]
        public void AHabitatsHarvest_IsRoutedOnlyAboveWhatItsWorkersEat(bool staged, double workers, long expectedLoad)
        {
            var world = MakeWorld();
            world.Nutrition = new FruitOnly();
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();
            tuning.HabitatBedsPerLevel = 20;
            tuning.WorkerNutritionPerHour = 2.23;
            tuning.WorkerFoodReserveHours = 6.0;

            var farm = AddNodeAt(world, "Farm", new Vec3(0, 0, 0));
            farm.Capabilities[NodeCapabilityKind.Habitat] = 1;
            farm.Workers = workers;
            farm.Inventory["Fruit"] = 2_000;
            StockHaulShips(farm, 2);

            AddNodeAt(world, "Kitchen", new Vec3(50, 0, 0));
            PostItemDemand(world, "F", "Fruit", "Kitchen", 5_000, DemandClass.RecipeInput);

            if (staged)
            {
                world.Tuning = tuning;
                var manager = new OperationsManager(itemVolumes: MineTestVolumes);
                var states = new List<FactionState> { st };
                var log = new List<string>();
                int passes = 0;
                while (!manager.TryAdvance(states, world, tuning, FrameDeadline.Unbounded(), log))
                    Assert.True(++passes < 1000, "Operations sweep did not complete");
            }
            else
            {
                Tick(world, st, tuning);
            }

            PendingFleetAction[] hauls = HaulsTo(st, "Kitchen").ToArray();
            Assert.Single(hauls);
            Assert.Equal(expectedLoad, hauls[0].Cargo["Fruit"]);
        }

        [Fact]
        public void InFlightDestination_DoesNotStarveALimitedSurplusASiblingDestinationCouldStillUse()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 20; // only enough to serve ONE of the two destinations
            StockHaulShips(source, 2);

            var already = AddNode(world, "Already");
            var fresh   = AddNode(world, "Fresh");
            PostItemDemand(world, "F", "Explosives", "Already", 20);
            PostItemDemand(world, "F", "Explosives", "Fresh", 20);

            // First tick: only enough surplus for one destination — Already wins (whichever the
            // fair-share match picks first with equal shortfalls); Fresh's demand stays outstanding.
            Tick(world, st);
            Assert.Single(st.PendingFleets);

            // Second tick: more surplus arrives. Already is already being served in flight — its
            // repeated demand post must not re-claim this surplus and starve Fresh, which has no
            // convoy coming yet.
            world.AddNodeInventory("Source", "Explosives", 20);
            PostItemDemand(world, "F", "Explosives", "Already", 20);
            PostItemDemand(world, "F", "Explosives", "Fresh", 20);
            Tick(world, st);

            Assert.Equal(20, HaulTo(st, "Fresh").Cargo["Explosives"]);
            Assert.Single(HaulsTo(st, "Already")); // still just the original convoy, not a second one
        }

        [Fact]
        public void DispatchedHaul_CreditsDestinationOnlyOnArrival_NotAtDispatch()
        {
            var world = MakeWorld();
            var st = MakeState();
            var tuning = TestSupport.Tuning();
            // FleetSpeedKmPerMin/FleetSpeedMinKmPerMin/FleetAccelKmPerMin2 are a coupled trapezoidal
            // speed profile (see FactionTuning.TryTransitProfile) — all three must be overridden
            // together, or a vMin above the overridden vMax silently zeroes the transit time.
            // Mirrors FleetConvoyTests.SlowFleetTuning().
            tuning.FleetSpeedKmPerMin = 1.0;
            tuning.FleetSpeedMinKmPerMin = 0.999;
            tuning.FleetAccelKmPerMin2 = 1000000.0;

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 50;
            StockHaulShips(source, 1);
            var need = AddNode(world, "Need");
            need.Position = new Vec3(10, 0, 0); // real distance so transit isn't instant

            var log = new List<string>();
            PostItemDemand(world, "F", "Explosives", "Need", 50);
            new OperationsManager().Tick(new List<FactionState> { st }, world, tuning, log);

            PendingFleetAction fleet = HaulTo(st, "Need");
            Assert.Equal(50, fleet.Cargo["Explosives"]);
            Assert.Equal(0, world.GetNodeInventory("Need", "Explosives")); // not delivered yet
            Assert.True(fleet.EstimatedArrivalUtc > world.UtcNow); // real transit time, not instant

            world.UtcNow = fleet.EstimatedArrivalUtc;
            var states = new List<FactionState> { st };
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null);

            Assert.Equal(50, world.GetNodeInventory("Need", "Explosives"));
            Assert.Empty(st.PendingFleets);

            // Regression: a real Haul arrival must NOT credit DemandHistory anymore -- the gravity
            // term is fed by actual consumption (ProductionManager/ShipyardManager), never delivery,
            // since delivery-fed gravity is a self-reinforcing loop with no external anchor (see
            // docs/material-vectors.md's dated correction note for the full account).
            Assert.Null(world.DrainPendingDemandHistory("Need"));
        }

        // ---- Slice C: single-target vector queries (contract generation) --------------------

        [Fact]
        public void TryFindSource_FindsNearestSurplusNode()
        {
            var world = MakeWorld();

            var far = AddNode(world, "Far");
            far.Position = new Vec3(100, 0, 0);
            far.Inventory["Explosives"] = 30;

            var near = AddNode(world, "Near");
            near.Position = new Vec3(10, 0, 0);
            near.Inventory["Explosives"] = 30;

            var need = AddNode(world, "Need");
            need.Position = new Vec3(0, 0, 0);

            var owned = new List<SpatialNode> { far, near, need };

            MaterialVector vector;
            bool found = OperationsManager.TryFindSource("F", "Explosives", "Need", 20, owned, world, out vector);

            Assert.True(found);
            Assert.Equal("Near", vector.SourceNodeId);
            Assert.Equal("Need", vector.NeedNodeId);
            Assert.Equal("Need", vector.DeliverNodeId);
            Assert.Equal(20, vector.Quantity); // capped at requested quantity, not the source's full surplus
        }

        [Fact]
        public void TryFindSource_ReturnsFalse_WhenNoOwnedNodeHasSurplus()
        {
            var world = MakeWorld();
            var source = AddNode(world, "Source"); // zero stock
            var need = AddNode(world, "Need");
            var owned = new List<SpatialNode> { source, need };

            MaterialVector vector;
            bool found = OperationsManager.TryFindSource("F", "Explosives", "Need", 20, owned, world, out vector);

            Assert.False(found);
        }

        [Fact]
        public void TryFindDemand_FindsOutstandingDemandForSourcesSurplus()
        {
            var world = MakeWorld();
            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 30;
            var need = AddNode(world, "Need");

            PostItemDemand(world, "F", "Explosives", "Need", 50);
            var demands = world.Demand.SnapshotItemDemands("F");
            demands.Sort((a, b) => b.Quantity.CompareTo(a.Quantity));

            MaterialVector vector;
            bool found = OperationsManager.TryFindDemand("F", "Source", demands, world, out vector);

            Assert.True(found);
            Assert.Equal("Explosives", vector.ItemKey);
            Assert.Equal("Source", vector.SourceNodeId);
            Assert.Equal("Need", vector.NeedNodeId);
            Assert.Equal("Need", vector.DeliverNodeId);
            Assert.Equal(30, vector.Quantity); // capped by the source's own surplus, not the full shortfall
        }

        [Fact]
        public void TryFindDemand_ReturnsFalse_WhenSourceStockMatchesNoOutstandingDemand()
        {
            var world = MakeWorld();
            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 30; // nothing anywhere demands it
            AddNode(world, "Need");

            var demands = world.Demand.SnapshotItemDemands("F");

            MaterialVector vector;
            bool found = OperationsManager.TryFindDemand("F", "Source", demands, world, out vector);

            Assert.False(found);
        }

        // ---- Slice C: ExternalHaulClaims (player-contract claim suppresses NPC dispatch) ----

        [Fact]
        public void ExternalHaulClaim_BlocksDispatchForSamePairNextTick()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            StockHaulShips(source, 5); // plenty of ships — the claim, not stock, must be what blocks dispatch

            AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 20);

            st.ExternalHaulClaims.Add(("Need", "Explosives")); // as if a player contract already claimed it
            Tick(world, st);

            Assert.Empty(st.PendingFleets);
        }

        // ---- Slice E: loaded-TWR cargo-mass trim -------------------------------------------

        [Fact]
        public void DispatchHaul_ManifestOverloadsTwr_TrimsQuantity_LogsCargoMassTrimmed()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cat = new FakePrefabCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000); // dryTwr ~1.53 at g=1, clears the real default MinDryTwr=1.05
            world.PrefabCatalogue = cat;
            world.Planets = new ConstantGravityCatalogue(1.0);

            var st = MakeState();
            var source = AddNode(world, "Source");
            source.Inventory["Ore"] = 1000;
            StockHaulShips(source, 1);
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var masses = new FakeMassCatalogue().Set("Ore", 10); // 100 units would be 1000kg — way over the ~456kg ceiling
            var log = new List<string>();
            new OperationsManager(itemMasses: masses).Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            PendingFleetAction haul = HaulTo(st, "Dest");
            Assert.Equal(45, haul.Cargo["Ore"]);
            Assert.Contains(log, l => l.Contains("cargo_mass_trimmed"));
        }

        [Fact]
        public void DispatchHaul_FitsUnderTwrCeiling_Unaffected_NoLogSuffix()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cat = new FakePrefabCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000);
            world.PrefabCatalogue = cat;
            world.Planets = new ConstantGravityCatalogue(1.0);

            var st = MakeState();
            var source = AddNode(world, "Source");
            source.Inventory["Ore"] = 1000;
            StockHaulShips(source, 1);
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 10); // 10 * 10kg = 100kg, well under the ~456kg ceiling

            var masses = new FakeMassCatalogue().Set("Ore", 10);
            var log = new List<string>();
            new OperationsManager(itemMasses: masses).Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            PendingFleetAction haul = HaulTo(st, "Dest");
            Assert.Equal(10, haul.Cargo["Ore"]);
            Assert.DoesNotContain(log, l => l.Contains("cargo_mass_trimmed"));
        }

        // ---- planet-aware Shipyard investment (local Haul-leg failure) ----------------------

        /// <summary>Stocks a cargo-suitable ship at an unrelated third node so
        /// ShipPool.NetworkHasSuitable is true faction-wide (DispatchHaul actually runs instead of
        /// the zero-stock RealizeBootstrapInstant fallback) without giving the vector's own source
        /// node anything to dispatch -- the simplest reliable way to force ShipPool.Consume to fail
        /// at the source specifically.</summary>
        private static void StockShipElsewhere(WorldModel world, string tag = "F")
        {
            SpatialNode elsewhere = AddNode(world, "Elsewhere", tag);
            StockHaulShips(elsewhere, 1);
        }

        [Fact]
        public void DispatchHaul_SamePlanetLegWithNoLocalStock_PostsPlanetScopedInvestment()
        {
            // Two vectors compete for the network's only Haul-suitable ship in the same tick:
            // SourceA is nearest to Dest and holds the one ship, so its vector dispatches for real,
            // consuming it. SourceB (farther, same shortfall pool) then finds nothing local AND
            // nothing left anywhere else in the network -- the genuine tier-3 case (see the
            // ship-acquisition fallback fix in docs/material-vectors.md), which is what should
            // trigger the same-planet Shipyard investment this test verifies. (StockShipElsewhere's
            // simple "a ship exists somewhere, never consumed" shape no longer reaches this branch at
            // all post-fix -- it's now always tier 2, Position:Trade -- so this scenario has to
            // actually deplete the network's ship stock within the tick to exercise tier 3.)
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            dest.Zone = NodeZone.Surface;
            dest.PlanetId = "Terra";
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var sourceA = AddNode(world, "SourceA");
            sourceA.Position = new Vec3(1, 0, 0);
            sourceA.Inventory["Ore"] = 60;
            StockHaulShips(sourceA, 1); // the network's only ship

            var sourceB = AddNode(world, "SourceB");
            sourceB.Position = new Vec3(100, 0, 0);
            sourceB.Zone = NodeZone.Surface;
            sourceB.PlanetId = "Terra";
            sourceB.Inventory["Ore"] = 40; // no ship here, and none left anywhere once SourceA's is consumed
            // A Shipyard is only asked for where one could be installed: a founded station in the
            // region, and the prerequisite capability somewhere in the network (FAC-238).
            sourceB.StableStationId = 4101;
            AddNode(world, "Manu").Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            Assert.Equal("SourceA", HaulTo(st, "Dest").OriginNodeId); // the ship-bearing source dispatched for real
            Assert.Single(world.Investments.SnapshotFor("F"));
            InvestmentRequest req = world.Investments.SnapshotFor("F")[0];
            Assert.Equal(NodeCapabilityKind.Shipyard, req.Cap);
            Assert.Equal("Terra", req.TargetPlanetId);
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST Shipyard planet=Terra"));
        }

        [Fact]
        public void DispatchHaul_CrossPlanetLeg_PostsNothing_RegressionGuard()
        {
            // Same no-local-stock failure shape as above, but Source and Dest are on DIFFERENT
            // planets -- this is exactly the bug the planet-identity fix exists to prevent: a
            // Shipyard on one planet must never be treated as satisfying need on another.
            // StockShipElsewhere means a ship exists in the network, so this is now the tier-2 case
            // (Position:Trade posted, FleetManager Ferries it in) -- it never reaches
            // MaybePostLocalShipyardInvestment at all, so the "posts nothing" guard holds via the
            // tier-2 short-circuit now, not just the original planet-mismatch early-return.
            var world = MakeWorld();
            StockShipElsewhere(world);
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Zone = NodeZone.Surface;
            source.PlanetId = "Terra";
            source.Inventory["Ore"] = 100;

            var dest = AddNode(world, "Dest");
            dest.Zone = NodeZone.Surface;
            dest.PlanetId = "Mars";
            PostItemDemand(world, "F", "Ore", "Dest", 50);

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            Assert.Empty(world.Investments.SnapshotFor("F"));
            Assert.DoesNotContain(log, l => l.Contains("INVEST_REQUEST Shipyard"));
        }

        [Fact]
        public void DispatchHaul_DeepSpaceLeg_PostsNothing_RegressionGuard()
        {
            // Neither end has a PlanetId (null) -- Ion/Hydrogen already serve deep space fine, so
            // this trigger must not fire there either. (Also now short-circuited by tier 2 before
            // MaybePostLocalShipyardInvestment is ever reached -- see the comment on the sibling
            // CrossPlanetLeg guard above.)
            var world = MakeWorld();
            StockShipElsewhere(world);
            var st = MakeState();

            var source = AddNode(world, "Source"); // Zone/PlanetId default: DeepSpace / null
            source.Inventory["Ore"] = 100;
            var dest = AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 50);

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            Assert.Empty(world.Investments.SnapshotFor("F"));
            Assert.DoesNotContain(log, l => l.Contains("INVEST_REQUEST Shipyard"));
        }

        [Fact]
        public void DispatchHaul_SamePlanetLeg_ShipyardAlreadyOnPlanet_PostsNothing_RegressionGuard()
        {
            // Also now short-circuited by tier 2 (StockShipElsewhere) before reaching
            // MaybePostLocalShipyardInvestment -- see the comment on CrossPlanetLeg above.
            var world = MakeWorld();
            StockShipElsewhere(world);
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Zone = NodeZone.Surface;
            source.PlanetId = "Terra";
            source.Inventory["Ore"] = 100;

            var dest = AddNode(world, "Dest");
            dest.Zone = NodeZone.Surface;
            dest.PlanetId = "Terra";
            PostItemDemand(world, "F", "Ore", "Dest", 50);

            var shipyardNode = AddNode(world, "TerraShipyard");
            shipyardNode.Zone = NodeZone.Surface;
            shipyardNode.PlanetId = "Terra";
            shipyardNode.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            Assert.Empty(world.Investments.SnapshotFor("F"));
            Assert.DoesNotContain(log, l => l.Contains("INVEST_REQUEST Shipyard"));
        }

        // ---- the region a local Shipyard request names, and what covers it (FAC-236) ----------

        /// <summary>The tier-3 shape the passing planet-scoped test above uses, factored out: two
        /// vectors compete for the network's only Haul hull in one tick, so the second finds nothing
        /// local and nothing anywhere and reaches MaybePostLocalShipyardInvestment for real. Every
        /// node is given a <see cref="StationSite"/> as well as a Zone, since the region the request
        /// names is read off Site.</summary>
        private static List<string> RunUnservableSameBodyLeg(WorldModel world, FactionState st,
            string planetId, StationSite site)
        {
            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            dest.Zone     = site == StationSite.Surface ? NodeZone.Surface : NodeZone.Orbit;
            dest.Site     = site;
            dest.PlanetId = planetId;
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var sourceA = AddNode(world, "SourceA");
            sourceA.Position = new Vec3(1, 0, 0);
            sourceA.Inventory["Ore"] = 60;
            StockHaulShips(sourceA, 1);              // the network's only ship, consumed this tick

            var sourceB = AddNode(world, "SourceB");
            sourceB.Position = new Vec3(100, 0, 0);
            sourceB.Zone     = dest.Zone;
            sourceB.Site     = site;
            sourceB.PlanetId = planetId;
            sourceB.Inventory["Ore"] = 40;           // no ship here, and none left anywhere
            // A Shipyard is only ever asked for where one could actually be installed: a founded
            // station in the region, and the prerequisite capability somewhere in the network
            // (FAC-238). Both ends of the leg are founded stations here.
            dest.StableStationId    = 4001;
            sourceB.StableStationId = 4002;
            AddNode(world, "Manu").Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);
            return log;
        }

        private static SpatialNode AddYard(WorldModel world, string id, string planetId, StationSite site)
        {
            SpatialNode yard = AddNode(world, id);
            yard.Zone     = site == StationSite.Surface ? NodeZone.Surface : NodeZone.Orbit;
            yard.Site     = site;
            yard.PlanetId = planetId;
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return yard;
        }

        /// <summary>Occupies the yard's one build slot with an entry that has absorbed nothing for
        /// <paramref name="stalledSeconds"/> of serviced time.</summary>
        private static void GiveBuild(SpatialNode yard, double stalledSeconds)
        {
            var e = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "P" };
            e.Bom["Comp:SteelPlate"] = 100;
            e.StalledSeconds = stalledSeconds;
            yard.BuildQueue.Add(e);
        }

        [Fact]
        public void DispatchHaul_LegInsideABodysWell_AsksForAYardInThatWell()
        {
            // Before FAC-236 this posted a request that named the body's GROUND, which on a body
            // forbidding surface stations no node can ever stand on -- so the request outlived every
            // install made against it and the next tick spent another one on the next node.
            var world = MakeWorld();
            var st = MakeState();

            List<string> log = RunUnservableSameBodyLeg(world, st, "Cauldron", StationSite.Orbital);

            InvestmentRequest req = Assert.Single(world.Investments.SnapshotFor("F"));
            Assert.Equal(NodeCapabilityKind.Shipyard, req.Cap);
            Assert.Equal("Cauldron", req.TargetPlanetId);
            Assert.Equal(StationSite.Orbital, req.TargetSite);
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST Shipyard planet=Cauldron site=Orbital yards=0/0"));
        }

        [Fact]
        public void DispatchHaul_YardInTheSameWell_ThatIsWorking_EarnsASecondAsk()
        {
            // The other half of the saturation gate, and the reason the idle case below proves
            // something: with the region's yard actually absorbing components, a second one is a
            // real answer to the leg that could not be served.
            var world = MakeWorld();
            var st = MakeState();
            GiveBuild(AddYard(world, "RingYard", "Cauldron", StationSite.Orbital), stalledSeconds: 0);

            List<string> log = RunUnservableSameBodyLeg(world, st, "Cauldron", StationSite.Orbital);

            Assert.Single(world.Investments.SnapshotFor("F"));
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST Shipyard planet=Cauldron site=Orbital yards=1/1"));
        }

        [Fact]
        public void DispatchHaul_YardInTheSameWell_ThatIsStalled_AsksForNothing()
        {
            var world = MakeWorld();
            var st = MakeState();
            GiveBuild(AddYard(world, "RingYard", "Cauldron", StationSite.Orbital),
                      stalledSeconds: TestSupport.Tuning().ShipyardWorkingStallSeconds + 1.0);

            RunUnservableSameBodyLeg(world, st, "Cauldron", StationSite.Orbital);

            Assert.Empty(world.Investments.SnapshotFor("F"));
        }

        [Fact]
        public void DispatchHaul_YardOnTheGroundBelow_DoesNotCoverTheWell()
        {
            // A landable body has both regions, and a hull built on its ground is not one an orbital
            // leg can dispatch. The coverage check must not read the wrong half of the body.
            var world = MakeWorld();
            var st = MakeState();
            GiveBuild(AddYard(world, "GroundYard", "Terra", StationSite.Surface), stalledSeconds: 0);

            List<string> log = RunUnservableSameBodyLeg(world, st, "Terra", StationSite.Orbital);

            Assert.Single(world.Investments.SnapshotFor("F"));
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST Shipyard planet=Terra site=Orbital yards=0/0"));
        }

        // ---- route-aware Ship demand on dispatch failure -------------------------------------

        [Fact]
        public void DispatchHaul_NoLocalStock_PostsShipDemandCarryingRealRouteAndSize()
        {
            // Tier-3 case (see the ship-acquisition fallback fix): the network's only ship is
            // consumed by a nearer, successfully-dispatching vector first (SourceA), leaving
            // SourceB's vector with nothing local or anywhere else in the network. Exercises the
            // fix's redirect target (the nearest real Shipyard, Shipyard1 -- never the failing
            // SourceB itself) alongside the pre-existing route/size-carrying behavior (Quantity x
            // VolumeLitersPerUnit, not a hardcoded flag).
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var sourceA = AddNode(world, "SourceA");
            sourceA.Position = new Vec3(1, 0, 0);
            sourceA.Inventory["Ore"] = 60;
            StockHaulShips(sourceA, 1);

            // Positioned beyond HaulConsolidationRadiusKm of SourceA: inside it, SourceA's convoy
            // would collect this stock on the way instead of leaving the vector stranded, and there
            // is no Tier-3 fallback left to assert on.
            var sourceB = AddNode(world, "SourceB");
            sourceB.Position = new Vec3(1000, 0, 0);
            sourceB.Inventory["Ore"] = 40;

            var shipyard = AddNode(world, "Shipyard1");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            var log = new List<string>();
            new OperationsManager(itemVolumes: volumes).Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), log);

            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Trade", shipDemand.Kind);
            Assert.Equal("Shipyard1", shipDemand.NodeId); // redirected to the nearest real Shipyard, not SourceB
            Assert.Equal("Dest", shipDemand.DestNodeId);
            // SourceB's own share of the shortfall (40 units x 3 L/unit = 120 L) x the 0.2
            // HaulShipDemandCargoShare: a hull is sized for the flow, not for one catch-up shipment.
            Assert.Equal(24, shipDemand.MinCargoLiters);
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND role=Trade node=Shipyard1 dest=Dest"));
        }

        [Fact]
        public void DispatchHaul_NoLocalStock_ShipDemandCarriesPickupSourceAsViaNode()
        {
            // Same tier-3 setup as the sibling test above, but asserting on ViaNodeId specifically:
            // the built hull's first real leg is Shipyard1 -> SourceB (fetch the stranded cargo),
            // not Shipyard1 -> Dest -- a route/hull-selection gate that only checks buildNode->dest
            // would be blind to this leg entirely, especially when buildNode and dest happen to
            // coincide (a separate, common case this field also fixes).
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var sourceA = AddNode(world, "SourceA");
            sourceA.Position = new Vec3(1, 0, 0);
            sourceA.Inventory["Ore"] = 60;
            StockHaulShips(sourceA, 1);

            // Positioned beyond HaulConsolidationRadiusKm of SourceA: inside it, SourceA's convoy
            // would collect this stock on the way instead of leaving the vector stranded, and there
            // is no Tier-3 fallback left to assert on.
            var sourceB = AddNode(world, "SourceB");
            sourceB.Position = new Vec3(1000, 0, 0);
            sourceB.Inventory["Ore"] = 40;

            var shipyard = AddNode(world, "Shipyard1");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            var log = new List<string>();
            new OperationsManager(itemVolumes: volumes).Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), log);

            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("SourceB", shipDemand.ViaNodeId);
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND role=Trade node=Shipyard1 dest=Dest via=SourceB"));
        }

        [Fact]
        public void DispatchHaul_MultipleBlockedDestinationsFromSameSource_PostsOneRepresentativeShipDemand()
        {
            // Two distinct blocked routes redirected to the same Shipyard must collapse to ONE
            // Ship:Trade demand, not one per destination -- ShipyardManager only ever maintains one
            // BuildQueueEntry per (node, role), so per-destination entries just pile up unused
            // (live-observed: dozens of coexisting demands at a single busy Shipyard node).
            var world = MakeWorld();
            var st = MakeState();

            // Two independent copies of the sibling test's known-working tier-3 setup (nearer source's
            // ship gets consumed by a successful dispatch first, leaving the farther source's vector
            // stranded with no ship anywhere), sharing the same single Shipyard as their redirect
            // target -- so both blocked vectors resolve to the same (faction, role, buildNode) group
            // with two different destinations.
            var dest1 = AddNode(world, "Dest1");
            dest1.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest1", 100);
            var sourceA1 = AddNode(world, "SourceA1");
            sourceA1.Position = new Vec3(1, 0, 0);
            sourceA1.Inventory["Ore"] = 60;
            StockHaulShips(sourceA1, 1);
            // Positioned beyond HaulConsolidationRadiusKm of SourceA: inside it, SourceA's convoy
            // would collect this stock on the way instead of leaving the vector stranded, and there
            // is no Tier-3 fallback left to assert on.
            var sourceB1 = AddNode(world, "SourceB1");
            sourceB1.Position = new Vec3(1000, 0, 0);
            sourceB1.Inventory["Ore"] = 40;

            var dest2 = AddNode(world, "Dest2");
            dest2.Position = new Vec3(0, 50, 0);
            PostItemDemand(world, "F", "Ore", "Dest2", 100);
            var sourceA2 = AddNode(world, "SourceA2");
            sourceA2.Position = new Vec3(1, 50, 0);
            sourceA2.Inventory["Ore"] = 60;
            StockHaulShips(sourceA2, 1);
            var sourceB2 = AddNode(world, "SourceB2");
            sourceB2.Position = new Vec3(1000, 50, 0);
            sourceB2.Inventory["Ore"] = 40;

            var shipyard = AddNode(world, "Shipyard1");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            var log = new List<string>();
            new OperationsManager(itemVolumes: volumes).Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), log);

            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Trade", shipDemand.Kind);
            Assert.Equal("Shipyard1", shipDemand.NodeId);
            Assert.True(shipDemand.DestNodeId == "Dest1" || shipDemand.DestNodeId == "Dest2");
        }

        [Fact]
        public void DispatchHaul_NoLocalStock_NoVolumeCatalogue_FallsBackToLegacyMinCargoFlag()
        {
            // Without volume data there's no way to compute a real liters target -- fall back to the
            // pre-existing boolean-flag value (1) rather than posting an unbounded/zero requirement.
            // Same tier-3 exhaustion setup as the sibling test above.
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var sourceA = AddNode(world, "SourceA");
            sourceA.Position = new Vec3(1, 0, 0);
            sourceA.Inventory["Ore"] = 60;
            StockHaulShips(sourceA, 1);

            var sourceB = AddNode(world, "SourceB");
            sourceB.Position = new Vec3(100, 0, 0);
            sourceB.Inventory["Ore"] = 40;

            var shipyard = AddNode(world, "Shipyard1");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Shipyard1", shipDemand.NodeId);
            Assert.Equal("Dest", shipDemand.DestNodeId);
            Assert.Equal(1, shipDemand.MinCargoLiters);
        }

        [Fact]
        public void DispatchHaul_EquivalentFallbacks_KeepTheLargestCargoRequirement()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var sourceA = AddNode(world, "SourceA");
            sourceA.Position = new Vec3(1, 0, 0);
            sourceA.Inventory["Ore"] = 60;
            StockHaulShips(sourceA, 1);

            // Positioned beyond HaulConsolidationRadiusKm of SourceA: inside it, SourceA's convoy
            // would collect this stock on the way instead of leaving the vector stranded, and there
            // is no Tier-3 fallback left to assert on.
            var sourceB = AddNode(world, "SourceB");
            sourceB.Position = new Vec3(1000, 0, 0);
            sourceB.Inventory["Ore"] = 30;
            var sourceC = AddNode(world, "SourceC");
            sourceC.Position = new Vec3(2000, 0, 0);
            sourceC.Inventory["Ore"] = 10;
            var shipyard = AddNode(world, "Shipyard");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            new OperationsManager(itemVolumes: volumes).Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), new List<string>());

            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Shipyard", shipDemand.NodeId);
            Assert.Equal("Dest", shipDemand.DestNodeId);
            Assert.Equal(18, shipDemand.MinCargoLiters); // 30 units x 3 L x 0.2 HaulShipDemandCargoShare
        }

        /// <summary>Stages one blocked Haul vector so the Tier-3 ship-build fallback posts its
        /// demand, against a two-hull Trade pool (1,000 L / 5,000 L) as the ceiling. Blocked holds
        /// <paramref name="blockedUnits"/> and no ship, so its vector's floor is that x 3 L/unit.
        /// <para>Ready is stocked with the network's only Haul ship and exactly enough ore to consume
        /// it: a faction with zero cargo ships ANYWHERE takes the bootstrap instant-transfer path
        /// instead (NetworkHasSuitable, once per faction per tick) and never reaches Tier 3, while a
        /// spare ship would let Tier 2 dispatch Blocked's cargo via a pickup leg.</para></summary>
        private static DemandEntry StageHaulFallbackDemand(long blockedUnits)
        {
            var world = MakeWorld();
            var catalogue = (FakePrefabCatalogue)world.PrefabCatalogue;
            catalogue.NamesByRole["Trade"] = new[] { "SmallHauler", "BigHauler" };
            catalogue.CargoLitersByPrefab["SmallHauler"] = 1_000;
            catalogue.CargoLitersByPrefab["BigHauler"] = 5_000;

            var st = MakeState();
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", blockedUnits + 60);

            var ready = AddNode(world, "Ready");
            ready.Position = new Vec3(1, 0, 0);
            ready.Inventory["Ore"] = 60;
            StockHaulShips(ready, 1);

            // Beyond HaulConsolidationRadiusKm of Ready, so Ready's convoy cannot simply collect
            // this stock on the way and leave nothing for Tier 3 to be asked about.
            var blocked = AddNode(world, "Blocked");
            blocked.Position = new Vec3(1000, 0, 0);
            blocked.Inventory["Ore"] = blockedUnits;

            var shipyard = AddNode(world, "Shipyard");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), new List<string>());
            return Assert.Single(world.Demand.SnapshotShipDemands("F"));
        }

        [Fact]
        public void Tick_HaulFallback_ShipmentLargerThanEveryHull_ClampsFloorToLargestTradeHull()
        {
            // 10,000 units x 3 L x the 0.2 share = 6,000 L, still above every hull in the pool.
            // Left unclamped the floor filters out every candidate, SelectPrefabBom falls through to
            // its own route-aware clamp, and the demand no longer describes a buildable size.
            DemandEntry shipDemand = StageHaulFallbackDemand(10_000);

            Assert.Equal("Shipyard", shipDemand.NodeId);
            Assert.Equal("Dest", shipDemand.DestNodeId);
            Assert.Equal(5_000, shipDemand.MinCargoLiters); // BigHauler, not the 30,000 L nothing can clear
        }

        [Fact]
        public void Tick_HaulFallback_ShipmentWithinPoolCapacity_LeavesFloorAtTheRealVolume()
        {
            // The clamp is a ceiling, not a flattening: below it the floor must still track the
            // shipment's own (share-scaled) volume, or hull size stops tracking shipping volume in
            // the middle of the range too -- which is what the clamp exists to preserve at the top.
            DemandEntry shipDemand = StageHaulFallbackDemand(500);

            Assert.Equal(300, shipDemand.MinCargoLiters); // 500 x 3 L x 0.2 share, under the ceiling and so unclamped
        }

        [Fact]
        public void Tick_ReusesRouteEnvironment_ForRepeatedIdenticalSourceDestinationRoutes()
        {
            var world = MakeWorld();
            var st = MakeState();
            var source = AddNode(world, "Source");
            source.Inventory["Ore"] = 10;
            source.Inventory["Ice"] = 10;
            StockHaulShips(source, 2);
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 10);
            PostItemDemand(world, "F", "Ice", "Dest", 10);

            var manager = new OperationsManager();
            manager.Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());

            Assert.Equal(1, manager.LastTelemetry.RouteCacheMisses);
            Assert.True(manager.LastTelemetry.RouteCacheHits >= 1);
            Assert.Equal(2, manager.LastTelemetry.DirectDispatches);
            Assert.True(manager.LastTelemetry.RoutePlanningTicks > 0);
        }

        [Fact]
        public void Tick_ReusesOneDeterministicSourceOrder_PerDestinationAcrossItems()
        {
            var world = MakeWorld();
            var st = MakeState();
            SpatialNode near = AddNode(world, "Near");
            SpatialNode far = AddNode(world, "Far");
            SpatialNode dest = AddNode(world, "Dest");
            near.Position = new Vec3(1, 0, 0);
            far.Position = new Vec3(10, 0, 0);
            dest.Position = new Vec3(0, 0, 0);
            near.Inventory["Ore"] = 100;
            near.Inventory["Ice"] = 100;
            far.Inventory["Ore"] = 100;
            far.Inventory["Ice"] = 100;
            StockHaulShips(near, 2);
            PostItemDemand(world, "F", "Ore", "Dest", 10);
            PostItemDemand(world, "F", "Ice", "Dest", 10);

            var manager = new OperationsManager();
            FactionTuning tuning = TestSupport.Tuning();
            tuning.HaulNeglectThreshold = TimeSpan.Zero;
            manager.Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            Assert.Equal(1, manager.LastTelemetry.DistanceOrderBuilds);
            Assert.Equal(2, manager.LastTelemetry.DirectDispatches);
            Assert.All(st.PendingFleets, fleet => Assert.Equal("Near", fleet.OriginNodeId));
        }

        [Fact]
        public void Tick_SingleItemSparseSources_DoesNotBuildFullOwnedNodeDistanceOrder()
        {
            var world = MakeWorld();
            var st = MakeState();
            SpatialNode near = AddNode(world, "Near");
            SpatialNode far = AddNode(world, "Far");
            SpatialNode dest = AddNode(world, "Dest");
            near.Position = new Vec3(1, 0, 0);
            far.Position = new Vec3(10, 0, 0);
            dest.Position = new Vec3(0, 0, 0);
            near.Inventory["Ore"] = 10;
            far.Inventory["Ore"] = 10;
            StockHaulShips(near, 1);
            for (int i = 0; i < 100; i++)
                AddNode(world, "Consumer" + i.ToString("D3"));
            PostItemDemand(world, "F", "Ore", "Dest", 10);

            var manager = new OperationsManager();
            FactionTuning tuning = TestSupport.Tuning();
            tuning.HaulNeglectThreshold = TimeSpan.Zero;
            manager.Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            Assert.Equal(0, manager.LastTelemetry.DistanceOrderBuilds);
            Assert.Equal(2, manager.LastTelemetry.CandidateSources);
            PendingFleetAction fleet = Assert.Single(st.PendingFleets);
            Assert.Equal("Near", fleet.OriginNodeId);
        }

        [Fact]
        public void Tick_ReusedDistanceOrder_ReevaluatesOutboundHotTierAcrossItems()
        {
            var world = MakeWorld();
            world.UtcNow = new DateTime(2035, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var st = MakeState();
            SpatialNode near = AddNode(world, "Near");
            SpatialNode far = AddNode(world, "Far");
            SpatialNode dest = AddNode(world, "Dest");
            near.Position = new Vec3(1, 0, 0);
            far.Position = new Vec3(10, 0, 0);
            dest.Position = new Vec3(0, 0, 0);
            near.Inventory["Ore"] = 10;
            near.Inventory["Ice"] = 10;
            far.Inventory["Ice"] = 10;
            near.LastHaulDepartedUtc = default(DateTime);
            far.LastHaulDepartedUtc = default(DateTime);
            StockHaulShips(near, 1, "NearHauler");
            StockHaulShips(far, 1, "FarHauler");
            PostItemDemand(world, "F", "Ore", "Dest", 10);
            PostItemDemand(world, "F", "Ice", "Dest", 10);

            var manager = new OperationsManager();
            FactionTuning tuning = TestSupport.Tuning();
            tuning.HaulNeglectThreshold = TimeSpan.FromMinutes(10);
            manager.Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            Assert.Equal(1, manager.LastTelemetry.DistanceOrderBuilds);
            Assert.Equal(2, st.PendingFleets.Count);
            Assert.Equal("Near", st.PendingFleets[0].OriginNodeId);
            Assert.Equal("Far", st.PendingFleets[1].OriginNodeId);
        }

        [Fact]
        public void Tick_MemoizesAlternateAndShipyardSearches_PerUnavailableSource()
        {
            var world = MakeWorld();
            var st = MakeState();
            var source = AddNode(world, "Source");
            source.Inventory["Ore"] = 10;
            source.Inventory["Ice"] = 10;
            var elsewhere = AddNode(world, "Elsewhere");
            StockHaulShips(elsewhere, 1);
            var shipyard = AddNode(world, "Shipyard");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 10);
            PostItemDemand(world, "F", "Ice", "Dest", 10);

            var manager = new OperationsManager();
            manager.Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());

            Assert.Equal(1, manager.LastTelemetry.AlternateSourceScans);
            Assert.Equal(1, manager.LastTelemetry.ShipyardScans);
            Assert.Equal(0, manager.LastTelemetry.DirectDispatches);
            Assert.Equal(1, manager.LastTelemetry.PickupDispatches);
            Assert.Equal(1, manager.LastTelemetry.FallbackCandidates);
            Assert.Equal(1, manager.LastTelemetry.UniqueFallbackKeys);
        }

        [Fact]
        public void Tick_SplitsAlternatePickupTelemetry_IntoScanAndRevalidateSpans()
        {
            var world = MakeWorld();
            var st = MakeState();
            var source = AddNode(world, "Source");
            source.Inventory["Ore"] = 10;
            source.Inventory["Ice"] = 10;
            var elsewhere = AddNode(world, "Elsewhere");
            StockHaulShips(elsewhere, 2);
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 10);
            PostItemDemand(world, "F", "Ice", "Dest", 10);

            var manager = new OperationsManager();
            manager.Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());

            // Two vectors share one unavailable source, so the ordered network scan runs once
            // (cached) while the candidate revalidation runs for both -- that asymmetry is the whole
            // reason the two spans are reported separately.
            Assert.Equal(1, manager.LastTelemetry.AlternateSourceScans);
            Assert.True(manager.LastTelemetry.AlternateScanTicks > 0);
            Assert.True(manager.LastTelemetry.AlternateRevalidateTicks > 0);
        }

        [Fact]
        public void Tick_AlternateSourceCache_AdvancesWhenCachedShipWasConsumed()
        {
            var world = MakeWorld();
            var st = MakeState();
            var source = AddNode(world, "Source");
            source.Inventory["Ore"] = 10;
            source.Inventory["Ice"] = 10;
            var first = AddNode(world, "First");
            first.Position = new Vec3(1, 0, 0);
            StockHaulShips(first, 1, "HaulerFirst");
            var second = AddNode(world, "Second");
            second.Position = new Vec3(2, 0, 0);
            StockHaulShips(second, 1, "HaulerSecond");
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ore", "Dest", 10);
            PostItemDemand(world, "F", "Ice", "Dest", 10);

            var manager = new OperationsManager();
            manager.Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());

            Assert.Equal(2, manager.LastTelemetry.PickupDispatches);
            Assert.Equal(0, manager.LastTelemetry.DirectDispatches);
            Assert.Equal(2, st.PendingFleets.Count);
            Assert.Empty(world.Demand.SnapshotShipDemands("F"));
        }

        [Fact]
        public void Tick_ConsolidatesEquivalentFallbackPosts_AndPublishesCompleteTelemetry()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Dest");
            PostItemDemand(world, "F", "A", "Dest", 1);
            PostItemDemand(world, "F", "Ore", "Dest", 30);
            PostItemDemand(world, "F", "Ice", "Dest", 10);

            var ready = AddNode(world, "Ready");
            ready.Inventory["A"] = 1;
            StockHaulShips(ready, 1);
            var sourceOre = AddNode(world, "SourceOre");
            sourceOre.Inventory["Ore"] = 30;
            var sourceIce = AddNode(world, "SourceIce");
            sourceIce.Inventory["Ice"] = 10;
            var shipyard = AddNode(world, "Shipyard");
            shipyard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var manager = new OperationsManager(new FakeVolumeCatalogue().Set("Ore", 3).Set("Ice", 3));
            manager.Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());

            DemandEntry demand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal(18, demand.MinCargoLiters); // Ore's 30 units x 3 L x 0.2 share beats Ice's 10 x 3 x 0.2
            // ViaNodeId travels with whichever post's cargo requirement wins (Ore's 90 > Ice's 30) --
            // must not be lost or arbitrarily tie-broken now that ViaNodeId differs per vector.
            Assert.Equal("SourceOre", demand.ViaNodeId);
            Assert.Equal(2, manager.LastTelemetry.FallbackCandidates);
            Assert.Equal(1, manager.LastTelemetry.UniqueFallbackKeys);
            Assert.Equal(1, manager.LastTelemetry.DemandPosts);
            Assert.Equal(0, manager.LastTelemetry.DemandRefreshes);
            Assert.True(manager.LastTelemetry.FallbackPublicationTicks > 0);
            Assert.True(manager.LastTelemetry.PreparationTicks > 0);
            Assert.True(manager.LastTelemetry.TargetMatchingTicks > 0);
            Assert.True(manager.LastTelemetry.DirectDispatchTicks > 0);
            Assert.True(manager.LastTelemetry.AlternatePickupTicks > 0);
            Assert.True(manager.LastTelemetry.ShipyardFallbackTicks > 0);
        }

        [Fact]
        public void OpsTickPerf_DetailedCountersRemainFactionLocal_InMultiFactionTick()
        {
            var world = MakeWorld();
            var first = MakeState("A");
            var second = MakeState("B");
            var firstSource = AddNode(world, "ASource", "A");
            AddNode(world, "ADest", "A");
            var secondSource = AddNode(world, "BSource", "B");
            AddNode(world, "BDest", "B");
            StockHaulShips(firstSource, 1000, "AHauler");
            StockHaulShips(secondSource, 1000, "BHauler");
            for (int i = 0; i < 1000; i++)
            {
                string aItem = "AItem" + i;
                string bItem = "BItem" + i;
                firstSource.Inventory[aItem] = 10;
                secondSource.Inventory[bItem] = 10;
                PostItemDemand(world, "A", aItem, "ADest", 10);
                PostItemDemand(world, "B", bItem, "BDest", 10);
            }

            var log = new List<string>();
            // Every tick reports: the subject is each line's counters, not whether a tick was slow.
            new OperationsManager { PerfLogThresholdMs = -1 }.Tick(new List<FactionState> { first, second }, world, TestSupport.Tuning(), log);

            List<string> perf = log.Where(line => line.Contains("OPS_TICK_PERF")).ToList();
            Assert.Equal(2, perf.Count);
            Assert.All(perf, line => Assert.Contains("routeMisses=1", line));
        }

        // ---- Slice F: DemandHistory (docs/material-vectors.md's DemandHistory addendum) -----

        [Fact]
        public void ZeroDemandAnywhere_DemandHistoryNeverAccumulates_WantTiedConstraintHolds()
        {
            // DemandHistory is want-tied by construction: an entry only ever exists because a
            // matched MaterialVector (itself born from a live Item: demand) credited it. "Ingot:Iron"
            // has real surplus at Source but zero demand posted anywhere for it -- it must never
            // accumulate history, be dispatched, or move, regardless of the unrelated demand below
            // (posted only so Tick doesn't early-continue on an empty demand snapshot).
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Ingot:Iron"] = 100;
            source.Inventory["Unrelated"] = 10;
            StockHaulShips(source, 2);

            var other = AddNode(world, "Other");
            PostItemDemand(world, "F", "Unrelated", "Other", 5);

            Tick(world, st);

            Assert.DoesNotContain(st.PendingFleets, f => f.Cargo != null && f.Cargo.ContainsKey("Ingot:Iron"));
            Assert.Equal(100, world.GetNodeInventory("Source", "Ingot:Iron"));
            Assert.False(world.Graph.Nodes["Source"].DemandHistory.ContainsKey("Ingot:Iron"));
        }

        [Fact]
        public void DemandHistory_DecaysAcrossManagerTicksWithNoNewDelivery()
        {
            var world = MakeWorld();
            var st = MakeState();

            var node = AddNode(world, "Node");
            // Keeps Tick from early-continuing; unrelated to the item under test.
            var other = AddNode(world, "Other");
            PostItemDemand(world, "F", "Unrelated", "Other", 5);

            node.DemandHistory["Ingot:Iron"] = 100.0;

            FactionTuning tuning = TestSupport.Tuning();
            double alpha = 2.0 / (tuning.DemandHistoryEmaPeriods + 1.0);
            double expected = 100.0;

            double last = 100.0;
            for (int i = 0; i < 5; i++)
            {
                var log = new List<string>();
                new OperationsManager().Tick(new List<FactionState> { st }, world, tuning, log);

                expected = (1.0 - alpha) * expected; // no credit this pass -- pure decay
                double current;
                node.DemandHistory.TryGetValue("Ingot:Iron", out current);
                Assert.True(current < last, "DemandHistory should strictly decrease with no new delivery");
                Assert.Equal(expected, current, 6);
                last = current;
            }
        }

        [Fact]
        public void DemandHistory_AmplifiesEqualShortfallDestination_OverHistoricallyQuietSibling()
        {
            // Two equal-shortfall destinations competing for the same scarce surplus (only enough
            // for one of them) -- with shortfall tied, DemandHistory's gravity term should decide
            // who gets processed (and served) first.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["X"] = 10; // enough for exactly one destination's shortfall, not both
            StockHaulShips(source, 2);

            var fed = AddNode(world, "Fed");
            var quiet = AddNode(world, "Quiet");
            PostItemDemand(world, "F", "X", "Fed", 10);
            PostItemDemand(world, "F", "X", "Quiet", 10);
            fed.DemandHistory["X"] = 50.0;

            Tick(world, st);

            Assert.True(HaulsTo(st, "Fed").Any());
            Assert.False(HaulsTo(st, "Quiet").Any());
        }

        [Fact]
        public void OutboundNeglect_LetsFartherSourceWinOverNearerRecentlyActiveOne()
        {
            // Mirrors NearestSourceIsPreferredOverFartherSourceWithEnoughSurplusEitherWay above, but
            // Near just dispatched a Haul (not hot) while Far never has (hot, default MinValue) --
            // the outbound neglect override should let Far win despite being much farther away.
            var world = MakeWorld();
            var st = MakeState();

            var need = AddNode(world, "Need");
            need.Position = new Vec3(0, 0, 0);

            var near = AddNode(world, "Near");
            near.Position = new Vec3(1, 0, 0);
            near.Inventory["Explosives"] = 50;
            StockHaulShips(near, 1);
            near.LastHaulDepartedUtc = world.UtcNow; // just shipped -- not hot

            var far = AddNode(world, "Far");
            far.Position = new Vec3(100, 0, 0);
            far.Inventory["Explosives"] = 50;
            StockHaulShips(far, 1);
            // far.LastHaulDepartedUtc left at default -- hot

            PostItemDemand(world, "F", "Explosives", "Need", 20);

            Tick(world, st);

            PendingFleetAction fleet = HaulTo(st, "Need");
            Assert.Equal("Far", fleet.OriginNodeId);
            Assert.Equal(50, world.GetNodeInventory("Near", "Explosives")); // untouched
        }

        [Fact]
        public void InboundNeglect_LetsOverdueLargeShortfallDestinationWinScarceSupplyOverSmallerQuietSibling()
        {
            // Scarce shared surplus: Big's shortfall (500) is far larger than Small's (2), so under
            // plain smallest-shortfall-first Small would be served first. But Big has never been
            // visited (hot) while Small was just serviced (not hot) -- the inbound neglect override
            // should let Big jump the queue and take the scarce 2 units, leaving Small unserved.
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["X"] = 2;
            StockHaulShips(source, 2);

            var big = AddNode(world, "Big");
            PostItemDemand(world, "F", "X", "Big", 500); // big.LastHaulArrivedUtc default -- hot

            var small = AddNode(world, "Small");
            small.LastHaulArrivedUtc = world.UtcNow; // just serviced -- not hot
            PostItemDemand(world, "F", "X", "Small", 2);

            Tick(world, st);

            Assert.True(HaulsTo(st, "Big").Any());
            Assert.False(HaulsTo(st, "Small").Any());
            Assert.Equal(2, HaulTo(st, "Big").Cargo["X"]);
        }

        [Fact]
        public void HaulMaxBatchFraction_CapsVectorSizeBelowFullShortfall_WhenConfigured()
        {
            var world = MakeWorld();
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["X"] = 1000;
            StockHaulShips(source, 2);

            var dest = AddNode(world, "Dest");
            PostItemDemand(world, "F", "X", "Dest", 100);

            FactionTuning tuning = TestSupport.Tuning();
            tuning.HaulMaxBatchFraction = 0.25;

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, tuning, log);

            Assert.Equal(25, HaulTo(st, "Dest").Cargo["X"]);
        }

        // ---- ship-acquisition fallback: ferry an existing ship in, don't ask to build a dead-end one ----

        [Fact]
        public void DispatchHaul_NoLocalStock_ShipExistsElsewhere_DispatchesOnePickupChainedHaul()
        {
            // The N1853 case directly: a producer with real surplus and no Shipyard, matched as a
            // Haul source, has no local ship -- but the network isn't ship-less, so the correct
            // response is one Haul convoy that fetches a ship from Elsewhere then delivers, not a
            // Position:Trade demand (FleetManager Ferry) and never a Ship:Trade build demand, which
            // would be a permanently dead-end demand at a Shipyard-less node.
            var world = MakeWorld();
            StockShipElsewhere(world);
            var st = MakeState();

            var source = AddNode(world, "Source"); // no Shipyard capability, no ship stock
            source.Inventory["Ingot:Iron"] = 100;

            var dest = AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ingot:Iron", "Dest", 50);

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            Assert.Empty(world.Demand.SnapshotShipDemands("F"));
            Assert.Empty(world.Investments.SnapshotFor("F"));
            Assert.Empty(world.Demand.SnapshotPositioningDemands("F")); // no Position: demand posted

            PendingFleetAction fleet = Assert.Single(st.PendingFleets);
            Assert.Equal(ActionKind.Haul, fleet.Kind);
            Assert.Equal("Elsewhere", fleet.OriginNodeId);
            Assert.Equal("Source", fleet.TargetNodeId); // currently on its pickup leg, not the real destination yet
            Assert.Equal(FleetStopKind.Pickup, fleet.CurrentStopKind);
            Assert.Equal("Ingot:Iron", fleet.PickupItemKey);
            Assert.Equal(50, fleet.PickupQuantity);
            FleetStop delivery = Assert.Single(fleet.RemainingStops);
            Assert.Equal("Dest", delivery.NodeId);
            Assert.Equal(FleetStopKind.Service, delivery.Kind);
            Assert.Contains(log, l => l.Contains("HAUL_DISPATCHED") && l.Contains("via_pickup=Elsewhere"));

            Assert.Equal(100, world.GetNodeInventory("Source", "Ingot:Iron")); // untouched at dispatch — loads at the pickup stop, not before
        }

        [Fact]
        public void DispatchHaul_ViaPickup_ResolvesToRealDeliveryAcrossBothLegs()
        {
            // Integration across dispatch (OperationsManager) and resolution (FleetResolution):
            // the pickup-chained Haul this session's fix produces actually loads and delivers, not
            // just dispatches with the right shape.
            var world = MakeWorld();
            StockShipElsewhere(world);
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Ingot:Iron"] = 100;
            var dest = AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ingot:Iron", "Dest", 50);

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            var states = new List<FactionState> { st };
            // Leg 1: pickup at Source — loads cargo, re-departs toward Dest.
            PendingFleetResolver.ResolveArrivals(states, world, TestSupport.Tuning(), new SeededRandom(1), log);
            Assert.Equal(50, world.GetNodeInventory("Source", "Ingot:Iron")); // 100 - 50 loaded
            PendingFleetAction inTransit = Assert.Single(st.PendingFleets);
            Assert.Equal(FleetStopKind.Service, inTransit.CurrentStopKind);
            Assert.Equal("Dest", inTransit.TargetNodeId);

            // Leg 2: delivery at Dest — credits the destination, convoy has no more stops.
            PendingFleetResolver.ResolveArrivals(states, world, TestSupport.Tuning(), new SeededRandom(1), log);
            Assert.Equal(50, world.GetNodeInventory("Dest", "Ingot:Iron"));
        }

        [Fact]
        public void DispatchHaul_ViaPickup_InFlightPairIsClaimed_NoDuplicateDispatchNextTick()
        {
            // Regression for the dedup gap found while implementing this: inFlightHaulPairs used to
            // key only on (TargetNodeId, Cargo.Keys), which is invisible for a Haul still on its
            // Pickup leg (TargetNodeId is the pickup node, Cargo is null) -- so a second tick's
            // MatchVectors would re-dispatch a duplicate Haul for the same (Dest, item) pair before
            // the first one even reached its pickup stop.
            var world = MakeWorld();
            StockShipElsewhere(world);
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Ingot:Iron"] = 100;
            var dest = AddNode(world, "Dest");
            PostItemDemand(world, "F", "Ingot:Iron", "Dest", 50);

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);
            Assert.Single(st.PendingFleets); // first tick dispatches the pickup-chained Haul

            // Demand is still outstanding (nothing delivered yet — the convoy hasn't reached Dest).
            // A second tick must not dispatch a second convoy for the same (Dest, Ingot:Iron) pair
            // while the first is still on its pickup leg.
            PostItemDemand(world, "F", "Ingot:Iron", "Dest", 50);
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            Assert.Single(st.PendingFleets); // still just the one convoy, no duplicate
        }

        // ---- Mine dispatch tests -----------------------------------------------------------

        // An ore field is ground that holds ore, so the deposit is what makes this node one -- the Kind
        // label is derived from it rather than the other way round. Before FAC-102 the label alone made
        // a node mineable and this helper set no deposit at all.
        private static SpatialNode AddOreFieldNode(WorldModel world, string id, Vec3 pos, string tag = "F")
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            n.Richness        = 1.0;   // ore under it AND measured: the mint and the drill read this
            n.Deposit         = GroundHolding("Iron");
            return n;
        }

        /// <summary>A deposit holding the named ores in equal share -- what a node needs to be
        /// mineable now that the kind label does not confer it.</summary>
        private static NodeDeposit GroundHolding(params string[] ores)
        {
            var deposit = new NodeDeposit();
            foreach (string ore in ores)
                deposit.Entries.Add(new DepositEntry(ore, 1.0 / ores.Length, 100.0 / ores.Length));
            return deposit;
        }

        private static void StockMineShip(SpatialNode node, int count = 1, string prefabId = "Miner")
        {
            node.ShipStock[prefabId] = count;
        }

        // TargetNodeId is now always the SOURCE node being dwelled at (see FleetStopKind.Mine) --
        // the real destination only ever appears as a queued Service stop in RemainingStops.
        private static PendingFleetAction MineFleetToDest(FactionState st, string destNodeId)
            => st.PendingFleets.Single(f => f.Kind == ActionKind.Mine && f.RemainingStops != null
                && f.RemainingStops.Any(s => s.Kind == FleetStopKind.Service && s.NodeId == destNodeId));

        /// <summary>Makes <paramref name="nodeId"/> an NPC hub drawing <paramref name="oreKey"/>
        /// (<see cref="SpatialNode.DemandHistory"/>, litres per Operations pass) -- by default far
        /// faster than any deposit's passive mint, so the extraction planner opens an open-ended
        /// order there. Since FAC-395 slice 2b that is the only automatic Mine trigger: an ore demand
        /// row no longer dispatches a trip by itself.</summary>
        private static void ConsumeOre(WorldModel world, string nodeId, string oreKey, double litersPerPass = 1e9)
        {
            world.Graph.Nodes[nodeId].DemandHistory[oreKey] = litersPerPass;
        }

        /// <summary>Every open extraction order is walked, neediest first -- not one winner per pass.
        /// BigNeed's target rate is met by one trip, so the second seam is left for SmallNeed; with
        /// the rate cap removed BigNeed would take both.</summary>
        [Fact]
        public void Mine_EveryOpenExtractionOrderIsWalked_NeediestFirst()
        {
            var world = MakeWorld();
            var st = MakeState();

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore, count: 2);
            AddOreFieldNode(world, "OreField2", new Vec3(0, 0, 0));   // a deposit takes one trip at a time

            AddNode(world, "BigNeed");
            AddNode(world, "SmallNeed");
            ConsumeOre(world, "BigNeed", "Ore:Iron", 200_000);         // 4 ML/h: one trip's rate covers it
            ConsumeOre(world, "SmallNeed", "Ore:Iron", 1_000);

            List<string> log = Tick(world, st);

            Assert.Equal(2, st.PendingFleets.Count(f => f.Kind == ActionKind.Mine));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCHED") && l.Contains("order=BigNeed:Ore:Iron"));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCHED") && l.Contains("order=SmallNeed:Ore:Iron"));
            Assert.NotNull(MineFleetToDest(st, "BigNeed"));
            Assert.NotNull(MineFleetToDest(st, "SmallNeed"));
        }

        // Ore:Stone is the MineOnly yield entry: the passive income pass skips it, so a Mine run is
        // its only source. Nothing in the dispatch chain special-cases it -- it classifies as
        // ItemKind.Ore and rides the ordinary shortfall path all the way to a fleet.
        [Fact]
        public void Mine_FiresForStoneOre_TheMineOnlyYieldEntry()
        {
            var world = MakeWorld();
            var st = MakeState();

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);

            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Stone");

            Tick(world, st);

            PendingFleetAction mine = MineFleetToDest(st, "Need");
            Assert.Equal("OreField1", mine.TargetNodeId);
            Assert.Contains("Ore:Stone", mine.MineWeights.Keys);
        }

        // ---- FAC-056: the source is the richest in range, not merely the nearest ---------------

        private static SpatialNode AddMineNode(WorldModel world, string id, Vec3 pos, double resourceValue = 1.0, string tag = "F", params string[] groundOres)
        {
            SpatialNode n = world.Graph.Add(pos, resourceValue, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            // One quality dial for the fixture, applied to both readings: since FAC-219 Mine ranks
            // and paces on Richness while founding/claim scoring still reads ResourceValue, and a
            // test saying "this node is half as rich" means it of the ground either way.
            n.Richness        = resourceValue;
            n.Deposit = groundOres != null && groundOres.Length > 0
                ? GroundHolding(groundOres)
                : GroundHolding("Iron");
            StockMineShip(n);
            return n;
        }

        private static string MineSourceFor(FactionState st, string destNodeId)
            => MineFleetToDest(st, destNodeId).TargetNodeId;

        [Fact]
        public void Mine_RareOreDemand_TakesTheFarNodeWhoseGroundHasIt()
        {
            var world = MakeWorld();
            var st = MakeState();

            AddNodeAt(world, "Need", new Vec3(0, 0, 0));
            AddMineNode(world, "NearIron", new Vec3(10, 0, 0),     1.0, "F", "Iron");
            AddMineNode(world, "FarGold",  new Vec3(200, 0, 0), 1.0, "F", "Gold");
            ConsumeOre(world, "Need", "Ore:Gold");

            Tick(world, st);

            Assert.Equal("FarGold", MineSourceFor(st, "Need"));
        }

        [Fact]
        public void Mine_CommonOreDemand_TakesTheFarNodeWhoseGroundHasIt()
        {
            // Supersedes FAC-056's cross-kind scale preference. The near node is ineligible because
            // its ground holds no iron, not because of the label it wears -- a stronger answer, since
            // the old 8x gap still dispatched to a field that had none.
            var world = MakeWorld();
            var st = MakeState();

            AddNodeAt(world, "Need", new Vec3(0, 0, 0));
            AddMineNode(world, "NearGold", new Vec3(10, 0, 0), 1.0, "F", "Gold");
            AddMineNode(world, "FarIron",  new Vec3(200, 0, 0),     1.0, "F", "Iron");
            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st);

            Assert.Equal("FarIron", MineSourceFor(st, "Need"));
        }

        [Fact]
        public void Mine_EqualRichness_StillTakesTheNearest()
        {
            var world = MakeWorld();
            var st = MakeState();

            AddNodeAt(world, "Need", new Vec3(0, 0, 0));
            AddMineNode(world, "NearOre", new Vec3(10, 0, 0));
            AddMineNode(world, "FarOre",  new Vec3(200, 0, 0));
            ConsumeOre(world, "Need", "Ore:Iron", 100_000);   // above the passive mint, below one trip's rate

            Tick(world, st);

            // The catchment walk ranks richest first and nearest among equal scores, so when nothing
            // separates the deposits the one trip the order needs goes to the nearer.
            Assert.Equal("NearOre", MineSourceFor(st, "Need"));
        }


        [Fact]
        public void Mine_RicherDeposit_BeatsTheNearerNode()
        {
            var world = MakeWorld();
            var st = MakeState();

            AddNodeAt(world, "Need", new Vec3(0, 0, 0));
            AddMineNode(world, "NearOre", new Vec3(10, 0, 0), resourceValue: 0.50);
            AddMineNode(world, "FarOre",  new Vec3(200, 0, 0), resourceValue: 1.00);
            ConsumeOre(world, "Need", "Ore:Iron", 100_000);   // above the passive mint, below one trip's rate

            Tick(world, st);

            // The control for the test above: the richer deposit leads the walk, whatever the
            // distance, so the one trip goes there.
            Assert.Equal("FarOre", MineSourceFor(st, "Need"));
        }

        [Fact]
        public void Mine_IceFieldIsNotAnIronSource_DespiteItsOreScaleMatchingAnOreField()
        {
            var world = MakeWorld();
            var st = MakeState();

            AddNodeAt(world, "Need", new Vec3(0, 0, 0));
            AddMineNode(world, "Ice", new Vec3(10, 0, 0), 1.0, "F", "Ice");
            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st);

            // IceField's Ore scale is 2.0, matching an OreField's, but it yields only Ore:Ice.
            // An iron order's catchment is the deposits that score for iron, the same
            // SelectionScore the dispatch uses -- so no fleet, rather than one guaranteed to
            // block on drill_rate_zero at the deposit.
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void Mine_DoesNotFireForNonOreItems()
        {
            var world = MakeWorld();
            var st = MakeState();

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);

            var need = AddNode(world, "Need");
            // A component demand has no owned-node surplus either, but it's not raw Ore/RareOre --
            // Mine must never treat it as its own scope (no recipe exists for Mine to help with here).
            PostItemDemand(world, "F", "Comp:SteelPlate", "Need", 50);

            Tick(world, st);

            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void Mine_DoesNotFireWhenHaulAlreadyCoversTheShortfall()
        {
            var world = MakeWorld();
            var st = MakeState();

            var haulSource = AddNode(world, "HaulSource");
            haulSource.Inventory["Ore:Iron"] = 1000;
            StockHaulShips(haulSource, 1);

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);

            var need = AddNode(world, "Need");
            PostItemDemand(world, "F", "Ore:Iron", "Need", 50);

            Tick(world, st, TuningWithoutLoadGate());

            Assert.Single(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void Mine_CooldownGates_SameNodeNotReMinedWithinCooldownWindow()
        {
            var world = MakeWorld();
            var st = MakeState();

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore, count: 2); // don't let a ship shortage mask the cooldown gate

            var need = AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron"); // an open-ended order across both ticks

            Tick(world, st);
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));

            // Cooldown is written at dwell resolution, not at dispatch time -- advance past the
            // dwell and resolve it so OreField1's MineCooldownUntilUtc is actually set.
            PendingFleetAction firstMine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            world.UtcNow = firstMine.EstimatedArrivalUtc.AddSeconds(1);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, TestSupport.Tuning(), new SeededRandom(1), new List<string>());

            // OreField1 is the only eligible source in the world, and it was just mined, so a
            // second attempt must skip it while its deposit is on cooldown.
            ConsumeOre(world, "Need", "Ore:Iron");
            Tick(world, st);

            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine)); // the resolved fleet stays in flight toward its Service leg; no new one replaced it
        }

        [Fact]
        public void Mine_NoShipAtSource_DispatchesViaPickupFromAnotherOwnedNode()
        {
            var world = MakeWorld();
            var st = MakeState();

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0)); // no ship here
            var elsewhere = AddNode(world, "Elsewhere");
            StockMineShip(elsewhere);

            var need = AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st);

            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            Assert.Equal(FleetStopKind.Navigation, mine.CurrentStopKind); // travels to the source first, then dwells
            Assert.Equal("Elsewhere", mine.OriginNodeId);
            Assert.Equal("OreField1", mine.TargetNodeId); // first leg: fetch the ship to the OreField
            Assert.Equal(2, mine.RemainingStops.Count);
            Assert.Equal(FleetStopKind.Mine, mine.RemainingStops[0].Kind);
            Assert.Equal("OreField1", mine.RemainingStops[0].NodeId);
            Assert.Equal(FleetStopKind.Service, mine.RemainingStops[1].Kind);
            Assert.Equal("Need", mine.RemainingStops[1].NodeId);
        }

        [Fact]
        public void Mine_NoShipAnywhere_FliesNoFleetAndStagesAShipDemand()
        {
            var world = MakeWorld();
            var st = MakeState();

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0)); // no ship anywhere in the network
            var need = AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st);

            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            // The DEPOSIT is never minted into: a dwell mint needs a real drill ship, and there is
            // none anywhere in the network. What the destination now receives instead is the
            // ownership-gated bootstrap mint -- see the MineBootstrap_* tests below, which own that
            // half of what this test used to assert.
            Assert.Equal(0, world.GetNodeInventory("OreField1", "Ore:Iron"));
            // Mine won the comparison (no real Haul surplus anywhere) but has no drill-suitable ship
            // anywhere in the network -- unlike Phase 1, Mine can no longer assume Haul's own
            // fallback covers this gap (distinct ship class), so it posts its own Ship:Mine demand.
            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Mine", shipDemand.Kind);
        }

        /// <summary>The other half of the split above, and the case where "dispatches nothing and
        /// mints nothing" is still the whole truth: the faction OWNS a drill, it is merely not
        /// available -- so no fleet flies, and no bootstrap mint stands in for one either.</summary>
        [Fact]
        public void Mine_OwnedDrillUnavailable_DispatchesNothingAndMintsNothing()
        {
            var world = MakeWorld();
            var st = MakeState();

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);
            ore.ListedForSale["Miner"] = 1;   // reserved for sale: unavailable to dispatch, still owned

            var need = AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> log = Tick(world, st);

            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            Assert.Equal(0, world.GetNodeInventory("OreField1", "Ore:Iron"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Ore:Iron"));
            // The block itself still happened -- otherwise "nothing was minted" would only prove
            // the dispatch path was never reached.
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("no_ship_anywhere"));
            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Mine", shipDemand.Kind);
        }

        // ---- FAC-204: the bootstrap mint for a faction that owns no drill anywhere ------------
        //
        // The measured deadlock (live, 2026-09-06): four factions owned no hull of any role, each
        // had a rank-6 Mine build queued at a shipyard holding single-digit units of iron, and the
        // only way to get iron out of the ground is a Mine dispatch, which needs the very drill the
        // iron would build. Haul's own bootstrap (ITEM_XFER_BOOTSTRAP) has always had this escape
        // hatch; these tests are Mine's.

        /// <summary>Liters one bootstrap mint delivers under a given tuning -- the number the mint's
        /// own arithmetic is derived from, restated here so a test asserts against tuning rather
        /// than against a copy of the implementation's constant.</summary>
        private static double BootstrapCapLiters(FactionTuning tuning)
            => tuning.FreeMinerCargoVolumeLiters * tuning.MineBootstrapYieldFraction;

        /// <summary>An ore demand far larger than one bootstrap load, so the mint is bounded by the
        /// free-miner hold and not by the request -- which is what makes the minted quantity a
        /// prediction from tuning instead of an echo of the demand.</summary>
        private const long OverwhelmingIronDemand = 100_000;

        [Fact]
        public void MineBootstrap_FiresWhenTheFactionOwnsNoDrillAnywhere()
        {
            var world = MakeWorld();
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));   // no hull of any kind, anywhere
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            // The hold, not the request, has to be the binding constraint or the expected quantity
            // below would be true for the wrong reason.
            Assert.True(OverwhelmingIronDemand * 1.0 > BootstrapCapLiters(tuning),
                "the demand must exceed one bootstrap load for the hold cap to be what binds");

            DateTime now = world.UtcNow;
            List<string> log = Tick(world, st, tuning);

            // MineTestVolumes prices Ore:Iron at 1 litre per unit, and the basket is all iron, so
            // the whole capped volume converts at weight 1.0.
            long expected = (long)BootstrapCapLiters(tuning);
            Assert.True(expected > 0, "tuning must actually fund a mint for this test to mean anything");
            Assert.Equal(expected, world.GetNodeInventory("Need", "Ore:Iron"));
            Assert.Contains(log, l => l.Contains("MINE_BOOTSTRAP")
                && l.Contains("src=OreField1 dest=Need Ore:Ironx" + expected)
                && l.Contains("cooldown=1800s"));

            // Accounting-only, at the DESTINATION: the deposit is not a warehouse and nothing was
            // taken out of it.
            Assert.Equal(0, world.GetNodeInventory("OreField1", "Ore:Iron"));
            // No hull flew, and none was pretended into existence.
            Assert.Empty(st.PendingFleets);

            // The source is rate-limited by a real cooldown -- this is the only thing stopping one
            // seam being tapped every ops cycle.
            Assert.Equal(now.AddSeconds(tuning.MineBootstrapCooldownSeconds),
                world.Graph.Nodes["OreField1"].MineCooldownUntilUtc);
            Assert.Equal(now, world.Graph.Nodes["OreField1"].LastMinedUtc);

            // The hull is still missing and the block is still the truth about this cycle; the
            // bootstrap is what is being done about it, not a claim that it was solved.
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("no_ship_anywhere"));
            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Mine", shipDemand.Kind);
        }

        [Fact]
        public void MineBootstrap_DoesNotFireWhileTheFactionsOnlyDrillIsAwayOnAnErrand()
        {
            // FAC-199's argument, on Mine's side of it. ShipStock holds only PARKED hulls, so a
            // one-drill faction reads as drill-less for the whole duration of every errand that
            // drill flies. On the availability reading the mint would make mining cheaper the fewer
            // drills you own -- and would fire forever for the very faction that already has one.
            var world = MakeWorld();
            var st = MakeState();

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));   // nothing parked anywhere
            AddNode(world, "Need");
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind         = ActionKind.Mine,
                ActorTag     = "F",
                ShipPrefabId = "Miner",   // owned, consumed out of ShipStock at dispatch, coming back
                ConvoySize   = 1,
                OriginNodeId = "OreField1",
                TargetNodeId = "OreField1",
            });
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> log = Tick(world, st);

            Assert.DoesNotContain(log, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Ore:Iron"));
            Assert.Equal(default(DateTime), world.Graph.Nodes["OreField1"].MineCooldownUntilUtc);
            // Reached the decision it declined, rather than falling out earlier for some other reason.
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("no_ship_anywhere"));
        }

        [Fact]
        public void MineBootstrap_DoesNotFireWhileTheFactionsOnlyDrillSitsInAStoreListing()
        {
            // Same defect, second door -- the mirror of the Haul bootstrap's own store-listing test.
            // Availability subtracts ListedForSale on top of the parked/away split, so listing the
            // last drill would otherwise buy the faction free ore.
            var world = MakeWorld();
            var st = MakeState();

            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);
            ore.ListedForSale["Miner"] = 1;

            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> log = Tick(world, st);

            Assert.DoesNotContain(log, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Ore:Iron"));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("no_ship_anywhere"));
        }

        [Fact]
        public void MineBootstrap_StopsOnceTheFactionOwnsAnyDrill()
        {
            // Self-terminating by construction: no flag, no expiry, no counter -- the first
            // Mine-suitable hull the faction owns flips the ownership gate and the mint is over.
            var world = MakeWorld();
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            AddNodeAt(world, "Need", new Vec3(0, 0, 0));
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> first = Tick(world, st, tuning);
            Assert.Contains(first, l => l.Contains("MINE_BOOTSTRAP"));
            long afterFirst = world.GetNodeInventory("Need", "Ore:Iron");

            // Past the cooldown the first mint stamped, so the second cycle is refused by ownership
            // and not merely by a source that is still resting.
            world.UtcNow = world.UtcNow.AddSeconds(tuning.MineBootstrapCooldownSeconds + 1);
            Assert.True(world.UtcNow > world.Graph.Nodes["OreField1"].MineCooldownUntilUtc,
                "the source must be off cooldown or this proves nothing about the ownership gate");
            StockMineShip(AddNodeAt(world, "Elsewhere", new Vec3(1, 0, 0)));
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> second = Tick(world, st, tuning);

            Assert.DoesNotContain(second, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(afterFirst, world.GetNodeInventory("Need", "Ore:Iron"));
            // And it stopped because a real trip took over, not because Mine gave up entirely.
            Assert.Contains(st.PendingFleets, f => f.Kind == ActionKind.Mine);
        }

        [Fact]
        public void MineBootstrap_RespectsTheCooldownItStamps()
        {
            var world = MakeWorld();
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st, tuning);
            long afterFirst = world.GetNodeInventory("Need", "Ore:Iron");
            Assert.True(afterFirst > 0);
            DateTime coolsAt = world.Graph.Nodes["OreField1"].MineCooldownUntilUtc;

            // Inside the window: the same demand is still unserved and the same source is still the
            // only one in the world, so nothing but the cooldown can be refusing this.
            world.UtcNow = world.UtcNow.AddSeconds(tuning.MineBootstrapCooldownSeconds / 2);
            Assert.True(world.UtcNow < coolsAt, "the second cycle must land inside the cooldown window");
            ConsumeOre(world, "Need", "Ore:Iron");
            List<string> inside = Tick(world, st, tuning);

            Assert.DoesNotContain(inside, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(afterFirst, world.GetNodeInventory("Need", "Ore:Iron"));

            // Past it: the seam is workable again, which is what makes the refusal above a cooldown
            // and not a one-shot.
            world.UtcNow = coolsAt.AddSeconds(1);
            ConsumeOre(world, "Need", "Ore:Iron");
            List<string> after = Tick(world, st, tuning);

            Assert.Contains(after, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(afterFirst * 2, world.GetNodeInventory("Need", "Ore:Iron"));
        }

        [Fact]
        public void MineBootstrap_IsDisabledAtZeroYieldFraction()
        {
            // Read through tuning.json rather than by poking the field, so the key an operator would
            // actually type is the one under test.
            var world = MakeWorld();
            var st = MakeState();
            FactionTuning tuning = TestSupport.TuningWithOverride("{ \"MineBootstrapYieldFraction\": 0.0 }");
            Assert.Equal(0.0, tuning.MineBootstrapYieldFraction);

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> log = Tick(world, st, tuning);

            Assert.DoesNotContain(log, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Ore:Iron"));
            Assert.Equal(default(DateTime), world.Graph.Nodes["OreField1"].MineCooldownUntilUtc);
            // The identical fixture on the shipped default does mint -- see
            // MineBootstrap_FiresWhenTheFactionOwnsNoDrillAnywhere -- so this is the switch working,
            // not the scenario failing to arise.
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("no_ship_anywhere"));
        }

        [Fact]
        public void MineBootstrap_DoesNotFireForAPlayerFaction()
        {
            // Unlike Haul's bootstrap, which relocates stock that already exists, this one creates
            // matter. A player mines with real drills: a player's standing order that finds no drill
            // anywhere stages the demand and mints nothing.
            var world = MakeWorld();
            var st = MakeState();
            st.IsPlayerFaction = true;

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");            // an NPC would plan an order here
            StandingOrderFixtures.AddMineOrder(st, "Need", 60, world.UtcNow);

            List<string> log = Tick(world, st);

            Assert.DoesNotContain(log, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Ore:Iron"));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("no_ship_anywhere"));
            Assert.Contains(log, l => l.Contains("MINE_AUTO_SCAN") && l.Contains("OreField1:no_ship_demand_staged"));
        }

        [Fact]
        public void MineBootstrap_DoesNotFireFromAPlayerTriggeredMineOrder()
        {
            // The automatic path is the only one that may mint. A player-named `station mine` (and
            // the standing auto-mine order that shares TryFireMine with it) has no live demand
            // behind it and no ops-cycle ownership scan in front of it.
            var world = MakeWorld();
            var st = MakeState();
            FactionTuning tuning = TestSupport.Tuning();

            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));   // no drill owned anywhere
            AddNode(world, "Need");

            var log = new List<string>();
            MineDispatchOutcome outcome;
            PlayerMineResult result = new OperationsManager(itemVolumes: MineTestVolumes)
                .TriggerPlayerMine(world, tuning, st, "OreField1", "Need", null, log, out outcome);

            // The order was accepted and reached the dispatch -- otherwise "nothing was minted"
            // would just mean the command was rejected up front.
            Assert.Equal(PlayerMineResult.Ok, result);
            Assert.Equal(MineDispatchOutcome.NoShipAvailable, outcome);
            Assert.DoesNotContain(log, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Equal(0, world.GetNodeInventory("Need", "Ore:Iron"));
            Assert.Equal(default(DateTime), world.Graph.Nodes["OreField1"].MineCooldownUntilUtc);
        }

        [Fact]
        public void Mine_SourceCanEqualDestination_LocalTopUpNoTravel()
        {
            var world = MakeWorld();
            var st = MakeState();

            // The demanding node is itself an OreField -- Mine should be able to service its own
            // shortfall without needing to travel anywhere.
            var self = AddOreFieldNode(world, "Self", new Vec3(0, 0, 0));
            StockMineShip(self);
            ConsumeOre(world, "Self", "Ore:Iron");

            Tick(world, st);

            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            Assert.Equal("Self", mine.OriginNodeId);
            Assert.Equal("Self", mine.TargetNodeId);
            // No travel distance, but the dwell itself takes real time -- gathering the target
            // volume at the drill's rate, plus overburden -- so arrival is strictly after departure.
            Assert.True(mine.EstimatedArrivalUtc > mine.DepartedUtc);
            Assert.Equal(FleetStopKind.Mine, mine.CurrentStopKind);
        }

        // ---- player-triggered Mine dispatch tests (one-shot) ---------------------------------

        [Fact]
        public void TriggerPlayerMine_Dispatches_WhenSourceAndDestOwnedAndOffCooldown()
        {
            // No demand posted anywhere -- proves the manual trigger works independent of the
            // reactive-demand precondition the automatic path requires.
            var world = MakeWorld();
            var st = MakeState();
            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);
            AddNode(world, "Dest");

            var log = new List<string>();
            MineDispatchOutcome outcome;
            PlayerMineResult result = new OperationsManager()
                .TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", "Dest", null, log, out outcome);

            Assert.Equal(PlayerMineResult.Ok, result);
            Assert.Equal(MineDispatchOutcome.Dispatched, outcome); // a real ship really is dwelling -- not the silent-fallback case
            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            Assert.Equal("OreField1", mine.OriginNodeId);
            Assert.Equal("OreField1", mine.TargetNodeId); // dwelling at the source; Dest is queued as the Service leg
            Assert.Contains(mine.RemainingStops, s => s.Kind == FleetStopKind.Service && s.NodeId == "Dest");
        }

        [Fact]
        public void TriggerPlayerMine_RejectsSourceNotFound()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Dest");

            var result = new OperationsManager().TriggerPlayerMine(world, TestSupport.Tuning(), st, "Nope", "Dest", null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.SourceNotFound, result);
        }

        [Fact]
        public void TriggerPlayerMine_RejectsSourceNotOwned()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0), tag: "OTHER");

            var result = new OperationsManager().TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", null, null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.SourceNotOwned, result);
        }

        [Fact]
        public void TriggerPlayerMine_RejectsSourceNotMineable()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "PlainNode"); // not an OreField/RareOreField/IceField

            var result = new OperationsManager().TriggerPlayerMine(world, TestSupport.Tuning(), st, "PlainNode", null, null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.SourceNotMineable, result);
        }

        [Fact]
        public void TriggerPlayerMine_RejectsDestNotFound()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));

            var result = new OperationsManager().TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", "Nope", null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.DestNotFound, result);
        }

        [Fact]
        public void TriggerPlayerMine_RejectsDestNotOwned()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            AddNode(world, "OtherDest", tag: "OTHER");

            var result = new OperationsManager().TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", "OtherDest", null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.DestNotOwned, result);
        }

        [Fact]
        public void TriggerPlayerMine_RejectsOnCooldown()
        {
            var world = MakeWorld();
            var st = MakeState();
            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore, count: 2); // don't let a ship shortage mask the cooldown gate
            AddNode(world, "Dest");
            var log = new List<string>();
            var mgr = new OperationsManager();

            Assert.Equal(PlayerMineResult.Ok, mgr.TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", "Dest", null, log, out _));

            // Cooldown is only set at dwell resolution -- advance past it and resolve before the
            // deposit is actually gated.
            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            world.UtcNow = mine.EstimatedArrivalUtc.AddSeconds(1);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, TestSupport.Tuning(), new SeededRandom(1), log);

            Assert.Equal(PlayerMineResult.OnCooldown, mgr.TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", "Dest", null, log, out _));
        }

        [Fact]
        public void TriggerPlayerMine_OmittedDest_ResolvesToHighestShortfallNode_WhenDemandExists()
        {
            var world = MakeWorld();
            var st = MakeState();
            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);
            AddNode(world, "BigNeed");
            AddNode(world, "SmallNeed");
            PostItemDemand(world, "F", "Ore:Iron", "BigNeed", 500);
            PostItemDemand(world, "F", "Ore:Iron", "SmallNeed", 5);

            var result = new OperationsManager()
                .TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", null, null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.Ok, result);
            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            Assert.Contains(mine.RemainingStops, s => s.Kind == FleetStopKind.Service && s.NodeId == "BigNeed");
        }

        [Fact]
        public void TriggerPlayerMine_OmittedDest_FallsBackToSource_WhenNoDemandExists()
        {
            var world = MakeWorld();
            var st = MakeState();
            var ore = AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0));
            StockMineShip(ore);

            var result = new OperationsManager()
                .TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", null, null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.Ok, result);
            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            Assert.Equal("OreField1", mine.OriginNodeId);
            Assert.Equal("OreField1", mine.TargetNodeId);
        }

        [Fact]
        public void TriggerPlayerMine_FallsBackToShipDemand_WhenNoDrillShipAnywhere()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddOreFieldNode(world, "OreField1", new Vec3(0, 0, 0)); // no ship anywhere in the network
            AddNode(world, "Dest");

            MineDispatchOutcome outcome;
            var result = new OperationsManager()
                .TriggerPlayerMine(world, TestSupport.Tuning(), st, "OreField1", "Dest", null, new List<string>(), out outcome);

            // Matches the automatic path's own convention: the trigger still succeeds (validation
            // passed, dispatch was genuinely attempted) even with no drill ship anywhere -- the gap
            // is closed via a Ship:Mine demand, never a failure result. Unlike the old instant-mint
            // model, nothing is minted either, since minting now happens only at dwell resolution,
            // which needs a real drill. dispatchOutcome distinguishes this "no ship" case from a real
            // dispatch -- both used to be silently indistinguishable to the player (live 2026-08-18
            // finding: an atmosphere-only miner aimed at an Orbit destination hit this exact same
            // outcome with zero chat/log feedback).
            Assert.Equal(PlayerMineResult.Ok, result);
            Assert.Equal(MineDispatchOutcome.NoShipAvailable, outcome);
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            Assert.Equal(0, world.GetNodeInventory("OreField1", "Ore:Iron"));
            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Mine", shipDemand.Kind);
        }

        private static List<string> TickWith(OperationsManager mgr, WorldModel world, FactionState st,
            FactionTuning tuning = null)
        {
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, tuning ?? TestSupport.Tuning(), log);
            return log;
        }

        /// <summary>Mine-suitable "Miner" stock at the alt node that a real dry-TWR route gate must
        /// reject: scanned mass, zero Down thrust, uniform 1g everywhere.</summary>
        private static void MakeMinerRouteInfeasible(WorldModel world)
        {
            world.Planets = new ConstantGravityCatalogue(1.0);
            ((FakePrefabCatalogue)world.PrefabCatalogue).DryMassKgByPrefab["Miner"] = 10_000;
        }

        [Fact]
        public void Mine_NearestAltRouteInfeasible_FallsThroughToFartherFeasibleNode_AndDispatches()
        {
            // Live 2026-08-22 (EXP): MINE_DISPATCH_BLOCKED src=N370 reason=route_infeasible alt=N8466.
            // The nearest node with drill stock held atmosphere-locked hulls, and because the alt
            // search returned that one node route-blind, the dispatch dead-ended there -- a perfectly
            // capable miner one node further out was never considered, every cycle, for days.
            var world = MakeWorld();
            var prefabs = (FakePrefabCatalogue)world.PrefabCatalogue;
            world.Planets = new ConstantGravityCatalogue(1.0);
            prefabs.DryMassKgByPrefab["Miner"] = 10_000;                          // no Down thrust -> fails at 1g
            prefabs.SetHydrogenDown("MinerCapable", dryMassKg: 1000, forceN: 50_000); // clears 1g comfortably

            var st = MakeState();
            AddOreFieldNode(world, "Deposit", new Vec3(0, 0, 0));
            SpatialNode near = AddNode(world, "NearAlt");
            near.Position = new Vec3(1, 0, 0);
            near.ShipStock["Miner"] = 1;              // nearest, but cannot fly the pickup leg
            SpatialNode far = AddNode(world, "FarAlt");
            far.Position = new Vec3(50, 0, 0);
            far.ShipStock["MinerCapable"] = 1;        // farther, and actually route-feasible
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> log = Tick(world, st);

            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            Assert.Equal("FarAlt", mine.OriginNodeId);
            Assert.Equal("Deposit", mine.TargetNodeId);
            Assert.Equal("MinerCapable", mine.ShipPrefabId);
            Assert.DoesNotContain(log, l => l.Contains("MINE_DISPATCH_BLOCKED"));
            Assert.Empty(world.Demand.SnapshotShipDemands("F").Where(d => d.Kind == "Ship:Mine"));
        }

        [Fact]
        public void Mine_ViaPickupRouteInfeasible_StagesShipMineFallback_AndLogsBlockedOnce()
        {
            var world = MakeWorld();
            MakeMinerRouteInfeasible(world);
            var st = MakeState();
            AddOreFieldNode(world, "Deposit", new Vec3(0, 0, 0));
            var alt = AddNode(world, "Alt");
            StockMineShip(alt); // drill-suitable stock exists, but nothing survives the route gate
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            var mgr = new OperationsManager(itemVolumes: MineTestVolumes);
            List<string> log1 = TickWith(mgr, world, st);

            // Pre-fix this exact shape was a silent no-op: no fleet, no demand, no log.
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            DemandEntry shipDemand = Assert.Single(world.Demand.SnapshotShipDemands("F"),
                d => d.Kind == "Ship:Mine");
            Assert.Equal("Deposit", shipDemand.NodeId); // no shipyard anywhere -> deposit itself
            Assert.Equal("Deposit", shipDemand.ViaNodeId);
            Assert.Contains(log1, l => l.Contains("MINE_DISPATCH_BLOCKED")
                && l.Contains("reason=route_infeasible alt=Alt"));

            // Unchanged block reason must not re-log on the next attempt (log-on-change dedup).
            ConsumeOre(world, "Need", "Ore:Iron");
            List<string> log2 = TickWith(mgr, world, st);
            Assert.DoesNotContain(log2, l => l.Contains("MINE_DISPATCH_BLOCKED"));
        }

        [Fact]
        public void Mine_NothingAnywhereCanFlyTheLeg_BlockLineNamesTheHullAndTheLegThatFailed()
        {
            // Live 2026-08-23 (EXP): eleven deposits, every one "reason=route_infeasible alt=N8466",
            // including src=N048 dest=N048 -- a src==dest pair with no travel leg at all. The leg that
            // actually failed was always alt->src, and the line named neither it nor the hull, so
            // "the gate misreads this prefab" and "this prefab genuinely cannot fly here" read alike.
            var world = MakeWorld();
            MakeMinerRouteInfeasible(world);
            var st = MakeState();
            AddOreFieldNode(world, "Deposit", new Vec3(0, 0, 0));
            SpatialNode alt = AddNode(world, "Alt");
            alt.Position = new Vec3(5, 0, 0);
            StockMineShip(alt);
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> log = Tick(world, st);

            string blocked = Assert.Single(log, l => l.Contains("MINE_DISPATCH_BLOCKED"));
            Assert.Contains("reason=route_infeasible alt=Alt", blocked);
            Assert.Contains("prefab=Miner", blocked);
            Assert.Contains("leg=Alt->Deposit", blocked);
        }

        [Fact]
        public void Mine_NothingAnywhereCanFlyTheLeg_StillReportsRouteInfeasible_NotNoShipAnywhere()
        {
            // The distinction drives the fallback's meaning: "build a drill ship" vs "the drill ships
            // you have are the wrong regime". Skipping the doomed pickup must not collapse the two.
            var world = MakeWorld();
            MakeMinerRouteInfeasible(world);
            var st = MakeState();
            AddOreFieldNode(world, "Deposit", new Vec3(0, 0, 0));
            StockMineShip(AddNode(world, "Alt"));
            AddNode(world, "Need");

            MineDispatchOutcome outcome;
            var result = new OperationsManager(itemVolumes: MineTestVolumes)
                .TriggerPlayerMine(world, TestSupport.Tuning(), st, "Deposit", "Need", null, new List<string>(), out outcome);

            Assert.Equal(PlayerMineResult.Ok, result);
            Assert.Equal(MineDispatchOutcome.NoShipRouteInfeasible, outcome);
            DemandEntry d = Assert.Single(world.Demand.SnapshotShipDemands("F"), x => x.Kind == "Ship:Mine");
            Assert.Equal("Deposit", d.ViaNodeId);
        }

        [Fact]
        public void Mine_NoShipAnywhere_FallbackDemandPostsAtShipyard_NotOreField()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddOreFieldNode(world, "Deposit", new Vec3(0, 0, 0));
            var yard = AddNode(world, "Yard");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st);

            // ShipyardManager silently drops Ship:X demands at non-Shipyard nodes, so the Mine
            // fallback must anchor at the shipyard (Haul-Tier-3 parity) with the deposit as via.
            DemandEntry d = Assert.Single(world.Demand.SnapshotShipDemands("F"),
                x => x.Kind == "Ship:Mine");
            Assert.Equal("Yard", d.NodeId);
            Assert.Equal("Deposit", d.ViaNodeId);
        }

        /// <summary>Gravity by position, so a catchment can state its own profile. Distinct from
        /// <see cref="ConstantGravityCatalogue"/>, which cannot differentiate two deposits.</summary>
        private sealed class PerNodeGravityCatalogue : IPlanetCatalogue
        {
            private readonly Dictionary<string, double> _byPos = new Dictionary<string, double>();
            public PerNodeGravityCatalogue Set(SpatialNode n, double g) { _byPos[Key(n.Position)] = g; return this; }
            private static string Key(Vec3 p) { return p.X + "|" + p.Y + "|" + p.Z; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm)
            {
                double g;
                return _byPos.TryGetValue(Key(posKm), out g) ? g : 0;
            }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        [Fact]
        public void Mine_FallbackDemandVia_IsTheCatchmentsDeepestWell_NotTheDepositChosenToday()
        {
            // ShipyardManager gates its candidate pool on the demand's via node
            // (BuildQueueCreation_UsesDemandViaNodeId_ForPickupLegRouteBias covers that half), so the
            // via decides which hulls are buildable. A Mine hull serves every deposit in the
            // catchment over its life, and naming today's winner sizes it against one sample: live
            // 2026-08-31 ISBT staged via=N007, its own shipyard at g=0, and minted a hull that could
            // not lift off two thirds of its deposits.
            var world = MakeWorld();
            var st = MakeState();

            // Need at the origin, so the catchment walk's nearest-among-equal-scores rule tries Shallow first.
            AddNode(world, "Need");
            SpatialNode shallow = AddOreFieldNode(world, "Shallow", new Vec3(0, 0, 0));
            SpatialNode deep    = AddOreFieldNode(world, "Deep",    new Vec3(10, 0, 0));
            var yard = AddNode(world, "Yard");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new PerNodeGravityCatalogue().Set(shallow, 0.17).Set(deep, 1.01);

            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st);

            DemandEntry d = Assert.Single(world.Demand.SnapshotShipDemands("F"),
                x => x.Kind == "Ship:Mine");
            Assert.Equal("Yard", d.NodeId);
            Assert.Equal("Deep", d.ViaNodeId);
        }

        [Fact]
        public void Mine_FallbackDemandVia_StaysTheChosenDeposit_WhenNothingInRangeIsHarder()
        {
            // Same fixture with the gravity profile flattened: the incumbent must hold, so an estate
            // where gravity separates nothing keeps the exact pre-FAC-216 via and no existing
            // dispatch behaviour moves.
            var world = MakeWorld();
            var st = MakeState();

            AddNode(world, "Need");
            SpatialNode shallow = AddOreFieldNode(world, "Shallow", new Vec3(0, 0, 0));
            SpatialNode deep    = AddOreFieldNode(world, "Deep",    new Vec3(10, 0, 0));
            var yard = AddNode(world, "Yard");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new PerNodeGravityCatalogue().Set(shallow, 0.17).Set(deep, 0.17);

            ConsumeOre(world, "Need", "Ore:Iron");

            Tick(world, st);

            DemandEntry d = Assert.Single(world.Demand.SnapshotShipDemands("F"),
                x => x.Kind == "Ship:Mine");
            Assert.Equal("Shallow", d.ViaNodeId);
        }

        [Fact]
        public void Mine_FallbackDemandPostsNoCargoFloor_HoweverLargeTheShortfall()
        {
            // Mine is multi-trip by construction: ClampTargetLiters caps every dispatch at the
            // chosen hull's own capacity, so the shortfall is never a size a single hull has to
            // meet. Posting it as MinCargoLiters made the demand unservable -- live 2026-08-23, the
            // median Ship:Mine demand asked for 6,353,640L against a 16,875L catalogue ceiling, so
            // ShipyardManager's zone+cargo filter emptied both candidate pools and fell through to
            // its unfiltered terminal branch, minting a hull that could not fly the leg. All 73
            // BOM_ZONE_INFEASIBLE lines that day were role=Mine; Trade posted 13,722 demands sized
            // to one real shipment and produced none.
            var world = MakeWorld();
            var st = MakeState();
            AddOreFieldNode(world, "Deposit", new Vec3(0, 0, 0));
            var yard = AddNode(world, "Yard");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            AddNode(world, "Need");
            ConsumeOre(world, "Need", "Ore:Iron"); // a target rate far beyond any hull

            Tick(world, st);

            DemandEntry d = Assert.Single(world.Demand.SnapshotShipDemands("F"),
                x => x.Kind == "Ship:Mine");
            Assert.Equal(1, d.MinCargoLiters);
        }

        [Fact]
        public void TriggerPlayerMine_RouteInfeasible_ReturnsNoShipRouteInfeasible()
        {
            var world = MakeWorld();
            MakeMinerRouteInfeasible(world);
            var st = MakeState();
            AddOreFieldNode(world, "Deposit", new Vec3(0, 0, 0));
            var alt = AddNode(world, "Alt");
            StockMineShip(alt);
            AddNode(world, "Need");

            MineDispatchOutcome outcome;
            var result = new OperationsManager(itemVolumes: MineTestVolumes)
                .TriggerPlayerMine(world, TestSupport.Tuning(), st, "Deposit", "Need", null, new List<string>(), out outcome);

            Assert.Equal(PlayerMineResult.Ok, result);
            Assert.Equal(MineDispatchOutcome.NoShipRouteInfeasible, outcome);
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void TriggerPlayerMine_SameNodeSourceAndDest_LocalTopUp()
        {
            var world = MakeWorld();
            var st = MakeState();
            var self = AddOreFieldNode(world, "Self", new Vec3(0, 0, 0));
            StockMineShip(self);

            var result = new OperationsManager()
                .TriggerPlayerMine(world, TestSupport.Tuning(), st, "Self", "Self", null, new List<string>(), out _);

            Assert.Equal(PlayerMineResult.Ok, result);
            PendingFleetAction mine = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            Assert.Equal("Self", mine.OriginNodeId);
            Assert.Equal("Self", mine.TargetNodeId);
        }

        // ---- standing auto-mine order tests (hub-anchored, dynamic catchment) ------------------

        [Fact]
        public void MineAutoOrder_ScanDispatchesOneFleetPerEligibleCatchmentNode_UpToCatchmentSize()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Hub");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)));
            StockMineShip(AddOreFieldNode(world, "Ore2", new Vec3(0, 0, 0)));
            StockMineShip(AddOreFieldNode(world, "Ore3", new Vec3(0, 0, 0)));

            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow);
            Tick(world, st);

            var mines = st.PendingFleets.Where(f => f.Kind == ActionKind.Mine).ToList();
            Assert.Equal(3, mines.Count); // one fleet per catchment node, all simultaneous -- the 1-->n proof
            Assert.All(mines, f => Assert.Contains(f.RemainingStops, s => s.Kind == FleetStopKind.Service && s.NodeId == "Hub"));
        }

        [Fact]
        public void MineAutoOrder_SkipsCatchmentNodeStillOnItsOwnCooldown_ButFillsOtherOpenSlots()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Hub");
            var ore1 = AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0));
            var ore2 = AddOreFieldNode(world, "Ore2", new Vec3(0, 0, 0));
            StockMineShip(ore1, count: 2);
            StockMineShip(ore2, count: 2);

            var mgr = new OperationsManager();
            var log = new List<string>();
            Assert.Equal(PlayerMineResult.Ok, mgr.TriggerPlayerMine(world, TestSupport.Tuning(), st, "Ore1", "Hub", null, log, out _));

            // Resolve Ore1's dwell so its own MineCooldownUntilUtc is actually set before the scan
            // runs -- cooldown is written at resolution, not dispatch. The fleet stays in flight
            // (queued Service leg to Hub), so it still counts toward catchment concurrency too.
            PendingFleetAction earlier = st.PendingFleets.Single(f => f.Kind == ActionKind.Mine);
            world.UtcNow = earlier.EstimatedArrivalUtc.AddSeconds(1);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, TestSupport.Tuning(), new SeededRandom(1), log);

            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow);
            mgr.Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);

            var mines = st.PendingFleets.Where(f => f.Kind == ActionKind.Mine).ToList();
            Assert.Equal(2, mines.Count); // Ore1's earlier dispatch, plus exactly one new one from Ore2
            Assert.Single(mines.Where(f => f.OriginNodeId == "Ore1"));
            Assert.Single(mines.Where(f => f.OriginNodeId == "Ore2"));
        }

        [Fact]
        public void MineAutoOrder_DoesNotExceedCatchmentSize_WhenFleetsAlreadyInFlight()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Hub");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)));
            StockMineShip(AddOreFieldNode(world, "Ore2", new Vec3(0, 0, 0)));

            // Two fleets already in flight toward the hub (e.g. from a prior scan) -- catchment size
            // is 2, so no open slots should remain regardless of either node's own cooldown state.
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Mine, TargetNodeId = "Hub", FleetId = "seed1" });
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Mine, TargetNodeId = "Hub", FleetId = "seed2" });

            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow);
            Tick(world, st);

            Assert.Equal(2, st.PendingFleets.Count(f => f.Kind == ActionKind.Mine)); // still just the two seeded fleets
        }

        [Fact]
        public void MineAutoOrder_NextScanIsEarliestCatchmentCooldownClear_NotFlatPeriod()
        {
            var world = MakeWorld();
            var st = MakeState();
            var tuning = TestSupport.Tuning(); // MineCooldownSeconds = 300 (tuning_defaults.json)
            AddNode(world, "Hub");
            var oreA = AddOreFieldNode(world, "OreA", new Vec3(0, 0, 0));
            var oreB = AddOreFieldNode(world, "OreB", new Vec3(0, 0, 0));
            StockMineShip(oreA);
            StockMineShip(oreB);
            oreA.MineCooldownUntilUtc = world.UtcNow.AddSeconds(200); // clears in 200s; OreB was never mined

            // Zero open slots so the scan itself doesn't touch either node's LastMinedUtc -- isolates
            // ComputeNextScanUtc's own behavior from the dispatch side effects.
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Mine, TargetNodeId = "Hub", FleetId = "seed1" });
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Mine, TargetNodeId = "Hub", FleetId = "seed2" });

            StandingOrder hubOrder = StandingOrderFixtures.AddMineOrder(st, "Hub", 3600, world.UtcNow);
            new OperationsManager().Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            Assert.Equal(world.UtcNow.AddSeconds(200), hubOrder.NextScanUtc);
        }

        [Fact]
        public void MineAutoOrder_FallsBackToPeriodCeiling_WhenNothingInCatchmentIsOnCooldown()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Hub");
            AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)); // never mined -- not "on cooldown"

            // One seeded in-flight fleet against a catchment of size 1 leaves zero open slots, so the
            // scan doesn't touch Ore1's LastMinedUtc either.
            st.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Mine, TargetNodeId = "Hub", FleetId = "seed1" });

            StandingOrder hubOrder = StandingOrderFixtures.AddMineOrder(st, "Hub", 120, world.UtcNow);
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());

            Assert.Equal(world.UtcNow.AddSeconds(120), hubOrder.NextScanUtc);
        }

        [Fact]
        public void MineAutoOrder_CatchmentGrows_WhenNewTerritoryEntersRadius()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Hub");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)));

            StandingOrder hubOrder = StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow);
            Tick(world, st);
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));

            // A new owned ore field enters the picture after the order was registered -- no
            // re-registration needed, the catchment is rediscovered fresh every scan.
            StockMineShip(AddOreFieldNode(world, "Ore2", new Vec3(0, 0, 0)));
            hubOrder.NextScanUtc = world.UtcNow; // force due again for the test
            Tick(world, st);

            Assert.Contains(st.PendingFleets, f => f.Kind == ActionKind.Mine && f.OriginNodeId == "Ore2");
        }

        [Fact]
        public void MineAutoOrder_NoOpWhenHubLosesOwnership()
        {
            var world = MakeWorld();
            var st = MakeState();
            var hub = AddNode(world, "Hub");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)));
            hub.OwnerFactionTag = "OTHER"; // player lost the hub node between setting the order and this scan

            StandingOrder hubOrder = StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow);

            Exception ex = Record.Exception(() => Tick(world, st));

            Assert.Null(ex);
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            // Order is left in place (not auto-removed -- the player may retake the node later) but
            // rescheduled, not retried every tick.
            Assert.True(hubOrder.NextScanUtc > world.UtcNow);
        }
        // ---- several standing Mine orders, ranked (FAC-008) ---------------------------------
        //
        // The pre-FAC-008 model allowed exactly one order per hub, so none of this was reachable.
        // What makes it need explicit rules: SpatialNode.MineCooldownUntilUtc is written at Mine
        // DWELL RESOLUTION, not at dispatch, so a deposit this pass just dispatched against still
        // reads as available to the next order in the same tick.

        [Fact]
        public void StandingMine_TwoHubsSharingADeposit_DoNotBothFireAtIt()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "HubA");
            AddNode(world, "HubB");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)), count: 4);

            // Different hubs, so their catchment budgets are independent -- the ONLY thing stopping
            // both from firing at the one shared deposit is the claim set. MineCooldownUntilUtc is
            // written at dwell resolution, not dispatch, so it cannot serialize them within a tick.
            StandingOrderFixtures.AddMineOrder(st, "HubA", 60, world.UtcNow, priority: 9);
            StandingOrderFixtures.AddMineOrder(st, "HubB", 60, world.UtcNow, priority: 0);

            Tick(world, st);

            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void StandingMine_ContestedDeposit_GoesToTheHigherRankedOrder()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "HubA");
            AddNode(world, "HubB");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)), count: 4);

            StandingOrder high = StandingOrderFixtures.AddMineOrder(st, "HubA", 60, world.UtcNow, priority: 9);
            StandingOrder low  = StandingOrderFixtures.AddMineOrder(st, "HubB", 60, world.UtcNow, priority: 0);

            List<string> log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("MINE_AUTO_SCAN") && l.Contains("order=" + high.Id) && l.Contains("Ore1:dispatched"));
            // Named distinctly rather than reading as a cooldown or a silent skip -- a contested
            // catchment has to be diagnosable from the log.
            Assert.Contains(log, l => l.Contains("MINE_AUTO_SCAN") && l.Contains("order=" + low.Id)
                && l.Contains("Ore1:claimed_by_" + high.Id));
        }

        [Fact]
        public void StandingMine_RankIsNotAGate_ALowerRankedOrderWithItsOwnCatchmentStillFires()
        {
            var world = MakeWorld();
            var st = MakeState();
            // Two hubs beyond MineSearchRadiusKm (500) of each other, each with its own deposit, so
            // the catchments are genuinely disjoint and nothing is contested.
            AddNodeAt(world, "HubA", new Vec3(0, 0, 0));
            AddNodeAt(world, "HubB", new Vec3(2000, 0, 0));
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)), count: 4);
            StockMineShip(AddOreFieldNode(world, "Ore2", new Vec3(2000, 0, 0)), count: 4);

            StandingOrder high = StandingOrderFixtures.AddMineOrder(st, "HubA", 60, world.UtcNow, priority: 9);
            StandingOrder low  = StandingOrderFixtures.AddMineOrder(st, "HubB", 60, world.UtcNow, priority: 0);

            List<string> log = Tick(world, st);

            // Rank orders the pass and settles contention; it is not a permission gate. A low-ranked
            // order that competes with nobody runs exactly as it would have on its own.
            Assert.Contains(log, l => l.Contains("order=" + high.Id) && l.Contains("Ore1:dispatched"));
            Assert.Contains(log, l => l.Contains("order=" + low.Id) && l.Contains("Ore2:dispatched"));
            Assert.Equal(2, st.PendingFleets.Count(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void StandingMine_TwoOrdersSharingAHub_ShareThatHubsOpenSlotBudget()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Hub");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)), count: 4);
            StockMineShip(AddOreFieldNode(world, "Ore2", new Vec3(0, 0, 0)), count: 4);

            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow, priority: 9);
            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow, priority: 0);

            Tick(world, st);

            // "Never more simultaneous fleets than there are distinct deposits" is a property of the
            // HUB, not of one order, so two orders on it get two fleets between them -- not two each.
            // The hub's pre-pass in-flight baseline is captured once and this pass's dispatches added
            // to it, which is what makes the count identical under the live and staged contexts.
            Assert.Equal(2, st.PendingFleets.Count(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void StandingMine_AnOrderThatOnlyStagedADemand_DoesNotClaimTheDeposit()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "HubA");
            AddNode(world, "HubB");
            // No mine ship anywhere: TryFireMine still returns Ok, but stages a Ship:Mine demand
            // instead of dispatching.
            AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0));

            StandingOrder high = StandingOrderFixtures.AddMineOrder(st, "HubA", 60, world.UtcNow, priority: 9);
            StandingOrder low  = StandingOrderFixtures.AddMineOrder(st, "HubB", 60, world.UtcNow, priority: 0);

            List<string> log = Tick(world, st);

            // FAC-046: the staged thing is a demand at a shipyard, keyed in the DemandRegister, so a
            // second order restaging it is a refresh and not a second trip. An order that could never
            // fly must not hold the deposit against every peer -- live, one held 10 of 11 for 90
            // minutes while dispatching none.
            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            Assert.DoesNotContain(log, l => l.Contains("MINE_AUTO_SCAN") && l.Contains("claimed_by_"));
            Assert.Contains(log, l => l.Contains("order=" + high.Id) && l.Contains("Ore1:no_ship_demand_staged"));
            Assert.Contains(log, l => l.Contains("order=" + low.Id) && l.Contains("Ore1:no_ship_demand_staged"));
        }

        [Fact]
        public void StandingMine_ClaimTokenNamesTheOrderThatTookIt_NotItsRank()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "HubA");
            AddNode(world, "HubB");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)), count: 4);

            // Equal rank: the winner is decided by the age/id tie-break, so a token claiming it was
            // outranked would be untrue.
            StandingOrder first  = StandingOrderFixtures.AddMineOrder(st, "HubA", 60, world.UtcNow);
            StandingOrder second = StandingOrderFixtures.AddMineOrder(st, "HubB", 60, world.UtcNow);

            List<string> log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("order=" + second.Id) && l.Contains("Ore1:claimed_by_" + first.Id));
        }

        [Fact]
        public void StandingMine_PausedOrder_DoesNotScan()
        {
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Hub");
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)));

            StandingOrder order = StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow);
            order.Paused = true;

            Tick(world, st);

            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            Assert.Equal(world.UtcNow, order.NextScanUtc); // cursor untouched, so resuming scans at once
        }

        [Fact]
        public void StandingMine_OrderWithNoHub_IsSkippedWithoutThrowing()
        {
            var world = MakeWorld();
            var st = MakeState();
            StockMineShip(AddOreFieldNode(world, "Ore1", new Vec3(0, 0, 0)));

            // Reachable from a brain file whose hub node no longer exists, and from a Build order
            // that a future bug leaves in the Mine bucket -- neither may take the tick down.
            StandingOrder order = StandingOrderFixtures.AddMineOrder(st, null, 60, world.UtcNow);

            List<string> log = Tick(world, st);

            Assert.Empty(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
            Assert.Contains(log, l => l.Contains("MINE_AUTO_SCAN") && l.Contains("hub_unavailable"));
            Assert.True(order.NextScanUtc > world.UtcNow);
        }

        // ---- HAUL_BLOCKED: name the item a rejected vector was carrying (FAC-051) ----------------

        /// <summary>A source holding surplus but no ship, and one alternate donor whose hull cannot
        /// fly the pickup leg -- the live shape behind EXP's <c>routeInfeasible=34</c> of 36 vectors.</summary>
        private static void SetUpRouteInfeasibleHaul(WorldModel world, string item)
        {
            var prefabs = (FakePrefabCatalogue)world.PrefabCatalogue;
            world.Planets = new ConstantGravityCatalogue(1.0);
            prefabs.DryMassKgByPrefab["Hauler"] = 10_000;   // no Down thrust -> fails at 1g

            SpatialNode source = AddNode(world, "Source");
            source.Inventory[item] = 100;                   // surplus, but no ship of its own
            StockHaulShips(AddNode(world, "Alt"), 1);       // donor exists; cannot survive the leg
            AddNode(world, "Need");
        }

        [Fact]
        public void HaulBlocked_RouteInfeasible_NamesTheItemAndTheAlternate()
        {
            // OPS_TICK_PERF counts rejected vectors in aggregate, so an item class can go unserved
            // for days while the totals look busy -- live: 2,444 Ore:* input demands, 2,898 hauls
            // dispatched, and not one of them ore.
            var world = MakeWorld();
            var st = MakeState();
            SetUpRouteInfeasibleHaul(world, "Ore:Gold");
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            List<string> log = Tick(world, st);

            Assert.DoesNotContain(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED")
                    && l.Contains("Ore:Gold")
                    && l.Contains("src=Source")
                    && l.Contains("dest=Need")
                    && l.Contains("reason=route_infeasible alt=Alt"));
        }

        [Fact]
        public void HaulBlocked_UnchangedBlock_DoesNotReLog()
        {
            var world = MakeWorld();
            var st = MakeState();
            SetUpRouteInfeasibleHaul(world, "Ore:Gold");
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            var mgr = new OperationsManager(itemVolumes: MineTestVolumes);
            List<string> log1 = TickWith(mgr, world, st);
            Assert.Contains(log1, l => l.Contains("HAUL_BLOCKED"));

            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);
            List<string> log2 = TickWith(mgr, world, st);

            Assert.DoesNotContain(log2, l => l.Contains("HAUL_BLOCKED"));
        }

        [Fact]
        public void HaulBlocked_SeparateItemsAtTheSameNodes_EachGetTheirOwnLine()
        {
            // Keyed per (faction, item): one blocked item must not mask another blocked item, which
            // is the whole point -- the missing fact live was WHICH item never got a ship.
            var world = MakeWorld();
            var st = MakeState();
            SetUpRouteInfeasibleHaul(world, "Ore:Gold");
            world.Graph.Nodes["Source"].Inventory["Ore:Platinum"] = 100;
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);
            PostItemDemand(world, "F", "Ore:Platinum", "Need", 50);

            List<string> log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED") && l.Contains("Ore:Gold"));
            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED") && l.Contains("Ore:Platinum"));
        }

        [Fact]
        public void HaulBlocked_ReBlockAfterASuccessfulDispatch_LogsAgain()
        {
            // The dedup must not swallow a re-block that follows an intervening dispatch, or a
            // recurring outage reads as a single old event.
            var world = MakeWorld();
            var st = MakeState();
            SetUpRouteInfeasibleHaul(world, "Ore:Gold");
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            var mgr = new OperationsManager(itemVolumes: MineTestVolumes);
            Assert.Contains(TickWith(mgr, world, st, TuningWithoutLoadGate()), l => l.Contains("HAUL_BLOCKED"));

            // Give Source its own route-capable hull: the same vector now dispatches.
            ((FakePrefabCatalogue)world.PrefabCatalogue).SetHydrogenDown("Capable", dryMassKg: 1000, forceN: 50_000);
            world.Graph.Nodes["Source"].ShipStock["Capable"] = 1;
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);
            List<string> log2 = TickWith(mgr, world, st, TuningWithoutLoadGate());
            Assert.DoesNotContain(log2, l => l.Contains("HAUL_BLOCKED"));
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Haul));

            // That haul arrives, so its (node, item) pair stops suppressing the demand -- and the
            // hull it consumed is gone, so the next cycle blocks on the same reason again.
            st.PendingFleets.Clear();
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);
            List<string> log3 = TickWith(mgr, world, st, TuningWithoutLoadGate());
            Assert.Contains(log3, l => l.Contains("HAUL_BLOCKED") && l.Contains("Ore:Gold"));
        }

        [Fact]
        public void HaulBlocked_RouteInfeasible_NamesTheRefusedHullAndThePickupLeg()
        {
            // alt= named the donor NODE but never the hull, so the line could not separate "correctly
            // refused an atmosphere-locked hull" from "wrongly refused a capable one" -- the whole
            // question FAC-052 answered on the Mine side with prefab=. leg= is alt->source, the hop
            // that actually failed; naming src->dest read as an impossible claim when they coincide.
            var world = MakeWorld();
            var st = MakeState();
            SetUpRouteInfeasibleHaul(world, "Ore:Gold");
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            List<string> log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED")
                    && l.Contains("reason=route_infeasible")
                    && l.Contains("alt=Alt")
                    && l.Contains("prefab=Hauler")
                    && l.Contains("leg=Alt->Source"));
        }

        // ---- FAC-217: the delivery leg of a fetched-hull convoy ----------------------------

        /// <summary>A three-node itinerary whose gravity separates the two legs: a hull can fly the
        /// empty hop out to the pickup and cannot hold altitude at the delivery node.
        ///
        /// <para><paramref name="destGravityG"/> is the whole fixture. 0.1 g makes both legs flyable
        /// (the regression case); 1.0 g makes only the pickup hop flyable, which is the shape that
        /// stranded a live hull — it arrives and then fails the origin end of every dispatch after.
        /// The hull lifts 50 kN against 10 t dry, so its ceiling is 0.51 g against MinDryTwr 1.05.</para></summary>
        private static void SetUpPickupWithDeliveryGravity(WorldModel world, string item, double destGravityG)
        {
            var prefabs = (FakePrefabCatalogue)world.PrefabCatalogue;
            prefabs.SetHydrogenDown("Hauler", dryMassKg: 10_000, forceN: 50_000);

            SpatialNode alt    = AddNodeAt(world, "Alt",    new Vec3(0, 0, 0));
            SpatialNode source = AddNodeAt(world, "Source", new Vec3(10, 0, 0));
            SpatialNode need   = AddNodeAt(world, "Need",   new Vec3(20, 0, 0));
            source.Inventory[item] = 100;   // surplus, but no hull of its own
            StockHaulShips(alt, 1);

            world.Planets = new PerNodeGravityCatalogue()
                .Set(alt, 0.1).Set(source, 0.1).Set(need, destGravityG);
        }

        [Fact]
        public void Haul_ViaPickup_HullThatCannotLiftOffTheDelivery_IsRefused_NotDispatched()
        {
            // The convoy flies alt->source->dest and HAUL_SHIP_RETURNED leaves the hull at dest, so a
            // gate on the empty hop alone delivers hulls to nodes they can never leave. Live
            // 2026-08-31: ISBT sent its only Idris on a Gas:Oxygen run to a 1.008 g node, and every
            // dispatch for the next 105 minutes died on that hull's origin end while the Ore:Iron
            // backlog behind it grew 145 -> 1828.
            var world = MakeWorld();
            var st = MakeState();
            SetUpPickupWithDeliveryGravity(world, "Ore:Gold", destGravityG: 1.0);
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            Tick(world, st);

            Assert.DoesNotContain(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            // The donor keeps its hull: a refusal must not consume the ship it refused.
            Assert.Equal(1, world.Graph.Nodes["Alt"].ShipStock["Hauler"]);
        }

        [Fact]
        public void Haul_ViaPickup_RefusedOnDelivery_NamesTheDeliveryLeg_NotThePickupHop()
        {
            // Naming alt->source here would send the next reader to the leg that was fine. The two
            // refusals are different facts: one says the donor is unreachable, the other says the
            // destination is unleavable.
            var world = MakeWorld();
            var st = MakeState();
            SetUpPickupWithDeliveryGravity(world, "Ore:Gold", destGravityG: 1.0);
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            List<string> log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED")
                    && l.Contains("reason=route_infeasible")
                    && l.Contains("alt=Alt")
                    && l.Contains("prefab=Hauler")
                    && l.Contains("leg=Source->Need"));
        }

        [Fact]
        public void Haul_ViaPickup_HullThatSurvivesBothLegs_StillDispatches()
        {
            // The gate must be able to pass. Same fixture with the delivery node inside the hull's
            // 0.51 g ceiling: without this, "refused" above is indistinguishable from a gate that
            // refuses everything.
            var world = MakeWorld();
            var st = MakeState();
            SetUpPickupWithDeliveryGravity(world, "Ore:Gold", destGravityG: 0.1);
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            List<string> log = Tick(world, st, TuningWithoutLoadGate());

            Assert.Contains(log, l => l.Contains("HAUL_DISPATCHED") && l.Contains("via_pickup=Alt"));
            Assert.Contains(st.PendingFleets, f => f.Kind == ActionKind.Haul);
        }

        [Fact]
        public void Mine_ViaPickup_HullThatCannotLiftOffTheDelivery_IsRefused()
        {
            // Same hole, same shape, on the Mine side: the fetched drill flies alt->deposit->hub.
            var world = MakeWorld();
            var st = MakeState();
            var prefabs = (FakePrefabCatalogue)world.PrefabCatalogue;
            prefabs.SetHydrogenDown("Miner", dryMassKg: 10_000, forceN: 50_000);

            SpatialNode deposit = AddOreFieldNode(world, "Deposit", new Vec3(10, 0, 0));
            SpatialNode alt     = AddNodeAt(world, "Alt",  new Vec3(0, 0, 0));
            SpatialNode need    = AddNodeAt(world, "Need", new Vec3(20, 0, 0));
            StockMineShip(alt);
            world.Planets = new PerNodeGravityCatalogue()
                .Set(alt, 0.1).Set(deposit, 0.1).Set(need, 1.0);
            ConsumeOre(world, "Need", "Ore:Iron");

            List<string> log = Tick(world, st);

            Assert.DoesNotContain(st.PendingFleets, f => f.Kind == ActionKind.Mine);
            Assert.Equal(1, world.Graph.Nodes["Alt"].ShipStock["Miner"]);
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED")
                    && l.Contains("reason=route_infeasible")
                    && l.Contains("alt=Alt")
                    && l.Contains("leg=Deposit->Need"));
        }

        [Fact]
        public void HaulBlocked_NamesTheLargestRefusedCargoHull_NotAnArbitraryOne()
        {
            // Must name the hull Consume would have preferred, or the line accuses the wrong prefab.
            var world = MakeWorld();
            var st = MakeState();
            var prefabs = (FakePrefabCatalogue)world.PrefabCatalogue;
            world.Planets = new ConstantGravityCatalogue(1.0);
            prefabs.DryMassKgByPrefab["Hauler"] = 10_000;
            prefabs.DryMassKgByPrefab["BigHauler"] = 10_000;
            prefabs.CargoLitersByPrefab["Hauler"]    = 1_000;    // set both explicitly: the fake's
            prefabs.CargoLitersByPrefab["BigHauler"] = 50_000;   // unset default is 1,000,000

            AddNode(world, "Source").Inventory["Ore:Gold"] = 100;
            SpatialNode altNode = AddNode(world, "Alt");
            altNode.ShipStock["Hauler"] = 1;
            altNode.ShipStock["BigHauler"] = 1;   // both refused; this is the one Consume would pick
            AddNode(world, "Need");
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            List<string> log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED") && l.Contains("prefab=BigHauler"));
        }

        [Fact]
        public void HaulBlocked_NoDonorAtAll_StaysNoShipAnywhereAndNamesNoHull()
        {
            // A network with no cargo hull anywhere bootstraps instead; with one parked at a node the
            // scan never reaches, the outcome must still not claim a refused hull it never saw.
            var world = MakeWorld();
            var st = MakeState();
            AddNode(world, "Source").Inventory["Ore:Gold"] = 100;
            StockHaulShips(AddNode(world, "Faraway"), 1);
            AddNode(world, "Need");
            PostItemDemand(world, "F", "Ore:Gold", "Need", 50);

            List<string> log = Tick(world, st);

            Assert.DoesNotContain(log, l => l.Contains("HAUL_BLOCKED") && l.Contains("prefab="));
        }

        [Fact]
        public void HaulBlocked_ManyVectorsOfOneItemBlockedTheSameWay_LogOnce()
        {
            // Live 2026-08-23: 9,580 HAUL_BLOCKED lines in four minutes against 41 dispatches. One
            // unserved item produces a vector per (source, destination) pair every cycle, all
            // failing identically -- so the dedup signature must be the reason class, not the node
            // ids, or every vector counts as its own "change" and nothing is deduped at all.
            var world = MakeWorld();
            var st = MakeState();
            var prefabs = (FakePrefabCatalogue)world.PrefabCatalogue;
            world.Planets = new ConstantGravityCatalogue(1.0);
            prefabs.DryMassKgByPrefab["Hauler"] = 10_000;

            foreach (string src in new[] { "SrcA", "SrcB", "SrcC" })
                AddNode(world, src).Inventory["Ore:Gold"] = 100;
            StockHaulShips(AddNode(world, "Alt"), 1);
            foreach (string need in new[] { "NeedA", "NeedB", "NeedC" })
            {
                AddNode(world, need);
                PostItemDemand(world, "F", "Ore:Gold", need, 50);
            }

            List<string> log = Tick(world, st);

            Assert.Single(log, l => l.Contains("HAUL_BLOCKED"));
        }

        [Fact]
        public void HaulBlocked_NotLoggedWhenTheVectorDispatches()
        {
            var world = MakeWorld();
            var st = MakeState();
            SpatialNode source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 100;
            StockHaulShips(source, 1);
            AddNode(world, "Need");
            PostItemDemand(world, "F", "Explosives", "Need", 50);

            List<string> log = Tick(world, st);

            Assert.Equal(50, HaulTo(st, "Need").Cargo["Explosives"]);
            Assert.DoesNotContain(log, l => l.Contains("HAUL_BLOCKED"));
        }

        // ---- FAC-196: production input demand is a gross target, not a shortfall -------------

        /// <summary>A refinery holding most of a machine-horizon of ore ranks its recipe, earns no
        /// slot, and posts its input demand through ProductionManager's slotless path. That row has
        /// to survive <c>MatchVectors</c>, which nets the destination's own stock out of
        /// <see cref="DemandEntry.Quantity"/> before forming a vector -- so the posted quantity is
        /// the whole horizon, not the part the node is missing.</summary>
        [Fact]
        public void SlotlessRefineryInputDemand_SurvivesMatching_AndPullsOre()
        {
            var world = MakeWorld();
            var st = MakeState();

            ConversionRecipe Ore2Ingot(string sub) => new ConversionRecipe
            {
                BlueprintId = sub + "OreToIngot",
                TimeSecs = 1.0f,
                Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = sub, Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = sub, Amount = 1 } },
            };
            // Two ranked recipes against ONE machine, so the lower-ranked (iron, listed last --
            // ties keep list order) is ranked-but-slotless. This used to be reached by relying on
            // ComputeSlotCounts' one-machine rescue being restricted to a sole survivor; FAC-231
            // removed that restriction because it was zeroing whole banks live, so machine scarcity
            // is now both the honest and the only way in. The assertions are unchanged.
            world.Tuning.MachineProfiles[NodeCapabilityKind.Refinery] = new MachineProfile { BaseSpeed = 0.65, MachineCount = 1 };
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe> { Ore2Ingot("Nickel"), Ore2Ingot("Iron") },
            };

            MachineModel model = MachinePhysics.ResolveAll(world.Tuning)[(int)NodeCapabilityKind.Refinery];
            long horizon = (long)Math.Ceiling(
                model.Speed / 1.0 * world.Tuning.ProductionSlotInputHorizonSeconds
                * MachinePhysics.InputAmount(1, model, false));
            Assert.True(horizon >= 4, "horizon must be big enough to sit three-quarters of the way up it");

            // Three quarters of a horizon: enough that the shortfall is smaller than the stock, which
            // is the regime where a shortfall-sized demand row can never out-measure the destination.
            long have = horizon - horizon / 4;

            var refinery = AddNode(world, "Refinery");
            refinery.Capabilities[NodeCapabilityKind.Refinery] = 1;
            refinery.Inventory["Ore:Iron"]   = have;
            refinery.Inventory["Ore:Nickel"] = have;
            PostItemDemand(world, "F", "Ingot:Iron", "Refinery", 1000000);
            PostItemDemand(world, "F", "Ingot:Nickel", "Refinery", 1000000);

            var field = AddNode(world, "OreField");
            field.Inventory["Ore:Iron"] = 1000000;
            StockHaulShips(field, 1);

            var prodLog = new List<string>();
            new ProductionManager(capRecipes).Tick(new List<FactionState> { st }, world, world.Tuning, prodLog);

            DemandEntry ore = Assert.Single(world.Demand.SnapshotItemDemands("F"),
                d => d.Kind == "Item:Ore:Iron" && d.NodeId == "Refinery");
            Assert.Equal(horizon, ore.Quantity);

            Tick(world, st, TuningWithoutLoadGate());

            Assert.Equal(horizon - have, HaulTo(st, "Refinery").Cargo["Ore:Iron"]);
        }

        // ---- multi-stop Haul consolidation ---------------------------------------------------
        //
        // Vectors matched the same pass for the same destination ride one convoy when their sources
        // are close enough together, instead of one convoy each. See docs/material-vectors.md.

        /// <summary>Two sources supplying one destination, the nearer holding the only ship.
        /// <paramref name="secondSourceX"/> places the second source relative to the first (which
        /// sits at x=1), which is what decides whether it falls inside HaulConsolidationRadiusKm.</summary>
        private static FactionState TickTwoSourcesOneDestination(double secondSourceX, WorldModel world,
            IItemVolumeCatalogue volumes, FactionTuning tuning, List<string> log = null)
        {
            var st = MakeState();
            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var near = AddNode(world, "Near");
            near.Position = new Vec3(1, 0, 0);
            near.Inventory["Ore"] = 60;
            StockHaulShips(near, 1);

            var far = AddNode(world, "Far");
            far.Position = new Vec3(secondSourceX, 0, 0);
            far.Inventory["Ore"] = 40;

            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, tuning, log ?? new List<string>());
            return st;
        }

        [Fact]
        public void Haul_SecondSourceWithinRadius_RidesOneConvoyAsAPickupStop()
        {
            var world = MakeWorld();
            var log = new List<string>();
            FactionState st = TickTwoSourcesOneDestination(120, world,
                new FakeVolumeCatalogue().Set("Ore", 3), TuningWithoutLoadGate(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal(ActionKind.Haul, convoy.Kind);
            // Near's own 60 units are aboard from the moment it leaves; the convoy flies to Far
            // first (a Pickup leg), and only the queued Service stop is the delivery.
            Assert.Equal(60, convoy.Cargo["Ore"]);
            Assert.Equal("Near", convoy.OriginNodeId);
            Assert.Equal("Far", convoy.TargetNodeId);
            Assert.Equal(FleetStopKind.Pickup, convoy.CurrentStopKind);
            Assert.Equal("Ore", convoy.PickupItemKey);
            Assert.Equal(40, convoy.PickupQuantity);
            FleetStop delivery = Assert.Single(convoy.RemainingStops);
            Assert.Equal("Dest", delivery.NodeId);
            Assert.Equal(FleetStopKind.Service, delivery.Kind);
            Assert.Contains(log, l => l.Contains("HAUL_DISPATCHED") && l.Contains("consolidated=1 pickupStops=1"));
        }

        [Fact]
        public void Haul_SecondSourceBeyondRadius_StaysASeparateVector()
        {
            // The discriminator for the test above: same topology, same tuning, the second source
            // moved past HaulConsolidationRadiusKm. Nothing is folded, so the second vector goes
            // looking for its own hull and finds none — one convoy, one blocked vector, not two
            // stops on one convoy.
            var world = MakeWorld();
            var log = new List<string>();
            FactionState st = TickTwoSourcesOneDestination(900, world,
                new FakeVolumeCatalogue().Set("Ore", 3), TuningWithoutLoadGate(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Dest", convoy.TargetNodeId);
            Assert.Equal(FleetStopKind.Service, convoy.CurrentStopKind);
            Assert.Null(convoy.RemainingStops);
            Assert.DoesNotContain(log, l => l.Contains("consolidated="));
            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED") && l.Contains("src=Far"));
        }

        [Fact]
        public void Haul_ConsolidationRadiusZero_KeepsOneConvoyPerVector()
        {
            var world = MakeWorld();
            FactionTuning tuning = TuningWithoutLoadGate();
            tuning.HaulConsolidationRadiusKm = 0;
            var log = new List<string>();
            FactionState st = TickTwoSourcesOneDestination(120, world,
                new FakeVolumeCatalogue().Set("Ore", 3), tuning, log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Dest", convoy.TargetNodeId);
            Assert.Null(convoy.RemainingStops);
            Assert.DoesNotContain(log, l => l.Contains("consolidated="));
        }

        [Fact]
        public void Haul_ExtraPickupThatDoesNotFitTheHold_IsLeftForItsOwnConvoy()
        {
            // 60 units x 3 L already fills 180 of the hull's 200 L, so Far's 40 x 3 L cannot be
            // budgeted onto this convoy and must not be folded in and then silently dropped at the
            // stop. The hold budget, not the radius, is what refuses it here.
            var world = MakeWorld();
            var catalogue = (FakePrefabCatalogue)world.PrefabCatalogue;
            catalogue.CargoLitersByPrefab["Hauler"] = 200;

            var log = new List<string>();
            FactionState st = TickTwoSourcesOneDestination(120, world,
                new FakeVolumeCatalogue().Set("Ore", 3), TestSupport.Tuning(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Dest", convoy.TargetNodeId);
            Assert.Null(convoy.RemainingStops);
            Assert.Contains(log, l => l.Contains("HAUL_BLOCKED") && l.Contains("src=Far"));
        }

        // ---- FAC-235: cargo work takes the least specialised hull that can do the job -----------

        /// <summary>One demand, one convoy, and two hulls at the source, so exactly one is chosen and
        /// the other is a real loser.
        /// <para><b><paramref name="favoured"/> is deliberately the BIGGER hull.</b> A 1,000 L load
        /// fits both, and a haul takes the cheapest hull for the job — for a load both hold, the
        /// smaller (<see cref="HaulHullCost.Fit"/>) — so the specialised hull here would win outright
        /// on the ranking. The expected answer is reachable only if the specialisation preference
        /// overrides it. Both hulls sharing the fake catalogue's default capacity made an earlier
        /// version of these tests pass with the middle tier deleted: a tie decided by iteration
        /// order, asserting nothing.</para></summary>
        private static string HullChosenBetween(string favoured, string disfavoured)
        {
            var world = MakeWorld();
            FactionState st = MakeState();
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab[favoured] = 900_000;
            cat.CargoLitersByPrefab[disfavoured] = 100_000;   // would win on the ranking alone

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 1000);

            var source = AddNode(world, "Source");
            source.Position = new Vec3(10, 0, 0);
            source.Inventory["Ore"] = 1000;
            source.ShipStock[favoured] = 1;
            source.ShipStock[disfavoured] = 1;

            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), new List<string>());

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            return convoy.ShipPrefabId;
        }

        [Fact]
        public void HullChoice_PlainHaulerBeatsABiggerDrillHull()
        {
            // The half PreferNonDrill already did. Kept so a rewrite cannot silently drop it.
            Assert.Equal("Hauler", HullChosenBetween("Hauler", "MinerHauler"));
        }

        [Fact]
        public void HullChoice_PlainHaulerBeatsABiggerArmedHull()
        {
            // NEW: there was no combat arm at all before FAC-235. Live 2026-09-02, 27.8% of cargo
            // convoys took an armed hull with nothing preferring otherwise -- a faction's only
            // warship hauling ore.
            Assert.Equal("Hauler", HullChosenBetween("Hauler", "GunHauler"));
        }

        [Fact]
        public void HullChoice_SingleSpecialismBeatsABiggerDualRoleHull()
        {
            // The middle pass, and the reason there are three tiers rather than two. A hull that both
            // drills AND fights is the scarcest thing a faction owns -- 8.2% of cargo convoys took one
            // -- and a two-pass scheme would spend it exactly as readily as a single-specialism hull.
            // Neither candidate qualifies for tier 1, so this cannot pass on the tier-1 rule, and the
            // loser is the larger hull, so it cannot pass on scoring either.
            Assert.Equal("MinerHauler", HullChosenBetween("MinerHauler", "MinerGunHauler"));
        }

        [Fact]
        public void HullChoice_OnlySpecialistHullsAvailable_StillDispatchesAndScoresNormally()
        {
            // The guarantee the whole design rests on: the last pass is unrestricted, so a faction
            // whose only cargo hulls carry drills and guns must still haul. And with every candidate
            // in the same tier, the hull ranking decides -- for a load both hold, the SMALLER one
            // (B, 100,000 L against A's 900,000), which is the opposite of what the helper's naming
            // implies and proves the tiers stop mattering once nothing separates the candidates.
            Assert.Equal("MinerGunHaulerB", HullChosenBetween("MinerGunHaulerA", "MinerGunHaulerB"));
        }

        // ---- FAC-232: the load gate ------------------------------------------------------------

        /// <summary>Builds the dribble shape the gate exists to stop: a large unmet want at Dest and
        /// a source holding a token amount of it, against a real 3,375 L hull.</summary>
        private static WorldModel DribbleWorld(out FactionState st, long sourceStock, long want,
            DemandClass demandClass = DemandClass.Unclassified)
        {
            var world = MakeWorld();
            st = MakeState();
            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", want, demandClass);

            var source = AddNode(world, "Source");
            source.Position = new Vec3(10, 0, 0);
            source.Inventory["Ore"] = sourceStock;
            StockHaulShips(source, 1, "RealHull");
            return world;
        }

        private static FactionTuning GateTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.HaulMinLoadFraction = 0.10;
            t.HaulHoldUrgency = 2.0;
            return t;
        }

        [Fact]
        public void LoadGate_DribbleAgainstAHugeWant_IsRefusedAndTheHullGoesBack()
        {
            // 40 units x 1 L = 40 L in a 3,375 L hull: 1.2% of the hold, closing 0.3% of the want.
            // This is the measured live shape -- FCTM sent 43 units of gravel at a time against a
            // 13,334 shortfall, 131 convoys in a day. See docs/issues/FAC-232.
            FactionState st;
            WorldModel world = DribbleWorld(out st, sourceStock: 40, want: 13_334);
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["RealHull"] = 3375;

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            Assert.Empty(st.PendingFleets);
            // The hull must come back, or the gate leaks the network's haulers one refusal at a time.
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["RealHull"]);
            Assert.Contains(log, l => l.Contains("HAUL_LOAD_GATED") && l.Contains("fill=0.012"));
            // And ONLY that line. A gated convoy fell through to the block logging as
            // reason=node_missing -- a wrong reason, and a second report of one refusal inflating the
            // count HAUL_BLOCKED exists to measure. Live: 120 such lines against 0 pre-gate.
            Assert.DoesNotContain(log, l => l.Contains("HAUL_BLOCKED"));
        }

        [Fact]
        public void LoadGate_SmallCompleteDelivery_WaitsWhenNotUrgent()
        {
            // A tiny load that closes an ordinary want outright no longer flies a hull 80 times its
            // size: the hull goes back and the vector waits for a right-sized hull or a convoy to fold
            // it in. Urgency 1.0 (unclassified, fully starved, fresh) is under HaulHoldUrgency.
            FactionState st;
            WorldModel world = DribbleWorld(out st, sourceStock: 40, want: 40);
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["RealHull"] = 3375;

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            Assert.Empty(st.PendingFleets);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["RealHull"]);
            Assert.Contains(log, l => l.Contains("HAUL_LOAD_GATED") && l.Contains("urgency=1"));
        }

        [Fact]
        public void LoadGate_SmallCompleteDelivery_FliesWhenUrgent()
        {
            // THE DISCRIMINATOR for the test above: the identical load and hull, for an install --
            // class weight 3.0, over HaulHoldUrgency -- flies at once. Without this the hold could be
            // refusing everything light and the test above would still pass.
            FactionState st;
            WorldModel world = DribbleWorld(out st, sourceStock: 40, want: 40, demandClass: DemandClass.Install);
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["RealHull"] = 3375;

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Dest", convoy.TargetNodeId);
            Assert.DoesNotContain(log, l => l.Contains("HAUL_LOAD_GATED"));
            // The dispatch line names the urgency that let it through: an install, fully starved,
            // fresh, scores its class weight 3.0.
            Assert.Contains(log, l => l.Contains("HAUL_DISPATCHED") && l.Contains(" urgency=3"));
        }

        // ---- FAC-393: the hull fits the job ------------------------------------------------------

        /// <summary>A small want at Dest, a source holding it, and a 900,000 L hull parked at the
        /// source; <paramref name="smallAt"/> names a node that also holds a 5,000 L hull, or null.
        /// 1,000 L of load is 0.1% of the big hold and 20% of the small one.</summary>
        private static WorldModel BigHullOnlyWorld(out FactionState st, string smallAt,
            DemandClass demandClass = DemandClass.Unclassified)
        {
            var world = MakeWorld();
            st = MakeState();
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["Big"] = 900_000;
            cat.CargoLitersByPrefab["Small"] = 5_000;

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 1000, demandClass);

            var source = AddNode(world, "Source");
            source.Position = new Vec3(10, 0, 0);
            source.Inventory["Ore"] = 1000;
            source.ShipStock["Big"] = 1;

            if (smallAt != null)
            {
                SpatialNode donor = world.Graph.Nodes.ContainsKey(smallAt) ? world.Graph.Nodes[smallAt] : AddNode(world, smallAt);
                if (!ReferenceEquals(donor, source)) donor.Position = new Vec3(20, 0, 0);
                donor.ShipStock["Small"] = 1;
            }
            return world;
        }

        [Fact]
        public void HullFit_ASmallLoad_TakesTheSmallerHoldThatFits()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: "Source");

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Small", convoy.ShipPrefabId);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Big"]);
        }

        /// <summary>The off switch, and the discriminator for the test above: ranking by largest hold
        /// takes the big hull for the same job.</summary>
        [Fact]
        public void HullFit_RankingOff_TakesTheLargestHold()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: "Source");
            FactionTuning t = TuningWithoutLoadGate();
            t.HaulRankHullsByFuel = false;

            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, t, new List<string>());

            Assert.Equal("Big", Assert.Single(st.PendingFleets).ShipPrefabId);
        }

        [Fact]
        public void HullFit_OnlyABigHullHere_FetchesARightSizedHullFromElsewhere()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: "Donor");

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Small", convoy.ShipPrefabId);
            Assert.Equal("Donor", convoy.OriginNodeId);
            Assert.Equal(FleetStopKind.Pickup, convoy.CurrentStopKind);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Big"]);
            Assert.Contains(log, l => l.Contains("HAUL_DISPATCHED") && l.Contains("via_pickup=Donor")
                                      && l.Contains("passed_over=Big") && l.Contains(" urgency=1"));
        }

        /// <summary>An urgent light convoy is not held, but it still takes the right-sized hull from
        /// elsewhere rather than the big one at its source.</summary>
        [Fact]
        public void HullFit_AnUrgentLightConvoy_StillFetchesARightSizedHull()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: "Donor", demandClass: DemandClass.Install);

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Small", convoy.ShipPrefabId);
            Assert.Equal("Donor", convoy.OriginNodeId);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Big"]);
            Assert.Contains(log, l => l.Contains("passed_over=Big") && l.Contains(" urgency=3"));
        }

        /// <summary>The discriminator: the same urgent job with no right-sized hull anywhere flies the
        /// big hull at once rather than waiting.</summary>
        [Fact]
        public void HullFit_AnUrgentLightConvoy_WithNoRightSizedHull_FliesTheLocalOne()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: null, demandClass: DemandClass.Install);

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Big", convoy.ShipPrefabId);
            Assert.Equal("Source", convoy.OriginNodeId);
            Assert.DoesNotContain(log, l => l.Contains("HAUL_LOAD_GATED"));
        }

        /// <summary>The donor sends the hull the search found room for. Here it also holds a plain
        /// 900,000 L freighter, which the specialisation tiers rank ahead of the armed 5,000 L courier;
        /// without the capacity ceiling on the donor's consume, the "right-sized" fetch flies the
        /// freighter at 0.1% full.</summary>
        [Fact]
        public void HullFit_TheDonorSendsTheRightSizedHull_NotItsFirstRankedOne()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: null);
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["BigPlain"] = 900_000;
            cat.CargoLitersByPrefab["GunSmall"] = 5_000;
            SpatialNode donor = AddNode(world, "Donor");
            donor.Position = new Vec3(20, 0, 0);
            donor.ShipStock["BigPlain"] = 1;
            donor.ShipStock["GunSmall"] = 1;

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("GunSmall", convoy.ShipPrefabId);
            Assert.Equal("Donor", convoy.OriginNodeId);
            Assert.Equal(1, donor.ShipStock["BigPlain"]);
        }

        // ---- FAC-014: a Haul carries the value of its hold ----------------------------------------

        /// <summary>The dashboard read 0 credits off every Haul because the dispatch filled the
        /// manifest and never valued it.</summary>
        [Fact]
        public void DispatchedHaul_CarriesTheBookValueOfItsHold()
        {
            var world = MakeWorld();
            world.PriceCatalogue = new SubtypePrices(new Dictionary<string, long> { { "Explosives", 7 } });
            var st = MakeState();

            var source = AddNode(world, "Source");
            source.Inventory["Explosives"] = 50;
            StockHaulShips(source, 1);
            AddNode(world, "Need").Position = new Vec3(10, 0, 0);

            PostItemDemand(world, "F", "Explosives", "Need", 50);
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());

            PendingFleetAction fleet = HaulTo(st, "Need");
            Assert.Equal(50, fleet.Cargo["Explosives"]);
            Assert.Equal(350, fleet.CargoValue);
        }

        /// <summary>A consolidated convoy leaves with the value of what it loaded at the source and
        /// gains each Pickup stop's load as it boards — the sum, not the last stop's share.</summary>
        [Fact]
        public void ConsolidatedHaul_ValueGrowsByEachPickupItBoards()
        {
            var world = MakeWorld();
            world.PriceCatalogue = new SubtypePrices(new Dictionary<string, long> { { "Ore", 5 } });
            FactionTuning tuning = TuningWithoutLoadGate();
            var st = MakeState();
            var states = new List<FactionState> { st };

            AddNode(world, "Dest").Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);
            var near = AddNode(world, "Near");
            near.Position = new Vec3(1, 0, 0);
            near.Inventory["Ore"] = 60;
            StockHaulShips(near, 1);
            var far = AddNode(world, "Far");
            far.Position = new Vec3(120, 0, 0);
            far.Inventory["Ore"] = 40;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            new OperationsManager(itemVolumes: volumes).Tick(states, world, tuning, new List<string>());
            Assert.Equal(300, Assert.Single(st.PendingFleets).CargoValue);

            bool sawFullHold = false;
            for (int i = 0; i < 6 && st.PendingFleets.Any(f => !f.Resolved); i++)
            {
                world.UtcNow = world.UtcNow.AddHours(1);
                PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), new List<string>(),
                    null, null, null, volumes);
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    long ore;
                    if (f.Resolved || f.Cargo == null || !f.Cargo.TryGetValue("Ore", out ore) || ore != 100) continue;
                    Assert.Equal(500, f.CargoValue);
                    sawFullHold = true;
                }
            }
            Assert.True(sawFullHold, "the convoy never held the whole 100 Ore between the Far pickup and delivery");
        }

        /// <summary>Gas is stocked in litres and priced per kilolitre; a unit price applied per litre
        /// would read a tank of hydrogen as a thousand times its worth.</summary>
        [Fact]
        public void CargoValueOf_PricesGasPerKilolitre()
        {
            var world = MakeWorld();
            var prices = new SubtypePrices(new Dictionary<string, long> { { "Gas:Hydrogen", 300 }, { "Iron", 10 } });
            var cargo = new Dictionary<string, long> { { "Gas:Hydrogen", 2000 }, { "Iron", 3 } };

            Assert.Equal(630, PendingFleetResolver.CargoValueOf(cargo, world, prices));
        }

        // ---- FAC-393: fuel against cargo value ---------------------------------------------------

        private sealed class SubtypePrices : IItemPriceCatalogue
        {
            private readonly Dictionary<string, long> _p;
            public SubtypePrices(Dictionary<string, long> p) { _p = p; }
            public long BasePrice(string subTypeId) { long v; return _p.TryGetValue(subTypeId, out v) ? v : 0; }
        }

        private static ThrustBand HydrogenBandForFuel()
        {
            return new ThrustBand
            {
                ThrusterType = "Hydrogen", ForceMagnitudeN = 7.2e6f,
                EffectivenessAtMinInfluence = 1, EffectivenessAtMaxInfluence = 1,
                MaxPowerMw = 7.5f, FuelId = "Gas:Hydrogen", FuelEfficiency = 1f,
                FuelEnergyDensityMwhPerL = 0.001556f,
            };
        }

        /// <summary>19 units of "Ore" wanted at Dest, 3,000 km of deep space from Source, where a 1,000 t
        /// hydrogen hull with a 100 L hold stands: the load fills 19% of it, so the load gate is not in
        /// play. The leg burns about 134 kL of hydrogen, ~40,000 credits at 300 the kilolitre. The want is
        /// an install, so urgency is not in play either.</summary>
        private static WorldModel FuelWorld(out FactionState st, long orePrice, bool pricedHull = true)
        {
            var world = MakeWorld();
            st = MakeState();
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            world.PriceCatalogue = new SubtypePrices(new Dictionary<string, long> { { "Ore", orePrice }, { "Hydrogen", 300 } });
            world.PriceOracle = NullPriceOracle.Instance;
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["Hauler"] = 100;
            if (pricedHull)
            {
                cat.DryMassKgByPrefab["Hauler"] = 1.0e6;
                var bands = new List<ThrustBand> { HydrogenBandForFuel() };
                cat.ThrustBandsByPrefab["Hauler"] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    { ThrustDirection.Forward, bands }, { ThrustDirection.Backward, bands }, { ThrustDirection.Down, bands },
                };
            }

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 19, DemandClass.Install);
            var source = AddNode(world, "Source");
            source.Position = new Vec3(3000, 0, 0);
            source.Inventory["Ore"] = 19;
            source.ShipStock["Hauler"] = 1;
            return world;
        }

        private static List<string> TickFuel(WorldModel world, FactionState st, FactionTuning tuning)
        {
            world.Tuning = tuning;
            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1),
                                  itemMasses: new FakeMassCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, tuning, log);
            return log;
        }

        [Fact]
        public void FuelCeiling_CheapCargoFarAway_IsRefused_EvenForAnUrgentWant()
        {
            FactionState st;
            WorldModel world = FuelWorld(out st, orePrice: 50);   // 950 credits of cargo

            List<string> log = TickFuel(world, st, GateTuning());

            Assert.Empty(st.PendingFleets);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Hauler"]);
            string line = Assert.Single(log.FindAll(l => l.Contains("HAUL_LOAD_GATED")));
            Assert.Contains("reason=fuel", line);
            Assert.Contains("cargoCr=950", line);
            Assert.Contains("fuelCr=", line);
        }

        /// <summary>The discriminator: the same trip carrying goods worth more than its fuel flies.</summary>
        [Fact]
        public void FuelCeiling_ValuableCargo_Flies()
        {
            FactionState st;
            WorldModel world = FuelWorld(out st, orePrice: 35_000);   // 665,000 credits of cargo

            List<string> log = TickFuel(world, st, GateTuning());

            Assert.Single(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("HAUL_LOAD_GATED"));
        }

        [Fact]
        public void FuelCeiling_Zero_TurnsTheCheckOff()
        {
            FactionState st;
            WorldModel world = FuelWorld(out st, orePrice: 50);
            FactionTuning t = GateTuning();
            t.FuelValueCeiling = 0;

            TickFuel(world, st, t);

            Assert.Single(st.PendingFleets);
        }

        /// <summary>With the fuel model off, no decision reads it: the same cheap trip flies.</summary>
        [Fact]
        public void FuelCeiling_FuelModelOff_Flies()
        {
            FactionState st;
            WorldModel world = FuelWorld(out st, orePrice: 50);
            FactionTuning t = GateTuning();
            t.FuelDrawMode = FuelDrawModes.Off;

            TickFuel(world, st, t);

            Assert.Single(st.PendingFleets);
        }

        /// <summary>Never on absent data: a hull with no scanned mass cannot be priced, so it flies.</summary>
        [Fact]
        public void FuelCeiling_AnUnpricedHull_Flies()
        {
            FactionState st;
            WorldModel world = FuelWorld(out st, orePrice: 50, pricedHull: false);

            TickFuel(world, st, GateTuning());

            Assert.Single(st.PendingFleets);
        }

        // ---- FAC-393: a refused vector can still ride a later convoy this cycle ------------------

        /// <summary>A light store top-up (urgency 1.5) is tried first and refused. A catalogue want
        /// (urgency 1.0) for the same destination follows with a load that fills its hull. The refused
        /// vector must be foldable into that convoy as a pickup; claimed-on-take, it stayed claimed and
        /// waited a whole cycle for nothing.</summary>
        [Fact]
        public void ARefusedVector_IsFoldedIntoALaterConvoyToTheSameDestination()
        {
            var world = MakeWorld();
            var st = MakeState();
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["Big"] = 900_000;
            cat.CargoLitersByPrefab["Mid"] = 5_000;

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ingot", "Dest", 10, DemandClass.StoreStock);
            PostItemDemand(world, "F", "Ore", "Dest", 1000, DemandClass.Catalog);

            var s1 = AddNode(world, "S1");
            s1.Position = new Vec3(10, 0, 0);
            s1.Inventory["Ingot"] = 10;
            s1.ShipStock["Big"] = 1;

            var s2 = AddNode(world, "S2");
            s2.Position = new Vec3(20, 0, 0);
            s2.Inventory["Ore"] = 1000;
            s2.ShipStock["Mid"] = 1;

            // No right-sized fetch: with it on, the Ingot convoy plans the Ore in as an extra, counts
            // S2's hull as right-sized and fetches it -- a fold by another route, which would pass this
            // test without the release.
            FactionTuning t = GateTuning();
            t.HaulSmallHullSearchNodes = 0;
            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1).Set("Ingot", 1))
                .Tick(new List<FactionState> { st }, world, t, log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Mid", convoy.ShipPrefabId);
            Assert.Equal("S2", convoy.OriginNodeId);
            Assert.Equal(FleetStopKind.Pickup, convoy.CurrentStopKind);
            Assert.Equal("Ingot", convoy.PickupItemKey);
            Assert.Equal(1, s1.ShipStock["Big"]);
            Assert.Contains(log, l => l.Contains("HAUL_LOAD_GATED") && l.Contains("Ingot") && l.Contains("reason=light"));
        }

        /// <summary>No right-sized hull anywhere: the vector waits and the big hull stays home.</summary>
        [Fact]
        public void HullFit_OnlyABigHullAnywhere_Waits()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: null);

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            Assert.Empty(st.PendingFleets);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Big"]);
            Assert.Contains(log, l => l.Contains("HAUL_LOAD_GATED") && l.Contains("prefab=Big"));
            Assert.DoesNotContain(log, l => l.Contains("HAUL_BLOCKED"));
        }

        /// <summary>A search width of 0 skips the fetch: with a right-sized hull next door the vector
        /// still waits.</summary>
        [Fact]
        public void HullFit_SearchWidthZero_DoesNotFetch()
        {
            FactionState st;
            WorldModel world = BigHullOnlyWorld(out st, smallAt: "Donor");
            FactionTuning t = GateTuning();
            t.HaulSmallHullSearchNodes = 0;

            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, t, new List<string>());

            Assert.Empty(st.PendingFleets);
            Assert.Equal(1, world.Graph.Nodes["Donor"].ShipStock["Small"]);
        }

        [Fact]
        public void LoadGate_FullHullAgainstAHugeWant_StillFlies()
        {
            // The other side: a convoy that closes almost none of a vast want but fills the hold is
            // exactly what haulage is for. A gate that stopped this would have broken haulage rather
            // than fixed it -- p90 fill was 0.991 in the live baseline, so this population is real.
            FactionState st;
            WorldModel world = DribbleWorld(out st, sourceStock: 3000, want: 263_731);
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["RealHull"] = 3375;

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, GateTuning(), log);

            Assert.Single(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("HAUL_LOAD_GATED"));
        }

        [Fact]
        public void LoadGate_Disabled_RestoresThePreGateBehaviour()
        {
            // HaulMinLoadFraction = 0 must be an exact off switch, so the gate can be backed out live
            // from tuning.json without a deploy if the threshold turns out wrong.
            FactionState st;
            WorldModel world = DribbleWorld(out st, sourceStock: 40, want: 13_334);
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["RealHull"] = 3375;

            FactionTuning t = GateTuning();
            t.HaulMinLoadFraction = 0;

            var log = new List<string>();
            new OperationsManager(itemVolumes: new FakeVolumeCatalogue().Set("Ore", 1))
                .Tick(new List<FactionState> { st }, world, t, log);

            Assert.Single(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("HAUL_LOAD_GATED"));
        }

        [Fact]
        public void LoadGate_UnknownItemVolume_Flies()
        {
            // The gate must never refuse on absent data: with no volume catalogue the planned litres
            // are 0, which would read as an empty convoy and ground the whole network.
            FactionState st;
            WorldModel world = DribbleWorld(out st, sourceStock: 40, want: 13_334);
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab["RealHull"] = 3375;

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, GateTuning(), log);

            Assert.Single(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("HAUL_LOAD_GATED"));
        }

        /// <summary>Tuning with FAC-232's load gate switched off, for tests whose subject is
        /// something else entirely. <see cref="FakePrefabCatalogue.CargoLiters"/> defaults to
        /// 1,000,000 L — two orders of magnitude past any real hull — so every fixture load here
        /// reads as a near-empty convoy and the gate would refuse it for reasons that have nothing
        /// to do with what the test is asserting. The gate has its own tests below; these do not
        /// need to re-exercise it.</summary>
        private static FactionTuning TuningWithoutLoadGate()
        {
            FactionTuning t = TestSupport.Tuning();
            t.HaulMinLoadFraction = 0;
            return t;
        }

        [Fact]
        public void Haul_TwoItemsFromOneSource_LoadTogetherWithoutAnExtraStop()
        {
            // The convoy is already standing at the source, so a second item matched from the same
            // node costs it no stop at all — one convoy, both items in the hold, straight to Dest.
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);
            PostItemDemand(world, "F", "Ingot", "Dest", 50);

            var source = AddNode(world, "Source");
            source.Position = new Vec3(10, 0, 0);
            source.Inventory["Ore"] = 100;
            source.Inventory["Ingot"] = 50;
            StockHaulShips(source, 1);

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3).Set("Ingot", 2);
            var log = new List<string>();
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Dest", convoy.TargetNodeId);
            Assert.Equal(FleetStopKind.Service, convoy.CurrentStopKind);
            Assert.Null(convoy.RemainingStops);
            Assert.Equal(100, convoy.Cargo["Ore"]);
            Assert.Equal(50, convoy.Cargo["Ingot"]);
            Assert.Contains(log, l => l.Contains("consolidated=1 pickupStops=0"));
        }

        /// <summary>A consolidated hold delivers two items; the resolution line names each with its
        /// own quantity, so deliveries can be counted per item without joining back to the dispatch.
        /// The per-item figures sum to <c>delivered=</c>.</summary>
        [Fact]
        public void HaulResolved_NamesEveryItemTheHoldDelivered()
        {
            var world = MakeWorld();
            var st = MakeState();
            var states = new List<FactionState> { st };
            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);
            PostItemDemand(world, "F", "Ingot", "Dest", 50);
            var source = AddNode(world, "Source");
            source.Position = new Vec3(10, 0, 0);
            source.Inventory["Ore"] = 100;
            source.Inventory["Ingot"] = 50;
            StockHaulShips(source, 1);

            FactionTuning tuning = TuningWithoutLoadGate();
            var volumes = new FakeVolumeCatalogue().Set("Ore", 3).Set("Ingot", 2);
            new OperationsManager(itemVolumes: volumes).Tick(states, world, tuning, new List<string>());

            var log = new List<string>();
            for (int i = 0; i < 6 && st.PendingFleets.Any(f => !f.Resolved); i++)
            {
                world.UtcNow = world.UtcNow.AddHours(1);
                PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);
            }

            string line = Assert.Single(log.FindAll(l => l.Contains("HAUL_RESOLVED")));
            Assert.Contains("delivered=150", line);
            Assert.EndsWith(" items=Ingot=50,Ore=100", line);
        }

        [Fact]
        public void Haul_LocalFold_AccountsForEveryUnitTheConvoyCarries()
        {
            // Same topology as the test above. The line's own x<qty> covers the primary vector
            // alone, so before foldedQty a per-item total summed from the log silently lost
            // everything consolidation absorbed. The assertion is the accounting identity —
            // reported units == units actually in the hold — not the presence of a field.
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);
            PostItemDemand(world, "F", "Ingot", "Dest", 50);

            var source = AddNode(world, "Source");
            source.Position = new Vec3(10, 0, 0);
            source.Inventory["Ore"] = 100;
            source.Inventory["Ingot"] = 50;
            StockHaulShips(source, 1);

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3).Set("Ingot", 2);
            var log = new List<string>();
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            long carried = convoy.Cargo["Ore"] + convoy.Cargo["Ingot"];
            Assert.Equal(150, carried);

            string line = Assert.Single(log.Where(l => l.Contains("HAUL_DISPATCHED")));
            Assert.Equal(carried, ParseQty(line, "x") + ParseQty(line, "foldedQty="));
            // Nothing is queued for a later stop -- this convoy never leaves the source unloaded.
            Assert.DoesNotContain("queuedQty=", line, StringComparison.Ordinal);

            // The hold is MIXED -- 100 Ore at 3 L and 50 Ingot at 2 L -- which is exactly why the
            // line carries litres and not just units: 150 units cannot be converted to litres by
            // any single volume, so a unit count alone can never yield a load factor.
            Assert.Equal(400, ParseQty(line, "loadL="));
            // The fake catalogue's default hull (FakePrefabCatalogue.CargoLiters), so this asserts
            // the capacity actually reaches the line rather than restating a local constant.
            Assert.Equal(1_000_000, ParseQty(line, "capL="));
            Assert.DoesNotContain("planL=", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Haul_RemotePickup_ReportsTheUnitsQueuedForTheStop_NotAsLoaded()
        {
            // The other half of the same accounting, and the reason the two are separate fields:
            // a pickup stop's units are a PLAN, re-capped against stock and the leg's mass ceiling
            // only when the convoy gets there. Summing them into the loaded figure would report a
            // plan as though it were cargo.
            var world = MakeWorld();
            var log = new List<string>();
            FactionState st = TickTwoSourcesOneDestination(120, world,
                new FakeVolumeCatalogue().Set("Ore", 3), TuningWithoutLoadGate(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal(60, convoy.Cargo["Ore"]);
            Assert.Equal(40, convoy.PickupQuantity);

            string line = Assert.Single(log.Where(l => l.Contains("HAUL_DISPATCHED")));
            Assert.Equal(60, ParseQty(line, "x"));
            Assert.Equal(40, ParseQty(line, "queuedQty="));
            // Far's 40 are not aboard yet, so they must not be reported as folded into the hold.
            Assert.DoesNotContain("foldedQty=", line, StringComparison.Ordinal);

            // Near's 60 are aboard at 3 L each; Far's 40 are still only planned, so they show up in
            // planL and never in loadL. Reporting the plan as load is the whole reason these differ.
            Assert.Equal(180, ParseQty(line, "loadL="));
            Assert.Equal(300, ParseQty(line, "planL="));
        }

        /// <summary>Reads the integer following <paramref name="token"/> in a log line. "x" finds the
        /// primary quantity, which the line writes as " x123" straight after the item key.</summary>
        private static long ParseQty(string line, string token)
        {
            string needle = token == "x" ? " x" : token;
            int at = line.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, "no '" + token + "' in: " + line);
            int i = at + needle.Length;
            long value = 0;
            bool any = false;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9')
            {
                value = value * 10 + (line[i] - '0');
                i++;
                any = true;
            }
            Assert.True(any, "no digits after '" + token + "' in: " + line);
            return value;
        }

        [Fact]
        public void Haul_TwoPickups_AreOrderedIntoTheShortestRoute_NotNearestFirst()
        {
            // Laid out so nearest-first and shortest-route disagree. From Start, SourceB is the
            // nearer detour (300.2 km vs SourceA's 310), so it is picked up first by the greedy
            // seed — but SourceB sits almost on the way to Dest while SourceA sits behind Start,
            // making Start -> A -> B -> Dest (~1,501 km) far shorter than Start -> B -> A -> Dest
            // (~1,750 km). Asserting the routed order is A then B is therefore a claim about the
            // route, not a restatement of the selection order.
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 700, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 300);

            var start = AddNode(world, "Start");
            start.Position = new Vec3(0, 0, 0);
            start.Inventory["Ore"] = 100;
            StockHaulShips(start, 1);

            var sourceB = AddNode(world, "SourceB");
            sourceB.Position = new Vec3(300, 12, 0);
            sourceB.Inventory["Ore"] = 100;

            var sourceA = AddNode(world, "SourceA");
            sourceA.Position = new Vec3(0, -310, 0);
            sourceA.Inventory["Ore"] = 100;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 1);
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), new List<string>());

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("SourceA", convoy.TargetNodeId);
            Assert.Equal(new[] { "SourceB", "Dest" }, convoy.RemainingStops.Select(stop => stop.NodeId).ToArray());
        }

        [Fact]
        public void Haul_VectorThatAlreadyHasItsOwnConvoy_IsNotAlsoFoldedOntoALaterOne()
        {
            // Found live 2026-08-31 on the v0.100.0 deploy, not by any test here. IMPR dispatched
            // four Ingot:Stone convoys to N005 in one cycle, and the LAST of them planned pickups at
            // N836 and N936 — the source nodes of two convoys dispatched moments earlier in the same
            // cycle. Two convoys were flying to collect the same stock; the second one arrives at an
            // emptied node and burns a leg for nothing.
            //
            // The loop's cursor is not a claim. It stops a vector being re-dispatched, but a LATER
            // vector looking backwards for company sees an unconsumed index and folds it. So this
            // needs the earlier vector to dispatch WITHOUT folding the later one — arranged here by
            // giving Near a hull too small to take Far's cargo — and then the later one to be
            // offered it.
            var world = MakeWorld();
            var catalogue = (FakePrefabCatalogue)world.PrefabCatalogue;
            catalogue.CargoLitersByPrefab["Tiny"] = 150;   // fits Near's own 100, not Far's 100 too
            catalogue.CargoLitersByPrefab["Big"]  = 100000;

            var st = MakeState();
            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 300);

            var near = AddNode(world, "Near");
            near.Position = new Vec3(10, 0, 0);
            near.Inventory["Ore"] = 100;
            StockHaulShips(near, 1, "Tiny");

            var far = AddNode(world, "Far");
            far.Position = new Vec3(20, 0, 0);
            far.Inventory["Ore"] = 100;
            StockHaulShips(far, 1, "Big");

            var volumes = new FakeVolumeCatalogue().Set("Ore", 1);
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), new List<string>());

            // Two convoys, one per source — Near could not fold Far (no room), and Far must not fold
            // Near, which already has its own.
            Assert.Equal(2, st.PendingFleets.Count);
            PendingFleetAction fromFar = st.PendingFleets.Single(f => f.OriginNodeId == "Far");
            Assert.Equal("Dest", fromFar.TargetNodeId);
            Assert.Equal(FleetStopKind.Service, fromFar.CurrentStopKind);
            Assert.Null(fromFar.RemainingStops);
            // The discriminator: before the fix this fleet carried a Pickup stop at Near, whose
            // stock the other convoy was already loading.
            Assert.DoesNotContain(st.PendingFleets,
                f => f.RemainingStops != null && f.RemainingStops.Any(stop => stop.NodeId == "Near"));
            Assert.Equal(100, st.PendingFleets.Single(f => f.OriginNodeId == "Near").Cargo["Ore"]);
            Assert.Equal(100, fromFar.Cargo["Ore"]);
        }

        [Fact]
        public void Haul_FetchedHull_CollectsTheOtherSourcesOnTheSameItinerary()
        {
            // The tier-2 path: the vector's own source has no hull, so one is fetched from Depot
            // and flies a Pickup itinerary. Consolidation applies there too — the radius is measured
            // from Primary (where loading starts), not from Depot (a hull it merely collects), so
            // Second is folded in even though it is far from Depot.
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var primary = AddNode(world, "Primary");
            primary.Position = new Vec3(10, 0, 0);
            primary.Inventory["Ore"] = 60;

            var second = AddNode(world, "Second");
            second.Position = new Vec3(130, 0, 0);
            second.Inventory["Ore"] = 40;

            // 2,000 km from Primary: far outside the consolidation radius, so it can only be
            // reached as the hull source, never folded in as a pickup of its own.
            var depot = AddNode(world, "Depot");
            depot.Position = new Vec3(0, 2000, 0);
            StockHaulShips(depot, 1);

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            var log = new List<string>();
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, TuningWithoutLoadGate(), log);

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Depot", convoy.OriginNodeId);
            Assert.Equal("Primary", convoy.TargetNodeId);
            Assert.Equal(FleetStopKind.Pickup, convoy.CurrentStopKind);
            Assert.Equal(new[] { "Second", "Dest" }, convoy.RemainingStops.Select(stop => stop.NodeId).ToArray());
            Assert.Equal(FleetStopKind.Pickup, convoy.RemainingStops[0].Kind);
            Assert.Equal(40, convoy.RemainingStops[0].PickupQuantity);
            Assert.Contains(log, l => l.Contains("via_pickup=Depot") && l.Contains("consolidated=1 pickupStops=1"));
        }

        [Fact]
        public void Haul_PickupNodeBudget_CapsHowManySourcesOneConvoyCollectsFrom()
        {
            var world = MakeWorld();
            FactionTuning tuning = TuningWithoutLoadGate();
            tuning.HaulMaxPickupNodes = 2; // the loading node plus exactly one detour

            var st = MakeState();
            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 300);

            var start = AddNode(world, "Start");
            start.Position = new Vec3(10, 0, 0);
            start.Inventory["Ore"] = 100;
            StockHaulShips(start, 1);

            var near = AddNode(world, "Near");
            near.Position = new Vec3(20, 0, 0);
            near.Inventory["Ore"] = 100;

            var alsoNear = AddNode(world, "AlsoNear");
            alsoNear.Position = new Vec3(30, 0, 0);
            alsoNear.Inventory["Ore"] = 100;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 1);
            new OperationsManager(itemVolumes: volumes)
                .Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Near", convoy.TargetNodeId);
            // AlsoNear is inside the radius and fits the hold; only the node budget refuses it.
            FleetStop delivery = Assert.Single(convoy.RemainingStops);
            Assert.Equal("Dest", delivery.NodeId);
        }

        [Fact]
        public void Haul_ConsolidatedConvoy_CollectsEveryStopAndDeliversTheWholeHold()
        {
            // End to end through the real resolver: the queued Pickup legs actually draw down each
            // source's stock and the single Service stop credits the destination with all of it.
            var world = MakeWorld();
            FactionTuning tuning = TuningWithoutLoadGate();
            var st = MakeState();
            var states = new List<FactionState> { st };

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);

            var near = AddNode(world, "Near");
            near.Position = new Vec3(1, 0, 0);
            near.Inventory["Ore"] = 60;
            StockHaulShips(near, 1);

            var far = AddNode(world, "Far");
            far.Position = new Vec3(120, 0, 0);
            far.Inventory["Ore"] = 40;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 3);
            new OperationsManager(itemVolumes: volumes).Tick(states, world, tuning, new List<string>());

            var log = new List<string>();
            for (int i = 0; i < 6 && st.PendingFleets.Any(f => !f.Resolved); i++)
            {
                world.UtcNow = world.UtcNow.AddHours(1);
                PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);
            }

            Assert.Equal(0, near.Inventory["Ore"]);
            Assert.Equal(0, far.Inventory["Ore"]);
            Assert.Equal(100, dest.Inventory["Ore"]);
            Assert.Contains(log, l => l.Contains("HAUL_PICKUP") && l.Contains("@ Far") && l.Contains("loaded=40"));
            Assert.Contains(log, l => l.Contains("HAUL_RESOLVED") && l.Contains("delivered=100")
                                      && l.Contains(" items=Ore=100"));
        }

        [Fact]
        public void Haul_ConvoyStillCollecting_ClaimsEveryItemItIsScheduledToPickUp()
        {
            // A consolidated convoy spends its first legs at pickup nodes with the delivery still
            // queued. Every item it has aboard or is scheduled to collect claims that delivery, or
            // the next pass matches the same shortfall again and dispatches a duplicate.
            var world = MakeWorld();
            var st = MakeState();

            var dest = AddNode(world, "Dest");
            dest.Position = new Vec3(0, 0, 0);
            PostItemDemand(world, "F", "Ore", "Dest", 100);
            PostItemDemand(world, "F", "Ingot", "Dest", 50);

            var near = AddNode(world, "Near");
            near.Position = new Vec3(1, 0, 0);
            near.Inventory["Ore"] = 100;
            StockHaulShips(near, 2); // a second hull, so a duplicate dispatch is possible if unclaimed

            var far = AddNode(world, "Far");
            far.Position = new Vec3(120, 0, 0);
            far.Inventory["Ingot"] = 50;
            far.Inventory["Ore"] = 100;

            var volumes = new FakeVolumeCatalogue().Set("Ore", 1).Set("Ingot", 1);
            var tuning = TuningWithoutLoadGate();
            var manager = new OperationsManager(itemVolumes: volumes);
            manager.Tick(new List<FactionState> { st }, world, tuning, new List<string>());
            PendingFleetAction convoy = Assert.Single(st.PendingFleets);
            Assert.Equal("Far", convoy.TargetNodeId);
            Assert.Equal("Ingot", convoy.PickupItemKey);

            // Second pass, nothing resolved in between: both (Dest, Ore) and (Dest, Ingot) are
            // already spoken for by the convoy above.
            manager.Tick(new List<FactionState> { st }, world, tuning, new List<string>());
            Assert.Single(st.PendingFleets);
        }

        // ---- gas in tanks: hull choice, the load gate and the log -----------------------------

        private const string H2Key = "Gas:Hydrogen";

        private static readonly IItemVolumeCatalogue GasVolumes = new FakeVolumeCatalogue().Set(H2Key, 1.0);

        /// <summary>Dest wants <paramref name="want"/> hydrogen; Source (10 km off) holds plenty and no
        /// hull. Hulls are placed per test.</summary>
        private static WorldModel GasWorld(out FactionState st, long want,
            DemandClass demandClass = DemandClass.Unclassified)
        {
            var world = MakeWorld();
            st = MakeState();
            AddNodeAt(world, "Dest", new Vec3(0, 0, 0));
            PostItemDemand(world, "F", H2Key, "Dest", want, demandClass);
            var source = AddNodeAt(world, "Source", new Vec3(10, 0, 0));
            source.Inventory[H2Key] = 90_000_000;
            return world;
        }

        private static void Hull(WorldModel world, string nodeId, string prefab, long holdLitres, long h2TankLitres)
        {
            var cat = (FakePrefabCatalogue)world.PrefabCatalogue;
            cat.CargoLitersByPrefab[prefab] = holdLitres;
            if (h2TankLitres > 0)
                cat.GasTanksByPrefab[prefab] = new Dictionary<string, long> { { H2Key, h2TankLitres } };
            world.Graph.Nodes[nodeId].ShipStock[prefab] = 1;
        }

        private static List<string> TickGas(OperationsManager manager, WorldModel world, FactionState st)
        {
            FactionTuning tuning = TestSupport.Tuning();
            world.Tuning = tuning;
            var log = new List<string>();
            manager.Tick(new List<FactionState> { st }, world, tuning, log);
            return log;
        }

        private static List<string> TickGas(WorldModel world, FactionState st)
        {
            return TickGas(new OperationsManager(itemVolumes: GasVolumes), world, st);
        }

        private static string Dispatched(List<string> log)
        {
            return Assert.Single(log, l => l.Contains(" HAUL_DISPATCHED "));
        }

        /// <summary>The tankless hull is the better fit by hold (600 kL against 1 ML for a 500 kL
        /// load), but it has no tank for hydrogen and is not a candidate at all: the tanker flies and
        /// the tankless hull stays parked.</summary>
        [Fact]
        public void GasInTanks_AGasVectorNeverPicksATanklessHull()
        {
            FactionState st;
            WorldModel on = GasWorld(out st, 500_000);
            Hull(on, "Source", "Tankless", 600_000, 0);
            Hull(on, "Source", "Tanker", 1_000_000, 2_000_000);
            List<string> log = TickGas(on, st);

            Assert.Contains(" prefab=Tanker ", Dispatched(log));
            PendingFleetAction haul = HaulTo(st, "Dest");
            Assert.Equal("Tanker", haul.ShipPrefabId);
            Assert.Equal(500_000L, haul.Cargo[H2Key]);
            Assert.Equal(1, on.Graph.Nodes["Source"].ShipStock["Tankless"]);
        }

        /// <summary>Fit ranks tank room, not the hold. SmallTank has the smaller hold's opposite: a
        /// 100 kL hold and a 600 kL tank; BigTank a 900 kL hold and a 5 ML tank. By hold only BigTank
        /// would take 500 kL; by tank both do and the smaller wins. A load only BigTank's tank takes
        /// goes to BigTank.</summary>
        [Fact]
        public void GasInTanks_FitPicksTheSmallestTankThatTakesTheLoad()
        {
            Func<long, string> chosen = want =>
            {
                FactionState st;
                WorldModel world = GasWorld(out st, want);
                Hull(world, "Source", "SmallTank", 100_000, 600_000);
                Hull(world, "Source", "BigTank", 900_000, 5_000_000);
                TickGas(world, st);
                return HaulTo(st, "Dest").ShipPrefabId;
            };

            Assert.Equal("SmallTank", chosen(500_000));
            Assert.Equal("BigTank", chosen(1_000_000));
        }

        /// <summary>The light-load rule for tanks: the fraction is of tank room, never of the hold.
        /// 3 ML in a 61 ML tank is 4.9%, under HaulMinLoadFraction, so an unurgent load waits, though
        /// against the same hull's 1 ML hold it would read 300%. Against a 20 ML tank it reads 15% and
        /// flies.</summary>
        [Fact]
        public void GasInTanks_TheLightLoadGateMeasuresTankRoom()
        {
            FactionState st;
            WorldModel world = GasWorld(out st, 3_000_000);
            Hull(world, "Source", "BigTank", 1_000_000, 61_000_000);
            List<string> log = TickGas(world, st);

            Assert.Empty(st.PendingFleets);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["BigTank"]);
            string gated = Assert.Single(log, l => l.Contains(" HAUL_LOAD_GATED "));
            Assert.Contains(" gasL=3000000 gasCapL=61000000 fill=0.049 ", gated);
            Assert.DoesNotContain(" capL=", gated);

            FactionState fitSt;
            WorldModel fit = GasWorld(out fitSt, 3_000_000);
            Hull(fit, "Source", "MidTank", 1_000_000, 20_000_000);
            TickGas(fit, fitSt);
            Assert.Equal(3_000_000L, HaulTo(fitSt, "Dest").Cargo[H2Key]);
        }

        /// <summary>A light gas load fetches a right-sized tank from a donor the way a light solid
        /// load fetches a right-sized hold: sized by tank room, so the 5 ML tanker 20 km off is sent
        /// and the 61 ML one standing at the source is passed over.</summary>
        [Fact]
        public void GasInTanks_ALightGasLoadFetchesARightSizedTank()
        {
            FactionState st;
            WorldModel world = GasWorld(out st, 3_000_000);
            Hull(world, "Source", "BigTank", 1_000_000, 61_000_000);
            AddNodeAt(world, "Donor", new Vec3(20, 0, 0));
            Hull(world, "Donor", "SmallTank", 1_000_000, 5_000_000);
            List<string> log = TickGas(world, st);

            string line = Dispatched(log);
            Assert.Contains(" via_pickup=Donor ", line);
            Assert.Contains(" prefab=SmallTank passed_over=BigTank ", line);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["BigTank"]);
        }

        /// <summary>A faction whose cargo hulls have no hydrogen tank cannot carry the vector by any
        /// tier. It is blocked once, with the reason named, and no hull is taken: before, a tankless
        /// hull was consumed, loaded nothing and went back on every pass. No Ship:Trade demand is
        /// staged either — it names a hold, not a tank.</summary>
        [Fact]
        public void GasInTanks_OnlyTanklessHulls_BlocksOnceWithAReasonAndTakesNoHull()
        {
            FactionState st;
            WorldModel world = GasWorld(out st, 500_000);
            Hull(world, "Source", "Tankless", 1_000_000, 0);
            AddNodeAt(world, "Elsewhere", new Vec3(30, 0, 0));
            Hull(world, "Elsewhere", "AlsoTankless", 1_000_000, 0);
            var manager = new OperationsManager(itemVolumes: GasVolumes);

            List<string> first = TickGas(manager, world, st);
            List<string> second = TickGas(manager, world, st);

            string blocked = Assert.Single(first, l => l.Contains(" HAUL_BLOCKED "));
            Assert.Equal("F HAUL_BLOCKED Gas:Hydrogen x500000 src=Source dest=Dest reason=no_gas_tank", blocked);
            Assert.DoesNotContain(second, l => l.Contains(" HAUL_BLOCKED "));
            Assert.DoesNotContain(first.Concat(second), l => l.Contains("SHIP_DEMAND") || l.Contains("HAUL_DISPATCHED"));
            Assert.Empty(st.PendingFleets);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Tankless"]);
            Assert.Equal(1, world.Graph.Nodes["Elsewhere"].ShipStock["AlsoTankless"]);
            Assert.Empty(world.Demand.SnapshotShipDemands("F"));
        }

        /// <summary>The pickup tier skips a donor whose hulls have no tank for the gas, even the
        /// nearer one, and plans only what the fetched tank holds: the 5 ML vector is logged as the
        /// 2 ML that fits, with <c>gasCapL=</c> naming the tank room.</summary>
        [Fact]
        public void GasInTanks_ThePickupTierFetchesATankerAndLogsWhatFits()
        {
            FactionState st;
            WorldModel world = GasWorld(out st, 5_000_000, DemandClass.Fuel);
            AddNodeAt(world, "Near", new Vec3(12, 0, 0));
            Hull(world, "Near", "Tankless", 1_000_000, 0);
            AddNodeAt(world, "Far", new Vec3(40, 0, 0));
            Hull(world, "Far", "Tanker", 1_000_000, 2_000_000);
            List<string> log = TickGas(world, st);

            string line = Dispatched(log);
            Assert.Contains(" HAUL_DISPATCHED Gas:Hydrogen x2000000 Source -> Dest via_pickup=Far ", line);
            Assert.Contains(" prefab=Tanker ", line);
            Assert.EndsWith(" gasL=0 planL=2000000 gasCapL=2000000 urgency=2.5", line);
            PendingFleetAction haul = Assert.Single(st.PendingFleets);
            Assert.Equal(2_000_000L, haul.PickupQuantity);
            Assert.Equal(1, world.Graph.Nodes["Near"].ShipStock["Tankless"]);
        }

        /// <summary>The direct tier's line names the gas aboard and the tank room, not the hold.</summary>
        [Fact]
        public void GasInTanks_HaulDispatchedShowsGasAndTankRoom()
        {
            FactionState st;
            WorldModel on = GasWorld(out st, 500_000);
            Hull(on, "Source", "Tanker", 1_000_000, 2_000_000);
            string onLine = Dispatched(TickGas(on, st));
            Assert.EndsWith(" prefab=Tanker gasL=500000 gasCapL=2000000 urgency=1", onLine);
            Assert.DoesNotContain(" capL=", onLine);
        }

        /// <summary>The staged cycle — the path the host runs — makes the same two decisions as the
        /// synchronous tick: the tanker for a gas vector, and a named block with no hull taken when
        /// the faction owns no tank for the gas.</summary>
        [Fact]
        public void GasInTanks_TheStagedCycleChoosesTheSameWay()
        {
            FactionState st;
            WorldModel world = GasWorld(out st, 500_000);
            Hull(world, "Source", "Tankless", 600_000, 0);
            Hull(world, "Source", "Tanker", 1_000_000, 2_000_000);
            RunStagedGasSweep(world, st);
            Assert.Equal("Tanker", HaulTo(st, "Dest").ShipPrefabId);
            Assert.Equal(1, world.Graph.Nodes["Source"].ShipStock["Tankless"]);

            FactionState noTankSt;
            WorldModel noTank = GasWorld(out noTankSt, 500_000);
            Hull(noTank, "Source", "Tankless", 1_000_000, 0);
            List<string> log = RunStagedGasSweep(noTank, noTankSt);
            Assert.Contains(log, l => l.EndsWith(" reason=no_gas_tank", StringComparison.Ordinal));
            Assert.Empty(noTankSt.PendingFleets);
            Assert.Equal(1, noTank.Graph.Nodes["Source"].ShipStock["Tankless"]);
        }

        private static List<string> RunStagedGasSweep(WorldModel world, FactionState st)
        {
            world.Tuning = TestSupport.Tuning();
            var manager = new OperationsManager(itemVolumes: GasVolumes);
            var states = new List<FactionState> { st };
            var log = new List<string>();
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            manager.GetType().GetMethod("BeginSweepForTesting", flags).Invoke(manager, new object[] { states });
            System.Reflection.MethodInfo step = manager.GetType().GetMethod("StepSweepOnceForTesting", flags);
            for (int i = 0; !(bool)step.Invoke(manager, new object[] { states, world, world.Tuning, log }); i++)
                Assert.True(i < 1000, "Operations sweep did not complete");
            return log;
        }
    }
}
