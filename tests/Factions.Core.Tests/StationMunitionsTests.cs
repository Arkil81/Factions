using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="StationMunitions"/> (FAC-441): a materialised station's magazines come out
    /// of its node's stock, only what was drawn is ever credited back, what a read finds gone is
    /// spent, and a restart hands back what the unsaved grids held.
    /// </summary>
    public class StationMunitionsTests
    {
        private const string Nato = "NATO_25x184mm";
        private const string Fort = "Fort";
        private const string Freighter = "Freighter";

        /// <summary>A station design whose guns hold 40 NATO magazines, and a freighter with one
        /// small gun on the same magazine.</summary>
        private sealed class StationCatalogue : IPrefabCatalogue, IMunitionPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return prefabId == Freighter ? 100000 : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 100000; }

            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                if (prefabId == Fort) return new[] { new WeaponBand { Dps = 200, RangeM = 2000 } };
                if (prefabId == Freighter) return new[] { new WeaponBand { Dps = 10, RangeM = 2000 } };
                return new WeaponBand[0];
            }

            public IReadOnlyList<MunitionSlot> MunitionSlots(string prefabId)
            {
                if (prefabId == Fort)
                    return new[] { new MunitionSlot { MagazineKey = Nato, RangeM = 2000, Dps = 200, MagazinesPerSecond = 0f, FullLoad = 40 } };
                if (prefabId == Freighter)
                    return new[] { new MunitionSlot { MagazineKey = Nato, RangeM = 2000, Dps = 10, MagazinesPerSecond = 0f, FullLoad = 8 } };
                return null;
            }
        }

        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = new StationCatalogue();
            w.Tuning.MunitionDrawMode = MunitionDrawModes.Draw;
            return w;
        }

        private static SpatialNode Station(WorldModel w, string id = "E1", long stock = 0)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.6, id);
            n.OwnerFactionTag = "RVN";
            n.StableStationId = 5;
            n.StationPrefabId = Fort;
            if (stock > 0) n.Inventory[Nato] = stock;
            return n;
        }

        private static long Stock(SpatialNode n)
        {
            long have;
            n.Inventory.TryGetValue(Nato, out have);
            return have;
        }

        private static long Record(SpatialNode n)
        {
            long held;
            return n.StationMunitions != null && n.StationMunitions.TryGetValue(Nato, out held) ? held : 0;
        }

        private static Dictionary<string, long> Load(long nato)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal) { { Nato, nato } };
        }

        // ---- sizing -----------------------------------------------------------------------------

        [Fact]
        public void FullLoad_IsTheDesignsSortieLoad()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);

            Dictionary<string, long> load = StationMunitions.FullLoad(n, w.PrefabCatalogue, w.Tuning);

            Assert.Equal(FleetMunitions.FullLoad(w.PrefabCatalogue, new[] { Fort }, w.Tuning), load);
            Assert.Equal(40, load[Nato]);
        }

        [Fact]
        public void FullLoad_UnfoundedOrUnknownDesign_IsEmpty()
        {
            WorldModel w = World();
            SpatialNode unfounded = Station(w, "E1");
            unfounded.StableStationId = 0;
            SpatialNode unknown = Station(w, "E2");
            unknown.StationPrefabId = "NoSuchDesign";

            Assert.Empty(StationMunitions.FullLoad(unfounded, w.PrefabCatalogue, w.Tuning));
            Assert.Empty(StationMunitions.FullLoad(unknown, w.PrefabCatalogue, w.Tuning));
        }

        // ---- drawing ----------------------------------------------------------------------------

        [Fact]
        public void Draw_TakesTheGapAsFarAsStockGoes_AndRecordsIt()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 30);

            Dictionary<string, long> drawn = StationMunitions.Draw(w, n, Load(40), null);

            Assert.Equal(30, drawn[Nato]);
            Assert.Equal(0, Stock(n));
            Assert.Equal(30, Record(n));
        }

        [Fact]
        public void Draw_TakesOnlyWhatIsNotAlreadyAboard()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 100);

            Dictionary<string, long> drawn = StationMunitions.Draw(w, n, Load(40), Load(25));

            Assert.Equal(15, drawn[Nato]);
            Assert.Equal(85, Stock(n));
        }

        [Fact]
        public void Draw_FullyLoaded_DrawsNothing()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 100);

            Assert.Empty(StationMunitions.Draw(w, n, Load(40), Load(40)));
            Assert.Equal(100, Stock(n));
            Assert.Null(n.StationMunitions);
        }

        [Fact]
        public void ReturnUnplaced_GivesBackNoMoreThanTheRecordHolds()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 10);
            StationMunitions.Draw(w, n, Load(10), null);

            long back = StationMunitions.ReturnUnplaced(w, n, Load(15));

            Assert.Equal(10, back);
            Assert.Equal(10, Stock(n));
            Assert.Null(n.StationMunitions);
        }

        // ---- reading ----------------------------------------------------------------------------

        [Fact]
        public void ApplyObserved_NeverRaisesTheRecord()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 40);
            StationMunitions.Draw(w, n, Load(40), null);

            long spent = StationMunitions.ApplyObserved(w, n, Load(55));

            Assert.Equal(0, spent);
            Assert.Equal(40, Record(n));
            Assert.Null(w.SnapshotPendingDemandHistory(n.Id));
        }

        [Fact]
        public void ApplyObserved_WhatWentIsSpent_AndBookedAsConsumptionAtTheNode()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 40);
            StationMunitions.Draw(w, n, Load(40), null);

            long spent = StationMunitions.ApplyObserved(w, n, Load(30));

            Assert.Equal(10, spent);
            Assert.Equal(30, Record(n));
            Assert.True(w.SnapshotPendingDemandHistory(n.Id)[Nato] > 0);
        }

        // ---- crediting --------------------------------------------------------------------------

        [Fact]
        public void Credit_HandsBackWhatIsAboard_NeverMoreThanWasDrawn()
        {
            // A grid reading more than the record — a pre-load that survived, or rounds a player
            // added — earns nothing beyond what the faction drew.
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 40);
            StationMunitions.Draw(w, n, Load(40), null);

            long spent;
            long credited = StationMunitions.Credit(w, n, Load(100), out spent);

            Assert.Equal(40, credited);
            Assert.Equal(0, spent);
            Assert.Equal(40, Stock(n));
            Assert.Null(n.StationMunitions);
        }

        [Fact]
        public void Credit_PartlyFired_ReturnsTheRest()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 40);
            StationMunitions.Draw(w, n, Load(40), null);

            long spent;
            long credited = StationMunitions.Credit(w, n, Load(12), out spent);

            Assert.Equal(12, credited);
            Assert.Equal(28, spent);
            Assert.Equal(12, Stock(n));
        }

        [Fact]
        public void Credit_GridAlreadyGone_ReturnsNothing()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 40);
            StationMunitions.Draw(w, n, Load(40), null);

            long spent;
            long credited = StationMunitions.Credit(w, n, new Dictionary<string, long>(), out spent);

            Assert.Equal(0, credited);
            Assert.Equal(40, spent);
            Assert.Equal(0, Stock(n));
            Assert.Null(n.StationMunitions);
        }

        [Fact]
        public void CreditAfterRestart_HandsEveryRecordBack()
        {
            WorldModel w = World();
            SpatialNode a = Station(w, "E1", 40);
            SpatialNode b = Station(w, "E2", 25);
            Station(w, "E3", 7);
            StationMunitions.Draw(w, a, Load(40), null);
            StationMunitions.Draw(w, b, Load(40), null);

            int nodes;
            long credited = StationMunitions.CreditAfterRestart(w, out nodes);

            Assert.Equal(65, credited);
            Assert.Equal(2, nodes);
            Assert.Equal(40, Stock(a));
            Assert.Equal(25, Stock(b));
            Assert.Null(a.StationMunitions);
            Assert.Null(b.StationMunitions);
        }

        // ---- reserve ----------------------------------------------------------------------------

        [Fact]
        public void CombatReserve_HoldsOneStationLoad_EvenWithNoHullParked()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);

            Dictionary<string, long> reserve = FleetMunitions.CombatReserve(n, w.PrefabCatalogue, w.Tuning);

            Assert.Equal(40, reserve[Nato]);
        }

        [Fact]
        public void ANonCombatDeparture_LeavesTheStationItsLoad()
        {
            WorldModel w = World();
            Station(w, stock: 45);
            var convoy = new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "RVN", OriginNodeId = "E1", ShipPrefabId = Freighter,
                ConvoySize = 1, FleetId = "F1",
            };

            FleetMunitions.Arm(ref convoy, w, w.Tuning, new List<string>());

            Assert.Equal(5, convoy.Munitions[Nato]);
        }

        // ---- persistence ------------------------------------------------------------------------

        private static SpatialNode RoundTrip(SpatialGraph graph, string id)
        {
            var st = new FactionState { Tag = "RVN", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("RVN"), FactionStore.DefaultLogCap));
                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("RVN"), reloaded);
                return reloaded.Nodes[id];
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void TheRecordSurvivesARestart_SoItCanBeHandedBack()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, stock: 40);
            StationMunitions.Draw(w, n, Load(40), null);

            SpatialNode after = RoundTrip(w.Graph, "E1");

            Assert.Equal(40, Record(after));
        }

        [Fact]
        public void NoRecord_WritesNothing()
        {
            WorldModel w = World();
            Station(w, stock: 40);

            BrainDoc doc = FactionStore.ToBrainDoc(new FactionState { Tag = "RVN", FactionId = 7, PersonalityId = "settler", RngSeed = 1 },
                                                   w.Graph.OwnedBy("RVN"), FactionStore.DefaultLogCap);

            Assert.DoesNotContain("StationMunitions", Newtonsoft.Json.JsonConvert.SerializeObject(doc));
        }
    }
}
