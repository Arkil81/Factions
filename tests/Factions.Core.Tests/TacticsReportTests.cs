using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the Tactics window's report (<see cref="TacticsReport"/>), the recent-review ring it
    /// reads from <see cref="TacticMemory"/>, the in-game hint (<see cref="TacticHint"/>,
    /// <see cref="TacticHintTracker"/>) and the scanner label it rides on. Every test passes its own
    /// clock.
    /// </summary>
    public class TacticsReportTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        private static TacticOutcome Fight(string opponent, string ctx, TacticArm arm, BreakOffLevel breakOff,
            double share, double predicted = -1)
        {
            return new TacticOutcome
            {
                OpponentTag = opponent, Context = ctx, Arm = arm, BreakOff = breakOff,
                DamageDealt = share * 100, DamageTaken = (1 - share) * 100,
                PredictedDealt = predicted < 0 ? -1 : predicted * 100,
                PredictedTaken = predicted < 0 ? -1 : (1 - predicted) * 100,
                HullsBrought = 2,
            };
        }

        private static FactionState State(string tag, string personality = "default")
        {
            return new FactionState { Tag = tag, PersonalityId = personality };
        }

        // ---- the report --------------------------------------------------------------------------

        [Fact]
        public void Opponents_AreListedMostRecentlyFoughtFirst()
        {
            FactionState st = State("ACME");
            st.TacticMemory.Record(Fight("OLD", "===", TacticArm.Brawl, BreakOffLevel.Low, 0.5), "default", T0);
            st.TacticMemory.Record(Fight("NEW", "===", TacticArm.Brawl, BreakOffLevel.Low, 0.5), "default", T0.AddHours(2));
            st.TacticMemory.Record(Fight("MID", "===", TacticArm.Brawl, BreakOffLevel.Low, 0.5), "default", T0.AddHours(1));

            TacticsReport r = TacticsReport.Build(st, true, T0.AddHours(3));

            Assert.Equal(new[] { "NEW", "MID", "OLD" }, r.Opponents.ConvertAll(o => o.OpponentTag).ToArray());
            Assert.Equal(T0.AddHours(2), r.Opponents[0].LastUtc);
        }

        [Fact]
        public void Preferred_IsTheBestPosterior_AndLearnedEvidenceCanOvertakeThePrior()
        {
            FactionState st = State("ACME");   // default personality: Standoff/Mid prior at 0.65
            for (int i = 0; i < 5; i++)
                st.TacticMemory.Record(Fight("RAIL", "===", TacticArm.Brawl, BreakOffLevel.Low, 1.0), "default", T0);

            TacticsOpponentReport rail = TacticsReport.Build(st, true, T0).Opponents[0];

            Assert.Equal(TacticArm.Brawl, rail.Preferred.Arm);
            Assert.Equal(BreakOffLevel.Low, rail.Preferred.BreakOff);
            Assert.Equal(5.0, rail.Preferred.Evidence, 6);
            Assert.Equal((6 * 0.45 + 5) / 11.0, rail.Preferred.Mean, 6);
            Assert.Equal(5.0, rail.Evidence, 6);
        }

        [Fact]
        public void Preferred_StaysOnThePersonalityDefault_WhenLittleIsLearned_AndSaysItHasNoEvidence()
        {
            FactionState st = State("ACME", "mercantile");   // Kite/High
            st.TacticMemory.Record(Fight("RAIL", "===", TacticArm.Brawl, BreakOffLevel.Low, 0.5), "mercantile", T0);

            TacticsOpponentReport rail = TacticsReport.Build(st, true, T0).Opponents[0];

            Assert.Equal(TacticArm.Kite, rail.Preferred.Arm);
            Assert.Equal(BreakOffLevel.High, rail.Preferred.BreakOff);
            Assert.Equal(0.0, rail.Preferred.Evidence);
            Assert.Equal(1.0, rail.Evidence, 6);
        }

        [Fact]
        public void Contexts_MostEvidenceFirst_CellsBestMeanFirst_AndBounded()
        {
            FactionState st = State("ACME");
            st.TacticMemory.Record(Fight("RAIL", "+==", TacticArm.Kite, BreakOffLevel.Low, 0.9), "default", T0);
            // Six cells in "===", so the context is the heavier one and its cell list is cut to four.
            var cells = new[]
            {
                new KeyValuePair<TacticArm, double>(TacticArm.Brawl, 0.1),
                new KeyValuePair<TacticArm, double>(TacticArm.Standoff, 0.9),
                new KeyValuePair<TacticArm, double>(TacticArm.Kite, 0.5),
                new KeyValuePair<TacticArm, double>(TacticArm.AttackRun, 0.3),
            };
            foreach (KeyValuePair<TacticArm, double> c in cells)
            {
                st.TacticMemory.Record(Fight("RAIL", "===", c.Key, BreakOffLevel.Low, c.Value), "default", T0);
                st.TacticMemory.Record(Fight("RAIL", "===", c.Key, BreakOffLevel.High, c.Value), "default", T0);
            }

            TacticsOpponentReport rail = TacticsReport.Build(st, true, T0).Opponents[0];

            Assert.Equal(new[] { "===", "+==" }, rail.Contexts.ConvertAll(c => c.Context).ToArray());
            TacticsContextReport even = rail.Contexts[0];
            Assert.Equal(8.0, even.Evidence, 6);
            Assert.Equal(TacticsReport.MaxCellsPerContext, even.Cells.Count);
            for (int i = 1; i < even.Cells.Count; i++) Assert.True(even.Cells[i - 1].Mean >= even.Cells[i].Mean);
            Assert.Equal(TacticArm.Standoff, even.Cells[0].Arm);
            Assert.DoesNotContain(even.Cells, c => c.Arm == TacticArm.Brawl);
            Assert.Single(rail.Contexts[1].Cells);
        }

        [Fact]
        public void Evidence_IsDecayedToTheReportsClock()
        {
            FactionState st = State("ACME");
            st.TacticMemory.Record(Fight("RAIL", "===", TacticArm.Kite, BreakOffLevel.Mid, 1.0), "default", T0);

            TacticsOpponentReport later = TacticsReport.Build(st, true, T0.AddDays(TacticMemory.HalfLifeDays)).Opponents[0];

            Assert.Equal(0.5, later.Evidence, 6);
            Assert.Equal(0.5, later.Contexts[0].Cells[0].Evidence, 6);
        }

        [Fact]
        public void Reviews_AreNewestFirst_AndTheMemoryKeepsOnlyTheLastFew()
        {
            FactionState st = State("ACME");
            for (int i = 0; i < TacticMemory.MaxReviews + 2; i++)
                st.TacticMemory.Record(Fight("R" + i, "===", TacticArm.Kite, BreakOffLevel.Mid, 0.5, i == 9 ? 0.9 : -1),
                    "default", T0.AddMinutes(i));

            TacticsReport r = TacticsReport.Build(st, true, T0.AddHours(1));

            Assert.Equal(TacticMemory.MaxReviews, st.TacticMemory.Reviews.Count);
            Assert.Equal(TacticMemory.MaxReviews, r.Reviews.Count);
            Assert.Equal("R9", r.Reviews[0].OpponentTag);
            Assert.Equal(T0.AddMinutes(9), r.Reviews[0].Utc);
            Assert.Equal(0.9, r.Reviews[0].Predicted, 6);
            Assert.True(r.Reviews[0].Mistake);
            Assert.Equal("R2", r.Reviews[r.Reviews.Count - 1].OpponentTag);
            Assert.True(double.IsNaN(r.Reviews[1].Predicted));
        }

        [Fact]
        public void TheReport_DescribesOnlyTheFactionItIsBuiltFor()
        {
            FactionState mine = State("MINE", "raider");
            FactionState theirs = State("THEM");
            mine.TacticMemory.Record(Fight("RAIL", "===", TacticArm.Kite, BreakOffLevel.Mid, 0.7), "raider", T0);
            theirs.TacticMemory.Record(Fight("MINE", "===", TacticArm.Brawl, BreakOffLevel.Low, 0.3), "default", T0);
            theirs.TacticMemory.Record(Fight("ZED", "===", TacticArm.Brawl, BreakOffLevel.Low, 0.3), "default", T0);

            TacticsReport r = TacticsReport.Build(mine, false, T0);

            Assert.Equal("MINE", r.FactionTag);
            Assert.Equal(TacticArm.AttackRun, r.PriorArm);
            Assert.False(r.Learning);
            Assert.Equal(1, r.Updates);
            Assert.Single(r.Opponents);
            Assert.Equal("RAIL", r.Opponents[0].OpponentTag);
            Assert.All(r.Reviews, rv => Assert.Equal("RAIL", rv.OpponentTag));
        }

        [Fact]
        public void AFactionThatNeverFought_GivesAnEmptyReport_AndNoFactionGivesOneToo()
        {
            TacticsReport r = TacticsReport.Build(State("NEW"), true, T0);
            Assert.Empty(r.Opponents);
            Assert.Empty(r.Reviews);
            Assert.Equal(TacticArm.Standoff, r.PriorArm);

            TacticsReport none = TacticsReport.Build(null, true, T0);
            Assert.Null(none.FactionTag);
            Assert.Empty(none.Opponents);
        }

        [Fact]
        public void Reviews_RoundTripThroughTheBrainFile_AndAreOmittedWhenThereAreNone()
        {
            FactionState st = State("ACME");
            st.TacticMemory.Record(Fight("RAIL", "+-=", TacticArm.AttackRun, BreakOffLevel.High, 0.2, 0.8), "default", T0);
            st.TacticMemory.Record(Fight("OTHR", "===", TacticArm.Kite, BreakOffLevel.Low, 0.6), "default", T0.AddHours(1));

            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(st, new SpatialNode[0], FactionStore.DefaultLogCap), settings);
            FactionState back = FactionStore.ApplyBrainDoc(JsonConvert.DeserializeObject<BrainDoc>(json), new SpatialGraph());

            Assert.Equal(2, back.TacticMemory.Reviews.Count);
            TacticReview a = st.TacticMemory.Reviews[0], b = back.TacticMemory.Reviews[0];
            Assert.Equal(a.Utc, b.Utc);
            Assert.Equal("RAIL", b.OpponentTag);
            Assert.Equal("+-=", b.Context);
            Assert.Equal(TacticArm.AttackRun, b.Arm);
            Assert.Equal(BreakOffLevel.High, b.BreakOff);
            Assert.Equal(a.Predicted, b.Predicted, 3);
            Assert.Equal(a.Actual, b.Actual, 3);
            Assert.Equal(a.Reward, b.Reward, 3);
            Assert.True(b.Mistake);
            Assert.Equal(2, b.HullsBrought);
            Assert.True(double.IsNaN(back.TacticMemory.Reviews[1].Predicted));

            var fresh = new TacticMemoryDoc();
            Assert.DoesNotContain("Reviews", JsonConvert.SerializeObject(fresh, settings));
        }

        // ---- the in-game hint ----------------------------------------------------------------------

        [Fact]
        public void EachCombatManoeuvre_HasAPhrase_AndNothingElseDoes()
        {
            Assert.Equal("holding at range", TacticHint.Phrase(ManoeuvreKind.HoldAtRange));
            Assert.Equal("closing to brawl", TacticHint.Phrase(ManoeuvreKind.Orbit));
            Assert.Equal("falling back to kite", TacticHint.Phrase(ManoeuvreKind.Kite));
            Assert.Equal("making attack runs", TacticHint.Phrase(ManoeuvreKind.AttackRun));
            Assert.Equal("breaking off", TacticHint.Phrase(ManoeuvreKind.Disengage));
            Assert.Null(TacticHint.Phrase(ManoeuvreKind.ApproachAndStop));
            Assert.Null(TacticHint.Phrase(ManoeuvreKind.Launch));
            Assert.Equal("[SPRT] squadron holding at range.", TacticHint.Whisper("SPRT", "holding at range"));
            Assert.Null(TacticHint.Whisper("SPRT", null));
        }

        [Fact]
        public void TheTracker_AnnouncesOncePerChange_NoMoreOftenThanTheInterval()
        {
            var t = new TacticHintTracker();
            TimeSpan gap = TacticHintTracker.MinAnnounceInterval;

            Assert.Equal("holding at range", t.Observe("f", ManoeuvreKind.HoldAtRange, T0));
            Assert.Null(t.Observe("f", ManoeuvreKind.HoldAtRange, T0.AddSeconds(1)));
            Assert.Null(t.Observe("f", ManoeuvreKind.HoldAtRange, T0 + gap + gap));

            // A change inside the interval waits for it, then is announced once.
            DateTime t1 = T0.AddMinutes(5);
            Assert.Equal("closing to brawl", t.Observe("f", ManoeuvreKind.Orbit, t1));
            Assert.Null(t.Observe("f", ManoeuvreKind.Kite, t1.AddSeconds(2)));
            Assert.Equal("falling back to kite", t.Observe("f", ManoeuvreKind.Kite, t1 + gap));
            Assert.Null(t.Observe("f", ManoeuvreKind.Kite, t1 + gap + gap));

            // A flip that comes back before the interval ends is never announced.
            DateTime t2 = T0.AddMinutes(10);
            Assert.Null(t.Observe("f", ManoeuvreKind.Orbit, t1 + gap + TimeSpan.FromSeconds(1)));
            Assert.Null(t.Observe("f", ManoeuvreKind.Kite, t1 + gap + TimeSpan.FromSeconds(2)));
            Assert.Null(t.Observe("f", ManoeuvreKind.Kite, t2));

            // Stopping is silent; resuming the announced manoeuvre is not a change.
            Assert.Null(t.Observe("f", null, t2.AddSeconds(1)));
            Assert.Null(t.Current("f", t2.AddSeconds(1)));
            Assert.Null(t.Observe("f", ManoeuvreKind.Kite, t2.AddMinutes(1)));
            Assert.Equal("falling back to kite", t.Current("f", t2.AddMinutes(1)));
        }

        [Fact]
        public void TheTracker_ForgetsWhatStoppedReporting_AndHoldsNothingForFleetsThatNeverFought()
        {
            var t = new TacticHintTracker();
            Assert.Null(t.Observe("idle", null, T0));
            Assert.Equal(0, t.Count);

            t.Observe("f", ManoeuvreKind.HoldAtRange, T0);
            Assert.Equal("holding at range", t.Current("f", T0 + TacticHintTracker.StaleAfter));
            Assert.Null(t.Current("f", T0 + TacticHintTracker.StaleAfter + TimeSpan.FromSeconds(1)));

            t.Observe("g", ManoeuvreKind.Orbit, T0 + TacticHintTracker.ForgetAfter + TimeSpan.FromMinutes(2));
            Assert.Equal(1, t.Count);
        }

        [Fact]
        public void AFightingSquadronsContact_CarriesThePhrase()
        {
            var fleets = new[]
            {
                new ScannerFleetCandidate { Key = "a", PositionKm = new Vec3(5, 0, 0), TacticHint = "holding at range" },
                new ScannerFleetCandidate { Key = "b", PositionKm = new Vec3(6, 0, 0), EngagementTargetNodeId = "N1", TacticHint = "closing to brawl" },
                new ScannerFleetCandidate { Key = "c", PositionKm = new Vec3(7, 0, 0) },
            };

            IList<ScannerContact> contacts = PlayerScanner.Resolve(1, new Vec3(0, 0, 0), 100, null, fleets);

            Assert.Equal("Fleet · holding at range", contacts[0].Label);
            Assert.StartsWith("Raid: ", contacts[1].Label);
            Assert.EndsWith(" · closing to brawl", contacts[1].Label);
            Assert.Equal("Fleet", contacts[2].Label);
        }

        // ---- wire --------------------------------------------------------------------------------

        [Fact]
        public void TheWireNames_MatchTheServersEnums()
        {
            Assert.Equal(Enum.GetNames(typeof(TacticArm)), WireTactics.ArmNames);
            foreach (TacticArm a in Enum.GetValues(typeof(TacticArm)))
                Assert.Equal(a.ToString(), WireTactics.Arm((byte)a));
            Assert.Equal(Enum.GetNames(typeof(BreakOffLevel)), WireTactics.BreakOffNames);
            Assert.Equal("?", WireTactics.Arm(9));
        }

        [Fact]
        public void TacticsRequest_RoundTrips()
        {
            bool stream;
            Assert.True(FactionsWire.TryReadTacticsRequest(FactionsWire.WriteTacticsRequest(true), out stream));
            Assert.True(stream);
            Assert.True(FactionsWire.TryReadTacticsRequest(FactionsWire.WriteTacticsRequest(false), out stream));
            Assert.False(stream);
            Assert.False(FactionsWire.TryReadTacticsRequest(FactionsWire.WriteFleetDoctrineRequest(true), out stream));
        }

        [Fact]
        public void TacticsSnapshot_RoundTripsEveryField()
        {
            var header = new WireTacticsHeader { FactionTag = "ACME", PriorArm = 2, PriorBreakOff = 2, Updates = 41, Learning = true };
            var opponents = new List<WireTacticOpponent>
            {
                new WireTacticOpponent
                {
                    Tag = "RAIL", LastUtcTicks = T0.Ticks, Evidence = 7.5f,
                    Preferred = new WireTacticCell { Arm = 0, BreakOff = 1, Mean = 0.71f, Evidence = 5f },
                    Contexts = new[]
                    {
                        new WireTacticContext
                        {
                            Context = "+-=", Evidence = 6f,
                            Cells = new[]
                            {
                                new WireTacticCell { Arm = 0, BreakOff = 1, Mean = 0.71f, Evidence = 5f },
                                new WireTacticCell { Arm = 3, BreakOff = 0, Mean = 0.4f, Evidence = 1f },
                            },
                        },
                        new WireTacticContext { Context = "===", Evidence = 1.5f, Cells = new WireTacticCell[0] },
                    },
                },
                new WireTacticOpponent { Tag = "OTHR", LastUtcTicks = 5, Contexts = null },
            };
            var reviews = new List<WireTacticReview>
            {
                new WireTacticReview
                {
                    UtcTicks = T0.AddHours(1).Ticks, Opponent = "RAIL", Context = "+-=", Arm = 3, BreakOff = 2,
                    Predicted = 0.8f, Actual = 0.2f, Reward = 0.15f, Mistake = true, HullsLost = 1, HullsBrought = 3,
                },
                new WireTacticReview { Opponent = "OTHR", Context = "===", Predicted = float.NaN },
            };

            WireTacticsHeader h;
            List<WireTacticOpponent> opps;
            List<WireTacticReview> revs;
            Assert.True(FactionsWire.TryReadTacticsSnapshot(
                FactionsWire.WriteTacticsSnapshot(header, opponents, reviews), out h, out opps, out revs));

            Assert.Equal("ACME", h.FactionTag);
            Assert.Equal(2, h.PriorArm);
            Assert.Equal(2, h.PriorBreakOff);
            Assert.Equal(41, h.Updates);
            Assert.True(h.Learning);

            Assert.Equal(2, opps.Count);
            WireTacticOpponent rail = opps[0];
            Assert.Equal("RAIL", rail.Tag);
            Assert.Equal(T0.Ticks, rail.LastUtcTicks);
            Assert.Equal(7.5f, rail.Evidence);
            Assert.Equal(0, rail.Preferred.Arm);
            Assert.Equal(1, rail.Preferred.BreakOff);
            Assert.Equal(0.71f, rail.Preferred.Mean);
            Assert.Equal(5f, rail.Preferred.Evidence);
            Assert.Equal(2, rail.Contexts.Length);
            Assert.Equal("+-=", rail.Contexts[0].Context);
            Assert.Equal(6f, rail.Contexts[0].Evidence);
            Assert.Equal(2, rail.Contexts[0].Cells.Length);
            Assert.Equal(3, rail.Contexts[0].Cells[1].Arm);
            Assert.Equal(0.4f, rail.Contexts[0].Cells[1].Mean);
            Assert.Empty(rail.Contexts[1].Cells);
            Assert.Empty(opps[1].Contexts);
            Assert.Equal(0, opps[1].Preferred.Arm);

            Assert.Equal(2, revs.Count);
            WireTacticReview rv = revs[0];
            Assert.Equal(T0.AddHours(1).Ticks, rv.UtcTicks);
            Assert.Equal("RAIL", rv.Opponent);
            Assert.Equal("+-=", rv.Context);
            Assert.Equal(3, rv.Arm);
            Assert.Equal(2, rv.BreakOff);
            Assert.Equal(0.8f, rv.Predicted);
            Assert.Equal(0.2f, rv.Actual);
            Assert.Equal(0.15f, rv.Reward);
            Assert.True(rv.Mistake);
            Assert.Equal(1, rv.HullsLost);
            Assert.Equal(3, rv.HullsBrought);
            Assert.True(float.IsNaN(revs[1].Predicted));
        }

        [Fact]
        public void TacticsSnapshot_WithNothing_IsAReadableAnswer_AndAWrongKindIsRefused()
        {
            WireTacticsHeader h;
            List<WireTacticOpponent> opps;
            List<WireTacticReview> revs;
            Assert.True(FactionsWire.TryReadTacticsSnapshot(
                FactionsWire.WriteTacticsSnapshot(new WireTacticsHeader(), null, null), out h, out opps, out revs));
            Assert.Equal("", h.FactionTag);
            Assert.Empty(opps);
            Assert.Empty(revs);

            Assert.False(FactionsWire.TryReadTacticsSnapshot(FactionsWire.WriteTacticsRequest(true), out h, out opps, out revs));
            Assert.Null(opps);
        }

        [Fact]
        public void TacticsSnapshot_WithAHostileCount_IsRefused()
        {
            byte[] data = FactionsWire.WriteTacticsSnapshot(new WireTacticsHeader { FactionTag = "A" }, null, null);
            // The opponent count follows kind (1), version (4), tag (1 + 1), two arms (2), updates (8), learning (1).
            int at = 1 + 4 + 2 + 2 + 8 + 1;
            BitConverter.GetBytes(int.MaxValue).CopyTo(data, at);

            WireTacticsHeader h;
            List<WireTacticOpponent> opps;
            List<WireTacticReview> revs;
            Assert.False(FactionsWire.TryReadTacticsSnapshot(data, out h, out opps, out revs));
            Assert.Null(opps);
        }
    }
}
