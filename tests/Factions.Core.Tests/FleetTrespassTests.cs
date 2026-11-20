using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 5 — an enemy NPC fleet a faction detects in its space is an intruder: marked,
    /// laddered by how long it stays, and caught at Intercept by the faction's Guard at the node.
    /// </summary>
    public sealed class FleetTrespassTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static readonly Vec3 InDefSpace = new Vec3(20, 0, 0);
        private static readonly Vec3 OpenSpace = new Vec3(1000, 0, 0);

        /// <summary>DEF holds HOME and N002 (StationCallTests.DefenderWorld); ENM is at
        /// <paramref name="relation"/> with it both ways.</summary>
        private static WorldModel World(out FactionState def, out FactionState enm, out List<FactionState> states,
                                        double relation = -0.6)
        {
            WorldModel w = StationCallTests.DefenderWorld();
            w.FleetTrespassOn = true;
            def = new FactionState { Tag = "DEF", RngSeed = 1 };
            enm = new FactionState { Tag = "ENM", RngSeed = 1 };
            def.Relations["ENM"] = relation;
            enm.Relations["DEF"] = relation;
            states = new List<FactionState> { def, enm };
            SpatialNode home = w.Graph.Add(new Vec3(500, 0, 0), 0.6, "ENM1");
            home.OwnerFactionTag = "ENM"; home.HoldStrength = 0.9; home.Radius = 40;
            return w;
        }

        private static PendingFleetAction Fleet(WorldModel w, ActionKind kind, string id, Vec3 at, string prefab = "Hauler")
        {
            return new PendingFleetAction
            {
                Kind = kind, ActorTag = "ENM", TargetTag = "OTH", FleetId = id,
                OriginPosKm = at, TargetPosKm = at,
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddHours(2),
                ShipPrefabId = prefab, ConvoySize = 1,
            };
        }

        private static PendingFleetAction Convoy(WorldModel w, Vec3 at)
        {
            PendingFleetAction f = Fleet(w, ActionKind.Trade, "T1", at);
            f.Cargo = new Dictionary<string, long> { { "Ingot:Iron", 1000 } };
            f.CargoValue = 1000;
            f.OriginNodeId = "ENM1";
            f.ReturnNodeId = "ENM1";
            f.RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "ENM1", Kind = FleetStopKind.Return } };
            return f;
        }

        /// <summary>A DEF Guard standing its duty at N002, sent at an intruder.</summary>
        private static PendingFleetAction Guard(WorldModel w)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "DEF", TargetTag = "DEF", TargetNodeId = "N002",
                OriginNodeId = "HOME", FleetId = "G1", AgainstIntruder = true,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow.AddMinutes(-2), EstimatedArrivalUtc = w.UtcNow.AddMinutes(-1), Resolved = true,
                ShipPrefabId = "Gunship", ConvoySize = 1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 500f, RangeM = 800 } },
            };
        }

        /// <summary>Runs the arrivals pass every 10 s for <paramref name="seconds"/>, or until a line
        /// starting <paramref name="until"/> is logged.</summary>
        private static List<string> Run(WorldModel w, List<FactionState> states, double seconds, string until = null)
        {
            var log = new List<string>();
            for (double t = 0; t <= seconds; t += 10)
            {
                PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);
                if (until != null && log.Exists(l => l.StartsWith(until))) break;
                w.UtcNow = w.UtcNow.AddSeconds(10);
            }
            return log;
        }

        [Fact]
        public void AnEnemyFleetInOurSpace_IsMarkedAtTheNearestNode_AndANeutralOneIsNot()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            enm.PendingFleets.Add(Convoy(w, InDefSpace));
            List<string> log = Run(w, states, 0);

            string line = Assert.Single(log, l => l.StartsWith("TRESPASS_INTRUDER "));
            Assert.StartsWith("TRESPASS_INTRUDER DEF node=N002 fleet=[T1] actor=ENM kind=Trade", line);
            Assert.True(w.IsIntruded("N002"));
            Assert.Equal(TrespassRung.Shadow, w.IntruderRungAt("N002"));

            WorldModel neutral = World(out def, out enm, out states, relation: 0.0);
            enm.PendingFleets.Add(Convoy(neutral, InDefSpace));
            Assert.DoesNotContain(Run(neutral, states, 0), l => l.StartsWith("TRESPASS_"));
            Assert.False(neutral.IsIntruded("N002"));
        }

        [Fact]
        public void TheSwitchOff_MarksNothing()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            w.FleetTrespassOn = false;
            enm.PendingFleets.Add(Convoy(w, InDefSpace));
            def.PendingFleets.Add(Guard(w));

            Assert.DoesNotContain(Run(w, states, 300), l => l.StartsWith("TRESPASS_"));
            Assert.False(w.IsIntruded("N002"));
        }

        [Fact]
        public void AnAttackOnTheOwner_IsAnInboundThreat_NotATrespass()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            PendingFleetAction raid = Fleet(w, ActionKind.Raid, "R1", InDefSpace, "Gunship");
            raid.TargetTag = "DEF";
            raid.TargetNodeId = "N002";
            enm.PendingFleets.Add(raid);

            Assert.DoesNotContain(Run(w, states, 0), l => l.StartsWith("TRESPASS_"));
        }

        [Fact]
        public void AFleetOutsideTheSpace_IsNotSeen()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            enm.PendingFleets.Add(Convoy(w, OpenSpace));
            def.PendingFleets.Add(Guard(w));

            Assert.DoesNotContain(Run(w, states, 300), l => l.StartsWith("TRESPASS_"));
        }

        [Fact]
        public void ACrossingShorterThanTheShadowTime_PassesFree()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            PendingFleetAction convoy = Convoy(w, InDefSpace);
            enm.PendingFleets.Add(convoy);
            def.PendingFleets.Add(Guard(w));

            List<string> log = Run(w, states, 20);
            Assert.Contains(log, l => l.StartsWith("TRESPASS_INTRUDER "));
            // Out of the space before the ladder reaches Intercept.
            PendingFleetAction gone = enm.PendingFleets[0];
            gone.OriginPosKm = OpenSpace; gone.TargetPosKm = OpenSpace;
            enm.PendingFleets[0] = gone;
            log.AddRange(Run(w, states, 600));

            Assert.DoesNotContain(log, l => l.StartsWith("TRESPASS_RUNG ") || l.StartsWith("TRESPASS_CATCH "));
            Assert.Equal(1000, enm.PendingFleets[0].Cargo["Ingot:Iron"]);
        }

        [Fact]
        public void AConvoyThatStays_IsCaughtByTheGuard_HeldForTheWindow_AndRobbed()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            enm.PendingFleets.Add(Convoy(w, InDefSpace));
            def.PendingFleets.Add(Guard(w));

            List<string> log = Run(w, states, 900, until: "RAID_LOOTS ");

            Assert.Single(log, l => l.StartsWith("TRESPASS_RUNG DEF node=N002 fleet=[T1] actor=ENM rung=intercept"));
            string caught = Assert.Single(log, l => l.StartsWith("TRESPASS_CATCH "));
            Assert.Equal("TRESPASS_CATCH [G1] DEF caught [T1] ENM kind=Trade @N002 verdict=convoy", caught);
            string window = Assert.Single(log, l => l.StartsWith("INTERCEPT_WINDOW "));
            Assert.Contains("[G1] DEF holds [T1] ENM @N002", window);
            Assert.Contains("raid=guard", window);
            // The rung was reached before the catch, and the catch only after it.
            Assert.True(log.FindIndex(l => l.StartsWith("TRESPASS_RUNG ")) < log.IndexOf(caught));

            // Unarmed against the Guard: robbed of half, hull kept on a 0.5 roll.
            string loots = Assert.Single(log, l => l.StartsWith("RAID_LOOTS "));
            Assert.Contains("[G1] DEF intercepted [T1] ENM @N002", loots);
            Assert.Contains("hull=survived", loots);
            Assert.Equal(500, enm.PendingFleets[0].Cargo["Ingot:Iron"]);
        }

        [Fact]
        public void ARobbedConvoyThatStays_IsNotCaughtAgainByTheSameGuard()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            enm.PendingFleets.Add(Convoy(w, InDefSpace));
            def.PendingFleets.Add(Guard(w));
            List<string> log = Run(w, states, 900, until: "RAID_LOOTS ");
            Assert.Contains(log, l => l.StartsWith("RAID_LOOTS "));

            // It stays where it was caught.
            PendingFleetAction robbed = enm.PendingFleets[0];
            robbed.OriginPosKm = InDefSpace; robbed.TargetPosKm = InDefSpace; robbed.TargetNodeId = null;
            robbed.CurrentStopKind = FleetStopKind.Service; robbed.PathNodeIds = null; robbed.RouteDistanceKm = 0;
            robbed.DepartedUtc = w.UtcNow; robbed.EstimatedArrivalUtc = w.UtcNow.AddHours(2);
            enm.PendingFleets[0] = robbed;
            log.AddRange(Run(w, states, 300));

            Assert.Single(log, l => l.StartsWith("TRESPASS_CATCH "));
            Assert.Single(log, l => l.StartsWith("RAID_LOOTS "));
            Assert.True(w.IsIntruded("N002"));
        }

        [Fact]
        public void WithoutAGuardAtTheNode_NothingIsCaught()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            enm.PendingFleets.Add(Convoy(w, InDefSpace));

            List<string> log = Run(w, states, 600);

            Assert.Contains(log, l => l.StartsWith("TRESPASS_RUNG "));
            Assert.DoesNotContain(log, l => l.StartsWith("TRESPASS_CATCH "));
            Assert.True(w.IsIntruded("N002"));
            Assert.Equal(TrespassRung.Intercept, w.IntruderRungAt("N002"));
        }

        [Fact]
        public void AnArmedFleetThatStays_DuelsTheGuard()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            PendingFleetAction patrol = Fleet(w, ActionKind.Patrol, "P1", InDefSpace, "Gunship");
            patrol.WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 500f, RangeM = 800 } };
            enm.PendingFleets.Add(patrol);
            def.PendingFleets.Add(Guard(w));

            List<string> log = Run(w, states, 600);

            string caught = Assert.Single(log, l => l.StartsWith("TRESPASS_CATCH "));
            Assert.Equal("TRESPASS_CATCH [G1] DEF caught [P1] ENM kind=Patrol @N002 verdict=duel", caught);
            Assert.Contains(log, l => (l.StartsWith("GUARD_INTERCEPT ") || l.StartsWith("RAID_REPELLED ")
                                       || l.StartsWith("RAID_BROKE_OFF ") || l.StartsWith("RAID_BREAKS_GUARD "))
                                      && l.Contains("@N002"));
        }

        [Fact]
        public void AMaterialisedFleet_IsLeftToTheGrids_AndTheMarkNamesItsGrid()
        {
            FactionState def, enm; List<FactionState> states;
            WorldModel w = World(out def, out enm, out states);
            PendingFleetAction convoy = Convoy(w, InDefSpace);
            convoy.MaterialisedEntityId = 555;
            enm.PendingFleets.Add(convoy);
            def.PendingFleets.Add(Guard(w));

            List<string> log = Run(w, states, 600);

            Assert.Contains(log, l => l.StartsWith("TRESPASS_RUNG "));
            Assert.DoesNotContain(log, l => l.StartsWith("TRESPASS_CATCH "));
            Assert.Equal(555, w.IntruderGridAt("N002"));
        }

        [Fact]
        public void CatchOf_CarriersGetTheConvoyVerdict_ArmedFleetsInTransitADuel_AReturningRaidNothing()
        {
            Assert.Equal(FleetTrespass.CatchKind.Convoy, FleetTrespass.CatchOf(new PendingFleetAction { Kind = ActionKind.Trade }));
            Assert.Equal(FleetTrespass.CatchKind.Convoy, FleetTrespass.CatchOf(new PendingFleetAction { Kind = ActionKind.Mine }));
            Assert.Equal(FleetTrespass.CatchKind.Duel, FleetTrespass.CatchOf(new PendingFleetAction { Kind = ActionKind.Raid }));
            Assert.Equal(FleetTrespass.CatchKind.None, FleetTrespass.CatchOf(new PendingFleetAction
                { Kind = ActionKind.Raid, CurrentStopKind = FleetStopKind.Return }));
            Assert.Equal(FleetTrespass.CatchKind.None, FleetTrespass.CatchOf(new PendingFleetAction { Kind = ActionKind.Found }));
        }

        [Fact]
        public void RepFor_MapsTheWarLineToTheEnemiesEdge_AndMinusOneToMinus1500()
        {
            Assert.Equal(-500, FleetTrespass.RepFor(-0.3, -0.3));
            Assert.Equal(-1500, FleetTrespass.RepFor(-1.0, -0.3));
            Assert.Equal(-1000, FleetTrespass.RepFor(-0.65, -0.3));
            Assert.Equal(-500, FleetTrespass.RepFor(0.2, -0.3));
        }
    }
}
