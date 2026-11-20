using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Tests for <see cref="ActionKind.Conquer"/> — the finishing-move territorial capture action.
    /// Covers brain candidate generation (hold-ceiling gate, diplomatic gates shared with Raid,
    /// grudge/truce-breach interplay, trait sensitivity) and the in-flight-fleet / truce-cancellation
    /// contracts a new fleet-dispatched ActionKind must respect. Modeled on the equivalent Raid
    /// coverage in BrainTests.cs, DiplomacyTests.cs, and GrudgeEmbargoChainTests.cs.
    /// </summary>
    public class ConquerTests
    {
        // ---- helpers (mirrors BrainTests.cs / GrudgeEmbargoChainTests.cs patterns) -------------

        private static FactionTuning Tuning() => TestSupport.Tuning();

        private static FakeWorld MakeFakeWorld(double influenceSample = 0.5, long treasury = 500_000)
        {
            var w = new FakeWorld { Treasury = treasury };
            ((ZeroField)w.Influence).OverrideSample = influenceSample;
            return w;
        }

        private static SpatialNode AddNode(SpatialGraph g, string id, string owner,
            double hold = 0.9, double value = 0.6, Vec3? pos = null)
        {
            SpatialNode n = g.Add(pos ?? new Vec3(0, 0, 0), value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static Personality AggressorPersona(double aggression = 0.9)
        {
            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = aggression, Xenophobia = 0.8 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 2.0;
            return p;
        }

        private static IRandom Det() => new DetRandom();
        private sealed class DetRandom : IRandom { public double NextDouble() => 0.01; }

        // ========================================================================================
        // AddConquers: hold-ceiling gate
        // ========================================================================================

        [Fact]
        public void AddConquers_TargetAboveHoldCeiling_NotGenerated()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            t.ConquerHoldCeiling = 0.15;
            t.RaidReachThreshold = 0;

            FakeWorld w = MakeFakeWorld(influenceSample: 0.5);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0, 0, 0));
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.9); // above ceiling

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;

            Decision d = brain.Think(s, AggressorPersona(), w, Det(), t);

            // The hold ceiling is no longer a nomination gate: a conquest campaign against a healthy
            // region is exactly what the softening phase is for, and FleetManager sends Raid until a
            // node falls to or under the ceiling (see OffensiveCampaignDispatchTests). What the
            // ceiling still does here is rank it: a fully-held target is worth less than a broken one.
            Candidate healthy = CampaignNomination.Conquest(d, w, "B1");
            Assert.NotNull(healthy);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Conquer);   // never a dispatch intent

            FakeWorld weakWorld = MakeFakeWorld(influenceSample: 0.5);
            AddNode(weakWorld.Graph, "A1", "A", pos: new Vec3(0, 0, 0));
            AddNode(weakWorld.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10); // at/below ceiling
            var weakState = new FactionState { Tag = "A", RngSeed = 1 };
            weakState.Relations["B"] = -0.7;
            Candidate broken = CampaignNomination.Conquest(
                brain.Think(weakState, AggressorPersona(), weakWorld, Det(), t), weakWorld, "B1");
            Assert.NotNull(broken);
            Assert.True(broken.Quality > healthy.Quality,
                "a broken target must rank above a fully-held one even though both are nominated");
        }

        [Fact]
        public void AddConquers_TargetAtOrBelowHoldCeiling_Generated()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            t.ConquerHoldCeiling = 0.15;
            t.RaidReachThreshold = 0;

            FakeWorld w = MakeFakeWorld(influenceSample: 0.5);
            SpatialNode home = AddNode(w.Graph, "A1", "A", pos: new Vec3(0, 0, 0));
            home.ShipStock["Raid"] = 1; // ship-stock gate: Conquer shares role "Raid" with Raid/Retaliate
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10); // at/below ceiling

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;

            Decision d = brain.Think(s, AggressorPersona(), w, Det(), t);

            Candidate conquest = CampaignNomination.Conquest(d, w, "B1");
            Assert.NotNull(conquest);
            Assert.True(conquest.Utility > 0);
        }

        // ========================================================================================
        // Diplomatic gates shared with Raid
        // ========================================================================================

        [Fact]
        public void PactPartner_NotConquered()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TestSupport.World();
            w.Tuning.ConquerHoldCeiling = 0.15;
            var mine = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "A1"); mine.OwnerFactionTag = "A"; mine.HoldStrength = 0.9; mine.Radius = 40;
            var his = w.Graph.Add(new Vec3(20, 0, 0), 0.7, "B1"); his.OwnerFactionTag = "B"; his.HoldStrength = 0.10; his.Radius = 40;
            w.SetTreasury("A", 200000);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;  // hostile — but allied
            s.PactWith.Add("B");

            var p = new Personality { Temperature = 0.5, Traits = new Traits { Aggression = 0.95 } };
            p.Weights[Drive.Rivalry] = 2.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.Null(CampaignNomination.Conquest(d, w, "B1"));
        }

        [Fact]
        public void FormTruce_CancelsPendingConquerFleets()
        {
            var t = TestSupport.Tuning();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = 0.8;

            // A has a conquer and a trade in flight toward B.
            a.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Conquer, ActorTag = "A", TargetTag = "B", DepartedUtc = now, EstimatedArrivalUtc = now.AddMinutes(10) });
            a.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Trade,   ActorTag = "A", TargetTag = "B", DepartedUtc = now, EstimatedArrivalUtc = now.AddMinutes(5) });

            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            Diplomacy.Propose(b, "A", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            var w = new WorldModel { UtcNow = now };
            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            // Conquer (A->B) is cancelled; Trade (A->B) survives.
            Assert.DoesNotContain(a.PendingFleets, f => f.Kind == ActionKind.Conquer);
            Assert.Contains(a.PendingFleets, f => f.Kind == ActionKind.Trade);
        }

        [Fact]
        public void FormTruce_DoesNotCancelConquerFleets_AgainstThirdParty()
        {
            var t = TestSupport.Tuning();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = 0.8;

            // A has a conquer toward C (third party) in flight.
            a.PendingFleets.Add(new PendingFleetAction { Kind = ActionKind.Conquer, ActorTag = "A", TargetTag = "C", DepartedUtc = now, EstimatedArrivalUtc = now.AddMinutes(10) });

            Diplomacy.Propose(a, "B", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            Diplomacy.Propose(b, "A", new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, now);
            var w = new WorldModel { UtcNow = now };
            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            Assert.Contains(a.PendingFleets, f => f.Kind == ActionKind.Conquer && f.TargetTag == "C");
        }

        [Fact]
        public void AggressiveFaction_CanGenerateTruceBreachConquer()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            t.ConquerHoldCeiling = 0.15;

            var w = new FakeWorld { Treasury = 200000 };
            ((ZeroField)w.Influence).OverrideSample = 0.5;
            w.Graph.Add(new Vec3(0, 0, 0), 0.6, "A1").OwnerFactionTag = "A";
            var b1 = w.Graph.Add(new Vec3(20, 0, 0), 0.9, "B1"); b1.OwnerFactionTag = "B"; b1.HoldStrength = 0.10; b1.Radius = 40;

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.8;
            s.TruceUntil["B"] = w.UtcNow.AddHours(1);

            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.95, Xenophobia = 0.9 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 3.0;

            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);

            Assert.NotNull(CampaignNomination.Conquest(d, w, "B1"));
        }

        [Fact]
        public void TruceBreachConquer_HasLowerQuality_ThanNormalConquer()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            t.ConquerHoldCeiling = 0.15;
            t.ConquerTruceBreachQualityPenalty = 0.15;

            // Scenario A: truce partner (penalized)
            var wA = new FakeWorld { Treasury = 200000 };
            ((ZeroField)wA.Influence).OverrideSample = 0.5;
            wA.Graph.Add(new Vec3(0, 0, 0), 0.6, "A1").OwnerFactionTag = "A";
            wA.Graph.Nodes["A1"].ShipStock["Raid"] = 1;
            var b1A = wA.Graph.Add(new Vec3(20, 0, 0), 0.9, "B1"); b1A.OwnerFactionTag = "B"; b1A.HoldStrength = 0.10; b1A.Radius = 40;
            var sA = new FactionState { Tag = "A", RngSeed = 1 };
            sA.Relations["B"] = -0.8;
            sA.TruceUntil["B"] = wA.UtcNow.AddHours(1);
            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.95, Xenophobia = 0.9 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 3.0;
            Decision dA = brain.Think(sA, p, wA, new SeededRandom(1), t);
            Candidate truceConquer = CampaignNomination.Conquest(dA, wA, "B1");

            // Scenario B: same setup, no truce
            var wB = new FakeWorld { Treasury = 200000 };
            ((ZeroField)wB.Influence).OverrideSample = 0.5;
            wB.Graph.Add(new Vec3(0, 0, 0), 0.6, "A1").OwnerFactionTag = "A";
            wB.Graph.Nodes["A1"].ShipStock["Raid"] = 1;
            var b1B = wB.Graph.Add(new Vec3(20, 0, 0), 0.9, "B1"); b1B.OwnerFactionTag = "B"; b1B.HoldStrength = 0.10; b1B.Radius = 40;
            var sB = new FactionState { Tag = "A", RngSeed = 1 };
            sB.Relations["B"] = -0.8;
            Decision dB = brain.Think(sB, p, wB, new SeededRandom(1), t);
            Candidate normalConquer = CampaignNomination.Conquest(dB, wB, "B1");

            Assert.NotNull(truceConquer);
            Assert.NotNull(normalConquer);
            Assert.True(truceConquer.Utility < normalConquer.Utility,
                "a truce-breaching Conquer candidate must score lower than an equivalent non-truce Conquer");
        }

        // ========================================================================================
        // Grudge boost
        // ========================================================================================

        [Fact]
        public void GrudgeTarget_HasHigherConquerQuality_ThanNonGrudge()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            t.GrudgeRaidQualityBoost = 0.15;
            t.RaidReachThreshold = 0;
            t.ConquerHoldCeiling = 0.15;

            // Scenario A: A has a grudge against B
            FakeWorld wA = MakeFakeWorld(influenceSample: 0.5);
            AddNode(wA.Graph, "A1", "A", pos: new Vec3(0, 0, 0)).ShipStock["Raid"] = 1;
            AddNode(wA.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10);

            var sA = new FactionState { Tag = "A", RngSeed = 1 };
            sA.Relations["B"] = -0.7;
            sA.Grudges["B"] = new GrudgeRecord { Kind = GrudgeKind.TruceBreached, ExpiresUtc = wA.UtcNow.AddDays(7) };
            Decision dA = brain.Think(sA, AggressorPersona(), wA, Det(), t);
            Candidate conquerWithGrudge = CampaignNomination.Conquest(dA, wA, "B1");

            // Scenario B: same setup, no grudge
            FakeWorld wB = MakeFakeWorld(influenceSample: 0.5);
            AddNode(wB.Graph, "A1", "A", pos: new Vec3(0, 0, 0)).ShipStock["Raid"] = 1;
            AddNode(wB.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10);

            var sB = new FactionState { Tag = "A", RngSeed = 1 };
            sB.Relations["B"] = -0.7;
            Decision dB = brain.Think(sB, AggressorPersona(), wB, Det(), t);
            Candidate conquerNoGrudge = CampaignNomination.Conquest(dB, wB, "B1");

            Assert.NotNull(conquerWithGrudge);
            Assert.NotNull(conquerNoGrudge);
            Assert.True(conquerWithGrudge.Utility > conquerNoGrudge.Utility,
                "a grudge-tagged target should score a higher-quality Conquer candidate");
        }

        // ========================================================================================
        // Lane weight (FAC-483 slice 6): a region whose space costs our lanes is worth taking
        // ========================================================================================

        [Fact]
        public void ARegionThatCostsOurLanes_HasHigherConquerQuality_AndTheIntentCarriesTheBoost()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            t.RaidReachThreshold = 0;
            t.ConquerHoldCeiling = 0.15;
            t.ConquerCost = 40000;

            FakeWorld wA = MakeFakeWorld(influenceSample: 0.5);
            AddNode(wA.Graph, "A1", "A", pos: new Vec3(0, 0, 0)).ShipStock["Raid"] = 1;
            SpatialNode b1 = AddNode(wA.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10);
            var sA = new FactionState { Tag = "A", RngSeed = 1 };
            sA.Relations["B"] = -0.7;
            sA.TransitDenial.Add("B", FactionBrain.CampaignKeyFor(NodeRegion.Of(b1), b1), 40000, wA.UtcNow);
            Candidate withLane = CampaignNomination.Conquest(brain.Think(sA, AggressorPersona(), wA, Det(), t), wA, "B1");

            FakeWorld wB = MakeFakeWorld(influenceSample: 0.5);
            AddNode(wB.Graph, "A1", "A", pos: new Vec3(0, 0, 0)).ShipStock["Raid"] = 1;
            AddNode(wB.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10);
            var sB = new FactionState { Tag = "A", RngSeed = 1 };
            sB.Relations["B"] = -0.7;
            Candidate without = CampaignNomination.Conquest(brain.Think(sB, AggressorPersona(), wB, Det(), t), wB, "B1");

            Assert.NotNull(withLane);
            Assert.NotNull(without);
            Assert.True(withLane.Quality > without.Quality, "the lane weight must raise the conquest's quality");
            Assert.Equal(t.LaneConquestQualityBoost / 2, withLane.Intent.LaneBoost, 6);
            Assert.Equal(40000.0, withLane.Intent.LaneCredits, 6);
            Assert.Equal(0.0, without.Intent.LaneBoost);
        }

        // ========================================================================================
        // TraitBias: Conquer should scale with Aggression more steeply than Raid
        // ========================================================================================

        [Fact]
        public void TraitBias_Conquer_ScalesWithAggressionMoreSteeplyThanRaid()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            t.RaidReachThreshold = 0;
            t.ConquerHoldCeiling = 0.5; // generous ceiling so both Raid and Conquer are candidates

            // Both nominations come out of the SAME Think, so Need, Afford, the campaign cooldown
            // and the repetition gate are identical between them and cancel in the ratio; the only
            // term left that moves with aggression is TraitBias, and the quality ratio between the
            // two candidates does not move with it at all. So the conquest-to-raid utility ratio
            // rising with aggression IS the steeper curve, with nothing else able to produce it.
            double ConquestOverRaidAt(double aggression)
            {
                FakeWorld w = MakeFakeWorld(influenceSample: 0.5);
                SpatialNode home = AddNode(w.Graph, "A1", "A", pos: new Vec3(0, 0, 0));
                home.ShipStock["Raid"] = 1;
                AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.3);
                var s = new FactionState { Tag = "A", RngSeed = 1 };
                s.Relations["B"] = -0.7;
                Decision d = brain.Think(s, AggressorPersona(aggression), w, Det(), t);
                Candidate raid = CampaignNomination.Raid(d, w, "B1");
                Candidate conquest = CampaignNomination.Conquest(d, w, "B1");
                Assert.NotNull(raid);
                Assert.NotNull(conquest);
                Assert.True(raid.Utility > 0, "the loser has to compete or the ratio proves nothing");
                return conquest.Utility / raid.Utility;
            }

            Assert.True(ConquestOverRaidAt(1.0) > ConquestOverRaidAt(0.0),
                "Conquer's aggression-driven bias must climb more steeply than Raid's");
        }

        // ========================================================================================
        // Concurrency: stock, not an in-flight-uniqueness rule, gates a second Conquer/Raid
        // ========================================================================================

        [Fact]
        public void Conquer_SecondCandidate_NotBlockedByFirstInFlight_WhenStockRemains()
        {
            // Ship stock (not "one in flight per kind") is the concurrency limiter now — with a
            // Raid-role ship still in stock, a second Conquer candidate must score > 0 even while
            // an earlier Conquer is already unresolved in PendingFleets. (Before this change, this
            // exact setup was blocked by HasInFlightFleet regardless of stock — that rule is gone.)
            var brain = new FactionBrain();
            var t = Tuning();
            t.RaidReachThreshold = 0;
            t.ConquerHoldCeiling = 0.15;

            FakeWorld w = MakeFakeWorld(influenceSample: 0.5);
            SpatialNode home = AddNode(w.Graph, "A1", "A", pos: new Vec3(0, 0, 0));
            home.ShipStock["Raid"] = 1; // still stocked — the first Conquer's dispatch already
                                        // debited its own ship in a real run; this fake world just
                                        // asserts scoring, so stock here represents "still available"
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10);
            AddNode(w.Graph, "C1", "C", pos: new Vec3(15, 0, 0), hold: 0.10);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;
            s.Relations["C"] = -0.7;
            // An unresolved Conquer fleet is already in flight toward B.
            s.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Conquer, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(10), Resolved = false
            });

            Decision d = brain.Think(s, AggressorPersona(), w, Det(), t);

            Assert.Contains(CampaignNomination.AllConquests(d), c => c.Utility > 0);
        }

        [Fact]
        public void ConquestCampaign_StillNominated_WithZeroShipStock_AndNoInFlightUniqueness()
        {
            // Same in-flight setup as above, with zero Raid-role stock network-wide. Deciding to take
            // a region costs no hull, so the nomination stands; what an empty yard blocks is the
            // dispatch, and that gate is FleetManager's readiness check
            // (OffensiveCampaignDispatchTests.Dispatch_IsBlocked_WhenNoShipStockExists).
            var brain = new FactionBrain();
            var t = Tuning();
            t.RaidReachThreshold = 0;
            t.ConquerHoldCeiling = 0.15;

            FakeWorld w = MakeFakeWorld(influenceSample: 0.5);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0, 0, 0)); // no ShipStock at all
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0), hold: 0.10);
            AddNode(w.Graph, "C1", "C", pos: new Vec3(15, 0, 0), hold: 0.10);

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;
            s.Relations["C"] = -0.7;
            s.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Conquer, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(10), Resolved = false
            });

            Decision d = brain.Think(s, AggressorPersona(), w, Det(), t);

            Assert.Contains(CampaignNomination.AllConquests(d), c => c.Utility > 0);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Conquer);
        }
    }
}
