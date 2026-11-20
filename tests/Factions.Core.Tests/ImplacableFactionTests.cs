using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The <see cref="Traits.Implacable"/> trait carried by the <c>factorum</c> profile: a faction
    /// that holds every other faction at -1 permanently and cannot be moved off it by drift, trade, gift,
    /// truce or exhaustion.
    ///
    /// <para>Every case runs the identical fixture twice — once implacable, once not — and asserts both
    /// halves. The control is what makes a green assertion mean the trait did it: without it, a fixture
    /// that never reaches the code at all would pass every "did not happen" check.</para></summary>
    public class ImplacableFactionTests
    {
        private static Personality Implacable()
        {
            Personality p = Personalities.Get("factorum");
            Assert.True(p.Traits.Implacable, "the factorum profile is the one that carries the trait");
            return p;
        }

        /// <summary>The same profile with the trait off — the control differs from the subject in exactly
        /// the one field under test, so nothing else about factorum (its weights, its xenophobia) can
        /// explain a difference between them.</summary>
        private static Personality NotImplacable()
        {
            Personality p = Personalities.Get("factorum");
            p.Traits.Implacable = false;
            return p;
        }

        private static FactionState State(bool implacable)
        {
            return new FactionState { Tag = "A", RngSeed = 1, PersonalityId = implacable ? "factorum" : "settler" };
        }

        /// <summary>Two neighbouring estates, close enough that the border-pressure path has something to
        /// work with — so the control actually drifts and the subject's pin is visibly a different rule.</summary>
        private static WorldModel Border()
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
            var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "B"; b.HoldStrength = 0.8; b.Radius = 30;
            return w;
        }

        // Pinned, not eased: ONE pass, not the twenty the drift path needs to get anywhere near the floor.
        [Fact]
        public void Drift_PinsImplacableStandingAtMinusOne_InASinglePass()
        {
            var t = TestSupport.Tuning();
            FactionState subject = State(true), control = State(false);

            Diplomacy.Drift(subject, Implacable(), Border(), t);
            Diplomacy.Drift(control, NotImplacable(), Border(), t);

            Assert.Equal(Diplomacy.ImplacableRelation, subject.RelationTo("B"), 6);
            Assert.True(control.RelationTo("B") > Diplomacy.ImplacableRelation + 0.5,
                "control: without the trait one pass eases the standing a fraction of the way, nowhere near the floor");
        }

        // A warm standing and an open truce are both overwritten — the pin outranks the truce hold.
        [Fact]
        public void Drift_PinsThroughAWarmStandingAndAnOpenTruce()
        {
            var t = TestSupport.Tuning();
            WorldModel wS = Border(), wC = Border();
            FactionState subject = State(true), control = State(false);
            foreach (FactionState s in new[] { subject, control }) s.Relations["B"] = 0.9;
            subject.TruceUntil["B"] = wS.UtcNow.AddHours(1);
            control.TruceUntil["B"] = wC.UtcNow.AddHours(1);

            Diplomacy.Drift(subject, Implacable(), wS, t);
            Diplomacy.Drift(control, NotImplacable(), wC, t);

            Assert.Equal(Diplomacy.ImplacableRelation, subject.RelationTo("B"), 6);
            Assert.Equal(0.9, control.RelationTo("B"), 6);   // control: a truce holds the standing where it was
        }

        // Trade warmth and an incoming gift both bounce off, and neither reaches the earned ledger either.
        [Fact]
        public void NoTradeOrGiftLiftsAnImplacableFaction()
        {
            var t = TestSupport.Tuning();
            FactionState subject = State(true), control = State(false);
            foreach (FactionState s in new[] { subject, control })
            {
                s.Relations["B"] = Diplomacy.ImplacableRelation;
                Diplomacy.LiftRelation(s, "B", t);      // a trade this faction chose
                Diplomacy.GiftRelation(s, "B", t);      // a gift convoy that arrived from B
            }

            Assert.Equal(Diplomacy.ImplacableRelation, subject.RelationTo("B"), 6);
            Assert.False(subject.EarnedRelations.ContainsKey("B"),
                "nothing accrues to the ledger either, so there is no goodwill banked to cash in later");
            Assert.True(control.RelationTo("B") > Diplomacy.ImplacableRelation,
                "control: without the trait the same trade and gift do warm the standing");
        }

        // The block is one-directional: hostility still registers on an implacable faction.
        [Fact]
        public void HitRelation_StillSoursAnImplacableFaction()
        {
            var t = TestSupport.Tuning();
            var subject = State(true);
            subject.Relations["B"] = 0.0;

            Diplomacy.HitRelation(subject, "B", t);

            Assert.True(subject.RelationTo("B") < 0.0, "a raid must still register — only goodwill is refused");
        }

        // Both sides war-weary and both offering: the normal path concludes a truce, this one cannot.
        [Theory]
        [InlineData(true, false)]   // the proposer is implacable
        [InlineData(false, true)]   // the party being asked is implacable
        public void NoTruceConcludes_WhenEitherSideIsImplacable(bool aImplacable, bool bImplacable)
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            FactionState a = Pair(w, "A", "B", aImplacable), b = Pair(w, "B", "A", bImplacable);

            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            Assert.False(Diplomacy.UnderTruce(a, "B", w.UtcNow));
            Assert.False(Diplomacy.UnderTruce(b, "A", w.UtcNow));
            Assert.False(a.Proposals.ContainsKey("B"), "the offer is dropped rather than left to age out");

            // Control: the same fixture with neither side implacable does conclude.
            var wc = new WorldModel { UtcNow = w.UtcNow };
            FactionState ca = Pair(wc, "A", "B", false), cb = Pair(wc, "B", "A", false);
            Diplomacy.ProcessProposals(new[] { ca, cb }, wc, t);
            Assert.True(Diplomacy.UnderTruce(ca, "B", wc.UtcNow), "control: a war-weary bilateral offer settles");
        }

        private static FactionState Pair(WorldModel w, string tag, string otherTag, bool implacable)
        {
            var s = new FactionState { Tag = tag, PersonalityId = implacable ? "factorum" : "settler" };
            s.Relations[otherTag] = -0.9;
            s.Fatigue[otherTag] = 0.8;
            Diplomacy.Propose(s, otherTag, new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, w.UtcNow);
            return s;
        }

        // An alliance is refused on the same terms, from the other end of the scale.
        [Fact]
        public void NoPactForms_WithAnImplacableFaction()
        {
            var t = TestSupport.Tuning();
            var w = new WorldModel { UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var a = new FactionState { Tag = "A", PersonalityId = "factorum" };
            var b = new FactionState { Tag = "B", PersonalityId = "settler" };
            var alliance = new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Alliance } };
            Diplomacy.Propose(a, "B", alliance, w.UtcNow);
            Diplomacy.Propose(b, "A", alliance, w.UtcNow);

            Diplomacy.ProcessProposals(new[] { a, b }, w, t);

            Assert.False(Diplomacy.InPactWith(a, "B"));
            Assert.False(Diplomacy.InPactWith(b, "A"));
        }

        // War-weariness is what eventually buys peace; a faction that can never take that deal must not
        // bank it either, or it saturates at the cap and cools its own Rivalry drive into passivity.
        [Fact]
        public void AccruesNoWarFatigue_AndDrainsWhatWasPersisted()
        {
            var t = TestSupport.Tuning();
            WorldModel wS = Border(), wC = Border();
            FactionState subject = State(true), control = State(false);
            foreach (var pair in new[] { Tuple.Create(subject, wS), Tuple.Create(control, wC) })
            {
                pair.Item1.Relations["B"] = Diplomacy.ImplacableRelation;   // well under the hostile threshold
                pair.Item1.LastHostileActUtc["B"] = pair.Item2.UtcNow;      // blows exchanged just now
                pair.Item1.Fatigue["B"] = 0.5;
                // Weariness is derived from what the war cost, so the control arm needs a real loss
                // to tire from. The subject must stay at nothing despite carrying the same one.
                FightLedger.Append(pair.Item1, new FightRecord
                {
                    Utc = pair.Item2.UtcNow,
                    Kind = ActionKind.Raid,
                    Attacking = true,
                    OpponentTag = "B",
                    AttackerDps = 1000,
                    AttackerRatio = 0,
                    AttackerHullsLost = 1,
                }, 64);
            }

            Diplomacy.AccrueFatigue(subject, wS, t);
            Diplomacy.AccrueFatigue(control, wC, t);

            Assert.True(subject.FatigueTo("B") < 0.5, "persisted fatigue drains instead of climbing");
            Assert.True(control.FatigueTo("B") > 0.5, "control: an active war does tire a faction that can tire");
        }

        // ...and the brain does not spend a Negotiate on an overture that could never be accepted.
        [Fact]
        public void Brain_GeneratesNoPeaceOverture_ForAnImplacableFaction()
        {
            var t = TestSupport.Tuning();

            Decision subject = Think(State(true), Implacable(), t);
            Decision control = Think(State(false), NotImplacable(), t);

            Assert.DoesNotContain(subject.Scored, c => c.Intent.Kind == ActionKind.Negotiate);
            Assert.Contains(control.Scored, c => c.Intent.Kind == ActionKind.Negotiate && c.Intent.TargetFactionTag == "B");
        }

        private static Decision Think(FactionState s, Personality p, FactionTuning t)
        {
            WorldModel w = Border();
            w.SetTreasury("A", 5_000_000);
            s.Relations["B"] = -0.9;
            s.Fatigue["B"] = 0.9;                 // well past PeaceThreshold: the overture's own trigger
            return new FactionBrain().Think(s, p, w, new SeededRandom(1), t);
        }
    }
}
