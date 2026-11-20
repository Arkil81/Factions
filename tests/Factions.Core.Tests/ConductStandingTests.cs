using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-249: an NPC-vs-NPC standing is what the two have DONE to each other, biased by
    /// where they currently stand — not a pure function of present border geometry.
    ///
    /// <para>Measured live 2026-09-04 before the fix: recomputing the old pressure-only target
    /// reproduced the whole relation matrix to a median residual of 0.002, and IMPR's standing toward
    /// SPRT read <b>+0.041</b> — it liked its raider — while its conduct ledger toward SPRT read
    /// <b>-0.412</b>. The ledger was already written by every act (`Diplomacy.ApplyAct`) and already
    /// persisted; `Drift` simply never consulted it on the NPC path.</para></summary>
    public class ConductStandingTests
    {
        private static Personality Persona(double xeno = 0.3, double aggr = 0.3, double merc = 0.4)
        {
            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = xeno, Aggression = aggr, Mercantilism = merc } };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1;
            return p;
        }

        /// <summary>Two factions sharing one border, with a gap wide enough that pressure is real but
        /// nowhere near total — the band where conduct has to be what decides.</summary>
        private static WorldModel Bordering()
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
            var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "B"; b.HoldStrength = 0.8; b.Radius = 30;
            return w;
        }

        private static double Settle(FactionState s, Personality p, FactionTuning t, int passes = 400)
        {
            for (int i = 0; i < passes; i++) Diplomacy.Drift(s, p, Bordering(), t);
            return s.RelationTo("B");
        }

        // ---- conduct is remembered ---------------------------------------------------------------

        /// <summary>The headline. A faction that has been raided must not drift back to liking its
        /// attacker just because the border is quiet. Under the old pressure-only target this settled
        /// at the same value as a faction that had never been touched.</summary>
        [Fact]
        public void ARaidedFaction_DoesNotDriftBackToNeutral()
        {
            var t = TestSupport.Tuning();

            var untouched = new FactionState { Tag = "A" };
            double untouchedSettled = Settle(untouched, Persona(), t);

            var raided = new FactionState { Tag = "A" };
            for (int i = 0; i < 6; i++) Diplomacy.HitRelation(raided, "B", t);   // six raids on the record
            double raidedSettled = Settle(raided, Persona(), t);

            Assert.True(raidedSettled < untouchedSettled - 0.2,
                "a raid victim must settle well below a faction that was never touched; raided="
                + raidedSettled + " untouched=" + untouchedSettled);
            Assert.True(raided.EarnedLedgerTo("B") < -0.2, "the ledger itself must carry the raids");
        }

        /// <summary>The other direction, and the reason `TradeRelationBoost` was inert: warmth has to
        /// accumulate. Under the old target a thousand convoys settled exactly where zero convoys did.</summary>
        [Fact]
        public void TradeWarmth_Accumulates_AndSurvivesTheDrift()
        {
            var t = TestSupport.Tuning();

            var stranger = new FactionState { Tag = "A" };
            double strangerSettled = Settle(stranger, Persona(), t);

            var partner = new FactionState { Tag = "A" };
            for (int i = 0; i < 500; i++) Diplomacy.LiftRelation(partner, "B", t);
            double partnerSettled = Settle(partner, Persona(), t);

            Assert.True(partnerSettled > strangerSettled + 0.2,
                "500 resolved convoys must leave a standing clearly above a stranger's; partner="
                + partnerSettled + " stranger=" + strangerSettled);
        }

        /// <summary>FAC-249 part 2: an NPC alliance was structurally unreachable. The old target's
        /// warmth ceiling was `Mercantilism * 0.3` — at most +0.195, for a personality that does not
        /// exist alongside `AllyRelationThreshold` 0.4. A long trading relationship must be able to
        /// cross it, which is what makes `FactionBrain`'s ProposePact candidate reachable at all.</summary>
        [Fact]
        public void ALongTradingRelationship_CanReachTheAllyThreshold()
        {
            var t = TestSupport.Tuning();
            Assert.True(t.AllyRelationThreshold > 0.195,
                "this test is only meaningful while the ally threshold sits above the OLD warmth ceiling");

            var partner = new FactionState { Tag = "A" };
            for (int i = 0; i < 900; i++) Diplomacy.LiftRelation(partner, "B", t);

            // A merchant, because that is who accumulates 900 convoys: its hostile coefficient is the
            // smallest of any personality, so its bias eats least of the record. A xenophobe with the
            // same ledger correctly stays short of the threshold across a hard border -- allying with
            // someone leaning on you should cost more than allying with someone who is not.
            double settled = Settle(partner, Persona(xeno: 0.0, aggr: 0.1, merc: 0.9), t);
            Assert.True(settled >= t.AllyRelationThreshold,
                "a merchant with a long positive record must be able to become an ally, got " + settled);
        }

        // ---- the bias stays a bias ----------------------------------------------------------------

        /// <summary>Border tension still moves the standing — this is a bias on conduct, not its
        /// removal. Same empty ledger, more pressure, lower standing.</summary>
        [Fact]
        public void BorderPressure_StillMovesAStandingWithNoConductBehindIt()
        {
            var t = TestSupport.Tuning();

            WorldModel FarApart()
            {
                var w = TestSupport.World();
                var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
                var b = w.Graph.Add(new Vec3(500, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "B"; b.HoldStrength = 0.8; b.Radius = 30;
                return w;   // beyond the 4r cutoff, so neither reaches the other at all
            }

            var quiet = new FactionState { Tag = "A" };
            for (int i = 0; i < 400; i++) Diplomacy.Drift(quiet, Persona(0.9, 0.8, 0.1), FarApart(), t);

            var pressed = new FactionState { Tag = "A" };
            double pressedSettled = Settle(pressed, Persona(0.9, 0.8, 0.1), t);

            Assert.True(pressedSettled < quiet.RelationTo("B"),
                "a xenophobe pressed at the border must still read worse than one left alone; pressed="
                + pressedSettled + " quiet=" + quiet.RelationTo("B"));
        }

        /// <summary>`MaxBorderPressureStandingBias` is what keeps the situational term a bias. Its own
        /// unclamped range reaches about -1.45 for a xenophobic aggressor at full pressure, which is
        /// enough to bury any ledger; the cap is the guard against conduct becoming irrelevant again
        /// the moment pressure climbs.</summary>
        [Fact]
        public void TheBiasCap_BoundsHowFarGeographyCanOverrideTheRecord()
        {
            var t = TestSupport.Tuning();
            t.MaxBorderPressureStandingBias = 0.1;
            var tight = new FactionState { Tag = "A" };
            for (int i = 0; i < 500; i++) Diplomacy.LiftRelation(tight, "B", t);
            double withTightCap = Settle(tight, Persona(0.9, 0.8, 0.1), t);

            t.MaxBorderPressureStandingBias = 1.0;
            var loose = new FactionState { Tag = "A" };
            for (int i = 0; i < 500; i++) Diplomacy.LiftRelation(loose, "B", t);
            double withLooseCap = Settle(loose, Persona(0.9, 0.8, 0.1), t);

            Assert.True(withTightCap > withLooseCap + 0.1,
                "a tighter cap must leave more of the trading record standing; tight=" + withTightCap
                + " loose=" + withLooseCap);
        }

        /// <summary>The ratchet this fix could easily have shipped. `EarnedRelationTo` falls back to the
        /// CURRENT standing when the ledger has no entry, so a target of `ledger + bias` built on it
        /// reads back the value it just wrote and walks by `bias` every pass until it clamps. A pair
        /// with no history must settle at the bias, not run away to it.</summary>
        [Fact]
        public void APairWithNoHistory_SettlesAtTheBias_AndDoesNotRatchet()
        {
            var t = TestSupport.Tuning();
            var s = new FactionState { Tag = "A" };
            var p = Persona();

            // Both reads are taken well past convergence (0.9^400 is ~1e-19 of the gap). Comparing a
            // still-converging early read against a settled one measures the easing, not a ratchet.
            for (int i = 0; i < 400; i++) Diplomacy.Drift(s, p, Bordering(), t);
            double settled = s.RelationTo("B");
            for (int i = 0; i < 4000; i++) Diplomacy.Drift(s, p, Bordering(), t);
            double late = s.RelationTo("B");

            Assert.Equal(settled, late, 9);
            Assert.True(Math.Abs(late) < t.MaxBorderPressureStandingBias + 1e-9,
                "with an empty ledger the standing cannot exceed the bias cap, got " + late);
            Assert.Equal(0.0, s.EarnedLedgerTo("B"), 9);
        }

        /// <summary>`EarnedLedgerTo` is deliberately not `EarnedRelationTo`: same question, different
        /// answer for an absent entry, and only one of them is safe to build a target from.</summary>
        [Fact]
        public void EarnedLedgerTo_ReadsZeroForAnAbsentEntry_WhileEarnedRelationToFallsBack()
        {
            var s = new FactionState { Tag = "A" };
            s.Relations["B"] = -0.4;

            Assert.Equal(0.0, s.EarnedLedgerTo("B"), 9);
            Assert.Equal(-0.4, s.EarnedRelationTo("B"), 9);

            s.EarnedRelations["B"] = -0.1;
            Assert.Equal(-0.1, s.EarnedLedgerTo("B"), 9);
        }

        // ---- fatigue is weariness with someone in particular ---------------------------------------

        /// <summary>FAC-249 part 3, the slice taken here. `Sense` damped the whole Rivalry drive by the
        /// MAXIMUM fatigue over every pair, so being worn out by one neighbour silenced rivalry toward
        /// an enemy this faction had never fought — the same max-over-everything aggregation error
        /// FAC-248 removed from border pressure. The damping is now paired with the rival Rivalry is
        /// actually about.
        ///
        /// <para>This matters more after the conduct fix above, not less: a ledger is permanent, so a
        /// pair that goes hostile STAYS hostile, and under the old global peak one exhausting war
        /// would have muted a faction's rivalry for good.</para></summary>
        [Fact]
        public void RivalryDamping_ReadsFatigueTowardTheWorstEnemy_NotThePeakOverAll()
        {
            var t = TestSupport.Tuning();

            FactionState Faction()
            {
                var s = new FactionState { Tag = "A", PersonalityId = "raider" };
                s.Relations["ENEMY"] = -0.9;    // the rival Rivalry is about
                s.Relations["OLDWAR"] = -0.1;   // barely disliked, but this is who we are tired of
                return s;
            }

            var tiredOfTheWrongOne = Faction();
            tiredOfTheWrongOne.Fatigue["OLDWAR"] = 1.0;   // exhausted, but not by ENEMY

            var tiredOfTheRightOne = Faction();
            tiredOfTheRightOne.Fatigue["ENEMY"] = 1.0;

            var world = TestSupport.World();
            var n = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); n.OwnerFactionTag = "A"; n.HoldStrength = 1.0;
            Personality raider = Personalities.Get("raider");
            var brain = new FactionBrain();

            brain.Think(tiredOfTheWrongOne, raider, world, new SeededRandom(1), t);
            brain.Think(tiredOfTheRightOne, raider, world, new SeededRandom(1), t);

            double stillAngry = tiredOfTheWrongOne.Drives[Drive.Rivalry];
            double genuinelyTired = tiredOfTheRightOne.Drives[Drive.Rivalry];

            Assert.True(stillAngry > genuinelyTired + 0.2,
                "exhaustion with one neighbour must not silence rivalry toward a different, unfought enemy; "
                + "tired-of-OLDWAR=" + stillAngry + " tired-of-ENEMY=" + genuinelyTired);
        }
    }
}
