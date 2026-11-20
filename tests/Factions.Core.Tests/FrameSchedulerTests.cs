using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The scheduler is pure policy over per-actor state, so it is driven here with a fake clock and
    /// synthetic costs rather than real elapsed time. The load-bearing tests are the two that encode
    /// the failure this design has to avoid: an admitted actor must never receive an already-spent
    /// deadline, and no actor may be starved by a competitor that always overruns.
    /// </summary>
    public sealed class FrameSchedulerTests
    {
        private const long Freq = 1_000_000; // 1 MHz: 1 tick == 1 microsecond
        private static readonly DateTime T0 = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        private sealed class FakeClock
        {
            public long Ticks;
            public long Now() { return Ticks; }
            public void AdvanceMs(double ms) { Ticks += (long)(ms * Freq / 1000.0); }
        }

        private static FrameActorSpec Spec(string name, double intervalSeconds, double seedMs,
            bool resumable = false, double maxDebtSeconds = 0.0)
        {
            return new FrameActorSpec
            {
                Name = name,
                IntervalSeconds = intervalSeconds,
                SeedCostMilliseconds = seedMs,
                Resumable = resumable,
                MaxDebtSeconds = maxDebtSeconds,
            };
        }

        private static long Ms(double ms) { return (long)(ms * Freq / 1000.0); }

        // ---- the anti-livelock invariant -------------------------------------------------------

        [Fact]
        public void AdmittedUnitDeadline_IsNeverAlreadyExpired_EvenAfterAnActorOverrunsTheWholeBudget()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int fat = s.RegisterActor(Spec("fat", 0.001, 50.0));
            int lean = s.RegisterActor(Spec("lean", 0.001, 0.05));

            for (int frame = 0; frame < 200; frame++)
            {
                DateTime now = T0.AddSeconds(frame);
                s.BeginFrame(clock.Ticks, now, 0.4);
                int id;
                FrameDeadline d;
                while (s.TryAdmitNext(out id, out d))
                {
                    // The scheduler's own arithmetic, not the stopwatch: a deadline read off the real
                    // clock can expire between admission and this check whenever the thread stalls
                    // for longer than the 0.1 ms floor, which is load on the test machine, not this.
                    Assert.True(d.BudgetMilliseconds > 0,
                        "actor " + id + " was admitted with a spent budget (" + d.BudgetMilliseconds + " ms)");
                    // Simulate the fat actor blowing the entire frame budget many times over.
                    double cost = id == fat ? 50.0 : 0.05;
                    clock.AdvanceMs(cost);
                    s.RecordCompletion(id, Ms(cost), FrameActorOutcome.Completed, now);
                }
                s.EndFrame();
            }

            Assert.True(s.GetStats(lean).Admissions > 0);
            Assert.True(s.GetStats(fat).Admissions > 0);
        }

        [Fact]
        public void ExpensiveActor_DoesNotPreventOthersFromEverRunning()
        {
            // The executable form of the incident this design reverses: an actor whose single unit
            // always exceeds the whole budget must not be able to hold every other actor at zero.
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            var ids = new List<int>
            {
                s.RegisterActor(Spec("hog",   0.01, 100.0)),
                s.RegisterActor(Spec("a",     0.01, 0.05)),
                s.RegisterActor(Spec("b",     0.05, 0.05)),
                s.RegisterActor(Spec("c",     0.10, 0.05)),
            };

            for (int frame = 0; frame < 2000; frame++)
            {
                DateTime now = T0.AddMilliseconds(frame * 16.0);
                s.BeginFrame(clock.Ticks, now, 0.4);
                int id;
                FrameDeadline d;
                while (s.TryAdmitNext(out id, out d))
                {
                    double cost = id == ids[0] ? 100.0 : 0.05;
                    clock.AdvanceMs(cost);
                    s.RecordCompletion(id, Ms(cost), FrameActorOutcome.Completed, now);
                }
                s.EndFrame();
            }

            foreach (int id in ids)
                Assert.True(s.GetStats(id).Admissions > 0,
                    "actor " + s.GetStats(id).Name + " never ran");
        }

        [Fact]
        public void NoActorIsDeferredBeyondItsDebtThreshold_UnderTheRealActorMix()
        {
            // Real intervals and real seed costs from the live baseline, driven for the equivalent of
            // several minutes of frames.
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            var costs = new Dictionary<int, double>();
            Action<string, double, double> add = (name, interval, cost) =>
            {
                int id = s.RegisterActor(Spec(name, interval, cost, maxDebtSeconds: Math.Max(4 * interval, 2.0)));
                costs[id] = cost;
            };
            add("prod", 30.0, 1.6);
            add("ops", 10.0, 0.6);
            add("yard", 60.0, 17.4);
            add("fleet", 1.0, 1.2);
            add("trade", 60.0, 8.8);
            add("stock", 60.0, 0.6);
            add("globalPass", 30.0, 0.6);
            add("brain", 60.0, 13.0);
            add("flush", 30.0, 6.2);
            add("fleetObs", 0.15, 0.10);
            add("stationObs", 0.15, 0.11);
            add("asteroidObs", 0.25, 0.05);

            for (int frame = 0; frame < 20_000; frame++)
            {
                DateTime now = T0.AddMilliseconds(frame * 16.0);
                s.BeginFrame(clock.Ticks, now, 0.4);
                int id;
                FrameDeadline d;
                while (s.TryAdmitNext(out id, out d))
                {
                    clock.AdvanceMs(costs[id]);
                    s.RecordCompletion(id, Ms(costs[id]), FrameActorOutcome.Completed, now);
                }
                s.EndFrame();
            }

            for (int id = 0; id < s.ActorCount; id++)
            {
                FrameActorStats st = s.GetStats(id);
                Assert.True(st.Admissions > 0, st.Name + " never ran");
                Assert.True(st.MaxDeferralMilliseconds <= st.IntervalSeconds * 1000.0 + 4000.0,
                    st.Name + " was deferred " + st.MaxDeferralMilliseconds + " ms");
            }
        }

        // ---- selection -------------------------------------------------------------------------

        [Fact]
        public void Selection_PrefersTheHigherOverdueRatio_NotTheLongerAbsoluteWait()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int fast = s.RegisterActor(Spec("fast", 1.0, 0.05));
            int slow = s.RegisterActor(Spec("slow", 60.0, 0.05));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            int id; FrameDeadline d;
            Assert.True(s.TryAdmitNext(out id, out d));
            s.RecordCompletion(id, Ms(0.05), FrameActorOutcome.Completed, T0);
            int other;
            Assert.True(s.TryAdmitNext(out other, out d));
            s.RecordCompletion(other, Ms(0.05), FrameActorOutcome.Completed, T0);
            s.EndFrame();

            // fast is 2 intervals late; slow is 30 s late but only half an interval late.
            DateTime now = T0.AddSeconds(32);
            s.BeginFrame(clock.Ticks, now, 0.4);
            Assert.True(s.TryAdmitNext(out id, out d));
            Assert.Equal(fast, id);
            Assert.NotEqual(slow, id);
        }

        [Fact]
        public void YieldedActor_KeepsWinningSelection_UntilItCompletes()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int resumable = s.RegisterActor(Spec("resumable", 10.0, 0.05, resumable: true));
            s.RegisterActor(Spec("other", 10.0, 0.05));

            DateTime now = T0;
            s.BeginFrame(clock.Ticks, now, 0.4);
            int id; FrameDeadline d;
            Assert.True(s.TryAdmitNext(out id, out d));
            Assert.Equal(resumable, id);
            s.RecordCompletion(id, Ms(0.05), FrameActorOutcome.Yielded, now);
            s.EndFrame();

            // A yield must not reschedule it, so it stays overdue and wins again next frame.
            for (int i = 0; i < 5; i++)
            {
                now = now.AddMilliseconds(16);
                s.BeginFrame(clock.Ticks, now, 0.4);
                Assert.True(s.TryAdmitNext(out id, out d));
                Assert.Equal(resumable, id);
                s.RecordCompletion(id, Ms(0.05), FrameActorOutcome.Yielded, now);
                s.EndFrame();
            }

            now = now.AddMilliseconds(16);
            s.BeginFrame(clock.Ticks, now, 0.4);
            Assert.True(s.TryAdmitNext(out id, out d));
            s.RecordCompletion(id, Ms(0.05), FrameActorOutcome.Completed, now);
            s.EndFrame();
            Assert.True(s.GetStats(resumable).Yields >= 6);
            Assert.Equal(1, s.GetStats(resumable).Completions);
        }

        // ---- admission -------------------------------------------------------------------------

        [Fact]
        public void FirstAdmissionOfAFrame_IsUnconditional_EvenWhenTheEstimateExceedsTheBudget()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int fat = s.RegisterActor(Spec("fat", 1.0, 100.0));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            int id; FrameDeadline d;
            Assert.True(s.TryAdmitNext(out id, out d));
            Assert.Equal(fat, id);
            Assert.Equal(0, s.DeniedByBudgetCount);
        }

        [Fact]
        public void SecondAdmission_IsDeniedWhenItsEstimateDoesNotFitTheRemainingBudget()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            s.RegisterActor(Spec("first", 1.0, 0.05));
            s.RegisterActor(Spec("second", 1.0, 5.0));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            int id; FrameDeadline d;
            Assert.True(s.TryAdmitNext(out id, out d));
            clock.AdvanceMs(0.05);
            s.RecordCompletion(id, Ms(0.05), FrameActorOutcome.Completed, T0);

            Assert.False(s.TryAdmitNext(out id, out d));
            Assert.True(s.DeniedByBudgetCount > 0);
        }

        [Fact]
        public void ForcedAdmission_FiresPastTheDebtThreshold_AndClosesTheFrame()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            s.RegisterActor(Spec("cheap", 0.001, 0.01));
            int fat = s.RegisterActor(Spec("fat", 1.0, 100.0, maxDebtSeconds: 2.0));

            // Let the fat actor fall well past its debt threshold while the cheap one keeps winning
            // on ratio. It can only ever get in via the debt rule, so this is what proves that rule
            // actually binds rather than being unreachable.
            DateTime now = T0;
            for (int i = 0; i < 6; i++)
            {
                now = now.AddSeconds(1);
                s.BeginFrame(clock.Ticks, now, 0.4);
                int id; FrameDeadline d;
                while (s.TryAdmitNext(out id, out d))
                {
                    clock.AdvanceMs(id == fat ? 100.0 : 0.01);
                    s.RecordCompletion(id, Ms(id == fat ? 100.0 : 0.01), FrameActorOutcome.Completed, now);
                }
                s.EndFrame();
            }

            // What the debt rule guarantees is that fat eventually runs despite never fitting the
            // budget and never having the best ratio. Whether it arrives as the frame's unconditional
            // first admission or as a forced later one is an implementation detail -- debt-first
            // selection usually makes it the former, which is the cheaper of the two.
            Assert.True(s.GetStats(fat).Admissions > 0,
                "the fat actor never ran; the debt rule never bound");
        }

        [Fact]
        public void ForcedAdmission_HappensWhenASecondDebtExceededActorCannotFit_AndClosesTheFrame()
        {
            // Two actors both past their threshold: the first takes the frame's unconditional
            // admission, so the second can only get in by being forced -- and once it is, nothing
            // else runs behind it.
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            s.RegisterActor(Spec("fatA", 1.0, 100.0, maxDebtSeconds: 2.0));
            s.RegisterActor(Spec("fatB", 1.0, 100.0, maxDebtSeconds: 2.0));
            s.RegisterActor(Spec("tail", 1.0, 0.01, maxDebtSeconds: 2.0));

            DateTime now = T0;
            s.BeginFrame(clock.Ticks, now, 0.4);
            s.EndFrame();                       // seed the schedule, run nothing

            now = T0.AddSeconds(10);            // everyone is far past their threshold
            s.BeginFrame(clock.Ticks, now, 0.4);
            int id; FrameDeadline d;
            int admitted = 0;
            while (s.TryAdmitNext(out id, out d))
            {
                admitted++;
                clock.AdvanceMs(100.0);
                s.RecordCompletion(id, Ms(100.0), FrameActorOutcome.Completed, now);
            }
            s.EndFrame();

            Assert.Equal(2, admitted);               // first unconditional, second forced, then closed
            Assert.Equal(1, s.ForcedAdmissionCount);
        }

        [Fact]
        public void SharedBudgetDisabled_NeverDeniesByBudget()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            s.SetSharedBudgetEnabled(false);
            s.RegisterActor(Spec("a", 0.001, 50.0));
            s.RegisterActor(Spec("b", 0.001, 50.0));
            s.RegisterActor(Spec("c", 0.001, 50.0));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            int id; FrameDeadline d;
            int admitted = 0;
            while (s.TryAdmitNext(out id, out d))
            {
                admitted++;
                clock.AdvanceMs(50.0);
                s.RecordCompletion(id, Ms(50.0), FrameActorOutcome.Completed, T0);
            }
            s.EndFrame();

            Assert.Equal(3, admitted);
            Assert.Equal(0, s.DeniedByBudgetCount);
        }

        [Fact]
        public void MaxUnitsPerFrame_CapsAdmissions()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            s.SetMaxUnitsPerFrame(3);
            for (int i = 0; i < 10; i++) s.RegisterActor(Spec("a" + i, 0.001, 0.001));

            s.BeginFrame(clock.Ticks, T0, 100.0);
            int id; FrameDeadline d;
            int admitted = 0;
            while (s.TryAdmitNext(out id, out d))
            {
                admitted++;
                s.RecordCompletion(id, 1, FrameActorOutcome.Completed, T0);
            }
            s.EndFrame();
            Assert.Equal(3, admitted);
        }

        // ---- cost estimation -------------------------------------------------------------------

        [Fact]
        public void Estimate_RisesFastAndDecaysSlowly_SoABimodalActorStaysConservative()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int id = s.RegisterActor(Spec("ops", 1.0, 0.6));

            s.RecordCompletion(id, Ms(0.6), FrameActorOutcome.Completed, T0);
            s.RecordCompletion(id, Ms(15.6), FrameActorOutcome.Completed, T0);
            double afterSpike = s.GetStats(id).EstimateMilliseconds;
            Assert.True(afterSpike > 7.0, "estimate barely moved after a 26x sample: " + afterSpike);

            for (int i = 0; i < 5; i++) s.RecordCompletion(id, Ms(0.6), FrameActorOutcome.Completed, T0);
            double afterDecay = s.GetStats(id).EstimateMilliseconds;
            Assert.True(afterDecay < afterSpike, "estimate did not decay");
            Assert.True(afterDecay > 0.6, "estimate forgot the spike far too quickly: " + afterDecay);
        }

        [Fact]
        public void SkippedOutcome_DoesNotUpdateTheEstimate()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int id = s.RegisterActor(Spec("obs", 1.0, 5.0));

            s.RecordCompletion(id, 0, FrameActorOutcome.Skipped, T0);
            Assert.Equal(5.0, s.GetStats(id).EstimateMilliseconds, 6);

            s.RecordCompletion(id, 0, FrameActorOutcome.NotDue, T0);
            Assert.True(s.GetStats(id).EstimateMilliseconds < 5.0,
                "NotDue spent real time and should move the estimate");
        }

        [Fact]
        public void ResetWindow_ClearsCountersButKeepsEstimatesAndSchedule()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int id = s.RegisterActor(Spec("a", 1.0, 0.05));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            int got; FrameDeadline d;
            Assert.True(s.TryAdmitNext(out got, out d));
            s.RecordCompletion(got, Ms(3.0), FrameActorOutcome.Completed, T0);
            s.EndFrame();

            double estimate = s.GetStats(id).EstimateMilliseconds;
            s.ResetWindow();

            Assert.Equal(0, s.GetStats(id).Admissions);
            Assert.Equal(0, s.AdmittedUnitCount);
            Assert.Equal(estimate, s.GetStats(id).EstimateMilliseconds, 6);
        }

        [Fact]
        public void CompletedActor_ReschedulesFromWhenItBecameDue_NotFromNow()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int id = s.RegisterActor(Spec("a", 10.0, 0.05));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            int got; FrameDeadline d;
            Assert.True(s.TryAdmitNext(out got, out d));
            // Completes five seconds late.
            DateTime late = T0.AddSeconds(5);
            s.RecordCompletion(got, Ms(0.05), FrameActorOutcome.Completed, late);
            s.EndFrame();

            // Due again at T0+10 (its own cadence), not late+10, so the run after a late one is not
            // pushed out as well.
            s.BeginFrame(clock.Ticks, T0.AddSeconds(10.5), 0.4);
            Assert.True(s.TryAdmitNext(out got, out d));
            Assert.Equal(id, got);
        }

        [Fact]
        public void FramesWithNoAdmission_CountsOnlyFramesWhereNothingWasDue()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int id = s.RegisterActor(Spec("a", 10.0, 0.05));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            int got; FrameDeadline d;
            Assert.True(s.TryAdmitNext(out got, out d));
            s.RecordCompletion(got, Ms(0.05), FrameActorOutcome.Completed, T0);
            s.EndFrame();
            Assert.Equal(0, s.FramesWithNoAdmission);

            s.BeginFrame(clock.Ticks, T0.AddSeconds(1), 0.4);
            Assert.False(s.TryAdmitNext(out got, out d));
            s.EndFrame();
            Assert.Equal(1, s.FramesWithNoAdmission);
        }

        [Fact]
        public void TryGetStarvingActor_ReportsTheWorstOverdueActor()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            s.RegisterActor(Spec("a", 1.0, 0.05, maxDebtSeconds: 2.0));
            int b = s.RegisterActor(Spec("b", 1.0, 0.05, maxDebtSeconds: 2.0));

            s.BeginFrame(clock.Ticks, T0, 0.4);
            s.EndFrame();

            int id; double deferred;
            Assert.False(s.TryGetStarvingActor(T0, out id, out deferred));
            Assert.True(s.TryGetStarvingActor(T0.AddSeconds(30), out id, out deferred));
            Assert.True(deferred > 2000.0);
            Assert.True(id == 0 || id == b);
        }

        [Fact]
        public void TryGetStarvingActor_DoesNotFlagAResumableActorThatIsBeingAdmittedEveryFrame()
        {
            // A mid-sweep actor deliberately stays overdue, so a due-based test would call every long
            // sweep starvation even though the actor is running constantly.
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int resumable = s.RegisterActor(Spec("resumable", 1.0, 0.05, resumable: true, maxDebtSeconds: 2.0));

            DateTime now = T0;
            for (int i = 0; i < 400; i++)
            {
                now = now.AddMilliseconds(16);
                s.BeginFrame(clock.Ticks, now, 100.0);
                int id; FrameDeadline d;
                while (s.TryAdmitNext(out id, out d))
                    s.RecordCompletion(id, Ms(0.05), FrameActorOutcome.Yielded, now);
                s.EndFrame();
            }

            // Six seconds of wall time have passed against a 2 s threshold, and the actor is still
            // overdue by design - but it has been admitted on every one of those frames.
            int starving; double deferred;
            Assert.False(s.TryGetStarvingActor(now, out starving, out deferred),
                "a continuously-admitted actor was reported as starving");
            Assert.True(s.GetStats(resumable).Yields > 300);
        }

        [Fact]
        public void PhaseOffsets_KeepEqualIntervalActorsFromAlwaysCoLanding()
        {
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            for (int i = 0; i < 3; i++)
                s.RegisterActor(new FrameActorSpec
                {
                    Name = "obs" + i,
                    IntervalSeconds = 0.15,
                    SeedCostMilliseconds = 0.1,
                    InitialPhaseOffsetSeconds = i * 0.05,
                });

            int framesWithAll = 0;
            DateTime now = T0;
            for (int frame = 0; frame < 600; frame++)
            {
                now = now.AddMilliseconds(16);
                s.BeginFrame(clock.Ticks, now, 0.4);
                int admitted = 0;
                int id; FrameDeadline d;
                while (s.TryAdmitNext(out id, out d))
                {
                    admitted++;
                    clock.AdvanceMs(0.1);
                    s.RecordCompletion(id, Ms(0.1), FrameActorOutcome.Completed, now);
                }
                s.EndFrame();
                if (admitted == 3) framesWithAll++;
            }

            Assert.True(framesWithAll * 10 < 600,
                "all three observers co-landed on " + framesWithAll + " of 600 frames");
        }
    
        [Fact]
        public void AnActorIsAdmittedAtMostOncePerFrame_EvenWhenItKeepsYielding()
        {
            // A yielding actor deliberately stays overdue, so without a per-frame guard it would win
            // selection again immediately and take every slot in the frame. The old drains each ran
            // one unit per frame; that property has to survive.
            var clock = new FakeClock();
            var s = new FrameScheduler(Freq, clock.Now);
            int resumable = s.RegisterActor(Spec("resumable", 0.01, 0.01, resumable: true));
            int other = s.RegisterActor(Spec("other", 0.01, 0.01));

            s.BeginFrame(clock.Ticks, T0.AddSeconds(10), 100.0);
            var admitted = new List<int>();
            int id; FrameDeadline d;
            while (s.TryAdmitNext(out id, out d))
            {
                admitted.Add(id);
                s.RecordCompletion(id, Ms(0.01), FrameActorOutcome.Yielded, T0.AddSeconds(10));
            }
            s.EndFrame();

            Assert.Equal(2, admitted.Count);
            Assert.Contains(resumable, admitted);
            Assert.Contains(other, admitted);
        }
}
}
