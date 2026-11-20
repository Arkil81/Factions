using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Tests for node ownership changes via Claim/ExpandFrom, Withdraw, and Raid.
    ///
    /// "src" in snap logs is <c>Graph.OwnedBy(tag).Count()</c> — a live count of nodes
    /// where <c>OwnerFactionTag == tag</c>. These tests verify that count changes correctly
    /// after each ownership-affecting operation, and that AccrueIncome / GetNetFlow
    /// (nf in snap logs) reflect the post-flip state.
    ///
    /// Motivated by live observation 2026-06-20 t247: DVSR Raid(E9259...) caused SPRT
    /// src to drop 4→3 with no corresponding DVSR change. Tests here document that
    /// Raid does NOT transfer ownership (only Withdraw does), so the observed src drop
    /// was downstream of a Withdraw, not the Raid itself.
    /// </summary>
    public class NodeOwnershipFlipTests
    {
        // ── helpers ──────────────────────────────────────────────────────────

        private static int Src(WorldModel w, string tag)
            => w.Graph.OwnedBy(tag).Count();

        private static SpatialNode AddOwned(WorldModel w, string id, string owner, double hold = 0.5)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            return n;
        }

        private static SpatialNode AddUnowned(WorldModel w, string id)
        {
            return w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
        }

        // ── CaptureNode: the transfer a player siege earns ────────────────────
        //
        // Every node these tests capture sits at FULL hold — above any sane ConquerHoldCeiling.
        // That is the whole point: it is the case Claim refuses and Conquer softens rather than
        // finishes, and a capture that only worked on a weakened node would pass a laxer test while
        // still being useless to the path that needs it.

        [Fact]
        public void CaptureNode_TakesANodeHeldByALiveFactionAtFullHold()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            w.Tuning.ConquerCapturedHoldStrength = 0.3;
            w.Tuning.ConquerCapturedThreat = 0.4;
            AddOwned(w, "N001", "SPRT", hold: 1.0);

            string victim = w.CaptureNode("EXP", "N001");

            Assert.Equal("SPRT", victim);
            Assert.Equal("EXP", w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Equal(1, Src(w, "EXP"));
            Assert.Equal(0, Src(w, "SPRT"));
            Assert.Equal(0.3, w.Graph.Nodes["N001"].HoldStrength);
            Assert.Equal(0.4, w.Graph.Nodes["N001"].Threat);
        }

        [Fact]
        public void CaptureNode_ReturnsNull_AndChangesNothing_WhenThereIsNoTransferToMake()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "EXP", hold: 1.0);
            AddUnowned(w, "N002");

            Assert.Null(w.CaptureNode("EXP", "N001"));      // already theirs
            Assert.Null(w.CaptureNode("EXP", "N404"));      // unknown node
            Assert.Null(w.CaptureNode("EXP", "N002"));      // nobody to take it from
            Assert.Null(w.CaptureNode(null, "N001"));
            Assert.Null(w.CaptureNode("EXP", null));

            Assert.Equal("EXP", w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Null(w.Graph.Nodes["N002"].OwnerFactionTag);
        }

        [Fact]
        public void Claim_RefusesALiveFactionsNode_WhichIsWhyCaptureNodeExists()
        {
            // Pins the guard the conquer-offer path used to run into. Claim is for a node nobody
            // holds; loosening it so a player conquest "works" would also let any claim take an
            // inhabited node.
            var w = TestSupport.World();
            AddOwned(w, "N001", "SPRT", hold: 1.0);

            w.Claim("EXP", "N001");

            Assert.Equal("SPRT", w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Equal(0, Src(w, "EXP"));
        }

        [Fact]
        public void Conquer_StillHonoursItsHoldCeiling_AfterDelegatingTheTransfer()
        {
            // The refactor moved Conquer's transfer body into CaptureNode. CaptureNode has no
            // ceiling of its own, so this pins that Conquer kept the one it always had -- otherwise
            // every NPC Conquer would silently become an outright capture.
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            w.Tuning.ConquerCapturedHoldStrength = 0.3;
            AddOwned(w, "N_strong", "SPRT", hold: 0.9);
            AddOwned(w, "N_broken", "SPRT", hold: 0.1);

            w.Conquer("EXP", "N_strong");
            Assert.Equal("SPRT", w.Graph.Nodes["N_strong"].OwnerFactionTag);
            Assert.True(w.Graph.Nodes["N_strong"].HoldStrength < 0.9, "above the ceiling still softens");

            w.Conquer("EXP", "N_broken");
            Assert.Equal("EXP", w.Graph.Nodes["N_broken"].OwnerFactionTag);
            Assert.Equal(0.3, w.Graph.Nodes["N_broken"].HoldStrength);
        }

        // ── Claim / ExpandFrom ────────────────────────────────────────────────

        [Fact]
        public void Claim_EmptyNode_IncrementsSrc()
        {
            var w = TestSupport.World();
            AddUnowned(w, "N001");

            Assert.Equal(0, Src(w, "ALFA"));
            w.Claim("ALFA", "N001");
            Assert.Equal(1, Src(w, "ALFA"));
            Assert.Equal("ALFA", w.Graph.Nodes["N001"].OwnerFactionTag);
        }

        [Fact]
        public void Claim_AlreadyOwnedNode_IsNoOp()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA");

            // Claim on an already-owned node must not change ownership.
            w.Claim("BETA", "N001");
            Assert.Equal(1, Src(w, "ALFA"));
            Assert.Equal(0, Src(w, "BETA"));
            Assert.Equal("ALFA", w.Graph.Nodes["N001"].OwnerFactionTag);
        }

        [Fact]
        public void ExpandFrom_NewNode_IncrementsSrcForExpander()
        {
            var w = TestSupport.World();
            AddOwned(w, "P001", "ALFA");

            Assert.Equal(1, Src(w, "ALFA"));
            SpatialNode child = w.ExpandFrom("ALFA", "P001");
            Assert.Equal(2, Src(w, "ALFA"));
            Assert.Equal("ALFA", child.OwnerFactionTag);
        }

        // ── Withdraw ──────────────────────────────────────────────────────────

        [Fact]
        public void Withdraw_OwnNode_DecrementsSrc()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA");
            AddOwned(w, "N002", "ALFA");

            Assert.Equal(2, Src(w, "ALFA"));
            w.Withdraw("ALFA", "N001");
            Assert.Equal(1, Src(w, "ALFA"));
            // Withdrawn nodes are handed to the neutral graveyard faction, not nulled — they
            // stay in the graph, persistent and claimable, rather than vanishing.
            Assert.Equal(NeutralFaction.Tag, w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Equal(0, w.Graph.Nodes["N001"].HoldStrength);
        }

        [Fact]
        public void Withdraw_OwnNode_LeavesOtherFactionSrcUnchanged()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA");
            AddOwned(w, "N002", "BETA");

            w.Withdraw("ALFA", "N001");

            Assert.Equal(0, Src(w, "ALFA"));
            Assert.Equal(1, Src(w, "BETA")); // BETA untouched
        }

        [Fact]
        public void Withdraw_RivalsNode_IsNoOp()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "BETA");

            // ALFA cannot withdraw BETA's node.
            w.Withdraw("ALFA", "N001");
            Assert.Equal(1, Src(w, "BETA"));
            Assert.Equal("BETA", w.Graph.Nodes["N001"].OwnerFactionTag);
        }

        // ── Raid ──────────────────────────────────────────────────────────────

        [Fact]
        public void Raid_RivalsNode_DoesNotChangeOwnership()
        {
            // Raid damages HoldStrength and spikes Threat — it does NOT transfer
            // OwnerFactionTag. Ownership only changes via DecayInfluence (hold → 0)
            // which triggers deletion for ENGINE nodes, or via explicit Withdraw.
            var w = TestSupport.World();
            AddOwned(w, "N001", "BETA", hold: 0.9);

            int srcAlfaBefore = Src(w, "ALFA");
            int srcBetaBefore = Src(w, "BETA");

            w.Raid("ALFA", "N001");

            Assert.Equal(srcAlfaBefore, Src(w, "ALFA")); // attacker src unchanged
            Assert.Equal(srcBetaBefore, Src(w, "BETA")); // victim src unchanged
            Assert.Equal("BETA", w.Graph.Nodes["N001"].OwnerFactionTag); // still BETA's
        }

        [Fact]
        public void Raid_RivalsNode_DamagesHoldAndSpikesThreat()
        {
            var w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 0.9);
            double threatBefore = n.Threat;

            w.Raid("ALFA", "N001");

            Assert.True(n.HoldStrength < 0.9,   "Raid must reduce hold strength");
            Assert.True(n.Threat > threatBefore, "Raid must spike node threat");
        }

        [Fact]
        public void Raid_OwnNode_IsNoOp()
        {
            var w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", "ALFA", hold: 0.9);

            w.Raid("ALFA", "N001"); // raiding your own node is a no-op per WorldModel guard

            Assert.Equal(1, Src(w, "ALFA"));
            Assert.Equal(0.9, n.HoldStrength);
        }

        [Fact]
        public void Raid_UnownedNode_IsNoOp()
        {
            var w = TestSupport.World();
            SpatialNode n = AddUnowned(w, "N001");
            n.HoldStrength = 0.5;

            w.Raid("ALFA", "N001");

            Assert.Equal(0, Src(w, "ALFA"));
            Assert.Equal(0.5, n.HoldStrength);
        }

        [Fact]
        public void Raid_RivalsNode_IncidentTaggedRaid()
        {
            // Regression guard: the RaidIncident.Kind field addition (for Conquer) must not
            // silently change Raid's own incident tagging.
            var w = TestSupport.World();
            AddOwned(w, "N001", "BETA", hold: 0.9);

            w.Raid("ALFA", "N001");

            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.Equal(ActionKind.Raid, inc.Kind);
            Assert.Equal("ALFA", inc.Attacker);
            Assert.Equal("BETA", inc.Victim);
            Assert.Equal("N001", inc.NodeId);
        }

        // ── Conquer ───────────────────────────────────────────────────────────

        [Fact]
        public void Conquer_WeakRivalNode_TransfersOwnership()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            w.Tuning.ConquerCapturedHoldStrength = 0.30;
            w.Tuning.ConquerCapturedThreat = 0.20;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 0.10);

            int srcAlfaBefore = Src(w, "ALFA");
            int srcBetaBefore = Src(w, "BETA");

            w.Conquer("ALFA", "N001");

            Assert.Equal("ALFA", w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Equal(srcAlfaBefore + 1, Src(w, "ALFA"));
            Assert.Equal(srcBetaBefore - 1, Src(w, "BETA"));
            Assert.Equal(w.Tuning.ConquerCapturedHoldStrength, n.HoldStrength);
            Assert.Equal(w.Tuning.ConquerCapturedThreat, n.Threat);
        }

        [Fact]
        public void Conquer_StrongRivalNode_DoesNotTransferOwnership_DegradesToRaidDamage()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 0.9);
            double threatBefore = n.Threat;

            w.Conquer("ALFA", "N001");

            Assert.Equal("BETA", w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.True(n.HoldStrength < 0.9, "not-yet-weak-enough Conquer must still apply fallback hold damage");
            Assert.True(n.Threat > threatBefore, "not-yet-weak-enough Conquer must still spike threat like Raid");
        }

        [Fact]
        public void Conquer_OwnNode_IsNoOp()
        {
            var w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", "ALFA", hold: 0.10);
            w.Tuning.ConquerHoldCeiling = 0.15;

            w.Conquer("ALFA", "N001"); // conquering your own node is a no-op per WorldModel guard

            Assert.Equal(1, Src(w, "ALFA"));
            Assert.Equal(0.10, n.HoldStrength);
        }

        [Fact]
        public void Conquer_UnownedNode_IsNoOp()
        {
            var w = TestSupport.World();
            SpatialNode n = AddUnowned(w, "N001");
            n.HoldStrength = 0.10;
            w.Tuning.ConquerHoldCeiling = 0.15;

            w.Conquer("ALFA", "N001");

            Assert.Equal(0, Src(w, "ALFA"));
            Assert.Null(n.OwnerFactionTag);
            Assert.Equal(0.10, n.HoldStrength);
        }

        [Fact]
        public void Conquer_CapturedNode_RetainsCapabilitiesAndShipStock()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 0.10);
            n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            n.ShipStock["Raid"] = 2;

            w.Conquer("ALFA", "N001");

            Assert.Equal("ALFA", n.OwnerFactionTag);
            Assert.Equal(1, n.Capabilities[NodeCapabilityKind.Shipyard]);
            Assert.Equal(2, n.ShipStock["Raid"]);
        }

        [Fact]
        public void Conquer_RecordsIncidentTaggedConquer()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            AddOwned(w, "N001", "BETA", hold: 0.10);

            w.Conquer("ALFA", "N001");

            RaidIncident inc = Assert.Single(w.Incidents);
            Assert.Equal(ActionKind.Conquer, inc.Kind);
            Assert.Equal("ALFA", inc.Attacker);
            Assert.Equal("BETA", inc.Victim);
            Assert.Equal("N001", inc.NodeId);
        }

        // ── DecayInfluence hands off a raided ENGINE node to UNKN and drops victim src ──

        [Fact]
        public void DecayInfluence_AfterRaid_TransfersEngineNodeToUnknAndDropsVictimSrc()
        {
            // ENGINE nodes (N### prefix) are handed to the neutral graveyard faction (UNKN) when
            // hold reaches 0 via DecayInfluence — never deleted. That is the mechanism behind the
            // live observation: DVSR Raid → hold damage → DecayInfluence abandons node → SPRT src
            // drops. This test documents that path, and that the node itself survives.
            var w = TestSupport.World();
            // Add a node with very low hold so one Raid + one decay cycle abandons it.
            AddOwned(w, "N001", "SPRT", hold: 0.05);

            Assert.Equal(1, Src(w, "SPRT"));
            w.Raid("DVSR", "N001"); // knocks hold to ≤ 0

            // Decay tick: hold ≤ 0 → ENGINE node transfers to UNKN, not deleted.
            w.DecayInfluence(0.0); // 0 step, but node hold is already ≤ 0 after Raid damage.

            Assert.True(w.Graph.Nodes.ContainsKey("N001"));   // never removed from the graph
            Assert.Equal(NeutralFaction.Tag, w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Equal(0, Src(w, "SPRT")); // src dropped because ownership was abandoned
            AbandonedNode lost = Assert.Single(w.AbandonedNodes);
            Assert.Equal("N001", lost.NodeId);
            Assert.Equal("SPRT", lost.PriorOwnerTag);
        }

        // ── NetFlow recalculation after ownership change ───────────────────────

        [Fact]
        public void GetNetFlow_ReflectsWithdraw_AfterAccrueIncome()
        {
            var w = TestSupport.World();
            SpatialNode n1 = AddOwned(w, "N001", "ALFA");
            n1.ResourceValue = 0.8;
            n1.StableStationId = 1;
            SpatialNode n2 = AddOwned(w, "N002", "ALFA");
            n2.ResourceValue = 0.8;
            n2.StableStationId = 2;

            w.SetTreasury("ALFA", 1_000_000);
            w.AccrueIncome();
            long nfBefore = w.GetNetFlow("ALFA");

            // Withdraw one node; accrue again so the cache reflects the new ownership.
            w.Withdraw("ALFA", "N002");
            w.SetTreasury("ALFA", 1_000_000);
            w.AccrueIncome();
            long nfAfter = w.GetNetFlow("ALFA");

            Assert.Equal(1, Src(w, "ALFA"));
            Assert.Equal(-2 * w.Tuning.UpkeepPerSource, nfBefore);
            Assert.Equal(-1 * w.Tuning.UpkeepPerSource, nfAfter);
        }

        [Fact]
        public void GetNetFlow_ReflectsExpand_AfterAccrueIncome()
        {
            var w = TestSupport.World();
            SpatialNode parent = AddOwned(w, "P001", "ALFA");
            parent.ResourceValue = 0.8;
            parent.StableStationId = 1;

            w.SetTreasury("ALFA", 1_000_000);
            w.AccrueIncome();

            SpatialNode child = w.ExpandFrom("ALFA", "P001");
            child.StableStationId = 2;

            w.SetTreasury("ALFA", 1_000_000);
            w.AccrueIncome();

            Assert.Equal(2, Src(w, "ALFA"));
            long nf = w.GetNetFlow("ALFA");
            Assert.Equal(-2 * w.Tuning.UpkeepPerSource, nf);
        }
    }
}
