using System;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>War fatigue + the diplomatic option: sustained hostility tires both sides, a war-weary faction
    /// sues for peace, and when the other is also willing a truce concludes (relations reset to a wary peace,
    /// fatigue clears, a non-aggression window holds). This is what resolves a stalemate into diplomacy rather
    /// than letting the war spiral forever.</summary>
    public class WarFatigueTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static Personality Persona(double xeno, double aggr, double merc)
        {
            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = xeno, Aggression = aggr, Mercantilism = merc } };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        private static WorldModel TwoNeighbours(FactionTuning t)
        {
            var w = new WorldModel(new SeededRandom(1)) { Tuning = t, UtcNow = T0 };
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
            var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "B"; b.HoldStrength = 0.8; b.Radius = 30;
            return w;
        }

        // Fatigue stands while a pair is actively at war and what the war cost is still on the
        // record, and clears once the fighting stops.
        [Fact]
        public void Fatigue_StandsWhileHostile_ClearsAtPeace()
        {
            var t = TestSupport.Tuning();
            var w = TwoNeighbours(t);
            var s = new FactionState { Tag = "A" };
            s.Relations["B"] = -0.5;                       // hostile (below the hostile threshold)
            // "Actively at war" is three things: a sour standing, blows exchanged inside
            // WarFatigueActWindowSeconds (FAC-253), and a cost paid for them -- fatigue is derived
            // from combat power spent and not recovered, so the lost convoy is what it reads.
            FightLedger.Append(s, new FightRecord
            {
                Utc = w.UtcNow,
                Kind = ActionKind.Raid,
                Attacking = true,
                OpponentTag = "B",
                AttackerDps = 1000,
                AttackerRatio = 0,
                AttackerHullsLost = 1,
            }, 64);
            Diplomacy.HitFatigue(s, "B", t.RaidFatigue, w.UtcNow);

            Diplomacy.AccrueFatigue(s, w, t);
            Assert.True(s.FatigueTo("B") > 0, "a war that has cost hulls tires the faction that lost them");

            s.Relations["B"] = 0.0;                        // peace breaks out
            Diplomacy.AccrueFatigue(s, w, t);
            Assert.Equal(0.0, s.FatigueTo("B"), 9);
        }

        // A raid is exhausting — it bumps fatigue (the intensity term on top of the per-tick drip).
        [Fact]
        public void Raid_Tires_TheFaction()
        {
            var t = TestSupport.Tuning();
            var s = new FactionState { Tag = "A" };
            Diplomacy.HitFatigue(s, "B", t, T0);
            Assert.Equal(t.RaidFatigue, s.FatigueTo("B"), 6);
            Assert.Equal(T0, s.LastHostileActUtc["B"]);   // the blow is stamped, not just counted
        }

        // Mutual peace overtures conclude a truce: relations reset to the wary-peace baseline, fatigue clears,
        // and a non-aggression window opens for both sides.
        [Fact]
        public void MutualOvertures_ConcludeTruce()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = T0 };
            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = 0.8;
            Diplomacy.OfferPeace(a, "B", w.UtcNow);
            Diplomacy.OfferPeace(b, "A", w.UtcNow);

            Diplomacy.ConcludeTruces(new[] { a, b }, w, t);

            Assert.Equal(t.TruceRelation, a.RelationTo("B"), 6);
            Assert.Equal(t.TruceRelation, b.RelationTo("A"), 6);
            Assert.Equal(0.0, a.FatigueTo("B"), 6);
            Assert.Equal(0.0, b.FatigueTo("A"), 6);
            Assert.True(Diplomacy.UnderTruce(a, "B", w.UtcNow));
            Assert.True(Diplomacy.UnderTruce(b, "A", w.UtcNow));
        }

        // One side's overture is accepted by a partner who is fatigued enough — even without its own offer.
        [Fact]
        public void Overture_Accepted_WhenOtherIsFatigued()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = T0 };
            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = t.AcceptPeaceThreshold + 0.01;
            Diplomacy.OfferPeace(a, "B", w.UtcNow);        // only A offers; B has no offer but is weary enough

            Diplomacy.ConcludeTruces(new[] { a, b }, w, t);

            Assert.True(Diplomacy.UnderTruce(a, "B", w.UtcNow));
        }

        // An overture to an UNwilling partner does not make peace, and is pruned once it goes stale.
        [Fact]
        public void Overture_Ignored_WhenOtherUnwilling_ThenExpires()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = T0 };
            var a = new FactionState { Tag = "A" }; a.Relations["B"] = -0.9; a.Fatigue["B"] = 0.8;
            var b = new FactionState { Tag = "B" }; b.Relations["A"] = -0.9; b.Fatigue["A"] = 0.1; // not weary, no offer
            Diplomacy.OfferPeace(a, "B", w.UtcNow);

            Diplomacy.ConcludeTruces(new[] { a, b }, w, t);
            Assert.False(Diplomacy.UnderTruce(a, "B", w.UtcNow));
            Assert.True(a.Proposals.ContainsKey("B"));      // still on the table

            w.UtcNow = T0 + t.PeaceOfferTtl + TimeSpan.FromMinutes(1);
            Diplomacy.ConcludeTruces(new[] { a, b }, w, t);
            Assert.False(a.Proposals.ContainsKey("B"));     // pruned once stale
        }

        // A truce holds: border-pressure drift no longer sours the pair.
        [Fact]
        public void Truce_HoldsAgainstDrift()
        {
            var t = TestSupport.Tuning();
            var w = TwoNeighbours(t);
            var s = new FactionState { Tag = "A" };
            s.Relations["B"] = t.TruceRelation;
            s.TruceUntil["B"] = w.UtcNow + t.TruceDuration;

            for (int i = 0; i < 20; i++) Diplomacy.Drift(s, Persona(0.9, 0.8, 0.0), w, t); // a xenophobe under truce

            Assert.Equal(t.TruceRelation, s.RelationTo("B"), 6); // unchanged: the truce overrides souring
        }

        // The brain sues for peace when war-weary past the threshold (and not while a truce already holds).
        [Fact]
        public void Brain_SuesForPeace_WhenWarWeary()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = TwoNeighbours(t);
            w.SetTreasury("A", 100000);
            var mine = w.Graph.Nodes["A1"]; mine.Threat = 0.9;        // a real threat -> Security dominant -> conflict branch

            var s = new FactionState { Tag = "A", RngSeed = 1 };
            s.Relations["B"] = -0.7;                                  // hostile
            s.Fatigue["B"] = 0.8;                                     // war-weary past PeaceThreshold

            var p = Persona(0.6, 0.2, 0.6); p.Weights[Drive.Security] = 2.0;
            Decision d = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Negotiate && c.Intent.TargetFactionTag == "B");

            s.TruceUntil["B"] = w.UtcNow + t.TruceDuration;          // already at peace -> no overture
            Decision d2 = brain.Think(s, p, w, new SeededRandom(1), t);
            Assert.DoesNotContain(d2.Scored, c => c.Intent.Kind == ActionKind.Negotiate);
            // Truce-breach raids are now possible (quality penalty applies) — no DoesNotContain for Raid here.
        }
    }
}
