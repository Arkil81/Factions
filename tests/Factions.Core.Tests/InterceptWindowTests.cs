using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-307: a caught convoy and the raid that caught it hold at the meeting node for
    /// a window a player could settle, and the encounter is decided when the window ends.</summary>
    public class InterceptWindowTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static WorldModel MakeWorld() => TestSupport.World(1);
        private static FactionState MakeState(string tag) => new FactionState { Tag = tag, RngSeed = 1 };

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner; n.HoldStrength = 0.8; n.Radius = 40;
            return n;
        }

        /// <summary>An unarmed MERC convoy and an RVN raid both arriving at OTH's node X this tick.
        /// The raid carries a FleetId, as every live raid does -- without one no window can open.</summary>
        private static void ArrivalMeeting(WorldModel w, out FactionState raider, out FactionState trader,
                                           out List<FactionState> states, string nodeOwner = "OTH")
        {
            AddNode(w, "X", nodeOwner, new Vec3(0, 0, 0));
            AddNode(w, "RVN1", "RVN", new Vec3(50, 0, 0));
            AddNode(w, "MERC1", "MERC", new Vec3(0, 50, 0));
            w.SetTreasury("RVN", 50000);
            DateTime now = w.UtcNow;
            raider = MakeState("RVN");
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = nodeOwner, TargetNodeId = "X",
                OriginNodeId = "RVN1", OriginPosKm = new Vec3(50, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now, FleetId = "RAID01",
                ShipPrefabId = "Corsair", ConvoySize = 1,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "RVN1", Kind = FleetStopKind.Return } },
            });
            trader = MakeState("MERC");
            trader.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = nodeOwner, TargetNodeId = "X",
                OriginNodeId = "MERC1", ReturnNodeId = "MERC1", OriginPosKm = new Vec3(0, 50, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now, FleetId = "TRADE1",
                ShipPrefabId = "Spadi-Space", ConvoySize = 1, CargoValue = 1000,
                Cargo = new Dictionary<string, long> { { "Ingot:Iron", 1000 } },
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "MERC1", Kind = FleetStopKind.Return } },
            });
            states = new List<FactionState> { raider, trader };
        }

        [Fact]
        public void Caught_OpensAWindow_HoldingBothAtTheMeetingNode()
        {
            WorldModel w = MakeWorld();
            FactionState raider, trader; List<FactionState> states;
            ArrivalMeeting(w, out raider, out trader, out states);
            DateTime now = w.UtcNow;
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            string open = Assert.Single(log, l => l.StartsWith("INTERCEPT_WINDOW "));
            Assert.Contains("[RAID01] RVN holds [TRADE1] MERC @X", open);
            Assert.DoesNotContain(log, l => l.StartsWith("RAID_LOOTS") || l.StartsWith("TRADE_ESCAPES") || l.StartsWith("RAID_RESOLVED"));

            PendingFleetAction t = Assert.Single(trader.PendingFleets);
            Assert.Equal(FleetStopKind.Intercept, t.CurrentStopKind);
            Assert.Equal("RAID01", t.InterceptedByFleetId);
            Assert.Equal("X", t.TargetNodeId);
            Assert.Equal(now.AddSeconds(w.Tuning.InterceptWindowSeconds), t.EstimatedArrivalUtc);
            Assert.Equal(FleetStopKind.Service, t.RemainingStops[0].Kind);   // the interrupted arrival, re-queued
            Assert.Equal("X", t.RemainingStops[0].NodeId);
            Assert.Equal(FleetStopKind.Return, t.RemainingStops[1].Kind);

            PendingFleetAction r = Assert.Single(raider.PendingFleets);
            Assert.Equal(FleetStopKind.Intercept, r.CurrentStopKind);
            Assert.Equal(t.EstimatedArrivalUtc, r.EstimatedArrivalUtc);    // same clock
            Assert.Equal(0.8, w.Graph.Nodes["X"].HoldStrength);            // the raid has not landed
            Assert.Equal(w.Tuning.InterceptThreat, w.Graph.Nodes["X"].Threat, 6);   // a visit's worth of threat
        }

        [Fact]
        public void WindowEnds_DecidesTheEncounter_AndBothFlyOn()
        {
            WorldModel w = MakeWorld();
            FactionState raider, trader; List<FactionState> states;
            ArrivalMeeting(w, out raider, out trader, out states);
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);
            Assert.Contains(log, l => l.StartsWith("INTERCEPT_WINDOW "));
            log.Clear();

            // Nobody touched it. 0.5 misses the 0.15 hull roll on foreign ground: robbed, hull kept.
            w.UtcNow = w.UtcNow.AddSeconds(w.Tuning.InterceptWindowSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            string loots = Assert.Single(log, l => l.StartsWith("RAID_LOOTS "));
            Assert.Contains("[TRADE1] MERC @X", loots);
            Assert.Contains("hull=survived", loots);
            Assert.Contains("(window)", loots);
            PendingFleetAction t = Assert.Single(trader.PendingFleets);
            Assert.NotEqual(FleetStopKind.Intercept, t.CurrentStopKind);
            Assert.Equal("RAID01", t.InterceptedByFleetId);   // kept: this pair does not meet again
            Assert.Equal(500, t.Cargo["Ingot:Iron"]);                     // half went to the pirate
            Assert.Contains(log, l => l.StartsWith("FLEET_DEPARTED [TRADE1]") && l.Contains("stop=Service"));
            Assert.Contains(log, l => l.StartsWith("FLEET_DEPARTED [RAID01]") && l.Contains("stop=Service"));
            Assert.Single(raider.PendingFleets);
        }

        [Fact]
        public void RaidDestroyedInTheWindow_FreesTheConvoy()
        {
            WorldModel w = MakeWorld();
            FactionState raider, trader; List<FactionState> states;
            ArrivalMeeting(w, out raider, out trader, out states);
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);
            log.Clear();

            // A player killed the raider mid-window: the host writes the loss and force-arrives it.
            PendingFleetAction r = raider.PendingFleets[0];
            r.ForcedRatio = 0.0; r.ForcedRatioCause = "grid_destroyed";
            raider.PendingFleets[0] = r;

            w.UtcNow = w.UtcNow.AddSeconds(w.Tuning.InterceptWindowSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            Assert.Contains(log, l => l.StartsWith("INTERCEPT_FREED [TRADE1] MERC") && l.Contains("why=raid_gone"));
            Assert.DoesNotContain(log, l => l.StartsWith("RAID_LOOTS"));
            PendingFleetAction t = Assert.Single(trader.PendingFleets);
            Assert.Equal(1000, t.Cargo["Ingot:Iron"]);                    // nothing taken
            Assert.Contains(log, l => l.StartsWith("FLEET_DEPARTED [TRADE1]"));
        }

        [Fact]
        public void ConvoyDestroyedInTheWindow_IsLostToThePirate()
        {
            WorldModel w = MakeWorld();
            FactionState raider, trader; List<FactionState> states;
            ArrivalMeeting(w, out raider, out trader, out states);
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);
            log.Clear();
            w.ClearPendingEvents();

            PendingFleetAction t0 = trader.PendingFleets[0];
            t0.ForcedRatio = 0.0; t0.ForcedRatioCause = "grid_destroyed";
            trader.PendingFleets[0] = t0;

            w.UtcNow = w.UtcNow.AddSeconds(w.Tuning.InterceptWindowSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            string loots = Assert.Single(log, l => l.StartsWith("RAID_LOOTS "));
            Assert.Contains("hull=lost why=grid_destroyed", loots);
            string lost = Assert.Single(log, l => l.StartsWith("SHIP_LOST [TRADE1] MERC"));
            Assert.Contains("why=intercepted:RVN", lost);
            Assert.Empty(trader.PendingFleets);
            Assert.Contains(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost && e.ActorTag == "MERC");
            Assert.Single(raider.PendingFleets);                            // the pirate flies on
        }

        [Fact]
        public void WindowTunedOff_DecidesOnContactAsBefore()
        {
            WorldModel w = MakeWorld();
            w.Tuning.InterceptWindowSeconds = 0;
            FactionState raider, trader; List<FactionState> states;
            ArrivalMeeting(w, out raider, out trader, out states);
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            Assert.DoesNotContain(log, l => l.StartsWith("INTERCEPT_WINDOW"));
            Assert.Contains(log, l => l.StartsWith("RAID_LOOTS ") && l.Contains("[TRADE1] MERC"));
        }

        [Fact]
        public void MidTransitCatch_HoldsAtTheWaypoint_ThenFliesOnToTheOriginalDestination()
        {
            WorldModel w = MakeWorld();
            FactionTuning t = w.Tuning;
            t.FleetSpeedKmPerMin = 1.0; t.FleetSpeedMinKmPerMin = 0.999; t.FleetAccelKmPerMin2 = 1000000.0;
            AddNode(w, "TO", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "MID", null,  new Vec3(50, 0, 0));
            AddNode(w, "RO", "RVN",  new Vec3(100, 0, 0));
            w.Graph.Connect("TO", "MID"); w.Graph.Connect("MID", "RO");
            w.SetTreasury("RVN", 50000);
            DateTime t0 = w.UtcNow;
            var trader = MakeState("MERC");
            trader.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO", FleetId = "TRADE1",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100), CargoValue = 6000,
                PathNodeIds = new[] { "TO", "MID", "RO" }, LastResolvedSegment = -1
            });
            var raider = MakeState("RVN");
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "TO", FleetId = "RAID01",
                OriginNodeId = "RO", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "RO", "MID", "TO" }, LastResolvedSegment = -1
            });
            var states = new List<FactionState> { trader, raider };
            var log = new List<string>();

            w.UtcNow = t0.AddMinutes(51);
            PendingFleetResolver.ResolveArrivals(states, w, t, new FixedRandom(0.5), log, null);

            Assert.Contains(log, l => l.StartsWith("INTERCEPT_WINDOW ") && l.Contains("@MID"));
            PendingFleetAction tr = trader.PendingFleets[0];
            Assert.Equal(FleetStopKind.Intercept, tr.CurrentStopKind);
            Assert.Equal("MID", tr.TargetNodeId);
            Assert.Equal("RO", tr.RemainingStops[0].NodeId);            // the interrupted leg, re-queued
            Assert.Equal(FleetStopKind.Service, tr.RemainingStops[0].Kind);
            Assert.Null(tr.PathNodeIds);                                  // no waypoints while held

            log.Clear();
            w.UtcNow = w.UtcNow.AddSeconds(t.InterceptWindowSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, t, new FixedRandom(0.5), log, null);

            Assert.Contains(log, l => l.StartsWith("RAID_LOOTS ") && l.Contains("@MID"));
            Assert.Contains(log, l => l.StartsWith("FLEET_DEPARTED [TRADE1] MERC MID -> RO stop=Service"));
            Assert.Contains(log, l => l.StartsWith("FLEET_DEPARTED [RAID01] RVN MID -> TO stop=Service"));
        }

        [Fact]
        public void Brain_RoundTripsTheHeldConvoy()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "X",
                CurrentStopKind = FleetStopKind.Intercept, InterceptedByFleetId = "RAID01",
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "D1", Kind = FleetStopKind.Service } },
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Equal("RAID01", doc.PendingFleets[0].InterceptedByFleetId);
            Assert.Equal("Intercept", doc.PendingFleets[0].CurrentStopKind);
            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(FleetStopKind.Intercept, back.PendingFleets[0].CurrentStopKind);
            Assert.Equal("RAID01", back.PendingFleets[0].InterceptedByFleetId);

            st.PendingFleets[0] = new PendingFleetAction { Kind = ActionKind.Trade, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "D1" };
            BrainDoc plain = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Null(plain.PendingFleets[0].InterceptedByFleetId);
        }
            [Fact]
        public void AfterTheVerdict_TheSamePairIsNotCaughtAgain()
        {
            WorldModel w = MakeWorld();
            FactionState raider, trader; List<FactionState> states;
            ArrivalMeeting(w, out raider, out trader, out states);
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);   // window opens
            w.UtcNow = w.UtcNow.AddSeconds(w.Tuning.InterceptWindowSeconds);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);   // robbed, both re-fly to X
            log.Clear();

            // Both re-arrive at X in the same pass: the pair has met, so no second window and no
            // second robbery -- the raid just resolves its own arrival.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            Assert.DoesNotContain(log, l => l.StartsWith("INTERCEPT_WINDOW") || l.StartsWith("RAID_LOOTS") || l.StartsWith("TRADE_ESCAPES"));
            Assert.Contains(log, l => l.StartsWith("RAID_RESOLVED [RAID01]") || l.StartsWith("STATION_DEFENSE [RAID01]") || l.StartsWith("STATION_INTERCEPT"));
            PendingFleetAction t = Assert.Single(trader.PendingFleets);
            Assert.NotEqual(FleetStopKind.Intercept, t.CurrentStopKind);
        }

        [Fact]
        public void ARaidHoldingAtOneWaypoint_DoesNotOpenAWindowAtAnotherInTheSamePass()
        {
            // Raid RO -> M2 -> M1 -> TO; convoy A TO -> M1 -> M2 -> RO; convoy B leaves M1 at
            // t=20 for RO. At t=76 min the raid has entered M2 and M1, A has entered M1 and M2, B
            // has entered M2 (and not arrived): two waypoints each hold a meeting, but the raid can
            // hold at one.
            WorldModel w = MakeWorld();
            FactionTuning t = w.Tuning;
            t.FleetSpeedKmPerMin = 1.0; t.FleetSpeedMinKmPerMin = 0.999; t.FleetAccelKmPerMin2 = 1000000.0;
            AddNode(w, "TO", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "M1", null,   new Vec3(33, 0, 0));
            AddNode(w, "M2", null,   new Vec3(66, 0, 0));
            AddNode(w, "RO", "RVN",  new Vec3(100, 0, 0));
            w.Graph.Connect("TO", "M1"); w.Graph.Connect("M1", "M2"); w.Graph.Connect("M2", "RO");
            w.SetTreasury("RVN", 50000);
            DateTime t0 = w.UtcNow;
            var trader = MakeState("MERC");
            trader.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO", FleetId = "TRADEA",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100), CargoValue = 6000,
                PathNodeIds = new[] { "TO", "M1", "M2", "RO" }, LastResolvedSegment = -1
            });
            trader.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO", FleetId = "TRADEB",
                OriginNodeId = "M1", OriginPosKm = new Vec3(33,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0.AddMinutes(20), EstimatedArrivalUtc = t0.AddMinutes(87), CargoValue = 6000,   // at t=76: past M2, not yet arrived
                PathNodeIds = new[] { "M1", "M2", "RO" }, LastResolvedSegment = -1
            });
            // Convoy C left TO 40 minutes later: by t=76 it has entered M1 only, so M1 has a
            // convoy of its own even after A is held at whichever node is processed first.
            trader.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO", FleetId = "TRADEC",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0.AddMinutes(40), EstimatedArrivalUtc = t0.AddMinutes(140), CargoValue = 6000,
                PathNodeIds = new[] { "TO", "M1", "M2", "RO" }, LastResolvedSegment = -1
            });
            var raider = MakeState("RVN");
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "TO", FleetId = "RAID01",
                OriginNodeId = "RO", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "RO", "M2", "M1", "TO" }, LastResolvedSegment = -1
            });
            var states = new List<FactionState> { trader, raider };
            var log = new List<string>();

            w.UtcNow = t0.AddMinutes(76);
            PendingFleetResolver.ResolveArrivals(states, w, t, new FixedRandom(0.5), log, null);

            var windows = log.FindAll(l => l.StartsWith("INTERCEPT_WINDOW "));
            Assert.NotEmpty(windows);
            string heldAt = raider.PendingFleets[0].TargetNodeId;
            Assert.Equal(FleetStopKind.Intercept, raider.PendingFleets[0].CurrentStopKind);
            Assert.All(windows, l => Assert.Contains("@" + heldAt + " ", l));   // every window is where the raid is
        }
    }
}
