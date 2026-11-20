using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="PendingFleetResolver.ResolveShipDuel"/> — the turn-based, range-gated combat
    /// resolution that replaced the old flat <c>GuardCombatChance</c> coin flip — via the public
    /// <see cref="PendingFleetResolver.ResolveArrivals"/> entry point (the duel itself is private).
    /// Focus: multi-band range gating (a ship's short-range weapons must stay inert during a
    /// long-range engagement turn), the no-Guard station-defense fallback, the flat station-defense
    /// floor, zero-band safety, and reproducibility.
    /// </summary>
    public class ShipDuelTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static List<WeaponBand> Bands(float dps, float rangeM = 2000)
            => new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        /// <summary>Zero jitter: Jitter() = 1 + (0.5*2-1)*variance = 1 exactly, so duel damage is
        /// hand-computable without worrying about the +/-CombatVariance roll direction.</summary>
        private static IRandom NoJitter() => new FixedRandom(0.5);

        [Fact]
        public void EqualPower_MutualBreakOff_NeitherGetsAFreeWin()
        {
            WorldModel w = MakeWorld();
            var stDefender = new FactionState { Tag = "MERC" };
            var stRaider   = new FactionState { Tag = "RVN" };
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = Bands(60),
            });
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = Bands(60),
            };
            stRaider.PendingFleets.Add(raid);

            var states = new List<FactionState> { stDefender, stRaider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            // Equal total Dps, equal range -> both sides grind down at the same rate and fall below
            // their break-off fractions on the same turn. Neither side gets a "free" win; hold is
            // undamaged because the raid left without its mission.
            string all = string.Join("\n", log);
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);
            Assert.Contains("broke_off=both", all);
            Assert.Contains("attack=BrokeOff defence=BrokeOff", all);
            Assert.DoesNotContain("RAID_RESOLVED", all);
        }

        [Fact]
        public void MultiBand_ShortRangeWeaponStaysInert_UntilEngagementCloses()
        {
            // The specific gap this mechanic exists to close: a prefab with both a weak long-range
            // weapon and a strong short-range weapon must not fire its short-range weapon's Dps
            // during a long-range engagement turn.
            WorldModel w = MakeWorld();
            var stDefender = new FactionState { Tag = "MERC" };
            var stRaider   = new FactionState { Tag = "RVN" };
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            // Defender is trivially weak so the attacker survives all 3 turns intact enough to log
            // every turn's contribution (nothing here is about the defender's own strength).
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = Bands(1, rangeM: 2000),
            });
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
                WeaponBands = new List<WeaponBand>
                {
                    new WeaponBand { Dps = 100, RangeM = 2000 }, // long-range, present every bracket
                    new WeaponBand { Dps = 900, RangeM = 100 },  // short-range, only bracket 0 (point-blank)
                },
            };
            stRaider.PendingFleets.Add(raid);

            var states = new List<FactionState> { stDefender, stRaider };
            var log = new List<string>();
            // Default tuning: CombatTurns=3, CombatRangeBracketsM=[2000,800,0], CombatBaseDamageFraction=0.35.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            string logStr = string.Join("\n", log);
            // The raid closes to point blank on its turns and the defender opens to 2000 m on its
            // one. At 2000 m only the 100-Dps long-range band is in range -> 100*0.35=35.
            Assert.Contains("T2(2000m set=def) atk=35", logStr);
            // At 0 m, point-blank: both bands contribute -> (100+900)*0.35=350.
            Assert.Contains("T1(0m set=atk) atk=350", logStr);
            Assert.Contains("T3(0m set=atk) atk=350", logStr);
        }

        [Fact]
        public void DefenderOutranges_AttackerDestroyedBeforeEverActing()
        {
            WorldModel w = MakeWorld();
            var stDefender = new FactionState { Tag = "MERC" };
            var stRaider   = new FactionState { Tag = "RVN" };
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = Bands(500, rangeM: 2000),
            });
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
                WeaponBands = Bands(50, rangeM: 100), // never reaches the 2000m/800m brackets
            };
            stRaider.PendingFleets.Add(raid);

            var states = new List<FactionState> { stDefender, stRaider };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);
            Assert.Contains("GUARD_INTERCEPT", string.Join("\n", log));
        }

        [Fact]
        public void StationBackedNode_NoGuardPresent_StillDamagesRaidViaFlatDefense()
        {
            // Same raid dispatched at an identical, unowned-by-Guard node with vs. without a
            // station present. The station has no weapon blocks (StationWeapons stays null) — only
            // FactionTuning.StationDefensePower's flat, non-range-gated floor is in play — so a
            // civilian/unarmed station is still not free damage for a raider.
            var raidBands = Bands(200, rangeM: 2000);

            WorldModel wBare = MakeWorld();
            var stRaiderBare = new FactionState { Tag = "RVN" };
            AddNode(wBare, "N1", "MERC", new Vec3(0, 0, 0), hold: 0.8); // no StationPrefabId
            DateTime nowBare = wBare.UtcNow;
            stRaiderBare.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "N1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = nowBare, EstimatedArrivalUtc = nowBare, WeaponBands = raidBands,
            });
            PendingFleetResolver.ResolveArrivals(new[] { stRaiderBare }, wBare, wBare.Tuning, NoJitter(), new List<string>(), null);
            double bareHold = wBare.Graph.Nodes["N1"].HoldStrength;

            WorldModel wStation = MakeWorld();
            var stRaiderStation = new FactionState { Tag = "RVN" };
            SpatialNode stationNode = AddNode(wStation, "N1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            stationNode.StationPrefabId = "Economy_Outpost_11"; // station-backed, but StationWeapons unset
            DateTime nowStation = wStation.UtcNow;
            stRaiderStation.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "N1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = nowStation, EstimatedArrivalUtc = nowStation, WeaponBands = raidBands,
            });
            var stationLog = new List<string>();
            PendingFleetResolver.ResolveArrivals(new[] { stRaiderStation }, wStation, wStation.Tuning, NoJitter(), stationLog, null);
            double stationHold = wStation.Graph.Nodes["N1"].HoldStrength;

            Assert.Contains("STATION_DEFENSE", string.Join("\n", stationLog));
            Assert.True(stationHold > bareHold,
                "a raid that took flat station counter-fire should end with a lower surviving ratio " +
                "(and therefore do less hold damage) than the exact same raid against a bare node");
        }

        [Fact]
        public void NoBandsEitherSide_DoesNotThrow_RaidStillAppliesBaselineDamage()
        {
            WorldModel w = MakeWorld();
            var stRaider = new FactionState { Tag = "RVN" };
            SpatialNode target = AddNode(w, "N1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            // No station, no Guard, no WeaponBands anywhere -> the token 1v1 floor duel.

            DateTime now = w.UtcNow;
            stRaider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "N1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
            });

            var log = new List<string>();
            var ex = Record.Exception(() =>
                PendingFleetResolver.ResolveArrivals(new[] { stRaider }, w, w.Tuning, NoJitter(), log, null));

            Assert.Null(ex);
            Assert.True(target.HoldStrength < 0.8, "an unopposed raid must still apply baseline hold damage");
        }

        [Fact]
        public void SameSeed_SameDuelOutcome_IsReproducible()
        {
            Func<double> runOnce = () =>
            {
                WorldModel w = MakeWorld();
                var stDefender = new FactionState { Tag = "MERC" };
                var stRaider   = new FactionState { Tag = "RVN" };
                AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

                DateTime now = w.UtcNow;
                stDefender.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                    DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                    Resolved = true, WeaponBands = Bands(70),
                });
                stRaider.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                    OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                    DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = Bands(90),
                });

                var states = new List<FactionState> { stDefender, stRaider };
                var log = new List<string>();
                PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new SeededRandom(7), log, null);
                return w.Graph.Nodes["MERC1"].HoldStrength;
            };

            double first = runOnce();
            double second = runOnce();
            Assert.Equal(first, second, 10);
        }
    }
}
