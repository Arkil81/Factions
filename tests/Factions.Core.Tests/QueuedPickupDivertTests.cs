using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-514: a Pickup arrival that waits out a station queue is collected from another node of the
    /// same owner (<see cref="QueueDivert.PickupSource"/> + <c>FleetRetargetPolicy.KeepStop</c>), or
    /// gives up its errand and is recalled home (<see cref="PendingFleetResolver.QueueGiveUpCause"/>).
    /// </summary>
    public class QueuedPickupDivertTests
    {
        private const string Item = "Ingot:Iron";

        private static WorldModel World(out SpatialNode home, out SpatialNode queued, out SpatialNode near,
                                        out SpatialNode far, out SpatialNode foreign)
        {
            WorldModel w = TestSupport.World();
            w.Tuning.FleetSpeedKmPerMin = 100.0;
            home = Node(w, "Home", new Vec3(0, 0, 0), "EXP");
            queued = Node(w, "N101", new Vec3(100, 0, 0), "UNG");
            near = Node(w, "N098", new Vec3(120, 0, 0), "UNG");
            far = Node(w, "N083", new Vec3(400, 0, 0), "UNG");
            foreign = Node(w, "N200", new Vec3(101, 0, 0), "OTH");
            return w;
        }

        private static SpatialNode Node(WorldModel w, string id, Vec3 pos, string owner)
        {
            SpatialNode n = w.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 1.0;
            return n;
        }

        private static PendingFleetAction PickupAt(WorldModel w, SpatialNode at, long qty)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "UNG", OriginNodeId = "Home", TargetNodeId = at.Id,
                TargetTag = at.OwnerFactionTag, CurrentStopKind = FleetStopKind.Pickup,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = at.Position,
                PickupItemKey = Item, PickupQuantity = qty,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "Home", Kind = FleetStopKind.Service } },
                DepartedUtc = w.UtcNow.AddMinutes(-1), EstimatedArrivalUtc = w.UtcNow.AddSeconds(12),
                FleetId = "71A447", ShipPrefabId = "Hauler", ConvoySize = 1, LastResolvedSegment = -1,
            };
        }

        [Fact]
        public void PickupSource_IsTheNearestOwnNodeThatCoversThePickup()
        {
            SpatialNode home, queued, near, far, foreign;
            WorldModel w = World(out home, out queued, out near, out far, out foreign);
            near.Inventory[Item] = 500;     // own, nearest, short of the pickup
            far.Inventory[Item] = 5000;     // own, covers it
            foreign.Inventory[Item] = 9000; // nearest of all, another owner's
            queued.Inventory[Item] = 9000;

            SpatialNode src = QueueDivert.PickupSource(PickupAt(w, queued, 1000), queued.Position, w, w.Tuning, null);

            Assert.Same(far, src);
        }

        [Fact]
        public void PickupSource_FallsBackToTheMostSpare_AndSkipsExcludedNodes()
        {
            SpatialNode home, queued, near, far, foreign;
            WorldModel w = World(out home, out queued, out near, out far, out foreign);
            near.Inventory[Item] = 500;
            far.Inventory[Item] = 800;

            Assert.Same(far, QueueDivert.PickupSource(PickupAt(w, queued, 1000), queued.Position, w, w.Tuning, null));
            Assert.Same(near, QueueDivert.PickupSource(PickupAt(w, queued, 1000), queued.Position, w, w.Tuning,
                                                       new HashSet<string> { "N083" }));
        }

        [Fact]
        public void PickupSource_IsNull_WhenNoOtherOwnNodeHoldsTheItem_OrTheStopIsNotAPickup()
        {
            SpatialNode home, queued, near, far, foreign;
            WorldModel w = World(out home, out queued, out near, out far, out foreign);
            queued.Inventory[Item] = 9000;
            foreign.Inventory[Item] = 9000;
            Assert.Null(QueueDivert.PickupSource(PickupAt(w, queued, 1000), queued.Position, w, w.Tuning, null));

            far.Inventory[Item] = 5000;
            PendingFleetAction service = PickupAt(w, queued, 1000);
            service.CurrentStopKind = FleetStopKind.Service;
            Assert.Null(QueueDivert.PickupSource(service, queued.Position, w, w.Tuning, null));
        }

        [Fact]
        public void KeepStop_MovesThePickupWithItsPayload_AndLoadsThereOnArrival()
        {
            SpatialNode home, queued, near, far, foreign;
            WorldModel w = World(out home, out queued, out near, out far, out foreign);
            near.Inventory[Item] = 5000;
            var st = new FactionState { Tag = "UNG" };
            st.PendingFleets.Add(PickupAt(w, queued, 1000));
            var log = new List<string>();

            Assert.True(PendingFleetResolver.Retarget(st, 0, "N098", PendingFleetResolver.FleetRetargetPolicy.KeepStop,
                                                      w, w.Tuning, log, "traffic_divert"));

            PendingFleetAction f = st.PendingFleets[0];
            Assert.Equal("N098", f.TargetNodeId);
            Assert.Equal(FleetStopKind.Pickup, f.CurrentStopKind);
            Assert.Equal(Item, f.PickupItemKey);
            Assert.Equal(1000, f.PickupQuantity);
            Assert.Equal("UNG", f.TargetTag);
            Assert.Single(f.RemainingStops);
            Assert.Contains(log, l => l.StartsWith("FLEET_RETARGET [71A447] UNG kind=Haul N101 -> N098 policy=KeepStop cause=traffic_divert"));

            w.UtcNow = f.EstimatedArrivalUtc.AddSeconds(1);
            log.Clear();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning, new SeededRandom(1), log);
            Assert.Contains(log, l => l.StartsWith("HAUL_PICKUP [71A447] UNG @ N098 loaded="));
            Assert.True(w.GetNodeInventory("N098", Item) < 5000);
        }

        [Fact]
        public void GiveUp_RecallsTheHullHome_WithoutCollecting()
        {
            SpatialNode home, queued, near, far, foreign;
            WorldModel w = World(out home, out queued, out near, out far, out foreign);
            queued.Inventory[Item] = 9000;
            PendingFleetAction f = PickupAt(w, queued, 1000);
            f.EstimatedArrivalUtc = w.UtcNow;
            f.ForcedRatio = 0.0;
            f.ForcedRatioCause = PendingFleetResolver.QueueGiveUpCause;
            var st = new FactionState { Tag = "UNG" };
            st.PendingFleets.Add(f);
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning, new SeededRandom(1), log);

            Assert.DoesNotContain(st.PendingFleets, x => !x.Resolved);
            Assert.Equal(9000, w.GetNodeInventory("N101", Item));
            Assert.DoesNotContain(log, l => l.StartsWith("HAUL_PICKUP"));
            Assert.Contains(log, l => l.StartsWith("FLEET_RECALLED [71A447] UNG kind=Haul") && l.Contains("why=traffic_give_up"));
            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST"));
        }
    }
}
