using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A node a raid siege strips to hold 0 stays with an owner that holds it and can pay,
    /// for <see cref="FactionTuning.HoldZeroGraceSeconds"/> past the contest window, and the reinforce
    /// manager buys it back the first pass no attack stands on it (FAC-502). Before this the next
    /// decay pass handed it to UNKN: EXP lost six opted-in nodes that way with 1.56 B in the bank.</summary>
    public class HoldZeroGraceTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static FactionTuning Tuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.7;
            t.FleetSpeedKmPerMin = 1200.0;
            t.RaidHoldStripMax = 0.35;
            t.ContestedHoldGainSuppressedSeconds = 900.0;
            t.HoldZeroGraceSeconds = 600.0;
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

        private static SpatialNode Node(WorldModel w, string id, string owner, double hold)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.7, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionState Npc(string tag, params string[] held)
        {
            var st = new FactionState { Tag = tag, RngSeed = 1 };
            for (int i = 0; i < held.Length; i++)
                st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = held[i], Score = 10.0 - i });
            return st;
        }

        // Three ratio-1.0 cycles, the siege that took EXP's nodes: 3 x 0.35 strips a full node.
        private static void Siege(WorldModel w, SpatialNode n)
        {
            for (int i = 0; i < 3; i++) w.Raid("R", n.Id, 1.0);
            Assert.Equal(0.0, n.HoldStrength, 6);
        }

        private static void Decay(WorldModel w, IList<FactionState> states)
        {
            w.DecayInfluence(0.001, 0.001, states, 60.0);
        }

        [Fact]
        public void AHeldNodeASiegeZeroes_IsNotAbandonedOnTheNextDecayPass()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            var states = new List<FactionState> { Npc("F", "N001") };

            Siege(w, n);
            Decay(w, states);

            Assert.Equal("F", n.OwnerFactionTag);
            Assert.Equal(T0, n.HoldZeroSinceUtc);
            Assert.Empty(w.AbandonedNodes);
        }

        /// <summary>The discriminator for the test above: the same siege on a node the owner does not
        /// hold is the "owner does not respond" case, and goes to UNKN on the same pass as before.</summary>
        [Fact]
        public void ANodeTheOwnerDoesNotHold_IsAbandonedAtOnce()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            var states = new List<FactionState> { Npc("F") };

            Siege(w, n);
            Decay(w, states);

            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
        }

        [Fact]
        public void AnOwnerThatCannotPay_LosesTheNodeAtOnce()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", t.ReinforceCost - 1);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            var states = new List<FactionState> { Npc("F", "N001") };

            Siege(w, n);
            Decay(w, states);

            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
        }

        /// <summary>The grace ends at the later of its own length and the contest window: the paid
        /// reinforce is refused while the siege stands, so a window shorter than the siege would
        /// abandon the node before the owner was ever allowed to answer.</summary>
        [Fact]
        public void TheGraceRunsPastTheContestWindow_ThenTheNodeIsAbandoned()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            var states = new List<FactionState> { Npc("F", "N001") };

            Siege(w, n);
            Decay(w, states);

            // 600 s of grace have passed, but the contest window (900 s) is still open.
            w.UtcNow = T0.AddSeconds(700);
            Decay(w, states);
            Assert.Equal("F", n.OwnerFactionTag);

            // Both have lapsed and nobody paid: now it is the unanswered case.
            w.UtcNow = T0.AddSeconds(901);
            Decay(w, states);
            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
            Assert.Equal(default(DateTime), n.HoldZeroSinceUtc);
        }

        /// <summary>A player's ranked list is projected from its opt-in by the manager, so a decay
        /// pass that runs first must read the opt-in itself.</summary>
        [Fact]
        public void APlayerOptIn_HoldsTheNode_BeforeTheManagerHasProjectedIt()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("EXP", 10_000);
            SpatialNode n = Node(w, "N096", "EXP", 1.0);
            var exp = new FactionState { Tag = "EXP", RngSeed = 1, IsPlayerFaction = true };
            exp.ReinforceOptIn["N096"] = 0;
            Assert.Empty(exp.ReinforcePriority);

            Siege(w, n);
            Decay(w, new List<FactionState> { exp });

            Assert.Equal("EXP", n.OwnerFactionTag);
        }

        [Fact]
        public void ZeroGrace_RestoresTheOldBehaviourExactly()
        {
            FactionTuning t = Tuning();
            t.HoldZeroGraceSeconds = 0;
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            var states = new List<FactionState> { Npc("F", "N001") };

            Siege(w, n);
            Decay(w, states);

            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
        }

        /// <summary>The whole loop, with a flyable hull on hand — EXP's case. A mission cannot land
        /// before the grace runs out, and a dead one has taken the node cooldown, so the manager
        /// pays outright instead of flying.</summary>
        [Fact]
        public void TheManagerPaysForANodeInGrace_EvenWithAHullAndAColdNode()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            ReinforceMissionSupport.Depot(w, "F");
            FactionState st = Npc("F", "N001");
            var states = new List<FactionState> { st };
            st.ReinforceNodeCooldowns["N001"] = T0.AddSeconds(900);   // a mission that died on the way in

            Siege(w, n);
            Decay(w, states);
            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);

            Assert.Contains(log, l => l.Contains("F REINFORCE_PAID cost=500 N001 hold=0.00 -> 0.70 why=hold_grace"));
            Assert.Contains(log, l => l.Contains("REINFORCE_SKIPPED") && l.Contains("paid_grace=1"));
            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_DISPATCH"));
            Assert.Equal(0.7, n.HoldStrength, 6);
            Assert.Equal(10_000 - t.ReinforceCost, w.GetTreasury("F"));

            // And the next decay pass does not abandon it, and its zero clock is gone.
            w.UtcNow = T0.AddSeconds(2000);
            Decay(w, states);
            Assert.Equal("F", n.OwnerFactionTag);
            Assert.Equal(default(DateTime), n.HoldZeroSinceUtc);
        }

        /// <summary>The payment is still refused while the attack stands, as every paid reinforce is,
        /// and lands the first pass after it ends — inside the grace.</summary>
        [Fact]
        public void TheGracePaymentWaitsForTheAttackToEnd()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            FactionState st = Npc("F", "N001");
            var states = new List<FactionState> { st };
            var fm = new FleetManager();

            Siege(w, n);
            Decay(w, states);
            Assert.True(PlayerSiege.BeginPresenceRaid(n, t, w.UtcNow));

            var during = new List<string>();
            fm.Tick(states, w, t, during);
            Assert.Contains(during, l => l.Contains("F REINFORCE_PAID_BLOCKED N001") && l.Contains("why=hold_grace"));
            Assert.Equal(0.0, n.HoldStrength, 6);
            Assert.Equal(10_000, w.GetTreasury("F"));

            Assert.True(PlayerSiege.Maintain(n, t, w.UtcNow, attackerPresent: false));
            w.UtcNow = T0.AddSeconds(60);
            Decay(w, states);
            Assert.Equal("F", n.OwnerFactionTag);
            var after = new List<string>();
            fm.Tick(states, w, t, after);

            Assert.Contains(after, l => l.Contains("REINFORCE_PAID cost=") && l.Contains("why=hold_grace"));
            Assert.Equal(0.7, n.HoldStrength, 6);
        }

        /// <summary>The operator's repair: the ground comes back as if never lost — full hold, no
        /// contest, zone up — and the holder's parked hulls fly to its own ground instead of being
        /// handed over with it. Unlike <see cref="WorldModel.CaptureNode"/>, which leaves a conquest's
        /// marks.</summary>
        [Fact]
        public void RestoreNode_ReturnsTheGround_NotTheConquest()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            SpatialNode n = Node(w, "N096", "IMPR", 0.4);
            SpatialNode home = w.Graph.Add(new Vec3(50, 0, 0), 0.7, "N004");
            home.OwnerFactionTag = "IMPR";
            n.ShipStock["Idris"] = 2;
            w.Raid("UNGD", "N096", 1.0);
            Assert.True(n.ContestedUntilUtc > T0);
            var impr = Npc("IMPR", "N096", "N004");

            string prior = w.RestoreNode("EXP", "N096", impr);

            Assert.Equal("IMPR", prior);
            Assert.Equal("EXP", n.OwnerFactionTag);
            Assert.Equal(1.0, n.HoldStrength, 6);
            Assert.Equal(default(DateTime), n.ContestedUntilUtc);
            Assert.Equal(default(DateTime), n.SafeZoneDownUntilUtc);
            Assert.False(n.ShipStock.ContainsKey("Idris") && n.ShipStock["Idris"] > 0);
            Assert.NotEmpty(w.ShipEvacuations);

            Assert.Null(w.RestoreNode("EXP", "N096", null));   // already theirs: nothing changed
        }

        /// <summary>A node the UNKN sweep deleted comes back under its old id at full hold — the id
        /// is what the owner's hold opt-in and every reader key on — and an id still in the graph is
        /// refused rather than duplicated.</summary>
        [Fact]
        public void RecreateNodeAt_RestoresTheIdAtFullHold_AndRefusesALiveId()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            Node(w, "N096", "IMPR", 1.0);

            SpatialNode n = w.RecreateNodeAt("EXP", "N099", new Vec3(500, 0, 0));

            Assert.NotNull(n);
            Assert.Equal("N099", n.Id);
            Assert.Same(n, w.Graph.Nodes["N099"]);
            Assert.Equal("EXP", n.OwnerFactionTag);
            Assert.Equal(1.0, n.HoldStrength, 6);
            Assert.Null(w.RecreateNodeAt("EXP", "N096", new Vec3(900, 0, 0)));
            Assert.Equal("IMPR", w.Graph.Nodes["N096"].OwnerFactionTag);
        }

        /// <summary>A Conquer still takes a node in grace: hold 0 is under the ceiling and the
        /// owner is live, so it goes down the capture path rather than waiting for a derelict.</summary>
        [Fact]
        public void AConquerTakesANodeInGrace()
        {
            FactionTuning t = Tuning();
            WorldModel w = World(t);
            w.SetTreasury("F", 10_000);
            SpatialNode n = Node(w, "N001", "F", 1.0);
            var states = new List<FactionState> { Npc("F", "N001") };

            Siege(w, n);
            Decay(w, states);
            string victim = w.Conquer("R", "N001", 1.0);

            Assert.Equal("F", victim);
            Assert.Equal("R", n.OwnerFactionTag);
        }
    }
}
