using System;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Inter-faction relations + conflict: border pressure souring standings (trait-dependent),
    /// raids damaging a rival + recording an incident, and the brain proposing a raid on a hostile rival.</summary>
    public class DiplomacyTests
    {
        private static Personality Persona(double xeno, double aggr, double merc)
        {
            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = xeno, Aggression = aggr, Mercantilism = merc } };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        // Two factions sharing a border. A xenophobe comes to dislike the presser; a merchant trades through it.
        [Fact]
        public void BorderPressure_SoursXenophobe_NotMerchant()
        {
            var t = TestSupport.Tuning();

            WorldModel World()
            {
                var w = TestSupport.World();
                var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
                var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "B"; b.HoldStrength = 0.8; b.Radius = 30;
                return w;
            }

            var xeno = new FactionState { Tag = "A" };
            var merch = new FactionState { Tag = "A" };
            for (int i = 0; i < 20; i++) Diplomacy.Drift(xeno, Persona(0.9, 0.8, 0.1), World(), t);
            for (int i = 0; i < 20; i++) Diplomacy.Drift(merch, Persona(0.0, 0.1, 0.9), World(), t);

            Assert.True(xeno.RelationTo("B") < -0.2, "a xenophobe should come to dislike a faction pressing its border");
            Assert.True(merch.RelationTo("B") > xeno.RelationTo("B"), "a merchant tolerates the same pressure far better");
        }

        [Fact]
        public void Raid_DamagesTarget_AndRecordsIncident()
        {
            var w = TestSupport.World();
            var n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "B1"); n.OwnerFactionTag = "B"; n.HoldStrength = 0.7;
            double holdBefore = n.HoldStrength;

            w.Raid("A", "B1");

            Assert.True(n.HoldStrength < holdBefore, "raid should knock down the target's hold");
            Assert.True(n.Threat > 0, "raid should spike the target's threat");
            Assert.Single(w.Incidents);
            Assert.Equal("A", w.Incidents[0].Attacker);
            Assert.Equal("B", w.Incidents[0].Victim);

            w.Raid("B", "B1");                  // can't raid your own node
            Assert.Single(w.Incidents);         // unchanged
        }

        [Fact]
        public void HitRelation_SoursVictimTowardAttacker()
        {
            var t = TestSupport.Tuning();
            var victim = new FactionState { Tag = "B" };
            Diplomacy.HitRelation(victim, "A", t);
            Assert.True(victim.RelationTo("A") < 0, "being raided should sour the victim toward the attacker");
        }

        // An aggressive faction that hates a reachable rival proposes a Raid on that rival's source.
        [Fact]
        public void Brain_GeneratesRaid_AgainstHostileReachableRival()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.9; mine.Radius = 40;
            var his = w.Graph.Add(new Vec3(20, 0, 0), 0.7, "B1"); his.OwnerFactionTag = "B"; his.HoldStrength = 0.5; his.Radius = 40;
            w.SetTreasury("A", 200000);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;             // we already hate B

            var p = Persona(0.6, 0.95, 0.1);     // aggressive raider
            p.Weights[Drive.Rivalry] = 2.0;      // make Rivalry dominate so we enter the conflict branch

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.NotNull(CampaignNomination.Raid(d, w, "B1"));
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Raid);   // the brain nominates, it does not dispatch
        }

        // Trade lifts the initiator's standing by TradeRelationBoost; partner is unaffected unless they also trade.
        [Fact]
        public void Trade_LiftsInitiatorRelation_PartnerUnchanged()
        {
            var t = TestSupport.Tuning();
            var initiator = new FactionState { Tag = "A" };
            var partner   = new FactionState { Tag = "B" };

            double before = initiator.RelationTo("B");
            Diplomacy.LiftRelation(initiator, "B", t);

            Assert.True(initiator.RelationTo("B") > before, "trade should lift the initiator's standing toward the partner");
            Assert.Equal(before + t.TradeRelationBoost, initiator.RelationTo("B"), 8);
            Assert.Equal(0.0, partner.RelationTo("A"), 8);   // partner is untouched
        }

        // Trade boost is ~1/10 of the raid penalty (asymmetry: negative actions are remembered longer).
        [Fact]
        public void Trade_BoostIsRoughlyOnetenthOfRaidHit()
        {
            var t = TestSupport.Tuning();
            Assert.True(t.TradeRelationBoost > 0, "trade boost must be positive");
            Assert.True(t.RaidRelationHit / t.TradeRelationBoost >= 8.0,
                        "raid hit should be at least ~8x the trade boost (negative actions remembered longer)");
        }

        // A mercantile brain with a reachable, non-hostile neighbour generates a Trade candidate.
        [Fact]
        public void Brain_GeneratesTrade_ForMercantile_WithReachableNeighbour()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.7, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.9; mine.Radius = 40;
            var his  = w.Graph.Add(new Vec3(20, 0, 0), 0.7, "B1"); his.OwnerFactionTag  = "B"; his.HoldStrength  = 0.9; his.Radius  = 40;
            w.SetTreasury("A", 200000);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            // B is neutral (default RelationTo = 0), above the HostileRelationThreshold (-0.3).

            var p = Persona(0.05, 0.2, 0.9);     // mercantile: low xeno, low aggr, high merc
            p.Weights[Drive.Growth] = 2.0;        // Growth dominates so Trade is generated

            brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.Contains(s.TradePriority, c => c.PartnerTag == "B");
        }

        // Propose(Truce) still triggers truce formation — backward compat with OfferPeace.
        [Fact]
        public void Propose_Truce_FormsTraceViaBilateral()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = 0.8;
            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, w.UtcNow);
            Diplomacy.Propose(b, "A", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, w.UtcNow);

            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            Assert.True(Diplomacy.UnderTruce(a, "B", w.UtcNow));
            Assert.True(Diplomacy.UnderTruce(b, "A", w.UtcNow));
        }

        // Bilateral ProposePact forms a formal alliance — both factions recorded in PactWith.
        [Fact]
        public void BilateralAllianceProposal_FormsPact()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var a = new FactionState { Tag = "A" };
            var b = new FactionState { Tag = "B" };
            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, w.UtcNow);
            Diplomacy.Propose(b, "A", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, w.UtcNow);

            string pactA = null, pactB = null;
            Diplomacy.ProcessProposals(new[] { a, b }, w, t,
                onPact: (x, y) => { pactA = x; pactB = y; });

            Assert.True(Diplomacy.InPactWith(a, "B"), "A should be in a pact with B");
            Assert.True(Diplomacy.InPactWith(b, "A"), "B should be in a pact with A");
            Assert.Equal("A", pactA);
            Assert.Equal("B", pactB);
            Assert.False(a.Proposals.ContainsKey("B"), "proposals should be cleared after conclusion");
        }

        // Unilateral alliance proposal does not form a pact without the other side's response.
        [Fact]
        public void UnilateralAllianceProposal_DoesNotFormPact()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var a = new FactionState { Tag = "A" };
            var b = new FactionState { Tag = "B" };
            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, w.UtcNow);

            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            Assert.False(Diplomacy.InPactWith(a, "B"));
            Assert.True(a.Proposals.ContainsKey("B"), "pending proposal stays until accepted or TTL");
        }

        // Stale alliance proposal is pruned when PactOfferTtl is exceeded.
        [Fact]
        public void AllianceProposal_PrunedWhenStale()
        {
            var t = TestSupport.Tuning();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var w = new WorldModel { UtcNow = t0 };
            var a = new FactionState { Tag = "A" };
            var b = new FactionState { Tag = "B" };
            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } }, t0);

            w.UtcNow = t0 + t.PactOfferTtl + TimeSpan.FromSeconds(1);
            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            Assert.False(a.Proposals.ContainsKey("B"), "stale alliance proposal should be pruned");
        }

        // Brain generates ProposePact when relation is above AllyRelationThreshold − 0.1.
        [Fact]
        public void Brain_GeneratesProposePact_WhenRelationHigh()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.7, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.9; mine.Radius = 40;
            var his  = w.Graph.Add(new Vec3(20, 0, 0), 0.7, "B1"); his.OwnerFactionTag  = "B"; his.HoldStrength = 0.9; his.Radius  = 40;
            w.SetTreasury("A", 200000);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = t.AllyRelationThreshold + 0.05;  // above the trigger threshold

            var p = new Personality { Temperature = 0.5, Traits = new Traits { Mercantilism = 0.8, Aggression = 0.1 } };
            p.Weights[Drive.Security] = 2.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.ProposePact && c.Intent.TargetFactionTag == "B");
        }

        // Brain does NOT generate ProposePact when already in a pact with the partner.
        [Fact]
        public void Brain_DoesNotProposePact_WhenAlreadyAllied()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.7, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.9; mine.Radius = 40;
            var his  = w.Graph.Add(new Vec3(20, 0, 0), 0.7, "B1"); his.OwnerFactionTag  = "B"; his.HoldStrength = 0.9; his.Radius  = 40;
            w.SetTreasury("A", 200000);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = t.AllyRelationThreshold + 0.05;
            s.PactWith.Add("B");  // already allied

            var p = new Personality { Temperature = 0.5, Traits = new Traits { Mercantilism = 0.8, Aggression = 0.1 } };
            p.Weights[Drive.Security] = 2.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.ProposePact);
        }

        // Pact partner is skipped when generating Raid candidates.
        [Fact]
        public void PactPartner_NotRaided()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.9; mine.Radius = 40;
            var his  = w.Graph.Add(new Vec3(20, 0, 0), 0.7, "B1"); his.OwnerFactionTag  = "B"; his.HoldStrength = 0.5; his.Radius  = 40;
            w.SetTreasury("A", 200000);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;  // hostile — but allied
            s.PactWith.Add("B");

            var p = new Personality { Temperature = 0.5, Traits = new Traits { Aggression = 0.95 } };
            p.Weights[Drive.Rivalry] = 2.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.Null(CampaignNomination.Raid(d, w, "B1"));
        }

        // Forming a truce cancels pending Raid and Retaliate fleets targeting the new partner.
        [Fact]
        public void FormTruce_CancelsPendingRaidFleets()
        {
            var t = TestSupport.Tuning();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = 0.8;

            // A has a raid and a trade in flight toward B; B has a retaliate in flight toward A.
            a.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Raid,      ActorTag = "A", TargetTag = "B", DepartedUtc = now, EstimatedArrivalUtc = now.AddMinutes(10) });
            a.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Trade,     ActorTag = "A", TargetTag = "B", DepartedUtc = now, EstimatedArrivalUtc = now.AddMinutes(5)  });
            b.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Retaliate, ActorTag = "B", TargetTag = "A", DepartedUtc = now, EstimatedArrivalUtc = now.AddMinutes(8)  });

            // Both sides propose a truce and it is concluded.
            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            Diplomacy.Propose(b, "A", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            var w = new WorldModel { UtcNow = now };
            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            // Raid (A→B) and Retaliate (B→A) are cancelled; Trade (A→B) survives.
            Assert.DoesNotContain(a.PendingFleets, f => f.Kind == ActionKind.Raid);
            Assert.Contains(a.PendingFleets, f => f.Kind == ActionKind.Trade);
            Assert.DoesNotContain(b.PendingFleets, f => f.Kind == ActionKind.Retaliate);
        }

        // Forming a truce does NOT cancel fleets targeting a third faction.
        [Fact]
        public void FormTruce_DoesNotCancelFleets_AgainstThirdParty()
        {
            var t = TestSupport.Tuning();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = 0.8;

            // A has a raid toward C (third party) in flight.
            a.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "C", DepartedUtc = now, EstimatedArrivalUtc = now.AddMinutes(10) });

            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            Diplomacy.Propose(b, "A", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            var w = new WorldModel { UtcNow = now };
            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            Assert.Contains(a.PendingFleets, f => f.Kind == ActionKind.Raid && f.TargetTag == "C");
        }

        // An aggressive faction CAN generate a raid candidate against a truce partner (with penalty).
        [Fact]
        public void AggressiveFaction_CanGenerateTruceBreachRaid()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            // FakeWorld: ZeroField.OverrideSample = 0.5 ensures reach >= RaidReachThreshold.
            var w = new FakeWorld { Treasury = 200000 };
            ((ZeroField)w.Influence).OverrideSample = 0.5;
            w.Graph.Add(new Vec3(0,  0, 0), 0.6, "A1").OwnerFactionTag = "A";
            var b1 = w.Graph.Add(new Vec3(20, 0, 0), 0.9, "B1"); b1.OwnerFactionTag = "B"; b1.HoldStrength = 0.3; b1.Radius = 40;

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.8;
            s.TruceUntil["B"] = w.UtcNow.AddHours(1);

            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.95, Xenophobia = 0.9 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 3.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Assert.NotNull(CampaignNomination.Raid(d, w, "B1"));
        }

        // The truce-breach raid candidate has lower quality than a normal raid against the same target.
        [Fact]
        public void TruceBreachRaid_HasLowerQuality_ThanNormalRaid()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.95, Xenophobia = 0.9 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 3.0;

            // Scenario A: raiding under truce (breach) — penalty applied
            var wA = new FakeWorld { Treasury = 200000 };
            ((ZeroField)wA.Influence).OverrideSample = 0.5;
            wA.Graph.Add(new Vec3(0,  0, 0), 0.6, "A1").OwnerFactionTag = "A";
            var b1A = wA.Graph.Add(new Vec3(20, 0, 0), 0.9, "B1"); b1A.OwnerFactionTag = "B"; b1A.HoldStrength = 0.3; b1A.Radius = 40;
            var sA = new FactionState { Tag = "A", RngSeed = 1 };
            sA.Relations["B"] = -0.8;
            sA.TruceUntil["B"] = wA.UtcNow.AddHours(1);
            Decision dA = brain.Think(sA, p, wA, new SeededRandom(1), t);
            Candidate breachRaid = CampaignNomination.Raid(dA, wA, "B1");

            // Scenario B: same target, no truce
            var wB = new FakeWorld { Treasury = 200000 };
            ((ZeroField)wB.Influence).OverrideSample = 0.5;
            wB.Graph.Add(new Vec3(0,  0, 0), 0.6, "A1").OwnerFactionTag = "A";
            var b1B = wB.Graph.Add(new Vec3(20, 0, 0), 0.9, "B1"); b1B.OwnerFactionTag = "B"; b1B.HoldStrength = 0.3; b1B.Radius = 40;
            var sB = new FactionState { Tag = "A", RngSeed = 1 };
            sB.Relations["B"] = -0.8;
            Decision dB = brain.Think(sB, p, wB, new SeededRandom(1), t);
            Candidate normalRaid = CampaignNomination.Raid(dB, wB, "B1");

            Assert.NotNull(breachRaid);
            Assert.NotNull(normalRaid);
            Assert.True(breachRaid.Quality < normalRaid.Quality,
                "raiding a truce partner should produce a lower-quality candidate than the same raid without a truce");
        }

        // Trade is NOT generated against a faction below the hostile threshold.
        [Fact]
        public void Brain_DoesNotGenerateTrade_AgainstHostileFaction()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.7, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.9; mine.Radius = 40;
            var his  = w.Graph.Add(new Vec3(20, 0, 0), 0.7, "B1"); his.OwnerFactionTag  = "B"; his.HoldStrength  = 0.9; his.Radius  = 40;
            w.SetTreasury("A", 200000);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;             // actively hostile

            var p = Persona(0.05, 0.2, 0.9);     // mercantile
            p.Weights[Drive.Growth] = 2.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Trade);
        }
    }
}
