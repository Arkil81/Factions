using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Tests for brain audit P9 (grudge memory), P10 (embargo), and P12 (supply-chain chain bonus).</summary>
    public class GrudgeEmbargoChainTests
    {
        // ---- helpers ---------------------------------------------------------------------------

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

        private static Personality AggressorPersona()
        {
            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.9, Xenophobia = 0.8 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Rivalry] = 2.0;
            return p;
        }

        private static Personality MerchantPersona()
        {
            var p = new Personality { Temperature = 0.01, Traits = new Traits { Mercantilism = 0.9, Aggression = 0.1 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Growth] = 2.0;
            return p;
        }

        private static PendingFleetAction MakeRaid(string actor, string targetTag, string targetNodeId,
            DateTime departed, DateTime arrival)
            => new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = actor, TargetTag = targetTag, TargetNodeId = targetNodeId,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = departed, EstimatedArrivalUtc = arrival
            };

        private static IRandom Det() => new DetRandom();
        private sealed class DetRandom : IRandom { public double NextDouble() => 0.01; }

        // ========================================================================================
        // P9: Grudge memory
        // ========================================================================================

        // Raiding during a truce records a TruceBreached grudge on the VICTIM against the ATTACKER.
        [Fact]
        public void TruceBreach_RecordsGrudge_OnVictim()
        {
            var w = TestSupport.World();
            var t = w.Tuning;
            DateTime now = w.UtcNow;

            w.Graph.Add(new Vec3(0,  0, 0), 0.6, "A1").OwnerFactionTag = "A";
            w.Graph.Add(new Vec3(10, 0, 0), 0.6, "B1").OwnerFactionTag = "B";

            var stA = new FactionState { Tag = "A" };
            var stB = new FactionState { Tag = "B" };

            stA.TruceUntil["B"] = now.AddHours(1);
            stB.TruceUntil["A"] = now.AddHours(1);
            stA.PendingFleets.Add(MakeRaid("A", "B", "B1", now.AddMinutes(-1), now));

            var states = new List<FactionState> { stA, stB };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, t, Det(), log, null);

            Assert.True(stB.Grudges.ContainsKey("A"), "victim B should record a grudge against treaty-breaching attacker A");
            Assert.Equal(GrudgeKind.TruceBreached, stB.Grudges["A"].Kind);
            Assert.True(stB.Grudges["A"].ExpiresUtc > now, "grudge should have a future expiry");
            Assert.Contains(log, l => l.Contains("TRUCE_BREACH"));
        }

        // Raiding WITHOUT an active truce does NOT record a grudge.
        [Fact]
        public void Raid_WithoutTruce_DoesNotRecordGrudge()
        {
            var w = TestSupport.World();
            DateTime now = w.UtcNow;

            w.Graph.Add(new Vec3(0,  0, 0), 0.6, "A1").OwnerFactionTag = "A";
            w.Graph.Add(new Vec3(10, 0, 0), 0.6, "B1").OwnerFactionTag = "B";

            var stA = new FactionState { Tag = "A" };
            var stB = new FactionState { Tag = "B" };
            // No truce
            stA.PendingFleets.Add(MakeRaid("A", "B", "B1", now.AddMinutes(-1), now));

            var states = new List<FactionState> { stA, stB };
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, Det(), new List<string>(), null);

            Assert.False(stB.Grudges.ContainsKey("A"), "no grudge when raiding outside a truce");
        }

        // PruneStaleGrudges removes entries whose ExpiresUtc is in the past.
        [Fact]
        public void PruneStaleGrudges_RemovesExpiredEntries()
        {
            DateTime now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var s = new FactionState { Tag = "A" };

            s.Grudges["B"] = new GrudgeRecord { Kind = GrudgeKind.TruceBreached, ExpiresUtc = now.AddSeconds(-1) };
            s.Grudges["C"] = new GrudgeRecord { Kind = GrudgeKind.TruceBreached, ExpiresUtc = now.AddDays(1) };

            Diplomacy.PruneStaleGrudges(s, now);

            Assert.False(s.Grudges.ContainsKey("B"), "expired grudge should be pruned");
            Assert.True(s.Grudges.ContainsKey("C"), "active grudge should remain");
        }

        // Brain generates a higher-quality Raid candidate against a grudge-tagged target.
        [Fact]
        public void GrudgeTarget_HasHigherRaidQuality_ThanNonGrudge()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            t.GrudgeRaidQualityBoost = 0.15;
            t.RaidReachThreshold = 0;

            // Scenario A: A has a grudge against B
            FakeWorld wA = MakeFakeWorld(influenceSample: 0.5);
            AddNode(wA.Graph, "A1", "A", pos: new Vec3(0, 0, 0));
            AddNode(wA.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var sA = new FactionState { Tag = "A", RngSeed = 1 };
            sA.Relations["B"] = -0.7;
            sA.Grudges["B"] = new GrudgeRecord { Kind = GrudgeKind.TruceBreached, ExpiresUtc = wA.UtcNow.AddDays(7) };
            Decision dA = brain.Think(sA, AggressorPersona(), wA, Det(), t);
            Candidate raidWithGrudge = CampaignNomination.Raid(dA, wA, "B1");

            // Scenario B: same setup, no grudge
            FakeWorld wB = MakeFakeWorld(influenceSample: 0.5);
            AddNode(wB.Graph, "A1", "A", pos: new Vec3(0, 0, 0));
            AddNode(wB.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var sB = new FactionState { Tag = "A", RngSeed = 1 };
            sB.Relations["B"] = -0.7;
            Decision dB = brain.Think(sB, AggressorPersona(), wB, Det(), t);
            Candidate raidNoGrudge = CampaignNomination.Raid(dB, wB, "B1");

            Assert.NotNull(raidWithGrudge);
            Assert.NotNull(raidNoGrudge);
            Assert.True(raidWithGrudge.Quality > raidNoGrudge.Quality,
                "raid against a grudge target should have higher quality than against the same node without a grudge");
        }

        // Brain does NOT generate a Gift toward a grudge-tagged target.
        [Fact]
        public void GrudgeTarget_SkippedByGiftOffers()
        {
            var brain = new FactionBrain();
            var t = Tuning();

            FakeWorld w = MakeFakeWorld(influenceSample: 0.1);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0,  0, 0));
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.2;
            s.Grudges["B"] = new GrudgeRecord { Kind = GrudgeKind.TruceBreached, ExpiresUtc = w.UtcNow.AddDays(7) };

            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.1 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Security] = 2.0;

            Decision d = brain.Think(s, p, w, Det(), t);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Gift && c.Intent.TargetFactionTag == "B");
        }

        // Without a grudge, the same cold-war target DOES receive a Gift candidate.
        [Fact]
        public void ColdWarTarget_WithoutGrudge_ReceivesGiftCandidate()
        {
            var brain = new FactionBrain();
            var t = Tuning();

            FakeWorld w = MakeFakeWorld(influenceSample: 0.1);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0,  0, 0));
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.2;  // cold-war band, no grudge

            var p = new Personality { Temperature = 0.01, Traits = new Traits { Aggression = 0.1 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Security] = 2.0;

            Decision d = brain.Think(s, p, w, Det(), t);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Gift && c.Intent.TargetFactionTag == "B");
        }

        // Grudge persists through ToBrainDoc → ApplyBrainDoc round-trip.
        [Fact]
        public void Grudge_PersistsRoundTrip()
        {
            DateTime expiry = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var s = new FactionState { Tag = "A" };
            s.Grudges["B"] = new GrudgeRecord { Kind = GrudgeKind.TruceBreached, ExpiresUtc = expiry };

            BrainDoc doc = FactionStore.ToBrainDoc(s, Enumerable.Empty<SpatialNode>(), 0);
            Assert.NotNull(doc.Grudges);
            Assert.True(doc.Grudges.ContainsKey("B"));

            var s2 = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.True(s2.Grudges.ContainsKey("B"));
            Assert.Equal(GrudgeKind.TruceBreached, s2.Grudges["B"].Kind);
            Assert.Equal(expiry, s2.Grudges["B"].ExpiresUtc);
        }

        // ========================================================================================
        // P10: Embargo mechanism
        // ========================================================================================

        // When relation is in the cold-war band (below EmbargoRelationThreshold), brain auto-adds embargo.
        [Fact]
        public void AutoEmbargo_AddedWhenRelationBelowThreshold()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            Assert.True(t.EmbargoRelationThreshold < 0, "EmbargoRelationThreshold should be negative");

            FakeWorld w = MakeFakeWorld(influenceSample: 0.3);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0,  0, 0));
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = t.EmbargoRelationThreshold - 0.01;  // just below threshold

            brain.Think(s, MerchantPersona(), w, Det(), t);

            Assert.True(s.Embargoes.Contains("B"), "relation below embargo threshold should trigger auto-embargo");
            Assert.DoesNotContain(s.TradePriority, e => e.PartnerTag == "B");
        }

        // When relation recovers above threshold + hysteresis, embargo is lifted.
        [Fact]
        public void AutoEmbargo_LiftedWhenRelationRecovers()
        {
            var brain = new FactionBrain();
            var t = Tuning();

            FakeWorld w = MakeFakeWorld(influenceSample: 0.3);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0,  0, 0));
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Embargoes.Add("B");  // embargo was previously placed
            s.Relations["B"] = t.EmbargoRelationThreshold + t.EmbargoHysteresis + 0.01;  // recovered

            brain.Think(s, MerchantPersona(), w, Det(), t);

            Assert.False(s.Embargoes.Contains("B"), "embargo should be lifted when relation recovers above threshold + hysteresis");
            Assert.Contains(s.TradePriority, e => e.PartnerTag == "B");
        }

        // An explicitly placed embargo blocks trade priority even in the hysteresis band (neither auto-add nor auto-lift).
        [Fact]
        public void Embargo_BlocksTradePartner()
        {
            var brain = new FactionBrain();
            var t = Tuning();

            FakeWorld w = MakeFakeWorld(influenceSample: 0.5);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0,  0, 0));
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            // Relation is in the hysteresis band: below auto-lift threshold but above auto-add threshold.
            // This means the auto-lift code (rel > threshold + hysteresis) does NOT fire, so a manually
            // placed embargo should remain in force and suppress the trade lane.
            s.Relations["B"] = t.EmbargoRelationThreshold + t.EmbargoHysteresis * 0.5;
            s.Embargoes.Add("B");     // manual embargo, stable in hysteresis band

            brain.Think(s, MerchantPersona(), w, Det(), t);

            Assert.DoesNotContain(s.TradePriority, e => e.PartnerTag == "B");
        }

        // Without an embargo, a neutral partner appears in TradePriority.
        [Fact]
        public void NoEmbargo_NeutralPartner_AppearsInTradePriority()
        {
            var brain = new FactionBrain();
            var t = Tuning();

            FakeWorld w = MakeFakeWorld(influenceSample: 0.5);
            AddNode(w.Graph, "A1", "A", pos: new Vec3(0,  0, 0));
            AddNode(w.Graph, "B1", "B", pos: new Vec3(10, 0, 0));

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = 0.0;  // neutral, no embargo

            brain.Think(s, MerchantPersona(), w, Det(), t);

            Assert.Contains(s.TradePriority, e => e.PartnerTag == "B");
        }

        // Embargo persists through ToBrainDoc → ApplyBrainDoc round-trip.
        [Fact]
        public void Embargo_PersistsRoundTrip()
        {
            var s = new FactionState { Tag = "A" };
            s.Embargoes.Add("B");
            s.Embargoes.Add("C");

            BrainDoc doc = FactionStore.ToBrainDoc(s, Enumerable.Empty<SpatialNode>(), 0);
            Assert.NotNull(doc.Embargoes);
            Assert.Contains("B", doc.Embargoes);
            Assert.Contains("C", doc.Embargoes);

            var s2 = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Contains("B", s2.Embargoes);
            Assert.Contains("C", s2.Embargoes);
        }

        // ========================================================================================
        // P12: Supply-chain chain bonus
        // ========================================================================================

        // Installing Manufactory adjacent to an owned Refinery gets higher quality than an isolated install.
        [Fact]
        public void ChainBonus_IncreasesDevQuality_WhenAdjacentUpstreamExists()
        {
            var brain = new FactionBrain();
            var t = Tuning();
            t.DevelopChainBonus = 0.15;

            var p = new Personality { Temperature = 0.01, Traits = new Traits { Mercantilism = 0.8 } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            p.Weights[Drive.Growth] = 2.0;

            // Scenario A: N2 is adjacent to N1 (which has Refinery) → chain bonus
            FakeWorld wA = MakeFakeWorld(influenceSample: 1.0);

            SpatialNode n1A = wA.Graph.Add(new Vec3(0, 0, 0), 0.8, "N1");
            n1A.OwnerFactionTag = "A"; n1A.HoldStrength = 1.0; n1A.StableStationId = 1001;
            n1A.Capabilities[NodeCapabilityKind.Refinery] = 1;

            SpatialNode n2A = wA.Graph.Add(new Vec3(10, 0, 0), 0.8, "N2");
            n2A.OwnerFactionTag = "A"; n2A.HoldStrength = 1.0; n2A.StableStationId = 1002;
            wA.Graph.Connect("N1", "N2");

            var sA = new FactionState { Tag = "A", RngSeed = 1 };
            Decision dA = brain.Think(sA, p, wA, Det(), t);
            // The chain bonus should make the best Develop candidate score higher.
            // We compare the best Develop quality rather than insisting on a specific node,
            // since the scoring pass selects the highest-scoring (node, cap) pair.
            Candidate withChain = dA.Scored
                .Where(c => c.Intent.Kind == ActionKind.Develop)
                .OrderByDescending(c => c.Quality)
                .FirstOrDefault();

            // Scenario B: N3 alone, no Refinery neighbor → no chain bonus for any cap
            FakeWorld wB = MakeFakeWorld(influenceSample: 1.0);

            SpatialNode n3B = wB.Graph.Add(new Vec3(0, 0, 0), 0.8, "N3");
            n3B.OwnerFactionTag = "A"; n3B.HoldStrength = 1.0; n3B.StableStationId = 1003;

            var sB = new FactionState { Tag = "A", RngSeed = 1 };
            Decision dB = brain.Think(sB, p, wB, Det(), t);
            Candidate noChain = dB.Scored
                .Where(c => c.Intent.Kind == ActionKind.Develop)
                .OrderByDescending(c => c.Quality)
                .FirstOrDefault();

            Assert.NotNull(withChain);
            Assert.NotNull(noChain);
            Assert.True(withChain.Quality > noChain.Quality,
                "best Develop candidate adjacent to Refinery should have higher quality than an isolated install");
        }
    }
}
