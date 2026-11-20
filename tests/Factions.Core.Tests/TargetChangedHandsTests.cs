using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A node changing hands under fleets standing at it: an attack whose target is no longer the
    /// victim's leaves without fighting, and a defence whose side no longer holds the node goes home.
    /// The live shape: EXP's siege took SPRT's hold to zero mid-dwell, EXP claimed the derelict, and the
    /// dwell then resolved against a SPRT Guard that arrived after the abandonment — RAID_REPELLED on a
    /// node EXP already owned.
    /// </summary>
    public class TargetChangedHandsTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static IRandom NoJitter() => new FixedRandom(0.5);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos, double hold = 0.8)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        /// <summary>RVN's raid on MERC's MERC1, already on its first engagement dwell. MERC keeps a
        /// second node, MERC2, so a withdrawn Guard has somewhere of its own to land.</summary>
        private static FactionState RaidOnItsDwell(WorldModel w, out List<FactionState> states,
                                                   out SpatialNode target, ActionKind kind = ActionKind.Raid)
        {
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            target = AddNode(w, "MERC1", "MERC", new Vec3(100, 0, 0));
            AddNode(w, "MERC2", "MERC", new Vec3(150, 0, 0));
            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            states = new List<FactionState> { raider, new FactionState { Tag = "MERC", RngSeed = 1 },
                                              new FactionState { Tag = "IMPR", RngSeed = 1 } };
            raider.PendingFleets.Add(PendingFleetResolver.CreatePending(
                kind, "RVN", new FactionIntent { Kind = kind, TargetNodeId = "MERC1", Cost = 0 },
                w, w.Tuning, immediate: false));
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), new List<string>(), null);
            Assert.Equal(FleetStopKind.Engage, raider.PendingFleets[0].CurrentStopKind);
            return raider;
        }

        /// <summary>A Guard standing its window at MERC1 for the faction it defends, armed well past anything the raid carries.</summary>
        private static PendingFleetAction StandingGuard(WorldModel w, string tag, string defends = "MERC")
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = tag, TargetTag = defends, TargetNodeId = "MERC1", FleetId = "G00001",
                DepartedUtc = w.UtcNow.AddMinutes(-1), EstimatedArrivalUtc = w.UtcNow.AddMinutes(-1),
                Resolved = true,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 100000, RangeM = 2000 } },
            };
        }

        private static bool Has(List<string> log, string prefix)
        {
            return log.Any(l => l.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool HasEvent(WorldModel w, FactionEventKind kind)
        {
            return w.PendingEvents.Any(e => e.Kind == kind);
        }

        [Fact]
        public void Dwell_OnANodeTheRaiderNowOwns_EndsAtOnceWithoutAFightAndGoesHome()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState raider = RaidOnItsDwell(w, out states, out target);
            states[1].PendingFleets.Add(StandingGuard(w, "MERC"));
            target.OwnerFactionTag = "RVN";                 // abandoned and claimed mid-dwell
            target.HoldStrength = 0.5;
            DateTime dwellEnd = raider.PendingFleets[0].EstimatedArrivalUtc;
            w.PendingEvents.Clear();

            var log = new List<string>();
            w.UtcNow = dwellEnd.AddSeconds(-200);           // the window still has minutes to run
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            PendingFleetAction raid = raider.PendingFleets[0];
            Assert.Equal(FleetStopKind.Return, raid.CurrentStopKind);
            Assert.True(raid.RemainingStops == null || raid.RemainingStops.All(s => s.Kind != FleetStopKind.Engage));
            Assert.True(Has(log, "TARGET_CHANGED_HANDS"), string.Join("\n", log));
            Assert.Contains(log, l => l.StartsWith("TARGET_CHANGED_HANDS", StringComparison.Ordinal)
                                      && l.Contains("from=MERC to=RVN"));
            Assert.False(Has(log, "RAID_REPELLED"));
            Assert.False(Has(log, "RAID_RESOLVED"));
            Assert.False(HasEvent(w, FactionEventKind.RaidRepelled));
            Assert.True(HasEvent(w, FactionEventKind.RaidResolved));
            Assert.Equal(0.5, target.HoldStrength);          // its own node: nothing stripped
        }

        [Fact]
        public void Raid_OnANodeAThirdFactionHolds_IsStillFought()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState raider = RaidOnItsDwell(w, out states, out target);
            target.OwnerFactionTag = "IMPR";

            Assert.Null(PendingFleetResolver.TargetChangedHands(raider.PendingFleets[0], w));
        }

        [Fact]
        public void Dwell_OnANodeStillTheVictims_IsFoughtByItsGuard()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState raider = RaidOnItsDwell(w, out states, out target);
            states[1].PendingFleets.Add(StandingGuard(w, "MERC"));

            var log = new List<string>();
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.False(Has(log, "TARGET_CHANGED_HANDS"));
            Assert.False(Has(log, "DEFENCE_WITHDRAWN"));
            Assert.True(log.Any(l => l.Contains("vs [G00001] MERC")), string.Join("\n", log));
        }

        [Fact]
        public void Arrival_AtANodeTheRaiderNowOwns_NeverStartsADwell()
        {
            WorldModel w = TestSupport.World(1);
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(100, 0, 0));
            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            var states = new List<FactionState> { raider, new FactionState { Tag = "MERC", RngSeed = 1 } };
            raider.PendingFleets.Add(PendingFleetResolver.CreatePending(
                ActionKind.Raid, "RVN", new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "MERC1", Cost = 0 },
                w, w.Tuning, immediate: false));
            target.OwnerFactionTag = "RVN";                 // changed hands while the raid was in flight

            var log = new List<string>();
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.False(Has(log, "RAID_ENGAGED"));
            Assert.True(Has(log, "TARGET_CHANGED_HANDS"));
            Assert.Equal(FleetStopKind.Return, raider.PendingFleets[0].CurrentStopKind);
        }

        [Fact]
        public void Conquer_OfADerelict_IsStillItsToTake()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState raider = RaidOnItsDwell(w, out states, out target, ActionKind.Conquer);
            target.OwnerFactionTag = NeutralFaction.Tag;
            target.HoldStrength = 0;

            Assert.Null(PendingFleetResolver.TargetChangedHands(raider.PendingFleets[0], w));
            target.OwnerFactionTag = "RVN";
            Assert.Equal("RVN", PendingFleetResolver.TargetChangedHands(raider.PendingFleets[0], w));
        }

        [Fact]
        public void Raid_OnADerelict_IsMoot()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            FactionState raider = RaidOnItsDwell(w, out states, out target);
            target.OwnerFactionTag = NeutralFaction.Tag;

            Assert.Equal(NeutralFaction.Tag, PendingFleetResolver.TargetChangedHands(raider.PendingFleets[0], w));
        }

        [Fact]
        public void Guard_AtANodeItsFactionLost_GoesHomeAndLandsOnItsOwnGround()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            RaidOnItsDwell(w, out states, out target);
            PendingFleetAction guard = StandingGuard(w, "MERC");
            guard.ShipPrefabId = "Wakizashi";
            guard.ConvoySize = 1;
            states[1].PendingFleets.Add(guard);
            target.OwnerFactionTag = "RVN";

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Contains(log, l => l.StartsWith("DEFENCE_WITHDRAWN [G00001] MERC kind=Guard @ MERC1 owner=RVN",
                                                   StringComparison.Ordinal));
            Assert.DoesNotContain(states[1].PendingFleets, f => f.FleetId == "G00001");
            int landedHome, landedOnLostNode;
            w.Graph.Nodes["MERC2"].ShipStock.TryGetValue("Wakizashi", out landedHome);
            target.ShipStock.TryGetValue("Wakizashi", out landedOnLostNode);
            Assert.Equal(1, landedHome);
            Assert.Equal(0, landedOnLostNode);
        }

        [Fact]
        public void Guard_DefendingAnAllysNode_KeepsStandingWhileTheAllyHoldsIt()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            RaidOnItsDwell(w, out states, out target);
            target.OwnerFactionTag = "IMPR";
            states[2].PactWith.Add("MERC");
            states[1].PendingFleets.Add(StandingGuard(w, "MERC", defends: "IMPR"));

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.False(Has(log, "DEFENCE_WITHDRAWN"));
            Assert.True(states[1].PendingFleets.Single(f => f.FleetId == "G00001").StandsAsDefender(w.UtcNow, w.Tuning));
        }

        [Fact]
        public void Guard_ArrivingAtANodeItsFactionLost_StandsNoWindow()
        {
            WorldModel w = TestSupport.World(1);
            List<FactionState> states; SpatialNode target;
            RaidOnItsDwell(w, out states, out target);
            PendingFleetAction guard = StandingGuard(w, "MERC");
            guard.Resolved = false;                         // arriving this pass
            guard.EstimatedArrivalUtc = w.UtcNow;
            states[1].PendingFleets.Add(guard);
            target.OwnerFactionTag = "RVN";

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.True(Has(log, "DEFENCE_WITHDRAWN"));
            Assert.False(Has(log, "GUARD_ARRIVED"));
            Assert.DoesNotContain(states[1].PendingFleets, f => f.FleetId == "G00001");
        }
    }
}
