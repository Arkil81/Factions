using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>An attack whose lead grid is destroyed, captured or disabled in the world has lost its
    /// meeting, however much of its dwell had run: no effect, no loot, no siege left, the survivors
    /// home. Built on the two live samples at N600 (2026-09-29): SPRT's [8B6A77] lost its lead 0.86 of
    /// the way through a dwell with 8 escorts and [B22322] 0.96 of the way with 9, and both still
    /// resolved their raid.</summary>
    public class LeadLossTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static IRandom NoJitter() { return new FixedRandom(0.5); }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.8;
            n.Radius = 40;
            return n;
        }

        /// <summary>SPRT's squadron of <paramref name="escorts"/>+1 Gunships dispatched on EXP's N600
        /// from S1, and advanced onto its first engagement dwell. Siege cycles are left queued.</summary>
        private static FactionState DwellingSquadron(WorldModel w, int escorts, out List<FactionState> states,
                                                     out SpatialNode target, ActionKind kind = ActionKind.Raid)
        {
            AddNode(w, "S1", "SPRT", new Vec3(0, 0, 0));
            target = AddNode(w, "N600", "EXP", new Vec3(100, 0, 0));
            var sprt = new FactionState { Tag = "SPRT", RngSeed = 1 };
            states = new List<FactionState> { sprt, new FactionState { Tag = "EXP", RngSeed = 1 } };
            PendingFleetAction raid = PendingFleetResolver.CreatePending(
                kind, "SPRT", new FactionIntent { Kind = kind, TargetNodeId = "N600", Cost = 0 },
                w, w.Tuning, immediate: false);
            var squadron = new List<string>();
            for (int i = 0; i <= escorts; i++) squadron.Add("Gunship");
            raid.Squadron = squadron;
            raid.ShipPrefabId = "Gunship";
            raid.ConvoySize = squadron.Count;
            raid.FleetId = "8B6A77";
            sprt.PendingFleets.Add(raid);

            w.UtcNow = raid.EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), new List<string>(), null);
            Assert.Equal(FleetStopKind.Engage, sprt.PendingFleets[0].CurrentStopKind);
            return sprt;
        }

        /// <summary>Exactly what the host writes when the lead grid is lost in combat with its escorts
        /// still flying: the lead's hull slot gone, the destroyed sentinel, ratio zero, force-arrived,
        /// the escorts kept for the departure.</summary>
        private static void LoseLead(FactionState st, WorldModel w, string cause, int escorts)
        {
            PendingFleetAction f = st.PendingFleets[0];
            Assert.True(PendingFleetResolver.MissionHangsOnLead(f));
            f.MarkHullLostAt(0);
            f.MaterialisedEntityId = -1;
            f.EscortEntityIds = new List<long>();
            for (int i = 0; i < escorts; i++) f.EscortEntityIds.Add(1000 + i);
            f.EstimatedArrivalUtc = w.UtcNow;
            f.ForcedRatio = 0.0;
            f.ForcedRatioCause = cause;
            st.PendingFleets[0] = f;
        }

        private static string Line(IEnumerable<string> log, string prefix)
        {
            return log.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("grid_disabled", 0.86, 8)]
        [InlineData("grid_disabled", 0.96, 9)]
        [InlineData("grid_destroyed", 0.86, 8)]
        [InlineData("grid_destroyed", 0.96, 9)]
        [InlineData("grid_captured", 0.96, 9)]
        public void ALeadLostLateInTheDwellLosesTheRaid(string cause, double elapsed, int escorts)
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState sprt = DwellingSquadron(w, escorts, out states, out target);
            Assert.True(sprt.PendingFleets[0].RemainingStops.Any(s => s.Kind == FleetStopKind.Engage));   // siege cycles queued
            double holdBefore = target.HoldStrength;

            PendingFleetAction dwelling = sprt.PendingFleets[0];
            w.UtcNow = dwelling.DepartedUtc.AddSeconds((dwelling.EstimatedArrivalUtc - dwelling.DepartedUtc).TotalSeconds * elapsed);
            LoseLead(sprt, w, cause, escorts);

            var log = new List<string>();
            var closed = new List<long>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null, closed.Add);

            Assert.Null(Line(log, "RAID_RESOLVED"));
            Assert.Equal(holdBefore, target.HoldStrength, 9);                // no hold stripped
            string stop = Line(log, "RAID_BROKE_OFF [8B6A77] SPRT vs station @N600 ratio=0.00");
            Assert.NotNull(stop);
            Assert.Contains("hulls_lost=1/" + (escorts + 1), stop);
            Assert.Contains("src=physical (lead_lost=" + cause + " stop=Engage)", stop);
            string outcome = Line(log, "COMBAT_OUTCOME [8B6A77] SPRT");
            Assert.Contains("attack=BrokeOff", outcome);
            Assert.Contains("next=withdraw", outcome);

            // The fleet is gone with every siege cycle it had left, and its escorts were handed back
            // to the host to fly out.
            Assert.Empty(sprt.PendingFleets);
            Assert.Equal(escorts, closed.Count);
            // The survivors are home, the lead is not.
            int stock;
            w.Graph.Nodes["S1"].ShipStock.TryGetValue("Gunship", out stock);
            Assert.Equal(escorts, stock);
        }

        [Fact]
        public void ALeadLostInTransitFailsTheMissionAndSendsTheSurvivorsHome()
        {
            WorldModel w = TestSupport.World(1);
            AddNode(w, "S1", "SPRT", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "N600", "EXP", new Vec3(100, 0, 0));
            var sprt = new FactionState { Tag = "SPRT", RngSeed = 1 };
            var states = new List<FactionState> { sprt, new FactionState { Tag = "EXP", RngSeed = 1 } };
            PendingFleetAction raid = PendingFleetResolver.CreatePending(
                ActionKind.Raid, "SPRT", new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "N600", Cost = 0 },
                w, w.Tuning, immediate: false);
            raid.Squadron = new List<string> { "Gunship", "Gunship", "Gunship" };
            raid.ShipPrefabId = "Gunship";
            raid.ConvoySize = 3;
            raid.FleetId = "T00001";
            sprt.PendingFleets.Add(raid);
            Assert.Equal(FleetStopKind.Service, raid.CurrentStopKind);
            w.UtcNow = raid.DepartedUtc.AddSeconds(5);
            double holdBefore = target.HoldStrength;
            LoseLead(sprt, w, "grid_destroyed", 2);

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Null(Line(log, "RAID_RESOLVED"));
            Assert.Null(Line(log, "STATION_DEFENSE"));
            Assert.NotNull(Line(log, "RAID_BROKE_OFF [T00001] SPRT"));
            Assert.Contains("stop=Service", Line(log, "RAID_BROKE_OFF [T00001] SPRT"));
            Assert.Equal(holdBefore, target.HoldStrength, 9);
            Assert.Empty(sprt.PendingFleets);
            int stock;
            w.Graph.Nodes["S1"].ShipStock.TryGetValue("Gunship", out stock);
            Assert.Equal(2, stock);
        }

        [Fact]
        public void ALoneHullShotDownIsWrittenOffWithNothingToFlyOut()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState sprt = DwellingSquadron(w, 0, out states, out target);
            PendingFleetAction f = sprt.PendingFleets[0];
            f.Squadron = null;
            f.ConvoySize = 1;
            f.MaterialisedEntityId = -1;
            f.EstimatedArrivalUtc = w.UtcNow;
            f.ForcedRatio = 0.0;
            f.ForcedRatioCause = "grid_destroyed";
            sprt.PendingFleets[0] = f;

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Null(Line(log, "RAID_RESOLVED"));
            Assert.Contains("hulls_lost=1/1", Line(log, "RAID_BROKE_OFF [8B6A77] SPRT"));
            Assert.Contains("attack=Destroyed", Line(log, "COMBAT_OUTCOME [8B6A77] SPRT"));
            Assert.NotNull(Line(log, "SHIP_LOST [8B6A77] SPRT"));
            Assert.Empty(sprt.PendingFleets);
            // Nothing came home: the one shape the event may be read as destroyed.
            FactionEvent ev = Assert.Single(w.PendingEvents, e => e.Kind == FactionEventKind.RaidRepelled);
            Assert.Equal(1, ev.Amount);
            Assert.Equal(1.0, ev.Ratio);
        }

        [Fact]
        public void AnAbstractRaidWithNoGridIsUnaffected()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState sprt = DwellingSquadron(w, 8, out states, out target);
            double holdBefore = target.HoldStrength;

            var log = new List<string>();
            w.UtcNow = sprt.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.NotNull(Line(log, "RAID_RESOLVED [8B6A77] SPRT -> N600"));
            Assert.Null(Line(log, "RAID_BROKE_OFF"));
            Assert.True(target.HoldStrength < holdBefore);
        }

        [Fact]
        public void OnlyAnUnresolvedAttackHangsOnItsLead()
        {
            var raid = new PendingFleetAction { Kind = ActionKind.Raid, CurrentStopKind = FleetStopKind.Engage };
            Assert.True(PendingFleetResolver.MissionHangsOnLead(raid));
            raid.CurrentStopKind = FleetStopKind.Return;       // the mission already resolved
            Assert.False(PendingFleetResolver.MissionHangsOnLead(raid));
            raid.CurrentStopKind = FleetStopKind.Engage;
            raid.RetreatReason = "repelled";                   // already leaving
            Assert.False(PendingFleetResolver.MissionHangsOnLead(raid));
            Assert.False(PendingFleetResolver.MissionHangsOnLead(
                new PendingFleetAction { Kind = ActionKind.Haul, CurrentStopKind = FleetStopKind.Service }));
            Assert.False(PendingFleetResolver.MissionHangsOnLead(
                new PendingFleetAction { Kind = ActionKind.Guard, CurrentStopKind = FleetStopKind.Service }));

            Assert.True(PendingFleetResolver.IsCombatLoss("grid_destroyed"));
            Assert.True(PendingFleetResolver.IsCombatLoss("grid_captured"));
            Assert.True(PendingFleetResolver.IsCombatLoss(DisabledHull.Cause));
            Assert.False(PendingFleetResolver.IsCombatLoss("grid_stalled"));
            Assert.False(PendingFleetResolver.IsCombatLoss("spawn_dedup"));
        }

        [Fact]
        public void ADisabledRaidLeadKeepsItsEscortsForTheDepartureAtRatioZero()
        {
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "SPRT", CurrentStopKind = FleetStopKind.Engage,
                DepartedUtc = new DateTime(2026, 9, 29, 23, 0, 0, DateTimeKind.Utc),
                EstimatedArrivalUtc = new DateTime(2026, 9, 29, 23, 5, 0, DateTimeKind.Utc),
                Squadron = new List<string> { "Gunship", "Gunship" }, ShipPrefabId = "Gunship", ConvoySize = 2,
                MaterialisedEntityId = 11, EscortEntityIds = new List<long> { 12 },
            };
            DisabledHull.WriteOffLead(ref raid, 0, "Gunship", null, new DateTime(2026, 9, 29, 23, 4, 48, DateTimeKind.Utc));
            Assert.Equal(new List<long> { 12 }, raid.EscortEntityIds);
            Assert.Equal(0.0, raid.ForcedRatio);
            Assert.True(PendingFleetResolver.LeadLostInCombat(raid));
        }
    }
}
