using System;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// UNKN (<see cref="NeutralFaction.Tag"/>) is the permanently-dormant graveyard for abandoned
    /// territory: <see cref="WorldModel.Withdraw"/> and decay expiry (<see cref="WorldModel.DecayInfluence"/>)
    /// hand nodes to it instead of deleting them or leaving them unowned, so a node never leaves the
    /// graph. These tests cover the resulting behavior: a UNKN node is taken by Claim or by Conquer
    /// and is immune to Raid, UNKN never thinks/acts, and UNKN nodes stay out of other factions'
    /// rival/economy math. See docs/economy-territory.md.
    /// </summary>
    public class UnclaimedNodeTests
    {
        private static SpatialNode AddOwned(WorldModel w, string id, string owner, double hold = 0.5)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            return n;
        }

        // ── Claim and Conquer take a derelict; Raid has nothing to damage ──────

        [Fact]
        public void Claim_UnknOwnedNode_TransfersOwnership()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", NeutralFaction.Tag, hold: 0);

            w.Claim("ALFA", "N001");

            Assert.Equal("ALFA", w.Graph.Nodes["N001"].OwnerFactionTag);
            Assert.Single(w.Graph.OwnedBy("ALFA"));
        }

        [Fact]
        public void Raid_UnknOwnedNode_IsNoOp()
        {
            var w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", NeutralFaction.Tag, hold: 0);

            w.Raid("ALFA", "N001");

            Assert.Equal(NeutralFaction.Tag, n.OwnerFactionTag);
            Assert.Equal(0, n.HoldStrength);
            Assert.Empty(w.Incidents);
        }

        /// <summary>A derelict has no garrison behind its hold, so Conquer takes it outright rather
        /// than grinding a number nobody is defending. Before this, a conquest fleet sent at an
        /// abandoned node resolved as repelled every time and re-dispatched forever, because nothing
        /// downstream could ever see the node change hands. Provenance: FAC-329.</summary>
        [Fact]
        public void Conquer_UnknOwnedNode_TransfersOwnership()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            SpatialNode n = AddOwned(w, "N001", NeutralFaction.Tag, hold: 0);

            string victim = w.Conquer("ALFA", "N001");

            Assert.Equal(NeutralFaction.Tag, victim);
            Assert.Equal("ALFA", n.OwnerFactionTag);
            Assert.Equal(w.Tuning.ConquerCapturedHoldStrength, n.HoldStrength);
        }

        /// <summary>Taking a derelict is not an act against anybody: UNKN holds no standing, so no
        /// RaidIncident is filed and the taker's diplomacy is untouched. Provenance: FAC-329.</summary>
        [Fact]
        public void Conquer_UnknOwnedNode_FilesNoIncident()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", NeutralFaction.Tag, hold: 0);

            w.Conquer("ALFA", "N001");

            Assert.Empty(w.Incidents);
        }

        /// <summary>The hold ceiling governs a rival's garrison and nothing else. A derelict that
        /// kept a high hold from the moment it was abandoned is still taken, so the capture cannot
        /// depend on how the node became neutral. Provenance: FAC-329.</summary>
        [Fact]
        public void Conquer_UnknOwnedNode_IgnoresHoldCeiling()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            SpatialNode n = AddOwned(w, "N001", NeutralFaction.Tag, hold: 1.0);

            w.Conquer("ALFA", "N001", 0.5);

            Assert.Equal("ALFA", n.OwnerFactionTag);
        }

        /// <summary>The rival path is untouched: a node held above the ceiling by a live owner is
        /// still only softened. Provenance: FAC-329.</summary>
        [Fact]
        public void Conquer_RivalHeldAboveCeiling_StillOnlySoftens()
        {
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            SpatialNode n = AddOwned(w, "N001", "BETA", hold: 1.0);

            w.Conquer("ALFA", "N001", 1.0);

            Assert.Equal("BETA", n.OwnerFactionTag);
            Assert.Single(w.Incidents);
        }

        // ── DecayInfluence leaves already-neutral nodes alone ──────────────────

        [Fact]
        public void DecayInfluence_UnknOwnedNode_IsSkipped()
        {
            var w = TestSupport.World();
            AddOwned(w, "N001", NeutralFaction.Tag, hold: 0);

            w.DecayInfluence(0.5);

            Assert.Equal(0, w.Graph.Nodes["N001"].HoldStrength);
            Assert.Empty(w.AbandonedNodes); // nothing new to report -- already neutral
        }

        // ── ApplyBrainDoc: UNKN's naming theme self-heals on load ──────────────

        [Fact]
        public void ApplyBrainDoc_UnknTag_ForcesPersonalityToDerelict_EvenIfPersistedDifferently()
        {
            // Live-caught 2026-08-12: a world where UNKN was already registered before this
            // feature shipped had PersonalityId="default" baked into brains/UNKN.json from the
            // original PersonalityFromFaction guess. The registration-time fix
            // (FactionsPlugin.InitializeFromNpcFactions) only ever applies to a brand-new UNKN,
            // so an already-registered one needs to self-heal here, on every load.
            var doc = new BrainDoc { Tag = NeutralFaction.Tag, PersonalityId = "default" };
            FactionState loaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal("derelict", loaded.PersonalityId);
        }

        [Fact]
        public void ApplyBrainDoc_NonUnknTag_PersonalityUnaffected()
        {
            var doc = new BrainDoc { Tag = "ALFA", PersonalityId = "raider" };
            FactionState loaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal("raider", loaded.PersonalityId);
        }

        // ── FactionLifecycle: UNKN is always dormant ───────────────────────────

        [Fact]
        public void IsDormant_UnknTag_AlwaysTrue()
        {
            var unkn = new FactionState { Tag = NeutralFaction.Tag, DormantUntilUtc = DateTime.MinValue };
            Assert.True(FactionLifecycle.IsDormant(unkn, DateTime.UtcNow));
        }

        // ── AccrueIncome: UNKN nodes produce and cost nothing ──────────────────

        [Fact]
        public void AccrueIncome_UnknOwnedNode_DoesNotMintOrChargeUpkeep()
        {
            var w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", NeutralFaction.Tag, hold: 0.5);
            n.ResourceValue = 0.8;
            n.StableStationId = 1;

            w.SetTreasury(NeutralFaction.Tag, 1_000_000);
            w.AccrueIncome();

            Assert.Empty(n.Inventory);
            Assert.Equal(1_000_000, w.GetTreasury(NeutralFaction.Tag));
        }

        // ── Diplomacy / InfluenceField: UNKN is not a rival ────────────────────

        [Fact]
        public void Drift_UnknOwnedNode_DoesNotShiftRelations()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.8; mine.Radius = 30;
            var derelict = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "N001"); derelict.OwnerFactionTag = NeutralFaction.Tag; derelict.HoldStrength = 0; derelict.Radius = 30;

            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = 0.9, Aggression = 0.8, Mercantilism = 0.1 } };
            var s = new FactionState { Tag = "A" };
            for (int i = 0; i < 20; i++) Diplomacy.Drift(s, p, w, t);

            Assert.Equal(0, s.RelationTo(NeutralFaction.Tag)); // never touched -- UNKN isn't a rival
        }

        [Fact]
        public void AccrueFatigue_UnknOwnedNode_DoesNotAccrue()
        {
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var derelict = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N001"); derelict.OwnerFactionTag = NeutralFaction.Tag; derelict.HoldStrength = 0;

            var s = new FactionState { Tag = "A" };
            s.Relations[NeutralFaction.Tag] = -0.9; // hypothetically hostile, should still not matter
            for (int i = 0; i < 5; i++) Diplomacy.AccrueFatigue(s, w, t);

            Assert.Equal(0, s.FatigueTo(NeutralFaction.Tag));
        }

        [Fact]
        public void RivalTags_ExcludesUnkn()
        {
            var w = TestSupport.World();
            var rival = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "B1"); rival.OwnerFactionTag = "BETA"; rival.HoldStrength = 0.5;
            var derelict = w.Graph.Add(new Vec3(10, 0, 0), 0.5, "N001"); derelict.OwnerFactionTag = NeutralFaction.Tag; derelict.HoldStrength = 0;

            var rivals = new InfluenceField(w.Graph).RivalTags("ALFA");

            Assert.Contains("BETA", rivals);
            Assert.DoesNotContain(NeutralFaction.Tag, rivals);
        }
    }
}
