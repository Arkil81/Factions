using System;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Decay hands a node whose hold has been ground to zero to <see cref="NeutralFaction"/>, which
    /// puts the besieger's prize on the open market at the exact moment the siege succeeds. Claim
    /// costs one brain action and no hulls; a conquest fleet costs both and has to fly there. These
    /// cover the reservation that stops a bystander taking the ground somebody else broke:
    /// <see cref="NeutralFaction.IsClaimableBy"/> holds the derelict for whoever last landed an
    /// attack on it, for as long as that contest window runs.
    /// </summary>
    public class SiegeClaimReservationTests
    {
        private static SpatialNode AddOwned(WorldModel w, string id, string owner, double hold)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            return n;
        }

        /// <summary>Raids the node until decay abandons it, and returns it.</summary>
        private static SpatialNode GrindToDerelict(WorldModel w, string attackerTag, string nodeId)
        {
            SpatialNode n = w.Graph.Nodes[nodeId];
            for (int i = 0; i < 20 && n.OwnerFactionTag != NeutralFaction.Tag; i++)
            {
                if (attackerTag == null) w.ResolvePlayerRaid(nodeId);
                else w.Raid(attackerTag, nodeId);
                w.DecayInfluence(0.02);
            }
            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
            return n;
        }

        // ── the reservation ───────────────────────────────────────────────────

        /// <summary>The live failure: EXP ground N452 down to abandonment at 13:30:57 with a Conquer
        /// fleet 64 s out, and IMPR's Growth-drive Claim took it at 13:31:29.</summary>
        [Fact]
        public void Claim_ByABystander_IsRefusedWhileTheBesiegersContestStands()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA", hold: 0.2);
            SpatialNode n = GrindToDerelict(w, "BRAVO", "N001");

            Assert.False(w.TryClaim("CHARLIE", "N001", false));
            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
        }

        [Fact]
        public void Claim_ByTheBesieger_Transfers()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA", hold: 0.2);
            SpatialNode n = GrindToDerelict(w, "BRAVO", "N001");

            Assert.True(w.TryClaim("BRAVO", "N001", false));
            Assert.Equal("BRAVO", n.OwnerFactionTag);
        }

        /// <summary>The reservation is a window, not a lock: a siege that walks away releases the
        /// ground to whoever wants it.</summary>
        [Fact]
        public void Claim_ByABystander_IsAllowedOnceTheContestWindowLapses()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA", hold: 0.2);
            SpatialNode n = GrindToDerelict(w, "BRAVO", "N001");

            w.UtcNow = n.ContestedUntilUtc;

            Assert.True(w.TryClaim("CHARLIE", "N001", false));
            Assert.Equal("CHARLIE", n.OwnerFactionTag);
        }

        /// <summary>A player's siege carries no faction tag, so the reservation it takes matches
        /// none — every faction is held off, which is the point.</summary>
        [Fact]
        public void Claim_ByAnyFaction_IsRefusedAfterAPlayerGroundTheNodeDown()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA", hold: 0.2);
            SpatialNode n = GrindToDerelict(w, null, "N001");

            Assert.Null(n.ContestedByTag);
            Assert.False(w.TryClaim("BRAVO", "N001", false));
            Assert.False(w.TryClaim("CHARLIE", "N001", false));
            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
        }

        /// <summary>The presence claim answers the reservation rather than waiting it out: the offer
        /// behind it already required the player to be standing on the node.</summary>
        [Fact]
        public void ClaimByPresence_IsAllowedAgainstAnotherFactionsContest()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA", hold: 0.2);
            SpatialNode n = GrindToDerelict(w, "BRAVO", "N001");

            Assert.True(w.TryClaim("PLYR", "N001", true));
            Assert.Equal("PLYR", n.OwnerFactionTag);
        }

        /// <summary>End to end, in the order the live log has it: the besieger's conquest fleet lands
        /// on the derelict its own raids made and takes it, because the claim could not be jumped in
        /// the meantime.</summary>
        [Fact]
        public void Conquer_ByTheBesieger_TakesTheDerelictABystanderCouldNotClaim()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", "ALFA", hold: 0.2);
            SpatialNode n = GrindToDerelict(w, "BRAVO", "N001");

            Assert.False(w.TryClaim("CHARLIE", "N001", false));

            Assert.Equal(NeutralFaction.Tag, w.Conquer("BRAVO", "N001", 1.0));
            Assert.Equal("BRAVO", n.OwnerFactionTag);
        }

        // ── the stamp ─────────────────────────────────────────────────────────

        /// <summary>The window and its holder move together, so the faction still hitting the node
        /// is the one it is reserved for.</summary>
        [Fact]
        public void MarkContested_LastLandedAttackOwnsTheReservation()
        {
            var w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", "ALFA", hold: 0.9);

            w.Raid("BRAVO", "N001");
            Assert.Equal("BRAVO", n.ContestedByTag);

            w.UtcNow = w.UtcNow.AddSeconds(60);
            w.Raid("CHARLIE", "N001");
            Assert.Equal("CHARLIE", n.ContestedByTag);
        }

        [Fact]
        public void IsReservedAgainst_UncontestedNode_HoldsNobodyOff()
        {
            var w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", NeutralFaction.Tag, hold: 0);

            Assert.False(NeutralFaction.IsReservedAgainst(n, "ALFA", w.UtcNow));
            Assert.True(NeutralFaction.IsClaimableBy(n, "ALFA", w.UtcNow));
        }
    }
}
