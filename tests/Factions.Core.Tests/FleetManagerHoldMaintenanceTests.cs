using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    // ================================================================
    // Brain never emits Reinforce / Guard / Patrol — the BUG-003 contract.
    // ================================================================

    /// <summary>
    /// Verifies that the brain does NOT emit Reinforce, Guard, or Patrol under any drive.
    /// These tests intentionally FAIL against current main for Guard emission (Brain still emits
    /// Guard at lines 273 and 298 of FactionBrain.cs) — the test encodes the BUG-003 residual
    /// defect as a regression guard. Reinforce emission tests PASS because BUG-003 removed
    /// Reinforce from the Brain (33dc4b7).
    /// </summary>
    public class BrainNoGuardPatrolEmitTests
    {
        private static Personality Persona(double xeno, double exp = 0.5, double merc = 0.5, double aggr = 0.5)
            => new Personality
            {
                Temperature = 0.5,
                Traits    = new Traits { Xenophobia = xeno, Expansionism = exp, Mercantilism = merc, Aggression = aggr },
                Weights   = { [Drive.Security] = 1, [Drive.Growth] = 1, [Drive.Wealth] = 1,
                              [Drive.Rivalry] = 1, [Drive.Caution] = 1 }
            };

        // Step 1a: Brain emits Guard under Rivalry drive because FactionBrain.cs:273 still
        // creates Make(ActionKind.Guard, ...) in the Security/Rivalry block. EXPECTED: FAILS
        // against current main; PASSES after BUG-003 residual fix removes this emission.
        [Fact]
        public void Brain_NoGuard_OnRivalryDrive()
        {
            var brain = new FactionBrain();
            var tuning = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 300_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.4; // decaying — attracts Security attention
            var frontier = w.Graph.Add(new Vec3(100, 0, 0), 1.0, "P1");
            w.Graph.Connect("H", "P1");
            // Hostile presence to trigger Security drive from the threat
            frontier.Presences.Add(new Presence { ActorId = "R1", Kind = ActorKind.Player, Hostile = true });

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            var p = Persona(xeno: 0.9, aggr: 0.9);
            // Ensure enmity > 0 by adding a negative relation
            s.Relations["R1"] = -0.5;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), tuning);

            // Rivalry should be a driver (aggression 0.9 + hostile presence + enmity)
            Assert.True(s.Drives[Drive.Rivalry] > 0.15,
                $"Rivalry drive should be active; was {s.Drives[Drive.Rivalry]:F4}");

            // BUG-003 contract: Brain emits NO Guard candidate regardless of drive.
            var guardCandidates = d.Scored.Where(c => c.Intent.Kind == ActionKind.Guard).ToList();
            Assert.Empty(guardCandidates); // FAILS if Brain still emits Guard at line 273
        }

        // Step 1b: Brain emits Guard under Caution drive because FactionBrain.cs:298 still
        // creates Make(ActionKind.Guard, ...) in the Caution block. EXPECTED: FAILS against
        // current main; PASSES after BUG-003 residual fix.
        [Fact]
        public void Brain_NoGuard_OnCautionDrive()
        {
            var brain = new FactionBrain();
            var tuning = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 100, Ledger = 100, NetFlow = -50000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.9, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.9;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            var p = new Personality { Temperature = 0.0 };
            // All other drives suppressed — Caution dominates because treasury is near zero
            p.Weights[Drive.Security]    = 0.01;
            p.Weights[Drive.Growth]      = 0.01;
            p.Weights[Drive.Wealth]      = 0.01;
            p.Weights[Drive.Rivalry]     = 0.01;
            p.Weights[Drive.Caution]     = 1.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), tuning);

            Assert.True(s.Drives[Drive.Caution] > 0.2,
                $"Caution drive should be active; was {s.Drives[Drive.Caution]:F4}");

            var guardCandidates = d.Scored.Where(c => c.Intent.Kind == ActionKind.Guard).ToList();
            Assert.Empty(guardCandidates); // FAILS if Brain still emits Guard at line 298
        }

        // Step 1c: Brain does NOT emit Reinforce (sanity-check — post-BUG-003). EXPECTED: PASSES.
        [Fact]
        public void Brain_NoReinforce_Emit()
        {
            var brain = new FactionBrain();
            var tuning = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 300_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.3; // decaying
            var frontier = w.Graph.Add(new Vec3(100, 0, 0), 1.0, "P1");
            frontier.Presences.Add(new Presence { ActorId = "R1", Kind = ActorKind.Player, Hostile = true });
            w.Graph.Connect("H", "P1");

            var s = new FactionState { Tag = "F", RngSeed = 7 };
            var p = Persona(0.5, aggr: 0.9);
            Decision d = brain.Think(s, p, w, new SeededRandom(7), tuning);

            var reinforceCandidates = d.Scored.Where(c => c.Intent.Kind == ActionKind.Reinforce).ToList();
            Assert.Empty(reinforceCandidates);
        }

        // Step 1d: Brain MAY still emit Campaign under Security drive. EXPECTED: PASSES.
        [Fact]
        public void Brain_MayStillEmitCampaign()
        {
            var brain = new FactionBrain();
            var tuning = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 2_000_000, NetFlow = 5000 };
            var n0 = w.Graph.Add(new Vec3(200, 0, 0), 0.3, "N001");
            n0.OwnerFactionTag = "F";
            n0.HoldStrength = 0.5;
            var n1 = w.Graph.Add(new Vec3(400, 0, 0), 0.9, "N002");
            n1.OwnerFactionTag = "F";
            n1.StableStationId = 0;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            var p = Persona(xeno: 0.5);
            Decision d = brain.Think(s, p, w, new SeededRandom(1), tuning);

            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Campaign);
        }

        // Step 1e: Brain emits NO Patrol under any drive. EXPECTED: PASSES.
        [Fact]
        public void Brain_NoPatrolUnderAnyDrive()
        {
            var brain = new FactionBrain();
            var tuning = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 300_000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.9;
            var frontier = w.Graph.Add(new Vec3(100, 0, 0), 1.0, "P1");
            w.Graph.Connect("H", "P1");

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.5), w, new SeededRandom(1), tuning);
            Assert.False(d.Scored.Any(c => c.Intent.Kind == ActionKind.Patrol));

            // Try Growth drive
            s = new FactionState { Tag = "F", RngSeed = 1 };
            d = brain.Think(s, Persona(0.0, exp: 1.0), w, new SeededRandom(1), tuning);
            Assert.False(d.Scored.Any(c => c.Intent.Kind == ActionKind.Patrol));

            // Try Caution drive
            var w2 = new FakeWorld { Treasury = 100, Ledger = 100, NetFlow = -50000 };
            var h2 = w2.Graph.Add(new Vec3(0, 0, 0), 0.9, "H2");
            h2.OwnerFactionTag = "F";
            s = new FactionState { Tag = "F", RngSeed = 1 };
            var pc = new Personality { Temperature = 0.0 };
            pc.Weights[Drive.Security]    = 0.01;
            pc.Weights[Drive.Growth]      = 0.01;
            pc.Weights[Drive.Wealth]      = 0.01;
            pc.Weights[Drive.Rivalry]     = 0.01;
            pc.Weights[Drive.Caution]     = 1.0;
            d = brain.Think(s, pc, w2, new SeededRandom(1), tuning);
            Assert.False(d.Scored.Any(c => c.Intent.Kind == ActionKind.Patrol));
        }
    }

    // ================================================================
    // FleetManager hold-maintenance threshold and behavior.
    // ================================================================

    /// <summary>
    /// Verifies FleetManager hold-maintenance triggers, thresholds, tuning usage, and priority.
    /// </summary>
    public class FleetManagerHoldMaintenanceTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            var n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionTuning FastFleetTuning()
        {
            var t = TestSupport.Tuning();
            t.PatrolCost = 1000;
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.1;
            t.FleetSpeedKmPerMin = 1200.0;
            t.CampaignCooldown = TimeSpan.FromHours(1);
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0; // disable spend-budget cap for these tests
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            // These fixtures are about the money gates and the ranking, so the two fleet-side
            // bounds are lifted out of the way: leaving them in would cap a budget test at three
            // dispatches and refuse a second tick against the same node.
            t.ReinforceMaxConcurrent = 1000;
            t.ReinforceNodeCooldown = TimeSpan.Zero;
            return t;
        }

        // ---- FAC-264: a player faction can hold the ground it took ---------------------------

        private static FactionState PlayerFaction(string tag = "EXP")
        {
            return new FactionState { Tag = tag, RngSeed = 1, IsPlayerFaction = true };
        }

        // The headline. A player opts a node in, and the upkeep runs while they are offline.
        [Fact]
        public void PlayerFaction_HoldsANodeItOptedIn()
        {
            WorldModel w = MakeWorld();
            w.SetTreasury("EXP", 1_000_000);
            FactionTuning t = FastFleetTuning();
            AddNode(w, "N130", "EXP", new Vec3(0, 0, 0), hold: 0.3, value: 0.7);
            ReinforceMissionSupport.Depot(w, "EXP");
            FactionState st = PlayerFaction();
            st.ReinforceOptIn["N130"] = 0;

            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);

            Assert.Contains("REINFORCE_DISPATCH cost=", string.Join("\n", log));
            // The mission has only been sent: the money is gone and the hold has not moved yet.
            Assert.Equal(0.3, w.Graph.Nodes["N130"].HoldStrength, 6);

            ReinforceMissionSupport.FlyFleets(w, t, states, log);
            Assert.True(w.Graph.Nodes["N130"].HoldStrength > 0.3);
        }

        // The negative control, and the reason the opt-in exists: a player faction is not simply
        // handed NPC upkeep across its whole estate. Ground it did not ask to hold, it does not hold.
        [Fact]
        public void PlayerFaction_DoesNotHoldANodeItDidNotOptIn()
        {
            WorldModel w = MakeWorld();
            w.SetTreasury("EXP", 1_000_000);
            FactionTuning t = FastFleetTuning();
            AddNode(w, "N130", "EXP", new Vec3(0, 0, 0), hold: 0.3, value: 0.7);
            FactionState st = PlayerFaction();          // no opt-in

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.DoesNotContain("REINFORCE_DISPATCH cost=", string.Join("\n", log));
            Assert.Equal(0.3, w.Graph.Nodes["N130"].HoldStrength);
        }

        // The trap this change had to avoid: the autonomy gate was a `continue` covering the WHOLE
        // faction loop, so lifting it wholesale would have switched Guard and Patrol dispatch on for
        // player factions too. Threat here is well over the patrol trigger and there is combat stock
        // and money — an NPC in this exact state dispatches. A player must not.
        [Fact]
        public void PlayerFaction_StillGetsNoPatrolOrGuard_OnlyUpkeep()
        {
            WorldModel w = MakeWorld();
            w.SetTreasury("EXP", 1_000_000);
            FactionTuning t = FastFleetTuning();
            SpatialNode n = AddNode(w, "N130", "EXP", new Vec3(0, 0, 0), hold: 0.3, value: 0.7);
            n.Threat = 0.95;                            // far above the patrol trigger
            ReinforceMissionSupport.Stock(w, n, 4);
            ReinforceMissionSupport.Depot(w, "EXP");
            FactionState st = PlayerFaction();
            st.ReinforceOptIn["N130"] = 0;

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            string joined = string.Join("\n", log);
            Assert.Contains("REINFORCE_DISPATCH cost=", joined);          // upkeep: yes
            Assert.DoesNotContain("PATROL_DISPATCH", joined);    // strategy: still theirs
            Assert.DoesNotContain("GUARD_DISPATCH", joined);
            Assert.DoesNotContain("CAMPAIGN", joined);
        }

        // Operator rule, 2026-09-05: a player's station is never "no worth". The mint model prunes
        // sterile ground for an NPC; a player's estate is exactly what they chose to hold.
        [Fact]
        public void PlayerFaction_HoldsASterileNode_WhereAnNpcWouldNot()
        {
            FactionTuning t = FastFleetTuning();

            WorldModel wp = MakeWorld();
            wp.SetTreasury("EXP", 1_000_000);
            // MintsNothing is ResourceValue <= 0 AND Richness <= 0 — both, so both are set.
            SpatialNode pn = AddNode(wp, "N130", "EXP", new Vec3(0, 0, 0), hold: 0.3, value: 0.0);
            pn.Richness = 0.0;
            ReinforceMissionSupport.Depot(wp, "EXP");
            FactionState player = PlayerFaction();
            player.ReinforceOptIn["N130"] = 0;
            var playerLog = new List<string>();
            new FleetManager().Tick(new[] { player }, wp, t, playerLog);

            WorldModel wn = MakeWorld();
            wn.SetTreasury("NPC", 1_000_000);
            SpatialNode nn = AddNode(wn, "N130", "NPC", new Vec3(0, 0, 0), hold: 0.3, value: 0.0);
            nn.Richness = 0.0;
            ReinforceMissionSupport.Depot(wn, "NPC");
            var npc = new FactionState { Tag = "NPC", RngSeed = 1 };
            npc.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = "N130", Score = 1.0 });
            var npcLog = new List<string>();
            new FleetManager().Tick(new[] { npc }, wn, t, npcLog);

            Assert.Contains("REINFORCE_DISPATCH cost=", string.Join("\n", playerLog));
            Assert.DoesNotContain("REINFORCE_DISPATCH cost=", string.Join("\n", npcLog));
            Assert.Contains("no_worth=1", string.Join("\n", npcLog));
        }

        // Rank decides the order the money is spent in, the same as it does for a brain ranking.
        [Fact]
        public void PlayerFaction_SpendsOnTheHigherRankedNodeFirst()
        {
            WorldModel w = MakeWorld();
            FactionTuning t = FastFleetTuning();
            w.SetTreasury("EXP", t.ReinforceCost);      // exactly one top-up
            AddNode(w, "N_low", "EXP", new Vec3(0, 0, 0), hold: 0.3, value: 0.7);
            AddNode(w, "N_high", "EXP", new Vec3(10, 0, 0), hold: 0.3, value: 0.7);
            ReinforceMissionSupport.Depot(w, "EXP");
            FactionState st = PlayerFaction();
            st.ReinforceOptIn["N_low"] = 1;
            st.ReinforceOptIn["N_high"] = 9;

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            string joined = string.Join("\n", log);
            Assert.Contains("N_high hold=", joined);
            Assert.DoesNotContain("N_low hold=", joined);
        }

        // A node lost to a raid keeps its instruction: upkeep pauses, and resumes on retaking it.
        // Losing ground should not also cost the player the order to hold it.
        [Fact]
        public void PlayerFaction_OptInSurvivesLosingAndRetakingTheNode()
        {
            WorldModel w = MakeWorld();
            w.SetTreasury("EXP", 1_000_000);
            FactionTuning t = FastFleetTuning();
            SpatialNode n = AddNode(w, "N130", "EXP", new Vec3(0, 0, 0), hold: 0.3, value: 0.7);
            ReinforceMissionSupport.Depot(w, "EXP");
            FactionState st = PlayerFaction();
            st.ReinforceOptIn["N130"] = 0;

            n.OwnerFactionTag = "SPRT";                 // lost
            var lostLog = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, lostLog);
            Assert.DoesNotContain("REINFORCE_DISPATCH cost=", string.Join("\n", lostLog));
            Assert.True(st.ReinforceOptIn.ContainsKey("N130"), "the instruction must outlive the node");

            n.OwnerFactionTag = "EXP";                  // retaken
            var backLog = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, backLog);
            Assert.Contains("REINFORCE_DISPATCH cost=", string.Join("\n", backLog));
        }

        // ---- FAC-264 slice 2: a faction can set how much of its own money upkeep may take ----

        private static List<string> RunWithBudget(FactionTuning t, long treasury, long? floorOverride,
                                                  params (string id, double hold)[] nodes)
        {
            WorldModel w = MakeWorld();
            w.SetTreasury("EXP", treasury);
            ReinforceMissionSupport.Depot(w, "EXP");
            var st = new FactionState { Tag = "EXP", RngSeed = 1, IsPlayerFaction = true };
            st.ReinforceBudgetFloorOverride = floorOverride;
            for (int i = 0; i < nodes.Length; i++)
            {
                AddNode(w, nodes[i].id, "EXP", new Vec3(i * 10, 0, 0), hold: nodes[i].hold, value: 0.7);
                st.ReinforceOptIn[nodes[i].id] = 10 - i;
            }
            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            return log;
        }

        // The point of the slice: the override binds, and it binds tighter than the world's setting.
        [Fact]
        public void ReinforceBudgetOverride_LimitsSpendBelowTheWorldDefault()
        {
            FactionTuning t = FastFleetTuning();
            t.ReinforceBudgetFloor = t.ReinforceCost * 2;   // world default affords two
            t.ReinforceMaxIncomeFractionPerWindow = 0;

            int onDefault = RunWithBudget(t, 1_000_000, null, ("N1", 0.2), ("N2", 0.2))
                            .Count(l => l.Contains("REINFORCE_DISPATCH cost="));
            int onOwn = RunWithBudget(t, 1_000_000, t.ReinforceCost, ("N1", 0.2), ("N2", 0.2))
                        .Count(l => l.Contains("REINFORCE_DISPATCH cost="));

            Assert.Equal(2, onDefault);
            Assert.Equal(1, onOwn);
        }

        [Fact]
        public void ReinforceBudgetOverride_UnsetReadsTheWorldTuning_AndClearingRestoresIt()
        {
            FactionTuning t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0.42;
            t.ReinforceBudgetFloor = 1234;
            t.ReinforceWealthFractionPerWindow = 0.05;
            var st = new FactionState { Tag = "EXP", IsPlayerFaction = true };

            Assert.Equal(0.42, st.ReinforceIncomeFraction(t));
            Assert.Equal(1234, st.ReinforceBudgetFloorFor(t));
            Assert.Equal(0.05, st.ReinforceWealthFraction(t));
            Assert.False(st.HasReinforceBudgetOverride);

            st.ReinforceIncomeFractionOverride = 0.9;
            Assert.Equal(0.9, st.ReinforceIncomeFraction(t));
            Assert.True(st.HasReinforceBudgetOverride);

            st.ReinforceIncomeFractionOverride = null;      // "default"
            Assert.Equal(0.42, st.ReinforceIncomeFraction(t));
            Assert.False(st.HasReinforceBudgetOverride);
        }

        // The premise the player-facing guard rests on, pinned here because the guard itself lives in
        // the host and cannot be unit-tested. A zero income fraction AND a zero floor mean UNCAPPED to
        // SpendBudget, not "spend nothing" — so a player typing 0 to stop spending would get the
        // opposite. If this ever changes, the refusal in SetReinforceBudgetForPlayer is wrong too.
        [Fact]
        public void SpendBudget_ZeroIncomeAndZeroFloor_MeansUncapped_NotZero()
        {
            var b = new SpendBudget();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.True(b.CanSpend("EXP", now, recentIncome: 0, cost: 1_000_000,
                                   incomeFraction: 0, floor: 0, windowSeconds: 60),
                        "both at zero is the uncapped case, which is why the verb refuses to set it");
            Assert.False(b.CanSpend("EXP", now, recentIncome: 0, cost: 1_000_000,
                                    incomeFraction: 0, floor: 1, windowSeconds: 60),
                         "a non-zero floor makes it a real budget again");
        }

        // ---- FAC-195: a reinforce has to buy enough to be worth its flat cost ----------------

        private static FactionTuning FillGateTuning(double step = 0.7, double fraction = 0.3)
        {
            FactionTuning t = FastFleetTuning();
            t.ReinforceStep = step;
            t.ReinforceMinFillFraction = fraction;
            return t;
        }

        private static List<string> RunReinforce(FactionTuning t, long treasury, params (string id, double hold)[] nodes)
        {
            WorldModel w = MakeWorld();
            w.SetTreasury("F", treasury);
            // A reinforce flies out of somewhere: an owned node holding warships that is not one of
            // the nodes under test, so the gate being measured is the only thing refusing anything.
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            for (int i = 0; i < nodes.Length; i++)
            {
                AddNode(w, nodes[i].id, "F", new Vec3(i * 10, 0, 0), hold: nodes[i].hold, value: 0.7);
                // Ranked in the order given, so a test can put the wasteful node FIRST.
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = nodes[i].id, Score = 1.0 - i * 0.01 });
            }
            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            return log;
        }

        // The measured waste: 50,000 for 0.01 of hold, because the gate was "any deficit at all".
        [Fact]
        public void Reinforce_SkipsANodeWhoseDeficitIsNotWorthTheFlatCost()
        {
            List<string> log = RunReinforce(FillGateTuning(), 10_000, ("N001", 0.99));
            Assert.DoesNotContain("REINFORCE_DISPATCH cost=", string.Join("\n", log));
        }

        [Fact]
        public void Reinforce_StillServesANodeWithARealDeficit()
        {
            List<string> log = RunReinforce(FillGateTuning(), 10_000, ("N001", 0.5));
            Assert.Contains("REINFORCE_DISPATCH cost=", string.Join("\n", log));
        }

        // The operator's actual requirement: the trigger is a share of what a reinforce DELIVERS,
        // not a second absolute threshold. Same hold, same fraction, two step sizes -> opposite
        // outcomes. A hardcoded 0.8 (or any constant) passes the two tests above and fails this one.
        [Fact]
        public void Reinforce_TriggerMovesWithReinforceStep_NotAFixedThreshold()
        {
            // deficit 0.25. Small step: 0.4 * 0.3 = 0.12, so 0.25 clears it -> reinforced.
            Assert.Contains("REINFORCE_DISPATCH cost=",
                string.Join("\n", RunReinforce(FillGateTuning(step: 0.4), 10_000, ("N001", 0.75))));
            // Big step: 1.0 * 0.3 = 0.30, so the same 0.25 deficit no longer earns the same cost.
            Assert.DoesNotContain("REINFORCE_DISPATCH cost=",
                string.Join("\n", RunReinforce(FillGateTuning(step: 1.0), 10_000, ("N001", 0.75))));
        }

        // The escape hatch, and the control: with the fraction off, the node the first test refused
        // is reinforced again. Without this, "skipped" could be any unrelated gate refusing it.
        [Fact]
        public void Reinforce_FractionAtZero_RestoresReinforcingAnythingBelowFull()
        {
            List<string> log = RunReinforce(FillGateTuning(fraction: 0), 10_000, ("N001", 0.99));
            Assert.Contains("REINFORCE_DISPATCH cost=", string.Join("\n", log));
        }

        // FAC-195's central claim, and the reason this is worth shipping: the freed budget is not
        // saved, it moves down the ranking to a node that needs it. The wasteful node is ranked
        // FIRST and the treasury holds exactly one reinforce, so under the old gate it would take
        // the money and the node at 0.4 would get nothing.
        [Fact]
        public void Reinforce_BudgetIsRedirectedToTheNodeThatNeedsIt_NotSaved()
        {
            FactionTuning t = FillGateTuning();
            List<string> log = RunReinforce(t, t.ReinforceCost, ("N_rich", 0.99), ("N_poor", 0.4));

            string joined = string.Join("\n", log);
            Assert.Contains("REINFORCE_DISPATCH cost=" + t.ReinforceCost, joined);
            Assert.Contains("-> N_poor hold=", joined);
            Assert.DoesNotContain("-> N_rich hold=", joined);
        }

        // Step 2a: current hold maintenance still uses the hard cutoff, but BUG-010 will replace
        // it with brain-ranked reinforcement and remove the 0.8 eligibility stop in FleetManager.
        [Fact]
        public void Hold_ReinforceTriggersBelowBacklogThreshold()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.79, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            // The dispatch verb, not a bare "REINFORCE": REINFORCE_SKIPPED carries that substring
            // too, so the looser assertion would pass on the failure it is here to catch.
            Assert.True(log.Any(l => l.Contains("REINFORCE_DISPATCH")),
                "This is the pre-BUG-010 baseline; the planned change will preserve reinforcement while switching to ranked priority.");
        }

        // Step 2b: Hold at the maintenance threshold remains eligible because the hard-stop is gone.
        [Fact]
        public void Hold_ReinforcesAtMaintenanceThreshold()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.80, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);

            Assert.Contains(log, l => l.Contains("REINFORCE_DISPATCH") && l.Contains(home.Id));
            Assert.Equal(0.80, home.HoldStrength, 6);        // dispatched, not yet delivered
            ReinforceMissionSupport.FlyFleets(w, t, states, log);
            Assert.Equal(0.9, home.HoldStrength, 6);
        }

        // Step 2c: Threshold gap detection — hold=0.60 exposed the defect.
        // Current code (0.6f): 0.60 < 0.60 → FALSE → no trigger → test FAILS.
        // Backlog code (0.8): 0.60 < 0.8 → TRUE → triggers → test PASSES.
        [Fact]
        public void Hold_FailsAtThresholdPoint()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            // hold exactly at boundary
            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.60, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            // Current code: 0.60 < 0.60 → FALSE → no REINFORCE → this Assert FAILS (correct)
            // Backlog fix: 0.60 < 0.8 → TRUE → triggers → PASSES
            Assert.True(log.Any(l => l.Contains("REINFORCE_DISPATCH")),
                "Should trigger REINFORCE at hold=0.60 to detect the BUG-003 residual threshold gap (0.6 vs 0.8)");
        }

        // Step 2d: FleetManager reinforces all below-threshold owned nodes in worst-first order.
        [Fact]
        public void Hold_ReinforcesAllBelowThresholdNodesWorstFirst()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            var n001 = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            var n002 = AddNode(w, "N002", "F", new Vec3(100, 0, 0), hold: 0.3, value: 0.6);
            var n003 = AddNode(w, "N003", "F", new Vec3(200, 0, 0), hold: 0.79, value: 0.5);
            w.Graph.Connect("N001", "N002");
            w.Graph.Connect("N002", "N003");

            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n002.Id, Score = 3.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n001.Id, Score = 2.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n003.Id, Score = 1.0 });
            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);

            var reinforceLogs = log.Where(l => l.Contains("REINFORCE_DISPATCH")).ToList();
            ReinforceMissionSupport.FlyFleets(w, t, states, log, legs: 16);

            Assert.Equal(3, reinforceLogs.Count);
            Assert.Contains(n002.Id, reinforceLogs[0]);
            Assert.Contains(n001.Id, reinforceLogs[1]);
            Assert.Contains(n003.Id, reinforceLogs[2]);
            Assert.Equal(0.4, n002.HoldStrength, 6);
            Assert.Equal(0.6, n001.HoldStrength, 6);
            Assert.Equal(0.89, n003.HoldStrength, 6);
            Assert.Equal(10_000 - (3 * t.ReinforceCost), w.GetTreasury("F"));
            Assert.False(st.Cooldowns.ContainsKey(ActionKind.Reinforce));
        }

        // Step 2e: Reinforce uses tuning.ReinforceStep, not a hardcoded value.
        [Fact]
        public void Hold_ReinforceIncreasesByTuningStepNotHardcoded()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            // FastFleetTuning() defaults ReinforceStep=0.1; override to 0.4 explicitly
            t.ReinforceStep = 0.4;

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var originalHold = home.HoldStrength;
            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);
            ReinforceMissionSupport.FlyFleets(w, t, states, log);

            Assert.True(log.Any(l => l.Contains("REINFORCE_DISPATCH")));
            // Clamp01(0.5 + 0.4) = Clamp01(0.9) = 0.9
            Assert.Equal(0.9, home.HoldStrength);
        }

        // Step 2f: Reinforce clamp at 1.0 — hold doesn't exceed max regardless of step size.
        [Fact]
        public void Hold_ReinforceClampedAt1_0()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            t.ReinforceStep = 1.0;

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.6, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);
            ReinforceMissionSupport.FlyFleets(w, t, states, log);

            Assert.Equal(1.0, home.HoldStrength);
        }

        // Step 2g: Treasury=0 gates Reinforce (FleetManager.cs:114).
        [Fact]
        public void Hold_NoReinforceWhenTreasuryZero()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 0);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.DoesNotContain("REINFORCE_DISPATCH cost=", string.Join("\n", log));
        }

        [Fact]
        public void Hold_ReinforceDeductsCost()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", 10_000);

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("REINFORCE_DISPATCH") && l.Contains("cost=" + t.ReinforceCost)));
            Assert.Equal(10_000 - t.ReinforceCost, w.GetTreasury("F"));
        }

        [Fact]
        public void Hold_ReinforceCost_FeedsNetFlowOnNextAccrueIncome()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0; // isolate: no CB grant income in net flow
            w.SetTreasury("F", 10_000);

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());
            w.AccrueIncome(new[] { st });

            // AddNode has no StableStationId, so no upkeep applies here — the reinforce cost is the
            // only component.
            Assert.Equal(-t.ReinforceCost, w.GetNetFlow("F"));
        }

        [Fact]
        public void Hold_ReinforceStopsWhenTreasuryCannotAffordAnotherNode()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", t.ReinforceCost * 2);

            var n001 = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            var n002 = AddNode(w, "N002", "F", new Vec3(100, 0, 0), hold: 0.3, value: 0.6);
            var n003 = AddNode(w, "N003", "F", new Vec3(200, 0, 0), hold: 0.2, value: 0.5);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n003.Id, Score = 3.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n002.Id, Score = 2.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n001.Id, Score = 1.0 });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);

            var reinforceLogs = log.Where(l => l.Contains("REINFORCE_DISPATCH cost=")).ToList();
            ReinforceMissionSupport.FlyFleets(w, t, states, log, legs: 16);

            Assert.Equal(2, reinforceLogs.Count);
            Assert.Contains(n003.Id, reinforceLogs[0]);
            Assert.Contains(n002.Id, reinforceLogs[1]);
            Assert.Equal(0, w.GetTreasury("F"));
            Assert.Equal(0.5, n001.HoldStrength, 6);
            Assert.Equal(0.4, n002.HoldStrength, 6);
            Assert.Equal(0.3, n003.HoldStrength, 6);
            Assert.Contains(log, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_money=1"));
            Assert.False(st.Cooldowns.ContainsKey(ActionKind.Reinforce));
        }

        [Fact]
        public void Hold_ReinforceNoCostWhenInsufficientTreasury()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", t.ReinforceCost - 1);

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var originalHold = home.HoldStrength;
            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Equal(t.ReinforceCost - 1, w.GetTreasury("F"));
            Assert.Equal(originalHold, home.HoldStrength);
            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_DISPATCH cost="));
            Assert.Contains(log, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_money=1"));
        }

        [Fact]
        public void Hold_ReinforceSkipSummaryOmittedWhenOnlyFullHoldNodesSkipped()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", 10_000);

            var full = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 1.0, value: 0.9);
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = full.Id, Score = 10.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_SKIPPED"));
        }

        [Fact]
        public void Hold_ReinforceSkipSummaryIsLogOnChangeNotEveryTick()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", t.ReinforceCost - 3);

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var fm = new FleetManager();

            var firstTick = new List<string>();
            fm.Tick(new[] { st }, w, t, firstTick);
            Assert.Contains(firstTick, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_money=1"));

            // Nothing changed (still same treasury, same node state) — repeat tick must stay silent.
            var secondTick = new List<string>();
            fm.Tick(new[] { st }, w, t, secondTick);
            Assert.DoesNotContain(secondTick, l => l.Contains("REINFORCE_SKIPPED"));

            // Treasury changes but is still short of ReinforceCost — summary should re-fire once.
            w.AddTreasury("F", 1);
            var thirdTick = new List<string>();
            fm.Tick(new[] { st }, w, t, thirdTick);
            Assert.Contains(thirdTick, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_money=1"));
        }

        [Fact]
        public void Hold_RankedPriorityControlsTopKReinforcementOrder()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", t.ReinforceCost * 2);

            var lowValueWorstHold = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.2, value: 0.1);
            var highValueHealthy = AddNode(w, "N002", "F", new Vec3(100, 0, 0), hold: 0.75, value: 0.95);
            highValueHealthy.Capabilities[NodeCapabilityKind.Shipyard] = 2;
            var medium = AddNode(w, "N003", "F", new Vec3(200, 0, 0), hold: 0.4, value: 0.5);
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = highValueHealthy.Id, Score = 30.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = medium.Id, Score = 20.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = lowValueWorstHold.Id, Score = 10.0 });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);

            var reinforceLogs = log.Where(l => l.Contains("REINFORCE_DISPATCH cost=")).ToList();
            ReinforceMissionSupport.FlyFleets(w, t, states, log, legs: 16);
            Assert.Equal(2, reinforceLogs.Count);
            Assert.Contains(highValueHealthy.Id, reinforceLogs[0]);
            Assert.Contains(medium.Id, reinforceLogs[1]);
            Assert.Equal(0.85, highValueHealthy.HoldStrength, 6);
            Assert.Equal(0.5, medium.HoldStrength, 6);
            Assert.Equal(0.2, lowValueWorstHold.HoldStrength, 6);
        }

        [Fact]
        public void Hold_NodeAtFullCapIsNoOpWithoutSpend()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", 10_000);

            var full = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 1.0, value: 0.9);
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = full.Id, Score = 10.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Equal(1.0, full.HoldStrength, 6);
            Assert.Equal(10_000, w.GetTreasury("F"));
            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_DISPATCH cost="));
        }

        // Core regression guard for the income term: with the wealth term disabled, budget is
        // anchored to CentralBankRecentIncome alone — a faction sitting on a big treasury but
        // earning nothing gets only the floor. (Treasury above the operational reserve is a
        // separate, deliberate channel — see the WealthTerm facts below.)
        [Fact]
        public void Hold_WealthyZeroIncomeFaction_GetsOnlyTheFloor()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0.3;
            t.ReinforceBudgetFloor = 500; // = one ReinforceCost
            t.ReinforceWealthFractionPerWindow = 0; // isolate the income term
            w.SetTreasury("F", 1_000_000); // big treasury, must not matter with the wealth term off
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            for (int i = 0; i < 5; i++)
            {
                var n = AddNode(w, "N" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 5.0 - i });
            }

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            // budget = 0*0.3 + 500 = 500 -> exactly one reinforcement, regardless of the $1M treasury.
            Assert.Equal(1, log.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));
            Assert.True(log.Any(l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_budget=4") && l.Contains("no_money=0")));
        }

        // ---- wealth term: treasury surplus above the operational reserve funds maintenance ------

        [Fact]
        public void Hold_WealthTerm_SurplusAboveReserve_ClearsWholeBacklogInOneTick()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0.3;
            t.ReinforceBudgetFloor = 500;
            t.ReinforceWealthFractionPerWindow = 0.001;
            // ReserveTreasury is 50M from tuning_defaults.json: 10M surplus * 0.001 = 10_000 budget
            w.SetTreasury("F", 60_000_000);
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            for (int i = 0; i < 5; i++)
            {
                var n = AddNode(w, "N" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 5.0 - i });
            }

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            // budget = 0*0.3 + 10M*0.001 + 500 = 10_500 -> all five 500-cost reinforcements fit.
            Assert.Equal(5, log.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));
            Assert.False(log.Any(l => l.Contains("REINFORCE_SKIPPED")));
        }

        [Fact]
        public void Hold_WealthTerm_TreasuryBelowReserve_StaysOnIncomeOnlyBudget()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0.3;
            t.ReinforceBudgetFloor = 500;
            t.ReinforceWealthFractionPerWindow = 0.001;
            w.SetTreasury("F", 30_000_000); // below the 50M ReserveTreasury -> surplus 0
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            for (int i = 0; i < 5; i++)
            {
                var n = AddNode(w, "N" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 5.0 - i });
            }

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            // wealth term inert below the reserve: floor only, exactly one reinforcement.
            Assert.Equal(1, log.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));
            Assert.True(log.Any(l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_budget=4")));
        }

        [Fact]
        public void Hold_HighIncomeFaction_GetsBiggerBudgetThanFloorAlone()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0.3;
            t.ReinforceBudgetFloor = 500;
            w.SetTreasury("F", 3_000); // modest treasury, but enough to actually spend the bigger budget
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 5_000 };
            for (int i = 0; i < 5; i++)
            {
                var n = AddNode(w, "N" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 5.0 - i });
            }

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            // budget = 5_000*0.3 + 500 = 2_000 -> four reinforcements (4*500<=2_000; a fifth would be 2_500).
            Assert.Equal(4, log.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));
            Assert.Equal(1_000, w.GetTreasury("F"));
            Assert.True(log.Any(l => l.Contains("REINFORCE_SKIPPED") && l.Contains("no_budget=1") && l.Contains("no_money=0")));
        }

        [Fact]
        public void Hold_FloorAloneGuaranteesOneSpendAtZeroIncome()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0; // isolate: floor only
            t.ReinforceBudgetFloor = 500;
            w.SetTreasury("F", 10_000);

            var n = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.5);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 1.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("REINFORCE_DISPATCH cost=")),
                "the floor must guarantee at least one spend per window even at zero income");
            Assert.Equal(9_500, w.GetTreasury("F"));
            Assert.DoesNotContain(log, l => l.Contains("no_budget"));
        }

        [Fact]
        public void Hold_BudgetCapDoesNotRearmWithinWindow()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0;
            t.ReinforceBudgetFloor = 500;
            w.SetTreasury("F", 10_000);
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            for (int i = 0; i < 10; i++)
            {
                var n = AddNode(w, "N" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 10.0 - i });
            }

            var fm = new FleetManager();
            var log1 = new List<string>();
            fm.Tick(new[] { st }, w, t, log1);
            Assert.Equal(1, log1.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));

            // Same window (1s later, window is 30s) — the backlog is still there but the budget
            // must not reset just because Tick() ran again.
            w.UtcNow = w.UtcNow.AddSeconds(1);
            var log2 = new List<string>();
            fm.Tick(new[] { st }, w, t, log2);
            Assert.Equal(0, log2.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));
        }

        [Fact]
        public void Hold_BudgetCapRearmsAfterWindow()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0;
            t.ReinforceBudgetFloor = 500;
            w.SetTreasury("F", 10_000);
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            for (int i = 0; i < 10; i++)
            {
                var n = AddNode(w, "N" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 10.0 - i });
            }

            var fm = new FleetManager();
            var log1 = new List<string>();
            fm.Tick(new[] { st }, w, t, log1);
            Assert.Equal(1, log1.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));

            w.UtcNow = w.UtcNow.AddSeconds(t.SpendBudgetWindowSeconds + 1);
            var log2 = new List<string>();
            fm.Tick(new[] { st }, w, t, log2);
            Assert.True(log2.Count(l => l.Contains("REINFORCE_DISPATCH cost=")) > 0,
                "spending should resume once a new window starts");
        }

        [Fact]
        public void Hold_BudgetSkipDistinctFromNoMoney()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0.3;
            t.ReinforceBudgetFloor = 500;

            // Case A: plenty of treasury and some income, but budget-limited. Exactly 2 needy nodes so
            // the budget (one spend's worth) blocks exactly one of them, not more.
            w.SetTreasury("F", 10_000);
            ReinforceMissionSupport.Depot(w, "F");
            var stA = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 1_000 };
            for (int i = 0; i < 2; i++)
            {
                var n = AddNode(w, "A" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                stA.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 2.0 - i });
            }
            var logA = new List<string>();
            new FleetManager().Tick(new[] { stA }, w, t, logA);
            // budget = 1_000*0.3 + 500 = 800 -> one spend fits (500<=800), a second doesn't (1000>800).
            Assert.True(logA.Any(l => l.Contains("no_budget=1") && l.Contains("no_money=0")));

            // Case B: treasury below ReinforceCost outright — budget is irrelevant here.
            var w2 = MakeWorld(2);
            w2.SetTreasury("G", 100);
            ReinforceMissionSupport.Depot(w2, "G");
            var stB = new FactionState { Tag = "G", RngSeed = 1 };
            var nb = AddNode(w2, "B0", "G", new Vec3(0, 0, 0), hold: 0.5, value: 0.5);
            stB.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = nb.Id, Score = 1.0 });
            var logB = new List<string>();
            new FleetManager().Tick(new[] { stB }, w2, t, logB);
            Assert.True(logB.Any(l => l.Contains("no_money=1") && l.Contains("no_budget=0")));
        }

        [Fact]
        public void Hold_BudgetUncappedWhenBothFractionAndFloorAreZero()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0;
            t.ReinforceBudgetFloor = 0;
            w.SetTreasury("F", 10_000);
            ReinforceMissionSupport.Depot(w, "F");

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            for (int i = 0; i < 10; i++)
            {
                var n = AddNode(w, "N" + i, "F", new Vec3(i * 10, 0, 0), hold: 0.5, value: 0.5);
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 10.0 - i });
            }

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Equal(10, log.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));
        }

        [Fact]
        public void Hold_BudgetStillAppliesWhenOnlyOneOfFractionOrFloorIsZero()
        {
            // fraction=0, floor>0: still capped, at exactly the floor.
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.ReinforceMaxIncomeFractionPerWindow = 0;
            t.ReinforceBudgetFloor = 500;
            w.SetTreasury("F", 10_000);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 999_999 }; // income must not matter
            var n = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.5);
            var n2 = AddNode(w, "N002", "F", new Vec3(10, 0, 0), hold: 0.5, value: 0.5);
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n.Id, Score = 2.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = n2.Id, Score = 1.0 });
            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);
            Assert.Equal(1, log.Count(l => l.Contains("REINFORCE_DISPATCH cost=")));

            // fraction>0, floor=0, zero income: fully frozen (no guarantee-first without the floor).
            var w2 = MakeWorld(2);
            var t2 = FastFleetTuning();
            t2.ReinforceMaxIncomeFractionPerWindow = 0.3;
            t2.ReinforceBudgetFloor = 0;
            w2.SetTreasury("G", 10_000);
            ReinforceMissionSupport.Depot(w2, "G");
            var stG = new FactionState { Tag = "G", RngSeed = 1, CentralBankRecentIncome = 0 };
            var ng = AddNode(w2, "G001", "G", new Vec3(0, 0, 0), hold: 0.5, value: 0.5);
            stG.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = ng.Id, Score = 1.0 });
            var log2 = new List<string>();
            new FleetManager().Tick(new[] { stG }, w2, t2, log2);
            Assert.DoesNotContain(log2, l => l.Contains("REINFORCE_DISPATCH cost="));
            Assert.True(log2.Any(l => l.Contains("no_budget=1")));
        }
    }

    // ================================================================
    // FleetManager hold maintenance is independent of Campaign focus.
    // ================================================================

    /// <summary>
    /// Verifies that FleetManager hold maintenance fires regardless of st.CampaignFocusRegion.
    /// The hold-decay block at FleetManager.cs lines 95-122 runs BEFORE the CampaignFocusRegion
    /// null-check at line 127, so it MUST fire unconditionally.
    /// </summary>
    public class FleetManagerCampaignIndependenceTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            var n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionTuning FastFleetTuning()
        {
            var t = TestSupport.Tuning();
            t.PatrolCost = 1000;
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.1;
            t.FleetSpeedKmPerMin = 1200.0;
            t.CampaignCooldown = TimeSpan.FromHours(1);
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0; // disable spend-budget cap for these tests
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            // These fixtures are about the money gates and the ranking, so the two fleet-side
            // bounds are lifted out of the way: leaving them in would cap a budget test at three
            // dispatches and refuse a second tick against the same node.
            t.ReinforceMaxConcurrent = 1000;
            t.ReinforceNodeCooldown = TimeSpan.Zero;
            return t;
        }

        // Step 3/6: REINFORCE fires even when CampaignFocusRegion is null.
        [Fact]
        public void Hold_ReinforceRunsUnconditionally()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), hold: 0.5, value: 0.7);
            ReinforceMissionSupport.Depot(w, "F");
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });
            // DO NOT set st.CampaignFocusRegion — verify it still fires
            Assert.Null(st.CampaignFocusRegion);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("REINFORCE_DISPATCH")),
                "REINFORCE should fire regardless of CampaignFocusRegion");
        }
    }

    // ================================================================
    // Guard / Raid interception — active vs expired semantics.
    // ================================================================
    // All Guard PendingFleetAction setup uses exact field values from Domain.cs:384-504
    // (PendingFleetAction struct) and FleetResolution.cs:322, 501-504 (IsActiveGuard).

    public class GuardRaidInterceptTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            var n = w.Graph.Add(pos, value, id);
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

        private static IRandom AlwaysWin() => new FixedRandom(0.0);
        private static IRandom AlwaysLose() => new FixedRandom(1.0);

        private static FactionTuning GetTuning() => TestSupport.Tuning();

        // Step 4a: Active guard intercepts incoming Raid — hold unchanged.
        [Fact]
        public void ActiveGuardInterceptsRaid()
        {
            var w = MakeWorld();
            w.SetTreasury("MERC", 10_000);
            var t = GetTuning();

            var n = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            var initialHold = n.HoldStrength;

            var stRaider = new FactionState { Tag = "RVN", RngSeed = 1 };
            var stDefender = new FactionState { Tag = "MERC", RngSeed = 1 };

            DateTime now = w.UtcNow;
            // Guard has arrived 5 min ago — fully active, and heavily outguns the unarmed raid so
            // the duel outcome is decisive regardless of jitter.
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = "MERC",
                TargetTag = "NONE",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now.AddMinutes(-5),
                EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true,
                Intercepted = false,
                LastResolvedSegment = -1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 1000, RangeM = 2000 } },
            });
            // Raid arrives this tick — immediate fast-path
            stRaider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RVN",
                TargetTag = "MERC",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now,
                EstimatedArrivalUtc = now,
                CargoValue = 0,
                Resolved = false,
                Intercepted = false,
                LastResolvedSegment = -1,
            });

            var states = new List<FactionState> { stRaider, stDefender };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, t, AlwaysWin(), log, null);

            Assert.Equal(initialHold, n.HoldStrength, 6);
            Assert.True(log.Any(l => l.Contains("GUARD_INTERCEPT")));
        }

        // Step 4b: Expired guard does NOT intercept — Raid proceeds.
        [Fact]
        public void ExpiredGuardDoesNotInterceptRaid()
        {
            var w = MakeWorld();
            w.SetTreasury("MERC", 10_000);
            var t = GetTuning();

            var n = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            var initialHold = n.HoldStrength;

            var stRaider = new FactionState { Tag = "RVN", RngSeed = 1 };
            var stDefender = new FactionState { Tag = "MERC", RngSeed = 1 };

            DateTime now = w.UtcNow;
            long guardDurationMs = (long)t.GuardDuration.Milliseconds;
            // Guard expired: now > EstimatedArrivalUtc + GuardDuration
            DateTime guardExpiredAt = now.AddMilliseconds(-1); // effectively expired
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = "MERC",
                TargetTag = "NONE",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = guardExpiredAt,
                EstimatedArrivalUtc = now.AddMinutes(-90),
                Resolved = true,
                Intercepted = false,
                LastResolvedSegment = -1,
            });

            stRaider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RVN",
                TargetTag = "MERC",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now,
                EstimatedArrivalUtc = now,
                CargoValue = 0,
                Resolved = false,
                Intercepted = false,
                LastResolvedSegment = -1,
            });

            var states = new List<FactionState> { stRaider, stDefender };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, t, AlwaysWin(), log, null);

            Assert.True(n.HoldStrength < initialHold, "Raid should deal damage when guard is expired");
        }

        // Step 4c: Pending guard not yet arrived does NOT intercept.
        [Fact]
        public void PendingGuardNotYetArrivedDoesNotIntercept()
        {
            var w = MakeWorld();
            w.SetTreasury("MERC", 10_000);
            var t = GetTuning();

            var n = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            var initialHold = n.HoldStrength;

            var stRaider = new FactionState { Tag = "RVN", RngSeed = 1 };
            var stDefender = new FactionState { Tag = "MERC", RngSeed = 1 };

            DateTime now = w.UtcNow;
            // Guard arrives 10 minutes from now — not yet arrived
            var guardArrival = now.AddMinutes(10);
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = "MERC",
                TargetTag = "NONE",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now.AddMinutes(-10),
                EstimatedArrivalUtc = guardArrival,
                Resolved = true,  // effects applied at arrival
                Intercepted = false,
                LastResolvedSegment = -1,
            });

            stRaider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RVN",
                TargetTag = "MERC",
                TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now,
                EstimatedArrivalUtc = now,
                CargoValue = 0,
                Resolved = false,
                Intercepted = false,
                LastResolvedSegment = -1,
            });

            var states = new List<FactionState> { stRaider, stDefender };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, t, AlwaysWin(), log, null);

            Assert.True(n.HoldStrength < initialHold, "guard has not yet arrived, so raid should deal damage");
        }

        // Step 4d: Guard intercept PREVENTS Raid damage — compare unguarded baseline.
        [Fact]
        public void GuardInterceptPreventsRaidDamageToHold()
        {
            // Unguarded baseline
            var wBase = MakeWorld();
            wBase.SetTreasury("MERC", 10_000);
            var nBase = AddNode(wBase, "BASE1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            var stBaseRaider = new FactionState { Tag = "RVN", RngSeed = 42 };
            var now = wBase.UtcNow;
            stBaseRaider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "BASE1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 0
            });
            PendingFleetResolver.ResolveArrivals(new[] { stBaseRaider }, wBase, GetTuning(), AlwaysWin(), new List<string>(), null);
            double damagedHold = nBase.HoldStrength;
            Assert.True(damagedHold < 0.8, "Raid should damage unguarded hold");

            // Guarded scenario — same raid params
            var wGuarded = MakeWorld();
            wGuarded.SetTreasury("MERC", 10_000);
            var t = GetTuning();
            var nGuarded = AddNode(wGuarded, "SAFE1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            var stRaider2 = new FactionState { Tag = "RVN", RngSeed = 42 };
            var stDefender9 = new FactionState { Tag = "MERC", RngSeed = 1 };
            var now9 = wGuarded.UtcNow;

            stDefender9.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetTag = "NONE", TargetNodeId = "SAFE1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now9.AddMinutes(-5), EstimatedArrivalUtc = now9.AddMinutes(-5),
                Resolved = true, Intercepted = false, LastResolvedSegment = -1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 1000, RangeM = 2000 } },
            });
            stRaider2.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "SAFE1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now9, EstimatedArrivalUtc = now9, CargoValue = 0
            });

            PendingFleetResolver.ResolveArrivals(new[] { stRaider2, stDefender9 }, wGuarded, t, AlwaysWin(), new List<string>(), null);

            // Guarded hold should remain at 0.8 while unguarded is damaged
            Assert.Equal(0.8, nGuarded.HoldStrength, 6);
            Assert.True(nGuarded.HoldStrength > damagedHold,
                "Guarded hold should be higher than damaged unguarded hold");
        }
    }

    // ================================================================
    // Patrol behavior tests — FleetManager operational dispatch.
    // ================================================================

    /// <summary>
    /// Patrol is FleetManager operational only (not Brain). Tests verify actual dispatch
    //  behavior against FleetManager.cs lines 157-212. PatrolCost defaults to 5000 in tuning.
    /// FleetManagerTests.cs FastFleetTuning overrides PatrolCost to 1000.
    /// </summary>
    public class PatrolBehaviorTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            var n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionTuning FastFleetTuning()
        {
            var t = TestSupport.Tuning();
            t.PatrolCost = 1000;
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.1;
            t.FleetSpeedKmPerMin = 1200.0;
            t.CampaignCooldown = TimeSpan.FromHours(1);
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0; // disable spend-budget cap for these tests
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            // These fixtures are about the money gates and the ranking, so the two fleet-side
            // bounds are lifted out of the way: leaving them in would cap a budget test at three
            // dispatches and refuse a second tick against the same node.
            t.ReinforceMaxConcurrent = 1000;
            t.ReinforceNodeCooldown = TimeSpan.Zero;
            return t;
        }

        /// <summary>Ship stock is unified and tag-based now (see ShipPool) — dispatch requires a
        /// prefab the catalogue actually classifies as combat-suitable, not just a nonzero count
        /// under the right role key.</summary>
        private sealed class FakePrefabCatalogue : IPrefabCatalogue
        {
            public readonly HashSet<string> ArmedPrefabs = new HashSet<string>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
                => ArmedPrefabs.Contains(prefabId) ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static void StockPatrolShips(WorldModel w, SpatialNode node, int count, string prefabId = "PatrolCargoship")
        {
            if (count > 0) node.ShipStock[prefabId] = count;
            var cat = w.PrefabCatalogue as FakePrefabCatalogue;
            if (cat == null) w.PrefabCatalogue = cat = new FakePrefabCatalogue();
            cat.ArmedPrefabs.Add(prefabId);
        }

        // Step 5a: Patrol dispatched when threat > 0.5 and patrol stock > 0.
        [Fact]
        public void PatrolDispatchedWhenConditionsMet()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.8; // above 0.5 threshold

            StockPatrolShips(w, home, 1);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threat.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("PATROL_DISPATCH")),
                "Patrol should dispatch when threat > 0.5 and patrol stock is available");
        }

        // Step 5: Patrol deducts tuning.PatrolCost from treasury (using FastFleetTuning
        // override of PatrolCost=1000).
        [Fact]
        public void PatrolUsesTuningCost()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", 50_000);

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.8;

            StockPatrolShips(w, home, 1);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threat.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var treasBefore = w.GetTreasury("F");
            var cost = t.PatrolCost; // PatrolCost=1000 from FastFleetTuning override
            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("PATROL_DISPATCH")));
            Assert.Equal(treasBefore - cost, w.GetTreasury("F"));
        }

        // Step 5b: Patrol requires Patrol stock; zero stock = no dispatch.
        [Fact]
        public void PatrolRequiresPatrolStock()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 50_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.8;

            // Patrol stock explicitly zero (StockPatrolShips(w, home, 0) is a no-op for stock,
            // only wires the catalogue)
            StockPatrolShips(w, home, 0);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threat.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.DoesNotContain("PATROL_DISPATCH", string.Join("\n", log));
        }

        [Fact]
        public void Patrol_BudgetCapBlocksSecondDispatchWithinWindow()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            t.PatrolMaxIncomeFractionPerWindow = 0; // isolate: floor only
            t.PatrolBudgetFloor = 1000; // = one PatrolCost
            w.SetTreasury("F", 10_000);

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.8;

            StockPatrolShips(w, home, 2);

            var st = new FactionState { Tag = "F", RngSeed = 1, CentralBankRecentIncome = 0 };
            st.CampaignFocusRegion = threat.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var fm = new FleetManager();
            var log1 = new List<string>();
            fm.Tick(new[] { st }, w, t, log1);
            Assert.True(log1.Any(l => l.Contains("PATROL_DISPATCH")));
            Assert.Equal(1, home.ShipStock["PatrolCargoship"]);

            w.UtcNow = w.UtcNow.AddSeconds(1);
            var log2 = new List<string>();
            fm.Tick(new[] { st }, w, t, log2);
            Assert.False(log2.Any(l => l.Contains("PATROL_DISPATCH")));
            Assert.Equal(1, home.ShipStock["PatrolCargoship"]); // blocked dispatch must not consume a ship
        }

        // Same node, two ticks a few seconds apart (well within FleetCooldown=10s) — must dispatch
        // exactly once. Direct regression test for the reported bug ("fires one ship per tick with
        // no memory").
        [Fact]
        public void Patrol_SameNode_SecondTickWithinCooldown_DoesNotRedispatch()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 50_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.9;

            StockPatrolShips(w, home, 5);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threat.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var fm = new FleetManager();
            var log1 = new List<string>();
            fm.Tick(new[] { st }, w, t, log1);
            Assert.Single(log1, l => l.Contains("PATROL_DISPATCH"));

            w.UtcNow = w.UtcNow.AddSeconds(1);
            var log2 = new List<string>();
            fm.Tick(new[] { st }, w, t, log2);
            Assert.DoesNotContain(log2, l => l.Contains("PATROL_DISPATCH"));
            Assert.Single(st.PendingFleets); // still only the one fleet from tick 1
        }

        // The key parallelism proof: two distinct defensive foci, each over its own hot node.
        // Focus A's node is dispatched on tick 1 and goes on the per-node cooldown; tick 2, well
        // inside that cooldown, must still dispatch to focus B's node — a faction fields more than
        // one ship, and a genuinely separate flashpoint must not wait on an unrelated node's stamp.
        [Fact]
        public void Patrol_TwoDistinctThreatenedNodes_BothGetServiced_NotSerializedByOneCooldown()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 50_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threatA = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            var threatB = AddNode(w, "N003", "F", new Vec3(-10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            w.Graph.Connect("N001", "N003");
            threatA.Threat = 0.9; // worse — picked first
            threatB.Threat = 0.7; // also above 0.5 — a distinct, genuine flashpoint

            StockPatrolShips(w, home, 5);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            // Two separate defensive foci, so neither node is a member of the other's region and
            // neither circuit can absorb the other's node.
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, threatA.Id);

            var fm = new FleetManager();
            var log1 = new List<string>();
            fm.Tick(new[] { st }, w, t, log1);
            Assert.True(log1.Any(l => l.Contains("PATROL_DISPATCH") && l.Contains(threatA.Id)),
                "Tick 1 should dispatch to focus A's node");

            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, threatB.Id);
            w.UtcNow = w.UtcNow.AddSeconds(1);
            var log2 = new List<string>();
            fm.Tick(new[] { st }, w, t, log2);
            Assert.True(log2.Any(l => l.Contains("PATROL_DISPATCH") && l.Contains(threatB.Id)),
                "Tick 2 should dispatch to B — A's cooldown must not block an unrelated flashpoint");
            Assert.Equal(2, st.PendingFleets.Count); // one fleet per node, both serviced
        }

        // Posture scaling mirrors Enact's Raid/Retaliate/Conquer/Guard block: at MilitaryPosture=1.0
        // with the shipped MusterConvoyScaleMax=2.0 and PatrolBurstSize=1, a dispatch should pull 2
        // ships in one coordinated convoy instead of 1.
        [Fact]
        public void Patrol_HighMilitaryPosture_ScalesConvoySize()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 50_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.9;

            StockPatrolShips(w, home, 5);

            var st = new FactionState { Tag = "F", RngSeed = 1, MilitaryPosture = 1.0 };
            st.CampaignFocusRegion = threat.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("PATROL_DISPATCH") && l.Contains("count=2")));
            Assert.Single(st.PendingFleets);
            Assert.Equal(2, st.PendingFleets[0].ConvoySize);
            Assert.Equal(3, home.ShipStock["PatrolCargoship"]); // 5 - 2 consumed
        }

        // Regression: at the default MilitaryPosture=0, behavior is byte-for-byte unchanged from
        // before the burst-size wiring — exactly PatrolBurstSize (1) ship consumed.
        [Fact]
        public void Patrol_ZeroMilitaryPosture_ConvoySizeStaysAtBurstSizeFloor()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 50_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.9;

            StockPatrolShips(w, home, 5);

            var st = new FactionState { Tag = "F", RngSeed = 1 }; // MilitaryPosture defaults to 0
            st.CampaignFocusRegion = threat.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("PATROL_DISPATCH") && l.Contains("count=1")));
            Assert.Equal(1, st.PendingFleets[0].ConvoySize);
            Assert.Equal(4, home.ShipStock["PatrolCargoship"]); // 5 - 1 consumed
        }

        // The trigger FAC-309 slice 3 replaced. A patrol is the picket a defensive focus keeps
        // around its region, so with no focus there is no region to picket -- raw threat on a node
        // is Guard's business, and answering it with a sweep was how Patrol ended up targeting the
        // same node Guard defends, chosen by a worse signal.
        [Fact]
        public void Patrol_DoesNotFireWithoutADefensiveFocus()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 50_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var threat = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            threat.Threat = 0.9;

            StockPatrolShips(w, home, 1);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            // No focus of any kind was ever set.
            Assert.Null(st.CampaignFocusRegion);
            Assert.Empty(st.CampaignFoci);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.DoesNotContain(log, l => l.Contains("PATROL_DISPATCH"));

            // ... and the same world with a focus over the hot node does dispatch, so the assertion
            // above is about the focus and not about some unrelated gate refusing this fixture.
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, threat.Id);
            var withFocus = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, withFocus);
            Assert.Contains(withFocus, l => l.Contains("PATROL_DISPATCH"));
        }

        // The in-region perceived-threat bonus is meant to let a node inside the focus region edge
        // out a node with higher RAW threat outside it — proving the bonus actually participates in
        // target selection, not just gates whether dispatch is allowed.
        [Fact]
        public void Patrol_InRegionBonus_TipsSelectionTowardLowerRawThreatNode()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 50_000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var outOfRegion = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            var inRegion = AddNode(w, "N003", "F", new Vec3(-10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");
            w.Graph.Connect("N001", "N003");
            outOfRegion.Threat = 0.6; // higher raw threat...
            inRegion.Threat = 0.55;   // ...but inRegion's boosted score will exceed it

            StockPatrolShips(w, home, 5);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = inRegion.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);
            st.CampaignFocusRegionMembers.Add(inRegion.Id); // only inRegion is in the flagged region
            st.PerceivedThreatBonusByNode[inRegion.Id] = 0.3; // 0.55 + 0.3 = 0.85 > outOfRegion's 0.6

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(l => l.Contains("PATROL_DISPATCH") && l.Contains(inRegion.Id)),
                "the in-region bonus should tip selection toward inRegion despite its lower raw threat");
            Assert.False(log.Any(l => l.Contains("PATROL_DISPATCH") && l.Contains(outOfRegion.Id)));
        }
    }
}
