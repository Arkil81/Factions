using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// PendingFleetResolver.Retarget -- redirecting a fleet that has NOT reached its current stop
    /// (docs/fleet-waypoints-plan.md Slice C), and FleetManager's first consumer of it: pulling an
    /// in-flight Guard onto a fresher threat rather than paying for a second one.
    /// </summary>
    public class FleetRetargetTests
    {
        private static FactionTuning T()
        {
            FactionTuning t = TestSupport.Tuning();
            t.FleetSpeedKmPerMin = 100.0;
            return t;
        }

        private static WorldModel W(out SpatialNode a, out SpatialNode b, out SpatialNode c)
        {
            WorldModel w = TestSupport.World();
            a = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "A"); a.OwnerFactionTag = "F"; a.Radius = 40;
            b = w.Graph.Add(new Vec3(1000, 0, 0), 0.6, "B"); b.OwnerFactionTag = "F"; b.Radius = 40;
            c = w.Graph.Add(new Vec3(0, 1000, 0), 0.6, "C"); c.OwnerFactionTag = "F"; c.Radius = 40;
            return w;
        }

        private static PendingFleetAction InFlight(WorldModel w, FactionTuning t, SpatialNode from,
                                                   SpatialNode to, ActionKind kind = ActionKind.Guard)
        {
            return new PendingFleetAction
            {
                Kind = kind,
                ActorTag = "F",
                OriginNodeId = from.Id,
                TargetNodeId = to.Id,
                OriginPosKm = from.Position,
                TargetPosKm = to.Position,
                DepartedUtc = w.UtcNow.AddMinutes(-2),
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(20),
                FleetId = "ABC123",
                ConvoySize = 1,
                LastResolvedSegment = -1,
            };
        }

        private static PendingFleetAction RoundTripFleet(FactionState st, SpatialGraph graph)
        {
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy(st.Tag), FactionStore.DefaultLogCap));
                FactionState reloaded = FactionStore.ApplyBrainDoc(store.LoadBrain(st.Tag), new SpatialGraph());
                return reloaded.PendingFleets.Single();
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // The core contract: destination moves, the fleet resumes from where it actually is, and the
        // stale graph path is dropped rather than describing a journey nobody is flying.
        [Fact]
        public void Retarget_MovesDestination_AndResumesFromInterpolatedPosition()
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            PendingFleetAction f = InFlight(w, t, a, b);
            f.PathNodeIds = new[] { "A", "B" };
            st.PendingFleets.Add(f);

            Vec3 posBefore = st.PendingFleets[0].CurrentPosKm(w.UtcNow, t);
            var log = new List<string>();
            bool ok = PendingFleetResolver.Retarget(st, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, log, "test");

            Assert.True(ok);
            PendingFleetAction after = st.PendingFleets[0];
            Assert.Equal("C", after.TargetNodeId);
            Assert.Equal(c.Position.X, after.TargetPosKm.X, 6);
            // Resumed from where it was, not from the node it departed.
            Assert.Equal(posBefore.X, after.OriginPosKm.X, 3);
            Assert.NotEqual(a.Position.X, after.OriginPosKm.X);
            Assert.Null(after.PathNodeIds);
            Assert.Equal(1, after.RetargetCount);
            Assert.Contains(log, m => m.Contains("FLEET_RETARGET") && m.Contains("-> C")
                                      && m.Contains("cause=test"));
        }

        // OriginNodeId means "where this fleet was dispatched from" and is read by Haul/Trade
        // settlement; a mid-air redirect must not repoint it at open space.
        [Fact]
        public void Retarget_LeavesOriginNodeIdAlone()
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.PendingFleets.Add(InFlight(w, t, a, b));

            PendingFleetResolver.Retarget(st, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test");

            Assert.Equal("A", st.PendingFleets[0].OriginNodeId);
        }

        // Replace drops the current destination but keeps queued stops, so a redirected Trade still
        // flies its Return leg.
        [Fact]
        public void Retarget_Replace_KeepsQueuedStops_DropsCurrentDestination()
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            PendingFleetAction f = InFlight(w, t, a, b, ActionKind.Trade);
            f.RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "A", Kind = FleetStopKind.Return } };
            st.PendingFleets.Add(f);

            PendingFleetResolver.Retarget(st, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test");

            List<FleetStop> stops = st.PendingFleets[0].RemainingStops;
            Assert.Single(stops);
            Assert.Equal("A", stops[0].NodeId);
            Assert.Equal(FleetStopKind.Return, stops[0].Kind);
        }

        // Divert owes the original destination something, so it is requeued behind the new one
        // rather than dropped -- a Haul with committed cargo still has to make its delivery.
        [Fact]
        public void Retarget_Divert_RequeuesCurrentDestinationAheadOfQueuedStops()
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            PendingFleetAction f = InFlight(w, t, a, b, ActionKind.Haul);
            f.RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "A", Kind = FleetStopKind.Return } };
            st.PendingFleets.Add(f);

            PendingFleetResolver.Retarget(st, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Divert, w, t, new List<string>(), "test");

            List<FleetStop> stops = st.PendingFleets[0].RemainingStops;
            Assert.Equal(2, stops.Count);
            Assert.Equal("B", stops[0].NodeId);   // the destination it was flying to, requeued
            Assert.Equal("A", stops[1].NodeId);   // then the return it already carried
            Assert.Equal("C", st.PendingFleets[0].TargetNodeId);
        }

        // A grid a player can see must not be moved out from under them by Core alone; the host
        // handoff for that is the Player Overhaul's.
        [Fact]
        public void Retarget_RefusesMaterialisedFleet()
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            PendingFleetAction f = InFlight(w, t, a, b);
            f.MaterialisedEntityId = 12345;
            st.PendingFleets.Add(f);

            bool ok = PendingFleetResolver.Retarget(st, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test");

            Assert.False(ok);
            Assert.Equal("B", st.PendingFleets[0].TargetNodeId);
            Assert.Equal(0, st.PendingFleets[0].RetargetCount);
        }

        [Fact]
        public void Retarget_RefusesArrivedResolvedOrUnknownNode()
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();

            var arrived = new FactionState { Tag = "F", RngSeed = 1 };
            PendingFleetAction fa = InFlight(w, t, a, b);
            fa.EstimatedArrivalUtc = w.UtcNow.AddMinutes(-1);
            arrived.PendingFleets.Add(fa);
            Assert.False(PendingFleetResolver.Retarget(arrived, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test"));

            var resolved = new FactionState { Tag = "F", RngSeed = 1 };
            PendingFleetAction fr = InFlight(w, t, a, b);
            fr.Resolved = true;
            resolved.PendingFleets.Add(fr);
            Assert.False(PendingFleetResolver.Retarget(resolved, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test"));

            var unknown = new FactionState { Tag = "F", RngSeed = 1 };
            unknown.PendingFleets.Add(InFlight(w, t, a, b));
            Assert.False(PendingFleetResolver.Retarget(unknown, 0, "NOPE",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test"));
        }

        // A dwell is time spent where the fleet already is, not a journey with a destination --
        // a Mine on its drill site and a raider mid-engagement are both unredirectable.
        [Theory]
        [InlineData(FleetStopKind.Mine, ActionKind.Mine)]
        [InlineData(FleetStopKind.Engage, ActionKind.Raid)]
        public void Retarget_RefusesADwell(FleetStopKind stop, ActionKind kind)
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            PendingFleetAction f = InFlight(w, t, a, b, kind);
            f.CurrentStopKind = stop;
            st.PendingFleets.Add(f);

            Assert.False(PendingFleetResolver.Retarget(st, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test"));
        }

        [Fact]
        public void Retarget_PersistsCountAndStampAcrossRoundTrip()
        {
            SpatialNode a, b, c;
            WorldModel w = W(out a, out b, out c);
            FactionTuning t = T();
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.PendingFleets.Add(InFlight(w, t, a, b));
            PendingFleetResolver.Retarget(st, 0, "C",
                PendingFleetResolver.FleetRetargetPolicy.Replace, w, t, new List<string>(), "test");
            PendingFleetAction before = st.PendingFleets[0];

            PendingFleetAction after = RoundTripFleet(st, w.Graph);

            Assert.Equal(before.RetargetCount, after.RetargetCount);
            Assert.Equal(before.LastRetargetUtc, after.LastRetargetUtc);
        }
    }
}
