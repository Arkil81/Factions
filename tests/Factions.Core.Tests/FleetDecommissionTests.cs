using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Retiring a design from a faction's fleet (<see cref="FleetDecommission"/>, FAC-449):
    /// parked hulls of every version become their components at the node that held them, shelf hulls
    /// are taken off the shelf first, the lines naming the design go with it, and nothing flying or in
    /// repair is touched.</summary>
    public class FleetDecommissionTests
    {
        /// <summary>"Clipper" and "Clipper@2" are two versions of one design; "Tanto" is another.</summary>
        private sealed class Catalogue : IPrefabCatalogue, IPrefabVersionCatalogue
        {
            public string FamilyOf(string prefabId) { return prefabId == "Clipper@2" ? "Clipper" : prefabId; }
            public int VersionOf(string prefabId) { return prefabId == "Clipper@2" ? 2 : 1; }
            public string LatestFor(string prefabId, string factionTag) { return FamilyOf(prefabId) == "Clipper" ? "Clipper@2" : prefabId; }
            public long CargoLiters(string prefabId) { return 1000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static WorldModel World(out SpatialNode a, out SpatialNode b, out FactionState st)
        {
            WorldModel world = TestSupport.World();
            world.PrefabCatalogue = new Catalogue();
            var recipes = new ArmedYard.CostRecipes();
            recipes.CostByPrefab["Clipper"] = 40;
            recipes.CostByPrefab["Clipper@2"] = 50;
            recipes.CostByPrefab["Tanto"] = 70;
            world.Recipes = recipes;
            a = Node(world, "N1", 0);
            b = Node(world, "N2", 5000);
            st = new FactionState { Tag = "F", IsPlayerFaction = true };
            return world;
        }

        private static SpatialNode Node(WorldModel world, string id, double x)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = 7;
            return n;
        }

        /// <summary>PendingFleetAction is a struct: resolve the one in the list, not a copy.</summary>
        private static void Resolve(FactionState st)
        {
            PendingFleetAction f = st.PendingFleets[0];
            f.Resolved = true;
            st.PendingFleets[0] = f;
        }

        private static long Steel(WorldModel world, SpatialNode n)
        {
            return world.GetNodeInventory(n.Id, "Comp:Steel");
        }

        [Fact]
        public void EveryVersionOfTheDesign_IsDismantled_AndItsBomCreditedOnceAtTheNodeThatHeldIt()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            a.ShipStock["Clipper"] = 2;
            b.ShipStock["Clipper@2"] = 1;
            a.ShipStock["Tanto"] = 3;
            var log = new List<string>();

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 0, true, log);

            Assert.Equal(3, r.Dismantled);
            Assert.Equal(2 * 40 + 50, r.BomUnitsCredited);
            Assert.Equal(80, Steel(world, a));
            Assert.Equal(50, Steel(world, b));
            Assert.Equal(0, a.ShipStock["Clipper"]);
            Assert.Equal(0, b.ShipStock["Clipper@2"]);
            Assert.Equal(3, a.ShipStock["Tanto"]);   // another design is not touched
            Assert.Contains(log, l => l == "F SHIP_DECOMMISSIONED node=N1 prefab=Clipper count=2 bomCredited=80");
        }

        [Fact]
        public void AHullOnTheShelf_IsTakenOffItFirst_SoTheReservationNeverOutgrowsTheStock()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            a.ShipStock["Clipper"] = 3;
            a.ListedForSale["Clipper"] = 2;
            var log = new List<string>();

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 0, true, log);

            Assert.Equal(3, r.Dismantled);
            Assert.Equal(2, r.Delisted);
            Assert.Equal(0, a.ShipStock["Clipper"]);
            Assert.False(a.ListedForSale.ContainsKey("Clipper"));
            Assert.Contains(log, l => l.Contains("STORE_SHELF_RELEASED prefab=Clipper") && l.Contains("reason=decommission"));
        }

        [Fact]
        public void ACount_TrimsUnreservedHullsBeforeTheShelf()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            a.ShipStock["Clipper"] = 3;
            a.ListedForSale["Clipper"] = 2;

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 2, true, new List<string>());

            // One unreserved hull, then one from the shelf.
            Assert.Equal(2, r.Dismantled);
            Assert.Equal(1, r.Delisted);
            Assert.Equal(1, a.ShipStock["Clipper"]);
            Assert.Equal(1, a.ListedForSale["Clipper"]);
        }

        [Fact]
        public void AHullInRepair_IsDismantledForWhatItHolds_NotRepairedFirst()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            var hull = new DamagedHull { PrefabId = "Clipper", Integrity = 0.6, QueuedUtc = world.UtcNow };
            hull.Bill["Comp:Steel"] = 15;      // shot off
            hull.Collected["Comp:Steel"] = 5;  // already pulled toward the repair
            b.DamagedHulls.Add(hull);
            var log = new List<string>();

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 0, true, log);

            // 40 in the design, 15 of it gone, 5 collected back: 30.
            Assert.Equal(1, r.Dismantled);
            Assert.Equal(1, r.FromYard);
            Assert.Equal(30, Steel(world, b));
            Assert.Empty(b.DamagedHulls);
            Assert.Equal(0, r.SkippedInRepair);
            Assert.Contains(log, l => l == "F SHIP_DECOMMISSIONED node=N2 prefab=Clipper count=1 bomCredited=30 from=repair");
        }

        [Fact]
        public void AHullInRefit_IsDismantledWhole_WithThePartsCollectedForTheRefit()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            var hull = new DamagedHull { PrefabId = "Clipper", Integrity = 1.0, QueuedUtc = world.UtcNow, RefitTo = "Clipper@2" };
            hull.Bill["Comp:Steel"] = 10;
            hull.Collected["Comp:Steel"] = 4;
            a.DamagedHulls.Add(hull);
            var log = new List<string>();

            FleetDecommission.Run(world, st, "Clipper", 0, true, log);

            // The refit's bill is the new version's parts, none of them missing from this hull: 40 + 4.
            Assert.Equal(44, Steel(world, a));
            Assert.Empty(a.DamagedHulls);
            Assert.Contains(log, l => l.Contains("SHIP_DECOMMISSIONED node=N1 prefab=Clipper count=1 bomCredited=44 from=refit"));
        }

        [Fact]
        public void FlyingHulls_AreMarked_AndOnlyThoseAreDismantledWhenTheyComeHome()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            var fleet = new PendingFleetAction { ShipPrefabId = "Clipper@2", ConvoySize = 2, OriginNodeId = "N1" };
            st.PendingFleets.Add(fleet);

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 0, true, new List<string>());

            Assert.Equal(2, r.MarkedOnReturn);
            Assert.Equal(0, r.SkippedFlying);
            Assert.Equal(2, st.DecommissionOnReturn["Clipper"]);

            // One comes home parked, one comes home damaged, and a build of the design finishes too.
            Resolve(st);
            ShipPool.Return(a, "Clipper@2", 1);
            b.DamagedHulls.Add(new DamagedHull { PrefabId = "Clipper@2", Integrity = 0.5, QueuedUtc = world.UtcNow });
            ShipPool.Return(b, "Clipper", 1);
            var log = new List<string>();

            int swept = FleetDecommission.SweepReturned(world, st, log);

            // The mark takes the two that were flying, not the new build.
            Assert.Equal(2, swept);
            Assert.Equal(0, a.ShipStock["Clipper@2"]);
            Assert.Empty(b.DamagedHulls);
            Assert.Equal(1, b.ShipStock["Clipper"]);
            Assert.False(st.DecommissionOnReturn.ContainsKey("Clipper"));
            Assert.Contains(log, l => l.Contains("DECOMMISSION_ON_RETURN_DONE family=Clipper unused=0"));
        }

        [Fact]
        public void TheShipyardPass_RunsTheSweep()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            st.PendingFleets.Add(new PendingFleetAction { ShipPrefabId = "Clipper", ConvoySize = 2, OriginNodeId = "N1" });
            st.DecommissionOnReturn["Clipper"] = 2;
            a.ShipStock["Clipper"] = 1;   // one of the two is home
            var log = new List<string>();

            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Equal(0, a.ShipStock["Clipper"]);
            Assert.Equal(1, st.DecommissionOnReturn["Clipper"]);
            Assert.Contains(log, l => l.Contains("SHIP_DECOMMISSIONED node=N1 prefab=Clipper count=1"));
        }

        [Fact]
        public void TheMark_IsDropped_WhenNothingOfTheDesignIsLeftFlying()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            var fleet = new PendingFleetAction { ShipPrefabId = "Clipper", ConvoySize = 1, OriginNodeId = "N1" };
            st.PendingFleets.Add(fleet);
            FleetDecommission.Run(world, st, "Clipper", 0, true, new List<string>());

            Resolve(st);   // lost in a fight: it never comes home
            FleetDecommission.SweepReturned(world, st, new List<string>());

            Assert.Empty(st.DecommissionOnReturn);
        }

        [Fact]
        public void TheMark_IsDropped_WhenALineNamesTheDesignAgain()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            st.PendingFleets.Add(new PendingFleetAction { ShipPrefabId = "Clipper", ConvoySize = 1, OriginNodeId = "N1" });
            FleetDecommission.Run(world, st, "Clipper", 0, true, new List<string>());
            StandingOrderFixtures.AddDoctrineLine(st, "Clipper", targetCount: 3);
            a.ShipStock["Clipper"] = 1;
            var log = new List<string>();

            FleetDecommission.SweepReturned(world, st, log);

            Assert.Empty(st.DecommissionOnReturn);
            Assert.Equal(1, a.ShipStock["Clipper"]);
            Assert.Contains(log, l => l.Contains("DECOMMISSION_ON_RETURN_DROPPED family=Clipper reason=line"));
        }

        [Fact]
        public void ACount_MarksNoFlyingHull_AndReportsThemLeftAlone()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            a.ShipStock["Clipper"] = 3;
            st.PendingFleets.Add(new PendingFleetAction { ShipPrefabId = "Clipper", ConvoySize = 2, OriginNodeId = "N1" });

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 1, true, new List<string>());

            Assert.Equal(1, r.Dismantled);
            Assert.Equal(0, r.MarkedOnReturn);
            Assert.Equal(2, r.SkippedFlying);
            Assert.Empty(st.DecommissionOnReturn);
        }

        [Fact]
        public void TheMark_SurvivesARestart_AndAnEmptyOneWritesNothing()
        {
            var st = new FactionState { Tag = "F" };
            st.DecommissionOnReturn["Clipper"] = 3;
            BrainDoc doc = FactionStore.ToBrainDoc(st, new SpatialGraph().OwnedBy("F"), FactionStore.DefaultLogCap);
            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph(), null, null);
            Assert.Equal(3, back.DecommissionOnReturn["clipper"]);   // case-insensitive, as the catalogue is

            string empty = Newtonsoft.Json.JsonConvert.SerializeObject(
                FactionStore.ToBrainDoc(new FactionState { Tag = "F" }, new SpatialGraph().OwnedBy("F"), FactionStore.DefaultLogCap));
            Assert.DoesNotContain("DecommissionOnReturn", empty, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDoctrineAndShelfLinesNamingTheDesign_GoWithIt_AndNoOthers()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            a.ShipStock["Clipper"] = 1;
            DoctrineLine clipper = StandingOrderFixtures.AddDoctrineLine(st, "Clipper@2", targetCount: 5);
            DoctrineLine tanto = StandingOrderFixtures.AddDoctrineLine(st, "Tanto", targetCount: 5);
            var shelfClipper = new ShelfLine { Id = "SH-001", PrefabId = "Clipper", TargetCount = 1 };
            var shelfTanto = new ShelfLine { Id = "SH-002", PrefabId = "Tanto", TargetCount = 1 };
            st.AddShelfLine(shelfClipper);
            st.AddShelfLine(shelfTanto);

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 0, true, new List<string>());

            Assert.Equal(new[] { clipper.Id }, r.RemovedDoctrineLines);
            Assert.Equal(new[] { "SH-001" }, r.RemovedShelfLines);
            Assert.Equal(new[] { tanto }, st.FleetDoctrine);
            Assert.Equal(new[] { shelfTanto }, st.Shelf);
        }

        [Fact]
        public void ADryRun_ChangesNothing_AndReportsWhatItWouldDo()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            a.ShipStock["Clipper"] = 2;
            a.ListedForSale["Clipper"] = 1;
            StandingOrderFixtures.AddDoctrineLine(st, "Clipper", targetCount: 5);
            var log = new List<string>();

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 0, false, log);

            Assert.Equal(2, r.Dismantled);
            Assert.Equal(1, r.Delisted);
            Assert.Equal(80, r.BomUnitsCredited);
            Assert.Single(r.RemovedDoctrineLines);
            Assert.Empty(st.DecommissionOnReturn);
            Assert.Equal(2, a.ShipStock["Clipper"]);
            Assert.Equal(1, a.ListedForSale["Clipper"]);
            Assert.Equal(0, Steel(world, a));
            Assert.Single(st.FleetDoctrine);
            Assert.Empty(log);
        }

        [Fact]
        public void AnotherFactionsHulls_AreNotTouched()
        {
            SpatialNode a, b; FactionState st;
            WorldModel world = World(out a, out b, out st);
            b.OwnerFactionTag = "OTHER";
            a.ShipStock["Clipper"] = 1;
            b.ShipStock["Clipper"] = 4;

            DecommissionResult r = FleetDecommission.Run(world, st, "Clipper", 0, true, new List<string>());

            Assert.Equal(1, r.Dismantled);
            Assert.Equal(4, b.ShipStock["Clipper"]);
        }
    }
}
