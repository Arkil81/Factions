using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-314: a patrol stays only where staying does something. `Threat &gt; 0` was sound
    /// while a patrol could only reach a node scoring above 0.5; FAC-309 slice 3 made a circuit visit
    /// every member of its focus region, so the old bar admitted nearly all of them.</summary>
    public class PatrolStandThresholdTests
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

        private static PendingFleetAction ArrivingPatrol(WorldModel w, string originId, string nodeId)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Patrol,
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                ActorTag = "MERC",
                OriginNodeId = originId,
                TargetNodeId = nodeId,
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = originId, Kind = FleetStopKind.Return }
                },
                WeaponBands = Bands(500),
                LastResolvedSegment = -1,
            };
        }

        /// <summary>A raid aimed at <paramref name="nodeId"/>, optionally flying a clearance detour —
        /// in which case its own TargetNodeId is null and only the queued stop names the node.</summary>
        private static PendingFleetAction InboundRaid(WorldModel w, string actor, string nodeId,
                                                      bool onClearanceLeg = false)
        {
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = actor,
                FleetId = "RAID01",
                OriginPosKm = new Vec3(50, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(5),
                WeaponBands = Bands(400),
                LastResolvedSegment = -1,
            };
            if (onClearanceLeg)
            {
                raid.CurrentStopKind = FleetStopKind.Navigation;
                raid.TargetNodeId = null;
                raid.RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = nodeId, Kind = FleetStopKind.Service }
                };
            }
            else
            {
                raid.CurrentStopKind = FleetStopKind.Service;
                raid.TargetNodeId = nodeId;
            }
            return raid;
        }

        private static List<string> Resolve(WorldModel w, params FactionState[] states)
        {
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.0), log, null);
            return log;
        }

        // The defect. Live on v0.224.0, 10 of 12 stands were at threat 0.22 or below and five of
        // them below 0.005 -- each committing a hull for GuardDuration and taking the node's one
        // defender slot.
        [Fact]
        public void AColdNodeIsNotGarrisoned()
        {
            WorldModel w = TestSupport.World();
            var st = new FactionState { Tag = "MERC", RngSeed = 1 };
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.25;   // 0.20 after the sweep's own -0.05, under the 0.35 floor
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1"));

            string joined = string.Join("\n", Resolve(w, st));

            Assert.DoesNotContain("PATROL_STANDS", joined);
            Assert.Contains("PATROL_AT", joined);
            Assert.Equal(ActionKind.Patrol, st.PendingFleets[0].Kind);
        }

        // FAC-257's own case, which must survive: a genuinely hot node still gets its garrison.
        [Fact]
        public void AHotNodeIsStillGarrisoned()
        {
            WorldModel w = TestSupport.World();
            var st = new FactionState { Tag = "MERC", RngSeed = 1 };
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.8;
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1"));

            string joined = string.Join("\n", Resolve(w, st));

            Assert.Contains("PATROL_STANDS", joined);
            Assert.Contains("why=threat", joined);
            Assert.Equal(ActionKind.Guard, st.PendingFleets[0].Kind);
        }

        // The principled half: an actual inbound raid is worth staying for at any threat level,
        // which is the same question Guard's own dispatch asks.
        [Fact]
        public void AnInboundRaidIsWorthStayingForEvenAtTrivialThreat()
        {
            WorldModel w = TestSupport.World();
            var def = new FactionState { Tag = "MERC", RngSeed = 1 };
            var raider = new FactionState { Tag = "RVN", RngSeed = 2 };
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.10;   // 0.05 after the sweep -- far under the floor
            def.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1"));
            raider.PendingFleets.Add(InboundRaid(w, "RVN", "MERC1"));

            string joined = string.Join("\n", Resolve(w, def, raider));

            Assert.Contains("PATROL_STANDS", joined);
            Assert.Contains("why=raid", joined);
            Assert.Equal(ActionKind.Guard, def.PendingFleets[0].Kind);
        }

        // A raid on a planet-clearance detour has a null TargetNodeId and names its real destination
        // only in a queued stop. Measured 2026-09-02/03: 22 of 61 combat dispatches were in that
        // state, so a check reading TargetNodeId alone is blind to better than a third of raids.
        [Fact]
        public void ARaidOnAClearanceLegStillCounts()
        {
            WorldModel w = TestSupport.World();
            var def = new FactionState { Tag = "MERC", RngSeed = 1 };
            var raider = new FactionState { Tag = "RVN", RngSeed = 2 };
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.10;
            def.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1"));
            raider.PendingFleets.Add(InboundRaid(w, "RVN", "MERC1", onClearanceLeg: true));

            string joined = string.Join("\n", Resolve(w, def, raider));

            Assert.Contains("PATROL_STANDS", joined);
            Assert.Contains("why=raid", joined);
        }

        // A combat fleet keeps its Kind on the way home, so a raider flying its Return leg reads as
        // an inbound raid against its OWN node. Without the owner filter every returning raider
        // would pin a garrison at home for GuardDuration.
        [Fact]
        public void AReturningRaiderIsNotAThreatToItsOwnNode()
        {
            WorldModel w = TestSupport.World();
            var st = new FactionState { Tag = "MERC", RngSeed = 1 };
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.10;
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1"));
            // MERC's own raider heading home to MERC1 -- same Kind, own node.
            st.PendingFleets.Add(InboundRaid(w, "MERC", "MERC1"));

            string joined = string.Join("\n", Resolve(w, st));

            Assert.DoesNotContain("PATROL_STANDS", joined);
        }

        // The key is a real lever, not decoration: zero restores the pre-FAC-314 behaviour exactly.
        [Fact]
        public void AZeroFloorRestoresTheOldStayWhereverAnythingIsLeftBehaviour()
        {
            WorldModel w = TestSupport.World();
            w.Tuning.PatrolStandThreatFloor = 0;
            var st = new FactionState { Tag = "MERC", RngSeed = 1 };
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.10;
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1"));

            Assert.Contains("PATROL_STANDS", string.Join("\n", Resolve(w, st)));
        }

        // The floor is a floor, not a strict threshold -- a node sitting exactly on it stays.
        [Fact]
        public void ANodeExactlyOnTheFloorIsGarrisoned()
        {
            WorldModel w = TestSupport.World();
            var st = new FactionState { Tag = "MERC", RngSeed = 1 };
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = w.Tuning.PatrolStandThreatFloor + 0.05;   // exactly the floor after -0.05
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1"));

            Assert.Contains("PATROL_STANDS", string.Join("\n", Resolve(w, st)));
        }
    }
}
