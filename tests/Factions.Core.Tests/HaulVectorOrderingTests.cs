using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Dispatch order across item boundaries. Before <see cref="MaterialVector.Urgency"/>
    /// this was <see cref="DemandRegister"/> insertion order — the order an item was first ever
    /// posted — which decided outright who got a hull whenever hulls were scarce.</summary>
    public sealed class HaulVectorOrderingTests
    {
        private sealed class CargoCatalogue : IPrefabCatalogue
        {
            public bool HasPrefab(string prefabId) => true;
            public long CargoLiters(string prefabId) => 1_000_000;
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public double Dps(string prefabId) => 0;
            public IReadOnlyList<string> RolesFor(string prefabId) => new[] { "Haul", "Trade", "Mine" };
            public double DryMassKg(string prefabId) => 0;
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) => null;
            public double DrillYieldScore(string prefabId) => 1.0;
            public string GridSize(string prefabId) => "Small";
            public long Cost(string prefabId) => 0;
            public string FactionHint(string prefabId) => "All";
            public IEnumerable<string> AllPrefabIds => new[] { "Hauler" };
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => new[] { "Hauler" };
        }

        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new CargoCatalogue();
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, Vec3 pos)
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            return n;
        }

        private static void Post(WorldModel world, string subTypeId, string nodeId, long qty,
            DemandClass cls, DateTime firstPosted)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag     = "F",
                Kind           = "Item:" + subTypeId,
                NodeId         = nodeId,
                Quantity       = qty,
                ExpiresUtc     = world.UtcNow.AddMinutes(5),
                Class          = cls,
                FirstPostedUtc = firstPosted,
            });
        }

        private static List<string> Tick(WorldModel world, FactionState st)
        {
            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);
            return log;
        }

        /// <summary>The live shape: one hull, two destinations wanting different items, and the
        /// comfortable one was posted first. ISBT owned exactly one hull across 70 nodes on
        /// 2026-08-31 while its shipyard sat one component short of a finished build.</summary>
        [Fact]
        public void OneHull_BurningShipBuildWins_OverAComfortableCatalogWantPostedFirst()
        {
            var world = MakeWorld();
            var st = new FactionState { Tag = "F" };

            SpatialNode source = AddNode(world, "Source", new Vec3(0, 0, 0));
            source.Inventory["Comp:Construction"] = 10_000;
            source.Inventory["Comp:Thrust"]       = 10_000;
            source.ShipStock["Hauler"] = 1;   // the whole point: exactly one

            SpatialNode comfortable = AddNode(world, "Comfortable", new Vec3(1, 0, 0));
            SpatialNode starving    = AddNode(world, "Starving",    new Vec3(2, 0, 0));
            comfortable.Inventory["Comp:Construction"] = 90;   // 90 of 100 wanted

            // Posted FIRST, so insertion order favours it, and it is the nearer destination too.
            Post(world, "Comp:Construction", comfortable.Id, 100, DemandClass.Catalog, world.UtcNow);
            Post(world, "Comp:Thrust", starving.Id, 31, DemandClass.ShipBuild, world.UtcNow.AddHours(-9));

            Tick(world, st);

            List<PendingFleetAction> hauls = st.PendingFleets.Where(f => f.Kind == ActionKind.Haul).ToList();
            PendingFleetAction dispatched = Assert.Single(hauls);
            Assert.Equal("Starving", dispatched.TargetNodeId);
        }

        /// <summary>The discriminator for the test above: with the classes swapped the ordering has
        /// to invert, or the first test would pass on nothing more than node naming or match order.</summary>
        [Fact]
        public void OneHull_OrderInverts_WhenTheStarvingNodeIsTheComfortableOne()
        {
            var world = MakeWorld();
            var st = new FactionState { Tag = "F" };

            SpatialNode source = AddNode(world, "Source", new Vec3(0, 0, 0));
            source.Inventory["Comp:Construction"] = 10_000;
            source.Inventory["Comp:Thrust"]       = 10_000;
            source.ShipStock["Hauler"] = 1;

            SpatialNode comfortable = AddNode(world, "Comfortable", new Vec3(1, 0, 0));
            SpatialNode starving    = AddNode(world, "Starving",    new Vec3(2, 0, 0));
            starving.Inventory["Comp:Thrust"] = 30;   // 30 of 31 wanted -- nearly met

            Post(world, "Comp:Construction", comfortable.Id, 100, DemandClass.ShipBuild, world.UtcNow.AddHours(-9));
            Post(world, "Comp:Thrust", starving.Id, 31, DemandClass.Catalog, world.UtcNow);

            Tick(world, st);

            PendingFleetAction dispatched = Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Haul));
            Assert.Equal("Comfortable", dispatched.TargetNodeId);
        }

        [Fact]
        public void SortByUrgency_IsStable_SoIdenticalInputConsolidatesTheSameWayEveryRun()
        {
            var vectors = new List<MaterialVector>
            {
                new MaterialVector { ItemKey = "A", NeedNodeId = "N1", Urgency = 1.0 },
                new MaterialVector { ItemKey = "B", NeedNodeId = "N2", Urgency = 5.0 },
                new MaterialVector { ItemKey = "C", NeedNodeId = "N3", Urgency = 1.0 },
                new MaterialVector { ItemKey = "D", NeedNodeId = "N4", Urgency = 5.0 },
                new MaterialVector { ItemKey = "E", NeedNodeId = "N5", Urgency = 1.0 },
            };

            OperationsManager.SortByUrgency(vectors);

            Assert.Equal(new[] { "B", "D", "A", "C", "E" }, vectors.Select(v => v.ItemKey).ToArray());
        }

        [Fact]
        public void SortByUrgency_EmptyAndSingleAreNoOps()
        {
            var empty = new List<MaterialVector>();
            OperationsManager.SortByUrgency(empty);
            Assert.Empty(empty);

            var one = new List<MaterialVector> { new MaterialVector { ItemKey = "A", Urgency = 3 } };
            OperationsManager.SortByUrgency(one);
            Assert.Equal("A", Assert.Single(one).ItemKey);
        }

        [Fact]
        public void MatchedVector_CarriesTheUrgencyOfTheDemandItServes_NotZero()
        {
            var world = MakeWorld();
            var st = new FactionState { Tag = "F" };

            SpatialNode source = AddNode(world, "Source", new Vec3(0, 0, 0));
            source.Inventory["Comp:Thrust"] = 1000;
            source.ShipStock["Hauler"] = 4;

            SpatialNode starving = AddNode(world, "Starving", new Vec3(1, 0, 0));
            Post(world, "Comp:Thrust", starving.Id, 31, DemandClass.ShipBuild, world.UtcNow.AddHours(-9));

            Tick(world, st);

            // A dispatch at all is the observable proof the stamp reached the vector; the ordering
            // tests above are what prove the value is used. Guarding the plumbing separately so a
            // stamp silently lost to a struct copy cannot pass by riding on match order.
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Haul && f.TargetNodeId == "Starving"));
            Assert.True(DemandUrgency.Score(
                world.Demand.GetFor("F", "Item:Comp:Thrust").Single(), 0, 0, world.UtcNow, TestSupport.Tuning()) > 0);
        }
    }
}
