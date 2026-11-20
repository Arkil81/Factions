using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The two-level reputation model (docs/player-progression.md → Reputation and standing): the
    /// activity-weighted member aggregate, the farm-proof trade accumulator, and the one place the
    /// individual level reaches the faction level — <see cref="Diplomacy.Drift"/>.
    /// </summary>
    public class PlayerReputationTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        private static MemberReputation Member(double standing, double daysAgo, long id = 1)
        {
            return new MemberReputation
            {
                IdentityId = id,
                Standing = standing,
                LastSeenUtc = Now.AddDays(-daysAgo),
            };
        }

        // ---- the member aggregate ---------------------------------------------------------------

        // The Consumer -> Magnate transition must not change how the world treats you. With one
        // member the mean IS the worst, so the blend weight cannot matter -- and it must not start
        // mattering just because that player has been away for a while.
        [Theory]
        [InlineData(0.0)]
        [InlineData(30.0)]
        [InlineData(400.0)]
        public void SoloMagnate_IsJudgedOnOwnConduct_AlphaDropsOut(double daysAway)
        {
            var roster = new List<MemberReputation> { Member(-0.6, daysAway) };

            double atZero, atHalf, atOne;
            Assert.True(MemberStandingAggregate.TryCompute(roster, Now, 0.0, 14.0, out atZero));
            Assert.True(MemberStandingAggregate.TryCompute(roster, Now, 0.5, 14.0, out atHalf));
            Assert.True(MemberStandingAggregate.TryCompute(roster, Now, 1.0, 14.0, out atOne));

            Assert.Equal(-0.6, atZero, 9);
            Assert.Equal(-0.6, atHalf, 9);
            Assert.Equal(-0.6, atOne, 9);
        }

        // "A faction repairs its standing by removing the offender" -- and short of removing them,
        // an offender who stops showing up while their peers keep playing fades on their own.
        [Fact]
        public void AbsentOffender_FadesRelativeToActivePeers()
        {
            var fresh = new List<MemberReputation> { Member(-1.0, 0, 1), Member(0.8, 0, 2) };
            var stale = new List<MemberReputation> { Member(-1.0, 90, 1), Member(0.8, 0, 2) };
            var removed = new List<MemberReputation> { Member(0.8, 0, 2) };

            double withFresh, withStale, withoutThem;
            Assert.True(MemberStandingAggregate.TryCompute(fresh, Now, 0.5, 14.0, out withFresh));
            Assert.True(MemberStandingAggregate.TryCompute(stale, Now, 0.5, 14.0, out withStale));
            Assert.True(MemberStandingAggregate.TryCompute(removed, Now, 0.5, 14.0, out withoutThem));

            Assert.True(withStale > withFresh,
                "an offender who stopped showing up should weigh less than one still active");
            Assert.True(withoutThem > withStale,
                "kicking them should still beat merely waiting them out");
        }

        // Absence must not manufacture conduct. Fading an idle member toward NEUTRAL made a
        // well-regarded member who simply stopped logging in the roster's minimum, so a faction of
        // two good members read 45% worse than the same two both active -- for conduct nobody
        // engaged in. Reported by a peer session against the first cut of this function.
        [Fact]
        public void IdleGoodMember_DoesNotDragTheFactionDown()
        {
            var bothActive = new List<MemberReputation> { Member(0.9, 0, 1), Member(0.9, 0, 2) };
            var oneIdle = new List<MemberReputation> { Member(0.9, 0, 1), Member(0.9, 23, 2) };

            double active, idle;
            Assert.True(MemberStandingAggregate.TryCompute(bothActive, Now, 0.5, 7.0, out active));
            Assert.True(MemberStandingAggregate.TryCompute(oneIdle, Now, 0.5, 7.0, out idle));

            Assert.Equal(0.9, active, 9);
            Assert.Equal(active, idle, 9);
        }

        // The same, in the direction that must still work: absence is not a laundry service either.
        // An idle member converges on their faction, so a lone offender among good peers softens --
        // but never past what removing them achieves.
        [Fact]
        public void AbsenceConvergesOnTheFaction_InBothDirections()
        {
            var offenderActive = new List<MemberReputation> { Member(-1.0, 0, 1), Member(0.8, 0, 2) };
            var offenderIdle = new List<MemberReputation> { Member(-1.0, 90, 1), Member(0.8, 0, 2) };
            var offenderGone = new List<MemberReputation> { Member(0.8, 0, 2) };

            double act, idle, gone;
            Assert.True(MemberStandingAggregate.TryCompute(offenderActive, Now, 0.5, 14.0, out act));
            Assert.True(MemberStandingAggregate.TryCompute(offenderIdle, Now, 0.5, 14.0, out idle));
            Assert.True(MemberStandingAggregate.TryCompute(offenderGone, Now, 0.5, 14.0, out gone));

            Assert.True(act < idle, "an absent offender should weigh less than an active one");
            Assert.True(idle < gone, "but never as little as one who has actually been removed");
        }

        // Zero activity HOLDS, it does not reset. A wholly dormant faction is still judged on what
        // its members did -- resetting to neutral would be a second, accidental laundering path.
        [Fact]
        public void WhollyDormantFaction_HoldsItsStanding_DoesNotResetToNeutral()
        {
            var roster = new List<MemberReputation> { Member(-0.9, 3000, 1), Member(-0.7, 3000, 2) };

            double aggregate;
            Assert.True(MemberStandingAggregate.TryCompute(roster, Now, 0.5, 14.0, out aggregate));
            Assert.True(aggregate < -0.5,
                "a dormant roster of offenders must keep reading as hostile, not drift to neutral");
        }

        // The genuinely undefined case: nobody to judge. The caller holds its last value.
        [Fact]
        public void EmptyRoster_ReportsNothingToCompute()
        {
            double aggregate;
            Assert.False(MemberStandingAggregate.TryCompute(new List<MemberReputation>(), Now, 0.5, 14.0, out aggregate));
            Assert.False(MemberStandingAggregate.TryCompute(null, Now, 0.5, 14.0, out aggregate));
        }

        // alpha is a real dial everywhere except the one-member case above.
        [Fact]
        public void Alpha_MovesBetweenMeanAndWorst_OnAMixedRoster()
        {
            var roster = new List<MemberReputation> { Member(-1.0, 0, 1), Member(1.0, 0, 2) };

            double byWorst, byMean;
            Assert.True(MemberStandingAggregate.TryCompute(roster, Now, 0.0, 14.0, out byWorst));
            Assert.True(MemberStandingAggregate.TryCompute(roster, Now, 1.0, 14.0, out byMean));

            Assert.Equal(-1.0, byWorst, 9);
            Assert.Equal(0.0, byMean, 9);
        }

        // ---- the trade accumulator --------------------------------------------------------------

        private static TradeReputationLedger NewLedger(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "fac-rep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return new TradeReputationLedger(dir);
        }

        // ---- the shipped rate, which no test above pins ------------------------------------------

        /// <summary>Every ledger test below passes its own threshold, because they are about the
        /// accumulator's mechanics. That leaves the number a live server actually runs on covered by
        /// nothing — so this pins it, and pins it as the RATE rather than as the knob, because the
        /// rate is what the operator asked for and it is a quotient of two fields.
        /// <para>Operator, 2026-08-30: take the trade boost to 5% of what it was.</para></summary>
        [Fact]
        public void TheShippedTradeRate_IsFivePercentOfWhatItWas()
        {
            FactionTuning t = TestSupport.Tuning();

            const double PreviousStep = 25.0;
            const double PreviousThreshold = 50000.0;
            double previousRate = PreviousStep / PreviousThreshold;
            double shippedRate = t.TradeReputationStep / t.TradeReputationThreshold;

            Assert.Equal(previousRate * 0.05, shippedRate, 12);
        }

        /// <summary>The rate above is a quotient, so it can be hit by moving either field — and one
        /// of them was deliberately left alone. <c>TradeReputationStep</c> is an <c>int</c>: 5% of 25
        /// is 1.25, which it cannot hold, and rounding to 1 would both miss the rate and multiply the
        /// granularity of a standing climb by twenty-five. The whole cut therefore belongs on the
        /// threshold, and this is what says so.</summary>
        [Fact]
        public void TheRateCut_LandsOnTheThreshold_AndLeavesTheStepAlone()
        {
            FactionTuning t = TestSupport.Tuning();

            Assert.Equal(25, t.TradeReputationStep);
            Assert.Equal(1000000.0, t.TradeReputationThreshold, 6);
        }

        /// <summary>What the rate means in the units an operator reads it in: a neutral-to-friendly
        /// climb is twenty crossings of the threshold, and each crossing is now worth twenty times
        /// the trade it was. Stated as a computation rather than a comment so it cannot drift away
        /// from the constants above.</summary>
        [Fact]
        public void AFullStandingClimb_CostsTwentyMillionInWeightedTrade()
        {
            FactionTuning t = TestSupport.Tuning();
            const int CrossingsForAFullClimb = 20;   // 20 x 25 = 500 points on SE's -1500..1500 scale

            double weightedTrade = CrossingsForAFullClimb * t.TradeReputationThreshold;

            Assert.Equal(20000000.0, weightedTrade, 6);
            // And a pure buyer pays for it at the consumption weight, so it costs them more still.
            Assert.True(weightedTrade / t.TradeConsumptionWeight > weightedTrade,
                "consumption must be weighted below provision, or buying is the cheaper climb");
        }

        /// <summary>A new key arrives on its C# initialiser; a CHANGED one never does. All eight
        /// player-reputation keys shipped as new and are persisted now, so every live tuning.json
        /// carries the old 50,000 and would keep it forever without an explicit line in
        /// <c>ApplyVersionedContractDefaults</c>. This is that line's test.</summary>
        [Fact]
        public void ATradeThresholdPersistedByAnOlderBuild_IsMigratedRatherThanKept()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 39, \"TradeReputationThreshold\": 50000.0 }");

            Assert.Equal(58, t.TuneVersion);
            Assert.Equal(1000000.0, t.TradeReputationThreshold, 6);
        }

        // The whole reason this is an accumulator: one 100k trade and four 25k trades must be worth
        // exactly the same standing, or a player farms it by splitting.
        [Fact]
        public void SplittingATrade_GrantsTheSameSteps()
        {
            string dirA, dirB;
            TradeReputationLedger whole = NewLedger(out dirA);
            TradeReputationLedger split = NewLedger(out dirB);
            try
            {
                int wholeSteps = whole.Accrue(7, "EXP", 100000.0, 50000.0, Now);

                int splitSteps = 0;
                for (int i = 0; i < 4; i++) splitSteps += split.Accrue(7, "EXP", 25000.0, 50000.0, Now);

                Assert.Equal(2, wholeSteps);
                Assert.Equal(wholeSteps, splitSteps);
            }
            finally { Directory.Delete(dirA, true); Directory.Delete(dirB, true); }
        }

        // The remainder carries: 3 x 20k against a 50k threshold is one step, with 10k banked toward
        // the next -- not zero (each trade under threshold) and not three.
        [Fact]
        public void RemainderCarriesBetweenSteps()
        {
            string dir;
            TradeReputationLedger led = NewLedger(out dir);
            try
            {
                int steps = 0;
                for (int i = 0; i < 3; i++) steps += led.Accrue(7, "EXP", 20000.0, 50000.0, Now);
                Assert.Equal(1, steps);
                Assert.Equal(10000.0, led.RemainderFor(7, "EXP"), 6);
                Assert.Equal(60000.0, led.LifetimeFor(7, "EXP"), 6);
            }
            finally { Directory.Delete(dir, true); }
        }

        // An unattributed transaction still moves goods and credits upstream; it must simply credit
        // nobody. Same for a world that has not configured a threshold.
        [Fact]
        public void UnattributedOrUnconfigured_AccruesNothing()
        {
            string dir;
            TradeReputationLedger led = NewLedger(out dir);
            try
            {
                Assert.Equal(0, led.Accrue(0, "EXP", 1000000.0, 50000.0, Now));
                Assert.Equal(0, led.Accrue(7, null, 1000000.0, 50000.0, Now));
                Assert.Equal(0, led.Accrue(7, "EXP", 1000000.0, 0.0, Now));
                Assert.Equal(0, led.EntryCount);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Ledger_RoundTripsAndCompacts()
        {
            string dir;
            TradeReputationLedger led = NewLedger(out dir);
            try
            {
                led.Accrue(7, "EXP", 30000.0, 50000.0, Now);
                led.SaveIfDirty();

                var reloaded = new TradeReputationLedger(dir);
                Assert.Equal(30000.0, reloaded.RemainderFor(7, "EXP"), 6);

                Assert.Equal(0, reloaded.Compact(Now.AddDays(10), TimeSpan.FromDays(90)));
                Assert.Equal(1, reloaded.Compact(Now.AddDays(200), TimeSpan.FromDays(90)));
                Assert.Equal(0, reloaded.EntryCount);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- the drift term ---------------------------------------------------------------------

        private sealed class FixedStanding : IMemberStandingSource
        {
            private readonly string _playerTag;
            private readonly double _value;
            public FixedStanding(string playerTag, double value) { _playerTag = playerTag; _value = value; }
            public bool TryGetAggregate(string npcFactionTag, string playerFactionTag, out double aggregate)
            {
                aggregate = _value;
                return playerFactionTag == _playerTag;
            }
        }

        private static Personality Persona()
        {
            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = 0.5, Aggression = 0.5, Mercantilism = 0.5 } };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        private static WorldModel TwoFactionWorld(string other)
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "A"; a.HoldStrength = 0.8; a.Radius = 30;
            var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = other; b.HoldStrength = 0.8; b.Radius = 30;
            return w;
        }

        // The invariant slice 2 of FAC-184 turns on: an NPC-vs-NPC pair must drift to exactly the
        // same number it did before the term existed. The default source answers false for every
        // pair, so this is the "term absent" path.
        [Fact]
        public void NpcVsNpcDrift_IsUnchangedByTheMemberTerm()
        {
            var t = TestSupport.Tuning();

            var withoutSource = new FactionState { Tag = "A" };
            for (int i = 0; i < 20; i++) Diplomacy.Drift(withoutSource, Persona(), TwoFactionWorld("B"), t);

            // Same pair, but with a live source that simply does not recognise "B" as a player faction.
            var withSource = new FactionState { Tag = "A" };
            for (int i = 0; i < 20; i++)
            {
                WorldModel w = TwoFactionWorld("B");
                w.MemberStanding = new FixedStanding("EXP", -1.0);
                Diplomacy.Drift(withSource, Persona(), w, t);
            }

            Assert.Equal(withoutSource.RelationTo("B"), withSource.RelationTo("B"), 12);
        }

        // A player faction whose members have behaved badly is disliked beyond what its border
        // pressure alone would produce -- and one whose members have behaved well is liked past it.
        [Fact]
        public void PlayerFactionStanding_PullsTheDriftTarget()
        {
            var t = TestSupport.Tuning();

            var baseline = new FactionState { Tag = "A" };
            for (int i = 0; i < 40; i++) Diplomacy.Drift(baseline, Persona(), TwoFactionWorld("EXP"), t);

            var hostile = new FactionState { Tag = "A" };
            var friendly = new FactionState { Tag = "A" };
            for (int i = 0; i < 40; i++)
            {
                WorldModel wh = TwoFactionWorld("EXP"); wh.MemberStanding = new FixedStanding("EXP", -1.0);
                Diplomacy.Drift(hostile, Persona(), wh, t);

                WorldModel wf = TwoFactionWorld("EXP"); wf.MemberStanding = new FixedStanding("EXP", 1.0);
                Diplomacy.Drift(friendly, Persona(), wf, t);
            }

            Assert.True(hostile.RelationTo("EXP") < baseline.RelationTo("EXP"),
                "bad member conduct should sour the faction relation past border pressure alone");
            Assert.True(friendly.RelationTo("EXP") > baseline.RelationTo("EXP"),
                "good member conduct should warm it past border pressure alone");
        }

        // The weight is a real off switch for the MEMBER half. It does not put the pair back on
        // border pressure -- under FAC-189 an actor with no traits is judged by its record either
        // way; weight 0 just means no member has personally contributed to that record.
        [Fact]
        public void ZeroDriftWeight_LeavesTheLedgerAloneAndIgnoresMemberConduct()
        {
            var t = TestSupport.Tuning();
            t.MemberStandingDriftWeight = 0.0;

            var s = new FactionState { Tag = "A" };
            Diplomacy.HitRelation(s, "EXP", 0.20);          // one act on the ledger

            for (int i = 0; i < 40; i++)
            {
                WorldModel w = TwoFactionWorld("EXP");
                w.MemberStanding = new FixedStanding("EXP", -1.0);   // members behaving terribly
                Diplomacy.Drift(s, Persona(), w, t);
            }

            // The ledger, leaned by this personality's xenophobia (0.5 traits -> lean 1.0), and
            // nothing from the members.
            Assert.Equal(-0.20, s.RelationTo("EXP"), 9);
        }

        // ---- FAC-189: standing is earned, not chased -------------------------------------------

        // The headline defect. Before this, every penalty eased back toward a target computed from
        // present border pressure, so five raids ended at the SAME warm number as one about ten
        // quanta later. The punishment has to change the destination, not merely the duration.
        [Fact]
        public void RaidPenaltiesPersistAndScaleWithTheCrime()
        {
            var t = TestSupport.Tuning();

            FactionState Raided(int times)
            {
                var s = new FactionState { Tag = "A" };
                for (int i = 0; i < times; i++) Diplomacy.HitRelation(s, "EXP", t.RaidRelationHit);
                for (int i = 0; i < 60; i++)
                {
                    WorldModel w = TwoFactionWorld("EXP");
                    w.MemberStanding = new FixedStanding("EXP", 0.0);   // no individual conduct either way
                    Diplomacy.Drift(s, Persona(), w, t);
                }
                return s;
            }

            double one = Raided(1).RelationTo("EXP");
            double five = Raided(5).RelationTo("EXP");

            Assert.True(one < 0, "one raid must still be resented after 60 quanta, not forgiven");
            Assert.True(five < one, "five raids must sit lower than one");
            // Scales with the crime rather than converging: five times the acts, five times the
            // standing cost, not the same destination reached ten quanta later.
            Assert.Equal(5.0, five / one, 6);
        }

        // Border pressure is instantaneous; conduct is a record. They must not share a target, so a
        // raider who withdraws to a quiet corner must not be warmed by the quiet.
        [Fact]
        public void BorderPressureDoesNotEnterAPlayerFactionsStanding()
        {
            var t = TestSupport.Tuning();

            var s = new FactionState { Tag = "A" };
            Diplomacy.HitRelation(s, "EXP", 0.30);

            for (int i = 0; i < 60; i++)
            {
                WorldModel w = TwoFactionWorld("EXP");
                w.MemberStanding = new FixedStanding("EXP", 0.0);
                Diplomacy.Drift(s, Persona(), w, t);
            }

            Assert.Equal(-0.30, s.RelationTo("EXP"), 9);
        }

        // A concluded truce is an act. If it reached only Relations, the next drift would rebuild the
        // pre-truce ledger and the settled peace would evaporate.
        [Fact]
        public void ConcludedTruceSurvivesTheNextDrift()
        {
            var t = TestSupport.Tuning();
            var npc = new FactionState { Tag = "A" };
            var player = new FactionState { Tag = "EXP" };

            Diplomacy.HitRelation(npc, "EXP", 0.60);
            player.Fatigue["A"] = 1.0;                       // war-weary, so it accepts

            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            Diplomacy.OfferPeace(npc, "EXP", now);
            WorldModel settle = TwoFactionWorld("EXP");
            settle.UtcNow = now;
            Diplomacy.ConcludeTruces(new[] { npc, player }, settle, t);
            Assert.Equal(t.TruceRelation, npc.RelationTo("EXP"), 9);

            // Past the truce window, so Drift no longer skips the pair.
            for (int i = 0; i < 5; i++)
            {
                WorldModel w = TwoFactionWorld("EXP");
                w.MemberStanding = new FixedStanding("EXP", 0.0);
                w.UtcNow = now.AddDays(400);
                Diplomacy.Drift(npc, Persona(), w, t);
            }

            Assert.Equal(t.TruceRelation, npc.RelationTo("EXP"), 9);
        }

        // FAC-188 item 6: Pressure() loops over nodes owned by self, so a landless faction reads 0
        // pressure against everyone and the old target collapsed to pure Mercantilism warmth --
        // warming toward the world purely for owning nothing. Not player-specific.
        [Fact]
        public void LandlessFactionHoldsItsStandings_RatherThanWarmingTowardEveryone()
        {
            var t = TestSupport.Tuning();

            var s = new FactionState { Tag = "A" };
            s.Relations["B"] = -0.60;

            for (int i = 0; i < 40; i++)
            {
                var w = TestSupport.World();
                // Only B owns anything; A is landless.
                var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "B";
                b.HoldStrength = 0.8; b.Radius = 30;
                Diplomacy.Drift(s, Persona(), w, t);
            }

            Assert.Equal(-0.60, s.RelationTo("B"), 9);
        }

        // ---- FAC-185: war is unilateral ---------------------------------------------------------

        // A declaration lands at once and lands hard enough that the hostile predicates actually
        // fire. Sitting exactly ON the threshold would leave a declared war no brain treats as one,
        // because every one of those reads is `< HostileRelationThreshold`.
        [Fact]
        public void DeclareWar_LandsBelowTheHostileThreshold_NotOnIt()
        {
            var t = TestSupport.Tuning();
            var npc = new FactionState { Tag = "A" };

            Diplomacy.DeclareWar(npc, "EXP", t, DateTime.UtcNow);

            Assert.True(npc.RelationTo("EXP") < t.HostileRelationThreshold,
                "a declared war must read as hostile to the predicates that gate raiding");
            Assert.True(npc.EarnedRelationTo("EXP") < t.HostileRelationThreshold,
                "and it must be on the ledger, or the next drift undoes it");
        }

        // The button must not become a laundering path: declaring war on someone who already hates
        // you far more cannot improve their opinion of you.
        [Fact]
        public void DeclareWar_NeverRaisesAnAlreadyWorseStanding()
        {
            var t = TestSupport.Tuning();
            var npc = new FactionState { Tag = "A" };
            Diplomacy.HitRelation(npc, "EXP", 0.95);
            double before = npc.RelationTo("EXP");

            Diplomacy.DeclareWar(npc, "EXP", t, DateTime.UtcNow);

            Assert.Equal(before, npc.RelationTo("EXP"), 9);
        }

        // A truce cannot survive its own breach, and neither can an outstanding peace offer.
        [Fact]
        public void DeclareWar_ClearsTheTruceAndAnyPendingOffer()
        {
            var t = TestSupport.Tuning();
            var npc = new FactionState { Tag = "A" };
            DateTime now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
            npc.TruceUntil["EXP"] = now.AddDays(30);
            Diplomacy.OfferPeace(npc, "EXP", now);
            Assert.True(Diplomacy.UnderTruce(npc, "EXP", now));

            Diplomacy.DeclareWar(npc, "EXP", t, DateTime.UtcNow);

            Assert.False(Diplomacy.UnderTruce(npc, "EXP", now));
            Assert.False(npc.Proposals.ContainsKey("EXP"));
            Assert.True(npc.FatigueTo("EXP") > 0, "war is tiring from the moment it is declared");
        }

        // A world saved before the ledger existed must carry its standings forward, not reset.
        [Fact]
        public void MissingLedgerEntry_FallsBackToTheCurrentStanding()
        {
            var s = new FactionState { Tag = "A" };
            s.Relations["EXP"] = -0.45;
            Assert.Empty(s.EarnedRelations);
            Assert.Equal(-0.45, s.EarnedRelationTo("EXP"), 9);

            var t = TestSupport.Tuning();
            WorldModel w = TwoFactionWorld("EXP");
            w.MemberStanding = new FixedStanding("EXP", 0.0);
            Diplomacy.Drift(s, Persona(), w, t);
            Assert.Equal(-0.45, s.RelationTo("EXP"), 9);
        }
    }
}
