using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>FAC-342/343/344/345 — what a faction does as it is being conquered.
    ///
    /// <para>Measured live 2026-09-18: SPRT, 122 nodes and 3.58 bn credits, lost 15 hulls to a
    /// 10-node player faction in one day across 14 intercepts, landed no raid, dispatched no guard,
    /// and sent one 2,500-Dps armed hauler at a time into defences reading 6,300 and above. Its
    /// fatigue toward the attacker sat at the clamp, which switched retaliation and conquest off
    /// entirely; its yards queued the single most expensive warship in the catalogue and had not
    /// finished one in three days.</para></summary>
    public class ResistanceEscalationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

        // ---- FAC-342: fatigue is a ratio of real quantities, so it cannot ratchet ----------------

        private static WorldModel Border(out SpatialNode ours)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            ours = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            ours.OwnerFactionTag = "A"; ours.HoldStrength = 0.8; ours.Radius = 30;
            SpatialNode theirs = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1");
            theirs.OwnerFactionTag = "B"; theirs.HoldStrength = 0.8; theirs.Radius = 30;
            return w;
        }

        private static FactionState AtWarWith(string tag, int convoysLost, long dpsEach)
        {
            var s = new FactionState { Tag = "A" };
            s.Relations[tag] = -0.9;
            s.EarnedRelations[tag] = -0.9;
            for (int i = 0; i < convoysLost; i++)
                FightLedger.Append(s, new FightRecord
                {
                    Utc = T0.AddMinutes(-i),
                    Kind = ActionKind.Raid,
                    Attacking = true,
                    OpponentTag = tag,
                    NodeId = "B1",
                    AttackerDps = dpsEach,
                    AttackerRatio = 0,
                    AttackerHullsLost = 1,
                }, 64);
            Diplomacy.HitFatigue(s, tag, 0.0, T0);
            return s;
        }

        /// <summary>The headline. Two factions take an identical beating; the one that can still
        /// field a fleet is not exhausted by it and the one that cannot is. Under the per-quantum
        /// accrual both saturated at 1.0, because what was being measured was how long the war had
        /// run rather than what it had cost.</summary>
        [Fact]
        public void TheSameLosses_ExhaustTheSmallFleetAndNotTheLarge()
        {
            FactionTuning t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");

            SpatialNode bigNode, smallNode;
            WorldModel big = Border(out bigNode);
            big.PrefabCatalogue = cat;
            bigNode.ShipStock["Fighter"] = 40;                 // 40,000 Dps still under arms

            WorldModel small = Border(out smallNode);
            small.PrefabCatalogue = cat;
            smallNode.ShipStock["Fighter"] = 1;                // 1,000 Dps left

            FactionState rich = AtWarWith("B", 4, 1000);       // 4,000 Dps spent and not recovered
            FactionState poor = AtWarWith("B", 4, 1000);       // the very same losses

            // GetFleetCombatPower is published by the income pass, which runs immediately before the
            // diplomacy phase AccrueFatigue lives in. Running it here is what makes the fixture the
            // same shape as the live pass rather than a world with an empty cache.
            big.AccrueIncome(new List<FactionState> { rich });
            small.AccrueIncome(new List<FactionState> { poor });

            Diplomacy.AccrueFatigue(rich, big, t);
            Diplomacy.AccrueFatigue(poor, small, t);

            Assert.True(rich.FatigueTo("B") < 0.2,
                "a faction that can replace what it lost is not worn out by losing it; got " + rich.FatigueTo("B"));
            Assert.True(poor.FatigueTo("B") > 0.7,
                "a faction ground down to nothing is; got " + poor.FatigueTo("B"));
            Assert.True(poor.FatigueTo("B") > rich.FatigueTo("B"));
        }

        /// <summary>And it releases as the fleet is rebuilt, which is the property the old ratchet
        /// could not have: there, fighting on could only ever raise it.</summary>
        [Fact]
        public void RebuildingTheFleet_ReleasesTheWeariness()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode ours;
            WorldModel w = Border(out ours);
            w.PrefabCatalogue = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            ours.ShipStock["Fighter"] = 1;

            FactionState s = AtWarWith("B", 4, 1000);
            var roster = new List<FactionState> { s };
            w.AccrueIncome(roster);
            Diplomacy.AccrueFatigue(s, w, t);
            double spent = s.FatigueTo("B");

            ours.ShipStock["Fighter"] = 40;                    // the yards deliver
            w.AccrueIncome(roster);
            Diplomacy.AccrueFatigue(s, w, t);

            Assert.True(s.FatigueTo("B") < spent,
                "rebuilding must release weariness; before=" + spent + " after=" + s.FatigueTo("B"));
        }

        // ---- FAC-343: a squadron is sized against the defence, not against the roster ------------

        private static WorldModel RaidWorld(out SpatialNode origin, out SpatialNode target, ArmedCatalogue cat)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            w.PrefabCatalogue = cat;
            origin = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "HOME");
            origin.OwnerFactionTag = "A"; origin.HoldStrength = 1.0; origin.StableStationId = 1;
            origin.Radius = 40;
            target = w.Graph.Add(new Vec3(4000, 0, 0), 0.6, "MARK");
            target.OwnerFactionTag = "B"; target.HoldStrength = 1.0; target.Radius = 40;
            return w;
        }

        /// <summary>A defended node draws a bigger squadron than an undefended one out of the very
        /// same stock. Before this, both drew the fixed roster — one large hull, up to
        /// RaidSquadronMaxFighters small ones — and the target had no say at all.</summary>
        [Fact]
        public void ADefendedTarget_DrawsABiggerSquadronThanAnEmptyOne()
        {
            FactionTuning t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");

            SpatialNode weakOrigin, weakTarget;
            WorldModel weak = RaidWorld(out weakOrigin, out weakTarget, cat);
            weakOrigin.ShipStock["Fighter"] = 30;

            SpatialNode strongOrigin, strongTarget;
            WorldModel strong = RaidWorld(out strongOrigin, out strongTarget, cat);
            strongOrigin.ShipStock["Fighter"] = 30;
            strongTarget.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 12000, RangeM = 800 } };

            int weakSize = DispatchAt(weak, weakTarget, t);
            int strongSize = DispatchAt(strong, strongTarget, t);

            Assert.True(strongSize > weakSize,
                "the defence has to decide the size; undefended=" + weakSize + " defended=" + strongSize);
        }

        /// <summary>The hull cap follows the Dps target rather than bounding it. With the cap at the
        /// old fighter roster a swarm could never be fielded, which is what made the cheap hull the
        /// wrong answer before it could be built.</summary>
        [Fact]
        public void TheSquadronPassesTheOldFighterCap_WhenTheDefenceDemandsIt()
        {
            FactionTuning t = TestSupport.Tuning();
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            SpatialNode origin, target;
            WorldModel w = RaidWorld(out origin, out target, cat);
            origin.ShipStock["Fighter"] = 30;
            target.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 12000, RangeM = 800 } };

            int size = DispatchAt(w, target, t);

            Assert.True(size > t.RaidSquadronMaxFighters + 1,
                "sized past the roster cap; got " + size + " cap+lead=" + (t.RaidSquadronMaxFighters + 1));
            Assert.True(size <= t.RaidSquadronMaxHulls, "and still bounded; got " + size);
        }

        private static int DispatchAt(WorldModel w, SpatialNode target, FactionTuning t)
        {
            var st = new FactionState { Tag = "A", RngSeed = 1 };
            double defence = CombatStrength.OfNodeDefence(target, w.PrefabCatalogue, t);
            double want = CombatStrength.DpsForDamage(
                CombatStrength.DamageToDestroy(defence, t.RaidOverwhelmMargin), t);
            var intent = new FactionIntent
            {
                Kind = ActionKind.Raid,
                TargetNodeId = target.Id,
                TargetFactionTag = target.OwnerFactionTag,
                Cost = t.RaidCost,
            };
            PendingFleetAction fleet;
            Assert.True(OffensiveDispatch.Dispatch(st, intent, w, t, false, w.PrefabCatalogue,
                null, null, null, null, null, out fleet, null, want));
            return fleet.ConvoySize;
        }

        // ---- FAC-345: a gap no hull can close is closed by value, not by size --------------------

        /// <summary>A gap no hull can close is closed by value per unit built. The pool is built so
        /// the three rules that could be at work each name a DIFFERENT hull, or the test would pass
        /// on a rule it is not about: cheapest-first takes Picket, highest-Dps takes Flagship, and
        /// only Dps-per-unit takes Swarmer.
        ///
        /// <para>Swarmer is the answer because it is the answer a player reaches unaided: at the
        /// Flagship's price it buys four hulls and 3,600 Dps against 1,000, and they arrive one at a
        /// time instead of as a single build nobody finishes.</para></summary>
        [Fact]
        public void AnUnreachableFloor_TakesTheBestValueHull_NotTheStrongestOrTheCheapest()
        {
            var cat = new ArmedCatalogue()
                .Add("Picket", 100, "Small")           // cheapest to build
                .Add("Swarmer", 900, "Small")          // best Dps per unit built
                .Add("Flagship", 1000, "Large");       // strongest single hull
            var recipes = new CostRecipes();
            recipes.CostByPrefab["Picket"] = 5;        // 20 Dps per unit built
            recipes.CostByPrefab["Swarmer"] = 10;      // 90 Dps per unit built
            recipes.CostByPrefab["Flagship"] = 40;     // 25 Dps per unit built
            SpatialNode yard;
            WorldModel world = Yard(cat, recipes, out yard);
            var state = new FactionState { Tag = "F", TerrorStandingDps = 50000 };

            var mgr = new ShipyardManager
            {
                PrefabListPicker = (role, tag) =>
                    role == "Raid" ? new[] { "Picket", "Swarmer", "Flagship" } : new string[0],
            };
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            BuildQueueEntry raid = yard.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(raid);
            Assert.Equal("Swarmer", raid.PrefabId);
        }

        /// <summary>A floor one hull CAN clear is a requirement, not a gap, and is still answered by a
        /// hull that meets it. Without this the swarm rule would quietly take over every combat build
        /// and a faction could never field anything heavy.</summary>
        [Fact]
        public void AReachableFloor_StillTakesAHullThatMeetsIt()
        {
            var cat = new ArmedCatalogue()
                .Add("Picket", 100, "Small").Add("Swarmer", 900, "Small").Add("Flagship", 1000, "Large");
            var recipes = new CostRecipes();
            recipes.CostByPrefab["Picket"] = 5;
            recipes.CostByPrefab["Swarmer"] = 10;
            recipes.CostByPrefab["Flagship"] = 40;
            SpatialNode yard;
            WorldModel world = Yard(cat, recipes, out yard);
            var state = new FactionState { Tag = "F", TerrorStandingDps = 950 };   // only a Flagship clears it

            var mgr = new ShipyardManager
            {
                PrefabListPicker = (role, tag) =>
                    role == "Raid" ? new[] { "Picket", "Swarmer", "Flagship" } : new string[0],
            };
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            BuildQueueEntry raid = yard.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(raid);
            Assert.Equal("Flagship", raid.PrefabId);
        }

        // ---- FAC-344: the combat target is a function of the estate, not a constant --------------

        /// <summary>A 40-node estate wants more combat hulls than a 1-node one at the same posture.
        /// The flat pair of constants read the same for both, which is how a 122-node faction came to
        /// want four.</summary>
        [Fact]
        public void AWideEstate_WantsMoreCombatHullsThanAnOutpost()
        {
            Assert.True(TargetFor(40) > TargetFor(1),
                "estate must move the target; wide=" + TargetFor(40) + " outpost=" + TargetFor(1));
        }

        /// <summary>The combat hull target, read through the behaviour rather than restated as a
        /// formula: stock the network one hull at a time on a fresh world each time, and the first
        /// count at which the proactive Raid demand stops being posted IS the target.</summary>
        private static int TargetFor(int nodes)
        {
            for (int stock = 0; stock <= 80; stock++)
            {
                var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
                var recipes = new CostRecipes();
                recipes.CostByPrefab["Fighter"] = 10;
                SpatialNode yard;
                WorldModel world = Yard(cat, recipes, out yard);
                for (int i = 1; i < nodes; i++)
                {
                    SpatialNode n = world.Graph.Add(new Vec3(i * 5, 0, 0), 0.5, "N" + i);
                    n.OwnerFactionTag = "F"; n.HoldStrength = 1.0;
                }
                yard.ShipStock["Fighter"] = stock;

                var state = new FactionState { Tag = "F", MilitaryPosture = 1.0 };
                var mgr = new ShipyardManager
                {
                    PrefabListPicker = (role, tag) => role == "Raid" ? new[] { "Fighter" } : new string[0],
                };
                var log = new List<string>();
                mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);
                if (!log.Exists(l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid"))) return stock;
            }
            return 81;
        }
    }
}
