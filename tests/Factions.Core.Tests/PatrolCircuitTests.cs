using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-309 slice 3: Patrol is the picket a defensive focus keeps around its region —
    /// a multi-leg circuit flown from somewhere other than the node it is going to, rate-limited by
    /// a cooldown that is longer than the tick that sets it.</summary>
    public class PatrolCircuitTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return new[] { new WeaponBand { Dps = 500, RangeM = 800 } };
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static FactionTuning Tuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.PatrolCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.MusterConvoyScaleMax = 1.0;
            t.PatrolBurstSize = 1;
            t.PatrolCircuitMaxStops = 4;
            t.PatrolNodeCooldown = TimeSpan.FromSeconds(900);
            return t;
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner; n.HoldStrength = 0.9; n.Radius = 40;
            return n;
        }

        /// <summary>HOME plus three region nodes in a line, all owned by DEF, HOME holding the
        /// hulls. Threat is left at zero unless a test sets it — the focus is the trigger.</summary>
        private static WorldModel MakeRegionWorld(int hulls = 4)
        {
            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = new Cat();
            w.SetTreasury("DEF", 500000);
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "N001", "DEF", new Vec3(10, 0, 0));
            AddNode(w, "N002", "DEF", new Vec3(20, 0, 0));
            AddNode(w, "N003", "DEF", new Vec3(30, 0, 0));
            w.Graph.Connect("HOME", "N001");
            w.Graph.Connect("N001", "N002");
            w.Graph.Connect("N002", "N003");
            home.ShipStock["Picket"] = hulls;
            return w;
        }

        private static List<string> Run(WorldModel w, FactionTuning t, params FactionState[] states)
        {
            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);
            return log;
        }

        private static string PatrolLine(IEnumerable<string> log)
        {
            return log.FirstOrDefault(l => l.Contains("PATROL_DISPATCH"));
        }

        // The circuit itself: one dispatch covers every member of the focus region, in legs, and
        // comes home. A there-and-back sweep is what made interception impossible — a leg's
        // mid-transit meeting check needs PathNodeIds.Length >= 3.
        [Fact]
        public void ADefensiveFocusSendsOneCircuitOverEveryRegionMember()
        {
            WorldModel w = MakeRegionWorld();
            FactionTuning t = Tuning();
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N001", "N002", "N003");

            List<string> log = Run(w, t, st);

            string line = PatrolLine(log);
            Assert.NotNull(line);
            PendingFleetAction patrol = Assert.Single(st.PendingFleets);

            // First leg plus the queued ones covers all three members, and the last stop is home.
            var visited = new List<string> { patrol.TargetNodeId };
            visited.AddRange(patrol.RemainingStops.Select(s => s.NodeId));
            Assert.Equal(new[] { "N001", "N002", "N003", "HOME" }, visited.ToArray());
            Assert.Equal(FleetStopKind.Return, patrol.RemainingStops.Last().Kind);
            Assert.All(patrol.RemainingStops.Take(patrol.RemainingStops.Count - 1),
                       s => Assert.Equal(FleetStopKind.Service, s.Kind));
        }

        // Measurement 2 of FAC-309: 55 of 121 live dispatches read "Nxxx -> Nxxx" because
        // FindShipSource was called with excludeNodeId null, so the threatened node was its own
        // nearest source at distance 0 and always won.
        [Fact]
        public void TheCircuitIsFlownFromSomewhereOtherThanItsFirstStop()
        {
            WorldModel w = MakeRegionWorld();
            FactionTuning t = Tuning();
            // The node being patrolled holds hulls of its own, and is nearer to itself than HOME is.
            w.Graph.Nodes["N001"].ShipStock["Picket"] = 4;
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N001");

            string line = PatrolLine(Run(w, t, st));

            Assert.NotNull(line);
            Assert.Contains("HOME -> N001", line);
            Assert.DoesNotContain("N001 -> N001", line);
            Assert.Equal("HOME", st.PendingFleets.Single().OriginNodeId);
        }

        // Measurement 3: PatrolNodeCooldowns used FleetCooldown (10 s live) against a 60 s tick, so
        // it blocked nothing — METC dispatched to N022 nine times in nine consecutive minutes.
        [Fact]
        public void ANodeJustPatrolledIsNotPatrolledAgainInsideTheCooldown()
        {
            WorldModel w = MakeRegionWorld(hulls: 8);
            FactionTuning t = Tuning();
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N001");

            Assert.NotNull(PatrolLine(Run(w, t, st)));

            // A minute later — well past the old 10 s FleetCooldown, well inside the new one.
            w.UtcNow = w.UtcNow.AddSeconds(60);
            Assert.Null(PatrolLine(Run(w, t, st)));

            // And once the cooldown has run out, the same node is patrolled again — so the
            // assertion above is a cooldown, not a one-shot.
            w.UtcNow = w.UtcNow.AddSeconds(900);
            Assert.NotNull(PatrolLine(Run(w, t, st)));
        }

        // The whole circuit is claimed, not just the lead: a second patrol sent to a node this one
        // is already inbound to is the wasted hull the cooldown exists to refuse.
        [Fact]
        public void EveryStopOnTheCircuitGoesOnCooldownNotJustTheFirst()
        {
            WorldModel w = MakeRegionWorld(hulls: 8);
            FactionTuning t = Tuning();
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N001", "N002", "N003");

            Run(w, t, st);

            Assert.True(st.PatrolNodeCooldowns.ContainsKey("N001"));
            Assert.True(st.PatrolNodeCooldowns.ContainsKey("N002"));
            Assert.True(st.PatrolNodeCooldowns.ContainsKey("N003"));
        }

        // Threat orders the circuit, it does not summon it: the hottest member leads so a region
        // that IS hot is answered at its hottest point first.
        [Fact]
        public void ThreatOrdersTheCircuitRatherThanTriggeringIt()
        {
            WorldModel w = MakeRegionWorld();
            FactionTuning t = Tuning();
            w.Graph.Nodes["N003"].Threat = 0.9;   // farthest from HOME, but the worst
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N001", "N002", "N003");

            string line = PatrolLine(Run(w, t, st));

            Assert.NotNull(line);
            Assert.Contains("HOME -> N003", line);
            Assert.Equal("N003", st.PendingFleets.Single().TargetNodeId);
        }

        // A cold region is still swept — that is what "predictive" means, and it is the behaviour
        // the raw-threat trigger could not produce.
        [Fact]
        public void AQuietRegionIsStillPatrolled()
        {
            WorldModel w = MakeRegionWorld();
            FactionTuning t = Tuning();
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N001", "N002");

            Assert.All(new[] { "N001", "N002" }, id => Assert.Equal(0, w.Graph.Nodes[id].Threat));
            Assert.NotNull(PatrolLine(Run(w, t, st)));
        }

        // An offensive focus stands on a staging node and names ground the faction does not hold,
        // so its members are not a circuit — patrolling them would fly into someone else's space.
        [Fact]
        public void AnOffensiveFocusDoesNotSummonAPatrol()
        {
            WorldModel w = MakeRegionWorld();
            FactionTuning t = Tuning();
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            var focus = DefensiveFocusFixtures.Defensive(w.UtcNow, "N001", "N002");
            focus.Objectives.Clear();
            focus.Objectives.Add(new CampaignObjective
            {
                Kind = CampaignObjectiveKind.SeizeRegion,
                TargetNodeId = "N001",
            });
            st.CampaignFoci.Add(focus);
            CampaignFocusLifecycle.RefreshCompatibilityView(st);

            Assert.Null(PatrolLine(Run(w, t, st)));
        }

        // The seam slice 5 landed on. A player faction patrols what its player asked for: a focus of
        // any other provenance on such a faction is a leftover, not an order. The creation path is
        // CampaignFocusLifecycle.TryStartPlayer (PlayerCampaignTests); this pins the gate alone.
        [Fact]
        public void APlayerFactionPatrolsOnlyAPlayerIssuedFocus()
        {
            WorldModel w = MakeRegionWorld();
            FactionTuning t = Tuning();
            var player = new FactionState { Tag = "DEF", RngSeed = 1, IsPlayerFaction = true };
            DefensiveFocusFixtures.GiveDefensiveFocus(player, w.UtcNow, "N001", "N002");

            Assert.Null(PatrolLine(Run(w, t, player)));

            player.CampaignFoci[0].PlayerIssued = true;
            Assert.NotNull(PatrolLine(Run(w, t, player)));
        }
    }
}
