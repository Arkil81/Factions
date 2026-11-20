using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Retaliate answers a recent attack, read from the fight ledger (FAC-275 slice 2). A defending
    /// <see cref="FightRecord"/> inside the war-fatigue act window creates the candidate whatever
    /// the faction thinks of its attacker; the older path — a threatened node of our own plus a
    /// hostile owner — is untouched, and every other gate still applies.
    /// </summary>
    public class RetaliateLedgerTests
    {
        private const string Us = "F";
        private const string Raider = "RVN";

        private static Personality Merchant()
        {
            var p = new Personality
            {
                Temperature = 0.5,
                Traits = new Traits { Xenophobia = 0.1, Expansionism = 0.5, Mercantilism = 0.9, Aggression = 0.1 }
            };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                RetaliateThreatThreshold = 0.25,
                RaidReachThreshold = 0.01,
                HostileRelationThreshold = -0.25,
                RaidCost = 12_000,
                IdleUtility = 0.01,
                RaidThreat = 0.3,
                WarFatigueActWindowSeconds = 3600.0,   // one hour of memory, so "inside/outside" is explicit
            };
        }

        /// <summary>A world where <see cref="Us"/> holds one calm, ship-stocked node and every
        /// query reaches everywhere.</summary>
        private static FakeWorld WorldWithCalmHome()
        {
            var w = new FakeWorld { Treasury = 500_000 };
            ((ZeroField)w.Influence).OverrideSample = 1.0;
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "H");
            home.OwnerFactionTag = Us;
            home.HoldStrength = 0.8;
            home.Threat = 0.0;                  // nothing landed: the defence held
            home.ShipStock["Raid"] = 1;         // the ship-stock gate Retaliate scores through
            return w;
        }

        /// <summary>The standing a victim holds toward an attacker after ONE repelled raid: the
        /// single <c>RaidRelationHit</c> slice 1 files, well above <c>HostileRelationThreshold</c>.
        /// This is what "neutral" means for this ticket — the acceptance line asks for a retaliation
        /// from a victim whose standing is still at or above -0.3.</summary>
        private const double AfterOneRepelledRaid = -0.05;

        private static SpatialNode AddRival(FakeWorld w, string id, string owner, double x)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, 0, 0), 0.7, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.8;
            return n;
        }

        /// <summary>The defending copy of a fight record — what an intercept files on the victim.</summary>
        private static void RecordDefence(FactionState s, string attackerTag, DateTime utc)
        {
            FightLedger.Append(s, new FightRecord
            {
                Utc = utc,
                Kind = ActionKind.Raid,
                Attacking = false,
                OpponentTag = attackerTag,
                NodeId = "H",
                AttackerDps = 2500,
                DefenderDps = 9000,
                AttackerRatio = 0,
                AttackerHullsLost = 1,
            }, 48);
        }

        private static Decision Think(FakeWorld w, FactionState s, FactionTuning t)
        {
            return new FactionBrain().Think(s, Merchant(), w, new SeededRandom(1), t);
        }

        // The headline: a civilian that beat off a raid may answer it, with no standing quarrel and
        // no threat left on the node it defended.
        [Fact]
        public void ADefendingRecordInsideTheWindow_CreatesRetaliate_AgainstANeutralAttacker()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            AddRival(w, "R1", Raider, 500);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations[Raider] = AfterOneRepelledRaid;
            Assert.True(s.RelationTo(Raider) > t.HostileRelationThreshold,
                        "the standing has to be ABOVE the hostility gate or this proves nothing");
            RecordDefence(s, Raider, w.UtcNow.AddMinutes(-10));

            Decision d = Think(w, s, t);

            Candidate c = Assert.Single(CampaignNomination.AllRetaliations(d));
            Assert.True(CampaignNomination.Covers(c.Intent, w, "R1"));
            Assert.True(c.Utility > 0, "a retaliation nobody would ever pick is not an answer");
        }

        // The floor on the severity term: with no threatened node the quality is what a landed raid
        // would have made it, so a repelled attack scores like a fresh one.
        [Fact]
        public void WithNoThreatenedNode_TheSeverityTermFallsBackToRaidThreat()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            AddRival(w, "R1", Raider, 500);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations[Raider] = AfterOneRepelledRaid;
            RecordDefence(s, Raider, w.UtcNow.AddMinutes(-10));

            Candidate c = Assert.Single(CampaignNomination.AllRetaliations(Think(w, s, t)));
            Assert.Equal(0.0, w.Graph.Nodes["H"].Threat);   // the fallback is the branch actually taken
            Assert.Equal(0.5 + 0.5 * t.RaidThreat, c.Quality, 6);
        }

        [Fact]
        public void ADefendingRecordOlderThanTheWindow_CreatesNothing()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            AddRival(w, "R1", Raider, 500);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations[Raider] = AfterOneRepelledRaid;
            DateTime stale = w.UtcNow.AddSeconds(-(t.WarFatigueActWindowSeconds + 60));
            RecordDefence(s, Raider, stale);
            Assert.True((w.UtcNow - stale).TotalSeconds > t.WarFatigueActWindowSeconds,
                        "the record has to be outside the window for this test to mean anything");

            Assert.Empty(CampaignNomination.AllRetaliations(Think(w, s, t)));
        }

        // The pre-existing rule, unchanged: no record and no hatred is no candidate.
        [Fact]
        public void NoRecordAndANeutralStanding_CreatesNothing()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            AddRival(w, "R1", Raider, 500);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations[Raider] = AfterOneRepelledRaid;
            Assert.Empty(s.Fights);

            Assert.Empty(CampaignNomination.AllRetaliations(Think(w, s, t)));
        }

        // An ally that shot at us is a diplomatic problem, not a target.
        [Fact]
        public void APactPartner_IsNeverRetaliatedAgainst_EvenWithARecord()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            AddRival(w, "R1", Raider, 500);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations[Raider] = AfterOneRepelledRaid;
            s.PactWith.Add(Raider);
            RecordDefence(s, Raider, w.UtcNow.AddMinutes(-10));

            Assert.Empty(CampaignNomination.AllRetaliations(Think(w, s, t)));
        }

        [Fact]
        public void ATrucePartner_IsNeverRetaliatedAgainst_EvenWithARecord()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            AddRival(w, "R1", Raider, 500);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations[Raider] = AfterOneRepelledRaid;
            s.TruceUntil[Raider] = w.UtcNow.AddHours(1);
            RecordDefence(s, Raider, w.UtcNow.AddMinutes(-10));

            Assert.Empty(CampaignNomination.AllRetaliations(Think(w, s, t)));
        }

        [Fact]
        public void AFullyFatiguedEnemy_IsStillAnsweredButRanksLower()
        {
            FactionTuning t = Tuning();

            // Same scenario twice, differing only in weariness, so the comparison is of that and
            // nothing else. A faction ground down by an enemy is exactly the one that must still be
            // able to nominate hitting back -- a hard fatigue gate made being conquered the reason
            // you could not answer.
            FakeWorld fresh = WorldWithCalmHome();
            AddRival(fresh, "R1", Raider, 500);
            var rested = new FactionState { Tag = Us, RngSeed = 1 };
            rested.Relations[Raider] = AfterOneRepelledRaid;
            RecordDefence(rested, Raider, fresh.UtcNow.AddMinutes(-10));

            FakeWorld spentWorld = WorldWithCalmHome();
            AddRival(spentWorld, "R1", Raider, 500);
            var spent = new FactionState { Tag = Us, RngSeed = 1 };
            spent.Relations[Raider] = AfterOneRepelledRaid;
            spent.Fatigue[Raider] = 1.0;
            RecordDefence(spent, Raider, spentWorld.UtcNow.AddMinutes(-10));

            var restedNoms = CampaignNomination.AllRetaliations(Think(fresh, rested, t));
            var spentNoms = CampaignNomination.AllRetaliations(Think(spentWorld, spent, t));

            Assert.NotEmpty(restedNoms);
            Assert.NotEmpty(spentNoms);
            Assert.True(spentNoms[0].Quality < restedNoms[0].Quality,
                "weariness must weigh on the answer; rested=" + restedNoms[0].Quality
                + " spent=" + spentNoms[0].Quality);
        }

        // Two attackers, two identical nodes, and the neutral one is offered FIRST — so without the
        // hostile boost it wins the tie on arrival order and this test fails.
        [Fact]
        public void AHostileAttacker_OutranksANeutralOne_WhenBothAttackedRecently()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            SpatialNode neutralNode = AddRival(w, "N1", Raider, 500);
            SpatialNode hostileNode = AddRival(w, "H1", "GRIM", 600);
            Assert.Equal(neutralNode.ResourceValue, hostileNode.ResourceValue);
            Assert.Equal(neutralNode.HoldStrength, hostileNode.HoldStrength);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations["GRIM"] = -0.9;                 // below HostileRelationThreshold
            s.Relations[Raider] = AfterOneRepelledRaid;
            RecordDefence(s, Raider, w.UtcNow.AddMinutes(-10));
            RecordDefence(s, "GRIM", w.UtcNow.AddMinutes(-10));

            Candidate c = Assert.Single(CampaignNomination.AllRetaliations(Think(w, s, t)));
            Assert.True(CampaignNomination.Covers(c.Intent, w, "H1"),
                        "the hostile attacker's region must be the one answered");
        }

        // The path FAC-275 must not disturb: a threatened node plus a hostile owner, with nothing
        // in the ledger at all.
        [Fact]
        public void TheThreatenedNodePath_StillWorksWithAnEmptyLedger()
        {
            FactionTuning t = Tuning();
            FakeWorld w = WorldWithCalmHome();
            w.Graph.Nodes["H"].Threat = 0.4;            // above RetaliateThreatThreshold
            AddRival(w, "R1", Raider, 500);

            var s = new FactionState { Tag = Us, RngSeed = 1 };
            s.Relations[Raider] = -0.5;
            Assert.Empty(s.Fights);

            Candidate c = Assert.Single(CampaignNomination.AllRetaliations(Think(w, s, t)));
            Assert.Equal(0.5 + 0.5 * 0.4, c.Quality, 6);   // severity still comes from the node
        }
    }
}
