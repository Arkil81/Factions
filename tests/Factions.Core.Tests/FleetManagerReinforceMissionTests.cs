using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Reinforce is a mission, not a purchase (FAC-327 slice B): a squadron flies to the
    /// node, holds an <see cref="FleetStopKind.Engage"/> dwell there, and delivers
    /// <see cref="FactionTuning.ReinforceStep"/> scaled by its completion ratio when that leg
    /// resolves. It can fail to find a hull, be destroyed on the way, be duelled at the node, and be
    /// refused outright while the node is under live attack.</summary>
    public class FleetManagerReinforceMissionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static FactionTuning Tuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.1;
            t.FleetSpeedKmPerMin = 1200.0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;   // budget out of the way
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            return t;
        }

        private static WorldModel World(FactionTuning t)
        {
            WorldModel w = TestSupport.World();
            w.Tuning = t;
            w.UtcNow = T0;
            return w;
        }

        private static SpatialNode Node(WorldModel w, string id, string owner, Vec3 pos, double hold)
        {
            SpatialNode n = w.Graph.Add(pos, 0.7, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionState Faction(string tag, params string[] reinforce)
        {
            var st = new FactionState { Tag = tag, RngSeed = 1 };
            for (int i = 0; i < reinforce.Length; i++)
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = reinforce[i], Score = 10.0 - i });
            return st;
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        // ---- B1: the dispatch ----------------------------------------------------------------

        [Fact]
        public void Dispatch_SpendsOnce_SendsOneFleet_AndTheHoldHasNotMovedYet()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            ReinforceMissionSupport.Depot(w, "F");
            FactionState st = Faction("F", "N001");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Single(log.Where(l => l.Contains("REINFORCE_DISPATCH")));
            Assert.Equal(10_000 - t.ReinforceCost, w.GetTreasury("F"));
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Reinforce));
            // The money bought a mission, not an outcome: nothing has reached the node yet.
            Assert.Equal(0.4, weak.HoldStrength, 6);
        }

        [Fact]
        public void TheHoldLandsOnlyWhenTheEngageLegResolves()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            ReinforceMissionSupport.Depot(w, "F");
            FactionState st = Faction("F", "N001");
            var states = new List<FactionState> { st };

            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);

            // Leg 1: the transit ends and the dwell opens. Still nothing delivered.
            ReinforceMissionSupport.FlyFleets(w, t, states, log, legs: 1);
            Assert.Equal(FleetStopKind.Engage, st.PendingFleets[0].CurrentStopKind);
            Assert.Equal(0.4, weak.HoldStrength, 6);

            // Leg 2: the dwell ends, and that is when the hold moves.
            ReinforceMissionSupport.FlyFleets(w, t, states, log, legs: 1);
            Assert.Equal(0.5, weak.HoldStrength, 6);
            Assert.Contains(log, l => l.Contains("REINFORCE_RESOLVED") && l.Contains("N001"));
        }

        [Fact]
        public void AMauledReinforce_DeliversTheStepScaledByItsRatio()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            ReinforceMissionSupport.Depot(w, "F");
            FactionState st = Faction("F", "N001");
            var states = new List<FactionState> { st };

            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);
            ReinforceMissionSupport.FlyFleets(w, t, states, log, legs: 1);   // on the dwell now

            // Half the squadron's combat capacity is gone: it delivers half the step.
            PendingFleetAction mauled = st.PendingFleets[0];
            mauled.ForcedRatio = 0.5;
            mauled.ForcedRatioCause = "guard_duel";
            st.PendingFleets[0] = mauled;

            ReinforceMissionSupport.FlyFleets(w, t, states, log, legs: 1);
            Assert.Equal(0.45, weak.HoldStrength, 6);
        }

        /// <summary>A faction with no flyable hull pays the flat cost and gets the hold anyway — the
        /// pre-mission reinforce, kept as the fallback. Without it both counter-forces to
        /// <c>DecayInfluence</c> need a hull the faction has not got, so its whole estate decays to
        /// UNKN and it can never claim or expand its way back.</summary>
        [Fact]
        public void NoHullAnywhere_PostsAPatrolDemand_AndPaysForTheHoldInstead()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            Node(w, "N002", "F", new Vec3(100, 0, 0), 0.9);   // owned, but holds no warship
            FactionState st = Faction("F", "N001");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            // No mission: nothing could fly one. The hold moved all the same, instantly.
            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_DISPATCH"));
            Assert.Empty(st.PendingFleets);
            Assert.Equal(0.5, weak.HoldStrength, 6);
            Assert.Equal(10_000 - t.ReinforceCost, w.GetTreasury("F"));
            Assert.Contains(log, l => l.Contains("REINFORCE_PAID") && l.Contains("why=no_hull_anywhere"));
            // Paying for hold is not a reason to stop wanting the hull.
            List<DemandEntry> demands = w.Demand.SnapshotShipDemands("F");
            Assert.Contains(demands, d => d.Kind == "Ship:Patrol" && d.NodeId == "N001");
            Assert.Contains(log, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("paid_no_hull=1"));
        }

        /// <summary>Third hull gate, and the one that emptied three factions live: the hull exists and
        /// is armed, and nothing survives the route. UVCO and TFEG each owned exactly one such hull
        /// (FAC-325). A route refusal must reach the fallback, not the decay.</summary>
        [Fact]
        public void AnArmedHullThatCannotFlyTheRoute_AlsoPays()
        {
            FactionTuning t = Tuning();
            t.MinDryTwr = 1.05;
            WorldModel w = World(t);
            w.Planets = new ReinforceMissionSupport.ConstantGravity(1.0);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            SpatialNode depot = Node(w, "N002", "F", new Vec3(100, 0, 0), 1.0);
            ReinforceMissionSupport.Stock(w, depot, 2, "Idris");
            // Mass with no thrust: armed, role-suitable, and it cannot lift out of 1 g.
            ((ReinforceMissionSupport.ArmedCatalogue)w.PrefabCatalogue).DryMassKgByPrefab["Idris"] = 500_000;
            FactionState st = Faction("F", "N001");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Equal(0.5, weak.HoldStrength, 6);
            Assert.Equal(10_000 - t.ReinforceCost, w.GetTreasury("F"));
            Assert.Contains(log, l => l.Contains("REINFORCE_PAID") && l.Contains("why=no_route_hull"));
            Assert.Empty(st.PendingFleets);
            // The hull is still in the pool: nothing was consumed to buy this.
            Assert.Equal(2, depot.ShipStock["Idris"]);
        }

        /// <summary>The discriminator: a faction that CAN fly the mission does not get the fallback.
        /// Same fixture as the dispatch test, asserting the absence — a fallback that fired for
        /// everyone would make the mission and its whole contestable window dead code.</summary>
        [Fact]
        public void AFlyableReinforce_TakesTheMission_NotTheFallback()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            ReinforceMissionSupport.Depot(w, "F");
            FactionState st = Faction("F", "N001");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Single(log.Where(l => l.Contains("REINFORCE_DISPATCH")));
            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_PAID"));
            Assert.Equal(0.4, weak.HoldStrength, 6);   // the mission has not arrived; nothing was paid for instantly
        }

        /// <summary>Money is the brake. The treasury gate sits upstream of the hull gates, so a broke
        /// faction with no hull gets nothing — which is what keeps the fallback from being free
        /// immortality for an estate that earns nothing.</summary>
        [Fact]
        public void TheFallbackIsRefusedWhenTheFactionCannotAffordIt()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", t.ReinforceCost - 1);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            FactionState st = Faction("F", "N001");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_PAID"));
            Assert.Equal(0.4, weak.HoldStrength, 6);
            Assert.Equal(t.ReinforceCost - 1, w.GetTreasury("F"));
            Assert.Contains(log, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_money=1"));
        }

        /// <summary>A player standing on an unfounded node is taking it (a presence raid), and a
        /// faction with no hull must not undo that by paying: a flown reinforce is refused there on
        /// arrival, so the fallback is too. Nothing is spent, the demand for the hull still goes up,
        /// and the node is bought on the first pass after the raid ends.</summary>
        [Fact]
        public void TheFallbackIsRefusedDuringAPresenceRaid_AndPaysOnceItEnds()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            Assert.True(PlayerSiege.BeginPresenceRaid(weak, t, w.UtcNow));
            FactionState st = Faction("F", "N001");
            var states = new List<FactionState> { st };
            var fm = new FleetManager();

            var log = new List<string>();
            fm.Tick(states, w, t, log);

            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_PAID cost="));
            Assert.Contains(log, l => l.Contains("F REINFORCE_PAID_BLOCKED N001") && l.Contains("by=player_siege")
                                      && l.Contains("why=no_hull_anywhere"));
            Assert.Contains(log, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("paid_no_hull=0")
                                      && l.Contains("under_attack=1"));
            Assert.Equal(0.4, weak.HoldStrength, 6);
            Assert.Equal(10_000, w.GetTreasury("F"));
            Assert.Contains(w.Demand.SnapshotShipDemands("F"), d => d.Kind == "Ship:Patrol" && d.NodeId == "N001");

            // The attacker leaves: Maintain resets a presence raid outright.
            Assert.True(PlayerSiege.Maintain(weak, t, w.UtcNow, attackerPresent: false));
            w.UtcNow = w.UtcNow.AddSeconds(1);
            var after = new List<string>();
            fm.Tick(states, w, t, after);

            Assert.Single(after.Where(l => l.Contains("REINFORCE_PAID cost=")));
            Assert.Equal(0.5, weak.HoldStrength, 6);
        }

        /// <summary>Recent player damage refuses it too, and the refusal is written once per node
        /// cooldown rather than on every tick of a manager that ticks about once a second.</summary>
        [Fact]
        public void TheFallbackIsRefusedUnderPlayerAttack_AndSaysSoOncePerCooldown()
        {
            FactionTuning t = Tuning();
            t.ReinforceNodeCooldown = TimeSpan.FromSeconds(900);
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode weak = Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            weak.LastPlayerAggressionUtc = w.UtcNow;
            FactionState st = Faction("F", "N001");
            var states = new List<FactionState> { st };
            var fm = new FleetManager();

            var first = new List<string>();
            fm.Tick(states, w, t, first);
            Assert.Single(first.Where(l => l.Contains("REINFORCE_PAID_BLOCKED") && l.Contains("by=player_attack")));

            w.UtcNow = w.UtcNow.AddSeconds(60);
            weak.LastPlayerAggressionUtc = w.UtcNow;
            var second = new List<string>();
            fm.Tick(states, w, t, second);
            Assert.DoesNotContain(second, l => l.Contains("REINFORCE_PAID"));

            Assert.Equal(0.4, weak.HoldStrength, 6);
            Assert.Equal(10_000, w.GetTreasury("F"));
        }

        /// <summary>...and the node cooldown bounds the rate, the same one the mission takes. Without
        /// it the fallback would re-buy the same node on every tick of a manager that ticks about once
        /// a second.</summary>
        [Fact]
        public void TheFallbackTakesTheNodeCooldown()
        {
            FactionTuning t = Tuning();
            t.ReinforceNodeCooldown = TimeSpan.FromSeconds(900);
            t.ReinforceStep = 0.05;   // small enough that one payment leaves a deficit worth buying
            t.ReinforceMinFillFraction = 0.1;
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000_000);
            Node(w, "N001", "F", new Vec3(0, 0, 0), 0.1);
            FactionState st = Faction("F", "N001");
            var states = new List<FactionState> { st };
            var fm = new FleetManager();

            var first = new List<string>();
            fm.Tick(states, w, t, first);
            Assert.Single(first.Where(l => l.Contains("REINFORCE_PAID")));

            w.UtcNow = w.UtcNow.AddSeconds(60);
            var second = new List<string>();
            fm.Tick(states, w, t, second);
            Assert.DoesNotContain(second, l => l.Contains("REINFORCE_PAID"));

            // ...and it is a cooldown, not a permanent refusal.
            w.UtcNow = w.UtcNow.AddSeconds(1000);
            var third = new List<string>();
            fm.Tick(states, w, t, third);
            Assert.Single(third.Where(l => l.Contains("REINFORCE_PAID")));
        }

        /// <summary>FAC-349 cause 2: that same shortage demand carries the faction's combat power
        /// floor. `Patrol` draws the pool `Raid` does, so a Patrol build IS a combat build and has to
        /// be ranked against the same bar — a floorless one resolves on the posture blend, which at a
        /// high posture takes the strongest hull in the pool.
        ///
        /// <para>Live 2026-09-18 on v0.263.0, the artifact that had just put the floor on every Raid
        /// demand: <c>19:22:25 SPRT SHIP_DEMAND role=Patrol node=N026 dest=N535</c> with no minDps,
        /// and two minutes later <c>BUILD_QUEUED role=Patrol prefab=Naginata</c> — the 229,659-unit
        /// flagship. These posts are where combat builds are actually created; the demands that DO
        /// carry a floor land on nodes whose entry `SelectPrefabBom` reuses.</para></summary>
        [Fact]
        public void NoHullAnywhere_AndAtWar_ThatDemandCarriesTheCombatFloor()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            Node(w, "N002", "F", new Vec3(100, 0, 0), 0.9);
            FactionState st = Faction("F", "N001");
            st.TerrorStandingDps = 500000;          // a standing combat requirement

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());

            DemandEntry patrol = w.Demand.SnapshotShipDemands("F").Find(d => d.Kind == "Ship:Patrol");
            Assert.True(patrol.MinDps > 0,
                "a Patrol demand is a combat demand and must carry the floor; got " + patrol.MinDps);
        }

        /// <summary>...and a faction nobody is fighting posts exactly what it posted before. The floor
        /// is self-gating — <c>ShipyardManager.CombatPowerFloor</c> is 0 with no offensive campaign
        /// and an empty fight ledger — so this is not a behaviour change for a peaceful network. Same
        /// scenario as the test above with the one term removed, so it is the war that decides.</summary>
        [Fact]
        public void NoHullAnywhere_AtPeace_ThatDemandIsPlain()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            Node(w, "N002", "F", new Vec3(100, 0, 0), 0.9);
            FactionState st = Faction("F", "N001");   // nothing standing, nothing fought

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());

            DemandEntry patrol = w.Demand.SnapshotShipDemands("F").Find(d => d.Kind == "Ship:Patrol");
            Assert.Equal("Ship:Patrol", patrol.Kind);   // it is still posted
            Assert.Equal(0, patrol.MinDps);
        }

        // ---- B1: the two bounds ---------------------------------------------------------------

        [Fact]
        public void MaxConcurrent_CapsTheNumberOfReinforcesInTheAir()
        {
            FactionTuning t = Tuning();
            t.ReinforceMaxConcurrent = 2;
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000_000);
            var ids = new List<string>();
            for (int i = 0; i < 6; i++) { Node(w, "N" + i, "F", new Vec3(i * 10, 0, 0), 0.4); ids.Add("N" + i); }
            ReinforceMissionSupport.Depot(w, "F");
            FactionState st = Faction("F", ids.ToArray());

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Equal(2, log.Count(l => l.Contains("REINFORCE_DISPATCH")));
            Assert.Equal(2, st.PendingFleets.Count(f => f.Kind == ActionKind.Reinforce));
            Assert.Contains(log, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("at_cap=1"));

            // The cap, not the money: six needy nodes and a treasury that affords all of them.
            Assert.Equal(10_000_000 - 2 * t.ReinforceCost, w.GetTreasury("F"));
        }

        [Fact]
        public void NodeCooldown_RefusesASecondReinforceToTheSameNode()
        {
            FactionTuning t = Tuning();
            t.ReinforceNodeCooldown = TimeSpan.FromSeconds(900);
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000_000);
            Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            ReinforceMissionSupport.Depot(w, "F");
            FactionState st = Faction("F", "N001");
            var states = new List<FactionState> { st };
            var fm = new FleetManager();

            var first = new List<string>();
            fm.Tick(states, w, t, first);
            Assert.Single(first.Where(l => l.Contains("REINFORCE_DISPATCH")));

            // Fly the whole mission home, so the in-flight claim is gone and the cooldown is the
            // only thing left refusing a second one.
            ReinforceMissionSupport.FlyFleets(w, t, states, first);
            Assert.Empty(st.PendingFleets);

            w.UtcNow = w.UtcNow.AddSeconds(60);
            var second = new List<string>();
            fm.Tick(states, w, t, second);
            Assert.DoesNotContain(second, l => l.Contains("REINFORCE_DISPATCH"));

            // ...and it is a cooldown, not a permanent refusal.
            w.UtcNow = w.UtcNow.AddSeconds(1000);
            var third = new List<string>();
            fm.Tick(states, w, t, third);
            Assert.Single(third.Where(l => l.Contains("REINFORCE_DISPATCH")));
        }

        // ---- B4: the hull ledger ---------------------------------------------------------------

        [Fact]
        public void ADispatchedReinforce_ConsumesAHull_AndAReturningOneCreditsBackExactlyWhatItTook()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            Node(w, "N001", "F", new Vec3(0, 0, 0), 0.4);
            ReinforceMissionSupport.Depot(w, "F", hulls: 5);
            FactionState st = Faction("F", "N001");
            var states = new List<FactionState> { st };

            int before = ReinforceMissionSupport.TotalShipStock(w);
            Assert.Equal(5, before);

            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);
            int drawn = before - ReinforceMissionSupport.TotalShipStock(w);
            Assert.Equal(1, drawn);

            ReinforceMissionSupport.FlyFleets(w, t, states, log);
            Assert.Empty(st.PendingFleets);
            Assert.Equal(before, ReinforceMissionSupport.TotalShipStock(w));
        }

        // ---- B3 rule 1: a dwelling reinforce is part of the node's defence ---------------------

        /// <summary>A Reinforce holding its dwell at <paramref name="nodeId"/>, built by hand so the
        /// fight can be stated without also stating a transit.</summary>
        private static PendingFleetAction DwellingReinforce(WorldModel w, string actor, SpatialNode node,
                                                            double dps, string origin = "DEPOT")
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Reinforce,
                ActorTag = actor,
                OriginNodeId = origin,
                ReturnNodeId = origin,
                TargetNodeId = node.Id,
                OriginPosKm = node.Position,
                TargetPosKm = node.Position,
                DepartedUtc = w.UtcNow.AddSeconds(-100),
                EstimatedArrivalUtc = w.UtcNow,          // the dwell ends now: this is its resolving leg
                CurrentStopKind = FleetStopKind.Engage,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = origin, Kind = FleetStopKind.Return },
                },
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                WeaponBands = dps > 0
                    ? new List<WeaponBand> { new WeaponBand { Dps = (float)dps, RangeM = 4000 } }
                    : new List<WeaponBand>(),
                LastResolvedSegment = -1,
            };
        }

        private static PendingFleetAction ArrivingRaid(WorldModel w, string actor, SpatialNode node, double dps)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = actor,
                TargetTag = node.OwnerFactionTag,
                OriginNodeId = null,
                TargetNodeId = node.Id,
                OriginPosKm = node.Position,
                TargetPosKm = node.Position,
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Engage,
                ShipPrefabId = "Raider",
                ConvoySize = 1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = (float)dps, RangeM = 4000 } },
                LastResolvedSegment = -1,
            };
        }

        /// <summary>The reinforce's own guns decide the raid's fate — the same raid against the same
        /// node with no reinforce standing there lands instead. Stating both is what makes this a
        /// test of the reinforce's bands rather than of two fleets coexisting.</summary>
        [Fact]
        public void ARaidResolvingAtTheNode_DuelsTheDwellingReinforce()
        {
            FactionTuning t = Tuning();

            // With the reinforce standing: it heavily outguns the raid, which is broken off.
            WorldModel w = World(t);
            SpatialNode n = Node(w, "N001", "DEF", new Vec3(0, 0, 0), 0.9);
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            def.PendingFleets.Add(DwellingReinforce(w, "DEF", n, dps: 20_000));
            raider.PendingFleets.Add(ArrivingRaid(w, "RVN", n, dps: 10));
            var withStates = new List<FactionState> { raider, def };
            var withLog = new List<string>();
            PendingFleetResolver.ResolveArrivals(withStates, w, t, new FixedRandom(0.0), withLog);

            Assert.Contains(withLog, l => l.Contains("GUARD_INTERCEPT") && l.Contains("DEF"));
            Assert.DoesNotContain(withLog, l => l.Contains("RAID_RESOLVED"));

            // Without it: the identical raid against the identical node lands.
            WorldModel w2 = World(t);
            SpatialNode n2 = Node(w2, "N001", "DEF", new Vec3(0, 0, 0), 0.9);
            var def2 = new FactionState { Tag = "DEF", RngSeed = 1 };
            var raider2 = new FactionState { Tag = "RVN", RngSeed = 1 };
            raider2.PendingFleets.Add(ArrivingRaid(w2, "RVN", n2, dps: 10));
            var withoutLog = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { raider2, def2 }, w2, t,
                                                 new FixedRandom(0.0), withoutLog);

            Assert.Contains(withoutLog, l => l.Contains("RAID_RESOLVED"));
            Assert.DoesNotContain(withoutLog, l => l.Contains("GUARD_INTERCEPT"));
        }

        [Fact]
        public void AReinforceDestroyedAtTheNode_DeliversNothing_AndIsNotRefunded()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("DEF", 10_000);
            SpatialNode n = Node(w, "N001", "DEF", new Vec3(0, 0, 0), 0.4);
            double holdBefore = n.HoldStrength;
            long treasuryBefore = w.GetTreasury("DEF");

            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            def.PendingFleets.Add(DwellingReinforce(w, "DEF", n, dps: 1));
            raider.PendingFleets.Add(ArrivingRaid(w, "RVN", n, dps: 1_000_000));
            var states = new List<FactionState> { raider, def };

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, t, new FixedRandom(0.0), log);

            // Destroyed in the duel: it never reaches its own resolution, so it delivers nothing.
            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_RESOLVED"));
            Assert.Empty(def.PendingFleets);
            Assert.True(n.HoldStrength <= holdBefore, "a destroyed reinforce must not raise the hold");
            Assert.Equal(treasuryBefore, w.GetTreasury("DEF"));   // spent at dispatch, never refunded
        }

        // ---- B3 rule 2: the effect is refused while the node is under live attack ---------------

        /// <summary>Both halves in one test: the same fleet at the same node delivers zero with a
        /// hostile raid standing there and the full step without one. A change that broke the effect
        /// outright would fail the second half.</summary>
        [Fact]
        public void ReinforceIsRefusedWhileTheNodeIsUnderAttack_AndDeliversInFullWhenItIsNot()
        {
            FactionTuning t = Tuning();

            // Blocked: a hostile raid is holding at the node when the dwell resolves. Its own guns
            // are nil so the duel cannot destroy it -- the refusal is the rule, not the fight.
            WorldModel wBlocked = World(t);
            SpatialNode blockedNode = Node(wBlocked, "N001", "DEF", new Vec3(0, 0, 0), 0.4);
            var defBlocked = new FactionState { Tag = "DEF", RngSeed = 1 };
            var raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            defBlocked.PendingFleets.Add(DwellingReinforce(wBlocked, "DEF", blockedNode, dps: 1_000_000));
            PendingFleetAction siege = ArrivingRaid(wBlocked, "RVN", blockedNode, dps: 1);
            siege.EstimatedArrivalUtc = wBlocked.UtcNow.AddSeconds(600);   // mid-dwell: present, not resolving
            raider.PendingFleets.Add(siege);
            var blockedLog = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { defBlocked, raider },
                                                 wBlocked, t, new FixedRandom(0.0), blockedLog);

            Assert.Contains(blockedLog, l => l.Contains("REINFORCE_BLOCKED") && l.Contains("N001") && l.Contains("RVN"));
            Assert.Equal(0.4, blockedNode.HoldStrength, 6);

            // Clear: the same fleet, the same node, nobody attacking it.
            WorldModel wClear = World(t);
            SpatialNode clearNode = Node(wClear, "N001", "DEF", new Vec3(0, 0, 0), 0.4);
            var defClear = new FactionState { Tag = "DEF", RngSeed = 1 };
            defClear.PendingFleets.Add(DwellingReinforce(wClear, "DEF", clearNode, dps: 1_000_000));
            var clearLog = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { defClear }, wClear, t,
                                                 new FixedRandom(0.0), clearLog);

            Assert.DoesNotContain(clearLog, l => l.Contains("REINFORCE_BLOCKED"));
            Assert.Equal(0.5, clearNode.HoldStrength, 6);
        }

        /// <summary>The per-node cooldown is only worth having if it survives a restart: a Torch
        /// restart mid-flight would otherwise let every below-threshold node be re-sent at once.</summary>
        [Fact]
        public void TheNodeCooldown_SurvivesASaveAndLoad()
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 0.6, "N001");
            n.OwnerFactionTag = "F";

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                var st = new FactionState { Tag = "F" };
                DateTime until = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc);
                st.ReinforceNodeCooldowns["N001"] = until;
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("F"), FactionStore.DefaultLogCap));

                FactionState reloaded = FactionStore.ApplyBrainDoc(store.LoadBrain("F"), new SpatialGraph());
                Assert.Equal(until, reloaded.ReinforceNodeCooldowns["N001"]);

                // Lean JSON: a faction with no cooldowns writes no key at all.
                BrainDoc empty = FactionStore.ToBrainDoc(new FactionState { Tag = "G" },
                                                         graph.OwnedBy("G"), FactionStore.DefaultLogCap);
                Assert.Null(empty.ReinforceNodeCooldowns);
            }
            finally
            {
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void APlayerSiegeAlsoRefusesTheReinforce()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            SpatialNode n = Node(w, "N001", "DEF", new Vec3(0, 0, 0), 0.4);
            n.ZoneBreachedUtc = w.UtcNow.AddSeconds(-30);      // a player raid is live on it

            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            def.PendingFleets.Add(DwellingReinforce(w, "DEF", n, dps: 500));

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def }, w, t,
                                                 new FixedRandom(0.0), log);

            Assert.Contains(log, l => l.Contains("REINFORCE_BLOCKED") && l.Contains("player_siege"));
            Assert.Equal(0.4, n.HoldStrength, 6);
        }

        [Fact]
        public void AnAlliedFleetAtTheNodeDoesNotBlockTheReinforce()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            SpatialNode n = Node(w, "N001", "DEF", new Vec3(0, 0, 0), 0.4);

            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            var ally = new FactionState { Tag = "ALY", RngSeed = 1 };
            def.PactWith.Add("ALY");
            ally.PactWith.Add("DEF");
            def.PendingFleets.Add(DwellingReinforce(w, "DEF", n, dps: 1_000_000));
            PendingFleetAction passing = ArrivingRaid(w, "ALY", n, dps: 1);
            passing.EstimatedArrivalUtc = w.UtcNow.AddSeconds(600);
            ally.PendingFleets.Add(passing);

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, ally }, w, t,
                                                 new FixedRandom(0.0), log);

            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_BLOCKED"));
            Assert.Equal(0.5, n.HoldStrength, 6);
        }
    }
}
