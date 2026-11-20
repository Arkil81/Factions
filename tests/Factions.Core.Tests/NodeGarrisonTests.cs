using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-309 slice 2: a node that keeps being attacked asks for a garrison of its own, sized against
    /// what actually hit <b>it</b>, and posted <b>there</b> rather than at whichever yard happens to be
    /// first in the estate.
    ///
    /// <para>The network-wide budget answers "how strong a hull should this faction own". That is the
    /// right question for building and the wrong one for standing — a defender is only worth having at
    /// the place it is needed, and `FleetManager`'s Defend block draws from the threatened node first
    /// precisely because a hull parked there fights for zero transit.</para>
    /// </summary>
    public class NodeGarrisonTests
    {
        private static readonly DateTime T0 = YardT0;

        // The same fixture DefenceBudgetTests uses, so a garrison floor and a network floor are read
        // off identical hulls: Drone 500, Interceptor 1000, Cruiser 6000, Hauler unarmed.
        private static ArmedCatalogue Catalogue()
        {
            return new ArmedCatalogue().Add("Drone", 500, "Small").Add("Interceptor", 1000, "Small")
                .Add("Cruiser", 6000, "Large").Add("Hauler", 0, "Large", 5000);
        }

        private static CostRecipes Recipes()
        {
            var r = new CostRecipes();
            r.CostByPrefab["Drone"] = 10; r.CostByPrefab["Interceptor"] = 30; r.CostByPrefab["Cruiser"] = 500;
            return r;
        }

        private static ShipyardManager Manager()
        {
            return new ShipyardManager
            {
                PrefabListPicker = (role, tag) => role == "Raid" || role == "Patrol"
                    ? new[] { "Drone", "Interceptor", "Cruiser" }
                    : new[] { "Hauler" }
            };
        }

        /// <summary>A fight as the DEFENDER's copy, at a named node.</summary>
        private static FightRecord DefendedAt(string nodeId, long attackerDps, DateTime at)
        {
            return new FightRecord
            {
                Utc = at, Kind = ActionKind.Raid, Attacking = false, OpponentTag = "SPRT",
                NodeId = nodeId, AttackerDps = attackerDps, DefenderDps = 0,
                AttackerRatio = 1.0, AttackerHullsLost = 0,
            };
        }

        /// <summary>The yard fixture plus a second owned node, so "posted at the attacked node" can be
        /// told apart from "posted at the yard" — with one node they are the same place and any
        /// assertion about location would pass vacuously.</summary>
        private static WorldModel TwoNodeWorld(out SpatialNode yard, out SpatialNode outpost)
        {
            WorldModel world = Yard(Catalogue(), Recipes(), out yard);
            yard.ShipStock["Drone"] = 1;
            outpost = world.Graph.Add(new Vec3(500, 0, 0), 1.0, "OUTPOST");
            outpost.OwnerFactionTag = "F";
            outpost.HoldStrength = 1.0;
            world.Graph.Connect("YARD", "OUTPOST");
            return world;
        }

        private static List<string> Run(WorldModel world, FactionState state)
        {
            var log = new List<string>();
            Manager().Tick(new List<FactionState> { state }, world, world.Tuning, log);
            return log;
        }

        /// <summary><c>DemandEntry</c> is a STRUCT, so <c>Find</c> returns a zero-valued entry rather
        /// than null when nothing matches — <c>Assert.Null</c> can never pass on it and
        /// <c>Assert.NotNull</c> passes whether or not a demand exists. Every assertion here goes
        /// through these two instead.</summary>
        private static bool HasGarrison(WorldModel world, string nodeId)
        {
            return world.Demand.SnapshotShipDemands("F")
                       .Exists(d => d.Kind == "Ship:Patrol" && d.NodeId == nodeId && d.MinDps > 0);
        }

        /// <summary>The one garrison floor posted at <paramref name="nodeId"/>. Asserts there is
        /// exactly one, so a duplicate post cannot hide behind a first match.</summary>
        private static double GarrisonDps(WorldModel world, string nodeId)
        {
            List<DemandEntry> found = world.Demand.SnapshotShipDemands("F")
                .FindAll(d => d.Kind == "Ship:Patrol" && d.NodeId == nodeId && d.MinDps > 0);
            Assert.Single(found);
            return found[0].MinDps;
        }

        // ---- the headline ------------------------------------------------------------------------

        // The whole point of the slice: the demand lands where the fighting is, not at the yard.
        // OUTPOST holds nothing and has no station, so the entire budget is owed there.
        [Fact]
        public void ANodeThatWasAttacked_AsksForAGarrisonAtThatNode()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            var state = new FactionState { Tag = "F" };
            FightLedger.Append(state, DefendedAt("OUTPOST", 2500, T0.AddHours(-1)), world.Tuning.FightHistoryCap);

            List<string> log = Run(world, state);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Patrol")
                                      && l.Contains("node=OUTPOST") && l.Contains("budget=garrison"));
            Assert.True(HasGarrison(world, "OUTPOST"));
            // And NOT at the yard, which nobody attacked. This is the assertion a one-node world
            // could not make.
            Assert.False(HasGarrison(world, "YARD"));
        }

        [Fact]
        public void AFactionNobodyHasAttacked_AsksForNoGarrison()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            var state = new FactionState { Tag = "F" };
            Assert.Empty(state.Fights);

            List<string> log = Run(world, state);

            Assert.DoesNotContain(log, l => l.Contains("budget=garrison"));
            Assert.False(HasGarrison(world, "OUTPOST"));
            Assert.False(HasGarrison(world, "YARD"));
        }

        // The budget is per PLACE, not per faction. Same faction, same window, two nodes hit at very
        // different strengths: the quiet one must not inherit the loud one's floor.
        [Fact]
        public void TheBudgetIsPerNode_NotTheFactionsWorstFightEverywhere()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            SpatialNode quiet = world.Graph.Add(new Vec3(0, 500, 0), 1.0, "QUIET");
            quiet.OwnerFactionTag = "F"; quiet.HoldStrength = 1.0;

            var state = new FactionState { Tag = "F" };
            FightLedger.Append(state, DefendedAt("OUTPOST", 8000, T0.AddHours(-1)), world.Tuning.FightHistoryCap);
            FightLedger.Append(state, DefendedAt("QUIET", 400, T0.AddHours(-1)), world.Tuning.FightHistoryCap);

            Run(world, state);

            Assert.True(HasGarrison(world, "OUTPOST"));
            Assert.True(HasGarrison(world, "QUIET"));
            Assert.True(GarrisonDps(world, "OUTPOST") > GarrisonDps(world, "QUIET"),
                        "the node that met 8000 must ask for more than the one that met 400");

            Assert.Equal(8000, DefenceBudget.WantedDpsAt(state, "OUTPOST", world.Tuning, world.UtcNow));
            Assert.Equal(400, DefenceBudget.WantedDpsAt(state, "QUIET", world.Tuning, world.UtcNow));
            // The network-wide budget is the faction's worst anywhere, which is exactly what a
            // per-node question must not return.
            Assert.Equal(8000, DefenceBudget.WantedDps(state, world.Tuning, world.UtcNow));
        }

        // A fortified node needs fewer hulls standing on it. Same attack, same empty stock -- only the
        // station differs, so nothing else can explain the two outcomes.
        [Fact]
        public void TheStationCountsTowardTheGarrison_AndCanSatisfyItOutright()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            var state = new FactionState { Tag = "F" };
            FightLedger.Append(state, DefendedAt("OUTPOST", 2000, T0.AddHours(-1)), world.Tuning.FightHistoryCap);

            Run(world, state);
            Assert.True(HasGarrison(world, "OUTPOST"));   // bare claim: the whole budget is owed

            SpatialNode yard2, fort;
            WorldModel fortified = TwoNodeWorld(out yard2, out fort);
            fort.StationPrefabId = "Fort";
            fort.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 9000, RangeM = 800 } };
            var state2 = new FactionState { Tag = "F" };
            FightLedger.Append(state2, DefendedAt("OUTPOST", 2000, T0.AddHours(-1)), fortified.Tuning.FightHistoryCap);

            Run(fortified, state2);
            Assert.False(HasGarrison(fortified, "OUTPOST"));  // the guns it already has cover it
        }

        // Hulls already parked at the node count too, or the faction is asked to build a garrison it
        // is already standing on.
        [Fact]
        public void StockAlreadyAtTheNode_CountsTowardItsGarrison()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            outpost.ShipStock["Cruiser"] = 1;                 // 6000 Dps, far above the 400 budget
            var state = new FactionState { Tag = "F" };
            FightLedger.Append(state, DefendedAt("OUTPOST", 400, T0.AddHours(-1)), world.Tuning.FightHistoryCap);

            Run(world, state);

            Assert.True(ShipPool.BestSquadron(new[] { outpost }, world.PrefabCatalogue,
                                              world.Tuning.RaidSquadronMaxFighters).Dps >= 400,
                        "fixture check: the parked hull must actually cover the budget, or this proves nothing");
            Assert.False(HasGarrison(world, "OUTPOST"));
        }

        // A node lost since the attack is not garrisoned: the demand would pull hulls toward ground
        // this faction no longer holds.
        [Fact]
        public void ANodeLostSinceTheAttack_IsNotGarrisoned()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            outpost.OwnerFactionTag = "SPRT";                 // taken
            var state = new FactionState { Tag = "F" };
            FightLedger.Append(state, DefendedAt("OUTPOST", 2500, T0.AddHours(-1)), world.Tuning.FightHistoryCap);

            Run(world, state);

            Assert.False(HasGarrison(world, "OUTPOST"));
        }

        [Fact]
        public void AnAttackOlderThanTheMemoryWindow_GarrisonsNothing()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            var state = new FactionState { Tag = "F" };
            FightLedger.Append(state, DefendedAt("OUTPOST", 2500, T0 - world.Tuning.DefenceMemoryWindow.Add(TimeSpan.FromHours(1))),
                               world.Tuning.FightHistoryCap);

            Run(world, state);

            Assert.Equal(0, DefenceBudget.WantedDpsAt(state, "OUTPOST", world.Tuning, world.UtcNow));
            Assert.False(HasGarrison(world, "OUTPOST"));
        }

        // The bound on the demand register. A faction under attack everywhere asks for its worst
        // places first rather than filling the register in one tick.
        [Fact]
        public void NoMoreThanGarrisonNodesPerPass_AreAskedForInOneTick()
        {
            SpatialNode yard, outpost;
            WorldModel world = TwoNodeWorld(out yard, out outpost);
            var state = new FactionState { Tag = "F" };
            for (int i = 0; i < 6; i++)
            {
                SpatialNode n = world.Graph.Add(new Vec3(100 * (i + 2), 0, 0), 1.0, "N" + i);
                n.OwnerFactionTag = "F"; n.HoldStrength = 1.0;
                FightLedger.Append(state, DefendedAt("N" + i, 2500, T0.AddMinutes(-10 * i)),
                                   world.Tuning.FightHistoryCap);
            }

            Assert.Equal(3, world.Tuning.GarrisonNodesPerPass);
            Run(world, state);

            List<DemandEntry> garrisons = world.Demand.SnapshotShipDemands("F")
                .FindAll(d => d.Kind == "Ship:Patrol" && d.MinDps > 0);
            Assert.Equal(3, garrisons.Count);
            // Most recently hit first: N0 was attacked last.
            Assert.Contains(garrisons, d => d.NodeId == "N0");
        }

        // ---- the budget itself -------------------------------------------------------------------

        [Fact]
        public void AttackedNodesSince_ListsEachNodeOnce_MostRecentFirst()
        {
            var state = new FactionState { Tag = "F" };
            FactionTuning t = TestSupport.Tuning();
            FightLedger.Append(state, DefendedAt("A", 100, T0.AddHours(-3)), t.FightHistoryCap);
            FightLedger.Append(state, DefendedAt("B", 100, T0.AddHours(-2)), t.FightHistoryCap);
            FightLedger.Append(state, DefendedAt("A", 100, T0.AddHours(-1)), t.FightHistoryCap);

            List<string> nodes = DefenceBudget.AttackedNodesSince(state, T0.AddHours(-4));

            Assert.Equal(new[] { "A", "B" }, nodes);   // A is newest, and appears once

            // Order must come from the timestamps, not from ledger position. Appending B's newest
            // record LAST would put it last by position while it is first in time.
            var reversed = new FactionState { Tag = "F" };
            FightLedger.Append(reversed, DefendedAt("A", 100, T0.AddHours(-3)), t.FightHistoryCap);
            FightLedger.Append(reversed, DefendedAt("B", 100, T0.AddMinutes(-1)), t.FightHistoryCap);
            Assert.Equal(new[] { "B", "A" }, DefenceBudget.AttackedNodesSince(reversed, T0.AddHours(-4)));
        }

        // An attack this faction MADE is not an attack on it. Without the Attacking filter a raider
        // would garrison the nodes it raids.
        [Fact]
        public void AnAttackThisFactionMade_DoesNotGarrisonTheTarget()
        {
            var state = new FactionState { Tag = "F" };
            FactionTuning t = TestSupport.Tuning();
            FightRecord ours = DefendedAt("THEIRS", 5000, T0.AddHours(-1));
            ours.Attacking = true;
            FightLedger.Append(state, ours, t.FightHistoryCap);

            Assert.Empty(DefenceBudget.AttackedNodesSince(state, T0.AddHours(-4)));
            Assert.Equal(0, DefenceBudget.WantedDpsAt(state, "THEIRS", t, T0));
        }
    }
}
