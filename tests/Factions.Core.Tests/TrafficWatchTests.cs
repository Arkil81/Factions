using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-419 slice 2: <see cref="TrafficWatch"/> turns fleet legs into what a station's
    /// traffic controller would report (docs/atc-voice.md).</summary>
    public class TrafficWatchTests
    {
        private static WorldModel World()
        {
            WorldModel w = TestSupport.World(1);
            AddNode(w, "A", new Vec3(0, 0, 0), "HAUL");
            AddNode(w, "B", new Vec3(200, 0, 0), "HAUL");
            AddNode(w, "V", new Vec3(400, 0, 0), "MERC");
            return w;
        }

        private static void AddNode(WorldModel w, string id, Vec3 pos, string owner)
        {
            SpatialNode n = w.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 1.0;
        }

        private static PendingFleetAction Leg(WorldModel w, string id, ActionKind kind, string actor,
                                              string from, string to, FleetStopKind stop,
                                              double departedMinsAgo, double arrivesInMins)
        {
            return new PendingFleetAction
            {
                Kind = kind, ActorTag = actor, FleetId = id,
                OriginNodeId = from, TargetNodeId = to,
                OriginPosKm = from != null ? w.Graph.Nodes[from].Position : default(Vec3),
                TargetPosKm = to != null ? w.Graph.Nodes[to].Position : default(Vec3),
                CurrentStopKind = stop,
                DepartedUtc = w.UtcNow.AddMinutes(-departedMinsAgo),
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(arrivesInMins),
                LastResolvedSegment = -1,
            };
        }

        private static List<FactionState> States(params PendingFleetAction[] fleets)
        {
            var st = new FactionState { Tag = "HAUL" };
            st.PendingFleets.AddRange(fleets);
            return new List<FactionState> { st };
        }

        private static List<TrafficEvent> Scan(List<FactionState> states, WorldModel w)
        {
            TrafficWatch.Scan(states, w, w.Tuning);
            return w.TakeTrafficEvents() ?? new List<TrafficEvent>();
        }

        private static void Seed(WorldModel w) { Scan(States(), w); }

        [Fact]
        public void First_scan_after_boot_only_records()
        {
            WorldModel w = World();
            var states = States(Leg(w, "H1", ActionKind.Haul, "HAUL", "A", "B", FleetStopKind.Service, 0, 10));
            Assert.Empty(Scan(states, w));
            Assert.Empty(Scan(states, w));
        }

        [Fact]
        public void A_new_leg_from_a_node_is_a_departure_from_that_node()
        {
            WorldModel w = World();
            Seed(w);
            List<TrafficEvent> e = Scan(States(Leg(w, "H1", ActionKind.Haul, "HAUL", "A", "B", FleetStopKind.Service, 0, 10)), w);
            TrafficEvent d = Assert.Single(e);
            Assert.Equal(TrafficEventKind.Depart, d.Kind);
            Assert.Equal("A", d.NodeId);
            Assert.Equal(ActionKind.Haul, d.FleetKind);
            Assert.Equal(w.Graph.Nodes["A"].Position, d.NodePosKm);
            Assert.Equal("H1", d.FleetId);   // spoken by the controller (docs/traffic-control.md, T9)
        }

        [Fact]
        public void A_miner_leaving_its_ring_does_not_call_a_departure_from_the_deposit()
        {
            WorldModel w = World();
            Seed(w);
            PendingFleetAction fromRing = Leg(w, "M1", ActionKind.Mine, "HAUL", "A", "B", FleetStopKind.Service, 0, 10);
            fromRing.OriginPosKm = new Vec3(0, 25, 0);   // out on the ring around A (MineWander)
            PendingFleetAction fromNode = Leg(w, "M2", ActionKind.Mine, "HAUL", "A", "B", FleetStopKind.Service, 0, 10);

            List<TrafficEvent> e = Scan(States(fromRing, fromNode), w);

            // The other arm: the same leg starting at the node still departs from it.
            TrafficEvent d = Assert.Single(e);
            Assert.Equal("M2", d.FleetId);
        }

        [Fact]
        public void An_arrival_at_a_mission_stop_is_announced_once()
        {
            WorldModel w = World();
            Seed(w);
            var states = States(Leg(w, "H1", ActionKind.Haul, "HAUL", "A", "B", FleetStopKind.Service, 0, 10));
            Scan(states, w);                       // the departure
            w.UtcNow = w.UtcNow.AddMinutes(11);
            TrafficEvent a = Assert.Single(Scan(states, w));
            Assert.Equal(TrafficEventKind.Arrive, a.Kind);
            Assert.Equal("B", a.NodeId);
            Assert.Empty(Scan(states, w));         // the failing case: announced every pass
        }

        [Theory]
        [InlineData(FleetStopKind.Navigation)]
        [InlineData(FleetStopKind.Intercept)]
        public void Transit_stops_announce_no_arrival(FleetStopKind stop)
        {
            WorldModel w = World();
            Seed(w);
            var states = States(Leg(w, "H1", ActionKind.Haul, "HAUL", null, "B", stop, 5, -1));
            Assert.Empty(Scan(states, w));
        }

        [Theory]
        [InlineData(FleetStopKind.Mine)]
        [InlineData(FleetStopKind.Engage)]
        public void A_dwell_at_the_node_neither_departs_nor_arrives(FleetStopKind stop)
        {
            WorldModel w = World();
            Seed(w);
            // A dwell is a leg from the node to itself; it resolves when the dwell ENDS.
            var states = States(Leg(w, "D1", ActionKind.Mine, "HAUL", "B", "B", stop, 0, 5));
            Assert.Empty(Scan(states, w));
            w.UtcNow = w.UtcNow.AddMinutes(6);
            Assert.Empty(Scan(states, w));
        }

        [Fact]
        public void A_bench_fleet_is_not_traffic()
        {
            WorldModel w = World();
            Seed(w);
            PendingFleetAction f = Leg(w, "B1", ActionKind.Raid, "HAUL", "A", "V", FleetStopKind.Service, 0, 10);
            f.BenchLabel = "duel";
            var states = States(f);
            Assert.Empty(Scan(states, w));
            w.UtcNow = w.UtcNow.AddMinutes(11);
            Assert.Empty(Scan(states, w));
        }

        [Fact]
        public void A_raid_is_announced_once_when_the_target_can_see_it_and_never_at_dispatch()
        {
            WorldModel w = World();
            Seed(w);
            // A to V is 400 km; the target's passive envelope is tens of km.
            var states = States(Leg(w, "R1", ActionKind.Raid, "HAUL", "A", "V", FleetStopKind.Service, 0, 100));
            List<TrafficEvent> atDispatch = Scan(states, w);
            Assert.DoesNotContain(atDispatch, e => e.Kind == TrafficEventKind.HostileInbound);

            w.UtcNow = w.UtcNow.AddMinutes(99.9);  // a whisker from the target
            TrafficEvent h = Assert.Single(Scan(states, w));
            Assert.Equal(TrafficEventKind.HostileInbound, h.Kind);
            Assert.Equal("V", h.NodeId);
            Assert.Empty(Scan(states, w));
        }

        [Fact]
        public void A_fleet_attacking_its_own_node_is_not_hostile_traffic()
        {
            WorldModel w = World();
            Seed(w);
            var states = States(Leg(w, "R2", ActionKind.Raid, "MERC", "A", "V", FleetStopKind.Service, 0, 100));
            w.UtcNow = w.UtcNow.AddMinutes(99.9);
            Assert.DoesNotContain(Scan(states, w), e => e.Kind == TrafficEventKind.HostileInbound);
        }

        [Fact]
        public void The_queue_is_capped_and_keeps_the_newest()
        {
            WorldModel w = World();
            for (int i = 0; i < WorldModel.TrafficEventCap + 10; i++)
                w.AddTrafficEvent(new TrafficEvent { NodeId = "n" + i });
            List<TrafficEvent> taken = w.TakeTrafficEvents();
            Assert.Equal(WorldModel.TrafficEventCap, taken.Count);
            Assert.Equal("n" + (WorldModel.TrafficEventCap + 9), taken.Last().NodeId);
            Assert.Null(w.TakeTrafficEvents());
        }

        [Fact]
        public void ResolveArrivals_runs_the_scan_before_it_resolves()
        {
            WorldModel w = World();
            var states = States();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), new List<string>());
            w.TakeTrafficEvents();
            states[0].PendingFleets.Add(Leg(w, "H2", ActionKind.Haul, "HAUL", "A", "B", FleetStopKind.Service, 10, -0.1));
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(1), new List<string>());
            List<TrafficEvent> e = w.TakeTrafficEvents();
            Assert.NotNull(e);
            Assert.Contains(e, x => x.Kind == TrafficEventKind.Arrive && x.NodeId == "B");
        }
    }
}
