using System;
using System.Collections.Generic;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-321: a patrol diverted onto a raid joins the defender already there instead of
    /// leaving, and flies the rest of its circuit once no raid is aimed at the node. Live on
    /// 2026-10-07/08, 70 of 71 diverted patrols arrived at a node a Guard already held and went home
    /// without fighting.</summary>
    public class PatrolJoinsDefenceTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
            public int Next(int minInclusive, int maxExclusive) { return minInclusive; }
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner; n.HoldStrength = 0.8; n.Radius = 40;
            return n;
        }

        private static List<WeaponBand> Bands(float dps)
        {
            return new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = 2000 } };
        }

        /// <summary>HOME, the raided node MERC1, and the circuit's next stop MERC2.</summary>
        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "MERC1", "MERC", new Vec3(40, 0, 0));
            AddNode(w, "MERC2", "MERC", new Vec3(80, 0, 0));
            w.Graph.Nodes["MERC1"].Threat = 0.10;   // under the stand floor: only a raid makes it stay
            return w;
        }

        /// <summary>A patrol arriving at MERC1 with MERC2 and the return home still to fly.</summary>
        private static PendingFleetAction ArrivingPatrol(WorldModel w)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Patrol,
                FleetId = "PAT001",
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                ActorTag = "MERC",
                OriginNodeId = "HOME",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(40, 0, 0),
                DepartedUtc = w.UtcNow.AddMinutes(-2),
                EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "MERC2", Kind = FleetStopKind.Service },
                    new FleetStop { NodeId = "HOME", Kind = FleetStopKind.Return }
                },
                WeaponBands = Bands(500),
                LastResolvedSegment = -1,
            };
        }

        private static PendingFleetAction StandingGuard(WorldModel w, string actor)
        {
            DateTime arrived = w.UtcNow.AddMinutes(-5);
            return new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                FleetId = "GRD001",
                ActorTag = actor,
                TargetNodeId = "MERC1",
                OriginNodeId = "HOME",
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                DepartedUtc = arrived,
                EstimatedArrivalUtc = arrived,
                Resolved = true,
                WeaponBands = Bands(500),
            };
        }

        /// <summary>A raid on MERC1 that lands in five minutes -- not this pass.</summary>
        private static PendingFleetAction InboundRaid(WorldModel w)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RVN",
                FleetId = "RAID01",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(200, 0, 0),
                TargetPosKm = new Vec3(40, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(5),
                CurrentStopKind = FleetStopKind.Service,
                WeaponBands = Bands(400),
                LastResolvedSegment = -1,
            };
        }

        private static string Resolve(WorldModel w, params FactionState[] states)
        {
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.0), log, null);
            return string.Join("\n", log);
        }

        private static PendingFleetAction Fleet(FactionState st, string fleetId)
        {
            foreach (PendingFleetAction f in st.PendingFleets)
                if (f.FleetId == fleetId) return f;
            throw new InvalidOperationException("no fleet " + fleetId);
        }

        // The defect: the defender already standing there sent the patrol home.
        [Fact]
        public void APatrolArrivingAtARaidedNodeAGuardHolds_StandsBesideIt()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 2 };
            merc.PendingFleets.Add(StandingGuard(w, "MERC"));
            merc.PendingFleets.Add(ArrivingPatrol(w));
            rvn.PendingFleets.Add(InboundRaid(w));

            string log = Resolve(w, merc, rvn);

            Assert.Contains("PATROL_STANDS [PAT001] MERC @ MERC1", log);
            Assert.Contains("why=raid beside=[GRD001]", log);
            PendingFleetAction patrol = Fleet(merc, "PAT001");
            Assert.Equal(ActionKind.Guard, patrol.Kind);
            Assert.Null(patrol.RemainingStops);
            Assert.Equal(2, patrol.SuspendedStops.Count);
            Assert.Equal("MERC2", patrol.SuspendedStops[0].NodeId);
            // Both are the node's defence now: the meeting path draws its force from every active
            // Guard at the node, so the raid will meet the two hulls together.
            Assert.True(patrol.IsActiveGuard(w.UtcNow, w.Tuning));
            Assert.True(Fleet(merc, "GRD001").IsActiveGuard(w.UtcNow, w.Tuning));
        }

        // An ally's Guard is the node's defence as much as the owner's own.
        [Fact]
        public void AnAlliedGuardIsJoinedToo()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var ally = new FactionState { Tag = "UVCO", RngSeed = 3 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 2 };
            ally.PendingFleets.Add(StandingGuard(w, "UVCO"));
            merc.PendingFleets.Add(ArrivingPatrol(w));
            rvn.PendingFleets.Add(InboundRaid(w));

            string log = Resolve(w, merc, ally, rvn);

            Assert.Contains("beside=[GRD001]", log);
            Assert.Equal(ActionKind.Guard, Fleet(merc, "PAT001").Kind);
        }

        // The control, and FAC-276's case: a node that is only hot needs one garrison, not two.
        [Fact]
        public void WithNoRaid_AHotNodeAGuardHolds_IsLeftToIt()
        {
            WorldModel w = World();
            w.Graph.Nodes["MERC1"].Threat = 0.8;
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            merc.PendingFleets.Add(StandingGuard(w, "MERC"));
            merc.PendingFleets.Add(ArrivingPatrol(w));

            string log = Resolve(w, merc);

            Assert.DoesNotContain("PATROL_STANDS", log);
            Assert.Contains("FLEET_DEPARTED [PAT001] MERC MERC1 -> MERC2", log);
            Assert.Null(Fleet(merc, "PAT001").SuspendedStops);
        }

        // A patrol standing alone is the node's garrison and holds the full window, as before.
        [Fact]
        public void APatrolStandingAlone_KeepsTheDutyWindowAndSetsNoCircuitAside()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 2 };
            merc.PendingFleets.Add(ArrivingPatrol(w));
            rvn.PendingFleets.Add(InboundRaid(w));

            string log = Resolve(w, merc, rvn);

            Assert.Contains("why=raid (holds 30 min)", log);
            Assert.DoesNotContain("beside=", log);
            Assert.Null(Fleet(merc, "PAT001").SuspendedStops);
        }

        // While the raid is still aimed at the node the joined patrol keeps standing.
        [Fact]
        public void AJoinedPatrolHoldsWhileTheRaidIsStillInbound()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 2 };
            merc.PendingFleets.Add(StandingGuard(w, "MERC"));
            merc.PendingFleets.Add(ArrivingPatrol(w));
            rvn.PendingFleets.Add(InboundRaid(w));
            Resolve(w, merc, rvn);

            w.UtcNow = w.UtcNow.AddMinutes(1);
            string log = Resolve(w, merc, rvn);

            Assert.DoesNotContain("PATROL_RESUMES", log);
            Assert.Equal(ActionKind.Guard, Fleet(merc, "PAT001").Kind);
        }

        // The operator's rule: once the hostile is gone, back to the circuit -- the stop it was
        // flying to when it was pulled in, then home.
        [Fact]
        public void OnceTheRaidIsGone_TheJoinedPatrolResumesItsCircuit()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 2 };
            merc.PendingFleets.Add(StandingGuard(w, "MERC"));
            merc.PendingFleets.Add(ArrivingPatrol(w));
            rvn.PendingFleets.Add(InboundRaid(w));
            Resolve(w, merc, rvn);

            rvn.PendingFleets.Clear();
            w.UtcNow = w.UtcNow.AddMinutes(1);
            string log = Resolve(w, merc, rvn);

            Assert.Contains("PATROL_RESUMES [PAT001] MERC @ MERC1 stops=2", log);
            Assert.Contains("FLEET_DEPARTED [PAT001] MERC MERC1 -> MERC2", log);
            PendingFleetAction patrol = Fleet(merc, "PAT001");
            Assert.Equal(ActionKind.Patrol, patrol.Kind);
            Assert.Null(patrol.SuspendedStops);
            Assert.Equal("MERC2", patrol.TargetNodeId);
            Assert.Single(patrol.RemainingStops);
            // The Guard that was there first is untouched: it holds its own window.
            Assert.Equal(ActionKind.Guard, Fleet(merc, "GRD001").Kind);
        }

        // A grid a player can see is the host's to move. It stands out the window instead.
        [Fact]
        public void AMaterialisedJoinedPatrolIsNotMovedFromCore()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 2 };
            merc.PendingFleets.Add(StandingGuard(w, "MERC"));
            merc.PendingFleets.Add(ArrivingPatrol(w));
            rvn.PendingFleets.Add(InboundRaid(w));
            Resolve(w, merc, rvn);
            for (int i = 0; i < merc.PendingFleets.Count; i++)
                if (merc.PendingFleets[i].FleetId == "PAT001")
                {
                    PendingFleetAction f = merc.PendingFleets[i];
                    f.MaterialisedEntityId = 4242;
                    merc.PendingFleets[i] = f;
                }

            rvn.PendingFleets.Clear();
            w.UtcNow = w.UtcNow.AddMinutes(1);
            string log = Resolve(w, merc, rvn);

            Assert.DoesNotContain("PATROL_RESUMES", log);
            Assert.Equal(ActionKind.Guard, Fleet(merc, "PAT001").Kind);
        }

        // A restart between the join and the resume must not lose the circuit.
        [Fact]
        public void TheSetAsideCircuitSurvivesTheBrainFile()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            var rvn = new FactionState { Tag = "RVN", RngSeed = 2 };
            merc.PendingFleets.Add(StandingGuard(w, "MERC"));
            merc.PendingFleets.Add(ArrivingPatrol(w));
            rvn.PendingFleets.Add(InboundRaid(w));
            Resolve(w, merc, rvn);

            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(merc, new SpatialNode[0], 10));
            FactionState back = FactionStore.ApplyBrainDoc(JsonConvert.DeserializeObject<BrainDoc>(json), new SpatialGraph());

            PendingFleetAction patrol = Fleet(back, "PAT001");
            Assert.Equal(2, patrol.SuspendedStops.Count);
            Assert.Equal("MERC2", patrol.SuspendedStops[0].NodeId);
            Assert.Equal(FleetStopKind.Return, patrol.SuspendedStops[1].Kind);
            Assert.Null(Fleet(back, "GRD001").SuspendedStops);
        }

        // Lean JSON: a fleet with nothing set aside writes no key.
        [Fact]
        public void AFleetWithNothingSetAsideWritesNoKey()
        {
            WorldModel w = World();
            var merc = new FactionState { Tag = "MERC", RngSeed = 1 };
            merc.PendingFleets.Add(StandingGuard(w, "MERC"));
            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(merc, new SpatialNode[0], 10));
            Assert.DoesNotContain("SuspendedStops", json);
        }
    }
}
