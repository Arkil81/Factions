using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the tactic learner (<see cref="TacticMemory"/>): the pick moves off an arm that keeps
    /// losing, moves again once a changed opponent makes it lose and old evidence decays, falls back
    /// from the opponent to the context aggregate to the prior, stays bounded, persists, weighs
    /// mistakes and commanded outcomes, and — switched off — changes nothing. Every test passes its
    /// clock and the switch explicitly; none touches <see cref="FeatureSwitch"/> or
    /// <see cref="DebugTraces"/>.
    /// </summary>
    public class TacticMemoryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        private static TacticContext Ctx(string key)
        {
            TacticContext c;
            Assert.True(TacticContext.TryParse(key, out c));
            return c;
        }

        /// <summary>An opponent whose damage share against each arm is fixed.</summary>
        private static TacticOutcome Fight(string opponent, string ctx, TacticPick pick,
            Func<TacticArm, double> shareFor, bool commanded = false)
        {
            double share = shareFor(pick.Arm);
            return new TacticOutcome
            {
                OpponentTag = opponent, Context = ctx, Arm = pick.Arm, BreakOff = pick.BreakOff,
                DamageDealt = share * 100, DamageTaken = (1 - share) * 100,
                PredictedDealt = -1, PredictedTaken = -1,
                Commanded = commanded,
            };
        }

        /// <summary>Picks and records <paramref name="fights"/> fights, one an hour from
        /// <paramref name="start"/>; returns the arm picked in each.</summary>
        private static List<TacticArm> Campaign(TacticMemory m, string personality, string opponent, string ctx,
            Func<TacticArm, double> shareFor, int fights, DateTime start)
        {
            var arms = new List<TacticArm>();
            for (int i = 0; i < fights; i++)
            {
                DateTime now = start.AddHours(i);
                TacticPick pick = m.Pick("SELF", personality, opponent, Ctx(ctx), now);
                arms.Add(pick.Arm);
                m.Record(Fight(opponent, ctx, pick, shareFor), personality, now);
            }
            return arms;
        }

        private static double Share(List<TacticArm> arms, TacticArm arm, int from, int count)
        {
            return arms.Skip(from).Take(count).Count(a => a == arm) / (double)count;
        }

        // ---- the pick moves -------------------------------------------------------------------

        /// <summary>A mercantile faction (default Kite) against an opponent that punishes kiting and
        /// loses to a brawl: its picks leave Kite and settle on Brawl.</summary>
        private static double KitingLoses(TacticArm a)
        {
            return a == TacticArm.Kite ? 0.1 : a == TacticArm.Brawl ? 0.85 : 0.35;
        }

        [Fact]
        public void ALosingArm_IsAbandoned_AndTheWinningOneTakesOver_WithinThirtyFights()
        {
            var m = new TacticMemory();
            List<TacticArm> arms = Campaign(m, "mercantile", "RAIL", "-+=", KitingLoses, 40, T0);

            Assert.True(Share(arms, TacticArm.Brawl, 30, 10) >= 0.7,
                        "last ten: " + string.Join(",", arms.Skip(30)));
            Assert.True(Share(arms, TacticArm.Kite, 30, 10) <= 0.1);
            // The memory actually moved: the posterior counts grew, not just the lines.
            double[] a = new double[TacticMemory.Actions], b = new double[TacticMemory.Actions];
            Assert.Equal(TacticBasis.Opponent, m.Evidence("RAIL", "-+=", T0.AddHours(40), a, b));
            Assert.True(a.Sum() + b.Sum() > 20);
            Assert.Equal(40, m.Updates);
        }

        [Fact]
        public void WithoutEvidence_ThePersonalityDefaultLeads()
        {
            // No record at all: the prior favours the personality's own arm and break-off.
            var m = new TacticMemory();
            int kite = 0;
            for (int i = 0; i < 200; i++)
            {
                TacticPick p = new TacticMemory { Updates = i }.Pick("SELF", "mercantile", "X", Ctx("==="), T0);
                Assert.Equal(TacticBasis.Prior, p.Basis);
                Assert.Equal(TacticArm.Kite, p.PriorArm);
                Assert.Equal(BreakOffLevel.High, p.PriorBreakOff);
                if (p.Arm == TacticArm.Kite) kite++;
            }
            Assert.True(kite > 200 / 4, "kite picked " + kite + "/200");
            Assert.True(m.IsEmpty);
        }

        [Fact]
        public void AfterTheOpponentRefits_OldEvidenceDecays_AndThePickMovesAgain()
        {
            var m = new TacticMemory();
            List<TacticArm> first = Campaign(m, "mercantile", "RAIL", "-+=", KitingLoses, 40, T0);
            Assert.True(Share(first, TacticArm.Brawl, 30, 10) >= 0.7);

            // The opponent refits for close range: brawling now loses and standing off wins. Two
            // half-lives later the brawl record is a quarter of what it was, and within forty fights
            // the pick has moved to Standoff.
            Func<TacticArm, double> brawlingLoses = arm =>
                arm == TacticArm.Brawl ? 0.1 : arm == TacticArm.Standoff ? 0.85 : 0.35;
            DateTime later = T0.AddDays(2 * TacticMemory.HalfLifeDays);
            List<TacticArm> second = Campaign(m, "mercantile", "RAIL", "-+=", brawlingLoses, 60, later);

            Assert.True(Share(second, TacticArm.Standoff, 40, 20) >= 0.7,
                        "fights 40-59: " + string.Join(",", second.Skip(40)));
            Assert.True(Share(second, TacticArm.Brawl, 40, 20) <= 0.1);
        }

        [Fact]
        public void TheRefitIsLearnedSooner_WhenTheOldLessonHasDecayed()
        {
            // The same two campaigns, the refit fought straight away or two half-lives on: the stale
            // brawl record costs more wasted brawls when it has not decayed.
            Func<TacticArm, double> brawlingLoses = arm =>
                arm == TacticArm.Brawl ? 0.1 : arm == TacticArm.Standoff ? 0.85 : 0.35;
            int[] wasted = new int[2];
            for (int k = 0; k < 2; k++)
            {
                var m = new TacticMemory();
                Campaign(m, "mercantile", "RAIL", "-+=", KitingLoses, 40, T0);
                DateTime refit = k == 0 ? T0.AddHours(40) : T0.AddDays(2 * TacticMemory.HalfLifeDays);
                List<TacticArm> second = Campaign(m, "mercantile", "RAIL", "-+=", brawlingLoses, 20, refit);
                wasted[k] = second.Count(a => a == TacticArm.Brawl);
            }
            Assert.True(wasted[1] * 2 <= wasted[0], "brawls after the refit: fresh " + wasted[0] + ", decayed " + wasted[1]);
        }

        [Fact]
        public void Decay_HalvesEvidenceEveryHalfLife_AgainstTheCallersClock()
        {
            var m = new TacticMemory();
            var pick = new TacticPick { Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low };
            m.Record(Fight("RAIL", "===", pick, a => 1.0), "default", T0);
            int i = TacticMemory.Index(TacticArm.Brawl, BreakOffLevel.Low);

            double[] a0 = new double[12], b0 = new double[12];
            m.Evidence("RAIL", "===", T0, a0, b0);
            Assert.Equal(1.0, a0[i], 9);

            double[] a1 = new double[12], b1 = new double[12];
            m.Evidence("RAIL", "===", T0.AddDays(TacticMemory.HalfLifeDays), a1, b1);
            Assert.Equal(0.5, a1[i], 9);

            double[] a2 = new double[12], b2 = new double[12];
            m.Evidence("RAIL", "===", T0.AddDays(-3), a2, b2);   // a clock behind the record does not grow it
            Assert.Equal(1.0, a2[i], 9);
        }

        [Fact]
        public void ACell_HoldsNoMoreThanMaxEvidence()
        {
            var m = new TacticMemory();
            var pick = new TacticPick { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid };
            for (int k = 0; k < 100; k++) m.Record(Fight("RAIL", "===", pick, a => 0.6), "default", T0);
            double[] a = new double[12], b = new double[12];
            m.Evidence("RAIL", "===", T0, a, b);
            int i = TacticMemory.Index(TacticArm.Standoff, BreakOffLevel.Mid);
            Assert.Equal(TacticMemory.MaxEvidence, a[i] + b[i], 6);
            Assert.Equal(0.6, a[i] / (a[i] + b[i]), 6);
        }

        // ---- fallback ------------------------------------------------------------------------------

        [Fact]
        public void AnUnseenOpponent_ReadsTheContextAcrossOpponents_ThenThePrior()
        {
            var m = new TacticMemory();
            var pick = new TacticPick { Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low };
            for (int k = 0; k < 40; k++) m.Record(Fight("AAA", "+==", pick, a => 0.9), "default", T0);
            m.Record(Fight("BBB", "+==", pick, a => 0.9), "default", T0);

            Assert.Equal(TacticBasis.Opponent, m.Pick("SELF", "default", "AAA", Ctx("+=="), T0).Basis);
            Assert.Equal(TacticBasis.Aggregate, m.Pick("SELF", "default", "NEW", Ctx("+=="), T0).Basis);
            Assert.Equal(TacticBasis.Prior, m.Pick("SELF", "default", "NEW", Ctx("-=="), T0).Basis);
            Assert.Equal(TacticBasis.Prior, m.Pick("SELF", "default", "AAA", Ctx("-=="), T0).Basis);
            Assert.Equal(TacticBasis.Aggregate, m.Pick("SELF", "default", null, Ctx("+=="), T0).Basis);

            // The aggregate is capped, so a new opponent is only weakly prejudged.
            double[] a = new double[12], b = new double[12];
            m.Evidence("NEW", "+==", T0, a, b);
            Assert.Equal(TacticMemory.AggregateEvidence, a.Sum() + b.Sum(), 6);
            // It is the other opponents' record, not this one's: BBB's aggregate is AAA alone.
            double[] a2 = new double[12], b2 = new double[12];
            m.Evidence("BBB", "+==", T0, a2, b2);
            Assert.Equal(1.0, a2.Sum() + b2.Sum(), 6);
        }

        // ---- bounds and persistence ---------------------------------------------------------------

        [Fact]
        public void Opponents_AreBounded_TheLeastRecentlyFoughtLeavesFirst()
        {
            var m = new TacticMemory();
            var pick = new TacticPick { Arm = TacticArm.Kite, BreakOff = BreakOffLevel.High };
            for (int k = 0; k < TacticMemory.MaxOpponents; k++)
                m.Record(Fight("F" + k.ToString("00"), "===", pick, a => 0.5), "default", T0.AddMinutes(k));
            // F00 is the oldest, but fought again now; F01 becomes the least recent.
            m.Record(Fight("F00", "===", pick, a => 0.5), "default", T0.AddHours(1));
            m.Record(Fight("NEW", "===", pick, a => 0.5), "default", T0.AddHours(2));

            Assert.Equal(TacticMemory.MaxOpponents, m.OpponentCount);
            Assert.NotNull(m.Find("F00"));
            Assert.Null(m.Find("F01"));
            Assert.NotNull(m.Find("NEW"));
        }

        [Fact]
        public void Memory_RoundTripsThroughTheBrainFile_AndALegacyBrainLoadsEmpty()
        {
            var st = new FactionState { Tag = "ACME", PersonalityId = "raider" };
            var pick = new TacticPick { Arm = TacticArm.AttackRun, BreakOff = BreakOffLevel.Mid };
            st.TacticMemory.Record(Fight("RAIL", "+-=", pick, a => 0.7), "raider", T0);
            st.TacticMemory.Record(Fight("RAIL", "==-", pick, a => 0.2), "raider", T0.AddHours(3));
            st.TacticMemory.Record(Fight("OTHR", "===", pick, a => 0.5), "raider", T0.AddHours(5));
            st.Fights.Add(new FightRecord
            {
                Utc = T0, Kind = ActionKind.Raid, Attacking = true, OpponentTag = "RAIL", AttackerRatio = 0.7,
                Tactic = new DuelTactic
                {
                    AttackerArm = TacticArm.Kite, AttackerBreakOff = BreakOffLevel.High,
                    DefenderArm = TacticArm.Brawl, DefenderBreakOff = BreakOffLevel.Low,
                    BracketsM = new[] { 2000, 2000, 800 },
                },
            });
            st.Fights.Add(new FightRecord { Utc = T0, Kind = ActionKind.Raid, OpponentTag = "OTHR" });

            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(st, new SpatialNode[0], FactionStore.DefaultLogCap), settings);
            FactionState back = FactionStore.ApplyBrainDoc(JsonConvert.DeserializeObject<BrainDoc>(json), new SpatialGraph());

            Assert.Equal(3, back.TacticMemory.Updates);
            Assert.Equal(2, back.TacticMemory.OpponentCount);
            Assert.Equal(T0.AddHours(3), back.TacticMemory.Find("RAIL").LastUtc);
            foreach (string ctx in new[] { "+-=", "==-" })
            {
                double[] a = new double[12], b = new double[12], a2 = new double[12], b2 = new double[12];
                st.TacticMemory.Evidence("RAIL", ctx, T0.AddDays(1), a, b);
                back.TacticMemory.Evidence("RAIL", ctx, T0.AddDays(1), a2, b2);
                for (int i = 0; i < 12; i++) { Assert.Equal(a[i], a2[i], 3); Assert.Equal(b[i], b2[i], 3); }
            }
            // The same memory draws the same pick after a restart.
            Assert.Equal(st.TacticMemory.Pick("ACME", "raider", "RAIL", Ctx("+-="), T0.AddDays(1)).Arm,
                         back.TacticMemory.Pick("ACME", "raider", "RAIL", Ctx("+-="), T0.AddDays(1)).Arm);

            DuelTactic t = back.Fights[0].Tactic;
            Assert.NotNull(t);
            Assert.Equal(TacticArm.Kite, t.AttackerArm);
            Assert.Equal(BreakOffLevel.Low, t.DefenderBreakOff);
            Assert.Equal(new[] { 2000, 2000, 800 }, t.BracketsM);
            Assert.Null(back.Fights[1].Tactic);
            Assert.Contains("\"Arms\": \"Kite/High:Brawl/Low\"", JsonConvert.SerializeObject(
                FactionStore.ToBrainDoc(st, new SpatialNode[0], FactionStore.DefaultLogCap), Formatting.Indented, settings));

            // Lean: a faction that never learned writes no key at all, and a brain written before the
            // learner existed loads with an empty memory.
            var fresh = new FactionState { Tag = "NEW", PersonalityId = "default" };
            string freshJson = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(fresh, new SpatialNode[0], FactionStore.DefaultLogCap), settings);
            Assert.DoesNotContain("Tactics", freshJson);
            Assert.DoesNotContain("Arms", json.Substring(json.IndexOf("OTHR\"", StringComparison.Ordinal)));
            FactionState legacy = FactionStore.ApplyBrainDoc(JsonConvert.DeserializeObject<BrainDoc>(freshJson), new SpatialGraph());
            Assert.True(legacy.TacticMemory.IsEmpty);
        }

        [Fact]
        public void TheLoader_SkipsWhatItCannotRead()
        {
            var doc = new BrainDoc
            {
                Tag = "ACME", PersonalityId = "default",
                Tactics = new TacticMemoryDoc
                {
                    Updates = 7,
                    Opponents = new List<TacticOpponentDoc>
                    {
                        new TacticOpponentDoc { Tag = "RAIL", Utc = T0, Buckets = new List<TacticBucketDoc>
                        {
                            new TacticBucketDoc { Ctx = "+=-", Utc = T0, Cells = "0:1.5:0.5 junk 13:1:1 4:x:1 5:2:0" },
                            new TacticBucketDoc { Ctx = "bad", Utc = T0, Cells = "0:1:1" },
                            null,
                        } },
                        new TacticOpponentDoc { Tag = null, Utc = T0 },
                        null,
                    },
                },
                Fights = new List<FightDoc>
                {
                    new FightDoc { Utc = T0, Kind = "Raid", Arms = "Kite/High:Nonsense/Low", Brackets = "2000" },
                },
            };
            FactionState st = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Equal(7, st.TacticMemory.Updates);
            Assert.Equal(1, st.TacticMemory.OpponentCount);
            TacticMemory.Opponent rail = st.TacticMemory.Find("RAIL");
            Assert.Single(rail.Buckets);
            TacticMemory.Bucket b = rail.Buckets["+=-"];
            Assert.Equal(1.5, b.A[0]);
            Assert.Equal(2.0, b.A[5]);
            Assert.Equal(0.0, b.A[4]);
            Assert.Null(st.Fights[0].Tactic);
        }

        // ---- weights ------------------------------------------------------------------------------

        [Fact]
        public void AMistake_UpdatesHarder_AndACommandedFightIsADemonstration()
        {
            var m = new TacticMemory();
            var o = new TacticOutcome
            {
                OpponentTag = "RAIL", Context = "===", Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid,
                DamageDealt = 30, DamageTaken = 70, PredictedDealt = 60, PredictedTaken = 40,
            };
            TacticReview plain = m.Record(o, "default", T0);
            Assert.True(plain.Mistake);                         // 0.30 against a predicted 0.60
            Assert.Equal(TacticMemory.MistakeWeight, plain.Weight);
            Assert.Equal(0.60, plain.Predicted, 9);
            Assert.Equal(0.30, plain.Actual, 9);

            o.PredictedDealt = 35; o.PredictedTaken = 65;         // 0.30 against 0.35: not a mistake
            Assert.Equal(1.0, m.Record(o, "default", T0).Weight);

            o.Commanded = true;
            TacticReview shown = m.Record(o, "default", T0);
            Assert.Equal(TacticMemory.DemonstrationWeight, shown.Weight);
            Assert.True(shown.Commanded);

            o.PredictedDealt = 60; o.PredictedTaken = 40;         // a commanded blunder
            Assert.Equal(TacticMemory.MistakeWeight * TacticMemory.DemonstrationWeight, m.Record(o, "default", T0).Weight);

            o.PredictedDealt = -1;                                // no prediction: never a mistake
            o.Commanded = false;
            TacticReview none = m.Record(o, "default", T0);
            Assert.False(none.Mistake);
            Assert.True(double.IsNaN(none.Predicted));

            // Evidence is the weights, reward-split: 2 + 1 + 0.5 + 1 + 1.
            Assert.Equal(5.5, none.Evidence, 9);
        }

        [Fact]
        public void TheCommandedWeight_MovesThePickLessThanTheFactionsOwnFights()
        {
            // Same record, once fought and once commanded: the demonstrated cell ends at half the
            // evidence, so its posterior sits nearer the prior.
            Func<TacticArm, double> win = a => 0.9;
            var pick = new TacticPick { Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low };
            var own = new TacticMemory();
            var shown = new TacticMemory();
            TacticReview r1 = default(TacticReview), r2 = default(TacticReview);
            for (int k = 0; k < 6; k++)
            {
                r1 = own.Record(Fight("RAIL", "===", pick, win), "mercantile", T0);
                r2 = shown.Record(Fight("RAIL", "===", pick, win, commanded: true), "mercantile", T0);
            }
            Assert.Equal(6.0, r1.Evidence, 9);
            Assert.Equal(3.0, r2.Evidence, 9);
            Assert.True(r1.PosteriorMean > r2.PosteriorMean);
        }

        [Fact]
        public void Reward_IsTheDamageShare_LessThePerHullPenalty()
        {
            var m = new TacticMemory();
            TacticReview r = m.Record(new TacticOutcome
            {
                OpponentTag = "RAIL", Context = "===", Arm = TacticArm.Kite, BreakOff = BreakOffLevel.High,
                DamageDealt = 80, DamageTaken = 20, PredictedDealt = -1, PredictedTaken = -1,
                HullsLost = 1, HullsBrought = 4,
            }, "default", T0);
            Assert.Equal(0.8 - TacticMemory.HullLossPenalty * 0.25, r.Reward, 9);

            TacticReview wiped = m.Record(new TacticOutcome
            {
                OpponentTag = "RAIL", Context = "===", Arm = TacticArm.Kite, BreakOff = BreakOffLevel.High,
                DamageDealt = 10, DamageTaken = 90, PredictedDealt = -1, PredictedTaken = -1,
                HullsLost = 4, HullsBrought = 4,
            }, "default", T0);
            Assert.Equal(0.0, wiped.Reward, 9);

            // No opponent, or an unreadable context, teaches nothing.
            Assert.False(m.Record(new TacticOutcome { OpponentTag = null, Context = "===" }, "default", T0).Recorded);
            Assert.False(m.Record(new TacticOutcome { OpponentTag = "X", Context = "?" }, "default", T0).Recorded);
            Assert.Equal(2, m.Updates);
        }

        // ---- context, exploration, determinism ------------------------------------------------------

        [Fact]
        public void Context_ReadsRangeMobilityAndForce_FromEachSidesPointOfView()
        {
            FactionTuning tuning = TestSupport.World().Tuning;   // brackets 2000 / 800 / 0
            var kiter = new List<WeaponBand> { new WeaponBand { Dps = 100, RangeM = 2000 } };
            var brawler = new List<WeaponBand> { new WeaponBand { Dps = 250, RangeM = 800 } };

            Assert.Equal("++-", TacticContext.Of(kiter, 0, brawler, 0, 10, 2, tuning).Key);
            Assert.Equal("--+", TacticContext.Of(brawler, 0, kiter, 0, 2, 10, tuning).Key);
            // A station cannot move; its flat floor is always in range.
            Assert.Equal("=-=", TacticContext.Of(kiter, 20, kiter, 0, CombatMobility.Station, 5, tuning).Key);
            Assert.Equal("===", TacticContext.Of(kiter, 0, kiter, 0, CombatMobility.Unknown, 3, tuning).Key);
        }

        [Fact]
        public void Exploration_IsWiderForARecklessPersonality_NarrowerForACautiousOne()
        {
            Assert.Equal(1.0, TacticMemory.Exploration("default"), 9);
            Assert.True(TacticMemory.Exploration("raider") > 1.0);
            Assert.True(TacticMemory.Exploration("factorum") < 1.0);
            Assert.True(TacticMemory.Exploration("factorum") >= 0.25);
        }

        [Fact]
        public void ThePick_IsTheSameUntilTheMemoryLearns_SoTheGateAndTheDuelAgree()
        {
            var st = new FactionState { Tag = "ACME", PersonalityId = "settler" };
            var pick = new TacticPick { Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low };
            st.TacticMemory.Record(Fight("RAIL", "===", pick, a => 0.4), "settler", T0);

            DuelStance gate = TacticChoice.For(st, 3, "RAIL", Ctx("==="), true, T0);
            DuelStance duel = TacticChoice.For(st, 3, "RAIL", Ctx("==="), true, T0);
            Assert.Equal(gate.Arm, duel.Arm);
            Assert.Equal(gate.BreakOff, duel.BreakOff);
            Assert.Equal(3, duel.Mobility);

            // Each outcome recorded reseeds the draw: over twenty updates the pick is not frozen.
            var seen = new HashSet<int>();
            for (int k = 0; k < 20; k++)
            {
                st.TacticMemory.Record(Fight("RAIL", "===", pick, a => 0.4), "settler", T0);
                TacticPick p = st.TacticMemory.Pick("ACME", "settler", "RAIL", Ctx("==="), T0);
                seen.Add(TacticMemory.Index(p.Arm, p.BreakOff));
            }
            Assert.True(seen.Count > 1);
        }

        [Fact]
        public void SwitchOff_TheChoiceIsThePersonalityDefault_WhateverTheMemoryHolds()
        {
            foreach (string personality in new[] { "mercantile", "raider", "settler", "factorum", "default", null })
            {
                var st = new FactionState { Tag = "ACME", PersonalityId = personality };
                var brawl = new TacticPick { Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low };
                for (int k = 0; k < 30; k++) st.TacticMemory.Record(Fight("RAIL", "===", brawl, a => 1.0), personality, T0);
                DuelStance expected = TacticChoice.For(st, 4);
                TacticPick pick;
                DuelStance off = TacticChoice.For(st, 4, "RAIL", Ctx("==="), false, T0, out pick);
                Assert.Equal(expected.Arm, off.Arm);
                Assert.Equal(expected.BreakOff, off.BreakOff);
                Assert.Equal(4, off.Mobility);
                Assert.Null(pick.Context);
            }
            // A null faction (an unowned station) has nothing to learn with.
            Assert.Equal(TacticChoice.For(null, 0).Arm,
                         TacticChoice.For(null, 0, "RAIL", Ctx("==="), true, T0).Arm);
        }

        [Fact]
        public void TheLogLines_NameWhatWasPickedAndWhatWasLearned()
        {
            var pick = new TacticPick
            {
                Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low, PriorArm = TacticArm.Kite,
                PriorBreakOff = BreakOffLevel.High, Basis = TacticBasis.Aggregate, Sampled = 0.734, Context = "-+=",
            };
            Assert.Equal("TACTIC_CHOSEN fleet=[R1] faction=MERC vs=RAIL ctx=-+= arm=Brawl breakoff=Low prior=Kite/High basis=aggregate sampled=0.73",
                         TacticMemory.FormatChosen("MERC", "[R1]", "RAIL", pick));
            Assert.StartsWith("TACTIC_CHOSEN fleet=station faction=MERC vs=none ",
                              TacticMemory.FormatChosen("MERC", null, null, pick));

            var m = new TacticMemory();
            TacticReview r = m.Record(new TacticOutcome
            {
                OpponentTag = "RAIL", Context = "-+=", Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low,
                DamageDealt = 25, DamageTaken = 75, PredictedDealt = 60, PredictedTaken = 40, HullsLost = 1, HullsBrought = 2,
            }, "mercantile", T0);
            Assert.Equal("TACTIC_REVIEW faction=MERC vs=RAIL ctx=-+= arm=Brawl breakoff=Low predicted=0.60 actual=0.25"
                         + " hulls_lost=1/2 reward=0.00 weight=2.0 mistake=1 commanded=0 mean=0.34 n=2.0",
                         TacticMemory.FormatReview("MERC", r));
        }
        [Fact]
        public void Describe_ListsEachOpponentsCells_BestFirst()
        {
            var m = new TacticMemory();
            var brawl = new TacticPick { Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.Low };
            var kite = new TacticPick { Arm = TacticArm.Kite, BreakOff = BreakOffLevel.High };
            for (int k = 0; k < 5; k++)
            {
                m.Record(Fight("RAIL", "-+=", brawl, a => 0.9), "mercantile", T0);
                m.Record(Fight("RAIL", "-+=", kite, a => 0.1), "mercantile", T0);
            }
            m.Record(Fight("OTHR", "===", kite, a => 0.5), "mercantile", T0);

            string all = m.Describe("MERC", "mercantile", null, T0);
            Assert.StartsWith("tactics MERC personality=mercantile prior=Kite/High explore=1.50 opponents=2 updates=11", all);
            Assert.Contains("\n  vs OTHR last=2026-03-01 00:00\n    ctx==== Kite/High mean=", all);
            Assert.Contains("\n    ctx=-+= Brawl/Low mean=0.65 n=5.0; Kite/High mean=0.40 n=5.0;", all);

            string one = m.Describe("MERC", "mercantile", "rail", T0);
            Assert.DoesNotContain("OTHR", one);
            Assert.EndsWith("no record against NONE", m.Describe("MERC", "mercantile", "NONE", T0));
        }

        // ---- through the resolver ------------------------------------------------------------------

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, List<WeaponBand>> _weapons = new Dictionary<string, List<WeaponBand>>();
            private readonly Dictionary<string, float> _thrust = new Dictionary<string, float>();
            public Catalogue Add(string id, float thrustN, float dps, float rangeM)
            {
                _weapons[id] = new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };
                _thrust[id] = thrustN;
                return this;
            }
            public long CargoLiters(string prefabId) { return 0; }
            public long Cost(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                List<WeaponBand> w;
                return prefabId != null && _weapons.TryGetValue(prefabId, out w) ? w : new List<WeaponBand>();
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                var d = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
                float n;
                if (prefabId != null && _thrust.TryGetValue(prefabId, out n))
                    d[ThrustDirection.Backward] = new List<ThrustBand>
                    {
                        new ThrustBand { ThrusterType = "Hydrogen", ForceMagnitudeN = n,
                                         EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f },
                    };
                return d;
            }
            public double DryMassKg(string prefabId) { return _thrust.ContainsKey(prefabId ?? "") ? 1000 : 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static WorldModel ResolverWorld(bool learning, Catalogue cat, DateTime now)
        {
            WorldModel w = TestSupport.World();
            w.TacticLearning = learning;
            w.PrefabCatalogue = cat;
            w.UtcNow = now;
            foreach (var spec in new[] { new { Id = "MERC1", Owner = "MERC", X = 0.0 }, new { Id = "RVN1", Owner = "RVN", X = 500.0 } })
            {
                SpatialNode n = w.Graph.Add(new Vec3(spec.X, 0, 0), 0.6, spec.Id);
                n.OwnerFactionTag = spec.Owner;
                n.HoldStrength = 0.8;
                n.Radius = 40;
            }
            return w;
        }

        private static void AddRaid(FactionState atk, string hull, List<WeaponBand> bands, DateTime now)
        {
            atk.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = atk.Tag, TargetTag = "MERC", TargetNodeId = "MERC1", FleetId = "R1",
                OriginNodeId = "RVN1", ShipPrefabId = hull, ConvoySize = 1,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = bands,
            });
        }

        private static void AddGuard(FactionState def, string hull, List<WeaponBand> bands, DateTime now)
        {
            def.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = def.Tag, TargetNodeId = "MERC1", FleetId = "G1",
                ShipPrefabId = hull, ConvoySize = 1,
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = bands,
            });
        }

        private static List<WeaponBand> Bands(float dps, float rangeM)
        {
            return new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };
        }

        [Fact]
        public void Resolver_BothSidesLearn_AndTheLedgerCarriesTheArmsAndBrackets()
        {
            var cat = new Catalogue().Add("gunboat", 5000, 100, 800);
            WorldModel w = ResolverWorld(true, cat, T0);
            var def = new FactionState { Tag = "MERC", PersonalityId = "factorum" };
            var atk = new FactionState { Tag = "RVN", PersonalityId = "raider" };
            AddGuard(def, "gunboat", Bands(100, 800), T0);
            AddRaid(atk, "gunboat", Bands(100, 800), T0);
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning, new FixedRandom(0.5), log, null);

            Assert.Equal(1, atk.TacticMemory.Updates);
            Assert.Equal(1, def.TacticMemory.Updates);
            Assert.NotNull(atk.TacticMemory.Find("MERC"));
            Assert.NotNull(def.TacticMemory.Find("RVN"));
            // Equal hulls, equal mobility: both sides read "===" and are recorded there.
            Assert.True(atk.TacticMemory.Find("MERC").Buckets.ContainsKey("==="));

            FightRecord mine = Assert.Single(atk.Fights);
            Assert.NotNull(mine.Tactic);
            Assert.NotNull(mine.Tactic.BracketsM);
            Assert.NotEmpty(mine.Tactic.BracketsM);
            FightRecord theirs = Assert.Single(def.Fights);
            Assert.Same(mine.Tactic, theirs.Tactic);
        }

        [Fact]
        public void Resolver_AStationsOwnerLearns_AsTheDefender()
        {
            var cat = new Catalogue().Add("gunboat", 5000, 100, 800);
            WorldModel w = ResolverWorld(true, cat, T0);
            SpatialNode target = w.Graph.Nodes["MERC1"];
            target.StationPrefabId = "station";
            target.StationWeapons = Bands(80, 2000);
            var def = new FactionState { Tag = "MERC", PersonalityId = "settler" };
            var atk = new FactionState { Tag = "RVN", PersonalityId = "raider" };
            AddRaid(atk, "gunboat", Bands(100, 800), T0);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning, new FixedRandom(0.5), new List<string>(), null);

            Assert.Equal(1, def.TacticMemory.Updates);
            Assert.NotNull(def.TacticMemory.Find("RVN"));
            Assert.Equal(1, atk.TacticMemory.Updates);
        }

        [Fact]
        public void Resolver_SwitchOff_FightsThePersonalityDefault_AndLearnsNothing()
        {
            // Two worlds, identical but for a memory that would move the pick; with learning off the
            // duel, its log and its record are the same line for line.
            var cat = new Catalogue().Add("kiter", 10000, 100, 2000).Add("brawler", 2000, 250, 800);
            string[] personalities = { "mercantile", "raider", "settler", "factorum", "default" };
            for (int i = 0; i < 25; i++)
            {
                string atkP = personalities[i % 5], defP = personalities[(i / 5) % 5];
                var runs = new List<string>[2];
                var records = new FightRecord[2];
                for (int run = 0; run < 2; run++)
                {
                    WorldModel w = ResolverWorld(false, cat, T0);
                    var def = new FactionState { Tag = "MERC", PersonalityId = defP };
                    var atk = new FactionState { Tag = "RVN", PersonalityId = atkP };
                    if (run == 1)
                        foreach (TacticArm arm in new[] { TacticArm.Brawl, TacticArm.Standoff, TacticArm.Kite, TacticArm.AttackRun })
                        {
                            var p = new TacticPick { Arm = arm, BreakOff = BreakOffLevel.Low };
                            double share = arm == TacticArm.Brawl ? 1.0 : 0.0;
                            for (int k = 0; k < 20; k++)
                            {
                                atk.TacticMemory.Record(Fight("MERC", "++-", p, a => share), atkP, T0);
                                atk.TacticMemory.Record(Fight("MERC", "--+", p, a => share), atkP, T0);
                                def.TacticMemory.Record(Fight("RVN", "--+", p, a => share), defP, T0);
                                def.TacticMemory.Record(Fight("RVN", "++-", p, a => share), defP, T0);
                            }
                        }
                    long updatesBefore = atk.TacticMemory.Updates + def.TacticMemory.Updates;
                    AddGuard(def, i % 2 == 0 ? "brawler" : "kiter", i % 2 == 0 ? Bands(250, 800) : Bands(100, 2000), T0);
                    AddRaid(atk, i % 2 == 0 ? "kiter" : "brawler", i % 2 == 0 ? Bands(100, 2000) : Bands(250, 800), T0);
                    var log = new List<string>();
                    PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning,
                                                         new SeededRandom(900 + i), log, null);
                    runs[run] = log;
                    records[run] = Assert.Single(atk.Fights);
                    Assert.Equal(updatesBefore, atk.TacticMemory.Updates + def.TacticMemory.Updates);
                    DuelStance atkDefault = TacticChoice.DefaultFor(atkP), defDefault = TacticChoice.DefaultFor(defP);
                    Assert.Equal(atkDefault.Arm, records[run].Tactic.AttackerArm);
                    Assert.Equal(atkDefault.BreakOff, records[run].Tactic.AttackerBreakOff);
                    Assert.Equal(defDefault.Arm, records[run].Tactic.DefenderArm);
                    Assert.Equal(defDefault.BreakOff, records[run].Tactic.DefenderBreakOff);
                }
                Assert.Equal(runs[0], runs[1]);
                Assert.Equal(records[0].AttackerRatio, records[1].AttackerRatio);
                Assert.Equal(records[0].Tactic.BracketsM, records[1].Tactic.BracketsM);
            }
        }

        [Fact]
        public void Resolver_RepeatedRaidsOnOneOpponent_GrowTheAttackersRecordAgainstIt()
        {
            // A mercantile raider in a fast gunboat against a factorum Guard that out-guns it at every
            // range: the record against MERC grows by one outcome per duel, in the context the duel
            // was read in — a memory that never moves off the prior is the failure this catches.
            var cat = new Catalogue().Add("kiter", 10000, 100, 2000).Add("wall", 2000, 300, 2000);
            var def = new FactionState { Tag = "MERC", PersonalityId = "factorum" };
            var atk = new FactionState { Tag = "RVN", PersonalityId = "mercantile" };
            var arms = new List<TacticArm>();
            for (int i = 0; i < 40; i++)
            {
                DateTime now = T0.AddHours(i);
                WorldModel w = ResolverWorld(true, cat, now);
                def.PendingFleets.Clear();
                atk.PendingFleets.Clear();
                atk.Fights.Clear();
                AddGuard(def, "wall", Bands(300, 2000), now);
                AddRaid(atk, "kiter", Bands(100, 2000), now);
                PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning,
                                                     new SeededRandom(i), new List<string>(), null);
                arms.Add(Assert.Single(atk.Fights).Tactic.AttackerArm);
            }
            Assert.Equal(40, atk.TacticMemory.Updates);
            double[] a = new double[12], b = new double[12];
            Assert.Equal(TacticBasis.Opponent, atk.TacticMemory.Evidence("MERC", "=+-", T0.AddHours(40), a, b));
            Assert.True(a.Sum() + b.Sum() > 20);
        }
    }
}
