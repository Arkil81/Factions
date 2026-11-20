using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A repair queue orders its missing parts (<see cref="RepairSupply"/>): the yard posts what its
    /// waiting hulls still lack as <c>Item:</c> and <c>Need:</c> rows, the way a build's BOM
    /// shortfall is posted, and those rows reach Operations (a haul to the yard) and the price
    /// oracle's operational need. See docs/fleet-events.md, "Hull integrity".
    /// </summary>
    public sealed class RepairSupplyTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

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
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            return w;
        }

        private static SpatialNode AddNode(WorldModel w, string id, double x, bool shipyard)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = 1;
            if (shipyard) n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        /// <summary>The N483 shape: a scraped Tanto whose steel is in and which waits on one Reactor.</summary>
        private static DamagedHull WaitingOnAReactor(SpatialNode node)
        {
            var hull = new DamagedHull { PrefabId = "Tanto", Integrity = 0.99, QueuedUtc = T0 };
            hull.Bill["Comp:SteelPlate"] = 38;
            hull.Bill["Comp:Reactor"] = 1;
            hull.Collected["Comp:SteelPlate"] = 38;
            node.DamagedHulls.Add(hull);
            return hull;
        }

        private static DemandEntry Row(WorldModel w, string kind, string nodeId)
        {
            DemandEntry row;
            Assert.True(w.Demand.TryGet("F", kind, nodeId, null, out row), "no " + kind + " row at " + nodeId);
            return row;
        }

        private static bool HasRow(WorldModel w, string kind, string nodeId)
        {
            DemandEntry row;
            return w.Demand.TryGet("F", kind, nodeId, null, out row);
        }

        [Fact]
        public void TheQueuesShortfall_IsPostedOncePerKey_SummedAcrossWaitingHulls()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N483", 0, shipyard: true);
            for (int i = 0; i < 9; i++) WaitingOnAReactor(yard);
            // A parts-complete hull lacks nothing, whatever its bill says.
            DamagedHull working = WaitingOnAReactor(yard);
            working.Collected["Comp:Reactor"] = 1;
            working.PartsCompleteUtc = T0;
            var log = new List<string>();

            Assert.Equal(1, RepairSupply.PostDemands(w, yard, "F", Ttl, log));

            DemandEntry item = Row(w, "Item:Comp:Reactor", "N483");
            Assert.Equal(9L, item.Quantity);
            Assert.Equal(DemandClass.ShipBuild, item.Class);
            Assert.Equal(T0.Add(Ttl), item.ExpiresUtc);
            Assert.Equal(9L, Row(w, "Need:Comp:Reactor", "N483").Quantity);
            Assert.False(HasRow(w, "Item:Comp:SteelPlate", "N483"));   // collected in full: not ordered
            Assert.Equal(new[] { "F ITEM_DEMAND Comp:Reactor need=9 have=0 repair_short=9 repair_hulls=9 node=N483" },
                         log.ToArray());
            // One row per key, not one per hull.
            Assert.Equal(1, w.Demand.ItemDemandRowsAtNode("N483", "Comp:Reactor"));
            // The price oracle's pinned operational need at the yard.
            Assert.Equal(9L, w.Demand.OperationalDemandAtNode("N483", "Comp:Reactor"));
        }

        [Fact]
        public void ARefresh_LogsNothing_AndTracksTheShrinkingShortfall()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N483", 0, shipyard: true);
            for (int i = 0; i < 3; i++) WaitingOnAReactor(yard);
            RepairSupply.PostDemands(w, yard, "F", Ttl, new List<string>());

            w.AddNodeInventory(yard.Id, "Comp:Reactor", 1);
            w.UtcNow = T0.AddSeconds(30);
            HullDamage.ServiceRepairs(w, yard, "F", new List<string>());
            var log = new List<string>();
            RepairSupply.PostDemands(w, yard, "F", Ttl, log);

            Assert.Equal(2L, Row(w, "Item:Comp:Reactor", "N483").Quantity);
            Assert.Equal(2L, w.Demand.OperationalDemandAtNode("N483", "Comp:Reactor"));
            Assert.Empty(log);
        }

        [Fact]
        public void ABuildAndARepair_WantingOneKey_ShareTheRow_AtTheirSum()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N483", 0, shipyard: true);
            var build = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", PrefabId = "Tanto", ExpiresUtc = T0.AddHours(24) };
            build.Bom["Comp:Reactor"] = 5;
            build.Collected["Comp:Reactor"] = 1;
            yard.BuildQueue.Add(build);
            // What the build pass posted earlier in the same shipyard pass.
            w.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Reactor", NodeId = "N483",
                ExpiresUtc = T0.Add(Ttl), Quantity = 4, Class = DemandClass.ShipBuild,
            });
            for (int i = 0; i < 2; i++) WaitingOnAReactor(yard);
            var log = new List<string>();

            RepairSupply.PostDemands(w, yard, "F", Ttl, log);

            Assert.Equal(6L, Row(w, "Item:Comp:Reactor", "N483").Quantity);   // 4 for the build + 2 for repairs
            Assert.Empty(log);   // the build's row was already there
        }

        [Fact]
        public void AYardThatDoesNotRepair_OrdersNothing()
        {
            WorldModel w = MakeWorld();
            SpatialNode depot = AddNode(w, "N217", 0, shipyard: false);
            WaitingOnAReactor(depot);
            SpatialNode paused = AddNode(w, "N300", 10, shipyard: true);
            paused.ShipyardPaused = true;
            WaitingOnAReactor(paused);
            var log = new List<string>();

            Assert.Equal(0, RepairSupply.PostDemands(w, depot, "F", Ttl, log));
            Assert.Equal(0, RepairSupply.PostDemands(w, paused, "F", Ttl, log));

            Assert.Equal(0, w.Demand.Count);
            Assert.Empty(log);
        }

        /// <summary>Through the shipyard pass, which is where it runs live: the row appears, and once
        /// the parts are in and the hull is back in stock it is no longer refreshed and expires.</summary>
        [Fact]
        public void TheShipyardPass_OrdersTheParts_AndTheRowExpiresOnceTheyAreIn()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N483", 0, shipyard: true);
            DamagedHull hull = WaitingOnAReactor(yard);
            var st = new FactionState { Tag = "F" };
            var manager = new ShipyardManager();

            var log = new List<string>();
            manager.Tick(new List<FactionState> { st }, w, w.Tuning, log);
            Assert.Equal(1L, Row(w, "Item:Comp:Reactor", "N483").Quantity);
            Assert.Contains("F ITEM_DEMAND Comp:Reactor need=1 have=0 repair_short=1 repair_hulls=1 node=N483", log);

            // The Reactor arrives; the next pass absorbs it and orders nothing more.
            w.AddNodeInventory(yard.Id, "Comp:Reactor", 1);
            w.UtcNow = T0.AddSeconds(10);
            log.Clear();
            manager.Tick(new List<FactionState> { st }, w, w.Tuning, log);
            Assert.True(hull.PartsComplete);
            DemandEntry stale = Row(w, "Item:Comp:Reactor", "N483");
            Assert.True(stale.ExpiresUtc <= T0.AddHours(1), "the row was refreshed after the parts were in");

            // Past the TTL (and past the 0.04 h repair) the row is gone and the hull is in stock.
            w.UtcNow = T0.AddHours(2);
            manager.Tick(new List<FactionState> { st }, w, w.Tuning, log);
            Assert.False(HasRow(w, "Item:Comp:Reactor", "N483"));
            Assert.False(HasRow(w, "Need:Comp:Reactor", "N483"));
            Assert.Equal(0L, w.Demand.OperationalDemandAtNode("N483", "Comp:Reactor"));
            Assert.Empty(yard.DamagedHulls);
            Assert.Equal(1, yard.ShipStock["Tanto"]);
        }

        /// <summary>One key end to end: the Reactor the queue lacks is at another owned node, and
        /// Operations sends a haul of it to the yard; delivered, the queue completes.</summary>
        [Fact]
        public void TheOrderedPart_IsHauledToTheYard_AndCompletesTheRepair()
        {
            WorldModel w = MakeWorld();
            w.PrefabCatalogue = new CargoCatalogue();
            SpatialNode source = AddNode(w, "N011", 0, shipyard: false);
            source.Inventory["Comp:Reactor"] = 100;
            source.ShipStock["Hauler"] = 1;
            SpatialNode yard = AddNode(w, "N483", 1, shipyard: true);
            DamagedHull hull = WaitingOnAReactor(yard);
            var st = new FactionState { Tag = "F" };

            // The shipyard's two calls, in its order.
            HullDamage.ServiceRepairs(w, yard, "F", new List<string>());
            RepairSupply.PostDemands(w, yard, "F", Ttl, new List<string>());

            new OperationsManager().Tick(new List<FactionState> { st }, w, TestSupport.Tuning(), new List<string>());

            PendingFleetAction haul = Assert.Single(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.Equal("N011", haul.OriginNodeId);
            Assert.Equal("N483", haul.TargetNodeId);
            Assert.NotNull(haul.Cargo);
            Assert.True(haul.Cargo.ContainsKey("Comp:Reactor") && haul.Cargo["Comp:Reactor"] >= 1,
                        "the haul does not carry the Reactor");

            // Delivered: the yard absorbs it on its next pass.
            w.AddNodeInventory(yard.Id, "Comp:Reactor", haul.Cargo["Comp:Reactor"]);
            HullDamage.ServiceRepairs(w, yard, "F", new List<string>());
            Assert.True(hull.PartsComplete);
            Assert.Equal(0, RepairSupply.PostDemands(w, yard, "F", Ttl, new List<string>()));
        }
    }
}
