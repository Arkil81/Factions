using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-312: Guard and Patrol draw a mixed squadron the way a raid does — a large lead
    /// plus the strongest hulls — instead of one prefab times a count.</summary>
    public class DefensiveSquadronTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public readonly Dictionary<string, float> Dps = new Dictionary<string, float>();
            public readonly Dictionary<string, string> Grid = new Dictionary<string, string>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float d;
                return Dps.TryGetValue(prefabId ?? "", out d) && d > 0
                    ? new[] { new WeaponBand { Dps = d, RangeM = 800 } }
                    : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { string g; return Grid.TryGetValue(prefabId ?? "", out g) ? g : "Small"; }
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
            t.GuardCost = 1000; t.PatrolCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            t.MusterConvoyScaleMax = 1.0;
            t.GuardBurstSize = 1;
            t.StationDefensePower = 0;
            t.GuardCommitRatio = 1.2;
            // These were written against a single convoy sized to the commit margin, which is the
            // composition rule they pin. The marshal's own knobs are held at that same premise so
            // they keep pinning it: one wave, sized to 1.2.
            t.GuardOverwhelmMargin = 1.2;
            t.GuardMaxConvoysPerThreat = 1;
            // And at the roster-sized wave (fighters + 1) these composition cases were written
            // against; the squadron-sized wave is pinned in GuardWaveCapTests.
            t.RaidSquadronMaxHulls = 0;
            return t;
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner; n.HoldStrength = 0.9; n.Radius = 40;
            return n;
        }

        /// <summary>HOME holds one 2500-DPS large Vulture and fourteen 300-DPS small Tantos;
        /// N002 is raided by <paramref name="raidDps"/>.</summary>
        private static WorldModel MakeWorld(out FactionState defender, out FactionState raider, double raidDps,
                                            int vultures = 1, int tantos = 14)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            var cat = new Cat();
            cat.Dps["Vulture"] = 2500f; cat.Grid["Vulture"] = "Large";
            cat.Dps["Tanto"] = 300f;    cat.Grid["Tanto"] = "Small";
            w.PrefabCatalogue = cat;
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            w.Graph.Connect("HOME", "N002");
            if (vultures > 0) home.ShipStock["Vulture"] = vultures;
            if (tantos > 0) home.ShipStock["Tanto"] = tantos;
            defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            raider = new FactionState { Tag = "RVN", RngSeed = 1 };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetNodeId = "N002",
                OriginPosKm = new Vec3(500, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(30), FleetId = "RAIDN002",
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = (float)raidDps, RangeM = 800 } },
                LastResolvedSegment = -1,
            });
            return w;
        }

        private static List<string> Run(WorldModel w, FactionTuning t, params FactionState[] states)
        {
            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);
            return log;
        }

        // The lead goes first and the fighters fill in behind it — the composition rule this file
        // exists for. The SIZE is no longer DPS parity with the attack: a convoy is drawn until the
        // duel says the raider is defeated. Against a 3000-DPS raider the lead and three fighters
        // fall short of that and the fourth carries it (duel=3885 of the 3600 that destroys, and
        // more than the share that drives it off). The node can field 3700 in one convoy, so the
        // convoy fills.
        [Fact]
        public void AGuardIsALargeLeadPlusEnoughFighters_NotOnePrefabTimesACount()
        {
            FactionState def, rvn;
            WorldModel w = MakeWorld(out def, out rvn, raidDps: 3000);
            List<string> log = Run(w, Tuning(), def, rvn);

            string line = Assert.Single(log, l => l.Contains("GUARD_DISPATCH"));
            Assert.Contains("count=5", line);
            Assert.Contains("squadron=Vulture,Tanto,Tanto,Tanto,Tanto", line);
            PendingFleetAction guard = Assert.Single(def.PendingFleets);
            Assert.Equal(new[] { "Vulture", "Tanto", "Tanto", "Tanto", "Tanto" }, guard.Squadron);
            Assert.Equal("Vulture", guard.ShipPrefabId);     // the large lead, not the commonest hull
            Assert.Equal(5, guard.ConvoySize);
            Assert.Equal(3700f, guard.WeaponBands.Sum(b => b.Dps));
            Assert.Equal(10, w.Graph.Nodes["HOME"].ShipStock["Tanto"]);   // four of fourteen drawn
            int vLeft; w.Graph.Nodes["HOME"].ShipStock.TryGetValue("Vulture", out vLeft); Assert.Equal(0, vLeft);
            Assert.DoesNotContain(log, l => l.Contains("GUARD_OUTGUNNED"));
        }

        // Fighters alone, no lead: they stack to the convoy cap rather than to a DPS figure, and the
        // node keeps what the cap left. The cap is what bounds one convoy; the marshal is what sends
        // the rest, and it is held at one convoy here (see Tuning).
        [Fact]
        public void FightersAloneStackToTheConvoyCap()
        {
            FactionState def, rvn;
            WorldModel w = MakeWorld(out def, out rvn, raidDps: 2000, vultures: 0);
            List<string> log = Run(w, Tuning(), def, rvn);

            string line = Assert.Single(log, l => l.Contains("GUARD_DISPATCH"));
            Assert.Contains("count=5", line);
            Assert.Equal(9, w.Graph.Nodes["HOME"].ShipStock["Tanto"]);
        }


        [Fact]
        public void ARefusalHandsEveryMemberBack()
        {
            FactionState def, rvn;
            WorldModel w = MakeWorld(out def, out rvn, raidDps: 20000);   // 24000 needed; the node fields 6700 at most
            List<string> log = Run(w, Tuning(), def, rvn);

            Assert.Contains(log, l => l.Contains("GUARD_OUTGUNNED"));
            Assert.DoesNotContain(log, l => l.Contains("GUARD_DISPATCH"));
            Assert.Equal(1, w.Graph.Nodes["HOME"].ShipStock["Vulture"]);
            Assert.Equal(14, w.Graph.Nodes["HOME"].ShipStock["Tanto"]);
            Assert.Empty(def.PendingFleets);
        }

        [Fact]
        public void AnExpiredMixedGuard_CreditsEachMemberBack()
        {
            WorldModel w = TestSupport.World();
            var cat = new Cat(); cat.Dps["Vulture"] = 2500f; cat.Dps["Tanto"] = 300f; w.PrefabCatalogue = cat;
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            FactionTuning t = TestSupport.Tuning();
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            def.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "DEF", TargetTag = "DEF", TargetNodeId = "N002", OriginNodeId = "HOME",
                ShipPrefabId = "Vulture", ConvoySize = 3, Squadron = new List<string> { "Vulture", "Tanto", "Tanto" },
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now.AddHours(-2), EstimatedArrivalUtc = now.Subtract(t.GuardDuration).AddMinutes(-1),
                Resolved = true, FleetId = "GUARD1",
            });
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def }, w, t, new SeededRandom(1), log, null);

            Assert.Empty(def.PendingFleets);
            Assert.Equal(1, w.Graph.Nodes["N002"].ShipStock["Vulture"]);
            Assert.Equal(2, w.Graph.Nodes["N002"].ShipStock["Tanto"]);
            Assert.Contains(log, l => l.StartsWith("SHIP_RETURNED DEF prefab=Vulture") && l.Contains("count=1"));
            Assert.Contains(log, l => l.StartsWith("SHIP_RETURNED DEF prefab=Tanto") && l.Contains("count=2"));
        }

        private static (List<string> log, FactionState st) PatrolAfterAnAttack(TimeSpan ago, long attackerDps)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            var cat = new Cat();
            cat.Dps["Vulture"] = 2500f; cat.Grid["Vulture"] = "Large";
            cat.Dps["Tanto"] = 300f;    cat.Grid["Tanto"] = "Small";
            w.PrefabCatalogue = cat;
            FactionTuning t = Tuning();
            t.PatrolBurstSize = 3;
            t.CampaignCooldown = TimeSpan.FromHours(1);
            SpatialNode home = AddNode(w, "N001", "DEF", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            target.Threat = 0.8;
            target.HoldStrength = 0.5;
            w.Graph.Connect("N001", "N002");
            home.ShipStock["Vulture"] = 1; home.ShipStock["Tanto"] = 14;
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N002");
            FightLedger.Append(st, new FightRecord
            {
                Utc = w.UtcNow - ago, Kind = ActionKind.Raid, Attacking = false, OpponentTag = "RVN",
                NodeId = "N002", AttackerDps = attackerDps,
            }, 48);
            return (Run(w, t, st), st);
        }

        [Fact]
        public void APatrolIsSizedToWhatItsCircuitHasFaced()
        {
            // A 2,000-DPS raid hit N002 an hour ago. The Vulture alone meets it; posture would have
            // sent three hulls.
            var result = PatrolAfterAnAttack(TimeSpan.FromHours(1), 2000);

            string line = Assert.Single(result.log, l => l.Contains("PATROL_DISPATCH"));
            Assert.Contains("count=1", line);
            Assert.Contains("squadron=Vulture", line);
            Assert.Contains(" need=2000 dps=2500", line);
        }

        [Fact]
        public void AnAttackOlderThanTheMemory_IsForgotten()
        {
            var result = PatrolAfterAnAttack(TimeSpan.FromHours(7), 2000);

            string line = Assert.Single(result.log, l => l.Contains("PATROL_DISPATCH"));
            Assert.Contains("count=3", line);
            Assert.Contains(" need=0 ", line);
        }

        [Fact]
        public void APatrolIsMixedAndPostureSized()
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            var cat = new Cat();
            cat.Dps["Vulture"] = 2500f; cat.Grid["Vulture"] = "Large";
            cat.Dps["Tanto"] = 300f;    cat.Grid["Tanto"] = "Small";
            w.PrefabCatalogue = cat;
            FactionTuning t = Tuning();
            t.PatrolBurstSize = 3;
            t.CampaignCooldown = TimeSpan.FromHours(1);
            SpatialNode home = AddNode(w, "N001", "DEF", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            target.Threat = 0.8;
            target.HoldStrength = 0.5;
            w.Graph.Connect("N001", "N002");
            home.ShipStock["Vulture"] = 1; home.ShipStock["Tanto"] = 14;
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N002");

            List<string> log = Run(w, t, st);

            string line = Assert.Single(log, l => l.Contains("PATROL_DISPATCH"));
            Assert.Contains("count=3", line);
            Assert.Contains("squadron=Vulture,Tanto,Tanto", line);
            PendingFleetAction patrol = Assert.Single(st.PendingFleets);
            Assert.Equal(3, patrol.Squadron.Count);
            Assert.Equal(3100f, patrol.WeaponBands.Sum(b => b.Dps));
        }
    }
}
