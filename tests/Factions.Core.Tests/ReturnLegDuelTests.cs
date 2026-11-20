using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-245: only a mission arrival is fought over. A raider coming home is not attacking the node
    /// it is landing at, and its own garrison has no business shooting at it — but the meeting loop
    /// selected on <see cref="ActionKind"/> alone and <c>ResolveStationDefense</c> reads the arriving
    /// node's weapons with no owner check, so every combat fleet was duelled twice: once at its
    /// target, once at its own origin on the way back.
    /// </summary>
    public class ReturnLegDuelTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static IRandom NoJitter() => new FixedRandom(0.5);

        /// <summary>A raid that has done its work and is flying home, with a heavily-armed station at
        /// the home node — the exact shape that destroyed SPRT's own raider live.</summary>
        private static FactionState ReturningRaid(
            WorldModel w, out List<FactionState> states, out SpatialNode home)
        {
            home = AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            home.StationPrefabId = "HomeBase";
            home.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 5000, RangeM = 3000 } };
            AddNode(w, "MERC1", "MERC", new Vec3(100, 0, 0));

            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            states = new List<FactionState> { raider, new FactionState { Tag = "MERC", RngSeed = 1 } };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "RVN",
                OriginNodeId = "MERC1", TargetNodeId = "RVN1",
                OriginPosKm = new Vec3(100, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow.AddMinutes(-1), EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Return,
                ShipPrefabId = "Raider", ConvoySize = 1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 60, RangeM = 2000 } },
                FleetId = "AAAAAA",
            });
            return raider;
        }

        [Fact]
        public void AReturningRaider_IsNotDuelledByItsOwnStation()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode home;
            FactionState raider = ReturningRaid(w, out states, out home);
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            string all = string.Join("\n", log);
            Assert.DoesNotContain("STATION_DEFENSE", all);
            Assert.DoesNotContain("STATION_INTERCEPT", all);
            Assert.Contains("FLEET_RETURNED", all);
        }

        [Fact]
        public void AReturningRaidersShip_ComesHome_InsteadOfBeingShotDownByItsOwnBase()
        {
            // The live loss, as a test: a 5000 Dps home station against a 60 Dps raider destroys it
            // outright, and the fleet was marked Intercepted and its hull never credited back.
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode home;
            FactionState raider = ReturningRaid(w, out states, out home);

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), new List<string>(), null);

            int stock;
            home.ShipStock.TryGetValue("Raider", out stock);
            Assert.Equal(1, stock);
        }

        [Fact]
        public void TheRatioTheRealFightProduced_SurvivesTheTripHome()
        {
            // The second cost: the return-leg duel overwrote ForcedRatio, so FinalizeReturn scaled
            // the hull and cargo recovery by a fight that never should have happened. A raid weakened
            // at its target must still be weakened when it lands.
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode home;
            FactionState raider = ReturningRaid(w, out states, out home);
            PendingFleetAction f = raider.PendingFleets[0];
            f.ForcedRatio = 0.4;
            f.ForcedRatioCause = "station_duel";
            raider.PendingFleets[0] = f;
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Contains("ratio=0.40", string.Join("\n", log));
        }

        [Fact]
        public void AMissionArrival_IsStillDuelled()
        {
            // The guard must not widen: a raid arriving at what it came to attack still fights.
            WorldModel w = MakeWorld();
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.StationPrefabId = "Fortress";
            target.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 400, RangeM = 3000 } };
            AddNode(w, "RVN1", "RVN", new Vec3(100, 0, 0));

            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            var states = new List<FactionState> { raider, new FactionState { Tag = "MERC", RngSeed = 1 } };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC",
                OriginNodeId = "RVN1", TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(100, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow.AddMinutes(-1), EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Service,   // no dwell queued: the legacy mission leg
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 60, RangeM = 2000 } },
                FleetId = "BBBBBB",
            });
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Contains("STATION_", string.Join("\n", log));
        }
    }
}
