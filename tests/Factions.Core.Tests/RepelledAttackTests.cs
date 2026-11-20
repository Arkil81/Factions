using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A repelled attack is still an attack (FAC-275 slice 1). Every intercept terminal files the
    /// same <see cref="RaidIncident"/> a landed raid does, so the victim's standing toward the
    /// attacker sours and <c>LastHostileActUtc</c> stamps whether the defence succeeded or not —
    /// and the line the drain logs for it can never be counted as a landed raid.
    /// </summary>
    public class RepelledAttackTests
    {
        private static IRandom AlwaysWin() { return new FixedRandom(0.0); }

        private static List<WeaponBand> Bands(float dps, float rangeM = 2000)
        {
            return new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos, double hold = 0.8)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionTuning SlowFleetTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.FleetSpeedKmPerMin = 1.0;
            t.FleetSpeedMinKmPerMin = 0.999;
            t.FleetAccelKmPerMin2 = 1000000.0;
            return t;
        }

        /// <summary>Drains the world's incidents the way the host does, collecting the lines.</summary>
        private static List<string> Drain(IList<FactionState> states, WorldModel w)
        {
            var lines = new List<string>();
            Diplomacy.ApplyIncidents(states, w, w.Tuning, lines.Add);
            return lines;
        }

        // ---- the three intercept terminals ------------------------------------------------------

        [Fact]
        public void AGuardIntercept_FilesARepelledIncident_AndSoursTheVictim()
        {
            WorldModel w = TestSupport.World();
            var defender = new FactionState { Tag = "MERC" };
            var raider   = new FactionState { Tag = "RVN" };
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            DateTime now = w.UtcNow;
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5), Resolved = true,
                WeaponBands = Bands(600),
            });
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", ConvoySize = 3,
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = Bands(60),
            });
            var states = new List<FactionState> { defender, raider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains(log, l => l.StartsWith("GUARD_INTERCEPT"));
            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.True(inc.Repelled, "an attack destroyed before it landed is still an attack");
            Assert.Equal("RVN", inc.Attacker);
            Assert.Equal("MERC", inc.Victim);
            Assert.Equal("MERC1", inc.NodeId);
            Assert.Equal(ActionKind.Raid, inc.Kind);

            List<string> dip = Drain(states, w);
            Assert.Equal(-w.Tuning.RaidRelationHit, defender.RelationTo("RVN"), 6);
            Assert.True(defender.LastHostileActUtc.ContainsKey("RVN"), "the blow has to stamp the victim's clock");
            Assert.Equal(w.UtcNow, defender.LastHostileActUtc["RVN"]);
            Assert.Contains("ATTACK_REPELLED RVN -> MERC @ MERC1", dip);
            // Peers count landed raids by this substring; an attempt must never match it.
            Assert.DoesNotContain(dip, l => l.Contains(" RAID RVN -> ") || l.StartsWith("RAID "));
        }

        [Fact]
        public void AStationIntercept_FilesARepelledIncident()
        {
            WorldModel w = TestSupport.World();
            var defender = new FactionState { Tag = "MERC" };
            var raider   = new FactionState { Tag = "RVN" };
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.StationPrefabId = "Economy_Outpost_11";
            target.StationWeapons = Bands(5000, 3000);
            DateTime now = w.UtcNow;
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", ConvoySize = 1,
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = Bands(1),
            });
            var states = new List<FactionState> { defender, raider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains(log, l => l.StartsWith("STATION_INTERCEPT"));
            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.True(inc.Repelled);
            Assert.Equal("MERC", inc.Victim);
            Assert.Equal("MERC1", inc.NodeId);

            List<string> dip = Drain(states, w);
            Assert.Equal(-w.Tuning.RaidRelationHit, defender.RelationTo("RVN"), 6);
            Assert.Contains("ATTACK_REPELLED RVN -> MERC @ MERC1", dip);
        }

        // The mid-transit meeting is the case the node label cannot be trusted for: the fight
        // happens at a waypoint nobody owns, and the faction that was attacked is the one the
        // fleet was flying at.
        [Fact]
        public void AMidTransitIntercept_FilesTheIncidentAgainstTheMissionTarget_NotTheMeetingNode()
        {
            WorldModel w = TestSupport.World();
            w.Tuning = SlowFleetTuning();
            AddNode(w, "A", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "MID", null, new Vec3(50, 0, 0));
            AddNode(w, "B", "MERC", new Vec3(100, 0, 0));
            w.Graph.Connect("A", "MID");
            w.Graph.Connect("MID", "B");
            DateTime t0 = w.UtcNow;

            var defender = new FactionState { Tag = "MERC" };
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "B",
                OriginNodeId = "A", OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(100, 0, 0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "A", "MID", "B" }, LastResolvedSegment = -1,
                WeaponBands = Bands(1000),
            });
            var raider = new FactionState { Tag = "RVN" };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "A",
                OriginNodeId = "B", OriginPosKm = new Vec3(100, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "B", "MID", "A" }, LastResolvedSegment = -1,
            });
            var states = new List<FactionState> { defender, raider };
            var log = new List<string>();
            w.UtcNow = t0.AddMinutes(51);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains(log, l => l.StartsWith("GUARD_INTERCEPT") && l.Contains("@MID"));
            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.True(inc.Repelled);
            Assert.Equal("MERC", inc.Victim);
            Assert.Equal("A", inc.NodeId);

            List<string> dip = Drain(states, w);
            Assert.Equal(-w.Tuning.RaidRelationHit, defender.RelationTo("RVN"), 6);
            Assert.Contains("ATTACK_REPELLED RVN -> MERC @ A", dip);
        }

        // A Conquer stopped on the way in is a repelled Conquer, and the drain has to keep charging
        // it at the harsher Conquer rate — the incident Kind is what selects that.
        [Fact]
        public void ARepelledConquer_KeepsTheConquerRate()
        {
            WorldModel w = TestSupport.World();
            var defender = new FactionState { Tag = "MERC" };
            var raider   = new FactionState { Tag = "RVN" };
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.1);
            DateTime now = w.UtcNow;
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5), Resolved = true,
                WeaponBands = Bands(600),
            });
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Conquer, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", ConvoySize = 1,
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = Bands(1),
            });
            var states = new List<FactionState> { defender, raider };
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), new List<string>(), null);

            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.True(inc.Repelled);
            Assert.Equal(ActionKind.Conquer, inc.Kind);
            Assert.Equal("MERC", w.Graph.Nodes["MERC1"].OwnerFactionTag);   // the node never changed hands

            List<string> dip = Drain(states, w);
            Assert.Equal(-w.Tuning.ConquerRelationHit, defender.RelationTo("RVN"), 6);
            Assert.Contains("ATTACK_REPELLED RVN -> MERC @ MERC1", dip);
        }

        // ---- what must NOT file one -------------------------------------------------------------

        // A convoy run down mid-route is a robbery, not an attack on a node's owner: the raid is
        // still flying and has attacked nobody's territory yet.
        [Fact]
        public void AnInterceptedTradeConvoy_FilesNoIncident()
        {
            WorldModel w = TestSupport.World();
            w.Tuning = SlowFleetTuning();
            AddNode(w, "TO", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "MID", null, new Vec3(50, 0, 0));
            AddNode(w, "RO", "RVN", new Vec3(100, 0, 0));
            w.Graph.Connect("TO", "MID");
            w.Graph.Connect("MID", "RO");
            w.SetTreasury("RVN", 50000);
            DateTime t0 = w.UtcNow;

            var merc = new FactionState { Tag = "MERC" };
            merc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(100, 0, 0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = 6000, PathNodeIds = new[] { "TO", "MID", "RO" }, LastResolvedSegment = -1,
            });
            var rvn = new FactionState { Tag = "RVN" };
            rvn.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "TO",
                OriginNodeId = "RO", OriginPosKm = new Vec3(100, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = 0, PathNodeIds = new[] { "RO", "MID", "TO" }, LastResolvedSegment = -1,
            });
            var states = new List<FactionState> { merc, rvn };
            var log = new List<string>();
            w.UtcNow = t0.AddMinutes(51);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains(log, l => l.StartsWith("RAID_LOOTS"));   // the path under test really ran
            Assert.Empty(w.Incidents);
            Assert.Equal(0.0, merc.RelationTo("RVN"));
        }

        [Fact]
        public void ARaidThatLands_StillFilesExactlyOneIncident_AndIsNotRepelled()
        {
            WorldModel w = TestSupport.World();
            var defender = new FactionState { Tag = "MERC" };
            var raider   = new FactionState { Tag = "RVN" };
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            DateTime now = w.UtcNow;
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", ConvoySize = 2,
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = Bands(900),
            });
            var states = new List<FactionState> { defender, raider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains(log, l => l.StartsWith("RAID_RESOLVED"));
            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.False(inc.Repelled, "a raid that landed must not be filed twice, nor as an attempt");

            List<string> dip = Drain(states, w);
            Assert.Contains("RAID RVN -> MERC @ MERC1", dip);
            Assert.DoesNotContain(dip, l => l.StartsWith("ATTACK_REPELLED"));
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }
    }
}
