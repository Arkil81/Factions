using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// What a build is short of, and where that shortfall could come from. The queue's own numbers
    /// say a build is at 29%; these rows say whether the missing component is absent from the
    /// faction, standing at another station, or already flying here.
    /// </summary>
    public sealed class BuildSupplyTests
    {
        private static WorldModel MakeWorld()
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return w;
        }

        private static SpatialNode AddNode(WorldModel world, string id, string tag, double x = 0)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.StableStationId = 42;
            return n;
        }

        private static BuildQueueEntry Entry(WorldModel world, string role, params object[] bom)
        {
            var e = new BuildQueueEntry
            {
                Id = role + ":001",
                Role = role,
                PrefabId = role + "Hull",
                CreatedUtc = world.UtcNow,
                ExpiresUtc = world.UtcNow.AddHours(24),
            };
            for (int i = 0; i < bom.Length; i += 2) e.Bom[(string)bom[i]] = Convert.ToInt64(bom[i + 1]);
            return e;
        }

        private static BuildSupply.ComponentRow Row(List<BuildSupply.ComponentRow> rows, string key)
        {
            foreach (BuildSupply.ComponentRow row in rows)
                if (row.Key == key) return row;
            throw new InvalidOperationException("No row for " + key + ".");
        }

        [Fact]
        public void EveryBomComponentGetsARowWithItsOwnShortfall()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            BuildQueueEntry e = Entry(w, "Raid", "Comp:SteelPlate", 100, "Comp:Superconductor", 40);
            e.Collected["Comp:SteelPlate"] = 100;
            e.Collected["Comp:Superconductor"] = 5;
            yard.BuildQueue.Add(e);

            List<BuildSupply.ComponentRow> rows =
                BuildSupply.Rows(yard, e, BuildSupply.EstateView.For(w, owner, yard));

            Assert.Equal(2, rows.Count);
            Assert.Equal(0L, Row(rows, "Comp:SteelPlate").Short);
            Assert.Equal(35L, Row(rows, "Comp:Superconductor").Short);
            // A complete row is complete, not owed a refund, even if it over-collected.
            e.Collected["Comp:SteelPlate"] = 130;
            rows = BuildSupply.Rows(yard, e, BuildSupply.EstateView.Empty);
            Assert.Equal(0L, Row(rows, "Comp:SteelPlate").Short);
        }

        [Fact]
        public void OutstandingRowsSortAboveCompleteOnes_LargestShortfallFirst()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N1", "EXP");
            BuildQueueEntry e = Entry(w, "Raid",
                "Comp:SteelPlate", 100, "Comp:Motor", 60, "Comp:Superconductor", 40);
            e.Collected["Comp:SteelPlate"] = 100;   // complete
            e.Collected["Comp:Motor"] = 10;         // short 50
            e.Collected["Comp:Superconductor"] = 5; // short 35

            List<BuildSupply.ComponentRow> rows = BuildSupply.Rows(yard, e, BuildSupply.EstateView.Empty);

            Assert.Equal("Comp:Motor", rows[0].Key);
            Assert.Equal("Comp:Superconductor", rows[1].Key);
            Assert.Equal("Comp:SteelPlate", rows[2].Key);
        }

        [Fact]
        public void StockAtTheYardIsSeparateFromStockElsewhereInTheEstate()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            SpatialNode depot = AddNode(w, "N2", "EXP", 10);
            SpatialNode stranger = AddNode(w, "N3", "SPRT", 20);

            yard.Inventory["Comp:Motor"] = 7;
            depot.Inventory["Comp:Motor"] = 500;
            stranger.Inventory["Comp:Motor"] = 9000;   // another faction's: never counted

            BuildQueueEntry e = Entry(w, "Raid", "Comp:Motor", 60);
            yard.BuildQueue.Add(e);

            BuildSupply.ComponentRow row = Row(
                BuildSupply.Rows(yard, e, BuildSupply.EstateView.For(w, owner, yard)), "Comp:Motor");

            Assert.Equal(7L, row.AtNode);
            Assert.Equal(500L, row.Elsewhere);
        }

        [Fact]
        public void CargoAboardAConvoyBoundHereCountsAsEnRoute()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            BuildQueueEntry e = Entry(w, "Raid", "Comp:Motor", 60);
            yard.BuildQueue.Add(e);

            owner.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul,
                ActorTag = "EXP",
                TargetNodeId = "N1",
                CurrentStopKind = FleetStopKind.Service,
                Cargo = new Dictionary<string, long> { { "Comp:Motor", 30 } },
            });

            Assert.Equal(30L, Row(BuildSupply.Rows(yard, e, BuildSupply.EstateView.For(w, owner, yard)),
                "Comp:Motor").EnRoute);
        }

        [Fact]
        public void AConsolidatedConvoyCountsBothItsHoldAndItsBookedPickups()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            BuildQueueEntry e = Entry(w, "Raid", "Comp:Motor", 200);
            yard.BuildQueue.Add(e);

            // Flying its first pickup leg, with a second pickup and then the delivery ahead of it.
            owner.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul,
                ActorTag = "EXP",
                TargetNodeId = "N7",
                CurrentStopKind = FleetStopKind.Pickup,
                Cargo = new Dictionary<string, long> { { "Comp:Motor", 30 } },
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "N8", Kind = FleetStopKind.Pickup, PickupItemKey = "Comp:Motor", PickupQuantity = 25 },
                    new FleetStop { NodeId = "N1", Kind = FleetStopKind.Service },
                    new FleetStop { NodeId = "N5", Kind = FleetStopKind.Return },
                },
            });

            Assert.Equal(55L, Row(BuildSupply.Rows(yard, e, BuildSupply.EstateView.For(w, owner, yard)),
                "Comp:Motor").EnRoute);
        }

        [Fact]
        public void AConvoyDeliveringSomewhereElseIsNotEnRouteHere()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            BuildQueueEntry e = Entry(w, "Raid", "Comp:Motor", 60);
            yard.BuildQueue.Add(e);

            // Delivers at N2, and only passes N1 on the way home.
            owner.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul,
                ActorTag = "EXP",
                TargetNodeId = "N2",
                CurrentStopKind = FleetStopKind.Service,
                Cargo = new Dictionary<string, long> { { "Comp:Motor", 30 } },
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "N2", Kind = FleetStopKind.Service },
                    new FleetStop { NodeId = "N1", Kind = FleetStopKind.Return },
                },
            });

            Assert.Equal(0L, Row(BuildSupply.Rows(yard, e, BuildSupply.EstateView.For(w, owner, yard)),
                "Comp:Motor").EnRoute);
        }

        [Fact]
        public void ARaidersHoldIsNotSupplyAndNeitherIsAResolvedConvoy()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            BuildQueueEntry e = Entry(w, "Raid", "Comp:Motor", 60);
            yard.BuildQueue.Add(e);

            owner.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "EXP", TargetNodeId = "N1",
                CurrentStopKind = FleetStopKind.Service,
                Cargo = new Dictionary<string, long> { { "Comp:Motor", 500 } },
            });
            owner.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "EXP", TargetNodeId = "N1", Resolved = true,
                CurrentStopKind = FleetStopKind.Service,
                Cargo = new Dictionary<string, long> { { "Comp:Motor", 500 } },
            });

            Assert.Equal(0L, Row(BuildSupply.Rows(yard, e, BuildSupply.EstateView.For(w, owner, yard)),
                "Comp:Motor").EnRoute);
        }

        [Fact]
        public void AKeyTheOperationsPassCouldNotSourceHereIsMarkedUnserved()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            BuildQueueEntry e = Entry(w, "Raid", "Comp:Motor", 60, "Comp:Superconductor", 40);
            yard.BuildQueue.Add(e);

            owner.UnservedDemands.Add(new UnservedDemand
            {
                NodeId = "N1", ItemKey = "Comp:Superconductor", Remaining = 35, Class = DemandClass.ShipBuild,
            });
            // Same key, another node: this build is not the one that went unserved.
            owner.UnservedDemands.Add(new UnservedDemand
            {
                NodeId = "N2", ItemKey = "Comp:Motor", Remaining = 10, Class = DemandClass.ShipBuild,
            });

            List<BuildSupply.ComponentRow> rows =
                BuildSupply.Rows(yard, e, BuildSupply.EstateView.For(w, owner, yard));

            Assert.True(Row(rows, "Comp:Superconductor").Unserved);
            Assert.False(Row(rows, "Comp:Motor").Unserved);
        }

        [Fact]
        public void OneEstateViewServesEveryEntryInTheQueue()
        {
            WorldModel w = MakeWorld();
            var owner = new FactionState { Tag = "EXP" };
            SpatialNode yard = AddNode(w, "N1", "EXP");
            SpatialNode depot = AddNode(w, "N2", "EXP", 10);
            depot.Inventory["Comp:Motor"] = 500;
            depot.Inventory["Comp:SteelPlate"] = 900;

            BuildQueueEntry raid = Entry(w, "Raid", "Comp:Motor", 60);
            BuildQueueEntry trade = Entry(w, "Trade", "Comp:SteelPlate", 100);
            yard.BuildQueue.Add(raid);
            yard.BuildQueue.Add(trade);

            BuildSupply.EstateView view = BuildSupply.EstateView.For(w, owner, yard);

            Assert.Equal(500L, Row(BuildSupply.Rows(yard, raid, view), "Comp:Motor").Elsewhere);
            Assert.Equal(900L, Row(BuildSupply.Rows(yard, trade, view), "Comp:SteelPlate").Elsewhere);
        }

        /// <summary>The number a "waiting behind n" surface must read, and the reason it is not the
        /// row index: a free build has no BOM to converge on, so it is skipped when counting who is
        /// ahead — the first real build behind it still holds the yard's working slot.</summary>
        [Fact]
        public void AFreeBuildDoesNotPushTheFirstRealBuildOutOfTheWorkingSlot()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N1", "EXP");

            var free = new BuildQueueEntry { Id = "Trade:002", Role = "Trade", CreatedUtc = w.UtcNow };
            BuildQueueEntry raid = Entry(w, "Raid", "Comp:Motor", 60);
            BuildQueueEntry mine = Entry(w, "Mine", "Comp:Motor", 60);
            yard.BuildQueue.Add(free);
            yard.BuildQueue.Add(raid);
            yard.BuildQueue.Add(mine);

            // Index in the ordered queue: free 0, raid 1, mine 2. Contest for the slot: raid 0,
            // mine 1 — and it is the second number a window has to show.
            Assert.True(yard.IsActiveBuild(free));
            Assert.True(yard.IsActiveBuild(raid));
            Assert.False(yard.IsActiveBuild(mine));

            Assert.Equal(-1, yard.BuildsAheadOf(free));
            Assert.Equal(0, yard.BuildsAheadOf(raid));
            Assert.Equal(1, yard.BuildsAheadOf(mine));
        }

        [Fact]
        public void AnEntryThisNodeDoesNotHoldIsAheadOfNothing()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N1", "EXP");
            yard.BuildQueue.Add(Entry(w, "Raid", "Comp:Motor", 60));

            Assert.Equal(-1, yard.BuildsAheadOf(Entry(w, "Trade", "Comp:Motor", 60)));
            Assert.Equal(-1, yard.BuildsAheadOf(null));
        }

        [Fact]
        public void AFreeBuildHasNoBomAndNoRows()
        {
            WorldModel w = MakeWorld();
            SpatialNode yard = AddNode(w, "N1", "EXP");
            var free = new BuildQueueEntry { Id = "Trade:002", Role = "Trade", CreatedUtc = w.UtcNow };

            Assert.Empty(BuildSupply.Rows(yard, free, BuildSupply.EstateView.Empty));
        }
    }
}
